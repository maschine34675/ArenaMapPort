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
    /// Per-map UltimateBloom threshold/gain on Arena raid cameras; -1 restores captured native values.
    /// This controls broad glow, not the early Equator camera's horizontal streak (see
    /// AnamorphicFlareTuning). CameraRepairHooks applies it after every SetCamera, including
    /// replacement CameraManager instances, and releases captured references at raid teardown.
    /// Native evidence: docs/progress/equator-anamorphic-hdr.md.
    /// </summary>
    internal static class BloomTuning
    {
        const string Tag = "[ArenaMapPort][Bloom]";
        const float EquatorThreshold = 1.25f;

        static bool _installed;
        static readonly Dictionary<string, (ConfigEntry<float> threshold, ConfigEntry<float> intensity)> _entries =
            new Dictionary<string, (ConfigEntry<float> threshold, ConfigEntry<float> intensity)>();

        // Component references avoid instance-id reuse and are released at scene teardown.
        static readonly Dictionary<UltimateBloom, (float threshold, float intensity)> _originals =
            new Dictionary<UltimateBloom, (float threshold, float intensity)>();

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

        }

        internal static void ApplyToCurrentCamera(string reason)
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

                if (!_originals.TryGetValue(bloom, out var original))
                {
                    if (entry.threshold.Value < 0f && entry.intensity.Value < 0f)
                    {
                        return; // nothing overridden and nothing to restore on this camera
                    }
                    original = (bloom.m_BloomThreshhold, bloom.m_BloomIntensity);
                    _originals[bloom] = original;
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

        internal static void Release()
        {
            foreach (var pair in _originals)
            {
                if (pair.Key == null) continue;
                pair.Key.m_BloomThreshhold = pair.Value.threshold;
                pair.Key.m_BloomIntensity = pair.Value.intensity;
            }
            _originals.Clear();
        }

        static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
