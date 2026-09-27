"""PLAN-TWO-MAPS item 8 -- Unity-independent unit tests for extract_ai_anchors.py: the raw byte
decoders (synthetic fixtures built with the mirror-image `_pack_*` helpers below, pattern of
test_physics_props.py's `raw_small_physics_object`) and the static per-map table/paths. No BSG data
(no UnityPy import, no Arena install read) in any test here, matching test_banners.py/
test_loadscreens.py's own convention -- except the one optional real-source smoke test at the
bottom, guarded by the same ARENA_SOURCE skip idiom test_navmesh_modifiers.py already uses.
"""
from __future__ import annotations

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

    def test_exactly_two_maps(self):
        self.assertEqual(set(A.MAPS), {"equator", "bay5"})

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
        maps_dir = os.path.dirname(A.default_output_path("equator"))
        self.assertEqual(os.path.basename(maps_dir), "maps")


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


if __name__ == "__main__":
    unittest.main()
