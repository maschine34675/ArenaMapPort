"""Synthetic malformed-graph/bake controls plus optional original-source round trips."""
import copy
import json
from pathlib import Path
import struct
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/extract'))
from extract_spatial_audio import validate_graph
from spatial_audio_bake import parse_bake


def bake():
    p = lambda fmt, *v: struct.pack('<' + fmt, *v)
    return (p('iIIhhiihffiB', 1, 11, 11, 1, 2, 1, 1, 3, .5, 2, 1, 1)
            + p('ih iIh iI', 2, 1, 1, 11, 2, 1, 11)
            + p('iIi hfHH', 1, 11, 1, 1, .5, 0, 0)
            + p('6iII', 3, 3, 3, 100, 100, 100, 1, 1))


def graph():
    vec = lambda x, y, z: dict(x=x, y=y, z=z)
    result = dict(schema_version=1, map_key='arena_equator', nodes=[], colliders=[],
                  areas=[], rooms=[], portals=[], groups=[], systems=[])
    for n in range(1, 5):
        result['nodes'].append(dict(id=n, parent_id=None, name='synthetic' + str(n),
            active_self=True, layer=13, position=vec((n - 1) * 10, 0, 0),
            rotation=dict(x=0, y=0, z=0, w=1), scale=vec(1, 1, 1)))
    for n in range(1, 4):
        result['colliders'].append(dict(id=10+n, node_id=n, enabled=True, is_trigger=True,
            center=vec(0, 0, 0), size=vec(2, 2, 0 if n == 3 else 2)))
    for n in range(1, 3):
        result['areas'].append(dict(id=20+n, node_id=n, enabled=True, active_in_hierarchy=True, collider_id=10+n))
        result['rooms'].append(dict(id=100+n, room_id=n, node_id=n, enabled=True,
            active_in_hierarchy=True, area_ids=[20+n], wall_occlusion=.5,
            source_area_obbs=[dict(center=[(n-1)*10, 0, 0], extents=[1, 1, 1])],
            connections=[dict(room_id=103-n, portal_ids=[30])],
            ambient_data=dict(season_sound_preset=None)))
    result['portals'] = [dict(id=30, portal_id=3, node_id=3, enabled=True,
        active_in_hierarchy=True, collider_id=13, connected_room_ids=[101, 102],
        portal_type=1, state=0, transmission=1, traversal_max_cost=.5, depth=3)]
    result['groups'] = [dict(id=40, node_id=4, enabled=True, active_in_hierarchy=True,
        room_ids=[101, 102], portal_ids=[30])]
    result['systems'] = [dict(id=41, node_id=4, enabled=True, active_in_hierarchy=True,
        location_info=dict(rooms_count=2, portals_count=1, room_pairs_count=1,
            routes_count=1, max_routes_count=1, max_route_portals_count=1,
            bake_settings=dict(zip(('max_depth_indoor_to_indoor','max_depth_indoor_to_outdoor',
                'max_depth_outdoor_to_outdoor','max_distance_outdoor_to_outdoor',
                'max_distance_indoor_to_outdoor','max_distance_indoor_to_indoor'), (3,3,3,100,100,100)))))]
    return result


class SpatialAudioTests(unittest.TestCase):
    def test_positive_control(self):
        d = graph(); validate_graph(d)
        self.assertEqual(parse_bake(bake(), d)['routes_count'], 1)

    def reject(self, mutation, text):
        d = graph(); mutation(d)
        with self.assertRaisesRegex((ValueError, KeyError), text): validate_graph(d)

    def test_duplicate_component(self):
        self.reject(lambda d: d['areas'].append(d['areas'][0]), 'duplicate component')

    def test_duplicate_active_native_id(self):
        self.reject(lambda d: d['rooms'][1].update(room_id=1), 'duplicate active')

    def test_missing_area(self):
        self.reject(lambda d: d['rooms'][0].update(area_ids=[999]), '999')

    def test_cyclic_hierarchy(self):
        self.reject(lambda d: d['nodes'][0].update(parent_id=1), 'cyclic')

    def test_activation_mismatch(self):
        self.reject(lambda d: d['nodes'][0].update(active_self=False), 'activation')

    def test_nonfinite_transform(self):
        self.reject(lambda d: d['nodes'][0]['position'].update(x=float('nan')), 'nonfinite')

    def test_zero_depth_portal_allowed(self):
        d=graph(); d['nodes'][2]['scale']['z']=0; validate_graph(d)

    def test_zero_depth_room_rejected(self):
        self.reject(lambda d: d['nodes'][0]['scale'].update(z=0), 'OBB')

    def test_wrong_volume_position(self):
        self.reject(lambda d: d['nodes'][0]['position'].update(x=4), 'OBB')

    def test_solid_collider(self):
        self.reject(lambda d: d['colliders'][0].update(is_trigger=False), 'collider')

    def test_wrong_collider_owner(self):
        self.reject(lambda d: d['areas'][0].update(collider_id=12), 'collider reference')

    def test_unwired_portal(self):
        self.reject(lambda d: d['portals'][0].update(connected_room_ids=[101]), 'nonreciprocal')

    def test_group_missing_room(self):
        self.reject(lambda d: d['groups'][0].update(room_ids=[101]), 'group coverage')

    def test_each_truncation_rejected(self):
        raw=bake()
        for end in range(len(raw)):
            with self.subTest(end=end), self.assertRaises(ValueError): parse_bake(raw[:end])

    def test_trailing_bake_bytes(self):
        with self.assertRaisesRegex(ValueError, 'trailing'): parse_bake(bake()+b'\0')

    def test_negative_count(self):
        with self.assertRaisesRegex(ValueError, 'count'): parse_bake(b'\xff'*4+bake()[4:])

    def test_wrong_location_info(self):
        d=graph();d['systems'][0]['location_info']['routes_count']=99
        with self.assertRaisesRegex(ValueError, 'location info'):parse_bake(bake(),d)

    def test_unknown_baked_portal(self):
        d=graph();d['portals'][0]['portal_id']=8
        with self.assertRaisesRegex(ValueError, 'coverage'):parse_bake(bake(),d)

    def test_original_packages_when_present(self):
        base=ROOT/'work/extract/spatial-audio'
        if not base.exists():self.skipTest('source-derived packages are local only')
        for key in ('arena_equator','arena_bay5'):
            with self.subTest(map=key):
                d=json.loads((base/key/'spatial-audio.json').read_text());validate_graph(d)
                self.assertEqual(parse_bake((base/key/'source.audiobakedata').read_bytes(),d),
                    {k:v for k,v in d['bake'].items() if k!='file'})


if __name__=='__main__':unittest.main()
