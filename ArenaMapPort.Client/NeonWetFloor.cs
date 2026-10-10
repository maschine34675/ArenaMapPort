using System;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>Night-only effective values, applied by the existing material owners.</summary>
    internal static class NeonWetFloor
    {
        static ConfigEntry<float> _strength;
        static bool _owned;
        static float _lastStrength;
        static bool _retry, _loggedFailure;
        static float _retryAt;

        internal static void BindConfig(EventHandler changed)
        {
            _strength = Plugin.Instance.Settings.Bind("Night neon (arena_equator)", "WetFloorStrength", .8f,
                new ConfigDescription("Additional floor sheen for the midnight neon variant. " +
                    "0 = your configured floor values; 1 = stronger native SMap floor gloss (target 2.2, never reduces higher values). " +
                    "Keeps existing wet masks, reflection colors and daytime settings. Live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            _strength.SettingChanged += changed;
        }

        // Native shader fields are misleading: _Specularness is G-buffer smoothness, while
        // _Glossness contributes to specular RGB. Only the verified SMap floor variant is changed.
        internal static float Effective(Material material, float configured, bool floor) => ArenaNeonPolicy.WetFloorSmoothness(
            floor && material != null && material.shader != null && material.shader.name == "p0/Reflective/Bumped Specular SMap"
                && _owned && EquatorNightNeon.IsActive, ArenaMapRaid.CurrentMap?.Key,
            TimeOfDayRepairs.ActiveFixedHour, configured, _strength?.Value ?? 0f);

        internal static void Update(bool owned)
        {
            bool active = owned && EquatorNightNeon.IsActive;
            float strength = _strength?.Value ?? 0f;
            bool changed = _owned != active || _lastStrength != strength;
            if (!changed && (!_retry || Time.unscaledTime < _retryAt)) return;
            if (changed) _loggedFailure = false;
            // Clear ownership before restoration, including error teardown while config is on.
            _owned = active;
            _lastStrength = strength;
            var map = ArenaMapRaid.CurrentMap;
            _retry = false;
            if (map != ArenaMapRegistry.Equator) return;
            // A disappearing material must not prevent the rest of neon teardown from restoring.
            // Retry on the main thread without flooding the log.
            try { IndoorFloorSmoothnessRepairs.ReapplyIfLoaded(map); }
            catch (Exception e) { Failed("indoor", e); }
            if (_retry) _retryAt = Time.unscaledTime + 1f;
            else _loggedFailure = false;
            if (changed && DeveloperOptions.Enabled) Probe();
        }

        static void Failed(string owner, Exception error)
        {
            _retry = true;
            if (!_loggedFailure)
                Plugin.Log.LogWarning($"[ArenaMapPort][NightWetFloor] {owner} refresh failed; cleanup continues and refresh will retry: {error}");
            _loggedFailure = true;
        }

        internal static void Probe() => Plugin.Log.LogInfo($"[ArenaMapPort][NightWetFloor] " +
            $"active={(_owned && EquatorNightNeon.IsActive)} strength={_strength.Value:F2} " +
            $"nativeGlossTarget=2.200 savedIndoorMultiplier={WeatherRig.IndoorFloorSmoothnessForCurrentMap():F3}; saved values retained");
    }
}
