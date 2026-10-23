using System.Text.Json;
using System.Text.RegularExpressions;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The gate poses of Bowl, Equator and Bay5 (docs/progress/door-poses.md): tools/maps/&lt;stem&gt;.doors.json is written by
/// tools/extract/extract_door_poses.py (Bowl from Arena's Mecanim clips, Equator and Bay5 from the DotAnimator script of its
/// gates), ArenaMapRegistry.&lt;Map&gt;.DoorPoses is the typed copy the client reads. The two must agree number for number
/// (validate_map_descriptors.py checks the same on the Python side), the schema-2 rules of source / ease / curve / at must
/// hold, and the lookup API (DoorPosesOf with and without a barrier root position) must tell repeated hierarchy paths apart.
/// </summary>
public sealed class DoorPoseSidecarTests
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

    private static ArenaMapDescriptor MapOf(string stem) => stem switch
    {
        "bowl" => ArenaMapRegistry.Bowl,
        "equator" => ArenaMapRegistry.Equator,
        "bay5" => ArenaMapRegistry.Bay5,
        _ => throw new ArgumentOutOfRangeException(nameof(stem)),
    };

    public static IEnumerable<object[]> Maps => new[] { new object[] { "bowl" }, new object[] { "equator" }, new object[] { "bay5" } };

    /// <summary>Equator's booth doors are not opened barriers (BoothDoorRepairs.OpenDoors finds the leaves by name); their poses
    /// name the assembly root, with its world position in At.</summary>
    private static readonly Regex BoothAssemblyRoot =
        new(@"^(SPAWN_DOORS|SBG_Arena_Equator/SOO/Respawn_2)/Door_Sliding_Respawn( \(\d+\))?$");

    [Theory]
    [MemberData(nameof(Maps))]
    public void DoorPosesMatchTheSidecarNumberForNumber(string stem)
    {
        var map = MapOf(stem);
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(stem + ".doors.json")));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(map.Key, root.GetProperty("key").GetString());
        Assert.Contains(root.GetProperty("source").GetProperty("scene").GetString(), map.AllSceneNames);

        var sidecar = root.GetProperty("poses").EnumerateArray().ToArray();
        var poses = map.DoorPoses;
        Assert.Equal(sidecar.Length, poses.Length);
        for (int i = 0; i < poses.Length; i++)
        {
            var s = sidecar[i];
            var p = poses[i];
            var label = $"{stem} pose {i} ({p.Barrier}/{p.Part}/{p.Node})";
            Assert.True(s.GetProperty("barrier").GetString() == p.Barrier, label + " barrier");
            Assert.True(s.GetProperty("part").GetString() == p.Part, label + " part");
            Assert.True(s.GetProperty("node").GetString() == p.Node, label + " node");
            Assert.True(s.GetProperty("clip").GetString() == p.Clip, label + " clip");
            Assert.True(s.GetProperty("source").GetString() == p.Source, label + " source");
            Assert.True(s.GetProperty("ease").GetString() == p.Ease, label + " ease");
            Assert.Equal((float)s.GetProperty("seconds").GetDouble(), p.Seconds);
            Assert.Equal(Floats(s.GetProperty("closed").GetProperty("position")), p.ClosedPosition);
            Assert.Equal(Floats(s.GetProperty("closed").GetProperty("euler")), p.ClosedEuler);
            Assert.Equal(Floats(s.GetProperty("open").GetProperty("position")), p.OpenPosition);
            Assert.Equal(Floats(s.GetProperty("open").GetProperty("euler")), p.OpenEuler);

            var curve = s.GetProperty("curve");
            if (curve.ValueKind == JsonValueKind.Null)
                Assert.Null(p.Curve);
            else
                Assert.Equal(curve.EnumerateArray().SelectMany(Floats).ToArray(), p.Curve);

            var at = s.GetProperty("at");
            if (at.ValueKind == JsonValueKind.Null)
                Assert.Null(p.At);
            else
                Assert.Equal(Floats(at), p.At);
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void EverySchemaTwoRuleHolds(string stem)
    {
        var map = MapOf(stem);
        Assert.NotEmpty(map.DoorPoses);
        var seen = new List<(string key, float[]? at)>();
        foreach (var pose in map.DoorPoses)
        {
            var label = $"{stem}: {pose.Barrier}/{pose.Part}/{pose.Node}";
            Assert.False(string.IsNullOrEmpty(pose.Barrier), label);
            Assert.False(string.IsNullOrEmpty(pose.Clip), label);
            Assert.False(pose.Part == "" && pose.Node != "", label + " a node needs a part");
            Assert.DoesNotContain("frame", pose.Part, StringComparison.OrdinalIgnoreCase); // BoothDoorRepairs keeps frame parts
            Assert.True(pose.Seconds > 0f && float.IsFinite(pose.Seconds), label);
            foreach (var vector in new[] { pose.ClosedPosition, pose.ClosedEuler, pose.OpenPosition, pose.OpenEuler })
            {
                Assert.Equal(3, vector.Length);
                Assert.All(vector, v => Assert.True(float.IsFinite(v), label));
            }
            var dx = pose.OpenPosition[0] - pose.ClosedPosition[0];
            var dy = pose.OpenPosition[1] - pose.ClosedPosition[1];
            var dz = pose.OpenPosition[2] - pose.ClosedPosition[2];
            var moved = Math.Sqrt(dx * dx + dy * dy + dz * dz) > 0.01
                || Enumerable.Range(0, 3).Any(i => Math.Abs(pose.OpenEuler[i] - pose.ClosedEuler[i]) > 1f);
            Assert.True(moved, label + " ends where it starts");

            Assert.Contains(pose.Source, new[] { ArenaDoorPose.SourceClip, ArenaDoorPose.SourceDotAnimator, ArenaDoorPose.SourceBorrowed });
            Assert.Contains(pose.Ease, new[] { ArenaDoorPose.EaseSmoothstep, ArenaDoorPose.EaseLinear, ArenaDoorPose.EaseInOutQuint, ArenaDoorPose.EaseCurve });
            // smoothstep is the client's stand-in for Mecanim clips and only for them
            Assert.Equal(pose.Source == ArenaDoorPose.SourceClip, pose.Ease == ArenaDoorPose.EaseSmoothstep);
            if (pose.Source == ArenaDoorPose.SourceDotAnimator) Assert.Equal("DotAnimator", pose.Clip);
            if (pose.Source == ArenaDoorPose.SourceBorrowed) Assert.StartsWith("borrowed from ", pose.Clip);

            if (pose.Ease == ArenaDoorPose.EaseCurve)
            {
                Assert.NotNull(pose.Curve);
                var c = pose.Curve!;
                Assert.True(c.Length >= 8 && c.Length % 4 == 0, label + " curve is time,value,inSlope,outSlope per key, at least two keys");
                Assert.Equal(c.Length / 4, pose.CurveKeyCount);
                Assert.All(c, v => Assert.True(float.IsFinite(v), label));
                for (int k = 1; k < c.Length / 4; k++) Assert.True(c[4 * k] > c[4 * (k - 1)], label + " curve times increase");
                Assert.Equal(0f, c[0]);
                Assert.Equal(1f, c[c.Length - 4]);
                Assert.Equal(0f, c[1]);
                Assert.Equal(1f, c[c.Length - 3]);
            }
            else
            {
                Assert.Null(pose.Curve);
                Assert.Equal(0, pose.CurveKeyCount);
            }

            if (pose.At != null) Assert.Equal(3, pose.At.Length);
            foreach (var other in seen.Where(o => o.key == label))
            {
                Assert.True(pose.At != null && other.at != null, label + " occurs twice: both need At to tell the instances apart");
                var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (pose.At![i] - other.at![i]) * (pose.At[i] - other.at[i])));
                Assert.True(d > ArenaDoorPose.AtTolerance, label + " twice at the same place");
            }
            seen.Add((label, pose.At));
        }
        // under one barrier either every pose names its root's position or none does
        foreach (var group in map.DoorPoses.GroupBy(p => p.Barrier))
            Assert.Single(group.Select(p => p.At == null).Distinct());
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void EveryPoseBarrierIsAnOpenedBarrierOrAnEquatorBoothRoot(string stem)
    {
        var map = MapOf(stem);
        foreach (var pose in map.DoorPoses)
        {
            var boothRoot = stem == "equator" && BoothAssemblyRoot.IsMatch(pose.Barrier);
            Assert.True(boothRoot || map.OpenedBarriers.Contains(pose.Barrier),
                $"{stem}: pose barrier '{pose.Barrier}' is neither an OpenedBarriers entry nor a booth assembly root");
        }
    }

    [Fact]
    public void BowlsPosesAreAllClipsWithTheClientsSmoothstepAndNoAt()
    {
        Assert.Equal(19, ArenaMapRegistry.Bowl.DoorPoses.Length);
        Assert.All(ArenaMapRegistry.Bowl.DoorPoses, p =>
        {
            Assert.Equal(ArenaDoorPose.SourceClip, p.Source);
            Assert.Equal(ArenaDoorPose.EaseSmoothstep, p.Ease);
            Assert.Null(p.Curve);
            Assert.Null(p.At);
        });
    }

    [Fact]
    public void ADefaultConstructedPoseIsAClipPoseWithSmoothstepAndNoCurveOrAt()
    {
        var pose = new ArenaDoorPose("B", "P", "", "Clip", 1f, new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 0f, 0f });
        Assert.Equal(("clip", "smoothstep"), (pose.Source, pose.Ease));
        Assert.Null(pose.Curve);
        Assert.Null(pose.At);
        Assert.Equal(0, pose.CurveKeyCount);
        Assert.True(pose.AppliesTo(1000f, -5f, 7f));                       // no At: every instance
    }

    [Fact]
    public void EquatorHasTenBoothDoorPosesEightFromArenasScriptAndTwoBorrowed()
    {
        var poses = ArenaMapRegistry.Equator.DoorPoses;
        Assert.Equal(10, poses.Length);
        Assert.Equal(8, poses.Count(p => p.Source == ArenaDoorPose.SourceDotAnimator));
        Assert.Equal(2, poses.Count(p => p.Source == ArenaDoorPose.SourceBorrowed));
        Assert.All(poses, p =>
        {
            Assert.Equal(ArenaDoorPose.EaseInOutQuint, p.Ease);
            Assert.Equal("", p.Node);
            Assert.NotNull(p.At);
            Assert.Equal(0.048f, p.At![1]);
            var sign = p.Part == "Door_Sliding_Respawn_L" ? 1f : -1f;
            Assert.Equal(new[] { sign * 2f, 0f, 0f }, p.OpenPosition);        // Arena's absolute local target, not an offset
            Assert.Equal(p.ClosedEuler, p.OpenEuler);
        });
        // exactly one leaf takes 7 s (the R leaf of GateEntity 24002), every other one 5 s
        var seven = poses.Single(p => p.Seconds == 7f);
        Assert.Equal(("SPAWN_DOORS/Door_Sliding_Respawn", "Door_Sliding_Respawn_R"), (seven.Barrier, seven.Part));
        Assert.Equal(new[] { 4.535f, 0.048f, 40.02f }, seven.At);
        Assert.All(poses.Where(p => p != seven), p => Assert.Equal(5f, p.Seconds));
        var borrowed = poses.Where(p => p.Source == ArenaDoorPose.SourceBorrowed).ToArray();
        Assert.All(borrowed, p => Assert.Equal("SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)", p.Barrier));
        Assert.All(borrowed, p => Assert.Equal("borrowed from SPAWN_DOORS/Door_Sliding_Respawn (1)", p.Clip));
        Assert.Equal(new[] { 0.683f, -0.686f }, borrowed.Select(p => p.ClosedPosition[0]).OrderByDescending(v => v));
    }

    [Fact]
    public void EquatorsTwoUnnumberedAssembliesAreToldApartByTheirRootPosition()
    {
        var eq = ArenaMapRegistry.Equator;
        const string barrier = "SPAWN_DOORS/Door_Sliding_Respawn";
        Assert.Empty(eq.DoorPosesOf(barrier, "Door_Sliding_Respawn_R"));                       // the 2-argument form never guesses
        var west = eq.DoorPosesOf(barrier, "Door_Sliding_Respawn_R", -4.328f, 0.048f, 40.02f);
        var east = eq.DoorPosesOf(barrier, "Door_Sliding_Respawn_R", 4.535f, 0.048f, 40.02f);
        Assert.Equal(5f, Assert.Single(west).Seconds);
        Assert.Equal(7f, Assert.Single(east).Seconds);
        Assert.Equal(-0.709f, west[0].ClosedPosition[0]);
        Assert.Equal(-0.722f, east[0].ClosedPosition[0]);
        Assert.Equal(2, eq.DoorPosesOfBarrier(barrier, 4.535f, 0.048f, 40.02f).Length);       // both leaves of that assembly, no part filter
        Assert.Empty(eq.DoorPosesOfBarrier(barrier));
        Assert.Empty(eq.DoorPosesOf(barrier, "Door_Sliding_Respawn_R", 0f, 0f, 0f));          // a root somewhere else: no pose
        Assert.Single(eq.DoorPosesOf("SPAWN_DOORS/Door_Sliding_Respawn (1)", "Door_Sliding_Respawn_L", -4.46f, 0.048f, -35.045f));
        Assert.Single(eq.DoorPosesOf("SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)", "Door_Sliding_Respawn_R", -3.753f, 0.048f, -45.87f));
        Assert.Empty(eq.DoorPosesOf("SPAWN_DOORS/Door_Sliding_Respawn (1)", "Door_Sliding_Respawn_L", 4.403f, 0.048f, -35.045f));   // (2)'s position
    }

    [Fact]
    public void TheAtToleranceIsFiveCentimetres()
    {
        var eq = ArenaMapRegistry.Equator;
        const string barrier = "SPAWN_DOORS/Door_Sliding_Respawn (1)";
        Assert.Single(eq.DoorPosesOf(barrier, "Door_Sliding_Respawn_L", -4.46f + 0.04f, 0.048f, -35.045f));
        Assert.Empty(eq.DoorPosesOf(barrier, "Door_Sliding_Respawn_L", -4.46f + 0.06f, 0.048f, -35.045f));
        Assert.Equal(0.05f, ArenaDoorPose.AtTolerance);
        var pose = eq.DoorPosesOf(barrier, "Door_Sliding_Respawn_L", -4.46f, 0.048f, -35.045f)[0];
        Assert.True(pose.AppliesTo(-4.46f, 0.048f + 0.03f, -35.045f + 0.03f));                // 4.2 cm on a diagonal
        Assert.False(pose.AppliesTo(-4.46f, 0.048f + 0.04f, -35.045f + 0.04f));               // 5.7 cm
    }

    [Fact]
    public void Bay5HasThreeRollersAndABorrowedOneThatLiftWithTheirWholeRoot()
    {
        var bay = ArenaMapRegistry.Bay5;
        var rollers = bay.DoorPoses.Where(p => p.Part == "").ToArray();
        Assert.Equal(4, rollers.Length);
        Assert.All(rollers, p =>
        {
            Assert.Equal("", p.Node);
            Assert.Equal((4f, ArenaDoorPose.EaseLinear), (p.Seconds, p.Ease));
            Assert.Null(p.At);                                                   // unique paths
            Assert.Equal(p.ClosedPosition[0], p.OpenPosition[0]);               // only y changes
            Assert.Equal(p.ClosedPosition[2], p.OpenPosition[2]);
            Assert.Equal(p.ClosedEuler, p.OpenEuler);
        });
        float Open(string name) => Assert.Single(bay.DoorPosesOf("WAREHOUSE_DOORS (1)/" + name, "")).OpenPosition[1];
        Assert.Equal(3.213f, Open("Bay5_Warehouse_Gate_Door_01 (4)"));
        Assert.Equal(3.222f, Open("Bay5_Warehouse_Gate_Door_01 (2)"));
        Assert.Equal(3.323f, Open("One_man_Door"));
        var three = Assert.Single(bay.DoorPosesOf("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)", ""));
        Assert.Equal(ArenaDoorPose.SourceBorrowed, three.Source);
        var four = Assert.Single(bay.DoorPosesOf("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)", ""));
        Assert.Equal(four.OpenPosition[1] - four.ClosedPosition[1], three.OpenPosition[1] - three.ClosedPosition[1], 2);   // the neighbour's lift, 3.089 m
        Assert.StartsWith("borrowed from WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)", three.Clip);
    }

    [Fact]
    public void Bay5sTwoDepotGatesAreToldApartByTheirRootPositionAndSwingOnACurve()
    {
        var bay = ArenaMapRegistry.Bay5;
        const string barrier = "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame";
        Assert.Empty(bay.DoorPosesOf(barrier, "Gates_Depot_Door_L"));                          // 2 instances: needs a position
        Assert.Empty(bay.DoorPosesOfBarrier(barrier));

        var first = new[] { 50.574f, -3.895f, 7.525f };
        var second = new[] { 69.045f, -3.883f, -6.317f };
        var firstL = Assert.Single(bay.DoorPosesOf(barrier, "Gates_Depot_Door_L", first[0], first[1], first[2]));
        var firstR = Assert.Single(bay.DoorPosesOf(barrier, "Gates_Depot_Door_R", first[0], first[1], first[2]));
        var secondL = Assert.Single(bay.DoorPosesOf(barrier, "Gates_Depot_Door_L", second[0], second[1], second[2]));
        var secondR = Assert.Single(bay.DoorPosesOf(barrier, "Gates_Depot_Door_R", second[0], second[1], second[2]));
        Assert.Equal(new[] { 162.432f, -141.759f, 138.837f, -98.974f },
            new[] { firstL.OpenEuler[2], firstR.OpenEuler[2], secondL.OpenEuler[2], secondR.OpenEuler[2] });
        Assert.Equal(new[] { 3.8f, 0f, 0f, -2.042f },
            new[] { firstL.ClosedEuler[2], firstR.ClosedEuler[2], secondL.ClosedEuler[2], secondR.ClosedEuler[2] });
        Assert.All(new[] { firstL, firstR, secondL, secondR }, p =>
        {
            Assert.Equal((7f, ArenaDoorPose.EaseCurve, 3), (p.Seconds, p.Ease, p.CurveKeyCount));
            Assert.Equal(p.Part + "_Leaf", p.Node);                                  // the leaf is the node below the part
            Assert.Equal(p.ClosedPosition, p.OpenPosition);                          // a pure rotation about the hinge
            Assert.Equal(0.4f, p.Curve![4]);
            Assert.InRange(p.Curve[5], 0.79f, 0.81f);                                // 80 % of the swing after 40 % of the time
        });
        Assert.Equal(2, bay.DoorPosesOfBarrier(barrier, first[0], first[1], first[2]).Length);   // both leaves of gate 1, and only them
        Assert.Empty(bay.DoorPosesOf(barrier, "Gates_Depot_Door_L", 60f, -3.9f, 0f));        // between the gates
    }

    [Fact]
    public void ThePartFilterIsExactAndTheBarrierFilterListsEveryPart()
    {
        var bay = ArenaMapRegistry.Bay5;
        Assert.Empty(bay.DoorPosesOf("WAREHOUSE_DOORS (1)/One_man_Door", "Anything"));
        Assert.Empty(bay.DoorPosesOf("WAREHOUSE_DOORS (1)/one_man_door", ""));               // case sensitive
        Assert.Empty(bay.DoorPosesOf("", ""));
        const string roller = "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)";
        Assert.Single(bay.DoorPosesOfBarrier(roller));                                         // a barrier without At answers both forms,
        Assert.Single(bay.DoorPosesOfBarrier(roller, 9.822f, 0.13f, -27.705f));
        Assert.Single(bay.DoorPosesOfBarrier(roller, 1000f, 0f, 0f));                          // wherever the root is asked for
        var bowl = ArenaMapRegistry.Bowl;
        Assert.Equal(10, bowl.DoorPosesOfBarrier("DOORS_SPAWN_A").Length);                     // the BTR with its 8 wheels, and the respawn gate
        Assert.Equal(9, bowl.DoorPosesOfBarrier("DOORS_SPAWN_B").Length);                      // the ambulance's 7 nodes and the army gate's 2 leaves
        Assert.Equal(bowl.DoorPosesOf("DOORS_SPAWN_A", "BTR_82_closed"), bowl.DoorPosesOf("DOORS_SPAWN_A", "BTR_82_closed", 1f, 2f, 3f));
    }

    [Fact]
    public void OnlyBowlEquatorAndBay5CarryDoorPosesTodayAirPitsComeLater()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            if (ReferenceEquals(map, ArenaMapRegistry.Bowl) || ReferenceEquals(map, ArenaMapRegistry.Equator)
                || ReferenceEquals(map, ArenaMapRegistry.Bay5)) continue;
            Assert.Empty(map.DoorPoses);
        }
    }
}
