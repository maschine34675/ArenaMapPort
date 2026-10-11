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
        const float DefaultThreshold = 2.1f;
        const float DefaultIntensity = 0.35f;

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
                bool releasedMap = map.Key == ArenaMapRegistry.Equator.Key || map.Key == ArenaMapRegistry.Bay5.Key;
                var threshold = Plugin.Instance.Settings.Bind(section, "BloomThreshold", releasedMap ? DefaultThreshold : -1f,
                    new ConfigDescription(
                        "UltimateBloom HDR threshold on the raid camera for this map. -1 keeps the camera's own value " +
                        "(donor rig: 2.10). Lower = more glow around bright lights. Equator and Bay5 default to " +
                        "the donor rig's normal threshold, 2.10. Live.",
                        new AcceptableValueRange<float>(-1f, 10f)));
                var intensity = Plugin.Instance.Settings.Bind(section, "BloomIntensity", releasedMap ? DefaultIntensity : -1f,
                    new ConfigDescription(
                        "UltimateBloom intensity on the raid camera for this map. -1 keeps the camera's own value " +
                        "(donor rig: 0.35). Equator and Bay5 default to this normal intensity, 0.35. Live.",
                        new AcceptableValueRange<float>(-1f, 5f)));

                // Migrate before subscribing: no camera writes during config binding. Unreleased maps
                // keep their native defaults and do not acquire a bloom migration marker.
                if (releasedMap) MigrateDefaults(map.Key, section, threshold, intensity);
                EquatorProfiles.Subscribe(threshold, (_, __) => ApplyToCurrentCamera("setting changed"));
                EquatorProfiles.Subscribe(intensity, (_, __) => ApplyToCurrentCamera("setting changed"));
                _entries[map.Key] = (threshold, intensity);
            }

        }

        static void MigrateDefaults(string mapKey, string section, ConfigEntry<float> threshold,
            ConfigEntry<float> intensity)
        {
            var revision = Plugin.Instance.Settings.Bind(section, "BloomDefaultsRevision", 0,
                new ConfigDescription(
                    "Internal — once-only bloom default migration for Equator and Bay5. Leave it alone; " +
                    "lowering this value repeats migration of values still equal to the legacy defaults.",
                    null, System.ComponentModel.BrowsableAttribute.No));
            if (revision.Value >= 1) return;

            // Exact legacy values only; independently tuned values remain untouched. A saved -1 that
            // was the old default cannot be distinguished from a manually selected native fallback.
            // After this one-time migration, choosing -1 again persists across subsequent startups.
            float oldThreshold = mapKey == ArenaMapRegistry.Equator.Key ? 1.25f : -1f;
            int moved = 0;
            if (threshold.Value == oldThreshold) { threshold.Value = DefaultThreshold; moved++; }
            if (intensity.Value == -1f) { intensity.Value = DefaultIntensity; moved++; }
            revision.Value = 1;
            Plugin.Log.LogDebug($"{Tag} {mapKey}: config re-seeded to bloom defaults revision 1 " +
                $"({moved} value(s) moved; threshold={F(threshold.Value)}, intensity={F(intensity.Value)})");
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
                    if (EquatorProfiles.Value(entry.threshold) < 0f && EquatorProfiles.Value(entry.intensity) < 0f)
                    {
                        return; // nothing overridden and nothing to restore on this camera
                    }
                    original = (bloom.m_BloomThreshhold, bloom.m_BloomIntensity);
                    _originals[bloom] = original;
                }

                var before = (threshold: bloom.m_BloomThreshhold, intensity: bloom.m_BloomIntensity);
                bloom.m_BloomThreshhold = EquatorProfiles.Value(entry.threshold) >= 0f ? EquatorProfiles.Value(entry.threshold) : original.threshold;
                bloom.m_BloomIntensity = EquatorProfiles.Value(entry.intensity) >= 0f ? EquatorProfiles.Value(entry.intensity) : original.intensity;
                if (before.threshold != bloom.m_BloomThreshhold || before.intensity != bloom.m_BloomIntensity)
                {
                    Plugin.Log.LogDebug($"{Tag} {map.Key} '{cam.name}' ({reason}): " +
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
