using System.Text.Json;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// docs/PLAN-AIRPIT.md item 6 (docs/progress/airpit-release.md): tools/maps/airpit.bots.json and
/// airpit.exits.json are the single source of AirPit's spawn points, waves, PMC squads and exits.
/// The compiled descriptor (what the client authors) and the shipped db/arena_airpit/base.json
/// (what make_base_json.py generated) must say exactly what the sidecars say.
/// </summary>
public sealed class AirPitMapTests
{
    private static readonly JsonSerializerOptions Options = CreateServerOptions();

    private static JsonSerializerOptions CreateServerOptions()
    {
        // Same converter set the live server's JsonUtil registers, so base.json parses as at server load.
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters())
            options.Converters.Add(converter);
        return options;
    }

    private static string Sidecar(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "maps", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    private static string BaseJsonPath => Path.Combine(AppContext.BaseDirectory, "db", "arena_airpit", "base.json");

    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(v => v.GetSingle()).ToArray();

    [Fact]
    public void ExitTransformsMatchTheSidecarAndServerEligibility()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.exits.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.AirPit;
        var authored = sidecar.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        var shipped = server.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        Assert.Equal(2, map.Exits.Length);
        Assert.Equal(map.Exits.Length, authored.Length);
        Assert.Equal(map.Exits.Length, shipped.Length);
        foreach (var expected in map.Exits)
        {
            var point = authored.Single(e => e.GetProperty("name").GetString() == expected.Name);
            Assert.Equal(new[] { expected.X, expected.Y, expected.Z }, Floats(point.GetProperty("position")));
            Assert.Equal(new[] { expected.SizeX, expected.SizeY, expected.SizeZ }, Floats(point.GetProperty("box_size")));
            Assert.Equal(expected.RotationY, point.GetProperty("rotation").GetSingle());
            var exit = shipped.Single(e => e.GetProperty("Name").GetString() == expected.Name);
            Assert.Equal(map.InfiltrationName, exit.GetProperty("EntryPoints").GetString());
            Assert.Equal("Individual", exit.GetProperty("ExfiltrationType").GetString());
            Assert.Equal("None", exit.GetProperty("PassageRequirement").GetString());
            Assert.Equal(100, exit.GetProperty("Chance").GetDouble());
            Assert.Equal(point.GetProperty("exfiltration_time").GetDouble(), exit.GetProperty("ExfiltrationTime").GetDouble());
        }
        Assert.Empty(ArenaMapRegistration.ExitNameMismatches(
            shipped.Select(e => e.GetProperty("Name").GetString()), map));
    }

    [Fact]
    public void SpawnPointsWavesAndSquadsMatchTheBotsSidecar()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.bots.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.AirPit;
        var bots = sidecar.RootElement;
        var zone = bots.GetProperty("zone").GetString();
        var shipped = server.RootElement.GetProperty("SpawnPointParams").EnumerateArray().ToArray();
        var players = bots.GetProperty("player_spawns").EnumerateArray().ToArray();
        var botSpawns = bots.GetProperty("bot_spawns").EnumerateArray().ToArray();

        Assert.Equal(4, players.Length);
        Assert.Equal(14, botSpawns.Length);
        Assert.Equal(players.Length + botSpawns.Length, shipped.Length);
        Assert.Equal(shipped.Length, shipped.Select(p => p.GetProperty("Id").GetString()).Distinct().Count());

        foreach (var expected in players)
        {
            var point = shipped.Single(p => p.GetProperty("Id").GetString() == expected.GetProperty("id").GetString());
            Assert.Equal(Floats(expected.GetProperty("position")), PositionOf(point));
            Assert.Equal(expected.GetProperty("rotation").GetDouble(), point.GetProperty("Rotation").GetDouble());
            Assert.Equal(expected.GetProperty("sides").EnumerateArray().Select(s => s.GetString()),
                         point.GetProperty("Sides").EnumerateArray().Select(s => s.GetString()));
            Assert.Equal(new[] { "Player" }, point.GetProperty("Categories").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal(map.InfiltrationName, point.GetProperty("Infiltration").GetString());
        }
        foreach (var expected in botSpawns)
        {
            var point = shipped.Single(p => p.GetProperty("Id").GetString() == expected.GetProperty("id").GetString());
            Assert.Equal(Floats(expected.GetProperty("position")), PositionOf(point));
            Assert.Equal(zone, point.GetProperty("BotZoneName").GetString());
            Assert.Equal(expected.GetProperty("categories").EnumerateArray().Select(c => c.GetString()),
                         point.GetProperty("Categories").EnumerateArray().Select(c => c.GetString()));
            Assert.Contains("BotPmc", point.GetProperty("Categories").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("", point.GetProperty("Infiltration").GetString());
        }

        var root = server.RootElement;
        Assert.Equal(bots.GetProperty("max_bots").GetInt32(), root.GetProperty("BotMax").GetInt32());
        Assert.Equal(zone, root.GetProperty("OpenZones").GetString());
        Assert.True(root.GetProperty("DisabledForScav").GetBoolean());
        Assert.Empty(root.GetProperty("Loot").EnumerateArray());
        Assert.Equal(bots.GetProperty("waves").GetArrayLength(), root.GetProperty("waves").GetArrayLength());
        Assert.All(root.GetProperty("waves").EnumerateArray(), w => Assert.Equal(zone, w.GetProperty("SpawnPoints").GetString()));

        var squads = root.GetProperty("BossLocationSpawn").EnumerateArray().ToArray();
        Assert.Equal(bots.GetProperty("boss_waves").GetArrayLength(), squads.Length);
        Assert.All(squads, b => Assert.Contains(b.GetProperty("BossName").GetString(), new[] { "pmcUSEC", "pmcBEAR" }));
        Assert.All(squads, b => Assert.Equal(zone, b.GetProperty("BossZone").GetString()));
        Assert.Equal(2, squads.Count(b => b.GetProperty("BossName").GetString() == "pmcUSEC"));
        Assert.Equal(2, squads.Count(b => b.GetProperty("BossName").GetString() == "pmcBEAR"));
    }

    [Fact]
    public void ShippedBaseJsonParsesAsTheServerLocationModel()
    {
        var location = JsonSerializer.Deserialize<LocationBase>(File.ReadAllText(BaseJsonPath), Options);
        Assert.NotNull(location);
        var map = ArenaMapRegistry.AirPit;
        Assert.Equal(map.Key, location!.Id);
        Assert.Equal(map.IdField, location.IdField.ToString());
        Assert.Equal(18, location.SpawnPointParams!.Count());
        Assert.Equal(4, location.SpawnPointParams!.Count(p => p.Categories!.Contains("Player")));
        Assert.Equal(14, location.SpawnPointParams!.Count(p => p.Categories!.Contains("Bot")));
        Assert.Equal(2, location.Exits.Count());
        Assert.Equal(5, location.Waves!.Count);
        Assert.Equal(4, location.BossLocationSpawn!.Count);
        Assert.Equal(8, location.BotMax);
        Assert.True(location.DisabledForScav);
        Assert.Empty(location.Banners!);
    }

    private static float[] PositionOf(JsonElement spawnPoint)
    {
        var p = spawnPoint.GetProperty("Position");
        return new[] { p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(), p.GetProperty("z").GetSingle() };
    }
}
