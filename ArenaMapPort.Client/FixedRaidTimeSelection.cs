using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ArenaMapPort.Shared;
using EFT;
using EFT.UI.Matchmaker;
using EFT.Utilities;
using EFT.Weather;
using HarmonyLib;
using JsonType;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Use the native CURR/PAST toggles for descriptor-defined time choices. EFT excludes custom
    /// locations in two independent whitelists, then otherwise displays the advancing backend clock.
    /// Retain its listeners; Bay5 and AirPit (descriptor UsesServerTime) keep the backend clock and weather. Equator alone replaces the
    /// clock labels and previews its day Clear / optional midnight rain without altering session weather.
    /// Do not duplicate Factory's special location identities.
    /// Verified against the installed LocationConditionsPanel and selection-controller call paths.
    /// </summary>
    internal static class FixedRaidTimeSelection
    {
        sealed class WeatherPreview
        {
            internal ArenaMapDescriptor Map;
            internal Sprite OriginalSprite;
            internal bool OriginalVisible;
        }
        static readonly ConditionalWeakTable<LocationConditionsPanel, WeatherPreview> Previews = new();

        internal static ArenaMapDescriptor Find(string key) =>
            ArenaMapRegistry.All.FirstOrDefault(m => m.Key == key && m.UsesFixedStartTimes);

        internal static ArenaMapDescriptor FindSelectable(string key) =>
            ArenaMapRegistry.All.FirstOrDefault(m => m.Key == key && m.HasTimeSelection);

        internal static void Install(Harmony harmony)
        {
            var location = typeof(LocationSettings.Location);
            var getter = AccessTools.PropertyGetter(location, nameof(LocationSettings.Location.NightTimeAllowedLocations));
            if (HarmonyPatches.Verify(getter, location, "get_NightTimeAllowedLocations"))
                harmony.Patch(getter, postfix: new HarmonyMethod(typeof(FixedRaidTimeSelection), nameof(AllowNight)));

            var controller = typeof(MatchMakerSelectionLocationScreen.SelectionLocationScreenController);
            var update = AccessTools.Method(controller, "UpdateSelectedDateTime");
            if (HarmonyPatches.Verify(update, controller, "UpdateSelectedDateTime"))
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(FixedRaidTimeSelection), nameof(KeepSelectedTime)));

            var panel = typeof(LocationConditionsPanel);
            var set = AccessTools.Method(panel, nameof(LocationConditionsPanel.Set));
            if (HarmonyPatches.Verify(set, panel, nameof(LocationConditionsPanel.Set)) &&
                AccessTools.Field(panel, "_needTimerUpdate") != null)
                harmony.Patch(set,
                    prefix: new HarmonyMethod(typeof(FixedRaidTimeSelection), nameof(ForgetWeather)),
                    postfix: new HarmonyMethod(typeof(FixedRaidTimeSelection), nameof(ShowFixedTimes)));

            // Native time toggles only set this property and notify the map controller. They do
            // NOT call Set again, so a Set-only weather preview would keep the previous icon.
            var selectedTime = AccessTools.PropertySetter(panel, nameof(LocationConditionsPanel.SelectedDateTime));
            if (HarmonyPatches.Verify(selectedTime, panel, "set_SelectedDateTime"))
                harmony.Patch(selectedTime,
                    postfix: new HarmonyMethod(typeof(FixedRaidTimeSelection), nameof(RefreshWeather)));
        }

        static void AllowNight(ref IEnumerable<string> __result)
        {
            __result = __result.Concat(ArenaMapRegistry.All.Where(m => m.HasTimeSelection).Select(m => m.Key)).Distinct();
        }

        static bool KeepSelectedTime(MatchMakerSelectionLocationScreen.SelectionLocationScreenController __instance) =>
            FindSelectable(__instance.RaidSettings.SelectedLocation?.Id) == null;

        static void ForgetWeather(LocationConditionsPanel __instance) => Previews.Remove(__instance);

        static void RefreshWeather(LocationConditionsPanel __instance)
        {
            if (!Previews.TryGetValue(__instance, out var preview) || __instance._weatherIcon == null) return;
            var selected = __instance.SelectedDateTime;
            int? hour = selected == EDateTime.CURR ? preview.Map.FixedDayHour :
                selected == EDateTime.PAST ? preview.Map.FixedNightHour : null;
            var mode = hour.HasValue ? WeatherTestOverride.ModeForSelection(preview.Map, hour) :
                WeatherTestOverride.TestWeather.Server;
            var weatherType = mode switch
            {
                WeatherTestOverride.TestWeather.Clear => EWeatherType.ClearDay,
                WeatherTestOverride.TestWeather.Rain => EWeatherType.Rain,
                WeatherTestOverride.TestWeather.Overcast => EWeatherType.FullCloud,
                WeatherTestOverride.TestWeather.Fog => EWeatherType.Fog,
                _ => EWeatherType.None,
            };
            var icon = weatherType != EWeatherType.None ? ResourcesCache.Pop<Sprite>("UI/WeatherIcons/" + weatherType) : null;
            // Missing optional sprite fails back to vanilla; never erase a useful existing icon.
            __instance._weatherIcon.sprite = icon ?? preview.OriginalSprite;
            __instance._weatherIcon.gameObject.SetActive(icon != null || preview.OriginalVisible);
        }

        static void ShowFixedTimes(LocationConditionsPanel __instance, RaidSettings raidSettings, ref bool ____needTimerUpdate)
        {
            var map = Find(raidSettings.SelectedLocation?.Id);
            if (map == null) return;
            ____needTimerUpdate = false;
            string day = $"{map.FixedDayHour.Value:00}:00:00";
            string night = $"{map.FixedNightHour.Value:00}:00:00";
            if (__instance._amTimeToggle != null && __instance._pmTimeToggle != null)
            {
                __instance._currentPhaseTime.text = day;
                __instance._nextPhaseTime.text = night;
            }
            else
                __instance._currentPhaseTime.text = raidSettings.SelectedDateTime == EDateTime.PAST ? night : day;
            if (__instance._weatherIcon != null)
            {
                Previews.Add(__instance, new WeatherPreview
                {
                    Map = map,
                    OriginalSprite = __instance._weatherIcon.sprite,
                    OriginalVisible = __instance._weatherIcon.gameObject.activeSelf,
                });
                RefreshWeather(__instance);
            }
            Plugin.Log.LogDebug($"[ArenaMapPort][TimeSelection] {map.Key}: " +
                $"day={day} night={night} selected={raidSettings.SelectedDateTime}");
        }
    }
}
