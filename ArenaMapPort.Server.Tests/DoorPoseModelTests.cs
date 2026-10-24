#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    // ---- 2026-10-23: the eases of the DotAnimator poses, the walking band, the timing scale, the instance resolver ----

    // Bay5's depot gate 821, left leaf: three keys (time, value, inSlope, outSlope) on a 0..1 timeline
    static readonly float[] DepotCurve = { 0f, 0f, 2f, 2f, 0.4f, 0.79929f, 0.89601f, 0.89601f, 1f, 1f, 0f, 0f };

    [Fact]
    public void InOutQuintIsDoTweensCurveExactAtItsEndsAndItsMiddle()
    {
        Assert.Equal(0f, DoorPoseModel.InOutQuint(0f));
        Assert.Equal(1f, DoorPoseModel.InOutQuint(1f));
        Assert.Equal(0.5f, DoorPoseModel.InOutQuint(0.5f), 6);
        Assert.Equal(0.015625f, DoorPoseModel.InOutQuint(0.25f), 6);   // 0.5 * (2 * 0.25)^5
        Assert.Equal(0.984375f, DoorPoseModel.InOutQuint(0.75f), 6);   // 0.5 * ((2 * 0.75 - 2)^5 + 2)
        Assert.Equal(0f, DoorPoseModel.InOutQuint(-1f));
        Assert.Equal(1f, DoorPoseModel.InOutQuint(9f));
        Assert.Equal(0f, DoorPoseModel.InOutQuint(float.NaN));
        float previous = -1f;
        for (int i = 0; i <= 200; i++)
        {
            float k = i / 200f;
            float e = DoorPoseModel.InOutQuint(k);
            Assert.True(e >= previous, $"monotone at {k}");
            Assert.Equal(1f, e + DoorPoseModel.InOutQuint(1f - k), 5); // point-symmetric about (0.5, 0.5)
            previous = e;
        }
        // slow start, slow stop: a tenth of the time moves a hair, the middle fifth moves most of the way
        Assert.True(DoorPoseModel.InOutQuint(0.1f) < 0.001f);
        Assert.True(DoorPoseModel.InOutQuint(0.6f) - DoorPoseModel.InOutQuint(0.4f) > 0.4f);
    }

    [Fact]
    public void TheDepotCurveHitsItsKeysAndStaysInsideZeroToOne()
    {
        Assert.Equal(0f, DoorPoseModel.Hermite(DepotCurve, 0f));
        Assert.Equal(0.79929f, DoorPoseModel.Hermite(DepotCurve, 0.4f), 6);
        Assert.Equal(1f, DoorPoseModel.Hermite(DepotCurve, 1f));
        Assert.Equal(0f, DoorPoseModel.Hermite(DepotCurve, -0.5f));      // before the first key: its value
        Assert.Equal(1f, DoorPoseModel.Hermite(DepotCurve, 3f));         // after the last key: its value
        Assert.Equal(0f, DoorPoseModel.Hermite(DepotCurve, float.NaN));
        // 80 % of the swing in the first 40 % of the time (2.8 of 7.0 s)
        Assert.InRange(DoorPoseModel.Hermite(DepotCurve, 0.4f), 0.79f, 0.81f);
        // mid-segment values from the Hermite basis by hand: h10 dt m0 + h01 v1 + h11 dt m1 (+ h00 v0)
        Assert.Equal(0.4548445f, DoorPoseModel.Hermite(DepotCurve, 0.2f), 4);   // 0.1 + 0.399645 - 0.0448005
        Assert.Equal(0.96684575f, DoorPoseModel.Hermite(DepotCurve, 0.7f), 4);  // 0.399645 + 0.06720075 + 0.5
        float previous = -1f;
        for (int i = 0; i <= 500; i++)
        {
            float v = DoorPoseModel.Hermite(DepotCurve, i / 500f);
            Assert.InRange(v, 0f, 1f);
            Assert.True(v >= previous - 1e-6f, $"monotone at {i / 500f}");
            previous = v;
        }
    }

    [Fact]
    public void AHermiteCurveWantsTwoKeysOfFourFloats()
    {
        Assert.Throws<ArgumentException>(() => DoorPoseModel.Hermite(null, 0.5f));
        Assert.Throws<ArgumentException>(() => DoorPoseModel.Hermite(new[] { 0f, 0f, 1f, 1f }, 0.5f));
        Assert.Throws<ArgumentException>(() => DoorPoseModel.Hermite(new[] { 0f, 0f, 1f, 1f, 1f, 1f, 1f }, 0.5f));
        // two keys with slope 1 are the identity
        Assert.Equal(0.3f, DoorPoseModel.Hermite(new[] { 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f }, 0.3f), 5);
    }

    [Theory]
    [InlineData("smoothstep", 0.5f, 0.5f)]
    [InlineData("smoothstep", 0.25f, 0.15625f)]
    [InlineData("linear", 0.3f, 0.3f)]
    [InlineData("inOutQuint", 0.25f, 0.015625f)]
    [InlineData("curve", 0.4f, 0.79929f)]
    public void EaseOfDispatchesOnTheNamedEase(string ease, float k, float expected)
    {
        Assert.Equal(expected, DoorPoseModel.EaseOf(ease, DepotCurve, k), 5);
    }

    [Theory]
    [InlineData("smoothstep")]
    [InlineData("linear")]
    [InlineData("inOutQuint")]
    [InlineData("curve")]
    public void EveryEaseIsExactAtBothEndsWhateverIsAskedOfIt(string ease)
    {
        foreach (float below in new[] { 0f, -1f, float.NegativeInfinity, float.NaN })
            Assert.Equal(0f, DoorPoseModel.EaseOf(ease, DepotCurve, below));
        foreach (float above in new[] { 1f, 2f, float.PositiveInfinity })
            Assert.Equal(1f, DoorPoseModel.EaseOf(ease, DepotCurve, above));
    }

    [Fact]
    public void AnUnknownEaseOrAMissingCurveIsRefusedNotGuessed()
    {
        Assert.Throws<ArgumentException>(() => DoorPoseModel.EaseOf("bounce", null, 0.5f));
        Assert.Throws<ArgumentException>(() => DoorPoseModel.EaseOf("curve", null, 0.5f));
        Assert.Throws<ArgumentException>(() => DoorPoseModel.EaseOf(null, null, 0.5f));
        Assert.Equal(0f, DoorPoseModel.EaseOf("bounce", null, 0f)); // the ends never reach the switch
    }

    [Theory]
    [InlineData("smoothstep", true)]
    [InlineData("linear", true)]
    [InlineData("inOutQuint", true)]
    [InlineData("InOutQuint", false)]
    [InlineData("bounce", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheFourNamedEasesAreUsableWithoutACurve(string ease, bool usable)
    {
        Assert.Equal(usable, DoorPoseModel.EaseIsUsable(ease, null, out string why));
        Assert.Equal(usable, why == null);
    }

    [Fact]
    public void ACurveEaseNeedsWellFormedKeys()
    {
        Assert.True(DoorPoseModel.EaseIsUsable("curve", DepotCurve, out string ok));
        Assert.Null(ok);
        Assert.False(DoorPoseModel.EaseIsUsable("curve", null, out string missing));
        Assert.NotNull(missing);
        Assert.False(DoorPoseModel.EaseIsUsable("curve", new[] { 0f, 0f, 1f, 1f }, out _));                       // one key
        Assert.False(DoorPoseModel.EaseIsUsable("curve", new[] { 0f, 0f, 1f, 1f, 1f, 1f, 1f }, out _));          // not a multiple of four
        Assert.False(DoorPoseModel.EaseIsUsable("curve", new[] { 0f, 0f, 1f, 1f, 1f, float.NaN, 1f, 1f }, out _));
        Assert.False(DoorPoseModel.EaseIsUsable("curve", new[] { 0f, 0f, 1f, 1f, 1f, float.PositiveInfinity, 1f, 1f }, out _));
        Assert.False(DoorPoseModel.EaseIsUsable("curve", new[] { 0.5f, 0f, 1f, 1f, 0.5f, 1f, 1f, 1f }, out _));  // equal times
        Assert.False(DoorPoseModel.EaseIsUsable("curve", new[] { 0.6f, 0f, 1f, 1f, 0.2f, 1f, 1f, 1f }, out _));  // descending times
    }

    [Theory]
    [InlineData(5f, 0.5f, 2.5f)]       // Equator's booth leaf at the default scale
    [InlineData(7f, 0.5f, 3.5f)]       // the slow leaf, and Bay5's depot gates
    [InlineData(4f, 0.5f, 2f)]         // Bay5's rollers
    [InlineData(13.5f, 0.5f, 6.75f)]   // Bowl's respawn gate, the longest drive: halved as well
    [InlineData(7.683f, 1f, 7.683f)]   // 1 = Arena's own timing
    [InlineData(10f, 0.1f, 1f)]
    [InlineData(10f, 0.05f, 1f)]       // clamped to the range of the key
    [InlineData(10f, 3f, 10f)]
    [InlineData(10f, float.NaN, 5f)]   // a broken value is the default
    [InlineData(0f, 0.5f, 0f)]         // a pose without a duration snaps
    [InlineData(-2f, 0.5f, 0f)]
    public void ADriveTakesArenasDurationTimesTheClampedTimingScale(float authored, float scale, float expected)
    {
        Assert.Equal(expected, DoorPoseModel.ScaledSeconds(authored, scale), 4);
    }

    [Fact]
    public void TheTimingKeyDefaultsToHalfAndRangesFromATenthToArenasOwn()
    {
        Assert.Equal(0.5f, DoorPoseModel.DefaultDriveScale);
        Assert.Equal(0.1f, DoorPoseModel.MinDriveScale);
        Assert.Equal(1.0f, DoorPoseModel.MaxDriveScale);
    }

    [Theory]
    // Bay5 roller (4) at its open pose: the slab spans world y 0.21 .. 3.68, 3.09 .. 6.56 m above its floor line (-2.88): lifted into the lintel
    [InlineData(0.21f, 3.68f, -2.88f, false)]
    // One_man_Door's two slab halves (3.17 .. 4.75 and 4.72 .. 6.65 m above the floor line)
    [InlineData(0.28f, 1.86f, -2.89f, false)]
    [InlineData(1.83f, 3.76f, -2.89f, false)]
    // a swung depot leaf stands on the floor line and is 4.2 m high
    [InlineData(-3.88f, 0.34f, -3.88f, true)]
    // the wicket door that moves with it: 0.08 .. 2.55 m above the floor line
    [InlineData(-3.80f, -1.33f, -3.88f, true)]
    // an Equator booth leaf slid into its pocket: the floor line to 2.05 m
    [InlineData(0.05f, 2.10f, 0.05f, true)]
    // Bowl: the BTR's body (world 0.41 .. 2.54, floor line -0.07) occupies the band, its turret (2.09 .. 3.02 m above the floor line) does not
    [InlineData(0.41f, 2.54f, -0.07f, true)]
    [InlineData(2.02f, 2.95f, -0.07f, false)]
    // the edges: the bottom must be BELOW floor + 1.8, the top ABOVE floor + 0.3
    [InlineData(1.79f, 3f, 0f, true)]
    [InlineData(1.80f, 3f, 0f, false)]
    [InlineData(-1f, 0.31f, 0f, true)]
    [InlineData(-1f, 0.30f, 0f, false)]    // a curb is not in the way
    [InlineData(-3f, -0.2f, 0f, false)]    // a body under the ground never carves the doorway shut
    public void AParkedColliderIsCarvedOnlyWhereSomeoneCanWalkIntoIt(float minY, float maxY, float floorY, bool expected)
    {
        Assert.Equal(expected, DoorPoseModel.OccupiesWalkingBand(minY, maxY, floorY));
    }

    [Fact]
    public void TheBandIsABandOfAPlayersHeightAboveTheFloorLine()
    {
        Assert.Equal(1.8f, DoorPoseModel.WalkingBandHeight);
        Assert.True(DoorPoseModel.WalkingBandHeight > DoorPoseModel.ParkedBodyMinHeight);
    }

    [Fact]
    public void ARotationMovesOnlyWhenTheTwoTriplesDiffer()
    {
        Assert.False(DoorPoseModel.RotationMoves(new[] { -90f, -90f, 0f }, new[] { -90f, -90f, 0f }));   // Bay5's roller: lifted, not turned
        Assert.False(DoorPoseModel.RotationMoves(new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 0.00005f }));
        Assert.True(DoorPoseModel.RotationMoves(new[] { 0f, 0f, 3.8f }, new[] { 0f, 0f, 162.432f }));    // a depot leaf
        Assert.True(DoorPoseModel.RotationMoves(new[] { 0f, 0f, 0f }, new[] { 0f, 0.01f, 0f }));
        Assert.Throws<ArgumentException>(() => DoorPoseModel.RotationMoves(new[] { 0f, 0f }, new[] { 0f, 0f, 0f }));
        Assert.Throws<ArgumentException>(() => DoorPoseModel.RotationMoves(null, new[] { 0f, 0f, 0f }));
    }

    [Theory]
    [InlineData("clip", false)]
    [InlineData("dotanimator", true)]
    [InlineData("borrowed", true)]
    public void OnlyAMecanimClipKeepsTheComponentWiseEulerLerp(string source, bool slerp)
    {
        Assert.Equal(slerp, DoorPoseModel.UsesSlerp(source));
    }

    // ---- AirPit (2026-10-24, docs/progress/door-poses.md "Runtime 3"): repeated part names, switch rows -------------------

    static ArenaDoorPose Row(string part, string node, float x, float y, float z, float openY = 90f) =>
        new ArenaDoorPose("BARRIER", part, node, "DotAnimator", 1.7f,
            new[] { x, y, z }, new[] { -90f, 0f, 0f }, new[] { x, y, z }, new[] { -90f, openY, 0f }, "dotanimator", "inOutQuint");

    [Fact]
    public void ASingleRowIsTakenAsItIsWhateverItsPosition()
    {
        var only = Row("Leaf", "", 1f, 2f, 3f);
        var picked = DoorPoseModel.SelectRowsForNode(new[] { only }, 50f, 50f, 50f);
        Assert.Same(only, Assert.Single(picked));     // a name that does not repeat: NotePoses' pose match reports a node that stands elsewhere
    }

    [Fact]
    public void SeveralRowsAreToldApartByTheClosedPositionWithinFiveCentimetres()
    {
        var a = Row("Leaf", "", -0.481f, -3.23f, 3.099f);
        var b = Row("Leaf", "", 4.483f, -3.23f, 3.099f);
        var c = Row("Leaf", "", 9.446f, -3.23f, 3.099f);
        var rows = new[] { a, b, c };
        Assert.Same(a, Assert.Single(DoorPoseModel.SelectRowsForNode(rows, -0.481f, -3.23f, 3.099f)));
        Assert.Same(b, Assert.Single(DoorPoseModel.SelectRowsForNode(rows, 4.483f, -3.2300f, 3.099f)));
        Assert.Same(c, Assert.Single(DoorPoseModel.SelectRowsForNode(rows, 9.446f, -3.2296f, 3.0987f)));   // the scene's own float noise (AirPit: y -3.2296 / -3.2300)
        // the tolerance is ClosedPositionTolerance (0.05 m) as a distance, not per axis
        Assert.Equal(0.05f, ArenaDoorPose.ClosedPositionTolerance);
        Assert.Same(b, Assert.Single(DoorPoseModel.SelectRowsForNode(rows, 4.483f + 0.04f, -3.23f, 3.099f)));
        Assert.Empty(DoorPoseModel.SelectRowsForNode(rows, 4.483f + 0.06f, -3.23f, 3.099f));
        Assert.Empty(DoorPoseModel.SelectRowsForNode(rows, 4.483f + 0.04f, -3.23f + 0.04f, 3.099f));      // 0.057 m
    }

    [Fact]
    public void NoRowMatchesAnEmptyAnswerNotAGuess()
    {
        var rows = new[] { Row("Leaf", "", 0f, 0f, 0f), Row("Leaf", "", 5f, 0f, 0f) };
        Assert.Empty(DoorPoseModel.SelectRowsForNode(rows, 2.5f, 0f, 0f));
        Assert.Empty(DoorPoseModel.SelectRowsForNode(rows, float.NaN, 0f, 0f));
        Assert.Empty(DoorPoseModel.SelectRowsForNode(null, 0f, 0f, 0f));
        Assert.Empty(DoorPoseModel.SelectRowsForNode(new ArenaDoorPose[0], 0f, 0f, 0f));
    }

    [Fact]
    public void SeveralMatchingRowsAreAllKeptInTheirOrder()
    {
        // two rows for one position cannot happen with the real data (the sidecar test pins >= 0.356 m between namesakes);
        // the helper does not pick one at random, it returns both in sidecar order and NotePoses poses the node by each
        var first = Row("Leaf", "", 1f, 0f, 0f);
        var second = Row("Leaf", "", 1.01f, 0f, 0f);
        var other = Row("Leaf", "", 9f, 0f, 0f);
        var picked = DoorPoseModel.SelectRowsForNode(new[] { first, other, second }, 1f, 0f, 0f);
        Assert.Equal(new[] { first, second }, picked);
    }

    [Fact]
    public void APartKeepsOneRowPerNodeAndTheSidecarOrder()
    {
        // a part with its own pose (node "") that repeats, and two nodes below it (one unique, one that repeats too)
        var leafA = Row("Leaf", "", 1f, 0f, 0f);
        var leafB = Row("Leaf", "", 8f, 0f, 0f);
        var hinge = Row("Leaf", "Hinge/Arm", 0f, 1f, 0f);
        var lampA = Row("Leaf", "Lamp", 0f, 0f, 1f);
        var lampB = Row("Leaf", "Lamp", 0f, 0f, 7f);
        var rows = new[] { leafA, hinge, leafB, lampA, lampB };
        var positions = new Dictionary<string, float[]>
        {
            [""] = new[] { 8.01f, 0f, 0f },
            ["Hinge/Arm"] = new[] { 100f, 100f, 100f },     // a single row: never checked here
            ["Lamp"] = new[] { 0f, 0f, 1.02f },
        };
        var picked = DoorPoseModel.SelectRowsForPart(rows, node => positions[node]);
        Assert.Equal(new[] { hinge, leafB, lampA }, picked);
    }

    [Fact]
    public void APartWhoseNodeIsMissingKeepsItsWholeGroupSoTheCallerReportsTheNode()
    {
        var a = Row("Leaf", "Gone", 0f, 0f, 0f);
        var b = Row("Leaf", "Gone", 5f, 0f, 0f);
        var kept = Row("Leaf", "", 0f, 0f, 0f);
        var picked = DoorPoseModel.SelectRowsForPart(new[] { a, b, kept }, node => node == "Gone" ? null : new[] { 0f, 0f, 0f });
        Assert.Equal(new[] { a, b, kept }, picked);
        // a part none of whose repeated rows is its own: an empty answer
        Assert.Empty(DoorPoseModel.SelectRowsForPart(new[] { a, b }, node => new[] { 2.5f, 0f, 0f }));
    }

    [Fact]
    public void APartWithoutRowsOrWithOneRowComesBackUntouched()
    {
        var one = Row("Leaf", "", 1f, 2f, 3f);
        var rows = new[] { one };
        Assert.Same(rows, DoorPoseModel.SelectRowsForPart(rows, node => throw new InvalidOperationException("one row needs no position")));
        Assert.Empty(DoorPoseModel.SelectRowsForPart(null, node => null));
        Assert.Empty(DoorPoseModel.SelectRowsForPart(new ArenaDoorPose[0], node => null));
        var two = new[] { Row("A", "x", 0f, 0f, 0f), Row("A", "y", 0f, 0f, 0f) };
        Assert.Equal(two, DoorPoseModel.SelectRowsForPart(two, node => throw new InvalidOperationException("one row per node needs no position")));
        Assert.Same(two, DoorPoseModel.SelectRowsForPart(two, null));
    }

    [Theory]
    [InlineData(0f, false, true, false)]        // closed: RedFog is on
    [InlineData(-1f, false, true, false)]
    [InlineData(float.NaN, false, true, false)]
    [InlineData(1f, false, true, true)]         // open: GreenFog is on
    [InlineData(7f, false, true, true)]
    [InlineData(0f, true, false, true)]         // RedFog: on while closed, off once open
    [InlineData(1f, true, false, false)]
    public void ASwitchTakesItsClosedStateAtTheStartAndItsOpenStateAtTheEnd(float k, bool closedActive, bool openActive, bool expected)
    {
        Assert.Equal((bool?)expected, DoorPoseModel.SwitchStateAt(k, closedActive, openActive));
    }

    [Theory]
    [InlineData(0.001f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.999f)]
    public void ASwitchKeepsWhateverStateItHasWhileTheDriveRuns(float k)
    {
        Assert.Null(DoorPoseModel.SwitchStateAt(k, false, true));
        Assert.Null(DoorPoseModel.SwitchStateAt(k, true, false));
    }

    [Fact]
    public void ASwitchPartTakesAsLongAsTheLongestDriveOfItsBarrier()
    {
        Assert.Equal(0.85f, DoorPoseModel.SwitchPartDriveSeconds(new[] { 0.85f, 0.85f, 0.85f }));
        Assert.Equal(3.5f, DoorPoseModel.SwitchPartDriveSeconds(new[] { 2.5f, 3.5f, 0f, 2f }));
        Assert.Equal(0f, DoorPoseModel.SwitchPartDriveSeconds(new float[0]));                 // nothing moves: the lamp flips in the first frame
        Assert.Equal(0f, DoorPoseModel.SwitchPartDriveSeconds(null));
        Assert.Equal(0f, DoorPoseModel.SwitchPartDriveSeconds(new[] { float.NaN, -2f }));
        Assert.Equal(1f, DoorPoseModel.SwitchPartDriveSeconds(new[] { float.NaN, 1f }));
    }

    sealed class Node
    {
        public string Name;
        public readonly List<Node> Children = new List<Node>();
        public Node(string name, params Node[] children) { Name = name; Children.AddRange(children); }
    }

    static List<Node> Resolve(IEnumerable<Node> roots, string path) =>
        DoorPoseModel.ResolveAll(roots, path, n => n.Name, n => n.Children);

    [Fact]
    public void APathResolvesToEveryInstanceThatCarriesItAtEverySegment()
    {
        var frameA = new Node("Gates_Depot_Frame", new Node("Gates_Depot_Door_L"));
        var frameB = new Node("Gates_Depot_Frame", new Node("Gates_Depot_Door_L"));
        var roots = new List<Node> { new Node("OLD_WAREHOUSE_DOORS", frameA, new Node("Other"), frameB), new Node("ROOT_B") };
        var found = Resolve(roots, "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame");
        Assert.Equal(2, found.Count);
        Assert.Same(frameA, found[0]);                 // tree order
        Assert.Same(frameB, found[1]);
        Assert.Equal(2, Resolve(roots, "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame/Gates_Depot_Door_L").Count);

        // names that repeat at SEVERAL levels multiply: two roots named alike, each with two groups, each group with one door
        var door = new Node("Door");
        var twin = new[]
        {
            new Node("SPAWN", new Node("Group", new Node("Door")), new Node("Group", new Node("Door"), new Node("Door"))),
            new Node("SPAWN", new Node("Group", door)),
        };
        Assert.Equal(2, Resolve(twin, "SPAWN").Count);               // a bare name is the scene roots
        Assert.Equal(3, Resolve(twin, "SPAWN/Group").Count);
        Assert.Equal(4, Resolve(twin, "SPAWN/Group/Door").Count);
        Assert.Same(door, Resolve(twin, "SPAWN/Group/Door")[3]);
    }

    [Fact]
    public void APathThatLeadsNowhereResolvesToNothing()
    {
        var roots = new List<Node> { new Node("A", new Node("B")) };
        Assert.Empty(Resolve(roots, "A/C"));
        Assert.Empty(Resolve(roots, "B"));
        Assert.Empty(Resolve(roots, "A/B/C"));
        Assert.Empty(Resolve(roots, ""));
        Assert.Empty(Resolve(roots, null));
        Assert.Empty(Resolve(null, "A"));
        Assert.Single(Resolve(roots, "A/B"));
        // an exact name, not a prefix: "A (1)" is not "A"
        Assert.Empty(Resolve(new List<Node> { new Node("A (1)") }, "A"));
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
    public void EquatorHasTenBoothLeavesBay5SevenGatePosesAndAirPitThirtyRows()
    {
        Assert.Equal(10, ArenaMapRegistry.Equator.DoorPoses.Length);
        Assert.Equal(7, ArenaMapRegistry.Bay5.DoorPoses.Length);   // roller (3) shuts again (user, 2026-10-24): no row, no opened barrier
        Assert.Equal(19, ArenaMapRegistry.Bowl.DoorPoses.Length);
        Assert.Equal(30, ArenaMapRegistry.AirPit.DoorPoses.Length);
    }

    const string Passport = "PASSPORT_CONTROL_DOORS";

    static readonly string[] BusAssemblies = { "Apron_bus_MAZ_doors", "Apron_bus_MAZ_doors (1)", "Apron_bus_MAZ_doors (2)" };

    [Fact]
    public void TheFrontBusAssembliesHaveFourNodeRowsEachAndTheStandingBackRowHasNone()
    {
        var map = ArenaMapRegistry.AirPit;
        foreach (var assembly in BusAssemblies)
        {
            var rows = map.DoorPosesOf("BUS_DOORS", assembly);
            Assert.Equal(4, rows.Length);
            Assert.Equal(4, rows.Select(r => r.Node).Distinct().Count());                    // one row per node: no position needed to tell them apart
            Assert.Same(rows[0], DoorPoseModel.SelectRowsForPart(rows, node => throw new InvalidOperationException()).First());
            Assert.All(rows, r =>
            {
                Assert.Equal("borrowed", r.Source);
                Assert.Equal("inOutQuint", r.Ease);
                Assert.Equal(3.0f, r.Seconds);
                Assert.False(r.IsSwitch);
                Assert.True(DoorPoseModel.UsesSlerp(r.Source));
                Assert.True(DoorPoseModel.EaseIsUsable(r.Ease, r.Curve, out string why), why);
            });
            // the hinge swings 127 degrees about its z axis, the leaf below it follows; both nodes move
            Assert.Equal(new[] { "Apron_bus_MAZ_door_L_01_part_01", "Apron_bus_MAZ_door_L_01_part_01/Apron_bus_MAZ_door_L_01",
                                 "Apron_bus_MAZ_door_L_02_part_01", "Apron_bus_MAZ_door_L_02_part_01/Apron_bus_MAZ_door_L_02" },
                         rows.Select(r => r.Node).ToArray());
            Assert.All(rows, r => Assert.True(DoorPoseModel.RotationMoves(r.ClosedEuler, r.OpenEuler)));
        }
        // the back row stands open as Arena built it (the keep-structure rule): no row at all, but the barrier has rows
        foreach (var assembly in new[] { "Apron_bus_MAZ_doors (3)", "Apron_bus_MAZ_doors (4)", "Apron_bus_MAZ_doors (5)" })
            Assert.Empty(map.DoorPosesOf("BUS_DOORS", assembly));
        Assert.Equal(12, map.DoorPosesOfBarrier("BUS_DOORS").Length);
        // the walking band keeps its meaning: a collider under a moved hinge or leaf is a moved body, the assembly's frame is not
        var nodes = map.DoorPosesOf("BUS_DOORS", BusAssemblies[0]).Select(p => p.Node).ToArray();
        Assert.True(DoorPoseModel.IsMoved("Apron_bus_MAZ_door_L_01_part_01/Apron_bus_MAZ_door_L_01/Apron_bus_MAZ_door_L_01_COLLIDER", nodes));
        Assert.False(DoorPoseModel.IsMoved("Apron_bus_MAZ_body_COLLIDER", nodes));
    }

    static readonly (string name, int rows)[] PassportNames =
    {
        ("Passport_control_01_Door (1)", 3), ("Passport_control_01_Door (2)", 3), ("Passport_control_01_Door (3)", 4),
        ("Passport_control_01_Door (4)", 4), ("Passport_control_01_Door (5)", 1), ("Passport_control_01_Door (6)", 1),
    };

    [Fact]
    public void EachOfTheSixteenPassportLeavesResolvesToExactlyOneRowByItsClosedPosition()
    {
        var map = ArenaMapRegistry.AirPit;
        var claimed = new HashSet<ArenaDoorPose>();
        foreach (var (name, count) in PassportNames)
        {
            var named = map.DoorPosesOf(Passport, name);          // every row of the NAME: no `at`, so any instance position finds them
            Assert.Equal(count, named.Length);
            Assert.Equal(named, map.DoorPosesOf(Passport, name, -16.1f, 3.9f, 63.2f));
            foreach (var row in named)
            {
                Assert.Equal("", row.Node);                       // the leaf itself turns
                var own = DoorPoseModel.SelectRowsForNode(named, row.ClosedPosition[0], row.ClosedPosition[1], row.ClosedPosition[2]);
                Assert.Same(row, Assert.Single(own));
                Assert.True(claimed.Add(row), "one leaf per row");
                // the scene's y differs by 0.0004 m between the two lanes (-3.2296 / -3.2300): still the same leaf
                Assert.Same(row, Assert.Single(DoorPoseModel.SelectRowsForPart(named, node => new[] { row.ClosedPosition[0], row.ClosedPosition[1] + 0.0004f, row.ClosedPosition[2] })));
                if (count > 1)
                {
                    // a leaf one handspan off every row is nobody's: the part falls back
                    Assert.Empty(DoorPoseModel.SelectRowsForNode(named, row.ClosedPosition[0] + 0.06f, row.ClosedPosition[1], row.ClosedPosition[2]));
                }
            }
        }
        Assert.Equal(16, claimed.Count);
    }

    [Fact]
    public void ThePassportLeavesTurnAboutTheVerticalAxisInArenasSeventeenTenthsOfASecond()
    {
        var leaves = ArenaMapRegistry.AirPit.DoorPosesOfBarrier(Passport).Where(p => !p.IsSwitch).ToArray();
        Assert.Equal(16, leaves.Length);
        foreach (var leaf in leaves)
        {
            Assert.Equal("dotanimator", leaf.Source);
            Assert.Equal("inOutQuint", leaf.Ease);
            Assert.Equal(1.7f, leaf.Seconds);
            Assert.Equal(leaf.ClosedPosition, leaf.OpenPosition);                          // turned, not moved
            Assert.True(DoorPoseModel.RotationMoves(leaf.ClosedEuler, leaf.OpenEuler));
            Assert.True(DoorPoseModel.UsesSlerp(leaf.Source));
            // one axis (the euler y, the vertical one after the -90 x), under 180 degrees: the quaternion slerp is the short turn
            Assert.Equal(leaf.ClosedEuler[0], leaf.OpenEuler[0]);
            Assert.Equal(leaf.ClosedEuler[2], leaf.OpenEuler[2]);
            Assert.InRange(Math.Abs(leaf.OpenEuler[1] - leaf.ClosedEuler[1]), 89f, 91f);
            Assert.Equal(0.85f, DoorPoseModel.ScaledSeconds(leaf.Seconds, DoorPoseModel.DefaultDriveScale), 4);
        }
        // the lane at local z 3.099 (world z 66.28) opens to +90 degrees, the one at 5.938 (world z 69.12) to -90 (the data agent's correction of the brief)
        Assert.All(leaves.Where(p => Math.Abs(p.ClosedPosition[2] - 3.099f) < 0.01f), p => Assert.Equal(90f, p.OpenEuler[1]));
        Assert.All(leaves.Where(p => Math.Abs(p.ClosedPosition[2] - 5.938f) < 0.01f), p => Assert.Equal(-90f, p.OpenEuler[1]));
    }

    [Fact]
    public void TheTwoLampsAreSwitchesWithNothingToMoveAndNothingToCarve()
    {
        var map = ArenaMapRegistry.AirPit;
        var red = Assert.Single(map.DoorPosesOf(Passport, "RED"));
        var green = Assert.Single(map.DoorPosesOf(Passport, "GREEN"));
        Assert.Equal("RedFog", red.Node);
        Assert.Equal("GreenFog", green.Node);
        Assert.True(red.IsSwitch && green.IsSwitch);
        Assert.Equal((true, false), (red.ClosedActive.Value, red.OpenActive.Value));          // the red lamp goes out ...
        Assert.Equal((false, true), (green.ClosedActive.Value, green.OpenActive.Value));      // ... and the green one comes on
        foreach (var sw in new[] { red, green })
        {
            Assert.Equal(0f, sw.Seconds);
            Assert.Equal("linear", sw.Ease);
            Assert.Equal(sw.ClosedPosition, sw.OpenPosition);                                  // the scene's transform on both sides
            Assert.Equal(sw.ClosedEuler, sw.OpenEuler);
            Assert.True(DoorPoseModel.EaseIsUsable(sw.Ease, sw.Curve, out string why), why);
            // the ease maps the drive to the switch: closed at the start, open at the end, untouched in between
            Assert.Equal(sw.ClosedActive, DoorPoseModel.SwitchStateAt(DoorPoseModel.EaseOf(sw.Ease, sw.Curve, 0f), sw.ClosedActive.Value, sw.OpenActive.Value));
            Assert.Null(DoorPoseModel.SwitchStateAt(DoorPoseModel.EaseOf(sw.Ease, sw.Curve, 0.5f), sw.ClosedActive.Value, sw.OpenActive.Value));
            Assert.Equal(sw.OpenActive, DoorPoseModel.SwitchStateAt(DoorPoseModel.EaseOf(sw.Ease, sw.Curve, 1f), sw.ClosedActive.Value, sw.OpenActive.Value));
            // Arena's duration word is 0: nothing to scale, the part's own drive length comes from its barrier (SwitchPartDriveSeconds)
            Assert.Equal(0f, DoorPoseModel.ScaledSeconds(sw.Seconds, DoorPoseModel.DefaultDriveScale));
        }
        // nothing else of the barrier is a switch, and no other map has one
        Assert.Equal(2, map.DoorPoses.Count(p => p.IsSwitch));
        Assert.DoesNotContain(new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5, ArenaMapRegistry.Bowl }.SelectMany(m => m.DoorPoses), p => p.IsSwitch);
    }

    /// <summary>The release of a held passport gate, simulated with the same pure rules the coroutine reads (SpawnGates.Open): the
    /// 16 leaves drive 0.85 s, the two lamp parts (no motion of their own) take the longest drive of their barrier, so the lamps
    /// flip in the frame the last leaf finishes and in no earlier one.</summary>
    [Fact]
    public void TheLampsFlipInTheFrameTheLastLeafStandsOpenAndNotBefore()
    {
        var map = ArenaMapRegistry.AirPit;
        float scale = DoorPoseModel.DefaultDriveScale;
        var leafSeconds = map.DoorPosesOfBarrier(Passport).Where(p => !p.IsSwitch).Select(p => DoorPoseModel.ScaledSeconds(p.Seconds, scale)).ToArray();
        float lampSeconds = DoorPoseModel.SwitchPartDriveSeconds(leafSeconds);
        Assert.Equal(0.85f, lampSeconds, 4);
        var red = map.DoorPosesOf(Passport, "RED").Single();
        var green = map.DoorPosesOf(Passport, "GREEN").Single();
        bool redOn = red.ClosedActive.Value, greenOn = green.ClosedActive.Value;
        bool leavesOpen = false;
        int flippedAt = -1;
        const float dt = 1f / 60f;
        for (int frame = 0; frame < 120 && flippedAt < 0; frame++)
        {
            float elapsed = frame * dt;
            bool done = DoorPoseModel.Finished(elapsed, lampSeconds);
            // the same frame test as the leaves' own: every leaf is done in the frame the lamps are
            Assert.Equal(done, leafSeconds.All(sec => DoorPoseModel.Finished(elapsed, sec)));
            float progress = DoorPoseModel.Progress(elapsed, lampSeconds);
            var r = DoorPoseModel.SwitchStateAt(DoorPoseModel.EaseOf(red.Ease, red.Curve, done ? 1f : progress), red.ClosedActive.Value, red.OpenActive.Value);
            var g = DoorPoseModel.SwitchStateAt(DoorPoseModel.EaseOf(green.Ease, green.Curve, done ? 1f : progress), green.ClosedActive.Value, green.OpenActive.Value);
            if (r.HasValue) redOn = r.Value;
            if (g.HasValue) greenOn = g.Value;
            if (!done)
            {
                Assert.True(redOn && !greenOn, $"frame {frame}: the lamps keep their closed state while the leaves swing");
                continue;
            }
            leavesOpen = true;
            flippedAt = frame;
        }
        Assert.True(leavesOpen);
        Assert.False(redOn);
        Assert.True(greenOn);
        Assert.InRange(flippedAt * dt, 0.85f, 0.85f + dt + 1e-4f);
        // a release that snaps (SpawnGateDrive false) is Progress 1 at once: the same end state
        Assert.Equal((bool?)false, DoorPoseModel.SwitchStateAt(DoorPoseModel.EaseOf(red.Ease, red.Curve, 1f), true, false));
    }

    [Fact]
    public void AtTheDefaultScaleAirPitOpensInAboutOneAndAHalfSecondsAndTheSwitchesAreInstant()
    {
        float scale = DoorPoseModel.DefaultDriveScale;
        var map = ArenaMapRegistry.AirPit;
        Assert.All(map.DoorPoses.Where(p => p.Barrier == "BUS_DOORS"), p => Assert.Equal(1.5f, DoorPoseModel.ScaledSeconds(p.Seconds, scale), 4));
        Assert.All(map.DoorPoses.Where(p => p.Barrier == Passport && !p.IsSwitch), p => Assert.Equal(0.85f, DoorPoseModel.ScaledSeconds(p.Seconds, scale), 4));
        Assert.Equal(1.5f, map.DoorPoses.Max(p => DoorPoseModel.ScaledSeconds(p.Seconds, scale)), 4);
        // at Arena's own timing (scale 1) the bus doors take the port's 3 s, the passport leaves Arena's 1.7 s
        Assert.Equal(3.0f, map.DoorPoses.Max(p => DoorPoseModel.ScaledSeconds(p.Seconds, 1f)), 4);
    }

    static readonly (string barrier, float x, float y, float z, float secondsL, float secondsR, string source)[] BoothAssemblies =
    {
        ("SPAWN_DOORS/Door_Sliding_Respawn", -4.328f, 0.048f, 40.02f, 5f, 5f, "dotanimator"),
        ("SPAWN_DOORS/Door_Sliding_Respawn", 4.535f, 0.048f, 40.02f, 5f, 7f, "dotanimator"),         // the 7 s leaf
        ("SPAWN_DOORS/Door_Sliding_Respawn (1)", -4.46f, 0.048f, -35.045f, 5f, 5f, "dotanimator"),
        ("SPAWN_DOORS/Door_Sliding_Respawn (2)", 4.403f, 0.048f, -35.045f, 5f, 5f, "dotanimator"),
        ("SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)", -3.753f, 0.048f, -45.87f, 5f, 5f, "borrowed"),
    };

    [Fact]
    public void EveryBoothLeafIsFoundByItsAssemblyPathAndPositionAndTwoNamesakesAreToldApart()
    {
        var map = ArenaMapRegistry.Equator;
        foreach (var (barrier, x, y, z, secondsL, secondsR, source) in BoothAssemblies)
        {
            var left = Assert.Single(map.DoorPosesOf(barrier, "Door_Sliding_Respawn_L", x, y, z));
            var right = Assert.Single(map.DoorPosesOf(barrier, "Door_Sliding_Respawn_R", x, y, z));
            Assert.Equal(secondsL, left.Seconds);
            Assert.Equal(secondsR, right.Seconds);
            Assert.Equal(2.0f, left.OpenPosition[0]);
            Assert.Equal(-2.0f, right.OpenPosition[0]);
            Assert.All(new[] { left, right }, p =>
            {
                Assert.Equal(source, p.Source);
                Assert.Equal("inOutQuint", p.Ease);
                Assert.Equal("", p.Node);                                  // the leaf itself moves
                Assert.Equal(p.ClosedPosition[1], p.OpenPosition[1]);       // x only
                Assert.Equal(p.ClosedPosition[2], p.OpenPosition[2]);
            });
            // every row carries an `at`: without a position there is no instance to match, and a position a metre away is another one
            Assert.Empty(map.DoorPosesOf(barrier, "Door_Sliding_Respawn_L"));
            Assert.Empty(map.DoorPosesOf(barrier, "Door_Sliding_Respawn_L", x + 1f, y, z));
            Assert.Empty(map.DoorPosesOf(barrier, "Door_Sliding_Respawn_L", x, y, z + 0.06f));
            Assert.Equal(2, map.DoorPosesOfBarrier(barrier, x, y, z).Length);
        }
        // the booth leaves are no barrier: an unrelated name matches nothing
        Assert.Empty(map.DoorPosesOf("SPAWN_DOORS/Door_Sliding_Respawn (4)", "Door_Sliding_Respawn_L", 0f, 0f, 0f));
    }

    [Fact]
    public void ADotAnimatorPoseTurnsBySlerpOnlyWhereItReallyTurns()
    {
        foreach (var pose in ArenaMapRegistry.Equator.DoorPoses.Concat(ArenaMapRegistry.Bay5.DoorPoses))
        {
            Assert.True(DoorPoseModel.UsesSlerp(pose.Source), pose.Barrier);
            Assert.True(DoorPoseModel.EaseIsUsable(pose.Ease, pose.Curve, out string why), why);
            bool turns = DoorPoseModel.RotationMoves(pose.ClosedEuler, pose.OpenEuler);
            bool depot = pose.Barrier.EndsWith("Gates_Depot_Frame", StringComparison.Ordinal);
            Assert.Equal(depot, turns); // the booth leaves and the rollers slide; only the depot leaves swing
            if (!turns) continue;
            // one axis, under 180 degrees: the quaternion slerp is then the same turn as an euler lerp, the short way round
            int changed = 0;
            for (int a = 0; a < 3; a++)
            {
                float delta = Math.Abs(pose.OpenEuler[a] - pose.ClosedEuler[a]);
                if (delta > 1e-4f) { changed++; Assert.True(delta < 180f, $"{pose.Barrier} {pose.Part}: {delta} degrees"); }
            }
            Assert.Equal(1, changed);
            Assert.Equal(pose.ClosedPosition, pose.OpenPosition); // swung about the hinge, not moved
        }
    }

    [Theory]
    [InlineData("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)", 0.13f, 3.222f, "dotanimator")]
    [InlineData("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)", 0.124f, 3.213f, "dotanimator")]
    [InlineData("WAREHOUSE_DOORS (1)/One_man_Door", 0.15f, 3.323f, "dotanimator")]
    public void EveryRollerLiftsItsWholeRootInYAndIsFoundWithoutAPosition(string barrier, float closedY, float openY, string source)
    {
        var map = ArenaMapRegistry.Bay5;
        // part "" is the barrier root itself; the rows carry no `at`, so any position finds them
        var pose = Assert.Single(map.DoorPosesOf(barrier, "", 129.15f, -2.9f, 45.9f));
        Assert.Same(pose, Assert.Single(map.DoorPosesOf(barrier, "")));
        Assert.Same(pose, Assert.Single(map.DoorPosesOfBarrier(barrier)));
        Assert.Equal("", pose.Node);
        Assert.Equal(source, pose.Source);
        Assert.Equal("linear", pose.Ease);
        Assert.Equal(4.0f, pose.Seconds);
        Assert.Equal(closedY, pose.ClosedPosition[1]);
        Assert.Equal(openY, pose.OpenPosition[1]);
        Assert.Equal(pose.ClosedPosition[0], pose.OpenPosition[0]);
        Assert.Equal(pose.ClosedPosition[2], pose.OpenPosition[2]);
        Assert.False(DoorPoseModel.RotationMoves(pose.ClosedEuler, pose.OpenEuler));
        // a roller has no parts of its own with poses: its children (LODs, colliders) stay with the root
        Assert.Empty(map.DoorPosesOf(barrier, "Rollete_Gate_Door_01_COLLIDER"));
        // the whole root moves, so every collider below it counts as moved
        Assert.True(DoorPoseModel.IsMoved("Rollete_Gate_Door_01_COLLIDER", new[] { pose.Node }));
    }

    [Theory]
    [InlineData(50.574f, -3.895f, 7.525f, 162.432f, -141.759f)]
    [InlineData(69.045f, -3.883f, -6.317f, 138.837f, -98.974f)]
    public void BothDepotGatesAreToldApartByTheirRootPosition(float x, float y, float z, float openL, float openR)
    {
        var map = ArenaMapRegistry.Bay5;
        const string barrier = "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame";
        Assert.Equal(2, map.DoorPosesOfBarrier(barrier, x, y, z).Length);
        var left = Assert.Single(map.DoorPosesOf(barrier, "Gates_Depot_Door_L", x, y, z));
        var right = Assert.Single(map.DoorPosesOf(barrier, "Gates_Depot_Door_R", x, y, z));
        Assert.Equal("Gates_Depot_Door_L_Leaf", left.Node);
        Assert.Equal("Gates_Depot_Door_R_Leaf", right.Node);
        Assert.Equal(openL, left.OpenEuler[2]);
        Assert.Equal(openR, right.OpenEuler[2]);
        Assert.All(new[] { left, right }, p =>
        {
            Assert.Equal(7.0f, p.Seconds);
            Assert.Equal("curve", p.Ease);
            Assert.Equal(3, p.CurveKeyCount);
            Assert.Equal("dotanimator", p.Source);
        });
        // no row is for "every instance": without a position, or a metre off, nothing matches
        Assert.Empty(map.DoorPosesOfBarrier(barrier));
        Assert.Empty(map.DoorPosesOf(barrier, "Gates_Depot_Door_L"));
        Assert.Empty(map.DoorPosesOfBarrier(barrier, x + 1f, y, z));
        // the posts and frame meshes of the barrier have no row: a barrier that animates keeps them standing
        Assert.Empty(map.DoorPosesOf(barrier, "Telfer_Help", x, y, z));
        Assert.NotEmpty(map.DoorPosesOfBarrier(barrier, x, y, z));
        // only the leaf below the part is a moved node: the post beside the gate is not
        Assert.True(DoorPoseModel.IsMoved("Gates_Depot_Door_L_Leaf/Gates_Depot_Door_L_COLLIDER", new[] { left.Node }));
        Assert.False(DoorPoseModel.IsMoved("Telfer_Help/Telfer_Help_Collider", new[] { left.Node }));
    }

    [Fact]
    public void TheDepotCurvesStartAtZeroEndAtOneAndPutAboutEightyPercentIntoTheFirstFortyPercent()
    {
        var curves = ArenaMapRegistry.Bay5.DoorPoses.Where(p => p.Ease == "curve").ToArray();
        Assert.Equal(4, curves.Length);
        foreach (var pose in curves)
        {
            Assert.Equal(0f, DoorPoseModel.EaseOf(pose.Ease, pose.Curve, 0f));
            Assert.Equal(1f, DoorPoseModel.EaseOf(pose.Ease, pose.Curve, 1f));
            Assert.InRange(DoorPoseModel.EaseOf(pose.Ease, pose.Curve, 0.4f), 0.79f, 0.81f);
            Assert.Equal(pose.Curve[5], DoorPoseModel.EaseOf(pose.Ease, pose.Curve, 0.4f), 6); // the middle key's value
            float previous = -1f;
            for (int i = 0; i <= 200; i++)
            {
                float v = DoorPoseModel.EaseOf(pose.Ease, pose.Curve, i / 200f);
                Assert.InRange(v, 0f, 1f);
                Assert.True(v >= previous - 1e-6f);
                previous = v;
            }
        }
    }

    [Fact]
    public void AtTheDefaultScaleEveryGateOpensInUnderFourSeconds()
    {
        // 2.5 s for a booth leaf, 3.5 s for the slow one and the depot gates, 2 s for a roller; Bowl's longest drive is 6.75 s
        foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5 })
        {
            Assert.All(map.DoorPoses, p => Assert.InRange(DoorPoseModel.ScaledSeconds(p.Seconds, DoorPoseModel.DefaultDriveScale), 2f, 3.5f));
        }
        Assert.Equal(6.75f, ArenaMapRegistry.Bowl.DoorPoses.Max(p => DoorPoseModel.ScaledSeconds(p.Seconds, DoorPoseModel.DefaultDriveScale)), 4);
    }

    [Fact]
    public void BowlKeepsItsClipPosesSmoothstepAndComponentWiseEuler()
    {
        Assert.All(ArenaMapRegistry.Bowl.DoorPoses, p =>
        {
            Assert.Equal("clip", p.Source);
            Assert.Equal("smoothstep", p.Ease);
            Assert.False(DoorPoseModel.UsesSlerp(p.Source));
            Assert.Equal(DoorPoseModel.Ease(0.3f), DoorPoseModel.EaseOf(p.Ease, p.Curve, 0.3f)); // the ease the 2026-10-22 build drove with
        });
    }
}

/// <summary>Runs only when the ignored extract of AirPit (work/extract/airpit-native-v9, BSG-derived) is on this machine.</summary>
internal sealed class AirPitSceneFactAttribute : FactAttribute
{
    public AirPitSceneFactAttribute()
    {
        if (AirPitScene.Path() == null) Skip = "work/extract/airpit-native-v9 is not present (BSG-derived, ignored by git)";
    }
}

/// <summary>The gate scene of AirPit (Arena level 28) as the extractor wrote it: objects with id, parent_id, name, active and a
/// local position; read as data only.</summary>
internal static class AirPitScene
{
    internal sealed class Obj
    {
        public string Id, Name, ParentId;
        public bool Active;
        public float[] LocalPosition;
        public readonly List<Obj> Children = new List<Obj>();
    }

    internal static string Path()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = System.IO.Path.Combine(dir.FullName, "work", "extract", "airpit-native-v9", "scenes", "Arena_Airpit_TeamFight_DesignStuff.json");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static List<Obj> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path()));
        var all = new Dictionary<string, Obj>();
        var order = new List<Obj>();
        foreach (var o in doc.RootElement.GetProperty("objects").EnumerateArray())
        {
            var obj = new Obj
            {
                Id = o.GetProperty("id").GetString(),
                Name = o.GetProperty("name").GetString(),
                ParentId = o.TryGetProperty("parent_id", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null,
                Active = o.GetProperty("active").GetBoolean(),
                LocalPosition = o.GetProperty("local_position").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray(),
            };
            all[obj.Id] = obj;
            order.Add(obj);
        }
        foreach (var obj in order)
        {
            if (obj.ParentId != null && all.TryGetValue(obj.ParentId, out var parent)) parent.Children.Add(obj);
        }
        return order;
    }

    /// <summary>The node below <paramref name="start"/> a '/'-separated path of names leads to (first match per level, like
    /// Transform.Find), null when there is none.</summary>
    internal static Obj Find(Obj start, string path)
    {
        var current = start;
        foreach (var segment in path.Split('/'))
        {
            current = current.Children.FirstOrDefault(c => c.Name == segment);
            if (current == null) return null;
        }
        return current;
    }
}

/// <summary>
/// The AirPit rows read against the real scene (the lookups the client runs at raid start, offline): every passport leaf of the
/// scene takes exactly one row, the lamps agree with the scene's active state, every node of every bus-door row exists at the
/// row's closed position, and the standing back row has no row. Skipped without the ignored extract.
/// </summary>
public sealed class DoorPoseAirPitSceneTests
{
    const string Passport = "PASSPORT_CONTROL_DOORS";

    [AirPitSceneFact]
    public void EveryPassportChildOfTheSceneHasRowsAndEveryLeafTakesExactlyOneByItsPosition()
    {
        var map = ArenaMapRegistry.AirPit;
        var scene = AirPitScene.Load();
        var barrier = Assert.Single(scene, o => o.Name == Passport);
        Assert.Equal(18, barrier.Children.Count);                                   // 16 leaves + RED + GREEN, nothing else would be left standing
        var claimed = new HashSet<ArenaDoorPose>();
        foreach (var child in barrier.Children)
        {
            var named = map.DoorPosesOf(Passport, child.Name, barrier.LocalPosition[0], barrier.LocalPosition[1], barrier.LocalPosition[2]);
            Assert.True(named.Length > 0, $"'{child.Name}' has no row: the keep-structure rule would leave it standing");
            if (child.Name == "RED" || child.Name == "GREEN")
            {
                var lamp = Assert.Single(named);
                Assert.True(lamp.IsSwitch);
                var fog = Assert.Single(child.Children);                             // RedFog / GreenFog
                Assert.Equal(lamp.Node, fog.Name);
                Assert.True(child.Active);                                           // the part stays active, only the fog node switches
                Assert.Equal(lamp.ClosedActive.Value, fog.Active);                   // the scene agrees with the row's closed state (NotePoses' check)
                continue;
            }
            var own = Assert.Single(DoorPoseModel.SelectRowsForNode(named, child.LocalPosition[0], child.LocalPosition[1], child.LocalPosition[2]));
            Assert.True(claimed.Add(own), $"'{child.Name}' at {child.LocalPosition[0]:F3}, {child.LocalPosition[2]:F3} takes a row another leaf took");
            Assert.True(DoorPoseModel.PoseMatchesScene(
                (float)Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(child.LocalPosition[i] - own.ClosedPosition[i], 2))), 0f));
        }
        Assert.Equal(16, claimed.Count);
        // PASSPORT_CONTROL_EMISSIVE keeps the legacy rule (hidden on the release): no row
        Assert.Empty(map.DoorPosesOfBarrier("PASSPORT_CONTROL_EMISSIVE"));
    }

    [AirPitSceneFact]
    public void TheBusDoorRowsNameNodesThatExistAtTheirClosedPositionAndTheBackRowIsLeftStanding()
    {
        var map = ArenaMapRegistry.AirPit;
        var scene = AirPitScene.Load();
        var bus = Assert.Single(scene, o => o.Name == "BUS_DOORS");
        Assert.Equal(6, bus.Children.Count);
        foreach (var assembly in bus.Children)
        {
            var rows = map.DoorPosesOf("BUS_DOORS", assembly.Name);
            bool front = assembly.Name == "Apron_bus_MAZ_doors" || assembly.Name == "Apron_bus_MAZ_doors (1)" || assembly.Name == "Apron_bus_MAZ_doors (2)";
            if (!front)
            {
                Assert.Empty(rows);                                                  // already open in the scene: stands as Arena built it
                continue;
            }
            Assert.Equal(4, rows.Length);
            Assert.Equal(rows, DoorPoseModel.SelectRowsForPart(rows, node => throw new InvalidOperationException("one row per node needs no position")));
            foreach (var row in rows)
            {
                var node = AirPitScene.Find(assembly, row.Node);
                Assert.True(node != null, $"{assembly.Name}: node '{row.Node}' not found");
                Assert.True(node.Active);
                double error = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(node.LocalPosition[i] - row.ClosedPosition[i], 2)));
                Assert.True(error <= DoorPoseModel.PoseMatchPosition, $"{assembly.Name}/{row.Node}: {error:F3} m off the data's closed position");
            }
        }
    }
}

/// <summary>
/// The client files sit on Unity types; their wiring is read as text like the other runtime files (SpawnGatesWiringTests).
/// </summary>
public sealed class DoorPoseWiringTests
{
    const string Passport = "PASSPORT_CONTROL_DOORS";

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
        var barriers = Between(code, "static void OpenBarriers(List<Transform> openings, StringBuilder sb, bool openPlain)", "// Is the way out clear?");
        int named = barriers.IndexOf("var named = map.DoorPosesOf(path, part.name, at.x, at.y, at.z);", StringComparison.Ordinal);
        int poses = barriers.IndexOf("var poses = SpawnGates.RowsForPart(part, named);", StringComparison.Ordinal);
        int note = barriers.IndexOf("SpawnGates.NotePoses(part, unmatched ? named : poses);", StringComparison.Ordinal);
        int held = barriers.IndexOf("SpawnGates.OfferHeld(part)", StringComparison.Ordinal);
        int start = barriers.IndexOf("SpawnGates.Offer(part)", StringComparison.Ordinal);
        int park = barriers.IndexOf("SpawnGates.Park(part)", StringComparison.Ordinal);
        int off = barriers.IndexOf("part.gameObject.SetActive(false);", park, StringComparison.Ordinal);
        Assert.True(named > 0 && named < poses && poses < note && note < held && held < start && start < park && park < off,
            "DoorPosesOf (every row of the name), RowsForPart (this child's), NotePoses, OfferHeld, Offer, Park, SetActive(false)");
        Assert.True(off - start < 400, "the existing offer-before-switch-off distance still holds");
        Assert.Contains("parked > 0 ? $\", parked {parked} part(s) at the open pose\"", code);
        // the park carvers wait for the NavMesh: one seal after BOTH opening steps (booth leaves and barrier parts), like the held gates
        int doorsStep = code.IndexOf("OpenDoors(openings, sb);", StringComparison.Ordinal);
        int barriersStep = code.IndexOf("OpenBarriers(openings, sb, barriers);", StringComparison.Ordinal);
        int seal = code.IndexOf("SpawnGates.SealParked(", barriersStep, StringComparison.Ordinal);
        Assert.True(doorsStep > 0 && doorsStep < barriersStep && barriersStep < seal);
        Assert.Single(Regex.Matches(code, @"SpawnGates\.SealParked\("));
    }

    [Fact]
    public void ABarrierRootWithAPoseIsParkedWholeAndABarrierThatAnimatesKeepsItsUnposedPartsStanding()
    {
        var code = Text("ArenaMapPort.Client/BoothDoorRepairs.cs");
        var barriers = Between(code, "static void OpenBarriers(List<Transform> openings, StringBuilder sb, bool openPlain)", "// Is the way out clear?");
        // the root: resolved while closed, then (only when the data applied) offered or parked, like a part
        int rootPoses = barriers.IndexOf("var rootPoses = map.DoorPosesOf(path, \"\", at.x, at.y, at.z);", StringComparison.Ordinal);
        int note = barriers.IndexOf("SpawnGates.NotePoses(barrier, rootPoses);", StringComparison.Ordinal);
        int has = barriers.IndexOf("SpawnGates.HasPoses(barrier)", StringComparison.Ordinal);
        int held = barriers.IndexOf("SpawnGates.OfferHeld(barrier)", StringComparison.Ordinal);
        int start = barriers.IndexOf("SpawnGates.Offer(barrier)", StringComparison.Ordinal);
        int park = barriers.IndexOf("SpawnGates.Park(barrier)", StringComparison.Ordinal);
        Assert.True(rootPoses > 0 && rootPoses < note && note < has && has < held && held < start && start < park, "rootPoses, NotePoses, HasPoses, OfferHeld, Offer, Park");
        Assert.Contains("role == ArenaGateRole.Held && SpawnGates.OfferHeld(barrier)", barriers);
        Assert.Contains("role == ArenaGateRole.Start && SpawnGates.Offer(barrier)", barriers);
        // a parked or held root keeps its children as they are; a root whose pose failed is hidden child by child like any barrier
        Assert.Contains("for (int i = 0; i < barrier.childCount && !rootDone; i++)", barriers);
        // the rows of THIS instance decide; an instance no row names is hidden the old way with one Warning (lead, 2026-10-24)
        Assert.Contains("var instancePoses = map.DoorPosesOfBarrier(path, at.x, at.y, at.z);", barriers);
        Assert.Contains("if (instancePoses.Length == 0 && PathHasPoseRows(map, path))", barriers);
        Assert.Contains("bool keepStructure = instancePoses.Length > 0 && !(rootPoses.Length > 0 && !rootDone);", barriers);
        Assert.Contains("if (poses.Length == 0 && keepStructure && !unmatched)", barriers);
        Assert.Contains("part(s) without a pose left standing", barriers);
        // the old rule (hide every non-frame part) is still the only switch-off, and still skips frame parts
        Assert.Single(Regex.Matches(barriers, @"SetActive\(false\)"));
        Assert.Contains("part.name.IndexOf(FramePart, StringComparison.OrdinalIgnoreCase) >= 0", barriers);
    }

    /// <summary>The spawn placement repair must not push a spawn out of a respawn booth any more: Equator's player spawns stand
    /// on Arena's markers inside the booths (2026-10-23), and the old booth-avoidance rule moved them 6 to 7.5 m out, one into
    /// the revolving entrance door (GPU-2, 2026-10-24 01:43). The SPAWN_BOUND disabling and the physics checks stay.</summary>
    [Fact]
    public void TheSpawnPlacementRepairLeavesTheBoothSpawnsWhereArenaPutThem()
    {
        var code = Text("ArenaMapPort.Client/SpawnPlacementRepairs.cs");
        Assert.DoesNotContain("name.Contains(\"_Respawn_\")", code);
        Assert.DoesNotContain("boothBounds", code);
        Assert.Contains("StartsWith(\"SPAWN_BOUND\")", code);
        Assert.Contains("Physics.CheckCapsule(bottom, top, CapsuleRadius, mask, QueryTriggerInteraction.Ignore)", code);
    }

    [Fact]
    public void EveryInstanceOfABarrierPathOpensAndTheFirstMatchResolverStaysForLampFill()
    {
        var code = Text("ArenaMapPort.Client/BoothDoorRepairs.cs");
        var barriers = Between(code, "static void OpenBarriers(List<Transform> openings, StringBuilder sb, bool openPlain)", "// Is the way out clear?");
        Assert.Contains("var found = FindAllByPath(path);", barriers);
        Assert.DoesNotContain("FindByPath(path)", barriers);
        Assert.Contains("{found.Count} instance(s) share this path", barriers);
        Assert.Contains("foreach (var barrier in found)", barriers);
        // each instance gets the rows for ITS position
        Assert.Contains("var at = barrier.position;", barriers);
        var resolver = Between(code, "internal static List<Transform> FindAllByPath(string path)", "internal static List<Transform> FindByPath(string path)");
        Assert.Contains("DoorPoseModel.ResolveAll(roots, path, t => t.name, ChildrenOf)", resolver);
        Assert.Contains("OwnedSceneRoots()", resolver);
        Assert.Contains("internal static List<Transform> FindByPath(string path) // also LampFillRepairs", code);
        Assert.Contains("BoothDoorRepairs.FindByPath(path)", Text("ArenaMapPort.Client/LampFillRepairs.cs"));
    }

    [Fact]
    public void ABoothLeafIsLookedUpByItsAssemblyAndParkedBeforeItIsSwitchedOff()
    {
        var code = Text("ArenaMapPort.Client/BoothDoorRepairs.cs");
        var doors = Between(code, "static void OpenDoors(List<Transform> openings, StringBuilder sb)", "// Static barriers the descriptor names by hierarchy path");
        int poses = doors.IndexOf("var poses = PosesOfLeaf(map, t);", StringComparison.Ordinal);
        int note = doors.IndexOf("SpawnGates.NotePoses(t, poses);", StringComparison.Ordinal);
        int offer = doors.IndexOf("SpawnGates.Offer(t)", StringComparison.Ordinal);
        int park = doors.IndexOf("SpawnGates.Park(t)", StringComparison.Ordinal);
        int off = doors.IndexOf("t.gameObject.SetActive(false);", StringComparison.Ordinal);
        Assert.True(poses > 0 && poses < note && note < offer && offer < park && park < off, "PosesOfLeaf, NotePoses, Offer, Park, SetActive(false)");
        Assert.Contains("parked > 0 ? $\", parked {parked} leaf(s) at the open pose\"", doors);
        // the lookup: the leaf's parent is the assembly, told apart by hierarchy path AND world position
        var lookup = Between(code, "static ArenaDoorPose[] PosesOfLeaf(ArenaMapDescriptor map, Transform leaf)", "// \"SceneRoot/Child/Grandchild (n)\"");
        Assert.Contains("var assembly = leaf.parent;", lookup);
        Assert.Contains("var at = assembly.position;", lookup);
        Assert.Contains("map.DoorPosesOf(HierarchyPath(assembly), leaf.name, at.x, at.y, at.z)", lookup);
        // a leaf the data does not know is one Warning, then the old switch-off
        Assert.Contains("have no door pose in the descriptor", doors);
        Assert.Contains("Plugin.Log.LogWarning", doors);
        // the path form is the descriptor's: names from the scene root down, no scene name
        var path = Between(code, "internal static string HierarchyPath(Transform t)", "static IEnumerable<Transform> ChildrenOf");
        Assert.Contains("for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, '/').Insert(0, p.name);", path);
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
    public void TheTimingScaleIsANewKeyOnSettingsWithTheUsersHalfAsDefaultAndTheDriveSecondsAreFixedPerRelease()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        Assert.Contains("_driveScale = s.Bind(\"Spawns\", \"SpawnGateDriveScale\", DoorPoseModel.DefaultDriveScale, new ConfigDescription(", gates);
        Assert.Contains("new AcceptableValueRange<float>(DoorPoseModel.MinDriveScale, DoorPoseModel.MaxDriveScale)", gates);
        Assert.Contains("Multiplies the authored drive durations of every map, Bowl included; 1 = Arena's own timing", gates);
        Assert.Equal(0.5f, DoorPoseModel.DefaultDriveScale);
        Assert.Equal(0.1f, DoorPoseModel.MinDriveScale);
        Assert.Equal(1.0f, DoorPoseModel.MaxDriveScale);
        // the old keys keep their meaning: the slide describes the pose-less leaf, the drive switch still snaps
        Assert.Contains("How long a sliding respawn-booth leaf WITHOUT an Arena pose", gates);
        Assert.Contains("when the gates release; false snaps them to the open pose at once", gates);
        // read once at the start of a release, applied to every part that drives
        var open = Between(gates, "static IEnumerator Open(string reason)", "static void OnSceneUnloaded(Scene scene)");
        Assert.Contains("float scale = _driveScale.Value;", open);
        Assert.Contains("posed.DriveSeconds = DoorPoseModel.ScaledSeconds(posed.Seconds, scale);", open);
        Assert.Contains("(scale {scale:F2})", open);
        Assert.Single(Regex.Matches(gates, @"_driveScale\.Value"));
    }

    [Fact]
    public void ADotAnimatorNodeTurnsBySlerpAndAClipNodeByComponentWiseEuler()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var apply = Between(gates, "static void ApplyPose(PosedPart posed, float progress)", "// Hold: back to the closed pose");
        Assert.Contains("float k = DoorPoseModel.EaseOf(n.Ease, n.Curve, progress);", apply);
        Assert.Contains("if (!n.WriteRotation) continue;", apply);
        Assert.Contains("n.Node.localRotation = k <= 0f ? n.ClosedRotation : k >= 1f ? n.OpenRotation : Quaternion.Slerp(n.ClosedRotation, n.OpenRotation, k);", apply);
        Assert.Contains("n.Node.localEulerAngles = new Vector3(ex, ey, ez);", apply);
        var note = Between(gates, "internal static void NotePoses(Transform part, ArenaDoorPose[] poses)", "internal static bool Park(Transform part)");
        Assert.Contains("bool slerp = DoorPoseModel.UsesSlerp(pose.Source);", note);
        Assert.Contains("WriteRotation = !slerp || DoorPoseModel.RotationMoves(pose.ClosedEuler, pose.OpenEuler)", note);
        Assert.Contains("ClosedRotation = Quaternion.Euler(ToVector(pose.ClosedEuler))", note);
        Assert.Contains("OpenRotation = Quaternion.Euler(ToVector(pose.OpenEuler))", note);
        Assert.Contains("DoorPoseModel.EaseIsUsable(pose.Ease, pose.Curve, out string easeProblem)", note);
        // a part is told whether it has poses without a public member
        Assert.Contains("internal static bool HasPoses(Transform part)", gates);
        Assert.DoesNotContain("public static bool HasPoses", gates);
    }

    [Fact]
    public void AReleaseNeverSwitchesOffAPartThatHasPoses()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var open = Between(gates, "static IEnumerator Open(string reason)", "static void OnSceneUnloaded(Scene scene)");
        Assert.Single(Regex.Matches(open, @"SetActive\(false\)"));
        Assert.Contains("part.gameObject.activeSelf && !_posed.ContainsKey(part)", open);
        Assert.Contains("ApplyPose(posed, DoorPoseModel.Progress(elapsed, posed.DriveSeconds));", open);
        Assert.Contains("if (!drive || DoorPoseModel.Finished(elapsed, posed.DriveSeconds))", open);
        Assert.Contains("(drive ? \"drove\" : \"snapped\")", open); // SpawnGateDrive = false still snaps
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
        // and only where someone can walk into it: a roller slab lifted into the lintel carves nothing
        int stands = carve.IndexOf("DoorPoseModel.ParkedBodyStands(b.max.y, posed.FloorY)", StringComparison.Ordinal);
        int band = carve.IndexOf("DoorPoseModel.OccupiesWalkingBand(b.min.y, b.max.y, posed.FloorY)", StringComparison.Ordinal);
        int make = carve.IndexOf("MakeCarver(\"Park carver \" + collider.name", StringComparison.Ordinal);
        Assert.True(stands > 0 && stands < band && band < make);
        Assert.Contains("walking band skipped", carve);
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

    [Theory]
    [InlineData(true, true, false)]    // the sealed probe: a part at its open pose is no closed gate
    [InlineData(true, false, true)]    // ... a closed one is judged
    [InlineData(false, true, true)]    // the probe after a release judges every part (a parked body on the segment is handled there)
    [InlineData(false, false, true)]
    public void TheSealedProbeJudgesOnlyClosedGateParts(bool expectSealed, bool parkedOrDriving, bool judged)
    {
        Assert.Equal(judged, DoorPoseModel.ProbeJudgesPart(expectSealed, parkedOrDriving));
    }

    [Fact]
    public void AParkedBusDoorwayIsNeverCountedByTheSealedProbe()
    {
        // The GPU-2 picture this guards (a plain AirPit raid): the passport leaves are held closed, so the NavMesh install runs
        // CreateCarvers + Probe(expectSealed: true); the three front bus assemblies are parked open and have their CLOSED bounds
        // in _bounds (Register). Judged, they were probed = 3, sealed = 0 in the BUS_DOORS enclosure: EnclosureContradicts(true, 3, 0)
        // and a false "a gate is NOT sealed" Warning.
        Assert.True(SpawnGateModel.EnclosureContradicts(true, 3, 0));
        // Skipped, the enclosure never reaches the tally and the passport enclosure is judged alone.
        Assert.False(SpawnGateModel.EnclosureContradicts(true, 0, 0));
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var probe = Between(gates, "static string ProbeOnce(bool expectSealed", "// The two probe points across a gate's thin axis");
        int bounds = probe.IndexOf("if (!_bounds.TryGetValue(part, out var bounds))", StringComparison.Ordinal);
        int parked = probe.IndexOf("DoorPoseModel.ProbeJudgesPart(expectSealed, IsParkedOrDriving(part))", StringComparison.Ordinal);
        int baseline = probe.IndexOf("_baseline.TryGetValue(part", StringComparison.Ordinal);
        int path = probe.IndexOf("ProbePath(bounds, out string detail)", StringComparison.Ordinal);
        int tally = probe.IndexOf("enclosures[enclosure] =", StringComparison.Ordinal);
        Assert.True(bounds > 0 && bounds < parked && parked < baseline && baseline < path && path < tally,
            "the parked check must come before the path probe and the enclosure tally");
        Assert.Contains("parkedOpen++; continue;", probe);
        Assert.Contains("part(s) parked at the open pose, not a closed gate, not judged", probe);
        // the part table is unchanged: a parked posed part still registers its closed bounds (the probe after a release reads them)
        Assert.Contains("_bounds[part] = posed.ClosedBounds;", Between(gates, "static void Register(Transform part)", "// ---- Arena's end poses"));
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
        var apply = Between(gates, "static void ApplyPose(PosedPart posed, float progress)", "// Hold: back to the closed pose");
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
        foreach (var path in new[] { open, Between(gates, "static void ApplyPose(PosedPart posed, float progress)", "// Hold: back to the closed pose") })
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
        Assert.DoesNotContain("public static bool HasPoses", gates);
    }

    // ---- AirPit (2026-10-24, docs/progress/door-poses.md "Runtime 3") --------------------------------------------------

    [Fact]
    public void ARepeatedPartNameTakesTheRowsOfItsOwnClosedPositionAndAnUnmatchedChildIsNeverLeftStanding()
    {
        var doors = Text("ArenaMapPort.Client/BoothDoorRepairs.cs");
        var barriers = Between(doors, "static void OpenBarriers(List<Transform> openings, StringBuilder sb, bool openPlain)", "// Is the way out clear?");
        // every row of the child's NAME, then the rows of THIS child, then "the name has rows but none is this child's"
        Assert.Contains("var named = map.DoorPosesOf(path, part.name, at.x, at.y, at.z);", barriers);
        Assert.Contains("var poses = SpawnGates.RowsForPart(part, named);", barriers);
        Assert.Contains("bool unmatched = named.Length > 0 && poses.Length == 0;", barriers);
        // an unmatched child is not structure: it goes through the offer / switch-off like a part without a pose, and NotePoses gets the
        // whole name so that its ONE Warning (PoseFallback) is logged, also on the second raid-start pass
        Assert.Contains("SpawnGates.NotePoses(part, unmatched ? named : poses);", barriers);
        int standing = barriers.IndexOf("if (poses.Length == 0 && keepStructure && !unmatched)", StringComparison.Ordinal);
        int note = barriers.IndexOf("SpawnGates.NotePoses(part, unmatched ? named : poses);", StringComparison.Ordinal);
        Assert.True(standing > 0 && standing < note);
        // the booth leaves keep their own lookup (assembly path + position): unchanged
        Assert.Contains("map.DoorPosesOf(HierarchyPath(assembly), leaf.name, at.x, at.y, at.z)", doors);

        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var rows = Between(gates, "internal static ArenaDoorPose[] RowsForPart(Transform part, ArenaDoorPose[] rows)", "internal static int SwitchCountOf(Transform part)");
        Assert.Contains("DoorPoseModel.SelectRowsForPart(rows, nodePath =>", rows);
        Assert.Contains("string.IsNullOrEmpty(nodePath) ? part : part.Find(nodePath)", rows);   // the node's own local position, "" = the part
        Assert.Contains("node.localPosition", rows);
        Assert.Contains("rows.Length < 2", rows);                                                // a name that does not repeat is taken as before
        // NotePoses narrows the rows itself too (the root pass and any other caller), before it reads a single row
        var note2 = Between(gates, "internal static void NotePoses(Transform part, ArenaDoorPose[] poses)", "internal static bool Park(Transform part)");
        int narrow = note2.IndexOf("poses = RowsForPart(part, poses);", StringComparison.Ordinal);
        int first = note2.IndexOf("var pose = poses[i];", StringComparison.Ordinal);
        Assert.True(narrow > 0 && narrow < first);
        Assert.Contains("if (poses.Length == 0)", note2);
        Assert.Contains("row(s) for this name has its closed position", note2);       // the one Warning, through PoseFallback
        Assert.Contains("PoseFallback(part,", note2);
    }

    [Fact]
    public void ASwitchRowIsCheckedAgainstTheScenesActiveStateAndSetOnlyAtTheEndsOfTheDrive()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var note = Between(gates, "internal static void NotePoses(Transform part, ArenaDoorPose[] poses)", "internal static bool Park(Transform part)");
        Assert.Contains("if (pose.IsSwitch)", note);
        Assert.Contains("node.gameObject.activeSelf", note);
        Assert.Contains("active != pose.ClosedActive.Value", note);                    // the identity check of a switch: the scene must agree with the closed state
        Assert.Contains("in the scene, the data's closed state is", note);
        Assert.Contains("a switch row names the part itself", note);                  // never a switch on the part (it would switch itself off)
        Assert.Contains("Switch = true, ClosedActive = pose.ClosedActive.Value, OpenActive = pose.OpenActive.Value", note);
        // a switch node is no moved body: only the transform rows give park-carver node paths
        Assert.Contains("paths.Add(path);", note);
        Assert.Single(Regex.Matches(note, @"paths\.Add\("));
        Assert.Contains("NodePaths = paths.ToArray()", note);
        Assert.Contains("Switches = switches, SwitchOnly = switches == nodes.Length", note);

        var apply = Between(gates, "static void ApplyPose(PosedPart posed, float progress)", "// Hold: back to the closed pose");
        int sw = apply.IndexOf("if (n.Switch)", StringComparison.Ordinal);
        int lerp = apply.IndexOf("DoorPoseModel.Lerp3(n.ClosedPosition, n.OpenPosition, k", StringComparison.Ordinal);
        Assert.True(sw > 0 && sw < lerp, "a switch node never reaches the transform code");
        Assert.Contains("DoorPoseModel.SwitchStateAt(k, n.ClosedActive, n.OpenActive)", apply);
        Assert.Contains("n.Node.gameObject.activeSelf != state.Value", apply);          // SetActive only on a change
        Assert.Contains("n.Node.gameObject.SetActive(state.Value);", apply);
        // Park applies progress 1 (the open state), Reclose progress 0 (the closed state): nothing else sets a switch
        var park = Between(gates, "internal static bool Park(Transform part)", "internal static void SealParked(string why)");
        Assert.Contains("ApplyPose(posed, 1f);", park);
        var reclose = Between(gates, "static void Reclose(PosedPart posed)", "static void DestroyParkCarvers(PosedPart posed)");
        Assert.Contains("ApplyPose(posed, 0f);", reclose);
        Assert.Single(Regex.Matches(gates, @"gameObject\.SetActive\(state\.Value\)"));
    }

    [Fact]
    public void APartWithOnlySwitchesFinishesWithTheLongestDriveOfItsBarrierAndCarvesNothing()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        var open = Between(gates, "static IEnumerator Open(string reason)", "static void OnSceneUnloaded(Scene scene)");
        // the drive seconds of the moving parts are fixed first, then the lamps take the longest of their siblings under the same root
        int scaled = open.IndexOf("posed.DriveSeconds = DoorPoseModel.ScaledSeconds(posed.Seconds, scale);", StringComparison.Ordinal);
        int lamps = open.IndexOf("posed.DriveSeconds = DoorPoseModel.SwitchPartDriveSeconds(siblings);", StringComparison.Ordinal);
        int clock = open.IndexOf("float t0 = Time.time;", StringComparison.Ordinal);
        Assert.True(scaled > 0 && scaled < lamps && lamps < clock);
        Assert.Contains("if (!posed.SwitchOnly || posed.Part == null) continue;", open);
        Assert.Contains("other.Part.parent == posed.Part.parent", open);
        Assert.Contains("!other.SwitchOnly", open);
        // the switch part is a driver like any other: it is finished by the same frame test as the leaves, and counted
        Assert.Contains("switched += posed.Switches;", open);
        Assert.Contains("switch(es) set to the open state", open);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(open, @"SetActive\(false\)"));   // the lamps are never switched off by the release
        var carve = Between(gates, "static void CreateParkCarvers(string why)", "static GameObject MakeCarver(");
        int only = carve.IndexOf("if (posed.SwitchOnly)", StringComparison.Ordinal);
        int walk = carve.IndexOf("foreach (var collider in posed.Part.GetComponentsInChildren<Collider>(true))", StringComparison.Ordinal);
        Assert.True(only > 0 && only < walk, "a switch-only part is skipped before any collider is looked at");
        Assert.Contains("no collider to carve", carve);
        Assert.Contains("switch(es) in the open state", carve);
    }

    [Fact]
    public void AHeldGateWithRowsIsRegisteredClosedAndDrivenOnlyByTheReleaseOfAHold()
    {
        var doors = Text("ArenaMapPort.Client/BoothDoorRepairs.cs");
        var barriers = Between(doors, "static void OpenBarriers(List<Transform> openings, StringBuilder sb, bool openPlain)", "// Is the way out clear?");
        // the poses are resolved BEFORE the held part is offered, so a Release can drive it
        int note = barriers.IndexOf("SpawnGates.NotePoses(part, unmatched ? named : poses);", StringComparison.Ordinal);
        int held = barriers.IndexOf("role == ArenaGateRole.Held && SpawnGates.OfferHeld(part)", StringComparison.Ordinal);
        Assert.True(note > 0 && note < held);
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        // OfferHeld registers (closed pose, held-gate set) and keeps the part active; nothing in it parks or drives
        var offerHeld = Between(gates, "internal static bool OfferHeld(Transform part)", "/// <summary>Once per raid-start opening, after every held part was offered");
        Assert.Contains("Register(part);", offerHeld);
        Assert.Contains("_heldGateParts.Add(part);", offerHeld);
        Assert.DoesNotContain("Park(", offerHeld);
        // Release opens only when a hold was outstanding; the Open coroutine then drives every registered, active, not yet parked part
        int from = Regex.Match(gates, @"public static void Release\(string reason\)\s*\r?\n").Index;
        int to = gates.IndexOf("internal static void Install()", StringComparison.Ordinal);
        var release = gates.Substring(from, to - from);
        Assert.Contains("bool held = IsHeld;", release);
        Assert.Contains("if (!held || _parts.Count == 0)", release);
        Assert.Contains("_heldGatesOpened = true;", release);
        Assert.Contains("_openCoroutine = _runner.StartCoroutine(Open(reason ?? \"no reason\"));", release);
        var open = Between(gates, "static IEnumerator Open(string reason)", "static void OnSceneUnloaded(Scene scene)");
        Assert.Contains("foreach (var part in _parts)", open);
        Assert.Contains("if (part == null || !part.gameObject.activeSelf) continue;", open);
        Assert.Contains("if (!posed.Parked)", open);
        Assert.Contains("posed.Driving = true;", open);
        // the developer hold (Spawns/SpawnGateHold) follows the same path: Watch() calls Release once SpawnGateHoldSeconds have passed
        Assert.Contains("Release($\"developer test hold, {sinceStart:F1} s after the raid timer started\");", gates);
        // the held-gate parts are carved from the NavMesh install on, and a driven part loses its hold carvers as it finishes
        Assert.Contains("DestroyHoldCarversOf(posed.Part)", open);
    }

    [Fact]
    public void AnAirPitBusRowAndTheStandingBackRowMeetTheKeepStructureRule()
    {
        var doors = Text("ArenaMapPort.Client/BoothDoorRepairs.cs");
        var barriers = Between(doors, "static void OpenBarriers(List<Transform> openings, StringBuilder sb, bool openPlain)", "// Is the way out clear?");
        // a barrier with rows keeps every child without a row of its name standing (AirPit's back row), and says so in its summary
        Assert.Contains("bool keepStructure = instancePoses.Length > 0 && !(rootPoses.Length > 0 && !rootDone);", barriers);
        Assert.Contains("part(s) without a pose left standing", barriers);
        Assert.Contains("parked > 0 ? $\", parked {parked} part(s) at the open pose\"", barriers);
        Assert.Contains("switches > 0 ? $\", {switches} switch(es)\"", barriers);
        // the summary counts the lamps through SpawnGates, once per offered, held or parked part
        Assert.Equal(3, Regex.Matches(barriers, @"switches \+= SpawnGates\.SwitchCountOf\(part\);").Count);
        // the descriptor side: the bus doors are the start gate (offered, parked in a plain raid), the passport control the held one
        var map = ArenaMapRegistry.AirPit;
        Assert.Equal(ArenaGateRole.Start, map.GateRoleOf("BUS_DOORS"));
        Assert.Equal(ArenaGateRole.Held, map.GateRoleOf(Passport));
        Assert.Equal(ArenaGateRole.Held, map.GateRoleOf("PASSPORT_CONTROL_EMISSIVE"));
        Assert.Contains("BUS_DOORS", map.OpenedBarriers);
        Assert.Contains(Passport, map.OpenedBarriers);
        // every direct child of the two barriers that has a row is a part the client registers; the rest of the map is untouched
        Assert.Empty(map.DoorPosesOfBarrier("PASSPORT_CONTROL_EMISSIVE"));
    }

    [Fact]
    public void TheOtherMapsRowsNeedNoPositionToTellTheirPartsApart()
    {
        // Equator, Bay5 and Bowl keep their lookups: (part, node, at) is unique per row, so SelectRowsForPart is the identity there
        foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5, ArenaMapRegistry.Bowl })
        {
            var keys = map.DoorPoses.Select(p => (p.Barrier, p.Part, p.Node, At: p.At == null ? "" : string.Join(",", p.At))).ToArray();
            Assert.Equal(keys.Length, keys.Distinct().Count());
            foreach (var group in map.DoorPoses.GroupBy(p => (p.Barrier, p.Part, At: p.At == null ? "" : string.Join(",", p.At))))
            {
                var rows = group.ToArray();
                Assert.Equal(rows, DoorPoseModel.SelectRowsForPart(rows, node => throw new InvalidOperationException("nothing to tell apart")));
            }
        }
    }
}
