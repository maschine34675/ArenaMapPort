#nullable disable
using System.Diagnostics;
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The worker side of the native grass (NativeGrassArtSource.cs, stage 2): the staged grass bundle is read through an injected file layer, as the plain pack (a development install) or as an AMPB container
/// opened with the map's key through the real <see cref="Ampb"/> writer and <see cref="AmpbReadStream"/>. Every refusal is a reason code and nothing throws. The packs are synthetic
/// (<see cref="GrassTestPack"/>); real packs are in <see cref="NativeGrassPackRealDataTests"/>. Unit-tested; nothing here is host-run.
/// </summary>
public sealed class NativeGrassArtSourceTests
{
    const string Yard = "arena_yard";
    const string Path = "plugin/bundles/arena_yard_grass.bundle";

    sealed class TrackedStream : MemoryStream
    {
        internal TrackedStream(byte[] bytes) : base(bytes, false) { }
        internal bool Closed;
        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    sealed class FakeFiles : IGrassPackFiles
    {
        internal readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        internal readonly List<TrackedStream> Opened = new List<TrackedStream>();
        internal Exception Throws;
        internal int Opens;

        public Stream OpenRead(string path)
        {
            Interlocked.Increment(ref Opens);
            if (Throws != null) throw Throws;
            if (!Files.TryGetValue(path, out byte[] bytes)) return null;
            var s = new TrackedStream(bytes);
            lock (Opened) Opened.Add(s);
            return s;
        }
    }

    sealed class ThrowingStream : MemoryStream
    {
        internal ThrowingStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("C:\\Users\\player\\secret\\path.bundle");
    }

    static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

    static byte[] Encrypt(byte[] plain, string mapKey, byte[] key = null)
    {
        using var source = new MemoryStream(plain);
        using var dest = new MemoryStream();
        Ampb.Encrypt(source, dest, mapKey, key ?? Key, CryptoProviders.Managed, 4096);
        return dest.ToArray();
    }

    static GrassPreparation Start(FakeFiles files, GrassKeyResolver keys = null, GrassArtKind kind = GrassArtKind.Pack, string mapKey = Yard)
    {
        keys ??= (string map, out byte[] key) => { key = Key; return true; };
        return GrassPreparation.Start(mapKey, Path, files, keys, () => CryptoProviders.Managed, kind);
    }

    static GrassInputs Finish(GrassPreparation p)
    {
        var watch = Stopwatch.StartNew();
        while (!p.IsCompleted)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "the worker did not finish");
            Thread.Sleep(2);
        }
        return p.Result;
    }

    static FakeFiles With(byte[] file) { var f = new FakeFiles(); f.Files[Path] = file; return f; }

    static readonly GrassKeyResolver NoKeyNeeded = (string map, out byte[] key) => throw new InvalidOperationException("the key must not be asked for a plain pack");

    // ---- the plain pack -----------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void APlainPackLoadsWithoutAnyKey()
    {
        byte[] pack = GrassTestPack.Build(Yard, GrassTestPack.Placement(40));
        var files = With(pack);
        GrassInputs r = Finish(Start(files, NoKeyNeeded));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("plain", r.Container);
        Assert.Equal("Pack", r.ArtName);
        Assert.Equal("pack=ok placement=ok blocks=3/3", r.Checks);
        Assert.Equal(Yard, r.Layout.MapKey);
        Assert.Equal(40, r.Plan.Count);
        Assert.Equal("8/8/8/8/8", r.Plan.HistogramText());
        Assert.Equal(40 * 68, r.PlacementLength);
        // the buffers are the payloads byte for byte
        Assert.Equal(Slice(pack, 3), r.NormalTexture);
        Assert.Equal(Slice(pack, 1), r.TexArray);
        Assert.Equal(Slice(pack, 2), r.NormalArray);
        Assert.Equal(272L + 40 * 68 + r.TexArray.Length + r.NormalArray.Length + r.NormalTexture.Length, r.BytesRead);
        Assert.True(r.OpenTicks >= 0 && r.PlacementTicks >= 0 && r.ArtTicks > 0);
        Assert.All(files.Opened, s => Assert.True(s.Closed));            // the file is closed when the worker is done
        Assert.Equal(1, files.Opens);
    }

    static byte[] Slice(byte[] pack, int entry)
    {
        (int offset, int size) = GrassTestPack.Payload(pack, entry);
        var part = new byte[size];
        Array.Copy(pack, offset, part, 0, size);
        return part;
    }

    [Fact]
    public void ADropOfTheArtLeavesThePlanAndTheLayout()
    {
        GrassInputs r = Finish(Start(With(GrassTestPack.Build(Yard)), NoKeyNeeded));
        Assert.True(r.Ok);
        r.DropArt();
        Assert.Null(r.TexArray);
        Assert.Null(r.NormalArray);
        Assert.Null(r.NormalTexture);
        Assert.NotNull(r.Plan);
        Assert.NotNull(r.Layout);
    }

    [Fact]
    public void APackOfBowlsSizeLoadsThroughTheWorker()
    {
        // 113,643 records (Bowl): the placement is one 7.7 MB payload and one parse
        byte[] records = GrassTestPack.Placement(113643);
        GrassInputs r = Finish(Start(With(GrassTestPack.Build(Yard, records)), NoKeyNeeded));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(113643, r.Plan.Count);
        Assert.Equal(113643, r.Plan.SliceHistogram.Sum());
        Assert.Equal(113643L * 68, r.PlacementLength);
    }

    [Fact]
    public void ATiltedNonUniformPlacementIsAcceptedThroughTheWholePath()
    {
        byte[] records = GrassTestPack.Placement(200, 5, (f, i) =>
        {
            if (i != 17) return;
            float c = (float)Math.Cos(50 * Math.PI / 180.0), s = (float)Math.Sin(50 * Math.PI / 180.0);
            float[] m = { 1f, 0f, 0f, 0f, 0f, c * 2.24f, s * 2.24f, 0f, 0f, -s, c, 0f, 5f, 3f, -80f, 1f };
            for (int k = 0; k < 16; k++) BitConverter.GetBytes(m[k]).CopyTo(f, i * 68 + 4 + 4 * k);
        });
        GrassInputs r = Finish(Start(With(GrassTestPack.Build(Yard, records)), NoKeyNeeded));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(50f, r.Plan.MaxTiltDegrees, 1);
        Assert.Equal(2.24f, r.Plan.MaxScale, 3);
    }

    // ---- the container ------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AContainerIsOpenedWithTheMapsKeyAndTheCopyOfTheKeyIsNotTheSharedOne()
    {
        byte[] pack = GrassTestPack.Build(Yard, GrassTestPack.Placement(40));
        byte[] container = Encrypt(pack, Yard);
        Assert.Equal((byte)'A', container[0]);
        byte[] shared = (byte[])Key.Clone();
        int asked = 0;
        string askedFor = null;
        var files = With(container);
        GrassInputs r = Finish(Start(files, (string map, out byte[] key) => { asked++; askedFor = map; key = shared; return true; }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("AMPB", r.Container);
        Assert.Equal(1, asked);
        Assert.Equal(Yard, askedFor);
        Assert.Equal(Key, shared);                                          // the resolver's array is shared with every other bundle: the worker clears only its own copy
        Assert.Equal(Slice(pack, 1), r.TexArray);
        Assert.Equal(Slice(pack, 3), r.NormalTexture);
        Assert.Equal(40, r.Plan.Count);
        Assert.All(files.Opened, s => Assert.True(s.Closed));
    }

    [Fact]
    public void LoadClearsTheCopyOfTheKeyWhateverHappens()
    {
        byte[] container = Encrypt(GrassTestPack.Build(Yard), Yard);
        // success
        byte[] copy = (byte[])Key.Clone();
        GrassInputs ok = GrassPreparation.Load(null, Yard, new TrackedStream(container), true, copy, CryptoProviders.Managed, GrassArtKind.Pack);
        Assert.True(ok.Ok, ok.Error);
        Assert.All(copy, b => Assert.Equal(0, b));
        // a wrong key fails in the stream's constructor: the copy is cleared there too
        byte[] wrong = (byte[])Key.Clone();
        wrong[0] ^= 0xFF;
        byte[] wrongCopy = (byte[])wrong.Clone();
        GrassInputs refused = GrassPreparation.Load(null, Yard, new TrackedStream(container), true, wrongCopy, CryptoProviders.Managed, GrassArtKind.Pack);
        Assert.Equal("container:HeaderAuth", refused.Error);
        Assert.All(wrongCopy, b => Assert.Equal(0, b));
        // a pack the reader refuses
        byte[] otherCopy = (byte[])Key.Clone();
        byte[] bowlContainer = Encrypt(GrassTestPack.Build("arena_bowl"), Yard);          // a container of the yard that holds a bowl pack
        GrassInputs mismatch = GrassPreparation.Load(null, Yard, new TrackedStream(bowlContainer), true, otherCopy, CryptoProviders.Managed, GrassArtKind.Pack);
        Assert.Equal("pack-format:MapKeyMismatch", mismatch.Error);
        Assert.All(otherCopy, b => Assert.Equal(0, b));
    }

    [Fact]
    public void AContainerOfAnotherMapIsRefused()
    {
        byte[] container = Encrypt(GrassTestPack.Build("arena_bowl"), "arena_bowl");
        GrassInputs r = Finish(Start(With(container)));                     // opened for the yard with the same key
        Assert.Equal("container:WrongMap", r.Error);
        Assert.False(r.Ok);
        Assert.Null(r.TexArray);
    }

    [Fact]
    public void AWrongKeyIsRefusedByTheHeaderCheck()
    {
        byte[] container = Encrypt(GrassTestPack.Build(Yard), Yard);
        byte[] wrong = (byte[])Key.Clone();
        wrong[31] ^= 1;
        GrassInputs r = Finish(Start(With(container), (string map, out byte[] key) => { key = wrong; return true; }));
        Assert.Equal("container:HeaderAuth", r.Error);
    }

    [Fact]
    public void ADamagedPayloadChunkIsRefusedAndNothingIsHandedOver()
    {
        byte[] pack = GrassTestPack.Build(Yard, GrassTestPack.Placement(40));
        byte[] container = Encrypt(pack, Yard);
        AmpbHeader header = Ampb.Inspect(new MemoryStream(container)).Header;
        // the last chunk lies in the art: the header and the placement read fine, the first art read raises the integrity error
        long lastChunk = header.PayloadOffset + (long)(header.ChunkCount - 1) * header.ChunkSize;
        container[lastChunk + 3] ^= 0x40;
        GrassInputs r = Finish(Start(With(container)));
        Assert.Equal("container:ChunkIntegrity", r.Error);
        Assert.Null(r.TexArray);
        Assert.Null(r.NormalArray);
        Assert.Null(r.NormalTexture);
        // the first chunk holds the pack header: the same code, from the first read
        byte[] early = Encrypt(pack, Yard);
        early[header.PayloadOffset + 5] ^= 0x01;
        Assert.Equal("container:ChunkIntegrity", Finish(Start(With(early))).Error);
    }

    [Theory]
    [InlineData(10)]       // version / flags
    [InlineData(45)]       // map key
    [InlineData(100)]      // the chunk table
    public void ADamagedContainerHeaderIsRefusedWithAContainerCode(int at)
    {
        byte[] container = Encrypt(GrassTestPack.Build(Yard), Yard);
        container[at] ^= 0x01;
        GrassInputs r = Finish(Start(With(container)));
        Assert.StartsWith("container:", r.Error);
    }

    [Fact]
    public void ATruncatedContainerIsRefused()
    {
        byte[] container = Encrypt(GrassTestPack.Build(Yard), Yard);
        var shorter = new byte[container.Length - 100];
        Array.Copy(container, shorter, shorter.Length);
        GrassInputs r = Finish(Start(With(shorter)));
        Assert.StartsWith("container:", r.Error);
    }

    // ---- refusals before the worker -----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AMissingFileCompletesAtOnceAsPackMissingWithoutAnyKey()
    {
        var files = new FakeFiles();
        GrassPreparation p = Start(files, NoKeyNeeded);
        Assert.True(p.IsCompleted);                                         // no worker was needed
        Assert.Equal("pack-missing", p.Result.Error);
        Assert.False(p.Result.Ok);
        Assert.Equal(1, files.Opens);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void AnUnreadableFileIsPackUnreadableWithTheExceptionTypeOnly(Type type)
    {
        var files = new FakeFiles { Throws = (Exception)Activator.CreateInstance(type, "C:\\Users\\player\\secret\\arena_yard_grass.bundle") };
        GrassPreparation p = Start(files, NoKeyNeeded);
        Assert.True(p.IsCompleted);
        Assert.Equal("pack-unreadable:" + type.Name, p.Result.Error);
        Assert.DoesNotContain("secret", p.Result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AKeyThatCannotBeRecoveredIsKeyUnavailableAndTheFileIsClosed(int how)
    {
        byte[] container = Encrypt(GrassTestPack.Build(Yard), Yard);
        var files = With(container);
        GrassKeyResolver resolver = how switch
        {
            0 => (string map, out byte[] key) => { key = null; return false; },
            1 => (string map, out byte[] key) => { key = Key; return false; },
            _ => (string map, out byte[] key) => { key = new byte[0]; return true; },
        };
        GrassPreparation p = Start(files, resolver);
        Assert.True(p.IsCompleted);
        Assert.Equal("key-unavailable", p.Result.Error);
        Assert.All(files.Opened, s => Assert.True(s.Closed));
    }

    [Fact]
    public void AResolverThatThrowsIsAPackUnreadableRefusalNotAnException()
    {
        var files = With(Encrypt(GrassTestPack.Build(Yard), Yard));
        GrassPreparation p = Start(files, (string map, out byte[] key) => throw new InvalidOperationException("boom"));
        Assert.True(p.IsCompleted);
        Assert.Equal("pack-unreadable:InvalidOperationException", p.Result.Error);
        Assert.All(files.Opened, s => Assert.True(s.Closed));
    }

    // ---- the pack and its payloads ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void APackOfAnotherMapIsRefusedByItsHeader()
    {
        GrassInputs r = Finish(Start(With(GrassTestPack.Build("arena_bowl")), NoKeyNeeded));
        Assert.Equal("pack-format:MapKeyMismatch", r.Error);
    }

    [Fact]
    public void ANonPackFileIsRefusedAsBadMagic()
    {
        var unity = new byte[5000];
        "UnityFS\0"u8.CopyTo(unity);
        Assert.Equal("pack-format:BadMagic", Finish(Start(With(unity), NoKeyNeeded)).Error);
        Assert.Equal("pack-format:TooShort", Finish(Start(With(new byte[10]), NoKeyNeeded)).Error);
        Assert.Equal("pack-format:TooShort", Finish(Start(With(new byte[0]), NoKeyNeeded)).Error);
    }

    [Fact]
    public void ATruncatedAndAnExtendedPlainPackAreRefused()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        var shorter = new byte[pack.Length - 1];
        Array.Copy(pack, shorter, shorter.Length);
        Assert.Equal("pack-format:FileLength", Finish(Start(With(shorter), NoKeyNeeded)).Error);
        var longer = new byte[pack.Length + 1];
        Array.Copy(pack, longer, pack.Length);
        Assert.Equal("pack-format:FileLength", Finish(Start(With(longer), NoKeyNeeded)).Error);
    }

    [Fact]
    public void ABrokenPlacementRecordIsNamedWithItsIndexAndNoContent()
    {
        byte[] records = GrassTestPack.Placement(40, 5, (f, i) => { if (i == 3) BitConverter.GetBytes(float.NaN).CopyTo(f, i * 68 + 4 + 4 * 12); });
        GrassInputs r = Finish(Start(With(GrassTestPack.Build(Yard, records)), NoKeyNeeded));
        Assert.Equal("placement:NonFiniteMatrix@3", r.Error);
        Assert.Null(r.Plan);
        byte[] slice = GrassTestPack.Placement(40, 5, (f, i) => { if (i == 9) BitConverter.GetBytes(5).CopyTo(f, i * 68); });
        Assert.Equal("placement:SliceOutOfRange@9", Finish(Start(With(GrassTestPack.Build(Yard, slice)), NoKeyNeeded)).Error);
    }

    [Fact]
    public void AWipedAlbedoIsConstantDataAndAScrambledOneIsIncoherent()
    {
        GrassTestPack.Shape shape = GrassTestPack.SmallShape;
        byte[] wiped = new byte[GrassTestPack.SizedRange(shape.Width, shape.Height, 5, shape.Mips, GrassBlockFormat.Dxt5).Size];
        Assert.Equal("art-blocks:GrassTexArray:ConstantData", Finish(Start(With(GrassTestPack.Build(Yard, albedo: wiped)), NoKeyNeeded)).Error);
        GrassArtRange range = GrassTestPack.SizedRange(shape.Width, shape.Height, 5, shape.Mips, GrassBlockFormat.Dxt5);
        byte[] scrambled = GrassTestPack.Art(shape, 5, GrassBlockFormat.Dxt5);
        var rng = new Random(3);
        for (int mip = 2; mip <= 5; mip++)
            for (long k = 0; k < range.MipBytes(mip); k++) scrambled[range.ElementOffset(2, mip) + k] = (byte)rng.Next(256);
        Assert.Equal("art-blocks:GrassTexArray:MipChainIncoherent", Finish(Start(With(GrassTestPack.Build(Yard, albedo: scrambled)), NoKeyNeeded)).Error);
        // the normal array and the normal texture are checked too
        byte[] wipedNormal = new byte[GrassTestPack.SizedRange(shape.Width, shape.Height, 5, shape.Mips, GrassBlockFormat.Dxt1).Size];
        Assert.Equal("art-blocks:GrassNormalArray:ConstantData", Finish(Start(With(GrassTestPack.Build(Yard, normalArray: wipedNormal)), NoKeyNeeded)).Error);
        byte[] wipedTexture = new byte[GrassTestPack.SizedRange(shape.Width, shape.Height, 1, shape.Mips, GrassBlockFormat.Dxt1).Size];
        Assert.Equal("art-blocks:Grass_new_1_N:ConstantData", Finish(Start(With(GrassTestPack.Build(Yard, normalTexture: wipedTexture)), NoKeyNeeded)).Error);
    }

    // ---- the placeholder ----------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePlaceholderReadsNoArtFromThePackAndStillTakesItsPlacementAndShapes()
    {
        GrassTestPack.Shape shape = GrassTestPack.SmallShape;
        // the pack's art is wiped: with the pack's art the build would be refused, with the placeholder it is not read
        byte[] wiped = new byte[GrassTestPack.SizedRange(shape.Width, shape.Height, 5, shape.Mips, GrassBlockFormat.Dxt5).Size];
        byte[] pack = GrassTestPack.Build(Yard, GrassTestPack.Placement(40), albedo: wiped);
        GrassInputs r = Finish(Start(With(pack), NoKeyNeeded, GrassArtKind.Placeholder));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Placeholder", r.ArtName);
        Assert.Equal("pack=ok placement=ok art=generated", r.Checks);
        Assert.Equal(272L + 40 * 68, r.BytesRead);                           // the header and the placement only
        Assert.Equal(40, r.Plan.Count);
        var expected = new byte[r.Layout.TexArray.Size];
        NativeGrassArt.Placeholder.Fill(expected, r.Layout.TexArray);
        Assert.Equal(expected, r.TexArray);
        Assert.Equal(r.Layout.NormalTexture.Size, r.NormalTexture.Length);
    }

    // ---- the worker itself --------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnAbandonedPreparationStopsBetweenTwoReads()
    {
        GrassPreparation owner = Start(new FakeFiles());                    // any instance: Load only reads Abandoned
        owner.Abandon();
        Assert.True(owner.Abandoned);
        GrassInputs r = GrassPreparation.Load(owner, Yard, new TrackedStream(GrassTestPack.Build(Yard)), false, null, null, GrassArtKind.Pack);
        Assert.Equal("worker:abandoned", r.Error);
        Assert.Null(r.TexArray);
    }

    [Fact]
    public void ALoadNeverThrowsAndNamesOnlyTheExceptionType()
    {
        var stream = new ThrowingStream(GrassTestPack.Build(Yard));
        GrassInputs r = GrassPreparation.Load(null, Yard, stream, false, null, null, GrassArtKind.Pack);
        Assert.Equal("io:InvalidOperationException", r.Error);
        Assert.DoesNotContain("secret", r.Error);
        Assert.Null(r.TexArray);
    }

    [Fact]
    public async Task ManyPreparationsRunTogetherWithoutSharingAnything()
    {
        byte[] pack = GrassTestPack.Build(Yard, GrassTestPack.Placement(300));
        byte[] container = Encrypt(pack, Yard);
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var files = With(i % 2 == 0 ? pack : container);
            return Finish(Start(files, i % 2 == 0 ? NoKeyNeeded : null));
        })).ToArray();
        GrassInputs[] results = await Task.WhenAll(tasks);
        Assert.All(results, x => { Assert.True(x.Ok, x.Error); Assert.Equal(300, x.Plan.Count); });
    }

    [Fact]
    public void ResultIsNullWhileAnUnfinishedWorkerRunsAndTheMillisecondsConvert()
    {
        Assert.Equal(1000.0, GrassInputs.Ms(Stopwatch.Frequency), 3);
        Assert.Equal(0.0, GrassInputs.Ms(0), 3);
        // a faulted or cancelled task cannot happen (the body catches everything); a finished one answers
        GrassPreparation p = Start(new FakeFiles());
        Assert.NotNull(p.Result);
    }
}
