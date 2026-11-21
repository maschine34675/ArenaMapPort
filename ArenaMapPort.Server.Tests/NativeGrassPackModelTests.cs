#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The reader of the grass pack, format version 1 ("AMGP", NativeGrassPackModel.cs): the format as the lead's contract states it, every refusal as a named code, the ranges that come out of a header, and
/// the arithmetic of Arena's real art shapes. The pack is synthetic (<see cref="GrassTestPack"/>, built from the format text); real packs are in <see cref="NativeGrassPackRealDataTests"/>.
/// Unit-tested; nothing here is host-run.
/// </summary>
public sealed class NativeGrassPackModelTests
{
    const string Yard = "arena_yard";

    static GrassPackError Parse(byte[] pack, string expected, out GrassPackLayout layout) => NativeGrassPack.Parse(pack, Math.Min(pack.Length, 4096), pack.Length, expected, out layout);

    static GrassPackError Parse(byte[] pack, string expected = Yard) => Parse(pack, expected, out _);

    // ---- the format ---------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheHeaderConstantsAreTheContract()
    {
        Assert.Equal("AMGP", NativeGrassPack.MagicText);
        Assert.Equal(1u, NativeGrassPack.FormatVersion);
        Assert.Equal(272, NativeGrassPack.HeaderSize);
        Assert.Equal(4, NativeGrassPack.EntryCount);
        Assert.Equal(56, NativeGrassPack.EntrySize);
        Assert.Equal(48, NativeGrassPack.FirstEntryAt);
        Assert.Equal(32, NativeGrassPack.MapKeyField);
        Assert.Equal(NativeGrassPack.HeaderSize, NativeGrassPack.FirstEntryAt + NativeGrassPack.EntryCount * NativeGrassPack.EntrySize);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, new[] { NativeGrassPack.KindPlacement, NativeGrassPack.KindAlbedo, NativeGrassPack.KindNormalArray, NativeGrassPack.KindNormalTexture });
        Assert.Equal(new uint[] { 0, 1, 5 }, new[] { NativeGrassPack.FormatRecords, NativeGrassPack.FormatDxt1, NativeGrassPack.FormatDxt5 });
        Assert.Equal(NativeGrassPlacement.RecordSize, 68);
    }

    [Fact]
    public void TheBundleFileNameIsTheDescriptorsAndTheStagedName()
    {
        foreach (ArenaMapDescriptor map in ArenaMapRegistry.All)
            Assert.Equal(map.Key + "_grass.bundle", NativeGrassPack.BundleFileName(map.Key));
        Assert.Equal("arena_yard_grass.bundle", ArenaMapRegistry.Yard.GrassBundleFileName);
        Assert.Equal(ArenaMapRegistry.Bowl.GrassBundleFileName, NativeGrassPack.BundleFileName(ArenaMapRegistry.Bowl.Key));
    }

    [Fact]
    public void AValidPackParsesToItsLayout()
    {
        byte[] pack = GrassTestPack.Build(Yard, GrassTestPack.Placement(40));
        GrassPackError error = Parse(pack, Yard, out GrassPackLayout layout);
        Assert.Equal(GrassPackError.None, error);
        Assert.Equal(Yard, layout.MapKey);
        Assert.Equal(40, layout.RecordCount);
        Assert.Equal(40 * 68, layout.PlacementSize);
        Assert.Equal(272, layout.PlacementOffset);
        Assert.Equal(5, layout.SliceCount);
        Assert.Equal(pack.Length, layout.FileLength);
        Assert.Equal(new[] { "GrassTexArray", "GrassNormalArray", "Grass_new_1_N" }, layout.Ranges.Select(r => r.Name).ToArray());
        Assert.Same(layout.TexArray, layout.Ranges[0]);
        Assert.Same(layout.NormalArray, layout.Ranges[1]);
        Assert.Same(layout.NormalTexture, layout.Ranges[2]);
        Assert.Equal(new[] { GrassBlockFormat.Dxt5, GrassBlockFormat.Dxt1, GrassBlockFormat.Dxt1 }, layout.Ranges.Select(r => r.Format).ToArray());
        Assert.Equal(new[] { true, true, false }, layout.Ranges.Select(r => r.IsArray).ToArray());
        Assert.Equal(new[] { true, true, false }, layout.Ranges.Select(r => r.IgnoreMipmapLimit).ToArray());
        Assert.Equal(new[] { 5, 5, 1 }, layout.Ranges.Select(r => r.Slices).ToArray());
        Assert.All(layout.Ranges, r => { Assert.Equal(128, r.Width); Assert.Equal(128, r.Height); Assert.Equal(8, r.MipCount); Assert.Equal(2, r.FilterMode); Assert.Equal(1, r.WrapMode); Assert.Equal(1, r.Aniso); });
        // contiguous, in entry order, ending at the end of the file
        long next = 272 + layout.PlacementSize;
        foreach (GrassArtRange r in layout.Ranges)
        {
            Assert.Equal(next, r.Offset);
            Assert.Equal(r.ComputedSize, r.Size);
            next += r.Size;
        }
        Assert.Equal(pack.Length, next);
    }

    [Fact]
    public void ThePlacementPayloadIsTheFileByteForByte()
    {
        byte[] records = GrassTestPack.Placement(7);
        byte[] pack = GrassTestPack.Build(Yard, records);
        Parse(pack, Yard, out GrassPackLayout layout);
        var inside = new byte[layout.PlacementSize];
        Array.Copy(pack, layout.PlacementOffset, inside, 0, inside.Length);
        Assert.Equal(records, inside);
    }

    [Fact]
    public void ANullExpectedMapKeyAcceptsAnyMap()
    {
        byte[] pack = GrassTestPack.Build("arena_bowl");
        Assert.Equal(GrassPackError.None, NativeGrassPack.Parse(pack, 272, pack.Length, null, out GrassPackLayout layout));
        Assert.Equal("arena_bowl", layout.MapKey);
    }

    [Fact]
    public void OnlyTheHeaderIsNeededToParse()
    {
        // the worker reads the first 272 bytes and passes the file's length: no payload byte is looked at
        byte[] pack = GrassTestPack.Build(Yard);
        var header = new byte[272];
        Array.Copy(pack, header, 272);
        Assert.Equal(GrassPackError.None, NativeGrassPack.Parse(header, 272, pack.Length, Yard, out _));
        // a longer buffer with a shorter length argument is honoured
        Assert.Equal(GrassPackError.TooShort, NativeGrassPack.Parse(pack, 271, pack.Length, Yard, out _));
    }

    // ---- every refusal ------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ATooShortInputIsRefused()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        Assert.Equal(GrassPackError.TooShort, NativeGrassPack.Parse(null, 0, 0, Yard, out GrassPackLayout layout));
        Assert.Null(layout);
        Assert.Equal(GrassPackError.TooShort, NativeGrassPack.Parse(new byte[10], 10, 10, Yard, out _));
        Assert.Equal(GrassPackError.TooShort, NativeGrassPack.Parse(pack, 100, pack.Length, Yard, out _));
        Assert.Equal(GrassPackError.TooShort, NativeGrassPack.Parse(pack, 272, 100, Yard, out _));         // the file itself is shorter than the header
        Assert.Equal(GrassPackError.TooShort, NativeGrassPack.Parse(pack, pack.Length + 1, pack.Length, Yard, out _));
    }

    [Fact]
    public void AWrongMagicOrAUnityBundleIsRefused()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        pack[3] = (byte)'Q';
        Assert.Equal(GrassPackError.BadMagic, Parse(pack));
        byte[] unity = GrassTestPack.Build(Yard);
        "UnityFS\0"u8.CopyTo(unity);
        Assert.Equal(GrassPackError.BadMagic, Parse(unity));
        byte[] ampb = GrassTestPack.Build(Yard);
        "AMPB"u8.CopyTo(ampb);
        Assert.Equal(GrassPackError.BadMagic, Parse(ampb));        // an unopened container is no pack
    }

    [Theory]
    [InlineData(4, 2u, "BadVersion")]
    [InlineData(4, 0u, "BadVersion")]
    [InlineData(8, 273u, "BadHeaderSize")]
    [InlineData(8, 0u, "BadHeaderSize")]
    [InlineData(12, 3u, "BadEntryCount")]
    [InlineData(12, 5u, "BadEntryCount")]
    public void TheFixedHeaderFieldsAreRefusedWhenTheyDiffer(int at, uint value, string expected)
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(pack, at, value);
        Assert.Equal(Enum.Parse<GrassPackError>(expected), Parse(pack));
    }

    [Fact]
    public void TheMapKeyIsCheckedForShapeAndForTheMapItIsOpenedFor()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        Assert.Equal(GrassPackError.MapKeyMismatch, Parse(pack, "arena_bowl"));
        Assert.Equal(GrassPackError.MapKeyMismatch, Parse(pack, "arena_yard2"));
        Assert.Equal(GrassPackError.MapKeyMismatch, Parse(pack, "ARENA_YARD"));
        // empty key
        byte[] empty = GrassTestPack.Build(Yard);
        Array.Clear(empty, 16, 32);
        Assert.Equal(GrassPackError.BadMapKey, Parse(empty));
        // a space and a control byte are not printable ASCII without a space
        byte[] space = GrassTestPack.Build(Yard);
        space[20] = (byte)' ';
        Assert.Equal(GrassPackError.BadMapKey, Parse(space));
        byte[] control = GrassTestPack.Build(Yard);
        control[16] = 0x07;
        Assert.Equal(GrassPackError.BadMapKey, Parse(control));
        byte[] high = GrassTestPack.Build(Yard);
        high[16] = 0xC3;
        Assert.Equal(GrassPackError.BadMapKey, Parse(high));
        // bytes after the terminating zero
        byte[] tail = GrassTestPack.Build(Yard);
        tail[16 + 31] = (byte)'x';
        Assert.Equal(GrassPackError.BadMapKey, Parse(tail));
        // a key that fills the whole field (32 characters, no terminator) is valid
        string full = new string('a', 32);
        Assert.Equal(GrassPackError.None, Parse(GrassTestPack.Build(full), full));
    }

    [Fact]
    public void TheKindsMustBeOneTwoThreeFourInThatOrder()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(pack, GrassTestPack.Field(1, 0), 3);
        GrassTestPack.Put32(pack, GrassTestPack.Field(2, 0), 2);
        Assert.Equal(GrassPackError.KindOrder, Parse(pack));
        byte[] unknown = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(unknown, GrassTestPack.Field(3, 0), 9);
        Assert.Equal(GrassPackError.KindOrder, Parse(unknown));
    }

    [Theory]
    [InlineData(0, 1u)]       // placement is records
    [InlineData(1, 1u)]       // the albedo is DXT5, not DXT1
    [InlineData(2, 5u)]       // the normal array is DXT1, not DXT5
    [InlineData(3, 5u)]
    [InlineData(1, 0u)]
    [InlineData(3, 2u)]
    public void EveryKindHasItsFixedFormat(int entry, uint format)
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(pack, GrassTestPack.Field(entry, 4), format);
        Assert.Equal(GrassPackError.BadFormat, Parse(pack));
    }

    [Fact]
    public void ReservedMustBeZero()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(pack, GrassTestPack.Field(2, 36), 1);
        Assert.Equal(GrassPackError.ReservedNotZero, Parse(pack));
    }

    [Theory]
    [InlineData(8, 67u)]      // record size
    [InlineData(8, 0u)]
    [InlineData(12, 0u)]      // no record
    [InlineData(20, 1u)]      // mips
    [InlineData(24, 1u)]      // filter
    [InlineData(28, 1u)]      // wrap
    [InlineData(32, 1u)]      // aniso
    [InlineData(16, 0u)]      // slice count below 1
    [InlineData(16, 1025u)]   // slice count above the maximum
    [InlineData(12, 500001u)] // more records than the absolute maximum
    public void ThePlacementEntryMustBeRecordsOfSixtyEightBytes(int field, uint value)
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(pack, GrassTestPack.Field(0, field), value);
        Assert.Equal(GrassPackError.BadPlacementShape, Parse(pack));
    }

    [Fact]
    public void ThePlacementSizeMustBeSixtyEightTimesTheCount()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put64(pack, GrassTestPack.Field(0, 48), 40 * 68 + 4);
        Assert.Equal(GrassPackError.SizeMismatch, Parse(pack));
        byte[] fewer = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(fewer, GrassTestPack.Field(0, 12), 39);       // the count says 39, the size says 40 records
        Assert.Equal(GrassPackError.SizeMismatch, Parse(fewer));
    }

    [Theory]
    [InlineData(1, 8, 0u)]            // width 0
    [InlineData(1, 12, 0u)]           // height 0
    [InlineData(2, 8, 16385u)]        // wider than the maximum
    [InlineData(2, 12, 16385u)]
    [InlineData(1, 16, 0u)]           // no slice
    [InlineData(2, 16, 1025u)]        // more slices than the maximum
    [InlineData(1, 20, 0u)]           // no mip
    [InlineData(1, 20, 9u)]           // 128 x 128 has 8 mips at most
    [InlineData(3, 20, 99u)]
    public void ATextureEntryMustBeSomethingThatCanExist(int entry, int field, uint value)
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(pack, GrassTestPack.Field(entry, field), value);
        Assert.Equal(GrassPackError.BadTextureShape, Parse(pack));
    }

    [Fact]
    public void ThePlainNormalTextureHasOneSlice()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(pack, GrassTestPack.Field(3, 16), 2);
        Assert.Equal(GrassPackError.BadTextureShape, Parse(pack));
    }

    [Fact]
    public void ATextureAtLeastTwoGibibytesIsRefusedAsNotFittingOneArray()
    {
        // 16384 x 16384 DXT5, eight mips, seven slices (2.5 GB): the arithmetic and the header agree (so no SizeMismatch), but the worker reads a payload into one array
        var header = new byte[272];
        byte[] pack = GrassTestPack.Build(Yard);
        Array.Copy(pack, header, 272);
        GrassTestPack.Put32(header, GrassTestPack.Field(1, 8), 16384);
        GrassTestPack.Put32(header, GrassTestPack.Field(1, 12), 16384);
        GrassTestPack.Put32(header, GrassTestPack.Field(1, 16), 7);
        var big = new GrassArtRange("x", 0, 0, 16384, 16384, 7, 8, GrassBlockFormat.Dxt5, true, 2, 1, 1, true);
        Assert.True(big.ComputedSize > int.MaxValue);
        GrassTestPack.Put64(header, GrassTestPack.Field(1, 48), (ulong)big.ComputedSize);
        Assert.Equal(GrassPackError.BadTextureShape, NativeGrassPack.Parse(header, 272, long.MaxValue / 2, Yard, out _));
    }

    [Fact]
    public void ATextureSizeThatIsNotTheMipChainArithmeticIsRefused()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        (int offset, int size) = GrassTestPack.Payload(pack, 1);
        GrassTestPack.Put64(pack, GrassTestPack.Field(1, 48), (ulong)(size - 16));
        Assert.Equal(GrassPackError.SizeMismatch, Parse(pack));
        byte[] normal = GrassTestPack.Build(Yard);
        GrassTestPack.Put64(normal, GrassTestPack.Field(3, 48), (ulong)(GrassTestPack.Payload(normal, 3).size + 8));
        Assert.Equal(GrassPackError.SizeMismatch, Parse(normal));
        Assert.True(offset > 0);
    }

    [Fact]
    public void TheSliceCountsOfPlacementAlbedoAndNormalArrayMustAgree()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(pack, GrassTestPack.Field(0, 16), 4);          // the placement says 4, the albedo has 5
        Assert.Equal(GrassPackError.Inconsistent, Parse(pack));
        // a normal array with another slice count: its own size is right (so no SizeMismatch) but it is not the albedo's
        byte[] four = GrassTestPack.Build(Yard, slices: 5, normalArray: GrassTestPack.Art(GrassTestPack.SmallShape, 4, GrassBlockFormat.Dxt1));
        GrassTestPack.Put32(four, GrassTestPack.Field(2, 16), 4);
        Assert.Equal(GrassPackError.Inconsistent, Parse(four));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryEntryMustStartWhereThePreviousOneEnded(int entry)
    {
        byte[] pack = GrassTestPack.Build(Yard);
        ulong at = BitConverter.ToUInt64(pack, GrassTestPack.Field(entry, 40));
        GrassTestPack.Put64(pack, GrassTestPack.Field(entry, 40), at + 16);
        Assert.Equal(GrassPackError.NotContiguous, Parse(pack));
        byte[] gap = GrassTestPack.Build(Yard);
        GrassTestPack.Put64(gap, GrassTestPack.Field(entry, 40), at - 8);
        Assert.Equal(GrassPackError.NotContiguous, Parse(gap));
    }

    [Fact]
    public void TheFileMustBeExactlyAsLongAsItsLastEntryEnds()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        Assert.Equal(GrassPackError.FileLength, NativeGrassPack.Parse(pack, 272, pack.Length + 1, Yard, out _));
        Assert.Equal(GrassPackError.FileLength, NativeGrassPack.Parse(pack, 272, pack.Length - 1, Yard, out _));
        Assert.Equal(GrassPackError.FileLength, NativeGrassPack.Parse(pack, 272, 272, Yard, out _));
        Assert.Equal(GrassPackError.None, NativeGrassPack.Parse(pack, 272, pack.Length, Yard, out _));
    }

    [Fact]
    public void TheFirstFailingCheckWinsInTheDocumentedOrder()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        pack[0] = 0;                                                           // magic
        GrassTestPack.Put32(pack, 4, 7);                                       // and a version
        Assert.Equal(GrassPackError.BadMagic, Parse(pack, "arena_bowl"));
        byte[] two = GrassTestPack.Build(Yard);
        GrassTestPack.Put32(two, GrassTestPack.Field(1, 4), 1);                // a format
        Assert.Equal(GrassPackError.MapKeyMismatch, Parse(two, "arena_bowl")); // the map comes before the format
    }

    [Fact]
    public void ARefusalNeverThrowsWhateverByteIsFlippedInTheHeader()
    {
        byte[] pack = GrassTestPack.Build(Yard);
        for (int at = 0; at < 272; at++)
        {
            foreach (byte value in new byte[] { 0x00, 0x01, 0x7F, 0xFF })
            {
                byte[] copy = (byte[])pack.Clone();
                copy[at] = value;
                GrassPackError error = NativeGrassPack.Parse(copy, 272, copy.Length, Yard, out GrassPackLayout layout);
                Assert.Equal(error == GrassPackError.None, layout != null);
            }
        }
    }

    // ---- the real shapes and the description ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ArenasRealArtShapesGiveTheSizesOfThePackThatWasMeasured()
    {
        // header only: 1024 x 1024, eleven mips, five slices; the same arithmetic as the stage-1 pins of Arena's stream
        GrassPackLayout layout = GrassTestPack.RealLayout();
        Assert.Equal(688976L, layout.PlacementSize);
        Assert.Equal(10132, layout.RecordCount);
        Assert.Equal(new[] { 6990640L, 3495320L, 699064L }, layout.Ranges.Select(r => r.Size).ToArray());
        Assert.Equal(new[] { 1398128L, 699064L, 699064L }, layout.Ranges.Select(r => r.SliceBytes).ToArray());
        Assert.Equal(1048576L, layout.TexArray.MipBytes(0));       // DXT5: 1 MiB at mip 0
        Assert.Equal(272L + 688976L + 6990640L + 3495320L + 699064L, layout.FileLength);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    [InlineData(128, 8)]
    [InlineData(1024, 11)]
    [InlineData(1023, 10)]
    [InlineData(16384, 15)]
    public void TheLongestMipChainIsTheBitLengthOfTheLargerSide(int side, int mips)
    {
        Assert.Equal(mips, NativeGrassPack.MaxMips(side, 1));
        Assert.Equal(mips, NativeGrassPack.MaxMips(1, side));
    }

    [Fact]
    public void TheDescriptionNamesTheMapTheCountsAndTheRangesAndNoContent()
    {
        byte[] pack = GrassTestPack.Build(Yard, GrassTestPack.Placement(40));
        Parse(pack, Yard, out GrassPackLayout layout);
        string text = NativeGrassPack.Describe(layout);
        Assert.StartsWith("pack for arena_yard " + pack.Length + " B: records=40 slices=5; GrassTexArray 128x128x5 mips=8 DXT5 @", text);
        Assert.Contains("Grass_new_1_N 128x128 mips=8 DXT1 @", text);
        Assert.DoesNotContain("\n", text);
    }

    // The synthetic pack of tools/tests/test_grass_pack.py (SYNTHETIC_HEX), byte for byte: the Python writer and this reader agree on one fixed vector.
    const string SyntheticHex =
        "414d47500100000010010000040000006172656e615f7465737400000000000000000000000000000000000000000000" +
        "010000000000000044000000020000000200000000000000000000000000000000000000000000001001000000000000" +
        "880000000000000002000000050000000800000008000000020000000400000002000000010000000100000000000000" +
        "9801000000000000e0000000000000000300000001000000080000000800000002000000040000000200000001000000" +
        "010000000000000078020000000000007000000000000000040000000100000008000000080000000100000004000000" +
        "02000000010000001000000000000000e8020000000000003800000000000000010000000000003f0000000000000000" +
        "00000000000000000000003f000000000000000000000000000000000000003f000000000000803f0000004000004040" +
        "0000803f000000000000803f000000000000000000000000000000000000803f00000000000000000000000000000000" +
        "0000803f00000000000090c00000803e000000410000803f01060b10151a1f24292e33383d42474c51565b60656a6f74" +
        "797e83888d92979ca1a6abb0b5babfc4c9ced3d8dde2e7ecf1f6fb00050a0f14191e23282d32373c41464b50555a5f64" +
        "696e73787d82878c91969ba0a5aaafb4b9bec3c8cdd2d7dce1e6ebf0f5faff04090e13181d22272c31363b40454a4f54" +
        "595e63686d72777c81868b90959a9fa4a9aeb3b8bdc2c7ccd1d6dbe0e5eaeff4f9fe03080d12171c21262b30353a3f44" +
        "494e53585d62676c71767b80858a8f94999ea3a8adb2b7bcc1c6cbd0d5dadfe4e9eef3f8fd02070c11161b20252a2f34" +
        "393e43484d52575c0205080b0e1114171a1d202326292c2f3235383b3e4144474a4d505356595c5f6265686b6e717477" +
        "7a7d808386898c8f9295989b9ea1a4a7aaadb0b3b6b9bcbfc2c5c8cbced1d4d7dadde0e3e6e9eceff2f5f8fbfe010407" +
        "0a0d101316191c1f2225282b2e3134373a3d404346494c4f030e19242f3a45505b66717c87929da8b3bec9d4dfeaf500" +
        "0b16212c37424d58636e79848f9aa5b0bbc6d1dce7f2fd08131e29343f4a5560";

    [Fact]
    public void ThePythonWritersSyntheticVectorParsesToWhatItEncodes()
    {
        byte[] pack = Convert.FromHexString(SyntheticHex);
        Assert.Equal(800, pack.Length);
        Assert.Equal(GrassPackError.None, Parse(pack, "arena_test", out GrassPackLayout l));
        Assert.Equal("arena_test", l.MapKey);
        Assert.Equal(2, l.RecordCount);
        Assert.Equal(272L, l.PlacementOffset);
        Assert.Equal(136L, l.PlacementSize);
        Assert.Equal(2, l.SliceCount);
        Assert.Equal(800L, l.FileLength);
        // (offset, size, width, height, slices, mips, format, filter, wrap, aniso)
        Assert.Equal((408L, 224L, 8, 8, 2, 4), (l.TexArray.Offset, l.TexArray.Size, l.TexArray.Width, l.TexArray.Height, l.TexArray.Slices, l.TexArray.MipCount));
        Assert.Equal((GrassBlockFormat.Dxt5, 2, 1, 1), (l.TexArray.Format, l.TexArray.FilterMode, l.TexArray.WrapMode, l.TexArray.Aniso));
        Assert.Equal((632L, 112L, 8, 8, 2, 4), (l.NormalArray.Offset, l.NormalArray.Size, l.NormalArray.Width, l.NormalArray.Height, l.NormalArray.Slices, l.NormalArray.MipCount));
        Assert.Equal((GrassBlockFormat.Dxt1, 2, 1, 1), (l.NormalArray.Format, l.NormalArray.FilterMode, l.NormalArray.WrapMode, l.NormalArray.Aniso));
        Assert.Equal((744L, 56L, 8, 8, 1, 4), (l.NormalTexture.Offset, l.NormalTexture.Size, l.NormalTexture.Width, l.NormalTexture.Height, l.NormalTexture.Slices, l.NormalTexture.MipCount));
        Assert.Equal((GrassBlockFormat.Dxt1, 2, 1, 16), (l.NormalTexture.Format, l.NormalTexture.FilterMode, l.NormalTexture.WrapMode, l.NormalTexture.Aniso));
        Assert.Equal(GrassPackError.MapKeyMismatch, Parse(pack, Yard));
    }
}
