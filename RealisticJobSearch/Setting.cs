using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using Game.UI.Widgets;
using System.Collections.Generic;
using System.Net.Configuration;

namespace RealisticJobSearch
{
    [FileLocation(nameof(RealisticJobSearch))]
    [SettingsUIGroupOrder(SettingsGroup)]
    //[SettingsUIShowGroupName(SettingsGroup, kToggleGroup, kSliderGroup, kDropdownGroup)]
    public class Setting : ModSetting
    {
        public const string SettingsSection = "SettingsSection";
        public const string SettingsGroup = "SettingsGroup";

        public Setting(IMod mod) : base(mod)
        {
            if (weight_free_jobs == 0) SetDefaults();
        }

        public override void SetDefaults()
        {
            weight_free_jobs = 0.28f;
            weight_total_jobs = 0.72f;
            alpha_jobs = 1.2f;
            beta_minute = 0.9f;
            min_accept = 0.15f;
            max_accept = 0.98f;
        }

        [SettingsUISection(SettingsSection, SettingsGroup)]
        public bool debug { get; set; }

        [SettingsUISlider(min = 0.05f, max = 1f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float weight_free_jobs { get; set; }

        [SettingsUISlider(min = 0.05f, max = 1f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float weight_total_jobs { get; set; }

        [SettingsUISlider(min = 0.05f, max = 3f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float alpha_jobs { get; set; }

        [SettingsUISlider(min = 0.05f, max = 2f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float beta_minute { get; set; }

        [SettingsUISlider(min = 0.05f, max = 1f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float min_accept { get; set; }

        [SettingsUISlider(min = 0.05f, max = 1f, step = 0.05f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(SettingsSection, SettingsGroup)]
        public float max_accept { get; set; }

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
                // Mod / Tab
                { m_Setting.GetSettingsLocaleID(), "Realistic JobSearch" },
                { m_Setting.GetOptionTabLocaleID(Setting.SettingsSection), "Settings" },

                // Debug
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.debug)), "Print Debug File" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.debug)),  "Writes a CSV with commute stats to the ModsData folder." },

                // Weights
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.weight_free_jobs)), "Weight: Open Jobs Now" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.weight_free_jobs)),  "How much to favor workplaces that currently have free positions. Higher = more bias to places with open slots." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.weight_total_jobs)), "Weight: Workplace Size" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.weight_total_jobs)),  "How much to favor larger workplaces (more total jobs), even if not all are open. Higher = more bias to big employers." },

                // Gravity / acceptance tuning
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.alpha_jobs)), "Job Availability Boost" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.alpha_jobs)),  "How strongly job availability increases the chance a job is accepted. Higher = big workplaces pull more strongly." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.beta_minute)), "Commute Time Sensitivity" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.beta_minute)),  "How quickly long trips become unattractive. Higher = avoids long commutes more aggressively." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.min_accept)), "Acceptance Floor" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.min_accept)),  "Lowest possible chance to accept any job. Raises this to keep rare options from being fully ruled out." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.max_accept)), "Acceptance Cap" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.max_accept)),  "Highest possible chance to accept even a perfect job. Lowers this to keep some variety/randomness." },


            };
        }

        public void Unload()
        {

        }
    }
}
