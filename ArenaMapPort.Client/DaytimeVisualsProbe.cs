using System;
using System.Collections;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// daytime-and-visuals, F7 — one consolidated, grep-able, once-per-raid log block covering F1-F6's
    /// post-fix state (docs/progress/daytime-and-visuals.md), so a single log read plus a single
    /// overview screenshot is enough to confirm the whole pass without cross-referencing six different
    /// files' own scattered log lines. Every line below is tagged
    /// "[ArenaMapPort][DaytimeVisualsProbe]" — <c>grep '\[DaytimeVisualsProbe\]' LogOutput.log</c> finds
    /// the whole block regardless of what else logged around it.
    ///
    /// Timed like <see cref="LightingDiagnosticProbe"/> (waits for <c>GameStatus.Started</c>, then a
    /// further delay) but with a longer post-Started margin — <see cref="DecalRepairs"/>' own sweep
    /// (4s after scene load) and <see cref="SeaPlaceholderRepairs"/>' own sweep (2s after scene load)
    /// both key off the SCENE LOAD, which happens well before <c>GameStatus.Started</c> (spawn +
    /// countdown), so by the time this probe's own post-Started delay elapses both have long finished —
    /// but the margin below is kept generous rather than tuned to the minimum.
    /// </summary>
    internal static class DaytimeVisualsProbe
    {
        const float DelaySecondsAfterStart = 3f;
        const float StartTimeoutSeconds = 180f;

        static bool _installed;
        static bool _ranThisRaid;
        static GameObject _host;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null || _ranThisRaid)
            {
                return;
            }

            if (_host == null)
            {
                _host = new GameObject("ArenaMapPort_DaytimeVisualsProbe") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(_host);
            }
            _host.AddComponent<Runner>().StartCoroutine(Runner.RunAfter());
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null)
            {
                return;
            }
            _ranThisRaid = false;
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }
            _host = null;
        }

        sealed class Runner : MonoBehaviour
        {
            internal static IEnumerator RunAfter()
            {
                float waited = 0f;
                while (waited < StartTimeoutSeconds)
                {
                    var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
                    if (game != null && game.Status == GameStatus.Started)
                    {
                        break;
                    }
                    yield return new WaitForSeconds(1f);
                    waited += 1f;
                }
                yield return new WaitForSeconds(DelaySecondsAfterStart);
                try
                {
                    RunProbe();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[ArenaMapPort][DaytimeVisualsProbe] probe threw: {e}");
                }
            }
        }

        static void RunProbe()
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || _ranThisRaid)
            {
                return;
            }
            _ranThisRaid = true;

            const string tag = "[ArenaMapPort][DaytimeVisualsProbe]";
            Plugin.Log.LogInfo($"{tag} ==== {map.Key} ====");

            ProbeTimeOfDay(tag, map);
            ProbeExposure(tag);
            ProbeTod(tag, map);
            ProbeDecals(tag);
            ProbeAmbient(tag);
            ProbeSea(tag);
            ProbeWeatherTestOverride(tag, map);
            ProbeRainCoverAndSSR(tag, map);
        }

        // F1 — TimeOfDayRepairs. "actual" is only meaningfully readable back via TODSkyProvider when a
        // WeatherRig is active (see LightingDiagnosticProbe.ProbeWeatherRig's own tripwire); otherwise
        // this line reports the CONFIGURED state only (there is no other in-game clock reading available
        // without a rig's TOD_Sky).
        static void ProbeTimeOfDay(string tag, ArenaMapPort.Shared.ArenaMapDescriptor map)
        {
            bool arenaMap = map.Key.StartsWith("arena_", StringComparison.Ordinal);
            var gdt = TODSkyProvider.IsAvailable ? TODSkyProvider.Instance.CurrentTime.GameDateTime : null;
            string liveHour = gdt != null ? gdt.Calculate().Hour.ToString("00") : "n/a (no rig's TOD_Sky to read back — see class doc comment)";
            Plugin.Log.LogInfo($"{tag} F1 TimeOfDay: enabled={TimeOfDayRepairs.Enabled} " +
                               $"legacyFixedHour={TimeOfDayRepairs.FixedHour} activeFixedHour={TimeOfDayRepairs.ActiveFixedHour} " +
                               $"fixedMenu={map.UsesFixedStartTimes} appliesToThisMap={arenaMap} liveHour={liveHour}");
        }

        // F2 — CameraEffectsRepairs exposure fix. Live readback off the active camera's own PrismEffects,
        // same resolution CameraEffectsRepairs itself uses.
        static void ProbeExposure(string tag)
        {
            var cam = ResolveActiveCamera();
            var prism = cam != null ? cam.GetComponent<PrismEffects>() : null;
            if (prism == null)
            {
                Plugin.Log.LogInfo($"{tag} F2 Exposure: fixEnabled={CameraEffectsRepairs.ExposureFixEnabled} " +
                                   "live=(no active camera / no PrismEffects found)");
                return;
            }
            Plugin.Log.LogInfo($"{tag} F2 Exposure: fixEnabled={CameraEffectsRepairs.ExposureFixEnabled} " +
                               $"useExposure(live)={prism.useExposure} middleGrey={prism.exposureMiddleGrey:0.000} " +
                               $"lower={prism.exposureLowerLimit:0.0} upper={prism.exposureUpperLimit:0.0}");
        }

        // F3 — CameraEffectsRepairs TOD_Camera/TOD_Scattering fix, plus Sun/dome world position (the
        // playtest's floating-black-square hypothesis this fix targets).
        static void ProbeTod(string tag, ArenaMapPort.Shared.ArenaMapDescriptor map)
        {
            bool rigActive = WeatherRig.IsActiveForCurrentMap();
            var cam = ResolveActiveCamera();
            var todCamera = cam != null ? cam.GetComponent<TOD_Camera>() : null;
            var scattering = cam != null ? cam.GetComponent<TOD_Scattering>() : null;
            Plugin.Log.LogInfo($"{tag} F3 TOD: rigActive={rigActive} " +
                               $"scatteringFixEnabled={CameraEffectsRepairs.TodScatteringFixEnabled} " +
                               $"TOD_Camera-present={todCamera != null} TOD_Camera-sky-set={(todCamera != null && todCamera.sky != null)} " +
                               $"TOD_Scattering-present={scattering != null} TOD_Scattering-sky-set={(scattering != null && scattering.Sky != null)}");

            if (!rigActive)
            {
                return; // no rig's TOD_Sky to read a dome/sun position off of
            }
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instantiated ? MonoBehaviourSingleton<TOD_Sky>.Instance : null;
            if (sky == null)
            {
                Plugin.Log.LogInfo($"{tag} F3 TOD: rigActive=True but MonoBehaviourSingleton<TOD_Sky>.Instance is null (unexpected)");
                return;
            }
            var domePos = sky.Components != null && sky.Components.DomeTransform != null
                ? sky.Components.DomeTransform.position.ToString()
                : "n/a";
            var sunLight = sky.LightObject;
            var sunPos = sunLight != null ? sunLight.transform.position.ToString() : "n/a (no LightObject)";
            var camPos = cam != null ? cam.transform.position.ToString() : "n/a (no active camera)";
            Plugin.Log.LogInfo($"{tag} F3 TOD: cameraWorldPos={camPos} domeWorldPos={domePos} sunWorldPos={sunPos}");

            // Does the sky's own clock follow the raid clock? 2026-09-17 14:54: GameDateTime said 12:00
            // while the sun sat on the horizon with Light.intensity 0.
            try
            {
                var time = sky.Components != null ? sky.Components.Time : null;
                var cycle = sky.Cycle;
                Plugin.Log.LogInfo(
                    $"{tag} TOD state: cycle={cycle.Year}-{cycle.Month:00}-{cycle.Day:00} hour={cycle.Hour:0.00} " +
                    $"lat={sky.World.Latitude:0.0} lon={sky.World.Longitude:0.0} utc={sky.World.UTC:0.0} " +
                    $"sunZenith={sky.SunZenith:0.0} lerp={sky.LerpValue:0.00} isDay={sky.IsDay} isNight={sky.IsNight} " +
                    $"fogginess={sky.Atmosphere.Fogginess:0.00} dayLightIntensity={sky.Day.LightIntensity:0.00} " +
                    $"light.intensity={(sunLight != null ? sunLight.intensity.ToString("0.00") : "n/a")} " +
                    $"light.enabled={(sunLight != null && sunLight.enabled)} light.shadows={(sunLight != null ? sunLight.shadows.ToString() : "n/a")} " +
                    // Must agree: TOD_Sky aims the light assuming the dome sits at the world origin.
                    $"light.forward={(sunLight != null ? sunLight.transform.forward.ToString("0.00") : "n/a")} " +
                    $"expected(-sunDirection)={(-sky.SunDirection).ToString("0.00")} " +
                    $"domeLocalScale={(sky.Components != null && sky.Components.DomeTransform != null ? sky.Components.DomeTransform.localScale.x.ToString("0") : "n/a")}");
                Plugin.Log.LogInfo(
                    $"{tag} TOD time: component={(time != null)} enabled={(time != null && time.isActiveAndEnabled)} " +
                    $"lockCurrentTime={(time != null && time.LockCurrentTime)} dayLengthMinutes={(time != null ? time.DayLengthInMinutes : 0f):0.0} " +
                    $"gameDateTime-set={(time != null && time.GameDateTime != null)} " +
                    $"gameDateTime-now={(time != null && time.GameDateTime != null ? time.GameDateTime.Calculate().ToString("HH:mm:ss") : "n/a")}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{tag} TOD state probe failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // F4 — DecalRepairs.
        static void ProbeDecals(string tag)
        {
            string count = DecalRepairs.LastDisabledCount >= 0 ? DecalRepairs.LastDisabledCount.ToString() : "n/a (pass hasn't run yet)";
            Plugin.Log.LogInfo($"{tag} F4 Decals: enabled={DecalRepairs.Enabled} disabledRendererCount={count}");
        }

        // F5 — ArenaLightingRepairs' ambient-color write gate.
        static void ProbeAmbient(string tag)
        {
            bool rigActive = WeatherRig.IsActiveForCurrentMap();
            Plugin.Log.LogInfo($"{tag} F5 Ambient: rigActive={rigActive} " +
                               $"ArenaLightingRepairs-ambientColorWriteSkipped={rigActive} " +
                               $"liveAmbientLight={RenderSettings.ambientLight}");

            // §18 — once-per-raid dump of the ToDController curve values feeding the analytic diffuse SH
            // (ToDController.cs:38-126). t/t01 are re-derived exactly as ToDController.Update computes
            // them (sun ELEVATION, not clock time — ToDController.cs:250,258) since neither is cached
            // outside that method's own local scope. Only meaningful with a rig active (no ToDController
            // otherwise — see §0's own Equator finding). Lets the user capture the identical line on a
            // vanilla map raid later (e.g. Reserve via a debug Donor) for a direct curve-value comparison —
            // see donor-settings-camera-weather.md §18.
            if (!rigActive)
            {
                return;
            }
            var rig = WeatherRig.ActiveRig;
            var tod = rig?.Controller?.TimeOfDayController;
            if (tod == null || rig.Sky == null)
            {
                return;
            }
            float y = rig.Sky.SunDirection.y;
            float t = y;
            float t01 = y * 0.5f + 0.5f;
            Plugin.Log.LogInfo($"{tag} §18 Curves @t={t:0.00} t01={t01:0.00}: " +
                $"Top={tod.TopHarmonicIntensity.Evaluate(t):0.00} Horizont={tod.HorizontHarmonicIntensity.Evaluate(t01):0.00} " +
                $"Bounce={tod.BounceHarmonicIntensity.Evaluate(t):0.00} Forwar={tod.ForwarHarmonicIntensity.Evaluate(t01):0.00} " +
                $"AmbientBrightness={tod.AmbientBrightness.Evaluate(t):0.00} AmbientContrast={tod.AmbientContrast.Evaluate(t):0.00} " +
                $"AmbientSaturation={tod.AmbientSaturation.Evaluate(t):0.00} EnvironmentColor={tod.EnvironmentColor} " +
                $"(HarmonicsDaylight={tod.HarmonicsDaylight:0.00} — always 0 in this decompile, so " +
                $"EnvironmentColor's own term is dead weight regardless of its value; see §18)");
        }

        // F6 — SeaPlaceholderRepairs.
        static void ProbeSea(string tag)
        {
            Plugin.Log.LogInfo($"{tag} F6 Sea: enabled={SeaPlaceholderRepairs.Enabled} " +
                               $"lastResult={SeaPlaceholderRepairs.LastResult ?? "n/a (pass hasn't run yet)"}");
        }

        // donor-settings-camera-weather §D — WeatherTestOverride's currently configured mode. The
        // Postfix itself logs "[WeatherTestOverride] applied '<mode>'" (expected twice per raid when
        // active — once from InitWeather, once from SPT's PVE_OFFLINE server re-sync); this line just
        // confirms what was CONFIGURED for this raid, for a single-log-read summary.
        static void ProbeWeatherTestOverride(string tag, ArenaMapPort.Shared.ArenaMapDescriptor map)
        {
            Plugin.Log.LogInfo($"{tag} WeatherTestOverride: configured='{WeatherTestOverride.Mode}' (map={map.Key}) " +
                               "— Server means no change; any other value should show two " +
                               "'[WeatherTestOverride] applied' lines above for this raid");
        }

        // §12 goal 4 — SSR/rain-cover diagnostics. CameraManager.GetSSREnabled() reads back EFT's own
        // PostProcessing-v2 ScreenSpaceReflections toggle (EFT's Graphics-options "SSR" dropdown,
        // CameraManager.cs:974-985); AmbientLight.IsSSREnable reads the SAME toggle
        // (CameraManager.Instance.GetSSREnabled(), AmbientLight.cs:225-235) and, per AmbientLight.method_3
        // (AmbientLight.cs:434-438), pins the "Custom Ambient" screen-space reflection compositing term to
        // 1.0 whenever it's on, independent of wetness — well-documented Unity PPv2 SSR behavior
        // (raymarches against the depth/color buffer of what's actually rendered), corroborated by but not
        // directly provable from this decompile beyond that corroboration. If PostFxSSR is false, the real
        // scene never feeds indoor reflections — see docs/progress/donor-settings-camera-weather.md §12
        // for the "enable SSR in EFT's graphics settings" guidance this line exists to support.
        static void ProbeRainCoverAndSSR(string tag, ArenaMapPort.Shared.ArenaMapDescriptor map)
        {
            bool ppSSR = false;
            try
            {
                ppSSR = CameraManager.Instance != null && CameraManager.Instance.GetSSREnabled();
            }
            catch
            {
                // CameraManager mid-teardown — leave ppSSR false, same defensive convention ResolveActiveCamera uses
            }

            var ambient = WeatherRig.ActiveRig?.Controller?.TimeOfDayController?.AmbientLightScript; // same accessor LightingLab.cs uses
            bool ambientIsSSR = ambient != null && ambient.IsSSREnable;

            var ls = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
            string rainBounds = ls != null ? $"center={ls.RainBounds.center} size={ls.RainBounds.size}" : "n/a";
            // CACHED — DepthPhotograper._depthCamera is long destroyed by the time this once-per-raid probe
            // runs (see RainCoverRepairs.LastAppliedCullingMaskForCurrentMap's own doc comment).
            int mask = RainCoverRepairs.LastAppliedCullingMaskForCurrentMap();

            Plugin.Log.LogInfo($"{tag} RainCover/SSR: RainCover={WeatherRig.RainCoverForCurrentMap()} " +
                               $"RainBounds=({rainBounds}) depthCameraCullingMask=0x{mask:X} " +
                               $"PostFxSSR(CameraManager.GetSSREnabled)={ppSSR} AmbientLight.IsSSREnable={ambientIsSSR} " +
                               $"RenderSettings: ambientMode={RenderSettings.ambientMode} " +
                               $"ambientLight={RenderSettings.ambientLight} ambientIntensity={RenderSettings.ambientIntensity:0.00} " +
                               $"ambientEquatorColor={RenderSettings.ambientEquatorColor} " +
                               $"ambientGroundColor={RenderSettings.ambientGroundColor}");
        }

        // Same resolution CameraEffectsRepairs/LightingDiagnosticProbe use — CameraManager.Instance.Camera,
        // falling back to Camera.main if that throws or is unset.
        static Camera ResolveActiveCamera()
        {
            Camera cam = null;
            try
            {
                cam = CameraManager.Instance?.Camera;
            }
            catch
            {
                // fall through to Camera.main
            }
            return cam != null ? cam : Camera.main;
        }
    }
}
