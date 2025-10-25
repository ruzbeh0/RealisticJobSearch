#nullable enable
using Game;
using Game.Agents;
using Game.Companies;
using Game.Pathfind;
using Game.Simulation;
using System.Security.Cryptography;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch.Systems
{
    public sealed partial class GravityAcceptanceGateSystem : GameSystemBase
    {
        EntityQuery _paramsQ;
        EntityQuery _seekersQ;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Cache queries
            _paramsQ = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GravityAcceptParams>());
            _seekersQ = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<JobSeeker>(),
                ComponentType.ReadOnly<PathInformation>());

            // Ensure params singleton exists (only once)
            if (_paramsQ.IsEmptyIgnoreFilter)
            {
                var e = EntityManager.CreateEntity(typeof(GravityAcceptParams));
                EntityManager.SetComponentData(e, new GravityAcceptParams
                {
                    AlphaJobs = Mod.m_Setting.alpha_jobs,
                    BetaMinute = Mod.m_Setting.beta_minute,
                    MinAccept = Mod.m_Setting.min_accept,
                    MaxAccept = Mod.m_Setting.max_accept
                });
            }
        }

        protected override void OnUpdate()
        {
            // Nothing to do if there are no candidates this frame
            if (_seekersQ.IsEmptyIgnoreFilter) return;

            var sim = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = sim.frameIndex;

            var p = EntityManager.GetComponentData<GravityAcceptParams>(_paramsQ.GetSingletonEntity());

            var ents = _seekersQ.ToEntityArray(Allocator.Temp);
            var infos = _seekersQ.ToComponentDataArray<PathInformation>(Allocator.Temp);

            var wpRO = GetComponentLookup<WorkProvider>(true);
            var fwRO = GetComponentLookup<FreeWorkplaces>(true);

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            uint seed = frame ^ 0x9E3779B9u;

            for (int i = 0; i < ents.Length; i++)
            {
                var info = infos[i];

                // "Complete" in your build = not Pending and not Failed
                if ((info.m_State & PathFlags.Pending) != 0) continue;
                if ((info.m_State & PathFlags.Failed) != 0) continue;

                float minutes = math.max(0.01f, info.m_Duration);

                // Safety rails
                if (minutes <= 50f) { /* accept */ continue; }
                if (EntityManager.HasComponent<RefusedLongCommute>(ents[i]) &&
                    EntityManager.GetComponentData<RefusedLongCommute>(ents[i]).Count >= 3)
                {
                    /* accept */
                    continue;
                }

                // mass from destination building (free slots or max workers)
                float mass = 1f;
                Entity dest = info.m_Destination;
                if (dest != Entity.Null)
                {
                    float free = fwRO.HasComponent(dest) ? fwRO[dest].Count : 0f;
                    float total = wpRO.HasComponent(dest) ? (float)wpRO[dest].m_MaxWorkers : 0f;
                    mass = math.max(1f, Mod.m_Setting.weight_free_jobs * free + Mod.m_Setting.weight_total_jobs * total);
                }

                float U = p.AlphaJobs * math.log(1f + mass) - p.BetaMinute * minutes;
                float prob = math.clamp(1f / (1f + math.exp(-U)), p.MinAccept, p.MaxAccept);

                // RNG
                seed = unchecked(seed * 1664525u + 1013904223u);
                float r = (seed & 0x00FFFFFFu) / 16777216f;

                if (r > prob)
                {
                    ecb.RemoveComponent<PathInformation>(ents[i]);
                    if (!EntityManager.HasComponent<RefusedLongCommute>(ents[i]))
                        ecb.AddComponent(ents[i], new RefusedLongCommute { Count = 1, LastRefusalFrame = frame });
                    else
                    {
                        var rc = EntityManager.GetComponentData<RefusedLongCommute>(ents[i]);
                        rc.Count += 1; rc.LastRefusalFrame = frame;
                        ecb.SetComponent(ents[i], rc);
                    }
                }
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
            ents.Dispose();
            infos.Dispose();
        }
    }
}
