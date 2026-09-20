using System.IO;
using System.Text.Json;
using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Loads the shipped db/base.json and asserts the invariants docs/SPEC.md §E.4 and
/// §B.WP6's validate_server_db.py table demand: identity constants, Scene.path/rcid,
/// Banners empty, DisabledForScav true — and, since bots-stage1 (docs/progress/bots-stage1.md),
/// SpawnPointParams PRESENT with player + Bot-category points and Factory-shaped waves on the zone.
/// </summary>
public class BaseJsonTests
{
    private const string ExpectedKey = "arena_equator";
    private const string ExpectedIdField = "e9b8e12c60dfd2820e949462";
    private const string ExpectedScenePath = "maps/arena_equator_preset.bundle";
    private const string ExpectedSceneRcid = "arena_equator.scenespreset.asset";

    // Same converter set the live server's JsonUtil registers (SptJsonConverterRegistrator),
    // so these tests parse db/base.json exactly as the server does at load.
    private static readonly JsonSerializerOptions Options = CreateServerOptions();

    private static JsonSerializerOptions CreateServerOptions()
    {
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters())
            options.Converters.Add(converter);
        return options;
    }

    private static string BaseJsonPath => Path.Combine(AppContext.BaseDirectory, "db", "base.json");

    private static string ReadRawJson() => File.ReadAllText(BaseJsonPath);

    private static LocationBase Deserialize()
    {
        var json = ReadRawJson();
        var location = JsonSerializer.Deserialize<LocationBase>(json, Options);
        Assert.NotNull(location);
        return location!;
    }

    [Fact]
    public void File_Exists()
    {
        Assert.True(File.Exists(BaseJsonPath), $"db/base.json not found at {BaseJsonPath}");
    }

    // exfils-stage2 — base.json exits carry the descriptor's names, the map's infiltration as EntryPoints,
    // and an always-open Individual exit (100 %, no requirement).
    [Fact]
    public void Exits_ShipTheDescriptorsExits()
    {
        using var doc = JsonDocument.Parse(ReadRawJson());
        var exits = doc.RootElement.GetProperty("exits").EnumerateArray().ToList();
        var expected = ArenaMapRegistry.Equator.Exits.Select(e => e.Name).OrderBy(n => n).ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, exits.Select(e => e.GetProperty("Name").GetString()).OrderBy(n => n).ToList());
        Assert.All(exits, e => Assert.Equal(ArenaMapRegistry.Equator.InfiltrationName, e.GetProperty("EntryPoints").GetString()));
        Assert.All(exits, e => Assert.Equal(100.0, e.GetProperty("Chance").GetDouble()));
        Assert.All(exits, e => Assert.Equal("None", e.GetProperty("PassageRequirement").GetString()));
        Assert.All(exits, e => Assert.Equal("Individual", e.GetProperty("ExfiltrationType").GetString()));
        Assert.All(exits, e => Assert.True(e.GetProperty("ExfiltrationTime").GetDouble() > 0));
        Assert.Empty(ArenaMapRegistration.ExitNameMismatches(exits.Select(e => e.GetProperty("Name").GetString()), ArenaMapRegistry.Equator));
    }

    // stage 1 — tools/maps/equator.json opened_barriers (what BakeNavMesh excludes from the NavMesh sources) and the
    // compiled descriptor (what BoothDoorRepairs opens at raid start) are one list.
    [Fact]
    public void OpenedBarriers_DescriptorMirrorsSidecar()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? sidecar = null;
        for (var d = dir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tools", "maps", "equator.json");
            if (File.Exists(candidate))
            {
                sidecar = candidate;
                break;
            }
        }
        Assert.NotNull(sidecar);

        using var doc = JsonDocument.Parse(File.ReadAllText(sidecar!));
        var fromSidecar = doc.RootElement.GetProperty("opened_barriers").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(fromSidecar, ArenaMapRegistry.Equator.OpenedBarriers);
    }

    // exfils-stage2 — tools/maps/equator.exits.json (what make_base_json.py reads) and the compiled descriptor
    // (what the client authors) are one table.
    [Fact]
    public void Exits_DescriptorMirrorsSidecar()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? sidecar = null;
        for (var d = dir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tools", "maps", "equator.exits.json");
            if (File.Exists(candidate))
            {
                sidecar = candidate;
                break;
            }
        }
        Assert.NotNull(sidecar);

        using var doc = JsonDocument.Parse(File.ReadAllText(sidecar!));
        var fromSidecar = doc.RootElement.GetProperty("exits").EnumerateArray()
            .Select(e => (
                Name: e.GetProperty("name").GetString(),
                Pos: e.GetProperty("position").EnumerateArray().Select(v => v.GetSingle()).ToArray(),
                Size: e.GetProperty("box_size").EnumerateArray().Select(v => v.GetSingle()).ToArray(),
                Rot: e.GetProperty("rotation").GetSingle()))
            .OrderBy(e => e.Name).ToList();
        var fromDescriptor = ArenaMapRegistry.Equator.Exits
            .Select(e => (Name: (string?)e.Name, Pos: new[] { e.X, e.Y, e.Z }, Size: new[] { e.SizeX, e.SizeY, e.SizeZ }, Rot: e.RotationY))
            .OrderBy(e => e.Name).ToList();

        Assert.Equal(fromSidecar.Count, fromDescriptor.Count);
        for (var i = 0; i < fromSidecar.Count; i++)
        {
            Assert.Equal(fromSidecar[i].Name, fromDescriptor[i].Name);
            Assert.Equal(fromSidecar[i].Pos, fromDescriptor[i].Pos);
            Assert.Equal(fromSidecar[i].Size, fromDescriptor[i].Size);
            Assert.Equal(fromSidecar[i].Rot, fromDescriptor[i].Rot);
        }
    }

    // bots-stage3 — PMC squads ride on BossLocationSpawn (Factory's shape), on the authored zone, and the bot
    // spawn points carry the BotPmc category so the zone counts as PMC-capable.
    [Fact]
    public void BossLocationSpawn_ShipsPmcSquadsOnTheAuthoredZone()
    {
        using var doc = JsonDocument.Parse(ReadRawJson());
        var root = doc.RootElement;
        var bosses = root.GetProperty("BossLocationSpawn").EnumerateArray().ToList();

        Assert.NotEmpty(bosses);
        Assert.All(bosses, b => Assert.Contains(b.GetProperty("BossName").GetString(), new[] { "pmcUSEC", "pmcBEAR" }));
        Assert.Contains(bosses, b => b.GetProperty("BossName").GetString() == "pmcUSEC");
        Assert.Contains(bosses, b => b.GetProperty("BossName").GetString() == "pmcBEAR");
        Assert.All(bosses, b => Assert.Equal("BotZone_Main", b.GetProperty("BossZone").GetString()));
        Assert.All(bosses, b => Assert.True(b.GetProperty("IgnoreMaxBots").GetBoolean()));
        Assert.All(bosses, b => Assert.Equal(-1, b.GetProperty("Time").GetDouble()));

        var botPoints = root.GetProperty("SpawnPointParams").EnumerateArray()
            .Where(p => p.GetProperty("Categories").EnumerateArray().Any(c => c.GetString() == "Bot")).ToList();
        Assert.NotEmpty(botPoints);
        Assert.All(botPoints, p => Assert.Contains("BotPmc", p.GetProperty("Categories").EnumerateArray().Select(c => c.GetString())));
    }

    [Fact]
    public void SpawnPointParams_ShipPlayerAndBotSpawns()
    {
        // bots-stage1 (docs/progress/bots-stage1.md): the server JSON is the single source of spawn
        // points again, as on vanilla maps — SpawnPointsCollection.FixSpawns deletes the scene's
        // GUID-named markers and creates these; a Bot-category point is linked to the zone by GameObject
        // name (SpawnPointMarker.Create), which is exactly the list waves spawn from. So the file MUST
        // carry the three player spawns (with the map's infiltration) plus bot points on the zone.
        using var doc = JsonDocument.Parse(ReadRawJson());
        var points = doc.RootElement.GetProperty("SpawnPointParams").EnumerateArray().ToList();

        Assert.NotEmpty(points);
        Assert.Contains(points, p => HasCategory(p, "Player") && p.GetProperty("Infiltration").GetString() == "ArenaEquator");
        Assert.Contains(points, p => HasCategory(p, "Bot") && p.GetProperty("BotZoneName").GetString() == "BotZone_Main");
        Assert.Equal(points.Count, points.Select(p => p.GetProperty("Id").GetString()).Distinct().Count());
        Assert.All(points, p => Assert.False(string.IsNullOrEmpty(p.GetProperty("Id").GetString())));
    }

    [Fact]
    public void Waves_TargetTheAuthoredZone()
    {
        using var doc = JsonDocument.Parse(ReadRawJson());
        var root = doc.RootElement;
        var waves = root.GetProperty("waves").EnumerateArray().ToList();

        Assert.NotEmpty(waves);
        Assert.All(waves, w => Assert.Equal("BotZone_Main", w.GetProperty("SpawnPoints").GetString()));
        Assert.All(waves, w => Assert.Equal("Savage", w.GetProperty("BotSide").GetString()));
        Assert.Equal("BotZone_Main", root.GetProperty("OpenZones").GetString());
        Assert.True(root.GetProperty("BotMax").GetInt32() > 0);
    }

    private static bool HasCategory(JsonElement point, string category) =>
        point.GetProperty("Categories").EnumerateArray().Any(c => c.GetString() == category);

    [Fact]
    public void Identity_MatchesSpecConstants()
    {
        var location = Deserialize();

        Assert.Equal(ExpectedKey, location.Id);
        Assert.Equal(ExpectedIdField, location.IdField.ToString());
    }

    [Fact]
    public void Scene_PathAndRcid_MatchSpecConstants()
    {
        var location = Deserialize();

        Assert.NotNull(location.Scene);
        Assert.Equal(ExpectedScenePath, location.Scene!.Path);
        Assert.Equal(ExpectedSceneRcid, location.Scene.Rcid);
    }

    [Fact]
    public void Banners_IsEmpty_NotNull()
    {
        var location = Deserialize();

        Assert.NotNull(location.Banners);
        Assert.Empty(location.Banners!);
    }

    [Fact]
    public void DisabledForScav_IsTrue()
    {
        var location = Deserialize();

        Assert.True(location.DisabledForScav);
    }

    [Fact]
    public void EnabledAndLocked_AllowSelectionInMapSelectUI()
    {
        var location = Deserialize();

        Assert.True(location.Enabled);
        Assert.False(location.Locked);
    }

    [Fact]
    public void Loot_IsEmpty_NotNull()
    {
        var location = Deserialize();

        Assert.NotNull(location.Loot);
        Assert.Empty(location.Loot!);
    }

    [Fact]
    public void MinAndMaxPlayers_AreOne()
    {
        var location = Deserialize();

        Assert.Equal(1, location.MinPlayers);
        Assert.Equal(1, location.MaxPlayers);
    }

    // The map selection screen calls location.BossLocationSpawn.Any(...) and the raid start
    // reads location.waves without null checks (MatchMakerSelectionLocationScreen.cs:439/451,
    // LocationInfoPanel.cs:174, BaseLocalGame.cs:415, LocalGame.cs:143/147). A missing array
    // throws while the button is built, so the location silently never appears.
    // bots-stage1: waves are populated on purpose now (see Waves_TargetTheAuthoredZone); the
    // dereference-safety invariant is "present, never null" — BossLocationSpawn stays empty until
    // a boss stage exists.
    [Fact]
    public void ClientDereferencedArrays_ArePresent()
    {
        var location = Deserialize();

        Assert.NotNull(location.BossLocationSpawn);
        Assert.NotEmpty(location.BossLocationSpawn!); // bots-stage3: PMC squads ride on BossLocationSpawn
        Assert.NotNull(location.Waves);
        Assert.NotEmpty(location.Waves!);
    }
}
