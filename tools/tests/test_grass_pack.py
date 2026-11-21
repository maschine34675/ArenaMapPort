"""The grass pack (tools/extract/grass_pack.py, tools/extract/extract_grass.py) and the art facts it carries (stage 2 of the native grass; 2026-11-21).

Replaces tools/tests/test_grass_art_ranges.py: the client no longer reads Arena's sharedassets95 at raid time, so the pinned art ranges are the pack tool's business (extract_grass.ART_PINS).

  * always: the format (round trip, the synthetic vector that the C# reader pins as well, every refusal), the mip-chain arithmetic recomputed independently and compared with the rule in
    ArenaMapPort.Client/NativeGrassArtModel.cs (parsed from that source, so the two cannot drift), the extractor's path refusals, the placement checks, the colour constants it parses;
  * with an installed Arena 0.6.1.x or 0.6.2.x and UnityPy/numpy (else skipped with the reason): the three packs of Bowl, Iceberg and Yard are built into work/tmp, parse, carry the
    measured counts and histograms, are deterministic and verify against the installation.

Nothing here hashes or copies Arena content into a tracked file. Run: python -m unittest tools.tests.test_grass_pack (PYTHONUTF8=1).
"""
import contextlib
import io
import json
import os
import re
import struct
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
import extract_grass as eg  # noqa: E402
import grass_pack as gp  # noqa: E402

ART_SOURCE = REPO / "ArenaMapPort.Client" / "NativeGrassArtModel.cs"
PINNED_BUILDS = ("0.6.1.", "0.6.2.")
ARENA_DATA = eg.arena_data_dir()

# The synthetic pack, byte for byte (800 bytes). The C# reader pins the same hex (work/tmp/grass2/handoff/pack.md describes the inputs).
SYNTHETIC_HEX = (
    "414d47500100000010010000040000006172656e615f7465737400000000000000000000000000000000000000000000"
    "010000000000000044000000020000000200000000000000000000000000000000000000000000001001000000000000"
    "880000000000000002000000050000000800000008000000020000000400000002000000010000000100000000000000"
    "9801000000000000e0000000000000000300000001000000080000000800000002000000040000000200000001000000"
    "010000000000000078020000000000007000000000000000040000000100000008000000080000000100000004000000"
    "02000000010000001000000000000000e8020000000000003800000000000000010000000000003f0000000000000000"
    "00000000000000000000003f000000000000000000000000000000000000003f000000000000803f0000004000004040"
    "0000803f000000000000803f000000000000000000000000000000000000803f00000000000000000000000000000000"
    "0000803f00000000000090c00000803e000000410000803f01060b10151a1f24292e33383d42474c51565b60656a6f74"
    "797e83888d92979ca1a6abb0b5babfc4c9ced3d8dde2e7ecf1f6fb00050a0f14191e23282d32373c41464b50555a5f64"
    "696e73787d82878c91969ba0a5aaafb4b9bec3c8cdd2d7dce1e6ebf0f5faff04090e13181d22272c31363b40454a4f54"
    "595e63686d72777c81868b90959a9fa4a9aeb3b8bdc2c7ccd1d6dbe0e5eaeff4f9fe03080d12171c21262b30353a3f44"
    "494e53585d62676c71767b80858a8f94999ea3a8adb2b7bcc1c6cbd0d5dadfe4e9eef3f8fd02070c11161b20252a2f34"
    "393e43484d52575c0205080b0e1114171a1d202326292c2f3235383b3e4144474a4d505356595c5f6265686b6e717477"
    "7a7d808386898c8f9295989b9ea1a4a7aaadb0b3b6b9bcbfc2c5c8cbced1d4d7dadde0e3e6e9eceff2f5f8fbfe010407"
    "0a0d101316191c1f2225282b2e3134373a3d404346494c4f030e19242f3a45505b66717c87929da8b3bec9d4dfeaf500"
    "0b16212c37424d58636e79848f9aa5b0bbc6d1dce7f2fd08131e29343f4a5560"
)


def _pattern(n, mul, add):
    return bytes((i * mul + add) & 0xFF for i in range(n))


def _record(slice_index, sx, sy, sz, tx, ty, tz):
    return struct.pack("<i16f", slice_index, sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0, tx, ty, tz, 1)


def synthetic_parts():
    placement = _record(1, 0.5, 0.5, 0.5, 1.0, 2.0, 3.0) + _record(0, 1.0, 1.0, 1.0, -4.5, 0.25, 8.0)
    facts = {"albedo": gp.TexFacts(8, 8, 2, 4, 2, 1, 1), "normal_array": gp.TexFacts(8, 8, 2, 4, 2, 1, 1), "normal_texture": gp.TexFacts(8, 8, 1, 4, 2, 1, 16)}
    return placement, _pattern(224, 5, 1), _pattern(112, 3, 2), _pattern(56, 11, 3), facts


def synthetic_pack() -> bytes:
    placement, alb, nrm, tex, facts = synthetic_parts()
    return gp.build("arena_test", placement, alb, nrm, tex, facts)


def installed_version() -> str:
    return eg.arena_version(ARENA_DATA)


def real_data_skip_reason():
    if not (ARENA_DATA / eg.ART_ASSETS).is_file():
        return "no Arena installation at %s" % ARENA_DATA
    if not installed_version().startswith(PINNED_BUILDS):
        return "the installed Arena is not 0.6.1.x / 0.6.2.x: the grass pins were measured on 0.6.2.0.48473"
    for module in ("UnityPy", "numpy"):
        try:
            __import__(module)
        except ImportError:
            return module + " is not installed"
    return None


def quiet(fn, *args, **kwargs):
    with contextlib.redirect_stdout(io.StringIO()):
        return fn(*args, **kwargs)


class FormatTests(unittest.TestCase):
    def test_the_constants_of_the_contract(self):
        self.assertEqual((gp.MAGIC, gp.VERSION, gp.HEADER_SIZE, gp.ENTRY_COUNT, gp.RECORD_SIZE), (b"AMGP", 1, 272, 4, 68))
        self.assertEqual(gp.FIRST_ENTRY_AT + gp.ENTRY_COUNT * gp.ENTRY_SIZE, gp.HEADER_SIZE)
        self.assertEqual((gp.KIND_PLACEMENT, gp.KIND_ALBEDO, gp.KIND_NORMAL_ARRAY, gp.KIND_NORMAL_TEXTURE), (1, 2, 3, 4))
        self.assertEqual((gp.FORMAT_RECORDS, gp.FORMAT_DXT1, gp.FORMAT_DXT5), (0, 1, 5))

    def test_the_synthetic_pack_is_the_pinned_bytes(self):
        self.assertEqual(synthetic_pack().hex(), SYNTHETIC_HEX)
        self.assertEqual(len(SYNTHETIC_HEX), 1600)

    def test_the_synthetic_pack_parses_to_what_it_encodes(self):
        pack = gp.parse(bytes.fromhex(SYNTHETIC_HEX), expected_map_key="arena_test")
        self.assertEqual((pack.version, pack.map_key, pack.record_count), (1, "arena_test", 2))
        e = pack.entries
        self.assertEqual([(x.kind, x.format, x.width, x.height, x.slices, x.mips, x.filter_mode, x.wrap_mode, x.aniso, x.reserved, x.offset, x.size) for x in e], [
            (1, 0, 68, 2, 2, 0, 0, 0, 0, 0, 272, 136),
            (2, 5, 8, 8, 2, 4, 2, 1, 1, 0, 408, 224),
            (3, 1, 8, 8, 2, 4, 2, 1, 1, 0, 632, 112),
            (4, 1, 8, 8, 1, 4, 2, 1, 16, 0, 744, 56)])
        placement, alb, nrm, tex, _facts = synthetic_parts()
        self.assertEqual(bytes(pack.payload(gp.KIND_PLACEMENT)), placement)
        self.assertEqual(bytes(pack.payload(gp.KIND_ALBEDO)), alb)
        self.assertEqual(bytes(pack.payload(gp.KIND_NORMAL_ARRAY)), nrm)
        self.assertEqual(bytes(pack.payload(gp.KIND_NORMAL_TEXTURE)), tex)
        self.assertEqual(e[-1].offset + e[-1].size, 800)

    def test_build_is_deterministic_and_round_trips(self):
        self.assertEqual(synthetic_pack(), synthetic_pack())
        pack = gp.parse(synthetic_pack())
        placement, alb, nrm, tex, facts = synthetic_parts()
        self.assertEqual(gp.build(pack.map_key, bytes(pack.payload(1)), bytes(pack.payload(2)), bytes(pack.payload(3)), bytes(pack.payload(4)), facts), synthetic_pack())

    def test_the_header_text_in_little_endian(self):
        blob = synthetic_pack()
        self.assertEqual(blob[:4], b"AMGP")
        self.assertEqual(struct.unpack_from("<III", blob, 4), (1, 272, 4))
        self.assertEqual(blob[16:26], b"arena_test")
        self.assertEqual(blob[26:48], bytes(22))
        self.assertEqual(struct.unpack_from("<Q", blob, 48 + 40)[0], 272)

    def test_facts_can_be_given_as_dicts_or_tuples(self):
        placement, alb, nrm, tex, facts = synthetic_parts()
        as_dicts = {k: v.__dict__ for k, v in facts.items()}
        as_tuples = {k: (v.width, v.height, v.slices, v.mips, v.filter_mode, v.wrap_mode, v.aniso) for k, v in facts.items()}
        self.assertEqual(gp.build("arena_test", placement, alb, nrm, tex, as_dicts), synthetic_pack())
        self.assertEqual(gp.build("arena_test", placement, alb, nrm, tex, as_tuples), synthetic_pack())


class RefusalTests(unittest.TestCase):
    def mutated(self, offset, value, width=4):
        blob = bytearray(synthetic_pack())
        blob[offset:offset + width] = value if isinstance(value, bytes) else struct.pack("<I" if width == 4 else "<Q", value)
        return bytes(blob)

    def refused(self, blob, code, **kw):
        with self.assertRaises(gp.GrassPackError) as ctx:
            gp.parse(blob, **kw)
        self.assertEqual(ctx.exception.code, code, str(ctx.exception))

    def entry_at(self, index, field):
        return 48 + index * 56 + field

    def test_too_short(self):
        self.refused(b"", "TooShort")
        self.refused(synthetic_pack()[:271], "TooShort")

    def test_magic_version_header_size_entry_count(self):
        self.refused(self.mutated(0, b"AMGQ"), "BadMagic")
        self.refused(self.mutated(4, 2), "BadVersion")
        self.refused(self.mutated(8, 273), "BadHeaderSize")
        self.refused(self.mutated(12, 3), "BadEntryCount")
        self.refused(self.mutated(12, 5), "BadEntryCount")

    def test_map_key(self):
        self.refused(synthetic_pack(), "MapKeyMismatch", expected_map_key="arena_yard")
        self.refused(self.mutated(16, b"\0" * 32, 32), "BadMapKey")
        self.refused(self.mutated(16, b"arena test" + bytes(22), 32), "BadMapKey")           # a space
        self.refused(self.mutated(16, b"arena\0test" + bytes(22), 32), "BadMapKey")          # bytes after the zero
        self.refused(self.mutated(16, b"arena_t\xe9st" + bytes(22), 32), "BadMapKey")        # not ASCII

    def test_kind_order_and_format(self):
        self.refused(self.mutated(self.entry_at(0, 0), 2), "KindOrder")
        self.refused(self.mutated(self.entry_at(3, 0), 9), "KindOrder")
        self.refused(self.mutated(self.entry_at(0, 4), 1), "BadFormat")
        self.refused(self.mutated(self.entry_at(1, 4), 1), "BadFormat")                       # albedo must be DXT5
        self.refused(self.mutated(self.entry_at(2, 4), 5), "BadFormat")
        self.refused(self.mutated(self.entry_at(3, 4), 5), "BadFormat")

    def test_reserved(self):
        self.refused(self.mutated(self.entry_at(2, 36), 1), "ReservedNotZero")

    def test_placement_shape_and_size(self):
        self.refused(self.mutated(self.entry_at(0, 8), 64), "BadPlacementShape")              # record size
        self.refused(self.mutated(self.entry_at(0, 12), 0), "BadPlacementShape")              # no records
        self.refused(self.mutated(self.entry_at(0, 20), 1), "BadPlacementShape")              # mips
        self.refused(self.mutated(self.entry_at(0, 24), 1), "BadPlacementShape")              # filter
        self.refused(self.mutated(self.entry_at(0, 28), 1), "BadPlacementShape")              # wrap
        self.refused(self.mutated(self.entry_at(0, 32), 1), "BadPlacementShape")              # aniso
        self.refused(self.mutated(self.entry_at(0, 16), 0), "BadPlacementShape")              # slice count
        self.refused(self.mutated(self.entry_at(0, 48), 135, 8), "SizeMismatch")
        self.refused(self.mutated(self.entry_at(0, 12), 3), "SizeMismatch")

    def test_texture_shape_and_arithmetic(self):
        self.refused(self.mutated(self.entry_at(1, 8), 0), "BadTextureShape")
        self.refused(self.mutated(self.entry_at(1, 12), 0), "BadTextureShape")
        self.refused(self.mutated(self.entry_at(1, 20), 0), "BadTextureShape")
        self.refused(self.mutated(self.entry_at(1, 20), 5), "BadTextureShape")                # 8 x 8 has 4 mips
        self.refused(self.mutated(self.entry_at(3, 16), 2), "BadTextureShape")                # the normal texture has one slice
        self.refused(self.mutated(self.entry_at(1, 48), 223, 8), "SizeMismatch")
        self.refused(self.mutated(self.entry_at(2, 48), 56, 8), "SizeMismatch")
        self.refused(self.mutated(self.entry_at(3, 8), 4), "SizeMismatch")                    # the mip chain of 4 x 4 is shorter

    def test_slice_consistency(self):
        # the placement says 2 slices; an albedo of 1 slice would also need a size change, so edit the placement's count and the arrays together
        blob = bytearray(synthetic_pack())
        struct.pack_into("<I", blob, self.entry_at(0, 16), 3)
        self.refused(bytes(blob), "Inconsistent")
        blob = bytearray(synthetic_pack())
        struct.pack_into("<I", blob, self.entry_at(2, 16), 1)
        struct.pack_into("<Q", blob, self.entry_at(2, 48), 56)
        struct.pack_into("<Q", blob, self.entry_at(3, 40), 688)                               # keep the offsets contiguous so only the slice rule can fire
        self.refused(bytes(blob), "Inconsistent")

    def test_contiguity_and_length(self):
        self.refused(self.mutated(self.entry_at(0, 40), 273, 8), "NotContiguous")
        self.refused(self.mutated(self.entry_at(2, 40), 633, 8), "NotContiguous")
        self.refused(synthetic_pack() + b"\0", "FileLength")
        self.refused(synthetic_pack()[:-1], "FileLength")

    def test_build_refuses_what_the_reader_would(self):
        placement, alb, nrm, tex, facts = synthetic_parts()
        with self.assertRaises(gp.GrassPackError) as ctx:
            gp.build("arena_test", placement[:-1], alb, nrm, tex, facts)
        self.assertEqual(ctx.exception.code, "BadPlacementShape")
        with self.assertRaises(gp.GrassPackError) as ctx:
            gp.build("arena_test", placement, alb[:-16], nrm, tex, facts)
        self.assertEqual(ctx.exception.code, "SizeMismatch")
        with self.assertRaises(gp.GrassPackError) as ctx:
            gp.build("arena test", placement, alb, nrm, tex, facts)
        self.assertEqual(ctx.exception.code, "BadMapKey")
        with self.assertRaises(gp.GrassPackError) as ctx:
            gp.build("a" * 33, placement, alb, nrm, tex, facts)
        self.assertEqual(ctx.exception.code, "BadMapKey")
        with self.assertRaises(gp.GrassPackError) as ctx:
            gp.build("arena_test", b"", alb, nrm, tex, facts)
        self.assertEqual(ctx.exception.code, "BadPlacementShape")

    def test_a_32_character_key_fills_the_field(self):
        placement, alb, nrm, tex, facts = synthetic_parts()
        key = "k" * 32
        self.assertEqual(gp.parse(gp.build(key, placement, alb, nrm, tex, facts)).map_key, key)


class ArithmeticTests(unittest.TestCase):
    """The mip-chain arithmetic against an independent recomputation and against the rule in the client's source."""

    @staticmethod
    def independent_chain(w, h, mips, bpb):
        total = 0
        for _ in range(mips):
            total += ((w + 3) // 4 if w >= 4 else 1) * ((h + 3) // 4 if h >= 4 else 1) * bpb
            w, h = max(1, w // 2), max(1, h // 2)
        return total

    def test_the_known_sizes(self):
        self.assertEqual(gp.slice_bytes(1024, 1024, 11, 16), 1398128)
        self.assertEqual(gp.slice_bytes(1024, 1024, 11, 8), 699064)
        self.assertEqual(gp.texture_bytes(1024, 1024, 5, 11, 16), 6990640)
        self.assertEqual(gp.texture_bytes(1024, 1024, 5, 11, 8), 3495320)
        self.assertEqual(gp.slice_bytes(8, 8, 4, 16), 112)          # 4 blocks + three padded single blocks
        self.assertEqual(gp.max_mips(1024, 1024), 11)
        self.assertEqual(gp.max_mips(8, 8), 4)

    def test_the_arithmetic_agrees_with_an_independent_loop(self):
        for w, h in ((1024, 1024), (512, 256), (64, 8), (4, 4), (1, 1), (8, 1)):
            for bpb in (8, 16):
                for mips in range(1, gp.max_mips(w, h) + 1):
                    with self.subTest(w=w, h=h, bpb=bpb, mips=mips):
                        self.assertEqual(gp.slice_bytes(w, h, mips, bpb), self.independent_chain(w, h, mips, bpb))

    def test_the_client_source_states_the_same_rule(self):
        text = re.sub(r"\s+", " ", ART_SOURCE.read_text(encoding="utf-8"))
        for needle in (
            "BlockBytes => Format == GrassBlockFormat.Dxt1 ? 8 : 16",
            "MipWidth(int mip) => Math.Max(1, Width >> mip)",
            "MipHeight(int mip) => Math.Max(1, Height >> mip)",
            "BlocksAcross(int mip) => Math.Max(1, (MipWidth(mip) + 3) / 4)",
            "BlocksDown(int mip) => Math.Max(1, (MipHeight(mip) + 3) / 4)",
            "MipBytes(int mip) => (long)BlocksAcross(mip) * BlocksDown(mip) * BlockBytes",
            "ComputedSize => Slices * SliceBytes",
        ):
            with self.subTest(rule=needle):
                self.assertIn(needle, text)
        slice_rule = re.search(r"long SliceBytes\s*\{\s*get\s*\{(.*?)\}\s*\}", ART_SOURCE.read_text(encoding="utf-8"), re.S)
        self.assertIsNotNone(slice_rule, "SliceBytes is no longer a loop over the mips")
        self.assertRegex(slice_rule.group(1), r"for \(int m = 0; m < MipCount; m\+\+\)")
        self.assertRegex(slice_rule.group(1), r"sum \+= MipBytes\(m\)")

    def test_the_coherence_constants_agree_with_the_client(self):
        text = ART_SOURCE.read_text(encoding="utf-8")
        ints = {m.group(1): int(m.group(2)) for m in re.finditer(r"const int (\w+) = (\d+);", text)}
        self.assertEqual(ints["CoherenceFirstMip"], eg.COHERENCE_FIRST_MIP)
        self.assertEqual(ints["CoherenceLastMip"], eg.COHERENCE_LAST_MIP)
        limit = re.search(r"const double MaxMeanBlockDifference = ([\d.]+);", text)
        self.assertIsNotNone(limit)
        self.assertEqual(float(limit.group(1)), eg.MAX_MEAN_BLOCK_DIFFERENCE)

    def test_the_pins_are_internally_consistent(self):
        previous = 0
        for name, pin in sorted(eg.ART_PINS.items(), key=lambda kv: kv[1]["offset"]):
            bb = gp.BLOCK_BYTES[eg.PACK_FORMAT_OF_KIND[pin["kind"]]]
            self.assertEqual(pin["size"], gp.texture_bytes(pin["width"], pin["height"], pin["slices"], pin["mips"], bb), name)
            self.assertGreaterEqual(pin["offset"], previous, name)
            previous = pin["offset"] + pin["size"]
        self.assertLessEqual(previous, eg.STREAM_SIZE)
        # the plain texture, 8 bytes of padding, the two arrays back to back, 8 bytes of padding
        self.assertEqual(eg.ART_PINS["GrassTexArray"]["offset"], eg.ART_PINS["Grass_new_1_N"]["size"] + 8)
        self.assertEqual(eg.ART_PINS["GrassNormalArray"]["offset"], eg.ART_PINS["GrassTexArray"]["offset"] + eg.ART_PINS["GrassTexArray"]["size"])
        self.assertEqual(eg.STREAM_SIZE, eg.ART_PINS["GrassNormalArray"]["offset"] + eg.ART_PINS["GrassNormalArray"]["size"] + 8)


class ExtractorRuleTests(unittest.TestCase):
    def test_output_only_under_work(self):
        root = eg.work_root().resolve()
        self.assertEqual(eg.checked_out_dir(root / "build" / "grass"), (root / "build" / "grass").resolve())
        self.assertEqual(eg.checked_out_dir(root / "tmp" / "grass2" / "x"), (root / "tmp" / "grass2" / "x").resolve())
        for bad in (REPO / "dist" / "x", REPO / "tools", REPO, Path(tempfile.gettempdir()) / "grass", root.parent / "elsewhere", root / ".." / "dist"):
            with self.subTest(path=str(bad)):
                with self.assertRaises(eg.ExtractError):
                    eg.checked_out_dir(bad)

    def test_a_dist_folder_inside_work_is_refused_too(self):
        with self.assertRaises(eg.ExtractError):
            eg.checked_out_dir(eg.work_root() / "tmp" / "dist" / "x")

    def test_the_pack_cli_refuses_a_foreign_folder_before_reading_anything(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = eg.main(["pack", "--map", "yard", "--out-dir", str(Path(tempfile.gettempdir()) / "grass-pack-refused")])
        self.assertEqual(code, 1)
        self.assertIn("refusing to write", err.getvalue())

    def test_the_maps_table(self):
        self.assertEqual(sorted(eg.MAPS), ["bowl", "iceberg", "yard"])
        self.assertEqual([eg.MAPS[s]["key"] for s in ("bowl", "iceberg", "yard")], ["arena_bowl", "arena_iceberg", "arena_yard"])
        self.assertEqual([eg.MAPS[s]["pcl"] for s in ("bowl", "iceberg", "yard")], ["ArenaGrassPrefabs_Bowl.pcl", "ArenaGrassPrefabs_Sanatorium.pcl", "ArenaGrassPrefabs_Yard.pcl"])
        for stem, row in eg.MAPS.items():
            sidecar = json.loads((REPO / "tools" / "maps" / (stem + ".json")).read_text(encoding="utf-8"))
            self.assertIn(row["scene"], sidecar["all_scene_names"], stem)
        with self.assertRaises(eg.ExtractError):
            eg.resolve_maps("equator")
        self.assertEqual(eg.resolve_maps("all"), ["bowl", "iceberg", "yard"])

    def test_the_client_summer_lists_are_parsed(self):
        dry, healthy = eg.client_summer_lists()
        self.assertEqual((len(dry), len(healthy)), (5, 5))
        self.assertEqual(dry[0], (1.0, struct.unpack("<f", struct.pack("<f", 0.94117653))[0], struct.unpack("<f", struct.pack("<f", 0.078431375))[0], 0.0))
        self.assertEqual(healthy[1], (1.0, 1.0, 1.0, 0.0))
        for row in dry + healthy:
            self.assertEqual(len(row), 4)
            self.assertEqual(row[3], 0.0)

    def test_the_colour_parser_refuses_a_source_without_the_arrays(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / "x.cs"
            f.write_text("class X { }", encoding="utf-8")
            with self.assertRaises(eg.ExtractError):
                eg.client_summer_lists(f)

    def test_the_header_text_says_why_a_pack_is_never_staged_plain(self):
        doc = eg.__doc__
        self.assertIn("NEVER STAGE A PACK PLAIN", doc)
        self.assertIn("key-source", doc)
        self.assertIn("sharedassets95.assets.resS", doc)


def numpy_skip_reason():
    try:
        import numpy  # noqa: F401
    except ImportError:
        return "numpy is not installed"
    return None


@unittest.skipUnless(numpy_skip_reason() is None, numpy_skip_reason() or "")
class PlacementCheckTests(unittest.TestCase):
    def good(self, n=3, slice_index=1):
        return b"".join(_record(slice_index, 1.0, 1.0, 1.0, float(i), 0.0, 0.0) for i in range(n))

    def test_a_good_file(self):
        stats = eg.placement_stats(self.good(), 5)
        self.assertEqual((stats["count"], stats["hist"]), (3, [0, 3, 0, 0, 0]))

    def test_refusals(self):
        with self.assertRaises(eg.ExtractError):
            eg.placement_stats(self.good()[:-1], 5)                 # not divisible by 68
        with self.assertRaises(eg.ExtractError):
            eg.placement_stats(b"", 5)                              # no records
        with self.assertRaises(eg.ExtractError):
            eg.placement_stats(self.good(slice_index=5), 5)         # slice == depth
        with self.assertRaises(eg.ExtractError):
            eg.placement_stats(self.good(slice_index=-1), 5)
        nan = bytearray(self.good())
        nan[4 + 48:4 + 52] = struct.pack("<f", float("nan"))
        with self.assertRaises(eg.ExtractError):
            eg.placement_stats(bytes(nan), 5)
        skew = bytearray(self.good())
        skew[4 + 12:4 + 16] = struct.pack("<f", 0.5)                # matrix element [0][3]
        with self.assertRaises(eg.ExtractError):
            eg.placement_stats(bytes(skew), 5)
        flat = _record(1, 0.0, 1.0, 1.0, 0.0, 0.0, 0.0)
        with self.assertRaises(eg.ExtractError):
            eg.placement_stats(flat, 5)                             # a zero scale
        far = _record(1, 1.0, 1.0, 1.0, 1e6, 0.0, 0.0)
        with self.assertRaises(eg.ExtractError):
            eg.placement_stats(far, 5)

    def test_tilt_and_non_uniform_scale_are_allowed(self):
        # Iceberg's records are tilted up to 50 degrees and scaled non-uniformly: a rotation of the up axis and unequal column lengths pass
        import math
        a = math.radians(40.0)
        m = [1.0, 0, 0, 0, 0, 2.0 * math.cos(a), 2.0 * math.sin(a), 0, 0, -1.5 * math.sin(a), 1.5 * math.cos(a), 0, 3.0, 4.0, 5.0, 1.0]
        stats = eg.placement_stats(struct.pack("<i16f", 2, *m), 5)
        self.assertAlmostEqual(stats["tilt_max"], 40.0, delta=0.01)
        self.assertGreater(stats["nonuniform_max"], 0.4)

    def test_wiped_and_shifted_art_is_refused(self):
        zero = bytes(eg.ART_PINS["GrassNormalArray"]["size"])
        with self.assertRaises(eg.ExtractError):
            eg.check_range_blocks("GrassNormalArray", zero)


@unittest.skipUnless(real_data_skip_reason() is None, real_data_skip_reason() or "")
class InstalledArenaTests(unittest.TestCase):
    OUT = eg.work_root() / "tmp" / "grass2" / "pack" / "test-out"
    # Measured on Arena 0.6.2.0.48473 (the placement files are what the stage-1 report and G2 section 7 list).
    EXPECTED = {
        "bowl": dict(key="arena_bowl", count=113643, hist=[0, 26265, 62125, 11315, 13938], size=7727724),
        "iceberg": dict(key="arena_iceberg", count=15410, hist=[6, 1064, 6357, 1301, 6682], size=1047880),
        "yard": dict(key="arena_yard", count=10132, hist=[0, 8, 8103, 1457, 564], size=688976),
    }
    ART_TOTAL = 6990640 + 3495320 + 699064

    @classmethod
    def setUpClass(cls):
        quiet(eg.cmd_pack, ["bowl", "iceberg", "yard"], cls.OUT)

    def pack_of(self, stem):
        return eg.pack_path(self.OUT, self.EXPECTED[stem]["key"])

    def test_the_three_packs_parse_with_the_measured_counts(self):
        for stem, want in self.EXPECTED.items():
            with self.subTest(map=stem):
                blob = self.pack_of(stem).read_bytes()
                pack = gp.parse(blob, expected_map_key=want["key"])
                self.assertEqual(pack.record_count, want["count"])
                self.assertEqual(pack.entry(gp.KIND_PLACEMENT).size, want["size"])
                self.assertEqual(want["size"], 68 * want["count"])
                self.assertEqual(len(blob), 272 + want["size"] + self.ART_TOTAL)
                self.assertEqual(pack.entry(gp.KIND_ALBEDO).slices, 5)
                stats = eg.placement_stats(bytes(pack.payload(gp.KIND_PLACEMENT)), 5)
                self.assertEqual(stats["hist"], want["hist"])

    def test_the_art_headers_of_every_pack(self):
        for stem, want in self.EXPECTED.items():
            pack = gp.parse(self.pack_of(stem).read_bytes())
            got = [(e.kind, e.format, e.width, e.height, e.slices, e.mips, e.filter_mode, e.wrap_mode, e.aniso, e.size) for e in pack.entries[1:]]
            with self.subTest(map=stem):
                self.assertEqual(got, [(2, 5, 1024, 1024, 5, 11, 2, 1, 1, 6990640), (3, 1, 1024, 1024, 5, 11, 2, 1, 1, 3495320), (4, 1, 1024, 1024, 1, 11, 2, 1, 16, 699064)])

    def test_the_three_packs_share_the_same_art_bytes(self):
        arts = [gp.parse(self.pack_of(s).read_bytes()) for s in self.EXPECTED]
        for kind in (gp.KIND_ALBEDO, gp.KIND_NORMAL_ARRAY, gp.KIND_NORMAL_TEXTURE):
            self.assertTrue(arts[0].payload(kind) == arts[1].payload(kind) == arts[2].payload(kind))

    def test_the_placement_is_the_installed_file_byte_for_byte(self):
        for stem in self.EXPECTED:
            pack = gp.parse(self.pack_of(stem).read_bytes())
            self.assertEqual(bytes(pack.payload(gp.KIND_PLACEMENT)), eg.read_placement(ARENA_DATA, stem), stem)

    def test_the_tool_is_deterministic(self):
        second = eg.work_root() / "tmp" / "grass2" / "pack" / "test-out-2"
        quiet(eg.cmd_pack, ["bowl", "iceberg", "yard"], second)
        for stem in self.EXPECTED:
            with self.subTest(map=stem):
                self.assertEqual(self.pack_of(stem).read_bytes(), eg.pack_path(second, self.EXPECTED[stem]["key"]).read_bytes())

    def test_verify_passes_and_notices_a_damaged_pack(self):
        quiet(eg.cmd_verify, ["bowl", "iceberg", "yard"], self.OUT)
        damaged = eg.work_root() / "tmp" / "grass2" / "pack" / "test-out-damaged"
        damaged.mkdir(parents=True, exist_ok=True)
        blob = bytearray(self.pack_of("yard").read_bytes())
        blob[-5000] ^= 0x01
        eg.pack_path(damaged, "arena_yard").write_bytes(bytes(blob))
        with self.assertRaises(eg.ExtractError):
            quiet(eg.cmd_verify, ["yard"], damaged)

    def test_the_tool_refuses_to_write_outside_work(self):
        with self.assertRaises(eg.ExtractError):
            quiet(eg.cmd_pack, ["yard"], Path(tempfile.gettempdir()) / "grass-pack-refused")
        with self.assertRaises(eg.ExtractError):
            quiet(eg.cmd_pack, ["yard"], REPO / "dist" / "grass")

    def test_the_scenes_use_the_shared_setup_and_the_summer_lists(self):
        dry, healthy = eg.client_summer_lists()
        scripts = eg.ScriptNames(ARENA_DATA)
        for stem in self.EXPECTED:
            with self.subTest(map=stem):
                scene = eg.read_scene_grass(ARENA_DATA, eg.MAPS[stem]["level"], scripts)
                self.assertEqual(len(eg.prove_scene(stem, scene, dry, healthy)), 2)

    def test_a_scene_with_other_colours_is_refused(self):
        dry, healthy = eg.client_summer_lists()
        scene = eg.read_scene_grass(ARENA_DATA, eg.MAPS["iceberg"]["level"], eg.ScriptNames(ARENA_DATA))
        other = [tuple(dry[0])] + [(0.1, 0.1, 0.1, 0.0)] + list(dry[2:])
        with self.assertRaises(eg.ExtractError):
            eg.prove_scene("iceberg", scene, other, healthy)

    def test_a_shifted_read_of_the_albedo_fails_the_structure_check(self):
        data = eg.read_art_ranges(ARENA_DATA)["GrassTexArray"]
        eg.check_range_blocks("GrassTexArray", data)
        for shift in (8, 32, 1000):
            with self.subTest(shift=shift):
                with self.assertRaises(eg.ExtractError):
                    eg.check_range_blocks("GrassTexArray", data[shift:] + data[:shift])

    def test_the_pins_are_what_unitypy_reads(self):
        headers = eg.read_art_headers(ARENA_DATA)
        for name, pin in eg.ART_PINS.items():
            for field in ("width", "height", "slices", "mips", "format_id", "offset", "size", "filter", "wrap", "aniso", "ignore_limit"):
                self.assertEqual(headers[name][field], pin[field], name + "." + field)


if __name__ == "__main__":
    unittest.main()
