#nullable disable
using System.ComponentModel;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests;

[Collection("MapPort client lifecycle")]
public sealed class ReleaseConfigurationTests
{
    static bool Visible(ConfigEntryBase entry) => entry.Description.Tags.OfType<BrowsableAttribute>().Last().Browsable;

    [Fact]
    public void PlayerMenuHasTenControlsAndRetainsHiddenValuesRangesAndDrawers()
    {
        var cfg = new ConfigFile();
        var expected = new[] {
            "General/DeveloperMode", "Audio (arena_equator)/AmbientGainDb", "Audio (arena_equator)/RainVolume",
            "Audio (arena_bay5)/AmbientGainDb", "Audio (arena_bay5)/RainVolume",
            "Lighting (arena_equator Day)/ExposureCompensationEV", "Lighting (arena_equator Night)/ExposureCompensationEV",
            "Lighting (arena_bay5)/ExposureCompensationEV", "Night neon (arena_equator)/PostFx", "Night neon (arena_equator)/FlickerStrength"
        };
        foreach (var path in expected) { var parts = path.Split('/'); cfg.Bind(parts[0], parts[1], 1f, "Player control"); }
        var drawer = new object();
        var original = new ConfigDescription("Curve", new AcceptableValueRange<float>(0f, 2f), drawer);
        var curve = cfg.Bind("Lighting (arena_equator Night)", "ToneCurve", 1f, original); curve.Value = 1.75f;
        var legacy = cfg.Bind("Lighting (arena_equator)", "BloomIntensity", .35f, "Legacy"); legacy.Value = .8f;
        var parked = cfg.Bind("Lighting (arena_bowl)", "ReflectionIntensity", .5f, "Parked");
        var revision = cfg.Bind("Internal", "Revision", 7, new ConfigDescription("Hidden", null, BrowsableAttribute.No));
        cfg.Bind("Unknown future section", "FutureExperimentalControl", true, "Must opt into player menu");
        MapConfigVisibility.Apply(cfg, false);
        Assert.Equal(expected.Order(), cfg.Where(p => Visible(p.Value)).Select(p => p.Key.Section + "/" + p.Key.Key).Order());
        for (int i = 0; i < 3; i++)
        {
            MapConfigVisibility.Apply(cfg, true);
            Assert.True(Visible(curve));
            Assert.False(Visible(legacy)); Assert.False(Visible(parked)); Assert.False(Visible(revision));
            Assert.Equal(2, curve.Description.Tags.Length);
            Assert.Same(drawer, curve.Description.Tags[0]);
            Assert.Same(original.AcceptableValues, curve.Description.AcceptableValues);
            MapConfigVisibility.Apply(cfg, false);
            Assert.False(Visible(curve)); Assert.Equal(1.75f, curve.Value); Assert.Equal(.8f, legacy.Value);
        }
        Assert.Single(original.Tags);
        curve.Value = 9f; Assert.Equal(2f, curve.Value);
        var late = cfg.Bind("Diagnostics", "LateDiagnostic", true, "Late bind");
        MapConfigVisibility.Apply(cfg, false); Assert.False(Visible(late));
    }

    [Fact]
    public void DeveloperModeIsLatchedUntilTheNextStartup()
    {
        var cfg = new ConfigFile();
        DeveloperOptions.Install(cfg); Assert.False(DeveloperOptions.Enabled);
        var enabled = cfg.Get<bool>("General", "DeveloperMode"); enabled.Value = true;
        Assert.False(DeveloperOptions.Enabled);
        DeveloperOptions.Install(cfg); Assert.True(DeveloperOptions.Enabled);
        enabled.Value = false; Assert.True(DeveloperOptions.Enabled);
        DeveloperOptions.Install(cfg); Assert.False(DeveloperOptions.Enabled);
    }

    [Fact]
    public void OnlyTwoMapsArePlayableAndParkedScenesNeverActivateRepairs()
    {
        Assert.Equal(new[] { "arena_equator", "arena_bay5" }, ArenaMapRegistry.All.Select(m => m.Key));
        var parked = ArenaMapRegistry.Catalog.Except(ArenaMapRegistry.All).ToArray();
        Assert.Equal(12, parked.Length);
        SceneManager.Scenes.Clear();
        try
        {
            foreach (var map in parked)
            {
                Assert.Null(ArenaMapRaid.MapForScriptsScene(map.ScriptsSceneName));
                foreach (var scene in map.AllSceneNames)
                {
                    Assert.False(ArenaMapRaid.IsOwnedScene(scene));
                    Assert.False(ArenaMapRaid.IsGameplayScene(scene));
                }
                SceneManager.Load(new Scene(map.ScriptsSceneName));
                Assert.Null(ArenaMapRaid.CurrentMap);
            }
        }
        finally { SceneManager.Scenes.Clear(); }
    }
}
