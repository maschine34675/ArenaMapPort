#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The worker side of the native grass probe (NativeGrassArtSource.cs) on an injected file layer: the load never throws, every refusal is a reason CODE (never a path or content), the placeholder
/// art needs no art file, Arena's art is checked by size and structure before the glue sees a byte, and a stopped worker answers "abandoned". Unit-tested only; the real files are in
/// <see cref="NativeGrassRealDataTests"/> (the same parse and checks on the installed Arena).
/// </summary>
public sealed class NativeGrassArtSourceTests
{
    static readonly NativeGrassMapRow Row = NativeGrassTable.Yard;

    /// <summary>An in-memory installation: path to bytes, with the lies a test needs (a file that reports another length, a throwing open).</summary>
    sealed class FakeFiles : IArenaFiles
    {
        internal readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        internal readonly Dictionary<string, long> LengthOverride = new Dictionary<string, long>();
        internal readonly HashSet<string> ZeroFilled = new HashSet<string>();
        internal Exception OpenThrows;
        internal Exception LengthThrows;
        internal readonly List<string> Opened = new List<string>();

        public long Length(string relativePath)
        {
            if (LengthThrows != null) throw LengthThrows;
            if (LengthOverride.TryGetValue(relativePath, out long forced)) return forced;
            return Files.TryGetValue(relativePath, out byte[] data) ? data.Length : -1;
        }

        public Stream Open(string relativePath)
        {
            Opened.Add(relativePath);
            if (OpenThrows != null) throw OpenThrows;
            if (Files.TryGetValue(relativePath, out byte[] data)) return new MemoryStream(data, false);
            if (ZeroFilled.Contains(relativePath)) return new ZeroStream(LengthOverride[relativePath]);
            throw new FileNotFoundException(relativePath);
        }
    }

    /// <summary>A read-only stream of zeros of a given length (an Arena file wiped to zero without allocating 11 MB).</summary>
    sealed class ZeroStream : Stream
    {
        readonly long _length;
        long _position;

        internal ZeroStream(long length) { _length = length; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Max(0, Math.Min(count, _length - _position));
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : _length + offset;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A valid placement file for the Yard row: identity rotation, scale 1, positions inside the box, the slice cycling 0..4.</summary>
    static byte[] PlacementBytes(int count)
    {
        var bytes = new byte[count * NativeGrassPlacement.RecordSize];
        for (int i = 0; i < count; i++)
        {
            int at = i * NativeGrassPlacement.RecordSize;
            BitConverter.GetBytes(i % Row.SliceCount).CopyTo(bytes, at);
            float[] m = new float[16];
            m[0] = 1f; m[5] = 1f; m[10] = 1f; m[15] = 1f;
            m[12] = -60f + (i % 70);
            m[13] = 6f;
            m[14] = 20f + (i % 90);
            for (int k = 0; k < 16; k++) BitConverter.GetBytes(m[k]).CopyTo(bytes, at + 4 + 4 * k);
        }
        return bytes;
    }

    static FakeFiles WithPlacement(int count = 9000)
    {
        var files = new FakeFiles();
        files.Files[Row.PlacementRelativePath] = PlacementBytes(count);
        return files;
    }

    // ---- the placeholder path: no art file at all ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePlaceholderLoadReadsOnlyThePlacementAndFillsThreeBuffersOfTheExactSizes()
    {
        FakeFiles files = WithPlacement();
        GrassInputs inputs = GrassPreparation.Load(null, Row, files, GrassArtKind.Placeholder);
        Assert.True(inputs.Ok, inputs.Error);
        Assert.Equal(9000, inputs.Plan.Count);
        Assert.Equal("Placeholder", inputs.ArtName);
        Assert.Equal(NativeGrassArt.NormalTexture.Size, inputs.NormalTexture.LongLength);
        Assert.Equal(NativeGrassArt.TexArray.Size, inputs.TexArray.LongLength);
        Assert.Equal(NativeGrassArt.NormalArray.Size, inputs.NormalArray.LongLength);
        Assert.Equal(0L, inputs.BytesRead);
        Assert.Equal(new[] { Row.PlacementRelativePath }, files.Opened);   // no Arena art file was opened
    }

    [Fact]
    public void ThePlaceholderLoadIsTheSameWhateverTheArenaFilesLookLike()
    {
        FakeFiles files = WithPlacement();
        files.LengthOverride[NativeGrassArt.StreamPath] = 5;   // would refuse Arena's art; the placeholder never looks
        GrassInputs inputs = GrassPreparation.Load(null, Row, files, GrassArtKind.Placeholder);
        Assert.True(inputs.Ok, inputs.Error);
    }

    // ---- the placement refusals ------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AMissingPlacementFileIsACodeNotAnException()
    {
        GrassInputs inputs = GrassPreparation.Load(null, Row, new FakeFiles(), GrassArtKind.Placeholder);
        Assert.Equal("placement:Missing", inputs.Error);
        Assert.Null(inputs.Plan);
    }

    [Fact]
    public void ARecordCountOutsideTheRowsRangeIsRefusedWithItsCode()
    {
        GrassInputs inputs = GrassPreparation.Load(null, Row, WithPlacement(Row.MinCount - 1), GrassArtKind.Placeholder);
        Assert.Equal("placement:CountOutOfRange", inputs.Error);
    }

    [Fact]
    public void ALengthThatIsNotAMultipleOfTheRecordSizeIsRefused()
    {
        FakeFiles files = WithPlacement();
        files.Files[Row.PlacementRelativePath] = new byte[NativeGrassPlacement.RecordSize * 9000 + 3];
        Assert.Equal("placement:LengthNotDivisible", GrassPreparation.Load(null, Row, files, GrassArtKind.Placeholder).Error);
    }

    [Fact]
    public void ABadRecordCarriesItsIndexInTheCode()
    {
        FakeFiles files = WithPlacement();
        byte[] bytes = files.Files[Row.PlacementRelativePath];
        BitConverter.GetBytes(float.NaN).CopyTo(bytes, 12 * NativeGrassPlacement.RecordSize + 4 + 4 * 12);
        Assert.Equal("placement:NonFiniteMatrix@12", GrassPreparation.Load(null, Row, files, GrassArtKind.Placeholder).Error);
    }

    [Fact]
    public void AnEmptyPlacementFileIsRefusedBeforeAnyRead()
    {
        FakeFiles files = new FakeFiles();
        files.Files[Row.PlacementRelativePath] = new byte[0];
        Assert.Equal("placement:BadLength", GrassPreparation.Load(null, Row, files, GrassArtKind.Placeholder).Error);
    }

    // ---- Arena's art: sizes first, then the structure check ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnAssetsFileOfAnotherSizeRefusesArenasArtBeforeReadingIt()
    {
        FakeFiles files = WithPlacement();
        files.Files[NativeGrassArt.AssetsPath] = new byte[100];
        files.LengthOverride[NativeGrassArt.StreamPath] = NativeGrassArt.StreamSize;
        Assert.Equal("art-size:AssetsSizeDiffers", GrassPreparation.Load(null, Row, files, GrassArtKind.Arena).Error);
        Assert.DoesNotContain(NativeGrassArt.StreamPath, files.Opened);
    }

    [Fact]
    public void AStreamOfAnotherSizeRefusesArenasArt()
    {
        FakeFiles files = WithPlacement();
        files.LengthOverride[NativeGrassArt.AssetsPath] = NativeGrassArt.AssetsSize;
        files.LengthOverride[NativeGrassArt.StreamPath] = NativeGrassArt.StreamSize + 8;
        Assert.Equal("art-size:StreamSizeDiffers", GrassPreparation.Load(null, Row, files, GrassArtKind.Arena).Error);
    }

    [Fact]
    public void AWipedStreamOfThePinnedSizeFailsTheStructureCheckOnTheFirstRange()
    {
        FakeFiles files = WithPlacement();
        files.LengthOverride[NativeGrassArt.AssetsPath] = NativeGrassArt.AssetsSize;
        files.LengthOverride[NativeGrassArt.StreamPath] = NativeGrassArt.StreamSize;
        files.ZeroFilled.Add(NativeGrassArt.StreamPath);
        GrassInputs inputs = GrassPreparation.Load(null, Row, files, GrassArtKind.Arena);
        Assert.Equal("art-blocks:Grass_new_1_N:ConstantData", inputs.Error);
        Assert.Null(inputs.TexArray);   // nothing past the failing range is handed over
    }

    // ---- failures never throw and never carry a path --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnExceptionFromTheFileLayerBecomesAnIoCodeWithTheTypeOnly()
    {
        FakeFiles files = WithPlacement();
        files.LengthThrows = new UnauthorizedAccessException(@"D:\secret\path");
        GrassInputs inputs = GrassPreparation.Load(null, Row, files, GrassArtKind.Placeholder);
        Assert.Equal("io:UnauthorizedAccessException", inputs.Error);
        Assert.DoesNotContain("secret", inputs.Error);

        files = WithPlacement();
        files.OpenThrows = new IOException(@"D:\another\secret");
        Assert.Equal("io:IOException", GrassPreparation.Load(null, Row, files, GrassArtKind.Placeholder).Error);
    }

    [Fact]
    public void EveryErrorCodeIsAShortCodeWithoutSeparatorsOfAPath()
    {
        var codes = new List<string>
        {
            GrassPreparation.Load(null, Row, new FakeFiles(), GrassArtKind.Arena).Error,
            GrassPreparation.Load(null, Row, WithPlacement(10), GrassArtKind.Arena).Error,
        };
        foreach (string code in codes)
        {
            Assert.NotNull(code);
            Assert.DoesNotContain("\\", code);
            Assert.DoesNotContain("/", code);
            Assert.True(code.Length < 60, code);
        }
    }

    // ---- the asynchronous side ---------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheWorkerFinishesAndHandsOverAReadyResult()
    {
        GrassPreparation prep = GrassPreparation.Start(Row, WithPlacement(), GrassArtKind.Placeholder);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!prep.IsCompleted && watch.ElapsedMilliseconds < 20000) Thread.Sleep(5);
        Assert.True(prep.IsCompleted);
        Assert.True(prep.Result.Ok, prep.Result.Error);
    }

    [Fact]
    public void AnAbandonedWorkerAnswersAbandonedAfterThePlacement()
    {
        // Load only asks the owner whether it was abandoned: an owner that never ran a task is enough for the question
        var owner = (GrassPreparation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GrassPreparation));
        owner.Abandon();
        Assert.True(owner.Abandoned);
        GrassInputs inputs = GrassPreparation.Load(owner, Row, WithPlacement(), GrassArtKind.Placeholder);
        Assert.Equal("worker:abandoned", inputs.Error);
    }

    [Fact]
    public void DropArtReleasesTheThreeBuffersButKeepsThePlan()
    {
        GrassInputs inputs = GrassPreparation.Load(null, Row, WithPlacement(), GrassArtKind.Placeholder);
        inputs.DropArt();
        Assert.Null(inputs.TexArray);
        Assert.Null(inputs.NormalArray);
        Assert.Null(inputs.NormalTexture);
        Assert.NotNull(inputs.Plan);
    }

    [Fact]
    public void TheArenaSourceReadsExactlyTheRangeAtItsOffset()
    {
        // a small fake stream with a marker at a known offset: Read(range) returns exactly Size bytes from Offset (the offsets are the model's, the read is positional)
        FakeFiles files = new FakeFiles();
        var stream = new byte[NativeGrassArt.StreamSize];
        stream[NativeGrassArt.NormalTexture.Offset] = 0xA5;
        stream[NativeGrassArt.NormalTexture.Offset + NativeGrassArt.NormalTexture.Size - 1] = 0x5A;
        files.Files[NativeGrassArt.StreamPath] = stream;
        files.Files[NativeGrassArt.AssetsPath] = new byte[NativeGrassArt.AssetsSize];
        var source = new ArenaFilesArtSource(files);
        Assert.Equal(GrassArtError.None, source.CheckFiles());
        Assert.Equal(GrassArtError.None, source.Read(NativeGrassArt.NormalTexture, out byte[] data));
        Assert.Equal((int)NativeGrassArt.NormalTexture.Size, data.Length);
        Assert.Equal(0xA5, data[0]);
        Assert.Equal(0x5A, data[^1]);
        Assert.Equal(NativeGrassArt.NormalTexture.Size, source.BytesRead);
    }
}
