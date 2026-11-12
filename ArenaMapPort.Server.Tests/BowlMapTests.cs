using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Bowl's gate door poses (docs/progress/door-poses.md): tools/maps/bowl.doors.json is written by
/// tools/extract/extract_door_poses.py from Arena's own gate animations, ArenaMapRegistry.Bowl.DoorPoses is the typed copy
/// the client reads. The two must agree number for number (validate_map_descriptors.py checks the same on the Python
/// side) and every pose must lie below one of the map's opened barriers. Equator and Bay5 (DotAnimator poses, schema 2)
/// have their own checks in DoorPoseSidecarTests, which also covers Bowl's schema-2 fields.
/// Since 2026-11-12 (more spawn rows, exits inside the team pockets) it also mirrors the exits and the spawn rows: descriptor ==
/// tools/maps/bowl.exits.json == db/arena_bowl/base.json, and base.json's SpawnPointParams == tools/maps/bowl.bots.json. The rules
/// behind the numbers (pocket footprint, 2.5 m to the box edge, the appended rows) are pinned on the Python side in
/// tools/tests/test_bowl_spawns.py, so a host-driven nudge of an exit touches the sidecar and the descriptor only.
/// </summary>
public sealed class BowlMapTests
{
    private static string Sidecar(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "maps", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    private static float[] Floats(JsonElement array) =>
        array.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();

    private static string BaseJsonPath => Path.Combine(AppContext.BaseDirectory, "db", "arena_bowl", "base.json");

    private static float[] PositionOf(JsonElement spawnPoint)
    {
        var p = spawnPoint.GetProperty("Position");
        return new[] { p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(), p.GetProperty("z").GetSingle() };
    }

    [Fact]
    public void ExitTransformsMatchTheSidecarAndServerEligibility()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("bowl.exits.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.Bowl;
        var authored = sidecar.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        var shipped = server.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        Assert.Equal(2, map.Exits.Length);
        Assert.Equal(new[] { "Bowl BTR Gate", "Bowl Army Gate" }, map.Exits.Select(e => e.Name));
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
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("bowl.bots.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.Bowl;
        var bots = sidecar.RootElement;
        var zone = bots.GetProperty("zone").GetString();
        var shipped = server.RootElement.GetProperty("SpawnPointParams").EnumerateArray().ToArray();
        var players = bots.GetProperty("player_spawns").EnumerateArray().ToArray();
        var botSpawns = bots.GetProperty("bot_spawns").EnumerateArray().ToArray();

        // Four player rows on pocket markers and 21 bot rows: 17 of Arena's 19 AI core points (the two inside or on the gate line of a pocket are
        // skipped) and four appended on 2026-11-12 (tools/tests/test_bowl_spawns.py states the rules of each group)
        Assert.Equal(4, players.Length);
        Assert.Equal(21, botSpawns.Length);
        Assert.Equal(players.Length + botSpawns.Length, shipped.Length);
        Assert.Equal(shipped.Length, shipped.Select(p => p.GetProperty("Id").GetString()).Distinct().Count());
        // the appended rows come after the originals, in the sidecar's order
        Assert.Equal(botSpawns.Select(p => p.GetProperty("id").GetString()),
                     shipped.Where(p => p.GetProperty("Categories").EnumerateArray().Any(c => c.GetString() == "Bot"))
                            .Select(p => p.GetProperty("Id").GetString()));

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
        // ArenaMode pins one donor row by id and exact floats (2026-11-12 rule R1: the original rows never move)
        var donor = shipped.Single(p => p.GetProperty("Id").GetString() == "arena_bowl-bot-core-02");
        Assert.Equal(new[] { 2.5f, 0.11f, -21.1f }, PositionOf(donor));

        var root = server.RootElement;
        Assert.Equal(bots.GetProperty("max_bots").GetInt32(), root.GetProperty("BotMax").GetInt32());
        Assert.Equal(zone, root.GetProperty("OpenZones").GetString());
        Assert.True(root.GetProperty("DisabledForScav").GetBoolean());
        Assert.Empty(root.GetProperty("Loot").EnumerateArray());
        Assert.Equal(bots.GetProperty("waves").GetArrayLength(), root.GetProperty("waves").GetArrayLength());
        Assert.Equal(bots.GetProperty("boss_waves").GetArrayLength(), root.GetProperty("BossLocationSpawn").GetArrayLength());
    }

    [Fact]
    public void DoorPosesMatchTheSidecarNumberForNumber()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("bowl.doors.json")));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(ArenaMapRegistry.Bowl.Key, root.GetProperty("key").GetString());
        Assert.Contains(root.GetProperty("source").GetProperty("scene").GetString(), ArenaMapRegistry.Bowl.AllSceneNames);

        var sidecar = root.GetProperty("poses").EnumerateArray().ToArray();
        var poses = ArenaMapRegistry.Bowl.DoorPoses;
        Assert.Equal(sidecar.Length, poses.Length);
        for (int i = 0; i < poses.Length; i++)
        {
            var s = sidecar[i];
            var p = poses[i];
            var label = $"pose {i} ({p.Barrier}/{p.Part}/{p.Node})";
            Assert.True(s.GetProperty("barrier").GetString() == p.Barrier, label + " barrier");
            Assert.True(s.GetProperty("part").GetString() == p.Part, label + " part");
            Assert.True(s.GetProperty("node").GetString() == p.Node, label + " node");
            Assert.True(s.GetProperty("clip").GetString() == p.Clip, label + " clip");
            Assert.Equal((float)s.GetProperty("seconds").GetDouble(), p.Seconds);
            Assert.Equal(Floats(s.GetProperty("closed").GetProperty("position")), p.ClosedPosition);
            Assert.Equal(Floats(s.GetProperty("closed").GetProperty("euler")), p.ClosedEuler);
            Assert.Equal(Floats(s.GetProperty("open").GetProperty("position")), p.OpenPosition);
            Assert.Equal(Floats(s.GetProperty("open").GetProperty("euler")), p.OpenEuler);
        }
    }

    [Fact]
    public void EveryPoseLiesBelowAnOpenedBarrierAndMovesSomething()
    {
        var bowl = ArenaMapRegistry.Bowl;
        Assert.NotEmpty(bowl.DoorPoses);
        var seen = new HashSet<string>();
        foreach (var pose in bowl.DoorPoses)
        {
            var label = $"{pose.Barrier}/{pose.Part}/{pose.Node}";
            Assert.Contains(pose.Barrier, bowl.OpenedBarriers);
            Assert.False(string.IsNullOrEmpty(pose.Part), label);
            Assert.False(string.IsNullOrEmpty(pose.Clip), label);
            Assert.DoesNotContain("frame", pose.Part, StringComparison.OrdinalIgnoreCase); // BoothDoorRepairs keeps frame parts
            Assert.True(pose.Seconds > 0f && float.IsFinite(pose.Seconds), label);
            foreach (var vector in new[] { pose.ClosedPosition, pose.ClosedEuler, pose.OpenPosition, pose.OpenEuler })
            {
                Assert.Equal(3, vector.Length);
                Assert.All(vector, v => Assert.True(float.IsFinite(v), label));
            }
            Assert.True(seen.Add(label), "duplicate pose " + label);
            var dx = pose.OpenPosition[0] - pose.ClosedPosition[0];
            var dy = pose.OpenPosition[1] - pose.ClosedPosition[1];
            var dz = pose.OpenPosition[2] - pose.ClosedPosition[2];
            var moved = Math.Sqrt(dx * dx + dy * dy + dz * dz) > 0.01
                || Enumerable.Range(0, 3).Any(i => Math.Abs(pose.OpenEuler[i] - pose.ClosedEuler[i]) > 1f);
            Assert.True(moved, label + " ends where it starts");
        }
    }

    [Fact]
    public void DoorPosesOfReturnsThePartsPosesInSidecarOrder()
    {
        var bowl = ArenaMapRegistry.Bowl;

        var btr = bowl.DoorPosesOf("DOORS_SPAWN_A", "BTR_82_closed");
        Assert.Equal(9, btr.Length);
        Assert.Equal("", btr[0].Node);                                   // the part itself first, then its wheels
        Assert.All(btr.Skip(1), p => Assert.StartsWith("BTR_82_wheel_", p.Node));
        Assert.Equal(new[] { 51.182f, -0.035f, -21.498f }, btr[0].ClosedPosition);
        Assert.Equal(new[] { 45.61f, -0.04f, -22.14f }, btr[0].OpenPosition);
        Assert.Equal("BTR_82_Respawn_Animation", btr[0].Clip);
        Assert.Equal(7.683f, btr[0].Seconds);

        var gate = bowl.DoorPosesOf("DOORS_SPAWN_A", "Bowl_Gates_Respawn_01");
        Assert.Single(gate);
        Assert.Equal("Bowl_Gates_Respawn_01_Gate", gate[0].Node);
        Assert.Equal(-1.833f, gate[0].OpenPosition[1]);

        var army = bowl.DoorPosesOf("DOORS_SPAWN_B", "Gates_Army_01");
        Assert.Equal(new[] { "Gates_Army/Gates_Army_door_01", "Gates_Army/Gates_Army_door_02" }, army.Select(p => p.Node));
        Assert.Equal(new[] { 142.922f, -161.335f }, army.Select(p => p.OpenEuler[2]));

        var ambulance = bowl.DoorPosesOf("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group");
        Assert.Equal(7, ambulance.Length);
        Assert.Equal("Bowl_Respawn_Ambulance_Animated", ambulance[0].Node);
        Assert.Equal(4.773f, ambulance[0].OpenPosition[0]);
        Assert.All(ambulance, p => Assert.StartsWith("Bowl_Respawn_Ambulance_Animated", p.Node));

        Assert.Equal(19, btr.Length + gate.Length + army.Length + ambulance.Length);
        Assert.Equal(bowl.DoorPoses.Length, btr.Length + gate.Length + army.Length + ambulance.Length); // no pose without a lookup
    }

    [Fact]
    public void DoorPosesOfIsExactAboutBarrierAndPart()
    {
        var bowl = ArenaMapRegistry.Bowl;
        Assert.Empty(bowl.DoorPosesOf("DOORS_SPAWN_B", "BTR_82_closed"));     // right part, wrong barrier
        Assert.Empty(bowl.DoorPosesOf("DOORS_SPAWN_A", "btr_82_closed"));     // names are case sensitive
        Assert.Empty(bowl.DoorPosesOf("DOORS_SPAWN_A", "Telfer_Help"));       // a part Arena does not animate
        Assert.Empty(bowl.DoorPosesOf("", ""));
        Assert.Empty(ArenaMapRegistry.Equator.DoorPosesOf("DOORS_SPAWN_A", "BTR_82_closed"));
    }

    [Fact]
    public void BowlsPosesAreClipPosesWithTheDefaultsOfSchemaTwo()
    {
        Assert.All(ArenaMapRegistry.Bowl.DoorPoses, p =>
        {
            Assert.Equal((ArenaDoorPose.SourceClip, ArenaDoorPose.EaseSmoothstep), (p.Source, p.Ease));
            Assert.Null(p.Curve);
            Assert.Null(p.At);
        });
    }
}
