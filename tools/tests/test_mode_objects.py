"""PLAN-AIRPIT item 4 -- mode-object sidecar extractor (tools/extract/extract_mode_objects.py).

TeamBoundRuleTests / StageRecordDecodeTests / StageGraphTests / IcebergSidecarTests (PLAN-ICEBERG item 7, 2026-10-30): the
per-map team rule that gives Iceberg (70 markers, 12 bounds, 10 against 10 only in the pockets) sides by team and a stage
table raw-decoded from Arena, while every released sidecar stays byte-identical.

SideRuleTests / BowlSidecarTests (PLAN-BOWL item 5, 2026-10-18): the pocket rule that gives Bowl, whose two
pockets both lie at z < 0, two balanced sides while the released maps keep the sign-of-z rule byte for byte.

Synthetic scene JSON throughout (parent chains, scales, rotations, naming quirks), the same
object shape extract_map.py / extract_equator.py write. The last class cross-checks the
git-tracked tools/maps/*.modes.json against the ignored local packages when those exist; it
skips on a machine without them, like the other source-derived checks in this suite.
"""
from __future__ import annotations

import contextlib
import io
import json
import math
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
# The ignored work/ folder and the installed Arena. A second checkout of the repository has no work/ of its own, so
# a developer points ARENAMAPPORT_WORK_DIR at the main checkout's (never a link); every check that needs them skips
# when the input is absent.
WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR") or ROOT / "work")
ARENA_DATA = Path(os.environ.get("ARENAMAPPORT_ARENA_DATA")
                  or "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data")
sys.path.insert(0, str(ROOT / "tools" / "extract"))
import extract_mode_objects as M  # noqa: E402
from common_ids import HardExtractError  # noqa: E402


def yaw_quat(degrees):
    half = math.radians(degrees) / 2.0
    return (0.0, math.sin(half), 0.0, math.cos(half))


def obj(oid, name, parent=None, pos=(0.0, 0.0, 0.0), yaw=0.0, scale=(1.0, 1.0, 1.0), components=(), active=True):
    return {"id": str(oid), "name": name, "parent_id": None if parent is None else str(parent), "active": active,
            "layer": 0, "tag": "Untagged", "components": list(components), "local_position": list(pos),
            "local_rotation": list(yaw_quat(yaw)), "local_scale": list(scale)}


def box(center, size):
    return {"type": "BoxCollider", "center": list(center), "size": list(size), "enabled": True, "is_trigger": False}


def graph(objects, name="Scene"):
    return M.SceneGraph(name, {"objects": objects, "scene_name": name})


def quiet(call, *args, **kwargs):
    """Run call() with stderr captured; returns (result, stderr text)."""
    buffer = io.StringIO()
    with contextlib.redirect_stderr(buffer):
        result = call(*args, **kwargs)
    return result, buffer.getvalue()


def extract(graphs):
    """extract_scenes with its stderr warnings (missing categories in tiny synthetic scenes) swallowed."""
    return quiet(M.extract_scenes, graphs)[0]


class AlmostMixin:
    def assertVec(self, got, want, places=3):
        self.assertEqual(len(got), len(want))
        for g, w in zip(got, want):
            self.assertAlmostEqual(g, w, places=places)


class WorldTransformTests(AlmostMixin, unittest.TestCase):
    def test_two_level_chain_applies_parent_scale_then_rotation_then_position(self):
        g = graph([obj(1, "A", pos=(10, 0, 5), yaw=90, scale=(2, 2, 2)), obj(2, "B", parent=1, pos=(0, 0, 3))])
        # local (0,0,3) * scale 2 = (0,0,6); yaw 90 turns +z into +x; plus (10,0,5)
        self.assertVec(g.position(g.by_id["2"]), (16, 0, 5))
        self.assertAlmostEqual(M.yaw_degrees(g.world(g.by_id["2"])), 90.0, places=3)

    def test_yaw_is_unity_heading_in_zero_to_360(self):
        for yaw, want in ((0, 0), (90, 90), (180, 180), (-90, 270), (-179.9, 180.1), (35.4, 35.4)):
            with self.subTest(yaw=yaw):
                g = graph([obj(1, "A", yaw=yaw)])
                self.assertAlmostEqual(M.yaw_degrees(g.world(g.by_id["1"])), want, places=3)
                self.assertGreaterEqual(M.yaw_degrees(g.world(g.by_id["1"])), 0.0)
                self.assertLess(M.yaw_degrees(g.world(g.by_id["1"])), 360.0)

    def test_negative_zero_never_reaches_the_output(self):
        self.assertEqual(json.dumps(M._vec([-0.0, -0.00001, 1.00004])), "[0.0, 0.0, 1.0]")
        self.assertEqual(json.dumps(M.yaw_degrees(M.local_matrix(obj(1, "A", yaw=-0.00001)))), "0.0")

    def test_hierarchy_path_and_activity(self):
        g = graph([obj(1, "Root", active=False), obj(2, "Mid", parent=1), obj(3, "Leaf", parent=2)])
        leaf = g.by_id["3"]
        self.assertEqual(g.path(leaf), "Root/Mid/Leaf")
        self.assertFalse(g.active_in_hierarchy(leaf))
        live = graph([obj(1, "Root"), obj(2, "Leaf", parent=1)])
        self.assertTrue(live.active_in_hierarchy(live.by_id["2"]))

    def test_children_listed_in_scene_order_and_order_of_objects_does_not_matter(self):
        forward = graph([obj(1, "R", pos=(1, 0, 0)), obj(2, "C", parent=1, pos=(0, 2, 0))])
        shuffled = graph([obj(2, "C", parent=1, pos=(0, 2, 0)), obj(1, "R", pos=(1, 0, 0))])
        self.assertVec(forward.position(forward.by_id["2"]), (1, 2, 0))
        self.assertVec(shuffled.position(shuffled.by_id["2"]), (1, 2, 0))

    def test_broken_hierarchy_fails_closed(self):
        with self.assertRaisesRegex(HardExtractError, "missing parent"):
            graph([obj(1, "A", parent=99)])
        with self.assertRaisesRegex(HardExtractError, "repeats object id"):
            graph([obj(1, "A"), obj(1, "B")])
        cyclic = graph([obj(1, "A", parent=2), obj(2, "B", parent=1)])
        with self.assertRaisesRegex(HardExtractError, "cycle"):
            cyclic.world(cyclic.by_id["1"])

    def test_malformed_transform_fails_closed(self):
        for field, value in (("local_position", [0, 0]), ("local_position", [0, float("nan"), 0]),
                             ("local_scale", [1, 1, "x"]), ("local_rotation", [0, 0, 0, 0])):
            with self.subTest(field=field, value=value):
                bad = obj(1, "A")
                bad[field] = value
                with self.assertRaises(HardExtractError):
                    M.local_matrix(bad)

    def test_rotation_quaternion_roundtrips_and_has_w_nonnegative(self):
        for yaw in (0, 35.4, 90, 180, 270, -36.5):
            with self.subTest(yaw=yaw):
                q = M.rotation_quaternion(M.local_matrix(obj(1, "A", yaw=yaw, scale=(1.01, 1.0, 3.53))))
                self.assertGreaterEqual(q[3], 0.0)
                self.assertAlmostEqual(math.sqrt(sum(v * v for v in q)), 1.0, places=3)
                self.assertAlmostEqual(math.degrees(2 * math.atan2(q[1], q[3])) % 360.0, yaw % 360.0, places=2)


class CaptureTests(AlmostMixin, unittest.TestCase):
    def two_light_scene(self, light_names=("AIRPIT_CAPTURE_POINT_LIGHT_03", "AIRPIT_CAPTURE_POINT_LIGHT_02 ")):
        return graph([
            obj(1, "CAPTURE_POINT"),
            obj(2, light_names[0], parent=1, pos=(-45.38, 0.0, 3.82)),
            obj(3, light_names[1], parent=1, pos=(18.96, -0.1, -1.24)),
            obj(4, "LIGHT", parent=2, pos=(72.71, -0.4, -4.94)),  # grandchild: not a marker
        ], "TF_DesignStuff")

    def test_capture_point_is_the_mean_of_the_light_children(self):
        found = extract([self.two_light_scene()])["capture_point"]
        self.assertEqual(found["method"], "capture_point_light_centroid")
        self.assertVec(found["position"], (-13.21, -0.05, 1.29), places=4)
        self.assertEqual([light["path"] for light in found["lights"]],
                         ["CAPTURE_POINT/AIRPIT_CAPTURE_POINT_LIGHT_03", "CAPTURE_POINT/AIRPIT_CAPTURE_POINT_LIGHT_02 "])

    def test_container_offset_moves_the_lights_in_world_space(self):
        g = graph([obj(1, "capture_point ", pos=(100, 0, 0)), obj(2, "x_capture_point_light", parent=1, pos=(1, 2, 3))])
        self.assertVec(extract([g])["capture_point"]["position"], (101, 2, 3))

    def test_zone_projection_child_anchors_when_the_root_is_far_away(self):
        g = graph([obj(1, "Capture_Zone", pos=(-186.11, 18.04, -23.59)),
                   obj(2, "PROJECTION", parent=1, pos=(172.94, -18.18, 24.93))], "DesignStuff")
        zone = extract([g])["capture_zones"][0]
        self.assertVec(zone["position"], (-186.11, 18.04, -23.59))
        self.assertVec(zone["projection_position"], (-13.17, -0.14, 1.34))
        self.assertVec(zone["anchor_position"], (-13.17, -0.14, 1.34))
        self.assertFalse(zone["at_origin"])

    def test_zone_at_origin_has_no_anchor(self):
        g = graph([obj(1, "CAPTURE_ZONE"), obj(2, "PROJECTION", parent=1)])
        zone = extract([g])["capture_zones"][0]
        self.assertTrue(zone["at_origin"])
        self.assertIsNone(zone["anchor_position"])
        self.assertIsNone(zone["distance_to_capture_point"])

    def test_zone_distance_is_measured_to_the_anchor(self):
        zone_scene = graph([obj(1, "Capture_Zone", pos=(-13.16, -0.14, 1.33))], "TeamFight")
        found = extract([self.two_light_scene(), zone_scene])
        self.assertAlmostEqual(found["capture_zones"][0]["distance_to_capture_point"], 0.109, places=2)

    def test_fallback_to_the_zone_anchor_without_lights(self):
        g = graph([obj(1, "Capture_Zone", pos=(5, 1, 7))])
        found = extract([g])["capture_point"]
        self.assertEqual(found["method"], "capture_zone_anchor")
        self.assertVec(found["position"], (5, 1, 7))
        self.assertEqual(found["lights"], [])

    def test_no_marker_at_all_warns_and_yields_null_not_an_invented_point(self):
        found, err = quiet(M.extract_scenes, [graph([obj(1, "Floor")])])
        self.assertIsNone(found["capture_point"])
        self.assertEqual(found["capture_zones"], [])
        self.assertIn("no capture point marker", err)

    def test_lights_of_several_scenes_are_pooled(self):
        a = graph([obj(1, "CAPTURE_POINT"), obj(2, "CAPTURE_POINT_LIGHT_A", parent=1, pos=(0, 0, 0))], "A")
        b = graph([obj(1, "CAPTURE_POINT"), obj(2, "CAPTURE_POINT_LIGHT_B", parent=1, pos=(10, 0, 4))], "B")
        found = extract([a, b])["capture_point"]
        self.assertVec(found["position"], (5, 0, 2))
        self.assertEqual([light["scene"] for light in found["lights"]], ["A", "B"])


class SpawnTests(AlmostMixin, unittest.TestCase):
    def spawn_scene(self):
        return graph([
            obj(1, "SPAWNS", pos=(0, 0, 0)),
            obj(2, "ScavPMCspawn (2)", parent=1, pos=(-15.22, 0.12, -74.4)),
            obj(3, "ScavPMCspawn (1)", parent=1, pos=(-12.57, 0.08, -74.48)),
            obj(4, "ScavPMCspawn (19)", parent=1, pos=(-10.24, 0.1, 76.74), yaw=180.15),
            obj(5, "SPAWN_BOUND_FIGHTER", parent=1, pos=(-13.94, 1.69, 76.87), scale=(1.01, 1.0, 3.53),
                components=[box((2.4, 0.5, 0.08), (49.4, 5.6, 4.5))]),
            obj(6, "SPAWN_BOUND_FIGHTER ", parent=1, pos=(-13.94, 1.69, -72.7), scale=(1.01, 1.0, 3.53),
                components=[box((2.26, 0.5, 0.08), (22.07, 5.6, 4.5))]),
            obj(7, "ScavPMCspawn (7)", pos=(1, 0, 1)),  # not under SPAWNS: ignored
        ], "TF_DesignStuff")

    def test_markers_are_grouped_by_z_sign_and_sorted_by_index(self):
        markers = extract([self.spawn_scene()])["spawn_markers"]
        self.assertEqual([m["index"] for m in markers["south"]], [1, 2])
        self.assertEqual([m["index"] for m in markers["north"]], [19])
        self.assertTrue(all(m["stem"] == "ScavPMCspawn" for m in markers["south"] + markers["north"]))
        north = markers["north"][0]
        self.assertEqual(north["path"], "SPAWNS/ScavPMCspawn (19)")
        self.assertVec(north["position"], (-10.24, 0.1, 76.74))
        self.assertAlmostEqual(north["yaw"], 180.15, places=3)
        self.assertTrue(north["active"])

    def test_bounds_are_not_markers_and_object_outside_spawns_is_ignored(self):
        found = extract([self.spawn_scene()])
        names = [m["name"] for side in found["spawn_markers"].values() for m in side]
        self.assertEqual(len(names), 3)
        self.assertNotIn("ScavPMCspawn (7)", names)
        self.assertFalse(any(name.startswith("SPAWN_BOUND") for name in names))

    def test_bound_center_and_size_apply_the_lossy_scale(self):
        bounds = extract([self.spawn_scene()])["spawn_bounds"]
        self.assertEqual([b["side"] for b in bounds], ["north", "south"])
        north, south = bounds
        # centre = position + scale * collider centre; size = scale * collider size
        self.assertVec(north["center"], (-13.94 + 1.01 * 2.4, 1.69 + 0.5, 76.87 + 3.53 * 0.08), places=3)
        self.assertVec(north["size"], (1.01 * 49.4, 5.6, 3.53 * 4.5), places=3)
        self.assertVec(south["size"], (1.01 * 22.07, 5.6, 3.53 * 4.5), places=3)
        self.assertEqual(north["name"], "SPAWN_BOUND_FIGHTER")
        self.assertEqual(south["name"], "SPAWN_BOUND_FIGHTER")  # trailing space stripped
        self.assertEqual(north["rotation"], [0.0, 0.0, 0.0, 1.0])

    def test_rotated_parent_rotates_markers_bounds_and_yaw(self):
        g = graph([
            obj(1, "SPAWNS", pos=(10, 0, 5), yaw=90),
            obj(2, "Bear house (1)", parent=1, pos=(0, 0, 4)),      # +z local -> +x world: (14, 0, 5)
            obj(3, "Usec house (1)", parent=1, pos=(0, 0, -4), yaw=90),  # (6, 0, 5), yaw 180
            obj(4, "SPAWN_BOUND_X", parent=1, pos=(0, 0, 0), components=[box((0, 0, 3), (2, 2, 2))]),
            # 2026-10-18: a layout on one side of z == 0 must now be two clear pockets (Bowl rule), so the
            # fixture straddles the origin like every real map: local +x is world -z under this yaw -> (10, 0, -3)
            obj(5, "Usec house (2)", parent=1, pos=(8, 0, 0)),
        ])
        found = extract([g])
        by_name = {m["name"]: m for side in found["spawn_markers"].values() for m in side}
        self.assertVec(by_name["Bear house (1)"]["position"], (14, 0, 5))
        self.assertVec(by_name["Usec house (1)"]["position"], (6, 0, 5))
        self.assertAlmostEqual(by_name["Bear house (1)"]["yaw"], 90.0, places=3)
        self.assertAlmostEqual(by_name["Usec house (1)"]["yaw"], 180.0, places=3)
        self.assertVec(found["spawn_bounds"][0]["center"], (13, 0, 5))
        self.assertAlmostEqual(found["spawn_bounds"][0]["yaw"], 90.0, places=3)

    def test_name_without_index_keeps_the_stem(self):
        g = graph([obj(1, "SPAWNS"), obj(2, "Bear house", parent=1, pos=(0, 0, -1)),
                   obj(3, "Bear house (4)", parent=1, pos=(0, 0, -2)), obj(4, "Odd (x)", parent=1, pos=(0, 0, 1))])
        south = extract([g])["spawn_markers"]["south"]
        self.assertEqual([(m["stem"], m["index"]) for m in south], [("Bear house", None), ("Bear house", 4)])
        self.assertEqual(extract([g])["spawn_markers"]["north"][0]["stem"], "Odd (x)")

    def test_marker_on_the_z_axis_cannot_be_grouped(self):
        g = graph([obj(1, "SPAWNS"), obj(2, "ScavPMCspawn (1)", parent=1, pos=(3, 0, 0))])
        with self.assertRaisesRegex(HardExtractError, "z == 0"):
            extract([g])

    def test_bound_without_a_box_collider_fails_closed(self):
        g = graph([obj(1, "SPAWNS"), obj(2, "SPAWN_BOUND_FIGHTER", parent=1, pos=(0, 0, 4))])
        with self.assertRaisesRegex(HardExtractError, "no BoxCollider"):
            extract([g])

    def test_inactive_parent_is_reported_not_dropped(self):
        # a second marker south of the origin: one marker on its own is no pair of pockets (see SideRuleTests)
        g = graph([obj(1, "SPAWNS", active=False), obj(2, "ScavPMCspawn (1)", parent=1, pos=(0, 0, 2)),
                   obj(3, "ScavPMCspawn (2)", parent=1, pos=(0, 0, -2))])
        marker = extract([g])["spawn_markers"]["north"][0]
        self.assertFalse(marker["active"])

    def test_output_order_does_not_depend_on_object_order(self):
        scene = self.spawn_scene()
        shuffled = graph(list(reversed(scene.objects)), "TF_DesignStuff")
        self.assertEqual(quiet(M.extract_scenes, [scene])[0], quiet(M.extract_scenes, [shuffled])[0])

    def test_missing_categories_warn_and_stay_empty(self):
        found, err = quiet(M.extract_scenes, [graph([obj(1, "Floor")])])
        self.assertEqual(found["spawn_bounds"], [])
        self.assertEqual(found["spawn_markers"], {"north": [], "south": []})
        self.assertIn("no SPAWN_BOUND", err)
        self.assertIn("no SPAWNS", err)


def grid(x0, z0, dx, dz, columns=5, rows=2):
    """columns x rows points, row by row, from (x0, z0)."""
    return [(x0 + dx * c, z0 + dz * r) for r in range(rows) for c in range(columns)]


def spawn_layout(pockets, bounds=(), scene="TF_DesignStuff"):
    """A scene whose SPAWNS container holds one marker per (x, z) and one SPAWN_BOUND box per centre.

    Markers are numbered through one index space, like Arena's single ``Bear house (n)`` row."""
    objects = [obj(1, "SPAWNS")]
    index = 0
    for pocket in pockets:
        for x, z in pocket:
            objects.append(obj(len(objects) + 1, f"Bear house ({index})", parent=1, pos=(x, 0.05, z)))
            index += 1
    for x, z in bounds:
        objects.append(obj(len(objects) + 1, "SPAWN_BOUND_FIGHTER", parent=1, pos=(x, 2.0, z),
                           components=[box((0, 0, 0), (20, 4, 20))]))
    return graph(objects, scene)


# Bowl-like: both pockets have z < 0 (east x 46..56 / z -11..-3, west x -58..-49 / z -69..-60 in the source)
EAST = grid(46.0, -11.0, 2.5, 8.0)
WEST = grid(-58.0, -69.0, 2.25, 9.0)
EAST_BOUND, WEST_BOUND = (51.0, -7.0), (-53.5, -64.5)


def sides_of(found):
    """{side: sorted list of marker x} of an extract_scenes result."""
    return {side: sorted(round(m["position"][0], 2) for m in found["spawn_markers"][side])
            for side in ("north", "south")}


class SideRuleTests(AlmostMixin, unittest.TestCase):
    """The side rule: sign of z while the origin lies between the pockets, the pockets themselves otherwise."""

    def test_bowl_like_pockets_on_one_side_of_z_get_two_balanced_sides(self):
        found = extract([spawn_layout([WEST, EAST], bounds=[WEST_BOUND, EAST_BOUND])])
        sides = sides_of(found)
        self.assertEqual((len(sides["north"]), len(sides["south"])), (10, 10))  # was 0 / 20 under the sign rule
        self.assertTrue(all(x > 40 for x in sides["north"]))   # the pocket with the larger mean z (east)
        self.assertTrue(all(x < -40 for x in sides["south"]))  # the west pocket
        self.assertEqual({b["side"]: b["center"][0] for b in found["spawn_bounds"]}, {"north": 51.0, "south": -53.5})
        rule = found["side_rule"]
        self.assertEqual(rule["method"], "pocket_split")
        self.assertEqual({side: rule["pockets"][side]["markers"] for side in rule["pockets"]}, {"north": 10, "south": 10})
        self.assertVec(rule["pockets"]["north"]["centroid_xz"], (51.0, -7.0), places=3)
        self.assertVec(rule["pockets"]["south"]["centroid_xz"], (-53.5, -64.5), places=3)
        self.assertGreater(rule["cut_distance"], 100.0)   # nearest east / west markers: about 107 m apart
        self.assertLess(rule["pocket_spread"], 15.0)

    def test_the_name_follows_the_mean_z_not_the_input_order_or_the_x_side(self):
        mirrored = [(-x, z) for x, z in EAST], [(-x, z) for x, z in WEST]
        for pockets in ([WEST, EAST], [EAST, WEST], list(mirrored), list(reversed(mirrored))):
            with self.subTest(first_x=pockets[0][0][0]):
                found = extract([spawn_layout(pockets)])
                north = [m["position"] for m in found["spawn_markers"]["north"]]
                self.assertEqual(len(north), 10)
                self.assertTrue(all(p[2] > -12.0 for p in north))  # the z -11..-3 pocket, whichever x it is at

    def test_pocket_rule_also_applies_when_the_sign_of_z_would_cut_a_pocket(self):
        # pockets side by side along x with both of them reaching across z == 0: the sign rule would mix them
        east = grid(46.0, -3.0, 2.5, 15.0)
        west = grid(-58.0, -12.0, 2.25, 15.0)
        found = extract([spawn_layout([east, west], bounds=[(51.0, 4.5), (-53.5, -4.5)])])
        sides = sides_of(found)
        self.assertTrue(all(x > 40 for x in sides["north"]) and len(sides["north"]) == 10)  # mean z 4.5 vs -4.5
        self.assertTrue(all(x < -40 for x in sides["south"]) and len(sides["south"]) == 10)
        self.assertEqual(found["side_rule"]["method"], "pocket_split")

    def test_released_layouts_keep_the_sign_rule_and_get_no_side_rule(self):
        equator = [grid(-7.0, 44.5, 3.5, 4.1), grid(-7.0, -42.0, 3.5, 3.6)]
        bay5 = [grid(140.0, 43.5, 3.0, 3.5, columns=2, rows=5), grid(48.0, -12.0, 3.25, 7.0)]
        for name, pockets in (("equator", equator), ("bay5", bay5)):
            with self.subTest(layout=name):
                found = extract([spawn_layout(pockets)])
                self.assertNotIn("side_rule", found)
                self.assertTrue(all(m["position"][2] > 0 for m in found["spawn_markers"]["north"]))
                self.assertTrue(all(m["position"][2] < 0 for m in found["spawn_markers"]["south"]))
                self.assertEqual([len(found["spawn_markers"][s]) for s in ("north", "south")], [10, 10])
                points = [(x, z) for pocket in pockets for x, z in pocket]
                groups = M.split_pockets(points)
                self.assertIsNotNone(groups)  # the pockets are clear, and they equal the sign groups
                self.assertEqual({frozenset(groups[0]), frozenset(groups[1])},
                                 {frozenset(range(10)), frozenset(range(10, 20))})
        # Bay5's centres are further apart in x (87.6 m) than in z (60.2 m): naming by the larger spread
        # would have turned its sides east/west. They stay north/south, by the sign of z, as released.
        centre = lambda pocket: (sum(x for x, _ in pocket) / len(pocket), sum(z for _, z in pocket) / len(pocket))
        (nx, nz), (sx, sz) = centre(bay5[0]), centre(bay5[1])
        self.assertGreater(abs(nx - sx), abs(nz - sz))

    def test_the_sign_rule_keeps_its_leniency_about_unequal_counts_and_unclear_pockets(self):
        # 2 vs 1 across the origin (the pre-existing fixtures), not two clear pockets: nothing to fail on
        found = extract([spawn_layout([[(0.0, -1.0), (0.0, -2.0)], [(0.0, 1.0)]])])
        self.assertEqual([len(found["spawn_markers"][s]) for s in ("north", "south")], [1, 2])
        self.assertNotIn("side_rule", found)

    def test_odd_or_unequal_pockets_are_refused(self):
        for label, pockets in (("19 markers", [WEST, EAST[:-1]]), ("21 markers", [WEST, EAST + [(51.0, -7.0)]]),
                               ("12 against 8", [WEST + [(-50.0, -70.0), (-48.0, -70.0)], EAST[:8]])):
            with self.subTest(layout=label), self.assertRaisesRegex(HardExtractError, "same number of markers"):
                extract([spawn_layout(pockets)])

    def test_one_side_of_z_without_two_separated_pockets_is_refused(self):
        blob = grid(46.0, -11.0, 2.5, 3.0, 5, 4)  # twenty markers within 10 x 9 m, all z < 0
        for label, pockets in (("one blob", [blob]), ("single marker", [[(0.0, 2.0)]])):
            with self.subTest(layout=label), self.assertRaisesRegex(HardExtractError, "no two separated pockets"):
                extract([spawn_layout(pockets)])
        near = [grid(0.0, -10.0, 2.0, 0.0, 5, 1), grid(23.0, -10.0, 2.0, 0.0, 5, 1)]  # 15 m gap, 8 m groups
        with self.assertRaisesRegex(HardExtractError, "no two separated pockets"):
            extract([spawn_layout(near)])

    def test_pockets_side_by_side_in_z_are_refused_rather_than_named_arbitrarily(self):
        with self.assertRaisesRegex(HardExtractError, "arbitrary"):
            extract([spawn_layout([grid(46.0, -10.0, 2.5, 8.0), grid(-58.0, -10.0, 2.25, 8.0)])])

    def test_a_bound_between_the_pockets_is_refused(self):
        with self.assertRaisesRegex(HardExtractError, "cannot be assigned to a side"):
            extract([spawn_layout([WEST, EAST], bounds=[WEST_BOUND, (-1.0, -36.0)])])

    def test_a_marker_on_z_zero_without_pockets_still_fails(self):
        blob = grid(0.0, -4.0, 2.0, 4.0, 3, 3)  # z -4, 0, 4: straddles, no pockets, one marker exactly on the line
        with self.assertRaisesRegex(HardExtractError, "z == 0"):
            extract([spawn_layout([blob])])

    def test_a_marker_on_z_zero_inside_a_clear_pocket_is_grouped_by_the_pockets(self):
        # Bay5-like (north z 43..58, south z -12..-5) with one south marker moved onto z == 0: the sign rule would
        # refuse it, but the pockets are unmistakable, so they decide (and the side_rule block says so)
        north = grid(140.0, 43.5, 3.0, 3.5, columns=2, rows=5)
        south = [(48.0, 0.0)] + grid(48.0, -12.0, 3.25, 7.0)[1:]
        found = extract([spawn_layout([north, south])])
        self.assertEqual([len(found["spawn_markers"][s]) for s in ("north", "south")], [10, 10])
        self.assertTrue(all(m["position"][0] > 100 for m in found["spawn_markers"]["north"]))
        self.assertIn((48.0, 0.0), [(m["position"][0], m["position"][2]) for m in found["spawn_markers"]["south"]])
        self.assertEqual(found["side_rule"]["method"], "pocket_split")

    def test_split_pockets_thresholds_and_determinism(self):
        self.assertIsNone(M.split_pockets([]))
        self.assertIsNone(M.split_pockets([(1.0, 1.0)]))
        self.assertIsNone(M.split_pockets([(5.0, 5.0), (5.0, 5.0)]))  # coincident: a zero gap is no gap
        row = lambda gap: [(0.0, 0.0), (8.0, 0.0), (8.0 + gap, 0.0), (16.0 + gap, 0.0)]  # two 8 m pockets
        self.assertIsNone(M.split_pockets(row(16.0)))           # exactly twice the pocket: not clearly two
        self.assertIsNotNone(M.split_pockets(row(16.01)))
        group_a, group_b, cut, spread = M.split_pockets(row(30.0))
        self.assertEqual((group_a, group_b), ([0, 1], [2, 3]))
        self.assertAlmostEqual(cut, 30.0)
        self.assertAlmostEqual(spread, 8.0)
        reordered = [(8.0, 0.0), (38.0, 0.0), (0.0, 0.0), (46.0, 0.0)]  # same points, other order
        a, b, _cut, _spread = M.split_pockets(reordered)
        self.assertEqual(sorted(sorted(reordered[i][0] for i in g) for g in (a, b)), [[0.0, 8.0], [38.0, 46.0]])

    def test_result_does_not_depend_on_object_order_and_leaves_no_scratch_key(self):
        scene = spawn_layout([WEST, EAST], bounds=[WEST_BOUND, EAST_BOUND])
        shuffled = graph(list(reversed(scene.objects)), "TF_DesignStuff")
        first, second = extract([scene]), extract([shuffled])
        self.assertEqual(first, second)
        for item in first["spawn_bounds"] + first["spawn_markers"]["north"] + first["spawn_markers"]["south"]:
            self.assertNotIn("_z", item)
            self.assertIn(item["side"], ("north", "south"))

    def test_package_and_cli_carry_the_rule_only_when_the_pockets_decided(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = PackageTests().make_package(tmp, {"map_key": "arena_x"}, scenes={
                "Arena_X_TeamFight_DesignStuff": spawn_layout([WEST, EAST], bounds=[WEST_BOUND, EAST_BOUND]).objects})
            first, second = Path(tmp) / "a.json", Path(tmp) / "b.json"
            for target in (first, second):
                code, err = quiet(M.main, ["--package", str(package), "--output", str(target)])
                self.assertEqual(code, 0)
            self.assertIn("markers north 10 / south 10 (pocket split)", err)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            data = json.loads(first.read_text(encoding="utf-8"))
            self.assertEqual(data["counts"], {"capture_zones": 0, "spawn_bounds": 2,
                                              "spawn_markers_north": 10, "spawn_markers_south": 10})
            self.assertEqual(data["schema_version"], 1)
            self.assertEqual(data["side_rule"]["method"], "pocket_split")
            plain = PackageTests().make_package(Path(tmp) / "plain", {"map_key": "arena_x"})
            code, err = quiet(M.main, ["--package", str(plain), "--output", str(Path(tmp) / "c.json")])
            self.assertEqual(code, 0)
            self.assertNotIn("pocket split", err)
            self.assertNotIn("side_rule", json.loads((Path(tmp) / "c.json").read_text(encoding="utf-8")))

    def test_ambiguous_layout_is_reported_as_exit_2_without_writing_a_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = PackageTests().make_package(tmp, {"map_key": "arena_x"}, scenes={
                "Arena_X_TeamFight_DesignStuff": spawn_layout([WEST, EAST[:-1]]).objects})
            target = Path(tmp) / "out.json"
            code, err = quiet(M.main, ["--package", str(package), "--output", str(target)])
            self.assertEqual(code, 2)
            self.assertIn("same number of markers", err)
            self.assertFalse(target.exists())


class PackageTests(unittest.TestCase):
    def make_package(self, root, manifest_source, scenes=None):
        scenes = scenes or {"Arena_X_TeamFight_DesignStuff": [
            obj(1, "CAPTURE_POINT"), obj(2, "X_CAPTURE_POINT_LIGHT", parent=1, pos=(4, 0, 6)),
            obj(3, "SPAWNS"), obj(4, "ScavPMCspawn (1)", parent=3, pos=(0, 0, 9), yaw=180),
            obj(5, "ScavPMCspawn (2)", parent=3, pos=(0, 0, -9)),
            obj(6, "SPAWN_BOUND_FIGHTER", parent=3, pos=(0, 1, 9), components=[box((0, 0, 0), (4, 2, 4))]),
        ]}
        package = Path(root) / "x-native-v5"
        (package / "scenes").mkdir(parents=True)
        (package / "manifest.json").write_text(json.dumps(
            {"source": manifest_source, "scenes": list(scenes)}), encoding="utf-8")
        for name, objects in scenes.items():
            (package / "scenes" / f"{name}.json").write_text(
                json.dumps({"scene_name": name, "objects": objects}), encoding="utf-8")
        return package

    def test_map_key_sources(self):
        self.assertEqual(M.map_key_of({"source": {"map_key": "arena_airpit"}}), "arena_airpit")
        self.assertEqual(M.map_key_of({"source": {"extractor_script": "tools/extract/extract_bay5.py"}}), "arena_bay5")
        self.assertEqual(M.map_key_of({"source": {"extractor_script": "tools/extract/extract_equator.py"}}), "arena_equator")
        self.assertEqual(M.map_key_of({"source": {"map_key": "arena_a"}}, "arena_b"), "arena_b")
        for bad in ({"source": {}}, {"source": {"map_key": "Equator"}}, {"source": {"map_key": "arena_../x"}}):
            with self.subTest(bad=bad), self.assertRaisesRegex(HardExtractError, "map key"):
                M.map_key_of(bad)

    def test_extract_package_end_to_end(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = self.make_package(tmp, {"map_key": "arena_x", "extracted_utc": "2026-10-11T00:00:00Z",
                                              "extractor_script": "tools/extract/extract_map.py"})
            data = M.extract_package(package)
        self.assertEqual(data["map_key"], "arena_x")
        self.assertEqual(data["schema_version"], 1)
        self.assertEqual(data["source_package"], {"name": "x-native-v5", "extracted_utc": "2026-10-11T00:00:00Z",
                                                  "extractor_script": "tools/extract/extract_map.py",
                                                  "scenes": ["Arena_X_TeamFight_DesignStuff"]})
        self.assertEqual(data["counts"], {"capture_zones": 0, "spawn_bounds": 1,
                                          "spawn_markers_north": 1, "spawn_markers_south": 1})
        self.assertEqual(data["capture_point"]["position"], [4.0, 0.0, 6.0])
        self.assertEqual(data["spawn_markers"]["north"][0]["yaw"], 180.0)

    def test_main_writes_reproducible_json_to_the_default_name(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = self.make_package(tmp, {"map_key": "arena_x"})
            first, second = Path(tmp) / "a" / "x.modes.json", Path(tmp) / "b" / "x.modes.json"
            for target in (first, second):
                code, _err = quiet(M.main, ["--package", str(package), "--output", str(target)])
                self.assertEqual(code, 0)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            self.assertTrue(first.read_text(encoding="utf-8").endswith("}\n"))
            self.assertNotIn(b"\r", first.read_bytes())
            self.assertEqual(json.loads(first.read_text(encoding="utf-8"))["map_key"], "arena_x")
        self.assertEqual(M.default_output_path("arena_airpit").name, "airpit.modes.json")
        self.assertEqual(M.default_output_path("arena_airpit").parent.name, "maps")

    def test_main_reports_a_broken_package_as_exit_2(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = self.make_package(tmp, {"map_key": "arena_x"}, scenes={"S": [obj(1, "A", parent=7)]})
            code, err = quiet(M.main, ["--package", str(package), "--output", str(Path(tmp) / "out.json")])
            self.assertEqual(code, 2)
            self.assertIn("[extract_mode_objects] FAILED", err)
            self.assertFalse((Path(tmp) / "out.json").exists())
            code, err = quiet(M.main, ["--package", str(Path(tmp) / "missing")])
            self.assertEqual(code, 2)

    def test_manifest_without_scenes_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp) / "p"
            package.mkdir()
            (package / "manifest.json").write_text(json.dumps({"source": {"map_key": "arena_x"}, "scenes": []}))
            with self.assertRaisesRegex(HardExtractError, "no scenes"):
                M.extract_package(package)


MAPS = ROOT / "tools" / "maps"
STEMS = ("equator", "bay5", "airpit")


class TrackedSidecarTests(unittest.TestCase):
    """The git-tracked sidecars are exactly what the extractor writes from the package they name."""

    def sidecar(self, stem):
        path = MAPS / f"{stem}.modes.json"
        if not path.is_file():
            self.skipTest(f"{path.name} not generated yet")
        return path

    def test_tracked_sidecars_equal_a_fresh_extraction(self):
        for stem in STEMS:
            with self.subTest(map=stem):
                path = self.sidecar(stem)
                text = path.read_text(encoding="utf-8")  # universal newlines: a CRLF checkout compares equal
                package = WORK / "extract" / json.loads(text)["source_package"]["name"]
                if not (package / "manifest.json").is_file():
                    self.skipTest(f"{package.name} package is local only")
                data, _ = quiet(M.extract_package, package)
                self.assertEqual(text, M.dumps(data))

    def test_airpit_sidecar_names_the_accepted_package_once_it_exists(self):
        # item 4 had to compose airpit.modes.json from the 0.5.4 spike package `airpit` because item 5 had
        # not produced the accepted 0.6.0 package yet (every marker and light agrees to 6e-5 m, so only
        # source_package differs). PLAN-AIRPIT item 5 says the old package is "no longer referenced":
        # this test starts running when the accepted package appears and fails until the sidecar is redone.
        # 2026-10-12 (item 13): the accepted package was airpit-native-v6 (v5 + native ambient audio; scenes,
        # meshes, materials and textures byte-identical to v5). Furnished shops (same day): airpit-native-v7 adds the
        # partial scenes Arena_Airpit_Main_hall and Arena_Airpit_Light; the first five scenes are byte-identical to v6,
        # so only source_package (name, extracted_utc, the scene list) changed in the sidecar. 2026-10-13:
        # airpit-native-v8 widens the two partial scenes (twin descendants, exclusions, decoration box); the first five
        # scenes are still byte-identical, again only source_package (name, extracted_utc) changed. 2026-10-17: v9 adds
        # three SOO crates to Arena_Airpit_Main_hall; the TeamFight scenes are byte-identical, only source_package changed.
        accepted = WORK / "extract" / "airpit-native-v9"
        if not (accepted / "manifest.json").is_file():
            self.skipTest("airpit-native-v9 not extracted yet (the furnished hall plus the crates under the tarp)")
        name = json.loads(self.sidecar("airpit").read_text(encoding="utf-8"))["source_package"]["name"]
        self.assertEqual(
            name, "airpit-native-v9",
            "airpit.modes.json still names the superseded package %r; regenerate it: python "
            "tools/extract/extract_mode_objects.py --package work/extract/airpit-native-v9 "
            "(only source_package should change)" % name)

    def test_every_sidecar_has_twenty_markers_and_two_bounds(self):
        for stem in STEMS:
            with self.subTest(map=stem):
                data = json.loads(self.sidecar(stem).read_text(encoding="utf-8"))
                self.assertEqual(data["counts"], {"capture_zones": data["counts"]["capture_zones"], "spawn_bounds": 2,
                                                  "spawn_markers_north": 10, "spawn_markers_south": 10})
                self.assertTrue(all(m["position"][2] > 0 for m in data["spawn_markers"]["north"]))
                self.assertTrue(all(m["position"][2] < 0 for m in data["spawn_markers"]["south"]))
                self.assertEqual(sorted(b["side"] for b in data["spawn_bounds"]), ["north", "south"])

    def test_released_sidecars_carry_no_side_rule_and_their_pockets_equal_the_sign_split(self):
        # the side rule of 2026-10-18 (Bowl): where the pockets equal the sign-of-z groups the original rule and
        # its output stay, so a released sidecar never has a side_rule block and the pocket analysis agrees with it
        for stem in STEMS:
            with self.subTest(map=stem):
                data = json.loads(self.sidecar(stem).read_text(encoding="utf-8"))
                self.assertNotIn("side_rule", data)
                markers = data["spawn_markers"]["north"] + data["spawn_markers"]["south"]
                groups = M.split_pockets([(m["position"][0], m["position"][2]) for m in markers])
                self.assertIsNotNone(groups)
                self.assertEqual({frozenset(groups[0]), frozenset(groups[1])},
                                 {frozenset(range(10)), frozenset(range(10, 20))})

    def test_airpit_measured_values(self):
        data = json.loads(self.sidecar("airpit").read_text(encoding="utf-8"))
        point = data["capture_point"]
        self.assertEqual(point["method"], "capture_point_light_centroid")
        self.assertEqual(len(point["lights"]), 2)
        for got, want in zip(point["position"], (-13.21, -0.05, 1.29)):
            self.assertAlmostEqual(got, want, places=2)
        # the TeamFight scene's own Capture_Zone root and the DesignStuff PROJECTION child agree with it
        anchored = [z for z in data["capture_zones"] if z["scene"] != "Arena_AirPit_ShootOut"]
        self.assertEqual(len(anchored), 2)
        for zone in anchored:
            self.assertLess(zone["distance_to_capture_point"], 0.2)
        indices = sorted(m["index"] for side in data["spawn_markers"].values() for m in side)
        self.assertEqual(indices, list(range(1, 21)))
        # Appendix A / item 6 player spawns come from these markers
        by_index = {m["index"]: m for side in data["spawn_markers"].values() for m in side}
        for index, (x, z) in {19: (-10.24, 76.74), 16: (0.33, 78.30), 4: (-9.94, -74.40), 13: (-16.91, -71.70)}.items():
            self.assertAlmostEqual(by_index[index]["position"][0], x, places=2)
            self.assertAlmostEqual(by_index[index]["position"][2], z, places=2)


class BowlSidecarTests(unittest.TestCase):
    """tools/maps/bowl.modes.json, once the accepted package has produced it (PLAN-BOWL item 5)."""

    def sidecar(self):
        path = MAPS / "bowl.modes.json"
        if not path.is_file():
            self.skipTest("bowl.modes.json not generated yet")
        return path

    def test_equals_a_fresh_extraction_of_the_package_it_names(self):
        text = self.sidecar().read_text(encoding="utf-8")
        package = WORK / "extract" / json.loads(text)["source_package"]["name"]
        if not (package / "manifest.json").is_file():
            self.skipTest(f"{package.name} package is local only")
        data, _ = quiet(M.extract_package, package)
        self.assertEqual(text, M.dumps(data))

    def test_two_balanced_pockets_east_and_west_named_by_z(self):
        data = json.loads(self.sidecar().read_text(encoding="utf-8"))
        self.assertEqual(data["counts"], {"capture_zones": data["counts"]["capture_zones"], "spawn_bounds": 2,
                                          "spawn_markers_north": 10, "spawn_markers_south": 10})
        self.assertEqual(data["side_rule"]["method"], "pocket_split")  # both pockets have z < 0: the sign rule fails
        north, south = data["spawn_markers"]["north"], data["spawn_markers"]["south"]
        self.assertTrue(all(m["position"][0] > 40 for m in north))   # north = the east pocket (larger mean z)
        self.assertTrue(all(m["position"][0] < -40 for m in south))  # south = the west pocket
        self.assertGreater(min(m["position"][2] for m in north), max(m["position"][2] for m in south))
        self.assertEqual(sorted(b["side"] for b in data["spawn_bounds"]), ["north", "south"])
        for bound in data["spawn_bounds"]:
            self.assertEqual(bound["center"][0] > 0, bound["side"] == "north")


# --------------------------------------------------------------------------------------------------------------
# PLAN-ICEBERG item 7 (2026-10-30): the team-bound rule, the stage record decoder and Iceberg's sidecar
# --------------------------------------------------------------------------------------------------------------

RULE = M.TEAM_BOUND_RULES["arena_iceberg"]


def team_layout(bear_pocket=((88.0, -90.0), (90.0, -88.0), (92.0, -92.0)), bear_stage=((30.0, -60.0), (32.0, -58.0)),
                usec_pocket=((-41.0, -88.0), (-39.0, -86.0)), usec_stage=((-10.0, -60.0),), bounds=None, lights="ABC",
                scene=None, extra_markers=()):
    """A CheckPoint-like scene: SPAWNS with Bear / Usec markers (one index space per team, pockets first) and
    spawn bounds (the two unsuffixed pocket bounds plus one numbered stage bound per team), plus a CAPTURE_POINT
    container with one ``..._LIGHT_02_<letter>`` child per letter. Unequal pockets (3 against 2) on purpose."""
    objects = [obj(1, "SPAWNS")]

    def add(name, pos, **kw):
        objects.append(obj(len(objects) + 1, name, parent=1, pos=pos, **kw))

    for stem, groups in (("Bear house", (bear_pocket, bear_stage)), ("Usec house", (usec_pocket, usec_stage))):
        index = 5
        for group in groups:
            for x, z in group:
                add(f"{stem} ({index})", (x, 0.05, z))
                index += 1
    for name, x, z in extra_markers:
        add(name, (x, 0.05, z))
    for name, center, size, yaw in (bounds or [
            ("SPAWN_BOUND_FIGHTER", (90.0, 2.0, -90.0), (20.0, 6.0, 20.0), 0.0),
            ("SPAWN_BOUND_FIGHTER (1)", (31.0, 2.0, -59.0), (10.0, 6.0, 10.0), 90.0),
            ("SPAWN_BOUND_DEFENCE", (-40.0, 2.0, -87.0), (8.0, 6.0, 8.0), 90.0),
            ("SPAWN_BOUND_DEFENCE (1)", (-10.0, 2.0, -60.0), (10.0, 6.0, 10.0), 0.0)]):
        add(name, center, yaw=yaw, components=[box((0, 0, 0), size)])
    if lights:
        objects.append(obj(len(objects) + 1, "CAPTURE_POINT"))
        container = len(objects)
        for letter, (x, z) in zip(lights, ((17.0, -91.0), (10.0, -77.0), (22.0, -47.0))):
            objects.append(obj(len(objects) + 1, f"CAPTURE_POINT_LIGHT_02_{letter}", parent=container, pos=(x, 3.0, z)))
    return graph(objects, scene or RULE.scene)


def team_extract(scene, rule=RULE):
    return quiet(M.extract_scenes, [scene], rule)[0]


def names_of(found, side, pocket=None):
    return sorted(m["index"] for m in found["spawn_markers"][side] if pocket is None or m["pocket"] == pocket)


class TeamBoundRuleTests(AlmostMixin, unittest.TestCase):
    """The per-map rule that replaces the two-pocket side rule (module docstring, "Team bounds")."""

    def test_only_iceberg_has_a_team_rule_so_the_released_maps_keep_the_original_rule(self):
        self.assertEqual(sorted(M.TEAM_BOUND_RULES), ["arena_iceberg"])
        self.assertEqual([(t.key, t.stem, t.bound, t.arena_team) for t in RULE.teams],
                         [("bear", "Bear house", "SPAWN_BOUND_FIGHTER", 1), ("usec", "Usec house", "SPAWN_BOUND_DEFENCE", 0)])
        self.assertEqual(RULE.scene, "Arena_Sanatorium_CheckPoint_DesignStuff")

    def test_sides_are_the_teams_and_pockets_are_the_unsuffixed_bounds(self):
        found = team_extract(team_layout())
        self.assertEqual(sorted(found["spawn_markers"]), ["bear", "usec"])
        self.assertEqual((len(found["spawn_markers"]["bear"]), len(found["spawn_markers"]["usec"])), (5, 3))
        self.assertEqual(names_of(found, "bear", True), [5, 6, 7])    # the pocket needs no twin: 3 against 2
        self.assertEqual(names_of(found, "usec", True), [5, 6])
        self.assertEqual(names_of(found, "bear", False), [8, 9])      # stage markers stay, flagged
        self.assertEqual(names_of(found, "usec", False), [7])
        self.assertEqual({b["name"]: (b["side"], b["initial"]) for b in found["spawn_bounds"]},
                         {"SPAWN_BOUND_FIGHTER": ("bear", True), "SPAWN_BOUND_FIGHTER (1)": ("bear", False),
                          "SPAWN_BOUND_DEFENCE": ("usec", True), "SPAWN_BOUND_DEFENCE (1)": ("usec", False)})
        for item in found["spawn_bounds"] + found["spawn_markers"]["bear"] + found["spawn_markers"]["usec"]:
            self.assertNotIn("_z", item)
        self.assertNotIn("north", found["spawn_markers"])

    def test_the_name_never_follows_the_mean_z_or_the_input_order(self):
        # 1 m of mean-z difference decided north/south before; the team names do not care which pocket is further north
        forward = team_extract(team_layout())
        flipped = team_extract(team_layout(bear_pocket=((88.0, -50.0), (90.0, -48.0), (92.0, -52.0)),
                                           usec_pocket=((-41.0, -128.0), (-39.0, -126.0)),
                                           bounds=[("SPAWN_BOUND_FIGHTER", (90.0, 2.0, -50.0), (20.0, 6.0, 20.0), 0.0),
                                                   ("SPAWN_BOUND_FIGHTER (1)", (31.0, 2.0, -59.0), (10.0, 6.0, 10.0), 90.0),
                                                   ("SPAWN_BOUND_DEFENCE", (-40.0, 2.0, -127.0), (8.0, 6.0, 8.0), 90.0),
                                                   ("SPAWN_BOUND_DEFENCE (1)", (-10.0, 2.0, -60.0), (10.0, 6.0, 10.0), 0.0)]))
        for found in (forward, flipped):
            self.assertEqual(names_of(found, "bear", True), [5, 6, 7])
            self.assertEqual(names_of(found, "usec", True), [5, 6])
        scene = team_layout()
        shuffled = graph(list(reversed(scene.objects)), RULE.scene)
        self.assertEqual(team_extract(scene), team_extract(shuffled))

    def test_the_side_rule_record_names_the_method_the_tolerance_and_the_counts(self):
        rule = team_extract(team_layout())["side_rule"]
        self.assertEqual(rule["method"], "team_bounds")
        self.assertEqual(rule["edge_tolerance"], M.BOUND_EDGE_TOLERANCE)
        self.assertEqual(rule["teams"]["bear"]["markers"], 5)
        self.assertEqual({k: rule["teams"]["bear"][k] for k in ("pocket_markers", "stage_bounds", "pocket_bound", "marker_stem")},
                         {"pocket_markers": 3, "stage_bounds": 1, "pocket_bound": "SPAWN_BOUND_FIGHTER", "marker_stem": "Bear house"})
        self.assertEqual(rule["teams"]["usec"]["pocket_markers"], 2)
        self.assertVec(rule["teams"]["bear"]["pocket_centroid_xz"], (90.0, -90.0))
        self.assertVec(rule["teams"]["usec"]["pocket_centroid_xz"], (-40.0, -87.0))

    def test_a_marker_on_the_edge_of_the_pocket_bound_is_in_and_one_clearly_outside_is_not(self):
        bounds = [("SPAWN_BOUND_FIGHTER", (90.0, 2.0, -90.0), (20.0, 6.0, 20.0), 0.0),
                  ("SPAWN_BOUND_DEFENCE", (-40.0, 2.0, -87.0), (8.0, 6.0, 8.0), 90.0)]
        # Bear box x 80..100; Usec box (yawed 90 degrees, 8 x 8) x -44..-36, z -91..-83
        found = team_extract(team_layout(bear_pocket=((100.04, -90.0), (100.2, -90.0), (90.0, -90.0)), bear_stage=(),
                                         usec_pocket=((-40.0, -87.0), (-35.97, -87.0)), usec_stage=(), bounds=bounds))
        self.assertEqual(names_of(found, "bear", True), [5, 7])     # 4 cm outside is on the edge, 20 cm is a stage marker
        self.assertEqual(names_of(found, "bear", False), [6])
        self.assertEqual(names_of(found, "usec", True), [5, 6])     # 3 cm outside the yawed box

    def test_box_margin_uses_the_published_rotation_and_size(self):
        # 4 x 8 x 12 box yawed 90 degrees: its local z (12 m) runs along world x, its local x (4 m) along world z
        bound = {"center": [10.0, 0.0, 20.0], "size": [4.0, 8.0, 12.0], "rotation": [0.0, 0.7071, 0.0, 0.7071]}
        self.assertAlmostEqual(M.box_margin(bound, (10.0, 0.0, 20.0)), 2.0, places=3)    # centre: the 4 m side is nearest
        self.assertAlmostEqual(M.box_margin(bound, (15.5, 0.0, 20.0)), 0.5, places=2)    # 5.5 m along world x of a 6 m half
        self.assertAlmostEqual(M.box_margin(bound, (10.0, 0.0, 22.5)), -0.5, places=2)   # 2.5 m along world z of a 2 m half
        self.assertAlmostEqual(M.box_margin(bound, (10.0, 3.0, 20.0)), 1.0, places=3)    # 3 m above the middle of an 8 m box

    def test_a_scaled_and_rotated_parent_moves_bounds_and_markers_together(self):
        objects = [obj(1, "Root", pos=(100.0, 0.0, -50.0), yaw=90.0, scale=(2.0, 1.0, 2.0)),
                   obj(2, "SPAWNS", parent=1),
                   obj(3, "Bear house (5)", parent=2, pos=(0.0, 0.0, 5.0)),
                   obj(4, "Usec house (5)", parent=2, pos=(0.0, 0.0, -30.0)),
                   obj(5, "SPAWN_BOUND_FIGHTER", parent=2, pos=(0.0, 0.0, 5.0), components=[box((0, 0, 0), (4.0, 4.0, 4.0))]),
                   obj(6, "SPAWN_BOUND_DEFENCE", parent=2, pos=(0.0, 0.0, -30.0), components=[box((0, 0, 0), (4.0, 4.0, 4.0))])]
        found = team_extract(graph(objects, RULE.scene))
        self.assertVec(found["spawn_markers"]["bear"][0]["position"], (110.0, 0.0, -50.0))
        self.assertTrue(found["spawn_markers"]["bear"][0]["pocket"] and found["spawn_markers"]["usec"][0]["pocket"])
        self.assertVec(found["spawn_bounds"][0]["size"], (8.0, 4.0, 8.0))

    def test_a_marker_inside_the_other_teams_pocket_bound_is_refused(self):
        with self.assertRaisesRegex(HardExtractError, r"Bear house \(7\).*inside the pocket bound SPAWN_BOUND_DEFENCE"):
            team_extract(team_layout(extra_markers=[("Bear house (7)", -40.0, -87.0)]))

    def test_a_team_without_a_pocket_marker_or_without_its_unsuffixed_bound_is_refused(self):
        with self.assertRaisesRegex(HardExtractError, "no Usec house marker lies inside the pocket bound SPAWN_BOUND_DEFENCE"):
            team_extract(team_layout(usec_pocket=((-10.0, -60.5),), usec_stage=()))
        with self.assertRaisesRegex(HardExtractError, "team usec has no unsuffixed spawn bound SPAWN_BOUND_DEFENCE"):
            team_extract(team_layout(bounds=[("SPAWN_BOUND_FIGHTER", (90.0, 2.0, -90.0), (20.0, 6.0, 20.0), 0.0),
                                             ("SPAWN_BOUND_DEFENCE (1)", (-10.0, 2.0, -60.0), (10.0, 6.0, 10.0), 0.0)]))

    def test_two_unsuffixed_bounds_of_one_team_are_refused(self):
        bounds = [("SPAWN_BOUND_FIGHTER", (90.0, 2.0, -90.0), (20.0, 6.0, 20.0), 0.0),
                  ("SPAWN_BOUND_FIGHTER ", (90.0, 2.0, -90.0), (20.0, 6.0, 20.0), 0.0),
                  ("SPAWN_BOUND_DEFENCE", (-40.0, 2.0, -87.0), (8.0, 6.0, 8.0), 90.0)]
        with self.assertRaisesRegex(HardExtractError, "two unsuffixed spawn bounds"):
            team_extract(team_layout(bounds=bounds))

    def test_unknown_names_are_refused_rather_than_guessed(self):
        with self.assertRaisesRegex(HardExtractError, "none of the teams"):
            team_extract(team_layout(extra_markers=[("Scav house (1)", 0.0, 0.0)]))
        bounds = [("SPAWN_BOUND_FIGHTER", (90.0, 2.0, -90.0), (20.0, 6.0, 20.0), 0.0),
                  ("SPAWN_BOUND_DEFENCE", (-40.0, 2.0, -87.0), (8.0, 6.0, 8.0), 90.0),
                  ("SPAWN_BOUND_NEUTRAL", (0.0, 2.0, 0.0), (8.0, 6.0, 8.0), 0.0)]
        with self.assertRaisesRegex(HardExtractError, "neither"):
            team_extract(team_layout(bounds=bounds))
        with self.assertRaisesRegex(HardExtractError, "not in the scene"):
            team_extract(team_layout(scene="Some_Other_Scene"))

    def test_the_capture_points_are_listed_one_by_one_without_a_centroid(self):
        found = team_extract(team_layout())
        self.assertIsNone(found["capture_point"])
        self.assertEqual([(p["label"], p["position"]) for p in found["capture_points"]],
                         [("A", [17.0, 3.0, -91.0]), ("B", [10.0, 3.0, -77.0]), ("C", [22.0, 3.0, -47.0])])
        self.assertEqual(found["capture_points"][0]["path"], "CAPTURE_POINT/CAPTURE_POINT_LIGHT_02_A")
        # without a team rule the same kind of lights collapse into one centroid (what the released maps need)
        lone = quiet(M.extract_scenes, [graph([obj(1, "CAPTURE_POINT"), obj(2, "CAPTURE_POINT_LIGHT_02_A", parent=1, pos=(1, 0, 1)),
                                               obj(3, "CAPTURE_POINT_LIGHT_02_B", parent=1, pos=(3, 0, 3))])])[0]
        self.assertEqual(lone["capture_point"]["position"], [2.0, 0.0, 2.0])
        self.assertNotIn("capture_points", lone)

    def test_capture_labels_must_exist_and_be_unique(self):
        with self.assertRaisesRegex(HardExtractError, "no _A/_B/_C label"):
            M.capture_points_of([{"scene": "S", "path": "CAPTURE_POINT/CAPTURE_POINT_LIGHT", "position": [0, 0, 0]}])
        with self.assertRaisesRegex(HardExtractError, "repeat"):
            M.capture_points_of([{"scene": "S", "path": "CAPTURE_POINT/L_A", "position": [0, 0, 0]},
                                 {"scene": "S", "path": "CAPTURE_POINT/L2_A", "position": [1, 0, 0]}])
        no_lights = quiet(M.extract_scenes, [team_layout(lights="")], RULE)
        self.assertEqual(no_lights[0]["capture_points"], [])
        self.assertIn("no capture points", no_lights[1])

    def test_the_package_and_the_command_line_carry_the_team_records(self):
        scenes = {RULE.scene: team_layout().objects}
        with tempfile.TemporaryDirectory() as tmp:
            package = PackageTests().make_package(tmp, {"map_key": "arena_iceberg", "extractor_script": "tools/extract/extract_map.py"},
                                                  scenes=scenes)
            first, second = Path(tmp) / "a.json", Path(tmp) / "b.json"
            for target in (first, second):
                code, err = quiet(M.main, ["--package", str(package), "--output", str(target)])
                self.assertEqual(code, 0)
            self.assertIn("bear 5 markers (3 in the pocket), usec 3 markers (2 in the pocket) (team bounds), no stage table", err)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            data = json.loads(first.read_text(encoding="utf-8"))
        self.assertEqual(data["counts"], {"capture_points": 3, "capture_zones": 0, "spawn_bounds": 4, "spawn_markers_bear": 5,
                                          "spawn_markers_usec": 3, "pocket_markers_bear": 3, "pocket_markers_usec": 2})
        self.assertEqual(data["side_rule"]["method"], "team_bounds")
        self.assertNotIn("stages", data)
        self.assertIn("CheckPoint", data["_comment"])
        self.assertNotIn("spawn_markers_north", data["counts"])

    def test_a_map_without_a_team_rule_ignores_the_arena_install(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = PackageTests().make_package(tmp, {"map_key": "arena_x"})
            data = quiet(M.extract_package, package, None, "no/such/arena")[0]
        self.assertNotIn("stages", data)
        self.assertEqual(data["counts"]["spawn_markers_north"], 1)


def pptr(path_id, file_id=0):
    return struct.pack("<iq", file_id, path_id)


def stage_bytes(game_object=7, team=1, initial=0, flag=1, links=(), markers=(), capture=(), null=0, name=b"", tail=b"",
                list_counts=None):
    """An ArenaSpawnBoundCheckPointData body as Arena stores it: the MonoBehaviour head, three ints, then three
    PPtr lists around one null PPtr."""
    head = pptr(game_object) + struct.pack("<i", 1) + pptr(99) + struct.pack("<i", len(name)) + name
    head += b"\0" * (-len(name) % 4)
    counts = list_counts or (len(links), len(markers), len(capture))
    body = struct.pack("<iii", team, initial, flag) + struct.pack("<i", counts[0]) + b"".join(pptr(p) for p in links)
    body += pptr(null) + struct.pack("<i", counts[1]) + b"".join(pptr(p) for p in markers)
    body += struct.pack("<i", counts[2]) + b"".join(pptr(p) for p in capture)
    return head + body + tail


class StageRecordDecodeTests(unittest.TestCase):
    def test_a_record_is_decoded_field_by_field(self):
        record = M.decode_stage_record(stage_bytes(game_object=139, team=1, initial=0, flag=1, links=[587, 12],
                                                   markers=[6, 7, 8], capture=[172], name=b"SPAWN_BOUND_FIGHTER (4)"))
        self.assertEqual(record, {"game_object": 139, "team": 1, "initial": False, "flag": 1, "links": [587, 12],
                                  "markers": [6, 7, 8], "capture": [172]})

    def test_empty_lists_and_an_unaligned_name_still_end_on_the_object_size(self):
        for name in (b"", b"a", b"ab", b"abc", b"abcd", b"abcde"):
            with self.subTest(name=name):
                record = M.decode_stage_record(stage_bytes(initial=1, team=0, name=name))
                self.assertEqual((record["links"], record["markers"], record["capture"], record["initial"]), ([], [], [], True))

    def test_the_layout_is_checked_against_the_object_size(self):
        good = stage_bytes(markers=[1, 2])
        with self.assertRaisesRegex(HardExtractError, "ended at"):
            M.decode_stage_record(good + b"\0\0\0\0")
        with self.assertRaisesRegex(HardExtractError, "shorter than its layout"):
            M.decode_stage_record(good[:-4])
        with self.assertRaisesRegex(HardExtractError, "claims 5 entries"):
            M.decode_stage_record(stage_bytes(markers=[1, 2], list_counts=(0, 5, 0)))
        with self.assertRaisesRegex(HardExtractError, "claims -1 entries"):
            M.decode_stage_record(stage_bytes(list_counts=(-1, 0, 0)))

    def test_a_layout_drift_is_refused_instead_of_misread(self):
        with self.assertRaisesRegex(HardExtractError, "null one"):
            M.decode_stage_record(stage_bytes(null=44))
        with self.assertRaisesRegex(HardExtractError, "expected 0 or 1"):
            M.decode_stage_record(stage_bytes(team=2))
        with self.assertRaisesRegex(HardExtractError, "expected 0 or 1"):
            M.decode_stage_record(stage_bytes(initial=-1))
        far = stage_bytes(markers=[3]).replace(pptr(3), pptr(3, file_id=1))
        with self.assertRaisesRegex(HardExtractError, "another file"):
            M.decode_stage_record(far)
        with self.assertRaisesRegex(HardExtractError, "bad m_Name length"):
            M.decode_stage_record(pptr(1) + struct.pack("<i", 1) + pptr(2) + struct.pack("<i", 9999))


def stage_world():
    """team_layout() plus the decoded records a consistent Arena would hold for it (GameObject path ids)."""
    scene = team_layout()
    found = team_extract(scene)
    ids = {o["name"]: int(o["id"]) for o in scene.objects}
    markers = found["spawn_markers"]["bear"] + found["spawn_markers"]["usec"]

    def names(side, pocket):
        return [ids[m["name"]] for m in markers if m["side"] == side and m["pocket"] == pocket]

    records = [
        {"game_object": ids["SPAWN_BOUND_FIGHTER"], "team": 1, "initial": True, "flag": 1, "links": [], "markers": names("bear", True), "capture": []},
        {"game_object": ids["SPAWN_BOUND_FIGHTER (1)"], "team": 1, "initial": False, "flag": 1, "links": [ids["SPAWN_BOUND_DEFENCE (1)"]],
         "markers": names("bear", False), "capture": [ids["CAPTURE_POINT_LIGHT_02_B"]]},
        {"game_object": ids["SPAWN_BOUND_DEFENCE"], "team": 0, "initial": True, "flag": 1, "links": [], "markers": names("usec", True), "capture": []},
        {"game_object": ids["SPAWN_BOUND_DEFENCE (1)"], "team": 0, "initial": False, "flag": 1, "links": [],
         "markers": names("usec", False), "capture": [ids["CAPTURE_POINT_LIGHT_02_C"], ids["CAPTURE_POINT_LIGHT_02_A"]]},
    ]
    return scene, found, ids, records


def build(scene, found, records, rule=RULE):
    return M.build_stages(records, scene, rule, found["spawn_markers"]["bear"] + found["spawn_markers"]["usec"],
                          found["spawn_bounds"], found["capture_points"])


class StageGraphTests(unittest.TestCase):
    def test_a_consistent_table_becomes_the_stage_list(self):
        scene, found, _ids, records = stage_world()
        stages = build(scene, found, records)
        self.assertEqual([(s["bound"], s["team"], s["initial"]) for s in stages],
                         [("SPAWN_BOUND_FIGHTER", "bear", True), ("SPAWN_BOUND_FIGHTER (1)", "bear", False),
                          ("SPAWN_BOUND_DEFENCE", "usec", True), ("SPAWN_BOUND_DEFENCE (1)", "usec", False)])
        by_bound = {s["bound"]: s for s in stages}
        self.assertEqual(by_bound["SPAWN_BOUND_FIGHTER (1)"]["links"], ["SPAWN_BOUND_DEFENCE (1)"])  # a link may cross teams
        self.assertEqual(by_bound["SPAWN_BOUND_FIGHTER (1)"]["capture_points"], ["B"])
        self.assertEqual(by_bound["SPAWN_BOUND_DEFENCE (1)"]["capture_points"], ["A", "C"])           # sorted labels
        self.assertEqual(by_bound["SPAWN_BOUND_FIGHTER"]["markers"], ["Bear house (5)", "Bear house (6)", "Bear house (7)"])
        self.assertEqual(by_bound["SPAWN_BOUND_FIGHTER (1)"]["flag"], 1)

    def test_markers_sort_by_their_number_not_their_text(self):
        scene = team_layout(bear_pocket=tuple((88.0 + i * 0.1, -90.0) for i in range(6)), bear_stage=((30.0, -60.0),))
        found = team_extract(scene)
        ids = {o["name"]: int(o["id"]) for o in scene.objects}
        markers = found["spawn_markers"]["bear"] + found["spawn_markers"]["usec"]
        pick = lambda side, pocket: [ids[m["name"]] for m in markers if m["side"] == side and m["pocket"] == pocket]  # noqa: E731
        records = [{"game_object": ids["SPAWN_BOUND_FIGHTER"], "team": 1, "initial": True, "flag": 1, "links": [],
                    "markers": pick("bear", True), "capture": []},
                   {"game_object": ids["SPAWN_BOUND_FIGHTER (1)"], "team": 1, "initial": False, "flag": 1, "links": [],
                    "markers": pick("bear", False), "capture": []},
                   {"game_object": ids["SPAWN_BOUND_DEFENCE"], "team": 0, "initial": True, "flag": 1, "links": [],
                    "markers": pick("usec", True), "capture": []},
                   {"game_object": ids["SPAWN_BOUND_DEFENCE (1)"], "team": 0, "initial": False, "flag": 1, "links": [],
                    "markers": pick("usec", False), "capture": []}]
        stages = build(scene, found, records)
        self.assertEqual(stages[0]["markers"], [f"Bear house ({i})" for i in range(5, 11)])   # (10) after (9), not after (1)

    def mutated(self, change):
        scene, found, ids, records = stage_world()
        change(records, ids)
        return build(scene, found, records)

    def test_every_disagreement_between_arena_and_the_package_fails_closed(self):
        cases = {
            "has no stage record": lambda r, ids: r.pop(1),
            "two stage records": lambda r, ids: r.append(dict(r[0])),
            "is not a spawn bound": lambda r, ids: r[0].update(game_object=ids["Bear house (5)"]),
            "says team 0": lambda r, ids: r[0].update(team=0),
            "has initial=True": lambda r, ids: r[1].update(initial=True),
            "links to a GameObject that is not a spawn bound": lambda r, ids: r[1].update(links=[ids["Bear house (5)"]]),
            "lists a GameObject that is not a spawn marker": lambda r, ids: r[1]["markers"].append(ids["SPAWN_BOUND_FIGHTER"]),
            "is listed by": lambda r, ids: r[1]["markers"].append(r[0]["markers"][0]),
            "of team usec": lambda r, ids: r[1]["markers"].append(ids["Usec house (5)"]),
            "belong to no stage record": lambda r, ids: r[1]["markers"].pop(),
            "serves a GameObject that is not a capture point light": lambda r, ids: r[1].update(capture=[ids["SPAWNS"]]),
            "the installed Arena is not the build": lambda r, ids: r[1]["markers"].append(424242),
            "initial record SPAWN_BOUND_FIGHTER lists": lambda r, ids: (r[0]["markers"].pop(), r[1]["markers"].append(ids["Bear house (7)"])),
        }
        for fragment, change in cases.items():
            with self.subTest(case=fragment), self.assertRaisesRegex(HardExtractError, fragment):
                self.mutated(change)

    def test_the_arena_read_is_only_attempted_with_an_arena_folder_and_names_what_is_missing(self):
        with self.assertRaisesRegex(HardExtractError, "does not exist"):
            M.read_stage_records("D:/no/such/arena", 376)
        with self.assertRaisesRegex(HardExtractError, "lists no scene"):
            M.scene_index_of(ROOT / "tools" / "extract" / "plans" / "iceberg.json", "Arena_Nope")
        self.assertEqual(M.scene_index_of(ROOT / "tools" / "extract" / "plans" / "iceberg.json", RULE.scene), 376)


# measured on the installed Arena 0.6.1.0.48206 / package iceberg-native-v1, 2026-10-30
ICEBERG_CAPTURE_POINTS = {"A": [16.77, 3.9, -91.39], "B": [10.614, 2.69, -77.727], "C": [21.91, 2.85, -47.3]}
# bound -> (team, capture points served, markers); the stage graph of level376's ArenaSpawnBoundCheckPointData
ICEBERG_STAGES = {
    "SPAWN_BOUND_FIGHTER": ("bear", [], list(range(5, 15))),
    "SPAWN_BOUND_FIGHTER (1)": ("bear", ["A"], list(range(15, 20))),
    "SPAWN_BOUND_FIGHTER (2)": ("bear", ["B"], [25, 26, 27, 29, 30]),
    "SPAWN_BOUND_FIGHTER (3)": ("bear", ["C"], list(range(31, 36))),
    "SPAWN_BOUND_FIGHTER (4)": ("bear", ["B"], [36, 38, 39, 40]),
    "SPAWN_BOUND_FIGHTER (5)": ("bear", ["A"], list(range(41, 47))),
    "SPAWN_BOUND_FIGHTER (6)": ("bear", ["C"], list(range(47, 52))),
    "SPAWN_BOUND_DEFENCE": ("usec", [], list(range(5, 15))),
    "SPAWN_BOUND_DEFENCE (1)": ("usec", ["B", "C"], [15, 16, 17, 19, 20]),
    "SPAWN_BOUND_DEFENCE (2)": ("usec", ["A", "B"], [18, 21, 22, 23, 24]),
    "SPAWN_BOUND_DEFENCE (4)": ("usec", ["A"], list(range(30, 35))),
    "SPAWN_BOUND_DEFENCE (5)": ("usec", ["C"], list(range(35, 40))),
}


class IcebergSidecarTests(unittest.TestCase):
    """tools/maps/iceberg.modes.json (PLAN-ICEBERG item 7): the data half needs only the file."""

    def data(self):
        path = MAPS / "iceberg.modes.json"
        if not path.is_file():
            self.skipTest("iceberg.modes.json not generated yet")
        return json.loads(path.read_text(encoding="utf-8"))

    def test_equals_a_fresh_extraction_of_the_package_and_the_stage_table_it_names(self):
        text = (MAPS / "iceberg.modes.json").read_text(encoding="utf-8")
        package = WORK / "extract" / json.loads(text)["source_package"]["name"]
        if not (package / "manifest.json").is_file():
            self.skipTest(f"{package.name} package is local only")
        if not (ARENA_DATA / "level376").is_file():
            self.skipTest("Arena is not installed (the stage table is read from level376)")
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            self.skipTest("UnityPy is not installed")
        data, _ = quiet(M.extract_package, package, None, str(ARENA_DATA))
        self.assertEqual(text, M.dumps(data))

    def test_the_package_alone_gives_the_same_file_without_the_stage_block(self):
        data = self.data()
        package = WORK / "extract" / data["source_package"]["name"]
        if not (package / "manifest.json").is_file():
            self.skipTest(f"{package.name} package is local only")
        plain, _ = quiet(M.extract_package, package)
        data = {k: v for k, v in data.items() if k not in ("stages", "stages_source")}
        self.assertEqual(M.dumps(data), M.dumps(plain))

    def test_counts_sides_and_the_rule_record(self):
        data = self.data()
        self.assertEqual(data["counts"], {"capture_points": 3, "capture_zones": 0, "spawn_bounds": 12, "spawn_markers_bear": 40,
                                          "spawn_markers_usec": 30, "pocket_markers_bear": 10, "pocket_markers_usec": 10})
        self.assertEqual(sorted(data["spawn_markers"]), ["bear", "usec"])
        self.assertNotIn("north", data["spawn_markers"])
        rule = data["side_rule"]
        self.assertEqual(rule["method"], "team_bounds")
        self.assertEqual({k: (v["markers"], v["pocket_markers"], v["stage_bounds"]) for k, v in rule["teams"].items()},
                         {"bear": (40, 10, 6), "usec": (30, 10, 4)})
        self.assertIsNone(data["capture_point"])

    def test_the_three_capture_points_are_arenas_a_b_c(self):
        points = {p["label"]: p["position"] for p in self.data()["capture_points"]}
        self.assertEqual(points, ICEBERG_CAPTURE_POINTS)

    def test_bear_starts_in_the_east_pocket_and_usec_in_the_west_one(self):
        data = self.data()
        bear = [m for m in data["spawn_markers"]["bear"] if m["pocket"]]
        usec = [m for m in data["spawn_markers"]["usec"] if m["pocket"]]
        self.assertEqual((sorted(m["index"] for m in bear), sorted(m["index"] for m in usec)), (list(range(5, 15)),) * 2)
        self.assertTrue(all(89.0 < m["position"][0] < 98.5 and -96.5 < m["position"][2] < -83.0 for m in bear))
        self.assertTrue(all(-45.0 < m["position"][0] < -36.5 and -95.5 < m["position"][2] < -79.5 for m in usec))
        # the stage markers are the respawn points of the capture stages and stay out of the pockets
        stage = [m for side in data["spawn_markers"].values() for m in side if not m["pocket"]]
        self.assertEqual(len(stage), 50)
        bounds = {b["name"]: b for b in data["spawn_bounds"] if b["initial"]}
        for marker in stage:
            for bound in bounds.values():
                self.assertLess(M.box_margin(bound, marker["position"]), -M.BOUND_EDGE_TOLERANCE, marker["name"])

    def test_the_pocket_bounds_are_the_two_unsuffixed_ones(self):
        bounds = {b["name"]: b for b in self.data()["spawn_bounds"]}
        self.assertEqual(sorted(n for n, b in bounds.items() if b["initial"]), ["SPAWN_BOUND_DEFENCE", "SPAWN_BOUND_FIGHTER"])
        self.assertEqual((bounds["SPAWN_BOUND_FIGHTER"]["side"], bounds["SPAWN_BOUND_DEFENCE"]["side"]), ("bear", "usec"))
        self.assertGreater(bounds["SPAWN_BOUND_FIGHTER"]["center"][0], 90.0)
        self.assertLess(bounds["SPAWN_BOUND_DEFENCE"]["center"][0], -35.0)

    def test_the_stage_graph_is_the_one_arena_stores(self):
        data = self.data()
        if "stages" not in data:
            self.fail("the tracked sidecar must carry the stage table (regenerate it with --arena-data)")
        got = {s["bound"]: (s["team"], s["capture_points"], [int(m.rsplit("(", 1)[1].rstrip(")")) for m in s["markers"]])
               for s in data["stages"]}
        self.assertEqual(got, ICEBERG_STAGES)
        self.assertEqual(data["stages_source"]["scene_index"], 376)
        self.assertEqual({s["flag"] for s in data["stages"]}, {1})
        every = [m["name"] for side in data["spawn_markers"].values() for m in side]
        listed = [m for s in data["stages"] for m in s["markers"]]
        self.assertEqual(sorted(every), sorted(listed))                       # all 70, each exactly once
        self.assertEqual(len(listed), 70)
        bounds = {b["name"] for b in data["spawn_bounds"]}
        self.assertEqual(bounds, set(ICEBERG_STAGES))
        for stage in data["stages"]:
            self.assertTrue(set(stage["links"]) <= bounds)

    def test_every_marker_lies_inside_the_bound_that_lists_it(self):
        data = self.data()
        if "stages" not in data:
            self.skipTest("no stage table in the sidecar")
        bounds = {b["name"]: b for b in data["spawn_bounds"]}
        markers = {m["name"]: m for side in data["spawn_markers"].values() for m in side}
        for stage in data["stages"]:
            for name in stage["markers"]:
                with self.subTest(bound=stage["bound"], marker=name):
                    # measured 2026-10-30: 0.225 m (Bear (29)) to 7.318 m, Usec (6) on the edge at -0.002 m
                    self.assertGreaterEqual(M.box_margin(bounds[stage["bound"]], markers[name]["position"]),
                                            -M.BOUND_EDGE_TOLERANCE)

    def test_the_released_maps_have_no_team_records(self):
        for stem in STEMS + ("bowl",):
            path = MAPS / f"{stem}.modes.json"
            if not path.is_file():
                continue
            data = json.loads(path.read_text(encoding="utf-8"))
            self.assertNotIn("capture_points", data)
            self.assertNotIn("stages", data)
            self.assertNotEqual(data.get("side_rule", {}).get("method"), "team_bounds")


if __name__ == "__main__":
    unittest.main()
