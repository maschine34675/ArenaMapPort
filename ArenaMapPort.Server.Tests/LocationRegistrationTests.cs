using System.Collections.Generic;
using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Exercises ArenaMapRegistration without a live server — WP5's acceptance criteria
/// (docs/SPEC.md §B.WP5): EnsureAvailable throws on a duplicate key, and the Location
/// BuildLocation assembles has non-null, structurally valid loot fields (the exact NRE class
/// Icebreaker's own comment warns raid start hits on a null LooseLoot/StaticLoot/StaticContainers).
///
/// Multimap generalization (docs/progress/multimap-generalize.md): every case that used to be a
/// bare [Fact] against Equator's own constants is now a [Theory] over
/// <see cref="AllMaps"/> (== ArenaMapRegistry.Catalog; retained development fixtures) — this both covers Bay5's registration logic
/// automatically and keeps Equator's own cases running exactly as before, just parameterized.
/// </summary>
public class LocationRegistrationTests
{
    public static IEnumerable<object[]> AllMaps()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            yield return new object[] { map };
        }
    }

    // LocationTable's own required properties are a fixed, closed set (Bigmap, Factory4Day, ...
    // SandboxHigh, Base) — every one must be supplied to construct an instance at all, even
    // though EnsureAvailable/Register only ever touch the table's *dictionary* (a plain
    // Dictionary<string, Location> hydrated once by reflection over those properties, then freely
    // mutable — confirmed by decompiling the live SPTarkov.Server.Core.dll's
    // LocationTable.HydrateDictionary/GetDictionary).
    private static LocationTable MakeLocationTable()
    {
        static Location Stub(string id) => new() { Base = new LocationBase { Id = id } };

        return new LocationTable
        {
            Bigmap = Stub("bigmap"),
            Factory4Day = Stub("factory4_day"),
            Factory4Night = Stub("factory4_night"),
            Interchange = Stub("interchange"),
            Laboratory = Stub("laboratory"),
            Lighthouse = Stub("lighthouse"),
            RezervBase = Stub("rezervbase"),
            Shoreline = Stub("shoreline"),
            TarkovStreets = Stub("tarkovstreets"),
            Labyrinth = Stub("labyrinth"),
            Woods = Stub("woods"),
            Sandbox = Stub("sandbox"),
            SandboxHigh = Stub("sandbox_high"),
            Base = new LocationsBase(),
        };
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void EnsureAvailable_DoesNotThrow_OnFreshTable(ArenaMapDescriptor map)
    {
        var table = MakeLocationTable();

        var exception = Record.Exception(() => ArenaMapRegistration.EnsureAvailable(table, map));

        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void EnsureAvailable_Throws_WhenKeyAlreadyRegistered(ArenaMapDescriptor map)
    {
        var table = MakeLocationTable();
        table.GetDictionary().Add(map.Key, new Location { Base = new LocationBase { Id = map.Key } });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ArenaMapRegistration.EnsureAvailable(table, map));

        Assert.Contains(map.Key, exception.Message);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void EnsureAvailable_Throws_WhenIdFieldAlreadyUsedUnderADifferentKey(ArenaMapDescriptor map)
    {
        var table = MakeLocationTable();
        table.GetDictionary().Add("some_other_key", new Location
        {
            Base = new LocationBase { Id = "some_other_key", IdField = map.IdField },
        });

        Assert.Throws<InvalidOperationException>(() => ArenaMapRegistration.EnsureAvailable(table, map));
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void Register_AddsLocation_RetrievableByKey(ArenaMapDescriptor map)
    {
        var table = MakeLocationTable();
        var location = ArenaMapRegistration.BuildLocation(new LocationBase { Id = "placeholder" }, map);

        ArenaMapRegistration.Register(table, location, map);

        Assert.True(table.GetDictionary().TryGetValue(map.Key, out var stored));
        Assert.Same(location, stored);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void Register_Throws_OnSecondCall_ForTheSameKey(ArenaMapDescriptor map)
    {
        var table = MakeLocationTable();
        var first = ArenaMapRegistration.BuildLocation(new LocationBase { Id = "placeholder" }, map);
        ArenaMapRegistration.Register(table, first, map);

        var second = ArenaMapRegistration.BuildLocation(new LocationBase { Id = "placeholder" }, map);

        Assert.Throws<InvalidOperationException>(() => ArenaMapRegistration.Register(table, second, map));
    }

    // New coverage (multimap generalization): proves ArenaMapPortMod.OnLoadAsync's per-map
    // try/catch loop rests on a sound invariant — one map's presence in LocationTable must never
    // block a DIFFERENT map's own registration. This is the exact behavior the loop depends on to
    // isolate one map's failure from another's success.
    [Fact]
    public void EnsureAvailable_DoesNotThrow_ForOneMap_WhenADifferentMapIsAlreadyRegistered()
    {
        var table = MakeLocationTable();
        var equatorLocation = ArenaMapRegistration.BuildLocation(
            new LocationBase { Id = "placeholder" }, ArenaMapRegistry.Equator);
        ArenaMapRegistration.Register(table, equatorLocation, ArenaMapRegistry.Equator);

        var exception = Record.Exception(
            () => ArenaMapRegistration.EnsureAvailable(table, ArenaMapRegistry.Bay5));

        Assert.Null(exception);
    }

    [Fact]
    public void Register_BothMaps_Succeed_Independently()
    {
        var table = MakeLocationTable();
        var equatorLocation = ArenaMapRegistration.BuildLocation(
            new LocationBase { Id = "placeholder" }, ArenaMapRegistry.Equator);
        var bay5Location = ArenaMapRegistration.BuildLocation(
            new LocationBase { Id = "placeholder" }, ArenaMapRegistry.Bay5);

        ArenaMapRegistration.Register(table, equatorLocation, ArenaMapRegistry.Equator);
        ArenaMapRegistration.Register(table, bay5Location, ArenaMapRegistry.Bay5);

        Assert.True(table.GetDictionary().ContainsKey(ArenaMapRegistry.Equator.Key));
        Assert.True(table.GetDictionary().ContainsKey(ArenaMapRegistry.Bay5.Key));
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void BuildLocation_StampsIdentityConstants(ArenaMapDescriptor map)
    {
        var location = ArenaMapRegistration.BuildLocation(new LocationBase { Id = "placeholder" }, map);

        Assert.Equal(map.Key, location.Base.Id);
        Assert.Equal(map.IdField, location.Base.IdField.ToString());
        Assert.True(location.Base.DisabledForScav);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void BuildLocation_LooseLoot_IsNonNullAndEmpty(ArenaMapDescriptor map)
    {
        var location = ArenaMapRegistration.BuildLocation(new LocationBase { Id = "placeholder" }, map);

        var looseLoot = location.LooseLoot?.Value;

        Assert.NotNull(looseLoot);
        Assert.NotNull(looseLoot!.SpawnpointCount);
        Assert.Equal(0, looseLoot.SpawnpointCount!.Mean);
        Assert.Equal(0, looseLoot.SpawnpointCount.Std);
        Assert.NotNull(looseLoot.Spawnpoints);
        Assert.Empty(looseLoot.Spawnpoints!);
        Assert.NotNull(looseLoot.SpawnpointsForced);
        Assert.Empty(looseLoot.SpawnpointsForced!);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void BuildLocation_StaticLoot_IsNonNullAndEmpty(ArenaMapDescriptor map)
    {
        var location = ArenaMapRegistration.BuildLocation(new LocationBase { Id = "placeholder" }, map);

        var staticLoot = location.StaticLoot?.Value;

        Assert.NotNull(staticLoot);
        Assert.Empty(staticLoot!);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void BuildLocation_StaticContainers_IsNonNullAndEmpty(ArenaMapDescriptor map)
    {
        var location = ArenaMapRegistration.BuildLocation(new LocationBase { Id = "placeholder" }, map);

        var staticContainers = location.StaticContainers?.Value;

        Assert.NotNull(staticContainers);
        Assert.NotNull(staticContainers!.StaticWeapons);
        Assert.Empty(staticContainers.StaticWeapons!);
        Assert.NotNull(staticContainers.StaticContainers);
        Assert.Empty(staticContainers.StaticContainers!);
        Assert.NotNull(staticContainers.StaticForced);
        Assert.Empty(staticContainers.StaticForced!);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void BuildLocation_StaticAmmoAndAllExtracts_AreNonNull(ArenaMapDescriptor map)
    {
        var location = ArenaMapRegistration.BuildLocation(new LocationBase { Id = "placeholder" }, map);

        Assert.NotNull(location.StaticAmmo);
        Assert.Empty(location.StaticAmmo);
        Assert.NotNull(location.AllExtracts);
        Assert.Empty(location.AllExtracts);
    }

    // Regression coverage for the fix-cs pass (2026-09-13): the client resolves the
    // map-select display name/description via location._Id, which base.json's wire
    // "_Id" field (server-side: LocationBase.IdField) deserializes into — NOT the
    // separate "Id" wire field (the location key). ApplyLocale must key the locale
    // table by the map's IdField (the MongoId), never by Key (the location key).
    [Theory]
    [MemberData(nameof(AllMaps))]
    public void ApplyLocale_KeysByIdField_NotByLocationKey(ArenaMapDescriptor map)
    {
        var locale = new GlobalLocaleDictionary();

        var result = ArenaMapRegistration.ApplyLocale(locale, map);

        Assert.Same(locale, result);
        Assert.True(locale.ContainsKey(map.IdField + " Name"));
        Assert.True(locale.ContainsKey(map.IdField + " Description"));
        Assert.Equal(map.LocaleName, locale[map.IdField + " Name"]);
        Assert.Equal(map.LocaleDescription, locale[map.IdField + " Description"]);

        // The bug this guards against: keying by the location key instead of IdField.
        Assert.False(locale.ContainsKey(map.Key + " Name"));
        Assert.False(locale.ContainsKey(map.Key + " Description"));
    }

    [Fact]
    public void ApplyLocale_ReturnsNull_WhenGivenNull()
    {
        var result = ArenaMapRegistration.ApplyLocale(null, ArenaMapRegistry.Equator);

        Assert.Null(result);
    }

    // Regression coverage for the raid-start KeyNotFound (2026-09-14): configs/location.json
    // has per-map multiplier entries but no "default", so an unlisted map fails loot
    // generation in LocationLootGenerator.GetLooseLootMultiplierForLocation.
    [Theory]
    [MemberData(nameof(AllMaps))]
    public void ApplyLocationConfig_AddsKey_ToEveryIndexedTable(ArenaMapDescriptor map)
    {
        var staticMult = new Dictionary<string, double> { ["factory4_day"] = 1.5, ["woods"] = 1 };
        var looseMult = new Dictionary<string, double> { ["factory4_day"] = 2, ["woods"] = 1 };
        var scavMaps = new Dictionary<string, ScavRaidTimeLocationSettings?>
        {
            ["factory4_day"] = new() { MinStaticLootPercent = 40 },
        };

        ArenaMapRegistration.ApplyLocationConfig(staticMult, looseMult, scavMaps, s => s with { }, map);

        Assert.Equal(1.5, staticMult[map.Key]);
        Assert.Equal(2, looseMult[map.Key]);
        var scav = scavMaps[map.Key];
        Assert.NotNull(scav);
        Assert.NotSame(scavMaps[ArenaMapRegistration.TemplateKey], scav);
        Assert.Equal(40, scav!.MinStaticLootPercent);
    }

    // bots-stage1 — APBS compatibility: Arena keys map to a vanilla stand-in, everything else passes.
    [Fact]
    public void ApbsCompat_RemapsArenaKeysOnly()
    {
        var count = ApbsCompat.RegisterStandIns(ArenaMapRegistry.All);

        Assert.Equal(ArenaMapRegistry.All.Count, count);
        Assert.Equal("Interchange", ApbsCompat.Remap(ArenaMapRegistry.Equator.Key));
        Assert.Equal("factory4_day", ApbsCompat.Remap(ArenaMapRegistry.Bay5.Key));
        Assert.Equal("factory4_day", ApbsCompat.Remap(ArenaMapRegistry.AirPit.Key)); // user decision 2026-10-11: Factory stand-in
        Assert.Equal("bigmap", ApbsCompat.Remap("bigmap"));
        Assert.Equal("RezervBase", ApbsCompat.Remap("RezervBase"));
        Assert.Null(ApbsCompat.Remap(null));
        Assert.Null(ApbsCompat.FindMapRangeWeightsType()); // APBS is not part of the test process
    }

    // exfils-stage2 — identity locale entries for every authored exit, nothing for maps without exits,
    // never overwriting an existing translation.
    [Fact]
    public void ApplyExitLocale_AddsIdentityEntries_OnlyForAuthoredExits()
    {
        var locale = new GlobalLocaleDictionary
        {
            ["Equator North Gate"] = "Nordtor",
            ["Bay5 North Loading Bay"] = "Nordladerampe",
        };

        var result = ArenaMapRegistration.ApplyExitLocale(locale, ArenaMapRegistry.Equator);
        Assert.Same(locale, ArenaMapRegistration.ApplyExitLocale(locale, ArenaMapRegistry.Bay5));

        Assert.Same(locale, result);
        Assert.Equal("Nordtor", locale["Equator North Gate"]);           // kept
        Assert.Equal("Equator South Gate", locale["Equator South Gate"]); // identity
        Assert.Equal("Nordladerampe", locale["Bay5 North Loading Bay"]); // kept
        Assert.Equal("Bay5 South Container Lane", locale["Bay5 South Container Lane"]); // identity
        Assert.Equal(4, locale.Count);
        // Bowl stays a geometry-only parked map without authored exits (AirPit gained its exits with plan item 6).
        Assert.Empty(ArenaMapRegistry.Bowl.Exits);
        Assert.Same(locale, ArenaMapRegistration.ApplyExitLocale(locale, ArenaMapRegistry.Bowl));
        Assert.Equal(4, locale.Count); // an unauthored map adds nothing
        Assert.Null(ArenaMapRegistration.ApplyExitLocale(null, ArenaMapRegistry.Equator));
    }

    [Fact]
    public void ExitNameMismatches_ReportsEachSideOnlyOnce()
    {
        var mismatches = ArenaMapRegistration.ExitNameMismatches(
            new[] { "Equator North Gate", "Gate 3", "Gate 3", null }, ArenaMapRegistry.Equator);

        Assert.Equal(new[] { "Equator South Gate (descriptor only)", "Gate 3 (base.json only)" }, mismatches);
        Assert.Empty(ArenaMapRegistration.ExitNameMismatches(
            ArenaMapRegistry.Equator.Exits.Select(e => (string?)e.Name), ArenaMapRegistry.Equator));
        Assert.Equal(new[] { "Bay5 North Loading Bay (descriptor only)", "Bay5 South Container Lane (descriptor only)" },
            ArenaMapRegistration.ExitNameMismatches(null, ArenaMapRegistry.Bay5));
        Assert.Empty(ArenaMapRegistration.ExitNameMismatches(
            ArenaMapRegistry.Bay5.Exits.Select(e => (string?)e.Name), ArenaMapRegistry.Bay5));
        Assert.Empty(ArenaMapRegistry.Bowl.Exits);
        Assert.Empty(ArenaMapRegistration.ExitNameMismatches(null, ArenaMapRegistry.Bowl));
        Assert.Equal(new[] { "Air Pit North Gate (descriptor only)", "Air Pit South Gate (descriptor only)" },
            ArenaMapRegistration.ExitNameMismatches(null, ArenaMapRegistry.AirPit));
    }

    // bots-stage1 — ApplyBrainTable gives an Arena key the stand-in map's brain-weight row, once.
    [Fact]
    public void ApplyBrainTable_CopiesTheStandInRow_ToTheLowercasedKey()
    {
        var table = new Dictionary<string, Dictionary<string, int>>
        {
            ["factory4_day"] = new() { ["assault"] = 1 },
            ["interchange"] = new() { ["assault"] = 2, ["pmcBot"] = 1 },
        };

        Assert.True(ArenaMapRegistration.ApplyBrainTable(table, ArenaMapRegistry.Equator));
        Assert.True(ArenaMapRegistration.ApplyBrainTable(table, ArenaMapRegistry.Bay5));

        Assert.Same(table["interchange"], table[ArenaMapRegistry.Equator.Key.ToLowerInvariant()]);
        Assert.Same(table["factory4_day"], table[ArenaMapRegistry.Bay5.Key.ToLowerInvariant()]);
        Assert.False(ArenaMapRegistration.ApplyBrainTable(table, ArenaMapRegistry.Equator)); // already present
        Assert.Equal(4, table.Count);
    }

    [Fact]
    public void ApplyBrainTable_LeavesTheTableAlone_WhenTheStandInIsMissing()
    {
        var table = new Dictionary<string, Dictionary<string, int>>();

        Assert.False(ArenaMapRegistration.ApplyBrainTable(table, ArenaMapRegistry.Equator));
        Assert.Empty(table);
    }

    // bots-stage3 — every PMC brain table (pmcbear, pmcusec) gets the stand-in row for an Arena key.
    [Fact]
    public void ApplyPmcBrainTables_WritesTheStandInRow_IntoEveryPmcTable()
    {
        var pmcType = new Dictionary<string, Dictionary<string, Dictionary<string, double>>>
        {
            ["pmcbear"] = new() { ["interchange"] = new() { ["pmcBEAR"] = 10 }, ["factory4_day"] = new() { ["pmcBEAR"] = 5 } },
            ["pmcusec"] = new() { ["interchange"] = new() { ["pmcUSEC"] = 10 }, ["factory4_day"] = new() { ["pmcUSEC"] = 5 } },
        };

        Assert.Equal(2, ArenaMapRegistration.ApplyPmcBrainTables(pmcType, ArenaMapRegistry.Equator));
        Assert.Same(pmcType["pmcbear"]["interchange"], pmcType["pmcbear"][ArenaMapRegistry.Equator.Key.ToLowerInvariant()]);
        Assert.Same(pmcType["pmcusec"]["interchange"], pmcType["pmcusec"][ArenaMapRegistry.Equator.Key.ToLowerInvariant()]);
        Assert.Equal(0, ArenaMapRegistration.ApplyPmcBrainTables(pmcType, ArenaMapRegistry.Equator)); // idempotent
        Assert.Equal(0, ArenaMapRegistration.ApplyPmcBrainTables<Dictionary<string, double>>(null, ArenaMapRegistry.Bay5));
    }

    // bots-stage1 — ApplyBotCap mirrors a bot-enabled map's BotMax into BotConfig.MaxBotCap.
    [Theory]
    [MemberData(nameof(AllMaps))]
    public void ApplyBotCap_LeavesTheTableAlone_ForAMapWithoutBots(ArenaMapDescriptor map)
    {
        var caps = new Dictionary<string, int> { ["default"] = 18 };

        ArenaMapRegistration.ApplyBotCap(caps, 0, map);

        Assert.Single(caps);
        Assert.Equal(18, caps["default"]);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void ApplyBotCap_WritesTheLowercasedKey_ForAMapWithBots(ArenaMapDescriptor map)
    {
        var caps = new Dictionary<string, int> { ["default"] = 18 };

        ArenaMapRegistration.ApplyBotCap(caps, 8, map);

        Assert.Equal(2, caps.Count);
        Assert.Equal(8, caps[map.Key.ToLowerInvariant()]);
        Assert.Equal(18, caps["default"]);
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void ApplyLocationConfig_FallsBack_WhenTemplateEntriesAreMissing(ArenaMapDescriptor map)
    {
        var staticMult = new Dictionary<string, double>();
        var looseMult = new Dictionary<string, double>();
        var scavMaps = new Dictionary<string, ScavRaidTimeLocationSettings?>();

        ArenaMapRegistration.ApplyLocationConfig(staticMult, looseMult, scavMaps, s => s with { }, map);

        Assert.Equal(1, staticMult[map.Key]);
        Assert.Equal(1, looseMult[map.Key]);
        Assert.True(scavMaps.ContainsKey(map.Key));
        Assert.Null(scavMaps[map.Key]);
    }
}
