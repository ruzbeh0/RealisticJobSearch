#nullable enable
using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch.Systems
{
    /// <summary>
    /// Applies the final accept/reject roll once the game has produced a real path duration.
    /// This runs on the main thread so rejected paths are removed before vanilla FindJobSystem
    /// can turn them into a job.
    /// </summary>
    public sealed partial class GravityAcceptanceGateSystem : GameSystemBase
    {
        private EntityQuery m_ParamsQ;
        private EntityQuery m_ResultsQ;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ParamsQ = GetEntityQuery(ComponentType.ReadOnly<GravityAcceptParams>());
            if (m_ParamsQ.IsEmptyIgnoreFilter)
            {
                EntityManager.CreateEntity(typeof(GravityAcceptParams));
            }

            EntityManager.SetComponentData(m_ParamsQ.GetSingletonEntity(), JobSearchScoring.FromSettings(Mod.m_Setting));

            m_ResultsQ = GetEntityQuery(
                ComponentType.ReadOnly<JobSeeker>(),
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<PathInformation>(),
                ComponentType.Exclude<Deleted>());

            RequireForUpdate(m_ResultsQ);
        }

        protected override void OnUpdate()
        {
            var settings = JobSearchScoring.FromSettings(Mod.m_Setting);
            EntityManager.SetComponentData(m_ParamsQ.GetSingletonEntity(), settings);

            if (!Mod.GameplayEnabled && !JobSearchDebug.Enabled)
            {
                return;
            }

            uint frame = World.GetExistingSystemManaged<SimulationSystem>().frameIndex;
            var freeLookup = GetComponentLookup<FreeWorkplaces>(true);
            var providerLookup = GetComponentLookup<WorkProvider>(true);
            var currentBuildingLookup = GetComponentLookup<CurrentBuilding>(true);
            var outsideConnectionLookup = GetComponentLookup<OutsideConnection>(true);

            var entities = m_ResultsQ.ToEntityArray(Allocator.Temp);
            var seekers = m_ResultsQ.ToComponentDataArray<JobSeeker>(Allocator.Temp);
            var owners = m_ResultsQ.ToComponentDataArray<Owner>(Allocator.Temp);
            var infos = m_ResultsQ.ToComponentDataArray<PathInformation>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                Entity seeker = entities[i];
                JobSeeker seekerData = seekers[i];
                Owner owner = owners[i];
                PathInformation info = infos[i];

                if ((info.m_State & PathFlags.Pending) != 0)
                {
                    continue;
                }

                if ((info.m_State & PathFlags.Failed) != 0)
                {
                    continue;
                }

                Entity destination = info.m_Destination;
                int totalJobs = GetTotalWorkplaces(providerLookup, destination);
                int freeJobs = GetFreeWorkplaces(freeLookup, destination);
                float minutes = math.max(0.01f, info.m_Duration / 60f);
                float meters = math.max(0f, info.m_Distance);

                if (destination == Entity.Null)
                {
                    string nullReason = ClassifyNullDestination(
                        seeker,
                        seekerData,
                        owner,
                        info,
                        currentBuildingLookup,
                        outsideConnectionLookup);

                    if (IsInertNullDestination(info, nullReason))
                    {
                        continue;
                    }

                    JobSearchScore nullTargetScore = JobSearchScoring.Score(settings, meters, minutes, totalJobs, freeJobs, roll: -1f);
                    JobSearchDebug.LogDecision(
                        frame,
                        "path_result",
                        seeker,
                        info.m_Origin,
                        destination,
                        "passed_through",
                        nullReason,
                        rank: -1,
                        candidateCount: -1,
                        keptCount: -1,
                        nullTargetScore,
                        settings);
                    continue;
                }

                if (!Mod.GameplayEnabled)
                {
                    JobSearchScore vanillaScore = JobSearchScoring.Score(settings, meters, minutes, totalJobs, freeJobs, roll: -1f);
                    JobSearchDebug.LogDecision(
                        frame,
                        "path_result",
                        seeker,
                        info.m_Origin,
                        destination,
                        "accepted",
                        "vanilla_mod_disabled",
                        rank: -1,
                        candidateCount: -1,
                        keptCount: -1,
                        vanillaScore,
                        settings);
                    continue;
                }

                uint seed = JobSearchScoring.MakeSeed(frame, seeker, destination, (int)math.round(minutes * 100f));
                var random = Unity.Mathematics.Random.CreateFromIndex(seed);
                float roll = random.NextFloat();
                JobSearchScore score = JobSearchScoring.Score(settings, meters, minutes, totalJobs, freeJobs, roll);

                if (totalJobs > 0 && freeJobs <= 0)
                {
                    EntityManager.RemoveComponent<PathInformation>(seeker);
                    JobSearchDebug.LogDecision(
                        frame,
                        "path_result",
                        seeker,
                        info.m_Origin,
                        destination,
                        "rejected",
                        "no_free_workplaces",
                        rank: -1,
                        candidateCount: -1,
                        keptCount: -1,
                        score,
                        settings);
                    continue;
                }

                bool accept;
                string reason;
                if (settings.MaxDailyRejections <= 0)
                {
                    accept = true;
                    reason = "rejection_disabled";
                }
                else if (EntityManager.HasComponent<RefusedLongCommute>(seeker) &&
                         EntityManager.GetComponentData<RefusedLongCommute>(seeker).Count >= settings.MaxDailyRejections)
                {
                    accept = true;
                    reason = "daily_rejection_cap_reached";
                }
                else
                {
                    accept = roll <= score.Probability;
                    reason = accept ? "roll_within_probability" : "roll_exceeded_probability";
                }

                if (accept)
                {
                    if (EntityManager.HasComponent<RefusedLongCommute>(seeker))
                    {
                        EntityManager.RemoveComponent<RefusedLongCommute>(seeker);
                    }

                    JobSearchDebug.LogDecision(
                        frame,
                        "path_result",
                        seeker,
                        info.m_Origin,
                        destination,
                        "accepted",
                        reason,
                        rank: -1,
                        candidateCount: -1,
                        keptCount: -1,
                        score,
                        settings);
                    continue;
                }

                EntityManager.RemoveComponent<PathInformation>(seeker);
                if (EntityManager.HasComponent<RefusedLongCommute>(seeker))
                {
                    var refusal = EntityManager.GetComponentData<RefusedLongCommute>(seeker);
                    refusal.Count += 1;
                    refusal.LastRefusalFrame = frame;
                    EntityManager.SetComponentData(seeker, refusal);
                }
                else
                {
                    EntityManager.AddComponentData(seeker, new RefusedLongCommute
                    {
                        Count = 1,
                        LastRefusalFrame = frame
                    });
                }

                JobSearchDebug.LogDecision(
                    frame,
                    "path_result",
                    seeker,
                    info.m_Origin,
                    destination,
                    "rejected",
                    reason,
                    rank: -1,
                    candidateCount: -1,
                    keptCount: -1,
                    score,
                    settings);
            }

            entities.Dispose();
            seekers.Dispose();
            owners.Dispose();
            infos.Dispose();
        }

        private static string ClassifyNullDestination(
            Entity seeker,
            in JobSeeker jobSeeker,
            in Owner owner,
            in PathInformation info,
            ComponentLookup<CurrentBuilding> currentBuildings,
            ComponentLookup<OutsideConnection> outsideConnections)
        {
            if (jobSeeker.m_Outside != 0)
            {
                return "null_target_outside_jobseeker";
            }

            if (IsOutsideConnection(info.m_Origin, outsideConnections))
            {
                return "null_target_origin_outside_connection";
            }

            if (TryGetCurrentBuilding(seeker, currentBuildings, out Entity currentBuilding) &&
                IsOutsideConnection(currentBuilding, outsideConnections))
            {
                return "null_target_current_building_outside_connection";
            }

            if (owner.m_Owner != Entity.Null &&
                TryGetCurrentBuilding(owner.m_Owner, currentBuildings, out currentBuilding) &&
                IsOutsideConnection(currentBuilding, outsideConnections))
            {
                return "null_target_owner_current_building_outside_connection";
            }

            if (info.m_Origin == Entity.Null)
            {
                if (TryGetCurrentBuilding(seeker, currentBuildings, out currentBuilding))
                {
                    return "null_target_no_origin_seeker_current_building";
                }

                if (owner.m_Owner != Entity.Null &&
                    TryGetCurrentBuilding(owner.m_Owner, currentBuildings, out currentBuilding))
                {
                    return "null_target_no_origin_owner_current_building";
                }

                return "null_target_no_origin";
            }

            if (owner.m_Owner == Entity.Null)
            {
                return "null_target_no_owner";
            }

            return "null_target_unknown";
        }

        private static bool IsInertNullDestination(in PathInformation info, string nullReason)
        {
            return nullReason == "null_target_no_origin_seeker_current_building" &&
                   info.m_Origin == Entity.Null &&
                   info.m_Distance <= 0.01f &&
                   info.m_Duration <= 0.01f;
        }

        private static bool TryGetCurrentBuilding(
            Entity entity,
            ComponentLookup<CurrentBuilding> currentBuildings,
            out Entity currentBuilding)
        {
            currentBuilding = Entity.Null;
            if (entity == Entity.Null || !currentBuildings.HasComponent(entity))
            {
                return false;
            }

            currentBuilding = currentBuildings[entity].m_CurrentBuilding;
            return currentBuilding != Entity.Null;
        }

        private static bool IsOutsideConnection(Entity entity, ComponentLookup<OutsideConnection> outsideConnections)
        {
            return entity != Entity.Null && outsideConnections.HasComponent(entity);
        }

        private static int GetTotalWorkplaces(ComponentLookup<WorkProvider> providerLookup, Entity building)
        {
            return building != Entity.Null && providerLookup.HasComponent(building) ? providerLookup[building].m_MaxWorkers : 0;
        }

        private static int GetFreeWorkplaces(ComponentLookup<FreeWorkplaces> freeLookup, Entity building)
        {
            return building != Entity.Null && freeLookup.HasComponent(building) ? freeLookup[building].Count : 0;
        }
    }
}
