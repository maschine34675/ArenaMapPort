using System.Reflection;
using ArenaMapPort.Shared;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Models.Enums;

namespace ArenaMapPort.Server;

/// <summary>
/// SPT derives night equipment from its accelerated backend clock, except for Factory.
/// A map with fixed day/night starts instead uses the chosen variant, matching the client.
/// Hook the shared helper so both raid configuration and direct bot-equipment calls agree.
/// </summary>
public static class FixedRaidTimeWeather
{
    public const string PatchHarmonyId = "com.maschine.ArenaMapPort.fixedRaidTime";

    public static bool TryResolveNightTime(string? mapKey, DateTimeEnum timeVariant, out bool isNight)
    {
        isNight = false;
        if (timeVariant != DateTimeEnum.CURR && timeVariant != DateTimeEnum.PAST) return false;
        foreach (var map in ArenaMapRegistry.All)
        {
            if (!map.UsesFixedStartTimes || !string.Equals(map.Key, mapKey, StringComparison.OrdinalIgnoreCase))
                continue;
            isNight = timeVariant == DateTimeEnum.PAST;
            return true;
        }
        return false;
    }

    public static bool TryInstall(Action<string> info, Action<string> warn)
    {
        try
        {
            new NightTimePatch().Enable();
            info("Fixed raid times: server night equipment follows the selected day/night variant on configured Arena maps.");
            return true;
        }
        catch (Exception e)
        {
            warn($"Fixed raid times server patch FAILED ({e.GetType().Name}: {e.Message}); " +
                 "night equipment will still follow the backend clock.");
            return false;
        }
    }

    private sealed class NightTimePatch : AbstractPatch
    {
        public NightTimePatch() : base(PatchHarmonyId) { }

        protected override MethodBase? GetTargetMethod() => AccessTools.Method(typeof(WeatherHelper),
            nameof(WeatherHelper.IsNightTime), [typeof(DateTimeEnum), typeof(string)]);

        [PatchPrefix]
        private static bool Prefix(DateTimeEnum timeVariant, string mapLocation, ref bool __result)
        {
            if (!TryResolveNightTime(mapLocation, timeVariant, out var isNight)) return true;
            __result = isNight;
            return false;
        }
    }
}
