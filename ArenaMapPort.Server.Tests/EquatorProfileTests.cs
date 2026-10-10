#nullable disable
using ArenaMapPort.Client;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests;

[Collection("MapPort client lifecycle")]
public sealed class EquatorProfileTests : IDisposable
{
    static readonly Harmony LifecycleHarmony = new("mapport.tests.equator-profiles");
    public EquatorProfileTests()
    {
        EquatorProfiles.Install(LifecycleHarmony);
        EquatorProfiles.BeginLoad(null, EDateTime.CURR);
        SceneManager.Scenes.Clear();
    }
    public void Dispose()
    {
        EquatorProfiles.BeginLoad(null, EDateTime.CURR);
        SceneManager.Scenes.Clear();
    }

    public enum Tone { Native, Filmic }
    public enum Ambient { Off, Custom }
    public enum Reflection { Rig, Off }

    // Acceptance recipe independently captured from the approved 2026-10-07 config inventory.
    public static IEnumerable<object[]> Recipes()
    {
        yield return new object[] { "Lighting (arena_equator)", "KeepFillLight", false, false };
        yield return new object[] { "Lighting (arena_equator)", "GroundSmoothness", 1f, 1f };
        yield return new object[] { "Lighting (arena_equator)", "ReflectionIntensity", 0.85f, 1f };
        yield return new object[] { "Lighting (arena_equator)", "AmbientIntensity", 0.5f, 4f };
        yield return new object[] { "Lighting (arena_equator)", "IndoorFloorSmoothness", 0.5f, 0.5f };
        yield return new object[] { "Lighting (arena_equator)", "IndoorReflectionStrength", 0.95f, 1f };
        yield return new object[] { "Lighting (arena_equator)", "IndoorSurfaceSmoothness", 0.5f, 0.5f };
        yield return new object[] { "Lighting (arena_equator)", "IndoorSurfaceReflectionStrength", 0.2f, 0.2f };
        yield return new object[] { "Lighting (arena_equator)", "ReflectiveSpecularCap", 0.3f, 1f };
        yield return new object[] { "Lighting (arena_equator)", "AmbientColorMultiplier", 0.5f, 0.5f };
        yield return new object[] { "Lighting (arena_equator)", "GlobalLightIntensityMultiplier", 0.5f, 0.5f };
        yield return new object[] { "Lighting (arena_equator)", "SunIntensityMultiplier", 0.2f, 0.2f };
        yield return new object[] { "Lighting (arena_equator)", "SunColorWhiteBlend", 0.1f, 0.1f };
        yield return new object[] { "Lighting (arena_equator)", "FogDensity", 0f, 0f };
        yield return new object[] { "Lighting (arena_equator)", "FillLightIntensity", 0.2f, 0.2f };
        yield return new object[] { "Lighting (arena_equator)", "NightSunMultiplier", 0f, 0f };
        yield return new object[] { "Lighting (arena_equator)", "BloomThreshold", 2.1f, 2f };
        yield return new object[] { "Lighting (arena_equator)", "BloomIntensity", 0.35f, 0.35f };
        yield return new object[] { "Lighting (arena_equator)", "AnamorphicFlare", true, true };
        yield return new object[] { "Lighting (arena_equator)", "AnamorphicFlareHDR", true, true };
        yield return new object[] { "Lighting (arena_equator)", "AnamorphicFlareIntensity", 0.4f, 0.3f };
        yield return new object[] { "Lighting (arena_equator)", "AnamorphicFlareLength", 3.5f, 4.2f };
        yield return new object[] { "Lighting (arena_equator)", "AnamorphicFlareThreshold", 0.155f, 0.17f };
        yield return new object[] { "Lighting (arena_equator)", "AnamorphicFlareTint", new Color(171/255f,220/255f,255/255f,191/255f), new Color(255/255f,38/255f,143/255f,204/255f) };
        yield return new object[] { "Lighting (arena_equator)", "ExposureCompensationEV", 0f, 0.3f };
        yield return new object[] { "Lighting (arena_equator)", "ExposureRangeMultiplier", 1f, 1f };
        yield return new object[] { "Lighting (arena_equator)", "ExposureSpeedMultiplier", 1f, 1f };
        yield return new object[] { "Lighting (arena_equator)", "TonemapProfile", Tone.Filmic, Tone.Filmic };
        yield return new object[] { "Lighting (arena_equator)", "TonemapCustomCurve", true, true };
        yield return new object[] { "Lighting (arena_equator)", "TonemapCurve", new Vector3(7.0f,0.3499999940395355f,0.699999988079071f), new Vector3(7.0f,0.3499999940395355f,0.699999988079071f) };
        yield return new object[] { "Lighting (arena_equator)", "TonemapSecondaryCurve", new Vector3(-0.019999999552965164f,0.18000000715255737f,0.0f), new Vector3(-0.019999999552965164f,0.18000000715255737f,0.0f) };
        yield return new object[] { "Lighting (arena_equator)", "NativeMultiFlare", true, true };
        yield return new object[] { "Lighting (arena_equator)", "NativeMultiFlareIntensity", 0.5f, 0.6f };
        yield return new object[] { "Lighting (arena_equator)", "HandsAmbientHighlightMultiplier", 1f, 1f };
        yield return new object[] { "LightingLab (arena_equator)", "AmbientOverride", Ambient.Off, Ambient.Custom };
        yield return new object[] { "LightingLab (arena_equator)", "AmbientCustomColor", new Color(128/255f,128/255f,128/255f,255/255f), new Color(2/255f,19/255f,26/255f,255/255f) };
        yield return new object[] { "LightingLab (arena_equator)", "GlobalReflection", Reflection.Rig, Reflection.Rig };
        yield return new object[] { "LightingLab (arena_equator)", "SSRFactor", 1f, 1f };
        yield return new object[] { "LightingLab (arena_equator)", "WaterLevelOverrideEnabled", false, false };
        yield return new object[] { "LightingLab (arena_equator)", "WaterLevelOverrideValue", 0.6f, 0.6f };
        yield return new object[] { "LightingLab (arena_equator)", "RainOff", false, false };
        yield return new object[] { "LightingLab (arena_equator)", "GroundSmoothnessMultiplier", 1f, 1f };
        yield return new object[] { "LightingLab (arena_equator)", "SunIntensityMultiplier", 1f, 1f };
    }

    [Theory]
    [MemberData(nameof(Recipes))]
    public void EveryRecipeRetainsIndependentDayAndNight(string section, string key, object dayDefault, object nightDefault)
    {
        var method = typeof(EquatorProfileTests).GetMethod(nameof(CheckRecipe), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.MakeGenericMethod(dayDefault.GetType()).Invoke(null, new[] { section, key, dayDefault, nightDefault });
    }

    static void CheckRecipe<T>(string section, string key, T dayDefault, T nightDefault)
    {
        var cfg = new ConfigFile();
        var legacy = cfg.Bind(section, key, dayDefault, "Legacy");
        legacy.Value = nightDefault;
        foreach (var selection in new[] { EDateTime.CURR, EDateTime.PAST, EDateTime.CURR })
        {
            EquatorProfiles.BeginLoad("arena_equator", selection);
            Assert.Equal(selection == EDateTime.CURR ? dayDefault : nightDefault, EquatorProfiles.Value(legacy));
            Assert.Equal(nightDefault, legacy.Value);
        }
        Assert.Equal(3, cfg.Count());
    }

    [Fact]
    public void SavedNewProfileValuesWinAndLegacyEditsCannotOverwriteThem()
    {
        var cfg = new ConfigFile();
        var day = cfg.Bind("Lighting (arena_equator Day)", "AmbientIntensity", .5f, "Saved"); day.Value = .75f;
        var night = cfg.Bind("Lighting (arena_equator Night)", "AmbientIntensity", 4f, "Saved"); night.Value = 3f;
        var legacy = cfg.Bind("Lighting (arena_equator)", "AmbientIntensity", .5f, "Legacy"); legacy.Value = 9f;
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        Assert.Equal(3f, EquatorProfiles.Value(legacy));
        legacy.Value = 5f;
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.CURR);
        Assert.Equal(.75f, EquatorProfiles.Value(legacy));
        Assert.Equal(3f, night.Value);
        Assert.Equal(5f, legacy.Value);
    }

    [Fact]
    public void ActiveReadsStayIsolatedWhenEitherProfileFiresSubscribedChanges()
    {
        var cfg = new ConfigFile();
        var legacy = cfg.Bind("Lighting (arena_equator)", "AmbientIntensity", .5f, "Legacy");
        var day = EquatorProfiles.ForSection(legacy, "Lighting (arena_equator Day)");
        var night = EquatorProfiles.ForSection(legacy, "Lighting (arena_equator Night)");
        var observed = new List<float>();
        // A caller may already have resolved an entry before installing its subscriptions.
        EquatorProfiles.Subscribe(day, (_, _) => observed.Add(EquatorProfiles.Value(legacy)));
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.CURR);
        night.Value = 3f; day.Value = .8f;
        Assert.Equal(new[] { .5f, .8f }, observed);
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        day.Value = .7f; night.Value = 2f;
        Assert.Equal(new[] { .5f, .8f, 3f, 2f }, observed);
        legacy.Value = 6f;
        Assert.Equal(4, observed.Count);
    }

    [Fact]
    public void HdrSiblingsUseEditedSectionIndependentlyOfActiveRaid()
    {
        var cfg = new ConfigFile();
        var curve = cfg.Bind("Lighting (arena_equator)", "TonemapCurve", new Vector3(7f, .35f, .7f), "Curve");
        var secondary = cfg.Bind("Lighting (arena_equator)", "TonemapSecondaryCurve", new Vector3(-.02f, .18f, 0), "Secondary");
        var custom = cfg.Bind("Lighting (arena_equator)", "TonemapCustomCurve", true, "Custom");
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        const string edited = "Lighting (arena_equator Day)";
        EquatorProfiles.ForSection(curve, edited).Value = new Vector3(2, 3, 4);
        EquatorProfiles.ForSection(secondary, edited).Value = new Vector3(5, 6, 7);
        EquatorProfiles.ForSection(custom, edited).Value = false;
        Assert.Equal(new Vector3(7f, .35f, .7f), EquatorProfiles.Value(curve));
        Assert.Equal(new Vector3(-.02f, .18f, 0), EquatorProfiles.Value(secondary));
        Assert.True(EquatorProfiles.Value(custom));
        Assert.Same(curve, EquatorProfiles.ForSection(curve, "Unrelated"));
    }

    [Fact]
    public void UnprofiledAndBay5SettingsPassThroughWithOriginalSubscriptions()
    {
        var cfg = new ConfigFile();
        foreach (var section in new[] { "Lighting (arena_bay5)", "Camera", "Audio (arena_equator)" })
        {
            var entry = cfg.Bind(section, "AmbientIntensity", .3f, "Common");
            var changes = 0;
            EquatorProfiles.Subscribe(entry, (_, _) => changes++);
            foreach (var variant in new[] { EDateTime.CURR, EDateTime.PAST })
            {
                EquatorProfiles.BeginLoad("arena_equator", variant);
                Assert.Same(entry, EquatorProfiles.Entry(entry));
                Assert.Same(entry, EquatorProfiles.ForSection(entry, "Lighting (arena_equator Night)"));
            }
            entry.Value = .7f;
            Assert.Equal(.7f, EquatorProfiles.Value(entry));
            Assert.Equal(1, changes);
        }
        Assert.Equal(3, cfg.Count());
    }

    [Fact]
    public void ProfileMetadataAndVisibilityPreserveLegacyAndCommonData()
    {
        var cfg = Plugin.Instance.Config;
        var tag = new object();
        var description = new ConfigDescription("Test control", new AcceptableValueRange<float>(0, 10), tag);
        var legacy = cfg.Bind("Lighting (arena_equator)", "AmbientIntensity", .5f, description);
        legacy.Value = 4f;
        var day = EquatorProfiles.ForSection(legacy, "Lighting (arena_equator Day)");
        var night = EquatorProfiles.ForSection(legacy, "Lighting (arena_equator Night)");
        var common = cfg.Bind("Lighting (arena_equator)", "Donor", "Reserve", "Common");
        var sain = cfg.Bind("Bots (arena_equator)", "SainLocation", "Interchange", "Legacy alias");
        var daySain = cfg.Bind("Bots (arena_equator Day)", "SainLocation", "Factory", "Day alias");
        MapConfigVisibility.Apply(cfg, true); MapConfigVisibility.Apply(cfg, true);
        Assert.Contains(legacy.Description.Tags, t => t is System.ComponentModel.BrowsableAttribute { Browsable: false });
        Assert.Contains(sain.Description.Tags, t => t is System.ComponentModel.BrowsableAttribute { Browsable: false });
        foreach (var entry in new ConfigEntryBase[] { day, night, common, daySain })
            Assert.DoesNotContain(entry.Description.Tags, t => t is System.ComponentModel.BrowsableAttribute { Browsable: false });
        Assert.Equal(4f, legacy.Value);
        Assert.Equal("Reserve", common.Value);
        Assert.Equal("Interchange", sain.Value);
        Assert.Same(day.Description.AcceptableValues, night.Description.AcceptableValues);
        Assert.NotSame(day.Description.Tags, night.Description.Tags);
        day.Value = 99f;
        Assert.Equal(10f, day.Value);
        Assert.Equal(4f, night.Value);
        Assert.Equal(new[] { tag }, description.Tags);
        day.Value = .5f;
    }

    [Fact]
    public void NightOnlyRecipeSeedsCapturedValuesWithoutReplacingSavedValues()
    {
        var cfg = new ConfigFile();
        var saved = cfg.Bind("Night neon (arena_equator)", "HazeStrength", .16f, "Saved"); saved.Value = .21f;
        var rebound = cfg.Bind("Night neon (arena_equator)", "HazeStrength", EquatorProfiles.NightDefault("HazeStrength", .16f), "Recipe");
        Assert.Equal(.21f, rebound.Value);
        Assert.Equal(.2398122f, EquatorProfiles.NightDefault("HazeStrength", .16f));
        Assert.Equal(1f, EquatorProfiles.NightDefault("SmokeDensity", .65f));
        Assert.Equal(3f, EquatorProfiles.NightDefault("FlickerFrequency", 1f));
        Assert.Equal(.01f, EquatorProfiles.NightDefault("DistanceHaze", .002f));
        Assert.Equal(123f, EquatorProfiles.NightDefault("FutureKey", 123f));
    }

    [Fact]
    public void BindAllCreatesUntouchedTypesAndNeverReseedsExistingProfiles()
    {
        var cfg = Plugin.Instance.Config;
        var enabled = cfg.Bind("Lighting (arena_equator)", "NativeMultiFlare", true, "Enabled");
        var intensity = cfg.Bind("Lighting (arena_equator)", "NativeMultiFlareIntensity", .5f, "Intensity");
        EquatorProfiles.BindAll();
        var night = cfg.Get<float>("Lighting (arena_equator Night)", "NativeMultiFlareIntensity");
        Assert.True(cfg.Get<bool>("Lighting (arena_equator Day)", "NativeMultiFlare").Value);
        Assert.Equal(.6f, night.Value);
        night.Value = .75f;
        EquatorProfiles.BindAll();
        Assert.Equal(.75f, night.Value);
        Assert.Equal(.5f, intensity.Value);
        Assert.True(enabled.Value);
        night.Value = .6f;
    }

    [Fact]
    public async Task LoaderCapturesSelectionBeforeNativeSceneCallbacksAndConfirmsIt()
    {
        foreach (var selected in new[] { EDateTime.CURR, EDateTime.PAST, EDateTime.CURR })
        {
            var expected = selected == EDateTime.PAST ? EquatorProfile.Night : EquatorProfile.Day;
            var app = new TarkovApplication
            {
                CurrentRaidSettings = new() { SelectedLocation = new() { Id = "arena_equator" }, SelectedDateTime = selected },
                DuringLoad = () =>
                {
                    Assert.Equal(expected, EquatorProfiles.Current);
                    SceneManager.Load(new Scene(ArenaMapPort.Shared.ArenaMapRegistry.Equator.ScriptsSceneName));
                }
            };
            var world = await app.LoadMapAndData();
            Assert.NotNull(world);
            EquatorProfiles.ConfirmSelection("arena_equator", selected);
            Assert.Equal(expected, EquatorProfiles.Current);
            EquatorNightNeon.Enabled = !EquatorNightNeon.Enabled;
            Assert.Equal(expected, EquatorProfiles.Current);
            SceneManager.Unload(ArenaMapPort.Shared.ArenaMapRegistry.Equator.ScriptsSceneName);
            Assert.Equal(EquatorProfile.None, EquatorProfiles.Current);
        }
    }

    [Fact]
    public void PreviousSceneUnloadCannotClearNextLoadSelection()
    {
        var sceneName = ArenaMapPort.Shared.ArenaMapRegistry.Equator.ScriptsSceneName;
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.CURR);
        SceneManager.Load(new Scene(sceneName));
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        SceneManager.Unload(sceneName);
        Assert.Equal(EquatorProfile.Night, EquatorProfiles.Current);
        SceneManager.Load(new Scene(sceneName));
        SceneManager.Unload(sceneName);
        Assert.Equal(EquatorProfile.None, EquatorProfiles.Current);
    }

    [Theory]
    [InlineData("arena_bay5")]
    [InlineData("bigmap")]
    [InlineData(null)]
    public void NewUnrelatedLoadClearsProfileAndLateWeatherConfirmationWorks(string map)
    {
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        EquatorProfiles.BeginLoad(map, EDateTime.PAST);
        Assert.Equal(EquatorProfile.None, EquatorProfiles.Current);
        EquatorProfiles.ConfirmSelection("arena_equator", EDateTime.CURR);
        Assert.Equal(EquatorProfile.Day, EquatorProfiles.Current);
        EquatorProfiles.ConfirmSelection("bigmap", EDateTime.PAST);
        Assert.Equal(EquatorProfile.None, EquatorProfiles.Current);
    }

    [Fact]
    public async Task CanceledFaultedAndEmptyLoadsClearOnlyTheirOwnGeneration()
    {
        var generation = EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        await Assert.ThrowsAsync<TaskCanceledException>(() => EquatorProfiles.ObserveLoad(Task.FromCanceled<object>(new CancellationToken(true)), generation));
        Assert.Equal(EquatorProfile.None, EquatorProfiles.Current);
        generation = EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EquatorProfiles.ObserveLoad(Task.FromException<object>(new InvalidOperationException()), generation));
        Assert.Equal(EquatorProfile.None, EquatorProfiles.Current);
        generation = EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        await EquatorProfiles.ObserveLoad(Task.FromResult<object>(null), generation);
        Assert.Equal(EquatorProfile.None, EquatorProfiles.Current);
        var old = EquatorProfiles.BeginLoad("arena_equator", EDateTime.CURR);
        EquatorProfiles.BeginLoad("arena_equator", EDateTime.PAST);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EquatorProfiles.ObserveLoad(Task.FromException<object>(new InvalidOperationException()), old));
        Assert.Equal(EquatorProfile.Night, EquatorProfiles.Current);
    }
}
