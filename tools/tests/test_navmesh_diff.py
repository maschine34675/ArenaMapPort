"""Tests for the port collider loader, the voxel column model and the NavMesh diff (synthetic scenes only)."""
import io
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

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

    def test_keep_level_borders_keeps_layer_29_cubes_and_their_name_prefix(self):
        # BakeNavMesh.NonPhysicalLayerReason + RoundOpenedReason with keep_level_borders (Bowl 2026-10-18; the name-prefix half
        # since Iceberg 2026-10-30: its 365 cubes are called "LevelBorder (N)" under a "LevelBorders" group).
        objs = [obj("1", "Root"), obj("2", "LevelBorders", "1"),
                obj("3", "LevelBorder (113)", "2", layer=29, comps=[box((1, 1, 1))]),
                obj("4", "KillTrigger", "1", layer=29, comps=[box((1, 1, 1))]),
                obj("5", "Cube", "2", layer=18, comps=[box((1, 1, 1))]),                  # a solid under a LevelBorder* group
                obj("6", "SPAWN_BOUND_A", "1", comps=[box((1, 1, 1))])]
        with tempfile.TemporaryDirectory() as d:
            c = pc.load_package_colliders(make_package(Path(d), objs))
        pc.mark_barriers(c, [])
        dropped = {o.obj_id: pc.bake_exclusion_reason(o) for o in c.owners}
        self.assertEqual(dropped["3"], "LevelBorder layer 29")
        self.assertEqual(dropped["4"], "LevelBorder layer 29")
        self.assertEqual(dropped["5"], "LevelBorder*")
        kept = {o.obj_id: pc.bake_exclusion_reason(o, keep_borders=True) for o in c.owners}
        self.assertEqual(kept, {"3": None, "4": None, "5": None, "6": "SPAWN_BOUND*"})
        self.assertEqual(int(pc.source_owner_mask(c).sum()), 0)
        mask = pc.source_owner_mask(c, keep_borders=True)
        self.assertEqual([c.owners[i].obj_id for i in np.nonzero(mask)[0]], ["3", "4", "5"])
        only29 = pc.source_owner_mask(c, layer_mask=pc.layers_to_mask([9, 18, 29]), keep_borders=True)
        self.assertEqual(int(only29.sum()), 3)
        without29 = pc.source_owner_mask(c, layer_mask=pc.layers_to_mask([9, 18, 30]), keep_borders=True)
        self.assertEqual([c.owners[i].obj_id for i in np.nonzero(without29)[0]], ["5"])   # no 29 in the list: the cubes stay out

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

    def test_bake_exclusion_drops_the_whole_subtree_including_the_listed_object(self):
        # BakeNavMesh.BakeOnlyExclusionReason: Transform.IsChildOf is true for the object itself, and nothing
        # is opened at runtime, so unlike opened_barriers there is no frame exception.
        c = self.package()
        pc.mark_bake_exclusions(c, ["Root/Gate"])
        r = self.reasons(c)
        for oid in ("2", "3", "4", "5"):
            self.assertEqual(r[oid], "navmesh_bake_exclusions", oid)
        self.assertIsNone(r["7"])                            # same leaf name elsewhere is untouched
        self.assertIsNone(r["15"])
        self.assertEqual(c.unresolved_exclusions, [])

    def test_bake_exclusion_paths_use_find_semantics_and_report_misses(self):
        objs = [obj("1", "Root"), obj("2", "Door", "1"), obj("3", "Leaf", "2", comps=[box((1, 1, 1))]),
                obj("4", "Door", "1"), obj("5", "Leaf", "4", comps=[box((1, 1, 1))])]
        with tempfile.TemporaryDirectory() as d:
            c = pc.load_package_colliders(make_package(Path(d), objs))
        pc.mark_bake_exclusions(c, ["Root/Door", "Root/Missing"])
        r = self.reasons(c)
        self.assertEqual(r["3"], "navmesh_bake_exclusions")
        self.assertIsNone(r["5"])                            # the second "Door" is not reachable by path
        self.assertEqual(c.unresolved_exclusions, ["Root/Missing"])
        pc.mark_bake_exclusions(c, [])                       # marking is not cumulative
        self.assertTrue(all(v is None for v in self.reasons(c).values()))

    def test_barriers_and_bake_exclusions_are_independent_marks(self):
        c = self.package()
        pc.mark_bake_exclusions(c, ["Root/Other"])
        pc.mark_barriers(c, ["Root/Gate"])                   # must not clear the exclusion mark
        r = self.reasons(c)
        self.assertEqual(r["7"], "navmesh_bake_exclusions")
        self.assertEqual(r["3"], "opened_barriers")
        self.assertIsNone(r["2"])                            # the barrier object itself stays a source

    def test_layer_rules_take_precedence_over_a_bake_exclusion(self):
        c = self.package()
        pc.mark_bake_exclusions(c, ["Root"])
        r = self.reasons(c)
        self.assertEqual(r["13"], "Triggers layer 13")
        self.assertEqual(r["11"], "LevelBorder layer 29")
        self.assertEqual(r["14"], "Water layer")
        self.assertEqual(r["15"], "navmesh_bake_exclusions")

    def test_layers_to_mask_matches_arenas_surface_mask(self):
        self.assertEqual(pc.layers_to_mask([9, 11, 13, 18, 30]), nd.AIRPIT_ARENA_LAYER_MASK)
        self.assertIsNone(pc.layers_to_mask(None))
        self.assertIsNone(pc.layers_to_mask([]))
        for bad in ([32], [-1], [True], ["18"], [18.0]):
            with self.assertRaises(pc.ColliderModelError):
                pc.layers_to_mask(bad)

    def test_source_mask_combines_layers_exclusions_and_the_owner_layer_rules(self):
        c = self.package()
        pc.mark_barriers(c, [])
        pc.mark_bake_exclusions(c, ["Root/Other"])
        kept = {c.owners[i].obj_id for i in np.nonzero(pc.source_owner_mask(c, layer_mask=pc.layers_to_mask([12, 13, 29])))[0]}
        self.assertEqual(kept, {"15"})                       # 12 is collected; 13 and 29 owners are dropped anyway
        every = {c.owners[i].obj_id for i in np.nonzero(pc.source_owner_mask(c))[0]}
        self.assertNotIn("7", every)                         # excluded subtree
        self.assertIn("15", every)


def floor_triangles(x0, x1, z0, z1, y=0.0):
    a, b, c, d = (x0, y, z0), (x0, y, z1), (x1, y, z1), (x1, y, z0)
    return np.array([[a, b, c], [a, c, d]], dtype=float)


def box_triangles(center, size):
    v, t = pc.box_mesh(center, size)
    return v[t]


class DoorRowBakeTests(unittest.TestCase):
    """BakeNavMesh.ResolveOpenedBarriers on the offline model (Iceberg 2026-10-30): a barrier with door pose rows drops only the MOVED
    subtrees; a barrier without rows keeps the legacy drop-everything-below rule."""

    def package(self):
        # Root/Gates holds two sliding gates called "Gate" (told apart by 'at') and one rotating gate; every gate has the moved leaves
        # A and B, the static top leaf C and a rail collider on the gate's own child
        objs = [obj("1", "Root"), obj("2", "Gates", "1")]
        for n, (gid, x) in enumerate((("10", 0.0), ("20", 10.0))):
            objs += [obj(gid, "Gate", "2", pos=(x, 0, 0)),
                     obj(gid + "1", "Gate_A", gid, pos=(0, 0, 0.8), comps=[box((1, 1, 1))]),
                     obj(gid + "2", "Gate_A_COLLIDER", gid + "1", comps=[box((1, 1, 1))]),
                     obj(gid + "3", "Gate_B", gid, pos=(0, 0, 2.2), comps=[box((1, 1, 1))]),
                     obj(gid + "4", "Gate_C", gid, pos=(0, 0, 3.6), comps=[box((1, 1, 1))]),
                     obj(gid + "5", "Gate_COLLIDER", gid, comps=[box((1, 1, 1))]),
                     obj(gid + "6", "Gate_frame", gid, comps=[box((1, 1, 1))])]
        objs += [obj("30", "Motor", "2", pos=(20, 0, 0), comps=[box((1, 1, 1))]), obj("31", "Motor_COLLIDER", "30", comps=[box((1, 1, 1))]),
                 obj("40", "Lamp", "2", pos=(30, 0, 0)), obj("41", "Lamp_glow", "40", comps=[box((1, 1, 1))])]
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        return pc.load_package_colliders(make_package(Path(d.name), objs))

    @staticmethod
    def row(barrier, part, at=None, closed=None, node="", switch=False):
        return {"barrier": barrier, "part": part, "node": node, "at": at, "closed_position": closed, "switch": switch}

    def dropped(self, c):
        return sorted(o.obj_id for o in c.owners if o.barrier_blocked)

    def test_a_path_without_rows_keeps_the_legacy_rule_and_none_is_the_same_as_empty(self):
        for rows in (None, [], [self.row("Root/Other", "X")]):
            c = self.package()
            pc.mark_barriers(c, ["Root/Gates/Gate"], rows)
            # the legacy rule: every non-frame descendant of the FIRST "Gate" goes, the object itself and its frame stay
            self.assertEqual(self.dropped(c), ["101", "102", "103", "104", "105"], rows)
            self.assertEqual(c.unresolved_barriers, [])

    def test_a_posed_path_drops_only_the_moved_subtrees_of_every_instance_the_rows_name(self):
        c = self.package()
        rows = [self.row("Root/Gates/Gate", "Gate_A", at=[0, 0, 0]), self.row("Root/Gates/Gate", "Gate_B", at=[0, 0, 0]),
                self.row("Root/Gates/Gate", "Gate_A", at=[10, 0, 0]), self.row("Root/Gates/Gate", "Gate_B", at=[10, 0, 0])]
        pc.mark_barriers(c, ["Root/Gates/Gate"], rows)
        # A (with the collider below it) and B of both gates; C, the rail collider and the frame stay bake sources
        self.assertEqual(self.dropped(c), ["101", "102", "103", "201", "202", "203"])
        self.assertEqual(c.unresolved_barriers, [])
        reasons = {o.obj_id: pc.bake_exclusion_reason(o) for o in c.owners}
        self.assertEqual({k: v for k, v in reasons.items() if k in ("101", "102", "104", "105", "106")},
                         {"101": "opened_barriers", "102": "opened_barriers", "104": None, "105": None, "106": None})

    def test_at_picks_the_instance_and_a_gate_no_row_names_stays_a_wall(self):
        c = self.package()
        pc.mark_barriers(c, ["Root/Gates/Gate"], [self.row("Root/Gates/Gate", "Gate_A", at=[10.02, 0, 0])])
        self.assertEqual(self.dropped(c), ["201", "202"])
        c = self.package()
        pc.mark_barriers(c, ["Root/Gates/Gate"], [self.row("Root/Gates/Gate", "Gate_A", at=[99, 0, 0])])
        self.assertEqual(self.dropped(c), [])
        self.assertEqual(c.unresolved_barriers, ["Root/Gates/Gate (door pose row 0 matches no instance)"])

    def test_a_row_without_at_holds_for_the_found_instance_only(self):
        # no row carries 'at': the instances are the Transform.Find ones, i.e. the first "Gate"
        c = self.package()
        pc.mark_barriers(c, ["Root/Gates/Gate"], [self.row("Root/Gates/Gate", "Gate_B")])
        self.assertEqual(self.dropped(c), ["103"])

    def test_part_empty_moves_the_whole_instance_including_its_own_collider(self):
        c = self.package()
        pc.mark_barriers(c, ["Root/Gates/Motor"], [self.row("Root/Gates/Motor", "")])
        self.assertEqual(self.dropped(c), ["30", "31"])

    def test_a_switch_row_moves_nothing_and_is_never_unresolved(self):
        c = self.package()
        pc.mark_barriers(c, ["Root/Gates/Lamp"], [self.row("Root/Gates/Lamp", "RedFog", switch=True)])
        self.assertEqual(self.dropped(c), [])
        self.assertEqual(c.unresolved_barriers, [])

    def test_a_missing_part_and_a_missing_path_are_reported(self):
        c = self.package()
        pc.mark_barriers(c, ["Root/Gates/Gate", "Root/Gates/Nope"], [self.row("Root/Gates/Gate", "Gate_Z"), self.row("Root/Gates/Nope", "A")])
        self.assertEqual(self.dropped(c), [])
        self.assertEqual(len(c.unresolved_barriers), 2)
        self.assertTrue(c.unresolved_barriers[0].startswith("Root/Gates/Gate/Gate_Z (door pose part"))
        self.assertEqual(c.unresolved_barriers[1], "Root/Gates/Nope")

    def test_same_named_children_are_told_apart_by_the_closed_position(self):
        objs = [obj("1", "Root"), obj("2", "Doors", "1"),
                obj("3", "Leaf", "2", pos=(0, 0, 0), comps=[box((1, 1, 1))]), obj("4", "Leaf", "2", pos=(5, 0, 0), comps=[box((1, 1, 1))])]
        with tempfile.TemporaryDirectory() as d:
            c = pc.load_package_colliders(make_package(Path(d), objs))
        pc.mark_barriers(c, ["Root/Doors"], [self.row("Root/Doors", "Leaf", closed=[5.02, 0, 0])])
        self.assertEqual(self.dropped(c), ["4"])
        pc.mark_barriers(c, ["Root/Doors"], [self.row("Root/Doors", "Leaf")])                      # no closed position: the first child, as Find
        self.assertEqual(self.dropped(c), ["3"])

    def test_the_rows_are_read_like_arenapaths_parsedoorrows(self):
        with tempfile.TemporaryDirectory() as d:
            doors = Path(d) / "x.doors.json"
            doors.write_text(json.dumps({"schema_version": 2, "key": "arena_x", "poses": [
                {"barrier": "A/B", "part": "P", "node": "", "at": [1, 2, 3], "closed": {"position": [0, 0, 1]}},
                {"barrier": "A/C", "part": "", "node": "n", "at": None, "closed": {}, "switch": {"closed": False, "open": True}}]}), encoding="utf-8")
            rows = pc.load_door_rows(doors, "arena_x")
            self.assertEqual(rows, [{"barrier": "A/B", "part": "P", "node": "", "at": [1.0, 2.0, 3.0], "closed_position": [0.0, 0.0, 1.0], "switch": False},
                                    {"barrier": "A/C", "part": "", "node": "n", "at": None, "closed_position": None, "switch": True}])
            with self.assertRaises(pc.ColliderModelError):
                pc.load_door_rows(doors, "arena_other")
            doors.write_text(json.dumps({"schema_version": 1, "key": "arena_x", "poses": []}), encoding="utf-8")
            with self.assertRaises(pc.ColliderModelError):
                pc.load_door_rows(doors)
            doors.write_text(json.dumps({"schema_version": 2, "key": "arena_x", "poses": [{"barrier": "", "part": "", "node": ""}]}), encoding="utf-8")
            with self.assertRaises(pc.ColliderModelError):
                pc.load_door_rows(doors)

    def test_the_sidecar_path_is_arenapaths_doorsidecarpath(self):
        self.assertEqual(pc.door_sidecar_path("tools/maps/bay5.json"), Path("tools/maps/bay5.doors.json"))
        self.assertIsNone(pc.door_sidecar_path("tools/maps/bay5.doors.json"))
        self.assertIsNone(pc.door_sidecar_path("tools/maps/bay5.txt"))

    def test_the_cli_hands_the_rows_to_the_analysis(self):
        seen = {}

        def analyse(package, cfg, barriers, points, arena_data, **kw):
            seen.update(barriers=list(barriers), **kw)
            return {}

        with tempfile.TemporaryDirectory() as d:
            descriptor = Path(d) / "iceberg.json"
            descriptor.write_text(json.dumps({"key": "arena_iceberg", "opened_barriers": ["DOORS/GATES_B/Sanatorium_CheckPoint_Gate"]}), encoding="utf-8")
            doors = Path(d) / "iceberg.doors.json"
            doors.write_text(json.dumps({"schema_version": 2, "key": "arena_iceberg", "poses": [
                {"barrier": "DOORS/GATES_B/Sanatorium_CheckPoint_Gate", "part": "", "node": "", "at": None}]}), encoding="utf-8")
            with mock.patch.object(nd, "analyse", analyse), mock.patch.object(nd, "format_report", lambda report, top: ""):
                self.assertEqual(nd.main(["--map", "iceberg", "--descriptor", str(descriptor), "--doors"]), 0)
                self.assertEqual(len(seen["door_rows"]), 1)
                seen.clear()
                self.assertEqual(nd.main(["--map", "iceberg", "--descriptor", str(descriptor), "--doors", str(doors)]), 0)
                self.assertEqual(seen["door_rows"][0]["part"], "")
                seen.clear()
                with mock.patch("sys.stderr", new_callable=io.StringIO) as stderr:
                    self.assertEqual(nd.main(["--map", "iceberg", "--descriptor", str(descriptor)]), 0)
                self.assertIsNone(seen["door_rows"])                                                  # opt-in: the legacy model stays the default
                self.assertIn("has door rows for 1 of the 1 opened_barriers", stderr.getvalue())      # ... and the run says that it is not the bake's model
                self.assertIn("pass --doors", stderr.getvalue())
                seen.clear()
                with mock.patch("sys.stderr", new_callable=io.StringIO) as stderr:
                    self.assertEqual(nd.main(["--map", "iceberg", "--descriptor", str(descriptor), "--doors"]), 0)
                self.assertEqual(stderr.getvalue(), "")                                               # --doors follows the bake: nothing to note
                with mock.patch("sys.stderr", new_callable=io.StringIO), self.assertRaises(SystemExit):
                    nd.main(["--map", "iceberg", "--doors"])                                         # bare --doors needs a descriptor
                with mock.patch("sys.stderr", new_callable=io.StringIO), self.assertRaises(SystemExit):
                    nd.main(["--map", "bowl", "--descriptor", str(descriptor), "--doors"])           # the rows' key must be the map's


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

    def run_analysis(self, barriers=(), analysis=None, **kw):
        cfg = nd.Config(prop_max_objects=2, arena_layer_mask=1 << 18)
        points = [{"name": "west", "kind": "player_spawn", "position": [1.0, 0.0, 1.0]},
                  {"name": "east", "kind": "exit", "position": [5.0, 0.0, 1.0]},
                  {"name": "cap", "kind": "capture_point", "position": [1.2, 0.0, 1.0]}]
        with tempfile.TemporaryDirectory() as d:
            package = self.package(Path(d), **kw)
            return nd.analyse(package, cfg, barriers, points, mesh=self.arena(), log=lambda *_a: None, **(analysis or {}))

    def test_bake_exclusion_joins_the_corridor_and_the_reachability_summary_says_so(self):
        blocked = self.run_analysis()["reachability"]
        self.assertEqual((blocked["located"], blocked["in_anchor_component"]), (3, 2))
        self.assertEqual(blocked["outside"], ["exit:east"])
        self.assertEqual(sorted(blocked["per_component"].values()), [1, 2])
        r = self.run_analysis(analysis={"bake_exclusions": ["Root/Props/Barrier"]})
        self.assertEqual(r["samples"]["blocked_port_agent"], 0)
        self.assertEqual(r["port"]["exclusion_reasons"]["navmesh_bake_exclusions"], 1)
        self.assertEqual(r["config"]["bake_exclusions"], ["Root/Props/Barrier"])
        self.assertEqual(r["port"]["unresolved_exclusions"], [])
        self.assertEqual(r["reachability"]["in_anchor_component"], 3)
        self.assertEqual(r["reachability"]["outside"], [])
        self.assertIn("probe reachability: 3 of 3 located probes", nd.format_report(r))
        self.assertIn("1 navmesh_bake_exclusions", nd.format_report(r))

    def test_a_bake_exclusion_removes_a_collider_on_the_listed_object_but_a_barrier_keeps_it(self):
        # opened_barriers keeps the barrier object itself (its frame stays standing); a bake exclusion does not.
        barrier = self.run_analysis(barriers=["Root/Props/Barrier"], wall_on_root=True)
        self.assertGreater(barrier["samples"]["blocked_port_agent"], 0)
        excluded = self.run_analysis(analysis={"bake_exclusions": ["Root/Props/Barrier"]}, wall_on_root=True)
        self.assertEqual(excluded["samples"]["blocked_port_agent"], 0)

    def test_an_unresolved_exclusion_is_reported_and_changes_nothing(self):
        r = self.run_analysis(analysis={"bake_exclusions": ["Root/Props/Missing"]})
        self.assertEqual(r["port"]["unresolved_exclusions"], ["Root/Props/Missing"])
        self.assertGreater(r["samples"]["blocked_port_agent"], 0)

    def test_the_port_layer_mask_drops_a_wall_on_a_layer_the_bake_does_not_collect(self):
        wall12 = self.run_analysis(wall_layer=12)
        self.assertGreater(wall12["samples"]["blocked_port_agent"], 0)       # everything is collected by default
        masked = self.run_analysis(wall_layer=12, analysis={"port_layer_mask": 1 << 18})
        self.assertEqual(masked["samples"]["blocked_port_agent"], 0)
        self.assertEqual(masked["port"]["exclusion_reasons"]["not in navmesh_bake_layers"], 1)
        self.assertEqual(masked["config"]["port_layer_mask"], 1 << 18)
        self.assertIn("layer mask 0x40000", nd.format_report(masked))
        on18 = self.run_analysis(wall_layer=18, analysis={"port_layer_mask": 1 << 18})
        self.assertGreater(on18["samples"]["blocked_port_agent"], 0)         # a collected layer still cuts the corridor

    def test_descriptor_inputs_and_cli_precedence(self):
        self.assertEqual(nd.descriptor_bake_inputs({}), {"barriers": [], "exclusions": [], "layer_mask": None, "keep_level_borders": False})
        inputs = nd.descriptor_bake_inputs({"opened_barriers": ["A"], "navmesh_bake_exclusions": ["B/C"],
                                            "navmesh_bake_layers": [9, 11, 13, 18, 30]})
        self.assertEqual(inputs, {"barriers": ["A"], "exclusions": ["B/C"], "layer_mask": nd.AIRPIT_ARENA_LAYER_MASK,
                                  "keep_level_borders": False})
        self.assertIs(nd.descriptor_bake_inputs({"keep_level_borders": True})["keep_level_borders"], True)

        captured = {}

        def fake_analyse(package, cfg, barriers, points, *_a, **kw):
            captured.update(barriers=list(barriers), exclusions=list(kw["bake_exclusions"]), mask=kw["port_layer_mask"],
                            keep=kw["keep_level_borders"])
            raise SystemExit(0)

        with tempfile.TemporaryDirectory() as d:
            sidecar = Path(d) / "map.json"
            sidecar.write_text(json.dumps({"opened_barriers": ["A"], "navmesh_bake_exclusions": ["B/C"],
                                           "navmesh_bake_layers": [18]}), encoding="utf-8")

            def run(*args):
                captured.clear()
                original = nd.analyse
                nd.analyse = fake_analyse
                try:
                    with self.assertRaises(SystemExit):
                        nd.main(["--package", d, "--descriptor", str(sidecar), "--opened-barrier", "X",
                                 "--bake-exclusion", "Y/Z", *args])
                finally:
                    nd.analyse = original
                return dict(captured)

            self.assertEqual(run(), {"barriers": ["X", "A"], "exclusions": ["Y/Z", "B/C"], "mask": 1 << 18, "keep": False})
            self.assertTrue(run("--keep-level-borders")["keep"])
            sidecar.write_text(json.dumps({"keep_level_borders": True}), encoding="utf-8")
            self.assertTrue(run()["keep"])                           # the descriptor's key sets it without the flag
            self.assertIsNone(run("--ignore-descriptor-layers")["mask"])
            self.assertEqual(run("--port-layer-mask", "0x1000")["mask"], 0x1000)

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


class ArenaSourceTableTests(unittest.TestCase):
    """The per-map oracle rows (ARENA_SOURCES). Autoservice's row was measured on 2026-11-11 (docs/progress/autoservice-release.md, item 8);
    test_autoservice_bake_inputs.py checks it against the installed Arena and the map's plan."""

    def test_every_row_has_a_distinct_map_key_and_ai_scene_and_a_native_package(self):
        rows = nd.ARENA_SOURCES
        self.assertEqual(sorted(rows), ["airpit", "autoservice", "bowl", "iceberg"])
        self.assertEqual(len({r.key for r in rows.values()}), len(rows))
        for stem, row in rows.items():
            self.assertEqual(row.key, "arena_" + stem)
            self.assertEqual(row.path_id, 2)
            self.assertRegex(row.package, r"^work/extract/" + stem + r"-native-v\d+$")
            self.assertEqual(row.assets, f"sharedassets{row.ai_scene_index}.assets")

    def test_autoservice_is_the_measured_row(self):
        row = nd.ARENA_SOURCES["autoservice"]
        self.assertEqual((row.key, row.assets, row.path_id, row.layer_mask), ("arena_autoservice", "sharedassets63.assets", 2, 0x40042A00))
        self.assertEqual((row.ai_scene_index, row.ai_scene_name, row.package), (63, "Arena_AutoService_AI", "work/extract/autoservice-native-v2"))

    def test_the_other_rows_are_unchanged(self):
        self.assertEqual(nd.ARENA_SOURCES["bowl"].layer_mask, 0x40040A00)
        self.assertEqual((nd.ARENA_SOURCES["iceberg"].assets, nd.ARENA_SOURCES["iceberg"].ai_scene_index), ("sharedassets377.assets", 377))
        self.assertEqual((nd.ARENA_SOURCES["airpit"].assets, nd.ARENA_SOURCES["airpit"].package),
                         ("sharedassets33.assets", "work/extract/airpit-native-v8"))


if __name__ == "__main__":
    unittest.main()
