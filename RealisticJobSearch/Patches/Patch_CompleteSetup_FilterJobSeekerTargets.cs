using Game.Common;
using Game.Citizens;
using Game.Companies;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch.Patches
{
    [HarmonyPatch(typeof(PathfindSetupSystem), "CompleteSetup")]
    public static class Patch_CompleteSetup_FilterJobSeekerTargets
    {
        private static AccessTools.FieldRef<PathfindSetupSystem, NativeList<PathfindSetupSystem.SetupListItem>> s_SetupListRef;
        private static AccessTools.FieldRef<PathfindSetupSystem, Unity.Jobs.JobHandle> s_SetupDepsRef;
        private static bool s_FieldRefsChecked;
        private static bool s_FieldRefsAvailable;
        private static bool s_RuntimeErrorLogged;

        private static readonly Dictionary<int, OriginInfo> s_OriginByAction = new Dictionary<int, OriginInfo>(256);
        private static readonly List<ScoredTarget> s_Scored = new List<ScoredTarget>(128);
        private static readonly List<PathTarget> s_UnknownPositionTargets = new List<PathTarget>(32);
        private static readonly List<PathTarget> s_KeptTargets = new List<PathTarget>(32);
        private static double[] s_Weights = new double[32];

        private struct OriginInfo
        {
            public Entity Entity;
            public float2 Position;
        }

        private struct ScoredTarget
        {
            public PathTarget Target;
            public Entity Workplace;
            public JobSearchScore Score;
            public int OriginalIndex;
        }

        private static bool EnsureFieldRefs()
        {
            if (s_FieldRefsChecked)
            {
                return s_FieldRefsAvailable;
            }

            s_FieldRefsChecked = true;
            try
            {
                if (AccessTools.Field(typeof(PathfindSetupSystem), "m_SetupList") == null ||
                    AccessTools.Field(typeof(PathfindSetupSystem), "m_SetupDependencies") == null)
                {
                    Mod.log.Info("[RJS] PathfindSetupSystem private fields were not found. Candidate prefilter disabled for this game version.");
                    s_FieldRefsAvailable = false;
                    return false;
                }

                s_SetupListRef = AccessTools.FieldRefAccess<PathfindSetupSystem, NativeList<PathfindSetupSystem.SetupListItem>>("m_SetupList");
                s_SetupDepsRef = AccessTools.FieldRefAccess<PathfindSetupSystem, Unity.Jobs.JobHandle>("m_SetupDependencies");
                s_FieldRefsAvailable = true;
                Mod.log.Info("[RJS] Candidate prefilter attached to PathfindSetupSystem.CompleteSetup.");
                return true;
            }
            catch (Exception ex)
            {
                Mod.log.Info("[RJS] Candidate prefilter disabled: " + ex.Message);
                s_FieldRefsAvailable = false;
                return false;
            }
        }

        public static void Prefix(PathfindSetupSystem __instance)
        {
            if (!Mod.GameplayEnabled)
            {
                return;
            }

            if (!EnsureFieldRefs())
            {
                return;
            }

            try
            {
                PrefixImpl(__instance);
            }
            catch (Exception ex)
            {
                if (!s_RuntimeErrorLogged)
                {
                    s_RuntimeErrorLogged = true;
                    Mod.log.Info("[RJS] Candidate prefilter failed and will be skipped this frame: " + ex);
                }
            }
            finally
            {
                s_OriginByAction.Clear();
                s_Scored.Clear();
                s_UnknownPositionTargets.Clear();
                s_KeptTargets.Clear();
            }
        }

        private static void PrefixImpl(PathfindSetupSystem instance)
        {
            var settings = JobSearchScoring.FromSettings(Mod.m_Setting);
            uint frame = instance.World.GetExistingSystemManaged<SimulationSystem>().frameIndex;
            var stats = new JobSearchDebug.PrefilterDebugStats
            {
                Calls = 1
            };

            ref var setupDeps = ref s_SetupDepsRef(instance);
            setupDeps.Complete();

            ref var setupList = ref s_SetupListRef(instance);
            stats.SetupItems = setupList.Length;

            try
            {
                if (setupList.Length == 0)
                {
                    return;
                }

                var tfRO = instance.GetComponentLookup<Transform>(isReadOnly: true);
                var wpRO = instance.GetComponentLookup<WorkProvider>(isReadOnly: true);
                var freeRO = instance.GetComponentLookup<FreeWorkplaces>(isReadOnly: true);
                var refusalsRO = instance.GetComponentLookup<RefusedLongCommute>(isReadOnly: true);
                var currentBuildingRO = instance.GetComponentLookup<CurrentBuilding>(isReadOnly: true);
                var ownerRO = instance.GetComponentLookup<Owner>(isReadOnly: true);

                for (int i = 0; i < setupList.Length; i++)
                {
                    ref var item = ref setupList.ElementAt(i);
                    if (item.m_ActionStart && item.m_Target.m_Type == SetupTargetType.CurrentLocation)
                    {
                        stats.CurrentLocationStarts++;
                        Entity originEntity = item.m_Target.m_Entity != Entity.Null ? item.m_Target.m_Entity : item.m_Owner;
                        if (TryXZ(originEntity, tfRO, out var pos))
                        {
                            stats.OriginsMapped++;
                            s_OriginByAction[item.m_ActionIndex] = new OriginInfo
                            {
                                Entity = originEntity,
                                Position = pos
                            };
                        }
                    }
                }

                for (int i = 0; i < setupList.Length; i++)
                {
                    ref var dst = ref setupList.ElementAt(i);
                    if (dst.m_ActionStart || dst.m_Target.m_Type != SetupTargetType.JobSeekerTo)
                    {
                        continue;
                    }

                    stats.JobSeekerTargets++;
                    if (!s_OriginByAction.TryGetValue(dst.m_ActionIndex, out var origin))
                    {
                        stats.MissingActionOrigins++;
                        if (!TryResolveOwnerOrigin(dst.m_Owner, tfRO, currentBuildingRO, ownerRO, out origin))
                        {
                            stats.OwnerOriginFallbackFailures++;
                            stats.SkippedNoOrigin++;
                            continue;
                        }

                        stats.OwnerOriginFallbacks++;
                    }

                    var buffer = dst.m_Buffer;
                    int originalCount = buffer.Length;
                    if (originalCount == 0)
                    {
                        stats.EmptyBuffers++;
                        continue;
                    }

                    stats.OriginalCandidates += originalCount;
                    if (IsCoolingDown(dst.m_Owner, refusalsRO, frame, settings))
                    {
                        stats.CooldownBuffers++;
                        stats.ClearedBuffers++;
                        Clear(ref dst.m_Buffer);
                        continue;
                    }

                    s_Scored.Clear();
                    s_UnknownPositionTargets.Clear();
                    s_KeptTargets.Clear();
                    int unknownFallbackLimit = UnknownPositionFallbackLimit(settings);

                    for (int k = 0; k < originalCount; k++)
                    {
                        var pathTarget = buffer[k];
                        if (!TryResolveCandidate(pathTarget, origin.Position, settings, tfRO, wpRO, freeRO, ownerRO, out var workplace, out var score, out var rejectReason))
                        {
                            stats.InvalidCandidates++;
                            if (rejectReason == "missing_workplace_entity")
                            {
                                stats.MissingWorkplace++;
                            }
                            else if (rejectReason == "missing_position")
                            {
                                stats.MissingPosition++;
                                AddUnknownPositionTarget(pathTarget, unknownFallbackLimit);
                            }
                            else if (rejectReason == "no_free_workplaces")
                            {
                                stats.NoFreeWorkplaces++;
                            }
                            continue;
                        }

                        stats.ValidCandidates++;
                        s_Scored.Add(new ScoredTarget
                        {
                            Target = pathTarget,
                            Workplace = workplace,
                            Score = score,
                            OriginalIndex = k
                        });
                    }

                    if (s_Scored.Count == 0)
                    {
                        stats.UnscoredPassthroughBuffers++;
                        stats.UnscoredPassthroughCandidates += originalCount;
                        stats.KeptCandidates += originalCount;
                        continue;
                    }

                    stats.ScoredBuffers++;
                    s_Scored.Sort((a, b) => b.Score.Utility.CompareTo(a.Score.Utility));
                    int topK = math.min(settings.TopK, s_Scored.Count);
                    int poolCount = math.min(topK * 2, s_Scored.Count);
                    int chosen = DrawSoftmaxIndex(frame, dst.m_Owner, dst.m_Target.m_Entity, dst.m_ActionIndex, poolCount, settings);

                    stats.ClearedBuffers++;
                    Clear(ref dst.m_Buffer);
                    AddKeptTarget(ref dst.m_Buffer, s_Scored[chosen].Target);
                    for (int s = 0; s < s_Scored.Count && s_KeptTargets.Count < topK; s++)
                    {
                        if (s == chosen)
                        {
                            continue;
                        }

                        AddKeptTarget(ref dst.m_Buffer, s_Scored[s].Target);
                    }

                    for (int u = 0; u < s_UnknownPositionTargets.Count; u++)
                    {
                        var unknown = s_UnknownPositionTargets[u];
                        if (ContainsTarget(unknown))
                        {
                            continue;
                        }

                        AddKeptTarget(ref dst.m_Buffer, unknown);
                        stats.UnknownPositionKept++;
                    }

                    stats.SelectedCandidates++;
                    stats.KeptCandidates += s_KeptTargets.Count;
                }
            }
            finally
            {
                JobSearchDebug.LogPrefilterStats(frame, stats, settings);
            }
        }

        private static int DrawSoftmaxIndex(uint frame, Entity owner, Entity target, int actionIndex, int poolCount, GravityAcceptParams settings)
        {
            EnsureWeightCapacity(poolCount);

            float maxUtility = float.NegativeInfinity;
            for (int i = 0; i < poolCount; i++)
            {
                maxUtility = math.max(maxUtility, s_Scored[i].Score.Utility);
            }

            double sum = 0d;
            float temperature = math.max(0.05f, settings.SoftmaxTemperature);
            for (int i = 0; i < poolCount; i++)
            {
                double weight = Math.Exp((s_Scored[i].Score.Utility - maxUtility) / temperature);
                s_Weights[i] = weight;
                sum += weight;
            }

            uint seed = JobSearchScoring.MakeSeed(frame, owner, target, actionIndex);
            var random = Unity.Mathematics.Random.CreateFromIndex(seed);
            double draw = random.NextDouble() * sum;
            double acc = 0d;
            for (int i = 0; i < poolCount; i++)
            {
                acc += s_Weights[i];
                if (acc >= draw)
                {
                    return i;
                }
            }

            return poolCount - 1;
        }

        private static int UnknownPositionFallbackLimit(GravityAcceptParams settings)
        {
            return math.clamp(settings.TopK / 2, 2, 8);
        }

        private static void AddUnknownPositionTarget(PathTarget target, int limit)
        {
            if (s_UnknownPositionTargets.Count >= limit)
            {
                return;
            }

            for (int i = 0; i < s_UnknownPositionTargets.Count; i++)
            {
                if (SameTarget(s_UnknownPositionTargets[i], target))
                {
                    return;
                }
            }

            s_UnknownPositionTargets.Add(target);
        }

        private static bool TryResolveCandidate(
            PathTarget pathTarget,
            float2 originXZ,
            GravityAcceptParams settings,
            ComponentLookup<Transform> tfRO,
            ComponentLookup<WorkProvider> wpRO,
            ComponentLookup<FreeWorkplaces> freeRO,
            ComponentLookup<Owner> ownerRO,
            out Entity workplace,
            out JobSearchScore score,
            out string rejectReason)
        {
            workplace = ResolveWorkplace(pathTarget, wpRO, freeRO, ownerRO);
            int total = GetTotalWorkplaces(wpRO, workplace);
            int free = GetFreeWorkplaces(freeRO, workplace);
            score = default;

            if (workplace == Entity.Null)
            {
                rejectReason = "missing_workplace_entity";
                score = JobSearchScoring.Score(settings, 0f, 0.01f, total, free);
                return false;
            }

            if (free <= 0)
            {
                rejectReason = "no_free_workplaces";
                score = JobSearchScoring.Score(settings, 0f, 0.01f, total, free);
                return false;
            }

            if (!TryXZOrOwner(pathTarget.m_Entity, tfRO, ownerRO, out var targetXZ) &&
                !TryXZOrOwner(workplace, tfRO, ownerRO, out targetXZ) &&
                !TryXZOrOwner(pathTarget.m_Target, tfRO, ownerRO, out targetXZ))
            {
                rejectReason = "missing_position";
                score = JobSearchScoring.Score(settings, 0f, 0.01f, total, free);
                return false;
            }

            float meters = math.distance(originXZ, targetXZ);
            float minutes = JobSearchScoring.EstimateMinutesFromMeters(meters, settings.EstimatedCommuteSpeedKmh);
            score = JobSearchScoring.Score(settings, meters, minutes, total, free);
            rejectReason = string.Empty;
            return true;
        }

        private static Entity ResolveWorkplace(
            PathTarget pathTarget,
            ComponentLookup<WorkProvider> wpRO,
            ComponentLookup<FreeWorkplaces> freeRO,
            ComponentLookup<Owner> ownerRO)
        {
            if (pathTarget.m_Entity != Entity.Null &&
                (wpRO.HasComponent(pathTarget.m_Entity) || freeRO.HasComponent(pathTarget.m_Entity)))
            {
                return pathTarget.m_Entity;
            }

            if (pathTarget.m_Target != Entity.Null &&
                (wpRO.HasComponent(pathTarget.m_Target) || freeRO.HasComponent(pathTarget.m_Target)))
            {
                return pathTarget.m_Target;
            }

            if (TryResolveOwnedWorkplace(pathTarget.m_Entity, wpRO, freeRO, ownerRO, out var workplace) ||
                TryResolveOwnedWorkplace(pathTarget.m_Target, wpRO, freeRO, ownerRO, out workplace))
            {
                return workplace;
            }

            return pathTarget.m_Entity != Entity.Null ? pathTarget.m_Entity : pathTarget.m_Target;
        }

        private static bool TryResolveOwnedWorkplace(
            Entity entity,
            ComponentLookup<WorkProvider> wpRO,
            ComponentLookup<FreeWorkplaces> freeRO,
            ComponentLookup<Owner> ownerRO,
            out Entity workplace)
        {
            workplace = Entity.Null;
            if (entity == Entity.Null || !ownerRO.HasComponent(entity))
            {
                return false;
            }

            Entity owner = ownerRO[entity].m_Owner;
            if (owner == Entity.Null)
            {
                return false;
            }

            if (wpRO.HasComponent(owner) || freeRO.HasComponent(owner))
            {
                workplace = owner;
                return true;
            }

            if (ownerRO.HasComponent(owner))
            {
                Entity grandOwner = ownerRO[owner].m_Owner;
                if (grandOwner != Entity.Null &&
                    (wpRO.HasComponent(grandOwner) || freeRO.HasComponent(grandOwner)))
                {
                    workplace = grandOwner;
                    return true;
                }
            }

            return false;
        }

        private static bool IsCoolingDown(Entity owner, ComponentLookup<RefusedLongCommute> refusalsRO, uint frame, GravityAcceptParams settings)
        {
            if (owner == Entity.Null || settings.MaxDailyRejections <= 0 || !refusalsRO.HasComponent(owner))
            {
                return false;
            }

            uint cooldownFrames = JobSearchScoring.FramesFromHours(settings.RetryCooldownHours);
            return frame - refusalsRO[owner].LastRefusalFrame < cooldownFrames;
        }

        private static bool TryXZ(Entity entity, ComponentLookup<Transform> tfRO, out float2 xz)
        {
            xz = default;
            if (entity == Entity.Null || !tfRO.HasComponent(entity))
            {
                return false;
            }

            var p = tfRO[entity].m_Position;
            xz = new float2(p.x, p.z);
            return true;
        }

        private static bool TryXZOrOwner(
            Entity entity,
            ComponentLookup<Transform> tfRO,
            ComponentLookup<Owner> ownerRO,
            out float2 xz)
        {
            if (TryXZ(entity, tfRO, out xz))
            {
                return true;
            }

            if (entity == Entity.Null || !ownerRO.HasComponent(entity))
            {
                return false;
            }

            Entity owner = ownerRO[entity].m_Owner;
            if (TryXZ(owner, tfRO, out xz))
            {
                return true;
            }

            if (owner != Entity.Null && ownerRO.HasComponent(owner))
            {
                return TryXZ(ownerRO[owner].m_Owner, tfRO, out xz);
            }

            return false;
        }

        private static bool TryResolveOwnerOrigin(
            Entity owner,
            ComponentLookup<Transform> tfRO,
            ComponentLookup<CurrentBuilding> currentBuildingRO,
            ComponentLookup<Owner> ownerRO,
            out OriginInfo origin)
        {
            origin = default;
            if (TryResolveOriginEntity(owner, tfRO, currentBuildingRO, out origin))
            {
                return true;
            }

            if (owner != Entity.Null &&
                ownerRO.HasComponent(owner) &&
                TryResolveOriginEntity(ownerRO[owner].m_Owner, tfRO, currentBuildingRO, out origin))
            {
                return true;
            }

            return false;
        }

        private static bool TryResolveOriginEntity(
            Entity entity,
            ComponentLookup<Transform> tfRO,
            ComponentLookup<CurrentBuilding> currentBuildingRO,
            out OriginInfo origin)
        {
            origin = default;
            if (TryXZ(entity, tfRO, out var pos))
            {
                origin = new OriginInfo
                {
                    Entity = entity,
                    Position = pos
                };
                return true;
            }

            if (entity != Entity.Null &&
                currentBuildingRO.HasComponent(entity) &&
                TryXZ(currentBuildingRO[entity].m_CurrentBuilding, tfRO, out pos))
            {
                origin = new OriginInfo
                {
                    Entity = currentBuildingRO[entity].m_CurrentBuilding,
                    Position = pos
                };
                return true;
            }

            return false;
        }

        private static int GetTotalWorkplaces(ComponentLookup<WorkProvider> wpRO, Entity building)
        {
            return building != Entity.Null && wpRO.HasComponent(building) ? wpRO[building].m_MaxWorkers : 0;
        }

        private static int GetFreeWorkplaces(ComponentLookup<FreeWorkplaces> freeRO, Entity building)
        {
            return building != Entity.Null && freeRO.HasComponent(building) ? freeRO[building].Count : 0;
        }

        private static void EnsureWeightCapacity(int count)
        {
            if (s_Weights.Length >= count)
            {
                return;
            }

            Array.Resize(ref s_Weights, math.ceilpow2(count));
        }

        private static bool ContainsTarget(PathTarget target)
        {
            for (int i = 0; i < s_KeptTargets.Count; i++)
            {
                if (SameTarget(s_KeptTargets[i], target))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameTarget(PathTarget a, PathTarget b)
        {
            return a.m_Entity == b.m_Entity && a.m_Target == b.m_Target;
        }

        private static void AddKeptTarget(ref Unity.Collections.LowLevel.Unsafe.UnsafeList<PathTarget> list, PathTarget target)
        {
            Add(ref list, target);
            s_KeptTargets.Add(target);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Clear(ref Unity.Collections.LowLevel.Unsafe.UnsafeList<PathTarget> list)
        {
            list.Length = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Add(ref Unity.Collections.LowLevel.Unsafe.UnsafeList<PathTarget> list, PathTarget value)
        {
            int idx = list.Length;
            list.Length = idx + 1;
            list.ElementAt(idx) = value;
        }
    }
}
