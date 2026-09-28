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
    ///
    /// PLAN-TWO-MAPS light-culling item — a fourth, independent fix in the same
    /// <see cref="OnCameraChanged"/> path: <c>VolumetricLightRenderer</c> (decompile
    /// D:/SPT41/Development/_decompiled/VolumetricLightRenderer.cs), the <c>[RequireComponent(typeof
    /// (Camera))]</c> companion that actually draws every scene's <c>VolumetricLight</c> components
    /// (Equator 17, Bay5 1, per the extractor's manifest counts — light-shaft/god-ray cones on a handful
    /// of spot/point lights). Vanilla ships it pre-attached on the real camera prefab (per this repair
    /// file's own header: our maps author no <c>LevelSettings.CameraPrefab</c>, so
    /// <c>CameraManager.SetCameraFromSettings</c> always falls back to the bare "Cam2" prefab, which
    /// never carries this component either) — without it, every scene's <c>VolumetricLight</c> instances
    /// stay fully constructed (their own <c>Awake</c>/<c>OnEnable</c> succeed and self-register with
    /// <c>VolumetricLightRenderer.AddLight</c>) but nothing ever calls
    /// <c>VolumetricLightRenderer.OnPreRender</c> to iterate <c>_lights</c> and drive
    /// <c>VolumetricLightPreRender</c> on any of them, so the cones never render at all — an inert
    /// feature, not a crash, exactly like the CullingManager gap this same package's client fix in
    /// EnvironmentRepairs.cs addresses for the sibling <c>CullingLightObject</c> components.
    ///
    /// Self-destroys harmlessly when unwanted: both <c>VolumetricLight.Awake</c> (VolumetricLight.cs) and
    /// <c>VolumetricLightRenderer.Awake</c> (VolumetricLightRenderer.cs) open with the identical guard —
    /// <c>bool flag = instance != null &amp;&amp; !instance.Graphics.Settings.VolumetricLight.Value; if
    /// (Application.isPlaying &amp;&amp; flag) { GraphicsHelper.SafeDestroy(this); return; }</c> — reading
    /// the player's own Graphics settings via <c>Singleton&lt;SettingsManager&gt;.Instance</c>, so a
    /// player with the in-game "Volumetric Light" option off gets the component added and then
    /// immediately removed again next frame, same as on a vanilla map; this fix does not need to read
    /// that setting itself. <c>VolumetricLightRenderer.Awake</c> otherwise only resolves its own shaders
    /// via <c>ShadersFinder.Find</c> (all in <c>ShadersFinder.SHADERS</c>'s pre-registered list, same
    /// belt-and-braces argument F3 above already makes for <c>TOD_Scattering</c>'s shader), builds its
    /// point/spot meshes procedurally, and reads <c>CameraManager.Instance.OpticCameraManager.
    /// OpticRenderResolution</c> unconditionally (even though the optic render textures it feeds are only
    /// swapped in when <c>IsOptic</c> is true, which we never set — so this fallback always takes the
    /// default non-optic branch, <c>CreateVolumetricRenderTextures()</c>, sized from the camera's own
    /// pixel dimensions); by the time <see cref="OnCameraChanged"/> fires, <c>CameraManager.Instance</c>
    /// is already known live (this method only reaches this fix after <c>CameraManager.Instance?.Camera</c>
    /// resolved non-null above), but <c>OpticCameraManager</c> itself is unverified against the decompile
    /// for this pass — hence the try/catch and log line below rather than assuming it can never throw.
    /// Deliberately NOT nested inside the <see cref="WeatherRig.IsActiveForCurrentMap"/> gate that
    /// <c>ApplyTodFixes</c> uses for TOD_Scattering: unlike TOD_Scattering (which needs a live TOD_Sky to
    /// point at), this fix has no WeatherRig dependency at all — it only enables the camera side of a
    /// feature whose scene-side components (<c>VolumetricLight</c>) exist on every map regardless of
    /// whether that map's package includes a weather rig, so gating it on rig presence would silently
    /// drop the fix on any future map extracted without one.
    /// </summary>
    internal static class CameraEffectsRepairs
    {
        static ConfigEntry<bool> _fixExposure;
        static ConfigEntry<float> _exposureMiddleGrey;
        static ConfigEntry<float> _exposureLowerLimit;
        static ConfigEntry<float> _exposureUpperLimit;
        static ConfigEntry<bool> _enableTodScatteringFix;
        static ConfigEntry<bool> _enableVolumetricLightRendererFix;
        static bool _installed;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _fixExposure = Plugin.Instance.Config.Bind("Camera", "FixExposure", true,
                "Repairs missing automatic exposure while a donor weather rig is active. Only applies " +
                "when the camera's exposure is off; cameras with exposure already enabled retain their " +
                "existing values. This is a fallback repair, not an exposure on/off switch. Next camera setup.");

            _exposureMiddleGrey = Plugin.Instance.Config.Bind("Camera", "ExposureMiddleGrey", 0.23f,
                "Middle grey used only when FixExposure repairs a camera with exposure off. " +
                "Does not override an already active donor exposure. Next camera setup.");
            _exposureLowerLimit = Plugin.Instance.Config.Bind("Camera", "ExposureLowerLimit", -10.0f,
                "Lower limit used only when FixExposure repairs a camera with exposure off. " +
                "Does not override an already active donor exposure. Next camera setup.");
            _exposureUpperLimit = Plugin.Instance.Config.Bind("Camera", "ExposureUpperLimit", 1.3f,
                "Upper limit used only when FixExposure repairs a camera with exposure off. " +
                "Does not override an already active donor exposure. Next camera setup.");

            _enableTodScatteringFix = Plugin.Instance.Config.Bind("Camera", "EnableTODScatteringFix", true,
                "Adds a TOD_Scattering component to the fallback camera while a WeatherRig is active for " +
                "this map (atmospheric scattering / aerial perspective). Ships with FromLevelSettings=true " +
                "unchanged this pass — reads our own map's authored LevelSettings.HeightFalloff/ZeroLevel.");

            _enableVolumetricLightRendererFix = Plugin.Instance.Config.Bind("Camera", "EnableVolumetricLightRendererFix", true,
                "Adds a VolumetricLightRenderer component to the fallback camera so the scene's own " +
                "VolumetricLight components (light-shaft/god-ray cones on a few Equator/Bay5 lights) " +
                "actually render, the way a real camera prefab already carries one on vanilla maps. " +
                "VolumetricLight/VolumetricLightRenderer both self-destroy on their own next frame if the " +
                "player's Graphics > Volumetric Light setting is off, same as on a vanilla map — this key " +
                "does not need to read that setting itself. Takes effect on the next camera change.");

            // CameraRepairHooks dispatches from SetCamera on every manager instance.
        }

        // CameraManager.OnCameraChanged is a plain event Action (CameraManager.cs:262) — no camera
        // reference is passed, matching WeatherController.OnCameraChanged's own signature this plugin
        // already subscribes elsewhere (WeatherRig.cs).
        internal static void OnCameraChanged()
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
            ApplyVolumetricLightRendererFix(cam);
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
                // donor-settings-camera-weather §B.4 — this early-return already existed pre-donor-camera;
                // it now also covers a donor camera's own baked useExposure=true (PrismPresetTakeover, §B),
                // which is exactly the "no double-apply" guard §B.4 requires. Tagged so a log read can tell
                // the two paths apart.
                string source = WeatherRig.UseDonorCameraPrefabForCurrentMap() && WeatherRig.ActiveRig?.CameraPrefab != null
                    ? "donor" : "fallback (already fixed for this camera)";
                Plugin.Log.LogInfo($"[ArenaMapPort][Camera] PrismEffects.useExposure already true on '{cam.name}' " +
                                   $"(middleGrey={prism.exposureMiddleGrey:0.000} lower={prism.exposureLowerLimit:0.0} " +
                                   $"upper={prism.exposureUpperLimit:0.0}) — source={source}, no changes made");
                return; // already on (real camera prefab, or this fix already ran for this camera)
            }

            prism.useExposure = true;
            prism.exposureMiddleGrey = _exposureMiddleGrey.Value;
            prism.exposureLowerLimit = _exposureLowerLimit.Value;
            prism.exposureUpperLimit = _exposureUpperLimit.Value;
            Plugin.Log.LogInfo($"[ArenaMapPort][Camera] enabled PrismEffects.useExposure on '{cam.name}' " +
                               $"(middleGrey={prism.exposureMiddleGrey:0.000} lower={prism.exposureLowerLimit:0.0} " +
                               $"upper={prism.exposureUpperLimit:0.0}) — daytime-and-visuals F2, source=fallback (added)");
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

            // No TOD_Camera: BSG's TOD_Sky aims the sun light with
            // Quaternion.LookRotation(DomeTransform.position - <unit sun vector>) (TOD_Sky.cs:1097/1103),
            // which is only correct while the dome sits at the world origin, as it does on vanilla maps
            // (Reserve: Sky Dome at (0,0,0), scale 1000). TOD_Camera moves the dome onto the camera, which
            // turned the sun into a horizontal light along the camera's position vector (2026-09-17:
            // forward=(0.99,-0.06,-0.12) at camera (52.7,-2.3,-6.1) while SunZenith was 35 degrees) —
            // blown-out sunlit surfaces and glare on the ground. Remove one a previous build added.
            var todCamera = cam.GetComponent<TOD_Camera>();
            if (todCamera != null)
            {
                // donor-settings-camera-weather §B.4 (critic point 3, verified): the donor camera itself
                // carries no TOD_Camera component (cross-referenced against all 59 resolved donor-camera
                // components), so this unconditional destroy is a safe no-op on the donor path too —
                // logged here so that verification is visible in a real playtest's log, not just this
                // comment.
                UnityEngine.Object.Destroy(todCamera);
                Plugin.Log.LogInfo($"[ArenaMapPort][Camera] destroyed stray TOD_Camera on '{cam.name}' " +
                                   "(donor camera never carries one — should only fire on the Cam2 fallback path)");
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
                                       "daytime-and-visuals F3, source=fallback (added)");
                }
                else
                {
                    // donor-settings-camera-weather §B.4 — the donor camera ships TOD_Scattering already
                    // enabled with its own tuned FromLevelSettings/GlobalDensity/etc fields; skip the add
                    // entirely and leave those baked values untouched, just re-point Sky at this raid's
                    // rig (harmless even on a second activation of the same camera).
                    Plugin.Log.LogInfo($"[ArenaMapPort][Camera] TOD_Scattering already present on '{cam.name}' " +
                                       "— daytime-and-visuals F3, source=donor (already present, no changes made)");
                }
            }
        }

        // PLAN-TWO-MAPS light-culling item — see class doc comment for why this is independent of the
        // WeatherRig gate ApplyTodFixes uses, and why VolumetricLight/VolumetricLightRenderer's own
        // Graphics-setting self-destroy means no config value here needs to read that setting.
        static Camera _volumetricRendererLoggedCamera;

        static void ApplyVolumetricLightRendererFix(Camera cam)
        {
            if (_enableVolumetricLightRendererFix == null || !_enableVolumetricLightRendererFix.Value)
            {
                return;
            }
            if (cam.GetComponent<VolumetricLightRenderer>() != null)
            {
                // Already present: a previous camera change added it, or the donor camera prefab (vanilla Cam2*
                // ships one baked in) brought it along. Logged once per camera so the host log carries the evidence.
                if (_volumetricRendererLoggedCamera != cam)
                {
                    _volumetricRendererLoggedCamera = cam;
                    Plugin.Log.LogInfo($"[ArenaMapPort][Camera] VolumetricLightRenderer already present on '{cam.name}' — no changes made");
                }
                return;
            }

            try
            {
                cam.gameObject.AddComponent<VolumetricLightRenderer>();
                Plugin.Log.LogInfo($"[ArenaMapPort][Camera] added VolumetricLightRenderer to '{cam.name}' " +
                                    "so this map's VolumetricLight components (light-shaft/god-ray cones) render " +
                                    "— self-destroys next frame if the player's Graphics > Volumetric Light " +
                                    "setting is off, same as a vanilla camera prefab.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][Camera] AddComponent<VolumetricLightRenderer> on " +
                                       $"'{cam.name}' threw: {e.GetType().Name}: {e.Message} — volumetric light " +
                                       "cones stay unrendered on this camera, everything else in this plugin " +
                                       "keeps working.");
            }
        }

        // Exposed for DaytimeVisualsProbe's consolidated F1-F6 summary line.
        internal static bool ExposureFixEnabled => _fixExposure != null && _fixExposure.Value;
        internal static bool TodScatteringFixEnabled => _enableTodScatteringFix != null && _enableTodScatteringFix.Value;
        internal static bool VolumetricLightRendererFixEnabled => _enableVolumetricLightRendererFix != null && _enableVolumetricLightRendererFix.Value;
    }
}
