"""Partial scenes: the scope helper and the whole-level source censuses of the validators.

A partial scene takes only a selected part of one Arena level file (plan record ``select``, manifest
``partial_scenes``). The validators that re-read the whole level file must count, for such a scene, only the
components whose owner is an object of the package scene, and must keep their whole-level behaviour for every
other scene. The Arena level files are replaced by small fakes here: no proprietary data is read.
"""
from __future__ import annotations

from collections import Counter
from contextlib import redirect_stderr, redirect_stdout
from copy import deepcopy
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
sys.path.insert(0, str(REPO / "tools" / "extract"))
import partial_scope as PS
import validate_bundle_roundtrip as VB
import validate_extract as VE

GENERIC = "tools/extract/extract_map.py"
FULL_IDS = ["1", "2"]
PART_IDS = ["10", "11", "12"]


def entry(ids=PART_IDS, orphans=((12, 202),), units=2, carriers=1, dropped=0):
    return {"index": 2, "objects": len(ids), "units": units, "carriers": carriers,
            "rules": {"roots:Root/Shelves": {"selected": units, "twin_skipped": 3}},
            "dropped_script_owners": dropped,
            "ballistic_orphans": [list(pair) for pair in orphans], "go_ids_sha256": PS.go_ids_digest(ids)}


def scene_json(name, ids, components=None):
    return {"scene_name": name, "objects": [
        {"id": i, "name": "go" + i, "parent_id": None, "active": True, "layer": 0,
         "components": list((components or {}).get(i, []))} for i in ids]}


def manifest_for(source="unused", partial=True):
    manifest = {"source": {"extractor_script": GENERIC, "arena_data_root": str(source),
                           "scene_registry": [{"name": "Full", "index": 1}, {"name": "Part", "index": 2}]},
                "scenes": ["Full", "Part"], "slice": {}}
    if partial:
        manifest["partial_scenes"] = {"Part": entry()}
    return manifest


class Package:
    """A temporary package directory with the two scene files."""
    def __init__(self, partial_components=None, full_components=None):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name)
        (self.path / "scenes").mkdir()
        self.write("Full", scene_json("Full", FULL_IDS, full_components))
        self.write("Part", scene_json("Part", PART_IDS, partial_components))

    def write(self, name, scene):
        (self.path / "scenes" / (name + ".json")).write_text(json.dumps(scene), encoding="utf-8")

    def close(self):
        self.directory.cleanup()


class Obj:
    """A source object reader: kind, path id, typetree, typed view and raw bytes."""
    def __init__(self, kind, path_id, tree=None, typed=None, raw=b""):
        self.type = SimpleNamespace(name=kind)
        self.path_id = path_id
        self.tree, self.typed, self.raw = tree or {}, typed, raw

    def read_typetree(self):
        return deepcopy(self.tree)

    def read(self):
        return self.typed

    def get_raw_data(self):
        return self.raw


class Level:
    def __init__(self, *objects):
        self.objects = {o.path_id: o for o in objects}


class Environment:
    """UnityPy.Environment stand-in: ``load_file`` maps 'level<N>' to a fake file."""
    def __init__(self, levels):
        self.levels = levels

    def load_file(self, path):
        return self.levels[Path(path).name]


def owner_ptr(go):
    return {"m_FileID": 0, "m_PathID": go}


NULL_MESH = {"m_FileID": 0, "m_PathID": 0}


def null_collider(path_id, go):
    return Obj("MeshCollider", path_id, {"m_Mesh": NULL_MESH, "m_GameObject": owner_ptr(go), "m_Enabled": 1,
                                         "m_Convex": 0, "m_IsTrigger": 0},
               SimpleNamespace(m_Material=None, m_GameObject=SimpleNamespace(path_id=go, deref=lambda go=go: SimpleNamespace(
                   read_typetree=lambda: {"m_Name": f"go{go}", "m_IsActive": 1, "m_Layer": 0}))))


def plan_patch():
    return patch.object(VE, "generic_plan_for", return_value=(None, {"scenes": [
        {"name": "Full", "index": 1}, {"name": "Part", "index": 2}]}))


class ScopeModuleTests(unittest.TestCase):
    def manifest(self, **change):
        manifest = manifest_for()
        manifest["partial_scenes"]["Part"].update(change)
        return manifest

    def test_no_partial_scenes_means_no_scope_and_no_package_access(self):
        manifest = manifest_for(partial=False)
        self.assertEqual(PS.manifest_entries(manifest), {})
        self.assertEqual(PS.scopes_from_package(manifest, None), {})  # nothing to read, nothing to fail
        self.assertEqual(PS.scopes_from_objects(manifest, {}), {})
        self.assertEqual(PS.manifest_entries({"partial_scenes": None}), {})
        self.assertEqual(PS.manifest_entries({"partial_scenes": {}}), {})
        self.assertTrue(PS.in_scope({}, "Full", 12345))

    def test_scope_covers_exactly_the_package_scene_objects(self):
        package = Package()
        self.addCleanup(package.close)
        scopes = PS.scopes_from_package(manifest_for(), package.path)
        self.assertEqual(list(scopes), ["Part"])
        scope = scopes["Part"]
        self.assertEqual(scope.owners, frozenset(PART_IDS))
        self.assertTrue(scope.covers(11) and scope.covers("11"))
        self.assertFalse(scope.covers(99) or scope.covers("1"))
        self.assertTrue(scope.is_orphan(12, 202) and scope.is_orphan("12", "202"))
        self.assertFalse(scope.is_orphan(12, 201) or scope.is_orphan(11, 202))
        self.assertTrue(PS.in_scope(scopes, "Part", 10) and not PS.in_scope(scopes, "Part", 5))
        self.assertTrue(PS.in_scope(scopes, "Full", 5))  # a scene that is not partial is never narrowed

    def test_scoping_a_partial_manifest_needs_its_package(self):
        with self.assertRaisesRegex(PS.PartialScopeError, "package directory"):
            PS.scopes_from_package(manifest_for(), None)

    def test_scenes_may_be_passed_as_object_lists_or_scene_documents(self):
        scene = scene_json("Part", PART_IDS)
        self.assertEqual(PS.scopes_from_objects(manifest_for(), {"Part": scene})["Part"].owners,
                         PS.scopes_from_objects(manifest_for(), {"Part": scene["objects"]})["Part"].owners)

    def test_shape_errors_fail_closed(self):
        package = Package()
        self.addCleanup(package.close)
        cases = {
            "not an object": lambda m: m.update(partial_scenes=[]),
            "unlisted scene": lambda m: m["partial_scenes"].update({"Other": entry()}),
            "entry not an object": lambda m: m["partial_scenes"].update(Part=[]),
            "missing key": lambda m: m["partial_scenes"]["Part"].pop("units"),
            "missing dropped count": lambda m: m["partial_scenes"]["Part"].pop("dropped_script_owners"),
            "negative dropped count": lambda m: m["partial_scenes"]["Part"].update(dropped_script_owners=-1),
            "float dropped count": lambda m: m["partial_scenes"]["Part"].update(dropped_script_owners=0.0),
            "float count": lambda m: m["partial_scenes"]["Part"].update(carriers=1.0),
            "bool count": lambda m: m["partial_scenes"]["Part"].update(objects=True),
            "zero objects": lambda m: m["partial_scenes"]["Part"].update(objects=0),
            "registry index": lambda m: m["partial_scenes"]["Part"].update(index=3),
            "empty rules": lambda m: m["partial_scenes"]["Part"].update(rules={}),
            "rule counts": lambda m: m["partial_scenes"]["Part"]["rules"].update({"r": {"selected": -1, "twin_skipped": 0}}),
            "orphans not a list": lambda m: m["partial_scenes"]["Part"].update(ballistic_orphans={}),
            "orphan not a pair": lambda m: m["partial_scenes"]["Part"].update(ballistic_orphans=[[1, 2, 3]]),
            "orphan not an id": lambda m: m["partial_scenes"]["Part"].update(ballistic_orphans=[["a", 2]]),
            "orphan negative": lambda m: m["partial_scenes"]["Part"].update(ballistic_orphans=[[-1, 2]]),
            "duplicate orphan": lambda m: m["partial_scenes"]["Part"].update(ballistic_orphans=[[12, 202], ["12", "202"]]),
            "bad hash": lambda m: m["partial_scenes"]["Part"].update(go_ids_sha256="ABC"),
        }
        for label, change in cases.items():
            manifest = manifest_for()
            change(manifest)
            with self.subTest(label), self.assertRaises(PS.PartialScopeError):
                PS.scopes_from_package(manifest, package.path)

    def test_package_scene_must_agree_with_the_recorded_selection(self):
        package = Package()
        self.addCleanup(package.close)
        PS.scopes_from_package(manifest_for(), package.path)  # baseline
        manifest = manifest_for()
        manifest["partial_scenes"]["Part"]["objects"] = 4
        with self.assertRaisesRegex(PS.PartialScopeError, "records 4 objects, the package scene holds 3"):
            PS.scopes_from_package(manifest, package.path)
        manifest = manifest_for()
        manifest["partial_scenes"]["Part"]["ballistic_orphans"] = [[99, 203]]
        with self.assertRaisesRegex(PS.PartialScopeError, "orphan owners .* are not objects of the scene"):
            PS.scopes_from_package(manifest, package.path)
        package.write("Part", scene_json("Part", PART_IDS + ["13"]))  # an extra object the digest does not know
        manifest = manifest_for()
        manifest["partial_scenes"]["Part"]["objects"] = 4
        with self.assertRaisesRegex(PS.PartialScopeError, "go_ids_sha256 differs"):
            PS.scopes_from_package(manifest, package.path)
        package.write("Part", scene_json("Part", ["10", "10", "11"]))
        with self.assertRaisesRegex(PS.PartialScopeError, "not unique"):
            PS.scopes_from_package(manifest_for(), package.path)
        (package.path / "scenes" / "Part.json").unlink()
        with self.assertRaisesRegex(PS.PartialScopeError, "cannot read"):
            PS.scopes_from_package(manifest_for(), package.path)

    def test_digest_is_the_extractors_definition(self):
        self.assertEqual(PS.go_ids_digest(["3", "1", "2"]), hashlib.sha256(b"1\n2\n3").hexdigest())
        self.assertEqual(PS.go_ids_digest([10, 9]), hashlib.sha256(b"9\n10").hexdigest())  # numeric, not lexicographic
        try:
            import scene_select
        except ImportError:
            return
        self.assertEqual(PS.go_ids_digest([10, 9, 123456]), scene_select.go_ids_digest([123456, 9, 10]))

    def test_contract_block_is_the_census_for_the_contract(self):
        block = PS.contract_block(manifest_for())
        self.assertEqual(block, {"Part": {"index": 2, "objects": 3, "units": 2, "carriers": 1, "orphans": 1,
                                          "dropped_script_owners": 0,
                                          "rules": {"roots:Root/Shelves": {"selected": 2, "twin_skipped": 3}}}})
        dropped = manifest_for()
        dropped["partial_scenes"]["Part"]["dropped_script_owners"] = 2
        self.assertEqual(PS.contract_block(dropped)["Part"]["dropped_script_owners"], 2)
        self.assertEqual(PS.contract_block(manifest_for(partial=False)), {})

    def test_command_line_prints_the_block_after_checking_the_package(self):
        package = Package()
        self.addCleanup(package.close)
        (package.path / "manifest.json").write_text(json.dumps(manifest_for()), encoding="utf-8")
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(PS.main([str(package.path)]), 0)
        self.assertEqual(json.loads(out.getvalue()), PS.contract_block(manifest_for()))
        (package.path / "manifest.json").write_text(json.dumps(self.manifest(go_ids_sha256="0" * 64)), encoding="utf-8")
        with self.assertRaises(PS.PartialScopeError):
            PS.main([str(package.path)])


class CensusScopeTests(unittest.TestCase):
    """The four whole-level censuses of validate_extract count only the package scene's owners for a partial scene."""

    def setUp(self):
        self.package = Package()
        self.addCleanup(self.package.close)

    def environment(self, full, part):
        return patch("UnityPy.Environment", return_value=Environment({"level1": Level(*full), "level2": Level(*part)}))

    def test_null_mesh_colliders(self):
        full = [null_collider(1, 1), null_collider(2, 500)]      # 500 is no object of the Full package scene
        part = [null_collider(3, 11), null_collider(4, 77), null_collider(5, 12)]
        with plan_patch(), self.environment(full, part):
            partial = VE.census_null_mesh_colliders(manifest_for(), self.package.path)
            whole = VE.census_null_mesh_colliders(manifest_for(partial=False))
        self.assertEqual(partial, Counter({("Full", "1"): 1, ("Full", "500"): 1, ("Part", "11"): 1, ("Part", "12"): 1}))
        self.assertEqual(whole[("Part", "77")], 1)  # without a partial scene the level is counted in full
        self.assertEqual(sum(whole.values()), 5)

    def test_null_mesh_colliders_need_the_package_for_a_partial_manifest(self):
        with plan_patch(), self.environment([], []), self.assertRaises(PS.PartialScopeError):
            VE.census_null_mesh_colliders(manifest_for())

    def test_mixed_material_slots(self):
        material = SimpleNamespace(type=SimpleNamespace(name="Material"), assets_file=SimpleNamespace(name="shared.assets"),
                                   path_id=900)
        pointers = [SimpleNamespace(path_id=0), SimpleNamespace(path_id=900, deref=lambda: material)]

        def renderer(path_id, go):
            return Obj("MeshRenderer", path_id, {"m_Materials": [{"m_FileID": 0, "m_PathID": p.path_id} for p in pointers],
                                                  "m_GameObject": owner_ptr(go)},
                       SimpleNamespace(m_Materials=pointers), raw=b"r%d" % path_id)
        full = [renderer(1, 500)]
        part = [renderer(2, 11), renderer(3, 77)]
        with plan_patch(), self.environment(full, part):
            partial = VE.census_mixed_material_slots(manifest_for(), self.package.path)
            whole = VE.census_mixed_material_slots(manifest_for(partial=False))
        self.assertEqual([(r["scene"], r["source_game_object_path_id"]) for r in partial], [("Full", 500), ("Part", 11)])
        self.assertEqual([(r["scene"], r["source_game_object_path_id"]) for r in whole],
                         [("Full", 500), ("Part", 11), ("Part", 77)])

    def test_special_source_meshes(self):
        def mesh(path_id):
            return SimpleNamespace(type=SimpleNamespace(name="Mesh"), assets_file=SimpleNamespace(name="source.assets"),
                                   path_id=path_id, read_typetree=lambda: {"m_MeshCompression": 1, "m_Name": "m",
                                                                           "m_VertexData": {"m_VertexCount": 3}})

        def filter_(path_id, go, mesh_id):
            pointer = SimpleNamespace(path_id=mesh_id, deref=lambda: mesh(mesh_id))
            return Obj("MeshFilter", path_id, typed=SimpleNamespace(m_Mesh=pointer, m_GameObject=SimpleNamespace(path_id=go)))
        full = [filter_(1, 500, 100)]
        part = [filter_(2, 11, 101), filter_(3, 77, 102)]
        digest = lambda i: hashlib.sha1(f"source.assets:{i}:Mesh".encode()).hexdigest()[:16]
        with plan_patch(), self.environment(full, part):
            special, bindings = VE.census_special_source_meshes(manifest_for(), self.package.path)
            whole_special, whole_bindings = VE.census_special_source_meshes(manifest_for(partial=False))
        self.assertEqual(set(special), {digest(100), digest(101)})  # mesh 102 is only used outside the package scene
        self.assertEqual(bindings, Counter({("MeshFilter", digest(100)): 1, ("MeshFilter", digest(101)): 1}))
        self.assertEqual(set(whole_special), {digest(100), digest(101), digest(102)})
        self.assertEqual(sum(whole_bindings.values()), 3)

    def disabled_environment(self, root):
        def renderer(path_id, go):
            return Obj("MeshRenderer", path_id, {"m_Enabled": 0, "m_GameObject": owner_ptr(go)})
        levels = {"level1": [renderer(1, 1), renderer(2, 500)], "level2": [renderer(3, 11), renderer(4, 77)]}

        def load(path):
            objects = levels[Path(path).name]
            return SimpleNamespace(objects=objects, files={str(path): Level(*objects)})
        for name in levels:
            (root / name).write_bytes(b"")
        return patch("UnityPy.load", side_effect=load)

    def test_disabled_components(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with plan_patch(), self.disabled_environment(root), \
                 patch.object(VE, "terrain_scene_adjustments", return_value={}):
                partial = VE.count_disabled_in_arena(str(root), manifest_for(root), self.package.path)
                whole = VE.count_disabled_in_arena(str(root), manifest_for(root, partial=False), self.package.path)
                with self.assertRaises(SystemExit), redirect_stderr(io.StringIO()):
                    VE.count_disabled_in_arena(str(root), manifest_for(root), None)
        self.assertEqual(partial, {"Full": {"MeshRenderer": 2}, "Part": {"MeshRenderer": 1}})
        self.assertEqual(whole, {"Full": {"MeshRenderer": 2}, "Part": {"MeshRenderer": 2}})

    def run_disabled_counts(self, package_components):
        """validate_disabled_component_counts for the partial fixture; returns the recorded failures."""
        package = Package(partial_components=package_components, full_components={
            "1": [{"type": "MeshRenderer", "enabled": False}], "2": [{"type": "MeshRenderer", "enabled": False}]})
        self.addCleanup(package.close)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with plan_patch(), self.disabled_environment(root), patch.object(VE, "FAILURES", []), \
                 patch.object(VE, "WARNINGS", []), patch.object(VE, "terrain_scene_adjustments", return_value={}), \
                 redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                VE.validate_disabled_component_counts(manifest_for(root), package.path)
                return list(VE.FAILURES), list(VE.WARNINGS)

    def test_a_partial_scene_compares_its_disabled_counts_exactly_without_a_slice_excuse(self):
        failures, warnings = self.run_disabled_counts({"11": [{"type": "MeshRenderer", "enabled": False}]})
        self.assertEqual((failures, warnings), ([], []))
        failures, _ = self.run_disabled_counts({})  # the package lost the disabled renderer of object 11
        self.assertEqual(len(failures), 1)
        self.assertIn("Part.json: disabled MeshRenderer count (0)", failures[0])
        failures, _ = self.run_disabled_counts({"11": [{"type": "MeshRenderer", "enabled": False}],
                                                "12": [{"type": "MeshRenderer", "enabled": False}]})
        self.assertEqual(len(failures), 1)  # one more than the source has on the package objects

    def test_partial_scene_block_check(self):
        def run(manifest, package_path):
            out = io.StringIO()
            with patch.object(VE, "FAILURES", []), redirect_stdout(out), redirect_stderr(io.StringIO()):
                VE.validate_partial_scenes(manifest, package_path)
                return list(VE.FAILURES), out.getvalue()
        failures, out = run(manifest_for(), self.package.path)
        self.assertEqual(failures, [])
        self.assertIn("partial scene Part: 3 objects, 1 hidden ballistic orphan(s)", out)
        self.assertEqual(run(manifest_for(partial=False), self.package.path), ([], ""))  # silent for full packages
        manifest = manifest_for()
        manifest["slice"] = {"2": [1]}
        self.assertEqual(len(run(manifest, self.package.path)[0]), 1)
        manifest = manifest_for()
        manifest["source"]["extractor_script"] = "tools/extract/extract_equator.py"
        self.assertEqual(len(run(manifest, self.package.path)[0]), 1)
        manifest = manifest_for()
        manifest["partial_scenes"]["Part"]["go_ids_sha256"] = "0" * 64
        failures, _ = run(manifest, self.package.path)
        self.assertEqual(len(failures), 1)
        self.assertIn("go_ids_sha256 differs", failures[0])


class RoundtripScopeTests(unittest.TestCase):
    """validate_bundle_roundtrip.source_reference_expectations is scoped like the census above."""

    def run_expectations(self, partial):
        package = Package()
        self.addCleanup(package.close)
        material = SimpleNamespace(type=SimpleNamespace(name="Material"), assets_file=SimpleNamespace(name="shared.assets"),
                                   path_id=900)
        pointers = [SimpleNamespace(path_id=0), SimpleNamespace(path_id=900, deref=lambda: material)]

        def renderer(path_id, go):
            return Obj("MeshRenderer", path_id, {"m_Materials": [{"m_FileID": 0, "m_PathID": p.path_id} for p in pointers],
                                                  "m_GameObject": owner_ptr(go)}, SimpleNamespace(m_Materials=pointers),
                       raw=b"r%d" % path_id)
        levels = {"level1": Level(null_collider(1, 1), null_collider(2, 500), renderer(3, 500)),
                  "level2": Level(null_collider(4, 11), null_collider(5, 77), renderer(6, 11), renderer(7, 77))}
        scenes = {"Full": json.loads((package.path / "scenes" / "Full.json").read_text()),
                  "Part": json.loads((package.path / "scenes" / "Part.json").read_text())}
        with patch("UnityPy.Environment", return_value=Environment(levels)):
            return VB.source_reference_expectations(manifest_for(partial=partial), scenes)

    def test_a_partial_scene_reads_only_its_package_owners(self):
        nulls, mixed = self.run_expectations(True)
        self.assertEqual([r["go_id"] for r in nulls["Full"]], ["1", "500"])  # the full scene is read whole
        self.assertEqual([r["go_id"] for r in nulls["Part"]], ["11"])
        self.assertEqual([(r["scene"], r["source_game_object_path_id"]) for r in mixed], [("Full", 500), ("Part", 11)])

    def test_without_partial_scenes_the_level_is_read_in_full(self):
        nulls, mixed = self.run_expectations(False)
        self.assertEqual([r["go_id"] for r in nulls["Part"]], ["11", "77"])
        self.assertEqual([(r["scene"], r["source_game_object_path_id"]) for r in mixed],
                         [("Full", 500), ("Part", 11), ("Part", 77)])


if __name__ == "__main__":
    unittest.main()
