using System;
using System.Collections.Generic;
using System.Reflection;
using ArenaMapPort.Shared;
using HarmonyLib;
using SPTarkov.Reflection.Patching;

namespace ArenaMapPort.Server;

/// <summary>
/// bots-stage1 (docs/progress/bots-stage1.md, "APBS") — soft compatibility with acidphantasm's
/// ProgressiveBotSystem (APBS) server mod, present on the GPU host, absent on the vanilla testbed.
///
/// APBS's weapon generator picks a long/short-range weapon pool per map through
/// <c>ModConfig.Config.GeneralConfig.MapRangeWeighting[RaidInformation.RaidLocation]</c>;
/// <c>ProgressiveBotSystem.Models.MapRangeWeights</c> is a class with one property per vanilla map and a
/// string indexer whose <c>switch</c> ends in <c>throw new KeyNotFoundException("Map '…' not found.")</c>
/// (APBS decompile, MapRangeWeights.cs:99-115). On a ported map every bot generation therefore fails
/// inside SPT's <c>BotController.TryGenerateSingleBot</c> ("Failed to generate bot #n (…): Map
/// 'arena_equator' not found.", 1320 times in the first host raid, 2026-09-19 19:5x) and no bot ever
/// reaches the client. The lookup key is the raid's location id verbatim, so neither a config entry nor
/// a server-side alias can help — the switch is compiled in.
///
/// Fix: a Harmony prefix on that indexer getter (SPT's own <see cref="AbstractPatch"/> machinery, the same
/// one APBS uses) rewrites an Arena key to a vanilla stand-in before the switch runs — Interchange (a
/// mall) for Equator, Factory for every other ported map. Everything is resolved by name at load time:
/// without APBS nothing is patched and one info line says so; a changed APBS shape logs a warning.
/// </summary>
public static class ApbsCompat
{
    public const string MapRangeWeightsTypeName = "ProgressiveBotSystem.Models.MapRangeWeights";
    public const string PatchHarmonyId = "com.maschine.ArenaMapPort.apbs";

    private static readonly Dictionary<string, string> StandIns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The vanilla map APBS should treat <paramref name="map"/> as (its own key spelling).</summary>
    public static string StandInFor(ArenaMapDescriptor map) =>
        map.Key == ArenaMapRegistry.Equator.Key ? "Interchange" : "factory4_day";

    /// <summary>Fills the key → stand-in table. Pure apart from the static table; unit tested.</summary>
    public static int RegisterStandIns(IEnumerable<ArenaMapDescriptor> maps)
    {
        var count = 0;
        foreach (var map in maps)
        {
            StandIns[map.Key] = StandInFor(map);
            count++;
        }
        return count;
    }

    /// <summary>What the prefix does: an Arena key becomes its stand-in, anything else passes through.</summary>
    public static string? Remap(string? key) =>
        key is not null && StandIns.TryGetValue(key, out var standIn) ? standIn : key;

    public static Type? FindMapRangeWeightsType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(MapRangeWeightsTypeName, throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // a dynamic/reflection-only assembly that refuses GetType is simply not APBS
            }
        }
        return null;
    }

    /// <summary>Installs the patch when APBS is loaded. Returns true when the patch is active.</summary>
    public static bool TryInstall(IEnumerable<ArenaMapDescriptor> maps, Action<string> info, Action<string> warn)
    {
        var type = FindMapRangeWeightsType();
        if (type is null)
        {
            info("APBS (ProgressiveBotSystem) not loaded — no map-range compatibility patch needed.");
            return false;
        }

        var count = RegisterStandIns(maps);
        try
        {
            new MapRangeWeightsIndexerPatch(type).Enable();
            info($"APBS compatibility: patched {MapRangeWeightsTypeName} indexer — {count} Arena map key(s) " +
                 "read their vanilla stand-in (Equator → Interchange, others → factory4_day).");
            return true;
        }
        catch (Exception e)
        {
            warn($"APBS compatibility patch FAILED ({e.GetType().Name}: {e.Message}) — while APBS is installed, " +
                 "bot generation on Arena maps will keep failing with \"Map '<key>' not found\".");
            return false;
        }
    }

    private sealed class MapRangeWeightsIndexerPatch : AbstractPatch
    {
        private readonly Type _type;

        public MapRangeWeightsIndexerPatch(Type type) : base(ApbsCompat.PatchHarmonyId)
        {
            _type = type;
        }

        // MapRangeWeights.this[string key] — the compiled getter is get_Item(string key).
        protected override MethodBase? GetTargetMethod() => AccessTools.PropertyGetter(_type, "Item");

        [PatchPrefix]
        private static void Prefix(ref string key)
        {
            key = Remap(key)!;
        }
    }
}
