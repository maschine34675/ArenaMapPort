using ArenaMapPort.Server;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Exercises ArenaEquatorRegistration without a live server — WP5's acceptance
/// criteria (docs/SPEC.md §B.WP5): EnsureAvailable throws on a duplicate key, and the
/// Location BuildLocation assembles has non-null, structurally valid loot fields (the
/// exact NRE class Icebreaker's own comment warns raid start hits on a null
/// LooseLoot/StaticLoot/StaticContainers).
/// </summary>
public class LocationRegistrationTests
{
    // LocationTable's own required properties are a fixed, closed set (Bigmap,
    // Factory4Day, ... SandboxHigh, Base) — every one must be supplied to construct an
    // instance at all, even though EnsureAvailable/Register only ever touch the table's
    // *dictionary* (a plain Dictionary<string, Location> hydrated once by reflection
    // over those properties, then freely mutable — confirmed by decompiling the live
    // SPTarkov.Server.Core.dll's LocationTable.HydrateDictionary/GetDictionary).
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

    [Fact]
    public void EnsureAvailable_DoesNotThrow_OnFreshTable()
    {
        var table = MakeLocationTable();

        var exception = Record.Exception(() => ArenaEquatorRegistration.EnsureAvailable(table));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureAvailable_Throws_WhenKeyAlreadyRegistered()
    {
        var table = MakeLocationTable();
        table.GetDictionary().Add(ArenaEquatorRegistration.Key,
            new Location { Base = new LocationBase { Id = ArenaEquatorRegistration.Key } });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ArenaEquatorRegistration.EnsureAvailable(table));

        Assert.Contains(ArenaEquatorRegistration.Key, exception.Message);
    }

    [Fact]
    public void EnsureAvailable_Throws_WhenIdFieldAlreadyUsedUnderADifferentKey()
    {
        var table = MakeLocationTable();
        table.GetDictionary().Add("some_other_key", new Location
        {
            Base = new LocationBase { Id = "some_other_key", IdField = ArenaEquatorRegistration.Id },
        });

        Assert.Throws<InvalidOperationException>(() => ArenaEquatorRegistration.EnsureAvailable(table));
    }

    [Fact]
    public void Register_AddsLocation_RetrievableByKey()
    {
        var table = MakeLocationTable();
        var location = ArenaEquatorRegistration.BuildLocation(new LocationBase { Id = "placeholder" });

        ArenaEquatorRegistration.Register(table, location);

        Assert.True(table.GetDictionary().TryGetValue(ArenaEquatorRegistration.Key, out var stored));
        Assert.Same(location, stored);
    }

    [Fact]
    public void Register_Throws_OnSecondCall_ForTheSameKey()
    {
        var table = MakeLocationTable();
        var first = ArenaEquatorRegistration.BuildLocation(new LocationBase { Id = "placeholder" });
        ArenaEquatorRegistration.Register(table, first);

        var second = ArenaEquatorRegistration.BuildLocation(new LocationBase { Id = "placeholder" });

        Assert.Throws<InvalidOperationException>(() => ArenaEquatorRegistration.Register(table, second));
    }

    [Fact]
    public void BuildLocation_StampsIdentityConstants()
    {
        var location = ArenaEquatorRegistration.BuildLocation(new LocationBase { Id = "placeholder" });

        Assert.Equal(ArenaEquatorRegistration.Key, location.Base.Id);
        Assert.Equal(ArenaEquatorRegistration.Id, location.Base.IdField.ToString());
        Assert.True(location.Base.DisabledForScav);
    }

    [Fact]
    public void BuildLocation_LooseLoot_IsNonNullAndEmpty()
    {
        var location = ArenaEquatorRegistration.BuildLocation(new LocationBase { Id = "placeholder" });

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

    [Fact]
    public void BuildLocation_StaticLoot_IsNonNullAndEmpty()
    {
        var location = ArenaEquatorRegistration.BuildLocation(new LocationBase { Id = "placeholder" });

        var staticLoot = location.StaticLoot?.Value;

        Assert.NotNull(staticLoot);
        Assert.Empty(staticLoot!);
    }

    [Fact]
    public void BuildLocation_StaticContainers_IsNonNullAndEmpty()
    {
        var location = ArenaEquatorRegistration.BuildLocation(new LocationBase { Id = "placeholder" });

        var staticContainers = location.StaticContainers?.Value;

        Assert.NotNull(staticContainers);
        Assert.NotNull(staticContainers!.StaticWeapons);
        Assert.Empty(staticContainers.StaticWeapons!);
        Assert.NotNull(staticContainers.StaticContainers);
        Assert.Empty(staticContainers.StaticContainers!);
        Assert.NotNull(staticContainers.StaticForced);
        Assert.Empty(staticContainers.StaticForced!);
    }

    [Fact]
    public void BuildLocation_StaticAmmoAndAllExtracts_AreNonNull()
    {
        var location = ArenaEquatorRegistration.BuildLocation(new LocationBase { Id = "placeholder" });

        Assert.NotNull(location.StaticAmmo);
        Assert.Empty(location.StaticAmmo);
        Assert.NotNull(location.AllExtracts);
        Assert.Empty(location.AllExtracts);
    }

    // Regression coverage for the fix-cs pass (2026-09-13): the client resolves the
    // map-select display name/description via location._Id, which base.json's wire
    // "_Id" field (server-side: LocationBase.IdField) deserializes into — NOT the
    // separate "Id" wire field (the location key). ApplyLocale must key the locale
    // table by ArenaEquatorRegistration.Id (== ArenaEquatorLocation.IdField, the
    // MongoId), never by Key (the "arena_equator" location key).
    [Fact]
    public void ApplyLocale_KeysByIdField_NotByLocationKey()
    {
        var locale = new GlobalLocaleDictionary();

        var result = ArenaEquatorRegistration.ApplyLocale(locale);

        Assert.Same(locale, result);
        Assert.True(locale.ContainsKey(ArenaEquatorRegistration.Id + " Name"));
        Assert.True(locale.ContainsKey(ArenaEquatorRegistration.Id + " Description"));
        Assert.Equal(ArenaEquatorRegistration.LocaleName, locale[ArenaEquatorRegistration.Id + " Name"]);
        Assert.Equal(ArenaEquatorRegistration.LocaleDescription,
            locale[ArenaEquatorRegistration.Id + " Description"]);

        // The bug this guards against: keying by the location key instead of IdField.
        Assert.False(locale.ContainsKey(ArenaEquatorRegistration.Key + " Name"));
        Assert.False(locale.ContainsKey(ArenaEquatorRegistration.Key + " Description"));
    }

    [Fact]
    public void ApplyLocale_ReturnsNull_WhenGivenNull()
    {
        var result = ArenaEquatorRegistration.ApplyLocale(null);

        Assert.Null(result);
    }
}
