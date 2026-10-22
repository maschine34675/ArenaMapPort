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

CLI:
    python tools/extract/extract_door_poses.py --map arena_bowl --arena auto \\
        --package work/extract/bowl-native-v2 --output tools/maps/bowl.doors.json [--csharp]
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
SCHEMA_VERSION = 1
DATA_DIR = "EscapeFromTarkovArena_Data"
DEFAULT_DATA = Path("D:/Battlestate Games/Escape from Tarkov Arena") / DATA_DIR
KEY_SOURCES = REPO / "tools" / "bundle" / "key-sources.json"

# map key -> the scene that holds the gate objects and the descriptor sidecar. Only Bowl has gate animations to port: no
# scene of Equator or Bay5 has an Animator under an opened barrier, AirPit's three bus-door Animators use a controller
# without clips (docs/progress/door-poses.md, "Other maps").
MAP_SOURCES = {
    "arena_bowl": {"scene": "Arena_Bowl_TeamFight_DesignStuff", "descriptor": REPO / "tools" / "maps" / "bowl.json"},
}

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


@dataclass
class NodePose:
    """Closed and open pose of one node, local to the node's parent (Unity localPosition / localEulerAngles)."""
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
    return {
        "barrier": p.barrier, "part": p.part, "node": p.node, "clip": p.clip, "seconds": round(p.seconds, DECIMALS),
        "closed": {"position": tidy(p.closed_position), "euler": tidy(p.closed_euler)},
        "open": {"position": tidy(p.open_position), "euler": tidy(p.open_euler)},
    }


def csharp_float(value: float) -> str:
    text = repr(float(value))
    if "e" in text or "E" in text:
        raise DoorPoseError(f"cannot spell {value!r} as a plain C# float literal")
    return text + "f"


def render_csharp(poses: list[dict], indent: str = " " * 16) -> str:
    """The `new ArenaDoorPose(...)` literals of ArenaMapRegistry.<Map>.DoorPoses for a sidecar's poses, one pose per three
    lines, ready to paste between `DoorPoses: new[] {` and `}` (a test pins that the registry carries exactly this text)."""
    def vec(values):
        return "new[] { " + ", ".join(csharp_float(v) for v in values) + " }"

    lines = []
    for p in poses:
        lines.append(f"{indent}new ArenaDoorPose({json.dumps(p['barrier'])}, {json.dumps(p['part'])}, {json.dumps(p['node'])}, "
                     f"{json.dumps(p['clip'])}, {csharp_float(p['seconds'])},")
        lines.append(f"{indent}    {vec(p['closed']['position'])}, {vec(p['closed']['euler'])},")
        lines.append(f"{indent}    {vec(p['open']['position'])}, {vec(p['open']['euler'])}),")
    return "\n".join(lines)


def render_sidecar(key: str, source: dict, poses: list[dict]) -> str:
    doc = {
        "_comment": ("Generated by tools/extract/extract_door_poses.py (docs/progress/door-poses.md): the END POSE of every node "
                     "Arena's gate animations move, next to the node's closed pose in the scene. barrier = the opened_barriers path, "
                     "part = the barrier root's direct child (what BoothDoorRepairs switches off), node = the transform path below "
                     "the part that the clip animates (\"\" = the part itself), positions and euler angles are LOCAL to the node's "
                     "parent (Unity localPosition / localEulerAngles). Euler angles are the clip's own raw values where the clip "
                     "stores euler curves (multi-turn spins survive; interpolate them component-wise, not as quaternions). "
                     "ArenaMapRegistry.<Map>.DoorPoses mirrors this file number for number (validate_map_descriptors.py and "
                     "BowlMapTests). Object names and derived end-pose numbers only; no Arena asset, file or curve is stored."),
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


def check_package(package: Path, scene_name: str, poses: list[NodePose], scene: ArenaScene) -> list[str]:
    """Every sidecar node exists exactly once in the extracted port scene with Arena's local transform."""
    path = package / "scenes" / f"{scene_name}.json"
    if not path.is_file():
        raise DoorPoseError(f"package scene {path} not found")
    objects = json.loads(path.read_text(encoding="utf-8"))["objects"]
    by_id = {o["id"]: o for o in objects}
    cache: dict[str, str] = {}

    def full_path(o):
        if o["id"] not in cache:
            parent = by_id.get(o.get("parent_id"))
            cache[o["id"]] = f"{full_path(parent)}/{o['name']}" if parent else o["name"]
        return cache[o["id"]]

    index: dict[str, list] = {}
    for o in objects:
        index.setdefault(full_path(o), []).append(o)
    problems = []
    for p in poses:
        key = "/".join(x for x in (p.barrier, p.part, p.node) if x)
        hits = index.get(key, [])
        if len(hits) != 1:
            problems.append(f"package: {key} found {len(hits)} time(s) in {scene_name}")
            continue
        o = hits[0]
        if math.dist(o["local_position"], p.closed_position) > 1e-3:
            problems.append(f"package: {key} local position {o['local_position']} != Arena {tidy(p.closed_position)}")
        if quat_angle(o["local_rotation"], euler_to_quat(p.closed_euler)) > 0.05:
            problems.append(f"package: {key} local rotation differs from the closed euler {tidy(p.closed_euler)}")
    return problems


def extract(map_key: str, arena: str, package: Path | None) -> tuple[str, dict, list[str]]:
    """(sidecar text, source block, verification summary lines)."""
    if map_key not in MAP_SOURCES:
        raise DoorPoseError(f"no door-pose source for {map_key}; known: {sorted(MAP_SOURCES)}")
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


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--map", required=True, choices=sorted(MAP_SOURCES))
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
