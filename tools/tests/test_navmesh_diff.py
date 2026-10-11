"""Tests for the port collider loader, the voxel column model and the NavMesh diff (synthetic scenes only)."""
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import navmesh_diff as nd
import navmesh_oracle as no
import port_colliders as pc
import walk_model as wm
from test_navmesh_oracle import tile_blob


def write_mesh(path, verts, tris):
    verts = np.asarray(verts, dtype="<f4")
    idx = np.asarray(tris, dtype="<u2").reshape(-1)
    with open(path, "wb") as f:
        f.write(b"AMPM" + struct.pack("<IIHBB", 1, len(verts), 1, 0, 1))
        f.write(verts.tobytes())
        f.write(struct.pack("<IIB", 0, len(idx), 0))
        f.write(idx.tobytes())


def obj(oid, name, parent=None, pos=(0, 0, 0), layer=18, active=True, comps=(), scale=(1, 1, 1)):
    return {"id": oid, "name": name, "parent_id": parent, "active": active, "layer": layer, "tag": "Untagged",
            "local_position": list(pos), "local_rotation": [0, 0, 0, 1], "local_scale": list(scale), "components": list(comps)}


def box(size, center=(0, 0, 0), enabled=True, trigger=False):
    return {"type": "BoxCollider", "size": list(size), "center": list(center), "enabled": enabled, "is_trigger": trigger}


def mesh_collider(mesh_id):
    return {"type": "MeshCollider", "mesh_id": mesh_id, "enabled": True, "is_trigger": False, "convex": False}


def make_package(root: Path, objects, scene="Test", floor=(-2.0, 8.0, -2.0, 4.0)):
    (root / "scenes").mkdir(parents=True, exist_ok=True)
    (root / "meshes").mkdir(exist_ok=True)
    x0, x1, z0, z1 = floor
    # clockwise from above in Unity's left-handed convention: normal (b-a)x(c-a) points up
    write_mesh(root / "meshes" / "floor.mesh.bin", [(x0, 0, z0), (x0, 0, z1), (x1, 0, z1), (x1, 0, z0)], [(0, 1, 2), (0, 2, 3)])
    (root / "scenes" / f"{scene}.json").write_text(json.dumps({"scene_name": scene, "objects": objects}), encoding="utf-8")
    return root


class MeshBinTests(unittest.TestCase):
    def test_roundtrip_and_validation(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "m.mesh.bin"
            write_mesh(p, [(0, 0, 0), (1, 0, 0), (0, 0, 1)], [(0, 1, 2)])
            v, t = pc.read_mesh_bin(p)
            self.assertEqual(v.shape, (3, 3))
            self.assertEqual(t.tolist(), [[0, 1, 2]])
            p.write_bytes(b"XXXX" + bytes(20))
            with self.assertRaises(pc.ColliderModelError):
                pc.read_mesh_bin(p)
            write_mesh(p, [(0, 0, 0)], [(0, 1, 2)])
            with self.assertRaises(pc.ColliderModelError):
                pc.read_mesh_bin(p)


class ColliderLoaderTests(unittest.TestCase):
    def objects(self):
        return [
            obj("1", "Root"),
            obj("2", "Floor", "1", comps=[mesh_collider("floor")]),
            obj("3", "Inactive", "1", active=False, comps=[box((1, 1, 1))]),
            obj("4", "ChildOfInactive", "3", comps=[box((1, 1, 1))]),
            obj("5", "Disabled", "1", comps=[box((1, 1, 1), enabled=False)]),
            obj("6", "Trigger", "1", comps=[box((1, 1, 1), trigger=True)]),
            obj("7", "Plain", "1", pos=(3, 0.5, 0), comps=[box((1, 1, 1))]),
            obj("8", "Mirrored", "1", scale=(-1, 1, 1), comps=[mesh_collider("floor")]),
        ]

    def load(self, **kw):
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        return pc.load_package_colliders(make_package(Path(d.name), self.objects()), **kw)

    def test_only_active_enabled_non_trigger_colliders_by_default(self):
        c = self.load()
        self.assertEqual(sorted(o.names[-1] for o in c.owners), ["Floor", "Mirrored", "Plain"])
        c = self.load(include_inactive=True, include_disabled=True, include_trigger=True)
        self.assertEqual(len(c.owners), 7)

    def test_box_triangles_are_world_space(self):
        c = self.load()
        plain = next(o for o in c.owners if o.names[-1] == "Plain")
        t = c.tris[plain.tri_start:plain.tri_start + plain.tri_count]
        self.assertEqual(len(t), 12)
        pts = t.reshape(-1, 3)
        np.testing.assert_allclose(pts.min(axis=0), [2.5, 0.0, -0.5], atol=1e-9)
        np.testing.assert_allclose(pts.max(axis=0), [3.5, 1.0, 0.5], atol=1e-9)

    def test_mirrored_transform_keeps_floor_facing_up(self):
        c = self.load()
        for name in ("Floor", "Mirrored"):
            o = next(o for o in c.owners if o.names[-1] == name)
            t = c.tris[o.tri_start:o.tri_start + o.tri_count]
            self.assertTrue((wm.triangle_normals(t)[:, 1] > 0).all(), name)


class BakeRuleTests(unittest.TestCase):
    def package(self):
        objs = [
            obj("1", "Root"),
            obj("2", "Gate", "1", comps=[box((1, 1, 1))]),
            obj("3", "Leaf", "2", comps=[box((1, 1, 1))]),
            obj("4", "Gate_frame", "2", comps=[box((1, 1, 1))]),
            obj("5", "Deep", "4", comps=[box((1, 1, 1))]),               # below a frame: kept
            obj("6", "Other", "1"),
            obj("7", "Leaf", "6", comps=[box((1, 1, 1))]),
            obj("8", "SPAWN_BOUND_A", "1", comps=[box((1, 1, 1))]),
            obj("9", "Under", "8", comps=[box((1, 1, 1))]),
            obj("10", "Borders", "1"),
            obj("11", "Wall", "10", layer=29, comps=[box((1, 1, 1))]),
            obj("12", "Door_Sliding_Respawn_L", "1", comps=[box((1, 1, 1))]),
            obj("13", "Trig", "1", layer=13, comps=[box((1, 1, 1))]),
            obj("14", "Sea", "1", layer=4, comps=[box((1, 1, 1))]),
            obj("15", "Hi", "1", layer=12, comps=[box((1, 1, 1))]),
        ]
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        return pc.load_package_colliders(make_package(Path(d.name), objs))

    def reasons(self, c):
        return {o.obj_id: pc.bake_exclusion_reason(o) for o in c.owners}

    def test_name_layer_and_leaf_rules(self):
        c = self.package()
        pc.mark_barriers(c, [])
        r = self.reasons(c)
        self.assertEqual(r["8"], "SPAWN_BOUND*")
        self.assertEqual(r["9"], "SPAWN_BOUND*")
        self.assertEqual(r["11"], "LevelBorder layer 29")
        self.assertEqual(r["12"], "Door_Sliding_Respawn_L")
        self.assertEqual(r["13"], "Triggers layer 13")
        self.assertEqual(r["14"], "Water layer")
        self.assertIsNone(r["15"])

    def test_opened_barrier_excludes_non_frame_descendants_only(self):
        c = self.package()
        pc.mark_barriers(c, ["Root/Gate"])
        r = self.reasons(c)
        self.assertIsNone(r["2"])                            # the barrier object itself stays
        self.assertEqual(r["3"], "opened_barriers")          # leaf excluded
        self.assertIsNone(r["4"])                            # frame kept
        self.assertIsNone(r["5"])                            # below a frame: kept
        self.assertIsNone(r["7"])                            # same name elsewhere is untouched
        self.assertEqual(c.unresolved_barriers, [])

    def test_unresolved_and_root_paths(self):
        c = self.package()
        pc.mark_barriers(c, ["Root/Missing", "Root"])
        self.assertEqual(c.unresolved_barriers, ["Root/Missing"])
        r = self.reasons(c)
        self.assertEqual(r["3"], "opened_barriers")          # a bare root path opens its whole subtree

    def test_find_resolves_duplicate_names_to_the_first_sibling(self):
        objs = [obj("1", "Root"), obj("2", "Door", "1"), obj("3", "Leaf", "2", comps=[box((1, 1, 1))]),
                obj("4", "Door", "1"), obj("5", "Leaf", "4", comps=[box((1, 1, 1))])]
        with tempfile.TemporaryDirectory() as d:
            c = pc.load_package_colliders(make_package(Path(d), objs))
        pc.mark_barriers(c, ["Root/Door"])
        r = self.reasons(c)
        self.assertEqual(r["3"], "opened_barriers")
        self.assertIsNone(r["5"])                            # the second "Door" is not reachable by path

    def test_layer_mask_selection(self):
        c = self.package()
        pc.mark_barriers(c, [])
        everything = pc.source_owner_mask(c)
        arena = pc.source_owner_mask(c, layer_mask=1 << 18)
        self.assertGreater(everything.sum(), arena.sum())
        self.assertFalse(any(c.owners[i].layer == 12 for i in np.nonzero(arena)[0]))


def floor_triangles(x0, x1, z0, z1, y=0.0):
    a, b, c, d = (x0, y, z0), (x0, y, z1), (x1, y, z1), (x1, y, z0)
    return np.array([[a, b, c], [a, c, d]], dtype=float)


def box_triangles(center, size):
    v, t = pc.box_mesh(center, size)
    return v[t]


class WalkModelTests(unittest.TestCase):
    def setUp(self):
        self.grid = wm.make_grid(-1, 11, -1, 11, -1, 4, cs=0.1, y_step=0.1)
        self.agent = wm.AgentParams(0.28, 1.6, 0.38, 48.0)

    def build(self, tris, owners=None):
        tris = np.concatenate(tris)
        owner = np.zeros(len(tris), dtype=np.int32) if owners is None else np.asarray(owners, dtype=np.int32)
        return tris, owner, wm.build_occupancy(tris, owner, self.grid, self.agent)

    def verdict(self, tris, points, shrink=0.0, **kw):
        _t, _o, (solid, floor, _n) = self.build(tris)
        return wm.evaluate_samples(points, solid, floor, self.grid, self.agent, shrink=shrink, **kw)

    def test_open_floor_is_supported_and_free(self):
        v = self.verdict([floor_triangles(0, 10, 0, 10)], [(5, 0, 5), (1, 0, 9)])
        self.assertFalse(v.blocked.any())
        self.assertFalse(v.unsupported.any())

    def test_point_off_the_floor_is_unsupported(self):
        v = self.verdict([floor_triangles(0, 10, 0, 10)], [(10.6, 0, 5), (5, 3.0, 5)])
        self.assertTrue(v.unsupported.all())

    def test_tall_box_blocks_within_the_agent_radius_only(self):
        tris = [floor_triangles(0, 10, 0, 10), box_triangles((5, 0.9, 5), (0.6, 1.8, 0.6))]
        # the model is quantised to 0.1 m cells: a sample whose cell centre is within the radius of a
        # box cell centre is blocked (box face at x = 5.3 / z = 5.3)
        pts = [(5.45, 0, 5), (5.8, 0, 5), (5, 0, 5.45), (7, 0, 7)]
        v = self.verdict(tris, pts)
        self.assertEqual(v.blocked.tolist(), [True, False, True, False])

    def test_low_obstacle_below_the_climb_does_not_block(self):
        v = self.verdict([floor_triangles(0, 10, 0, 10), box_triangles((5, 0.15, 5), (0.6, 0.3, 0.6))], [(5.5, 0, 5)])
        self.assertFalse(v.blocked.any())

    def test_low_ceiling_blocks_and_high_ceiling_does_not(self):
        low = self.verdict([floor_triangles(0, 10, 0, 10), floor_triangles(0, 10, 0, 10, y=1.0)], [(5, 0, 5)])
        high = self.verdict([floor_triangles(0, 10, 0, 10), floor_triangles(0, 10, 0, 10, y=2.5)], [(5, 0, 5)])
        self.assertTrue(low.blocked[0])
        self.assertFalse(high.blocked[0])

    def test_slack_shrinks_the_effective_radius(self):
        tris = [floor_triangles(0, 10, 0, 10), box_triangles((5, 0.9, 5), (0.6, 1.8, 0.6))]
        pts = [(5.45, 0, 5)]
        self.assertTrue(self.verdict(tris, pts, shrink=0.0).blocked[0])
        self.assertFalse(self.verdict(tris, pts, shrink=0.1).blocked[0])

    def test_steep_ramp_is_not_a_floor(self):
        ramp = np.array([[(0, 0, 0), (0, 3, 1.5), (4, 3, 1.5)], [(0, 0, 0), (4, 3, 1.5), (4, 0, 0)]], dtype=float)
        _t, _o, (solid, floor, _n) = self.build([ramp])
        self.assertEqual(int(floor.any(axis=1).sum()), 0)
        self.assertGreater(int(solid.any(axis=1).sum()), 0)

    def test_scanline_and_padded_paths_cover_the_same_cells(self):
        big = floor_triangles(0, 6, 0, 6)
        small = []
        for i in range(24):
            for j in range(24):
                small.append(floor_triangles(i * 0.25, (i + 1) * 0.25, j * 0.25, (j + 1) * 0.25))
        _a = self.build([big])[2]
        _b = self.build(small)[2]
        a_cells = _a[1].any(axis=1)
        b_cells = _b[1].any(axis=1)
        self.assertEqual(int((a_cells & ~b_cells).sum()), 0)
        self.assertLessEqual(int((b_cells & ~a_cells).sum()), 0)       # both paths are conservative the same way
        self.assertEqual(int(a_cells.sum()), int(b_cells.sum()))

    def test_vertical_triangle_marks_the_cells_along_its_segment(self):
        wall = np.array([[(1, 0, 1), (4, 0, 1), (4, 2, 1)]], dtype=float)
        _t, _o, (solid, _f, _n) = self.build([wall])
        cells = np.nonzero(solid.any(axis=1))[0]
        zs = {int(c) // self.grid.nx for c in cells}
        xs = sorted({int(c) % self.grid.nx for c in cells})
        self.assertLessEqual(len(zs), 2)
        self.assertAlmostEqual(self.grid.x0 + xs[0] * self.grid.cs, 1.0, delta=0.11)
        self.assertAlmostEqual(self.grid.x0 + (xs[-1] + 1) * self.grid.cs, 4.0, delta=0.11)

    def test_attribution_names_the_intruding_owner(self):
        floor = floor_triangles(0, 10, 0, 10)
        b1, b2 = box_triangles((3, 0.9, 5), (0.6, 1.8, 0.6)), box_triangles((7, 0.9, 5), (0.6, 1.8, 0.6))
        tris = np.concatenate([floor, b1, b2])
        owner = np.array([0] * 2 + [1] * 12 + [2] * 12, dtype=np.int32)
        solid, floor_bits, _n = wm.build_occupancy(tris, owner, self.grid, self.agent)
        pts = np.array([(3.45, 0, 5), (6.55, 0, 5), (5, 0, 8)], dtype=float)
        v = wm.evaluate_samples(pts, solid, floor_bits, self.grid, self.agent)
        self.assertEqual(v.blocked.tolist(), [True, True, False])
        attr = wm.attribute_blockers(tris, owner, self.grid, self.agent, pts, v.blocked)
        self.assertEqual(sorted(attr), [1, 2])
        self.assertEqual(attr[1].tolist(), [0])
        self.assertEqual(attr[2].tolist(), [1])


class HelperTests(unittest.TestCase):
    def test_family_names(self):
        self.assertEqual(nd.family_name("Sandbag_03 (12)"), "Sandbag_03")
        self.assertEqual(nd.family_name("Barricade_Fanera_01_(ARENA) (8)"), "Barricade_Fanera_01")
        self.assertEqual(nd.family_name("plywood_board_2 (59) Group"), "plywood_board_2")
        self.assertEqual(nd.family_name("Floor_shards_03_(ARENA) Variant"), "Floor_shards_03")
        self.assertEqual(nd.family_name("DOORS"), "DOORS")

    def test_load_points_accepts_sidecar_shapes(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "p.json"
            p.write_text(json.dumps({
                "points": [{"name": "cap", "kind": "capture_point", "position": [1, 2, 3]}],
                "player_spawns": [{"id": "a", "position": [4, 5, 6]}],
                "bot_spawns": [{"name": "b", "position": [7, 8, 9]}],
                "exits": [{"name": "gate", "position": [1, 1, 1]}],
                "core_points": [{"id": 3, "position": [0, 0, 0]}],
                "patrol_ways": [{"hubs": [{"position": [1, 0, 1], "sub_points": [[2, 0, 2]]}]}],
            }), encoding="utf-8")
            kinds = [q["kind"] for q in nd.load_points(p)]
        self.assertEqual(kinds, ["capture_point", "player_spawn", "bot_spawn", "exit", "core_point", "patrol_hub", "patrol_point"])

    def test_cluster_of_picks_the_prop_instance_and_flags_duplicates(self):
        objs = [obj("1", "Root"), obj("2", "SOO", "1")]
        for i in range(8):
            objs.append(obj(f"p{i}", "Sandbag (1)" if i < 2 else f"Sandbag ({i})", "2", comps=[]))
            objs.append(obj(f"c{i}", "COLLIDER", f"p{i}", comps=[box((1, 1, 1))]))
            objs.append(obj(f"v{i}", "visual", f"p{i}"))
        with tempfile.TemporaryDirectory() as d:
            cset = pc.load_package_colliders(make_package(Path(d), objs))
        o = next(o for o in cset.owners if o.obj_id == "c0")
        info = nd.cluster_of(cset, o, max_prop_objects=3)
        self.assertEqual(info["path"], "Root/SOO/Sandbag (1)")
        self.assertEqual(info["container"], "Root/SOO")
        self.assertEqual(info["family"], "Sandbag")
        self.assertTrue(info["ambiguous"])
        self.assertFalse(info["collider_on_root"])
        o3 = next(o for o in cset.owners if o.obj_id == "c3")
        self.assertFalse(nd.cluster_of(cset, o3, max_prop_objects=3)["ambiguous"])
        # a larger limit would swallow the container: the whole SOO group becomes one "instance"
        self.assertEqual(nd.cluster_of(cset, o, max_prop_objects=1000)["path"], "Root/SOO")


class PresenceTests(unittest.TestCase):
    """Presence survey: a prop is `respected` only for a hole in open floor; proximity alone is `near-edge`."""

    STEP = 0.2
    ORIGIN = (0.05, 0.05)

    def lattice(self, holes, y=0.0):
        nodes = [self.ORIGIN[0] + i * self.STEP for i in range(50)]
        keep = [(x, y, z) for x in nodes for z in nodes
                if not any(abs(x - hx) <= r and abs(z - hz) <= r for hx, hz, r in holes)]
        return np.array(keep)

    def survey(self, props, holes, lattice_y=0.0, with_ring=True):
        """props: (name, x, z, kind) with kind walked | near | far. Returns {name: row}."""
        objs = [obj("1", "Root"), obj("2", "Props", "1")]
        for i, (name, x, z, _kind) in enumerate(props):
            objs.append(obj(f"p{i}", name, "2", pos=(x, 0, z)))
            objs.append(obj(f"c{i}", "collider", f"p{i}", comps=[box((1, 1.8, 1), center=(0, 0.9, 0))]))
        with tempfile.TemporaryDirectory() as d:
            cset = pc.load_package_colliders(make_package(Path(d), objs))
        by_name = {o.names[-2]: o.index for o in cset.owners}
        walked = {by_name[n]: np.array([0]) for n, _x, _z, k in props if k == "walked"}
        near = {by_name[n]: np.array([0]) for n, _x, _z, k in props if k in ("walked", "near")}
        pts = self.lattice(holes, lattice_y)
        graph = nd.SampleGraph(pts, np.zeros(len(pts), dtype=int), self.STEP, self.ORIGIN)
        cfg = nd.Config(prop_max_objects=3)
        mask = np.ones(len(cset.owners), dtype=bool)
        ring = nd.ring_survey(cset, mask, pts, graph, self.STEP, self.ORIGIN, cfg)
        rows = nd.classify_presence(cset, mask, walked, near, cfg, ring if with_ring else None)
        return {r["path"].split("/")[-1]: r for r in rows}

    PROPS = [("Hole", 5.0, 5.0, "near"), ("Edge", 0.3, 5.0, "near"), ("Open", 2.0, 8.0, "near"),
             ("Walked", 8.0, 8.0, "walked"), ("Far", 50.0, 50.0, "far")]
    HOLES = [(5.0, 5.0, 0.8), (0.3, 5.0, 0.8)]

    def test_hole_in_open_floor_is_respected(self):
        r = self.survey(self.PROPS, self.HOLES)["Hole"]
        self.assertEqual((r["state"], r["ring"], r["centre_walkable"]), ("respected", 16, False))

    def test_hole_at_the_edge_of_the_footprint_is_only_near_edge(self):
        # same hole in the floor, but the prop stands at the border of Arena's footprint: 9 of 16 ring points
        r = self.survey(self.PROPS, self.HOLES)["Edge"]
        self.assertEqual(r["state"], "near-edge")
        self.assertEqual(r["ring"], 9)
        self.assertFalse(r["centre_walkable"])

    def test_walkable_footprint_centre_is_near_edge_even_with_a_full_ring(self):
        r = self.survey(self.PROPS, self.HOLES)["Open"]
        self.assertEqual((r["state"], r["ring"], r["centre_walkable"]), ("near-edge", 16, True))

    def test_walked_through_wins_and_a_prop_without_samples_nearby_is_unknown(self):
        rows = self.survey(self.PROPS, self.HOLES)
        self.assertEqual(rows["Walked"]["state"], "walked-through")
        self.assertEqual(rows["Far"]["state"], "unknown")
        self.assertEqual(rows["Far"]["ring"], 0)

    def test_without_a_ring_survey_nothing_is_respected(self):
        rows = self.survey(self.PROPS, self.HOLES, with_ring=False)
        self.assertEqual(rows["Hole"]["state"], "near-edge")
        self.assertIsNone(rows["Hole"]["ring"])

    def test_ring_points_must_lie_at_the_props_level(self):
        # Arena's floor is 6 m above the prop's base: no ring point counts
        rows = self.survey(self.PROPS, self.HOLES, lattice_y=6.0)
        self.assertEqual(rows["Hole"]["ring"], 0)
        self.assertEqual(rows["Hole"]["state"], "near-edge")

    def test_summaries_carry_all_states(self):
        rows = list(self.survey(self.PROPS, self.HOLES).values())
        table = nd.summarise_presence(rows, "container", by="total")
        self.assertEqual(len(table), 1)
        self.assertEqual({k: table[0][k] for k in nd.PRESENCE_STATES},
                         {"walked-through": 1, "respected": 1, "near-edge": 2, "unknown": 1})


class EndToEndTests(unittest.TestCase):
    """Arena corridor 6 x 2 m; the port has a wall across it inside a prop group."""

    def arena(self):
        verts = [(0, 0, 0), (0, 0, 2), (6, 0, 2), (6, 0, 0)]
        blob = tile_blob(0, 0, verts, [([0, 1, 2, 3], [0, 0, 0, 0], 1)], (0, -1, 0), (6, 1, 2))
        return no.build_navmesh([no.parse_tile(blob)])

    def package(self, root, wall_layer=18, wall_on_root=False):
        objs = [obj("1", "Root"), obj("2", "Floor", "1", comps=[mesh_collider("floor")]), obj("3", "Props", "1"),
                obj("4", "Barrier", "3", pos=(3, 0.9, 1), comps=[box((0.3, 1.8, 2.4))] if wall_on_root else []),
                obj("6", "Other", "3"), obj("7", "OtherVisual", "6")]
        if not wall_on_root:
            objs.append(obj("5", "collider", "4", layer=wall_layer, comps=[box((0.3, 1.8, 2.4))]))
        return make_package(root, objs, floor=(-2.0, 8.0, -2.0, 4.0))

    def run_analysis(self, barriers=(), **kw):
        cfg = nd.Config(prop_max_objects=2, arena_layer_mask=1 << 18)
        points = [{"name": "west", "kind": "player_spawn", "position": [1.0, 0.0, 1.0]},
                  {"name": "east", "kind": "exit", "position": [5.0, 0.0, 1.0]},
                  {"name": "cap", "kind": "capture_point", "position": [1.2, 0.0, 1.0]}]
        with tempfile.TemporaryDirectory() as d:
            package = self.package(Path(d), **kw)
            return nd.analyse(package, cfg, barriers, points, mesh=self.arena(), log=lambda *_a: None)

    def test_wall_across_the_corridor_splits_the_port_model(self):
        r = self.run_analysis()
        self.assertEqual(r["arena"]["components"][0]["area_xz"], 12.0)
        self.assertGreater(r["samples"]["blocked_port_agent"], 0)
        west, east, cap = r["probes"]
        self.assertEqual(west["model_status"], "passable")
        self.assertNotEqual(west["model_component"], east["model_component"])
        self.assertEqual(west["model_component"], cap["model_component"])
        self.assertTrue(west["arena_contains"])
        self.assertAlmostEqual(west["arena_clearance"], 1.0, places=6)
        top = r["clusters_arena_agent"][0]
        self.assertEqual(top["path"], "Root/Props/Barrier")
        self.assertTrue(top["arena_mask_visible"])
        self.assertEqual(top["layers"], {18: 1})
        self.assertEqual(r["greedy"]["steps"][0]["path"], "Root/Props/Barrier")
        self.assertEqual(r["greedy"]["probes_joined"], 3)

    def test_opening_the_barrier_joins_the_corridor(self):
        r = self.run_analysis(barriers=["Root/Props/Barrier"])
        self.assertEqual(r["samples"]["blocked_port_agent"], 0)
        self.assertEqual(r["clusters_arena_agent"], [])
        west, east, _cap = r["probes"]
        self.assertEqual(west["model_component"], east["model_component"])
        self.assertEqual(r["greedy"]["steps"], [])

    def test_collider_on_a_layer_arena_does_not_collect_is_marked(self):
        r = self.run_analysis(wall_layer=12)
        top = r["clusters_arena_agent"][0]
        self.assertFalse(top["arena_mask_visible"])
        self.assertEqual(r["samples"]["blocked_arena_agent_arena_layers"], 0)

    def test_barrier_path_to_a_collider_on_the_root_itself_is_flagged(self):
        r = self.run_analysis(wall_on_root=True)
        self.assertTrue(r["clusters_arena_agent"][0]["collider_on_root"])

    def test_report_text_and_json_are_serialisable(self):
        r = self.run_analysis()
        text = nd.format_report(r, top=5)
        self.assertIn("Solids Arena walks through", text)
        self.assertIn("Root/Props/Barrier", text)
        self.assertIn("near-edge", text)
        self.assertEqual(sorted(r["presence"]["totals"]), sorted(nd.PRESENCE_STATES))
        self.assertEqual(r["presence"]["instances"], len(r["presence"]["rows"]))
        json.dumps(nd._plain(r))

    def test_probes_are_located_in_another_mesh(self):
        cfg = nd.Config(prop_max_objects=2, arena_layer_mask=1 << 18)
        points = [{"name": "west", "kind": "player_spawn", "position": [1.0, 0.0, 1.0]},
                  {"name": "off", "kind": "exit", "position": [50.0, 0.0, 1.0]}]
        with tempfile.TemporaryDirectory() as d:
            r = nd.analyse(self.package(Path(d)), cfg, (), points, mesh=self.arena(), other_mesh=self.arena(), log=lambda *_a: None)
        self.assertEqual([q["other_contains"] for q in r["probes"]], [True, False])
        self.assertEqual(r["other_mesh"]["probe_components"], {"0": 1})
        self.assertEqual(r["other_mesh"]["covered_samples"], r["samples"]["count"])
        self.assertIn("probes inside it: 1 of 2", nd.format_report(r))

    def test_coverage_of_another_mesh(self):
        r = self.run_analysis()
        cov = nd.coverage_of(self.arena(), r["_pts"], r["_grid"])
        self.assertTrue(cov.all())
        far = no.build_navmesh([no.parse_tile(tile_blob(0, 0, [(20, 0, 0), (20, 0, 1), (21, 0, 1), (21, 0, 0)],
                                                         [([0, 1, 2, 3], [0, 0, 0, 0], 1)], (20, 0, 0), (21, 1, 1)))])
        self.assertFalse(nd.coverage_of(far, r["_pts"], r["_grid"]).any())


if __name__ == "__main__":
    unittest.main()
