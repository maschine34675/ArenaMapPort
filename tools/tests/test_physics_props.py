"""Portable synthetic fixtures; no extracted game data in the tests (pattern of test_ballistics.py)."""
from copy import deepcopy
from pathlib import Path
import sys
from types import SimpleNamespace as NS
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_physics_props as P  # noqa: E402
from common_ids import HardExtractError  # noqa: E402


def rigidbody_reader(fields):
    return NS(read_typetree=lambda: dict(fields))


def script_reader(class_name, namespace="", path_id=99, deref_ok=True):
    if not deref_ok:
        return NS(path_id=path_id, read_typetree=lambda: None)
    return NS(path_id=path_id, read_typetree=lambda: {"m_ClassName": class_name, "m_Namespace": namespace})


def mono_reader(path_id, script, gameobject_path_id, gameobject_file_id=0, enabled=True):
    head = NS(m_Script=script, m_GameObject=(NS(path_id=gameobject_path_id, file_id=gameobject_file_id)
                                              if gameobject_path_id is not None else None),
              m_Enabled=enabled)
    return NS(path_id=path_id, type=NS(name="MonoBehaviour"), parse_monobehaviour_head=lambda: head)


def fixture(owner_id="10", enabled=True, extra_components=None):
    script = script_reader("SmallPhysicsObject")
    reader = mono_reader(20, script, int(owner_id), enabled=enabled)
    components = [{"type": "Rigidbody", "mass": 0.2}]
    if extra_components:
        components = components + extra_components
    scene = {"scene_name": "Synthetic", "objects": [
        {"id": owner_id, "name": "plasticCup_2_Physic (1)", "components": components},
    ]}
    file = NS(name="level51", objects={20: reader})
    return file, scene


class RigidbodyBuilderTests(unittest.TestCase):
    def test_maps_every_verified_field(self):
        obj = rigidbody_reader({
            "m_Mass": 0.1, "m_LinearDamping": 0.25, "m_AngularDamping": 0.75,
            "m_UseGravity": 1, "m_IsKinematic": 0, "m_Interpolate": 2,
            "m_Constraints": 126, "m_CollisionDetection": 1,
        })
        result = P.build_rigidbody(obj, ctx=None)
        self.assertEqual(result, {
            "type": "Rigidbody", "mass": 0.1, "linear_damping": 0.25, "angular_damping": 0.75,
            "use_gravity": True, "is_kinematic": False, "interpolate": 2,
            "constraints": 126, "collision_detection": 1,
        })

    def test_never_records_an_enabled_key(self):
        # Rigidbody has no m_Enabled field at all (verified against real Arena data this spike) --
        # unlike every other COMPONENT_BUILDERS entry, this record must never carry "enabled".
        result = P.build_rigidbody(rigidbody_reader({"m_Enabled": 0}), ctx=None)
        self.assertNotIn("enabled", result)

    def test_back_compat_defaults_when_fields_absent(self):
        result = P.build_rigidbody(rigidbody_reader({}), ctx=None)
        self.assertEqual(result["mass"], 1.0)
        self.assertEqual(result["linear_damping"], 0.0)
        self.assertEqual(result["angular_damping"], 0.05)
        self.assertTrue(result["use_gravity"])
        self.assertFalse(result["is_kinematic"])
        self.assertEqual((result["interpolate"], result["constraints"], result["collision_detection"]), (0, 0, 0))

    def test_kinematic_and_no_gravity_survive_as_booleans(self):
        result = P.build_rigidbody(rigidbody_reader({"m_UseGravity": 0, "m_IsKinematic": 1}), ctx=None)
        self.assertIs(result["use_gravity"], False)
        self.assertIs(result["is_kinematic"], True)


class AugmentSceneTests(unittest.TestCase):
    def test_tags_matching_owner_with_presence_marker(self):
        file, scene = fixture()
        self.assertEqual(P.augment_scene(scene, file), 1)
        component = scene["objects"][0]["components"][-1]
        self.assertEqual(component, {"type": "SmallPhysicsObject", "schema_version": 1, "enabled": True})

    def test_records_disabled_state(self):
        file, scene = fixture(enabled=False)
        P.augment_scene(scene, file)
        self.assertFalse(scene["objects"][0]["components"][-1]["enabled"])

    def test_idempotent_second_pass_adds_zero(self):
        file, scene = fixture()
        P.augment_scene(scene, file)
        original = deepcopy(scene)
        self.assertEqual(P.augment_scene(scene, file), 0)
        self.assertEqual(scene, original)

    def test_conflicting_duplicate_component_rejected(self):
        file, scene = fixture()
        P.augment_scene(scene, file)
        scene["objects"][0]["components"][-1]["enabled"] = False
        with self.assertRaisesRegex(HardExtractError, "conflicting existing"):
            P.augment_scene(scene, file)

    def test_unrelated_or_null_script_is_ignored(self):
        file, scene = fixture()
        file.objects[20] = mono_reader(20, script_reader("SomeOtherClass"), 10)
        self.assertEqual(P.augment_scene(scene, file), 0)
        file.objects[20] = mono_reader(20, NS(path_id=0), 10)  # null script
        self.assertEqual(P.augment_scene(scene, file), 0)

    def test_namespaced_class_still_matches_by_short_name(self):
        # Real SmallPhysicsObject is plain/non-namespaced (verified this spike) -- this only proves
        # the short-name match itself is correct, in case that ever changes.
        file, scene = fixture()
        file.objects[20] = mono_reader(20, script_reader("SmallPhysicsObject", namespace="Some.Namespace"), 10)
        self.assertEqual(P.augment_scene(scene, file), 1)

    def test_owner_missing_from_scene_is_rejected(self):
        file, scene = fixture()
        scene["objects"] = []
        with self.assertRaisesRegex(HardExtractError, "missing or duplicate"):
            P.augment_scene(scene, file)

    def test_duplicate_owner_across_two_components_is_rejected(self):
        file, scene = fixture()
        second = mono_reader(21, script_reader("SmallPhysicsObject"), 10)
        file.objects[21] = second
        with self.assertRaisesRegex(HardExtractError, "missing or duplicate"):
            P.augment_scene(scene, file)

    def test_cross_file_owner_is_rejected(self):
        file, scene = fixture()
        file.objects[20] = mono_reader(20, script_reader("SmallPhysicsObject"), 10, gameobject_file_id=1)
        with self.assertRaisesRegex(HardExtractError, "no scene-local owner"):
            P.augment_scene(scene, file)

    def test_orphaned_marker_with_no_source_counterpart_is_rejected(self):
        file, scene = fixture()
        scene["objects"][0]["components"].append(
            {"type": "SmallPhysicsObject", "schema_version": 1, "enabled": True})
        file.objects = {}  # no source MonoBehaviour claims this marker at all
        with self.assertRaisesRegex(HardExtractError, "no source counterpart"):
            P.augment_scene(scene, file)

    def test_duplicate_scene_object_ids_rejected(self):
        file, scene = fixture()
        scene["objects"].append(deepcopy(scene["objects"][0]))
        with self.assertRaisesRegex(HardExtractError, "duplicate scene object IDs"):
            P.augment_scene(scene, file)


if __name__ == "__main__":
    unittest.main()
