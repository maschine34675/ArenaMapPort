"""Autoservice's native acoustics (PLAN-AUTOSERVICE item 10, 2026-11-11): ``extract_spatial_audio.py --map arena_autoservice`` reads the
0.6.2 Sound scene (level112, plan role ``sound``) with the strict checks of every map: no mirrored trigger volume, no reviewed bake
repair, no table row. This module pins the result so that an Arena update or a change of the exporter turns it red.

The synthetic controls run everywhere; the package tests read the ignored export (ARENAMAPPORT_SPATIAL_AUDIO_DIR, else
work/extract/spatial-audio of this checkout) and skip without it; the live tests re-export from the installed Arena
(ARENAMAPPORT_ARENA_DATA, else the default install) into a temporary folder and skip without it.
"""
import hashlib
import json
import os
import struct
import sys
import tempfile
import unittest
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/extract'))
sys.path.insert(0, str(ROOT / 'tools/tests'))
import extract_spatial_audio as S  # noqa: E402
import room_reverb  # noqa: E402
import spatial_audio_bake as B  # noqa: E402

KEY = 'arena_autoservice'
ARENA_SOURCE = Path(os.environ.get('ARENAMAPPORT_ARENA_DATA') or str(S.ROOT))
ORIGINAL_BAKE = ARENA_SOURCE / 'StreamingAssets/AudioBakeData/arena_autoservice_sound.audiobakedata'
TONE = 'amb_arena_autoservice_winter_night_indoor'

# (native room_id, path below SpatialAudioSystem/, EAudioRoomTypeMask, outdoor flag, W, H, D, trigger areas): the full AABB size in metres
# (twice the bounds extents). Types: 0x800 = 2048, 0x200 = 512 (IndoorMetalThin), 0x4000 = 16384, 0x2000 = 8192, 0x1 = OutdoorGeneric.
ROOMS = (
    (1, 'IndoorResp2', 2048, False, 11.029, 5.758, 39.078, 1),
    (2, 'AudioRoom_4', 512, False, 6.629, 3.341, 6.234, 1),
    (3, 'AudioRoom_2', 16384, False, 8.756, 2.813, 7.206, 1),
    (4, 'AudioRoom_1', 8192, False, 9.087, 3.096, 6.150, 1),
    (5, 'MainRoom', 2048, False, 57.460, 22.744, 46.672, 3),
    (6, 'OutdoorResp1', 1, True, 99.371, 14.734, 51.754, 1),
    (7, 'AudioRoom_3', 512, False, 22.703, 2.903, 2.926, 1),
)

# The mixer parameters the compatibility table pins: reverb 13 + delay 5 + effects sends 8 = 26 (effects volumes: none).
MIXER_LAYOUT = {'reverb': ('ReverbMixerDataSO', 13), 'delay': ('DelayMixerDataSO', 5),
                'effects_sends': ('GenericVolumeParamsMixerDataSO', 8), 'effects_volumes': ('GenericVolumeParamsMixerDataSO', 0)}


def spatial_base():
    override = os.environ.get('ARENAMAPPORT_SPATIAL_AUDIO_DIR')
    base = Path(override) if override else ROOT / 'work/extract/spatial-audio'
    return base if (base / KEY / 'spatial-audio.json').is_file() else None


class ExporterRowsTests(unittest.TestCase):
    """No per-map row is needed: the tables that hold the reviewed exceptions of other maps stay without Autoservice."""

    def test_the_map_reads_its_sound_scene_from_the_plan_role(self):
        self.assertNotIn(KEY, S.LEGACY_SOUND_SCENES)
        self.assertEqual(S.scene_for_map(KEY), 'level112')
        self.assertEqual(S.plan_path(KEY).name, 'autoservice.json')

    def test_no_reviewed_repair_and_no_mirrored_volume_row(self):
        self.assertNotIn(KEY, S.REVIEWED_BAKE_REPAIRS)
        self.assertNotIn(KEY, S.REVIEWED_MIRRORED_VOLUMES)
        self.assertEqual(set(S.REVIEWED_BAKE_REPAIRS), {'arena_iceberg'})
        self.assertEqual(S.REVIEWED_MIRRORED_VOLUMES, {'arena_iceberg': 10})
        self.assertEqual(S.DEFAULT_MAPS, ('arena_equator', 'arena_bay5'))  # a bare run still exports only the two accepted maps

    def test_a_failing_route_check_is_not_repaired_for_this_map(self):
        # parse_or_repair_bake re-raises RouteGraphError for a map without a rule: hand it a graph whose portal does not touch the route.
        from test_spatial_audio import bake, graph  # noqa: E402
        d = graph()
        d['map_key'] = KEY
        self.assertEqual(S.parse_or_repair_bake(KEY, bake(), d)[2], None)
        d['portals'][0]['connected_room_ids'] = [101, 101]
        with self.assertRaises(ValueError):
            S.parse_or_repair_bake(KEY, bake(), d)


@unittest.skipUnless(spatial_base() is not None, 'source-derived packages are local only')
class AutoservicePackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = spatial_base() / KEY
        cls.data = json.loads((cls.dir / 'spatial-audio.json').read_text(encoding='utf-8'))
        cls.bake = (cls.dir / 'source.audiobakedata').read_bytes()

    def test_the_package_has_the_documented_shape(self):
        d = self.data
        self.assertEqual((d['map_key'], d['schema_version'], d['source_scene']), (KEY, 1, 'level112'))
        self.assertEqual((len(d['rooms']), len(d['portals']), len(d['areas']), len(d['colliders']), len(d['nodes'])), (7, 16, 9, 25, 34))
        self.assertEqual((len(d['groups']), len(d['systems']), len(d['clips'])), (1, 1, 1))
        self.assertEqual(sorted(p.name for p in self.dir.iterdir()),
                         ['sharedassets112.assets_29.wav', 'source.audiobakedata', 'spatial-audio.json'])
        self.assertNotIn('bake_repair', d)

    def test_the_graph_passes_the_strict_validator_without_a_mirrored_volume(self):
        S.validate_graph(self.data)
        self.assertFalse([n for n in self.data['nodes'] if any(v < 0 for v in n['scale'].values())])
        # mirrored_volume_nodes names the nodes that MAY carry a sign (the area nodes); none of them does here
        self.assertEqual(S.mirrored_volume_nodes(self.data) & {n['id'] for n in self.data['nodes'] if any(v < 0 for v in n['scale'].values())}, set())

    def test_the_room_table(self):
        rooms = {r['room_id']: r for r in self.data['rooms']}
        self.assertEqual(sorted(rooms), [1, 2, 3, 4, 5, 6, 7])
        for room_id, path, kind, outdoor, w, h, d, areas in ROOMS:
            with self.subTest(room=room_id):
                r = rooms[room_id]
                self.assertEqual(r['path'], 'SpatialAudioSystem/' + path)
                self.assertEqual((r['type'], r['outdoor'], r['isolated']), (kind, outdoor, False))
                self.assertTrue(r['enabled'] and r['active_in_hierarchy'])
                self.assertEqual(len(r['area_ids']), areas)
                for got, want in zip((e * 2 for e in r['bounds']['extents']), (w, h, d)):
                    self.assertAlmostEqual(got, want, places=3)
                self.assertIsNone(r['ambient_data']['season_sound_preset'])

    def test_sixteen_doorways_join_seven_rooms_reciprocally(self):
        ids = {r['id']: r['room_id'] for r in self.data['rooms']}
        links = B.portal_room_links(self.data)
        self.assertEqual(sorted(links), list(range(1, 17)))
        self.assertTrue(all(len(set(v)) == 2 for v in links.values()))
        self.assertTrue(all(p['portal_type'] == 1 and p['state'] == 0 for p in self.data['portals']))  # all Door-type portals, open at load
        self.assertEqual(set(x for v in links.values() for x in v), set(ids.values()))
        # every room lists the portals that touch it, from both sides
        by_native = {r['room_id']: r for r in self.data['rooms']}
        for pid, (a, b) in links.items():
            for here, there in ((a, b), (b, a)):
                listed = [pid2 for c in by_native[here]['connections'] for pid2 in c['portal_ids']]
                self.assertEqual(sum(1 for p in self.data['portals'] if p['portal_id'] == pid and p['id'] in listed), 1, (pid, here, there))

    def test_the_bake_is_arenas_own_file_and_passes_the_strict_check(self):
        stats = B.parse_bake(self.bake, self.data)
        self.assertEqual({k: v for k, v in self.data['bake'].items() if k != 'file'}, stats)
        self.assertEqual((stats['bytes'], stats['room_pairs_count'], stats['routes_count'], stats['routes_aware_rooms_count']), (4465, 21, 139, 6))
        self.assertEqual((stats['max_routes_count'], stats['max_route_portals_count']), (19, 54))
        self.assertEqual(stats['room_ids'], [1, 2, 3, 4, 5, 6, 7])
        self.assertEqual(stats['portal_ids'], list(range(1, 17)))
        self.assertEqual(stats['bake_settings'], {'max_depth_indoor_to_indoor': 8, 'max_depth_indoor_to_outdoor': 8,
                                                  'max_depth_outdoor_to_outdoor': 8, 'max_distance_outdoor_to_outdoor': 500,
                                                  'max_distance_indoor_to_outdoor': 300, 'max_distance_indoor_to_indoor': 500})
        self.assertEqual(stats['sha256'], '8f32eb9fc0c021fcc7623e190390e23a7af36e97f5ba5b345a2c5afaae17d80f')
        self.assertEqual(self.data['bake']['file'], 'source.audiobakedata')

    def test_every_route_walks_the_graph_and_no_route_is_listed_twice(self):
        links = B.portal_room_links(self.data)
        routes = B.scan_routes(self.bake)
        self.assertEqual(len(routes), 139)
        self.assertTrue(all(B.route_follows_graph(links, a, b, ids) for _, a, b, _, _, ids in routes))
        pairs = {}
        for key, a, b, _, _, ids in routes:
            self.assertLess(a, b)
            pairs.setdefault(key, []).append(tuple(ids))
        self.assertEqual(len(pairs), 21)
        self.assertTrue(all(len(set(v)) == len(v) for v in pairs.values()))  # the invariant SPT-SEB's checker adds (it flags Iceberg's repair)
        self.assertEqual(max(len(v) for v in pairs.values()), 19)
        self.assertEqual(max(sum(len(r) for r in v) for v in pairs.values()), 54)
        self.assertEqual(len({(a, b) for _, a, b, _, _, _ in routes}), 21)  # one entry per pair of rooms

    def test_the_location_info_of_the_scene_agrees_with_the_bake(self):
        info = self.data['systems'][0]['location_info']
        self.assertEqual((info['rooms_count'], info['portals_count'], info['room_pairs_count'], info['routes_count']), (7, 16, 21, 139))
        self.assertEqual((info['max_routes_count'], info['max_route_portals_count']), (19, 54))
        self.assertEqual(info['name'], 'arena_autoservice_sound_info')
        self.assertEqual(info['relative_bake_data_path'].replace('\\', '/'), 'AudioBakeData/arena_autoservice_sound.audiobakedata')

    def test_one_room_tone_serves_every_room_in_every_season(self):
        # The only indoor tone of the scene is named for winter; no room carries a season preset, so the tone is the room's, whatever the set.
        clips = self.data['clips']
        self.assertEqual([c['name'] for c in clips], [TONE])
        self.assertEqual({r['ambient_data']['room_tone'] for r in self.data['rooms']}, {clips[0]['id']})
        self.assertEqual({r['ambient_data']['season_sound_preset'] for r in self.data['rooms']}, {None})
        decoded = clips[0]['decoded']
        self.assertEqual((decoded['channels'], decoded['sampleRate'], decoded['frames'], decoded['sampleWidth']), (2, 44100, 705600, 2))
        wav = self.dir / decoded['file']
        self.assertEqual(wav.name, 'sharedassets112.assets_29.wav')
        self.assertEqual(hashlib.sha256(wav.read_bytes()).hexdigest(), decoded['sha256'])
        with wave.open(str(wav)) as w:
            self.assertEqual((w.getnchannels(), w.getframerate(), w.getsampwidth(), w.getnframes()), (2, 44100, 2, 705600))
        self.assertEqual(clips[0]['source_metadata']['m_Length'], 16.0)

    def test_mixer_effects_have_the_26_parameter_layout_and_the_category_map(self):
        me = self.data['mixer_effects']
        room_reverb.validate(me, self.data)
        self.assertEqual({k: (v['class'], len(v['parameters'])) for k, v in me['handlers'].items()}, MIXER_LAYOUT)
        self.assertEqual(sum(len(v['parameters']) for v in me['handlers'].values()), 26)
        self.assertEqual((me['map_key'], me['source_scene'], me['effects_name']), (KEY, 'level112', 'CommonMixerParamsData'))
        for handler in me['handlers'].values():
            for p in handler['parameters']:
                self.assertEqual(len(p['values']), 3)
        # Detailed room types fold into the three mixer categories by the isolated and outdoor FLAGS: four indoor types -> 4, OutdoorGeneric -> 67.
        self.assertEqual(room_reverb.room_categories(self.data), {67: 67, 2048: 4, 512: 4, 16384: 4, 8192: 4, 1: 67})


@unittest.skipUnless(ORIGINAL_BAKE.is_file() and (ARENA_SOURCE / 'level112').is_file(), 'Arena source not reachable from this machine')
class AutoserviceLiveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.data = S.export(str(ARENA_SOURCE), cls.tmp.name, KEY)
        cls.out = Path(cls.tmp.name)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_the_export_ships_arenas_bake_unchanged(self):
        self.assertEqual((self.out / 'source.audiobakedata').read_bytes(), ORIGINAL_BAKE.read_bytes())
        self.assertNotIn('bake_repair', self.data)
        stats = B.parse_bake(ORIGINAL_BAKE.read_bytes(), self.data)  # the strict check accepts Arena's file as it is
        self.assertEqual((stats['bytes'], stats['room_pairs_count'], stats['routes_count']), (4465, 21, 139))

    def test_the_export_equals_the_package_when_it_is_present(self):
        base = spatial_base()
        if base is None:
            self.skipTest('no package')
        for name in ('source.audiobakedata', 'sharedassets112.assets_29.wav'):
            self.assertEqual((self.out / name).read_bytes(), (base / KEY / name).read_bytes(), name)
        self.assertEqual(json.loads((self.out / 'spatial-audio.json').read_text(encoding='utf-8')),
                         json.loads((base / KEY / 'spatial-audio.json').read_text(encoding='utf-8')))

    def test_the_export_is_deterministic(self):
        with tempfile.TemporaryDirectory() as second:
            S.export(str(ARENA_SOURCE), second, KEY)
            for name in ('spatial-audio.json', 'source.audiobakedata', 'sharedassets112.assets_29.wav'):
                self.assertEqual((Path(second) / name).read_bytes(), (self.out / name).read_bytes(), name)

    def test_the_plan_mapping_still_matches_the_installed_build(self):
        from source_plan import load_plan, read_build_scenes, verify_scene_mapping
        verify_scene_mapping(load_plan(S.plan_path(KEY)), read_build_scenes(ARENA_SOURCE), ARENA_SOURCE)


if __name__ == '__main__':
    unittest.main()
