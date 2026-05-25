using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using Game.UI.Widgets;
using System.Collections.Generic;

namespace RealisticJobSearch
{
    [FileLocation(nameof(RealisticJobSearch))]
    [SettingsUIGroupOrder(SettingsGroup)]
    public class Setting : ModSetting
    {
        public const string SettingsSection = "SettingsSection";
        public const string SettingsGroup = "SettingsGroup";
        public const int CurrentSettingsVersion = 7;

        private const float LegacyDefaultAlphaJobs = 1.2f;
        private const float LegacyDefaultBetaMinute = 0.12f;
        public Setting(IMod mod) : base(mod)
        {
            if (weight_free_jobs == 0)
            {
                SetDefaults();
            }
        }

        public override void SetDefaults()
        {
            settings_version = CurrentSettingsVersion;
            enable_mod = true;
            weight_free_jobs = JobSearchScoring.DefaultWeightFreeJobs;
            weight_total_jobs = JobSearchScoring.DefaultWeightTotalJobs;
            alpha_jobs = JobSearchScoring.DefaultAlphaJobs;
            beta_minute = JobSearchScoring.DefaultBetaMinute;
            min_accept = JobSearchScoring.DefaultMinAccept;
            max_accept = JobSearchScoring.DefaultMaxAccept;
            top_k = JobSearchScoring.DefaultTopK;
            softmax_temperature = JobSearchScoring.DefaultSoftmaxTemperature;
            estimated_commute_speed_kmh = JobSearchScoring.DefaultEstimatedCommuteSpeedKmh;
            max_daily_rejections = JobSearchScoring.DefaultMaxDailyRejections;
            retry_cooldown_hours = JobSearchScoring.DefaultRetryCooldownHours;
            debug_summary_decisions = 500;
            debug = false;
        }

        public bool Migrate()
        {
            bool changed = false;
            if (settings_version < CurrentSettingsVersion)
            {
                if (settings_version < 3)
                {
                    enable_mod = true;
                    changed = true;
                }

                if (settings_version < 4)
                {
                    debug_summary_decisions = 500;
                    changed = true;
                }

                if (settings_version < 5)
                {
                    weight_free_jobs = JobSearchScoring.DefaultWeightFreeJobs;
                    weight_total_jobs = JobSearchScoring.DefaultWeightTotalJobs;
                    beta_minute = JobSearchScoring.DefaultBetaMinute;
                    max_accept = JobSearchScoring.DefaultMaxAccept;
                    max_daily_rejections = JobSearchScoring.DefaultMaxDailyRejections;
                    changed = true;
                }

                if (settings_version < 6)
                {
                    if (NearlyEqual(alpha_jobs, LegacyDefaultAlphaJobs))
                    {
                        alpha_jobs = JobSearchScoring.DefaultAlphaJobs;
                    }

                    if (NearlyEqual(beta_minute, LegacyDefaultBetaMinute))
                    {
                        beta_minute = JobSearchScoring.DefaultBetaMinute;
                    }

                    changed = true;
                }

                if (beta_minute > 0.35f)
                {
                    beta_minute = JobSearchScoring.DefaultBetaMinute;
                    changed = true;
                }

                if (top_k <= 0)
                {
                    top_k = JobSearchScoring.DefaultTopK;
                    changed = true;
                }

                if (softmax_temperature <= 0f)
                {
                    softmax_temperature = JobSearchScoring.DefaultSoftmaxTemperature;
                    changed = true;
                }

                if (estimated_commute_speed_kmh <= 0f)
                {
                    estimated_commute_speed_kmh = JobSearchScoring.DefaultEstimatedCommuteSpeedKmh;
                    changed = true;
                }

                if (max_daily_rejections <= 0)
                {
                    max_daily_rejections = JobSearchScoring.DefaultMaxDailyRejections;
                    changed = true;
                }

                if (retry_cooldown_hours <= 0f)
                {
                    retry_cooldown_hours = JobSearchScoring.DefaultRetryCooldownHours;
                    changed = true;
                }

                settings_version = CurrentSettingsVersion;
                changed = true;
            }

            changed |= ClampSettings();
            return changed;
        }

        private bool ClampSettings()
        {
            bool changed = false;
            float originalFloat = weight_free_jobs;
            weight_free_jobs = Clamp(weight_free_jobs, 0f, 1f);
            changed |= originalFloat != weight_free_jobs;

            originalFloat = weight_total_jobs;
            weight_total_jobs = Clamp(weight_total_jobs, 0f, 1f);
            changed |= originalFloat != weight_total_jobs;

            originalFloat = alpha_jobs;
            alpha_jobs = Clamp(alpha_jobs, 0.05f, 3f);
            changed |= originalFloat != alpha_jobs;

            originalFloat = beta_minute;
            beta_minute = Clamp(beta_minute, 0.001f, 2f);
            changed |= originalFloat != beta_minute;

            originalFloat = min_accept;
            min_accept = Clamp(min_accept, 0f, 1f);
            changed |= originalFloat != min_accept;

            originalFloat = max_accept;
            max_accept = Clamp(max_accept, 0f, 1f);
            changed |= originalFloat != max_accept;

            if (max_accept < min_accept)
            {
                float tmp = max_accept;
                max_accept = min_accept;
                min_accept = tmp;
                changed = true;
            }

            originalFloat = softmax_temperature;
            softmax_temperature = Clamp(softmax_temperature, 0.05f, 5f);
            changed |= originalFloat != softmax_temperature;

            originalFloat = estimated_commute_speed_kmh;
            estimated_commute_speed_kmh = Clamp(estimated_commute_speed_kmh, 1f, 120f);
            changed |= originalFloat != estimated_commute_speed_kmh;

            originalFloat = retry_cooldown_hours;
            retry_cooldown_hours = Clamp(retry_cooldown_hours, 0.01f, 24f);
            changed |= originalFloat != retry_cooldown_hours;

            int originalInt = top_k;
            top_k = Clamp(top_k, 1, 64);
            changed |= originalInt != top_k;

            originalInt = max_daily_rejections;
            max_daily_rejections = Clamp(max_daily_rejections, 0, 16);
            changed |= originalInt != max_daily_rejections;

            originalInt = debug_summary_decisions;
            debug_summary_decisions = Clamp(debug_summary_decisions, 50, 5000);
            changed |= originalInt != debug_summary_decisions;
            return changed;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) value = min;
            if (value > max) value = max;
            return value;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) value = min;
            if (value > max) value = max;
            return value;
        }

        private static bool NearlyEqual(float a, float b)
        {
            return System.Math.Abs(a - b) < 0.0001f;
        }

        [SettingsUIHidden]
        public int settings_version { get; set; }

        [SettingsUISection(SettingsSection, SettingsGroup)]
        public bool enable_mod { get; set; }

        [SettingsUISection(SettingsSection, SettingsGroup)]
        public bool debug { get; set; }

        [SettingsUISlider(min = 50, max = 5000, step = 50, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public int debug_summary_decisions { get; set; }

        [SettingsUISlider(min = 0.05f, max = 1f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float weight_free_jobs { get; set; }

        [SettingsUISlider(min = 0.05f, max = 1f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float weight_total_jobs { get; set; }

        [SettingsUISlider(min = 0.05f, max = 3f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float alpha_jobs { get; set; }

        [SettingsUISlider(min = 0.01f, max = 0.3f, step = 0.01f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float beta_minute { get; set; }

        [SettingsUISlider(min = 0f, max = 1f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float min_accept { get; set; }

        [SettingsUISlider(min = 0f, max = 1f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float max_accept { get; set; }

        [SettingsUISlider(min = 4, max = 32, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public int top_k { get; set; }

        [SettingsUISlider(min = 0.1f, max = 2f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float softmax_temperature { get; set; }

        [SettingsUISlider(min = 5f, max = 80f, step = 1f, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float estimated_commute_speed_kmh { get; set; }

        [SettingsUISlider(min = 0, max = 4, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public int max_daily_rejections { get; set; }

        [SettingsUISlider(min = 0.25f, max = 8f, step = 0.25f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float retry_cooldown_hours { get; set; }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Realistic Job Search" },
                { m_Setting.GetOptionTabLocaleID(Setting.SettingsSection), "Settings" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.enable_mod)), "Enable Mod" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.enable_mod)),  "When disabled, gameplay stays vanilla. Debug logging can still observe vanilla job decisions." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.debug)), "Log Debug Decisions" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.debug)),  "Writes aggregated decision summaries to the game log. Aggregate metrics still go to ModsData." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.debug_summary_decisions)), "Debug Summary Size" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.debug_summary_decisions)),  "How many decisions to group into each debug summary before writing it to the log." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.weight_free_jobs)), "Weight: Open Jobs Now" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.weight_free_jobs)),  "How much to favor workplaces that currently have free positions. Higher = more bias to places with open slots and low staffing." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.weight_total_jobs)), "Weight: Workplace Size" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.weight_total_jobs)),  "How much to favor larger workplaces. Lower this if big employers keep outcompeting small understaffed workplaces." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.alpha_jobs)), "Job Availability Boost" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.alpha_jobs)),  "How strongly job availability increases the chance a job is accepted. Higher = big workplaces pull more strongly." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.beta_minute)), "Commute Time Sensitivity" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.beta_minute)),  "How quickly long trips become unattractive per minute. Higher = avoids long commutes more aggressively." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.min_accept)), "Acceptance Floor" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.min_accept)),  "Lowest possible chance to accept any job. Raise this to keep rare options from being fully ruled out." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.max_accept)), "Acceptance Cap" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.max_accept)),  "Highest possible chance to accept even a perfect job. Lower this to keep some variety." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.top_k)), "Candidate List Size" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.top_k)),  "How many workplace candidates are kept after the gravity prefilter." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.softmax_temperature)), "Selection Randomness" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.softmax_temperature)),  "How much variety to allow among good candidates. Lower = more greedy, higher = more varied." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.estimated_commute_speed_kmh)), "Estimated Commute Speed" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.estimated_commute_speed_kmh)),  "Straight-line speed used only before the game has calculated a real path duration." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.max_daily_rejections)), "Daily Rejection Cap" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.max_daily_rejections)),  "Maximum long-commute refusals per job seeker before the next valid result is accepted." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.retry_cooldown_hours)), "Retry Cooldown Hours" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.retry_cooldown_hours)),  "How long a job seeker waits after refusing a long commute before another attempt is allowed." },
            };
        }

        public void Unload()
        {
        }
    }
}
