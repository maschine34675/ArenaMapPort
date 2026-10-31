"""Synthetic Iceberg geometry and integration checks; no game assets required."""
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import unittest

from test_dynamicmaps_export import exporter, owner, np

REPO = Path(__file__).resolve().parents[2]


def quad(x0, x1, z0, z1, y0, y1=None):
    y1 = y0 if y1 is None else y1
    a, b, c, d = [x0, y0, z0], [x1, y1, z0], [x1, y1, z1], [x0, y0, z1]
    return [a, b, c], [a, c, d]


@unittest.skipIf(exporter is None, "optional cartography dependencies unavailable")
class IcebergOverviewTests(unittest.TestCase):
    def test_3d_component_preserves_ramp_but_excludes_overlapping_roof_and_corner_touch(self):
        floor = quad(0, 5, 0, 5, 1)
        ramp = quad(5.000003, 10, 0, 5, 1, 4)
        roof = quad(0, 5, 0, 5, 5)
        corner = quad(10, 12, 5, 7, 4)
        selected = exporter.connected_triangles(np.array([*floor, *ramp, *roof, *corner]), [2, 1, 2])
        self.assertEqual(len(selected), 4)
        self.assertEqual(selected[:, :, 1].max(), 4)
        self.assertAlmostEqual(sum(exporter.Polygon(t[:, (0, 2)]).area for t in selected), 50, places=3)

    def test_bad_height_and_ambiguous_3d_anchors_are_refused(self):
        triangles = np.array([*quad(0, 5, 0, 5, 1), *quad(0, 5, 0, 5, 1.5)])
        for anchor in ([2, 4, 2], [2, 1.25, 2], [20, 1, 20]):
            with self.subTest(anchor=anchor), self.assertRaisesRegex(ValueError, "one native 3D component"):
                exporter.connected_triangles(triangles, anchor)

    def test_profile_tracks_reviewed_bounds_and_core_anchor(self):
        profile = exporter.MAP_PROFILES["arena_iceberg"]
        descriptor = json.loads((REPO / "tools/maps/iceberg.json").read_text())
        bounds = descriptor["bake_bounds"]
        self.assertEqual(profile["bounds"], [bounds[i] for i in (0, 4, 1, 5)])
        ai = json.loads((REPO / "tools/maps/iceberg.ai.json").read_text())
        core = next(p for p in ai["core_points"] if p["id"] == 3)
        np.testing.assert_allclose(profile["connected_anchor"], core["position"], atol=.001)
        self.assertEqual(profile["anchor"], tuple(profile["connected_anchor"][i] for i in (0, 2)))

    def test_pack_covers_main_surface_excludes_roofs_and_keeps_coordinate_contract(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root / "source"
            (package / "scenes").mkdir(parents=True)
            collider = {"type": "BoxCollider", "center": [27.5, 3, -81.5], "size": [175, .3, 93]}
            (package / "scenes/Test.json").write_text(json.dumps({"objects": [owner("floor", "floor", components=[collider])]}))
            descriptor = root / "iceberg.json"
            descriptor.write_text(json.dumps({"key": "arena_iceberg", "all_scene_names": ["Test"], "opened_barriers": []}))
            descriptor.with_suffix(".exits.json").write_text(json.dumps({"exits": [{"name": "East", "position": [72, 1.95, -94]}]}))
            triangles = np.array([*quad(-60, 115, -128, -35, 3.15), *quad(-60, 115, -128, -35, 5.8)])
            nav = root / "navmesh.json"
            nav.write_text(json.dumps({"vertices": [dict(zip(("x", "y", "z"), v)) for v in triangles.reshape(-1, 3)],
                                       "indices": list(range(triangles.size // 3))}))
            output = root / "output"
            with redirect_stdout(io.StringIO()):
                exporter.generate(package, descriptor, output, nav)
            definition = json.loads((output / "pack/ArenaMapPort_Iceberg.jsonc").read_text(encoding="utf-8"))
            receipt = json.loads((output / "receipt.json").read_text())
            self.assertEqual(definition["MapInternalNames"], ["arena_iceberg"])
            self.assertEqual(definition["CoordinateRotation"], 0)
            self.assertEqual(list(definition["Layers"]), ["Ground Floor"])
            layer = definition["Layers"]["Ground Floor"]
            self.assertEqual(layer["ImageBounds"], definition["Bounds"])
            self.assertEqual(layer["ImagePath"], "Maps/ArenaMapPort_Iceberg/Layers/Ground.svg")
            self.assertEqual(definition["StaticMarkers"][0]["Position"], {"x": 72, "y": -94, "z": 1.95})
            self.assertAlmostEqual(receipt["native_walkable_area_m2"], 175 * 93, places=2)
            self.assertEqual(receipt["bounds_xz"], [-62, -131, 121, -33])
            svg = (output / "pack/Layers/Ground.svg").read_text()
            self.assertIn('viewBox="0 0 183 98"', svg)
            self.assertIn('id="arena-north-marker"', svg)
            with self.assertRaisesRegex(ValueError, "requires native ground triangulation"):
                exporter.generate(package, descriptor, output)


if __name__ == "__main__":
    unittest.main()
