using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Thesis.Glass.Study.EditorTools
{
    public static class StudyControllerValidation
    {
        static IEnumerator operation;
        static string resultFile;
        [MenuItem("Tools/Study/Validate Automated Controller")]
        public static void Run()
        {
            if (operation != null) throw new InvalidOperationException("Controller validation is already running.");
            resultFile = Path.GetFullPath("Temp/StudyControllerValidation.result");
            operation = Validate(); EditorApplication.update += Tick;
        }
        static void Tick()
        {
            try
            {
                if (operation.MoveNext()) return;
                File.WriteAllText(resultFile, "PASS"); Debug.Log("[StudyControllerValidation] PASS.");
            }
            catch (Exception e) { File.WriteAllText(resultFile, e.ToString()); Debug.LogException(e); }
            (operation as IDisposable)?.Dispose(); operation = null; EditorApplication.update -= Tick;
        }
        static IEnumerator Validate()
        {
            Scene original = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject("Study controller fixture");
            var rig = new GameObject("Motion root"); rig.transform.SetParent(root.transform);
            var cameraObject = new GameObject("Study test camera"); cameraObject.transform.SetParent(rig.transform);
            Camera camera = cameraObject.AddComponent<Camera>(); camera.transform.localPosition = new Vector3(0, 0, -3);
            camera.stereoTargetEye = StereoTargetEyeMask.None; camera.cullingMask = 1 << 31;
            cameraObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            var rt = new RenderTexture(32, 32, 24, RenderTextureFormat.ARGBHalf); camera.targetTexture = rt;
            var target = GameObject.CreatePrimitive(PrimitiveType.Sphere); target.layer = 31; target.transform.SetParent(root.transform);
            Material material = new Material(Shader.Find("Thesis/GlassTiered")); material.SetFloat("_Dispersion", 0);
            target.GetComponent<Renderer>().sharedMaterial = material;
            var panel = GameObject.CreatePrimitive(PrimitiveType.Quad); panel.layer = 31; panel.transform.SetParent(root.transform);
            panel.transform.position = new Vector3(0, 0, 1); panel.transform.localScale = Vector3.one * 3;
            Material white = new Material(Shader.Find("Universal Render Pipeline/Unlit")); white.SetColor("_BaseColor", Color.white);
            panel.GetComponent<Renderer>().sharedMaterial = white;
            StudyRunSettings profile = ScriptableObject.CreateInstance<StudyRunSettings>();
            profile.mode = StudyRunMode.ImageCapture; profile.requireHeadset = false; profile.startAutomatically = false;
            profile.startupDelaySeconds = 0; profile.warmupFrames = 8; profile.cooldownSeconds = 0;
            profile.conditions = new[] { material }; profile.imagePositions = 5; profile.imageSettleFrames = 2;
            profile.paths = new[] { StudyPath.Static, StudyPath.Lateral, StudyPath.Yaw }; profile.captureNoGlassBaseline = true;
            StudyRunController controller = root.AddComponent<StudyRunController>();
            controller.settings = profile; controller.measurementCamera = camera; controller.motionRoot = rig.transform;
            var anchor = new GameObject("View Origin"); anchor.transform.SetParent(root.transform); anchor.transform.position = camera.transform.position;
            controller.viewOrigin = anchor.transform;
            controller.targetRenderer = target.GetComponent<Renderer>(); controller.referenceRoots = new[] { root };
            IEnumerator guard = null;
            typeof(StudyRunController).GetField("EditorOutputRootOverride").SetValue(null, Path.GetFullPath("Temp/StudyControllerFixture"));
            try
            {

                for (int i = 0; i < 3; i++) yield return null;
                for (int pass = 0; pass < 4; pass++)
                {
                    if (pass >= 2)
                    {
                        profile.mode = StudyRunMode.ExternalPerformance;
                        profile.pathDurationSeconds = 0.1f; profile.performanceCycles = 1; profile.blocks = 2;
                    }
                    Set(controller, "running", true); Set(controller, "failure", null); Set(controller, "manifest", null); Set(controller, "trial", null); Set(controller, "directory", null);
                    IEnumerator run = (IEnumerator)Call(controller, "Run");
                    guard = (IEnumerator)Call(controller, "Guard", run);
                    bool done = false; CustomYieldInstruction wait = null;
                    for (int frame = 0; frame < 3000 && !done; frame++)
                    {
                        if (wait == null || !wait.keepWaiting)
                        {
                            done = !guard.MoveNext(); wait = done ? null : guard.Current as CustomYieldInstruction;
                        }
                        if (!done)
                        {
                            Call(controller, "BeforeRender");
                            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                            AsyncGPUReadback.WaitAllRequests();
                            if (pass == 1 && File.Exists(Path.Combine(controller.OutputDirectory ?? "", "run.json")))
                            {
                                controller.StopStudy(); (guard as IDisposable)?.Dispose(); done = true;
                            }
                            if (pass == 3 && GetTrial(controller)?.measurement_status == "measuring")
                            { controller.StopStudy(); (guard as IDisposable)?.Dispose(); done = true; }
                        }
                        yield return null;
                    }
                    if (!done) throw new TimeoutException("Controller integration test exceeded 3000 updates.");
                    string output = controller.OutputDirectory;
                    if (output == null) throw new InvalidOperationException("Controller did not create output.");
                    var runRecord = JsonUtility.FromJson<StudyRunController.RunRecord>(File.ReadAllText(Path.Combine(output, "run.json")));
                    if (pass == 0)
                    {
                        if (runRecord.status != "complete" || runRecord.trials.Count != 6) throw new InvalidOperationException("Controller did not complete six image trials: " + runRecord.failure);
                        foreach (var trial in runRecord.trials)
                        {
                            if (trial.captured_positions != 5 || trial.status != "complete") throw new InvalidOperationException("Missing scheduled captures.");
                            var quarter = JsonUtility.FromJson<StudyEyeCaptureFeature.CaptureRecord>(File.ReadAllText(Path.Combine(output, trial.directory, "captures/frame_0001.json")));
                            if (trial.path == "Lateral" && Mathf.Abs(quarter.cameras[0].view[3] + profile.translationMetres) > 1e-4f)
                                throw new InvalidOperationException("Captured lateral camera does not follow the scheduled phase.");
                            if (trial.path == "Yaw" && Mathf.Abs(quarter.cameras[0].view[0] - Mathf.Cos(profile.yawDegrees * Mathf.Deg2Rad)) > 1e-4f)
                                throw new InvalidOperationException("Captured yaw camera does not follow the scheduled phase.");
                        }
                        File.WriteAllText("Temp/StudyControllerRunDirectory.txt", output);
                    }
                    else if (pass == 2)
                    {
                        if (runRecord.status != "complete" || runRecord.trials.Count != 6)
                            throw new InvalidOperationException("External mode failed full sequence: " + runRecord.failure);
                        foreach (var t in runRecord.trials)
                        {
                            if (t.measurement_start == null || t.measurement_end == null ||
                                t.measurement_end.unity_realtime_seconds <= t.measurement_start.unity_realtime_seconds ||
                                t.gpu_query_attempts != 0 || t.valid_gpu_samples != 0 || t.captured_positions != 0 ||
                                File.Exists(Path.Combine(output, t.directory, "performance.csv")))
                                throw new InvalidOperationException("External mode queried GPU timing, captured images or lost a measurement window.");
                        }
                        if (runRecord.trials[0].path != runRecord.trials[5].path || !File.Exists(Path.Combine(output, "trial_windows.csv")))
                            throw new InvalidOperationException("External counterbalancing/windows export failed.");
                    }
                    else if (runRecord.status != "aborted") throw new InvalidOperationException("Stopped run was not marked aborted.");
                    if ((rig.transform.localPosition - Vector3.zero).sqrMagnitude > 1e-8f || Quaternion.Angle(rig.transform.localRotation, Quaternion.identity) > 0.001f
                        || controller.targetRenderer.sharedMaterial != material || !controller.targetRenderer.enabled)
                        throw new InvalidOperationException("Controller did not restore scene state.");
                }
            }
            finally
            {
                typeof(StudyRunController).GetField("EditorOutputRootOverride").SetValue(null, null);
                controller.StopStudy(); (guard as IDisposable)?.Dispose();
                EditorSceneManager.CloseScene(scene, true);
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                foreach (UnityEngine.Object value in new UnityEngine.Object[] { material, white, profile, rt })
                    if (value != null) UnityEngine.Object.DestroyImmediate(value);
            }
        }
        static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        static StudyRunController.TrialRecord GetTrial(StudyRunController controller) =>
            (StudyRunController.TrialRecord)typeof(StudyRunController).GetField("trial", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
        static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }
}
