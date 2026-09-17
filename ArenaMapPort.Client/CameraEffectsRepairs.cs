using System;
using BepInEx.Configuration;
using EFT.CameraControl;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// daytime-and-visuals, F2 + F3 — 2026-09-17 playtest symptom B, two of its several causes, both
    /// traced to the same root: our scenes have no <c>LevelSettings.CameraPrefab</c> (SPEC §D), so
    /// <c>CameraManager.SetCameraFromSettings</c> (EFT.CameraControl/CameraManager.cs:322-338) always
    /// falls through to the fallback camera ("Cam2", <c>SetCameraFromPrefab()</c> with no args) — which
    /// never gets <c>SetPrismPreset</c>/<c>SetPostProcessProfile</c> called on it
    /// (CameraManager.cs:322-360: both are only called when a <c>CameraPrefab</c> supplies a preset),
    /// and predates <see cref="global::TOD_Camera"/>/<see cref="global::TOD_Scattering"/> entirely.
    /// Both fixes hook <see cref="CameraManager.OnCameraChanged"/> — the same event
    /// <see cref="WeatherRig"/> already uses — since that fires once per real camera swap (raid start,
    /// spectate, death cam), well after the raid's Scripts scene has loaded and any weather rig has
    /// already activated, so no install-order hazard applies here (unlike the
    /// SceneManager.sceneLoaded-based repairs elsewhere in this plugin, which do have one — see
    /// WeatherRig.OnSceneLoaded's own doc comment).
    ///
    /// F2 (exposure): live-log-confirmed at LogOutput.log:1550 — Cam2's own baked PrismEffects preset
    /// has <c>useExposure=False</c> with plausible non-zero exposure fields already set
    /// (<c>exposureMiddleGrey=0.230 exposureLowerLimit=-10.0 exposureUpperLimit=1.3</c>). Flipping the
    /// flag is self-contained: <c>PrismEffects.HandleExposure</c> (PrismEffects.cs:1766-1786) only takes
    /// its "adapt from a master effect" branch when <c>m_MasterEffectExposure != null</c> — the expected
    /// (unset) state on Cam2, confirmed via the same log line's absence of that field ever being wired —
    /// so no extra plumbing is needed, just the flag plus the config-tunable exposure fields pre-seeded
    /// from Cam2's own baked values rather than PrismEffects' generic field defaults (0.12/-6/6).
    ///
    /// F3 (TOD_Camera/TOD_Scattering): live-log-confirmed absent at LogOutput.log:1564 ("FPS camera
    /// TOD_Scattering present=False Sky-set=False"). Gated on <see cref="WeatherRig.IsActiveForCurrentMap"/>
    /// — no rig means no TOD_Sky to wire either component onto. <c>TOD_Camera</c>'s own
    /// <c>OnPreCull</c> (TOD_Camera.cs:64-71) re-centers/rescales the sky dome onto this camera every
    /// frame (<c>DoDomePosToCamera</c>/<c>DoDomeScaleToFarClip</c>) — the leading hypothesis for the
    /// playtest's floating black square (a dome stranded at whatever position the donor scene's own
    /// camera last left it at). <c>TOD_Scattering</c> ships with <c>FromLevelSettings=true</c>
    /// unchanged this pass (TOD_Scattering.cs:165-172 reads <c>Singleton&lt;LevelSettings&gt;
    /// .Instance.HeightFalloff/.ZeroLevel</c> — our own map's authored LevelSettings, not the rig's;
    /// not hand-tuned blind). Its shader (<c>"Hidden/Time of Day/Scattering"</c>) is one of
    /// <c>ShadersFinder.SHADERS</c>' own pre-registered entries (ShadersFinder.cs) — a real, expected
    /// game shader — and <c>TOD_ImageEffect.CreateMaterial</c> (TOD_ImageEffect.cs:27-40) degrades
    /// gracefully (disables itself, does not throw) if it were ever missing, so adding the component is
    /// low-risk even in the worst case.
    ///
    /// Both wire <c>Sky</c>/<c>sky</c> explicitly to <c>MonoBehaviourSingleton&lt;TOD_Sky&gt;.Instance</c>
    /// rather than relying on each component's own self-resolving fallback (<c>TOD_Camera.OnEnable</c>/
    /// <c>TOD_ImageEffect.Awake</c> both already do <c>FindObjectOfType&lt;TOD_Sky&gt;</c> if unset) —
    /// belt-and-braces only, and cheap since <see cref="WeatherRig.IsActiveForCurrentMap"/> already
    /// guarantees the singleton is live and is this raid's own rig's sky
    /// (WeatherRig.ReestablishSingletons keeps it current every activation).
    /// </summary>
    internal static class CameraEffectsRepairs
    {
        static ConfigEntry<bool> _fixExposure;
        static ConfigEntry<float> _exposureMiddleGrey;
        static ConfigEntry<float> _exposureLowerLimit;
        static ConfigEntry<float> _exposureUpperLimit;
        static ConfigEntry<bool> _enableTodCameraFix;
        static ConfigEntry<bool> _enableTodScatteringFix;
        static bool _installed;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _fixExposure = Plugin.Instance.Config.Bind("Camera", "FixExposure", true,
                "Enables PrismEffects.useExposure on the fallback camera (Cam2), which never gets its " +
                "exposure preset applied because our maps have no LevelSettings.CameraPrefab. Cam2's own " +
                "baked exposure fields (below) are used, not left at the generic engine defaults.");

            _exposureMiddleGrey = Plugin.Instance.Config.Bind("Camera", "ExposureMiddleGrey", 0.23f,
                "PrismEffects.exposureMiddleGrey applied when FixExposure=true. Pre-seeded from Cam2's own " +
                "baked value (LogOutput.log-confirmed 2026-09-17).");
            _exposureLowerLimit = Plugin.Instance.Config.Bind("Camera", "ExposureLowerLimit", -10.0f,
                "PrismEffects.exposureLowerLimit applied when FixExposure=true. Pre-seeded from Cam2's own " +
                "baked value.");
            _exposureUpperLimit = Plugin.Instance.Config.Bind("Camera", "ExposureUpperLimit", 4.0f,
                "PrismEffects.exposureUpperLimit applied when FixExposure=true. Cam2's own baked value was " +
                "1.3 (script/class default is 6) — a severe, confirmed ceiling on how far auto-exposure " +
                "can darken a bright frame (light-levels F3: ~0.093 minimum multiplier at 1.3 vs. ~0.0019 " +
                "at the script default — roughly 50x less headroom). Raised to 4.0 as the first test value. " +
                "Only takes effect on a fresh raid load: ApplyExposureFix early-outs once " +
                "prism.useExposure=true for a given camera, and this file has no SettingChanged wiring " +
                "(unlike ArenaLightingRepairs.cs), so changing this via F12 mid-raid does NOT apply live " +
                "(adversarial review) — reload the raid after changing it.");

            _enableTodCameraFix = Plugin.Instance.Config.Bind("Camera", "EnableTODCameraFix", true,
                "Adds a TOD_Camera component to the fallback camera while a WeatherRig is active for this " +
                "map, wired to the rig's own TOD_Sky. Re-centers/rescales the sky dome onto the camera " +
                "every frame — leading hypothesis for a dome/quad stranded at a stale position.");
            _enableTodScatteringFix = Plugin.Instance.Config.Bind("Camera", "EnableTODScatteringFix", true,
                "Adds a TOD_Scattering component to the fallback camera while a WeatherRig is active for " +
                "this map (atmospheric scattering / aerial perspective). Ships with FromLevelSettings=true " +
                "unchanged this pass — reads our own map's authored LevelSettings.HeightFalloff/ZeroLevel.");

            CameraManager.Instance.OnCameraChanged += OnCameraChanged;
        }

        // CameraManager.OnCameraChanged is a plain event Action (CameraManager.cs:262) — no camera
        // reference is passed, matching WeatherController.OnCameraChanged's own signature this plugin
        // already subscribes elsewhere (WeatherRig.cs).
        static void OnCameraChanged()
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return; // every other map's raid — real camera prefabs ship their own preset, untouched
            }

            Camera cam = null;
            try
            {
                cam = CameraManager.Instance?.Camera;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][Camera] CameraManager.Instance.Camera threw: " +
                                       $"{e.GetType().Name}: {e.Message}");
            }
            if (cam == null)
            {
                return;
            }

            ApplyExposureFix(cam);
            ApplyTodFixes(cam);
        }

        static void ApplyExposureFix(Camera cam)
        {
            // Only with a harvested weather rig: the fill-light workaround maps (e.g. Equator) were tuned
            // without auto-exposure and must keep their look.
            if (_fixExposure == null || !_fixExposure.Value || !WeatherRig.IsActiveForCurrentMap())
            {
                return;
            }
            var prism = cam.GetComponent<PrismEffects>();
            if (prism == null)
            {
                return; // Cam2 always carries one; defensive only, should not happen
            }
            if (prism.useExposure)
            {
                return; // already on (real camera prefab, or this fix already ran for this camera)
            }

            prism.useExposure = true;
            prism.exposureMiddleGrey = _exposureMiddleGrey.Value;
            prism.exposureLowerLimit = _exposureLowerLimit.Value;
            prism.exposureUpperLimit = _exposureUpperLimit.Value;
            Plugin.Log.LogInfo($"[ArenaMapPort][Camera] enabled PrismEffects.useExposure on '{cam.name}' " +
                               $"(middleGrey={prism.exposureMiddleGrey:0.000} lower={prism.exposureLowerLimit:0.0} " +
                               $"upper={prism.exposureUpperLimit:0.0}) — daytime-and-visuals F2");
        }

        static void ApplyTodFixes(Camera cam)
        {
            if (!WeatherRig.IsActiveForCurrentMap())
            {
                return; // no rig, no TOD_Sky to wire onto — nothing to fix
            }
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instantiated ? MonoBehaviourSingleton<TOD_Sky>.Instance : null;
            if (sky == null)
            {
                return; // defensive — should not happen while a rig reports active, see WeatherRig.ReestablishSingletons
            }

            if (_enableTodCameraFix != null && _enableTodCameraFix.Value)
            {
                var todCamera = cam.GetComponent<TOD_Camera>();
                bool added = todCamera == null;
                if (added)
                {
                    todCamera = cam.gameObject.AddComponent<TOD_Camera>();
                }
                todCamera.sky = sky;
                if (added)
                {
                    Plugin.Log.LogInfo($"[ArenaMapPort][Camera] added TOD_Camera to '{cam.name}', wired to the " +
                                       "active rig's TOD_Sky — daytime-and-visuals F3");
                }
            }

            if (_enableTodScatteringFix != null && _enableTodScatteringFix.Value)
            {
                var scattering = cam.GetComponent<TOD_Scattering>();
                bool added = scattering == null;
                if (added)
                {
                    scattering = cam.gameObject.AddComponent<TOD_Scattering>();
                }
                scattering.Sky = sky;
                if (added)
                {
                    Plugin.Log.LogInfo($"[ArenaMapPort][Camera] added TOD_Scattering to '{cam.name}' " +
                                       "(FromLevelSettings=true), wired to the active rig's TOD_Sky — " +
                                       "daytime-and-visuals F3");
                }
            }
        }

        // Exposed for DaytimeVisualsProbe's consolidated F1-F6 summary line.
        internal static bool ExposureFixEnabled => _fixExposure != null && _fixExposure.Value;
        internal static bool TodCameraFixEnabled => _enableTodCameraFix != null && _enableTodCameraFix.Value;
        internal static bool TodScatteringFixEnabled => _enableTodScatteringFix != null && _enableTodScatteringFix.Value;
    }
}
