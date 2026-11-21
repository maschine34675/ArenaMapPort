#nullable disable
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The facts of the grass art and its checks (PLAN-YARD item 23, stage 2): the mip-chain arithmetic, the ranges as the pack states them, the structure checks that need no hash and the BSG-free placeholder
/// art. The numbers of Arena's real art (1024 x 1024, eleven mips, five slices) were read from the installed Arena 0.6.2.0.48473 on 2026-11-21; since stage 2 no offset or size is pinned in the client,
/// the pack states them and <see cref="NativeGrassPackModelTests"/> / <see cref="NativeGrassPackRealDataTests"/> check them. Unit-tested; nothing here is host-run.
/// </summary>
public sealed class NativeGrassArtModelTests
{
    static GrassArtRange TexArray => GrassTestPack.RealLayout().TexArray;
    static GrassArtRange NormalArray => GrassTestPack.RealLayout().NormalArray;
    static GrassArtRange NormalTexture => GrassTestPack.RealLayout().NormalTexture;

    // ---- the mip-chain arithmetic -------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADxt5ChainOf1024ElevenMipsSumsToThePerSliceSizeOfTheRealFile()
    {
        GrassArtRange r = TexArray;
        long[] expected = { 1048576, 262144, 65536, 16384, 4096, 1024, 256, 64, 16, 16, 16 };
        for (int m = 0; m < 11; m++) Assert.Equal(expected[m], r.MipBytes(m));
        Assert.Equal(1398128L, r.SliceBytes);
        Assert.Equal(6990640L, r.ComputedSize);
        Assert.Equal(6990640L, r.Size);
    }

    [Fact]
    public void ADxt1ChainIsHalfAndThe4x4MipsArePaddedToOneBlock()
    {
        GrassArtRange r = NormalArray;
        long[] expected = { 524288, 131072, 32768, 8192, 2048, 512, 128, 32, 8, 8, 8 };
        for (int m = 0; m < 11; m++) Assert.Equal(expected[m], r.MipBytes(m));
        Assert.Equal(699064L, r.SliceBytes);
        Assert.Equal(3495320L, r.ComputedSize);
        Assert.Equal(699064L, NormalTexture.SliceBytes);
        Assert.Equal(699064L, NormalTexture.ComputedSize);
        // the last three mips are 4 x 4, 2 x 2 and 1 x 1 pixels and each takes a whole block
        Assert.Equal(4, r.MipWidth(8));
        Assert.Equal(2, r.MipWidth(9));
        Assert.Equal(1, r.MipWidth(10));
        Assert.Equal(1, r.BlocksAcross(9));
        Assert.Equal(1, r.BlocksDown(10));
        Assert.Equal(256, r.BlocksAcross(0));
    }

    [Fact]
    public void TheOffsetsAreSliceMajorWithTheWholeChainPerSlice()
    {
        GrassArtRange r = TexArray;
        Assert.Equal(0L, r.ElementOffset(0, 0));
        Assert.Equal(1048576L, r.ElementOffset(0, 1));
        Assert.Equal(1048576L + 262144L, r.ElementOffset(0, 2));
        Assert.Equal(1398128L, r.ElementOffset(1, 0));                       // slice 1 starts after slice 0's whole chain, not after slice 0's mip 0 of every slice
        Assert.Equal(1398128L + 1048576L, r.ElementOffset(1, 1));
        Assert.Equal(4L * 1398128L, r.ElementOffset(4, 0));
        Assert.Equal(6990640L - 16L, r.ElementOffset(4, 10));                // the last 1 x 1 mip of the last slice is the last block of the range
        Assert.Equal(r.Offset + 1398128L, r.AbsoluteOffset(1, 0));
        Assert.Equal(r.Size, r.ElementOffset(4, 10) + r.MipBytes(10));
        for (int s = 0; s < 5; s++)
            for (int m = 0; m < 11; m++)
                Assert.True(r.ElementOffset(s, m) + r.MipBytes(m) <= r.Size);
    }

    [Fact]
    public void EveryElementOffsetIsDistinctAndTilesTheRangeExactly()
    {
        foreach (GrassArtRange r in GrassTestPack.RealLayout().Ranges)
        {
            long next = 0;
            for (int s = 0; s < r.Slices; s++)
            {
                for (int m = 0; m < r.MipCount; m++)
                {
                    Assert.Equal(next, r.ElementOffset(s, m));
                    next += r.MipBytes(m);
                }
            }
            Assert.Equal(r.Size, next);
        }
    }

    // ---- the ranges as a pack states them -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheThreeRangesCarryTheFactsOfTheirEntries()
    {
        GrassPackLayout layout = GrassTestPack.RealLayout();
        GrassArtRange[] ranges = layout.Ranges;
        Assert.Equal(new[] { "GrassTexArray", "GrassNormalArray", "Grass_new_1_N" }, ranges.Select(r => r.Name).ToArray());
        Assert.All(ranges, r => { Assert.Equal(1024, r.Width); Assert.Equal(1024, r.Height); Assert.Equal(11, r.MipCount); });
        Assert.Equal(new[] { 5, 5, 1 }, ranges.Select(r => r.Slices).ToArray());
        Assert.Equal(new[] { true, true, false }, ranges.Select(r => r.IsArray).ToArray());
        Assert.Equal(GrassBlockFormat.Dxt5, layout.TexArray.Format);
        Assert.Equal(GrassBlockFormat.Dxt1, layout.NormalArray.Format);
        Assert.Equal(GrassBlockFormat.Dxt1, layout.NormalTexture.Format);
        Assert.Equal("DXT5", layout.TexArray.FormatName);
        Assert.Equal("DXT1", layout.NormalArray.FormatName);
        Assert.Equal(16, layout.TexArray.BlockBytes);
        Assert.Equal(8, layout.NormalArray.BlockBytes);
        Assert.True(layout.TexArray.IgnoreMipmapLimit);
        Assert.True(layout.NormalArray.IgnoreMipmapLimit);
        Assert.False(layout.NormalTexture.IgnoreMipmapLimit);
        Assert.All(ranges, r => { Assert.Equal(2, r.FilterMode); Assert.Equal(1, r.WrapMode); });
        // the pack, not the client, says where each range is: the first starts after the header and the placement
        Assert.Equal(272L + 688976L, layout.TexArray.Offset);
        Assert.Equal(layout.TexArray.Offset + layout.TexArray.Size, layout.NormalArray.Offset);
        Assert.Equal(layout.NormalArray.Offset + layout.NormalArray.Size, layout.NormalTexture.Offset);
    }

    [Fact]
    public void ThePinnedConstantsOfStage1AreGone()
    {
        // the offsets, sizes, file paths and file sizes of Arena's sharedassets95 left the client in stage 2 (the grass bundle states every range); a source pin in NativeGrassSourcePinTests
        // keeps them out of the grass files, and this test keeps the public surface honest
        var names = typeof(NativeGrassArt).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(f => f.Name).ToArray();
        foreach (string gone in new[] { "StreamPath", "AssetsPath", "AssetsSize", "StreamSize", "TexArrayOffset", "NormalArrayOffset", "NormalTextureOffset", "TexArraySize", "NormalArraySize", "StreamTailPadding", "Ranges", "TexArray", "NormalArray", "NormalTexture" })
            Assert.DoesNotContain(gone, names);
    }

    [Fact]
    public void ARangeWithTheWrongSizeIsCaughtByTheArithmetic()
    {
        // the same arithmetic the pack reader runs, on a made-up range
        var shortRange = new GrassArtRange("x", 0, 1000, 1024, 1024, 5, 11, GrassBlockFormat.Dxt5, true, 2, 1, 1, true);
        Assert.NotEqual(shortRange.Size, shortRange.ComputedSize);
        var one = new GrassArtRange("x", 0, 1398128, 1024, 1024, 1, 11, GrassBlockFormat.Dxt5, false, 2, 1, 1, false);
        Assert.Equal(one.Size, one.ComputedSize);
    }

    // ---- structure checks ---------------------------------------------------------------------------------------------------------------------------------------------------

    static byte[] Placeholder(GrassArtRange range)
    {
        var data = new byte[range.Size];
        NativeGrassArt.Placeholder.Fill(data, range);
        return data;
    }

    [Fact]
    public void AWrongLengthIsAShortRead()
    {
        GrassArtRange r = NormalArray;
        Assert.Equal(GrassArtError.ShortRead, NativeGrassArt.CheckBlocks(null, r));
        Assert.Equal(GrassArtError.ShortRead, NativeGrassArt.CheckBlocks(new byte[10], r));
        Assert.Equal(GrassArtError.ShortRead, NativeGrassArt.CheckBlocks(new byte[r.Size + 1], r));
    }

    [Fact]
    public void AWipedOrRepeatedByteRangeIsConstantData()
    {
        var zero = new byte[TexArray.Size];
        Assert.Equal(GrassArtError.ConstantData, NativeGrassArt.CheckBlocks(zero, TexArray));
        var ff = new byte[NormalArray.Size];
        Array.Fill(ff, (byte)0xFF);
        Assert.Equal(GrassArtError.ConstantData, NativeGrassArt.CheckBlocks(ff, NormalArray));
    }

    [Fact]
    public void RandomBytesAreAnIncoherentMipChain()
    {
        var data = new byte[TexArray.Size];
        new Random(5).NextBytes(data);
        Assert.Equal(GrassArtError.MipChainIncoherent, NativeGrassArt.CheckBlocks(data, TexArray));
    }

    [Fact]
    public void ARangeWhoseCoarseMipsAreScrambledIsIncoherent()
    {
        // build a coherent range from the placeholder, then scramble the coarse mips of slice 1
        GrassArtRange r = TexArray;
        byte[] data = Placeholder(r);
        Assert.Equal(GrassArtError.None, NativeGrassArt.CheckBlocks(data, r));
        var copy = (byte[])data.Clone();
        var rng = new Random(9);
        for (int mip = 2; mip <= 7; mip++)
        {
            long m = r.ElementOffset(1, mip);
            for (long k = 0; k < r.MipBytes(mip); k++) copy[m + k] = (byte)rng.Next(256);
        }
        Assert.Equal(GrassArtError.MipChainIncoherent, NativeGrassArt.CheckBlocks(copy, r));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ThePlaceholderOfEveryRangePassesTheStructureCheck(int range)
    {
        GrassArtRange r = GrassTestPack.RealLayout().Ranges[range];
        Assert.Equal(GrassArtError.None, NativeGrassArt.CheckBlocks(Placeholder(r), r));
    }

    [Fact]
    public void ATextureWithFewerThanFourMipsIsCheckedAtItsLastMipAndNeverReadsOutOfRange()
    {
        // any chain the pack format admits must be checkable: three mips, two mips, one mip
        foreach (int mips in new[] { 1, 2, 3 })
        {
            GrassArtRange r = GrassTestPack.SizedRange(16, 16, 2, mips, GrassBlockFormat.Dxt5);
            var data = new byte[r.Size];
            NativeGrassArt.Placeholder.Fill(data, r);
            Assert.Equal(GrassArtError.None, NativeGrassArt.CheckBlocks(data, r));
            Assert.Equal(GrassArtError.ConstantData, NativeGrassArt.CheckBlocks(new byte[data.Length], r));
        }
    }

    [Fact]
    public void TheMeanBlockColourOfASolidBlockIsItsEndpointColour()
    {
        var blocks = new byte[16];
        // DXT5 block: alpha bytes first, then colour: c0 = c1 = RGB565 (255, 0, 0) = 0xF800, indices 0
        blocks[8] = 0x00; blocks[9] = 0xF8; blocks[10] = 0x00; blocks[11] = 0xF8;
        var rgb = new float[3];
        NativeGrassArt.BlockMean(blocks, 0, GrassBlockFormat.Dxt5, rgb);
        Assert.Equal(255f, rgb[0], 3);
        Assert.Equal(0f, rgb[1], 3);
        Assert.Equal(0f, rgb[2], 3);
        // DXT1 with c0 > c1 (four-colour mode) and all indices 3 = (c0 + 2 c1) / 3: c0 = white (0xFFFF), c1 = black
        var dxt1 = new byte[8] { 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };
        NativeGrassArt.BlockMean(dxt1, 0, GrassBlockFormat.Dxt1, rgb);
        Assert.Equal(255f / 3f, rgb[0], 2);
        Assert.Equal(255f / 3f, rgb[1], 2);
        Assert.Equal(255f / 3f, rgb[2], 2);
        // the same endpoints with c0 <= c1 (three-colour mode) and all indices 3 = transparent black
        var dxt1Alpha = new byte[8] { 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        NativeGrassArt.BlockMean(dxt1Alpha, 0, GrassBlockFormat.Dxt1, rgb);
        Assert.Equal(0f, rgb[0], 3);
    }

    [Fact]
    public void TheCoherenceNumberOfASolidPlaceholderIsZero()
    {
        GrassArtRange r = TexArray;
        byte[] data = Placeholder(r);
        for (int s = 0; s < 5; s++) Assert.Equal(0.0, NativeGrassArt.MipChainDifference(data, 0, r, s), 3);
    }

    // ---- the placeholder art ------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePlaceholderIsDeterministicAndFillsTheBufferItIsGiven()
    {
        GrassArtRange r = NormalArray;
        byte[] a = Placeholder(r);
        var b = new byte[r.Size];
        Array.Fill(b, (byte)0xAB);                                           // a dirty buffer is cleared first
        NativeGrassArt.Placeholder.Fill(b, r);
        Assert.Equal(a, b);
        Assert.Throws<ArgumentException>(() => NativeGrassArt.Placeholder.Fill(new byte[10], r));
        Assert.Throws<ArgumentException>(() => NativeGrassArt.Placeholder.Fill(null, r));
    }

    [Fact]
    public void ThePlaceholderAlbedoHasOneSolidColourPerSliceInEveryMip()
    {
        GrassArtRange r = TexArray;
        byte[] data = Placeholder(r);
        var rgb = new float[3];
        for (int s = 0; s < 5; s++)
        {
            byte[] c = NativeGrassArt.Placeholder.SliceColors[s];
            for (int mip = 0; mip < 11; mip += 5)
            {
                NativeGrassArt.BlockMean(data, (int)r.ElementOffset(s, mip), GrassBlockFormat.Dxt5, rgb);
                Assert.Equal(c[0], rgb[0], 0);
                Assert.Equal(c[1], rgb[1], 0);
                Assert.Equal(c[2], rgb[2], 0);
            }
        }
        // the five slice colours are five different colours
        Assert.Equal(5, NativeGrassArt.Placeholder.SliceColors.Select(c => c[0] * 65536 + c[1] * 256 + c[2]).Distinct().Count());
    }

    [Fact]
    public void ThePlaceholderCutOutShowsSlicePlusOneBladesInMipZero()
    {
        GrassArtRange r = TexArray;
        byte[] data = Placeholder(r);
        int bw = r.BlocksAcross(0), bh = r.BlocksDown(0);
        for (int s = 0; s < 5; s++)
        {
            // count runs of opaque blocks along the block row at 1/4 of the height
            int by = bh / 4;
            int runs = 0;
            bool inRun = false;
            long at = r.ElementOffset(s, 0);
            for (int bx = 0; bx < bw; bx++)
            {
                byte alpha = data[at + ((long)by * bw + bx) * 16];
                Assert.True(alpha == 0 || alpha == 255);
                Assert.Equal(alpha, data[at + ((long)by * bw + bx) * 16 + 1]);       // alpha0 == alpha1: every texel of the block is that alpha
                bool opaque = alpha == 255;
                if (opaque && !inRun) runs++;
                inRun = opaque;
            }
            Assert.Equal(s + 1, runs);
        }
    }

    [Fact]
    public void ThePlaceholderBladesNarrowTowardsTheTipAndTheCoarseMipsAreAllOpaque()
    {
        int bw = 256;
        int top = 0, bottom = 0;
        for (int bx = 0; bx < bw; bx++)
        {
            if (NativeGrassArt.Placeholder.Opaque(0, bx, 10, bw, bw)) top++;
            if (NativeGrassArt.Placeholder.Opaque(0, bx, 245, bw, bw)) bottom++;
        }
        Assert.True(top > bottom && bottom > 0, top + " vs " + bottom);
        for (int s = 0; s < 5; s++)
            for (int by = 0; by < NativeGrassArt.Placeholder.OpaqueGrid; by++)
                for (int bx = 0; bx < NativeGrassArt.Placeholder.OpaqueGrid; bx++)
                    Assert.True(NativeGrassArt.Placeholder.Opaque(s, bx, by, NativeGrassArt.Placeholder.OpaqueGrid, NativeGrassArt.Placeholder.OpaqueGrid));
    }

    [Fact]
    public void ThePlaceholderNormalsAreFlat()
    {
        GrassArtRange r = NormalArray;
        byte[] data = Placeholder(r);
        var rgb = new float[3];
        for (int s = 0; s < 5; s++)
        {
            NativeGrassArt.BlockMean(data, (int)r.ElementOffset(s, 0), GrassBlockFormat.Dxt1, rgb);
            // RGB565 cannot hold 128 exactly: 5 bits give 131.6 (16/31), 6 bits 129.5 (32/63)
            Assert.Equal(16 * 255f / 31f, rgb[0], 2);
            Assert.Equal(32 * 255f / 63f, rgb[1], 2);
            Assert.Equal(255f, rgb[2], 2);
        }
        Assert.Equal(0xF800, (int)NativeGrassArt.Placeholder.Rgb565(255, 0, 0));
        Assert.Equal(0xFFFF, (int)NativeGrassArt.Placeholder.Rgb565(255, 255, 255));
        Assert.Equal(0, (int)NativeGrassArt.Placeholder.Rgb565(0, 0, 0));
    }

    [Fact]
    public void ThePlaceholderContainsNoByteOfArena()
    {
        // it is generated from the arithmetic above: the same range filled twice is identical and uses only the few block patterns the generator writes
        byte[] data = Placeholder(TexArray);
        var distinct = new HashSet<byte>(data);
        Assert.True(distinct.Count <= 12, "distinct byte values " + distinct.Count);
    }

    [Fact]
    public void ThePlaceholderServesAnySliceCountOfAPack()
    {
        // the colours repeat after five slices: a pack with more slices than the five of Arena's art still gets art
        GrassArtRange r = GrassTestPack.SizedRange(64, 64, 7, 6, GrassBlockFormat.Dxt5);
        var data = new byte[r.Size];
        NativeGrassArt.Placeholder.Fill(data, r);
        var rgb = new float[3];
        NativeGrassArt.BlockMean(data, (int)r.ElementOffset(5, 0), GrassBlockFormat.Dxt5, rgb);
        Assert.Equal(NativeGrassArt.Placeholder.SliceColors[0][0], rgb[0], 0);
    }

    [Fact]
    public void TheDescriptionNamesTheRangeAndNoContent()
    {
        GrassPackLayout layout = GrassTestPack.RealLayout();
        Assert.Equal("GrassTexArray 1024x1024x5 mips=11 DXT5 @" + (272L + 688976L) + "+6990640", NativeGrassArt.Describe(layout.TexArray));
        Assert.Equal("Grass_new_1_N 1024x1024 mips=11 DXT1 @" + (272L + 688976L + 6990640L + 3495320L) + "+699064", NativeGrassArt.Describe(layout.NormalTexture));
    }
}
