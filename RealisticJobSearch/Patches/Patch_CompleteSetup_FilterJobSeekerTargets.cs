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
        // Tune these via your params singleton if you want:
        static readonly float AlphaJobs = Mod.m_Setting.alpha_jobs;
        static readonly float BetaMinute = Mod.m_Setting.beta_minute;
        static readonly float WTotal = Mod.m_Setting.weight_total_jobs;  // your current test
        static readonly float WFree = Mod.m_Setting.weight_free_jobs;
        static readonly int TopK = 12;      // cap list size for performance
        static readonly float MinKeepP = 0.05f;  // soft floor (~8%)

        // FieldRefs into private members
        static readonly AccessTools.FieldRef<PathfindSetupSystem, NativeList<PathfindSetupSystem.SetupListItem>> _setupListRef =
            AccessTools.FieldRefAccess<PathfindSetupSystem, NativeList<PathfindSetupSystem.SetupListItem>>("m_SetupList");

        static readonly AccessTools.FieldRef<PathfindSetupSystem, Unity.Jobs.JobHandle> _setupDepsRef =
            AccessTools.FieldRefAccess<PathfindSetupSystem, Unity.Jobs.JobHandle>("m_SetupDependencies");

        // helpers now take ComponentLookup<T>
        static bool TryXZ(Entity e, ComponentLookup<Transform> tfRO, out float2 xz)
        {
            xz = default;
            if (e == Entity.Null || !tfRO.HasComponent(e)) return false;
            var p = tfRO[e].m_Position;
            xz = new float2(p.x, p.z);
            return true;
        }

        static int GetTotalWorkplaces(ComponentLookup<WorkProvider> wpRO, Entity b)
            => wpRO.HasComponent(b) ? wpRO[b].m_MaxWorkers : 0;

        static int GetFreeWorkplaces(ComponentLookup<FreeWorkplaces> freeRO, Entity b)
            => freeRO.HasComponent(b) ? freeRO[b].Count : 0;

        static void Prefix(PathfindSetupSystem __instance)
        {
            // Ensure the jobs that filled m_SetupList buffers are finished, just like the original will do.
            ref var setupDeps = ref _setupDepsRef(__instance);
            setupDeps.Complete();

            ref var setupList = ref _setupListRef(__instance);
            if (setupList.Length == 0) return;

            var tfRO = __instance.GetComponentLookup<Transform>(isReadOnly: true);
            var wpRO = __instance.GetComponentLookup<WorkProvider>(isReadOnly: true);
            var freeRO = __instance.GetComponentLookup<FreeWorkplaces>(isReadOnly: true);

            TimeSystem timeSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<TimeSystem>();
            DateTime currentDateTime = timeSystem.GetCurrentDateTime();
            uint seed = (uint)(currentDateTime.Minute * 1000 + currentDateTime.Hour * 100 + currentDateTime.Year);
            Unity.Mathematics.Random random = Unity.Mathematics.Random.CreateFromIndex(seed);

            // 1) Build origin position by ActionIndex (from the paired CurrentLocation item)
            //    We’ll use this to estimate straight-line minutes for each candidate.
            var originByAction = new Dictionary<int, float2>(128);
            for (int i = 0; i < setupList.Length; i++)
            {
                ref var item = ref setupList.ElementAt(i);
                if (item.m_ActionStart && item.m_Target.m_Type == SetupTargetType.CurrentLocation)
                {
                    var originEntity = item.m_Target.m_Entity != Entity.Null ? item.m_Target.m_Entity : item.m_Owner;
                    if (TryXZ(originEntity, tfRO, out var pos))
                        originByAction[item.m_ActionIndex] = pos;
                }
            }

            // 2) Filter only the JobSeekerTo items
            for (int i = 0; i < setupList.Length; i++)
            {
                ref var dst = ref setupList.ElementAt(i);
                if (dst.m_ActionStart) continue; // we only filter end-target buffers
                if (dst.m_Target.m_Type != SetupTargetType.JobSeekerTo) continue;
                if (!originByAction.TryGetValue(dst.m_ActionIndex, out var originXZ)) continue;

                // Pull target list
                var buf = dst.m_Buffer;
                if (buf.Length == 0) continue;

                // Materialize, score, and keep best
                var scored = new List<(PathTarget t, float U)>(buf.Length);

                for (int k = 0; k < buf.Length; k++)
                {
                    var pt = buf[k];
                    var targetEnt = pt.m_Entity;
                    if (targetEnt == Entity.Null) continue;

                    // distance -> rough minutes (7 m/s ≈ 25 km/h)
                    float minutes = 0f;
                    if (TryXZ(targetEnt, tfRO, out var txz))
                    {
                        var meters = math.distance(originXZ, txz);
                        minutes = (meters / 7f) / 60f;
                    }

                    // Mass blend (use real components if present)
                    float total = GetTotalWorkplaces(wpRO, targetEnt);
                    float free = GetFreeWorkplaces(freeRO, targetEnt);
                    float mass = math.max(1f, WTotal * total + WFree * free);

                    float U = AlphaJobs * math.log(1f + mass) - BetaMinute * minutes;

                    scored.Add((pt, U));
                }

                if (scored.Count == 0) { Clear(ref dst.m_Buffer); continue; }

                // Sort descending by U
                scored.Sort((a, b) => b.U.CompareTo(a.U));

                // --------- D) Softmax draw (added) ----------
                // We’ll draw one candidate proportional to exp(U / Tau), then fill the rest by next-best.
                const float Tau = 0.45f; // temperature for softmax; lower = more greedy

                // Build a small pool (top 2*TopK or all if smaller)
                int poolCount = math.min(TopK * 2, scored.Count);
                if (poolCount <= 0) { Clear(ref dst.m_Buffer); continue; }

                // Numerical stability: shift by maxU
                float maxU = float.NegativeInfinity;
                for (int p = 0; p < poolCount; p++)
                    maxU = math.max(maxU, scored[p].U);

                double sum = 0d;
                var weights = new double[poolCount];
                for (int p = 0; p < poolCount; p++)
                {
                    double w = Math.Exp((scored[p].U - maxU) / Tau);
                    weights[p] = w;
                    sum += w;
                }

                // Random draw
                double r = random.NextDouble() * sum;
                int chosen = 0; double acc = 0d;
                for (; chosen < poolCount; chosen++)
                {
                    acc += weights[chosen];
                    if (acc >= r) break;
                }
                if (chosen >= poolCount) chosen = poolCount - 1;

                // Write chosen first
                SetAt(ref dst.m_Buffer, 0, scored[chosen].t);
                int writeCountSoft = 1;

                // Fill remaining with next-best (skip the chosen), up to TopK
                int maxKeep = math.min(TopK, scored.Count);
                for (int s = 0; s < scored.Count && writeCountSoft < maxKeep; s++)
                {
                    if (s == chosen) continue;
                    Add(ref dst.m_Buffer, scored[s].t);
                    writeCountSoft++;
                }

                Truncate(ref dst.m_Buffer, writeCountSoft);
                // --------- end D) ---------------------------

                // (No other logic changed)
            }
        }

        static float InvLogit(float p) => math.log(p / math.max(1e-6f, 1f - p)); // maps probability to U

        // --- UnsafeList helpers (no allocations) ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Clear(ref Unity.Collections.LowLevel.Unsafe.UnsafeList<PathTarget> list) => list.Length = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Truncate(ref Unity.Collections.LowLevel.Unsafe.UnsafeList<PathTarget> list, int newLen)
        {
            list.Length = math.clamp(newLen, 0, list.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SetAt(ref Unity.Collections.LowLevel.Unsafe.UnsafeList<PathTarget> list, int idx, in PathTarget val)
        {
            if (idx >= list.Length) list.Length = idx + 1;
            list.ElementAt(idx) = val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Add(ref Unity.Collections.LowLevel.Unsafe.UnsafeList<PathTarget> list, in PathTarget val)
        {
            int idx = list.Length;
            list.Length = idx + 1;
            list.ElementAt(idx) = val;
        }
    }
}
