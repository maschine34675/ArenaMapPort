"""Synthetic AirPit ground map checks, independent of game assets."""
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET
from test_dynamicmaps_export import exporter, owner


@unittest.skipIf(exporter is None, "optional cartography dependencies unavailable")
class AirPitOverviewTests(unittest.TestCase):
    def test_ground_only_map_keeps_south_apron_across_tile_seam_and_excludes_upper(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root / "source"
            (package / "scenes").mkdir(parents=True)
            floor = {"type": "BoxCollider", "center": [0, -.1, 1.5], "size": [20, .2, 163]}
            (package / "scenes/Test.json").write_text(json.dumps({"objects": [owner("floor", components=[floor])]}))
            descriptor = root / "airpit.json"
            descriptor.write_text(json.dumps({"key": "arena_airpit", "all_scene_names": ["Test"]}))
            descriptor.with_suffix(".exits.json").write_text('{"exits": []}')
            vertices, indices = [], []
            def quad(z0, z1, y):
                start = len(vertices)
                vertices.extend(dict(x=x, y=y, z=z) for x,z in [(-10,z0),(10,z0),(10,z1),(-10,z1)])
                indices.extend(start+i for i in [0,1,2,0,2,3])
            quad(-51.2, 20, .05)
            quad(-80, -51.200003, .05)
            quad(20, 83, .05)  # original northern team box, beyond the old Z73 drawing
            quad(84, 85, 4.25)  # spectator deck must not become a floor
            native = root / "navmesh.json"
            native.write_text(json.dumps({"vertices": vertices, "indices": indices}))
            output = root / "output"
            with redirect_stdout(io.StringIO()):
                exporter.generate(package, descriptor, output, native)
            actual = json.loads((output / "pack/ArenaMapPort_AirPit.jsonc").read_text())
            receipt = json.loads((output / "receipt.json").read_text())
            self.assertEqual(actual["MapInternalNames"], ["arena_airpit"])
            self.assertEqual(list(actual["Layers"]), ["Ground Floor"])
            self.assertEqual(actual["CoordinateRotation"], 0)
            self.assertEqual(receipt["bounds_xz"], [-60,-86,40,86])
            self.assertEqual(receipt["obstacle_tolerance_m"], .20)
            self.assertAlmostEqual(receipt["atrium_component_area_m2"], 3260, delta=.1)
            self.assertAlmostEqual(receipt["native_walkable_area_m2"], 3260, delta=.1)
            rect = {"Min": {"x": -60, "y": -86}, "Max": {"x": 40, "y": 86}}
            layer = actual["Layers"]["Ground Floor"]
            self.assertEqual(actual["Bounds"], rect)
            self.assertEqual(layer["ImageBounds"], rect)
            self.assertEqual(layer["GameBounds"], [{"Min": {**rect["Min"], "z": -8},
                                                   "Max": {**rect["Max"], "z": 12}}])
            drawing = ET.parse(output / "pack/Layers/Ground.svg").getroot()
            self.assertEqual(drawing.attrib["viewBox"], "0 0 100 172")
            ground = next(p for p in drawing if p.attrib.get("fill") == "#b8c1c4")
            # The native ground rectangle reaches both spawn boxes in SVG coordinates.
            svg_y = [float(y) for _, y in re.findall(r"([\d.-]+),([\d.-]+)", ground.attrib["d"])]
            self.assertEqual(min(svg_y), 3)  # north Z83 -> SVG Y3
            self.assertEqual(max(svg_y), 166)  # south Z-80 -> SVG Y166
            self.assertEqual(len(list((output / "pack").rglob("*.svg"))), 1)
            self.assertIn('arena-north-marker', (output / "pack/Layers/Ground.svg").read_text())


if __name__ == "__main__":
    unittest.main()
