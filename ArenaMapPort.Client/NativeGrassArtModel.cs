#nullable disable
using System;
using System.Globalization;

namespace ArenaMapPort.Client
{
    // The Unity-free facts of the grass art (PLAN-YARD item 23; the data comes from the map's staged grass bundle since stage 2, 2026-11-21): how a DXT mip chain is laid out, the facts a range
    // carries (taken from the pack's header, see NativeGrassPackModel.cs: no offset, size or format is pinned in the client any more), the structure checks that need no hash, and the BSG-free
    // placeholder art of the same shape. The worker (NativeGrassArtSource.cs) reads the ranges from the pack; nothing here touches a file or Unity.
    //
    // The three textures are Arena's Texture2DArray GrassTexArray (the albedo with alpha cut-out), Texture2DArray GrassNormalArray and Texture2D Grass_new_1_N. The same art serves every map
    // that has Arena grass (Bowl, Iceberg, Yard use one prefab, one prototype and one set of textures: work/tmp/yard-grass/reports/G2 section 7); each map's pack carries its own copy.

    internal enum GrassBlockFormat
    {
        /// <summary>BC1, 8 bytes per 4 x 4 block (the normal array and the normal texture).</summary>
        Dxt1 = 0,
        /// <summary>BC3, 16 bytes per 4 x 4 block: 8 bytes of alpha, then 8 bytes of colour (the albedo array).</summary>
        Dxt5 = 1,
    }

    /// <summary>Why a range's bytes were refused (codes only, never file content).</summary>
    internal enum GrassArtError
    {
        None = 0,
        /// <summary>A read returned fewer or more bytes than the range's size.</summary>
        ShortRead,
        /// <summary>The mip data is one repeated byte (a zero-filled or wiped file).</summary>
        ConstantData,
        /// <summary>A coarse mip does not average into the next one: the offsets or the order are wrong.</summary>
        MipChainIncoherent,
    }

    /// <summary>One texture inside the pack: where it is, how big, and the facts a Texture2DArray / Texture2D must be created with (all from the pack's header).</summary>
    internal sealed class GrassArtRange
    {
        internal GrassArtRange(string name, long offset, long size, int width, int height, int slices, int mips, GrassBlockFormat format, bool isArray,
            int filterMode, int wrapMode, int aniso, bool ignoreMipmapLimit)
        {
            Name = name;
            Offset = offset;
            Size = size;
            Width = width;
            Height = height;
            Slices = slices;
            MipCount = mips;
            Format = format;
            IsArray = isArray;
            FilterMode = filterMode;
            WrapMode = wrapMode;
            Aniso = aniso;
            IgnoreMipmapLimit = ignoreMipmapLimit;
        }

        internal string Name { get; }
        /// <summary>Byte offset of the range's first byte inside the pack file (absolute, from the start of the file).</summary>
        internal long Offset { get; }
        /// <summary>Byte count: slices x the mip chain.</summary>
        internal long Size { get; }
        internal int Width { get; }
        internal int Height { get; }
        /// <summary>Array depth; 1 for the plain Texture2D.</summary>
        internal int Slices { get; }
        internal int MipCount { get; }
        internal GrassBlockFormat Format { get; }
        internal bool IsArray { get; }
        internal string FormatName => Format == GrassBlockFormat.Dxt1 ? "DXT1" : "DXT5";
        /// <summary>UnityEngine.FilterMode as Arena serialized it: 2 = Trilinear.</summary>
        internal int FilterMode { get; }
        /// <summary>UnityEngine.TextureWrapMode as serialized: 1 = Clamp.</summary>
        internal int WrapMode { get; }
        internal int Aniso { get; }
        /// <summary>Both arrays carry it (EFT's script textures set it too), the plain texture does not.</summary>
        internal bool IgnoreMipmapLimit { get; }

        internal int BlockBytes => Format == GrassBlockFormat.Dxt1 ? 8 : 16;

        internal int MipWidth(int mip) => Math.Max(1, Width >> mip);
        internal int MipHeight(int mip) => Math.Max(1, Height >> mip);

        /// <summary>Blocks per row of a mip: a mip smaller than 4 pixels still occupies one whole block.</summary>
        internal int BlocksAcross(int mip) => Math.Max(1, (MipWidth(mip) + 3) / 4);
        internal int BlocksDown(int mip) => Math.Max(1, (MipHeight(mip) + 3) / 4);

        /// <summary>The bytes of one mip of one slice.</summary>
        internal long MipBytes(int mip) => (long)BlocksAcross(mip) * BlocksDown(mip) * BlockBytes;

        /// <summary>The bytes of one slice's whole chain.</summary>
        internal long SliceBytes
        {
            get
            {
                long sum = 0;
                for (int m = 0; m < MipCount; m++)
                {
                    sum += MipBytes(m);
                }
                return sum;
            }
        }

        /// <summary>Offset of a mip inside one slice's chain: the mips follow each other, largest first.</summary>
        internal long MipOffsetInSlice(int mip)
        {
            long sum = 0;
            for (int m = 0; m < mip; m++)
            {
                sum += MipBytes(m);
            }
            return sum;
        }

        /// <summary>
        /// Offset of (slice, mip) from the start of the range. The order is slice-major: slice 0's whole chain, then slice 1's. Proven on the real file (work/tmp/yard-grass/core/layout_proof.py):
        /// decoded, mip k + 1 of the slice read this way is the 2 x 2 average of mip k of the same slice (mean difference 0.6 to 1.4 of 255), where the mip-major reading gives 5 to 76.
        /// </summary>
        internal long ElementOffset(int slice, int mip) => (long)slice * SliceBytes + MipOffsetInSlice(mip);

        /// <summary>Offset of (slice, mip) inside the pack file.</summary>
        internal long AbsoluteOffset(int slice, int mip) => Offset + ElementOffset(slice, mip);

        /// <summary>The whole range's size from the arithmetic: slices x the chain.</summary>
        internal long ComputedSize => Slices * SliceBytes;
    }

    /// <summary>The checks that need no hash and the placeholder art; every fact of a range comes from the pack.</summary>
    internal static class NativeGrassArt
    {
        /// <summary>The coarse mips that the coherence check compares (mip k against mip k + 1): 2 to 7, i.e. 256 x 256 down to 16 x 16 pixels.</summary>
        internal const int CoherenceFirstMip = 2;
        internal const int CoherenceLastMip = 7;

        /// <summary>
        /// The largest mean absolute colour difference (of 255) that a coherent chain may show between the average of four child blocks and their parent block, averaged over the checked mips.
        /// Measured on the real albedo array: 0.84 to 1.07 per slice (0.4 to 1.5 per mip); a read shifted by 8 or 1000 bytes gives 17 to 27, by 32 bytes (one block row of mip 2) 2.3, a start inside another slice 42, random bytes 49.
        /// A flat normal array cannot tell a shifted read from the right one: its protection is the pack's size arithmetic (and, in a release, the container's chunk hashes).
        /// </summary>
        internal const double MaxMeanBlockDifference = 2.0;

        /// <summary>
        /// Structure check of one range's bytes (exactly <see cref="GrassArtRange.Size"/> long): the length, not one repeated byte, and per slice the mip-chain coherence. No hash, no
        /// digest and nothing a copy could be compared to: a number computed from the data itself.
        /// </summary>
        internal static GrassArtError CheckBlocks(byte[] data, GrassArtRange range)
        {
            if (data == null || data.Length != range.Size)
            {
                return GrassArtError.ShortRead;
            }
            for (int slice = 0; slice < range.Slices; slice++)
            {
                int probeMip = Math.Min(3, range.MipCount - 1);
                long at = range.ElementOffset(slice, probeMip);
                if (IsConstant(data, (int)at, (int)range.MipBytes(probeMip)))
                {
                    return GrassArtError.ConstantData;
                }
                if (MipChainDifference(data, 0, range, slice) > MaxMeanBlockDifference)
                {
                    return GrassArtError.MipChainIncoherent;
                }
            }
            return GrassArtError.None;
        }

        /// <summary>
        /// The mean absolute difference (of 255, over the three colour channels and the checked mips) between the average colour of each parent block of mip k + 1 and the mean of the four
        /// blocks of mip k it covers, for one slice. A block's mean colour is the average of its 16 palette lookups.
        /// </summary>
        internal static double MipChainDifference(byte[] data, int baseOffset, GrassArtRange range, int slice)
        {
            double total = 0;
            int used = 0;
            var child = new float[3];
            var parentMean = new float[3];
            for (int k = CoherenceFirstMip; k <= CoherenceLastMip && k + 1 < range.MipCount; k++)
            {
                int across = range.BlocksAcross(k);
                int down = range.BlocksDown(k);
                int pAcross = range.BlocksAcross(k + 1);
                int pDown = range.BlocksDown(k + 1);
                if (across != pAcross * 2 || down != pDown * 2)
                {
                    break;
                }
                long kAt = baseOffset + range.ElementOffset(slice, k);
                long pAt = baseOffset + range.ElementOffset(slice, k + 1);
                double sum = 0;
                for (int py = 0; py < pDown; py++)
                {
                    for (int px = 0; px < pAcross; px++)
                    {
                        float r = 0, g = 0, b = 0;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            for (int dx = 0; dx < 2; dx++)
                            {
                                long block = kAt + ((long)(py * 2 + dy) * across + px * 2 + dx) * range.BlockBytes;
                                BlockMean(data, (int)block, range.Format, child);
                                r += child[0];
                                g += child[1];
                                b += child[2];
                            }
                        }
                        BlockMean(data, (int)(pAt + ((long)py * pAcross + px) * range.BlockBytes), range.Format, parentMean);
                        sum += Math.Abs(r / 4f - parentMean[0]) + Math.Abs(g / 4f - parentMean[1]) + Math.Abs(b / 4f - parentMean[2]);
                    }
                }
                total += sum / (3.0 * pAcross * pDown);
                used++;
            }
            return used == 0 ? 0.0 : total / used;
        }

        /// <summary>Mean RGB (0..255) of one block: the colour part is the last 8 bytes (all of a DXT1 block, the second half of a DXT5 block): two RGB565 endpoints and sixteen 2-bit indices.</summary>
        internal static void BlockMean(byte[] data, int blockAt, GrassBlockFormat format, float[] rgb)
        {
            int c = format == GrassBlockFormat.Dxt5 ? blockAt + 8 : blockAt;
            int c0 = data[c] | (data[c + 1] << 8);
            int c1 = data[c + 2] | (data[c + 3] << 8);
            float r0 = ((c0 >> 11) & 31) * 255f / 31f, g0 = ((c0 >> 5) & 63) * 255f / 63f, b0 = (c0 & 31) * 255f / 31f;
            float r1 = ((c1 >> 11) & 31) * 255f / 31f, g1 = ((c1 >> 5) & 63) * 255f / 63f, b1 = (c1 & 31) * 255f / 31f;
            bool four = c0 > c1;
            float r2, g2, b2, r3, g3, b3;
            if (four)
            {
                r2 = (2 * r0 + r1) / 3f; g2 = (2 * g0 + g1) / 3f; b2 = (2 * b0 + b1) / 3f;
                r3 = (r0 + 2 * r1) / 3f; g3 = (g0 + 2 * g1) / 3f; b3 = (b0 + 2 * b1) / 3f;
            }
            else
            {
                r2 = (r0 + r1) / 2f; g2 = (g0 + g1) / 2f; b2 = (b0 + b1) / 2f;
                r3 = 0f; g3 = 0f; b3 = 0f;
            }
            uint bits = (uint)(data[c + 4] | (data[c + 5] << 8) | (data[c + 6] << 16) | (data[c + 7] << 24));
            float r = 0, g = 0, b = 0;
            for (int p = 0; p < 16; p++)
            {
                switch ((bits >> (2 * p)) & 3u)
                {
                    case 0: r += r0; g += g0; b += b0; break;
                    case 1: r += r1; g += g1; b += b1; break;
                    case 2: r += r2; g += g2; b += b2; break;
                    default: r += r3; g += g3; b += b3; break;
                }
            }
            rgb[0] = r / 16f;
            rgb[1] = g / 16f;
            rgb[2] = b / 16f;
        }

        static bool IsConstant(byte[] data, int at, int count)
        {
            byte first = data[at];
            for (int i = 1; i < count; i++)
            {
                if (data[at + i] != first)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>A short line for the log: the range, its size and the format (no content).</summary>
        internal static string Describe(GrassArtRange r)
        {
            return r.Name + " " + r.Width.ToString(CultureInfo.InvariantCulture) + "x" + r.Height.ToString(CultureInfo.InvariantCulture)
                + (r.IsArray ? "x" + r.Slices.ToString(CultureInfo.InvariantCulture) : string.Empty)
                + " mips=" + r.MipCount.ToString(CultureInfo.InvariantCulture) + " " + r.FormatName
                + " @" + r.Offset.ToString(CultureInfo.InvariantCulture) + "+" + r.Size.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The BSG-free placeholder art of the same shape (key GrassArt = Placeholder; no art byte is read from the pack). It is generated here as compressed blocks: the albedo array holds
        /// one solid colour per slice (0 red, 1 green, 2 blue, 3 yellow, 4 magenta) with a cut-out of slice + 1 vertical blades (alpha 255 inside a blade, 0 outside; DXT5 alpha blocks that are
        /// all opaque or all clear, decided at the block centre, so no block mixes), so that the slice order of the pcl is visible on the host: each clump shows its slice number in the number of
        /// blades and (before the dry/healthy tint of the colour buffers multiplies it) in its hue. The two normal textures are flat (0.5, 0.5, 1). Mips whose grid is 4 blocks or fewer
        /// across are all opaque, so no slice vanishes at a distance.
        /// </summary>
        internal static class Placeholder
        {
            /// <summary>Grids with at most this many blocks across are filled opaque.</summary>
            internal const int OpaqueGrid = 4;

            /// <summary>The slice colours as RGB bytes.</summary>
            internal static readonly byte[][] SliceColors =
            {
                new byte[] { 255, 0, 0 },
                new byte[] { 0, 255, 0 },
                new byte[] { 0, 0, 255 },
                new byte[] { 255, 255, 0 },
                new byte[] { 255, 0, 255 },
            };

            internal static ushort Rgb565(int r, int g, int b) => (ushort)(((r * 31 + 127) / 255 << 11) | ((g * 63 + 127) / 255 << 5) | ((b * 31 + 127) / 255));

            /// <summary>True where the cut-out of <paramref name="slice"/> is opaque for the block (bx, by) of a grid of bw x bh blocks.</summary>
            internal static bool Opaque(int slice, int bx, int by, int bw, int bh)
            {
                if (bw <= OpaqueGrid || bh <= OpaqueGrid)
                {
                    return true;
                }
                int n = slice + 1;
                float u = (bx + 0.5f) / bw;
                float v = (by + 0.5f) / bh;
                float halfWidth = 0.4f / n * (1f - 0.8f * v);
                for (int i = 0; i < n; i++)
                {
                    if (Math.Abs(u - (i + 0.5f) / n) <= halfWidth)
                    {
                        return true;
                    }
                }
                return false;
            }

            /// <summary>Fills <paramref name="dst"/> (exactly the range's size) with the placeholder blocks of <paramref name="range"/>.</summary>
            internal static void Fill(byte[] dst, GrassArtRange range)
            {
                if (dst == null || dst.Length != range.Size)
                {
                    throw new ArgumentException("the buffer must be exactly " + range.Size.ToString(CultureInfo.InvariantCulture) + " bytes", nameof(dst));
                }
                Array.Clear(dst, 0, dst.Length);
                for (int slice = 0; slice < range.Slices; slice++)
                {
                    byte[] color = range.Format == GrassBlockFormat.Dxt5 ? SliceColors[slice % SliceColors.Length] : new byte[] { 128, 128, 255 };
                    ushort c565 = Rgb565(color[0], color[1], color[2]);
                    for (int mip = 0; mip < range.MipCount; mip++)
                    {
                        int bw = range.BlocksAcross(mip);
                        int bh = range.BlocksDown(mip);
                        long at = range.ElementOffset(slice, mip);
                        for (int by = 0; by < bh; by++)
                        {
                            for (int bx = 0; bx < bw; bx++)
                            {
                                int block = (int)(at + ((long)by * bw + bx) * range.BlockBytes);
                                int c = block;
                                if (range.Format == GrassBlockFormat.Dxt5)
                                {
                                    byte alpha = Opaque(slice, bx, by, bw, bh) ? (byte)255 : (byte)0;
                                    dst[block] = alpha;       // alpha0
                                    dst[block + 1] = alpha;   // alpha1; the 48 index bits stay 0: every texel is alpha0
                                    c = block + 8;
                                }
                                dst[c] = (byte)(c565 & 0xFF);          // colour0 = colour1: a solid block, index 0 everywhere
                                dst[c + 1] = (byte)(c565 >> 8);
                                dst[c + 2] = (byte)(c565 & 0xFF);
                                dst[c + 3] = (byte)(c565 >> 8);
                            }
                        }
                    }
                }
            }
        }
    }
}
