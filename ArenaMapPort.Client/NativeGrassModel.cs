#nullable disable
using System;
using System.Globalization;
using System.Text;

namespace ArenaMapPort.Client
{
    // The Unity-free core of the native grass (PLAN-YARD items 22 and 23; shipped on every map with Arena grass since stage 2, 2026-11-21; design: work/tmp/yard-grass/handoff/design-final.md).
    // Three parts:
    //   1. the keys (a map's own section, default ON, none public),
    //   2. the placement records as data (NativeGrassPlacement.Parse: Arena's ArenaGrassPrefabs_<Name>.pcl, the payload of the grass pack -> a plan of plain arrays),
    //   3. the state machine the glue ticks (NativeGrassState).
    // Which maps have grass is the descriptor's HasNativeGrass (ArenaMapDescriptor, mirrored in tools/maps/<stem>.json has_native_grass), their Grass scene its GrassSceneName and their bundle its
    // GrassBundleFileName; nothing per map lives here. No UnityEngine type, no log call, no file access: the glue (NativeGrassRepairs, NativeGrassArtSource) owns those. Linked into the server tests.

    /// <summary>The keys of the native grass (section "Map (&lt;map key&gt;)" of every map with HasNativeGrass; none public: nothing of them is in CompactConfiguration.IsPublic or MapConfigVisibility.IsPlayerOption).</summary>
    internal static class NativeGrassKeys
    {
        /// <summary>The section of a map's keys, the one every per-map repair uses (LevelBorderBoxRepairs, EmissionCapRepairs).</summary>
        internal static string SectionOf(string mapKey) => "Map (" + mapKey + ")";

        /// <summary>bool, default TRUE, live: false tears the build down, true builds (never in the frame of a teardown). The stage-1 probe key NativeGrass (Off|On, default Off) was never released and is not bound any more.</summary>
        internal const string Grass = "Grass";
        internal const bool GrassDefault = true;

        /// <summary>Developer A/B, bool, default true, live: false sets IsActive = false on both managers (no compute, no draw; buffers, HiZ and motion-vector targets stay), true sets it back. No teardown: the A/B that separates instance cost from fixed cost.</summary>
        internal const string Drawing = "GrassDrawing";
        internal const bool DrawingDefault = true;

        /// <summary>Developer A/B, Pack|Placeholder, default Pack. Placeholder = the BSG-free art of NativeGrassArt.Placeholder (a solid colour and a cut-out pattern per slice; the placement still comes from the pack). A change rebuilds.</summary>
        internal const string ArtSource = "GrassArt";
        internal const string ArtSourceDefault = "Pack";

        /// <summary>Developer A/B, SetPixelData|CopyTexture, default SetPixelData: how the compressed mips reach the Texture2DArray. A change rebuilds.</summary>
        internal const string UploadPath = "GrassUpload";
        internal const string UploadPathDefault = "SetPixelData";

        /// <summary>The stage-1 key names of the unreleased probe: never bound again (a new key instead of a changed default; an orphan line in a developer's .cfg is harmless).</summary>
        internal static readonly string[] RetiredStage1Keys = { "NativeGrass", "NativeGrassDrawing", "NativeGrassArt", "NativeGrassUpload" };
    }

    /// <summary>How the art reaches the GPU: the two ways the design names (design-final.md section 4).</summary>
    internal enum GrassUploadPath
    {
        /// <summary>Texture2DArray.SetPixelData(bytes, mip, element) per (slice, mip), then Apply(false, true).</summary>
        SetPixelData = 0,
        /// <summary>A temporary Texture2D per mip that Graphics.CopyTexture copies into (slice, mip), as EFT's decal code does.</summary>
        CopyTexture = 1,
    }

    /// <summary>Why a placement file was refused. The first failing check wins, in this order.</summary>
    internal enum GrassPlacementError
    {
        None = 0,
        /// <summary>No bytes at all (null).</summary>
        NoData,
        /// <summary>Zero length.</summary>
        Empty,
        /// <summary>The length is not a multiple of 68.</summary>
        LengthNotDivisible,
        /// <summary>More records than <see cref="NativeGrassPlacement.AbsoluteMaxRecords"/>.</summary>
        TooManyRecords,
        /// <summary>A matrix element is NaN or infinite (ErrorIndex = the record).</summary>
        NonFiniteMatrix,
        /// <summary>The matrix's fourth row is not (0, 0, 0, 1): it is no TRS matrix.</summary>
        NotAffine,
        /// <summary>An axis scale is outside <see cref="NativeGrassPlacement.MinAxisScale"/> .. <see cref="NativeGrassPlacement.MaxAxisScale"/> (the generic sanity range of every map).</summary>
        ScaleOutOfRange,
        /// <summary>The slice index is negative or not below the pack's slice count.</summary>
        SliceOutOfRange,
        /// <summary>A coordinate is farther than <see cref="NativeGrassPlacement.MaxAbsCoordinate"/> from the origin.</summary>
        OutsideBounds,
    }

    /// <summary>
    /// The placement records of one map as plain arrays the glue copies into Matrix4x4[] without allocating per record. Layout of <see cref="Matrices"/>: 16 floats per record,
    /// in the order of Unity's Matrix4x4 memory (column-major): element [16 * i + 4 * column + row] is m{row}{column} of record i, so floats 0..3 are m00, m10, m20, m30 (the first
    /// column), 12..14 are m03, m13, m23 (the translation) and 15 is m33. That is the order of the file itself (GrassPrefabData: int arrayIndex, Matrix4x4 matrix, 68 B, no padding).
    /// </summary>
    internal sealed class GrassPlan
    {
        internal GrassPlan(int count, int sliceCount)
        {
            Count = count;
            Slices = new int[count];
            Matrices = new float[count * NativeGrassPlacement.FloatsPerMatrix];
            SliceHistogram = new int[sliceCount];
        }

        internal int Count { get; }
        /// <summary>The slice (texture array index) of each record, in file order.</summary>
        internal int[] Slices { get; }
        /// <summary>16 floats per record, see the class comment.</summary>
        internal float[] Matrices { get; }
        /// <summary>How many records use each slice (length = the pack's slice count).</summary>
        internal int[] SliceHistogram { get; }
        internal float MinX { get; set; }
        internal float MaxX { get; set; }
        internal float MinY { get; set; }
        internal float MaxY { get; set; }
        internal float MinZ { get; set; }
        internal float MaxZ { get; set; }
        /// <summary>The smallest and the largest axis scale over all records (Iceberg's records are scaled non-uniformly: the three axes are measured on their own).</summary>
        internal float MinScale { get; set; }
        internal float MaxScale { get; set; }
        /// <summary>The largest angle in degrees between a record's up axis (the second matrix column) and the world's up axis: 0 for yaw-only placement (Yard), up to 50 on Iceberg.</summary>
        internal float MaxTiltDegrees { get; set; }

        /// <summary>The element of record <paramref name="record"/> at (row, column) of the matrix.</summary>
        internal float Element(int record, int row, int column) => Matrices[record * NativeGrassPlacement.FloatsPerMatrix + NativeGrassPlacement.ElementIndex(row, column)];

        /// <summary>The slice histogram as "0/8/8103/1457/564" (for the log line).</summary>
        internal string HistogramText()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < SliceHistogram.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('/');
                }
                sb.Append(SliceHistogram[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    /// <summary>The outcome of <see cref="NativeGrassPlacement.Parse"/>: a plan, or an error code with the index of the first bad record.</summary>
    internal readonly struct GrassParseResult
    {
        internal GrassParseResult(GrassPlan plan, GrassPlacementError error, int errorIndex)
        {
            Plan = plan;
            Error = error;
            ErrorIndex = errorIndex;
        }

        internal GrassPlan Plan { get; }
        internal GrassPlacementError Error { get; }
        /// <summary>The record that failed (record-level errors), -1 otherwise.</summary>
        internal int ErrorIndex { get; }
        internal bool Ok => Error == GrassPlacementError.None;
    }

    /// <summary>
    /// The placement file of Arena's grass (StreamingAssets/Grass/ArenaGrassPrefabs_&lt;Name&gt;.pcl, the first payload of the grass pack) as data. Read from the decompile (GPUInstancer/GrassInitialization.cs,
    /// GrassPrefabData.cs, GrassData.cs): no header; a record is GrassPrefabData { int arrayIndex; Matrix4x4 matrix; } = 4 + 64 = 68 bytes with no padding (all members 4-aligned), the count is the length
    /// divided by 68, the file is little-endian. The Unity matrix is stored in memory order, which is column-major (m00, m10, m20, m30, m01, ...): the translation is floats 12, 13, 14 of the 16.
    /// The checks are the ones that catch a BROKEN file on any map and nothing that pins a map's own measure (stage 1 pinned Yard's box and scale; the pack carries every map's own file, whose
    /// byte count its header states): the length is a whole number of records and at least one, every float is finite, the fourth matrix row is (0, 0, 0, 1) (a TRS matrix; tilted and non-uniformly
    /// scaled records, Iceberg's, are TRS too), the slice is below the slice count, every axis scale (the length of a matrix column) is a plausible size and no coordinate is absurd. Real files:
    /// Yard 10,132 records yaw-only and uniform; Bowl 113,643 up to 2.6 degrees of tilt; Iceberg 15,410 up to 50 degrees and scale up to 2.24 on one axis only (NativeGrassPackRealDataTests,
    /// tools/tests/test_grass_pack_real.py). The parse runs on a worker thread: it never touches Unity.
    /// </summary>
    internal static class NativeGrassPlacement
    {
        internal const int RecordSize = 68;
        internal const int FloatsPerMatrix = 16;
        /// <summary>No map has more (EFT's biggest vanilla file, Streets, has 285,383; Arena's Bowl 113,643); a bigger count is a wrong file.</summary>
        internal const int AbsoluteMaxRecords = 500000;
        /// <summary>How far the fourth row may be from (0, 0, 0, 1).</summary>
        internal const float AffineTolerance = 1e-4f;
        /// <summary>The plausible range of an axis scale (the length of a matrix column): Arena's grass measures 0.18 to 2.24; a column of zero length or of fifty metres is a broken matrix.</summary>
        internal const float MinAxisScale = 0.01f;
        internal const float MaxAxisScale = 50f;
        /// <summary>No coordinate of a map lies this far from the origin (the largest Arena map is a few hundred metres across).</summary>
        internal const float MaxAbsCoordinate = 20000f;

        /// <summary>The index of m{row}{column} in the 16 floats of a record (column-major).</summary>
        internal static int ElementIndex(int row, int column) => 4 * column + row;

        /// <summary>Parses <paramref name="length"/> bytes of <paramref name="bytes"/> into a plan for <paramref name="sliceCount"/> texture slices. Never throws for bad data: the result carries the error.</summary>
        internal static GrassParseResult Parse(byte[] bytes, int length, int sliceCount)
        {
            if (sliceCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(sliceCount));
            }
            if (bytes == null)
            {
                return Fail(GrassPlacementError.NoData, -1);
            }
            if (length < 0 || length > bytes.Length)
            {
                length = bytes.Length;
            }
            if (length == 0)
            {
                return Fail(GrassPlacementError.Empty, -1);
            }
            if (length % RecordSize != 0)
            {
                return Fail(GrassPlacementError.LengthNotDivisible, -1);
            }
            int count = length / RecordSize;
            if (count > AbsoluteMaxRecords)
            {
                return Fail(GrassPlacementError.TooManyRecords, -1);
            }

            var plan = new GrassPlan(count, sliceCount);
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            float minScale = float.MaxValue, maxScale = float.MinValue;
            float minUpY = 1f; // the cosine of the largest tilt is the smallest normalised y of the up axis
            float[] matrices = plan.Matrices;
            for (int i = 0; i < count; i++)
            {
                int at = i * RecordSize;
                int slice = ReadInt32(bytes, at);
                int m = i * FloatsPerMatrix;
                for (int k = 0; k < FloatsPerMatrix; k++)
                {
                    float v = ReadSingle(bytes, at + 4 + 4 * k);
                    if (float.IsNaN(v) || float.IsInfinity(v))
                    {
                        return Fail(GrassPlacementError.NonFiniteMatrix, i);
                    }
                    matrices[m + k] = v;
                }
                if (slice < 0 || slice >= sliceCount)
                {
                    return Fail(GrassPlacementError.SliceOutOfRange, i);
                }
                if (Math.Abs(matrices[m + 3]) > AffineTolerance || Math.Abs(matrices[m + 7]) > AffineTolerance
                    || Math.Abs(matrices[m + 11]) > AffineTolerance || Math.Abs(matrices[m + 15] - 1f) > AffineTolerance)
                {
                    return Fail(GrassPlacementError.NotAffine, i);
                }
                for (int c = 0; c < 3; c++)
                {
                    float x = matrices[m + 4 * c], y = matrices[m + 4 * c + 1], z = matrices[m + 4 * c + 2];
                    float scale = (float)Math.Sqrt(x * x + y * y + z * z);
                    if (!(scale >= MinAxisScale && scale <= MaxAxisScale))
                    {
                        return Fail(GrassPlacementError.ScaleOutOfRange, i);
                    }
                    if (scale < minScale) minScale = scale;
                    if (scale > maxScale) maxScale = scale;
                    if (c == 1)
                    {
                        float upY = y / scale;
                        if (upY < minUpY) minUpY = upY;
                    }
                }
                float px = matrices[m + 12], py = matrices[m + 13], pz = matrices[m + 14];
                if (Math.Abs(px) > MaxAbsCoordinate || Math.Abs(py) > MaxAbsCoordinate || Math.Abs(pz) > MaxAbsCoordinate)
                {
                    return Fail(GrassPlacementError.OutsideBounds, i);
                }
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
                if (pz < minZ) minZ = pz;
                if (pz > maxZ) maxZ = pz;
                plan.Slices[i] = slice;
                plan.SliceHistogram[slice]++;
            }
            plan.MinX = minX; plan.MaxX = maxX;
            plan.MinY = minY; plan.MaxY = maxY;
            plan.MinZ = minZ; plan.MaxZ = maxZ;
            plan.MinScale = minScale;
            plan.MaxScale = maxScale;
            plan.MaxTiltDegrees = (float)(Math.Acos(Math.Max(-1.0, Math.Min(1.0, minUpY))) * 180.0 / Math.PI);
            return new GrassParseResult(plan, GrassPlacementError.None, -1);
        }

        /// <summary>A one-line reason for the log (codes, no file content).</summary>
        internal static string Describe(GrassParseResult result, int length)
        {
            string head = "placement " + length.ToString(CultureInfo.InvariantCulture) + " B: ";
            if (result.Ok)
            {
                return head + result.Plan.Count.ToString(CultureInfo.InvariantCulture) + " records";
            }
            return head + result.Error + (result.ErrorIndex >= 0 ? " at record " + result.ErrorIndex.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        static GrassParseResult Fail(GrassPlacementError error, int index) => new GrassParseResult(null, error, index);

        // The file is little-endian; every target of the client (Windows x64) is too, so BitConverter reads it as it is.
        static int ReadInt32(byte[] b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

        static float ReadSingle(byte[] b, int at) => BitConverter.ToSingle(b, at);
    }

    /// <summary>What the state machine decided for this tick.</summary>
    internal enum GrassAction
    {
        None = 0,
        /// <summary>The glue builds now, in this tick on the main thread, then calls <see cref="NativeGrassState.BuildSucceeded"/> or <see cref="NativeGrassState.Fail"/>.</summary>
        Build,
        /// <summary>The glue tears everything down now, then calls <see cref="NativeGrassState.TeardownDone"/>.</summary>
        Teardown,
    }

    internal enum GrassPhase
    {
        /// <summary>Nothing wanted, or no row, or no owned raid.</summary>
        Idle = 0,
        /// <summary>Wanted, a precondition is missing (see <see cref="NativeGrassState.WaitReason"/>).</summary>
        Waiting,
        /// <summary>A Build was handed out and not yet answered.</summary>
        Building,
        /// <summary>Built and drawing (or switched to not drawing by the A/B key).</summary>
        Built,
        /// <summary>A Teardown was handed out and not yet answered.</summary>
        TearingDown,
        /// <summary>A failure latched for the session: no more builds until the process restarts.</summary>
        Failed,
    }

    internal enum GrassWait
    {
        None = 0,
        NotWanted,
        NoGrassMap,
        NoRaid,
        ScenesNotLoaded,
        NoCamera,
        NoOpticCamera,
        GameStopping,
        FramesPending,
        InputsPending,
        /// <summary>The map's pack was refused (missing, damaged, wrong map...): no build for this map in this session, and no failure latch for any other map.</summary>
        InputsRefused,
        GapAfterTeardown,
        Latched,
    }

    internal enum GrassTeardownReason
    {
        None = 0,
        KeyOff,
        RaidEnded,
        ScenesUnloaded,
        CameraLost,
        CameraReplaced,
        GameStopping,
        ConfigChanged,
        Failure,
    }

    /// <summary>What the glue reads each tick: cheap reads only, no scene search. The glue keeps one instance and assigns the fields (it is a struct: no allocation).</summary>
    internal struct GrassTickInputs
    {
        /// <summary>The map's key Grass is on and its pack was not refused earlier in this session.</summary>
        internal bool Wanted;
        /// <summary>The map the raid runs has native grass (the descriptor's HasNativeGrass).</summary>
        internal bool HasGrassMap;
        /// <summary>An owned raid is on (the map's Scripts scene is loaded).</summary>
        internal bool InOwnedRaid;
        /// <summary>Every scene of the map (the Grass scene in particular) is loaded.</summary>
        internal bool ScenesLoaded;
        /// <summary>CameraManager.Exist and the game camera exists.</summary>
        internal bool CameraReady;
        /// <summary>The optic camera exists (a disabled GameObject until a scope is raised: existence, not enabled).</summary>
        internal bool OpticCameraReady;
        /// <summary>Changes whenever the glue sees another game camera instance than before (camera replaced by a second SetCamera).</summary>
        internal int CameraSerial;
        /// <summary>The raid's AbstractGame status is Stopping or Stopped.</summary>
        internal bool GameStopping;
        /// <summary>The worker delivered the placement and the art from the pack, both verified.</summary>
        internal bool InputsReady;
        /// <summary>The worker's definitive refusal (a reason code), or null. Not "pending": pending is InputsReady false. A refusal is the MAP's, never a session latch (see <see cref="GrassWait.InputsRefused"/>).</summary>
        internal string InputError;
        /// <summary>A number that changes when a key that needs a rebuild changes (art source, upload path): a built set with another signature is torn down and rebuilt.</summary>
        internal int BuildSignature;
    }

    /// <summary>
    /// The state machine of the grass, a pure class the glue ticks once per LateUpdate (design-final.md section 2, G4 section 4). It decides; the glue acts and answers.
    ///
    /// Protocol: <c>Tick</c> returns Build, Teardown or None. After Build the glue builds in that same tick (inside one try/catch) and calls <see cref="BuildSucceeded"/>, or <see cref="Fail"/>
    /// with a reason (which also owes a teardown). After Teardown the glue tears down and calls <see cref="TeardownDone"/> in the same tick. A Build is never returned in the tick after a
    /// teardown was handed out nor in the one after that was answered: <see cref="TeardownDone"/> arms a one-tick gap (the design's one-frame gap before a rebuild), and a rebuild also
    /// needs <see cref="ReadyFramesRequired"/> ticks of ready cameras, which restart whenever the cameras are missing or replaced.
    ///
    /// Two kinds of failure. An ENGINE-side failure (the build or the tick threw: EFT's GPUInstancer, a Unity call) is a sticky latch for the process: <see cref="Fail"/> sets it, the next Tick
    /// returns Teardown once (idempotent teardown of whatever was built), the first failure of the session asks for ONE Warning (<see cref="TakeWarningDue"/>), later raids ask for a Debug notice
    /// (<see cref="TakeLatchedNoticeDue"/>) once per raid. A refusal of a MAP's inputs (its pack is missing, damaged or for another map: <see cref="GrassTickInputs.InputError"/>) is not a latch:
    /// the tick answers <see cref="GrassWait.InputsRefused"/> and the glue switches that one map's grass off (its own per-map record), so the other maps keep theirs. Nothing here logs.
    /// </summary>
    internal sealed class NativeGrassState
    {
        /// <summary>Ticks with the game camera and the optic camera both present (and the same camera) before a build starts: keeps the work out of any EFT call stack of the raid start.</summary>
        internal const int ReadyFramesRequired = 30;

        int _readyFrames;
        int _lastSerial;
        bool _haveSerial;
        int _builtSerial;
        int _builtSignature;
        int _gapTicks;
        bool _failed;
        bool _teardownOwed;
        bool _warningDue;
        bool _warningTaken;
        bool _noticeShownThisRaid;
        // The game reported stopping while the scenes and cameras of this raid were up: the raid is over for the grass. Host 2026-11-21: EFT releases its game singleton about 1.7 s after the
        // stop while the scenes and both cameras still live, so 'stopping' read false again and the grass was built once more for 1.4 s before the scene unload tore it down.
        bool _raidOver;
        bool _noticeDue;
        string _failureReason;
        GrassTeardownReason _teardownReason;

        internal GrassPhase Phase { get; private set; }
        internal GrassWait WaitReason { get; private set; }
        /// <summary>Why the last Teardown was handed out.</summary>
        internal GrassTeardownReason TeardownReason => _teardownReason;
        internal bool Failed => _failed;
        internal string FailureReason => _failureReason;
        internal int ReadyFrames => _readyFrames;
        internal bool IsBuilt => Phase == GrassPhase.Built;
        /// <summary>True from the first stop the machine saw with the raid fully up until <see cref="NewRaid"/> or a tick outside a raid: nothing is built or read any more.</summary>
        internal bool RaidOver => _raidOver;

        /// <summary>The glue saw the Scripts scene of a grass map load: a new raid, whatever the last one ended as (ticks do not run between raids while nothing is wanted).</summary>
        internal void NewRaid()
        {
            _raidOver = false;
            _noticeShownThisRaid = false;
        }

        /// <summary>True when the worker has work to do: the key is on, the map has grass and a raid is on. The glue starts its file worker only then (never while nothing is wanted).</summary>
        internal static bool WantsInputs(in GrassTickInputs i) => i.Wanted && i.HasGrassMap && i.InOwnedRaid;

        internal GrassAction Tick(in GrassTickInputs i)
        {
            if (!i.InOwnedRaid)
            {
                _noticeShownThisRaid = false;
                _raidOver = false;
            }
            TrackCameras(i);

            if (_gapTicks > 0)
            {
                _gapTicks--;
                WaitReason = GrassWait.GapAfterTeardown;
                return GrassAction.None;
            }

            switch (Phase)
            {
                case GrassPhase.Building:
                case GrassPhase.TearingDown:
                    return GrassAction.None;

                case GrassPhase.Built:
                {
                    GrassTeardownReason reason = BuiltTeardownReason(i);
                    if (reason == GrassTeardownReason.None)
                    {
                        return GrassAction.None;
                    }
                    _teardownReason = reason;
                    Phase = GrassPhase.TearingDown;
                    return GrassAction.Teardown;
                }

                case GrassPhase.Failed:
                    if (_teardownOwed)
                    {
                        _teardownReason = GrassTeardownReason.Failure;
                        Phase = GrassPhase.TearingDown;
                        return GrassAction.Teardown;
                    }
                    WaitReason = GrassWait.Latched;
                    if (i.Wanted && i.HasGrassMap && i.InOwnedRaid && !_noticeShownThisRaid)
                    {
                        _noticeShownThisRaid = true;
                        _noticeDue = true;
                    }
                    return GrassAction.None;
            }

            // Idle or Waiting: the ladder of preconditions, the first missing one is the reason.
            GrassWait wait = Ladder(i);
            if (wait == GrassWait.None)
            {
                Phase = GrassPhase.Building;
                WaitReason = GrassWait.None;
                _builtSerial = i.CameraSerial;
                _builtSignature = i.BuildSignature;
                return GrassAction.Build;
            }
            if (wait == GrassWait.Latched)
            {
                // an input refusal latched inside Ladder
                return GrassAction.None;
            }
            WaitReason = wait;
            Phase = (wait == GrassWait.NotWanted || wait == GrassWait.NoGrassMap || wait == GrassWait.NoRaid) ? GrassPhase.Idle : GrassPhase.Waiting;
            return GrassAction.None;
        }

        /// <summary>The glue's build finished without an exception.</summary>
        internal void BuildSucceeded()
        {
            if (Phase == GrassPhase.Building)
            {
                Phase = GrassPhase.Built;
            }
        }

        /// <summary>
        /// A failure: the build threw, a later step threw, or the glue found the art or placement unusable. Latches for the process, owes one idempotent teardown (the next Tick returns it)
        /// and arms the one Warning of the session.
        /// </summary>
        internal void Fail(string reason)
        {
            bool hadWork = Phase == GrassPhase.Building || Phase == GrassPhase.Built || Phase == GrassPhase.TearingDown;
            if (!_failed)
            {
                _failed = true;
                _failureReason = string.IsNullOrEmpty(reason) ? "unspecified" : reason;
                if (!_warningTaken)
                {
                    _warningDue = true;
                }
            }
            if (hadWork)
            {
                _teardownOwed = true;
            }
            _noticeShownThisRaid = true;
            Phase = GrassPhase.Failed;
            WaitReason = GrassWait.Latched;
        }

        /// <summary>The glue tore everything down (best effort, idempotent). Arms the one-tick gap before any rebuild.</summary>
        internal void TeardownDone()
        {
            if (Phase != GrassPhase.TearingDown)
            {
                return;
            }
            _teardownOwed = false;
            _gapTicks = 1;
            Phase = _failed ? GrassPhase.Failed : GrassPhase.Idle;
            WaitReason = _failed ? GrassWait.Latched : GrassWait.None;
        }

        /// <summary>True once, at the first failure of the session: the glue logs ONE Warning with <paramref name="reason"/>.</summary>
        internal bool TakeWarningDue(out string reason)
        {
            reason = _failureReason;
            if (!_warningDue)
            {
                return false;
            }
            _warningDue = false;
            _warningTaken = true;
            return true;
        }

        /// <summary>True once per raid while the latch holds and grass is wanted: the glue logs one Debug line that this raid has no grass and why.</summary>
        internal bool TakeLatchedNoticeDue()
        {
            if (!_noticeDue)
            {
                return false;
            }
            _noticeDue = false;
            return true;
        }

        /// <summary>A short line for the Debug log: phase, wait reason, ready frames, latch.</summary>
        internal string Describe()
        {
            return "phase=" + Phase + " wait=" + WaitReason + " readyFrames=" + _readyFrames.ToString(CultureInfo.InvariantCulture)
                + (_failed ? " latched=" + _failureReason : string.Empty);
        }

        void TrackCameras(in GrassTickInputs i)
        {
            bool ready = i.CameraReady && i.OpticCameraReady;
            if (!ready || !_haveSerial || _lastSerial != i.CameraSerial)
            {
                _readyFrames = 0;
            }
            _haveSerial = ready;
            _lastSerial = i.CameraSerial;
            if (ready && _readyFrames < int.MaxValue)
            {
                _readyFrames++;
            }
        }

        GrassTeardownReason BuiltTeardownReason(in GrassTickInputs i)
        {
            if (!i.Wanted) return GrassTeardownReason.KeyOff;
            if (!i.InOwnedRaid || !i.HasGrassMap) return GrassTeardownReason.RaidEnded;
            if (!i.ScenesLoaded) return GrassTeardownReason.ScenesUnloaded;
            if (!i.CameraReady || !i.OpticCameraReady) return GrassTeardownReason.CameraLost;
            if (i.CameraSerial != _builtSerial) return GrassTeardownReason.CameraReplaced;
            if (i.GameStopping || _raidOver)
            {
                _raidOver = true;
                return GrassTeardownReason.GameStopping;
            }
            if (i.BuildSignature != _builtSignature) return GrassTeardownReason.ConfigChanged;
            return GrassTeardownReason.None;
        }

        GrassWait Ladder(in GrassTickInputs i)
        {
            if (!i.Wanted) return GrassWait.NotWanted;
            if (!i.HasGrassMap) return GrassWait.NoGrassMap;
            if (!i.InOwnedRaid) return GrassWait.NoRaid;
            if (i.InputError != null)
            {
                // the map's pack was refused: not an engine failure, so no session latch; the glue records it for this map and the next tick finds the map not wanted
                return GrassWait.InputsRefused;
            }
            if (!i.ScenesLoaded) return GrassWait.ScenesNotLoaded;
            if (!i.CameraReady) return GrassWait.NoCamera;
            if (!i.OpticCameraReady) return GrassWait.NoOpticCamera;
            // only here, with the scenes and both cameras up: a stale flag while the next raid is still loading must not end that raid
            if (i.GameStopping || _raidOver)
            {
                _raidOver = true;
                return GrassWait.GameStopping;
            }
            if (_readyFrames < ReadyFramesRequired) return GrassWait.FramesPending;
            if (!i.InputsReady) return GrassWait.InputsPending;
            return GrassWait.None;
        }
    }
}
