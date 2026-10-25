"""Negative controls for source selection, reproducibility and runtime registry drift."""
from __future__ import annotations

from copy import deepcopy
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
import json
import math
import hashlib
from pathlib import Path
import sys
import tempfile
import struct
from types import SimpleNamespace
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import source_plan as SP
import extract_map as EX
import validate_extract as VE
import validate_map_descriptors as VD
import validate_bundle_roundtrip as VB


def example_plan():
    scene = {"name": "Arena_Test_Main", "asset_path": "Assets/Content/Arena_Test_Main.unity", "index": 1}
    return {"schema_version": 1, "key": "arena_test", "source_preset": "arena_test_preset.bundle",
            "source_mode": "TeamFight", "scenes": [scene], "anchor_scenes": [dict(scene)],
            "navmesh_scenes": [], "fog_color": [0.1, 0.2, 0.3], "excluded_scenes": []}


def example_anchors():
    return {SP.PLAYER_MARKER: {"count": 1, "sample_positions": [[1, 2, 3]], "sample_files": ["level1"]}}


def roles_plan():
    """example_plan plus the four scene roles; as in AirPit the AI scene is also a navmesh scene."""
    def scene(name, index):
        return {"name": name, "asset_path": f"Assets/Content/{name}.unity", "index": index}
    plan = example_plan()
    plan["navmesh_scenes"] = [scene("Arena_Test_AI", 4)]
    plan["excluded_scenes"] = [
        dict(scene("Arena_Test_Scripts", 2), role="scripts", reason="authored scripts"),
        dict(scene("Arena_Test_Sound", 3), role="sound", reason="ambient sound"),
        dict(scene("Arena_Test_AI", 4), role="ai", reason="AI metadata"),
        dict(scene("Arena_Test_Culling", 5), role="culling", reason="culling data"),
    ]
    return plan


class SpawnPlacementAdmissionTests(unittest.TestCase):
    def test_native_equator_keeps_legacy_authored_starts(self):
        self.assertFalse(VB.requires_spawn_placement({"source": {"map_key": "arena_equator"}}))
        self.assertFalse(VB.requires_spawn_placement({"source": {}}))

    def test_other_identified_maps_still_require_placement_proof(self):
        for descriptor in (REPO / "tools/maps").glob("*.json"):
            data = json.loads(descriptor.read_text(encoding="utf-8-sig"))
            key = data.get("key") if isinstance(data, dict) else None
            if key and key != "arena_equator":
                with self.subTest(map=key):
                    self.assertTrue(VB.requires_spawn_placement({"source": {"map_key": key}}))
        self.assertTrue(VB.requires_spawn_placement({"source": {"map_key": "arena_future_map"}}))


class SourcePlanTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.root = self.repo / "arena-source"
        self.root.mkdir()
        (self.root / "level1").write_bytes(b"scene")
        (self.root / "sharedassets1.assets").write_bytes(b"assets")
        self.plan = example_plan()
        self.path = self.repo / "tools/extract/plans/test.json"
        self.path.parent.mkdir(parents=True)
        self.path.write_text(json.dumps(self.plan), encoding="utf-8")
        self.manifest = {"source": SP.provenance(self.plan, self.path, self.root, "fixed", self.repo),
                         "scenes": ["Arena_Test_Main"], "slice": {}, "anchors_reference": example_anchors()}
        self.build = ["unused", self.plan["scenes"][0]["asset_path"]]

    def verify(self):
        with patch.object(SP, "read_build_scenes", return_value=self.build):
            return SP.validate_manifest_plan(self.manifest, self.repo)

    def test_valid_plan_and_exact_source_mapping(self):
        self.assertEqual(self.verify()[1], self.plan)

    def test_hash_ignores_json_formatting_but_detects_actual_plan_edit(self):
        self.path.write_text(json.dumps(self.plan, indent=4, sort_keys=True), encoding="utf-8")
        self.verify()
        self.plan["source_mode"] = "LastHero"
        self.path.write_text(json.dumps(self.plan), encoding="utf-8")
        with self.assertRaisesRegex(SP.PlanError, "digest drift"):
            self.verify()

    def test_manifest_scene_registry_drift_is_rejected(self):
        self.manifest["source"]["scene_registry"][0]["index"] = 4
        with self.assertRaisesRegex(SP.PlanError, "scene_registry"):
            self.verify()

    def test_full_asset_path_is_required_not_just_basename(self):
        self.build[1] = "Assets/Other/Arena_Test_Main.unity"
        with self.assertRaisesRegex(SP.PlanError, "missing from source BuildSettings"):
            self.verify()

    def test_duplicate_path_chooses_only_unique_largest_copy(self):
        self.build.append(self.build[1])
        (self.root / "level2").write_bytes(b"scene")
        (self.root / "sharedassets2.assets").write_bytes(b"x")
        self.verify()
        (self.root / "sharedassets2.assets").write_bytes(b"assets")
        with self.assertRaisesRegex(SP.PlanError, "ambiguity"):
            self.verify()
        (self.root / "sharedassets2.assets").write_bytes(b"larger than expected")
        with self.assertRaisesRegex(SP.PlanError, "drift"):
            self.verify()

    def test_missing_level_is_hard_failure(self):
        (self.root / "level1").unlink()
        with self.assertRaisesRegex(SP.PlanError, "input missing"):
            self.verify()

    def test_excluded_scene_index_must_still_match_exact_path(self):
        excluded = {"name": "Arena_Test_Culling", "asset_path": "Assets/Content/Arena_Test_Culling.unity",
                    "index": 2, "reason": "not imported"}
        self.plan["excluded_scenes"] = [excluded]
        self.build.extend([excluded["asset_path"], excluded["asset_path"]])
        SP.verify_scene_mapping(self.plan, self.build, self.root)
        # No serialized file is needed for excluded references, but stale
        # indices cannot be allowed to identify a different source scene.
        self.build[2] = "Assets/Content/Arena_New_Scene.unity"
        with self.assertRaisesRegex(SP.PlanError, "excluded source scene mapping drift"):
            SP.verify_scene_mapping(self.plan, self.build, self.root)

    @unittest.skipUnless(Path(EX.DEFAULT_SOURCE, "globalgamemanagers").is_file(), "Arena source not installed")
    def test_all_repository_plans_match_installed_arena(self):
        source = Path(EX.DEFAULT_SOURCE)
        build = SP.read_build_scenes(source)
        for path in sorted(SP.PLAN_ROOT.glob("*.json")):
            with self.subTest(plan=path.name):
                SP.verify_scene_mapping(SP.load_plan(path), build, source)

    def test_missing_source_is_hard_failure(self):
        self.manifest["source"]["arena_data_root"] = str(self.repo / "missing")
        with self.assertRaisesRegex(SP.PlanError, "unavailable"):
            self.verify()

    def test_plan_path_cannot_escape_repository_plans(self):
        self.manifest["source"]["source_plan"] = "../../external.py"
        with self.assertRaisesRegex(SP.PlanError, "under"):
            self.verify()

    def test_duplicate_or_conflicting_scene_records_are_rejected(self):
        self.plan["scenes"].append(dict(self.plan["scenes"][0]))
        with self.assertRaisesRegex(SP.PlanError, "duplicate"):
            SP.validate_plan(self.plan)
        self.plan = example_plan()
        self.plan["anchor_scenes"][0]["index"] = 2
        with self.assertRaisesRegex(SP.PlanError, "conflicting"):
            SP.validate_plan(self.plan)

    def test_missing_nonfinite_or_truncated_anchors_rejected(self):
        cases = [{}, {SP.PLAYER_MARKER: {"count": 1, "sample_positions": [[1, float("nan"), 2]], "sample_files": ["level1"]}},
                 {SP.PLAYER_MARKER: {"count": 20, "sample_positions": [[1, 2, 3]], "sample_files": ["level1"]}}]
        for anchors in cases:
            with self.subTest(anchors=anchors), self.assertRaises(SP.PlanError):
                SP.validate_anchors(anchors)

    def test_external_files_must_exist_and_stay_under_source(self):
        with self.assertRaisesRegex(SP.PlanError, "missing"):
            EX.external_path(self.root, "sharedassets999.assets")
        with self.assertRaisesRegex(SP.PlanError, "escapes"):
            EX.external_path(self.root, "../outside.assets")
        self.assertEqual(EX.external_path(self.root, "sharedassets1.assets"), (self.root / "sharedassets1.assets").resolve())

    def test_builtin_resources_resolve_from_source_resources_directory(self):
        resource = self.root / "Resources/unity default resources"
        resource.parent.mkdir()
        resource.write_bytes(b"builtin")
        self.assertEqual(EX.external_path(self.root, "library/unity default resources"), resource)

    def test_required_terrain_is_never_silently_dropped(self):
        file = SimpleNamespace(objects={1: SimpleNamespace(type=SimpleNamespace(name="TerrainCollider"))})
        with self.assertRaisesRegex(SP.PlanError, "unsupported terrain"):
            EX.reject_unsupported_ground(file, "Arena_Test_Main")


def _airpit_plan_is_v8() -> bool:
    """True once airpit.json carries the v8 Main_hall record (the one with an `exclude` list)."""
    plan = SP.load_plan(SP.PLAN_ROOT / "airpit.json")
    return any("exclude" in r.get("select", {}) for r in plan["scenes"] if r["name"] == "Arena_Airpit_Main_hall")


AIRPIT_PLAN_V8 = _airpit_plan_is_v8()


def partial_plan():
    """example_plan plus a partial scene (level 2) that skips the twins of the full scene (level 1)."""
    plan = example_plan()
    plan["scenes"].append({
        "name": "Arena_Test_Part", "asset_path": "Assets/Content/Arena_Test_Part.unity", "index": 2,
        "select": {"schema": 1, "reason": "furnished shops", "skip_twins_of": 1,
                   "roots": ["Root/Shops"],
                   "region_roots": [{"path": "Root/Props ", "regions": ["shop"]}],
                   "regions": [{"name": "shop", "min": [0, 0, 0], "max": [10, 5, 10]}]}})
    return plan


class PartialScenePlanTests(unittest.TestCase):
    """A scenes record with `select` is a partial scene: validated by the plan, reported by the manifest."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.root = self.repo / "arena-source"
        self.root.mkdir()
        self.plan = partial_plan()
        self.path = self.repo / "tools/extract/plans/test.json"
        self.path.parent.mkdir(parents=True)
        self.path.write_text(json.dumps(self.plan), encoding="utf-8")
        self.manifest = {"source": SP.provenance(self.plan, self.path, self.root, "fixed", self.repo),
                         "scenes": ["Arena_Test_Main", "Arena_Test_Part"], "slice": {},
                         "partial_scenes": {"Arena_Test_Part": {"index": 2, "objects": 3}},
                         "anchors_reference": example_anchors()}

    def verify(self):
        with patch.object(SP, "read_build_scenes", return_value=[]), patch.object(SP, "verify_scene_mapping"):
            return SP.validate_manifest_plan(self.manifest, self.repo, verify_source=False)

    def test_partial_plan_is_valid_and_its_registry_stays_a_plain_scene_list(self):
        SP.validate_plan(self.plan)
        self.assertEqual([r["name"] for r in SP.partial_scene_records(self.plan)], ["Arena_Test_Part"])
        self.assertEqual(SP.registry_for(self.plan), [
            {"name": "Arena_Test_Main", "asset_path": "Assets/Content/Arena_Test_Main.unity", "index": 1},
            {"name": "Arena_Test_Part", "asset_path": "Assets/Content/Arena_Test_Part.unity", "index": 2}])
        self.assertEqual([r["index"] for r in SP.unique_records(self.plan)], [1, 2])

    def test_a_plan_without_select_has_no_partial_scenes(self):
        self.assertEqual(SP.partial_scene_records(example_plan()), [])

    def test_select_belongs_on_scenes_records_only(self):
        select = self.plan["scenes"][1]["select"]
        for group in ("anchor_scenes", "navmesh_scenes"):
            plan = partial_plan()
            plan[group] = [dict(plan["scenes"][1])]
            with self.subTest(group=group), self.assertRaisesRegex(SP.PlanError, "cannot carry select"):
                SP.validate_plan(plan)
        plan = partial_plan()
        plan["excluded_scenes"] = [{"name": "Arena_Test_Culling", "asset_path": "Assets/Content/Arena_Test_Culling.unity",
                                    "index": 9, "reason": "not imported", "select": select}]
        with self.assertRaisesRegex(SP.PlanError, "cannot carry select"):
            SP.validate_plan(plan)

    def test_skip_twins_of_must_name_an_earlier_full_scene(self):
        for twins, label in ((2, "itself"), (3, "unknown index"), (7, "an excluded or foreign index")):
            plan = partial_plan()
            plan["scenes"][1]["select"]["skip_twins_of"] = twins
            with self.subTest(label), self.assertRaisesRegex(SP.PlanError, "earlier full scene|itself"):
                SP.validate_plan(plan)
        # a full scene that comes later in the scene order is not a valid reference
        plan = partial_plan()
        plan["scenes"].reverse()
        with self.assertRaisesRegex(SP.PlanError, "earlier full scene"):
            SP.validate_plan(plan)
        # another partial scene is not a reference either
        plan = partial_plan()
        second = deepcopy(plan["scenes"][1])
        second.update(name="Arena_Test_Part2", asset_path="Assets/Content/Arena_Test_Part2.unity", index=3)
        second["select"]["skip_twins_of"] = 2
        plan["scenes"].append(second)
        with self.assertRaisesRegex(SP.PlanError, "earlier full scene"):
            SP.validate_plan(plan)
        # no twin skipping at all is fine
        plan = partial_plan()
        del plan["scenes"][1]["select"]["skip_twins_of"]
        SP.validate_plan(plan)

    def test_a_malformed_selection_is_a_plan_error_naming_the_scene(self):
        for change in ({"schema": 2}, {"reason": ""}, {"roots": []}, {"roots": ["Root/Shops/"]},
                       {"regions": []}, {"surprise": True}):
            plan = partial_plan()
            plan["scenes"][1]["select"].update(change)
            if "roots" in change:
                plan["scenes"][1]["select"]["region_roots"] = [] if change["roots"] == [] else \
                    plan["scenes"][1]["select"]["region_roots"]
                if change["roots"] == []:
                    del plan["scenes"][1]["select"]["regions"]
            with self.subTest(change=change), self.assertRaisesRegex(SP.PlanError, "Arena_Test_Part"):
                SP.validate_plan(plan)

    def test_the_digest_covers_the_selection(self):
        before = SP.plan_digest(self.plan)
        for edit in (lambda s: s["roots"].append("Root/Extra"), lambda s: s.update(skip_twins_of=None),
                     lambda s: s["regions"][0]["max"].__setitem__(0, 11), lambda s: s.update(reason="other")):
            plan = partial_plan()
            edit(plan["scenes"][1]["select"])
            with self.subTest(edit=edit):
                self.assertNotEqual(SP.plan_digest(plan), before)

    def test_manifest_must_list_exactly_the_partial_scenes_of_the_plan(self):
        self.assertEqual(self.verify()[1], self.plan)
        for label, change in (("missing block", lambda m: m.pop("partial_scenes")),
                              ("empty block", lambda m: m.update(partial_scenes={})),
                              ("extra scene", lambda m: m["partial_scenes"].update(Arena_Test_Main={"index": 1})),
                              ("renamed scene", lambda m: m.update(partial_scenes={"Other": {"index": 2}})),
                              ("wrong index", lambda m: m["partial_scenes"]["Arena_Test_Part"].update(index=1)),
                              ("not an object", lambda m: m["partial_scenes"].update(Arena_Test_Part=[2])),
                              ("not a mapping", lambda m: m.update(partial_scenes=["Arena_Test_Part"]))):
            manifest = deepcopy(self.manifest)
            change(manifest)
            self.manifest = manifest
            with self.subTest(label), self.assertRaisesRegex(SP.PlanError, "partial_scenes"):
                self.verify()
            self.setUp()

    def test_a_package_of_a_plan_without_partial_scenes_must_not_claim_any(self):
        plan = example_plan()
        path = self.repo / "tools/extract/plans/plain.json"
        path.write_text(json.dumps(plan), encoding="utf-8")
        manifest = {"source": SP.provenance(plan, path, self.root, "fixed", self.repo), "scenes": ["Arena_Test_Main"],
                    "slice": {}, "anchors_reference": example_anchors()}
        SP.validate_manifest_plan(manifest, self.repo, verify_source=False)
        manifest["partial_scenes"] = {"Arena_Test_Main": {"index": 1}}
        with self.assertRaisesRegex(SP.PlanError, "partial_scenes"):
            SP.validate_manifest_plan(manifest, self.repo, verify_source=False)
        manifest["partial_scenes"] = {}
        SP.validate_manifest_plan(manifest, self.repo, verify_source=False)

    def test_slices_stay_refused_beside_partial_scenes(self):
        self.manifest["slice"] = {"2": [7]}
        with self.assertRaisesRegex(SP.PlanError, "cannot contain slices"):
            self.verify()

    def test_the_selection_edit_invalidates_an_extracted_package(self):
        self.plan["scenes"][1]["select"]["roots"].append("Root/Extra")
        self.path.write_text(json.dumps(self.plan), encoding="utf-8")
        with self.assertRaisesRegex(SP.PlanError, "digest drift"):
            self.verify()

    def test_exclude_and_twin_descendants_belong_inside_select(self):
        entry = [{"path": "Root/Shops/A", "reason": "moved copy"}]
        for key, value in (("exclude", entry), ("twin_descendants", "import_missing")):
            with self.subTest(key=key, where="beside select"):
                plan = partial_plan()
                plan["scenes"][1][key] = value
                with self.assertRaisesRegex(SP.PlanError, f"'Arena_Test_Part' carries '{key}' outside its select"):
                    SP.validate_plan(plan)
            with self.subTest(key=key, where="full scene"):
                plan = partial_plan()
                plan["scenes"][0][key] = value
                with self.assertRaisesRegex(SP.PlanError, f"carries '{key}' outside its select"):
                    SP.validate_plan(plan)
            for group in ("anchor_scenes", "navmesh_scenes"):
                with self.subTest(key=key, where=group):
                    plan = partial_plan()
                    plan[group] = [dict(plan["scenes"][0], **{key: value})]
                    with self.assertRaisesRegex(SP.PlanError, f"{group} record .* carries '{key}'"):
                        SP.validate_plan(plan)
            with self.subTest(key=key, where="excluded_scenes"):
                plan = partial_plan()
                plan["excluded_scenes"] = [{"name": "Arena_Test_Culling", "asset_path": "Assets/Content/Arena_Test_Culling.unity",
                                            "index": 9, "reason": "not imported", key: value}]
                with self.assertRaisesRegex(SP.PlanError, f"excluded_scenes record .* carries '{key}'"):
                    SP.validate_plan(plan)

    def test_the_new_select_keys_are_valid_inside_select(self):
        plan = partial_plan()
        plan["scenes"][1]["select"].update(twin_descendants="import_missing", exclude=[
            {"path": "Root/Shops/A", "reason": "moved copy"},
            {"path": "Root/Shops/B", "at": [1.0, 2.0, 3.0], "reason": "replaced in level 1"}])
        SP.validate_plan(plan)
        (record,) = SP.partial_scene_records(plan)
        self.assertEqual(record["select"]["twin_descendants"], "import_missing")

    def test_a_malformed_new_key_is_a_plan_error_naming_the_scene(self):
        for change in ({"twin_descendants": "everything"}, {"exclude": "Root/Shops/A"},
                       {"exclude": [{"path": "Root/Shops/A"}]},
                       {"exclude": [{"path": "A", "reason": "x"}, {"path": "A", "reason": "y"}]},
                       {"exclude": [{"path": "A", "reason": "x", "at": [1, 2]}]}):
            plan = partial_plan()
            plan["scenes"][1]["select"].update(change)
            with self.subTest(change=change), self.assertRaisesRegex(SP.PlanError, "Arena_Test_Part"):
                SP.validate_plan(plan)
        plan = partial_plan()
        del plan["scenes"][1]["select"]["skip_twins_of"]
        plan["scenes"][1]["select"]["twin_descendants"] = "import_missing"
        with self.assertRaisesRegex(SP.PlanError, "needs skip_twins_of"):
            SP.validate_plan(plan)

    def test_the_digest_covers_the_new_keys(self):
        before = SP.plan_digest(self.plan)
        for edit in (lambda s: s.update(twin_descendants="import_missing"),
                     lambda s: s.update(exclude=[{"path": "Root/Shops/A", "reason": "x"}]),
                     lambda s: s.update(exclude=[{"path": "Root/Shops/A", "at": [0, 0, 0], "reason": "x"}])):
            plan = partial_plan()
            edit(plan["scenes"][1]["select"])
            with self.subTest(edit=edit):
                self.assertNotEqual(SP.plan_digest(plan), before)
        a, b = partial_plan(), partial_plan()
        a["scenes"][1]["select"]["exclude"] = [{"path": "Root/Shops/A", "reason": "x"}]
        b["scenes"][1]["select"]["exclude"] = [{"path": "Root/Shops/A", "reason": "y"}]
        self.assertNotEqual(SP.plan_digest(a), SP.plan_digest(b), "the reason is part of the reviewed record")

    def reload_with(self, **select_changes):
        self.plan["scenes"][1]["select"].update(select_changes)
        self.path.write_text(json.dumps(self.plan), encoding="utf-8")
        self.manifest["source"] = SP.provenance(self.plan, self.path, self.root, "fixed", self.repo)

    def test_manifest_counters_are_checked_against_the_plan_select(self):
        self.reload_with(twin_descendants="import_missing",
                         exclude=[{"path": "Root/Shops/A", "reason": "x"}, {"path": "Root/Shops/B", "reason": "y"}])
        block = self.manifest["partial_scenes"]["Arena_Test_Part"]
        block.update(excluded=5, exclude_entries=2, twin_descendants_added=2,
                     twin_descendant_paths=["Root/Shops/A/Light", "Root/Shops/B/Light"])
        self.assertEqual(self.verify()[1], self.plan)
        for label, change in (("wrong entry count", {"exclude_entries": 1}),
                              ("entry count not an int", {"exclude_entries": "2"}),
                              ("count without a list", {"twin_descendant_paths": ["Root/Shops/A/Light"]}),
                              ("unsorted paths", {"twin_descendant_paths": ["Root/Shops/B/Light", "Root/Shops/A/Light"]}),
                              ("paths not strings", {"twin_descendant_paths": [1, 2]}),
                              ("paths not a list", {"twin_descendant_paths": "x"}),
                              ("negative count", {"twin_descendants_added": -1}),
                              ("count not an int", {"twin_descendants_added": True})):
            manifest = deepcopy(self.manifest)
            manifest["partial_scenes"]["Arena_Test_Part"].update(change)
            saved, self.manifest = self.manifest, manifest
            with self.subTest(label), self.assertRaisesRegex(SP.PlanError, "partial_scenes"):
                self.verify()
            self.manifest = saved

    def test_a_package_cannot_claim_imports_the_plan_does_not_ask_for(self):
        block = self.manifest["partial_scenes"]["Arena_Test_Part"]
        block.update(exclude_entries=0, twin_descendants_added=0, twin_descendant_paths=[])
        self.verify()
        block.update(twin_descendants_added=1, twin_descendant_paths=["Root/Shops/A/Light"])
        with self.assertRaisesRegex(SP.PlanError, "imported 1 twin descendant"):
            self.verify()

    def test_a_block_without_the_new_counters_is_not_refused_here(self):
        # the contract pins (native_contract_loader / partial_scope) make them mandatory; the plan check only
        # compares the counters a block carries
        self.assertEqual(set(self.manifest["partial_scenes"]["Arena_Test_Part"]), {"index", "objects"})
        self.verify()

    def test_airpit_plan_imports_the_two_furnished_levels_after_the_five_full_scenes(self):
        plan = SP.load_plan(SP.PLAN_ROOT / "airpit.json")
        self.assertEqual([(r["name"], r["index"]) for r in plan["scenes"]], [
            ("Arena_AirPit_ShootOut", 324), ("Arena_AirPit_ShootOut_Light", 325), ("Arena_Airpit_DesignStuff", 40),
            ("Arena_Airpit_TeamFight", 29), ("Arena_Airpit_TeamFight_DesignStuff", 28),
            ("Arena_Airpit_Main_hall", 38), ("Arena_Airpit_Light", 37)])
        partial = {r["name"]: r for r in SP.partial_scene_records(plan)}
        self.assertEqual(sorted(partial), ["Arena_Airpit_Light", "Arena_Airpit_Main_hall"])
        self.assertEqual({k: v["select"]["skip_twins_of"] for k, v in partial.items()},
                         {"Arena_Airpit_Main_hall": 324, "Arena_Airpit_Light": 325})
        for record in partial.values():
            self.assertEqual(record["asset_path"], f"Assets/Content/Locations/Arena_Airpit/{record['name']}.unity")

    @unittest.skipIf(AIRPIT_PLAN_V8, "the plan holds the v8 records; see AirpitV8PlanTests")
    def test_airpit_main_hall_selection_names_the_reviewed_groups(self):
        from scene_select import validate_select
        plan = SP.load_plan(SP.PLAN_ROOT / "airpit.json")
        spec = validate_select(plan["scenes"][5]["select"])
        hall = "SBG_Arena_Airpit_Main_hall/"
        keys = [r.key for r in spec.rules]
        self.assertEqual(keys[:2], [f"roots:{hall}OO/PROPS/INDOOR/SHOPS", "roots:PROJECTION/INDOOR/SHOPS"])
        self.assertIn(f"roots:{hall}SOO_LOD0/DOORS", keys)
        self.assertEqual(sorted(k for k in keys if "SOO_LOD0/" in k and "DOORS" not in k), sorted(
            f"roots:{hall}SOO_LOD0/{n}" for n in ("Airport_Shop_BS", "DonB_Ramen", "LP_Shop", "LP_Defile",
                                                  "TH_Roll_door_01", "TH_Roll_door_02", "TH_Roll_door_03",
                                                  "TH_Shop_indoor")))
        regions = {r.path: [x.name for x in r.regions] for r in spec.rules if r.kind == "region_roots"}
        self.assertEqual(regions["PROJECTION"], ["outline box"])
        self.assertIn(f"{hall}OO/PROPS/FLOOR_DAMAGE ", regions)  # the trailing space is part of the name
        shops = ["NW shop", "NE shop", "SE shop", "SW shop"]
        for group in ("INDOOR/BACK", "INDOOR/FRONT", "INDOOR/LEFT", "INDOOR/RIGHT", "OUTDOOR/FIELD"):
            self.assertEqual(regions[f"{hall}OO/PROPS/{group}"], shops)
        light = validate_select(plan["scenes"][6]["select"])
        self.assertEqual([r.path for r in light.rules if r.kind == "roots"],
                         ["SBG_Airpit_Light/OO/" + n for n in ("ChinaPlace*", "BurgerSpot*", "HELLFINGER", "LouiPeeton*")])
        self.assertEqual({tuple(x.name for x in r.regions) for r in light.rules if r.kind == "region_roots"},
                         {tuple(shops)})


@unittest.skipUnless(AIRPIT_PLAN_V8, "airpit.json still holds the v7 records")
class AirpitV8PlanTests(unittest.TestCase):
    """The two partial-scene records of package v8 (twin descendants, exclusions, the decoration box).

    ``plan_file`` can name a candidate plan; the default is the repository plan."""
    plan_file = None

    @classmethod
    def setUpClass(cls):
        plan = SP.load_plan(cls.plan_file or SP.PLAN_ROOT / "airpit.json")
        cls.records = {r["name"]: r for r in SP.partial_scene_records(plan)}

    def specs(self):
        from scene_select import validate_select
        return {name: validate_select(r["select"]) for name, r in self.records.items()}

    def test_main_hall_record(self):
        hall = "SBG_Arena_Airpit_Main_hall/"
        spec = self.specs()["Arena_Airpit_Main_hall"]
        self.assertEqual((spec.skip_twins_of, spec.twin_descendants), (324, "import_missing"))
        self.assertEqual([r.path for r in spec.rules if r.kind == "roots"],
                         [hall + "OO/PROPS/INDOOR/SHOPS", "PROJECTION/INDOOR/SHOPS"]
                         + [hall + "SOO_LOD0/" + n for n in ("Airport_Shop_BS", "DonB_Ramen", "LP_Shop", "LP_Defile",
                                                              "TH_Roll_door_01", "TH_Roll_door_02", "TH_Roll_door_03",
                                                              "TH_Shop_indoor", "DOORS")]
                         # v9 (2026-10-17): the three level38 SOO crates under the tarp City_Generic_Tarp_01 (1)
                         + [hall + "SOO/" + n for n in ("reserve_woodBox_medium (10)", "reserve_woodBox_medium (9)", "cardbox_vinyl")])
        self.assertEqual({r.path: [x.name for x in r.regions] for r in spec.rules if r.kind == "region_roots"},
                         {"PROJECTION": ["decoration box"], hall + "OO/PROPS": ["decoration box"],
                          "VARIANT": ["decoration box"], "PROPSnoCulling": ["decoration box"]})
        self.assertEqual([(r.name, r.lo, r.hi) for r in spec.regions],
                         [("decoration box", (-60.0, -5.0, -85.0), (40.0, 20.0, 91.0))])
        # not imported on purpose: PHYSIC (inactive in Arena), the SOO cover walls and the brick walls; the three SOO
        # crates under the tarp are the reviewed exception (their same-named siblings elsewhere are excluded by position)
        crates = {hall + "SOO/" + n for n in ("reserve_woodBox_medium (10)", "reserve_woodBox_medium (9)", "cardbox_vinyl")}
        for rule in spec.rules:
            for forbidden in ("PHYSIC", "Destroyed_Brick_Wall", "LIGHT_Disabled"):
                self.assertNotIn(forbidden, rule.path)
            if "SOO/" in rule.path:
                self.assertIn(rule.path, crates)
            self.assertNotEqual(rule.path, hall + "SOO_LOD0")

    def test_main_hall_exclusions(self):
        spec = self.specs()["Arena_Airpit_Main_hall"]
        self.assertEqual(len(spec.exclude), 213)   # 208 census entries + the 5 same-named crate siblings (v9)
        self.assertTrue(all(e.reason.strip() for e in spec.exclude))
        self.assertTrue(any(e.at is not None for e in spec.exclude))
        self.assertTrue(all(e.path.split("/")[0] in ("SBG_Arena_Airpit_Main_hall", "VARIANT", "PROPSnoCulling")
                            for e in spec.exclude), "every exclusion lies in a selected subtree")

    def test_light_record(self):
        spec = self.specs()["Arena_Airpit_Light"]
        lamp = "SBG_Airpit_Light/OO/"
        self.assertEqual((spec.skip_twins_of, spec.twin_descendants), (325, "import_missing"))
        # The nine imported twin descendants that level325 replaced or disabled on purpose (review of 2026-10-13):
        # SupportLight stand-ins on the AIRBUS/NO_CULL bunker lamps, a re-tuned Spot Light, two emptied LODGroups.
        self.assertEqual([e.path for e in spec.exclude], [
            "NO_CULL/Old_Bunker_Light_cookie (8)/BaseLight",
            "NO_CULL/Old_Bunker_Light_cookie (8)/MainLight",
            lamp + "AIRBUS/Old_Bunker_Light_cookie (6)/BaseLight",
            lamp + "AIRBUS/Old_Bunker_Light_cookie (6)/MainLight",
            lamp + "AIRBUS/Old_Bunker_Light_cookie (9)/BaseLight ",
            lamp + "AIRBUS/Old_Bunker_Light_cookie (9)/MainLight",
            lamp + "AIRPORT LAMPS/Airport_lamp (72)/Spot Light (2)",
            lamp + "ShadowPlanes/ShadowPlaneBox (125)/Plane_LOD0",
            lamp + "ShadowPlanes/ShadowPlaneBox (140)/Plane_LOD0",
        ])
        self.assertTrue(all(e.at is None and e.reason.strip() for e in spec.exclude), "every path is unique in level37")
        self.assertEqual([r.path for r in spec.rules if r.kind == "roots"],
                         [lamp + n for n in ("ChinaPlace*", "BurgerSpot*", "HELLFINGER", "LouiPeeton*")])
        regions = {r.path: [x.name for x in r.regions] for r in spec.rules if r.kind == "region_roots"}
        self.assertEqual(list(regions), [lamp + "GLOBAL GI", "NO_CULL", lamp + "ShadowPlanes", "PUDDLES", lamp + "FogSheets"]
                         + [lamp + n for n in ("Airport_lamp_02", "LAMPS_GENERIC*", "AIRBUS", "AIRPORT LAMPS",
                                               "BACK_AIRPORT_LAMPS", "LUM_LAMPS", "Point Light", "chemlight_emissive",
                                               "Lamps_ceiling - DISABLED")])
        self.assertEqual({tuple(v) for v in regions.values()}, {("decoration box",)})
        self.assertEqual([(r.name, r.lo, r.hi) for r in spec.regions],
                         [("decoration box", (-60.0, -5.0, -85.0), (40.0, 20.0, 91.0))])
        for rule in spec.rules:   # script-only shells, disabled lights and the ceiling track lights stay out
            for forbidden in ("LIGHT_Disabled", "BACKGROUND", "STENCILS", "PLANE_AREA_HIGHLIGHTS", "CEILING"):
                self.assertNotIn(forbidden, rule.path)


class SceneRoleTests(unittest.TestCase):
    """Plan item 1: excluded scenes carry a `role` so extractors need no per-map index table."""

    def setUp(self):
        self.plan = roles_plan()

    def test_roles_resolve_to_the_exact_excluded_scene_records(self):
        SP.validate_plan(self.plan)
        expected = {"scripts": 2, "sound": 3, "ai": 4, "culling": 5}
        self.assertEqual(set(SP.SCENE_ROLES), set(expected))
        for role, index in expected.items():
            with self.subTest(role=role):
                record = SP.scene_for_role(self.plan, role)
                self.assertEqual(set(record), {"name", "asset_path", "index"})
                self.assertEqual(record["index"], index)
                self.assertEqual(record["asset_path"], f"Assets/Content/{record['name']}.unity")
        self.assertEqual({r: v["index"] for r, v in SP.scene_roles(self.plan).items()}, expected)

    def test_returned_record_is_a_copy(self):
        SP.scene_for_role(self.plan, "sound")["index"] = 99
        self.assertEqual(SP.scene_for_role(self.plan, "sound")["index"], 3)

    def test_plan_without_roles_stays_valid_and_declares_none(self):
        plain = example_plan()
        self.assertEqual(SP.scene_roles(SP.validate_plan(plain)), {})
        with self.assertRaisesRegex(SP.PlanError, "declares no scene with role 'sound'"):
            SP.scene_for_role(plain, "sound")
        partial = roles_plan()
        partial["excluded_scenes"] = partial["excluded_scenes"][:1]
        partial["navmesh_scenes"] = []
        SP.validate_plan(partial)
        self.assertEqual(set(SP.scene_roles(partial)), {"scripts"})
        with self.assertRaisesRegex(SP.PlanError, "declares no scene with role 'ai'"):
            SP.scene_for_role(partial, "ai")

    def test_unknown_or_malformed_role_names_are_rejected(self):
        for bad in ("audio", "Scripts", "", None, 3, ["sound"]):
            with self.subTest(role=bad):
                plan = roles_plan()
                plan["excluded_scenes"][1]["role"] = bad
                with self.assertRaisesRegex(SP.PlanError, "unknown scene role"):
                    SP.validate_plan(plan)
        for bad in ("audio", "SOUND", "", None):
            with self.subTest(lookup=bad), self.assertRaisesRegex(SP.PlanError, "unknown scene role"):
                SP.scene_for_role(self.plan, bad)

    def test_a_role_names_exactly_one_scene(self):
        self.plan["excluded_scenes"][3]["role"] = "sound"
        with self.assertRaisesRegex(SP.PlanError, "duplicate scene role 'sound'"):
            SP.validate_plan(self.plan)

    def test_a_scene_cannot_carry_two_roles(self):
        self.plan["excluded_scenes"][3] = dict(self.plan["excluded_scenes"][0], role="culling")
        with self.assertRaisesRegex(SP.PlanError, "cannot carry two roles"):
            SP.validate_plan(self.plan)

    def test_roles_never_sit_on_an_imported_scene(self):
        imported = self.plan["scenes"][0]
        self.plan["excluded_scenes"][3] = dict(imported, role="culling", reason="x")
        with self.assertRaisesRegex(SP.PlanError, "cannot be assigned to the imported scene"):
            SP.validate_plan(self.plan)

    def test_role_key_is_rejected_outside_excluded_scenes(self):
        for group in SP.SCENE_GROUPS:
            with self.subTest(group=group):
                plan = roles_plan()
                plan[group][0]["role"] = "scripts"
                with self.assertRaisesRegex(SP.PlanError, "cannot carry a scene role"):
                    SP.validate_plan(plan)

    def test_ai_role_must_also_be_the_same_navmesh_scene(self):
        self.plan["navmesh_scenes"] = []
        with self.assertRaisesRegex(SP.PlanError, "must also be listed in navmesh_scenes"):
            SP.validate_plan(self.plan)
        other = roles_plan()
        other["navmesh_scenes"][0]["asset_path"] = "Assets/Content/Other/Arena_Test_AI.unity"
        with self.assertRaisesRegex(SP.PlanError, "must also be listed in navmesh_scenes"):
            SP.validate_plan(other)

    def test_roles_do_not_make_extraction_load_the_excluded_scenes(self):
        # Scripts, Sound and Culling stay unloaded; the AI scene is already a navmesh scene.
        self.assertEqual({r["index"] for r in SP.unique_records(self.plan)}, {1, 4})

    def test_role_scenes_keep_the_exact_path_drift_check(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp)
            for index in (1, 4):
                (source / f"level{index}").write_bytes(b"scene")
                (source / f"sharedassets{index}.assets").write_bytes(b"assets")
            build = ["unused", self.plan["scenes"][0]["asset_path"]]
            build += [r["asset_path"] for r in self.plan["excluded_scenes"]]
            SP.verify_scene_mapping(self.plan, build, source)
            build[3] = "Assets/Content/Arena_New_Sound.unity"
            with self.assertRaisesRegex(SP.PlanError, "excluded source scene mapping drift for Arena_Test_Sound"):
                SP.verify_scene_mapping(self.plan, build, source)

    def test_repository_plans_validate_and_every_role_names_its_excluded_scene(self):
        for path in sorted(SP.PLAN_ROOT.glob("*.json")):
            with self.subTest(plan=path.name):
                plan = SP.load_plan(path)
                for role, record in SP.scene_roles(plan).items():
                    tagged = [SP.scene_record(r) for r in plan["excluded_scenes"] if r.get("role") == role]
                    self.assertEqual(tagged, [record])
                    self.assertEqual(SP.scene_for_role(plan, role), record)

    def test_airpit_plan_declares_its_four_roles(self):
        plan = SP.load_plan(SP.PLAN_ROOT / "airpit.json")
        found = {role: SP.scene_for_role(plan, role) for role in SP.SCENE_ROLES}
        self.assertEqual({r: v["index"] for r, v in found.items()},
                         {"scripts": 36, "sound": 39, "ai": 33, "culling": 30})
        self.assertEqual({r: v["name"] for r, v in found.items()},
                         {"scripts": "Arena_Airpit_Scripts", "sound": "Arena_Airpit_Sound",
                          "ai": "Arena_Airpit_TeamFight_AI", "culling": "Arena_Airpit_TeamFight_Culling"})
        # The AI scene is the one already loaded for NavMesh metadata; the other three are not.
        self.assertEqual({r["index"] for r in plan["navmesh_scenes"]}, {found["ai"]["index"]})
        # Loaded levels: the five full scenes, the two partial scenes (38 and 37) and the AI scene.
        self.assertEqual({r["index"] for r in SP.unique_records(plan)}, {324, 325, 40, 29, 28, 38, 37, 33})

    @unittest.skipUnless(Path(EX.DEFAULT_SOURCE, "globalgamemanagers").is_file(), "Arena source not installed")
    def test_airpit_role_scenes_occur_exactly_once_in_installed_build_settings(self):
        build = SP.read_build_scenes(Path(EX.DEFAULT_SOURCE))
        plan = SP.load_plan(SP.PLAN_ROOT / "airpit.json")
        for role in SP.SCENE_ROLES:
            record = SP.scene_for_role(plan, role)
            with self.subTest(role=role):
                self.assertEqual(build.count(record["asset_path"]), 1)
                self.assertEqual(build[record["index"]], record["asset_path"])


class CompleteAnchorTests(unittest.TestCase):
    def test_all_501_markers_have_stable_positions_and_source_identity(self):
        # The old census stopped at 500 records and returned only eight positions.
        def marker(i):
            transform = SimpleNamespace(position=(i, 2, 3))
            ptr = SimpleNamespace(deref=lambda: transform)
            go = SimpleNamespace(read=lambda: SimpleNamespace(m_Components=[ptr]))
            return SimpleNamespace(path_id=i, type=SimpleNamespace(name="MonoBehaviour"),
                                   parse_monobehaviour_head=lambda: SimpleNamespace(m_GameObject=SimpleNamespace(deref=lambda: go)))
        file = SimpleNamespace(objects={i: marker(i) for i in range(501, 0, -1)})
        env = SimpleNamespace(files={"level1": file})
        with patch.object(EX.EQ.arena_census, "resolve_script_name", return_value=(SP.PLAYER_MARKER, "ok")), \
             patch.object(EX.EQ.arena_census, "world_transform_of_transform", side_effect=lambda t: (t.position, None)):
            result = EX.collect_anchors(env, example_plan(), {1: {"level": "level1"}})[SP.PLAYER_MARKER]
        self.assertEqual(result["count"], 501)
        self.assertEqual(result["sample_positions"][0], [1.0, 2.0, 3.0])
        self.assertEqual(result["sample_positions"][-1], [501.0, 2.0, 3.0])
        self.assertEqual(len(result["source_anchors"]), 501)


class ReproducibilityTests(unittest.TestCase):
    def test_equator_native_features_reject_partial_and_sliced_source_before_loading(self):
        cases = (("--scenes", "51"), ("--slice-roots", "51:1"),
                 ("--scenes", "51,51,53,55,57,58"))
        for flags in cases:
            with self.subTest(flags=flags), patch.object(sys, "argv", ["extract_equator.py", "--native-features", *flags]), \
                 patch.object(EX.EQ, "load_environment") as load:
                with self.assertRaises(SystemExit) as stopped:
                    EX.EQ.main()
                self.assertEqual(stopped.exception.code, 2)
                load.assert_not_called()

    def test_equator_native_reproduction_retains_feature_opt_in_and_target(self):
        with patch.object(VE.subprocess, "run") as run:
            VE.run_extractor(REPO / "tools/extract/extract_equator.py", [51, 53, 55, 57, 58], {},
                             "source", Path("out"), "fixed", 90,
                             target_root="custom SPT/EscapeFromTarkov_Data", native_features=True)
        cmd = run.call_args.args[0]
        self.assertIn("--native-features", cmd)
        self.assertEqual(cmd[cmd.index("--target") + 1], "custom SPT/EscapeFromTarkov_Data")
        self.assertEqual(cmd[cmd.index("--scenes") + 1], "51,53,55,57,58")

    def test_bay5_reproduction_retains_the_selected_target_install(self):
        with patch.object(VE.subprocess, "run") as run:
            VE.run_extractor(REPO / "tools/extract/extract_bay5.py", [1], {},
                             "source", Path("out"), "fixed", 90,
                             target_root="custom SPT/EscapeFromTarkov_Data")
        cmd = run.call_args.args[0]
        self.assertEqual(cmd[cmd.index("--target") + 1], "custom SPT/EscapeFromTarkov_Data")
        self.assertNotIn("--scenes", cmd)

    def test_generic_invocation_uses_only_fixed_script_and_verified_plan_flag(self):
        with patch.object(VE.subprocess, "run") as run:
            VE.run_extractor(REPO / "tools/extract/extract_map.py", [1], {}, "source", Path("out"), "fixed", 90, Path("plan.json"))
        cmd = run.call_args.args[0]
        self.assertIn("--plan", cmd)
        self.assertNotIn("--scenes", cmd)
        self.assertEqual(run.call_args.kwargs["timeout"], 90)

    def test_legacy_equator_and_bay5_cli_preserved(self):
        for script, expected in (("extract_equator.py", True), ("extract_bay5.py", False)):
            with self.subTest(script=script), patch.object(VE.subprocess, "run") as run:
                VE.run_extractor(REPO / "tools/extract" / script, [1], {}, "source", Path("out"), "fixed", 90)
                self.assertEqual("--scenes" in run.call_args.args[0], expected)
                self.assertNotIn("--plan", run.call_args.args[0])
                self.assertNotIn("--native-features", run.call_args.args[0])

    def test_manifest_cannot_choose_an_executable_path(self):
        for script in ("../../evil.py", "tools/extract/arbitrary.py", str(REPO / "tools/extract/extract_map.py")):
            with self.subTest(script=script), self.assertRaises(SystemExit):
                VE.extractor_script_for({"source": {"extractor_script": script}})
        self.assertEqual(VE.extractor_script_for({}), VE.EXTRACTOR_SCRIPT)


class RegistryTests(unittest.TestCase):
    def test_distant_shadow_sidecar_drift_is_rejected(self):
        original_load = VD.load_map
        for altered in (False, True):
            def read_map(path):
                data = original_load(path)
                if altered and path.name == "equator.json":
                    data["distant_shadow_caster_paths"] = ["Unexpected/CeilingShadow"]
                return data

            output = StringIO()
            with self.subTest(altered=altered), \
                 patch.object(sys, "argv", ["validate_map_descriptors.py"]), \
                 patch.object(VD, "load_map", side_effect=read_map), \
                 redirect_stdout(output), redirect_stderr(output):
                # The unchanged repository is the positive control. Alter only the JSON
                # view, keeping the actual C# registry parser and comparison active.
                self.assertEqual(VD.main(), 1 if altered else 0)
                if altered:
                    self.assertIn("distant_shadow_caster_paths mismatch", output.getvalue())

    def test_puddle_sidecar_drift_is_rejected(self):
        # 2026-10-25 (docs/progress/puddle-footsteps-all-maps.md): the water material names and the extra eligible surfaces of the
        # puddle footstep repair exist twice, in tools/maps/<stem>.json and in the descriptor; either side alone must fail.
        original_load = VD.load_map
        for file, field, value in (("equator.json", "puddle_water_material_names", ["0877f2335044042e"]),
                                   ("airpit.json", "puddle_water_material_names", []),
                                   ("bowl.json", "puddle_extra_surfaces", []),
                                   ("bowl.json", "puddle_water_material_names", ["70f045bb011600e7"]),
                                   ("bay5.json", "puddle_extra_surfaces", ["Soil"])):
            def read_map(path, file=file, field=field, value=value):
                data = original_load(path)
                if path.name == file:
                    data[field] = value
                return data

            output = StringIO()
            with self.subTest(file=file, field=field), \
                 patch.object(sys, "argv", ["validate_map_descriptors.py"]), \
                 patch.object(VD, "load_map", side_effect=read_map), \
                 redirect_stdout(output), redirect_stderr(output):
                self.assertEqual(VD.main(), 1)
                self.assertIn(field + " mismatch", output.getvalue())

    def test_puddle_field_shapes(self):
        good = {"puddle_water_material_names": ["70f045bb011600e7", "0877f2335044042e"], "puddle_extra_surfaces": ["Soil", "Gravel"]}
        self.assertEqual(VD.puddle_errors(good), [])
        self.assertEqual(VD.puddle_errors({}), [])
        names = ["0877f2335044042e"]
        for label, data in (
                ("padded name", {"puddle_water_material_names": [" 0877f2335044042e"]}),
                ("empty name", {"puddle_water_material_names": [""]}),
                ("not a list", {"puddle_water_material_names": "0877f2335044042e"}),
                ("duplicate name", {"puddle_water_material_names": names * 2}),
                ("unknown surface", {"puddle_water_material_names": names, "puddle_extra_surfaces": ["Dirt"]}),
                ("wrong case", {"puddle_water_material_names": names, "puddle_extra_surfaces": ["soil"]}),
                ("built-in Concrete", {"puddle_water_material_names": names, "puddle_extra_surfaces": ["Concrete"]}),
                ("built-in Asphalt", {"puddle_water_material_names": names, "puddle_extra_surfaces": ["Asphalt"]}),
                ("the result itself", {"puddle_water_material_names": names, "puddle_extra_surfaces": ["Puddle"]}),
                ("duplicate surface", {"puddle_water_material_names": names, "puddle_extra_surfaces": ["Soil", "Soil"]}),
                ("surfaces without water", {"puddle_extra_surfaces": ["Soil"]}),
                ("surface not a list", {"puddle_water_material_names": names, "puddle_extra_surfaces": "Soil"})):
            with self.subTest(label):
                self.assertTrue(VD.puddle_errors(data), label)

    def test_catalog_and_release_membership_are_independently_checked(self):
        # The released set is Equator, Bay5, AirPit and Bowl (RELEASE_MAP_NAMES, Bowl since PLAN-BOWL item 8,
        # 2026-10-18); Third stands for any parked map.
        entries = {name: {} for name in ("Equator", "Bay5", "AirPit", "Bowl", "Third")}
        prefix = "public static readonly IReadOnlyList<ArenaMapDescriptor> "
        def source(catalog, release):
            return prefix + "Catalog = new[] {" + catalog + "};\n" + prefix + "All = new[] {" + release + "};"
        released = "Equator, Bay5, AirPit, Bowl"
        full = "Equator, Bay5, AirPit, Bowl, Third"
        self.assertEqual(VD.RELEASE_MAP_NAMES, ("Equator", "Bay5", "AirPit", "Bowl"))
        self.assertEqual(VD.RELEASE_MAP_KEYS, ("arena_equator", "arena_bay5", "arena_airpit", "arena_bowl"))
        self.assertEqual(VD.registry_membership_errors(source(full, released), entries), [])
        for catalog, release in (("Equator, Bay5, AirPit, Bowl", released), (full + ", Third", released),
                                 ("Equator, Bay5, AirPit, Bowl, Unknown", released), (full, "Equator"),
                                 (full, "Equator, Bay5"), (full, "Equator, Bay5, AirPit"),
                                 (full, "Equator, Bay5, AirPit, Bowl, Third"),
                                 (full, "Equator, Bay5, AirPit, Bowl, Bowl")):
            with self.subTest(catalog=catalog, release=release):
                self.assertTrue(VD.registry_membership_errors(source(catalog, release), entries))

    def test_additional_map_declarations_are_actually_discovered(self):
        text = 'ArenaMapDescriptor Equator = new(Key: "arena_equator"); ArenaMapDescriptor Third = new(Key: "arena_third");'
        self.assertEqual(set(VD.registry_entries(text)), {"Equator", "Third"})

    def test_duplicate_infiltration_and_script_order_rejected(self):
        entry = {"Key": "arena_test", "IdField": "a" * 24, "ScriptsSceneName": "Scripts", "InfiltrationName": "Test", "AllSceneNames": ["Scripts", "Main"]}
        self.assertEqual(VD.registry_identity_errors({"Test": entry}, {}), [])
        second = dict(entry, Key="arena_other", IdField="b" * 24, ScriptsSceneName="OtherScripts", AllSceneNames=["OtherScripts"])
        self.assertTrue(any("InfiltrationName" in error for error in VD.registry_identity_errors({"Test": entry, "Other": second}, {})))
        entry["AllSceneNames"] = ["Main", "Scripts"]
        self.assertTrue(any("first" in error for error in VD.registry_identity_errors({"Test": entry}, {})))


class TerrainArrayTests(unittest.TestCase):
    def setUp(self):
        from PIL import Image
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.package = Path(self.temp.name)
        (self.package / "textures").mkdir()
        slices = []
        for i in range(12):
            path = self.package / "textures" / f"slice{i}.png"
            Image.new("RGBA", (4, 4), (i, 20, 30, 255)).save(path)
            slices.append(f"textures/slice{i}.png")
        self.meta = {"generated_terrain_array": True, "terrain_role": "diffuse", "dimension": "Tex2DArray",
                     "depth": 12, "width": 4, "height": 4, "file": slices[0], "array_slices": slices,
                     "color_space": "sRGB", "is_normal_map": False}

    def test_all_array_slices_required_with_preserved_dimensions_and_channels(self):
        from PIL import Image
        self.assertEqual(VE.generated_terrain_texture_errors(self.meta, self.package), [])
        (self.package / self.meta["array_slices"][11]).unlink()
        self.assertTrue(VE.generated_terrain_texture_errors(self.meta, self.package))
        Image.new("RGBA", (2, 4)).save(self.package / self.meta["array_slices"][11])
        self.assertTrue(VE.generated_terrain_texture_errors(self.meta, self.package))

    def test_array_metadata_rejects_truncated_duplicates_wrong_color_or_import_type(self):
        variants = [dict(self.meta, depth=8), dict(self.meta, array_slices=self.meta["array_slices"][:8]),
                    dict(self.meta, array_slices=[self.meta["file"]] * 12), dict(self.meta, color_space="Linear"),
                    dict(self.meta, is_normal_map=True), dict(self.meta, dimension="Tex2D")]
        for meta in variants:
            with self.subTest(meta=meta):
                self.assertTrue(VE.generated_terrain_texture_errors(meta, self.package))
        normal = dict(self.meta, terrain_role="normalSAO", color_space="Linear")
        self.assertEqual(VE.generated_terrain_texture_errors(normal, self.package), [])

    def test_array_slice_paths_cannot_escape_package(self):
        self.meta["array_slices"][1] = "../outside.png"
        self.assertTrue(any("escapes" in error for error in VE.generated_terrain_texture_errors(self.meta, self.package)))

    def test_arbitrary_legacy_arrays_still_not_assigned(self):
        self.assertIsNone(VB.built_texture_class({"dimension": "Tex2DArray"}))
        self.assertEqual(VB.built_texture_class(self.meta), "Texture2DArray")
        self.assertEqual(VB.built_texture_class({}), "Texture2D")
        self.assertEqual(VE.generated_terrain_texture_errors({"dimension": "Tex2DArray"}, self.package), [])

    def test_only_exact_owned_shader_can_bypass_rebinding(self):
        self.assertEqual(VB.expected_shader_name({"shader": "ArenaMapPort/Terrain", "preserve_shader": True}), "ArenaMapPort/Terrain")
        self.assertEqual(VB.expected_shader_name({"shader": "Legacy"}), "Hidden/ArenaMapPort/Legacy")
        with self.assertRaises(ValueError):
            VB.expected_shader_name({"shader": "Other", "preserve_shader": True})

    def test_terrain_material_cannot_hide_missing_arrays_or_controls(self):
        mat = {"shader": "ArenaMapPort/Terrain", "preserve_shader": True}
        errors = VE.generated_terrain_material_errors(mat, mat, {})
        self.assertTrue(any("_DiffuseArray" in error for error in errors))
        self.assertTrue(any("_Control2" in error for error in errors))
        self.assertEqual(VE.generated_terrain_material_errors({"shader": "Legacy"}, {"shader": "Legacy"}, {}), [])


class TerrainGeometryValidationTests(unittest.TestCase):
    def test_source_vertex_and_collider_tampering_are_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp)
            (package / "scenes").mkdir()
            (package / "meshes").mkdir()
            data_tree = {"m_Heightmap": {"m_Resolution": 3, "m_Heights": list(range(9)),
                                        "m_Scale": {"x": 1, "y": 32766, "z": 1}}}
            data = SimpleNamespace(path_id=5, assets_file=SimpleNamespace(name="sharedassets1.assets"), read_typetree=lambda: data_tree)
            terrain_tree = {"m_GameObject": {"m_PathID": 100}, "m_Enabled": 1}
            terrain = SimpleNamespace(path_id=7, type=SimpleNamespace(name="Terrain"), read_typetree=lambda: terrain_tree,
                                      read=lambda: SimpleNamespace(m_TerrainData=SimpleNamespace(deref=lambda: data)))
            collider = SimpleNamespace(path_id=8, type=SimpleNamespace(name="TerrainCollider"),
                                       read_typetree=lambda: {"m_GameObject": {"m_PathID": 100}, "m_Enabled": 0})
            source = SimpleNamespace(name="level1", objects={7: terrain, 8: collider})
            mesh_id = hashlib.sha1(b"sharedassets1.assets:5:TerrainV1:mesh:0:0").hexdigest()[:16]
            object_key = hashlib.sha1(b"level1:7:TerrainV1:object:0:0:2:2").hexdigest()[:16]
            child_id = str(-(int(object_key, 16) & ((1 << 63) - 1)) - 1)
            comps = [{"type": "MeshFilter", "mesh_id": mesh_id}, {"type": "MeshRenderer", "enabled": True},
                     {"type": "MeshCollider", "mesh_id": mesh_id, "enabled": False, "convex": False, "is_trigger": False}]
            scene = {"objects": [{"id": child_id, "parent_id": "100", "local_position": [0, 0, 0],
                                    "local_rotation": [0, 0, 0, 1], "local_scale": [1, 1, 1], "components": comps}]}
            scene_path = package / "scenes/Arena_Test_Main.json"
            scene_path.write_text(json.dumps(scene), encoding="utf-8")
            positions = [coordinate for z in range(3) for x in range(3) for coordinate in (x, z * 3 + x, z)]
            uv = [c for z in range(3) for x in range(3) for c in (x / 2, z / 2)]
            raw = b"AMPM" + struct.pack("<IIHBB", 1, 9, 23, 0, 1) + struct.pack("<27f", *positions)
            raw += struct.pack("<27f", *([0, 1, 0] * 9)) + struct.pack("<36f", *([1, 0, 0, -1] * 9)) + struct.pack("<18f", *uv)
            indices = [c for corner in (0, 1, 3, 4) for c in (corner, corner + 3, corner + 1, corner + 1, corner + 3, corner + 4)]
            raw += struct.pack("<IIB", 0, 24, 0) + struct.pack("<24H", *indices)
            mesh_path = package / "meshes/ground.mesh.bin"
            mesh_path.write_bytes(raw)
            record = {"scene": "Arena_Test_Main", "source_terrain_id": 7, "source_gameobject_id": 100,
                      "source_collider_id": 8, "terrain_data_file": "sharedassets1.assets", "terrain_data_path_id": 5,
                      "height_resolution": 3, "height_sample_count": 9, "height_denominator": 32766.0,
                      "renderer_enabled": True, "collider_enabled": False, "triangle_count": 8, "hole_cell_count": 0,
                      "mesh_ids": [mesh_id]}
            manifest = {"terrain_conversion": [record], "meshes": {mesh_id: {"file": "meshes/ground.mesh.bin"}}}
            outcomes = []
            with patch.object(VE, "check", side_effect=lambda result, message: outcomes.append(bool(result)) or bool(result)):
                extras = VE.terrain_scene_adjustments("Arena_Test_Main", source, manifest, package)
                self.assertTrue(all(outcomes))
                self.assertEqual(extras, {"MeshCollider": 1})
                outcomes.clear()
                damaged = bytearray(raw)
                struct.pack_into("<f", damaged, 20, 999.0)
                mesh_path.write_bytes(damaged)
                VE.terrain_scene_adjustments("Arena_Test_Main", source, manifest, package)
                self.assertFalse(all(outcomes))
                mesh_path.write_bytes(raw)
                outcomes.clear()
                comps[-1]["enabled"] = True
                scene_path.write_text(json.dumps(scene), encoding="utf-8")
                VE.terrain_scene_adjustments("Arena_Test_Main", source, manifest, package)
                self.assertFalse(all(outcomes))


class TerrainBytecodeValidationTests(unittest.TestCase):
    def test_shader_identity_alone_cannot_pass_compiled_program_gate(self):
        with self.assertRaisesRegex(ValueError, "no D3D11"):
            VB.validate_terrain_shader_bytecode(SimpleNamespace(platforms=[]))
        tree = {"m_ParsedForm": {"m_Name": "ArenaMapPort/Terrain"}}
        shader = SimpleNamespace(type=SimpleNamespace(name="Shader"), read_typetree=lambda: tree,
                                 read=lambda: SimpleNamespace(platforms=[]))
        outcomes = []
        with patch.object(VB, "check", side_effect=lambda result, message: outcomes.append(bool(result)) or bool(result)):
            VB.validate_owned_terrain_shader(SimpleNamespace(objects=[shader]), {
                "materials": {"terrain": {"shader": "ArenaMapPort/Terrain", "preserve_shader": True}}})
        self.assertFalse(all(outcomes))

    def test_dxbc_truncation_and_missing_executable_chunk_fail(self):
        # Structurally valid token container; actual compiler acceptance is a
        # separate native check exercised on source and final built bytecode.
        code = b"DXBC" + bytes(16) + struct.pack("<IIII", 1, 52, 1, 36)
        code += b"SHDR" + struct.pack("<III", 8, 0x10040, 2)
        self.assertEqual(VB.dxbc_stage(code), 1)
        variants = [code[:-1], code.replace(b"SHDR", b"RDEF"), bytes(52)]
        bad_offset = bytearray(code)
        struct.pack_into("<I", bad_offset, 32, 500)
        variants.append(bytes(bad_offset))
        for damaged in variants:
            with self.subTest(damaged=damaged):
                with self.assertRaises(ValueError):
                    VB.dxbc_stage(damaged)


class SpawnBundleValidationTests(unittest.TestCase):
    def setUp(self):
        self.starts = [{"name": name, "accepted": True, "accepted_position": [i * 3, 2, -5]}
                       for i, name in enumerate(VB.SPAWN_SIDES)]
        self.records = [{"name": start["name"], "spawn_name": start["name"], "id": str(i),
                         "position": list(start["accepted_position"]), "world_position": list(start["accepted_position"]),
                         "infiltration": "ArenaTest", "sides": VB.SPAWN_SIDES[start["name"]], "categories": 1,
                         "active": True, "trigger_collider": True} for i, start in enumerate(self.starts)]

    def test_stale_serialized_position_cannot_pass_accepted_report(self):
        self.assertEqual(VB.spawn_record_errors(self.records, self.starts, "ArenaTest"), [])
        self.records[0]["position"][1] += 0.05
        self.assertTrue(any("built position" in e for e in VB.spawn_record_errors(self.records, self.starts, "ArenaTest")))
        self.records[0]["position"][1] -= 0.05
        self.records[1]["world_position"][0] += 1
        self.assertTrue(any("world_position" in e for e in VB.spawn_record_errors(self.records, self.starts, "ArenaTest")))

    def test_spawn_proof_requires_all_three_active_trigger_markers_and_infiltration(self):
        self.records[1]["trigger_collider"] = False
        self.records[2]["infiltration"] = "OtherMap"
        errors = VB.spawn_record_errors(self.records, self.starts, "ArenaTest")
        self.assertTrue(any("trigger" in e for e in errors))
        self.assertTrue(any("infiltration" in e for e in errors))
        self.assertTrue(VB.spawn_record_errors(self.records[:2], self.starts, "ArenaTest"))

    def test_world_position_uses_parent_rotation_and_scale(self):
        transform = {"m_LocalPosition": {"x": 10, "y": 20, "z": 30},
                     "m_LocalScale": {"x": 2, "y": 3, "z": 4},
                     "m_LocalRotation": {"x": 0, "y": 0, "z": math.sqrt(0.5), "w": math.sqrt(0.5)}}
        point = VB._apply_trs([1, 2, 3], transform)
        for actual, expected in zip(point, [4, 22, 42]):
            self.assertAlmostEqual(actual, expected)


class SpecialMeshValidationTests(unittest.TestCase):
    def test_independent_source_decode_catches_changed_color_position_and_index(self):
        from test_mesh_compat import fixture, Reader
        import mesh_compat
        tree = fixture()
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "mesh.bin"
            result = mesh_compat.write_compressed_mesh_bin(Reader(tree), "source.assets", path)
            record = {"kind": "packed_bit_vectors", "vertex_count": result.vertex_count,
                      "submesh_count": result.submesh_count, "channel_mask": result.channel_mask,
                      "index_format": result.index_format}
            self.assertEqual(VE.special_mesh_output_errors(tree, record, path), [])
            original = path.read_bytes()
            for offset, value, channel in [(16, 100.0, "positions"), (16 + 3 * 40, 1 / 255, "colors")]:
                damaged = bytearray(original)
                struct.pack_into("<f", damaged, offset, value)
                path.write_bytes(damaged)
                self.assertTrue(any(channel in e for e in VE.special_mesh_output_errors(tree, record, path)))
            damaged = bytearray(original)
            struct.pack_into("<H", damaged, len(damaged) - 2, 0)
            path.write_bytes(damaged)
            self.assertTrue(any("index buffer" in e for e in VE.special_mesh_output_errors(tree, record, path)))

    def test_source_empty_is_exact_and_cannot_hide_packed_or_replacement_geometry(self):
        from test_mesh_compat import fixture, Reader, packed
        import mesh_compat
        tree = fixture()
        tree["m_MeshCompression"] = 0
        tree["m_SubMeshes"][0]["indexCount"] = 0
        tree["m_CompressedMesh"] = {key: 0 if key == "m_UVInfo" else packed() for key in tree["m_CompressedMesh"]}
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "empty.bin"
            result = mesh_compat.write_empty_mesh_bin(Reader(tree), "source.assets", path)
            record = {"kind": "source_empty", "vertex_count": 0, "submesh_count": 1,
                      "channel_mask": result.channel_mask, "index_format": 0}
            self.assertEqual(VE.special_mesh_output_errors(tree, record, path), [])
            self.assertEqual(VB.empty_built_mesh_errors(tree), [])
            tree["m_CompressedMesh"]["m_Vertices"] = packed([1], 1)
            with self.assertRaisesRegex(ValueError, "packed geometry"):
                VE.special_mesh_output_errors(tree, record, path)
            self.assertTrue(VB.empty_built_mesh_errors(tree))
            tree["m_CompressedMesh"]["m_Vertices"] = packed()
            tree["m_VertexData"]["m_VertexCount"] = 3
            self.assertTrue(VB.empty_built_mesh_errors(tree))

    def test_provenance_hash_and_scene_attachment_are_required(self):
        from test_mesh_compat import fixture, Reader
        import mesh_compat
        import UnityPy
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp)
            (package / "meshes").mkdir()
            (package / "scenes").mkdir()
            reader = Reader(fixture())
            reader.type = SimpleNamespace(name="Mesh")
            engine = SimpleNamespace(write_mesh_bin=None)
            with mesh_compat.compressed_mesh_decoder(engine) as records:
                result = engine.write_mesh_bin(reader, "source.assets", package / "meshes/special.bin")
            manifest = {"source": {"extractor_script": "tools/extract/extract_map.py", "arena_data_root": str(package)},
                        "scenes": ["Test"], "mesh_compatibility": records,
                        "meshes": {result.content_id: {"file": "meshes/special.bin", "vertex_count": 3}}}
            scene = {"objects": [{"components": [{"type": "MeshFilter", "mesh_id": result.content_id}]}]}
            scene_path = package / "scenes/Test.json"
            scene_path.write_text(json.dumps(scene))
            required = {result.content_id: ("source.assets", reader.path_id, reader, reader.read_typetree())}
            from collections import Counter
            census = (required, Counter({("MeshFilter", result.content_id): 1}))
            outcomes = []
            with patch.object(VE, "census_special_source_meshes", return_value=census) as source_census, patch.object(VE, "check", side_effect=lambda ok, message: outcomes.append(ok) or ok):
                VE.validate_mesh_compatibility(manifest, package)
                self.assertTrue(all(outcomes))
                outcomes.clear()
                records[0]["source_object_sha256"] = "0" * 64
                VE.validate_mesh_compatibility(manifest, package)
                self.assertFalse(all(outcomes))
                outcomes.clear()
                records[0]["source_object_sha256"] = hashlib.sha256(reader.get_raw_data()).hexdigest()
                scene_path.write_text(json.dumps({"objects": []}))
                VE.validate_mesh_compatibility(manifest, package)
                self.assertFalse(all(outcomes))
                outcomes.clear()
                scene_path.write_text(json.dumps(scene))
                manifest.pop("mesh_compatibility")
                VE.validate_mesh_compatibility(manifest, package)
                self.assertFalse(all(outcomes))
                self.assertEqual(source_census.call_count, 4)

    def test_live_source_census_deduplicates_meshes_but_counts_every_attachment(self):
        from collections import Counter
        import UnityPy
        tree = {"m_MeshCompression": 0, "m_VertexData": {"m_VertexCount": 0}}
        reads = []
        reader = SimpleNamespace(type=SimpleNamespace(name="Mesh"), assets_file=SimpleNamespace(name="source.assets"),
                                 path_id=123, read_typetree=lambda: reads.append(True) or tree)
        pointer = SimpleNamespace(path_id=123, deref=lambda: reader)
        components = {i: SimpleNamespace(type=SimpleNamespace(name=kind), read=lambda: SimpleNamespace(m_Mesh=pointer))
                      for i, kind in enumerate(["MeshFilter", "MeshFilter", "MeshCollider", "SkinnedMeshRenderer"])}
        env = SimpleNamespace(load_file=lambda path: SimpleNamespace(objects=components))
        manifest = {"source": {"arena_data_root": "D:/source"}}
        with patch.object(UnityPy, "Environment", return_value=env), patch.object(VE, "generic_plan_for", return_value=(None, {"scenes": [{"index": 1}]})):
            special, bindings = VE.census_special_source_meshes(manifest)
        mesh_id = hashlib.sha1(b"source.assets:123:Mesh").hexdigest()[:16]
        self.assertEqual(set(special), {mesh_id})
        self.assertEqual(len(reads), 1)
        self.assertEqual(bindings, Counter({("MeshFilter", mesh_id): 2, ("MeshCollider", mesh_id): 1}))

    def test_built_packed_mesh_count_parity_does_not_hide_changed_channels_or_indices(self):
        from test_mesh_compat import fixture, Reader, packed
        import mesh_compat
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "mesh.bin"
            tree = fixture()
            mesh_compat.write_compressed_mesh_bin(Reader(tree), "source.assets", path)
            self.assertEqual(VB.built_mesh_geometry_errors(Reader(tree), path), [])
            tree["m_CompressedMesh"]["m_FloatColors"] = packed([0, 0, 1, 3] * 3, 2, True)
            self.assertTrue(any("colors" in error for error in VB.built_mesh_geometry_errors(Reader(tree), path)))
            tree = fixture()
            tree["m_CompressedMesh"]["m_Triangles"] = packed([0, 2, 1], 2)
            self.assertTrue(any("index values" in error for error in VB.built_mesh_geometry_errors(Reader(tree), path)))
    def test_bundle_empty_mesh_cannot_remain_as_an_unattached_decoy(self):
        from collections import Counter
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp)
            (package / "scenes").mkdir()
            mesh_id = "abcdef0123456789"
            manifest = {"scenes": ["Test"], "materials": {}, "textures": {},
                        "meshes": {mesh_id: {"vertex_count": 0, "submesh_count": 1}},
                        "mesh_compatibility": [{"kind": "source_empty", "mesh_id": mesh_id}]}
            (package / "manifest.json").write_text(json.dumps(manifest))
            (package / "scenes/Test.json").write_text(json.dumps({"objects": [{"components": [{"type": "MeshFilter", "mesh_id": mesh_id}]}]}))
            expected = VB.load_package_expected(package)
            self.assertEqual(expected["special_mesh_bindings"], Counter({("MeshFilter", mesh_id): 1}))
            tree = {"m_Name": mesh_id, "m_VertexData": {"m_VertexCount": 0, "m_DataSize": b""},
                    "m_IndexBuffer": b"", "m_SubMeshes": [{"firstByte": 0, "indexCount": 0, "topology": 0}]}
            mesh = SimpleNamespace(type=SimpleNamespace(name="Mesh"), path_id=1, assets_file=object(), read_typetree=lambda: tree)
            pointer = SimpleNamespace(path_id=1, deref=lambda: mesh)
            mesh_filter = SimpleNamespace(type=SimpleNamespace(name="MeshFilter"), read=lambda: SimpleNamespace(m_Mesh=pointer))
            go = SimpleNamespace(type=SimpleNamespace(name="GameObject"))
            env = SimpleNamespace(objects=[mesh, mesh_filter, go])
            outcomes = []
            with patch.object(VB, "check", side_effect=lambda ok, message: outcomes.append((ok, message)) or ok):
                VB.validate_scenes_bundle_contents(env, expected, None)
                self.assertTrue(all(ok for ok, _ in outcomes))
                outcomes.clear()
                pointer.path_id = 0
                VB.validate_scenes_bundle_contents(env, expected, None)
                failures = [message for ok, message in outcomes if not ok]
                self.assertTrue(any("attachment counts" in message for message in failures))


    def test_generic_ordinary_raw_mesh_checks_values_and_legacy_keeps_existing_scope(self):
        from test_mesh_compat import fixture, Reader, packed

        class RawReader(Reader):
            type = SimpleNamespace(name="Mesh")
            version = (2022, 3, 43, 1)

            def read(self):
                mesh = super().read()
                mesh.m_IndexBuffer = self.tree["m_IndexBuffer"]
                return mesh

        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp)
            (package / "scenes").mkdir()
            mesh_id = "0123456789abcdef"
            positions = [0., 0., 0., 2., 0., 0., 0., 3., 0.]
            uv = [0., 0., 1., 0., 0., 1.]
            raw = struct.pack("<4sIIHBB", b"AMPM", 1, 3, 17, 0, 1)
            raw += struct.pack("<9f6fIIB3H", *positions, *uv, 0, 3, 0, 0, 1, 2)
            (package / "mesh.bin").write_bytes(raw)
            manifest = {"source": {"extractor_script": "tools/extract/extract_map.py"},
                        "scenes": ["Test"], "materials": {}, "textures": {},
                        "meshes": {mesh_id: {"vertex_count": 3, "submesh_count": 1, "file": "mesh.bin"}}}
            (package / "manifest.json").write_text(json.dumps(manifest))
            (package / "scenes/Test.json").write_text(json.dumps({"objects": [{"components": [{"type": "MeshFilter", "mesh_id": mesh_id}]}]}))
            expected = VB.load_package_expected(package)
            tree = fixture()
            tree.update(m_Name=mesh_id, m_MeshCompression=0, m_IndexBuffer=struct.pack("<3H", 0, 1, 2))
            tree["m_CompressedMesh"] = {key: 0 if key == "m_UVInfo" else packed() for key in tree["m_CompressedMesh"]}
            channels = tree["m_VertexData"]["m_Channels"]
            channels[0].update(dimension=3)
            channels[4].update(dimension=2, offset=12)
            vertex_bytes = struct.pack("<15f", *(value for i in range(3) for value in positions[i * 3:i * 3 + 3] + uv[i * 2:i * 2 + 2]))
            tree["m_VertexData"].update(m_VertexCount=3, m_DataSize=vertex_bytes)
            mesh = RawReader(tree)
            go = SimpleNamespace(type=SimpleNamespace(name="GameObject"))
            mf = SimpleNamespace(type=SimpleNamespace(name="MeshFilter"))
            env = SimpleNamespace(objects=[mesh, go, mf])
            outcomes = []
            with patch.dict(VB.DEFAULT_PACKAGE_HOLDER, package=package), patch.object(VB, "check", side_effect=lambda ok, message: outcomes.append((ok, message)) or ok):
                VB.validate_scenes_bundle_contents(env, expected, None)
                self.assertTrue(all(ok for ok, _ in outcomes), outcomes)
                for offset, label in [(0, "positions"), (12, "uv0")]:
                    outcomes.clear()
                    damaged = bytearray(vertex_bytes)
                    struct.pack_into("<f", damaged, offset, 99.)
                    tree["m_VertexData"]["m_DataSize"] = bytes(damaged)
                    VB.validate_scenes_bundle_contents(env, expected, None)
                    self.assertTrue(any(not ok and label in message for ok, message in outcomes))
                tree["m_VertexData"]["m_DataSize"] = vertex_bytes
                tree["m_IndexBuffer"] = struct.pack("<3H", 0, 2, 1)
                outcomes.clear()
                VB.validate_scenes_bundle_contents(env, expected, None)
                self.assertTrue(any(not ok and "index values" in message for ok, message in outcomes))
                tree["m_IndexBuffer"] = struct.pack("<3H", 0, 1, 2)
                # Actual source car meshes retain signed UV infinities in every
                # stage. That exception cannot hide an introduced infinity, a
                # sign change, a NaN, or nonfinite vertex positions.
                for offset, package_offset, value, accepted in [
                    (12, 52, float("inf"), True),
                    (12, 52, float("-inf"), False),
                    (12, 52, float("nan"), False),
                    (0, 16, float("inf"), False),
                ]:
                    damaged_package, damaged_vertex = bytearray(raw), bytearray(vertex_bytes)
                    struct.pack_into("<f", damaged_package, package_offset, float("inf"))
                    struct.pack_into("<f", damaged_vertex, offset, value)
                    (package / "mesh.bin").write_bytes(damaged_package)
                    tree["m_VertexData"]["m_DataSize"] = bytes(damaged_vertex)
                    outcomes.clear()
                    VB.validate_scenes_bundle_contents(env, expected, None)
                    self.assertEqual(all(ok for ok, _ in outcomes), accepted, outcomes)
                moved_package, moved_vertex = bytearray(raw), bytearray(vertex_bytes)
                struct.pack_into("<f", moved_package, 52, float("inf"))
                struct.pack_into("<f", moved_vertex, 16, float("inf"))
                (package / "mesh.bin").write_bytes(moved_package)
                tree["m_VertexData"]["m_DataSize"] = bytes(moved_vertex)
                outcomes.clear()
                VB.validate_scenes_bundle_contents(env, expected, None)
                self.assertTrue(any(not ok and "uv0" in message for ok, message in outcomes))
                (package / "mesh.bin").write_bytes(raw)
                introduced = bytearray(vertex_bytes)
                struct.pack_into("<f", introduced, 12, float("inf"))
                tree["m_VertexData"]["m_DataSize"] = bytes(introduced)
                outcomes.clear()
                VB.validate_scenes_bundle_contents(env, expected, None)
                self.assertTrue(any(not ok and "uv0" in message for ok, message in outcomes))
                expected["manifest"].pop("source")
                outcomes.clear()
                with patch.object(VB, "built_mesh_geometry_errors", side_effect=AssertionError("legacy geometry scope changed")):
                    VB.validate_scenes_bundle_contents(env, expected, None)
                self.assertTrue(all(ok for ok, _ in outcomes), outcomes)


class NullReferenceBundleValidationTests(unittest.TestCase):
    def test_physical_material_source_and_target_schemas_preserve_values(self):
        source = {"m_DynamicFriction": 0.4, "m_StaticFriction": 0.6, "m_Bounciness": 0.1,
                  "m_FrictionCombine": 2, "m_BounceCombine": 3}
        target = {"dynamicFriction": 0.4, "staticFriction": 0.6, "bounciness": 0.1,
                  "frictionCombine": 2, "bounceCombine": 3}
        def values(tree):
            reader = SimpleNamespace(type=SimpleNamespace(name="PhysicMaterial"), read_typetree=lambda: tree)
            return VB.reference_material_values(SimpleNamespace(path_id=1, deref=lambda: reader))
        self.assertEqual(values(source), values(target))
        expected = {"name": "collider", "type": "MeshCollider", "chain": [], "physic_material": values(source)}
        for key, changed in [("dynamicFriction", 0.9), ("frictionCombine", 0), ("bounceCombine", 0)]:
            damaged = dict(target, **{key: changed})
            actual = dict(expected, physic_material=values(damaged))
            self.assertTrue(VB.reference_records_errors([expected], [actual]))
        incomplete = dict(target)
        incomplete.pop("staticFriction")
        with self.assertRaisesRegex(ValueError, "incomplete"):
            values(incomplete)
        with self.assertRaisesRegex(ValueError, "nonfinite"):
            values(dict(target, dynamicFriction=float("nan")))

    def test_null_component_and_mixed_slots_require_source_and_exact_built_state(self):
        import copy
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp)
            (package / "scenes").mkdir()
            material_id = "abcdef0123456789"
            collider = {"type": "MeshCollider", "mesh_id": None, "enabled": True, "convex": False,
                        "is_trigger": False, "physic_material": None}
            renderer = {"type": "MeshRenderer", "material_ids": [material_id, None]}
            obj = {"id": "100", "name": "repeated name", "active": False, "layer": 12,
                   "parent_id": None, "local_position": [1, 2, 3], "local_rotation": [0, 0, 0, 1],
                   "local_scale": [1, 1, 1], "components": [collider, renderer]}
            scene = {"objects": [obj]}
            record = {"schema_version": 1, "scene": "Test", "source_file": "level1",
                      "source_renderer_path_id": 42, "source_game_object_path_id": 100,
                      "source_object_sha256": "a" * 64, "material_ids": [material_id, None],
                      "source_materials": [{"source_file": "source.assets", "source_path_id": 1}, None],
                      "null_slots": [1], "slot_count": 2}
            manifest = {"source": {"extractor_script": "tools/extract/extract_map.py"}, "scenes": ["Test"],
                        "materials": {material_id: {"tex_envs": {}}}, "meshes": {}, "textures": {},
                        "material_slot_preservation": [record]}
            (package / "manifest.json").write_text(json.dumps(manifest))
            scene_path = package / "scenes/Test.json"
            scene_path.write_text(json.dumps(scene))
            null_record = {"go_id": "100", "name": obj["name"], "active": False, "layer": 12,
                           **VB.package_reference_state(collider)}
            ptr = lambda number: {"m_FileID": 0, "m_PathID": number}
            sf = SimpleNamespace(name="BuildPlayer-Test", objects={})
            def reader(kind, number, tree, typed=None):
                value = SimpleNamespace(type=SimpleNamespace(name=kind), path_id=number, assets_file=sf,
                                        read_typetree=lambda: tree, read=lambda: typed)
                sf.objects[number] = value
                return value
            go_tree = {"m_Name": obj["name"], "m_IsActive": 0, "m_Layer": 12,
                       "m_Component": [{"component": ptr(i)} for i in (200, 300, 400)]}
            go = reader("GameObject", 100, go_tree)
            go_pointer = SimpleNamespace(path_id=100, deref=lambda: go)
            reader("Transform", 200, {"m_GameObject": ptr(100), "m_Father": ptr(0),
                   "m_LocalPosition": dict(zip("xyz", obj["local_position"])),
                   "m_LocalRotation": dict(zip("xyzw", obj["local_rotation"])),
                   "m_LocalScale": dict(zip("xyz", obj["local_scale"]))})
            mc_tree = {"m_Mesh": ptr(0), "m_GameObject": ptr(100), "m_Enabled": 1, "m_Convex": False, "m_IsTrigger": False}
            mc_typed = SimpleNamespace(m_GameObject=go_pointer, m_Material=SimpleNamespace(path_id=0))
            mc = reader("MeshCollider", 300, mc_tree, mc_typed)
            material = SimpleNamespace(type=SimpleNamespace(name="Material"), read_typetree=lambda: {"m_Name": material_id})
            assigned = SimpleNamespace(path_id=500, deref=lambda: material)
            empty = SimpleNamespace(path_id=0)
            mr_tree = {"m_Materials": [ptr(500), ptr(0)], "m_GameObject": ptr(100)}
            mr_typed = SimpleNamespace(m_GameObject=go_pointer, m_Materials=[assigned, empty])
            reader("MeshRenderer", 400, mr_tree, mr_typed)
            outcomes = []
            def run():
                outcomes.clear()
                VB.validate_generic_null_references(object(), package, manifest)
                return all(ok for ok, _ in outcomes)
            with patch.object(VB, "source_reference_expectations", return_value=({"Test": [null_record]}, [record])), \
                 patch.object(VB, "iter_serialized_files", return_value=[sf]), \
                 patch.object(VB, "check", side_effect=lambda ok, message: outcomes.append((ok, message)) or ok):
                self.assertTrue(run(), outcomes)
                for key, changed in [("m_Enabled", 0), ("m_Convex", True), ("m_IsTrigger", True)]:
                    original = mc_tree[key]
                    mc_tree[key] = changed
                    self.assertFalse(run(), outcomes)
                    mc_tree[key] = original
                go_tree["m_IsActive"] = 1
                self.assertFalse(run(), outcomes)
                go_tree["m_IsActive"] = 0
                physical = SimpleNamespace(type=SimpleNamespace(name="PhysicMaterial"), read_typetree=lambda: {
                    "m_DynamicFriction": 0.4, "m_StaticFriction": 0.4, "m_Bounciness": 0., "m_FrictionCombine": 0, "m_BounceCombine": 0})
                mc_typed.m_Material = SimpleNamespace(path_id=999, deref=lambda: physical)
                self.assertFalse(run(), outcomes)
                mc_typed.m_Material = empty
                mr_typed.m_Materials = [empty, assigned]
                self.assertFalse(run(), outcomes)
                mr_typed.m_Materials = [assigned, empty]
                manifest["material_slot_preservation"] = []
                self.assertFalse(run(), outcomes)
                manifest["material_slot_preservation"] = [record]
                sf.objects.pop(300)
                go_tree["m_Component"] = [{"component": ptr(i)} for i in (200, 400)]
                self.assertFalse(run(), outcomes)
                # Removing the package collider as well cannot hide a dropped
                # live-source component from the mandatory source census.
                obj["components"] = [renderer]
                scene_path.write_text(json.dumps(scene))
                self.assertFalse(run(), outcomes)
            obj["components"] = [collider, renderer]
            scene_path.write_text(json.dumps(scene))
            expected = VB.load_package_expected(package)
            self.assertEqual(expected["material_ids_in_scope"], {material_id})
            editor = {"scenes": [{"scene_name": "Test", "game_object_count": 1,
                                 "mesh_filters": {"null_mesh": 0}, "renderers": {"has_null_material_slot": 1},
                                 "lod_groups": {"null_renderer_entries": 0}}]}
            report_path = package / "editor_verify.json"
            report_path.write_text(json.dumps(editor))
            with patch.dict(VB.DEFAULT_PACKAGE_HOLDER, package=package), \
                 patch.object(VB, "check", side_effect=lambda ok, message: outcomes.append((ok, message)) or ok):
                outcomes.clear()
                VB.validate_editor_verify(report_path, expected, require_scripts_scene=False)
                self.assertTrue(all(ok for ok, _ in outcomes), outcomes)
                editor["scenes"][0]["renderers"]["has_null_material_slot"] = 0
                report_path.write_text(json.dumps(editor))
                outcomes.clear()
                VB.validate_editor_verify(report_path, expected, require_scripts_scene=False)
                self.assertFalse(all(ok for ok, _ in outcomes), outcomes)

    def test_ancestor_transform_binds_repeated_names_to_the_right_object(self):
        base = {"type": "MeshCollider", "name": "puddle", "active": True, "layer": 12,
                "chain": [("puddle", [0, 0, 0], [0, 0, 0, 1], [1, 1, 1]),
                          ("parent", [5, 0, 0], [0, 0, 0, 1], [1, 1, 1])]}
        import copy
        actual = copy.deepcopy(base)
        self.assertEqual(VB.reference_records_errors([base], [actual]), [])
        actual["chain"][1][1][0] = 6
        self.assertTrue(VB.reference_records_errors([base], [actual]))


if __name__ == "__main__":
    unittest.main()
