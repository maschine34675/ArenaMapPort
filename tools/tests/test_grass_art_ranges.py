"""The pinned grass art facts of ArenaMapPort.Client/NativeGrassArtModel.cs against the installed Arena (PLAN-YARD item 23, shape E; 2026-11-21).

The client reads Arena's own grass art at raid start: two Texture2DArrays and one Texture2D from sharedassets95.assets.resS at pinned offsets and sizes, and the placement records of
Yard from StreamingAssets/Grass/ArenaGrassPrefabs_Yard.pcl. The numbers live in NativeGrassArtModel.cs / NativeGrassModel.cs as plain decimal constants; this module PARSES them out of the C#
source (so the two cannot drift) and compares them with

  * always: the model's own arithmetic, recomputed here independently (a DXT1/DXT5 mip chain padded to 4 x 4 blocks, slices x chain, the ranges tile the stream);
  * with an installed Arena of a build the pins were measured on (0.6.1.x or 0.6.2.x; else skipped with the reason): what UnityPy reads from sharedassets95.assets (width, height, depth, mip
    count, format number, colour space, mip limit flag, stream path/offset/size), the two file sizes, the slice-major order of the data (decoded: mip k + 1 is the 2 x 2 average of mip k of the
    SAME slice), and the placement file (size, record count, slice histogram, extents, scales).

Nothing here hashes, copies or writes Arena content: sizes, offsets, counts and decoded statistics only. Run: python -m unittest tools/tests/test_grass_art_ranges.py (PYTHONUTF8=1).
"""
import json
import os
import re
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ART_SOURCE = REPO / "ArenaMapPort.Client" / "NativeGrassArtModel.cs"
MODEL_SOURCE = REPO / "ArenaMapPort.Client" / "NativeGrassModel.cs"

ARENA_ROOT = Path(os.environ.get("ARENAMAPPORT_ARENA_DIR", r"D:\Battlestate Games\Escape from Tarkov Arena"))
ARENA_DATA = ARENA_ROOT / "EscapeFromTarkovArena_Data"
PINNED_BUILDS = ("0.6.1.", "0.6.2.")   # measured on 0.6.2.0.48473 (2026-11-21); 0.6.1 shares the resource stream, its assets file was not measured, so a size pin failure there is a finding


def installed_version() -> str:
    try:
        return str(json.loads((ARENA_ROOT / "ConsistencyInfo").read_bytes().decode("utf-8-sig")).get("Version", ""))
    except (OSError, ValueError):
        return ""


def have_arena() -> bool:
    return (ARENA_DATA / "sharedassets95.assets").is_file() and installed_version().startswith(PINNED_BUILDS)


def constants(path: Path) -> dict:
    """Every `const long|int Name = 123;` of a C# file (plain decimal literals, an optional L suffix)."""
    text = path.read_text(encoding="utf-8")
    return {m.group(2): int(m.group(3)) for m in re.finditer(r"\bconst\s+(long|int)\s+(\w+)\s*=\s*(-?\d+)L?\s*;", text)}


ART = constants(ART_SOURCE)


def mip_bytes(width: int, height: int, block_bytes: int) -> int:
    return max(1, (width + 3) // 4) * max(1, (height + 3) // 4) * block_bytes


def chain_bytes(width: int, height: int, mips: int, block_bytes: int) -> int:
    total = 0
    for _ in range(mips):
        total += mip_bytes(width, height, block_bytes)
        width, height = max(1, width // 2), max(1, height // 2)
    return total


# name in the C# file -> (constant prefix, bytes per block, array?)
TEXTURES = {
    "Grass_new_1_N": ("NormalTexture", 8, False),
    "GrassTexArray": ("TexArray", 16, True),
    "GrassNormalArray": ("NormalArray", 8, True),
}


def slices_of(prefix: str) -> int:
    return ART.get(prefix + "Slices", 1)


class SourceConstantsTests(unittest.TestCase):
    """No Arena needed: the constants of the C# source agree with the arithmetic."""

    def test_the_source_has_every_constant(self):
        for prefix in ("NormalTexture", "TexArray", "NormalArray"):
            for suffix in ("Offset", "Size", "Width", "Height", "Mips", "FormatId", "ColorSpace"):
                self.assertIn(prefix + suffix, ART, prefix + suffix)
        for name in ("AssetsSize", "StreamSize", "StreamTailPadding"):
            self.assertIn(name, ART, name)

    def test_every_range_size_is_slices_times_the_padded_mip_chain(self):
        for name, (prefix, bb, _array) in TEXTURES.items():
            with self.subTest(texture=name):
                expect = slices_of(prefix) * chain_bytes(ART[prefix + "Width"], ART[prefix + "Height"], ART[prefix + "Mips"], bb)
                self.assertEqual(ART[prefix + "Size"], expect)

    def test_the_known_per_slice_sizes(self):
        self.assertEqual(chain_bytes(1024, 1024, 11, 16), 1398128)
        self.assertEqual(chain_bytes(1024, 1024, 11, 8), 699064)
        self.assertEqual(ART["TexArraySize"], 5 * 1398128)
        self.assertEqual(ART["NormalArraySize"], 5 * 699064)

    def test_the_ranges_tile_the_stream_in_file_order(self):
        self.assertEqual(ART["NormalTextureOffset"], 0)
        # the plain texture is followed by 8 bytes of padding, the arrays follow each other, the stream ends with 8 bytes of padding
        self.assertEqual(ART["TexArrayOffset"], ART["NormalTextureOffset"] + ART["NormalTextureSize"] + 8)
        self.assertEqual(ART["NormalArrayOffset"], ART["TexArrayOffset"] + ART["TexArraySize"])
        self.assertEqual(ART["StreamSize"], ART["NormalArrayOffset"] + ART["NormalArraySize"] + ART["StreamTailPadding"])
        self.assertEqual(ART["StreamTailPadding"], 8)

    def test_the_model_pins_the_yard_placement_file(self):
        text = MODEL_SOURCE.read_text(encoding="utf-8")
        size = int(re.search(r"expectedBytes:\s*(\d+)L", text).group(1))
        self.assertEqual(size, 688976)
        self.assertEqual(size % 68, 0)
        self.assertEqual(size // 68, 10132)
        self.assertIn('placementRelativePath: "EscapeFromTarkovArena_Data/StreamingAssets/Grass/ArenaGrassPrefabs_Yard.pcl"', text)


@unittest.skipUnless(have_arena(), "the installed Arena is not 0.6.1.x / 0.6.2.x (or not installed): the grass pins were measured on 0.6.2.0.48473")
class InstalledArenaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import UnityPy
        cls.env = UnityPy.load(str(ARENA_DATA / "sharedassets95.assets"))
        cls.textures = {}
        for obj in cls.env.objects:
            if obj.type.name in ("Texture2D", "Texture2DArray"):
                tt = obj.read_typetree()
                cls.textures[tt["m_Name"]] = (obj.type.name, tt)

    def test_the_two_art_files_have_the_pinned_sizes(self):
        self.assertEqual((ARENA_DATA / "sharedassets95.assets").stat().st_size, ART["AssetsSize"])
        self.assertEqual((ARENA_DATA / "sharedassets95.assets.resS").stat().st_size, ART["StreamSize"])

    def test_unitypy_reads_exactly_the_pinned_ranges(self):
        self.assertEqual(sorted(self.textures), sorted(TEXTURES))
        for name, (prefix, _bb, is_array) in TEXTURES.items():
            kind, tt = self.textures[name]
            with self.subTest(texture=name):
                self.assertEqual(kind, "Texture2DArray" if is_array else "Texture2D")
                self.assertEqual(tt["m_Width"], ART[prefix + "Width"])
                self.assertEqual(tt["m_Height"], ART[prefix + "Height"])
                self.assertEqual(tt["m_MipCount"], ART[prefix + "Mips"])
                self.assertEqual(tt["m_ColorSpace"], ART[prefix + "ColorSpace"])
                if is_array:
                    self.assertEqual(tt["m_Depth"], ART[prefix + "Slices"])
                    self.assertEqual(tt["m_Format"], ART[prefix + "FormatId"])
                    self.assertEqual(tt["m_DataSize"], ART[prefix + "Size"])
                    self.assertTrue(tt["m_IgnoreMipmapLimit"])
                else:
                    self.assertEqual(tt["m_TextureFormat"], ART[prefix + "FormatId"])
                    self.assertEqual(tt["m_CompleteImageSize"], ART[prefix + "Size"])
                    self.assertFalse(tt["m_IgnoreMipmapLimit"])
                sd = tt["m_StreamData"]
                self.assertEqual(sd["path"], "sharedassets95.assets.resS")
                self.assertEqual(sd["offset"], ART[prefix + "Offset"])
                self.assertEqual(sd["size"], ART[prefix + "Size"])
                self.assertEqual(tt["m_TextureSettings"]["m_FilterMode"], 2)       # trilinear
                self.assertEqual(tt["m_TextureSettings"]["m_WrapU"], 1)            # clamp

    def test_the_formats_are_the_block_compressed_ones_the_byte_arithmetic_assumes(self):
        # GraphicsFormat 101 = RGBA_DXT5_UNorm (16 B per block), 97 = RGB_DXT1_UNorm (8 B), TextureFormat 10 = DXT1: the byte sizes above only work out for these block sizes
        self.assertEqual(ART["TexArrayFormatId"], 101)
        self.assertEqual(ART["NormalArrayFormatId"], 97)
        self.assertEqual(ART["NormalTextureFormatId"], 10)

    def test_the_data_is_slice_major_with_the_whole_chain_per_slice(self):
        import numpy as np

        def decode_rgb(buf, w, h, bpb):
            a = np.frombuffer(buf, dtype=np.uint8).reshape((w // 4) * (h // 4), bpb)[:, bpb - 8:]
            c0 = a[:, 0].astype(np.uint16) | (a[:, 1].astype(np.uint16) << 8)
            c1 = a[:, 2].astype(np.uint16) | (a[:, 3].astype(np.uint16) << 8)

            def rgb(c):
                return np.stack([((c >> 11) & 31) * 255.0 / 31, ((c >> 5) & 63) * 255.0 / 63, (c & 31) * 255.0 / 31], axis=1)
            p0, p1 = rgb(c0), rgb(c1)
            four = (c0 > c1)[:, None]
            p2 = np.where(four, (2 * p0 + p1) / 3, (p0 + p1) / 2)
            p3 = np.where(four, (p0 + 2 * p1) / 3, np.zeros_like(p0))
            pal = np.stack([p0, p1, p2, p3], axis=1)
            word = a[:, 4].astype(np.uint32) | (a[:, 5].astype(np.uint32) << 8) | (a[:, 6].astype(np.uint32) << 16) | (a[:, 7].astype(np.uint32) << 24)
            idx = np.stack([(word >> (2 * i)) & 3 for i in range(16)], axis=1).astype(np.int64)
            px = np.take_along_axis(pal, idx[:, :, None], axis=1)
            return px.reshape(h // 4, w // 4, 4, 4, 3).transpose(0, 2, 1, 3, 4).reshape(h, w, 3)

        def down(img):
            h, w, c = img.shape
            return img.reshape(h // 2, 2, w // 2, 2, c).mean(axis=(1, 3))

        size, off, bb, slices = ART["TexArraySize"], ART["TexArrayOffset"], 16, ART["TexArraySlices"]
        with open(ARENA_DATA / "sharedassets95.assets.resS", "rb") as f:
            f.seek(off)
            data = f.read(size)
        m0 = mip_bytes(1024, 1024, bb)
        m1 = mip_bytes(512, 512, bb)
        per_slice = chain_bytes(1024, 1024, 11, bb)
        for s in (0, 4):
            base = s * per_slice
            slice_major = float(np.abs(down(decode_rgb(data[base:base + m0], 1024, 1024, bb)) - decode_rgb(data[base + m0:base + m0 + m1], 512, 512, bb)).mean())
            a0 = s * m0                                   # mip-major: all mip 0 first, then all mip 1
            a1 = slices * m0 + s * m1
            mip_major = float(np.abs(down(decode_rgb(data[a0:a0 + m0], 1024, 1024, bb)) - decode_rgb(data[a1:a1 + m1], 512, 512, bb)).mean())
            with self.subTest(slice=s):
                self.assertLess(slice_major, 2.5, "the slice-major reading must be a coherent mip chain")
                self.assertGreater(mip_major, 10 * slice_major, "the mip-major reading must not be")

    def test_the_yard_placement_file_has_the_pinned_size_count_and_extents(self):
        import numpy as np
        path = ARENA_DATA / "StreamingAssets" / "Grass" / "ArenaGrassPrefabs_Yard.pcl"
        self.assertTrue(path.is_file())
        size = int(re.search(r"expectedBytes:\s*(\d+)L", MODEL_SOURCE.read_text(encoding="utf-8")).group(1))
        self.assertEqual(path.stat().st_size, size)
        rec = np.dtype([("slice", "<i4"), ("m", "<f4", (16,))])
        self.assertEqual(rec.itemsize, 68)
        a = np.fromfile(path, dtype=rec)
        self.assertEqual(len(a), 10132)
        self.assertEqual(np.bincount(a["slice"]).tolist(), [0, 8, 8103, 1457, 564])
        m = a["m"]
        pos = m[:, 12:15]                                  # column-major Matrix4x4: the translation is floats 12, 13, 14
        self.assertTrue(np.all(np.isfinite(m)))
        self.assertTrue(np.all(m[:, [3, 7, 11]] == 0) and np.all(m[:, 15] == 1))
        lo, hi = pos.min(axis=0), pos.max(axis=0)
        for got, want in ((lo, (-66.95, 5.90, 17.39)), (hi, (9.23, 6.30, 117.81))):
            for g, w in zip(got, want):
                self.assertAlmostEqual(float(g), w, delta=0.011)
        scales = np.concatenate([np.linalg.norm(m[:, 4 * c:4 * c + 3], axis=1) for c in range(3)])
        self.assertAlmostEqual(float(scales.min()), 0.184, delta=0.002)
        self.assertAlmostEqual(float(scales.max()), 1.124, delta=0.002)


if __name__ == "__main__":
    unittest.main()
