"""Export a ported map's authored sea water settings and native texture mip chains (Bay5's harbor, Iceberg's sea).

Arena's EFT.Water.WaterContainer is newer than SPT's runtime type. Its settings
payload is nevertheless byte-compatible with SPT's WaterSettings (344 bytes in
the current source). A container then stores Renderer/Filter pointer pairs.
SPT's WaterSSR.WaterForSSRv3 can receive the settings and existing scene mesh at
runtime; no scene bundle rebuild is needed. No Arena material is missing: the
source water renderer constructs its rendering material itself.

Raw Texture2D data is intentional: DXT5 normal-map channel packing, all mips,
color space and sampling settings survive without PNG decoding/recompression.
Output is BSG-derived data and belongs only in ignored work/ or dist/ paths.

Maps (the MAPS table below; every row is a reviewed fact about one Arena scene, nothing is searched for):
  arena_bay5     level84   Arena_Bay5_Main    container 'Sea' -> surface 'Sea/Plane'   work/extract/bay5-water
  arena_iceberg  level256  Arena_Sanatorium   container 'Sea' -> surface 'Sea/Plane'   work/extract/iceberg-water
Both scenes also hold a second container ('WaterForSSrv3') whose only target is empty; it renders nothing and is tolerated. A container
with a REAL target besides the selected one stops the export ("additional water surfaces need investigation"), and so does a
WaterContainer with a real target in any other level file of the map (the row's census_levels).

Stage-2 guard (2026-10-31): the shipped textures are plaintext, and the content keys of the encrypted bundles are derived from sampled blocks of
the raw resource streams of tools/bundle/key-sources.json. A texture that lived on such a block, or was a whole key-source file, would
publish key material, so the export refuses it (check_not_key_source). The normals and the foam are fragments of
sharedassets51.assets.resS, a key-source file of Equator, and clear the sampled blocks with room to spare.

python tools/extract/extract_water.py --output work/extract/bay5-water
python tools/extract/extract_water.py --map arena_iceberg --output work/extract/iceberg-water
(--output defaults to work/extract/<stem>-water; the directory must not exist.)
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from dataclasses import dataclass
from pathlib import Path
import struct
from typing import NamedTuple

from common_ids import HardExtractError
from extract_light_culling import _mb_header
import census

REPO = Path(__file__).resolve().parents[2]
KEY_SOURCES = REPO / "tools" / "bundle" / "key-sources.json"
DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"

SCHEMA_VERSION = 1
SOURCE_TYPE = "EFT.Water.WaterContainer"
TARGET_SHADER = "Hidden/WaterForSSR"
TEXTURE_FIELDS = ("_rippleTexture", "_normals", "_normalsDetails", "_foam")
BLEND_FIELDS = ("_normalsA", "_normalsB", "_normalsDetails0", "_foamA", "_foamB")
DEPTH_FIELDS = ("_borderFade", "_borderFadeDistStart", "_borderFadeDistRange",
                "_depthFade", "_depthColorFade", "_depthRefractions")
OTHER_FIELDS = ("_bumpiness", "_rippleScale", "_rippleBumpness", "_foamSize", "_foamIntensity")
REFLECTION_FIELDS = ("_fresnelIntensity", "_fresnelPower",
                     "_additionalCubemapReflectionMinWetting", "_additionalCubemapReflectionMaxWetting")


@dataclass(frozen=True)
class WaterMap:
    """One map's water source: where the container sits and which surface it drives."""
    map_key: str
    stem: str                  # tools/maps/<stem>.json; the default output is work/extract/<stem>-water
    level_index: int           # BuildSettings index of the scene whose level file holds the container AND its surface
    scene: str                 # that scene's name; it is written to water.json (target.scene) and must be a scene of the map
    container_path: str        # hierarchy path of the owner of the selected WaterContainer
    target_path: str           # hierarchy path of the one surface it drives (layer 4, no materials)
    census_levels: tuple = ()  # the map's other level files: none may hold a WaterContainer with a real target

    @property
    def default_output(self) -> Path:
        return REPO / "work" / "extract" / f"{self.stem}-water"


# Bay5: the harbor sea (docs/progress/bay5-water.md, 2026-09-28); the census levels are its other eight scenes
# (extract_bay5.BAY5_SCENE_NAMES, Scripts included). Iceberg: Arena_Sanatorium's sea (docs/progress/iceberg-release.md, 2026-10-31);
# the census levels are the other scenes of tools/extract/plans/iceberg.json (imported, anchor, NavMesh and excluded ones).
MAPS = {wm.map_key: wm for wm in (
    WaterMap("arena_bay5", "bay5", 84, "Arena_Bay5_Main", "Sea", "Sea/Plane",
             census_levels=(79, 80, 81, 82, 83, 85, 86, 88, 89)),
    WaterMap("arena_iceberg", "iceberg", 256, "Arena_Sanatorium", "Sea", "Sea/Plane",
             census_levels=(255, 257, 258, 259, 260, 375, 376, 377, 378)),
)}


def resolve_map(name: str) -> WaterMap:
    """A row by map key ('arena_iceberg') or by stem ('iceberg')."""
    for wm in MAPS.values():
        if name in (wm.map_key, wm.stem):
            return wm
    raise HardExtractError(f"WaterContainer: no water source row for {name!r}; known: {', '.join(sorted(MAPS))}")


def require(condition, message):
    if not condition:
        raise HardExtractError("WaterContainer: " + message)


def decode_container(raw):
    """Decode the verified native field order, rejecting incomplete/unknown data."""
    enabled, offset = _mb_header(raw)

    def take(fmt):
        nonlocal offset
        size = struct.calcsize("<" + fmt)
        require(offset + size <= len(raw), "truncated payload")
        values = struct.unpack_from("<" + fmt, raw, offset)
        offset += size
        require(all(not isinstance(v, float) or math.isfinite(v) for v in values), "non-finite field")
        return values[0] if len(values) == 1 else list(values)

    def pointer():
        file_id, path_id = take("iq")
        require(file_id >= 0 and path_id >= 0, "invalid pointer")
        return {"file_id": file_id, "path_id": path_id}

    settings, textures = {}, {}
    for name in ("_rippleTexture", "_normals", "_normalsDetails"):
        textures[name] = pointer()
        require(textures[name]["path_id"] != 0, "missing texture")
    settings["_normalsDetailsMipMapBias"] = take("f")
    textures["_foam"] = pointer()
    require(textures["_foam"]["path_id"] != 0, "missing foam texture")
    for name in BLEND_FIELDS:
        value = take("4f")
        require(value[0] > 0 and 0 <= value[3] <= 1, "invalid texture blend")
        settings[name] = {"Scale": value[0], "MovementDirection": value[1:3], "Blend": value[3]}
    for group in (BLEND_FIELDS[:3], BLEND_FIELDS[3:]):
        require(abs(sum(settings[name]["Blend"] for name in group) - 1) < 1e-5,
                "texture blends are not normalized")
    for name in DEPTH_FIELDS:
        settings[name] = take("f")
    require(settings["_borderFadeDistRange"] > 0, "zero/negative border fade range")
    for name in ("_depthColorDeep", "_depthColorShallow"):
        settings[name] = take("4f")
    for name in OTHER_FIELDS:
        settings[name] = take("f")
    require(settings["_foamSize"] > 0, "zero/negative foam size")
    settings["_foamColor"] = take("4f")
    for name in REFLECTION_FIELDS:
        settings[name] = take("f")
    count = take("i")
    require(1 <= count <= 128, "invalid reflection curve key count")
    keys = []
    for _ in range(count):
        value = take("4fi2f")
        require(0 <= value[4] <= 3, "invalid curve weighted mode")
        keys.append(dict(zip(("time", "value", "in_tangent", "out_tangent", "weighted_mode",
                              "in_weight", "out_weight"), value)))
    require(all(a["time"] < b["time"] for a, b in zip(keys, keys[1:])), "unordered curve keys")
    pre, post, rotation = take("3i")
    # These are Unity serialized infinity codes, not UnityEngine.WrapMode enum values.
    require(pre in (1, 2) and post in (1, 2) and rotation == 4, "unsupported curve metadata")
    settings["_reflectionWettingFunc"] = {"keys": keys, "pre_infinity": pre,
                                          "post_infinity": post, "rotation_order": rotation}
    for name in ("_reflectionColor", "_diffuseColor"):
        settings[name] = take("4f")
    count = take("i")
    require(0 <= count <= 128, "invalid target count")
    targets = [{"renderer": pointer(), "filter": pointer()} for _ in range(count)]
    require(offset == len(raw), "unexpected trailing payload")
    for target in targets:
        require(bool(target["renderer"]["path_id"]) == bool(target["filter"]["path_id"]),
                "incomplete renderer/filter target")
    return {"enabled": enabled, "settings": settings, "texture_pointers": textures, "targets": targets}


def texture_byte_count(width, height, mip_count, fmt):
    require(width > 0 and height > 0 and 1 <= mip_count <= max(width, height).bit_length(),
            "invalid texture dimensions/mips")
    require(fmt in (5, 10, 12), "unsupported native texture format")
    total = 0
    for mip in range(mip_count):
        w, h = max(1, width >> mip), max(1, height >> mip)
        total += w * h * 4 if fmt == 5 else ((w + 3) // 4) * ((h + 3) // 4) * (8 if fmt == 10 else 16)
    return total


# ---- stage-2 key sources ---------------------------------------------------------------------------------------------------------------

class KeySources(NamedTuple):
    block_size: int
    blocks: int
    files: dict          # base file name -> the map keys whose content key is derived from it


def load_key_sources(path: Path = KEY_SOURCES) -> KeySources:
    """tools/bundle/key-sources.json: the sample parameters and every file any map's key reads, by base name."""
    table = json.loads(Path(path).read_text(encoding="utf-8"))
    require(table.get("schema") == 2, "key-sources.json is not schema 2")
    sample = table["sample"]
    files: dict = {}
    for map_key, entries in table["maps"].items():
        for entry in entries:
            for name in entry["files"]:
                files.setdefault(Path(name).name, []).append(map_key)
    return KeySources(sample["block_size"], sample["blocks"], {name: tuple(keys) for name, keys in files.items()})


def sample_blocks(size: int, block_size: int, blocks: int) -> list:
    """(offset, length) of every block the key derivation reads from a file of `size` bytes: the whole file when it is small, otherwise
    block k at k * (size - block_size) // (blocks - 1) (ArenaMapPort.Crypto KeySample.BlockOffset)."""
    if size <= block_size * blocks:
        return [(0, size)]
    return [(k * (size - block_size) // (blocks - 1), block_size) for k in range(blocks)]


def sample_overlaps(size: int, offset: int, length: int, block_size: int, blocks: int) -> list:
    """(block offset, overlapping bytes) of every sampled block of a `size`-byte file that the range [offset, offset + length) touches."""
    hits = []
    for block_offset, block_length in sample_blocks(size, block_size, blocks):
        overlap = min(offset + length, block_offset + block_length) - max(offset, block_offset)
        if overlap > 0:
            hits.append((block_offset, overlap))
    return hits


def check_not_key_source(label: str, stream: dict, raw: bytes, data_dir: Path, sources: KeySources) -> None:
    """Refuse a texture whose shipped bytes would carry key material (see the module docstring).

    `stream` is the texture's m_StreamData ({'path', 'offset', 'size'}; an empty path = the bytes sit inline in the .assets file, which no key
    reads). Two rules: (1) when the bytes come from a key-source file, their range must touch none of that file's sampled blocks; (2) the
    shipped bytes must not equal a whole key-source file (the gate-sound trap: a clip that IS sharedassets28.resource). Rule 2 only hashes a
    file whose size equals the texture's."""
    name = Path(stream["path"]).name if stream.get("path") else ""
    if name in sources.files:
        size = (Path(data_dir) / name).stat().st_size
        hits = sample_overlaps(size, stream["offset"], stream["size"], sources.block_size, sources.blocks)
        require(not hits, f"{label} lies on {len(hits)} sampled block(s) of key-source file {name} "
                          f"(maps {', '.join(sources.files[name])}); its plaintext would publish key material")
    digest = hashlib.sha256(raw).hexdigest()
    for source_name, maps in sources.files.items():
        candidate = Path(data_dir) / source_name
        if candidate.is_file() and candidate.stat().st_size == len(raw):
            require(hashlib.sha256(candidate.read_bytes()).hexdigest() != digest,
                    f"{label} is a byte copy of key-source file {source_name} (maps {', '.join(maps)})")


# ---- reading Arena ---------------------------------------------------------------------------------------------------------------------

def dereference(reader, pointer, expected_type):
    from UnityPy.classes import PPtr
    try:
        result = PPtr(m_FileID=pointer["file_id"], m_PathID=pointer["path_id"],
                      assetsfile=reader.assets_file).deref()
        require(result is not None and result.type.name == expected_type, "wrong reference type: " + expected_type)
        return result
    except HardExtractError:
        raise
    except Exception as exc:
        raise HardExtractError(f"WaterContainer: unresolved {expected_type} pointer {pointer}: {exc}") from exc


def hierarchy(game_object):
    names, seen, active = [], set(), True
    while game_object is not None:
        require(game_object.object_reader.path_id not in seen, "cyclic hierarchy")
        seen.add(game_object.object_reader.path_id)
        names.append(game_object.m_Name)
        active = active and bool(game_object.m_IsActive)
        transforms = [c.component.read() for c in game_object.m_Component
                      if c.component.deref().type.name == "Transform"]
        require(len(transforms) == 1, "owner needs one Transform")
        parent = transforms[0].m_Father
        game_object = parent.read().m_GameObject.read() if parent.path_id else None
    return "/".join(reversed(names)), active


def find_containers(level):
    """(reader, decoded settings, owner hierarchy path, owner active) of every WaterContainer in one level file."""
    containers = []
    for reader in level.objects.values():
        if reader.type.name != "MonoBehaviour":
            continue
        name, status = census.resolve_script_name(reader)
        if status == "ok" and name == SOURCE_TYPE:
            decoded = decode_container(reader.get_raw_data())
            owner = reader.parse_monobehaviour_head().m_GameObject.read()
            path, active = hierarchy(owner)
            containers.append((reader, decoded, path, active))
    return containers


def real_targets(decoded):
    """The targets of a decoded container that point at a renderer (a null/null pair is an unused slot)."""
    return [t for t in decoded["targets"] if t["renderer"]["path_id"]]


def select_sea(containers, wm: WaterMap):
    """The one container whose owner is the row's container_path, enabled and active, with exactly one real target. Every other container
    of the level may exist but must not drive a surface: one whose targets are all empty (Bay5's and Iceberg's 'WaterForSSrv3') renders
    nothing and is accepted, a second real surface needs investigation."""
    selected = [entry for entry in containers if entry[2] == wm.container_path]
    require(len(selected) == 1, f"expected exactly one {wm.container_path} container")
    reader, decoded, _path, active = selected[0]
    require(active and decoded["enabled"], f"{wm.container_path} container is disabled")
    require(len(real_targets(decoded)) == 1, f"expected one {wm.container_path} surface")
    other_targets = [t for item in containers if item[0] is not reader for t in real_targets(item[1])]
    require(not other_targets, "additional water surfaces need investigation")
    return selected[0]


def census_other_levels(source, wm: WaterMap) -> int:
    """Scan the row's other level files for WaterContainers: any with a real target stops the export; a MonoBehaviour whose script cannot be
    named (not a missing-script slot) makes the scan untrustworthy and stops it too. Returns the number of containers found (empty ones)."""
    import UnityPy
    found = 0
    for index in wm.census_levels:
        level = next(iter(UnityPy.load(str(Path(source) / f"level{index}")).files.values()))
        for reader in level.objects.values():
            if reader.type.name != "MonoBehaviour":
                continue
            name, status = census.resolve_script_name(reader)
            require(status in ("ok", "null_script"), f"level{index}: script of MonoBehaviour {reader.path_id} unreadable ({status})")
            if status == "ok" and name == SOURCE_TYPE:
                found += 1
                require(not real_targets(decode_container(reader.get_raw_data())),
                        f"level{index}: another WaterContainer ({reader.path_id}) drives a surface; additional water surfaces need investigation")
    return found


def verify_scene(source, wm: WaterMap) -> None:
    """BuildSettings must still put the row's scene at its level index (Arena renumbers on updates)."""
    from source_plan import PlanError, read_build_scenes
    try:
        scenes = read_build_scenes(Path(source))
    except PlanError as exc:
        raise HardExtractError(f"WaterContainer: {exc}") from exc
    require(wm.level_index < len(scenes) and Path(scenes[wm.level_index]).stem == wm.scene,
            f"level{wm.level_index} is no longer scene {wm.scene} in the source BuildSettings")


def export(source, output, map_key="arena_bay5"):
    import extract_bay5 as bay
    wm = resolve_map(map_key)
    output = Path(output)
    require(not output.exists(), "output must be a new directory")
    verify_scene(source, wm)
    environment, paths = bay.load_environment_bay5(str(source), [wm.level_index])
    level = environment.files[paths[wm.level_index]["level"]]
    containers = find_containers(level)
    reader, decoded, owner_path, active = select_sea(containers, wm)
    target = real_targets(decoded)[0]
    renderer = dereference(reader, target["renderer"], "MeshRenderer")
    mesh_filter = dereference(reader, target["filter"], "MeshFilter")
    require(renderer.assets_file is level and mesh_filter.assets_file is level, "non-local Sea target")
    renderer_data, filter_data = renderer.read(), mesh_filter.read()
    require(renderer_data.m_GameObject.path_id == filter_data.m_GameObject.path_id, "different target owners")
    owner = renderer_data.m_GameObject.read()
    target_path, target_active = hierarchy(owner)
    require(target_path == wm.target_path and target_active and renderer_data.m_Enabled, "unexpected/disabled Sea target")
    require(owner.m_Layer == 4 and not renderer_data.m_Materials, "unexpected Sea renderer layer/materials")
    mesh = filter_data.m_Mesh.deref()
    require(mesh is not None and mesh.type.name == "Mesh", "missing target mesh")
    elsewhere = census_other_levels(source, wm) if wm.census_levels else 0

    sources = load_key_sources()
    texture_data, texture_files = {}, {}
    for name, pointer in decoded["texture_pointers"].items():
        texture = dereference(reader, pointer, "Texture2D")
        tree, data = texture.read_typetree(), texture.read()
        width, height, fmt, mips = (tree[key] for key in ("m_Width", "m_Height", "m_TextureFormat", "m_MipCount"))
        require(tree["m_ImageCount"] == 1 and tree["m_TextureDimension"] == 2, "not a single 2D texture")
        require(tree["m_ColorSpace"] in (0, 1), "unknown source color space")
        raw = data.get_image_data()
        require(len(raw) == texture_byte_count(width, height, mips, fmt), "native texture byte-count mismatch")
        if name in ("_normals", "_normalsDetails"):
            require(fmt == 12 and tree["m_ColorSpace"] == 0, "normal-map packing changed")
        check_not_key_source(f"texture {name} ({tree['m_Name']})", tree.get("m_StreamData") or {}, raw, Path(source), sources)
        settings = tree["m_TextureSettings"]
        relative = "textures/" + name.lstrip("_") + ".bin"
        texture_files[relative] = raw
        texture_data[name] = dict(file=relative, source_file=texture.assets_file.name,
                                 source_path_id=texture.path_id, source_name=tree["m_Name"],
                                 width=width, height=height, format=fmt, mip_count=mips,
                                 linear=tree["m_ColorSpace"] == 0, filter_mode=settings["m_FilterMode"],
                                 aniso_level=settings["m_Aniso"], mip_map_bias=settings["m_MipBias"],
                                 wrap_u=settings["m_WrapU"], wrap_v=settings["m_WrapV"], wrap_w=settings["m_WrapW"],
                                 byte_count=len(raw), sha256=hashlib.sha256(raw).hexdigest())
    result = dict(schema_version=SCHEMA_VERSION, map_key=wm.map_key, target_shader=TARGET_SHADER,
                  source=dict(arena_data_root=str(source), file=f"level{wm.level_index}", component_type=SOURCE_TYPE,
                              component_id=reader.path_id, owner_path=owner_path,
                              raw_sha256=hashlib.sha256(reader.get_raw_data()).hexdigest(),
                              container_count=len(containers), targetless_container_count=len(containers)-1),
                  target=dict(scene=wm.scene, hierarchy_path=target_path,
                              game_object_id=owner.object_reader.path_id,
                              mesh_renderer_id=renderer.path_id, mesh_filter_id=mesh_filter.path_id,
                              mesh_source_file=mesh.assets_file.name, mesh_source_id=mesh.path_id),
                  settings=decoded["settings"], textures=texture_data)
    output.mkdir(parents=True)
    (output / "textures").mkdir()
    for relative, raw in texture_files.items():
        (output / relative).write_bytes(raw)
    (output / "water.json").write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    census_note = f", {len(wm.census_levels)} other levels scanned ({elsewhere} empty container(s))" if wm.census_levels else ""
    print(f"WATER VERIFIED: {wm.map_key}: {len(containers)} source containers, 1 surface, "
          f"{len(texture_files)} textures, {sum(map(len, texture_files.values()))} native bytes{census_note}")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--map", default="arena_bay5", help="map key or stem: " + ", ".join(sorted(MAPS)) + " (default arena_bay5)")
    parser.add_argument("--source", type=Path, default=Path(DEFAULT_SOURCE))
    parser.add_argument("--output", type=Path, help="new directory (default work/extract/<stem>-water)")
    args = parser.parse_args()
    wm = resolve_map(args.map)
    export(args.source, args.output or wm.default_output, wm.map_key)


if __name__ == "__main__":
    main()
