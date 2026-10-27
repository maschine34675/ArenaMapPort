"""Skinning/package safety fixtures; no Arena content is embedded in these tests."""
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
    return dict(schema=1, map='arena_airpit', nodes=[dict(parent=-1, position=[0, 0, 0],
                rotation=[0, 0, 0, 1], scale=[1, 1, 1])],
                parts=[dict(node=0, bones=[0], materials=['m'], mesh=mesh)],
                clips=[dict(duration=1, frames=2, tracks=[dict(node=0, kind=2, values=[0, 0, 0, 1]*2)]) for _ in range(2)])


class SpectatorValidation(unittest.TestCase):
    def setUp(self):
        self.data = fixture()

    def test_valid_positive_control(self):
        S.validate(self.data)

    def test_original_euler_xyz_order(self):
        # First X=90, then Z=90: qz*qx. Default Unity ZXY gives a different sign for y.
        expected = [.5, .5, .5, .5]
        for actual, target in zip(S.euler_quaternion([90, 0, 90], 0), expected):
            self.assertAlmostEqual(actual, target)
        self.assertAlmostEqual(S.euler_quaternion([90, 0, 90], 4)[1], -.5)

    def test_unknown_euler_order(self):
        with self.assertRaisesRegex(ValueError, 'rotation order'):
            S.euler_quaternion([0, 0, 0], 7)

    def test_weight_sum(self):
        self.data['parts'][0]['mesh']['weights'][0] = [.1, .1, .1, .1]
        with self.assertRaisesRegex(ValueError, 'weights'):
            S.validate(self.data)

    def test_nonfinite_weight(self):
        self.data['parts'][0]['mesh']['weights'][0][0] = float('nan')
        with self.assertRaisesRegex(ValueError, 'non-finite'):
            S.validate(self.data)

    def test_bone_out_of_range(self):
        self.data['parts'][0]['mesh']['indices'][0][0] = 1
        with self.assertRaisesRegex(ValueError, 'bone index'):
            S.validate(self.data)

    def test_missing_bindpose(self):
        self.data['parts'][0]['mesh']['bindposes'] = []
        with self.assertRaisesRegex(ValueError, 'bind pose'):
            S.validate(self.data)

    def test_parent_cycle(self):
        self.data['nodes'][0]['parent'] = 0
        with self.assertRaisesRegex(ValueError, 'hierarchy'):
            S.validate(self.data)

    def test_short_clip(self):
        self.data['clips'][0]['tracks'][0]['values'].pop()
        with self.assertRaisesRegex(ValueError, 'sample count'):
            S.validate(self.data)

    def test_unknown_track(self):
        self.data['clips'][0]['tracks'][0]['node'] = 1
        with self.assertRaisesRegex(ValueError, 'track'):
            S.validate(self.data)

    def test_duplicate_track(self):
        self.data['clips'][0]['tracks'] *= 2
        with self.assertRaisesRegex(ValueError, 'duplicate track'):
            S.validate(self.data)

    def test_triangle_outside_mesh(self):
        self.data['parts'][0]['mesh']['triangles'][0][0] = 20
        with self.assertRaisesRegex(ValueError, 'triangle outside'):
            S.validate(self.data)

    def test_nonfinite_animation(self):
        self.data['clips'][0]['tracks'][0]['values'][0] = float('inf')
        with self.assertRaisesRegex(ValueError, 'non-finite'):
            S.validate(self.data)

    @unittest.skipUnless((S.DEFAULT_PACKAGE / 'spectator.json.gz').exists(), 'owner-extracted package unavailable')
    def test_real_package(self):
        data = json.loads(gzip.decompress((S.DEFAULT_PACKAGE / 'spectator.json.gz').read_bytes()))
        S.validate(data)
        self.assertEqual(data['source_id'], 5434)
        self.assertEqual(len(data['parts']), 4)
        self.assertEqual(sum(len(p['mesh']['vertices']) for p in data['parts']), 4333)
        self.assertEqual([c['name'] for c in data['clips']], list(S.CLIPS))
        for clip in data['clips']:
            self.assertEqual({x['path_hash'] for x in clip['absent_optional_tracks']}, {S.ABSENT_OPTIONAL_PATH})
            self.assertEqual({x['kind'] for x in clip['absent_optional_tracks']}, {1, 2, 3})
            self.assertFalse(clip['root_motion'])
            self.assertTrue(any(max(t['values']) - min(t['values']) > .1 for t in clip['tracks']))
        S.verify(S.DEFAULT_PACKAGE)


if __name__ == '__main__':
    unittest.main()
