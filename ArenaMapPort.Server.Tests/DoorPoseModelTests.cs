#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The pure rules behind the parked gate parts (DoorPoseModel, docs/progress/door-poses.md "Runtime"): the clip fraction and
/// its ease, the exact-endpoint interpolation of positions and euler angles, which colliders Arena moved, when a parked
/// collider stands in the way, and whether a NavMesh probe segment runs through a parked body. The numbers in the geometry
/// tests are Bowl's, read offline from work/extract/bowl-native-v2 with the sidecar's poses applied (2026-10-22).
/// </summary>
public sealed class DoorPoseModelTests
{
    [Theory]
    [InlineData(0f, 7.683f, 0f)]
    [InlineData(3.8415f, 7.683f, 0.5f)]
    [InlineData(7.683f, 7.683f, 1f)]
    [InlineData(99f, 7.683f, 1f)]
    [InlineData(-1f, 7.683f, 0f)]
    [InlineData(0f, 0f, 1f)]     // a clip of no length is over at once
    [InlineData(2f, -3f, 1f)]
    [InlineData(float.NaN, 5f, 0f)]
    public void ProgressIsTheClampedFractionOfTheClip(float elapsed, float clip, float expected)
    {
        Assert.Equal(expected, DoorPoseModel.Progress(elapsed, clip), 4);
    }

    [Theory]
    [InlineData(0f, 7.683f, false)]
    [InlineData(7.682f, 7.683f, false)]
    [InlineData(7.683f, 7.683f, true)]
    [InlineData(0f, 0f, true)]
    [InlineData(0f, -1f, true)]
    public void ADriveIsOverWhenItHasRunItsClip(float elapsed, float clip, bool expected)
    {
        Assert.Equal(expected, DoorPoseModel.Finished(elapsed, clip));
    }

    [Fact]
    public void EaseIsExactAtTheEndsSymmetricAndMonotone()
    {
        Assert.Equal(0f, DoorPoseModel.Ease(0f));
        Assert.Equal(1f, DoorPoseModel.Ease(1f));
        Assert.Equal(0f, DoorPoseModel.Ease(-4f));
        Assert.Equal(1f, DoorPoseModel.Ease(4f));
        Assert.Equal(0f, DoorPoseModel.Ease(float.NaN));
        Assert.Equal(0.5f, DoorPoseModel.Ease(0.5f), 6);
        float previous = -1f;
        for (int i = 0; i <= 100; i++)
        {
            float k = i / 100f;
            float e = DoorPoseModel.Ease(k);
            Assert.True(e >= previous, $"monotone at {k}");
            Assert.Equal(1f, e + DoorPoseModel.Ease(1f - k), 5);
            previous = e;
        }
    }

    [Theory]
    [InlineData(10f, 20f, 0f, 10f)]
    [InlineData(10f, 20f, 1f, 20f)]
    [InlineData(10f, 20f, 0.5f, 15f)]
    [InlineData(10f, 20f, -3f, 10f)]
    [InlineData(10f, 20f, 7f, 20f)]
    [InlineData(10f, 20f, float.NaN, 10f)]
    public void LerpHasExactEndpoints(float a, float b, float k, float expected)
    {
        Assert.Equal(expected, DoorPoseModel.Lerp(a, b, k));
    }

    [Fact]
    public void LerpRejectsAnythingButThreeFloats()
    {
        Assert.Throws<ArgumentException>(() => DoorPoseModel.Lerp3(new[] { 1f, 2f }, new[] { 1f, 2f, 3f }, 0.5f, out _, out _, out _));
        Assert.Throws<ArgumentException>(() => DoorPoseModel.Lerp3(null, new[] { 1f, 2f, 3f }, 0.5f, out _, out _, out _));
    }

    [Fact]
    public void SnappingToTheOpenPoseIsTheDataBitForBitAndIdempotent()
    {
        foreach (var pose in ArenaMapRegistry.Bowl.DoorPoses)
        {
            for (int pass = 0; pass < 2; pass++) // the two raid-start passes
            {
                DoorPoseModel.Lerp3(pose.ClosedPosition, pose.OpenPosition, 1f, out float px, out float py, out float pz);
                DoorPoseModel.Lerp3(pose.ClosedEuler, pose.OpenEuler, 1f, out float ex, out float ey, out float ez);
                Assert.Equal(pose.OpenPosition, new[] { px, py, pz });
                Assert.Equal(pose.OpenEuler, new[] { ex, ey, ez });
            }
            DoorPoseModel.Lerp3(pose.ClosedPosition, pose.OpenPosition, 0f, out float cx, out float cy, out float cz);
            Assert.Equal(pose.ClosedPosition, new[] { cx, cy, cz }); // and the re-close of a hold is the closed data
        }
    }

    [Fact]
    public void TheBtrWheelsKeepTheirExtraTurnsAndTheBodyKeepsItsHeading()
    {
        var btr = ArenaMapRegistry.Bowl.DoorPosesOf("DOORS_SPAWN_A", "BTR_82_closed");
        var wheel = btr.Single(p => p.Node == "BTR_82_wheel_L_01");
        DoorPoseModel.Lerp3(wheel.ClosedEuler, wheel.OpenEuler, 0.5f, out float x, out float y, out float z);
        // component-wise like Unity's euler clips: 1.5 revolutions in total, not the 0.5 a quaternion slerp would take
        Assert.Equal(-276.6565f, x, 3);
        Assert.Equal(3f, y, 3);
        Assert.Equal(0f, z, 3);

        // the body's raw triple swings y and z against each other, but y + z stays at the heading (x = -90 is a gimbal lock)
        var body = btr.Single(p => p.Node == "");
        for (int i = 0; i <= 20; i++)
        {
            DoorPoseModel.Lerp3(body.ClosedEuler, body.OpenEuler, i / 20f, out _, out float by, out float bz);
            Assert.Equal(83.511f, by + bz, 2);
        }
    }

    [Theory]
    [InlineData("BTR_82_wheel_L_01/BTR_82_wheel_L_01_COLLIDER", new[] { "" }, true)]                          // the part itself moves everything
    [InlineData("", new[] { "" }, true)]
    [InlineData("", new[] { "Gates_Army/Gates_Army_door_01" }, false)]
    [InlineData("Gates_Army/Gates_Army_door_01", new[] { "Gates_Army/Gates_Army_door_01" }, true)]
    [InlineData("Gates_Army/Gates_Army_door_01/Gates_Army_door_01_COLLIDER", new[] { "Gates_Army/Gates_Army_door_01", "Gates_Army/Gates_Army_door_02" }, true)]
    [InlineData("Gates_Army/Gates_Army_COLLIDER", new[] { "Gates_Army/Gates_Army_door_01", "Gates_Army/Gates_Army_door_02" }, false)]
    [InlineData("Gates_Army/Gates_Army_door_011/Gates_Army_door_011_COLLIDER", new[] { "Gates_Army/Gates_Army_door_01" }, false)] // a shared prefix is not a child
    [InlineData("Gates_Army/Gates_Army_door_01", new[] { "Gates_Army/Gates_Army_door_01/Gates_Army_door_01_COLLIDER" }, false)] // the parent of a node is not moved by it
    [InlineData(null, new[] { "" }, false)]
    public void AColliderIsMovedWhenItSitsOnOrBelowAPoseNode(string collider, string[] nodes, bool expected)
    {
        Assert.Equal(expected, DoorPoseModel.IsMoved(collider, nodes));
    }

    [Fact]
    public void NoNodesMoveNothing()
    {
        Assert.False(DoorPoseModel.IsMoved("Gates_Army/Gates_Army_COLLIDER", new string[0]));
        Assert.False(DoorPoseModel.IsMoved("Gates_Army/Gates_Army_COLLIDER", null));
    }

    [Theory]
    [InlineData(2.54f, -0.07f, true)]    // the BTR's roof against the wheels' floor line
    [InlineData(0.68f, 0.01f, true)]     // an ambulance wheel
    [InlineData(0.31f, 0.0f, true)]
    [InlineData(0.30f, 0.0f, false)]     // a curb is not in the way
    [InlineData(-0.20f, 0.0f, false)]    // a body under the ground never carves the doorway shut
    public void AParkedColliderStandsInTheWayOnlyAboveTheFloorLine(float maxY, float floorY, bool expected)
    {
        Assert.Equal(expected, DoorPoseModel.ParkedBodyStands(maxY, floorY));
    }

    [Fact]
    public void TheCarverOfTheParkedBtrBodyIsItsBoundsPlusTheMargins()
    {
        // BTR_82_exterior_closed_COLLIDER at the open pose, world AABB (offline, 2026-10-22)
        SpawnGateModel.CarverBox(41.39f, 0.41f, -24.03f, 49.95f, 2.54f, -20.17f,
            out float cx, out float cy, out float cz, out float sx, out float sy, out float sz);
        Assert.Equal(8.56f + 0.4f, sx, 3);
        Assert.Equal(3.86f + 0.4f, sz, 3);
        Assert.Equal(2.13f + 0.5f, sy, 3);
        Assert.Equal(45.67f, cx, 3);
        Assert.Equal(-22.10f, cz, 3);
        Assert.Equal(0.41f + (2.13f + 0.5f) * 0.5f, cy, 3);
        // the nose reaches x 50.15, 0.65 m into the port's mesh (which ends at 49.5): that is what the carver is for
        Assert.True(cx + sx * 0.5f > 50.1f);
    }

    [Theory]
    // a segment along z at x 51.23: the BTR's closed probe (centre of the closed bounds), against the parked body's box
    [InlineData(51.23f, -22.97f, 51.23f, -19.97f, 41.19f, -24.23f, 50.15f, -19.97f, false)]
    // the respawn gate's closed probe (x 39.79, z -3.69 .. -0.69) against the box of the leaf slid onto its rail
    [InlineData(39.79f, -3.69f, 39.79f, -0.69f, 38.67f, -3.01f, 43.96f, 0.60f, true)]
    // the army gate: the closed probe (x -51.68, z -56.19 .. -53.19) against the box of a swung leaf: clear ...
    [InlineData(-51.68f, -56.19f, -51.68f, -53.19f, -49.75f, -55.20f, -47.41f, -53.69f, false)]
    [InlineData(-51.68f, -56.19f, -51.68f, -53.19f, -55.97f, -54.55f, -53.61f, -53.09f, false)]
    // ... and against the box of its FRAME collider, which spans the doorway: why the frame is never carved when parked
    [InlineData(-51.68f, -56.19f, -51.68f, -53.19f, -54.19f, -55.26f, -49.17f, -54.05f, true)]
    // generic cases
    [InlineData(0f, -5f, 0f, 5f, -1f, -1f, 1f, 1f, true)]        // through
    [InlineData(-5f, 0f, 5f, 0f, -1f, 2f, 1f, 3f, false)]         // beside
    [InlineData(0f, 0f, 0.5f, 0.5f, -1f, -1f, 1f, 1f, true)]      // inside
    [InlineData(5f, 5f, 5f, 5f, -1f, -1f, 1f, 1f, false)]         // a point outside
    [InlineData(1f, -5f, 1f, 5f, -1f, -1f, 1f, 1f, true)]         // along an edge
    [InlineData(-5f, 1.5f, 5f, 1.5f, -1f, -1f, 1f, 1f, false)]    // parallel, outside
    [InlineData(-3f, -3f, 3f, 3f, 1.5f, -1f, 2.5f, 0f, false)]    // a diagonal that misses the box
    public void AProbeSegmentTouchesAParkCarverOnlyWhereItRunsThroughIt(
        float ax, float az, float bx, float bz, float minX, float minZ, float maxX, float maxZ, bool expected)
    {
        Assert.Equal(expected, DoorPoseModel.SegmentTouchesBox(ax, az, bx, bz, minX, minZ, maxX, maxZ));
        Assert.Equal(expected, DoorPoseModel.SegmentTouchesBox(bx, bz, ax, az, minX, minZ, maxX, maxZ)); // either direction
    }

    [Fact]
    public void ABoxWithInvertedExtentsIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DoorPoseModel.SegmentTouchesBox(0f, 0f, 1f, 1f, 2f, 0f, 1f, 1f));
    }

    [Theory]
    [InlineData(0.0f, 0f, true)]
    [InlineData(0.03f, 1f, true)]
    [InlineData(0.05f, 2f, true)]
    [InlineData(0.06f, 0f, false)]
    [InlineData(0f, 2.5f, false)]
    [InlineData(float.NaN, 0f, false)]
    [InlineData(0f, float.NaN, false)]
    public void TheDataAppliesOnlyToANodeNearItsClosedPose(float metres, float degrees, bool expected)
    {
        Assert.Equal(expected, DoorPoseModel.PoseMatchesScene(metres, degrees));
    }
}

/// <summary>
/// The pose data of Bowl read through the model: what the client would do with the real descriptor entries.
/// </summary>
public sealed class DoorPoseDataTests
{
    static readonly (string barrier, string part, float seconds)[] Parts =
    {
        ("DOORS_SPAWN_A", "BTR_82_closed", 7.683f),
        ("DOORS_SPAWN_A", "Bowl_Gates_Respawn_01", 13.5f),
        ("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", 8.3f),
        ("DOORS_SPAWN_B", "Gates_Army_01", 10f),
    };

    [Fact]
    public void FourPartsDriveAndEveryNodeOfAPartSharesItsClipAndLength()
    {
        var map = ArenaMapRegistry.Bowl;
        Assert.Equal(Parts.Length, map.DoorPoses.Select(p => (p.Barrier, p.Part)).Distinct().Count());
        foreach (var (barrier, part, seconds) in Parts)
        {
            var poses = map.DoorPosesOf(barrier, part);
            Assert.NotEmpty(poses);
            Assert.Single(poses.Select(p => p.Clip).Distinct());
            Assert.All(poses, p => Assert.Equal(seconds, p.Seconds, 3));
        }
        // the longest clip is the respawn gate's: a release is complete 13.5 s after it began
        Assert.Equal(13.5f, map.DoorPoses.Max(p => p.Seconds), 3);
    }

    [Fact]
    public void EveryPoseIsThreeFloatsAndTheEaseDrivesItBetweenTheEndsWithoutLeavingThem()
    {
        foreach (var pose in ArenaMapRegistry.Bowl.DoorPoses)
        {
            Assert.Equal(3, pose.ClosedPosition.Length);
            Assert.Equal(3, pose.OpenPosition.Length);
            Assert.Equal(3, pose.ClosedEuler.Length);
            Assert.Equal(3, pose.OpenEuler.Length);
            for (int i = 0; i <= 50; i++)
            {
                float k = DoorPoseModel.Ease(DoorPoseModel.Progress(i * pose.Seconds / 50f, pose.Seconds));
                DoorPoseModel.Lerp3(pose.ClosedPosition, pose.OpenPosition, k, out float px, out float py, out float pz);
                Assert.InRange(px, Math.Min(pose.ClosedPosition[0], pose.OpenPosition[0]), Math.Max(pose.ClosedPosition[0], pose.OpenPosition[0]));
                Assert.InRange(py, Math.Min(pose.ClosedPosition[1], pose.OpenPosition[1]), Math.Max(pose.ClosedPosition[1], pose.OpenPosition[1]));
                Assert.InRange(pz, Math.Min(pose.ClosedPosition[2], pose.OpenPosition[2]), Math.Max(pose.ClosedPosition[2], pose.OpenPosition[2]));
            }
        }
    }

    [Theory]
    // the army gate: the two swinging leaves are moved, the frame posts and the Telfer pillars stay where they stood
    [InlineData("DOORS_SPAWN_B", "Gates_Army_01", "Gates_Army/Gates_Army_door_01/Gates_Army_door_01_COLLIDER", true)]
    [InlineData("DOORS_SPAWN_B", "Gates_Army_01", "Gates_Army/Gates_Army_door_02/Gates_Army_door_02_COLLIDER", true)]
    [InlineData("DOORS_SPAWN_B", "Gates_Army_01", "Gates_Army/Gates_Army_COLLIDER", false)]
    [InlineData("DOORS_SPAWN_B", "Gates_Army_01", "Telfer_Help/Telfer_Help_Collider", false)]
    [InlineData("DOORS_SPAWN_B", "Gates_Army_01", "Telfer_Help (1)/Telfer_Help_Collider", false)]
    // the respawn gate: the leaf slides along its rail, the rail stays
    [InlineData("DOORS_SPAWN_A", "Bowl_Gates_Respawn_01", "Bowl_Gates_Respawn_01_Gate/Bowl_Gates_Respawn_01_Gate_COLLIDER", true)]
    [InlineData("DOORS_SPAWN_A", "Bowl_Gates_Respawn_01", "Bowl_Gates_Respawn_01_RAIL/Bowl_Gates_Respawn_01_RAIL_COLLIDER", false)]
    // the BTR drives as a whole, body and wheels
    [InlineData("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_exterior_closed_COLLIDER", true)]
    [InlineData("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_R_03/BTR_82_wheel_R_03_COLLIDER", true)]
    [InlineData("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_turret/BTR_82_turret_COLLIDER", true)]
    // the ambulance likewise, from its root node down
    [InlineData("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Body/Bowl_Respawn_Ambulance_Body_COLLIDER", true)]
    [InlineData("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Wheel_L/Bowl_Respawn_Ambulance_Wheel_L_COLLIDER", true)]
    public void OnlyWhatArenaMovesGetsAParkCarver(string barrier, string part, string colliderPath, bool moved)
    {
        var nodes = ArenaMapRegistry.Bowl.DoorPosesOf(barrier, part).Select(p => p.Node).ToArray();
        Assert.Equal(moved, DoorPoseModel.IsMoved(colliderPath, nodes));
    }

    [Fact]
    public void NoOtherMapDrivesAnything()
    {
        foreach (var map in ArenaMapRegistry.All.Where(m => m.Key != ArenaMapRegistry.Bowl.Key))
        {
            Assert.Empty(map.DoorPoses);
            Assert.Empty(map.DoorPosesOf("BUS_DOORS", "Apron_bus_MAZ_doors"));
        }
    }
}

/// <summary>
/// The client files sit on Unity types; their wiring is read as text like the other runtime files (SpawnGatesWiringTests).
/// </summary>
public sealed class DoorPoseWiringTests
{
    static string Text(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }

    static string Between(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, "missing: " + from);
        int b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        Assert.True(b > a, "missing: " + to);
        return text.Substring(a, b - a);
    }

    [Fact]
    public void ThePoseDataIsResolvedBeforeAnOfferAndAPartIsParkedBeforeItIsSwitchedOff()
    {
        var code = Text("ArenaMapPort.Client/BoothDoorRepairs.cs");
        int note = code.IndexOf("SpawnGates.NotePoses(part, map.DoorPosesOf(path, part.name));", StringComparison.Ordinal);
        int held = code.IndexOf("SpawnGates.OfferHeld(part)", StringComparison.Ordinal);
        int start = code.IndexOf("SpawnGates.Offer(part)", StringComparison.Ordinal);
        int park = code.IndexOf("SpawnGates.Park(part)", StringComparison.Ordinal);
        int off = code.IndexOf("part.gameObject.SetActive(false);", park, StringComparison.Ordinal);
        Assert.True(note > 0 && note < held && held < start && start < park && park < off, "NotePoses, OfferHeld, Offer, Park, SetActive(false)");
        Assert.True(off - start < 400, "the existing offer-before-switch-off distance still holds");
        Assert.Contains("parked > 0 ? $\", parked {parked} part(s) at the open pose\"", code);
        // the park carvers wait for the NavMesh: one seal per opening pass, like the held gates
        Assert.True(code.IndexOf("SpawnGates.SealParked(", code.IndexOf("OpenBarriers(openings, sb, barriers);", StringComparison.Ordinal), StringComparison.Ordinal) > 0);
    }

    [Fact]
    public void TheDriveKeyIsANewBindOnSettingsAndTheOldKeysKeepTheirDefaults()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        Assert.Contains("_drive = s.Bind(\"Spawns\", \"SpawnGateDrive\", true,", gates);
        Assert.Contains("_carve = s.Bind(\"Spawns\", \"SpawnGateCarve\", true,", gates);
        Assert.Contains("_slideSeconds = s.Bind(\"Spawns\", \"SpawnGateSlideSeconds\", 1.0f", gates);
        Assert.DoesNotContain("Plugin.Instance.Config", gates);
    }

    [Fact]
    public void AReleaseNeverSwitchesOffAPartThatHasPoses()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var open = Between(gates, "static IEnumerator Open(string reason)", "static void OnSceneUnloaded(Scene scene)");
        Assert.Single(Regex.Matches(open, @"SetActive\(false\)"));
        Assert.Contains("part.gameObject.activeSelf && !_posed.ContainsKey(part)", open);
        Assert.Contains("ApplyPose(posed, DoorPoseModel.Ease(DoorPoseModel.Progress(elapsed, posed.Seconds)));", open);
        Assert.Contains("if (!drive || DoorPoseModel.Finished(elapsed, posed.Seconds))", open);
        // a part finishing its drive loses its own hold carvers before its park carvers are made, through the guarded
        // SealParked (a carver failure must not leave the coroutine and _opening stuck)
        int destroy = open.IndexOf("DestroyHoldCarversOf(posed.Part)", StringComparison.Ordinal);
        int make = open.IndexOf("SealParked(\"released\")", StringComparison.Ordinal);
        Assert.DoesNotContain("CreateParkCarvers(\"released\")", open);
        Assert.True(destroy > 0 && make > destroy);
        Assert.Contains("drove", open);
        Assert.Contains("to the open pose in", open);
    }

    [Fact]
    public void AHoldReclosesEveryParkedPartAndTakesItsParkCarversAway()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        // the class doc quotes both signatures inside <c>...</c>; a declaration ends its line
        int from = Regex.Match(gates, @"public static bool Hold\(string reason\)\s*\r?\n").Index;
        int to = Regex.Match(gates, @"public static void Release\(string reason\)\s*\r?\n").Index;
        Assert.True(from > 0 && to > from);
        var hold = gates.Substring(from, to - from);
        Assert.Contains("Reclose(posed);", hold);
        Assert.Contains("posed.Parked || posed.Driving", hold);
        var reclose = Between(gates, "static void Reclose(PosedPart posed)", "static void DestroyParkCarvers(PosedPart posed)");
        Assert.Contains("DestroyParkCarvers(posed);", reclose);
        Assert.Contains("posed.Parked = false;", reclose);
        Assert.Contains("posed.Driving = false;", reclose);
        Assert.Contains("posed.Carved = false;", reclose);
        Assert.Contains("ApplyPose(posed, 0f);", reclose);
        // a parked part is not a closed part: the hold carvers skip it, the park carvers come with the NavMesh install
        Assert.Contains("if (IsParkedOrDriving(part)) continue;", gates);
        Assert.Contains("SealParked(\"navmesh installed\");", gates);
    }

    [Fact]
    public void ParkCarversCoverOnlyMovedCollidersThatStandInTheWay()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var carve = Between(gates, "static void CreateParkCarvers(string why)", "static GameObject MakeCarver(");
        Assert.Contains("IsMovementCollider(collider)", carve);
        Assert.Contains("DoorPoseModel.IsMoved(RelativePath(posed.Part, collider.transform), posed.NodePaths)", carve);
        Assert.Contains("DoorPoseModel.ParkedBodyStands(b.max.y, posed.FloorY)", carve);
        Assert.Contains("if (!_navMeshReady || !_carve.Value) return;", carve);
        Assert.Contains("EnsureBaseline();", carve);
        // the layers are the hold carvers' own: Default and LowPolyCollider
        var movement = Between(gates, "static bool IsMovementCollider(Collider collider)", "// The path of a transform below a root");
        Assert.Contains("layer == LayerDefault || layer == LayerLowPoly", movement);
        Assert.Contains("!collider.isTrigger && collider.enabled && collider.gameObject.activeInHierarchy", movement);
    }

    [Fact]
    public void ThePostReleaseProbeIsNotJudgedWhereAParkedBodyStands()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        Assert.Contains("if (!expectSealed && ParkedBodyOnProbe(bounds))", gates);
        Assert.Contains("DoorPoseModel.SegmentTouchesBox(a.x, a.z, b.x, b.z, box.min.x, box.min.z, box.max.x, box.max.z)", gates);
        // one segment definition for the path probe and the occupancy test
        Assert.Equal(2, Regex.Matches(gates, @"ProbeSegment\(bounds, out var a, out var b\);").Count);
    }

    [Fact]
    public void PoseDataThatDoesNotFitFallsBackToTheSwitchOffWithOneWarning()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var note = Between(gates, "internal static void NotePoses(Transform part, ArenaDoorPose[] poses)", "internal static bool Park(Transform part)");
        Assert.Contains("DoorPoseModel.PoseMatchesScene(positionError, angleError)", note);
        Assert.Contains("node '{path}' not found below it", note);
        Assert.Contains("catch (Exception e)", note);
        Assert.Contains("_poseFallback.Contains(part)", note);
        var fallback = Between(gates, "static void PoseFallback(Transform part, string why)", "static bool IsTriple");
        Assert.Contains("if (!_poseFallback.Add(part)) return;", fallback);
        Assert.Contains("Plugin.Log.LogWarning", fallback);
        // Park never lets an exception into EFT's load chain and gives the part back to the switch-off
        var park = Between(gates, "internal static bool Park(Transform part)", "internal static void SealParked(string why)");
        Assert.Contains("catch (Exception e)", park);
        Assert.Contains("_posed.Remove(part);", park);
        Assert.Contains("return false;", park);
    }

    [Fact]
    public void ColliderBoundsAreReadAfterTheTransformsAreSynced()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        // Unity's physics scene follows a moved transform only at the next step unless it is synced
        Assert.Equal(4, Regex.Matches(gates, @"Physics\.SyncTransforms\(\);").Count); // measure, park, park carvers, hold carvers
        var park = Between(gates, "internal static bool Park(Transform part)", "internal static void SealParked(string why)");
        Assert.True(park.IndexOf("ApplyPose(posed, 1f);", StringComparison.Ordinal) < park.IndexOf("Physics.SyncTransforms();", StringComparison.Ordinal));
        var apply = Between(gates, "static void ApplyPose(PosedPart posed, float k)", "// Hold: back to the closed pose");
        Assert.Contains("n.Node.localPosition = new Vector3(px, py, pz);", apply);
        Assert.Contains("n.Node.localEulerAngles = new Vector3(ex, ey, ez);", apply);
    }

    [Fact]
    public void TheDriveIsTimedAndPosesStaticCollidersEveryFrameWithoutSearchesOrRigidbodies()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var open = Between(gates, "static IEnumerator Open(string reason)", "static void OnSceneUnloaded(Scene scene)");
        // the host check of the cost model (door-poses.md "Cost of the drive"): the whole frame while a part is still posed
        Assert.Contains("if (elapsed > 0f && drivers.Count > 0)", open);       // a snap finishes on the first pass (elapsed 0): no line
        Assert.Contains("Time.unscaledDeltaTime", open);
        Assert.Contains("drive frames: {driveFrames} frame(s) in {driveEnd:F1} s, mean ", open);
        Assert.Contains("Physics.autoSyncTransforms", open);
        Assert.Contains("Plugin.Log.LogDebug($\"{Tag} drive frames:", open);   // Debug: Info is reserved for startup and player information
        // no kinematic body: in the scratch Unity 2022.3.43 model it measured +0.1 to +0.2 ms MORE than the static colliders it was
        // meant to relieve (+0.2 ms for the static ones against no movement); the colliders stay static, the transforms are re-posed
        Assert.DoesNotContain("AddComponent<Rigidbody>", gates);
        Assert.DoesNotContain("isKinematic", gates);
        // the per-frame path searches nothing: node references were resolved once, in NotePoses (a scene-wide search in periodic
        // code cost a 30 ms hitch once, AGENTS.md traps)
        foreach (var path in new[] { open, Between(gates, "static void ApplyPose(PosedPart posed, float k)", "// Hold: back to the closed pose") })
        {
            Assert.DoesNotContain("FindObjectsOfType", path);
            Assert.DoesNotContain("GetComponent", path);
            Assert.DoesNotContain(".Find(", path);
            Assert.DoesNotContain("new GameObject", path);
        }
    }

    [Fact]
    public void EveryPoseStateResetsOnTheScriptsSceneUnload()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var unload = Between(gates, "static void OnSceneUnloaded(Scene scene)", "\n        }");
        foreach (var reset in new[] { "DestroyParkCarvers(posed);", "_posed.Clear();", "_poseFallback.Clear();", "DestroyCarvers();" })
            Assert.Contains(reset, unload);
        var destroy = Between(gates, "static void DestroyCarvers()", "static int DestroyHoldCarversOf(Transform part)");
        Assert.Contains("_carversOf.Clear();", destroy);
    }

    [Fact]
    public void TheModeContractIsUnchanged()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        Assert.Single(Regex.Matches(gates, @"public static bool Hold\(string reason\)\s*\r?\n"));
        Assert.Single(Regex.Matches(gates, @"public static void Release\(string reason\)\s*\r?\n"));
        Assert.DoesNotContain("public static bool Park", gates);
        Assert.DoesNotContain("public static void NotePoses", gates);
        Assert.DoesNotContain("public static void SealParked", gates);
    }
}
