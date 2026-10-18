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
        _ => throw new InvalidOperationException("unexpected released map " + map.Key),
    };

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
        foreach (var map in ArenaMapRegistry.All)
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
    public void Released_registry_is_exactly_the_four_maps_of_the_description_pack()
    {
        Assert.Equal(new[] { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" }, ArenaMapRegistry.All.Select(m => m.Key));
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
