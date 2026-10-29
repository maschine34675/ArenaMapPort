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
        public const string PluginVersion = "0.4.0";

        public static ManualLogSource Log { get; private set; }

        public static Plugin Instance { get; private set; }

        // Every tuning bind must use Settings. Config is reserved for the compact player menu.
        internal ConfigFile Settings { get; private set; }

        Harmony _harmony;

        void LateUpdate()
        {
            ArenaLightingRepairs.UpdateArtificialSun();
            // The score monitors (docs/progress/score-monitors.md): one flag read per frame while nothing is claimed and no owned raid is loaded. Placed before the
            // gate poll and the notices on purpose: MapNoticeWiringTests pins the notices' tick as the last statement of this method.
            ScoreMonitorView.Tick();
            // One flag read per frame; armed only while the Arena check outlives startup, disarmed after its one log line.
            ArenaInstallationGate.PollOutcome();
            // The in-game map notices (docs/progress/map-notices.md): one flag read per frame while nothing is pending.
            MapNotices.Tick();
        }

        void Awake()
        {
            Instance = this;
            Log = base.Logger;

            CompactConfiguration.Warning = message => Log.LogWarning(message);
            try
            {
                Settings = CompactConfiguration.Initialize(Config);
            }
            catch (Exception e)
            {
                // 2026-10-12: an IOException from the archive replace escaped here once and left the plugin without
                // patches for the whole session (raid load aborted with a bundle 404). Degrade, never die.
                Log.LogError($"[Config] compact configuration failed ({e.GetType().Name}: {e.Message}); " +
                             "using the public configuration file for this session");
                Settings = CompactConfiguration.Fallback(Config);
            }

            // Arena installation gate (0.2.3, user decision 2026-10-19): the maps work only with an installed
            // Escape from Tarkov: Arena. Binds the public General/ArenaInstallPath entry (right after the compact
            // configuration, which keeps it in the public file) and starts the one check on a worker thread; the
            // server registers the locations only with a verified installation, BundleHost.TryServe serves the
            // bundles only with one. Never throws: a failure leaves Arena not verified, the plugin alive.
            try
            {
                ArenaInstallationGate.Initialize(Config);
            }
            catch (Exception e)
            {
                Log.LogError($"{ArenaInstallationGate.Tag} the Arena installation gate failed to start ({e.GetType().Name}: {e.Message}); " +
                             "the Arena maps are not served this session.");
            }

            // In-game map notices (2026-10-22, docs/progress/map-notices.md): the public General/ShowMapNotices switch and the hooks that show why the maps
            // are missing or failed to load. Each step is isolated: an exception must never leave Awake (the 2026-10-12 incident above), and the notices
            // are only a message, never a condition of the maps. The Harmony instance is created here so the hooks are in place before the banner.
            _harmony = new Harmony(PluginGuid);
            try
            {
                MapNotices.BindConfig(Config);
            }
            catch (Exception e)
            {
                Log.LogError($"[Notice] the General/ShowMapNotices setting could not be bound ({e.GetType().Name}: {e.Message}); the messages stay on.");
            }
            try
            {
                MapNotices.Install(_harmony);
            }
            catch (Exception e)
            {
                Log.LogError($"[Notice] the map messages could not be installed ({e.GetType().Name}: {e.Message}); the maps themselves are not affected.");
            }
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

            EquatorProfiles.Install(_harmony); // capture the chosen profile before owned scenes load
            HarmonyPatches.InstallAll(_harmony);
            LightingModCompatibility.Install(_harmony);
            DynamicMapsIntegration.Install(_harmony);
            ArenaModeMapPositionIntegration.Install(_harmony);
            SelectionMapArtwork.Install(_harmony);

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
            // Blurred optics (2026-10-16): the vanilla Scripts scenes' TechnicalManagers/ScopeMaskRenderer is
            // missing on ported maps, so the aiming depth-of-field blurred the sight picture. See ScopeMaskRepairs.cs.
            ScopeMaskRepairs.Install();
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
            RoomShotAcoustics.Install(); // per-room gunshot reverb from invented room data, on by default
            ReverbAudioProbe.Install();
            SpatialAudioProbe.Install(_harmony);
            SpectatorAudio.Install(); // developer-only live hearing comparison; player defaults remain enabled
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
                Log.LogDebug($"[Config] Removed {removed} retired saved options; active settings preserved.");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[Config] Retired saved-option cleanup skipped: {e.Message}");
            }
            EquatorProfiles.BindAll(); // after legacy binds/migrations; independent Day/Night defaults
            MapConfigVisibility.Install(); // after all binds; keep F12 focused on the released maps
            CompactConfiguration.Complete(Config);
            Log.LogDebug($"[Config] Public entries={Config.Count}, tuning entries={Settings.Count}; " +
                $"tuning persistence={(DeveloperOptions.Enabled ? "developer" : "read-only")}, next-raid natural night={PlayerOptions.NaturalEquatorNight}.");

            // Without a verified Arena the server registers no Arena location, so the summary says "none available"
            // instead of listing maps next to "not found" (ArenaInstallationGate.MapsSummary).
            Log.LogInfo($"Version {PluginVersion} loaded. " +
                $"{ArenaInstallationGate.MapsSummary(ArenaMapRegistry.All.Select(map => map.LocaleName))}" +
                (DeveloperOptions.Enabled ? " Developer mode enabled; diagnostics use Debug logging." : ""));
        }

        // Multimap generalization (docs/progress/multimap-generalize.md): loops ArenaMapRegistry.All
        // instead of 3 hardcoded Equator literals — same 3 filenames logged for Equator as before.
        void LogStartupBanner()
        {
            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
            var bundlesDir = Path.Combine(pluginDir, "bundles");

            Log.LogDebug($"plugin dir: {pluginDir}; {ArenaInstallationGate.StateSummary()}");

            foreach (var map in ArenaMapRegistry.All)
            {
                LogExpectedFile(Path.Combine(bundlesDir, map.PresetBundleFileName));
                LogExpectedFile(Path.Combine(bundlesDir, map.ScenesBundleFileName));
                LogExpectedFile(Path.Combine(bundlesDir, map.NavMeshBundleFileName));
                LogExpectedFile(Path.Combine(pluginDir, ShaderRebind.SidecarFileName(map)));
            }
        }

        void OnDestroy() => SelectionMapArtwork.Dispose();

        void LogExpectedFile(string path)
        {
            if (File.Exists(path))
            {
                Log.LogDebug($"found: {path}");
            }
            else
            {
                // Not fatal at Awake() — every hook that needs one of these degrades to a clear per-use
                // error log rather than crashing plugin load (§B.WP4: "log clearly if a target is missing").
                Log.LogWarning($"Required map file is missing: {path}. Reinstall the complete ArenaMapPort package.");
            }
        }
    }
}
