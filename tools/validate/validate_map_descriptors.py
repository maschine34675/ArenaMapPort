#!/usr/bin/env python3
"""Cross-checks the three hand-maintained copies of each map's identity/display fields against
each other, so a future edit to only one of them is caught instead of silently drifting:

  1. tools/maps/<key>.json                                        (Python/Editor-shared source)
  2. ArenaMapPort.Shared/ArenaMapDescriptor.cs's ArenaMapRegistry.<Name>  (C# runtime descriptor)
  3. UnityProject/Assets/ArenaMapPort/Editor/Common/ArenaPaths.cs's EquatorDefault()  (C# Editor
     fallback used when -mapDescriptor is omitted -- Equator only, there is no Bay5 equivalent by
     design: an un-parameterized Editor invocation must always mean Equator)

Equator's own ArenaMapRegistry.Equator entry sources most fields directly off
ArenaEquatorLocation.cs's consts (see that class's own doc comment) rather than repeating them as
literals, so byte-for-byte drift on THOSE fields is already structurally impossible and is instead
covered by ArenaMapPort.Server.Tests/MapDescriptorConsistencyTests.cs. This script's job is the
fields nothing else cross-checks: the ones ArenaMapRegistry.cs writes as its own literals
(InfiltrationName/LocaleName/LocaleDescription/IconX/IconY/DbBaseJsonPath for both maps, plus every
field of Bay5's entry, which has no ArenaEquatorLocation.cs-style C# source of truth to delegate
to), and tools/maps/*.json / ArenaPaths.cs's EquatorDefault() against all of it.

This is a regex-based extractor, not a real C# parser -- it understands exactly the named-argument
`new ArenaMapDescriptor(Field: value, ...)` call-site shape both registry entries use today. If a
future edit reformats that call site into something this script's split_top_level_args() can't
parse, IT WILL SAY SO (a parse failure is a hard error here, never a silent skip) rather than
report a false pass.

Exit code 0 + "OK" summary on success. Exit code 1 with a specific, actionable per-field message on
any mismatch or parse failure, per SPEC.md SS B.WP6's acceptance criterion for validators.

Usage:
    python tools/validate/validate_map_descriptors.py
    python tools/validate/validate_map_descriptors.py --maps-dir tools/maps
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path
from typing import NoReturn

REPO = Path(__file__).resolve().parents[2]
DESCRIPTOR_CS = REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs"
EQUATOR_LOCATION_CS = REPO / "ArenaMapPort.Shared" / "ArenaEquatorLocation.cs"
ARENA_PATHS_CS = REPO / "UnityProject" / "Assets" / "ArenaMapPort" / "Editor" / "Common" / "ArenaPaths.cs"

# JSON field name -> ArenaMapDescriptor.cs named-argument name. IdField/Key etc. line up 1:1;
# a couple of names differ slightly between the snake_case JSON and the PascalCase C# side.
JSON_TO_CS_FIELD = {
    "key": "Key",
    "id_field": "IdField",
    "scripts_scene_name": "ScriptsSceneName",
    "all_scene_names": "AllSceneNames",
    "infiltration_name": "InfiltrationName",
    "locale_name": "LocaleName",
    "locale_description": "LocaleDescription",
    "icon_x": "IconX",
    "icon_y": "IconY",
    "db_base_json_path": "DbBaseJsonPath",
}

def fail(msg: str) -> NoReturn:
    print(f"FAIL: {msg}", file=sys.stderr)
    sys.exit(1)


def load_map(map_path: Path) -> dict:
    return json.loads(map_path.read_text(encoding="utf-8"))


def map_descriptor_paths(maps_dir: Path) -> list[Path]:
    """Select map files by name, leaving malformed descriptors visible to validation.

    Dotted stems (<stem>.bots.json, .exits.json, .doors.json, .opened.json) are map sidecars.
    Both registry and distribution validation must use the same descriptor set.
    """
    return sorted(p for p in maps_dir.glob("*.json") if "." not in p.stem)


def strip_line_comments(s: str) -> str:
    """Removes `// ...` through end-of-line, string-literal-aware, so a trailing comment on the
    same line as a `Field: "value",` argument never gets glued onto the next argument fragment."""
    out, in_str, i = [], False, 0
    while i < len(s):
        c = s[i]
        if in_str:
            out.append(c)
            if c == "\\" and i + 1 < len(s):
                i += 1
                out.append(s[i])
            elif c == '"':
                in_str = False
        elif c == '"':
            in_str = True
            out.append(c)
        elif c == "/" and i + 1 < len(s) and s[i + 1] == "/":
            while i < len(s) and s[i] != "\n":
                i += 1
            continue
        else:
            out.append(c)
        i += 1
    return "".join(out)


def split_top_level_args(s: str) -> list[str]:
    """Splits a C# argument-list body on top-level commas (depth 0), respecting string literals
    and (),[],{} nesting so a comma inside `new[] { "a", "b" }` or a quoted string never splits."""
    parts, cur, depth, in_str = [], "", 0, False
    i = 0
    while i < len(s):
        c = s[i]
        if in_str:
            cur += c
            if c == "\\" and i + 1 < len(s):
                i += 1
                cur += s[i]
            elif c == '"':
                in_str = False
        elif c == '"':
            in_str = True
            cur += c
        elif c in "([{":
            depth += 1
            cur += c
        elif c in ")]}":
            depth -= 1
            cur += c
        elif c == "," and depth == 0:
            parts.append(cur)
            cur = ""
        else:
            cur += c
        i += 1
    if cur.strip():
        parts.append(cur)
    return parts


def parse_lamp_fill_literal(v: str) -> dict:
    """LampFillRepairs: `new ArenaLampFillDescriptor("template path", y, new[] { x0, z0, x1, z1, ... })`. The flat cell
    array is paired up as the sidecar's "lamp_fill" object spells it: {template, y, cells [[x, z], ...]}. An odd
    number of values is a hard error (a cell without a z)."""
    inner = v[len("new ArenaLampFillDescriptor("):].rstrip()
    if not inner.endswith(")"):
        fail(f"could not parse lamp fill literal: {v[:80]!r}")
    values = [parse_cs_value(x) for x in split_top_level_args(inner[:-1])]
    if len(values) != 3 or not isinstance(values[0], str) or not isinstance(values[2], list):
        fail("lamp fill literal requires (template path string, y, new[] { x0, z0, ... })")
    flat = values[2]
    if len(flat) % 2:
        fail(f"lamp fill literal has an odd number of cell values ({len(flat)}): every cell needs an x and a z")
    return {"template": values[0], "y": values[1], "cells": [list(flat[i:i + 2]) for i in range(0, len(flat), 2)]}


def parse_door_pose_literal(v: str) -> dict:
    """Door poses: `new ArenaDoorPose(barrier, part, node, clip, seconds, closedPosition, closedEuler, openPosition,
    openEuler)` with four `new[] { x, y, z }` vectors; spelled as one entry of the sidecar's "poses" array."""
    inner = v[len("new ArenaDoorPose("):].rstrip()
    if not inner.endswith(")"):
        fail(f"could not parse door pose literal: {v[:80]!r}")
    values = [parse_cs_value(x) for x in split_top_level_args(inner[:-1])]
    if len(values) != 9 or any(not isinstance(x, str) for x in values[:4]) or any(not isinstance(x, list) for x in values[5:]):
        fail("door pose literal requires (barrier, part, node, clip, seconds, new[] closedPosition, new[] closedEuler, "
             f"new[] openPosition, new[] openEuler): {v[:80]!r}")
    return {"barrier": values[0], "part": values[1], "node": values[2], "clip": values[3], "seconds": values[4],
            "closed": {"position": values[5], "euler": values[6]}, "open": {"position": values[7], "euler": values[8]}}


def parse_cs_value(raw: str):
    """Parses one named-argument value into a Python str / int / list[str] / ("ref", "A.B")."""
    v = raw.strip()
    if v.startswith('"') and v.endswith('"'):
        return v[1:-1]
    if re.fullmatch(r"-?\d+", v):
        return int(v)
    if re.fullmatch(r"-?\d+\.\d+f?", v):
        return float(v.rstrip("f"))
    if v.startswith("new[]"):
        body_m = re.search(r"\{(.*)\}", v, re.S)
        if not body_m:
            fail(f"could not parse array literal: {v!r}")
        items = [parse_cs_value(x) for x in split_top_level_args(body_m.group(1))]
        return items
    if v.startswith("new ArenaExitDescriptor("):
        # exfils-stage2: Exits are `new ArenaExitDescriptor("Name", x, y, z[, sizes, rotation])` literals;
        # the sidecar twin is tools/maps/<key>.exits.json (checked by BaseJsonTests, not here).
        inner = v[len("new ArenaExitDescriptor("):].rstrip()
        if not inner.endswith(")"):
            fail(f"could not parse exit literal: {v!r}")
        return ("exit", [parse_cs_value(x) for x in split_top_level_args(inner[:-1])])
    if v.startswith("new ArenaBorderDescriptor("):
        # AuthoredBorderRepairs: `new ArenaBorderDescriptor("name", x0, z0, x1, z1, minY, maxY)`; the sidecar twin
        # is the map's "authored_borders" array (compared in check_map_against_registry_entry).
        inner = v[len("new ArenaBorderDescriptor("):].rstrip()
        if not inner.endswith(")"):
            fail(f"could not parse border literal: {v!r}")
        values = [parse_cs_value(x) for x in split_top_level_args(inner[:-1])]
        if len(values) != 7:
            fail(f"border literal requires exactly 7 parameters: {v!r}")
        return {"name": values[0], "from": values[1:3], "to": values[3:5], "y": values[5:7]}
    if v.startswith("new ArenaDustDescriptor("):
        inner = v[len("new ArenaDustDescriptor("):].rstrip()
        if not inner.endswith(")"):
            fail(f"could not parse dust literal: {v!r}")
        values = [parse_cs_value(x) for x in split_top_level_args(inner[:-1])]
        if len(values) != 12:
            fail(f"dust literal requires exactly 12 source parameters: {v!r}")
        return dict(zip(("scene", "path", "time_speed", "size", "quad_tile_set_side", "uv_tile_padding"), values[:6]),
                    bounds_center=values[6:9], bounds_extents=values[9:12])
    if v.startswith("new ArenaLampFillDescriptor("):
        return parse_lamp_fill_literal(v)
    if v.startswith("new ArenaDoorPose("):
        return parse_door_pose_literal(v)
    if re.fullmatch(r"[A-Za-z_]\w*\.[A-Za-z_]\w*", v):
        return ("ref", v)
    if re.fullmatch(r"[A-Za-z_]\w*", v):
        # A bare identifier with no ClassName. prefix — only ever seen inside
        # ArenaEquatorLocation.AllSceneNames, which references its OWN sibling consts
        # unqualified (e.g. `ScriptsSceneName,` not `ArenaEquatorLocation.ScriptsSceneName,`).
        return ("localref", v)
    fail(f"don't know how to parse C# value: {v!r}")


def extract_named_args(text: str, call_prefix_regex: str) -> dict:
    """Finds `<call_prefix_regex>(...)` and returns {ArgName: parsed_value} for every top-level
    `ArgName: value` in the parenthesized body. Balances parens itself (regex alone can't) so a
    nested `new[] { ... }` doesn't terminate the match early."""
    m = re.search(call_prefix_regex + r"\(", text)
    if not m:
        fail(f"pattern {call_prefix_regex!r} not found in source")
    start = m.end()
    depth = 1
    i = start
    while i < len(text) and depth > 0:
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
        i += 1
    if depth != 0:
        fail(f"unbalanced parens scanning {call_prefix_regex!r}'s argument list")
    body = strip_line_comments(text[start : i - 1])

    args = {}
    for part in split_top_level_args(body):
        part = part.strip()
        if not part:
            continue
        km = re.match(r"^([A-Za-z_]\w*)\s*:\s*(.*)$", part, re.S)
        if not km:
            fail(f"argument fragment doesn't match 'Name: value' shape: {part!r}")
        name, value_raw = km.group(1), km.group(2)
        args[name] = parse_cs_value(value_raw)
    return args


def extract_string_consts(text: str) -> dict:
    return {
        m.group(1): m.group(2)
        for m in re.finditer(r'public const string (\w+)\s*=\s*"((?:[^"\\]|\\.)*)"\s*;', text)
    }


def extract_string_array_const(text: str, name: str) -> list[str] | None:
    m = re.search(r"public static readonly string\[\]\s*" + re.escape(name) + r"\s*=\s*\{(.*?)\};", text, re.S)
    if not m:
        return None
    return [parse_cs_value(x) for x in split_top_level_args(strip_line_comments(m.group(1)))]


def resolve(value, ref_scope: dict, local_consts: dict | None = None):
    """Resolves a ('ref', 'ArenaEquatorLocation.Foo') tuple against a {ClassName: {member: value}}
    scope, or a ('localref', 'Foo') tuple (a bare identifier referencing a sibling const in the
    SAME file, e.g. ArenaPaths.cs's EquatorDefault() writing `scripts_scene_name = ScriptsSceneName`)
    against `local_consts`. Leaves every other value (str/int/float/list) untouched; lists are
    resolved element-wise."""
    if isinstance(value, tuple) and value[0] == "ref":
        cls, member = value[1].split(".", 1)
        if cls not in ref_scope or member not in ref_scope[cls]:
            fail(f"unresolved C# reference {value[1]!r} (no known const/array with that name)")
        return ref_scope[cls][member]
    if isinstance(value, tuple) and value[0] == "localref":
        if local_consts is None or value[1] not in local_consts:
            fail(f"unresolved unqualified C# identifier {value[1]!r} (no local const with that name in scope)")
        return local_consts[value[1]]
    if isinstance(value, list):
        return [resolve(x, ref_scope, local_consts) for x in value]
    return value


def check_field(errors: list, where: str, field: str, expected, actual) -> None:
    if expected != actual:
        errors.append(f"{where}: {field} mismatch — expected {expected!r}, got {actual!r}")


def background_scene_errors(map_json: dict, registry_args: dict, ref_scope: dict) -> list[str]:
    """A backdrop must be loaded, unique and never replace the gameplay Scripts scene."""
    names = map_json.get("background_scene_names", [])
    if not isinstance(names, list) or any(not isinstance(n, str) or not n for n in names):
        return ["background_scene_names must be an array of nonempty scene names"]
    errors = []
    if len(names) != len(set(names)):
        errors.append("background_scene_names contains duplicate scenes")
    for name in names:
        if name not in map_json.get("all_scene_names", []):
            errors.append(f"background_scene_names references an unloaded scene: {name}")
        if name == map_json.get("scripts_scene_name"):
            errors.append("background_scene_names must not include the Scripts scene")
    actual = resolve(registry_args.get("BackgroundSceneNames", []), ref_scope)
    if names != actual:
        errors.append(f"background_scene_names differs from runtime descriptor: {names!r} != {actual!r}")
    return errors


def is_hierarchy_path(path) -> bool:
    """A root object name plus optional `/`-separated children: no empty or padded segment."""
    return isinstance(path, str) and bool(path.strip()) and all(s and s == s.strip() for s in path.split("/"))


def bake_input_errors(map_json: dict) -> list[str]:
    """Shape of the optional NavMesh bake inputs (PLAN-AIRPIT item 8): opened_barriers and
    navmesh_bake_exclusions (hierarchy paths), bake_bounds ([min_x, max_x, min_y, max_y, min_z, max_z]) and
    navmesh_bake_layers (layer indices). BakeNavMesh.cs reads the same shapes and throws on a malformed box."""
    errors: list[str] = []
    for name in ("opened_barriers", "navmesh_bake_exclusions"):
        paths = map_json.get(name, [])
        if not isinstance(paths, list) or not all(is_hierarchy_path(p) for p in paths):
            errors.append(f"{name} must be an array of hierarchy paths (no empty segment)")
        elif len(paths) != len(set(paths)):
            errors.append(f"{name} contains duplicate paths")
    barriers = map_json.get("opened_barriers", [])
    # start_gates / held_gates (SpawnGates, 2026-10-17): which opened barriers a mode plugin may hold and which stay
    # closed until its Release. Both name opened barriers (the bake drops every gate through that list), never overlap.
    for name in ("start_gates", "held_gates"):
        gates = map_json.get(name, [])
        if not isinstance(gates, list) or not all(is_hierarchy_path(p) for p in gates):
            errors.append(f"{name} must be an array of hierarchy paths (no empty segment)")
        elif len(gates) != len(set(gates)):
            errors.append(f"{name} contains duplicate paths")
        elif isinstance(barriers, list) and any(p not in barriers for p in gates):
            errors.append(f"{name} must be a subset of opened_barriers (the bake leaves every gate out through that list)")
    starts, helds = map_json.get("start_gates", []), map_json.get("held_gates", [])
    if isinstance(starts, list) and isinstance(helds, list) and set(map(str, starts)) & set(map(str, helds)):
        errors.append("start_gates and held_gates overlap (a gate is either held by a mode plugin or closed until Release)")
    if isinstance(helds, list) and helds and not (isinstance(starts, list) and starts):
        errors.append("held_gates without start_gates: name the start gates explicitly (an empty start_gates offers every "
                      "opened barrier to a hold)")
    paths = map_json.get("navmesh_bake_exclusions", [])
    if isinstance(paths, list) and isinstance(barriers, list):
        for path in paths:
            if isinstance(path, str) and any(path == b or path.startswith(str(b) + "/") for b in barriers):
                errors.append(f"navmesh_bake_exclusions entry {path!r} lies inside an opened_barriers path "
                              "(the client deactivates it; a bake exclusion is for objects that stay active)")
    borders = map_json.get("authored_borders")
    if borders is not None:
        # AuthoredBorderRepairs: one invisible wall per entry {name, from [x, z], to [x, z], y [min, max]}.
        def pair(v):
            return isinstance(v, list) and len(v) == 2 and all(type(c) in (int, float) and math.isfinite(c) for c in v)
        ok = isinstance(borders, list) and all(
            isinstance(b, dict) and isinstance(b.get("name"), str) and b["name"].strip() == b["name"] and b["name"]
            and pair(b.get("from")) and pair(b.get("to")) and pair(b.get("y")) and b["y"][0] < b["y"][1]
            and math.hypot(b["to"][0] - b["from"][0], b["to"][1] - b["from"][1]) >= 0.5 for b in borders)
        if not ok:
            errors.append("authored_borders must be an array of {name, from [x, z], to [x, z], y [min < max]} "
                          "objects with finite numbers and segments of at least 0.5 m")
        elif len(borders) != len({b["name"] for b in borders}):
            errors.append("authored_borders contains duplicate names")
    bounds = map_json.get("bake_bounds")
    if bounds is not None:
        finite = isinstance(bounds, list) and len(bounds) == 6 and all(
            type(v) in (int, float) and math.isfinite(v) for v in bounds)
        if not finite or any(bounds[2 * axis] >= bounds[2 * axis + 1] for axis in range(3)):
            errors.append("bake_bounds must be six finite numbers [min_x, max_x, min_y, max_y, min_z, max_z] with min < max")
    layers = map_json.get("navmesh_bake_layers")
    if layers is not None:
        if not isinstance(layers, list) or not layers or any(type(v) is not int or not 0 <= v <= 31 for v in layers):
            errors.append("navmesh_bake_layers must be a nonempty array of layer indices 0..31 (omit it for every layer)")
        elif 4 in layers:
            errors.append("navmesh_bake_layers must not name layer 4 (Water is never collected)")
        elif len(layers) != len(set(layers)):
            errors.append("navmesh_bake_layers contains duplicate layers")
    keep = map_json.get("keep_level_borders")
    if keep is not None:
        # Bowl (docs/PLAN-BOWL.md): the layer-29 LevelBorder cubes stay active and are baked as walls, so the bake must
        # be allowed to collect layer 29 (an explicit layer list without it would silently drop the walls again).
        if type(keep) is not bool:
            errors.append("keep_level_borders must be a boolean (omit it for the default false)")
        elif keep and isinstance(layers, list) and layers and 29 not in layers:
            errors.append("keep_level_borders=true needs navmesh_bake_layers to include layer 29 (or no layer list)")
    return errors


LAMP_FILL_MIN_SPACING = 2.0   # metres; the lattice pitch is about 3.1-3.3 m, a closer pair would overlap its lamp discs


def is_finite_pair(v) -> bool:
    return isinstance(v, list) and len(v) == 2 and all(type(c) in (int, float) and math.isfinite(c) for c in v)


def lamp_fill_errors(map_json: dict) -> list[str]:
    """Shape of the optional lamp_fill (LampFillRepairs): {template, y, cells [[x, z], ...]}. `template` is the hierarchy
    path of the authored lamp the copies are made from (scene root first, at least one child), `y` the world height of the
    copies and `cells` their world x/z positions: finite, no duplicate and no two closer than LAMP_FILL_MIN_SPACING."""
    fill = map_json.get("lamp_fill")
    if fill is None:
        return []
    if not isinstance(fill, dict) or set(fill) != {"template", "y", "cells"}:
        return ["lamp_fill must be an object with exactly template, y and cells"]
    errors: list[str] = []
    if not is_hierarchy_path(fill["template"]) or "/" not in fill["template"]:
        errors.append("lamp_fill template must be a hierarchy path below a scene root (Root/Child/... with no empty segment)")
    if type(fill["y"]) not in (int, float) or not math.isfinite(fill["y"]):
        errors.append("lamp_fill y must be a finite number")
    cells = fill["cells"]
    if not isinstance(cells, list) or not cells or not all(is_finite_pair(c) for c in cells):
        errors.append("lamp_fill cells must be a nonempty array of [x, z] pairs of finite numbers")
        return errors
    duplicates, close = [], []
    for i, a in enumerate(cells):
        for b in cells[i + 1:]:
            distance = math.hypot(a[0] - b[0], a[1] - b[1])
            if distance == 0:
                duplicates.append(a)
            elif distance < LAMP_FILL_MIN_SPACING:
                close.append((a, b))
    if duplicates:
        errors.append(f"lamp_fill cells contain duplicates, e.g. {duplicates[0]}")
    if close:
        errors.append(f"lamp_fill has {len(close)} cell pair(s) closer than {LAMP_FILL_MIN_SPACING} m, e.g. {close[0][0]} and {close[0][1]}")
    return errors


def shader_fallback_errors(map_json: dict) -> list[str]:
    """Shape of the optional shader_fallback_names (PLAN-AIRPIT item 11): the Arena shader names the client's
    ShaderStandinPolicy may rebind to an SPT substitute on this map. Only the C# runtime reads it."""
    names = map_json.get("shader_fallback_names", [])
    if not isinstance(names, list) or any(not isinstance(n, str) or not n or n != n.strip() for n in names):
        return ["shader_fallback_names must be an array of nonempty, unpadded shader names"]
    if len(names) != len(set(names)):
        return ["shader_fallback_names contains duplicate names"]
    return []


DOOR_POSE_KEYS = ("barrier", "part", "node", "clip", "seconds", "closed", "open")
DOOR_SIDECAR_SCHEMA = 1


def is_finite_vec3(value) -> bool:
    return isinstance(value, list) and len(value) == 3 and all(
        isinstance(x, (int, float)) and not isinstance(x, bool) and math.isfinite(x) for x in value)


def door_pose_errors(map_json: dict, doors_path: Path, registry_poses) -> list[str]:
    """tools/maps/<stem>.doors.json (written by tools/extract/extract_door_poses.py) against the map descriptor and the
    registry's DoorPoses: schema, barriers that really are opened_barriers, and the same poses in the same order with the
    same numbers on both sides. A map without the sidecar must have no DoorPoses."""
    name = doors_path.name
    registry_poses = registry_poses or []
    if not doors_path.exists():
        return [f"registry DoorPoses has {len(registry_poses)} entr{'y' if len(registry_poses) == 1 else 'ies'} but {name} does not exist"] \
            if registry_poses else []
    try:
        doc = json.loads(doors_path.read_text(encoding="utf-8"))
    except ValueError as exc:
        return [f"{name} is not valid JSON: {exc}"]
    errors: list[str] = []
    if not isinstance(doc, dict):
        return [f"{name} must be an object"]
    if doc.get("schema_version") != DOOR_SIDECAR_SCHEMA:
        errors.append(f"{name}: schema_version must be {DOOR_SIDECAR_SCHEMA}")
    if doc.get("key") != map_json.get("key"):
        errors.append(f"{name}: key {doc.get('key')!r} is not the map's {map_json.get('key')!r}")
    source = doc.get("source")
    if not isinstance(source, dict) or source.get("scene") not in map_json.get("all_scene_names", []) \
            or type(source.get("level")) is not int or not isinstance(source.get("assets"), str) \
            or not isinstance(source.get("arena_version"), str):
        errors.append(f"{name}: source needs scene (a scene of the map), level (int), assets and arena_version")
    poses = doc.get("poses")
    if not isinstance(poses, list) or not poses:
        return errors + [f"{name}: poses must be a non-empty array"]
    barriers = map_json.get("opened_barriers", [])
    seen: set = set()
    for i, pose in enumerate(poses):
        label = f"{name} poses[{i}]"
        if not isinstance(pose, dict) or set(pose) != set(DOOR_POSE_KEYS):
            errors.append(f"{label}: needs exactly the keys {', '.join(DOOR_POSE_KEYS)}")
            continue
        for field in ("barrier", "part", "clip"):
            if not isinstance(pose[field], str) or not pose[field]:
                errors.append(f"{label}: {field} must be a non-empty string")
        if not isinstance(pose["node"], str):
            errors.append(f"{label}: node must be a string (\"\" = the part itself)")
        if pose["barrier"] not in barriers:
            errors.append(f"{label}: barrier {pose['barrier']!r} is not an opened_barriers entry")
        if isinstance(pose["seconds"], bool) or not isinstance(pose["seconds"], (int, float)) \
                or not math.isfinite(pose["seconds"]) or pose["seconds"] <= 0:
            errors.append(f"{label}: seconds must be a positive finite number")
        for state in ("closed", "open"):
            block = pose[state]
            if not isinstance(block, dict) or set(block) != {"position", "euler"} \
                    or not is_finite_vec3(block.get("position")) or not is_finite_vec3(block.get("euler")):
                errors.append(f"{label}: {state} needs position and euler, three finite numbers each")
        identity = (pose["barrier"], pose["part"], pose["node"])
        if identity in seen:
            errors.append(f"{label}: duplicate pose for {'/'.join(x for x in identity if x)}")
        seen.add(identity)
    if errors:
        return errors
    if len(poses) != len(registry_poses):
        errors.append(f"{name} has {len(poses)} poses, the registry's DoorPoses {len(registry_poses)}")
    for i, (side, reg) in enumerate(zip(poses, registry_poses)):
        if not isinstance(reg, dict):
            errors.append(f"registry DoorPoses[{i}] is not an ArenaDoorPose literal")
            continue
        if side != reg:
            diff = [k for k in DOOR_POSE_KEYS if side.get(k) != reg.get(k)]
            errors.append(f"{name} poses[{i}] ({side['barrier']}/{side['part']}/{side['node']}) differs from the registry in "
                          f"{', '.join(diff)}: sidecar { {k: side[k] for k in diff} }, registry { {k: reg.get(k) for k in diff} }")
    return errors


def check_map_against_registry_entry(
    map_json: dict, registry_args: dict, ref_scope: dict, map_path: Path, cs_var_name: str, errors: list
) -> None:
    where = f"{map_path.name} vs ArenaMapRegistry.{cs_var_name}"
    errors.extend(f"{where}: {error}" for error in background_scene_errors(map_json, registry_args, ref_scope))
    errors.extend(f"{where}: {error}" for error in bake_input_errors(map_json))
    errors.extend(f"{where}: {error}" for error in shader_fallback_errors(map_json))
    errors.extend(f"{where}: {error}" for error in lamp_fill_errors(map_json))
    check_field(errors, where, "lamp_fill", map_json.get("lamp_fill"), registry_args.get("LampFill"))
    errors.extend(f"{where}: {error}" for error in door_pose_errors(
        map_json, map_path.with_name(f"{map_path.stem}.doors.json"), registry_args.get("DoorPoses")))
    check_field(errors, where, "shader_fallback_names", map_json.get("shader_fallback_names", []),
                resolve(registry_args.get("ShaderFallbackNames", []), ref_scope))
    for json_field, cs_field in (("opened_barriers", "OpenedBarriers"), ("navmesh_bake_exclusions", "NavMeshBakeExclusions"),
                                 ("bake_bounds", "BakeBounds"), ("navmesh_bake_layers", "NavMeshBakeLayers"),
                                 ("authored_borders", "AuthoredBorders"), ("start_gates", "StartGates"),
                                 ("held_gates", "HeldGates")):
        check_field(errors, where, json_field, map_json.get(json_field, []), resolve(registry_args.get(cs_field, []), ref_scope))
    check_field(errors, where, "restored_shadow_caster_roots",
                map_json.get("restored_shadow_caster_roots", []),
                resolve(registry_args.get("RestoredShadowCasterRoots", []), ref_scope))
    check_field(errors, where, "distant_shadow_caster_paths",
                map_json.get("distant_shadow_caster_paths", []),
                resolve(registry_args.get("DistantShadowCasterPaths", []), ref_scope))
    dust = map_json.get("dust")
    check_field(errors, where, "dust", dust, registry_args.get("Dust"))
    if dust is not None:
        if not isinstance(dust, dict):
            errors.append(f"{where}: dust must be an object")
        else:
            if dust.get("scene") not in map_json.get("all_scene_names", []):
                errors.append(f"{where}: dust scene must belong to the map")
            if not isinstance(dust.get("path"), str) or not dust["path"]:
                errors.append(f"{where}: dust path must be nonempty")
            for name in ("time_speed", "size", "uv_tile_padding"):
                value = dust.get(name)
                if not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
                    errors.append(f"{where}: dust {name} must be finite and nonnegative")
            if type(dust.get("quad_tile_set_side")) is not int or dust["quad_tile_set_side"] < 1:
                errors.append(f"{where}: dust quad_tile_set_side must be a positive integer")
            for name in ("bounds_center", "bounds_extents"):
                values = dust.get(name)
                if not isinstance(values, list) or len(values) != 3 or any(
                    not isinstance(value, (int, float)) or not math.isfinite(value)
                    or (name == "bounds_extents" and value <= 0) for value in values):
                    errors.append(f"{where}: dust {name} requires three finite values (positive extents)")
    for json_field, cs_field in JSON_TO_CS_FIELD.items():
        if json_field not in map_json:
            errors.append(f"{where}: {map_path.name} is missing field {json_field!r}")
            continue
        if cs_field not in registry_args:
            errors.append(f"{where}: ArenaMapDescriptor.cs's {cs_var_name} entry is missing argument {cs_field!r}")
            continue
        actual = resolve(registry_args[cs_field], ref_scope, local_consts=None)
        check_field(errors, where, json_field, map_json[json_field], actual)

    key = map_json.get("key", "")
    for field, expected in {
        "PresetBundleKey": f"maps/{key}_preset.bundle",
        "PresetAssetName": f"{key}.scenespreset.asset",
        "ScenesBundleKey": f"{key}/{key}_scenes.bundle",
        "NavMeshBundleKey": f"{key}/{key}_navmesh.bundle",
    }.items():
        actual = resolve(registry_args.get(field), ref_scope, local_consts=None)
        check_field(errors, where, field, expected, actual)


def registry_entries(text: str) -> dict[str, dict]:
    clean = strip_line_comments(text)
    names = re.findall(r"ArenaMapDescriptor\s+(\w+)\s*=\s*new\s*\(", clean)
    if not names or len(names) != len(set(names)):
        fail("registry descriptor declarations are absent or duplicated")
    return {name: extract_named_args(clean, rf"ArenaMapDescriptor\s+{re.escape(name)}\s*=\s*new\s*")
            for name in names}


# Bowl joined with PLAN-BOWL item 8 (2026-10-18): registered and staged on feature/bowl for 0.3.0, not released yet.
RELEASE_MAP_NAMES = ("Equator", "Bay5", "AirPit", "Bowl")
RELEASE_MAP_KEYS = ("arena_equator", "arena_bay5", "arena_airpit", "arena_bowl")


def registry_membership_errors(text: str, entries: dict) -> list[str]:
    clean = strip_line_comments(text)
    errors = []
    for array, expected in (("Catalog", set(entries)), ("All", set(RELEASE_MAP_NAMES))):
        match = re.search(r"IReadOnlyList<ArenaMapDescriptor>\s+" + array + r"\s*=\s*new\[\]\s*\{([^}]+)\}", clean, re.S)
        if not match:
            errors.append(f"ArenaMapRegistry.{array} membership array could not be parsed")
            continue
        names = [part.strip() for part in match.group(1).split(",") if part.strip()]
        if len(names) != len(set(names)):
            errors.append(f"ArenaMapRegistry.{array} contains duplicate entries")
        if set(names) != expected:
            errors.append(f"ArenaMapRegistry.{array} membership differs: "
                          f"missing={sorted(expected - set(names))}, unknown={sorted(set(names) - expected)}")
        if not set(names) <= set(entries):
            errors.append(f"ArenaMapRegistry.{array} references undeclared maps")
    return errors


def registry_identity_errors(entries: dict, ref_scope: dict) -> list[str]:
    errors = []
    for field in ("Key", "IdField", "ScriptsSceneName", "InfiltrationName"):
        seen = {}
        for name, entry in entries.items():
            value = resolve(entry.get(field), ref_scope)
            if not isinstance(value, str) or not value:
                errors.append(f"{name}: {field} is missing or invalid")
                continue
            normalized = value.casefold()
            if normalized in seen:
                errors.append(f"duplicate registry {field}: {seen[normalized]} / {name}")
            seen[normalized] = name
            if field == "Key" and not re.fullmatch(r"arena_[a-z0-9_]+", value):
                errors.append(f"{name}: invalid map key {value!r}")
            if field == "IdField" and not re.fullmatch(r"[0-9a-f]{24}", value):
                errors.append(f"{name}: IdField must be 24 lowercase hexadecimal characters")
    for name, entry in entries.items():
        scenes = resolve(entry.get("AllSceneNames"), ref_scope)
        script = resolve(entry.get("ScriptsSceneName"), ref_scope)
        if not isinstance(scenes, list) or not scenes or scenes[0] != script:
            errors.append(f"{name}: Scripts scene must be first in AllSceneNames")
        elif len(scenes) != len(set(scenes)):
            errors.append(f"{name}: duplicate AllSceneNames")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--maps-dir", type=Path, default=REPO / "tools" / "maps")
    args = parser.parse_args()

    errors: list[str] = []

    if not DESCRIPTOR_CS.exists():
        fail(f"{DESCRIPTOR_CS} does not exist")
    if not EQUATOR_LOCATION_CS.exists():
        fail(f"{EQUATOR_LOCATION_CS} does not exist")
    if not ARENA_PATHS_CS.exists():
        fail(f"{ARENA_PATHS_CS} does not exist")

    descriptor_text = DESCRIPTOR_CS.read_text(encoding="utf-8")
    equator_location_text = EQUATOR_LOCATION_CS.read_text(encoding="utf-8")
    arena_paths_text = ARENA_PATHS_CS.read_text(encoding="utf-8")

    # Build the reference-resolution scope: ArenaEquatorLocation's consts + its AllSceneNames array
    # (whose own elements are bare identifiers referencing its OTHER consts -- resolved in two
    # passes since Python dict comprehension can't see later entries while building earlier ones).
    equator_consts = extract_string_consts(equator_location_text)
    all_scene_names_raw = extract_string_array_const(equator_location_text, "AllSceneNames")
    if all_scene_names_raw is None:
        fail(f"{EQUATOR_LOCATION_CS}: could not find 'public static readonly string[] AllSceneNames'")
    resolved_scene_names = []
    for x in all_scene_names_raw:
        if isinstance(x, tuple) and x[0] == "localref":
            if x[1] not in equator_consts:
                fail(f"{EQUATOR_LOCATION_CS}: AllSceneNames references unknown sibling const {x[1]!r}")
            resolved_scene_names.append(equator_consts[x[1]])
        elif isinstance(x, str):
            resolved_scene_names.append(x)
        else:
            fail(f"{EQUATOR_LOCATION_CS}: unexpected AllSceneNames element {x!r}")
    equator_consts["AllSceneNames"] = resolved_scene_names
    ref_scope = {"ArenaEquatorLocation": equator_consts}

    entries = registry_entries(descriptor_text)
    errors.extend(registry_membership_errors(descriptor_text, entries))
    errors.extend(registry_identity_errors(entries, ref_scope))
    registry_by_key = {resolve(entry.get("Key"), ref_scope): (name, entry) for name, entry in entries.items()}
    maps_dir: Path = args.maps_dir
    paths = map_descriptor_paths(maps_dir)
    if not paths:
        fail(f"no map descriptors found in {maps_dir}")
    map_keys = set()
    for path in paths:
        data = load_map(path)
        key = data.get("key")
        if not isinstance(key, str):
            errors.append(f"{path.name}: missing map key")
            continue
        if key in map_keys:
            errors.append(f"duplicate map descriptor key {key}")
        map_keys.add(key)
        if path.stem != key.removeprefix("arena_"):
            errors.append(f"{path.name}: descriptor filename does not match key {key}")
        if key not in registry_by_key:
            errors.append(f"{path.name}: map {key} has no runtime registry descriptor")
            continue
        name, entry = registry_by_key[key]
        check_map_against_registry_entry(data, entry, ref_scope, path, name, errors)
        fog = data.get("fog_color")
        if not isinstance(fog, list) or len(fog) != 3 or any(
            isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in fog
        ):
            errors.append(f"{path.name}: fog_color must contain three finite numbers")
    for missing in sorted(set(registry_by_key) - map_keys):
        errors.append(f"runtime registry map {missing} has no tools/maps JSON descriptor")
    for doors in sorted(maps_dir.glob("*.doors.json")):
        if not (maps_dir / f"{doors.name.removesuffix('.doors.json')}.json").exists():
            errors.append(f"{doors.name}: no tools/maps descriptor {doors.name.removesuffix('.doors.json')}.json beside it")

    equator_json_path = maps_dir / "equator.json"
    if not equator_json_path.exists():
        fail(f"{equator_json_path} does not exist")
    equator_map = load_map(equator_json_path)

    # 3) tools/maps/equator.json vs ArenaPaths.cs's EquatorDefault() (the Editor-assembly fallback
    #    used when -mapDescriptor is omitted — Bay5 has no equivalent by design, see module doc).
    # EquatorDefault() builds an ArenaMapDescriptorData object initializer (`field = value,`), not
    # a constructor call (`Field: value,`) — its own small parse, inline below rather than reusing
    # extract_named_args()/parse_cs_value()'s constructor-call assumptions.
    obj_m = re.search(r"static ArenaMapDescriptorData EquatorDefault\(\)\s*\{.*?return new ArenaMapDescriptorData\s*\{", arena_paths_text, re.S)
    if not obj_m:
        fail(f"{ARENA_PATHS_CS}: could not find 'return new ArenaMapDescriptorData {{' inside EquatorDefault()")
    start = obj_m.end()
    depth = 1
    i = start
    while i < len(arena_paths_text) and depth > 0:
        if arena_paths_text[i] == "{":
            depth += 1
        elif arena_paths_text[i] == "}":
            depth -= 1
        i += 1
    body = strip_line_comments(arena_paths_text[start : i - 1])

    equator_default = {}
    for part in split_top_level_args(body):
        part = part.strip()
        if not part:
            continue
        km = re.match(r"^(\w+)\s*=\s*(.*)$", part, re.S)
        if not km:
            fail(f"{ARENA_PATHS_CS}: EquatorDefault() field fragment doesn't match 'name = value': {part!r}")
        equator_default[km.group(1)] = parse_cs_value(km.group(2))

    equator_default_field_map = {
        "key": "key", "id_field": "id_field", "scripts_scene_name": "scripts_scene_name",
        "all_scene_names": "all_scene_names", "infiltration_name": "infiltration_name",
        "locale_name": "locale_name", "locale_description": "locale_description",
        "icon_x": "icon_x", "icon_y": "icon_y", "template_key": "template_key",
        "db_base_json_path": "db_base_json_path",
    }
    arena_paths_consts = extract_string_consts(arena_paths_text)

    where = f"{equator_json_path.name} vs ArenaPaths.cs's EquatorDefault()"
    for json_field, py_field in equator_default_field_map.items():
        if json_field not in equator_map:
            errors.append(f"{where}: {equator_json_path.name} is missing field {json_field!r}")
            continue
        if py_field not in equator_default:
            errors.append(f"{where}: EquatorDefault() is missing field {py_field!r}")
            continue
        actual = resolve(equator_default[py_field], ref_scope, local_consts=arena_paths_consts)
        check_field(errors, where, json_field, equator_map[json_field], actual)
    # fog_color: float[] — checked separately (approximate-equal, not exact) since JSON stores
    # more decimal places than the C# float literal source might round-trip identically.
    if "fog_color" in equator_map and "fog_color" in equator_default:
        expected = equator_map["fog_color"]
        actual = equator_default["fog_color"]
        if not (isinstance(actual, list) and len(actual) == 3 and all(
            abs(float(a) - float(e)) < 1e-4 for a, e in zip(actual, expected)
        )):
            errors.append(f"{where}: fog_color mismatch — expected {expected!r}, got {actual!r}")

    if errors:
        for e in errors:
            print(f"FAIL: {e}", file=sys.stderr)
        print(f"\n{len(errors)} descriptor mismatch(es) found.", file=sys.stderr)
        return 1

    print(
        f"OK — {len(paths)} tools/maps descriptors, all {len(entries)} catalog registry entries, "
        "release All membership, and ArenaPaths.cs's EquatorDefault() all agree."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
