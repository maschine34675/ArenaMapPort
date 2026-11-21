"""Builds the per-map grass packs (plaintext of the staged <map_key>_grass.bundle) from the player's own Arena installation.

python tools/extract/extract_grass.py pack   --map bowl|iceberg|yard|all [--out-dir DIR]
python tools/extract/extract_grass.py verify --map bowl|iceberg|yard|all [--pack-dir DIR]

Arena (Bowl, Iceberg and Yard) grows its grass from three things: a placement file (StreamingAssets/Grass/ArenaGrassPrefabs_<Map>.pcl, 68-byte records), three textures of
sharedassets95.assets (the albedo array GrassTexArray, the normal array GrassNormalArray and the normal texture Grass_new_1_N, their texels in sharedassets95.assets.resS) and the colour
lists of the scene's GrassInitialization. The three maps use the SAME prefab, prototype and art set; only the placement file differs. A pack (tools/extract/grass_pack.py, format "AMGP"
version 1) carries one map's placement file byte for byte and its own copy of the three art ranges byte for byte, so the client needs no Arena file at raid time.

What the tool does, in order, and what stops it (every stop names the cause):
  * reads the installation read-only (ARENAMAPPORT_ARENA_DATA = the ..._Data folder, else ARENAMAPPORT_ARENA_DIR + EscapeFromTarkovArena_Data, else the default install);
  * locates the three textures by reading sharedassets95.assets with UnityPy (width, height, depth, mip count, format, filter, wrap, aniso, stream path, offset, size) and compares
    them with ART_PINS, the numbers measured on Arena 0.6.2.0.48473: any difference stops the tool ("Arena changed"), because a stage-2 reader and the shipped packs rest on them;
  * checks each range: size = slices x padded mip chain (grass_pack arithmetic), inside the .resS, not one repeated byte, and the slice-major mip-chain coherence that the client's
    structure check uses (a read shifted by a few bytes fails it);
  * checks each map's placement file: size divisible by 68, at least one record, finite numbers, an affine matrix (last row 0 0 0 1), positive finite scales, slice index below the
    albedo array's depth;
  * proves per map, from the map's level file, that its GrassInitialization names the expected placement file, the shared prefab, the five-entry PrefabList of sharedassets95, the one
    prototype and the one prefab of both managers, and that its DryColor and HealthyColor lists equal, float for float, the summer constants of ArenaMapPort.Client/
    NativeGrassColorModel.cs (parsed from that source; the season rule of the port is "Arena's summer lists, always"); a map that differs stops the tool: its lists would have to become
    per-map constants;
  * writes work/build/grass/<map_key>_grass.bundle atomically and prints one summary line per map (no hash, no digest).
The output is deterministic: running the tool twice gives identical bytes. `verify` parses a written pack and compares it with the installation.

NEVER STAGE A PACK PLAIN. The art ranges are bytes of sharedassets95.assets.resS, which is a key-source file of Bowl (tools/bundle/key-sources.json: the content keys of the encrypted
bundles are derived from sampled blocks of it), so a plaintext pack would publish key material. A pack goes to dist/ only through the encrypting staging step of the client build
(the BundleCrypt tool's stage command), as an AMPB container. The tool therefore writes only under work/ and refuses any other place, dist/ included.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import grass_pack as gp  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
COLOR_MODEL = REPO / "ArenaMapPort.Client" / "NativeGrassColorModel.cs"
DEFAULT_ARENA_DIR = r"D:\Battlestate Games\Escape from Tarkov Arena"
DATA_FOLDER = "EscapeFromTarkovArena_Data"
ART_ASSETS = "sharedassets95.assets"
ART_STREAM = "sharedassets95.assets.resS"
GGM = "globalgamemanagers.assets"

# The measured builds. A different build is not refused by name (the pins decide), it is only reported.
MEASURED_BUILDS = ("0.6.1.", "0.6.2.")

# The pinned facts of the art (UnityPy on Arena 0.6.2.0.48473, 2026-11-21; G2 section 5). `kind` is the pack kind, `type` the Unity class.
ASSETS_SIZE = 13940
STREAM_SIZE = 11185040
ART_PINS = {
    "Grass_new_1_N": dict(kind=gp.KIND_NORMAL_TEXTURE, type="Texture2D", width=1024, height=1024, slices=1, mips=11, format_field="m_TextureFormat", format_id=10,
                          offset=0, size=699064, filter=2, wrap=1, aniso=16, ignore_limit=False),
    "GrassTexArray": dict(kind=gp.KIND_ALBEDO, type="Texture2DArray", width=1024, height=1024, slices=5, mips=11, format_field="m_Format", format_id=101,
                          offset=699072, size=6990640, filter=2, wrap=1, aniso=1, ignore_limit=True),
    "GrassNormalArray": dict(kind=gp.KIND_NORMAL_ARRAY, type="Texture2DArray", width=1024, height=1024, slices=5, mips=11, format_field="m_Format", format_id=97,
                             offset=7689712, size=3495320, filter=2, wrap=1, aniso=1, ignore_limit=True),
}
PACK_FORMAT_OF_KIND = {gp.KIND_ALBEDO: gp.FORMAT_DXT5, gp.KIND_NORMAL_ARRAY: gp.FORMAT_DXT1, gp.KIND_NORMAL_TEXTURE: gp.FORMAT_DXT1}

# Largest mean colour difference of the mip-chain coherence check (the client's MaxMeanBlockDifference); the first and last mip compared (NativeGrassArt.CoherenceFirstMip / LastMip).
MAX_MEAN_BLOCK_DIFFERENCE = 2.0
COHERENCE_FIRST_MIP = 2
COHERENCE_LAST_MIP = 7

# Per map: the level file that holds its Grass scene, the placement file, the port's scene name (tools/maps/<stem>.json lists it).
MAPS = {
    "bowl": dict(key="arena_bowl", level="level95", pcl="ArenaGrassPrefabs_Bowl.pcl", scene="Arena_Bowl_Grass"),
    "iceberg": dict(key="arena_iceberg", level="level260", pcl="ArenaGrassPrefabs_Sanatorium.pcl", scene="Arena_Sanatorium_Grass"),
    "yard": dict(key="arena_yard", level="level162", pcl="ArenaGrassPrefabs_Yard.pcl", scene="Arena_Yard_Grass"),
}

# What every map's grass scene must reference (G2 section 7): objects of sharedassets95.assets.
SHARED_PREFAB_ID = 85          # GrassInitialization.prefab
SHARED_PREFAB_LIST = (26, 24, 14, 21, 25)
SHARED_PROTOTYPE_ID = 75       # both managers' prototypeList
SHARED_MANAGER_PREFAB_ID = 15  # both managers' prefabList
GRASS_INIT_CLASS = "GPUInstancer.GrassInitialization"
MANAGER_CLASS = "GPUInstancer.GPUInstancerPrefabManager"
COLOR_LIST_LENGTH = 5

# Sanity limits of the placement (broken-file detection, not Yard's box): world positions and scales.
MAX_ABS_POSITION = 20000.0
MAX_SCALE = 100.0
MAX_RECORDS = 2_000_000
AFFINE_TOLERANCE = 1e-4


class ExtractError(Exception):
    """A stop with a cause the user can act on."""


# ---- the installation ------------------------------------------------------------------------------------------------------------------------------------------------------

def arena_data_dir() -> Path:
    env = os.environ.get("ARENAMAPPORT_ARENA_DATA")
    if env:
        return Path(env)
    root = os.environ.get("ARENAMAPPORT_ARENA_DIR") or DEFAULT_ARENA_DIR
    return Path(root) / DATA_FOLDER


def arena_version(data_dir: Path) -> str:
    try:
        info = json.loads((data_dir.parent / "ConsistencyInfo").read_bytes().decode("utf-8-sig"))
        return str(info.get("Version", ""))
    except (OSError, ValueError):
        return ""


def work_root() -> Path:
    env = os.environ.get("ARENAMAPPORT_WORK_DIR")
    return Path(env) if env else REPO / "work"


def checked_out_dir(path) -> Path:
    """The output folder, only under work/ and never under a dist folder (a plaintext pack must not be staged; see the header)."""
    p = Path(path).resolve()
    root = work_root().resolve()
    try:
        p.relative_to(root)
    except ValueError:
        raise ExtractError("refusing to write '%s': a grass pack is written only under %s" % (p, root))
    if any(part.lower() == "dist" for part in p.parts):
        raise ExtractError("refusing to write '%s': a plaintext pack never goes near dist/" % p)
    return p


def default_pack_dir() -> Path:
    return work_root() / "build" / "grass"


def pack_path(directory: Path, map_key: str) -> Path:
    return directory / (map_key + "_grass.bundle")


# ---- summer colour constants of the client ---------------------------------------------------------------------------------------------------------------------------------

def _f32(token: str) -> float:
    return struct.unpack("<f", struct.pack("<f", float(token)))[0]


def client_summer_lists(source: Path = COLOR_MODEL):
    """(dry, healthy): the SummerDry and SummerHealthy float arrays of NativeGrassColorModel.cs as lists of five RGBA tuples of float32 values."""
    try:
        text = source.read_text(encoding="utf-8")
    except OSError as e:
        raise ExtractError("cannot read %s: %s" % (source, e))
    lists = []
    for name in ("SummerDry", "SummerHealthy"):
        m = re.search(r"\b" + name + r"\s*=\s*\{(.*?)\}\s*;", text, re.S)
        if not m:
            raise ExtractError("%s has no float array named %s (the colour lists became per-map data? then this tool must change too)" % (source.name, name))
        values = [_f32(t) for t in re.findall(r"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?(?=f\b)", m.group(1))]
        if len(values) != COLOR_LIST_LENGTH * 4:
            raise ExtractError("%s %s holds %d numbers, expected %d" % (source.name, name, len(values), COLOR_LIST_LENGTH * 4))
        lists.append([tuple(values[i * 4:i * 4 + 4]) for i in range(COLOR_LIST_LENGTH)])
    return lists[0], lists[1]


# ---- reading Arena's serialized files --------------------------------------------------------------------------------------------------------------------------------------

class Cursor:
    """A cursor over a MonoBehaviour body with Unity's serialization rules (bool aligned to 4 after each field, strings 4-aligned, counts int32)."""

    def __init__(self, raw: bytes, pos: int = 0):
        self.b = raw
        self.p = pos

    def i32(self):
        v = struct.unpack_from("<i", self.b, self.p)[0]
        self.p += 4
        return v

    def i64(self):
        v = struct.unpack_from("<q", self.b, self.p)[0]
        self.p += 8
        return v

    def f32(self):
        v = struct.unpack_from("<f", self.b, self.p)[0]
        self.p += 4
        return v

    def boolean(self):
        v = self.b[self.p]
        self.p += 1
        self.p = (self.p + 3) & ~3
        if v not in (0, 1):
            raise ExtractError("a bool field holds %d" % v)
        return bool(v)

    def pptr(self):
        return (self.i32(), self.i64())

    def vec3(self):
        return [self.f32() for _ in range(3)]

    def string(self):
        n = self.i32()
        v = bytes(self.b[self.p:self.p + n]).decode("utf-8")
        self.p += n
        self.p = (self.p + 3) & ~3
        return v

    def flist(self):
        return [self.f32() for _ in range(self.i32())]

    def plist(self):
        return [self.pptr() for _ in range(self.i32())]

    def clist(self):
        return [tuple(self.f32() for _ in range(4)) for _ in range(self.i32())]

    def left(self):
        return len(self.b) - self.p


def _behaviour_header(c: Cursor):
    c.pptr()
    c.boolean()
    c.pptr()
    c.string()


def decode_grass_initialization(raw: bytes) -> dict:
    c = Cursor(raw)
    _behaviour_header(c)
    d = {"GrassParent": c.pptr(), "prefab": c.pptr(), "PrefabList": c.plist(), "DryColor": c.clist(), "HealthyColor": c.clist(), "WinterColors": c.clist(), "SeasonsColors": c.clist(),
         "FileName": c.string()}
    if c.left() != 0:
        raise ExtractError("GrassInitialization: %d bytes left after the known fields (the layout changed)" % c.left())
    return d


def decode_prefab_manager(raw: bytes) -> dict:
    c = Cursor(raw)
    _behaviour_header(c)
    d = {"IsOptic": c.boolean(), "prototypeList": c.plist()}
    c.pptr(); c.pptr(); c.pptr()          # mainCamera, mainCameraSSAA, hiZOcclusionGenerator
    c.flist(); c.flist(); c.vec3()        # mvp matrices, camera position
    c.boolean(); c.f32()                  # hasOcclusionGenerator, halfAngle
    c.boolean(); c.pptr(); c.pptr(); c.pptr()   # floating origin
    c.vec3(); c.vec3()                    # instancing bounds
    c.boolean(); c.boolean(); c.f32(); c.i32(); c.boolean(); c.i32()   # culling, min distance, threads, isInitialized, layerMask
    for _ in range(c.i32()):
        c.pptr()
        c.plist()
    d["prefabList"] = c.plist()
    c.boolean(); c.boolean(); c.boolean()
    if c.left() != 0:
        raise ExtractError("GPUInstancerPrefabManager: %d bytes left after the known fields (the layout changed)" % c.left())
    return d


def _external_names(sf) -> list:
    out = []
    for e in sf.externals:
        path = getattr(e, "path", None) or str(e)
        out.append(os.path.basename(str(path).replace("\\", "/")))
    return out


def _resolve(externals: list, pptr) -> tuple:
    fid, pid = pptr
    if fid == 0:
        return ("<self>", pid)
    if fid < 0 or fid > len(externals):
        raise ExtractError("a PPtr names external file %d of %d" % (fid, len(externals)))
    return (externals[fid - 1], pid)


class ScriptNames:
    """MonoScript path id -> 'Namespace.Class' of globalgamemanagers.assets (the file every level file's scripts live in)."""

    def __init__(self, data_dir: Path):
        import UnityPy
        env = UnityPy.load(str(data_dir / GGM))
        self.names = {}
        for o in env.objects:
            if o.type.name == "MonoScript":
                tt = o.read_typetree()
                ns = tt.get("m_Namespace") or ""
                self.names[o.path_id] = (ns + "." if ns else "") + tt.get("m_ClassName", "")


def read_scene_grass(data_dir: Path, level: str, scripts: ScriptNames) -> dict:
    """The GrassInitialization and the two prefab managers of one level file, decoded and resolved. Everything read from the file; nothing assumed."""
    import UnityPy
    env = UnityPy.load(str(data_dir / level))
    files = list(env.files.values())
    if len(files) != 1:
        raise ExtractError("%s: expected one serialized file, found %d" % (level, len(files)))
    externals = _external_names(files[0])
    inits, managers = [], []
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        raw = o.get_raw_data()
        if len(raw) < 28:
            continue
        fid, pid = struct.unpack_from("<iq", raw, 16)
        if fid < 1 or fid > len(externals) or externals[fid - 1] != GGM:
            continue
        cls = scripts.names.get(pid, "")
        if cls == GRASS_INIT_CLASS:
            inits.append(decode_grass_initialization(raw))
        elif cls == MANAGER_CLASS:
            managers.append(decode_prefab_manager(raw))
    if len(inits) != 1:
        raise ExtractError("%s: expected exactly one %s, found %d" % (level, GRASS_INIT_CLASS, len(inits)))
    if sorted(m["IsOptic"] for m in managers) != [False, True]:
        raise ExtractError("%s: expected one normal and one optic %s, found %d" % (level, MANAGER_CLASS, len(managers)))
    init = inits[0]
    return {
        "externals": externals,
        "init": init,
        "prefab": _resolve(externals, init["prefab"]),
        "prefab_list": [_resolve(externals, p) for p in init["PrefabList"]],
        "manager_prototypes": [[_resolve(externals, p) for p in m["prototypeList"]] for m in managers],
        "manager_prefabs": [[_resolve(externals, p) for p in m["prefabList"]] for m in managers],
    }


def prove_scene(stem: str, scene: dict, dry, healthy) -> list:
    """Raises ExtractError unless the scene is the shared grass setup of the map; returns the proof lines."""
    row = MAPS[stem]
    want = lambda i: (ART_ASSETS, i)  # noqa: E731
    init = scene["init"]
    if init["FileName"] != row["pcl"]:
        raise ExtractError("%s: its GrassInitialization names '%s', expected '%s'" % (row["level"], init["FileName"], row["pcl"]))
    if scene["prefab"] != want(SHARED_PREFAB_ID):
        raise ExtractError("%s: GrassInitialization.prefab is %s, expected the shared prefab %s" % (row["level"], scene["prefab"], want(SHARED_PREFAB_ID)))
    if scene["prefab_list"] != [want(i) for i in SHARED_PREFAB_LIST]:
        raise ExtractError("%s: PrefabList is %s, expected the shared list" % (row["level"], scene["prefab_list"]))
    for protos in scene["manager_prototypes"]:
        if protos != [want(SHARED_PROTOTYPE_ID)]:
            raise ExtractError("%s: a manager's prototype list is %s, expected the one shared prototype" % (row["level"], protos))
    for prefabs in scene["manager_prefabs"]:
        if prefabs != [want(SHARED_MANAGER_PREFAB_ID)]:
            raise ExtractError("%s: a manager's prefab list is %s, expected the one shared prefab" % (row["level"], prefabs))
    for label, got, expect in (("DryColor", init["DryColor"], dry), ("HealthyColor", init["HealthyColor"], healthy)):
        if len(got) != COLOR_LIST_LENGTH:
            raise ExtractError("%s: %s has %d entries, expected %d" % (row["level"], label, len(got), COLOR_LIST_LENGTH))
        for i, (g, e) in enumerate(zip(got, expect)):
            if tuple(g) != tuple(e):
                raise ExtractError("%s: %s[%d] is %s but NativeGrassColorModel.cs says %s: the colour lists differ per map and must become per-map constants" % (row["level"], label, i, g, e))
    return ["shared prefab/prototype/art set (sharedassets95: prefab %d, list %s, prototype %d)" % (SHARED_PREFAB_ID, "/".join(str(i) for i in SHARED_PREFAB_LIST), SHARED_PROTOTYPE_ID),
            "DryColor and HealthyColor equal the summer constants of NativeGrassColorModel.cs (float for float)"]


# ---- the art ------------------------------------------------------------------------------------------------------------------------------------------------------------------

def read_art_headers(data_dir: Path) -> dict:
    """name -> the pinned facts as UnityPy reads them, after comparing every one with ART_PINS."""
    import UnityPy
    assets = data_dir / ART_ASSETS
    stream = data_dir / ART_STREAM
    try:
        assets_size, stream_size = assets.stat().st_size, stream.stat().st_size
    except OSError as e:
        raise ExtractError("the Arena grass art is missing: %s" % e)
    if assets_size != ASSETS_SIZE:
        raise ExtractError("%s is %d bytes, pinned %d: Arena changed" % (ART_ASSETS, assets_size, ASSETS_SIZE))
    if stream_size != STREAM_SIZE:
        raise ExtractError("%s is %d bytes, pinned %d: Arena changed" % (ART_STREAM, stream_size, STREAM_SIZE))
    env = UnityPy.load(str(assets))
    found = {}
    for obj in env.objects:
        if obj.type.name in ("Texture2D", "Texture2DArray"):
            tt = obj.read_typetree()
            found[tt["m_Name"]] = (obj.type.name, tt)
    if sorted(found) != sorted(ART_PINS):
        raise ExtractError("%s holds the textures %s, expected %s: Arena changed" % (ART_ASSETS, sorted(found), sorted(ART_PINS)))
    out = {}
    for name, pin in ART_PINS.items():
        kind, tt = found[name]
        sd, ts = tt["m_StreamData"], tt["m_TextureSettings"]
        got = dict(type=kind, width=tt["m_Width"], height=tt["m_Height"], slices=tt["m_Depth"] if kind == "Texture2DArray" else 1, mips=tt["m_MipCount"],
                   format_id=tt[pin["format_field"]], offset=sd["offset"], size=sd["size"], filter=ts["m_FilterMode"], wrap=ts["m_WrapU"], aniso=ts["m_Aniso"],
                   ignore_limit=bool(tt["m_IgnoreMipmapLimit"]))
        for field in got:
            if got[field] != pin[field]:
                raise ExtractError("%s: %s is %r, pinned %r: Arena changed" % (name, field, got[field], pin[field]))
        if sd["path"] != ART_STREAM:
            raise ExtractError("%s: its stream is '%s', pinned '%s'" % (name, sd["path"], ART_STREAM))
        if ts["m_WrapV"] != ts["m_WrapU"] or ("m_WrapW" in ts and ts["m_WrapW"] != ts["m_WrapU"]):
            raise ExtractError("%s: the wrap modes differ per axis; the pack carries one" % name)
        if tt["m_ColorSpace"] != 1:
            raise ExtractError("%s: colour space %r, pinned 1" % (name, tt["m_ColorSpace"]))
        bb = gp.BLOCK_BYTES[PACK_FORMAT_OF_KIND[pin["kind"]]]
        want = gp.texture_bytes(pin["width"], pin["height"], pin["slices"], pin["mips"], bb)
        if want != pin["size"]:
            raise ExtractError("%s: the pinned size %d is not the mip-chain arithmetic %d" % (name, pin["size"], want))
        out[name] = got
    ranges = sorted((p["offset"], p["offset"] + p["size"], n) for n, p in ART_PINS.items())
    previous = 0
    for start, end, name in ranges:
        if start < previous or end > STREAM_SIZE:
            raise ExtractError("%s: the range overlaps another or lies outside the stream" % name)
        previous = end
    return out


def read_art_ranges(data_dir: Path) -> dict:
    """name -> bytes of each range, read from the .resS with one seek and one exact read."""
    out = {}
    with open(data_dir / ART_STREAM, "rb") as f:
        for name, pin in ART_PINS.items():
            f.seek(pin["offset"])
            data = f.read(pin["size"])
            if len(data) != pin["size"]:
                raise ExtractError("%s: short read, %d of %d bytes" % (name, len(data), pin["size"]))
            out[name] = data
    return out


def _block_means(buf, offset: int, count: int, bpb: int):
    import numpy as np
    a = np.frombuffer(buf, dtype=np.uint8, count=count * bpb, offset=offset).reshape(count, bpb)[:, bpb - 8:]
    c0 = a[:, 0].astype(np.uint16) | (a[:, 1].astype(np.uint16) << 8)
    c1 = a[:, 2].astype(np.uint16) | (a[:, 3].astype(np.uint16) << 8)

    def rgb(c):
        return np.stack([((c >> 11) & 31) * 255.0 / 31.0, ((c >> 5) & 63) * 255.0 / 63.0, (c & 31) * 255.0 / 31.0], axis=1)
    p0, p1 = rgb(c0), rgb(c1)
    four = (c0 > c1)[:, None]
    p2 = np.where(four, (2 * p0 + p1) / 3.0, (p0 + p1) / 2.0)
    p3 = np.where(four, (p0 + 2 * p1) / 3.0, np.zeros_like(p0))
    word = a[:, 4].astype(np.uint32) | (a[:, 5].astype(np.uint32) << 8) | (a[:, 6].astype(np.uint32) << 16) | (a[:, 7].astype(np.uint32) << 24)
    mean = np.zeros((count, 3))
    for j, p in enumerate((p0, p1, p2, p3)):
        n = np.zeros(count)
        for i in range(16):
            n += ((word >> (2 * i)) & 3) == j
        mean += n[:, None] * p
    return mean / 16.0


def check_range_blocks(name: str, data: bytes) -> float:
    """The client's structure check: not one repeated byte (mip 3 of every slice) and the slice-major chain coherence. Returns the worst slice's mean difference."""
    pin = ART_PINS[name]
    bpb = gp.BLOCK_BYTES[PACK_FORMAT_OF_KIND[pin["kind"]]]
    w, h, mips, slices = pin["width"], pin["height"], pin["mips"], pin["slices"]
    per_slice = gp.slice_bytes(w, h, mips, bpb)

    def mip_at(s, k):
        return s * per_slice + sum(gp.mip_bytes(w, h, m, bpb) for m in range(k))
    worst = 0.0
    import numpy as np
    for s in range(slices):
        at = mip_at(s, 3)
        part = data[at:at + gp.mip_bytes(w, h, 3, bpb)]
        if len(set(part)) <= 1:
            raise ExtractError("%s: slice %d holds one repeated byte at mip 3 (a wiped or zero-filled file)" % (name, s))
        total, used = 0.0, 0
        for k in range(COHERENCE_FIRST_MIP, COHERENCE_LAST_MIP + 1):
            if k + 1 >= mips:
                break
            across, down = max(1, (max(1, w >> k) + 3) // 4), max(1, (max(1, h >> k) + 3) // 4)
            pacross, pdown = max(1, (max(1, w >> (k + 1)) + 3) // 4), max(1, (max(1, h >> (k + 1)) + 3) // 4)
            if across != pacross * 2 or down != pdown * 2:
                break
            child = _block_means(data, mip_at(s, k), across * down, bpb).reshape(pdown, 2, pacross, 2, 3).mean(axis=(1, 3))
            parent = _block_means(data, mip_at(s, k + 1), pacross * pdown, bpb).reshape(pdown, pacross, 3)
            total += float(np.abs(child - parent).mean())
            used += 1
        diff = total / used if used else 0.0
        if diff > MAX_MEAN_BLOCK_DIFFERENCE:
            raise ExtractError("%s: slice %d mip chain is incoherent (mean difference %.2f > %.1f): the offsets or the slice order are wrong" % (name, s, diff, MAX_MEAN_BLOCK_DIFFERENCE))
        worst = max(worst, diff)
    return worst


# ---- the placement ------------------------------------------------------------------------------------------------------------------------------------------------------------

def placement_stats(raw: bytes, slice_count: int) -> dict:
    """Checks a placement file and returns its statistics. Raises ExtractError for a broken file."""
    import numpy as np
    if len(raw) % gp.RECORD_SIZE:
        raise ExtractError("the placement size %d is not divisible by %d" % (len(raw), gp.RECORD_SIZE))
    count = len(raw) // gp.RECORD_SIZE
    if not 1 <= count <= MAX_RECORDS:
        raise ExtractError("the placement holds %d records" % count)
    a = np.frombuffer(raw, dtype=np.dtype([("slice", "<i4"), ("m", "<f4", (16,))]))
    m = a["m"]
    if not np.all(np.isfinite(m)):
        raise ExtractError("the placement has non-finite numbers")
    if int(a["slice"].min()) < 0 or int(a["slice"].max()) >= slice_count:
        raise ExtractError("a slice index lies outside 0..%d (found %d..%d)" % (slice_count - 1, a["slice"].min(), a["slice"].max()))
    if np.abs(m[:, [3, 7, 11]]).max() > AFFINE_TOLERANCE or np.abs(m[:, 15] - 1.0).max() > AFFINE_TOLERANCE:
        raise ExtractError("a placement matrix is not affine (last row is not 0 0 0 1)")
    scales = np.stack([np.linalg.norm(m[:, 4 * c:4 * c + 3], axis=1) for c in range(3)], axis=1)
    if scales.min() <= 0.0 or scales.max() > MAX_SCALE:
        raise ExtractError("a placement scale is outside (0, %g]: %g..%g" % (MAX_SCALE, scales.min(), scales.max()))
    pos = m[:, 12:15]
    if np.abs(pos).max() > MAX_ABS_POSITION:
        raise ExtractError("a placement position is beyond %g m" % MAX_ABS_POSITION)
    up = m[:, 4:7] / scales[:, 1:2]
    tilt = float(np.degrees(np.arccos(np.clip(up[:, 1], -1.0, 1.0))).max())
    return {"count": count, "hist": np.bincount(a["slice"], minlength=slice_count).tolist(), "lo": pos.min(axis=0).tolist(), "hi": pos.max(axis=0).tolist(),
            "scale_min": float(scales.min()), "scale_max": float(scales.max()), "tilt_max": tilt,
            "nonuniform_max": float(np.max(np.abs(scales[:, 0] / scales[:, 1] - 1.0)))}


def summary_line(stem: str, stats: dict, pack_size: int) -> str:
    return ("map=%s key=%s records=%d slices=%s extent x %.2f..%.2f y %.2f..%.2f z %.2f..%.2f scale %.3f..%.3f tilt<=%.1fdeg nonuniform<=%.2f pack=%d B" % (
        stem, MAPS[stem]["key"], stats["count"], "/".join(str(n) for n in stats["hist"]), stats["lo"][0], stats["hi"][0], stats["lo"][1], stats["hi"][1],
        stats["lo"][2], stats["hi"][2], stats["scale_min"], stats["scale_max"], stats["tilt_max"], stats["nonuniform_max"], pack_size))


# ---- pack and verify ----------------------------------------------------------------------------------------------------------------------------------------------------------

def facts_from_pins(headers: dict) -> dict:
    def tf(name):
        h = headers[name]
        return gp.TexFacts(h["width"], h["height"], h["slices"], h["mips"], h["filter"], h["wrap"], h["aniso"])
    return {"albedo": tf("GrassTexArray"), "normal_array": tf("GrassNormalArray"), "normal_texture": tf("Grass_new_1_N")}


def resolve_maps(arg: str) -> list:
    if arg == "all":
        return list(MAPS)
    if arg not in MAPS:
        raise ExtractError("unknown map '%s' (bowl, iceberg, yard or all)" % arg)
    return [arg]


def read_placement(data_dir: Path, stem: str) -> bytes:
    path = data_dir / "StreamingAssets" / "Grass" / MAPS[stem]["pcl"]
    try:
        return path.read_bytes()
    except OSError as e:
        raise ExtractError("cannot read the placement file of %s: %s" % (stem, e))


def cmd_pack(maps: list, out_dir: Path) -> int:
    data_dir = arena_data_dir()
    if not (data_dir / ART_ASSETS).is_file():
        raise ExtractError("no Arena installation at %s (set ARENAMAPPORT_ARENA_DATA or ARENAMAPPORT_ARENA_DIR)" % data_dir)
    out_dir = checked_out_dir(out_dir)
    version = arena_version(data_dir)
    print("arena=%s build=%s%s" % (data_dir, version or "unknown", "" if version.startswith(MEASURED_BUILDS) else " (not a measured build; the pins decide)"))
    dry, healthy = client_summer_lists()
    headers = read_art_headers(data_dir)
    print("art: pins ok (%s %d B, %s %d B)" % (ART_ASSETS, ASSETS_SIZE, ART_STREAM, STREAM_SIZE))
    ranges = read_art_ranges(data_dir)
    for name in ART_PINS:
        worst = check_range_blocks(name, ranges[name])
        print("art: %s %d B, structure ok (worst slice mean difference %.2f)" % (name, ART_PINS[name]["size"], worst))
    scripts = ScriptNames(data_dir)
    facts = facts_from_pins(headers)
    out_dir.mkdir(parents=True, exist_ok=True)
    for stem in maps:
        row = MAPS[stem]
        scene = read_scene_grass(data_dir, row["level"], scripts)
        proof = prove_scene(stem, scene, dry, healthy)
        placement = read_placement(data_dir, stem)
        stats = placement_stats(placement, ART_PINS["GrassTexArray"]["slices"])
        blob = gp.build(row["key"], placement, ranges["GrassTexArray"], ranges["GrassNormalArray"], ranges["Grass_new_1_N"], facts)
        target = pack_path(out_dir, row["key"])
        tmp = target.with_name(target.name + ".tmp")
        tmp.write_bytes(blob)
        os.replace(tmp, target)
        print(summary_line(stem, stats, len(blob)))
        for line in proof:
            print("  proof: " + line)
        print("  wrote %s" % target)
    print("GRASS_PACK_OK %d maps" % len(maps))
    return 0


def cmd_verify(maps: list, pack_dir: Path) -> int:
    data_dir = arena_data_dir()
    headers = read_art_headers(data_dir)
    ranges = read_art_ranges(data_dir)
    facts = facts_from_pins(headers)
    for stem in maps:
        row = MAPS[stem]
        path = pack_path(Path(pack_dir), row["key"])
        try:
            blob = path.read_bytes()
        except OSError as e:
            raise ExtractError("cannot read %s: %s" % (path, e))
        try:
            pack = gp.parse(blob, expected_map_key=row["key"])
        except gp.GrassPackError as e:
            raise ExtractError("%s: %s" % (path.name, e))
        if bytes(pack.payload(gp.KIND_PLACEMENT)) != read_placement(data_dir, stem):
            raise ExtractError("%s: the placement differs from the installation" % path.name)
        for kind, name in ((gp.KIND_ALBEDO, "GrassTexArray"), (gp.KIND_NORMAL_ARRAY, "GrassNormalArray"), (gp.KIND_NORMAL_TEXTURE, "Grass_new_1_N")):
            if bytes(pack.payload(kind)) != ranges[name]:
                raise ExtractError("%s: %s differs from the installation" % (path.name, name))
            e = pack.entry(kind)
            h = headers[name]
            if (e.width, e.height, e.slices, e.mips, e.filter_mode, e.wrap_mode, e.aniso) != (h["width"], h["height"], h["slices"], h["mips"], h["filter"], h["wrap"], h["aniso"]):
                raise ExtractError("%s: the %s header differs from what Arena serialized" % (path.name, name))
        if blob != gp.build(row["key"], bytes(pack.payload(gp.KIND_PLACEMENT)), ranges["GrassTexArray"], ranges["GrassNormalArray"], ranges["Grass_new_1_N"], facts):
            raise ExtractError("%s is not what the writer produces today" % path.name)
        print("verify %s: ok (%d records, %d B)" % (row["key"], pack.record_count, len(blob)))
    print("GRASS_PACK_VERIFY_OK %d maps" % len(maps))
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Build or verify the per-map grass packs (read-only on Arena, writes only under work/).")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("pack", help="write work/build/grass/<map_key>_grass.bundle")
    p.add_argument("--map", required=True, help="bowl, iceberg, yard or all")
    p.add_argument("--out-dir", default=None, help="output folder, under work/ only (default work/build/grass)")
    v = sub.add_parser("verify", help="re-read written packs and compare them with the installation")
    v.add_argument("--map", required=True)
    v.add_argument("--pack-dir", default=None)
    args = ap.parse_args(argv)
    try:
        maps = resolve_maps(args.map)
        if args.cmd == "pack":
            return cmd_pack(maps, Path(args.out_dir) if args.out_dir else default_pack_dir())
        return cmd_verify(maps, Path(args.pack_dir) if args.pack_dir else default_pack_dir())
    except ExtractError as e:
        print("FAIL " + str(e), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
