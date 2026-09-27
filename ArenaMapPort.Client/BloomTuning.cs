using System;
using System.Collections.Generic;
using System.Globalization;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using EFT.CameraControl;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Floodlight glare follow-up to PLAN-TWO-MAPS item 7 (2026-09-27) — per-map bloom on the raid camera.
    ///
    /// What vanilla does: the raid camera prefab carries <c>UltimateBloom</c> (HDR bloom, <c>m_BloomThreshhold</c>
    /// / <c>m_BloomIntensity</c>) next to <c>PrismEffects</c>; each vanilla map's own camera prefab brings values
    /// tuned for that map.
    ///
    /// What the ported maps lack: Arena's own camera does not ship with the maps, so Equator and Bay5 render
    /// through the donor camera rig. The F7 probe (host 2026-09-27 17:53) read it: PrismEffects <c>useBloom=False</c>,
    /// <c>UltimateBloom</c> live with threshold 2.10 and intensity 0.35 — too high for Equator's atrium floodlights
    /// (<c>CAPTURE_ZONE/LIGHT/Projector_portable_mask_rezerv*</c>), which no longer glare the way the first playable
    /// build did (user screenshot 2026-09-19 09:28).
    ///
    /// What this does: per map, <c>Lighting (&lt;map&gt;) › BloomThreshold</c> and <c>BloomIntensity</c>; -1 keeps
    /// the camera's own value. A value &gt;= 0 is written to the active raid camera's <c>UltimateBloom</c> on every
    /// camera change in that map's raid and live when the key changes; -1 restores the camera's original value.
    /// Equator defaults to threshold 1.25, every other map to -1. Vanilla maps are never touched.
    ///
    /// Host A/B 2026-09-27 18:17 (instance 011a4a56, Equator atrium, 5.6 m from the floodlight): 2.10 = white lamp
    /// face without halo; 1.5 = faint glow; 1.25 = clear halo, EQUATOR letters glow but stay readable (the 09-19
    /// look); 1.0 = strong halo, letters blown out. Bay5 overcast outdoors: 1.25 and 2.10 look the same.
    /// </summary>
    internal static class BloomTuning
    {
        const string Tag = "[ArenaMapPort][Bloom]";
        const float EquatorThreshold = 1.25f;

        static bool _installed;
        static readonly Dictionary<string, (ConfigEntry<float> threshold, ConfigEntry<float> intensity)> _entries =
            new Dictionary<string, (ConfigEntry<float> threshold, ConfigEntry<float> intensity)>();

        // camera instance id -> the UltimateBloom values before this class first wrote them.
        static readonly Dictionary<int, (float threshold, float intensity)> _originals =
            new Dictionary<int, (float threshold, float intensity)>();

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            foreach (var map in ArenaMapRegistry.All)
            {
                var section = $"Lighting ({map.Key})";
                var defaultThreshold = map.Key == ArenaMapRegistry.Equator.Key ? EquatorThreshold : -1f;
                var threshold = Plugin.Instance.Config.Bind(section, "BloomThreshold", defaultThreshold,
                    new ConfigDescription(
                        "UltimateBloom HDR threshold on the raid camera for this map. -1 keeps the camera's own value " +
                        "(donor rig: 2.10). Lower = more glow around bright lights: 1.0 strong, 1.25 the Equator " +
                        "default (atrium floodlights glare, signs stay readable), 1.5 faint. Live.",
                        new AcceptableValueRange<float>(-1f, 10f)));
                var intensity = Plugin.Instance.Config.Bind(section, "BloomIntensity", -1f,
                    new ConfigDescription(
                        "UltimateBloom intensity on the raid camera for this map. -1 keeps the camera's own value " +
                        "(donor rig: 0.35). Live.",
                        new AcceptableValueRange<float>(-1f, 5f)));
                threshold.SettingChanged += (_, __) => ApplyToCurrentCamera("setting changed");
                intensity.SettingChanged += (_, __) => ApplyToCurrentCamera("setting changed");
                _entries[map.Key] = (threshold, intensity);
            }

            CameraManager.Instance.OnCameraChanged += () => ApplyToCurrentCamera("camera changed");
        }

        static void ApplyToCurrentCamera(string reason)
        {
            try
            {
                var map = ArenaMapRaid.CurrentMap;
                if (map == null || !_entries.TryGetValue(map.Key, out var entry))
                {
                    return;
                }
                var cam = CameraManager.Instance?.Camera;
                var bloom = cam != null ? cam.GetComponent<UltimateBloom>() : null;
                if (bloom == null)
                {
                    return;
                }

                var id = cam.GetInstanceID();
                if (!_originals.TryGetValue(id, out var original))
                {
                    if (entry.threshold.Value < 0f && entry.intensity.Value < 0f)
                    {
                        return; // nothing overridden and nothing to restore on this camera
                    }
                    original = (bloom.m_BloomThreshhold, bloom.m_BloomIntensity);
                    _originals[id] = original;
                }

                var before = (threshold: bloom.m_BloomThreshhold, intensity: bloom.m_BloomIntensity);
                bloom.m_BloomThreshhold = entry.threshold.Value >= 0f ? entry.threshold.Value : original.threshold;
                bloom.m_BloomIntensity = entry.intensity.Value >= 0f ? entry.intensity.Value : original.intensity;
                if (before.threshold != bloom.m_BloomThreshhold || before.intensity != bloom.m_BloomIntensity)
                {
                    Plugin.Log.LogInfo($"{Tag} {map.Key} '{cam.name}' ({reason}): " +
                                       $"threshold {F(before.threshold)} -> {F(bloom.m_BloomThreshhold)}, " +
                                       $"intensity {F(before.intensity)} -> {F(bloom.m_BloomIntensity)} " +
                                       $"(camera's own {F(original.threshold)}/{F(original.intensity)})");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} apply failed ({reason}): {e.GetType().Name}: {e.Message}");
            }
        }

        static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
