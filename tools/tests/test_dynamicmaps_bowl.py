"""Synthetic Bowl ground map checks (PLAN-BOWL item 17), independent of game assets.

The fixtures are invented rectangles. The mirror tests read only tracked sidecars
(tools/maps/bowl*.json), never game data, and pin the profile to them.
"""
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET
from test_dynamicmaps_export import exporter, owner

REPO = Path(__file__).resolve().parents[2]
EXITS = [
    {"name": "Bowl BTR Gate", "position": [51.0, 0.1, -31.0]},
    {"name": "Bowl Army Gate", "position": [-51.0, 0.1, -49.0]},
]
# Pitch area of the synthetic layout (see pitch_quads): north half, south half, bridge, east strip.
PITCH_AREA = 80 * 59.2 + 80 * 27.8 + 10 * 19 + 28 * 87  # 9,586 m2
GROUND_FILL, MUTED_FILL = "#b8c1c4", "#66747d"


def quad(vertices, indices, x0, x1, z0, z1, y):
    start = len(vertices)
    vertices.extend(dict(x=x, y=y, z=z) for x, z in [(x0, z0), (x1, z0), (x1, z1), (x0, z1)])
    indices.extend(start + i for i in (0, 1, 2, 0, 2, 3))


def pitch_quads():
    """A pitch with a 10 x 68 m hole (x 20..30, z -60..8) and a tile seam at z=-51.2.

    Bowl's real NavMesh tiles meet at z=-51.2 with a float-sized gap, like AirPit's.
    Above the 1.2 m ground band: a roof-like cell (Mi-26 top, y 1.6) and a stand-like
    cell (y 9); both lie outside the pitch and must never become ground.
    """
    vertices, indices = [], []
    quad(vertices, indices, -60, 20, -51.2, 8, .05)
    quad(vertices, indices, -60, 20, -79, -51.200003, .05)
    quad(vertices, indices, 20, 30, -79, -60, .05)
    quad(vertices, indices, 30, 58, -79, 8, .05)
    quad(vertices, indices, 60, 61.5, -30, -20, 1.6)
    quad(vertices, indices, -65, -63, -40, -30, 9.0)
    return {"vertices": vertices, "indices": indices}


def generate(root, objects=(), native=None, *, navmesh=True):
    """Build a synthetic Bowl package under root and run the real exporter; returns the output directory."""
    package = root / "source"
    (package / "scenes").mkdir(parents=True)
    floor = {"type": "BoxCollider", "center": [0, -.1, -35], "size": [140, .2, 100]}
    (package / "scenes/Test.json").write_text(json.dumps({"objects": [owner("floor", "floor", components=[floor]), *objects]}))
    descriptor = root / "bowl.json"
    descriptor.write_text(json.dumps({"key": "arena_bowl", "all_scene_names": ["Test"], "opened_barriers": []}))
    descriptor.with_suffix(".exits.json").write_text(json.dumps({"exits": EXITS}))
    nav = root / "navmesh.json"
    nav.write_text(json.dumps(native or pitch_quads()))
    output = root / "output"
    with redirect_stdout(io.StringIO()):
        exporter.generate(package, descriptor, output, nav if navmesh else None)
    return output


def box_under(path, name_suffix="_COLLIDER"):
    """A 4 x 2 x 4 m box standing in the pitch hole under the given hierarchy path."""
    objects, parent = [], None
    for index, name in enumerate(path):
        objects.append(owner(f"n{index}", name, parent=parent))
        parent = f"n{index}"
    collider = {"type": "BoxCollider", "center": [0, 1, 0], "size": [4, 2, 4]}
    objects.append(owner("box", path[-1] + name_suffix, parent=parent,
                         local_position=[25, .05, -30], components=[collider]))
    return objects


def read(output):
    receipt = json.loads((output / "receipt.json").read_text())
    layers = {p.attrib["fill"]: p.attrib["d"] for p in ET.parse(output / "pack/Layers/Ground.svg").getroot()
              if p.tag.endswith("path") and "fill" in p.attrib}
    return receipt, layers


def svg_y(path):
    return [float(y) for _, y in re.findall(r"([\d.-]+),([\d.-]+)", path)]


def svg_x(path):
    return [float(x) for x, _ in re.findall(r"([\d.-]+),([\d.-]+)", path)]


@unittest.skipIf(exporter is None, "optional cartography dependencies unavailable")
class BowlOverviewTests(unittest.TestCase):
    def test_pack_identity_bounds_labels_and_exit_markers(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = generate(Path(temporary))
            actual = json.loads((output / "pack/ArenaMapPort_Bowl.jsonc").read_text(encoding="utf-8"))
            receipt, _ = read(output)
            rect = {"Min": {"x": -66, "y": -82}, "Max": {"x": 62, "y": 11}}
            layer = actual["Layers"]["Ground Floor"]
            self.assertEqual(actual["MapInternalNames"], ["arena_bowl"])
            self.assertEqual(actual["DisplayName"], "Arena: Bowl — ground plan")
            self.assertEqual(list(actual["Layers"]), ["Ground Floor"])
            self.assertEqual((actual["CoordinateRotation"], actual["DefaultLevel"], layer["Level"]), (0, 0, 0))
            self.assertEqual(layer["ImagePath"], "Maps/ArenaMapPort_Bowl/Layers/Ground.svg")
            self.assertEqual((actual["Bounds"], layer["ImageBounds"]), (rect, rect))
            self.assertEqual(layer["GameBounds"], [{"Min": {**rect["Min"], "z": -8}, "Max": {**rect["Max"], "z": 12}}])
            self.assertEqual(actual["Labels"][0]["Text"], "BOWL / GROUND")
            self.assertEqual(receipt["bounds_xz"], [-66, -82, 62, 11])
            # World (x, y, z) becomes map (x, z, y); the exits stay outside raids only.
            self.assertEqual([(m["Text"], m["Position"], m["ShowInRaid"]) for m in actual["StaticMarkers"]], [
                ("Bowl BTR Gate", {"x": 51.0, "y": -31.0, "z": 0.1}, False),
                ("Bowl Army Gate", {"x": -51.0, "y": -49.0, "z": 0.1}, False)])
            self.assertEqual(sorted(p.relative_to(output / "pack").as_posix() for p in (output / "pack").rglob("*") if p.is_file()),
                             ["ArenaMapPort_Bowl.jsonc", "Layers/Ground.svg"])
            drawing = ET.parse(output / "pack/Layers/Ground.svg").getroot()
            self.assertEqual(drawing.attrib["viewBox"], "0 0 128 93")
            self.assertIn("arena-north-marker", (output / "pack/Layers/Ground.svg").read_text())

    def test_tile_seam_is_closed_and_elevated_cells_never_become_ground(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = generate(Path(temporary))
            receipt, layers = read(output)
            # Without the 1 cm seam grid the 3 micrometre gap at z=-51.2 would leave only the north half
            # (4,736 m2) connected to the anchor; with it the whole pitch is one component.
            self.assertAlmostEqual(receipt["atrium_component_area_m2"], PITCH_AREA, delta=1)
            # The 1.5 x 10 m roof cell (y 1.6) and the 2 x 10 m stand cell (y 9) are outside the ground band.
            self.assertAlmostEqual(receipt["native_walkable_area_m2"], PITCH_AREA, delta=1)
            self.assertEqual(layers[MUTED_FILL], "")  # one component: nothing muted
            ground_y, ground_x = svg_y(layers[GROUND_FILL]), svg_x(layers[GROUND_FILL])
            self.assertEqual((min(ground_y), max(ground_y)), (3, 90))  # z 8 -> y 3, z -79 -> y 90
            self.assertEqual((min(ground_x), max(ground_x)), (6, 124))  # x -60 .. 58
            self.assertEqual(receipt["ground_profile"]["anchor"], [2.5, -21.1])
            self.assertEqual(receipt["obstacle_tolerance_m"], .20)

    def test_obstacles_stay_in_the_holes_and_stadium_paths_are_hidden_by_whole_segments(self):
        stadium = ["SBG_Arena_Bowl_Building", "SOO_LOD0", "BUILDING", "Stadium"]
        cases = {
            "ordinary container": (box_under(["SBG_Arena_Bowl", "OO", "Container"]), True),
            "stadium shell": (box_under(stadium), False),
            "stadium seating": (box_under(["SBG_Arena_Bowl_Building", "OO", "Stadium_Chairs_F1"]), False),
            "name that only shares the prefix": (box_under(stadium[:3] + ["StadiumGate"]), True),
        }
        for label, (objects, visible) in cases.items():
            with self.subTest(label), tempfile.TemporaryDirectory() as temporary:
                receipt, layers = read(generate(Path(temporary), objects))
                low, high = receipt["areas_m2"]["low"], receipt["areas_m2"]["high"]
                if visible:
                    self.assertGreater(low, 1)
                    self.assertGreater(high, 1)
                    # The box (x 23..27, z -32..-28) sits in the hole: SVG x 89..93, y 39..43.
                    self.assertTrue(any(88.5 <= x <= 93.5 for x in svg_x(layers["#8d8167"])))
                else:
                    self.assertEqual((low, high), (0, 0))
                # Counted colliders: the floor plate always, the box only when it is not hidden.
                self.assertEqual(receipt["colliders"]["BoxCollider"], 2 if visible else 1)

    def test_meshless_mesh_colliders_are_skipped_but_malformed_ids_still_fail(self):
        base = {"type": "MeshCollider", "enabled": True, "is_trigger": False, "convex": False, "physic_material": None}
        with tempfile.TemporaryDirectory() as temporary:
            # Bowl's two Cabin_ladder colliders have no sharedMesh in the source (mesh_id null).
            receipt, _ = read(generate(Path(temporary), [owner("m", "Cabin_ladder_COLLIDER", components=[{**base, "mesh_id": None}])]))
            self.assertEqual(receipt["meshless_mesh_colliders_skipped"], 1)
            self.assertNotIn("MeshCollider", receipt["colliders"])
        with tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "Invalid mesh ID"):
            generate(Path(temporary), [owner("m", "Broken", components=[{**base, "mesh_id": "ZZ-not-hex"}])])

    def test_bowl_requires_native_ground_triangulation(self):
        with tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "Bowl requires native ground triangulation"):
            generate(Path(temporary), navmesh=False)


@unittest.skipIf(exporter is None, "optional cartography dependencies unavailable")
class BowlProfileMirrorTests(unittest.TestCase):
    """The profile follows the tracked Bowl sidecars; a changed bake volume or anchor must change both."""
    profile = exporter.MAP_PROFILES["arena_bowl"] if exporter else None

    def sidecar(self, name):
        return json.loads((REPO / "tools/maps" / name).read_text(encoding="utf-8"))

    def test_bounds_equal_the_descriptor_bake_volume(self):
        x0, x1, _, _, z0, z1 = self.sidecar("bowl.json")["bake_bounds"]
        self.assertEqual(self.profile["bounds"], [x0, z0, x1, z1])
        self.assertEqual(self.sidecar("bowl.json")["key"], "arena_bowl")

    def test_anchor_is_arenas_core_point_two_and_the_naming_is_fixed(self):
        spawns = {s["id"]: s["position"] for s in self.sidecar("bowl.bots.json")["bot_spawns"]}
        x, _, z = spawns["arena_bowl-bot-core-02"]
        self.assertEqual(tuple(self.profile["anchor"]), (x, z))
        self.assertEqual((self.profile["name"], self.profile["title"], self.profile["folder"]), ("Bowl", "BOWL", "ArenaMapPort_Bowl"))
        self.assertTrue(self.profile["relative"])
        self.assertEqual(self.profile["heights"], (-.5, 1.2))

    def test_exit_sidecar_matches_the_synthetic_fixture_and_lies_inside_the_drawing(self):
        exits = self.sidecar("bowl.exits.json")["exits"]
        self.assertEqual([(e["name"], e["position"]) for e in exits], [(e["name"], e["position"]) for e in EXITS])
        x0, z0, x1, z1 = self.profile["bounds"]
        for item in exits:
            self.assertTrue(x0 < item["position"][0] < x1 and z0 < item["position"][2] < z1, item["name"])

    def test_hidden_paths_are_exact_hierarchy_prefixes_of_the_building_scene(self):
        for path in self.profile["hidden_paths"]:
            self.assertTrue(path.startswith("SBG_Arena_Bowl_Building/"), path)
            self.assertFalse(path.endswith("/"), path)


if __name__ == "__main__":
    unittest.main()
