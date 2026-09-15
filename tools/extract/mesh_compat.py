"""Scoped, loss-checked compressed Mesh support for the generic map extractor.

UnityPy 1.25.2's MeshHandler decodes the reached PackedBitVector layout. Its
results are already floating point: compressed FloatColors must not pass through
the legacy raw-UNorm normalization a second time. The packed position count is
authoritative because fully compressed meshes have vertexData.vertexCount=0.

The source engine and AMPM v1 layout remain unchanged. Unsupported source fields
fail explicitly before writing; this adapter does not manufacture missing data.
"""
from contextlib import contextmanager
import hashlib
import math
import struct

from UnityPy.helpers.MeshHelper import MeshHandler

from common_ids import HardExtractError, content_id
from mesh_bin import MAGIC, VERSION, MeshBinResult


def _require(condition, message):
    if not condition:
        raise HardExtractError(message)


def _packed(field, name, expected=None):
    """Check bounds before UnityPy allocates/reads the packed stream.

    Zero bits with nonzero items and empty data is valid: it represents a
    constant value, including the actual BRDM tangent-sign channel.
    """
    _require(isinstance(field, dict), f"{name}: missing packed vector")
    count, bits, data = field.get('m_NumItems'), field.get('m_BitSize'), field.get('m_Data')
    _require(type(count) is int and count >= 0, f"{name}: invalid item count")
    _require(type(bits) is int and 0 <= bits <= 32, f"{name}: invalid bit width")
    _require(isinstance(data, (bytes, bytearray, list)), f"{name}: invalid packed data")
    _require(len(data) * 8 >= count * bits, f"{name}: truncated packed bits")
    if expected is not None:
        _require(count == expected, f"{name}: {count} items != required {expected}")
    if 'm_Range' in field or 'm_Start' in field:
        _require(all(isinstance(field.get(k), (int, float)) and math.isfinite(field[k])
                     for k in ('m_Range', 'm_Start')), f"{name}: nonfinite float range/start")
        _require(field['m_Range'] >= 0, f"{name}: negative float range")
    return count


def _source_layout(tree):
    _require(tree.get('m_MeshCompression') in (1, 2, 3), 'unsupported mesh compression setting')
    vd = tree.get('m_VertexData') or {}
    stream = tree.get('m_StreamData') or {}
    _require(not vd.get('m_VertexCount') and not vd.get('m_DataSize') and
             not any(c.get('dimension') for c in vd.get('m_Channels', [])) and
             not stream.get('size') and not stream.get('path') and not tree.get('m_IndexBuffer'),
             'compressed mesh also contains raw/streamed vertex or index data; mixed layout unsupported')
    _require(not tree.get('m_BindPose') and not tree.get('m_BoneNameHashes') and
             not (tree.get('m_VariableBoneCountWeights') or {}).get('m_Data') and
             not any((tree.get('m_Shapes') or {}).get(k) for k in ('vertices', 'shapes', 'channels', 'fullWeights')),
             'compressed skin/blendshape data cannot be represented by static AMPM v1')
    packed = tree.get('m_CompressedMesh')
    _require(isinstance(packed, dict), 'missing compressed mesh')
    allowed = {'m_Vertices', 'm_UV', 'm_Normals', 'm_Tangents', 'm_Weights', 'm_NormalSigns',
               'm_TangentSigns', 'm_FloatColors', 'm_BoneIndices', 'm_Triangles', 'm_UVInfo',
               'm_Colors', 'm_BindPoses'}
    _require(not (packed.keys() - allowed), 'unrecognized compressed mesh channel')
    for key, field in packed.items():
        if key != 'm_UVInfo' and field is not None:
            _packed(field, key)
    for key in ('m_Weights', 'm_BoneIndices', 'm_BindPoses', 'm_Colors'):
        _require(not (packed.get(key) or {}).get('m_NumItems'),
                 f'{key}: legacy color or skin layout unsupported by this scoped adapter')
    count = _packed(packed.get('m_Vertices'), 'm_Vertices')
    _require(count > 0 and count % 3 == 0, 'packed positions must contain complete nonempty XYZ vertices')
    count //= 3
    channels = {'positions': 3}
    for field, signs, multiplier, name, dimension in (
        ('m_Normals', 'm_NormalSigns', 1, 'normals', 3),
        ('m_Tangents', 'm_TangentSigns', 2, 'tangents', 4),
    ):
        present = bool((packed.get(field) or {}).get('m_NumItems'))
        _packed(packed.get(field), field, count * 2 if present else 0)
        _packed(packed.get(signs), signs, count * multiplier if present else 0)
        _require(packed[signs]['m_BitSize'] <= 1, f'{signs}: signs must contain binary values')
        if present:
            channels[name] = dimension
    colors = (packed.get('m_FloatColors') or {}).get('m_NumItems', 0)
    if colors:
        _packed(packed['m_FloatColors'], 'm_FloatColors', count * 4)
        channels['colors'] = 4
    uv_count = (packed.get('m_UV') or {}).get('m_NumItems', 0)
    info = packed.get('m_UVInfo', 0) or 0
    _require(type(info) is int and 0 <= info <= 0xffffffff, 'invalid UV info word')
    uv_channels = []
    if info:
        for number in range(8):
            nibble = (info >> (4 * number)) & 15
            _require(nibble in (0, 5), f'UV{number}: AMPM v1 requires two-dimensional present UV channels')
            if nibble:
                uv_channels.append(number)
        # UnityPy 1.25.2 assigns rather than accumulates the packed source offset.
        # Zero, one, or two channels are exact; later channels would repeat data.
        _require(len(uv_channels) <= 2, 'three or more compressed UV channels require a corrected decoder')
        _require(uv_count == count * 2 * len(uv_channels), 'UV info/count mismatch')
    elif uv_count:
        _require(uv_count in (count * 2, count * 4), 'legacy compressed UV item count unsupported')
        uv_channels = list(range(uv_count // (count * 2)))
    for number in uv_channels:
        channels[f'uv{number}'] = 2
    _require(tree.get('m_IndexFormat') in (0, 1), 'invalid source index format')
    submeshes = tree.get('m_SubMeshes')
    _require(isinstance(submeshes, list) and 0 < len(submeshes) <= 255, 'invalid AMPM submesh count')
    index_size = 2 if tree['m_IndexFormat'] == 0 else 4
    index_count = _packed(packed.get('m_Triangles'), 'm_Triangles')
    _require(index_count > 0, 'compressed mesh has no index buffer')
    ranges = []
    for sm in submeshes:
        start, length = sm.get('firstByte'), sm.get('indexCount')
        _require(type(start) is int and start >= 0 and start % index_size == 0 and
                 type(length) is int and length >= 0 and length % 3 == 0,
                 'submesh has invalid byte offset or triangle index count')
        _require(sm.get('topology') == 0 and sm.get('baseVertex', 0) == 0,
                 'compressed non-triangle topology or nonzero baseVertex unsupported')
        _require(start // index_size + length <= index_count, 'submesh exceeds packed index buffer')
        ranges.append((start // index_size, length, 0))
    _require(sum(length for _, length, _ in ranges) == index_count,
             'AMPM importer requires complete index coverage by submeshes')
    cursor = 0
    for start, length, _ in sorted(ranges):
        _require(start == cursor, 'overlapping or gapped compressed submesh ranges unsupported')
        cursor += length
    return count, channels, ranges


def decode_compressed(mesh_reader):
    """Return validated UnityPy-decoded AMPM channels, with no coordinate flip."""
    tree = mesh_reader.read_typetree()
    count, layout, ranges = _source_layout(tree)
    handler = MeshHandler(mesh_reader.read())
    try:
        handler.process()
    except (AssertionError, IndexError, ValueError, TypeError, ZeroDivisionError, struct.error) as exc:
        raise HardExtractError(f'compressed mesh decoder failed: {exc}') from exc
    _require(handler.m_VertexCount == count, 'decoder vertex count disagrees with packed positions')
    names = {'positions': 'm_Vertices', 'normals': 'm_Normals', 'tangents': 'm_Tangents',
             'colors': 'm_Colors', **{f'uv{i}': f'm_UV{i}' for i in range(8)}}
    decoded = {}
    for name, attribute in names.items():
        values = getattr(handler, attribute, None)
        expected = name in layout
        _require(bool(values) == expected, f'decoded {name} presence disagrees with packed source')
        if expected:
            dimension = layout[name]
            _require(len(values) == count and all(len(row) == dimension for row in values),
                     f'decoded {name} count/dimension mismatch')
            _require(all(math.isfinite(v) and abs(v) <= 3.4028234663852886e38
                         for row in values for v in row), f'decoded {name} has nonfinite/out-of-float32 values')
            decoded[name] = values
    indices = handler.m_IndexBuffer
    _require(indices is not None and len(indices) == tree['m_CompressedMesh']['m_Triangles']['m_NumItems'],
             'decoded index count mismatch')
    maximum = 65535 if tree['m_IndexFormat'] == 0 else 0xffffffff
    _require(all(type(i) is int and 0 <= i < count and i <= maximum for i in indices),
             'decoded index outside vertex buffer or source index format')
    return tree, decoded, list(indices), ranges


def write_compressed_mesh_bin(mesh_reader, source_file_basename, out_path):
    """Write the exact existing AMPM v1 schema after complete validation."""
    try:
        tree, channels, indices, ranges = decode_compressed(mesh_reader)
    except HardExtractError as exc:
        raise HardExtractError(f'mesh {source_file_basename}:{mesh_reader.path_id}: {exc}') from exc
    count = len(channels['positions'])
    ordered = [('positions', 3), ('normals', 3), ('tangents', 4), ('colors', 4)]
    ordered += [(f'uv{i}', 2) for i in range(8)]
    mask = sum(1 << i for i, (name, _) in enumerate(ordered) if name in channels)
    index_format = tree['m_IndexFormat']
    with open(out_path, 'wb') as output:
        output.write(MAGIC)
        output.write(struct.pack('<IIHBB', VERSION, count, mask, index_format, len(ranges)))
        for name, width in ordered:
            if name in channels:
                pack = struct.Struct(f'<{width}f').pack
                for values in channels[name]:
                    output.write(pack(*values))
        for submesh in ranges:
            output.write(struct.pack('<IIB', *submesh))
        pack_index = struct.Struct('<H' if index_format == 0 else '<I').pack
        for index in indices:
            output.write(pack_index(index))
    return MeshBinResult(content_id(source_file_basename, mesh_reader.path_id, 'Mesh'), count,
                         len(ranges), tree.get('m_Name', ''), mask, index_format, False)


def write_empty_mesh_bin(mesh_reader, basename, path):
    """Preserve a source-proven empty mesh, including its empty submesh records."""
    tree = mesh_reader.read_typetree()
    vd, stream = tree.get('m_VertexData') or {}, tree.get('m_StreamData') or {}
    _require(tree.get('m_MeshCompression') == 0 and vd.get('m_VertexCount') == 0 and
             not vd.get('m_DataSize') and not tree.get('m_IndexBuffer') and
             not stream.get('size') and not stream.get('path'), 'empty source mesh has vertex/index/stream payload')
    for key in ('m_Vertices', 'm_Normals', 'm_Tangents', 'm_Colors', 'm_UV', 'm_UV1', 'm_Skin',
                'm_BindPose', 'm_BoneNameHashes', 'm_ShapeVertices', 'm_CollisionTriangles'):
        _require(not tree.get(key), f'empty source mesh still contains {key}')
    _require(not (tree.get('m_VariableBoneCountWeights') or {}).get('m_Data') and
             not any((tree.get('m_Shapes') or {}).get(k) for k in ('vertices', 'shapes', 'channels', 'fullWeights')),
             'empty source mesh still contains skin/blendshape payload')
    for key, field in (tree.get('m_CompressedMesh') or {}).items():
        if key == 'm_UVInfo':
            _require(not field, 'empty source mesh has packed UV declaration')
        elif field is not None:
            _packed(field, key, 0)
            _require(not field['m_Data'], 'empty source mesh still contains packed data')
    channels = vd.get('m_Channels', [])
    widths = [3, 3, 4, 4] + [2] * 8
    mask = 1
    for number, channel in enumerate(channels):
        dimension = channel.get('dimension', 0)
        if dimension:
            _require(number < len(widths) and dimension == widths[number],
                     'empty source vertex declaration is not representable in AMPM')
            mask |= 1 << number
    submeshes = tree.get('m_SubMeshes')
    _require(isinstance(submeshes, list) and len(submeshes) <= 255, 'invalid empty source submesh count')
    for sm in submeshes:
        _require(all(sm.get(key, 0) == 0 for key in ('firstByte', 'indexCount', 'topology', 'baseVertex',
                                                  'firstVertex', 'vertexCount')),
                 'empty source mesh has nonempty submesh metadata')
    index_format = tree.get('m_IndexFormat')
    _require(index_format in (0, 1), 'invalid empty source index format')
    with open(path, 'wb') as output:
        output.write(MAGIC + struct.pack('<IIHBB', VERSION, 0, mask, index_format, len(submeshes)))
        for _ in submeshes:
            output.write(struct.pack('<IIB', 0, 0, 0))
    return MeshBinResult(content_id(basename, mesh_reader.path_id, 'Mesh'), 0, len(submeshes),
                         tree.get('m_Name', ''), mask, index_format, False)


@contextmanager
def compressed_mesh_decoder(engine):
    """Scope only the generic caller's EQ.write_meshes to this additional format.

    Usage: with compressed_mesh_decoder(EQ) as evidence: meshes = EQ.write_meshes(ctx, output)
    The exact previous function is restored after success or failure; uncompressed
    meshes call it unchanged. No scene/component or source object is modified.
    """
    previous = engine.write_mesh_bin
    evidence = []

    def compatible(reader, basename, path):
        try:
            tree = reader.read_typetree()
            compression = tree.get('m_MeshCompression')
            if compression == 0 and (tree.get('m_VertexData') or {}).get('m_VertexCount') != 0:
                return previous(reader, basename, path)
            if compression == 0:
                result = write_empty_mesh_bin(reader, basename, path)
                kind = 'source_empty'
            else:
                result = write_compressed_mesh_bin(reader, basename, path)
                kind = 'packed_bit_vectors'
            evidence.append({'schema_version': 1, 'kind': kind, 'source_file': basename,
                'source_path_id': reader.path_id, 'source_object_sha256': hashlib.sha256(reader.get_raw_data()).hexdigest(),
                'mesh_id': result.content_id, 'source_name': result.source_object_name,
                'source_mesh_compression': compression, 'vertex_count': result.vertex_count,
                'submesh_count': result.submesh_count, 'channel_mask': result.channel_mask,
                'index_format': result.index_format, 'decoder': 'UnityPy.MeshHandler' if compression else 'source_empty',
                'geometry_substituted': False})
            return result
        except Exception as exc:
            raise HardExtractError(f'mesh {basename}:{reader.path_id}: {exc}') from exc

    engine.write_mesh_bin = compatible
    try:
        yield evidence
    finally:
        engine.write_mesh_bin = previous
