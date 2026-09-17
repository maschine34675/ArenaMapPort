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

            // daytime-and-visuals F1 — config must be bound before HarmonyPatches.InstallAll patches
            // LocalGame.InitWeather, whose prefix (TimeOfDayRepairs.Prefix) reads it. See TimeOfDayRepairs.cs.
            TimeOfDayRepairs.Install();

            // donor-settings-camera-weather §C.3/§D — same reasoning as TimeOfDayRepairs.Install() above:
            // config must be bound before HarmonyPatches.InstallAll patches the methods whose
            // prefix/postfix reads it. See RainWetnessRepairs.cs / WeatherTestOverride.cs.
            RainWetnessRepairs.Install();
            WeatherTestOverride.Install();

            _harmony = new Harmony(PluginGuid);
            HarmonyPatches.InstallAll(_harmony);

            SceneDiagnostics.Install();
            ShaderRebind.Install();

            // weather-rig-stage0 — harvests a vanilla donor scene's Weather rig and activates it per
            // raid. Must install BEFORE ArenaLightingRepairs: both subscribe to the same static
            // SceneManager.sceneLoaded event, and ArenaLightingRepairs.OnSceneLoaded reads
            // WeatherRig.IsActiveForCurrentMap() on THIS SAME dispatch (C# multicast delegates invoke
            // subscribers in subscription order) — it must see this raid's own activation, not a stale
            // false left over from before this raid's Scripts scene loaded. See docs/progress/
            // weather-rig-stage0.md.
            WeatherRig.Install();

            // fix-lighting-movement, items 2-4 — gated ambient/light brightness repair, its live
            // ConfigEntries, and the once-per-raid diagnostic dump. See docs/progress/
            // fix-lighting-movement.md.
            ArenaLightingRepairs.Install();
            LightingDiagnosticProbe.Install();

            // Playtest finding 2026-09-14 20:09 — large flat white untextured blocks blocking movement.
            // Removes Arena's own TeamFight play-area borders (config-gated) plus a narrow name-based
            // safety net for components disabled in Arena that the current extractor/importer still
            // ships enabled. See MapBorderRepairs.cs doc comment for the full root-cause breakdown.
            MapBorderRepairs.Install();

            // daytime-and-visuals F2-F7 — 2026-09-17 playtest symptom B follow-up. See
            // docs/progress/daytime-and-visuals.md and each file's own doc comment.
            CameraEffectsRepairs.Install();
            DecalRepairs.Install();
            SeaPlaceholderRepairs.Install();
            DaytimeVisualsProbe.Install();

            // docs/progress/shader-compat-and-inspector.md §b — F10 live diagnostic hotkey (any map, no
            // gate/dependency on the repairs above; order doesn't matter, placed last for clarity).
            SceneInspector.Install();

            int totalOwnedScenes = 0;
            foreach (var map in ArenaMapRegistry.All)
            {
                totalOwnedScenes += map.AllSceneNames.Length;
            }
            Log.LogInfo($"[ArenaMapPort] {PluginName} {PluginVersion} loaded — " +
                        $"{ArenaMapRegistry.All.Count} map(s), {totalOwnedScenes} owned scenes, " +
                        "Harmony patches applied.");
        }

        // Multimap generalization (docs/progress/multimap-generalize.md): loops ArenaMapRegistry.All
        // instead of 3 hardcoded Equator literals — same 3 filenames logged for Equator as before.
        void LogStartupBanner()
        {
            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
            var bundlesDir = Path.Combine(pluginDir, "bundles");

            Log.LogInfo($"[ArenaMapPort] plugin dir: {pluginDir}");

            foreach (var map in ArenaMapRegistry.All)
            {
                LogExpectedFile(Path.Combine(bundlesDir, map.PresetBundleFileName));
                LogExpectedFile(Path.Combine(bundlesDir, map.ScenesBundleFileName));
                LogExpectedFile(Path.Combine(bundlesDir, map.NavMeshBundleFileName));
                LogExpectedFile(Path.Combine(pluginDir, ShaderRebind.SidecarFileName(map)));
            }
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
