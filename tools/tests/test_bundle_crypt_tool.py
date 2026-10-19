"""The BundleCrypt tool (tools/bundle/ArenaMapPort.BundleCrypt, docs/PLAN-ENCRYPTION.md TOOL) and the Python pieces built on it.

Every test runs the BUILT tool (`dotnet build` once per process through tools/bundle/bundle_crypt.py) on synthetic data:
bundle-like files of random bytes and a synthetic "Arena" folder the test creates (level files of random bytes). The tool's
test seam (--arena-unverified, enabled by ARENAMAPPORT_TOOL_TEST=1 in the environment of these tests only) stands in for the
installation check a fake folder could never pass; the staging build and the validators never use it. Nothing here reads the
real Arena except the pin tests at the end, which skip without it. The crypto itself (known answers, container, key derivation)
is proved by the xunit tests of ArenaMapPort.Crypto (BundleCryptoTests.cs); these tests prove the tool, the files it writes,
the key store, the staging and the Python validators that read through it."""
import ast
import hashlib
import json
import os
from pathlib import Path
import random
import re
import shutil
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "bundle"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
sys.path.insert(0, str(REPO / "tools" / "release"))
import bundle_crypt
import make_release_zips as release
import validate_bundle_names as bundle_names
import validate_distribution as distribution

MIB = 1 << 20
REAL_ARENA = Path(os.environ.get("ARENAMAPPORT_ARENA_DIR", r"D:\Battlestate Games\Escape from Tarkov Arena"))
KEY_SOURCES = REPO / "tools" / "bundle" / "key-sources.json"


def setUpModule():
    if not os.environ.get(bundle_crypt.ENV_OVERRIDE) and shutil.which("dotnet") is None:
        raise unittest.SkipTest("dotnet is not installed")
    bundle_crypt.ensure_built()


def random_bytes(seed, size):
    return random.Random(seed).randbytes(size)


def synthetic_unityfs(names, *, at_end=False, fmt=7, data_size=3000, seed=1):
    """A minimal uncompressed UnityFS bundle with the given directory-table names (what validate_bundle_names parses)."""
    info = bytes(16) + struct.pack(">i", 1) + struct.pack(">IIH", data_size, data_size, 0) + struct.pack(">i", len(names))
    offset = 0
    for name in names:
        info += struct.pack(">qqI", offset, 10, 4) + name.encode() + b"\0"
        offset += 10
    header = b"UnityFS\0" + struct.pack(">I", fmt) + b"5.x.x\0" + b"2022.3.43f1\0"
    data = random_bytes(seed, data_size)
    flags = 0x80 if at_end else 0
    header += struct.pack(">qIII", 64 + len(info) + data_size, len(info), len(info), flags)
    if fmt >= 7:
        header += bytes((16 - len(header) % 16) % 16)
    return header + (data + info if at_end else info + data)


class ToolCase(unittest.TestCase):
    """A temporary folder, the tool's test environment and a few helpers."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.tmp = Path(temporary.name)
        environment = patch.dict(os.environ, {"ARENAMAPPORT_TOOL_TEST": "1"})
        environment.start()
        self.addCleanup(environment.stop)
        self.store = self.tmp / "store"

    def tool(self, *args, expect=0):
        proc = bundle_crypt.run(*args, check=False)
        self.assertEqual(proc.returncode, expect, proc.stdout.decode(errors="replace") + proc.stderr.decode(errors="replace"))
        return proc

    def expect_fail(self, *args, fragment=None, code=1):
        """The tool refuses: the exit code and one FAIL/USAGE line naming the cause (never an 'unexpected' crash)."""
        proc = bundle_crypt.run(*args, check=False)
        text = proc.stderr.decode(errors="replace").strip()
        self.assertEqual(proc.returncode, code, text + proc.stdout.decode(errors="replace"))
        self.assertTrue(text.startswith("FAIL " if code == 1 else "USAGE "), text)
        self.assertFalse(text.startswith("FAIL unexpected "), text)  # that is the format of a crash, not of a refusal
        if fragment is not None:
            self.assertEqual(len(text.splitlines()), 1, text)
            self.assertIn(fragment, text)
        return text

    def plain(self, name, size, seed=1, folder=None):
        path = (folder or self.tmp) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"UnityFS\0" + random_bytes(seed, size - 8))
        return path

    def encrypt(self, plain, out, key="arena_test", *extra):
        return self.tool("encrypt", "--map-key", key, "--in", plain, "--out", out, "--key-store", self.store, "--create-key", *extra)

    @staticmethod
    def flip(path, offset):
        data = bytearray(Path(path).read_bytes())
        data[offset] ^= 0x01
        Path(path).write_bytes(bytes(data))


class EncryptRoundTripTests(ToolCase):
    SIZE = 3 * MIB + 123  # four chunks of 1 MiB, the last one 123 bytes

    def setUp(self):
        super().setUp()
        self.source = self.plain("plain.bundle", self.SIZE)
        self.enc = self.tmp / "enc.bundle"

    def test_encrypt_inspect_read_range_decrypt_round_trip(self):
        proc = self.encrypt(self.source, self.enc)
        self.assertIn("CREATED", proc.stdout.decode())
        self.assertRegex(proc.stdout.decode(), r"OK encrypt arena_test enc\.bundle: 3,145,851 B plaintext, .* 4 chunks, written")
        data = self.enc.read_bytes()
        plain = self.source.read_bytes()
        self.assertEqual(data[:4], b"AMPB")
        self.assertNotIn(plain[100:140], data)

        facts = bundle_crypt.inspect(self.enc, key_store=False)
        self.assertEqual((facts["version"], facts["flags"], facts["map_key"], facts["chunk_size"], facts["chunk_count"], facts["plaintext_length"]),
                         (1, 0, "arena_test", MIB, 4, self.SIZE))
        self.assertEqual(facts["file_length"], len(data))
        self.assertEqual(facts["payload_offset"] + self.SIZE, len(data))
        self.assertIsNone(facts["mac_verified"])
        checked = bundle_crypt.inspect(self.enc, key_store=self.store, verify_chunks=True)
        self.assertEqual((checked["mac_verified"], checked["chunks"]), (True, "verified"))
        self.assertNotIn("k_map", json.dumps(checked))

        for offset, length in ((0, 8), (0, self.SIZE), (MIB - 10, 20), (3 * MIB - 1, 124), (5, 0), (self.SIZE - 100, 100), (2 * MIB, MIB)):
            with self.subTest(offset=offset, length=length):
                self.assertEqual(bundle_crypt.read_range(self.enc, offset, length, self.store), plain[offset:offset + length])
        for offset, length in ((-100, 100), (-1, 1), (-self.SIZE, 50), (-(MIB + 7), MIB + 7)):
            with self.subTest(negative=offset):
                self.assertEqual(bundle_crypt.read_range(self.enc, offset, length, self.store), plain[offset:][:length])
        self.assertEqual(bundle_crypt.read_range(self.enc, self.SIZE - 5, 50, self.store, clip=True), plain[-5:])
        self.assertEqual(bundle_crypt.read_range(self.enc, self.SIZE, 10, self.store, clip=True), b"")
        self.expect_fail("read-range", "--in", self.enc, "--offset", self.SIZE - 5, "--length", 50, "--key-store", self.store,
                  fragment="reaches past the end")
        self.expect_fail("read-range", "--in", self.enc, "--offset", self.SIZE + 1, "--length", 1, "--key-store", self.store, "--clip",
                  fragment="outside the plaintext")
        self.expect_fail("read-range", "--in", self.enc, "--offset", -(self.SIZE + 1), "--length", 1, "--key-store", self.store, "--clip",
                  fragment="outside the plaintext")

        out = self.tmp / "dec.bundle"
        self.tool("decrypt", "--in", self.enc, "--out", out, "--key-store", self.store)
        self.assertEqual(out.read_bytes(), plain)
        digest, length = bundle_crypt.plain_sha256(self.enc, self.store)
        self.assertEqual((digest, length), (hashlib.sha256(plain).hexdigest(), self.SIZE))
        range_file = self.tmp / "range.bin"
        self.tool("read-range", "--in", self.enc, "--offset", 1000, "--length", 5000, "--out", range_file, "--key-store", self.store)
        self.assertEqual(range_file.read_bytes(), plain[1000:6000])

    def test_small_chunks_and_odd_sizes(self):
        for size in (1 + 8, 16 + 8, 17 + 8, 4096, 4097, 8192, 4096 * 3 + 5):
            with self.subTest(size=size):
                source = self.plain(f"p{size}.bundle", size, seed=size)
                enc = self.tmp / f"e{size}.bundle"
                self.encrypt(source, enc, "arena_test", "--chunk-size", 4096)
                self.assertEqual(bundle_crypt.inspect(enc, key_store=self.store, verify_chunks=True)["chunk_count"], -(-size // 4096))
                self.assertEqual(bundle_crypt.read_range(enc, 0, size, self.store), source.read_bytes())
                self.assertEqual(bundle_crypt.read_range(enc, -3, 3, self.store), source.read_bytes()[-3:])

    def test_an_unchanged_rebuild_leaves_the_output_alone_and_a_changed_input_does_not(self):
        self.encrypt(self.source, self.enc)
        first, stamp = self.enc.read_bytes(), self.enc.stat().st_mtime_ns
        proc = self.encrypt(self.source, self.enc)
        self.assertIn("unchanged", proc.stdout.decode())
        self.assertEqual((self.enc.read_bytes(), self.enc.stat().st_mtime_ns), (first, stamp))
        self.assertFalse(Path(str(self.enc) + ".tmp").exists())
        self.flip(self.source, 70_000)
        proc = self.encrypt(self.source, self.enc)
        self.assertIn("written", proc.stdout.decode())
        second = self.enc.read_bytes()
        self.assertNotEqual(first, second)
        self.assertEqual(len(first), len(second))
        # the nonce follows the plaintext: another plaintext, another nonce
        self.assertNotEqual(first[32:40], second[32:40])
        self.assertEqual(bundle_crypt.read_range(self.enc, 70_000, 1, self.store), self.source.read_bytes()[70_000:70_001])

    def test_a_plaintext_file_of_the_same_name_is_replaced(self):
        self.enc.write_bytes(b"UnityFS\0 the old copy step left a plaintext file here")
        proc = self.encrypt(self.source, self.enc)
        self.assertIn("written", proc.stdout.decode())
        self.assertEqual(self.enc.read_bytes()[:4], b"AMPB")

    def test_both_crypto_providers_write_identical_bytes(self):
        managed, cng = self.tmp / "managed.bundle", self.tmp / "cng.bundle"
        self.encrypt(self.source, managed, "arena_test", "--provider", "managed")
        proc = bundle_crypt.run("encrypt", "--map-key", "arena_test", "--in", self.source, "--out", cng, "--key-store", self.store,
                                "--provider", "cng", check=False)
        if proc.returncode != 0:
            self.skipTest("Windows CNG is unavailable here: " + proc.stderr.decode(errors="replace").strip())
        self.assertEqual(managed.read_bytes(), cng.read_bytes())
        self.assertIn("crypto: managed", self.tool("verify", "--map-key", "arena_test", "--plain", self.source, "--enc", cng,
                                                  "--key-store", self.store, "--provider", "managed").stdout.decode())

    def test_the_content_key_is_created_only_on_request_and_never_printed(self):
        text = self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.source, "--out", self.enc, "--key-store", self.store,
                         fragment="no content key for 'arena_test'")
        self.assertIn("--create-key", text)
        self.assertFalse(self.store.exists())
        proc = self.encrypt(self.source, self.enc)
        entry = json.loads((self.store / "arena_test.key").read_text(encoding="utf-8"))
        self.assertEqual((entry["schema"], entry["map_key"], entry["wraps"]), (1, "arena_test", []))
        self.assertRegex(entry["k_map"], r"^[0-9a-f]{64}$")
        shown = proc.stdout.decode() + proc.stderr.decode() + json.dumps(bundle_crypt.inspect(self.enc, key_store=self.store))
        self.assertNotIn(entry["k_map"], shown)
        self.assertNotIn(entry["k_map"], self.enc.read_bytes().hex())
        # a second run does not make a new key
        self.encrypt(self.source, self.enc)
        self.assertEqual(json.loads((self.store / "arena_test.key").read_text(encoding="utf-8"))["k_map"], entry["k_map"])
        # a store file of another map under this name is refused
        other = json.loads((self.store / "arena_test.key").read_text(encoding="utf-8"))
        other["map_key"] = "arena_other"
        (self.store / "arena_test.key").write_text(json.dumps(other), encoding="utf-8")
        self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.source, "--out", self.enc, "--key-store", self.store,
                  fragment="belongs to 'arena_other'")

    def test_input_that_is_already_encrypted_or_missing_is_refused(self):
        self.encrypt(self.source, self.enc)
        self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.enc, "--out", self.tmp / "again.bundle", "--key-store", self.store,
                  fragment="already an AMPB container")
        self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.tmp / "missing.bundle", "--out", self.tmp / "o.bundle",
                  "--key-store", self.store, fragment="file not found")
        self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.source, "--out", self.source, "--key-store", self.store, code=2)
        empty = self.tmp / "empty.bundle"
        empty.write_bytes(b"")
        self.expect_fail("encrypt", "--map-key", "arena_test", "--in", empty, "--out", self.tmp / "e.bundle", "--key-store", self.store,
                         fragment="is empty: there is nothing to encrypt")
        for size in ("0", "4095", "4100", str(32 * MIB)):
            self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.source, "--out", self.tmp / "c.bundle", "--key-store", self.store,
                             "--chunk-size", size, code=2, fragment="--chunk-size must be a multiple of 16")

    def test_a_missing_input_creates_no_content_key(self):
        self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.tmp / "typo.bundle", "--out", self.enc, "--key-store", self.store,
                         "--create-key", fragment="file not found")
        self.assertFalse(self.store.exists())

    def test_damage_is_reported_by_kind_and_a_corrupt_chunk_fails_only_the_reads_that_touch_it(self):
        self.encrypt(self.source, self.enc)
        payload = bundle_crypt.inspect(self.enc, key_store=False)["payload_offset"]
        damaged = self.tmp / "damaged.bundle"
        shutil.copy(self.enc, damaged)
        self.flip(damaged, payload + MIB + 5)  # inside chunk 1
        text = self.expect_fail("inspect", "--in", damaged, "--key-store", self.store, "--verify-chunks", fragment="ChunkIntegrity")
        self.assertIn("chunk 1 (offset 1048576)", text)
        self.assertEqual(bundle_crypt.read_range(damaged, 0, 100, self.store), self.source.read_bytes()[:100])
        self.assertEqual(bundle_crypt.read_range(damaged, 2 * MIB, 100, self.store), self.source.read_bytes()[2 * MIB:2 * MIB + 100])
        self.expect_fail("read-range", "--in", damaged, "--offset", MIB + 1, "--length", 10, "--key-store", self.store, fragment="chunk 1")
        self.expect_fail("decrypt", "--in", damaged, "--out", self.tmp / "d.bundle", "--key-store", self.store, fragment="chunk 1")
        self.assertFalse((self.tmp / "d.bundle").exists())
        self.assertFalse((self.tmp / "d.bundle.tmp").exists())
        # without a key the table cannot be trusted, but a damaged chunk is still found against it
        self.expect_fail("inspect", "--in", damaged, "--verify-chunks", fragment="ChunkIntegrity")

    def test_every_kind_of_header_damage_is_a_clean_failure(self):
        self.encrypt(self.plain("small.bundle", 3000), self.enc, "arena_test", "--chunk-size", 4096)
        header_end = bundle_crypt.inspect(self.enc, key_store=False)["payload_offset"]
        for offset in (0, 3, 4, 6, 8, 12, 16, 24, 32, 40, 42, 45, 52, header_end - 33, header_end - 32, header_end - 1):
            with self.subTest(offset=offset):
                bad = self.tmp / f"bad{offset}.bundle"
                shutil.copy(self.enc, bad)
                self.flip(bad, offset)
                self.expect_fail("inspect", "--in", bad, "--key-store", self.store)
                self.expect_fail("read-range", "--in", bad, "--offset", 0, "--length", 8, "--key-store", self.store)
        truncated = self.tmp / "truncated.bundle"
        data = self.enc.read_bytes()
        for cut in (0, 3, 20, header_end - 1, len(data) - 1):
            truncated.write_bytes(data[:cut])
            self.expect_fail("inspect", "--in", truncated, "--key-store", self.store)
        truncated.write_bytes(data + b"x")
        self.expect_fail("inspect", "--in", truncated, "--key-store", self.store, fragment="unexpected bytes after the payload")
        self.expect_fail("inspect", "--in", self.source, fragment="not an AMPB container")
        self.expect_fail("inspect", "--in", self.tmp / "nothing", fragment="file not found")

    def test_a_key_of_another_map_does_not_open_the_container(self):
        self.encrypt(self.source, self.enc)
        other = self.tmp / "other.bundle"
        self.encrypt(self.source, other, "arena_other")
        # the container names its map; the store entry of that map is the only key tried
        self.assertEqual(bundle_crypt.inspect(other, key_store=self.store)["map_key"], "arena_other")
        entry = self.store / "arena_other.key"
        swapped = self.tmp / "store2"
        swapped.mkdir()
        shutil.copy(self.store / "arena_test.key", swapped / "arena_other.key")
        text = (swapped / "arena_other.key")
        stored = json.loads(text.read_text(encoding="utf-8"))
        stored["map_key"] = "arena_other"
        text.write_text(json.dumps(stored), encoding="utf-8")
        self.expect_fail("inspect", "--in", other, "--key-store", swapped, fragment="HeaderAuth")
        self.assertTrue(entry.exists())

    def test_verify_proves_that_the_output_is_exactly_the_encryption_of_the_input(self):
        self.encrypt(self.source, self.enc)
        proc = self.tool("verify", "--map-key", "arena_test", "--plain", self.source, "--enc", self.enc, "--key-store", self.store)
        self.assertIn("is exactly the encryption of plain.bundle", proc.stdout.decode())
        self.assertIn("OK verify", bundle_crypt.verify("arena_test", self.source, self.enc, self.store))
        changed = self.tmp / "changed.bundle"
        shutil.copy(self.source, changed)
        self.flip(changed, MIB + 9)
        self.expect_fail("verify", "--map-key", "arena_test", "--plain", changed, "--enc", self.enc, "--key-store", self.store,
                  fragment="is not the encryption of changed.bundle")
        self.expect_fail("verify", "--map-key", "arena_test", "--plain", self.source, "--enc", self.tmp / "missing.bundle", "--key-store", self.store,
                  fragment="file not found")
        longer = self.tmp / "longer.bundle"
        longer.write_bytes(self.enc.read_bytes() + b"\0")
        self.expect_fail("verify", "--map-key", "arena_test", "--plain", self.source, "--enc", longer, "--key-store", self.store)
        self.assertEqual(bundle_crypt.read_range(self.enc, 0, 8, self.store), b"UnityFS\0")

    def test_rotating_the_key_needs_yes_and_invalidates_the_old_bundles(self):
        self.encrypt(self.source, self.enc)
        old = json.loads((self.store / "arena_test.key").read_text(encoding="utf-8"))["k_map"]
        self.expect_fail("rotate-key", "--map-key", "arena_test", "--key-store", self.store, code=2)
        self.assertEqual(json.loads((self.store / "arena_test.key").read_text(encoding="utf-8"))["k_map"], old)
        proc = self.tool("rotate-key", "--map-key", "arena_test", "--key-store", self.store, "--yes")
        self.assertIn("previous store file is kept as", proc.stdout.decode())
        self.assertNotEqual(json.loads((self.store / "arena_test.key").read_text(encoding="utf-8"))["k_map"], old)
        self.assertEqual(len(list(self.store.glob("arena_test.key.rotated-*"))), 1)
        self.expect_fail("inspect", "--in", self.enc, "--key-store", self.store, fragment="HeaderAuth")
        self.encrypt(self.source, self.enc)
        self.assertEqual(bundle_crypt.read_range(self.enc, 0, 8, self.store), b"UnityFS\0")

    def test_usage_errors_exit_2(self):
        self.assertEqual(bundle_crypt.run(check=False).returncode, 2)
        self.assertEqual(bundle_crypt.run("help", check=False).returncode, 0)
        self.assertIn(b"stage", bundle_crypt.run("help").stdout)
        self.expect_fail("nonsense", code=2)
        self.expect_fail("encrypt", "--map-key", "arena_test", "--bogus", "x", code=2, fragment="unknown option --bogus")
        self.expect_fail("encrypt", "--map-key", "Arena Test", "--in", self.source, "--out", self.enc, "--key-store", self.store, code=2)
        self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.source, code=2, fragment="--out is required")
        self.expect_fail("inspect", "--in", self.enc, "--create-key", code=2, fragment="does not apply to this command")
        self.expect_fail("read-range", "--in", self.enc, "--offset", "abc", "--length", 1, "--key-store", self.store, code=2)
        self.encrypt(self.source, self.enc)
        self.expect_fail("read-range", "--in", self.enc, "--offset", 0, "--length", 1, code=2, fragment="a key source is required")
        self.expect_fail("inspect", "--in", self.enc, "--key-store", self.store, "--keys-file", self.tmp / "k.json", code=2)
        self.expect_fail("encrypt", "--map-key", "arena_test", "--in", self.source, "--out", self.enc, "--key-store", self.store, "--provider", "rot13", code=2)

    def test_the_container_may_be_the_positional_argument_and_stdout_carries_only_the_data(self):
        self.encrypt(self.source, self.enc)
        self.assertIn("OK inspect enc.bundle", self.tool("inspect", self.enc).stdout.decode())
        proc = self.tool("read-range", self.enc, "--offset", 0, "--length", 8, "--key-store", self.store)
        self.assertEqual(proc.stdout, b"UnityFS\0")  # nothing but the bytes: the OK line goes to stderr
        self.assertIn("OK read-range", proc.stderr.decode())
        self.assertEqual(self.tool("read-range", self.enc, "--offset", 0, "--length", 0, "--key-store", self.store).stdout, b"")
        self.expect_fail("inspect", self.enc, "--in", self.enc, code=2, fragment="not both")
        self.expect_fail("inspect", self.enc, self.enc, code=2, fragment="unexpected argument")
        self.expect_fail("encrypt", "stray", "--map-key", "arena_test", code=2, fragment="unexpected argument 'stray'")

    def test_the_benchmark_prints_a_rate_per_provider(self):
        text = self.tool("bench", "--size-mib", "4").stdout.decode()
        self.assertRegex(text, r"BENCH managed \(4 MiB, best of 3, MiB/s\): aes-ctr \d+, sha256 \d+, hmac-sha256 \d+")
        self.assertIn("OK bench", text)


class FakeArenaCase(ToolCase):
    """A synthetic Arena folder (levels 2, 3 and 10: numeric order is not text order) and a key sources table for two maps."""

    MAP = "arena_test"
    LEVELS = (2, 3, 10)

    def setUp(self):
        super().setUp()
        self.arena = self.tmp / "arena"
        for index in self.LEVELS:
            self.write_level(index, random_bytes(100 + index, 20_000 + 7_000 * index))
        self.levels = self.tmp / "levels.json"
        self.levels.write_text(json.dumps({"schema": 1, "maps": {
            self.MAP: [{"index": 10, "scene": "C"}, {"index": 2, "scene": "A"}, {"index": 3, "scene": "B"}],
            "arena_airpit": [{"index": 2, "scene": "A"}]}}), encoding="utf-8")
        self.plugin = self.tmp / "plugin"
        self.bundles = {kind: self.plain(f"{self.MAP}_{kind}.bundle", size, seed=n, folder=self.tmp / "build")
                        for n, (kind, size) in enumerate((("scenes", 2 * MIB + 77), ("navmesh", 40_000), ("preset", 6_000)), 5)}

    def level_path(self, index):
        return self.arena / "EscapeFromTarkovArena_Data" / f"level{index}"

    def write_level(self, index, data):
        path = self.level_path(index)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def key_file(self, key=None):
        return self.plugin / "keys" / f"{key or self.MAP}.json"

    def stage(self, *extra, built_for="Arena-1", key=None, create=True, bundles=None, out_dir=None):
        key = key or self.MAP
        args = ["stage", "--map-key", key]
        for bundle in bundles if bundles is not None else self.bundles.values():
            args += ["--bundle", bundle]
        args += ["--out-dir", out_dir or self.plugin, "--key-store", self.store, "--levels", self.levels,
                 "--arena-unverified", self.arena, "--built-for", built_for]
        if create:
            args.append("--create-key")
        return self.tool(*args, *extra)

    def verify_keys(self, built_for="Arena-1", expect=0, key=None):
        key = key or self.MAP
        return self.tool("verify-keys", "--map-key", key, "--keys-file", self.key_file(key), "--key-store", self.store,
                         "--arena-unverified", self.arena, "--built-for", built_for, expect=expect)

    def snapshot(self):
        names = sorted(p for p in self.plugin.rglob("*") if p.is_file())
        return {p.relative_to(self.plugin).as_posix(): (hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_mtime_ns) for p in names}


class KeysTests(FakeArenaCase):
    def test_the_key_file_names_files_and_sizes_but_carries_no_hash(self):
        out = self.tmp / "keys.json"
        proc = self.tool("keys", "--map-key", self.MAP, "--arena-unverified", self.arena, "--built-for", "Arena-1", "--key-store", self.store,
                         "--levels", self.levels, "--out", out, "--create-key")
        self.assertIn("OK keys arena_test: new wrap for Arena Arena-1, 1 wrap(s) in the file (written), 3 level files", proc.stdout.decode())
        text = out.read_text(encoding="utf-8")
        self.assertTrue(text.endswith("\n") and "\r" not in text and not text.startswith("\ufeff"))
        data = json.loads(text)
        self.assertEqual(list(data), ["schema", "map_key", "wraps"])
        self.assertEqual((data["schema"], data["map_key"], len(data["wraps"])), (1, self.MAP, 1))
        wrap = data["wraps"][0]
        self.assertEqual(list(wrap), ["built_for", "files", "salt", "wrapped_key", "check"])
        self.assertEqual(wrap["built_for"], "Arena-1")
        self.assertEqual([f["path"] for f in wrap["files"]], [f"EscapeFromTarkovArena_Data/level{n}" for n in (2, 3, 10)])
        self.assertEqual([f["size"] for f in wrap["files"]], [self.level_path(n).stat().st_size for n in (2, 3, 10)])
        for value in (wrap["salt"], wrap["wrapped_key"], wrap["check"]):
            self.assertRegex(value, r"^[0-9a-f]{64}$")
        # no level-file hash, and not the content key, anywhere in the shipped file
        key_store = json.loads((self.store / f"{self.MAP}.key").read_text(encoding="utf-8"))
        for index in self.LEVELS:
            self.assertNotIn(hashlib.sha256(self.level_path(index).read_bytes()).hexdigest(), text)
        self.assertNotIn(key_store["k_map"], text)
        self.assertNotIn(wrap["wrapped_key"], key_store["k_map"])
        # the store keeps the per-file hashes (never shipped): the first Arena update shows which files changed
        self.assertEqual([f["sha256"] for f in key_store["wraps"][0]["files"]],
                         [hashlib.sha256(self.level_path(n).read_bytes()).hexdigest() for n in (2, 3, 10)])
        self.assertEqual(distribution.check_key_file_schema(out, self.MAP), None)

    def test_the_players_derivation_unlocks_the_store_key_and_every_defect_is_named(self):
        self.stage()
        self.assertIn("unlocks the store's content key", self.verify_keys().stdout.decode())
        self.assertIn("OK verify-keys", bundle_crypt.verify_keys(self.MAP, self.key_file(), self.store, arena_unverified=self.arena,
                                                                  built_for="Arena-1"))
        original = json.loads(self.key_file().read_text(encoding="utf-8"))

        def write(change, name="edited.json"):
            data = json.loads(json.dumps(original))
            change(data)
            path = self.tmp / name
            path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="\n")
            return path

        def refuse(path, fragment, key=None):
            return self.expect_fail("verify-keys", "--map-key", key or self.MAP, "--keys-file", path, "--key-store", self.store,
                             "--arena-unverified", self.arena, "--built-for", "Arena-1", fragment=fragment)

        # a wrong size entry (the player has another build of that file)
        refuse(write(lambda d: d["wraps"][0]["files"][0].update(size=d["wraps"][0]["files"][0]["size"] + 1)),
               "SizeDiffers: 'EscapeFromTarkovArena_Data/level2' is")
        # right sizes, another content: one digit of the check value, or one flipped byte in a level file
        refuse(write(lambda d: d["wraps"][0].update(check=("0" if d["wraps"][0]["check"][0] != "0" else "1") + d["wraps"][0]["check"][1:])),
               "ContentDiffers")
        refuse(write(lambda d: d["wraps"][0].update(salt="0" * 64)), "ContentDiffers")
        self.flip(self.level_path(3), 100)
        refuse(self.key_file(), "ContentDiffers")
        self.flip(self.level_path(3), 100)
        self.verify_keys()
        # a missing level file, a level file that grew
        saved = self.level_path(10).read_bytes()
        self.level_path(10).unlink()
        refuse(self.key_file(), "ArenaFileUnreadable: the file does not exist")
        self.write_level(10, saved + b"x")
        refuse(self.key_file(), "SizeDiffers")
        self.write_level(10, saved)
        self.verify_keys()
        # a key file of another map, a key file naming a file outside the installation, malformed files
        other = self.tool("encrypt", "--map-key", "arena_airpit", "--in", self.bundles["preset"], "--out", self.tmp / "ap.bundle",
                          "--key-store", self.store, "--create-key")
        self.assertIn("OK encrypt", other.stdout.decode())
        refuse(self.key_file(), "the key file is for 'arena_test', not for 'arena_airpit'", key="arena_airpit")
        refuse(write(lambda d: d["wraps"][0]["files"][0].update(path="../outside")), "is not a plain relative path")
        refuse(write(lambda d: d["wraps"][0]["files"][0].update(path="C:/Windows/notepad.exe")), "is not a plain relative path")
        refuse(write(lambda d: d.update(schema=2)), "unsupported key file schema 2")
        refuse(write(lambda d: d.update(wraps=[])), "lists no wraps")
        refuse(write(lambda d: d["wraps"][0].update(salt="zz")), "must be 64 hex digits")
        bad = self.tmp / "bad.json"
        bad.write_text("{", encoding="utf-8")
        refuse(bad, "KeyFileInvalid")
        refuse(self.tmp / "missing.json", "KeyFileMissing")
        # the store's key is required: a key file made with another store does not verify
        elsewhere = self.tmp / "store2"
        shutil.copytree(self.store, elsewhere)
        entry = json.loads((elsewhere / f"{self.MAP}.key").read_text(encoding="utf-8"))
        entry["k_map"] = "ab" * 32
        (elsewhere / f"{self.MAP}.key").write_text(json.dumps(entry), encoding="utf-8")
        self.expect_fail("verify-keys", "--map-key", self.MAP, "--keys-file", self.key_file(), "--key-store", elsewhere,
                  "--arena-unverified", self.arena, "--built-for", "Arena-1", fragment="not the one in the key store")

    def test_levels_come_from_the_table_and_a_manifest_must_agree(self):
        out = self.tmp / "keys.json"
        base = ["keys", "--map-key", self.MAP, "--arena-unverified", self.arena, "--key-store", self.store, "--out", out, "--create-key"]
        manifest = self.tmp / "manifest.json"
        manifest.write_text(json.dumps({"source": {"scene_registry": [{"index": 3, "name": "B"}, {"index": 2, "name": "A"}, {"index": 10, "name": "C"}]}}),
                            encoding="utf-8")
        self.tool(*base, "--manifest", manifest)  # a manifest alone is enough when it carries a scene registry
        self.tool(*base, "--manifest", manifest, "--levels", self.levels)
        manifest.write_text(json.dumps({"source": {"scene_registry": [{"index": 2}, {"index": 3}]}}), encoding="utf-8")
        self.expect_fail(*base, "--manifest", manifest, "--levels", self.levels, fragment="list different level files")
        manifest.write_text(json.dumps({"source": {"scenes": []}}), encoding="utf-8")
        self.expect_fail(*base, "--manifest", manifest, fragment="no source.scene_registry")
        self.expect_fail(*base, code=2, fragment="--levels FILE")
        self.expect_fail(*base, "--levels", self.tmp / "nothing.json", fragment="file not found")
        table = json.loads(self.levels.read_text(encoding="utf-8"))
        table["maps"][self.MAP].append({"index": 3, "scene": "dup"})
        self.levels.write_text(json.dumps(table), encoding="utf-8")
        self.expect_fail(*base, "--levels", self.levels, fragment="level 3 is listed twice")
        table["maps"][self.MAP] = [{"index": 2}, {"index": 999}]
        self.levels.write_text(json.dumps(table), encoding="utf-8")
        self.expect_fail(*base, "--levels", self.levels, fragment="'EscapeFromTarkovArena_Data/level999' is missing")
        self.expect_fail("keys", "--map-key", "arena_nomap", "--arena-unverified", self.arena, "--key-store", self.store, "--out", out, "--create-key",
                  "--levels", self.levels, fragment="no entry for 'arena_nomap'")

    def test_the_installation_check_cannot_be_skipped_without_the_test_environment(self):
        out = self.tmp / "keys.json"
        args = ("keys", "--map-key", self.MAP, "--arena-unverified", self.arena, "--key-store", self.store, "--levels", self.levels,
                "--out", out, "--create-key")
        with patch.dict(os.environ):
            os.environ.pop("ARENAMAPPORT_TOOL_TEST")
            self.expect_fail(*args, code=2, fragment="needs ARENAMAPPORT_TOOL_TEST=1")
        os.environ["ARENAMAPPORT_TOOL_TEST"] = "0"
        self.expect_fail(*args, code=2, fragment="needs ARENAMAPPORT_TOOL_TEST=1")
        os.environ["ARENAMAPPORT_TOOL_TEST"] = "1"
        self.expect_fail("keys", "--map-key", self.MAP, "--arena-unverified", self.arena, "--arena", "auto", "--key-store", self.store,
                  "--levels", self.levels, "--out", out, code=2, fragment="exclude each other")
        self.expect_fail("keys", "--map-key", self.MAP, "--built-for", "x", "--key-store", self.store, "--levels", self.levels, "--out", out,
                  code=2, fragment="--built-for belongs to --arena-unverified")
        # an explicit folder is verified like the registry's: a synthetic one cannot pass the floors
        text = self.expect_fail("keys", "--map-key", self.MAP, "--arena", self.arena, "--key-store", self.store, "--levels", self.levels, "--out", out,
                         "--create-key", fragment="the Arena installation is not verified")
        self.assertNotIn("FAIL unexpected", text)
        self.assertFalse(out.exists())
        self.assertFalse(self.store.exists())  # no Arena, no key: a refused run leaves no store entry behind


class StageTests(FakeArenaCase):
    def test_stage_writes_three_encrypted_bundles_and_the_key_file_and_a_second_run_changes_nothing(self):
        proc = self.stage()
        text = proc.stdout.decode()
        self.assertIn("CREATED", text)
        self.assertIn("OK stage arena_test: 3 bundles encrypted (3 written), key file keys/arena_test.json: new wrap for Arena Arena-1", text)
        self.assertEqual(sorted(p.name for p in (self.plugin / "bundles").iterdir()),
                         [f"arena_test_{kind}.bundle" for kind in ("navmesh", "preset", "scenes")])
        self.assertEqual([p.name for p in (self.plugin / "keys").iterdir()], ["arena_test.json"])
        for kind, source in self.bundles.items():
            staged = self.plugin / "bundles" / source.name
            self.assertEqual(staged.read_bytes()[:4], b"AMPB")
            self.assertEqual(bundle_crypt.read_range(staged, 0, 8, self.store), b"UnityFS\0")
            self.assertEqual(bundle_crypt.plain_sha256(staged, self.store)[0], hashlib.sha256(source.read_bytes()).hexdigest())
            self.assertEqual(bundle_crypt.inspect(staged, key_store=self.store, verify_chunks=True)["map_key"], "arena_test")
            bundle_crypt.verify("arena_test", source, staged, self.store)
        self.verify_keys()
        # a player holding the same Arena files derives the key from the SHIPPED file alone and opens every bundle
        for source in self.bundles.values():
            staged = self.plugin / "bundles" / source.name
            self.tool("read-range", "--in", staged, "--offset", 0, "--length", 8, "--keys-file", self.key_file(), "--arena-unverified", self.arena,
                      "--built-for", "Arena-1", "--out", self.tmp / "head.bin")
            self.assertEqual((self.tmp / "head.bin").read_bytes(), b"UnityFS\0")
        store_before = (self.store / "arena_test.key").read_bytes()
        before = self.snapshot()
        text = self.stage().stdout.decode()
        self.assertIn("3 bundles encrypted (0 written)", text)
        self.assertIn("(unchanged)", text)
        self.assertNotIn("CREATED", text)
        self.assertEqual(self.snapshot(), before)  # identical bytes AND untouched files
        self.assertEqual((self.store / "arena_test.key").read_bytes(), store_before)
        self.assertEqual(len(before), 4)

    def test_staging_into_a_dist_that_holds_the_old_plaintext_copies_replaces_them(self):
        (self.plugin / "bundles").mkdir(parents=True)
        for source in self.bundles.values():
            shutil.copy(source, self.plugin / "bundles" / source.name)
        self.stage()
        self.assertTrue(all(p.read_bytes()[:4] == b"AMPB" for p in (self.plugin / "bundles").iterdir()))

    def test_a_second_arena_build_adds_a_wrap_and_leaves_the_bundles_unchanged(self):
        self.stage()
        before = self.snapshot()
        first = json.loads(self.key_file().read_text(encoding="utf-8"))
        original = {n: self.level_path(n).read_bytes() for n in self.LEVELS}
        # the Arena is updated: other content (and size) in one level file, another version
        self.write_level(3, original[3] + b"patched by the next Arena build")
        self.tool("verify-keys", "--map-key", self.MAP, "--keys-file", self.key_file(), "--key-store", self.store,
                  "--arena-unverified", self.arena, "--built-for", "Arena-2", expect=1)
        text = self.stage(built_for="Arena-2").stdout.decode()
        self.assertIn("new wrap for Arena Arena-2, 2 wrap(s) in the file (written)", text)
        after = self.snapshot()
        for name in before:
            if name.startswith("bundles/"):
                self.assertEqual(after[name], before[name], name)  # nothing re-encrypted, nothing touched
        self.assertNotEqual(after["keys/arena_test.json"], before["keys/arena_test.json"])
        data = json.loads(self.key_file().read_text(encoding="utf-8"))
        self.assertEqual([w["built_for"] for w in data["wraps"]], ["Arena-1", "Arena-2"])
        self.assertEqual(data["wraps"][0], first["wraps"][0])  # the first wrap is carried over untouched
        # the same shipped file opens against BOTH Arena builds
        self.assertIn("wrap 1 (built for Arena-2)", self.verify_keys("Arena-2").stdout.decode())
        for index, data_ in original.items():
            self.write_level(index, data_)
        self.assertIn("wrap 0 (built for Arena-1)", self.verify_keys("Arena-1").stdout.decode())
        # and restaging the old Arena again adds nothing
        self.assertIn("2 wrap(s) in the file (unchanged)", self.stage().stdout.decode())

    def test_a_hotfix_with_the_same_sizes_and_version_adds_a_wrap_too(self):
        self.stage()
        self.flip(self.level_path(2), 500)
        self.tool("verify-keys", "--map-key", self.MAP, "--keys-file", self.key_file(), "--key-store", self.store,
                  "--arena-unverified", self.arena, "--built-for", "Arena-1", expect=1)
        self.assertIn("2 wrap(s) in the file", self.stage().stdout.decode())
        self.assertIn("wrap 1", self.verify_keys().stdout.decode())
        self.flip(self.level_path(2), 500)
        self.assertIn("wrap 0", self.verify_keys().stdout.decode())

    def test_a_lost_key_store_is_never_recreated_silently(self):
        self.stage()
        shutil.rmtree(self.store)
        text = self.expect_fail("stage", "--map-key", self.MAP, *sum((["--bundle", b] for b in self.bundles.values()), []), "--out-dir", self.plugin,
                         "--key-store", self.store, "--levels", self.levels, "--arena-unverified", self.arena, "--built-for", "Arena-1",
                         fragment="no content key for 'arena_test'")
        self.assertIn("restored from its backup", text)

    def test_a_store_kept_across_builds_gives_the_same_bundles_for_the_same_inputs(self):
        self.stage()
        first = self.snapshot()
        shutil.rmtree(self.plugin)
        self.stage()
        again = self.snapshot()
        self.assertEqual({k: v[0] for k, v in again.items()}, {k: v[0] for k, v in first.items()})

    def test_stage_refuses_what_is_not_a_bundle_of_the_map(self):
        stray = self.plain("arena_other_scenes.bundle", 5000)
        self.expect_fail("stage", "--map-key", self.MAP, "--bundle", stray, "--out-dir", self.plugin, "--key-store", self.store, "--levels", self.levels,
                  "--arena-unverified", self.arena, "--create-key", fragment="is not a bundle of 'arena_test'")
        for name in ("arena_test_other.bundle", "arena_test_scenes.bundle.bak", "xarena_test_scenes.bundle", "arena_test_scenes.BUNDLE"):
            self.expect_fail("stage", "--map-key", self.MAP, "--bundle", self.plain(name, 5000, folder=self.tmp / "strays"), "--out-dir", self.plugin,
                             "--key-store", self.store,
                      "--levels", self.levels, "--arena-unverified", self.arena, "--create-key", fragment="is not a bundle of")
        self.expect_fail("stage", "--map-key", self.MAP, "--bundle", self.bundles["scenes"], "--bundle", self.bundles["scenes"], "--out-dir", self.plugin,
                  "--key-store", self.store, "--levels", self.levels, "--arena-unverified", self.arena, code=2, fragment="listed twice")
        self.expect_fail("stage", "--map-key", self.MAP, "--bundle", self.tmp / "missing" / "arena_test_scenes.bundle", "--out-dir", self.plugin,
                         "--key-store", self.store,
                  "--levels", self.levels, "--arena-unverified", self.arena, "--create-key", fragment="file not found")
        self.expect_fail("stage", "--map-key", self.MAP, "--out-dir", self.plugin, "--key-store", self.store, "--levels", self.levels,
                  "--arena-unverified", self.arena, code=2, fragment="--bundle")
        self.assertFalse(self.plugin.exists())
        self.assertFalse(self.store.exists())  # nothing was created by a refused stage

    def test_a_plaintext_input_that_is_an_ampb_container_is_refused(self):
        self.stage()
        staged = self.plugin / "bundles" / self.bundles["preset"].name
        self.expect_fail("stage", "--map-key", self.MAP, "--bundle", staged, "--out-dir", self.tmp / "again", "--key-store", self.store,
                  "--levels", self.levels, "--arena-unverified", self.arena, fragment="already an AMPB container")

    def test_two_maps_keep_separate_keys(self):
        self.stage()
        other = {kind: self.plain(f"arena_airpit_{kind}.bundle", 9_000 + n, seed=50 + n, folder=self.tmp / "build2")
                 for n, kind in enumerate(("scenes", "navmesh", "preset"))}
        self.stage(key="arena_airpit", bundles=other.values())
        keys = {k: json.loads((self.store / f"{k}.key").read_text(encoding="utf-8"))["k_map"] for k in (self.MAP, "arena_airpit")}
        self.assertNotEqual(*keys.values())
        self.assertEqual(sorted(p.name for p in (self.plugin / "keys").iterdir()), ["arena_airpit.json", "arena_test.json"])
        # a bundle of one map is not opened by the key of the other (the container names its own map)
        copied = self.tmp / "swap_store"
        copied.mkdir()
        shutil.copy(self.store / f"{self.MAP}.key", copied / "arena_airpit.key")
        swapped = json.loads((copied / "arena_airpit.key").read_text(encoding="utf-8"))
        swapped["map_key"] = "arena_airpit"
        (copied / "arena_airpit.key").write_text(json.dumps(swapped), encoding="utf-8")
        self.expect_fail("inspect", "--in", self.plugin / "bundles" / "arena_airpit_scenes.bundle", "--key-store", copied, fragment="HeaderAuth")
        self.verify_keys(key="arena_airpit")


class ValidatorsWithTheRealToolTests(FakeArenaCase):
    """The Python validators and the packer on real AMPB containers made by the tool (the stubbed unit tests are in
    test_validate_distribution.py and test_release_zips.py)."""

    def staged(self):
        self.stage()
        return self.plugin / "bundles"

    def test_validate_bundle_names_reads_an_encrypted_bundle_like_the_plain_one(self):
        names = ["CAB-0123456789abcdef", "BuildPlayer-Arena_Test.sharedAssets", "BuildPlayer-Arena_Test"]
        for at_end in (False, True):
            for fmt in (6, 7, 8):
                with self.subTest(at_end=at_end, fmt=fmt):
                    plain = self.tmp / f"{self.MAP}_scenes.bundle"
                    plain.write_bytes(synthetic_unityfs(names, at_end=at_end, fmt=fmt))
                    shutil.rmtree(self.plugin, ignore_errors=True)
                    self.stage(bundles=[plain])
                    staged = self.plugin / "bundles" / plain.name
                    self.assertEqual(bundle_names.read_directory_names(plain), names)
                    self.assertEqual(bundle_names.read_directory_names(staged, self.store), names)
                    self.assertEqual(bundle_names.read_directory_names_checked(staged, self.store), names)
        # a duplicate across containers is found through the tool, exactly as for plain files
        other = self.tmp / "arena_airpit_scenes.bundle"
        other.write_bytes(synthetic_unityfs(["CAB-0123456789abcdef", "unique"]))
        self.stage(key="arena_airpit", bundles=[other], built_for="Arena-1")
        failures = bundle_names.check_unique_names([self.plugin / "bundles" / "arena_test_scenes.bundle", self.plugin / "bundles" / "arena_airpit_scenes.bundle"],
                                                   self.store)
        self.assertEqual(len(failures), 1)
        self.assertIn("'CAB-0123456789abcdef'", failures[0])

    def test_the_reader_reads_only_the_header_and_the_blocks_info(self):
        plain = self.tmp / f"{self.MAP}_scenes.bundle"
        plain.write_bytes(synthetic_unityfs(["CAB-a", "CAB-b"], at_end=True, data_size=3 * MIB))
        self.stage(bundles=[plain])
        staged = self.plugin / "bundles" / plain.name
        reads = []
        real = bundle_crypt.read_range

        def spy(path, offset, length, **kwargs):
            reads.append((offset, length))
            return real(path, offset, length, **kwargs)

        with patch.object(bundle_names.bundle_crypt, "read_range", spy):
            self.assertEqual(bundle_names.read_directory_names(staged, self.store), ["CAB-a", "CAB-b"])
        self.assertEqual(len(reads), 2)
        self.assertEqual(reads[0], (0, 64))
        self.assertLess(reads[1][1], 200)  # the blocks-info blob: far smaller than the 3 MiB of data
        self.assertLess(reads[1][0], 0)    # counted from the end: BlocksInfoAtTheEnd

    @unittest.skipUnless((REPO / "work/build/preset/arena_bowl_preset.bundle").is_file(), "work/build/preset/arena_bowl_preset.bundle is not built here")
    def test_the_unitypy_fallback_decrypts_to_a_temporary_file_and_removes_it(self):
        real = REPO / "work/build/preset/arena_bowl_preset.bundle"
        plain = self.tmp / "arena_test_preset.bundle"
        shutil.copy(real, plain)
        self.stage(bundles=[plain])
        staged = self.plugin / "bundles" / plain.name
        expected = bundle_names.read_directory_names(real)
        scratch = self.tmp / "work-tmp"
        with patch.object(bundle_names, "TMP_DIR", scratch), \
                patch.object(bundle_names, "read_directory_names", side_effect=NotImplementedError("forced fast-parse failure")):
            self.assertEqual(bundle_names.read_directory_names_checked(staged, self.store), expected)
        self.assertEqual(list(scratch.iterdir()), [])  # the decrypted copy is gone

    def test_a_container_that_cannot_be_read_is_an_actionable_failure_not_a_fallback(self):
        self.stage()
        staged = self.plugin / "bundles" / "arena_test_navmesh.bundle"
        with self.assertRaises(SystemExit) as caught, patch.object(sys, "stderr") as stderr:
            bundle_names.read_directory_names_checked(staged, self.tmp / "no-store")
        self.assertEqual(caught.exception.code, 1)
        printed = "".join(call.args[0] for call in stderr.write.call_args_list)
        self.assertIn("cannot be read through the BundleCrypt tool", printed)
        self.assertIn("no content key for 'arena_test'", printed)

    def test_the_distribution_checks_accept_the_staged_files_and_name_every_defect(self):
        bundles = self.staged()
        keys = [self.MAP]
        distribution.validate_encrypted_bundles(bundles, keys, self.store)
        distribution.validate_key_files(self.plugin, keys, self.store, arena_unverified=self.arena)
        distribution.validate_no_plaintext_unityfs(self.plugin)
        distribution.validate_not_spike_build(bundles, self.store)

        # a bundle damaged in transit
        scenes = bundles / "arena_test_scenes.bundle"
        original = scenes.read_bytes()
        payload = bundle_crypt.inspect(scenes, key_store=False)["payload_offset"]
        self.flip(scenes, payload + MIB + 3)
        with self.assertRaisesRegex(ValueError, "Bundle arena_test_scenes.bundle failed inspection: FAIL ChunkIntegrity: chunk 1"):
            distribution.validate_encrypted_bundles(bundles, keys, self.store)
        scenes.write_bytes(original)
        # a plaintext bundle, a container of another map under this name, a key store without the key
        scenes.write_bytes(self.bundles["scenes"].read_bytes())
        with self.assertRaisesRegex(ValueError, "Plaintext bundle in dist .*: arena_test_scenes.bundle"):
            distribution.validate_encrypted_bundles(bundles, keys, self.store)
        self.assertRaisesRegex(ValueError, "Plaintext UnityFS file in dist: bundles/arena_test_scenes.bundle",
                               distribution.validate_no_plaintext_unityfs, self.plugin)
        scenes.write_bytes(original)
        airpit = self.tmp / "arena_airpit_scenes.bundle"
        airpit.write_bytes(self.bundles["scenes"].read_bytes())
        self.stage(key="arena_airpit", bundles=[airpit], out_dir=self.tmp / "plugin2")
        shutil.copy(self.tmp / "plugin2" / "bundles" / "arena_airpit_scenes.bundle", bundles / "arena_test_navmesh.bundle")
        with self.assertRaisesRegex(ValueError, "Bundle arena_test_navmesh.bundle belongs to map 'arena_airpit', not 'arena_test'"):
            distribution.validate_encrypted_bundles(bundles, keys, self.store)
        self.stage()  # restores the navmesh bundle
        distribution.validate_encrypted_bundles(bundles, keys, self.store)
        with self.assertRaisesRegex(ValueError, "failed inspection: FAIL there is no content key for 'arena_test'"):
            distribution.validate_encrypted_bundles(bundles, keys, self.tmp / "empty-store")

        # the key file: a wrong size entry and a flipped level byte do not unlock; schema defects name themselves
        key_path = self.key_file()
        good = key_path.read_bytes()
        data = json.loads(good)
        data["wraps"][0]["files"][1]["size"] += 1
        key_path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="\n")
        with self.assertRaisesRegex(ValueError, "Key file arena_test.json does not unlock: .*SizeDiffers: 'EscapeFromTarkovArena_Data/level3'"):
            distribution.validate_key_files(self.plugin, keys, self.store, arena_unverified=self.arena)
        key_path.write_bytes(good)
        self.flip(self.level_path(10), 9)
        with self.assertRaisesRegex(ValueError, "does not unlock: .*ContentDiffers"):
            distribution.validate_key_files(self.plugin, keys, self.store, arena_unverified=self.arena)
        self.flip(self.level_path(10), 9)
        distribution.validate_key_files(self.plugin, keys, self.store, arena_unverified=self.arena)
        (self.plugin / "keys" / "arena_other.json").write_bytes(good)
        with self.assertRaisesRegex(ValueError, "Key file set mismatch: missing=\\[\\] unexpected=\\['arena_other.json'\\]"):
            distribution.validate_key_files(self.plugin, keys, self.store, arena_unverified=self.arena)

    def test_the_spike_check_decrypts_only_when_the_plaintext_length_matches(self):
        bundles = self.staged()
        airpit = self.tmp / "arena_airpit_navmesh.bundle"
        airpit.write_bytes(b"UnityFS\0" + random_bytes(77, 5_000))
        self.stage(key="arena_airpit", bundles=[airpit])
        staged = bundles / airpit.name
        digest = hashlib.sha256(airpit.read_bytes()).hexdigest()
        stale = {airpit.name: (airpit.stat().st_size, digest)}
        with patch.object(distribution, "STALE_SPIKE_BUNDLES", stale):
            with self.assertRaisesRegex(ValueError, "Stale AirPit bundle: arena_airpit_navmesh.bundle"):
                distribution.validate_not_spike_build(bundles, self.store)
        with patch.object(distribution, "STALE_SPIKE_BUNDLES", {airpit.name: (airpit.stat().st_size, "0" * 64)}):
            distribution.validate_not_spike_build(bundles, self.store)  # same length, another digest: a rebuilt bundle
        with patch.object(distribution, "STALE_SPIKE_BUNDLES", {airpit.name: (airpit.stat().st_size + 1, digest)}):
            distribution.validate_not_spike_build(bundles, self.store)  # another length: not even hashed
        # the ENCRYPTED size is never compared: it differs from the recorded plaintext size by the container overhead
        self.assertNotEqual(staged.stat().st_size, airpit.stat().st_size)

    def test_the_packer_accepts_a_real_container_and_refuses_the_plaintext(self):
        bundles = self.staged()
        scenes = release.DistFile("BepInEx/plugins/maschine-ArenaMapPort/bundles/arena_test_scenes.bundle", bundles / "arena_test_scenes.bundle", 1)
        release.require_encrypted_bundle(scenes)
        plain = release.DistFile(scenes.rel, self.bundles["scenes"], 1)
        with self.assertRaisesRegex(release.PackagingError, "plaintext bundle in dist"):
            release.require_encrypted_bundle(plain)
        self.assertTrue(release.BUNDLE_ENTRY_RE.fullmatch("BepInEx/plugins/maschine-ArenaMapPort/bundles/arena_bowl_scenes.bundle"))
        self.assertIsNone(release.BUNDLE_ENTRY_RE.fullmatch("BepInEx/plugins/maschine-ArenaMapPort/bundles/arena_bowl_scenes.bundle.bak"))


class KeySourcesTests(unittest.TestCase):
    """tools/bundle/key-sources.json names the Arena level files each map's key depends on (docs/PLAN-ENCRYPTION.md 0.3). It is pinned
    against the tracked extractor tables and extraction plans always, against the extraction manifests when they are present, and against
    the installed Arena when there is one."""

    @classmethod
    def setUpClass(cls):
        cls.table = json.loads(KEY_SOURCES.read_text(encoding="utf-8"))

    @staticmethod
    def literal_assignment(path, name):
        tree = ast.parse(path.read_text(encoding="utf-8"))
        for node in tree.body:
            target = node.target if isinstance(node, ast.AnnAssign) else node.targets[0] if isinstance(node, ast.Assign) and len(node.targets) == 1 else None
            if isinstance(target, ast.Name) and target.id == name:
                return ast.literal_eval(node.value)
        raise AssertionError(f"{name} not found in {path}")

    def entries(self, key):
        return {e["index"]: e["scene"] for e in self.table["maps"][key]}

    def test_the_table_is_well_formed_and_lists_exactly_the_released_maps(self):
        from validate_map_descriptors import RELEASE_MAP_KEYS
        self.assertEqual(self.table["schema"], 1)
        self.assertEqual(sorted(self.table["maps"]), sorted(RELEASE_MAP_KEYS))
        text = KEY_SOURCES.read_text(encoding="utf-8")
        self.assertNotRegex(text, r"[0-9a-f]{40,}")  # indices and scene names only, never a hash
        for key, entries in self.table["maps"].items():
            with self.subTest(key=key):
                indices = [e["index"] for e in entries]
                self.assertEqual(indices, sorted(set(indices)), "ascending and unique")
                self.assertTrue(all(isinstance(i, int) and 0 < i < 10_000 for i in indices))
                self.assertTrue(all(set(e) == {"index", "scene"} and e["scene"].startswith("Arena_") for e in entries))

    def test_equator_and_bay5_match_their_extractor_tables(self):
        equator = self.literal_assignment(REPO / "tools/extract/extract_equator.py", "SCENE_REGISTRY")
        bay5 = self.literal_assignment(REPO / "tools/extract/extract_bay5.py", "BAY5_SCENE_NAMES")
        self.assertEqual(self.entries("arena_equator"), equator)
        self.assertEqual(self.entries("arena_bay5"), bay5)

    def test_airpit_and_bowl_match_their_extraction_plans(self):
        for key, plan_name in (("arena_airpit", "airpit"), ("arena_bowl", "bowl")):
            plan = json.loads((REPO / "tools/extract/plans" / f"{plan_name}.json").read_text(encoding="utf-8"))
            planned = {e["index"]: e["name"] for e in plan["scenes"] + plan["anchor_scenes"]}
            with self.subTest(key=key):
                self.assertEqual(self.entries(key), planned)

    def test_the_accepted_manifests_agree_when_they_are_present(self):
        packages = {"arena_equator": "equator-native-v5", "arena_bay5": "bay5-native-v5", "arena_airpit": "airpit-native-v9", "arena_bowl": "bowl-native-v2"}
        checked = 0
        for key, package in packages.items():
            path = REPO / "work/extract" / package / "manifest.json"
            if not path.is_file():
                continue
            manifest = json.loads(path.read_text(encoding="utf-8"))
            with self.subTest(key=key):
                registry = manifest["source"].get("scene_registry")
                if registry is not None:
                    self.assertEqual({e["index"]: e["name"] for e in registry}, self.entries(key))
                else:  # Equator and Bay5 name their scenes only
                    self.assertEqual(sorted(manifest["scenes"]), sorted(self.entries(key).values()))
                checked += 1
        if not checked:
            self.skipTest("no accepted extraction package under work/extract")

    @unittest.skipUnless((REAL_ARENA / "EscapeFromTarkovArena_Data").is_dir(), "no Arena installation here")
    def test_the_installed_arena_has_every_listed_level_file(self):
        for key, entries in self.table["maps"].items():
            for entry in entries:
                path = REAL_ARENA / "EscapeFromTarkovArena_Data" / f"level{entry['index']}"
                with self.subTest(key=key, level=entry["index"]):
                    self.assertTrue(path.is_file(), f"{path} is missing: the level indices may have shifted in this Arena build")
                    self.assertGreater(path.stat().st_size, 0)


class BuildWiringTests(unittest.TestCase):
    """Source-level guards on the project files that wire the tool in (the staging itself is exercised by the build)."""

    def test_the_tool_links_the_shared_crypto_sources_and_uses_no_package(self):
        text = (REPO / "tools/bundle/ArenaMapPort.BundleCrypt/ArenaMapPort.BundleCrypt.csproj").read_text(encoding="utf-8")
        self.assertIn(r'<Compile Include="..\..\..\ArenaMapPort.Crypto\*.cs"', text)
        self.assertIn(r'<Compile Include="..\..\..\ArenaMapPort.Shared\ArenaInstallation.cs"', text)
        self.assertIn("<TargetFramework>net10.0</TargetFramework>", text)
        self.assertIn("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", text)
        self.assertNotIn("PackageReference", text)
        client = (REPO / "ArenaMapPort.Client/ArenaMapPort.Client.csproj").read_text(encoding="utf-8")
        self.assertIn(r'<Compile Include="..\ArenaMapPort.Crypto\*.cs"', client)

    def test_the_staging_target_encrypts_every_released_map_and_dllonly_still_skips_it(self):
        from validate_map_descriptors import RELEASE_MAP_KEYS
        text = (REPO / "ArenaMapPort.Client/ArenaMapPort.Client.csproj").read_text(encoding="utf-8")
        target = re.search(r'<Target Name="ArenaMapPortStageWpOutputs"(.*?)</Target>', text, re.S)
        self.assertIsNotNone(target)
        header = text[target.start():text.index(">", target.start())]
        self.assertIn('DependsOnTargets="ArenaMapPortBuildBundleCrypt"', header)
        self.assertIn("'$(ArenaMapPortDllOnly)'!='true'", header)
        body = target.group(1)
        self.assertNotRegex(body, r'<Copy SourceFiles="@\(ArenaMapPortBundles\)"')  # the plaintext copy is gone
        for key in RELEASE_MAP_KEYS:
            with self.subTest(key=key):
                self.assertEqual(len(re.findall(rf'stage --map-key {key} @\(ArenaMapPortBundles->WithMetadataValue\(\'MapKey\',\'{key}\'\)', body)), 1)
                self.assertEqual(len(re.findall(rf'<MapKey>{key}</MapKey></ArenaMapPortBundles>', body)), 3)
        self.assertEqual(len(re.findall(r"<ArenaMapPortBundles Include=", body)), 3 * len(RELEASE_MAP_KEYS))
        self.assertEqual(len(re.findall(r'<Exec Command="[^"]*stage --map-key', body.replace("&quot;", ""))), len(RELEASE_MAP_KEYS))
        self.assertIn("--levels", body)
        self.assertIn("--key-store", body)
        build_target = re.search(r'<Target Name="ArenaMapPortBuildBundleCrypt">(.*?)</Target>', text, re.S)
        self.assertIsNotNone(build_target)
        self.assertIn("tools\\bundle\\ArenaMapPort.BundleCrypt", text)
        self.assertIn("dotnet build", build_target.group(1))

    def test_staging_creates_a_content_key_only_on_request(self):
        """Integration review (2026-10-19): a lost work/release-keys must never become a new content key by an ordinary build, because
        every released bundle of the map would stop unlocking. --create-key is passed only with -p:ArenaMapPortCreateReleaseKeys=true."""
        text = (REPO / "ArenaMapPort.Client/ArenaMapPort.Client.csproj").read_text(encoding="utf-8")
        arg = re.search(r'<ArenaMapPortCreateKeyArg Condition="([^"]*)">([^<]*)</ArenaMapPortCreateKeyArg>', text)
        self.assertIsNotNone(arg)
        self.assertEqual(arg.group(1), "'$(ArenaMapPortCreateReleaseKeys)'=='true'")
        self.assertEqual(arg.group(2), "--create-key")
        self.assertEqual(text.count("--create-key"), 1)  # the conditional property above is the only place it appears
        for exec_command in re.findall(r'<Exec Command="([^"]*stage --map-key[^"]*)"', text):
            self.assertNotIn("--create-key", exec_command)
            self.assertIn("$(ArenaMapPortStageCommon)", exec_command)  # which carries $(ArenaMapPortCreateKeyArg) and nothing else secret

    def test_the_key_store_and_the_tool_output_are_ignored_by_git(self):
        ignore = (REPO / ".gitignore").read_text(encoding="utf-8").splitlines()
        self.assertIn("work/", ignore)  # work/release-keys lives below it
        self.assertIn("**/bin/", ignore)
        self.assertIn("**/obj/", ignore)
        self.assertIn("dist/", ignore)


if __name__ == "__main__":
    unittest.main()
