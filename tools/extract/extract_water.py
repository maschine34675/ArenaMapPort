"""Export Bay5's authored harbor water settings and native texture mip chains.

Arena's EFT.Water.WaterContainer is newer than SPT's runtime type. Its settings
payload is nevertheless byte-compatible with SPT's WaterSettings (344 bytes in
the current source). A container then stores Renderer/Filter pointer pairs.
SPT's WaterSSR.WaterForSSRv3 can receive the settings and existing scene mesh at
runtime; no scene bundle rebuild is needed. No Arena material is missing: the
source water renderer constructs its rendering material itself.

Raw Texture2D data is intentional: DXT5 normal-map channel packing, all mips,
color space and sampling settings survive without PNG decoding/recompression.
Output is BSG-derived data and belongs only in ignored work/ or dist/ paths.

python tools/extract/extract_water.py --output work/extract/bay5-water
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import struct

from common_ids import HardExtractError
from extract_light_culling import _mb_header
import census

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


def export(source, output):
    import extract_bay5 as bay
    output = Path(output)
    require(not output.exists(), "output must be a new directory")
    environment, paths = bay.load_environment_bay5(str(source), [84])
    level = environment.files[paths[84]["level"]]
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
    selected = [entry for entry in containers if entry[2] == "Sea"]
    require(len(selected) == 1, "expected exactly one Sea container")
    reader, decoded, owner_path, active = selected[0]
    require(active and decoded["enabled"], "Sea container is disabled")
    targets = [t for t in decoded["targets"] if t["renderer"]["path_id"]]
    require(len(targets) == 1, "expected one Sea surface")
    target = targets[0]
    renderer = dereference(reader, target["renderer"], "MeshRenderer")
    mesh_filter = dereference(reader, target["filter"], "MeshFilter")
    require(renderer.assets_file is level and mesh_filter.assets_file is level, "non-local Sea target")
    renderer_data, filter_data = renderer.read(), mesh_filter.read()
    require(renderer_data.m_GameObject.path_id == filter_data.m_GameObject.path_id, "different target owners")
    owner = renderer_data.m_GameObject.read()
    target_path, target_active = hierarchy(owner)
    require(target_path == "Sea/Plane" and target_active and renderer_data.m_Enabled, "unexpected/disabled Sea target")
    require(owner.m_Layer == 4 and not renderer_data.m_Materials, "unexpected Sea renderer layer/materials")
    mesh = filter_data.m_Mesh.deref()
    require(mesh is not None and mesh.type.name == "Mesh", "missing target mesh")
    other_targets = [t for item in containers if item[0] is not reader
                     for t in item[1]["targets"] if t["renderer"]["path_id"]]
    require(not other_targets, "additional water surfaces need investigation")

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
    result = dict(schema_version=SCHEMA_VERSION, map_key="arena_bay5", target_shader=TARGET_SHADER,
                  source=dict(arena_data_root=str(source), file="level84", component_type=SOURCE_TYPE,
                              component_id=reader.path_id, owner_path=owner_path,
                              raw_sha256=hashlib.sha256(reader.get_raw_data()).hexdigest(),
                              container_count=len(containers), targetless_container_count=len(containers)-1),
                  target=dict(scene="Arena_Bay5_Main", hierarchy_path=target_path,
                              game_object_id=owner.object_reader.path_id,
                              mesh_renderer_id=renderer.path_id, mesh_filter_id=mesh_filter.path_id,
                              mesh_source_file=mesh.assets_file.name, mesh_source_id=mesh.path_id),
                  settings=decoded["settings"], textures=texture_data)
    output.mkdir(parents=True)
    (output / "textures").mkdir()
    for relative, raw in texture_files.items():
        (output / relative).write_bytes(raw)
    (output / "water.json").write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"WATER VERIFIED: arena_bay5: {len(containers)} source containers, 1 surface, "
          f"{len(texture_files)} textures, {sum(map(len, texture_files.values()))} native bytes")
    return result


def main():
    import extract_bay5 as bay
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=Path(bay.DEFAULT_SOURCE))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    export(args.source, args.output)


if __name__ == "__main__":
    main()
