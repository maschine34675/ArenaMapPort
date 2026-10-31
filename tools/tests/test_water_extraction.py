"""Synthetic serialization and native mip-chain checks, the per-map table and the stage-2 key-source guard; no BSG data fixtures.

The real-source classes at the end read the installed Arena (and the ignored export of Iceberg's water) and skip when either is absent. The work
folder is REPO/work unless ARENAMAPPORT_WORK_DIR names another (a worktree has no work/ of its own)."""
from pathlib import Path
import hashlib
import json
import math
import os
import struct
import sys
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
import extract_water as W
from common_ids import HardExtractError

WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
ARENA = Path(W.DEFAULT_SOURCE)


def container(targets=None, **changes):
    """A synthetic container; `targets` = [(renderer path id, filter path id), ...] replaces the one target of renderer_id/filter_id."""
    values = dict(mip_bias=-1.5, foam_size=.2, border_range=25, blend=.25,
                  texture_id=44, renderer_id=701, filter_id=702, weighted_mode=0,
                  second_key_time=1, pre=2, enabled=1)
    values.update(changes)
    if targets is None:
        targets = [(values["renderer_id"], values["filter_id"])]
    raw = bytearray(struct.pack("<iqB3xiqi", 0, 50, values["enabled"], 1, 60, 0))
    for file_id, path_id in ((3, values["texture_id"]), (4, 45), (4, 46)):
        raw += struct.pack("<iq", file_id, path_id)
    raw += struct.pack("<fiq", values["mip_bias"], 4, 47)
    for blend in (values["blend"], .25, .5, .75, .25):
        raw += struct.pack("<4f", .2, -3, 5, blend)
    raw += struct.pack("<6f", 1, 2, values["border_range"], 3, 4, 5)
    raw += struct.pack("<8f", .1, .2, .3, 1, .4, .5, .6, 1)
    raw += struct.pack("<5f", .5, 10, .25, values["foam_size"], 6)
    raw += struct.pack("<4f", 1, .8, .7, .3)
    raw += struct.pack("<4f", .4, 5, .05, .35)
    raw += struct.pack("<i", 2)
    for time, value in ((0, 0), (values["second_key_time"], 1)):
        raw += struct.pack("<4fi2f", time, value, 0, 0, values["weighted_mode"], 1/3, 1/3)
    raw += struct.pack("<3i", values["pre"], 2, 4)
    raw += struct.pack("<8f", .9, .8, .7, 1, .1, .1, .1, 1)
    raw += struct.pack("<i", len(targets))
    for renderer_id, filter_id in targets:
        raw += struct.pack("<iqiq", 0, renderer_id, 0, filter_id)
    return bytes(raw)


class WaterExtractionTests(unittest.TestCase):
    def test_full_settings_and_separate_target_refs_survive(self):
        result = W.decode_container(container())
        self.assertEqual(len(container()), 404)
        self.assertEqual(result["texture_pointers"]["_rippleTexture"], {"file_id": 3, "path_id": 44})
        self.assertEqual(result["settings"]["_normalsDetailsMipMapBias"], -1.5)
        self.assertEqual(result["settings"]["_normalsA"]["MovementDirection"], [-3, 5])
        self.assertAlmostEqual(result["settings"]["_depthColorDeep"][2], .3)
        self.assertAlmostEqual(result["settings"]["_foamColor"][3], .3)
        self.assertEqual(result["targets"], [{"renderer": {"file_id": 0, "path_id": 701},
                                            "filter": {"file_id": 0, "path_id": 702}}])
        self.assertEqual(result["settings"]["_reflectionWettingFunc"]["keys"][1]["time"], 1)

    def test_unused_null_target_and_disabled_component_are_preserved(self):
        result = W.decode_container(container(renderer_id=0, filter_id=0, enabled=0))
        self.assertFalse(result["enabled"])
        self.assertEqual(result["targets"][0]["renderer"]["path_id"], 0)

    def test_truncation_and_extra_unknown_fields_fail_closed(self):
        for raw in (container()[:15], container()[:-1], container() + b"\0\0\0\0"):
            with self.subTest(length=len(raw)), self.assertRaises(HardExtractError):
                W.decode_container(raw)

    def test_missing_texture_and_half_target_rejected(self):
        for changes in ({"texture_id": 0}, {"renderer_id": 0}, {"filter_id": 0}, {"texture_id": -1}):
            with self.subTest(changes=changes), self.assertRaises(HardExtractError):
                W.decode_container(container(**changes))

    def test_nonfinite_and_division_by_zero_settings_rejected(self):
        for changes in ({"mip_bias": math.nan}, {"foam_size": 0}, {"border_range": 0},
                        {"blend": .6}, {"foam_size": math.inf}):
            with self.subTest(changes=changes), self.assertRaises(HardExtractError):
                W.decode_container(container(**changes))

    def test_unknown_curve_layout_and_unordered_keys_rejected(self):
        for changes in ({"weighted_mode": 5}, {"second_key_time": 0}, {"pre": 99}):
            with self.subTest(changes=changes), self.assertRaises(HardExtractError):
                W.decode_container(container(**changes))

    def test_complete_compressed_mip_chains_include_small_blocks(self):
        # DXT1's final 2x2 and 1x1 mips still each occupy one 8-byte block.
        self.assertEqual(W.texture_byte_count(8, 8, 4, 10), 32 + 8 + 8 + 8)
        self.assertEqual(W.texture_byte_count(8, 8, 4, 12), 64 + 16 + 16 + 16)
        self.assertEqual(W.texture_byte_count(8, 8, 4, 5), (64 + 16 + 4 + 1) * 4)
        self.assertEqual(W.texture_byte_count(512, 512, 10, 12), 349552)

    def test_invalid_texture_metadata_rejected(self):
        for args in ((0, 512, 10, 12), (512, 512, 11, 12), (512, 512, 0, 12), (512, 512, 10, 99)):
            with self.subTest(args=args), self.assertRaises(HardExtractError):
                W.texture_byte_count(*args)


def entry(name, path_id, targets, owner="Sea", active=True, **changes):
    """A find_containers() entry built from a synthetic container: (reader stand-in, decoded settings, owner path, owner active)."""
    class Reader:
        pass
    reader = Reader()
    reader.path_id = path_id
    return (reader, W.decode_container(container(targets=targets, **changes)), owner, active)


SEA = dict(name="Sea", path_id=100, targets=[(701, 702)])
EMPTY = dict(name="WaterForSSrv3", path_id=101, targets=[(0, 0)], owner="WaterForSSrv3")


class WaterMapTableTests(unittest.TestCase):
    def test_the_rows_are_bay5_and_iceberg_with_their_reviewed_facts(self):
        self.assertEqual(sorted(W.MAPS), ["arena_bay5", "arena_iceberg"])
        bay5, iceberg = W.MAPS["arena_bay5"], W.MAPS["arena_iceberg"]
        self.assertEqual((bay5.stem, bay5.level_index, bay5.scene), ("bay5", 84, "Arena_Bay5_Main"))
        self.assertEqual((iceberg.stem, iceberg.level_index, iceberg.scene), ("iceberg", 256, "Arena_Sanatorium"))
        for row in W.MAPS.values():
            self.assertEqual((row.container_path, row.target_path), ("Sea", "Sea/Plane"))
            self.assertNotIn(row.level_index, row.census_levels)
            self.assertEqual(len(row.census_levels), len(set(row.census_levels)))
            self.assertEqual(row.default_output, REPO / "work" / "extract" / f"{row.stem}-water")

    def test_a_row_is_found_by_key_or_stem_and_an_unknown_name_is_refused(self):
        for name, key in (("arena_bay5", "arena_bay5"), ("bay5", "arena_bay5"), ("iceberg", "arena_iceberg"), ("arena_iceberg", "arena_iceberg")):
            self.assertEqual(W.resolve_map(name).map_key, key)
        for name in ("", "equator", "arena_airpit", "Bay5", "iceberg-water"):
            with self.subTest(name=name), self.assertRaises(HardExtractError):
                W.resolve_map(name)

    def test_the_rows_name_scenes_of_their_map_sidecar(self):
        for row in W.MAPS.values():
            sidecar = json.loads((REPO / "tools" / "maps" / f"{row.stem}.json").read_text(encoding="utf-8"))
            self.assertEqual(sidecar["key"], row.map_key)
            self.assertIn(row.scene, sidecar["all_scene_names"])

    def test_iceberg_census_levels_are_the_other_scenes_of_its_plan(self):
        from source_plan import load_plan, unique_records
        plan = load_plan(REPO / "tools" / "extract" / "plans" / "iceberg.json")
        indices = {r["index"] for r in unique_records(plan)} | {r["index"] for r in plan["excluded_scenes"]}
        row = W.MAPS["arena_iceberg"]
        self.assertIn(row.level_index, indices)
        self.assertEqual(set(row.census_levels), indices - {row.level_index})

    def test_bay5_census_levels_are_the_other_scenes_of_its_registry_plus_its_scripts_scene(self):
        import extract_bay5
        row = W.MAPS["arena_bay5"]
        self.assertEqual(extract_bay5.BAY5_SCENE_NAMES[row.level_index], row.scene)
        self.assertEqual(set(row.census_levels), (set(extract_bay5.BAY5_SCENE_NAMES) - {row.level_index}) | {85})


class SelectSeaTests(unittest.TestCase):
    """The 'additional water surfaces need investigation' rule: an empty second container is fine (both maps carry the unused
    'WaterForSSrv3'), a second real surface, an ambiguous Sea or a Sea that is off or without exactly one surface is not."""

    def select(self, *entries, key="arena_iceberg"):
        return W.select_sea([entry(**e) for e in entries], W.MAPS[key])

    def test_the_sea_next_to_an_empty_container_is_selected_on_both_maps(self):
        for key in W.MAPS:
            with self.subTest(map=key):
                selected = self.select(EMPTY, SEA, key=key)
                self.assertEqual((selected[0].path_id, selected[2]), (100, "Sea"))
                self.assertEqual(len(W.real_targets(selected[1])), 1)

    def test_a_second_container_without_any_target_is_accepted(self):
        self.assertEqual(self.select(dict(EMPTY, targets=[]), SEA)[2], "Sea")

    def test_an_empty_container_with_several_empty_slots_is_accepted(self):
        self.assertEqual(self.select(dict(EMPTY, targets=[(0, 0), (0, 0), (0, 0)]), SEA)[2], "Sea")

    def test_a_second_real_surface_is_refused(self):
        with self.assertRaisesRegex(HardExtractError, "additional water surfaces need investigation"):
            self.select(SEA, dict(EMPTY, targets=[(801, 802)]))
        with self.assertRaisesRegex(HardExtractError, "additional water surfaces need investigation"):
            self.select(dict(EMPTY, targets=[(0, 0), (801, 802)]), SEA)

    def test_a_third_container_with_a_real_surface_is_refused_even_next_to_an_empty_one(self):
        with self.assertRaisesRegex(HardExtractError, "additional water surfaces"):
            self.select(EMPTY, SEA, dict(name="Pool", path_id=102, targets=[(803, 804)], owner="Pool"))

    def test_no_sea_two_seas_and_a_sea_without_exactly_one_surface_are_refused(self):
        with self.assertRaisesRegex(HardExtractError, "exactly one Sea container"):
            self.select(EMPTY)
        with self.assertRaisesRegex(HardExtractError, "exactly one Sea container"):
            self.select(SEA, dict(SEA, path_id=103))
        with self.assertRaisesRegex(HardExtractError, "one Sea surface"):
            self.select(dict(SEA, targets=[(0, 0)]), EMPTY)
        with self.assertRaisesRegex(HardExtractError, "one Sea surface"):
            self.select(dict(SEA, targets=[(701, 702), (801, 802)]), EMPTY)

    def test_a_disabled_or_inactive_sea_is_refused(self):
        with self.assertRaisesRegex(HardExtractError, "disabled"):
            self.select(dict(SEA, enabled=0), EMPTY)
        with self.assertRaisesRegex(HardExtractError, "disabled"):
            self.select(dict(SEA, active=False), EMPTY)

    def test_the_selected_container_is_the_one_at_the_rows_path(self):
        row = W.WaterMap("arena_test", "test", 1, "Test", "Harbor", "Harbor/Plane")
        selected = W.select_sea([entry(**dict(SEA, owner="Harbor")), entry(**EMPTY)], row)
        self.assertEqual(selected[2], "Harbor")
        with self.assertRaisesRegex(HardExtractError, "exactly one Harbor container"):
            W.select_sea([entry(**SEA)], row)


class KeySourceGuardTests(unittest.TestCase):
    """Stage 2 (docs/PLAN-ENCRYPTION.md section 6): the shipped textures are plaintext, the content keys are derived from sampled blocks of
    the raw resource streams, so a texture may neither lie on a sampled block nor be a whole key-source file."""
    BLOCK, BLOCKS = 65536, 16

    def sources(self, *names):
        return W.KeySources(self.BLOCK, self.BLOCKS, {name: ("arena_test",) for name in names})

    def test_the_sample_is_the_whole_file_when_small_and_sixteen_spread_blocks_otherwise(self):
        self.assertEqual(W.sample_blocks(1000, self.BLOCK, self.BLOCKS), [(0, 1000)])
        self.assertEqual(W.sample_blocks(self.BLOCK * self.BLOCKS, self.BLOCK, self.BLOCKS), [(0, self.BLOCK * self.BLOCKS)])
        size = 207867856                                  # sharedassets51.assets.resS of Arena 0.6.1
        blocks = W.sample_blocks(size, self.BLOCK, self.BLOCKS)
        self.assertEqual(len(blocks), 16)
        self.assertEqual(blocks[0], (0, self.BLOCK))
        self.assertEqual(blocks[-1], (size - self.BLOCK, self.BLOCK))
        self.assertEqual([offset for offset, _ in blocks], [k * (size - self.BLOCK) // 15 for k in range(16)])
        self.assertTrue(all(b[0] > a[0] for a, b in zip(blocks, blocks[1:])))

    def test_a_range_is_checked_against_every_block_it_touches(self):
        size = 2 * 1024 * 1024
        blocks = W.sample_blocks(size, self.BLOCK, self.BLOCKS)
        first_gap = (blocks[0][0] + self.BLOCK, blocks[1][0])
        self.assertEqual(W.sample_overlaps(size, first_gap[0], first_gap[1] - first_gap[0], self.BLOCK, self.BLOCKS), [])
        self.assertEqual(W.sample_overlaps(size, first_gap[0] - 1, 2, self.BLOCK, self.BLOCKS), [(0, 1)])
        self.assertEqual(W.sample_overlaps(size, first_gap[1] - 1, 2, self.BLOCK, self.BLOCKS), [(blocks[1][0], 1)])
        self.assertEqual(W.sample_overlaps(size, 0, size, self.BLOCK, self.BLOCKS), [(o, self.BLOCK) for o, _ in blocks])
        self.assertEqual(W.sample_overlaps(size, 10, 0, self.BLOCK, self.BLOCKS), [])

    def test_the_real_key_source_table_loads_with_its_parameters_and_the_equator_stream_the_water_textures_sit_on(self):
        table = W.load_key_sources()
        self.assertEqual((table.block_size, table.blocks), (65536, 16))
        self.assertEqual(table.files["sharedassets51.assets.resS"], ("arena_equator",))
        self.assertEqual(table.files["sharedassets28.resource"], ("arena_airpit",))
        self.assertNotIn("sharedassets36.assets", table.files)             # the ripple texture sits inline in this .assets file, which no key reads

    def test_a_range_on_a_sampled_block_is_refused_and_one_between_blocks_passes(self):
        with tempfile.TemporaryDirectory() as temporary:
            data_dir = Path(temporary)
            size = 2 * 1024 * 1024
            (data_dir / "sharedassets9.assets.resS").write_bytes(os.urandom(size))
            sources = self.sources("sharedassets9.assets.resS")
            second_block = W.sample_blocks(size, self.BLOCK, self.BLOCKS)[1][0]
            ok = {"path": "archive:/CAB-x/sharedassets9.assets.resS", "offset": self.BLOCK + 100, "size": 4096}
            W.check_not_key_source("texture _foam", ok, b"x" * 4096, data_dir, sources)
            for offset in (0, 1000, second_block - 10, size - 100):
                with self.subTest(offset=offset), self.assertRaisesRegex(HardExtractError, "sampled block"):
                    W.check_not_key_source("texture _foam", dict(ok, offset=offset), b"x" * 4096, data_dir, sources)
            # a stream of a file that no key reads is never refused on its range
            W.check_not_key_source("texture _foam", {"path": "sharedassets8.assets.resS", "offset": 0, "size": 4096}, b"x" * 4096,
                                   data_dir, sources)

    def test_a_texture_that_is_a_whole_key_source_file_is_refused_wherever_it_claims_to_come_from(self):
        with tempfile.TemporaryDirectory() as temporary:
            data_dir = Path(temporary)
            payload = os.urandom(700_000)
            (data_dir / "sharedassets9.resource").write_bytes(payload)
            sources = self.sources("sharedassets9.resource")
            for stream in ({}, {"path": "", "offset": 0, "size": 0}, {"path": "sharedassets7.assets.resS", "offset": 5, "size": 9}):
                with self.subTest(stream=stream), self.assertRaisesRegex(HardExtractError, "byte copy of key-source file sharedassets9.resource"):
                    W.check_not_key_source("texture _normals", stream, payload, data_dir, sources)
            # the same length with other bytes is not a copy; a different length is not even hashed
            W.check_not_key_source("texture _normals", {}, os.urandom(len(payload)), data_dir, sources)
            W.check_not_key_source("texture _normals", {}, payload[:-1], data_dir, sources)

    def test_a_key_source_file_this_machine_does_not_have_is_not_an_error_for_the_whole_file_rule(self):
        with tempfile.TemporaryDirectory() as temporary:
            W.check_not_key_source("texture _foam", {}, b"x" * 100, Path(temporary), self.sources("sharedassets9.resource"))

    def test_a_malformed_key_source_table_is_refused_and_a_shared_file_lists_every_map(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "key-sources.json"
            path.write_text(json.dumps({"schema": 1, "sample": {"block_size": 65536, "blocks": 16}, "maps": {}}), encoding="utf-8")
            with self.assertRaises(HardExtractError):
                W.load_key_sources(path)
            path.write_text(json.dumps({"schema": 2, "sample": {"block_size": 65536, "blocks": 16},
                                        "maps": {"arena_a": [{"index": 1, "scene": "S", "files": ["sharedassets1.assets.resS"]}],
                                                 "arena_b": [{"index": 1, "scene": "S", "files": ["sharedassets1.assets.resS", "sharedassets2.resource"]}]}}),
                            encoding="utf-8")
            table = W.load_key_sources(path)
            self.assertEqual(table.files, {"sharedassets1.assets.resS": ("arena_a", "arena_b"), "sharedassets2.resource": ("arena_b",)})


@unittest.skipUnless((ARENA / "level256").is_file() and (ARENA / "globalgamemanagers").is_file(), "Arena is not installed")
class RealSourceTests(unittest.TestCase):
    """The reviewed facts of the table against the installed Arena (0.6.1.0.48206): the scenes sit at their levels, each holds the Sea and the
    unused 'WaterForSSrv3', no other level holds a drawing container, and no texture touches the key material."""

    @classmethod
    def setUpClass(cls):
        import extract_bay5
        cls.levels = {}
        for key, row in W.MAPS.items():
            env, paths = extract_bay5.load_environment_bay5(str(ARENA), [row.level_index])
            cls.levels[key] = (env, env.files[paths[row.level_index]["level"]])

    def test_each_scene_is_still_at_its_level_index(self):
        for row in W.MAPS.values():
            with self.subTest(map=row.map_key):
                W.verify_scene(ARENA, row)

    def test_a_row_whose_scene_moved_is_refused(self):
        moved = W.WaterMap("arena_iceberg", "iceberg", 255, "Arena_Sanatorium", "Sea", "Sea/Plane")
        with self.assertRaisesRegex(HardExtractError, "no longer scene"):
            W.verify_scene(ARENA, moved)

    def test_each_level_holds_the_sea_and_one_empty_container_and_select_sea_accepts_it(self):
        for key, row in W.MAPS.items():
            with self.subTest(map=key):
                containers = W.find_containers(self.levels[key][1])
                self.assertEqual(sorted(path for _r, _d, path, _a in containers), ["Sea", "WaterForSSrv3"])
                selected = W.select_sea(containers, row)
                self.assertEqual(selected[2], "Sea")
                empty = [d for _r, d, path, _a in containers if path == "WaterForSSrv3"][0]
                self.assertEqual(W.real_targets(empty), [])

    def test_no_other_level_of_either_map_holds_a_water_container(self):
        for key, row in W.MAPS.items():
            with self.subTest(map=key):
                self.assertEqual(W.census_other_levels(ARENA, row), 0)

    def test_a_census_that_reaches_another_maps_sea_stops_the_export(self):
        # Bay5's level84 holds a Sea with a real target: scanned as one of Iceberg's census levels it must stop the export
        row = W.WaterMap("arena_iceberg", "iceberg", 256, "Arena_Sanatorium", "Sea", "Sea/Plane", census_levels=(84,))
        with self.assertRaisesRegex(HardExtractError, "another WaterContainer"):
            W.census_other_levels(ARENA, row)

    def test_no_water_texture_of_either_map_touches_a_sampled_block_or_copies_a_key_source_file(self):
        sources = W.load_key_sources()
        streams = 0
        for key in W.MAPS:
            reader, decoded = [(r, d) for r, d, path, _a in W.find_containers(self.levels[key][1]) if path == "Sea"][0]
            for name, pointer in decoded["texture_pointers"].items():
                texture = W.dereference(reader, pointer, "Texture2D")
                stream = texture.read_typetree().get("m_StreamData") or {}
                W.check_not_key_source(f"{key} {name}", stream, texture.read().get_image_data(), ARENA, sources)
                if stream.get("path"):
                    streams += 1
                    self.assertEqual(Path(stream["path"]).name, "sharedassets51.assets.resS")
                    self.assertIn("sharedassets51.assets.resS", sources.files)
        self.assertEqual(streams, 6)                       # normals, details and foam of both maps; the ripple texture sits inline

    def test_the_guard_would_refuse_a_texture_that_sat_on_a_sampled_block_of_that_very_stream(self):
        sources = W.load_key_sources()
        size = (ARENA / "sharedassets51.assets.resS").stat().st_size
        block = W.sample_blocks(size, sources.block_size, sources.blocks)[5]
        with self.assertRaisesRegex(HardExtractError, "sampled block"):
            W.check_not_key_source("synthetic", {"path": "sharedassets51.assets.resS", "offset": block[0] - 100, "size": 200},
                                   b"x" * 200, ARENA, sources)


ICEBERG_WATER = WORK / "extract" / "iceberg-water"
BAY5_WATER = WORK / "extract" / "bay5-water"


def package(directory):
    return json.loads((directory / "water.json").read_text(encoding="utf-8"))


@unittest.skipUnless((ICEBERG_WATER / "water.json").is_file(), "work/extract/iceberg-water is not exported (extract_water.py --map arena_iceberg)")
class IcebergPackageTests(unittest.TestCase):
    def test_the_package_is_icebergs_sea_with_the_four_textures_it_lists(self):
        data = package(ICEBERG_WATER)
        self.assertEqual((data["schema_version"], data["map_key"], data["target_shader"]), (W.SCHEMA_VERSION, "arena_iceberg", W.TARGET_SHADER))
        self.assertEqual((data["target"]["scene"], data["target"]["hierarchy_path"]), ("Arena_Sanatorium", "Sea/Plane"))
        self.assertEqual((data["source"]["file"], data["source"]["owner_path"], data["source"]["container_count"]), ("level256", "Sea", 2))
        sidecar = json.loads((REPO / "tools" / "maps" / "iceberg.json").read_text(encoding="utf-8"))
        self.assertIn(data["target"]["scene"], sidecar["all_scene_names"])
        self.assertEqual(set(data["textures"]), set(W.TEXTURE_FIELDS))
        listed = set()
        for field, texture in data["textures"].items():
            blob = (ICEBERG_WATER / texture["file"]).read_bytes()
            listed.add(texture["file"])
            self.assertEqual(texture["file"], "textures/" + field.lstrip("_") + ".bin")
            self.assertEqual(len(blob), texture["byte_count"])
            self.assertEqual(hashlib.sha256(blob).hexdigest(), texture["sha256"])
            self.assertEqual(W.texture_byte_count(texture["width"], texture["height"], texture["mip_count"], texture["format"]), len(blob))
        files = {p.relative_to(ICEBERG_WATER).as_posix() for p in ICEBERG_WATER.rglob("*") if p.is_file()}
        self.assertEqual(files, listed | {"water.json"})

    @unittest.skipUnless((BAY5_WATER / "water.json").is_file(), "work/extract/bay5-water is not exported")
    def test_the_settings_and_textures_are_the_harbors(self):
        # Iceberg's Sea container carries the very settings of Bay5's (checked 2026-10-31, the same shared prefab): only the identity differs.
        iceberg, bay5 = package(ICEBERG_WATER), package(BAY5_WATER)
        self.assertEqual(iceberg["settings"], bay5["settings"])
        self.assertEqual(iceberg["textures"], bay5["textures"])
        for texture in iceberg["textures"].values():
            self.assertEqual((ICEBERG_WATER / texture["file"]).read_bytes(), (BAY5_WATER / texture["file"]).read_bytes())


if __name__ == "__main__":
    unittest.main()
