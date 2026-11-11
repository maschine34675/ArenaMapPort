using System.Text.Json;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-AUTOSERVICE items 6, 7, 8, 9, 11 and 12 (docs/progress/autoservice-release.md): tools/maps/autoservice.json, autoservice.bots.json,
/// autoservice.exits.json, autoservice.doors.json and autoservice.monitors.json are the single sources of Autoservice's identity, bake inputs,
/// spawn points, waves, PMC squads, exits, gate poses and score monitors. The compiled descriptor (what the client authors) and the shipped
/// db/arena_autoservice/base.json (what make_base_json.py generated) must say exactly what the sidecars say. Modelled on IcebergMapTests; the
/// sidecar numbers themselves are pinned on the Python side (test_autoservice_spawns, test_autoservice_bake_inputs, test_door_poses,
/// test_score_monitors, test_autoservice_light).
/// </summary>
public sealed class AutoServiceMapTests
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

    private static string BaseJsonPath => Path.Combine(AppContext.BaseDirectory, "db", "arena_autoservice", "base.json");

    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(v => v.GetSingle()).ToArray();

    private static float[] PositionOf(JsonElement spawnPoint)
    {
        var p = spawnPoint.GetProperty("Position");
        return new[] { p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(), p.GetProperty("z").GetSingle() };
    }

    [Fact]
    public void IdentityIsTheShippedOneNotTheSpike()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("autoservice.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.AutoService;
        Assert.Contains(map, ArenaMapRegistry.All);
        Assert.Equal(ArenaMapRegistry.Iceberg, ArenaMapRegistry.All[^2]);          // joined after Iceberg, last in the release order
        Assert.Equal(map, ArenaMapRegistry.All[^1]);
        Assert.Equal("Arena: Chop Shop", map.LocaleName);
        Assert.Equal(sidecar.RootElement.GetProperty("locale_name").GetString(), map.LocaleName);
        Assert.Equal(sidecar.RootElement.GetProperty("locale_description").GetString(), map.LocaleDescription);
        Assert.Equal(map.LocaleName, server.RootElement.GetProperty("Name").GetString());
        Assert.Equal(map.IconX, server.RootElement.GetProperty("IconX").GetInt32());
        Assert.Equal(map.IconY, server.RootElement.GetProperty("IconY").GetInt32());
        Assert.Equal((407, 471), (map.IconX, map.IconY));
        // The port's own fallback words (the official text is in the ignored pack and wins when it is there): factual, ASCII, no spike wording.
        Assert.DoesNotContain("spike", map.LocaleName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Internal test build", map.LocaleDescription, StringComparison.OrdinalIgnoreCase);
        Assert.All(map.LocaleDescription, c => Assert.InRange(c, ' ', '~'));
        Assert.Contains("EFT Arena", map.LocaleDescription);
        Assert.Equal("e6aa4591d3e1a59835d625b3", map.IdField);
        Assert.Equal("ArenaAutoService", map.InfiltrationName);
        Assert.Equal(server.RootElement.GetProperty("Id").GetString(), map.Key);
        Assert.True(map.UsesServerTime);
    }

    [Fact]
    public void ExitTransformsMatchTheSidecarAndServerEligibility()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("autoservice.exits.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.AutoService;
        var authored = sidecar.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        var shipped = server.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        Assert.Equal(2, map.Exits.Length);
        Assert.Equal(new[] { "Chop Shop East Gate", "Chop Shop West Gate" }, map.Exits.Select(e => e.Name));
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
        // the plan's values: positions (-349.0, -3.77, 488.5) and (-393.0, -3.79, 501.0), 4 x 3 x 4 m boxes, 8 s
        Assert.Equal(new[] { -349.0f, -3.77f, 488.5f }, new[] { map.Exits[0].X, map.Exits[0].Y, map.Exits[0].Z });
        Assert.Equal(new[] { -393.0f, -3.79f, 501.0f }, new[] { map.Exits[1].X, map.Exits[1].Y, map.Exits[1].Z });
        Assert.Empty(ArenaMapRegistration.ExitNameMismatches(
            shipped.Select(e => e.GetProperty("Name").GetString()), map));
    }

    [Fact]
    public void SpawnPointsWavesAndSquadsMatchTheBotsSidecar()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("autoservice.bots.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.AutoService;
        var bots = sidecar.RootElement;
        var zone = bots.GetProperty("zone").GetString();
        var shipped = server.RootElement.GetProperty("SpawnPointParams").EnumerateArray().ToArray();
        var players = bots.GetProperty("player_spawns").EnumerateArray().ToArray();
        var botSpawns = bots.GetProperty("bot_spawns").EnumerateArray().ToArray();

        // Four player rows on pocket markers (an All and a side row at each end) and seven bot rows from the 7 AI core points
        Assert.Equal(4, players.Length);
        Assert.Equal(7, botSpawns.Length);
        Assert.Equal(11, shipped.Length);
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
        Assert.Equal("BotZone_Main", zone);
        Assert.Equal(zone, root.GetProperty("OpenZones").GetString());
        Assert.True(root.GetProperty("DisabledForScav").GetBoolean());
        Assert.Empty(root.GetProperty("Loot").EnumerateArray());
        Assert.Equal(5, bots.GetProperty("waves").GetArrayLength());
        Assert.Equal(bots.GetProperty("waves").GetArrayLength(), root.GetProperty("waves").GetArrayLength());
        Assert.All(root.GetProperty("waves").EnumerateArray(), w => Assert.Equal(zone, w.GetProperty("SpawnPoints").GetString()));

        var squads = root.GetProperty("BossLocationSpawn").EnumerateArray().ToArray();
        Assert.Equal(bots.GetProperty("boss_waves").GetArrayLength(), squads.Length);
        Assert.Equal(4, squads.Length);
        Assert.All(squads, b => Assert.Contains(b.GetProperty("BossName").GetString(), new[] { "pmcUSEC", "pmcBEAR" }));
        Assert.All(squads, b => Assert.Equal(zone, b.GetProperty("BossZone").GetString()));
        Assert.Equal(2, squads.Count(b => b.GetProperty("BossName").GetString() == "pmcUSEC"));
        Assert.Equal(2, squads.Count(b => b.GetProperty("BossName").GetString() == "pmcBEAR"));
    }

    // PLAN-AUTOSERVICE item 8/12: tools/maps/autoservice.json is what BakeNavMesh and the Python model read, the compiled descriptor is what the
    // client opens at raid start. One list each; the gate strings are the unit-level paths of autoservice.doors.json.
    [Fact]
    public void OpenedBarriersAndBakeInputsMatchTheSidecar()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("autoservice.json")));
        var root = sidecar.RootElement;
        var map = ArenaMapRegistry.AutoService;

        Assert.Equal(root.GetProperty("opened_barriers").EnumerateArray().Select(e => e.GetString()), map.OpenedBarriers);
        Assert.Equal(root.GetProperty("start_gates").EnumerateArray().Select(e => e.GetString()), map.StartGates);
        Assert.Equal(root.GetProperty("held_gates").EnumerateArray().Select(e => e.GetString()), map.HeldGates);
        Assert.Equal(root.GetProperty("navmesh_bake_exclusions").EnumerateArray().Select(e => e.GetString()), map.NavMeshBakeExclusions);
        Assert.Equal(Floats(root.GetProperty("bake_bounds")), map.BakeBounds);
        Assert.Equal(new[] { -415.0f, -331.0f, -5.0f, 2.0f, 464.0f, 515.0f }, map.BakeBounds);
        Assert.Equal(root.GetProperty("navmesh_bake_layers").EnumerateArray().Select(e => e.GetInt32()), map.NavMeshBakeLayers);
        Assert.Equal(new[] { 9, 11, 13, 18, 29, 30 }, map.NavMeshBakeLayers);
        Assert.Equal(root.GetProperty("keep_level_borders").GetBoolean(), map.KeepLevelBorders);
        Assert.True(map.KeepLevelBorders);
        Assert.Equal(root.GetProperty("uses_server_time").GetBoolean(), map.UsesServerTime);
        Assert.Equal(root.GetProperty("puddle_water_material_names").EnumerateArray().Select(e => e.GetString()), map.PuddleWaterMaterialNames);
        Assert.False(root.TryGetProperty("puddle_extra_surfaces", out _));
        Assert.Empty(map.PuddleExtraSurfaces);
        // the four gates are unit-level barriers (rows match a barrier by the exact string, the last one carries a space), all start gates, none held
        Assert.Equal(new[] { "DOORS/big_gate_metal", "DOORS/equator_roller_shutters_01", "DOORS/roller_shutters_techlight_01", "DOORS/roller_shutters_techlight_01 (1)" }, map.OpenedBarriers);
        Assert.Equal(map.OpenedBarriers, map.StartGates);
        Assert.Empty(map.HeldGates);
        Assert.All(map.OpenedBarriers, path => Assert.Equal(ArenaGateRole.Start, map.GateRoleOf(path)));
        // the shader decision of item 11: four names, mirrored by the sidecar (ShaderStandinPolicyTests pins their rows and the switch)
        Assert.Equal(root.GetProperty("shader_fallback_names").EnumerateArray().Select(e => e.GetString()), map.ShaderFallbackNames);
        Assert.Equal(4, map.ShaderFallbackNames.Length);
        // the border cubes are the play space's bound and Arena has no data polygon: no authored borders, no lamp fill, no sun, no emission cap
        Assert.Empty(map.AuthoredBorders);
        Assert.Null(map.LampFill);
        Assert.Empty(map.SunPaths);
        Assert.Equal(0f, map.EmissionCap);
        Assert.Equal(0f, map.EmissionFloor);
        Assert.False(root.TryGetProperty("emission_cap", out _));
        Assert.False(root.TryGetProperty("emission_floor", out _));
        // waterless on purpose (no sea mesh); its crowd was added 2026-11-11 (PLAN-AUTOSERVICE phase C), reversing the "crowdless passt" decision of 2026-11-08
        Assert.False(map.HasNativeWater);
        Assert.False(root.TryGetProperty("has_native_water", out _));
    }

    [Fact]
    public void DoorPosesMatchTheSidecarAndEveryGateRowLiesBelowAStartGate()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("autoservice.doors.json")));
        var root = doc.RootElement;
        var map = ArenaMapRegistry.AutoService;
        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(map.Key, root.GetProperty("key").GetString());
        var sidecar = root.GetProperty("poses").EnumerateArray().ToArray();
        Assert.Equal(23, sidecar.Length);
        Assert.Equal(sidecar.Length, map.DoorPoses.Length);
        for (var i = 0; i < sidecar.Length; i++)
        {
            var s = sidecar[i];
            var p = map.DoorPoses[i];
            var label = $"pose {i} ({p.Barrier}/{p.Part}/{p.Node})";
            Assert.True(s.GetProperty("barrier").GetString() == p.Barrier, label);
            Assert.True(s.GetProperty("part").GetString() == p.Part, label);
            Assert.True(s.GetProperty("node").GetString() == p.Node, label);
            Assert.Equal((float)s.GetProperty("seconds").GetDouble(), p.Seconds);
            Assert.Equal(Floats(s.GetProperty("closed").GetProperty("position")), p.ClosedPosition);
            Assert.Equal(Floats(s.GetProperty("open").GetProperty("position")), p.OpenPosition);
            if (!p.IsSwitch) Assert.Contains(p.Barrier, map.StartGates);
        }
        Assert.Equal(4, map.DoorPoses.Count(p => p.IsSwitch));
        Assert.Equal(19, map.DoorPoses.Count(p => !p.IsSwitch));
        // the gate sounds join the rows by barrier: each of the four start gates has a row of autoservice.gatesounds.json
        using var sounds = JsonDocument.Parse(File.ReadAllText(Sidecar("autoservice.gatesounds.json")));
        var soundBarriers = sounds.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("barrier").GetString()).ToArray();
        Assert.Equal(map.OpenedBarriers.Order(), soundBarriers.Order());
    }

    [Fact]
    public void ScoreMonitorsMatchTheSidecarAndAreFourActiveScreensOfTheDesignStuffScene()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("autoservice.monitors.json")));
        var root = doc.RootElement;
        var map = ArenaMapRegistry.AutoService;
        Assert.Equal(map.Key, root.GetProperty("key").GetString());
        var rows = root.GetProperty("monitors").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.Equal(rows.Length, map.ScoreMonitors.Length);
        Assert.All(map.ScoreMonitors, m => Assert.True(m.ActiveInScene));
        Assert.All(map.ScoreMonitors, m => Assert.Equal("Arena_AutoService_DesignStuff", m.Scene));
        Assert.All(map.ScoreMonitors, m => Assert.Contains(m.Scene, map.AllSceneNames));
        // two timer faces (type word 0) and two alive faces (word 2); the TV (1) group holds all four
        Assert.Equal(new[] { 2, 0, 0, 2 }, map.ScoreMonitors.Select(m => m.ArenaType).ToArray());
        Assert.All(map.ScoreMonitors, m => Assert.StartsWith("TV (1)/Arena_Home_theater (", m.Path));
    }

    [Fact]
    public void ShippedBaseJsonParsesAsTheServerLocationModel()
    {
        var location = JsonSerializer.Deserialize<LocationBase>(File.ReadAllText(BaseJsonPath), Options);
        Assert.NotNull(location);
        var map = ArenaMapRegistry.AutoService;
        Assert.Equal(map.Key, location!.Id);
        Assert.Equal(map.IdField, location.IdField.ToString());
        Assert.Equal(11, location.SpawnPointParams!.Count());
        Assert.Equal(4, location.SpawnPointParams!.Count(p => p.Categories!.Contains("Player")));
        Assert.Equal(7, location.SpawnPointParams!.Count(p => p.Categories!.Contains("Bot")));
        Assert.Equal(2, location.Exits.Count());
        Assert.Equal(5, location.Waves!.Count);
        Assert.Equal(4, location.BossLocationSpawn!.Count);
        Assert.Equal(8, location.BotMax);
        Assert.True(location.DisabledForScav);
        Assert.Empty(location.Banners!);
    }
}
