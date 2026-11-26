import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

import bpy
from mathutils import Matrix, Vector
from bpy_extras.io_utils import ImportHelper
from bpy.props import StringProperty, EnumProperty, BoolProperty, FloatProperty

WORLD = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))

DEFAULT_ABBE_NUMBER = 75.0
DEFAULT_DISPERSION_SCALE = 1.0


def triples(values, stride=3):
    if len(values) % stride:
        raise ValueError(f"Array length must be divisible by {stride}")
    return [tuple(values[i:i + stride]) for i in range(0, len(values), stride)]


def matrix(values):
    if len(values) != 16 or not all(math.isfinite(x) for x in values):
        raise ValueError("Expected finite row-major 4x4 matrix")
    return Matrix(tuple(tuple(values[r * 4:r * 4 + 4]) for r in range(4)))


def texture_path(package_dir, relative):
    path = (package_dir / relative).resolve()
    if not path.is_relative_to(package_dir.resolve()):
        raise ValueError("Texture path escapes package directory")
    if not path.is_file():
        raise FileNotFoundError(path)
    return path


def configure_camera(scene, record):
    p = matrix(record["projection"])
    if abs(p[0][1]) > 1e-6 or abs(p[1][0]) > 1e-6:
        raise ValueError("Skewed camera projections are not supported")
    if abs(p[2][0]) > 1e-6 or abs(p[2][1]) > 1e-6:
        raise ValueError("Oblique near-plane projections are not supported")
    width, height = int(record["width"]), int(record["height"])
    if width < 1 or height < 1 or p[0][0] <= 0 or p[1][1] <= 0:
        raise ValueError("Invalid camera dimensions or focal scales")
    camera = bpy.data.cameras.new(f"Reference_{record['eye']}")
    obj = bpy.data.objects.new(camera.name, camera)
    scene.collection.objects.link(obj)
    obj.matrix_world = WORLD @ matrix(record["view"]).inverted()
    if abs(obj.matrix_world.to_3x3().determinant() - 1) > 1e-3:
        raise ValueError("Camera view must be a rigid Unity world-to-view transform")
    camera.sensor_fit = 'HORIZONTAL'
    camera.sensor_width = 36
    camera.clip_start = max(float(record["near"]), 0.00001)
    camera.clip_end = float(record["far"])
    aspect = p[1][1] / p[0][0]
    pixel_y = width / (height * aspect)
    if not 0.005 <= pixel_y <= 200:
        raise ValueError("Projection requires an unsupported pixel aspect ratio")
    scene.render.pixel_aspect_x = max(1.0, 1.0 / pixel_y)
    scene.render.pixel_aspect_y = max(1.0, pixel_y)
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    if record.get("orthographic", False):
        if abs(p[3][3] - 1) > 1e-5:
            raise ValueError("Invalid orthographic projection")
        camera.type = 'ORTHO'
        camera.ortho_scale = 2 / p[0][0]
        camera.shift_x = -p[0][3] / 2
        camera.shift_y = -p[1][3] / (2 * aspect)
    else:
        if abs(p[3][2] + 1) > 1e-5 or abs(p[3][3]) > 1e-5:
            raise ValueError("Expected Unity CPU perspective matrix, not a GPU/reversed-Z matrix")
        if abs(p[0][3]) > 1e-6 or abs(p[1][3]) > 1e-6:
            raise ValueError("Translated perspective projections are not supported")
        camera.type = 'PERSP'
        camera.lens = camera.sensor_width * p[0][0] / 2
        camera.shift_x = p[0][2] / 2
        camera.shift_y = p[1][2] / (2 * aspect)
    scene.camera = obj
    actual = obj.calc_matrix_camera(bpy.context.evaluated_depsgraph_get(), x=width, y=height,
                                   scale_x=scene.render.pixel_aspect_x, scale_y=scene.render.pixel_aspect_y)
    entries = ((0, 0), (1, 1), (0, 3), (1, 3)) if camera.type == 'ORTHO' else ((0, 0), (1, 1), (0, 2), (1, 2))
    error = max(abs(actual[r][c] - p[r][c]) for r, c in entries)
    if error > 1e-4:
        raise ValueError(f"Camera intrinsics failed validation: max matrix difference {error:g}")
    obj["unity_view_row_major"] = record["view"]
    obj["unity_projection_row_major"] = record["projection"]
    return obj, error


def base_color_nodes(material, record, package_dir):
    nodes, links = material.node_tree.nodes, material.node_tree.links
    color = nodes.new('ShaderNodeRGB')
    color.outputs[0].default_value = record.get("base_color", [1, 1, 1, 1])
    relative = record.get("base_texture")
    if not relative:
        return color.outputs[0]
    image = nodes.new('ShaderNodeTexImage')
    image.image = bpy.data.images.load(str(texture_path(package_dir, relative)), check_existing=True)
    image.image.colorspace_settings.name = 'sRGB'
    image.interpolation = 'Closest' if record.get('texture_filter') == 'Point' else 'Linear'
    image.extension = 'REPEAT' if record.get('texture_wrap') in ('Repeat', 'Mirror', 'MirrorOnce') else 'EXTEND'
    uv = nodes.new('ShaderNodeTexCoord')
    mapping = nodes.new('ShaderNodeVectorMath'); mapping.operation = 'MULTIPLY_ADD'
    transform = record.get("texture_scale_offset", [1, 1, 0, 0])
    mapping.inputs[1].default_value = (*transform[:2], 1)
    mapping.inputs[2].default_value = (*transform[2:], 0)
    links.new(uv.outputs['UV'], mapping.inputs[0])
    links.new(mapping.outputs[0], image.inputs['Vector'])
    multiply = nodes.new('ShaderNodeMixRGB'); multiply.blend_type = 'MULTIPLY'
    multiply.inputs[0].default_value = 1
    links.new(image.outputs['Color'], multiply.inputs[1])
    links.new(color.outputs[0], multiply.inputs[2])
    return multiply.outputs[0]


def validate_glass_settings(abbe_number, dispersion_scale):
    if not math.isfinite(abbe_number) or abbe_number <= 0:
        raise ValueError('Abbe number must be finite and positive')
    if not math.isfinite(dispersion_scale) or not 0 <= dispersion_scale <= 1:
        raise ValueError('Dispersion scale must be finite and between 0 and 1')


def make_material(record, package_dir, baseline=False, glass_roughness=0.0,
                  abbe_number=DEFAULT_ABBE_NUMBER, dispersion_scale=DEFAULT_DISPERSION_SCALE):
    validate_glass_settings(abbe_number, dispersion_scale)
    material = bpy.data.materials.new(record["name"])
    material.use_nodes = True
    material["unity_material"] = json.dumps(record)
    nodes, links = material.node_tree.nodes, material.node_tree.links
    nodes.clear()
    output = nodes.new('ShaderNodeOutputMaterial')
    if record["kind"] == "glass":
        if baseline:
            shader = nodes.new('ShaderNodeBsdfTransparent')
        else:
            shader = nodes.new('ShaderNodeBsdfPrincipled')
            required = ('Transmission Dispersion Scale', 'Transmission Dispersion Abbe Number', 'Thin Wall')
            if any(shader.inputs.get(name) is None for name in required):
                raise RuntimeError('Glass import requires Blender 5.3 with native Principled dispersion inputs. '
                                   f'This executable is Blender {bpy.app.version_string}; no non-dispersive fallback was used.')
            shader.label = 'Cycles reference glass (provisional Abbe)'
            shader.inputs['Base Color'].default_value = (1, 1, 1, 1)
            shader.inputs['Metallic'].default_value = 0
            shader.inputs['Transmission Weight'].default_value = 1
            shader.inputs['Thin Wall'].default_value = False
            shader.inputs['Alpha'].default_value = 1
            shader.inputs['Coat Weight'].default_value = 0
            shader.inputs['Sheen Weight'].default_value = 0
            shader.inputs['Subsurface Weight'].default_value = 0
            shader.inputs['Thin Film Thickness'].default_value = 0
            shader.inputs['IOR'].default_value = record.get('ior', 1.52)
            shader.inputs['Roughness'].default_value = glass_roughness
            shader.inputs['Transmission Dispersion Scale'].default_value = dispersion_scale
            shader.inputs['Transmission Dispersion Abbe Number'].default_value = abbe_number
            if not math.isclose(shader.inputs['Transmission Dispersion Abbe Number'].default_value, abbe_number, rel_tol=1e-6):
                raise ValueError('Abbe number is outside this Blender build\'s supported socket range')
            material['reference_abbe_number'] = abbe_number
            material['reference_dispersion_scale'] = dispersion_scale
            sigma = record.get('extinction_per_metre', [0, 0, 0])
            if any(not math.isfinite(v) or v < 0 for v in sigma):
                raise ValueError("Extinction coefficients must be finite and non-negative")
            density = max(sigma)
            if density > 0:
                absorption = nodes.new('ShaderNodeVolumeAbsorption')
                absorption.inputs['Color'].default_value = (*(1 - s / density for s in sigma), 1)
                absorption.inputs['Density'].default_value = density
                links.new(absorption.outputs['Volume'], output.inputs['Volume'])
        links.new(shader.outputs[0], output.inputs['Surface'])
        return material
    color = base_color_nodes(material, record, package_dir)
    if record["kind"] == "unlit":
        emission = nodes.new('ShaderNodeEmission')
        links.new(color, emission.inputs['Color'])
        path = nodes.new('ShaderNodeLightPath')
        first = nodes.new('ShaderNodeMath'); first.operation = 'MAXIMUM'
        second = nodes.new('ShaderNodeMath'); second.operation = 'MAXIMUM'
        links.new(path.outputs['Is Camera Ray'], first.inputs[0])
        links.new(path.outputs['Is Transmission Ray'], first.inputs[1])
        links.new(first.outputs[0], second.inputs[0])
        links.new(path.outputs['Is Glossy Ray'], second.inputs[1])
        transparent = nodes.new('ShaderNodeBsdfTransparent')
        mix = nodes.new('ShaderNodeMixShader')
        links.new(second.outputs[0], mix.inputs[0])
        links.new(transparent.outputs[0], mix.inputs[1])
        links.new(emission.outputs[0], mix.inputs[2])
        links.new(mix.outputs[0], output.inputs['Surface'])
    else:
        shader = nodes.new('ShaderNodeBsdfPrincipled')
        links.new(color, shader.inputs['Base Color'])
        shader.inputs['Metallic'].default_value = record.get('metallic', 0)
        shader.inputs['Roughness'].default_value = record.get('roughness', 0.5)
        links.new(shader.outputs[0], output.inputs['Surface'])
    return material


def make_mesh(scene, record, materials):
    vertices = [(x, z, y) for x, y, z in triples(record["vertices"])]
    if not vertices or not all(math.isfinite(v) for p in vertices for v in p):
        raise ValueError("Mesh has no vertices or has non-finite coordinates")
    faces, ids = [], []
    for submesh in record["submeshes"]:
        material_id = submesh["material"]
        if not 0 <= material_id < len(materials):
            raise ValueError("Invalid material index")
        for a, b, c in triples(submesh["triangles"]):
            if min(a, b, c) < 0 or max(a, b, c) >= len(vertices):
                raise ValueError("Triangle index outside mesh")
            faces.append((a, c, b))
            ids.append(material_id)
    mesh = bpy.data.meshes.new(record["name"])
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(record["name"], mesh)
    scene.collection.objects.link(obj)
    if record.get('source_local_to_world'):
        obj['unity_source_local_to_world'] = record['source_local_to_world']
    for material in materials:
        mesh.materials.append(material)
    for poly, material_id in zip(mesh.polygons, ids):
        poly.material_index = material_id
    uv = triples(record.get("uv", []), 2)
    if uv:
        if len(uv) != len(vertices):
            raise ValueError("UV count differs from vertex count")
        layer = mesh.uv_layers.new(name="Unity UV0")
        for loop in mesh.loops:
            layer.data[loop.index].uv = uv[loop.vertex_index]
    normals = triples(record.get("normals", []))
    if normals:
        if len(normals) != len(vertices):
            raise ValueError("Normal count differs from vertex count")
        for poly in mesh.polygons:
            poly.use_smooth = True
        mesh.normals_split_custom_set_from_vertices([(x, z, y) for x, y, z in normals])
    if ids and all(materials[i].get("reference_kind") == "unlit" for i in set(ids)):
        obj.visible_diffuse = False
        obj.visible_shadow = False
    return obj


def make_light(scene, record, power_scale, warnings):
    kind = {'Directional': 'SUN', 'Point': 'POINT', 'Spot': 'SPOT', 'Area': 'AREA', 'Rectangle': 'AREA', 'Disc': 'AREA'}.get(record['type'])
    if kind is None:
        warnings.append(f"Skipped unsupported light type {record['type']}: {record['name']}")
        return
    light = bpy.data.lights.new(record['name'], kind)
    obj = bpy.data.objects.new(record['name'], light)
    scene.collection.objects.link(obj)
    x, y, z = record['position']; obj.location = (x, z, y)
    x, y, z = record['direction']
    obj.rotation_euler = Vector((x, z, y)).to_track_quat('-Z', 'Y').to_euler()
    light.color = record['color'][:3]
    light.energy = record['intensity'] * power_scale
    light.use_shadow = record.get('shadows', True)
    if kind == 'SPOT':
        light.spot_size = math.radians(record['spot_angle'])
        light.spot_blend = max(0, min(1, 1 - record['inner_spot_angle'] / max(record['spot_angle'], 0.001)))
        light.shadow_soft_size = 0
    if kind == 'POINT':
        light.shadow_soft_size = 0
    if kind == 'AREA':
        light.shape = 'RECTANGLE'
        light.size, light.size_y = record.get('area_size', [1, 1])
    obj['unity_light'] = json.dumps(record)


def build_reference(package_file, *, eye=None, samples=256, baseline=False,
                    light_scale=1.0, glass_roughness=0.0, world_strength=0.0, camera_override=None,
                    abbe_number=DEFAULT_ABBE_NUMBER, dispersion_scale=DEFAULT_DISPERSION_SCALE):
    validate_glass_settings(abbe_number, dispersion_scale)
    package_file = Path(package_file).resolve()
    package = json.loads(package_file.read_text(encoding='utf-8-sig'))
    if package.get('schema_version') != 1:
        raise ValueError("Unsupported reference package schema")
    cameras = [camera_override] if camera_override is not None else package.get('cameras', [])
    if eye is None:
        eye = 'left' if any(c['eye'] == 'left' for c in cameras) else 'mono'
    matches = [c for c in cameras if c['eye'] == eye]
    if len(matches) != 1:
        raise ValueError(f"Eye {eye!r} not present; available: {[c['eye'] for c in cameras]}")
    if samples < 1 or not 0 <= glass_roughness <= 1 or light_scale < 0 or world_strength < 0:
        raise ValueError("Invalid render settings")
    scene = bpy.data.scenes.new(f"Unity Reference ({eye})")
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = samples
    scene.cycles.use_denoising = False
    scene.cycles.use_adaptive_sampling = False
    scene.cycles.seed = 0
    scene.cycles.max_bounces = 32
    scene.cycles.transmission_bounces = 24
    scene.cycles.glossy_bounces = 24
    scene.cycles.transparent_max_bounces = 32
    scene.cycles.sample_clamp_direct = 0
    scene.cycles.sample_clamp_indirect = 0
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1
    world = bpy.data.worlds.new('Reference World'); world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Color'].default_value = (1, 1, 1, 1)
    world.node_tree.nodes['Background'].inputs['Strength'].default_value = world_strength
    scene.world = world
    _, camera_error = configure_camera(scene, matches[0])
    materials = []
    warnings = list(package.get('warnings', []))
    for record in package['materials']:
        material = make_material(record, package_file.parent, baseline, glass_roughness, abbe_number, dispersion_scale)
        material['reference_kind'] = record['kind']
        materials.append(material)
    has_glass = not baseline and any(record['kind'] == 'glass' for record in package['materials'])
    if has_glass:
        warnings.append('Reference dispersion uses the configured Abbe number independently of Unity tier keywords. '
                        'Unity IOR spread is preserved as metadata, not converted to Abbe. The provisional material still needs calibration.')
    for record in package['meshes']:
        make_mesh(scene, record, materials)
    for record in package.get('lights', []):
        make_light(scene, record, light_scale, warnings)
    warnings.append('Lighting power/URP shading, texture filtering, analytic proxy normals and colour/post-processing require baseline validation. This import is not an automatically validated ground truth.')
    report = {
        'schema_version': 1, 'blender_version': bpy.app.version_string,
        'package_sha256': hashlib.sha256(package_file.read_bytes()).hexdigest(),
        'texture_sha256': {m['base_texture']: hashlib.sha256(texture_path(package_file.parent, m['base_texture']).read_bytes()).hexdigest()
                           for m in package['materials'] if m.get('base_texture')},
        'eye': eye, 'camera_intrinsics_max_error': camera_error,
        'mesh_count': len(package['meshes']), 'material_count': len(materials),
        'baseline': baseline, 'glass_surface_model': 'Principled BSDF' if has_glass else 'not_applicable',
        'dispersion_mode': 'cycles_principled' if has_glass and dispersion_scale > 0 else 'none',
        'abbe_number': abbe_number, 'dispersion_scale': dispersion_scale, 'glass_roughness': glass_roughness,
        'light_scale_uncalibrated': light_scale, 'world_strength': world_strength,
        'samples': samples, 'seed': 0, 'denoising': False, 'max_bounces': 32,
        'reference_validated': False, 'warnings': warnings,
    }
    scene['reference_report'] = json.dumps(report, indent=2)
    if bpy.context.window:
        bpy.context.window.scene = scene
    return scene, report


class IMPORT_SCENE_OT_unity_reference(bpy.types.Operator, ImportHelper):
    bl_idname = 'import_scene.unity_reference'
    bl_label = 'Unity Reference Package'
    filename_ext = '.json'
    filter_glob: StringProperty(default='*.json', options={'HIDDEN'})
    eye: EnumProperty(name='Camera', items=[('auto', 'Automatic', 'Left eye if available, otherwise mono'),
                                           ('mono', 'Mono', ''), ('left', 'Left eye', ''), ('right', 'Right eye', '')], default='auto')
    baseline: BoolProperty(name='No-glass baseline', default=False)
    abbe_number: FloatProperty(name='Reference Abbe number', default=DEFAULT_ABBE_NUMBER, min=0.001)
    dispersion_scale: FloatProperty(name='Dispersion scale', default=DEFAULT_DISPERSION_SCALE, min=0, max=1)

    def execute(self, context):
        try:
            scene, report = build_reference(self.filepath, eye=None if self.eye == 'auto' else self.eye, baseline=self.baseline,
                                            abbe_number=self.abbe_number, dispersion_scale=self.dispersion_scale)
            self.report({'INFO'}, f"Imported {report['mesh_count']} meshes into {scene.name}. Read Scene custom property reference_report.")
            return {'FINISHED'}
        except Exception as error:
            self.report({'ERROR'}, str(error))
            return {'CANCELLED'}


def menu_import(self, context):
    self.layout.operator(IMPORT_SCENE_OT_unity_reference.bl_idname, text='Unity Reference Package (.json)')


def register():
    previous = bpy.app.driver_namespace.get('unity_reference_import_menu')
    if previous is not None:
        bpy.types.TOPBAR_MT_file_import.remove(previous)
    existing = getattr(bpy.types, IMPORT_SCENE_OT_unity_reference.__name__, None)
    if existing:
        bpy.utils.unregister_class(existing)
    bpy.utils.register_class(IMPORT_SCENE_OT_unity_reference)
    bpy.types.TOPBAR_MT_file_import.append(menu_import)
    bpy.app.driver_namespace['unity_reference_import_menu'] = menu_import


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    if not argv and not bpy.app.background:
        register()
        bpy.ops.import_scene.unity_reference('INVOKE_DEFAULT')
        return
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--eye', choices=['mono', 'left', 'right'])
    parser.add_argument('--samples', type=int, default=256)
    parser.add_argument('--baseline', action='store_true')
    parser.add_argument('--light-scale', type=float, default=1)
    parser.add_argument('--world-strength', type=float, default=0)
    parser.add_argument('--glass-roughness', type=float, default=0)
    parser.add_argument('--abbe-number', type=float, default=DEFAULT_ABBE_NUMBER)
    parser.add_argument('--dispersion-scale', type=float, default=DEFAULT_DISPERSION_SCALE)
    parser.add_argument('--render', action='store_true')
    parser.add_argument('--overwrite', action='store_true')
    args = parser.parse_args(argv)
    output = args.output.resolve()
    if output.suffix.lower() != '.blend':
        parser.error('--output must end in .blend')
    report_path = output.with_suffix('.report.json')
    render_path = output.with_suffix('.exr')
    for path in (output, report_path, render_path if args.render else output):
        if path.exists() and not args.overwrite:
            raise FileExistsError(f"Refusing to overwrite {path}; choose another output or --overwrite")
    scene, report = build_reference(args.package, eye=args.eye, samples=args.samples, baseline=args.baseline,
                                    light_scale=args.light_scale, world_strength=args.world_strength,
                                    glass_roughness=args.glass_roughness, abbe_number=args.abbe_number,
                                    dispersion_scale=args.dispersion_scale)
    output.parent.mkdir(parents=True, exist_ok=True)
    scene.render.filepath = str(render_path)
    for image in bpy.data.images:
        if image.source == 'FILE' and image.filepath:
            image.pack()
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
    if args.render:
        bpy.ops.render.render(write_still=True, scene=scene.name)
    print(f"REFERENCE_IMPORT_PASS: {output}")


if __name__ == '__main__':
    main()
