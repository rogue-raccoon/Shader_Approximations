using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Thesis.Glass.Reference.EditorTools
{
    public static class ReferenceExportValidation
    {
        [MenuItem("Tools/Validate Reference Export")]
        public static void Run()
        {
            Scene original = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            string directory = Path.GetFullPath("Temp/ReferenceValidation_" + Guid.NewGuid().ToString("N"));
            var root = new GameObject("Reference Validation");
            SceneManager.MoveGameObjectToScene(root, scene);
            Material unlit = null, glass = null;
            Texture2D texture = null;
            RenderTexture rt = null;
            try
            {
                var cameraObject = new GameObject("Validation Camera");
                cameraObject.transform.SetParent(root.transform);
                cameraObject.transform.position = new Vector3(0, 0, -3);
                Camera camera = cameraObject.AddComponent<Camera>();
                rt = new RenderTexture(256, 256, 0); camera.targetTexture = rt;
                Matrix4x4 projection = Matrix4x4.Perspective(60, 1, 0.1f, 100);
                projection.m02 = 0.14f; projection.m12 = -0.08f;
                camera.projectionMatrix = projection;
                unlit = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                unlit.SetColor("_BaseColor", Color.white);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white }); texture.Apply();
                unlit.SetTexture("_BaseMap", texture);
                var panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
                panel.name = "UV quadrants"; panel.transform.SetParent(root.transform);
                panel.transform.localPosition = new Vector3(0, 0, 1);
                panel.GetComponent<Renderer>().sharedMaterial = unlit;
                glass = new Material(Shader.Find("Thesis/GlassTiered"));
                glass.SetFloat("_IOR", 1.52f); glass.SetFloat("_Dispersion", 0);
                glass.SetColor("_AbsorptionColor", new Color(0.8f, 0.9f, 0.95f));
                glass.SetFloat("_AbsorptionDensity", 1); glass.SetFloat("_ThicknessScale", 1);
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "Mirrored Glass"; sphere.transform.SetParent(root.transform);
                sphere.transform.localScale = new Vector3(-0.5f, 0.5f, 0.5f);
                sphere.GetComponent<Renderer>().sharedMaterial = glass;
                ReferenceSceneExporter.WritePackage(camera, new[] { root }, directory, false);
                var package = JsonUtility.FromJson<ReferenceSceneExporter.Package>(File.ReadAllText(Path.Combine(directory, "scene.json")));
                if (package.meshes.Count != 2 || package.materials.Count != 2 || package.cameras.Count != 1)
                    throw new InvalidOperationException("Export fixture counts differ.");
                if (!File.Exists(Path.Combine(directory, "textures/material_001.png")) && !File.Exists(Path.Combine(directory, "textures/material_000.png")))
                    throw new InvalidOperationException("Target texture not exported.");
                foreach (var mesh in package.meshes)
                    foreach (var submesh in mesh.submeshes)
                        for (int t = 0; t < submesh.triangles.Length; t += 3)
                        {
                            int a = submesh.triangles[t], b = submesh.triangles[t + 1], c = submesh.triangles[t + 2];
                            Vector3 pa = Read(mesh.vertices, a), pb = Read(mesh.vertices, b), pc = Read(mesh.vertices, c);
                            Vector3 normal = Read(mesh.normals, a) + Read(mesh.normals, b) + Read(mesh.normals, c);
                            Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
                            if (cross.sqrMagnitude > 1e-12f && Vector3.Dot(normal, cross) < 0)
                                throw new InvalidOperationException("Mirrored mesh winding differs from normals.");
                        }
                File.WriteAllText("Temp/ReferenceValidationPackage.txt", directory);
                Debug.Log($"[ReferenceValidation] PASS: textured target, mirrored glass, asymmetric camera. Package: {directory}");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (unlit != null) UnityEngine.Object.DestroyImmediate(unlit);
                if (glass != null) UnityEngine.Object.DestroyImmediate(glass);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }
        }
        static Vector3 Read(float[] values, int i) => new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
    }
}
