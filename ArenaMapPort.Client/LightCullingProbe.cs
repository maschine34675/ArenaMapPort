using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Comfort.Common;
using EFT.CameraControl;
using EFT.Settings;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// PLAN-TWO-MAPS item 7 diagnostic (no repair). Vanilla: <c>CullingLightObject</c> fades each Light's intensity
    /// with the camera distance between <c>_fadeStartDistance</c> and <c>_fadeEndDistance</c> (CullingManager drives
    /// it, <c>_light.intensity = float_1 * multiplier</c>), <c>VolumetricLight</c> draws god-rays through the camera's
    /// <c>VolumetricLightRenderer</c>, and <c>LightFlicker</c> animates intensity through FlickerSystem. Since the v4
    /// packages the ported scenes carry all three; the first host test (2026-09-26) proved the components are live
    /// (Lighting summary counts) but screenshots could not settle the distance fade, the god-rays or the flicker.
    /// This probe logs them as numbers: on <c>Diagnostics › LightCullingProbeHotkey</c> (F7) it groups every
    /// CullingLightObject of the loaded Arena map into "inside fade start", "inside the fade window" and "beyond fade
    /// end" by its distance to the active camera and reports how many sit at full / partial / zero intensity, lists
    /// the nearest few, reports the VolumetricLight state (graphics setting, live components, renderer on the camera)
    /// and samples each enabled LightFlicker's intensity ten times over one second. Since 2026-09-27 it also logs the
    /// active camera's image-effect stack and PrismEffects' bloom/exposure values (floodlight glare comparison).
    /// Only in an Arena raid; reads only.
    /// </summary>
    internal static class LightCullingProbe
    {
        static ConfigEntry<KeyboardShortcut> _hotkey;
        static FieldInfo _float1;
        static bool _float1Resolved;
        static bool _running;

        internal static void Install()
        {
            _hotkey = Plugin.Instance.Settings.Bind("Diagnostics", "LightCullingProbeHotkey", new KeyboardShortcut(KeyCode.F7),
                "PLAN-TWO-MAPS item 7 diagnostic: logs the distance fade of every culling light of the loaded Arena map " +
                "(inside fade start / fade window / beyond fade end vs. full / partial / zero intensity), the nearest lights, " +
                "the volumetric-light state and a one-second flicker sample. Read-only, only in an Arena raid. Takes effect at once.");
            if (!DeveloperOptions.Enabled) return;
            Plugin.Instance.gameObject.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            void Update()
            {
                try
                {
                    if (!_running && _hotkey != null && _hotkey.Value.IsDown() && ArenaMapRaid.IsLoaded())
                    {
                        StartCoroutine(Probe());
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[LightProbe] hotkey Update threw: {ex}");
                }
            }
        }

        static float Float1(CullingLightObject clo)
        {
            if (!_float1Resolved)
            {
                _float1Resolved = true;
                _float1 = typeof(CullingLightObject).GetField("float_1", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_float1 == null)
                    Plugin.Log.LogWarning("[LightProbe] CullingLightObject.float_1 not found via reflection — reporting NaN");
            }
            return _float1 != null ? (float)_float1.GetValue(clo) : float.NaN;
        }

        static Camera ResolveActiveCamera()
        {
            try
            {
                Camera c = CameraManager.Instance?.Camera;
                if (c != null) return c;
            }
            catch { /* CameraManager mid-teardown — fall through */ }
            return Camera.main;
        }

        static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        static IEnumerator Probe()
        {
            _running = true;
            var map = ArenaMapRaid.CurrentMap;
            Camera cam = ResolveActiveCamera();
            if (map == null || cam == null)
            {
                Plugin.Log.LogDebug("[LightProbe] no Arena map or no active camera — skipped");
                _running = false;
                yield break;
            }
            Vector3 camPos = cam.transform.position;
            EquatorNightNeon.Probe();
            NativeMultiFlare.Probe();
            LightingModCompatibility.Probe();
            EquatorRoofShadowRepairs.Probe();
            var lights = new List<(CullingLightObject clo, Light light, float dist, bool flicker)>();
            foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
            {
                if (clo == null || !ArenaMapRaid.IsOwnedScene(clo.gameObject.scene.name)) continue;
                var light = clo.GetComponent<Light>();
                if (light == null) continue;
                var flicker = clo.GetComponent<EFT.Visual.LightFlicker>();
                lights.Add((clo, light, Vector3.Distance(camPos, light.transform.position), flicker != null && flicker.enabled));
            }

            int[] inside = new int[3], window = new int[3], beyond = new int[3]; // [full, partial, zero]
            int visible = 0;
            foreach (var l in lights)
            {
                if (l.flicker) continue; // intensity comes from the flicker formula, reported below
                if (l.clo.IsVisible) visible++;
                float target = Float1(l.clo);
                float ratio = target > 0.0001f ? l.light.intensity / target : (l.light.intensity > 0.0001f ? 1f : 0f);
                int bucket = ratio >= 0.99f ? 0 : (ratio <= 0.01f ? 2 : 1);
                if (l.dist < l.clo._fadeStartDistance) inside[bucket]++;
                else if (l.dist <= l.clo._fadeEndDistance) window[bucket]++;
                else beyond[bucket]++;
            }
            var sb = new StringBuilder();
            sb.Append($"[LightProbe] {map.Key} camera=({F(camPos.x)}, {F(camPos.y)}, {F(camPos.z)}) " +
                      $"CullingManager={(CullingManager.Instance != null)} cullingLights={lights.Count} visible={visible} | " +
                      $"inside fade start: full={inside[0]} partial={inside[1]} zero={inside[2]} | " +
                      $"fade window: full={window[0]} partial={window[1]} zero={window[2]} | " +
                      $"beyond fade end: full={beyond[0]} partial={beyond[1]} zero={beyond[2]}");
            lights.Sort((a, b) => a.dist.CompareTo(b.dist));
            for (int i = 0; i < Math.Min(8, lights.Count); i++)
            {
                var l = lights[i];
                sb.Append($"\n  '{l.clo.gameObject.name}' d={F(l.dist)} fade={F(l.clo._fadeStartDistance)}..{F(l.clo._fadeEndDistance)} " +
                          $"visible={l.clo.IsVisible} multiplier={F(l.clo.CalculateMultiplier())} float_1={F(Float1(l.clo))} " +
                          $"intensity={F(l.light.intensity)} enabled={l.light.enabled} flicker={l.flicker}");
            }
            Plugin.Log.LogDebug(sb.ToString());

            // Volumetric light: the player's graphics setting, the live components (they self-destroy when it is off)
            // and the renderer on the active camera.
            bool? setting = null;
            try { setting = Singleton<SettingsManager>.Instance?.Graphics.Settings.VolumetricLight.Value; }
            catch (Exception ex) { Plugin.Log.LogWarning($"[LightProbe] reading the VolumetricLight setting threw: {ex.GetType().Name}"); }
            int vlTotal = 0, vlActive = 0;
            var vlNames = new List<string>();
            foreach (var vl in UnityEngine.Object.FindObjectsOfType<VolumetricLight>())
            {
                if (vl == null || !ArenaMapRaid.IsOwnedScene(vl.gameObject.scene.name)) continue;
                vlTotal++;
                if (vl.isActiveAndEnabled) { vlActive++; if (vlNames.Count < 4) vlNames.Add(vl.gameObject.name); }
            }
            var renderer = cam.GetComponent<VolumetricLightRenderer>();
            Plugin.Log.LogDebug($"[LightProbe] {map.Key} volumetric: setting={(setting.HasValue ? setting.Value.ToString() : "unknown")} " +
                               $"components={vlTotal} activeAndEnabled={vlActive} [{string.Join(", ", vlNames)}] " +
                               $"renderer on '{cam.name}'={(renderer != null ? (renderer.enabled ? "enabled" : "disabled") : "missing")}");

            // Flicker: ten samples over one second per enabled LightFlicker of this map.
            var flickers = new List<EFT.Visual.LightFlicker>();
            foreach (var fl in UnityEngine.Object.FindObjectsOfType<EFT.Visual.LightFlicker>())
            {
                if (fl != null && fl.enabled && ArenaMapRaid.IsOwnedScene(fl.gameObject.scene.name)) flickers.Add(fl);
            }
            if (flickers.Count > 0)
            {
                var samples = new List<float>[flickers.Count];
                for (int i = 0; i < samples.Length; i++) samples[i] = new List<float>();
                for (int s = 0; s < 10; s++)
                {
                    for (int i = 0; i < flickers.Count; i++)
                    {
                        var light = flickers[i] != null ? flickers[i].GetComponent<Light>() : null;
                        samples[i].Add(light != null ? light.intensity : float.NaN);
                    }
                    yield return new WaitForSeconds(0.1f);
                }
                var fsb = new StringBuilder($"[LightProbe] {map.Key} flicker: {flickers.Count} enabled LightFlicker(s)");
                for (int i = 0; i < flickers.Count; i++)
                {
                    float min = float.MaxValue, max = float.MinValue;
                    foreach (float v in samples[i]) { if (v < min) min = v; if (v > max) max = v; }
                    var l = flickers[i] != null ? flickers[i].GetComponent<Light>() : null;
                    fsb.Append($"\n  '{(flickers[i] != null ? flickers[i].gameObject.name : "?")}' " +
                               $"d={(l != null ? F(Vector3.Distance(camPos, l.transform.position)) : "?")} " +
                               $"cullingCoef={(flickers[i] != null ? F(flickers[i].CullingCoef) : "?")} min={F(min)} max={F(max)} " +
                               $"samples=[{string.Join(" ", samples[i].ConvertAll(F))}]");
                }
                Plugin.Log.LogDebug(fsb.ToString());
            }
            else
            {
                Plugin.Log.LogDebug($"[LightProbe] {map.Key} flicker: no enabled LightFlicker");
            }
            LogCameraStack(map.Key, cam);
            HdrImageTuning.LogDiagnostics();
            _running = false;
        }

        // Floodlight glare follow-up (2026-09-27): the camera's image-effect stack and PrismEffects' bloom/exposure
        // values, so the donor camera and the Cam2 fallback can be compared by number instead of by screenshot.
        static void LogCameraStack(string mapKey, Camera cam)
        {
            var sb = new StringBuilder();
            sb.Append($"[LightProbe] {mapKey} camera stack '{cam.name}' hdr={cam.allowHDR} path={cam.actualRenderingPath}:");
            foreach (var b in cam.GetComponents<Behaviour>())
            {
                if (b != null)
                    sb.Append($" {b.GetType().Name}{(b.enabled ? "" : "(off)")}");
            }
            Plugin.Log.LogDebug(sb.ToString());
            sb.Clear();
            sb.Append($"[LightProbe] {mapKey} camera exposure:");
            var prism = cam.GetComponent<PrismEffects>();
            if (prism != null)
            {
                sb.Append($"\n  PrismEffects enabled={prism.enabled} useBloom={prism.useBloom} bloomType={prism.bloomType} " +
                          $"intensity={F(prism.bloomIntensity)} threshold={F(prism.bloomThreshold)} " +
                          $"downsample={prism.bloomDownsample} blurPasses={prism.bloomBlurPasses} " +
                          $"screenBlend={prism.bloomUseScreenBlend} stability={prism.useBloomStability} " +
                          $"lensDirt={prism.useLensDirt}/{F(prism.dirtIntensity)}" +
                          $"\n  exposure use={prism.useExposure} middleGrey={F(prism.exposureMiddleGrey)} " +
                          $"limits={F(prism.exposureLowerLimit)}..{F(prism.exposureUpperLimit)} speed={F(prism.exposureSpeed)} " +
                          $"tonemap={prism.useTonemap}");
            }
            else
            {
                sb.Append("\n  PrismEffects: none");
            }
            Plugin.Log.LogDebug(sb.ToString());
            // UltimateBloom and BloomAndFlares (Unity standard assets, not in Assembly-CSharp) by reflection.
            foreach (var b in cam.GetComponents<Behaviour>())
            {
                if (b == null)
                    continue;
                var typeName = b.GetType().Name;
                if (typeName == "UltimateBloom" || typeName == "BloomAndFlares")
                    Plugin.Log.LogDebug($"[LightProbe] {mapKey} {typeName}" +
                        $"{(b.enabled ? "" : "(off)")}:{ScalarFields(b)}");
            }
        }

        // Public instance fields of scalar type (float/int/bool/enum), skipping editor-UI toggles.
        static string ScalarFields(object o)
        {
            var sb = new StringBuilder();
            foreach (var f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var t = f.FieldType;
                if (f.Name.StartsWith("m_UiShow", StringComparison.Ordinal))
                    continue;
                if (t != typeof(float) && t != typeof(int) && t != typeof(bool) && !t.IsEnum)
                    continue;
                var v = f.GetValue(o);
                sb.Append(' ').Append(f.Name).Append('=')
                  .Append(v is float fv ? F(fv) : v?.ToString());
            }
            return sb.ToString();
        }
    }
}
