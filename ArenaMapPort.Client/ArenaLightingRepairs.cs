using System.Collections.Generic;
using System.Reflection;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// fix-lighting-movement, item 2 — brightens the reported "extremely dark, silhouettes-only, even
    /// with a flashlight" raid. See docs/progress/fix-lighting-movement.md for the full root-cause
    /// chain against the investigator/verifier reports; short version:
    ///
    ///  - A real LevelSettings component DOES exist (AuthorScriptsScene.cs:74-86) and its
    ///    Awake()/OnPreCullCallback (decompiled LevelSettings.cs:166-171,240-263, re-read this pass)
    ///    DOES push RenderSettings.ambientMode=Flat / ambientLight=SkyColor(0.5,0.5,0.5) into the live
    ///    RenderSettings every camera pre-cull, all raid long — the "LevelSettings is simply absent"
    ///    premise some investigator reports started from (and the project context's own stale summary)
    ///    does not hold for the current source; two independent verifier passes both re-confirmed the
    ///    component's presence directly against AuthorScriptsScene.cs and the shipped bundle.
    ///  - RenderSettings.ambientIntensity has NO EFFECT while ambientMode==Flat (confirmed against
    ///    Unity's own scripting reference: "Unity scales the ambient light only when ambientMode is
    ///    Skybox... in the other ambient modes, Unity reads the ambient colors directly, so changing
    ///    this value has no effect") — so brightening LevelSettings.AmbientIntensity would silently do
    ///    nothing. This repair instead scales the actual color fields (SkyColor/EquatorColor/
    ///    GroundColor) that DO drive RenderSettings.ambientLight/ambientEquatorColor/ambientGroundColor
    ///    under Flat mode.
    ///  - A real Directional light ("FAKE_SUN" per lighting-data's UnityPy census) also exists, enabled,
    ///    intensity 1.7 — but with an unusually dim/desaturated authored color (~0.18,0.18,0.20, net
    ///    effective brightness ≈0.3 per channel).
    ///  - Whether Equator's dominant "p0/Reflective/Bumped Specular[SMap]" shader family (53.5% of
    ///    materials) actually SAMPLES the live ambient at all is unconfirmed either way (it is a
    ///    compiled shader, no source available; one verifier's bytecode string-scan found no
    ///    unity_SHAr/_EFT_Ambient reference in it, but flagged that scan itself as inconclusive) — so
    ///    this repair does not gamble everything on ambient alone. It brightens both plausible levers at
    ///    once (the flat ambient LevelSettings already drives, AND the real-time point/spot/directional
    ///    lights the shader is built to respond to via its own single-light `_LightColor0` term), each
    ///    independently tunable live, so a tester can isolate which one actually mattered without a
    ///    bundle rebuild.
    ///
    /// Every multiplier/blend defaults to a MODEST, SAFE uplift (never a blowout) rather than a pure
    /// no-op, per this task's brief ("brighten a black scene to a playable level without blowing out a
    /// correctly lit one") — and every one multiplies/blends from a captured ORIGINAL baseline (each
    /// LevelSettings color field's own authored value; each Light's own authored intensity/color), never
    /// from whatever the previous config change already produced, so repeated live edits via
    /// F12/ConfigurationManager never compound.
    ///
    /// Multimap generalization (docs/progress/multimap-generalize.md): every ConfigEntry set and every
    /// piece of per-raid captured state (LevelSettings baseline colors, per-Light baselines, fill-light
    /// GameObjects, the ambient-fallback-written flag) is now keyed per map instead of a single unkeyed
    /// instance. Equator keeps its exact pre-generalization config section name "Lighting (arena_equator)"
    /// and default values, so a user's already-tuned config values keep applying without a reset; Bay5
    /// gets its own section "Lighting (arena_bay5)" with the SAME defaults (no Bay5-specific tuning has
    /// been done yet — a playtest-based re-tune is future work, see docs/progress/multimap-generalize.md).
    /// Gated on <see cref="ArenaMapRaid.IsLoaded()"/> throughout — a complete no-op for every other map's
    /// raid. Every Light this touches is one actually authored in the CURRENT map's own owned scenes
    /// (found by walking each owned Scene's own root GameObjects, the same technique ShaderRebind.cs
    /// already uses for materials) — never the player's weapon flashlight/muzzle-flash lights or another
    /// bot's, which live under an entirely different (DontDestroyOnLoad/player) hierarchy.
    ///
    /// PLAN-TWO-MAPS light-culling item — <see cref="ApplyLights"/> writes <c>Light.intensity</c>
    /// directly, but on any Light that also carries a <c>CullingLightObject</c> sibling (Equator 472 /
    /// Bay5 186 of them, per the extractor's manifest counts) that write is only a one-frame illusion:
    /// <c>CullingLightObject.Awake</c> caches <c>float_1 = _useLightIntensityFromEditor ?
    /// _maxLightIntensity : _light.intensity</c> once (decompile CullingLightObject.cs), and from then on
    /// every call to <c>UpdateCullingObject</c> (its own distance/frustum fade update, driven off
    /// <see cref="CullingManager"/> — see the fallback EnvironmentRepairs.cs now creates) does
    /// <c>_light.intensity = float_1 * multiplier</c>, silently overwriting whatever this repair last set
    /// the moment the light's next fade update runs (immediately, if <c>CullingManager</c> is live and the
    /// light is visible/unfaded, since <c>multiplier</c> is then 1). <see cref="SyncCullingLightIntensity"/>
    /// keeps that cached <c>float_1</c> in lock-step with the exact same target value <see cref="ApplyLights"/>
    /// just wrote to <c>Light.intensity</c> — sun/global multipliers keep applying even once
    /// <c>CullingManager</c> starts pushing its own writes, and turning the master switch off restores
    /// <c>float_1</c> to the authored baseline too, via the same identity-multiplier codepath that already
    /// restores <c>Light.intensity</c> (<see cref="ApplyAll"/> passes multiplier 1 / blend 0 when
    /// <c>Enabled=false</c>, so no separate restore branch is needed). Known gap: a Light whose
    /// <c>CullingLightObject</c> has an enabled <c>LightFlicker</c> sibling (two on Equator) takes its
    /// intensity from <c>Flicker.ManualUpdate</c>'s own formula instead of <c>float_1</c>, so the
    /// multipliers do not reach those two lights. Reflection-only (private field,
    /// no public setter) — resolved once via <see cref="GetCullingLightFloat1Field"/> and logged-and-skipped
    /// if ever missing (BSG rename), same "log and skip a missing target" spirit as
    /// <see cref="HarmonyPatches.Verify"/>, so a missing field disables only this one sub-fix.
    /// </summary>
    internal static class ArenaLightingRepairs
    {
        // Shadowless directional fill pair (down + weaker up), parented to a GameObject in the current
        // map's Scripts scene so it unloads with the raid. Stand-in for EFT's missing deferred ambient
        // pass: the dominant p0/* shaders get their ambient from AmbientLight's "Custom Ambient" command
        // buffer (AmbientLight.cs:311, fed via SetSH by ToDController/TODSkySimple, ToDController.cs:265),
        // which does not exist in our scenes, so RenderSettings ambient alone never reaches them.
        const string FillLightName = "ArenaMapPort_FillLight";

        sealed class ConfigBundle
        {
            internal ConfigEntry<bool> Enabled;
            internal ConfigEntry<float> AmbientColorMultiplier;
            internal ConfigEntry<float> GlobalLightIntensityMultiplier;
            internal ConfigEntry<float> SunIntensityMultiplier;
            internal ConfigEntry<float> SunColorWhiteBlend;
            internal ConfigEntry<float> FogDensity;
            internal ConfigEntry<float> FillLightIntensity;
            internal ConfigEntry<bool> SyncCullingLightIntensity;
        }

        struct LightBaseline
        {
            internal Light Light;
            internal float Intensity;
            internal Color Color;
        }

        sealed class MapState
        {
            // --- LevelSettings baseline (captured once per raid, from the pristine authored values) ---
            internal bool LevelSettingsBaselineCaptured;
            internal Color BaseSkyColor;
            internal Color BaseEquatorColor;
            internal Color BaseGroundColor;

            // Set whenever ApplyAmbient() takes its defensive RenderSettings-direct fallback path — unlike
            // every other field this repair touches, RenderSettings is true global engine state, not
            // owned by any scene/component, so nothing naturally resets it when this map's scenes unload.
            // Tracked so OnSceneUnloaded knows whether it must restore RenderSettings.ambient* itself
            // before the next scene (which may or may not carry its own LevelSettings) runs.
            internal bool AmbientFallbackWritten;

            // --- Light baseline (captured once per raid, per Light actually found in this map's scenes) ---
            internal readonly List<LightBaseline> Lights = new List<LightBaseline>();

            internal Light FillDown;
            internal Light FillUp;
        }

        static readonly Dictionary<string, ConfigBundle> _configs = new Dictionary<string, ConfigBundle>();
        static readonly Dictionary<string, MapState> _states = new Dictionary<string, MapState>();
        static bool _installed;

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
                _states[map.Key] = new MapState();
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static ConfigBundle BindConfig(ArenaMapDescriptor map)
        {
            // Equator's section name is byte-identical to the pre-generalization literal
            // ("Lighting (arena_equator)", since map.Key == "arena_equator") — a user's already-tuned
            // config values keep applying without a config reset.
            string section = $"Lighting ({map.Key})";
            var cfg = new ConfigBundle();

            cfg.Enabled = Plugin.Instance.Config.Bind(section, "Enabled", true,
                $"Master switch for every repair below. Only ever applies during a {map.Key} raid — " +
                "every other map's raid is untouched regardless of this value.");

            cfg.AmbientColorMultiplier = Plugin.Instance.Config.Bind(section, "AmbientColorMultiplier", 0.5f,
                new ConfigDescription(
                    "Multiplies LevelSettings' own authored ambient colors (Sky/Equator/Ground, each " +
                    "0.5 grey) before they reach RenderSettings. 1.0 = authored value (the dim flat grey " +
                    "fill from the 2026-09-14 dark playtest); 2.0 = full-bright white flat ambient.",
                    new AcceptableValueRange<float>(0.2f, 6f)));

            cfg.GlobalLightIntensityMultiplier = Plugin.Instance.Config.Bind(section, "GlobalLightIntensityMultiplier", 0.5f,
                new ConfigDescription(
                    "Multiplies every non-directional (point/spot) Light's own authored intensity in this " +
                    "map's scenes. 1.0 = authored value.",
                    new AcceptableValueRange<float>(0.2f, 6f)));

            cfg.SunIntensityMultiplier = Plugin.Instance.Config.Bind(section, "SunIntensityMultiplier", 0.2f,
                new ConfigDescription(
                    "Multiplies the one Directional light's own authored intensity (Equator: \"FAKE_SUN\", " +
                    "1.7). 1.0 = authored value.",
                    new AcceptableValueRange<float>(0.2f, 8f)));

            cfg.SunColorWhiteBlend = Plugin.Instance.Config.Bind(section, "SunColorWhiteBlend", 0.1f,
                new ConfigDescription(
                    "Blends the Directional light's own dim/desaturated authored color toward white. " +
                    "0 = authored color unchanged, 1 = pure white.",
                    new AcceptableValueRange<float>(0f, 1f)));

            cfg.FogDensity = Plugin.Instance.Config.Bind(section, "FogDensity", 0f,
                new ConfigDescription(
                    "Density of a light grey distance fog (exponential squared). 0 = no fog. Around 0.01 is " +
                    "a light haze, 0.04 is roughly Arena's own authored fog.",
                    new AcceptableValueRange<float>(0f, 0.06f)));

            cfg.FillLightIntensity = Plugin.Instance.Config.Bind(section, "FillLightIntensity", 0.2f,
                new ConfigDescription(
                    "Intensity of a shadowless directional fill light (from above, plus half as much from " +
                    "below) that stands in for EFT's missing ambient pass — the main lever if surfaces stay " +
                    "dark away from lamps. 0 = off.",
                    new AcceptableValueRange<float>(0f, 3f)));

            cfg.SyncCullingLightIntensity = Plugin.Instance.Config.Bind(section, "SyncCullingLightIntensity", true,
                "Keeps CullingLightObject's own cached intensity field in step with the sun/global " +
                "multipliers above, on every Light that carries one. Without this, CullingLightObject's " +
                "own distance/frustum fade update (once CullingManager is running — see EnvironmentRepairs.cs' " +
                "fallback) overwrites Light.intensity back to the pre-repair authored value on its next " +
                "update. Independent of Enabled: stays on so the sync itself, not just the multipliers, " +
                "keeps applying live.");

            cfg.Enabled.SettingChanged += (_, __) => ReapplyIfLoaded(map);
            cfg.FillLightIntensity.SettingChanged += (_, __) => ReapplyIfLoaded(map);
            cfg.AmbientColorMultiplier.SettingChanged += (_, __) => ReapplyIfLoaded(map);
            cfg.GlobalLightIntensityMultiplier.SettingChanged += (_, __) => ReapplyIfLoaded(map);
            cfg.SunIntensityMultiplier.SettingChanged += (_, __) => ReapplyIfLoaded(map);
            cfg.SunColorWhiteBlend.SettingChanged += (_, __) => ReapplyIfLoaded(map);
            cfg.FogDensity.SettingChanged += (_, __) => ReapplyIfLoaded(map);
            cfg.SyncCullingLightIntensity.SettingChanged += (_, __) => ReapplyIfLoaded(map);

            return cfg;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaMapRaid.IsOwnedScene(scene.name))
            {
                return;
            }

            // Scene is already registered with SceneManager by the time sceneLoaded fires (see
            // ArenaMapRaid's own doc comment), so CurrentMap resolves even on the Scripts scene's own
            // load event.
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !_states.TryGetValue(map.Key, out var state))
            {
                return;
            }

            CaptureLightsFromScene(scene, state);
            ApplyAll(map, logSummary: true);
        }

        static void OnSceneUnloaded(Scene scene)
        {
            // Stateless resolution (not ArenaMapRaid.CurrentMap) — see ArenaMapRaid's own doc comment
            // for why an unload handler must never depend on a shared mutable "current map" field.
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null || !_states.TryGetValue(map.Key, out var state))
            {
                return; // reset once per raid, on the raid's own Scripts-scene teardown
            }

            // RenderSettings.fog and (on the defensive fallback path only) RenderSettings.ambient* are
            // true global engine state, not scoped to any of our components — unlike LevelSettings'
            // own fields and each Light's intensity/color, nothing destroys them when this map's scenes
            // unload. In the common case the NEXT raid's own LevelSettings.Awake()/OnPostLoadingScene()
            // (decompile: LevelSettings.cs:166-171,173-185 -> ApplySettings() at :194-218) overwrites
            // these again before it matters — but a scene loaded between raids that carries no
            // LevelSettings of its own (e.g. a results/menu scene) would otherwise inherit whatever we
            // last wrote. Restore the authored no-op values explicitly so nothing outlives this raid.
            RenderSettings.fog = false; // AuthorScriptsScene.cs:83 authors LevelSettings.fog = false
            RenderSettings.fogDensity = 0f;
            RenderSettings.fogColor = new Color(0.5f, 0.5f, 0.5f, 1f); // Unity's default; fogMode stays valid
            if (state.AmbientFallbackWritten)
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                var authored = new Color(0.5f, 0.5f, 0.5f, 1f); // AuthorScriptsScene.cs:79-81
                RenderSettings.ambientLight = authored;
                RenderSettings.ambientSkyColor = authored;
                RenderSettings.ambientEquatorColor = authored;
                RenderSettings.ambientGroundColor = authored;
                state.AmbientFallbackWritten = false;
            }

            state.LevelSettingsBaselineCaptured = false;
            state.Lights.Clear(); // next raid gets fresh Light instances — old baselines would be stale/destroyed
            _lastCullingLightCount = 0;
            _lastEditorIntensityBaselines = 0;
            state.FillDown = null; // destroyed with the Scripts scene
            state.FillUp = null;
        }

        static void CaptureLightsFromScene(Scene scene, MapState state)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var lights = root.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                {
                    var light = lights[i];
                    // PLAN-TWO-MAPS item 7: Arena saves many Lights in their culled state (m_Intensity 0 —
                    // 60+ Equator 'BasePoint' lights) and CullingLightObject owns the real value
                    // (_useLightIntensityFromEditor ? _maxLightIntensity : Light.intensity, CullingLightObject.Awake).
                    // Capturing light.intensity there would bake 0 into the baseline, the multiplied target and
                    // the float_1 sync below, and keep those lights dark for the raid.
                    float authored = light.intensity;
                    var culling = light.GetComponent<CullingLightObject>();
                    if (culling != null) _lastCullingLightCount++;
                    if (culling != null && culling._useLightIntensityFromEditor)
                    {
                        authored = culling._maxLightIntensity;
                        _lastEditorIntensityBaselines++;
                    }
                    state.Lights.Add(new LightBaseline { Light = light, Intensity = authored, Color = light.color });
                }
            }
        }

        static void ReapplyIfLoaded(ArenaMapDescriptor map)
        {
            if (ArenaMapRaid.CurrentMap == map)
            {
                ApplyAll(map, logSummary: false);
            }
        }

        static void ApplyAll(ArenaMapDescriptor map, bool logSummary)
        {
            if (ArenaMapRaid.CurrentMap != map)
            {
                return;
            }
            var cfg = _configs[map.Key];
            var state = _states[map.Key];

            // Enabled=false applies neutral values instead of returning, so switching it off live
            // restores the authored look rather than freezing the last tuned state.
            bool on = cfg.Enabled.Value;
            bool levelSettingsFound = ApplyAmbient(state, on ? cfg.AmbientColorMultiplier.Value : 1f, on ? cfg.FogDensity.Value : 0f);
            int lightsApplied = ApplyLights(
                state,
                on ? cfg.GlobalLightIntensityMultiplier.Value : 1f,
                on ? cfg.SunIntensityMultiplier.Value : 1f,
                on ? cfg.SunColorWhiteBlend.Value : 0f,
                cfg.SyncCullingLightIntensity.Value); // independent of Enabled — see BindConfig's doc string
            // weather-rig-stage0: while a WeatherRig clone is active for this raid, its real
            // AmbientLight/_SHAr feeds ambient instead — this fill light becomes redundant (and can
            // visibly double up interior brightness), unless KeepFillLight explicitly asks to keep it.
            float fillLightIntensity = on ? cfg.FillLightIntensity.Value : 0f;
            if (on && WeatherRig.IsActiveForCurrentMap() && !WeatherRig.KeepFillLightForCurrentMap())
            {
                fillLightIntensity = 0f;
            }
            bool fillActive = ApplyFillLight(map, state, fillLightIntensity);

            if (logSummary)
            {
                Plugin.Log.LogInfo(
                    $"[ArenaMapPort][Lighting] {map.Key} applied (Enabled={on}): LevelSettings-found={levelSettingsFound} " +
                    $"(ambientColorMultiplier={cfg.AmbientColorMultiplier.Value:0.00}), {lightsApplied} light(s) " +
                    $"(globalMultiplier={cfg.GlobalLightIntensityMultiplier.Value:0.00}, " +
                    $"sunMultiplier={cfg.SunIntensityMultiplier.Value:0.00}, sunWhiteBlend={cfg.SunColorWhiteBlend.Value:0.00}), " +
                    $"fillLight={(fillActive ? cfg.FillLightIntensity.Value.ToString("0.00") : "off")}, fogDensity={cfg.FogDensity.Value:0.000}, " +
                    $"syncCullingLightIntensity={cfg.SyncCullingLightIntensity.Value} (cullingLights={_lastCullingLightCount}, " +
                    $"editorIntensityBaselines={_lastEditorIntensityBaselines})");
            }
        }

        // Returns whether the fill light is active. Created lazily inside the current map's Scripts scene.
        static bool ApplyFillLight(ArenaMapDescriptor map, MapState state, float intensity)
        {
            if (state.FillDown == null || state.FillUp == null)
            {
                if (intensity <= 0f)
                {
                    return false;
                }
                var scripts = SceneManager.GetSceneByName(map.ScriptsSceneName);
                if (!scripts.IsValid() || !scripts.isLoaded)
                {
                    return false;
                }
                var root = new GameObject(FillLightName);
                SceneManager.MoveGameObjectToScene(root, scripts);
                state.FillDown = CreateFillLight(root, "Down", Quaternion.Euler(90f, 0f, 0f));
                state.FillUp = CreateFillLight(root, "Up", Quaternion.Euler(-90f, 0f, 0f));
            }

            bool active = intensity > 0f;
            state.FillDown.enabled = active;
            state.FillUp.enabled = active;
            state.FillDown.intensity = intensity;
            state.FillUp.intensity = intensity * 0.5f;
            return active;
        }

        static Light CreateFillLight(GameObject root, string suffix, Quaternion rotation)
        {
            var go = new GameObject(FillLightName + "_" + suffix);
            go.transform.SetParent(root.transform, false);
            go.transform.rotation = rotation;
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.color = new Color(0.9f, 0.93f, 1f);
            return light;
        }

        // Returns true if a live LevelSettings singleton was found and tuned (the expected, confirmed
        // case — see class doc comment), false if it fell back to writing RenderSettings directly
        // (defensive only; should not happen given the component is authored and confirmed present).
        static bool ApplyAmbient(MapState state, float m, float fogDensity)
        {
            var levelSettings = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;

            // weather-rig-stage0: TOD_Sky.UpdateFog (runs every LateUpdate once a rig is active) writes
            // ONLY RenderSettings.fogColor — never fogMode/fogDensity/the fog enabled flag (confirmed
            // directly from the decompiled switch body). Our own one-shot fogColor write would just be
            // silently overridden from the next frame onward, so skip writing it (and the LevelSettings
            // fogColor field, which only ever feeds ApplySettings()'s own one-shot push and is otherwise
            // inert once the rig owns the live value) while a rig is active — everything else here
            // (fogMode/fogDensity/the enabled flag, and every ambient color field) stays under this
            // repair's control regardless of rig state.
            bool ownFogColor = !WeatherRig.IsActiveForCurrentMap();

            // daytime-and-visuals F5 — confirmed double-ambient write: while a WeatherRig is active,
            // its own AmbientLight/_SHAr already feeds ambient every frame (ToDController.Update ->
            // AmbientLightScript.SetSH), and this repair's own SkyColor/EquatorColor/GroundColor push
            // below (which LevelSettings.OnPreCullCallback re-applies into RenderSettings.ambient* every
            // camera pre-cull) was landing UNGATED on top of it — live-confirmed as exactly 0.5x the
            // authored base ambient (ambientLight=(0.25,0.25,0.25) while this repair's own
            // AmbientColorMultiplier default is 0.5). ownFogColor (above) and the fill light
            // (ApplyFillLight/ApplyAll) already gate the same way; this closes the one remaining ungated
            // write.
            bool ownAmbientColor = !WeatherRig.IsActiveForCurrentMap();

            if (levelSettings != null)
            {
                // The authored LevelSettings leaves fogMode at default(FogMode) = 0 and fogColor black,
                // and ApplySettings copies both into RenderSettings (LevelSettings.cs:206-211). The FPS
                // camera's GlobalFog image effect ignores RenderSettings.fog and computes its fog factor
                // only for modes 1-3, so mode 0 yields factor 0: every non-sky pixel becomes fogColor
                // (black). Patch the LevelSettings fields too, since OnPostLoadingScene re-applies them.
                levelSettings.fogMode = FogMode.ExponentialSquared;
                levelSettings.fogDensity = fogDensity;
                if (ownFogColor)
                {
                    levelSettings.fogColor = FogColor;
                }
                levelSettings.fog = fogDensity > 0f;
                ApplyFog(fogDensity, ownFogColor);

                if (!state.LevelSettingsBaselineCaptured)
                {
                    state.BaseSkyColor = levelSettings.SkyColor;
                    state.BaseEquatorColor = levelSettings.EquatorColor;
                    state.BaseGroundColor = levelSettings.GroundColor;
                    state.LevelSettingsBaselineCaptured = true;
                }

                if (ownAmbientColor)
                {
                    levelSettings.SkyColor = ScaleColor(state.BaseSkyColor, m);
                    levelSettings.EquatorColor = ScaleColor(state.BaseEquatorColor, m);
                    levelSettings.GroundColor = ScaleColor(state.BaseGroundColor, m);
                }
                // LevelSettings.OnPreCullCallback (decompile, LevelSettings.cs:240-263) re-pushes these
                // exact fields into RenderSettings every camera pre-cull on its own — we only need to
                // update the fields it reads from. Fog is not re-pushed per frame, hence ApplyFog above.
                return true;
            }

            // Defensive fallback — belt-and-braces only, see class doc comment; keeps the raid from
            // being pitch black if LevelSettings is ever genuinely absent again (future scene change).
            state.AmbientFallbackWritten = true; // so OnSceneUnloaded knows to restore RenderSettings.ambient*
            RenderSettings.ambientMode = AmbientMode.Flat;
            var fallback = ScaleColor(new Color(0.5f, 0.5f, 0.5f, 1f), m);
            RenderSettings.ambientLight = fallback;
            RenderSettings.ambientSkyColor = fallback;
            RenderSettings.ambientEquatorColor = fallback;
            RenderSettings.ambientGroundColor = fallback;
            ApplyFog(fogDensity, ownFogColor);
            Plugin.Log.LogWarning("[ArenaMapPort][Lighting] LevelSettings singleton not found at apply time — " +
                                   "wrote RenderSettings ambient directly as a fallback (should not happen; the " +
                                   "component is authored in AuthorScriptsScene.cs and was confirmed present in " +
                                   "the shipped bundle — see docs/progress/fix-lighting-movement.md).");
            return false;
        }

        // internal, not private: weather-rig-stage0's LightingDiagnosticProbe.ProbeWeatherRig reads this
        // side-by-side with TOD_Sky's own live fogColor to show which system last won the write.
        internal static readonly Color FogColor = new Color(0.62f, 0.66f, 0.70f, 1f);

        static void ApplyFog(float density, bool ownFogColor)
        {
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = density;
            if (ownFogColor)
            {
                RenderSettings.fogColor = FogColor;
            }
            RenderSettings.fog = density > 0f;
        }

        static int ApplyLights(MapState state, float globalMultiplier, float sunMultiplier, float sunWhiteBlend,
            bool syncCullingLightIntensity)
        {
            var float1Field = syncCullingLightIntensity ? GetCullingLightFloat1Field() : null;

            int applied = 0;
            for (int i = 0; i < state.Lights.Count; i++)
            {
                var baseline = state.Lights[i];
                if (baseline.Light == null)
                {
                    continue; // destroyed/unloaded since capture
                }

                float target;
                if (baseline.Light.type == LightType.Directional)
                {
                    target = baseline.Intensity * sunMultiplier;
                    baseline.Light.intensity = target;
                    baseline.Light.color = Color.Lerp(baseline.Color, Color.white, sunWhiteBlend);
                }
                else
                {
                    target = baseline.Intensity * globalMultiplier;
                    baseline.Light.intensity = target;
                }

                if (float1Field != null)
                {
                    // Only Lights that actually carry the sibling component are affected — most Arena
                    // lights on either map do (472/186 of them per the manifest), but not all.
                    var cullingLight = baseline.Light.GetComponent<CullingLightObject>();
                    if (cullingLight != null)
                    {
                        float1Field.SetValue(cullingLight, target);
                    }
                }

                applied++;
            }
            return applied;
        }

        // Diagnostics for the summary line (PLAN-TWO-MAPS item 7): how many captured Lights carry a
        // CullingLightObject (472/186 with the v4 bundles, 0 with an older cached scenes bundle) and how many
        // baselines came from _maxLightIntensity instead of the saved Light.intensity.
        static int _lastCullingLightCount;
        static int _lastEditorIntensityBaselines;

        static FieldInfo _cullingLightFloat1Field;
        static bool _cullingLightFloat1FieldLookupDone;

        // CullingLightObject.float_1 (decompile CullingLightObject.cs) — private, no accessor. Cached
        // once; if the field is ever renamed/removed by a future game build this logs once and every
        // subsequent ApplyLights call simply skips the sync (same "log and skip a missing target" spirit
        // as HarmonyPatches.Verify — everything else in this repair keeps working).
        static FieldInfo GetCullingLightFloat1Field()
        {
            if (_cullingLightFloat1FieldLookupDone)
            {
                return _cullingLightFloat1Field;
            }
            _cullingLightFloat1FieldLookupDone = true;
            _cullingLightFloat1Field = typeof(CullingLightObject).GetField("float_1",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_cullingLightFloat1Field == null)
            {
                Plugin.Log.LogWarning("[ArenaMapPort][Lighting] CullingLightObject.float_1 field not found via " +
                                       "reflection (renamed/removed?) — SyncCullingLightIntensity will not run " +
                                       "this raid; every other lighting repair keeps applying.");
            }
            return _cullingLightFloat1Field;
        }

        static Color ScaleColor(Color c, float m)
        {
            return new Color(c.r * m, c.g * m, c.b * m, c.a);
        }
    }
}
