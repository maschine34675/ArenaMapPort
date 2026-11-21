using System.Text.Json;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-YARD item 10 part 2 (docs/progress/yard-release.md, 2026-11-21): tools/maps/yard.json, yard.exits.json, yard.doors.json, yard.monitors.json and
/// yard.borders.json are the single sources of Yard's identity, bake inputs, exits, gate poses, score monitors and level-border walls, and the compiled
/// descriptor (what the client reads) must say exactly what they say. Yard is Catalog only until item 15: it is not in <see cref="ArenaMapRegistry.All"/>,
/// so no raid reaches any of it. Modelled on AutoServiceMapTests; the sidecar numbers themselves are pinned on the Python side (test_yard_bake_inputs,
/// test_yard_spawns, test_door_poses, test_score_monitors, test_level_borders). The door-pose and score-monitor rows are also covered by the Theories of
/// DoorPoseSidecarTests and ScoreMonitorSidecarTests (Yard is one of their maps). The pending pins of item 15 (Yard in All, base.json regenerated) stay pending here.
/// </summary>
public sealed class YardMapTests
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

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Sidecar(name)));

    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(v => v.GetSingle()).ToArray();

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(v => v.GetString()!).ToArray();

    private static readonly ArenaMapDescriptor Yard = ArenaMapRegistry.Yard;

    [Fact]
    public void IdentityIsTheDecidedOneNotTheSpike()
    {
        using var sidecar = Load("yard.json");
        var root = sidecar.RootElement;
        Assert.Equal("Arena: Block", Yard.LocaleName);                               // user decision 2026-11-20 (the key stays arena_yard)
        Assert.Equal(root.GetProperty("locale_name").GetString(), Yard.LocaleName);
        Assert.Equal(root.GetProperty("locale_description").GetString(), Yard.LocaleDescription);
        Assert.Equal(root.GetProperty("key").GetString(), Yard.Key);
        Assert.Equal(root.GetProperty("scripts_scene_name").GetString(), Yard.ScriptsSceneName);
        Assert.Equal(root.GetProperty("id_field").GetString(), Yard.IdField);
        Assert.Equal(root.GetProperty("infiltration_name").GetString(), Yard.InfiltrationName);
        Assert.Equal(root.GetProperty("icon_x").GetInt32(), Yard.IconX);
        Assert.Equal(root.GetProperty("icon_y").GetInt32(), Yard.IconY);
        Assert.Equal(root.GetProperty("db_base_json_path").GetString(), Yard.DbBaseJsonPath);
        Assert.Equal(Strings(root.GetProperty("all_scene_names")), Yard.AllSceneNames);
        Assert.Equal("arena_yard", Yard.Key);
        Assert.Equal("187d5cbc21744c97e8348e76", Yard.IdField);
        Assert.Equal("ArenaYard", Yard.InfiltrationName);
        Assert.Equal((691, 799), (Yard.IconX, Yard.IconY));                          // the stub position of markers.json until item 25
        // The port's own fallback words (the official text is in the ignored pack and wins when it is there): factual, ASCII, no spike wording.
        Assert.DoesNotContain("spike", Yard.LocaleName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Internal test build", Yard.LocaleDescription, StringComparison.OrdinalIgnoreCase);
        Assert.All(Yard.LocaleDescription, c => Assert.InRange(c, ' ', '~'));
        Assert.Contains("EFT Arena", Yard.LocaleDescription);
        Assert.True(Yard.UsesServerTime);                                            // user decision 2026-11-20 "serverzeit ok"
        Assert.Equal(root.GetProperty("uses_server_time").GetBoolean(), Yard.UsesServerTime);
        Assert.False(Yard.UsesFixedStartTimes);
    }

    [Fact]
    public void YardIsInTheCatalogButNotReleasedUntilItem15()
    {
        Assert.Contains(Yard, ArenaMapRegistry.Catalog);
        Assert.DoesNotContain(Yard, ArenaMapRegistry.All);                           // item 15 inverts this and every pin below that says "Catalog only"
        Assert.DoesNotContain("arena_yard", ArenaMapRegistry.All.Select(m => m.Key));
        Assert.Equal(6, ArenaMapRegistry.All.Count);
        // The shipped placeholder base.json is item 15's (make_base_json.py regenerates it with the 22 spawn points, the waves and the exits): until then it still carries the
        // spike name, afterwards the descriptor's.
        string baseJson = Path.Combine(AppContext.BaseDirectory, "db", "arena_yard", "base.json");
        if (File.Exists(baseJson))
        {
            using var server = JsonDocument.Parse(File.ReadAllText(baseJson));
            Assert.Contains(server.RootElement.GetProperty("Name").GetString(), new[] { "Arena: Yard (spike)", Yard.LocaleName });
        }
    }

    [Fact]
    public void BakeInputsMirrorTheSidecar()
    {
        using var sidecar = Load("yard.json");
        var root = sidecar.RootElement;
        Assert.Equal(Strings(root.GetProperty("opened_barriers")), Yard.OpenedBarriers);
        Assert.Equal(Strings(root.GetProperty("start_gates")), Yard.StartGates);
        Assert.Equal(Strings(root.GetProperty("held_gates")), Yard.HeldGates);
        Assert.Equal(Strings(root.GetProperty("navmesh_bake_exclusions")), Yard.NavMeshBakeExclusions);
        Assert.Equal(Floats(root.GetProperty("bake_bounds")), Yard.BakeBounds);
        Assert.Equal(root.GetProperty("navmesh_bake_layers").EnumerateArray().Select(v => v.GetInt32()).ToArray(), Yard.NavMeshBakeLayers);
        Assert.Equal(root.GetProperty("keep_level_borders").GetBoolean(), Yard.KeepLevelBorders);
        Assert.Equal(Strings(root.GetProperty("puddle_water_material_names")), Yard.PuddleWaterMaterialNames);
        Assert.Equal(Strings(root.GetProperty("shader_fallback_names")), Yard.ShaderFallbackNames);
        // the decided numbers (test_yard_bake_inputs pins what lies behind them)
        Assert.Equal(new[] { "DOORS/Metal_gate_1", "DOORS/Construction_Cradle_02" }, Yard.OpenedBarriers);
        Assert.Equal(Yard.OpenedBarriers, Yard.StartGates);
        Assert.Empty(Yard.HeldGates);
        Assert.Empty(Yard.NavMeshBakeExclusions);
        Assert.Equal(new[] { -67.0f, 21.0f, 4.0f, 14.0f, -1.0f, 162.0f }, Yard.BakeBounds);
        Assert.Equal(new[] { 0, 9, 11, 13, 18, 29, 30 }, Yard.NavMeshBakeLayers);
        Assert.True(Yard.KeepLevelBorders);
        Assert.Contains(29, Yard.NavMeshBakeLayers);                                 // the kept cubes are bake sources
        Assert.Equal(new[] { "0877f2335044042e" }, Yard.PuddleWaterMaterialNames);
        Assert.Empty(Yard.PuddleExtraSurfaces);
        Assert.Equal(new[]
        {
            "Custom/Billboard_FogSheet_Simple", "Cloth/ClothShader", "Cloth/ClothShader_backface", "p0/Reflective/Bumped Specular SMap Baked",
            "Nature/SpeedTree", "Impostors/Examples/StandardCrossfade",
        }, Yard.ShaderFallbackNames);
        // keys the sidecar leaves out stay at the descriptor's defaults (test_yard_bake_inputs.OMITTED_SIDECAR_KEYS)
        Assert.Equal(0f, Yard.EmissionCap);
        Assert.Equal(0f, Yard.EmissionFloor);
        Assert.False(Yard.HasNativeWater);
        Assert.Empty(Yard.AuthoredBorders);
        Assert.Empty(Yard.DistantShadowCasterPaths);
        Assert.Empty(Yard.RestoredShadowCasterRoots);
        Assert.Null(Yard.Dust);
        Assert.Null(Yard.LampFill);
        Assert.All(Yard.OpenedBarriers, p => Assert.Equal(ArenaGateRole.Start, Yard.GateRoleOf(p)));
    }

    [Fact]
    public void ExitsMatchTheSidecarAndEachPocketHoldsOne()
    {
        using var sidecar = Load("yard.exits.json");
        var authored = sidecar.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        Assert.Equal(2, Yard.Exits.Length);
        Assert.Equal(new[] { "Block South Gate", "Block North Gate" }, Yard.Exits.Select(e => e.Name));
        Assert.Equal(authored.Length, Yard.Exits.Length);
        for (var i = 0; i < authored.Length; i++)
        {
            var e = Yard.Exits[i];
            Assert.Equal(authored[i].GetProperty("name").GetString(), e.Name);
            Assert.Equal(Floats(authored[i].GetProperty("position")), new[] { e.X, e.Y, e.Z });
            Assert.Equal(Floats(authored[i].GetProperty("box_size")), new[] { e.SizeX, e.SizeY, e.SizeZ });
            Assert.Equal(authored[i].GetProperty("rotation").GetSingle(), e.RotationY);
            Assert.Equal(new[] { 4.0f, 3.0f, 4.0f }, new[] { e.SizeX, e.SizeY, e.SizeZ });
        }
        // R3: one exit per team pocket, its centre inside the pocket's spawn-bound footprint of yard.modes.json
        using var modes = Load("yard.modes.json");
        var bounds = modes.RootElement.GetProperty("spawn_bounds").EnumerateArray().ToArray();
        Assert.Equal(2, bounds.Length);
        foreach (var exit in Yard.Exits)
        {
            var holders = bounds.Where(b => MarginInside(b, exit.X, exit.Y, exit.Z) >= 0.0).Select(b => b.GetProperty("name").GetString()).ToArray();
            Assert.True(holders.Length == 1, $"{exit.Name} lies inside {holders.Length} pockets");
        }
        Assert.Equal(bounds.Length, Yard.Exits.Select(e => bounds.Single(b => MarginInside(b, e.X, e.Y, e.Z) >= 0.0).GetProperty("name").GetString()).Distinct().Count());
    }

    /// <summary>Smallest distance from a point to a face of a spawn-bound box, along the box axes (negative = outside); the Python twin is extract_mode_objects.box_margin.</summary>
    private static double MarginInside(JsonElement bound, float x, float y, float z)
    {
        var q = bound.GetProperty("rotation").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var c = bound.GetProperty("center").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var size = bound.GetProperty("size").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        double qx = -q[0], qy = -q[1], qz = -q[2], w = q[3];           // conjugate: world -> box frame
        double dx = x - c[0], dy = y - c[1], dz = z - c[2];
        double tx = 2.0 * (qy * dz - qz * dy), ty = 2.0 * (qz * dx - qx * dz), tz = 2.0 * (qx * dy - qy * dx);
        var local = new[] { dx + w * tx + (qy * tz - qz * ty), dy + w * ty + (qz * tx - qx * tz), dz + w * tz + (qx * ty - qy * tx) };
        return Enumerable.Range(0, 3).Min(i => size[i] / 2.0 - Math.Abs(local[i]));
    }

    [Fact]
    public void DoorPosesAndScoreMonitorsAreInTheDescriptor()
    {
        // the number-for-number mirrors are DoorPoseSidecarTests and ScoreMonitorSidecarTests (Yard is one of their maps); here the shape the plan decided
        Assert.Equal(3, Yard.DoorPoses.Length);
        Assert.Equal(new[] { "DOORS/Construction_Cradle_02", "DOORS/Metal_gate_1", "DOORS/Metal_gate_1" }, Yard.DoorPoses.Select(p => p.Barrier));
        Assert.All(Yard.DoorPoses, p => Assert.Equal(ArenaDoorPose.SourceDotAnimator, p.Source));
        var cradle = Yard.DoorPoses[0];
        Assert.Equal((ArenaDoorPose.EaseOutBack, 8.0f), (cradle.Ease, cradle.Seconds));
        Assert.Equal(2.446f, cradle.OpenPosition[2] - cradle.ClosedPosition[2], 3);
        Assert.All(Yard.DoorPoses.Skip(1), p => Assert.Equal((ArenaDoorPose.EaseLinear, 1.7f), (p.Ease, p.Seconds)));
        Assert.Equal(new[] { 90.0f, -90.0f }, Yard.DoorPoses.Skip(1).Select(p => p.OpenEuler[2]));
        Assert.All(Yard.DoorPoses, p => Assert.Contains(p.Barrier, Yard.OpenedBarriers));
        Assert.Equal(8, Yard.ScoreMonitors.Length);
        Assert.Equal(8, Yard.ScoreMonitors.Count(m => m.ActiveInScene));
        Assert.All(Yard.ScoreMonitors, m => Assert.Equal("Arena_Yard_TeamFight_DesignStuff", m.Scene));
        Assert.Equal((5, 3), (Yard.ScoreMonitors.Count(m => m.ArenaType == 0), Yard.ScoreMonitors.Count(m => m.ArenaType == 2)));   // five timers, three alive boards, no score board
    }

    [Fact]
    public void LevelBorderBoxesMatchTheBordersSidecarNumberForNumber()
    {
        using var sidecar = Load("yard.borders.json");
        var root = sidecar.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(Yard.Key, root.GetProperty("key").GetString());
        Assert.Contains(root.GetProperty("source_scene").GetString(), Yard.AllSceneNames);
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(104, rows.Length);
        Assert.Equal(root.GetProperty("count").GetInt32(), rows.Length);
        Assert.Equal(rows.Length, Yard.LevelBorderBoxes.Length);
        Assert.Equal(new[] { 29 }, root.GetProperty("layers").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new[] { float.MinValue, float.MinValue, float.MinValue };
        var bounds = new float[3];
        var upper = new float[3];
        double worst = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var b = Yard.LevelBorderBoxes[i];
            var label = $"row {i} ({row.GetProperty("path").GetString()})";
            Assert.True(row.GetProperty("active").GetBoolean() && row.GetProperty("enabled").GetBoolean() && !row.GetProperty("is_trigger").GetBoolean(), label);
            Assert.Equal(29, row.GetProperty("layer").GetInt32());
            Assert.True(Floats(row.GetProperty("center")).SequenceEqual(new[] { b.CenterX, b.CenterY, b.CenterZ }), label + " centre");
            Assert.True(Floats(row.GetProperty("size")).SequenceEqual(new[] { b.SizeX, b.SizeY, b.SizeZ }), label + " size");
            Assert.True(Floats(row.GetProperty("rotation")).SequenceEqual(new[] { b.RotationX, b.RotationY, b.RotationZ, b.RotationW }), label + " rotation");
            Assert.True(LevelBorderBoxModel.IsUsable(b), label);
            // the AABB the extractor wrote from the cube's world matrix follows from the box the client raises
            var aabb = row.GetProperty("aabb").EnumerateArray().Select(Floats).ToArray();
            LevelBorderBoxModel.WorldBounds(b, bounds, upper);
            for (var k = 0; k < 3; k++)
            {
                worst = Math.Max(worst, Math.Max(Math.Abs(bounds[k] - aabb[0][k]), Math.Abs(upper[k] - aabb[1][k])));
                min[k] = Math.Min(min[k], aabb[0][k]);
                max[k] = Math.Max(max[k], aabb[1][k]);
            }
        }
        Assert.True(worst < 0.002, $"largest AABB difference {worst} m");
        var declared = root.GetProperty("bounds").EnumerateArray().Select(Floats).ToArray();
        Assert.Equal(declared[0], min);
        Assert.Equal(declared[1], max);
        Assert.Equal(new[] { -71.6863f, 5.087f, -0.2058f }, declared[0]);
        Assert.Equal(new[] { 10.6488f, 20.3267f, 151.1742f }, declared[1]);
        Assert.Equal(104, LevelBorderBoxModel.CountUsable(Yard.LevelBorderBoxes));
    }

    // The walls must not shut a spawn out: no Player or Bot row stands within 2 m of a wall, no exit box centre within 2.5 m (a box corner may come as near as 1 m: the north
    // exit's east edge lies 1.08 m from row 54 and its centre 3.08 m). Distances are from the point to the oriented box.
    [Fact]
    public void NoSpawnRowAndNoExitCentreStandsNearAWall()
    {
        using var bots = Load("yard.bots.json");
        using var exits = Load("yard.exits.json");
        var points = new List<(string Name, float[] Position, double Clearance)>();
        foreach (var key in new[] { "player_spawns", "bot_spawns" })
            foreach (var row in bots.RootElement.GetProperty(key).EnumerateArray())
                points.Add((row.GetProperty("id").GetString()!, Floats(row.GetProperty("position")), 2.0));
        foreach (var exit in exits.RootElement.GetProperty("exits").EnumerateArray())
            points.Add((exit.GetProperty("name").GetString()! + " centre", Floats(exit.GetProperty("position")), 2.5));
        Assert.Equal(4 + 18 + 2, points.Count);
        foreach (var (name, position, clearance) in points)
        {
            var nearest = Yard.LevelBorderBoxes.Select((b, i) => (Index: i, Distance: DistanceToBox(b, position))).MinBy(t => t.Distance);
            Assert.True(nearest.Distance >= clearance, $"{name} is {nearest.Distance:F2} m from wall {nearest.Index} (needs {clearance} m)");
        }
    }

    private static double DistanceToBox(ArenaBorderBox box, float[] point)
    {
        // R^T (p - c) with R from the unit quaternion; the outside distance along each box axis
        double x = box.RotationX, y = box.RotationY, z = box.RotationZ, w = box.RotationW;
        double[,] r =
        {
            { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w) },
            { 2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w) },
            { 2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y) },
        };
        double[] d = { point[0] - box.CenterX, point[1] - box.CenterY, point[2] - box.CenterZ };
        double[] half = { box.SizeX / 2.0, box.SizeY / 2.0, box.SizeZ / 2.0 };
        double sum = 0;
        for (var j = 0; j < 3; j++)
        {
            var local = r[0, j] * d[0] + r[1, j] * d[1] + r[2, j] * d[2];
            var outside = Math.Max(Math.Abs(local) - half[j], 0.0);
            sum += outside * outside;
        }
        return Math.Sqrt(sum);
    }
}
