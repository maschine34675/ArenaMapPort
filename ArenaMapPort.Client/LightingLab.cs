using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT.CameraControl;
using EFT.Weather;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// "Lighting Lab" — docs/progress/donor-settings-camera-weather.md, "Lighting Lab" section. Live F12
    /// toggles for the grazing-angle white/veil ground bug the 2026-09-18 06:19/06:23 playtest screenshots
    /// (analysis/screenshots/clear-spiegelung1-2.png, regen-spiegelung1-2.png) confirm: dark matte asphalt
    /// with a genuine mirror puddle from a steep angle, a blown-out white veil with no mirror at all from a
    /// grazing angle, identical between Clear and Rain (including at sun intensity 0) — the signature of a
    /// Fresnel-driven specular/reflection term reading a bright, non-black environment.
    ///
    /// Seven independently-flippable candidates (A-G), each a thin, isolated lever so a tester can flip
    /// ONE at a time (F12) and press F9 to snapshot which one removes the veil:
    ///  A) UnityAmbient — Off/DonorBlack/StubGrey/Custom (§12, docs/progress/
    ///     donor-settings-camera-weather.md §12: renamed from "AmbientPreset"/Stub-default to
    ///     "AmbientOverride"/Off-default once WeatherRig.ApplyDonorLevelSettingsOverrides took over the
    ///     real Donor-ambient fix by default — this candidate now isolates/overrides that fix rather than
    ///     being the only source of it), written into the live LevelSettings fields (never RenderSettings
    ///     directly — LevelSettings.OnPreCullCallback re-applies RenderSettings.ambient* FROM
    ///     LevelSettings every camera pre-cull, confirmed in ArenaLightingRepairs.cs's own doc comment and
    ///     re-verified against the decompile this pass). Isolates Unity's own implicit default
    ///     environment reflection (no ReflectionProbe exists anywhere in these scenes).
    ///  B) GlobalReflection — Rig/Black/Grey/OffStrength, overrides the '_MyGlobalReflectionProbe' shader
    ///     global with a tiny generated Cubemap via a Harmony postfix on AmbientLight.method_2 (wins
    ///     regardless of AmbientLight.RenderDelay timing), or (OffStrength) zeroes
    ///     AmbientLight.ReflectionIntensity directly (its own separate "Custom Ambient" screen-space
    ///     compositing lever — a narrower, second mechanism). Isolates EFT's own custom reflection-probe
    ///     global, structurally separate from Candidate A.
    ///  C) SSRFactor — live override of the '_SSRFactor' shader global (LevelSettings.ApplySettings is
    ///     the ONLY other writer anywhere in the decompile, a one-shot call, never per-frame).
    ///  D) Wetness — WaterLevelOverride (hard override of '_WaterLevel', independent of
    ///     RainWetnessRepairs' own PuddleWaterLevel ceiling clamp) and RainOff (zeroes '_WaterLevel' and
    ///     RainController._wetRenderer.Intensity, wins over WaterLevelOverride). The existing
    ///     WetRendererIntensityMultiplier knob (RainWetnessRepairs.cs, Wetness section) is reused as-is
    ///     for the "SurfaceWetIntensity multiplier" the task asked for — not duplicated here.
    ///  E) GroundSmoothnessLive — live multiplier on every discovered Float/Range shader property whose
    ///     name contains "smooth"/"gloss"/"shin" (Shader.GetPropertyCount/Name/Type — the same technique
    ///     SceneInspector.cs already uses — rather than a hardcoded property-name guess for the compiled,
    ///     source-unavailable 'p0/Reflective/Bumped Specular SMap' shader family), on materials matched by
    ///     shader name ('Custom/Vert Paint Shader Solid' or starting with 'p0/Reflective') AND renderer
    ///     name containing "floor"/"ground"/"asphalt". Scanned once, 5s after scene load (after
    ///     ShaderRebind's own 3s stand-in-shader sweep completes, so material.shader.name reads real
    ///     names, not stand-ins) — sharedMaterials, never .materials (ShaderRebind.cs/SceneInspector.cs's
    ///     own documented reason: bundle materials are already per-map instances; .materials would
    ///     needlessly instantiate a second copy).
    ///  F) SunIntensityMultiplier — scales the rig's own TOD_Sky.Day/Night.LightIntensity fields (a plain
    ///     public field on a plain C# class, TOD_DayParameters/TOD_NightParameters — no Harmony needed;
    ///     TOD_Sky.UpdateCelestials re-reads them on its own throttled schedule).
    ///  G) F9 snapshot hotkey — one log block with every candidate's live value, camera pos/fwd, the live
    ///     shader globals, and a per-face average-color readback of whatever texture is currently bound to
    ///     '_MyGlobalReflectionProbe' (via the smallest generated mip — already a GPU box-filtered average
    ///     of that face — copied into a 1x1 scratch Texture2D with Graphics.CopyTexture; falls back to
    ///     logging the bound texture's type/format/size only if that read-back isn't feasible).
    ///
    /// Every ConfigEntry defaults to today's exact behavior (Stub/Rig/1.0/false/1.0/1.0) — this file
    /// writes NOTHING different from what WeatherRig/ArenaLightingRepairs/RainWetnessRepairs already do
    /// until the tester flips a value live via F12. Gated on <see cref="ArenaMapRaid.IsLoaded"/> (and, for
    /// the two Harmony targets — AmbientLight.method_2 and RainController.AbstractState.vmethod_15 — also
    /// <see cref="WeatherRig.IsActiveForCurrentMap"/> first, since both patched methods run on ANY loaded
    /// AmbientLight/RainController, including a real vanilla raid played earlier in the session, same
    /// reasoning <see cref="RainWetnessRepairs"/>/<see cref="WeatherTestOverride"/> already document).
    /// Bound for every map in <see cref="ArenaMapRegistry.All"/> (matching WeatherRig/ArenaLightingRepairs'
    /// own per-map-bundle convention) — every knob is a structural no-op on a map with no rig (Candidates
    /// B/F need the rig's own AmbientLight/TOD_Sky and silently do nothing without one; A/C/E work on any
    /// map but default to a no-op everywhere).
    ///
    /// Two deliberate adjustments from the implementation spec this file was built from (both noted here
    /// per the task's own "adjust only where the code contradicts it, and say so"):
    ///  1. The spec's own decompile findings call AmbientLight.method_2 "private" while the actual
    ///     decompile (re-verified this pass, AmbientLight.cs:389) shows it `public` — resolves identically
    ///     either way via AccessTools.Method, no code change needed, noted for the record only.
    ///  2. The spec's pseudocode puts the F9 SnapshotHotkey ConfigEntry inside each map's per-map
    ///     LabConfigBundle. Implemented instead as ONE global ConfigEntry (section "LightingLab", no map
    ///     suffix) — matching SceneInspector.cs's own F10 hotkey convention exactly (one key for the whole
    ///     plugin, not N per-map copies of the same binding), since only one map's raid can ever be loaded
    ///     at a time regardless.
    ///  3. "CaptureSunBaseline" (spec's step 3) is folded directly into <see cref="ApplySunIntensity"/>
    ///     (captures lazily on first call this raid) rather than kept as a separate method — same
    ///     information, fewer moving parts.
    /// </summary>
    internal static class LightingLab
    {
        // §12 (docs/progress/donor-settings-camera-weather.md) goal 2 — renamed from
        // {Stub, DonorBlack, Custom} (BepInEx ConfigEntry<T> serializes/deserializes an enum by NAME, so
        // renaming the type is safe on its own, but the project's hard rule is a config KEY never gets
        // reused with new semantics — the bind below renames "AmbientPreset" to "AmbientOverride" too,
        // leaving "AmbientPreset" orphaned in a tester's existing config file). Off replaces Stub as the
        // true no-op default: WeatherRig.ApplyDonorLevelSettingsOverrides now owns the real ambient fix
        // (goal 1, defaults to Donor for a rig-active map) — Off here means "don't override what that
        // already wrote", not "force the stub". StubGrey is new: forces the PRE-goal-1 stub even while
        // WeatherRig's own default is active, for A/B isolation.
        internal enum AmbientPreset { Off, DonorBlack, StubGrey, Custom }
        internal enum ReflectionPreset { Rig, Black, Grey, OffStrength }

        const float GroundScanDelaySeconds = 5f; // after ShaderRebind's own 3s stand-in-shader sweep

        // §12 goal 2 — Reserve_Base_Scripts' own authored LevelSettings ambient values now live in
        // WeatherRig.GetDonorAmbient (WeatherRig.cs) — single source of truth, "do not duplicate
        // constants". This file no longer keeps its own copy.

        sealed class LabConfigBundle
        {
            // A — UnityAmbient
            internal ConfigEntry<AmbientPreset> AmbientPresetEntry;
            internal ConfigEntry<Color> AmbientCustomColor;
            internal ConfigEntry<float> AmbientCustomIntensity;

            // B — GlobalReflection
            internal ConfigEntry<ReflectionPreset> ReflectionPresetEntry;

            // C — SSRFactor
            internal ConfigEntry<float> SSRFactorOverride;

            // D — Wetness (new keys; WetRendererIntensityMultiplier/MaxLyingWaterOverride* stay in
            // RainWetnessRepairs.cs, reused not duplicated — see class doc comment)
            internal ConfigEntry<bool> WaterLevelOverrideEnabled;
            internal ConfigEntry<float> WaterLevelOverrideValue;
            internal ConfigEntry<bool> RainOff;

            // E — GroundSmoothnessLive
            internal ConfigEntry<float> GroundSmoothnessMultiplier;

            // F — SunIntensityMultiplier
            internal ConfigEntry<float> SunIntensityMultiplier;
        }

        sealed class GroundMaterialEntry
        {
            internal Material Material;
            internal List<(int propId, float original)> Props;
        }

        // Per-map captured baselines + material cache. A new instance per map, created once at Install()
        // and reused for every raid this session (fields reset by OnSceneUnloaded, not re-allocated) —
        // same convention ArenaLightingRepairs.MapState already uses.
        sealed class LabState
        {
            // A
            internal bool AmbientBaselineCaptured;
            internal bool AmbientOverrideApplied; // true once a non-Stub preset has actually been written this raid
            internal Color BaseSkyColor, BaseEquatorColor, BaseGroundColor;
            internal float BaseAmbientIntensity;

            // B
            internal bool ReflectionIntensityCaptured;
            internal float BaseReflectionIntensity;
            internal AmbientLight CachedAmbient; // the rig's own AmbientLight, cached so OnSceneUnloaded can
                                                  // restore it even after WeatherRig's own OnSceneUnloaded
                                                  // (which runs first — see Install-order doc comment) has
                                                  // already cleared WeatherRig.ActiveRig for this raid.
            internal Cubemap BlackCube, GreyCube; // tiny generated override cubemaps, built lazily, reused

            // C
            internal bool SsrBaselineCaptured;
            internal float BaseSsrFactor = 1f;

            // F
            internal bool SunBaselineCaptured;
            internal float BaseDayLightIntensity, BaseNightLightIntensity;
            internal TOD_Sky CachedSky; // same "cache before WeatherRig clears ActiveRig" reasoning as CachedAmbient

            // E
            internal readonly List<GroundMaterialEntry> GroundMaterials = new List<GroundMaterialEntry>();
        }

        static readonly Dictionary<string, LabConfigBundle> _configs = new Dictionary<string, LabConfigBundle>();
        static readonly Dictionary<string, LabState> _states = new Dictionary<string, LabState>();
        static bool _installed;
        static GameObject _scanHost;

        // The map/config/state THIS raid actually loaded — read by the two per-frame Harmony postfixes
        // instead of a dictionary lookup, same "_activeMap/_activeRig" convention WeatherRig.cs already
        // uses for its own per-frame-adjacent state. Null whenever no ported-map raid with a Lab config
        // applied is currently loaded.
        static ArenaMapDescriptor _activeMap;
        static LabConfigBundle _activeCfg;
        static LabState _activeState;

        static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> _snapshotHotkey;

        static readonly int _myGlobalReflectionProbeId = Shader.PropertyToID("_MyGlobalReflectionProbe");
        static readonly int _ssrFactorId = Shader.PropertyToID("_SSRFactor");
        static readonly int _waterLevelId = Shader.PropertyToID("_WaterLevel");
        static readonly int _surfaceWetIntensityId = Shader.PropertyToID("_SurfaceWetIntensity");
        // Review fix-up (2026-09-18): the other two globals LevelSettings.ApplySettings() also pushes
        // (LevelSettings.cs:213,215) — bracketed around ApplyAmbient's own ApplySettings() call below, same
        // set WeatherRig.RenderSettingsSnapshot already brackets around its own one-shot ApplySettings() call.
        static readonly int _directionLightShadowId = Shader.PropertyToID("_DirectionLightShadow");
        static readonly int _minAmbientColorId = Shader.PropertyToID("_MinAmbientColor");

        static readonly string[] SmoothnessPropertyHints = { "smooth", "gloss", "shin" };

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            foreach (var map in ArenaMapRegistry.All)
            {
                _configs[map.Key] = BindConfig(map);
                _states[map.Key] = new LabState();
            }

            _snapshotHotkey = Plugin.Instance.Config.Bind("LightingLab", "SnapshotHotkey",
                new BepInEx.Configuration.KeyboardShortcut(KeyCode.F9),
                "G — logs one [LightingLab][Snapshot] block: every candidate's live value, camera pos/fwd, " +
                "RenderSettings.ambient*, live _SSRFactor/_WaterLevel/_SurfaceWetIntensity, and a per-face " +
                "average-color readback of whatever texture is bound to _MyGlobalReflectionProbe. Only during " +
                "a ported-map raid (ArenaMapRaid.IsLoaded()).");

            // Plugin.Instance.gameObject is the same always-alive host SceneInspector.cs uses for its own
            // F10 poller — no new persistent GameObject needed just for a key-poll.
            Plugin.Instance.gameObject.AddComponent<HotkeyRunner>();

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        // ---------------------------------------------------------------------------------------------
        // Config binding.
        // ---------------------------------------------------------------------------------------------
        static LabConfigBundle BindConfig(ArenaMapDescriptor map)
        {
            // Deliberately its OWN section, "LightingLab (<key>)" — never "Lighting (<key>)"
            // (WeatherRig/ArenaLightingRepairs' own section) — both for F12-UI discoverability of this
            // experimental Lab as a distinct group, and to make a key-name collision structurally
            // impossible regardless of what either file adds later.
            string section = $"LightingLab ({map.Key})";
            var cfg = new LabConfigBundle();

            // §12 goal 2 — renamed from "AmbientPreset"/Stub-default to "AmbientOverride"/Off-default; see
            // this class's own AmbientPreset enum doc comment and docs/progress/
            // donor-settings-camera-weather.md §12. "AmbientPreset" stays orphaned in a tester's existing
            // config file (BepInEx persists a saved value under its old key regardless).
            cfg.AmbientPresetEntry = Plugin.Instance.Config.Bind(section, "AmbientOverride", AmbientPreset.Off,
                "A — UnityAmbient override, for isolating/overriding WeatherRig's own UnityAmbient fix (§12 " +
                "goal 1). Off = write nothing at load; switching back to Off live re-applies this raid's own " +
                "captured baseline (whatever WeatherRig/ArenaLightingRepairs already wrote before this file " +
                "ran). DonorBlack = Reserve_Base_Scripts' own authored ambient (SkyColor=black, " +
                "EquatorColor/GroundColor=(0.604,0.635,0.651), AmbientIntensity=0 — only SkyColor/" +
                "AmbientIntensity are visually live under AmbientMode.Flat; same values WeatherRig." +
                "GetDonorAmbient supplies, not a separate copy). StubGrey = force the PRE-§12 stub (0.5 " +
                "grey / intensity 1, AuthorScriptsScene.cs's own authored values) even while WeatherRig's " +
                "own UnityAmbient=Donor default is active, for A/B isolation. Custom = AmbientCustomColor/" +
                "AmbientCustomIntensity below. Written into the live LevelSettings fields " +
                "(LevelSettings.OnPreCullCallback re-applies RenderSettings.ambient* FROM these fields every " +
                "camera pre-cull — a direct RenderSettings write would not stick) plus " +
                "DynamicGI.UpdateEnvironment(). Isolates: Unity's own implicit default environment reflection " +
                "(no ReflectionProbe exists anywhere in these scenes) reading a bright ambient at grazing " +
                "Fresnel angles.");

            cfg.AmbientCustomColor = Plugin.Instance.Config.Bind(section, "AmbientCustomColor",
                new Color(0.5f, 0.5f, 0.5f, 1f),
                "A — SkyColor/EquatorColor/GroundColor used when AmbientPreset=Custom.");

            cfg.AmbientCustomIntensity = Plugin.Instance.Config.Bind(section, "AmbientCustomIntensity", 1f,
                new ConfigDescription(
                    "A — LevelSettings.AmbientIntensity used when AmbientPreset=Custom (inert under " +
                    "AmbientMode.Flat, this project's mode — copied for completeness only).",
                    new AcceptableValueRange<float>(0f, 4f)));

            cfg.ReflectionPresetEntry = Plugin.Instance.Config.Bind(section, "GlobalReflection", ReflectionPreset.Rig,
                "B — GlobalReflection. Rig = today's behavior, untouched (AmbientLight's own live sky-only " +
                "cubemap). Black/Grey override the '_MyGlobalReflectionProbe' shader global every frame with a " +
                "tiny generated solid-color Cubemap (Harmony postfix on AmbientLight.method_2 — wins " +
                "regardless of AmbientLight.RenderDelay's own render-interval timing). OffStrength leaves the " +
                "texture untouched but zeroes AmbientLight.ReflectionIntensity (its own separate 'Custom " +
                "Ambient' screen-space compositing lever — a narrower, second mechanism; see " +
                "docs/progress/donor-settings-camera-weather.md). Isolates: EFT's own custom reflection-probe " +
                "global, structurally separate from Candidate A's implicit Unity ambient-derived reflection.");

            cfg.SSRFactorOverride = Plugin.Instance.Config.Bind(section, "SSRFactor", 1f,
                new ConfigDescription(
                    "C — live override of the '_SSRFactor' shader global (LevelSettings.ApplySettings is the " +
                    "ONLY other writer anywhere in the decompile, confirmed by a full-tree grep — a one-shot " +
                    "call, never per-frame). 1.0 = authored no-op. Applied directly via Shader.SetGlobalFloat, " +
                    "no Harmony needed.",
                    new AcceptableValueRange<float>(0f, 1f)));

            cfg.WaterLevelOverrideEnabled = Plugin.Instance.Config.Bind(section, "WaterLevelOverrideEnabled", false,
                "D — hard-overrides the '_WaterLevel' shader global every frame (Harmony postfix on " +
                "RainController.AbstractState.vmethod_15), independent of RainController.PuddleWaterLevel's " +
                "own ceiling clamp (Wetness/MaxLyingWaterOverride*, RainWetnessRepairs.cs — a different lever: " +
                "that one clamps the target the per-frame lerp approaches, this one pins the resulting value " +
                "outright). false = today's behavior, the vanilla per-frame lerp stands. RainOff below wins " +
                "over this when both are set.");

            cfg.WaterLevelOverrideValue = Plugin.Instance.Config.Bind(section, "WaterLevelOverrideValue", 0.6f,
                new ConfigDescription(
                    "D — replacement '_WaterLevel' value applied live when WaterLevelOverrideEnabled=true.",
                    new AcceptableValueRange<float>(0f, 0.95f)));

            cfg.RainOff = Plugin.Instance.Config.Bind(section, "RainOff", false,
                "D — zeroes the rain-driven globals this Lab targets for isolating the ground-veil symptom: " +
                "forces '_WaterLevel'=0 (wins over WaterLevelOverrideEnabled above) and " +
                "RainController._wetRenderer.Intensity=0 (one added check in RainWetnessRepairs' own existing " +
                "postfix). Deliberately does NOT touch rain VFX (splash drops, screen rain, '_RainIntensity') " +
                "— scoped narrowly to ground wetness/reflection, not the whole weather system. false = today's " +
                "behavior.");

            cfg.GroundSmoothnessMultiplier = Plugin.Instance.Config.Bind(section, "GroundSmoothnessMultiplier", 1f,
                new ConfigDescription(
                    "E — live multiplier on every discovered Float/Range shader property whose name contains " +
                    "'smooth'/'gloss'/'shin' (case-insensitive; Shader.GetPropertyCount/Name/Type — same " +
                    "technique SceneInspector.cs's F10 dump already uses), on every material matched once at " +
                    "raid start: shader 'Custom/Vert Paint Shader Solid' or starting with 'p0/Reflective', AND " +
                    "renderer name containing 'floor'/'ground'/'asphalt'. 1.0 = authored no-op. Scanned 5s " +
                    "after scene load (after ShaderRebind's own 3s stand-in-shader sweep); original values " +
                    "cached and restored on raid end.",
                    new AcceptableValueRange<float>(0f, 2f)));

            cfg.SunIntensityMultiplier = Plugin.Instance.Config.Bind(section, "SunIntensityMultiplier", 1f,
                new ConfigDescription(
                    "F — multiplies the rig's own TOD_Sky.Day.LightIntensity/Night.LightIntensity fields " +
                    "(captured once per raid as the baseline, never compounded). TOD_Sky.UpdateCelestials " +
                    "re-reads these on its own throttled schedule and writes the result into the sun Light's " +
                    "intensity — no Harmony needed. 1.0 = authored no-op. Only has an effect while a Full rig " +
                    "(TOD_Sky) is active for this map.",
                    new AcceptableValueRange<float>(0f, 4f)));

            cfg.AmbientPresetEntry.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplyAmbient(m, c, s));
            cfg.AmbientCustomColor.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplyAmbient(m, c, s));
            cfg.AmbientCustomIntensity.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplyAmbient(m, c, s));
            cfg.ReflectionPresetEntry.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplyReflectionPreset(m, c, s));
            cfg.SSRFactorOverride.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplySSRFactor(m, c, s));
            cfg.WaterLevelOverrideEnabled.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplyWetness(m, c));
            cfg.WaterLevelOverrideValue.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplyWetness(m, c));
            cfg.RainOff.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplyWetness(m, c));
            cfg.GroundSmoothnessMultiplier.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ReapplyGroundSmoothness(m, c, s));
            // The per-map base value lives in WeatherRig's "Lighting (<key>)" section (bound before this
            // Install runs, see Plugin.cs order); a live edit of it re-applies through the same path.
            var baseEntry = WeatherRig.GroundSmoothnessBaseEntry(map);
            if (baseEntry != null)
            {
                baseEntry.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ReapplyGroundSmoothness(m, c, s));
            }
            cfg.SunIntensityMultiplier.SettingChanged += (_, __) => ReapplyIfLoaded(map, (m, c, s) => ApplySunIntensity(m, c, s));

            return cfg;
        }

        static void ReapplyIfLoaded(ArenaMapDescriptor map, Action<ArenaMapDescriptor, LabConfigBundle, LabState> apply)
        {
            if (ArenaMapRaid.CurrentMap != map)
            {
                return; // "applies on the next raid" would be wrong here — every other knob in this file is
                        // live via F12, matching the task's own "Lighting Lab" goal; only skip if this map's
                        // raid isn't even the one currently loaded.
            }
            if (!_configs.TryGetValue(map.Key, out var cfg) || !_states.TryGetValue(map.Key, out var state))
            {
                return;
            }
            apply(map, cfg, state);
        }

        // ---------------------------------------------------------------------------------------------
        // Scene load / unload.
        // ---------------------------------------------------------------------------------------------
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null)
            {
                return; // only the Scripts-scene load matters — ground materials in OTHER owned scenes are
                        // still reachable 5s later by ScanGroundMaterials, which walks every owned scene.
            }
            if (!_configs.TryGetValue(map.Key, out var cfg) || !_states.TryGetValue(map.Key, out var state))
            {
                return;
            }

            _activeMap = map;
            _activeCfg = cfg;
            _activeState = state;

            // Order matters: WeatherRig/ArenaLightingRepairs both install BEFORE this file (Plugin.cs) and
            // subscribe to the same SceneManager.sceneLoaded — C# multicast delegates invoke subscribers in
            // subscription order, so by the time this handler runs, this raid's rig (if any) is already
            // activated and ArenaLightingRepairs' own ambient/light pass has already run once. See class
            // doc comment.
            ApplyAmbient(map, cfg, state);
            ApplySSRFactor(map, cfg, state);
            ApplySunIntensity(map, cfg, state);
            ApplyReflectionPreset(map, cfg, state);
            ApplyWetness(map, cfg);
            ScheduleGroundScan(map, cfg, state);
        }

        static void OnSceneUnloaded(Scene scene)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null || !_states.TryGetValue(map.Key, out var state))
            {
                return;
            }

            // C — true process-wide global; nothing else resets it on scene unload (same reasoning
            // ArenaLightingRepairs.OnSceneUnloaded's own doc comment gives for RenderSettings.fog).
            Shader.SetGlobalFloat(_ssrFactorId, 1f);
            state.SsrBaselineCaptured = false;

            // D — Review fix-up (2026-09-18, Low): same process-wide-global reasoning as C above, added for
            // parity — a WaterLevelOverride/RainOff session otherwise leaves '_WaterLevel' at a stale value
            // past raid end with no explicit reset here, unlike C. In practice this is low-impact: unlike
            // SSRFactor (LevelSettings.ApplySettings is its ONLY other writer, a one-shot call), '_WaterLevel'
            // is also written every frame by RainController.AbstractState.vmethod_15 (RainController.cs:
            // 142-147, driven by WeatherController.ControlRain every weather Update — RainController.cs
            // confirms no per-scene-unload reset there either), which recomputes it unconditionally from
            // PuddleWaterLevel/laingWater on the very next frame of ANY raid, vanilla or ported — so a stale
            // value self-heals almost immediately regardless. Reset to 0 (LevelSettings.LyingWater's own
            // compiled field default, same convention as SSRFactor's reset to 1 mirroring LevelSettings.
            // SSRFactor's own default) anyway, for parity with C and so this method's own summary log line
            // below is accurate about what it restored.
            Shader.SetGlobalFloat(_waterLevelId, 0f);

            // F — restore the rig's own TOD_Sky fields via the CACHED reference (WeatherRig.OnSceneUnloaded
            // runs first in this same dispatch and already cleared WeatherRig.ActiveRig by this point — see
            // LabState.CachedSky's own doc comment).
            if (state.SunBaselineCaptured && state.CachedSky != null)
            {
                state.CachedSky.Day.LightIntensity = state.BaseDayLightIntensity;
                state.CachedSky.Night.LightIntensity = state.BaseNightLightIntensity;
            }
            state.SunBaselineCaptured = false;
            state.CachedSky = null;

            // B — restore AmbientLight.ReflectionIntensity if OffStrength was ever applied this raid. The
            // override cubemaps (BlackCube/GreyCube) are deliberately NOT cleared — tiny (4x4, 3 mips)
            // generated textures, cheap to keep for reuse next raid on this map; the Harmony postfix only
            // ever reads them when the live config preset says to.
            if (state.ReflectionIntensityCaptured && state.CachedAmbient != null)
            {
                state.CachedAmbient.SetReflectionIntensity(state.BaseReflectionIntensity);
            }
            state.ReflectionIntensityCaptured = false;
            state.CachedAmbient = null;

            // A — LevelSettings itself is a per-scene component AuthorScriptsScene.cs re-authors fresh
            // every scene load; nothing to restore beyond our own capture flags.
            state.AmbientBaselineCaptured = false;
            state.AmbientOverrideApplied = false;

            // E — restore every scanned ground material's original property values, then drop the cache
            // (next raid's materials are fresh instances from the bundle).
            int restoredCount = state.GroundMaterials.Count;
            for (int i = 0; i < restoredCount; i++)
            {
                var entry = state.GroundMaterials[i];
                if (entry.Material == null)
                {
                    continue; // destroyed/unloaded already
                }
                for (int p = 0; p < entry.Props.Count; p++)
                {
                    entry.Material.SetFloat(entry.Props[p].propId, entry.Props[p].original);
                }
            }
            state.GroundMaterials.Clear();
            if (_scanHost != null)
            {
                UnityEngine.Object.Destroy(_scanHost);
                _scanHost = null;
            }

            Plugin.Log.LogInfo($"[ArenaMapPort][LightingLab] {map.Key}: raid ended — restored SSRFactor=1.00, " +
                "WaterLevel=0.00, sun-intensity baseline (if captured), reflection-intensity baseline (if " +
                $"captured), {restoredCount} ground material(s).");

            if (_activeMap == map)
            {
                _activeMap = null;
                _activeCfg = null;
                _activeState = null;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // A — UnityAmbient.
        // ---------------------------------------------------------------------------------------------
        static void ApplyAmbient(ArenaMapDescriptor map, LabConfigBundle cfg, LabState state)
        {
            var ls = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
            if (ls == null)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][LightingLab] {map.Key}: LevelSettings singleton not " +
                    "found at A-apply time — skipped (should not happen; AuthorScriptsScene.cs authors one " +
                    "in every owned scene).");
                return;
            }

            if (!state.AmbientBaselineCaptured)
            {
                // Captured AFTER WeatherRig/ArenaLightingRepairs have already run this dispatch (see
                // OnSceneLoaded's own ordering comment) — this is "today's behavior" for Stub to restore to,
                // not the raw AuthorScriptsScene.cs authored value.
                state.BaseSkyColor = ls.SkyColor;
                state.BaseEquatorColor = ls.EquatorColor;
                state.BaseGroundColor = ls.GroundColor;
                state.BaseAmbientIntensity = ls.AmbientIntensity;
                state.AmbientBaselineCaptured = true;
            }

            var preset = cfg.AmbientPresetEntry.Value;
            if (preset == AmbientPreset.Off && !state.AmbientOverrideApplied)
            {
                return; // true no-op path — nothing has ever diverged from baseline this raid, nothing to write
            }

            Color sky, equator, ground;
            float intensity;
            switch (preset)
            {
                case AmbientPreset.DonorBlack:
                    // §12 goal 2 — single source of truth (WeatherRig.cs), not a duplicate copy.
                    WeatherRig.GetDonorAmbient(out sky, out var donorEquatorGround, out intensity);
                    equator = ground = donorEquatorGround;
                    break;
                case AmbientPreset.StubGrey:
                    sky = equator = ground = new Color(0.5f, 0.5f, 0.5f, 1f); // AuthorScriptsScene.cs:78-82's own authored stub
                    intensity = 1f;
                    break;
                case AmbientPreset.Custom:
                    sky = equator = ground = cfg.AmbientCustomColor.Value;
                    intensity = cfg.AmbientCustomIntensity.Value;
                    break;
                default: // Off, but AmbientOverrideApplied — restore this raid's own captured baseline
                    sky = state.BaseSkyColor;
                    equator = state.BaseEquatorColor;
                    ground = state.BaseGroundColor;
                    intensity = state.BaseAmbientIntensity;
                    break;
            }

            ls.SkyColor = sky;
            ls.EquatorColor = equator;
            ls.GroundColor = ground;
            ls.AmbientIntensity = intensity;
            // ApplySettings() re-pushes RenderSettings.ambient*/_SSRFactor/etc from these fields
            // (LevelSettings.cs:194-218) — without it our field writes above have no visible effect until
            // the next camera pre-cull happens to already agree. DynamicGI.UpdateEnvironment() (Unity
            // engine API — no BSG decompile citation) recomputes Unity's implicit default environment
            // reflection/light-probe data from the new RenderSettings.ambient* state; this is Candidate A's
            // actual mechanism under test (no ReflectionProbe exists anywhere in these scenes).
            //
            // Review fix-up (2026-09-18, High): ApplySettings() ALSO unconditionally re-asserts four
            // Shader.SetGlobal* process-wide globals from LevelSettings' own fields, not from whatever
            // Candidate C/D most recently wrote live (LevelSettings.cs:213-217: _DirectionLightShadow,
            // _WaterLevel [LevelSettings.LyingWater, default 0], _MinAmbientColor, _SSRFactor [LevelSettings.
            // SSRFactor, default 1]) — silently clobbering a live SSRFactor/WaterLevel override the moment the
            // tester changes AmbientPreset, exactly the "revert to default before trying the next candidate"
            // step §11.6 step 5 of this doc instructs. Bracket the same four globals WeatherRig's own
            // RenderSettingsSnapshot (WeatherRig.cs:746-819) already brackets around its own one-shot
            // ApplySettings() call, so a live Candidate C/D override survives an Ambient preset change.
            float ssrBefore = Shader.GetGlobalFloat(_ssrFactorId);
            float waterBefore = Shader.GetGlobalFloat(_waterLevelId);
            float shadowBefore = Shader.GetGlobalFloat(_directionLightShadowId);
            Color minAmbientBefore = Shader.GetGlobalColor(_minAmbientColorId);

            ls.ApplySettings();

            Shader.SetGlobalFloat(_ssrFactorId, ssrBefore);
            Shader.SetGlobalFloat(_waterLevelId, waterBefore);
            Shader.SetGlobalFloat(_directionLightShadowId, shadowBefore);
            Shader.SetGlobalColor(_minAmbientColorId, minAmbientBefore);

            DynamicGI.UpdateEnvironment();
            state.AmbientOverrideApplied = preset != AmbientPreset.Off;

            Plugin.Log.LogInfo($"[ArenaMapPort][LightingLab] A: applied preset={preset} SkyColor={ls.SkyColor} " +
                $"EquatorColor={ls.EquatorColor} GroundColor={ls.GroundColor} " +
                $"AmbientIntensity={ls.AmbientIntensity:0.00} (map={map.Key})");
        }

        // ---------------------------------------------------------------------------------------------
        // B — GlobalReflection.
        // ---------------------------------------------------------------------------------------------
        static void ApplyReflectionPreset(ArenaMapDescriptor map, LabConfigBundle cfg, LabState state)
        {
            var ambient = WeatherRig.ActiveRig?.Controller?.TimeOfDayController?.AmbientLightScript;
            var preset = cfg.ReflectionPresetEntry.Value;

            if (ambient != null)
            {
                state.CachedAmbient = ambient; // see LabState.CachedAmbient's own doc comment
                if (!state.ReflectionIntensityCaptured)
                {
                    state.BaseReflectionIntensity = ambient.ReflectionIntensity;
                    state.ReflectionIntensityCaptured = true;
                }
                float targetIntensity = preset == ReflectionPreset.OffStrength ? 0f : state.BaseReflectionIntensity;
                if (!Mathf.Approximately(ambient.ReflectionIntensity, targetIntensity))
                {
                    // SetReflectionIntensity also calls method_3() immediately (AmbientLight.cs:237-241) —
                    // not gated by RenderDelay, sticks live with zero Harmony needed.
                    ambient.SetReflectionIntensity(targetIntensity);
                }
            }

            // Lazily build the tiny override cubemaps once per map per session — cheap (4x4, 3 mips,
            // RGBAHalf), reused across every raid on this map thereafter.
            if (preset == ReflectionPreset.Black && state.BlackCube == null)
            {
                state.BlackCube = BuildSolidCubemap(Color.black);
            }
            else if (preset == ReflectionPreset.Grey && state.GreyCube == null)
            {
                state.GreyCube = BuildSolidCubemap(new Color(0.5f, 0.5f, 0.5f, 1f));
            }

            Plugin.Log.LogInfo($"[ArenaMapPort][LightingLab] B: reflection override preset={preset} " +
                $"ambientFound={ambient != null} (map={map.Key})");
        }

        static Cubemap BuildSolidCubemap(Color color)
        {
            const int size = 4; // small enough to be free, large enough for a real mip chain down to 1x1
            var cube = new Cubemap(size, TextureFormat.RGBAHalf, true) { name = $"ArenaMapPort_LightingLab_{color}" };
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            for (int face = 0; face < 6; face++)
            {
                cube.SetPixels(pixels, (CubemapFace)face);
            }
            cube.Apply(updateMipmaps: true);
            return cube;
        }

        // Harmony postfix target — HarmonyPatches.InstallLightingLabReflectionOverride.
        // AmbientLight.method_2() is called every LateUpdate (AmbientLight.cs:293) but only actually
        // writes the '_MyGlobalReflectionProbe' global on the frames its own RenderDelay throttle allows
        // (AmbientLight.cs:391) — this postfix runs on EVERY call regardless (Harmony postfixes run after
        // the whole method executes, including an early return inside it), so our override re-asserts
        // itself every frame and wins regardless of that timing.
        internal static void ReflectionPostfix(AmbientLight __instance)
        {
            // MANDATORY first check — this method runs on ANY loaded AmbientLight, including a real
            // vanilla raid played earlier in the same session, not just ours.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return;
            }
            var cfg = _activeCfg;
            var state = _activeState;
            if (cfg == null || state == null)
            {
                return;
            }

            switch (cfg.ReflectionPresetEntry.Value)
            {
                case ReflectionPreset.Black:
                    if (state.BlackCube != null)
                    {
                        Shader.SetGlobalTexture(_myGlobalReflectionProbeId, state.BlackCube);
                    }
                    break;
                case ReflectionPreset.Grey:
                    if (state.GreyCube != null)
                    {
                        Shader.SetGlobalTexture(_myGlobalReflectionProbeId, state.GreyCube);
                    }
                    break;
                default:
                    break; // Rig / OffStrength — leave AmbientLight's own just-written CubeRT binding alone
            }
        }

        // ---------------------------------------------------------------------------------------------
        // C — SSRFactor.
        // ---------------------------------------------------------------------------------------------
        static void ApplySSRFactor(ArenaMapDescriptor map, LabConfigBundle cfg, LabState state)
        {
            if (!state.SsrBaselineCaptured)
            {
                state.BaseSsrFactor = Shader.GetGlobalFloat(_ssrFactorId);
                state.SsrBaselineCaptured = true;
            }
            float value = cfg.SSRFactorOverride.Value;
            float before = Shader.GetGlobalFloat(_ssrFactorId);
            if (Mathf.Approximately(before, value))
            {
                return; // already at target — steady-state no-op, no log spam
            }
            Shader.SetGlobalFloat(_ssrFactorId, value);
            Plugin.Log.LogInfo($"[ArenaMapPort][LightingLab] C: SSRFactor override={value:0.00} " +
                $"(was {before:0.00}, map={map.Key})");
        }

        // ---------------------------------------------------------------------------------------------
        // D — Wetness (WaterLevelOverride / RainOff). Log-only here; the live per-frame write happens in
        // WaterLevelPostfix (Harmony) and in RainWetnessRepairs.Postfix's own added RainOff check.
        // ---------------------------------------------------------------------------------------------
        static void ApplyWetness(ArenaMapDescriptor map, LabConfigBundle cfg)
        {
            string waterLevel = cfg.WaterLevelOverrideEnabled.Value
                ? cfg.WaterLevelOverrideValue.Value.ToString("0.00")
                : "off";
            Plugin.Log.LogInfo($"[ArenaMapPort][LightingLab] D: WaterLevel override={waterLevel} " +
                $"RainOff={cfg.RainOff.Value} (map={map.Key})");
        }

        // Harmony postfix target — HarmonyPatches.InstallLightingLabWaterLevelOverride.
        internal static void WaterLevelPostfix()
        {
            // MANDATORY first check — see ReflectionPostfix's own comment; RainController.AbstractState.
            // vmethod_15 runs for ANY rain-driven season state, including a real vanilla raid.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return;
            }
            var cfg = _activeCfg;
            if (cfg == null)
            {
                return;
            }
            if (cfg.RainOff.Value)
            {
                Shader.SetGlobalFloat(_waterLevelId, 0f);
                return;
            }
            if (cfg.WaterLevelOverrideEnabled.Value)
            {
                Shader.SetGlobalFloat(_waterLevelId, cfg.WaterLevelOverrideValue.Value);
            }
        }

        // Read by RainWetnessRepairs.Postfix's own added RainOff check.
        internal static bool IsRainOffForCurrentMap() => _activeCfg != null && _activeCfg.RainOff.Value;

        // ---------------------------------------------------------------------------------------------
        // E — GroundSmoothnessLive.
        // ---------------------------------------------------------------------------------------------
        static void ScheduleGroundScan(ArenaMapDescriptor map, LabConfigBundle cfg, LabState state)
        {
            if (_scanHost != null)
            {
                return; // already scheduled this raid
            }
            _scanHost = new GameObject("ArenaMapPort_LightingLabScan") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_scanHost);
            _scanHost.AddComponent<ScanRunner>().StartCoroutine(ScanRunner.RunAfter(GroundScanDelaySeconds, map, cfg, state));
        }

        sealed class ScanRunner : MonoBehaviour
        {
            internal static IEnumerator RunAfter(float seconds, ArenaMapDescriptor map, LabConfigBundle cfg, LabState state)
            {
                yield return new WaitForSeconds(seconds);
                try
                {
                    // Reads the multiplier LIVE (not the value captured when this coroutine was scheduled)
                    // so a config edit made during the 5s delay still takes effect on the first scan.
                    ScanGroundMaterials(map, state, cfg.GroundSmoothnessMultiplier.Value * WeatherRig.GroundSmoothnessBaseForCurrentMap());
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[ArenaMapPort][LightingLab] E: ground-material scan threw: {e}");
                }
            }
        }

        static void ScanGroundMaterials(ArenaMapDescriptor map, LabState state, float multiplier)
        {
            int renderersScanned = 0;
            int materialsMatched = 0;

            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    var renderers = root.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        renderersScanned++;
                        var renderer = renderers[r];
                        var goName = renderer.gameObject.name;
                        bool nameMatches =
                            goName.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            goName.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            goName.IndexOf("asphalt", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!nameMatches)
                        {
                            continue;
                        }

                        // sharedMaterials, never .materials — see ShaderRebind.cs/SceneInspector.cs's own
                        // documented reason (bundle materials are already per-map instances; .materials
                        // would needlessly instantiate a second copy per renderer per scan).
                        var mats = renderer.sharedMaterials;
                        for (int m = 0; m < mats.Length; m++)
                        {
                            var material = mats[m];
                            if (material == null || material.shader == null)
                            {
                                continue;
                            }
                            var shaderName = material.shader.name;
                            bool shaderMatches = shaderName == "Custom/Vert Paint Shader Solid" ||
                                shaderName.StartsWith("p0/Reflective", StringComparison.Ordinal);
                            if (!shaderMatches)
                            {
                                continue;
                            }

                            var props = DiscoverSmoothnessProps(material);
                            if (props.Count == 0)
                            {
                                continue; // shader has no smooth/gloss/shin-named Float/Range property — nothing to touch
                            }

                            state.GroundMaterials.Add(new GroundMaterialEntry { Material = material, Props = props });
                            materialsMatched++;
                            ApplyGroundSmoothness(material, props, multiplier);
                        }
                    }
                }
            }

            Plugin.Log.LogInfo($"[ArenaMapPort][LightingLab] E: scanned {renderersScanned} renderer(s), " +
                $"{materialsMatched} material(s) matched, multiplier={multiplier:0.00} (map={map.Key})");
            if (materialsMatched == 0)
            {
                Plugin.Log.LogWarning("[ArenaMapPort][LightingLab] E: 0 material(s) matched — the renderer-" +
                    "name filter ('floor'/'ground'/'asphalt') may need a hierarchy-path fallback (e.g. also " +
                    "checking the PARENT object's name) for this map's actual naming convention; the F10 " +
                    "SceneInspector hotkey (aim at the ground, press F10) shows the real renderer/material " +
                    "names to check against.");
            }
        }

        static List<(int propId, float original)> DiscoverSmoothnessProps(Material material)
        {
            var result = new List<(int, float)>();
            var shader = material.shader;
            if (shader == null)
            {
                return result;
            }

            int propCount;
            try
            {
                propCount = shader.GetPropertyCount();
            }
            catch
            {
                return result;
            }

            for (int i = 0; i < propCount; i++)
            {
                try
                {
                    var propType = shader.GetPropertyType(i);
                    if (propType != ShaderPropertyType.Float && propType != ShaderPropertyType.Range)
                    {
                        continue;
                    }
                    var propName = shader.GetPropertyName(i);
                    bool matches = false;
                    for (int h = 0; h < SmoothnessPropertyHints.Length; h++)
                    {
                        if (propName.IndexOf(SmoothnessPropertyHints[h], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches = true;
                            break;
                        }
                    }
                    if (!matches)
                    {
                        continue;
                    }
                    int id = Shader.PropertyToID(propName);
                    result.Add((id, material.GetFloat(id)));
                }
                catch
                {
                    // One bad property must not abort discovery of the rest — same defensive convention
                    // SceneInspector.cs's own shader-property dump already uses.
                }
            }
            return result;
        }

        static void ApplyGroundSmoothness(Material material, List<(int propId, float original)> props, float multiplier)
        {
            for (int i = 0; i < props.Count; i++)
            {
                material.SetFloat(props[i].propId, props[i].original * multiplier);
            }
        }

        static void ReapplyGroundSmoothness(ArenaMapDescriptor map, LabConfigBundle cfg, LabState state)
        {
            if (state.GroundMaterials.Count == 0)
            {
                return; // scan hasn't run yet this raid — nothing cached to reapply; the scan itself reads
                        // the multiplier live once it does run
            }
            float multiplier = cfg.GroundSmoothnessMultiplier.Value * WeatherRig.GroundSmoothnessBaseForCurrentMap();
            for (int i = 0; i < state.GroundMaterials.Count; i++)
            {
                var entry = state.GroundMaterials[i];
                if (entry.Material == null)
                {
                    continue;
                }
                ApplyGroundSmoothness(entry.Material, entry.Props, multiplier);
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][LightingLab] E: multiplier={multiplier:0.00} reapplied live " +
                $"to {state.GroundMaterials.Count} cached material(s) (map={map.Key})");
        }

        // ---------------------------------------------------------------------------------------------
        // F — SunIntensityMultiplier.
        // ---------------------------------------------------------------------------------------------
        static void ApplySunIntensity(ArenaMapDescriptor map, LabConfigBundle cfg, LabState state)
        {
            var sky = WeatherRig.ActiveRig?.Sky;
            if (sky == null)
            {
                return; // no Full rig (TOD_Sky) this raid — nothing to scale, matches "no visual change" default
            }
            state.CachedSky = sky; // see LabState.CachedSky's own doc comment

            if (!state.SunBaselineCaptured)
            {
                state.BaseDayLightIntensity = sky.Day.LightIntensity;
                state.BaseNightLightIntensity = sky.Night.LightIntensity;
                state.SunBaselineCaptured = true;
            }

            float multiplier = cfg.SunIntensityMultiplier.Value;
            float newDay = state.BaseDayLightIntensity * multiplier;
            float newNight = state.BaseNightLightIntensity * multiplier;
            if (Mathf.Approximately(sky.Day.LightIntensity, newDay) && Mathf.Approximately(sky.Night.LightIntensity, newNight))
            {
                return; // already at target — steady-state no-op, no log spam
            }

            float oldDay = sky.Day.LightIntensity;
            float oldNight = sky.Night.LightIntensity;
            sky.Day.LightIntensity = newDay;
            sky.Night.LightIntensity = newNight;

            Plugin.Log.LogInfo($"[ArenaMapPort][LightingLab] F: SunIntensityMultiplier={multiplier:0.00} " +
                $"Day.LightIntensity {oldDay:0.00} -> {newDay:0.00} Night.LightIntensity {oldNight:0.00} -> " +
                $"{newNight:0.00} (map={map.Key})");
        }

        // ---------------------------------------------------------------------------------------------
        // G — F9 snapshot hotkey. Same "poll a KeyboardShortcut every Update, do real work only inside the
        // keypress branch" pattern SceneInspector.cs's own F10 hotkey already uses.
        // ---------------------------------------------------------------------------------------------
        sealed class HotkeyRunner : MonoBehaviour
        {
            void Update()
            {
                try
                {
                    if (_snapshotHotkey != null && _snapshotHotkey.Value.IsDown() && ArenaMapRaid.IsLoaded())
                    {
                        RunSnapshot();
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[ArenaMapPort][LightingLab] snapshot Update threw: {ex}");
                }
            }
        }

        static void RunSnapshot()
        {
            try
            {
                var map = ArenaMapRaid.CurrentMap;
                if (map == null || !_configs.TryGetValue(map.Key, out var cfg))
                {
                    return;
                }
                _states.TryGetValue(map.Key, out var state);

                var cam = ResolveActiveCamera();
                string camInfo = cam != null
                    ? $"pos={Fmt(cam.transform.position)} fwd={Fmt(cam.transform.forward)}"
                    : "pos=n/a fwd=n/a (no active camera)";

                var sb = new StringBuilder();
                sb.AppendLine($"[ArenaMapPort][LightingLab][Snapshot] ==== {map.Key} ====");
                sb.AppendLine($"  camera: {camInfo}");
                sb.AppendLine($"  A UnityAmbient: preset={cfg.AmbientPresetEntry.Value} " +
                    $"custom=({cfg.AmbientCustomColor.Value}, intensity={cfg.AmbientCustomIntensity.Value:0.00})");
                sb.AppendLine($"  B GlobalReflection: preset={cfg.ReflectionPresetEntry.Value}");
                sb.AppendLine($"  C SSRFactor: configured={cfg.SSRFactorOverride.Value:0.00} " +
                    $"live={Shader.GetGlobalFloat(_ssrFactorId):0.00}");
                sb.AppendLine($"  D Wetness: WaterLevelOverrideEnabled={cfg.WaterLevelOverrideEnabled.Value} " +
                    $"WaterLevelOverrideValue={cfg.WaterLevelOverrideValue.Value:0.00} RainOff={cfg.RainOff.Value} " +
                    $"live _WaterLevel={Shader.GetGlobalFloat(_waterLevelId):0.00} " +
                    $"live _SurfaceWetIntensity={Shader.GetGlobalFloat(_surfaceWetIntensityId):0.00}");
                sb.AppendLine($"  B2 Lighting.ReflectionIntensity: scale={WeatherRig.ReflectionIntensityForCurrentMap():0.00}");
                sb.AppendLine($"  E GroundSmoothnessMultiplier: configured={cfg.GroundSmoothnessMultiplier.Value:0.00} " +
                    $"base(Lighting.GroundSmoothness)={WeatherRig.GroundSmoothnessBaseForCurrentMap():0.00} " +
                    $"effective={cfg.GroundSmoothnessMultiplier.Value * WeatherRig.GroundSmoothnessBaseForCurrentMap():0.00} " +
                    $"cachedMaterials={(state != null ? state.GroundMaterials.Count : 0)}");
                sb.AppendLine($"  F SunIntensityMultiplier: configured={cfg.SunIntensityMultiplier.Value:0.00}");
                sb.AppendLine($"  RenderSettings: ambientMode={RenderSettings.ambientMode} " +
                    $"ambientLight={Fmt(RenderSettings.ambientLight)} ambientIntensity={RenderSettings.ambientIntensity:0.00} " +
                    $"ambientEquatorColor={Fmt(RenderSettings.ambientEquatorColor)} " +
                    $"ambientGroundColor={Fmt(RenderSettings.ambientGroundColor)}");

                AppendReflectionProbeFaces(sb);

                // docs/progress/donor-settings-camera-weather.md §13 goal 1 — rain-cover depth-map
                // readback at THIS snapshot's own camera position, so "F9 at both spots" (§13 test steps)
                // tells whether a roof was actually photographed above the tester's feet.
                try
                {
                    sb.AppendLine(cam != null
                        ? "  " + RainCoverRepairs.BuildDepthReadback(cam.transform.position)
                        : "  RainCover depth readback: n/a (no active camera)");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  RainCover depth readback threw: {ex.GetType().Name}: {ex.Message}");
                }

                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ArenaMapPort][LightingLab] RunSnapshot threw: {ex}");
            }
        }

        // Candidate G's own read-back: the smallest generated mip of a cube face is already a GPU
        // box-filtered average of that whole face (AmbientLight.CubeRT is created with
        // useMipMap=true/autoGenerateMips=true, AmbientLight.cs:212-219) — copying just that 1x1 texel per
        // face (Graphics.CopyTexture straight into a reusable 1x1 scratch Texture2D) is far cheaper than
        // reading/averaging a full face and needs no RenderTexture.active/SetRenderTarget juggling.
        static void AppendReflectionProbeFaces(StringBuilder sb)
        {
            Texture bound;
            try
            {
                bound = Shader.GetGlobalTexture(_myGlobalReflectionProbeId);
            }
            catch (Exception e)
            {
                sb.AppendLine($"  G reflection-probe faces: Shader.GetGlobalTexture threw " +
                    $"({e.GetType().Name}) — not feasible.");
                return;
            }

            if (bound == null)
            {
                sb.AppendLine("  G reflection-probe faces: no texture currently bound to _MyGlobalReflectionProbe.");
                return;
            }
            if (bound.dimension != TextureDimension.Cube)
            {
                sb.AppendLine($"  G reflection-probe faces: bound texture is not Cube-dimension — not " +
                    $"feasible, logging metadata only: type={bound.GetType().Name} dimension={bound.dimension} " +
                    $"size={bound.width}x{bound.height}");
                return;
            }
            if (bound is RenderTexture rtCheck && !rtCheck.IsCreated())
            {
                sb.AppendLine($"  G reflection-probe faces: bound RenderTexture not yet created this raid " +
                    $"(AmbientLight.method_2 hasn't rendered a first frame yet) — not feasible, logging " +
                    $"metadata only: format={rtCheck.format} size={rtCheck.width}x{rtCheck.height}");
                return;
            }

            Texture2D scratch = null;
            try
            {
                scratch = new Texture2D(1, 1, TextureFormat.RGBAHalf, false);
                int mipCount = bound is Cubemap cubeAsset ? cubeAsset.mipmapCount
                    : bound is RenderTexture rtex ? rtex.mipmapCount : 1;
                int mip = Mathf.Max(0, mipCount - 1);

                var faceLines = new StringBuilder();
                for (int face = 0; face < 6; face++)
                {
                    try
                    {
                        Graphics.CopyTexture(bound, face, mip, scratch, 0, 0);
                        var c = scratch.GetPixel(0, 0);
                        faceLines.Append($" {(CubemapFace)face}={Fmt(c)}");
                    }
                    catch (Exception e)
                    {
                        faceLines.Append($" {(CubemapFace)face}=<threw {e.GetType().Name}>");
                    }
                }
                sb.AppendLine($"  G reflection-probe faces (type={bound.GetType().Name} " +
                    $"size={bound.width}x{bound.height} mip={mip}/{mipCount}):{faceLines}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"  G reflection-probe faces: readback failed ({e.GetType().Name}: " +
                    $"{e.Message}) — not feasible, logging metadata only: type={bound.GetType().Name} " +
                    $"size={bound.width}x{bound.height}");
            }
            finally
            {
                if (scratch != null)
                {
                    UnityEngine.Object.Destroy(scratch);
                }
            }
        }

        // Same resolution SceneInspector.cs/DaytimeVisualsProbe.cs already use.
        static Camera ResolveActiveCamera()
        {
            try
            {
                Camera c = CameraManager.Instance?.Camera;
                if (c != null)
                {
                    return c;
                }
            }
            catch
            {
                // CameraManager mid-teardown — fall through
            }
            return Camera.main;
        }

        static string Fmt(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
        static string Fmt(Color c) => $"RGBA({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})";
    }
}
