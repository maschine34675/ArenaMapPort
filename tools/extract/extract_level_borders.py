"""Arena's static level-border cubes, read from a scene the package does not import.

Arena authors its play-space boundary as invisible-wall cubes (BoxCollider + MeshFilter + MeshRenderer, layer 29)
under one root of its Scripts scene. The Scripts scene is excluded from every package (the port authors its own
Scripts scene under the same name, so a package scene file of that name would collide in the Unity importer), and
``source_plan`` refuses a scene that is both imported (partial) and excluded with a role. This tool is the fallback of
docs/PLAN-YARD.md item 4: it reads the cubes of the plan's ``scripts`` scene straight from the Arena install (read
only) and writes their world-space boxes as a sidecar, ``tools/maps/<stem>.borders.json`` (positions and sizes only).

Each row: path, name, layer, active, the collider's ``enabled`` / ``is_trigger``, the world box (``center``,
``size`` = collider size times the axis lengths of the world matrix, ``rotation`` quaternion x y z w) and its world
AABB. A parent chain with non-uniform scale above a rotated child can shear a box; such a row is flagged ``sheared``
and the run fails unless ``--allow-shear`` is given (Yard has none).

    python tools/extract/extract_level_borders.py --plan tools/extract/plans/yard.json \
        --root Arena_Yard_LevelBorders --out tools/maps/yard.borders.json
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))

DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
SCHEMA = 1
BOX_TYPE = "BoxCollider"


def quaternion_from_matrix(m):
    """Unit quaternion (x, y, z, w) of an orthonormal row-major 3x3 matrix."""
    trace = m[0] + m[4] + m[8]
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        q = ((m[7] - m[5]) / s, (m[2] - m[6]) / s, (m[3] - m[1]) / s, 0.25 * s)
    elif m[0] > m[4] and m[0] > m[8]:
        s = math.sqrt(1.0 + m[0] - m[4] - m[8]) * 2
        q = (0.25 * s, (m[1] + m[3]) / s, (m[2] + m[6]) / s, (m[7] - m[5]) / s)
    elif m[4] > m[8]:
        s = math.sqrt(1.0 + m[4] - m[0] - m[8]) * 2
        q = ((m[1] + m[3]) / s, 0.25 * s, (m[5] + m[7]) / s, (m[2] - m[6]) / s)
    else:
        s = math.sqrt(1.0 + m[8] - m[0] - m[4]) * 2
        q = ((m[2] + m[6]) / s, (m[5] + m[7]) / s, 0.25 * s, (m[3] - m[1]) / s)
    norm = math.sqrt(sum(c * c for c in q))
    return tuple(c / norm for c in q)


def world_box(linear, position, center, size):
    """World centre, per-axis size, rotation quaternion, AABB and the shear residual of a box collider.

    ``linear`` is the row-major 3x3 world matrix (rotation times scale along the parent chain), ``position`` the world
    position of the GameObject; ``center`` and ``size`` are the collider's local values.
    """
    def apply(v):
        return tuple(linear[3 * i] * v[0] + linear[3 * i + 1] * v[1] + linear[3 * i + 2] * v[2] for i in range(3))

    columns = [(linear[0 + j], linear[3 + j], linear[6 + j]) for j in range(3)]
    lengths = [math.sqrt(sum(c * c for c in col)) for col in columns]
    world_center = tuple(position[i] + apply(center)[i] for i in range(3))
    world_size = tuple(abs(size[j]) * lengths[j] for j in range(3))
    unit = [tuple(c / lengths[j] for c in columns[j]) for j in range(3)]
    shear = max(abs(sum(a * b for a, b in zip(unit[i], unit[j]))) for i, j in ((0, 1), (0, 2), (1, 2)))
    rotation_matrix = (unit[0][0], unit[1][0], unit[2][0], unit[0][1], unit[1][1], unit[2][1],
                       unit[0][2], unit[1][2], unit[2][2])
    low, high = [math.inf] * 3, [-math.inf] * 3
    for sx in (-.5, .5):
        for sy in (-.5, .5):
            for sz in (-.5, .5):
                corner = apply((center[0] + sx * size[0], center[1] + sy * size[1], center[2] + sz * size[2]))
                for i in range(3):
                    value = position[i] + corner[i]
                    low[i], high[i] = min(low[i], value), max(high[i], value)
    return {"center": world_center, "size": world_size, "rotation": quaternion_from_matrix(rotation_matrix),
            "aabb": (tuple(low), tuple(high)), "shear": shear}


def _round(values, digits=4):
    return [round(v + 0.0, digits) for v in values]


def read_borders(plan_path: Path, source: Path, root_path: str, allow_shear: bool = False) -> dict:
    import UnityPy
    import extract_equator as EQ
    import scene_select as SS
    from source_plan import load_plan, scene_for_role

    plan = load_plan(plan_path)
    record = scene_for_role(plan, "scripts")
    index = record["index"]
    level = (source / f"level{index}").as_posix()
    env = UnityPy.load(level, (source / f"sharedassets{index}.assets").as_posix(),
                       (source / "globalgamemanagers.assets").as_posix())
    file = env.files[level]
    go_map, tf_map, _ = EQ.build_scene_graph(file)
    graph = SS.build_graph(file)
    roots = graph.by_path.get(root_path, [])
    if len(roots) != 1:
        raise SystemExit(f"{root_path!r} resolves to {len(roots)} objects in level{index}, expected exactly one")
    identity = (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)
    # world matrix and position of the root's parents (the root of Yard's borders has none)
    chain = []
    cursor = graph.nodes[roots[0]].parent
    while cursor is not None:
        chain.append(cursor)
        cursor = graph.nodes[cursor].parent
    linear, position = identity, (0.0, 0.0, 0.0)
    for tf_id in reversed(chain):
        tf = tf_map[tf_id]
        offset = SS._apply(linear, tf["pos"])
        position = tuple(position[i] + offset[i] for i in range(3))
        linear = SS._mul(linear, SS._local_linear(tf["rot"], tf["scale"]))
    rows, stack = [], [(roots[0], linear, position)]
    while stack:
        tf_id, parent_linear, parent_pos = stack.pop()
        tf, node = tf_map[tf_id], graph.nodes[tf_id]
        offset = SS._apply(parent_linear, tf["pos"])
        pos = tuple(parent_pos[i] + offset[i] for i in range(3))
        lin = SS._mul(parent_linear, SS._local_linear(tf["rot"], tf["scale"]))
        for child in node.children:
            stack.append((child, lin, pos))
        for component_id in node.components:
            reader = file.objects[component_id]
            if reader.type.name != BOX_TYPE:
                continue
            tree = reader.read_typetree()
            box = world_box(lin, pos, list(tree["m_Center"].values()), list(tree["m_Size"].values()))
            if box["shear"] > 1e-4 and not allow_shear:
                raise SystemExit(f"{node.path}: the world matrix shears the box (residual {box['shear']:.3g})")
            rows.append({"path": node.path, "layer": node.layer, "active": bool(node.active),
                         "enabled": bool(tree.get("m_Enabled", 1)), "is_trigger": bool(tree.get("m_IsTrigger", 0)),
                         "center": _round(box["center"]), "size": _round(box["size"]),
                         "rotation": _round(box["rotation"], 6),
                         "aabb": [_round(box["aabb"][0]), _round(box["aabb"][1])]})
    rows.sort(key=lambda r: r["path"])
    paths = [r["path"] for r in rows]
    if len(set(paths)) != len(paths):
        raise SystemExit("duplicate border paths; the rows are addressed by hierarchy path")
    low = [min(r["aabb"][0][i] for r in rows) for i in range(3)]
    high = [max(r["aabb"][1][i] for r in rows) for i in range(3)]
    return {"schema": SCHEMA, "key": plan["key"], "source_scene": record["name"], "root": root_path,
            "count": len(rows), "bounds": [low, high], "layers": sorted({r["layer"] for r in rows}), "rows": rows}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--source", default=DEFAULT_SOURCE, type=Path)
    parser.add_argument("--root", required=True, help="hierarchy path of the borders root in the scripts scene")
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--allow-shear", action="store_true")
    args = parser.parse_args(argv)
    result = read_borders(args.plan, args.source.resolve(), args.root, args.allow_shear)
    head = {k: v for k, v in result.items() if k != "rows"}
    lines = [json.dumps(head, ensure_ascii=False)[:-1] + ', "rows": [']
    lines += [("  " + json.dumps(r, ensure_ascii=False) + ("," if i < len(result["rows"]) - 1 else ""))
              for i, r in enumerate(result["rows"])]
    lines.append("]}")
    args.out.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"BORDERS_OK {result['key']} {result['count']} boxes layers={result['layers']} bounds={result['bounds']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
