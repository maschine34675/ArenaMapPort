using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Bowl's gate door poses (docs/progress/door-poses.md): tools/maps/bowl.doors.json is written by
/// tools/extract/extract_door_poses.py from Arena's own gate animations, ArenaMapRegistry.Bowl.DoorPoses is the typed copy
/// the client reads. The two must agree number for number (validate_map_descriptors.py checks the same on the Python
/// side), every pose must lie below one of the map's opened barriers, and no other map carries poses.
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

    [Fact]
    public void DoorPosesMatchTheSidecarNumberForNumber()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("bowl.doors.json")));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
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
    public void OnlyBowlCarriesDoorPoses()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            if (ReferenceEquals(map, ArenaMapRegistry.Bowl)) continue;
            Assert.Empty(map.DoorPoses);
        }
    }
}
