using System;
using UnityEditor;
using UnityEngine;

namespace Thesis.Glass.EditorTools
{
    public static class GlassValidation
    {
        [MenuItem("Tools/Regenerate and Validate Glass Assets")]
        public static void Validate()
        {
            GlassProxyMeshGenerator.Generate();
            GlassConditionGenerator.Generate();
            foreach (string guid in AssetDatabase.FindAssets("t:Mesh", new[] { "Assets/VRShaders/Glass/Meshes" }))
            {
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                Vector3[] vertices = mesh.vertices, normals = mesh.normals;
                int[] triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                    Vector3 cross = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                    if (cross.sqrMagnitude < 1e-16f || Vector3.Dot(cross, normals[a] + normals[b] + normals[c]) <= 0)
                        throw new InvalidOperationException($"{mesh.name}: degenerate/inward triangle {i / 3}");
                }
                if (mesh.name.Contains("Prism"))
                {
                    Vector3[] planes = { Vector3.forward, new Vector3(-0.8660254f, 0, -0.5f), new Vector3(0.8660254f, 0, -0.5f) };
                    foreach (Vector3 vertex in vertices)
                    {
                        foreach (Vector3 plane in planes)
                            if (Vector3.Dot(vertex, plane) > GlassProxyMeshGenerator.PrismInradius + 1e-5f)
                                throw new InvalidOperationException("Prism mesh exceeds analytic proxy.");
                    }
                }
                Debug.Log($"[GlassValidation] {mesh.name}: {vertices.Length} vertices, {triangles.Length / 3} outward triangles.");
            }

            Shader shader = Shader.Find("Thesis/GlassTiered");
            if (shader == null) throw new InvalidOperationException("Glass shader not found.");
            ShaderUtil.ClearShaderMessages(shader);
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/VRShaders/Glass/Conditions" }))
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                Color color = material.GetColor("_BaseColor");
                color.a = 0.1f;
                material.SetColor("_BaseColor", color);
                EditorUtility.SetDirty(material);
                ShaderUtil.CompilePass(material, 0, true);
                count++;
            }
            AssetDatabase.SaveAssets();
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
            {
                if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                    throw new InvalidOperationException($"Shader error: {message.message} ({message.file}:{message.line})");
                Debug.LogWarning($"[GlassValidation] {message.message}");
            }
            if (ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Glass shader compilation failed.");
            Debug.Log($"[GlassValidation] PASS: compiled {count} material variants on the current desktop graphics backend. Android/XR still requires a device build.");
        }
    }
}
