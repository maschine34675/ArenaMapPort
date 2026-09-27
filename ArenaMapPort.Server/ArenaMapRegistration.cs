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
//
// Multimap generalization (docs/progress/multimap-generalize.md): renamed from
// ArenaEquatorRegistration; every method now takes an ArenaMapDescriptor parameter instead of
// reading class-level Equator-only consts, so the identical registration logic serves every
// registered map (ArenaMapPort.Shared.ArenaMapRegistry.All), not just Equator. Every value this
// file used to hardcode (Key/Id/LocaleName/LocaleDescription) now lives on the descriptor
// instead — for the Equator case those are the exact same values, byte-for-byte (see
// ArenaMapDescriptor.cs's own comment on ArenaMapRegistry.Equator).
using ArenaMapPort.Shared;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;

namespace ArenaMapPort.Server;

/// <summary>
/// Validates and performs an ArenaMapPort location's identity registration against SPT's
/// <see cref="LocationTable"/>, and assembles the runtime <see cref="Location"/> from a
/// deserialized base.json. Kept DI-free and static so the ArenaMapPort.Server.Tests project can
/// exercise it without a live server.
/// </summary>
public static class ArenaMapRegistration
{
    // base.json is generated from factory4_day (tools/server/make_base_json.py). Shared donor for
    // every map's loot-multiplier/ScavRaidTimeSettings fallback — not itself Equator-specific.
    public const string TemplateKey = "factory4_day";

    /// <summary>
    /// Throws if <paramref name="map"/>'s key is already registered, or if any already-registered
    /// location's identity (IdField or Id) already matches it. Direct port of
    /// IcebreakerLocationRegistration.EnsureAvailable.
    /// </summary>
    public static void EnsureAvailable(LocationTable table, ArenaMapDescriptor map)
    {
        var locations = table.GetDictionary();
        if (locations.ContainsKey(map.Key))
        {
            throw new InvalidOperationException(
                $"ArenaMapPort location key '{map.Key}' is already registered.");
        }

        foreach (var pair in locations)
        {
            if (pair.Value?.Base is { } existing &&
                (existing.IdField.ToString() == map.IdField ||
                 string.Equals(existing.Id, map.Key, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"ArenaMapPort location identity is already used by '{pair.Key}'.");
            }
        }
    }

    /// <summary>
    /// Validates <paramref name="location"/>'s identity against <paramref name="map"/>, re-checks
    /// availability, then adds it to the table. Direct port of
    /// IcebreakerLocationRegistration.Register.
    /// </summary>
    public static void Register(LocationTable table, Location location, ArenaMapDescriptor map)
    {
        if (location.Base?.Id != map.Key || location.Base.IdField.ToString() != map.IdField)
        {
            throw new InvalidOperationException(
                $"ArenaMapPort base identity does not match {map.Key}'s descriptor.");
        }

        EnsureAvailable(table, map);
        // SPT 4.1.5: GetDictionary() returns persistent storage, consulted by
        // GetLocation and LocationController.GenerateAll.
        table.GetDictionary().Add(map.Key, location);
    }

    /// <summary>
    /// Builds the runtime <see cref="Location"/> from a deserialized base.json: stamps
    /// <paramref name="map"/>'s identity, sets DisabledForScav, and fills every loot field with
    /// an empty-but-non-null shape (loot is out of scope for the spike, SPEC decision #8). Pure —
    /// no DI, no file/network I/O — so it is directly unit testable.
    /// </summary>
    public static Location BuildLocation(LocationBase newBase, ArenaMapDescriptor map)
    {
        ArgumentNullException.ThrowIfNull(newBase);

        newBase.Id = map.Key;
        newBase.IdField = map.IdField;
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
    /// Adds <paramref name="map"/>'s key to the location-keyed server config tables that the raid
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
        Func<ScavRaidTimeLocationSettings, ScavRaidTimeLocationSettings> clone,
        ArenaMapDescriptor map)
    {
        // Loot tables are empty for the spike, so the multiplier only has to exist;
        // Factory's value keeps any user override consistent with the template map.
        staticLootMultiplier[map.Key] = staticLootMultiplier.GetValueOrDefault(TemplateKey, 1);
        looseLootMultiplier[map.Key] = looseLootMultiplier.GetValueOrDefault(TemplateKey, 1);

        // A null entry is also accepted (GetMapSettings falls back to defaults with a
        // warning), so a missing Factory entry still leaves the key present.
        scavRaidTimeMaps[map.Key] = scavRaidTimeMaps.GetValueOrDefault(TemplateKey) is { } factory
            ? clone(factory)
            : null;
    }

    /// <summary>
    /// bots-stage1: mirrors a bot-enabled map's base.json BotMax into BotConfig.MaxBotCap (see the call
    /// site's comment for why). Pure — no DI — so it is directly unit testable.
    /// </summary>
    /// <summary>
    /// exfils-stage2 — exit names double as locale keys (vanilla: "Gate 3" → "Gate 3"); an identity entry per
    /// runtime-authored exit keeps the extraction list from ever showing an unresolved key. Same
    /// <see cref="LazyLoad{T}.AddTransformer"/> shape as <see cref="ApplyLocale"/>; never overwrites an existing key.
    /// </summary>
    public static GlobalLocaleDictionary? ApplyExitLocale(GlobalLocaleDictionary? locale, ArenaMapDescriptor map)
    {
        if (locale is null)
        {
            return locale;
        }
        foreach (var exit in map.Exits)
        {
            if (!locale.ContainsKey(exit.Name))
            {
                locale[exit.Name] = exit.Name;
            }
        }
        return locale;
    }

    /// <summary>
    /// exfils-stage2 — the names base.json ships and the names the client authors must be one set: an exit only
    /// in base.json stays NotPresent on the client (no scene object), one only in the descriptor never loads
    /// settings. Returns every name present on exactly one side, for a startup warning.
    /// </summary>
    public static IReadOnlyList<string> ExitNameMismatches(IEnumerable<string?>? baseExitNames, ArenaMapDescriptor map)
    {
        var fromBase = new HashSet<string>((baseExitNames ?? []).Where(n => n is not null)!, StringComparer.Ordinal);
        var fromDescriptor = new HashSet<string>(map.Exits.Select(e => e.Name), StringComparer.Ordinal);
        return fromBase.Except(fromDescriptor).Select(n => n + " (base.json only)")
            .Concat(fromDescriptor.Except(fromBase).Select(n => n + " (descriptor only)"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// bots-stage3 — pmc.json <c>pmcType</c> is {"pmcbear": {map: {brain: weight}}, "pmcusec": {...}}; every inner table gets
    /// the stand-in row. Returns the number of rows written.
    /// </summary>
    public static int ApplyPmcBrainTables<TRow>(IDictionary<string, Dictionary<string, TRow>>? pmcType, ArenaMapDescriptor map)
    {
        if (pmcType is null)
        {
            return 0;
        }
        var written = 0;
        foreach (var (_, perMap) in pmcType)
        {
            if (perMap is not null && ApplyBrainTable(perMap, map))
            {
                written++;
            }
        }
        return written;
    }

    /// <summary>The SPT bot.json map key whose brain weights an Arena map borrows (bot.json keys are lowercase).</summary>
    public static string BrainStandInFor(ArenaMapDescriptor map) =>
        map.Key == ArenaMapRegistry.Equator.Key ? "interchange" : "factory4_day";

    /// <summary>
    /// bots-stage1 — copies the stand-in map's row of a per-map brain-weight table (assaultBrainType,
    /// playerScavBrainType) to the Arena map's lowercased key. Returns true when a row was written; false when
    /// the map already has one or the stand-in row is missing (nothing to copy, no throw).
    /// </summary>
    public static bool ApplyBrainTable<TRow>(IDictionary<string, TRow> table, ArenaMapDescriptor map)
    {
        var key = map.Key.ToLowerInvariant();
        if (table.ContainsKey(key))
        {
            return false;
        }
        if (!table.TryGetValue(BrainStandInFor(map), out var row))
        {
            return false;
        }
        table[key] = row;
        return true;
    }

    public static void ApplyBotCap(Dictionary<string, int> maxBotCap, int botMax, ArenaMapDescriptor map)
    {
        if (botMax > 0)
        {
            // BotController.GetBotCap lower-cases its lookup (BotController.cs:281-291); every current Key is
            // lower-case already, normalising keeps a future map's spelling from silently missing the cap.
            maxBotCap[map.Key.ToLowerInvariant()] = botMax;
        }
    }

    /// <summary>
    /// Stamps <paramref name="map"/>'s map-select locale keys into a deserialized locale
    /// dictionary and returns it (mutate-in-place, matching
    /// <see cref="LazyLoad{T}.AddTransformer"/>'s signature so this can be passed straight to
    /// it, once per map, via a closure — see <see cref="ArenaMapPortMod.OnLoadAsync"/>). Pure —
    /// no DI, directly unit testable.
    ///
    /// Keyed by <see cref="ArenaMapDescriptor.IdField"/> (the MongoId), NOT by
    /// <see cref="ArenaMapDescriptor.Key"/> (the location key, e.g. "arena_equator") — the
    /// client's `LocationInfoPanel`/`LocationButton`/`MatchMaker*` screens all resolve display
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
    public static GlobalLocaleDictionary? ApplyLocale(GlobalLocaleDictionary? locale, ArenaMapDescriptor map)
    {
        if (locale is null)
        {
            return locale;
        }

        locale[map.IdField + " Name"] = map.LocaleName;
        locale[map.IdField + " Description"] = map.LocaleDescription;
        return locale;
    }
}

// Locale keys (SPEC §E.5, §B.WP5 step 6) — LocationInfoPanel reads "<_Id> Name" /
// "<_Id> Description" from the locale table, not Base.Name/Base.Description
// (ref-manimal.md §1's decompiled citation) — "<_Id>" here is IdField (the MongoId),
// NOT the location key; see ArenaMapRegistration.ApplyLocale's doc comment for the
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
    BotConfig botConfig,
    PmcConfig pmcConfig,
    ICloner cloner,
    JsonUtil jsonUtil,
    ImageRouter imageRouter,
    ISptLogger<ArenaMapPortMod> logger) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modDir = Path.GetDirectoryName(typeof(ArenaMapPortMod).Assembly.Location)!;
        // PLAN-TWO-MAPS item 10 — extract_banners.py writes straight into this same deployed
        // folder's "banners" subdirectory (dist/SPT_Runtime/user/mods/ArenaMapPort/banners/),
        // sitting alongside "db" rather than under it, specifically so it needs no csproj
        // Content/DeployToDist glob change (that folder is never touched by `dotnet build`).
        var bannersDir = Path.Combine(modDir, "banners");

        // Multimap generalization (docs/progress/multimap-generalize.md): one map's bad/missing
        // base.json must not block any OTHER map's registration (both Equator's and Bay5's
        // base.json exist on disk today — docs/progress/bay5-port.md — but the isolation still
        // matters for whichever map breaks next). Each map is independently try/caught, mirroring
        // the existing per-hook independence already used client-side (HarmonyPatches.cs).
        // Equator's own db path is unchanged
        // ("db/base.json" — ArenaMapRegistry.Equator.DbBaseJsonPath — not "db/arena_equator/base.json")
        // so its already-deployed dist/ layout keeps working without a migration.
        foreach (var map in ArenaMapRegistry.All)
        {
            var basePath = Path.Combine(modDir, "db", map.DbBaseJsonPath);
            try
            {
                LocationBase? newBase;
                try
                {
                    newBase = await jsonUtil.DeserializeFromFileAsync<LocationBase>(basePath, cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.Error($"[ArenaMapPort] {map.Key}: {basePath} failed to parse — location NOT enabled: {e.Message}");
                    continue;
                }

                if (newBase is null)
                {
                    logger.Error($"[ArenaMapPort] {map.Key}: could not load {basePath} — location NOT enabled");
                    continue;
                }

                // Availability is checked BEFORE building the Location so a duplicate-key
                // conflict surfaces as a clear startup error naming the conflicting key
                // (EnsureAvailable's own message), not a silent overwrite further down.
                ArenaMapRegistration.EnsureAvailable(locationTable, map);
                var location = ArenaMapRegistration.BuildLocation(newBase, map);

                ArenaMapRegistration.ApplyLocationConfig(
                    locationConfig.StaticLootMultiplier,
                    locationConfig.LooseLootMultiplier,
                    locationConfig.ScavRaidTimeSettings.Maps,
                    settings => cloner.Clone(settings)!,
                    map);

                // bots-stage1 (docs/progress/bots-stage1.md): a map whose base.json ships BotMax > 0 gets
                // that value into BotConfig.MaxBotCap too. PostDbLoadService.AdjustLocationBotValues runs
                // from GameCallbacks (OnLoadOrder 200000, i.e. AFTER this Preload+90000 hook) and writes
                // Location.BotMax only for keys present in that dictionary — configs/bot.json has no
                // arena_* key, so without this the cap would silently fall back to the raw JSON value.
                // A map without bots (BotMax 0, every map but Equator today) is left alone.
                ArenaMapRegistration.ApplyBotCap(botConfig.MaxBotCap, newBase.BotMax, map);
                // bots-stage1 (docs/progress/bots-stage1.md, "Host test 8") — SPT's client CustomAiPatch picks a
                // brain per ASSAULT/player-scav bot from BotConfig.AssaultBrainType[<map id lowercased>] /
                // PlayerScavBrainType[...] (spt-custom AIBrainSpawnWeightAdjustment.cs:55-66) and throws
                // "the assault cache still doesn't contain data" for a map the table does not know. Give every
                // Arena map the stand-in map's row (Equator → interchange, others → factory4_day).
                ArenaMapRegistration.ApplyBrainTable(botConfig.AssaultBrainType, map);
                ArenaMapRegistration.ApplyBrainTable(botConfig.PlayerScavBrainType, map);
                // bots-stage3 (docs/progress/bots-stage3.md): the same lookup for PMCs — pmc.json pmcType[pmcbear|pmcusec][map]
                // is what spt-custom's GetPmcWildSpawnType indexes without a guard (KeyNotFoundException per PMC otherwise).
                ArenaMapRegistration.ApplyPmcBrainTables(pmcConfig.PmcType, map);

                // exfils-stage2 (docs/progress/exfils-stage2.md): base.json exits and the client's authored
                // positions (ArenaMapRegistry.<Map>.Exits) must name the same set — regenerate base.json from
                // tools/maps/<key>.exits.json when this warns.
                var exitMismatches = ArenaMapRegistration.ExitNameMismatches(newBase.Exits?.Select(e => e.Name), map);
                if (exitMismatches.Count > 0)
                {
                    logger.Warning($"[ArenaMapPort] {map.Key}: exit names differ between base.json and the client table — " +
                                   string.Join(", ", exitMismatches));
                }

                // PLAN-TWO-MAPS item 10 (docs/progress/two-maps-item-10-banners.md): base.json
                // keeps shipping Banners: [] for every map, unchanged (SPEC §B.WP5 step 7 / §E.4)
                // — Equator/Bay5 get a real entry assigned here in memory instead, and only when
                // ArenaMapBanners.Resolve finds the extracted image file already on disk. This
                // runs in its own try/catch (docs/PLAN-TWO-MAPS.md §10's own instruction) so a
                // banner-wiring failure can never block this map's own Register() call below —
                // Icebreaker's own documented landmine (an unresolvable Banners entry -> an
                // "[UNHANDLED]" server log line + an empty client error dialog at the menu) is
                // exactly why ArenaMapBanners.Resolve refuses to hand back a Banner at all when
                // no file backs it, rather than relying on this catch to paper over that case.
                try
                {
                    var resolvedBanner = ArenaMapBanners.Resolve(
                        map, bannersDir, message => logger.Warning($"[ArenaMapPort] {message}"));
                    if (resolvedBanner is { } banner)
                    {
                        newBase.Banners = new List<Banner> { banner.Banner };
                        imageRouter.AddRoute(banner.RouteKey, banner.FilePath);
                        logger.Success(
                            $"[ArenaMapPort] {map.Key}: location-select banner wired ({banner.RouteKey} -> {banner.FilePath})");
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.Warning(
                        $"[ArenaMapPort] {map.Key}: banner wiring failed (location-select falls back to the default art): {e.Message}");
                }

                ArenaMapRegistration.Register(locationTable, location, map);
                logger.Success($"[Manimal-ArenaMapPort] {map.LocaleName} registered as an independent location");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.Error($"[ArenaMapPort] {map.Key} registration failed: {e.Message}");
                // continue to the next map — one map's failure must not block the others.
            }
        }

        try
        {
            foreach (var (_, lazyLocale) in localeTable.Global)
            {
                foreach (var map in ArenaMapRegistry.All)
                {
                    lazyLocale.AddTransformer(locale => ArenaMapRegistration.ApplyLocale(locale, map));
                    lazyLocale.AddTransformer(locale => ArenaMapRegistration.ApplyExitLocale(locale, map)); // exfils-stage2
                    lazyLocale.AddTransformer(locale => ArenaMapBanners.ApplyLocale(locale, map)); // two-maps item 10
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Warning($"[ArenaMapPort] location locale registration failed: {e.Message}");
        }
    
        FixedRaidTimeWeather.TryInstall(
            message => logger.Info("[ArenaMapPort] " + message),
            message => logger.Warning("[ArenaMapPort] " + message));

        // bots-stage1 (docs/progress/bots-stage1.md, "APBS") — once, after every map is registered: a
        // soft Harmony patch on APBS's per-map weapon-range lookup so bot generation on Arena maps does
        // not throw "Map '<key>' not found". No-op without APBS. See ApbsCompat.cs.
        ApbsCompat.TryInstall(
            ArenaMapRegistry.All,
            message => logger.Info("[ArenaMapPort] " + message),
            message => logger.Warning("[ArenaMapPort] " + message));
}
}
