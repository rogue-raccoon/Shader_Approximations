using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thesis.Glass.EditorTools
{
    public static class GlassConditionGenerator
    {
        const string ShaderName = "Thesis/GlassTiered";
        const string OutputDir  = "Assets/VRShaders/Glass/Conditions";

        struct Condition
        {
            public string Id;
            public string[] Keywords;
            public int MaxBounces;
            public int SpectralSamples;
        }

        struct Shape
        {
            public string Id;
            public string Keyword;
            public Vector4 Extents;
        }

        static readonly Shape[] Shapes =
        {
            new Shape { Id = "Sphere", Keyword = "_PROXY_SPHERE",
                        Extents = new Vector4(GlassProxyMeshGenerator.SphereRadius, 0, 0, 0) },
            new Shape { Id = "Prism",  Keyword = "_PROXY_PRISM",
                        Extents = new Vector4(GlassProxyMeshGenerator.PrismInradius,
                                              GlassProxyMeshGenerator.PrismHalfHeight, 0, 0) },
            new Shape { Id = "Lens",   Keyword = "_PROXY_LENS",
                        Extents = new Vector4(GlassProxyMeshGenerator.LensCurvature,
                                              GlassProxyMeshGenerator.LensHalfThick, 0, 0) },
        };

        static readonly Condition[] Conditions =
        {
            new Condition {
                Id = "T0_Reference",
                Keywords = new[] { "_REFRACT_SSR", "_DISPERSION_SPECTRAL", "_ABSORB_BEER",
                                   "_INTERNAL_MULTI", "_REFLECT_PROBE_ROUGH", "_THICKNESS_ANALYTIC" },
                MaxBounces = 4, SpectralSamples = 8 },

            new Condition {
                Id = "T1_Reduced",
                Keywords = new[] { "_REFRACT_SSR", "_DISPERSION_RGB", "_ABSORB_BEER",
                                   "_INTERNAL_SINGLE", "_REFLECT_PROBE_ROUGH", "_THICKNESS_ANALYTIC" },
                MaxBounces = 1, SpectralSamples = 3 },

            new Condition {
                Id = "T2_NoResolve",
                Keywords = new[] { "_REFRACT_CUBE", "_DISPERSION_RGB", "_ABSORB_BEER",
                                   "_REFLECT_PROBE", "_THICKNESS_BAKED", "_PRECISION_HALF" },
                MaxBounces = 1, SpectralSamples = 3 },

            new Condition {
                Id = "T3_Cheap",
                Keywords = new[] { "_REFRACT_CUBE", "_ABSORB_TINT",
                                   "_REFLECT_PROBE", "_THICKNESS_BAKED", "_PRECISION_HALF" },
                MaxBounces = 1, SpectralSamples = 1 },

            new Condition {
                Id = "T4_Minimal",
                Keywords = new[] { "_ABSORB_TINT", "_REFLECT_PROBE", "_PRECISION_HALF" },
                MaxBounces = 1, SpectralSamples = 1 },

            new Condition {
                Id = "A1_T0_DispersionRGB",
                Keywords = new[] { "_REFRACT_SSR", "_DISPERSION_RGB", "_ABSORB_BEER",
                                   "_INTERNAL_MULTI", "_REFLECT_PROBE_ROUGH", "_THICKNESS_ANALYTIC" },
                MaxBounces = 4, SpectralSamples = 3 },

            new Condition {
                Id = "A2_T0_NoDispersion",
                Keywords = new[] { "_REFRACT_SSR", "_ABSORB_BEER",
                                   "_INTERNAL_MULTI", "_REFLECT_PROBE_ROUGH", "_THICKNESS_ANALYTIC" },
                MaxBounces = 4, SpectralSamples = 1 },

            new Condition {
                Id = "A3_T0_SingleBounce",
                Keywords = new[] { "_REFRACT_SSR", "_DISPERSION_SPECTRAL", "_ABSORB_BEER",
                                   "_INTERNAL_SINGLE", "_REFLECT_PROBE_ROUGH", "_THICKNESS_ANALYTIC" },
                MaxBounces = 1, SpectralSamples = 8 },

            new Condition {
                Id = "A4_T0_CubeRefraction",  
                Keywords = new[] { "_REFRACT_CUBE", "_DISPERSION_SPECTRAL", "_ABSORB_BEER",
                                   "_INTERNAL_MULTI", "_REFLECT_PROBE_ROUGH", "_THICKNESS_ANALYTIC" },
                MaxBounces = 4, SpectralSamples = 8 },

            new Condition {
                Id = "A5_T0_TintAbsorption",
                Keywords = new[] { "_REFRACT_SSR", "_DISPERSION_SPECTRAL", "_ABSORB_TINT",
                                   "_INTERNAL_MULTI", "_REFLECT_PROBE_ROUGH", "_THICKNESS_ANALYTIC" },
                MaxBounces = 4, SpectralSamples = 8 },

            new Condition {
                Id = "A6_T0_SharpReflection",
                Keywords = new[] { "_REFRACT_SSR", "_DISPERSION_SPECTRAL", "_ABSORB_BEER",
                                   "_INTERNAL_MULTI", "_REFLECT_PROBE", "_THICKNESS_ANALYTIC" },
                MaxBounces = 4, SpectralSamples = 8 },

            new Condition {
                Id = "A7_T0_HalfPrecision",
                Keywords = new[] { "_REFRACT_SSR", "_DISPERSION_SPECTRAL", "_ABSORB_BEER",
                                   "_INTERNAL_MULTI", "_REFLECT_PROBE_ROUGH", "_THICKNESS_ANALYTIC",
                                   "_PRECISION_HALF" },
                MaxBounces = 4, SpectralSamples = 8 },
        };

        [MenuItem("Tools/Generate Glass Conditions")]
        public static void Generate()
        {
            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError($"[GlassConditions] Shader '{ShaderName}' not found.");
                return;
            }

            var created = new List<string>();

            foreach (var shape in Shapes)
            {
                string dir = $"{OutputDir}/{shape.Id}";
                Directory.CreateDirectory(dir);

                foreach (var c in Conditions)
                {
                    string path = $"{dir}/M_Glass_{shape.Id}_{c.Id}.mat";
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

                    if (mat == null)
                    {
                        mat = new Material(shader);
                        AssetDatabase.CreateAsset(mat, path);
                    }
                    else
                    {
                        mat.shader = shader;
                    }

                    mat.shaderKeywords = new string[0];
                    foreach (var kw in c.Keywords)
                        mat.EnableKeyword(kw);
                    mat.EnableKeyword(shape.Keyword);

                    if (c.Id.StartsWith("T0") || c.Id.StartsWith("T1") || c.Id.StartsWith("A"))
                        mat.EnableKeyword("_ANALYTIC_ENTRY_NORMAL");

                    mat.SetVector("_ProxyExtents", shape.Extents);
                    mat.SetFloat("_MaxBounces", c.MaxBounces);
                    mat.SetFloat("_SpectralSamples", c.SpectralSamples);
                    mat.SetFloat("_IOR", 1.52f);            
                    mat.SetFloat("_Dispersion", 0.02f);
                    Color baseColor = mat.GetColor("_BaseColor");
                    baseColor.a = 0.1f;
                    mat.SetColor("_BaseColor", baseColor);
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

                    EditorUtility.SetDirty(mat);
                    created.Add($"{shape.Id}/{c.Id}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[GlassConditions] {created.Count} condition materials " +
                      $"({Shapes.Length} shapes x {Conditions.Length} conditions) written to {OutputDir}");
        }
    }
}
