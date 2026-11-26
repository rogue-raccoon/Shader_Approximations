using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Thesis.Glass.EditorTools
{
    public sealed class CornellBoxGenerator : EditorWindow
    {
        [SerializeField] Vector3 dimensions = new Vector3(3, 3, 3);
        [SerializeField] float wallThickness = 0.05f;
        [SerializeField] float lightIntensity = 20;
        [SerializeField] bool includeBlocks;
        [SerializeField] Material white, red, green;
        const string MaterialDirectory = "Assets/VRShaders/CornellBox/Materials/";

        [MenuItem("Tools/Create Procedural Cornell Box")]
        public static void Open() => GetWindow<CornellBoxGenerator>("Cornell Box");

        void OnEnable()
        {
            if (!white) white = AssetDatabase.LoadAssetAtPath<Material>(MaterialDirectory + "M_White.mat");
            if (!red) red = AssetDatabase.LoadAssetAtPath<Material>(MaterialDirectory + "M_Red.mat");
            if (!green) green = AssetDatabase.LoadAssetAtPath<Material>(MaterialDirectory + "M_Green.mat");
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Reproducible Cornell Box", EditorStyles.boldLabel);
            dimensions = EditorGUILayout.Vector3Field("Interior size (metres)", dimensions);
            wallThickness = EditorGUILayout.FloatField("Wall thickness (metres)", wallThickness);
            white = (Material)EditorGUILayout.ObjectField("White surfaces", white, typeof(Material), false);
            red = (Material)EditorGUILayout.ObjectField("Left wall", red, typeof(Material), false);
            green = (Material)EditorGUILayout.ObjectField("Right wall", green, typeof(Material), false);
            lightIntensity = EditorGUILayout.FloatField("Spot intensity (Unity units)", lightIntensity);
            includeBlocks = EditorGUILayout.Toggle("Include reference blocks", includeBlocks);
            bool valid = IsPositive(dimensions.x) && IsPositive(dimensions.y) && IsPositive(dimensions.z)
                && IsPositive(wallThickness) && IsPositive(lightIntensity) && white && red && green;
            using (new EditorGUI.DisabledScope(!valid))
                if (GUILayout.Button("Create Box in Active Scene")) CreateBox();
        }

        static bool IsPositive(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);

        void CreateBox()
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Procedural Cornell Box");
            var root = new GameObject("Cornell Box (Procedural)");
            float w = dimensions.x, h = dimensions.y, d = dimensions.z, t = wallThickness;
            Surface(root.transform, "Floor", new Vector3(0, -t / 2, 0), new Vector3(w + 2 * t, t, d + t), white);
            Surface(root.transform, "Ceiling", new Vector3(0, h + t / 2, 0), new Vector3(w + 2 * t, t, d + t), white);
            Surface(root.transform, "Back", new Vector3(0, h / 2, (d + t) / 2), new Vector3(w + 2 * t, h, t), white);
            Surface(root.transform, "Left (Red)", new Vector3(-(w + t) / 2, h / 2, 0), new Vector3(t, h, d), red);
            Surface(root.transform, "Right (Green)", new Vector3((w + t) / 2, h / 2, 0), new Vector3(t, h, d), green);
            Surface(root.transform, "Ceiling light panel (visual)", new Vector3(0, h - 0.01f * h, 0), new Vector3(w * 0.25f, h * 0.005f, d * 0.25f), white);

            var source = new GameObject("Ceiling illumination (spot approximation)");
            source.transform.SetParent(root.transform, false);
            source.transform.localPosition = new Vector3(0, h * 0.97f, 0);
            source.transform.localRotation = Quaternion.Euler(90, 0, 0);
            Light light = source.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 140;
            light.innerSpotAngle = 100;
            light.range = dimensions.magnitude * 2;
            light.intensity = lightIntensity;
            light.shadows = LightShadows.Soft;

            var probeObject = new GameObject("Reflection Probe (bake after placement)");
            probeObject.transform.SetParent(root.transform, false);
            probeObject.transform.localPosition = new Vector3(0, h / 2, 0);
            ReflectionProbe probe = probeObject.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Baked;
            probe.size = dimensions;
            probe.boxProjection = true;
            probe.resolution = 256;
            probe.nearClipPlane = Mathf.Min(w, Mathf.Min(h, d)) * 0.001f;
            probe.farClipPlane = dimensions.magnitude * 2;
            probe.clearFlags = ReflectionProbeClearFlags.SolidColor;
            probe.backgroundColor = Color.black;
            probe.blendDistance = 0;

            Marker(root.transform, "Test object position", new Vector3(0, h * 0.35f, 0));
            Marker(root.transform, "Camera position (faces +Z)", new Vector3(0, h * 0.5f, -d * 1.5f));
            if (includeBlocks)
            {
                float footprint = Mathf.Min(w, d) * 0.25f;
                GameObject shortBlock = Surface(root.transform, "Short reference block", new Vector3(-w * 0.22f, h * 0.15f, d * 0.08f), new Vector3(footprint, h * 0.3f, footprint), white);
                shortBlock.transform.localRotation = Quaternion.Euler(0, -15, 0);
                GameObject tallBlock = Surface(root.transform, "Tall reference block", new Vector3(w * 0.2f, h * 0.3f, d * 0.16f), new Vector3(footprint, h * 0.6f, footprint), white);
                tallBlock.transform.localRotation = Quaternion.Euler(0, 18, 0);
            }
            Undo.RegisterCreatedObjectUndo(root, "Create Cornell Box");
            Selection.activeGameObject = root;
            Undo.CollapseUndoOperations(group);
            Debug.Log("[CornellBox] Created deterministic box. Bake the reflection probe after placement. Keep other scene lights/volumes controlled during measurement.", root);
        }

        static GameObject Surface(Transform parent, string name, Vector3 position, Vector3 size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = material;
            DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        static void Marker(Transform parent, string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
        }
    }
}
