using System;
using System.IO;
using System.Linq;
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
    // bots-stage1 — SainLocationShim resolves SAIN's LocationClass by name at install time; the soft
    // dependency only orders our Awake after SAIN's assembly is loaded when SAIN is present.
    [BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.smajlec.lights", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(DynamicMapsIntegration.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ArenaModeMapPositionIntegration.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.maschine.ArenaMapPort";
        public const string PluginName = "maschine-ArenaMapPort";
        public const string PluginVersion = "0.1.0";

        public static ManualLogSource Log { get; private set; }

        public static Plugin Instance { get; private set; }

        // Every tuning bind must use Settings. Config is reserved for the compact player menu.
        internal ConfigFile Settings { get; private set; }

        Harmony _harmony;

        void LateUpdate() => ArenaLightingRepairs.UpdateArtificialSun();

        void Awake()
        {
            Instance = this;
            Log = base.Logger;

            Settings = CompactConfiguration.Initialize(Config);
            LogStartupBanner();
            Bay5AmbientAudio.BindConfig(); // accepted audio defaults before the release configuration policy

            // daytime-and-visuals F1 — config must be bound before HarmonyPatches.InstallAll patches
            // LocalGame.InitWeather, whose prefix (TimeOfDayRepairs.Prefix) reads it. See TimeOfDayRepairs.cs.
            TimeOfDayRepairs.Install();

            // donor-settings-camera-weather §C.3/§D — same reasoning as TimeOfDayRepairs.Install() above:
            // config must be bound before HarmonyPatches.InstallAll patches the methods whose
            // prefix/postfix reads it. See RainWetnessRepairs.cs / WeatherTestOverride.cs.
            RainWetnessRepairs.Install();
            WeatherTestOverride.Install();

            _harmony = new Harmony(PluginGuid);
            EquatorProfiles.Install(_harmony); // capture the chosen profile before owned scenes load
            HarmonyPatches.InstallAll(_harmony);
            LightingModCompatibility.Install(_harmony);
            DynamicMapsIntegration.Install(_harmony);
            ArenaModeMapPositionIntegration.Install(_harmony);

            if (DeveloperOptions.Enabled) SceneDiagnostics.Install();
            VertexGroundDiffuseRepairs.Install(); // capture source RGB before shader/indoor repair cloning
            ShaderRebind.Install();
            NativeDustRepairs.Install(); // native animation after real shaders are rebound; preserve generated meshes
            ShadowCasterRepairs.Install(); // restore only descriptor-listed authored shadow proxies
            EquatorRoofShadowRepairs.Install(); // register existing structural ceilings for native distant sun shadows

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
            if (DeveloperOptions.Enabled) LightingDiagnosticProbe.Install();

            // "Lighting Lab" (docs/progress/donor-settings-camera-weather.md) — live F12 toggles for the
            // grazing-angle white-ground veil bug. Must install AFTER WeatherRig/ArenaLightingRepairs (both
            // subscribe to the same static SceneManager.sceneLoaded event; C# multicast delegates invoke
            // subscribers in subscription order) so its own OnSceneLoaded sees the rig already activated
            // and ArenaLightingRepairs' own ambient/light pass already applied before layering its
            // candidate overrides on top. See LightingLab.cs.
            LightingLab.Install();
            ReflectiveSpecularRepairs.Install(); // own scene materials only; live outer layer above indoor repairs

            // docs/progress/donor-settings-camera-weather.md §14 — live RainCoverLab experiments
            // (DrawTransform/CoverAll/RerenderHotkey) for the RoofDepthBlack rain-cover investigation. Must
            // install AFTER WeatherRig.Install() (its own live re-render path reads
            // WeatherRig.IsActiveForCurrentMap()) — same ordering reasoning as LightingLab.Install() above;
            // order relative to LightingLab.Install() itself does not matter, neither reads the other's state.
            RainCoverRepairs.InstallLab();

            // §19 — per-renderer indoor floor smoothness (RoofDepthBlack's own depth photo classifies
            // covered floor-like renderers). Must install AFTER WeatherRig.Install() — its own OnSceneLoaded
            // reads WeatherRig.IsActiveForCurrentMap()/RainCoverForCurrentMap(), same ordering reasoning as
            // LightingLab.Install()/RainCoverRepairs.InstallLab() above. See IndoorFloorSmoothnessRepairs.cs.
            IndoorFloorSmoothnessRepairs.Install();
            RaidCoverBootstrap.Install(); // after indoor state creation; starts from raid/camera readiness

            // Playtest finding 2026-09-14 20:09 — large flat white untextured blocks blocking movement.
            // Removes Arena's own TeamFight play-area borders (config-gated) plus a narrow name-based
            // safety net for components disabled in Arena that the current extractor/importer still
            // ships enabled. See MapBorderRepairs.cs doc comment for the full root-cause breakdown.
            MapBorderRepairs.Install();

            // daytime-and-visuals F2-F7 — 2026-09-17 playtest symptom B follow-up. See
            // docs/progress/daytime-and-visuals.md and each file's own doc comment.
            CameraEffectsRepairs.Install();
            // Floodlight glare follow-up (2026-09-27): live UltimateBloom knobs. See BloomTuning.cs.
            BloomTuning.Install();
            AnamorphicFlareTuning.Install();
            AnamorphicFlareTuning.InstallRenderHooks(_harmony);
            HdrImageTuning.Install(_harmony);
            NativeMultiFlare.Install();
            EquatorNightNeon.Install(_harmony);
            CameraRepairHooks.Install(_harmony);
            DecalRepairs.Install();
            NativeDecalRepairs.Install();
            SeaPlaceholderRepairs.Install();
            NativeWaterRepairs.Install();
            NativeSpatialAudio.Install(_harmony);
            PrecipitationAudio.Install(); // missing native rain beds, owned independently of local scenery ambience
            RoomReverb.Install();
            ReverbAudioProbe.Install();
            SpatialAudioProbe.Install(_harmony);
            PuddleFootstepRepairs.Install(_harmony);
            if (DeveloperOptions.Enabled) DaytimeVisualsProbe.Install();

            // docs/progress/shader-compat-and-inspector.md §b — F10 live diagnostic hotkey (any map, no
            // gate/dependency on the repairs above; order doesn't matter, placed last for clarity).
            SceneInspector.Install();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name) != null)
                    Bay5AmbientAudio.Ensure();
            };
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += scene =>
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name) != null)
                    Bay5AmbientAudio.Shutdown();
            };

            HandsAmbientHighlight.Install();
            try
            {
                int removed = RetiredConfiguration.RemoveSavedEntries(Settings, persist: false);
                Log.LogDebug($"[ArenaMapPort][Config] Removed {removed} retired saved options; active settings preserved.");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[ArenaMapPort][Config] Retired saved-option cleanup skipped: {e.Message}");
            }
            EquatorProfiles.BindAll(); // after legacy binds/migrations; independent Day/Night defaults
            MapConfigVisibility.Install(); // after all binds; keep F12 focused on Equator and Bay5
            CompactConfiguration.Complete(Config);
            Log.LogDebug($"[ArenaMapPort][Config] Public entries={Config.Count}, tuning entries={Settings.Count}; " +
                $"tuning persistence={(DeveloperOptions.Enabled ? "developer" : "read-only")}, next-raid natural night={PlayerOptions.NaturalEquatorNight}.");

            Log.LogInfo($"[ArenaMapPort] {PluginVersion} loaded. Maps: {string.Join(", ", ArenaMapRegistry.All.Select(map => map.LocaleName))}." +
                (DeveloperOptions.Enabled ? " Developer mode enabled; diagnostics use Debug logging." : ""));
        }

        // Multimap generalization (docs/progress/multimap-generalize.md): loops ArenaMapRegistry.All
        // instead of 3 hardcoded Equator literals — same 3 filenames logged for Equator as before.
        void LogStartupBanner()
        {
            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
            var bundlesDir = Path.Combine(pluginDir, "bundles");

            Log.LogDebug($"[ArenaMapPort] plugin dir: {pluginDir}");

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
                Log.LogDebug($"[ArenaMapPort] found: {path}");
            }
            else
            {
                // Not fatal at Awake() — every hook that needs one of these degrades to a clear per-use
                // error log rather than crashing plugin load (§B.WP4: "log clearly if a target is missing").
                Log.LogWarning($"[ArenaMapPort] Required map file is missing: {path}. Reinstall the complete ArenaMapPort package.");
            }
        }
    }
}
