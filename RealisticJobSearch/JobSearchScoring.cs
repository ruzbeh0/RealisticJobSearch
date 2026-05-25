#nullable enable
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticJobSearch
{
    public struct JobSearchScore
    {
        public float Meters;
        public float Minutes;
        public int TotalJobs;
        public int FreeJobs;
        public float Mass;
        public float Utility;
        public float Probability;
        public float Roll;
    }

    public static class JobSearchScoring
    {
        public const float DefaultAlphaJobs = 0.65f;
        public const float DefaultBetaMinute = 0.20f;
        public const float DefaultMinAccept = 0.05f;
        public const float DefaultMaxAccept = 0.90f;
        public const float DefaultWeightFreeJobs = 0.65f;
        public const float DefaultWeightTotalJobs = 0.35f;
        public const float DefaultSoftmaxTemperature = 0.45f;
        public const float DefaultEstimatedCommuteSpeedKmh = 25f;
        public const int DefaultTopK = 12;
        public const int DefaultMaxDailyRejections = 2;
        public const float DefaultRetryCooldownHours = 2f;

        private const float LowFilledThreshold = 0.10f;
        private const float LowFilledMaxBonus = 24f;
        private const float LowFilledBonusPerJob = 0.75f;
        private const float LowFilledMinBonus = 6f;

        public static GravityAcceptParams FromSettings(Setting? setting)
        {
            if (setting == null)
            {
                return Defaults();
            }

            return Sanitize(new GravityAcceptParams
            {
                AlphaJobs = setting.alpha_jobs,
                BetaMinute = setting.beta_minute,
                MinAccept = setting.min_accept,
                MaxAccept = setting.max_accept,
                WeightFreeJobs = setting.weight_free_jobs,
                WeightTotalJobs = setting.weight_total_jobs,
                EstimatedCommuteSpeedKmh = setting.estimated_commute_speed_kmh,
                SoftmaxTemperature = setting.softmax_temperature,
                TopK = setting.top_k,
                MaxDailyRejections = setting.max_daily_rejections,
                RetryCooldownHours = setting.retry_cooldown_hours
            });
        }

        public static GravityAcceptParams Defaults()
        {
            return new GravityAcceptParams
            {
                AlphaJobs = DefaultAlphaJobs,
                BetaMinute = DefaultBetaMinute,
                MinAccept = DefaultMinAccept,
                MaxAccept = DefaultMaxAccept,
                WeightFreeJobs = DefaultWeightFreeJobs,
                WeightTotalJobs = DefaultWeightTotalJobs,
                EstimatedCommuteSpeedKmh = DefaultEstimatedCommuteSpeedKmh,
                SoftmaxTemperature = DefaultSoftmaxTemperature,
                TopK = DefaultTopK,
                MaxDailyRejections = DefaultMaxDailyRejections,
                RetryCooldownHours = DefaultRetryCooldownHours
            };
        }

        public static GravityAcceptParams Sanitize(GravityAcceptParams p)
        {
            p.AlphaJobs = math.clamp(p.AlphaJobs, 0.05f, 3f);
            p.BetaMinute = math.clamp(p.BetaMinute, 0.001f, 2f);
            p.MinAccept = math.clamp(p.MinAccept, 0f, 1f);
            p.MaxAccept = math.clamp(p.MaxAccept, 0f, 1f);
            if (p.MaxAccept < p.MinAccept)
            {
                float tmp = p.MaxAccept;
                p.MaxAccept = p.MinAccept;
                p.MinAccept = tmp;
            }

            p.WeightFreeJobs = math.max(0f, p.WeightFreeJobs);
            p.WeightTotalJobs = math.max(0f, p.WeightTotalJobs);
            p.EstimatedCommuteSpeedKmh = math.clamp(p.EstimatedCommuteSpeedKmh, 1f, 120f);
            p.SoftmaxTemperature = math.clamp(p.SoftmaxTemperature, 0.05f, 5f);
            p.TopK = math.clamp(p.TopK, 1, 64);
            p.MaxDailyRejections = math.clamp(p.MaxDailyRejections, 0, 16);
            p.RetryCooldownHours = math.clamp(p.RetryCooldownHours, 0.01f, 24f);
            return p;
        }

        public static JobSearchScore Score(
            GravityAcceptParams p,
            float meters,
            float minutes,
            int totalJobs,
            int freeJobs,
            float roll = -1f)
        {
            p = Sanitize(p);
            totalJobs = math.max(0, totalJobs);
            freeJobs = math.max(0, freeJobs);
            minutes = math.max(0.01f, minutes);
            meters = math.max(0f, meters);

            if (totalJobs > 0 && freeJobs <= 0)
            {
                return new JobSearchScore
                {
                    Meters = meters,
                    Minutes = minutes,
                    TotalJobs = totalJobs,
                    FreeJobs = freeJobs,
                    Mass = 0f,
                    Utility = -20f,
                    Probability = 0f,
                    Roll = roll
                };
            }

            float mass = CalculateMass(p, totalJobs, freeJobs);
            float utility = p.AlphaJobs * math.log(1f + mass) - p.BetaMinute * minutes;
            float probability = 1f / (1f + math.exp(-utility));
            probability = math.clamp(probability, p.MinAccept, p.MaxAccept);

            return new JobSearchScore
            {
                Meters = meters,
                Minutes = minutes,
                TotalJobs = totalJobs,
                FreeJobs = freeJobs,
                Mass = mass,
                Utility = utility,
                Probability = probability,
                Roll = roll
            };
        }

        private static float CalculateMass(GravityAcceptParams p, int totalJobs, int freeJobs)
        {
            float mass = p.WeightTotalJobs * totalJobs + p.WeightFreeJobs * freeJobs;
            if (totalJobs <= 0 || freeJobs <= 0)
            {
                return math.max(1f, mass);
            }

            int cappedFreeJobs = math.min(freeJobs, totalJobs);
            int filledJobs = math.max(0, totalJobs - cappedFreeJobs);
            float filledRatio = (float)filledJobs / totalJobs;
            if (filledJobs == 0 || filledRatio < LowFilledThreshold)
            {
                float pressure = math.saturate((LowFilledThreshold - filledRatio) / LowFilledThreshold);
                float boost = math.clamp(totalJobs * LowFilledBonusPerJob, LowFilledMinBonus, LowFilledMaxBonus);
                mass += pressure * boost;
            }

            return math.max(1f, mass);
        }

        public static float EstimateMinutesFromMeters(float meters, float speedKmh)
        {
            float metersPerMinute = math.max(1f, speedKmh) * 1000f / 60f;
            return math.max(0.01f, meters / metersPerMinute);
        }

        public static uint MakeSeed(uint frame, Entity a, Entity b, int salt)
        {
            unchecked
            {
                uint seed = 2166136261u;
                seed = (seed ^ frame) * 16777619u;
                seed = (seed ^ (uint)a.Index) * 16777619u;
                seed = (seed ^ (uint)a.Version) * 16777619u;
                seed = (seed ^ (uint)b.Index) * 16777619u;
                seed = (seed ^ (uint)b.Version) * 16777619u;
                seed = (seed ^ (uint)salt) * 16777619u;
                return seed == 0u ? 1u : seed;
            }
        }

        public static uint FramesFromHours(float hours)
        {
            const uint framesPerHour = 1024u;
            return (uint)math.max(1f, hours * framesPerHour);
        }
    }
}
