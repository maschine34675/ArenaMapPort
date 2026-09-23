"""Lossless Texture3D payloads for the source-authored ambient dust.

Texture3D.m_Format is a GraphicsFormat, NOT Texture2D's TextureFormat enum.
The verified source format 54 is Unity's internal Alpha8 graphics format: one
alpha byte per voxel. Passing 54 to UnityPy's Texture2D converter instead selects
ASTC_4x4 and corrupts these bytes. The native payload below is never decoded,
flipped, gamma-converted, resampled or recompressed. PNGs are inspection only.
"""
from __future__ import annotations

import hashlib
import math
import re
from pathlib import Path

from PIL import Image


class UnsupportedVolumeFormat(ValueError):
    """An unverified source encoding must remain unassigned, never guessed."""


def _integer(value, name: str, minimum: int, maximum: int) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError(f"Invalid volume {name}: {value!r}")
    return value


def alpha8_volume_metadata(typetree: dict, raw: bytes) -> dict:
    """Validate source layout and describe its complete native mip chain.

    Unity's raw voxel order is x fastest, then y, then z; mip levels follow in
    descending size. Texture3D.SetPixelData uses that same order. Partial source
    mip chains are preserved, not regenerated into complete chains.
    """
    if typetree.get("m_Format") != 54:
        raise UnsupportedVolumeFormat(f"Unverified Texture3D graphics format: {typetree.get('m_Format')!r}")
    if type(typetree.get("m_Format")) is not int:
        raise ValueError("Volume graphics format must be an integer")
    if type(typetree.get("m_ColorSpace")) is not int or typetree.get("m_ColorSpace") != 0:
        raise ValueError("Alpha8 volume must have source Linear color space")
    dimensions = [_integer(typetree.get(key), key, 1, 2048)
                  for key in ("m_Width", "m_Height", "m_Depth")]
    max_mips = max(dimensions).bit_length()
    mip_count = _integer(typetree.get("m_MipCount"), "m_MipCount", 1, max_mips)
    mips, offset = [], 0
    for level in range(mip_count):
        width, height, depth = [max(1, size >> level) for size in dimensions]
        byte_length = width * height * depth
        mips.append(dict(width=width, height=height, depth=depth,
                         offset=offset, byte_length=byte_length))
        offset += byte_length
    if offset > 64 * 1024 * 1024:
        raise ValueError("Native volume exceeds 64MiB payload limit")
    source_size = _integer(typetree.get("m_DataSize"), "m_DataSize", 1, 64 * 1024 * 1024)
    if len(raw) != offset or source_size != offset:
        raise ValueError(f"Volume payload size mismatch: source={typetree.get('m_DataSize')}, "
                         f"actual={len(raw)}, expected={offset}")
    settings = typetree.get("m_TextureSettings")
    if not isinstance(settings, dict):
        raise ValueError("Missing volume sampler settings")
    sampler = {
        "filter_mode": _integer(settings.get("m_FilterMode"), "filter mode", 0, 2),
        "aniso_level": _integer(settings.get("m_Aniso"), "anisotropy", 0, 16),
        "wrap_u": _integer(settings.get("m_WrapU"), "wrap U", 0, 3),
        "wrap_v": _integer(settings.get("m_WrapV"), "wrap V", 0, 3),
        "wrap_w": _integer(settings.get("m_WrapW"), "wrap W", 0, 3),
    }
    bias = settings.get("m_MipBias")
    if type(bias) not in (int, float) or not math.isfinite(bias) or not -16 <= bias <= 16:
        raise ValueError(f"Invalid volume mip bias: {bias!r}")
    sampler["mip_bias"] = bias
    return dict(schema_version=1, encoding="Alpha8", layout="x-fastest-y-z-mips",
                width=dimensions[0], height=dimensions[1], depth=dimensions[2],
                mip_count=mip_count, mips=mips, byte_length=offset,
                sha256=hashlib.sha256(raw).hexdigest(), source_graphics_format=54,
                color_space="Linear", sampler=sampler)


def alpha8_preview(metadata: dict, raw: bytes) -> Image.Image:
    """Top mip, z increasing down the strip; flip y only within each slice.

    A PNG row starts at the top, whereas native texture rows start at y=0. The
    flip is solely for human inspection; the .volume.bin is the untouched source.
    RGB is white and alpha is the source scalar, matching Alpha8 sampling.
    """
    width, height, depth = (metadata[k] for k in ("width", "height", "depth"))
    strip = Image.new("RGBA", (width, height * depth), (255, 255, 255, 0))
    for z in range(depth):
        offset = z * width * height
        alpha = Image.frombytes("L", (width, height), raw[offset:offset + width * height])
        alpha = alpha.transpose(Image.Transpose.FLIP_TOP_BOTTOM)
        frame = Image.new("RGBA", (width, height), (255, 255, 255, 255))
        frame.putalpha(alpha)
        strip.paste(frame, (0, z * height))
    return strip


def write_volume_texture(tex_obj, texture_id: str, output: str | Path) -> tuple[dict, Image.Image]:
    """Export a validated source volume; usable by full and additive extraction."""
    if not re.fullmatch(r"[0-9a-f]{16}", texture_id):
        raise ValueError(f"Invalid volume texture id: {texture_id!r}")
    if tex_obj.type.name != "Texture3D":
        raise ValueError("Native volume export requires Texture3D")
    source_file = tex_obj.assets_file.name.replace("\\", "/").rsplit("/", 1)[-1]
    source_path_id = _integer(tex_obj.path_id, "source path id", 1, 2**63 - 1)
    # Historical texture-pool IDs use the Texture2D discriminator for every
    # native texture class. Preserve existing material references and GUIDs.
    expected_id = hashlib.sha1(f"{source_file}:{source_path_id}:Texture2D".encode("utf-8")).hexdigest()[:16]
    if not source_file.endswith(".assets") or texture_id != expected_id:
        raise ValueError("Volume texture id does not match its source provenance")
    typetree = tex_obj.read_typetree()
    raw = bytes(typetree.get("image data", b""))
    stream = typetree.get("m_StreamData", {})
    if not isinstance(stream, dict):
        raise ValueError("Invalid volume stream metadata")
    stream_size = _integer(stream.get("size", 0), "stream size", 0, 64 * 1024 * 1024)
    if stream_size:
        if raw:
            raise ValueError("Volume has ambiguous inline and streamed payloads")
        stream_offset = _integer(stream.get("offset"), "stream offset", 0, 2**63 - 1)
        stream_path = stream.get("path")
        if not isinstance(stream_path, str) or not stream_path:
            raise ValueError("Invalid volume stream path")
        # Same resource resolver used by UnityPy's Texture2D.get_image_data.
        from UnityPy.helpers.ResourceReader import get_resource_data
        raw = bytes(get_resource_data(stream_path, tex_obj.assets_file, stream_offset, stream_size))
    metadata = alpha8_volume_metadata(typetree, raw)
    metadata["source_file"] = source_file
    metadata["source_path_id"] = source_path_id
    relative_path = f"textures/{texture_id}.volume.bin"
    destination = Path(output) / relative_path
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(raw)
    metadata["file"] = relative_path
    return metadata, alpha8_preview(metadata, raw)
