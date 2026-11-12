using System.Text.Json;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-ICEBERG items 7, 8, 9 and 12 (docs/progress/iceberg-release.md): tools/maps/iceberg.json, iceberg.bots.json, iceberg.exits.json and
/// iceberg.doors.json are the single sources of Iceberg's identity, bake inputs, spawn points, waves, PMC squads, exits and gate poses. The
/// compiled descriptor (what the client authors) and the shipped db/arena_iceberg/base.json (what make_base_json.py generated) must say exactly
/// what the sidecars say. Modelled on AirPitMapTests and BowlMapTests; the sidecar numbers themselves are pinned on the Python side
/// (test_iceberg_spawns, test_iceberg_bake_inputs, test_door_poses).
/// </summary>
public sealed class IcebergMapTests
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

    private static string BaseJsonPath => Path.Combine(AppContext.BaseDirectory, "db", "arena_iceberg", "base.json");

    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(v => v.GetSingle()).ToArray();

    private static float[] PositionOf(JsonElement spawnPoint)
    {
        var p = spawnPoint.GetProperty("Position");
        return new[] { p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(), p.GetProperty("z").GetSingle() };
    }

    [Fact]
    public void IdentityIsTheShippedOneNotTheSpike()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("iceberg.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.Iceberg;
        Assert.Contains(map, ArenaMapRegistry.All);
        Assert.Equal("Arena: Iceberg", map.LocaleName);
        Assert.Equal(sidecar.RootElement.GetProperty("locale_name").GetString(), map.LocaleName);
        Assert.Equal(sidecar.RootElement.GetProperty("locale_description").GetString(), map.LocaleDescription);
        Assert.Equal(map.LocaleName, server.RootElement.GetProperty("Name").GetString());
        Assert.Equal(map.IconX, server.RootElement.GetProperty("IconX").GetInt32());
        Assert.Equal(map.IconY, server.RootElement.GetProperty("IconY").GetInt32());
        // Arena's measured 110 moved east so that ArenaMode's mode panel no longer hides the marker (markers.json "display", 2026-11-01).
        Assert.Equal((215, 608), (map.IconX, map.IconY));
        // The port's own words (Arena's locales have no Iceberg name or description row): factual, one English string for every language, ASCII, no
        // spike wording; ArenaMapDescriptions.Load leaves Iceberg out of the official pack and Resolve answers with this text.
        Assert.DoesNotContain("spike", map.LocaleName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Internal test build", map.LocaleDescription, StringComparison.OrdinalIgnoreCase);
        Assert.All(map.LocaleDescription, c => Assert.InRange(c, ' ', '~'));
        Assert.Contains("EFT Arena", map.LocaleDescription);
        Assert.Contains("A, B and C", map.LocaleDescription);
        Assert.Equal("2e2d6dadf4ebb29e53a69aae", map.IdField);
    }

    [Fact]
    public void ExitTransformsMatchTheSidecarAndServerEligibility()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("iceberg.exits.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.Iceberg;
        var authored = sidecar.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        var shipped = server.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        Assert.Equal(2, map.Exits.Length);
        Assert.Equal(new[] { "Iceberg East Gate", "Iceberg West Gate" }, map.Exits.Select(e => e.Name));
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
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("iceberg.bots.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.Iceberg;
        var bots = sidecar.RootElement;
        var zone = bots.GetProperty("zone").GetString();
        var shipped = server.RootElement.GetProperty("SpawnPointParams").EnumerateArray().ToArray();
        var players = bots.GetProperty("player_spawns").EnumerateArray().ToArray();
        var botSpawns = bots.GetProperty("bot_spawns").EnumerateArray().ToArray();

        // Four player rows on pocket markers (an All and a side row at each end) and 17 bot rows: nine from the 11 AI core points (the two within
        // 10 m of a gate leaf are skipped) and, appended after them on 2026-11-12, eight more (seven CheckPoint stage markers and one fill point)
        // for ArenaMode's 25 m burst selector. The first nine ids stay in this order (ArenaMode pins bot-core-01 by id and exact floats).
        Assert.Equal(4, players.Length);
        Assert.Equal(17, botSpawns.Length);
        Assert.All(botSpawns.Take(9), b => Assert.StartsWith("arena_iceberg-bot-core-", b.GetProperty("id").GetString()));
        Assert.All(botSpawns.Skip(9), b => Assert.Matches("^arena_iceberg-bot-(stage-(bear|usec)-[0-9]+|fill-[0-9]+)$", b.GetProperty("id").GetString()));
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
        Assert.Equal(8, root.GetProperty("BotMax").GetInt32());
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

    // PLAN-ICEBERG item 9/12: tools/maps/iceberg.json is what BakeNavMesh and the Python model read, the compiled descriptor is what the client
    // opens at raid start. One list each; the gate strings are the unit-level paths of iceberg.doors.json.
    [Fact]
    public void OpenedBarriersAndBakeInputsMatchTheSidecar()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("iceberg.json")));
        var root = sidecar.RootElement;
        var map = ArenaMapRegistry.Iceberg;

        Assert.Equal(root.GetProperty("opened_barriers").EnumerateArray().Select(e => e.GetString()), map.OpenedBarriers);
        Assert.Equal(root.GetProperty("start_gates").EnumerateArray().Select(e => e.GetString()), map.StartGates);
        Assert.Equal(root.GetProperty("held_gates").EnumerateArray().Select(e => e.GetString()), map.HeldGates);
        Assert.Equal(root.GetProperty("navmesh_bake_exclusions").EnumerateArray().Select(e => e.GetString()), map.NavMeshBakeExclusions);
        Assert.Equal(Floats(root.GetProperty("bake_bounds")), map.BakeBounds);
        Assert.Equal(root.GetProperty("navmesh_bake_layers").EnumerateArray().Select(e => e.GetInt32()), map.NavMeshBakeLayers);
        Assert.Equal(root.GetProperty("keep_level_borders").GetBoolean(), map.KeepLevelBorders);
        Assert.Equal(root.GetProperty("uses_server_time").GetBoolean(), map.UsesServerTime);
        Assert.Equal(root.GetProperty("puddle_water_material_names").EnumerateArray().Select(e => e.GetString()), map.PuddleWaterMaterialNames);
        Assert.False(root.TryGetProperty("puddle_extra_surfaces", out _));
        Assert.Empty(map.PuddleExtraSurfaces);
        // the shader decision is open until the first host raid: no fallback row (a descriptor list would be inert, ShaderStandinPolicy has no Iceberg switch)
        Assert.False(root.TryGetProperty("shader_fallback_names", out _));
        Assert.Empty(map.ShaderFallbackNames);
        // the border cubes are the play space's only bound and Arena has no data polygon: no authored borders, no lamp fill, no sun
        Assert.Empty(map.AuthoredBorders);
        Assert.Null(map.LampFill);
        Assert.Empty(map.SunPaths);
    }

    [Fact]
    public void DoorPosesMatchTheSidecarAndEveryOneLiesBelowAStartGate()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("iceberg.doors.json")));
        var root = doc.RootElement;
        var map = ArenaMapRegistry.Iceberg;
        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(map.Key, root.GetProperty("key").GetString());
        var sidecar = root.GetProperty("poses").EnumerateArray().ToArray();
        Assert.Equal(11, sidecar.Length);
        Assert.Equal(sidecar.Length, map.DoorPoses.Length);
        for (var i = 0; i < sidecar.Length; i++)
        {
            var s = sidecar[i];
            var p = map.DoorPoses[i];
            var label = $"pose {i} ({p.Barrier}/{p.Part})";
            Assert.True(s.GetProperty("barrier").GetString() == p.Barrier, label);
            Assert.True(s.GetProperty("part").GetString() == p.Part, label);
            Assert.Equal((float)s.GetProperty("seconds").GetDouble(), p.Seconds);
            Assert.Equal(Floats(s.GetProperty("closed").GetProperty("position")), p.ClosedPosition);
            Assert.Equal(Floats(s.GetProperty("open").GetProperty("position")), p.OpenPosition);
            Assert.Equal(Floats(s.GetProperty("at")), p.At);                         // every row names its gate by the barrier root's world position
            Assert.Contains(p.Barrier, map.StartGates);
            Assert.Equal((ArenaDoorPose.SourceDotAnimator, ArenaDoorPose.EaseLinear), (p.Source, p.Ease));
            Assert.InRange(p.Seconds, 2.0f, 5.0f);
        }
        // five sliding gates with two leaves each and the rotating motor gate (its root moves: part "")
        Assert.Equal(10, map.DoorPoses.Count(p => p.Part.StartsWith("sanatorium_resp_gate_")));
        Assert.Single(map.DoorPoses, p => p.Barrier.EndsWith("Sanatorium_CheckPoint_Gate") && p.Part == "");
        Assert.Equal(new[] { 2, 3, 1 }, new[] { "DOORS/GATES_A/sanatorium_resp_gate", "DOORS/GATES_B/sanatorium_resp_gate", "DOORS/GATES_B/Sanatorium_CheckPoint_Gate" }
            .Select(b => map.DoorPoses.Where(p => p.Barrier == b).Select(p => string.Join(",", p.At!)).Distinct().Count()));
    }

    [Fact]
    public void ShippedBaseJsonParsesAsTheServerLocationModel()
    {
        var location = JsonSerializer.Deserialize<LocationBase>(File.ReadAllText(BaseJsonPath), Options);
        Assert.NotNull(location);
        var map = ArenaMapRegistry.Iceberg;
        Assert.Equal(map.Key, location!.Id);
        Assert.Equal(map.IdField, location.IdField.ToString());
        Assert.Equal(21, location.SpawnPointParams!.Count());
        Assert.Equal(4, location.SpawnPointParams!.Count(p => p.Categories!.Contains("Player")));
        Assert.Equal(17, location.SpawnPointParams!.Count(p => p.Categories!.Contains("Bot")));
        Assert.Equal(2, location.Exits.Count());
        Assert.Equal(5, location.Waves!.Count);
        Assert.Equal(4, location.BossLocationSpawn!.Count);
        Assert.Equal(8, location.BotMax);
        Assert.True(location.DisabledForScav);
        Assert.Empty(location.Banners!);
    }
}
