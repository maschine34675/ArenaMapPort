"""PLAN-TWO-MAPS item 8 -- Unity-independent unit tests for extract_ai_anchors.py: the raw byte
decoders (synthetic fixtures built with the mirror-image `_pack_*` helpers below, pattern of
test_physics_props.py's `raw_small_physics_object`) and the static per-map table/paths. No BSG data
(no UnityPy import, no Arena install read) in any test here, matching test_banners.py/
test_loadscreens.py's own convention -- except the one optional real-source smoke test at the
bottom, guarded by the same ARENA_SOURCE skip idiom test_navmesh_modifiers.py already uses.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_ai_anchors as A  # noqa: E402
from common_ids import HardExtractError  # noqa: E402

ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"


# --------------------------------------------------------------------------
# Synthetic raw-byte fixtures -- mirror-image packers of extract_ai_anchors.py's own _read_*
# helpers, so a fixture's correctness is checked by the decoder's self-validation (end_off ==
# raw_len), not by a second hand-counted byte offset.
# --------------------------------------------------------------------------

def _pack_pptr(file_id: int = 0, path_id: int = 0) -> bytes:
    return struct.pack("<iq", file_id, path_id)


def _pack_bool(v: bool) -> bytes:
    return struct.pack("<B", 1 if v else 0) + b"\x00\x00\x00"


def _pack_i32(v: int) -> bytes:
    return struct.pack("<i", v)


def _pack_f32(v: float) -> bytes:
    return struct.pack("<f", v)


def _pack_string(s: bytes = b"") -> bytes:
    data = struct.pack("<i", len(s)) + s
    return data + bytes((-len(data)) % 4)


def _pack_list(items, elem_packer) -> bytes:
    return struct.pack("<i", len(items)) + b"".join(elem_packer(x) for x in items)


def _pack_header(go_path_id: int = 10) -> bytes:
    return _pack_pptr(0, go_path_id) + _pack_bool(True) + _pack_pptr(1, 99) + _pack_string()


def _raw_aicorepoint(id_: int, cg_id: int, conn_ids=(), conn_net_ids=()) -> bytes:
    raw = _pack_header()
    raw += _pack_i32(id_)
    raw += _pack_i32(cg_id)
    raw += _pack_list(conn_ids, _pack_i32)
    raw += _pack_list(conn_net_ids, lambda pid: _pack_pptr(0, pid))
    return raw


def _raw_patrolpoint(core_point_id: int, id_: int, sub_point_ids=()) -> bytes:
    raw = _pack_header()
    raw += _pack_i32(core_point_id)
    raw += _pack_i32(id_)
    raw += _pack_bool(True)          # CanUseByBoss
    raw += _pack_pptr()              # PatrolWay
    raw += _pack_bool(False)         # ShallSit
    raw += _pack_i32(0)              # PatrolPointType
    raw += _pack_pptr()              # ActionData
    raw += _pack_pptr()              # PointWithLookSides
    raw += _pack_bool(False)         # SubManual
    raw += _pack_list(sub_point_ids, lambda pid: _pack_pptr(0, pid))
    return raw


def _raw_patrolway(point_ids=(), coef_sub_points: float = 1.0) -> bytes:
    raw = _pack_header()
    raw += _pack_i32(0)               # PatrolType
    raw += _pack_list(point_ids, lambda pid: _pack_pptr(0, pid))
    raw += _pack_i32(-1)              # _maxPersons
    raw += _pack_f32(coef_sub_points)
    return raw


def _raw_aicorepointholder(core_point_ids=(), draw_height: float = 10.0) -> bytes:
    raw = _pack_header()
    raw += _pack_f32(draw_height)
    raw += _pack_list(core_point_ids, lambda pid: _pack_pptr(0, pid))
    raw += _pack_bool(False)          # IsAlwaysDraw
    return raw


class AICorePointDecodeTests(unittest.TestCase):
    def test_decodes_id_and_connection_group(self):
        result = A.decode_aicorepoint(_raw_aicorepoint(7, 12, conn_ids=[1, 2], conn_net_ids=[100, 200]))
        self.assertEqual(result["id"], 7)
        self.assertEqual(result["connection_group_id"], 12)
        self.assertNotIn("connections_at_net", result)  # deliberately not exported -- module docstring

    def test_isolated_point_with_no_connections(self):
        # Equator's own near:BotZone ids 13/14 -- empty connection lists, still self-validates.
        result = A.decode_aicorepoint(_raw_aicorepoint(14, 14))
        self.assertEqual((result["id"], result["connection_group_id"]), (14, 14))

    def test_truncated_buffer_raises(self):
        with self.assertRaises(HardExtractError):
            A.decode_aicorepoint(_raw_aicorepoint(1, 1, conn_ids=[1])[:-1])

    def test_trailing_garbage_raises(self):
        with self.assertRaises(HardExtractError):
            A.decode_aicorepoint(_raw_aicorepoint(1, 1) + b"\x00\x00\x00\x00")


class PatrolPointDecodeTests(unittest.TestCase):
    def test_hub_fields(self):
        result = A.decode_patrolpoint(_raw_patrolpoint(core_point_id=4, id_=-1434792055, sub_point_ids=[10, 20, 30]))
        self.assertEqual(result["core_point_id"], 4)
        self.assertEqual(result["id"], -1434792055)
        self.assertEqual(result["sub_points_pptr"], [(0, 10), (0, 20), (0, 30)])

    def test_subpoint_defaults_zero_corepoint_and_id(self):
        # Every real subpoint in both maps' sidecars decodes to exactly this shape.
        result = A.decode_patrolpoint(_raw_patrolpoint(core_point_id=0, id_=0))
        self.assertEqual((result["core_point_id"], result["id"], result["sub_points_pptr"]), (0, 0, []))

    def test_length_mismatch_raises(self):
        with self.assertRaises(HardExtractError):
            A.decode_patrolpoint(_raw_patrolpoint(0, 0, sub_point_ids=[1])[:-1])


class PatrolWayDecodeTests(unittest.TestCase):
    def test_points_order_preserved(self):
        result = A.decode_patrolway(_raw_patrolway(point_ids=[111, 222]))
        self.assertEqual(result["points_pptr"], [(0, 111), (0, 222)])

    def test_length_mismatch_raises(self):
        with self.assertRaises(HardExtractError):
            A.decode_patrolway(_raw_patrolway(point_ids=[1, 2]) + b"\x00")


class AICorePointHolderDecodeTests(unittest.TestCase):
    def test_core_points_pptr_list(self):
        result = A.decode_aicorepointholder(_raw_aicorepointholder(core_point_ids=[1, 2, 3]))
        self.assertEqual(result["core_points_pptr"], [(0, 1), (0, 2), (0, 3)])

    def test_length_mismatch_raises(self):
        with self.assertRaises(HardExtractError):
            A.decode_aicorepointholder(_raw_aicorepointholder(core_point_ids=[1])[:-1])


class VecHelperTests(unittest.TestCase):
    def test_vec_takes_first_three_components(self):
        self.assertEqual(A._vec((1.5, -2.0, 3.25)), [1.5, -2.0, 3.25])


class MapTableTests(unittest.TestCase):
    """The two supported maps and where their sidecar lands -- tools/maps/<key>.ai.json,
    matching the client's own ai/<key>.ai.json staging (see the item-8 progress doc)."""

    def test_exactly_three_maps(self):
        self.assertEqual(set(A.MAPS), {"equator", "bay5", "airpit"})

    def test_default_maps_stay_the_two_released_sidecars(self):
        # Running with no --map must not start rewriting a third map's tracked sidecar.
        self.assertEqual(A.DEFAULT_MAPS, ("equator", "bay5"))

    def test_airpit_scene_identity(self):
        idx, name = A.MAPS["airpit"]
        self.assertEqual((idx, name), (33, "Arena_Airpit_TeamFight_AI"))

    def test_airpit_table_entry_is_the_plans_ai_role(self):
        # PLAN-AIRPIT item 4: the table entry and the plan's `ai` scene role must name one scene.
        from source_plan import PLAN_ROOT, load_plan
        plan = load_plan(PLAN_ROOT / "airpit.json")
        self.assertEqual(A.ai_scene_for_plan(plan), A.MAPS["airpit"])
        self.assertEqual(A.map_key_for_plan(plan), "airpit")

    def test_plan_without_ai_role_is_an_error(self):
        from source_plan import PLAN_ROOT, PlanError, load_plan
        plan = load_plan(PLAN_ROOT / "prison.json")  # roles are declared for AirPit and Bowl only
        with self.assertRaises(PlanError):
            A.ai_scene_for_plan(plan)

    def test_equator_scene_identity(self):
        idx, name = A.MAPS["equator"]
        self.assertEqual((idx, name), (52, "Arena_Equator_TeamFight_AI"))

    def test_bay5_scene_name_index_resolved_at_runtime(self):
        idx, name = A.MAPS["bay5"]
        self.assertIsNone(idx)  # resolved via resolve_bay5_ai_scene_index (duplicate BuildSettings entry)
        self.assertEqual(name, "Arena_Bay5_TeamFight_AI")

    def test_bay5_expected_index_is_the_full_non_stripped_scene(self):
        self.assertEqual(A.BAY5_AI_SCENE_EXPECTED_IDX, 78)

    def test_default_output_paths(self):
        self.assertEqual(os.path.basename(A.default_output_path("equator")), "equator.ai.json")
        self.assertEqual(os.path.basename(A.default_output_path("bay5")), "bay5.ai.json")
        self.assertEqual(os.path.basename(A.default_output_path("airpit")), "airpit.ai.json")
        maps_dir = os.path.dirname(A.default_output_path("equator"))
        self.assertEqual(os.path.basename(maps_dir), "maps")


class CommentWordingTests(unittest.TestCase):
    """The sidecar `_comment` names which maps lack a sidecar. Equator's and Bay5's tracked files keep
    their original "twelve deferred maps" wording byte-for-byte; AirPit (released third) must not
    repeat a count that is already wrong, and no later map may inherit it."""

    LEGACY = "the twelve deferred maps"
    GENERIC = "any map without a sidecar"

    @staticmethod
    def tracked_comment(map_key):
        with open(A.default_output_path(map_key), encoding="utf-8") as fh:
            return json.load(fh)["_comment"]

    def test_legacy_maps_keep_the_released_wording(self):
        self.assertEqual(A.LEGACY_COMMENT_MAP_KEYS, frozenset({"equator", "bay5"}))
        for map_key in ("equator", "bay5"):
            self.assertEqual(A.missing_file_note(map_key), self.LEGACY)

    def test_every_other_map_gets_wording_without_a_count(self):
        for map_key in ("airpit", "shoreline", "a_future_map"):
            self.assertEqual(A.missing_file_note(map_key), self.GENERIC)

    def test_tracked_equator_and_bay5_comments_are_the_released_text(self):
        for map_key in ("equator", "bay5"):
            comment = self.tracked_comment(map_key)
            self.assertIn(f"a missing/invalid file ({self.LEGACY}) keeps that synthesis unchanged", comment)
            self.assertIn(f"topology for {map_key}, raw-decoded", comment)

    def test_tracked_airpit_comment_has_no_stale_count(self):
        comment = self.tracked_comment("airpit")
        self.assertIn(f"a missing/invalid file ({self.GENERIC}) keeps that synthesis unchanged", comment)
        self.assertIn("topology for airpit, raw-decoded", comment)
        self.assertNotIn("twelve", comment)
        self.assertNotIn("deferred", comment)


class CommandLineTests(unittest.TestCase):
    """main(): --map / --plan selection without reading the Arena install (extract_map is patched)."""

    def run_main(self, argv, **patches):
        import contextlib
        import io
        from unittest import mock
        err = io.StringIO()
        with contextlib.redirect_stderr(err),              mock.patch.object(A, "extract_map", return_value={"stub": 1}) as extract,              mock.patch.object(A, "read_build_scenes", return_value=[]),              mock.patch.object(A, "verify_scene_mapping", **patches):
            code = A.main(argv)
        return code, extract, err.getvalue()

    def test_plan_mode_takes_key_index_and_name_from_the_plan_role(self):
        import tempfile
        from source_plan import PLAN_ROOT
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "nested", "x.json")
            code, extract, _err = self.run_main(["--plan", str(PLAN_ROOT / "airpit.json"), "--source", tmp, "--output", out])
            with open(out, encoding="utf-8") as fh:
                written = json.load(fh)
        self.assertEqual(code, 0)
        self.assertEqual(extract.call_args.args, ("airpit", 33, "Arena_Airpit_TeamFight_AI", tmp))
        self.assertEqual(written, {"stub": 1})

    def test_map_mode_uses_the_table_and_default_stays_the_released_pair(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            code, extract, _err = self.run_main(["--map", "airpit", "--source", tmp, "--output", os.path.join(tmp, "a.json")])
        self.assertEqual((code, extract.call_args.args[:3]), (0, ("airpit", 33, "Arena_Airpit_TeamFight_AI")))

    def test_plan_and_map_are_alternatives_and_a_bad_plan_exits_2(self):
        import tempfile
        from source_plan import PLAN_ROOT
        with self.assertRaises(SystemExit):
            self.run_main(["--plan", str(PLAN_ROOT / "airpit.json"), "--map", "airpit"])
        with tempfile.TemporaryDirectory() as tmp:
            code, extract, err = self.run_main(["--plan", os.path.join(tmp, "missing.json"), "--source", tmp])
        self.assertEqual(code, 2)
        self.assertIn("FAILED", err)
        extract.assert_not_called()

    def test_plan_without_an_ai_role_exits_2(self):
        import tempfile
        from source_plan import PLAN_ROOT, PlanError
        with tempfile.TemporaryDirectory() as tmp:
            code, extract, err = self.run_main(["--plan", str(PLAN_ROOT / "prison.json"), "--source", tmp])
        self.assertEqual(code, 2)
        self.assertIn("declares no scene with role 'ai'", err)
        extract.assert_not_called()


@unittest.skipUnless(os.path.isdir(ARENA_SOURCE), "Arena source not reachable from this machine")
class RealSourceExtractionTests(unittest.TestCase):
    """One live UnityPy read per map, cross-checked against the 2026-09-27 research pass'
    verified counts (docs/PLAN-TWO-MAPS.md item 8 / docs/progress/two-maps-item-8-ai-anchors.md)."""

    def test_equator_counts(self):
        data = A.extract_map("equator", A.EQUATOR_AI_SCENE_IDX, A.EQUATOR_AI_SCENE_NAME, A.DEFAULT_SOURCE)
        self.assertEqual(len(data["core_points"]), 14)
        hubs = data["patrol_ways"][0]["hubs"]
        self.assertEqual(len(hubs), 2)
        self.assertEqual(sum(len(h["sub_points"]) for h in hubs), 21)
        self.assertEqual({c["id"] for c in data["core_points"]}, set(range(1, 15)))

    def test_bay5_counts(self):
        idx = A.resolve_bay5_ai_scene_index(A.DEFAULT_SOURCE)
        self.assertEqual(idx, 78)
        data = A.extract_map("bay5", idx, A.BAY5_AI_SCENE_NAME, A.DEFAULT_SOURCE)
        self.assertEqual(len(data["core_points"]), 11)
        hubs = data["patrol_ways"][0]["hubs"]
        self.assertEqual(len(hubs), 4)
        self.assertEqual(sum(len(h["sub_points"]) for h in hubs), 43)
        self.assertEqual({c["id"] for c in data["core_points"]}, set(range(1, 12)))

    def test_airpit_counts(self):
        idx, name = A.MAPS["airpit"]
        data = A.extract_map("airpit", idx, name, A.DEFAULT_SOURCE)
        self.assertEqual(len(data["core_points"]), 19)
        hubs = data["patrol_ways"][0]["hubs"]
        self.assertEqual([len(h["sub_points"]) for h in hubs], [12, 12, 11])
        self.assertEqual({c["id"] for c in data["core_points"]}, set(range(1, 20)))

    def test_tracked_airpit_sidecar_matches_the_live_source(self):
        # tools/maps/airpit.ai.json is git-tracked data; it must be what the extractor writes today.
        idx, name = A.MAPS["airpit"]
        live = A.extract_map("airpit", idx, name, A.DEFAULT_SOURCE)
        with open(A.default_output_path("airpit"), encoding="utf-8") as fh:
            self.assertEqual(json.load(fh), json.loads(json.dumps(live)))



# --------------------------------------------------------------------------------------------------------------
# PLAN-ICEBERG item 7 (2026-10-30): the off-map patrol hubs of Iceberg's AI scene are not shipped
# --------------------------------------------------------------------------------------------------------------

MAPS_DIR = Path(__file__).resolve().parents[1] / "maps"
ICEBERG_AREA = A.PLAY_AREA["iceberg"]


def hub(core_point_id, position, sub_points=()):
    return {"core_point_id": core_point_id, "position": list(position), "sub_points": [list(p) for p in sub_points]}


def core(ident, position):
    return {"id": ident, "connection_group_id": 11, "position": list(position), "name": f"AICore ID:{ident} CG:11"}


class OffMapHubRuleTests(unittest.TestCase):
    """drop_off_map_hubs: whole hubs outside the per-map box go, nothing else is touched, a surprise fails the run."""

    AREA = {"x": (-10.0, 10.0), "z": (-20.0, 0.0)}
    CORES = [core(1, (0.0, 0.0, -10.0)), core(2, (5.0, 0.0, -2.0))]

    def test_only_iceberg_has_a_play_area_row_so_the_released_sidecars_are_untouched(self):
        self.assertEqual(set(A.PLAY_AREA), {"iceberg"})
        self.assertEqual(ICEBERG_AREA, {"x": (-61.0, 121.0), "z": (-130.0, -33.0)})
        self.assertTrue({"equator", "bay5", "airpit", "bowl"}.isdisjoint(A.PLAY_AREA))

    def test_a_hub_outside_the_box_is_dropped_with_its_sub_points_and_an_inside_hub_is_kept(self):
        inside = hub(1, (1.0, 0.0, -5.0), [(2.0, 0.0, -6.0)])
        outside = hub(2, (30.0, -3.9, 12.0), [(31.0, -3.9, 13.0), (29.0, -3.9, 11.0)])
        kept, dropped = A.drop_off_map_hubs(self.CORES, [outside, inside], self.AREA)
        self.assertEqual((kept, dropped), ([inside], [outside]))

    def test_the_box_edge_belongs_to_the_play_area(self):
        edge = hub(1, (10.0, 0.0, 0.0))
        self.assertEqual(A.drop_off_map_hubs(self.CORES, [edge], self.AREA), ([edge], []))
        self.assertEqual(A.drop_off_map_hubs(self.CORES, [hub(1, (10.01, 0.0, 0.0))], self.AREA)[0], [])

    def test_the_height_does_not_matter_only_x_and_z_do(self):
        low = hub(1, (0.0, -3.89, -10.0))   # Iceberg's stale hubs stand at y -3.9; this one is in the box in x / z
        self.assertEqual(A.drop_off_map_hubs(self.CORES, [low], self.AREA)[0], [low])

    def test_a_core_point_outside_the_box_fails_the_run_instead_of_being_dropped(self):
        with self.assertRaisesRegex(HardExtractError, "core point 3"):
            A.drop_off_map_hubs(self.CORES + [core(3, (50.0, 0.0, -10.0))], [], self.AREA)

    def test_a_kept_hub_with_a_stray_sub_point_fails_the_run(self):
        with self.assertRaisesRegex(HardExtractError, "1 of its sub points"):
            A.drop_off_map_hubs(self.CORES, [hub(1, (0.0, 0.0, -10.0), [(1.0, 0.0, -9.0), (40.0, 0.0, -9.0)])], self.AREA)

    def test_nothing_to_drop_returns_everything(self):
        hubs = [hub(1, (0.0, 0.0, -10.0), [(1.0, 0.0, -9.0)]), hub(2, (5.0, 0.0, -2.0))]
        self.assertEqual(A.drop_off_map_hubs(self.CORES, hubs, self.AREA), (hubs, []))


class TrackedIcebergAiSidecarTests(unittest.TestCase):
    """tools/maps/iceberg.ai.json: 11 core points, no patrol way, a record of what was dropped."""

    @staticmethod
    def load(stem):
        path = MAPS_DIR / f"{stem}.ai.json"
        if not path.is_file():
            raise unittest.SkipTest(f"{path.name} not generated yet")
        return json.loads(path.read_text(encoding="utf-8"))

    def test_eleven_core_points_all_inside_the_play_area_and_no_patrol_way(self):
        data = self.load("iceberg")
        self.assertEqual(data["map_key"], "iceberg")
        self.assertEqual([c["id"] for c in data["core_points"]], list(range(1, 12)))
        self.assertTrue(all(c["connection_group_id"] == 11 for c in data["core_points"]))
        for c in data["core_points"]:
            self.assertTrue(A.inside_play_area(c["position"], ICEBERG_AREA), c)
        self.assertEqual(data["patrol_ways"], [])           # the client then builds NavMesh-derived ways (PatrolWayRepairs.cs)

    def test_the_drop_is_recorded_and_matches_what_arena_ships(self):
        dropped = self.load("iceberg")["dropped"]
        self.assertEqual((dropped["hubs"], dropped["sub_points"], dropped["ways_removed"]), (4, 40, 1))
        self.assertEqual(dropped["play_area"], {"x": [-61.0, 121.0], "z": [-130.0, -33.0]})
        self.assertIn("4 hub(s) with 40 sub point(s)", self.load("iceberg")["_comment"])

    def test_the_released_sidecars_have_no_drop_record_and_keep_their_patrol_ways(self):
        for stem, hubs in (("equator", 2), ("bay5", 4), ("airpit", 3), ("bowl", 7)):
            with self.subTest(map=stem):
                data = self.load(stem)
                self.assertNotIn("dropped", data)
                self.assertEqual(len(data["patrol_ways"][0]["hubs"]), hubs)

    def test_every_core_point_height_is_the_ground_it_stands_on(self):
        # the NavMesh half (all 11 on Arena's main component) is in test_iceberg_spawns.py; here only the range of
        # Arena's main component, y -1.8..6.15, which the bake box of PLAN-ICEBERG item 9 covers
        for c in self.load("iceberg")["core_points"]:
            self.assertTrue(-1.8 <= c["position"][1] <= 6.15, c)


@unittest.skipUnless(os.path.isdir(ARENA_SOURCE), "Arena source not reachable from this machine")
class IcebergRealSourceTests(unittest.TestCase):
    PLAN = Path(__file__).resolve().parents[1] / "extract" / "plans" / "iceberg.json"

    @classmethod
    def setUpClass(cls):
        from source_plan import load_plan
        plan = load_plan(cls.PLAN)
        idx, name = A.ai_scene_for_plan(plan)
        cls.live = A.extract_map(A.map_key_for_plan(plan), idx, name, A.DEFAULT_SOURCE)

    def test_the_plans_ai_role_is_level377(self):
        from source_plan import load_plan
        self.assertEqual(A.ai_scene_for_plan(load_plan(self.PLAN)), (377, "Arena_Sanatorium_CheckPoint_AI"))

    def test_the_live_scene_is_eleven_core_points_and_four_stale_hubs(self):
        self.assertEqual(len(self.live["core_points"]), 11)
        self.assertEqual(self.live["patrol_ways"], [])
        self.assertEqual({k: self.live["dropped"][k] for k in ("hubs", "sub_points", "ways_removed")},
                         {"hubs": 4, "sub_points": 40, "ways_removed": 1})

    def test_without_the_rule_the_scene_still_has_the_four_hubs_forty_metres_off_the_map(self):
        from unittest import mock
        with mock.patch.dict(A.PLAY_AREA, clear=True):
            raw = A.extract_map("iceberg", 377, "Arena_Sanatorium_CheckPoint_AI", A.DEFAULT_SOURCE)
        hubs = raw["patrol_ways"][0]["hubs"]
        self.assertEqual((len(hubs), sum(len(h["sub_points"]) for h in hubs)), (4, 40))
        self.assertNotIn("dropped", raw)
        for h in hubs:
            self.assertFalse(A.inside_play_area(h["position"], ICEBERG_AREA))
            self.assertGreater(h["position"][2] - ICEBERG_AREA["z"][1], 38.0)      # z 5..24 against the box edge -33
            for sub in h["sub_points"]:
                self.assertFalse(A.inside_play_area(sub, ICEBERG_AREA))

    def test_the_tracked_sidecar_is_what_the_extractor_writes_today(self):
        path = MAPS_DIR / "iceberg.ai.json"
        if not path.is_file():
            self.skipTest("iceberg.ai.json not generated yet")
        self.assertEqual(json.loads(path.read_text(encoding="utf-8")), json.loads(json.dumps(self.live)))

    def test_the_ten_savage_markers_of_the_scene_are_outside_the_play_area_too(self):
        # not shipped anywhere (bots.json draws its bot spawns from the core points); this pins the survey's claim
        positions = self.marker_positions()
        self.assertEqual(len(positions), 10)
        for p in positions:
            self.assertFalse(A.inside_play_area(p, ICEBERG_AREA), p)

    @staticmethod
    def marker_positions():
        paths = A.scene_files({377: "ai"}, base=A.DEFAULT_SOURCE)
        env = A.UnityPy.load(*paths, f"{A.DEFAULT_SOURCE}/globalgamemanagers.assets")
        level = env.files[f"{A.DEFAULT_SOURCE}/level377"]
        out = []
        for obj in list(level.objects.values()):
            if obj.type.name != "MonoBehaviour":
                continue
            cls, status = A.arena_census.resolve_script_name(obj)
            if status == "ok" and cls and cls.rsplit(".", 1)[-1] == "SpawnPointMarker":
                out.append(A._world_position(A._transform_of(obj, level)))
        return out


if __name__ == "__main__":
    unittest.main()
