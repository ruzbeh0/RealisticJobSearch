#nullable enable
using Game;
using Game.Agents;
using Game.Pathfind;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch.Systems
{
    public sealed partial class RetryThrottleSystem : GameSystemBase
    {
        EntityQuery _paramsQ;
        EntityQuery _seekersQ;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Cache queries
            _paramsQ = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SpatialSamplerParams>());
            _seekersQ = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<JobSeeker>(),
                ComponentType.ReadWrite<RefusedLongCommute>(),
                ComponentType.Exclude<PathInformation>());

            // Ensure params singleton exists once (safe if already present)
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
                    MaxDailyRetries = 1,
                    RetryCooldownHours = 2f
                });
            }
        }

        protected override void OnUpdate()
        {
            // Nothing to do if no seekers are currently in "refused" state and idle
            if (_seekersQ.IsEmptyIgnoreFilter) return;

            var sim = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = sim.frameIndex;

            var s = EntityManager.GetComponentData<SpatialSamplerParams>(_paramsQ.GetSingletonEntity());

            const uint framesPerHour = 1024; // adjust if you have a better constant
            uint cooldown = (uint)math.max(1, s.RetryCooldownHours * framesPerHour);

            var ents = _seekersQ.ToEntityArray(Allocator.Temp);
            var data = _seekersQ.ToComponentDataArray<RefusedLongCommute>(Allocator.Temp);

            for (int i = 0; i < ents.Length; i++)
            {
                var rc = data[i];

                // Hard cap: stop re-trying beyond your daily limit; vanilla can reset count on a schedule if needed.
                if (rc.Count >= s.MaxDailyRetries) continue;

                // Cooldown passed → allow another attempt by removing the marker
                if (frame - rc.LastRefusalFrame >= cooldown)
                    EntityManager.RemoveComponent<RefusedLongCommute>(ents[i]);
            }

            ents.Dispose();
            data.Dispose();
        }
    }
}
