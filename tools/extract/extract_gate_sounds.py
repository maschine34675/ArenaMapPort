#!/usr/bin/env python3
"""Gate and door sounds of the four maps, from Arena's own clips (docs/progress/gate-sounds.md).

Arena plays a clip on every gate it animates: an `Audio.Arena.EntitySoundPlayer` MonoBehaviour (a 100-byte object: 32 bytes of head and
17 words) sits on an emitter in the map's sound scene (the passports of AirPit on the leaves of its TeamFight scene) and names an
AudioSource, an AudioClip, a volume, a pitch, the distances and the mixer group (ArenaGates, -15 dB). The port moves the same gates
(tools/maps/<stem>.doors.json, SpawnGates.cs) but plays nothing, so a booth door slides in silence. This tool turns the players into
data the client can play, in two steps:

  rows   tools/maps/<stem>.gatesounds.json (git-tracked, names and numbers only). One row per gate unit of the port that Arena gives a
         sound: the unit it belongs to (barrier / part / `at` / closed position, the keys of the door rows, the same tolerances),
         the clip, the emitter position, the volumes, the distances and the mixer. An emitter is joined to its unit by the horizontal
         distance of the emitter to the gate (at most 2.0 m, the next gate of another assembly at least 4.0 m away); the emitters of
         other modes (ShootOut) have no gate of the port near and are listed as skipped. A part of the unit is `null` (every part of the
         barrier instance: both leaves of a booth door, both leaves of a depot gate share one sound) when the emitter stands on the
         barrier root, "" when the barrier root itself moves (Bay5's rollers), else the part's name. Gates that Arena animates in the
         port without an emitter of their own (Equator's fifth booth door) get a borrowed row from tools/extract/gate_sound_fit.json.
  clips  work/extract/gate-sounds/<map_key>/gate-sounds.json plus one PCM16 WAV per clip (git-ignored, BSG-derived; staged into dist/
         by the client build): the rows of the sidecar, the decoded clips, and for every clip the seam table of the fitter
         (see below). AirPit's two clips are PITCH-BAKED: the authored players play them at 1.25 and 1.2, and the passport clip is
         sharedassets28.resource itself, a stage-2 key-source file of arena_airpit (tools/bundle/key-sources.json), whose PCM payload
         would otherwise ship byte for byte. Resampling the clip by its pitch in the extractor makes the shipped bytes no copy
         (docs/PLAN-ENCRYPTION.md), and the client plays every clip at pitch 1.

The fit. The port opens a gate in `SpawnGateDriveScale` (default 0.5) of Arena's time, the clip is as long as Arena's motion. The client
removes ONE stationary stretch of the clip between two seams chosen here (head, the impact that ends the motion and the tail are kept; a
60 ms equal-power crossfade joins the seam, the material before the seam is ramped to the level after it). The table holds, for every
removal on a 0.1 s grid, the best seam (c0 / c1 in frames of the shipped clip) and whether it is clean enough (`ok`: the level jump over the
seam is within 1 dB of the clip's natural 50 ms level changes, the spectral centroid jump within 1500 Hz). The runtime needs no FFT: it picks
the largest `ok` entry whose removal does not exceed (1 - scale) * event_s and cuts. The search is the prototype of the 2026-10-25 research
(80 ms band-energy and level cost on a 10 ms grid, +-5 ms alignment by correlation).

CLI:
    python tools/extract/extract_gate_sounds.py rows  [--map arena_bowl] [--arena auto] [--check]
    python tools/extract/extract_gate_sounds.py clips [--map arena_bowl] [--arena auto] [--output work/extract/gate-sounds]
Without --map all four released maps. `rows --check` compares with the tracked sidecars and writes nothing. The output is byte-stable.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import struct
import sys
import wave
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(HERE))
import extract_door_poses as doors  # noqa: E402  (stdlib only at import time; UnityPy is imported when a scene is read)

SCHEMA_VERSION = 1
MAPS_DIR = REPO / "tools" / "maps"
FIT_FILE = HERE / "gate_sound_fit.json"
KEY_SOURCES = REPO / "tools" / "bundle" / "key-sources.json"
OUTPUT_ROOT = REPO / "work" / "extract" / "gate-sounds"
MANIFEST_NAME = "gate-sounds.json"

ENTITY_SOUND_PLAYER = "Audio.Arena.EntitySoundPlayer"
PLAYER_RAW_BYTES = 100       # 32 bytes of MonoBehaviour head (empty name) + 17 words
PLAYER_TAIL_BYTES = 68
MIXER_ASSET = "resources.assets"
GATES_MIXER = "ArenaGates"
GATES_MIXER_DB = -15.0       # the group's fader, the same in all three snapshots of the master mixer (the ancestors stand at 0 dB)
MIXER_SNAPSHOT_SPREAD_DB = 0.1
JOIN_MAX_M = 2.0             # an emitter belongs to a gate when its horizontal distance is at most this
JOIN_MARGIN_M = 4.0          # ... and the nearest gate of another assembly is at least this far
TIE_M = 0.01                 # places of one assembly this close to the nearest are tied (the root wins)
AT_TOLERANCE = 0.05          # ArenaDoorPose.AtTolerance
CLOSED_TOLERANCE = 0.05      # ArenaDoorPose.ClosedPositionTolerance
CLIP_FORMATS = {0: "PCM16", 1: "Vorbis"}

# map key -> stem and the scenes whose players count, in order. "sound" = the map's ambient Sound scene (extract_spatial_audio.scene_for_map),
# "gate" = the TeamFight_DesignStuff scene the door rows were read from (doors.json source.level). AirPit's passports sound from players on the
# leaves of the gate scene; the sound scene holds twins of them (kept out as duplicates, the gate scene is read first).
MAPS = {
    "arena_equator": {"stem": "equator", "scenes": ("sound",)},
    "arena_bay5": {"stem": "bay5", "scenes": ("sound",)},
    "arena_airpit": {"stem": "airpit", "scenes": ("gate", "sound")},
    "arena_bowl": {"stem": "bowl", "scenes": ("sound",)},
}

# fit search (the 2026-10-25 prototype, numbers unchanged)
FIT_WINDOW_S = 0.08
FIT_STEP_S = 0.01
FIT_ALIGN_S = 0.005
FIT_LEVEL_TRIGGER_DB = 1.5
FIT_RAMP_LIMIT_DB = 12.0
FIT_GRID_S = 0.1
OK_JUMP_MARGIN_DB = 1.0
OK_CENTROID_HZ = 1500.0
NATURAL_WINDOW_S = 0.05


class GateSoundError(Exception):
    """A check of the extraction failed; nothing is written."""


# ---- row keys: the one matching rule (client GateSoundModel, validators, tests) -------------------------------------------------------

def near3(a, b, tolerance: float) -> bool:
    """Are two 3-vectors within `tolerance` metres? (None never is.)"""
    return a is not None and b is not None and math.dist(a, b) <= tolerance


def row_applies(row: dict, barrier: str, part: str, at, closed) -> bool:
    """Does a sound row belong to the gate part with this key? barrier equal; part: the row's null = every part, else equal; at: the row's
    null = every instance, else the part's barrier instance within AT_TOLERANCE; closed_position: the row's null = any, else the part's
    closed local position within CLOSED_TOLERANCE. The same rule as GateSoundModel.RowApplies in the client."""
    if row["barrier"] != barrier:
        return False
    if row["part"] is not None and row["part"] != part:
        return False
    if row["at"] is not None and not near3(row["at"], at, AT_TOLERANCE):
        return False
    if row["closed_position"] is not None and not near3(row["closed_position"], closed, CLOSED_TOLERANCE):
        return False
    return True


def row_identity(row: dict) -> tuple:
    return (row["barrier"], row["part"], tuple(row["at"]) if row["at"] is not None else None,
            tuple(row["closed_position"]) if row["closed_position"] is not None else None)


def row_resolves(row: dict, door_rows: list[dict]) -> bool:
    """Does at least one TRANSFORM row of a doors sidecar belong to the sound row? A closed position is the closed local position of the
    part itself (node ""), which is what the client reads off the part when it notes the poses."""
    for pose in door_rows:
        if "switch" in pose:
            continue
        if pose["barrier"] != row["barrier"]:
            continue
        if row["part"] is not None and pose["part"] != row["part"]:
            continue
        if row["at"] is not None and not near3(row["at"], pose.get("at"), AT_TOLERANCE):
            continue
        if row["closed_position"] is not None and not (pose["node"] == "" and near3(row["closed_position"], pose["closed"]["position"], CLOSED_TOLERANCE)):
            continue
        return True
    return False


# ---- the sidecar ---------------------------------------------------------------------------------------------------------------------

SIDECAR_COMMENT = (
    "Generated by tools/extract/extract_gate_sounds.py rows (docs/progress/gate-sounds.md): the sounds Arena plays on the gates the port "
    "moves. rows = one per gate unit of tools/maps/<stem>.doors.json that Arena gives a sound. A row belongs to a gate part when barrier is "
    "equal, part is null or equal ('' = the barrier root itself moves), at is null or within 0.05 m of the part's barrier instance (world "
    "position, as in the door rows), closed_position is null or within 0.05 m of the part's closed local position (AirPit's passport "
    "leaves: six names for sixteen leaves). emitter = the WORLD position the sound plays from (Arena's player object). clip names an "
    "entry of clips (an AudioClip of the map's shared assets). source_volume is the AudioSource's volume, player_volume the "
    "EntitySoundPlayer's; the client plays at source_volume * player_volume * 10^(mixer_db / 20) * Spawns/GateSoundVolume. mixer = the "
    "mixer group the PLAYER names (ArenaGates, -15 dB, or null = none, 0 dB), source_mixer = the group of the AudioSource itself "
    "(Environment on Bay5's gates and AirPit's bus doors: which one wins in Arena is not known, the player's is baked). authored_pitch is "
    "what Arena plays the clip at; pitch is what the CLIENT still applies: 1 for every clip, AirPit's two clips are resampled by their "
    "pitch when they are extracted (tools/extract/gate_sound_fit.json bake_pitch). min_distance / max_distance are the player's. A borrowed "
    "row (origin 'borrowed ...') is for a gate Arena authors no player for and copies its lender's. skipped = players of Arena that no "
    "gate of the port is near (other modes) or that duplicate a kept row. Object names and derived numbers only; no Arena asset or file is stored."
)


def _line(value) -> str:
    return json.dumps(value, separators=(", ", ": "), ensure_ascii=True)


def render_rows_sidecar(doc: dict) -> str:
    """The sidecar text: one object per line for clips, skipped and rows, so a review diff reads row by row."""
    out = ["{", f'  "_comment": {json.dumps(SIDECAR_COMMENT, ensure_ascii=True)},',
           f'  "schema_version": {doc["schema_version"]},', f'  "key": {json.dumps(doc["key"])},',
           f'  "source": {_line(doc["source"])},', f'  "mixer": {_line(doc["mixer"])},']
    for name, last in (("clips", False), ("skipped", False), ("rows", True)):
        items = doc[name]
        if items:
            out.append(f'  "{name}": [')
            out.append(",\n".join("    " + _line(item) for item in items))
            out.append("  ]" + ("" if last else ","))
        else:
            out.append(f'  "{name}": []' + ("" if last else ","))
    out.append("}")
    return "\n".join(out) + "\n"


def sidecar_path(key: str) -> Path:
    return MAPS_DIR / f"{MAPS[key]['stem']}.gatesounds.json"


def doors_path(key: str) -> Path:
    return MAPS_DIR / f"{MAPS[key]['stem']}.doors.json"


def load_fit() -> dict:
    return json.loads(FIT_FILE.read_text(encoding="utf-8"))


def key_source_files() -> set[str]:
    """The base names of every file the stage-2 key derivation samples (tools/bundle/key-sources.json), over all maps."""
    table = json.loads(KEY_SOURCES.read_text(encoding="utf-8"))["maps"]
    return {Path(name).name for entries in table.values() for entry in entries for name in entry["files"]}


# ---- reading Arena -------------------------------------------------------------------------------------------------------------------

def decode_player(tail: bytes) -> dict:
    """The 17 words behind the 32-byte head of an EntitySoundPlayer. w0 entity id, w1-w3 PPtr<AudioSource>, w4-w6 PPtr<AudioClip>,
    w7 volume, w8 pitch, w9 / w10 unknown, w11 min distance, w12 max distance, w13 unknown, w14-w16 PPtr<AudioMixerGroup>."""
    if len(tail) != PLAYER_TAIL_BYTES:
        raise GateSoundError(f"an EntitySoundPlayer tail of {len(tail)} bytes, expected {PLAYER_TAIL_BYTES}: the class changed")
    entity = struct.unpack_from("<i", tail, 0)[0]
    source = struct.unpack_from("<iq", tail, 4)
    clip = struct.unpack_from("<iq", tail, 16)
    volume, pitch, w9, w10, minimum, maximum, w13 = struct.unpack_from("<7f", tail, 28)
    mixer = struct.unpack_from("<iq", tail, 56)
    return {"entity": entity, "source": source, "clip": clip, "volume": volume, "pitch": pitch, "min": minimum, "max": maximum,
            "unknown": (w9, w10, w13), "mixer": mixer}


def external_name(obj, file_id: int) -> str:
    """The file a PPtr of `obj` points into (file id 0 = its own file)."""
    if file_id == 0:
        return Path(obj.assets_file.name).name
    return Path(obj.assets_file.externals[file_id - 1].path).name


def r3(value: float) -> float:
    return round(float(value), 3)


def r4(value: float) -> float:
    return round(float(value), 4)


class MixerBook:
    """AudioMixerGroup names, paths and fader levels of Arena's master mixer (resources.assets), read once."""

    def __init__(self, data_dir: Path):
        import UnityPy
        self.env = UnityPy.load(str(data_dir / MIXER_ASSET))
        self.by_id = {o.path_id: o for o in self.env.objects}
        self.cache: dict[int, dict] = {}

    def name(self, path_id: int) -> str:
        """The name of a group (no level computed: a source's own group only needs its name)."""
        obj = self.by_id.get(path_id)
        if obj is None or obj.type.name != "AudioMixerGroupController":
            raise GateSoundError(f"mixer group {path_id} is not an AudioMixerGroupController of {MIXER_ASSET}")
        return obj.read_typetree()["m_Name"]

    def group(self, path_id: int) -> dict:
        """{name, path, db}: the group's name, its path from the mixer root, and the sum of the fader levels up to the root, which must
        agree over all snapshots (within MIXER_SNAPSHOT_SPREAD_DB)."""
        if path_id in self.cache:
            return self.cache[path_id]
        obj = self.by_id.get(path_id)
        if obj is None or obj.type.name != "AudioMixerGroupController":
            raise GateSoundError(f"mixer group {path_id} is not an AudioMixerGroupController of {MIXER_ASSET}")
        tt = obj.read_typetree()
        mixer = self.by_id[tt["m_AudioMixer"]["m_PathID"]].read_typetree()["m_MixerConstant"]
        guid = tuple(tt["m_GroupID"][f"data[{i}]"] for i in range(4))
        guids = [tuple(g[f"data[{i}]"] for i in range(4)) for g in mixer["groupGUIDs"]]
        names = bytes(mixer["groupNameBuffer"]).split(b"\x00")
        index = guids.index(guid)
        chain = []
        c = index
        while c >= 0:
            chain.append(c)
            c = mixer["groups"][c]["parentConstantIndex"]
        totals = [sum(snapshot["values"][mixer["groups"][c]["volumeIndex"]] for c in chain) for snapshot in mixer["snapshots"]]
        if max(totals) - min(totals) > MIXER_SNAPSHOT_SPREAD_DB:
            raise GateSoundError(f"mixer group {tt['m_Name']} is {totals} dB in the three snapshots: no single level to bake")
        info = {"name": tt["m_Name"], "path": "/".join(names[c].decode() for c in reversed(chain)),
                "db": round(sum(totals) / len(totals), 1)}
        self.cache[path_id] = info
        return info


class Emitter:
    """One decoded EntitySoundPlayer with its scene context."""

    def __init__(self, level: int, path: str, world, active: bool, player: dict, scene, obj, tpid: int):
        self.level, self.path, self.world, self.active, self.player = level, path, world, active, player
        self.scene, self.obj, self.tpid = scene, obj, tpid

    @property
    def label(self) -> str:
        return f"level{self.level} {self.path}"


def read_emitters(scene, level: int) -> list[Emitter]:
    out = []
    for pid, obj in sorted(scene.files[scene.level_name].items()):
        if obj.type.name != "MonoBehaviour" or scene.mono_class(obj) != ENTITY_SOUND_PLAYER:
            continue
        raw = obj.get_raw_data()
        if len(raw) != PLAYER_RAW_BYTES:
            raise GateSoundError(f"level{level} object {pid}: an EntitySoundPlayer of {len(raw)} bytes, expected {PLAYER_RAW_BYTES}")
        gf, gp, enabled, sf, sp, name, tail_at = doors.parse_mono_head(raw)
        player = decode_player(raw[tail_at:])
        tpid = scene.go2tr[gp]
        out.append(Emitter(level, scene.path(tpid), tuple(r3(v) for v in scene.world_position(tpid)), scene.active_in_hierarchy(tpid), player, scene, obj, tpid))
    return out


class AssetFiles:
    """Objects of the shared-assets files an emitter's clip lives in. Bay5's sound scene names clips of sharedassets87.assets, another
    scene's file: such a file is loaded on first use."""

    def __init__(self, data_dir: Path):
        self.data_dir = data_dir
        self.cache: dict[str, dict] = {}

    def find(self, scene, file_name: str, path_id: int):
        obj = scene.files.get(file_name, {}).get(path_id)
        if obj is not None:
            return obj
        if file_name not in self.cache:
            import UnityPy
            env = UnityPy.load(str(self.data_dir / file_name))
            self.cache[file_name] = {o.path_id: o for o in env.objects if Path(o.assets_file.name).name == file_name}
        return self.cache[file_name].get(path_id)


def clip_entry(scene, emitter: Emitter, mixers: MixerBook, assets: AssetFiles) -> tuple[dict, dict]:
    """(clip table entry, source info) of an emitter: the AudioClip the player names and the AudioSource's own volume and mixer group."""
    fid, pid = emitter.player["clip"]
    file_name = external_name(emitter.obj, fid)
    clip_obj = assets.find(scene, file_name, pid)
    if clip_obj is None or clip_obj.type.name != "AudioClip":
        raise GateSoundError(f"{emitter.label}: the AudioClip {file_name}:{pid} was not found")
    tt = clip_obj.read_typetree()
    resource = tt["m_Resource"]
    entry = {"id": tt["m_Name"], "asset": file_name, "path_id": pid, "format": CLIP_FORMATS.get(tt["m_CompressionFormat"], str(tt["m_CompressionFormat"])),
             "channels": tt["m_Channels"], "sample_rate": tt["m_Frequency"], "seconds": r4(tt["m_Length"]),
             "resource": {"file": resource["m_Source"], "offset": resource["m_Offset"], "size": resource["m_Size"]}}
    sfid, spid = emitter.player["source"]
    source_obj = scene.files[external_name(emitter.obj, sfid)].get(spid) if sfid == 0 else None
    if source_obj is None or source_obj.type.name != "AudioSource":
        raise GateSoundError(f"{emitter.label}: the AudioSource {sfid}:{spid} of the player is not in its level file")
    st = source_obj.read_typetree()
    group = st["OutputAudioMixerGroup"]
    source_mixer = None
    if group["m_PathID"]:
        if external_name(source_obj, group["m_FileID"]) != MIXER_ASSET:
            raise GateSoundError(f"{emitter.label}: the AudioSource names a mixer group outside {MIXER_ASSET}")
        source_mixer = mixers.name(group["m_PathID"])
    return entry, {"volume": float(st["m_Volume"]), "mixer": source_mixer}


# ---- the join with the door rows --------------------------------------------------------------------------------------------------------

def dist_xz(a, b) -> float:
    return math.hypot(a[0] - b[0], a[2] - b[2])


def moves_itself(rows: list[dict], part: str) -> bool:
    """Does a transform row move the part itself (node "")? Then its closed local position is a key of the part."""
    return any(r["part"] == part and r["node"] == "" for r in rows)


def gate_candidates(doc: dict, scene) -> list[dict]:
    """The places a sound can belong to, from the door rows and the gate scene: per barrier instance (barrier, at) its root and each of its
    parts that carries a transform row. part is None on a root candidate (every part of the instance), "" when the root itself moves, the
    part's name otherwise; a part name that several children of the barrier share (AirPit's passport leaves) is one candidate per child, told
    apart by closed_position (the child's closed local position)."""
    groups: dict[tuple, list[dict]] = {}
    for pose in doc["poses"]:
        if "switch" in pose:
            continue
        at = tuple(pose["at"]) if pose["at"] is not None else None
        groups.setdefault((pose["barrier"], at), []).append(pose)
    out = []
    for (barrier, at), rows in sorted(groups.items(), key=lambda kv: (kv[0][0], kv[0][1] or ())):
        instances = scene.find_path(barrier)
        if at is not None:
            instances = [t for t in instances if dist_xz(scene.world_position(t), at) <= 0.2]
        # a barrier that is not in this scene (Equator's fifth booth door lives in Arena_Equator) has no candidate: it is borrowed
        for tpid in instances:
            group = (barrier, at)
            has_root_row = any(r["part"] == "" for r in rows)
            # a barrier that reuses part names among its children (AirPit's 16 passport leaves, 6 names) keys every part that moves ITSELF by
            # its closed local position; elsewhere the name is the key
            names = [scene.name(c) for c in scene.children(tpid)]
            repeats = any(names.count(n) > 1 for n in {r["part"] for r in rows if r["part"]})
            out.append({"group": group, "part": "" if has_root_row else None, "closed": None, "world": scene.world_position(tpid), "root": True})
            for part in sorted({r["part"] for r in rows if r["part"]}):
                children = [c for c in scene.children(tpid) if scene.name(c) == part]
                for child in children:
                    closed = tuple(r3(v) for v in scene.local(child)[0]) if repeats and moves_itself(rows, part) else None
                    out.append({"group": group, "part": part, "closed": closed, "world": scene.world_position(child), "root": False})
    return out


def join_emitter(emitter: Emitter, candidates: list[dict]) -> dict | None:
    """The candidate an emitter belongs to, or None when no gate of the port is within JOIN_MAX_M. Within the winning barrier instance the
    nearest place wins, and the barrier root wins a tie (within TIE_M): an emitter that cannot be told from the root belongs to the
    assembly (Bay5's depot gates: both leaves stand at the frame's origin, one sound for both). The nearest place of ANY OTHER barrier
    instance must be JOIN_MARGIN_M away."""
    scored = sorted(((dist_xz(emitter.world, c["world"]), i, c) for i, c in enumerate(candidates)), key=lambda t: (t[0], t[1]))
    if not scored or scored[0][0] > JOIN_MAX_M:
        return None
    nearest = scored[0][0]
    group = scored[0][2]["group"]
    tied = [t for t in scored if t[2]["group"] == group and t[0] <= nearest + TIE_M]
    best = next((t[2] for t in tied if t[2]["root"]), tied[0][2])
    for distance, _i, other in scored:
        if other["group"] != group and distance < JOIN_MARGIN_M:
            raise GateSoundError(f"{emitter.label} is {nearest:.2f} m from {group[0]} but only {distance:.2f} m from {other['group'][0]}: the join is not clear")
    return {**best, "distance": nearest}


def build_row(emitter: Emitter, candidate: dict, clip: dict, source: dict, fit: dict, mixers: MixerBook) -> dict:
    player = emitter.player
    mixer_name = None
    mixer_db = 0.0
    if player["mixer"][1]:
        if external_name(emitter.obj, player["mixer"][0]) != MIXER_ASSET:
            raise GateSoundError(f"{emitter.label}: the player names a mixer group outside {MIXER_ASSET}")
        group = mixers.group(player["mixer"][1])
        if group["name"] != GATES_MIXER:
            raise GateSoundError(f"{emitter.label}: the player names the mixer group {group['name']}, expected {GATES_MIXER} or none")
        mixer_name, mixer_db = group["name"], group["db"]
    authored_pitch = r4(player["pitch"])
    bake = fit["clips"].get(clip["id"], {}).get("bake_pitch", 1.0)
    pitch = r4(authored_pitch / bake)
    if abs(pitch - 1.0) > 1e-3:
        raise GateSoundError(f"{emitter.label}: the clip {clip['id']} plays at pitch {authored_pitch} and bake_pitch is {bake}: the client plays at pitch 1")
    barrier, at = candidate["group"]
    return {"barrier": barrier, "part": candidate["part"], "at": list(at) if at is not None else None,
            "closed_position": list(candidate["closed"]) if candidate["closed"] is not None else None,
            "clip": clip["id"], "emitter": list(emitter.world), "source_volume": r4(source["volume"]), "player_volume": r4(player["volume"]),
            "authored_pitch": authored_pitch, "pitch": 1.0, "min_distance": r3(player["min"]), "max_distance": r3(player["max"]),
            "mixer": mixer_name, "mixer_db": mixer_db, "source_mixer": source["mixer"],
            "origin": emitter.label, "match_distance": r3(candidate["distance"])}


def borrowed_rows(key: str, rows: list[dict], fit: dict) -> list[dict]:
    out = []
    for spec in fit.get("borrowed_rows", {}).get(key, []):
        lender = [r for r in rows if r["barrier"] == spec["lender"]["barrier"] and near3(r["at"], spec["lender"]["at"], AT_TOLERANCE)]
        if len(lender) != 1:
            raise GateSoundError(f"the lender of the borrowed row {spec['barrier']} ({spec['lender']['barrier']}) is {len(lender)} rows, expected 1")
        lend = lender[0]
        offset = [e - a for e, a in zip(lend["emitter"], lend["at"])]
        row = dict(lend)
        row.update({"barrier": spec["barrier"], "part": spec["part"], "at": list(spec["at"]), "closed_position": None,
                    "emitter": [r3(a + o) for a, o in zip(spec["at"], offset)],
                    "origin": f"borrowed from {lend['barrier']}", "match_distance": None})
        out.append(row)
    return out


def sorted_rows(rows: list[dict]) -> list[dict]:
    return sorted(rows, key=lambda r: (r["barrier"], r["part"] or "", r["at"] or [], r["closed_position"] or [], r["emitter"]))


def extract_rows(key: str, data_dir: Path, arena_version: str, mixers: MixerBook | None = None) -> dict:
    """The sidecar document of one map, read from the installed Arena."""
    from extract_spatial_audio import scene_for_map
    spec = MAPS[key]
    doc = json.loads(doors_path(key).read_text(encoding="utf-8"))
    if doc["key"] != key:
        raise GateSoundError(f"{doors_path(key).name} is the sidecar of {doc['key']}")
    fit = load_fit()
    mixers = mixers or MixerBook(data_dir)
    levels = {"sound": int(scene_for_map(key).removeprefix("level")), "gate": int(doc["source"]["level"])}
    gate_scene = doors.ArenaScene(data_dir, levels["gate"])
    scenes = {"gate": gate_scene}
    if "sound" in spec["scenes"] and levels["sound"] != levels["gate"]:
        scenes["sound"] = doors.ArenaScene(data_dir, levels["sound"])
    else:
        scenes["sound"] = gate_scene
    candidates = gate_candidates(doc, gate_scene)
    assets = AssetFiles(data_dir)
    clips: dict[str, dict] = {}
    rows: list[dict] = []
    skipped: list[dict] = []
    seen: dict[tuple, str] = {}
    for scene_role in spec["scenes"]:
        scene = scenes[scene_role]
        for emitter in read_emitters(scene, levels[scene_role]):
            candidate = join_emitter(emitter, candidates)
            if candidate is None:
                skipped.append({"origin": emitter.label, "reason": f"no gate of the port within {JOIN_MAX_M:.1f} m (an emitter of another mode)"})
                continue
            identity = (candidate["group"][0], candidate["part"], candidate["group"][1], candidate["closed"])
            if identity in seen:
                skipped.append({"origin": emitter.label, "reason": f"duplicate of {seen[identity]} (same gate unit, kept)"})
                continue
            seen[identity] = emitter.label
            clip, source = clip_entry(scene, emitter, mixers, assets)
            row = build_row(emitter, candidate, clip, source, fit, mixers)
            previous = clips.setdefault(clip["id"], clip)
            if previous != clip:
                raise GateSoundError(f"two different clips are both called {clip['id']}")
            rows.append(row)
    rows += borrowed_rows(key, rows, fit)
    if not rows:
        raise GateSoundError(f"{key}: no gate sound row")
    mixer_info = {"name": GATES_MIXER, "db": GATES_MIXER_DB}
    for row in rows:
        if row["mixer"] == GATES_MIXER and row["mixer_db"] != GATES_MIXER_DB:
            raise GateSoundError(f"{row['origin']}: {GATES_MIXER} is {row['mixer_db']} dB, expected {GATES_MIXER_DB}")
    mixer_info["path"] = next((mixers.group(r_id)["path"] for r_id in sorted(mixers.cache) if mixers.cache[r_id]["name"] == GATES_MIXER), "")
    for row in rows:
        if not row_resolves(row, doc["poses"]):
            raise GateSoundError(f"{row['origin']} does not resolve to a door row of {doors_path(key).name}")
    unknown = sorted(set(clips) - set(fit["clips"]))
    if unknown:
        raise GateSoundError(f"the clips {unknown} have no entry in {FIT_FILE.name}")
    return {"schema_version": SCHEMA_VERSION, "key": key,
            "source": {"arena_version": arena_version, "gate_scene": {"scene": doc["source"]["scene"], "level": levels["gate"]},
                       "sound_scene": {"level": levels["sound"]}},
            "mixer": mixer_info, "clips": sorted(clips.values(), key=lambda c: c["id"]), "skipped": sorted(skipped, key=lambda s: s["origin"]),
            "rows": sorted_rows(rows)}


# ---- WAV, the pitch bake and the fitter ------------------------------------------------------------------------------------------------

def read_wav(data: bytes):
    """(float64 samples shaped (frames, channels) in -1..1, sample rate) of a PCM16 WAV."""
    import numpy as np
    with wave.open(io.BytesIO(data)) as w:
        if w.getsampwidth() != 2:
            raise GateSoundError("a PCM16 WAV was expected")
        channels, rate, frames = w.getnchannels(), w.getframerate(), w.getnframes()
        pcm = np.frombuffer(w.readframes(frames), dtype="<i2").astype(np.float64) / 32768.0
    return pcm.reshape(-1, channels), rate


def write_wav(samples, rate: int) -> bytes:
    """A canonical 44-byte-header PCM16 WAV of float samples (frames, channels); values are clipped to -1..1."""
    import numpy as np
    pcm = np.rint(np.clip(samples, -1.0, 1.0) * 32767.0).astype("<i2")
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as w:
        w.setnchannels(samples.shape[1])
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())
    return buffer.getvalue()


def resample_speed(samples, factor: float, half_width: int = 24, beta: float = 8.6):
    """Play `samples` (frames, channels) `factor` times faster at the same sample rate (pitch and speed both rise by `factor`): a
    windowed-sinc interpolation (Kaiser window) whose cutoff is the input Nyquist divided by `factor`, so a faster clip aliases nothing.
    The output has int(frames / factor) frames."""
    import numpy as np
    if not factor > 0:
        raise GateSoundError("a resampling factor must be positive")
    frames, channels = samples.shape
    out_frames = int(frames / factor)
    cutoff = min(1.0, 1.0 / factor)
    support = half_width / cutoff
    taps = int(math.ceil(support))
    padded = np.zeros((frames + 2 * taps + 2, channels))
    padded[taps + 1:taps + 1 + frames] = samples
    out = np.empty((out_frames, channels))
    i0 = float(np.i0(beta))
    chunk = 8192
    for start in range(0, out_frames, chunk):
        n = np.arange(start, min(out_frames, start + chunk))
        position = n * factor
        base = np.floor(position).astype(np.int64)
        acc = np.zeros((len(n), channels))
        for k in range(-taps + 1, taps + 1):
            index = base + k
            t = position - index
            u = t / support
            window = np.where(np.abs(u) < 1.0, np.i0(beta * np.sqrt(np.clip(1.0 - u * u, 0.0, 1.0))) / i0, 0.0)
            weight = cutoff * np.sinc(cutoff * t) * window
            acc += weight[:, None] * padded[index + taps + 1]
        out[start:start + len(n)] = acc
    return out


def _level_db(segment) -> float:
    import numpy as np
    return 10 * np.log10(np.mean(segment ** 2) + 1e-12)


def _centroid(segment, rate: int) -> float:
    import numpy as np
    spectrum = np.abs(np.fft.rfft(segment * np.hanning(len(segment))))
    freqs = np.fft.rfftfreq(len(segment), 1 / rate)
    return float((spectrum * freqs).sum() / max(spectrum.sum(), 1e-12))


def _band_features(segment, rate: int, bands: int = 16):
    """Log band energies (dB) of a mono segment: a rough stationarity descriptor."""
    import numpy as np
    n = len(segment)
    spectrum = np.abs(np.fft.rfft(segment * np.hanning(n))) ** 2
    freqs = np.fft.rfftfreq(n, 1 / rate)
    edges = np.geomspace(100, min(16000, rate / 2 - 1), bands + 1)
    out = []
    for i in range(bands):
        mask = (freqs >= edges[i]) & (freqs < edges[i + 1])
        out.append(10 * np.log10(spectrum[mask].sum() + 1e-12))
    return np.array(out)


def fit_cut(x, rate: int, remove_s: float, body_lo_s: float, body_hi_s: float, xfade_s: float):
    """Remove about `remove_s` seconds from the body [body_lo_s, body_hi_s] of x (frames, channels) by one cut with an equal-power crossfade.
    The cut start c0 is searched on a 10 ms grid so the 80 ms before c0 and before c1 = c0 + removed are spectrally and level alike; c1 is
    then aligned by waveform correlation within +-5 ms. Returns (y, info) with the frame indices c0 / c1 in x, the ramp (dB) put on the
    material before the seam and the crossfade length in frames. The client reproduces y from (x, c0, c1, ramp, body start, crossfade)."""
    import numpy as np
    mono = x.mean(axis=1)
    removed = int(round(remove_s * rate))
    lo = int(body_lo_s * rate)
    hi = int(body_hi_s * rate)
    window = int(FIT_WINDOW_S * rate)
    fade = int(xfade_s * rate)
    if removed <= 0:
        return x.copy(), {"removed": 0.0, "c0": 0, "c1": 0, "ramp_db": 0.0, "lo": lo, "fade": fade}
    if hi - lo < removed + 2 * window:
        removed = max(0, hi - lo - 2 * window)
    best = None
    c0 = lo + window
    while c0 + removed + window <= hi:
        a = mono[c0 - window:c0]
        b = mono[c0 + removed - window:c0 + removed]
        d = np.sqrt(np.mean((_band_features(a, rate) - _band_features(b, rate)) ** 2))
        la, lb = 10 * np.log10(np.mean(a ** 2) + 1e-12), 10 * np.log10(np.mean(b ** 2) + 1e-12)
        cost = d + 0.5 * abs(la - lb)
        if best is None or cost < best[0]:
            best = (cost, c0, d, la - lb)
        c0 += int(FIT_STEP_S * rate)
    if best is None:
        raise GateSoundError("no seam fits the body")
    _cost, c0, feature_distance, level_diff = best
    c1 = c0 + removed
    reach = int(FIT_ALIGN_S * rate)
    reference = mono[c0 - fade:c0]
    best_corr, best_shift = -2.0, 0
    for shift in range(-reach, reach + 1):
        candidate = mono[c1 - fade + shift:c1 + shift]
        if len(candidate) != len(reference):
            continue
        corr = np.dot(reference, candidate) / (np.linalg.norm(reference) * np.linalg.norm(candidate) + 1e-12)
        if corr > best_corr:
            best_corr, best_shift = corr, shift
    c1 += best_shift
    y_source = x.copy()
    ramp_db = 0.0
    if abs(level_diff) > FIT_LEVEL_TRIGGER_DB and c0 > lo + 4 * window:
        a = mono[c0 - window:c0]
        b = mono[c1 - window:c1]
        delta = 10 * np.log10(np.mean(b ** 2) + 1e-12) - 10 * np.log10(np.mean(a ** 2) + 1e-12)
        ramp_db = float(np.clip(delta, -FIT_RAMP_LIMIT_DB, FIT_RAMP_LIMIT_DB))
        y_source[lo:c0] *= (10 ** (np.linspace(0.0, ramp_db, c0 - lo) / 20.0))[:, None]
    return apply_cut(y_source, c0, c1, fade), {"removed": (c1 - c0) / rate, "c0": c0, "c1": c1, "ramp_db": ramp_db, "lo": lo, "fade": fade,
                                               "feature_distance_db": float(feature_distance), "level_diff_db": float(level_diff), "corr": float(best_corr)}


def apply_cut(x, c0: int, c1: int, fade: int):
    """x with the frames [c0, c1) removed: the `fade` frames before c0 are crossfaded (cos / sin) with the `fade` frames before c1."""
    import numpy as np
    t = np.linspace(0, np.pi / 2, fade, endpoint=False)[:, None]
    seam = x[c0 - fade:c0] * np.cos(t) + x[c1 - fade:c1] * np.sin(t)
    return np.concatenate([x[:c0 - fade], seam, x[c1:]], axis=0)


def seam_table(x, rate: int, anchor: dict, xfade_s: float) -> dict:
    """The `fit` block of a body_cut clip: the best seam for every removal on a 0.1 s grid up to 90 % of the event, with its quality."""
    import numpy as np
    event, (lo_s, hi_s) = float(anchor["event_s"]), anchor["body"]
    mono = x.mean(axis=1)
    fade = int(xfade_s * rate)
    window = int(NATURAL_WINDOW_S * rate)
    natural = []
    for t in np.linspace(lo_s + 0.2, hi_s - 0.3, 40):
        i = int(t * rate)
        natural.append(abs(_level_db(mono[i:i + window]) - _level_db(mono[i - fade - window:i - fade])))
    natural90 = float(np.percentile(natural, 90))
    seams = []
    most = (hi_s - lo_s) - 0.2
    removal = FIT_GRID_S
    while removal <= min(most, event * 0.9) + 1e-9:
        y, info = fit_cut(x, rate, removal, lo_s, hi_s, xfade_s)
        m = y.mean(axis=1)
        c = info["c0"]
        before = m[c - fade - window:c - fade]
        after = m[c:c + window]
        jump = abs(_level_db(after) - _level_db(before))
        centroid_jump = abs(_centroid(after, rate) - _centroid(before, rate))
        seams.append({"remove_s": round(info["removed"], 3), "c0": r4(info["c0"] / rate), "c1": r4(info["c1"] / rate), "c0f": int(info["c0"]), "c1f": int(info["c1"]),
                      "ramp_db": round(info["ramp_db"], 2), "jump_db": round(float(jump), 2), "centroid_jump_hz": int(centroid_jump),
                      "ok": bool(jump <= natural90 + OK_JUMP_MARGIN_DB and centroid_jump <= OK_CENTROID_HZ)})
        removal = round(removal + FIT_GRID_S, 3)
    return {"mode": "body_cut", "event_s": event, "body": [lo_s, hi_s], "body_f": [int(lo_s * rate), int(hi_s * rate)], "xfade_s": xfade_s,
            "xfade_f": fade, "natural_jump_p90_db": round(natural90, 2), "seams": seams}


# ---- the package -----------------------------------------------------------------------------------------------------------------------

def render_manifest(doc: dict) -> str:
    """The manifest text: rows and seams one per line."""
    out = ["{", f'  "schema_version": {doc["schema_version"]},', f'  "map_key": {json.dumps(doc["map_key"])},',
           f'  "arena_version": {json.dumps(doc["arena_version"])},', f'  "mixer": {_line(doc["mixer"])},', '  "rows": [',
           ",\n".join("    " + _line(row) for row in doc["rows"]), "  ],", '  "clips": [']
    clips = []
    for clip in doc["clips"]:
        lines = ["    {"]
        fit = clip["fit"]
        head = {k: v for k, v in clip.items() if k not in ("fit", "source")}
        lines.append("      " + _line(head)[1:-1] + ",")
        lines.append('      "source": ' + _line(clip["source"]) + ",")
        if fit["mode"] == "body_cut":
            fit_head = {k: v for k, v in fit.items() if k != "seams"}
            lines.append('      "fit": {' + _line(fit_head)[1:-1] + ", \"seams\": [")
            lines.append(",\n".join("        " + _line(s) for s in fit["seams"]))
            lines.append("      ]}")
        else:
            lines.append('      "fit": ' + _line(fit))
        lines.append("    }")
        clips.append("\n".join(lines))
    out.append(",\n".join(clips))
    out += ["  ]", "}"]
    return "\n".join(out) + "\n"


def prepare_clip(entry: dict, decoded: bytes, metadata: dict | None, fit_spec: dict, xfade_s: float, protected: set[str]) -> tuple[dict, bytes]:
    """The shipped bytes and the manifest entry of one clip. `decoded` is the WAV Arena's clip decodes to; a clip with `bake_pitch` is
    resampled, any other clip ships as decoded. A clip whose resource file is a key-source file must be baked."""
    resource_file = Path(entry["resource"]["file"]).name
    bake = float(fit_spec.get("bake_pitch", 1.0))
    if resource_file in protected and abs(bake - 1.0) < 1e-9:
        raise GateSoundError(f"clip {entry['id']} lives in {resource_file}, a stage-2 key-source file: bake_pitch is required so that the shipped bytes are not a copy")
    samples, rate = read_wav(decoded)
    if metadata is not None:
        if samples.shape[1] != metadata["m_Channels"] or rate != metadata["m_Frequency"]:
            raise GateSoundError(f"clip {entry['id']}: decoded format drift")
        if abs(len(samples) / rate - metadata["m_Length"]) > 2 / rate:
            raise GateSoundError(f"clip {entry['id']}: decoded duration drift")
    if not samples.any():
        raise GateSoundError(f"clip {entry['id']}: silent decoded clip")
    if abs(bake - 1.0) >= 1e-9:
        samples = resample_speed(samples, bake)
        shipped = write_wav(samples, rate)
        samples, rate = read_wav(shipped)
    else:
        shipped = decoded
    mode = fit_spec["mode"]
    if mode == "body_cut":
        fit = seam_table(samples, rate, fit_spec, xfade_s)
    elif mode == "as_is":
        fit = {"mode": "as_is"}
    else:
        raise GateSoundError(f"clip {entry['id']}: unknown fit mode {mode}")
    manifest_entry = {"id": entry["id"], "file": entry["id"] + ".wav", "channels": samples.shape[1], "sampleRate": rate, "frames": len(samples),
                      "bytes": len(shipped), "sha256": hashlib.sha256(shipped).hexdigest(), "pitch_baked": abs(bake - 1.0) >= 1e-9, "baked_pitch": bake,
                      "source": {"asset": entry["asset"], "path_id": entry["path_id"], "format": entry["format"], "resource": entry["resource"]},
                      "fit": fit}
    return manifest_entry, shipped


def build_package(key: str, rows_doc: dict, decode, output: Path, fit: dict, arena_version: str) -> dict:
    """Write work/extract/gate-sounds/<key>: one WAV per clip and the manifest. `decode(entry)` returns (WAV bytes, AudioClip typetree or
    None). The folder is replaced file by file; nothing else is touched."""
    protected = key_source_files()
    clips, shipped = [], {}
    for entry in rows_doc["clips"]:
        if entry["id"] not in fit["clips"]:
            raise GateSoundError(f"clip {entry['id']} has no entry in {FIT_FILE.name}")
        wav, metadata = decode(entry)
        manifest_entry, data = prepare_clip(entry, wav, metadata, fit["clips"][entry["id"]], float(fit["xfade_s"]), protected)
        clips.append(manifest_entry)
        shipped[manifest_entry["file"]] = data
    manifest = {"schema_version": SCHEMA_VERSION, "map_key": key, "arena_version": arena_version, "mixer": rows_doc["mixer"], "rows": rows_doc["rows"], "clips": clips}
    output.mkdir(parents=True, exist_ok=True)
    for name, data in shipped.items():
        (output / name).write_bytes(data)
    (output / MANIFEST_NAME).write_text(render_manifest(manifest), encoding="utf-8", newline="\n")
    stale = sorted(p.name for p in output.iterdir() if p.is_file() and p.name not in shipped and p.name != MANIFEST_NAME)
    for name in stale:
        (output / name).unlink()
    return manifest


def arena_decoder(data_dir: Path):
    """decode(entry) reading the clip from the installed Arena (UnityPy + fmod for Vorbis)."""
    import UnityPy
    loaded: dict[str, object] = {}

    def decode(entry: dict):
        name = entry["asset"]
        if name not in loaded:
            loaded[name] = UnityPy.load(str(data_dir / name))
        for obj in loaded[name].objects:
            if obj.type.name == "AudioClip" and obj.path_id == entry["path_id"] and Path(obj.assets_file.name).name == name:
                samples = obj.read().samples
                if len(samples) != 1:
                    raise GateSoundError(f"clip {entry['id']} decodes to {len(samples)} WAV files")
                data = next(iter(samples.values()))
                if data[:4] != b"RIFF":
                    raise GateSoundError(f"clip {entry['id']}: expected a decoded WAV")
                tt = obj.read_typetree()
                if tt["m_Name"] != entry["id"]:
                    raise GateSoundError(f"{name}:{entry['path_id']} is {tt['m_Name']}, expected {entry['id']}")
                return data, tt
        raise GateSoundError(f"clip {entry['id']} ({name}:{entry['path_id']}) not found")

    return decode


# ---- CLI -------------------------------------------------------------------------------------------------------------------------------

def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("step", choices=("rows", "clips"))
    parser.add_argument("--map", action="append", dest="maps", choices=sorted(MAPS), metavar="ARENA_KEY", help="repeatable; default: the four released maps")
    parser.add_argument("--arena", default="auto", help="the Arena installation (or its EscapeFromTarkovArena_Data folder); auto = the launcher's entry")
    parser.add_argument("--check", action="store_true", help="rows: compare with the tracked sidecars and write nothing")
    parser.add_argument("--output", type=Path, default=OUTPUT_ROOT, help="clips: the folder that gets one subfolder per map key")
    args = parser.parse_args(argv)
    try:
        data_dir, version = doors.find_arena(args.arena)
        mixers = None
        fit = load_fit()
        status = 0
        for key in args.maps or sorted(MAPS):
            if args.step == "rows":
                mixers = mixers or MixerBook(data_dir)
                doc = extract_rows(key, data_dir, version, mixers)
                text = render_rows_sidecar(doc)
                path = sidecar_path(key)
                if args.check:
                    same = path.is_file() and path.read_bytes().replace(b"\r\n", b"\n") == text.encode("utf-8")
                    print(f"{key}: {len(doc['rows'])} row(s), {len(doc['clips'])} clip(s), {len(doc['skipped'])} skipped; {path.name} {'is current' if same else 'DIFFERS'}")
                    status |= 0 if same else 1
                else:
                    path.write_text(text, encoding="utf-8", newline="\n")
                    print(f"{key}: wrote {path.name}: {len(doc['rows'])} row(s), {len(doc['clips'])} clip(s), {len(doc['skipped'])} skipped")
            else:
                path = sidecar_path(key)
                if not path.is_file():
                    raise GateSoundError(f"{path.name} does not exist; run the rows step first")
                rows_doc = json.loads(path.read_text(encoding="utf-8"))
                manifest = build_package(key, rows_doc, arena_decoder(data_dir), args.output / key, fit, version)
                total = sum(c["bytes"] for c in manifest["clips"])
                print(f"{key}: {len(manifest['clips'])} clip(s), {total} bytes of WAV, "
                      + ", ".join(f"{c['id']} {c['fit']['mode']}" + (f" ({sum(1 for s in c['fit']['seams'] if s['ok'])}/{len(c['fit']['seams'])} seams ok)" if c['fit']['mode'] == 'body_cut' else "") for c in manifest["clips"]))
        return status
    except (GateSoundError, doors.DoorPoseError, OSError, ValueError, KeyError) as exc:
        print(f"[extract_gate_sounds] FAILED: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
