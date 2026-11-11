using System.Text.Json;
using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class ArenaMapDescriptionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ArenaMapDescriptions_" + Guid.NewGuid().ToString("N"));
    private string PackPath => Path.Combine(_folder, "map-descriptions.json");

    public ArenaMapDescriptionsTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);

    private Dictionary<string, Dictionary<string, string>> Fixture() => new()
    {
        ["en"] = new() { ["arena_equator"] = "English mall fixture", ["arena_bay5"] = "English harbor fixture", ["arena_airpit"] = "English terminal fixture", ["arena_bowl"] = "English stadium fixture" },
        ["ge"] = new() { ["arena_equator"] = "German mall fixture", ["arena_bay5"] = "German harbor fixture", ["arena_airpit"] = "German terminal fixture", ["arena_bowl"] = "German stadium fixture" },
        ["ru"] = new() { ["arena_equator"] = "Russian mall fixture", ["arena_bay5"] = "Russian harbor fixture", ["arena_airpit"] = "Russian terminal fixture", ["arena_bowl"] = "Russian stadium fixture" },
    };

    private static string Kind(ArenaMapDescriptor map) => map.Key switch
    {
        "arena_equator" => " mall fixture",
        "arena_bay5" => " harbor fixture",
        "arena_airpit" => " terminal fixture",
        "arena_bowl" => " stadium fixture", // PLAN-BOWL item 8, 2026-10-18
        "arena_autoservice" => " workshop fixture", // PLAN-AUTOSERVICE item 9, 2026-11-11 (pending: not in All yet)
        _ => throw new InvalidOperationException("no official text for " + map.Key),
    };

    /// <summary>The released maps that have an official text in Arena's locales (the pack must cover exactly these; it may also carry the pending ones).</summary>
    private static IEnumerable<ArenaMapDescriptor> OfficialTextMaps => ArenaMapRegistry.All.Where(ArenaMapDescriptions.HasOfficialText);

    /// <summary>The released registry (Iceberg, which has no official text, PLAN-ICEBERG item 10, joined it with item 12). Kept as a named
    /// property so the Iceberg cases below read as what they test.</summary>
    private static IReadOnlyList<ArenaMapDescriptor> ReleasedWithIceberg => ArenaMapRegistry.All;

    /// <summary>The registry as it was before Iceberg joined: the same pack must be valid for it too.</summary>
    private static IReadOnlyList<ArenaMapDescriptor> ReleasedWithoutIceberg =>
        ArenaMapRegistry.All.Where(map => map.Key != ArenaMapRegistry.Iceberg.Key).ToArray();

    private void WritePack(Dictionary<string, Dictionary<string, string>> descriptions, int version = 1) =>
        File.WriteAllText(PackPath, JsonSerializer.Serialize(new { schema_version = version, descriptions }));

    [Theory]
    [InlineData("en", "English")]
    [InlineData("ge", "German")]
    [InlineData("de", "German")]
    [InlineData("ru", "Russian")]
    [InlineData("fr", "English")]
    public void Localized_or_English_fallback_reaches_both_UI_keys(string language, string expected)
    {
        WritePack(Fixture());
        var warnings = new List<string>();
        var pack = ArenaMapDescriptions.Load(PackPath, warnings.Add);
        Assert.Empty(warnings);
        foreach (var map in OfficialTextMaps)
        {
            var description = pack.Resolve(language, map);
            Assert.Equal(expected + Kind(map), description);
            var locale = new GlobalLocaleDictionary();
            ArenaMapRegistration.ApplyLocale(locale, map, description);
            ArenaMapBanners.ApplyLocale(locale, map, description);
            Assert.Equal(description, locale[map.IdField + " Description"]);
            Assert.Equal(description, locale[ArenaMapBanners.For(map)!.Id + " Description"]);
        }
    }

    [Fact]
    public void Official_text_maps_are_released_maps_and_the_key_set_of_the_extractors_pack()
    {
        // Every map with an official text is a Catalog map (released, or pending like Autoservice before its promotion, item 12),
        // every released map's official text is listed, in the order of the pack generator's table, and the C# list is the
        // key set of extract_map_descriptions.DESCRIPTION_KEYS (they change together: a new Arena location id goes into the
        // Python table, the key into ArenaMapDescriptions.OfficialTextMapKeys).
        var catalog = ArenaMapRegistry.Catalog.Select(m => m.Key).ToHashSet();
        Assert.All(ArenaMapDescriptions.OfficialTextMapKeys, key => Assert.Contains(key, catalog));
        var source = File.ReadAllText(ExtractorPath());
        var table = source[source.IndexOf("DESCRIPTION_KEYS = {", StringComparison.Ordinal)..];
        table = table[..table.IndexOf('}')];
        var pythonKeys = System.Text.RegularExpressions.Regex
            .Matches(table, "\"(arena_[a-z0-9]+)\":\\s*\"[0-9a-f]{24} Description\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(pythonKeys, ArenaMapDescriptions.OfficialTextMapKeys.ToArray());
    }

    private static string ExtractorPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "extract", "extract_map_descriptions.py");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("tools/extract/extract_map_descriptions.py");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ge")]
    [InlineData("de")]
    [InlineData("ru")]
    [InlineData("fr")]
    public void A_released_map_without_an_official_text_answers_with_its_own_description_and_costs_the_others_nothing(string language)
    {
        // PLAN-ICEBERG item 10: Arena's locales have no Iceberg row, so the pack covers the four official maps only. With
        // Iceberg released the pack still loads (no warning) and the four keep their official texts; Iceberg answers with
        // the descriptor's own text, one string for every language.
        WritePack(Fixture());
        var warnings = new List<string>();
        var pack = ArenaMapDescriptions.Load(PackPath, warnings.Add, ReleasedWithIceberg);
        Assert.Empty(warnings);
        var iceberg = ArenaMapRegistry.Iceberg;
        Assert.False(ArenaMapDescriptions.HasOfficialText(iceberg));
        Assert.Equal(iceberg.LocaleDescription, pack.Resolve(language, iceberg));
        foreach (var map in OfficialTextMaps)
        {
            Assert.EndsWith(Kind(map), pack.Resolve(language, map));
            Assert.NotEqual(map.LocaleDescription, pack.Resolve(language, map));
        }
        var locale = new GlobalLocaleDictionary();
        ArenaMapRegistration.ApplyLocale(locale, iceberg, pack.Resolve(language, iceberg));
        ArenaMapBanners.ApplyLocale(locale, iceberg, pack.Resolve(language, iceberg));
        Assert.Equal(iceberg.LocaleDescription, locale[iceberg.IdField + " Description"]);
        Assert.Equal(iceberg.LocaleDescription, locale[ArenaMapBanners.For(iceberg)!.Id + " Description"]);
    }

    [Theory]
    [InlineData("iceberg-entry")]
    [InlineData("missing-bowl")]
    [InlineData("missing-en")]
    [InlineData("empty-text")]
    public void With_a_map_that_has_no_official_text_released_a_damaged_pack_is_still_discarded_whole(string fault)
    {
        var data = Fixture();
        // A pack must never carry a text for a map that has none, and must never lose one of the four official maps.
        if (fault == "iceberg-entry") data["en"]["arena_iceberg"] = "Not an official text";
        if (fault == "missing-bowl") data["ru"].Remove("arena_bowl");
        if (fault == "missing-en") data.Remove("en");
        if (fault == "empty-text") data["ge"]["arena_equator"] = " ";
        WritePack(data);
        var warnings = new List<string>();
        var pack = ArenaMapDescriptions.Load(PackPath, warnings.Add, ReleasedWithIceberg);
        Assert.Single(warnings);
        Assert.Contains("locales/map-descriptions.json", warnings[0]);
        foreach (var map in ReleasedWithIceberg)
            Assert.Equal(map.LocaleDescription, pack.Resolve("ge", map));
    }

    [Fact]
    public void The_released_maps_without_an_official_text_are_exactly_iceberg()
    {
        // The pack rule is "the released maps that HAVE an official text" (ArenaMapDescriptions.OfficialTextMapKeys, a hand-kept list that mirrors the
        // extractor's DESCRIPTION_KEYS): a newly released map that does have an official text in Arena's locales but is not added to the list would
        // silently fall back to its own LocaleDescription instead of being discarded as a stale pack. This pins the complement, so a promotion has to
        // edit it on purpose (review of 2026-10-30): Iceberg is the one released map Arena's locales have no Name or Description row for.
        var without = ArenaMapRegistry.All.Where(map => !ArenaMapDescriptions.HasOfficialText(map)).Select(map => map.Key).ToArray();
        Assert.Equal(new[] { "arena_iceberg" }, without);
        // OfficialTextMapKeys also lists the pending Autoservice (item 9, before its promotion): the released maps with a text are All minus Iceberg.
        var releasedKeys = ArenaMapRegistry.All.Select(m => m.Key).ToHashSet();
        var releasedOfficial = ArenaMapDescriptions.OfficialTextMapKeys.Where(releasedKeys.Contains).ToArray();
        Assert.Equal(ArenaMapRegistry.All.Count - 1, releasedOfficial.Length);
        Assert.Equal(ArenaMapRegistry.All.Select(m => m.Key).Order().ToArray(),
            releasedOfficial.Concat(without).Order().ToArray());
    }

    [Fact]
    public void The_pack_the_registry_loads_is_valid_for_the_registry_with_and_without_iceberg()
    {
        WritePack(Fixture());
        Assert.Contains(ArenaMapRegistry.Iceberg, ReleasedWithIceberg);
        Assert.DoesNotContain(ArenaMapRegistry.Iceberg, ReleasedWithoutIceberg);
        foreach (var released in new[] { ReleasedWithoutIceberg, ReleasedWithIceberg })
        {
            var warnings = new List<string>();
            ArenaMapDescriptions.Load(PackPath, warnings.Add, released);
            Assert.Empty(warnings);
        }
    }

    private static Dictionary<string, Dictionary<string, string>> FixtureWithAutoService()
    {
        var data = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (language, texts) in new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new() { ["arena_autoservice"] = "English workshop fixture" },
            ["ge"] = new() { ["arena_autoservice"] = "German workshop fixture" },
            ["ru"] = new() { ["arena_autoservice"] = "Russian workshop fixture" },
        }) data[language] = texts;
        return data;
    }

    [Fact]
    public void Autoservice_is_an_official_text_map_that_is_pending_until_it_is_released()
    {
        // PLAN-AUTOSERVICE item 9: Arena's location id 6441004f... has Name and Description rows, so the map is in
        // OfficialTextMapKeys ahead of its promotion (item 12). The extractor's pack then carries its entry too.
        Assert.True(ArenaMapDescriptions.HasOfficialText(ArenaMapRegistry.AutoService));
        Assert.DoesNotContain(ArenaMapRegistry.AutoService, ArenaMapRegistry.All);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ge")]
    [InlineData("ru")]
    public void A_pack_with_the_pending_autoservice_entry_loads_for_the_released_maps_and_costs_them_nothing(string language)
    {
        // The pack the extractor writes today has five entries per language; the server that registers five maps must neither
        // discard it (the four official texts would be lost) nor require the pending entry from a pack that lacks it.
        var data = Fixture();
        foreach (var (lang, texts) in FixtureWithAutoService()) data[lang]["arena_autoservice"] = texts["arena_autoservice"];
        WritePack(data);
        var warnings = new List<string>();
        var pack = ArenaMapDescriptions.Load(PackPath, warnings.Add);
        Assert.Empty(warnings);
        foreach (var map in OfficialTextMaps)
            Assert.EndsWith(Kind(map), pack.Resolve(language, map));
        Assert.EndsWith(" workshop fixture", pack.Resolve(language, ArenaMapRegistry.AutoService));

        // And the four-entry pack stays valid until the promotion.
        WritePack(Fixture());
        warnings.Clear();
        ArenaMapDescriptions.Load(PackPath, warnings.Add);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Once_autoservice_is_released_its_entry_is_required()
    {
        // The promotion (item 12) makes the pending entry mandatory: a pack from before it is discarded whole, as for every map.
        var released = ArenaMapRegistry.All.Append(ArenaMapRegistry.AutoService).ToArray();
        var data = Fixture();
        foreach (var (lang, texts) in FixtureWithAutoService()) data[lang]["arena_autoservice"] = texts["arena_autoservice"];
        WritePack(data);
        var warnings = new List<string>();
        ArenaMapDescriptions.Load(PackPath, warnings.Add, released);
        Assert.Empty(warnings);

        WritePack(Fixture());
        ArenaMapDescriptions.Load(PackPath, warnings.Add, released);
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("version")]
    [InlineData("missing-en")]
    [InlineData("missing-map")]
    [InlineData("empty-text")]
    [InlineData("unexpected-map")]
    [InlineData("missing-airpit")]
    [InlineData("two-map-pack")]
    [InlineData("three-map-pack")]
    public void Broken_pack_warns_once_and_preserves_location_registration(string fault)
    {
        var data = Fixture();
        if (fault == "missing-en") data.Remove("en");
        if (fault == "missing-map") data["en"].Remove("arena_bay5");
        // A pack from before AirPit joined ArenaMapRegistry.All: the whole pack is discarded (every map falls
        // back to its built-in description), never half-applied. tools/extract/extract_map_descriptions.py
        // must be re-run in the same change that promotes a map.
        if (fault == "missing-airpit") data["ru"].Remove("arena_airpit");
        if (fault == "two-map-pack") foreach (var language in data.Values) language.Remove("arena_airpit");
        // The same for a pack from before Bowl joined (PLAN-BOWL item 8): the whole pack is discarded.
        if (fault == "three-map-pack") foreach (var language in data.Values) language.Remove("arena_bowl");
        if (fault == "empty-text") data["ge"]["arena_equator"] = " ";
        // Prison is a parked map (Bowl was this fixture until PLAN-BOWL item 8 released it).
        if (fault == "unexpected-map") data["en"]["arena_prison"] = "Unreleased fixture";
        if (fault != "missing") WritePack(data, fault == "version" ? 2 : 1);
        if (fault == "invalid-json") File.WriteAllText(PackPath, "{");
        var warnings = new List<string>();
        var pack = ArenaMapDescriptions.Load(PackPath, warnings.Add);
        Assert.Single(warnings);
        Assert.Contains("locales/map-descriptions.json", warnings[0]);
        foreach (var map in ArenaMapRegistry.All)
            Assert.Equal(map.LocaleDescription, pack.Resolve("ge", map));
    }
}
