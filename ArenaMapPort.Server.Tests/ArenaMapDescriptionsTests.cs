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
        ["en"] = new() { ["arena_equator"] = "English mall fixture", ["arena_bay5"] = "English harbor fixture" },
        ["ge"] = new() { ["arena_equator"] = "German mall fixture", ["arena_bay5"] = "German harbor fixture" },
        ["ru"] = new() { ["arena_equator"] = "Russian mall fixture", ["arena_bay5"] = "Russian harbor fixture" },
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
            Assert.Equal(expected + (map.Key == "arena_equator" ? " mall fixture" : " harbor fixture"), description);
            var locale = new GlobalLocaleDictionary();
            ArenaMapRegistration.ApplyLocale(locale, map, description);
            ArenaMapBanners.ApplyLocale(locale, map, description);
            Assert.Equal(description, locale[map.IdField + " Description"]);
            Assert.Equal(description, locale[ArenaMapBanners.For(map)!.Id + " Description"]);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("version")]
    [InlineData("missing-en")]
    [InlineData("missing-map")]
    [InlineData("empty-text")]
    [InlineData("unexpected-map")]
    public void Broken_pack_warns_once_and_preserves_location_registration(string fault)
    {
        var data = Fixture();
        if (fault == "missing-en") data.Remove("en");
        if (fault == "missing-map") data["en"].Remove("arena_bay5");
        if (fault == "empty-text") data["ge"]["arena_equator"] = " ";
        if (fault == "unexpected-map") data["en"]["arena_bowl"] = "Unreleased fixture";
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
