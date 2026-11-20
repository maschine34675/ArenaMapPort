"""Yard's native acoustics (PLAN-YARD item 12, 2026-11-20): ``extract_spatial_audio.py --map arena_yard`` reads the 0.6.2 Sound scene
(level163, plan role ``sound``) with the strict checks of every map: no mirrored trigger volume, no reviewed bake repair, no table row.
Yard is the first map with TWO room-tone clips (indoor rooms play Bowl's indoor tone, outdoor rooms ``amb_residential``) and the first whose
graph has an inactive portal that shares its ID with an active one (ID 3). This module pins the result so that an Arena update or a change
of the exporter turns it red.

The synthetic controls run everywhere; the package tests read the ignored export (ARENAMAPPORT_SPATIAL_AUDIO_DIR, else
work/extract/spatial-audio of this checkout) and skip without it; the live tests re-export from the installed Arena
(ARENAMAPPORT_ARENA_DATA, else the default install) into a temporary folder and skip without it. Yard is Catalog-only: nothing here
reads dist/ or the client's release lists.
"""
import json
import os
import re
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

KEY = 'arena_yard'
ARENA_SOURCE = Path(os.environ.get('ARENAMAPPORT_ARENA_DATA') or str(S.ROOT))
ORIGINAL_BAKE = ARENA_SOURCE / 'StreamingAssets/AudioBakeData/arena_yard_sound.audiobakedata'
INDOOR_TONE = ('amb_bowl_arena_indoor', 'sharedassets111.assets:40', 'sharedassets111.assets_40.wav')
OUTDOOR_TONE = ('amb_residential', 'sharedassets163.assets:17', 'sharedassets163.assets_17.wav')

# EAudioRoomTypeMask (EFT 0.16.9.5): OutdoorGeneric 0x1, OutdoorCommon 0x40, IndoorWoodThick 0x400, IndoorBrick 0x800, IndoorConcrete 0x1000.
# (native room_id, path below SpatialAudioSystem/, type mask, outdoor flag, W, H, D, trigger areas): the full AABB size in metres.
ROOMS = (
    (1, 'Outdoor/Rooms/Outdoor_Resp_1', 64, True, 69.113, 32.521, 46.628, 1),
    (2, 'Indoor/tonnel_1 (1)', 4096, False, 9.255, 3.079, 4.784, 1),
    (3, 'Indoor/Resp_1/Room_3', 2048, False, 6.370, 4.064, 10.436, 1),
    (4, 'Indoor/Side/Room_2', 4096, False, 10.205, 6.247, 8.933, 2),
    (5, 'Indoor/Garage_2', 1024, False, 3.628, 3.079, 5.690, 1),
    (6, 'Outdoor/Rooms/Outdoor_Mid', 64, True, 69.113, 32.521, 48.884, 1),
    (7, 'Indoor/tonnel_1 (3)', 4096, False, 4.784, 3.265, 11.733, 1),
    (8, 'Indoor/Kishka_to_Mid', 2048, False, 8.636, 3.486, 7.973, 1),
    (9, 'Indoor/Resp_1/Room_1', 2048, False, 11.322, 3.776, 5.374, 1),
    (10, 'Indoor/Side/Room_1', 4096, False, 9.621, 6.247, 9.047, 3),
    (11, 'Indoor/tonnel_1', 4096, False, 10.031, 3.079, 10.031, 1),
    (12, 'Indoor/Garage_1', 1024, False, 4.050, 2.821, 6.395, 1),
    (13, 'Outdoor/Rooms/Outdoor_Kishka', 1, True, 56.050, 32.521, 85.387, 1),
    (14, 'Indoor/Side/Room_3', 4096, False, 3.126, 6.247, 2.731, 1),
    (15, 'Outdoor/Rooms/Outdoor_Resp_2', 1, True, 17.624, 23.891, 24.920, 1),
    (16, 'Indoor/Resp_1/Room_2', 2048, False, 7.349, 3.505, 8.617, 1),
    (17, 'Outdoor/Rooms/Outdoor_Resp_2_Out', 64, True, 69.113, 33.007, 32.158, 1),
    (18, 'Indoor/tonnel_1 (4)', 4096, False, 4.784, 4.426, 14.460, 1),
    (19, 'Indoor/tonnel_1 (2)', 4096, False, 9.781, 3.079, 9.781, 1),
)

# active portal id -> the two native room ids it joins (sorted; all Door-type portals, open at load)
PORTALS = {
    1: (1, 12), 2: (11, 17), 3: (11, 13), 4: (4, 6), 5: (1, 3), 6: (6, 7), 7: (15, 18), 8: (2, 13), 9: (17, 18), 10: (7, 17),
    11: (8, 13), 12: (5, 6), 13: (4, 10), 14: (6, 10), 15: (4, 14), 16: (3, 16), 17: (1, 19), 18: (1, 6), 19: (5, 6), 20: (2, 6),
    21: (1, 12), 22: (6, 17), 23: (6, 8), 24: (9, 16), 25: (13, 19), 26: (10, 14),
}

MIXER_LAYOUT = {'reverb': ('ReverbMixerDataSO', 13), 'delay': ('DelayMixerDataSO', 5),
                'effects_sends': ('GenericVolumeParamsMixerDataSO', 8), 'effects_volumes': ('GenericVolumeParamsMixerDataSO', 0)}

# The gunshot reverb's class rule (RoomShotAcousticsModel.Classify), mirrored here and pinned against the C# constants below.
HALL_MIN_FLOOR_EDGE = 40.0
METAL_THICK_MIN_FLOOR_EDGE = 15.0
OUTDOOR_BITS, METAL_THIN, METAL_THICK = 0x43, 0x200, 0x10000


def shot_class(type_mask, outdoor, isolated, width, depth):
    if not isolated and (outdoor or type_mask & OUTDOOR_BITS):
        return 'Outdoor'
    if type_mask & METAL_THIN:
        return 'Metal'
    if type_mask & METAL_THICK and max(width, depth) >= METAL_THICK_MIN_FLOOR_EDGE:
        return 'Metal'
    if max(width, depth) >= HALL_MIN_FLOOR_EDGE:
        return 'Hall'
    return 'Shop'


def spatial_base():
    override = os.environ.get('ARENAMAPPORT_SPATIAL_AUDIO_DIR')
    base = Path(override) if override else ROOT / 'work/extract/spatial-audio'
    return base if (base / KEY / 'spatial-audio.json').is_file() else None


class ExporterRowsTests(unittest.TestCase):
    """No per-map row is needed: the tables that hold the reviewed exceptions of other maps stay without Yard."""

    def test_the_map_reads_its_sound_scene_from_the_plan_role(self):
        self.assertNotIn(KEY, S.LEGACY_SOUND_SCENES)
        self.assertEqual(S.scene_for_map(KEY), 'level163')
        self.assertEqual(S.plan_path(KEY).name, 'yard.json')

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

    def test_the_shot_class_mirror_matches_the_client_constants(self):
        source = (ROOT / 'ArenaMapPort.Client/RoomShotAcousticsModel.cs').read_text(encoding='utf-8')
        self.assertIn('internal const float HallMinFloorEdge = 40f;', source)
        self.assertIn('internal const float MetalThickBodyMinFloorEdge = 15f;', source)
        self.assertIn('OutdoorBits = OutdoorGeneric | OutdoorVehicle | OutdoorCommon; // EAudioRoomTypeMask.Outdoor = 0x43', source)
        self.assertIn('internal const int IndoorMetalThin = 0x200;', source)
        self.assertIn('internal const int IndoorMetalThick = 0x10000;', source)
        self.assertRegex(source, re.escape('internal const int OutdoorGeneric = 0x1, OutdoorVehicle = 0x2, OutdoorCommon = 0x40;'))


class ShotClassTests(unittest.TestCase):
    """Item 12, question 6: every detailed type Yard uses has a class (Yard's table needs no new row)."""

    def test_yard_rooms_classify_five_outdoor_and_fourteen_shop(self):
        classes = {}
        for room_id, _, kind, outdoor, w, _, d, _ in ROOMS:
            classes[room_id] = shot_class(kind, outdoor, False, w, d)
        self.assertEqual(sum(1 for c in classes.values() if c == 'Outdoor'), 5)
        self.assertEqual(sum(1 for c in classes.values() if c == 'Shop'), 14)
        self.assertEqual({c for c in classes.values()} - {'Outdoor', 'Shop'}, set())  # no Hall, no Metal
        self.assertEqual({r[0] for r in ROOMS if classes[r[0]] == 'Outdoor'}, {1, 6, 13, 15, 17})

    def test_the_type_to_class_table(self):
        by_type = {}
        for _, _, kind, outdoor, w, _, d, _ in ROOMS:
            by_type.setdefault(kind, set()).add(shot_class(kind, outdoor, False, w, d))
        # 0x40 OutdoorCommon (3 rooms) and 0x1 OutdoorGeneric (2): Outdoor; 0x1000 IndoorConcrete (8), 0x800 IndoorBrick (4) and
        # 0x400 IndoorWoodThick (2): Shop. The type mask only matters for the outdoor bits and the two metal bits, which Yard has none of.
        self.assertEqual(by_type, {64: {'Outdoor'}, 1: {'Outdoor'}, 4096: {'Shop'}, 2048: {'Shop'}, 1024: {'Shop'}})
        self.assertEqual({k: sum(1 for r in ROOMS if r[2] == k) for k in by_type}, {64: 3, 1: 2, 4096: 8, 2048: 4, 1024: 2})

    def test_the_largest_indoor_floor_edge_is_far_below_the_hall_threshold(self):
        edge = max(max(w, d) for _, _, kind, outdoor, w, _, d, _ in ROOMS if not outdoor)
        self.assertAlmostEqual(edge, 14.460, places=3)
        self.assertLess(edge, HALL_MIN_FLOOR_EDGE / 2)
        self.assertLess(edge, METAL_THICK_MIN_FLOOR_EDGE)


@unittest.skipUnless(spatial_base() is not None, 'source-derived packages are local only')
class YardPackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = spatial_base() / KEY
        cls.data = json.loads((cls.dir / 'spatial-audio.json').read_text(encoding='utf-8'))
        cls.bake = (cls.dir / 'source.audiobakedata').read_bytes()

    def test_the_package_has_the_documented_shape(self):
        d = self.data
        self.assertEqual((d['map_key'], d['schema_version'], d['source_scene']), (KEY, 1, 'level163'))
        self.assertEqual((len(d['rooms']), len(d['portals']), len(d['areas']), len(d['colliders']), len(d['nodes'])), (19, 27, 22, 49, 76))
        self.assertEqual((len(d['groups']), len(d['systems']), len(d['clips'])), (1, 1, 2))
        self.assertEqual(sorted(p.name for p in self.dir.iterdir()), [INDOOR_TONE[2], OUTDOOR_TONE[2], 'source.audiobakedata', 'spatial-audio.json'])
        self.assertNotIn('bake_repair', d)

    def test_the_graph_passes_the_strict_validator_without_a_mirrored_volume(self):
        S.validate_graph(self.data)
        S.check_reviewed_mirrored_volumes(self.data)
        self.assertFalse([n for n in self.data['nodes'] if any(v < 0 for v in n['scale'].values())])

    def test_the_room_table(self):
        rooms = {r['room_id']: r for r in self.data['rooms']}
        self.assertEqual(sorted(rooms), list(range(1, 20)))
        for room_id, path, kind, outdoor, w, h, d, areas in ROOMS:
            with self.subTest(room=room_id):
                r = rooms[room_id]
                self.assertEqual(r['path'], 'SpatialAudioSystem/' + path)
                self.assertEqual((r['type'], r['outdoor'], r['isolated']), (kind, outdoor, False))
                self.assertTrue(r['enabled'] and r['active_in_hierarchy'])
                self.assertEqual(len(r['area_ids']), areas)
                for got, want in zip((e * 2 for e in r['bounds']['extents']), (w, h, d)):
                    self.assertAlmostEqual(got, want, places=3)
                self.assertIsNone(r['ambient_data']['season_sound_preset'])  # one tone for every season of the summer set

    def test_twenty_six_active_doorways_join_the_rooms_and_one_inactive_twin_shares_id_3(self):
        by_native = {r['room_id']: r for r in self.data['rooms']}
        links = B.portal_room_links(self.data)
        self.assertEqual(sorted(links), list(range(1, 27)))
        self.assertEqual({pid: tuple(sorted(v)) for pid, v in links.items()}, {pid: tuple(sorted(v)) for pid, v in PORTALS.items()})
        self.assertTrue(all(len(set(v)) == 2 for v in links.values()))
        self.assertTrue(all(p['portal_type'] == 1 and p['state'] == 0 for p in self.data['portals']))  # Door type, open at load
        # every active portal is listed from both rooms it joins
        for pid, (a, b) in links.items():
            for here in (a, b):
                listed = [pid2 for c in by_native[here]['connections'] for pid2 in c['portal_ids']]
                self.assertEqual(sum(1 for p in self.data['portals'] if p['portal_id'] == pid and p['enabled'] and p['active_in_hierarchy'] and p['id'] in listed), 1, (pid, here))
        # the twin: portal ID 3 appears twice, the inactive one (disabled in the hierarchy, no rooms) is the "_static (1)" object
        threes = [p for p in self.data['portals'] if p['portal_id'] == 3]
        self.assertEqual(len(threes), 2)
        active = [p for p in threes if p['enabled'] and p['active_in_hierarchy']]
        dead = [p for p in threes if not (p['enabled'] and p['active_in_hierarchy'])]
        self.assertEqual((len(active), len(dead)), (1, 1))
        self.assertTrue(active[0]['path'].endswith('AudioPortal_FROM_tonnel_1_TO_Outdoor_Kishka'))
        self.assertTrue(dead[0]['path'].endswith('AudioPortal_FROM_Outdoor_Resp_1_TO_Room_3_static (1)'))
        self.assertEqual(dead[0]['connected_room_ids'], [])
        self.assertEqual(sum(1 for p in self.data['portals'] if p['enabled'] and p['active_in_hierarchy']), 26)

    def test_the_bake_is_arenas_own_file_and_passes_the_strict_check(self):
        stats = B.parse_bake(self.bake, self.data)
        self.assertEqual({k: v for k, v in self.data['bake'].items() if k != 'file'}, stats)
        self.assertEqual((stats['bytes'], stats['room_pairs_count'], stats['routes_count'], stats['routes_aware_rooms_count']), (19913, 171, 483, 18))
        self.assertEqual((stats['max_routes_count'], stats['max_route_portals_count']), (8, 37))
        self.assertEqual(stats['room_ids'], list(range(1, 20)))
        self.assertEqual(stats['portal_ids'], list(range(1, 27)))
        self.assertEqual(stats['bake_settings'], {'max_depth_indoor_to_indoor': 8, 'max_depth_indoor_to_outdoor': 8,
                                                  'max_depth_outdoor_to_outdoor': 8, 'max_distance_outdoor_to_outdoor': 500,
                                                  'max_distance_indoor_to_outdoor': 300, 'max_distance_indoor_to_indoor': 500})
        self.assertEqual(len(stats['sha256']), 64)  # no digest of an Arena file in a tracked test (AGENTS.md); the counts above pin the bake
        self.assertEqual(self.data['bake']['file'], 'source.audiobakedata')

    def test_every_route_walks_the_graph_and_no_route_is_listed_twice(self):
        links = B.portal_room_links(self.data)
        routes = B.scan_routes(self.bake)
        self.assertEqual(len(routes), 483)
        self.assertTrue(all(B.route_follows_graph(links, a, b, ids) for _, a, b, _, _, ids in routes))
        pairs = {}
        for key, a, b, _, _, ids in routes:
            self.assertLess(a, b)
            pairs.setdefault(key, []).append(tuple(ids))
        self.assertEqual(len(pairs), 171)
        self.assertEqual(len({(a, b) for _, a, b, _, _, _ in routes}), 171)  # one entry per pair of rooms (19 * 18 / 2)
        self.assertTrue(all(len(set(v)) == len(v) for v in pairs.values()))  # the invariant SPT-SEB's checker adds (it flags Iceberg's repair)
        self.assertEqual(max(len(v) for v in pairs.values()), 8)
        self.assertEqual(max(sum(len(r) for r in v) for v in pairs.values()), 37)

    def test_portal_id_3_in_the_bake_means_the_active_tonnel_doorway(self):
        # Iceberg's repair rule was a duplicated portal ID the bake used for a second doorway. Here the bake uses ID 3 only where it joins
        # rooms 11 and 13 (the active portal); it never crosses the rooms the inactive twin names (1 and 3, which portal 5 joins).
        links = B.portal_room_links(self.data)
        self.assertEqual(tuple(sorted(links[3])), (11, 13))
        uses = 0
        for _, a, b, _, _, ids in B.scan_routes(self.bake):
            here = a
            for pid in ids:
                one, two = links[pid]
                nxt = two if here == one else one
                if pid == 3:
                    uses += 1
                    self.assertEqual({here, nxt}, {11, 13})
                here = nxt
        self.assertEqual(uses, 32)
        direct = [ids for _, a, b, _, _, ids in B.scan_routes(self.bake) if (a, b) == (1, 3)]
        self.assertEqual(direct, [[5]])

    def test_the_location_info_of_the_scene_agrees_with_the_bake(self):
        info = self.data['systems'][0]['location_info']
        # portals_count is 26 (the active unique IDs), not the 27 objects; rooms 19
        self.assertEqual((info['rooms_count'], info['portals_count'], info['room_pairs_count'], info['routes_count']), (19, 26, 171, 483))
        self.assertEqual((info['max_routes_count'], info['max_route_portals_count']), (8, 37))
        self.assertEqual(info['name'], 'arena_yard_sound_info')
        self.assertEqual(info['relative_bake_data_path'].replace('\\', '/'), 'AudioBakeData/arena_yard_sound.audiobakedata')

    def test_two_room_tones_indoor_rooms_play_bowls_tone_outdoor_rooms_the_residential_loop(self):
        clips = {c['name']: c for c in self.data['clips']}
        self.assertEqual(sorted(clips), sorted([INDOOR_TONE[0], OUTDOOR_TONE[0]]))
        self.assertEqual([c['id'] for c in self.data['clips']], sorted([INDOOR_TONE[1], OUTDOOR_TONE[1]]))  # the exporter sorts the ids
        for name, cid, wav_name in (INDOOR_TONE, OUTDOOR_TONE):
            decoded = clips[name]['decoded']
            self.assertEqual((clips[name]['id'], decoded['file']), (cid, wav_name))
            self.assertEqual((decoded['channels'], decoded['sampleRate'], decoded['sampleWidth']), (2, 44100, 2))
            with wave.open(str(self.dir / wav_name)) as w:
                self.assertEqual((w.getnchannels(), w.getframerate(), w.getsampwidth(), w.getnframes()), (2, 44100, 2, decoded['frames']))
            self.assertEqual((self.dir / wav_name).stat().st_size, decoded['bytes'])
        self.assertEqual((clips[INDOOR_TONE[0]]['decoded']['frames'], clips[OUTDOOR_TONE[0]]['decoded']['frames']), (700070, 206290))
        self.assertEqual(clips[INDOOR_TONE[0]]['source_metadata']['m_Resource']['m_Source'], 'sharedassets111.resource')
        self.assertEqual(clips[OUTDOOR_TONE[0]]['source_metadata']['m_Resource']['m_Source'], 'sharedassets163.resource')
        tone = {r['room_id']: r['ambient_data']['room_tone'] for r in self.data['rooms']}
        outdoor = {r[0] for r in ROOMS if r[3]}
        self.assertEqual({tone[i] for i in outdoor}, {OUTDOOR_TONE[1]})
        self.assertEqual({tone[i] for i in tone if i not in outdoor}, {INDOOR_TONE[1]})
        self.assertEqual({r['ambient_data']['room_tone_volume'] for r in self.data['rooms']}, {0.4000000059604645})
        # the outdoor room of the south team pocket carries a louder outdoor ambient (0.5) than the others (0.1)
        loud = {r['room_id'] for r in self.data['rooms'] if r['ambient_data']['outdoor_ambient_volume'] > 0.2}
        self.assertEqual(loud, {15})

    def test_mixer_effects_have_the_26_parameter_layout_and_the_category_map(self):
        me = self.data['mixer_effects']
        room_reverb.validate(me, self.data)
        self.assertEqual({k: (v['class'], len(v['parameters'])) for k, v in me['handlers'].items()}, MIXER_LAYOUT)
        self.assertEqual(sum(len(v['parameters']) for v in me['handlers'].values()), 26)
        self.assertEqual((me['map_key'], me['source_scene'], me['effects_name']), (KEY, 'level163', 'CommonMixerParamsData'))
        for handler in me['handlers'].values():
            for p in handler['parameters']:
                self.assertEqual(len(p['values']), 3)
        # Detailed types fold into the mixer categories by the isolated and outdoor FLAGS: Concrete, Brick and WoodThick -> 4, both outdoor types -> 67.
        self.assertEqual(room_reverb.room_categories(self.data), {67: 67, 64: 67, 1: 67, 4096: 4, 2048: 4, 1024: 4})

    def test_shot_classes_of_the_package_rooms_match_the_table(self):
        classes = [shot_class(r['type'], r['outdoor'], r['isolated'], *(lambda e: (e[0] * 2, e[2] * 2))(r['bounds']['extents'])) for r in self.data['rooms']]
        self.assertEqual((classes.count('Outdoor'), classes.count('Shop'), classes.count('Hall'), classes.count('Metal')), (5, 14, 0, 0))


@unittest.skipUnless(spatial_base() is not None and ARENA_SOURCE.is_dir(), 'package or Arena source not reachable from this machine')
class RoomToneKeySourceTests(unittest.TestCase):
    """Item 12, question 3: no shipped room-tone WAV of ANY map is a copy of a stage-2 key-source file (tools/bundle/key-sources.json), and no
    clip is read from one. The gate-sound exporter refuses a clip that streams from such a file unless its pitch is baked; the same rule is
    enforced here for the acoustics, which validate_distribution.validate_acoustics does not check."""

    @classmethod
    def setUpClass(cls):
        table = json.loads((ROOT / 'tools/bundle/key-sources.json').read_text(encoding='utf-8'))['maps']
        cls.names = sorted({Path(n).name for entries in table.values() for e in entries for n in e['files']})
        cls.sources = [ARENA_SOURCE / n for n in cls.names if (ARENA_SOURCE / n).is_file()]
        base = spatial_base()
        cls.packages = {p.name: json.loads((p / 'spatial-audio.json').read_text(encoding='utf-8')) for p in sorted(base.iterdir())
                        if (p / 'spatial-audio.json').is_file()}

    def test_the_key_source_files_are_present_and_yard_is_listed(self):
        self.assertTrue(self.sources, 'no key-source file in the Arena folder')
        table = json.loads((ROOT / 'tools/bundle/key-sources.json').read_text(encoding='utf-8'))['maps']
        self.assertIn(KEY, table)

    def test_no_room_tone_streams_from_a_key_source_file(self):
        self.assertIn(KEY, self.packages)
        for key, package in self.packages.items():
            for clip in package['clips']:
                source = Path(clip['source_metadata']['m_Resource']['m_Source']).name
                with self.subTest(map=key, clip=clip['name']):
                    self.assertNotIn(source, self.names)

    def test_no_room_tone_wav_equals_a_key_source_file(self):
        base = spatial_base()
        by_size = {}
        for source in self.sources:
            by_size.setdefault(source.stat().st_size, []).append(source)
        for key, package in self.packages.items():
            for clip in package['clips']:
                wav = base / key / clip['decoded']['file']
                with self.subTest(map=key, clip=clip['name']):
                    self.assertEqual(by_size.get(wav.stat().st_size, []), [])

    def test_yards_indoor_tone_is_byte_identical_to_bowls_but_each_map_stages_its_own_copy(self):
        base = spatial_base()
        if 'arena_bowl' not in self.packages:
            self.skipTest('no Bowl package')
        self.assertEqual((base / KEY / INDOOR_TONE[2]).read_bytes(), (base / 'arena_bowl' / INDOOR_TONE[2]).read_bytes())


@unittest.skipUnless(ORIGINAL_BAKE.is_file() and (ARENA_SOURCE / 'level163').is_file(), 'Arena source not reachable from this machine')
class YardLiveTests(unittest.TestCase):
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
        self.assertEqual((stats['bytes'], stats['room_pairs_count'], stats['routes_count']), (19913, 171, 483))

    def test_the_export_writes_both_room_tones(self):
        self.assertEqual(sorted(p.name for p in self.out.iterdir()), [INDOOR_TONE[2], OUTDOOR_TONE[2], 'source.audiobakedata', 'spatial-audio.json'])

    def test_the_export_equals_the_package_when_it_is_present(self):
        base = spatial_base()
        if base is None:
            self.skipTest('no package')
        for name in ('source.audiobakedata', INDOOR_TONE[2], OUTDOOR_TONE[2]):
            self.assertEqual((self.out / name).read_bytes(), (base / KEY / name).read_bytes(), name)
        self.assertEqual(json.loads((self.out / 'spatial-audio.json').read_text(encoding='utf-8')),
                         json.loads((base / KEY / 'spatial-audio.json').read_text(encoding='utf-8')))

    def test_the_export_is_deterministic(self):
        with tempfile.TemporaryDirectory() as second:
            S.export(str(ARENA_SOURCE), second, KEY)
            for name in ('spatial-audio.json', 'source.audiobakedata', INDOOR_TONE[2], OUTDOOR_TONE[2]):
                self.assertEqual((Path(second) / name).read_bytes(), (self.out / name).read_bytes(), name)

    def test_the_plan_mapping_still_matches_the_installed_build(self):
        from source_plan import load_plan, read_build_scenes, verify_scene_mapping
        verify_scene_mapping(load_plan(S.plan_path(KEY)), read_build_scenes(ARENA_SOURCE), ARENA_SOURCE)


if __name__ == '__main__':
    unittest.main()
