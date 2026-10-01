"""Independent synthetic tests for Bay5's sloped-ground DynamicMaps export."""
from __future__ import annotations

from contextlib import redirect_stdout
import importlib.util
import io
import json
from pathlib import Path
import re
import struct
import tempfile
import unittest
import xml.etree.ElementTree as ET

try:
    import numpy as np
    from shapely import GeometryCollection, Polygon, box
except ImportError:
    np = None

exporter = None
if np is not None:
    source = Path(__file__).resolve().parents[1] / "dynamicmaps" / "export_equator.py"
    spec = importlib.util.spec_from_file_location("bay5_export_under_test", source)
    exporter = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(exporter)


def write_native(path, vertices, indices):
    path.write_text(json.dumps({"vertices": [dict(zip(("x", "y", "z"), v)) for v in vertices],
                                "indices": indices}), encoding="utf-8")


def write_mesh(path, vertices, indices):
    """Only the documented position channel and a uint16 triangle submesh."""
    content = bytearray(struct.pack("<4sIIHBB", b"AMPM", 1, len(vertices), 1, 0, 1))
    for vertex in vertices:
        content.extend(struct.pack("<3f", *vertex))
    content.extend(struct.pack("<IIB", 0, len(indices), 0))
    content.extend(struct.pack("<" + "H" * len(indices), *indices))
    path.write_bytes(content)


def owner(key, components):
    return {"id": key, "parent_id": None, "name": key, "active": True, "layer": 11,
            "local_position": [0, 0, 0], "local_rotation": [0, 0, 0, 1],
            "local_scale": [1, 1, 1], "components": components}


@unittest.skipIf(exporter is None, "Bay5 exporter tests require optional numpy and shapely")
class GroundSurfaceTests(unittest.TestCase):
    def slope(self, reverse=False):
        # Plane y = -4 + x/4 + z/2, limited to a triangular footprint.
        triangle = np.array([[0., -4., 0.], [4., -3., 0.], [0., -2., 4.]])
        return exporter.GroundSurface(np.array([triangle[::-1] if reverse else triangle]),
                                      box(-10, -10, 10, 10))

    def test_interpolates_sloped_triangle_in_world_xz(self):
        xz = [[0, 0], [4, 0], [0, 4], [1, 1], [2, 1]]
        np.testing.assert_allclose(self.slope().heights(xz), [-4, -3, -2, -3.25, -3], atol=1e-12)

    def test_nearest_boundary_clamps_instead_of_extrapolating_slope(self):
        # Nearest XZ points: (4,0), (2,2), (0,2), (0,0).
        xz = [[6, 0], [4, 4], [-1, 2], [-2, -3]]
        np.testing.assert_allclose(self.slope().heights(xz), [-3, -2.5, -3, -4], atol=1e-12)

    def test_reversed_winding_and_query_order_preserve_heights(self):
        xz = [[1, 1], [0, 4], [6, 0], [1, 1], [0, 0]]
        expected = [-3.25, -2, -3, -3.25, -4]
        np.testing.assert_allclose(self.slope(reverse=True).heights(xz), expected, atol=1e-12)

    def test_disjoint_ground_levels_select_nearest_local_surface(self):
        triangles = np.array([[[0., -4., 0.], [2., -4., 0.], [0., -4., 2.]],
                              [[10., -2., 0.], [12., -2., 0.], [10., -2., 2.]]])
        surface = exporter.GroundSurface(triangles, box(-1, -1, 13, 3))
        np.testing.assert_allclose(surface.heights([[.5, .5], [10.5, .5], [7, 0], [3, 0]]),
                                   [-4, -2, -2, -4], atol=1e-12)

    def test_triangles_outside_drawing_region_are_not_sampled(self):
        triangles = np.array([[[0., -4., 0.], [2., -4., 0.], [0., -4., 2.]],
                              [[10., 20., 0.], [12., 20., 0.], [10., 20., 2.]]])
        surface = exporter.GroundSurface(triangles, box(-1, -1, 3, 3))
        np.testing.assert_allclose(surface.heights([[10.5, .5]]), [-4], atol=1e-12)

    def test_empty_degenerate_and_nonintersecting_ground_are_rejected(self):
        cases = [np.empty((0, 3, 3)), np.zeros((1, 3, 3)),
                 np.array([[[0., -4., 0.], [0., -3., 0.], [0., -2., 2.]]]),
                 np.array([[[50., -4., 50.], [52., -4., 50.], [50., -4., 52.]]])]
        for triangles in cases:
            with self.subTest(triangles=triangles.tolist()), self.assertRaisesRegex(ValueError, "No native ground"):
                exporter.GroundSurface(triangles, box(-1, -1, 5, 5))

    def test_empty_query_has_empty_result(self):
        self.assertEqual(self.slope().heights(np.empty((0, 2))).shape, (0,))


@unittest.skipIf(exporter is None, "Bay5 exporter tests require optional numpy and shapely")
class Bay5TriangulationTests(unittest.TestCase):
    def test_height_range_selects_negative_ground_and_excludes_upper_level(self):
        vertices = [(0, -4, 0), (4, -3, 0), (0, -3, 4),
                    (20, .1, 20), (24, .1, 20), (20, .1, 24)]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "native.json"
            write_native(path, vertices, [0, 1, 2, 3, 4, 5])
            actual = exporter.read_triangulation(path, height_range=(-4.25, -2.5))
            expected = Polygon([(0, 0), (4, 0), (0, 4)])
            self.assertAlmostEqual(actual.symmetric_difference(expected).area, 0)
            # The Equator default still selects its own height band.
            self.assertEqual(exporter.read_triangulation(path).bounds, (20, 20, 24, 24))

    def test_range_with_no_ground_fails_explicitly(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "native.json"
            write_native(path, [(0, 0, 0), (4, 0, 0), (0, 0, 4)], [0, 1, 2])
            with self.assertRaisesRegex(ValueError, "no ground area"):
                exporter.read_triangulation(path, height_range=(-4.25, -2.5))


@unittest.skipIf(exporter is None or importlib.util.find_spec("PIL") is None,
                 "Bay5 package tests require optional numpy, shapely, and Pillow")
class Bay5PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.package = self.root / "source"
        (self.package / "scenes").mkdir(parents=True)
        (self.package / "meshes").mkdir()
        # A 20 x 15 m slab with y = -4 + .03*(x-84) + .01*(z-24).
        # It spans Bay5's reviewed anchor (90,30), without copied map geometry.
        vertices = [(84, -4, 24), (104, -3.4, 24), (104, -3.25, 39), (84, -3.85, 39)]
        indices = [0, 1, 2, 0, 2, 3]
        mesh_id = "a" * 32
        write_mesh(self.package / "meshes" / (mesh_id + ".mesh.bin"), vertices, indices)
        floor = owner("SyntheticSlope", [{"type": "MeshCollider", "mesh_id": mesh_id}])
        (self.package / "scenes/SyntheticGameplay.json").write_text(
            json.dumps({"objects": [floor]}), encoding="utf-8")
        # A listed background scene references an absent mesh deliberately. A
        # background regression must fail rather than silently alter the drawing.
        background = owner("SyntheticHarbor", [{"type": "MeshCollider", "mesh_id": "b" * 32}])
        self.background = self.package / "scenes/SyntheticBackground.json"
        self.background.write_text(json.dumps({"objects": [background]}), encoding="utf-8")
        self.descriptor = self.root / "bay5.json"
        self.descriptor.write_text(json.dumps({"key": "arena_bay5",
            "all_scene_names": ["SyntheticGameplay", "SyntheticBackground"],
            "background_scene_names": ["SyntheticBackground"], "opened_barriers": []}), encoding="utf-8")
        self.exits = [{"name": "Synthetic North Loading Bay", "position": [88, -3.85, 27]},
                      {"name": "Synthetic South Container Lane", "position": [99, -3.45, 34]}]
        self.descriptor.with_suffix(".exits.json").write_text(json.dumps({"exits": self.exits}), encoding="utf-8")
        self.native = self.root / "native.json"
        write_native(self.native, vertices, indices)

    def generate(self, name="output"):
        output = self.root / name
        with redirect_stdout(io.StringIO()):
            exporter.generate(self.package, self.descriptor, output, navmesh=self.native)
        return output

    def test_bay5_requires_native_height_calibration_before_writing(self):
        output = self.root / "invalid"
        with self.assertRaisesRegex(ValueError, "Bay5 requires native ground triangulation"):
            exporter.generate(self.package, self.descriptor, output)
        self.assertFalse(output.exists())

    def test_empty_remaining_ground_layer_has_no_invalid_ring_and_renders(self):
        # A single native component leaves no secondary ground polygon. Its
        # empty polygon must neither emit an invalid M/Z ring nor crash Pillow.
        empty = box(0, 0, 4, 4).difference(box(0, 0, 4, 4))
        self.assertEqual(empty.geom_type, "Polygon")
        self.assertTrue(empty.is_empty)
        self.assertEqual(exporter.svg_path(empty, [0, 0, 4, 4]), ("", 0))
        preview = self.root / "empty-preview.png"
        exporter.render_preview(preview, [(empty, "#ffffff")], [0, 0, 4, 4])
        from PIL import Image
        with Image.open(preview) as image:
            self.assertEqual(image.size, (40, 40))
            self.assertEqual(len(image.getcolors()), 1)

    def test_exact_bay5_identity_bounds_axes_and_static_exits(self):
        output = self.generate()
        pack = output / "pack"
        self.assertEqual({p.relative_to(pack).as_posix() for p in pack.rglob("*") if p.is_file()},
                         {"ArenaMapPort_Bay5.jsonc", "Layers/Ground.svg"})
        definition = json.loads((pack / "ArenaMapPort_Bay5.jsonc").read_text(encoding="utf-8"))
        expected_bounds = {"Min": {"x": 81, "y": 21}, "Max": {"x": 107, "y": 42}}
        self.assertEqual(definition["MapInternalNames"], ["arena_bay5"])
        self.assertEqual(definition["DisplayName"], "Arena: Bay 5 — ground plan")
        self.assertEqual(definition["CoordinateRotation"], 0)
        self.assertEqual(definition["DefaultLevel"], 0)
        self.assertEqual(definition["Bounds"], expected_bounds)
        self.assertEqual(set(definition["Layers"]), {"Ground Floor"})
        layer = definition["Layers"]["Ground Floor"]
        self.assertEqual(layer["Level"], 0)
        self.assertEqual(layer["ImagePath"], "Maps/ArenaMapPort_Bay5/Layers/Ground.svg")
        self.assertEqual(layer["TesselationIndex"], 1)
        self.assertEqual(layer["ImageBounds"], expected_bounds)
        self.assertEqual(layer["GameBounds"], [{"Min": {"x": 81, "y": 21, "z": -8},
                                                "Max": {"x": 107, "y": 42, "z": 12}}])
        self.assertEqual(len(definition["StaticMarkers"]), 2)
        for marker, source_exit in zip(definition["StaticMarkers"], self.exits):
            x, y, z = source_exit["position"]
            self.assertEqual(marker["Text"], source_exit["name"])
            self.assertEqual(marker["Position"], {"x": x, "y": z, "z": y})
            self.assertEqual(marker["ImagePath"], "Markers/exit.png")
            self.assertEqual(marker["Category"], "Extract")
            self.assertIs(marker["ShowInRaid"], False)
        svg = ET.fromstring((pack / "Layers/Ground.svg").read_bytes())
        self.assertEqual(svg.attrib["viewBox"], "0 0 26 21")
        compass = svg.find("{http://www.w3.org/2000/svg}g[@id='arena-north-marker']")
        self.assertIsNotNone(compass, "Bay5 must retain the current labeled north compass")
        self.assertEqual(len(compass.findall("{http://www.w3.org/2000/svg}path")), 4)
        self.assertEqual(exporter.svg_point(81, 42, [81, 21, 107, 42]), (0, 0))
        self.assertEqual(exporter.svg_point(107, 21, [81, 21, 107, 42]), (26, 21))

    def test_sloped_floor_does_not_become_a_cover_or_wall_overlay(self):
        output = self.generate()
        receipt = json.loads((output / "receipt.json").read_text(encoding="utf-8"))
        self.assertAlmostEqual(receipt["areas_m2"]["floor"], 300)
        self.assertEqual(receipt["areas_m2"]["low"], 0)
        self.assertEqual(receipt["areas_m2"]["high"], 0)
        self.assertAlmostEqual(receipt["native_walkable_area_m2"], 300)

    def test_flat_floor_with_uneven_native_corners_keeps_walkable_clear_and_hole_obstacle(self):
        # Only the native south-west corner is lower. Subtracting native heights
        # at the four corners of a large, flat source floor creates a false
        # diagonal obstacle across its two triangles unless final layers exclude
        # native walkable ground. The center cell is a real NavMesh obstacle hole.
        xs, zs = [84, 94, 98, 104], [24, 30, 34, 39]
        vertices = [(x, -4.2 if (x, z) == (84, 24) else -3.4, z) for z in zs for x in xs]
        indices = []
        for row in range(3):
            for column in range(3):
                if (row, column) == (1, 1):
                    continue
                a = row * 4 + column
                indices.extend([a, a + 1, a + 5, a, a + 5, a + 4])
        write_native(self.native, vertices, indices)
        mesh_path = self.package / "meshes" / ("a" * 32 + ".mesh.bin")
        write_mesh(mesh_path, [(84, -3.4, 24), (104, -3.4, 24),
                               (104, -3.4, 39), (84, -3.4, 39)], [0, 1, 2, 0, 2, 3])
        scene_path = self.package / "scenes/SyntheticGameplay.json"
        scene = json.loads(scene_path.read_text(encoding="utf-8"))
        scene["objects"].extend([
            # A separate ground patch supplies unambiguous near-ground faces.
            owner("GroundPatch", [{"type": "BoxCollider", "center": [102, -3.45, 37],
                                   "size": [4, .1, 4]}]),
            owner("CoverInsideNativeHole", [{"type": "BoxCollider", "center": [96, -3, 32],
                                             "size": [2, .8, 2]}]),
        ])
        scene_path.write_text(json.dumps(scene), encoding="utf-8")
        output = self.generate()
        definition = json.loads((output / "pack/ArenaMapPort_Bay5.jsonc").read_text(encoding="utf-8"))
        bounds = definition["Bounds"]
        svg = ET.fromstring((output / "pack/Layers/Ground.svg").read_bytes())

        def final_layer(color):
            layer = next(child for child in svg if child.attrib.get("fill") == color)
            self.assertEqual(layer.attrib["fill-rule"], "evenodd")
            result = GeometryCollection()
            for subpath in layer.attrib["d"].split(" Z"):
                if not subpath.strip():
                    continue
                coordinates = re.findall(r"(-?\d+\.\d+),(-?\d+\.\d+)", subpath)
                ring = Polygon([(bounds["Min"]["x"] + float(u), bounds["Max"]["y"] - float(v))
                                for u, v in coordinates])
                self.assertTrue(ring.is_valid)
                # The serialized SVG uses even-odd fill, including interior rings.
                result = result.symmetric_difference(ring)
            return result

        low, high = final_layer("#8d8167"), final_layer("#293641")
        walkable = box(84, 24, 104, 39).difference(box(94, 30, 98, 34))
        self.assertLess(low.union(high).intersection(walkable).area, .02,
                        "Final obstacle drawing must not paint a diagonal over native ground")
        real_cover = box(95, 31, 97, 33)
        self.assertGreater(low.intersection(real_cover).area, 3.8,
                           "At least 95% of the real low obstacle must remain visible inside the native hole")

    def test_background_is_excluded_even_when_descriptor_lists_it(self):
        output = self.generate()
        receipt = json.loads((output / "receipt.json").read_text(encoding="utf-8"))
        self.assertEqual(receipt["colliders"], {"MeshCollider": 1})
        self.assertNotIn(str(self.background.resolve()), receipt["inputs_sha256"])

    def test_two_exports_are_identical_and_inputs_remain_unchanged(self):
        before = {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        first, second = self.generate("first"), self.generate("second")
        for relative in ("pack/ArenaMapPort_Bay5.jsonc", "pack/Layers/Ground.svg", "receipt.json", "preview.png"):
            self.assertEqual((first / relative).read_bytes(), (second / relative).read_bytes(), relative)
        for path, content in before.items():
            self.assertEqual(path.read_bytes(), content, str(path))


if __name__ == "__main__":
    unittest.main()
