#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch
{
    public static class JobSearchDebug
    {
        private const string Prefix = "[RJS Summary] ";
        private static readonly object s_Lock = new object();
        private static readonly Aggregate s_Current = new Aggregate();

        public static bool Enabled => Mod.m_Setting != null && Mod.m_Setting.debug;

        public struct PrefilterDebugStats
        {
            public int Calls;
            public int SetupItems;
            public int CurrentLocationStarts;
            public int OriginsMapped;
            public int JobSeekerTargets;
            public int MissingActionOrigins;
            public int OwnerOriginFallbacks;
            public int OwnerOriginFallbackFailures;
            public int SkippedNoOrigin;
            public int EmptyBuffers;
            public int CooldownBuffers;
            public int ScoredBuffers;
            public int ClearedBuffers;
            public int OriginalCandidates;
            public int ValidCandidates;
            public int InvalidCandidates;
            public int KeptCandidates;
            public int SelectedCandidates;
            public int UnknownPositionKept;
            public int UnscoredPassthroughBuffers;
            public int UnscoredPassthroughCandidates;
            public int MissingWorkplace;
            public int MissingPosition;
            public int NoFreeWorkplaces;

            public bool HasRows => Calls != 0 || JobSeekerTargets != 0 || OriginalCandidates != 0;
        }

        public static void ResetSession()
        {
            lock (s_Lock)
            {
                s_Current.Reset();
            }

            if (Enabled)
            {
                Mod.log.Info(Prefix + "debug summaries enabled; grouped_rows=" + GetFlushDecisionCount().ToString(CultureInfo.InvariantCulture));
            }
        }

        public static void Flush(string reason)
        {
            if (!Enabled)
            {
                return;
            }

            lock (s_Lock)
            {
                FlushLocked(reason);
            }
        }

        public static void LogDecision(
            uint frame,
            string phase,
            Entity seeker,
            Entity origin,
            Entity target,
            string result,
            string reason,
            int rank,
            int candidateCount,
            int keptCount,
            in JobSearchScore score,
            in GravityAcceptParams settings)
        {
            if (!Enabled)
            {
                return;
            }

            lock (s_Lock)
            {
                s_Current.Add(frame, phase, seeker, origin, target, result, reason, score, settings, Mod.GameplayEnabled);
                if (s_Current.Count >= GetFlushDecisionCount())
                {
                    FlushLocked("interval");
                }
            }
        }

        public static void LogPrefilterStats(uint frame, in PrefilterDebugStats stats, in GravityAcceptParams settings)
        {
            if (!Enabled || !stats.HasRows)
            {
                return;
            }

            lock (s_Lock)
            {
                s_Current.AddPrefilter(frame, stats, settings, Mod.GameplayEnabled);
            }
        }

        private static int GetFlushDecisionCount()
        {
            return math.clamp(Mod.m_Setting?.debug_summary_decisions ?? 500, 50, 5000);
        }

        private static void FlushLocked(string reason)
        {
            if (!s_Current.HasData)
            {
                return;
            }

            Mod.log.Info(Prefix + s_Current.BuildOverview(reason));
            Mod.log.Info(Prefix + s_Current.BuildResults());
            Mod.log.Info(Prefix + s_Current.BuildReasons());
            Mod.log.Info(Prefix + s_Current.BuildRealTargetStats());
            Mod.log.Info(Prefix + s_Current.BuildNullTargetStats());

            string prefilter = s_Current.BuildPrefilterStats();
            if (!string.IsNullOrEmpty(prefilter))
            {
                Mod.log.Info(Prefix + prefilter);
            }

            string bins = s_Current.BuildAcceptedRealBins();
            if (!string.IsNullOrEmpty(bins))
            {
                Mod.log.Info(Prefix + bins);
            }

            string topTargets = s_Current.BuildTopTargets();
            if (!string.IsNullOrEmpty(topTargets))
            {
                Mod.log.Info(Prefix + topTargets);
            }

            s_Current.Reset();
        }

        private static string EntityId(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index.ToString(CultureInfo.InvariantCulture) + ":" + entity.Version.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatPct(int numerator, int denominator)
        {
            if (denominator <= 0)
            {
                return "n/a";
            }

            return (100d * numerator / denominator).ToString("F1", CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatKm(double meters)
        {
            return (meters / 1000d).ToString("F2", CultureInfo.InvariantCulture);
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("F2", CultureInfo.InvariantCulture);
        }

        private static string FormatProb(double value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private sealed class Aggregate
        {
            private readonly Dictionary<string, int> m_Phases = new Dictionary<string, int>();
            private readonly Dictionary<string, int> m_Results = new Dictionary<string, int>();
            private readonly Dictionary<string, int> m_Reasons = new Dictionary<string, int>();
            private readonly Dictionary<string, TargetStats> m_Targets = new Dictionary<string, TargetStats>();
            private readonly HashSet<string> m_Seekers = new HashSet<string>();
            private readonly HashSet<string> m_RealTargets = new HashSet<string>();
            private readonly DistanceStats m_Real = new DistanceStats();
            private readonly DistanceStats m_AcceptedReal = new DistanceStats();
            private readonly int[] m_AcceptedRealKmBins = new int[31];

            private DateTime m_StartWallTime;
            private DateTime m_EndWallTime;
            private uint m_StartFrame;
            private uint m_EndFrame;
            private PrefilterDebugStats m_Prefilter;

            public int Count { get; private set; }
            public bool HasData => Count > 0 || m_Prefilter.HasRows;
            private int m_Accepted;
            private int m_Rejected;
            private int m_PassedThrough;
            private int m_RealTargetRows;
            private int m_NullTargetRows;
            private int m_RealAccepted;
            private int m_NullAccepted;
            private int m_NullRejected;
            private int m_NullPassedThrough;
            private int m_RealAtMinProbability;
            private int m_RealAtMaxProbability;
            private int m_NullOriginRows;
            private int m_NullOriginPresentRows;
            private int m_NullZeroDistanceRows;
            private int m_NullNonZeroDistanceRows;
            private int m_NullZeroDurationRows;
            private int m_NullNonZeroDurationRows;
            private GravityAcceptParams m_LastSettings;
            private bool m_LastModEnabled;

            public void Reset()
            {
                Count = 0;
                m_Accepted = 0;
                m_Rejected = 0;
                m_PassedThrough = 0;
                m_RealTargetRows = 0;
                m_NullTargetRows = 0;
                m_RealAccepted = 0;
                m_NullAccepted = 0;
                m_NullRejected = 0;
                m_NullPassedThrough = 0;
                m_RealAtMinProbability = 0;
                m_RealAtMaxProbability = 0;
                m_NullOriginRows = 0;
                m_NullOriginPresentRows = 0;
                m_NullZeroDistanceRows = 0;
                m_NullNonZeroDistanceRows = 0;
                m_NullZeroDurationRows = 0;
                m_NullNonZeroDurationRows = 0;
                m_Phases.Clear();
                m_Results.Clear();
                m_Reasons.Clear();
                m_Targets.Clear();
                m_Seekers.Clear();
                m_RealTargets.Clear();
                m_Real.Reset();
                m_AcceptedReal.Reset();
                Array.Clear(m_AcceptedRealKmBins, 0, m_AcceptedRealKmBins.Length);
                m_StartWallTime = default;
                m_EndWallTime = default;
                m_StartFrame = 0u;
                m_EndFrame = 0u;
                m_Prefilter = default;
                m_LastSettings = default;
                m_LastModEnabled = false;
            }

            public void Add(
                uint frame,
                string phase,
                Entity seeker,
                Entity origin,
                Entity target,
                string result,
                string reason,
                in JobSearchScore score,
                in GravityAcceptParams settings,
                bool modEnabled)
            {
                DateTime now = DateTime.Now;
                if (!HasData)
                {
                    m_StartWallTime = now;
                    m_StartFrame = frame;
                }

                Count++;
                m_EndWallTime = now;
                m_EndFrame = frame;
                m_LastSettings = settings;
                m_LastModEnabled = modEnabled;

                Increment(m_Phases, phase);
                Increment(m_Results, result);
                Increment(m_Reasons, reason);
                m_Seekers.Add(EntityId(seeker));

                bool accepted = result == "accepted";
                bool rejected = result == "rejected";
                bool passedThrough = result == "passed_through";
                if (accepted) m_Accepted++;
                if (rejected) m_Rejected++;
                if (passedThrough) m_PassedThrough++;

                bool pathResult = phase == "path_result";
                bool realTarget = target != Entity.Null;
                if (!realTarget)
                {
                    m_NullTargetRows++;
                    if (accepted) m_NullAccepted++;
                    if (rejected) m_NullRejected++;
                    if (passedThrough) m_NullPassedThrough++;
                    if (origin == Entity.Null) m_NullOriginRows++;
                    else m_NullOriginPresentRows++;
                    if (score.Meters <= 0.01f) m_NullZeroDistanceRows++;
                    else m_NullNonZeroDistanceRows++;
                    if (score.Minutes <= 0.02f) m_NullZeroDurationRows++;
                    else m_NullNonZeroDurationRows++;
                    return;
                }

                string targetId = EntityId(target);
                if (!pathResult)
                {
                    return;
                }

                m_RealTargetRows++;
                m_RealTargets.Add(targetId);
                m_Real.Add(score);
                if (score.Probability <= settings.MinAccept + 0.0001f) m_RealAtMinProbability++;
                if (score.Probability >= settings.MaxAccept - 0.0001f) m_RealAtMaxProbability++;

                if (accepted)
                {
                    m_RealAccepted++;
                    m_AcceptedReal.Add(score);
                    int bin = math.clamp((int)math.floor(score.Meters / 1000f), 0, m_AcceptedRealKmBins.Length - 1);
                    m_AcceptedRealKmBins[bin]++;
                }

                if (!m_Targets.TryGetValue(targetId, out var targetStats))
                {
                    targetStats = new TargetStats();
                    m_Targets[targetId] = targetStats;
                }
                targetStats.Add(accepted, score);
            }

            public void AddPrefilter(
                uint frame,
                in PrefilterDebugStats stats,
                in GravityAcceptParams settings,
                bool modEnabled)
            {
                DateTime now = DateTime.Now;
                if (!HasData)
                {
                    m_StartWallTime = now;
                    m_StartFrame = frame;
                }

                m_EndWallTime = now;
                m_EndFrame = frame;
                m_LastSettings = settings;
                m_LastModEnabled = modEnabled;

                m_Prefilter.Calls += stats.Calls;
                m_Prefilter.SetupItems += stats.SetupItems;
                m_Prefilter.CurrentLocationStarts += stats.CurrentLocationStarts;
                m_Prefilter.OriginsMapped += stats.OriginsMapped;
                m_Prefilter.JobSeekerTargets += stats.JobSeekerTargets;
                m_Prefilter.MissingActionOrigins += stats.MissingActionOrigins;
                m_Prefilter.OwnerOriginFallbacks += stats.OwnerOriginFallbacks;
                m_Prefilter.OwnerOriginFallbackFailures += stats.OwnerOriginFallbackFailures;
                m_Prefilter.SkippedNoOrigin += stats.SkippedNoOrigin;
                m_Prefilter.EmptyBuffers += stats.EmptyBuffers;
                m_Prefilter.CooldownBuffers += stats.CooldownBuffers;
                m_Prefilter.ScoredBuffers += stats.ScoredBuffers;
                m_Prefilter.ClearedBuffers += stats.ClearedBuffers;
                m_Prefilter.OriginalCandidates += stats.OriginalCandidates;
                m_Prefilter.ValidCandidates += stats.ValidCandidates;
                m_Prefilter.InvalidCandidates += stats.InvalidCandidates;
                m_Prefilter.KeptCandidates += stats.KeptCandidates;
                m_Prefilter.SelectedCandidates += stats.SelectedCandidates;
                m_Prefilter.UnknownPositionKept += stats.UnknownPositionKept;
                m_Prefilter.UnscoredPassthroughBuffers += stats.UnscoredPassthroughBuffers;
                m_Prefilter.UnscoredPassthroughCandidates += stats.UnscoredPassthroughCandidates;
                m_Prefilter.MissingWorkplace += stats.MissingWorkplace;
                m_Prefilter.MissingPosition += stats.MissingPosition;
                m_Prefilter.NoFreeWorkplaces += stats.NoFreeWorkplaces;
            }

            public string BuildOverview(string flushReason)
            {
                double wallSeconds = Math.Max(0d, (m_EndWallTime - m_StartWallTime).TotalSeconds);
                uint frameSpan = m_EndFrame >= m_StartFrame ? m_EndFrame - m_StartFrame : 0u;
                return "flush=" + flushReason +
                       " rows=" + Count.ToString(CultureInfo.InvariantCulture) +
                       " wall=" + m_StartWallTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "-" + m_EndWallTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                       " wall_seconds=" + wallSeconds.ToString("F1", CultureInfo.InvariantCulture) +
                       " frames=" + m_StartFrame.ToString(CultureInfo.InvariantCulture) + "-" + m_EndFrame.ToString(CultureInfo.InvariantCulture) +
                       " frame_span=" + frameSpan.ToString(CultureInfo.InvariantCulture) +
                       " seekers=" + m_Seekers.Count.ToString(CultureInfo.InvariantCulture) +
                       " real_targets=" + m_RealTargets.Count.ToString(CultureInfo.InvariantCulture) +
                       " prefilter_calls=" + m_Prefilter.Calls.ToString(CultureInfo.InvariantCulture) +
                       " mod_enabled=" + (m_LastModEnabled ? "true" : "false") +
                       " settings=alpha:" + FormatProb(m_LastSettings.AlphaJobs) +
                       ",beta:" + FormatProb(m_LastSettings.BetaMinute) +
                       ",w_total:" + FormatProb(m_LastSettings.WeightTotalJobs) +
                       ",w_free:" + FormatProb(m_LastSettings.WeightFreeJobs) +
                       ",min:" + FormatProb(m_LastSettings.MinAccept) +
                       ",max:" + FormatProb(m_LastSettings.MaxAccept) +
                       ",top_k:" + m_LastSettings.TopK.ToString(CultureInfo.InvariantCulture) +
                       ",tau:" + FormatProb(m_LastSettings.SoftmaxTemperature);
            }

            public string BuildResults()
            {
                return "results rows=" + Count.ToString(CultureInfo.InvariantCulture) +
                       " accepted=" + m_Accepted.ToString(CultureInfo.InvariantCulture) +
                       " rejected=" + m_Rejected.ToString(CultureInfo.InvariantCulture) +
                       " passed_through=" + m_PassedThrough.ToString(CultureInfo.InvariantCulture) +
                       " acceptance_rate=" + FormatPct(m_Accepted, m_Accepted + m_Rejected) +
                       " phases=" + BuildCounts(m_Phases) +
                       " result_counts=" + BuildCounts(m_Results);
            }

            public string BuildReasons()
            {
                return "reasons " + BuildCounts(m_Reasons);
            }

            public string BuildRealTargetStats()
            {
                if (m_RealTargetRows == 0)
                {
                    return "real_targets rows=0";
                }

                return "real_targets rows=" + m_RealTargetRows.ToString(CultureInfo.InvariantCulture) +
                       " accepted=" + m_RealAccepted.ToString(CultureInfo.InvariantCulture) +
                       " acceptance_rate=" + FormatPct(m_RealAccepted, m_RealTargetRows) +
                       " avg_km=" + FormatKm(m_Real.AvgMeters) +
                       " p50_km=" + FormatKm(m_Real.PercentileMeters(0.50)) +
                       " p90_km=" + FormatKm(m_Real.PercentileMeters(0.90)) +
                       " p95_km=" + FormatKm(m_Real.PercentileMeters(0.95)) +
                       " max_km=" + FormatKm(m_Real.MaxMeters) +
                       " avg_min=" + FormatDouble(m_Real.AvgMinutes) +
                       " p50_min=" + FormatDouble(m_Real.PercentileMinutes(0.50)) +
                       " p90_min=" + FormatDouble(m_Real.PercentileMinutes(0.90)) +
                       " p95_min=" + FormatDouble(m_Real.PercentileMinutes(0.95)) +
                       " max_min=" + FormatDouble(m_Real.MaxMinutes) +
                       " avg_probability=" + FormatProb(m_Real.AvgProbability) +
                       " at_min_probability=" + FormatPct(m_RealAtMinProbability, m_RealTargetRows) +
                       " at_max_probability=" + FormatPct(m_RealAtMaxProbability, m_RealTargetRows) +
                       " avg_utility=" + FormatDouble(m_Real.AvgUtility) +
                       " accepted_avg_km=" + FormatKm(m_AcceptedReal.AvgMeters) +
                       " accepted_avg_min=" + FormatDouble(m_AcceptedReal.AvgMinutes);
            }

            public string BuildNullTargetStats()
            {
                return "null_targets rows=" + m_NullTargetRows.ToString(CultureInfo.InvariantCulture) +
                       " accepted=" + m_NullAccepted.ToString(CultureInfo.InvariantCulture) +
                       " rejected=" + m_NullRejected.ToString(CultureInfo.InvariantCulture) +
                       " passed_through=" + m_NullPassedThrough.ToString(CultureInfo.InvariantCulture) +
                       " share=" + FormatPct(m_NullTargetRows, Count) +
                       " origin_null=" + m_NullOriginRows.ToString(CultureInfo.InvariantCulture) +
                       " origin_present=" + m_NullOriginPresentRows.ToString(CultureInfo.InvariantCulture) +
                       " zero_distance=" + m_NullZeroDistanceRows.ToString(CultureInfo.InvariantCulture) +
                       " nonzero_distance=" + m_NullNonZeroDistanceRows.ToString(CultureInfo.InvariantCulture) +
                       " zero_duration=" + m_NullZeroDurationRows.ToString(CultureInfo.InvariantCulture) +
                       " nonzero_duration=" + m_NullNonZeroDurationRows.ToString(CultureInfo.InvariantCulture);
            }

            public string BuildPrefilterStats()
            {
                if (!m_Prefilter.HasRows)
                {
                    return string.Empty;
                }

                return "prefilter calls=" + m_Prefilter.Calls.ToString(CultureInfo.InvariantCulture) +
                       " setup_items=" + m_Prefilter.SetupItems.ToString(CultureInfo.InvariantCulture) +
                       " current_location_starts=" + m_Prefilter.CurrentLocationStarts.ToString(CultureInfo.InvariantCulture) +
                       " origins_mapped=" + m_Prefilter.OriginsMapped.ToString(CultureInfo.InvariantCulture) +
                       " job_seeker_targets=" + m_Prefilter.JobSeekerTargets.ToString(CultureInfo.InvariantCulture) +
                       " missing_action_origins=" + m_Prefilter.MissingActionOrigins.ToString(CultureInfo.InvariantCulture) +
                       " owner_origin_fallbacks=" + m_Prefilter.OwnerOriginFallbacks.ToString(CultureInfo.InvariantCulture) +
                       " owner_origin_fallback_failures=" + m_Prefilter.OwnerOriginFallbackFailures.ToString(CultureInfo.InvariantCulture) +
                       " skipped_no_origin=" + m_Prefilter.SkippedNoOrigin.ToString(CultureInfo.InvariantCulture) +
                       " empty_buffers=" + m_Prefilter.EmptyBuffers.ToString(CultureInfo.InvariantCulture) +
                       " cooldown_buffers=" + m_Prefilter.CooldownBuffers.ToString(CultureInfo.InvariantCulture) +
                       " scored_buffers=" + m_Prefilter.ScoredBuffers.ToString(CultureInfo.InvariantCulture) +
                       " cleared_buffers=" + m_Prefilter.ClearedBuffers.ToString(CultureInfo.InvariantCulture) +
                       " original_candidates=" + m_Prefilter.OriginalCandidates.ToString(CultureInfo.InvariantCulture) +
                       " valid_candidates=" + m_Prefilter.ValidCandidates.ToString(CultureInfo.InvariantCulture) +
                       " invalid_candidates=" + m_Prefilter.InvalidCandidates.ToString(CultureInfo.InvariantCulture) +
                       " kept_candidates=" + m_Prefilter.KeptCandidates.ToString(CultureInfo.InvariantCulture) +
                       " selected=" + m_Prefilter.SelectedCandidates.ToString(CultureInfo.InvariantCulture) +
                       " unknown_position_kept=" + m_Prefilter.UnknownPositionKept.ToString(CultureInfo.InvariantCulture) +
                       " unscored_passthrough_buffers=" + m_Prefilter.UnscoredPassthroughBuffers.ToString(CultureInfo.InvariantCulture) +
                       " unscored_passthrough_candidates=" + m_Prefilter.UnscoredPassthroughCandidates.ToString(CultureInfo.InvariantCulture) +
                       " missing_workplace=" + m_Prefilter.MissingWorkplace.ToString(CultureInfo.InvariantCulture) +
                       " missing_position=" + m_Prefilter.MissingPosition.ToString(CultureInfo.InvariantCulture) +
                       " no_free_workplaces=" + m_Prefilter.NoFreeWorkplaces.ToString(CultureInfo.InvariantCulture);
            }

            public string BuildAcceptedRealBins()
            {
                if (m_AcceptedReal.Count == 0)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder("accepted_real_km_bins ");
                bool wrote = false;
                for (int i = 0; i < m_AcceptedRealKmBins.Length; i++)
                {
                    int value = m_AcceptedRealKmBins[i];
                    if (value == 0)
                    {
                        continue;
                    }

                    if (wrote)
                    {
                        builder.Append(';');
                    }

                    if (i == m_AcceptedRealKmBins.Length - 1)
                    {
                        builder.Append("30km_plus=");
                    }
                    else
                    {
                        builder.Append(i.ToString(CultureInfo.InvariantCulture));
                        builder.Append('-');
                        builder.Append((i + 1).ToString(CultureInfo.InvariantCulture));
                        builder.Append("km=");
                    }
                    builder.Append(value.ToString(CultureInfo.InvariantCulture));
                    wrote = true;
                }

                return builder.ToString();
            }

            public string BuildTopTargets()
            {
                if (m_Targets.Count == 0)
                {
                    return string.Empty;
                }

                var items = new List<KeyValuePair<string, TargetStats>>(m_Targets);
                items.Sort((a, b) => b.Value.Decisions.CompareTo(a.Value.Decisions));

                var builder = new StringBuilder("top_targets ");
                int limit = Math.Min(5, items.Count);
                for (int i = 0; i < limit; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(" | ");
                    }

                    var item = items[i];
                    TargetStats stats = item.Value;
                    builder.Append(item.Key);
                    builder.Append(" decisions=");
                    builder.Append(stats.Decisions.ToString(CultureInfo.InvariantCulture));
                    builder.Append(" accepted=");
                    builder.Append(stats.Accepted.ToString(CultureInfo.InvariantCulture));
                    builder.Append(" avg_km=");
                    builder.Append(FormatKm(stats.AvgMeters));
                    builder.Append(" avg_min=");
                    builder.Append(FormatDouble(stats.AvgMinutes));
                    builder.Append(" avg_probability=");
                    builder.Append(FormatProb(stats.AvgProbability));
                    builder.Append(" total_jobs_avg=");
                    builder.Append(FormatDouble(stats.AvgTotalJobs));
                    builder.Append(" free_jobs_avg=");
                    builder.Append(FormatDouble(stats.AvgFreeJobs));
                }

                return builder.ToString();
            }

            private static void Increment(Dictionary<string, int> counts, string key)
            {
                if (string.IsNullOrEmpty(key))
                {
                    key = "none";
                }

                counts.TryGetValue(key, out int value);
                counts[key] = value + 1;
            }

            private static string BuildCounts(Dictionary<string, int> counts)
            {
                if (counts.Count == 0)
                {
                    return "none";
                }

                var items = new List<KeyValuePair<string, int>>(counts);
                items.Sort((a, b) => b.Value.CompareTo(a.Value));

                var builder = new StringBuilder();
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(';');
                    }

                    builder.Append(items[i].Key);
                    builder.Append('=');
                    builder.Append(items[i].Value.ToString(CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private sealed class DistanceStats
        {
            private readonly List<float> m_Meters = new List<float>();
            private readonly List<float> m_Minutes = new List<float>();
            private double m_SumMeters;
            private double m_SumMinutes;
            private double m_SumProbability;
            private double m_SumUtility;
            private double m_SumTotalJobs;
            private double m_SumFreeJobs;

            public int Count => m_Meters.Count;
            public double AvgMeters => Count == 0 ? 0d : m_SumMeters / Count;
            public double AvgMinutes => Count == 0 ? 0d : m_SumMinutes / Count;
            public double AvgProbability => Count == 0 ? 0d : m_SumProbability / Count;
            public double AvgUtility => Count == 0 ? 0d : m_SumUtility / Count;
            public double AvgTotalJobs => Count == 0 ? 0d : m_SumTotalJobs / Count;
            public double AvgFreeJobs => Count == 0 ? 0d : m_SumFreeJobs / Count;
            public double MaxMeters { get; private set; }
            public double MaxMinutes { get; private set; }

            public void Reset()
            {
                m_Meters.Clear();
                m_Minutes.Clear();
                m_SumMeters = 0d;
                m_SumMinutes = 0d;
                m_SumProbability = 0d;
                m_SumUtility = 0d;
                m_SumTotalJobs = 0d;
                m_SumFreeJobs = 0d;
                MaxMeters = 0d;
                MaxMinutes = 0d;
            }

            public void Add(in JobSearchScore score)
            {
                m_Meters.Add(score.Meters);
                m_Minutes.Add(score.Minutes);
                m_SumMeters += score.Meters;
                m_SumMinutes += score.Minutes;
                m_SumProbability += score.Probability;
                m_SumUtility += score.Utility;
                m_SumTotalJobs += score.TotalJobs;
                m_SumFreeJobs += score.FreeJobs;
                MaxMeters = Math.Max(MaxMeters, score.Meters);
                MaxMinutes = Math.Max(MaxMinutes, score.Minutes);
            }

            public double PercentileMeters(double percentile)
            {
                return Percentile(m_Meters, percentile);
            }

            public double PercentileMinutes(double percentile)
            {
                return Percentile(m_Minutes, percentile);
            }

            private static double Percentile(List<float> values, double percentile)
            {
                if (values.Count == 0)
                {
                    return 0d;
                }

                var copy = new List<float>(values);
                copy.Sort();
                int index = (int)Math.Ceiling(percentile * copy.Count) - 1;
                index = Math.Max(0, Math.Min(copy.Count - 1, index));
                return copy[index];
            }
        }

        private sealed class TargetStats
        {
            private readonly DistanceStats m_Distance = new DistanceStats();
            public int Decisions { get; private set; }
            public int Accepted { get; private set; }
            public double AvgMeters => m_Distance.AvgMeters;
            public double AvgMinutes => m_Distance.AvgMinutes;
            public double AvgProbability => m_Distance.AvgProbability;
            public double AvgTotalJobs => m_Distance.AvgTotalJobs;
            public double AvgFreeJobs => m_Distance.AvgFreeJobs;

            public void Add(bool accepted, in JobSearchScore score)
            {
                Decisions++;
                if (accepted)
                {
                    Accepted++;
                }
                m_Distance.Add(score);
            }
        }
    }
}
