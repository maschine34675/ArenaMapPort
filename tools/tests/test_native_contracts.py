"""PLAN-AIRPIT item 3: data-driven native contracts (tools/extract/native_contracts/*.json).

Covers the strict loader (native_contract_loader), the golden proof that the Equator and Bay5 data
files reproduce the literals validate_native_features.py used to hard-code, and the validator
behaviour that now follows the data: AirPit's census and audio pins (item 13 filled its audio; the
`audio: null` ballistics-only branch is exercised through a patched copy of the AirPit contract) and
the negative cases (wrong census fails, missing feature fails), the optional ``partial_scenes`` pins of the
furnished-shop scenes (loader schema incl. the exclude / twin-descendant counters and their sum rule, manifest pin
check in both directions) and the real-package tests, which read the ignored work/ packages and are skipped when
those are absent (airpit-native-v7 is an archive that the validators refuse; v8 is the accepted package).
"""
from __future__ import annotations

from collections import Counter
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import native_contract_loader as L
import partial_scope
import validate_native_features as v
from common_ids import HardExtractError

# The pre-item-3 literals of validate_native_features.py (MAPS, SCENES, AUDIO_ROOT), frozen here as the
# golden reference. The data files must reproduce them value for value; do not "fix" this table to
# follow a data change, change the data only together with a reviewed census change.
_BAY5_SCENES = {84: 'Main', 83: 'Light', 81: 'DesignStuff', 89: 'TeamFight', 88: 'TeamFight_DesignStuff',
                80: 'DEPOT', 86: 'WAREHOUSE', 82: 'HANGAR', 79: 'Background'}
LEGACY_MAPS = {
    'arena_bay5': {
        'scenes': {index: 'Arena_Bay5_' + suffix for index, suffix in _BAY5_SCENES.items()},
        'ballistics': {84: 3541, 83: 44, 81: 5, 89: 429, 88: 65, 80: 645, 86: 541, 82: 159, 79: 893},
        'feature': 'harbor_audio', 'schema': 1, 'audio_scene': 'level188', 'audio_root': 'ArenaMapPort_Bay5NativeAudio',
        'players': {'AmbientSoundPlayer': 17, 'SeasonAmbientSoundPlayer': 6, 'LoopAmbientSoundPlayer': 1},
        'support': {'AmbientSoundPlayerGroup': 2, 'BezierSpline': 1, 'AmbientSplineEmitterController': 1,
                    'SoundAmbientZoneCalculator': 1, 'AmbientPlayerSplineMappedEmitter': 1},
        'banks': 13, 'clips': 106,
    },
    'arena_equator': {
        'scenes': {51: 'Arena_Equator', 53: 'Arena_Equator_DesignStuff', 55: 'Arena_Equator_Lighting',
                   57: 'Arena_Equator_TeamFight_DesignStuff', 58: 'Arena_Equator_TeamFight'},
        'ballistics': {51: 2639, 53: 25, 55: 127, 57: 89, 58: 12},
        'feature': 'ambient_audio', 'schema': 2, 'audio_scene': 'level56',
        'audio_root': 'ArenaMapPort_EquatorNativeAudio', 'players': {'AmbientSoundPlayer': 25},
        'support': {'AmbientSoundPlayerGroup': 1}, 'banks': 11, 'clips': 65,
    },
}
# The Equator-only audio branch of load_audio() (literals 23, far_dogs, CommonAmbInEffects, level56:965).
LEGACY_EQUATOR_STATES = {
    'grouped_players': 23, 'disabled_players': [],
    'inactive_players': {'level56:970': 'far_dogs2', 'level56:975': 'far_dogs1'},
    'group_mixers': ['CommonAmbInEffects'], 'loop_players': ['level56:965'],
}


# The five scenes of the accepted airpit-native-v6 package (PLAN-AIRPIT item 2 / 13 dry run, reviewed).
AIRPIT_V6_SCENES = {324: 'Arena_AirPit_ShootOut', 325: 'Arena_AirPit_ShootOut_Light', 40: 'Arena_Airpit_DesignStuff',
                    29: 'Arena_Airpit_TeamFight', 28: 'Arena_Airpit_TeamFight_DesignStuff'}
AIRPIT_V6_BALLISTICS = {324: 2856, 325: 512, 40: 28, 29: 94, 28: 58}
# Furnished shops: the two partial scenes appended after the v6 scenes. The pins are the airpit-native-v8 extraction
# (2026-10-13, Arena 0.6.0.47752; python tools/validate/partial_scope.py <package> for the partial_scenes block). Change
# this table, AIRPIT_SHOP_BALLISTICS / AIRPIT_SHOP_FEATURES, the contract file and nothing else, together with the
# reviewed census. v7 (7118 / 1446 / 85 and 962 / 187 / 26, ballistics 785 / 122, decals 683, culling lights 680 / 139) is
# an archive. v8 = v7 + the rest of the furnished hall: Main_hall 13006 objects = 3547 units (1148 + 104 shop units, the
# decoration box with 123 PROJECTION decal units and 1210 + 1534 + 20 prop/variant units, 28 doors, 632 of the claimed
# units dropped by 208 exclude entries, 4 imported carshadow descendants) and 245 carriers (4 of them twin carriers);
# Light 1538 objects = 298 units (22 imported lamp lights / shadow planes, 9 of them excluded again: the parts
# level325 replaced or disabled on purpose) and 37 carriers (5 twin carriers).
# Ballistics 38: 1562, 37: 172 (total 3548 + 1734 = 5282). Decals: 481 (v6) + 241 = 722, all in Main_hall. Culling lights
# and volumetrics only come from Light (189 / 24 on top of 533 / 130).
# v9 (2026-10-17) = v8 + the three level38 SOO crates under the tarp City_Generic_Tarp_01 (1) at (-31.3, -16.7)
# (reserve_woodBox_medium (9)/(10), cardbox_vinyl as explicit roots; the five same-named SOO siblings elsewhere are
# excluded by position, 213 entries): Main_hall 13035 objects = 3550 units, 246 carriers, ballistics 38: 1566.
AIRPIT_SHOP_SCENES = {38: 'Arena_Airpit_Main_hall', 37: 'Arena_Airpit_Light'}
AIRPIT_SHOP_BALLISTICS = {38: 1566, 37: 172}
AIRPIT_SHOP_FEATURES = {'static_decals': 722, 'culling_light_object_count': 722, 'volumetric_light_count': 154}
AIRPIT_SHOP_PARTIAL = {'Arena_Airpit_Main_hall': {'objects': 13035, 'units': 3550, 'carriers': 246, 'orphans': 4,
                                                  'dropped_script_owners': 0, 'excluded': 637, 'exclude_entries': 213,
                                                  'twin_descendants_added': 4},
                       'Arena_Airpit_Light': {'objects': 1538, 'units': 298, 'carriers': 37, 'orphans': 0,
                                              'dropped_script_owners': 0, 'excluded': 9, 'exclude_entries': 9,
                                              'twin_descendants_added': 22}}
PARTIAL_COUNT_KEYS = ('objects', 'units', 'carriers', 'orphans', 'dropped_script_owners', 'excluded', 'exclude_entries',
                      'twin_descendants_added')
PARTIAL_RULE_KEYS = ('selected', 'twin_skipped', 'excluded', 'descendants_added')
NEW_PARTIAL_KEYS = ('excluded', 'exclude_entries', 'twin_descendants_added')


def flatten(contract: dict) -> dict:
    """The data contract in the shape of the retired MAPS literal (audio maps only)."""
    audio = contract['audio']
    return {'scenes': contract['scenes'], 'ballistics': contract['ballistics'], 'feature': audio['feature'],
            'schema': audio['schema'], 'audio_scene': audio['scene'], 'audio_root': audio['root'],
            'players': audio['players'], 'support': audio['support'], 'banks': audio['banks'], 'clips': audio['clips']}


def raw_contract() -> dict:
    """A small valid schema-2 contract (one scene, two groups) used as the base for schema mutations."""
    return {
        'schema_version': 1, 'map_key': 'arena_x', 'notes': ['fixture'],
        'scenes': {'7': 'Arena_X_Main', '8': 'Arena_X_Light'}, 'ballistics': {'7': 10, '8': 0},
        'require_unsliced': True, 'implicit_scene_identity': False,
        'features': {'static_decals': 3, 'light_culling': {'culling_light_object_count': 4, 'volumetric_light_count': 2,
                                                          'light_flicker_count': 0},
                     'raincover': {'indoor_trigger_count': 1, 'dry_plane_count': 0, 'weather_obstacle': False},
                     'navmesh_modifiers': {'modifiers': 5, 'volumes': 6}, 'small_physics_objects': 0},
        'audio': {'feature': 'ambient_audio', 'schema': 2, 'scene': 'level9', 'root': 'ArenaMapPort_XNativeAudio',
                  'players': {'AmbientSoundPlayer': 6}, 'support': {'AmbientSoundPlayerGroup': 2},
                  'banks': 2, 'clips': 3, 'group_coverage': 'all_players',
                  'states': {'grouped_players': 6, 'disabled_players': [], 'inactive_players': {},
                             'groups': {'CommonAmbientOutGroup': {'mixer': 'CommonAmbOutEffects', 'players': 2},
                                        'CommonAmbientInGroup': {'mixer': 'CommonAmbInEffects', 'players': 4}},
                             'group_mixers': ['CommonAmbOutEffects', 'CommonAmbInEffects'],
                             'loops': {}, 'loop_players': []}},
    }


def parse(raw: dict) -> dict:
    return L.parse_contract(raw, 'x', 'fixture.json')


def rule_counts(selected=0, twin_skipped=0, excluded=0, descendants_added=0) -> dict:
    return {'selected': selected, 'twin_skipped': twin_skipped, 'excluded': excluded,
            'descendants_added': descendants_added}


def partial_block() -> dict:
    """A valid ``partial_scenes`` block for the fixture contract (scene 8 'Arena_X_Light' is partial).

    It uses both newer mechanisms: Shops loses one claimed unit to the exclude list (8 + 0 - 1 = 7 units), Props gains
    one twin descendant (2 + 1 - 0 = 3 units), 7 + 3 = 10 units."""
    return {'Arena_X_Light': {'index': 8, 'objects': 30, 'units': 10, 'carriers': 4, 'orphans': 1,
                              'dropped_script_owners': 0, 'excluded': 1, 'exclude_entries': 1,
                              'twin_descendants_added': 1,
                              'rules': {'roots:Root/Shops': rule_counts(8, 2, excluded=1),
                                        'region_roots:Root/Props': rule_counts(2, 0, descendants_added=1)}}}


class ContractFilesTests(unittest.TestCase):
    def test_repository_contracts_load_and_validator_uses_them(self):
        contracts = L.load_contracts()
        self.assertEqual(set(contracts), {'arena_equator', 'arena_bay5', 'arena_airpit', 'arena_bowl'})
        self.assertEqual(set(v.MAPS), set(contracts))
        for key, contract in contracts.items():
            self.assertEqual(contract['key'], key)
            self.assertEqual(v.MAPS[key], contract)
        files = sorted(p.stem for p in L.CONTRACT_DIR.glob('*.json'))
        self.assertEqual(files, ['airpit', 'bay5', 'bowl', 'equator'])

    def test_load_contract_by_key_fails_for_unknown_map(self):
        self.assertEqual(L.load_contract('arena_airpit')['key'], 'arena_airpit')
        with self.assertRaisesRegex(L.NativeContractError, 'no native contract for map'):
            L.load_contract('arena_prison')

    def test_error_class_is_both_a_hard_extract_error_and_a_value_error(self):
        self.assertTrue(issubclass(L.NativeContractError, HardExtractError))
        self.assertTrue(issubclass(L.NativeContractError, ValueError))

    def test_scene_indices_are_integers_in_file_order(self):
        for contract in L.load_contracts().values():
            self.assertTrue(all(type(i) is int for i in contract['scenes']))
            self.assertEqual(list(contract['scenes']), list(contract['ballistics']))

    def test_partial_scenes_key_is_always_normalised(self):
        for key, contract in L.load_contracts().items():
            with self.subTest(key=key):
                self.assertIn('partial_scenes', contract)
                self.assertEqual(bool(contract['partial_scenes']), key == 'arena_airpit')
                for name, pin in contract['partial_scenes'].items():
                    self.assertEqual(contract['scenes'][pin['index']], name)
                    self.assertTrue(contract['require_unsliced'])


class GoldenLegacyTests(unittest.TestCase):
    """The data files reproduce the retired literals exactly (values and iteration order)."""

    def test_equator_and_bay5_reproduce_the_legacy_maps_table(self):
        for key, legacy in LEGACY_MAPS.items():
            with self.subTest(key=key):
                got = flatten(v.MAPS[key])
                self.assertEqual(got, legacy)
                self.assertEqual(list(got['scenes'].items()), list(legacy['scenes'].items()))
                self.assertEqual(list(got['ballistics'].items()), list(legacy['ballistics'].items()))
                self.assertEqual(sum(got['ballistics'].values()), {'arena_bay5': 6322, 'arena_equator': 2892}[key])

    def test_equator_audio_branch_literals_are_now_data(self):
        states = v.MAPS['arena_equator']['audio']['states']
        self.assertEqual({key: states[key] for key in LEGACY_EQUATOR_STATES}, LEGACY_EQUATOR_STATES)
        self.assertEqual(v.MAPS['arena_equator']['audio']['group_coverage'], 'active_players')

    def test_equator_group_and_loop_pins_extend_the_legacy_literals(self):
        # Added with the item-13 review: the loop is pinned by leaf name too (the importer and the client look it
        # up as 'electricity'), the single indoor group by name, mixer and member count (25 players - 2 dogs).
        states = v.MAPS['arena_equator']['audio']['states']
        self.assertEqual(set(states), set(LEGACY_EQUATOR_STATES) | {'groups', 'loops', 'forced_mixer_players'})
        self.assertEqual(states['forced_mixer_players'], {})  # optional pin, normalised to {} (Bowl pins metal_rigging)
        self.assertEqual(states['loops'], {'level56:965': 'electricity'})
        self.assertEqual(states['groups'], {'CommonAmbientInGroup': {'mixer': 'CommonAmbInEffects', 'players': 23}})

    def test_bay5_has_no_state_pins_and_every_player_is_grouped(self):
        audio = v.MAPS['arena_bay5']['audio']
        self.assertIsNone(audio['states'])
        self.assertEqual(audio['group_coverage'], 'all_players')

    def test_legacy_flags_are_preserved(self):
        self.assertTrue(v.MAPS['arena_equator']['require_unsliced'])
        self.assertFalse(v.MAPS['arena_bay5']['require_unsliced'])
        self.assertTrue(v.MAPS['arena_bay5']['implicit_scene_identity'])
        self.assertFalse(v.MAPS['arena_equator']['implicit_scene_identity'])
        self.assertFalse(v.MAPS['arena_airpit']['implicit_scene_identity'])
        # Equator and Bay5 keep the shape-only validation of the optional entries (Bay5's decal count
        # moved from 499 to 502 with Arena 0.6.0, so a census pin would reject one of the two sources).
        self.assertIsNone(v.MAPS['arena_equator']['features'])
        self.assertIsNone(v.MAPS['arena_bay5']['features'])

    def test_airpit_v6_scenes_keep_the_item_2_dry_run_pins(self):
        airpit = v.MAPS['arena_airpit']
        self.assertEqual({i: n for i, n in airpit['scenes'].items() if i in AIRPIT_V6_SCENES}, AIRPIT_V6_SCENES)
        self.assertEqual({i: n for i, n in airpit['ballistics'].items() if i in AIRPIT_V6_SCENES}, AIRPIT_V6_BALLISTICS)
        self.assertEqual(sum(AIRPIT_V6_BALLISTICS.values()), 3548)
        self.assertEqual(list(airpit['scenes'])[:5], list(AIRPIT_V6_SCENES))  # the shops are appended, never interleaved
        self.assertEqual(airpit['features']['raincover'], {'indoor_trigger_count': 2, 'dry_plane_count': 0,
                                                             'weather_obstacle': False})
        self.assertEqual(airpit['features']['navmesh_modifiers'], {'modifiers': 20, 'volumes': 12})
        self.assertEqual(airpit['features']['small_physics_objects'], 0)
        self.assertEqual(airpit['features']['light_culling']['light_flicker_count'], 0)
        self.assertTrue(airpit['require_unsliced'])

    def test_airpit_shop_scenes_are_appended_with_the_reviewed_pins(self):
        airpit = v.MAPS['arena_airpit']
        self.assertEqual(list(airpit['scenes'])[5:], list(AIRPIT_SHOP_SCENES))
        self.assertEqual({i: n for i, n in airpit['scenes'].items() if i in AIRPIT_SHOP_SCENES}, AIRPIT_SHOP_SCENES)
        self.assertEqual({i: n for i, n in airpit['ballistics'].items() if i in AIRPIT_SHOP_SCENES},
                         AIRPIT_SHOP_BALLISTICS)
        self.assertEqual(sum(airpit['ballistics'].values()), 3548 + sum(AIRPIT_SHOP_BALLISTICS.values()))
        features = airpit['features']
        self.assertEqual((features['static_decals'], features['light_culling']['culling_light_object_count'],
                          features['light_culling']['volumetric_light_count']),
                         (AIRPIT_SHOP_FEATURES['static_decals'], AIRPIT_SHOP_FEATURES['culling_light_object_count'],
                          AIRPIT_SHOP_FEATURES['volumetric_light_count']))
        partial = airpit['partial_scenes']
        self.assertEqual(list(partial), list(AIRPIT_SHOP_SCENES.values()))
        self.assertEqual({name: pin['index'] for name, pin in partial.items()},
                         {name: index for index, name in AIRPIT_SHOP_SCENES.items()})
        self.assertEqual({name: {k: pin[k] for k in PARTIAL_COUNT_KEYS}
                          for name, pin in partial.items()},
                         {name: {k: AIRPIT_SHOP_PARTIAL[name][k] for k in PARTIAL_COUNT_KEYS} for name in AIRPIT_SHOP_PARTIAL})
        for name, pin in partial.items():
            self.assertGreaterEqual(pin['objects'], pin['units'] + pin['carriers'])
            self.assertEqual(list(next(iter(pin['rules'].values()))), list(PARTIAL_RULE_KEYS), name)
            rules = pin['rules'].values()
            self.assertEqual(sum(r['selected'] + r['descendants_added'] - r['excluded'] for r in rules), pin['units'], name)
            self.assertEqual(sum(r['excluded'] for r in rules), pin['excluded'], name)
            self.assertEqual(sum(r['descendants_added'] for r in rules), pin['twin_descendants_added'], name)
            self.assertEqual(pin['exclude_entries'] == 0, pin['excluded'] == 0, name)

    def test_airpit_audio_pins_are_the_item_13_export(self):
        audio = v.MAPS['arena_airpit']['audio']
        self.assertEqual(audio, {
            'feature': 'ambient_audio', 'schema': 2, 'scene': 'level39', 'root': 'ArenaMapPort_AirPitNativeAudio',
            'players': {'AmbientSoundPlayer': 26}, 'support': {'AmbientSoundPlayerGroup': 2},
            'banks': 9, 'clips': 61, 'group_coverage': 'all_players',
            'states': {'grouped_players': 26, 'disabled_players': [], 'inactive_players': {},
                       'groups': {'CommonAmbientOutGroup': {'mixer': 'CommonAmbOutEffects', 'players': 4},
                                  'CommonAmbientInGroup': {'mixer': 'CommonAmbInEffects', 'players': 22}},
                       'group_mixers': ['CommonAmbOutEffects', 'CommonAmbInEffects'],
                       'loops': {}, 'loop_players': [], 'forced_mixer_players': {}}})
        # AirPit is the first map with both an Out and an In group: the Out group comes first in source order
        # (kept in the group_mixers mirror), but the pins themselves are keyed by group name.
        self.assertEqual(audio['states']['group_mixers'][0], 'CommonAmbOutEffects')
        self.assertEqual(sum(audio['players'].values()), audio['states']['grouped_players'])
        self.assertEqual(sum(g['players'] for g in audio['states']['groups'].values()), audio['states']['grouped_players'])


class SchemaTests(unittest.TestCase):
    def test_base_fixture_is_valid_and_normalised(self):
        contract = parse(raw_contract())
        self.assertEqual(contract['scenes'], {7: 'Arena_X_Main', 8: 'Arena_X_Light'})
        self.assertEqual(contract['ballistics'], {7: 10, 8: 0})
        self.assertEqual(contract['audio']['states']['group_mixers'], ['CommonAmbOutEffects', 'CommonAmbInEffects'])
        self.assertEqual(contract['audio']['states']['groups'],
                         {'CommonAmbientOutGroup': {'mixer': 'CommonAmbOutEffects', 'players': 2},
                          'CommonAmbientInGroup': {'mixer': 'CommonAmbInEffects', 'players': 4}})
        self.assertEqual(contract['audio']['states']['loops'], {})

    def test_two_groups_may_share_a_mixer(self):
        raw = raw_contract()
        raw['audio']['states']['group_mixers'] = ['CommonAmbInEffects', 'CommonAmbInEffects']
        for record in raw['audio']['states']['groups'].values():
            record['mixer'] = 'CommonAmbInEffects'
        parse(raw)

    def assertRejects(self, change, pattern=None):
        raw = raw_contract()
        change(raw)
        with self.assertRaises(L.NativeContractError) as caught:
            parse(raw)
        if pattern:
            self.assertRegex(str(caught.exception), pattern)

    def test_unknown_and_missing_keys(self):
        self.assertRejects(lambda r: r.update(bogus=1), 'unknown keys')
        for key in ('schema_version', 'map_key', 'scenes', 'ballistics', 'require_unsliced',
                    'implicit_scene_identity', 'features', 'audio'):
            with self.subTest(key=key):
                self.assertRejects(lambda r, key=key: r.pop(key), 'missing keys')
        self.assertRejects(lambda r: r['audio'].update(extra=1), 'unknown keys')
        self.assertRejects(lambda r: r['audio'].pop('states'), 'missing keys')
        self.assertRejects(lambda r: r['features'].pop('raincover'), 'missing keys')
        self.assertRejects(lambda r: r['features']['light_culling'].pop('light_flicker_count'), 'missing keys')
        for key in ('loop_players', 'loops', 'groups', 'group_mixers'):
            with self.subTest(states_key=key):
                self.assertRejects(lambda r, key=key: r['audio']['states'].pop(key), 'missing keys')
        self.assertRejects(lambda r: r['audio']['states']['groups']['CommonAmbientInGroup'].pop('players'), 'missing keys')
        self.assertRejects(lambda r: r['audio']['states']['groups']['CommonAmbientInGroup'].update(extra=1), 'unknown keys')

    def test_notes_are_optional_but_typed(self):
        raw = raw_contract()
        raw.pop('notes')
        self.assertEqual(parse(raw)['notes'], [])
        self.assertRejects(lambda r: r.update(notes='text'), 'list of strings')

    def test_identity_and_version(self):
        self.assertRejects(lambda r: r.update(schema_version=2), 'schema_version')
        self.assertRejects(lambda r: r.update(schema_version=True), 'schema_version')
        self.assertRejects(lambda r: r.update(map_key='arena_y'), "must be 'arena_x'")
        self.assertRejects(lambda r: r.update(map_key=''), 'non-empty string')

    def test_scene_tables(self):
        self.assertRejects(lambda r: r.update(scenes={}), 'non-empty')
        for bad in ('07', 'a', '-1', '', '7.0', '²'):
            with self.subTest(index=bad):
                self.assertRejects(lambda r, bad=bad: r['scenes'].update({bad: 'Arena_X_Other'}), 'canonical decimal')
        self.assertRejects(lambda r: r['scenes'].update({'8': 'Arena_X_Main'}), 'duplicate scene names')
        self.assertRejects(lambda r: r['ballistics'].pop('8'), 'exactly the scenes')
        self.assertRejects(lambda r: r['ballistics'].update({'9': 1}), 'exactly the scenes')
        self.assertRejects(lambda r: r['ballistics'].update({'7': -1}), 'integer >= 0')
        self.assertRejects(lambda r: r['ballistics'].update({'7': True}), 'integer >= 0')
        self.assertRejects(lambda r: r['ballistics'].update({'7': 1.0}), 'integer >= 0')
        self.assertRejects(lambda r: r.update(ballistics={'7': 0, '8': 0}), 'total must be positive')

    def test_flags_are_strict_booleans(self):
        self.assertRejects(lambda r: r.update(require_unsliced=1), 'boolean')
        self.assertRejects(lambda r: r.update(implicit_scene_identity='yes'), 'boolean')
        self.assertRejects(lambda r: r['features']['raincover'].update(weather_obstacle=0), 'boolean')

    def test_feature_pins_are_non_negative_integers(self):
        for change in (lambda r: r['features'].update(static_decals=-1),
                       lambda r: r['features'].update(static_decals=True),
                       lambda r: r['features']['light_culling'].update(volumetric_light_count=1.5),
                       lambda r: r['features']['navmesh_modifiers'].update(volumes=None),
                       lambda r: r['features'].update(small_physics_objects='0')):
            with self.subTest(change=change):
                self.assertRejects(change, 'integer >= 0')

    def test_features_null_is_allowed(self):
        raw = raw_contract()
        raw['features'] = None
        self.assertIsNone(parse(raw)['features'])

    def test_audio_null_is_allowed(self):
        raw = raw_contract()
        raw['audio'] = None
        self.assertIsNone(parse(raw)['audio'])

    def test_audio_shape(self):
        self.assertRejects(lambda r: r['audio'].update(schema=3), 'unsupported audio schema')
        self.assertRejects(lambda r: r['audio'].update(schema=True), 'unsupported audio schema')
        self.assertRejects(lambda r: r['audio'].update(scene='Sound'), "'level<N>'")
        self.assertRejects(lambda r: r['audio'].update(group_coverage='some'), 'group_coverage')
        self.assertRejects(lambda r: r['audio'].update(feature='ballistics'), 'reserved')
        self.assertRejects(lambda r: r['audio'].update(feature=''), 'non-empty string')
        self.assertRejects(lambda r: r['audio'].update(players={}), 'non-empty object')
        self.assertRejects(lambda r: r['audio'].update(support={'AmbientSoundPlayerGroup': 0}), 'integer >= 1')
        self.assertRejects(lambda r: r['audio'].update(banks=0), 'integer >= 1')
        self.assertRejects(lambda r: r['audio'].update(clips=None), 'integer >= 1')

    def test_states_follow_the_schema(self):
        # A schema-2 graph records hierarchy states and must pin them; a schema-1 graph has none.
        self.assertRejects(lambda r: r['audio'].update(states=None), 'must pin them')
        schema_one = raw_contract()
        schema_one['audio'].update(schema=1)
        with self.assertRaisesRegex(L.NativeContractError, 'must pin them'):
            parse(schema_one)
        schema_one['audio']['states'] = None
        self.assertIsNone(parse(schema_one)['audio']['states'])

    def test_states_must_agree_with_the_census(self):
        self.assertRejects(lambda r: r['audio']['states'].update(group_mixers=['CommonAmbInEffects']), 'entries for 2')
        self.assertRejects(lambda r: r['audio']['states'].update(grouped_players=5), 'disagrees')
        self.assertRejects(lambda r: r['audio']['states'].update(grouped_players=7), 'disagrees')
        self.assertRejects(lambda r: r['audio']['states'].update(inactive_players=[]), 'object of player id')
        self.assertRejects(lambda r: r['audio']['states'].update(disabled_players=['a', 'a']), 'duplicate')
        self.assertRejects(lambda r: r['audio']['states'].update(loop_players=[1]), 'non-empty string')
        self.assertRejects(lambda r: r['audio']['states'].update(loops=[]), 'object of player id')

        def active_coverage(r):
            r['audio']['group_coverage'] = 'active_players'
            r['audio']['states']['inactive_players'] = {'level9:1': 'far'}
        self.assertRejects(active_coverage, 'disagrees')  # 6 grouped but one player is inactive

        raw = raw_contract()
        active_coverage(raw)
        raw['audio']['states']['grouped_players'] = 5
        raw['audio']['states']['groups']['CommonAmbientInGroup']['players'] = 3
        self.assertEqual(parse(raw)['audio']['states']['grouped_players'], 5)
        raw['audio']['states']['disabled_players'] = ['level9:2']  # disabled players are not grouped either
        raw['audio']['states']['grouped_players'] = 4
        raw['audio']['states']['groups']['CommonAmbientInGroup']['players'] = 2
        self.assertEqual(parse(raw)['audio']['states']['grouped_players'], 4)

    def test_group_pins_are_per_group_data_that_agree_with_their_mirrors(self):
        def states(r):
            return r['audio']['states']

        def groups(r):
            return states(r)['groups']

        # per-group record shape
        self.assertRejects(lambda r: states(r).update(groups=[]), 'object of group leaf name')
        self.assertRejects(lambda r: states(r).update(groups={}), 'entries for 2')
        self.assertRejects(lambda r: groups(r).pop('CommonAmbientInGroup'), 'entries for 2')
        self.assertRejects(lambda r: groups(r).update(Third={'mixer': 'CommonAmbInEffects', 'players': 1}), 'entries for 2')
        self.assertRejects(lambda r: groups(r)['CommonAmbientInGroup'].update(mixer=''), 'non-empty string')
        self.assertRejects(lambda r: groups(r)['CommonAmbientInGroup'].update(players=0), 'integer >= 1')
        self.assertRejects(lambda r: groups(r)['CommonAmbientInGroup'].update(players=True), 'integer >= 1')
        self.assertRejects(lambda r: groups(r)['CommonAmbientInGroup'].update(players='4'), 'integer >= 1')
        self.assertRejects(lambda r: groups(r).update(CommonAmbientInGroup=['x']), 'expected an object')
        # the member counts must add up to the grouped players (a typo changes the total)
        self.assertRejects(lambda r: groups(r)['CommonAmbientInGroup'].update(players=5), 'group members disagree')
        self.assertRejects(lambda r: groups(r)['CommonAmbientOutGroup'].update(players=1), 'group members disagree')
        # group_mixers stays the ordered mirror of the mixers of 'groups'
        self.assertRejects(lambda r: states(r).update(group_mixers=['CommonAmbInEffects', 'CommonAmbOutEffects']), 'same order')
        self.assertRejects(lambda r: states(r).update(group_mixers=['CommonAmbOutEffects', 'CommonAmbOutEffects']), 'same order')

    def test_loops_are_pinned_by_id_and_leaf_name_with_loop_players_as_the_mirror(self):
        def states(r):
            return r['audio']['states']

        def named_loop(r):
            states(r).update(loops={'level9:3': 'hum'}, loop_players=['level9:3'])

        raw = raw_contract()
        named_loop(raw)
        self.assertEqual(parse(raw)['audio']['states']['loops'], {'level9:3': 'hum'})
        self.assertRejects(lambda r: (named_loop(r), states(r).update(loop_players=[])), 'exactly the ids')
        self.assertRejects(lambda r: (named_loop(r), states(r).update(loop_players=['level9:4'])), 'exactly the ids')
        self.assertRejects(lambda r: (named_loop(r), states(r).update(loops={'level9:3': ''})), 'non-empty string')
        self.assertRejects(lambda r: states(r).update(loops={'level9:3': 'hum'}), 'exactly the ids')

    def test_group_names_are_unique_keys(self):
        text = json.dumps(raw_contract()).replace('"CommonAmbientInGroup": {', '"CommonAmbientOutGroup": {', 1)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'x.json'
            path.write_text(text, encoding='utf-8')
            with self.assertRaisesRegex(L.NativeContractError, 'duplicate JSON key'):
                L.load_contract_file(path)


class PartialScenesSchemaTests(unittest.TestCase):
    """Optional ``partial_scenes`` block: strict inside, always present (``{}``) after normalisation."""

    def raw(self):
        raw = raw_contract()
        raw['partial_scenes'] = partial_block()
        return raw

    def assertRejects(self, change, pattern=None):
        raw = self.raw()
        change(raw)
        with self.assertRaises(L.NativeContractError) as caught:
            parse(raw)
        if pattern:
            self.assertRegex(str(caught.exception), pattern)

    def test_absent_block_normalises_to_an_empty_table(self):
        self.assertEqual(parse(raw_contract())['partial_scenes'], {})
        raw = raw_contract()
        raw['partial_scenes'] = {}
        self.assertEqual(parse(raw)['partial_scenes'], {})

    def test_valid_block_is_normalised_in_file_order(self):
        raw = self.raw()
        raw['partial_scenes']['Arena_X_Main'] = {'index': 7, 'objects': 5, 'units': 1, 'carriers': 0, 'orphans': 0,
                                                 'dropped_script_owners': 0, 'excluded': 0, 'exclude_entries': 0,
                                                 'twin_descendants_added': 0,
                                                 'rules': {'roots:A': rule_counts(1)}}
        contract = parse(raw)
        self.assertEqual(list(contract['partial_scenes']), ['Arena_X_Light', 'Arena_X_Main'])
        self.assertEqual(contract['partial_scenes']['Arena_X_Light'], partial_block()['Arena_X_Light'])
        self.assertEqual(list(contract['partial_scenes']['Arena_X_Light']['rules']),
                         ['roots:Root/Shops', 'region_roots:Root/Props'])
        self.assertEqual(list(contract['partial_scenes']['Arena_X_Light']['rules']['roots:Root/Shops']),
                         list(PARTIAL_RULE_KEYS))

    def test_unknown_and_missing_keys_inside_the_block_are_rejected(self):
        entry = lambda r: r['partial_scenes']['Arena_X_Light']
        self.assertRejects(lambda r: entry(r).update(reason='why'), 'unknown keys')
        self.assertRejects(lambda r: entry(r)['rules']['roots:Root/Shops'].update(extra=1), 'unknown keys')
        for key in ('index', 'objects', 'units', 'carriers', 'orphans', 'dropped_script_owners') + NEW_PARTIAL_KEYS + ('rules',):
            with self.subTest(key=key):
                self.assertRejects(lambda r, key=key: entry(r).pop(key), 'missing keys')
        for key in PARTIAL_RULE_KEYS:
            with self.subTest(rule_key=key):
                self.assertRejects(lambda r, key=key: entry(r)['rules']['roots:Root/Shops'].pop(key), 'missing keys')

    def test_top_level_unknown_key_next_to_the_block_is_still_rejected(self):
        self.assertRejects(lambda r: r.update(partial_scene={}), 'unknown keys')

    def test_counts_are_non_negative_integers(self):
        entry = lambda r: r['partial_scenes']['Arena_X_Light']
        for key, bad in (('objects', 0), ('objects', 30.0), ('objects', True), ('units', 0), ('carriers', -1),
                         ('orphans', -1), ('orphans', '1'), ('dropped_script_owners', -1),
                         ('dropped_script_owners', 0.0), ('index', None), ('excluded', -1), ('excluded', 1.0),
                         ('excluded', True), ('exclude_entries', -1), ('exclude_entries', 1.0), ('exclude_entries', None),
                         ('twin_descendants_added', -1), ('twin_descendants_added', 1.0), ('twin_descendants_added', '1'),
                         ('twin_descendants_added', False)):
            with self.subTest(key=key, bad=bad):
                self.assertRejects(lambda r, key=key, bad=bad: entry(r).update({key: bad}), 'integer >=')
        for key, bad in (('selected', -1), ('twin_skipped', 1.5), ('selected', False), ('excluded', -1),
                         ('excluded', 1.0), ('excluded', True), ('descendants_added', -1), ('descendants_added', 0.0),
                         ('descendants_added', '1'), ('descendants_added', None)):
            with self.subTest(rule_key=key, bad=bad):
                self.assertRejects(lambda r, key=key, bad=bad: entry(r)['rules']['roots:Root/Shops'].update({key: bad}),
                                   'integer >= 0')

    def test_block_must_agree_with_the_scene_table(self):
        self.assertRejects(lambda r: r['partial_scenes'].update({'Arena_X_Other': r['partial_scenes']['Arena_X_Light']}),
                           'not a scene of')
        self.assertRejects(lambda r: r['partial_scenes']['Arena_X_Light'].update(index=7), 'contract index 8')
        self.assertRejects(lambda r: r.update(partial_scenes=[]), 'object of scene name')
        self.assertRejects(lambda r: r['partial_scenes'].update({'': {}}), 'non-empty string')

    def test_object_count_must_hold_the_units_and_carriers(self):
        self.assertRejects(lambda r: r['partial_scenes']['Arena_X_Light'].update(objects=13), 'cannot hold 10 units and 4 carriers')
        parse_ok = self.raw()
        parse_ok['partial_scenes']['Arena_X_Light']['objects'] = 14
        parse(parse_ok)

    def test_rules_must_be_a_non_empty_table_of_named_counts(self):
        self.assertRejects(lambda r: r['partial_scenes']['Arena_X_Light'].update(rules={}), 'non-empty object')
        self.assertRejects(lambda r: r['partial_scenes']['Arena_X_Light'].update(rules=[]), 'non-empty object')
        self.assertRejects(lambda r: r['partial_scenes']['Arena_X_Light']['rules'].update({'': rule_counts(1)}),
                           'non-empty string')

    def test_the_rules_must_add_up_to_the_units(self):
        """units == sum(selected) + sum(descendants_added) - sum(excluded); twin-skipped units never count."""
        entry = lambda r: r['partial_scenes']['Arena_X_Light']
        shops = lambda r: entry(r)['rules']['roots:Root/Shops']
        props = lambda r: entry(r)['rules']['region_roots:Root/Props']
        self.assertRejects(lambda r: shops(r).update(selected=9), 'add up to the 10 units, the rules leave 11')
        self.assertRejects(lambda r: entry(r).update(units=11, objects=40), 'add up to the 11 units')
        # the added descendants count as units ...
        self.assertRejects(lambda r: props(r).update(descendants_added=0), 'add up to the 10 units, the rules leave 9')
        # ... and the excluded ones do not
        self.assertRejects(lambda r: shops(r).update(excluded=0), 'add up to the 10 units, the rules leave 11')
        # twin-skipped units are not selected and do not count
        raw = self.raw()
        shops(raw)['twin_skipped'] = 99
        parse(raw)

    def test_the_scene_level_counters_repeat_the_rule_totals(self):
        entry = lambda r: r['partial_scenes']['Arena_X_Light']
        self.assertRejects(lambda r: entry(r).update(twin_descendants_added=2), 'twin_descendants_added.*differs from the sum')
        self.assertRejects(lambda r: entry(r).update(twin_descendants_added=0), 'twin_descendants_added.*differs from the sum')
        self.assertRejects(lambda r: entry(r).update(excluded=2), 'excluded.*differs from the sum')
        self.assertRejects(lambda r: entry(r).update(excluded=0), 'excluded.*differs from the sum')
        self.assertEqual(parse(self.raw())['partial_scenes']['Arena_X_Light']['excluded'], 1)

    def test_a_rule_cannot_drop_more_units_than_it_holds(self):
        # Props holds 2 selected + 1 added units; dropping 4 would leave it negative
        self.assertRejects(lambda r: r['partial_scenes']['Arena_X_Light']['rules']['region_roots:Root/Props'].update(excluded=4),
                           'cannot be dropped from a rule that holds 2 selected and 1 added')

    def test_exclude_entries_exist_exactly_when_units_were_excluded(self):
        """An entry that drops no unit fails the extraction, so entries and excluded units are zero together."""
        entry = lambda r: r['partial_scenes']['Arena_X_Light']
        self.assertRejects(lambda r: entry(r).update(exclude_entries=0), 'exclude entries cannot drop 1 units')

        def entries_without_units(raw):  # Shops keeps all 7 + the unit it would have lost, Props unchanged: units stay 10
            entry(raw)['rules']['roots:Root/Shops'].update(selected=7, excluded=0)
            entry(raw).update(excluded=0, exclude_entries=2)
        self.assertRejects(entries_without_units, '2 exclude entries cannot drop 0 units')
        # several entries may drop several units (a container entry drops a whole group)
        raw = self.raw()
        entry(raw)['rules']['roots:Root/Shops'].update(selected=11, excluded=4)
        entry(raw).update(excluded=4, exclude_entries=2)
        self.assertEqual(parse(raw)['partial_scenes']['Arena_X_Light']['exclude_entries'], 2)

    def test_a_contract_from_before_the_new_counters_is_refused(self):
        """The pins of airpit-native-v7 (no exclude list, no twin descendants) must be re-pinned, not read with zeros."""
        def v7_shape(raw):
            entry = raw['partial_scenes']['Arena_X_Light']
            for key in NEW_PARTIAL_KEYS:
                entry.pop(key)
            for counts in entry['rules'].values():
                counts.pop('excluded')
                counts.pop('descendants_added')
        self.assertRejects(v7_shape, 'missing keys')

    def test_partial_scenes_require_the_slice_refusal(self):
        self.assertRejects(lambda r: r.update(require_unsliced=False), 'require_unsliced')
        raw = raw_contract()
        raw['require_unsliced'] = False
        parse(raw)  # a contract without partial scenes may still allow slices (released Bay5)


class ContractFileTests(unittest.TestCase):
    def write(self, directory, name, text):
        path = Path(directory) / name
        path.write_text(text, encoding='utf-8')
        return path

    def test_duplicate_json_key_is_an_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            text = json.dumps(raw_contract()).replace('"7": 10', '"7": 10, "7": 11')
            path = self.write(tmp, 'x.json', text)
            with self.assertRaisesRegex(L.NativeContractError, 'duplicate JSON key'):
                L.load_contract_file(path)

    def test_unreadable_and_malformed_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(L.NativeContractError, 'cannot read'):
                L.load_contract_file(Path(tmp) / 'missing.json')
            path = self.write(tmp, 'x.json', '{"schema_version": 1,')
            with self.assertRaisesRegex(L.NativeContractError, 'cannot read'):
                L.load_contract_file(path)

    def test_file_stem_must_match_the_map_key(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = self.write(tmp, 'y.json', json.dumps(raw_contract()))
            with self.assertRaisesRegex(L.NativeContractError, "must be 'arena_y'"):
                L.load_contract_file(path)
            ok = self.write(tmp, 'x.json', json.dumps(raw_contract()))
            self.assertEqual(L.load_contract_file(ok)['key'], 'arena_x')

    def test_missing_or_empty_directory_fails_instead_of_returning_an_empty_table(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(L.NativeContractError, 'no native contract files'):
                L.load_contracts(tmp)
            with self.assertRaisesRegex(L.NativeContractError, 'directory missing'):
                L.load_contracts(Path(tmp) / 'absent')

    def test_one_bad_file_fails_the_whole_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.write(tmp, 'x.json', json.dumps(raw_contract()))
            bad = raw_contract()
            bad['map_key'] = 'arena_z'
            bad['ballistics'] = {'7': 0, '8': 0}
            self.write(tmp, 'z.json', json.dumps(bad))
            with self.assertRaisesRegex(L.NativeContractError, 'total must be positive'):
                L.load_contracts(tmp)


# ---------------------------------------------------------------------------------------------
# validate_native_features driven by the data
# ---------------------------------------------------------------------------------------------

def partial_manifest_block(contract: dict) -> dict:
    """The manifest ``partial_scenes`` block that satisfies the contract's pins exactly (hash, orphan ids and the
    hierarchy paths of the added twin descendants are synthetic; the contract does not pin them)."""
    block = {}
    for name, pin in contract['partial_scenes'].items():
        block[name] = {'index': pin['index'], 'objects': pin['objects'], 'units': pin['units'],
                       'carriers': pin['carriers'], 'rules': deepcopy(pin['rules']),
                       'dropped_script_owners': pin['dropped_script_owners'],
                       'ballistic_orphans': [[1000 + i, 5000 + i] for i in range(pin['orphans'])],
                       'go_ids_sha256': 'a' * 64,
                       'excluded': pin['excluded'], 'exclude_entries': pin['exclude_entries'],
                       'twin_descendants_added': pin['twin_descendants_added'],
                       'twin_descendant_paths': [f'Root/{name}/Twin{i:05d}' for i in range(pin['twin_descendants_added'])],
                       'twin_carrier_ids': ['3000'] if pin['twin_descendants_added'] else []}
    return block


def airpit_manifest(audio: bool = True) -> dict:
    """The manifest blocks of an AirPit package; audio=False is the ballistics-only (pre item 13) shape."""
    contract = v.MAPS['arena_airpit']
    pins = contract['features']
    manifest = {
        'source': {'map_key': 'arena_airpit'}, 'scenes': list(contract['scenes'].values()),
        'materials': {'m0': {}},
        'native_features': {
            'schema_version': 1, 'required': ['ballistics'],
            'navmesh_areas': {'0': {'name': 'Walkable', 'cost': 1.0}, '1': {'name': 'Not Walkable', 'cost': 1.0}},
            'raincover': {'schema_version': 1, 'source_scene': 'Scripts (level36)', **pins['raincover']},
            'light_culling': {'schema_version': 1, **pins['light_culling']},
            'static_decals': {'schema_version': 1, 'count': pins['static_decals']}},
        'native_ballistics': {},
    }
    if contract['partial_scenes']:
        manifest['partial_scenes'] = partial_manifest_block(contract)
    if audio:
        manifest['native_features']['required'].append(contract['audio']['feature'])
        manifest['native_audio'] = {}
    return manifest


def airpit_scenes(manifest: dict, **override) -> dict:
    """One owner per scene carrying the pinned component census (plus overrides: type -> count)."""
    wanted = {'BallisticCollider': sum(v.MAPS['arena_airpit']['ballistics'].values()),
              **v.pinned_component_census(v.MAPS['arena_airpit']['features'])}
    wanted.update(override)
    scenes = {name: [{'id': name, 'components': []}] for name in manifest['scenes']}
    first = scenes[manifest['scenes'][0]][0]['components']
    for kind, count in wanted.items():
        for _ in range(count):
            component = {'type': kind}
            if kind == 'StaticDeferredDecal':
                component.update(schema_version=1, material_id='m0')
            first.append(component)
    return scenes


def mono(script: str):
    head = SimpleNamespace(m_Script=SimpleNamespace(path_id=1, deref=lambda: SimpleNamespace(
        read=lambda: SimpleNamespace(m_ClassName=script, m_Namespace='', m_AssemblyName='Assembly-CSharp'))))
    return SimpleNamespace(type=SimpleNamespace(name='MonoBehaviour'), parse_monobehaviour_head=lambda: head)


class AirPitNativeContractTests(unittest.TestCase):
    """AirPit's contract since PLAN-AIRPIT item 13: ballistics plus the native ambient audio graph."""

    def test_manifest_validates_with_and_without_scenes(self):
        manifest = airpit_manifest()
        self.assertEqual(manifest['native_features']['required'], ['ballistics', 'ambient_audio'])
        self.assertTrue(v.feature_enabled(manifest))
        self.assertTrue(v.feature_enabled(manifest, airpit_scenes(manifest)))

    def test_the_printed_partial_scope_block_is_the_airpit_contract_block(self):
        manifest = airpit_manifest()
        self.assertEqual(partial_scope.contract_block(manifest), v.MAPS['arena_airpit']['partial_scenes'])

    def test_native_audio_entry_is_now_required(self):
        manifest = airpit_manifest()
        del manifest['native_audio']
        with self.assertRaisesRegex(ValueError, 'incomplete required feature contract'):
            v.feature_enabled(manifest)

    def test_the_superseded_ballistics_only_shape_is_rejected(self):
        # work/extract/airpit-native-v5 (no native_audio, required == ['ballistics']) predates item 13.
        manifest = airpit_manifest(audio=False)
        with self.assertRaisesRegex(ValueError, 'incomplete required feature contract'):
            v.feature_enabled(manifest)
        manifest['native_audio'] = {}  # an audio entry without the required feature name is no better
        with self.assertRaisesRegex(ValueError, 'feature contract'):
            v.feature_enabled(manifest)

    def test_required_list_must_be_exactly_ballistics_then_ambient_audio(self):
        for required in (['ballistics'], [], ['ambient_audio'], ['ballistics', 'ballistics'],
                         ['ambient_audio', 'ballistics'], ['ballistics', 'ambient_audio', 'ambient_audio'],
                         ['ballistics', 'harbor_audio']):
            manifest = airpit_manifest()
            manifest['native_features']['required'] = required
            with self.subTest(required=required), self.assertRaisesRegex(ValueError, 'feature contract'):
                v.feature_enabled(manifest)

    def test_equator_and_bay5_still_require_native_audio(self):
        for key in ('arena_equator', 'arena_bay5'):
            contract = v.MAPS[key]
            manifest = {'source': {'map_key': key}, 'scenes': list(contract['scenes'].values()),
                        'native_features': {'schema_version': 1, 'required': ['ballistics']},
                        'native_ballistics': {}}
            with self.subTest(key=key, case='audio absent'), self.assertRaisesRegex(ValueError, 'incomplete required feature contract'):
                v.feature_enabled(manifest)
            manifest['native_features']['required'].append(contract['audio']['feature'])
            manifest['native_audio'] = {}
            self.assertTrue(v.feature_enabled(manifest))
            manifest['native_features']['required'] = ['ballistics']  # audio present, but not required
            with self.subTest(key=key, case='audio not required'), self.assertRaisesRegex(ValueError, 'feature contract'):
                v.feature_enabled(manifest)

    def test_wrong_manifest_census_fails(self):
        def wrong(change):
            manifest = airpit_manifest()
            change(manifest['native_features'])
            return manifest
        pins = v.MAPS['arena_airpit']['features']
        cases = {
            'decals -1': lambda f: f['static_decals'].update(count=pins['static_decals'] - 1),
            'decals +1': lambda f: f['static_decals'].update(count=pins['static_decals'] + 1),
            'culling lights': lambda f: f['light_culling'].update(
                culling_light_object_count=pins['light_culling']['culling_light_object_count'] - 1),
            'volumetric': lambda f: f['light_culling'].update(
                volumetric_light_count=pins['light_culling']['volumetric_light_count'] + 1),
            'flicker': lambda f: f['light_culling'].update(light_flicker_count=1),
            'indoor triggers': lambda f: f['raincover'].update(indoor_trigger_count=3),
            'dry planes': lambda f: f['raincover'].update(dry_plane_count=1),
            'weather obstacle': lambda f: f['raincover'].update(weather_obstacle=True),
            'bool for count': lambda f: f['light_culling'].update(light_flicker_count=False),
        }
        for name, change in cases.items():
            with self.subTest(name), self.assertRaisesRegex(ValueError, 'differs from the map contract'):
                v.feature_enabled(wrong(change))

    def test_missing_feature_fails(self):
        for entry in ('static_decals', 'light_culling', 'raincover'):
            manifest = airpit_manifest()
            manifest['native_features'].pop(entry)
            with self.subTest(entry), self.assertRaisesRegex(ValueError, 'differs from the map contract'):
                v.feature_enabled(manifest)
        for key in ('native_features', 'native_ballistics'):
            manifest = airpit_manifest()
            manifest.pop(key)
            with self.subTest(key), self.assertRaisesRegex(ValueError, 'incomplete required feature contract'):
                v.feature_enabled(manifest)

    def test_wrong_scene_census_fails(self):
        manifest = airpit_manifest()
        for kind, delta in [('NavMeshModifier', -1), ('NavMeshModifier', 1), ('NavMeshModifierVolume', -1),
                            ('NavMeshModifierVolume', 1), ('SmallPhysicsObject', 1), ('CullingLightObject', -1),
                            ('VolumetricLight', 1), ('LightFlicker', 1)]:
            pinned = v.pinned_component_census(v.MAPS['arena_airpit']['features'])[kind]
            scenes = airpit_scenes(manifest, **{kind: pinned + delta})
            with self.subTest(kind=kind, delta=delta), self.assertRaisesRegex(ValueError, 'native component census'):
                v.feature_enabled(manifest, scenes)

    def test_scene_decals_must_match_the_manifest_and_the_pin(self):
        manifest = airpit_manifest()
        pinned = v.MAPS['arena_airpit']['features']['static_decals']
        with self.assertRaisesRegex(ValueError, 'static decal census differs'):
            v.feature_enabled(manifest, airpit_scenes(manifest, StaticDeferredDecal=pinned - 1))
        manifest['native_features']['static_decals']['count'] = pinned - 1
        with self.assertRaisesRegex(ValueError, 'differs from the map contract'):
            v.feature_enabled(manifest, airpit_scenes(manifest, StaticDeferredDecal=pinned - 1))

    def test_scene_set_and_slice_rules(self):
        for change in (lambda m: m['scenes'].pop(), lambda m: m['scenes'].append(m['scenes'][0]),
                       lambda m: m['source'].update(map_key='arena_prison'), lambda m: m.pop('source'),
                       lambda m: m.update(slice={'324': [1]})):
            manifest = airpit_manifest()
            change(manifest)
            with self.subTest(change=change), self.assertRaises(ValueError):
                v.feature_enabled(manifest)
        manifest = airpit_manifest()
        manifest['slice'] = {}
        self.assertTrue(v.feature_enabled(manifest))

    def test_unmarked_native_components_still_fail_without_a_manifest_block(self):
        self.assertFalse(v.feature_enabled({'scenes': ['Arena_AirPit_ShootOut']}))
        with self.assertRaisesRegex(ValueError, 'unmarked ballistic'):
            v.feature_enabled({}, {'scene': [{'components': [{'type': 'BallisticCollider'}]}]})

    def test_partial_scene_census_is_pinned_by_the_contract(self):
        manifest = airpit_manifest()
        self.assertEqual(sorted(manifest['partial_scenes']), sorted(v.MAPS['arena_airpit']['partial_scenes']))
        self.assertTrue(v.feature_enabled(manifest))
        pins = v.MAPS['arena_airpit']['partial_scenes']
        name = 'Arena_Airpit_Main_hall'
        first_rule = next(iter(pins[name]['rules']))
        changes = {
            'objects': lambda e: e.update(objects=e['objects'] + 1),
            'units': lambda e: e.update(units=e['units'] - 1),
            'carriers': lambda e: e.update(carriers=e['carriers'] + 1),
            'dropped script owner': lambda e: e.update(dropped_script_owners=e['dropped_script_owners'] + 1),
            'excluded': lambda e: e.update(excluded=e['excluded'] + 1),
            'exclude_entries': lambda e: e.update(exclude_entries=e['exclude_entries'] + 1),
            'twin_descendants_added': lambda e: e.update(twin_descendants_added=e['twin_descendants_added'] + 1,
                                                         twin_descendant_paths=e['twin_descendant_paths'] + ['Z']),
            'rule excluded': lambda e: e['rules'][first_rule].update(excluded=e['rules'][first_rule]['excluded'] + 1),
            'rule descendants_added': lambda e: e['rules'][first_rule].update(
                descendants_added=e['rules'][first_rule]['descendants_added'] + 1),
            'index': lambda e: e.update(index=39),
            'orphan dropped': lambda e: e['ballistic_orphans'].pop(),
            'orphan added': lambda e: e['ballistic_orphans'].append([7, 8]),
            'rule selected': lambda e: e['rules'][first_rule].update(selected=e['rules'][first_rule]['selected'] + 1),
            'rule twin_skipped': lambda e: e['rules'][first_rule].update(twin_skipped=e['rules'][first_rule]['twin_skipped'] + 1),
            'rule renamed': lambda e: e['rules'].update({'roots:Renamed': e['rules'].pop(first_rule)}),
            'rule added': lambda e: e['rules'].update({'roots:Extra': rule_counts()}),
        }
        for label, change in changes.items():
            broken = airpit_manifest()
            change(broken['partial_scenes'][name])
            with self.subTest(label), self.assertRaises(ValueError) as caught:
                v.feature_enabled(broken)
            self.assertRegex(str(caught.exception), 'partial scene|Arena_Airpit_Main_hall|index')

    def test_partial_scene_pins_are_exact_integers(self):
        manifest = airpit_manifest()
        entry = manifest['partial_scenes']['Arena_Airpit_Light']
        entry['carriers'] = float(entry['carriers'])
        with self.assertRaises(ValueError):  # shape check of the manifest block already refuses a float count
            v.feature_enabled(manifest)
        manifest = airpit_manifest()
        manifest['partial_scenes']['Arena_Airpit_Light']['rules'] = {
            rule: {k: counts[k] for k in PARTIAL_RULE_KEYS}
            for rule, counts in manifest['partial_scenes']['Arena_Airpit_Light']['rules'].items()}
        self.assertTrue(v.feature_enabled(manifest))
        for key in NEW_PARTIAL_KEYS:  # the new scene-level counters compare by exact type too
            manifest = airpit_manifest()
            manifest['partial_scenes']['Arena_Airpit_Light'][key] = float(manifest['partial_scenes']['Arena_Airpit_Light'][key])
            with self.subTest(key=key), self.assertRaises(ValueError):
                v.feature_enabled(manifest)

    def test_the_manifest_and_the_contract_must_name_the_same_partial_scenes(self):
        for label, change in {
                'block missing': lambda m: m.pop('partial_scenes'),
                'block empty': lambda m: m.update(partial_scenes={}),
                'one scene missing': lambda m: m['partial_scenes'].pop('Arena_Airpit_Light'),
                'unlisted scene': lambda m: m['partial_scenes'].update({'Arena_Airpit_DesignStuff': m['partial_scenes']['Arena_Airpit_Light']}),
        }.items():
            manifest = airpit_manifest()
            change(manifest)
            with self.subTest(label), self.assertRaises(ValueError):
                v.feature_enabled(manifest)

    def test_a_manifest_with_partial_scenes_needs_a_contract_that_pins_them(self):
        for key in ('arena_equator', 'arena_bay5'):
            contract = v.MAPS[key]
            manifest = {'source': {'map_key': key}, 'scenes': list(contract['scenes'].values()),
                        'native_features': {'schema_version': 1, 'required': ['ballistics', contract['audio']['feature']]},
                        'native_ballistics': {}, 'native_audio': {}}
            self.assertTrue(v.feature_enabled(manifest))
            manifest['partial_scenes'] = {contract['scenes'][next(iter(contract['scenes']))]: partial_manifest_block(
                v.MAPS['arena_airpit'])['Arena_Airpit_Light']}
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'pins no partial scenes'):
                v.feature_enabled(manifest)

    def test_a_damaged_manifest_block_fails_instead_of_narrowing_a_census(self):
        for label, change in {
                'not an object': lambda e: e.update(rules=[]),
                'orphan not a pair': lambda e: e.update(ballistic_orphans=[[1]]),
                'orphan not an id': lambda e: e.update(ballistic_orphans=[['x', 'y']]),
                'bad hash': lambda e: e.update(go_ids_sha256='zz'),
                'missing key': lambda e: e.pop('units'),
                'missing dropped count': lambda e: e.pop('dropped_script_owners'),
                'missing excluded': lambda e: e.pop('excluded'),
                'missing exclude_entries': lambda e: e.pop('exclude_entries'),
                'missing twin_descendants_added': lambda e: e.pop('twin_descendants_added'),
                'missing twin_descendant_paths': lambda e: e.pop('twin_descendant_paths'),
                'paths unsorted': lambda e: e.update(twin_descendants_added=2, twin_descendant_paths=['b', 'a']),
                'rule without descendants_added': lambda e: next(iter(e['rules'].values())).pop('descendants_added'),
        }.items():
            manifest = airpit_manifest()
            change(manifest['partial_scenes']['Arena_Airpit_Main_hall'])
            with self.subTest(label), self.assertRaisesRegex(ValueError, 'partial scene Arena_Airpit_Main_hall'):
                v.feature_enabled(manifest)

    def test_manifest_counters_that_contradict_each_other_fail_before_the_pins_are_read(self):
        manifest = airpit_manifest()
        first = next(iter(manifest['partial_scenes']['Arena_Airpit_Light']['rules'].values()))
        first['excluded'] += 1  # one more dropped unit than the totals and the units account for
        with self.assertRaisesRegex(ValueError, 'partial scene Arena_Airpit_Light'):
            v.feature_enabled(manifest)

    def test_strict_census_equality(self):
        self.assertTrue(v.same(2, 2))
        self.assertFalse(v.same(True, 1))
        self.assertFalse(v.same(2.0, 2))
        self.assertFalse(v.same(None, 0))


class PartialPinCounterTests(unittest.TestCase):
    """check_partial_scene_pins holds the exclude / twin-descendant counters to the pins in BOTH directions.

    Synthetic contract with counters small enough to check by hand (the AirPit contract's own v8 pins are covered by
    the real-package tests): two rules A and B, selected 8 and 5, units = 13 + added - excluded."""

    @staticmethod
    def block(a_added=2, a_excluded=1, b_added=0, b_excluded=0, entries=1):
        added, excluded = a_added + b_added, a_excluded + b_excluded
        return {'index': 8, 'objects': 40, 'units': 13 + added - excluded, 'carriers': 4, 'orphans': 1,
                'dropped_script_owners': 0, 'excluded': excluded, 'exclude_entries': entries if excluded else 0,
                'twin_descendants_added': added,
                'rules': {'roots:A': rule_counts(8, 2, a_excluded, a_added), 'roots:B': rule_counts(5, 0, b_excluded, b_added)}}

    @staticmethod
    def contract(block):
        raw = raw_contract()
        raw['partial_scenes'] = {'Arena_X_Light': deepcopy(block)}
        return parse(raw)

    def check(self, pinned, actual):
        manifest = {'scenes': ['Arena_X_Main', 'Arena_X_Light'],
                    'partial_scenes': partial_manifest_block(self.contract(actual))}
        v.check_partial_scene_pins('arena_x', self.contract(pinned), manifest)

    # label -> (block change, expected message)
    VARIANTS = {
        'one more twin descendant': (dict(a_added=3), 'census differs from the map contract'),
        'one twin descendant less': (dict(a_added=1), 'census differs from the map contract'),
        'one more excluded unit': (dict(a_excluded=2), 'census differs from the map contract'),
        'no excluded unit and no entry': (dict(a_excluded=0), 'census differs from the map contract'),
        'one more exclude entry': (dict(entries=2), 'census differs from the map contract'),
        'no twin descendants': (dict(a_added=0), 'census differs from the map contract'),
        # totals and units identical, the unit sits in another rule: only the per-rule counters tell
        'excluded unit in another rule': (dict(a_excluded=0, b_excluded=1), 'rule roots:A census differs'),
        'twin descendant in another rule': (dict(a_added=1, b_added=1), 'rule roots:A census differs'),
    }

    def test_identical_counters_are_accepted(self):
        self.check(self.block(), self.block())
        self.check(self.block(a_added=0, a_excluded=0), self.block(a_added=0, a_excluded=0))  # neither mechanism used

    def test_every_counter_is_compared_in_both_directions(self):
        base = self.block()
        for label, (change, pattern) in self.VARIANTS.items():
            other = self.block(**change)
            self.assertNotEqual(other, base, label)
            with self.subTest(label, direction='manifest differs'), self.assertRaisesRegex(ValueError, pattern):
                self.check(base, other)
            with self.subTest(label, direction='pin differs'), self.assertRaisesRegex(ValueError, pattern):
                self.check(other, base)

    def test_the_hierarchy_paths_are_review_material_and_not_pinned(self):
        pin = self.contract(self.block())
        manifest = {'scenes': ['Arena_X_Main', 'Arena_X_Light'], 'partial_scenes': partial_manifest_block(pin)}
        manifest['partial_scenes']['Arena_X_Light']['twin_descendant_paths'] = ['Other/Path/A', 'Other/Path/B']
        v.check_partial_scene_pins('arena_x', pin, manifest)

    def test_the_printed_block_is_paste_ready(self):
        """tools/validate/partial_scope.py prints exactly what the contract file holds (minus the review-only paths)."""
        contract = self.contract(self.block())
        manifest = {'scenes': ['Arena_X_Main', 'Arena_X_Light'], 'partial_scenes': partial_manifest_block(contract)}
        printed = partial_scope.contract_block(manifest)
        self.assertEqual(printed, contract['partial_scenes'])
        self.assertEqual(list(printed['Arena_X_Light']), list(contract['partial_scenes']['Arena_X_Light']))
        raw = raw_contract()
        raw['partial_scenes'] = json.loads(json.dumps(printed))  # through JSON, as the CLI prints and the file stores it
        self.assertEqual(parse(raw)['partial_scenes'], contract['partial_scenes'])

    def test_a_manifest_with_the_counters_needs_pins_that_carry_them(self):
        manifest = {'scenes': ['Arena_X_Main', 'Arena_X_Light'],
                    'partial_scenes': partial_manifest_block(self.contract(self.block()))}
        with self.assertRaisesRegex(ValueError, 'pins no partial scenes'):
            v.check_partial_scene_pins('arena_x', parse(raw_contract()), manifest)


class BallisticsOnlyContractBranchTests(unittest.TestCase):
    """The `audio: null` branch of the validator. No repository map uses it since item 13 filled AirPit's
    audio, so the AirPit contract with its audio dropped stands in for a future ballistics-only map."""

    def setUp(self):
        patcher = patch.dict(v.MAPS, {'arena_airpit': dict(v.MAPS['arena_airpit'], audio=None)})
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_ballistics_only_manifest_validates_with_and_without_scenes(self):
        manifest = airpit_manifest(audio=False)
        self.assertTrue(v.feature_enabled(manifest))
        self.assertTrue(v.feature_enabled(manifest, airpit_scenes(manifest)))

    def test_native_audio_entry_is_rejected_when_the_contract_declares_none(self):
        manifest = airpit_manifest(audio=False)
        manifest['native_audio'] = {}
        with self.assertRaisesRegex(ValueError, 'declares no native audio'):
            v.feature_enabled(manifest)

    def test_required_list_must_be_exactly_ballistics(self):
        for required in (['ballistics', 'ambient_audio'], [], ['ambient_audio'], ['ballistics', 'ballistics']):
            manifest = airpit_manifest(audio=False)
            manifest['native_features']['required'] = required
            with self.subTest(required=required), self.assertRaisesRegex(ValueError, 'feature contract'):
                v.feature_enabled(manifest)

    def test_extra_counts_without_audio_are_the_rain_cover_objects_only(self):
        manifest = airpit_manifest(audio=False)
        # EnvironmentManager + TriggerGroup + 2 IndoorTrigger, no WeatherObstacle/DryPlane; no audio root.
        self.assertEqual(v.extra_counts('unused', manifest), {'GameObject': 4, 'Transform': 4, 'AudioSource': 0})
        manifest['native_features'].pop('raincover')
        with self.assertRaises(ValueError):  # the pin makes the rain-cover summary mandatory
            v.extra_counts('unused', manifest)

    def test_audio_proofs_refuse_a_map_without_an_audio_contract(self):
        manifest = airpit_manifest(audio=False)
        with self.assertRaisesRegex(ValueError, 'declares no native audio'):
            v.load_audio('unused', manifest)
        data = {'mapKey': 'arena_airpit', 'schemaVersion': 2, 'players': [], 'support': []}
        with self.assertRaisesRegex(ValueError, 'declares no native audio'):
            v.validate_audio_bundle([], 'unused', data)
        with self.assertRaisesRegex(ValueError, 'unsupported built audio map identity'):
            v.validate_audio_bundle([], 'unused', dict(data, mapKey='arena_prison'))

    def test_bundle_must_not_carry_native_audio_the_contract_does_not_declare(self):
        v.reject_undeclared_audio([mono('BallisticCollider'), SimpleNamespace(type=SimpleNamespace(name='AudioSource'))])
        for script in ('AmbientSoundPlayer', 'SeasonAmbientSoundPlayer', 'AmbientSoundPlayerGroup', 'SoundBank',
                       'BezierSpline'):
            with self.subTest(script), self.assertRaisesRegex(ValueError, 'does not declare'):
                v.reject_undeclared_audio([mono('BallisticCollider'), mono(script)])


class AudioFollowsTheContractTests(unittest.TestCase):
    """Audio is conditional on the contract: swap in a data contract and the validator follows it."""

    def with_audio_contract(self, raw_audio):
        raw = deepcopy(raw_contract())
        raw.update(map_key='arena_airpit', features=deepcopy(v.MAPS['arena_airpit']['features']))
        raw['scenes'] = {str(i): n for i, n in v.MAPS['arena_airpit']['scenes'].items()}
        raw['ballistics'] = {str(i): n for i, n in v.MAPS['arena_airpit']['ballistics'].items()}
        raw['partial_scenes'] = deepcopy(v.MAPS['arena_airpit']['partial_scenes'])
        raw['audio'] = raw_audio
        return L.parse_contract(raw, 'airpit', 'fixture.json')

    def test_item_13_style_contract_makes_audio_required(self):
        contract = self.with_audio_contract(raw_contract()['audio'])
        manifest = airpit_manifest(audio=False)
        manifest['native_features']['required'] = ['ballistics', 'ambient_audio']
        with patch.dict(v.MAPS, {'arena_airpit': contract}):
            with self.assertRaisesRegex(ValueError, 'incomplete required feature contract'):
                v.feature_enabled(manifest)  # audio now required but absent
            manifest['native_audio'] = {}
            self.assertTrue(v.feature_enabled(manifest))
            manifest['native_features']['required'] = ['ballistics']
            with self.assertRaisesRegex(ValueError, 'feature contract'):
                v.feature_enabled(manifest)

    # The fixture's two groups: (leaf name, mixer, member count), players 0-1 in the first and 2-5 in the second.
    GROUPS = (('CommonAmbientOutGroup', 'CommonAmbOutEffects', 2), ('CommonAmbientInGroup', 'CommonAmbInEffects', 4))

    def players(self, count=6):
        return [{'id': f'level9:{i}', 'path': f'Amb/{self.GROUPS[0 if i < 2 else 1][0]}/p{i}', 'enabled': True,
                 'activeInHierarchy': True, 'sourceAudio': {'Loop': False}} for i in range(count)]

    def groups(self, ids):
        return [{'kind': 'AmbientSoundPlayerGroup', 'enabled': True, 'activeInHierarchy': True, 'mixerName': mixer,
                 'path': f'Amb/{name}', 'soundPlayers': [{'resolved': i} for i in members]}
                for (name, mixer, _size), members in zip(self.GROUPS, ids)]

    def check(self, data, audio=None):
        audio = audio or L.parse_contract(raw_contract(), 'x')['audio']
        grouped = Counter(p['resolved'] for s in data['support'] for p in s.get('soundPlayers', []))
        v.check_audio_group_states(audio, data, grouped)

    def good(self):
        players = self.players()
        ids = [p['id'] for p in players]
        return {'players': players, 'support': self.groups([ids[:2], ids[2:]])}

    def test_two_groups_with_different_mixers_pass(self):
        self.check(self.good())

    def test_group_pins_are_enforced(self):
        def swap_mixers(d):
            d['support'][0]['mixerName'], d['support'][1]['mixerName'] = d['support'][1]['mixerName'], d['support'][0]['mixerName']
        def rename_group(d):
            d['support'][0]['path'] = 'Amb/CommonAmbientOutGroupRenamed'
        def move_member(d):  # coverage still holds (every player grouped once), only the pinned counts differ
            d['support'][1]['soundPlayers'].append(d['support'][0]['soundPlayers'].pop())
        def swap_members(d):  # counts preserved, the members no longer sit under their group
            out, indoor = d['support'][0]['soundPlayers'], d['support'][1]['soundPlayers']
            out[0], indoor[0] = indoor[0], out[0]
        def drop_member(d):
            d['support'][1]['soundPlayers'].pop()
        def duplicate_member(d):
            d['support'][0]['soundPlayers'].append(d['support'][1]['soundPlayers'][0])
        def inactive_player(d):
            d['players'][3]['activeInHierarchy'] = False
        def disabled_player(d):
            d['players'][3]['enabled'] = False
        def looping_player(d):
            d['players'][0]['sourceAudio']['Loop'] = True
        def disabled_group(d):
            d['support'][0]['enabled'] = False
        def inactive_group(d):
            d['support'][1]['activeInHierarchy'] = False
        for change in (swap_mixers, rename_group, move_member, swap_members, drop_member, duplicate_member,
                       inactive_player, disabled_player, looping_player, disabled_group, inactive_group):
            data = self.good()
            change(data)
            with self.subTest(change.__name__), self.assertRaises(ValueError):
                self.check(data)

    def test_group_pins_name_their_failure(self):
        for change, pattern in ((lambda d: d['support'][0].update(mixerName='CommonAmbInEffects'), 'audio group mixer routes'),
                                (lambda d: d['support'][0].update(path='Amb/Elsewhere'), 'audio group mixer routes'),
                                (lambda d: d['support'][1]['soundPlayers'].append(d['support'][0]['soundPlayers'].pop()),
                                 'audio group member counts')):
            data = self.good()
            change(data)
            with self.assertRaisesRegex(ValueError, pattern):
                self.check(data)
        data = self.good()  # two players swapped between the groups: counts agree, the hierarchy does not
        out, indoor = data['support'][0]['soundPlayers'], data['support'][1]['soundPlayers']
        out[0], indoor[0] = indoor[0], out[0]
        with self.assertRaisesRegex(ValueError, 'outside their group hierarchy'):
            self.check(data)
        data = self.good()  # two groups with one leaf name cannot be keyed
        data['support'][1]['path'] = 'Other/' + self.GROUPS[0][0]
        with self.assertRaisesRegex(ValueError, 'not unique'):
            self.check(data)

    def test_group_order_is_not_pinned(self):
        data = self.good()
        data['support'].reverse()
        self.check(data)

    def test_loops_are_pinned_by_id_and_leaf_name(self):
        raw = raw_contract()
        raw['audio']['states'].update(loops={'level9:0': 'hum'}, loop_players=['level9:0'])
        audio = L.parse_contract(raw, 'x')['audio']
        data = self.good()
        data['players'][0].update(path='Amb/CommonAmbientOutGroup/hum', sourceAudio={'Loop': True})
        self.check(data, audio)
        for name, change in (('renamed leaf', lambda d: d['players'][0].update(path='Amb/CommonAmbientOutGroup/hum2')),
                             ('loop lost', lambda d: d['players'][0].update(sourceAudio={'Loop': False})),
                             ('extra loop', lambda d: d['players'][1].update(sourceAudio={'Loop': True})),
                             ('moved loop', lambda d: (d['players'][0].update(sourceAudio={'Loop': False}),
                                                       d['players'][1].update(path='Amb/CommonAmbientOutGroup/hum',
                                                                              sourceAudio={'Loop': True})))):
            moved = self.good()
            moved['players'][0].update(path='Amb/CommonAmbientOutGroup/hum', sourceAudio={'Loop': True})
            change(moved)
            with self.subTest(name), self.assertRaisesRegex(ValueError, 'original looping audio players'):
                self.check(moved, audio)

    def test_active_coverage_with_pinned_inactive_players(self):
        raw = raw_contract()
        raw['audio']['group_coverage'] = 'active_players'
        raw['audio']['states'].update(grouped_players=5, inactive_players={'level9:5': 'far_dogs'})
        raw['audio']['states']['groups']['CommonAmbientInGroup']['players'] = 3  # the inactive player is not a member
        audio = L.parse_contract(raw, 'x')['audio']
        data = self.good()
        data['players'][5].update(activeInHierarchy=False, path='Amb/CommonAmbientInGroup/far_dogs')
        data['support'][1]['soundPlayers'].pop()  # the inactive player is not grouped
        self.check(data, audio)
        data['support'][1]['soundPlayers'].append({'resolved': 'level9:5'})  # an inactive player must not join
        with self.assertRaisesRegex(ValueError, 'active group coverage'):
            self.check(data, audio)

    def test_schema_one_style_contract_only_checks_coverage(self):
        raw = raw_contract()
        raw['audio'].update(schema=1, states=None)
        audio = L.parse_contract(raw, 'x')['audio']
        data = self.good()
        data['players'][0]['enabled'] = False  # no state pins: not examined
        self.check(data, audio)
        data['support'][1]['soundPlayers'].pop()
        with self.assertRaisesRegex(ValueError, 'audio group coverage'):
            self.check(data, audio)


# ---------------------------------------------------------------------------------------------
# Real packages (ignored work/ inputs; skipped when absent)
# ---------------------------------------------------------------------------------------------

def package_dir(name: str):
    path = REPO / 'work' / 'extract' / name
    return path if (path / 'manifest.json').is_file() else None


class AcceptedPackageTests(unittest.TestCase):
    """The accepted v5 packages still clear the manifest, scene-census and audio-index gates from data.

    These gates do not re-read the live Arena install, unlike validate_extract's source checks, so they
    are not affected by the 0.6.0 provenance drift documented in docs/progress/airpit-release.md."""

    # extra_counts() values recorded from the pre-item-3 validator on the same packages.
    EXPECTED_EXTRA = {'equator-native-v5': {'GameObject': 32, 'Transform': 32, 'AudioSource': 25},
                      'bay5-native-v5': {'GameObject': 63, 'Transform': 63, 'AudioSource': 24}}
    # Recorded from validate_native_features.extra_counts on work/extract/airpit-native-v6 (item 13).
    EXPECTED_AIRPIT_EXTRA = {'GameObject': 35, 'Transform': 35, 'AudioSource': 26}

    def test_manifest_scene_census_and_audio_index(self):
        for name, extra in self.EXPECTED_EXTRA.items():
            package = package_dir(name)
            if package is None:
                continue
            with self.subTest(package=name):
                manifest = v.read_json(package / 'manifest.json')
                scenes = v.scene_objects(package, manifest)
                self.assertTrue(v.feature_enabled(manifest, scenes))
                key, _ = v.map_contract(manifest)
                self.assertEqual(key, {'equator-native-v5': 'arena_equator', 'bay5-native-v5': 'arena_bay5'}[name])
                self.assertEqual(v.load_audio(package, manifest)['mapKey'], key)
                self.assertEqual(v.extra_counts(package, manifest), extra)
                counted = Counter(c['type'] for objects in scenes.values() for o in objects for c in o['components']
                                  if c['type'] == 'BallisticCollider')
                self.assertEqual(counted['BallisticCollider'], sum(v.MAPS[key]['ballistics'].values()))
        if all(package_dir(n) is None for n in self.EXPECTED_EXTRA):
            self.skipTest('accepted native packages not present')

    def test_superseded_airpit_v5_package_lacks_the_audio_contract_requires(self):
        package = package_dir('airpit-native-v5')
        if package is None:
            self.skipTest('work/extract/airpit-native-v5 not present')
        manifest = v.read_json(package / 'manifest.json')
        self.assertNotIn('native_audio', manifest)
        self.assertEqual(manifest['native_features']['required'], ['ballistics'])
        # Both gaps reject it: the scene set (v5 predates the furnished shops) and the missing audio contract.
        with self.assertRaisesRegex(ValueError, 'full arena_airpit scene set'):
            v.feature_enabled(manifest, v.scene_objects(package, manifest))

    def test_airpit_v6_is_the_five_scene_archive_the_shop_contract_refuses(self):
        package = package_dir('airpit-native-v6')
        if package is None:
            self.skipTest('work/extract/airpit-native-v6 not present')
        manifest = v.read_json(package / 'manifest.json')
        self.assertEqual(manifest['slice'], {})
        self.assertNotIn('partial_scenes', manifest)
        self.assertEqual(list(manifest['scenes']), list(AIRPIT_V6_SCENES.values()))
        self.assertEqual(manifest['native_features']['required'], ['ballistics', 'ambient_audio'])
        with self.assertRaisesRegex(ValueError, 'full arena_airpit scene set'):
            v.feature_enabled(manifest, v.scene_objects(package, manifest))

    def test_airpit_v7_is_the_archive_the_new_partial_keys_refuse(self):
        """v7 (first partial-scene package) predates select.exclude and twin_descendants: its partial_scenes block lacks
        the required counters, so every validator path that reads it fails instead of defaulting them to zero."""
        package = package_dir('airpit-native-v7')
        if package is None:
            self.skipTest('work/extract/airpit-native-v7 not present')
        manifest = v.read_json(package / 'manifest.json')
        self.assertEqual(manifest['slice'], {})
        self.assertEqual(sorted(manifest['partial_scenes']), sorted(AIRPIT_SHOP_SCENES.values()))
        for name, entry in manifest['partial_scenes'].items():
            with self.subTest(name):
                self.assertFalse(set(NEW_PARTIAL_KEYS) & set(entry))
                self.assertNotIn('twin_descendant_paths', entry)
                self.assertFalse({'excluded', 'descendants_added'} & {k for counts in entry['rules'].values() for k in counts})
        with self.assertRaisesRegex(partial_scope.PartialScopeError, 'lacks keys'):
            partial_scope.manifest_entries(manifest)
        with self.assertRaisesRegex(ValueError, 'lacks keys'):
            v.map_contract(manifest)
        with self.assertRaisesRegex(ValueError, 'lacks keys'):
            v.feature_enabled(manifest)

    def test_airpit_native_package_when_present(self):
        package = package_dir('airpit-native-v9')
        if package is None:
            self.skipTest('work/extract/airpit-native-v9 not present (furnished shops + the crates under the tarp)')
        manifest = v.read_json(package / 'manifest.json')
        self.assertEqual(manifest['slice'], {})
        self.assertEqual(sorted(manifest['partial_scenes']), sorted(AIRPIT_SHOP_SCENES.values()))
        scenes = v.scene_objects(package, manifest)
        self.assertTrue(v.feature_enabled(manifest, scenes))
        self.assertEqual(v.map_contract(manifest)[0], 'arena_airpit')
        self.assertEqual(manifest['native_features']['required'], ['ballistics', 'ambient_audio'])
        self.assertEqual({k: manifest['native_audio'][k] for k in ('file', 'schema_version', 'players', 'banks', 'clips')},
                         {'file': 'audio/native-audio.json', 'schema_version': 2, 'players': 26, 'banks': 9, 'clips': 61})
        data = v.load_audio(package, manifest)
        self.assertEqual((data['mapKey'], data['schemaVersion'], data['sourceScene']), ('arena_airpit', 2, 'level39'))
        groups = [s for s in data['support'] if s['kind'] == 'AmbientSoundPlayerGroup']
        # The Out group routes four players to CommonAmbOutEffects, the In group 22 to CommonAmbInEffects.
        self.assertEqual([(g['path'].rsplit('/', 1)[-1], g['mixerName'], len(g['soundPlayers'])) for g in groups],
                         [('CommonAmbientOutGroup', 'CommonAmbOutEffects', 4), ('CommonAmbientInGroup', 'CommonAmbInEffects', 22)])
        self.assertEqual(sum(p['volume'] < p['deltaVolume'] for p in data['players']), 0)  # no quiet-source silencing
        self.assertEqual(v.extra_counts(package, manifest), self.EXPECTED_AIRPIT_EXTRA)


if __name__ == '__main__':
    unittest.main()
