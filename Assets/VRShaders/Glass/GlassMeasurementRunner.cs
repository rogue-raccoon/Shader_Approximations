using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Thesis.Glass
{
    public class GlassMeasurementRunner : MonoBehaviour
    {
        [SerializeField] Material[] conditions;
        [SerializeField] Renderer targetRenderer;
        [SerializeField] Camera measurementCamera;
        [SerializeField] bool toggleOpaqueTexturePerCondition = true;
        [SerializeField] float[] coverageScales = { 0.35f, 0.7f, 1.2f };
        [SerializeField] string[] coverageLabels = { "low", "mid", "high" };
        [SerializeField, Min(8)] int warmupFrames = 120;
        [SerializeField, Min(1)] int captureFrames = 1000;
        [SerializeField, Min(0)] float cooldownSeconds = 60f;
        [SerializeField, Min(1)] int blocks = 3;
        [SerializeField] string fileNamePrefix = "glass_gpu";

        readonly FrameTiming[] _timings = new FrameTiming[1];
        FrameTiming[] _samples;
        int _sampleCount, _block;
        string _condition, _coverage, _path;
        bool _running, _opaqueEnabled;
        ulong _lastTimestamp;
        Material[] _originalMaterials;
        Vector3 _originalScale;
        bool _originalEnabled;
        UniversalAdditionalCameraData _cameraData;
        CameraOverrideOption _originalColorOption;

        IEnumerator Start()
        {
            if (!ValidateConfiguration()) yield break;
            _originalMaterials = targetRenderer.sharedMaterials;
            _originalScale = targetRenderer.transform.localScale;
            _originalEnabled = targetRenderer.enabled;
            _originalColorOption = _cameraData.requiresColorOption;
            _samples = new FrameTiming[captureFrames];
            _path = Path.Combine(Application.persistentDataPath,
                $"{fileNamePrefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.csv");
            try
            {
                File.WriteAllText(_path, "block,condition,coverage,sample,frame_start_timestamp,gpu_ms,cpu_ms,width_scale,height_scale,opaque_texture\n");
                File.WriteAllText(Path.ChangeExtension(_path, ".txt"),
                    $"UTC: {DateTime.UtcNow:O}\nUnity: {Application.unityVersion}\nDevice: {SystemInfo.deviceModel}\n" +
                    $"GPU: {SystemInfo.graphicsDeviceName}\nAPI: {SystemInfo.graphicsDeviceType}\n" +
                    $"Quality: {QualitySettings.names[QualitySettings.GetQualityLevel()]}\nOpaque copy toggled: {toggleOpaqueTexturePerCondition}\n" +
                    $"Warmup: {warmupFrames}\nSamples: {captureFrames}\n" +
                    "Coverage is calibrated externally. Record thermal state and XR refresh/foveation settings separately.\n");
            }
            catch (Exception e)
            {
                Debug.LogError($"[GlassRunner] Cannot create output: {e.Message}");
                yield break;
            }
            _running = true;
            targetRenderer.enabled = true;
            foreach (Material m in conditions)
            {
                ApplyCondition(m);
                for (int i = 0; i < 8; i++) yield return null;
            }
            for (int b = 0; b < blocks && _running; b++)
            {
                foreach (var (ci, cov) in BuildOrder(b))
                {
                    _block = b;
                    _condition = conditions[ci].name;
                    _coverage = coverageLabels[cov];
                    ApplyCondition(conditions[ci]);
                    targetRenderer.transform.localScale = Vector3.one * coverageScales[cov];
                    yield return RunCondition();
                    if (!_running) break;
                    targetRenderer.enabled = false;
                    yield return new WaitForSecondsRealtime(cooldownSeconds);
                    targetRenderer.enabled = true;
                }
            }
            RestoreState();
            Debug.Log($"[GlassRunner] Capture ended. CSV: {_path}");
        }

        bool Error(string message)
        {
            Debug.LogError($"[GlassRunner] {message}");
            return false;
        }

        bool ValidateConfiguration()
        {
            if (conditions == null || conditions.Length == 0 || targetRenderer == null ||
                coverageScales == null || coverageScales.Length == 0 || coverageLabels == null ||
                coverageLabels.Length != coverageScales.Length || warmupFrames < 8 || captureFrames < 1 || blocks < 1 ||
                float.IsNaN(cooldownSeconds) || float.IsInfinity(cooldownSeconds) || cooldownSeconds < 0)
                return Error("Assign conditions/renderer, matching coverage arrays, and valid timing values (warmup >= 8).");
            foreach (Material m in conditions)
                if (m == null || m.shader == null || m.shader.name != "Thesis/GlassTiered" || !m.shader.isSupported)
                    return Error("Every condition must use a supported Thesis/GlassTiered shader.");
            foreach (float scale in coverageScales)
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0)
                    return Error("Coverage scales must be finite and positive.");
            if (string.IsNullOrWhiteSpace(fileNamePrefix) || fileNamePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                fileNamePrefix.Contains("/") || fileNamePrefix.Contains("\\"))
                return Error("Output prefix must be a plain file name.");
            if (!FrameTimingManager.IsFeatureEnabled()) return Error("Enable Player Settings > Frame Timing Stats.");
            if (measurementCamera == null) measurementCamera = Camera.main;
            if (measurementCamera == null || !measurementCamera.TryGetComponent(out _cameraData))
                return Error("Assign the URP camera rendering the test object.");
            if (!targetRenderer.gameObject.activeInHierarchy || targetRenderer.sharedMaterials.Length != 1)
                return Error("Use an active target with exactly one material slot.");
            if (!toggleOpaqueTexturePerCondition && !_cameraData.requiresColorTexture)
                foreach (Material m in conditions)
                    if (m.IsKeywordEnabled("_REFRACT_SSR")) return Error("Screen-space conditions require the camera opaque texture.");
            return true;
        }

        void ApplyCondition(Material material)
        {
            targetRenderer.sharedMaterial = material;
            if (toggleOpaqueTexturePerCondition)
                _cameraData.requiresColorOption = material.IsKeywordEnabled("_REFRACT_SSR")
                    ? CameraOverrideOption.On : CameraOverrideOption.Off;
            _opaqueEnabled = _cameraData.requiresColorTexture;
        }

        List<(int, int)> BuildOrder(int block)
        {
            var order = new List<(int, int)>();
            for (int c = 0; c < conditions.Length; c++)
                for (int v = 0; v < coverageScales.Length; v++) order.Add((c, v));
            if (block % 2 == 1) order.Reverse();
            return order;
        }

        IEnumerator RunCondition()
        {
            _sampleCount = 0;
            for (int i = 0; i < warmupFrames; i++)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return null;
                if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
                    _lastTimestamp = Math.Max(_lastTimestamp, _timings[0].frameStartTimestamp);
            }
            long attempts = 0, maxAttempts = Math.Max((long)captureFrames * 10, 600);
            while (_sampleCount < captureFrames && attempts++ < maxAttempts)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return null;
                if (FrameTimingManager.GetLatestTimings(1, _timings) == 0) continue;
                FrameTiming t = _timings[0];
                if (t.frameStartTimestamp <= _lastTimestamp) continue;
                _lastTimestamp = t.frameStartTimestamp;
                if (t.gpuFrameTime <= 0 || double.IsNaN(t.gpuFrameTime) || double.IsInfinity(t.gpuFrameTime) ||
                    t.cpuFrameTime <= 0 || double.IsNaN(t.cpuFrameTime) || double.IsInfinity(t.cpuFrameTime)) continue;
                _samples[_sampleCount++] = t;
            }
            int captured = _sampleCount;
            SaveSamples();
            if (captured < captureFrames)
            {
                Debug.LogError($"[GlassRunner] Only {captured}/{captureFrames} valid samples. Stopping: GPU timing may be unsupported on this XR backend; use a verified headset profiler.");
                _running = false;
            }
            else if (_running) Debug.Log($"[GlassRunner] Saved block {_block} / {_condition} / {_coverage}: {captured} samples.");
        }

        static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

        void SaveSamples()
        {
            if (_sampleCount == 0 || _path == null) return;
            var csv = new StringBuilder(_sampleCount * 100);
            for (int i = 0; i < _sampleCount; i++)
            {
                FrameTiming t = _samples[i];
                csv.Append(_block).Append(',').Append(Csv(_condition)).Append(',').Append(Csv(_coverage)).Append(',')
                    .Append(i).Append(',').Append(t.frameStartTimestamp).Append(',')
                    .Append(t.gpuFrameTime.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.cpuFrameTime.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.widthScale.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.heightScale.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                    .Append(_opaqueEnabled ? 1 : 0).AppendLine();
            }
            try { File.AppendAllText(_path, csv.ToString()); }
            catch (Exception e)
            {
                Debug.LogError($"[GlassRunner] CSV write failed: {e.Message}");
                _running = false;
            }
            _sampleCount = 0;
        }

        void RestoreState()
        {
            if (_originalMaterials == null) return;
            if (targetRenderer != null && _originalMaterials != null)
            {
                targetRenderer.sharedMaterials = _originalMaterials;
                targetRenderer.transform.localScale = _originalScale;
                targetRenderer.enabled = _originalEnabled;
            }
            if (_cameraData != null) _cameraData.requiresColorOption = _originalColorOption;
            _originalMaterials = null;
            _running = false;
        }

        void OnDisable()
        {
            StopAllCoroutines();
            if (_originalMaterials == null) return;
            SaveSamples();
            RestoreState();
        }

        void OnApplicationPause(bool paused)
        {
            if (!paused || !_running) return;
            Debug.LogError("[GlassRunner] Application paused: stopping capture to avoid mixing conditions across resume.");
            OnDisable();
        }
    }
}
