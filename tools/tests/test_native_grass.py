"""The one list of maps with Arena grass shipped as an additional bundle (Bowl, Iceberg, Yard; user decision 2026-11-21) and every place that must agree with it.

validate_map_descriptors.NATIVE_GRASS_MAP_KEYS is the list; the sidecars' has_native_grass, the C# descriptor's HasNativeGrass (both held equal to it by
native_grass_errors), the distribution validator's GRASS_MAP_KEYS and its grass-pack header check, validate_port's staged-copy comparison, the packer's rules
and required files and the bundle validators that must skip the grass bundle are each pinned to it here, so adding a map to one of them without the others
fails a test instead of a release. Nothing here needs the key store or an Arena installation: the container tool is stubbed like in test_validate_distribution.py.
The grass pack itself (format version 1, tools/extract/grass_pack.py) is pinned by its own tests; here its header is built with that writer and read by the
validator's independent parser, so a drift between the two fails."""
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
for folder in ("validate", "release", "extract", "bundle"):
    sys.path.insert(0, str(REPO / "tools" / folder))

import bundle_crypt  # noqa: E402
import grass_pack  # noqa: E402
import make_release_zips as release  # noqa: E402
import validate_bundle_blocks as VBLOCKS  # noqa: E402
import validate_bundle_names as VNAMES  # noqa: E402
import validate_distribution as VDIST  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402
import validate_port as VPORT  # noqa: E402

DESCRIPTOR_CS = REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs"
GRASS_KEYS = ("arena_bowl", "arena_iceberg", "arena_yard")
PLUGIN = "BepInEx/plugins/maschine-ArenaMapPort"
# A small art shape for the fixtures (the real set is 1024 x 1024, 5 slices, 11 mips); the pack tool's own tests use the real arithmetic.
ART = (8, 8, 5, 4)


def sidecars():
    return {path.stem: json.loads(path.read_text(encoding="utf-8")) for path in VD.map_descriptor_paths(REPO / "tools" / "maps")}


def pack_for(key, records=3, art=ART, albedo_facts=None):
    """A valid grass pack of `key` with `records` placement records, built by the pack tool's own writer (small art). Returns the bytes."""
    width, height, slices, mips = art
    block = grass_pack.BLOCK_BYTES
    albedo = bytes(grass_pack.texture_bytes(width, height, slices, mips, block[grass_pack.FORMAT_DXT5]))
    normals = bytes(grass_pack.texture_bytes(width, height, slices, mips, block[grass_pack.FORMAT_DXT1]))
    single = bytes(grass_pack.texture_bytes(width, height, 1, mips, block[grass_pack.FORMAT_DXT1]))
    facts = {"albedo": grass_pack.TexFacts(width, height, slices, mips, 2, 1, 1),
             "normal_array": grass_pack.TexFacts(width, height, slices, mips, 2, 1, 1),
             "normal_texture": grass_pack.TexFacts(width, height, 1, mips, 2, 1, 16)}
    return grass_pack.build(key, bytes(records * grass_pack.RECORD_SIZE), albedo, normals, single, facts)


def pack_offset_after_normal_array(slices):
    """Where the normal texture starts when the normal array has `slices` slices (the fixture's shape): the header, 3 records, the albedo array, the normal array."""
    width, height, _slices, mips = ART
    return (VDIST.GRASS_HEADER_SIZE + 3 * VDIST.GRASS_RECORD_SIZE + VDIST.grass_texture_bytes(16, width, height, ART[2], mips)
            + VDIST.grass_texture_bytes(8, width, height, slices, mips))


def small_records(records=3):
    return patch.object(VDIST, "GRASS_PLACEMENT_RECORDS", {key: records for key in GRASS_KEYS})


class ListAgreementTests(unittest.TestCase):
    def test_the_list_is_bowl_iceberg_and_yard_in_registry_order(self):
        self.assertEqual(VD.NATIVE_GRASS_MAP_KEYS, GRASS_KEYS)
        self.assertEqual(list(VD.NATIVE_GRASS_MAP_KEYS), [key for key in VD.RELEASE_MAP_KEYS if key in GRASS_KEYS])
        self.assertTrue(set(VD.NATIVE_GRASS_MAP_KEYS) <= set(VD.RELEASE_MAP_KEYS))
        for key in ("arena_equator", "arena_bay5", "arena_airpit", "arena_autoservice"):
            self.assertNotIn(key, VD.NATIVE_GRASS_MAP_KEYS)

    def test_every_reader_of_the_list_agrees(self):
        self.assertEqual(VDIST.GRASS_MAP_KEYS, VD.NATIVE_GRASS_MAP_KEYS)
        self.assertIs(release.NATIVE_GRASS_MAP_KEYS, VD.NATIVE_GRASS_MAP_KEYS)
        self.assertIs(VPORT.NATIVE_GRASS_MAP_KEYS, VD.NATIVE_GRASS_MAP_KEYS)
        self.assertEqual(sorted(VDIST.GRASS_PLACEMENT_RECORDS), sorted(VD.NATIVE_GRASS_MAP_KEYS))

    def test_the_recorded_placement_sizes_are_the_clump_counts_times_68(self):
        # Arena's three .pcl files: Bowl 7,727,724 B, Iceberg 1,047,880 B, Yard 688,976 B (sizes only; docs/progress/native-grass.md).
        sizes = {"arena_bowl": 7727724, "arena_iceberg": 1047880, "arena_yard": 688976}
        for key, size in sizes.items():
            with self.subTest(map=key):
                self.assertEqual(VDIST.GRASS_PLACEMENT_RECORDS[key] * VDIST.GRASS_RECORD_SIZE, size)

    def test_the_sidecar_flag_is_true_for_exactly_the_listed_maps(self):
        documents = sidecars()
        for stem, document in documents.items():
            with self.subTest(sidecar=stem):
                self.assertEqual(document.get("has_native_grass", False), document["key"] in VD.NATIVE_GRASS_MAP_KEYS)
                if "has_native_grass" in document:
                    self.assertIs(type(document["has_native_grass"]), bool)

    def test_every_grass_map_lists_exactly_one_grass_scene(self):
        documents = {document["key"]: document for document in sidecars().values()}
        for key in VD.NATIVE_GRASS_MAP_KEYS:
            with self.subTest(map=key):
                scenes = [name for name in documents[key]["all_scene_names"] if name.endswith("_Grass")]
                self.assertEqual(len(scenes), 1, scenes)

    def test_the_validators_pack_constants_are_the_pack_tools(self):
        """The validator reads the format from the contract text, the writer is tools/extract/grass_pack.py: both must say the same."""
        self.assertEqual(VDIST.GRASS_PACK_MAGIC, grass_pack.MAGIC)
        self.assertEqual(VDIST.GRASS_PACK_VERSION, grass_pack.VERSION)
        self.assertEqual(VDIST.GRASS_HEADER_SIZE, grass_pack.HEADER_SIZE)
        self.assertEqual(VDIST.GRASS_ENTRY_SIZE, grass_pack.ENTRY_SIZE)
        self.assertEqual(VDIST.GRASS_ENTRY_COUNT, grass_pack.ENTRY_COUNT)
        self.assertEqual(VDIST.GRASS_RECORD_SIZE, grass_pack.RECORD_SIZE)
        self.assertEqual({kind: fmt for kind, (_name, fmt) in VDIST.GRASS_KINDS.items()}, grass_pack.KIND_FORMAT)
        self.assertEqual(VDIST.GRASS_BLOCK_BYTES, grass_pack.BLOCK_BYTES)
        for width, height, slices, mips, block in ((1024, 1024, 5, 11, 16), (1024, 1024, 5, 11, 8), (8, 8, 5, 4, 16), (3, 5, 2, 3, 8)):
            self.assertEqual(VDIST.grass_texture_bytes(block, width, height, slices, mips),
                             grass_pack.texture_bytes(width, height, slices, mips, block))
        self.assertEqual(VDIST.grass_texture_bytes(16, 1024, 1024, 5, 11), 6990640)    # Arena's GrassTexArray (docs/progress/native-grass.md)
        self.assertEqual(VDIST.grass_texture_bytes(8, 1024, 1024, 5, 11), 3495320)     # GrassNormalArray
        self.assertEqual(VDIST.grass_texture_bytes(8, 1024, 1024, 1, 11), 699064)      # Grass_new_1_N


REGISTRY = DESCRIPTOR_CS.read_text(encoding="utf-8")


class MirrorCheckTests(unittest.TestCase):
    """native_grass_errors: the sidecar flag, the descriptor's HasNativeGrass and NATIVE_GRASS_MAP_KEYS must say the same."""

    def test_the_real_sidecars_and_descriptors_agree(self):
        entries = VD.registry_entries(REGISTRY)
        documents = sidecars()
        for stem, name in (("bowl", "Bowl"), ("iceberg", "Iceberg"), ("yard", "Yard"), ("equator", "Equator"), ("bay5", "Bay5"),
                           ("airpit", "AirPit"), ("autoservice", "AutoService")):
            with self.subTest(map=name):
                self.assertEqual(VD.native_grass_errors(documents[stem], entries[name]), [])
        for name in ("Bowl", "Iceberg", "Yard"):
            self.assertIs(VD.cs_bool(entries[name], "HasNativeGrass"), True)
        for name in ("Equator", "Bay5", "AirPit", "AutoService"):
            self.assertIsNone(VD.cs_bool(entries[name], "HasNativeGrass"))

    def test_the_whole_validator_passes_on_the_real_repository(self):
        import subprocess
        result = subprocess.run([sys.executable, str(REPO / "tools" / "validate" / "validate_map_descriptors.py")], cwd=REPO,
                                capture_output=True, text=True, encoding="utf-8", errors="replace", env={**__import__("os").environ, "PYTHONUTF8": "1"})
        self.assertEqual(result.returncode, 0, result.stdout[-2000:] + result.stderr[-2000:])

    def test_each_wrong_combination_is_named(self):
        scenes = ["Arena_X_Scripts", "Arena_X_Grass"]
        listed = {"key": "arena_yard", "all_scene_names": scenes}
        unlisted = {"key": "arena_equator", "all_scene_names": ["Arena_Equator"]}
        yes, no = {"HasNativeGrass": ("localref", "true")}, {"HasNativeGrass": ("localref", "false")}
        self.assertEqual(VD.native_grass_errors({**listed, "has_native_grass": True}, yes), [])
        self.assertEqual(VD.native_grass_errors(unlisted, {}), [])
        self.assertEqual(VD.native_grass_errors({**unlisted, "has_native_grass": False}, no), [])
        self.assertTrue(any("sidecar False" in e for e in VD.native_grass_errors({**listed, "has_native_grass": False}, yes)))
        self.assertTrue(any("sidecar True" in e and "HasNativeGrass False" in e for e in VD.native_grass_errors({**listed, "has_native_grass": True}, no)))
        self.assertTrue(any("does not list" in e for e in VD.native_grass_errors({**unlisted, "has_native_grass": True, "all_scene_names": scenes}, yes)))
        self.assertTrue(any("lists" in e for e in VD.native_grass_errors(listed, {})))
        self.assertTrue(any("boolean" in e for e in VD.native_grass_errors({**listed, "has_native_grass": "yes"}, yes)))
        self.assertTrue(any("boolean" in e for e in VD.native_grass_errors({**listed, "has_native_grass": 1}, yes)))

    def test_a_grass_map_needs_exactly_one_grass_scene(self):
        yes = {"HasNativeGrass": ("localref", "true")}
        for scenes in ([], ["Arena_X_Scripts", "Arena_X"], ["A_Grass", "B_Grass"]):
            with self.subTest(scenes=scenes):
                errors = VD.native_grass_errors({"key": "arena_yard", "has_native_grass": True, "all_scene_names": scenes}, yes)
                self.assertTrue(any("exactly one scene named *_Grass" in e for e in errors), errors)

    def test_a_descriptor_without_the_flag_for_a_listed_map_is_a_mismatch(self):
        newline = "\r\n" if "\r\n" in REGISTRY else "\n"
        text = REGISTRY.replace(f"            HasNativeGrass: true,{newline}", "", 1)
        self.assertNotEqual(text, REGISTRY)
        entries = VD.registry_entries(text)
        documents = sidecars()
        broken = [name for name, stem in (("Bowl", "bowl"), ("Iceberg", "iceberg"), ("Yard", "yard")) if VD.native_grass_errors(documents[stem], entries[name])]
        self.assertEqual(len(broken), 1)

    def test_an_argument_that_is_no_boolean_literal_is_a_parse_failure(self):
        with self.assertRaises(SystemExit):
            VD.cs_bool({"HasNativeGrass": ("localref", "maybe")}, "HasNativeGrass")


def patch_header(pack, **changes):
    """The pack with header words changed: header fields by name, entry fields as (entry index, field)."""
    data = bytearray(pack)
    for name, value in changes.items():
        if name == "magic":
            data[0:4] = value
        elif name == "version":
            struct.pack_into("<I", data, 4, value)
        elif name == "header_size":
            struct.pack_into("<I", data, 8, value)
        elif name == "entry_count":
            struct.pack_into("<I", data, 12, value)
        elif name == "map_key":
            data[16:48] = value.ljust(32, b"\0")
        else:
            index, field = name.split(":")
            order = ("kind", "format", "width", "height", "slices", "mips", "filter", "wrap", "aniso", "reserved")
            base = 48 + int(index) * 56
            if field == "offset":
                struct.pack_into("<Q", data, base + 40, value)
            elif field == "size":
                struct.pack_into("<Q", data, base + 48, value)
            else:
                struct.pack_into("<I", data, base + 4 * order.index(field), value)
    return bytes(data)


class HeaderParserTests(unittest.TestCase):
    def parse(self, pack, key="arena_yard", length=None):
        with small_records():
            return VDIST.parse_grass_pack_header(pack[:VDIST.GRASS_HEADER_SIZE], key, len(pack) if length is None else length)

    def test_a_pack_of_the_pack_tools_writer_passes_and_matches_its_reader(self):
        for key in GRASS_KEYS:
            with self.subTest(map=key):
                pack = pack_for(key)
                entries = self.parse(pack, key)
                reader = grass_pack.parse(pack, key)
                self.assertEqual(sorted(entries), [1, 2, 3, 4])
                for kind, row in entries.items():
                    entry = reader.entry(kind)
                    self.assertEqual((row["format"], row["width"], row["height"], row["slices"], row["mips"], row["offset"], row["size"]),
                                     (entry.format, entry.width, entry.height, entry.slices, entry.mips, entry.offset, entry.size))
                self.assertEqual(entries[1]["height"], 3)
                self.assertEqual(entries[1]["offset"], VDIST.GRASS_HEADER_SIZE)

    def test_every_defect_of_the_header_is_refused(self):
        pack = pack_for("arena_yard")
        cases = {
            "the magic": ({"magic": b"UnFS"}, "magic"),
            "the version": ({"version": 2}, "version"),
            "the header size": ({"header_size": 288}, "header size"),
            "the entry count": ({"entry_count": 3}, "entry count"),
            "the map key": ({"map_key": b"arena_bowl"}, "is for map"),
            "a key with a byte after the zero": ({"map_key": b"arena_yard\0x"}, "is for map"),
            "the kind order": ({"0:kind": 2}, "kind"),
            "the placement format": ({"0:format": 1}, "format"),
            "the albedo format": ({"1:format": 1}, "format"),
            "the normal array format": ({"2:format": 5}, "format"),
            "the normal texture format": ({"3:format": 5}, "format"),
            "a reserved word": ({"1:reserved": 1}, "reserved"),
            "the record size": ({"0:width": 64}, "record size"),
            "no record": ({"0:height": 0}, "record size"),
            "a placement filter word": ({"0:filter": 1}, "filter, wrap and aniso"),
            "a placement mip count": ({"0:mips": 1}, "mips"),
            "the record count pin": ({"0:height": 4, "0:size": 4 * 68}, "records"),
            "the placement size": ({"0:size": 3 * 68 + 1}, "bytes"),
            "an albedo size": ({"1:size": 1}, "mip chain"),
            "an impossible mip count": ({"1:mips": 5}, "impossible shape"),
            "a zero width": ({"2:width": 0}, "impossible shape"),
            "two normal slices": ({"3:slices": 2}, "slices"),
            "a normal array of other depth": ({"2:slices": 4, "2:size": VDIST.grass_texture_bytes(8, 8, 8, 4, 4), "3:offset": pack_offset_after_normal_array(4)}, "normal array has 4 slices"),
            "a gap before the albedo": ({"1:offset": 272 + 3 * 68 + 4}, "starts at"),
        }
        for name, (changes, fragment) in cases.items():
            with self.subTest(defect=name):
                with self.assertRaises(ValueError) as caught:
                    self.parse(patch_header(pack, **changes))
                self.assertIn(fragment, str(caught.exception))

    def test_a_header_that_is_too_short_or_a_plaintext_length_that_differs_is_refused(self):
        pack = pack_for("arena_yard")
        with small_records(), self.assertRaises(ValueError):
            VDIST.parse_grass_pack_header(pack[:271], "arena_yard", len(pack))
        for length in (len(pack) - 1, len(pack) + 1, 0):
            with self.subTest(length=length), self.assertRaises(ValueError) as caught:
                self.parse(pack, length=length)
            self.assertIn("plaintext", str(caught.exception))

    def test_the_placement_record_count_is_pinned_per_map(self):
        pack = pack_for("arena_yard", records=3)
        with patch.object(VDIST, "GRASS_PLACEMENT_RECORDS", {"arena_yard": 10132}), self.assertRaises(ValueError) as caught:
            VDIST.parse_grass_pack_header(pack[:272], "arena_yard", len(pack))
        self.assertIn("10132", str(caught.exception))


class StubCrypt:
    """Stands in for tools/bundle/bundle_crypt.py: a container is b"AMPB" + anything, its plaintext comes from `plain`."""
    BundleCryptError = bundle_crypt.BundleCryptError
    is_encrypted = staticmethod(bundle_crypt.is_encrypted)

    def __init__(self):
        self.plain = {}        # file name -> plaintext
        self.map_key_of = {}   # file name -> claimed map key
        self.fail = {}         # (function, file name) -> reason
        self.verified = True
        self.calls = []

    def inspect(self, path, key_store=None, verify_chunks=False):
        name = Path(path).name
        self.calls.append(("inspect", name, verify_chunks))
        if (reason := self.fail.get(("inspect", name))):
            raise bundle_crypt.BundleCryptError(reason)
        return {"map_key": self.map_key_of.get(name, name.removesuffix("_grass.bundle")), "plaintext_length": len(self.plain[name]),
                "mac_verified": True if self.verified else None, "chunks": "verified" if self.verified else None}

    def read_range(self, path, offset, length, key_store=None, clip=False):
        name = Path(path).name
        self.calls.append(("read_range", name, offset, length))
        if (reason := self.fail.get(("read_range", name))):
            raise bundle_crypt.BundleCryptError(reason)
        return self.plain[name][offset:offset + length]


class GrassBundleValidatorTests(unittest.TestCase):
    KEYS = ("arena_equator", *GRASS_KEYS)

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.bundles = Path(temporary.name)
        self.crypt = StubCrypt()
        for key in GRASS_KEYS:
            self.put(key)
        patcher = patch.object(VDIST, "bundle_crypt", self.crypt)
        patcher.start()
        self.addCleanup(patcher.stop)
        records = small_records()
        records.start()
        self.addCleanup(records.stop)

    def put(self, key, pack=None):
        name = VDIST.grass_bundle_name(key)
        (self.bundles / name).write_bytes(b"AMPB" + key.encode())
        self.crypt.plain[name] = pack_for(key) if pack is None else pack

    def run_check(self):
        VDIST.validate_grass_bundles(self.bundles, self.KEYS, Path("keys"))

    def test_the_three_grass_bundles_pass_and_each_is_inspected_with_its_chunks_and_its_header_read(self):
        self.run_check()
        for key in GRASS_KEYS:
            name = f"{key}_grass.bundle"
            self.assertIn(("inspect", name, True), self.crypt.calls)
            self.assertIn(("read_range", name, 0, 272), self.crypt.calls)
        self.assertEqual([c for c in self.crypt.calls if "equator" in c[1]], [])

    def test_a_grass_bundle_of_a_map_without_arena_grass_is_refused(self):
        (self.bundles / "arena_equator_grass.bundle").write_bytes(b"AMPBx")
        with self.assertRaisesRegex(ValueError, "Grass bundle for a map without Arena grass: arena_equator_grass.bundle"):
            self.run_check()

    def test_a_missing_grass_bundle_is_refused(self):
        (self.bundles / "arena_bowl_grass.bundle").unlink()
        with self.assertRaisesRegex(ValueError, "Grass bundle missing: arena_bowl_grass.bundle"):
            self.run_check()

    def test_a_plaintext_pack_or_a_plaintext_unityfs_bundle_is_refused(self):
        for head, message in ((b"AMGP" + b"\x01\0\0\0", "Plaintext grass pack in dist"), (b"UnityFS\0", "Plaintext bundle in dist"), (b"", "Plaintext bundle in dist"),
                              (b"AMP", "Plaintext bundle in dist")):
            with self.subTest(head=head):
                (self.bundles / "arena_yard_grass.bundle").write_bytes(head)
                with self.assertRaisesRegex(ValueError, message + r".*arena_yard_grass\.bundle"):
                    self.run_check()

    def test_a_container_of_another_map_or_an_unverified_one_is_refused(self):
        self.crypt.map_key_of["arena_bowl_grass.bundle"] = "arena_yard"
        with self.assertRaisesRegex(ValueError, "belongs to map 'arena_yard', not 'arena_bowl'"):
            self.run_check()
        self.crypt.map_key_of.clear()
        self.crypt.verified = False
        with self.assertRaisesRegex(ValueError, "was not verified"):
            self.run_check()

    def test_a_tool_failure_is_a_distribution_failure_with_the_tools_reason(self):
        self.crypt.fail[("inspect", "arena_iceberg_grass.bundle")] = "FAIL ChunkIntegrity: chunk 1 failed its integrity check"
        with self.assertRaisesRegex(ValueError, "arena_iceberg_grass.bundle failed inspection: FAIL ChunkIntegrity"):
            self.run_check()
        self.crypt.fail.clear()
        self.crypt.fail[("read_range", "arena_iceberg_grass.bundle")] = "FAIL the offset is outside the plaintext"
        with self.assertRaisesRegex(ValueError, "arena_iceberg_grass.bundle cannot be read: FAIL the offset"):
            self.run_check()

    def test_a_plaintext_that_is_a_unity_bundle_or_an_invalid_pack_is_refused(self):
        self.crypt.plain["arena_yard_grass.bundle"] = b"UnityFS\0" + bytes(400)
        with self.assertRaisesRegex(ValueError, "does not decrypt to a grass pack"):
            self.run_check()
        good = pack_for("arena_yard")
        self.crypt.plain["arena_yard_grass.bundle"] = patch_header(good, **{"1:format": 1})
        with self.assertRaisesRegex(ValueError, "arena_yard_grass.bundle is not a valid grass pack: .*format"):
            self.run_check()
        self.crypt.plain["arena_yard_grass.bundle"] = good + b"x"
        with self.assertRaisesRegex(ValueError, "not a valid grass pack: .*plaintext"):
            self.run_check()
        self.crypt.plain["arena_yard_grass.bundle"] = pack_for("arena_bowl")
        with self.assertRaisesRegex(ValueError, "is for map"):
            self.run_check()

    def test_the_art_of_the_grass_maps_must_be_identical(self):
        self.put("arena_iceberg", pack_for("arena_iceberg", art=(16, 16, 5, 5)))
        with self.assertRaisesRegex(ValueError, "other art ranges than the other grass bundles"):
            self.run_check()

    def test_different_placement_counts_are_fine_when_each_matches_its_pin(self):
        counts = {"arena_bowl": 5, "arena_iceberg": 2, "arena_yard": 3}
        for key, count in counts.items():
            self.put(key, pack_for(key, records=count))
        with patch.object(VDIST, "GRASS_PLACEMENT_RECORDS", counts):
            self.run_check()


class PlaintextScanTests(unittest.TestCase):
    def test_a_plaintext_grass_pack_anywhere_in_dist_is_refused_but_a_container_or_a_short_file_is_not(self):
        with tempfile.TemporaryDirectory() as temporary:
            dist = Path(temporary)
            (dist / "BepInEx").mkdir()
            (dist / "BepInEx" / "ok.bundle").write_bytes(b"AMPB" + bytes(100))
            (dist / "short.bin").write_bytes(b"AMG")
            (dist / "note.txt").write_bytes(b"mentions AMGP but does not start with it")
            VDIST.validate_no_plaintext_unityfs(dist)
            for relative in ("stray.dat", "BepInEx/maschine-ArenaMapPort/bundles/arena_yard_grass.bundle", "work_copy.bak"):
                path = dist / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"AMGP" + b"\x01\0\0\0" + bytes(50))
                with self.assertRaisesRegex(ValueError, "Plaintext grass pack in dist: " + relative.replace(".", r"\.")):
                    VDIST.validate_no_plaintext_unityfs(dist)
                path.unlink()


class BundleToolsSkipTheGrassBundleTests(unittest.TestCase):
    def test_validate_bundle_names_skips_a_grass_bundle(self):
        self.assertTrue(VNAMES.is_grass_bundle(Path("arena_yard_grass.bundle")))
        self.assertTrue(VNAMES.is_grass_bundle(Path("dist/arena_bowl_grass.bundle")))
        for name in ("arena_yard_scenes.bundle", "arena_yard_navmesh.bundle", "arena_yard_preset.bundle"):
            self.assertFalse(VNAMES.is_grass_bundle(Path(name)))
        read = []
        with patch.object(VNAMES, "read_directory_names_checked", side_effect=lambda path, key_store: read.append(path.name) or [f"CAB-{path.name}"]):
            failures = VNAMES.check_unique_names([Path("arena_yard_scenes.bundle"), Path("arena_yard_grass.bundle"), Path("arena_bowl_grass.bundle")])
        self.assertEqual(failures, [])
        self.assertEqual(read, ["arena_yard_scenes.bundle"])

    def test_validate_bundle_blocks_skips_the_grass_bundles_of_a_directory_but_not_a_named_one(self):
        checked = []

        def fake_check(path, key_store=None, encrypted=None, crypt=None):
            checked.append(Path(path).name)
            return VBLOCKS.Report(Path(path).name, block_count=1)

        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            for name in ("arena_yard_scenes.bundle", "arena_yard_grass.bundle", "arena_bowl_grass.bundle", "arena_bowl_preset.bundle"):
                (folder / name).write_bytes(b"AMPB")
            with patch.object(VBLOCKS, "check_bundle", fake_check), patch("builtins.print"):
                self.assertEqual(VBLOCKS.main(["--bundles-dir", str(folder)]), 0)
                self.assertEqual(sorted(checked), ["arena_bowl_preset.bundle", "arena_yard_scenes.bundle"])
                checked.clear()
                VBLOCKS.main([str(folder / "arena_yard_grass.bundle")])
                self.assertEqual(checked, ["arena_yard_grass.bundle"])

    def test_the_block_table_check_in_the_distribution_validator_never_names_a_grass_bundle(self):
        self.assertEqual(VDIST.BUNDLE_KINDS, ("scenes", "navmesh", "preset"))


class PortValidatorTests(unittest.TestCase):
    def test_the_staged_grass_bundle_is_compared_with_the_plain_pack_of_work_build_grass(self):
        source = (REPO / "tools" / "validate" / "validate_port.py").read_text(encoding="utf-8")
        self.assertIn("work/build/grass/{key}_grass.bundle", source)
        self.assertIn("if key in NATIVE_GRASS_MAP_KEYS:", source)
        self.assertIn("bundle_crypt.verify(key, source, target", source)


class PackerTests(unittest.TestCase):
    def spec(self):
        """The release spec of the seven released maps over a throwaway repo whose two csproj files are minimal (the packer's rules do not read the real ones)."""
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        for project, assembly in (("ArenaMapPort.Client", "maschine-ArenaMapPort.Client"), ("ArenaMapPort.Server", "maschine-ArenaMapPort.Server")):
            (Path(temporary.name) / project).mkdir()
            (Path(temporary.name) / project / f"{project}.csproj").write_text(
                f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>{assembly}</AssemblyName><Version>9.9.9</Version></PropertyGroup></Project>', encoding="utf-8")
        return release.load_spec(Path(temporary.name), "9.9.9", maps=tuple(zip(VD.RELEASE_MAP_KEYS, VD.RELEASE_MAP_NAMES)))

    def test_a_grass_rule_exists_only_for_the_listed_maps(self):
        labels = {(rule.part, rule.label) for rule in release.build_rules(self.spec())}
        for key in VD.RELEASE_MAP_KEYS:
            stem = key.removeprefix("arena_")
            self.assertEqual((stem, "grass bundle") in labels, key in VD.NATIVE_GRASS_MAP_KEYS, key)

    def test_the_grass_rule_accepts_exactly_the_staged_name_in_the_maps_own_part(self):
        spec = self.spec()
        rules = release.build_rules(spec)
        for key in VD.NATIVE_GRASS_MAP_KEYS:
            stem = key.removeprefix("arena_")
            rule = release.classify(f"{PLUGIN}/bundles/{key}_grass.bundle", rules)
            self.assertEqual((rule.part, rule.label), (stem, "grass bundle"))
            for stray in (f"{key}_grass.bundle.bak", f"{key}_grass.bundle~", f"{key}_grass2.bundle", f"x{key}_grass.bundle", f"{key}_Grass.bundle", f"{key}_grass.bin"):
                with self.assertRaises(release.ClassificationError, msg=stray):
                    release.classify(f"{PLUGIN}/bundles/{stray}", rules)
        for key in ("arena_equator", "arena_bay5", "arena_airpit", "arena_autoservice", "arena_airpit2"):
            with self.assertRaises(release.ClassificationError, msg=key):
                release.classify(f"{PLUGIN}/bundles/{key}_grass.bundle", rules)

    def test_the_grass_bundle_is_a_required_file_of_exactly_the_grass_maps_parts(self):
        required = release.required_files(self.spec())
        for key in VD.RELEASE_MAP_KEYS:
            stem = key.removeprefix("arena_")
            self.assertEqual(f"{PLUGIN}/bundles/{key}_grass.bundle" in required[stem], key in VD.NATIVE_GRASS_MAP_KEYS, key)

    def test_a_plaintext_grass_pack_is_refused_by_the_packers_container_check(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "arena_yard_grass.bundle"
            path.write_bytes(b"AMGP" + bytes(100))
            with self.assertRaises(release.PackagingError):
                release.require_encrypted_bundle(release.DistFile(f"{PLUGIN}/bundles/arena_yard_grass.bundle", path, 104))
            path.write_bytes(b"AMPB" + bytes(100))
            release.require_encrypted_bundle(release.DistFile(f"{PLUGIN}/bundles/arena_yard_grass.bundle", path, 104))


class RealDistTests(unittest.TestCase):
    """Facts about the real dist/, which exist only after the lead's staging build (a Release client build without DllOnly): they skip before it."""

    def test_every_grass_map_has_its_staged_grass_bundle_and_no_other_map_has_one(self):
        bundles = REPO / "dist" / PLUGIN / "bundles"
        staged = {path.name for path in bundles.glob("*_grass.bundle")} if bundles.is_dir() else set()
        if not staged:
            self.skipTest("no grass bundle is staged yet: the staging build (Release client build without DllOnly, after the grass pack tool) comes first")
        self.assertEqual(staged, {f"{key}_grass.bundle" for key in VD.NATIVE_GRASS_MAP_KEYS})
        for name in staged:
            with (bundles / name).open("rb") as stream:
                self.assertEqual(stream.read(4), b"AMPB", name)


if __name__ == "__main__":
    unittest.main()
