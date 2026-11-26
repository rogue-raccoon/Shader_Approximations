using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Thesis.Glass.Reference;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Thesis.Glass.Study.EditorTools
{
    public static class StudyValidation
    {
        static IEnumerator operation;
        static string output;
        static bool batch;
        public static void RunBatch() { batch = true; Run(); }
        [MenuItem("Tools/Study/Validate Motion and Eye Capture")]
        public static void Run()
        {
            if (operation != null) throw new InvalidOperationException("Validation is already running.");
            output = Path.GetFullPath("Temp/StudyValidation_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output); File.WriteAllText("Temp/StudyValidationDirectory.txt", output);
            operation = Validate(); EditorApplication.update += Tick;
        }
        static void Tick()
        {
            bool passed = false;
            try
            {
                if (operation.MoveNext()) return;
                File.WriteAllText(Path.Combine(output, "result.txt"), "PASS");
                Debug.Log($"[StudyValidation] PASS. Output: {output}");
                passed = true;
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(output, "result.txt"), e.ToString()); Debug.LogException(e); }
            (operation as IDisposable)?.Dispose(); operation = null; EditorApplication.update -= Tick;
            if (batch) EditorApplication.Exit(passed ? 0 : 1);
        }
        static IEnumerator Validate()
        {
            Scene originalScene = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            RenderPipelineAsset originalPipeline = QualitySettings.renderPipeline;
            var pipeline = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset"));
            var renderer = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset"));
            var feature = ScriptableObject.CreateInstance<StudyEyeCaptureFeature>();
            var profile = ScriptableObject.CreateInstance<StudyRunSettings>();
            var cameraObject = new GameObject("Study validation camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false; camera.orthographic = true; camera.orthographicSize = 1;
            camera.transform.position = new Vector3(0, 0, -2); camera.cullingMask = 1 << 31;
            camera.stereoTargetEye = StereoTargetEyeMask.None; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            cameraObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            RenderTexture target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBHalf);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Material material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            StudyEyeCaptureFeature.Request request = null;
            try
            {
                foreach (StudyPath path in Enum.GetValues(typeof(StudyPath)))
                {
                    profile.Evaluate(path, 0, out Vector3 start, out Quaternion startRotation);
                    profile.Evaluate(path, 1, out Vector3 end, out Quaternion endRotation);
                    if (start.sqrMagnitude > 1e-10f || end.sqrMagnitude > 1e-10f || Quaternion.Angle(startRotation, endRotation) > 0.001f)
                        throw new InvalidOperationException("Path is not closed at its neutral pose.");
                    profile.Evaluate(path, 0.25f, out Vector3 quarter, out Quaternion quarterRotation);
                    if (path == StudyPath.Lateral && Mathf.Abs(quarter.x - profile.translationMetres) > 1e-6f)
                        throw new InvalidOperationException("Lateral path amplitude differs.");
                    if (path == StudyPath.Yaw && Mathf.Abs(Quaternion.Angle(Quaternion.identity, quarterRotation) - profile.yawDegrees) > 0.001f)
                        throw new InvalidOperationException("Yaw path amplitude differs.");
                }
                renderer.rendererFeatures.Clear(); renderer.rendererFeatures.Add(feature); feature.Create(); renderer.SetDirty();
                var serialized = new SerializedObject(pipeline);
                var data = serialized.FindProperty("m_RendererDataList"); data.arraySize = 1; data.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
                serialized.FindProperty("m_DefaultRendererIndex").intValue = 0; serialized.ApplyModifiedPropertiesWithoutUndo();
                QualitySettings.renderPipeline = pipeline;
                texture.filterMode = FilterMode.Point;
                texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white }); texture.Apply();
                material.SetTexture("_BaseMap", texture); material.SetColor("_BaseColor", Color.white);
                GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Quad); panel.layer = 31;
                panel.transform.localScale = Vector3.one * 2; panel.GetComponent<Renderer>().sharedMaterial = material;
                for (int i = 0; i < 4; i++) yield return null;
                request = StudyEyeCaptureFeature.Schedule(camera, Path.Combine(output, "mono"), new StudyEyeCaptureFeature.CaptureRecord { sample_index = 0 });
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                AsyncGPUReadback.WaitAllRequests();
                for (int i = 0; i < 120 && !request.completed; i++) yield return null;

                if (!request.completed || request.error != null) throw new InvalidOperationException("Mono render-graph capture failed: " + request.error);
                var desc = new RenderTextureDescriptor(8, 8) { graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    dimension = TextureDimension.Tex2DArray, volumeDepth = 2, msaaSamples = 1, depthStencilFormat = GraphicsFormat.None };
                var array = new RenderTexture(desc); array.Create(); RTHandle owned = RTHandles.Alloc(array, true);
                var command = new CommandBuffer();
                command.SetRenderTarget(array, 0, CubemapFace.Unknown, 0); command.ClearRenderTarget(false, true, Color.red);
                command.SetRenderTarget(array, 0, CubemapFace.Unknown, 1); command.ClearRenderTarget(false, true, Color.green);
                Graphics.ExecuteCommandBuffer(command); command.Release();

                var copyDesc = desc; copyDesc.enableRandomWrite = true;
                var copied = new RenderTexture(copyDesc); copied.Create();
                var copyShader = Resources.Load<ComputeShader>("StudyEyeCopy");
                if (copyShader == null) throw new InvalidOperationException("Missing StudyEyeCopy resource.");
                int kernel = copyShader.FindKernel("CopyArray");
                command = new CommandBuffer();
                command.SetComputeIntParam(copyShader, "_Width", 8);
                command.SetComputeIntParam(copyShader, "_Height", 8);
                command.SetComputeIntParam(copyShader, "_Slices", 2);
                command.SetComputeTextureParam(copyShader, kernel, "_SourceArray", array);
                command.SetComputeTextureParam(copyShader, kernel, "_DestinationArray", copied);
                command.DispatchCompute(copyShader, kernel, 1, 1, 2);
                Graphics.ExecuteCommandBuffer(command); command.Release();
                var check = AsyncGPUReadback.Request(copied, 0, TextureFormat.RGBAFloat);
                check.WaitForCompletion();
                if (check.hasError || check.GetData<Color>(0)[0].r < 0.99f || check.GetData<Color>(1)[0].g < 0.99f
                    || check.GetData<Color>(1)[0].r > 0.01f)
                    throw new InvalidOperationException("Compute array copy did not preserve red/green source layers.");
                owned.Release(); UnityEngine.Object.DestroyImmediate(copied);

                array = new RenderTexture(desc); array.Create(); owned = RTHandles.Alloc(array, true);
                command = new CommandBuffer();
                command.SetRenderTarget(array, 0, CubemapFace.Unknown, 0); command.ClearRenderTarget(false, true, Color.red);
                command.SetRenderTarget(array, 0, CubemapFace.Unknown, 1); command.ClearRenderTarget(false, true, Color.green);
                Graphics.ExecuteCommandBuffer(command); command.Release();
                var record = new StudyEyeCaptureFeature.CaptureRecord { sample_index = 0,
                    images = new[] { "left.exr", "right.exr" }, cameras = new ReferenceSceneExporter.CameraRecord[0] };
                request = StudyEyeCaptureFeature.Schedule(camera, Path.Combine(output, "array"), record);
                Type type = typeof(StudyEyeCaptureFeature).GetNestedType("ReadbackData", BindingFlags.NonPublic);
                object payload = Activator.CreateInstance(type, true);
                foreach (var pair in new[] { new object[] { "owned", owned }, new object[] { "request", request },
                    new object[] { "width", 8 }, new object[] { "height", 8 }, new object[] { "slices", new[] { 0, 1 } },
                    new object[] { "crops", new[] { new RectInt(0, 0, 8, 8), new RectInt(0, 0, 8, 8) } } })
                    type.GetField((string)pair[0]).SetValue(payload, pair[1]);
                AsyncGPUReadback.Request(array, 0, TextureFormat.RGBAHalf, r => typeof(StudyEyeCaptureFeature)
                    .GetMethod("Save", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { payload, (object)r }));
                AsyncGPUReadback.WaitAllRequests();
                for (int i = 0; i < 120 && !request.completed; i++) yield return null;
                if (!request.completed || request.error != null) throw new InvalidOperationException("Array capture failed: " + request.error);
                File.WriteAllText(Path.Combine(output, "checks.txt"), "Closed paths and amplitude checks passed.\nActual mono URP RenderGraph capture passed.\nTwo-layer GPU readback and EXR encoding passed.\nInspect mono EXR orientation and array colours with validate_study_capture.py.\n");
            }
            finally
            {
                StudyEyeCaptureFeature.Cancel(request); QualitySettings.renderPipeline = originalPipeline;
                EditorSceneManager.CloseScene(scene, true);
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                foreach (UnityEngine.Object value in new UnityEngine.Object[] { pipeline, renderer, feature, profile, target, texture, material })
                    if (value != null) UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
}
