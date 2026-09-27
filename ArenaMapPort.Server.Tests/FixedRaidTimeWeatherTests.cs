using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using HarmonyLib;
using System.Text.Json;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Utils;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public class FixedRaidTimeWeatherTests
{
    [Fact]
    public void FixedStartTimesAndSunTargets_AreEquatorOnly_AndMatchPipelineSidecars()
    {
        Assert.Equal(12, ArenaMapRegistry.Equator.FixedDayHour);
        Assert.Equal(0, ArenaMapRegistry.Equator.FixedNightHour);
        Assert.Equal("Arena_Equator_Lighting", ArenaMapRegistry.Equator.SunScene);
        Assert.Equal(new[] { "FAKE_SUN", "FAKE_SUN_VOLUME" }, ArenaMapRegistry.Equator.SunPaths);
        foreach (var map in ArenaMapRegistry.All)
        {
            var name = map.Key["arena_".Length..] + ".json";
            string? path = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "maps", name);
                if (!File.Exists(candidate)) continue;
                path = candidate;
                break;
            }
            Assert.NotNull(path);
            using var document = JsonDocument.Parse(File.ReadAllText(path!));
            var json = document.RootElement;
            if (map == ArenaMapRegistry.Equator)
            {
                Assert.True(map.UsesFixedStartTimes);
                Assert.Equal(map.FixedDayHour, json.GetProperty("fixed_day_hour").GetInt32());
                Assert.Equal(map.FixedNightHour, json.GetProperty("fixed_night_hour").GetInt32());
                Assert.Equal(map.SunScene, json.GetProperty("sun_scene").GetString());
                Assert.Equal(map.SunPaths, json.GetProperty("sun_paths").EnumerateArray().Select(v => v.GetString()!));
                Assert.Contains(map.SunScene, map.AllSceneNames);
            }
            else
            {
                Assert.False(map.UsesFixedStartTimes);
                Assert.Null(map.FixedDayHour);
                Assert.Null(map.FixedNightHour);
                Assert.Empty(map.SunPaths);
                Assert.Equal(string.Empty, map.SunScene);
                foreach (var field in new[] { "fixed_day_hour", "fixed_night_hour", "sun_scene", "sun_paths" })
                    Assert.False(json.TryGetProperty(field, out _), $"Unexpected {field} on {map.Key}");
            }
        }
    }

    [Fact]
    public void NativeHelper_UsesEquatorSelection_AndPreservesEveryOtherMap()
    {
        // Zero acceleration fixes the real helper's clock at 03:00. Its ordinary CURR/PAST
        // answers are deliberately opposite to Equator's fixed 12:00/00:00 starts.
        var helper = new WeatherHelper(new TimeUtil(), new WeatherConfig
        {
            Acceleration = 0,
            Weather = new WeatherValues(),
            SeasonDates = [],
        });
        var target = typeof(WeatherHelper).GetMethod(nameof(WeatherHelper.IsNightTime),
            [typeof(DateTimeEnum), typeof(string)])!;
        // Reflection prevents a caller's JIT inlining from bypassing the patched native method.
        bool Night(string key, DateTimeEnum variant) => (bool)target.Invoke(helper, [variant, key])!;
        var variants = new[] { DateTimeEnum.CURR, DateTimeEnum.PAST };
        var otherKeys = ArenaMapRegistry.All.Where(map => map != ArenaMapRegistry.Equator)
            .Select(map => map.Key).Concat(["factory4_day", "factory4_night", "bigmap", "unknown_map"]).ToArray();
        var before = otherKeys.SelectMany(key => variants.Select(variant =>
            (key, variant, value: Night(key, variant)))).ToArray();
        Assert.True(Night(ArenaMapRegistry.Equator.Key, DateTimeEnum.CURR));
        Assert.False(Night(ArenaMapRegistry.Equator.Key, DateTimeEnum.PAST));
        var warnings = new List<string>();
        try
        {
            Assert.True(FixedRaidTimeWeather.TryInstall(_ => { }, warnings.Add), string.Join("\n", warnings));
            Assert.Empty(warnings);
            Assert.False(Night(ArenaMapRegistry.Equator.Key, DateTimeEnum.CURR));
            Assert.True(Night(ArenaMapRegistry.Equator.Key, DateTimeEnum.PAST));
            foreach (var expected in before)
                Assert.Equal(expected.value, Night(expected.key, expected.variant));
        }
        finally
        {
            new Harmony(FixedRaidTimeWeather.PatchHarmonyId).Unpatch(target, HarmonyPatchType.All,
                FixedRaidTimeWeather.PatchHarmonyId);
        }
        Assert.True(Night(ArenaMapRegistry.Equator.Key, DateTimeEnum.CURR));
        Assert.False(Night(ArenaMapRegistry.Equator.Key, DateTimeEnum.PAST));
    }

    [Theory]
    [InlineData(null, DateTimeEnum.PAST)]
    [InlineData("arena_equator_suffix", DateTimeEnum.PAST)]
    [InlineData("arena_equator", (DateTimeEnum)123)]
    public void UnsupportedIdentityOrVariant_LeavesNativeDecision(string? key, DateTimeEnum variant)
    {
        Assert.False(FixedRaidTimeWeather.TryResolveNightTime(key, variant, out _));
    }
}
