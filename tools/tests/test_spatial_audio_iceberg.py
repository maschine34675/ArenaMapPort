"""The two reviewed acoustic rules Iceberg needs (PLAN-ICEBERG item 11, docs/progress/iceberg-release.md "Spatial acoustics").

1. Mirrored trigger volumes: ten AudioTriggerArea nodes of Arena's Sanatorium sound scene carry a negative local scale. The
   exporter accepts that sign on exactly one node class (``mirrored_volume_nodes``) and only where Arena's own records agree.
2. Duplicated portal IDs: 533 of 17,751 baked routes use the portal ID 66 for a second portal. ``repair_portal_ids`` rewrites
   the ID under a reviewed, pinned rule; every other map, and every other failure, keeps the strict check.

Synthetic controls first (they run everywhere), then the real data (skipped when the Arena install or the extracted packages are
not reachable from this machine).
"""
import copy
import hashlib
import json
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/extract'))
sys.path.insert(0, str(ROOT / 'tools/tests'))
import extract_spatial_audio as S  # noqa: E402
import spatial_audio_bake as B  # noqa: E402
from test_spatial_audio import graph as synthetic_graph  # noqa: E402

ARENA_SOURCE = str(S.ROOT)
ORIGINAL_BAKE = Path(ARENA_SOURCE) / 'StreamingAssets/AudioBakeData/arena_sanatorium_sound.audiobakedata'
RELEASED = ('arena_equator', 'arena_bay5', 'arena_airpit', 'arena_bowl')


def spatial_base():
    """The extracted packages: ARENAMAPPORT_SPATIAL_AUDIO_DIR, else work/extract/spatial-audio of this checkout (ignored by git)."""
    override = os.environ.get('ARENAMAPPORT_SPATIAL_AUDIO_DIR')
    base = Path(override) if override else ROOT / 'work/extract/spatial-audio'
    return base if base.is_dir() else None


# ---------------------------------------------------------------------------------------------------------------------
# 1. Mirrored trigger volumes
# ---------------------------------------------------------------------------------------------------------------------

def mirrored_graph(scale=(-1, 1, 1)):
    """The synthetic two-room graph with room 1 re-pointed at a leaf area node (5, child of the room node) that is mirrored.
    The recorded OBB carries Arena's signed extent (lossyScale x size / 2), the room bounds are the ones Unity computed."""
    d = synthetic_graph()
    vec = lambda x, y, z: dict(x=x, y=y, z=z)
    d['nodes'].append(dict(id=5, parent_id=1, name='AudioTriggerArea', active_self=True, layer=13, position=vec(0, 0, 0),
                           rotation=dict(x=0, y=0, z=0, w=1), scale=vec(*scale)))
    d['colliders'].append(dict(id=14, node_id=5, enabled=True, is_trigger=True, center=vec(0, 0, 0), size=vec(2, 2, 2)))
    d['areas'].append(dict(id=23, node_id=5, enabled=True, active_in_hierarchy=True, collider_id=14))
    room = d['rooms'][0]
    room['area_ids'] = [23]
    room['source_area_obbs'] = [dict(center=[0, 0, 0], extents=[float(s) for s in scale])]
    room['bounds'] = dict(center=[0, 0, 0], extents=[1, 1, 1])
    return d


class MirroredVolumeTests(unittest.TestCase):
    def reject(self, mutation, text, base=None):
        d = base if base is not None else mirrored_graph()
        mutation(d)
        with self.assertRaisesRegex((ValueError, KeyError), text):
            S.validate_graph(d)

    def test_the_unmirrored_control_still_passes_and_a_mirrored_volume_is_accepted(self):
        S.validate_graph(synthetic_graph())
        S.validate_graph(mirrored_graph((1, 1, 1)))
        S.validate_graph(mirrored_graph())

    def test_every_sign_pattern_is_accepted_when_the_record_carries_the_same_signs(self):
        for scale in ((-1, 1, 1), (1, -1, 1), (1, 1, -1), (-1, -1, 1), (-1, -1, -1)):
            with self.subTest(scale=scale):
                S.validate_graph(mirrored_graph(scale))

    def test_the_number_of_mirrored_volumes_is_pinned_per_map(self):
        # validate_graph judges each mirrored node; the count the review accepted is a separate pin, so a volume that an Arena update
        # mirrors (or un-mirrors) stops the export of that map. A map that is not listed must have none.
        self.assertEqual(S.REVIEWED_MIRRORED_VOLUMES, {'arena_iceberg': 10})
        control = synthetic_graph()
        S.check_reviewed_mirrored_volumes(control)                                    # no mirrored node, no rule: fine
        mirrored = mirrored_graph()
        with self.assertRaisesRegex(ValueError, 'has 1 mirrored volume.*accepted 0'):
            S.check_reviewed_mirrored_volumes(mirrored)                               # a released map that suddenly has one
        S.check_reviewed_mirrored_volumes(mirrored, {mirrored['map_key']: 1})
        with self.assertRaisesRegex(ValueError, 'has 1 mirrored volume.*accepted 2'):
            S.check_reviewed_mirrored_volumes(mirrored, {mirrored['map_key']: 2})
        with self.assertRaisesRegex(ValueError, 'has 0 mirrored volume.*accepted 1'):
            S.check_reviewed_mirrored_volumes(control, {control['map_key']: 1})
        for key in RELEASED:
            self.assertNotIn(key, S.REVIEWED_MIRRORED_VOLUMES)

    def test_the_rule_names_exactly_the_leaf_area_node(self):
        self.assertEqual(S.mirrored_volume_nodes(mirrored_graph()), {5})
        # In the plain synthetic graph every area node also carries its room, so nothing may be mirrored there.
        self.assertEqual(S.mirrored_volume_nodes(synthetic_graph()), set())

    def test_a_record_with_the_other_sign_is_rejected(self):
        # Arena says +1 on X, the hierarchy mirrors X: the volumes disagree, so the sign is not trusted.
        self.reject(lambda d: d['rooms'][0].update(source_area_obbs=[dict(center=[0, 0, 0], extents=[1, 1, 1])]), 'OBB')
        # ... and the other way round: Arena mirrors Z, the node does not.
        d = mirrored_graph()
        d['rooms'][0]['source_area_obbs'] = [dict(center=[0, 0, 0], extents=[-1, 1, 1])]
        d['nodes'][4]['scale'] = dict(x=1, y=1, z=-1)
        with self.assertRaisesRegex(ValueError, 'OBB'):
            S.validate_graph(d)

    def test_room_bounds_that_differ_from_the_mirrored_boxes_are_rejected(self):
        self.reject(lambda d: d['rooms'][0]['bounds'].update(extents=[2, 1, 1]), 'mirrored room bounds')
        self.reject(lambda d: d['rooms'][0]['bounds'].update(center=[0.5, 0, 0]), 'mirrored room bounds')

    def test_a_mirrored_axis_must_not_carry_a_centre_offset(self):
        # With a centre of 0 on the mirrored axis the reflection maps the box onto itself; with an offset the sign would move it.
        def offset_on_the_mirrored_axis(d):
            d['colliders'][3]['center']['x'] = 0.5  # collider 14, the mirrored volume's
            d['rooms'][0]['source_area_obbs'] = [dict(center=[-0.5, 0, 0], extents=[-1, 1, 1])]
            d['rooms'][0]['bounds'] = dict(center=[-0.5, 0, 0], extents=[1, 1, 1])
        self.reject(offset_on_the_mirrored_axis, 'off its pivot')
        # An offset on an axis that is NOT mirrored is an ordinary box offset and stays valid.
        d = mirrored_graph()
        d['colliders'][3]['center']['y'] = 0.5
        d['rooms'][0]['source_area_obbs'] = [dict(center=[0, 0.5, 0], extents=[-1, 1, 1])]
        d['rooms'][0]['bounds'] = dict(center=[0, 0.5, 0], extents=[1, 1, 1])
        S.validate_graph(d)

    def test_a_mirrored_node_with_a_child_is_rejected(self):
        def add_child(d):
            d['nodes'].append(dict(id=6, parent_id=5, name='child', active_self=True, layer=13, position=dict(x=0, y=0, z=0),
                                   rotation=dict(x=0, y=0, z=0, w=1), scale=dict(x=1, y=1, z=1)))
        self.reject(add_child, 'invalid transform')

    def test_a_mirrored_node_that_carries_a_room_or_a_portal_or_the_group_is_rejected(self):
        for node_index, label in ((0, 'room node'), (2, 'portal node'), (3, 'group and system node')):
            with self.subTest(node=label):
                self.reject(lambda d, i=node_index: d['nodes'][i]['scale'].update(x=-1), 'invalid transform')

    def test_an_ordinary_node_is_still_refused_a_negative_scale(self):
        d = synthetic_graph()
        d['nodes'][1]['scale']['y'] = -1
        with self.assertRaisesRegex(ValueError, 'invalid transform'):
            S.validate_graph(d)

    def test_a_mirrored_volume_outside_every_active_room_is_rejected(self):
        self.reject(lambda d: d['rooms'][0].update(area_ids=[21]), 'without source OBB')

    def test_a_flat_mirrored_volume_is_rejected(self):
        def flatten(d):
            d['nodes'][4]['scale'].update(y=0)
            d['rooms'][0]['source_area_obbs'] = [dict(center=[0, 0, 0], extents=[-1, 0, 1])]
        self.reject(flatten, 'OBB')

    def test_a_mirrored_node_still_needs_a_unit_rotation_and_finite_numbers(self):
        self.reject(lambda d: d['nodes'][4]['rotation'].update(w=2), 'invalid transform')
        self.reject(lambda d: d['nodes'][4]['position'].update(x=float('nan')), 'nonfinite')

    def test_the_graph_rule_stays_the_only_place_that_names_the_exception(self):
        text = (ROOT / 'tools/extract/extract_spatial_audio.py').read_text(encoding='utf-8')
        self.assertEqual(text.count('nid not in mirrored'), 1)
        self.assertIn('def mirrored_volume_nodes(data):', text)


# ---------------------------------------------------------------------------------------------------------------------
# 2. Duplicated portal IDs in the baked routes
# ---------------------------------------------------------------------------------------------------------------------

def build_bake(pairs):
    """A consistent bake file from [(key, first_room, second_room, [(portal_ids, cost, distance), ...]), ...]."""
    p = struct.pack
    out = p('<i', len(pairs))
    for key, a, b, routes in pairs:
        out += p('<IIhhi', key, key, a, b, len(routes))
        for ids, cost, dist in routes:
            out += p('<i', len(ids)) + b''.join(p('<h', x) for x in ids) + p('<ff', cost, dist)
        out += p('<iB', sum(len(r[0]) for r in routes), min(len(r[0]) for r in routes))
    aware = {}
    for key, a, b, _ in pairs:
        aware.setdefault(a, []).append(key)
        aware.setdefault(b, []).append(key)
    out += p('<i', len(aware))
    for room, keys in sorted(aware.items()):
        out += p('<hi', room, len(keys)) + b''.join(p('<I', k) for k in keys)
    out += p('<i', len(pairs))
    for key, a, b, routes in pairs:
        out += p('<Ii', key, len(routes))
        start = node = 0
        for ids, cost, dist in routes:
            out += p('<hfHH', len(ids), cost, start, node)
            start += len(ids)
            node += len(ids) + 2
    out += p('<6iII', 3, 3, 3, 100, 100, 100, max(len(x[3]) for x in pairs), max(sum(len(r[0]) for r in x[3]) for x in pairs))
    return out


def route_graph(portals, bake):
    """The minimal graph parse_bake needs: {portal_id: (room_a, room_b)} plus the counts of ``bake``."""
    rooms = sorted({r for pair in portals.values() for r in pair})
    stats = B.parse_bake(bake)
    info = dict(rooms_count=len(rooms), portals_count=len(portals), room_pairs_count=stats['room_pairs_count'],
                routes_count=stats['routes_count'], max_routes_count=stats['max_routes_count'],
                max_route_portals_count=stats['max_route_portals_count'], bake_settings=stats['bake_settings'])
    return dict(
        rooms=[dict(id=100 + r, room_id=r, enabled=True, active_in_hierarchy=True) for r in rooms],
        portals=[dict(id=200 + pid, portal_id=pid, enabled=True, active_in_hierarchy=True,
                      connected_room_ids=[100 + a, 100 + b]) for pid, (a, b) in portals.items()],
        systems=[dict(location_info=info)])


# Iceberg in miniature: portal 66 joins rooms 1 and 2 (the shipped portal); the bake also uses 66 for the doorway 2-3 that the
# shipped scene knows as portal 30.
PORTALS = {66: (1, 2), 30: (2, 3), 5: (3, 4)}


def stale_pairs():
    c = lambda n: (n * 0.5, n * 2.0)
    return [
        (10, 1, 2, [([66], *c(1))]),
        (11, 2, 3, [([30], *c(1)), ([66], *c(1))]),
        (12, 1, 3, [([66, 30], *c(2)), ([66, 66], *c(2))]),
        (13, 1, 4, [([66, 30, 5], *c(3)), ([66, 66, 5], *c(3))]),
        (14, 3, 4, [([5], *c(1))]),
    ]


EXPECTED_FIXED = [
    (10, 1, 2, [([66], .5, 2.0)]),
    (11, 2, 3, [([30], .5, 2.0), ([30], .5, 2.0)]),
    (12, 1, 3, [([66, 30], 1.0, 4.0), ([66, 30], 1.0, 4.0)]),
    (13, 1, 4, [([66, 30, 5], 1.5, 6.0), ([66, 30, 5], 1.5, 6.0)]),
    (14, 3, 4, [([5], .5, 2.0)]),
]


class RouteRepairTests(unittest.TestCase):
    def setUp(self):
        self.bake = build_bake(stale_pairs())
        self.graph = route_graph(PORTALS, self.bake)

    def test_the_synthetic_fixture_is_a_well_formed_bake_that_fails_only_the_route_check(self):
        with self.assertRaises(B.RouteGraphError):
            B.parse_bake(self.bake, self.graph)
        # The same file against a graph whose portal 66 joins the other rooms passes, so only the pairing is wrong.
        B.parse_bake(build_bake(EXPECTED_FIXED), route_graph(PORTALS, build_bake(EXPECTED_FIXED)))

    def test_route_graph_error_is_a_value_error_with_the_old_messages(self):
        self.assertTrue(issubclass(B.RouteGraphError, ValueError))
        with self.assertRaisesRegex(ValueError, 'route does not reach pair endpoint|route is disconnected from source graph'):
            B.parse_bake(self.bake, self.graph)

    def test_scan_routes_finds_every_route_at_its_byte_offset(self):
        routes = B.scan_routes(self.bake)
        self.assertEqual(len(routes), B.parse_bake(self.bake)['routes_count'])
        for key, a, b, index, offset, ids in routes:
            self.assertEqual([struct.unpack_from('<h', self.bake, offset + 2 * i)[0] for i in range(len(ids))], ids)
        self.assertEqual([r[5] for r in routes if r[0] == 12], [[66, 30], [66, 66]])

    def test_the_repair_rewrites_exactly_the_failing_positions(self):
        fixed, report = B.repair_portal_ids(self.bake, self.graph, (66,))
        self.assertEqual(fixed, build_bake(EXPECTED_FIXED))
        self.assertEqual(len(fixed), len(self.bake))
        self.assertEqual(sum(1 for x, y in zip(self.bake, fixed) if x != y), 3)  # one low byte per rewritten ID
        self.assertEqual((report['routes_repaired'], report['ids_rewritten'], report['rewrites']), (3, 3, {'66->30': 3}))
        self.assertEqual(report['portal_ids'], [66])
        self.assertEqual(report['original'], dict(bytes=len(self.bake), sha256=hashlib.sha256(self.bake).hexdigest()))
        self.assertEqual(report['repaired'], dict(bytes=len(fixed), sha256=hashlib.sha256(fixed).hexdigest()))
        # The unchanged strict check accepts the result.
        self.assertEqual(B.parse_bake(fixed, self.graph)['routes_count'], 8)

    def test_the_repair_is_idempotent_and_leaves_a_clean_file_alone(self):
        fixed, _ = B.repair_portal_ids(self.bake, self.graph, (66,))
        again, report = B.repair_portal_ids(fixed, self.graph, (66,))
        self.assertEqual(again, fixed)
        self.assertEqual((report['routes_repaired'], report['ids_rewritten'], report['rewrites']), (0, 0, {}))

    def test_an_id_the_rule_does_not_name_is_never_rewritten(self):
        for named in ((30,), (5,), (99,)):
            with self.subTest(named=named), self.assertRaisesRegex(B.RouteGraphError, 'no reading'):
                B.repair_portal_ids(self.bake, self.graph, named)

    def test_the_rule_needs_at_least_one_named_id(self):
        with self.assertRaisesRegex(ValueError, 'no portal ID'):
            B.repair_portal_ids(self.bake, self.graph, ())

    def test_parallel_doorways_are_a_guess_and_fail_closed(self):
        ports = dict(PORTALS)
        ports[31] = (2, 3)  # a second portal joins rooms 2 and 3: the substitution is no longer unique
        bake = build_bake(stale_pairs())
        graph = route_graph(ports, bake)
        with self.assertRaisesRegex(B.RouteGraphError, 'no reading'):
            B.repair_portal_ids(bake, graph, (66,))

    def test_equally_cheap_readings_fail_closed(self):
        ports = {66: (1, 2), 30: (2, 3), 40: (2, 4), 31: (3, 5), 41: (4, 5)}
        bake = build_bake([(10, 1, 5, [([66, 66, 66], 1.5, 6.0)]), (11, 1, 2, [([66], .5, 2.0)]), (12, 2, 3, [([30], .5, 2.0)]),
                           (13, 2, 4, [([40], .5, 2.0)]), (14, 3, 5, [([31], .5, 2.0)]), (15, 4, 5, [([41], .5, 2.0)])])
        graph = route_graph(ports, bake)
        with self.assertRaisesRegex(B.RouteGraphError, 'equally cheap'):
            B.repair_portal_ids(bake, graph, (66,))

    def test_a_route_that_cannot_reach_its_end_fails_closed(self):
        bake = build_bake([(10, 1, 4, [([66], .5, 2.0)]), (11, 3, 4, [([5], .5, 2.0)]), (12, 2, 3, [([30], .5, 2.0)])])
        graph = route_graph(PORTALS, bake)
        with self.assertRaisesRegex(B.RouteGraphError, 'no reading'):
            B.repair_portal_ids(bake, graph, (66,))

    def test_a_malformed_bake_stays_a_plain_value_error(self):
        with self.assertRaises(ValueError):
            B.repair_portal_ids(self.bake[:-5], self.graph, (66,))

    def test_route_follows_graph_matches_parse_bake(self):
        links = B.portal_room_links(self.graph)
        self.assertEqual(links, {66: (1, 2), 30: (2, 3), 5: (3, 4)})
        verdicts = {(key, tuple(ids)): B.route_follows_graph(links, a, b, ids) for key, a, b, _, _, ids in B.scan_routes(self.bake)}
        self.assertEqual([k for k, ok in verdicts.items() if not ok], [(11, (66,)), (12, (66, 66)), (13, (66, 66, 5))])


class ReviewedRuleTests(unittest.TestCase):
    def setUp(self):
        self.bake = build_bake(stale_pairs())
        self.graph = route_graph(PORTALS, self.bake)
        self.fixed, self.report = B.repair_portal_ids(self.bake, self.graph, (66,))
        self.rule = {'portal_ids': (66,), 'original_sha256': hashlib.sha256(self.bake).hexdigest(), 'routes_repaired': 3,
                     'rewrites': {'66->30': 3}, 'repaired_sha256': hashlib.sha256(self.fixed).hexdigest()}

    def test_only_iceberg_has_a_reviewed_rule(self):
        self.assertEqual(set(S.REVIEWED_BAKE_REPAIRS), {'arena_iceberg'})
        rule = S.REVIEWED_BAKE_REPAIRS['arena_iceberg']
        self.assertEqual(rule['portal_ids'], (66,))
        self.assertEqual((rule['routes_repaired'], rule['rewrites']), (533, {'66->30': 533}))
        for digest in (rule['original_sha256'], rule['repaired_sha256']):
            self.assertRegex(digest, r'^[0-9a-f]{64}$')
        self.assertNotEqual(rule['original_sha256'], rule['repaired_sha256'])

    def test_a_pinned_rule_repairs_and_reports(self):
        shipped, parsed, report = S.parse_or_repair_bake('arena_x', self.bake, self.graph, {'arena_x': self.rule})
        self.assertEqual(shipped, self.fixed)
        self.assertEqual(parsed['sha256'], hashlib.sha256(self.fixed).hexdigest())
        self.assertEqual(report, self.report)

    def test_a_clean_bake_is_passed_through_untouched(self):
        shipped, parsed, report = S.parse_or_repair_bake('arena_x', self.fixed, self.graph, {'arena_x': self.rule})
        self.assertEqual(shipped, self.fixed)
        self.assertIsNone(report)
        self.assertEqual(parsed['routes_count'], 8)

    def test_a_map_without_a_rule_keeps_the_strict_check(self):
        for rules in ({}, {'arena_other': self.rule}):
            with self.subTest(rules=list(rules)), self.assertRaises(B.RouteGraphError):
                S.parse_or_repair_bake('arena_x', self.bake, self.graph, rules)
        for key in RELEASED:  # the real table: no released map has a rule
            with self.subTest(map=key), self.assertRaises(B.RouteGraphError):
                S.parse_or_repair_bake(key, self.bake, self.graph)

    def test_a_different_bake_stops_until_it_is_reviewed_again(self):
        other = build_bake(stale_pairs()[:4] + [(14, 3, 4, [([5], .75, 2.0)])])
        graph = route_graph(PORTALS, other)
        with self.assertRaisesRegex(ValueError, 'not the one the route repair was reviewed for'):
            S.parse_or_repair_bake('arena_x', other, graph, {'arena_x': self.rule})

    def test_a_different_outcome_stops_too(self):
        for change in ({'routes_repaired': 4}, {'rewrites': {'66->31': 3}}, {'repaired_sha256': '0' * 64}):
            with self.subTest(change=change), self.assertRaisesRegex(ValueError, 'other than the reviewed one'):
                S.parse_or_repair_bake('arena_x', self.bake, self.graph, {'arena_x': {**self.rule, **change}})

    def test_failures_other_than_the_route_pairing_are_never_repaired(self):
        graph = copy.deepcopy(self.graph)
        graph['systems'][0]['location_info']['routes_count'] += 1  # a count mismatch is not a route problem
        with self.assertRaisesRegex(ValueError, 'location info') as raised:
            S.parse_or_repair_bake('arena_x', self.fixed, graph, {'arena_x': self.rule})
        self.assertNotIsInstance(raised.exception, B.RouteGraphError)
        with self.assertRaises(ValueError) as raised:
            S.parse_or_repair_bake('arena_x', self.bake[:-5], self.graph, {'arena_x': self.rule})
        self.assertNotIsInstance(raised.exception, B.RouteGraphError)

    def test_main_reports_a_repaired_bake_and_stays_silent_for_a_clean_one(self):
        import contextlib
        import io
        from unittest.mock import patch
        base = {"bake": {"bytes": 1, "room_pairs_count": 2, "routes_count": 3, "sha256": "x"}, "source_scene": "level259",
                "rooms": [1], "portals": [1], "areas": [], "clips": [1], "mixer_effects": {"a": 1}}
        repaired = dict(base, bake_repair={"rule": "duplicated-portal-id", "routes_repaired": 533, "rewrites": {"66->30": 533},
                                           "original": {"sha256": "ignored"}})
        outputs = []
        for result in (base, repaired):
            out = io.StringIO()
            with tempfile.TemporaryDirectory() as tmp, patch.object(S, "read_build_scenes", return_value=["x"]), \
                    patch.object(S, "verify_scene_mapping"), patch.object(S, "export", return_value=result), \
                    contextlib.redirect_stdout(out):
                self.assertEqual(S.main(["--map", "arena_iceberg", "--source", tmp, "--output", tmp]), 0)
            outputs.append(out.getvalue())
        self.assertNotIn("bake_repair", outputs[0])
        self.assertIn('"routes_repaired": 533', outputs[1])
        self.assertIn('"66->30": 533', outputs[1])

    def test_export_records_the_repair_beside_the_bake_without_touching_its_keys(self):
        text = (ROOT / 'tools/extract/extract_spatial_audio.py').read_text(encoding='utf-8')
        self.assertIn("bake,result['bake'],repair=parse_or_repair_bake(key,bake,result)", text)
        self.assertIn("if repair:result['bake_repair']=repair", text)
        # The shipped bytes are the repaired ones, written under the unchanged name every staging rule knows.
        self.assertIn("(OUT/'source.audiobakedata').write_bytes(bake)", text)
        self.assertIn("result['bake']['file']='source.audiobakedata'", text)


# ---------------------------------------------------------------------------------------------------------------------
# 3. Real data
# ---------------------------------------------------------------------------------------------------------------------

@unittest.skipUnless(Path(ARENA_SOURCE).is_dir() and ORIGINAL_BAKE.is_file(), 'Arena source not reachable from this machine')
class IcebergLiveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.data = S.export(ARENA_SOURCE, cls.tmp.name, 'arena_iceberg')
        cls.out = Path(cls.tmp.name)
        cls.shipped = json.loads((cls.out / 'spatial-audio.json').read_text(encoding='utf-8'))

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_the_package_has_the_documented_shape(self):
        d = self.data
        self.assertEqual(d['source_scene'], 'level259')
        self.assertEqual((len(d['rooms']), len(d['portals']), len(d['areas']), len(d['colliders']), len(d['clips'])), (46, 89, 120, 209, 1))
        self.assertEqual([c['name'] for c in d['clips']], ['amb_sanatorium_indoor_tone_big'])
        self.assertTrue(d['mixer_effects'])
        self.assertEqual(sorted(p.name for p in self.out.iterdir()), ['sharedassets259.assets_6.wav', 'source.audiobakedata', 'spatial-audio.json'])

    def test_the_shipped_graph_passes_the_validator_again(self):
        S.validate_graph(self.shipped)

    def test_ten_mirrored_volumes_in_two_rooms_are_accepted(self):
        nodes = {n['id']: n for n in self.data['nodes']}
        mirrored = [n for n in self.data['nodes'] if any(v < 0 for v in n['scale'].values())]
        self.assertEqual(len(mirrored), 10)
        self.assertEqual(S.mirrored_volume_nodes(self.data) & {n['id'] for n in mirrored}, {n['id'] for n in mirrored})
        areas = {a['node_id']: a['id'] for a in self.data['areas']}
        rooms = {r['room_id'] for r in self.data['rooms'] if any(areas.get(n['id']) in r['area_ids'] for n in mirrored)}
        self.assertEqual(rooms, {5, 35})  # corridor_2_1 and coffe_hall
        cols = {c['node_id']: c for c in self.data['colliders']}
        for n in mirrored:  # one mirrored axis each, a normalised (yaw) rotation, nothing below the node, a box centred on its pivot
            self.assertEqual(sum(v < 0 for v in n['scale'].values()), 1)
            self.assertEqual(cols[n['id']]['center'], dict(x=0, y=0, z=0))
            self.assertEqual(cols[n['id']]['size'], dict(x=1, y=1, z=1))
            self.assertAlmostEqual(sum(v * v for v in n['rotation'].values()), 1.0, places=3)
            self.assertFalse([m for m in nodes.values() if m['parent_id'] == n['id']])

    def test_arenas_own_records_carry_the_sign_and_the_room_bounds(self):
        # Every mirrored area's cached extent has the sign of its node's scale (the validator compares it), and the cached bounds of
        # the two rooms equal the union of the mirrored boxes' AABBs: checked by the validator, restated here as numbers.
        rooms = {r['room_id']: r for r in self.data['rooms']}
        self.assertEqual([round(x, 1) for x in rooms[35]['bounds']['extents']], [16.7, 2.2, 11.7])
        self.assertEqual([round(x, 1) for x in rooms[5]['bounds']['extents']], [5.3, 2.6, 10.0])

    def test_the_original_bake_is_the_reviewed_one_and_fails_exactly_533_routes(self):
        original = ORIGINAL_BAKE.read_bytes()
        rule = S.REVIEWED_BAKE_REPAIRS['arena_iceberg']
        self.assertEqual(hashlib.sha256(original).hexdigest(), rule['original_sha256'])
        self.assertEqual(len(original), 622301)
        with self.assertRaises(B.RouteGraphError):
            B.parse_bake(original, self.data)
        links = B.portal_room_links(self.data)
        routes = B.scan_routes(original)
        failing = [r for r in routes if not B.route_follows_graph(links, r[1], r[2], r[5])]
        self.assertEqual((len(routes), len(failing)), (17751, 533))
        self.assertTrue(all(66 in r[5] for r in failing))  # every failing route passes through portal 66
        self.assertEqual(len({r[0] for r in failing}), 157)  # in 157 of the 1,003 room pairs

    def test_portal_66_has_two_meanings_and_portal_30_is_the_only_other_doorway(self):
        links = B.portal_room_links(self.data)
        self.assertEqual(sorted(links[66]), [24, 27])  # the shipped scene: elevators <-> boiler (2)
        self.assertEqual([pid for pid, rooms in links.items() if sorted(rooms) == [11, 27]], [30])  # elevators <-> boiler
        routes = B.scan_routes(ORIGINAL_BAKE.read_bytes())
        as_shipped = as_second = 0
        for key, a, b, index, offset, ids in routes:
            if 66 not in ids:
                continue
            if B.route_follows_graph(links, a, b, ids):
                as_shipped += 1
            else:
                as_second += 1
        self.assertEqual((as_shipped, as_second), (343, 533))

    def test_the_shipped_bake_is_the_repair_of_the_original(self):
        rule = S.REVIEWED_BAKE_REPAIRS['arena_iceberg']
        shipped = (self.out / 'source.audiobakedata').read_bytes()
        original = ORIGINAL_BAKE.read_bytes()
        self.assertEqual(hashlib.sha256(shipped).hexdigest(), rule['repaired_sha256'])
        self.assertEqual(len(shipped), len(original))
        self.assertEqual(sum(1 for x, y in zip(original, shipped) if x != y), 533)
        stats = B.parse_bake(shipped, self.data)  # the unchanged strict check
        self.assertEqual((stats['routes_count'], stats['room_pairs_count']), (17751, 1003))
        self.assertEqual({k: v for k, v in self.data['bake'].items() if k != 'file'}, stats)
        self.assertEqual(self.data['bake_repair']['original']['sha256'], rule['original_sha256'])
        self.assertEqual(self.data['bake_repair']['rewrites'], {'66->30': 533})
        self.assertEqual(self.data['bake_repair']['routes_repaired'], 533)
        self.assertEqual(self.shipped['bake_repair'], self.data['bake_repair'])
        self.assertEqual(self.shipped['bake']['sha256'], rule['repaired_sha256'])

    def test_the_export_is_deterministic(self):
        with tempfile.TemporaryDirectory() as second:
            S.export(ARENA_SOURCE, second, 'arena_iceberg')
            for name in ('spatial-audio.json', 'source.audiobakedata', 'sharedassets259.assets_6.wav'):
                self.assertEqual((Path(second) / name).read_bytes(), (self.out / name).read_bytes(), name)

    def test_every_other_map_keeps_the_strict_check_on_the_iceberg_bake(self):
        # The same file offered to another map key is refused: the rule belongs to Iceberg's reviewed bake alone.
        for key in RELEASED:
            with self.subTest(map=key), self.assertRaises(B.RouteGraphError):
                S.parse_or_repair_bake(key, ORIGINAL_BAKE.read_bytes(), self.data)


@unittest.skipUnless(spatial_base() is not None, 'source-derived packages are local only')
class ReleasedPackagesUnchangedTests(unittest.TestCase):
    """The four released maps need neither rule: their packages validate and parse as before, carry no repair record and no
    mirrored node, and the validator and the bake reader still agree with every recorded number."""

    def test_every_released_package_validates_with_the_strict_bake_check(self):
        base = spatial_base()
        for key in RELEASED:
            if not (base / key / 'spatial-audio.json').is_file():
                continue
            with self.subTest(map=key):
                d = json.loads((base / key / 'spatial-audio.json').read_text(encoding='utf-8'))
                S.validate_graph(d)
                self.assertNotIn('bake_repair', d)
                self.assertFalse([n for n in d['nodes'] if any(v < 0 for v in n['scale'].values())])
                self.assertEqual(S.mirrored_volume_nodes(d) & {n['id'] for n in d['nodes'] if any(v < 0 for v in n['scale'].values())}, set())
                self.assertEqual(B.parse_bake((base / key / 'source.audiobakedata').read_bytes(), d), {k: v for k, v in d['bake'].items() if k != 'file'})

    def test_the_released_graphs_are_unchanged_by_the_signed_extent_comparison(self):
        # For a node without a negative scale the signed and the unsigned comparison are the same, so a graph of a released map
        # fails this rule exactly where it failed before: shifting one recorded extent by 0.01 m is still caught.
        base = spatial_base()
        key = next((k for k in RELEASED if (base / k / 'spatial-audio.json').is_file()), None)
        if key is None:
            self.skipTest('no released package')
        d = json.loads((base / key / 'spatial-audio.json').read_text(encoding='utf-8'))
        room = next(r for r in d['rooms'] if r['enabled'] and r['active_in_hierarchy'])
        room['source_area_obbs'][0]['extents'][0] += 0.01
        with self.assertRaisesRegex(ValueError, 'OBB'):
            S.validate_graph(d)


if __name__ == '__main__':
    unittest.main()
