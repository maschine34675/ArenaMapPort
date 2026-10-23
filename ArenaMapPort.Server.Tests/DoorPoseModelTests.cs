#nullable disable
using System;
using System.Collections.Generic;
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
    public void AirPitDrivesNothingYet()
    {
        // its passport and bus doors come in a later change set (docs/progress/door-animations-survey.md)
        Assert.Empty(ArenaMapRegistry.AirPit.DoorPoses);
        Assert.Empty(ArenaMapRegistry.AirPit.DoorPosesOf("BUS_DOORS", "Apron_bus_MAZ_doors"));
        Assert.Empty(ArenaMapRegistry.AirPit.DoorPosesOfBarrier("PASSPORT_CONTROL_DOORS"));
    }

    [Fact]
    public void EquatorHasTenBoothLeavesAndBay5EightGatePoses()
    {
        Assert.Equal(10, ArenaMapRegistry.Equator.DoorPoses.Length);
        Assert.Equal(8, ArenaMapRegistry.Bay5.DoorPoses.Length);
        Assert.Equal(19, ArenaMapRegistry.Bowl.DoorPoses.Length);
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
    [InlineData("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)", 0.092f, 3.181f, "borrowed")]
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
        var barriers = Between(code, "static void OpenBarriers(List<Transform> openings, StringBuilder sb, bool openPlain)", "// Is the way out clear?");
        int poses = barriers.IndexOf("var poses = map.DoorPosesOf(path, part.name, at.x, at.y, at.z);", StringComparison.Ordinal);
        int note = barriers.IndexOf("SpawnGates.NotePoses(part, poses);", StringComparison.Ordinal);
        int held = barriers.IndexOf("SpawnGates.OfferHeld(part)", StringComparison.Ordinal);
        int start = barriers.IndexOf("SpawnGates.Offer(part)", StringComparison.Ordinal);
        int park = barriers.IndexOf("SpawnGates.Park(part)", StringComparison.Ordinal);
        int off = barriers.IndexOf("part.gameObject.SetActive(false);", park, StringComparison.Ordinal);
        Assert.True(poses > 0 && poses < note && note < held && held < start && start < park && park < off, "DoorPosesOf, NotePoses, OfferHeld, Offer, Park, SetActive(false)");
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
        Assert.Contains("if (poses.Length == 0 && keepStructure)", barriers);
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
}
