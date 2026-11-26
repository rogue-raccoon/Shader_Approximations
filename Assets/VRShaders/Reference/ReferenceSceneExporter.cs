using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Thesis.Glass.Reference
{
    public sealed class ReferenceSceneExporter : MonoBehaviour
    {
        [SerializeField] Camera referenceCamera;
        [Tooltip("Empty exports all loaded scene roots. Otherwise include the enclosure, targets and glass.")]
        [SerializeField] GameObject[] sceneRoots;
        [SerializeField] bool includeStereo = true;
        [SerializeField] bool exportOnStart;
        bool requested;

        void OnEnable() => Application.onBeforeRender += OnBeforeRender;
        void OnDisable() { Application.onBeforeRender -= OnBeforeRender; requested = false; }
        void Start() { if (exportOnStart) RequestExport(); }

        [ContextMenu("Request Reference Export")]
        public void RequestExport()
        {
            if (Application.isPlaying) requested = true;
            else ExportNow();
        }

        void OnBeforeRender() { if (requested) { requested = false; ExportNow(); } }

        void ExportNow()
        {
            try
            {
                Camera camera = referenceCamera != null ? referenceCamera : Camera.main;
                string path = Path.Combine(Application.persistentDataPath, "ReferenceExports",
                    "Reference_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N"));
                WritePackage(camera, sceneRoots, path, includeStereo);
                Debug.Log($"[ReferenceExport] Saved {path}/scene.json. This is a scene snapshot, not an eye-image capture.");
            }
            catch (Exception e) { Debug.LogError($"[ReferenceExport] {e}"); }
        }

        [Serializable] public sealed class Package
        {
            public int schema_version = 1;
            public string unity_version, exported_utc, scene_name, color_space;
            public string coordinates = "Unity world: X right, Y up, Z forward; metres; row-major matrices";
            public bool playing, stereo;
            public int frame;
            public float realtime;
            public List<CameraRecord> cameras = new List<CameraRecord>();
            public List<MaterialRecord> materials = new List<MaterialRecord>();
            public List<MeshRecord> meshes = new List<MeshRecord>();
            public List<LightRecord> lights = new List<LightRecord>();
            public List<string> warnings = new List<string>();
        }
        [Serializable] public sealed class CameraRecord
        {
            public string name, eye;
            public float[] view, projection;
            public int width, height, culling_mask;
            public float near, far;
            public bool orthographic;
        }
        [Serializable] public sealed class MaterialRecord
        {
            public string name, shader, kind, base_texture, texture_wrap, texture_filter;
            public string[] keywords;
            public float[] base_color, texture_scale_offset, absorption_transmittance, extinction_per_metre, proxy_extents;
            public float ior, dispersion, reflection_roughness, metallic, roughness;
            public float absorption_density, thickness_scale, spectral_samples, max_internal_bounces, thin_offset;
        }
        [Serializable] public sealed class SubmeshRecord { public int material; public int[] triangles; }
        [Serializable] public sealed class MeshRecord
        {
            public string name;
            public float[] vertices, normals, uv, source_local_to_world;
            public List<SubmeshRecord> submeshes = new List<SubmeshRecord>();
        }
        [Serializable] public sealed class LightRecord
        {
            public string name, type;
            public float[] position, direction, color, area_size;
            public float intensity, range, spot_angle, inner_spot_angle;
            public bool shadows;
        }

        public static void WritePackage(Camera camera, GameObject[] roots, string directory, bool stereo = true)
        {
            if (camera == null) throw new ArgumentException("Assign a reference camera.");
            if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length > 0)
                throw new IOException("Choose a new or empty export folder; existing trials are never overwritten.");
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "textures"));
            var package = new Package
            {
                unity_version = Application.unityVersion, exported_utc = DateTime.UtcNow.ToString("O"),
                scene_name = camera.gameObject.scene.name, color_space = QualitySettings.activeColorSpace.ToString(),
                playing = Application.isPlaying, frame = Time.frameCount, realtime = Time.realtimeSinceStartup
            };
            package.stereo = stereo && Application.isPlaying && camera.stereoEnabled && XRSettings.enabled;
            if (package.stereo)
            {
                foreach (Camera.StereoscopicEye eye in new[] { Camera.StereoscopicEye.Left, Camera.StereoscopicEye.Right })
                    package.cameras.Add(CameraData(camera, eye.ToString().ToLowerInvariant(), camera.GetStereoViewMatrix(eye),
                        camera.GetStereoProjectionMatrix(eye), XRSettings.eyeTextureWidth, XRSettings.eyeTextureHeight));
                package.warnings.Add("XR resolution is the eye-texture allocation. Verify the capture viewport, render scale/foveation and pose against the actual eye images; no images or compositor output are captured here.");
            }
            else
            {
                package.cameras.Add(CameraData(camera, "mono", camera.worldToCameraMatrix, camera.projectionMatrix, camera.pixelWidth, camera.pixelHeight));
                if (stereo && Application.isPlaying) package.warnings.Add("Stereo requested but XR stereo is not active: this package contains only a mono camera. Wait for XR initialization and request another export.");
            }
            package.warnings.Add("Lights retain Unity intensities; their conversion to Cycles power is not calibrated. Skybox, probes, baked lightmaps, ambient lighting and post-processing are not transferred.");
            var renderers = new HashSet<Renderer>();
            var lights = new HashSet<Light>();
            if (roots == null || roots.Length == 0)
            {
                var allRoots = new List<GameObject>();
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.isLoaded) allRoots.AddRange(scene.GetRootGameObjects());
                }
                roots = allRoots.ToArray();
            }
            foreach (GameObject root in roots)
            {
                if (root == null) continue;
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>()) renderers.Add(renderer);
                foreach (Light light in root.GetComponentsInChildren<Light>()) lights.Add(light);
            }
            var materialIds = new Dictionary<Material, int>();
            var sortedRenderers = new List<Renderer>(renderers);
            sortedRenderers.Sort((a, b) => string.CompareOrdinal(HierarchyPath(a.transform), HierarchyPath(b.transform)));
            foreach (Renderer renderer in sortedRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff ||
                    (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                Mesh mesh = null;
                bool temporary = false;
                if (renderer is SkinnedMeshRenderer skin)
                {
                    mesh = new Mesh(); skin.BakeMesh(mesh); temporary = true;
                }
                else if (renderer.TryGetComponent(out MeshFilter filter)) mesh = filter.sharedMesh;
                if (mesh == null) { package.warnings.Add($"Unsupported renderer: {HierarchyPath(renderer.transform)}"); continue; }
                try
                {
                    if (!mesh.isReadable) throw new InvalidOperationException("Mesh requires Read/Write Enabled for snapshot export.");
                    if (renderer.HasPropertyBlock()) package.warnings.Add($"MaterialPropertyBlock not transferred: {renderer.name}");
                    Material[] materials = renderer.sharedMaterials;
                    if (materials.Length != mesh.subMeshCount) package.warnings.Add($"Material/submesh count differs: {renderer.name}; extra multipass materials are not transferred.");
                    Matrix4x4 matrix = renderer.localToWorldMatrix;
                    if (Mathf.Abs(matrix.determinant) < 1e-10f) throw new InvalidOperationException("Singular world transform.");
                    Matrix4x4 normalMatrix = matrix.inverse.transpose;
                    Vector3[] vertices = mesh.vertices, normals = mesh.normals;
                    Vector2[] uv = mesh.uv;
                    var record = new MeshRecord { name = HierarchyPath(renderer.transform), source_local_to_world = M(matrix), vertices = new float[vertices.Length * 3],
                        normals = normals.Length == vertices.Length ? new float[vertices.Length * 3] : new float[0], uv = uv.Length == vertices.Length ? new float[vertices.Length * 2] : new float[0] };
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Put(record.vertices, i * 3, matrix.MultiplyPoint3x4(vertices[i]));
                        if (record.normals.Length > 0) Put(record.normals, i * 3, normalMatrix.MultiplyVector(normals[i]).normalized);
                        if (record.uv.Length > 0) { record.uv[i * 2] = uv[i].x; record.uv[i * 2 + 1] = uv[i].y; }
                    }
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        if (mesh.GetTopology(s) != MeshTopology.Triangles) throw new InvalidOperationException("Only triangle meshes are supported.");
                        Material material = materials.Length > 0 ? materials[Mathf.Min(s, materials.Length - 1)] : null;
                        if (material == null) throw new InvalidOperationException("Missing material.");
                        if (!materialIds.TryGetValue(material, out int id))
                        {
                            id = package.materials.Count;
                            package.materials.Add(MaterialData(material, id, directory, package));
                            materialIds.Add(material, id);
                        }
                        int[] triangles = mesh.GetTriangles(s);
                        if (matrix.determinant < 0)
                            for (int t = 0; t < triangles.Length; t += 3) { int swap = triangles[t + 1]; triangles[t + 1] = triangles[t + 2]; triangles[t + 2] = swap; }
                        record.submeshes.Add(new SubmeshRecord { material = id, triangles = triangles });
                    }
                    package.meshes.Add(record);
                }
                catch (Exception e) { package.warnings.Add($"SKIPPED {renderer.name}: {e.Message}"); }
                finally { if (temporary) DisposeTransient(mesh); }
            }
            var sortedLights = new List<Light>(lights);
            sortedLights.Sort((a, b) => string.CompareOrdinal(HierarchyPath(a.transform), HierarchyPath(b.transform)));
            foreach (Light light in sortedLights)
                if (light.enabled && light.gameObject.activeInHierarchy)
                {
                    package.lights.Add(new LightRecord { name = HierarchyPath(light.transform), type = light.type.ToString(),
                        position = V(light.transform.position), direction = V(light.transform.forward), color = C(ShaderColor(light.color)),
                        intensity = light.intensity, range = light.range, spot_angle = light.spotAngle, inner_spot_angle = light.innerSpotAngle,
                        area_size = new[] { light.areaSize.x, light.areaSize.y }, shadows = light.shadows != LightShadows.None });
                    if (light.useColorTemperature) package.warnings.Add($"Light colour temperature not transferred: {light.name}");
                }
            if (package.meshes.Count == 0) throw new InvalidOperationException("No supported visible meshes were exported. See scene selection and mesh Read/Write settings.");
            File.WriteAllText(Path.Combine(directory, "scene.json"), JsonUtility.ToJson(package, true));
            File.WriteAllLines(Path.Combine(directory, "ExportNotes.txt"), package.warnings);
        }

        static CameraRecord CameraData(Camera camera, string eye, Matrix4x4 view, Matrix4x4 projection, int width, int height)
            => new CameraRecord { name = camera.name, eye = eye, view = M(view), projection = M(projection), width = Mathf.Max(1, width),
                height = Mathf.Max(1, height), near = camera.nearClipPlane, far = camera.farClipPlane, orthographic = camera.orthographic, culling_mask = camera.cullingMask };

        static MaterialRecord MaterialData(Material m, int id, string directory, Package package)
        {
            bool glass = m.shader.name == "Thesis/GlassTiered";
            bool unlit = m.shader.name == "Universal Render Pipeline/Unlit" || m.shader.name == "Unlit/Texture" || m.shader.name == "Unlit/Color";
            if (!glass && !unlit && m.shader.name != "Universal Render Pipeline/Lit") package.warnings.Add($"Approximate unsupported shader {m.shader.name}: {m.name}");
            if (!glass && m.renderQueue >= 3000) package.warnings.Add($"Non-glass transparency not reproduced: {m.name}");
            foreach (string keyword in new[] { "_NORMALMAP", "_METALLICSPECGLOSSMAP", "_OCCLUSIONMAP", "_EMISSION", "_ALPHATEST_ON" })
                if (!glass && m.IsKeywordEnabled(keyword)) package.warnings.Add($"Material feature {keyword} is not transferred: {m.name}");
            Color color = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            var r = new MaterialRecord { name = m.name, shader = m.shader.name, kind = glass ? "glass" : unlit ? "unlit" : "lit",
                base_color = C(ShaderColor(color)), keywords = m.shaderKeywords, metallic = Float(m, "_Metallic", 0),
                roughness = 1 - Float(m, "_Smoothness", 0.5f), ior = Float(m, "_IOR", 1.52f), dispersion = Float(m, "_Dispersion", 0),
                reflection_roughness = Float(m, "_Roughness", 0), base_texture = "", extinction_per_metre = new float[3] };
            Array.Sort(r.keywords, StringComparer.Ordinal);
            string textureProperty = m.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            Vector2 scale = m.HasProperty(textureProperty) ? m.GetTextureScale(textureProperty) : Vector2.one;
            Vector2 offset = m.HasProperty(textureProperty) ? m.GetTextureOffset(textureProperty) : Vector2.zero;
            r.texture_scale_offset = new[] { scale.x, scale.y, offset.x, offset.y };
            if (m.HasProperty(textureProperty) && m.GetTexture(textureProperty) is Texture2D texture)
            {
                r.texture_wrap = texture.wrapMode.ToString(); r.texture_filter = texture.filterMode.ToString();
                if (texture.wrapMode == TextureWrapMode.Mirror || texture.wrapMode == TextureWrapMode.MirrorOnce)
                    package.warnings.Add($"Mirrored texture wrapping is not reproduced: {m.name}");
                r.base_texture = $"textures/material_{id:D3}.png";
                SaveTexture(texture, Path.Combine(directory, r.base_texture));
            }
            if (glass)
            {
                Color transmittance = ShaderColor(m.GetColor("_AbsorptionColor"));
                r.absorption_transmittance = C(transmittance);
                r.absorption_density = Float(m, "_AbsorptionDensity", 1); r.thickness_scale = Float(m, "_ThicknessScale", 1);
                r.spectral_samples = Float(m, "_SpectralSamples", 1); r.max_internal_bounces = Float(m, "_MaxBounces", 1);
                r.thin_offset = Float(m, "_ThinOffset", 0);
                float density = Mathf.Max(0, r.absorption_density) * Mathf.Max(0, r.thickness_scale);
                for (int c = 0; c < 3; c++) r.extinction_per_metre[c] = -Mathf.Log(Mathf.Clamp(transmittance[c], 1e-4f, 1)) * density;
                Vector4 extents = m.GetVector("_ProxyExtents"); r.proxy_extents = new[] { extents.x, extents.y, extents.z, extents.w };
                package.warnings.Add($"Glass {m.name}: reference uses physical interfaces/volume, not Unity tier keywords. Geometry remains the rendered mesh; analytic entry normals/proxy boundary differences can remain.");
            }
            return r;
        }

        static void SaveTexture(Texture2D texture, string path)
        {
#if UNITY_EDITOR
            string source = UnityEditor.AssetDatabase.GetAssetPath(texture);
            if (Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(source))
            { File.Copy(source, path); return; }
#endif
            RenderTexture previous = RenderTexture.active;
            bool previousWrite = GL.sRGBWrite;
            RenderTexture rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D copy = null;
            try
            {
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply(); File.WriteAllBytes(path, copy.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous; GL.sRGBWrite = previousWrite;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) DisposeTransient(copy);
            }
        }
        static void DisposeTransient(UnityEngine.Object value) { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
        static float Float(Material m, string name, float fallback) => m.HasProperty(name) ? m.GetFloat(name) : fallback;
        static Color ShaderColor(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;
        static float[] C(Color c) => new[] { c.r, c.g, c.b, c.a };
        static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        static void Put(float[] a, int offset, Vector3 v) { a[offset] = v.x; a[offset + 1] = v.y; a[offset + 2] = v.z; }
        static float[] M(Matrix4x4 m) { var a = new float[16]; for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) a[r * 4 + c] = m[r, c]; return a; }
        static string HierarchyPath(Transform t) => t.parent == null ? t.name : HierarchyPath(t.parent) + "/" + t.name;
    }
}
