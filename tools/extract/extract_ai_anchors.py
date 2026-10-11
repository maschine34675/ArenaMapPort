#!/usr/bin/env python3
"""ArenaMapPort AI anchors extractor -- PLAN-TWO-MAPS item 8 (docs/progress/two-maps-item-8-ai-anchors.md).

Why. `tools/server/make_base_json.py` gives every `SpawnPointParams.CorePointId` the value 0,
while Arena's own core-point ids start at 1 (see the census `AICorePointHolder` dump). The client's
existing fallback (`AICorePointFallback`/`PatrolWayFallback` in ArenaMapPort.Client) therefore
synthesizes a single id-0/group-0 core point and NavMesh-derived patrol ways from the Bot spawn
markers, which works but discards Arena's own authored `PatrolWay -> PatrolPoint` hub/subpoint
hierarchy and `AICorePointHolder -> AICorePoint` net -- both fully present in the excluded
`TeamFight_AI` scene (level52 Equator / level78 Bay5), which extract_equator.py/extract_bay5.py
never extract (fresh bot support is authored for SPT, not ported geometry). This tool reads THAT
scene, raw-decodes the handful of MonoBehaviour classes involved, and writes a small sidecar per
map (`tools/maps/<key>.ai.json`) the client loads at raid start to author the REAL topology instead
of synthesizing one -- see ArenaMapPort.Client/AiAnchors.cs, AICorePointRepairs.cs,
PatrolWayRepairs.cs. A map without a sidecar (the deferred maps) keeps today's synthesis unchanged.

Decoding technique. Player-built data carries no typetree for custom (non-built-in) script classes,
so every field below is read directly from `obj.get_raw_data()` -- the same technique
extract_physics_props.py's `decode_sound_type`/analysis/scripts/census.py's `resolve_script_name`
already establish: `parse_monobehaviour_head()` gives the generic MonoBehaviour header (m_GameObject
PPtr, m_Enabled + 3-byte align, m_Script PPtr, m_Name int32-length-prefixed + 4-aligned bytes), then
the class-specific tail is `struct.unpack_from` in C# declaration order (fields 4-byte aligned; bool
= 1 byte + 3 pad; PPtr = int32 fileID + int64 pathID, 12 bytes; List<T>/T[] = int32 count +
count*element). Field layouts are transcribed from the EFT 0.16.9.5 decompile
(D:/SPT41/Development/_decompiled/{AICorePoint,AICorePointHolder,PatrolWay,PatrolPoint}.cs), keeping
only fields Unity actually serializes (public fields and [SerializeField] private fields). Every
decode is self-validating: the offset after the last field parsed must equal `len(raw)` exactly (a
`HardExtractError` otherwise -- a wrong layout guess, not silently wrong data).

ConnectionsAtNet (AICorePoint's graph-edge fields, `_connectionsAtNetId`/`ConnectionsAtNet`) is
parsed (required to advance the byte offset correctly and keep the self-validation meaningful) but
deliberately NOT exported to the sidecar: its only populator at runtime is `AICorePoint.Restore(List
<AICorePoint>)`, called exclusively from `AICorePointHolder.DrawInner()`, itself only reachable from
`OnDrawGizmos`/`OnDrawGizmosSelected` -- Unity Editor Scene-view callbacks that never fire in a built
EFT client or a live raid (see the item-8 section of docs/PLAN-TWO-MAPS.md, corrected 2026-09-27:
the earlier "unrecoverable custom data" claim was wrong -- it decodes exactly like every other field
here; it stays unauthored because its own populator never runs outside the editor, not because the
bytes are unreadable). The client mirrors that by leaving every authored AICorePoint's connections
empty (`ClearConnections()`), same as today's single synthetic point.

Self-validation beyond the raw byte offsets: (a) `AICorePointHolder.CorePoints`' own PPtr list must
name exactly the set of path_ids the independent class-keyword scan found tagged "AICorePoint" (PPtr
vs class-census agreement); (b) each `PatrolPoint` a hub's `subPoints` PPtr list names must resolve
to an object whose OWN Transform's `m_Father` PPtr points back at the hub's Transform (PPtr vs
scene-hierarchy agreement) -- catches a mismatched/rebuilt hierarchy the flat PPtr graph alone would
miss; (c) exactly one `PatrolWay` component is expected per map (matches the census; a future
official Arena patch shipping more would fail loudly here rather than silently drop data).

World positions reuse analysis/scripts/census.py's own `world_transform_of_transform()` (walks
m_Father up to the scene root, composing local TRS) -- the same helper `spawn_zone_anchors()`
already uses for spawn/zone markers, not reimplemented here.

Bay5's AI scene duplicate BuildSettings index: same technique as extract_bay5.py's
`resolve_scene_indices()` module docstring ("Duplicate-BuildSettings-index resolver") -- Bay5's own
BuildSettings.scenes lists `Arena_Bay5_TeamFight_AI` at BOTH idx 78 (the full scene) and idx 222 (an
emptied duplicate, the same repeated-basename-different-full-path-list trap already documented
there for TeamFight_AI/Sound on the geometry scene set); resolved here by exact full asset path,
preferring whichever index's own `sharedassets<N>.assets` is the larger (non-stripped) file, with a
hard assertion against the pre-verified expected index (78) so a future Arena patch reordering
BuildSettings can't silently swap in the stripped copy. Equator's AI scene (idx 52) has no such
duplicate (verified directly; extract_equator.py's own SCENE_REGISTRY already established idx 52 ==
Arena_Equator_TeamFight_AI with no ambiguity).

`common.scene_files(scene_dict, base=...)` binds its default `base=` at import time (a module
constant pointing at a placeholder drive) -- always pass `base=` explicitly (docs/PLAN-TWO-MAPS.md
item 8's own "Verified facts" note), never rely on the default.

AirPit (PLAN-AIRPIT item 4). The MAPS table gains ``airpit`` = (33, "Arena_Airpit_TeamFight_AI");
index and name are the ``ai`` scene role of tools/extract/plans/airpit.json (a test keeps the two
in step, and ``--plan`` reads the role directly so a later map needs no table entry). The scene is
unambiguous in BuildSettings (one entry), and the decode self-validates without a change: 19 core
points, one PatrolWay with 3 hubs holding 12/12/11 = 35 sub points, 38 PatrolPoints all consumed
(Equator 14/2/21, Bay5 11/4/43).

CLI:
    python extract_ai_anchors.py --map equator --output tools/maps/equator.ai.json
    python extract_ai_anchors.py --map bay5 --output tools/maps/bay5.ai.json
    python extract_ai_anchors.py --map equator --map bay5    (writes both maps' default paths)
    python extract_ai_anchors.py --map airpit                (writes tools/maps/airpit.ai.json)
    python extract_ai_anchors.py --plan tools/extract/plans/airpit.json
The default without --map stays equator + bay5 (their sidecars are shipped as released); name
every other map explicitly.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import struct
import sys
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)
sys.path.insert(0, os.path.normpath(os.path.join(_HERE, "..", "..", "analysis", "scripts")))

import UnityPy  # noqa: E402

import census as arena_census  # analysis/scripts/census.py -- resolve_script_name / world_transform_of_transform  # noqa: E402
from common import scene_files  # noqa: E402
from common_ids import HardExtractError  # noqa: E402
from source_plan import PlanError, load_plan, read_build_scenes, scene_for_role, verify_scene_mapping  # noqa: E402

SCHEMA_VERSION = 1
DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
DEFAULT_MAPS_DIR = os.path.normpath(os.path.join(_HERE, "..", "maps"))

# The shipped Equator and Bay5 sidecars were written when twelve maps were deferred, and their
# `_comment` says so. They are tracked release data, so that wording is kept byte-for-byte; every
# later map (AirPit) gets wording that names no count, which cannot go stale as maps are released.
LEGACY_COMMENT_MAP_KEYS = frozenset({"equator", "bay5"})


def missing_file_note(map_key: str) -> str:
    """Parenthetical of the sidecar `_comment` that says which maps lack a sidecar."""
    if map_key in LEGACY_COMMENT_MAP_KEYS:
        return "the twelve deferred maps"
    return "any map without a sidecar"


EQUATOR_AI_SCENE_IDX = 52
EQUATOR_AI_SCENE_NAME = "Arena_Equator_TeamFight_AI"
BAY5_AI_SCENE_NAME = "Arena_Bay5_TeamFight_AI"
BAY5_AI_SCENE_FULL_PATH = "Assets/Content/Locations/Arena_Bay5/Arena_Bay5_TeamFight_AI.unity"
BAY5_AI_SCENE_EXPECTED_IDX = 78  # scratchpad decode_ai.py / ai_decode_out.json, this pass

CORE_POINT_CLASS = "AICorePoint"
PATROL_POINT_CLASS = "PatrolPoint"
PATROL_WAY_CLASS = "PatrolWay"
HOLDER_CLASS = "AICorePointHolder"


# --------------------------------------------------------------------------
# Raw byte decoding (module docstring's "Decoding technique")
# --------------------------------------------------------------------------

def _read_i32(raw: bytes, off: int):
    return struct.unpack_from("<i", raw, off)[0], off + 4


def _read_f32(raw: bytes, off: int):
    return struct.unpack_from("<f", raw, off)[0], off + 4


def _read_bool(raw: bytes, off: int):
    v = struct.unpack_from("<B", raw, off)[0]
    off += 1
    return bool(v), (off + 3) & ~3


def _read_pptr(raw: bytes, off: int):
    file_id = struct.unpack_from("<i", raw, off)[0]
    path_id = struct.unpack_from("<q", raw, off + 4)[0]
    return (file_id, path_id), off + 12


def _read_string(raw: bytes, off: int):
    length, off = _read_i32(raw, off)
    s = bytes(raw[off:off + length])
    off += length
    off = (off + 3) & ~3
    return s.decode("utf-8", errors="replace"), off


def _read_list(raw: bytes, off: int, elem_reader):
    count, off = _read_i32(raw, off)
    items = []
    for _ in range(count):
        v, off = elem_reader(raw, off)
        items.append(v)
    return items, off


def _read_pptr_list(raw: bytes, off: int):
    return _read_list(raw, off, _read_pptr)


def _read_int_list(raw: bytes, off: int):
    return _read_list(raw, off, _read_i32)


def _read_header(raw: bytes):
    """m_GameObject PPtr, m_Enabled(+align), m_Script PPtr, m_Name -- every MonoBehaviour's
    generic prefix, identical across all four classes decoded here."""
    go, off = _read_pptr(raw, 0)
    _enabled, off = _read_bool(raw, off)
    _script, off = _read_pptr(raw, off)
    _name, off = _read_string(raw, off)
    return {"m_GameObject": go}, off


def _decode(name: str, raw: bytes, body):
    """Runs `body(raw) -> (out_dict, off)`, converting a too-short buffer's low-level
    struct/index error into a HardExtractError (a truncated/garbled instance is a hard extractor
    failure like any other here, not an uncaught crash) and enforcing the self-validation every
    decode_* function below relies on: the offset after the last field parsed must equal
    len(raw) exactly, or the layout guess (or the input) is wrong."""
    try:
        result, off = body(raw)
    except (struct.error, IndexError) as e:
        raise HardExtractError(f"{name} decode failed (buffer too short for this layout): {e}") from e
    if off != len(raw):
        raise HardExtractError(f"{name} decode length mismatch: end_off={off} raw_len={len(raw)}")
    return result


def decode_aicorepoint(raw: bytes) -> dict:
    """AICorePoint.cs:20-30 -- _id, _connectionGroupId, _connectionsAtNetId (List<int>),
    ConnectionsAtNet (List<PPtr<AICorePoint>>). The connection fields are parsed (required to reach
    the correct end offset) but not returned -- see module docstring."""
    def body(raw):
        head, off = _read_header(raw)
        id_, off = _read_i32(raw, off)
        cg_id, off = _read_i32(raw, off)
        _conn_ids, off = _read_int_list(raw, off)
        _conn_net, off = _read_pptr_list(raw, off)
        return {"m_GameObject": head["m_GameObject"], "id": id_, "connection_group_id": cg_id}, off
    return _decode("AICorePoint", raw, body)


def decode_patrolpoint(raw: bytes) -> dict:
    """PatrolPoint.cs:10-30 -- _corePointId, Id, CanUseByBoss, PatrolWay(PPtr), ShallSit,
    PatrolPointType(int enum), ActionData(PPtr), PointWithLookSides(PPtr), SubManual,
    subPoints (List<PPtr<PatrolPoint>>). Only _corePointId/Id/subPoints are exported; the rest is
    parsed solely to keep the byte offset (and therefore the self-validation) correct."""
    def body(raw):
        head, off = _read_header(raw)
        core_point_id, off = _read_i32(raw, off)
        id_, off = _read_i32(raw, off)
        _can_use_by_boss, off = _read_bool(raw, off)
        _patrol_way, off = _read_pptr(raw, off)
        _shall_sit, off = _read_bool(raw, off)
        _patrol_point_type, off = _read_i32(raw, off)
        _action_data, off = _read_pptr(raw, off)
        _point_with_look_sides, off = _read_pptr(raw, off)
        _sub_manual, off = _read_bool(raw, off)
        sub_points, off = _read_pptr_list(raw, off)
        out = {"m_GameObject": head["m_GameObject"], "core_point_id": core_point_id, "id": id_, "sub_points_pptr": sub_points}
        return out, off
    return _decode("PatrolPoint", raw, body)


def decode_patrolway(raw: bytes) -> dict:
    """PatrolWay.cs:10-19 -- PatrolType(int enum), Points (List<PPtr<PatrolPoint>>), _maxPersons,
    CoefSubPoints. Only Points (the hub order) is exported."""
    def body(raw):
        head, off = _read_header(raw)
        _patrol_type, off = _read_i32(raw, off)
        points, off = _read_pptr_list(raw, off)
        _max_persons, off = _read_i32(raw, off)
        _coef_sub_points, off = _read_f32(raw, off)
        return {"m_GameObject": head["m_GameObject"], "points_pptr": points}, off
    return _decode("PatrolWay", raw, body)


def decode_aicorepointholder(raw: bytes) -> dict:
    """AICorePointHolder.cs:57-63 -- DrawHeight, CorePoints (List<PPtr<AICorePoint>>), IsAlwaysDraw.
    Only CorePoints is used (cross-validation, see module docstring)."""
    def body(raw):
        head, off = _read_header(raw)
        _draw_height, off = _read_f32(raw, off)
        core_points, off = _read_pptr_list(raw, off)
        _is_always_draw, off = _read_bool(raw, off)
        return {"m_GameObject": head["m_GameObject"], "core_points_pptr": core_points}, off
    return _decode("AICorePointHolder", raw, body)


_DECODERS = {
    CORE_POINT_CLASS: decode_aicorepoint,
    PATROL_POINT_CLASS: decode_patrolpoint,
    PATROL_WAY_CLASS: decode_patrolway,
    HOLDER_CLASS: decode_aicorepointholder,
}


# --------------------------------------------------------------------------
# Bay5's duplicate-BuildSettings-index resolver (module docstring)
# --------------------------------------------------------------------------

def resolve_bay5_ai_scene_index(source: str) -> int:
    gm_path = f"{source}/globalgamemanagers"
    if not os.path.exists(gm_path):
        gm_path = f"{source}/globalgamemanagers.assets"
    env = UnityPy.load(gm_path)
    scenes: list[str] = []
    for obj in env.objects:
        if obj.type.name == "BuildSettings":
            scenes = obj.read_typetree().get("scenes") or []
            break
    if not scenes:
        raise HardExtractError(f"no BuildSettings.scenes found in {gm_path}")

    candidates = [i for i, s in enumerate(scenes) if s == BAY5_AI_SCENE_FULL_PATH]
    if not candidates:
        raise HardExtractError(f"scene {BAY5_AI_SCENE_FULL_PATH!r} not found anywhere in BuildSettings.scenes")
    if len(candidates) == 1:
        chosen = candidates[0]
    else:
        def sa_size(idx: int) -> int:
            p = f"{source}/sharedassets{idx}.assets"
            return os.path.getsize(p) if os.path.exists(p) else -1

        chosen = max(candidates, key=sa_size)
        print(f"[extract_ai_anchors] {BAY5_AI_SCENE_NAME}: {len(candidates)} BuildSettings entries {candidates} "
              f"(duplicate path, same as extract_bay5.py's TeamFight_AI/Sound pattern) -- chose idx {chosen} "
              f"(largest sharedassets<N>.assets: the full, non-stripped copy)", file=sys.stderr)
    if chosen != BAY5_AI_SCENE_EXPECTED_IDX:
        raise HardExtractError(
            f"resolver picked idx {chosen} for {BAY5_AI_SCENE_NAME!r}, but the pre-verified expected index is "
            f"{BAY5_AI_SCENE_EXPECTED_IDX} (scratchpad decode_ai.py/ai_decode_out.json) -- investigate before proceeding"
        )
    return chosen


# --------------------------------------------------------------------------
# Per-object world position / hierarchy (module docstring's "World positions" / self-validation)
# --------------------------------------------------------------------------

def _transform_of(obj, f):
    """The Transform component of a MonoBehaviour's own GameObject -- same resolution
    census.spawn_zone_anchors() already uses (m_Component[0] is always the Transform)."""
    mb_head = obj.parse_monobehaviour_head()
    go = f.objects[mb_head.m_GameObject.path_id].read_typetree()
    comps = go.get("m_Component") or go.get("m_Components")
    transform_ptr = comps[0]["component"]
    return f.objects[transform_ptr["m_PathID"]]


def _world_position(tobj) -> tuple[float, float, float]:
    pos, _rot = arena_census.world_transform_of_transform(tobj)
    return pos


def _parent_path_id(tobj) -> int:
    t = tobj.read_typetree()
    father = t.get("m_Father") or {}
    return father.get("m_PathID", 0)


def _gameobject_name(obj, f) -> str:
    mb_head = obj.parse_monobehaviour_head()
    go = f.objects[mb_head.m_GameObject.path_id].read_typetree()
    return go.get("m_Name", "")


# --------------------------------------------------------------------------
# Per-map extraction
# --------------------------------------------------------------------------

def _collect_ai_objects(f) -> dict:
    """One pass over every MonoBehaviour in the AI scene file, keyed by path_id, decoding the four
    classes this tool cares about and recording each one's world position / own+parent Transform
    path_id (for the hierarchy-agreement self-check)."""
    by_class: dict[str, dict[int, dict]] = {cls: {} for cls in _DECODERS}
    for obj in list(f.objects.values()):
        if obj.type.name != "MonoBehaviour":
            continue
        cls_name, status = arena_census.resolve_script_name(obj)
        if status != "ok" or not cls_name:
            continue
        short = cls_name.rsplit(".", 1)[-1]
        decoder = _DECODERS.get(short)
        if decoder is None:
            continue
        raw = obj.get_raw_data()
        record = decoder(raw)
        tobj = _transform_of(obj, f)
        record["path_id"] = obj.path_id
        record["transform_path_id"] = tobj.path_id
        record["parent_transform_path_id"] = _parent_path_id(tobj)
        record["world_pos"] = _world_position(tobj)
        record["name"] = _gameobject_name(obj, f)
        by_class[short][obj.path_id] = record
    return by_class


def _vec(pos) -> list:
    return [pos[0], pos[1], pos[2]]


def extract_map(map_key: str, ai_scene_idx: int, ai_scene_name: str, source: str) -> dict:
    print(f"[extract_ai_anchors] {map_key}: loading level{ai_scene_idx} ({ai_scene_name}) from {source}", file=sys.stderr)
    paths = scene_files({ai_scene_idx: ai_scene_name}, base=source)  # always pass base= explicitly (item 8 note)
    paths.append(f"{source}/globalgamemanagers.assets")
    env = UnityPy.load(*paths)
    level_path = f"{source}/level{ai_scene_idx}"
    f = env.files[level_path]

    by_class = _collect_ai_objects(f)
    core_points = by_class[CORE_POINT_CLASS]
    patrol_points = by_class[PATROL_POINT_CLASS]
    patrol_ways = by_class[PATROL_WAY_CLASS]
    holders = by_class[HOLDER_CLASS]

    if len(holders) != 1:
        raise HardExtractError(f"{map_key}: expected exactly one {HOLDER_CLASS}, found {len(holders)}")
    holder = next(iter(holders.values()))
    holder_ids = {pid for (_file_id, pid) in holder["core_points_pptr"]}
    scanned_ids = set(core_points.keys())
    if holder_ids != scanned_ids:
        raise HardExtractError(
            f"{map_key}: {HOLDER_CLASS}.CorePoints PPtr set {sorted(holder_ids)} does not match the "
            f"class-scan set {sorted(scanned_ids)} -- PPtr vs class-census disagreement"
        )

    if len(patrol_ways) != 1:
        raise HardExtractError(f"{map_key}: expected exactly one {PATROL_WAY_CLASS}, found {len(patrol_ways)}")
    way = next(iter(patrol_ways.values()))

    out_core_points = []
    for pid, cp in sorted(core_points.items(), key=lambda kv: kv[1]["id"]):
        out_core_points.append({
            "id": cp["id"],
            "connection_group_id": cp["connection_group_id"],
            "position": _vec(cp["world_pos"]),
            "name": cp["name"],
        })

    consumed_patrol_point_ids: set[int] = set()
    hubs_out = []
    for file_id, hub_pid in way["points_pptr"]:
        if file_id != 0:
            raise HardExtractError(f"{map_key}: PatrolWay.Points has a cross-file PPtr ({file_id}, {hub_pid}), not expected")
        hub = patrol_points.get(hub_pid)
        if hub is None:
            raise HardExtractError(f"{map_key}: PatrolWay.Points references path_id {hub_pid}, not a decoded PatrolPoint")
        consumed_patrol_point_ids.add(hub_pid)
        sub_points_out = []
        for sfile_id, sub_pid in hub["sub_points_pptr"]:
            if sfile_id != 0:
                raise HardExtractError(f"{map_key}: hub {hub_pid} subPoints has a cross-file PPtr ({sfile_id}, {sub_pid})")
            sub = patrol_points.get(sub_pid)
            if sub is None:
                raise HardExtractError(f"{map_key}: hub {hub_pid} subPoints references path_id {sub_pid}, not a decoded PatrolPoint")
            # PPtr vs scene-hierarchy agreement (module docstring point (b)): the subpoint's OWN
            # Transform.m_Father must literally be the hub's own Transform.
            if sub["parent_transform_path_id"] != hub["transform_path_id"]:
                raise HardExtractError(
                    f"{map_key}: hub {hub_pid} lists subPoint {sub_pid} by PPtr, but that object's Transform "
                    f"parent ({sub['parent_transform_path_id']}) is not the hub's own Transform "
                    f"({hub['transform_path_id']}) -- PPtr/hierarchy disagreement"
                )
            consumed_patrol_point_ids.add(sub_pid)
            sub_points_out.append(_vec(sub["world_pos"]))
        hubs_out.append({
            "core_point_id": hub["core_point_id"],
            "position": _vec(hub["world_pos"]),
            "sub_points": sub_points_out,
        })

    orphans = set(patrol_points.keys()) - consumed_patrol_point_ids
    if orphans:
        raise HardExtractError(
            f"{map_key}: {len(orphans)} PatrolPoint object(s) found by class scan but never reached via "
            f"PatrolWay.Points/hub.subPoints (path_ids {sorted(orphans)}) -- the flat hub/subpoint model "
            "assumed here does not cover this map's data"
        )

    for cp in out_core_points:
        for v in cp["position"]:
            if not math.isfinite(v):
                raise HardExtractError(f"{map_key}: core point {cp['id']} has a non-finite position {cp['position']}")
    for hub in hubs_out:
        for v in hub["position"]:
            if not math.isfinite(v):
                raise HardExtractError(f"{map_key}: hub (core point {hub['core_point_id']}) has a non-finite position")
        for sp in hub["sub_points"]:
            for v in sp:
                if not math.isfinite(v):
                    raise HardExtractError(f"{map_key}: a sub point of hub (core point {hub['core_point_id']}) has a non-finite position")

    sub_point_total = sum(len(h["sub_points"]) for h in hubs_out)
    print(f"[extract_ai_anchors] {map_key}: {len(out_core_points)} core point(s), {len(hubs_out)} hub(s), "
          f"{sub_point_total} sub point(s) -- {len(patrol_points)} PatrolPoint object(s) total, all consumed",
          file=sys.stderr)

    return {
        "_comment": (
            f"PLAN-TWO-MAPS item 8 (docs/progress/two-maps-item-8-ai-anchors.md) -- Arena's own AI patrol "
            f"hub/core-point topology for {map_key}, raw-decoded from the excluded TeamFight_AI scene "
            f"(tools/extract/extract_ai_anchors.py). Loaded at raid start by ArenaMapPort.Client/AiAnchors.cs "
            "in place of AICorePointFallback/PatrolWayFallback's NavMesh-derived synthesis; a missing/invalid "
            f"file ({missing_file_note(map_key)}) keeps that synthesis unchanged. No connection/edge data: "
            "AICorePoint.ConnectionsAtNet is never populated outside the Unity Editor's own Gizmos callbacks "
            "(see extract_ai_anchors.py's module docstring), so every authored core point here has empty "
            "connections, same as today's single synthetic point."
        ),
        "schema_version": SCHEMA_VERSION,
        "map_key": map_key,
        "core_points": out_core_points,
        "patrol_ways": [{"hubs": hubs_out}],
    }


AIRPIT_AI_SCENE_IDX = 33
AIRPIT_AI_SCENE_NAME = "Arena_Airpit_TeamFight_AI"

MAPS = {
    "equator": (EQUATOR_AI_SCENE_IDX, EQUATOR_AI_SCENE_NAME),
    "bay5": (None, BAY5_AI_SCENE_NAME),  # idx resolved at run time (duplicate BuildSettings entry)
    "airpit": (AIRPIT_AI_SCENE_IDX, AIRPIT_AI_SCENE_NAME),  # the `ai` role of plans/airpit.json
}
# Maps written when --map is not given: the two released-before sidecars, unchanged.
DEFAULT_MAPS = ("equator", "bay5")


def map_key_for_plan(plan: dict) -> str:
    """Sidecar key of a plan-driven map: arena_<stem> -> <stem> (airpit -> airpit.ai.json)."""
    return plan["key"].removeprefix("arena_")


def ai_scene_for_plan(plan: dict) -> tuple[int, str]:
    """(BuildSettings index, scene name) of the plan's AI scene, from its ``ai`` scene role."""
    record = scene_for_role(plan, "ai")
    return record["index"], record["name"]


def default_output_path(map_key: str) -> str:
    return os.path.join(DEFAULT_MAPS_DIR, f"{map_key}.ai.json")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="ArenaMapPort AI anchors extractor (PLAN-TWO-MAPS item 8)")
    parser.add_argument("--source", default=DEFAULT_SOURCE, help="Arena EscapeFromTarkovArena_Data dir (read-only)")
    parser.add_argument("--map", action="append", dest="maps", choices=sorted(MAPS), default=None,
                         help="map key to extract; repeatable. Default: equator and bay5.")
    parser.add_argument("--plan", default=None,
                         help="source plan JSON of a plan-driven map; the AI scene is its `ai` role "
                              "(alternative to --map for a map without a MAPS entry)")
    parser.add_argument("--output", default=None,
                         help="output path (only valid with exactly one map); default tools/maps/<key>.ai.json")
    args = parser.parse_args(argv)

    if args.plan and args.maps:
        parser.error("--plan and --map are alternatives")
    jobs = []  # (map key, scene idx or None, scene name)
    if args.plan:
        try:
            plan = load_plan(Path(args.plan))
            # Fail closed if an Arena update moved a scene the plan names (the ai role included).
            verify_scene_mapping(plan, read_build_scenes(Path(args.source)), Path(args.source))
            idx, scene_name = ai_scene_for_plan(plan)
        except (PlanError, OSError) as exc:
            print(f"[extract_ai_anchors] FAILED: {exc}", file=sys.stderr)
            return 2
        jobs.append((map_key_for_plan(plan), idx, scene_name))
    else:
        for map_key in args.maps or DEFAULT_MAPS:
            jobs.append((map_key, *MAPS[map_key]))
    if args.output and len(jobs) != 1:
        parser.error("--output requires exactly one map")

    for map_key, idx, scene_name in jobs:
        if idx is None:
            idx = resolve_bay5_ai_scene_index(args.source)
        data = extract_map(map_key, idx, scene_name, args.source)
        out_path = args.output or default_output_path(map_key)
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        with open(out_path, "w", encoding="utf-8") as fh:
            json.dump(data, fh, indent=2, sort_keys=True, ensure_ascii=False)
            fh.write("\n")
        print(f"[extract_ai_anchors] {map_key}: wrote {out_path}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
