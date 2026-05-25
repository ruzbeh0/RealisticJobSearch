using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.PSI.Environment;
using Game;
using Game.Modding;
using Game.SceneFlow;
using HarmonyLib;
using RealisticJobSearch.Systems;
using System;
using System.IO;
using System.Linq;

namespace RealisticJobSearch
{
    public class Mod : IMod
    {
        public static readonly string harmonyID = "RealisticJobSearch";
        public static ILog log = LogManager.GetLogger($"{nameof(RealisticJobSearch)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        public static Setting m_Setting;
        public static string outputPath = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(RealisticJobSearch));
        public static bool GameplayEnabled => m_Setting == null || m_Setting.enable_mod;
        private Harmony m_Harmony;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));
            log.Info($"Game assembly version: {typeof(GameManager).Assembly.GetName().Version}");

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Setting));
            AssetDatabase.global.LoadSettings(nameof(RealisticJobSearch), m_Setting, new Setting(this));
            if (m_Setting.Migrate())
            {
                _ = AssetDatabase.global.SaveSettings();
                log.Info("Migrated Realistic Job Search settings to version " + Setting.CurrentSettingsVersion);
            }
            JobSearchDebug.ResetSession();
            if (m_Setting.debug)
                log.Info("Debug decision logging enabled");
            if (!m_Setting.enable_mod)
                log.Info("Realistic Job Search gameplay changes disabled; observing vanilla job results only.");
            
            updateSystem.UpdateBefore<GravityAcceptanceGateSystem,
                                     Game.Simulation.FindJobSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<RetryThrottleSystem,
                                     Game.Simulation.FindJobSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<MetricsSystem,
                                     Game.Simulation.FindJobSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<MetricsSystem,
                                    GravityAcceptanceGateSystem>(SystemUpdatePhase.GameSimulation);

            m_Harmony = new Harmony(harmonyID);
            // Harmony.DEBUG = true;
            try
            {
                m_Harmony.PatchAll(typeof(Mod).Assembly);

                var patchedMethods = m_Harmony.GetPatchedMethods().ToArray();
                log.Info($"Plugin {harmonyID} made patches! Patched methods: " + patchedMethods.Length);
                foreach (var patchedMethod in patchedMethods)
                    log.Info($"Patched: {patchedMethod.DeclaringType?.FullName}.{patchedMethod.Name}");
            }
            catch (Exception ex)
            {
                log.Info("Harmony patching failed; Realistic Job Search will run without candidate prefilter patches. " + ex);
            }
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            JobSearchDebug.Flush("dispose");
            if (m_Setting != null)
            {
                m_Setting.UnregisterInOptionsUI();
                m_Setting = null;
            }
            m_Harmony?.UnpatchAll(harmonyID);
            m_Harmony = null;
        }
    }
}
