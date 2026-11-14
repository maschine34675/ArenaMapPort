"""Chop Shop's sloping routes, detached roofs and negative world coordinates."""
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import unittest

from test_dynamicmaps_export import exporter, owner, np
from test_dynamicmaps_iceberg import quad

REPO = Path(__file__).resolve().parents[2]


@unittest.skipIf(exporter is None, "optional cartography dependencies unavailable")
class ChopShopOverviewTests(unittest.TestCase):
    def test_bounds_and_anchor_follow_bake_and_original_ai(self):
        profile = exporter.MAP_PROFILES["arena_autoservice"]
        descriptor = json.loads((REPO / "tools/maps/autoservice.json").read_text())
        bounds = descriptor["bake_bounds"]
        self.assertEqual(profile["bounds"], [bounds[i] for i in (0, 4, 1, 5)])
        ai = json.loads((REPO / "tools/maps/autoservice.ai.json").read_text())
        core = next(p for p in ai["core_points"] if p["id"] == 3)
        np.testing.assert_allclose(profile["connected_anchor"], core["position"], atol=.001)

    def test_generated_pack_keeps_raised_route_but_omits_disconnected_roof(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root / "source"
            (package / "scenes").mkdir(parents=True)
            collider = {"type": "BoxCollider", "center": [-375, -4, 485], "size": [70, .3, 30]}
            (package / "scenes/Test.json").write_text(json.dumps({"objects": [owner("floor", "floor", components=[collider])]}))
            descriptor = root / "autoservice.json"
            descriptor.write_text(json.dumps({"key": "arena_autoservice", "all_scene_names": ["Test"], "opened_barriers": []}))
            descriptor.with_suffix(".exits.json").write_text(json.dumps({"exits": [{"name": "East", "position": [-340.5, -3.6, 485]}]}))
            floor = quad(-410, -340, 470, 500, -3.8)
            ramp = quad(-340, -334, 470, 500, -3.8, -.45)
            roof = quad(-413, -340, 470, 500, -1.25)
            triangles = np.array([*floor, *ramp, *roof])
            nav = root / "navmesh.json"
            nav.write_text(json.dumps({"vertices": [dict(zip(("x", "y", "z"), v)) for v in triangles.reshape(-1, 3)],
                                       "indices": list(range(triangles.size // 3))}))
            output = root / "output"
            with redirect_stdout(io.StringIO()):
                exporter.generate(package, descriptor, output, nav)
            definition = json.loads((output / "pack/ArenaMapPort_AutoService.jsonc").read_text(encoding="utf-8"))
            receipt = json.loads((output / "receipt.json").read_text())
            self.assertEqual(definition["MapInternalNames"], ["arena_autoservice"])
            self.assertIn("Chop Shop", definition["DisplayName"])
            self.assertEqual(definition["CoordinateRotation"], 0)
            self.assertEqual(list(definition["Layers"]), ["Ground Floor"])
            layer = definition["Layers"]["Ground Floor"]
            self.assertEqual(layer["ImageBounds"], definition["Bounds"])
            self.assertEqual(layer["ImagePath"], "Maps/ArenaMapPort_AutoService/Layers/Ground.svg")
            self.assertEqual(definition["StaticMarkers"][0]["Position"], {"x": -340.5, "y": 485, "z": -3.6})
            self.assertAlmostEqual(receipt["native_walkable_area_m2"], 76 * 30, places=2)
            self.assertEqual(receipt["bounds_xz"], [-415, 464, -331, 515])
            svg = (output / "pack/Layers/Ground.svg").read_text()
            self.assertIn('viewBox="0 0 84 51"', svg)
            self.assertIn('id="arena-north-marker"', svg)
            with self.assertRaisesRegex(ValueError, "requires native ground triangulation"):
                exporter.generate(package, descriptor, output)


if __name__ == "__main__":
    unittest.main()
