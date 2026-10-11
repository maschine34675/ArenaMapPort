"""Read-only probe: how does Arena hide (or not hide) base-scene props in a game mode?

Why. docs/PLAN-AIRPIT.md item 7: Arena's own NavMesh walks through ~190 prop instances that the port
bakes as solids. If a mode toggler deactivated them in TeamFight, the port could do the same
(`opened_barriers`); if not, the right policy is different. This probe looks for the toggler in the
live Arena install, without writing anything:

  1. The map preset bundle: which scenes each game mode loads (`GameModes -> ScenePreset`).
  2. The MonoBehaviour class census (analysis/scripts/census.resolve_script_name) of the Scripts scene
     and of the mode scenes, flagging class names that look like object togglers.
  3. Every Unity.AI.Navigation.NavMeshSurface of the AI scenes, decoded: which colliders Arena's own
     bake collected (layer mask, geometry mode, volume) - the other half of "what Arena's mesh saw".
  4. Whether the IL2CPP metadata is searchable for identifier strings (it is not on 0.6.0).

Result on AirPit (Arena 0.6.0.47752), recorded in docs/progress/airpit-release.md: no toggler class in
any mode scene, the presets only list scenes, the base scene's own components cannot reference props
across scenes, and the metadata holds only a few thousand readable identifiers. The mechanism is
therefore UNKNOWN; the NavMeshSurface decode shows the bake mask but not why 190 props are ignored.

    python tools/validate/probe_mode_toggler.py [--json-out FILE] [--scenes 36 324 ...]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import struct
import sys
from collections import Counter
from pathlib import Path

DEFAULT_DATA = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
REPO = Path(__file__).resolve().parents[2]
TOGGLER_HINT = re.compile(r"variant|toggle|switch|activat|deactivat|enabler|disabler|visib|hider|show|hide|"
                          r"mode(?!l)|group(?!ed)|conditional|swap", re.I)
# default scenes: Scripts, ShootOut base, ShootOut Light, DesignStuff, TeamFight, TeamFight_DesignStuff,
# Tournament_DesignStuff, LastHero_DesignStuff, TeamFight_AI, Tournament_AI, LastHero_AI
DEFAULT_SCENES = [36, 324, 325, 40, 29, 28, 114, 139, 33, 116, 140]
NAVMESH_SURFACE_FIELDS = ("agent_type_id", "collect_objects", "size", "center", "layer_mask", "use_geometry",
                          "default_area", "generate_links", "ignore_agent", "ignore_obstacle", "override_tile_size",
                          "tile_size", "override_voxel_size", "voxel_size", "min_region_area", "navmesh_data", "build_height_mesh")
COLLECT_OBJECTS = {0: "All", 1: "Volume", 2: "Children"}
USE_GEOMETRY = {0: "RenderMeshes", 1: "PhysicsColliders"}


class ProbeError(ValueError):
    pass


# ---------------------------------------------------------------------------------------------
# pure decoders (unit-tested on synthetic bytes)
# ---------------------------------------------------------------------------------------------

def monobehaviour_body_offset(raw: bytes) -> int:
    """Offset of the first script-specific field: m_GameObject (12), m_Enabled (+pad to 4), m_Script (12),
    m_Name (length-prefixed string aligned to 4)."""
    off = 12 + 4 + 12
    (n,) = struct.unpack_from("<i", raw, off)
    if n < 0 or off + 4 + n > len(raw):
        raise ProbeError("bad m_Name length")
    return (off + 4 + n + 3) & ~3


def decode_navmesh_surface(raw: bytes) -> dict:
    """Unity.AI.Navigation.NavMeshSurface (2.x serialized layout, 23 four-byte fields after the head).

    Layout confirmed on AirPit's three AI scenes: agentTypeID, collectObjects, size(3), center(3),
    layerMask, useGeometry, defaultArea, generateLinks, ignoreNavMeshAgent, ignoreNavMeshObstacle,
    overrideTileSize, tileSize, overrideVoxelSize, voxelSize(float), minRegionArea(float), NavMeshData
    PPtr (int fileID + int64 pathID), buildHeightMesh. Bools occupy four bytes each.
    """
    off = monobehaviour_body_offset(raw)
    if len(raw) - off != 92:
        raise ProbeError(f"unexpected NavMeshSurface body of {len(raw) - off} bytes (expected 92)")
    ints = struct.unpack_from("<23i", raw, off)
    flt = struct.unpack_from("<23f", raw, off)
    mask = ints[8] & 0xFFFFFFFF
    return {
        "agent_type_id": ints[0], "collect_objects": COLLECT_OBJECTS.get(ints[1], ints[1]),
        "size": [flt[2], flt[3], flt[4]], "center": [flt[5], flt[6], flt[7]],
        "layer_mask": mask, "layer_mask_bits": [i for i in range(32) if mask >> i & 1],
        "use_geometry": USE_GEOMETRY.get(ints[9], ints[9]), "default_area": ints[10], "generate_links": bool(ints[11]),
        "ignore_agent": bool(ints[12]), "ignore_obstacle": bool(ints[13]), "override_tile_size": bool(ints[14]),
        "tile_size": ints[15], "override_voxel_size": bool(ints[16]), "voxel_size": flt[17], "min_region_area": flt[18],
        "navmesh_data": {"file_id": ints[19], "path_id": struct.unpack_from("<q", raw, off + 20 * 4)[0]},
        "build_height_mesh": bool(ints[22]),
    }


def toggler_candidates(census: dict) -> dict:
    """Class names of a census that look like object togglers (name hints only)."""
    return {name: n for name, n in census.items() if name and TOGGLER_HINT.search(name.rsplit(".", 1)[-1])}


def metadata_identifier_count(data: bytes) -> int:
    """Distinct readable NUL-terminated identifier-like strings; the unobfuscated metadata of a game of
    this size holds hundreds of thousands, 0.6.0 holds a few thousand."""
    return len({m.group() for m in re.finditer(rb"[A-Za-z_][A-Za-z0-9_.`<>]{2,119}(?=\x00)", data)})


# ---------------------------------------------------------------------------------------------
# live reads
# ---------------------------------------------------------------------------------------------

def _census_module():
    sys.path.insert(0, str(REPO / "analysis" / "scripts"))
    import census
    return census


def build_scene_paths(data_root) -> list[str]:
    import UnityPy
    for name in ("globalgamemanagers", "globalgamemanagers.assets"):
        path = f"{data_root}/{name}"
        if os.path.exists(path):
            env = UnityPy.load(path)
            for obj in env.objects:
                if obj.type.name == "BuildSettings":
                    return list(obj.read_typetree().get("scenes") or [])
    raise ProbeError("BuildSettings not found")


def layer_names(data_root) -> dict:
    import UnityPy
    env = UnityPy.load(f"{data_root}/globalgamemanagers")
    for obj in env.objects:
        if obj.type.name == "TagManager":
            return {i: n for i, n in enumerate(obj.read_typetree().get("layers") or []) if n}
    return {}


def read_preset(bundle_path) -> list[dict]:
    """[{name, active_scene, server_name, scenes:[paths], modes:{mode: preset_path_id}}] per ScenesPreset."""
    import UnityPy
    env = UnityPy.load(str(bundle_path))
    out = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        t = obj.read_typetree()
        if "_scenesResourceKeys" not in t:
            continue
        out.append({
            "name": t.get("m_Name"), "path_id": obj.path_id, "active_scene": t.get("_activeSceneName"),
            "scenes": [k["path"].replace("Assets/Content/Locations/", "") for k in t.get("_scenesResourceKeys", [])],
            "modes": {g["GameModeName"]: g["ScenePreset"]["m_PathID"] for g in t.get("GameModes", [])},
        })
    return out


def scene_census(data_root, index: int) -> dict:
    """{'classes': Counter of MonoBehaviour class names, 'surfaces': decoded NavMeshSurfaces,
    'objects': Counter of object types} for level<index>/sharedassets<index>.assets (read-only)."""
    import UnityPy
    census = _census_module()
    paths = [p for p in (f"{data_root}/level{index}", f"{data_root}/sharedassets{index}.assets",
                         f"{data_root}/globalgamemanagers.assets", f"{data_root}/resources.assets") if os.path.exists(p)]
    env = UnityPy.load(*paths)
    classes, types, surfaces = Counter(), Counter(), []
    level_key = f"{data_root}/level{index}"
    for fname, f in list(env.files.items()):
        base = os.path.basename(fname)
        if not hasattr(f, "objects") or not (base.startswith("level") or base.startswith("sharedassets")):
            continue
        for obj in list(f.objects.values()):
            types[obj.type.name] += 1
            if obj.type.name != "MonoBehaviour":
                continue
            name, status = census.resolve_script_name(obj)
            classes[name or status] += 1
            if name and name.endswith("NavMeshSurface"):
                try:
                    surface = decode_navmesh_surface(obj.get_raw_data())
                    head = obj.parse_monobehaviour_head()
                    go = f.objects[head.m_GameObject.path_id].read_typetree()
                    surface["game_object"] = go.get("m_Name")
                    surfaces.append(surface)
                except Exception as error:               # report, never guess
                    surfaces.append({"error": str(error)})
    return {"index": index, "classes": dict(classes.most_common()), "surfaces": surfaces, "objects": dict(types.most_common(12))}


def metadata_readability(data_root) -> dict:
    path = Path(data_root) / "il2cpp_data" / "Metadata" / "global-metadata.dat"
    if not path.exists():
        return {"present": False}
    data = path.read_bytes()
    n = metadata_identifier_count(data)
    return {"present": True, "bytes": len(data), "readable_identifiers": n, "contains_VARIANT": b"VARIANT" in data}


def run_probe(data_root=DEFAULT_DATA, scenes=None, preset="arena_airpit_preset.bundle", log=print) -> dict:
    scenes = scenes or DEFAULT_SCENES
    result = {"data_root": str(data_root)}
    paths = build_scene_paths(data_root)
    result["scene_names"] = {i: paths[i].replace("Assets/Content/Locations/", "") for i in scenes if i < len(paths)}
    layers = layer_names(data_root)
    result["layers"] = layers
    result["presets"] = read_preset(Path(data_root) / "StreamingAssets" / "Windows" / "maps" / preset)
    result["scenes"] = {}
    for i in scenes:
        log(f"[probe] census of level{i} {result['scene_names'].get(i)}")
        c = scene_census(data_root, i)
        c["toggler_candidates"] = toggler_candidates(c["classes"])
        for s in c["surfaces"]:
            if "layer_mask_bits" in s:
                s["layer_mask_names"] = [layers.get(b, str(b)) for b in s["layer_mask_bits"]]
        result["scenes"][i] = c
    result["metadata"] = metadata_readability(data_root)
    return result


def format_probe(result) -> str:
    out = []
    out.append("== presets")
    for p in result["presets"]:
        out.append(f"  {p['name']} (active {p['active_scene']!r}): {len(p['scenes'])} scenes; modes {sorted(p['modes'])}")
        for s in p["scenes"]:
            out.append(f"      {s}")
    out.append("== scenes")
    for i, c in result["scenes"].items():
        out.append(f"  level{i} {result['scene_names'].get(int(i)) if isinstance(i, int) else ''}: {sum(c['classes'].values())} MonoBehaviours, "
                   f"{len(c['classes'])} classes; toggler-like names: {c['toggler_candidates'] or 'none'}")
        for s in c["surfaces"]:
            out.append(f"      NavMeshSurface {s}")
    out.append(f"== IL2CPP metadata: {result['metadata']}")
    return "\n".join(out)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--data", default=DEFAULT_DATA)
    ap.add_argument("--scenes", type=int, nargs="*", default=None)
    ap.add_argument("--preset", default="arena_airpit_preset.bundle")
    ap.add_argument("--json-out")
    args = ap.parse_args(argv)
    result = run_probe(args.data, args.scenes, args.preset)
    print(format_probe(result))
    if args.json_out:
        Path(args.json_out).parent.mkdir(parents=True, exist_ok=True)
        Path(args.json_out).write_text(json.dumps(result, indent=1, default=str), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
