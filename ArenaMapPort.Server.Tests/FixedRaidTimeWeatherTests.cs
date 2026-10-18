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
    public void FixedStartTimesAndSunTargets_MatchPipelineSidecars()
    {
        Assert.Equal(12, ArenaMapRegistry.Equator.FixedDayHour);
        Assert.Equal(0, ArenaMapRegistry.Equator.FixedNightHour);
        Assert.Equal("Arena_Equator_Lighting", ArenaMapRegistry.Equator.SunScene);
        Assert.Equal(new[] { "FAKE_SUN", "FAKE_SUN_VOLUME" }, ArenaMapRegistry.Equator.SunPaths);
        Assert.Null(ArenaMapRegistry.Bay5.FixedDayHour);
        Assert.Null(ArenaMapRegistry.Bay5.FixedNightHour);
        Assert.True(ArenaMapRegistry.Bay5.UsesServerTime);
        // AirPit follows Bay5's policy (user decision 2026-10-11): native server time, no fixed hours, no sun.
        Assert.Null(ArenaMapRegistry.AirPit.FixedDayHour);
        Assert.Null(ArenaMapRegistry.AirPit.FixedNightHour);
        Assert.True(ArenaMapRegistry.AirPit.UsesServerTime);
        Assert.True(ArenaMapRegistry.AirPit.HasTimeSelection);
        Assert.False(ArenaMapRegistry.AirPit.UsesFixedStartTimes);
        // Bowl follows the same policy (docs/PLAN-BOWL.md, 2026-10-18): no Directional light in its imported scenes.
        var serverTimeMaps = new[] { ArenaMapRegistry.Bay5, ArenaMapRegistry.AirPit, ArenaMapRegistry.Bowl };
        foreach (var map in ArenaMapRegistry.Catalog)
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
            Assert.Equal(serverTimeMaps.Contains(map), map.UsesServerTime);
            Assert.Equal(map.UsesServerTime, json.TryGetProperty("uses_server_time", out var serverTime) && serverTime.GetBoolean());
            Assert.Equal(map == ArenaMapRegistry.Equator || serverTimeMaps.Contains(map), map.HasTimeSelection);
            if (map == ArenaMapRegistry.Equator)
            {
                Assert.True(map.UsesFixedStartTimes);
                Assert.Equal(map.FixedDayHour, json.GetProperty("fixed_day_hour").GetInt32());
                Assert.Equal(map.FixedNightHour, json.GetProperty("fixed_night_hour").GetInt32());
            }
            else
            {
                Assert.False(map.UsesFixedStartTimes);
                Assert.Null(map.FixedDayHour);
                Assert.Null(map.FixedNightHour);
                foreach (var field in new[] { "fixed_day_hour", "fixed_night_hour" })
                    Assert.False(json.TryGetProperty(field, out _), $"Unexpected {field} on {map.Key}");
            }
            if (map == ArenaMapRegistry.Equator)
            {
                Assert.Equal(map.SunScene, json.GetProperty("sun_scene").GetString());
                Assert.Equal(map.SunPaths, json.GetProperty("sun_paths").EnumerateArray().Select(v => v.GetString()!));
                Assert.Contains(map.SunScene, map.AllSceneNames);
            }
            else
            {
                Assert.Empty(map.SunPaths);
                Assert.Equal(string.Empty, map.SunScene);
                foreach (var field in new[] { "sun_scene", "sun_paths" })
                    Assert.False(json.TryGetProperty(field, out _), $"Unexpected {field} on {map.Key}");
            }
        }
    }

    [Fact]
    public void NativeHelper_UsesFixedSelections_AndPreservesEveryOtherMap()
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
        var fixedKeys = new[] { ArenaMapRegistry.Equator.Key };
        var otherKeys = ArenaMapRegistry.Catalog.Where(map => !fixedKeys.Contains(map.Key))
            .Select(map => map.Key).Concat(["factory4_day", "factory4_night", "bigmap", "unknown_map"]).ToArray();
        var before = otherKeys.SelectMany(key => variants.Select(variant =>
            (key, variant, value: Night(key, variant)))).ToArray();
        foreach (var key in fixedKeys)
        {
            Assert.True(Night(key, DateTimeEnum.CURR));
            Assert.False(Night(key, DateTimeEnum.PAST));
        }
        var warnings = new List<string>();
        try
        {
            Assert.True(FixedRaidTimeWeather.TryInstall(_ => { }, warnings.Add), string.Join("\n", warnings));
            Assert.Empty(warnings);
            foreach (var key in fixedKeys)
            {
                Assert.False(Night(key, DateTimeEnum.CURR));
                Assert.True(Night(key, DateTimeEnum.PAST));
            }
            foreach (var expected in before)
                Assert.Equal(expected.value, Night(expected.key, expected.variant));
        }
        finally
        {
            new Harmony(FixedRaidTimeWeather.PatchHarmonyId).Unpatch(target, HarmonyPatchType.All,
                FixedRaidTimeWeather.PatchHarmonyId);
        }
        foreach (var key in fixedKeys)
        {
            Assert.True(Night(key, DateTimeEnum.CURR));
            Assert.False(Night(key, DateTimeEnum.PAST));
        }
    }

    [Theory]
    [InlineData(null, DateTimeEnum.PAST)]
    [InlineData("arena_equator_suffix", DateTimeEnum.PAST)]
    [InlineData("arena_bay5", DateTimeEnum.CURR)]
    [InlineData("arena_bay5", DateTimeEnum.PAST)]
    [InlineData("arena_airpit", DateTimeEnum.CURR)]
    [InlineData("arena_airpit", DateTimeEnum.PAST)]
    [InlineData("arena_equator", (DateTimeEnum)123)]
    public void UnsupportedIdentityOrVariant_LeavesNativeDecision(string? key, DateTimeEnum variant)
    {
        Assert.False(FixedRaidTimeWeather.TryResolveNightTime(key, variant, out _));
    }
}
