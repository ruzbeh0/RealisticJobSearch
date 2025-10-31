#nullable enable
using Game;
using Game.Agents;
using Game.Common;
using Game.Companies;
using Game.Pathfind;
using Game.Simulation;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch.Systems
{
    /// <summary>
    /// Gates finished job-seeker path results using a gravity-style acceptance model.
    /// IMPORTANT: Uses the same PathInformation fields as the base game (m_Destination, m_Duration, m_State).
    /// </summary>
    public sealed partial class GravityAcceptanceGateSystem : GameSystemBase
    {
        private EndFrameBarrier m_EndBarrier;
        private EntityQuery m_ParamsQ;
        private EntityQuery m_ResultsQ;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            // Singleton params (created by Mod bootstrap or defaulted here)
            m_ParamsQ = GetEntityQuery(ComponentType.ReadOnly<GravityAcceptParams>());
            if (m_ParamsQ.IsEmptyIgnoreFilter)
            {
                EntityManager.CreateEntity(typeof(GravityAcceptParams));
                EntityManager.SetComponentData(m_ParamsQ.GetSingletonEntity(), new GravityAcceptParams
                {
                    AlphaJobs = Mod.m_Setting.alpha_jobs,
                    BetaMinute = Mod.m_Setting.beta_minute,
                    MinAccept = Mod.m_Setting.min_accept,
                    MaxAccept = Mod.m_Setting.max_accept
                });
            }

            // Finished path results for job seekers (same shape as vanilla StartWorkingJob’s query).
            m_ResultsQ = GetEntityQuery(
                ComponentType.ReadOnly<JobSeeker>(),
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<PathInformation>(),
                ComponentType.Exclude<Deleted>());

            RequireForUpdate(m_ResultsQ);
        }

        protected override void OnUpdate()
        {
            var p = EntityManager.GetComponentData<GravityAcceptParams>(m_ParamsQ.GetSingletonEntity());
            uint frame = World.GetExistingSystemManaged<SimulationSystem>().frameIndex;

            var job = new GateJob
            {
                m_EntityType = GetEntityTypeHandle(),
                m_PathInfoType = GetComponentTypeHandle<PathInformation>(true),
                m_FreeWorkplaces = GetComponentLookup<FreeWorkplaces>(true),
                m_Refusals = GetComponentLookup<RefusedLongCommute>(false),
                m_Params = p,
                m_SimulationFrame = frame,
                m_ECB = m_EndBarrier.CreateCommandBuffer().AsParallelWriter()
            };

            Dependency = job.ScheduleParallel(m_ResultsQ, Dependency);
            m_EndBarrier.AddJobHandleForProducer(Dependency);
        }

        [BurstCompile]
        private struct GateJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<PathInformation> m_PathInfoType;

            // Lookups (read-only for data checks)
            [ReadOnly] public ComponentLookup<FreeWorkplaces> m_FreeWorkplaces;
            // Lookup for presence check of the marker we update; writes go via ECB.
            public ComponentLookup<RefusedLongCommute> m_Refusals;

            public GravityAcceptParams m_Params;
            public uint m_SimulationFrame;

            public EntityCommandBuffer.ParallelWriter m_ECB;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var ents = chunk.GetNativeArray(m_EntityType);
                var infos = chunk.GetNativeArray(ref m_PathInfoType);

                for (int i = 0; i < ents.Length; i++)
                {
                    var info = infos[i];

                    // still pending? leave it for later
                    if ((info.m_State & PathFlags.Pending) != 0)
                        continue;

                    Entity seeker = ents[i];
                    Entity dest = info.m_Destination;

                    // crude "mass" term: available workplaces at the destination (>=1)
                    int jobs = 1;
                    if (dest != Entity.Null && m_FreeWorkplaces.HasComponent(dest))
                        jobs = math.max(1, m_FreeWorkplaces[dest].Count);

                    float minutes = math.max(0.01f, info.m_Duration / 60f);         // duration is in seconds
                    float mass = math.pow(jobs, m_Params.AlphaJobs);
                    float accept = mass * math.exp(-m_Params.BetaMinute * minutes);
                    accept = math.clamp(accept, m_Params.MinAccept, m_Params.MaxAccept);

                    // Reject below lower bound: strip PathInformation so vanilla won’t start working yet.
                    if (accept <= m_Params.MinAccept + 1e-4f)
                    {
                        m_ECB.RemoveComponent<PathInformation>(unfilteredChunkIndex, seeker);

                        if (m_Refusals.HasComponent(seeker))
                        {
                            var rc = m_Refusals[seeker];
                            rc.Count += 1;
                            rc.LastRefusalFrame = m_SimulationFrame;
                            m_ECB.SetComponent(unfilteredChunkIndex, seeker, rc);
                        }
                        else
                        {
                            m_ECB.AddComponent(unfilteredChunkIndex, seeker, new RefusedLongCommute
                            {
                                Count = 1,
                                LastRefusalFrame = m_SimulationFrame
                            });
                        }
                    }
                    // else accepted — do nothing; FindJobSystem.StartWorkingJob will handle it
                }
            }
        }
    }
}
