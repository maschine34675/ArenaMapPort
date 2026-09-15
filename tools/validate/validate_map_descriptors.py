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


def check_map_against_registry_entry(
    map_json: dict, registry_args: dict, ref_scope: dict, map_path: Path, cs_var_name: str, errors: list
) -> None:
    where = f"{map_path.name} vs ArenaMapRegistry.{cs_var_name}"
    for json_field, cs_field in JSON_TO_CS_FIELD.items():
        if json_field not in map_json:
            errors.append(f"{where}: {map_path.name} is missing field {json_field!r}")
            continue
        if cs_field not in registry_args:
            errors.append(f"{where}: ArenaMapDescriptor.cs's {cs_var_name} entry is missing argument {cs_field!r}")
            continue
        actual = resolve(registry_args[cs_field], ref_scope, local_consts=None)
        check_field(errors, where, json_field, map_json[json_field], actual)


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

    equator_args = extract_named_args(descriptor_text, r"ArenaMapDescriptor\s+Equator\s*=\s*new")
    bay5_args = extract_named_args(descriptor_text, r"ArenaMapDescriptor\s+Bay5\s*=\s*new")

    maps_dir: Path = args.maps_dir
    equator_json_path = maps_dir / "equator.json"
    bay5_json_path = maps_dir / "bay5.json"
    if not equator_json_path.exists():
        fail(f"{equator_json_path} does not exist")
    if not bay5_json_path.exists():
        fail(f"{bay5_json_path} does not exist")

    equator_map = load_map(equator_json_path)
    bay5_map = load_map(bay5_json_path)

    # 1) tools/maps/equator.json vs ArenaMapRegistry.Equator (delegated fields resolved through
    #    ArenaEquatorLocation.cs's own consts; the rest — InfiltrationName/Locale*/Icon*/
    #    DbBaseJsonPath — are ArenaMapRegistry.cs's own literals, checked directly).
    check_map_against_registry_entry(equator_map, equator_args, ref_scope, equator_json_path, "Equator", errors)

    # 2) tools/maps/bay5.json vs ArenaMapRegistry.Bay5 (every field is Bay5's own literal — no
    #    delegation, ref_scope only matters if a future edit adds one).
    check_map_against_registry_entry(bay5_map, bay5_args, ref_scope, bay5_json_path, "Bay5", errors)

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
        "OK — tools/maps/equator.json, tools/maps/bay5.json, ArenaMapDescriptor.cs's "
        "ArenaMapRegistry.{Equator,Bay5}, and ArenaPaths.cs's EquatorDefault() all agree."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
