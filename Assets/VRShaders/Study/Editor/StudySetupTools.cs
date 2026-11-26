using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Thesis.Glass.Study.EditorTools
{
    public static class StudySetupTools
    {
        [MenuItem("Tools/Study/Create Run Profiles")]
        public static void CreateProfiles()
        {
            const string parent = "Assets/VRShaders/Study";
            if (!AssetDatabase.IsValidFolder(parent + "/Profiles")) AssetDatabase.CreateFolder(parent, "Profiles");
            foreach (StudyRunMode mode in Enum.GetValues(typeof(StudyRunMode)))
            {
                string path = $"{parent}/Profiles/{mode}.asset";
                if (AssetDatabase.LoadAssetAtPath<StudyRunSettings>(path) != null) continue;
                StudyRunSettings settings = ScriptableObject.CreateInstance<StudyRunSettings>(); settings.mode = mode;
                if (mode == StudyRunMode.ExternalPerformance) settings.performanceCycles = 6; 
                settings.conditions = AssetDatabase.FindAssets("t:Material", new[] { "Assets/VRShaders/Glass/Conditions/Sphere" })
                    .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
                    .Where(m => m.name.StartsWith("M_Glass_Sphere_T", StringComparison.Ordinal)).OrderBy(m => m.name, StringComparer.Ordinal).ToArray();
                AssetDatabase.CreateAsset(settings, path);
            }
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<StudyRunSettings>(parent + "/Profiles/Performance.asset"));
        }

        [MenuItem("Tools/Study/Install Eye Capture Feature")]
        public static void InstallCaptureFeature()
        {
            foreach (string asset in new[] { "Assets/Settings/Mobile_Renderer.asset", "Assets/Settings/PC_Renderer.asset" })
            {
                ScriptableRendererData renderer = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(asset);
                if (renderer == null) continue;
                bool onlyStudy = renderer.rendererFeatures.All(f => f is StudyEyeCaptureFeature);
                if (onlyStudy && renderer is UniversalRendererData universal)
                {
                    universal.intermediateTextureMode = IntermediateTextureMode.Auto;
                    EditorUtility.SetDirty(universal);
                }
                if (renderer.rendererFeatures.Any(f => f is StudyEyeCaptureFeature)) continue;
                var feature = ScriptableObject.CreateInstance<StudyEyeCaptureFeature>(); feature.name = "Study Eye Capture";
                AssetDatabase.AddObjectToAsset(feature, renderer); renderer.rendererFeatures.Add(feature);
                var serialized = new SerializedObject(renderer);
                SerializedProperty map = serialized.FindProperty("m_RendererFeatureMap");
                map.arraySize = renderer.rendererFeatures.Count;
                for (int i = 0; i < renderer.rendererFeatures.Count; i++)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(renderer.rendererFeatures[i], out string _, out long id);
                    map.GetArrayElementAtIndex(i).longValue = id;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo(); renderer.SetDirty(); EditorUtility.SetDirty(renderer);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[Study] Capture feature installed. It only enqueues on image requests; performance runs do not allocate capture buffers or force an intermediate target.");
        }

        [MenuItem("Tools/Study/Add Controller")]
        public static void AddController()
        {
            var go = new GameObject("Automated Study");
            StudyRunController controller = go.AddComponent<StudyRunController>();
            controller.measurementCamera = Camera.main;
            controller.viewOrigin = GameObject.Find("Camera position (faces +Z)")?.transform;
            controller.settings = AssetDatabase.LoadAssetAtPath<StudyRunSettings>("Assets/VRShaders/Study/Profiles/Performance.asset");
            if (Selection.activeGameObject != null) controller.targetRenderer = Selection.activeGameObject.GetComponent<Renderer>();
            Undo.RegisterCreatedObjectUndo(go, "Add Automated Study Controller"); Selection.activeGameObject = go;
        }
    }
}
