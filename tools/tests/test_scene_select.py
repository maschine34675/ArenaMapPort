"""Partial-scene selection (tools/extract/scene_select.py) on synthetic levels; no Arena install needed.

A tiny builder produces the GameObject/Transform tables ``extract_equator.build_scene_graph``
returns, so the graph, the unit model, the carriers, the twin rule and the plan record are all
exercised without UnityPy. The last classes cover ScopedFile with fake UnityPy readers and the
ballistic augmenter that motivates it.
"""
from __future__ import annotations

from collections import Counter
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


def counters(selected, twin_skipped=0, excluded=0, descendants_added=0):
    """The four per-rule counters select_nodes reports."""
    return {"selected": selected, "twin_skipped": twin_skipped, "excluded": excluded,
            "descendants_added": descendants_added}


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


class ValidateNewKeysTests(unittest.TestCase):
    """`twin_descendants` and `exclude`, the keys of the second AirPit package."""

    def test_defaults_keep_the_old_behaviour(self):
        spec = SS.validate_select(select(roots=["A"]))
        self.assertEqual((spec.twin_descendants, spec.exclude), ("skip", ()))

    def test_twin_descendants_modes(self):
        for mode in ("skip", "import_missing"):
            spec = SS.validate_select(select(roots=["A"], skip_twins_of=324, twin_descendants=mode))
            self.assertEqual(spec.twin_descendants, mode)
        SS.validate_select(select(roots=["A"], twin_descendants="skip"))   # nothing to compare with is fine for skip
        for bad in ("import", "IMPORT_MISSING", "", None, True, 1, ["skip"]):
            with self.subTest(bad=bad), self.assertRaisesRegex(PlanError, "twin_descendants must be one of"):
                SS.validate_select(select(roots=["A"], skip_twins_of=324, twin_descendants=bad))

    def test_import_missing_needs_a_reference_scene(self):
        with self.assertRaisesRegex(PlanError, "needs skip_twins_of"):
            SS.validate_select(select(roots=["A"], twin_descendants="import_missing"))

    def test_exclude_entries(self):
        spec = SS.validate_select(select(roots=["A"], exclude=[
            {"path": "A/B", "reason": "moved copy"},
            {"path": "A/C", "at": [1, 2.5, -3], "reason": "replaced in 324"},
            {"path": "A/C", "at": [1, 2.5, 4], "reason": "same path elsewhere"}]))
        self.assertEqual([(e.path, e.at, e.reason) for e in spec.exclude],
                         [("A/B", None, "moved copy"), ("A/C", (1.0, 2.5, -3.0), "replaced in 324"),
                          ("A/C", (1.0, 2.5, 4.0), "same path elsewhere")])
        self.assertEqual(SS.validate_select(select(roots=["A"], exclude=[])).exclude, ())

    def test_malformed_exclude_entries_are_refused(self):
        good = {"path": "A/B", "reason": "r"}
        cases = {"not a list": {"path": "A/B", "reason": "r"}, "entry not an object": ["A/B"],
                 "no reason": [{"path": "A/B"}], "blank reason": [dict(good, reason=" ")],
                 "non-string reason": [dict(good, reason=3)], "no path": [{"reason": "r"}],
                 "bad path": [dict(good, path="A/")], "empty path": [dict(good, path="")],
                 "unknown key": [dict(good, why="x")], "short at": [dict(good, at=[1, 2])],
                 "text at": [dict(good, at="1,2,3")], "bool at": [dict(good, at=[True, 0, 0])],
                 "infinite at": [dict(good, at=[0, math.inf, 0])], "nan at": [dict(good, at=[0, 0, math.nan])]}
        for label, bad in cases.items():
            with self.subTest(label), self.assertRaises(PlanError):
                SS.validate_select(select(roots=["A"], exclude=bad))

    def test_duplicate_exclusions_are_refused(self):
        a = {"path": "A/B", "reason": "r"}
        with self.assertRaisesRegex(PlanError, "listed twice"):
            SS.validate_select(select(roots=["A"], exclude=[a, dict(a, reason="other text")]))
        at = dict(a, at=[1, 2, 3])
        with self.assertRaisesRegex(PlanError, "listed twice"):
            SS.validate_select(select(roots=["A"], exclude=[at, dict(at, at=[1.0, 2.0, 3.0])]))
        # the same path at another position, or once with and once without a position, is not a duplicate
        SS.validate_select(select(roots=["A"], exclude=[at, dict(at, at=[1, 2, 4]), a]))

    def test_the_keys_are_part_of_the_schema(self):
        self.assertLessEqual({"twin_descendants", "exclude"}, SS.SELECT_KEYS)


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

    def test_nodes_keep_their_component_ids_and_content_flag(self):
        level = Level()
        root = level.add("Root")
        lamp = level.add("Lamp", root, ("Transform", "Light", "MonoBehaviour"))
        decal = level.add("Spray", root, ("MonoBehaviour:" + DECAL_SCRIPT,))
        plain = level.add("Plain", root, ("MeshFilter",))
        graph = level.graph()
        self.assertEqual(graph.nodes[lamp].components, tuple(level.go_map[lamp - 1000]["components"]))
        self.assertEqual(len(graph.nodes[lamp].components), 3)
        self.assertEqual([graph.nodes[t].has_content for t in (root, lamp, decal, plain)], [False, True, True, False])

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
        self.assertEqual(report["rules"], {"roots:Root/Hall/Shop": counters(2)})
        self.assertEqual(report["go_ids_sha256"], SS.go_ids_digest(sel.go_ids))
        # nothing was imported or excluded: the new report keys are present and empty
        self.assertEqual((report["excluded"], report["exclude_entries"], report["twin_descendants_added"],
                          report["twin_descendant_paths"], report["twin_carriers"]), (0, 0, 0, [], 0))
        self.assertEqual((sel.twin_carrier_tf_ids, sel.twin_carrier_go_ids, sel.descendant_tf_ids, sel.excluded_tf_ids),
                         ((), frozenset(), (), ()))
        self.assertEqual(sel.scope_go_ids, sel.go_ids)

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
        self.assertEqual(sel.report["rules"], {"roots:Root/Near/A": counters(1), "region_roots:Root/Near": counters(1),
                                               "region_roots:Root/Far": counters(1)})
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
        self.assertEqual(sel.report["rules"], {"region_roots:Root": counters(1, 1)})
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
        self.assertEqual(sel.report["rules"]["roots:Root/Shop"], counters(2, 1))
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


def lamp(level, parent, name="Lamp", pos=(1.0, 0.0, 0.0), lights=(), comps=("LODGroup",)):
    """A LODGroup unit with a mesh child and one Light child per ``(name, local position)``; returns (root, lights)."""
    root = level.add(name, parent, comps, pos=pos)
    level.add(name + "_LOD0", root, ("MeshFilter", "MeshRenderer"))
    return root, [level.add(light, root, ("Light",), pos=at) for light, at in lights]


class TwinDescendantTests(unittest.TestCase):
    """``twin_descendants: import_missing``: the content a twin-skipped unit has and its reference twin lacks.

    Every case selects ``Root/Shop`` of a candidate level against a reference level; the candidate always carries a
    ``Keeper`` unit without a twin, so the selection is never empty."""

    def build(self, candidate_build, reference_build):
        candidate = Level()
        shop = candidate.add("Shop", candidate.add("Root"))
        out = candidate_build(candidate, shop)
        prop(candidate, "Keeper", shop, pos=(99.0, 0.0, 0.0))
        reference = Level()
        reference.add("Pad")  # keeps reference transform ids apart from the candidate's
        reference_build(reference, reference.add("Shop", reference.add("Root")))
        return candidate, reference, out

    def run_select(self, candidate_build, reference_build, mode="import_missing", **extra):
        candidate, reference, out = self.build(candidate_build, reference_build)
        graph, ref_graph = candidate.graph(), reference.graph()
        rec = record(roots=["Root/Shop"], skip_twins_of=324, twin_descendants=mode, **extra)
        return graph, ref_graph, SS.select_nodes(rec, graph, ref_graph), out

    def test_a_light_the_twin_lacks_is_imported_under_bare_twin_carriers(self):
        def reference(level, shop):
            lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0))])

        def candidate(level, shop):
            return lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0)), ("Spot (1)", (0.0, -2.0, 0.0))])
        graph, _ref, sel, (root, (spot, extra)) = self.run_select(candidate, reference)
        shop, base = root - 1, root - 1000
        self.assertEqual(set(sel.twins), {root})
        self.assertEqual(sel.descendant_tf_ids, (extra,))
        self.assertEqual(sel.twin_carrier_tf_ids, (root,))
        self.assertEqual(sel.twin_carrier_go_ids, {base})
        self.assertEqual(sel.report["twin_carrier_ids"], [str(base)])
        # the lamp unit itself is skipped: only the imported light, its twin carrier and the ordinary carriers remain
        keeper = [t for t in sel.unit_tf_ids if graph.nodes[t].name == "Keeper"]
        self.assertEqual(sel.unit_tf_ids, (keeper[0], extra))
        self.assertNotIn(root + 1, sel.node_tf_ids)           # the lamp's own mesh child is not emitted
        self.assertNotIn(spot, sel.node_tf_ids)                # the light the twin has stays out
        self.assertEqual(set(sel.node_tf_ids), {shop - 1, shop, root, extra, *range(keeper[0], keeper[0] + 3)})
        self.assertIn(base, sel.carrier_go_ids)
        self.assertEqual(sel.scope_go_ids, sel.go_ids - {base})
        self.assertEqual(graph.nodes[extra].pos, (1.0, -2.0, 0.0), "the imported light keeps its world pose")
        report = sel.report
        self.assertEqual((report["objects"], report["units"], report["carriers"], report["twin_carriers"]), (7, 2, 3, 1))
        self.assertEqual(report["rules"], {"roots:Root/Shop": counters(1, 1, 0, 1)})
        self.assertEqual((report["twin_descendants_added"], report["twin_descendant_paths"], report["excluded"]),
                         (1, ["Root/Shop/Lamp/Spot (1)"], 0))
        self.assertEqual(report["go_ids_sha256"], SS.go_ids_digest(sel.go_ids))

    def test_skip_is_the_default_and_imports_nothing(self):
        def reference(level, shop):
            lamp(level, shop)

        def candidate(level, shop):
            return lamp(level, shop, lights=[("Spot", (0.0, -2.0, 0.0))])
        for mode in ("skip", None):
            extra = {} if mode is None else {"twin_descendants": mode}
            with self.subTest(mode=mode):
                candidate_level, reference_level, (root, _lights) = self.build(candidate, reference)
                graph, ref_graph = candidate_level.graph(), reference_level.graph()
                rec = record(roots=["Root/Shop"], skip_twins_of=324)
                rec["select"].update(extra)
                sel = SS.select_nodes(rec, graph, ref_graph)
                self.assertEqual((sel.descendant_tf_ids, sel.twin_carrier_tf_ids), ((), ()))
                self.assertEqual(sel.report["twin_descendants_added"], 0)
                self.assertEqual(sel.report["rules"]["roots:Root/Shop"], counters(1, 1))
                self.assertNotIn(root, sel.node_tf_ids)

    def test_content_the_twin_carries_is_not_imported(self):
        def both(level, shop):
            lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0)), ("Spot (2)", (0.0, -2.0, 0.0))])
        _g, _r, sel, _ = self.run_select(both, both)
        self.assertEqual((sel.descendant_tf_ids, sel.twin_carrier_tf_ids), ((), ()))
        self.assertEqual(sel.report["rules"]["roots:Root/Shop"], counters(1, 1))

    def test_renumbered_copies_are_the_same_light(self):
        def reference(level, shop):
            lamp(level, shop, lights=[("Spot Light", (0.0, -1.0, 0.0)), ("Spot Light (1)", (0.0, -2.0, 0.0))])

        def candidate(level, shop):
            return lamp(level, shop, lights=[("Spot Light (5)", (0.0, -2.0, 0.0)), ("Spot Light (9)", (0.0, -1.0, 0.0))])
        _g, _r, sel, _ = self.run_select(candidate, reference)
        self.assertEqual(sel.descendant_tf_ids, ())

    def test_each_counterpart_is_used_once_and_the_earlier_path_takes_it(self):
        def reference(level, shop):
            lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0))])

        def candidate(level, shop):
            return lamp(level, shop, lights=[("Spot (1)", (0.0, -1.0, 0.0)), ("Spot", (0.0, -1.0, 0.0))])
        graph, _r, sel, (_root, (numbered, plain)) = self.run_select(candidate, reference)
        # "Spot" sorts before "Spot (1)" and takes the only counterpart, so the numbered one is the missing light
        self.assertEqual(sel.descendant_tf_ids, (numbered,))
        self.assertEqual((graph.nodes[numbered].name, graph.nodes[plain].name), ("Spot (1)", "Spot"))

    def test_the_reported_descendant_paths_are_sorted_as_strings_not_left_in_import_order(self):
        # the twin units are visited in (path, tf id) order, so 'Lamp' imports before 'Lamp (1)'; as strings
        # ' ' sorts before '/', so the sorted list names 'Lamp (1)/Spot' first
        def reference(level, shop):
            lamp(level, shop)
            lamp(level, shop, name="Lamp (1)", pos=(5.0, 0.0, 0.0))

        def candidate(level, shop):
            return (lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0))]),
                    lamp(level, shop, name="Lamp (1)", pos=(5.0, 0.0, 0.0), lights=[("Spot", (0.0, -1.0, 0.0))]))
        graph, _r, sel, ((first, (first_spot,)), (second, (second_spot,))) = self.run_select(candidate, reference)
        self.assertEqual(set(sel.twins), {first, second})
        self.assertEqual(set(sel.descendant_tf_ids), {first_spot, second_spot})
        self.assertEqual(sel.report["twin_descendant_paths"], ["Root/Shop/Lamp (1)/Spot", "Root/Shop/Lamp/Spot"])
        self.assertEqual([graph.nodes[t].path for t in sel.descendant_tf_ids],
                         sel.report["twin_descendant_paths"], "the selection's own order is the same string order")
        self.assertEqual(sel.report["twin_descendants_added"], 2)

    def test_what_makes_a_counterpart(self):
        base_at = (0.0, -1.0, 0.0)

        def reference(level, shop):
            lamp(level, shop, lights=[("Spot", base_at)])
        cases = (
            ("same light", {}, False),
            ("other name", {"name": "Flood"}, True),
            ("other component set", {"comps": ("Light", "ParticleSystem")}, True),
            ("inactive here, active there", {"active": False}, True),
            ("exactly on the tolerance", {"at": (0.02, -1.0, 0.0)}, False),
            ("per axis beyond the tolerance", {"at": (0.0, -1.0, 0.0201)}, True),
            ("moved far", {"at": (0.0, -1.5, 0.0)}, True))
        for label, change, missing in cases:
            def candidate(level, shop, change=change):
                root = level.add("Lamp", shop, ("LODGroup",), pos=(1.0, 0.0, 0.0))
                level.add("Lamp_LOD0", root, ("MeshFilter", "MeshRenderer"))
                level.add(change.get("name", "Spot"), root, change.get("comps", ("Light",)),
                          pos=change.get("at", base_at), active=change.get("active", True))
                return root
            with self.subTest(label):
                _g, _r, sel, _root = self.run_select(candidate, reference)
                self.assertEqual(len(sel.descendant_tf_ids), 1 if missing else 0)

    def test_inactivity_through_the_parent_chain_counts_like_the_own_flag(self):
        def reference(level, shop):
            root = level.add("Lamp", shop, ("LODGroup",), pos=(1.0, 0.0, 0.0), active=False)
            level.add("Spot", root, ("Light",), pos=(0.0, -1.0, 0.0))     # inactive through the lamp

        def candidate(level, shop):
            root = level.add("Lamp", shop, ("LODGroup",), pos=(1.0, 0.0, 0.0), active=False)
            level.add("Spot", root, ("Light",), pos=(0.0, -1.0, 0.0), active=False)   # inactive by its own flag too
            return root
        # the unit itself is a twin only when its effective activity matches, and so are its parts
        _g, _r, sel, _ = self.run_select(candidate, reference)
        self.assertEqual((sel.descendant_tf_ids, len(sel.twins)), ((), 1))

    def test_only_content_below_the_reference_twin_counts(self):
        def reference(level, shop):
            lamp(level, shop)
            # an identical light elsewhere in the reference level does not make the lamp's light a counterpart
            level.add("Spot", shop, ("Light",), pos=(1.0, -1.0, 0.0))

        def candidate(level, shop):
            return lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0))])
        graph, _r, sel, (_root, (spot,)) = self.run_select(candidate, reference)
        self.assertEqual(sel.descendant_tf_ids, (spot,))

    def test_a_missing_part_takes_its_subtree_and_the_chain_up_to_the_unit(self):
        def reference(level, shop):
            lamp(level, shop)

        def candidate(level, shop):
            root = level.add("Lamp", shop, ("LODGroup",), pos=(1.0, 0.0, 0.0))
            level.add("Lamp_LOD0", root, ("MeshFilter", "MeshRenderer"))
            arm = level.add("Arm", root, pos=(0.0, 1.0, 0.0))
            mount = level.add("Mount", arm, ("MeshRenderer",), pos=(0.0, 1.0, 0.0))
            bulb = level.add("Bulb", mount, ("Light",), pos=(0.0, 1.0, 0.0))     # content inside the missing part
            return root, arm, mount, bulb
        graph, _r, sel, (root, arm, mount, bulb) = self.run_select(candidate, reference)
        self.assertEqual(sel.descendant_tf_ids, (mount,), "the bulb belongs to the mount that carries it")
        self.assertEqual(set(sel.twin_carrier_tf_ids), {root, arm})
        self.assertIn(bulb, sel.node_tf_ids)
        self.assertEqual(graph.nodes[bulb].pos, (1.0, 3.0, 0.0))
        self.assertEqual(sel.report["twin_descendant_paths"], ["Root/Shop/Lamp/Arm/Mount"])
        self.assertEqual(sel.report["twin_carriers"], 2)

    def test_missing_content_inside_a_part_the_twin_has_is_found_on_its_own(self):
        def reference(level, shop):
            root = level.add("Lamp", shop, ("LODGroup",), pos=(1.0, 0.0, 0.0))
            level.add("Housing", root, ("MeshRenderer",), pos=(0.0, 1.0, 0.0))

        def candidate(level, shop):
            root = level.add("Lamp", shop, ("LODGroup",), pos=(1.0, 0.0, 0.0))
            housing = level.add("Housing", root, ("MeshRenderer",), pos=(0.0, 1.0, 0.0))
            bulb = level.add("Bulb", housing, ("Light",), pos=(0.0, 1.0, 0.0))
            return root, housing, bulb
        graph, _r, sel, (root, housing, bulb) = self.run_select(candidate, reference)
        self.assertEqual(sel.descendant_tf_ids, (bulb,))
        self.assertEqual(set(sel.twin_carrier_tf_ids), {root, housing}, "the housing the twin has is a bare stand-in")
        self.assertEqual(graph.nodes[bulb].pos, (1.0, 2.0, 0.0))

    def test_several_missing_parts_share_their_carriers(self):
        def reference(level, shop):
            lamp(level, shop)

        def candidate(level, shop):
            return lamp(level, shop, lights=[("A", (0.0, -1.0, 0.0)), ("B", (0.0, -2.0, 0.0)), ("C", (0.0, -3.0, 0.0))])
        _g, _r, sel, (root, kids) = self.run_select(candidate, reference)
        self.assertEqual(set(sel.descendant_tf_ids), set(kids))
        self.assertEqual(sel.twin_carrier_tf_ids, (root,))
        self.assertEqual(sel.report["twin_descendants_added"], 3)
        self.assertEqual(sel.report["twin_descendant_paths"], sorted(f"Root/Shop/Lamp/{n}" for n in "ABC"))
        self.assertEqual(sel.report["carriers"], 3)

    def test_a_twin_carrier_may_own_a_script_that_stays_out_of_the_augmenter_scope(self):
        def reference(level, shop):
            lamp(level, shop)

        def candidate(level, shop):
            root, (light,) = lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0))],
                                  comps=("LODGroup", "MonoBehaviour:Some.Other"))
            return root, light
        _g, _r, sel, (root, _light) = self.run_select(candidate, reference)
        self.assertIn(root - 1000, sel.go_ids)
        self.assertNotIn(root - 1000, sel.scope_go_ids)
        self.assertEqual(sel.report["dropped_script_owners"], 0)

    def test_a_selection_made_only_of_imported_descendants_is_not_empty(self):
        candidate = Level()
        shop = candidate.add("Shop", candidate.add("Root"))
        root, (spot,) = lamp(candidate, shop, lights=[("Spot", (0.0, -1.0, 0.0))])
        reference = Level()
        reference.add("Pad")
        lamp(reference, reference.add("Shop", reference.add("Root")))
        rec = record(roots=["Root/Shop"], skip_twins_of=324, twin_descendants="import_missing")
        sel = SS.select_nodes(rec, candidate.graph(), reference.graph())
        self.assertEqual(sel.unit_tf_ids, (spot,))
        with self.assertRaisesRegex(HardExtractError, "selects no unit"):
            SS.select_nodes(dict(rec, select=dict(rec["select"], twin_descendants="skip")),
                            candidate.graph(), reference.graph())

    def test_an_imported_part_counts_for_the_rule_that_claimed_its_unit(self):
        candidate = Level()
        root = candidate.add("Root")
        west, east = candidate.add("West", root), candidate.add("East", root)
        w_root, (w_spot,) = lamp(candidate, west, lights=[("Spot", (0.0, -1.0, 0.0))])
        e_root, (e_spot,) = lamp(candidate, east, pos=(50.0, 0.0, 0.0), lights=[("Spot", (0.0, -1.0, 0.0))])
        prop(candidate, "Keeper", east, pos=(99.0, 0.0, 0.0))
        reference = Level()
        reference.add("Pad")
        ref_root = reference.add("Root")
        lamp(reference, reference.add("West", ref_root))
        lamp(reference, reference.add("East", ref_root), pos=(50.0, 0.0, 0.0))
        rec = record(roots=["Root/West", "Root/East"], skip_twins_of=324, twin_descendants="import_missing")
        sel = SS.select_nodes(rec, candidate.graph(), reference.graph())
        self.assertEqual(sel.report["rules"], {"roots:Root/West": counters(0, 1, 0, 1),
                                               "roots:Root/East": counters(1, 1, 0, 1)})
        self.assertEqual(sel.report["units"], 3)
        self.assertEqual(set(sel.descendant_tf_ids), {w_spot, e_spot})

    def test_missing_descendants_function(self):
        candidate, reference = Level(), Level()
        root, (a, b) = lamp(candidate, candidate.add("Shop"), lights=[("A", (0.0, -1.0, 0.0)), ("B", (0.0, -2.0, 0.0))])
        ref_root, _ = lamp(reference, reference.add("Shop"), lights=[("A", (0.0, -1.0, 0.0))])
        graph, ref_graph = candidate.graph(), reference.graph()
        self.assertEqual(SS.missing_descendants(graph, root, ref_graph, ref_root), [b])
        self.assertEqual(SS.missing_descendants(graph, root, ref_graph, ref_root + 1), [a, b, root + 1],
                         "the reference side is the subtree of the given node only (the mesh child is missing too)")
        self.assertEqual(SS.missing_descendants(graph, a, ref_graph, ref_root), [], "a leaf has nothing below it")

    def test_results_do_not_depend_on_table_order(self):
        def reference(level, shop):
            lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0)), ("Spot (1)", (0.0, -1.0, 0.0))])

        def candidate(level, shop):
            lamp(level, shop, lights=[("Spot (3)", (0.0, -1.0, 0.0)), ("Spot (4)", (0.0, -1.0, 0.0)),
                                      ("Spot (5)", (0.0, -1.0, 0.0)), ("Spot (6)", (0.0, -2.0, 0.0))])
        cand, ref, _ = self.build(candidate, reference)
        rec = record(roots=["Root/Shop"], skip_twins_of=324, twin_descendants="import_missing")
        base = SS.select_nodes(rec, cand.graph(), ref.graph())
        self.assertEqual(base.report["twin_descendants_added"], 2)
        for seed in range(6):
            self.assertEqual(SS.select_nodes(rec, cand.graph(seed), ref.graph(seed + 100)), base, seed)

    def test_the_default_mode_leaves_the_old_selection_untouched(self):
        # same level, same reference: only the mode differs, and the difference is exactly the imported part
        def reference(level, shop):
            lamp(level, shop)

        def candidate(level, shop):
            return lamp(level, shop, lights=[("Spot", (0.0, -1.0, 0.0))])
        _g, _r, skip, _ = self.run_select(candidate, reference, mode="skip")
        _g, _r, imported, (_root, (spot,)) = self.run_select(candidate, reference)
        self.assertEqual(set(imported.unit_tf_ids) - set(skip.unit_tf_ids), {spot})
        self.assertLess(set(skip.node_tf_ids), set(imported.node_tf_ids))


class ExcludeTests(unittest.TestCase):
    """``exclude``: reviewed duplicates are removed after claiming, twin matching and twin descendants."""

    def setUp(self):
        level = self.level = Level()
        root = level.add("Root")
        props = level.add("Props", root)
        stack = level.add("Stack", props)
        variant = level.add("Variant", root)
        self.n = {"root": root, "props": props, "stack": stack, "variant": variant,
                  "a": prop(level, "A", props, pos=(1.0, 0.0, 0.0)),
                  "b": prop(level, "B", props, pos=(2.0, 0.0, 0.0)),
                  "c": prop(level, "C", stack, pos=(3.0, 0.0, 0.0)),
                  "d": prop(level, "D", stack, pos=(4.0, 0.0, 0.0)),
                  "crate1": prop(level, "Crate", variant, pos=(10.0, 0.0, 0.0)),
                  "crate2": prop(level, "Crate", variant, pos=(20.0, 0.0, 0.0)),
                  "crate3": prop(level, "Crate", variant, pos=(30.0, 0.0, 0.0))}
        self.graph = level.graph()

    def run_select(self, exclude, roots=("Root",), graph=None, reference=None, **extra):
        rec = record(roots=list(roots), exclude=exclude, **extra)
        return SS.select_nodes(rec, graph or self.graph, reference)

    def test_a_unit_root_is_dropped_with_its_subtree(self):
        base = SS.select_nodes(record(roots=["Root"]), self.graph)
        sel = self.run_select([{"path": "Root/Props/A", "reason": "dup"}])
        self.assertEqual(set(base.unit_tf_ids) - set(sel.unit_tf_ids), {self.n["a"]})
        self.assertEqual(sel.excluded_tf_ids, (self.n["a"],))
        self.assertFalse(set(range(self.n["a"], self.n["a"] + 3)) & set(sel.node_tf_ids))
        self.assertEqual(sel.report["objects"], base.report["objects"] - 3)
        self.assertEqual(sel.report["rules"], {"roots:Root": counters(7, 0, 1)})
        self.assertEqual((sel.report["excluded"], sel.report["exclude_entries"]), (1, 1))
        self.assertNotEqual(sel.report["go_ids_sha256"], base.report["go_ids_sha256"])

    def test_a_container_drops_every_unit_below_it_and_the_carriers_follow(self):
        n = self.n
        sel = self.run_select([{"path": "Root/Props/Stack", "reason": "a prefab instance"}])
        self.assertEqual(set(sel.excluded_tf_ids), {n["c"], n["d"]})
        self.assertNotIn(n["stack"], sel.node_tf_ids, "a container without surviving units is no carrier either")
        self.assertIn(n["props"], sel.node_tf_ids)
        self.assertEqual(sel.report["carriers"], 3)       # Root, Props, Variant
        self.assertEqual(sel.report["rules"]["roots:Root"], counters(7, 0, 2), "selected counts before the exclusion")
        self.assertEqual(sel.report["units"], 5)

    def test_a_container_that_loses_all_its_units_loses_its_carrier_chain(self):
        sel = self.run_select([{"path": "Root/Props/Stack", "reason": "x"}, {"path": "Root/Props/A", "reason": "x"},
                               {"path": "Root/Props/B", "reason": "x"}])
        self.assertNotIn(self.n["props"], sel.node_tf_ids)
        self.assertEqual(sel.report["carriers"], 2)       # Root, Variant

    def test_at_picks_one_of_several_nodes_that_share_a_path(self):
        n = self.n
        sel = self.run_select([{"path": "Root/Variant/Crate", "at": [20.0, 0.0, 0.0], "reason": "second crate"}])
        self.assertEqual(sel.excluded_tf_ids, (n["crate2"],))
        self.assertEqual({n["crate1"], n["crate3"]} - set(sel.unit_tf_ids), set())
        # the position matches within 0.05 m, euclidean
        for offset, found in (((0.0, 0.0, 0.049), True), ((0.03, 0.03, 0.0), True), ((0.0, 0.0, 0.0501), False),
                              ((0.04, 0.04, 0.0), False)):
            at = [20.0 + offset[0], offset[1], offset[2]]
            with self.subTest(offset=offset):
                if found:
                    self.run_select([{"path": "Root/Variant/Crate", "at": at, "reason": "x"}])
                else:
                    with self.assertRaisesRegex(HardExtractError, "no object within"):
                        self.run_select([{"path": "Root/Variant/Crate", "at": at, "reason": "x"}])

    def test_every_crate_can_be_named_by_its_position(self):
        sel = self.run_select([{"path": "Root/Variant/Crate", "at": [x, 0.0, 0.0], "reason": "x"}
                               for x in (10.0, 20.0, 30.0)])
        self.assertEqual(set(sel.excluded_tf_ids), {self.n["crate1"], self.n["crate2"], self.n["crate3"]})

    def test_a_shared_path_without_a_position_is_ambiguous(self):
        with self.assertRaisesRegex(HardExtractError, r"is ambiguous \(3 objects share the path\)"):
            self.run_select([{"path": "Root/Variant/Crate", "reason": "which one?"}])

    def test_a_unique_path_with_a_wrong_position_is_stale(self):
        with self.assertRaisesRegex(HardExtractError, "no object within"):
            self.run_select([{"path": "Root/Props/A", "at": [50.0, 0.0, 0.0], "reason": "moved by an update"}])
        self.run_select([{"path": "Root/Props/A", "at": [1.0, 0.0, 0.0], "reason": "still there"}])

    def test_two_objects_closer_than_the_tolerance_stay_ambiguous(self):
        level = Level()
        group = level.add("Group", level.add("Root"))
        prop(level, "Twin", group, pos=(1.0, 0.0, 0.0))
        prop(level, "Twin", group, pos=(1.02, 0.0, 0.0))
        prop(level, "Other", group, pos=(7.0, 0.0, 0.0))
        with self.assertRaisesRegex(HardExtractError, r"ambiguous \(2 objects within"):
            SS.select_nodes(record(roots=["Root"], exclude=[{"path": "Root/Group/Twin", "at": [1.01, 0.0, 0.0],
                                                             "reason": "x"}]), level.graph())

    def test_unresolved_paths_fail_closed(self):
        for path in ("Root/Props/Missing", "root/props", "Root/Props/A/A_LOD0/Missing"):
            with self.subTest(path=path), self.assertRaisesRegex(HardExtractError, "resolves to no object"):
                self.run_select([{"path": path, "reason": "stale"}])

    def test_a_path_inside_a_unit_is_refused(self):
        with self.assertRaisesRegex(HardExtractError, "points inside the unit"):
            self.run_select([{"path": "Root/Props/A/A_LOD0", "reason": "part of a prop"}])

    def test_an_entry_that_drops_nothing_is_stale(self):
        # outside every rule, a unit that is only a twin, and a container without selected units
        level = Level()
        root = level.add("Root")
        prop(level, "Chair", level.add("Shop", root), pos=(1.0, 0.0, 0.0))
        prop(level, "Table", level.add("Hall", root), pos=(5.0, 0.0, 0.0))
        prop(level, "Sofa", level.add("Keep", root), pos=(9.0, 0.0, 0.0))     # keeps the selection non-empty
        reference = Level()
        reference.add("Pad")
        prop(reference, "Chair", reference.add("Shop", reference.add("Root")), pos=(1.0, 0.0, 0.0))
        graph, ref_graph = level.graph(), reference.graph()
        for path, why in (("Root/Hall/Table", "outside every rule"), ("Root/Shop/Chair", "a twin"),
                          ("Root/Shop", "a container whose only unit is a twin")):
            rec = record(roots=["Root/Shop", "Root/Keep"], skip_twins_of=324, exclude=[{"path": path, "reason": why}])
            with self.subTest(why), self.assertRaisesRegex(HardExtractError, "drops no unit"):
                SS.select_nodes(rec, graph, ref_graph)

    def test_two_entries_for_one_object_are_refused(self):
        with self.assertRaisesRegex(HardExtractError, "names the same object"):
            self.run_select([{"path": "Root/Props/A", "reason": "x"},
                             {"path": "Root/Props/A", "at": [1.0, 0.0, 0.0], "reason": "y"}])

    def test_overlapping_entries_for_a_container_and_a_unit_below_it_are_fine(self):
        sel = self.run_select([{"path": "Root/Props/Stack", "reason": "x"}, {"path": "Root/Props/Stack/C", "reason": "y"}])
        self.assertEqual(set(sel.excluded_tf_ids), {self.n["c"], self.n["d"]})
        self.assertEqual(sel.report["excluded"], 2)

    def test_excluding_everything_selects_no_unit(self):
        with self.assertRaisesRegex(HardExtractError, "selects no unit .every selected unit is excluded"):
            self.run_select([{"path": "Root/Props", "reason": "x"}, {"path": "Root/Variant", "reason": "y"}])

    def test_the_exclusion_comes_after_twin_matching(self):
        # X sorts first and takes the reference twin; excluding X must not hand that twin to Y
        level = Level()
        group = level.add("Group", level.add("Root"))
        x = prop(level, "Cup", group, pos=(1.0, 0.0, 0.0))
        y = prop(level, "Cup (1)", group, pos=(1.0, 0.0, 0.0))
        prop(level, "Keeper", group, pos=(99.0, 0.0, 0.0))
        reference = Level()
        reference.add("Pad")
        prop(reference, "Cup", reference.add("Group", reference.add("Root")), pos=(1.0, 0.0, 0.0))
        graph, ref_graph = level.graph(), reference.graph()
        sel = SS.select_nodes(record(roots=["Root"], skip_twins_of=324,
                                     exclude=[{"path": "Root/Group/Cup (1)", "reason": "x"}]), graph, ref_graph)
        # Cup took the twin, so Cup (1) was a selected unit and the exclusion could drop it
        self.assertEqual(set(sel.twins), {x})
        self.assertEqual(sel.excluded_tf_ids, (y,))
        with self.assertRaisesRegex(HardExtractError, "drops no unit"):
            SS.select_nodes(record(roots=["Root"], skip_twins_of=324,
                                   exclude=[{"path": "Root/Group/Cup", "reason": "x"}]), graph, ref_graph)

    def test_the_exclusion_drops_imported_twin_descendants_too(self):
        def make():
            candidate, reference = Level(), Level()
            root = candidate.add("Root")
            holder = candidate.add("Holder", root)
            lamp_root, (spot,) = lamp(candidate, holder, lights=[("Spot", (0.0, -1.0, 0.0))])
            prop(candidate, "Keeper", root, pos=(99.0, 0.0, 0.0))
            reference.add("Pad")
            lamp(reference, reference.add("Holder", reference.add("Root")))
            return candidate.graph(), reference.graph(), holder, lamp_root, spot
        graph, ref_graph, holder, lamp_root, spot = make()
        base = SS.select_nodes(record(roots=["Root"], skip_twins_of=324, twin_descendants="import_missing"),
                               graph, ref_graph)
        self.assertEqual(base.descendant_tf_ids, (spot,))
        for path in ("Root/Holder", "Root/Holder/Lamp", "Root/Holder/Lamp/Spot"):
            with self.subTest(path=path):
                sel = SS.select_nodes(record(roots=["Root"], skip_twins_of=324, twin_descendants="import_missing",
                                             exclude=[{"path": path, "reason": "x"}]), graph, ref_graph)
                self.assertEqual(sel.excluded_tf_ids, (spot,))
                self.assertEqual(sel.descendant_tf_ids, (spot,), "it was imported, then dropped again")
                self.assertEqual((sel.twin_carrier_tf_ids, sel.twin_carrier_go_ids), ((), frozenset()))
                self.assertNotIn(lamp_root, sel.node_tf_ids, "no twin carrier without a surviving descendant")
                self.assertNotIn(holder, sel.node_tf_ids)
                self.assertNotIn(spot, sel.unit_tf_ids)
                report = sel.report
                self.assertEqual((report["excluded"], report["twin_descendants_added"], report["twin_descendant_paths"],
                                  report["twin_carrier_ids"]), (1, 1, ["Root/Holder/Lamp/Spot"], []))
                self.assertEqual(report["rules"]["roots:Root"], counters(1, 1, 1, 1))
                self.assertEqual(report["units"], 1)

    def test_an_exclusion_naming_a_unit_part_that_was_not_imported_is_refused(self):
        # without import_missing the light is inside the skipped lamp unit, so it cannot be named
        candidate, reference = Level(), Level()
        root = candidate.add("Root")
        lamp(candidate, candidate.add("Holder", root), lights=[("Spot", (0.0, -1.0, 0.0))])
        prop(candidate, "Keeper", root, pos=(99.0, 0.0, 0.0))
        reference.add("Pad")
        lamp(reference, reference.add("Holder", reference.add("Root")))
        rec = record(roots=["Root"], skip_twins_of=324, exclude=[{"path": "Root/Holder/Lamp/Spot", "reason": "x"}])
        with self.assertRaisesRegex(HardExtractError, "points inside the unit"):
            SS.select_nodes(rec, candidate.graph(), reference.graph())

    def test_each_rule_counts_its_own_exclusions(self):
        sel = self.run_select([{"path": "Root/Props/A", "reason": "x"}, {"path": "Root/Variant/Crate", "at": [10, 0, 0],
                                                                           "reason": "y"}],
                              roots=["Root/Props", "Root/Variant"])
        self.assertEqual(sel.report["rules"], {"roots:Root/Props": counters(4, 0, 1), "roots:Root/Variant": counters(3, 0, 1)})
        self.assertEqual(sel.report["units"], 5)
        self.assertEqual(sum(v["selected"] + v["descendants_added"] - v["excluded"] for v in sel.report["rules"].values()),
                         sel.report["units"])

    def test_an_excluded_imported_descendant_counts_for_the_rule_that_claimed_its_unit(self):
        # two rules, each claims one twin-skipped lamp that imports a Spot; naming a Spot moves only the counters of
        # the rule that claimed the lamp it sits in (the descendant is not a claimed unit root itself)
        candidate, reference = Level(), Level()
        root = candidate.add("Root")
        left, right = candidate.add("Left", root), candidate.add("Right", root)
        lamp(candidate, left, lights=[("Spot", (0.0, -1.0, 0.0))])
        prop(candidate, "KeeperL", left, pos=(97.0, 0.0, 0.0))
        lamp(candidate, right, pos=(5.0, 0.0, 0.0), lights=[("Spot", (0.0, -1.0, 0.0))])
        prop(candidate, "KeeperR", right, pos=(98.0, 0.0, 0.0))
        reference.add("Pad")
        reference_root = reference.add("Root")
        lamp(reference, reference.add("Left", reference_root))
        lamp(reference, reference.add("Right", reference_root), pos=(5.0, 0.0, 0.0))
        graph, ref_graph = candidate.graph(), reference.graph()
        left_key, right_key = "roots:Root/Left", "roots:Root/Right"
        spots = {"left": {"path": "Root/Left/Lamp/Spot", "reason": "x"},
                 "right": {"path": "Root/Right/Lamp/Spot", "reason": "y"}}
        for named, expected in ((("left",), (1, 0)), (("right",), (0, 1)), (("left", "right"), (1, 1)), ((), (0, 0))):
            with self.subTest(named=named):
                rec = record(roots=["Root/Left", "Root/Right"], skip_twins_of=324, twin_descendants="import_missing",
                             exclude=[spots[n] for n in named])
                if not named:
                    del rec["select"]["exclude"]
                report = SS.select_nodes(rec, graph, ref_graph).report
                self.assertEqual(report["rules"], {left_key: counters(1, 1, expected[0], 1),
                                                   right_key: counters(1, 1, expected[1], 1)})
                self.assertEqual((report["excluded"], report["twin_descendants_added"], report["units"]),
                                 (sum(expected), 2, 4 - sum(expected)))
                self.assertEqual(report["twin_descendant_paths"], ["Root/Left/Lamp/Spot", "Root/Right/Lamp/Spot"],
                                 "the paths report what was imported, before the exclusion")

    def test_exclude_entries_counts_the_entries_not_the_dropped_units(self):
        # one container entry drops C and D (two units), one unit entry drops A: 2 entries, 3 units
        sel = self.run_select([{"path": "Root/Props/Stack", "reason": "a prefab instance"},
                               {"path": "Root/Props/A", "reason": "dup"}])
        self.assertEqual(set(sel.excluded_tf_ids), {self.n["a"], self.n["c"], self.n["d"]})
        self.assertEqual((sel.report["excluded"], sel.report["exclude_entries"]), (3, 2))
        self.assertEqual(sel.report["rules"], {"roots:Root": counters(7, 0, 3)})
        # no entries at all: both counters are 0, and the key is reported nevertheless
        none = SS.select_nodes(record(roots=["Root"]), self.graph).report
        self.assertEqual((none["excluded"], none["exclude_entries"]), (0, 0))

    def test_results_do_not_depend_on_table_order(self):
        rec = record(roots=["Root"], exclude=[{"path": "Root/Props/Stack", "reason": "x"},
                                              {"path": "Root/Variant/Crate", "at": [30.0, 0.0, 0.0], "reason": "y"}])
        base = SS.select_nodes(rec, self.level.graph())
        for seed in range(6):
            self.assertEqual(SS.select_nodes(rec, self.level.graph(seed)), base, seed)


class StructureProblemsTests(unittest.TestCase):
    def selection(self, twin_carriers=()):
        return NS(go_ids=frozenset((1, 2, 3)), carrier_go_ids=frozenset((1, *twin_carriers)),
                  twin_carrier_go_ids=frozenset(twin_carriers))

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

    def test_twin_carriers_are_emitted_bare_like_every_other_carrier(self):
        # id 2 is a twin carrier here (its source has a LODGroup); extract_map emits it without components
        scene = self.scene()
        scene["objects"][1]["components"] = []
        scene["objects"][1]["name"] = "stand-in"
        scene["objects"][2]["components"] = [{"type": "MeshRenderer"}]
        self.assertEqual(SS.structure_problems(scene, self.selection(twin_carriers=(2,))), [])
        # a component that survived on it is named as a twin carrier defect, and only there
        scene["objects"][1]["components"] = [{"type": "LODGroup", "lods": [{"renderer_object_ids": ["3"]}]}]
        problems = SS.structure_problems(scene, self.selection(twin_carriers=(2,)))
        self.assertEqual(len(problems), 1)
        self.assertIn("twin carrier has components ['LODGroup']", problems[0])
        # a LODGroup on a unit that is not a carrier stays fine
        self.assertEqual(SS.structure_problems(scene, self.selection()), [])

    def test_a_twin_carrier_must_be_a_carrier(self):
        selection = NS(go_ids=frozenset((1, 2, 3)), carrier_go_ids=frozenset((1,)), twin_carrier_go_ids=frozenset((2,)))
        problems = SS.structure_problems(self.scene(), selection)
        self.assertTrue(any("are not carriers" in p for p in problems), problems)


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

# The select records of package v7 (phase 1, before ``twin_descendants`` and ``exclude`` existed). They stay here
# as the regression for records without the new keys: they must keep reproducing the v7 selections whatever
# tools/extract/plans/airpit.json holds today.
V7_MAIN_HALL_SELECT = {
    "schema": 1,
    "reason": "phase 1 record (package v7)",
    "skip_twins_of": 324,
    "roots": [
        "SBG_Arena_Airpit_Main_hall/OO/PROPS/INDOOR/SHOPS",
        "PROJECTION/INDOOR/SHOPS",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/Airport_Shop_BS",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/DonB_Ramen",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/LP_Shop",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/LP_Defile",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/TH_Roll_door_01",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/TH_Roll_door_02",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/TH_Roll_door_03",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/TH_Shop_indoor",
        "SBG_Arena_Airpit_Main_hall/SOO_LOD0/DOORS",
    ],
    "region_roots": [
        {"path": "PROJECTION", "regions": ["outline box"]},
        {"path": "SBG_Arena_Airpit_Main_hall/OO/PROPS/INDOOR/BACK", "regions": ["NW shop", "NE shop", "SE shop", "SW shop"]},
        {"path": "SBG_Arena_Airpit_Main_hall/OO/PROPS/INDOOR/FRONT", "regions": ["NW shop", "NE shop", "SE shop", "SW shop"]},
        {"path": "SBG_Arena_Airpit_Main_hall/OO/PROPS/INDOOR/LEFT", "regions": ["NW shop", "NE shop", "SE shop", "SW shop"]},
        {"path": "SBG_Arena_Airpit_Main_hall/OO/PROPS/INDOOR/RIGHT", "regions": ["NW shop", "NE shop", "SE shop", "SW shop"]},
        {"path": "SBG_Arena_Airpit_Main_hall/OO/PROPS/OUTDOOR/FIELD", "regions": ["NW shop", "NE shop", "SE shop", "SW shop"]},
        {"path": "SBG_Arena_Airpit_Main_hall/OO/PROPS/FLOOR_DAMAGE ", "regions": ["NW shop", "NE shop", "SE shop", "SW shop"]},
    ],
    "regions": [
        {"name": "outline box", "min": [-57.0, -5.0, -83.0], "max": [37.0, 12.0, 70.0]},
        {"name": "NW shop", "min": [-57.5, -1.5, 13.5], "max": [-33.5, 9.0, 44.5]},
        {"name": "NE shop", "min": [11.5, -1.5, 6.5], "max": [34.5, 9.0, 44.5]},
        {"name": "SE shop", "min": [9.5, -1.5, -45.5], "max": [33.5, 9.0, -14.5]},
        {"name": "SW shop", "min": [-57.5, -1.5, -41.5], "max": [-26.5, 9.0, -7.5]},
    ],
}

V7_LIGHT_SELECT = {
    "schema": 1,
    "reason": "phase 1 record (package v7)",
    "skip_twins_of": 325,
    "roots": [
        "SBG_Airpit_Light/OO/ChinaPlace*",
        "SBG_Airpit_Light/OO/BurgerSpot*",
        "SBG_Airpit_Light/OO/HELLFINGER",
        "SBG_Airpit_Light/OO/LouiPeeton*",
    ],
    "region_roots": [
        "SBG_Airpit_Light/OO/GLOBAL GI",
        "NO_CULL",
        "SBG_Airpit_Light/OO/ShadowPlanes",
        "PUDDLES",
        "SBG_Airpit_Light/OO/FogSheets",
    ],
    "regions": [
        {"name": "NW shop", "min": [-57.5, -1.5, 13.5], "max": [-33.5, 9.0, 44.5]},
        {"name": "NE shop", "min": [11.5, -1.5, 6.5], "max": [34.5, 9.0, 44.5]},
        {"name": "SE shop", "min": [9.5, -1.5, -45.5], "max": [33.5, 9.0, -14.5]},
        {"name": "SW shop", "min": [-57.5, -1.5, -41.5], "max": [-26.5, 9.0, -7.5]},
    ],
}

V7_RECORDS = {38: ("Arena_Airpit_Main_hall", V7_MAIN_HALL_SELECT), 37: ("Arena_Airpit_Light", V7_LIGHT_SELECT)}


def pin_rules(**rules):
    """{rule key: (selected, twin_skipped[, excluded, descendants_added])} padded to four counters."""
    return {key: tuple(values) + (0,) * (4 - len(values)) for key, values in rules.items()}


# Selection pins of the AirPit partial scenes against Arena 0.6.0 (level38 vs level324, level37 vs level325).
# V7: the shop, projection and tier-2 group counts reproduce the design dry run; DOORS and the outline-box
# projection decals were added to it afterwards, and the two renderer-less spray_02 decal quads
# (PROJECTION/OUTDOOR/FIELD) came with the decal unit rule (+2 objects, +2 units, PROJECTION 97).
MAIN_HALL_V7_PINS = {"objects": 7118, "units": 1446, "carriers": 85, "twins": 544, "dropped_script_owners": 0,
                     "go_ids_sha256": "d73986aa20321399d1bf679a21ff15680c23b472902ee5d250e2c41e9e582958",
                     "rules": pin_rules(**{HALL + "OO/PROPS/INDOOR/SHOPS": (1148, 30), "roots:PROJECTION/INDOOR/SHOPS": (104, 92),
                                           HALL + "SOO_LOD0/Airport_Shop_BS": (1, 0), HALL + "SOO_LOD0/DonB_Ramen": (1, 0),
                                           HALL + "SOO_LOD0/LP_Shop": (1, 0), HALL + "SOO_LOD0/LP_Defile": (1, 0),
                                           HALL + "SOO_LOD0/TH_Roll_door_01": (1, 0), HALL + "SOO_LOD0/TH_Roll_door_02": (1, 0),
                                           HALL + "SOO_LOD0/TH_Roll_door_03": (1, 0), HALL + "SOO_LOD0/TH_Shop_indoor": (1, 0),
                                           HALL + "SOO_LOD0/DOORS": (28, 0), "region_roots:PROJECTION": (97, 262),
                                           HALL_REGION + "OO/PROPS/INDOOR/BACK": (24, 67), HALL_REGION + "OO/PROPS/INDOOR/FRONT": (22, 35),
                                           HALL_REGION + "OO/PROPS/INDOOR/LEFT": (3, 10), HALL_REGION + "OO/PROPS/INDOOR/RIGHT": (4, 9),
                                           HALL_REGION + "OO/PROPS/OUTDOOR/FIELD": (3, 7), HALL_REGION + "OO/PROPS/FLOOR_DAMAGE ": (5, 32)})}
LIGHT_V7_PINS = {"objects": 962, "units": 187, "carriers": 26, "twins": 15, "dropped_script_owners": 0,
                 "go_ids_sha256": "0bbb13dca87d5037d79c8cb8de4dca3767fed509da664d659802bab4f5b5742d",
                 "rules": pin_rules(**{LIGHT + "ChinaPlace*": (27, 0), LIGHT + "BurgerSpot*": (37, 0), LIGHT + "HELLFINGER": (34, 0),
                                       LIGHT + "LouiPeeton*": (25, 2), LIGHT_REGION + "GLOBAL GI": (8, 3), "region_roots:NO_CULL": (6, 1),
                                       LIGHT_REGION + "ShadowPlanes": (47, 8), "region_roots:PUDDLES": (1, 0),
                                       LIGHT_REGION + "FogSheets": (2, 1)})}
# V8 (package v8): the whole recoverable decoration of level38 (decoration box x -60..40, y -5..20, z -85..91,
# 208 exclusions, twin descendants imported) and of level37. Counters are (selected, twin_skipped, excluded,
# descendants_added) with ``selected`` counted before the later steps, so selected + descendants_added - excluded
# over all rules is the unit count.
MAIN_HALL_V8_PINS = {"objects": 13006, "units": 3547, "carriers": 245, "twins": 3212, "dropped_script_owners": 0,
                     "go_ids_sha256": "d63155ffe9936e40e836c4eb802ad38f0ef6b3caa11f954bda07dc45f086507d",
                     "twin_carriers": 4, "excluded": 632, "exclude_entries": 208, "twin_descendants_added": 4,
                     "rules": pin_rules(**{HALL + "OO/PROPS/INDOOR/SHOPS": (1148, 30), "roots:PROJECTION/INDOOR/SHOPS": (104, 92),
                                           HALL + "SOO_LOD0/Airport_Shop_BS": (1, 0), HALL + "SOO_LOD0/DonB_Ramen": (1, 0),
                                           HALL + "SOO_LOD0/LP_Shop": (1, 0), HALL + "SOO_LOD0/LP_Defile": (1, 0),
                                           HALL + "SOO_LOD0/TH_Roll_door_01": (1, 0), HALL + "SOO_LOD0/TH_Roll_door_02": (1, 0),
                                           HALL + "SOO_LOD0/TH_Roll_door_03": (1, 0), HALL + "SOO_LOD0/TH_Shop_indoor": (1, 0),
                                           HALL + "SOO_LOD0/DOORS": (28, 0), "region_roots:PROJECTION": (123, 263),
                                           HALL_REGION + "OO/PROPS": (1210, 1251, 161, 4), "region_roots:VARIANT": (1534, 1555, 455),
                                           "region_roots:PROPSnoCulling": (20, 21, 16)})}
LIGHT_V8_PINS = {"objects": 1538, "units": 298, "carriers": 37, "twins": 268, "dropped_script_owners": 0,
                 "go_ids_sha256": "153847417c4427b3e6882b11a58a66932aeef63df6bb9857c76b976c1857c022",
                 "twin_carriers": 5, "excluded": 9, "exclude_entries": 9, "twin_descendants_added": 22,
                 "rules": pin_rules(**{LIGHT + "ChinaPlace*": (27, 0), LIGHT + "BurgerSpot*": (37, 0), LIGHT + "HELLFINGER": (34, 0),
                                       LIGHT + "LouiPeeton*": (25, 2), LIGHT_REGION + "GLOBAL GI": (19, 88), "region_roots:NO_CULL": (12, 7, 2, 2),
                                       LIGHT_REGION + "ShadowPlanes": (73, 47, 2, 2), "region_roots:PUDDLES": (1, 14),
                                       LIGHT_REGION + "FogSheets": (2, 7), LIGHT_REGION + "Airport_lamp_02": (45, 0),
                                       LIGHT_REGION + "LAMPS_GENERIC*": (4, 0), LIGHT_REGION + "AIRBUS": (4, 10, 4, 4),
                                       LIGHT_REGION + "AIRPORT LAMPS": (0, 26, 1, 10), LIGHT_REGION + "BACK_AIRPORT_LAMPS": (0, 36, 0, 2),
                                       LIGHT_REGION + "LUM_LAMPS": (1, 22, 0, 2), LIGHT_REGION + "Point Light": (0, 1),
                                       LIGHT_REGION + "chemlight_emissive": (1, 2), LIGHT_REGION + "Lamps_ceiling - DISABLED": (0, 6)})}
# What the 324 / 325 twins lack (phase 1b census): the 20 Lights and 2 ShadowPlaneBox renderer children below
# twin-skipped lamp units of level37, and the car-shadow decals of four baggage carts of level38.
LIGHT_DESC = [
    "NO_CULL/Old_Bunker_Light_cookie (8)/BaseLight",
    "NO_CULL/Old_Bunker_Light_cookie (8)/MainLight",
    "SBG_Airpit_Light/OO/AIRBUS/Old_Bunker_Light_cookie (6)/BaseLight",
    "SBG_Airpit_Light/OO/AIRBUS/Old_Bunker_Light_cookie (6)/MainLight",
    "SBG_Airpit_Light/OO/AIRBUS/Old_Bunker_Light_cookie (9)/BaseLight ",
    "SBG_Airpit_Light/OO/AIRBUS/Old_Bunker_Light_cookie (9)/MainLight",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (72)/Spot Light (2)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (11)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (12)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (13)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (15)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (4)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (6)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (7)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (8)",
    "SBG_Airpit_Light/OO/AIRPORT LAMPS/Airport_lamp (79)/Spot Light (9)",
    "SBG_Airpit_Light/OO/BACK_AIRPORT_LAMPS/Airport_lamp (102)/Spot Light (1)",
    "SBG_Airpit_Light/OO/BACK_AIRPORT_LAMPS/Airport_lamp (103)/Spot Light (1)",
    "SBG_Airpit_Light/OO/LUM_LAMPS/lab_lamp_lam_ceiling_destroyable (3)/Spot Light",
    "SBG_Airpit_Light/OO/LUM_LAMPS/lab_lamp_lam_ceiling_destroyable (3)/Spot Light",
    "SBG_Airpit_Light/OO/ShadowPlanes/ShadowPlaneBox (125)/Plane_LOD0",
    "SBG_Airpit_Light/OO/ShadowPlanes/ShadowPlaneBox (140)/Plane_LOD0",
]
HALL_DESC = [
    "SBG_Arena_Airpit_Main_hall/OO/PROPS/OUTDOOR/FIELD/Baggage_cart_02_D (10)/decal_carshadow (4)",
    "SBG_Arena_Airpit_Main_hall/OO/PROPS/OUTDOOR/FIELD/Baggage_cart_02_D (14)/decal_carshadow (5)",
    "SBG_Arena_Airpit_Main_hall/OO/PROPS/OUTDOOR/FIELD/Baggage_cart_02_D (15)/decal_carshadow (7)",
    "SBG_Arena_Airpit_Main_hall/OO/PROPS/OUTDOOR/FIELD/Baggage_cart_02_D (16)/decal_carshadow (7)",
]


_LIVE = {}


def arena_graph():
    """graph(index) over the installed Arena, built once and shared by every live test class."""
    if not _LIVE:
        import contextlib
        import io
        import extract_map as em
        from source_plan import PLAN_ROOT, load_plan
        plan = load_plan(PLAN_ROOT / "airpit.json")
        with contextlib.redirect_stderr(io.StringIO()):
            env, exact = em.load_environment(Path(ARENA_SOURCE), plan)
        graphs = {}

        def graph(index):
            if index not in graphs:
                graphs[index] = SS.build_graph(env.files[exact[index]["level"]])
            return graphs[index]
        _LIVE["graph"] = graph
    return _LIVE["graph"]


class LiveSelectionCase(unittest.TestCase):
    def check(self, sel, pins):
        report = sel.report
        keys = ("objects", "units", "carriers", "dropped_script_owners", "go_ids_sha256")
        self.assertEqual({k: report[k] for k in keys}, {k: pins[k] for k in keys})
        self.assertEqual({k: (v["selected"], v["twin_skipped"], v["excluded"], v["descendants_added"])
                          for k, v in report["rules"].items()}, pins["rules"])
        self.assertEqual(len(sel.twins), pins["twins"])
        self.assertEqual(report["objects"], len(sel.node_tf_ids))
        self.assertEqual(report["objects"], len(sel.go_ids))
        self.assertEqual(report["units"], len(sel.unit_tf_ids))
        self.assertEqual(report["units"], sum(v["selected"] + v["descendants_added"] - v["excluded"]
                                              for v in report["rules"].values()))
        self.assertEqual(report["excluded"], sum(v["excluded"] for v in report["rules"].values()))
        self.assertEqual(report["twin_descendants_added"], sum(v["descendants_added"] for v in report["rules"].values()))
        self.assertEqual(len(report["twin_carrier_ids"]), report["twin_carriers"])
        self.assertEqual(report["twin_carrier_ids"], sorted(report["twin_carrier_ids"], key=int))
        for key in ("twin_carriers", "excluded", "exclude_entries", "twin_descendants_added"):
            self.assertEqual(report[key], pins.get(key, 0), key)
        self.assertEqual(len(report["twin_descendant_paths"]), report["twin_descendants_added"])
        self.assertEqual(len(sel.twin_carrier_tf_ids), report["twin_carriers"])


@unittest.skipUnless(Path(ARENA_SOURCE, "globalgamemanagers").is_file(), "Arena source not reachable from this machine")
class LiveLegacyRecordTests(LiveSelectionCase):
    """Records without ``twin_descendants`` / ``exclude`` still select exactly what the v7 package holds."""

    @classmethod
    def setUpClass(cls):
        graph = arena_graph()
        cls.selections = {name: SS.select_nodes({"name": name, "index": index, "select": select},
                                                graph(index), graph(select["skip_twins_of"]))
                          for index, (name, select) in V7_RECORDS.items()}

    def test_main_hall(self):
        self.check(self.selections["Arena_Airpit_Main_hall"], MAIN_HALL_V7_PINS)

    def test_light(self):
        self.check(self.selections["Arena_Airpit_Light"], LIGHT_V7_PINS)

    def test_the_new_keys_are_empty(self):
        for sel in self.selections.values():
            self.assertEqual((sel.twin_carrier_tf_ids, sel.descendant_tf_ids, sel.excluded_tf_ids,
                              sel.scope_go_ids == sel.go_ids), ((), (), (), True))


@unittest.skipUnless(Path(ARENA_SOURCE, "globalgamemanagers").is_file(), "Arena source not reachable from this machine")
class LiveSelectionTests(LiveSelectionCase):
    """The airpit.json selections against the installed Arena: every root resolves and the counts are the reviewed ones.

    ``plan_path`` can name a candidate plan to run through the same checks. The plan holds either the v7 records (no
    ``exclude`` in the Main_hall select) or the v8 ones; the checks of the other generation skip."""
    plan_path = None

    @classmethod
    def setUpClass(cls):
        from source_plan import PLAN_ROOT, load_plan, partial_scene_records
        graph = arena_graph()
        cls.graph = staticmethod(graph)
        plan = load_plan(cls.plan_path or PLAN_ROOT / "airpit.json")
        cls.records = {r["name"]: r for r in partial_scene_records(plan)}
        cls.selections = {name: SS.select_nodes(r, graph(r["index"]), graph(r["select"]["skip_twins_of"]))
                          for name, r in cls.records.items()}
        cls.v8 = "exclude" in cls.records["Arena_Airpit_Main_hall"]["select"]
        cls.legacy = {name: SS.select_nodes({"name": name, "index": index, "select": select},
                                            graph(index), graph(select["skip_twins_of"]))
                      for index, (name, select) in V7_RECORDS.items()}

    def pins(self, v7, v8):
        return v8 if self.v8 else v7

    def need_v8(self):
        if not self.v8:
            self.skipTest("the plan still holds the v7 records")

    def test_main_hall(self):
        self.check(self.selections["Arena_Airpit_Main_hall"], self.pins(MAIN_HALL_V7_PINS, MAIN_HALL_V8_PINS))

    def test_main_hall_takes_the_renderer_less_spray_decals_of_the_outline_box(self):
        graph = self.graph(38)
        sel = self.selections["Arena_Airpit_Main_hall"]
        bare = sorted(graph.nodes[t].path for t in sel.unit_tf_ids if not graph.nodes[t].types)
        self.assertEqual(bare, ["PROJECTION/OUTDOOR/FIELD/spray_02 (59)", "PROJECTION/OUTDOOR/FIELD/spray_02 (62)"])
        for tf_id in sel.unit_tf_ids:
            if not graph.nodes[tf_id].types:
                self.assertEqual(graph.nodes[tf_id].scripts, tuple(SS.UNIT_SCRIPTS))

    def test_light(self):
        self.check(self.selections["Arena_Airpit_Light"], self.pins(LIGHT_V7_PINS, LIGHT_V8_PINS))

    def test_the_v7_selection_is_a_subset_of_the_plan_selection(self):
        for name, sel in self.selections.items():
            old = self.legacy[name]
            with self.subTest(name):
                self.assertLessEqual(set(old.unit_tf_ids), set(sel.unit_tf_ids))
                self.assertLessEqual(set(old.node_tf_ids), set(sel.node_tf_ids))

    def test_every_twin_carrier_is_script_free_so_the_decal_refresh_sees_what_extraction_saw(self):
        # extract_decals.refresh scopes the augmenters to the object ids of the scene JSON, twin carriers
        # included; that equals the extraction scope only while no twin carrier owns a MonoBehaviour
        for name, sel in self.selections.items():
            graph = self.graph(self.records[name]["index"])
            for tf_id in sel.twin_carrier_tf_ids:
                with self.subTest(graph.nodes[tf_id].path):
                    self.assertEqual(graph.nodes[tf_id].scripts, ())

    def test_v8_light_imports_the_lights_and_shadow_plane_parts_its_twins_lack(self):
        self.need_v8()
        sel = self.selections["Arena_Airpit_Light"]
        graph = self.graph(37)
        self.assertEqual(sel.report["twin_descendant_paths"], LIGHT_DESC)
        self.assertEqual(Counter(graph.nodes[t].types for t in sel.descendant_tf_ids),
                         {("Light",): 20, ("MeshFilter", "MeshRenderer"): 2})
        # Nine of the 22 imports are excluded again (level325 replaced or disabled them on purpose); the twin
        # carriers are recomputed from the surviving descendants, so an excluded import leaves no stand-in behind.
        excluded = set(sel.excluded_tf_ids)
        self.assertEqual(len(excluded & set(sel.descendant_tf_ids)), 9)
        self.assertEqual(Counter(graph.nodes[t].types for t in excluded), {("Light",): 7, ("MeshFilter", "MeshRenderer"): 2})
        kept = [t for t in sel.descendant_tf_ids if t not in excluded]
        self.assertEqual(set(sel.twin_carrier_tf_ids), {graph.nodes[t].unit for t in kept})
        self.assertLessEqual(set(sel.twin_carrier_tf_ids), set(sel.twins))

    def test_v8_main_hall_imports_only_the_car_shadow_decals_its_twins_lack(self):
        self.need_v8()
        sel = self.selections["Arena_Airpit_Main_hall"]
        self.assertEqual(sel.report["twin_descendant_paths"], HALL_DESC)
        self.assertLessEqual(sel.report["twin_descendants_added"], 300, "the guard of the import: small parts only")

    def test_v8_main_hall_exclusions_resolve_and_drop_the_census_units(self):
        self.need_v8()
        record = self.records["Arena_Airpit_Main_hall"]
        entries = record["select"]["exclude"]
        self.assertEqual(len(entries), 208)
        self.assertTrue(all(e["reason"] for e in entries))
        sel = self.selections["Arena_Airpit_Main_hall"]
        self.assertEqual((sel.report["excluded"], sel.report["exclude_entries"]), (632, 208))
        # the census run of phase 1b cut the decoration box at z 90 and imported no twin descendants
        census = deepcopy(record)
        census["select"]["twin_descendants"] = "skip"
        for region in census["select"]["regions"]:
            region["max"][2] = 90.0
        sel = SS.select_nodes(census, self.graph(38), self.graph(record["select"]["skip_twins_of"]))
        self.assertEqual({k: sel.report[k] for k in ("objects", "units", "carriers", "excluded")},
                         {"objects": 12997, "units": 3542, "carriers": 241, "excluded": 632})
        self.assertEqual(sel.report["go_ids_sha256"], "5648ec2a7805e8d1081943de6e8c8f5425f5d7ab881e4ea944887ff5b09da92a")

    def test_v8_exclusions_are_the_only_difference_to_the_unexcluded_record(self):
        self.need_v8()
        record = deepcopy(self.records["Arena_Airpit_Main_hall"])
        record["select"]["exclude"] = []
        plain = SS.select_nodes(record, self.graph(38), self.graph(324))
        sel = self.selections["Arena_Airpit_Main_hall"]
        self.assertEqual(set(plain.unit_tf_ids) - set(sel.unit_tf_ids), set(sel.excluded_tf_ids))
        self.assertEqual(set(sel.unit_tf_ids) - set(plain.unit_tf_ids), set())
        self.assertEqual(len(sel.excluded_tf_ids), 632)


class DigestTests(unittest.TestCase):
    def test_digest_is_order_invariant_and_content_sensitive(self):
        self.assertEqual(SS.go_ids_digest([3, 1, 2]), SS.go_ids_digest({2, 3, 1}))
        self.assertEqual(SS.go_ids_digest(["3", "1"]), SS.go_ids_digest([1, 3]))
        self.assertNotEqual(SS.go_ids_digest([1, 2]), SS.go_ids_digest([1, 3]))
        self.assertNotEqual(SS.go_ids_digest([12]), SS.go_ids_digest([1, 2]))


if __name__ == "__main__":
    unittest.main()
