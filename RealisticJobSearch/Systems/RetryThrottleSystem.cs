#nullable enable
using Game;
using Game.Agents;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace RealisticJobSearch.Systems
{
    /// <summary>
    /// Keeps refusal markers for the current in-game day so the acceptance gate can cap
    /// repeated long-commute refusals, then clears them for a fresh day.
    /// </summary>
    public sealed partial class RetryThrottleSystem : GameSystemBase
    {
        private EntityQuery m_ParamsQ;
        private EntityQuery m_SeekersQ;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ParamsQ = GetEntityQuery(ComponentType.ReadOnly<SpatialSamplerParams>());
            m_SeekersQ = GetEntityQuery(ComponentType.ReadOnly<RefusedLongCommute>(), ComponentType.ReadOnly<JobSeeker>());

            if (m_ParamsQ.IsEmptyIgnoreFilter)
            {
                EntityManager.CreateEntity(typeof(SpatialSamplerParams));
            }
        }

        protected override void OnUpdate()
        {
            var settings = JobSearchScoring.FromSettings(Mod.m_Setting);
            EntityManager.SetComponentData(m_ParamsQ.GetSingletonEntity(), new SpatialSamplerParams
            {
                CellSizeMeters = 192f,
                RadiusKm = 6f,
                AlphaJobs = settings.AlphaJobs,
                BetaMinute = settings.BetaMinute,
                TauMinutes = 5f,
                MaxCandidates = settings.TopK,
                Wildcards = 1,
                MaxDailyRetries = settings.MaxDailyRejections,
                RetryCooldownHours = settings.RetryCooldownHours
            });

            if (m_SeekersQ.IsEmptyIgnoreFilter)
            {
                return;
            }

            var entities = m_SeekersQ.ToEntityArray(Allocator.Temp);
            if (!Mod.GameplayEnabled)
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    EntityManager.RemoveComponent<RefusedLongCommute>(entities[i]);
                }

                entities.Dispose();
                return;
            }

            uint frame = World.GetExistingSystemManaged<SimulationSystem>().frameIndex;
            uint dailyResetFrames = (uint)TimeSystem.kTicksPerDay;
            var refusals = m_SeekersQ.ToComponentDataArray<RefusedLongCommute>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                uint age = frame - refusals[i].LastRefusalFrame;
                if (age >= dailyResetFrames)
                {
                    EntityManager.RemoveComponent<RefusedLongCommute>(entities[i]);
                }
            }

            entities.Dispose();
            refusals.Dispose();
        }
    }
}
