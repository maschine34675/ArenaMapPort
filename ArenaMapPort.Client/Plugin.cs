using System;
using System.IO;
using ArenaMapPort.Shared;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// WP4 client plugin entry point. See docs/SPEC.md §F for the full hook/timing design; §B.WP4 for the
    /// work package summary. Responsibilities wired here: serve the 3 bundles (§F.1), add the NavMesh
    /// before BotsController.Init (§F.2), rebind stand-in shaders (§F.3, O1), skip PerfectCulling's
    /// autoculling init for our own raid (§F.6), diagnostics (§F.5).
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.maschine.ArenaMapPort";
        public const string PluginName = "maschine-ArenaMapPort";
        public const string PluginVersion = "0.1.0";

        public static ManualLogSource Log { get; private set; }

        public static Plugin Instance { get; private set; }

        // §F.5: "No config beyond a LogLevel toggle — no player-facing settings for the spike."
        static ConfigEntry<bool> _verboseLogging;
        public static bool VerboseLogging => _verboseLogging?.Value ?? false;

        Harmony _harmony;

        void Awake()
        {
            Instance = this;
            Log = base.Logger;

            _verboseLogging = Config.Bind(
                "Logging",
                "Verbose",
                false,
                "Extra per-scene/per-material diagnostic detail beyond the always-on §F.5 summary lines. " +
                "No other settings exist for this spike.");

            LogStartupBanner();

            _harmony = new Harmony(PluginGuid);
            HarmonyPatches.InstallAll(_harmony);

            SceneDiagnostics.Install();
            ShaderRebind.Install();

            // fix-lighting-movement, items 2-4 — gated ambient/light brightness repair, its live
            // ConfigEntries, and the once-per-raid diagnostic dump. See docs/progress/
            // fix-lighting-movement.md.
            ArenaLightingRepairs.Install();
            LightingDiagnosticProbe.Install();

            Log.LogInfo($"[ArenaMapPort] {PluginName} {PluginVersion} loaded — " +
                        $"{ArenaEquatorLocation.AllSceneNames.Length} owned scenes, Harmony patches applied.");
        }

        void LogStartupBanner()
        {
            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
            var bundlesDir = Path.Combine(pluginDir, "bundles");

            Log.LogInfo($"[ArenaMapPort] plugin dir: {pluginDir}");

            LogExpectedFile(Path.Combine(bundlesDir, "arena_equator_preset.bundle"));
            LogExpectedFile(Path.Combine(bundlesDir, "arena_equator_scenes.bundle"));
            LogExpectedFile(Path.Combine(bundlesDir, "arena_equator_navmesh.bundle"));
            LogExpectedFile(Path.Combine(pluginDir, "material-shaders.json"));
        }

        void LogExpectedFile(string path)
        {
            if (File.Exists(path))
            {
                Log.LogInfo($"[ArenaMapPort] found: {path}");
            }
            else
            {
                // Not fatal at Awake() — every hook that needs one of these degrades to a clear per-use
                // error log rather than crashing plugin load (§B.WP4: "log clearly if a target is missing").
                Log.LogWarning($"[ArenaMapPort] missing at Awake(): {path} (expected once WP2/WP3 bundles " +
                                "and material-shaders.json are deployed into this plugin's folder)");
            }
        }
    }
}
