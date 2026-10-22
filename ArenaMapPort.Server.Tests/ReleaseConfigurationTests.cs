#nullable disable
using System.ComponentModel;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine.SceneManagement;
using Xunit;
using ClientGate = ArenaMapPort.Client.ArenaInstallationGate; // the server mod has an ArenaInstallationGate of its own

namespace ArenaMapPort.Server.Tests;

[Collection("MapPort client lifecycle")]
public sealed class ReleaseConfigurationTests
{
    static bool Visible(ConfigEntryBase entry) => entry.Description.Tags.OfType<BrowsableAttribute>().Last().Browsable;

    [Fact]
    public void PlayerMenuRetainsThreeNormalAndOneAdvancedControlWithTuningHidden()
    {
        var cfg = new ConfigFile();
        // Release 0.2.3 added the Arena installation override and the in-game map notices (2026-10-22) their switch: four public entries in the
        // player's F12 menu (three normal controls and DeveloperMode under Advanced).
        var expected = new[] {
            "Appearance/NaturalEquatorNight", "General/ArenaInstallPath", "General/DeveloperMode", "General/ShowMapNotices"
        };
        cfg.Bind("Appearance", "NaturalEquatorNight", false, "Player control");
        DeveloperOptions.Install(cfg);
        var arenaPath = ClientGate.BindConfig(cfg); // the plugin's own binding (Plugin.Awake)
        // MapNotices.BindConfig binds exactly this (the glue calls EFT's UI and cannot be compiled here; a source pin keeps the two together)
        var showNotices = cfg.Bind(MapNoticeText.ShowMapNoticesSection, MapNoticeText.ShowMapNoticesKey, true, MapNoticeText.ShowMapNoticesDescription);
        cfg.Bind("Audio (arena_equator)", "AmbientGainDb", 5f, "Hidden");
        cfg.Bind("Lighting (arena_equator Night)", "ExposureCompensationEV", .3f, "Hidden");
        var drawer = new object();
        var original = new ConfigDescription("Curve", new AcceptableValueRange<float>(0f, 2f), drawer);
        var curve = cfg.Bind("Lighting (arena_equator Night)", "ToneCurve", 1f, original); curve.Value = 1.75f;
        var legacy = cfg.Bind("Lighting (arena_equator)", "BloomIntensity", .35f, "Legacy"); legacy.Value = .8f;
        // Prison is a parked map (Bowl was this fixture until PLAN-BOWL item 8, 2026-10-18, released it).
        var parked = cfg.Bind("Lighting (arena_prison)", "ReflectionIntensity", .5f, "Parked");
        // Released maps keep their tuning sections: hidden for players, visible in developer mode
        // (AirPit joined Equator Night and Bay5 when it entered ArenaMapRegistry.All, Bowl after it).
        var bay5 = cfg.Bind("Lighting (arena_bay5)", "ReflectionIntensity", 1f, "Released map");
        var airpit = cfg.Bind("Lighting (arena_airpit)", "ReflectionIntensity", 1f, "Released map");
        var bowl = cfg.Bind("Lighting (arena_bowl)", "ReflectionIntensity", 1f, "Released map");
        var airpit2 = cfg.Bind("Lighting (arena_airpit2)", "ReflectionIntensity", 1f, "Parked sibling of AirPit");
        var revision = cfg.Bind("Internal", "Revision", 7, new ConfigDescription("Hidden", null, BrowsableAttribute.No));
        cfg.Bind("Unknown future section", "FutureExperimentalControl", true, "Must opt into player menu");
        MapConfigVisibility.Apply(cfg, false);
        Assert.Equal(expected.Order(), cfg.Where(p => Visible(p.Value)).Select(p => p.Key.Section + "/" + p.Key.Key).Order());
        Assert.False(Visible(bay5)); Assert.False(Visible(airpit)); Assert.False(Visible(bowl)); Assert.False(Visible(airpit2));
        var developerEntry = cfg.Get<bool>("General", "DeveloperMode");
        var advancedTag = Assert.Single(developerEntry.Description.Tags, t => t.GetType().Name == "ConfigurationManagerAttributes");
        Assert.Equal(true, advancedTag.GetType().GetField("IsAdvanced").GetValue(advancedTag));
        Assert.DoesNotContain(cfg.Get<bool>("Appearance", "NaturalEquatorNight").Description.Tags,
            t => t.GetType().GetField("IsAdvanced")?.GetValue(t) is true);
        // The install path is a normal control: visible to players, never an Advanced-only entry, empty by default.
        Assert.True(Visible(arenaPath));
        Assert.DoesNotContain(arenaPath.Description.Tags, t => t.GetType().GetField("IsAdvanced")?.GetValue(t) is true);
        Assert.Equal("", arenaPath.Value);
        // The message switch is a normal control too (the message that names it must send the player to a setting that is there): default on.
        Assert.True(Visible(showNotices));
        Assert.DoesNotContain(showNotices.Description.Tags, t => t.GetType().GetField("IsAdvanced")?.GetValue(t) is true);
        Assert.True(showNotices.Value);
        Assert.Equal(MapNoticeText.ShowMapNoticesDescription, showNotices.Description.Description);
        for (int i = 0; i < 3; i++)
        {
            MapConfigVisibility.Apply(cfg, true);
            Assert.True(Visible(curve));
            Assert.True(Visible(developerEntry));
            Assert.True(Visible(arenaPath));
            Assert.True(Visible(showNotices));
            Assert.False(Visible(legacy)); Assert.False(Visible(parked)); Assert.False(Visible(revision));
            Assert.True(Visible(bay5)); Assert.True(Visible(airpit)); Assert.True(Visible(bowl)); Assert.False(Visible(airpit2));
            Assert.Equal(2, curve.Description.Tags.Length);
            Assert.Same(drawer, curve.Description.Tags[0]);
            Assert.Same(original.AcceptableValues, curve.Description.AcceptableValues);
            MapConfigVisibility.Apply(cfg, false);
            Assert.False(Visible(curve)); Assert.Equal(1.75f, curve.Value); Assert.Equal(.8f, legacy.Value);
            Assert.True(Visible(arenaPath));
            Assert.True(Visible(showNotices));
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
    public void OnlyFourMapsArePlayableAndParkedScenesNeverActivateRepairs()
    {
        Assert.Equal(new[] { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" }, ArenaMapRegistry.All.Select(m => m.Key));
        var parked = ArenaMapRegistry.Catalog.Except(ArenaMapRegistry.All).ToArray();
        Assert.Equal(10, parked.Length);
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
