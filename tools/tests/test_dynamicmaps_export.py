"""Synthetic correctness checks for the optional DynamicMaps geometry exporter.

The fixtures contain no game data. The existing pipeline test environment may
omit the exporter dependencies; its discovery run should then skip this module.
"""
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
    from shapely import Polygon, box
except ImportError:
    np = None

EXPORTER = Path(__file__).resolve().parents[1] / "dynamicmaps" / "export_equator.py"
exporter = None
if np is not None:
    spec = importlib.util.spec_from_file_location("dynamicmaps_export_under_test", EXPORTER)
    exporter = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(exporter)


def mesh_bytes(vertices, indices=(0, 1, 2), *, mask=1, index_format=0, submeshes=None):
    """Independent fixture writer for the documented AMPM channel layout."""
    if submeshes is None:
        submeshes = [(0, len(indices), 0)]
    result = bytearray(struct.pack("<4sIIHBB", b"AMPM", 1, len(vertices), mask,
                                   index_format, len(submeshes)))
    for position in vertices:
        result.extend(struct.pack("<3f", *position))
    # Channel-major blocks, not an interleaved vertex stream.
    for bit, dimensions in [(1, 3), (2, 4), (3, 4), *[(n, 2) for n in range(4, 12)]]:
        if mask & (1 << bit):
            result.extend(struct.pack("<" + "f" * (len(vertices) * dimensions),
                                      *([bit + .25] * (len(vertices) * dimensions))))
    for submesh in submeshes:
        result.extend(struct.pack("<IIB", *submesh))
    if indices:
        result.extend(struct.pack("<" + ("H" if index_format == 0 else "I") * len(indices),
                                  *indices))
    return result


def owner(key, name="object", parent=None, **overrides):
    value = {"id": key, "parent_id": parent, "name": name, "active": True, "layer": 11,
             "local_position": [0, 0, 0], "local_rotation": [0, 0, 0, 1],
             "local_scale": [1, 1, 1], "components": []}
    value.update(overrides)
    return value


@unittest.skipIf(exporter is None, "DynamicMaps exporter requires optional numpy and shapely")
class MeshReaderTests(unittest.TestCase):
    vertices = [(1.25, -2.5, 3), (0, 4, 5), (-1, 0, 2), (6, 7, 8)]

    def read(self, raw):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "fixture.mesh.bin"
            path.write_bytes(raw)
            return exporter.read_mesh(path)

    def test_channel_major_offsets_and_both_index_widths(self):
        for mask in (1, 0xFFF, 1 | (1 << 11), 1 | 2 | 8 | 16):
            for width in (0, 1):
                with self.subTest(mask=mask, index_format=width):
                    vertices, triangles = self.read(mesh_bytes(
                        self.vertices, (3, 2, 1, 0, 2, 3), mask=mask, index_format=width,
                        submeshes=[(3, 3, 0), (0, 3, 0)]))
                    np.testing.assert_array_equal(vertices, self.vertices)
                    np.testing.assert_array_equal(triangles, [[0, 2, 3], [3, 2, 1]])

    def test_empty_mesh_has_well_shaped_outputs(self):
        vertices, triangles = self.read(mesh_bytes([], (), submeshes=[]))
        self.assertEqual(vertices.shape, (0, 3))
        self.assertEqual(triangles.shape, (0, 3))

    def test_rejects_unsupported_headers(self):
        cases = []
        raw = mesh_bytes(self.vertices)
        for offset, value in [(0, b"FAIL"), (4, struct.pack("<I", 2)),
                              (12, struct.pack("<H", 0)), (12, struct.pack("<H", 0x1001)),
                              (14, b"\x02")]:
            changed = bytearray(raw)
            changed[offset:offset + len(value)] = value
            cases.append(changed)
        for index, changed in enumerate(cases):
            with self.subTest(case=index), self.assertRaises(ValueError):
                self.read(changed)

    def test_rejects_truncated_header_channels_submesh_and_indices(self):
        raw = mesh_bytes(self.vertices, mask=0xFFF)
        channel_end = 16 + len(self.vertices) * (3 + 3 + 4 + 4 + 8 * 2) * 4
        for length in (0, 15, channel_end - 1, channel_end + 8, len(raw) - 1, len(raw) - 2):
            with self.subTest(length=length), self.assertRaises(ValueError):
                self.read(raw[:length])

    def test_rejects_invalid_triangle_indices_and_topology(self):
        cases = [mesh_bytes(self.vertices, (0, 1, 4)),
                 mesh_bytes(self.vertices, (0, 1)),
                 mesh_bytes(self.vertices, submeshes=[(1, 3, 0)]),
                 mesh_bytes(self.vertices, submeshes=[(0, 3, 2)])]
        for index, raw in enumerate(cases):
            with self.subTest(case=index), self.assertRaises(ValueError):
                self.read(raw)

    def test_rejects_nonfinite_positions(self):
        for invalid in (float("nan"), float("inf"), -float("inf")):
            with self.subTest(value=invalid), self.assertRaises(ValueError):
                self.read(mesh_bytes([(invalid, 0, 0), (1, 0, 0), (0, 0, 1)]))


@unittest.skipIf(exporter is None, "DynamicMaps exporter requires optional numpy and shapely")
class SceneTransformTests(unittest.TestCase):
    def tearDown(self):
        exporter.Scene.world.cache_clear()
        exporter.Scene.lineage.cache_clear()

    def test_parent_rotation_scale_translation_compose_in_unity_order(self):
        half = 2 ** -.5
        scene = exporter.Scene({"objects": [
            owner("parent", local_position=[10, 20, 30], local_scale=[2, 3, 4],
                  local_rotation=[0, half, 0, half]),
            owner("child", parent="parent", local_position=[1, 2, 3], local_scale=[1, 2, 1])]})
        np.testing.assert_allclose(scene.world("child") @ [1, 1, 1, 1], [26, 32, 26, 1], atol=1e-12)

    def test_inactive_ancestor_excludes_active_descendant(self):
        scene = exporter.Scene({"objects": [owner("root", active=False), owner("child", parent="root")]})
        self.assertFalse(scene.included("child", []))

    def test_nonphysical_layer_is_checked_on_owner_not_parent(self):
        for excluded_layer in (4, 13, 29):
            scene = exporter.Scene({"objects": [owner("root", layer=excluded_layer),
                                                owner("child", parent="root")]})
            with self.subTest(layer=excluded_layer):
                self.assertFalse(scene.included("root", []))
                self.assertTrue(scene.included("child", []))

    def test_round_open_names_exclude_descendants(self):
        for name in ("SPAWN_BOUND (2)", "LevelBorderWall", "Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R"):
            scene = exporter.Scene({"objects": [owner("root", name=name), owner("child", parent="root")]})
            with self.subTest(name=name):
                self.assertFalse(scene.included("child", []))

    def test_opened_barrier_preserves_root_frames_and_unrelated_prefixes(self):
        scene = exporter.Scene({"objects": [owner("root", "Arena"),
            owner("barrier", "Shutter", "root"), owner("leaf", "Moving", "barrier"),
            owner("frame", "Fixed_FRAME", "barrier"), owner("bolt", "Bolt", "frame"),
            owner("other", "ShutterOther", "root"), owner("other_leaf", "Moving", "other")]})
        opened = ["Arena/Shutter"]
        for key, included in [("barrier", True), ("leaf", False), ("frame", True),
                              ("bolt", True), ("other_leaf", True)]:
            with self.subTest(key=key):
                self.assertEqual(scene.included(key, opened), included)

    def test_invalid_hierarchies_and_nonfinite_transforms_fail(self):
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            exporter.Scene({"objects": [owner("a"), owner("a")]})
        cyclic = exporter.Scene({"objects": [owner("a", parent="b"), owner("b", parent="a")]})
        with self.assertRaisesRegex(ValueError, "Cyclic"):
            cyclic.world("a")
        with self.assertRaisesRegex(ValueError, "Non-finite"):
            exporter.local_matrix(owner("a", local_position=[float("inf"), 0, 0]))


@unittest.skipIf(exporter is None, "DynamicMaps exporter requires optional numpy and shapely")
class ProjectionTests(unittest.TestCase):
    def test_oblique_triangle_clips_to_both_slab_planes(self):
        triangle = np.array([[[0., 0., 0.], [4., 0., 0.], [0., 4., 4.]]])
        actual = exporter.project_slab(triangle, 1, 2)
        expected = Polygon([(0, 1), (3, 1), (2, 2), (0, 2)])
        self.assertAlmostEqual(actual.symmetric_difference(expected).area, 0, places=12)
        self.assertAlmostEqual(actual.area, 2.5)

    def test_horizontal_faces_inside_slab_remain_filled(self):
        triangle = np.array([[[0., .5, 0.], [4., .5, 0.], [0., .5, 3.]]])
        self.assertAlmostEqual(exporter.project_slab(triangle, .4, 1.05).area, 6)

    def test_roof_above_and_geometry_below_do_not_become_obstacles(self):
        for height in (-1, 5):
            triangle = np.array([[[0., height, 0.], [4., height, 0.], [0., height, 3.]]])
            with self.subTest(height=height):
                self.assertTrue(exporter.project_slab(triangle, .4, 1.8).is_empty)

    def test_vertical_wall_remains_visible_with_narrow_stroke(self):
        triangle = np.array([[[0., 0., 0.], [0., 3., 0.], [0., 0., 3.]]])
        actual = exporter.project_slab(triangle, 1, 2)
        np.testing.assert_allclose(actual.bounds, [-.055, 0, .055, 2], atol=1e-12)
        self.assertAlmostEqual(actual.area, .22)

    def test_degenerate_and_empty_triangles_add_no_geometry(self):
        for triangles in (np.zeros((1, 3, 3)), np.empty((0, 3, 3))):
            with self.subTest(shape=triangles.shape):
                self.assertTrue(exporter.project_slab(triangles, .4, 1.8).is_empty)

    def test_primitive_boxes_preserve_center_and_capsule_axis(self):
        vertices, triangles = exporter.primitive({"type": "BoxCollider", "size": [2, 4, 6],
                                                "center": [10, 20, 30]})
        np.testing.assert_array_equal(vertices.min(axis=0), [9, 18, 27])
        np.testing.assert_array_equal(vertices.max(axis=0), [11, 22, 33])
        self.assertEqual(triangles.shape, (12, 3))
        for axis in range(3):
            vertices, _ = exporter.primitive({"type": "CapsuleCollider", "radius": .5,
                                              "height": 3, "direction": axis})
            expected = np.ones(3)
            expected[axis] = 3
            np.testing.assert_array_equal(np.ptp(vertices, axis=0), expected)

    def test_world_to_svg_corners_and_coordinate_roundtrip(self):
        bounds = [-10, -20, 30, 40]
        self.assertEqual(exporter.svg_point(-10, 40, bounds), (0, 0))
        self.assertEqual(exporter.svg_point(30, -20, bounds), (40, 60))
        for x, z in [(-10, -20), (30, 40), (1.25, -3.5), (-7, 32)]:
            u, v = exporter.svg_point(x, z, bounds)
            # SVG image pixels are mapped into ImageBounds with a vertical flip.
            actual_x = bounds[0] + u
            actual_z = bounds[1] + (bounds[3] - bounds[1] - v)
            self.assertEqual((actual_x, actual_z), (x, z))
        self.assertLess(exporter.svg_point(0, 1, bounds)[1], exporter.svg_point(0, 0, bounds)[1])

    def test_svg_path_keeps_hole_and_deterministic_subpath_order(self):
        outer = [(-4, -3), (4, -3), (4, 3), (-4, 3)]
        hole = [(-1, -1), (1, -1), (1, 1), (-1, 1)]
        geom = Polygon(outer, [hole])
        path, count = exporter.svg_path(geom, [-5, -5, 5, 5])
        self.assertEqual(count, 8)
        rings = []
        for subpath in path.split(" Z"):
            if subpath.strip():
                coordinates = re.findall(r"(-?\d+\.\d+),(-?\d+\.\d+)", subpath)
                rings.append([(float(x) - 5, 5 - float(y)) for x, y in coordinates])
        self.assertEqual(len(rings), 2)
        self.assertAlmostEqual(Polygon(rings[0], rings[1:]).symmetric_difference(geom).area, 0)
        second = box(8, 8, 9, 9)
        self.assertEqual(exporter.svg_path(geom.union(second), [-5, -5, 10, 10]),
                         exporter.svg_path(second.union(geom), [-5, -5, 10, 10]))

    def test_floor_part_threshold_does_not_fill_significant_courtyard_holes(self):
        ground = Polygon([(0, 0), (10, 0), (10, 10), (0, 10)], [
            [(2, 2), (3, 2), (3, 3), (2, 3)],
            [(6, 6), (6.2, 6), (6.2, 6.2), (6, 6.2)]])
        # Discarding small disconnected floor pieces must not fill a useful hole
        # in a large piece. Hole noise has its own smaller threshold.
        actual = exporter.clean(ground, min_area=2)
        self.assertEqual(len(actual.interiors), 1)
        self.assertAlmostEqual(actual.area, 99)

    def test_svg_precision_does_not_turn_a_narrow_notch_into_a_self_intersection(self):
        # The valid 0.8 mm notch collapses into an overlapping spike when each
        # coordinate is independently formatted to three decimal places.
        source = Polygon([(0, 0), (4, 0), (4, 4), (0, 4), (0, 2.0004),
                          (3, 2.0004), (3, 1.9996), (0, 1.9996)])
        self.assertTrue(source.is_valid)
        naive = Polygon([(float(f"{x:.3f}"), float(f"{z:.3f}"))
                         for x, z in source.exterior.coords])
        self.assertFalse(naive.is_valid, "Fixture must reproduce independent-rounding corruption")

        bounds = [-1, -1, 5, 5]
        path, _ = exporter.svg_path(source, bounds)
        emitted = []
        for subpath in path.split(" Z"):
            if subpath.strip():
                coordinates = re.findall(r"(-?\d+\.\d+),(-?\d+\.\d+)", subpath)
                ring = Polygon([(float(u) + bounds[0], bounds[3] - float(v))
                                for u, v in coordinates])
                self.assertTrue(ring.is_valid, f"Invalid serialized ring: {subpath}")
                emitted.append(ring)
        self.assertEqual(len(emitted), 1)
        # Only the sub-millimetre notch may disappear; the room's footprint and
        # world-coordinate placement must survive the topology repair.
        actual = emitted[0]
        self.assertEqual(actual.bounds, source.bounds)
        self.assertLess(actual.symmetric_difference(source).area, .003)
        self.assertAlmostEqual(actual.area, 16)


@unittest.skipIf(exporter is None or importlib.util.find_spec("PIL") is None,
                 "Full exporter fixture requires optional numpy, shapely, and Pillow")
class GenerationTests(unittest.TestCase):
    def test_synthetic_package_filters_colliders_and_is_reproducible(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root / "source"
            (package / "scenes").mkdir(parents=True)
            floor = {"type": "BoxCollider", "center": [0, -.1, 0], "size": [20, .2, 10]}
            ignored = {"type": "BoxCollider", "center": [500, -.1, 0], "size": [10, .2, 10]}
            scene = {"objects": [owner("floor", components=[floor]),
                owner("disabled", components=[{**ignored, "enabled": False}]),
                owner("trigger", components=[{**ignored, "is_trigger": True}]),
                owner("inactive", active=False, components=[ignored]),
                owner("border", name="LevelBorder (2)", components=[ignored]),
                owner("roof", components=[{**floor, "center": [0, 5, 0]}])]}
            (package / "scenes" / "Synthetic.json").write_text(json.dumps(scene), encoding="utf-8")
            # Unlisted scenes must not leak into the exported map.
            (package / "scenes" / "Other.json").write_text(json.dumps({"objects": [
                owner("unrelated", components=[ignored])]}), encoding="utf-8")
            descriptor = root / "equator.json"
            descriptor.write_text(json.dumps({"key": "arena_equator", "all_scene_names": ["Synthetic"],
                                               "opened_barriers": []}), encoding="utf-8")
            descriptor.with_suffix(".exits.json").write_text(json.dumps({"exits": [
                {"name": "Synthetic North", "position": [2, .1, 4]}]}), encoding="utf-8")
            before = {p: p.read_bytes() for p in root.rglob("*") if p.is_file()}
            outputs = [root / "first", root / "second"]
            for output in outputs:
                with redirect_stdout(io.StringIO()):
                    exporter.generate(package, descriptor, output)
            self.assertTrue(all(path.read_bytes() == original for path, original in before.items()))
            for relative in ("pack/ArenaMapPort_Equator.jsonc", "pack/Layers/Ground.svg", "receipt.json", "preview.png"):
                self.assertEqual((outputs[0] / relative).read_bytes(), (outputs[1] / relative).read_bytes())
            receipt = json.loads((outputs[0] / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual(receipt["bounds_xz"], [-13, -8, 13, 8])
            self.assertEqual(receipt["colliders"], {"BoxCollider": 1})
            self.assertAlmostEqual(receipt["areas_m2"]["floor"], 200)
            definition = json.loads((outputs[0] / "pack/ArenaMapPort_Equator.jsonc").read_text(encoding="utf-8"))
            self.assertEqual(definition["MapInternalNames"], ["arena_equator"])
            self.assertEqual(definition["CoordinateRotation"], 0)
            self.assertEqual(definition["StaticMarkers"][0]["Position"], {"x": 2, "y": 4, "z": .1})
            layer = definition["Layers"]["Ground Floor"]
            self.assertEqual(definition["Bounds"], layer["ImageBounds"])
            self.assertEqual(layer["ImagePath"], "Maps/ArenaMapPort_Equator/Layers/Ground.svg")
            svg = ET.fromstring((outputs[0] / "pack/Layers/Ground.svg").read_bytes())
            self.assertEqual(svg.attrib["viewBox"], "0 0 26 16")
            compass = svg.find("{http://www.w3.org/2000/svg}g[@id='arena-north-marker']")
            self.assertIsNotNone(compass, "Equator must retain the current labeled north compass")
            self.assertEqual(len(compass.findall("{http://www.w3.org/2000/svg}path")), 4)
            for path in list(svg)[1:4]:
                self.assertEqual(path.attrib["fill-rule"], "evenodd")

            # The optional native triangulation uses the same untransformed world
            # axes and controls the visible extent independently of the full floor.
            native = root / "triangulation.json"
            native.write_text(json.dumps({"vertices": [
                {"x": -2, "y": .1, "z": -1}, {"x": 4, "y": .1, "z": -1},
                {"x": 4, "y": .1, "z": 2}, {"x": -2, "y": .1, "z": 2}],
                "indices": [0, 1, 2, 0, 2, 3]}), encoding="utf-8")
            native_before = native.read_bytes()
            with redirect_stdout(io.StringIO()):
                exporter.generate(package, descriptor, root / "native", navmesh=native)
            receipt = json.loads((root / "native/receipt.json").read_text(encoding="utf-8"))
            self.assertEqual(receipt["bounds_xz"], [-5, -4, 7, 5])
            self.assertIn(str(native.resolve()), receipt["inputs_sha256"])
            self.assertEqual(native.read_bytes(), native_before)


@unittest.skipIf(exporter is None, "DynamicMaps exporter requires optional numpy and shapely")
class NativeTriangulationTests(unittest.TestCase):
    vertices = [{"x": -4, "y": .1, "z": 10}, {"x": 2, "y": .2, "z": 10},
                {"x": 2, "y": .3, "z": 13}, {"x": -4, "y": .4, "z": 13}]

    def read(self, data):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "native.json"
            path.write_text(json.dumps(data), encoding="utf-8-sig")
            return exporter.read_triangulation(path)

    def test_world_xz_projection_unions_ground_and_excludes_upper_geometry(self):
        vertices = self.vertices + [{"x": 100, "y": 5, "z": 100},
                                    {"x": 110, "y": 5, "z": 100},
                                    {"x": 100, "y": 5, "z": 110}]
        actual = self.read({"vertices": vertices, "indices": [0, 1, 2, 0, 2, 3, 4, 5, 6]})
        self.assertAlmostEqual(actual.symmetric_difference(box(-4, 10, 2, 13)).area, 0)
        self.assertEqual(actual.bounds, (-4, 10, 2, 13))
        self.assertEqual(exporter.svg_point(-4, 13, actual.bounds), (0, 0))

    def test_rejects_negative_out_of_range_noninteger_and_empty_indices(self):
        for indices in ([-1, 1, 2], [0, 1, 4], [0, 1], [], [[0, 1, 2]],
                        [0., 1., 2.], ["0", "1", "2"], [False, True, True]):
            with self.subTest(indices=indices), self.assertRaisesRegex(ValueError, "Invalid native"):
                self.read({"vertices": self.vertices, "indices": indices})

    def test_rejects_empty_and_nonfinite_vertices(self):
        for vertices in ([], [{"x": float("nan"), "y": 0, "z": 0}],
                         [{"x": 0, "y": float("inf"), "z": 0}]):
            with self.subTest(vertices=vertices), self.assertRaisesRegex(ValueError, "Invalid native"):
                self.read({"vertices": vertices, "indices": [0, 0, 0]})

    def test_rejects_triangulation_without_ground_area(self):
        upper = [{**v, "y": 3} for v in self.vertices]
        for vertices, indices in ((upper, [0, 1, 2]), (self.vertices, [0, 0, 0])):
            with self.subTest(indices=indices), self.assertRaisesRegex(ValueError, "no ground area"):
                self.read({"vertices": vertices, "indices": indices})


if __name__ == "__main__":
    unittest.main()
