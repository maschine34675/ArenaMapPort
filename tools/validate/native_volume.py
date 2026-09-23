"""Independent package/source/bundle checks for the native dust alpha volumes.

This module deliberately does not import the volume exporter or its metadata builder.
The source Texture3D bytes are compared directly to the payload and built Unity asset.
"""
from __future__ import annotations

import hashlib
import json
import math
import re
from pathlib import Path


def package_errors(meta: dict, package: Path) -> list[str]:
    volume = meta.get("native_volume")
    if volume is None:
        return []  # Legacy inspection-only volumes are still allowed, but never assigned.
    errors = []
    try:
        if not isinstance(volume, dict):
            raise ValueError("native_volume must be an object")
        if (meta.get("dimension"), volume.get("schema_version"), volume.get("encoding"),
                volume.get("layout"), volume.get("source_graphics_format"), volume.get("color_space")) != (
                "Tex3D", 1, "Alpha8", "x-fastest-y-z-mips", 54, "Linear"):
            raise ValueError("unsupported volume shape/encoding/version/color space")
        dims = [volume.get(k) for k in ("width", "height", "depth")]
        count = volume.get("mip_count")
        if any(type(d) is not int or not 1 <= d <= 2048 for d in dims):
            raise ValueError("invalid volume dimensions")
        if type(count) is not int or not 1 <= count <= max(dims).bit_length():
            raise ValueError("invalid volume mip count")
        if meta.get("color_space") != "Linear" or meta.get("is_normal_map") is not False:
            raise ValueError("volume metadata contradicts texture flags")
        if (meta.get("width"), meta.get("height")) != (dims[0], dims[1] * dims[2]):
            raise ValueError("volume preview dimensions differ")
        expected, offset = [], 0
        for level in range(count):
            width, height, depth = [max(1, d >> level) for d in dims]
            length = width * height * depth
            expected.append(dict(width=width, height=height, depth=depth, offset=offset, byte_length=length))
            offset += length
        if volume.get("mips") != expected or volume.get("byte_length") != offset or offset > 64 * 1024 * 1024:
            raise ValueError("volume mip layout/byte count differs")
        filename = volume.get("file", "")
        if not re.fullmatch(r"textures/[0-9a-f]{16}\.volume\.bin", filename):
            raise ValueError("invalid volume payload path")
        raw = (package / filename).read_bytes()
        if len(raw) != offset or hashlib.sha256(raw).hexdigest() != volume.get("sha256"):
            raise ValueError("volume payload size/hash differs")
        sampler = volume.get("sampler", {})
        for key, valid in (("filter_mode", range(3)), ("aniso_level", range(17)),
                           ("wrap_u", range(4)), ("wrap_v", range(4)), ("wrap_w", range(4))):
            if type(sampler.get(key)) is not int or sampler[key] not in valid:
                raise ValueError("invalid sampler " + key)
        bias = sampler.get("mip_bias")
        if type(bias) not in (int, float) or not math.isfinite(bias):
            raise ValueError("invalid mip bias")
        if not re.fullmatch(r"[^/\\]+\.assets", volume.get("source_file", "")):
            raise ValueError("invalid source filename")
        if type(volume.get("source_path_id")) is not int or volume["source_path_id"] <= 0:
            raise ValueError("invalid source path id")
    except (KeyError, TypeError, ValueError, OSError) as exc:
        errors.append(str(exc))
    return errors


def source_and_bundle_errors(env, package: Path, manifest: dict) -> list[str]:
    """Read authoritative Unity serialization, not exporter-derived expected pixels."""
    import UnityPy
    errors = []
    source_root = Path(manifest["source"]["arena_data_root"])
    for key, meta in manifest.get("textures", {}).items():
        if "native_volume" not in meta:
            continue
        bad = package_errors(meta, package)
        if bad:
            errors.extend(f"{key}: {error}" for error in bad)
            continue
        v = meta["native_volume"]
        try:
            # Historical extractor pools every texture class under this discriminator.
            # Keep those stable material references, but verify the actual class below.
            identity = hashlib.sha1(f"{v['source_file']}:{v['source_path_id']}:Texture2D".encode()).hexdigest()[:16]
            if identity != key:
                raise ValueError("source identity differs from texture id")
            source_env = UnityPy.load(str(source_root / v["source_file"]))
            candidates = [o for o in source_env.objects if o.type.name == "Texture3D"
                          and o.path_id == v["source_path_id"]
                          and Path(o.assets_file.name).name == v["source_file"]]
            if len(candidates) != 1:
                raise ValueError("expected one original volume")
            original = candidates[0]
            source = original.read_typetree()
            raw = (package / v["file"]).read_bytes()
            if bytes(original.read().image_data) != raw:
                raise ValueError("payload differs from original source bytes")
            for source_key, value in (("m_Format", 54), ("m_Width", v["width"]), ("m_Height", v["height"]),
                                      ("m_Depth", v["depth"]), ("m_MipCount", v["mip_count"]),
                                      ("m_ColorSpace", 0), ("m_DataSize", len(raw))):
                if source.get(source_key) != value:
                    raise ValueError("original " + source_key + " differs")
            built = [o for o in env.objects if o.type.name == "Texture3D" and o.read_typetree().get("m_Name") == key]
            if len(built) != 1:
                raise ValueError("expected exactly one built Texture3D")
            tree = built[0].read_typetree()
            if bytes(built[0].read().image_data) != raw:
                raise ValueError("built alpha bytes differ from original (axes/mips/alpha)")
            for field in ("m_Format", "m_Width", "m_Height", "m_Depth", "m_MipCount", "m_ColorSpace"):
                if tree.get(field) != source.get(field):
                    raise ValueError("built " + field + " differs from original")
            settings = source["m_TextureSettings"]
            fields = {"filter_mode": "m_FilterMode", "aniso_level": "m_Aniso", "mip_bias": "m_MipBias",
                      "wrap_u": "m_WrapU", "wrap_v": "m_WrapV", "wrap_w": "m_WrapW"}
            for field, original_field in fields.items():
                if abs(v["sampler"][field] - settings[original_field]) > 1e-6:
                    raise ValueError("package sampler differs from original: " + field)
                if abs(tree["m_TextureSettings"][original_field] - settings[original_field]) > 1e-6:
                    raise ValueError("built sampler differs from original: " + field)
            errors.extend(material_binding_errors(env, package, manifest, key))
        except (KeyError, TypeError, ValueError, OSError) as exc:
            errors.append(f"{key}: {exc}")
    return errors


def material_binding_errors(env, package: Path, manifest: dict, volume_id: str) -> list[str]:
    """A preserved but unreferenced asset is not a working particle texture."""
    errors = []
    for material_id in manifest.get("materials", {}):
        source = json.loads((package / "materials" / (material_id + ".json")).read_text(encoding="utf-8"))
        slots = [name for name, slot in source.get("tex_envs", {}).items()
                 if isinstance(slot, dict) and slot.get("texture_id") == volume_id]
        if not slots:
            continue
        matches = [o for o in env.objects if o.type.name == "Material"
                   and o.read_typetree().get("m_Name") == material_id]
        if len(matches) != 1:
            errors.append(f"{material_id}: expected one material using native volume {volume_id}")
            continue
        assigned = dict(matches[0].read().m_SavedProperties.m_TexEnvs)
        for slot in slots:
            try:
                pointer = assigned[slot].m_Texture
                if pointer is None or pointer.path_id == 0:
                    raise ValueError("unassigned")
                target = pointer.deref()
                if target.type.name != "Texture3D" or target.read_typetree().get("m_Name") != volume_id:
                    raise ValueError("wrong texture identity or dimension")
            except (KeyError, AttributeError, ValueError) as exc:
                errors.append(f"{material_id}/{slot}: native volume binding differs ({exc})")
    return errors
