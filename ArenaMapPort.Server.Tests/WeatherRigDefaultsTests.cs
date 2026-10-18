#nullable disable
using System.ComponentModel;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Xunit;

namespace ArenaMapPort.Server.Tests;

// PLAN-AIRPIT item 10. WeatherRig.BindConfig and ArenaLightingRepairs.BindConfig cannot be compiled into this
// project (they sit on EFT and Unity types), so every map-dependent first-run default lives in the pure table
// MapLightingDefaults and is pinned here. BepInEx persists whatever a bound key defaults to, so the Equator and
// Bay5 rows below are the values their released builds shipped; AirPit is a new section with no saved keys on
// either test install (read-only grep 2026-10-12), so its rows are free to be chosen but are now pinned too. Bowl
// (PLAN-BOWL item 9, 2026-10-18) is the same kind of new section.
[Collection("MapPort client lifecycle")]
public sealed class WeatherRigDefaultsTests
{
    sealed record Row(string Donor, float ZeroLevel, float HeightFalloff, bool ReserveTextureBudget,
        bool UseDonorCameraPrefab, float ReflectionIntensity, float AmbientIntensity, float IndoorFloorSmoothness,
        int IndoorCoverPercent, float IndoorReflectionStrength, bool IndoorSurfaceRepair, float ReflectiveSpecularCap)
    {
        internal static Row Of(string key)
        {
            var v = MapLightingDefaults.Rig(key);
            return new Row(v.Donor, v.ZeroLevel, v.HeightFalloff, v.ReserveTextureBudget, v.UseDonorCameraPrefab,
                v.ReflectionIntensity, v.AmbientIntensity, v.IndoorFloorSmoothness, v.IndoorCoverPercent,
                v.IndoorReflectionStrength, v.IndoorSurfaceRepair, v.ReflectiveSpecularCap);
        }
    }

    // The values the pre-item-10 WeatherRig.BindConfig expressions produced for these keys.
    static readonly Row Equator = new("Reserve_Base_Scripts", -4.4f, 0.025f, true, true, 0.85f, 0.5f, 0.5f, 30, 0.95f, false, 0.3f);
    static readonly Row Bay5 = new("Reserve_Base_Scripts", -8.2f, 0.025f, true, true, 1f, 1f, 0.5f, 50, 1f, true, 1f);
    static readonly Row Neutral = new("Off", 0f, 0.029f, false, false, 1f, 1f, 1f, 50, 1f, false, 1f);

    [Fact]
    public void EquatorAndBay5DefaultsAreTheReleasedOnes()
    {
        Assert.Equal(Equator, Row.Of(ArenaMapRegistry.Equator.Key));
        Assert.Equal(Bay5, Row.Of(ArenaMapRegistry.Bay5.Key));
    }

    [Fact]
    public void AirPitJoinsTheReserveRigWithBay5sNeutralTuningAndEquatorsZeroLevel()
    {
        var airPit = Row.Of(ArenaMapRegistry.AirPit.Key);
        // User decision 2026-10-11: donor rig Reserve_Base_Scripts (Full); the camera prefab comes from the same donor.
        Assert.Equal("Reserve_Base_Scripts", airPit.Donor);
        Assert.True(airPit.UseDonorCameraPrefab);
        Assert.True(airPit.ReserveTextureBudget);
        Assert.Equal(0.025f, airPit.HeightFalloff);
        // 4.4 m below the walkable ground; its 20 original spawn markers sit at y 0.08..0.14 (Equator 0.03..0.12).
        Assert.Equal(-4.4f, airPit.ZeroLevel);
        Assert.Equal(0.5f, airPit.IndoorFloorSmoothness); // every rig-active map shares the indoor floor baseline
        // Everything Equator tuned on the host, and Bay5's mixed-mesh repair, stay map-specific.
        Assert.Equal(1f, airPit.ReflectionIntensity);
        Assert.Equal(1f, airPit.AmbientIntensity);
        Assert.Equal(50, airPit.IndoorCoverPercent);
        Assert.Equal(1f, airPit.IndoorReflectionStrength);
        Assert.Equal(1f, airPit.ReflectiveSpecularCap);
        Assert.False(airPit.IndoorSurfaceRepair);
        Assert.Equal(airPit, Bay5 with { ZeroLevel = -4.4f, IndoorSurfaceRepair = false });
    }

    [Fact]
    public void BowlJoinsTheReserveRigLikeAirPitWithItsOwnSpawnHeightZeroLevel()
    {
        var bowl = Row.Of(ArenaMapRegistry.Bowl.Key);
        // PLAN-BOWL item 9: donor rig Reserve_Base_Scripts and the same neutral tuning as AirPit; its 20 original spawn
        // markers sit at y 0.047 (the same rule as Equator/AirPit: 4.4 m below the walkable ground).
        Assert.Equal("Reserve_Base_Scripts", bowl.Donor);
        Assert.True(bowl.UseDonorCameraPrefab);
        Assert.True(bowl.ReserveTextureBudget);
        Assert.Equal(-4.4f, bowl.ZeroLevel);
        Assert.Equal(Row.Of(ArenaMapRegistry.AirPit.Key), bowl);
    }

    [Fact]
    public void EveryOtherCatalogMapKeepsDonorOffAndNeutralValues()
    {
        var parked = ArenaMapRegistry.Catalog.Except(ArenaMapRegistry.All).ToArray();
        Assert.Equal(10, parked.Length);
        foreach (var map in parked) Assert.Equal(Neutral, Row.Of(map.Key));
        Assert.Equal(Neutral, Row.Of("bigmap"));
        Assert.Equal(Neutral, Row.Of(null));
    }

    [Fact]
    public void OnlyTheReleasedMapsAreRigActive()
    {
        var active = ArenaMapRegistry.Catalog.Where(m => MapLightingDefaults.Rig(m.Key).Donor != MapLightingDefaults.OffDonor)
            .Select(m => m.Key);
        Assert.Equal(ArenaMapRegistry.All.Select(m => m.Key).Order(), active.Order());
    }

    [Fact]
    public void SunKeysAreBoundOnlyForAMapWithAnAuthoredSun()
    {
        Assert.True(MapLightingDefaults.BindsAuthoredSunKeys(ArenaMapRegistry.Equator));
        Assert.False(MapLightingDefaults.BindsAuthoredSunKeys(ArenaMapRegistry.Bay5));
        Assert.False(MapLightingDefaults.BindsAuthoredSunKeys(ArenaMapRegistry.AirPit));
        Assert.False(MapLightingDefaults.BindsAuthoredSunKeys(ArenaMapRegistry.Bowl)); // no Directional light in its imported scenes
        // The condition is the descriptor's data, not a map list: Equator is the only map with SunPaths.
        foreach (var map in ArenaMapRegistry.Catalog)
            Assert.Equal(map.SunPaths.Length > 0, MapLightingDefaults.BindsAuthoredSunKeys(map));
        Assert.Equal(new[] { ArenaMapRegistry.Equator.Key },
            ArenaMapRegistry.Catalog.Where(MapLightingDefaults.BindsAuthoredSunKeys).Select(m => m.Key));
        // AirPit really has nothing to scale: no sun scene or path in descriptor, none to mirror in the sidecar.
        Assert.Empty(ArenaMapRegistry.AirPit.SunPaths);
        Assert.Equal(string.Empty, ArenaMapRegistry.AirPit.SunScene);
    }

    static bool Visible(ConfigEntryBase entry) => entry.Description.Tags.OfType<BrowsableAttribute>().Last().Browsable;

    [Fact]
    public void AirPitLightingSectionsAreHiddenForPlayersAndVisibleInDeveloperMode()
    {
        // The real key names the released AirPit binds (WeatherRig, ArenaLightingRepairs, HdrImageTuning, BloomTuning,
        // LightingLab, RainCoverRepairs): one entry per owner section is enough, the rule is by section suffix.
        var cfg = new ConfigFile();
        var entries = new ConfigEntryBase[]
        {
            cfg.Bind("Lighting (arena_airpit)", "Donor", "Reserve_Base_Scripts", "Rig donor"),
            cfg.Bind("Lighting (arena_airpit)", "ZeroLevelOverride", -4.4f, "Rig ZeroLevel"),
            cfg.Bind("Lighting (arena_airpit)", "ExposureCompensationEV", 0f, "HDR"),
            cfg.Bind("Lighting (arena_airpit)", "BloomThreshold", 1.2f, "Bloom"),
            cfg.Bind("LightingLab (arena_airpit)", "ReflectionStrength", 1f, "Lab"),
            cfg.Bind("RainCoverLab (arena_airpit)", "CoverAll", false, "Rain lab"),
        };
        var parked = cfg.Bind("Lighting (arena_airpit2)", "Donor", "Off", "Air Pit 2.0 stays parked");
        MapConfigVisibility.Apply(cfg, false);
        foreach (var entry in entries) Assert.False(Visible(entry), entry.Definition.Section + "/" + entry.Definition.Key);
        MapConfigVisibility.Apply(cfg, true);
        foreach (var entry in entries) Assert.True(Visible(entry), entry.Definition.Section + "/" + entry.Definition.Key);
        Assert.False(Visible(parked));
    }
}
