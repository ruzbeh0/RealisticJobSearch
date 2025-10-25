#nullable enable
using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Pathfind;
using Game.Simulation;
using RealisticJobSearch.Systems;
using System;
using System.Globalization;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch
{
    /// Tag to ensure we only count each completed/accepted path once
    public struct CountedThisPath : IComponentData { }

    /// Collects origin→destination straight-line distances for accepted job paths
    public partial class MetricsSystem : GameSystemBase
    {
        // Rolling stats
        long _acceptedCount;
        double _sumMeters;
        double _sumMinutes;

        // Simple distance histogram (km bins)
        // 0: 0–1 km, 1: 1–2 km, ... 29: 29–30 km, 30: 30+ km
        readonly int[] _hist = new int[31];

        uint _lastWriteFrame;
        long _lastWriteCount;

        string _csvPath = Path.Combine(Mod.outputPath, "RealisticJobSearch_metrics.csv");
        bool _wroteHeader;

        protected override void OnCreate()
        {
            base.OnCreate();
            // Make sure file has a header
            TryWriteHeader();
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            // One day (or month) in-game is '262144' ticks
            return TimeSystem.kTicksPerDay / 32;
        }

        protected override void OnUpdate()
        {
            var sim = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = sim.frameIndex;

            var tf = GetComponentLookup<Game.Objects.Transform>(true);
            var bLookup = GetComponentLookup<Game.Buildings.Building>(true);
            var curLookup = GetComponentLookup<CurrentBuilding>(true);

            // JobSeekers that still have a PathInformation (not filtered yet for completion)
            var q = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Agents.JobSeeker>(),
                ComponentType.ReadOnly<Game.Pathfind.PathInformation>(),
                ComponentType.Exclude<RealisticJobSearch.CountedThisPath>());

            var ents = q.ToEntityArray(Allocator.Temp);
            var infos = q.ToComponentDataArray<Game.Pathfind.PathInformation>(Allocator.Temp);

            //Mod.log.Info($"[RJS Metrics] Frame {frame}: Found {ents.Length} seekers w/ PathInformation (accepted so far {_acceptedCount})");

            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                var info = infos[i];

                // Complete in this build = not Pending and not Failed
                if ((info.m_State & Game.Pathfind.PathFlags.Pending) != 0) continue;
                if ((info.m_State & Game.Pathfind.PathFlags.Failed) != 0) continue;
                //Mod.log.Info($"[RJS Metrics]   Counting path for entity {e.Index}");
                // Resolve origin position
                if (!TryGetWorldXZ(info.m_Origin, tf, bLookup, out float2 oPos))
                {
                    // fallback: seeker’s current building (home or wherever they are)
                    if (curLookup.HasComponent(e))
                    {
                        var cb = curLookup[e].m_CurrentBuilding;
                        if (!TryGetWorldXZ(cb, tf, bLookup, out oPos)) continue; // give up if still no pos
                    }
                    else continue;
                }

                // Resolve destination position
                if (!TryGetWorldXZ(info.m_Destination, tf, bLookup, out float2 dPos))
                {
                    // Dest should be a building; try anyway via Building -> Transform
                    // If we get here, and still no pos, skip
                    continue;
                }

                float meters = math.distance(oPos, dPos);
                float minutes = math.max(0.01f, info.m_Duration);

                _acceptedCount++;
                _sumMeters += meters;
                _sumMinutes += minutes;
                Bin(meters);

                // Prevent double counting while this PathInformation remains
                EntityManager.AddComponent<RealisticJobSearch.CountedThisPath>(e);
            }

            ents.Dispose(); infos.Dispose();

            // Clean up CountedThisPath when the path is gone (e.g., next frame after hire)
            var q2 = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RealisticJobSearch.CountedThisPath>(),
                ComponentType.Exclude<Game.Pathfind.PathInformation>());
            var done = q2.ToEntityArray(Allocator.Temp);
            for (int k = 0; k < done.Length; k++)
                EntityManager.RemoveComponent<RealisticJobSearch.CountedThisPath>(done[k]);
            done.Dispose();

            // Append a row periodically (your method already handles "no data yet")
            TryAppendRow(frame);
            _lastWriteFrame = frame;
            _lastWriteCount = _acceptedCount;
        }

        // Helper: get world XZ for any entity (Transform if present; else Building proxy)
        static bool TryGetWorldXZ(Entity ent,
                                  ComponentLookup<Game.Objects.Transform> tf,
                                  ComponentLookup<Game.Buildings.Building> bLookup,
                                  out float2 pos)
        {
            pos = default;
            if (ent == Entity.Null) return false;

            if (tf.HasComponent(ent))
            {
                var t = tf[ent].m_Position;
                pos = new float2(t.x, t.z);
                return true;
            }

            if (bLookup.HasComponent(ent))
            {
                // Building might lack Transform in rare cases; but most have it, or you can synthesize
                // a coarse proxy from the building data if needed. Here we only accept exact Transform to be safe.
                // If you prefer a proxy, uncomment the fallback:
                var b = bLookup[ent];
                pos = new float2(b.m_CurvePosition, b.m_CurvePosition * 19.0f);
                return true;
            }

            return false;
        }

        void Bin(float meters)
        {
            int km = (int)math.floor(meters / 1000f);
            int idx = math.clamp(km, 0, _hist.Length - 1);
            _hist[idx]++;
        }

        void TryWriteHeader()
        {
            try
            {
                if (!File.Exists(_csvPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_csvPath)!);
                    using var sw = new StreamWriter(_csvPath, append: false);
                    sw.WriteLine("timestamp,frame,count,avg_meters,avg_minutes,p50_km,p90_km,p95_km,p99_km,hist_0_1km,hist_1_2km,...,hist_30km_plus");
                    _wroteHeader = true;
                }
                else _wroteHeader = true;
            }
            catch (Exception ex)
            {
                Mod.log.Info("[RJS Metrics] Failed to create CSV: " + ex.Message);
            }
        }

        void TryAppendRow(uint frame)
        {
            if (!_wroteHeader) TryWriteHeader();
            if (_acceptedCount == 0) return;

            // Percentiles from histogram
            float p50 = QuantileKm(0.50);
            float p90 = QuantileKm(0.90);
            float p95 = QuantileKm(0.95);
            float p99 = QuantileKm(0.99);

            try
            {
                using var sw = new StreamWriter(_csvPath, append: true);
                sw.Write(DateTime.Now.ToString("s"));
                sw.Write(','); sw.Write(frame.ToString(CultureInfo.InvariantCulture));
                sw.Write(','); sw.Write(_acceptedCount.ToString(CultureInfo.InvariantCulture));
                sw.Write(','); sw.Write((_sumMeters / _acceptedCount).ToString("F2", CultureInfo.InvariantCulture));
                sw.Write(','); sw.Write((_sumMinutes / _acceptedCount).ToString("F2", CultureInfo.InvariantCulture));
                sw.Write(','); sw.Write(p50.ToString("F2", CultureInfo.InvariantCulture));
                sw.Write(','); sw.Write(p90.ToString("F2", CultureInfo.InvariantCulture));
                sw.Write(','); sw.Write(p95.ToString("F2", CultureInfo.InvariantCulture));
                sw.Write(','); sw.Write(p99.ToString("F2", CultureInfo.InvariantCulture));
                // dump histogram
                for (int i = 0; i < _hist.Length; i++)
                {
                    sw.Write(','); sw.Write(_hist[i].ToString(CultureInfo.InvariantCulture));
                }
                sw.WriteLine();
            }
            catch (Exception ex)
            {
                Mod.log.Info("[RJS Metrics] Failed to append CSV: " + ex.Message);
            }
        }

        float QuantileKm(double q)
        {
            if (_acceptedCount == 0) return 0f;
            long target = (long)Math.Ceiling(q * _acceptedCount);
            long cum = 0;
            for (int i = 0; i < _hist.Length; i++)
            {
                cum += _hist[i];
                if (cum >= target) return i == _hist.Length - 1 ? i : (i + 0.5f); // mid-bin estimate
            }
            return _hist.Length - 1;
        }

        protected override void OnDestroy()
        {
            // Final flush
            TryAppendRow(World.GetExistingSystemManaged<SimulationSystem>().frameIndex);
            base.OnDestroy();
        }
    }
}
