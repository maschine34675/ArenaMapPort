"""Bit-boundary, channel fidelity and fail-closed source mesh compatibility tests."""
import copy
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import mesh_compat as compat
from common_ids import HardExtractError
from UnityPy.classes import Mesh
from UnityPy.classes.generated import ChannelInfo, CompressedMesh, PackedBitVector, VertexData


def packed(values=(), bits=0, float_values=False, start=0, span=1):
    value = sum(v << (i * bits) for i, v in enumerate(values))
    result = {'m_NumItems': len(values), 'm_BitSize': bits,
              'm_Data': value.to_bytes((len(values) * bits + 7) // 8, 'little')}
    if float_values:
        result.update(m_Start=start, m_Range=span)
    return result


def fixture():
    p = {key: packed() for key in ('m_Weights', 'm_BoneIndices')}
    p.update(m_Vertices=packed([0, 0, 0, 31, 0, 0, 0, 31, 0], 5, True),
             m_Normals=packed([0] * 6, 0, True, span=0), m_NormalSigns=packed([0, 1, 1], 1),
             m_Tangents=packed([1, 0] * 3, 1, True), m_TangentSigns=packed([0] * 6),
             m_FloatColors=packed([3, 0, 1, 3] * 3, 2, True),
             m_UV=packed([0, 0, 3, 0, 0, 3], 2, True), m_UVInfo=5,
             m_Triangles=packed([0, 1, 2], 2))
    return {'m_Name': 'asymmetric packed triangle', 'm_MeshCompression': 1, 'm_IndexFormat': 0,
            'm_IndexBuffer': [], 'm_CompressedMesh': p,
            'm_SubMeshes': [{'firstByte': 0, 'indexCount': 3, 'topology': 0, 'baseVertex': 0}],
            'm_VertexData': {'m_VertexCount': 0, 'm_DataSize': b'', 'm_Channels':
                [{'stream': 0, 'offset': 0, 'format': 0, 'dimension': 0} for _ in range(14)]}}


class Reader:
    path_id = 123
    version = (6000, 3, 6, 1)

    def __init__(self, tree):
        self.tree = tree

    def read_typetree(self):
        return self.tree

    def get_raw_data(self):
        return b'synthetic source fixture'

    def read(self):
        t = self.tree
        fields = {k: PackedBitVector(**v) if isinstance(v, dict) else v
                  for k, v in t['m_CompressedMesh'].items()}
        vd = copy.deepcopy(t['m_VertexData'])
        vd['m_Channels'] = [ChannelInfo(**v) for v in vd['m_Channels']]
        typed = Mesh(m_Name=t['m_Name'], m_BindPose=[], m_CompressedMesh=CompressedMesh(**fields),
                     m_IndexBuffer=[], m_LocalAABB=None, m_MeshCompression=t['m_MeshCompression'],
                     m_MeshUsageFlags=0, m_SubMeshes=[], m_IndexFormat=t['m_IndexFormat'],
                     m_VertexData=VertexData(**vd))
        typed.set_object_reader(self)
        return typed


class MeshCompatibilityTests(unittest.TestCase):
    def test_actual_decoder_retains_channels_zero_bit_signs_and_triangle(self):
        tree, channels, indices, ranges = compat.decode_compressed(Reader(fixture()))
        channels = {key: list(map(tuple, values)) for key, values in channels.items()}
        self.assertEqual(channels['positions'], [(0., 0., 0.), (1., 0., 0.), (0., 1., 0.)])
        self.assertEqual(channels['normals'], [(0., 0., -1.), (0., 0., 1.), (0., 0., 1.)])
        self.assertEqual(channels['tangents'], [(1., 0., -0., -1.)] * 3)
        self.assertEqual(channels['uv0'], [(0., 0.), (1., 0.), (0., 1.)])
        self.assertEqual(channels['colors'][0], (1., 0., 1 / 3, 1.))
        self.assertEqual(indices, [0, 1, 2])
        self.assertEqual(ranges, [(0, 3, 0)])

    def test_binary_matches_ampm_layout_and_does_not_divide_float_colors(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'triangle.bin'
            result = compat.write_compressed_mesh_bin(Reader(fixture()), 'source.assets', target)
            data = target.read_bytes()
        self.assertEqual(struct.unpack_from('<4sIIHBB', data), (b'AMPM', 1, 3, 31, 0, 1))
        color_start = 16 + 3 * (3 + 3 + 4) * 4
        self.assertEqual(struct.unpack_from('<4f', data, color_start)[0], 1.)
        self.assertEqual(struct.unpack_from('<IIB3H', data, len(data) - 15), (0, 3, 0, 0, 1, 2))
        self.assertEqual(result.vertex_count, 3)

    def test_two_sparse_uv_channels_keep_their_source_order(self):
        tree = fixture()
        tree['m_CompressedMesh']['m_UVInfo'] = 5 | (5 << 28)
        tree['m_CompressedMesh']['m_UV'] = packed([0] * 6 + [3] * 6, 2, True)
        channels = compat.decode_compressed(Reader(tree))[1]
        channels = {key: list(map(tuple, values)) for key, values in channels.items()}
        self.assertEqual(channels['uv0'], [(0., 0.)] * 3)
        self.assertEqual(channels['uv7'], [(1., 1.)] * 3)

    def test_truncated_byte_stream_and_short_sign_count_fail_before_writing(self):
        for channel in ('m_Vertices', 'm_NormalSigns', 'm_Triangles'):
            with self.subTest(channel=channel), tempfile.TemporaryDirectory() as directory:
                tree = fixture()
                tree['m_CompressedMesh'][channel]['m_Data'] = b''
                path = Path(directory) / 'bad.bin'
                with self.assertRaisesRegex(HardExtractError, 'truncated packed bits'):
                    compat.write_compressed_mesh_bin(Reader(tree), 'source.assets', path)
                self.assertFalse(path.exists())
        tree = fixture()
        tree['m_CompressedMesh']['m_NormalSigns']['m_NumItems'] = 2
        with self.assertRaisesRegex(HardExtractError, 'items != required'):
            compat.decode_compressed(Reader(tree))

    def test_invalid_range_width_shape_and_index_fail(self):
        cases = [lambda t: t['m_CompressedMesh']['m_Vertices'].update(m_Start=float('nan')),
                 lambda t: t['m_CompressedMesh']['m_Vertices'].update(m_BitSize=33),
                 lambda t: t['m_CompressedMesh']['m_Vertices'].update(m_NumItems=8),
                 lambda t: t['m_CompressedMesh'].update(m_Triangles=packed([0, 1, 3], 2)),
                 lambda t: t['m_SubMeshes'][0].update(firstByte=1),
                 lambda t: t['m_SubMeshes'][0].update(indexCount=6),
                 lambda t: t['m_SubMeshes'][0].update(baseVertex=1),
                 lambda t: t['m_SubMeshes'][0].update(topology=1)]
        for change in cases:
            tree = fixture()
            change(tree)
            with self.subTest(change=change), self.assertRaises(HardExtractError):
                compat.decode_compressed(Reader(tree))

    def test_unrepresentable_channels_fail_instead_of_silent_truncation(self):
        for name, value in [('m_UVInfo', 6), ('m_UVInfo', 0x555),
                            ('m_Weights', packed([1], 1)), ('m_Colors', packed([1], 32))]:
            tree = fixture()
            tree['m_CompressedMesh'][name] = value
            with self.subTest(name=name, value=value), self.assertRaises(HardExtractError):
                compat.decode_compressed(Reader(tree))

    def test_empty_source_is_retained_and_payload_cannot_be_disguised_as_empty(self):
        tree = fixture()
        tree['m_MeshCompression'] = 0
        tree['m_SubMeshes'][0]['indexCount'] = 0
        tree['m_VertexData']['m_Channels'][0]['dimension'] = 3
        tree['m_CompressedMesh'] = {k: 0 if k == 'm_UVInfo' else packed() for k in tree['m_CompressedMesh']}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'empty.bin'
            result = compat.write_empty_mesh_bin(Reader(tree), 'source.assets', path)
            self.assertEqual(path.read_bytes(), struct.pack('<4sIIHBBIIB', b'AMPM', 1, 0, 1, 0, 1, 0, 0, 0))
            self.assertEqual(result.submesh_count, 1)
            tree['m_CompressedMesh']['m_Vertices'] = packed([1], 1)
            with self.assertRaises(HardExtractError):
                compat.write_empty_mesh_bin(Reader(tree), 'source.assets', path)

    def test_scope_restores_legacy_writer_even_on_error_and_adds_source_identity(self):
        calls = []
        def legacy(*args):
            calls.append(args)
            return 'unchanged'
        engine = SimpleNamespace(write_mesh_bin=legacy)
        source = Reader({'m_MeshCompression': 0, 'm_VertexData': {'m_VertexCount': 3}})
        with compat.compressed_mesh_decoder(engine) as evidence:
            self.assertEqual(engine.write_mesh_bin(source, 'source.assets', 'unused'), 'unchanged')
            self.assertEqual(evidence, [])
        self.assertIs(engine.write_mesh_bin, legacy)
        self.assertEqual(len(calls), 1)
        with self.assertRaisesRegex(HardExtractError, 'source.assets:123'):
            with compat.compressed_mesh_decoder(engine):
                engine.write_mesh_bin(Reader({}), 'source.assets', 'unused')
        self.assertIs(engine.write_mesh_bin, legacy)


if __name__ == '__main__':
    unittest.main()
