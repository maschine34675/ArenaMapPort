"""Block's two team pockets and connected raised routes, without projected roofs."""
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
class BlockOverviewTests(unittest.TestCase):
    def test_profile_keeps_original_core_and_extends_only_the_documented_west_alley(self):
        profile = exporter.MAP_PROFILES["arena_yard"]
        descriptor = json.loads((REPO / "tools/maps/yard.json").read_text())
        # The source's west dead-end alley lies at x -76..-67, outside the bot bake.
        self.assertEqual(profile["bounds"][0], -77)
        self.assertEqual(profile["bounds"][1:], [descriptor["bake_bounds"][i] for i in (4, 1, 5)])
        self.assertLess(profile["bounds"][0], descriptor["bake_bounds"][0])
        ai = json.loads((REPO / "tools/maps/yard.ai.json").read_text())
        core = next(p for p in ai["core_points"] if p["id"] == 3)
        np.testing.assert_allclose(profile["connected_anchor"], core["position"], atol=.001)
        self.assertEqual(profile["name"], descriptor["locale_name"].removeprefix("Arena: "))

    def test_both_pockets_and_raised_route_survive_but_overlapping_roof_does_not(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root / "source"
            (package / "scenes").mkdir(parents=True)
            collider = {"type": "BoxCollider", "center": [-30, 6, 80], "size": [60, .3, 140]}
            alley = {"type": "BoxCollider", "center": [-67.5, 6, 65], "size": [15, .3, 50]}
            (package / "scenes/Test.json").write_text(json.dumps({"objects": [
                owner("floor", "floor", components=[collider]), owner("alley", "alley", components=[alley])]}))
            descriptor = root / "yard.json"
            descriptor.write_text(json.dumps({"key": "arena_yard", "all_scene_names": ["Test"], "opened_barriers": []}))
            descriptor.with_suffix(".exits.json").write_text(json.dumps({"exits": [
                {"name": "South", "position": [-25, 6.15, 5]}, {"name": "North", "position": [-25, 6.15, 155]}]}))
            floor = quad(-60, 0, 10, 150, 6.15)
            south = quad(-60, 0, 1, 10, 6.15)
            north = quad(-60, 0, 150, 160, 6.15)
            ramp = quad(0, 10, 10, 150, 6.15, 10.7)
            # Within the height band and over the playable footprint, but detached in 3D.
            roof = quad(-65, 20, 1, 160, 9.5)
            triangles = np.array([*floor, *south, *north, *ramp, *roof])
            nav = root / "navmesh.json"
            nav.write_text(json.dumps({"vertices": [dict(zip(("x", "y", "z"), v)) for v in triangles.reshape(-1, 3)],
                                       "indices": list(range(triangles.size // 3))}))
            output = root / "output"
            with redirect_stdout(io.StringIO()):
                exporter.generate(package, descriptor, output, nav)
            definition = json.loads((output / "pack/ArenaMapPort_Yard.jsonc").read_text(encoding="utf-8"))
            receipt = json.loads((output / "receipt.json").read_text())
            self.assertEqual(definition["MapInternalNames"], ["arena_yard"])
            self.assertEqual(definition["DisplayName"], "Arena: Block — ground plan")
            self.assertEqual(definition["CoordinateRotation"], 0)
            self.assertEqual(list(definition["Layers"]), ["Ground Floor"])
            layer = definition["Layers"]["Ground Floor"]
            self.assertEqual(layer["ImageBounds"], definition["Bounds"])
            self.assertEqual(layer["ImagePath"], "Maps/ArenaMapPort_Yard/Layers/Ground.svg")
            self.assertEqual([p["Position"] for p in definition["StaticMarkers"]],
                             [{"x": -25, "y": 5, "z": 6.15}, {"x": -25, "y": 155, "z": 6.15}])
            self.assertAlmostEqual(receipt["native_walkable_area_m2"], 60 * 159 + 10 * 140, places=2)
            self.assertEqual(receipt["bounds_xz"], [-77, -1, 21, 162])
            selected = exporter.native_triangles(nav, (5.8, 11), (-5.08, 6.15, 57.92))
            self.assertAlmostEqual(selected[:, :, 1].max(), 10.7)
            self.assertEqual(len(selected), 8)
            svg = (output / "pack/Layers/Ground.svg").read_text()
            self.assertIn('viewBox="0 0 98 163"', svg)
            self.assertIn('id="arena-north-marker"', svg)
            from PIL import Image
            with Image.open(output / "preview.png") as preview:
                # The physical alley is visible beyond the bake, but never promoted
                # to the light, navmesh-backed route colour.
                self.assertEqual(preview.getpixel((50, 970)), (70, 84, 94))
            with self.assertRaisesRegex(ValueError, "requires native ground triangulation"):
                exporter.generate(package, descriptor, output)


if __name__ == "__main__":
    unittest.main()
