#!/usr/bin/env python3
"""Decode the door animations Arena plays on a map's spawn gates into tools/maps/<stem>.doors.json.

Arena opens the gates of a team pocket (Bowl: the BTR, the Telfer gate, the army gate and the ambulance) with
Mecanim animations: an Animator on a scene object, an AnimatorController in the level's shared assets, and Mecanim
AnimationClips whose curves are stored as a StreamedClip (cubic keys), a DenseClip (sampled) and a ConstantClip
(one value per curve). The port cannot play those (the extractor drops Animators and the EFT client has no Arena
controllers), so the raid-start opening used to hide the parts. This tool reads the clips, keeps the END POSE of
every node a gate clip moves, and writes it next to the closed pose of the scene, so the client can park the part in
its open end pose instead (docs/progress/door-poses.md has the method, the decoded table and the limits).

What is read (everything read-only, names and numbers only, nothing is copied from Arena):
  <Arena>/EscapeFromTarkovArena_Data/level<N>                  the scene's hierarchy, its Animators and local transforms
  <Arena>/EscapeFromTarkovArena_Data/sharedassets<N>.assets    the AnimatorControllers and AnimationClips of that level
N is the scene's index from tools/bundle/key-sources.json (the same table stage 2 uses).

Method:
  1. Every Animator whose GameObject lies below an `opened_barriers` entry of tools/maps/<stem>.json is a gate
     animator. The barrier is the longest listed prefix of its hierarchy path, the PART is the next segment (the direct
     child of the barrier root that BoothDoorRepairs switches off), the rest is the Animator's own sub path.
  2. The AnimatorController lists its clips. The OPEN clip is the one whose name does not end in "closed"
     (case-insensitive); exactly one must remain, otherwise the tool stops. The `*_closed` clip (a constant clip) is only
     a cross-check of the closed pose.
  3. A clip binding names a node by the CRC-32 (zlib, UTF-8) of its transform path relative to the Animator's
     GameObject; 0 is the Animator's GameObject itself. The hash is resolved against every descendant of the Animator
     (Unity's own rule; an unresolved or ambiguous hash stops the tool).
  4. Curve indices run over the bindings in order (position 3, quaternion rotation 4, scale 3, euler 3 curves per
     binding); indices below the streamed curve count belong to the StreamedClip, then the DenseClip, then the
     ConstantClip. A node's OPEN value is the curve value at the clip's stop time.
  5. The CLOSED pose is the scene's own local transform of the node. The tool cross-checks it against the `*_closed`
     clip and the open clip's first key: a warning beyond 2 cm / 1 degree, a stop beyond 10 cm / 5 degrees, which is what
     would show a wrong hash or a wrong frame convention. It also checks its own decode (each key's cubic must reach the
     next key's value) and, with --package, that every node exists once in the extracted port scene.
  6. A node enters the sidecar when its open pose differs from the closed pose by more than 1 cm or 1 degree.

Second source, the DotAnimator (Equator, Bay5 and AirPit, 2026-10-23/24, docs/progress/door-animations-survey.md and the
"DotAnimator" sections of door-poses.md): Equator's booth doors, Bay5's roller gates and depot gates and AirPit's passport
leaves have NO Animator and no clip. Arena moves them with its own tween script, the MonoBehaviour CommonAssets.Scripts.Game.Modes.Data.DotAnimator (DOTween
based), which stores the END value, the duration and the ease of one move or rotation per target. Its serialized tail is read
from the raw bytes (no typetree exists for player-built script classes):

    MonoBehaviour head   m_GameObject PPtr (12 bytes), m_Enabled (4), m_Script PPtr (12), m_Name (int32 length + bytes, 4-aligned)
    tail                 5 header words (0, 1, 0, flag, element count), then per element 53 words + 7 words per curve key:
      words 0-2          PPtr (fileID 0, pathID) to the element's own target GameObject
      word 3             float, unknown (-1 / -2 / 0)
      word 4             float, duration in seconds (kept UNSCALED; the client scales it)
      word 5             int kind: 1 = move to a LOCAL position, 2 = rotate to LOCAL euler angles, 3 = switch (SetActive / emissive)
      word 7             int DOTween ease: 1 Linear, 16 InOutQuint, 37 INTERNAL_Custom (use the stored curve); a switch carries 17
                         (the unused default), which is not read
      word 11            int curve key count n, then n x 7 words (time, value, inSlope, outSlope, weightedMode, inWeight, outWeight),
                         then the words 2, 2, 4 (the curve's wrap modes and rotation order), then a 38-word payload whose words
                         1-3 are the TARGET vector (x, y, z) of a move / rotate and whose word 8 is, for a switch, the active state
                         it sets (0 / 1; 2026-10-24: 23 of 23 switches of Equator level 57 and AirPit level 28 set the OPPOSITE of
                         their target's scene state, RedFog 1 -> 0, GreenFog 0 -> 1, every lamp 0 -> 1)
The decode of an object is trusted only when the offset after the last field equals the object's byte length. The targets are
ABSOLUTE local values in the parent's frame (Bay5's roller targets keep the node's x and z and change only y; Equator's
+-2.0 as an offset would push each leaf 0.58 m out of its frame), so a kind-1 pose is closed position -> target, a kind-2 pose
closed euler -> target euler (interpolate rotations as a quaternion slerp between Quaternion.Euler of the two triples). A kind-3
element is skipped (a lamp) unless the scan sets "switches": then it becomes a SWITCH row (the sidecar's optional "switch":
{"closed": the target's scene activeSelf, "open": the payload bool}; its closed and open transforms are the node's scene
transform; AirPit's passport lamps RED / GREEN). A DotAnimator on an inactive GameObject (Bay5's Gates_PTOR_medium twins) moves
nothing and is skipped. Where Arena authors no motion for a sibling gate the sidecar borrows the neighbour's (source "borrowed",
checked: the borrower really has no DotAnimator); mode "scene" (AirPit's bus doors) borrows the lender's STATIC pose instead of
a motion: the open pose of a node is the lender's current local transform of the same-named node. Per-map scan roots, barrier
depth and borrowed gates are in DOT_SOURCES.

Sidecar schema 2 adds to every pose: "source" (clip / dotanimator / borrowed), "ease" (smoothstep = the client's stand-in for
Bowl's clips, linear, inOutQuint, curve), "curve" (null, or Hermite keys [time, value, inSlope, outSlope] on a 0..1 timeline,
used when ease is "curve") and "at" (null, or the WORLD position of the barrier root instance, which tells repeated hierarchy
paths apart). "part" may be "" (the barrier root itself moves: Bay5's rollers). A part name may repeat among the direct
children of a barrier (AirPit's 16 passport leaves reuse 6 names): such rows are told apart by their closed position, which
the client matches within CLOSED_MATCH_TOL (0.05 m).

CLI:
    python tools/extract/extract_door_poses.py --map arena_bowl --arena auto \\
        --package work/extract/bowl-native-v2 --output tools/maps/bowl.doors.json [--csharp]
    python tools/extract/extract_door_poses.py --map arena_equator --package work/extract/equator-native-v5 \\
        --output tools/maps/equator.doors.json --csharp
    python tools/extract/extract_door_poses.py --map arena_bay5 --package work/extract/bay5-native-v5 \\
        --output tools/maps/bay5.doors.json --csharp
    python tools/extract/extract_door_poses.py --map arena_airpit --package work/extract/airpit-native-v9 \\
        --output tools/maps/airpit.doors.json --csharp
`--csharp` also prints the `new ArenaDoorPose(...)` literals for ArenaMapRegistry.<Map>.DoorPoses; the output is
byte-stable. The verification summary goes to stdout; a failed check goes to stderr with exit code 1 and no file written.
"""
from __future__ import annotations

import argparse
import json
import math
import struct
import sys
import zlib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

REPO = Path(__file__).resolve().parents[2]
SCHEMA_VERSION = 2       # 1 (Bowl until 2026-10-23) lacked source / ease / curve / at; the tool writes 2 for every map
DATA_DIR = "EscapeFromTarkovArena_Data"
DEFAULT_DATA = Path("D:/Battlestate Games/Escape from Tarkov Arena") / DATA_DIR
KEY_SOURCES = REPO / "tools" / "bundle" / "key-sources.json"

# Pose provenance and ease names of the sidecar (schema 2).
SOURCE_CLIP, SOURCE_DOT, SOURCE_BORROWED = "clip", "dotanimator", "borrowed"
SOURCES = (SOURCE_CLIP, SOURCE_DOT, SOURCE_BORROWED)
EASE_SMOOTHSTEP, EASE_LINEAR, EASE_IN_OUT_QUINT, EASE_CURVE = "smoothstep", "linear", "inOutQuint", "curve"
EASES = (EASE_SMOOTHSTEP, EASE_LINEAR, EASE_IN_OUT_QUINT, EASE_CURVE)
CURVE_DECIMALS = 5
AT_TOLERANCE = 0.05       # m, two barrier roots closer than this are the same instance (client DoorPosesOf uses the same)

# map key -> the scene that holds the gate objects and the descriptor sidecar of the Mecanim clip source. Only Bowl's gates are
# animated by clips; Equator, Bay5 and AirPit use the DotAnimator (DOT_SOURCES below); AirPit's three bus-door Animators use a
# controller without clips (their open pose is the static back row, mode "scene"; docs/progress/door-poses.md, "AirPit").
MAP_SOURCES = {
    "arena_bowl": {"scene": "Arena_Bowl_TeamFight_DesignStuff", "descriptor": REPO / "tools" / "maps" / "bowl.json"},
}

# DotAnimator source (see the module doc). Per map:
#   scene      the TeamFight_DesignStuff level that holds the gate groups
#   scans      [{"root", "depth", "at", "switches"?}]: every active DotAnimator below the scene path `root` is a gate element.
#              `depth` is the number of path segments that make the BARRIER root (Equator: SPAWN_DOORS/<assembly>; Bay5 rollers:
#              WAREHOUSE_DOORS (1)/<roller>, which is also the node that moves, so part = ""; Bay5 depot:
#              OLD_WAREHOUSE_DOORS/Gates_Depot_Frame; AirPit: PASSPORT_CONTROL_DOORS itself, depth 1, the root IS the barrier);
#              the next segment is the part, the rest the node. `at` = True records the barrier root's world position (repeated
#              paths: both unnumbered Equator assemblies, both depot frames). `switches` = True reads the kind-3 elements below
#              the root as switch rows (AirPit's RED / GREEN lamps); without it they are skipped with a note.
#   borrowed   gates Arena authors no motion for, given a neighbour's: {"scene", "barrier", "parts": [part names, "" = the
#              root], "lender": {"barrier", "at"?}, "mode"}. mode "absolute" copies the lender's end value on every axis the
#              lender moves (Equator: x = +-2.0), "offset" adds the lender's travel (Bay5: y + 3.089). The borrower must carry
#              no DotAnimator itself, or the tool stops. mode "scene" (AirPit's bus doors) takes no motion at all: the open pose
#              of every node below a borrower part is the CURRENT local transform of the same-named node below the lender
#              (`lender`: {"barrier", "part", "twins": [parts that must carry the same poses]}), the closed pose the borrower's
#              own; `seconds` and `ease` are the port's (Arena authors no duration for these doors).
DOT_SOURCES = {
    "arena_equator": {
        "scene": "Arena_Equator_TeamFight_DesignStuff",
        "descriptor": REPO / "tools" / "maps" / "equator.json",
        "scans": [{"root": "SPAWN_DOORS", "depth": 2, "at": True}],
        "borrowed": [{
            "scene": "Arena_Equator",
            "barrier": "SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)",
            "parts": ["Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R"],
            "lender": {"barrier": "SPAWN_DOORS/Door_Sliding_Respawn (1)"},
            "mode": "absolute",
            "at": True,
        }],
    },
    "arena_bay5": {
        "scene": "Arena_Bay5_TeamFight_DesignStuff",
        "descriptor": REPO / "tools" / "maps" / "bay5.json",
        # Door_01 (3) is NOT borrowed any more (user decision 2026-10-24): Arena authors no motion and no sound for it and a tarp
        # with a carton stack stands in its doorway, so it stays shut like Door_01 and Door_01 (1).
        "scans": [{"root": "WAREHOUSE_DOORS (1)", "depth": 2, "at": False},
                  {"root": "OLD_WAREHOUSE_DOORS", "depth": 2, "at": True}],
    },
    "arena_airpit": {
        "scene": "Arena_Airpit_TeamFight_DesignStuff",
        "descriptor": REPO / "tools" / "maps" / "airpit.json",
        # PASSPORT_CONTROL_DOORS is the barrier itself: its 16 turnstile leaves (kind 2) are the parts, RED / GREEN carry the lamp
        # switches (kind 3, RedFog off / GreenFog on).
        "scans": [{"root": "PASSPORT_CONTROL_DOORS", "depth": 1, "at": False, "switches": True}],
        "borrowed": [{
            "scene": "Arena_Airpit_TeamFight_DesignStuff",
            "barrier": "BUS_DOORS",
            "parts": ["Apron_bus_MAZ_doors", "Apron_bus_MAZ_doors (1)", "Apron_bus_MAZ_doors (2)"],
            "lender": {"barrier": "BUS_DOORS", "part": "Apron_bus_MAZ_doors (3)",
                       "twins": ["Apron_bus_MAZ_doors (4)", "Apron_bus_MAZ_doors (5)"]},
            "mode": "scene",
            "seconds": 3.0,
            "ease": EASE_IN_OUT_QUINT,
            "at": False,
        }],
    },
}
DOT_SCRIPT = "CommonAssets.Scripts.Game.Modes.Data.DotAnimator"
DOTWEEN_EASE = {1: EASE_LINEAR, 16: EASE_IN_OUT_QUINT, 37: EASE_CURVE}   # DOTween.Ease numbers (EFT's Managed/DOTween.dll)
DOT_KIND_MOVE, DOT_KIND_ROTATE, DOT_KIND_SWITCH = 1, 2, 3
DOT_HEADER_WORDS = 5
DOT_ELEMENT_WORDS = 53
DOT_KEY_WORDS = 7
DOT_PAYLOAD_WORDS = 38
DOT_WRAP = (2, 2, 4)
DOT_SWITCH_WORD = 8       # payload word of a kind-3 element that holds the active state it sets (0 / 1)

# Tolerances (docs/progress/door-poses.md).
CLOSED_POS_TOL = 0.02     # m, closed pose of the scene against the closed clip / the open clip's first key
CLOSED_ROT_TOL = 1.0      # degrees
# Beyond the strict tolerance a node is a WARNING (an authoring offset: BTR wheels sit up to 4 cm off their clip's closed
# value); beyond the hard one the hash resolution or a frame convention is wrong and the tool stops.
HARD_POS_TOL = 0.10       # m
HARD_ROT_TOL = 5.0        # degrees
SAME_ROT = 0.05           # degrees, two rotations that count as identical
DIFF_POS = 0.01           # m, an open pose closer than this to the closed pose is not a move
DIFF_ROT = 1.0            # degrees
DIFF_SCALE = 0.01         # relative; a scale change is reported, the sidecar has no scale
FRAME_PART = "frame"      # BoothDoorRepairs.FramePart: a part whose name contains it is never switched off
DECIMALS = 3
CLOSED_MATCH_TOL = 0.05   # m, rows of one repeated part name are told apart by their closed local position (AirPit's passport leaves;
                          # the client's DoorPoseModel.SelectRowsForNode uses the same tolerance)

BIND_POSITION, BIND_ROTATION, BIND_SCALE, BIND_EULER = 1, 2, 3, 4
BIND_WIDTH = {BIND_POSITION: 3, BIND_ROTATION: 4, BIND_SCALE: 3, BIND_EULER: 3}
BIND_NAME = {BIND_POSITION: "position", BIND_ROTATION: "rotation", BIND_SCALE: "scale", BIND_EULER: "euler"}
TRANSFORM_TYPE_ID = 4


class DoorPoseError(Exception):
    """A violated contract of the data (fails closed: no sidecar is written)."""


# ---------------------------------------------------------------------------------------------------------------
# Pure helpers (unit-tested without Arena)
# ---------------------------------------------------------------------------------------------------------------

def crc32_path(path: str) -> int:
    """Unity's hash of a transform path in a clip binding: zlib CRC-32 of the UTF-8 bytes, 0 for the empty path."""
    return zlib.crc32(path.encode("utf-8")) & 0xFFFFFFFF if path else 0


def quat_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def quat_rotate(q, v):
    """Rotate the vector v by the quaternion q (x, y, z, w)."""
    x, y, z, w = q
    r = quat_mul(quat_mul(q, (v[0], v[1], v[2], 0.0)), (-x, -y, -z, w))
    return (r[0], r[1], r[2])


def world_position_of(chain):
    """World position of the last entry of a chain [(local position, local rotation xyzw, local scale), ...] that runs from the
    scene root down: every level adds its position, scaled and rotated by everything above it."""
    pos, rot, scl = (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)
    for lp, lr, ls in chain:
        moved = quat_rotate(rot, (lp[0] * scl[0], lp[1] * scl[1], lp[2] * scl[2]))
        pos = (pos[0] + moved[0], pos[1] + moved[1], pos[2] + moved[2])
        rot = quat_mul(rot, tuple(lr))
        scl = (scl[0] * ls[0], scl[1] * ls[1], scl[2] * ls[2])
    return pos


def euler_to_quat(euler):
    """Unity's Quaternion.Euler: z first, then x, then y (q = qy * qx * qz)."""
    ex, ey, ez = (math.radians(v) * 0.5 for v in euler)
    qx = (math.sin(ex), 0.0, 0.0, math.cos(ex))
    qy = (0.0, math.sin(ey), 0.0, math.cos(ey))
    qz = (0.0, 0.0, math.sin(ez), math.cos(ez))
    return quat_mul(quat_mul(qy, qx), qz)


def quat_to_euler(q):
    """Unity's ZXY convention, degrees in [0, 360); at the x = +-90 degree gimbal lock z is 0 and y takes the heading."""
    x, y, z, w = q
    norm = math.sqrt(x * x + y * y + z * z + w * w)
    if norm == 0.0:
        raise DoorPoseError("zero quaternion")
    x, y, z, w = x / norm, y / norm, z / norm, w / norm
    test = x * w - y * z
    if test > 0.4995:
        ex, ey, ez = math.pi / 2, 2.0 * math.atan2(y, x), 0.0
    elif test < -0.4995:
        ex, ey, ez = -math.pi / 2, -2.0 * math.atan2(y, x), 0.0
    else:
        ex = math.asin(max(-1.0, min(1.0, 2.0 * (w * x - y * z))))
        ey = math.atan2(2.0 * w * y + 2.0 * z * x, 1.0 - 2.0 * (x * x + y * y))
        ez = math.atan2(2.0 * w * z + 2.0 * x * y, 1.0 - 2.0 * (z * z + x * x))
    return tuple(math.degrees(v) % 360.0 for v in (ex, ey, ez))


def quat_angle(a, b) -> float:
    """Angle in degrees between two rotations (q and -q are the same rotation)."""
    na = math.sqrt(sum(v * v for v in a))
    nb = math.sqrt(sum(v * v for v in b))
    dot = abs(sum(p * q for p, q in zip(a, b))) / (na * nb)
    return math.degrees(2.0 * math.acos(min(1.0, dot)))


def unwrap_near(angle: float, reference: float) -> float:
    """The representative of `angle` (mod 360) closest to `reference`."""
    return angle - 360.0 * round((angle - reference) / 360.0)


def centered(angle: float) -> float:
    """An angle in (-180, 180]."""
    a = angle % 360.0
    return a - 360.0 if a > 180.0 else a


def tidy(values: Iterable[float], decimals: int = DECIMALS) -> list[float]:
    """Round and normalise negative zero, so the sidecar is byte-stable."""
    return [round(v, decimals) + 0.0 for v in values]


def is_closed_clip_name(name: str) -> bool:
    return name.lower().endswith("closed")


def pick_open_clip(names: list[str]) -> str:
    """The open clip of a controller: the one name that does not end in "closed" (case-insensitive)."""
    candidates = [n for n in names if not is_closed_clip_name(n)]
    if len(candidates) != 1:
        raise DoorPoseError(f"cannot pick the open clip of {names}: {len(candidates)} names do not end in 'closed'")
    return candidates[0]


def decode_streamed(words) -> list[tuple[float, list[tuple[int, float, float, float, float]]]]:
    """Frames of a Mecanim StreamedClip: per frame float32 time and uint32 key count, per key the uint32 curve index
    and four float32 cubic coefficients (value(t) = a*dt^3 + b*dt^2 + c*dt + d after the key, d the value AT the key).
    The terminating frame (time = +inf) is dropped."""
    raw = bytes(words) if isinstance(words, (bytes, bytearray)) else struct.pack("<%dI" % len(words), *words)
    frames = []
    off = 0
    while off + 8 <= len(raw):
        t, count = struct.unpack_from("<fI", raw, off)
        off += 8
        keys = []
        for _ in range(count):
            if off + 20 > len(raw):
                raise DoorPoseError("truncated StreamedClip")
            idx, a, b, c, d = struct.unpack_from("<I4f", raw, off)
            off += 20
            keys.append((idx, a, b, c, d))
        if math.isfinite(t):
            frames.append((t, keys))
    return frames


@dataclass
class Curve:
    """One scalar curve: cubic keys (time, a, b, c, d), a sampled dense curve or a constant."""
    keys: list = field(default_factory=list)
    samples: list = field(default_factory=list)
    rate: float = 0.0
    begin: float = 0.0
    constant: float | None = None

    def start(self) -> float:
        if self.constant is not None:
            return self.constant
        return self.keys[0][4] if self.keys else (self.samples[0] if self.samples else 0.0)

    def end(self) -> float:
        if self.constant is not None:
            return self.constant
        return self.keys[-1][4] if self.keys else (self.samples[-1] if self.samples else 0.0)

    def at(self, t: float) -> float:
        if self.constant is not None:
            return self.constant
        if self.keys:
            current = None
            for key in self.keys:
                if key[0] <= t + 1e-9:
                    current = key
                else:
                    break
            if current is None:
                return self.keys[0][4]
            dt = t - current[0]
            _, a, b, c, d = current
            if not all(math.isfinite(v) for v in (a, b, c)):
                return d
            return ((a * dt + b) * dt + c) * dt + d
        if self.samples:
            position = max(0.0, (t - self.begin) * self.rate)
            lo = min(int(position), len(self.samples) - 1)
            hi = min(lo + 1, len(self.samples) - 1)
            frac = position - lo
            return self.samples[lo] * (1.0 - frac) + self.samples[hi] * frac
        return 0.0


@dataclass
class Channel:
    """The curves of one binding: position / rotation (quaternion) / scale / euler of one node."""
    path_hash: int
    kind: int
    curves: list

    @property
    def values_end(self):
        return [c.end() for c in self.curves]

    @property
    def values_start(self):
        return [c.start() for c in self.curves]

    def values_at(self, t: float):
        return [c.at(t) for c in self.curves]


@dataclass
class Clip:
    name: str
    stop: float
    channels: list
    unsupported: int = 0     # bindings that are not Transform position/rotation/scale/euler curves


def build_clip(tt: dict) -> Clip:
    """A decoded Clip from an AnimationClip typetree (UnityPy read_typetree())."""
    muscle = tt["m_MuscleClip"]
    data = muscle["m_Clip"]["data"]
    streamed, dense, const = data["m_StreamedClip"], data["m_DenseClip"], data["m_ConstantClip"]
    n_streamed = int(streamed["curveCount"])
    n_dense = int(dense["m_CurveCount"])
    constants = list(const.get("data") or [])
    curves: dict[int, Curve] = {}
    for time, keys in decode_streamed(streamed["data"]):
        for idx, a, b, c, d in keys:
            if idx >= n_streamed:
                raise DoorPoseError(f"{tt['m_Name']}: streamed key for curve {idx} >= curveCount {n_streamed}")
            curves.setdefault(idx, Curve()).keys.append((time, a, b, c, d))
    if n_dense:
        samples = list(dense["m_SampleArray"])
        frames = int(dense["m_FrameCount"])
        if len(samples) != frames * n_dense:
            raise DoorPoseError(f"{tt['m_Name']}: dense sample count {len(samples)} != {frames} x {n_dense}")
        for i in range(n_dense):
            curves[n_streamed + i] = Curve(samples=samples[i::n_dense], rate=float(dense["m_SampleRate"]),
                                           begin=float(dense["m_BeginTime"]))
    for i, value in enumerate(constants):
        curves[n_streamed + n_dense + i] = Curve(constant=float(value))
    channels, unsupported, cursor = [], 0, 0
    for binding in tt["m_ClipBindingConstant"]["genericBindings"]:
        kind = int(binding["attribute"])
        width = BIND_WIDTH.get(kind, 1)
        if int(binding["typeID"]) != TRANSFORM_TYPE_ID or kind not in BIND_WIDTH or binding.get("isPPtrCurve"):
            unsupported += 1
            cursor += width
            continue
        group = []
        for i in range(cursor, cursor + width):
            if i not in curves:
                raise DoorPoseError(f"{tt['m_Name']}: curve {i} of binding {int(binding['path'])}/{kind} has no data")
            group.append(curves[i])
        channels.append(Channel(int(binding["path"]), kind, group))
        cursor += width
    total = n_streamed + n_dense + len(constants)
    if cursor != total:
        raise DoorPoseError(f"{tt['m_Name']}: bindings span {cursor} curves, the clip stores {total}")
    return Clip(tt["m_Name"], float(muscle["m_StopTime"]), channels, unsupported)


def key_residual(clip: Clip) -> tuple[int, float, int]:
    """Self-check of the StreamedClip decode: the cubic of a key, evaluated at the next key's time, must reach the next key's
    value (a wrong coefficient order or curve index cannot pass). Returns (key spans checked, worst absolute error, spans
    beyond 1e-4 + 1e-5 * |value|). Stepped keys (infinite slopes) jump at the next key and are skipped."""
    spans, worst, bad = 0, 0.0, 0
    for channel in clip.channels:
        for curve in channel.curves:
            keys = [k for k in curve.keys if k[0] > -1e30]    # the first "key" of every curve sits at -FLT_MAX
            for a, b in zip(keys, keys[1:]):
                if not all(math.isfinite(v) for v in a[1:4]):
                    continue
                dt = b[0] - a[0]
                error = abs(((a[1] * dt + a[2]) * dt + a[3]) * dt + a[4] - b[4])
                spans += 1
                worst = max(worst, error)
                if error > 1e-4 + 1e-5 * abs(b[4]):
                    bad += 1
    return spans, worst, bad


# ---------------------------------------------------------------------------------------------------------------
# DotAnimator decoder (pure: unit-tested on synthetic bytes)
# ---------------------------------------------------------------------------------------------------------------

@dataclass
class DotElement:
    """One tween of a DotAnimator: what moves (a PPtr to a GameObject), for how long, how, and to where."""
    target_file: int
    target_path: int
    unknown: float            # word 3: -1 / -2 / 0, meaning unknown
    seconds: float            # word 4, unscaled
    kind: int                 # word 5: 1 move, 2 rotate, 3 switch
    ease: int                 # word 7: DOTween Ease number
    keys: list = field(default_factory=list)      # (time, value, inSlope, outSlope, weightedMode, inWeight, outWeight)
    target: tuple = (0.0, 0.0, 0.0)               # payload words 1-3: the absolute local end vector
    payload: tuple = ()                           # the 38 payload words

    @property
    def active(self) -> bool | None:
        """The active state a kind-3 (switch) element sets: payload word 8; None for a move / rotate element."""
        if self.kind != DOT_KIND_SWITCH:
            return None
        return bool(self.payload[DOT_SWITCH_WORD])


@dataclass
class DotAnimator:
    game_object: int          # path id of the GameObject the component sits on
    script: tuple             # (file id, path id) of the MonoScript
    name: str
    header: tuple             # the first four header words
    elements: list = field(default_factory=list)


def parse_mono_head(raw: bytes):
    """(GameObject file, GameObject path, enabled, script file, script path, name, tail offset) of a MonoBehaviour's raw data:
    m_GameObject PPtr (int32 + int64), m_Enabled (4 bytes with padding), m_Script PPtr, m_Name (int32 length, bytes, 4-aligned)."""
    if len(raw) < 32:
        raise DoorPoseError(f"MonoBehaviour of {len(raw)} bytes is shorter than its head")
    gf, gp, enabled, sf, sp, nlen = struct.unpack_from("<iqiiqi", raw, 0)
    if nlen < 0 or 32 + nlen > len(raw):
        raise DoorPoseError(f"MonoBehaviour name length {nlen} is out of range")
    return gf, gp, enabled, sf, sp, raw[32:32 + nlen].decode("utf-8", "replace"), (32 + nlen + 3) & ~3


def decode_dot_animator(raw: bytes) -> DotAnimator:
    """The DotAnimator in the raw data of a MonoBehaviour whose script is DotAnimator. Fails closed (DoorPoseError) unless the
    offset after the last field equals the object's byte length, the header, the curve wrap words and every kind are the
    known ones and every number is finite: a layout that merely looks right must not reach a sidecar."""
    gf, gp, _enabled, sf, sp, name, off = parse_mono_head(raw)
    tail = raw[off:]
    if len(tail) % 4:
        raise DoorPoseError(f"DotAnimator tail of {len(tail)} bytes is not a whole number of words")
    n = len(tail) // 4
    words = struct.unpack("<%di" % n, tail)
    floats = struct.unpack("<%df" % n, tail)
    if n < DOT_HEADER_WORDS:
        raise DoorPoseError("DotAnimator tail is shorter than its header")
    if tuple(words[:3]) != (0, 1, 0):
        raise DoorPoseError(f"DotAnimator header words {words[:4]} are not (0, 1, 0, flag)")
    count = words[4]
    if not 0 <= count <= 256:
        raise DoorPoseError(f"DotAnimator element count {count} is out of range")
    pos = DOT_HEADER_WORDS
    elements = []
    for index in range(count):
        if pos + 12 > n:
            raise DoorPoseError(f"DotAnimator element {index} is cut off")
        nkeys = words[pos + 11]
        if not 0 <= nkeys <= 256:
            raise DoorPoseError(f"DotAnimator element {index}: curve key count {nkeys} is out of range")
        end = pos + DOT_ELEMENT_WORDS + DOT_KEY_WORDS * nkeys
        if end > n:
            raise DoorPoseError(f"DotAnimator element {index} needs {end - pos} words, {n - pos} are left")
        target_file, target_path = struct.unpack_from("<iq", tail, 4 * pos)
        keys = []
        for i in range(nkeys):
            q = pos + 12 + DOT_KEY_WORDS * i
            keys.append((floats[q], floats[q + 1], floats[q + 2], floats[q + 3], words[q + 4], floats[q + 5], floats[q + 6]))
        w = pos + 12 + DOT_KEY_WORDS * nkeys
        if tuple(words[w:w + 3]) != DOT_WRAP:
            raise DoorPoseError(f"DotAnimator element {index}: curve wrap words {words[w:w + 3]} are not {DOT_WRAP}")
        payload = tuple(words[w + 3:w + 3 + DOT_PAYLOAD_WORDS])
        element = DotElement(target_file, target_path, floats[pos + 3], floats[pos + 4], words[pos + 5], words[pos + 7], keys,
                             tuple(floats[w + 4:w + 7]), payload)
        if element.kind not in (DOT_KIND_MOVE, DOT_KIND_ROTATE, DOT_KIND_SWITCH):
            raise DoorPoseError(f"DotAnimator element {index}: unknown kind {element.kind}")
        numbers = [element.unknown, element.seconds, *element.target] + [v for k in keys for v in (k[0], k[1], k[2], k[3], k[5], k[6])]
        if not all(math.isfinite(v) for v in numbers):
            raise DoorPoseError(f"DotAnimator element {index} carries a non-finite number")
        if element.kind != DOT_KIND_SWITCH and element.seconds <= 0:
            raise DoorPoseError(f"DotAnimator element {index}: duration {element.seconds} is not positive")
        if element.kind == DOT_KIND_SWITCH and (element.seconds < 0 or payload[DOT_SWITCH_WORD] not in (0, 1)):
            raise DoorPoseError(f"DotAnimator element {index}: a switch needs a duration >= 0 and an active state of 0 or 1 in payload "
                                f"word {DOT_SWITCH_WORD}, got {element.seconds} and {payload[DOT_SWITCH_WORD]}")
        elements.append(element)
        pos = end
    if pos != n:
        raise DoorPoseError(f"DotAnimator decode ends at word {pos}, the object has {n} words: the layout is wrong")
    return DotAnimator(gp, (sf, sp), name, tuple(words[:4]), elements)


def dot_ease_name(number: int) -> str:
    """The sidecar ease of a DOTween Ease number; anything but Linear (1), InOutQuint (16) and the custom curve (37) stops the tool."""
    if number not in DOTWEEN_EASE:
        raise DoorPoseError(f"DOTween ease {number} is not one of {dict(sorted(DOTWEEN_EASE.items()))}: add it to the "
                            "sidecar schema and the client before porting this gate")
    return DOTWEEN_EASE[number]


def curve_from_keys(keys: list) -> list[list[float]]:
    """[time, value, inSlope, outSlope] per key of an ease-37 curve, on a 0..1 timeline from value 0 to value 1; anything else
    (fewer than two keys, unsorted times, a curve that does not start at 0 or end at 1) stops the tool."""
    if len(keys) < 2:
        raise DoorPoseError(f"a custom ease curve needs at least 2 keys, got {len(keys)}")
    if any(k[4] != 0 for k in keys):
        raise DoorPoseError("a custom ease curve with weighted tangents is not supported (the sidecar stores slopes only)")
    curve = [[round(k[0], CURVE_DECIMALS) + 0.0, round(k[1], CURVE_DECIMALS) + 0.0,
              round(k[2], CURVE_DECIMALS) + 0.0, round(k[3], CURVE_DECIMALS) + 0.0] for k in keys]
    times = [k[0] for k in curve]
    if any(b <= a for a, b in zip(times, times[1:])):
        raise DoorPoseError(f"custom ease curve times {times} are not increasing")
    if abs(times[0]) > 1e-4 or abs(times[-1] - 1.0) > 1e-4:
        raise DoorPoseError(f"custom ease curve runs {times[0]}..{times[-1]}, not 0..1")
    if abs(curve[0][1]) > 1e-3 or abs(curve[-1][1] - 1.0) > 1e-3:
        raise DoorPoseError(f"custom ease curve runs from {curve[0][1]} to {curve[-1][1]}, not from 0 to 1")
    return curve


def hermite_value(curve: list, t: float) -> float:
    """Unity AnimationCurve.Evaluate of unweighted keys [time, value, inSlope, outSlope]: a cubic Hermite between the two keys
    around t, using the left key's out slope and the right key's in slope; the first/last value outside the keys."""
    if t <= curve[0][0]:
        return curve[0][1]
    for a, b in zip(curve, curve[1:]):
        if a[0] <= t <= b[0]:
            dt = b[0] - a[0]
            s = (t - a[0]) / dt
            h00 = 2 * s ** 3 - 3 * s ** 2 + 1
            h10 = s ** 3 - 2 * s ** 2 + s
            h01 = -2 * s ** 3 + 3 * s ** 2
            h11 = s ** 3 - s ** 2
            return h00 * a[1] + h10 * dt * a[3] + h01 * b[1] + h11 * dt * b[2]
    return curve[-1][1]


def ease_fraction(ease: str, curve: list | None, k: float) -> float:
    """How far along its path a part is after the fraction `k` of its duration (the formulas DOTween uses; the client mirrors
    them in DoorPoseModel)."""
    k = min(1.0, max(0.0, k))
    if ease == EASE_LINEAR:
        return k
    if ease == EASE_SMOOTHSTEP:
        return k * k * (3.0 - 2.0 * k)
    if ease == EASE_IN_OUT_QUINT:
        t = 2.0 * k
        return 0.5 * t ** 5 if t < 1.0 else 0.5 * ((t - 2.0) ** 5 + 2.0)
    if ease == EASE_CURVE:
        if not curve:
            raise DoorPoseError("ease 'curve' without keys")
        return hermite_value(curve, k)
    raise DoorPoseError(f"unknown ease {ease!r}")


@dataclass
class NodePose:
    """Closed and open pose of one node, local to the node's parent (Unity localPosition / localEulerAngles).

    source / ease / curve / at are the schema-2 fields: where the pose comes from (a Mecanim clip, a DotAnimator, a
    neighbour's), the interpolation (Bowl's clips keep the client's smoothstep stand-in), the Hermite keys of ease "curve"
    ([time, value, inSlope, outSlope] on a 0..1 timeline) and the barrier root's world position for repeated paths. `scene`
    is the Arena scene the node was read from (package check only, not written). `switch` = (closed active, open active)
    makes the row a SWITCH: the node is switched, not moved; its closed and open transforms are the scene transform."""
    barrier: str
    part: str
    node: str
    clip: str
    seconds: float
    closed_position: list
    closed_euler: list
    open_position: list
    open_euler: list
    moved: bool = True
    source: str = SOURCE_CLIP
    ease: str = EASE_SMOOTHSTEP
    curve: list | None = None
    at: list | None = None
    scene: str = ""
    switch: tuple | None = None     # (closed active, open active) of a SetActive node (a lamp); None for a transform pose


def continuous_euler(channel: Channel, stop: float, start_euler, per_second: int = 240):
    """End euler of a QUATERNION rotation channel, unwrapped over the whole clip so that a spin of several turns
    (a rolling wheel) survives: every sample's euler is moved to the branch nearest to the previous sample's."""
    previous = list(start_euler)
    steps = max(1, int(round(stop * per_second)))
    for i in range(steps + 1):
        q = channel.values_at(stop * i / steps)
        previous = [unwrap_near(a, p) for a, p in zip(quat_to_euler(q), previous)]
    return previous


def solve_node(scene_pos, scene_quat, scene_scale, channels: dict, closed_channels: dict, label: str, stop: float):
    """Closed/open pose of one node from its open-clip channels (kind -> Channel), the matching `*_closed` clip
    channels and the scene transform. Returns (pose dict, issues list of (level, text), scale_changed bool); level is
    "warn" for a clip/scene offset beyond the strict tolerance and "error" beyond the hard one."""
    issues = []

    def judge(kind, delta, strict, hard, unit, detail):
        if delta > hard:
            issues.append(("error", f"{label}: {kind} {detail} differs from the scene by {delta:.3f} {unit} (hard limit {hard})"))
        elif delta > strict:
            issues.append(("warn", f"{label}: {kind} {detail} differs from the scene by {delta:.3f} {unit} (strict {strict})"))

    open_pos = list(scene_pos)
    open_scale = list(scene_scale)
    closed_euler = None
    open_euler = None
    open_quat = tuple(scene_quat)
    if BIND_POSITION in channels:
        open_pos = channels[BIND_POSITION].values_at(stop)
        start_pos = (closed_channels[BIND_POSITION].values_start if BIND_POSITION in closed_channels
                     else channels[BIND_POSITION].values_start)
        judge("closed position", math.dist(start_pos, scene_pos), CLOSED_POS_TOL, HARD_POS_TOL, "m", tidy(start_pos))
    if BIND_SCALE in channels:
        open_scale = channels[BIND_SCALE].values_at(stop)
    if BIND_EULER in channels:
        raw_open = channels[BIND_EULER].values_at(stop)
        # the raw branch the Animator writes (multi-turn spins survive); the closed side from the closed clip, else the
        # open clip's first key. judge() is the cross-check that the scene rotation is that raw closed euler.
        raw_closed = (closed_channels[BIND_EULER].values_start if BIND_EULER in closed_channels
                      else channels[BIND_EULER].values_start)
        angle = quat_angle(euler_to_quat(raw_closed), scene_quat)
        judge("closed euler", angle, CLOSED_ROT_TOL, HARD_ROT_TOL, "deg", tidy(raw_closed))
        closed_euler = list(raw_closed)
        if angle > SAME_ROT:
            # the scene's own rotation, on the clip's branch: the part is exactly where the scene has it at s = 0
            near = [unwrap_near(v, ref) for v, ref in zip(quat_to_euler(scene_quat), raw_closed)]
            if quat_angle(euler_to_quat(near), scene_quat) <= SAME_ROT:
                closed_euler = near
        open_euler = list(raw_open)
        open_quat = euler_to_quat(raw_open)
    elif BIND_ROTATION in channels:
        open_quat = tuple(channels[BIND_ROTATION].values_at(stop))
        norm = math.sqrt(sum(v * v for v in open_quat))
        open_quat = tuple(v / norm for v in open_quat)
        start_q = (closed_channels[BIND_ROTATION].values_start if BIND_ROTATION in closed_channels
                   else channels[BIND_ROTATION].values_start)
        judge("closed rotation", quat_angle(start_q, scene_quat), CLOSED_ROT_TOL, HARD_ROT_TOL, "deg", tidy(start_q))
    if closed_euler is None:
        closed_euler = [centered(v) for v in quat_to_euler(scene_quat)]
        if BIND_ROTATION in channels and any(c.constant is None for c in channels[BIND_ROTATION].curves):
            # an animated quaternion channel: follow it through the clip so multi-turn spins (wheels) are kept
            open_euler = continuous_euler(channels[BIND_ROTATION], stop, closed_euler)
        else:
            # nearest branch to the closed angles, so a component-wise interpolation takes the short way
            open_euler = [unwrap_near(v, ref) for v, ref in zip(quat_to_euler(open_quat), closed_euler)]
    scale_changed = any(abs(a - b) > DIFF_SCALE * max(1.0, abs(b)) for a, b in zip(open_scale, scene_scale))
    pos_delta = math.dist(open_pos, scene_pos)
    rot_delta = quat_angle(scene_quat, open_quat)
    raw_delta = max(abs(a - b) for a, b in zip(open_euler, closed_euler))
    moved = pos_delta > DIFF_POS or rot_delta > DIFF_ROT or raw_delta > DIFF_ROT
    return {"closed_position": list(scene_pos), "closed_euler": closed_euler, "open_position": open_pos,
            "open_euler": open_euler, "moved": moved, "pos_delta": pos_delta, "rot_delta": rot_delta}, issues, scale_changed


def motion_window(channels: Iterable[Channel], stop: float, rate: float = 60.0, eps: float = 1e-3):
    """(first time a channel leaves its start value, time after which none changes any more), by sampling."""
    steps = max(1, int(round(stop * rate)))
    times = [stop * i / steps for i in range(steps + 1)]
    channels = list(channels)
    first, last = None, None
    for t in times:
        moving = any(abs(c.at(t) - c.start()) > eps for ch in channels for c in ch.curves)
        notdone = any(abs(c.at(t) - c.end()) > eps for ch in channels for c in ch.curves)
        if moving and first is None:
            first = t
        if notdone:
            last = t
    return (first if first is not None else 0.0), (min(stop, last + stop / steps) if last is not None else 0.0)


def sidecar_pose(p: NodePose) -> dict:
    row = {
        "barrier": p.barrier, "part": p.part, "node": p.node, "clip": p.clip, "source": p.source,
        "seconds": round(p.seconds, DECIMALS) + 0.0, "ease": p.ease,
        "curve": None if p.curve is None else [[round(v, CURVE_DECIMALS) + 0.0 for v in key] for key in p.curve],
        "at": None if p.at is None else tidy(p.at),
        "closed": {"position": tidy(p.closed_position), "euler": tidy(p.closed_euler)},
        "open": {"position": tidy(p.open_position), "euler": tidy(p.open_euler)},
    }
    if p.switch is not None:          # only a switch row carries the key; the transform rows of the other sidecars stay as they were
        row["switch"] = {"closed": bool(p.switch[0]), "open": bool(p.switch[1])}
    return row


def csharp_float(value: float) -> str:
    text = repr(float(value))
    if "e" in text or "E" in text:
        raise DoorPoseError(f"cannot spell {value!r} as a plain C# float literal")
    return text + "f"


def has_default_extras(p: dict) -> bool:
    """A pose from a Mecanim clip with the smoothstep stand-in, no curve, no `at` and no switch: ArenaDoorPose's last four
    constructor parameters take their defaults, so Bowl's literals stay the nine-argument form."""
    return (p.get("source", SOURCE_CLIP) == SOURCE_CLIP and p.get("ease", EASE_SMOOTHSTEP) == EASE_SMOOTHSTEP
            and p.get("curve") is None and p.get("at") is None and p.get("switch") is None)


def render_csharp(poses: list[dict], indent: str = " " * 16) -> str:
    """The `new ArenaDoorPose(...)` literals of ArenaMapRegistry.<Map>.DoorPoses for a sidecar's poses, ready to paste between
    `DoorPoses: new[] {` and `}` (a test pins that the registry carries exactly this text). A default pose (clip source,
    smoothstep, no curve, no `at`) is three lines of nine arguments; every other pose adds a fourth line with the last four
    arguments `source, ease, curve, at` (curve = a flat float[] of time, value, inSlope, outSlope per key, or null). A switch
    row appends the named arguments `ClosedActive: true, OpenActive: false` to that fourth line (the constructor's optional
    trailing parameters; a transform row never prints them)."""
    def vec(values):
        return "new[] { " + ", ".join(csharp_float(v) for v in values) + " }"

    lines = []
    for p in poses:
        default = has_default_extras(p)
        lines.append(f"{indent}new ArenaDoorPose({json.dumps(p['barrier'])}, {json.dumps(p['part'])}, {json.dumps(p['node'])}, "
                     f"{json.dumps(p['clip'])}, {csharp_float(p['seconds'])},")
        lines.append(f"{indent}    {vec(p['closed']['position'])}, {vec(p['closed']['euler'])},")
        if default:
            lines.append(f"{indent}    {vec(p['open']['position'])}, {vec(p['open']['euler'])}),")
            continue
        lines.append(f"{indent}    {vec(p['open']['position'])}, {vec(p['open']['euler'])},")
        curve = p.get("curve")
        curve_text = "null" if curve is None else vec([v for key in curve for v in key])
        at = p.get("at")
        at_text = "null" if at is None else vec(at)
        switch = p.get("switch")
        switch_text = "" if switch is None else (f", ClosedActive: {str(bool(switch['closed'])).lower()}, "
                                                 f"OpenActive: {str(bool(switch['open'])).lower()}")
        lines.append(f"{indent}    {json.dumps(p.get('source', SOURCE_CLIP))}, {json.dumps(p.get('ease', EASE_SMOOTHSTEP))}, "
                     f"{curve_text}, {at_text}{switch_text}),")
    return "\n".join(lines)


SIDECAR_COMMENT = (
    "Generated by tools/extract/extract_door_poses.py (docs/progress/door-poses.md): the END POSE of every node "
    "Arena's gates move, next to the node's closed pose in the scene. barrier = the path of the gate's root "
    "(an opened_barriers entry; Equator's booth doors are not barriers: the assembly root under SPAWN_DOORS or "
    "Respawn_2), part = the barrier root's direct child (what BoothDoorRepairs switches off; \"\" = the root "
    "itself moves), node = the transform path below the part that is animated (\"\" = the part itself), "
    "positions and euler angles are LOCAL to the node's parent (Unity localPosition / localEulerAngles). "
    "source = clip (a Mecanim clip: Bowl), dotanimator (Arena's DOTween script: Equator, Bay5) or borrowed "
    "(Arena authors no motion for this gate; a neighbour's is used). seconds = Arena's own duration, unscaled. "
    "ease = smoothstep (the client's stand-in for Bowl's clips), linear, inOutQuint or curve (curve = Hermite "
    "keys [time, value, inSlope, outSlope] on a 0..1 timeline). at = null, or the WORLD position of the "
    "barrier root instance (tells repeated hierarchy paths apart, matched within 0.05 m). Euler angles of clip "
    "poses are the clip's own raw values (interpolate component-wise); rotations of dotanimator poses are "
    "interpolated as a quaternion slerp between the two triples. "
    "ArenaMapRegistry.<Map>.DoorPoses mirrors this file number for number (validate_map_descriptors.py and "
    "the DoorPoseSidecarTests). Object names and derived numbers only (the curve keys are derived numbers too); no Arena asset "
    "or file is stored.")

# Appended only to a sidecar that uses the AirPit features (a switch row, a repeated part name), so the Equator / Bay5 / Bowl
# files keep their text byte for byte.
SIDECAR_COMMENT_SWITCHES = (
    " A row with a \"switch\" key {closed, open} switches the node on or off instead of moving it (AirPit's passport lamps: "
    "closed = the node's active state in the scene, open = the state Arena's DotAnimator sets; the row's transforms are the "
    "node's scene transform on both sides, seconds is Arena's duration word, 0 for a switch). A part name may repeat among a "
    "barrier's direct children (the 16 passport leaves reuse 6 names): such rows are told apart by their closed position, "
    "matched within 0.05 m. A borrowed row whose clip says \"static open pose\" takes the open pose from a neighbour that stands "
    "open in the scene (AirPit's bus doors); its seconds are the port's, not Arena's.")


def render_sidecar(key: str, source: dict, poses: list[dict]) -> str:
    identities = [(p["barrier"], p["part"], p["node"], tuple(p["at"]) if p.get("at") else None) for p in poses]
    extra = any(p.get("switch") is not None for p in poses) or len(set(identities)) != len(identities)
    doc = {
        "_comment": SIDECAR_COMMENT + (SIDECAR_COMMENT_SWITCHES if extra else ""),
        "schema_version": SCHEMA_VERSION,
        "key": key,
        "source": source,
        "poses": poses,
    }
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


# ---------------------------------------------------------------------------------------------------------------
# Arena access (UnityPy, read-only)
# ---------------------------------------------------------------------------------------------------------------

def arena_version(data_dir: Path) -> str:
    """The installed Arena build from the launcher manifest (ConsistencyInfo beside the Data directory, the file the
    installation check reads), or '' when it cannot be read."""
    try:
        manifest = json.loads((data_dir.parent / "ConsistencyInfo").read_bytes().decode("utf-8-sig"))
        return str(manifest.get("Version", ""))
    except (OSError, ValueError):
        return ""


def find_arena(arena: str) -> tuple[Path, str]:
    """(EscapeFromTarkovArena_Data directory, installed version or ''). `auto` = the launcher's registry entry, then the
    default location; a folder may be the installation or its Data directory."""
    if arena == "auto":
        location = None
        try:
            import winreg
            for base in (r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                         r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"):
                try:
                    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, base + r"\EscapeFromTarkovArena_live") as key:
                        location = winreg.QueryValueEx(key, "InstallLocation")[0]
                        break
                except OSError:
                    continue
        except ImportError:
            pass
        data = Path(location) / DATA_DIR if location else DEFAULT_DATA
    else:
        data = Path(arena)
        if data.name != DATA_DIR and (data / DATA_DIR).is_dir():
            data = data / DATA_DIR
    if not data.is_dir():
        raise DoorPoseError(f"Arena data directory not found: {data}")
    return data, arena_version(data)


def scene_level_index(map_key: str, scene: str) -> int:
    table = json.loads(KEY_SOURCES.read_text(encoding="utf-8"))["maps"][map_key]
    for entry in table:
        if entry["scene"] == scene:
            return int(entry["index"])
    raise DoorPoseError(f"{scene} is not in {KEY_SOURCES.name} for {map_key}")


class ArenaScene:
    """The GameObject/Transform hierarchy of one level file with its Animators (read-only through UnityPy)."""

    def __init__(self, data_dir: Path, level: int):
        import UnityPy
        level_file = data_dir / f"level{level}"
        shared_file = data_dir / f"sharedassets{level}.assets"
        for path in (level_file, shared_file):
            if not path.is_file():
                raise DoorPoseError(f"missing {path}")
        self.env = UnityPy.load(str(level_file), str(shared_file))
        self.files: dict[str, dict] = {}
        for obj in self.env.objects:
            self.files.setdefault(Path(obj.assets_file.name).name, {})[obj.path_id] = obj
        self.level_name = level_file.name
        self.shared_name = shared_file.name
        if self.level_name not in self.files:
            raise DoorPoseError(f"{self.level_name} holds no objects")
        level_objects = self.files[self.level_name]
        self.go, self.tr = {}, {}
        for pid, obj in level_objects.items():
            kind = obj.type.name
            if kind == "GameObject":
                self.go[pid] = obj.read_typetree()
            elif kind in ("Transform", "RectTransform"):
                self.tr[pid] = obj.read_typetree()
        self.go2tr = {t["m_GameObject"]["m_PathID"]: pid for pid, t in self.tr.items()}
        self.animators = [obj for obj in level_objects.values() if obj.type.name == "Animator"]
        self._paths: dict[int, str] = {}
        self._by_path: dict[str, list] | None = None
        self._script_names: dict[tuple, str] = {}
        self.assets_file = self.animators[0].assets_file if self.animators else None

    def name(self, tpid: int) -> str:
        return self.go[self.tr[tpid]["m_GameObject"]["m_PathID"]]["m_Name"]

    def path(self, tpid: int) -> str:
        if tpid not in self._paths:
            father = self.tr[tpid]["m_Father"]["m_PathID"]
            own = self.name(tpid)
            self._paths[tpid] = f"{self.path(father)}/{own}" if father else own
        return self._paths[tpid]

    def children(self, tpid: int) -> list[int]:
        return [c["m_PathID"] for c in self.tr[tpid]["m_Children"]]

    def descendants(self, tpid: int, prefix: str = "") -> Iterable[tuple[str, int]]:
        for child in self.children(tpid):
            rel = f"{prefix}/{self.name(child)}" if prefix else self.name(child)
            yield rel, child
            yield from self.descendants(child, rel)

    def local(self, tpid: int):
        t = self.tr[tpid]
        p, r, s = t["m_LocalPosition"], t["m_LocalRotation"], t["m_LocalScale"]
        return ((p["x"], p["y"], p["z"]), (r["x"], r["y"], r["z"], r["w"]), (s["x"], s["y"], s["z"]))

    def world_position(self, tpid: int):
        """World position of a transform, composed through the parent chain (rotation and scale of the parents included)."""
        chain = []
        t = tpid
        while t:
            chain.append(self.local(t))
            t = self.tr[t]["m_Father"]["m_PathID"]
        return world_position_of(list(reversed(chain)))

    def find_path(self, path: str) -> list[int]:
        """Every transform whose hierarchy path is `path` (names repeat: the result may hold several instances)."""
        if self._by_path is None:
            self._by_path = {}
            for tpid in self.tr:
                self._by_path.setdefault(self.path(tpid), []).append(tpid)
        return sorted(self._by_path.get(path, []))

    def active_in_hierarchy(self, tpid: int) -> bool:
        """Is the GameObject and every ancestor active (the scene's own flags, as the importer reads them)."""
        t = tpid
        while t:
            if not self.go[self.tr[t]["m_GameObject"]["m_PathID"]]["m_IsActive"]:
                return False
            t = self.tr[t]["m_Father"]["m_PathID"]
        return True

    def mono_class(self, obj) -> str:
        """The full script class name ('Namespace.Class') of a MonoBehaviour, '' when its script cannot be read. The script
        PPtr sits at byte 16 of the raw data; one lookup per distinct script."""
        raw = obj.get_raw_data()
        key = struct.unpack_from("<iq", raw, 16)
        if key not in self._script_names:
            name = ""
            if key[1]:
                try:
                    st = obj.parse_monobehaviour_head().m_Script.read_typetree()
                    name = ((st.get("m_Namespace") or "") + "." if st.get("m_Namespace") else "") + st.get("m_ClassName", "")
                except Exception:
                    name = ""
            self._script_names[key] = name
        return self._script_names[key]

    def monobehaviours(self, tpid: int) -> list:
        """The MonoBehaviour objects on the GameObject of a transform (level file only)."""
        out = []
        for entry in self.go[self.tr[tpid]["m_GameObject"]["m_PathID"]]["m_Component"]:
            ptr = entry["component"]
            if ptr["m_FileID"]:
                continue
            obj = self.files[self.level_name].get(ptr["m_PathID"])
            if obj is not None and obj.type.name == "MonoBehaviour":
                out.append(obj)
        return out

    def animator_clip_counts(self, tpid: int) -> list[int]:
        """The number of clips of the controller of every Animator on the GameObject of a transform (level file only; a
        missing controller counts 0). Empty when the object carries no Animator."""
        go = self.tr[tpid]["m_GameObject"]["m_PathID"]
        counts = []
        for animator in sorted(self.animators, key=lambda o: o.path_id):
            tt = animator.read_typetree()
            if tt["m_GameObject"]["m_PathID"] != go:
                continue
            if not tt["m_Controller"]["m_PathID"]:
                counts.append(0)
                continue
            counts.append(len(self.deref(animator, tt["m_Controller"]).read_typetree()["m_AnimationClips"]))
        return counts

    def deref(self, obj, pptr: dict):
        """The object a PPtr of `obj` points at (same file, or an external file that was loaded)."""
        file_id, path_id = pptr["m_FileID"], pptr["m_PathID"]
        if not path_id:
            raise DoorPoseError("null PPtr")
        if file_id == 0:
            file_name = Path(obj.assets_file.name).name
        else:
            file_name = Path(obj.assets_file.externals[file_id - 1].path).name
        table = self.files.get(file_name)
        if table is None or path_id not in table:
            raise DoorPoseError(f"PPtr target {file_name}:{path_id} is not loaded")
        return table[path_id]


@dataclass
class Gate:
    barrier: str
    part: str
    sub: str                # the Animator's GameObject path below the part ("" = the part itself)
    animator_path: str
    controller: str
    clip_names: list
    open_clip: str
    transitions: list


def split_gate_path(animator_path: str, barriers: list[str]) -> tuple[str, str, str] | None:
    """(barrier, part, sub) of an Animator path, or None when it lies outside every opened barrier or is the barrier
    itself. The barrier is the longest listed prefix."""
    best = None
    for barrier in barriers:
        if animator_path.startswith(barrier + "/") and (best is None or len(barrier) > len(best)):
            best = barrier
    if best is None:
        return None
    rest = animator_path[len(best) + 1:].split("/")
    return best, rest[0], "/".join(rest[1:])


def read_gates(scene: ArenaScene, barriers: list[str]) -> tuple[list[Gate], list[str]]:
    """The gate animators below the opened barriers, and the paths of the Animators ignored."""
    gates, ignored = [], []
    for animator in sorted(scene.animators, key=lambda o: o.path_id):
        tt = animator.read_typetree()
        tpid = scene.go2tr[tt["m_GameObject"]["m_PathID"]]
        path = scene.path(tpid)
        split = split_gate_path(path, barriers)
        if split is None:
            ignored.append(path)
            continue
        barrier, part, sub = split
        if FRAME_PART in part.lower():
            ignored.append(path + "  (frame part, never switched off)")
            continue
        controller = scene.deref(animator, tt["m_Controller"])
        ctt = controller.read_typetree()
        clips = [scene.deref(controller, c).read_typetree()["m_Name"] for c in ctt["m_AnimationClips"]]
        if not clips:
            # AirPit's bus doors: an Animator whose controller carries no clip at all, nothing to port
            ignored.append(f"{path}  (controller {ctt['m_Name']} has no clips)")
            continue
        open_clip = pick_open_clip(clips)
        tos = [name for _, name in ctt.get("m_TOS", [])]
        transitions = [n for n in tos if "->" in n and n.split("->")[-1].strip().endswith(open_clip)]
        gates.append(Gate(barrier, part, sub, path, ctt["m_Name"], clips, open_clip, transitions))
    return gates, ignored


def find_clip(scene: ArenaScene, controller_name: str, clip_name: str) -> dict:
    for obj in scene.files[scene.shared_name].values():
        if obj.type.name == "AnimatorController":
            ctt = obj.read_typetree()
            if ctt["m_Name"] == controller_name:
                for pptr in ctt["m_AnimationClips"]:
                    tt = scene.deref(obj, pptr).read_typetree()
                    if tt["m_Name"] == clip_name:
                        return tt
    raise DoorPoseError(f"clip {clip_name} of controller {controller_name} not found")


@dataclass
class GateResult:
    gate: Gate
    clip: Clip
    nodes: int = 0
    kept: list = field(default_factory=list)       # NodePose
    dropped: list = field(default_factory=list)    # node paths without a net change
    scale_changed: list = field(default_factory=list)
    issues: list = field(default_factory=list)
    unresolved: int = 0
    windows: dict = field(default_factory=dict)    # node -> (start, end)
    constant_curves: int = 0
    animated_curves: int = 0
    spans: int = 0          # key spans of the decode self-check
    worst_residual: float = 0.0


def solve_gate(scene: ArenaScene, gate: Gate) -> GateResult:
    animator_tpid = None
    # locate the Animator's transform by its path
    for tpid in scene.tr:
        if scene.path(tpid) == gate.animator_path:
            if animator_tpid is not None:
                raise DoorPoseError(f"ambiguous Animator path {gate.animator_path}")
            animator_tpid = tpid
    if animator_tpid is None:
        raise DoorPoseError(f"Animator path {gate.animator_path} not found")
    table: dict[int, list] = {0: [("", animator_tpid)]}
    for rel, tpid in scene.descendants(animator_tpid):
        table.setdefault(crc32_path(rel), []).append((rel, tpid))
    open_clip = build_clip(find_clip(scene, gate.controller, gate.open_clip))
    closed_names = [n for n in gate.clip_names if is_closed_clip_name(n) and n != gate.open_clip]
    closed_clips = [build_clip(find_clip(scene, gate.controller, n)) for n in closed_names]
    result = GateResult(gate, open_clip)
    result.spans, result.worst_residual, bad_spans = key_residual(open_clip)
    if bad_spans:
        raise DoorPoseError(f"{open_clip.name}: {bad_spans} of {result.spans} key spans do not reach the next key's value "
                            f"(worst {result.worst_residual:.3g}): the StreamedClip decode is wrong")
    if open_clip.unsupported:
        result.issues.append(("warn", f"{open_clip.name}: {open_clip.unsupported} binding(s) are not Transform curves (ignored)"))
    by_node: dict[int, dict[int, Channel]] = {}
    for ch in open_clip.channels:
        if ch.kind in by_node.setdefault(ch.path_hash, {}):
            raise DoorPoseError(f"{open_clip.name}: two bindings of kind {ch.kind} for hash {ch.path_hash}")
        by_node[ch.path_hash][ch.kind] = ch
        for curve in ch.curves:
            if curve.constant is None:
                result.animated_curves += 1
            else:
                result.constant_curves += 1
    closed_by_node: dict[int, dict[int, Channel]] = {}
    for clip in closed_clips:
        for ch in clip.channels:
            closed_by_node.setdefault(ch.path_hash, {})[ch.kind] = ch
    result.nodes = len(by_node)
    for path_hash in sorted(by_node, key=lambda h: (table.get(h, [("~", 0)])[0][0])):
        hits = table.get(path_hash)
        if not hits:
            result.unresolved += 1
            raise DoorPoseError(f"{open_clip.name}: hash {path_hash} resolves to no descendant of {gate.animator_path}")
        if len(hits) > 1:
            raise DoorPoseError(f"{open_clip.name}: hash {path_hash} is ambiguous: {[h[0] for h in hits]}")
        rel, tpid = hits[0]
        node = "/".join(p for p in (gate.sub, rel) if p)
        pos, quat, scale = scene.local(tpid)
        solved, issues, scale_changed = solve_node(pos, quat, scale, by_node[path_hash], closed_by_node.get(path_hash, {}),
                                                   f"{gate.part}/{node}" if node else gate.part, open_clip.stop)
        result.issues.extend(issues)
        if scale_changed:
            result.scale_changed.append(node or gate.part)
        if solved["moved"]:
            result.kept.append(NodePose(gate.barrier, gate.part, node, open_clip.name, open_clip.stop,
                                        solved["closed_position"], solved["closed_euler"],
                                        solved["open_position"], solved["open_euler"]))
            result.windows[node] = motion_window(by_node[path_hash].values(), open_clip.stop)
        else:
            result.dropped.append(node or gate.part)
    return result


class PackageScene:
    """One scene of an extracted package (scenes/<name>.json): hierarchy paths, instances of a repeated path and world positions."""

    def __init__(self, package: Path, scene_name: str):
        path = package / "scenes" / f"{scene_name}.json"
        if not path.is_file():
            raise DoorPoseError(f"package scene {path} not found")
        self.name = scene_name
        self.objects = json.loads(path.read_text(encoding="utf-8"))["objects"]
        self.by_id = {o["id"]: o for o in self.objects}
        self._paths: dict[str, str] = {}
        self.index: dict[str, list] = {}
        self.children: dict[tuple, list] = {}
        for o in self.objects:
            self.index.setdefault(self.full_path(o), []).append(o)
            self.children.setdefault((o.get("parent_id"), o["name"]), []).append(o)

    def full_path(self, o) -> str:
        if o["id"] not in self._paths:
            parent = self.by_id.get(o.get("parent_id"))
            self._paths[o["id"]] = f"{self.full_path(parent)}/{o['name']}" if parent else o["name"]
        return self._paths[o["id"]]

    def world_position(self, o):
        chain = []
        while o is not None:
            chain.append((o["local_position"], o["local_rotation"], o["local_scale"]))
            o = self.by_id.get(o.get("parent_id"))
        return world_position_of(list(reversed(chain)))


def check_package(package: Path, scene_name: str, poses: list[NodePose], scene: ArenaScene | None = None) -> list[str]:
    """Every sidecar node exists exactly once in the extracted port scene with Arena's local transform. A pose with `at`
    finds its barrier root among the instances of a repeated path by world position (AT_TOLERANCE); a pose read from another
    Arena scene (a borrowed gate) is looked up in that scene of the package. A node name that repeats among its parent's
    children (AirPit's passport leaves) is told apart by the pose's closed local position (CLOSED_MATCH_TOL), and two rows
    must never resolve to the same object. A switch row also needs the node's active flag to be its `closed` state."""
    scenes: dict[str, PackageScene] = {}
    problems = []
    claimed: dict[tuple, str] = {}
    for p in poses:
        name = p.scene or scene_name
        if name not in scenes:
            scenes[name] = PackageScene(package, name)
        ps = scenes[name]
        key = "/".join(x for x in (p.barrier, p.part, p.node) if x)
        roots = ps.index.get(p.barrier, [])
        if p.at is not None:
            roots = [r for r in roots if math.dist(ps.world_position(r), p.at) <= AT_TOLERANCE]
        if len(roots) != 1:
            problems.append(f"package: barrier {p.barrier} found {len(roots)} time(s) in {name}"
                            + (f" at {tidy(p.at)}" if p.at is not None else ""))
            continue
        o = roots[0]
        segments = [s for s in (p.part, *p.node.split("/")) if s]
        for index, segment in enumerate(segments):
            hits = ps.children.get((o["id"], segment), [])
            if len(hits) > 1 and index == len(segments) - 1:
                hits = [h for h in hits if math.dist(h["local_position"], p.closed_position) <= CLOSED_MATCH_TOL]
            if len(hits) != 1:
                problems.append(f"package: {key} found {len(hits)} child(ren) named {segment!r} in {name}"
                                + (f" at the closed position {tidy(p.closed_position)}" if index == len(segments) - 1 else ""))
                o = None
                break
            o = hits[0]
        if o is None:
            continue
        if (name, o["id"]) in claimed:
            problems.append(f"package: {key} and {claimed[(name, o['id'])]} resolve to the same object in {name}")
        claimed[(name, o["id"])] = key
        if math.dist(o["local_position"], p.closed_position) > 1e-3:
            problems.append(f"package: {key} local position {o['local_position']} != Arena {tidy(p.closed_position)}")
        if quat_angle(o["local_rotation"], euler_to_quat(p.closed_euler)) > 0.05:
            problems.append(f"package: {key} local rotation differs from the closed euler {tidy(p.closed_euler)}")
        if p.switch is not None and bool(o.get("active", True)) != bool(p.switch[0]):
            problems.append(f"package: {key} is {'active' if o.get('active', True) else 'inactive'}, the switch row says it starts "
                            f"{'active' if p.switch[0] else 'inactive'}")
    return problems


def extract(map_key: str, arena: str, package: Path | None) -> tuple[str, dict, list[str]]:
    """(sidecar text, source block, verification summary lines): the Mecanim clip path for Bowl, the DotAnimator path for
    Equator and Bay5."""
    if map_key in DOT_SOURCES:
        return extract_dot(map_key, arena, package)
    if map_key in MAP_SOURCES:
        return extract_clips(map_key, arena, package)
    raise DoorPoseError(f"no door-pose source for {map_key}; known: {sorted(set(MAP_SOURCES) | set(DOT_SOURCES))}")


def extract_clips(map_key: str, arena: str, package: Path | None) -> tuple[str, dict, list[str]]:
    """Bowl: the end poses of the Mecanim gate clips below the map's opened barriers."""
    src = MAP_SOURCES[map_key]
    descriptor = json.loads(src["descriptor"].read_text(encoding="utf-8"))
    barriers = list(descriptor["opened_barriers"])
    data_dir, version = find_arena(arena)
    level = scene_level_index(map_key, src["scene"])
    scene = ArenaScene(data_dir, level)
    gates, ignored = read_gates(scene, barriers)
    if not gates:
        raise DoorPoseError(f"no gate animator below {barriers} in level{level}")
    summary = [f"scene {src['scene']} = level{level} + sharedassets{level}.assets, Arena {version or '(version unknown)'}, "
               f"{len(scene.tr)} transforms, {len(scene.animators)} animators ({len(gates)} below opened barriers)"]
    all_poses: list[NodePose] = []
    issues: list[tuple] = []
    for gate in sorted(gates, key=lambda g: (barriers.index(g.barrier), g.part, g.sub)):
        res = solve_gate(scene, gate)
        issues.extend(res.issues)
        all_poses.extend(res.kept)
        summary.append(
            f"gate {gate.barrier}/{gate.part}  animator '{gate.animator_path}'  controller {gate.controller}  "
            f"clips {gate.clip_names} -> open '{gate.open_clip}' ({res.clip.stop:.3f} s)")
        summary.append(
            f"    nodes animated {res.nodes} (curves: {res.animated_curves} animated, {res.constant_curves} constant), "
            f"kept {len(res.kept)}, dropped {len(res.dropped)} (no net change), scale changed {len(res.scale_changed)}, "
            f"transition into open clip {'yes' if gate.transitions else 'NOT SEEN'}")
        summary.append(f"    decode self-check: {res.spans} key spans, each cubic reaches the next key (worst error {res.worst_residual:.1e})")
        for p in res.kept:
            start, end = res.windows.get(p.node, (0.0, 0.0))
            summary.append(f"    + {p.part}/{p.node or '.'}: pos {tidy(p.closed_position)} -> {tidy(p.open_position)}, "
                           f"euler {tidy(p.closed_euler)} -> {tidy(p.open_euler)}, moves {start:.2f}-{end:.2f} s")
        if res.dropped:
            shown = ", ".join(res.dropped[:6]) + (" ..." if len(res.dropped) > 6 else "")
            summary.append(f"    - dropped: {shown}")
        if res.scale_changed:
            summary.append(f"    ! scale differs (not carried): {res.scale_changed}")
    for path in ignored:
        summary.append(f"ignored animator: {path}")
    if package is not None:
        problems = check_package(package, src["scene"], all_poses, scene)
        issues.extend(("error", p) for p in problems)
        summary.append(f"package check: {len(all_poses)} node(s) exist once in {package.name} with Arena's closed transform"
                       if not problems else "package check: FAILED")
    summary.extend(f"WARN {text}" for level, text in issues if level == "warn")
    errors = [text for level, text in issues if level == "error"]
    if errors:
        raise DoorPoseError("verification failed:\n  " + "\n  ".join(errors))
    ordered = sorted(all_poses, key=lambda p: (barriers.index(p.barrier), p.part, p.node != "", p.node))
    source = {"scene": src["scene"], "level": level, "assets": f"sharedassets{level}.assets",
              "arena_version": version or "unknown"}
    summary.append(f"poses {len(ordered)}: " + ", ".join(sorted({f'{p.barrier}/{p.part}' for p in ordered})))
    return render_sidecar(map_key, source, [sidecar_pose(p) for p in ordered]), source, summary


@dataclass
class DotScan:
    """What one DotAnimator scan (or one borrowed gate) found."""
    poses: list = field(default_factory=list)        # NodePose
    lines: list = field(default_factory=list)        # verification summary lines
    objects: int = 0                                 # DotAnimator components decoded (offset == byte length each)
    elements: int = 0
    skipped_inactive: int = 0
    skipped_switch: int = 0
    switches: int = 0                               # switch rows read (scan option `switches`)
    warnings: list = field(default_factory=list)


def closed_pose_of(scene: ArenaScene, tpid: int):
    """(local position, local euler in (-180, 180], local rotation) of a node as the scene has it. The euler triple is checked
    against the quaternion, so a gimbal-lock branch can never hide a different rotation."""
    pos, quat, _ = scene.local(tpid)
    euler = [centered(v) for v in quat_to_euler(quat)]
    if quat_angle(euler_to_quat(euler), quat) > SAME_ROT:
        raise DoorPoseError(f"{scene.path(tpid)}: euler {tidy(euler)} does not reproduce the scene rotation")
    return list(pos), euler, quat


def ancestor(scene: ArenaScene, tpid: int, levels: int) -> int:
    for _ in range(levels):
        tpid = scene.tr[tpid]["m_Father"]["m_PathID"]
    return tpid


def merge_elements(label: str, elements: list) -> tuple:
    """(move element, rotate element) of one target node; one of each kind at most, and a node that both moves and rotates must
    do both with one duration, ease and curve (the pose has one of each)."""
    move = [e for e in elements if e.kind == DOT_KIND_MOVE]
    rot = [e for e in elements if e.kind == DOT_KIND_ROTATE]
    if len(move) > 1 or len(rot) > 1:
        raise DoorPoseError(f"{label}: several DotAnimator elements of the same kind move this node")
    if move and rot and (move[0].seconds != rot[0].seconds or move[0].ease != rot[0].ease or move[0].keys != rot[0].keys):
        raise DoorPoseError(f"{label}: its move and its rotation differ in duration, ease or curve; the pose has one of each")
    return (move[0] if move else None), (rot[0] if rot else None)


def scan_dot_animators(scene: ArenaScene, scene_name: str, scan: dict) -> DotScan:
    """The poses of every active DotAnimator below the scene path scan['root'] (see DOT_SOURCES). With scan['switches'] the
    kind-3 elements become switch rows; without it they are skipped with a note (lamps are not gates)."""
    roots = scene.find_path(scan["root"])
    if not roots:
        raise DoorPoseError(f"scan root {scan['root']!r} not found in {scene_name}")
    depth = scan["depth"]
    read_switches = bool(scan.get("switches"))
    result = DotScan()
    moves: dict[int, list] = {}
    switches: dict[int, list] = {}
    for root in roots:
        for _rel, tpid in [("", root), *scene.descendants(root)]:
            for obj in scene.monobehaviours(tpid):
                if scene.mono_class(obj) != DOT_SCRIPT:
                    continue
                animator = decode_dot_animator(obj.get_raw_data())
                result.objects += 1
                own = scene.tr[tpid]["m_GameObject"]["m_PathID"]
                if animator.game_object != own:
                    raise DoorPoseError(f"{scene.path(tpid)}: the DotAnimator names GameObject {animator.game_object}, not {own}")
                result.elements += len(animator.elements)
                if not scene.active_in_hierarchy(tpid):
                    result.skipped_inactive += 1
                    result.lines.append(f"    - skipped (inactive in the scene, moves nothing): {scene.path(tpid)}")
                    continue
                for element in animator.elements:
                    if element.kind == DOT_KIND_SWITCH and not read_switches:
                        result.skipped_switch += 1
                        result.lines.append(f"    - skipped kind-3 element (switch / lamp): {scene.path(tpid)}")
                        continue
                    if element.target_file != 0:
                        raise DoorPoseError(f"{scene.path(tpid)}: a DotAnimator element targets another file ({element.target_file})")
                    target = scene.go2tr.get(element.target_path)
                    if target is None:
                        raise DoorPoseError(f"{scene.path(tpid)}: the element's target GameObject {element.target_path} is not in this scene")
                    path = scene.path(target)
                    if path != scan["root"] and not path.startswith(scan["root"] + "/"):
                        raise DoorPoseError(f"{scene.path(tpid)}: the element's target {path} lies outside the scan root {scan['root']}")
                    (switches if element.kind == DOT_KIND_SWITCH else moves).setdefault(target, []).append(element)
    for target in sorted(moves, key=lambda t: (scene.path(t), t)):
        path = scene.path(target)
        segments = path.split("/")
        if len(segments) < depth:
            raise DoorPoseError(f"{path}: shallower than the barrier depth {depth}")
        move, rotate = merge_elements(path, moves[target])
        barrier_root = ancestor(scene, target, len(segments) - depth)
        rest = segments[depth:]
        part, node = (rest[0] if rest else ""), "/".join(rest[1:])
        pos, euler, quat = closed_pose_of(scene, target)
        open_position = list(move.target) if move else list(pos)
        open_euler = list(rotate.target) if rotate else list(euler)
        lead = move or rotate
        ease = dot_ease_name(lead.ease)
        if ease == EASE_CURVE:
            curve = curve_from_keys(lead.keys)
        else:
            curve = None
            if lead.keys:
                result.warnings.append(f"{path}: {len(lead.keys)} curve key(s) beside the non-curve ease {ease} are ignored")
        if move is not None:
            unchanged = sum(1 for a, b in zip(open_position, pos) if abs(a - b) < 5e-3)
            if unchanged == 0:
                result.warnings.append(f"{path}: the move target {tidy(open_position)} differs from the closed position "
                                       f"{tidy(pos)} on all three axes; check the absolute-target reading")
        moved = (math.dist(open_position, pos) > DIFF_POS
                 or quat_angle(euler_to_quat(open_euler), quat) > DIFF_ROT)
        if not moved:
            result.lines.append(f"    - dropped (no net change): {path}")
            continue
        at = list(scene.world_position(barrier_root)) if scan["at"] else None
        result.poses.append(NodePose("/".join(segments[:depth]), part, node, "DotAnimator", lead.seconds, pos, euler,
                                     open_position, open_euler, True, SOURCE_DOT, ease, curve, at, scene_name))
    for target in sorted(switches, key=lambda t: (scene.path(t), t)):
        path = scene.path(target)
        segments = path.split("/")
        if len(segments) <= depth:
            raise DoorPoseError(f"{path}: a switch needs a part below the barrier root (depth {depth}); the root itself cannot be switched")
        if target in moves:
            raise DoorPoseError(f"{path}: this node is both switched and moved; the pose has one or the other")
        if len(switches[target]) > 1:
            raise DoorPoseError(f"{path}: several DotAnimator switch elements target this node")
        element = switches[target][0]
        closed_active = bool(scene.go[scene.tr[target]["m_GameObject"]["m_PathID"]]["m_IsActive"])
        open_active = bool(element.active)
        if closed_active == open_active:
            result.lines.append(f"    - dropped (no net change, stays {'active' if closed_active else 'inactive'}): {path}")
            continue
        barrier_root = ancestor(scene, target, len(segments) - depth)
        rest = segments[depth:]
        pos, euler, _ = closed_pose_of(scene, target)
        at = list(scene.world_position(barrier_root)) if scan["at"] else None
        result.switches += 1
        result.poses.append(NodePose("/".join(segments[:depth]), rest[0], "/".join(rest[1:]), "DotAnimator", element.seconds,
                                     pos, euler, list(pos), list(euler), True, SOURCE_DOT, EASE_LINEAR, None, at, scene_name,
                                     (closed_active, open_active)))
    return result


def dot_animator_paths(scene: ArenaScene, root: int) -> list[str]:
    """The hierarchy paths of the DotAnimator components on a transform and below it."""
    found = []
    for _rel, tpid in [("", root), *scene.descendants(root)]:
        if any(scene.mono_class(obj) == DOT_SCRIPT for obj in scene.monobehaviours(tpid)):
            found.append(scene.path(tpid))
    return found


def build_borrowed(scene: ArenaScene, scene_name: str, spec: dict, decoded: list) -> DotScan:
    """The poses of a gate Arena authors no motion for, taken from a neighbour (see DOT_SOURCES): the neighbour's decoded
    motion (modes "absolute" and "offset") or the neighbour's static pose in the scene (mode "scene"). Stops when the borrower
    carries a DotAnimator of its own (it is then not borrowed), is missing or ambiguous, or the lender moves nothing."""
    if spec["mode"] == "scene":
        return build_scene_borrowed(scene, scene_name, spec)
    result = DotScan()
    roots = scene.find_path(spec["barrier"])
    if len(roots) != 1:
        raise DoorPoseError(f"borrowed gate {spec['barrier']!r} found {len(roots)} time(s) in {scene_name}")
    root = roots[0]
    for _rel, tpid in [("", root), *scene.descendants(root)]:
        for obj in scene.monobehaviours(tpid):
            if scene.mono_class(obj) == DOT_SCRIPT:
                raise DoorPoseError(f"{scene.path(tpid)} has a DotAnimator of its own: it is not a borrowed gate")
    for part in spec["parts"]:
        if part:
            hits = [c for c in scene.children(root) if scene.name(c) == part]
            if len(hits) != 1:
                raise DoorPoseError(f"borrowed gate {spec['barrier']}: part {part!r} found {len(hits)} time(s)")
            target = hits[0]
        else:
            target = root
        lender_barrier = spec["lender"]["barrier"]
        lenders = [p for p in decoded if p.barrier == lender_barrier and p.part == part and p.node == ""
                   and (spec["lender"].get("at") is None or (p.at is not None
                                                             and math.dist(p.at, spec["lender"]["at"]) <= AT_TOLERANCE))]
        if len(lenders) != 1:
            raise DoorPoseError(f"lender {lender_barrier}/{part or '.'} found {len(lenders)} time(s) among the decoded poses")
        lender = lenders[0]
        pos, euler, quat = closed_pose_of(scene, target)
        open_position, open_euler = list(pos), list(euler)
        moved_axes = 0
        for i in range(3):
            travel = lender.open_position[i] - lender.closed_position[i]
            if abs(travel) > DIFF_POS:
                moved_axes += 1
                open_position[i] = lender.open_position[i] if spec["mode"] == "absolute" else pos[i] + travel
            turn = lender.open_euler[i] - lender.closed_euler[i]
            if abs(turn) > DIFF_ROT:
                moved_axes += 1
                open_euler[i] = lender.open_euler[i] if spec["mode"] == "absolute" else euler[i] + turn
        if not moved_axes:
            raise DoorPoseError(f"lender {lender_barrier}/{part or '.'} moves nothing")
        at = list(scene.world_position(root)) if spec["at"] else None
        result.poses.append(NodePose(spec["barrier"], part, "", f"borrowed from {lender_barrier}", lender.seconds, pos, euler,
                                     open_position, open_euler, True, SOURCE_BORROWED, lender.ease, lender.curve, at, scene_name))
    return result


def build_scene_borrowed(scene: ArenaScene, scene_name: str, spec: dict) -> DotScan:
    """Mode "scene" (AirPit's front-row bus doors): Arena authors neither a motion nor a duration for them (an Animator with a
    controller that has no clip), but the back row of the same assemblies stands OPEN in the scene. The open pose of every node
    below a borrower part is the lender's CURRENT local transform of the same-named node (same relative path), the closed pose
    the borrower's own; a node whose two poses differ by more than 1 cm / 1 degree becomes one row. Seconds and ease are the
    port's (spec). Stops unless: the barrier, the lender and every part exist exactly once; neither side carries a DotAnimator;
    the lender has no Animator and every borrower Animator a controller without clips (nothing else could pose these nodes); both
    sides have the same node paths (unique per side) and scales; the lender's twins carry the lender's poses."""
    lender_spec = spec["lender"]
    result = DotScan()
    roots = scene.find_path(spec["barrier"])
    if len(roots) != 1:
        raise DoorPoseError(f"borrowed gate {spec['barrier']!r} found {len(roots)} time(s) in {scene_name}")
    root = roots[0]
    lender_roots = scene.find_path(lender_spec["barrier"])
    if len(lender_roots) != 1:
        raise DoorPoseError(f"lender barrier {lender_spec['barrier']!r} found {len(lender_roots)} time(s) in {scene_name}")

    def child(parent: int, name: str, what: str) -> int:
        hits = [c for c in scene.children(parent) if scene.name(c) == name]
        if len(hits) != 1:
            raise DoorPoseError(f"{what} {name!r} found {len(hits)} time(s) below {scene.path(parent)}")
        return hits[0]

    def nodes_of(tpid: int, what: str) -> dict:
        found: dict[str, int] = {}
        for rel, node in scene.descendants(tpid):
            if rel in found:
                raise DoorPoseError(f"{what}: the node path {rel!r} occurs twice below {scene.path(tpid)}; a scene borrow needs unique paths")
            found[rel] = node
        return found

    def difference(a: int, b: int) -> tuple[float, float]:
        (pa, qa, sa), (pb, qb, sb) = scene.local(a), scene.local(b)
        if any(abs(x - y) > DIFF_SCALE * max(1.0, abs(y)) for x, y in zip(sa, sb)):
            raise DoorPoseError(f"{scene.path(a)} and {scene.path(b)} differ in scale {tidy(sa)} / {tidy(sb)}: the sidecar carries no scale")
        return math.dist(pa, pb), quat_angle(qa, qb)

    lender = child(lender_roots[0], lender_spec["part"], "lender part")
    if dot_animator_paths(scene, lender):
        raise DoorPoseError(f"{scene.path(lender)} has a DotAnimator of its own: its pose is not the static one this mode borrows")
    if scene.animator_clip_counts(lender):
        raise DoorPoseError(f"{scene.path(lender)} carries an Animator: its pose may be driven, not static")
    lender_nodes = nodes_of(lender, "lender")
    lender_label = f"{lender_spec['barrier']}/{lender_spec['part']}"
    for twin in lender_spec.get("twins", []):
        twin_nodes = nodes_of(child(lender_roots[0], twin, "lender twin"), "lender twin")
        if set(twin_nodes) != set(lender_nodes):
            raise DoorPoseError(f"lender twin {twin!r} has other nodes than {lender_spec['part']!r}")
        shift = turn = 0.0
        for rel in lender_nodes:
            d_shift, d_turn = difference(twin_nodes[rel], lender_nodes[rel])
            shift, turn = max(shift, d_shift), max(turn, d_turn)
        if shift > 0.005 or turn > 0.5:
            raise DoorPoseError(f"lender twin {twin!r} differs from {lender_spec['part']!r} by {shift:.3f} m / {turn:.2f} deg: "
                                "the back row is not one pose")
        result.lines.append(f"    lender twin {twin!r} carries the same poses (worst {shift:.4f} m / {turn:.3f} deg)")
    for part in spec["parts"]:
        borrower = child(root, part, "borrowed part")
        nested = dot_animator_paths(scene, borrower)
        if nested:
            raise DoorPoseError(f"{nested[0]} has a DotAnimator of its own: it is not a borrowed gate")
        clips = scene.animator_clip_counts(borrower)
        if any(clips):
            raise DoorPoseError(f"{scene.path(borrower)} has an Animator whose controller carries {clips} clip(s): Arena animates it")
        nodes = nodes_of(borrower, "borrowed part")
        if set(nodes) != set(lender_nodes):
            only = sorted(set(nodes) ^ set(lender_nodes))[:4]
            raise DoorPoseError(f"{scene.path(borrower)} and {lender_label} have different node paths (e.g. {only})")
        at = list(scene.world_position(root)) if spec["at"] else None
        kept = 0
        for rel, node in sorted(nodes.items()):
            shift, turn = difference(node, lender_nodes[rel])
            if shift <= DIFF_POS and turn <= DIFF_ROT:
                continue
            pos, euler, _ = closed_pose_of(scene, node)
            open_pos, open_euler, _ = closed_pose_of(scene, lender_nodes[rel])
            result.poses.append(NodePose(
                spec["barrier"], part, rel, f"borrowed from the static open pose of {lender_label}; seconds are the port's",
                spec["seconds"], pos, euler, open_pos, open_euler, True, SOURCE_BORROWED, spec["ease"], None, at, scene_name))
            kept += 1
        result.lines.append(f"    {part}: {len(nodes)} node(s), {kept} differ from {lender_spec['part']!r} and become rows")
        if not kept:
            raise DoorPoseError(f"{scene.path(borrower)} already stands like {lender_label}: nothing to borrow")
    return result


def check_identities(poses: list) -> None:
    """A (barrier, part, node) may occur twice only for two different instances of a repeated hierarchy path, and then both
    poses carry `at` more than AT_TOLERANCE apart; the client could not choose between them otherwise. Without `at` a repeated
    NAME among one parent's children (AirPit's passport leaves) is allowed when the closed local positions are more than
    CLOSED_MATCH_TOL apart: the client picks the row of a node by that position (DoorPoseModel.SelectRowsForNode)."""
    seen: dict[tuple, list] = {}
    for p in poses:
        slot = (p.barrier, p.part, p.node)
        label = "/".join(x for x in slot if x)
        for other in seen.get(slot, []):
            if p.at is None and other.at is None and math.dist(p.closed_position, other.closed_position) > CLOSED_MATCH_TOL:
                continue
            if p.at is None or other.at is None:
                raise DoorPoseError(f"{label} occurs twice and a pose lacks 'at' to tell the instances apart "
                                    f"(closed positions {tidy(p.closed_position)} and {tidy(other.closed_position)} are within "
                                    f"{CLOSED_MATCH_TOL} m)")
            if math.dist(p.at, other.at) <= AT_TOLERANCE:
                raise DoorPoseError(f"{label} occurs twice at {tidy(p.at)}")
        seen.setdefault(slot, []).append(p)


def pose_row(p: NodePose) -> str:
    curve = f", {len(p.curve)}-key curve" if p.curve else ""
    at = f", at {tidy(p.at)}" if p.at is not None else ""
    if p.switch is not None:
        return (f"    + [{p.source}] {p.barrier} | {p.part or '.'} | {p.node or '.'}: SWITCH "
                f"{'active' if p.switch[0] else 'inactive'} -> {'active' if p.switch[1] else 'inactive'}, at the scene transform "
                f"pos {tidy(p.closed_position)} euler {tidy(p.closed_euler)}, {round(p.seconds, DECIMALS)} s{at}")
    return (f"    + [{p.source}] {p.barrier} | {p.part or '.'} | {p.node or '.'}: pos {tidy(p.closed_position)} -> "
            f"{tidy(p.open_position)}, euler {tidy(p.closed_euler)} -> {tidy(p.open_euler)}, {round(p.seconds, DECIMALS)} s "
            f"{p.ease}{curve}{at}")


def extract_dot(map_key: str, arena: str, package: Path | None) -> tuple[str, dict, list[str]]:
    """Equator, Bay5 and AirPit: the end poses of the DotAnimators below the scan roots of DOT_SOURCES (switch rows included
    where a scan asks for them), plus the borrowed gates."""
    src = DOT_SOURCES[map_key]
    data_dir, version = find_arena(arena)
    scenes: dict[str, ArenaScene] = {}

    def scene_of(name: str) -> ArenaScene:
        if name not in scenes:
            scenes[name] = ArenaScene(data_dir, scene_level_index(map_key, name))
        return scenes[name]

    main = scene_of(src["scene"])
    level = scene_level_index(map_key, src["scene"])
    summary = [f"scene {src['scene']} = level{level} + sharedassets{level}.assets, Arena {version or '(version unknown)'}, "
               f"{len(main.tr)} transforms"]
    poses: list[NodePose] = []
    warnings: list[str] = []
    objects = elements = 0
    for scan in src["scans"]:
        scanned = scan_dot_animators(main, src["scene"], scan)
        poses.extend(scanned.poses)
        warnings.extend(scanned.warnings)
        objects += scanned.objects
        elements += scanned.elements
        summary.append(f"scan {scan['root']} (barrier depth {scan['depth']}): {scanned.objects} DotAnimator(s), {scanned.elements} "
                       f"element(s), {len(scanned.poses)} pose(s) ({scanned.switches} switch row(s)), {scanned.skipped_inactive} inactive, "
                       f"{scanned.skipped_switch} switch element(s) skipped")
        summary.extend(scanned.lines)
        summary.extend(pose_row(p) for p in scanned.poses)
    summary.append(f"decode self-check: {objects} DotAnimator object(s), {elements} element(s); every decode ends exactly at the "
                   "object's byte length, every wrap word is (2, 2, 4), every kind and ease is known")
    for spec in src.get("borrowed", []):
        borrowed = build_borrowed(scene_of(spec["scene"]), spec["scene"], spec, poses)
        poses.extend(borrowed.poses)
        lender_label = (f"{spec['lender']['barrier']}/{spec['lender']['part']}" if spec["mode"] == "scene"
                        else spec["lender"]["barrier"])
        summary.append(f"borrowed {spec['barrier']} (scene {spec['scene']}, from {lender_label}, mode {spec['mode']}, "
                       f"no DotAnimator of its own): {len(borrowed.poses)} pose(s)")
        summary.extend(borrowed.lines)
        summary.extend(pose_row(p) for p in borrowed.poses)
    if not poses:
        raise DoorPoseError(f"no DotAnimator pose for {map_key}")
    check_identities(poses)
    if package is not None:
        problems = check_package(package, src["scene"], poses, main)
        if problems:
            raise DoorPoseError("verification failed:\n  " + "\n  ".join(problems))
        summary.append(f"package check: {len(poses)} pose(s) exist in {package.name} with Arena's closed transform "
                       f"(repeated paths told apart by 'at', repeated names by the closed position, each object claimed once)")
    summary.extend(f"WARN {text}" for text in warnings)
    ordered = sorted(poses, key=lambda p: (p.barrier, tuple(tidy(p.at)) if p.at is not None else (), p.part, p.node,
                                           tuple(tidy(p.closed_position))))
    source = {"scene": src["scene"], "level": level, "assets": f"sharedassets{level}.assets", "arena_version": version or "unknown"}
    extra = []
    for name in scenes:
        if name != src["scene"]:
            index = scene_level_index(map_key, name)
            extra.append({"scene": name, "level": index, "assets": f"sharedassets{index}.assets"})
    if extra:
        source["extra_scenes"] = extra
    counts = {s: sum(1 for p in ordered if p.source == s) for s in SOURCES}
    summary.append(f"poses {len(ordered)}: " + ", ".join(f"{n} {s}" for s, n in counts.items() if n))
    return render_sidecar(map_key, source, [sidecar_pose(p) for p in ordered]), source, summary


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--map", required=True, choices=sorted(set(MAP_SOURCES) | set(DOT_SOURCES)))
    parser.add_argument("--arena", default="auto", help="'auto' (launcher registry, default location) or an Arena folder (read-only)")
    parser.add_argument("--package", type=Path, default=None, help="extracted package to cross-check names and closed transforms")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--csharp", action="store_true",
                        help="also print the ArenaDoorPose literals for ArenaMapRegistry.<Map>.DoorPoses (paste them by hand)")
    args = parser.parse_args(argv)
    try:
        text, _, summary = extract(args.map, args.arena, args.package)
    except DoorPoseError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    print("\n".join(summary))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {args.output} ({len(json.loads(text)['poses'])} poses)")
    if args.csharp:
        print(render_csharp(json.loads(text)["poses"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
