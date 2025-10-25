using Game;
using Game.Companies;
using Game.Objects;           // Transform
using Game.Pathfind;
using Game.Simulation;        // SimulationSystem (for frameIndex)
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch.Systems
{

    public sealed partial class GravityPreFilterSystem : GameSystemBase
    {
        private EntityQuery _proposalsQ;
        private ComponentLookup<Transform> _xf;   // read positions
        private ComponentLookup<WorkProvider> _wp;     // total workplaces (your wrapper)
        private ComponentLookup<FreeWorkplaces> _free; // free workplaces (your wrapper)

        protected override void OnCreate()
        {
            base.OnCreate();
            _proposalsQ = GetEntityQuery(ComponentType.ReadOnly<ProposedJobPath>());
            _xf = GetComponentLookup<Transform>(true);
            _wp = GetComponentLookup<WorkProvider>(true);
            _free = GetComponentLookup<FreeWorkplaces>(true);
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

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // Pull entities + components explicitly (no Entities.ForEach)
            var entities = _proposalsQ.ToEntityArray(Allocator.Temp);
            var props = _proposalsQ.ToComponentDataArray<ProposedJobPath>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var prop = props[i];

                if (!TryXZ(prop.Origin, _xf, out var o) || !TryXZ(prop.Target, _xf, out var d))
                {
                    ecb.RemoveComponent<ProposedJobPath>(e);
                    continue;
                }

                float meters = math.distance(o, d);
                float tMin = (meters / 7.0f) / 60f; // crude ~25 km/h → minutes

                float free = _free.HasComponent(prop.Target) ? _free[prop.Target].Count : 0f;
                float total = _wp.HasComponent(prop.Target) ? _wp[prop.Target].m_MaxWorkers : 0f;
                float mass = math.max(1f, Mod.m_Setting.weight_free_jobs * free + Mod.m_Setting.weight_total_jobs * total); // blended mass
                if(free == 0f)
                {
                    mass = 0f; // no free spots means zero mass
                }

                float U = parms.AlphaJobs * math.log(1f + mass) - parms.BetaMinute * tMin;
                float p = math.saturate(1f / (1f + math.exp(-U))); // logistic-ish
                float floor = math.max(0.05f, parms.MinAccept * 0.75f);

                if (p < floor)
                {
                    ecb.RemoveComponent<ProposedJobPath>(e);
                    ecb.AddComponent(e, new SoftCooldown { UntilFrame = sim.frameIndex + 1024u });
                }
                else
                {
                    // Your enqueue hook — do NOT try to read PathfindParameters as a component
                    EnqueueJobPath(ecb, e, prop.Origin, prop.Target);
                    ecb.RemoveComponent<ProposedJobPath>(e);
                }
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();

            entities.Dispose();
            props.Dispose();
        }

        private static bool TryXZ(Entity ent, ComponentLookup<Transform> tf, out float2 xz)
        {
            xz = default;
            if (ent == Entity.Null || !tf.HasComponent(ent)) return false;
            var p = tf[ent].m_Position;
            xz = new float2(p.x, p.z);
            return true;
        }

        private void EnqueueJobPath(EntityCommandBuffer ecb, Entity seeker, Entity originEnt, Entity targetEnt)
        {
            // Let the Harmony prefix pass this one through
            ecb.AddComponent(seeker, new RjsBypassPrefilter());

            var parameters = new PathfindParameters
            {
                m_MaxSpeed = new float2(111.111115f),  // vanilla literal
                m_WalkSpeed = new float2(1.66666675f),  // ~6 km/h
                m_ParkingSize = default,
                m_ParkingDelta = 0f,
                m_MaxCost = CitizenBehaviorSystem.kMaxPathfindCost,
                m_MaxResultCount = 1,
                m_Methods = PathMethod.Pedestrian | PathMethod.PublicTransportDay | PathMethod.PublicTransportNight,
                m_PathfindFlags = PathfindFlags.Simplified | PathfindFlags.IgnorePath,
                m_IgnoredRules = default,
                m_SecondaryIgnoredRules = default
            };

            var origin = new SetupQueueTarget
            {
                m_Type = SetupTargetType.CurrentLocation,
                m_Methods = PathMethod.Pedestrian,
                m_Entity = originEnt
            };

            var destination = new SetupQueueTarget
            {
                m_Type = SetupTargetType.JobSeekerTo,
                m_Methods = PathMethod.Pedestrian,
                m_Entity = targetEnt,
                m_RandomCost = 0f
            };

            var setup = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
            var queue = setup.GetQueue(this, 80, 16); // capacities same as vanilla FindJobSystem
            queue.Enqueue(new SetupQueueItem(seeker, parameters, origin, destination));
        }
    }

    // Your lightweight proposal & params
    public struct ProposedJobPath : IComponentData
    {
        public Entity Seeker;
        public Entity Origin;
        public Entity Target;
    }

    public struct SoftCooldown : IComponentData { public uint UntilFrame; }
}
