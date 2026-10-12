"""Partial-scene selection (tools/extract/scene_select.py) on synthetic levels; no Arena install needed.

A tiny builder produces the GameObject/Transform tables ``extract_equator.build_scene_graph``
returns, so the graph, the unit model, the carriers, the twin rule and the plan record are all
exercised without UnityPy. The last classes cover ScopedFile with fake UnityPy readers and the
ballistic augmenter that motivates it.
"""
from __future__ import annotations

from copy import deepcopy
import math
from pathlib import Path
import random
import sys
from types import SimpleNamespace as NS
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "extract"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import extract_ballistics as B  # noqa: E402
import scene_select as SS  # noqa: E402
from common_ids import HardExtractError  # noqa: E402
from source_plan import PlanError  # noqa: E402
from test_ballistics import contract, raw  # noqa: E402

HALF_TURN_Y = (0.0, math.sqrt(0.5), 0.0, math.sqrt(0.5))  # +90 degrees about Y
DECAL_SCRIPT = "EFT.Visual.StaticDecal.StaticDeferredDecal"


class Level:
    """Builds the tables ``Graph.from_tables`` reads; ``add`` returns the transform id (game object = tf - 1000)."""

    def __init__(self):
        self.go_map, self.tf_map, self.kinds, self.scripts, self._n = {}, {}, {}, {}, 0

    def add(self, name, parent=None, comps=(), pos=(0.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0, 1.0),
            scale=(1.0, 1.0, 1.0), active=True):
        self._n += 1
        tf, go = 2000 + self._n, 1000 + self._n
        ids = []
        for k, kind in enumerate(comps):
            cid = 100000 * self._n + k + 1
            kind, _, script = kind.partition(":")   # "MonoBehaviour:<script class>" names the script
            self.kinds[cid] = kind
            if script:
                self.scripts[cid] = script
            ids.append(cid)
        self.go_map[go] = {"name": name, "layer": 0, "tag_raw": 0, "active": active, "components": ids}
        self.tf_map[tf] = {"go_id": go, "father_id": parent if parent is not None else 0, "children": [],
                           "pos": list(pos), "rot": list(rot), "scale": list(scale)}
        if parent is not None:
            self.tf_map[parent]["children"].append(tf)
        return tf

    def graph(self, seed=None) -> SS.Graph:
        go_map, tf_map = self.go_map, self.tf_map
        if seed is not None:  # the same level with a different insertion order in every table
            rng = random.Random(seed)
            items = list(tf_map.items())
            rng.shuffle(items)
            tf_map = {k: dict(v, children=rng.sample(v["children"], len(v["children"]))) for k, v in items}
            items = list(go_map.items())
            rng.shuffle(items)
            go_map = dict(items)
        return SS.Graph.from_tables(go_map, tf_map, self.kinds.get, self.scripts.get)


def select(**changes):
    value = {"schema": 1, "reason": "test selection"}
    value.update(changes)
    return value


def record(name="Partial", index=38, **changes):
    return {"name": name, "asset_path": f"Assets/{name}.unity", "index": index, "select": select(**changes)}


def prop(level, name, parent, pos=(0.0, 0.0, 0.0), comps=("LODGroup",), **kwargs):
    """A LODGroup unit with a mesh child and a collider child, the shape of most Arena props (3 objects)."""
    root = level.add(name, parent, comps, pos=pos, **kwargs)
    level.add(name + "_LOD0", root, ("MeshFilter", "MeshRenderer"))
    level.add(name + "_COLLIDER", root, ("MeshCollider",))
    return root


def shop_level():
    """Root/Hall/Shop/{Chair, Table (lone leaf + child)}, Root/Hall/Outside/Crate, Root/Other/Lamp (Light)."""
    level = Level()
    root = level.add("Root")
    hall = level.add("Hall", root)
    shop = level.add("Shop", hall)
    outside = level.add("Outside", hall)
    other = level.add("Other", root)
    table = level.add("Table", shop, ("BoxCollider",), pos=(50.0, 0.0, 0.0))
    level.names = {"root": root, "hall": hall, "shop": shop, "outside": outside, "other": other, "table": table,
                   "chair": prop(level, "Chair", shop, pos=(1.0, 0.0, 1.0)),
                   "table_child": level.add("TableTop", table, ("MeshRenderer",)),
                   "crate": prop(level, "Crate", outside, pos=(100.0, 0.0, 0.0)),
                   "lamp": level.add("Lamp", other, ("Light",), pos=(2.0, 3.0, 4.0))}
    return level


class ValidateSelectTests(unittest.TestCase):
    def test_minimal_and_full_records_are_accepted(self):
        spec = SS.validate_select(select(roots=["A/B"]))
        self.assertEqual((spec.skip_twins_of, [r.key for r in spec.rules]), (None, ["roots:A/B"]))
        regions = [{"name": "box", "min": [0, 0, 0], "max": [1, 1, 1]}]
        spec = SS.validate_select(select(skip_twins_of=324, roots=["A"],
                                         region_roots=["B", {"path": "C", "regions": ["box"]}], regions=regions))
        self.assertEqual(spec.skip_twins_of, 324)
        self.assertEqual([r.key for r in spec.rules], ["roots:A", "region_roots:B", "region_roots:C"])
        self.assertEqual(spec.rules[1].regions[0].name, "box")

    def test_schema_and_required_keys(self):
        base = select(roots=["A"])
        cases = {"not an object": None, "a list": [], "wrong schema": dict(base, schema=2),
                 "bool schema": dict(base, schema=True), "string schema": dict(base, schema="1"),
                 "no schema": {"reason": "x", "roots": ["A"]}, "no reason": {"schema": 1, "roots": ["A"]},
                 "unknown key": dict(base, surprise=1), "blank reason": dict(base, reason="  "),
                 "non-string reason": dict(base, reason=5)}
        for label, bad in cases.items():
            with self.subTest(label), self.assertRaises(PlanError):
                SS.validate_select(bad)

    def test_nothing_selected_is_refused(self):
        for empty in (select(), select(roots=[]), select(roots=[], region_roots=[])):
            with self.subTest(empty=empty), self.assertRaisesRegex(PlanError, "selects nothing"):
                SS.validate_select(empty)

    def test_roots_and_region_roots_must_be_arrays(self):
        for bad in ({"roots": "A"}, {"roots": ["A"], "region_roots": "B"}, {"roots": ["A"], "regions": {}}):
            with self.subTest(bad=bad), self.assertRaises(PlanError):
                SS.validate_select(select(**bad))

    def test_skip_twins_of_must_be_an_index(self):
        for bad in (True, -1, "324", 3.0):
            with self.subTest(bad=bad), self.assertRaises(PlanError):
                SS.validate_select(select(roots=["A"], skip_twins_of=bad))

    def test_paths_are_checked_but_trailing_spaces_survive(self):
        for bad in ("", " ", "/A", "A/", "A//B", "A\x00", 7, None):
            with self.subTest(path=bad), self.assertRaises(PlanError):
                SS.validate_select(select(roots=[bad]))
        spec = SS.validate_select(select(roots=["Root/FLOOR_DAMAGE ", "Root/FLOOR_DAMAGE"]))
        self.assertEqual([r.path for r in spec.rules], ["Root/FLOOR_DAMAGE ", "Root/FLOOR_DAMAGE"])

    def test_duplicate_paths_are_refused_across_both_lists(self):
        regions = [{"name": "box", "min": [0, 0, 0], "max": [1, 1, 1]}]
        with self.assertRaisesRegex(PlanError, "twice"):
            SS.validate_select(select(roots=["A", "A"]))
        with self.assertRaisesRegex(PlanError, "twice"):
            SS.validate_select(select(roots=["A"], region_roots=["A"], regions=regions))
        with self.assertRaisesRegex(PlanError, "twice"):
            SS.validate_select(select(region_roots=["A", {"path": "A", "regions": ["box"]}], regions=regions))

    def test_regions_need_finite_ordered_boxes_with_unique_names(self):
        def run(regions):
            return SS.validate_select(select(region_roots=["A"], regions=regions))
        good = {"name": "box", "min": [0, 0, 0], "max": [1, 1, 1]}
        run([good])
        for bad in (dict(good, min=[0, 0]), dict(good, max=[1, 1, math.inf]), dict(good, min=[math.nan, 0, 0]),
                    dict(good, max=[0, 1, 1]), dict(good, min=[0, 2, 0]), dict(good, name=""), dict(good, extra=1),
                    {"name": "box", "min": [0, 0, 0]}, "box", dict(good, min=[True, 0, 0])):
            with self.subTest(bad=bad), self.assertRaises(PlanError):
                run([bad])
        with self.assertRaisesRegex(PlanError, "duplicate region"):
            run([good, dict(good)])
        with self.assertRaisesRegex(PlanError, "declared region"):
            SS.validate_select(select(region_roots=["A"]))

    def test_every_region_must_be_used_and_names_must_exist(self):
        a = {"name": "a", "min": [0, 0, 0], "max": [1, 1, 1]}
        b = {"name": "b", "min": [2, 2, 2], "max": [3, 3, 3]}
        with self.assertRaisesRegex(PlanError, "not used"):
            SS.validate_select(select(region_roots=[{"path": "A", "regions": ["a"]}], regions=[a, b]))
        for names in (["c"], [], ["a", "a"], "a", [1]):
            with self.subTest(names=names), self.assertRaises(PlanError):
                SS.validate_select(select(region_roots=[{"path": "A", "regions": names}], regions=[a]))
        with self.assertRaises(PlanError):
            SS.validate_select(select(region_roots=[{"path": "A", "regions": ["a"], "extra": 1}], regions=[a]))

    def test_bare_path_uses_every_region_and_the_object_form_a_subset(self):
        a = {"name": "a", "min": [0, 0, 0], "max": [1, 1, 1]}
        b = {"name": "b", "min": [2, 2, 2], "max": [3, 3, 3]}
        spec = SS.validate_select(select(region_roots=["All", {"path": "Some", "regions": ["b"]}], regions=[a, b]))
        self.assertEqual([r.name for r in spec.rules[0].regions], ["a", "b"])
        self.assertEqual([r.name for r in spec.rules[1].regions], ["b"])


class GraphTests(unittest.TestCase):
    def test_paths_keep_duplicate_sibling_names_and_trailing_spaces(self):
        level = Level()
        root = level.add("Root")
        first = level.add("BUILDING", root)
        second = level.add("BUILDING", root)
        damage = level.add("FLOOR_DAMAGE ", root)
        child = level.add("Door", second)
        graph = level.graph()
        self.assertEqual(graph.by_path["Root/BUILDING"], [first, second])
        self.assertEqual(graph.by_path["Root/FLOOR_DAMAGE "], [damage])
        self.assertNotIn("Root/FLOOR_DAMAGE", graph.by_path)
        self.assertEqual(graph.nodes[child].path, "Root/BUILDING/Door")

    def test_world_position_follows_the_whole_parent_chain(self):
        level = Level()
        root = level.add("Root", pos=(10.0, 0.0, 0.0), rot=HALF_TURN_Y, scale=(2.0, 2.0, 2.0))
        mid = level.add("Mid", root, pos=(1.0, 0.0, 0.0))
        leaf = level.add("Leaf", mid, pos=(0.0, 1.0, 0.0))
        graph = level.graph()
        # +90 degrees about Y maps local +x to world -z; the scale doubles every offset.
        for tf, expected in ((root, (10, 0, 0)), (mid, (10, 0, -2)), (leaf, (10, 2, -2))):
            self.assertEqual(tuple(round(v, 9) + 0.0 for v in graph.nodes[tf].pos), expected)

    def test_zero_quaternion_is_treated_as_identity(self):
        level = Level()
        root = level.add("Root", rot=(0.0, 0.0, 0.0, 0.0))
        child = level.add("Child", root, pos=(1.0, 2.0, 3.0))
        self.assertEqual(level.graph().nodes[child].pos, (1.0, 2.0, 3.0))

    def test_effective_activity_inherits_from_the_parent_chain(self):
        level = Level()
        root = level.add("Root", active=False)
        child = level.add("Child", root)
        grandchild = level.add("Grand", child, active=False)
        graph = level.graph()
        self.assertEqual([(graph.nodes[t].self_active, graph.nodes[t].active) for t in (root, child, grandchild)],
                         [(False, False), (True, False), (False, False)])

    def test_unit_decomposition(self):
        level = Level()
        root = level.add("Root")
        lod = level.add("Prop", root, ("LODGroup",))
        lod0 = level.add("Prop_LOD0", lod, ("MeshFilter", "MeshRenderer"))
        inner = level.add("Inner", lod, ("LODGroup",))
        collider = level.add("Prop_COLLIDER", lod, ("MeshCollider",))
        lone = level.add("Lone", root, ("BoxCollider",))
        lone_child = level.add("LoneChild", lone, ("MeshRenderer", "BoxCollider"))
        lamp = level.add("Lamp", root, ("Light",))
        smoke = level.add("Smoke", root, ("ParticleSystem",))
        body = level.add("Body", root, ("Rigidbody",))
        script_only = level.add("Script", root, ("MonoBehaviour",))
        other_script = level.add("OtherScript", root, ("MonoBehaviour:Some.Other",))
        mesh_filter_only = level.add("FilterOnly", root, ("MeshFilter",))
        empty = level.add("Empty", root)
        spray = level.add("Spray", root, ("MonoBehaviour:" + DECAL_SCRIPT,))
        spray_child = level.add("SprayChild", spray, ("MeshRenderer",))
        graph = level.graph()
        unit = {t: graph.nodes[t].unit for t in graph.nodes}
        self.assertEqual({unit[t] for t in (lod, lod0, inner, collider)}, {lod})
        self.assertEqual({unit[t] for t in (lone, lone_child)}, {lone})
        for single in (lamp, smoke, body):
            self.assertEqual(unit[single], single)
        # a renderer-less StaticDeferredDecal is a unit of its own and takes its children along
        self.assertEqual({unit[t] for t in (spray, spray_child)}, {spray})
        for container in (root, script_only, other_script, mesh_filter_only, empty):
            self.assertIsNone(unit[container])
        self.assertEqual([n.tf_id for n in graph.unit_roots],
                         sorted([lod, lone, lamp, smoke, body, spray], key=lambda t: (graph.nodes[t].path, t)))

    def test_unit_scripts_name_the_decal_class_the_augmenter_restores(self):
        import extract_decals
        self.assertEqual(SS.UNIT_SCRIPTS, frozenset((extract_decals.SOURCE_TYPE,)))

    def test_scripts_are_recorded_per_node_and_stay_out_of_the_twin_signature(self):
        level = Level()
        a = level.add("A", comps=("MonoBehaviour:" + DECAL_SCRIPT, "MeshRenderer", "MonoBehaviour", "MonoBehaviour:B.C"))
        node = level.graph().nodes[a]
        self.assertEqual(node.scripts, ("", "B.C", DECAL_SCRIPT))
        self.assertEqual(node.types, ("MeshRenderer",))
        # without a resolver every script is unresolved and no script can make a unit
        bare = Level()
        b = bare.add("B", comps=("MonoBehaviour:" + DECAL_SCRIPT,))
        graph = SS.Graph.from_tables(bare.go_map, bare.tf_map, bare.kinds.get)
        self.assertEqual((graph.nodes[b].scripts, graph.nodes[b].unit), (("",), None))

    def test_signature_leaves_out_transform_and_monobehaviour(self):
        level = Level()
        a = level.add("A", comps=("Transform", "MonoBehaviour", "MeshRenderer", "MeshFilter", "MonoBehaviour"))
        self.assertEqual(level.graph().nodes[a].types, ("MeshFilter", "MeshRenderer"))

    def test_hierarchy_cycles_are_refused(self):
        level = Level()
        a, b = level.add("A"), level.add("B")
        level.tf_map[a]["father_id"], level.tf_map[b]["father_id"] = b, a
        with self.assertRaisesRegex(HardExtractError, "cycle"):
            level.graph()

    def test_transforms_without_a_game_object_are_skipped_and_foreign_fathers_make_roots(self):
        level = Level()
        root = level.add("Root")
        orphan = level.add("Orphan", root)
        foreign = level.add("Foreign", root)
        del level.go_map[level.tf_map[orphan]["go_id"]]
        level.tf_map[foreign]["father_id"] = -1
        graph = level.graph()
        self.assertEqual(sorted(graph.nodes), [root, foreign])
        self.assertIsNone(graph.nodes[foreign].parent)
        self.assertEqual(graph.nodes[foreign].path, "Foreign")

    def test_subtree_and_ancestors(self):
        level = shop_level()
        graph, n = level.graph(), level.names
        self.assertEqual(sorted(graph.subtree(n["chair"])), [n["chair"], n["chair"] + 1, n["chair"] + 2])
        self.assertEqual(list(graph.ancestors(n["chair"])), [n["shop"], n["hall"], n["root"]])
        self.assertEqual(list(graph.ancestors(n["root"])), [])


class BuildGraphTests(unittest.TestCase):
    """build_graph feeds Graph.from_tables with the tables extract_equator.build_scene_graph reads."""

    def test_graph_of_a_fake_unity_file(self):
        def vector(x, y, z):
            return {"x": x, "y": y, "z": z}

        def game_object_reader(path_id, name, components, layer):
            tree = {"m_Name": name, "m_Layer": layer, "m_Tag": 0, "m_IsActive": True,
                    "m_Component": [{"component": {"m_FileID": 0, "m_PathID": c}} for c in components]}
            return NS(path_id=path_id, type=NS(name="GameObject"), read_typetree=lambda: tree)

        def transform_reader(path_id, owner, father, pos):
            tree = {"m_GameObject": {"m_PathID": owner}, "m_Father": {"m_FileID": 0, "m_PathID": father},
                    "m_Children": [], "m_LocalPosition": vector(*pos), "m_LocalRotation": dict(vector(0, 0, 0), w=1),
                    "m_LocalScale": vector(1, 1, 1)}
            return NS(path_id=path_id, type=NS(name="Transform"), read_typetree=lambda: tree)
        def script_reader(path_id, class_name, namespace=""):
            script = NS(path_id=900 + path_id, read_typetree=lambda: {"m_ClassName": class_name, "m_Namespace": namespace})
            return NS(path_id=path_id, type=NS(name="MonoBehaviour"),
                      parse_monobehaviour_head=lambda: NS(m_Script=script, m_GameObject=None))
        readers = [game_object_reader(1, "Root", [11], 0), game_object_reader(2, "Prop", [12, 13, 14], 5),
                   transform_reader(11, 1, 0, (1, 0, 0)), transform_reader(12, 2, 11, (0, 2, 0)),
                   NS(path_id=13, type=NS(name="LODGroup")), NS(path_id=14, type=NS(name="MonoBehaviour")),
                   game_object_reader(3, "Spray", [15, 16], 0), transform_reader(15, 3, 11, (0, 0, 3)),
                   script_reader(16, "StaticDeferredDecal", "EFT.Visual.StaticDecal")]
        graph = SS.build_graph(NS(objects={r.path_id: r for r in readers}))
        self.assertEqual(sorted(graph.nodes), [11, 12, 15])
        node = graph.nodes[12]
        self.assertEqual((node.path, node.pos, node.types, node.unit, node.layer, node.go_id),
                         ("Root/Prop", (1.0, 2.0, 0.0), ("LODGroup",), 12, 5, 2))
        # a reader without a readable script header resolves to "" and cannot make a unit
        self.assertEqual(node.scripts, ("",))
        # the namespaced script of a renderer-less leaf makes it a unit
        spray = graph.nodes[15]
        self.assertEqual((spray.scripts, spray.types, spray.unit, spray.pos), ((DECAL_SCRIPT,), (), 15, (1.0, 0.0, 3.0)))


class SelectNodesTests(unittest.TestCase):
    def setUp(self):
        self.level = shop_level()
        self.n = self.level.names
        self.graph = self.level.graph()

    def test_roots_take_whole_units_and_emit_the_carrier_chain(self):
        n = self.n
        sel = SS.select_nodes(record(roots=["Root/Hall/Shop"]), self.graph)
        expected = {n["chair"], n["chair"] + 1, n["chair"] + 2, n["table"], n["table_child"],
                    n["root"], n["hall"], n["shop"]}
        self.assertEqual(set(sel.node_tf_ids), expected)
        self.assertEqual(list(sel.node_tf_ids), sorted(expected))
        self.assertEqual(sel.carrier_go_ids, {n[k] - 1000 for k in ("root", "hall", "shop")})
        self.assertEqual(sel.go_ids, {t - 1000 for t in expected})
        self.assertEqual(sel.unit_tf_ids, (n["chair"], n["table"]))
        report = sel.report
        self.assertEqual((report["objects"], report["units"], report["carriers"]), (len(expected), 2, 3))
        self.assertEqual(report["rules"], {"roots:Root/Hall/Shop": {"selected": 2, "twin_skipped": 0}})
        self.assertEqual(report["go_ids_sha256"], SS.go_ids_digest(sel.go_ids))

    def test_a_root_path_never_matches_a_longer_sibling_name(self):
        level = Level()
        root = level.add("Root")
        shop = prop(level, "Chair", level.add("Shop", root))
        prop(level, "Table", level.add("Shop2", root))
        prop(level, "Sofa", level.add("Shop (1)", root))
        sel = SS.select_nodes(record(roots=["Root/Shop"]), level.graph())
        self.assertEqual(sel.unit_tf_ids, (shop,))

    def test_a_root_may_be_a_unit_root_and_carriers_start_at_the_real_root(self):
        sel = SS.select_nodes(record(roots=["Root/Other/Lamp"]), self.graph)
        self.assertEqual(set(sel.node_tf_ids), {self.n["lamp"], self.n["other"], self.n["root"]})
        self.assertEqual(sel.report["carriers"], 2)

    def test_carriers_are_exactly_the_container_ancestors_of_the_selected_units(self):
        sel = SS.select_nodes(record(roots=["Root"]), self.graph)
        self.assertEqual(sel.report["carriers"], 5)  # Root, Hall, Shop, Outside, Other
        for tf in sel.node_tf_ids:
            node = self.graph.nodes[tf]
            self.assertEqual(node.go_id in sel.carrier_go_ids, node.unit is None)

    def test_unresolved_roots_fail_closed(self):
        for path in ("Root/Hall/Missing", "root/hall", "Hall", "Root/Hall/Shop/Chair/Missing"):
            with self.subTest(path=path), self.assertRaisesRegex(HardExtractError, "resolves to no object"):
                SS.select_nodes(record(roots=[path]), self.graph)
        box = {"name": "all", "min": [-1e9] * 3, "max": [1e9] * 3}
        with self.assertRaisesRegex(HardExtractError, "region_roots:Root/Nope"):
            SS.select_nodes(record(roots=["Root/Hall/Shop"], region_roots=["Root/Nope"], regions=[box]), self.graph)

    def test_a_root_inside_a_unit_is_refused(self):
        with self.assertRaisesRegex(HardExtractError, "inside the unit"):
            SS.select_nodes(record(roots=["Root/Hall/Shop/Chair/Chair_LOD0"]), self.graph)
        with self.assertRaisesRegex(HardExtractError, "inside the unit"):
            SS.select_nodes(record(roots=["Root/Hall/Shop/Table/TableTop"]), self.graph)

    def test_trailing_space_names_are_exact(self):
        level = Level()
        root = level.add("Root")
        prop(level, "Plank", level.add("FLOOR_DAMAGE ", root))
        prop(level, "Nail", level.add("FLOOR_DAMAGE", root))
        graph = level.graph()
        sel = SS.select_nodes(record(roots=["Root/FLOOR_DAMAGE "]), graph)
        self.assertEqual([graph.nodes[t].name for t in sel.unit_tf_ids], ["Plank"])
        with self.assertRaises(HardExtractError):
            SS.select_nodes(record(roots=["Root/FLOOR_DAMAGE  "]), graph)

    def test_duplicate_sibling_names_select_every_match(self):
        level = Level()
        root = level.add("Root")
        first, second = level.add("BUILDING", root), level.add("BUILDING", root)
        a, b = prop(level, "Door", first), prop(level, "Door", second)
        sel = SS.select_nodes(record(roots=["Root/BUILDING"]), level.graph())
        self.assertEqual(sel.unit_tf_ids, (a, b))
        self.assertEqual(sel.report["carriers"], 3)

    def test_region_box_is_inclusive_and_uses_the_unit_root_position(self):
        level = Level()
        group = level.add("Group", level.add("Root"))
        inside = prop(level, "Inside", group, pos=(5.0, 1.0, 5.0))
        face_lo = prop(level, "FaceLo", group, pos=(0.0, 0.0, 0.0))
        face_hi = prop(level, "FaceHi", group, pos=(10.0, 2.0, 10.0))
        outside_x = prop(level, "OutsideX", group, pos=(10.001, 1.0, 5.0))
        outside_y = prop(level, "OutsideY", group, pos=(5.0, -0.001, 5.0))
        far_child = prop(level, "FarChild", group, pos=(1.0, 1.0, 1.0))
        level.add("Mesh", far_child, ("MeshRenderer",), pos=(500.0, 0.0, 0.0))  # root decides, not the members
        box = {"name": "box", "min": [0, 0, 0], "max": [10, 2, 10]}
        sel = SS.select_nodes(record(region_roots=["Root/Group"], regions=[box]), level.graph())
        self.assertEqual(set(sel.unit_tf_ids), {inside, face_lo, face_hi, far_child})
        self.assertNotIn(outside_x, sel.unit_tf_ids)
        self.assertNotIn(outside_y, sel.unit_tf_ids)

    def test_a_region_root_may_name_several_regions(self):
        level = Level()
        group = level.add("Group", level.add("Root"))
        west = prop(level, "West", group, pos=(1.0, 0.0, 0.0))
        east = prop(level, "East", group, pos=(101.0, 0.0, 0.0))
        middle = prop(level, "Middle", group, pos=(51.0, 0.0, 0.0))
        regions = [{"name": "west", "min": [0, -1, -1], "max": [10, 1, 1]},
                   {"name": "east", "min": [100, -1, -1], "max": [110, 1, 1]}]
        sel = SS.select_nodes(record(region_roots=["Root/Group"], regions=regions), level.graph())
        self.assertEqual(set(sel.unit_tf_ids), {west, east})
        self.assertNotIn(middle, sel.unit_tf_ids)

    def test_each_region_root_uses_its_own_regions_and_roots_claim_before_region_roots(self):
        level = Level()
        root = level.add("Root")
        near, far = level.add("Near", root), level.add("Far", root)
        a = prop(level, "A", near, pos=(1.0, 0.0, 0.0))
        b = prop(level, "B", near, pos=(101.0, 0.0, 0.0))
        c = prop(level, "C", far, pos=(1.0, 0.0, 0.0))
        d = prop(level, "D", far, pos=(101.0, 0.0, 0.0))
        graph = level.graph()
        regions = [{"name": "west", "min": [0, -1, -1], "max": [10, 1, 1]},
                   {"name": "east", "min": [100, -1, -1], "max": [110, 1, 1]}]
        rec = record(roots=["Root/Near/A"], regions=regions,
                     region_roots=[{"path": "Root/Near", "regions": ["east"]},
                                   {"path": "Root/Far", "regions": ["west"]}])
        sel = SS.select_nodes(rec, graph)
        self.assertEqual(set(sel.unit_tf_ids), {a, b, c})
        self.assertNotIn(d, sel.unit_tf_ids)
        self.assertEqual(sel.report["rules"], {"roots:Root/Near/A": {"selected": 1, "twin_skipped": 0},
                                               "region_roots:Root/Near": {"selected": 1, "twin_skipped": 0},
                                               "region_roots:Root/Far": {"selected": 1, "twin_skipped": 0}})
        # a unit under both kinds of rule is claimed by the roots rule, whatever the plan text order
        both = SS.select_nodes(record(roots=["Root/Near"], regions=regions[:1],
                                      region_roots=[{"path": "Root/Near/A", "regions": ["west"]}]), graph)
        self.assertEqual(both.report["rules"]["roots:Root/Near"]["selected"], 2)
        self.assertEqual(both.report["rules"]["region_roots:Root/Near/A"]["selected"], 0)

    def test_missing_reference_graph_and_empty_selection_are_refused(self):
        with self.assertRaisesRegex(HardExtractError, "no reference graph"):
            SS.select_nodes(record(roots=["Root/Hall/Shop"], skip_twins_of=324), self.graph)
        box = {"name": "nowhere", "min": [900, 900, 900], "max": [901, 901, 901]}
        with self.assertRaisesRegex(HardExtractError, "selects no unit"):
            SS.select_nodes(record(region_roots=["Root/Hall"], regions=[box]), self.graph)

    def test_a_malformed_record_is_a_plan_error(self):
        with self.assertRaises(PlanError):
            SS.select_nodes({"name": "Partial", "index": 38}, self.graph)

    def test_a_renderer_less_decal_leaf_is_selected_like_any_other_unit(self):
        level = Level()
        root = level.add("Root")
        shop = level.add("Shop", root)
        chair = prop(level, "Chair", shop)
        spray = level.add("Spray (3)", shop, ("MonoBehaviour:" + DECAL_SCRIPT,), pos=(1.0, 0.0, 0.0))
        sel = SS.select_nodes(record(roots=["Root/Shop"]), level.graph())
        self.assertEqual(sel.unit_tf_ids, (chair, spray))
        self.assertEqual(set(sel.node_tf_ids), {root, shop, chair, chair + 1, chair + 2, spray})
        self.assertEqual(sel.carrier_go_ids, {root - 1000, shop - 1000})
        self.assertEqual(sel.report["dropped_script_owners"], 0)

    def test_a_renderer_less_decal_leaf_is_found_by_its_region_and_twin_skipped_like_a_unit(self):
        level = Level()
        root = level.add("Root")
        inside = level.add("Spray", root, ("MonoBehaviour:" + DECAL_SCRIPT,), pos=(1.0, 0.0, 0.0))
        twin = level.add("Spray (1)", root, ("MonoBehaviour:" + DECAL_SCRIPT,), pos=(2.0, 0.0, 0.0))
        level.add("Spray", root, ("MonoBehaviour:" + DECAL_SCRIPT,), pos=(50.0, 0.0, 0.0))   # outside the region
        reference = Level()
        ref_root = reference.add("Root")
        reference.add("Spray", ref_root, ("MonoBehaviour:" + DECAL_SCRIPT,), pos=(2.0, 0.0, 0.0))
        rec = record(region_roots=["Root"], skip_twins_of=324,
                     regions=[{"name": "box", "min": [0, -1, -1], "max": [3, 1, 1]}])
        sel = SS.select_nodes(rec, level.graph(), reference.graph())
        self.assertEqual(sel.unit_tf_ids, (inside,))
        self.assertEqual(set(sel.twins), {twin})
        self.assertEqual(sel.report["rules"], {"region_roots:Root": {"selected": 1, "twin_skipped": 1}})
        self.assertEqual(sel.report["dropped_script_owners"], 0)

    def test_dropped_script_owners_are_claimed_containers_that_are_neither_unit_nor_carrier(self):
        level = Level()
        root = level.add("Root")
        shop = level.add("Shop", root)
        prop(level, "Chair", shop)
        level.add("Manager", shop, ("MonoBehaviour:Some.Other",))                   # container, no unit below: dropped
        level.add("Quiet", shop)                                                    # container without a script
        other = level.add("Other", root)
        level.add("Manager", other, ("MonoBehaviour:Some.Other", "MonoBehaviour"))  # outside the claimed path
        group = level.add("Group", shop, ("MonoBehaviour:Some.Other",))             # carrier of the table below: not counted
        prop(level, "Table", group)
        sel = SS.select_nodes(record(roots=["Root/Shop"]), level.graph())
        self.assertEqual(sel.report["dropped_script_owners"], 1)
        self.assertIn(group, sel.node_tf_ids)

    def test_scripts_inside_selected_and_twin_skipped_units_are_not_dropped(self):
        level = Level()
        root = level.add("Root")
        shop = level.add("Shop", root)
        kept = prop(level, "Chair", shop, pos=(1.0, 0.0, 0.0))
        level.add("Script", kept, ("MonoBehaviour:Some.Other",))
        skipped = prop(level, "Table", shop, pos=(2.0, 0.0, 0.0))
        level.add("Script", skipped, ("MonoBehaviour:Some.Other",))
        keeper = prop(level, "Keeper", shop, pos=(9.0, 0.0, 0.0))
        reference = Level()
        ref_root = reference.add("Root")
        prop(reference, "Table", ref_root, pos=(2.0, 0.0, 0.0))
        sel = SS.select_nodes(record(roots=["Root/Shop"], skip_twins_of=324), level.graph(), reference.graph())
        self.assertEqual(set(sel.twins), {skipped})
        self.assertEqual(sel.unit_tf_ids, (kept, keeper))
        self.assertEqual(sel.report["dropped_script_owners"], 0)

    def test_the_dropped_count_respects_the_regions_of_a_region_root(self):
        level = Level()
        root = level.add("Root")
        prop(level, "Chair", root, pos=(1.0, 0.0, 0.0))
        zone_in = level.add("ZoneIn", root, pos=(1.0, 0.0, 0.0))
        zone_out = level.add("ZoneOut", root, pos=(50.0, 0.0, 0.0))
        level.add("Manager", zone_in, ("MonoBehaviour:Some.Other",))
        level.add("Manager", zone_out, ("MonoBehaviour:Some.Other",))
        box = {"name": "box", "min": [0, -1, -1], "max": [3, 1, 1]}
        region = SS.select_nodes(record(region_roots=["Root"], regions=[box]), level.graph())
        self.assertEqual(region.report["dropped_script_owners"], 1)
        roots = SS.select_nodes(record(roots=["Root"]), level.graph())
        self.assertEqual(roots.report["dropped_script_owners"], 2)

    def test_results_do_not_depend_on_table_order(self):
        rec = record(roots=["Root/Hall/Shop", "Root/Other"])
        base = SS.select_nodes(rec, self.level.graph())
        for seed in range(6):
            self.assertEqual(SS.select_nodes(rec, self.level.graph(seed)), base, seed)


class TwinTests(unittest.TestCase):
    """Every case selects ``Root/Shop`` of a candidate level against a reference level; the candidate level always
    carries a ``Keeper`` unit without a twin, so the selection is never empty."""

    def run_select(self, candidate_build, reference_build):
        candidate = Level()
        shop = candidate.add("Shop", candidate.add("Root"))
        candidate_build(candidate, shop)
        prop(candidate, "Keeper", shop, pos=(99.0, 0.0, 0.0))
        reference = Level()
        reference.add("Pad")  # keeps reference transform ids apart from the candidate's
        reference_build(reference, reference.add("Shop", reference.add("Root")))
        graph, ref_graph = candidate.graph(), reference.graph()
        return graph, ref_graph, SS.select_nodes(record(roots=["Root/Shop"], skip_twins_of=324), graph, ref_graph)

    def kept(self, graph, sel):
        return sorted(graph.nodes[t].name for t in sel.unit_tf_ids)

    def test_exact_twin_is_skipped_and_the_rest_is_kept(self):
        def reference(level, shop):
            prop(level, "Chair", shop, pos=(1.0, 0.0, 1.0))

        def candidates(level, shop):
            prop(level, "Chair", shop, pos=(1.0, 0.0, 1.0))
            prop(level, "Table", shop, pos=(5.0, 0.0, 0.0))
        graph, _ref, sel = self.run_select(candidates, reference)
        self.assertEqual(self.kept(graph, sel), ["Keeper", "Table"])
        self.assertEqual(sel.report["rules"]["roots:Root/Shop"], {"selected": 2, "twin_skipped": 1})
        self.assertEqual(len(sel.twins), 1)

    def test_a_selection_of_nothing_but_twins_selects_no_unit(self):
        level = Level()
        prop(level, "Chair", level.add("Shop", level.add("Root")), pos=(1.0, 0.0, 1.0))
        with self.assertRaisesRegex(HardExtractError, "selects no unit"):
            SS.select_nodes(record(roots=["Root/Shop"], skip_twins_of=324), level.graph(), level.graph())

    def test_tolerance_boundary_is_inclusive_and_per_axis(self):
        cases = (((0.02, 0, 0), True), ((0.0201, 0, 0), False), ((0, -0.02, 0.02), True),
                 ((0.015, 0.015, 0.015), True), ((0, 0, 0.021), False), ((0.0, 0.5, 0.0), False))
        for offset, twin in cases:
            def reference(level, shop, offset=offset):
                prop(level, "Chair", shop, pos=(10.0 + offset[0], 3.0 + offset[1], -4.0 + offset[2]))

            def candidates(level, shop):
                prop(level, "Chair", shop, pos=(10.0, 3.0, -4.0))
            with self.subTest(offset=offset):
                graph, _ref, sel = self.run_select(candidates, reference)
                self.assertEqual(self.kept(graph, sel), ["Keeper"] if twin else ["Chair", "Keeper"])

    def test_activity_must_match_the_effective_state(self):
        def active(level, shop):
            prop(level, "Chair", shop, pos=(1.0, 0.0, 0.0))

        def self_inactive(level, shop):
            prop(level, "Chair", shop, pos=(1.0, 0.0, 0.0), active=False)

        def parent_inactive(level, shop):
            prop(level, "Chair", level.add("Holder", shop, active=False), pos=(1.0, 0.0, 0.0))
        # Same name and position, different effective activity: not a twin, the unit is kept.
        for reference in (self_inactive, parent_inactive):
            with self.subTest(reference=reference.__name__):
                graph, _ref, sel = self.run_select(active, reference)
                self.assertEqual(self.kept(graph, sel), ["Chair", "Keeper"])
        # Inactive on both sides, by different routes (own flag vs parent chain): a twin.
        graph, _ref, sel = self.run_select(self_inactive, parent_inactive)
        self.assertEqual(self.kept(graph, sel), ["Keeper"])

    def test_copy_suffix_and_component_multiset(self):
        def reference(level, shop):
            prop(level, "Chair", shop, pos=(1.0, 0.0, 0.0))

        def candidate(name, comps=("LODGroup",)):
            return lambda level, shop: prop(level, name, shop, pos=(1.0, 0.0, 0.0), comps=comps)
        cases = (("suffix", candidate("Chair (12)"), ["Keeper"]),
                 ("not a counter", candidate("Chair (a)"), ["Chair (a)", "Keeper"]),
                 ("other component", candidate("Chair", ("LODGroup", "MeshRenderer")), ["Chair", "Keeper"]),
                 ("script component is ignored", candidate("Chair", ("LODGroup", "MonoBehaviour")), ["Keeper"]))
        for label, build, kept in cases:
            with self.subTest(label):
                graph, _ref, sel = self.run_select(build, reference)
                self.assertEqual(self.kept(graph, sel), kept)

    def test_greedy_matching_uses_each_reference_unit_once_in_path_then_id_order(self):
        def reference(level, shop):
            prop(level, "Cup", shop, pos=(1.0, 0.0, 0.0))

        def candidates(level, shop):
            prop(level, "Cup (1)", shop, pos=(1.0, 0.0, 0.0))   # its path sorts after "Cup"
            prop(level, "Cup", shop, pos=(1.0, 0.0, 0.0))
        graph, _ref, sel = self.run_select(candidates, reference)
        self.assertEqual(self.kept(graph, sel), ["Cup (1)", "Keeper"])

        def same_path(level, shop):
            prop(level, "Cup", shop, pos=(1.0, 0.0, 0.0))
            prop(level, "Cup", shop, pos=(1.0, 0.0, 0.0))
        graph, _ref, sel = self.run_select(same_path, reference)
        cups = sorted(t for t in graph.nodes if graph.nodes[t].name == "Cup" and graph.nodes[t].unit == t)
        self.assertEqual(list(sel.twins), [cups[0]], "the lower transform id takes the twin")
        self.assertIn(cups[1], sel.unit_tf_ids)

    def test_nearest_reference_wins_so_the_other_one_stays_available(self):
        def reference(level, shop):
            prop(level, "Cup", shop, pos=(1.015, 0.0, 0.0))
            prop(level, "Cup", shop, pos=(1.000, 0.0, 0.0))

        def candidates(level, shop):
            prop(level, "Cup", shop, pos=(1.0, 0.0, 0.0))        # both references are in reach; 1.000 is nearer
            prop(level, "Cup (1)", shop, pos=(1.030, 0.0, 0.0))  # only the 1.015 unit is in reach
        graph, ref, sel = self.run_select(candidates, reference)
        self.assertEqual(self.kept(graph, sel), ["Keeper"])
        pairs = {graph.nodes[c].name: ref.nodes[r].pos[0] for c, r in sel.twins.items()}
        self.assertEqual(pairs, {"Cup": 1.0, "Cup (1)": 1.015})

    def test_twins_are_found_across_the_lookup_grid_cells(self):
        def reference(level, shop):
            prop(level, "Cup", shop, pos=(0.1005, 0.0, 0.0))
            prop(level, "Mug", shop, pos=(-0.0005, 0.0, 0.0))
            prop(level, "Jug", shop, pos=(0.0, 0.1005, 0.0))
            prop(level, "Pot", shop, pos=(0.0, 0.0, 0.1005))
            prop(level, "Pan", shop, pos=(0.1005, 0.1005, 0.1005))

        def candidates(level, shop):
            prop(level, "Cup", shop, pos=(0.0995, 0.0, 0.0))     # another 0.1 m cell, 0.001 m away
            prop(level, "Mug", shop, pos=(0.0005, 0.0, 0.0))     # across zero
            prop(level, "Jug", shop, pos=(0.0, 0.0995, 0.0))     # the same on y
            prop(level, "Pot", shop, pos=(0.0, 0.0, 0.0995))     # and on z
            prop(level, "Pan", shop, pos=(0.0995, 0.0995, 0.0995))
        graph, _ref, sel = self.run_select(candidates, reference)
        self.assertEqual(self.kept(graph, sel), ["Keeper"])

    def test_match_twins_reports_pairs_and_never_matches_members(self):
        ref = Level()
        ref.add("Pad")
        ref_prop = prop(ref, "Chair", ref.add("Shop"), pos=(1.0, 0.0, 0.0))
        cand = Level()
        cand_prop = prop(cand, "Chair", cand.add("Shop"), pos=(1.0, 0.0, 0.0))
        graph = cand.graph()
        self.assertEqual(SS.match_twins([graph.nodes[cand_prop]], ref.graph()), {cand_prop: ref_prop})
        self.assertEqual(SS.match_twins([graph.nodes[cand_prop + 1]], ref.graph()), {})

    def test_strip_copy_suffix(self):
        for name, expected in (("Chair (12)", "Chair"), ("Chair(3)", "Chair"), ("Chair (1) (2)", "Chair (1)"),
                               ("Chair", "Chair"), ("Chair (a)", "Chair (a)"), ("(5)", ""), (" Chair (7) ", "Chair"),
                               ("FLOOR_DAMAGE ", "FLOOR_DAMAGE")):
            self.assertEqual(SS.strip_copy_suffix(name), expected, name)


class StructureProblemsTests(unittest.TestCase):
    def selection(self):
        return NS(go_ids=frozenset((1, 2, 3)), carrier_go_ids=frozenset((1,)))

    def scene(self):
        return {"scene_name": "S", "objects": [
            {"id": "1", "name": "carrier", "parent_id": None, "components": []},
            {"id": "2", "name": "unit", "parent_id": "1", "components": [
                {"type": "LODGroup", "lods": [{"renderer_object_ids": ["3"]}]}]},
            {"id": "3", "name": "mesh", "parent_id": "2", "components": [{"type": "MeshRenderer"}]}]}

    def test_a_sound_scene_has_no_problems(self):
        self.assertEqual(SS.structure_problems(self.scene(), self.selection()), [])

    def test_each_defect_is_named(self):
        cases = {
            "dangling parent": (lambda s: s["objects"][2].update(parent_id="99"), "parent 99"),
            "missing object": (lambda s: s["objects"].pop(), "1 missing"),
            "extra object": (lambda s: s["objects"].append({"id": "7", "name": "x", "parent_id": None,
                                                            "components": []}), "1 extra"),
            "duplicate id": (lambda s: s["objects"].append(dict(s["objects"][0])), "duplicate"),
            "LOD outside": (lambda s: s["objects"][1]["components"][0]["lods"][0].update(renderer_object_ids=["42"]),
                            "LODGroup renderers"),
            "carrier components": (lambda s: s["objects"][0].update(components=[{"type": "MeshFilter"}]),
                                   "carrier has components"),
        }
        for label, (mutate, expected) in cases.items():
            scene = self.scene()
            mutate(scene)
            with self.subTest(label):
                problems = SS.structure_problems(scene, self.selection())
                self.assertTrue(any(expected in p for p in problems), problems)


# --------------------------------------------------------------------------
# ScopedFile
# --------------------------------------------------------------------------

def reader(path_id, kind, owner=None, script=None, file_id=0, broken=False):
    if kind != "MonoBehaviour":
        return NS(path_id=path_id, type=NS(name=kind))

    def head():
        if broken:
            raise ValueError("cannot parse")
        return NS(m_GameObject=None if owner is None else NS(file_id=file_id, path_id=owner))
    return NS(path_id=path_id, type=NS(name=kind), parse_monobehaviour_head=head, script=script)


def game_object(path_id, component_ids):
    return NS(path_id=path_id, type=NS(name="GameObject"),
              read_typetree=lambda: {"m_Component": [{"component": {"m_FileID": 0, "m_PathID": c}}
                                                     for c in component_ids]})


class ScopedFileTests(unittest.TestCase):
    BALLISTIC = B.SCRIPT_NAME

    def make_file(self):
        readers = [
            game_object(10, [20, 30]), reader(30, "BoxCollider"),                  # owner 10: collider + ballistic
            game_object(11, [21]),                                               # owner 11: ballistic only (orphan)
            game_object(12, [22, 32]), reader(32, "CapsuleCollider"),            # owner 12: other script + collider
            game_object(13, [23]),                                               # owner 13: other script only
            game_object(14, [24]),                                               # owner 14: out of scope
            reader(20, "MonoBehaviour", 10, self.BALLISTIC),
            reader(21, "MonoBehaviour", 11, self.BALLISTIC),
            reader(22, "MonoBehaviour", 12, "Some.Other"),
            reader(23, "MonoBehaviour", 13, "Some.Other"),
            reader(24, "MonoBehaviour", 14, self.BALLISTIC),
            reader(26, "MonoBehaviour", None, "Some.Other"),                     # no owner at all
            reader(40, "Material"),
        ]
        return NS(name="level38", objects={r.path_id: r for r in readers}, marker="forwarded")

    def scoped(self, owners=(10, 11, 12, 13), file=None):
        file = file or self.make_file()
        with patch.object(B, "script_name", side_effect=lambda r: r.script):
            return file, SS.ScopedFile(file, owners)

    def test_scans_cover_only_in_scope_owners_and_all_other_objects(self):
        _file, scoped = self.scoped()
        visible = set(scoped.objects.keys())
        self.assertEqual(visible, {10, 11, 12, 13, 14, 30, 32, 40, 20, 22, 23, 26})
        monobehaviours = {k for k, r in scoped.objects.items() if r.type.name == "MonoBehaviour"}
        # hidden: 24 (its owner 14 is out of scope) and 21 (an orphan); 26 has no owner and stays visible
        self.assertEqual(monobehaviours, {20, 22, 23, 26})
        self.assertEqual(len(scoped.objects), len(visible))
        self.assertEqual({r.path_id for r in scoped.objects.values()}, visible)
        self.assertEqual({k for k, _r in scoped.objects.items()}, visible)
        self.assertEqual(set(iter(scoped.objects)), visible)

    def test_orphan_ballistics_are_hidden_and_recorded(self):
        _file, scoped = self.scoped()
        self.assertEqual(scoped.orphans, [(11, 21)])
        self.assertNotIn(21, set(scoped.objects.keys()))
        # a ballistic collider whose owner has a collider stays; so does a non-ballistic script on a bare owner
        self.assertIn(20, set(scoped.objects.keys()))
        self.assertIn(23, set(scoped.objects.keys()))

    def test_orphans_are_sorted_by_owner_then_component(self):
        readers = [game_object(15, [126]), reader(126, "MonoBehaviour", 15, self.BALLISTIC),
                   game_object(9, [127]), reader(127, "MonoBehaviour", 9, self.BALLISTIC),
                   game_object(11, [21]), reader(21, "MonoBehaviour", 11, self.BALLISTIC)]
        file = NS(name="level38", objects={r.path_id: r for r in readers})
        _f, scoped = self.scoped(owners=(15, 9, 11), file=file)
        self.assertEqual(scoped.orphans, [(9, 127), (11, 21), (15, 126)])

    def test_lookups_by_id_still_reach_hidden_objects(self):
        file, scoped = self.scoped()
        self.assertIs(scoped.objects[21], file.objects[21])
        self.assertIs(scoped.objects[24], file.objects[24])
        self.assertIs(scoped.objects.get(24), file.objects[24])
        self.assertIsNone(scoped.objects.get(999))
        self.assertIn(24, scoped.objects)
        self.assertNotIn(24, set(scoped.objects))
        with self.assertRaises(KeyError):
            scoped.objects[999]

    def test_other_attributes_are_forwarded(self):
        _file, scoped = self.scoped()
        self.assertEqual((scoped.name, scoped.marker), ("level38", "forwarded"))
        with self.assertRaises(AttributeError):
            scoped.not_there
        blank = SS.ScopedFile.__new__(SS.ScopedFile)
        with self.assertRaises(AttributeError):
            blank.anything

    def test_with_every_owner_in_scope_the_scan_equals_the_plain_file(self):
        file = NS(name="level1", objects={10: game_object(10, [20, 30]), 30: reader(30, "BoxCollider"),
                                         20: reader(20, "MonoBehaviour", 10, self.BALLISTIC), 40: reader(40, "Mesh")})
        _f, scoped = self.scoped(owners=(10,), file=file)
        self.assertEqual(list(scoped.objects.items()), list(file.objects.items()))
        self.assertEqual(scoped.orphans, [])

    def test_owner_ids_may_be_strings_of_integers(self):
        _file, scoped = self.scoped(owners=("10", 11))
        monobehaviours = {k for k, r in scoped.objects.items() if r.type.name == "MonoBehaviour"}
        self.assertEqual(monobehaviours, {20, 26})

    def test_a_monobehaviour_without_an_owner_is_never_hidden(self):
        # the plain augmenters decide what an owner-less script means (they refuse it for their own classes),
        # so the scoped view must not turn that refusal into silence
        for owner in (None, 0):
            file = NS(name="level1", objects={r.path_id: r for r in (
                game_object(10, [20]), reader(20, "MonoBehaviour", 10, "Some.Other"),
                reader(26, "MonoBehaviour", owner, "Some.Other"))})
            _f, scoped = self.scoped(owners=(10,), file=file)
            self.assertEqual(set(scoped.objects.keys()), {10, 20, 26}, owner)
            self.assertEqual(scoped.orphans, [])

    def test_an_owner_in_another_file_is_a_hard_failure_naming_the_component(self):
        file = self.make_file()
        file.objects[25] = reader(25, "MonoBehaviour", 10, "Some.Other", file_id=1)
        with self.assertRaisesRegex(HardExtractError, r"MonoBehaviour 25: owner 10 lives in file 1"):
            self.scoped(file=file)

    def test_an_unreadable_head_is_a_hard_failure_naming_the_component(self):
        file = self.make_file()
        file.objects[27] = reader(27, "MonoBehaviour", 10, "Some.Other", broken=True)
        with self.assertRaisesRegex(HardExtractError, r"MonoBehaviour 27: unreadable head \(cannot parse\)"):
            self.scoped(file=file)

    def test_no_hidden_counter_remains_and_unknown_attributes_are_not_invented(self):
        _file, scoped = self.scoped()
        with self.assertRaises(AttributeError):
            scoped.hidden_out_of_scope

    def test_the_ballistic_augmenter_fails_on_an_orphan_without_the_proxy_and_passes_with_it(self):
        script = NS(path_id=99, read_typetree=lambda: {"m_ClassName": "BallisticCollider",
                                                       "m_Namespace": "EFT.Ballistics"})

        def ballistic(path_id, owner):
            return NS(path_id=path_id, type=NS(name="MonoBehaviour"), get_raw_data=lambda: raw(owner=owner),
                      parse_monobehaviour_head=lambda: NS(m_Script=script, m_GameObject=NS(file_id=0, path_id=owner)))
        tree10 = {"m_IsActive": True, "m_Component": [{"component": {"m_PathID": p}} for p in (20, 30)]}
        tree11 = {"m_IsActive": True, "m_Component": [{"component": {"m_PathID": 21}}]}
        file = NS(name="level100", objects={
            10: NS(path_id=10, type=NS(name="GameObject"), read_typetree=lambda: tree10),
            11: NS(path_id=11, type=NS(name="GameObject"), read_typetree=lambda: tree11),
            20: ballistic(20, 10), 21: ballistic(21, 11),
            30: NS(path_id=30, type=NS(name="BoxCollider"),
                   read_typetree=lambda: {"m_Enabled": True, "m_IsTrigger": False})})

        def scene():
            return {"scene_name": "S", "objects": [
                {"id": "10", "name": "with collider", "active": True, "parent_id": None,
                 "components": [{"type": "BoxCollider", "enabled": True, "is_trigger": False}]},
                {"id": "11", "name": "video_player_LOD0", "active": True, "parent_id": None, "components": []}]}
        with self.assertRaises(HardExtractError):
            B.augment_scene(scene(), file, contract())
        extracted = scene()
        scoped = SS.ScopedFile(file, [10, 11])
        self.assertEqual(B.augment_scene(extracted, scoped, contract()), 1)
        self.assertEqual(scoped.orphans, [(11, 21)])
        self.assertEqual(extracted["objects"][1]["components"], [])
        # an owner outside the scope is not an error, while the plain file still refuses the same scene
        outside = scene()
        outside["objects"].pop(1)
        self.assertEqual(B.augment_scene(outside, SS.ScopedFile(file, [10]), contract()), 1)
        with self.assertRaises(HardExtractError):
            B.augment_scene(deepcopy(outside), file, contract())


ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
HALL = "roots:SBG_Arena_Airpit_Main_hall/"
HALL_REGION = "region_roots:SBG_Arena_Airpit_Main_hall/"
LIGHT = "roots:SBG_Airpit_Light/OO/"
LIGHT_REGION = "region_roots:SBG_Airpit_Light/OO/"
# Selection pins of the two AirPit partial scenes against Arena 0.6.0 (level38 vs level324, level37 vs
# level325). The shop, projection and tier-2 group counts reproduce the design dry run; DOORS and the
# outline-box projection decals were added to it afterwards, and the two renderer-less spray_02 decal
# quads (PROJECTION/OUTDOOR/FIELD) came with the decal unit rule (+2 objects, +2 units, PROJECTION 97).
MAIN_HALL_PINS = {"objects": 7118, "units": 1446, "carriers": 85, "twins": 544, "dropped_script_owners": 0,
                  "go_ids_sha256": "d73986aa20321399d1bf679a21ff15680c23b472902ee5d250e2c41e9e582958",
                  "rules": {HALL + "OO/PROPS/INDOOR/SHOPS": (1148, 30), "roots:PROJECTION/INDOOR/SHOPS": (104, 92),
                            HALL + "SOO_LOD0/Airport_Shop_BS": (1, 0), HALL + "SOO_LOD0/DonB_Ramen": (1, 0),
                            HALL + "SOO_LOD0/LP_Shop": (1, 0), HALL + "SOO_LOD0/LP_Defile": (1, 0),
                            HALL + "SOO_LOD0/TH_Roll_door_01": (1, 0), HALL + "SOO_LOD0/TH_Roll_door_02": (1, 0),
                            HALL + "SOO_LOD0/TH_Roll_door_03": (1, 0), HALL + "SOO_LOD0/TH_Shop_indoor": (1, 0),
                            HALL + "SOO_LOD0/DOORS": (28, 0), "region_roots:PROJECTION": (97, 262),
                            HALL_REGION + "OO/PROPS/INDOOR/BACK": (24, 67), HALL_REGION + "OO/PROPS/INDOOR/FRONT": (22, 35),
                            HALL_REGION + "OO/PROPS/INDOOR/LEFT": (3, 10), HALL_REGION + "OO/PROPS/INDOOR/RIGHT": (4, 9),
                            HALL_REGION + "OO/PROPS/OUTDOOR/FIELD": (3, 7), HALL_REGION + "OO/PROPS/FLOOR_DAMAGE ": (5, 32)}}
LIGHT_PINS = {"objects": 962, "units": 187, "carriers": 26, "twins": 15, "dropped_script_owners": 0,
              "go_ids_sha256": "0bbb13dca87d5037d79c8cb8de4dca3767fed509da664d659802bab4f5b5742d",
              "rules": {LIGHT + "ChinaPlace*": (27, 0), LIGHT + "BurgerSpot*": (37, 0), LIGHT + "HELLFINGER": (34, 0),
                        LIGHT + "LouiPeeton*": (25, 2), LIGHT_REGION + "GLOBAL GI": (8, 3), "region_roots:NO_CULL": (6, 1),
                        LIGHT_REGION + "ShadowPlanes": (47, 8), "region_roots:PUDDLES": (1, 0),
                        LIGHT_REGION + "FogSheets": (2, 1)}}


@unittest.skipUnless(Path(ARENA_SOURCE, "globalgamemanagers").is_file(), "Arena source not reachable from this machine")
class LiveSelectionTests(unittest.TestCase):
    """The airpit.json selections against the installed Arena: every root resolves and the counts are the reviewed ones."""

    @classmethod
    def setUpClass(cls):
        import contextlib
        import io
        import extract_map as em
        from source_plan import PLAN_ROOT, load_plan, partial_scene_records
        plan = load_plan(PLAN_ROOT / "airpit.json")
        with contextlib.redirect_stderr(io.StringIO()):
            env, exact = em.load_environment(Path(ARENA_SOURCE), plan)
        graphs = cls.graphs = {}

        def graph(index):
            if index not in graphs:
                graphs[index] = SS.build_graph(env.files[exact[index]["level"]])
            return graphs[index]
        cls.selections = {r["name"]: SS.select_nodes(r, graph(r["index"]), graph(r["select"]["skip_twins_of"]))
                          for r in partial_scene_records(plan)}

    def check(self, name, pins):
        sel = self.selections[name]
        report = sel.report
        keys = ("objects", "units", "carriers", "dropped_script_owners", "go_ids_sha256")
        self.assertEqual({k: report[k] for k in keys}, {k: pins[k] for k in keys})
        self.assertEqual({k: (v["selected"], v["twin_skipped"]) for k, v in report["rules"].items()}, pins["rules"])
        self.assertEqual(len(sel.twins), pins["twins"])
        self.assertEqual(report["objects"], len(sel.node_tf_ids))
        self.assertEqual(report["objects"], len(sel.go_ids))

    def test_main_hall(self):
        self.check("Arena_Airpit_Main_hall", MAIN_HALL_PINS)

    def test_main_hall_takes_the_renderer_less_spray_decals_of_the_outline_box(self):
        graph = self.graphs[38]
        sel = self.selections["Arena_Airpit_Main_hall"]
        bare = sorted(graph.nodes[t].path for t in sel.unit_tf_ids if not graph.nodes[t].types)
        self.assertEqual(bare, ["PROJECTION/OUTDOOR/FIELD/spray_02 (59)", "PROJECTION/OUTDOOR/FIELD/spray_02 (62)"])
        for tf_id in sel.unit_tf_ids:
            if not graph.nodes[tf_id].types:
                self.assertEqual(graph.nodes[tf_id].scripts, tuple(SS.UNIT_SCRIPTS))

    def test_light(self):
        self.check("Arena_Airpit_Light", LIGHT_PINS)


class DigestTests(unittest.TestCase):
    def test_digest_is_order_invariant_and_content_sensitive(self):
        self.assertEqual(SS.go_ids_digest([3, 1, 2]), SS.go_ids_digest({2, 3, 1}))
        self.assertEqual(SS.go_ids_digest(["3", "1"]), SS.go_ids_digest([1, 3]))
        self.assertNotEqual(SS.go_ids_digest([1, 2]), SS.go_ids_digest([1, 3]))
        self.assertNotEqual(SS.go_ids_digest([12]), SS.go_ids_digest([1, 2]))


if __name__ == "__main__":
    unittest.main()
