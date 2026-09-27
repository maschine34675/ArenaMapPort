using System.Collections.Generic;
using System.Linq;
using ArenaMapPort.Shared;
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using JsonType;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Use the native CURR/PAST toggles for descriptor-defined fixed start times. EFT excludes custom
    /// locations in two independent whitelists, then otherwise displays the advancing backend clock.
    /// Retain its listeners and weather icon; do not duplicate Factory's special location identities.
    /// Verified against the installed LocationConditionsPanel and selection-controller call paths.
    /// </summary>
    internal static class FixedRaidTimeSelection
    {
        internal static ArenaMapDescriptor Find(string key) =>
            ArenaMapRegistry.All.FirstOrDefault(m => m.Key == key && m.UsesFixedStartTimes);

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
                harmony.Patch(set, postfix: new HarmonyMethod(typeof(FixedRaidTimeSelection), nameof(ShowFixedTimes)));
        }

        static void AllowNight(ref IEnumerable<string> __result)
        {
            __result = __result.Concat(ArenaMapRegistry.All.Where(m => m.UsesFixedStartTimes).Select(m => m.Key)).Distinct();
        }

        static bool KeepSelectedTime(MatchMakerSelectionLocationScreen.SelectionLocationScreenController __instance) =>
            Find(__instance.RaidSettings.SelectedLocation?.Id) == null;

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
            Plugin.Log.LogInfo($"[ArenaMapPort][TimeSelection] {map.Key}: " +
                $"day={day} night={night} selected={raidSettings.SelectedDateTime}");
        }
    }
}
