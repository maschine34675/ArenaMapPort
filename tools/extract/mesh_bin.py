"""Mesh -> meshes/<id>.mesh.bin writer (docs/SPEC.md §C.2 binary layout).

Uses UnityPy's own UnityPy.helpers.MeshHelper.MeshHandler to decode the
per-vertex channel data (this is the exact internal machinery
UnityPy.export.MeshExporter uses for its OBJ exporter -- reused here instead
of re-implementing vertex-stream unpacking, but bypassing the OBJ text
format entirely so we keep Unity's raw, un-flipped left-handed coordinates
and every channel §C.2 wants, not just position/uv0/normal).

Channel format handling: read_vertex_data() (UnityPy's internal decoder)
returns RAW, unnormalized component values matching each channel's declared
VertexFormat -- float channels (position/normal/tangent/uv, format 0) come
back as plain floats, but a vertex-color channel is typically encoded
UNorm8 (format 2, unsigned-byte struct type "B", raw ints 0-255) and is NOT
auto-normalized by UnityPy (only the *legacy* Color32 m_Colors array path
divides by 255 internally). We therefore normalize every channel ourselves
from its own declared format code, which is correct generically instead of
assuming "colors are always /255".
"""
from __future__ import annotations

import struct
from dataclasses import dataclass

from UnityPy.helpers.MeshHelper import MeshHandler

from common_ids import HardExtractError, content_id

MAGIC = b"AMPM"
VERSION = 1

# UnityEngine.Rendering.VertexAttributeFormat-equivalent raw codes as used in
# m_VertexData.m_Channels[i].format for Unity >= 2019 (UnityPy's `VertexFormat`
# enum, UnityPy/enums/VertexFormat.py) -- confirmed against real Arena data
# this pass (probe_fields.py): position/normal/tangent/uv channels all read
# format 0 (Float). No color-channel example was hit in the S1 slice itself,
# so the UNorm8/SNorm8/... branches below are implemented from Unity's own
# documented VertexFormat semantics rather than a second direct sample --
# flagged in docs/progress/wp1.md as untested-on-real-Arena-color-data.
def _normalize(v: float, fmt: int) -> float:
    if fmt in (0, 1):          # Float, Float16
        return float(v)
    if fmt == 2:                # UNorm8
        return v / 255.0
    if fmt == 3:                # SNorm8
        return max(v / 127.0, -1.0)
    if fmt == 4:                # UNorm16
        return v / 65535.0
    if fmt == 5:                # SNorm16
        return max(v / 32767.0, -1.0)
    return float(v)              # UInt/SInt channels -- not expected here


@dataclass
class MeshBinResult:
    content_id: str
    vertex_count: int
    submesh_count: int
    source_object_name: str
    channel_mask: int
    index_format: int
    had_nonzero_topology: bool


def _channel_format(vertex_data_tt, channel_index: int):
    chans = vertex_data_tt.get("m_Channels", [])
    if channel_index >= len(chans):
        return None
    c = chans[channel_index]
    if c.get("dimension", 0) <= 0:
        return None
    return c.get("format", 0)


def _norm_tuple(t, fmt, keep):
    return tuple(_normalize(v, fmt) for v in t[:keep])


def write_mesh_bin(mesh_reader, source_file_basename: str, out_path: str) -> MeshBinResult:
    """mesh_reader: a UnityPy ObjectReader for a Mesh object (Environment-
    resident, i.e. mesh_reader.read_typetree()/.read() both work)."""
    tt = mesh_reader.read_typetree()
    compression = tt.get("m_MeshCompression")
    if compression != 0:
        raise HardExtractError(
            f"mesh path_id={mesh_reader.path_id} in {source_file_basename}: "
            f"m_MeshCompression={compression} != 0 (Off) -- §C.2 has no "
            f"compressed-mesh decode path, hard extractor failure per §B.WP1 step 2."
        )

    name = tt.get("m_Name", "")
    index_format = tt.get("m_IndexFormat", 0)
    submeshes_tt = tt.get("m_SubMeshes", [])
    vd_tt = tt.get("m_VertexData", {})

    mesh_typed = mesh_reader.read()
    handler = MeshHandler(mesh_typed)
    handler.process()

    vertex_count = handler.m_VertexCount
    vd_expected = vd_tt.get("m_VertexCount")
    if vd_expected is not None and vertex_count != vd_expected:
        raise HardExtractError(
            f"mesh path_id={mesh_reader.path_id} in {source_file_basename}: "
            f"decoded vertex_count {vertex_count} != typetree m_VertexData."
            f"m_VertexCount {vd_expected}"
        )

    has_normal = bool(handler.m_Normals)
    has_tangent = bool(handler.m_Tangents)
    has_color = bool(handler.m_Colors)
    uv_present = [bool(getattr(handler, f"m_UV{n}", None)) for n in range(8)]

    channel_mask = 1  # bit 0: position, always present
    if has_normal:
        channel_mask |= 1 << 1
    if has_tangent:
        channel_mask |= 1 << 2
    if has_color:
        channel_mask |= 1 << 3
    for n, present in enumerate(uv_present):
        if present:
            channel_mask |= 1 << (4 + n)

    # channel format codes, for correct per-channel normalization (see
    # module docstring). Standard 2018+ channel ordering: 0 pos,1 normal,
    # 2 tangent,3 color,4..11 uv0..uv7 -- matches the layout observed in
    # probe_fields.py this pass.
    fmt_pos = _channel_format(vd_tt, 0) or 0
    fmt_normal = _channel_format(vd_tt, 1) or 0
    fmt_tangent = _channel_format(vd_tt, 2) or 0
    fmt_color = _channel_format(vd_tt, 3) or 2  # default UNorm8 if absent-but-present is impossible
    fmt_uv = [_channel_format(vd_tt, 4 + n) or 0 for n in range(8)]

    positions = [_norm_tuple(v, fmt_pos, 3) for v in handler.m_Vertices]
    normals = [_norm_tuple(v, fmt_normal, 3) for v in handler.m_Normals] if has_normal else []
    tangents = [_norm_tuple(v, fmt_tangent, 4) for v in handler.m_Tangents] if has_tangent else []
    colors = [_norm_tuple(v, fmt_color, 4) for v in handler.m_Colors] if has_color else []
    uvs = []
    for n in range(8):
        if uv_present[n]:
            raw = getattr(handler, f"m_UV{n}")
            uvs.append([_norm_tuple(v, fmt_uv[n], 2) for v in raw])

    had_nonzero_topology = False
    submeshes = []
    for sm in submeshes_tt:
        first_byte = sm.get("firstByte", 0)
        index_bytes = 2 if index_format == 0 else 4
        index_start = first_byte // index_bytes
        index_count = sm.get("indexCount", 0)
        topology = sm.get("topology", 0)
        if topology != 0:
            had_nonzero_topology = True
        submeshes.append((index_start, index_count, topology))

    indices = list(handler.m_IndexBuffer) if handler.m_IndexBuffer else []
    expected_total = sum(c for _, c, _ in submeshes)
    if len(indices) < expected_total:
        raise HardExtractError(
            f"mesh path_id={mesh_reader.path_id} in {source_file_basename}: "
            f"index buffer has {len(indices)} indices, submeshes need {expected_total}"
        )

    with open(out_path, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<I", VERSION))
        f.write(struct.pack("<I", vertex_count))
        f.write(struct.pack("<H", channel_mask))
        f.write(struct.pack("<B", 0 if index_format == 0 else 1))
        f.write(struct.pack("<B", len(submeshes)))
        for p in positions:
            f.write(struct.pack("<3f", *p))
        for n in normals:
            f.write(struct.pack("<3f", *n))
        for t in tangents:
            f.write(struct.pack("<4f", *t))
        for c in colors:
            f.write(struct.pack("<4f", *c))
        for uv_channel in uvs:
            for uv in uv_channel:
                f.write(struct.pack("<2f", *uv))
        for index_start, index_count, topology in submeshes:
            f.write(struct.pack("<IIB", index_start, index_count, topology))
        idx_char = "H" if index_format == 0 else "I"
        for i in indices:
            f.write(struct.pack(f"<{idx_char}", i))

    cid = content_id(source_file_basename, mesh_reader.path_id, "Mesh")
    return MeshBinResult(
        content_id=cid,
        vertex_count=vertex_count,
        submesh_count=len(submeshes),
        source_object_name=name,
        channel_mask=channel_mask,
        index_format=index_format,
        had_nonzero_topology=had_nonzero_topology,
    )
