using System.Collections.Generic;
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
    /// Gated on ArenaEquatorRaid.IsLoaded() throughout — a complete no-op for every other map's raid.
    /// Every Light this touches is one actually authored in our own owned scenes (found by walking each
    /// owned Scene's own root GameObjects, the same technique ShaderRebind.cs already uses for
    /// materials) — never the player's weapon flashlight/muzzle-flash lights or another bot's, which
    /// live under an entirely different (DontDestroyOnLoad/player) hierarchy.
    /// </summary>
    internal static class ArenaLightingRepairs
    {
        const string ConfigSection = "Lighting (arena_equator)";

        static ConfigEntry<bool> _enabled;
        static ConfigEntry<float> _ambientColorMultiplier;
        static ConfigEntry<float> _globalLightIntensityMultiplier;
        static ConfigEntry<float> _sunIntensityMultiplier;
        static ConfigEntry<float> _sunColorWhiteBlend;
        static ConfigEntry<float> _fogDensity;
        static ConfigEntry<float> _fillLightIntensity;

        static bool _installed;

        // Shadowless directional fill pair (down + weaker up), parented to a GameObject in our Scripts
        // scene so it unloads with the raid. Stand-in for EFT's missing deferred ambient pass: the
        // dominant p0/* shaders get their ambient from AmbientLight's "Custom Ambient" command buffer
        // (AmbientLight.cs:311, fed via SetSH by ToDController/TODSkySimple, ToDController.cs:265), which
        // does not exist in our scenes, so RenderSettings ambient alone never reaches them.
        const string FillLightName = "ArenaMapPort_FillLight";
        static Light _fillDown;
        static Light _fillUp;

        // --- LevelSettings baseline (captured once per raid, from the pristine authored values) -------
        static bool _levelSettingsBaselineCaptured;
        static Color _baseSkyColor;
        static Color _baseEquatorColor;
        static Color _baseGroundColor;

        // Set whenever ApplyAmbient() takes its defensive RenderSettings-direct fallback path (see
        // below) — unlike every other field this repair touches, RenderSettings is true global engine
        // state, not owned by any scene/component, so nothing naturally resets it when our scenes
        // unload. Tracked so OnSceneUnloaded knows whether it must restore RenderSettings.ambient*
        // itself before the next scene (which may or may not carry its own LevelSettings) runs.
        static bool _ambientFallbackWritten;

        // --- Light baseline (captured once per raid, per Light actually found in our own scenes) ------
        struct LightBaseline
        {
            internal Light Light;
            internal float Intensity;
            internal Color Color;
        }
        static readonly List<LightBaseline> _lights = new List<LightBaseline>();

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            BindConfig();

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void BindConfig()
        {
            _enabled = Plugin.Instance.Config.Bind(ConfigSection, "Enabled", true,
                "Master switch for every repair below. Only ever applies during an arena_equator raid — " +
                "every other map's raid is untouched regardless of this value.");

            _ambientColorMultiplier = Plugin.Instance.Config.Bind(ConfigSection, "AmbientColorMultiplier", 2.0f,
                new ConfigDescription(
                    "Multiplies LevelSettings' own authored ambient colors (Sky/Equator/Ground, each " +
                    "0.5 grey) before they reach RenderSettings. 1.0 = authored value (the dim flat grey " +
                    "fill from the 2026-09-14 dark playtest); 2.0 = full-bright white flat ambient.",
                    new AcceptableValueRange<float>(0.2f, 6f)));

            _globalLightIntensityMultiplier = Plugin.Instance.Config.Bind(ConfigSection, "GlobalLightIntensityMultiplier", 1.5f,
                new ConfigDescription(
                    "Multiplies every non-directional (point/spot) Light's own authored intensity in the " +
                    "Equator scenes. 1.0 = authored value.",
                    new AcceptableValueRange<float>(0.2f, 6f)));

            _sunIntensityMultiplier = Plugin.Instance.Config.Bind(ConfigSection, "SunIntensityMultiplier", 1.6f,
                new ConfigDescription(
                    "Multiplies the one Directional light's (\"FAKE_SUN\") own authored intensity (1.7). " +
                    "1.0 = authored value.",
                    new AcceptableValueRange<float>(0.2f, 8f)));

            _sunColorWhiteBlend = Plugin.Instance.Config.Bind(ConfigSection, "SunColorWhiteBlend", 0.5f,
                new ConfigDescription(
                    "Blends the Directional light's own dim/desaturated authored color (~0.18,0.18,0.20) " +
                    "toward white. 0 = authored color unchanged, 1 = pure white.",
                    new AcceptableValueRange<float>(0f, 1f)));

            _fogDensity = Plugin.Instance.Config.Bind(ConfigSection, "FogDensity", 0f,
                new ConfigDescription(
                    "Density of a light grey distance fog (exponential squared). 0 = no fog. Around 0.01 is " +
                    "a light haze, 0.04 is roughly Arena's own authored fog.",
                    new AcceptableValueRange<float>(0f, 0.06f)));

            _fillLightIntensity = Plugin.Instance.Config.Bind(ConfigSection, "FillLightIntensity", 0.4f,
                new ConfigDescription(
                    "Intensity of a shadowless directional fill light (from above, plus half as much from " +
                    "below) that stands in for EFT's missing ambient pass — the main lever if surfaces stay " +
                    "dark away from lamps. 0 = off.",
                    new AcceptableValueRange<float>(0f, 3f)));

            _enabled.SettingChanged += (_, __) => ReapplyIfLoaded();
            _fillLightIntensity.SettingChanged += (_, __) => ReapplyIfLoaded();
            _ambientColorMultiplier.SettingChanged += (_, __) => ReapplyIfLoaded();
            _globalLightIntensityMultiplier.SettingChanged += (_, __) => ReapplyIfLoaded();
            _sunIntensityMultiplier.SettingChanged += (_, __) => ReapplyIfLoaded();
            _sunColorWhiteBlend.SettingChanged += (_, __) => ReapplyIfLoaded();
            _fogDensity.SettingChanged += (_, __) => ReapplyIfLoaded();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaEquatorRaid.IsOwnedScene(scene.name))
            {
                return;
            }
            CaptureLightsFromScene(scene);
            ApplyAll(logSummary: true);
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (scene.name != ArenaMapPort.Shared.ArenaEquatorLocation.ScriptsSceneName)
            {
                return; // reset once per raid, on the raid's own Scripts-scene teardown
            }

            // RenderSettings.fog and (on the defensive fallback path only) RenderSettings.ambient* are
            // true global engine state, not scoped to any of our components — unlike LevelSettings'
            // own fields and each Light's intensity/color, nothing destroys them when our scenes
            // unload. In the common case the NEXT raid's own LevelSettings.Awake()/OnPostLoadingScene()
            // (decompile: LevelSettings.cs:166-171,173-185 -> ApplySettings() at :194-218) overwrites
            // these again before it matters — but a scene loaded between raids that carries no
            // LevelSettings of its own (e.g. a results/menu scene) would otherwise inherit whatever we
            // last wrote. Restore the authored no-op values explicitly so nothing outlives this raid.
            RenderSettings.fog = false; // AuthorScriptsScene.cs:83 authors LevelSettings.fog = false
            RenderSettings.fogDensity = 0f;
            if (_ambientFallbackWritten)
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                var authored = new Color(0.5f, 0.5f, 0.5f, 1f); // AuthorScriptsScene.cs:79-81
                RenderSettings.ambientLight = authored;
                RenderSettings.ambientSkyColor = authored;
                RenderSettings.ambientEquatorColor = authored;
                RenderSettings.ambientGroundColor = authored;
                _ambientFallbackWritten = false;
            }

            _levelSettingsBaselineCaptured = false;
            _lights.Clear(); // next raid gets fresh Light instances — old baselines would be stale/destroyed
            _fillDown = null; // destroyed with the Scripts scene
            _fillUp = null;
        }

        static void CaptureLightsFromScene(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var lights = root.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                {
                    var light = lights[i];
                    _lights.Add(new LightBaseline { Light = light, Intensity = light.intensity, Color = light.color });
                }
            }
        }

        static void ReapplyIfLoaded()
        {
            if (ArenaEquatorRaid.IsLoaded())
            {
                ApplyAll(logSummary: false);
            }
        }

        static void ApplyAll(bool logSummary)
        {
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return;
            }

            // Enabled=false applies neutral values instead of returning, so switching it off live
            // restores the authored look rather than freezing the last tuned state.
            bool on = _enabled.Value;
            bool levelSettingsFound = ApplyAmbient(on ? _ambientColorMultiplier.Value : 1f, on ? _fogDensity.Value : 0f);
            int lightsApplied = ApplyLights(
                on ? _globalLightIntensityMultiplier.Value : 1f,
                on ? _sunIntensityMultiplier.Value : 1f,
                on ? _sunColorWhiteBlend.Value : 0f);
            bool fillActive = ApplyFillLight(on ? _fillLightIntensity.Value : 0f);

            if (logSummary)
            {
                Plugin.Log.LogInfo(
                    $"[ArenaMapPort][Lighting] applied (Enabled={on}): LevelSettings-found={levelSettingsFound} " +
                    $"(ambientColorMultiplier={_ambientColorMultiplier.Value:0.00}), {lightsApplied} light(s) " +
                    $"(globalMultiplier={_globalLightIntensityMultiplier.Value:0.00}, " +
                    $"sunMultiplier={_sunIntensityMultiplier.Value:0.00}, sunWhiteBlend={_sunColorWhiteBlend.Value:0.00}), " +
                    $"fillLight={(fillActive ? _fillLightIntensity.Value.ToString("0.00") : "off")}, fogDensity={_fogDensity.Value:0.000}");
            }
        }

        // Returns whether the fill light is active. Created lazily inside our Scripts scene.
        static bool ApplyFillLight(float intensity)
        {
            if (_fillDown == null || _fillUp == null)
            {
                if (intensity <= 0f)
                {
                    return false;
                }
                var scripts = SceneManager.GetSceneByName(ArenaMapPort.Shared.ArenaEquatorLocation.ScriptsSceneName);
                if (!scripts.IsValid() || !scripts.isLoaded)
                {
                    return false;
                }
                var root = new GameObject(FillLightName);
                SceneManager.MoveGameObjectToScene(root, scripts);
                _fillDown = CreateFillLight(root, "Down", Quaternion.Euler(90f, 0f, 0f));
                _fillUp = CreateFillLight(root, "Up", Quaternion.Euler(-90f, 0f, 0f));
            }

            bool active = intensity > 0f;
            _fillDown.enabled = active;
            _fillUp.enabled = active;
            _fillDown.intensity = intensity;
            _fillUp.intensity = intensity * 0.5f;
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
        static bool ApplyAmbient(float m, float fogDensity)
        {
            var levelSettings = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
            if (levelSettings != null)
            {
                // The authored LevelSettings leaves fogMode at default(FogMode) = 0 and fogColor black,
                // and ApplySettings copies both into RenderSettings (LevelSettings.cs:206-211). The FPS
                // camera's GlobalFog image effect ignores RenderSettings.fog and computes its fog factor
                // only for modes 1-3, so mode 0 yields factor 0: every non-sky pixel becomes fogColor
                // (black). Patch the LevelSettings fields too, since OnPostLoadingScene re-applies them.
                levelSettings.fogMode = FogMode.ExponentialSquared;
                levelSettings.fogDensity = fogDensity;
                levelSettings.fogColor = FogColor;
                levelSettings.fog = fogDensity > 0f;
                ApplyFog(fogDensity);

                if (!_levelSettingsBaselineCaptured)
                {
                    _baseSkyColor = levelSettings.SkyColor;
                    _baseEquatorColor = levelSettings.EquatorColor;
                    _baseGroundColor = levelSettings.GroundColor;
                    _levelSettingsBaselineCaptured = true;
                }

                levelSettings.SkyColor = ScaleColor(_baseSkyColor, m);
                levelSettings.EquatorColor = ScaleColor(_baseEquatorColor, m);
                levelSettings.GroundColor = ScaleColor(_baseGroundColor, m);
                // LevelSettings.OnPreCullCallback (decompile, LevelSettings.cs:240-263) re-pushes these
                // exact fields into RenderSettings every camera pre-cull on its own — we only need to
                // update the fields it reads from. Fog is not re-pushed per frame, hence ApplyFog above.
                return true;
            }

            // Defensive fallback — belt-and-braces only, see class doc comment; keeps the raid from
            // being pitch black if LevelSettings is ever genuinely absent again (future scene change).
            _ambientFallbackWritten = true; // so OnSceneUnloaded knows to restore RenderSettings.ambient*
            RenderSettings.ambientMode = AmbientMode.Flat;
            var fallback = ScaleColor(new Color(0.5f, 0.5f, 0.5f, 1f), m);
            RenderSettings.ambientLight = fallback;
            RenderSettings.ambientSkyColor = fallback;
            RenderSettings.ambientEquatorColor = fallback;
            RenderSettings.ambientGroundColor = fallback;
            ApplyFog(fogDensity);
            Plugin.Log.LogWarning("[ArenaMapPort][Lighting] LevelSettings singleton not found at apply time — " +
                                   "wrote RenderSettings ambient directly as a fallback (should not happen; the " +
                                   "component is authored in AuthorScriptsScene.cs and was confirmed present in " +
                                   "the shipped bundle — see docs/progress/fix-lighting-movement.md).");
            return false;
        }

        static readonly Color FogColor = new Color(0.62f, 0.66f, 0.70f, 1f);

        static void ApplyFog(float density)
        {
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = density;
            RenderSettings.fogColor = FogColor;
            RenderSettings.fog = density > 0f;
        }

        static int ApplyLights(float globalMultiplier, float sunMultiplier, float sunWhiteBlend)
        {
            int applied = 0;
            for (int i = 0; i < _lights.Count; i++)
            {
                var baseline = _lights[i];
                if (baseline.Light == null)
                {
                    continue; // destroyed/unloaded since capture
                }

                if (baseline.Light.type == LightType.Directional)
                {
                    baseline.Light.intensity = baseline.Intensity * sunMultiplier;
                    baseline.Light.color = Color.Lerp(baseline.Color, Color.white, sunWhiteBlend);
                }
                else
                {
                    baseline.Light.intensity = baseline.Intensity * globalMultiplier;
                }
                applied++;
            }
            return applied;
        }

        static Color ScaleColor(Color c, float m)
        {
            return new Color(c.r * m, c.g * m, c.b * m, c.a);
        }
    }
}
