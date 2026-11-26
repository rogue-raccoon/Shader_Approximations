using System;
using UnityEditor;
using UnityEngine;

namespace Thesis.Glass.Reference.EditorTools
{
    public sealed class ReferenceExportWindow : EditorWindow
    {
        [SerializeField] Camera referenceCamera;
        [SerializeField] bool selectedRootsOnly;
        [MenuItem("Tools/Export Blender Reference Package")]
        public static void Open() => GetWindow<ReferenceExportWindow>("Reference Export");
        void OnGUI()
        {
            referenceCamera = (Camera)EditorGUILayout.ObjectField("Reference camera", referenceCamera, typeof(Camera), true);
            selectedRootsOnly = EditorGUILayout.Toggle("Selected hierarchies only", selectedRootsOnly);
            EditorGUILayout.HelpBox("Exports enabled meshes and lights from the loaded scenes, filtered by camera layers. Select the complete enclosure, targets and glass if limiting to selected hierarchies. The scene is not modified. For actual XR eye matrices, use ReferenceSceneExporter on the running headset; this editor export is monoscopic.", MessageType.Info);
            if (GUILayout.Button("Export Reference Package"))
            {
                Camera camera = referenceCamera != null ? referenceCamera : Camera.main;
                GameObject[] roots = selectedRootsOnly ? Selection.gameObjects : null;
                if (selectedRootsOnly && roots.Length == 0) { Debug.LogError("[ReferenceExport] Select at least one complete hierarchy."); return; }
                string directory = EditorUtility.OpenFolderPanel("Choose a new or empty reference folder", "", "");
                if (string.IsNullOrEmpty(directory)) return;
                try
                {
                    ReferenceSceneExporter.WritePackage(camera, roots, directory, false);
                    Debug.Log($"[ReferenceExport] Saved {directory}/scene.json. Read ExportNotes.txt before rendering comparisons.");
                }
                catch (Exception e) { Debug.LogError($"[ReferenceExport] {e}"); }
            }
        }
    }
}
