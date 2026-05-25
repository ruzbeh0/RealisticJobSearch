// ============================================================
// File: RJS.Components.cs
// ============================================================
#nullable enable
using Unity.Entities;

namespace RealisticJobSearch
{
    /// <summary>Counts refusal attempts so we can broaden search but cap retries.</summary>
    public struct RefusedLongCommute : IComponentData
    {
        public int Count;
        public uint LastRefusalFrame;
    }

    /// <summary>Sampler knobs (Singleton). Tune via your in-game UI or a config file.</summary>
    public struct SpatialSamplerParams : IComponentData
    {
        public float CellSizeMeters;     // e.g., 192
        public float RadiusKm;           // e.g., 6
        public float AlphaJobs;          // gravity mass exponent (0.8–1.2)
        public float BetaMinute;         // time sensitivity per minute (0.06–0.12)
        public float TauMinutes;         // small offset for near-zero times (3–6)
        public int MaxCandidates;      // 8–24
        public int Wildcards;          // 0–2 unweighted picks
        public int MaxDailyRetries;    // cap path retries per-day (1-2)
        public float RetryCooldownHours; // min hours before retry (e.g., 2)
    }

    /// <summary>Gravity accept gate parameters (Singleton).</summary>
    public struct GravityAcceptParams : IComponentData
    {
        public float AlphaJobs;     // mass elasticity
        public float BetaMinute;    // time cost (per minute)
        public float MinAccept;     // clamp
        public float MaxAccept;     // clamp
        public float WeightFreeJobs;
        public float WeightTotalJobs;
        public float EstimatedCommuteSpeedKmh;
        public float SoftmaxTemperature;
        public int TopK;
        public int MaxDailyRejections;
        public float RetryCooldownHours;
    }
}
