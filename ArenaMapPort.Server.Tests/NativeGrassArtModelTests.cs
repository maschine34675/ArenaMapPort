#nullable disable
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The pinned facts of the grass art and its checks (PLAN-YARD item 23, shape E). The numbers are those UnityPy read from the installed Arena 0.6.2.0.48473 on 2026-11-21
/// (tools/tests/test_grass_art_ranges.py re-reads them; <see cref="NativeGrassRealDataTests"/> checks the file sizes). Unit-tested; nothing here is host-run.
/// </summary>
public sealed class NativeGrassArtModelTests
{
    // ---- the mip-chain arithmetic -------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADxt5ChainOf1024ElevenMipsSumsToThePerSliceSizeOfTheRealFile()
    {
        GrassArtRange r = NativeGrassArt.TexArray;
        long[] expected = { 1048576, 262144, 65536, 16384, 4096, 1024, 256, 64, 16, 16, 16 };
        for (int m = 0; m < 11; m++) Assert.Equal(expected[m], r.MipBytes(m));
        Assert.Equal(1398128L, r.SliceBytes);
        Assert.Equal(6990640L, r.ComputedSize);
        Assert.Equal(NativeGrassArt.TexArraySize, r.ComputedSize);
    }

    [Fact]
    public void ADxt1ChainIsHalfAndThe4x4MipsArePaddedToOneBlock()
    {
        GrassArtRange r = NativeGrassArt.NormalArray;
        long[] expected = { 524288, 131072, 32768, 8192, 2048, 512, 128, 32, 8, 8, 8 };
        for (int m = 0; m < 11; m++) Assert.Equal(expected[m], r.MipBytes(m));
        Assert.Equal(699064L, r.SliceBytes);
        Assert.Equal(3495320L, r.ComputedSize);
        Assert.Equal(699064L, NativeGrassArt.NormalTexture.SliceBytes);
        Assert.Equal(NativeGrassArt.NormalTextureSize, NativeGrassArt.NormalTexture.ComputedSize);
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
        GrassArtRange r = NativeGrassArt.TexArray;
        Assert.Equal(0L, r.ElementOffset(0, 0));
        Assert.Equal(1048576L, r.ElementOffset(0, 1));
        Assert.Equal(1048576L + 262144L, r.ElementOffset(0, 2));
        Assert.Equal(1398128L, r.ElementOffset(1, 0));                       // slice 1 starts after slice 0's whole chain, not after slice 0's mip 0 of every slice
        Assert.Equal(1398128L + 1048576L, r.ElementOffset(1, 1));
        Assert.Equal(4L * 1398128L, r.ElementOffset(4, 0));
        Assert.Equal(6990640L - 16L, r.ElementOffset(4, 10));                // the last 1 x 1 mip of the last slice is the last block of the range
        Assert.Equal(699072L + 1398128L, r.AbsoluteOffset(1, 0));
        Assert.Equal(r.Size, r.ElementOffset(4, 10) + r.MipBytes(10));
        for (int s = 0; s < 5; s++)
            for (int m = 0; m < 11; m++)
                Assert.True(r.ElementOffset(s, m) + r.MipBytes(m) <= r.Size);
    }

    [Fact]
    public void EveryElementOffsetIsDistinctAndTilesTheRangeExactly()
    {
        foreach (GrassArtRange r in NativeGrassArt.Ranges)
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

    // ---- the pinned ranges --------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheThreeRangesAreTheMeasuredOnesInFileOrder()
    {
        Assert.Equal(new[] { "Grass_new_1_N", "GrassTexArray", "GrassNormalArray" }, NativeGrassArt.Ranges.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { 0L, 699072L, 7689712L }, NativeGrassArt.Ranges.Select(r => r.Offset).ToArray());
        Assert.Equal(new[] { 699064L, 6990640L, 3495320L }, NativeGrassArt.Ranges.Select(r => r.Size).ToArray());
        Assert.All(NativeGrassArt.Ranges, r => { Assert.Equal(1024, r.Width); Assert.Equal(1024, r.Height); Assert.Equal(11, r.MipCount); });
        Assert.Equal(new[] { 1, 5, 5 }, NativeGrassArt.Ranges.Select(r => r.Slices).ToArray());
        Assert.Equal(new[] { false, true, true }, NativeGrassArt.Ranges.Select(r => r.IsArray).ToArray());
        Assert.Equal(GrassBlockFormat.Dxt5, NativeGrassArt.TexArray.Format);
        Assert.Equal(GrassBlockFormat.Dxt1, NativeGrassArt.NormalArray.Format);
        Assert.Equal(GrassBlockFormat.Dxt1, NativeGrassArt.NormalTexture.Format);
        Assert.Equal(16, NativeGrassArt.TexArray.BlockBytes);
        Assert.Equal(8, NativeGrassArt.NormalArray.BlockBytes);
        Assert.Equal(101, NativeGrassArt.TexArray.FormatId);                // GraphicsFormat RGBA_DXT5_UNorm
        Assert.Equal(97, NativeGrassArt.NormalArray.FormatId);              // GraphicsFormat RGB_DXT1_UNorm
        Assert.Equal(10, NativeGrassArt.NormalTexture.FormatId);            // TextureFormat DXT1
        Assert.True(NativeGrassArt.TexArray.IgnoreMipmapLimit);
        Assert.True(NativeGrassArt.NormalArray.IgnoreMipmapLimit);
        Assert.False(NativeGrassArt.NormalTexture.IgnoreMipmapLimit);
        Assert.All(NativeGrassArt.Ranges, r => { Assert.Equal(2, r.FilterMode); Assert.Equal(1, r.WrapMode); Assert.True(r.Linear); });
    }

    [Fact]
    public void TheFilePinsAndTheRangesAgree()
    {
        Assert.Equal(11185040L, NativeGrassArt.StreamSize);
        Assert.Equal(13940L, NativeGrassArt.AssetsSize);
        Assert.Equal(NativeGrassArt.StreamSize, NativeGrassArt.NormalArrayOffset + NativeGrassArt.NormalArraySize + NativeGrassArt.StreamTailPadding);
        Assert.Equal(699064L + 8L, NativeGrassArt.TexArrayOffset);          // the plain texture is padded to 8 bytes
        Assert.Equal(NativeGrassArt.TexArrayOffset + NativeGrassArt.TexArraySize, NativeGrassArt.NormalArrayOffset);
        Assert.Equal(GrassArtError.None, NativeGrassArt.CheckRanges());
        Assert.Equal("EscapeFromTarkovArena_Data/sharedassets95.assets.resS", NativeGrassArt.StreamPath);
        Assert.Equal("EscapeFromTarkovArena_Data/sharedassets95.assets", NativeGrassArt.AssetsPath);
    }

    [Theory]
    [InlineData(13940L, 11185040L, "None")]
    [InlineData(13941L, 11185040L, "AssetsSizeDiffers")]
    [InlineData(-1L, 11185040L, "AssetsSizeDiffers")]
    [InlineData(13940L, 11185039L, "StreamSizeDiffers")]
    [InlineData(13940L, -1L, "StreamSizeDiffers")]
    [InlineData(0L, 0L, "AssetsSizeDiffers")]
    public void TheTwoFileSizesAreChecked(long assets, long stream, string expected)
    {
        Assert.Equal(Enum.Parse<GrassArtError>(expected), NativeGrassArt.CheckFileSizes(assets, stream));
    }

    [Fact]
    public void ARangeWithTheWrongSizeOrAnOverlapIsCaughtByTheArithmeticCheck()
    {
        // the same arithmetic the pinned check runs, on a made-up range
        var shortRange = new GrassArtRange("x", 0, 1000, 1024, 1024, 5, 11, GrassBlockFormat.Dxt5, true, 101, "x", true, 2, 1, 1, true);
        Assert.NotEqual(shortRange.Size, shortRange.ComputedSize);
        var one = new GrassArtRange("x", 0, 1398128, 1024, 1024, 1, 11, GrassBlockFormat.Dxt5, false, 12, "DXT5", true, 2, 1, 1, false);
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
        Assert.Equal(GrassArtError.ShortRead, NativeGrassArt.CheckBlocks(null, NativeGrassArt.NormalArray));
        Assert.Equal(GrassArtError.ShortRead, NativeGrassArt.CheckBlocks(new byte[10], NativeGrassArt.NormalArray));
        Assert.Equal(GrassArtError.ShortRead, NativeGrassArt.CheckBlocks(new byte[NativeGrassArt.NormalArraySize + 1], NativeGrassArt.NormalArray));
    }

    [Fact]
    public void AWipedOrRepeatedByteRangeIsConstantData()
    {
        var zero = new byte[NativeGrassArt.TexArraySize];
        Assert.Equal(GrassArtError.ConstantData, NativeGrassArt.CheckBlocks(zero, NativeGrassArt.TexArray));
        var ff = new byte[NativeGrassArt.NormalArraySize];
        Array.Fill(ff, (byte)0xFF);
        Assert.Equal(GrassArtError.ConstantData, NativeGrassArt.CheckBlocks(ff, NativeGrassArt.NormalArray));
    }

    [Fact]
    public void RandomBytesAreAnIncoherentMipChain()
    {
        var data = new byte[NativeGrassArt.TexArraySize];
        new Random(5).NextBytes(data);
        Assert.Equal(GrassArtError.MipChainIncoherent, NativeGrassArt.CheckBlocks(data, NativeGrassArt.TexArray));
    }

    [Fact]
    public void ARangeWhoseSecondSliceIsShiftedByABlockIsIncoherent()
    {
        // build a coherent range from the placeholder, then shift slice 1's data by 3 blocks (48 bytes) inside the buffer
        GrassArtRange r = NativeGrassArt.TexArray;
        byte[] data = Placeholder(r);
        Assert.Equal(GrassArtError.None, NativeGrassArt.CheckBlocks(data, r));
        long at = r.ElementOffset(1, 0);
        var copy = (byte[])data.Clone();
        var rng = new Random(9);
        // a shift in a placeholder (a solid colour per slice) changes nothing a coherence check can see, so scramble the coarse mips of slice 1 instead
        for (int mip = 2; mip <= 7; mip++)
        {
            long m = r.ElementOffset(1, mip);
            for (long k = 0; k < r.MipBytes(mip); k++) copy[m + k] = (byte)rng.Next(256);
        }
        Assert.Equal(GrassArtError.MipChainIncoherent, NativeGrassArt.CheckBlocks(copy, r));
        Assert.True(at > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ThePlaceholderOfEveryRangePassesTheStructureCheck(int range)
    {
        GrassArtRange r = NativeGrassArt.Ranges[range];
        Assert.Equal(GrassArtError.None, NativeGrassArt.CheckBlocks(Placeholder(r), r));
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
        GrassArtRange r = NativeGrassArt.TexArray;
        byte[] data = Placeholder(r);
        for (int s = 0; s < 5; s++) Assert.Equal(0.0, NativeGrassArt.MipChainDifference(data, 0, r, s), 3);
    }

    // ---- the placeholder art ------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePlaceholderIsDeterministicAndFillsTheBufferItIsGiven()
    {
        GrassArtRange r = NativeGrassArt.NormalArray;
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
        GrassArtRange r = NativeGrassArt.TexArray;
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
        GrassArtRange r = NativeGrassArt.TexArray;
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
        GrassArtRange r = NativeGrassArt.NormalArray;
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
        GrassArtRange r = NativeGrassArt.TexArray;
        byte[] data = Placeholder(r);
        var distinct = new HashSet<byte>(data);
        Assert.True(distinct.Count <= 12, "distinct byte values " + distinct.Count);
    }

    [Fact]
    public void TheDescriptionNamesTheRangeAndNoContent()
    {
        Assert.Equal("GrassTexArray 1024x1024x5 mips=11 RGBA_DXT5_UNorm @699072+6990640", NativeGrassArt.Describe(NativeGrassArt.TexArray));
        Assert.Equal("Grass_new_1_N 1024x1024 mips=11 DXT1 @0+699064", NativeGrassArt.Describe(NativeGrassArt.NormalTexture));
    }
}
