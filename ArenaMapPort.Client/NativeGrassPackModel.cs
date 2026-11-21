#nullable disable
using System;
using System.Globalization;
using System.Text;

namespace ArenaMapPort.Client
{
    // The reader of the grass pack, format version 1 ("AMGP"), the plaintext of a map's staged grass bundle (bundles/<map_key>_grass.bundle, an AMPB container in a release). Unity-free, no
    // file access, no log: the worker (NativeGrassArtSource) reads the first HeaderSize bytes and hands them here; everything else this file does is arithmetic on the header.
    //
    // The format (little-endian) is the contract between the Python writer (tools/extract/grass_pack.py, tools/extract/extract_grass.py pack) and this reader:
    //   offset 0   4 bytes   ASCII "AMGP"
    //   offset 4   uint32    version = 1
    //   offset 8   uint32    headerSize = 272 (the first payload starts here)
    //   offset 12  uint32    entryCount = 4
    //   offset 16  32 bytes  map key in ASCII, zero-padded
    //   offset 48  4 entries of 56 bytes each, in the fixed order of kinds 1, 2, 3, 4:
    //      +0  uint32 kind      1 placement records, 2 albedo array (GrassTexArray), 3 normal array (GrassNormalArray), 4 normal texture (Grass_new_1_N)
    //      +4  uint32 format    0 records, 1 DXT1 (BC1, 8 bytes per 4x4 block), 5 DXT5 (BC3, 16 bytes per block)
    //      +8  uint32 width     (placement: the record size in bytes, 68)
    //      +12 uint32 height    (placement: the record count)
    //      +16 uint32 slices    (placement: the slice count of the albedo array; kind 4: 1)
    //      +20 uint32 mips      (placement: 0)
    //      +24 uint32 filterMode   +28 uint32 wrapMode   +32 uint32 aniso   (Unity's enum values as Arena serialized them; 0 for the placement)
    //      +36 uint32 reserved = 0
    //      +40 uint64 offset    absolute, from the start of the file
    //      +48 uint64 size
    // Payloads follow the header contiguously in entry order with no padding. A placement payload is Arena's .pcl file byte for byte (68-byte records: an int32 slice index and a
    // column-major 4x4 float matrix); a texture payload is Arena's stream range byte for byte (slice-major, the whole mip chain of a slice from mip 0 down, a mip below one block padded to
    // one block). The reader refuses anything that does not match exactly, with the named codes of GrassPackError (the same names as tools/extract/grass_pack.py). It does not look at floats:
    // the finite-number, affine-matrix and slice checks of the placement are NativeGrassPlacement.Parse's, on the payload.

    /// <summary>Why a pack header was refused. The first failing check wins, in the order the members are listed.</summary>
    internal enum GrassPackError
    {
        None = 0,
        /// <summary>Fewer bytes than the header (or no bytes).</summary>
        TooShort,
        BadMagic,
        BadVersion,
        BadHeaderSize,
        BadEntryCount,
        /// <summary>The map key field is empty, not printable ASCII, or has bytes after its terminating zero.</summary>
        BadMapKey,
        /// <summary>The pack is for another map than the one it was opened for.</summary>
        MapKeyMismatch,
        /// <summary>The entries are not kinds 1, 2, 3, 4 in that order.</summary>
        KindOrder,
        /// <summary>A kind has a format other than its fixed one (records 0, DXT5 albedo, DXT1 for the two normal textures).</summary>
        BadFormat,
        ReservedNotZero,
        /// <summary>The placement entry is not 68-byte records, has no record, or carries mips, filter, wrap or aniso.</summary>
        BadPlacementShape,
        /// <summary>A texture's size, slice or mip count is outside what can exist (or its bytes would not fit one array).</summary>
        BadTextureShape,
        /// <summary>An entry's size differs from its arithmetic (68 x records; slices x the mip chain).</summary>
        SizeMismatch,
        /// <summary>An entry does not start where the previous one ended (the first at the header size).</summary>
        NotContiguous,
        /// <summary>The file is not exactly as long as the last entry's end.</summary>
        FileLength,
        /// <summary>The placement's or the normal array's slice count differs from the albedo array's.</summary>
        Inconsistent,
    }

    /// <summary>A parsed pack header: the map it is for, the placement entry and the three texture ranges (every offset absolute in the pack file).</summary>
    internal sealed class GrassPackLayout
    {
        internal GrassPackLayout(string mapKey, long placementOffset, long placementSize, int recordCount, int sliceCount, GrassArtRange texArray, GrassArtRange normalArray,
            GrassArtRange normalTexture, long fileLength)
        {
            MapKey = mapKey;
            PlacementOffset = placementOffset;
            PlacementSize = placementSize;
            RecordCount = recordCount;
            SliceCount = sliceCount;
            TexArray = texArray;
            NormalArray = normalArray;
            NormalTexture = normalTexture;
            FileLength = fileLength;
            Ranges = new[] { texArray, normalArray, normalTexture };
        }

        internal string MapKey { get; }
        internal long PlacementOffset { get; }
        /// <summary>68 x <see cref="RecordCount"/>: Arena's .pcl file byte for byte.</summary>
        internal long PlacementSize { get; }
        internal int RecordCount { get; }
        /// <summary>The slice count of the albedo array: a placement record's slice index is below it, and so is the normal array's slice count.</summary>
        internal int SliceCount { get; }
        /// <summary>GrassTexArray, the albedo (DXT5).</summary>
        internal GrassArtRange TexArray { get; }
        /// <summary>GrassNormalArray (DXT1).</summary>
        internal GrassArtRange NormalArray { get; }
        /// <summary>Grass_new_1_N, the plain normal texture (DXT1).</summary>
        internal GrassArtRange NormalTexture { get; }
        /// <summary>The three texture ranges in pack order (albedo, normal array, normal texture).</summary>
        internal GrassArtRange[] Ranges { get; }
        /// <summary>The whole file's length, which is the last entry's end.</summary>
        internal long FileLength { get; }
    }

    /// <summary>The grass pack's constants and its header parser.</summary>
    internal static class NativeGrassPack
    {
        internal const string MagicText = "AMGP";
        internal const uint FormatVersion = 1;
        internal const int HeaderSize = 272;
        internal const int EntryCount = 4;
        internal const int EntrySize = 56;
        internal const int FirstEntryAt = 48;
        internal const int MapKeyField = 32;

        internal const uint KindPlacement = 1;
        internal const uint KindAlbedo = 2;
        internal const uint KindNormalArray = 3;
        internal const uint KindNormalTexture = 4;

        internal const uint FormatRecords = 0;
        internal const uint FormatDxt1 = 1;
        internal const uint FormatDxt5 = 5;

        /// <summary>The largest texture the format admits (the writer's and the reader's bound alike); the 1024 x 1024 x 5 art is far inside it.</summary>
        internal const int MaxDimension = 16384;
        internal const int MaxSlices = 1024;

        static readonly uint[] KindsInOrder = { KindPlacement, KindAlbedo, KindNormalArray, KindNormalTexture };

        /// <summary>The file name of a map's staged grass bundle (the descriptor's <c>GrassBundleFileName</c>), spelled once for the tests.</summary>
        internal static string BundleFileName(string mapKey) => mapKey + "_grass.bundle";

        /// <summary>
        /// Parses the first <paramref name="length"/> bytes of <paramref name="bytes"/> (at least <see cref="HeaderSize"/> unless the file is shorter, which is TooShort) as a pack header for
        /// <paramref name="expectedMapKey"/> and checks it against the file's total <paramref name="fileLength"/>. Never throws for bad data: the code names the first defect.
        /// </summary>
        internal static GrassPackError Parse(byte[] bytes, int length, long fileLength, string expectedMapKey, out GrassPackLayout layout)
        {
            layout = null;
            if (bytes == null || length < HeaderSize || length > bytes.Length || fileLength < HeaderSize)
            {
                return GrassPackError.TooShort;
            }
            if (bytes[0] != (byte)'A' || bytes[1] != (byte)'M' || bytes[2] != (byte)'G' || bytes[3] != (byte)'P')
            {
                return GrassPackError.BadMagic;
            }
            if (U32(bytes, 4) != FormatVersion)
            {
                return GrassPackError.BadVersion;
            }
            if (U32(bytes, 8) != HeaderSize)
            {
                return GrassPackError.BadHeaderSize;
            }
            if (U32(bytes, 12) != EntryCount)
            {
                return GrassPackError.BadEntryCount;
            }
            string mapKey;
            if (!ReadMapKey(bytes, 16, out mapKey))
            {
                return GrassPackError.BadMapKey;
            }
            if (expectedMapKey != null && !string.Equals(mapKey, expectedMapKey, StringComparison.Ordinal))
            {
                return GrassPackError.MapKeyMismatch;
            }

            var e = new Entry[EntryCount];
            for (int i = 0; i < EntryCount; i++)
            {
                e[i] = ReadEntry(bytes, FirstEntryAt + i * EntrySize);
            }
            for (int i = 0; i < EntryCount; i++)
            {
                if (e[i].Kind != KindsInOrder[i])
                {
                    return GrassPackError.KindOrder;
                }
                if (e[i].Format != FormatOf(e[i].Kind))
                {
                    return GrassPackError.BadFormat;
                }
                if (e[i].Reserved != 0)
                {
                    return GrassPackError.ReservedNotZero;
                }
            }

            Entry placement = e[0];
            if (placement.Width != NativeGrassPlacement.RecordSize || placement.Height < 1 || placement.Height > NativeGrassPlacement.AbsoluteMaxRecords
                || placement.Mips != 0 || placement.Filter != 0 || placement.Wrap != 0 || placement.Aniso != 0 || placement.Slices < 1 || placement.Slices > MaxSlices)
            {
                return GrassPackError.BadPlacementShape;
            }
            if (placement.Size != (ulong)NativeGrassPlacement.RecordSize * placement.Height)
            {
                return GrassPackError.SizeMismatch;
            }

            var ranges = new GrassArtRange[3];
            for (int i = 1; i < EntryCount; i++)
            {
                Entry t = e[i];
                if (t.Width < 1 || t.Height < 1 || t.Slices < 1 || t.Mips < 1 || t.Width > MaxDimension || t.Height > MaxDimension || t.Slices > MaxSlices)
                {
                    return GrassPackError.BadTextureShape;
                }
                if (t.Mips > MaxMips((int)t.Width, (int)t.Height))
                {
                    return GrassPackError.BadTextureShape;
                }
                if (t.Kind == KindNormalTexture && t.Slices != 1)
                {
                    return GrassPackError.BadTextureShape;
                }
                bool array = t.Kind != KindNormalTexture;
                var range = new GrassArtRange(NameOf(t.Kind), Clamp(t.Offset), Clamp(t.Size), (int)t.Width, (int)t.Height, (int)t.Slices, (int)t.Mips,
                    t.Format == FormatDxt5 ? GrassBlockFormat.Dxt5 : GrassBlockFormat.Dxt1, array, (int)t.Filter, (int)t.Wrap, (int)t.Aniso, ignoreMipmapLimit: array);
                if (t.Size != (ulong)range.ComputedSize)
                {
                    return GrassPackError.SizeMismatch;
                }
                if (t.Size > int.MaxValue)
                {
                    return GrassPackError.BadTextureShape; // the worker reads a payload into one array
                }
                ranges[i - 1] = range;
            }
            if (placement.Slices != e[1].Slices || e[2].Slices != e[1].Slices)
            {
                return GrassPackError.Inconsistent;
            }

            ulong expect = HeaderSize;
            for (int i = 0; i < EntryCount; i++)
            {
                if (e[i].Offset != expect)
                {
                    return GrassPackError.NotContiguous;
                }
                expect += e[i].Size;
            }
            if ((ulong)fileLength != expect)
            {
                return GrassPackError.FileLength;
            }

            layout = new GrassPackLayout(mapKey, (long)placement.Offset, (long)placement.Size, (int)placement.Height, (int)placement.Slices, ranges[0], ranges[1], ranges[2], fileLength);
            return GrassPackError.None;
        }

        /// <summary>A one-line description for the log (codes and numbers, no content): the map, the file length, the record and slice counts and the three ranges.</summary>
        internal static string Describe(GrassPackLayout layout)
        {
            var sb = new StringBuilder();
            sb.Append("pack for ").Append(layout.MapKey).Append(' ').Append(layout.FileLength.ToString(CultureInfo.InvariantCulture)).Append(" B: records=")
                .Append(layout.RecordCount.ToString(CultureInfo.InvariantCulture)).Append(" slices=").Append(layout.SliceCount.ToString(CultureInfo.InvariantCulture));
            foreach (GrassArtRange r in layout.Ranges)
            {
                sb.Append("; ").Append(NativeGrassArt.Describe(r));
            }
            return sb.ToString();
        }

        /// <summary>The longest mip chain a texture of this size has (down to 1 x 1).</summary>
        internal static int MaxMips(int width, int height)
        {
            int size = Math.Max(width, height);
            int count = 0;
            while (size > 0)
            {
                count++;
                size >>= 1;
            }
            return count;
        }

        static string NameOf(uint kind)
        {
            switch (kind)
            {
                case KindAlbedo: return "GrassTexArray";
                case KindNormalArray: return "GrassNormalArray";
                default: return "Grass_new_1_N";
            }
        }

        static uint FormatOf(uint kind)
        {
            switch (kind)
            {
                case KindAlbedo: return FormatDxt5;
                case KindNormalArray:
                case KindNormalTexture: return FormatDxt1;
                default: return FormatRecords;
            }
        }

        struct Entry
        {
            internal uint Kind, Format, Width, Height, Slices, Mips, Filter, Wrap, Aniso, Reserved;
            internal ulong Offset, Size;
        }

        static Entry ReadEntry(byte[] b, int at)
        {
            return new Entry
            {
                Kind = U32(b, at), Format = U32(b, at + 4), Width = U32(b, at + 8), Height = U32(b, at + 12), Slices = U32(b, at + 16), Mips = U32(b, at + 20),
                Filter = U32(b, at + 24), Wrap = U32(b, at + 28), Aniso = U32(b, at + 32), Reserved = U32(b, at + 36), Offset = U64(b, at + 40), Size = U64(b, at + 48),
            };
        }

        // printable ASCII without a space (0x21..0x7E), terminated by the first zero, and nothing but zeros after it
        static bool ReadMapKey(byte[] b, int at, out string key)
        {
            key = null;
            int end = 0;
            while (end < MapKeyField && b[at + end] != 0)
            {
                end++;
            }
            if (end == 0)
            {
                return false;
            }
            for (int i = 0; i < end; i++)
            {
                if (b[at + i] < 0x21 || b[at + i] > 0x7E)
                {
                    return false;
                }
            }
            for (int i = end; i < MapKeyField; i++)
            {
                if (b[at + i] != 0)
                {
                    return false;
                }
            }
            key = Encoding.ASCII.GetString(b, at, end);
            return true;
        }

        static long Clamp(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;

        static uint U32(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

        static ulong U64(byte[] b, int at) => U32(b, at) | ((ulong)U32(b, at + 4) << 32);
    }
}
