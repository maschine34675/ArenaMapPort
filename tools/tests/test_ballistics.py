"""Portable synthetic source/target fixtures; no extracted game data in the tests."""
from copy import deepcopy
import hashlib
import math
from pathlib import Path
import struct
import sys
from types import SimpleNamespace as NS
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_ballistics as B
from common_ids import HardExtractError


def f32(number):
    return struct.unpack("<f", struct.pack("<f", number))[0]


def presets(material_offset=0, target=False):
    names = ["None", "MetalThin", "WoodThin", "Concrete", "Glass", "Cardboard", "Cardboard_MedPen"]
    if target:
        names = [names[i] for i in (0, 4, 3, 2, 1, 5, 6)]
    source_ids = {"None": 0, "MetalThin": 101, "WoodThin": 102, "Concrete": 103, "Glass": 104, "Cardboard": 105}
    result = []
    for index, name in enumerate(names):
        canonical = name.split("_")[0]
        values = [0., 0., 0., 0., 0., 0.] if name == "None" else [12., .25, .375, .5, .625, .75]
        result.append({"index": index, "name": name, "material_id": source_ids[canonical] + material_offset,
                       "values": dict(zip(B.VALUE_FIELDS, values)), "depend_on_scale": False})
    return result


def table_bytes(rows, prefix=b"TEST" * 3):
    data = bytearray(prefix + struct.pack("<i", len(rows)))
    for row in rows:
        name = row["name"].encode("ascii")
        data += struct.pack("<i", len(name)) + name
        data += b"\0" * (-len(data) % 4)
        data += struct.pack("<ii6fiiiii", row["material_id"], 6,
                            *[row["values"][f] for f in B.VALUE_FIELDS], 0, 0, 2, 2, 4)
    return bytes(data)


def contract():
    return {"schema_version": 1, "source_presets": B.parse_preset_table(table_bytes(presets())),
            "target_presets": B.parse_preset_table(table_bytes(presets(500, target=True)))}


def raw(owner=10, enabled=True, material=102, preset=2, values=None, name=b"", hit=0, net=0, surface=0):
    if values is None:
        values = [2.5, .125, .25, .375, .5, .625]  # intentional per-object preset override
    result = bytearray(struct.pack("<iqB3xiqi", 0, owner, int(enabled), 1, 99, len(name)) + name)
    result += b"\0" * (-len(result) % 4)
    result += struct.pack("<5i6f", surface, preset, net, hit, material, *values)
    return bytes(result)


def fixture(active=False, enabled=True, collider_enabled=False, trigger=True, role=0):
    source_raw = raw(enabled=enabled, material=104 if role else 102, preset=4 if role else 2,
                     hit=role, net=77 if role else 0)
    script = NS(path_id=99, read_typetree=lambda: {"m_ClassName": "BallisticCollider", "m_Namespace": "EFT.Ballistics"})
    reader = NS(path_id=20, type=NS(name="MonoBehaviour"), get_raw_data=lambda: source_raw,
                parse_monobehaviour_head=lambda: NS(m_Script=script, m_GameObject=NS(path_id=10)))
    tree = {"m_IsActive": active, "m_Component": [{"component": {"m_PathID": p}} for p in (20, 30)]}
    owner = NS(path_id=10, type=NS(name="GameObject"), read_typetree=lambda: tree)
    physics_tree = {"m_Enabled": collider_enabled, "m_IsTrigger": trigger}
    collider = NS(path_id=30, type=NS(name="BoxCollider"), read_typetree=lambda: physics_tree)
    file = NS(name="level100", objects={10: owner, 20: reader, 30: collider})
    scene = {"scene_name": "Synthetic", "objects": [{"id": "10", "name": "same name",
              "active": active, "parent_id": None, "components": [
                  {"type": "BoxCollider", "enabled": collider_enabled, "is_trigger": trigger, "size": [1, 2, 3]}]}]}
    return file, scene, source_raw, tree, physics_tree


class BallisticsTests(unittest.TestCase):
    def test_preset_parser_extracts_bounded_named_table(self):
        result = B.parse_preset_table(table_bytes(presets()))
        self.assertEqual(result["offset"], 12)
        self.assertEqual(result["presets"], presets())
        self.assertEqual(result["byte_length"], len(table_bytes(presets())) - 12)

    def test_unknown_scale_curve_and_ambiguous_tables_fail_closed(self):
        encoded = table_bytes(presets(), b"")
        mutated = bytearray(encoded)
        # None has a four-byte name: dependOnScale is at offset 44 from first row.
        struct.pack_into("<i", mutated, 44, 1)
        for payload in (bytes(mutated), encoded + encoded, encoded[:-1], b"random data"):
            with self.subTest(length=len(payload)), self.assertRaises(HardExtractError):
                B.parse_preset_table(payload)

    def test_canonical_names_map_different_numeric_ids_and_reordered_presets(self):
        file, scene, source_raw, _, _ = fixture()
        self.assertEqual(B.augment_scene(scene, file, contract()), 1)
        component = scene["objects"][0]["components"][-1]
        self.assertEqual((component["material"], component["target_material_id"], component["target_preset_id"]),
                         ("WoodThin", 602, 3))
        self.assertEqual(component["source"]["material_id"], 102)
        self.assertEqual(component["source"]["preset_id"], 2)
        self.assertEqual(component["source"]["raw_sha256"], hashlib.sha256(source_raw).hexdigest())

    def test_per_collider_override_survives_and_states_remain_unchanged(self):
        file, scene, _, _, _ = fixture(enabled=False)
        original = deepcopy(scene)
        B.augment_scene(scene, file, contract())
        owner = scene["objects"][0]
        result = owner["components"].pop()
        self.assertEqual(scene, original)
        self.assertFalse(result["enabled"])
        self.assertEqual(result["values"], dict(zip(B.VALUE_FIELDS, [2.5, .125, .25, .375, .5, .625])))
        self.assertNotEqual(result["values"], contract()["source_presets"]["presets"][2]["values"])

    def test_variant_preset_is_not_a_material_enum(self):
        file, scene, _, _, _ = fixture()
        file.objects[20].get_raw_data = lambda: raw(material=105, preset=6)
        B.augment_scene(scene, file, contract())
        c = scene["objects"][0]["components"][-1]
        self.assertEqual((c["preset_name"], c["material"], c["target_material_id"]),
                         ("Cardboard_MedPen", "Cardboard", 605))

    def test_legacy_preset_and_window_provenance_are_preserved(self):
        file, scene, _, _, _ = fixture(role=2)
        file.objects[20].get_raw_data = lambda: raw(material=104, preset=-900, hit=2, net=77)
        B.augment_scene(scene, file, contract())
        c = scene["objects"][0]["components"][-1]
        self.assertIsNone(c["preset_name"])
        self.assertEqual(c["target_preset_id"], -900)
        self.assertEqual((c["source"]["hit_type_id"], c["source"]["net_id"]), (2, 77))
        self.assertNotIn("WindowBreaker", str(c))

    def test_idempotent_matching_second_pass_and_conflicting_duplicate(self):
        file, scene, _, _, _ = fixture()
        B.augment_scene(scene, file, contract())
        original = deepcopy(scene)
        self.assertEqual(B.augment_scene(scene, file, contract()), 0)
        self.assertEqual(scene, original)
        scene["objects"][0]["components"][-1]["values"]["penetration_level"] = 999
        with self.assertRaisesRegex(HardExtractError, "conflicting existing"):
            B.augment_scene(scene, file, contract())

    def test_nonempty_mono_name_alignment(self):
        decoded = B.decode_payload(raw(name=b"abcde"))
        self.assertEqual(decoded["owner_id"], 10)
        self.assertEqual(decoded["values"]["penetration_level"], 2.5)

    def test_unknown_or_truncated_layout_rejected(self):
        for payload in (b"", raw()[:-4], raw() + b"\0\0\0\0"):
            with self.assertRaises(HardExtractError):
                B.decode_payload(payload)

    def test_nonfinite_or_invalid_probabilities_rejected(self):
        for value in (math.nan, math.inf, -0.125, 1.125):
            with self.subTest(value=value), self.assertRaises(HardExtractError):
                B.decode_payload(raw(values=[5, value, .25, .25, .25, .25]))

    def test_unrecognized_material_preset_role_and_stale_sound_are_rejected(self):
        cases = ({"material": 999}, {"preset": 999}, {"preset": 1}, {"hit": 5},
                 {"net": 5}, {"surface": 99})
        for kwargs in cases:
            file, scene, _, _, _ = fixture()
            file.objects[20].get_raw_data = lambda k=kwargs: raw(**k)
            with self.subTest(kwargs=kwargs), self.assertRaises(HardExtractError):
                B.augment_scene(scene, file, contract())
            self.assertEqual(len(scene["objects"][0]["components"]), 1)

    def test_changed_active_or_physics_state_fails_before_mutation(self):
        for change in ("active", "collider_enabled", "trigger", "missing_physics"):
            file, scene, _, _, _ = fixture()
            if change == "active":
                scene["objects"][0]["active"] = True
            elif change == "missing_physics":
                scene["objects"][0]["components"] = []
            else:
                key = "enabled" if change == "collider_enabled" else "is_trigger"
                scene["objects"][0]["components"][0][key] = not scene["objects"][0]["components"][0][key]
            original = deepcopy(scene)
            with self.subTest(change=change), self.assertRaises(HardExtractError):
                B.augment_scene(scene, file, contract())
            self.assertEqual(scene, original)

    def test_a_ballistic_component_on_an_owner_without_a_physical_collider_fails_closed(self):
        # AirPit's level38 has four of these (video_player_LOD0). The plain augmenter must keep refusing
        # them; scene_select.ScopedFile hides and records them for partial scenes (test_scene_select).
        file, scene, _, tree, _ = fixture()
        tree["m_Component"] = [{"component": {"m_PathID": 20}}]
        scene["objects"][0]["components"] = []
        with self.assertRaisesRegex(HardExtractError, "owner physics or state differ"):
            B.augment_scene(scene, file, contract())
        self.assertEqual(scene["objects"][0]["components"], [])

    def test_owner_missing_or_unlinked_component_fails(self):
        file, scene, _, tree, _ = fixture()
        tree["m_Component"] = [{"component": {"m_PathID": 30}}]
        with self.assertRaisesRegex(HardExtractError, "not linked"):
            B.augment_scene(scene, file, contract())
        scene["objects"] = []
        with self.assertRaisesRegex(HardExtractError, "missing or duplicate"):
            B.augment_scene(scene, file, contract())

    def test_second_invalid_component_does_not_partially_append_first(self):
        file, scene, _, _, _ = fixture()
        second = deepcopy(file.objects[20])
        second.path_id = 21
        second.get_raw_data = lambda: raw(owner=999)
        file.objects[21] = second
        original = deepcopy(scene)
        with self.assertRaises(HardExtractError):
            B.augment_scene(scene, file, contract())
        self.assertEqual(scene, original)

    def test_unknown_or_inconsistent_named_preset_mapping_rejected(self):
        for mutation in ("missing", "different_material", "ambiguous_canonical"):
            c = contract()
            if mutation == "missing":
                c["target_presets"]["presets"].pop()
            elif mutation == "different_material":
                c["target_presets"]["presets"][-1]["material_id"] = 602
            else:
                c["source_presets"]["presets"][2]["material_id"] = 101
            with self.subTest(mutation=mutation), self.assertRaises(HardExtractError):
                B._bindings(c)

    def test_malformed_contract_types_fail_closed(self):
        for field, value in (("index", True), ("name", None), ("material_id", True),
                             ("material_id", -1), ("values", []), ("depend_on_scale", 0)):
            c = contract()
            c["source_presets"]["presets"][0][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(HardExtractError):
                B._bindings(c)
        for value in (True, "0", None, 1e300):
            c = contract()
            c["target_presets"]["presets"][0]["values"]["penetration_level"] = value
            with self.subTest(value=value), self.assertRaises(HardExtractError):
                B._bindings(c)
        for c in ({"schema_version": True}, {"schema_version": 1},
                  {"schema_version": 1, "source_presets": {"presets": []}}):
            with self.subTest(contract=c), self.assertRaises(HardExtractError):
                B._bindings(c)

    def test_numeric_activation_and_physics_flags_are_not_booleans(self):
        for key in ("active", "enabled", "is_trigger"):
            file, scene, _, _, _ = fixture()
            target = scene["objects"][0] if key == "active" else scene["objects"][0]["components"][0]
            target[key] = int(target[key])
            original = deepcopy(scene)
            with self.subTest(key=key), self.assertRaises(HardExtractError):
                B.augment_scene(scene, file, contract())
            self.assertEqual(scene, original)

    def test_unrelated_or_null_script_is_ignored_and_broken_reference_fails(self):
        file, scene, _, _, _ = fixture()
        file.objects[20].parse_monobehaviour_head = lambda: NS(m_Script=NS(path_id=0))
        self.assertEqual(B.augment_scene(scene, file, contract()), 0)
        file.objects[20].parse_monobehaviour_head = lambda: NS(m_Script=NS(path_id=1,
            read_typetree=lambda: {"m_ClassName": "", "m_Namespace": ""}))
        self.assertEqual(B.augment_scene(scene, file, contract()), 0)
        file.objects[20].parse_monobehaviour_head = lambda: NS(m_Script=NS(path_id=1, read_typetree=lambda: {}))
        with self.assertRaisesRegex(HardExtractError, "cannot resolve MonoScript"):
            B.augment_scene(scene, file, contract())

    def test_load_contract_uses_only_explicit_source_and_target_paths(self):
        tables = [contract()["source_presets"], contract()["target_presets"]]
        with patch.object(B, "_read_presets", side_effect=tables) as read:
            result = B.load_contract("source-root", "target-root")
        self.assertEqual(result["schema_version"], 1)
        self.assertEqual([c.args for c in read.call_args_list], [("source-root",), ("target-root",)])


if __name__ == "__main__":
    unittest.main()
