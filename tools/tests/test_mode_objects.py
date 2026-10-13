"""PLAN-AIRPIT item 4 -- mode-object sidecar extractor (tools/extract/extract_mode_objects.py).

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
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
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
        g = graph([obj(1, "SPAWNS", active=False), obj(2, "ScavPMCspawn (1)", parent=1, pos=(0, 0, 2))])
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
                package = ROOT / "work" / "extract" / json.loads(text)["source_package"]["name"]
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
        # scenes are still byte-identical, again only source_package (name, extracted_utc) changed.
        accepted = ROOT / "work" / "extract" / "airpit-native-v8"
        if not (accepted / "manifest.json").is_file():
            self.skipTest("airpit-native-v8 not extracted yet (PLAN-AIRPIT item 5/13, furnished shops and hall decoration)")
        name = json.loads(self.sidecar("airpit").read_text(encoding="utf-8"))["source_package"]["name"]
        self.assertEqual(
            name, "airpit-native-v8",
            "airpit.modes.json still names the superseded package %r; regenerate it: python "
            "tools/extract/extract_mode_objects.py --package work/extract/airpit-native-v8 "
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


if __name__ == "__main__":
    unittest.main()
