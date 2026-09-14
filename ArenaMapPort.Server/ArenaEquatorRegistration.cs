// Registration mechanism and loot-LazyLoad shape ported (MIT) from
// Manimal.Icebreaker.Server.IcebreakerLocationRegistration / IcebreakerMod
// (ManimalIcebreaker-main, icebreaker-server/IcebreakerLocationRegistration.cs:11-29,
// icebreaker-server/IcebreakerMod.cs:97-204) — see THIRD-PARTY-NOTICES.md.
//
// SPT 4.1.5's LocationTable.GetDictionary() returns the live, persistent dictionary
// (confirmed identical HydrateDictionary/GetDictionary logic between the 4.1.0
// reference package and the live 4.1.5 binary, analysis/critic.md §1 — re-confirmed
// this pass by decompiling the live D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll
// directly with ilspycmd), so a plain Dictionary.Add() during OnLoadAsync is
// sufficient — no reflection-based "closed record" workaround needed.
using ArenaMapPort.Shared;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;

namespace ArenaMapPort.Server;

/// <summary>
/// Validates and performs the arena_equator location identity registration against
/// SPT's <see cref="LocationTable"/>, and assembles the runtime <see cref="Location"/>
/// from a deserialized base.json. Kept DI-free and static so the ArenaMapPort.Server.Tests
/// project can exercise it without a live server.
/// </summary>
public static class ArenaEquatorRegistration
{
    public const string Key = ArenaEquatorLocation.Key;
    public const string Id = ArenaEquatorLocation.IdField;

    // base.json is generated from factory4_day (tools/server/make_base_json.py).
    public const string TemplateKey = "factory4_day";

    // Locale display text (SPEC §E.5). Placeholder English, spike-only.
    public const string LocaleName = "Arena: Equator (spike)";
    public const string LocaleDescription = "Internal test build — geometry/collision/NavMesh spike.";

    /// <summary>
    /// Throws if <see cref="Key"/> is already registered, or if any already-registered
    /// location's identity (IdField or Id) already matches ours. Direct port of
    /// IcebreakerLocationRegistration.EnsureAvailable.
    /// </summary>
    public static void EnsureAvailable(LocationTable table)
    {
        var locations = table.GetDictionary();
        if (locations.ContainsKey(Key))
        {
            throw new InvalidOperationException(
                $"ArenaMapPort location key '{Key}' is already registered.");
        }

        foreach (var pair in locations)
        {
            if (pair.Value?.Base is { } existing &&
                (existing.IdField.ToString() == Id ||
                 string.Equals(existing.Id, Key, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"ArenaMapPort location identity is already used by '{pair.Key}'.");
            }
        }
    }

    /// <summary>
    /// Validates <paramref name="location"/>'s identity against our constants, re-checks
    /// availability, then adds it to the table. Direct port of
    /// IcebreakerLocationRegistration.Register.
    /// </summary>
    public static void Register(LocationTable table, Location location)
    {
        if (location.Base?.Id != Key || location.Base.IdField.ToString() != Id)
        {
            throw new InvalidOperationException(
                "ArenaMapPort base identity does not match ArenaEquatorLocation constants.");
        }

        EnsureAvailable(table);
        // SPT 4.1.5: GetDictionary() returns persistent storage, consulted by
        // GetLocation and LocationController.GenerateAll.
        table.GetDictionary().Add(Key, location);
    }

    /// <summary>
    /// Builds the runtime <see cref="Location"/> from a deserialized base.json: stamps
    /// the §0 identity constants, sets DisabledForScav, and fills every loot field with
    /// an empty-but-non-null shape (loot is out of scope for the spike, SPEC decision
    /// #8). Pure — no DI, no file/network I/O — so it is directly unit testable.
    /// </summary>
    public static Location BuildLocation(LocationBase newBase)
    {
        ArgumentNullException.ThrowIfNull(newBase);

        newBase.Id = ArenaEquatorLocation.Key;
        newBase.IdField = ArenaEquatorLocation.IdField;
        newBase.DisabledForScav = true;

        var location = new Location { Base = newBase };

        // Loot must be non-null even though it's out of scope for the spike: raid-start
        // loot generation (LocationLifecycleService.GenerateLocationAndLoot ->
        // LocationLootGenerator.GenerateLocationLoot, called with generateLoot=true by
        // default on every normal raid start) NREs on a null LooseLoot/StaticLoot/
        // StaticContainers. Icebreaker's own "loot-free" branch (IcebreakerMod.cs:
        // 196-204) is the exact shape ported here.
        location.LooseLoot = new LazyLoad<LooseLoot>(() => new LooseLoot
        {
            SpawnpointCount = new SpawnpointCount { Mean = 0, Std = 0 },
            Spawnpoints = [],
            SpawnpointsForced = [],
        }, cacheValue: false);

        location.StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(
            () => new Dictionary<MongoId, StaticLootDetails>(), cacheValue: true);

        location.StaticContainers = new LazyLoad<StaticContainerDetails>(() => new StaticContainerDetails
        {
            // Field names verified against the live 4.1.5 StaticContainerDetails shape
            // (ilspycmd decompile of D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll,
            // this pass) — StaticWeapons/StaticContainers/StaticForced, NOT the
            // ObjectsSpawnPoints/Containers placeholder names SPEC §B.WP5 step 3 flagged
            // as "TBD, verify at implementation time".
            StaticWeapons = [],
            StaticContainers = [],
            StaticForced = [],
        }, cacheValue: true);

        location.StaticAmmo = new Dictionary<string, IEnumerable<StaticAmmoDetails>>();
        location.AllExtracts = [];

        return location;
    }

    /// <summary>
    /// Adds <see cref="Key"/> to the location-keyed server config tables that the raid
    /// path reads with a bare indexer. Pattern ported (MIT) from IcebreakerMod.cs:231-239.
    /// Without these, /client/match/local/start throws KeyNotFound ('default' was not
    /// present) in LocationLootGenerator.GetLooseLootMultiplierForLocation — configs/
    /// location.json has no "default" key for either multiplier table — and scav raid-time
    /// adjustment indexes ScavRaidTimeSettings.Maps the same way
    /// (RaidTimeAdjustmentService.GetMapSettings). The other per-map tables on that path
    /// (MaxBotCap, LootMaxSpawnLimits, LooseLootBlacklist, CustomPmcWaves) fall back safely.
    /// Pure — no DI — so it is directly unit testable.
    /// </summary>
    public static void ApplyLocationConfig(
        Dictionary<string, double> staticLootMultiplier,
        Dictionary<string, double> looseLootMultiplier,
        Dictionary<string, ScavRaidTimeLocationSettings?> scavRaidTimeMaps,
        Func<ScavRaidTimeLocationSettings, ScavRaidTimeLocationSettings> clone)
    {
        // Loot tables are empty for the spike, so the multiplier only has to exist;
        // Factory's value keeps any user override consistent with the template map.
        staticLootMultiplier[Key] = staticLootMultiplier.GetValueOrDefault(TemplateKey, 1);
        looseLootMultiplier[Key] = looseLootMultiplier.GetValueOrDefault(TemplateKey, 1);

        // A null entry is also accepted (GetMapSettings falls back to defaults with a
        // warning), so a missing Factory entry still leaves the key present.
        scavRaidTimeMaps[Key] = scavRaidTimeMaps.GetValueOrDefault(TemplateKey) is { } factory
            ? clone(factory)
            : null;
    }

    /// <summary>
    /// Stamps the map-select locale keys into a deserialized locale dictionary and
    /// returns it (mutate-in-place, matching <see cref="LazyLoad{T}.AddTransformer"/>'s
    /// signature so this can be passed straight to it). Pure — no DI, directly unit
    /// testable.
    ///
    /// Keyed by <see cref="Id"/> (<c>ArenaEquatorLocation.IdField</c>, the MongoId), NOT
    /// by <see cref="Key"/> (the location key, "arena_equator") — the client's
    /// `LocationInfoPanel`/`LocationButton`/`MatchMaker*` screens all resolve display
    /// text via `location.LocalizedName` / `(location._Id + " Description").Localized()`,
    /// where `LocationSettings._Id` is deserialized from base.json's wire `"_Id"` field —
    /// which is `LocationBase.IdField` server-side
    /// (`[JsonPropertyName("_Id")] public MongoId IdField`), a DISTINCT property from
    /// `[JsonPropertyName("Id")] public required string Id`. Confirmed by decompiling
    /// both the live SPTarkov.Server.Core 4.1.5 DLL and the client's
    /// JsonType/LocationSettings.cs + EFT.UI/LocationInfoPanel.cs (see SPEC §E.5 / §0).
    /// Matches Icebreaker's own working pattern (`IcebreakerMod.cs:252/258`, keyed by
    /// `IcebreakerLocation.Id`, itself a MongoId).
    /// </summary>
    public static GlobalLocaleDictionary? ApplyLocale(GlobalLocaleDictionary? locale)
    {
        if (locale is null)
        {
            return locale;
        }

        locale[Id + " Name"] = LocaleName;
        locale[Id + " Description"] = LocaleDescription;
        return locale;
    }
}

// Locale keys (SPEC §E.5, §B.WP5 step 6) — LocationInfoPanel reads "<_Id> Name" /
// "<_Id> Description" from the locale table, not Base.Name/Base.Description
// (ref-manimal.md §1's decompiled citation) — "<_Id>" here is IdField (the MongoId),
// NOT the location key; see ArenaEquatorRegistration.ApplyLocale's doc comment for the
// decompiled evidence. Registers at TypePriority
// Preload + 90000, matching IcebreakerMod.cs's own class attribute — high enough
// that LocationTable is already hydrated by other mods' PostLoad-order location
// edits, but registration itself only needs LocationTable to exist, not be
// finalized, since we .Add() a brand-new key.
[Injectable(TypePriority = OnLoadOrder.Preload + 90000)]
public class ArenaMapPortMod(
    LocationTable locationTable,
    LocaleTable localeTable,
    LocationConfig locationConfig,
    ICloner cloner,
    JsonUtil jsonUtil,
    ISptLogger<ArenaMapPortMod> logger) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modDir = Path.GetDirectoryName(typeof(ArenaMapPortMod).Assembly.Location)!;
        var basePath = Path.Combine(modDir, "db", "base.json");

        LocationBase? newBase;
        try
        {
            newBase = await jsonUtil.DeserializeFromFileAsync<LocationBase>(basePath, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Error($"[ArenaMapPort] {basePath} failed to parse — location NOT enabled: {e.Message}");
            return;
        }

        if (newBase is null)
        {
            logger.Error($"[ArenaMapPort] could not load {basePath} — location NOT enabled");
            return;
        }

        // Availability is checked BEFORE building the Location so a duplicate-key
        // conflict surfaces as a clear startup error naming the conflicting key
        // (EnsureAvailable's own message), not a silent overwrite further down.
        ArenaEquatorRegistration.EnsureAvailable(locationTable);
        var location = ArenaEquatorRegistration.BuildLocation(newBase);

        ArenaEquatorRegistration.ApplyLocationConfig(
            locationConfig.StaticLootMultiplier,
            locationConfig.LooseLootMultiplier,
            locationConfig.ScavRaidTimeSettings.Maps,
            settings => cloner.Clone(settings)!);

        // TODO(bots-milestone): bot cap (BotConfig.MaxBotCap, falls back to "default") /
        // NonMaps — skipped for the spike (SPEC decision #8, bots out of scope;
        // analysis/ref-manimal.md §1's punch-list has the retail levers Icebreaker uses).

        try
        {
            foreach (var (_, lazyLocale) in localeTable.Global)
            {
                lazyLocale.AddTransformer(ArenaEquatorRegistration.ApplyLocale);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Warning($"[ArenaMapPort] location locale registration failed: {e.Message}");
        }

        // Banners: base.json ships Banners: [] (SPEC §B.WP5 step 7 / §E.4) so there is
        // nothing to wire through ImageRouter — Icebreaker's own documented landmine
        // (a Banners array with unresolvable MongoIds -> [UNHANDLED] server log + an
        // empty client error dialog at the menu) does not apply here.

        ArenaEquatorRegistration.Register(locationTable, location);
        logger.Success("[Manimal-ArenaMapPort] Arena: Equator (spike) registered as an independent location");
    }
}
