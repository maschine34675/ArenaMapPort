"""Stored-block defect of UnityPy's chunk compressor (2026-10-29, the intermittent Equator noise): a UnityFS data block
with a compression flag whose stored size is not smaller than its content is evidently read by Unity's player as stored data, so
the raw LZ4 stream reaches the consumer.

Covers tools/bundle/fix_stored_blocks.py (scan, repair, --check, in place and --out), tools/validate/validate_bundle_blocks.py
(plain files and AMPB containers read through a stubbed BundleCrypt tool), the corrected chunk compressor of
tools/bundle/chunk_compressor_guard.py and, as source pins, the places that must call these (the two UnityPy savers, build_all.ps1,
validate_distribution.py and the client project's staging target). No BSG data: the bundles here are synthetic UnityFS files written by
`build_unityfs`, and the defective block is a VALID LZ4 stream whose length equals its decoded length, constructed below."""
import ast
import contextlib
import io
import os
import random
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import lz4.block

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "bundle"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import bundle_crypt
import chunk_compressor_guard as guard
import fix_stored_blocks as fsb
import patch_monoscript_identity as pmi
import validate_bundle_blocks as vbb

FIX_TOOL = REPO / "tools" / "bundle" / "fix_stored_blocks.py"
CHUNK = 0x20000   # UnityPy's LZ4 chunk size


def lz4hc(data):
    return lz4.block.compress(data, mode="high_compression", compression=9, store_size=False)


def equal_size_chunk(size):
    """(data, stream): `data` is `size` bytes whose LZ4HC form `stream` is a valid LZ4 stream of exactly `size` bytes. A run of zeros
    saves about one byte per 255 and random bytes cost about one byte per 255, so a run of about size/250 zeros followed by random bytes
    lands on the target; the run length (and the seed, when a length skips over it) is searched."""
    for seed in range(40):
        tail = random.Random(seed).randbytes(size)
        for run in range(max(size // 250 - 40, 1), size // 250 + 60):
            data = bytes(run) + tail[:size - run]
            stream = lz4hc(data)
            if len(stream) == size:
                return data, stream
    raise AssertionError(f"no LZ4HC stream of exactly {size} bytes found")


# A block is (stored bytes, uncompressed size, flags).
def stored(data):
    return (data, len(data), 0)


def packed(data):
    stream = lz4hc(data)
    assert len(stream) < len(data)
    return (stream, len(data), 3)


def build_unityfs(blocks, *, flags=0x243, version=7, nodes=None, tail=b"", player=b"5.x.x", engine=b"2022.3.43f1",
                  table_hash=bytes(range(16))):
    """A UnityFS bundle as UnityPy and Unity write it: signature, format version, two version strings, int64 file size, table sizes and
    flags, 16-byte alignment (format 7), the LZ4HC/LZ4/stored block+node table, 16-byte alignment (flag 0x200), the stored blocks, then
    `tail`. With flag 0x80 the layout is NOT that of a real table-at-end file (only the header matters to the tests that refuse it)."""
    total = sum(usize for _, usize, _ in blocks)
    nodes = nodes if nodes is not None else [(0, total, 4, b"CAB-fixture")]
    table = bytearray(table_hash)
    table += struct.pack(">i", len(blocks))
    for data, usize, bflags in blocks:
        table += struct.pack(">IIH", usize, len(data), bflags)
    table += struct.pack(">i", len(nodes))
    for offset, size, nflags, name in nodes:
        table += struct.pack(">qqI", offset, size, nflags) + name + b"\0"
    method = flags & 0x3F
    stored_table = {0: bytes(table), 2: lz4.block.compress(bytes(table), mode="default", store_size=False), 3: lz4hc(bytes(table))}[method]
    head = bytearray(b"UnityFS\0" + struct.pack(">I", version) + player + b"\0" + engine + b"\0")
    size_at = len(head)
    head += struct.pack(">qIII", 0, len(stored_table), len(table), flags)
    if version >= 7:
        head += bytes(-len(head) % 16)
    body = bytearray(head) + stored_table
    if flags & 0x200:
        body += bytes(-len(body) % 16)
    body += b"".join(data for data, _, _ in blocks) + tail
    struct.pack_into(">q", body, size_at, len(body))
    return bytes(body)


def fixture_unityfs(label):
    """A tiny valid bundle that differs per label (the distribution fixtures use it as the plaintext of an AMPB stand-in)."""
    return build_unityfs([stored(label.encode()), packed(b"fixture " * 64)])


def decoded_stream(bundle, equal_size_is_stored=False):
    """The uncompressed stream. The default decodes every flagged block (what the bundle means); with `equal_size_is_stored` a flagged
    block with csize == usize is taken as stored data (what Unity's player evidently does)."""
    header = fsb.parse_header(fsb.MemoryReader(bundle))
    out = b""
    for block in header.blocks:
        raw = bundle[block.file_offset:block.file_offset + block.csize]
        if block.compression in (fsb.LZ4, fsb.LZ4HC) and not (equal_size_is_stored and block.csize == block.usize):
            raw = lz4.block.decompress(raw, uncompressed_size=block.usize)
        out += raw
    return out


class BundleFixture(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.crafted, cls.crafted_stream = equal_size_chunk(600)
        rnd = random.Random(5)
        cls.pieces = [stored(rnd.randbytes(300)), packed(b"abc" * 400), (cls.crafted_stream, 600, 3),
                      packed(b"xyz" * 500), stored(rnd.randbytes(50))]
        cls.clean_pieces = [cls.pieces[0], cls.pieces[1], stored(cls.crafted), cls.pieces[3], cls.pieces[4]]
        cls.bad = build_unityfs(cls.pieces, tail=b"TRAILER")
        cls.clean = build_unityfs(cls.clean_pieces, tail=b"TRAILER")

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.dir = Path(temporary.name)

    def write(self, name, data):
        path = self.dir / name
        path.write_bytes(data)
        return path

    def run_tool(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = fsb.main([str(a) for a in args])
        return code, out.getvalue()


class ConstructionTests(BundleFixture):
    def test_the_crafted_block_is_a_valid_lz4_stream_as_long_as_its_content(self):
        self.assertEqual(len(self.crafted_stream), len(self.crafted))
        self.assertEqual(lz4.block.decompress(self.crafted_stream, uncompressed_size=len(self.crafted)), self.crafted)
        # a player that takes csize == usize for stored data plays the LZ4 tokens, not the content
        self.assertNotEqual(decoded_stream(self.bad, equal_size_is_stored=True), decoded_stream(self.bad))
        self.assertEqual(decoded_stream(self.bad), decoded_stream(self.clean))


class ScanTests(BundleFixture):
    def test_scan_reports_the_flagged_block_that_is_not_smaller(self):
        header = fsb.parse_header(fsb.MemoryReader(self.bad))
        found = fsb.scan_blocks(header)
        self.assertEqual([b.index for b in found], [2])
        self.assertEqual((found[0].usize, found[0].csize, found[0].compression), (600, 600, fsb.LZ4HC))
        self.assertEqual(len(header.blocks), 5)
        self.assertFalse(header.table_bad)
        self.assertEqual((header.version, header.player, header.engine), (7, b"5.x.x", b"2022.3.43f1"))
        self.assertEqual(header.nodes, ((0, 600 + 300 + 1200 + 1500 + 50, 4, b"CAB-fixture"),))

    def test_block_offsets_and_stream_offsets_follow_the_table(self):
        header = fsb.parse_header(fsb.MemoryReader(self.bad))
        block = header.blocks[2]
        self.assertEqual(self.bad[block.file_offset:block.file_offset + block.csize], self.crafted_stream)
        self.assertEqual(block.stream_offset, 300 + 1200)
        self.assertEqual(header.data_end + len(b"TRAILER"), len(self.bad))
        self.assertEqual(header.size, len(self.bad))

    def test_a_clean_bundle_scans_clean(self):
        self.assertEqual(fsb.scan_blocks(fsb.parse_header(fsb.MemoryReader(self.clean))), [])

    def test_a_block_with_no_content_is_not_a_defect(self):
        bundle = build_unityfs([(b"", 0, 3), stored(b"data")])
        self.assertEqual(fsb.scan_blocks(fsb.parse_header(fsb.MemoryReader(bundle))), [])

    def test_a_flagged_block_larger_than_its_content_is_a_defect_too(self):
        content = random.Random(2).randbytes(300)
        stream = lz4.block.compress(content, store_size=False)      # random bytes: a valid stream a little longer than its content
        self.assertGreater(len(stream), len(content))
        bundle = build_unityfs([(stream, len(content), 2)])
        self.assertEqual([b.index for b in fsb.scan_blocks(fsb.parse_header(fsb.MemoryReader(bundle)))], [0])

    def test_lz4_and_stored_tables_and_format_six_headers_parse(self):
        for flags, version in ((0x242, 7), (0x240, 7), (0x203 | 0x40, 7), (0x43, 6)):
            with self.subTest(flags=hex(flags), version=version):
                bundle = build_unityfs(self.pieces, flags=flags, version=version)
                header = fsb.parse_header(fsb.MemoryReader(bundle))
                self.assertEqual([b.index for b in fsb.scan_blocks(header)], [2])
                self.assertEqual(fsb.parse_header(fsb.MemoryReader(bundle)).data_offset, header.blocks[0].file_offset)

    def test_truncated_and_foreign_files_are_refused(self):
        for data in (b"", b"PK\x03\x04", b"UnityFS\0", self.bad[:40], self.bad[:80]):
            with self.subTest(length=len(data)):
                with self.assertRaises(fsb.BundleBlockError):
                    fsb.parse_header(fsb.MemoryReader(data))

    def test_a_table_at_the_end_of_the_file_is_refused(self):
        bundle = build_unityfs(self.pieces, flags=0x243 | 0x80)
        with self.assertRaisesRegex(fsb.BundleBlockError, "end of the file"):
            fsb.parse_header(fsb.MemoryReader(bundle))

    def test_an_lzma_table_is_refused(self):
        bundle = bytearray(self.bad)
        header = fsb.parse_header(fsb.MemoryReader(self.bad))
        struct.pack_into(">I", bundle, header.size_field_offset + 16, (header.flags & ~0x3F) | 1)
        with self.assertRaisesRegex(fsb.BundleBlockError, "lzma"):
            fsb.parse_header(fsb.MemoryReader(bytes(bundle)))


class FlagMeaningTests(BundleFixture):
    """Flag 0x200 is the data padding only from 2020.3.34 / 2021.3.2 / 2022.1.1 (UnityPy's BundleFile.read_fs rule); before, it marks an
    encrypted bundle, and later engines mark encryption with 0x400 / 0x1000. The tool must not rewrite what it cannot read."""

    def parse(self, engine, flags=0x243):
        return fsb.parse_header(fsb.MemoryReader(build_unityfs(self.pieces, flags=flags, engine=engine)))

    def test_the_engines_with_a_padded_data_region_parse(self):
        for engine in (b"2022.3.43f1", b"2022.1.1f1", b"2021.3.2f1", b"2021.3.40f1", b"2020.3.34f1", b"2020.3.48f1", b"2023.1.0a1", b"6000.0.1f1"):
            with self.subTest(engine=engine):
                header = self.parse(engine)
                self.assertEqual([b.index for b in fsb.scan_blocks(header)], [2])
                self.assertEqual(header.data_offset % 16, 0)

    def test_flag_0x200_on_an_engine_that_means_encryption_is_refused(self):
        for engine in (b"2019.4.10f1", b"2020.3.33f1", b"2021.3.1f1", b"2022.1.0f1", b"2018.4.36f1", b"5.6.7f1", b"no version"):
            with self.subTest(engine=engine):
                with self.assertRaisesRegex(fsb.BundleBlockError, "encrypted bundle"):
                    self.parse(engine)

    def test_an_older_engine_without_flag_0x200_still_parses(self):
        header = self.parse(b"2019.4.10f1", flags=0x43)
        self.assertEqual([b.index for b in fsb.scan_blocks(header)], [2])

    def test_unity_encryption_flags_of_the_newer_engines_are_refused(self):
        for flags in (0x243 | 0x400, 0x243 | 0x1000):
            with self.subTest(flags=hex(flags)):
                with self.assertRaisesRegex(fsb.BundleBlockError, "encrypted by Unity"):
                    self.parse(b"2022.3.43f1", flags=flags)

    def test_the_command_line_names_the_refusal_and_leaves_the_file_alone(self):
        path = self.write("old.bundle", build_unityfs(self.pieces, flags=0x243, engine=b"2019.4.10f1", tail=b"TRAILER"))
        before = path.read_bytes()
        for args in ((path,), ("--check", path)):
            code, out = self.run_tool(*args)
            self.assertEqual(code, 1)
            self.assertTrue(out.splitlines()[-1].startswith("BLOCKS_FAIL "))
            self.assertIn("encrypted bundle", out)
        self.assertEqual(path.read_bytes(), before)


class EncryptedContainerMessageTests(BundleFixture):
    def test_an_ampb_container_is_named_as_such(self):
        path = self.write("arena_x_scenes.bundle", b"AMPB" + bytes(600))
        for args in ((path,), ("--check", path)):
            code, out = self.run_tool(*args)
            self.assertEqual(code, 1)
            self.assertIn("encrypted AMPB container", out)
            self.assertIn("plaintext in work/build", out)
            self.assertNotIn("signature is missing", out)
        self.assertEqual(path.read_bytes(), b"AMPB" + bytes(600))

    def test_any_other_foreign_file_keeps_the_signature_message(self):
        path = self.write("foreign.bundle", b"PK\x03\x04" + bytes(600))
        code, out = self.run_tool("--check", path)
        self.assertEqual(code, 1)
        self.assertIn("signature is missing", out)


class CheckModeTests(BundleFixture):
    def test_check_exits_one_names_the_block_and_writes_nothing(self):
        path = self.write("bad.bundle", self.bad)
        code, out = self.run_tool("--check", path)
        self.assertEqual(code, 1)
        self.assertIn("bad block 2 of 5: lz4hc flag 0x3, csize 600 >= usize 600", out)
        self.assertEqual(out.splitlines()[-1].split()[0], "BLOCKS_FAIL")
        self.assertEqual(path.read_bytes(), self.bad)
        self.assertEqual(sorted(p.name for p in self.dir.iterdir()), ["bad.bundle"])

    def test_check_of_a_clean_bundle_is_ok(self):
        path = self.write("clean.bundle", self.clean)
        code, out = self.run_tool("--check", path)
        self.assertEqual(code, 0)
        self.assertEqual(out.strip(), f"BLOCKS_OK {path} repaired=0")

    def test_the_command_line_exits_one_for_a_defective_bundle(self):
        path = self.write("bad.bundle", self.bad)
        proc = subprocess.run([sys.executable, str(FIX_TOOL), "--check", str(path)], capture_output=True, text=True)
        self.assertEqual(proc.returncode, 1, proc.stdout + proc.stderr)
        self.assertTrue(proc.stdout.splitlines()[-1].startswith("BLOCKS_FAIL "))
        clean = self.write("clean.bundle", self.clean)
        proc = subprocess.run([sys.executable, str(FIX_TOOL), "--check", str(clean)], capture_output=True, text=True)
        self.assertEqual((proc.returncode, proc.stdout.splitlines()[-1]), (0, f"BLOCKS_OK {clean} repaired=0"))

    def test_check_and_out_cannot_be_combined(self):
        path = self.write("bad.bundle", self.bad)
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            fsb.main(["--check", "--out", str(self.dir / "o"), str(path)])
        self.assertEqual(caught.exception.code, 2)


class RepairTests(BundleFixture):
    def repaired(self, source=None):
        src = self.write("src.bundle", self.bad if source is None else source)
        dst = self.dir / "dst.bundle"
        result = fsb.repair(src, dst)
        return result, dst.read_bytes()

    def test_repair_clears_the_flag_and_keeps_the_stream_and_every_other_block(self):
        result, fixed = self.repaired()
        self.assertEqual([b.index for b in result.blocks], [2])
        old = fsb.parse_header(fsb.MemoryReader(self.bad))
        new = fsb.parse_header(fsb.MemoryReader(fixed))
        self.assertEqual(fsb.scan_blocks(new), [])
        block = new.blocks[2]
        self.assertEqual((block.flags, block.csize, block.usize), (0, 600, 600))
        self.assertEqual(fixed[block.file_offset:block.file_offset + block.csize], self.crafted)
        for i in (0, 1, 3, 4):
            a, b = old.blocks[i], new.blocks[i]
            self.assertEqual((a.usize, a.csize, a.flags), (b.usize, b.csize, b.flags))
            self.assertEqual(self.bad[a.file_offset:a.file_offset + a.csize], fixed[b.file_offset:b.file_offset + b.csize])
        self.assertEqual((new.version, new.player, new.engine, new.nodes, new.table_hash),
                         (old.version, old.player, old.engine, old.nodes, old.table_hash))
        self.assertEqual(new.size, len(fixed))
        self.assertEqual(fixed[new.data_end:], b"TRAILER")
        # the decoded stream is the same, and a player that reads equal-size blocks as stored now plays it
        self.assertEqual(decoded_stream(fixed), decoded_stream(self.bad))
        self.assertEqual(decoded_stream(fixed, equal_size_is_stored=True), decoded_stream(self.bad))
        # only the table fields of the header, the table and the one block changed
        self.assertEqual(len(fixed), len(self.bad))
        self.assertEqual(fixed[:old.size_field_offset], self.bad[:old.size_field_offset])

    def test_a_clean_bundle_is_not_rewritten(self):
        src = self.write("clean.bundle", self.clean)
        dst = self.dir / "dst.bundle"
        result = fsb.repair(src, dst)
        self.assertFalse(result.changed)
        self.assertEqual(result.blocks, [])
        self.assertFalse(dst.exists())

    def test_repair_to_the_same_file_is_refused(self):
        src = self.write("src.bundle", self.bad)
        with self.assertRaises(ValueError):
            fsb.repair(src, src)

    def test_in_place_repair_replaces_the_file_and_leaves_no_temporary(self):
        path = self.write("bad.bundle", self.bad)
        result = fsb.repair_file(path)
        self.assertEqual([b.index for b in result.blocks], [2])
        self.assertEqual(fsb.scan_blocks(fsb.parse_header(fsb.MemoryReader(path.read_bytes()))), [])
        self.assertEqual(sorted(p.name for p in self.dir.iterdir()), ["bad.bundle"])

    def test_a_second_repair_is_a_noop(self):
        path = self.write("bad.bundle", self.bad)
        fsb.repair_file(path)
        once = path.read_bytes()
        stamp = path.stat().st_mtime_ns
        result = fsb.repair_file(path)
        self.assertFalse(result.changed)
        self.assertEqual(path.read_bytes(), once)
        self.assertEqual(path.stat().st_mtime_ns, stamp)
        code, out = self.run_tool(path)
        self.assertEqual(code, 0)
        self.assertEqual(out.splitlines()[-1], f"BLOCKS_OK {path} repaired=0")
        self.assertEqual(path.read_bytes(), once)

    def test_the_command_reports_each_repaired_block_and_ends_with_blocks_ok(self):
        path = self.write("bad.bundle", self.bad)
        code, out = self.run_tool(path)
        self.assertEqual(code, 0)
        lines = out.splitlines()
        self.assertEqual(sum(1 for line in lines if line.lstrip().startswith("repaired block 2 of 5")), 1)
        self.assertEqual(lines[-1], f"BLOCKS_OK {path} repaired=1")
        self.assertEqual(fsb.scan_blocks(fsb.parse_header(fsb.MemoryReader(path.read_bytes()))), [])

    def test_out_writes_a_copy_and_leaves_the_source_alone(self):
        bad = self.write("bad.bundle", self.bad)
        clean = self.write("clean.bundle", self.clean)
        out_dir = self.dir / "fixed"
        code, out = self.run_tool("--out", out_dir, bad, clean)
        self.assertEqual(code, 0, out)
        self.assertEqual(bad.read_bytes(), self.bad)
        self.assertEqual(fsb.scan_blocks(fsb.parse_header(fsb.MemoryReader((out_dir / "bad.bundle").read_bytes()))), [])
        self.assertEqual((out_dir / "clean.bundle").read_bytes(), self.clean)   # a clean bundle is copied through unchanged
        self.assertEqual(sorted(p.name for p in out_dir.iterdir()), ["bad.bundle", "clean.bundle"])
        self.assertEqual([line for line in out.splitlines() if line.startswith("BLOCKS_")],
                         [f"BLOCKS_OK {bad} repaired=1", f"BLOCKS_OK {clean} repaired=0"])

    def test_several_bundles_are_handled_and_a_failure_does_not_stop_the_rest(self):
        bad = self.write("bad.bundle", self.bad)
        lzma_table = bytearray(self.bad)
        struct.pack_into(">I", lzma_table, fsb.parse_header(fsb.MemoryReader(self.bad)).size_field_offset + 16, 0x241)
        broken = self.write("broken.bundle", bytes(lzma_table))
        code, out = self.run_tool(broken, bad)
        self.assertEqual(code, 1)
        results = [line for line in out.splitlines() if line.startswith("BLOCKS_")]
        self.assertTrue(results[0].startswith(f"BLOCKS_FAIL {broken}: "))
        self.assertEqual(results[1], f"BLOCKS_OK {bad} repaired=1")

    def test_a_missing_file_is_a_failure(self):
        code, out = self.run_tool(self.dir / "absent.bundle")
        self.assertEqual(code, 1)
        self.assertIn("BLOCKS_FAIL", out.splitlines()[-1])

    def test_a_table_at_the_end_is_refused_and_the_file_stays_untouched(self):
        data = build_unityfs(self.pieces, flags=0x243 | 0x80)
        path = self.write("end.bundle", data)
        code, out = self.run_tool(path)
        self.assertEqual(code, 1)
        self.assertRegex(out.splitlines()[-1], r"^BLOCKS_FAIL .*end of the file")
        self.assertEqual(path.read_bytes(), data)
        self.assertEqual(sorted(p.name for p in self.dir.iterdir()), ["end.bundle"])

    def test_a_flagged_block_that_does_not_decode_is_refused(self):
        garbage = [(b"\xff" * 64, 64, 3), stored(b"data")]
        short = [(b"\x50hello", 6, 3), stored(b"data")]     # a valid LZ4 stream, but of 5 bytes, not 6
        for name, pieces in (("invalid", garbage), ("short", short)):
            with self.subTest(name):
                data = build_unityfs(pieces)
                path = self.write(f"{name}.bundle", data)
                code, out = self.run_tool(path)
                self.assertEqual(code, 1, out)
                self.assertRegex(out.splitlines()[-1], r"^BLOCKS_FAIL .*block 0")
                self.assertEqual(path.read_bytes(), data)
                self.assertNotIn(f"{name}.bundle.tmp", [p.name for p in self.dir.iterdir()])

    def test_a_flagged_lzma_block_that_is_not_smaller_is_refused(self):
        data = build_unityfs([(b"x" * 64, 64, 1)])
        path = self.write("lzma.bundle", data)
        code, out = self.run_tool(path)
        self.assertEqual(code, 1)
        self.assertRegex(out.splitlines()[-1], r"^BLOCKS_FAIL .*lzma")
        self.assertEqual(path.read_bytes(), data)

    def test_a_truncated_bundle_is_refused(self):
        path = self.write("cut.bundle", self.bad[:fsb.parse_header(fsb.MemoryReader(self.bad)).blocks[2].file_offset + 100])
        code, out = self.run_tool(path)
        self.assertEqual(code, 1)
        self.assertRegex(out.splitlines()[-1], r"^BLOCKS_FAIL .*(truncated|cut off)")

    def test_a_failed_verification_leaves_the_original_in_place(self):
        path = self.write("bad.bundle", self.bad)
        real_copy = fsb._copy_range

        def corrupting_copy(src, offset, dst, length):
            real_copy(src, offset, dst, length)
            if length > 100:      # damage the last byte of the first copied run
                dst.seek(-1, os.SEEK_CUR)
                dst.write(b"\xaa")

        with mock.patch.object(fsb, "_copy_range", corrupting_copy):
            code, out = self.run_tool(path)
        self.assertEqual(code, 1)
        self.assertRegex(out.splitlines()[-1], r"^BLOCKS_FAIL .*verification")
        self.assertEqual(path.read_bytes(), self.bad)
        self.assertEqual(sorted(p.name for p in self.dir.iterdir()), ["bad.bundle"])

    def test_a_table_that_would_not_shrink_is_stored_instead_of_flagged(self):
        src = self.write("src.bundle", self.bad)
        dst = self.dir / "dst.bundle"
        with mock.patch.object(fsb, "_pack_table", lambda table, compression: table + b"\0"):
            fsb.repair(src, dst)
        header = fsb.parse_header(fsb.MemoryReader(dst.read_bytes()))
        self.assertEqual(header.table_compression, fsb.NONE)
        self.assertEqual(header.table_csize, header.table_usize)
        self.assertFalse(header.table_bad)
        self.assertEqual(fsb.scan_blocks(header), [])

    def test_bundles_without_the_padding_flag_or_with_an_lz4_table_repair_too(self):
        for flags in (0x43, 0x242, 0x240):
            with self.subTest(flags=hex(flags)):
                bundle = build_unityfs(self.pieces, flags=flags, tail=b"T")
                result, fixed = self.repaired(bundle)
                self.assertEqual([b.index for b in result.blocks], [2])
                new = fsb.parse_header(fsb.MemoryReader(fixed))
                self.assertEqual(fsb.scan_blocks(new), [])
                self.assertEqual(decoded_stream(fixed, equal_size_is_stored=True), decoded_stream(self.bad))
                self.assertEqual(fixed[new.data_end:], b"T")

    def test_a_flagged_block_larger_than_its_content_is_repaired_and_the_data_region_shrinks(self):
        content = random.Random(2).randbytes(300)
        stream = lz4.block.compress(content, store_size=False)
        bundle = build_unityfs([(stream, 300, 2), stored(b"tail data")], tail=b"T")
        result, fixed = self.repaired(bundle)
        self.assertEqual([b.index for b in result.blocks], [0])
        old = fsb.parse_header(fsb.MemoryReader(bundle))
        new = fsb.parse_header(fsb.MemoryReader(fixed))
        self.assertEqual((new.blocks[0].usize, new.blocks[0].csize, new.blocks[0].flags), (300, 300, 0))
        self.assertEqual(new.data_end - new.data_offset, old.data_end - old.data_offset - (len(stream) - 300))
        self.assertEqual(new.size, len(fixed))
        self.assertEqual(decoded_stream(fixed), content + b"tail data")
        self.assertEqual(fixed[new.data_end:], b"T")


class ValidatorTests(BundleFixture):
    def run_validator(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = vbb.main([str(a) for a in args])
        return code, out.getvalue()

    def test_fails_before_the_repair_and_passes_after(self):
        path = self.write("scenes.bundle", self.bad)
        code, out = self.run_validator(path)
        self.assertEqual(code, 1)
        self.assertIn("block 2 of 5", out)
        self.assertIn("csize 600 >= usize 600", out)
        self.assertEqual(out.splitlines()[-1], "BUNDLE_BLOCKS_FAIL 1 of 1 bundle(s)")
        fsb.repair_file(path)
        code, out = self.run_validator(path)
        self.assertEqual(code, 0)
        self.assertEqual(out.splitlines()[-1], "BUNDLE_BLOCKS_OK 1 bundle(s)")
        self.assertIn("5 blocks", out)

    def test_one_defective_bundle_among_clean_ones_fails_the_run(self):
        clean = self.write("clean.bundle", self.clean)
        bad = self.write("bad.bundle", self.bad)
        code, out = self.run_validator(clean, bad)
        self.assertEqual(code, 1)
        self.assertEqual(out.splitlines()[-1], "BUNDLE_BLOCKS_FAIL 1 of 2 bundle(s)")
        self.assertIn("ok   clean.bundle", out)
        self.assertIn("FAIL bad.bundle", out)

    def test_bundles_dir_and_missing_and_foreign_files(self):
        self.write("a.bundle", self.clean)
        self.write("b.bundle", self.clean)
        code, out = self.run_validator("--bundles-dir", self.dir)
        self.assertEqual((code, out.splitlines()[-1]), (0, "BUNDLE_BLOCKS_OK 2 bundle(s)"))
        code, out = self.run_validator(self.dir / "absent.bundle")
        self.assertEqual((code, out.splitlines()[-1]), (1, "BUNDLE_BLOCKS_FAIL 1 of 1 bundle(s)"))
        foreign = self.write("foreign.bundle", b"PK\x03\x04 not a bundle")
        code, out = self.run_validator(foreign)
        self.assertEqual(code, 1)
        self.assertIn("cannot be checked", out)

    def test_no_bundles_is_a_usage_error(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            vbb.main([])
        self.assertEqual(caught.exception.code, 2)

    def test_a_defective_block_table_is_reported(self):
        header = fsb.parse_header(fsb.MemoryReader(self.clean))
        with mock.patch.object(vbb, "read_header", return_value=header), \
                mock.patch.object(fsb.Header, "table_bad", new_callable=mock.PropertyMock, return_value=True):
            report = vbb.check_bundle("x.bundle")
        self.assertEqual(len(report.problems), 1)
        self.assertIn("block table has the same defect", report.problems[0])


class StubCrypt:
    """Stands in for tools/bundle/bundle_crypt.py: a file starting with AMPB 'decrypts' to `plaintext_of[name]`; every read is recorded."""
    BundleCryptError = bundle_crypt.BundleCryptError
    is_encrypted = staticmethod(bundle_crypt.is_encrypted)

    def __init__(self, plaintext_of):
        self.plaintext_of = plaintext_of
        self.reads = []
        self.fail = None

    def read_range(self, path, offset, length, key_store=None, clip=False):
        self.reads.append((Path(path).name, offset, length, key_store, clip))
        if self.fail:
            raise bundle_crypt.BundleCryptError(self.fail)
        return self.plaintext_of[Path(path).name][offset:offset + length]


class EncryptedBundleTests(BundleFixture):
    def test_an_ampb_container_is_read_through_the_tool_header_and_table_only(self):
        path = self.write("arena_x_scenes.bundle", b"AMPB container stand-in")
        stub = StubCrypt({path.name: build_unityfs(self.pieces + [stored(bytes(1 << 20))])})
        report = vbb.check_bundle(path, key_store=self.dir / "keys", crypt=stub)
        self.assertEqual(len(report.problems), 1)
        self.assertIn("block 2 of 6", report.problems[0])
        self.assertEqual(len(stub.reads), 2)       # the header probe, then the table
        header = fsb.parse_header(fsb.MemoryReader(stub.plaintext_of[path.name]))
        self.assertEqual(stub.reads[0][:3], (path.name, 0, fsb.HEADER_PROBE))
        self.assertEqual(stub.reads[1][:3], (path.name, header.table_offset, header.table_csize))
        self.assertTrue(all(r[3] == self.dir / "keys" and r[4] is True for r in stub.reads))   # key store passed, reads clipped
        self.assertTrue(all(offset + length <= header.data_offset or offset == 0 for _, offset, length, _, _ in stub.reads))

    def test_a_clean_container_passes_and_encrypted_true_forces_the_tool(self):
        path = self.write("arena_x_scenes.bundle", b"AMPB container stand-in")
        stub = StubCrypt({path.name: self.clean})
        self.assertEqual(vbb.check_bundle(path, crypt=stub).problems, [])
        plain = self.write("plain.bundle", self.clean)   # not an AMPB file: auto-detection reads the file, encrypted=True asks the tool
        stub.plaintext_of[plain.name] = self.bad
        self.assertEqual(vbb.check_bundle(plain, crypt=stub).problems, [])
        self.assertEqual(len(vbb.check_bundle(plain, encrypted=True, crypt=stub).problems), 1)

    def test_a_tool_failure_is_a_problem_not_an_exception(self):
        path = self.write("arena_x_scenes.bundle", b"AMPB container stand-in")
        stub = StubCrypt({path.name: self.clean})
        stub.fail = "FAIL there is no content key for 'arena_x' in work/release-keys"
        report = vbb.check_bundle(path, crypt=stub)
        self.assertEqual(report.problems, ["cannot be read through the BundleCrypt tool: " + stub.fail])


class CorrectedCompressorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from UnityPy.helpers import CompressionHelper
        cls.helper = CompressionHelper
        cls.original = staticmethod(CompressionHelper.chunk_based_compress)     # captured before any test installs the correction
        cls.equal_chunk, cls.equal_stream = equal_size_chunk(CHUNK)

    def setUp(self):
        self.addCleanup(self.restore)

    def restore(self):
        self.helper.chunk_based_compress = self.original
        for name, module in list(sys.modules.items()):
            if name.startswith("UnityPy") and module is not None and getattr(module, "chunk_based_compress", None) is guard.corrected_chunk_based_compress:
                module.chunk_based_compress = self.original

    def test_the_crafted_chunk_compresses_to_exactly_the_chunk_size(self):
        self.assertEqual(len(self.equal_chunk), CHUNK)
        self.assertEqual(len(self.equal_stream), CHUNK)

    def test_a_full_chunk_whose_compressed_size_equals_the_chunk_size_is_stored_raw(self):
        data, info = guard.corrected_chunk_based_compress(self.equal_chunk, 3)
        self.assertEqual(info, [(CHUNK, CHUNK, 0)])
        self.assertEqual(data, self.equal_chunk)

    def test_unitypy_itself_writes_that_chunk_compressed(self):
        data, info = self.original(self.equal_chunk, 3)
        if info == [(CHUNK, CHUNK, 0)]:
            self.skipTest("UnityPy stores the chunk raw by itself: fixed upstream, the override can be dropped")
        self.assertEqual(info, [(CHUNK, CHUNK, 3)])      # the defect: flagged, csize == usize
        self.assertEqual(data, self.equal_stream)

    def test_a_last_partial_chunk_that_does_not_shrink_is_stored_raw(self):
        small, _ = equal_size_chunk(600)
        incompressible = random.Random(9).randbytes(CHUNK)
        data, info = guard.corrected_chunk_based_compress(incompressible + small, 3)
        self.assertEqual(info, [(CHUNK, CHUNK, 0), (600, 600, 0)])
        self.assertEqual(data, incompressible + small)
        # UnityPy flags the tail
        self.assertEqual(self.original(incompressible + small, 3)[1], [(CHUNK, CHUNK, 0), (600, 600, 3)])

    def test_everything_else_matches_unitypy(self):
        rnd = random.Random(3)
        payload = b"".join([b"pattern " * 20000, rnd.randbytes(CHUNK), bytes(CHUNK), rnd.randbytes(70000) + b"z" * 70000])
        for flag in (3, 2, 0, 0x43):
            with self.subTest(flag=flag):
                self.assertEqual(guard.corrected_chunk_based_compress(payload, flag), self.original(payload, flag))
        self.assertEqual(guard.corrected_chunk_based_compress(b"", 3), self.original(b"", 3))
        small = b"hello hello hello hello"
        self.assertEqual(guard.corrected_chunk_based_compress(small, 3), self.original(small, 3))

    def test_the_old_names_in_patch_monoscript_identity_are_the_shared_functions(self):
        self.assertIs(pmi.corrected_chunk_based_compress, guard.corrected_chunk_based_compress)
        self.assertIs(pmi.install_corrected_chunk_compressor, guard.install_corrected_chunk_compressor)

    def test_the_correction_is_installed_where_unitypy_saves(self):
        guard.install_corrected_chunk_compressor()
        self.assertIs(self.helper.chunk_based_compress, guard.corrected_chunk_based_compress)
        guard.install_corrected_chunk_compressor()           # idempotent
        self.assertIs(self.helper.chunk_based_compress, guard.corrected_chunk_based_compress)
        stale = [name for name, module in sys.modules.items()
                 if name.startswith("UnityPy") and module is not None and getattr(module, "chunk_based_compress", None) is self.original]
        self.assertEqual(stale, [])

    def test_a_name_bound_import_of_the_original_is_replaced_too(self):
        import types
        probe = types.ModuleType("UnityPy.probe_bound_import")
        probe.chunk_based_compress = self.original
        sys.modules[probe.__name__] = probe
        self.addCleanup(sys.modules.pop, probe.__name__, None)
        guard.install_corrected_chunk_compressor()
        self.assertIs(probe.chunk_based_compress, guard.corrected_chunk_based_compress)

    def test_bundle_file_save_stores_the_chunk_raw_once_installed(self):
        """End to end through UnityPy's own BundleFile.save: a bundle whose first 128 KiB chunk compresses to exactly its size."""
        import UnityPy
        source = build_unityfs([stored(self.equal_chunk), packed(b"tail " * 1000)], nodes=[(0, CHUNK + 5000, 0, b"CAB-fixture.resS")])

        def saved():
            env = UnityPy.load(source)
            bundle = next(iter(env.files.values()))
            return bundle.save(packer=(0x243, 3))          # (data_flag, block_info_flag): LZ4HC table, LZ4HC chunks

        before = saved()
        defects_before = fsb.scan_blocks(fsb.parse_header(fsb.MemoryReader(before)))
        if not defects_before:
            self.skipTest("UnityPy no longer writes the defective block: fixed upstream, the override can be dropped")
        guard.install_corrected_chunk_compressor()
        after = saved()
        self.assertEqual([b.index for b in defects_before], [0])
        self.assertEqual(fsb.scan_blocks(fsb.parse_header(fsb.MemoryReader(after))), [])
        self.assertEqual(decoded_stream(after), decoded_stream(before))
        self.assertEqual(decoded_stream(after)[:CHUNK], self.equal_chunk)


class WiringPinTests(unittest.TestCase):
    """Source pins, like the repo's *SourcePinTests: the repair only helps while the places that must call it still do, in order."""

    @staticmethod
    def source(relative):
        return (REPO / relative).read_text(encoding="utf-8").replace("\r\n", "\n")

    def assertInOrder(self, text, *needles):
        position = -1
        for needle in needles:
            found = text.find(needle, position + 1)
            self.assertGreater(found, position, f"{needle!r} is missing or comes before the previous marker")
            position = found

    def test_patch_monoscript_identity_installs_the_guard_before_it_saves(self):
        text = self.source("tools/bundle/patch_monoscript_identity.py")
        main = text[text.index("def main()"):]
        self.assertInOrder(main, "install_corrected_chunk_compressor()", "bundle_file.save(packer=packer_arg)", "stored_block_defects(out_path)")

    def test_patch_preset_installs_the_guard_before_it_saves(self):
        text = self.source("tools/preset/patch_preset.py")
        save = text[text.index("def save_and_verify("):]
        self.assertInOrder(save, "install_corrected_chunk_compressor()", "env.file.save(packer=packer)")
        self.assertIn("from chunk_compressor_guard import install_corrected_chunk_compressor", text)

    def test_patch_preset_imports_the_shared_guard_from_any_working_directory(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("patch_preset_under_test", REPO / "tools" / "preset" / "patch_preset.py")
        module = importlib.util.module_from_spec(spec)
        with tempfile.TemporaryDirectory() as elsewhere:
            previous = os.getcwd()
            os.chdir(elsewhere)
            try:
                spec.loader.exec_module(module)
            finally:
                os.chdir(previous)
        self.assertIs(module.install_corrected_chunk_compressor, guard.install_corrected_chunk_compressor)

    def test_every_unitypy_bundle_save_in_tools_is_behind_the_guard(self):
        """A new `.save(packer=` caller must install the guard too (and be added to the pins above)."""
        callers = []
        for path in sorted((REPO / "tools").rglob("*.py")):
            if "tests" in path.parts or path.name == "chunk_compressor_guard.py":
                continue
            calls = [node for node in ast.walk(ast.parse(path.read_text(encoding="utf-8")))
                     if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr == "save"
                     and any(keyword.arg == "packer" for keyword in node.keywords)]
            if calls:
                callers.append(path.relative_to(REPO).as_posix())
        self.assertEqual(callers, ["tools/bundle/patch_monoscript_identity.py", "tools/preset/patch_preset.py"])

    def test_build_all_repairs_after_the_patch_step_and_validates_before_validate_extract(self):
        text = self.source("tools/build_all.ps1")
        self.assertInOrder(text, "tools/bundle/patch_monoscript_identity.py", 'Write-Step "3b/4 tools/bundle/fix_stored_blocks.py',
                           "fix_stored_blocks.py FAILED", "# Step 4", "tools/validate/validate_bundle_blocks.py\")",
                           "validate_bundle_blocks.py FAILED", "tools/validate/validate_extract.py\")")
        step = text[text.index('Write-Step "3b/4'):text.index("# Step 4")]
        self.assertIn("foreach ($b in $bundles)", step)       # every bundle of the output directory, not only the scenes bundle
        self.assertNotIn("$Skip", step)                       # mandatory: no switch skips it

    def test_validate_distribution_checks_the_block_tables_between_decrypt_and_key_files(self):
        text = self.source("tools/validate/validate_distribution.py")
        body = text[text.index("def validate():"):]
        self.assertInOrder(body, "validate_encrypted_bundles(plugin/'bundles', keys, key_store_dir())",
                           "validate_block_tables(plugin/'bundles', keys, key_store_dir())",
                           "validate_key_files(plugin, keys, key_store_dir())")

    def test_the_staging_target_runs_the_block_check_before_the_first_encryption(self):
        """A non-DllOnly Release build must not encrypt a plaintext bundle that has the defect (the DllOnly build skips the whole target)."""
        text = self.source("ArenaMapPort.Client/ArenaMapPort.Client.csproj")
        target = text[text.index('<Target Name="ArenaMapPortStageWpOutputs"'):]
        target = target[:target.index("</Target>")]
        gate = target.index("validate_bundle_blocks.py")
        self.assertLess(gate, target.index("stage --map-key"))
        exec_line = next(line for line in target.splitlines() if "validate_bundle_blocks.py" in line and "<Exec" in line)
        self.assertIn("@(ArenaMapPortBundles->", exec_line)   # every staged plaintext bundle, all four maps
        self.assertNotIn("ContinueOnError", exec_line)
        self.assertNotIn("IgnoreExitCode", exec_line)


if __name__ == "__main__":
    unittest.main()
