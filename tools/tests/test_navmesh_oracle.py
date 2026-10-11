"""Synthetic-tile tests for the Arena NavMesh oracle reader and the mode-toggler probe decoders.

No game data: every Detour tile here is built byte by byte from the layout documented in
tools/validate/navmesh_oracle.py (72-byte header, float3 vertices, 32-byte polygons).
"""
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
