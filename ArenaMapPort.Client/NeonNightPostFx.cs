using System;
using BepInEx.Configuration;
using Comfort.Common;
using EFT.CameraControl;
using EFT.Settings;
using EFT.Settings.PostFx;
using EFT.UI;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Applies the reviewed Chillwave recipe to the raid camera through EFT's own controller.
    /// Only detached settings clones are changed; the global settings provider is never saved.
    /// Native settings/preview retain control while open. Evidence: equator-night-haze-postfx.md.
    /// </summary>
    internal sealed class NeonNightPostFx : IDisposable
    {
        const string Tag = "[NightPostFx]";
        static ConfigEntry<bool> _enabled;
        GradingPostFX _effect;
        PostFxSettingsGroup _lastUserSettings;
        bool _applied, _failed;
        string _state;

        internal static void BindConfig(EventHandler changed)
        {
            _enabled = Plugin.Instance.Settings.Bind("Night neon (arena_equator)", "PostFx", true,
                "Temporarily apply the balanced Chillwave look to the Equator 00:00 raid camera. " +
                "Saved game PostFX settings and colorblind options are preserved. Suspends in the " +
                "native settings/preview and restores your current settings on exit or disable. Live.");
            _enabled.SettingChanged += changed;
        }

        internal void Update(Camera camera)
        {
            try
            {
                var candidate = CameraManager.Exist ? CameraManager.Instance.PostFX : null;
                if (candidate != null && candidate.gameObject != camera.gameObject) candidate = null;
                bool editing = SettingsOpen();
                bool allowed = EquatorNightNeon.IsActive && _enabled.Value && !editing;
                if (_effect != candidate || !allowed)
                {
                    Restore();
                    _effect = candidate;
                }
                if (!allowed)
                {
                    _failed = false;
                    Log(editing ? "suspended for native settings/preview" : "inactive");
                    return;
                }
                if (_failed || _effect == null) return;
                var original = UserSettings();
                if (original == null) return;
                // Native Escape -> Save can close the screen before asynchronous settings saves
                // reach PostFX. A later native binding must not silently cancel this raid's look.
                if (_applied && _lastUserSettings != null && _lastUserSettings.HasSameSettings(original)) return;
                _lastUserSettings = original.Clone();
                var temporary = original.Clone();
                temporary.EnablePostFx.Value = true;
                temporary.Brightness.Value = 0;
                temporary.Saturation.Value = 35;
                temporary.Clarity.Value = 0;
                temporary.Colorfulness.Value = 45;
                temporary.LumaSharpen.Value = 0;
                temporary.AdaptiveSharpen.Value = 0;
                temporary.ColorFilterType.Value = CC_Vintage.Filter.Chillwave;
                temporary.Intensity.Value = 50;
                // Mark ownership first, so a partly applied native binding is restored on failure.
                _applied = true;
                Apply(_effect, temporary);
                Log($"applied camera={camera.GetInstanceID()} Chillwave/50 saturation=35 colorfulness=45; " +
                    $"saved={Describe(original)}; colorblind options retained");
            }
            catch (Exception e)
            {
                Restore();
                _failed = true;
                Log($"disabled after error; toggle PostFx off/on to retry: {e}", true);
            }
        }

        static bool SettingsOpen() =>
            (MonoBehaviourSingleton<CommonUI>.Instantiated &&
             MonoBehaviourSingleton<CommonUI>.Instance.SettingsScreen != null &&
             MonoBehaviourSingleton<CommonUI>.Instance.SettingsScreen.gameObject.activeInHierarchy) ||
            (MonoBehaviourSingleton<GameUI>.Instantiated &&
             MonoBehaviourSingleton<GameUI>.Instance.PostFXPreview != null &&
             MonoBehaviourSingleton<GameUI>.Instance.PostFXPreview.gameObject.activeInHierarchy);

        static PostFxSettingsGroup UserSettings() => Singleton<SettingsManager>.Instantiated
            ? Singleton<SettingsManager>.Instance.PostFx.Settings : null;

        static void Apply(GradingPostFX effect, PostFxSettingsGroup settings)
        {
            using (var controller = new PostFxSettingsController(effect))
            {
                controller.BindTo(settings);
                // Disabled user PostFX still need their parameters restored for later re-enabling.
                controller.TryForceUpdate();
            }
        }

        void Restore()
        {
            if (!_applied) return;
            _applied = false;
            _lastUserSettings = null;
            if (_effect == null) return;
            var current = UserSettings();
            if (current == null) return;
            try
            {
                Apply(_effect, current.Clone());
                Log($"restored latest saved settings: {Describe(current)}");
            }
            catch (Exception e) { Plugin.Log.LogError($"{Tag} camera restore failed: {e}"); }
        }

        internal void Probe() => Plugin.Log.LogDebug($"{Tag} enabled={_enabled.Value} applied={_applied} " +
            $"cameraEffect={(_effect != null)} effectEnabled={(_effect != null && _effect.enabled)} " +
            $"editing={SettingsOpen()} saved={Describe(UserSettings())}");

        static string Describe(PostFxSettingsGroup value) => value == null ? "unavailable" :
            $"enabled={value.EnablePostFx.Value},filter={value.ColorFilterType.Value}/{value.Intensity.Value}," +
            $"saturation={value.Saturation.Value},colorfulness={value.Colorfulness.Value}";

        void Log(string message, bool error = false)
        {
            if (_state == message) return;
            _state = message;
            if (error) Plugin.Log.LogError($"{Tag} {message}");
            else Plugin.Log.LogDebug($"{Tag} {message}");
        }

        public void Dispose() { Restore(); _effect = null; }
    }
}
