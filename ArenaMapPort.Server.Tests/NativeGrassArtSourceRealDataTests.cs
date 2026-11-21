#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The whole worker path on the installed Arena (skipped like NativeGrassRealDataTests when no build the pins were measured on is installed): the real <see cref="DirectoryArenaFiles"/> (read-only,
/// sharing, path guards), the sizes, the pinned ranges, the structure checks and the placement parse, exactly what the glue's worker runs at raid start. Nothing is written, hashed or logged.
/// </summary>
public sealed class NativeGrassArtSourceRealDataTests
{
    [RealGrassFact]
    public void TheRealWorkerLoadsYardsPlacementAndAllThreeArtRangesFromTheInstalledArena()
    {
        GrassInputs inputs = GrassPreparation.Load(null, NativeGrassTable.Yard, new DirectoryArenaFiles(RealGrass.Root()), GrassArtKind.Arena);
        Assert.True(inputs.Ok, inputs.Error);
        Assert.Equal("Arena", inputs.ArtName);
        Assert.Equal(10132, inputs.Plan.Count);
        Assert.Equal("0/8/8103/1457/564", inputs.Plan.HistogramText());
        Assert.Equal(688976, inputs.PlacementLength);
        Assert.Equal(NativeGrassArt.NormalTexture.Size, inputs.NormalTexture.LongLength);
        Assert.Equal(NativeGrassArt.TexArray.Size, inputs.TexArray.LongLength);
        Assert.Equal(NativeGrassArt.NormalArray.Size, inputs.NormalArray.LongLength);
        Assert.Equal("sizes=ok ranges=ok blocks=3/3", inputs.Checks);
        Assert.Equal(NativeGrassArt.NormalTexture.Size + NativeGrassArt.TexArray.Size + NativeGrassArt.NormalArray.Size, inputs.BytesRead);
    }

    [RealGrassFact]
    public void TheRealWorkerWithPlaceholderArtReadsOnlyThePlacement()
    {
        GrassInputs inputs = GrassPreparation.Load(null, NativeGrassTable.Yard, new DirectoryArenaFiles(RealGrass.Root()), GrassArtKind.Placeholder);
        Assert.True(inputs.Ok, inputs.Error);
        Assert.Equal(0L, inputs.BytesRead);
        Assert.Equal(10132, inputs.Plan.Count);
    }

    [RealGrassFact]
    public void TheMatricesOfTheRealPlanKeepTheClumpsWhereTheFilePutsThem()
    {
        // the glue copies plan.Matrices[16 * i + 4 * column + row] into m{row}{column}: the translation of every record is floats 12..14 and lies in the measured Yard extent
        GrassInputs inputs = GrassPreparation.Load(null, NativeGrassTable.Yard, new DirectoryArenaFiles(RealGrass.Root()), GrassArtKind.Placeholder);
        Assert.True(inputs.Ok, inputs.Error);
        GrassPlan plan = inputs.Plan;
        for (int i = 0; i < plan.Count; i += 97)
        {
            float x = plan.Matrices[16 * i + 12], y = plan.Matrices[16 * i + 13], z = plan.Matrices[16 * i + 14];
            Assert.InRange(x, -67.5f, 9.5f);
            Assert.InRange(y, 5.8f, 6.4f);
            Assert.InRange(z, 17f, 118f);
            Assert.Equal(1f, plan.Matrices[16 * i + 15]);
        }
    }
}
