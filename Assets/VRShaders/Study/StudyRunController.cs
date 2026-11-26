using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Thesis.Glass.Reference;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using Debug = UnityEngine.Debug;

namespace Thesis.Glass.Study
{
    [DefaultExecutionOrder(10000)]
    public sealed class StudyRunController : MonoBehaviour
    {
#if UNITY_EDITOR
        public static string EditorOutputRootOverride;
#endif
        public StudyRunSettings settings;
        public Camera measurementCamera;
        public Transform motionRoot;
        public Transform viewOrigin;
        public Renderer targetRenderer;
        public GameObject[] referenceRoots;

        [Serializable] public sealed class TrialRecord
        {
            public int index, block, condition_index, captured_positions, valid_gpu_samples;
            public string material, path, directory, scene_package, status = "running";
            public string timing_source;
            public int gpu_query_attempts, gpu_query_successes;
            public int meta_gpu_query_successes, meta_gpu_positive_samples;
            public bool meta_performance_metrics_enabled;
            public bool baseline;
            public double gpu_median_ms, gpu_p95_ms;
            public ClockRecord measurement_start, measurement_end;
            public string measurement_status;
        }
        [Serializable] public sealed class RunRecord
        {
            public int schema_version = 1;
            public string status = "running", failure, mode, settings_json, unity_version, device, gpu, graphics_api, utc;
            public string capture_policy = "continuous motion for performance; held scheduled poses for image capture";
            public string pose_alignment = "URP render-pass matrices; headset row orientation and late-latch alignment require device validation";
            public bool headset, tracked_centre_compensated;
            public double frame_timing_cpu_frequency, stopwatch_frequency;
            public float display_refresh_hz, foveation_level, xr_viewport_scale;
            public string render_pipeline, quality_level;
            public List<TrialRecord> trials = new List<TrialRecord>();
            public ClockRecord clock_anchor;
            public string external_metrics_status;
            public string run_id, application_identifier;
        }
        [Serializable] public sealed class ClockRecord
        {
            public string utc;
            public double unity_realtime_seconds;
            public long stopwatch_ticks, android_elapsed_realtime_ms;
            public int frame;
        }
        [Serializable] sealed class Override { public string mode; }
        struct TimingSample { public FrameTiming timing; public int retrieved_frame; public double retrieved_seconds; public string source; public bool cpuAvailable; }
        struct PoseSample
        {
            public int frame; public double seconds; public long stopwatch; public float phase; public bool stereo;
            public Matrix4x4 leftView, leftProjection, rightView, rightProjection;
        }
        public bool IsRunning => running;
        public string OutputDirectory => directory;
        bool running, ownsState, moving, recordPoses;
        Coroutine routine;
        StudyRunSettings profile;
        RunRecord manifest;
        string directory, failure;
        TrialRecord trial;
        Material[] originalMaterials;
        bool originalEnabled, originalPost, originalDynamic;
        CameraOverrideOption originalOpaque;
        Vector3 originalRootPosition, centrePosition;
        Quaternion originalRootRotation, centreRotation;
        UniversalAdditionalCameraData cameraData;
        XRDisplaySubsystem display;
        float originalFoveation;
        StudyPath path;
        float phase;
        StudyEyeCaptureFeature.Request capture;
        readonly FrameTiming[] latest = new FrameTiming[1];
        readonly List<TimingSample> timings = new List<TimingSample>();
        readonly List<PoseSample> poses = new List<PoseSample>();
        ulong lastTiming;

        void Start() { if (settings != null && settings.startAutomatically) StartStudy(); }
        void OnEnable() => Application.onBeforeRender += BeforeRender;
        void OnDisable() { Application.onBeforeRender -= BeforeRender; StopStudy("Component disabled."); }
        void OnApplicationPause(bool paused) { if (paused) StopStudy("Application paused; trial interrupted."); }
        void OnApplicationQuit() => StopStudy("Application quitting before completion.");

        [ContextMenu("Start Automated Study")]
        public void StartStudy()
        {
            if (running || !Application.isPlaying) return;
            running = true; failure = null; manifest = null; directory = null; trial = null;
            routine = StartCoroutine(Guard(Run()));
        }
        [ContextMenu("Stop Automated Study")]
        public void StopStudy() => StopStudy("Stopped manually.");
        void StopStudy(string reason)
        {
            if (!running) return;
            failure = reason;
            if (routine != null) StopCoroutine(routine);
            Finish();
        }

        IEnumerator Guard(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(root);
            try
            {
                while (stack.Count > 0)
                {
                    object value = null; bool next = false;
                    try { next = stack.Peek().MoveNext(); if (next) value = stack.Peek().Current; }
                    catch (Exception e) { failure = e.ToString(); Debug.LogError($"[Study] {failure}"); break; }
                    if (!next) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                    if (value is IEnumerator child && !(value is CustomYieldInstruction)) stack.Push(child);
                    else yield return value;
                }
            }
            finally
            {
                while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                Finish();
            }
        }

        IEnumerator Run()
        {
            if (settings == null) throw new InvalidOperationException("Assign Study Run Settings.");
            profile = Instantiate(settings);
            string overrideFile = Path.Combine(Application.persistentDataPath, "study-run.json");
            if (File.Exists(overrideFile))
            {
                string mode = JsonUtility.FromJson<Override>(File.ReadAllText(overrideFile))?.mode;
                if (mode == "Performance") profile.mode = StudyRunMode.Performance;
                else if (mode == "ImageCapture") profile.mode = StudyRunMode.ImageCapture;
                else if (mode == "ExternalPerformance") profile.mode = StudyRunMode.ExternalPerformance;
                else throw new InvalidOperationException("study-run.json mode must be Performance, ImageCapture or ExternalPerformance.");
            }
            string invalid = profile.Validate(); if (invalid != null) throw new InvalidOperationException(invalid);
            if (measurementCamera == null) measurementCamera = Camera.main;
            if (measurementCamera == null || !measurementCamera.isActiveAndEnabled || !measurementCamera.TryGetComponent(out cameraData))
                throw new InvalidOperationException("Assign an enabled URP measurement camera.");
            if (motionRoot == null || motionRoot == measurementCamera.transform || !measurementCamera.transform.IsChildOf(motionRoot)
                || (motionRoot.lossyScale - Vector3.one).sqrMagnitude > 1e-6f)
                throw new InvalidOperationException("Motion Root must be a unit-scale parent of the tracked camera/rig, not the camera itself.");
            if (profile.compensateTrackedCentre && (viewOrigin == null || viewOrigin.IsChildOf(motionRoot)))
                throw new InvalidOperationException("Assign a fixed View Origin marker outside Motion Root for repeatable camera paths.");
            if (targetRenderer == null || !targetRenderer.gameObject.activeInHierarchy || targetRenderer.sharedMaterials.Length != 1 || targetRenderer.transform.IsChildOf(motionRoot))
                throw new InvalidOperationException("Assign one active, single-material glass renderer outside Motion Root.");
            string proxy = ProxyShape(profile.conditions[0]);
            foreach (Material material in profile.conditions)
                if (ProxyShape(material) != proxy) throw new InvalidOperationException("Use one proxy shape per condition set.");
            Material assigned = targetRenderer.sharedMaterial;
            if (assigned != null && assigned.shader.name == "Thesis/GlassTiered" && ProxyShape(assigned) != proxy)
                throw new InvalidOperationException($"Condition set uses {proxy}, but the target material uses {ProxyShape(assigned)}. Choose matching shape materials.");
            foreach (var legacy in FindObjectsByType<GlassMeasurementRunner>(FindObjectsSortMode.None))
                if (legacy.isActiveAndEnabled) throw new InvalidOperationException("Disable GlassMeasurementRunner while StudyRunController owns the experiment.");
            foreach (var exporter in FindObjectsByType<ReferenceSceneExporter>(FindObjectsSortMode.None))
                if (exporter.isActiveAndEnabled) throw new InvalidOperationException("Disable standalone ReferenceSceneExporter during the study; this controller owns reference export.");
            foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                if (renderer != targetRenderer && renderer.enabled && (measurementCamera.cullingMask & (1 << renderer.gameObject.layer)) != 0)
                    foreach (Material material in renderer.sharedMaterials)
                        if (material != null && material.shader.name == "Thesis/GlassTiered")
                            throw new InvalidOperationException($"Disable other glass objects for this single-target study: {renderer.name}");
            if (profile.mode == StudyRunMode.Performance && !FrameTimingManager.IsFeatureEnabled())
                throw new InvalidOperationException("Enable Player Settings > Frame Timing Stats.");
            if (profile.mode == StudyRunMode.ImageCapture && !profile.disablePostProcessing)
                throw new InvalidOperationException("Image mode requires Disable Post Processing for linear reference output.");
            if (cameraData.renderType != CameraRenderType.Base || cameraData.cameraStack.Count != 0)
                throw new InvalidOperationException("Use one base camera without camera stacking for the study.");
            if (profile.mode == StudyRunMode.ImageCapture && QualitySettings.activeColorSpace != ColorSpace.Linear)
                throw new InvalidOperationException("Image mode requires the project's Linear colour space.");
            yield return new WaitForSecondsRealtime(profile.startupDelaySeconds);
            double deadline = Time.realtimeSinceStartupAsDouble + profile.startupTimeoutSeconds;
            while (profile.requireHeadset && (!XRSettings.enabled || !measurementCamera.stereoEnabled))
            {
                if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("XR did not become ready before startup timeout.");
                yield return null;
            }
            var displays = new List<XRDisplaySubsystem>(); SubsystemManager.GetSubsystems(displays);
            display = displays.Find(d => d.running);
            originalMaterials = targetRenderer.sharedMaterials; originalEnabled = targetRenderer.enabled;
            originalRootPosition = motionRoot.localPosition; originalRootRotation = motionRoot.localRotation;
            originalPost = cameraData.renderPostProcessing; originalOpaque = cameraData.requiresColorOption;
            originalDynamic = measurementCamera.allowDynamicResolution;
            originalFoveation = display != null ? display.foveatedRenderingLevel : 0;
            ownsState = true;
            measurementCamera.allowDynamicResolution = false;
            if (profile.disablePostProcessing) cameraData.renderPostProcessing = false;
            if (display != null && profile.disableFoveation) display.foveatedRenderingLevel = 0;
            if (profile.mode == StudyRunMode.ImageCapture && display != null &&
                (display.foveatedRenderingLevel > 0 || Mathf.Abs(display.scaleOfAllViewports - 1) > 1e-4f))
                throw new InvalidOperationException("Image mode requires foveation off and full XR viewport scale.");
            centrePosition = viewOrigin != null ? viewOrigin.position : measurementCamera.transform.position;
            centreRotation = viewOrigin != null ? viewOrigin.rotation : measurementCamera.transform.rotation;
            string outputRoot = Application.persistentDataPath;
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(EditorOutputRootOverride)) outputRoot = EditorOutputRootOverride;
#endif
            directory = Path.Combine(outputRoot, "StudyRuns", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            manifest = new RunRecord { mode = profile.mode.ToString(), settings_json = JsonUtility.ToJson(profile, true), utc = DateTime.UtcNow.ToString("O"),
                unity_version = Application.unityVersion, device = SystemInfo.deviceModel, gpu = SystemInfo.graphicsDeviceName, graphics_api = SystemInfo.graphicsDeviceType.ToString(),
                headset = measurementCamera.stereoEnabled, tracked_centre_compensated = profile.compensateTrackedCentre,
                frame_timing_cpu_frequency = FrameTimingManager.GetCpuTimerFrequency(), stopwatch_frequency = Stopwatch.Frequency,
                render_pipeline = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none",
                quality_level = QualitySettings.names[QualitySettings.GetQualityLevel()], foveation_level = display != null ? display.foveatedRenderingLevel : 0,
                xr_viewport_scale = display != null ? display.scaleOfAllViewports : 1 };
            if (display != null && display.TryGetDisplayRefreshRate(out float refresh)) manifest.display_refresh_hz = refresh;
            manifest.clock_anchor = ReadClock();
            manifest.run_id = Path.GetFileName(directory);
            manifest.application_identifier = Application.identifier;
            if (profile.mode == StudyRunMode.ExternalPerformance)
                manifest.external_metrics_status = "Not imported. Complete means workload completed, not that OVR recording was verified.";
            SaveManifest();
            Debug.Log($"[Study] {profile.mode} started. Output: {directory}");
            for (int block = 0; block < profile.blocks; block++)
            {
                int count = profile.conditions.Length * profile.paths.Length;
                for (int order = 0; order < count; order++)
                {
                    int index = block % 2 == 0 ? order : count - 1 - order;
                    int condition = index / profile.paths.Length;
                    path = profile.paths[index % profile.paths.Length]; phase = 0; moving = true;
                    targetRenderer.enabled = true; targetRenderer.sharedMaterial = profile.conditions[condition];
                    if (profile.toggleOpaqueTexturePerCondition)
                        cameraData.requiresColorOption = targetRenderer.sharedMaterial.IsKeywordEnabled("_REFRACT_SSR") ? CameraOverrideOption.On : CameraOverrideOption.Off;
                    if (targetRenderer.sharedMaterial.IsKeywordEnabled("_REFRACT_SSR") && !cameraData.requiresColorTexture)
                        throw new InvalidOperationException("Screen-space condition needs the camera opaque texture.");
                    int variants = profile.mode == StudyRunMode.ImageCapture && profile.captureNoGlassBaseline ? 2 : 1;
                    for (int baseline = 0; baseline < variants; baseline++)
                    {
                        trial = new TrialRecord { index = manifest.trials.Count, block = block, condition_index = condition,
                            material = profile.conditions[condition].name, path = path.ToString(), baseline = baseline == 1 };
                        trial.directory = $"trial_{trial.index:D4}";
                        manifest.trials.Add(trial); SaveManifest();
                        targetRenderer.enabled = !trial.baseline;
                        for (int w = 0; w < profile.warmupFrames; w++) yield return null;
                        if (profile.mode == StudyRunMode.Performance) yield return Performance();
                        else if (profile.mode == StudyRunMode.ExternalPerformance) yield return ExternalPerformance();
                        else yield return Images();
                        trial.status = "complete"; SaveManifest();
                        moving = false; recordPoses = false; phase = 0;
                        targetRenderer.enabled = false;
                        yield return new WaitForSecondsRealtime(profile.cooldownSeconds);
                        moving = true;
                    }
                }
            }
        }

        IEnumerator Performance()
        {
            timings.Clear(); poses.Clear();
            bool xrTiming = display != null && display.running;
            trial.timing_source = xrTiming ? "OpenXR Provider XRStats perfmetrics (ms), fallback XRDisplaySubsystem GPU (seconds converted to ms); per-row source; once per Unity frame; native sample identity unavailable" : "FrameTimingManager milliseconds; unique native timestamps";
            trial.meta_performance_metrics_enabled = UnityEngine.XR.OpenXR.OpenXRRuntime.IsExtensionEnabled("XR_META_performance_metrics");
            int capacity = Mathf.CeilToInt(profile.pathDurationSeconds * profile.performanceCycles * 180) + 512;
            timings.Capacity = Mathf.Max(timings.Capacity, capacity); poses.Capacity = Mathf.Max(poses.Capacity, capacity);
            double start = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - start < profile.pathDurationSeconds)
            {
                phase = (float)((Time.realtimeSinceStartupAsDouble - start) / profile.pathDurationSeconds);
                FrameTimingManager.CaptureFrameTimings();
                yield return null;
                if (FrameTimingManager.GetLatestTimings(1, latest) > 0) lastTiming = Math.Max(lastTiming, latest[0].frameStartTimestamp);
            }
            recordPoses = true;
            start = Time.realtimeSinceStartupAsDouble;
            double duration = profile.pathDurationSeconds * profile.performanceCycles;
            while (Time.realtimeSinceStartupAsDouble - start < duration)
            {
                phase = (float)(((Time.realtimeSinceStartupAsDouble - start) / profile.pathDurationSeconds) % 1);
                FrameTimingManager.CaptureFrameTimings();
                if (xrTiming)
                {

                    trial.gpu_query_attempts++;
                    bool available = UnityEngine.XR.Provider.XRStats.TryGetStat(display, "perfmetrics.appgputime", out float gpuMs);
                    if (available) trial.meta_gpu_query_successes++;
                    if (available && gpuMs > 0 && !float.IsNaN(gpuMs) && !float.IsInfinity(gpuMs)) trial.meta_gpu_positive_samples++;
                    string source = "OpenXR.perfmetrics.appgputime_ms";
                    bool cpuAvailable = UnityEngine.XR.Provider.XRStats.TryGetStat(display, "perfmetrics.appcputime", out float cpuMs)
                        && cpuMs > 0 && !float.IsNaN(cpuMs) && !float.IsInfinity(cpuMs);
                    if (!available || gpuMs <= 0 || float.IsNaN(gpuMs) || float.IsInfinity(gpuMs))
                    {
                        available = display.TryGetAppGPUTimeLastFrame(out float gpuSeconds);
                        gpuMs = gpuSeconds * 1000;
                        source = "XRDisplaySubsystem.gpu_seconds_to_ms";
                    }
                    if (available) trial.gpu_query_successes++;
                    if (available && gpuMs > 0 && !float.IsNaN(gpuMs) && !float.IsInfinity(gpuMs))
                    {
                        timings.Add(new TimingSample { timing = new FrameTiming {
                            gpuFrameTime = gpuMs, cpuFrameTime = cpuAvailable ? cpuMs : 0
                        }, source = source, cpuAvailable = cpuAvailable,
                            retrieved_frame = Time.frameCount, retrieved_seconds = Time.realtimeSinceStartupAsDouble });
                    }
                }
                else if (FrameTimingManager.GetLatestTimings(1, latest) > 0)
                {
                    FrameTiming timing = latest[0];
                    if (timing.frameStartTimestamp > lastTiming)
                    {
                        lastTiming = timing.frameStartTimestamp;
                        if (timing.gpuFrameTime > 0 && !double.IsNaN(timing.gpuFrameTime) && !double.IsInfinity(timing.gpuFrameTime)
                            && timing.cpuFrameTime > 0 && !double.IsNaN(timing.cpuFrameTime) && !double.IsInfinity(timing.cpuFrameTime))
                            timings.Add(new TimingSample { timing = timing, retrieved_frame = Time.frameCount, retrieved_seconds = Time.realtimeSinceStartupAsDouble });
                    }
                }
                yield return null;
            }
            recordPoses = false;
            string folder = Path.Combine(directory, trial.directory); Directory.CreateDirectory(folder);
            var csv = new StringBuilder("frame_start_timestamp,retrieved_frame,retrieved_seconds,gpu_ms,cpu_ms,timing_source\n");
            foreach (TimingSample sample in timings)
                csv.Append(xrTiming ? "" : sample.timing.frameStartTimestamp.ToString(CultureInfo.InvariantCulture)).Append(',').Append(sample.retrieved_frame).Append(',').Append(Number(sample.retrieved_seconds)).Append(',')
                    .Append(Number(sample.timing.gpuFrameTime)).Append(',').Append(xrTiming && !sample.cpuAvailable ? "" : Number(sample.timing.cpuFrameTime))
                    .Append(',').Append(xrTiming ? sample.source : "FrameTimingManager").AppendLine();
            File.WriteAllText(Path.Combine(folder, "performance.csv"), csv.ToString());
            csv.Clear(); csv.Append("frame,realtime_seconds,stopwatch_ticks,path_phase,stereo");
            foreach (string name in new[] { "left_view", "left_projection", "right_view", "right_projection" })
                for (int i = 0; i < 16; i++) csv.Append(',').Append(name).Append('_').Append(i);
            csv.AppendLine();
            foreach (PoseSample pose in poses)
            {
                csv.Append(pose.frame).Append(',').Append(Number(pose.seconds)).Append(',').Append(pose.stopwatch).Append(',').Append(Number(pose.phase)).Append(',').Append(pose.stereo ? 1 : 0);
                AppendMatrix(csv, pose.leftView); AppendMatrix(csv, pose.leftProjection); AppendMatrix(csv, pose.rightView); AppendMatrix(csv, pose.rightProjection); csv.AppendLine();
            }
            File.WriteAllText(Path.Combine(folder, "poses.csv"), csv.ToString());
            trial.valid_gpu_samples = timings.Count;
            if (timings.Count > 0)
            {
                double[] gpu = new double[timings.Count]; for (int i = 0; i < gpu.Length; i++) gpu[i] = timings[i].timing.gpuFrameTime;
                Array.Sort(gpu); trial.gpu_median_ms = (gpu[(gpu.Length - 1) / 2] + gpu[gpu.Length / 2]) / 2;
                trial.gpu_p95_ms = gpu[Mathf.Clamp(Mathf.CeilToInt(gpu.Length * 0.95f) - 1, 0, gpu.Length - 1)];
            }
            if (timings.Count < profile.minimumPerformanceSamples)
                throw new InvalidOperationException($"Only {timings.Count}/{profile.minimumPerformanceSamples} required positive GPU timing observations from {trial.timing_source}. Zero samples indicate unavailable timing, not a short trial. Validate against headset metrics before using results.");
        }

        IEnumerator ExternalPerformance()
        {
            trial.timing_source = "External OVR recording required; no internal GPU samples";
            trial.measurement_status = "warming_up";
            poses.Clear();
            poses.Capacity = Mathf.Max(poses.Capacity,
                Mathf.CeilToInt(profile.pathDurationSeconds * profile.performanceCycles * 180) + 512);
            double start = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - start < profile.pathDurationSeconds)
            {
                phase = (float)((Time.realtimeSinceStartupAsDouble - start) / profile.pathDurationSeconds);
                yield return null;
            }
            trial.measurement_status = "measuring";
            SaveManifest();
            trial.measurement_start = ReadClock();
            Debug.Log($"[StudyWindow] START run={manifest.run_id} trial={trial.index} material={trial.material} path={trial.path} utc={trial.measurement_start.utc} android_ms={trial.measurement_start.android_elapsed_realtime_ms}");
            SaveManifest();
            recordPoses = true;
            start = Time.realtimeSinceStartupAsDouble;
            double duration = profile.pathDurationSeconds * profile.performanceCycles;
            while (Time.realtimeSinceStartupAsDouble - start < duration)
            {
                phase = (float)(((Time.realtimeSinceStartupAsDouble - start) / profile.pathDurationSeconds) % 1);
                yield return null;
            }
            recordPoses = false;
            trial.measurement_end = ReadClock();
            trial.measurement_status = "workload_complete_external_data_unverified";
            Debug.Log($"[StudyWindow] END run={manifest.run_id} trial={trial.index} utc={trial.measurement_end.utc} android_ms={trial.measurement_end.android_elapsed_realtime_ms}");
            string folder = Path.Combine(directory, trial.directory);
            Directory.CreateDirectory(folder);
            var csv = new StringBuilder("frame,realtime_seconds,stopwatch_ticks,path_phase\n");
            foreach (PoseSample pose in poses)
                csv.Append(pose.frame).Append(',').Append(Number(pose.seconds)).Append(',').Append(pose.stopwatch)
                    .Append(',').Append(Number(pose.phase)).AppendLine();
            File.WriteAllText(Path.Combine(folder, "poses.csv"), csv.ToString());
            SaveManifest();
        }

        static ClockRecord ReadClock()
        {
            long androidMs = -1;
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var clock = new AndroidJavaClass("android.os.SystemClock"))
                androidMs = clock.CallStatic<long>("elapsedRealtime");
#endif
            return new ClockRecord { utc = DateTime.UtcNow.ToString("O"),
                unity_realtime_seconds = Time.realtimeSinceStartupAsDouble,
                stopwatch_ticks = Stopwatch.GetTimestamp(), android_elapsed_realtime_ms = androidMs,
                frame = Time.frameCount };
        }

        IEnumerator Images()
        {
            string folder = Path.Combine(directory, trial.directory); Directory.CreateDirectory(folder);
            phase = 0; ApplyMotion();
            ReferenceSceneExporter.WritePackage(measurementCamera, referenceRoots, Path.Combine(folder, "scene"), measurementCamera.stereoEnabled);
            trial.scene_package = trial.directory + "/scene/scene.json"; SaveManifest();
            for (int i = 0; i < profile.imagePositions; i++)
            {
                phase = i / (float)(profile.imagePositions - 1);
                for (int w = 0; w < profile.imageSettleFrames; w++) yield return null;
                var metadata = new StudyEyeCaptureFeature.CaptureRecord { block = trial.block, condition_index = trial.condition_index,
                    sample_index = i, phase = phase, material = trial.material, path = trial.path, baseline = trial.baseline,
                    scene_package = trial.scene_package, rows_flipped = profile.flipReadbackRows };
                capture = StudyEyeCaptureFeature.Schedule(measurementCamera, Path.Combine(folder, "captures"), metadata);
                double timeout = Time.realtimeSinceStartupAsDouble + profile.captureTimeoutSeconds;
                while (!capture.completed)
                {
                    if (Time.realtimeSinceStartupAsDouble > timeout) throw new TimeoutException("Eye capture timed out. Check StudyEyeCaptureFeature is installed on the active URP renderer and RenderGraph is enabled.");
                    yield return null;
                }
                if (capture.error != null) throw new IOException(capture.error);
                capture = null; trial.captured_positions++; SaveManifest();
            }
        }

        void LateUpdate() { if (moving) ApplyMotion(); }
        [BeforeRenderOrder(10000)]
        void BeforeRender()
        {
            if (!running || !moving) return;
            ApplyMotion();
            if (!recordPoses) return;
            bool stereo = measurementCamera.stereoEnabled;
            poses.Add(new PoseSample { frame = Time.frameCount, seconds = Time.realtimeSinceStartupAsDouble, stopwatch = Stopwatch.GetTimestamp(), phase = phase, stereo = stereo,
                leftView = stereo ? measurementCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Left) : measurementCamera.worldToCameraMatrix,
                leftProjection = stereo ? measurementCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) : measurementCamera.projectionMatrix,
                rightView = stereo ? measurementCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Right) : measurementCamera.worldToCameraMatrix,
                rightProjection = stereo ? measurementCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) : measurementCamera.projectionMatrix });
        }
        void ApplyMotion()
        {
            profile.Evaluate(path, phase, out Vector3 translation, out Quaternion rotation);
            if (profile.compensateTrackedCentre)
            {
                Vector3 relativePosition = motionRoot.InverseTransformPoint(measurementCamera.transform.position);
                Quaternion relativeRotation = Quaternion.Inverse(motionRoot.rotation) * measurementCamera.transform.rotation;
                motionRoot.rotation = centreRotation * rotation * Quaternion.Inverse(relativeRotation);
                motionRoot.position = centrePosition + centreRotation * translation - motionRoot.TransformVector(relativePosition);
            }
            else
            {
                motionRoot.localPosition = originalRootPosition + originalRootRotation * translation;
                motionRoot.localRotation = originalRootRotation * rotation;
            }
        }
        void SaveManifest()
        {
            File.WriteAllText(Path.Combine(directory, "run.json"), JsonUtility.ToJson(manifest, true));
            if (profile == null || profile.mode != StudyRunMode.ExternalPerformance) return;
            var csv = new StringBuilder("trial,block,condition_index,material,path,status,measurement_status,start_utc,end_utc,start_unity_seconds,end_unity_seconds,start_android_ms,end_android_ms\n");
            foreach (TrialRecord t in manifest.trials)
            {
                csv.Append(t.directory).Append(',').Append(t.block).Append(',').Append(t.condition_index).Append(',')
                    .Append(Csv(t.material)).Append(',').Append(t.path).Append(',').Append(t.status).Append(',').Append(t.measurement_status).Append(',')
                    .Append(t.measurement_start?.utc).Append(',').Append(t.measurement_end?.utc).Append(',')
                    .Append(t.measurement_start == null ? "" : Number(t.measurement_start.unity_realtime_seconds)).Append(',')
                    .Append(t.measurement_end == null ? "" : Number(t.measurement_end.unity_realtime_seconds)).Append(',')
                    .Append(t.measurement_start == null ? "" : t.measurement_start.android_elapsed_realtime_ms.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.measurement_end == null ? "" : t.measurement_end.android_elapsed_realtime_ms.ToString(CultureInfo.InvariantCulture)).AppendLine();
            }
            File.WriteAllText(Path.Combine(directory, "trial_windows.csv"), csv.ToString());
        }
        static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        void Finish()
        {
            if (!running) return;
            running = false; moving = false; recordPoses = false;
            StudyEyeCaptureFeature.Cancel(capture); capture = null;
            if (manifest != null)
            {
                manifest.status = failure == null ? "complete" : "aborted"; manifest.failure = failure;
                if (trial != null && trial.status != "complete") trial.status = "aborted";
                if (trial != null && trial.status == "aborted" && profile != null && profile.mode == StudyRunMode.ExternalPerformance)
                    trial.measurement_status = "aborted_exclude_from_analysis";
                try { SaveManifest(); } catch (Exception e) { Debug.LogError($"[Study] Cannot save run status: {e.Message}"); }
            }
            if (ownsState)
            {
                if (targetRenderer != null) { targetRenderer.sharedMaterials = originalMaterials; targetRenderer.enabled = originalEnabled; }
                if (motionRoot != null) { motionRoot.localPosition = originalRootPosition; motionRoot.localRotation = originalRootRotation; }
                if (cameraData != null) { cameraData.renderPostProcessing = originalPost; cameraData.requiresColorOption = originalOpaque; }
                if (measurementCamera != null) measurementCamera.allowDynamicResolution = originalDynamic;
                if (display != null && display.running) display.foveatedRenderingLevel = originalFoveation;
                ownsState = false;
            }
            if (profile != null) { if (Application.isPlaying) Destroy(profile); else DestroyImmediate(profile); }
            Debug.Log($"[Study] {(failure == null ? "Complete" : "Aborted: " + failure)}. Output: {directory}");
        }
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string ProxyShape(Material material) => material.IsKeywordEnabled("_PROXY_LENS") ? "lens" : material.IsKeywordEnabled("_PROXY_PRISM") ? "prism" : "sphere";
        static void AppendMatrix(StringBuilder builder, Matrix4x4 matrix)
        { for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) builder.Append(',').Append(Number(matrix[r, c])); }
    }
}
