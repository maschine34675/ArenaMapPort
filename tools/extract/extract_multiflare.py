"""Recover authored MultiFlare lights, atlas and raw BC7 textures from Arena.

FlareLight's full layout is independently checked against an original source
weapon bundle type tree. Arena adds _cullDistance before the unchanged Flare[];
its scene settings also contain a trailing scalar absent from EFT's type. That
scalar is preserved explicitly and only the verified value 1 is accepted.
Generated data is proprietary source content: output belongs in ignored work/.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

import UnityPy

from extract_bay5_audio import Reader, deref, hierarchy, identity, script, script_hash

SCHEMA_VERSION = 1
SOURCE = Path("D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data")
LIGHT_SCHEMA_BUNDLE = "StreamingAssets/Windows/assets/content/items/mods/tactical/tactical_all_surefire_x400_vis_laser.bundle"
SCENES = {
    "arena_equator": {51: "Arena_Equator", 53: "Arena_Equator_DesignStuff",
                      54: "Arena_Equator_Scripts", 55: "Arena_Equator_Lighting",
                      57: "Arena_Equator_TeamFight_DesignStuff", 58: "Arena_Equator_TeamFight"},
    "arena_bay5": {79: "Arena_Bay5_Background", 80: "Arena_Bay5_DEPOT",
                   81: "Arena_Bay5_DesignStuff", 82: "Arena_Bay5_HANGAR",
                   83: "Arena_Bay5_Light", 84: "Arena_Bay5_Main", 85: "Arena_Bay5_Scripts",
                   86: "Arena_Bay5_WAREHOUSE", 88: "Arena_Bay5_TeamFight_DesignStuff",
                   89: "Arena_Bay5_TeamFight"},
}


def require(value, message):
    if not value:
        raise ValueError("MultiFlare: " + message)


def finite(values):
    if isinstance(values, dict):
        return all(finite(x) for x in values.values())
    if isinstance(values, (list, tuple)):
        return all(finite(x) for x in values)
    return not isinstance(values, float) or math.isfinite(values)


class RawObject:
    path_id = "synthetic"

    def __init__(self, raw):
        self.raw = raw

    def get_raw_data(self):
        return self.raw


def _reader(raw):
    return Reader(raw if hasattr(raw, "get_raw_data") else RawObject(raw))


def decode_light(raw):
    r = _reader(raw)
    h = r.header()
    d = dict(enabled=h["enabled"], total_scale=r.take("f"), total_alpha=r.take("f"),
             cull_distance=r.take("f"), flares=[])
    for _ in range(r.count()):
        d["flares"].append(dict(scale=r.take("2f"), alpha=r.take("f"), color=r.take("4f"),
                                flare_type=r.take("i"), min_dist=r.take("f"), max_dist=r.take("f"),
                                min_scale=r.take("f"), max_scale=r.take("f"), min_alpha=r.take("f"),
                                max_alpha=r.take("f"), tex_id=r.take("i")))
    r.done()
    validate_light(d)
    return d


def validate_light(d):
    require(finite(d), "nonfinite light data")
    require(d["total_scale"] >= 0 and 0 <= d["total_alpha"] <= 1 and d["cull_distance"] > 0,
            "invalid light scale/alpha/culling")
    require(0 < len(d["flares"]) <= 128, "invalid flare count")
    for f in d["flares"]:
        require(len(f["scale"]) == 2 and min(f["scale"]) >= 0 and len(f["color"]) == 4,
                "invalid flare vectors")
        require(0 <= f["alpha"] <= 1 and all(0 <= x <= 1 for x in f["color"]), "invalid flare color/alpha")
        require(f["flare_type"] in (0, 2), "unsupported scene flare type")
        require(0 <= f["min_dist"] < f["max_dist"], "invalid distance range")
        require(min(f[k] for k in ("min_scale", "max_scale", "min_alpha", "max_alpha")) >= 0,
                "negative distance multiplier")
        require(f["min_scale"] > 0 and f["min_alpha"] > 0, "zero native divisor")
        require(f["tex_id"] >= 0, "negative atlas index")


def _batch(r):
    return dict(capacity=r.take("i"), material_pointer=r.ptr(),
                blind_protection_alpha_factor=r.take("f"), blind_protection_size_factor=r.take("f"))


def decode_settings(raw):
    r = _reader(raw)
    h = r.header()
    d = dict(enabled=h["enabled"], atlas_pointer=r.ptr(), normal_batch=_batch(r),
             noise_batch=_batch(r), overlap_batch=_batch(r))
    d["overlap_batch"].update(max_neighbor_count=r.take("i"), search_range=r.take("f"),
                              max_scale_multiplier=r.take("f"))
    # Source field identity is unknown; neither silently drop nor invent its meaning.
    d["source_extra_scalar"] = r.take("f")
    r.done()
    require(finite(d) and d["source_extra_scalar"] == 1.0, "unknown source settings extension")
    require(d["atlas_pointer"]["pathId"] > 0, "missing atlas")
    for key in ("normal_batch", "noise_batch", "overlap_batch"):
        b = d[key]
        require(0 < b["capacity"] <= 65536 and b["material_pointer"]["pathId"] > 0,
                "invalid batch/material")
        require(all(0 <= b[k] <= 1 for k in ("blind_protection_alpha_factor", "blind_protection_size_factor")),
                "invalid blindness factor")
    b = d["overlap_batch"]
    require(0 < b["max_neighbor_count"] <= b["capacity"] and b["search_range"] > 0
            and b["max_scale_multiplier"] >= 0, "invalid overlap settings")
    return d


def decode_atlas(raw):
    r = _reader(raw)
    h = r.header()
    d = dict(name=h["name"], containers=[dict(name=r.string(), rect=r.take("4f")) for _ in range(r.count())])
    r.done()
    require(finite(d) and 0 < len(d["containers"]) <= 128, "invalid atlas")
    for c in d["containers"]:
        x, y, w, h = c["rect"]
        require(0 <= x < 1 and 0 <= y < 1 and w > 0 and h > 0 and x+w <= 1.00001 and y+h <= 1.00001,
                "invalid atlas UV rectangle")
    return d


def texture_byte_count(width, height, mip_count, fmt):
    require(width > 0 and height > 0 and 1 <= mip_count <= max(width, height).bit_length(),
            "invalid texture dimensions/mips")
    require(fmt == 25, "expected native BC7 texture")
    return sum(((max(1, width >> m)+3)//4) * ((max(1, height >> m)+3)//4) * 16 for m in range(mip_count))


def _ptr(pointer):
    return dict(fileId=pointer["m_FileID"], pathId=pointer["m_PathID"])


def _vector(value, keys):
    return [value[k] for k in keys]


def _object_info(obj, scene):
    h = hierarchy(obj, True)
    chain = [dict(position=_vector(n["position"], "xyz"), rotation=_vector(n["rotation"], "xyzw"),
                  scale=_vector(n["scale"], "xyz"), active_self=n["activeSelf"], layer=n["layer"])
             for n in h["transformChain"]]
    return dict(id=identity(obj), scene=scene, hierarchy_path=h["path"], active_in_hierarchy=h["activeInHierarchy"],
                transform_chain=chain, source_raw_sha256=hashlib.sha256(obj.get_raw_data()).hexdigest(),
                source_byte_count=obj.byte_size)


def _schema_check(obj, schema):
    require(script_hash(obj) == script_hash(schema), "FlareLight schema hash differs from source bundle")
    t = obj.read_typetree(nodes=schema.serialized_type.node, check_read=True)
    decoded = decode_light(obj)
    require(decoded["total_scale"] == t["_totalScale"] and decoded["total_alpha"] == t["_totalAlpha"]
            and decoded["cull_distance"] == t["_cullDistance"] and decoded["enabled"] == bool(t["m_Enabled"]),
            "light bundle schema mismatch")
    fields = {"alpha": "_alpha", "flare_type": "_flareType", "min_dist": "_minDist", "max_dist": "_maxDist",
              "min_scale": "_minScale", "max_scale": "_maxScale", "min_alpha": "_minAlpha", "max_alpha": "_maxAlpha", "tex_id": "_texId"}
    require(len(decoded["flares"]) == len(t["_flares"]), "flare count mismatch")
    for actual, original in zip(decoded["flares"], t["_flares"]):
        require(actual["scale"] == _vector(original["_scale"], "xy")
                and actual["color"] == _vector(original["_color"], "rgba")
                and all(actual[k] == original[v] for k, v in fields.items()), "flare bundle schema mismatch")
    return decoded


def validate_graph(d):
    require(d["schema_version"] == SCHEMA_VERSION and d["map_key"] in SCENES and finite(d), "invalid package identity/data")
    lights, mats, texs = d["lights"], d["materials"], d["textures"]
    for entries in (lights, mats, texs):
        require(entries and len({x["id"] for x in entries}) == len(entries), "empty or duplicate identities")
    mat_ids, texture_ids = {x["id"] for x in mats}, {x["id"] for x in texs}
    require(d["settings"]["source_extra_scalar"] == 1, "unsupported settings extension")
    require(d["settings"]["enabled"] and d["settings"]["active_in_hierarchy"], "inactive source settings")
    for key in ("normal_batch", "noise_batch", "overlap_batch"):
        require(d["settings"][key]["material_id"] in mat_ids, "dangling batch material")
    for m in mats:
        require(m["shader"] in ("Custom/multiFlare", "Custom/ShitOnScreen"), "unsupported shader")
        require(all(t["texture_id"] in texture_ids for t in m["textures"]), "dangling material texture")
    for t in texs:
        require(t["byte_count"] == texture_byte_count(t["width"], t["height"], t["mip_count"], t["format"]),
                "texture byte count mismatch")
        path = Path(t["file"])
        require(not path.is_absolute() and ".." not in path.parts and path.parts[0] == "textures",
                "unsafe texture output path")
    for l in lights:
        validate_light(l)
        require(l["scene"] in SCENES[d["map_key"]].values(), "light outside owned scene set")
        require(l["transform_chain"] and l["active_in_hierarchy"] == all(n["active_self"] for n in l["transform_chain"]),
                "incorrect inherited activity")
        for n in l["transform_chain"]:
            require(len(n["position"]) == 3 and len(n["rotation"]) == 4 and len(n["scale"]) == 3,
                    "invalid transform")
            require(abs(sum(v*v for v in n["rotation"])-1) < .001 and all(v != 0 for v in n["scale"])
                    and 0 <= n["layer"] <= 31, "invalid transform rotation/scale/layer")
        require(all(f["tex_id"] < len(d["atlas"]["containers"]) for f in l["flares"]), "atlas index out of range")


def export(source, output, map_key):
    source, output = Path(source).resolve(), Path(output).resolve()
    require(map_key in SCENES, "unsupported map")
    require(source != output and source not in output.parents, "output must not be in source installation")
    sidecar = Path(__file__).resolve().parents[1] / "maps" / (map_key.removeprefix("arena_")+".json")
    require(set(json.loads(sidecar.read_text())["all_scene_names"]) == set(SCENES[map_key].values()), "map scene sidecar drift")
    schema_path = source / LIGHT_SCHEMA_BUNDLE
    schema_env = UnityPy.load(str(schema_path))
    schemas = [o for o in schema_env.objects if o.type.name == "MonoBehaviour"
               and o.serialized_type.node and "_flares" in o.read_typetree()]
    require(schemas and all(script_hash(x) == script_hash(schemas[0]) for x in schemas),
            "missing/ambiguous source FlareLight schema")
    schema = schemas[0]
    source_files = {"globalgamemanagers.assets"}
    lights, scene_settings = [], []
    for index, name in SCENES[map_key].items():
        relative = "level"+str(index)
        source_files.add(relative)
        env = UnityPy.load(str(source/relative), str(source/"globalgamemanagers.assets"))
        level = env.files[str(source/relative)]
        for obj in list(level.objects.values()):
            if obj.type.name != "MonoBehaviour":
                continue
            kind = script(obj)
            if kind == "FlareLight":
                d = _schema_check(obj, schema)
                d.update(_object_info(obj, name))
                lights.append(d)
            elif kind == "FlareSceneSettings":
                scene_settings.append((obj, name))
    require(len(scene_settings) == 1, "ambiguous/missing scene settings")
    obj, scene_name = scene_settings[0]
    settings = decode_settings(obj)
    settings.update(_object_info(obj, scene_name))
    settings["source_id"] = settings.pop("id")
    atlas_obj = deref(obj, settings.pop("atlas_pointer"), "MonoBehaviour")
    require(script(atlas_obj) == "ProFlareAtlas", "wrong atlas script")
    atlas = decode_atlas(atlas_obj)
    atlas.update(source_id=identity(atlas_obj), source_raw_sha256=hashlib.sha256(atlas_obj.get_raw_data()).hexdigest())
    source_files.add(Path(atlas_obj.assets_file.name).name)
    materials, textures, raw_files = {}, {}, {}
    for key in ("normal_batch", "noise_batch", "overlap_batch"):
        material = deref(obj, settings[key].pop("material_pointer"), "Material")
        mid = identity(material)
        settings[key]["material_id"] = mid
        if mid in materials:
            continue
        mt = material.read_typetree()
        shader = deref(material, _ptr(mt["m_Shader"]), "Shader")
        props = mt["m_SavedProperties"]
        md = dict(id=mid, name=mt["m_Name"], shader=shader.read_typetree()["m_ParsedForm"]["m_Name"],
                  render_queue=mt["m_CustomRenderQueue"], keywords=mt["m_ValidKeywords"],
                  floats=dict(props["m_Floats"]), colors={k: _vector(v, "rgba") for k, v in props["m_Colors"]}, textures=[],
                  source_raw_sha256=hashlib.sha256(material.get_raw_data()).hexdigest())
        require(not props["m_Ints"], "unexpected integer material parameters")
        source_files.update((Path(material.assets_file.name).name, Path(shader.assets_file.name).name))
        for prop, value in props["m_TexEnvs"]:
            texture = deref(material, _ptr(value["m_Texture"]), "Texture2D")
            require(texture is not None, "missing material texture")
            tid = identity(texture)
            md["textures"].append(dict(property=prop, texture_id=tid, scale=_vector(value["m_Scale"], "xy"), offset=_vector(value["m_Offset"], "xy")))
            if tid in textures:
                continue
            tt, raw = texture.read_typetree(), texture.read().get_image_data()
            require(tt["m_ImageCount"] == 1 and tt["m_TextureDimension"] == 2 and tt["m_ColorSpace"] in (0, 1), "unsupported texture shape/color space")
            ts = tt["m_TextureSettings"]
            relative = "textures/"+tid.replace(":", "_")+".bin"
            textures[tid] = dict(id=tid, name=tt["m_Name"], file=relative, width=tt["m_Width"], height=tt["m_Height"],
                                 format=tt["m_TextureFormat"], mip_count=tt["m_MipCount"], linear=tt["m_ColorSpace"] == 0,
                                 filter_mode=ts["m_FilterMode"], aniso_level=ts["m_Aniso"], mip_map_bias=ts["m_MipBias"],
                                 wrap_u=ts["m_WrapU"], wrap_v=ts["m_WrapV"], wrap_w=ts["m_WrapW"],
                                 byte_count=len(raw), sha256=hashlib.sha256(raw).hexdigest())
            raw_files[relative] = raw
            source_files.add(Path(texture.assets_file.name).name)
        materials[mid] = md
    d = dict(schema_version=SCHEMA_VERSION, map_key=map_key, settings=settings, atlas=atlas,
             materials=list(materials.values()), textures=list(textures.values()), lights=lights,
             source=dict(files=[dict(file=f, sha256=hashlib.sha256((source/f).read_bytes()).hexdigest()) for f in sorted(source_files)],
                         light_schema=dict(bundle=LIGHT_SCHEMA_BUNDLE, sha256=hashlib.sha256(schema_path.read_bytes()).hexdigest(),
                                           script_hash=script_hash(schema))))
    validate_graph(d)
    expected = (28, 25, 48, 33) if map_key == "arena_equator" else (25, 24, 116, 111)
    active = [l for l in lights if l["enabled"] and l["active_in_hierarchy"]]
    require((len(lights), len(active), sum(len(l["flares"]) for l in lights), sum(len(l["flares"]) for l in active)) == expected,
            "source light/flare census drift")
    output.mkdir(parents=True, exist_ok=True)
    (output/"textures").mkdir(exist_ok=True)
    for name, raw in raw_files.items():
        (output/name).write_bytes(raw)
    payload = (json.dumps(d, indent=2, allow_nan=False)+"\n").encode()
    tmp = output/"flares.json.tmp"
    tmp.write_bytes(payload)
    tmp.replace(output/"flares.json")
    print(f"MULTIFLARE VERIFIED: {map_key}: {len(active)}/{len(lights)} active lights, {expected[3]}/{expected[2]} active flares, {len(textures)} original textures")
    return d


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=SOURCE)
    parser.add_argument("--map", dest="map_key", choices=SCENES, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    export(args.source, args.output, args.map_key)


if __name__ == "__main__":
    main()
