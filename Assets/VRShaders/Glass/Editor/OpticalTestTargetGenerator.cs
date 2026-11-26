using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Thesis.Glass.EditorTools
{
    public sealed class OpticalTestTargetGenerator : EditorWindow
    {
        enum Target { All, Checkerboard, SiemensStars, DotGrid }
        [SerializeField] Target target;
        [SerializeField] float panelSize = 1;
        [SerializeField] int resolution = 1024;
        [SerializeField] Vector2Int checkerCells = new Vector2Int(8, 8);
        [SerializeField] Vector2Int starGrid = new Vector2Int(1, 1);
        [SerializeField] int spokePairs = 32;
        [SerializeField] Vector2Int dotGrid = new Vector2Int(9, 9);
        [SerializeField] float dotRadius = 0.18f;
        [SerializeField] Color foreground = Color.black;
        [SerializeField] Color background = Color.white;
        const string OutputRoot = "Assets/VRShaders/TestTargets";
        const int SamplesPerAxis = 2;

        [MenuItem("Tools/Create Optical Test Targets")]
        public static void Open() => GetWindow<OpticalTestTargetGenerator>("Optical Targets");

        void OnGUI()
        {
            EditorGUILayout.LabelField("Reproducible optical test targets", EditorStyles.boldLabel);
            target = (Target)EditorGUILayout.EnumPopup("Generate", target);
            panelSize = EditorGUILayout.FloatField("Panel side (metres)", panelSize);
            int[] sizes = { 512, 1024, 2048 };
            resolution = EditorGUILayout.IntPopup("Texture resolution", resolution,
                new[] { "512", "1024", "2048" }, sizes);
            foreground = EditorGUILayout.ColorField("Foreground", foreground);
            background = EditorGUILayout.ColorField("Background", background);
            if (target == Target.All || target == Target.Checkerboard)
                checkerCells = EditorGUILayout.Vector2IntField("Checker columns / rows", checkerCells);
            if (target == Target.All || target == Target.SiemensStars)
            {
                starGrid = EditorGUILayout.Vector2IntField("Star columns / rows", starGrid);
                spokePairs = EditorGUILayout.IntSlider("Black/white spoke pairs", spokePairs, 4, 64);
            }
            if (target == Target.All || target == Target.DotGrid)
            {
                dotGrid = EditorGUILayout.Vector2IntField("Dot columns / rows", dotGrid);
                dotRadius = EditorGUILayout.Slider("Dot radius / smallest cell", dotRadius, 0.05f, 0.45f);
            }
            EditorGUILayout.HelpBox("Targets face -Z, matching the Cornell Box opening. Saved opaque, unlit materials keep the targets independent of scene illumination and visible to the glass shader's opaque texture. Each generation saves a new asset set and its settings. Move one target behind the glass for each comparison.", MessageType.Info);
            bool valid = panelSize > 0 && !float.IsNaN(panelSize) && !float.IsInfinity(panelSize)
                && ValidGrid(checkerCells) && ValidGrid(starGrid) && ValidGrid(dotGrid)
                && (resolution == 512 || resolution == 1024 || resolution == 2048);
            using (new EditorGUI.DisabledScope(!valid))
                if (GUILayout.Button("Create Targets in Active Scene")) Generate();
        }

        static bool ValidGrid(Vector2Int grid) => grid.x >= 1 && grid.x <= 64 && grid.y >= 1 && grid.y <= 64;

        void Generate()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) { Debug.LogError("[OpticalTargets] URP Unlit shader not found."); return; }
            Directory.CreateDirectory(OutputRoot);
            AssetDatabase.Refresh();
            string directory = AssetDatabase.GenerateUniqueAssetPath(OutputRoot + "/Targets_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
            AssetDatabase.CreateFolder(OutputRoot, Path.GetFileName(directory));
            Target[] targets = target == Target.All
                ? new[] { Target.Checkerboard, Target.SiemensStars, Target.DotGrid } : new[] { target };
            GameObject root = null;
            try
            {
                Mesh mesh = PanelMesh();
                AssetDatabase.CreateAsset(mesh, directory + "/Panel.asset");
                root = new GameObject("Optical Test Targets");
                for (int i = 0; i < targets.Length; i++)
                {
                    Target kind = targets[i];
                    EditorUtility.DisplayProgressBar("Generate optical targets", kind.ToString(), (float)i / targets.Length);
                    Texture2D texture = Rasterize(kind);
                    string path = directory + "/" + kind + ".png";
                    try { File.WriteAllBytes(path, texture.EncodeToPNG()); }
                    finally { DestroyImmediate(texture); }
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = true;
                    importer.alphaSource = TextureImporterAlphaSource.None;
                    importer.mipmapEnabled = true;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.filterMode = FilterMode.Trilinear;
                    importer.anisoLevel = 1;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.maxTextureSize = resolution;
                    importer.SaveAndReimport();
                    var material = new Material(shader) { name = kind.ToString() };
                    material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                    material.SetColor("_BaseColor", Color.white);
                    AssetDatabase.CreateAsset(material, directory + "/" + kind + ".mat");
                    var panel = new GameObject(kind.ToString());
                    panel.transform.SetParent(root.transform, false);
                    panel.transform.localPosition = new Vector3((i - (targets.Length - 1) * 0.5f) * panelSize * 1.1f, panelSize * 0.5f, 0);
                    panel.transform.localScale = new Vector3(panelSize, panelSize, 1);
                    panel.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer renderer = panel.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                }
                File.WriteAllText(directory + "/Settings.json", EditorJsonUtility.ToJson(this, true));
                Undo.IncrementCurrentGroup();
                Undo.RegisterCreatedObjectUndo(root, "Create Optical Test Targets");
                Selection.activeGameObject = root;
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[OpticalTargets] Saved {targets.Length} panels and settings to {directory}. Scene creation can be undone; saved assets are retained.", root);
            }
            catch (Exception e)
            {
                if (root != null) DestroyImmediate(root);
                Debug.LogError($"[OpticalTargets] Generation failed. Partial assets are retained in {directory}: {e}");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        Texture2D Rasterize(Target kind)
        {
            var pixels = new Color32[resolution * resolution];
            Color dark = foreground.linear, light = background.linear;
            for (int y = 0; y < resolution; y++)
                for (int x = 0; x < resolution; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < SamplesPerAxis; sy++)
                        for (int sx = 0; sx < SamplesPerAxis; sx++)
                        {
                            float u = (x + (sx + 0.5f) / SamplesPerAxis) / resolution;
                            float v = (y + (sy + 0.5f) / SamplesPerAxis) / resolution;
                            if (IsForeground(kind, u, v)) covered++;
                        }
                    Color color = Color.Lerp(light, dark, covered / (float)(SamplesPerAxis * SamplesPerAxis)).gamma;
                    color.a = 1;
                    pixels[y * resolution + x] = color;
                }
            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        bool IsForeground(Target kind, float u, float v)
        {
            if (kind == Target.Checkerboard)
                return ((Mathf.FloorToInt(u * checkerCells.x) + Mathf.FloorToInt(v * checkerCells.y)) & 1) == 0;
            Vector2Int grid = kind == Target.DotGrid ? dotGrid : starGrid;
            float x = u - (Mathf.Floor(u * grid.x) + 0.5f) / grid.x;
            float y = v - (Mathf.Floor(v * grid.y) + 0.5f) / grid.y;
            float radius = (kind == Target.DotGrid ? dotRadius : 0.45f) / Mathf.Max(grid.x, grid.y);
            if (x * x + y * y > radius * radius) return false;
            if (kind == Target.DotGrid) return true;
            return Mathf.Cos(Mathf.Atan2(y, x) * spokePairs) >= 0;
        }

        static Mesh PanelMesh()
        {
            var mesh = new Mesh { name = "OpticalTargetPanel" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
