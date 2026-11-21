#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The Unity-free model of the native grass (PLAN-YARD item 23, stage 2): the keys, the generic placement parser (Yard's, Bowl's and Iceberg's records) and the state machine the glue ticks. The descriptor
/// mirror is in <see cref="NativeGrassDescriptorTests"/>, the pack in <see cref="NativeGrassPackModelTests"/>, real data in <see cref="NativeGrassPackRealDataTests"/>; the glue is pinned as text in
/// <see cref="NativeGrassSourcePinTests"/>. Unit-tested only: nothing here is host-run.
/// </summary>
public sealed class NativeGrassModelTests
{
    const int Slices = 5; // the slice count of the pack's albedo array (the length of Arena's colour lists)

    // ---- the keys -----------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheKeysAreInTheMapSectionAndGrassDefaultsToOn()
    {
        Assert.Equal("Map (arena_yard)", NativeGrassKeys.SectionOf("arena_yard"));
        Assert.Equal("Map (arena_bowl)", NativeGrassKeys.SectionOf("arena_bowl"));
        Assert.Equal("Grass", NativeGrassKeys.Grass);
        Assert.True(NativeGrassKeys.GrassDefault);                  // user, 2026-11-21: on by default
        Assert.True(NativeGrassKeys.DrawingDefault);
        Assert.Equal("GrassDrawing", NativeGrassKeys.Drawing);
        Assert.Equal("GrassArt", NativeGrassKeys.ArtSource);
        Assert.Equal("Pack", NativeGrassKeys.ArtSourceDefault);
        Assert.Equal("GrassUpload", NativeGrassKeys.UploadPath);
        Assert.Equal("SetPixelData", NativeGrassKeys.UploadPathDefault);
        Assert.Equal(GrassUploadPath.SetPixelData, (GrassUploadPath)0);
        Assert.Equal(GrassArtKind.Pack, (GrassArtKind)0);
        var names = new[] { NativeGrassKeys.Grass, NativeGrassKeys.Drawing, NativeGrassKeys.ArtSource, NativeGrassKeys.UploadPath };
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void TheStage1KeysOfTheUnreleasedProbeAreNotTheNewKeys()
    {
        // a new key instead of a changed default (AGENTS.md hard rule on config keys): the four stage-1 names stay unbound, an orphan line in a developer's .cfg is harmless
        var current = new[] { NativeGrassKeys.Grass, NativeGrassKeys.Drawing, NativeGrassKeys.ArtSource, NativeGrassKeys.UploadPath };
        Assert.Equal(new[] { "NativeGrass", "NativeGrassDrawing", "NativeGrassArt", "NativeGrassUpload" }, NativeGrassKeys.RetiredStage1Keys);
        foreach (string old in NativeGrassKeys.RetiredStage1Keys) Assert.DoesNotContain(old, current);
    }

    // ---- the placement parser -----------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>One record: slice, then the 16 floats in file order (column-major). Translation floats 12..14, fourth row 3, 7, 11 = 0 and 15 = 1.</summary>
    static void Put(byte[] file, int record, int slice, float px, float py, float pz, float scale = 0.5f, float yaw = 0f)
    {
        int at = record * NativeGrassPlacement.RecordSize;
        BitConverter.GetBytes(slice).CopyTo(file, at);
        float c = (float)Math.Cos(yaw), s = (float)Math.Sin(yaw);
        float[] m =
        {
            c * scale, 0f, -s * scale, 0f,     // first column: m00, m10, m20, m30
            0f, scale, 0f, 0f,                 // second column
            s * scale, 0f, c * scale, 0f,      // third column
            px, py, pz, 1f,                    // translation column
        };
        for (int k = 0; k < 16; k++) BitConverter.GetBytes(m[k]).CopyTo(file, at + 4 + 4 * k);
    }

    static byte[] Records(int count, Action<byte[], int> each = null)
    {
        var file = new byte[count * NativeGrassPlacement.RecordSize];
        for (int i = 0; i < count; i++)
        {
            Put(file, i, i % Slices, -30f + i % 20, 6.1f, 40f + i % 50);
            each?.Invoke(file, i);
        }
        return file;
    }

    [Fact]
    public void ARecordIs68BytesAndTheMatrixIsStoredColumnMajorWithTheTranslationLast()
    {
        Assert.Equal(68, NativeGrassPlacement.RecordSize);
        Assert.Equal(4 + 16 * 4, NativeGrassPlacement.RecordSize);
        Assert.Equal(0, NativeGrassPlacement.ElementIndex(0, 0));
        Assert.Equal(1, NativeGrassPlacement.ElementIndex(1, 0));     // m10 follows m00: column-major
        Assert.Equal(4, NativeGrassPlacement.ElementIndex(0, 1));     // m01
        Assert.Equal(12, NativeGrassPlacement.ElementIndex(0, 3));    // m03 = translation x
        Assert.Equal(13, NativeGrassPlacement.ElementIndex(1, 3));
        Assert.Equal(14, NativeGrassPlacement.ElementIndex(2, 3));
        Assert.Equal(15, NativeGrassPlacement.ElementIndex(3, 3));
    }

    [Fact]
    public void AValidFileParsesToTheSameNumbersInTheSameOrder()
    {
        byte[] file = Records(9000, (f, i) => { if (i == 7) Put(f, i, 3, -12.5f, 6.25f, 99.75f, 0.75f, 1.0f); });
        GrassParseResult result = NativeGrassPlacement.Parse(file, file.Length, Slices);
        Assert.True(result.Ok, result.Error.ToString());
        Assert.Equal(-1, result.ErrorIndex);
        GrassPlan plan = result.Plan;
        Assert.Equal(9000, plan.Count);
        Assert.Equal(9000, plan.Slices.Length);
        Assert.Equal(9000 * 16, plan.Matrices.Length);
        Assert.Equal(3, plan.Slices[7]);
        Assert.Equal(-12.5f, plan.Element(7, 0, 3));
        Assert.Equal(6.25f, plan.Element(7, 1, 3));
        Assert.Equal(99.75f, plan.Element(7, 2, 3));
        Assert.Equal(1f, plan.Element(7, 3, 3));
        Assert.Equal(0.75f * (float)Math.Cos(1.0f), plan.Element(7, 0, 0), 5);
        Assert.Equal(0.75f, plan.Element(7, 1, 1), 5);
        Assert.Equal(plan.Count, plan.SliceHistogram.Sum());
        Assert.Equal(Slices, plan.SliceHistogram.Length);
    }

    [Fact]
    public void TheHistogramCountsEverySliceAndPrintsSlashSeparated()
    {
        byte[] file = Records(9000, (f, i) => Put(f, i, i < 8 ? 1 : (i < 8100 ? 2 : (i < 9000 ? 3 : 4)), -30f, 6.1f, 40f));
        GrassPlan plan = NativeGrassPlacement.Parse(file, file.Length, Slices).Plan;
        Assert.Equal(new[] { 0, 8, 8092, 900, 0 }, plan.SliceHistogram);
        Assert.Equal("0/8/8092/900/0", plan.HistogramText());
    }

    [Fact]
    public void TheExtentsAreTheRecordsOwn()
    {
        byte[] file = Records(9000, (f, i) =>
        {
            if (i == 0) Put(f, i, 1, -66.5f, 5.9f, 17.5f, 0.2f);
            if (i == 1) Put(f, i, 1, 9.0f, 6.3f, 117.5f, 1.1f);
        });
        GrassPlan plan = NativeGrassPlacement.Parse(file, file.Length, Slices).Plan;
        Assert.Equal(-66.5f, plan.MinX);
        Assert.Equal(9.0f, plan.MaxX);
        Assert.Equal(5.9f, plan.MinY);
        Assert.Equal(6.3f, plan.MaxY);
        Assert.Equal(17.5f, plan.MinZ);
        Assert.Equal(117.5f, plan.MaxZ);
        Assert.Equal(0.2f, plan.MinScale, 4);
        Assert.Equal(1.1f, plan.MaxScale, 4);
    }

    [Fact]
    public void ANullBufferIsRefusedAsNoData()
    {
        Assert.Equal(GrassPlacementError.NoData, NativeGrassPlacement.Parse(null, 0, Slices).Error);
    }

    [Fact]
    public void AnEmptyFileIsRefused()
    {
        GrassParseResult r = NativeGrassPlacement.Parse(new byte[0], 0, Slices);
        Assert.False(r.Ok);
        Assert.Equal(GrassPlacementError.Empty, r.Error);
        Assert.Null(r.Plan);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(67)]
    [InlineData(69)]
    public void ALengthThatIsNotAMultipleOfTheRecordSizeIsRefused(int extra)
    {
        byte[] file = Records(9000);
        var longer = new byte[file.Length + extra];
        Array.Copy(file, longer, file.Length);
        Assert.Equal(extra % 68 == 0 ? GrassPlacementError.None : GrassPlacementError.LengthNotDivisible, NativeGrassPlacement.Parse(longer, longer.Length, Slices).Error);
    }

    [Fact]
    public void ALengthBelowTheBufferIsHonoured()
    {
        byte[] file = Records(9000);
        var padded = new byte[file.Length + 40];
        Array.Copy(file, padded, file.Length);
        Assert.True(NativeGrassPlacement.Parse(padded, file.Length, Slices).Ok);
        Assert.Equal(GrassPlacementError.LengthNotDivisible, NativeGrassPlacement.Parse(padded, padded.Length, Slices).Error);
    }

    [Fact]
    public void TooManyRecordsAreRefused()
    {
        var file = new byte[(NativeGrassPlacement.AbsoluteMaxRecords + 1) * NativeGrassPlacement.RecordSize];
        Assert.Equal(GrassPlacementError.TooManyRecords, NativeGrassPlacement.Parse(file, file.Length, Slices).Error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7999)]
    [InlineData(12501)]
    [InlineData(113643)]    // Bowl's count; no per-map count range exists any more
    public void AnyCountFromOneRecordUpToTheAbsoluteMaximumParses(int count)
    {
        byte[] file = Records(count);
        GrassParseResult r = NativeGrassPlacement.Parse(file, file.Length, Slices);
        Assert.True(r.Ok, r.Error.ToString());
        Assert.Equal(count, r.Plan.Count);
    }

    [Fact]
    public void ASliceCountBelowOneIsAProgrammingError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGrassPlacement.Parse(new byte[68], 68, 0));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ANonFiniteMatrixElementIsRefusedWithItsRecord(float bad)
    {
        foreach (int element in new[] { 0, 5, 12, 14 })
        {
            byte[] file = Records(9000, (f, i) =>
            {
                if (i == 4321) BitConverter.GetBytes(bad).CopyTo(f, i * 68 + 4 + 4 * element);
            });
            GrassParseResult r = NativeGrassPlacement.Parse(file, file.Length, Slices);
            Assert.Equal(GrassPlacementError.NonFiniteMatrix, r.Error);
            Assert.Equal(4321, r.ErrorIndex);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ASliceOutsideTheSliceCountIsRefused(int slice)
    {
        byte[] file = Records(9000, (f, i) => { if (i == 100) BitConverter.GetBytes(slice).CopyTo(f, i * 68); });
        GrassParseResult r = NativeGrassPlacement.Parse(file, file.Length, Slices);
        Assert.Equal(GrassPlacementError.SliceOutOfRange, r.Error);
        Assert.Equal(100, r.ErrorIndex);
    }

    [Theory]
    [InlineData(-20001f, 6.1f, 40f)]
    [InlineData(20001f, 6.1f, 40f)]
    [InlineData(-30f, -20001f, 40f)]
    [InlineData(-30f, 6.1f, 1e9f)]
    public void AnAbsurdCoordinateIsRefused(float x, float y, float z)
    {
        byte[] file = Records(9000, (f, i) => { if (i == 55) Put(f, i, 2, x, y, z); });
        GrassParseResult r = NativeGrassPlacement.Parse(file, file.Length, Slices);
        Assert.Equal(GrassPlacementError.OutsideBounds, r.Error);
        Assert.Equal(55, r.ErrorIndex);
    }

    [Theory]
    [InlineData(-66.95f, 5.9f, 117.81f)]      // Yard's own corners
    [InlineData(300f, -50f, -400f)]           // another map's extent: no map box is pinned any more
    [InlineData(20000f, 20000f, -20000f)]
    public void AnyPlausibleCoordinateIsInside(float x, float y, float z)
    {
        byte[] file = Records(9000, (f, i) => { if (i == 3) Put(f, i, 2, x, y, z); });
        Assert.True(NativeGrassPlacement.Parse(file, file.Length, Slices).Ok);
    }

    [Theory]
    [InlineData(0.005f)]
    [InlineData(60f)]
    public void AScaleOutsideTheGenericRangeIsRefused(float scale)
    {
        byte[] file = Records(9000, (f, i) => { if (i == 9) Put(f, i, 2, -30f, 6.1f, 40f, scale); });
        GrassParseResult r = NativeGrassPlacement.Parse(file, file.Length, Slices);
        Assert.Equal(GrassPlacementError.ScaleOutOfRange, r.Error);
        Assert.Equal(9, r.ErrorIndex);
    }

    [Theory]
    [InlineData(0.184f)]    // Yard's smallest
    [InlineData(1.124f)]    // Yard's largest
    [InlineData(2.24f)]     // Iceberg's largest (one axis only, see the tilted test)
    [InlineData(0.011f)]
    [InlineData(49f)]
    public void EveryMeasuredScaleIsAccepted(float scale)
    {
        byte[] file = Records(9000, (f, i) => { if (i == 9) Put(f, i, 2, -30f, 6.1f, 40f, scale); });
        Assert.True(NativeGrassPlacement.Parse(file, file.Length, Slices).Ok);
    }

    /// <summary>A record rotated about X by <paramref name="tiltDegrees"/> and scaled per axis (sx, sy, sz): Iceberg's tilted, non-uniformly scaled clumps.</summary>
    static void PutTilted(byte[] file, int record, int slice, float tiltDegrees, float sx, float sy, float sz, float px = 5f, float py = 3f, float pz = -80f)
    {
        int at = record * NativeGrassPlacement.RecordSize;
        BitConverter.GetBytes(slice).CopyTo(file, at);
        float c = (float)Math.Cos(tiltDegrees * Math.PI / 180.0), s = (float)Math.Sin(tiltDegrees * Math.PI / 180.0);
        float[] m =
        {
            sx, 0f, 0f, 0f,                    // first column: the X axis, scaled
            0f, c * sy, s * sy, 0f,            // second column: the up axis, tilted about X and scaled
            0f, -s * sz, c * sz, 0f,           // third column
            px, py, pz, 1f,
        };
        for (int k = 0; k < 16; k++) BitConverter.GetBytes(m[k]).CopyTo(file, at + 4 + 4 * k);
    }

    [Fact]
    public void ATiltedAndNonUniformlyScaledRecordPassesAndTheTiltAndScalesAreMeasured()
    {
        // Iceberg's records: tilts up to 50 degrees and a scale up to 2.24 on one axis (G2 section 7)
        byte[] file = Records(9000, (f, i) => { if (i == 123) PutTilted(f, i, 2, 50f, 1f, 2.24f, 1f); });
        GrassParseResult r = NativeGrassPlacement.Parse(file, file.Length, Slices);
        Assert.True(r.Ok, r.Error.ToString());
        Assert.Equal(50f, r.Plan.MaxTiltDegrees, 1);
        Assert.Equal(2.24f, r.Plan.MaxScale, 3);
        Assert.Equal(0.5f, r.Plan.MinScale, 3);                // the other records are uniform 0.5
        Assert.Equal(2.24f * (float)Math.Cos(50 * Math.PI / 180.0), r.Plan.Element(123, 1, 1), 4);
    }

    [Fact]
    public void AYawOnlyFileHasNoTiltAndASmallTiltIsMeasuredToo()
    {
        byte[] flat = Records(100);
        Assert.Equal(0f, NativeGrassPlacement.Parse(flat, flat.Length, Slices).Plan.MaxTiltDegrees, 2);
        byte[] bowl = Records(100, (f, i) => { if (i == 4) PutTilted(f, i, 1, 2.6f, 0.9f, 0.9f, 0.9f); });      // Bowl's largest tilt
        Assert.Equal(2.6f, NativeGrassPlacement.Parse(bowl, bowl.Length, Slices).Plan.MaxTiltDegrees, 1);
    }

    [Fact]
    public void ABrokenTiltedRecordIsStillRefused()
    {
        byte[] nan = Records(200, (f, i) => { if (i == 77) { PutTilted(f, i, 2, 50f, 1f, 2.24f, 1f); BitConverter.GetBytes(float.NaN).CopyTo(f, i * 68 + 4 + 4 * 6); } });
        GrassParseResult r = NativeGrassPlacement.Parse(nan, nan.Length, Slices);
        Assert.Equal(GrassPlacementError.NonFiniteMatrix, r.Error);
        Assert.Equal(77, r.ErrorIndex);
        byte[] projective = Records(200, (f, i) => { if (i == 78) { PutTilted(f, i, 2, 50f, 1f, 2.24f, 1f); BitConverter.GetBytes(0.25f).CopyTo(f, i * 68 + 4 + 4 * 7); } });
        Assert.Equal(GrassPlacementError.NotAffine, NativeGrassPlacement.Parse(projective, projective.Length, Slices).Error);
        byte[] badSlice = Records(200, (f, i) => { if (i == 79) { PutTilted(f, i, 5, 50f, 1f, 2.24f, 1f); } });
        Assert.Equal(GrassPlacementError.SliceOutOfRange, NativeGrassPlacement.Parse(badSlice, badSlice.Length, Slices).Error);
        byte[] collapsed = Records(200, (f, i) => { if (i == 80) { PutTilted(f, i, 2, 50f, 1f, 0f, 1f); } });
        Assert.Equal(GrassPlacementError.ScaleOutOfRange, NativeGrassPlacement.Parse(collapsed, collapsed.Length, Slices).Error);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    public void AMatrixWithAProjectiveBottomRowIsRefused(int element)
    {
        byte[] file = Records(9000, (f, i) => { if (i == 12) BitConverter.GetBytes(0.5f).CopyTo(f, i * 68 + 4 + 4 * element); });
        GrassParseResult r = NativeGrassPlacement.Parse(file, file.Length, Slices);
        Assert.Equal(GrassPlacementError.NotAffine, r.Error);
        Assert.Equal(12, r.ErrorIndex);
    }

    [Fact]
    public void AMatrixWhoseCornerIsNotOneIsRefused()
    {
        byte[] file = Records(9000, (f, i) => { if (i == 12) BitConverter.GetBytes(2f).CopyTo(f, i * 68 + 4 + 4 * 15); });
        Assert.Equal(GrassPlacementError.NotAffine, NativeGrassPlacement.Parse(file, file.Length, Slices).Error);
    }

    [Fact]
    public void TheErrorTextNamesTheCodeAndTheRecordAndNeverContent()
    {
        byte[] file = Records(9000, (f, i) => { if (i == 77) BitConverter.GetBytes(9).CopyTo(f, i * 68); });
        GrassParseResult r = NativeGrassPlacement.Parse(file, file.Length, Slices);
        Assert.Equal("placement 612000 B: SliceOutOfRange at record 77", NativeGrassPlacement.Describe(r, file.Length));
        GrassParseResult ok = NativeGrassPlacement.Parse(Records(9000), 9000 * 68, Slices);
        Assert.Equal("placement 612000 B: 9000 records", NativeGrassPlacement.Describe(ok, 612000));
    }

    // ---- the state machine --------------------------------------------------------------------------------------------------------------------------------------------------

    static GrassTickInputs Ready(int serial = 1) => new GrassTickInputs
    {
        Wanted = true,
        HasGrassMap = true,
        InOwnedRaid = true,
        ScenesLoaded = true,
        CameraReady = true,
        OpticCameraReady = true,
        CameraSerial = serial,
        GameStopping = false,
        InputsReady = true,
        InputError = null,
        BuildSignature = 7,
    };

    /// <summary>Ticks until the machine hands out an action other than None (at most 200 ticks), and returns it.</summary>
    static GrassAction Run(NativeGrassState s, GrassTickInputs inputs, int max = 200)
    {
        for (int i = 0; i < max; i++)
        {
            GrassAction a = s.Tick(inputs);
            if (a != GrassAction.None) return a;
        }
        return GrassAction.None;
    }

    static NativeGrassState Built(GrassTickInputs inputs)
    {
        var s = new NativeGrassState();
        Assert.Equal(GrassAction.Build, Run(s, inputs));
        s.BuildSucceeded();
        Assert.Equal(GrassPhase.Built, s.Phase);
        return s;
    }

    [Fact]
    public void NothingWantedStaysIdleAndCostsNoAction()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.Wanted = false;
        for (int n = 0; n < 100; n++) Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.Equal(GrassPhase.Idle, s.Phase);
        Assert.Equal(GrassWait.NotWanted, s.WaitReason);
    }

    [Fact]
    public void AMapWithoutGrassOrOutsideARaidStaysIdle()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.HasGrassMap = false;
        Assert.Equal(GrassAction.None, Run(s, i, 50));
        Assert.Equal(GrassWait.NoGrassMap, s.WaitReason);
        Assert.Equal(GrassPhase.Idle, s.Phase);
        i = Ready();
        i.InOwnedRaid = false;
        Assert.Equal(GrassAction.None, Run(s, i, 50));
        Assert.Equal(GrassWait.NoRaid, s.WaitReason);
        Assert.Equal(GrassPhase.Idle, s.Phase);
    }

    [Fact]
    public void TheLadderNamesTheFirstMissingPrecondition()
    {
        GrassTickInputs i = Ready();
        i.ScenesLoaded = false; i.CameraReady = false; i.OpticCameraReady = false; i.GameStopping = true; i.InputsReady = false;
        var s = new NativeGrassState();
        Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.Equal(GrassWait.ScenesNotLoaded, s.WaitReason);
        Assert.Equal(GrassPhase.Waiting, s.Phase);
        i.ScenesLoaded = true;
        s.Tick(i);
        Assert.Equal(GrassWait.NoCamera, s.WaitReason);
        i.CameraReady = true;
        s.Tick(i);
        Assert.Equal(GrassWait.NoOpticCamera, s.WaitReason);
        i.OpticCameraReady = true;
        s.Tick(i);
        Assert.Equal(GrassWait.GameStopping, s.WaitReason);
        i.GameStopping = false;
        s.Tick(i);
        Assert.Equal(GrassWait.GameStopping, s.WaitReason); // a stop seen with the raid fully up ends the raid for the grass
        s.NewRaid();
        s.Tick(i);
        Assert.Equal(GrassWait.FramesPending, s.WaitReason);
        Assert.Equal(GrassAction.None, Run(s, i, 200));
        Assert.Equal(GrassWait.InputsPending, s.WaitReason);
        i.InputsReady = true;
        Assert.Equal(GrassAction.Build, s.Tick(i));
    }

    [Fact]
    public void AStoppedGameEndsTheRaidForTheGrassEvenWhenTheFlagReadsFalseAgain()
    {
        // GPU-2, 2026-11-21: stop, teardown, and 1.7 s later (the game singleton released, scenes and cameras still up) a second build that the scene unload tore down 1.4 s after it
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        Assert.Equal(GrassAction.Build, Run(s, i));
        s.BuildSucceeded();
        i.GameStopping = true;
        Assert.Equal(GrassAction.Teardown, s.Tick(i));
        Assert.Equal(GrassTeardownReason.GameStopping, s.TeardownReason);
        s.TeardownDone();
        Assert.True(s.RaidOver);
        i.GameStopping = false;
        Assert.Equal(GrassAction.None, Run(s, i, 300));
        Assert.Equal(GrassWait.GameStopping, s.WaitReason);
        Assert.True(s.RaidOver);
        s.NewRaid();
        Assert.False(s.RaidOver);
        Assert.Equal(GrassAction.Build, Run(s, i));
    }

    [Fact]
    public void AStaleStoppingFlagWhileTheRaidIsLoadingDoesNotEndIt()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.GameStopping = true; i.ScenesLoaded = false;
        s.Tick(i);
        Assert.False(s.RaidOver);
        i.ScenesLoaded = true; i.CameraReady = false;
        s.Tick(i);
        Assert.False(s.RaidOver);
        i.CameraReady = true; i.OpticCameraReady = false;
        s.Tick(i);
        Assert.False(s.RaidOver);
        i.OpticCameraReady = true; i.GameStopping = false;
        Assert.Equal(GrassAction.Build, Run(s, i));
    }

    [Fact]
    public void ATickOutsideARaidClearsTheStoppedLatch()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.GameStopping = true;
        s.Tick(i);
        Assert.True(s.RaidOver);
        i.GameStopping = false; i.InOwnedRaid = false;
        s.Tick(i);
        Assert.False(s.RaidOver);
        i.InOwnedRaid = true;
        Assert.Equal(GrassAction.Build, Run(s, i));
    }

    [Fact]
    public void ABuildNeedsThirtyTicksOfReadyCamerasAndNeverEarlier()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        for (int n = 1; n < NativeGrassState.ReadyFramesRequired; n++) Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.Equal(GrassAction.Build, s.Tick(i));
        Assert.Equal(30, NativeGrassState.ReadyFramesRequired);
    }

    [Fact]
    public void TheReadyFramesRestartWhenACameraIsMissingForATickOrIsReplaced()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        for (int n = 0; n < 20; n++) s.Tick(i);
        i.OpticCameraReady = false;
        s.Tick(i);
        Assert.Equal(0, s.ReadyFrames);
        i.OpticCameraReady = true;
        for (int n = 1; n < 30; n++) Assert.Equal(GrassAction.None, s.Tick(i));
        i.CameraSerial = 2;                      // another camera instance: the count starts again
        Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.Equal(1, s.ReadyFrames);
        for (int n = 2; n < 30; n++) Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.Equal(GrassAction.Build, s.Tick(i));
    }

    [Fact]
    public void WaitingForTheInputsDoesNotBlockTheCountAndTheBuildStartsTheTickTheyArrive()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.InputsReady = false;
        for (int n = 0; n < 100; n++) Assert.Equal(GrassAction.None, s.Tick(i));
        i.InputsReady = true;
        Assert.Equal(GrassAction.Build, s.Tick(i));
    }

    [Fact]
    public void BuildIsHandedOutOnceAndTheMachineAnswersNothingWhileItBuilds()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        Assert.Equal(GrassAction.Build, Run(s, i));
        Assert.Equal(GrassPhase.Building, s.Phase);
        for (int n = 0; n < 10; n++) Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.Equal(GrassPhase.Building, s.Phase);
        s.BuildSucceeded();
        Assert.Equal(GrassPhase.Built, s.Phase);
        Assert.True(s.IsBuilt);
        for (int n = 0; n < 100; n++) Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.Equal(GrassPhase.Built, s.Phase);
    }

    [Fact]
    public void BuildSucceededOutsideABuildDoesNothing()
    {
        var s = new NativeGrassState();
        s.BuildSucceeded();
        Assert.Equal(GrassPhase.Idle, s.Phase);
    }

    public static IEnumerable<object[]> TeardownCases()
    {
        yield return new object[] { "Wanted", "KeyOff" };
        yield return new object[] { "InOwnedRaid", "RaidEnded" };
        yield return new object[] { "HasGrassMap", "RaidEnded" };
        yield return new object[] { "ScenesLoaded", "ScenesUnloaded" };
        yield return new object[] { "CameraReady", "CameraLost" };
        yield return new object[] { "OpticCameraReady", "CameraLost" };
        yield return new object[] { "CameraSerial", "CameraReplaced" };
        yield return new object[] { "GameStopping", "GameStopping" };
        yield return new object[] { "BuildSignature", "ConfigChanged" };
    }

    static GrassTickInputs Break(GrassTickInputs i, string what)
    {
        switch (what)
        {
            case "Wanted": i.Wanted = false; break;
            case "InOwnedRaid": i.InOwnedRaid = false; break;
            case "HasGrassMap": i.HasGrassMap = false; break;
            case "ScenesLoaded": i.ScenesLoaded = false; break;
            case "CameraReady": i.CameraReady = false; break;
            case "OpticCameraReady": i.OpticCameraReady = false; break;
            case "CameraSerial": i.CameraSerial = 99; break;
            case "GameStopping": i.GameStopping = true; break;
            case "BuildSignature": i.BuildSignature = 8; break;
            default: throw new ArgumentException(what);
        }
        return i;
    }

    [Theory]
    [MemberData(nameof(TeardownCases))]
    public void ABuiltSetIsTornDownWithTheRightReason(string what, string reasonName)
    {
        NativeGrassState s = Built(Ready());
        Assert.Equal(GrassAction.Teardown, s.Tick(Break(Ready(), what)));
        Assert.Equal(Enum.Parse<GrassTeardownReason>(reasonName), s.TeardownReason);
        Assert.Equal(GrassPhase.TearingDown, s.Phase);
        Assert.False(s.Failed);
    }

    [Fact]
    public void TheTeardownReasonsAreOrderedKeyThenRaidThenScenesThenCamera()
    {
        NativeGrassState s = Built(Ready());
        GrassTickInputs i = Ready();
        i.Wanted = false; i.InOwnedRaid = false; i.CameraReady = false;
        Assert.Equal(GrassAction.Teardown, s.Tick(i));
        Assert.Equal(GrassTeardownReason.KeyOff, s.TeardownReason);
    }

    [Fact]
    public void ATeardownIsHandedOutOnceAndNoBuildFollowsInTheNextTickOrTheOneAfterTheAnswer()
    {
        NativeGrassState s = Built(Ready());
        Assert.Equal(GrassAction.Teardown, s.Tick(Break(Ready(), "Wanted")));
        for (int n = 0; n < 5; n++) Assert.Equal(GrassAction.None, s.Tick(Ready()));       // unanswered: never a second Teardown, never a Build
        Assert.Equal(GrassPhase.TearingDown, s.Phase);
        s.TeardownDone();
        Assert.Equal(GrassPhase.Idle, s.Phase);
        // the one-frame gap: even with everything ready again, the next tick is empty
        Assert.Equal(GrassAction.None, s.Tick(Ready()));
        Assert.Equal(GrassWait.GapAfterTeardown, s.WaitReason);
        Assert.Equal(GrassAction.Build, s.Tick(Ready()));
    }

    [Fact]
    public void TheKeyToggledOffAndOnRebuildsOnTheCurrentCameraWithAGapInBetween()
    {
        NativeGrassState s = Built(Ready());
        Assert.Equal(GrassAction.Teardown, s.Tick(Break(Ready(), "Wanted")));
        s.TeardownDone();
        GrassTickInputs on = Ready();
        var actions = new List<GrassAction>();
        for (int n = 0; n < 3; n++) actions.Add(s.Tick(on));
        Assert.Equal(new[] { GrassAction.None, GrassAction.Build, GrassAction.None }, actions);
        s.BuildSucceeded();
        Assert.Equal(GrassPhase.Built, s.Phase);
    }

    [Fact]
    public void AnOffTickAlwaysReturnsTeardownBeforeAnOnTickCanReturnBuild()
    {
        // ten live toggles: never a Build in the tick of a Teardown, never two builds without a teardown between
        NativeGrassState s = Built(Ready());
        int builds = 0, teardowns = 0;
        for (int toggle = 0; toggle < 10; toggle++)
        {
            GrassAction a = s.Tick(Break(Ready(), "Wanted"));
            Assert.Equal(GrassAction.Teardown, a);
            teardowns++;
            s.TeardownDone();
            GrassAction last = GrassAction.None;
            for (int n = 0; n < 5 && last != GrassAction.Build; n++) last = s.Tick(Ready());
            Assert.Equal(GrassAction.Build, last);
            builds++;
            s.BuildSucceeded();
        }
        Assert.Equal(10, teardowns);
        Assert.Equal(10, builds);
    }

    [Fact]
    public void ACameraReplacedMidRaidTearsDownAndRebuildsOnlyAfterTheFramesAgain()
    {
        NativeGrassState s = Built(Ready(1));
        Assert.Equal(GrassAction.Teardown, s.Tick(Ready(2)));
        Assert.Equal(GrassTeardownReason.CameraReplaced, s.TeardownReason);
        s.TeardownDone();
        GrassAction a = GrassAction.None;
        int ticks = 0;
        while (a == GrassAction.None && ticks < 100) { a = s.Tick(Ready(2)); ticks++; }
        Assert.Equal(GrassAction.Build, a);
        Assert.True(ticks >= NativeGrassState.ReadyFramesRequired - 1, "ticks " + ticks);
    }

    [Fact]
    public void ARaidEndTearsDownAndTheNextRaidRebuildsOnce()
    {
        NativeGrassState s = Built(Ready());
        GrassTickInputs ended = Ready();
        ended.InOwnedRaid = false; ended.ScenesLoaded = false; ended.CameraReady = false; ended.OpticCameraReady = false;
        Assert.Equal(GrassAction.Teardown, s.Tick(ended));
        s.TeardownDone();
        for (int n = 0; n < 20; n++) Assert.Equal(GrassAction.None, s.Tick(ended));
        Assert.Equal(GrassPhase.Idle, s.Phase);
        Assert.Equal(GrassAction.Build, Run(s, Ready(3)));
    }

    [Fact]
    public void AFailedBuildLatchesOwesATeardownAndNeverBuildsAgain()
    {
        var s = new NativeGrassState();
        Assert.Equal(GrassAction.Build, Run(s, Ready()));
        s.Fail("GPUInstancerPrefabManager.Awake: NullReferenceException");
        Assert.Equal(GrassPhase.Failed, s.Phase);
        Assert.True(s.Failed);
        Assert.Equal("GPUInstancerPrefabManager.Awake: NullReferenceException", s.FailureReason);
        Assert.Equal(GrassAction.Teardown, s.Tick(Ready()));
        Assert.Equal(GrassTeardownReason.Failure, s.TeardownReason);
        Assert.Equal(GrassAction.None, s.Tick(Ready()));                   // unanswered
        s.TeardownDone();
        Assert.Equal(GrassPhase.Failed, s.Phase);
        for (int n = 0; n < 200; n++) Assert.Equal(GrassAction.None, s.Tick(Ready()));
        // a key flip does not clear the latch (sticky for the session)
        Assert.Equal(GrassAction.None, s.Tick(Break(Ready(), "Wanted")));
        Assert.Equal(GrassAction.None, Run(s, Ready()));
        Assert.True(s.Failed);
        Assert.Equal(GrassWait.Latched, s.WaitReason);
    }

    [Fact]
    public void AFailureOfABuiltSetTearsItDownToo()
    {
        NativeGrassState s = Built(Ready());
        s.Fail("camera lost its command buffer");
        Assert.Equal(GrassAction.Teardown, s.Tick(Ready()));
        s.TeardownDone();
        Assert.Equal(GrassPhase.Failed, s.Phase);
        Assert.False(s.IsBuilt);
    }

    [Fact]
    public void AnInputRefusalIsTheMapsAndNeverASessionLatch()
    {
        // decision 6 of stage 2: a map whose pack is missing, refused or damaged has no grass, and a failure on one map must not switch the grass off on another map in the same session
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.InputError = "container:HeaderAuth";
        Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.False(s.Failed);                                              // no latch, no Warning due from the state machine
        Assert.Equal(GrassWait.InputsRefused, s.WaitReason);
        Assert.Equal(GrassPhase.Waiting, s.Phase);
        Assert.False(s.TakeWarningDue(out _));
        // the glue records the map as refused and the tick finds it not wanted
        i.Wanted = false;
        Assert.Equal(GrassAction.None, s.Tick(i));
        Assert.Equal(GrassPhase.Idle, s.Phase);
        // another map in the same session builds with the same machine
        Assert.Equal(GrassAction.Build, Run(s, Ready(5)));
        Assert.False(s.Failed);
    }

    [Fact]
    public void ARefusedMapOwesNoTeardownAndAnEngineFailureStillLatchesTheSession()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.InputError = "placement:NotAffine@12";
        s.Tick(i);
        s.Tick(i);
        Assert.False(s.Failed);
        Assert.Equal(GrassAction.None, s.Tick(i));                           // nothing owed
        s.Fail("build: NullReferenceException");                              // the engine-side failure keeps its session latch
        Assert.True(s.Failed);
        Assert.Equal(GrassPhase.Failed, s.Phase);
    }

    [Fact]
    public void AnInputErrorIsIgnoredWhileNothingIsWanted()
    {
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.Wanted = false;
        i.InputError = "ShortRead";
        s.Tick(i);
        Assert.False(s.Failed);
    }

    [Fact]
    public void TheFirstFailureOfTheSessionAsksForOneWarningAndNoMore()
    {
        var s = new NativeGrassState();
        Assert.False(s.TakeWarningDue(out _));
        Run(s, Ready());
        s.Fail("first");
        Assert.True(s.TakeWarningDue(out string reason));
        Assert.Equal("first", reason);
        Assert.False(s.TakeWarningDue(out _));
        s.Fail("second");                                                  // a repeated failure keeps the first reason and asks for nothing new
        Assert.Equal("first", s.FailureReason);
        Assert.False(s.TakeWarningDue(out _));
    }

    [Fact]
    public void LaterRaidsGetOneDebugNoticeEachWhileTheLatchHolds()
    {
        var s = new NativeGrassState();
        Run(s, Ready());
        s.Fail("boom");
        Assert.Equal(GrassAction.Teardown, s.Tick(Ready()));
        s.TeardownDone();
        Assert.True(s.TakeWarningDue(out _));
        for (int n = 0; n < 5; n++) s.Tick(Ready());
        Assert.False(s.TakeLatchedNoticeDue());                            // the raid that failed got its Warning
        GrassTickInputs outside = Ready();
        outside.InOwnedRaid = false;
        s.Tick(outside);                                                   // raid ends
        for (int n = 0; n < 5; n++) s.Tick(Ready(5));                      // the next raid
        Assert.True(s.TakeLatchedNoticeDue());
        Assert.False(s.TakeLatchedNoticeDue());
        for (int n = 0; n < 50; n++) s.Tick(Ready(5));
        Assert.False(s.TakeLatchedNoticeDue());
        s.Tick(outside);
        for (int n = 0; n < 3; n++) s.Tick(Ready(6));
        Assert.True(s.TakeLatchedNoticeDue());
    }

    [Fact]
    public void NoNoticeIsDueWhenNothingIsWanted()
    {
        var s = new NativeGrassState();
        Run(s, Ready());
        s.Fail("boom");
        s.Tick(Ready()); s.TeardownDone();
        GrassTickInputs off = Ready();
        off.InOwnedRaid = false;
        s.Tick(off);
        GrassTickInputs notWanted = Ready();
        notWanted.Wanted = false;
        for (int n = 0; n < 5; n++) s.Tick(notWanted);
        Assert.False(s.TakeLatchedNoticeDue());
    }

    [Fact]
    public void TeardownDoneWithoutATeardownChangesNothing()
    {
        NativeGrassState s = Built(Ready());
        s.TeardownDone();
        Assert.Equal(GrassPhase.Built, s.Phase);
        Assert.Equal(GrassAction.None, s.Tick(Ready()));
    }

    [Fact]
    public void FailFromIdleOwesNoTeardown()
    {
        var s = new NativeGrassState();
        s.Fail("probe");
        Assert.Equal(GrassAction.None, s.Tick(Ready()));
        Assert.Equal(GrassPhase.Failed, s.Phase);
    }

    [Fact]
    public void AnEmptyFailureReasonIsNamedUnspecified()
    {
        var s = new NativeGrassState();
        s.Fail(null);
        Assert.Equal("unspecified", s.FailureReason);
    }

    [Fact]
    public void WantsInputsOnlyWhileTheKeyIsOnTheMapHasGrassAndARaidIsOn()
    {
        GrassTickInputs i = Ready();
        Assert.True(NativeGrassState.WantsInputs(i));
        i = Ready(); i.Wanted = false; Assert.False(NativeGrassState.WantsInputs(i));
        i = Ready(); i.HasGrassMap = false; Assert.False(NativeGrassState.WantsInputs(i));
        i = Ready(); i.InOwnedRaid = false; Assert.False(NativeGrassState.WantsInputs(i));
    }

    [Fact]
    public void DescribeNamesPhaseWaitFramesAndTheLatchWithoutAnyContent()
    {
        var s = new NativeGrassState();
        s.Tick(Ready());
        Assert.Equal("phase=Waiting wait=FramesPending readyFrames=1", s.Describe());
        s.Fail("x");
        Assert.Equal("phase=Failed wait=Latched readyFrames=1 latched=x", s.Describe());
    }

    [Fact]
    public void TheTickInputsAreAStructSoTheGlueAllocatesNothingPerTick()
    {
        Assert.True(typeof(GrassTickInputs).IsValueType);
        var s = new NativeGrassState();
        GrassTickInputs i = Ready();
        i.Wanted = false;
        s.Tick(i);                                                          // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 1000; n++) s.Tick(i);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(after - before < 512, "allocated " + (after - before) + " B in 1000 idle ticks");
    }
}
