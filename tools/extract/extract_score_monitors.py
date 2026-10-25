#!/usr/bin/env python3
"""The TV screens Arena fills with a mode's score, time or alive count, as a tracked sidecar per map (docs/progress/score-monitors.md).

Arena's TeamFight design scenes carry `Arena_Home_theater` TV props. Under each sits a GameObject `ArenaMonitorHolder`: a RectTransform with
one MonoBehaviour (`Game.Modes.Data.Monitors.ArenaMonitorHolder`, 40 bytes: the 28-byte MonoBehaviour head, an empty name, then two int32 words:
a scene-local holder id and the TYPE WORD that picks one of Arena's monitor templates). At run time Arena's mode scripts instantiate that
template (Timer, TeamsCommonRoundScore, AlivePlayers, ...) as a world-space canvas on the holder. The extractor of the port skips RectTransform
objects, so the packages carry the TV props but no canvas; the client draws its own faces on the props (ScoreMonitors, tools/extract/
extract_monitor_templates.py for the artwork). This tool reads the holders from Arena's raw level files and writes where they are:

  tools/maps/<stem>.monitors.json   (git-tracked: object paths, positions, sizes and type words only)

What is read (read-only; names and numbers only, nothing is copied from Arena):
  <Arena>/EscapeFromTarkovArena_Data/level<N>    the scene's hierarchy, with its RectTransforms and MonoBehaviours
N is the index of every scene of the map that tools/bundle/key-sources.json lists AND the map imports (tools/maps/<stem>.json
all_scene_names): Equator 51, 53, 55, 57, 58, Bay5 79 to 89, AirPit 28, 29, 37, 38, 40, 324 and Bowl 41 to 48 (95), of which only the design scenes carry holders
(Equator 53 / 57, Bay5 81 / 88, AirPit 28 / 40, Bowl 43 / 48); `source.scenes` of a sidecar lists every scene that was read, so a holder that a future Arena build
adds to another scene is found, not missed. A scene that is not imported (Equator's Tournament scene 61) is never read.

Checks (a failed check stops the tool and writes nothing; the layout is read fail-closed, never guessed):
  * every holder MonoBehaviour is exactly 40 bytes and its GameObject is called ArenaMonitorHolder, active, with a parent prop;
  * all holders share ONE pose below their prop (MOUNT: local position, rotation, uniform scale 0.00054, 1930 x 1163 rect, pivot 0.5);
  * the prop's world scale is uniform (the canvas stays undistorted);
  * the type word is one of TYPE_WORDS (a new word of a new Arena build needs a human decision, the tool stops);
  * (scene, path, at) is unique;
  * with the extracted package present (work/extract/<PACKAGES>), every row's prop exists once in the package scene at the same place and the
    row's `active_in_scene` is the package's active state.

Type words (docs/progress/score-monitors.md, 49 Arena levels correlated with their prop names): 0 Timer, 1 TeamsCommonRoundScore and 2 AlivePlayers
are CONFIRMED (the props of Bowl's TeamFight set are called TIMER / SCORE / ALIVE); 6 TeamsFinalRoundScore and 7 Playerboard are INFERRED from the
order of the 13 template fields of Arena's MonitorTemplatesHolder; 10 names no template (Bowl's two inactive props, most likely a respawn-side
panel): it is a known word with `template: null`. The enum has gaps (11 to 14 are the capture-point boards of Arena's own TDM scenes, which the
four maps do not carry), so a template name is never derived for a word above 7. `face` is the port's choice: timer, score (words 1 and 6),
alive, or none (a prop that stays dark).

Row fields: scene, path (the PROP's hierarchy path, the holder's parent), at (the holder's WORLD position, the canvas centre; tells apart a path
that repeats), arena_type, template, confirmed, face, active_in_scene (the PROP and all its ancestors are active in the scene data),
prop_scale (the prop's uniform world scale), world_size_m (canvas width and height in metres) and viewer_yaw (the compass yaw, 0 = +Z, 90 = +X,
of the direction from the screen towards the reader: the side the canvas is readable from. NOT the canvas forward, which is the opposite).

CLI:
    python tools/extract/extract_score_monitors.py [--map arena_bowl ...] [--arena auto] [--check] [--csharp] [--no-package]
Without --map all four released maps. `--check` compares with the tracked sidecars and writes nothing (exit 1 when one differs). `--csharp` also
prints the `new ArenaScoreMonitor(...)` literals for ArenaMapRegistry.<Map>.ScoreMonitors. The output is byte-stable.
"""
from __future__ import annotations

import argparse
import json
import math
import re
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(HERE))
import extract_door_poses as doors  # noqa: E402  (stdlib only at import time; UnityPy is imported when a scene is read)

SCHEMA_VERSION = 1
MAPS_DIR = REPO / "tools" / "maps"
KEY_SOURCES = REPO / "tools" / "bundle" / "key-sources.json"
EXTRACT_ROOT = REPO / "work" / "extract"

HOLDER_CLASS = "Game.Modes.Data.Monitors.ArenaMonitorHolder"
HOLDER_NAME = "ArenaMonitorHolder"
HOLDER_RAW_BYTES = 40        # 28 bytes of MonoBehaviour head + an empty m_Name (int32 0) + holder_id + holder_type
HOLDER_TAIL_BYTES = 8

# The one pose every ArenaMonitorHolder has under its TV prop (47 of 47 in the Arena build of 2026-10-25; ArenaMapPort.Shared
# ArenaScoreMonitorMount mirrors it): prop-local position, rotation (x, y, z, w), uniform local scale and the canvas size in canvas pixels.
MOUNT = {"position": [0.0, 0.0, 0.369285], "rotation": [-0.5, 0.5, 0.5, -0.5], "scale": 0.00054, "size": [1930, 1163], "pivot": [0.5, 0.5]}
MOUNT_POSITION_TOL = 1e-5
MOUNT_ROTATION_TOL = 1e-5
MOUNT_SCALE_TOL = 1e-7
UNIFORM_SCALE_TOL = 1e-4     # relative spread of the prop's world scale
PACKAGE_AT_TOL = 0.05        # m, the client matches a prop to a row within this (the packages hold the same data to < 1e-3)

FACE_TIMER, FACE_SCORE, FACE_ALIVE, FACE_NONE = "timer", "score", "alive", "none"

# type word -> (Arena template, confirmed, face). A word that is not here stops the tool (fail closed).
TYPE_WORDS = {
    0: ("MonitorTimer", True, FACE_TIMER),
    1: ("MonitorTeamsCommonRoundScore", True, FACE_SCORE),
    2: ("MonitorAlivePlayers", True, FACE_ALIVE),
    6: ("MonitorTeamsFinalRoundScore", False, FACE_SCORE),
    7: ("MonitorPlayerboard", False, FACE_NONE),
    10: (None, False, FACE_NONE),
}

# map key -> stem, the extracted package the rows are cross-checked against (accepted inputs, AGENTS.md)
MAPS = {
    "arena_equator": {"stem": "equator", "package": "equator-native-v5"},
    "arena_bay5": {"stem": "bay5", "package": "bay5-native-v5"},
    "arena_airpit": {"stem": "airpit", "package": "airpit-native-v9"},
    "arena_bowl": {"stem": "bowl", "package": "bowl-native-v2"},
}


class MonitorError(Exception):
    """A violated contract of the data (fails closed: no sidecar is written)."""


# ---- pure helpers (unit-tested without Arena) ---------------------------------------------------------------------------------------

def world_pose(chain):
    """(position, rotation xyzw, scale) of the last entry of a chain [(local position, local rotation xyzw, local scale), ...] that runs from the
    scene root down. Rotations compose as parent * child, scales multiply component-wise (exact while the chain scales uniformly, which the
    extractor checks for the prop)."""
    pos, rot, scl = (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)
    for lp, lr, ls in chain:
        moved = doors.quat_rotate(rot, (lp[0] * scl[0], lp[1] * scl[1], lp[2] * scl[2]))
        pos = (pos[0] + moved[0], pos[1] + moved[1], pos[2] + moved[2])
        rot = doors.quat_mul(rot, tuple(lr))
        length = math.sqrt(sum(v * v for v in rot))
        rot = tuple(v / length for v in rot)
        scl = (scl[0] * ls[0], scl[1] * ls[1], scl[2] * ls[2])
    return pos, rot, scl


def uniform_scale(scale, what: str) -> float:
    """The single value of a uniform 3-scale; anything else would stretch the canvas."""
    top = max(abs(v) for v in scale)
    if top == 0.0 or max(abs(v - scale[0]) for v in scale) > UNIFORM_SCALE_TOL * top or scale[0] <= 0.0:
        raise MonitorError(f"{what}: the world scale {tuple(round(v, 5) for v in scale)} is not a uniform positive scale")
    return sum(scale) / 3.0


def reader_yaw(rotation) -> float:
    """The compass yaw (degrees in [0, 360), 0 = +Z, 90 = +X) of the direction from a holder's screen towards the reader. A world-space canvas
    is readable from its -Z side, so the reader stands towards the NEGATIVE of the canvas forward (the holder's +Z axis)."""
    forward = doors.quat_rotate(rotation, (0.0, 0.0, 1.0))
    return round(math.degrees(math.atan2(-forward[0], -forward[2])) % 360.0, 1) % 360.0


def type_word(word: int, where: str) -> tuple:
    """(template, confirmed, face) of a type word, or the tool stops: a new Arena build may add or renumber words."""
    if word not in TYPE_WORDS:
        raise MonitorError(f"{where}: the type word {word} is not one of {sorted(TYPE_WORDS)}; classify it in extract_score_monitors.TYPE_WORDS "
                           "(a word above 7 names no template of Arena's list, docs/progress/score-monitors.md)")
    return TYPE_WORDS[word]


def natural_key(path: str) -> list:
    return [int(t) if t.isdigit() else t for t in re.split(r"(\d+)", path)]


def r4(value: float) -> float:
    return round(float(value), 4) + 0.0


def r3(value: float) -> float:
    return round(float(value), 3) + 0.0


def row_identity(row: dict) -> tuple:
    return (row["scene"], row["path"], tuple(row["at"]))


def check_unique_rows(key: str, rows: list[dict]) -> None:
    """No two rows of one scene and path stand within PACKAGE_AT_TOL of each other (a repeated path is told apart by `at`; two holders at one place
    would be one screen twice)."""
    for i, a in enumerate(rows):
        for b in rows[i + 1:]:
            if a["scene"] == b["scene"] and a["path"] == b["path"] and math.dist(a["at"], b["at"]) <= PACKAGE_AT_TOL:
                raise MonitorError(f"{key}: two holders at the same place: {row_identity(a)}")


def sort_rows(rows: list[dict], scene_order: list[str]) -> list[dict]:
    return sorted(rows, key=lambda r: (scene_order.index(r["scene"]), natural_key(r["path"]), r["at"]))


def check_mount(local, size, pivot, where: str) -> None:
    """The holder's local pose and rect against MOUNT (one pose for every holder; a different one would need a per-row pose)."""
    (position, rotation, scale), expected = local, MOUNT
    if max(abs(a - b) for a, b in zip(position, expected["position"])) > MOUNT_POSITION_TOL:
        raise MonitorError(f"{where}: the holder's local position {tuple(round(v, 6) for v in position)} is not the shared mount {expected['position']}")
    same = max(abs(a - b) for a, b in zip(rotation, expected["rotation"]))
    flipped = max(abs(a + b) for a, b in zip(rotation, expected["rotation"]))
    if min(same, flipped) > MOUNT_ROTATION_TOL:
        raise MonitorError(f"{where}: the holder's local rotation {tuple(round(v, 6) for v in rotation)} is not the shared mount {expected['rotation']}")
    if max(abs(v - expected["scale"]) for v in scale) > MOUNT_SCALE_TOL:
        raise MonitorError(f"{where}: the holder's local scale {tuple(scale)} is not the shared mount {expected['scale']}")
    if [round(v, 3) for v in size] != [float(v) for v in expected["size"]] or [round(v, 3) for v in pivot] != expected["pivot"]:
        raise MonitorError(f"{where}: the holder's rect {tuple(size)} / pivot {tuple(pivot)} is not the shared mount {expected['size']} / {expected['pivot']}")


def make_row(scene_name: str, path: str, at, word: int, active: bool, prop_scale: float, rotation, where: str) -> dict:
    template, confirmed, face = type_word(word, where)
    return {"scene": scene_name, "path": path, "at": [r4(v) for v in at], "arena_type": word, "template": template, "confirmed": confirmed,
            "face": face, "active_in_scene": active, "prop_scale": round(prop_scale, 4) + 0.0,
            "world_size_m": [r3(MOUNT["size"][0] * MOUNT["scale"] * prop_scale), r3(MOUNT["size"][1] * MOUNT["scale"] * prop_scale)],
            "viewer_yaw": reader_yaw(rotation)}


def prop_world_at(prop_pose) -> tuple:
    """The holder's world position for a prop pose: the prop's world position plus the mount's position, scaled and rotated by the prop."""
    pos, rot, scl = prop_pose
    mp = MOUNT["position"]
    moved = doors.quat_rotate(rot, (mp[0] * scl[0], mp[1] * scl[1], mp[2] * scl[2]))
    return (pos[0] + moved[0], pos[1] + moved[1], pos[2] + moved[2])


# ---- the sidecar --------------------------------------------------------------------------------------------------------------------

SIDECAR_COMMENT = (
    "Generated by tools/extract/extract_score_monitors.py (docs/progress/score-monitors.md): the TV props of Arena's design scenes whose "
    "ArenaMonitorHolder shows a mode's timer, score or alive count. One row per holder of every scene the map imports. path = the PROP's hierarchy path "
    "(the holder's parent) inside scene (Equator's two DesignStuff scenes repeat the same paths), at = the holder's WORLD position in the scene data "
    "(the canvas centre; tells apart a path that repeats, and is checked against the loaded prop within 0.05 m). arena_type = the holder's type word; "
    "template = Arena's template for it (null when the word names none), confirmed = true for the words that Bowl's TIMER / SCORE / ALIVE prop names prove "
    "(0, 1, 2); face = what the port draws: timer, score (words 1 and 6), alive, or none (the prop stays dark). active_in_scene = the prop and all its "
    "ancestors are active in the scene data; only such a prop gets a screen. prop_scale = the prop's uniform world scale, world_size_m = the canvas "
    "in metres, viewer_yaw = the compass yaw (0 = +Z, 90 = +X) of the direction from the screen towards the reader (the canvas is readable from there; "
    "the canvas forward points the other way). mount = the pose every holder has below its prop (local position, rotation xyzw, uniform scale, rect "
    "in canvas pixels). ArenaMapRegistry.<Map>.ScoreMonitors mirrors scene, path, at, arena_type and active_in_scene number for number "
    "(validate_map_descriptors.py and ScoreMonitorSidecarTests). Object names and derived numbers only; no Arena asset or file is stored."
)


def _line(value) -> str:
    return json.dumps(value, separators=(", ", ": "), ensure_ascii=True)


def render_sidecar(doc: dict) -> str:
    """The sidecar text: one object per line for the rows, so a review diff reads row by row."""
    out = ["{", f'  "_comment": {json.dumps(SIDECAR_COMMENT, ensure_ascii=True)},', f'  "schema_version": {doc["schema_version"]},',
           f'  "key": {json.dumps(doc["key"])},', f'  "source": {_line(doc["source"])},', f'  "mount": {_line(doc["mount"])},', '  "monitors": [',
           ",\n".join("    " + _line(row) for row in doc["monitors"]), "  ]", "}"]
    return "\n".join(out) + "\n"


def csharp_float(value: float) -> str:
    text = repr(float(value))
    if "e" in text or "E" in text:
        raise MonitorError(f"cannot spell {value!r} as a plain C# float literal")
    return text + "f"


def render_csharp(rows: list[dict], indent: str = " " * 16) -> str:
    """The `new ArenaScoreMonitor(...)` literals of ArenaMapRegistry.<Map>.ScoreMonitors for a sidecar's rows, ready to paste between
    `ScoreMonitors: new[] {` and `}` (a test pins that the registry carries exactly this text)."""
    lines = []
    for r in rows:
        at = ", ".join(csharp_float(v) for v in r["at"])
        lines.append(f"{indent}new ArenaScoreMonitor({json.dumps(r['scene'])}, {json.dumps(r['path'])}, new[] {{ {at} }}, "
                     f"{r['arena_type']}, {str(bool(r['active_in_scene'])).lower()}),")
    return "\n".join(lines)


def sidecar_path(key: str) -> Path:
    return MAPS_DIR / f"{MAPS[key]['stem']}.monitors.json"


def map_scenes(key: str) -> list[tuple[str, int]]:
    """(scene name, level index) of every scene of the map that key-sources.json lists and the map imports, in the map's load order."""
    descriptor = json.loads((MAPS_DIR / f"{MAPS[key]['stem']}.json").read_text(encoding="utf-8"))
    table = {e["scene"]: int(e["index"]) for e in json.loads(KEY_SOURCES.read_text(encoding="utf-8"))["maps"][key]}
    return [(name, table[name]) for name in descriptor["all_scene_names"] if name in table]


# ---- reading Arena ------------------------------------------------------------------------------------------------------------------

def read_holders(scene, scene_name: str, level: int) -> list[dict]:
    """The rows of one level file, unsorted. Every MonoBehaviour whose script is the holder class must be a 40-byte holder."""
    rows = []
    for pid, obj in sorted(scene.files[scene.level_name].items()):
        if obj.type.name != "MonoBehaviour" or scene.mono_class(obj) != HOLDER_CLASS:
            continue
        where = f"level{level} object {pid}"
        raw = obj.get_raw_data()
        if len(raw) != HOLDER_RAW_BYTES:
            raise MonitorError(f"{where}: an ArenaMonitorHolder of {len(raw)} bytes, expected {HOLDER_RAW_BYTES}: the class changed")
        _gf, gp, _enabled, _sf, _sp, name, tail_at = doors.parse_mono_head(raw)
        tail = raw[tail_at:]
        if name != "" or len(tail) != HOLDER_TAIL_BYTES:
            raise MonitorError(f"{where}: the holder's name / tail is not an empty name and two int32 words")
        _holder_id, word = struct.unpack("<ii", tail)
        holder = scene.go2tr[gp]
        if scene.name(holder) != HOLDER_NAME:
            raise MonitorError(f"{where}: the holder's GameObject is called {scene.name(holder)!r}, expected {HOLDER_NAME!r}")
        if not scene.go[gp]["m_IsActive"]:
            raise MonitorError(f"{where}: the holder's own GameObject is inactive: {scene.path(holder)}")
        parent = scene.tr[holder]["m_Father"]["m_PathID"]
        if not parent:
            raise MonitorError(f"{where}: the holder has no parent prop")
        rect = scene.tr[holder]
        if "m_SizeDelta" not in rect:
            raise MonitorError(f"{where}: the holder is no RectTransform")
        local = scene.local(holder)
        check_mount(local, (rect["m_SizeDelta"]["x"], rect["m_SizeDelta"]["y"]), (rect["m_Pivot"]["x"], rect["m_Pivot"]["y"]), where)
        chain, t = [], holder
        while t:
            chain.append(scene.local(t))
            t = scene.tr[t]["m_Father"]["m_PathID"]
        chain.reverse()
        pos, rot, _scl = world_pose(chain)
        prop_chain = chain[:-1]
        prop_scale = uniform_scale(world_pose(prop_chain)[2], f"{where} ({scene.path(parent)})")
        rows.append(make_row(scene_name, scene.path(parent), pos, word, scene.active_in_hierarchy(parent), prop_scale, rot, f"{scene_name} {scene.path(parent)}"))
    return rows


def check_package(rows: list[dict], package: Path) -> list[str]:
    """Every row's prop exists once in the extracted package scene, where the row says (within PACKAGE_AT_TOL), with the row's activity.
    Returns the problems (empty = fine)."""
    problems = []
    scenes: dict[str, doors.PackageScene] = {}
    for row in rows:
        scene = scenes.get(row["scene"])
        if scene is None:
            scene = scenes[row["scene"]] = doors.PackageScene(package, row["scene"])
        found = []
        for o in scene.index.get(row["path"], []):
            chain = []
            cur = o
            while cur is not None:
                chain.append((cur["local_position"], cur["local_rotation"], cur["local_scale"]))
                cur = scene.by_id.get(cur.get("parent_id"))
            pose = world_pose(list(reversed(chain)))
            if math.dist(prop_world_at(pose), row["at"]) <= PACKAGE_AT_TOL:
                active = True
                cur = o
                while cur is not None:
                    active = active and bool(cur["active"])
                    cur = scene.by_id.get(cur.get("parent_id"))
                found.append((o, active))
        label = f"{row['scene']} {row['path']} at {row['at']}"
        if len(found) != 1:
            problems.append(f"{label}: {len(found)} props of the package scene stand there, expected 1")
        elif found[0][1] != row["active_in_scene"]:
            problems.append(f"{label}: active_in_scene is {row['active_in_scene']}, the package's prop is {found[0][1]}")
    return problems


def extract_map(key: str, data_dir: Path, arena_version: str, package_root: Path | None) -> tuple[dict, list[str]]:
    """The sidecar document of one map, read from the installed Arena. Returns (document, notes)."""
    notes = []
    scenes = map_scenes(key)
    if not scenes:
        raise MonitorError(f"{key}: no scene of the map is listed in {KEY_SOURCES.name}")
    rows = []
    for name, level in scenes:
        rows += read_holders(doors.ArenaScene(data_dir, level), name, level)
    if not rows:
        raise MonitorError(f"{key}: no ArenaMonitorHolder in its scenes {[n for n, _ in scenes]}")
    check_unique_rows(key, rows)
    if package_root is not None:
        package = package_root / MAPS[key]["package"]
        if package.is_dir():
            problems = check_package(rows, package)
            if problems:
                raise MonitorError(f"{key}: the rows disagree with {package.name}:\n  " + "\n  ".join(problems))
            notes.append(f"{key}: {len(rows)} prop(s) found once in {package.name} with the same activity")
        else:
            notes.append(f"{key}: package {package.name} not present, the rows were not cross-checked")
    order = [name for name, _ in scenes]
    doc = {"schema_version": SCHEMA_VERSION, "key": key,
           "source": {"arena_version": arena_version, "scenes": [{"scene": n, "level": lv} for n, lv in scenes],
                      "tool": "tools/extract/extract_score_monitors.py"},
           "mount": MOUNT, "monitors": sort_rows(rows, order)}
    return doc, notes


# ---- CLI ----------------------------------------------------------------------------------------------------------------------------

def summary_line(key: str, doc: dict) -> str:
    rows = doc["monitors"]
    faces: dict[str, int] = {}
    for r in rows:
        if r["active_in_scene"]:
            faces[r["face"]] = faces.get(r["face"], 0) + 1
    shown = ", ".join(f"{n} {f}" for f, n in sorted(faces.items()))
    return f"{key}: {len(rows)} holder(s), {sum(1 for r in rows if r['active_in_scene'])} active ({shown})"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--map", action="append", dest="maps", choices=sorted(MAPS), metavar="ARENA_KEY", help="repeatable; default: the four released maps")
    parser.add_argument("--arena", default="auto", help="the Arena installation (or its EscapeFromTarkovArena_Data folder); auto = the launcher's entry")
    parser.add_argument("--check", action="store_true", help="compare with the tracked sidecars and write nothing")
    parser.add_argument("--csharp", action="store_true", help="also print the ArenaScoreMonitor literals for ArenaMapRegistry.<Map>.ScoreMonitors")
    parser.add_argument("--no-package", action="store_true", help="skip the cross-check against work/extract/<package>")
    parser.add_argument("--package-root", type=Path, default=EXTRACT_ROOT, help="the folder that holds the extracted packages")
    args = parser.parse_args(argv)
    try:
        data_dir, version = doors.find_arena(args.arena)
        status = 0
        for key in args.maps or sorted(MAPS):
            doc, notes = extract_map(key, data_dir, version, None if args.no_package else args.package_root)
            text = render_sidecar(doc)
            path = sidecar_path(key)
            print(summary_line(key, doc))
            for note in notes:
                print("  " + note)
            if args.check:
                same = path.is_file() and path.read_bytes().replace(b"\r\n", b"\n") == text.encode("utf-8")
                print(f"  {path.name} {'is current' if same else 'DIFFERS'}")
                status |= 0 if same else 1
            else:
                path.write_text(text, encoding="utf-8", newline="\n")
                print(f"  wrote {path.name}")
            if args.csharp:
                print(f"// {key}: {len(doc['monitors'])} rows\n" + render_csharp(doc["monitors"]))
        return status
    except (MonitorError, doors.DoorPoseError, OSError, ValueError, KeyError) as exc:
        print(f"[extract_score_monitors] FAILED: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
