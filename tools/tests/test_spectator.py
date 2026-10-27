"""Skinning/package safety fixtures; no Arena content is embedded in these tests."""
import copy
import gzip
import json
from pathlib import Path
import sys
import unittest

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools/extract'))
import extract_spectator as S


def fixture():
    mesh = dict(vertices=[[0, 0, 0]], normals=[[0, 1, 0]], tangents=[[1, 0, 0, 1]], uv=[[0, 0]],
                triangles=[[0, 0, 0]], weights=[[1, 0, 0, 0]], indices=[[0, 0, 0, 0]],
                bindposes=[[1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]])
    mesh['bone_hashes'] = [0]
    actor = dict(source_id=1, name='synthetic', nodes=[dict(name='synthetic', path_hash=0,
                 parent=-1, position=[0, 0, 0], rotation=[0, 0, 0, 1], scale=[1, 1, 1])],
                 parts=[dict(node=0, root=0, bones=[0], materials=['m'], mesh_id='mesh')])
    return dict(schema=3, map='arena_airpit', source_scene=S.MAP_SCENES['airpit'],
                container=dict(position=[0, 0, 0], rotation=[0, 0, 0, 1], scale=[1, 1, 1]),
                source_count=1, actors=[actor], meshes={'mesh': mesh},
                materials={'m': {'textures': {}}}, textures={},
                clips=[dict(name=name, duration=1, frames=2,
                       tracks=[dict(path_hash=0, kind=2, values=[0, 0, 0, 1]*2)]) for name in S.CLIPS])



class SpectatorValidation(unittest.TestCase):
    def setUp(self):
        self.data = fixture()

    def test_valid_positive_control(self):
        S.validate(self.data)

    def test_supported_map_scene_pairs(self):
        for name, scene in S.MAP_SCENES.items():
            with self.subTest(map=name):
                self.data.update(map=f'arena_{name}', source_scene=scene)
                S.validate(self.data, f'arena_{name}')

    def test_package_copied_under_wrong_map_rejected(self):
        with self.assertRaisesRegex(ValueError, 'package map'):
            S.validate(self.data, 'arena_bay5')

    def test_unrelated_map_rejected(self):
        self.data['map'] = 'factory4_day'
        with self.assertRaisesRegex(ValueError, 'schema/map'):
            S.validate(self.data)

    def test_wrong_scene_rejected(self):
        self.data['source_scene'] = S.MAP_SCENES['bay5']
        with self.assertRaisesRegex(ValueError, 'source scene'):
            S.validate(self.data)

    def test_legacy_package_without_container_rejected(self):
        self.data['schema'] = 2
        del self.data['container']
        with self.assertRaisesRegex(ValueError, 'schema/map'):
            S.validate(self.data)

    def test_translated_container_preserves_actor_local_placement(self):
        self.data.update(map='arena_bay5', source_scene=S.MAP_SCENES['bay5'])
        self.data['container']['position'] = [104.146446228, 6.381745338, -16.224702835]
        self.data['actors'][0]['nodes'][0]['position'] = [-10, 0, 2]
        S.validate(self.data, 'arena_bay5')
        self.assertEqual(self.data['actors'][0]['nodes'][0]['position'], [-10, 0, 2])

    def test_invalid_container_rejected(self):
        for prop, value in [('position', [0, 0]), ('position', [0, float('nan'), 0]),
                            ('rotation', [0, 0, 0, 0]), ('scale', [2, 1, 1])]:
            with self.subTest(prop=prop, value=value):
                data = copy.deepcopy(self.data)
                data['container'][prop] = value
                with self.assertRaises(ValueError):
                    S.validate(data)

    def test_scene_discovery_follows_build_settings_order(self):
        paths = ['Assets/Other.unity', 'Assets/Maps/Arena_Bay5_DesignStuff.unity']
        self.assertEqual(S.scene_index(paths, S.MAP_SCENES['bay5']), 1)
        self.assertEqual(S.scene_index(paths[::-1], S.MAP_SCENES['bay5']), 0)
        for invalid in [paths[:1], paths + paths]:
            with self.assertRaisesRegex(ValueError, 'one BuildSettings scene'):
                S.scene_index(invalid, S.MAP_SCENES['bay5'])

    def test_original_euler_xyz_order(self):
        # First X=90, then Z=90: qz*qx. Default Unity ZXY gives a different sign for y.
        expected = [.5, .5, .5, .5]
        for actual, target in zip(S.euler_quaternion([90, 0, 90], 0), expected):
            self.assertAlmostEqual(actual, target)
        self.assertAlmostEqual(S.euler_quaternion([90, 0, 90], 4)[1], -.5)

    def test_unknown_euler_order(self):
        with self.assertRaisesRegex(ValueError, 'rotation order'):
            S.euler_quaternion([0, 0, 0], 7)

    def test_implicit_single_influence_weights(self):
        weights, indices = S.skin_channels(None, [(0,), (2,)], 3)
        self.assertEqual(weights, [[1, 0, 0, 0]] * 2)
        self.assertEqual(indices, [[0, 0, 0, 0], [2, 0, 0, 0]])

    def test_missing_multi_influence_weights_rejected(self):
        with self.assertRaisesRegex(ValueError, 'missing non-rigid'):
            S.skin_channels(None, [(0, 1)], 2)

    def test_swapped_in_range_bones_rejected(self):
        import zlib
        node = copy.deepcopy(self.data['actors'][0]['nodes'][0])
        node.update(name='head', parent=0, path_hash=zlib.crc32(b'head'))
        self.data['actors'][0]['nodes'].append(node)
        mesh = self.data['meshes']['mesh']
        mesh['bindposes'] *= 2
        mesh['bone_hashes'] = [0, node['path_hash']]
        self.data['actors'][0]['parts'][0]['bones'] = [0, 1]
        S.validate(self.data)
        self.data['actors'][0]['parts'][0]['bones'] = [1, 0]
        with self.assertRaisesRegex(ValueError, 'bone path order'):
            S.validate(self.data)

    def test_weight_sum(self):
        self.data['meshes']['mesh']['weights'][0] = [.1, .1, .1, .1]
        with self.assertRaisesRegex(ValueError, 'weights'):
            S.validate(self.data)

    def test_nonfinite_weight(self):
        self.data['meshes']['mesh']['weights'][0][0] = float('nan')
        with self.assertRaisesRegex(ValueError, 'non-finite'):
            S.validate(self.data)

    def test_bone_out_of_range(self):
        self.data['meshes']['mesh']['indices'][0][0] = 1
        with self.assertRaisesRegex(ValueError, 'bone index'):
            S.validate(self.data)

    def test_missing_bindpose(self):
        self.data['meshes']['mesh']['bindposes'] = []
        with self.assertRaisesRegex(ValueError, 'bind pose|skin binding'):
            S.validate(self.data)

    def test_parent_cycle(self):
        self.data['actors'][0]['nodes'][0]['parent'] = 0
        with self.assertRaisesRegex(ValueError, 'hierarchy'):
            S.validate(self.data)

    def test_short_clip(self):
        self.data['clips'][0]['tracks'][0]['values'].pop()
        with self.assertRaisesRegex(ValueError, 'sample count'):
            S.validate(self.data)

    def test_unknown_track(self):
        self.data['clips'][0]['tracks'][0]['path_hash'] = 1
        with self.assertRaisesRegex(ValueError, 'bindings|track'):
            S.validate(self.data)

    def test_duplicate_track(self):
        self.data['clips'][0]['tracks'] *= 2
        with self.assertRaisesRegex(ValueError, 'duplicate track'):
            S.validate(self.data)

    def test_triangle_outside_mesh(self):
        self.data['meshes']['mesh']['triangles'][0][0] = 20
        with self.assertRaisesRegex(ValueError, 'triangle outside'):
            S.validate(self.data)

    def test_nonfinite_animation(self):
        self.data['clips'][0]['tracks'][0]['values'][0] = float('inf')
        with self.assertRaisesRegex(ValueError, 'non-finite'):
            S.validate(self.data)

    def test_shared_mesh_independent_placements(self):
        second = copy.deepcopy(self.data['actors'][0])
        second['source_id'] = 2
        second['nodes'][0]['position'] = [12, 8, -4]
        self.data['actors'].append(second)
        self.data['source_count'] = 2
        S.validate(self.data)
        self.assertEqual(len(self.data['meshes']), 1)

    def test_truncated_crowd(self):
        self.data['source_count'] = 99
        with self.assertRaisesRegex(ValueError, 'crowd count'):
            S.validate(self.data)

    def test_duplicate_source_id(self):
        self.data['actors'] *= 2
        self.data['source_count'] = 2
        with self.assertRaisesRegex(ValueError, 'duplicate spectator'):
            S.validate(self.data)

    def test_unresolved_source_mesh(self):
        self.data['actors'][0]['missing'] = ['head']
        with self.assertRaisesRegex(ValueError, 'unresolved spectator'):
            S.validate(self.data)

    def test_skin_cannot_be_static(self):
        self.data['actors'][0]['parts'][0]['bones'] = []
        with self.assertRaisesRegex(ValueError, 'skin binding'):
            S.validate(self.data)

    def test_bone_path_order(self):
        self.data['meshes']['mesh']['bone_hashes'] = [123]
        with self.assertRaisesRegex(ValueError, 'bone path order'):
            S.validate(self.data)

    def test_missing_shared_mesh(self):
        self.data['actors'][0]['parts'][0]['mesh_id'] = 'missing'
        with self.assertRaisesRegex(ValueError, 'missing shared'):
            S.validate(self.data)

    def test_orphan_shared_mesh(self):
        self.data['meshes']['orphan'] = self.data['meshes']['mesh']
        with self.assertRaisesRegex(ValueError, 'unreferenced shared'):
            S.validate(self.data)

    def test_missing_texture(self):
        self.data['materials']['m']['textures']['_MainTex'] = {'id': 'missing'}
        with self.assertRaisesRegex(ValueError, 'material texture'):
            S.validate(self.data)

    def test_invalid_attribute(self):
        self.data['meshes']['mesh']['normals'] = []
        with self.assertRaisesRegex(ValueError, 'attribute count'):
            S.validate(self.data)

    def test_node_path_hash(self):
        self.data['actors'][0]['nodes'][0]['path_hash'] = 1
        with self.assertRaisesRegex(ValueError, 'node path hash'):
            S.validate(self.data)

    @unittest.skipUnless(all((S.package_path(m) / 'spectator.json.gz').exists() for m in S.MAP_SCENES),
                         'owner-extracted packages unavailable')
    def test_real_packages(self):
        for name in S.MAP_SCENES:
            with self.subTest(map=name):
                self.check_real_package(name)

    def check_real_package(self, name):
        package = S.package_path(name)
        data = json.loads(gzip.decompress((package / 'spectator.json.gz').read_bytes()))
        S.validate(data, f'arena_{name}')
        self.assertEqual(len(data['actors']), 88 if name == 'bay5' else 99)
        self.assertEqual(len({a['name'].split(' (')[0] for a in data['actors']}), 15)
        self.assertTrue(all(not a['missing'] for a in data['actors']))
        self.assertLess(len(data['meshes']), sum(len(a['parts']) for a in data['actors']))
        self.assertEqual([c['name'] for c in data['clips']], list(S.CLIPS))
        for clip in data['clips']:
            self.assertEqual({x['path_hash'] for x in clip['absent_optional_tracks']}, {S.ABSENT_OPTIONAL_PATH})
            self.assertEqual({x['kind'] for x in clip['absent_optional_tracks']}, {1, 2, 3})
            self.assertFalse(clip['root_motion'])
            self.assertTrue(any(max(t['values']) - min(t['values']) > .1 for t in clip['tracks']))
        S.verify(package, f'arena_{name}')


if __name__ == '__main__':
    unittest.main()
