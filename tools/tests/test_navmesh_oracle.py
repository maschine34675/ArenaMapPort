"""Synthetic-tile tests for the Arena NavMesh oracle reader and the mode-toggler probe decoders.

No game data: every Detour tile here is built byte by byte from the layout documented in
tools/validate/navmesh_oracle.py (72-byte header, float3 vertices, 32-byte polygons). PlacementTests,
PlacedMeshTests and NavMeshDataTreeTests cover the NavMeshData position / rotation (PLAN-BOWL item 6): Bowl's
mesh is yawed 90 degrees about Y, AirPit's placement is the identity; the live Arena numbers are pinned in
test_bowl_navmesh_oracle.py.
"""
import dataclasses
import math
import struct
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
import navmesh_oracle as no
import probe_mode_toggler as probe

MAGIC = no.MAGIC
EXT = no.EXTERNAL_LINK


def tile_blob(x, y, verts, polys, bmin, bmax, version=16, magic=MAGIC, extra=0):
    """polys: list of (vertex indices, neighbour codes, area)."""
    head = struct.pack("<5i", magic, version, x, y, 7)
    head += struct.pack("<6i", len(polys), len(verts), 0, 0, 0, 0)
    head += struct.pack("<3f", *bmin) + struct.pack("<3f", *bmax) + struct.pack("<f", 14.0)
    assert len(head) == no.HEADER_SIZE
    body = b"".join(struct.pack("<3f", *v) for v in verts)
    for idx, neis, area in polys:
        n = len(idx)
        vs = list(idx) + [0] * (6 - n)
        ns = list(neis) + [0] * (6 - n)
        body += struct.pack("<6H6HIBBH", *vs, *ns, 1, n, area, 0)
    return head + body + b"\x00" * extra


def two_quads():
    # A = unit square x 0..1, B = x 1..2, sharing the edge x=1; both z 0..1 at y=0
    verts = [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1), (2, 0, 0), (2, 0, 1)]
    # clockwise from above: (0,0,0)->(0,0,1)->(1,0,1)->(1,0,0); order irrelevant for adjacency tests
    polys = [([0, 3, 2, 1], [0, 0, 2, 0], 1),      # A: edge 2 (verts 2->1) borders B (1-based index 2)
             ([1, 2, 5, 4], [1, 0, 0, 0], 1)]      # B: edge 0 (verts 1->2) borders A
    return tile_blob(0, 0, verts, polys, (0, 0, 0), (2, 1, 1))


class ParseTileTests(unittest.TestCase):
    def test_parses_vertices_polygons_and_neighbours(self):
        tile = no.parse_tile(two_quads())
        self.assertEqual((tile.x, tile.y, tile.agent_type), (0, 0, 7))
        self.assertEqual(tile.verts.shape, (6, 3))
        self.assertEqual(list(tile.poly_nverts), [4, 4])
        self.assertEqual(list(tile.poly_neis[0][:4]), [0, 0, 2, 0])
        self.assertEqual(list(tile.poly_verts[0]), [0, 3, 2, 1, -1, -1])

    def test_header_only_tile_is_empty(self):
        blob = struct.pack("<5i", MAGIC, 16, 3, 4, 0) + struct.pack("<6i", 0, 0, 0, 0, 0, 0) + bytes(28)
        self.assertEqual(len(blob), 72)
        self.assertIsNone(no.parse_tile(blob))

    def test_rejects_wrong_magic_version_and_truncation(self):
        good = two_quads()
        with self.assertRaises(no.NavMeshFormatError):
            no.parse_tile(tile_blob(0, 0, [(0, 0, 0)] * 3, [([0, 1, 2], [0, 0, 0], 1)], (0, 0, 0), (1, 1, 1), magic=1234))
        with self.assertRaises(no.NavMeshFormatError):
            no.parse_tile(tile_blob(0, 0, [(0, 0, 0)] * 3, [([0, 1, 2], [0, 0, 0], 1)], (0, 0, 0), (1, 1, 1), version=15))
        with self.assertRaises(no.NavMeshFormatError):
            no.parse_tile(good[:-10])
        with self.assertRaises(no.NavMeshFormatError):
            no.parse_tile(good[:20])

    def test_rejects_out_of_range_vertex_and_neighbour(self):
        with self.assertRaises(no.NavMeshFormatError):
            no.parse_tile(tile_blob(0, 0, [(0, 0, 0)] * 3, [([0, 1, 9], [0, 0, 0], 1)], (0, 0, 0), (1, 1, 1)))
        with self.assertRaises(no.NavMeshFormatError):
            no.parse_tile(tile_blob(0, 0, [(0, 0, 0)] * 3, [([0, 1, 2], [5, 0, 0], 1)], (0, 0, 0), (1, 1, 1)))

    def test_rejects_degenerate_polygon_size(self):
        with self.assertRaises(no.NavMeshFormatError):
            no.parse_tile(tile_blob(0, 0, [(0, 0, 0)] * 3, [([0, 1], [0, 0], 1)], (0, 0, 0), (1, 1, 1)))


class BuildMeshTests(unittest.TestCase):
    def mesh(self):
        return no.build_navmesh([no.parse_tile(two_quads())])

    def test_internal_adjacency_one_component_and_area(self):
        mesh = self.mesh()
        self.assertEqual(mesh.poly_count, 2)
        self.assertEqual(mesh.edges.tolist(), [[0, 1]])
        self.assertEqual(int(mesh.labels.max()), 0)
        axz, a3 = mesh.poly_areas()
        self.assertAlmostEqual(axz.sum(), 2.0, places=6)
        self.assertEqual(no.check_symmetry(mesh), 0)

    def test_non_reciprocal_link_is_detected(self):
        verts = [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1), (2, 0, 0), (2, 0, 1)]
        polys = [([0, 3, 2, 1], [0, 0, 2, 0], 1), ([1, 2, 5, 4], [0, 0, 0, 0], 1)]
        mesh = no.build_navmesh([no.parse_tile(tile_blob(0, 0, verts, polys, (0, 0, 0), (2, 1, 1)))])
        self.assertEqual(no.check_symmetry(mesh), 1)

    def test_locate_and_clearance(self):
        mesh = self.mesh()
        poly, surf = mesh.locate([(0.5, 0.2, 0.5), (1.5, 0.0, 0.5), (3.0, 0.0, 0.5), (0.5, 9.0, 0.5)])
        self.assertEqual(poly[0], 0)
        self.assertEqual(poly[1], 1)
        self.assertEqual(poly[2], -1)
        self.assertEqual(poly[3], -1)                      # too far above the surface
        clear = mesh.clearance([(1.0, 0.0, 0.5), (0.2, 0.0, 0.5)])
        self.assertAlmostEqual(clear[0], 0.5, places=6)    # nearest border: z=0 or z=1 edge, 0.5 away
        self.assertAlmostEqual(clear[1], 0.2, places=6)    # the x=0 border

    def test_sample_polygons_covers_each_lattice_point_once(self):
        mesh = self.mesh()
        pts, polys = no.sample_polygons(mesh, 0.5, origin=(0.25, 0.25))
        # lattice x in {0.25,0.75,1.25,1.75}, z in {0.25,0.75}
        self.assertEqual(len(pts), 8)
        self.assertEqual(sorted(set(polys.tolist())), [0, 1])
        self.assertEqual(len({(round(float(x), 3), round(float(z), 3)) for x, _y, z in pts}), 8)

    def test_component_summary_orders_by_area(self):
        v = [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1), (10, 0, 0), (13, 0, 0), (13, 0, 3), (10, 0, 3)]
        polys = [([0, 3, 2, 1], [0, 0, 0, 0], 1), ([4, 7, 6, 5], [0, 0, 0, 0], 1)]
        mesh = no.build_navmesh([no.parse_tile(tile_blob(0, 0, v, polys, (0, 0, 0), (13, 1, 3)))])
        summary = no.component_summary(mesh)
        self.assertEqual([round(c["area_xz"]) for c in summary], [9, 1])
        self.assertEqual(mesh.labels.tolist(), [1, 0])


class PortalTests(unittest.TestCase):
    def tiles(self, second_z=(0.0, 1.0), second_y=0.0, far=False):
        a_verts = [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1)]
        a = tile_blob(0, 0, a_verts, [([0, 3, 2, 1], [0, 0, EXT | 0, 0], 1)], (0, 0, 0), (1, 1, 1))
        x0 = 1.0 if not far else 5.0
        z0, z1 = second_z
        b_verts = [(x0, second_y, z0), (x0 + 1, second_y, z0), (x0 + 1, second_y, z1), (x0, second_y, z1)]
        b = tile_blob(1, 0, b_verts, [([0, 3, 2, 1], [EXT | 4, 0, 0, 0], 1)] if not far else
                      [([0, 3, 2, 1], [0, 0, 0, 0], 1)], (x0, 0, 0), (x0 + 1, 1, 1))
        return [no.parse_tile(a), no.parse_tile(b)]

    def test_overlapping_portal_edges_connect_the_tiles(self):
        mesh = no.build_navmesh(self.tiles())
        self.assertEqual(mesh.portal_pairs, 1)
        self.assertEqual(int(mesh.labels.max()), 0)
        self.assertEqual(mesh.unmatched_portal_edges, 0)

    def test_partial_overlap_still_connects(self):
        mesh = no.build_navmesh(self.tiles(second_z=(0.5, 1.5)))
        self.assertEqual(int(mesh.labels.max()), 0)

    def test_non_overlapping_edges_do_not_connect(self):
        mesh = no.build_navmesh(self.tiles(second_z=(1.0, 2.0)))
        self.assertEqual(int(mesh.labels.max()), 1)
        self.assertGreater(mesh.unmatched_portal_edges, 0)

    def test_height_mismatch_does_not_connect(self):
        mesh = no.build_navmesh(self.tiles(second_y=3.0))
        self.assertEqual(int(mesh.labels.max()), 1)

    def test_distant_tile_does_not_connect(self):
        mesh = no.build_navmesh(self.tiles(far=True))
        self.assertEqual(int(mesh.labels.max()), 1)


class TriangulationTests(unittest.TestCase):
    def test_shared_edges_are_welded_and_components_counted(self):
        verts = [(0, 0, 0), (1, 0, 0), (0, 0, 1), (1, 0, 1),            # square as two triangles
                 (10, 0, 0), (11, 0, 0), (10, 0, 1)]                    # a separate triangle
        idx = [0, 2, 1, 1, 2, 3, 4, 6, 5]
        mesh = no.mesh_from_triangulation(verts, idx)
        self.assertEqual(mesh.poly_count, 3)
        self.assertEqual(int(mesh.labels.max()) + 1, 2)
        self.assertEqual(sorted(np.bincount(mesh.labels).tolist()), [1, 2])

    def test_duplicate_vertices_are_merged(self):
        verts = [(0, 0, 0), (1, 0, 0), (0, 0, 1), (1, 0, 0), (1, 0, 1), (0, 0, 1)]     # vertex 1 == 3, 2 == 5
        mesh = no.mesh_from_triangulation(verts, [0, 2, 1, 3, 5, 4])
        self.assertEqual(int(mesh.labels.max()) + 1, 1)


S = math.sqrt(0.5)
YAW90 = (0.0, S, 0.0, S)                                               # Quaternion.Euler(0, 90, 0)
BOWL_ROTATION = (-0.0, 0.7071057558059692, -0.0, 0.7071078419685364)   # Bowl's NavMeshData.m_Rotation (level128)
AIRPIT_ROTATION = (-0.0, -0.0, -0.0, 1.0)                              # AirPit's (level33): the identity


def yaw(degrees):
    half = math.radians(degrees) / 2.0
    return (0.0, math.sin(half), 0.0, math.cos(half))


def stamped(tiles, placement):
    return [dataclasses.replace(t, placement=placement) for t in tiles]


def navmesh_tree(blobs, position=(0.0, 0.0, 0.0), rotation=(0.0, 0.0, 0.0, 1.0)):
    """The slice of a read_typetree() dict of a NavMeshData that the oracle reads."""
    return {"m_NavMeshTiles": [{"m_MeshData": blob, "m_Hash": b""} for blob in blobs],
            "m_Position": dict(zip("xyz", position)), "m_Rotation": dict(zip("xyzw", rotation))}


class PlacementTests(unittest.TestCase):
    """NavMeshData.m_Position / m_Rotation (PLAN-BOWL item 6): Bowl's mesh is yawed 90 degrees, AirPit's is not."""

    def test_a_yaw_of_90_degrees_sends_local_x_to_minus_z_and_local_z_to_x(self):
        placed = no.Placement(rotation=YAW90).apply([[1, 0, 0], [0, 0, 1], [0, 5, 0]])
        np.testing.assert_allclose(placed, [[0, 0, -1], [1, 0, 0], [0, 5, 0]], atol=1e-12)
        self.assertAlmostEqual(no.Placement(rotation=YAW90).yaw_degrees(), 90.0, places=9)

    def test_the_translation_is_added_after_the_rotation(self):
        placed = no.Placement(position=(10.0, 1.0, -2.0), rotation=YAW90).apply([[1, 0, 0]])
        np.testing.assert_allclose(placed, [[10.0, 1.0, -3.0]], atol=1e-12)

    def test_the_identity_is_recognised_and_applies_bit_exactly(self):
        for rotation in ((0.0, 0.0, 0.0, 1.0), AIRPIT_ROTATION):        # AirPit stores negative zeros
            placement = no.Placement(rotation=rotation)
            self.assertTrue(placement.is_identity)
            points = np.array([[0.1, 0.2, 0.3], [-56.14, 0.0235, 84.77]])
            self.assertTrue(np.array_equal(placement.apply(points), points))
        self.assertFalse(no.Placement(rotation=YAW90).is_identity)
        self.assertFalse(no.Placement(position=(0.0, 0.1, 0.0)).is_identity)

    def test_bowls_recorded_quaternion_is_a_90_degree_yaw_and_maps_the_raw_corner_to_the_world_corner(self):
        placement = no.Placement(rotation=BOWL_ROTATION)
        self.assertAlmostEqual(placement.yaw_degrees(), 90.0, places=3)
        # raw tile corner (x 79.45, z -63.84) -> world (x -63.84, z -79.45): within 0.3 mm of the exact quarter turn
        np.testing.assert_allclose(placement.apply([[79.45, 0.04, -63.84]]), [[-63.84, 0.04, -79.45]], atol=3e-4)

    def test_malformed_and_non_finite_placements_are_refused_at_construction(self):
        for kwargs in ({"position": (float("nan"), 0.0, 0.0)}, {"position": (0.0, float("inf"), 0.0)}, {"position": (0.0, 0.0)},
                       {"rotation": (0.0, float("nan"), 0.0, 1.0)}, {"rotation": (0.0, 0.0, 1.0)}, {"rotation": ("a", 0, 0, 1)}):
            with self.assertRaises(no.NavMeshFormatError, msg=kwargs):
                no.Placement(**kwargs)
        # lists are accepted and normalised to float tuples, so a Placement stays hashable and comparable
        listed = no.Placement(position=[1, 2, 3], rotation=[0, 0, 0, 1])
        self.assertEqual(listed, no.Placement(position=(1.0, 2.0, 3.0)))
        self.assertEqual(hash(listed), hash(no.Placement(position=(1.0, 2.0, 3.0))))
        with self.assertRaises(no.NavMeshFormatError):
            no.placement_from_navmesh_data(navmesh_tree([], (float("nan"), 0.0, 0.0)))

    def test_tilted_and_non_unit_rotations_are_refused(self):
        with self.assertRaises(no.NavMeshFormatError):
            no.Placement(rotation=(S, 0.0, 0.0, S)).require_upright()         # 90 degrees about X: up is not up
        with self.assertRaises(no.NavMeshFormatError):
            no.Placement(rotation=(0.0, 0.0, 0.0, 2.0)).matrix()
        no.Placement(rotation=yaw(37.0)).require_upright()                    # any pure yaw is fine


class PlacedMeshTests(unittest.TestCase):
    def raw(self):
        return [no.parse_tile(two_quads())]

    def test_vertices_are_world_coordinates_and_the_topology_is_unchanged(self):
        tiles = self.raw()
        raw = no.build_navmesh(tiles)
        placed = no.build_navmesh(stamped(tiles, no.Placement(rotation=YAW90)))
        np.testing.assert_allclose(placed.verts, np.stack([raw.verts[:, 2], raw.verts[:, 1], -raw.verts[:, 0]], axis=1), atol=1e-12)
        for name in ("poly_verts", "poly_nverts", "poly_area", "poly_tile", "poly_nei", "edges", "labels"):
            self.assertTrue(np.array_equal(getattr(placed, name), getattr(raw, name)), name)
        np.testing.assert_allclose(placed.poly_areas()[0], raw.poly_areas()[0], atol=1e-12)
        self.assertEqual(no.check_symmetry(placed), 0)
        self.assertAlmostEqual(placed.placement.yaw_degrees(), 90.0, places=9)

    def test_queries_answer_in_world_coordinates(self):
        # quad A spans world x 0..1, z -1..0 and B z -2..-1 once the yaw is applied (raw: A x 0..1, B x 1..2 at z 0..1)
        placed = no.build_navmesh(stamped(self.raw(), no.Placement(rotation=YAW90)))
        poly, _surf = placed.locate([(0.5, 0.0, -0.5), (0.5, 0.0, -1.5), (0.5, 0.0, 0.5), (1.5, 0.0, -0.5)])
        self.assertEqual(poly.tolist(), [0, 1, -1, -1])          # the raw-frame spot (0.5, 0.5) is off the mesh now
        self.assertAlmostEqual(placed.clearance([(0.2, 0.0, -1.0)])[0], 0.2, places=9)      # the world x = 0 border
        pts, _polys = no.sample_polygons(placed, 0.5, origin=(0.25, 0.25))
        self.assertEqual(len(pts), 8)
        self.assertTrue((pts[:, 0] > 0).all() and (pts[:, 0] < 1).all() and (pts[:, 2] < 0).all() and (pts[:, 2] > -2).all())
        # the fan triangles cached while the areas were computed in the tile frame must not survive the move
        tri_x = placed.fan_triangles()[0][:, :, 0]
        self.assertTrue(tri_x.min() >= -1e-9 and tri_x.max() <= 1 + 1e-9)

    def test_the_translation_moves_the_whole_mesh(self):
        tiles = self.raw()
        placed = no.build_navmesh(stamped(tiles, no.Placement(position=(100.0, 5.0, -7.0), rotation=YAW90)))
        self.assertEqual(placed.locate([(100.5, 5.0, -7.5)])[0].tolist(), [0])
        np.testing.assert_allclose(placed.verts.min(axis=0), [100.0, 5.0, -9.0], atol=1e-9)
        np.testing.assert_allclose(placed.verts.max(axis=0), [101.0, 5.0, -7.0], atol=1e-9)

    def test_tile_boxes_follow_the_rotation_and_the_parsed_tiles_are_not_mutated(self):
        tiles = self.raw()
        before = tiles[0].verts.copy()
        placed = no.build_navmesh(stamped(tiles, no.Placement(rotation=YAW90)))
        np.testing.assert_allclose(placed.tiles[0].bmin, (0.0, 0.0, -2.0), atol=1e-9)    # raw box (0,0,0)-(2,1,1)
        np.testing.assert_allclose(placed.tiles[0].bmax, (1.0, 1.0, 0.0), atol=1e-9)
        self.assertTrue(np.array_equal(tiles[0].verts, before))
        # the world copies carry the identity, so building from them again cannot apply the rotation twice
        self.assertTrue(placed.tiles[0].placement.is_identity)
        again = no.build_navmesh(placed.tiles)
        self.assertTrue(np.array_equal(again.verts, placed.verts))

    def test_the_identity_placement_returns_the_input_tiles_and_vertices_untouched(self):
        tiles = self.raw()
        mesh = no.build_navmesh(stamped(tiles, no.Placement(rotation=AIRPIT_ROTATION)))
        self.assertTrue(np.array_equal(mesh.verts, no.build_navmesh(tiles).verts))
        self.assertTrue(mesh.placement.is_identity)
        self.assertTrue(np.array_equal(mesh.tiles[0].verts, tiles[0].verts))
        self.assertTrue(mesh.tiles[0].placement.is_identity)

    def test_an_explicit_placement_overrides_the_stamp(self):
        stamped_tiles = stamped(self.raw(), no.Placement(rotation=YAW90))
        raw = no.build_navmesh(stamped_tiles, no.Placement())                 # the pre-placement oracle, on purpose
        np.testing.assert_allclose(raw.verts, no.build_navmesh(self.raw()).verts, atol=0.0)
        self.assertTrue(raw.placement.is_identity)
        self.assertTrue(all(t.placement.is_identity for t in raw.tiles))      # no tile claims a rotation the mesh lacks

    def test_portals_are_matched_in_the_tile_frame_so_any_yaw_keeps_the_tiles_joined(self):
        tiles = PortalTests("test_overlapping_portal_edges_connect_the_tiles").tiles()
        for rotation in (YAW90, yaw(37.0), yaw(180.0), yaw(-90.0)):
            mesh = no.build_navmesh(stamped(tiles, no.Placement(position=(3.0, 0.0, 4.0), rotation=rotation)))
            self.assertEqual((mesh.portal_pairs, int(mesh.labels.max()), mesh.unmatched_portal_edges), (1, 0, 0), rotation)

    def test_a_tilted_frame_and_mixed_placements_are_refused(self):
        tiles = self.raw()
        with self.assertRaises(no.NavMeshFormatError):
            no.build_navmesh(stamped(tiles, no.Placement(rotation=(S, 0.0, 0.0, S))))
        two = stamped(PortalTests("test_overlapping_portal_edges_connect_the_tiles").tiles(), no.Placement(rotation=YAW90))
        two[1] = dataclasses.replace(two[1], placement=no.Placement())
        with self.assertRaises(no.NavMeshFormatError):
            no.build_navmesh(two)


class NavMeshDataTreeTests(unittest.TestCase):
    """The m_Position / m_Rotation of a read_typetree() dict reach the mesh (no Arena install needed)."""

    EMPTY = struct.pack("<5i", MAGIC, 16, 3, 4, 0) + struct.pack("<6i", 0, 0, 0, 0, 0, 0) + bytes(28)

    def test_placement_is_read_from_the_tree(self):
        placement = no.placement_from_navmesh_data(navmesh_tree([], (1.0, 2.0, 3.0), BOWL_ROTATION))
        self.assertEqual((placement.position, placement.rotation), ((1.0, 2.0, 3.0), BOWL_ROTATION))

    def test_a_tree_without_a_placement_is_refused_instead_of_assumed_identity(self):
        tree = navmesh_tree([two_quads()])
        del tree["m_Rotation"]
        with self.assertRaises(no.NavMeshFormatError):
            no.placement_from_navmesh_data(tree)
        with self.assertRaises(no.NavMeshFormatError):
            no.tiles_from_navmesh_data(tree)
        with self.assertRaises(no.NavMeshFormatError):
            no.placement_from_navmesh_data({**navmesh_tree([]), "m_Position": {"x": 0.0}})

    def test_tiles_are_stamped_with_the_placement_and_empty_tiles_are_dropped(self):
        tiles = no.tiles_from_navmesh_data(navmesh_tree([self.EMPTY, two_quads()], rotation=YAW90))
        self.assertEqual(len(tiles), 1)
        self.assertEqual(tiles[0].placement.rotation, YAW90)
        np.testing.assert_allclose(tiles[0].verts[:, 0], [0, 1, 1, 0, 2, 2])      # the raw arrays stay in the tile frame

    def test_a_rotated_tree_builds_a_world_mesh_and_an_identity_tree_the_raw_one(self):
        raw = no.build_navmesh([no.parse_tile(two_quads())])
        for rotation, same in ((AIRPIT_ROTATION, True), (BOWL_ROTATION, False)):
            mesh = no.build_navmesh(no.tiles_from_navmesh_data(navmesh_tree([two_quads()], rotation=rotation)))
            self.assertEqual(np.array_equal(mesh.verts, raw.verts), same, rotation)
            self.assertEqual(mesh.placement.is_identity, same)
        self.assertEqual(mesh.locate([(0.5, 0.0, -0.5)])[0].tolist(), [0])


class ProbeDecoderTests(unittest.TestCase):
    @staticmethod
    def surface_bytes(mask=0x40042A00, name=b"", body_extra=0):
        head = struct.pack("<iq", 0, 0) + struct.pack("<i", 1) + struct.pack("<iq", 0, 0)
        head += struct.pack("<i", len(name)) + name
        head += b"\x00" * ((-len(head)) % 4)
        fields = [287145453, 1, 208.4, 121.99, 228.65, 0.0, 2.0, 0.0, mask, 1, 0, 0, 1, 1, 0, 256, 1, 0.07, 2.0, 5, 2, 0, 0]
        body = b""
        for i, v in enumerate(fields):
            if isinstance(v, float):
                body += struct.pack("<f", v)
            else:
                body += struct.pack("<I" if v > 0x7fffffff else "<i", v)
        return head + body + b"\x00" * body_extra

    def test_decodes_layer_mask_and_geometry(self):
        s = probe.decode_navmesh_surface(self.surface_bytes())
        self.assertEqual(s["layer_mask_bits"], [9, 11, 13, 18, 30])
        self.assertEqual(s["collect_objects"], "Volume")
        self.assertEqual(s["use_geometry"], "PhysicsColliders")
        self.assertAlmostEqual(s["voxel_size"], 0.07, places=6)
        self.assertEqual(s["agent_type_id"], 287145453)
        self.assertEqual(s["navmesh_data"], {"file_id": 5, "path_id": 2})

    def test_name_alignment_is_respected(self):
        s = probe.decode_navmesh_surface(self.surface_bytes(name=b"NavMesh"))
        self.assertEqual(s["layer_mask_bits"], [9, 11, 13, 18, 30])

    def test_wrong_size_is_refused(self):
        with self.assertRaises(probe.ProbeError):
            probe.decode_navmesh_surface(self.surface_bytes(body_extra=4))

    def test_toggler_name_hints(self):
        found = probe.toggler_candidates({"EFT.Interactive.BarbedWire": 26, "Game.Modes.Data.SceneVariantSwitch": 1,
                                          "ObjectsToggle": 2, "Unity.AI.Navigation.NavMeshSurface": 1, "SceneLights": 2})
        self.assertEqual(sorted(found), ["Game.Modes.Data.SceneVariantSwitch", "ObjectsToggle"])

    def test_metadata_identifier_count(self):
        data = b"\x01\x02Alpha\x00BetaGamma\x00\xff\xfe\x00x\x00Alpha\x00"
        self.assertEqual(probe.metadata_identifier_count(data), 2)


if __name__ == "__main__":
    unittest.main()
