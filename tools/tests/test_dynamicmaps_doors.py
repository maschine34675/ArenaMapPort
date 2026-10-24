"""Synthetic gate-pose cartography tests; no extracted game assets required."""
from contextlib import redirect_stdout
from copy import deepcopy
import io
import json
import math
from pathlib import Path
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET

from test_dynamicmaps_export import exporter, owner, np


def pose(barrier="Gate", part="Leaf", node="", closed=(0, 0, 0), opened=(4, 0, 0), **extras):
    return {"barrier": barrier, "part": part, "node": node, "source": "dotanimator",
            "closed": {"position": list(closed), "euler": [0, 0, 0]},
            "open": {"position": list(opened), "euler": [0, 0, 0]}, **extras}


@unittest.skipIf(exporter is None, "optional cartography dependencies unavailable")
class DoorPoseTests(unittest.TestCase):
    def tearDown(self):
        exporter.Scene.world.cache_clear()
        exporter.Scene.lineage.cache_clear()

    def test_duplicate_roots_match_original_world_at_before_root_and_child_move(self):
        objects = [owner("r1", "Gate", local_position=[10, 0, 0]),
                   owner("l1", "Leaf", "r1"), owner("r2", "Gate", local_position=[20, 0, 0]),
                   owner("l2", "Leaf", "r2")]
        rows = [pose(part="", closed=(10, 0, 0), opened=(50, 0, 0), at=[10, 0, 0]),
                pose(at=[10, 0, 0]), pose(opened=(-3, 0, 0), at=[20, 0, 0])]
        for order in (rows, rows[::-1]):
            scene = exporter.Scene({"objects": deepcopy(objects)})
            self.assertEqual(dict(scene.apply_open_poses(order)), {0: 1, 1: 1, 2: 1})
            np.testing.assert_allclose(scene.world("l1")[:3, 3], [54, 0, 0])
            np.testing.assert_allclose(scene.world("l2")[:3, 3], [17, 0, 0])
        self.assertEqual(objects[0]["local_position"], [10, 0, 0])

    def test_repeated_passport_leaf_names_use_closed_local_position(self):
        scene = exporter.Scene({"objects": [owner("root", "Gate"),
            owner("a", "Leaf", "root", local_position=[1, 0, 0]),
            owner("b", "Leaf", "root", local_position=[3, 0, 0])]})
        scene.apply_open_poses([pose(closed=(1, 0, 0), opened=(2, 0, 0)),
                               pose(closed=(3, 0, 0), opened=(4, 0, 0))])
        self.assertEqual(scene.objects["a"]["local_position"], [2, 0, 0])
        self.assertEqual(scene.objects["b"]["local_position"], [4, 0, 0])

    def test_unity_mixed_axis_order_matches_independent_elementary_matrices(self):
        x, y, z = np.radians([31, -64, 22])
        c, s = math.cos, math.sin
        rx = np.array([[1, 0, 0], [0, c(x), -s(x)], [0, s(x), c(x)]])
        ry = np.array([[c(y), 0, s(y)], [0, 1, 0], [-s(y), 0, c(y)]])
        rz = np.array([[c(z), -s(z), 0], [s(z), c(z), 0], [0, 0, 1]])
        actual = exporter.local_matrix(owner("r", local_rotation=exporter.unity_euler_quaternion([31, -64, 22])))
        np.testing.assert_allclose(actual[:3, :3], ry @ rx @ rz, atol=1e-12)

    def test_slide_only_preserves_scene_rotation_within_tolerance(self):
        rotation = [0, math.sin(math.radians(.5)), 0, math.cos(math.radians(.5))]
        scene = exporter.Scene({"objects": [owner("root", "Gate"),
            owner("leaf", "Leaf", "root", local_rotation=rotation)]})
        scene.apply_open_poses([pose()])
        self.assertEqual(scene.objects["leaf"]["local_rotation"], rotation)

    def test_posed_barrier_keeps_structure_and_static_row_but_hidden_paths_still_win(self):
        scene = exporter.Scene({"objects": [owner("root", "Gate"), owner("leaf", "Leaf", "root"),
            owner("post", "Telfer", "root"), owner("back", "Bus (3)", "root"),
            owner("closed", "Gate (3)")]})
        scene.apply_open_poses([pose()])
        for key in ("leaf", "post", "back", "closed"):
            self.assertTrue(scene.included(key, ["Gate"]))
        self.assertFalse(scene.included("post", ["Gate"], ["Gate/Telfer"]))
        self.assertTrue(scene.moved("leaf"))
        self.assertFalse(scene.moved("post"))

    def test_switch_changes_only_active_state_and_obeys_inactive_ancestors(self):
        scene = exporter.Scene({"objects": [owner("root", "Gate"), owner("leaf", "Leaf", "root"),
            owner("red", "Red", "leaf"), owner("green", "Green", "leaf", active=False)]})
        rows = [pose(node="Red", switch={"closed": True, "open": False}),
                pose(node="Green", switch={"closed": False, "open": True})]
        scene.apply_open_poses(rows)
        self.assertFalse(scene.included("red", ["Gate"]))
        self.assertTrue(scene.included("green", ["Gate"]))
        self.assertFalse(scene.moved("green"))
        np.testing.assert_allclose(scene.world("green")[:3, 3], [0, 0, 0])
        scene.objects["leaf"]["active"] = False
        self.assertFalse(scene.included("green", ["Gate"]))

    def test_stale_ambiguous_and_duplicate_targets_fail(self):
        cases = [([owner("root", "Gate"), owner("leaf", "Leaf", "root", local_position=[1, 0, 0])], [pose()]),
                 ([owner("root", "Gate"), owner("a", "Leaf", "root"), owner("b", "Leaf", "root")], [pose()]),
                 ([owner("root", "Gate"), owner("leaf", "Leaf", "root")], [pose(), pose()])]
        for objects, rows in cases:
            with self.subTest(objects=objects), self.assertRaises(ValueError):
                exporter.Scene({"objects": objects}).apply_open_poses(rows)

    def test_generation_parked_obstacle_overrides_static_navmesh_and_roller_rises(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root / "source"
            (package / "scenes").mkdir(parents=True)
            collider = {"type": "BoxCollider", "center": [0, 1, 0], "size": [2, 2, 2]}
            scene = {"objects": [owner("floor", components=[{"type": "BoxCollider", "center": [0, -.1, 0], "size": [20, .2, 20]}]),
                owner("gate", "Gate"), owner("leaf", "Leaf", "gate", components=[collider]),
                owner("roller", "Roller", components=[collider]),
                owner("ordinary", "Ordinary", local_position=[-4, 0, 0], components=[collider])]}
            (package / "scenes/Test.json").write_text(json.dumps(scene))
            descriptor = root / "airpit.json"
            descriptor.write_text(json.dumps({"key": "arena_airpit", "all_scene_names": ["Test"], "opened_barriers": ["Gate", "Roller"]}))
            descriptor.with_suffix(".exits.json").write_text('{"exits": []}')
            rows = [pose(), pose(barrier="Roller", part="", opened=(0, 5, 0))]
            sidecar = {"schema_version": 2, "key": "arena_airpit", "poses": rows}
            doors = descriptor.with_suffix(".doors.json")
            doors.write_text(json.dumps(sidecar))
            navmesh = root / "navmesh.json"
            navmesh.write_text(json.dumps({"vertices": [dict(x=x, y=0, z=z) for x, z in [(-10, -10), (10, -10), (10, 10), (-10, 10)]],
                                           "indices": [0, 1, 2, 0, 2, 3]}))
            def generate():
                with redirect_stdout(io.StringIO()):
                    exporter.generate(package, descriptor, root / "out", navmesh)
            generate()
            receipt = json.loads((root / "out/receipt.json").read_text())
            # A moved box remains at x=4; the raised roller is above the slab, and
            # unrelated ordinary geometry on the native floor retains old subtraction.
            self.assertEqual(receipt["door_poses"]["matched_rows"], 2)
            # A tall closed collider projects as slab contours, not a filled AABB.
            self.assertGreater(receipt["areas_m2"]["high"], .5)
            self.assertGreater(receipt["door_poses"]["parked_high_area_m2"], .5)
            drawing = ET.parse(root / "out/pack/Layers/Ground.svg").getroot()
            high = next(p for p in drawing if p.attrib.get("fill") == "#293641")
            xs = [float(x)-60 for x, _ in re.findall(r"([\d.-]+),([\d.-]+)", high.attrib["d"])]
            self.assertGreater(min(xs), 2.8)
            self.assertLess(max(xs), 5.2)
            self.assertIn(str(doors.resolve()), receipt["inputs_sha256"])
            sidecar["poses"].append(pose(barrier="Missing"))
            doors.write_text(json.dumps(sidecar))
            with self.assertRaisesRegex(ValueError, "resolve once"):
                generate()
            sidecar["key"] = "arena_bowl"
            doors.write_text(json.dumps(sidecar))
            with self.assertRaisesRegex(ValueError, "identity"):
                generate()


if __name__ == "__main__":
    unittest.main()
