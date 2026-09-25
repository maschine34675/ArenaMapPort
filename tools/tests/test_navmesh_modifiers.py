"""PLAN-TWO-MAPS item 4 -- NavMesh modifier/volume extraction.

Portable synthetic fixtures for augment_scene()/read_navmesh_areas() (same shape
as test_ballistics.py's SimpleNamespace mocks; no extracted game data checked
in), plus one real-source, byte-exact typetree read against a live Bay5
NavMeshModifier/NavMeshModifierVolume instance -- skipped when the Arena
install is not reachable from this machine (same optional-dependency skip
idiom test_dynamicmaps_export.py uses for its own missing-package case).
"""
from __future__ import annotations

from copy import deepcopy
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace as NS
import unittest
import unittest.mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_navmesh_modifiers as M  # noqa: E402
from common_ids import HardExtractError  # noqa: E402

ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
FAKE_VERSION = (2022, 3, 43, 0)


def script(name):
    namespace, cls = name.rsplit(".", 1)
    return NS(path_id=1556, read_typetree=lambda: {"m_ClassName": cls, "m_Namespace": namespace})


def modifier_reader(path_id=20, owner=10, enabled=1, override_area=False, area=0,
                     ignore_from_build=True, apply_to_children=True, affected_agents=(-1,)):
    tree = {"m_OverrideArea": override_area, "m_Area": area, "m_IgnoreFromBuild": ignore_from_build,
            "m_ApplyToChildren": apply_to_children, "m_AffectedAgents": list(affected_agents)}
    return NS(path_id=path_id, version=FAKE_VERSION, type=NS(name="MonoBehaviour"),
              parse_monobehaviour_head=lambda: NS(m_Script=script(M.SCRIPT_MODIFIER), m_GameObject=NS(path_id=owner), m_Enabled=enabled),
              read_typetree=lambda nodes=None, check_read=False: tree)


def volume_reader(path_id=21, owner=10, enabled=1, area=1, size=(4.0, 3.0, 4.0), center=(0.0, 1.0, 0.0),
                   affected_agents=(-1,)):
    tree = {"m_Area": area, "m_AffectedAgents": list(affected_agents),
            "m_Size": {"x": size[0], "y": size[1], "z": size[2]},
            "m_Center": {"x": center[0], "y": center[1], "z": center[2]}}
    return NS(path_id=path_id, version=FAKE_VERSION, type=NS(name="MonoBehaviour"),
              parse_monobehaviour_head=lambda: NS(m_Script=script(M.SCRIPT_VOLUME), m_GameObject=NS(path_id=owner), m_Enabled=enabled),
              read_typetree=lambda nodes=None, check_read=False: tree)


def unrelated_reader(path_id=22, owner=10):
    return NS(path_id=path_id, version=FAKE_VERSION, type=NS(name="MonoBehaviour"),
              parse_monobehaviour_head=lambda: NS(m_Script=script("EFT.Ballistics.BallisticCollider"), m_GameObject=NS(path_id=owner), m_Enabled=1))


def scene(owner_id="10"):
    return {"scene_name": "Synthetic", "objects": [{"id": owner_id, "name": "bruno_helix (1)",
             "active": True, "parent_id": None, "components": []}]}


class NavMeshModifierExtractionTests(unittest.TestCase):
    def test_modifier_and_volume_are_appended_and_counted(self):
        file = NS(name="level89", objects={20: modifier_reader(override_area=True, area=1), 21: volume_reader()})
        result = M.augment_scene(scene(), file)
        self.assertEqual(result, (1, 1))

    def test_modifier_fields_and_default_enabled_state(self):
        s = scene()
        file = NS(name="level89", objects={20: modifier_reader(override_area=True, area=1, enabled=0)})
        M.augment_scene(s, file)
        comp = s["objects"][0]["components"][0]
        self.assertEqual(comp, {"type": "NavMeshModifier", "schema_version": 1, "enabled": False,
                                 "override_area": True, "area": 1, "ignore_from_build": True,
                                 "apply_to_children": True, "affected_agents": [-1]})

    def test_volume_fields_use_array_vectors(self):
        s = scene()
        file = NS(name="level89", objects={21: volume_reader(area=1, size=(2, 3, 4), center=(0, 1, 0))})
        M.augment_scene(s, file)
        comp = s["objects"][0]["components"][0]
        self.assertEqual(comp["size"], [2, 3, 4])
        self.assertEqual(comp["center"], [0, 1, 0])
        self.assertIsInstance(comp["size"], list)

    def test_unrelated_script_is_ignored(self):
        file = NS(name="level89", objects={22: unrelated_reader()})
        self.assertEqual(M.augment_scene(scene(), file), (0, 0))
        self.assertEqual(scene()["objects"][0]["components"], [])

    def test_null_script_is_ignored(self):
        reader = NS(path_id=23, version=FAKE_VERSION, type=NS(name="MonoBehaviour"),
                    parse_monobehaviour_head=lambda: NS(m_Script=NS(path_id=0), m_GameObject=NS(path_id=10), m_Enabled=1))
        file = NS(name="level89", objects={23: reader})
        self.assertEqual(M.augment_scene(scene(), file), (0, 0))

    def test_owner_not_in_scene_raises(self):
        file = NS(name="level89", objects={20: modifier_reader(owner=999)})
        with self.assertRaisesRegex(HardExtractError, "not in this scene"):
            M.augment_scene(scene(), file)

    def test_idempotent_second_pass_adds_nothing(self):
        s = scene()
        file = NS(name="level89", objects={20: modifier_reader(override_area=True, area=1)})
        self.assertEqual(M.augment_scene(s, file), (1, 0))
        original = deepcopy(s)
        self.assertEqual(M.augment_scene(s, file), (0, 0))
        self.assertEqual(s, original)

    def test_conflicting_existing_component_raises(self):
        s = scene()
        file = NS(name="level89", objects={20: modifier_reader(override_area=True, area=1)})
        M.augment_scene(s, file)
        s["objects"][0]["components"][0]["area"] = 5
        with self.assertRaisesRegex(HardExtractError, "conflicting existing"):
            M.augment_scene(s, file)

    def test_non_default_affected_agents_is_recorded_faithfully(self):
        # Ground truth (2026-09-25 spike): every real instance carries the default [-1]
        # ("All agent types"), but the extractor's own job is only faithful recording --
        # it is NavMeshModifierImporter.SetAffectedAgents (com.unity.ai.navigation@1.1.5
        # exposes no public setter for an arbitrary per-agent list) that fails loudly on
        # a differing value at import time, not this module.
        s = scene()
        file = NS(name="level89", objects={20: modifier_reader(affected_agents=(0, 1))})
        M.augment_scene(s, file)
        self.assertEqual(s["objects"][0]["components"][0]["affected_agents"], [0, 1])

    def test_duplicate_scene_object_ids_rejected(self):
        bad_scene = {"scene_name": "Synthetic", "objects": [
            {"id": "10", "name": "a", "active": True, "parent_id": None, "components": []},
            {"id": "10", "name": "b", "active": True, "parent_id": None, "components": []},
        ]}
        with self.assertRaisesRegex(HardExtractError, "duplicate scene object IDs"):
            M.augment_scene(bad_scene, NS(name="level89", objects={}))

    def test_read_navmesh_areas_from_project_settings(self):
        areas_tree = {"areas": [{"name": "Walkable", "cost": 1.0}, {"name": "Not Walkable", "cost": 1.0},
                                 {"name": "", "cost": 1.0}, {"name": "Jump", "cost": 2.0}]}
        settings_obj = NS(type=NS(name="NavMeshProjectSettings"), read_typetree=lambda: areas_tree)
        other_obj = NS(type=NS(name="Transform"))
        env = NS(files={"gm": NS(objects={1: other_obj, 2: settings_obj})})
        self.assertEqual(M.read_navmesh_areas(env), {
            "0": {"name": "Walkable", "cost": 1.0},
            "1": {"name": "Not Walkable", "cost": 1.0},
            "3": {"name": "Jump", "cost": 2.0},
        })

    def test_read_navmesh_areas_missing_is_empty(self):
        env = NS(files={"level1": NS(objects={1: NS(type=NS(name="Transform"))})})
        self.assertEqual(M.read_navmesh_areas(env), {})

    def test_read_navmesh_areas_falls_back_to_bare_globalgamemanagers(self):
        # Ground truth (2026-09-25 spike, re-confirmed against a real extractor run this
        # session): NavMeshProjectSettings lives in Arena's bare "globalgamemanagers" file,
        # not "globalgamemanagers.assets" -- the file extract_equator.load_environment()/
        # extract_bay5.load_environment_bay5() actually load. Without source_root, a miss
        # in the already-loaded env must stay {}, not raise.
        empty_env = NS(files={"globalgamemanagers.assets": NS(objects={1: NS(type=NS(name="Transform"))})})
        self.assertEqual(M.read_navmesh_areas(empty_env), {})
        self.assertEqual(M.read_navmesh_areas(empty_env, source_root="/does/not/exist"), {})

        areas_tree = {"areas": [{"name": "Walkable", "cost": 1.0}, {"name": "Not Walkable", "cost": 1.0}]}
        bare_file_env = NS(files={"gm": NS(objects={1: NS(type=NS(name="NavMeshProjectSettings"), read_typetree=lambda: areas_tree)})})
        with tempfile.TemporaryDirectory() as tmp:
            open(os.path.join(tmp, "globalgamemanagers"), "wb").close()
            with unittest.mock.patch.object(M, "UnityPy", NS(load=lambda path: bare_file_env)):
                self.assertEqual(M.read_navmesh_areas(empty_env, source_root=tmp),
                                 {"0": {"name": "Walkable", "cost": 1.0}, "1": {"name": "Not Walkable", "cost": 1.0}})


@unittest.skipUnless(os.path.isdir(ARENA_SOURCE), "Arena source not reachable from this machine")
class FeatureEnabledAcceptsNavmeshAreasKeyTests(unittest.TestCase):
    """validate_native_features.feature_enabled() must accept the extra manifest.native_features
    ['navmesh_areas'] entry read_navmesh_areas() adds (the merged extractor writes it into every
    package) without breaking its strict key-equality compare() for packages that do not carry it."""

    def setUp(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
        import validate_native_features as v
        self.v = v

    def _bay5_manifest(self, areas):
        contract = self.v.MAPS["arena_bay5"]
        native_features = {"schema_version": 1, "required": ["ballistics", contract["feature"]]}
        if areas is not None:
            native_features["navmesh_areas"] = areas
        return {
            "scenes": list(contract["scenes"].values()),
            "native_features": native_features,
            "native_ballistics": {}, "native_audio": {},
        }

    AREAS = {"0": {"name": "Walkable", "cost": 1.0}, "1": {"name": "Not Walkable", "cost": 1.0},
             "2": {"name": "Jump", "cost": 2.0}, "3": {"name": "Sitdown", "cost": 1.0}}

    def test_accepts_manifest_without_navmesh_areas(self):
        self.assertTrue(self.v.feature_enabled(self._bay5_manifest(None)))

    def test_accepts_manifest_with_navmesh_areas(self):
        self.assertTrue(self.v.feature_enabled(self._bay5_manifest(self.AREAS)))

    def test_accepts_navmesh_areas_together_with_raincover(self):
        manifest = self._bay5_manifest(self.AREAS)
        manifest["native_features"]["raincover"] = {"schema_version": 1, "indoor_trigger_count": 30,
                                                    "dry_plane_count": 669, "weather_obstacle": True}
        self.assertTrue(self.v.feature_enabled(manifest))

    def test_rejects_malformed_navmesh_areas(self):
        for bad in ({}, {"0": {"name": "Walkable"}}, {"x": {"name": "Walkable", "cost": 1.0}},
                    {"0": {"name": "Not Walkable", "cost": 1.0}, "1": {"name": "Walkable", "cost": 1.0}}):
            with self.assertRaises(ValueError, msg=repr(bad)):
                self.v.feature_enabled(self._bay5_manifest(bad))

    def test_still_rejects_unknown_extra_key(self):
        manifest = self._bay5_manifest(self.AREAS)
        manifest["native_features"]["something_else"] = 1
        with self.assertRaises(ValueError):
            self.v.feature_enabled(manifest)


class NavMeshModifierRealSourceTests(unittest.TestCase):
    """Byte-exact (check_read=True) read of real Bay5 NavMeshModifier/NavMeshModifierVolume
    instances -- the same falsification this module's typetree node lists were proven
    against in the 2026-09-25 spike, re-run here rather than trusted from a stale log."""

    @classmethod
    def setUpClass(cls):
        import UnityPy
        cls.env = UnityPy.load(f"{ARENA_SOURCE}/level84")  # Arena_Bay5_Main

    def test_real_bay5_instances_round_trip_byte_exact(self):
        found_modifier = found_volume = 0
        for obj in self.env.objects:
            if obj.type.name != "MonoBehaviour":
                continue
            name, status = M.census.resolve_script_name(obj)
            if status != "ok":
                continue
            if name == M.SCRIPT_MODIFIER:
                tree = obj.read_typetree(nodes=M.build_modifier_nodes(obj.version), check_read=True)
                self.assertIn("m_OverrideArea", tree)
                self.assertIsInstance(tree["m_AffectedAgents"], list)
                found_modifier += 1
            elif name == M.SCRIPT_VOLUME:
                tree = obj.read_typetree(nodes=M.build_volume_nodes(obj.version), check_read=True)
                self.assertIn("m_Size", tree)
                found_volume += 1
        self.assertGreater(found_modifier + found_volume, 0,
                            "expected at least one real NavMeshModifier/NavMeshModifierVolume in level84")


if __name__ == "__main__":
    unittest.main()
