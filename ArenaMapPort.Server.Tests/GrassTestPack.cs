#nullable disable
using ArenaMapPort.Client;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Synthetic grass packs for the tests (format AMGP version 1, NativeGrassPackModel.cs): the same bytes as tools/extract/grass_pack.py writes, built here from the format text alone with generated
/// art (the placeholder blocks, so the structure check passes) and valid placement records. Small shapes by default (128 x 128, 8 mips) so a test builds a pack in microseconds; the real shape of Arena's art
/// is <see cref="RealShape"/> and is used only where the exact numbers matter.
/// </summary>
internal static class GrassTestPack
{
    internal sealed class Shape
    {
        internal Shape(int width, int height, int mips) { Width = width; Height = height; Mips = mips; }
        internal int Width, Height, Mips;
        internal int Filter = 2, Wrap = 1, Aniso = 1;
    }

    internal static readonly Shape SmallShape = new Shape(128, 128, 8);
    /// <summary>The shape of Arena's art: 1024 x 1024, eleven mips.</summary>
    internal static readonly Shape RealShape = new Shape(1024, 1024, 11);

    internal const int HeaderSize = NativeGrassPack.HeaderSize;
    internal const int EntryAt = NativeGrassPack.FirstEntryAt;
    internal const int EntrySize = NativeGrassPack.EntrySize;

    /// <summary>Valid placement records (yaw-only, uniform 0.5, slice = i modulo <paramref name="slices"/>), with an optional per-record edit.</summary>
    internal static byte[] Placement(int count, int slices = 5, Action<byte[], int> each = null)
    {
        var file = new byte[count * NativeGrassPlacement.RecordSize];
        for (int i = 0; i < count; i++)
        {
            int at = i * NativeGrassPlacement.RecordSize;
            BitConverter.GetBytes(i % slices).CopyTo(file, at);
            float c = (float)Math.Cos(i * 0.1), s = (float)Math.Sin(i * 0.1);
            float[] m = { c * 0.5f, 0f, -s * 0.5f, 0f, 0f, 0.5f, 0f, 0f, s * 0.5f, 0f, c * 0.5f, 0f, -30f + i % 20, 6.1f, 40f + i % 50, 1f };
            for (int k = 0; k < 16; k++) BitConverter.GetBytes(m[k]).CopyTo(file, at + 4 + 4 * k);
            each?.Invoke(file, i);
        }
        return file;
    }

    /// <summary>A range whose size is its own arithmetic (offset 0).</summary>
    internal static GrassArtRange SizedRange(int width, int height, int slices, int mips, GrassBlockFormat format, int filter = 2, int wrap = 1, int aniso = 1)
    {
        var probe = new GrassArtRange("x", 0, 0, width, height, slices, mips, format, true, filter, wrap, aniso, true);
        return new GrassArtRange("x", 0, probe.ComputedSize, width, height, slices, mips, format, true, filter, wrap, aniso, true);
    }

    /// <summary>The generated art of one range as the pack's payload (placeholder blocks: coherent, not constant).</summary>
    internal static byte[] Art(Shape shape, int slices, GrassBlockFormat format)
    {
        GrassArtRange range = SizedRange(shape.Width, shape.Height, slices, shape.Mips, format, shape.Filter, shape.Wrap, shape.Aniso);
        var data = new byte[range.Size];
        NativeGrassArt.Placeholder.Fill(data, range);
        return data;
    }

    /// <summary>One pack: the header, then the four payloads. The art arguments replace the generated payloads (their length decides the entry's size; the header's size is NOT recomputed).</summary>
    internal static byte[] Build(string mapKey, byte[] placement = null, int slices = 5, Shape shape = null, byte[] albedo = null, byte[] normalArray = null, byte[] normalTexture = null)
    {
        shape ??= SmallShape;
        placement ??= Placement(40, slices);
        albedo ??= Art(shape, slices, GrassBlockFormat.Dxt5);
        normalArray ??= Art(shape, slices, GrassBlockFormat.Dxt1);
        normalTexture ??= Art(shape, 1, GrassBlockFormat.Dxt1);
        var header = new byte[HeaderSize];
        "AMGP"u8.CopyTo(header);
        Put32(header, 4, 1);
        Put32(header, 8, HeaderSize);
        Put32(header, 12, 4);
        System.Text.Encoding.ASCII.GetBytes(mapKey).CopyTo(header, 16);
        ulong offset = HeaderSize;
        (uint kind, uint format, uint w, uint h, uint s, uint mips, uint filter, uint wrap, uint aniso, byte[] payload)[] rows =
        {
            (1, 0, 68, (uint)(placement.Length / 68), (uint)slices, 0, 0, 0, 0, placement),
            (2, 5, (uint)shape.Width, (uint)shape.Height, (uint)slices, (uint)shape.Mips, (uint)shape.Filter, (uint)shape.Wrap, (uint)shape.Aniso, albedo),
            (3, 1, (uint)shape.Width, (uint)shape.Height, (uint)slices, (uint)shape.Mips, (uint)shape.Filter, (uint)shape.Wrap, (uint)shape.Aniso, normalArray),
            (4, 1, (uint)shape.Width, (uint)shape.Height, 1, (uint)shape.Mips, (uint)shape.Filter, (uint)shape.Wrap, (uint)shape.Aniso, normalTexture),
        };
        for (int i = 0; i < 4; i++)
        {
            int at = EntryAt + i * EntrySize;
            Put32(header, at, rows[i].kind); Put32(header, at + 4, rows[i].format); Put32(header, at + 8, rows[i].w); Put32(header, at + 12, rows[i].h);
            Put32(header, at + 16, rows[i].s); Put32(header, at + 20, rows[i].mips); Put32(header, at + 24, rows[i].filter); Put32(header, at + 28, rows[i].wrap);
            Put32(header, at + 32, rows[i].aniso); Put32(header, at + 36, 0);
            Put64(header, at + 40, offset); Put64(header, at + 48, (ulong)rows[i].payload.Length);
            offset += (ulong)rows[i].payload.Length;
        }
        var pack = new byte[(int)offset];
        header.CopyTo(pack, 0);
        int cursor = HeaderSize;
        foreach (var row in rows)
        {
            row.payload.CopyTo(pack, cursor);
            cursor += row.payload.Length;
        }
        return pack;
    }

    /// <summary>The layout of a pack with Arena's real art shapes (1024 x 1024, eleven mips, five slices) and Yard's 10,132 records: parsed from a header only (no payload exists), for the exact arithmetic.</summary>
    internal static GrassPackLayout RealLayout(string mapKey = "arena_yard", int records = 10132)
    {
        byte[] small = Build(mapKey);
        var header = new byte[HeaderSize];
        Array.Copy(small, header, HeaderSize);
        (uint s, long size)[] textures = { (5, 6990640L), (5, 3495320L), (1, 699064L) };
        long placement = (long)records * 68;
        long next = HeaderSize + placement;
        Put32(header, Field(0, 12), (uint)records);
        Put64(header, Field(0, 40), HeaderSize);
        Put64(header, Field(0, 48), (ulong)placement);
        for (int i = 0; i < 3; i++)
        {
            int e = i + 1;
            Put32(header, Field(e, 8), 1024);
            Put32(header, Field(e, 12), 1024);
            Put32(header, Field(e, 16), textures[i].s);
            Put32(header, Field(e, 20), 11);
            Put64(header, Field(e, 40), (ulong)next);
            Put64(header, Field(e, 48), (ulong)textures[i].size);
            next += textures[i].size;
        }
        GrassPackError error = NativeGrassPack.Parse(header, HeaderSize, next, mapKey, out GrassPackLayout layout);
        if (error != GrassPackError.None) throw new InvalidOperationException("the real-shape header is refused: " + error);
        return layout;
    }

    internal static void Put32(byte[] b, int at, uint v) => BitConverter.GetBytes(v).CopyTo(b, at);

    internal static void Put64(byte[] b, int at, ulong v) => BitConverter.GetBytes(v).CopyTo(b, at);

    internal static uint Get32(byte[] b, int at) => BitConverter.ToUInt32(b, at);

    /// <summary>The offset of field <paramref name="field"/> (the byte offset inside the entry, see the format text) of entry <paramref name="entry"/> (0 placement .. 3 normal texture).</summary>
    internal static int Field(int entry, int field) => EntryAt + entry * EntrySize + field;

    /// <summary>The entry's payload offset and size as the header states them.</summary>
    internal static (int offset, int size) Payload(byte[] pack, int entry) => ((int)BitConverter.ToUInt64(pack, Field(entry, 40)), (int)BitConverter.ToUInt64(pack, Field(entry, 48)));
}
