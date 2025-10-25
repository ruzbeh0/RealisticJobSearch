// ============================================================
// File: RJS.Components.cs
// ============================================================
#nullable enable
using Unity.Entities;

namespace RealisticJobSearch
{
    /// <summary>Hansen-style accessibility for work around a home building.</summary>
    public struct Accessibility : IComponentData
    {
        public float WorkAccess;        // higher => better access
        public uint LastUpdateFrame;   // throttling (simulation frames)
    }

    /// <summary>Marks a JobSeeker we already sampled for (avoid double work).</summary>
    public struct SampledPathPending : IComponentData { }

    /// <summary>Stores the explicit building we want to path to.</summary>
    public struct TargetBuildingOverride : IComponentData
    {
        public Entity Building;
    }

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
        public int MaxDailyRetries;    // cap path retries per-day (1–2)
        public float RetryCooldownHours; // min hours before retry (e.g., 2)
    }

    /// <summary>Gravity accept gate parameters (Singleton).</summary>
    public struct GravityAcceptParams : IComponentData
    {
        public float AlphaJobs;     // mass elasticity
        public float BetaMinute;    // time cost (per minute)
        public float MinAccept;     // clamp
        public float MaxAccept;     // clamp
    }

    public struct ProposedJobPath : IComponentData
    {
        public Entity Seeker;
        public Entity Origin;
        public Entity Target;   // workplace building the picker chose
    }

    /// <summary>Tag added right before we re-enqueue, so the Harmony prefix lets that one pass.</summary>
    public struct RjsBypassPrefilter : IComponentData { }

}
