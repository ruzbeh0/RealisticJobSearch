// ===============================
// RetryThrottleSystem.cs
// ===============================
#nullable enable
using Game;
using Game.Agents;
using Game.Pathfind;
using Game.Simulation;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch.Systems
{
    public sealed partial class RetryThrottleSystem : GameSystemBase
    {
        EntityQuery _paramsQ;
        EntityQuery _seekersQ;
        EndFrameBarrier _endBarrier;

        protected override void OnCreate()
        {
            base.OnCreate();
            _paramsQ = GetEntityQuery(ComponentType.ReadOnly<SpatialSamplerParams>());
            _seekersQ = GetEntityQuery(ComponentType.ReadOnly<RefusedLongCommute>(), ComponentType.ReadOnly<JobSeeker>());
            _endBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            if (_paramsQ.IsEmptyIgnoreFilter)
            {
                var e = EntityManager.CreateEntity(typeof(SpatialSamplerParams));
                EntityManager.SetComponentData(e, new SpatialSamplerParams
                {
                    CellSizeMeters = 192f,
                    RadiusKm = 6f,
                    AlphaJobs = Mod.m_Setting.alpha_jobs,
                    BetaMinute = Mod.m_Setting.beta_minute,
                    TauMinutes = 5f,
                    MaxCandidates = 12,
                    Wildcards = 1,
                    MaxDailyRetries = 1,
                    RetryCooldownHours = 2f
                });
            }
        }

        protected override void OnUpdate()
        {
            if (_seekersQ.IsEmptyIgnoreFilter) return;

            var sim = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = sim.frameIndex;

            var s = EntityManager.GetComponentData<SpatialSamplerParams>(_paramsQ.GetSingletonEntity());
            const uint framesPerHour = 1024; // match vanilla sim scale
            uint cooldown = (uint)math.max(1, s.RetryCooldownHours * framesPerHour);

            var ecb = _endBarrier.CreateCommandBuffer().AsParallelWriter();

            var job = new ThrottleJob
            {
                Frame = frame,
                Cooldown = cooldown,
                MaxDailyRetries = s.MaxDailyRetries,
                Ecb = ecb
            };

            Dependency = job.ScheduleParallel(_seekersQ, Dependency);
        }

        [BurstCompile]
        private partial struct ThrottleJob : IJobEntity
        {
            public uint Frame;
            public uint Cooldown;
            public int MaxDailyRetries;
            public EntityCommandBuffer.ParallelWriter Ecb;

            public void Execute([ChunkIndexInQuery] int ciq, Entity e, in RefusedLongCommute rc)
            {
                if (rc.Count >= MaxDailyRetries) return;
                if (Frame - rc.LastRefusalFrame < Cooldown) return;
                Ecb.RemoveComponent<RefusedLongCommute>(ciq, e);
            }
        }
    }
}