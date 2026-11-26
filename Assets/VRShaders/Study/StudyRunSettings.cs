using System;
using UnityEngine;

namespace Thesis.Glass.Study
{
    public enum StudyRunMode { Performance, ImageCapture, ExternalPerformance }
    public enum StudyPath { Static, Lateral, Yaw, Approach }

    [CreateAssetMenu(menuName = "Thesis/Automated Study Settings", fileName = "StudySettings")]
    public sealed class StudyRunSettings : ScriptableObject
    {
        public StudyRunMode mode = StudyRunMode.Performance;
        public bool startAutomatically = true;
        public bool requireHeadset = true;
        [Min(0)] public float startupDelaySeconds = 5;
        [Min(1)] public float startupTimeoutSeconds = 30;
        public Material[] conditions;
        public StudyPath[] paths = { StudyPath.Static, StudyPath.Lateral, StudyPath.Yaw };
        [Min(1)] public int blocks = 1;
        [Min(8)] public int warmupFrames = 120;
        [Min(0)] public float cooldownSeconds = 60;
        [Min(0.1f)] public float pathDurationSeconds = 10;
        [Min(1)] public int performanceCycles = 2;
        [Min(8)] public int minimumPerformanceSamples = 1000;
        [Min(2)] public int imagePositions = 21;
        [Min(2)] public int imageSettleFrames = 4;
        [Min(1)] public float captureTimeoutSeconds = 60;
        [Min(0)] public float translationMetres = 0.1f;
        [Min(0)] public float yawDegrees = 10;
        public bool captureNoGlassBaseline = true;
        public bool toggleOpaqueTexturePerCondition = true;
        public bool disablePostProcessing = true;
        public bool disableFoveation = true;
        public bool compensateTrackedCentre = true;
        public bool flipReadbackRows;

        public string Validate()
        {
            if (!Enum.IsDefined(typeof(StudyRunMode), mode)) return "Invalid study mode.";
            if (conditions == null || conditions.Length == 0) return "Assign at least one glass condition material.";
            foreach (Material material in conditions)
                if (material == null || material.shader == null || material.shader.name != "Thesis/GlassTiered" || !material.shader.isSupported)
                    return "Conditions must use a supported Thesis/GlassTiered shader.";
            if (paths == null || paths.Length == 0) return "Choose at least one motion path.";
            foreach (StudyPath path in paths) if (!Enum.IsDefined(typeof(StudyPath), path)) return "Invalid motion path.";
            if (blocks < 1 || warmupFrames < 8 || performanceCycles < 1 || minimumPerformanceSamples < 8 || imagePositions < 2 || imageSettleFrames < 2) return "Invalid trial counts or warmup settings.";
            if (!Positive(pathDurationSeconds) || !Positive(startupTimeoutSeconds) || !Positive(captureTimeoutSeconds)
                || !Nonnegative(startupDelaySeconds) || !Nonnegative(cooldownSeconds) || !Nonnegative(translationMetres) || !Nonnegative(yawDegrees))
                return "Timing and motion values must be finite, with positive durations.";
            return null;
        }
        static bool Positive(float x) => Nonnegative(x) && x > 0;
        static bool Nonnegative(float x) => !float.IsNaN(x) && !float.IsInfinity(x) && x >= 0;

        public void Evaluate(StudyPath path, float phase, out Vector3 translation, out Quaternion rotation)
        {
            float wave = Mathf.Sin(Mathf.Clamp01(phase) * 2 * Mathf.PI);
            translation = path == StudyPath.Lateral ? Vector3.right * (wave * translationMetres)
                : path == StudyPath.Approach ? Vector3.forward * (wave * translationMetres) : Vector3.zero;
            rotation = path == StudyPath.Yaw ? Quaternion.Euler(0, wave * yawDegrees, 0) : Quaternion.identity;
        }
    }
}
