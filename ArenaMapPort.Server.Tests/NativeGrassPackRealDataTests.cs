#nullable disable
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The real grass packs of the build machine (work/build/grass/&lt;map key&gt;_grass.bundle, written by python tools/extract/extract_grass.py pack --map all from the installed Arena; ignored, never in git):
/// the header, the placement payload and the three art payloads through the same reader and worker code the client runs. The counts and histograms are those measured on Arena 0.6.2.0.48473 on 2026-11-21
/// (work/tmp/yard-grass/reports/G2 section 7). A machine without the packs skips these tests; ARENAMAPPORT_WORK_DIR points at another work folder (a worktree has none).
/// </summary>
public sealed class NativeGrassPackRealDataTests
{
    sealed class RealFactAttribute : FactAttribute
    {
        public RealFactAttribute(string mapKey)
        {
            if (!File.Exists(PathOf(mapKey) ?? string.Empty)) Skip = "work/build/grass/" + mapKey + "_grass.bundle does not exist (run extract_grass.py pack)";
        }
    }

    sealed class RealTheoryAttribute : TheoryAttribute
    {
        public RealTheoryAttribute()
        {
            if (!MapKeys.All(k => File.Exists(PathOf(k) ?? string.Empty))) Skip = "the grass packs under work/build/grass do not all exist (run extract_grass.py pack)";
        }
    }

    static readonly string[] MapKeys = { "arena_bowl", "arena_iceberg", "arena_yard" };

    static string PathOf(string mapKey)
    {
        string work = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrEmpty(work)) return System.IO.Path.Combine(work, "build", "grass", mapKey + "_grass.bundle");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "work", "build", "grass", mapKey + "_grass.bundle");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    sealed class DiskFiles : IGrassPackFiles
    {
        public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    static GrassInputs Load(string mapKey)
    {
        GrassPreparation p = GrassPreparation.Start(mapKey, PathOf(mapKey), new DiskFiles(), (string m, out byte[] key) => throw new InvalidOperationException("a plain pack needs no key"), () => null, GrassArtKind.Pack);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!p.IsCompleted)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120));
            Thread.Sleep(5);
        }
        return p.Result;
    }

    [RealFact("arena_yard")]
    public void YardsPackHas10132RecordsAndTheMeasuredHistogramAndRanges()
    {
        GrassInputs r = Load("arena_yard");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("arena_yard", r.Layout.MapKey);
        Assert.Equal(10132, r.Plan.Count);
        Assert.Equal(688976, r.PlacementLength);
        Assert.Equal(new[] { 0, 8, 8103, 1457, 564 }, r.Plan.SliceHistogram);
        Assert.Equal("0/8/8103/1457/564", r.Plan.HistogramText());
        // yaw-only, uniform, inside the measured box (x -66.95..9.23, y 5.90..6.30, z 17.39..117.81, scale 0.184..1.124)
        Assert.Equal(0f, r.Plan.MaxTiltDegrees, 1);
        Assert.InRange(r.Plan.MinScale, 0.18f, 0.19f);
        Assert.InRange(r.Plan.MaxScale, 1.12f, 1.13f);
        Assert.InRange(r.Plan.MinX, -67.0f, -66.9f);
        Assert.InRange(r.Plan.MaxX, 9.2f, 9.3f);
        Assert.InRange(r.Plan.MinZ, 17.3f, 17.4f);
        Assert.InRange(r.Plan.MaxZ, 117.8f, 117.9f);
    }

    [RealFact("arena_bowl")]
    public void BowlsPackHas113643RecordsAndTheMeasuredHistogramAndAFlatTilt()
    {
        GrassInputs r = Load("arena_bowl");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(113643, r.Plan.Count);
        Assert.Equal(7727724, r.PlacementLength);
        Assert.Equal(new[] { 0, 26265, 62125, 11315, 13938 }, r.Plan.SliceHistogram);
        Assert.InRange(r.Plan.MaxTiltDegrees, 0f, 3.5f);           // up to 2.6 degrees (G2 section 7)
    }

    [RealFact("arena_iceberg")]
    public void IcebergsPackHas15410RecordsTiltedUpToFiftyDegreesAndNonUniformlyScaled()
    {
        GrassInputs r = Load("arena_iceberg");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(15410, r.Plan.Count);
        Assert.Equal(1047880, r.PlacementLength);
        Assert.Equal(new[] { 6, 1064, 6357, 1301, 6682 }, r.Plan.SliceHistogram);
        Assert.InRange(r.Plan.MaxTiltDegrees, 49.9f, 50.1f);       // tilts up to 50.0 degrees (G2 section 7; the pack tool measured 50.0)
        Assert.InRange(r.Plan.MaxScale, 2.23f, 2.24f);             // a scale up to 2.236 on a single axis
    }

    [RealTheory]
    [InlineData("arena_bowl")]
    [InlineData("arena_iceberg")]
    [InlineData("arena_yard")]
    public void EveryRealPackHasArenasSharedArtShapesAndPassesTheStructureCheck(string mapKey)
    {
        GrassInputs r = Load(mapKey);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("pack=ok placement=ok blocks=3/3", r.Checks);
        Assert.Equal("plain", r.Container);
        Assert.Equal(5, r.Layout.SliceCount);
        Assert.Equal(new[] { 6990640L, 3495320L, 699064L }, r.Layout.Ranges.Select(x => x.Size).ToArray());
        Assert.All(r.Layout.Ranges, x => { Assert.Equal(1024, x.Width); Assert.Equal(1024, x.Height); Assert.Equal(11, x.MipCount); Assert.Equal(2, x.FilterMode); Assert.Equal(1, x.WrapMode); });
        Assert.Equal(GrassBlockFormat.Dxt5, r.Layout.TexArray.Format);
        Assert.Equal(GrassBlockFormat.Dxt1, r.Layout.NormalArray.Format);
        Assert.Equal(GrassBlockFormat.Dxt1, r.Layout.NormalTexture.Format);
        Assert.Equal(r.Layout.TexArray.Size, r.TexArray.Length);
        Assert.Equal(r.Layout.NormalArray.Size, r.NormalArray.Length);
        Assert.Equal(r.Layout.NormalTexture.Size, r.NormalTexture.Length);
        Assert.Equal(272L + r.PlacementLength + 6990640L + 3495320L + 699064L, new FileInfo(PathOf(mapKey)).Length);
        Assert.Equal(new FileInfo(PathOf(mapKey)).Length, r.BytesRead);
    }

    [RealTheory]
    [InlineData("arena_bowl")]
    [InlineData("arena_iceberg")]
    [InlineData("arena_yard")]
    public void ARealPackIsRefusedForAnyOtherMap(string mapKey)
    {
        string other = MapKeys.First(k => k != mapKey);
        GrassPreparation p = GrassPreparation.Start(other, PathOf(mapKey), new DiskFiles(), (string m, out byte[] key) => throw new InvalidOperationException("no key"), () => null, GrassArtKind.Pack);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!p.IsCompleted) { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60)); Thread.Sleep(5); }
        Assert.Equal("pack-format:MapKeyMismatch", p.Result.Error);
    }

    [RealTheory]
    [InlineData("arena_bowl")]
    [InlineData("arena_iceberg")]
    [InlineData("arena_yard")]
    public void TheRealPackIsAPlainPackNotAUnityBundleAndNotAContainer(string mapKey)
    {
        using var s = File.OpenRead(PathOf(mapKey));
        var magic = new byte[8];
        Assert.Equal(8, s.Read(magic, 0, 8));
        Assert.Equal("AMGP", System.Text.Encoding.ASCII.GetString(magic, 0, 4));
        Assert.Equal(1u, BitConverter.ToUInt32(magic, 4));
    }

    // The header of every real pack as tools/extract/grass_pack.py parses it (numbers only, read on 2026-11-21 from the three packs of Arena 0.6.2.0.48473): the C# reader must agree field for field.
    // (map key, records, file length, placement size, albedo offset, normal-array offset, normal-texture offset)
    [RealTheory]
    [InlineData("arena_bowl", 113643, 18913020L, 7727724L, 7727996L, 14718636L, 18213956L)]
    [InlineData("arena_iceberg", 15410, 12233176L, 1047880L, 1048152L, 8038792L, 11534112L)]
    [InlineData("arena_yard", 10132, 11874272L, 688976L, 689248L, 7679888L, 11175208L)]
    public void TheCSharpReaderAgreesWithThePythonReaderOnEveryHeaderField(string mapKey, int records, long fileLength, long placement, long albedoAt, long normalAt, long textureAt)
    {
        var head = new byte[NativeGrassPack.HeaderSize];
        using (var s = File.OpenRead(PathOf(mapKey))) Assert.Equal(head.Length, s.Read(head, 0, head.Length));
        Assert.Equal(GrassPackError.None, NativeGrassPack.Parse(head, head.Length, new FileInfo(PathOf(mapKey)).Length, mapKey, out GrassPackLayout l));
        Assert.Equal(fileLength, l.FileLength);
        Assert.Equal((mapKey, records, 272L, placement, 5), (l.MapKey, l.RecordCount, l.PlacementOffset, l.PlacementSize, l.SliceCount));
        Assert.Equal((albedoAt, 6990640L, 1024, 1024, 5, 11, GrassBlockFormat.Dxt5, 2, 1, 1), (l.TexArray.Offset, l.TexArray.Size, l.TexArray.Width, l.TexArray.Height, l.TexArray.Slices, l.TexArray.MipCount, l.TexArray.Format, l.TexArray.FilterMode, l.TexArray.WrapMode, l.TexArray.Aniso));
        Assert.Equal((normalAt, 3495320L, 1024, 1024, 5, 11, GrassBlockFormat.Dxt1, 2, 1, 1), (l.NormalArray.Offset, l.NormalArray.Size, l.NormalArray.Width, l.NormalArray.Height, l.NormalArray.Slices, l.NormalArray.MipCount, l.NormalArray.Format, l.NormalArray.FilterMode, l.NormalArray.WrapMode, l.NormalArray.Aniso));
        Assert.Equal((textureAt, 699064L, 1024, 1024, 1, 11, GrassBlockFormat.Dxt1, 2, 1, 16), (l.NormalTexture.Offset, l.NormalTexture.Size, l.NormalTexture.Width, l.NormalTexture.Height, l.NormalTexture.Slices, l.NormalTexture.MipCount, l.NormalTexture.Format, l.NormalTexture.FilterMode, l.NormalTexture.WrapMode, l.NormalTexture.Aniso));
    }
}
