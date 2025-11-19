using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.PSI.Environment;
using Game;
using Game.Modding;
using Game.SceneFlow;
using HarmonyLib;
using RealisticJobSearch.Systems;
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

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Setting));
            AssetDatabase.global.LoadSettings(nameof(RealisticJobSearch), m_Setting, new Setting(this));

            updateSystem.UpdateBefore<GravityPreFilterSystem,
                                     Game.Simulation.FindJobSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<GravityAcceptanceGateSystem,
                                     Game.Simulation.FindJobSystem > (SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<RetryThrottleSystem,
                                     Game.Simulation.FindJobSystem>(SystemUpdatePhase.GameSimulation);
            if(m_Setting.debug)
            {
                log.Info($"Debug CSV output enabled");
                updateSystem.UpdateBefore<MetricsSystem,
                                                     Game.Simulation.FindJobSystem>(SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAfter<MetricsSystem,
                                         GravityAcceptanceGateSystem>(SystemUpdatePhase.GameSimulation);
            }
            

            AssetDatabase.global.LoadSettings(nameof(RealisticJobSearch), m_Setting, new Setting(this));

            var harmony = new Harmony(harmonyID);
            // Harmony.DEBUG = true;
            harmony.PatchAll(typeof(Mod).Assembly);
            
            
            var patchedMethods = harmony.GetPatchedMethods().ToArray();
            log.Info($"Plugin {harmonyID} made patches! Patched methods: " + patchedMethods.Length);
            foreach (var patchedMethod in patchedMethods)
                log.Info($"Patched: {patchedMethod.DeclaringType?.FullName}.{patchedMethod.Name}");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (m_Setting != null)
            {
                m_Setting.UnregisterInOptionsUI();
                m_Setting = null;
            }
        }
    }
}
