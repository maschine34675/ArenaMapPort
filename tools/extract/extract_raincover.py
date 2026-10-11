"""PLAN-TWO-MAPS item 6 -- rain-cover geometry (DryPlane / IndoorTrigger / WeatherObstacle) from
each map's own Scripts scene (Equator level54, Bay5 level85 -- the "Scripts" entry of
analysis/scripts/common.py's EQUATOR/BAY5 scene tables; never part of the five/nine content
scenes the main extractor(s) import).

Hand-built MonoBehaviour typetree node lists (DryPlane/IndoorTrigger/TriggerGroup have no public
Unity-native reader) are adapted, with attribution, from the 2026-09-25 read-only feasibility
spike: <local-scratchpad>/rain-cover-spike/raincover_extract.py. That spike proved, against the
live Arena install:
  - Bay5 level85: 669 DryPlane, 30 IndoorTrigger, 2 TriggerGroup, 1 WeatherObstacle.
  - Equator level54: 0 DryPlane, 1 IndoorTrigger (the whole mall), 1 TriggerGroup, 0 WeatherObstacle
    -- Equator authors no WeatherObstacle in Arena's own data; this module never invents one.
  - Every DryPlane's Mesh PPtr matches its sibling MeshFilter.m_Mesh PPtr byte-for-byte across the
    FULL corpus (669/669 on Bay5) -- confirming (together with the plan's own established fact)
    that every DryPlane uses the same single built-in Quad mesh; this module does not re-resolve
    the external "unity_builtin_extra" file, it only re-proves the cheaper DryPlane/MeshFilter
    agreement as a fail-closed guard against a future Arena patch introducing a custom mesh.
  - IndoorTrigger.Awake() (decompile, EFT.EnvironmentEffect/IndoorTrigger.cs) only calls Reinit()
    (which would recompute Bounds from the live Transform) when the SERIALIZED Bounds field is
    already near-zero; every real IndoorTrigger ships with a non-zero baked Bounds, so Reinit()
    never runs and the game always uses the baked Bounds verbatim. One sampled instance (Bay5
    path_id 8095, "IndoorTrigger 117 (12)") has a baked Bounds identical to a DIFFERENT sibling's
    (path_id 6796) -- almost certainly an editor-time duplicate whose Bounds cache was never
    refreshed -- while its live Transform-recomputed position sits ~60m away. This is exactly the
    "baked-Bounds vs live-recomputed mismatch" the plan's own spike flagged. Since Check(pos) only
    ever reads the baked field, THIS MODULE ALWAYS EXTRACTS THE BAKED BOUNDS, NEVER A LIVE
    TRANSFORM RECOMPUTE -- that is the one ground-truth value the shipped Arena game itself uses.
    world_position/world_rotation are exported for author-time placement/gizmo fidelity only and
    are not read by any functional code path.

Which scene (PLAN-AIRPIT item 4). Equator/Bay5 keep the SCRIPTS_SCENE_INDEX table below. A
plan-driven map names its Scripts scene by the ``scripts`` role on an ``excluded_scenes`` record
of its source plan (source_plan.scene_for_role), so the next map needs no edit here; the CLI
and ``export_for_plan`` take the index from that role and nothing else. AirPit (level36)
measured against the live 0.6.0 install: 2 IndoorTrigger (``Bus_IndoorTrigger`` with a baked
centre 155.2 m from its live transform (125.76 m on the largest single axis, which is what the
mismatch log line and gate measure), ``IndoorTrigger 11`` covering the north ~79 percent of
the play length), 0 DryPlane, no WeatherObstacle.

CLI:
    python extract_raincover.py --plan tools/extract/plans/airpit.json --output <package-dir>
    python extract_raincover.py --map arena_equator --output <package-dir>     (legacy table)
Writes <package-dir>/raincover/raincover.json and prints the manifest summary entry as JSON.
"""
from __future__ import annotations

import os
import sys

import numpy as np

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)
sys.path.insert(0, os.path.normpath(os.path.join(_HERE, "..", "..", "analysis", "scripts")))

import UnityPy  # noqa: E402
from UnityPy.helpers.TypeTreeNode import TypeTreeNode  # noqa: E402
from UnityPy.helpers.Tpk import get_typetree_node  # noqa: E402
from UnityPy.enums import ClassIDType  # noqa: E402

import common  # noqa: E402
import census  # noqa: E402
from source_plan import scene_for_role  # noqa: E402

SCHEMA_VERSION = 1
DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"

# analysis/scripts/common.py's own per-map scene-index tables (docs/PLAN-TWO-MAPS.md §6 facts;
# task brief: Equator Scripts=54, Bay5 Scripts=85). Legacy maps only: plan-driven maps read the
# index from their source plan's `scripts` scene role (scripts_scene_index_for_plan).
SCRIPTS_SCENE_INDEX = {"arena_equator": 54, "arena_bay5": 85}
TARGET_CLASSES = ("DryPlane", "IndoorTrigger", "TriggerGroup", "WeatherObstacle")


# ---------------------------------------------------------------------------------------------
# Hand-built typetree node helpers -- adapted from the rain-cover-spike (see module docstring).
# ---------------------------------------------------------------------------------------------

def _mb_header_dicts(version):
    """Full MonoBehaviour header (level-0 root included) as plain dicts, ready to splice extra
    field nodes onto for TypeTreeNode.from_list(). from_list() keeps only the FIRST level-1 field
    if the level-0 wrapper is omitted -- see the spike's dict_nodes_from_header docstring."""
    head = get_typetree_node(ClassIDType.MonoBehaviour, version)
    out = []

    def walk(n):
        out.append({"m_Level": n.m_Level, "m_Type": n.m_Type, "m_Name": n.m_Name,
                    "m_ByteSize": n.m_ByteSize, "m_MetaFlag": n.m_MetaFlag})
        for c in n.m_Children:
            walk(c)
    walk(head)
    return out


def _pptr(name, level=1):
    return [
        {"m_Level": level, "m_Type": "PPtr", "m_Name": name, "m_ByteSize": 12, "m_MetaFlag": 0},
        {"m_Level": level + 1, "m_Type": "int", "m_Name": "m_FileID", "m_ByteSize": 4, "m_MetaFlag": 0},
        {"m_Level": level + 1, "m_Type": "SInt64", "m_Name": "m_PathID", "m_ByteSize": 8, "m_MetaFlag": 0},
    ]


def _float(name, level=1):
    return [{"m_Level": level, "m_Type": "float", "m_Name": name, "m_ByteSize": 4, "m_MetaFlag": 0}]


def _bool(name, level=1):
    return [{"m_Level": level, "m_Type": "bool", "m_Name": name, "m_ByteSize": 1, "m_MetaFlag": 0x4000}]


def _int(name, level=1):
    return [{"m_Level": level, "m_Type": "int", "m_Name": name, "m_ByteSize": 4, "m_MetaFlag": 0}]


def _vector3(name, level=1):
    return [
        {"m_Level": level, "m_Type": "Vector3f", "m_Name": name, "m_ByteSize": 12, "m_MetaFlag": 0},
        {"m_Level": level + 1, "m_Type": "float", "m_Name": "x", "m_ByteSize": 4, "m_MetaFlag": 0},
        {"m_Level": level + 1, "m_Type": "float", "m_Name": "y", "m_ByteSize": 4, "m_MetaFlag": 0},
        {"m_Level": level + 1, "m_Type": "float", "m_Name": "z", "m_ByteSize": 4, "m_MetaFlag": 0},
    ]


def _bounds(name, level=1):
    out = [{"m_Level": level, "m_Type": "AABB", "m_Name": name, "m_ByteSize": 24, "m_MetaFlag": 0}]
    out += _vector3("m_Center", level + 1)
    out += _vector3("m_Extent", level + 1)
    return out


def _read(obj, header_dicts, extra_fields):
    """check_read=True hard-validates the node list consumes the object's exact byte size."""
    nodes = list(header_dicts)
    for fn in extra_fields:
        nodes += fn
    node = TypeTreeNode.from_list(nodes)
    return obj.read_typetree(nodes=node, check_read=True)


# ---------------------------------------------------------------------------------------------
# Hierarchy / world-transform helpers.
# ---------------------------------------------------------------------------------------------

def _gameobject_of_mb(obj):
    mb = obj.parse_monobehaviour_head()
    return mb.m_GameObject.deref()


def _transform_of_go(go_obj, f):
    got = go_obj.read_typetree()
    for c in got.get("m_Component", []):
        cp = c.get("component", c)
        pid = cp.get("m_PathID", 0)
        if not pid:
            continue
        cobj = f.objects.get(pid)
        if cobj is not None and cobj.type.name == "Transform":
            return cobj
    return None


def _local_trs_matrix(t):
    lp, lr, ls = t["m_LocalPosition"], t["m_LocalRotation"], t["m_LocalScale"]
    x, y, z, w = lr["x"], lr["y"], lr["z"], lr["w"]
    rot = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])
    scale = np.diag([ls["x"], ls["y"], ls["z"]])
    m = np.eye(4)
    m[:3, :3] = rot @ scale
    m[:3, 3] = [lp["x"], lp["y"], lp["z"]]
    return m


def _world_matrix_of_transform(tobj, max_depth=64):
    """Proper hierarchical TRS composition (position/rotation/scale, respecting parent rotation),
    unlike census.world_transform_of_transform's own simplified position/rotation-only walk (its
    own docstring: 'ignores parent rotation skew') -- needed here because a DryPlane's world SCALE
    (its actual roof-panel footprint size) must round-trip exactly, not just its position."""
    chain = []
    cur = tobj
    depth = 0
    while cur is not None and depth < max_depth:
        t = cur.read_typetree()
        chain.append(_local_trs_matrix(t))
        father = t.get("m_Father", {})
        fpid = father.get("m_PathID", 0)
        cur = cur.assets_file.objects.get(fpid) if fpid and father.get("m_FileID", 0) == 0 else None
        depth += 1
    m = np.eye(4)
    for local in reversed(chain):  # root -> leaf
        m = m @ local
    return m


def _root_name_of_transform(tobj, max_depth=64):
    """Name of the top-most ancestor GameObject of a Transform (the WeatherObstacle root a DryPlane hangs
    under). Bay5 finding 2026-09-25: Arena's 669 DryPlanes all hang under a root called
    'Lighthouse_DryPlanes' -- the WeatherObstacle prefab of EFT's Lighthouse map copied into the Arena
    scene, spanning x -383..454 / z -973..566 while Bay5 plays in x -60..160 / z -40..120."""
    cur = tobj
    depth = 0
    last = None
    while cur is not None and depth < max_depth:
        last = cur
        t = cur.read_typetree()
        father = t.get("m_Father", {})
        fpid = father.get("m_PathID", 0)
        cur = cur.assets_file.objects.get(fpid) if fpid and father.get("m_FileID", 0) == 0 else None
        depth += 1
    if last is None:
        return None
    t = last.read_typetree()
    go = last.assets_file.objects.get(t.get("m_GameObject", {}).get("m_PathID", 0))
    return _go_name(go) if go is not None else None


def plausible_dry_planes(dry_planes, indoor_triggers, pad=3.0):
    """Keep only DryPlanes that can roof one of THIS map's indoor rooms: the plane's world xz centre lies
    inside some IndoorTrigger's xz Bounds (padded by `pad` metres) and the plane is at or above that
    trigger's floor. Returns (kept, dropped). A plane hundreds of metres from every room protects nothing;
    Bay5's Lighthouse leftovers (see _root_name_of_transform) fail this for all but a handful of chance hits."""
    kept, dropped = [], []
    for dp in dry_planes:
        px, py, pz = dp["world_position"]
        ok = False
        for it in indoor_triggers:
            cx, cy, cz = it["bounds_center"]
            sx, sy, sz = it["bounds_size"]
            if abs(px - cx) <= sx / 2 + pad and abs(pz - cz) <= sz / 2 + pad and py >= cy - sy / 2 - 0.5:
                ok = True
                break
        (kept if ok else dropped).append(dp)
    return kept, dropped


def select_dry_planes(dry_planes, indoor_triggers, pad=3.0):
    """plausible_dry_planes() plus a whole-set rule: when MOST planes roof none of this map's rooms the set is
    foreign (Bay5: 667 of 669 Lighthouse quads fail, 2 hit a room by chance) and is dropped entirely, so no
    chance hit ends up as a fake roof. Returns (kept, dropped, reason)."""
    kept, dropped = plausible_dry_planes(dry_planes, indoor_triggers, pad)
    if dry_planes and len(dropped) > len(kept):
        return [], list(dry_planes), f"foreign set: {len(dropped)} of {len(dry_planes)} planes roof no room"
    return kept, dropped, ("" if not dropped else f"{len(dropped)} stray plane(s) dropped")


def _decompose(m):
    pos = m[:3, 3].tolist()
    sx = float(np.linalg.norm(m[:3, 0]))
    sy = float(np.linalg.norm(m[:3, 1]))
    sz = float(np.linalg.norm(m[:3, 2]))
    r = np.column_stack([
        m[:3, 0] / sx if sx else m[:3, 0],
        m[:3, 1] / sy if sy else m[:3, 1],
        m[:3, 2] / sz if sz else m[:3, 2],
    ])
    # standard rotation-matrix -> quaternion (xyzw), Shepperd's method via trace.
    tr = r[0, 0] + r[1, 1] + r[2, 2]
    if tr > 0:
        s = 0.5 / (tr + 1.0) ** 0.5
        qw = 0.25 / s
        qx = (r[2, 1] - r[1, 2]) * s
        qy = (r[0, 2] - r[2, 0]) * s
        qz = (r[1, 0] - r[0, 1]) * s
    else:
        i = int(np.argmax([r[0, 0], r[1, 1], r[2, 2]]))
        if i == 0:
            s = 2.0 * (1.0 + r[0, 0] - r[1, 1] - r[2, 2]) ** 0.5
            qw = (r[2, 1] - r[1, 2]) / s
            qx = 0.25 * s
            qy = (r[0, 1] + r[1, 0]) / s
            qz = (r[0, 2] + r[2, 0]) / s
        elif i == 1:
            s = 2.0 * (1.0 + r[1, 1] - r[0, 0] - r[2, 2]) ** 0.5
            qw = (r[0, 2] - r[2, 0]) / s
            qx = (r[0, 1] + r[1, 0]) / s
            qy = 0.25 * s
            qz = (r[1, 2] + r[2, 1]) / s
        else:
            s = 2.0 * (1.0 + r[2, 2] - r[0, 0] - r[1, 1]) ** 0.5
            qw = (r[1, 0] - r[0, 1]) / s
            qx = (r[0, 2] + r[2, 0]) / s
            qy = (r[1, 2] + r[2, 1]) / s
            qz = 0.25 * s
    return pos, [float(qx), float(qy), float(qz), float(qw)], [sx, sy, sz]


def _go_name(go_obj):
    return go_obj.read_typetree().get("m_Name", "")


# ---------------------------------------------------------------------------------------------
# Export.
# ---------------------------------------------------------------------------------------------

def export(source, package, map_key, scripts_scene_index=None):
    """Read map_key's Scripts scene and write <package>/raincover/raincover.json. Returns the
    manifest.native_features['raincover'] summary entry. Fails closed (raises) on anything that
    would silently ship wrong/half-authored cover data -- same convention as extract_bay5_audio.export.

    scripts_scene_index: BuildSettings index of the map's Scripts scene. Plan-driven maps pass it
    from their source plan's ``scripts`` scene role (source_plan.scene_for_role), so no table entry
    is needed; omitted, the legacy Equator/Bay5 SCRIPTS_SCENE_INDEX table applies (PLAN-AIRPIT items
    2 and 4)."""
    if scripts_scene_index is not None:
        if type(scripts_scene_index) is not int or scripts_scene_index < 0:
            raise ValueError(f"raincover: invalid Scripts scene index {scripts_scene_index!r} for {map_key!r}")
        idx = scripts_scene_index
    elif map_key not in SCRIPTS_SCENE_INDEX:
        raise ValueError(f"raincover: unsupported map_key {map_key!r}")
    else:
        idx = SCRIPTS_SCENE_INDEX[map_key]

    paths = common.scene_files({idx: "Scripts"}, base=str(source))
    if not paths:
        raise ValueError(f"raincover: no Scripts scene files found for {map_key} (level{idx}) under {source}")
    env = UnityPy.load(*paths)

    f = None
    for fname, ff in env.files.items():
        if fname.endswith(f"level{idx}") and hasattr(ff, "objects"):
            f = ff
            break
    if f is None:
        raise ValueError(f"raincover: could not find level{idx} file object container for {map_key}")

    header_dicts = _mb_header_dicts(f.version)
    monoscript_pool = census.build_monoscript_pool(env)

    by_class = {c: [] for c in TARGET_CLASSES}
    for obj in f.objects.values():
        if obj.type.name != "MonoBehaviour":
            continue
        cls_name, status = census.resolve_script_name(obj, monoscript_pool)
        if status != "ok" or not cls_name:
            continue
        short = cls_name.rsplit(".", 1)[-1]
        if short in by_class:
            by_class[short].append(obj)

    # ---- DryPlane ----
    dry_planes = []
    for obj in by_class["DryPlane"]:
        t = _read(obj, header_dicts, [_pptr("Mesh")])
        go = _gameobject_of_mb(obj)
        tf = _transform_of_go(go, f)
        if tf is None:
            raise ValueError(f"raincover: {map_key} DryPlane path_id={obj.path_id} has no Transform sibling")
        # Fail-closed cross-check (see module docstring): every DryPlane must share the exact
        # same Mesh PPtr as its own sibling MeshFilter.m_Mesh -- the spike's proof that all 669
        # Bay5 instances use one shared built-in Quad, re-run here against THIS install's data.
        got = go.read_typetree()
        mesh_ptr = t["Mesh"]
        gt_mesh = None
        for c in got.get("m_Component", []):
            cp = c.get("component", c)
            pid = cp.get("m_PathID", 0)
            cobj = f.objects.get(pid) if pid else None
            if cobj is not None and cobj.type.name == "MeshFilter":
                gt_mesh = cobj.read_typetree().get("m_Mesh")
                break
        if gt_mesh is None or mesh_ptr["m_FileID"] != gt_mesh["m_FileID"] or mesh_ptr["m_PathID"] != gt_mesh["m_PathID"]:
            raise ValueError(f"raincover: {map_key} DryPlane path_id={obj.path_id} Mesh PPtr disagrees with its "
                              "own MeshFilter.m_Mesh -- not the single shared built-in Quad this module assumes; "
                              "re-run the rain-cover spike before trusting this package.")
        pos, rot, scale = _decompose(_world_matrix_of_transform(tf))
        dry_planes.append({"name": _go_name(go), "world_position": pos, "world_rotation": rot, "world_scale": scale,
                           "root": _root_name_of_transform(tf)})

    # ---- IndoorTrigger ----
    indoor_fields = [_bool("IsBunker"), _float("FadeTime"), _float("BunkerDepth"), _float("BunkerLowPass"),
                     _float("ExposureSpeed"), _float("ExposureOffset"), _float("RainVolume"),
                     _bounds("Bounds"), _int("AreaAutoId")]
    indoor_triggers = []
    bounds_transform_mismatches = 0
    for obj in by_class["IndoorTrigger"]:
        t = _read(obj, header_dicts, indoor_fields)
        go = _gameobject_of_mb(obj)
        tf = _transform_of_go(go, f)
        if tf is None:
            raise ValueError(f"raincover: {map_key} IndoorTrigger path_id={obj.path_id} has no Transform sibling")
        pos, rot, _scale = _decompose(_world_matrix_of_transform(tf))
        b = t["Bounds"]
        center = [b["m_Center"]["x"], b["m_Center"]["y"], b["m_Center"]["z"]]
        size = [2.0 * b["m_Extent"]["x"], 2.0 * b["m_Extent"]["y"], 2.0 * b["m_Extent"]["z"]]
        if max(abs(center[i] - pos[i]) for i in range(3)) > 1.0:
            # Ground truth per the module docstring: the BAKED Bounds (already recorded above) is
            # what Check(pos) actually uses at runtime; this is purely an informational counter for
            # the progress doc, never a reason to prefer the live-recomputed position instead.
            bounds_transform_mismatches += 1
            # Two different measures, both labelled: the gate above is the largest single-axis offset,
            # the Euclidean distance is what "how far apart" means (AirPit Bus trigger: 125.76 m on the
            # largest axis, 155.2 m apart).
            largest_axis = max(abs(center[i] - pos[i]) for i in range(3))
            distance = sum((center[i] - pos[i]) ** 2 for i in range(3)) ** 0.5
            print(f"[extract_raincover] {map_key}: IndoorTrigger path_id={obj.path_id} name={_go_name(go)!r} "
                  f"baked Bounds.center {center} vs live Transform-recomputed position {pos} disagree by "
                  f"{largest_axis:.2f}m on the largest axis ({distance:.2f}m apart) -- using the baked Bounds "
                  "(see docstring).",
                  file=sys.stderr)
        indoor_triggers.append({
            "name": _go_name(go), "bounds_center": center, "bounds_size": size,
            "is_bunker": bool(t["IsBunker"]), "fade_time": t["FadeTime"], "bunker_depth": t["BunkerDepth"],
            "bunker_low_pass": t["BunkerLowPass"], "exposure_speed": t["ExposureSpeed"],
            "exposure_offset": t["ExposureOffset"], "rain_volume": t["RainVolume"],
            "area_auto_id": t["AreaAutoId"], "world_position": pos, "world_rotation": rot,
        })

    # 2026-09-25: Arena's Bay5 DryPlanes are Lighthouse leftovers (root 'Lighthouse_DryPlanes', see
    # _root_name_of_transform) -- only planes that can actually roof one of this map's IndoorTrigger rooms
    # are authored; the WeatherObstacle root is only worth authoring when at least one plane survives.
    dry_plane_roots = sorted({dp.get("root") or "?" for dp in dry_planes})
    dry_planes, dropped_dry_planes, drop_reason = select_dry_planes(dry_planes, indoor_triggers)
    weather_obstacle = len(by_class["WeatherObstacle"]) > 0 and len(dry_planes) > 0
    if dropped_dry_planes:
        print(f"[extract_raincover] {map_key}: dropped {len(dropped_dry_planes)} DryPlane(s) ({drop_reason}; "
              f"{len(indoor_triggers)} IndoorTrigger rooms; roots: {dry_plane_roots}); kept {len(dry_planes)}",
              file=sys.stderr)
    # Established plan fact (docs/PLAN-TWO-MAPS.md §6): Equator authors zero WeatherObstacle/DryPlane
    # in Arena's own data; never invent one for Equator. Fail closed if a future Arena patch changes this
    # without the plan/importer being revisited.
    if map_key == "arena_equator" and (weather_obstacle or dry_planes):
        raise ValueError("raincover: arena_equator unexpectedly authors DryPlane/WeatherObstacle data in this "
                          "install -- docs/PLAN-TWO-MAPS.md §6 established Equator has none; re-verify before "
                          "changing the importer to author one.")
    if map_key == "arena_bay5" and dry_planes and not weather_obstacle:
        raise ValueError("raincover: arena_bay5 has DryPlane instances but no WeatherObstacle root -- "
                          "CombineMesh()'s GetComponentsInChildren<DryPlane>() needs one to attach to.")
    if not indoor_triggers:
        raise ValueError(f"raincover: {map_key} has zero IndoorTrigger instances -- refusing to write an "
                          "empty package (EnvironmentManager.InitIndoors would find no TriggerGroup content).")

    out = {
        "schema_version": SCHEMA_VERSION,
        "indoor_triggers": indoor_triggers,
        "dry_planes": dry_planes,
        "weather_obstacle": weather_obstacle,
        "dry_planes_dropped": len(dropped_dry_planes),
        "dry_plane_roots": dry_plane_roots,
    }
    raincover_dir = os.path.join(package, "raincover")
    os.makedirs(raincover_dir, exist_ok=True)
    import json
    with open(os.path.join(raincover_dir, "raincover.json"), "w", encoding="utf-8") as fh:
        json.dump(out, fh, indent=2, sort_keys=True, ensure_ascii=False)

    print(f"[extract_raincover] {map_key}: wrote {len(indoor_triggers)} IndoorTrigger, {len(dry_planes)} DryPlane "
          f"({len(dropped_dry_planes)} dropped as implausible), weather_obstacle={weather_obstacle}, "
          f"{bounds_transform_mismatches} baked-vs-live Bounds mismatch(es)", file=sys.stderr)

    return {
        "schema_version": SCHEMA_VERSION,
        "source_scene": f"Scripts (level{idx})",
        "indoor_trigger_count": len(indoor_triggers),
        "dry_planes_dropped": len(dropped_dry_planes),
        "dry_plane_count": len(dry_planes),
        "weather_obstacle": weather_obstacle,
        "bounds_transform_mismatches": bounds_transform_mismatches,
    }


# ---------------------------------------------------------------------------------------------
# Plan-driven entry points (PLAN-AIRPIT item 4).
# ---------------------------------------------------------------------------------------------

def scripts_scene_index_for_plan(plan):
    """BuildSettings index of the plan's Scripts scene, taken from its ``scripts`` scene role.
    Raises source_plan.PlanError for a plan that declares no such role (no table fallback)."""
    return scene_for_role(plan, "scripts")["index"]


def export_for_plan(source, package, plan):
    """export() for a plan-driven map: the Scripts scene is the plan's ``scripts`` role."""
    return export(source, package, plan["key"], scripts_scene_index=scripts_scene_index_for_plan(plan))


def build_parser():
    import argparse
    parser = argparse.ArgumentParser(description="Extract Arena rain-cover geometry (IndoorTrigger, DryPlane, "
                                                 "WeatherObstacle) from a map's Scripts scene.")
    which = parser.add_mutually_exclusive_group(required=True)
    which.add_argument("--plan", type=str, help="source plan JSON; the Scripts scene is its `scripts` role")
    which.add_argument("--map", choices=sorted(SCRIPTS_SCENE_INDEX),
                       help="legacy table map (Equator/Bay5); plan-driven maps use --plan")
    parser.add_argument("--source", default=DEFAULT_SOURCE, help="Arena EscapeFromTarkovArena_Data dir (read-only)")
    parser.add_argument("--output", required=True,
                        help="package directory; raincover/raincover.json is written inside it")
    return parser


def main(argv=None):
    from pathlib import Path
    import json
    from common_ids import HardExtractError
    from source_plan import PlanError, load_plan, read_build_scenes, verify_scene_mapping

    args = build_parser().parse_args(argv)
    source, output = Path(args.source).resolve(), Path(args.output).resolve()
    try:
        if output == source or source in output.parents or output in source.parents:
            raise PlanError("raincover output must be separate from the read-only Arena source tree")
        if args.plan:
            plan = load_plan(Path(args.plan))
            # Fail closed if an Arena update moved a scene the plan names (roles included).
            verify_scene_mapping(plan, read_build_scenes(source), source)
            summary = export_for_plan(source, output, plan)
        else:
            summary = export(source, output, args.map)
    except (PlanError, HardExtractError, ValueError, OSError) as exc:
        print(f"[extract_raincover] FAILED: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(summary, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
