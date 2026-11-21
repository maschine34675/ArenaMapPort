#nullable disable
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Runs only with the installed Arena of a build that the pins were measured on (0.6.1.x, checked on 0.6.2.x; the folder is ARENAMAPPORT_ARENA_DIR, else the default install folder of the
/// other real-data tests). Reads three Arena files read-only, with sharing that cannot block Arena: nothing is written, copied or hashed. The same facts are re-read with UnityPy by
/// tools/tests/test_grass_art_ranges.py.
/// </summary>
internal sealed class RealGrassFactAttribute : FactAttribute
{
    public RealGrassFactAttribute()
    {
        string reason = RealGrass.SkipReason();
        if (reason != null) Skip = reason;
    }
}

internal static class RealGrass
{
    internal static string Root()
    {
        string env = Environment.GetEnvironmentVariable("ARENAMAPPORT_ARENA_DIR");
        return string.IsNullOrWhiteSpace(env) ? RealArena.Folder : env;
    }

    internal static string DataPath(string relative) => Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar));

    internal static string SkipReason()
    {
        string manifest = Path.Combine(Root(), "ConsistencyInfo");
        if (!File.Exists(manifest)) return "no Arena installation at " + Root();
        string version;
        try { version = Regex.Match(File.ReadAllText(manifest), "\"Version\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value; }
        catch (IOException) { return "ConsistencyInfo is not readable"; }
        if (!version.StartsWith("0.6.1.", StringComparison.Ordinal) && !version.StartsWith("0.6.2.", StringComparison.Ordinal))
            return "installed Arena " + version + " is not a build the grass pins were measured on (0.6.1.x, 0.6.2.x)";
        foreach (string relative in new[] { NativeGrassArt.StreamPath, NativeGrassArt.AssetsPath, NativeGrassTable.Yard.PlacementRelativePath })
            if (!File.Exists(DataPath(relative))) return relative + " is missing";
        return null;
    }

    internal static FileStream OpenRead(string relative) =>
        new FileStream(DataPath(relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    internal static byte[] ReadRange(string relative, long offset, long size)
    {
        using FileStream f = OpenRead(relative);
        var data = new byte[size];
        f.Seek(offset, SeekOrigin.Begin);
        int done = 0;
        while (done < data.Length)
        {
            int n = f.Read(data, done, data.Length - done);
            if (n <= 0) throw new IOException("short read");
            done += n;
        }
        return data;
    }
}

public sealed class NativeGrassRealDataTests
{
    [RealGrassFact]
    public void TheTwoArtFilesHaveThePinnedSizes()
    {
        long assets = new FileInfo(RealGrass.DataPath(NativeGrassArt.AssetsPath)).Length;
        long stream = new FileInfo(RealGrass.DataPath(NativeGrassArt.StreamPath)).Length;
        Assert.Equal(13940L, assets);
        Assert.Equal(11185040L, stream);
        Assert.Equal(GrassArtError.None, NativeGrassArt.CheckFileSizes(assets, stream));
    }

    [RealGrassFact]
    public void TheThreeRangesLieInsideTheStreamAndPassTheStructureCheck()
    {
        long stream = new FileInfo(RealGrass.DataPath(NativeGrassArt.StreamPath)).Length;
        Assert.Equal(GrassArtError.None, NativeGrassArt.CheckRanges());
        foreach (GrassArtRange r in NativeGrassArt.Ranges)
        {
            Assert.True(r.Offset + r.Size <= stream, r.Name);
            byte[] data = RealGrass.ReadRange(NativeGrassArt.StreamPath, r.Offset, r.Size);
            Assert.Equal(GrassArtError.None, NativeGrassArt.CheckBlocks(data, r));
        }
    }

    [RealGrassFact]
    public void TheCoherenceNumbersOfTheRealArraysAreSmallAndAShiftedReadIsRefused()
    {
        GrassArtRange r = NativeGrassArt.TexArray;
        byte[] data = RealGrass.ReadRange(NativeGrassArt.StreamPath, r.Offset, r.Size);
        for (int s = 0; s < r.Slices; s++)
        {
            double d = NativeGrassArt.MipChainDifference(data, 0, r, s);
            Assert.InRange(d, 0.0, 1.5);                                       // measured 0.84 to 1.07 (python prototype: mean over mips 2..7); the bound of the check is 2.0
        }
        // the same range read 8 bytes late (inside the next padding) is not a chain any more; it still fits in the file
        byte[] shifted = RealGrass.ReadRange(NativeGrassArt.StreamPath, r.Offset + 8, r.Size);
        Assert.Equal(GrassArtError.MipChainIncoherent, NativeGrassArt.CheckBlocks(shifted, r));
        // 32 bytes late (one block row of mip 2) reads 2.3 against the bound 2.0: still caught, with little margin
        byte[] shifted32 = RealGrass.ReadRange(NativeGrassArt.StreamPath, r.Offset + 32, r.Size);
        Assert.Equal(GrassArtError.MipChainIncoherent, NativeGrassArt.CheckBlocks(shifted32, r));
    }

    [RealGrassFact]
    public void ArenasYardFileParsesWithExactlyTenThousandOneHundredThirtyTwoRecordsAndTheMeasuredExtents()
    {
        NativeGrassMapRow row = NativeGrassTable.Yard;
        long length = new FileInfo(RealGrass.DataPath(row.PlacementRelativePath)).Length;
        Assert.Equal(row.ExpectedBytes, length);
        byte[] bytes = RealGrass.ReadRange(row.PlacementRelativePath, 0, length);
        GrassParseResult result = NativeGrassPlacement.Parse(bytes, bytes.Length, row);
        Assert.True(result.Ok, NativeGrassPlacement.Describe(result, bytes.Length));
        GrassPlan plan = result.Plan;
        Assert.Equal(10132, plan.Count);
        Assert.Equal("0/8/8103/1457/564", plan.HistogramText());
        // the extents S5 and G2 measured: x -66.95..9.23, y 5.90..6.30, z 17.39..117.81, scales 0.18..1.12 (the proof of the matrix mapping: translation = floats 12..14)
        Assert.InRange(plan.MinX, -66.96f, -66.94f);
        Assert.InRange(plan.MaxX, 9.22f, 9.24f);
        Assert.InRange(plan.MinY, 5.89f, 5.91f);
        Assert.InRange(plan.MaxY, 6.29f, 6.31f);
        Assert.InRange(plan.MinZ, 17.38f, 17.40f);
        Assert.InRange(plan.MaxZ, 117.80f, 117.82f);
        Assert.InRange(plan.MinScale, 0.18f, 0.19f);
        Assert.InRange(plan.MaxScale, 1.12f, 1.13f);
        // every record is a yaw-only matrix with a uniform scale (G2 section 7): the first and second column are perpendicular to the up axis, the up column is (0, s, 0)
        for (int i = 0; i < plan.Count; i += 97)
        {
            float s = plan.Element(i, 1, 1);
            Assert.True(s > 0f);
            Assert.Equal(0f, plan.Element(i, 0, 1), 4);
            Assert.Equal(0f, plan.Element(i, 2, 1), 4);
            Assert.Equal(s, (float)Math.Sqrt(plan.Element(i, 0, 0) * plan.Element(i, 0, 0) + plan.Element(i, 1, 0) * plan.Element(i, 1, 0) + plan.Element(i, 2, 0) * plan.Element(i, 2, 0)), 4);
        }
    }

    [RealGrassFact]
    public void AFileCutByOneRecordOrOneByteIsRefused()
    {
        NativeGrassMapRow row = NativeGrassTable.Yard;
        byte[] bytes = RealGrass.ReadRange(row.PlacementRelativePath, 0, row.ExpectedBytes);
        Assert.Equal(GrassPlacementError.LengthNotDivisible, NativeGrassPlacement.Parse(bytes, bytes.Length - 1, row).Error);
        Assert.True(NativeGrassPlacement.Parse(bytes, bytes.Length - 68, row).Ok);              // 10,131 records are still a sane count; the pin test above holds the exact one
    }
}
