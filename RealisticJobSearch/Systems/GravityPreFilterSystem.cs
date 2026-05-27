// ===============================
// GravityPreFilterSystem.cs
// ===============================
#nullable enable
using Game;
using Game.Common;
using Game.Companies;
using Game.Objects;           // Transform
using Game.Pathfind;
using Game.Simulation;        // SimulationSystem (for frameIndex)
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch.Systems
{
    /// <summary>
    /// Phase 1 (Burst job): score ProposedJobPath and either (a) drop + cooldown, or (b) emit an enqueue request.
    /// Phase 2 (main thread): consume requests and push to PathfindSetupSystem queue.
    /// </summary>
    public sealed partial class GravityPreFilterSystem : GameSystemBase
    {
        private EntityQuery _proposalsQ;
        private ComponentLookup<Transform> _xf;   // read positions
        private ComponentLookup<WorkProvider> _wp; // read total workers
        private ComponentLookup<FreeWorkplaces> _free; // read free workers

        private EndFrameBarrier _endBarrier; // for ECB

        protected override void OnCreate()
        {
            base.OnCreate();
            _proposalsQ = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadOnly<ProposedJobPath>() },
                None = new ComponentType[] { typeof(Deleted) }
            });
            _xf = GetComponentLookup<Transform>(true);
            _wp = GetComponentLookup<WorkProvider>(true);
            _free = GetComponentLookup<FreeWorkplaces>(true);

            _endBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            RequireForUpdate(_proposalsQ);
        }

        protected override void OnUpdate()
        {
            if (_proposalsQ.IsEmptyIgnoreFilter) return;
            if (!HasSingleton<GravityAcceptParams>()) return;

            _xf.Update(this);
            _wp.Update(this);
            _free.Update(this);

            var parms = GetSingleton<GravityAcceptParams>();
            var sim = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = sim.frameIndex;

            var ecb = _endBarrier.CreateCommandBuffer().AsParallelWriter();

            // Phase 1: Burst job to evaluate proposals and emit enqueue requests
            var job = new PrefilterJob
            {
                XfRO = _xf,
                WpRO = _wp,
                FreeRO = _free,
                Params = parms,
                Frame = frame,
                Ecb = ecb
            };

            Dependency = job.ScheduleParallel(_proposalsQ, Dependency);

            // Phase 2: main-thread consume any RjsEnqueueRequest and push into setup queue
            Dependency.Complete(); // keep consumption simple & deterministic
            ConsumeEnqueueRequests();
        }

        [BurstCompile]
        private partial struct PrefilterJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<Transform> XfRO;
            [ReadOnly] public ComponentLookup<WorkProvider> WpRO;
            [ReadOnly] public ComponentLookup<FreeWorkplaces> FreeRO;
            public GravityAcceptParams Params;
            public uint Frame;
            public EntityCommandBuffer.ParallelWriter Ecb;

            public void Execute([ChunkIndexInQuery] int ciq, Entity e, in ProposedJobPath prop)
            {
                if (!TryXZ(prop.Origin, XfRO, out var o) || !TryXZ(prop.Target, XfRO, out var d))
                {
                    Ecb.RemoveComponent<ProposedJobPath>(ciq, e);
                    return;
                }

                float meters = math.distance(o, d);
                float minutes = math.max(ParamsMin, EstimateMinutes(meters));
                int total = GetTotalWorkplaces(WpRO, prop.Target);
                int free = GetFreeWorkplaces(FreeRO, prop.Target);
                if (total <= 0)
                {
                    Ecb.RemoveComponent<ProposedJobPath>(ciq, e);
                    return;
                }

                float mass = math.max(1f, free); // current availability weight
                float u = Params.AlphaJobs * math.log(mass) - Params.BetaMinute * minutes; // utility
                float p = SaturateFast(math.saturate(1f / (1f + math.exp(-u))));
                p = math.clamp(p, Params.MinAccept, Params.MaxAccept);
                const float floor = 0.05f; // soft floor; keep in sync with patch

                if (p < floor)
                {
                    Ecb.RemoveComponent<ProposedJobPath>(ciq, e);
                    Ecb.AddComponent(ciq, e, new SoftCooldown { UntilFrame = Frame + 1024u });
                }
                else
                {
                    // Defer enqueuing to main thread: emit request component
                    Ecb.AddComponent(ciq, e, new RjsEnqueueRequest
                    {
                        Seeker = prop.Seeker,
                        Origin = prop.Origin,
                        Target = prop.Target
                    });
                    Ecb.RemoveComponent<ProposedJobPath>(ciq, e);
                }
            }
        }

        private void ConsumeEnqueueRequests()
        {
            var q = GetEntityQuery(ComponentType.ReadOnly<RjsEnqueueRequest>());
            if (q.IsEmptyIgnoreFilter) return;

            var reqs = q.ToComponentDataArray<RjsEnqueueRequest>(Allocator.Temp);
            var ents = q.ToEntityArray(Allocator.Temp);

            var setup = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
            var queue = setup.GetQueue(this, 80, 16); // match vanilla FindJobSystem capacities

            for (int i = 0; i < reqs.Length; i++)
            {
                var r = reqs[i];
                // Let the Harmony prefix pass this one through
                EntityManager.AddComponent<RjsBypassPrefilter>(r.Seeker);

                var parameters = new PathfindParameters
                {
                    m_MaxSpeed = new float2(111.111115f),
                    m_WalkSpeed = new float2(1.66666675f),
                    m_ParkingSize = default,
                    m_ParkingDelta = 0f,
                    m_MaxCost = CitizenBehaviorSystem.kMaxPathfindCost,
                    m_MaxResultCount = 1,
                    m_Methods = PathMethod.Pedestrian | PathMethod.PublicTransportDay | PathMethod.PublicTransportNight,
                    m_PathfindFlags = PathfindFlags.Simplified | PathfindFlags.IgnorePath,
                    m_IgnoredRules = default,
                };

                var origin = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = PathMethod.Pedestrian,
                    m_Entity = r.Origin
                };

                var destination = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.JobSeekerTo,
                    m_Methods = PathMethod.Pedestrian,
                    m_Entity = r.Target,
                    m_RandomCost = 0f
                };

                queue.Enqueue(new SetupQueueItem(r.Seeker, parameters, origin, destination));

                // cleanup the request tag
                EntityManager.RemoveComponent<RjsEnqueueRequest>(ents[i]);
            }

            reqs.Dispose();
            ents.Dispose();
        }

        private const float ParamsMin = 0.01f;

        private static bool TryXZ(Entity ent, ComponentLookup<Transform> tf, out float2 xz)
        {
            xz = default;
            if (ent == Entity.Null || !tf.HasComponent(ent)) return false;
            var p = tf[ent].m_Position;
            xz = new float2(p.x, p.z);
            return true;
        }

        private static int GetTotalWorkplaces(ComponentLookup<WorkProvider> wpRO, Entity b)
            => wpRO.HasComponent(b) ? wpRO[b].m_MaxWorkers : 0;

        private static int GetFreeWorkplaces(ComponentLookup<FreeWorkplaces> freeRO, Entity b)
            => freeRO.HasComponent(b) ? freeRO[b].Count : 0;

        private static float EstimateMinutes(float meters) => meters / 80f / 60f; // rough ~5 km/h → minutes
        private static float SaturateFast(float x) => math.clamp(x, 0f, 1f);
    }

    // ============ Components ============
    public struct ProposedJobPath : IComponentData
    {
        public Entity Seeker;
        public Entity Origin;
        public Entity Target;
    }

    public struct SoftCooldown : IComponentData { public uint UntilFrame; }

    /// <summary>Emitted by Burst job, consumed on main thread to enqueue a pathfind.</summary>
    public struct RjsEnqueueRequest : IComponentData
    {
        public Entity Seeker;
        public Entity Origin;
        public Entity Target;
    }
}
