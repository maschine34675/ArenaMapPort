using System;
using System.Collections;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using Comfort.Common;
using EFT.CameraControl;
using EFT.Weather;
using Prism.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Weather rig, Stage 0 (Bay5 prototype). See docs/progress/weather-rig-stage0.md and the
    /// implementation spec it was built from for the full evidence trail. Short version:
    ///
    ///  - LIGHTING-DESIGN.md's Candidate B: harvest a vanilla SPT built-in "&lt;Donor&gt;_Scripts" scene's
    ///    "Weather" (full rig) or "Weather_Simple" (simplified) root, deep-clone it, unload the donor
    ///    scene, keep the clone inactive on a DontDestroyOnLoad host, and toggle it active/inactive per
    ///    raid — so <see cref="EFT.Weather.WeatherController"/>/<c>TOD_Sky</c>/<c>AmbientLight</c> feed
    ///    this map's ambient/sky/fog instead of the flat LevelSettings ambient
    ///    <see cref="ArenaLightingRepairs"/> stands in for on every other map.
    ///  - Stage 0 scope: <c>arena_bay5</c> only, Donor = "Reserve_Base_Scripts" (level139, full rig — the
    ///    smallest full-rig donor surveyed; Laboratory's "Weather_Simple" is smaller but has no dome/sun,
    ///    the wrong shape for an outdoor map). Every other map's Donor defaults to "Off" — a complete
    ///    no-op, byte-identical to today's ArenaLightingRepairs-only behavior.
    ///  - Harvest pre-flight gate (<see cref="PreflightClear"/>): "no raid of ANY kind is currently
    ///    loaded" (not just "not one of ours") — a vanilla SPT raid played earlier in the session authors
    ///    the identical LevelSettings/LocationScene/CullingManager/ObservedCullingManager/
    ///    EnvironmentManagerBase/WeatherController singleton set our own raids do, so harvesting while any
    ///    of those exist risks colliding with them. Six independent, freshly re-decompiled checks; see
    ///    each field's own comment for citations.
    ///  - Activation is a plain <c>SetActive(true)</c>/<c>SetActive(false)</c> on the pre-harvested clone
    ///    from THIS plugin's own Scripts-scene sceneLoaded/sceneUnloaded pair (no new Harmony patch) —
    ///    the clone is instantiated from an INACTIVE donor root so Awake/OnEnable/Start are deferred to
    ///    the SetActive(true) call, which runs synchronously and completes before
    ///    <c>BaseLocalGame.InitWeather</c> can ever see a null <c>WeatherController.Instance</c>. See
    ///    <see cref="OnSceneLoaded"/>'s own comment for the full ordering argument.
    ///  - Review fix-ups (docs/progress/weather-rig-stage0.md, "Review fix-ups"): Unity's own message
    ///    dispatch try-catches each MonoBehaviour's Awake/OnEnable/Start internally and logs to console —
    ///    it never lets such an exception reach the C# caller of SetActive()/Instantiate(). A failed child
    ///    Awake is therefore NOT something the try/catch around <c>rig.Root.SetActive(true)</c> can catch
    ///    by itself; <see cref="VerifyFirstActivation"/> is the actual detector (re-reads a couple of the
    ///    fields Awake() is responsible for setting and throws — a real, catchable C# exception — if they
    ///    look wrong). Deactivation also now explicitly unsubscribes/releases the rig's singletons and
    ///    <c>CameraManager.OnCameraChanged</c> subscription (<see cref="OnSceneUnloaded"/>) — SetActive(false)
    ///    alone does not, and since the clone is reused (never <c>Destroy</c>'d), <c>OnDestroy</c>'s own
    ///    cleanup never fires either; left alone this stale state would silently redirect every OTHER
    ///    map's raid onto this rig's frozen sky/ambient for the rest of the session.
    ///  - Everything this file creates (the clone, its DontDestroyOnLoad host) is brand-new state; every
    ///    donor scene it loads is a vanilla SPT built-in scene already safe to load per
    ///    LIGHTING-DESIGN.md's own distribution/legality note. No project file, bundle, or vanilla file is
    ///    ever written by this file.
    /// </summary>
    internal static class WeatherRig
    {
        internal enum SkyboxMode { Dome, FrozenStatic }
        internal enum RigKind { Full, Simplified }

        // donor-settings-camera-weather §A.3 — LevelSettings.MaxMapTextureMemory is a Vector4 streaming
        // budget (LevelSettings.cs:111), not something worth exposing as 4 raw floats in the config UI.
        // ReserveTier's numbers are Reserve_Base_Scripts' own authored value (critic review,
        // 2026-09-17 — exact match against levelsettings_raw.json); Customs/LaboratoryTier are left as
        // TODOs (same enum shape, not yet measured) since only Reserve has been harvested this pass —
        // Stub = today's stub value (1024,1024,1024,1024), the untouched C# class default.
        internal enum TextureBudgetTier { Stub, ReserveTier, CustomsTier, LaboratoryTier }

        // §12 (docs/progress/donor-settings-camera-weather.md) goal 1 — real ambient fix. Donor = write
        // Reserve's own authored ambient (SkyColor=black, EquatorColor=GroundColor=(0.604,0.635,0.651),
        // AmbientIntensity=0, AmbientMode stays Flat) into this map's LevelSettings before
        // ApplySettings(), so Unity's own implicit default environment reflection (no ReflectionProbe
        // exists anywhere in these scenes) sees a black ambient the way every vanilla map's own Unity
        // ambient does — confirmed by the 2026-09-18 14:52 playtest (LightingLab candidate A=DonorBlack)
        // to remove the outdoor white ground veil. Stub = leave AuthorScriptsScene.cs's freshly-authored
        // stub (0.5 grey / intensity 1) untouched — a diagnostic fallback, not expected to be selected
        // for a rig-active map.
        internal enum AmbientSource { Donor, Stub }

        // §12 goal 3 / §13 goal 2 — rain-cover fix for ported maps. No ArenaMapPort map authors a
        // WeatherObstacle/DryPlane/Terrain-layer the way a vanilla donor does, so DepthPhotograper's
        // top-down depth photo is empty and every point on the map reads "uncovered" (RainCoverRepairs.cs).
        // RoofDepthBlack (new, §13, the default) mirrors vanilla's own mechanism byte-for-byte: a Harmony
        // prefix on DepthPhotograper.RenderObstacles draws every elevated static mesh (roofs/overhangs,
        // found by the same once-per-raid scan) in "Hidden/Black" onto the Terrain layer via
        // Graphics.DrawMesh(..., depthCamera) — exactly what WeatherObstacle.MeshCollider's combined
        // DryPlane mesh does on a real donor — and does NOT widen the depth camera's cullingMask (stays
        // Terrain-only, like vanilla). RoofDepth (legacy, kept for A/B comparison) instead widens the
        // depth camera's cullingMask to every layer the bounds scan found, so the map's own renderers get
        // photographed directly — unverified whether this draws anything at all for a Forward-path camera
        // against deferred-only shaders (see docs/progress/donor-settings-camera-weather.md §13). Both
        // still compute LevelSettings.RainBounds from the map's own static geometry once per raid. Off =
        // today's behavior (class-default RainBounds, Terrain-only cullingMask, no obstacle draws).
        internal enum RainCoverMode { RoofDepth, RoofDepthBlack, Off }

        // §12 goal 1 — Reserve_Base_Scripts' own authored LevelSettings ambient values, re-verified
        // against LevelSettings.cs:35,37,39,41 (SkyColor=Color.black class default override,
        // EquatorColor/GroundColor are literally the untouched C# class defaults since Reserve never
        // overrides them, AmbientIntensity=0). Single source of truth — LightingLab's own Candidate A
        // reads these via GetDonorAmbient instead of keeping its own duplicate copy (goal 2's "do not
        // duplicate constants").
        static readonly Color DonorBlackSky = Color.black;
        static readonly Color DonorBlackEquatorGround = new Color(0.6039216f, 54f / 85f, 0.6509804f, 1f);
        const float DonorBlackAmbientIntensity = 0f;

        internal static void GetDonorAmbient(out Color sky, out Color equatorGround, out float intensity)
        {
            sky = DonorBlackSky;
            equatorGround = DonorBlackEquatorGround;
            intensity = DonorBlackAmbientIntensity;
        }

        const string OffDonor = "Off";
        const float PollIntervalSeconds = 3f;
        const int MaxHarvestAttempts = 5;

        // donor-settings-camera-weather §A.1 — Reserve's own authored MaxMapTextureMemory (critic
        // review: numerically confirmed against levelsettings_raw.json). Customs/Laboratory are not yet
        // measured this pass (TextureBudgetTier.CustomsTier/LaboratoryTier fall back to Stub with a
        // warning if ever selected — see TierToVector4).
        static readonly Vector4 ReserveTextureBudget = new Vector4(2000f, 2450f, 2850f, 3100f);
        static readonly Vector4 StubTextureBudget = new Vector4(1024f, 1024f, 1024f, 1024f); // LevelSettings.cs class default

        sealed class RigConfigBundle
        {
            internal BepInEx.Configuration.ConfigEntry<string> Donor;
            internal BepInEx.Configuration.ConfigEntry<SkyboxMode> SkyboxModeEntry;
            internal BepInEx.Configuration.ConfigEntry<bool> KeepFillLight;

            // donor-settings-camera-weather §A.3 — donor-consistent LevelSettings overrides, applied only
            // while a rig is active for this map (ApplyDonorLevelSettingsOverrides). New key names
            // throughout (never "ZeroLevel"/"HeightFalloff" etc verbatim) per the project's hard rule:
            // BepInEx persists a user's saved value under the OLD key even after a default changes, so a
            // stale "ZeroLevel" key from some earlier build could otherwise silently resurrect a value
            // that no longer means what it used to.
            internal BepInEx.Configuration.ConfigEntry<float> ZeroLevelOverride;
            internal BepInEx.Configuration.ConfigEntry<float> HeightFalloffOverride;
            internal BepInEx.Configuration.ConfigEntry<TextureBudgetTier> TextureBudgetTierEntry;
            internal BepInEx.Configuration.ConfigEntry<Vector3> RainBoundsCenter;
            internal BepInEx.Configuration.ConfigEntry<Vector3> RainBoundsSize;
            internal BepInEx.Configuration.ConfigEntry<float> NorthDirectionOverride;

            // donor-settings-camera-weather §B.2/§B.3 — donor camera/PrismPreset/PostProcessProfile
            // takeover (ApplyDonorCameraPrefab). False falls straight back to today's Cam2 fallback path
            // (LevelSettings.CameraPrefab stays AuthorScriptsScene.cs's authored null) — see §B.3.
            internal BepInEx.Configuration.ConfigEntry<bool> UseDonorCameraPrefab;

            // §12 goal 1/3 — new keys, see AmbientSource/RainCoverMode's own doc comments.
            internal BepInEx.Configuration.ConfigEntry<AmbientSource> UnityAmbientEntry;
            internal BepInEx.Configuration.ConfigEntry<RainCoverMode> RainCoverEntry;

            // §13 goal 2 — RoofDepthBlack's elevated-mesh filter threshold. See RainCoverRepairs.cs.
            internal BepInEx.Configuration.ConfigEntry<float> CoverMinHeightEntry;
        }

        // Internal (not the default private) — LightingDiagnosticProbe.LastHarvestResult returns this
        // type, so it must be at least as accessible as that internal method (CS0053 otherwise).
        internal sealed class HarvestedRig
        {
            internal GameObject Root;               // clone root, parented under _host, inactive until raid
            internal RigKind Kind;
            internal WeatherController Controller;   // null for Simplified
            internal TOD_Sky Sky;                    // null for Simplified
            internal TODSkySimple SimpleSky;         // null for Full

            // donor-settings-camera-weather §B.1 (critic amendment 1, 2026-09-17): captured DIRECTLY off
            // the donor's LevelSettings singleton while the donor scene is still loaded — NOT
            // Instantiate()'d/SetActive(false)'d the way "Weather" is. Reserve/Laboratory/Customs'
            // CameraPrefab all resolve to the identical m_PathID:691 object in sharedassets15.assets — a
            // persistent, cross-map, process-wide shared asset that survives SceneManager.
            // UnloadSceneAsync(donorScene) on its own, unlike a scene-local object such as "Weather".
            // Null for a donor whose LevelSettings never authors one (defensive; every donor surveyed so
            // far does). PrismPreset/PostProcessProfile are almost always null too (Reserve authors
            // neither) — carried through generically for whatever future donor does.
            internal Camera CameraPrefab;
            internal PrismPreset PrismPreset;
            internal PostProcessProfile PostProcessProfile;

            // One-time harvest-health snapshot, surfaced by LightingDiagnosticProbe. A HarvestedRig is
            // only ever constructed on full success (every failure path returns before one is built), so
            // these four are always true when a HarvestedRig exists — kept as explicit fields anyway
            // (rather than that fact being implicit) so the probe's log line is self-explanatory without
            // cross-referencing this file.
            internal bool LoadOk, CloneOk, UnloadOk, ValidationOk;
            internal int KeptComponents;
            internal bool LevelSettingsUnchanged, LocationSceneUnchanged, EnvironmentManagerUnchanged;

            // Review fix-ups #1/#4 — false until the very first successful SetActive(true). Awake/OnEnable/
            // Start only ever run once per clone lifetime (the clone is reused, never Destroy()'d), so this
            // tells OnSceneLoaded whether to run VerifyFirstActivation's Awake-postcondition check (only
            // meaningful the first time) versus just ReestablishSingletons (needed every time after).
            internal bool EverActivated;

            // Review fix-ups (docs/progress/donor-settings-camera-weather.md) #1 — RainController's own
            // vanilla-authored PuddleWaterLevel.y (0.95, RainController.cs:417), captured once at harvest
            // time, BEFORE any raid's RainWetnessRepairs.ApplyPuddleClamp call can overwrite the field.
            // Needed because this clone is a session-persistent, SetActive(true)/SetActive(false)-only
            // object — never Destroy()'d (see OnSceneUnloaded's own comment) — reused across every raid on
            // this donor for the rest of the session: without a captured original, turning
            // MaxLyingWaterOverrideEnabled back off after a raid that had it on has nothing correct to
            // restore PuddleWaterLevel.y to, and the clamped value would otherwise leak into every later
            // raid on this donor regardless of the config. Defaults to the vanilla constant for a
            // Simplified rig (no RainController) or the defensive null-Controller case; never actually read
            // in that case since ApplyPuddleClamp is only called for RigKind.Full.
            internal float OriginalPuddleWaterLevelY = 0.95f;
        }

        static readonly Dictionary<string, RigConfigBundle> _configs = new Dictionary<string, RigConfigBundle>();
        static readonly Dictionary<string, HarvestedRig> _harvested = new Dictionary<string, HarvestedRig>(); // keyed by donor scene name
        static readonly Dictionary<string, int> _failedAttempts = new Dictionary<string, int>();              // keyed by donor scene name
        static readonly HashSet<string> _permanentlyFailed = new HashSet<string>();                            // donor scene names
        static readonly HashSet<string> _pendingDonors = new HashSet<string>();                                // donor scene names still to try
        static GameObject _host;
        static bool _installed;
        static bool _loopRunning; // guards OnDonorConfigChanged from starting a second, overlapping HarvestLoop
        static ArenaMapDescriptor _activeMap; // map this raid's clone was activated for, or null

        // Review fix-ups #5 — the rig OnSceneLoaded actually activated for _activeMap, tracked directly
        // instead of re-derived from the live Donor ConfigEntry at unload time (which can name a
        // different/not-yet-harvested donor if it was edited between this raid's load and unload).
        static HarvestedRig _activeRig;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _host = new GameObject("ArenaMapPort_WeatherRigHost") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_host);

            foreach (var map in ArenaMapRegistry.All)
            {
                _configs[map.Key] = BindConfig(map);
            }
            RecomputePendingDonors();

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _host.AddComponent<Runner>().StartCoroutine(HarvestLoop());
        }

        sealed class Runner : MonoBehaviour { }

        // ---------------------------------------------------------------------------------------------
        // Config binding — one new RigConfigBundle per map, same "Lighting (<key>)" section
        // ArenaLightingRepairs.BindConfig already uses (ArenaLightingRepairs.cs) — BepInEx allows several
        // classes to bind distinct keys into one section; "Donor"/"SkyboxMode"/"KeepFillLight" never
        // collide with ArenaLightingRepairs' own key names.
        // ---------------------------------------------------------------------------------------------
        static RigConfigBundle BindConfig(ArenaMapDescriptor map)
        {
            string section = $"Lighting ({map.Key})";
            string defaultDonor = map.Key == "arena_bay5" ? "Reserve_Base_Scripts" : OffDonor;
            // donor-settings-camera-weather Tier-1/Tier-2 split — real defaults only for the one map this
            // pass actually harvests a rig for (Bay5); every other map keeps Donor="Off" above, so none
            // of §A/§B/§C ever reads these values for it regardless of what they default to.
            bool bay5Defaults = map.Key == ArenaMapRegistry.Bay5.Key;
            var cfg = new RigConfigBundle();

            cfg.Donor = Plugin.Instance.Config.Bind(section, "Donor", defaultDonor,
                new BepInEx.Configuration.ConfigDescription(
                    "Vanilla SPT built-in scene ('<Name>_Scripts', BuildSettings scene, e.g. " +
                    "'Reserve_Base_Scripts') whose Weather/Weather_Simple subtree is harvested once per " +
                    $"process and toggled on for this map's raids. '{OffDonor}' = today's behavior " +
                    "(ArenaLightingRepairs workaround only, no rig). Takes effect on the NEXT raid — never " +
                    "hot-applied mid-raid."));

            cfg.SkyboxModeEntry = Plugin.Instance.Config.Bind(section, "SkyboxMode", SkyboxMode.Dome,
                new BepInEx.Configuration.ConfigDescription(
                    "Dome = the donor's real procedural TOD_Sky dome renders. FrozenStatic = disable the " +
                    "dome's painter GameObjects (Atmosphere/Clear/Space/Sun/Moon/cloud renderer) if the dome " +
                    "renders with a horizon seam or a flat white/black atmosphere (Icebreaker's own documented " +
                    "failure mode for a cross-build sky rip) — ambient SH, reflections, fog and wind/rain keep " +
                    "running either way. Only read when Donor != 'Off'. Takes effect on the NEXT raid."));

            cfg.KeepFillLight = Plugin.Instance.Config.Bind(section, "KeepFillLight", false,
                new BepInEx.Configuration.ConfigDescription(
                    "When a rig is active for this map (Donor != 'Off' AND harvested successfully in time for " +
                    "this raid), ArenaLightingRepairs' own FillLightIntensity is forced to 0 regardless of its " +
                    "own value, since the rig's real AmbientLight/_SHAr now feeds ambient instead. Set true to " +
                    "keep the fill light on top of the real rig (e.g. as extra interior fill)."));

            // donor-settings-camera-weather §A.3 — Tier-1 default ON for a rig-active map (Bay5): the
            // "vanilla-consistent set" itself. ZeroLevel is explicitly NOT a blind donor copy (plan §A.1 /
            // critic — Reserve's own ZeroLevel is Reserve-geometry-relative, not transferable) — seeded
            // with the project brief's own starting guess instead (ground ~ -3, sky dome much higher;
            // -8.2 split the difference for the first playtest). HeightFalloff DOES match the donor
            // exactly (Reserve AND Customs both author 0.025 — critic-confirmed against
            // levelsettings_raw.json), so it's copied straight.
            cfg.ZeroLevelOverride = Plugin.Instance.Config.Bind(section, "ZeroLevelOverride", bay5Defaults ? -8.2f : 0f,
                "TOD_Scattering reads Singleton<LevelSettings>.Instance.ZeroLevel every rendered frame " +
                "(TOD_Scattering.cs:165-176) once a rig is active (CameraEffectsRepairs adds the " +
                "component) — our stub LevelSettings ships ZeroLevel=-78.64 (Laboratory's own authored " +
                "value, coincidentally equal to the raw C# class default), wrong by roughly 10x for an " +
                "outdoor map. This is a per-map, geometry-relative tuning value, NOT a donor copy — see " +
                "docs/progress/donor-settings-camera-weather.md for the evidence trail and how to re-tune " +
                "it. Applied only while a rig is active for this map (Donor != 'Off' and harvested).");

            cfg.HeightFalloffOverride = Plugin.Instance.Config.Bind(section, "HeightFalloffOverride", bay5Defaults ? 0.025f : 0.029f,
                "TOD_Scattering's other per-frame ZeroLevel/HeightFalloff read (see ZeroLevelOverride). " +
                "0.025 is Reserve's (and Customs') own authored value, byte-for-byte, vs. our stub's " +
                "0.029 (Laboratory's authored value / the raw C# class default) — a real donor copy, not a " +
                "guess. Applied only while a rig is active for this map.");

            cfg.TextureBudgetTierEntry = Plugin.Instance.Config.Bind(section, "TextureBudgetTier",
                bay5Defaults ? TextureBudgetTier.ReserveTier : TextureBudgetTier.Stub,
                "LevelSettings.MaxMapTextureMemory (a Vector4 streaming-texture budget, LevelSettings.cs:" +
                "111,198) — our stub ships (1024,1024,1024,1024), the raw C# class default. ReserveTier " +
                "= Reserve_Base_Scripts' own authored (2000,2450,2850,3100) — streaming-quality only, no " +
                "plausible visual-correctness downside. Applied only while a rig is active for this map.");

            cfg.RainBoundsCenter = Plugin.Instance.Config.Bind(section, "RainBoundsCenter", Vector3.zero,
                "LevelSettings.RainBounds' center (LevelSettings.cs:30). Default = the class's own default " +
                "center (0,0,0) — a deliberate no-op until Bay5's real play-area coordinates are measured " +
                "(see docs/progress/donor-settings-camera-weather.md, deferred). Applied only while a rig " +
                "is active for this map.");
            cfg.RainBoundsSize = Plugin.Instance.Config.Bind(section, "RainBoundsSize", new Vector3(1000f, 220f, 1000f),
                "LevelSettings.RainBounds' size. Default = the class's own default size — see " +
                "RainBoundsCenter's own description for why this ships as a no-op this pass.");

            cfg.NorthDirectionOverride = Plugin.Instance.Config.Bind(section, "NorthDirectionOverride", 0f,
                new BepInEx.Configuration.ConfigDescription(
                    "LevelSettings._northDirection (LevelSettings.cs:108, degrees). Default 0 = our current " +
                    "authored value (unset) — the 2026-09-17 playtest already confirmed the sun direction " +
                    "correct at this value once the dome-recenter fix landed, so this ships as a deliberate " +
                    "no-op, NOT a donor copy (donor North is Reserve-map-relative, meaningless here). " +
                    "Tune only after its own dedicated playtest. Applied only while a rig is active for " +
                    "this map.",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 360f)));

            // donor-settings-camera-weather §B.2/§B.3 (critic amendment: capture-only, no Instantiate of
            // the donor's original — see HarvestedRig's own doc comment). Default true for a rig-active
            // map — the core of the "consistent donor set" strategy (rig + LevelSettings + camera all
            // from the same donor) — but a real, single-click toggle: falling back to the Cam2 fallback
            // path (§B.3) needs no other config change.
            cfg.UseDonorCameraPrefab = Plugin.Instance.Config.Bind(section, "UseDonorCameraPrefab", bay5Defaults,
                "Assigns the harvested donor's own CameraPrefab/PrismPresetPrefab/PostProcessProfilePrefab " +
                "onto this map's LevelSettings while a rig is active, so CameraManager.SetCameraFromSettings " +
                "instantiates the donor's real camera (with its own baked TOD_Scattering/PrismEffects." +
                "useExposure) instead of falling back to Cam2. false = always Cam2, matching today's " +
                "behavior regardless of the rig. Takes effect on the next raid.");

            // §12 goal 1 — real ambient fix, unconditional default Donor (harmless for a no-rig map —
            // Donor="Off" above means OnSceneLoaded never reaches ApplyDonorLevelSettingsOverrides for it).
            cfg.UnityAmbientEntry = Plugin.Instance.Config.Bind(section, "UnityAmbient", AmbientSource.Donor,
                "§12 goal 1 — Donor = write Reserve_Base_Scripts' own authored ambient (SkyColor=black, " +
                "EquatorColor=GroundColor=(0.604,0.635,0.651), AmbientIntensity=0) into this map's " +
                "LevelSettings before ApplySettings(), every sceneLoaded — fixes the outdoor white ground " +
                "veil (2026-09-18 14:52 playtest, LightingLab candidate A=DonorBlack). Stub = leave " +
                "AuthorScriptsScene.cs's freshly-authored stub (0.5 grey / intensity 1) untouched. No " +
                "effect on a map with no active rig (Donor='Off' above). Applied on the NEXT raid, then " +
                "kept live every camera pre-cull by LevelSettings.OnPreCullCallback (BSG code) once " +
                "written — see docs/progress/donor-settings-camera-weather.md §12.");

            // §12 goal 3 / §13 goal 2 — rain-cover fix, unconditional default RoofDepthBlack (same
            // reasoning as UnityAmbientEntry above). §13: adding RoofDepthBlack as a new enum VALUE (not a
            // new key) is safe per this project's hard rule — BepInEx serializes an enum ConfigEntry by
            // NAME, and both old values ("RoofDepth"/"Off") still parse byte-for-byte from an existing
            // saved config; only the unset/default case changes to the new value.
            // Key renamed from "RainCover" (saved as RoofDepth in existing configs) so the new default
            // takes effect; the old key stays orphaned in the cfg.
            cfg.RainCoverEntry = Plugin.Instance.Config.Bind(section, "RainCoverMethod", RainCoverMode.RoofDepthBlack,
                "§13 — RoofDepthBlack (default) draws every elevated static mesh (roofs/overhangs, found " +
                "by the same once-per-raid geometry scan) in black onto DepthPhotograper's depth camera via " +
                "Graphics.DrawMesh, the same mechanism a real donor's WeatherObstacle/DryPlane does — fixes " +
                "the indoor wet-floor/sky-reflection veil (2026-09-18 14:52/20:20 playtests) the vanilla-" +
                "faithful way. RoofDepth (§12, legacy) instead widens the depth camera's cullingMask to " +
                "every layer the scan found — kept only for A/B comparison; the 2026-09-18 20:20 playtest " +
                "found it ineffective (SetupDepthCamera forces RenderingPath.Forward, and this map's " +
                "deferred-only shaders very plausibly draw nothing into a forward-path camera). Off = " +
                "today's original behavior (class-default RainBounds, Terrain-only cullingMask, no obstacle " +
                "draws — an empty depth map). Both RoofDepth/RoofDepthBlack still compute " +
                "LevelSettings.RainBounds from this map's own static geometry once per raid. See " +
                "RainCoverRepairs.cs. Applied fresh every raid, not live.");

            // §13 goal 2 — RoofDepthBlack's elevated-mesh filter: a renderer only becomes a black-draw
            // candidate if its bounds.min.y sits at least this far above the ground/collider hit directly
            // below it (or, with no hit, above the scanned map's own min.y) — mirrors vanilla's own
            // DryPlane roofs/overhangs never including ground-level geometry. 2.2m is this task's own
            // starting value (Reserve's 637 DryPlane instances span a rough 5-13m spread above their local
            // reference height per docs/progress/donor-settings-camera-weather.md §13 investigation (c) —
            // not a precise per-plane ground-clearance measurement, so treat 2.2m as a reasonable floor,
            // not a donor-matched constant). Only read when RainCover=RoofDepthBlack.
            cfg.CoverMinHeightEntry = Plugin.Instance.Config.Bind(section, "CoverMinHeight", 2.2f,
                new BepInEx.Configuration.ConfigDescription(
                    "§13 — minimum height (meters) a static renderer's own bounds.min.y must clear the " +
                    "ground/collider directly below it (or the scanned map's own min.y, if nothing is hit) " +
                    "to be treated as an elevated roof/overhang and drawn black onto the rain-cover depth " +
                    "camera. Only used when RainCover=RoofDepthBlack.",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 50f)));

            // Deliberately NO SettingChanged handlers on these three for re-apply purposes — "changes apply
            // on the next raid" is a hard requirement here (unlike ArenaLightingRepairs' own live-tunable
            // knobs): re-toggling SetActive or re-running the harvest mid-raid is not supported. The one
            // exception: Donor's own SettingChanged pre-warms the harvest for a newly-typed donor name
            // between raids, never reacting during an active one. Same "next raid only" rule applies to
            // every §A/§B ConfigEntry added above — none of them re-apply live either.
            cfg.Donor.SettingChanged += (_, __) => OnDonorConfigChanged();

            return cfg;
        }

        static Vector4 TierToVector4(TextureBudgetTier tier)
        {
            switch (tier)
            {
                case TextureBudgetTier.ReserveTier:
                    return ReserveTextureBudget;
                case TextureBudgetTier.CustomsTier:
                case TextureBudgetTier.LaboratoryTier:
                    // Not measured this pass (only Reserve was harvested/inspected) — fall back to Stub
                    // rather than guess at a plausible-looking wrong number.
                    Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] TextureBudgetTier={tier} has no measured " +
                        "value yet this pass — falling back to Stub. See docs/progress/" +
                        "donor-settings-camera-weather.md §Deferred.");
                    return StubTextureBudget;
                default:
                    return StubTextureBudget;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Harvest pre-flight gate + retry bookkeeping.
        // ---------------------------------------------------------------------------------------------

        // "No raid of any kind is currently loaded" — a vanilla SPT raid played earlier this session
        // authors the identical singleton/manager set our own maps do, so gating on
        // !ArenaMapRaid.IsLoaded() alone is insufficient. All six re-verified fresh against the
        // decompile (Comfort.dll ilspycmd -t; LocationScene.cs; CullingManager.cs; ObservedCullingManager
        // .cs; LevelSettings.cs; EnvironmentManagerBase.cs + EFT.EnvironmentEffect/EnvironmentManager.cs;
        // EFT.Weather/WeatherController.cs).
        static bool PreflightClear() =>
            !Singleton<LevelSettings>.Instantiated &&
            LocationScene.LoadedScenes.Count == 0 &&
            CullingManager.Instance == null &&
            !Singleton<ObservedCullingManager>.Instantiated &&
            EnvironmentManagerBase.Instance == null &&
            EFT.Weather.WeatherController.Instance == null;

        // Re-reads every map's current Donor value; any name not already harvested/permanently failed and
        // not "Off" is (re-)added to _pendingDonors. Called once at Install() and again whenever a Donor
        // ConfigEntry changes (OnDonorConfigChanged) so a tester can pre-warm a newly-typed donor between
        // raids without restarting the game.
        static void RecomputePendingDonors()
        {
            foreach (var cfg in _configs.Values)
            {
                string donor = cfg.Donor.Value;
                if (donor == OffDonor || _harvested.ContainsKey(donor) || _permanentlyFailed.Contains(donor))
                {
                    continue;
                }
                _pendingDonors.Add(donor);
            }
        }

        static void OnDonorConfigChanged()
        {
            int before = _pendingDonors.Count;
            RecomputePendingDonors();
            // Only (re)start the loop if it isn't already running — two concurrent HarvestLoop coroutines
            // could both snapshot _pendingDonors on the same tick and both attempt HarvestOne for the same
            // donor at once (SceneManager.LoadScene is not meant to be re-entered for the same scene
            // concurrently). If the loop is still running, it will pick the new donor up on its own next
            // pass — RecomputePendingDonors already added it to the live _pendingDonors set above.
            if (_pendingDonors.Count > before && !_loopRunning)
            {
                _host.AddComponent<Runner>().StartCoroutine(HarvestLoop());
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Harvest coroutine. Retry loop, not a one-shot check: polls every PollIntervalSeconds; a donor
        // stops being retried after MaxHarvestAttempts consecutive FAILED harvest attempts (load/clone/
        // unload threw or produced a corrupted result) — not after any number of ticks where the gate was
        // simply not clear yet, which can legitimately last the whole length of a raid.
        // ---------------------------------------------------------------------------------------------
        static IEnumerator HarvestLoop()
        {
            _loopRunning = true;
            try
            {
                while (_pendingDonors.Count > 0)
                {
                    yield return new WaitForSeconds(PollIntervalSeconds);

                    // Snapshot: iterating a live HashSet while HarvestOne can mutate it (success removes
                    // the entry) needs a copy.
                    var attempt = new List<string>(_pendingDonors);
                    foreach (var donorName in attempt)
                    {
                        if (!PreflightClear())
                        {
                            continue; // expected during an active raid — no log, this is the common case
                        }
                        yield return HarvestOne(donorName);
                    }
                }
                Plugin.Log.LogInfo("[ArenaMapPort][WeatherRig] harvest loop finished — " +
                    $"{_harvested.Count} donor(s) ready, {_permanentlyFailed.Count} permanently failed this session.");
            }
            finally
            {
                _loopRunning = false;
            }
        }

        static IEnumerator HarvestOne(string donorName)
        {
            Scene donorScene = default;
            GameObject weatherRoot = null;
            GameObject clone = null;
            // donor-settings-camera-weather §B.1 — captured (not cloned) while the donor scene is still
            // loaded; see the doc comment where these land on HarvestedRig for why a plain reference is
            // correct here and Instantiate()/SetActive(false) (the "Weather" root technique) is not.
            Camera donorCameraPrefab = null;
            PrismPreset donorPrismPreset = null;
            PostProcessProfile donorPostProcessProfile = null;

            // Pre-load snapshot for the post-harvest identity check. Must be taken BEFORE the donor loads:
            // the donor's own LevelSettings/LocationScene/EnvironmentManager register during its load and
            // release again on unload, so a snapshot taken after loading always reads as "changed".
            bool lsBefore = Singleton<LevelSettings>.Instantiated;
            int locBefore = LocationScene.LoadedScenes.Count;
            bool envBefore = EnvironmentManagerBase.Instance != null;

            // The donor's LevelSettings.Awake pushes its fog/ambient/skybox into RenderSettings
            // (LevelSettings.cs:194-218) and nothing resets them on unload; restore them afterwards.
            var renderSettingsBefore = RenderSettingsSnapshot.Capture();

            // Unload any copy of the donor left loaded by an earlier failed attempt this session.
            foreach (var leftover in LoadedScenesNamed(donorName))
            {
                AsyncOperation leftoverOp = null;
                try
                {
                    leftoverOp = SceneManager.UnloadSceneAsync(leftover);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] could not unload leftover '{donorName}': " +
                        $"{e.GetType().Name}: {e.Message}");
                }
                if (leftoverOp != null)
                {
                    yield return leftoverOp;
                }
            }

            // Step 1: load. SceneManager.LoadScene only loads on the NEXT frame (the returned Scene is not
            // loaded yet), so use LoadSceneAsync and wait for it. It returns null (and Unity logs an error)
            // when the name is not a BuildSettings scene of this client.
            AsyncOperation loadOp = null;
            try
            {
                loadOp = SceneManager.LoadSceneAsync(donorName, new LoadSceneParameters(LoadSceneMode.Additive));
                if (loadOp == null)
                {
                    throw new InvalidOperationException("LoadSceneAsync returned null");
                }
            }
            catch (Exception e)
            {
                Fail(donorName, $"load failed: {e.GetType().Name}: {e.Message} " +
                    "(donor scene name not in this client's BuildSettings? typo in the Donor config?)");
                yield break;
            }
            yield return loadOp;

            try
            {
                var loaded = LoadedScenesNamed(donorName);
                donorScene = loaded.Count > 0 ? loaded[loaded.Count - 1] : default;
                if (!donorScene.IsValid() || !donorScene.isLoaded)
                {
                    throw new InvalidOperationException("scene not loaded after LoadSceneAsync completed");
                }
            }
            catch (Exception e)
            {
                Fail(donorName, $"load failed: {e.GetType().Name}: {e.Message}");
                yield break;
            }

            // Step 2: find the rig root by name — "Weather" (full) or "Weather_Simple" (simplified).
            try
            {
                foreach (var root in donorScene.GetRootGameObjects())
                {
                    if (root.name == "Weather" || root.name == "Weather_Simple")
                    {
                        weatherRoot = root;
                        break;
                    }
                }
                if (weatherRoot == null)
                {
                    throw new InvalidOperationException("no 'Weather' or 'Weather_Simple' root GameObject found");
                }
            }
            catch (Exception e)
            {
                Fail(donorName, $"rig-root lookup failed: {e.GetType().Name}: {e.Message}");
                SafeUnload(donorScene);
                renderSettingsBefore.Restore();
                yield break;
            }

            // donor-settings-camera-weather §B.1 (critic amendment, 2026-09-17): capture the donor's
            // LevelSettings.CameraPrefab/PrismPresetPrefab/PostProcessProfilePrefab as plain references,
            // taken from the donor's OWN LevelSettings singleton while the donor scene is still loaded —
            // deliberately NOT Instantiate()'d/SetActive(false)'d here, unlike "Weather" above. Reserve's
            // CameraPrefab resolves to a shared, cross-map, process-wide asset in sharedassets15.assets
            // (m_PathID:691, confirmed identical for Laboratory/Customs too) that survives
            // SceneManager.UnloadSceneAsync(donorScene) below on its own — mutating the ORIGINAL (as an
            // earlier draft of this plan did) would have been a real, session-wide side effect on a BSG
            // asset shared by every future real Reserve/Laboratory/Customs raid this session; a bare
            // reference has no such risk, and CameraManager.SetCameraFromPrefab's own Instantiate() (at
            // the correct per-raid moment — see ApplyDonorCameraPrefab) does all the real work anyway.
            // Best-effort only: a null CameraPrefab here (the expected case for most donors surveyed) just
            // means ApplyDonorCameraPrefab later has nothing to assign — never a harvest failure.
            try
            {
                var donorLevelSettings = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
                if (donorLevelSettings != null)
                {
                    donorCameraPrefab = donorLevelSettings.CameraPrefab;
                    donorPrismPreset = donorLevelSettings.PrismPresetPrefab;
                    donorPostProcessProfile = donorLevelSettings.PostProcessProfilePrefab;
                }
            }
            catch (Exception e)
            {
                // Non-fatal — see this block's own comment; the rig itself (Weather root) is unaffected.
                Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] '{donorName}': donor camera/preset capture " +
                    $"failed (non-fatal, rig harvest continues without it): {e.GetType().Name}: {e.Message}");
            }

            // Step 4: SetActive(false) on the DONOR original before cloning, so the CLONE's own Awake/
            // OnEnable/Start are deferred until WE SetActive(true) it later (once the FPS camera/raid
            // context exists). This does NOT undo the donor original's own Awake, which already ran once
            // as part of the normal additive scene load in Step 1 — safe because the pre-flight gate
            // guarantees nothing of ours existed for it to collide with.
            int keptBefore = 0;
            TOD_Sky cloneSky = null;
            TODSkySimple cloneSimple = null;
            try
            {
                weatherRoot.SetActive(false);
                clone = UnityEngine.Object.Instantiate(weatherRoot);
                clone.name = "WeatherRig_" + donorName;
                keptBefore = clone.GetComponentsInChildren<MonoBehaviour>(true).Length;

                // Defensive: register the clone's own TOD_Sky/TODSkySimple with TODSkyProvider immediately,
                // even while the clone is still inactive — closes the "TODSkyProvider.Instance left
                // dangling at the soon-to-be-destroyed donor original" window. Harmless: it is a plain
                // static setter with no active-object requirement (TODSkyProvider.cs). TOD_Sky.Awake()
                // re-registers itself anyway once the clone is later activated for real.
                cloneSky = clone.GetComponentInChildren<TOD_Sky>(true);
                cloneSimple = cloneSky == null ? clone.GetComponentInChildren<TODSkySimple>(true) : null;
                if (cloneSky != null)
                {
                    TODSkyProvider.RegisterInstance(cloneSky);
                }
                else if (cloneSimple != null)
                {
                    TODSkyProvider.RegisterInstance(cloneSimple);
                }
                else
                {
                    throw new InvalidOperationException("clone has neither TOD_Sky nor TODSkySimple");
                }

                // DontDestroyOnLoad BEFORE SetParent: Object.DontDestroyOnLoad only accepts a ROOT
                // GameObject (logs an error and no-ops otherwise) — clone is still root at this point,
                // fresh from Instantiate. Reparenting under _host afterward keeps it in that same
                // DontDestroyOnLoad scene (a child of an already-DontDestroyOnLoad object persists with
                // it), so the explicit call here is defensive/order-correct rather than load-bearing on
                // its own, but calling it AFTER SetParent (clone no longer root) would instead log a
                // Unity error every harvest for no effect.
                UnityEngine.Object.DontDestroyOnLoad(clone);
                clone.transform.SetParent(_host.transform, worldPositionStays: false);

                // Post-clone cross-reference validation — the load-bearing safety net for any future donor
                // whose rig doesn't self-contain everything under one root the way Reserve_Base_Scripts
                // does (WeatherController/CloudController/the full TOD_* rig/RainController/AmbientLight/
                // AmbientHighlight/ReflectionProbe are all already descendants of "Weather" for that donor,
                // confirmed by a live dump_hierarchy.py pass — Instantiate() correctly remaps in-hierarchy
                // references, standard Unity behavior, so this check should always pass for that donor;
                // it exists for whatever donor Stage 1/2 adds next).
                if (cloneSky != null)
                {
                    var ctrl = clone.GetComponentInChildren<WeatherController>(true);
                    if (ctrl == null)
                    {
                        throw new InvalidOperationException("no WeatherController on the clone");
                    }
                    if (ctrl.WindController == null || ctrl.RainController == null || ctrl.CloudController == null)
                    {
                        throw new InvalidOperationException("WeatherController's Wind/Rain/CloudController is " +
                            "null post-clone — cross-root reference did not survive Instantiate");
                    }
                    if (ctrl.TimeOfDayController.AmbientLightScript == null)
                    {
                        // ToDController.Update() calls AmbientLightScript.SetSH(SH) with NO null check
                        // (unlike its sibling AmbientHighlightScript, which IS guarded) — a broken
                        // cross-reference here would NRE every LateUpdate once the rig activates, not
                        // degrade gracefully. Caught here, before ever activating this clone for a raid.
                        throw new InvalidOperationException("ToDController.AmbientLightScript is null " +
                            "post-clone — would NRE every LateUpdate once active (unguarded call site)");
                    }
                }

                clone.SetActive(false); // stays inactive — activation is per-raid, see OnSceneLoaded
            }
            catch (Exception e)
            {
                Fail(donorName, $"clone/validate failed: {e.GetType().Name}: {e.Message}");
                if (clone != null)
                {
                    UnityEngine.Object.Destroy(clone);
                }
                SafeUnload(donorScene);
                renderSettingsBefore.Restore();
                yield break;
            }

            // Step 6: unload — MUST be awaited so the harvested-flag only flips once the donor's own
            // singletons have actually released (LevelSettings/LocationScene/etc. OnDestroy calls fire
            // synchronously as part of UnloadSceneAsync's own destroy pass, but this still waits for the
            // operation itself to report done before trusting post-unload state).
            AsyncOperation unloadOp = null;
            try
            {
                unloadOp = SceneManager.UnloadSceneAsync(donorScene);
            }
            catch (Exception e)
            {
                Fail(donorName, $"UnloadSceneAsync threw synchronously: {e.GetType().Name}: {e.Message}");
                UnityEngine.Object.Destroy(clone);
                yield break;
            }
            if (unloadOp != null)
            {
                yield return unloadOp; // the actual await — a Unity coroutine yield on the AsyncOperation
            }

            // Step 7: post-unload identity check + the one confirmed-persistent side effect: rebuild the
            // physics broadphase regions the donor's own load/unload disturbed. Never reverted by anything
            // else in this repo (AuthorScriptsScene.cs never authors a LevelPhysicsSettings of its own).
            renderSettingsBefore.Restore();
            bool lsUnchanged = Singleton<LevelSettings>.Instantiated == lsBefore;
            bool locUnchanged = LocationScene.LoadedScenes.Count == locBefore;
            bool envUnchanged = (EnvironmentManagerBase.Instance != null) == envBefore;
            try
            {
                Physics.RebuildBroadphaseRegions(new Bounds(Vector3.zero, Vector3.one * 2000f), 1);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] physics-broadphase revert failed (non-fatal): " +
                    $"{e.GetType().Name}: {e.Message}");
            }

            // Review fix-ups #6: these three flags used to be computed and logged but never enforced — a
            // donor whose LevelSettings/LocationScene/EnvironmentManager genuinely leaked past
            // UnloadSceneAsync's own completion (exactly the scenario the pre-flight gate exists to keep
            // from ever colliding with) was still marked ValidationOk=true and made eligible for per-raid
            // activation with corrupted world state. Route it through Fail() instead, same as any other
            // harvest failure — it counts toward MaxHarvestAttempts and eventually permanently fails the
            // donor rather than silently shipping a broken rig.
            if (!lsUnchanged || !locUnchanged || !envUnchanged)
            {
                UnityEngine.Object.Destroy(clone);
                Fail(donorName, "post-unload identity check failed: " +
                    $"LevelSettings-unchanged={lsUnchanged} LocationScene-unchanged={locUnchanged} " +
                    $"EnvironmentManager-unchanged={envUnchanged} — the donor scene's own singletons/" +
                    "registries did not fully release after UnloadSceneAsync completed");
                yield break;
            }

            // Review fix-ups (docs/progress/donor-settings-camera-weather.md) #1 — hoisted out of the
            // object initializer below (was inline as `cloneSky != null ? clone.GetComponentInChildren
            // <WeatherController>(true) : null` for Controller alone) so OriginalPuddleWaterLevelY can
            // read RainController off the SAME lookup instead of a second GetComponentInChildren call.
            var harvestedController = cloneSky != null ? clone.GetComponentInChildren<WeatherController>(true) : null;

            var rig = new HarvestedRig
            {
                Root = clone,
                Kind = cloneSky != null ? RigKind.Full : RigKind.Simplified,
                Controller = harvestedController,
                Sky = cloneSky,
                CameraPrefab = donorCameraPrefab,
                PrismPreset = donorPrismPreset,
                PostProcessProfile = donorPostProcessProfile,
                SimpleSky = cloneSimple,
                OriginalPuddleWaterLevelY = harvestedController != null && harvestedController.RainController != null
                    ? harvestedController.RainController.PuddleWaterLevel.y
                    : 0.95f, // vanilla authored default (RainController.cs:417) — defensive fallback only
                LoadOk = true,
                CloneOk = true,
                UnloadOk = true,
                ValidationOk = true,
                KeptComponents = keptBefore,
                LevelSettingsUnchanged = lsUnchanged,
                LocationSceneUnchanged = locUnchanged,
                EnvironmentManagerUnchanged = envUnchanged,
            };
            _harvested[donorName] = rig;
            _pendingDonors.Remove(donorName);
            _failedAttempts.Remove(donorName);

            EnsureOnRenderObjectManager(); // donor-independent; early attempt — see its own comment on the CameraManager.Exist gate; retried again from OnSceneLoaded

            Plugin.Log.LogInfo($"[ArenaMapPort][WeatherRig] harvested '{donorName}' ({rig.Kind}) — " +
                $"{keptBefore} MonoBehaviour(s) on the clone; post-unload identity unchanged: " +
                $"LevelSettings={lsUnchanged} LocationScene={locUnchanged} EnvironmentManager={envUnchanged}; " +
                $"donor camera captured={donorCameraPrefab != null} " +
                $"(name='{(donorCameraPrefab != null ? donorCameraPrefab.name : "n/a")}') " +
                $"prism={donorPrismPreset != null} postfx={donorPostProcessProfile != null} " +
                "(donor-settings-camera-weather §B.1)");
        }

        static void Fail(string donorName, string reason)
        {
            int n = _failedAttempts.TryGetValue(donorName, out var c) ? c + 1 : 1;
            _failedAttempts[donorName] = n;
            Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] '{donorName}' harvest attempt {n}/{MaxHarvestAttempts} " +
                $"failed: {reason}");
            if (n >= MaxHarvestAttempts)
            {
                _pendingDonors.Remove(donorName);
                _permanentlyFailed.Add(donorName);
                Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] giving up on '{donorName}' after {n} failed " +
                    "attempts this session — every map configured with this Donor falls back to the existing " +
                    "ArenaLightingRepairs workaround for the rest of this session.");
            }
        }

        // RenderSettings fields LevelSettings.ApplySettings writes (LevelSettings.cs:194-218).
        //
        // donor-settings-camera-weather §A (critic amendment 3, 2026-09-17): ApplySettings also pushes
        // four Shader.SetGlobal* values that are true PROCESS-WIDE globals, not scoped to RenderSettings
        // at all (LevelSettings.cs:213-217: "_DirectionLightShadow"/"_WaterLevel"/"_MinAmbientColor"/
        // "_SSRFactor"). The donor's own LevelSettings.Awake() (which runs once, synchronously, as part
        // of the harvest's additive scene load — LevelSettings.cs:166-169) pushes THESE globals too,
        // using the DONOR's authored values, not ours. Reserve's values happen to numerically coincide
        // with the plain C# class defaults (SSRFactor=1, LyingWater=0, DirectionLightShadow=0,
        // _minSmokeAmbientColor=clear — critic-verified against levelsettings_raw.json), so nothing
        // currently leaks past this harvest in practice — but that harmlessness is a fact about Reserve's
        // specific values, not a structural guarantee. Capturing/restoring them here (alongside the
        // pre-existing RenderSettings.* fields) closes the leak window generically, for whatever future
        // donor's values actually differ.
        struct RenderSettingsSnapshot
        {
            AmbientMode _ambientMode;
            Color _ambientSky, _ambientEquator, _ambientGround, _ambientLight;
            float _ambientIntensity;
            bool _fog;
            Color _fogColor;
            float _fogDensity, _fogStart, _fogEnd;
            FogMode _fogMode;
            Material _skybox;
            float _directionLightShadow; // shader global "_DirectionLightShadow" (LevelSettings.cs:213)
            float _lyingWater;           // shader global "_WaterLevel" (LevelSettings.cs:214)
            Color _minAmbientColor;      // shader global "_MinAmbientColor" (LevelSettings.cs:215)
            float _ssrFactor;            // shader global "_SSRFactor" (LevelSettings.cs:217)

            internal static RenderSettingsSnapshot Capture() => new RenderSettingsSnapshot
            {
                _ambientMode = RenderSettings.ambientMode,
                _ambientSky = RenderSettings.ambientSkyColor,
                _ambientEquator = RenderSettings.ambientEquatorColor,
                _ambientGround = RenderSettings.ambientGroundColor,
                _ambientLight = RenderSettings.ambientLight,
                _ambientIntensity = RenderSettings.ambientIntensity,
                _fog = RenderSettings.fog,
                _fogColor = RenderSettings.fogColor,
                _fogDensity = RenderSettings.fogDensity,
                _fogStart = RenderSettings.fogStartDistance,
                _fogEnd = RenderSettings.fogEndDistance,
                _fogMode = RenderSettings.fogMode,
                _skybox = RenderSettings.skybox,
                _directionLightShadow = Shader.GetGlobalFloat("_DirectionLightShadow"),
                _lyingWater = Shader.GetGlobalFloat("_WaterLevel"),
                _minAmbientColor = Shader.GetGlobalColor("_MinAmbientColor"),
                _ssrFactor = Shader.GetGlobalFloat("_SSRFactor"),
            };

            internal void Restore()
            {
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientSkyColor = _ambientSky;
                RenderSettings.ambientEquatorColor = _ambientEquator;
                RenderSettings.ambientGroundColor = _ambientGround;
                RenderSettings.ambientLight = _ambientLight;
                RenderSettings.ambientIntensity = _ambientIntensity;
                RenderSettings.fog = _fog;
                RenderSettings.fogColor = _fogColor;
                RenderSettings.fogDensity = _fogDensity;
                RenderSettings.fogStartDistance = _fogStart;
                RenderSettings.fogEndDistance = _fogEnd;
                if (_fogMode != 0)
                {
                    RenderSettings.fogMode = _fogMode;
                }
                RenderSettings.skybox = _skybox;
                Shader.SetGlobalFloat("_DirectionLightShadow", _directionLightShadow);
                Shader.SetGlobalFloat("_WaterLevel", _lyingWater);
                Shader.SetGlobalColor("_MinAmbientColor", _minAmbientColor);
                Shader.SetGlobalFloat("_SSRFactor", _ssrFactor);
            }
        }

        static List<Scene> LoadedScenesNamed(string sceneName)
        {
            var result = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name == sceneName)
                {
                    result.Add(scene);
                }
            }
            return result;
        }

        static void SafeUnload(Scene scene)
        {
            try
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] cleanup unload also failed: " +
                    $"{e.GetType().Name}: {e.Message}");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // OnRenderObjectManager provision. Lives under the donor's separate "_TechnicalManagers" root
        // (not under "Weather", so it is never part of the clone), its Start() subscribes to
        // OnRenderObjectConnector's static Add/Remove events and does one FindObjectsOfType scan — order
        // relative to AmbientLight.Start() does not matter (self-healing), so AddComponent, not clone.
        //
        // Review fix-ups #2: vanilla's own OnRenderObjectManager.OnRenderObject() dereferences
        // `CameraManager.Instance.Camera`/`...OpticCameraManager.Camera` with NO `CameraManager.Exist`
        // guard (unlike AmbientLight/AudioSourceContainer/TODSkySimple/SoundSuspensionController/
        // AudioUtils/GamePlayerOwner, every one of which gates on `CameraManager.Exist` first —
        // freshly re-verified against the decompile). `CameraManager.Instance` itself can never be null
        // (`instance ?? (instance = new CameraManager())` — it auto-vivifies), so that specific NRE this
        // file's own progress doc originally worried about cannot happen; the real risk is the OPPOSITE
        // problem: OnRenderObject() fires on this component every rendered frame once added, and a
        // freshly auto-vivified "cold" CameraManager's `OpticCameraManager` (a get-only auto-property with
        // no discoverable constructor in the decompile) is null, so
        // `instance.OpticCameraManager.Camera` would NRE continuously, not intermittently, from the
        // moment this component is added if that happens before any real camera/raid setup has ever run
        // `CameraManager.Instance` for real (which is exactly when this used to be called — right after
        // the very first harvest, at the main menu, before any raid). Gated on `CameraManager.Exist` here
        // (the idiomatic vanilla pattern) and retried from <see cref="OnSceneLoaded"/> on every raid
        // activation, since a live raid should always have a real, non-"cold" CameraManager by then.
        // ---------------------------------------------------------------------------------------------
        static void EnsureOnRenderObjectManager()
        {
            if (UnityEngine.Object.FindObjectOfType<OnRenderObjectManager>() != null)
            {
                return; // already present (defensive — no donor's clone includes _TechnicalManagers by design)
            }
            if (!CameraManager.Exist)
            {
                return; // not yet safe — see this method's own comment; retried on the next call site
            }
            _host.AddComponent<OnRenderObjectManager>();
            Plugin.Log.LogInfo("[ArenaMapPort][WeatherRig] added OnRenderObjectManager (lives outside any " +
                "donor's Weather subtree — _TechnicalManagers root).");
        }

        // ---------------------------------------------------------------------------------------------
        // Per-raid activation / deactivation. Reuses this plugin's own Scripts-scene sceneLoaded/
        // sceneUnloaded pair (the same pattern ArenaLightingRepairs/ShaderRebind/LightingDiagnosticProbe
        // already use) rather than a new Harmony prefix on BaseLocalGame<TPlayerOwner>.Create<T>.
        //
        // Ordering argument: Unity dispatches sceneLoaded to every subscriber SYNCHRONOUSLY as part of
        // reporting a scene load's completion, and raid setup's own BaseLocalGame<TPlayerOwner>.Create<T>
        // (which calls InitWeather) is not itself a scene-load callback — it runs only once whatever
        // raid-setup code awaits the scene-load pipeline's own completion, which by construction happens
        // strictly AFTER this handler returns. rig.Root.SetActive(true) below is synchronous: every
        // Awake/OnEnable/Start on the clone (WeatherController, TOD_Sky, AmbientLight, CloudController,
        // WindController, RainController) runs inline, inside this call, before this handler returns — so
        // WeatherController.Instance/MonoBehaviourSingleton&lt;TODSkySimple&gt;.Instance are guaranteed
        // non-null by the time InitWeather's own gate (`if (WeatherController.Instance != null || ...)`)
        // can possibly run. This is the SAME guarantee every other sceneLoaded-based repair in this
        // codebase already depends on for its own, earlier-in-the-pipeline purposes — reusing it here is
        // the minimal-risk choice over a new Harmony prefix. LightingDiagnosticProbe.ProbeWeatherRig's
        // TODSkyProvider.CurrentTime.GameDateTime-set line is the tripwire that would catch this reasoning
        // being wrong in the Stage-0 playtest; a Harmony prefix on Create&lt;T&gt; is the documented (not
        // implemented) fallback if it ever fires.
        //
        // Also install-order-dependent: Plugin.cs calls WeatherRig.Install() BEFORE
        // ArenaLightingRepairs.Install() — both subscribe to the same static sceneLoaded event, and C#
        // multicast delegates invoke subscribers in subscription order, so ArenaLightingRepairs.
        // OnSceneLoaded must read IsActiveForCurrentMap() AFTER this handler has already run on the same
        // dispatch, not before.
        // ---------------------------------------------------------------------------------------------
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name); // stateless — see ArenaMapRaid's own doc comment
            if (map == null || scene.name != map.ScriptsSceneName)
            {
                return; // only the Scripts-scene load matters here
            }

            _activeMap = null; // pessimistic default — flips true below only on a clean activation
            var cfg = _configs[map.Key];
            string donorName = cfg.Donor.Value;
            if (donorName == OffDonor)
            {
                return; // today's behavior, unchanged
            }

            if (!_harvested.TryGetValue(donorName, out var rig))
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] {map.Key}: Donor='{donorName}' configured " +
                    "but not harvested in time for this raid (still pending, or permanently failed this " +
                    "session) — falling back to the ArenaLightingRepairs workaround for this raid.");
                return;
            }

            try
            {
                bool firstActivation = !rig.EverActivated;
                rig.Root.SetActive(true); // synchronous — see this method's own doc comment for the ordering argument

                // Review fix-ups #1: Unity's message dispatch try-catches each MonoBehaviour's own Awake/
                // OnEnable/Start internally and logs to console — it never lets that exception reach us
                // here, so the surrounding try/catch alone cannot detect a failed child Awake. This is the
                // actual detector, and only meaningful the first time this rig is ever activated (Awake
                // never runs again on an already-woken clone) — it re-reads a couple of the fields Awake()
                // is responsible for setting and throws a real, catchable exception if they look wrong.
                if (firstActivation)
                {
                    VerifyFirstActivation(rig);
                }

                // Review fix-ups #4: OnSceneUnloaded's own deactivation below deliberately releases the
                // rig's singletons/event subscription so a stale WeatherController/TOD_Sky can't leak into
                // every OTHER map's raids for the rest of the session — re-establish them here on every
                // activation. Effectively a no-op the very first time (Awake already made the same writes;
                // the -=/+= pair inside guards against ending up double-subscribed), load-bearing from the
                // 2nd activation onward, since Awake does not run again to redo it on its own.
                ReestablishSingletons(rig);
                rig.EverActivated = true;

                if (rig.Kind == RigKind.Full)
                {
                    Shader.EnableKeyword("_WEATHER_DEFINED"); // idempotent; covers a prior deactivate
                }
                _activeMap = map;
                _activeRig = rig; // Review fix-ups #5 — OnSceneUnloaded deactivates THIS, not a re-derived lookup
                EnsureOnRenderObjectManager(); // Review fix-ups #2 — retried here in case CameraManager.Exist was still false at harvest time
                Plugin.Log.LogInfo($"[ArenaMapPort][WeatherRig] {map.Key}: activated '{donorName}' " +
                    $"({rig.Kind}) for this raid. SkyboxMode={cfg.SkyboxModeEntry.Value}.");
                ApplySkyboxMode(rig, cfg.SkyboxModeEntry.Value);

                // donor-settings-camera-weather §A/§B — AFTER the rig itself is fully activated (so
                // IsActiveForCurrentMap() already reads true for anything these two log/branch on) and
                // BEFORE this handler returns, per this method's own ordering argument: PlayerCameraController
                // .Construct/CameraManager.SetCameraFromSettings and TOD_Scattering's per-frame LevelSettings
                // read both happen strictly later in the same startup sequence.
                ApplyDonorLevelSettingsOverrides(map, cfg);
                RainCoverRepairs.ApplyForCurrentMap(map, cfg.RainCoverEntry.Value, cfg.RainBoundsCenter, cfg.RainBoundsSize, cfg.CoverMinHeightEntry); // §12 goal 3 / §13 goal 2
                ApplyDonorCameraPrefab(map, cfg, rig);
                if (rig.Kind == RigKind.Full)
                {
                    // §C.4 — always writes now (Review fix-ups (docs/progress/donor-settings-camera-weather.md)
                    // #1): restores rig.OriginalPuddleWaterLevelY when MaxLyingWaterOverrideEnabled=false,
                    // not just a no-op, since this same RainController persists across every raid this
                    // session (see HarvestedRig.OriginalPuddleWaterLevelY's own doc comment).
                    RainWetnessRepairs.ApplyPuddleClamp(map, rig.Controller, rig.OriginalPuddleWaterLevelY);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] {map.Key}: activation failed — " +
                    $"{e.GetType().Name}: {e.Message}. Falling back to the ArenaLightingRepairs workaround " +
                    "for this raid.");
                try { rig.Root.SetActive(false); } catch { /* best-effort */ }
                // donor-settings-camera-weather §B.2 — never leave a half-assigned LevelSettings.CameraPrefab
                // behind on ANY failure in the block above (not just a camera-specific one): reset it back to
                // AuthorScriptsScene.cs's own authored null so CameraManager falls straight through to Cam2.
                try
                {
                    if (Singleton<LevelSettings>.Instantiated)
                    {
                        var ls = Singleton<LevelSettings>.Instance;
                        ls.CameraPrefab = null;
                        ls.PrismPresetPrefab = null;
                        ls.PostProcessProfilePrefab = null;
                    }
                }
                catch { /* best-effort */ }
                _activeMap = null;
                _activeRig = null;
            }
        }

        // §12 goal 1 — the same 4 shader globals LevelSettings.ApplySettings() unconditionally re-asserts
        // (LevelSettings.cs:213-217), bracketed around ApplyDonorLevelSettingsOverrides' own one-shot
        // ApplySettings() call below so a live LightingLab Candidate C/D override can't be silently
        // clobbered — the same narrow bracket LightingLab.ApplyAmbient's own review fix-up #1 already uses
        // around its own ApplySettings() call (LightingLab.cs). Deliberately NOT the full
        // RenderSettingsSnapshot struct above: that also resets RenderSettings.ambient* back to their
        // PRE-write values on Restore(), which would silently undo the ambient write this method makes
        // below — RenderSettingsSnapshot is reserved for HarvestOne's own one-time donor-scene bracket
        // (see its own doc comment). Defined locally here rather than shared across files, matching this
        // codebase's existing per-file Shader.PropertyToID caching convention.
        static readonly int _ssrFactorId = Shader.PropertyToID("_SSRFactor");
        static readonly int _waterLevelId = Shader.PropertyToID("_WaterLevel");
        static readonly int _directionLightShadowId = Shader.PropertyToID("_DirectionLightShadow");
        static readonly int _minAmbientColorId = Shader.PropertyToID("_MinAmbientColor");

        // donor-settings-camera-weather §A.2 — same hook WeatherRig already uses, run AFTER the rig
        // activation block above succeeds. A no-rig map never reaches here (OnSceneLoaded already
        // returned earlier for Donor=="Off"), so Equator's own fresh-every-scene-load LevelSettings
        // (AuthorScriptsScene.cs) is never touched by this method.
        static void ApplyDonorLevelSettingsOverrides(ArenaMapDescriptor map, RigConfigBundle cfg)
        {
            var ls = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
            if (ls == null)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] {map.Key}: LevelSettings singleton not found " +
                    "at donor-override apply time — skipped (should not happen; AuthorScriptsScene.cs authors " +
                    "one in every owned scene).");
                return;
            }

            float oldZeroLevel = ls.ZeroLevel;
            float oldHeightFalloff = ls.HeightFalloff;
            Vector4 oldBudget = ls.MaxMapTextureMemory;
            float oldNorth = ls._northDirection;
            Color oldSky = ls.SkyColor;
            Color oldEquator = ls.EquatorColor;
            Color oldGround = ls.GroundColor;
            float oldAmbientIntensity = ls.AmbientIntensity;

            ls.ZeroLevel = cfg.ZeroLevelOverride.Value;
            ls.HeightFalloff = cfg.HeightFalloffOverride.Value;
            ls.MaxMapTextureMemory = TierToVector4(cfg.TextureBudgetTierEntry.Value);
            ls.RainBounds = new Bounds(cfg.RainBoundsCenter.Value, cfg.RainBoundsSize.Value);
            ls._northDirection = cfg.NorthDirectionOverride.Value;

            // §12 goal 1 — the real ambient fix. Unity's own implicit default environment
            // reflection/ambient (no ReflectionProbe exists anywhere in these scenes) is derived from
            // RenderSettings.ambient*, which LevelSettings.OnPreCullCallback (BSG code) re-pushes every
            // camera pre-cull FROM these exact fields — writing RenderSettings directly would not stick
            // past the next pre-cull. Donor = Reserve_Base_Scripts' own authored values, confirmed by the
            // 2026-09-18 14:52 playtest (LightingLab candidate A=DonorBlack) to remove the outdoor white
            // ground veil. Stub leaves AuthorScriptsScene.cs's freshly-authored stub untouched.
            if (cfg.UnityAmbientEntry.Value == AmbientSource.Donor)
            {
                GetDonorAmbient(out var sky, out var equatorGround, out var intensity);
                ls.SkyColor = sky;
                ls.EquatorColor = equatorGround;
                ls.GroundColor = equatorGround;
                ls.AmbientIntensity = intensity;
            }

            // §12 goal 1 (spec §0(a)) — capture the 4 shader globals immediately before ApplySettings()
            // clobbers them from LevelSettings' own fields (see the cached-ID fields' own doc comment
            // above), restored right after.
            float ssrBefore = Shader.GetGlobalFloat(_ssrFactorId);
            float waterBefore = Shader.GetGlobalFloat(_waterLevelId);
            float shadowBefore = Shader.GetGlobalFloat(_directionLightShadowId);
            Color minAmbientBefore = Shader.GetGlobalColor(_minAmbientColorId);

            // ApplySettings() is idempotent (LevelSettings.cs:194-218) and pushes MaxMapTextureMemory's
            // streaming-budget refresh plus the four Shader.SetGlobal* fields (§A "no action" list) with
            // our just-written values — those are one-shot writes (only made from Awake()/
            // OnPostLoadingScene(), never re-applied per-frame), so without this call TextureBudgetTier
            // would silently have no effect this raid. Any RenderSettings ambient/fog values it also
            // re-pushes are immediately superseded by ArenaLightingRepairs' own gated write, which runs
            // right after this handler in the same sceneLoaded dispatch (WeatherRig installs before
            // ArenaLightingRepairs — see Plugin.cs) — EXCEPT for SkyColor/EquatorColor/GroundColor/
            // AmbientIntensity, which ArenaLightingRepairs' own ApplyAmbient never touches while a rig is
            // active (ownAmbientColor = !WeatherRig.IsActiveForCurrentMap(), ArenaLightingRepairs.cs) — so
            // the ambient write above is the one that actually sticks.
            ls.ApplySettings();

            Shader.SetGlobalFloat(_ssrFactorId, ssrBefore);
            Shader.SetGlobalFloat(_waterLevelId, waterBefore);
            Shader.SetGlobalFloat(_directionLightShadowId, shadowBefore);
            Shader.SetGlobalColor(_minAmbientColorId, minAmbientBefore);

            // §12 goal 1 — recompute Unity's implicit environment reflection/light-probe data from the new
            // RenderSettings.ambient* state (same mechanism LightingLab candidate A already uses).
            DynamicGI.UpdateEnvironment();

            Plugin.Log.LogInfo($"[ArenaMapPort][LevelSettingsOverride] map={map.Key} " +
                $"ZeroLevel={ls.ZeroLevel:0.00} (was {oldZeroLevel:0.00}) " +
                $"HeightFalloff={ls.HeightFalloff:0.000} (was {oldHeightFalloff:0.000}) " +
                $"TextureBudget={cfg.TextureBudgetTierEntry.Value} (was {oldBudget}, now {ls.MaxMapTextureMemory}) " +
                $"RainBounds=center({ls.RainBounds.center}) size({ls.RainBounds.size}) " +
                $"NorthDirection={(Mathf.Approximately(ls._northDirection, oldNorth) ? "unchanged" : ls._northDirection.ToString("0.0"))} " +
                $"UnityAmbient={cfg.UnityAmbientEntry.Value} " +
                $"SkyColor={ls.SkyColor} (was {oldSky}) EquatorColor={ls.EquatorColor} (was {oldEquator}) " +
                $"GroundColor={ls.GroundColor} (was {oldGround}) " +
                $"AmbientIntensity={ls.AmbientIntensity:0.00} (was {oldAmbientIntensity:0.00})");
        }

        // donor-settings-camera-weather §B.2 (critic amendment — see HarvestedRig's own doc comment for
        // why CameraPrefab/PrismPreset/PostProcessProfile are plain captured references, never cloned).
        static void ApplyDonorCameraPrefab(ArenaMapDescriptor map, RigConfigBundle cfg, HarvestedRig rig)
        {
            if (!cfg.UseDonorCameraPrefab.Value)
            {
                return; // §B.3 — LevelSettings.CameraPrefab stays AuthorScriptsScene.cs's authored null; Cam2 fallback, unchanged
            }
            if (rig.CameraPrefab == null)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][CameraPrefabTakeover] {map.Key}: UseDonorCameraPrefab=true but " +
                    "the harvested rig has no donor CameraPrefab (donor's own LevelSettings.CameraPrefab was null) " +
                    "— falling back to Cam2 for this raid.");
                return;
            }

            var ls = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
            if (ls == null)
            {
                return; // ApplyDonorLevelSettingsOverrides already warned about this above
            }

            ls.CameraPrefab = rig.CameraPrefab;
            ls.PrismPresetPrefab = rig.PrismPreset;             // may be null — SetCameraFromPrefab's own null-check covers it
            ls.PostProcessProfilePrefab = rig.PostProcessProfile; // may be null
            Plugin.Log.LogInfo($"[ArenaMapPort][CameraPrefabTakeover] assigned donor camera for map={map.Key} " +
                $"camera='{rig.CameraPrefab.name}' prism={rig.PrismPreset != null} postfx={rig.PostProcessProfile != null}");
        }

        // Review fix-ups #1 — see OnSceneLoaded's own comment for why this, and not the try/catch around
        // SetActive(true), is what actually catches a failed child Awake. Only called on the first-ever
        // activation of a given rig; on later activations Awake does not run again, so these fields would
        // legitimately read back whatever ReestablishSingletons/OnSceneUnloaded last left them as, not
        // Awake's own output — checking them again then would be meaningless.
        static void VerifyFirstActivation(HarvestedRig rig)
        {
            if (rig.Kind == RigKind.Full)
            {
                if (WeatherController.Instance != rig.Controller)
                {
                    throw new InvalidOperationException("post-activation check failed: " +
                        "WeatherController.Instance is not this rig's own controller after SetActive(true) " +
                        "— WeatherController.Awake() likely threw (Unity swallows the exception itself; " +
                        "nothing else sets this field)");
                }
                if (!rig.Sky.Initialized)
                {
                    throw new InvalidOperationException("post-activation check failed: TOD_Sky.Initialized " +
                        "is still false after SetActive(true) — TOD_Sky.Awake()/Initialize() likely threw " +
                        "before reaching the line that sets it");
                }
            }
            else if (!Singleton<TODSkySimple>.Instantiated || Singleton<TODSkySimple>.Instance != rig.SimpleSky)
            {
                throw new InvalidOperationException("post-activation check failed: " +
                    "Singleton<TODSkySimple>.Instance is not this rig's own sky after SetActive(true) — " +
                    "TODSkySimple.Awake() likely threw");
            }
        }

        // Review fix-ups #4 — undoes OnSceneUnloaded's own explicit teardown (below). MonoBehaviourSingleton
        // <T>.OnDestroy()/WeatherController.OnDestroy() would normally do this, but neither ever runs here:
        // the clone is SetActive(false)'d for reuse, never Destroy()'d.
        static void ReestablishSingletons(HarvestedRig rig)
        {
            if (rig.Kind == RigKind.Full)
            {
                // TOD_Sky aims the sun light relative to the dome's WORLD position (TOD_Sky.cs:1097/1103:
                // LookRotation(DomeTransform.position - <unit sun vector>)), so the dome must sit at the
                // world origin exactly as on vanilla maps (Reserve: (0,0,0), scale 1000).
                var dome = rig.Sky != null && rig.Sky.Components != null ? rig.Sky.Components.DomeTransform : null;
                if (dome != null)
                {
                    dome.position = Vector3.zero;
                    dome.localScale = Vector3.one * 1000f;
                }
                Singleton<TOD_Sky>.Create(rig.Sky);
                WeatherController.Instance = rig.Controller;
                if (CameraManager.Exist)
                {
                    CameraManager.Instance.OnCameraChanged -= rig.Controller.OnCameraChanged; // guard against double-subscribe
                    CameraManager.Instance.OnCameraChanged += rig.Controller.OnCameraChanged;
                }
            }
            else
            {
                Singleton<TODSkySimple>.Create(rig.SimpleSky);
                if (CameraManager.Exist)
                {
                    CameraManager.Instance.OnCameraChanged -= rig.SimpleSky.OnCameraCreated;
                    CameraManager.Instance.OnCameraChanged += rig.SimpleSky.OnCameraCreated;
                }
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null)
            {
                return;
            }

            // Review fix-ups #5: deactivate the rig THIS raid actually activated (_activeRig), not one
            // re-derived from the live Donor ConfigEntry — cfg.Donor.Value can name a different (or
            // not-yet-harvested) donor than the one OnSceneLoaded actually looked up, if it was edited
            // (e.g. via BepInEx's F12 ConfigurationManager) while this raid was in progress; nothing in
            // this file enforces the "next raid only" doc comment on that ConfigEntry, only asks for it.
            if (_activeMap == map && _activeRig != null)
            {
                var rig = _activeRig;
                try
                {
                    // Review fix-ups #4: release everything SetActive(false) below does NOT itself undo, so
                    // a stale WeatherController/TOD_Sky doesn't keep silently feeding every subsequent raid
                    // on every OTHER map for the rest of the session. Concretely:
                    //  - MonoBehaviourSingleton<TOD_Sky>.Instance (Comfort.Common.Singleton<T>.Create is an
                    //    unconditional overwrite with no matching auto-release outside OnDestroy) — every
                    //    ported map's fresh "Cam2" FPS camera picks this up via
                    //    TOD_ImageEffect.Awake()'s own `tod_Sky_0 ??= MonoBehaviourSingleton<TOD_Sky>.Instance`
                    //    AND WeatherController.OnCameraChanged (see next bullet) explicitly re-assigns it.
                    //  - EFT.Weather.WeatherController.Instance — a plain static field, set only in Awake(),
                    //    never cleared by SetActive(false); left stale it also permanently fails
                    //    PreflightClear()'s own `WeatherController.Instance == null` check for the rest of
                    //    the session, blocking any further donor harvest.
                    //  - CameraManager.Instance.OnCameraChanged -= .../SimpleSky's OnCameraCreated — Awake()
                    //    subscribed once and it is never unsubscribed by SetActive(false); left subscribed,
                    //    the still-live delegate fires on every future raid's fresh Cam2 (CameraManager fires
                    //    OnCameraChanged on every raid, not just the first) and re-assigns the stale Sky.
                    // ReestablishSingletons (OnSceneLoaded) puts all of this back on the next activation.
                    if (rig.Kind == RigKind.Full)
                    {
                        Shader.DisableKeyword("_WEATHER_DEFINED");
                        if (CameraManager.Exist)
                        {
                            CameraManager.Instance.OnCameraChanged -= rig.Controller.OnCameraChanged;
                        }
                        if (Singleton<TOD_Sky>.Instantiated && Singleton<TOD_Sky>.Instance == rig.Sky)
                        {
                            Singleton<TOD_Sky>.Release(rig.Sky);
                        }
                        if (WeatherController.Instance == rig.Controller)
                        {
                            WeatherController.Instance = null;
                        }
                    }
                    else
                    {
                        if (CameraManager.Exist)
                        {
                            CameraManager.Instance.OnCameraChanged -= rig.SimpleSky.OnCameraCreated;
                        }
                        if (Singleton<TODSkySimple>.Instantiated && Singleton<TODSkySimple>.Instance == rig.SimpleSky)
                        {
                            Singleton<TODSkySimple>.Release(rig.SimpleSky);
                        }
                    }
                    rig.Root.SetActive(false); // NOT Destroy — reused next raid
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] {map.Key}: deactivation threw (non-fatal, " +
                        $"raid already ending): {e.GetType().Name}: {e.Message}");
                }
            }
            _activeMap = null;
            _activeRig = null;
        }

        // Public accessor ArenaLightingRepairs' gating reads. Reflects only a SUCCESSFUL activation for
        // THIS raid — a configured-but-failed-to-activate map naturally returns false here, which is
        // exactly the "fall back to the existing workaround" behavior required.
        internal static bool IsActiveForCurrentMap() => _activeMap != null && _activeMap == ArenaMapRaid.CurrentMap;

        internal static bool KeepFillLightForCurrentMap() =>
            _activeMap != null && _configs.TryGetValue(_activeMap.Key, out var c) && c.KeepFillLight.Value;

        // §12 goal 3 — mirrors KeepFillLightForCurrentMap's own accessor pattern.
        internal static RainCoverMode RainCoverForCurrentMap() =>
            _activeMap != null && _configs.TryGetValue(_activeMap.Key, out var c) ? c.RainCoverEntry.Value : RainCoverMode.Off;

        // SetActive(true)/SetActive(false) are never called mid-raid in response to a config
        // SettingChanged — matches "changes apply on the next raid" and avoids any re-entrancy question
        // about toggling a live WeatherController/TOD_Sky while LateUpdate is running.
        //
        // Cross-raid reuse needs no extra glue for CloudController/AmbientLight/TOD_Camera (all
        // self-symmetric on OnEnable/OnDisable). Review fix-ups #4 (superseding this comment's earlier,
        // now-incorrect claim that the CameraManager.OnCameraChanged subscription is "deliberately left
        // subscribed forever"): SetActive(false) alone does NOT unsubscribe it or release the
        // WeatherController.Instance/Singleton&lt;TOD_Sky&gt;/Singleton&lt;TODSkySimple&gt; statics —
        // OnSceneUnloaded now does this explicitly (see its own comment), and OnSceneLoaded's
        // ReestablishSingletons puts it all back (including a fresh, exactly-once
        // CameraManager.OnCameraChanged subscription) on the next activation, so cross-raid reuse no
        // longer depends on the still-subscribed delegate happening to fire correctly on its own.

        // ---------------------------------------------------------------------------------------------
        // SkyboxMode — Dome vs FrozenStatic. FrozenStatic adapts ManimalIcebreaker's IcebreakerSky.
        // FreezeSkybox technique (MIT, icebreaker-client/IcebreakerSky.cs — see THIRD-PARTY-NOTICES.md):
        // disable the dome's painter GameObjects, leave everything else (AmbientLight, TOD_Time, wind,
        // rain, the ReflectionProbe) running.
        // ---------------------------------------------------------------------------------------------
        static void ApplySkyboxMode(HarvestedRig rig, SkyboxMode mode)
        {
            if (rig.Kind != RigKind.Full || rig.Sky == null)
            {
                return; // simplified rigs have no dome to freeze
            }
            var c = rig.Sky.Components;
            if (c == null)
            {
                return;
            }

            bool freeze = mode == SkyboxMode.FrozenStatic;
            if (c.Atmosphere != null) c.Atmosphere.SetActive(!freeze);
            if (c.Clear != null) c.Clear.SetActive(!freeze);
            if (c.Space != null) c.Space.SetActive(!freeze);
            if (c.Sun != null) c.Sun.SetActive(!freeze);
            if (c.Moon != null) c.Moon.SetActive(!freeze);
            if (c.CloudRenderer != null)
            {
                c.CloudRenderer.gameObject.SetActive(!freeze);
            }
            // Stage 0 does NOT source a replacement RenderSettings.skybox material (Reserve_Base_Scripts
            // is not known this pass to author its own separate skybox asset; RenderSettings.skybox is
            // already null for every Arena scene today, so FrozenStatic's visual result for Stage 0 is
            // "no dome geometry, camera clears to its own background color" — a deliberately narrower,
            // lower-risk fallback. Sourcing/authoring a flat-gradient skybox material is out of Stage-0
            // scope; only matters if Dome mode's own visual result actually needs it.
        }

        // ---------------------------------------------------------------------------------------------
        // Small accessors LightingDiagnosticProbe needs (ProbeWeatherRig).
        // ---------------------------------------------------------------------------------------------
        internal static string DonorForMap(ArenaMapDescriptor map) =>
            _configs.TryGetValue(map.Key, out var cfg) ? cfg.Donor.Value : OffDonor;

        internal static SkyboxMode SkyboxModeForCurrentMap() =>
            _activeMap != null && _configs.TryGetValue(_activeMap.Key, out var cfg) ? cfg.SkyboxModeEntry.Value : SkyboxMode.Dome;

        internal static HarvestedRig LastHarvestResult(string donorName) =>
            donorName != null && _harvested.TryGetValue(donorName, out var rig) ? rig : null;

        // donor-settings-camera-weather — the rig THIS raid actually activated (null if none), so
        // LightingDiagnosticProbe/DaytimeVisualsProbe can read back CameraPrefab/PrismPreset/
        // PostProcessProfile without re-deriving the donor lookup themselves.
        internal static HarvestedRig ActiveRig => _activeRig;

        internal static bool UseDonorCameraPrefabForCurrentMap() =>
            _activeMap != null && _configs.TryGetValue(_activeMap.Key, out var cfg) && cfg.UseDonorCameraPrefab.Value;
    }
}
