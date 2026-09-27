"""Synthetic byte-layout fixtures for extract_light_culling.py; no extracted game data in the
tests (pattern of test_physics_props.py)."""
from copy import deepcopy
from pathlib import Path
import struct
import sys
from types import SimpleNamespace as NS
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_light_culling as L  # noqa: E402
from common_ids import HardExtractError  # noqa: E402


# ---------------------------------------------------------------------------------------------
# Raw-byte builders, mirroring the real layout documented in extract_light_culling.py.
# ---------------------------------------------------------------------------------------------

def _pptr(file_id=0, path_id=0):
    return struct.pack("<iq", file_id, path_id)


def _bool(value):
    return struct.pack("<B3x", 1 if value else 0)


def _f32(value):
    return struct.pack("<f", value)


def _i32(value):
    return struct.pack("<i", value)


def _vector3(x=0.0, y=0.0, z=0.0):
    return struct.pack("<3f", x, y, z)


def _vector2(x=0.0, y=0.0):
    return struct.pack("<2f", x, y)


def _keyframe(time=0.0, value=0.0, in_slope=0.0, out_slope=0.0, weighted_mode=0, in_weight=0.0, out_weight=0.0):
    return struct.pack("<4f", time, value, in_slope, out_slope) + struct.pack("<i", weighted_mode) + \
        struct.pack("<2f", in_weight, out_weight)


def _anim_curve(keys, pre_infinity=2, post_infinity=2, rotation_order=4):
    out = _i32(len(keys))
    for key in keys:
        out += _keyframe(**key)
    out += _i32(pre_infinity) + _i32(post_infinity) + _i32(rotation_order)
    return out


DEFAULT_FADE_KEYS = (
    {"time": 0.0, "value": 1.0, "in_slope": 0.0, "out_slope": 0.0, "weighted_mode": 0, "in_weight": 0.0, "out_weight": 0.0},
    {"time": 1.0, "value": 0.0, "in_slope": 0.0, "out_slope": 0.0, "weighted_mode": 0, "in_weight": 0.0, "out_weight": 0.0},
)


def _mb_header(game_object_path_id=10, enabled=True, script_path_id=99, name=b""):
    header = _pptr(0, game_object_path_id) + _bool(enabled) + _pptr(0, script_path_id)
    header += _i32(len(name)) + name
    header += bytes((-len(header)) % 4)
    return header


def culling_object_base(cull_distance=65.0, radius=10.0, shift=(0.0, 0.0, 0.0), draw_sphere=True,
                         cull_by_distance_only=True, comp_count=0, comp_default_count=0, go_count=0,
                         transform=(0, 1), switch=(25, 25, 25, 25), comp_elems=b"", comp_default_elems=b"",
                         go_elems=b""):
    """The three List<> fields take their element bytes explicitly (comp_elems etc.) so a test can
    build the real 200/204-byte shapes (one PPtr / one {PPtr,bool}) or a mismatching one."""
    out = _f32(cull_distance) + _f32(radius) + _vector3(*shift) + _bool(draw_sphere) + _bool(cull_by_distance_only)
    out += _i32(comp_count) + comp_elems + _i32(comp_default_count) + comp_default_elems + _i32(go_count) + go_elems
    out += _pptr(*transform) + b"".join(_i32(v) for v in switch)
    return out


def culling_light_object_own(light=(0, 3317), fade_start=55.0, fade_end=65.0, keys=DEFAULT_FADE_KEYS,
                              pre_infinity=2, post_infinity=2, rotation_order=4,
                              use_intensity_editor=True, max_intensity=0.89, take_flare=False,
                              cull_shadows=True, shadows_fade_start=12.0, shadows_fade_end=17.0,
                              initial_shadows_mode=0):
    out = _pptr(*light) + _f32(fade_start) + _f32(fade_end)
    out += _anim_curve(list(keys), pre_infinity, post_infinity, rotation_order)
    out += _bool(use_intensity_editor) + _f32(max_intensity) + _bool(take_flare) + _bool(cull_shadows)
    out += _f32(shadows_fade_start) + _f32(shadows_fade_end) + _i32(initial_shadows_mode)
    return out


def raw_culling_light_object(game_object_path_id=10, enabled=True, base_kwargs=None, own_kwargs=None):
    base_kwargs = base_kwargs or {}
    own_kwargs = own_kwargs or {}
    payload = culling_object_base(**base_kwargs) + culling_light_object_own(**own_kwargs)
    return _mb_header(game_object_path_id, enabled) + payload


def raw_volumetric_light(game_object_path_id=10, enabled=True, sample_count=3, scattering_coef=0.9,
                          extinction_coef=0.0086, skybox_extinction_coef=0.417, mie_g=0.0,
                          height_fog=False, height_scale=0.1, ground_level=0.0, noise=False,
                          noise_scale=0.015, noise_intensity=1.0, noise_intensity_offset=0.3,
                          noise_velocity=(3.0, 3.0), max_ray_length=400.0):
    payload = _i32(sample_count) + _f32(scattering_coef) + _f32(extinction_coef) + _f32(skybox_extinction_coef)
    payload += _f32(mie_g) + _bool(height_fog) + _f32(height_scale) + _f32(ground_level) + _bool(noise)
    payload += _f32(noise_scale) + _f32(noise_intensity) + _f32(noise_intensity_offset)
    payload += _vector2(*noise_velocity) + _f32(max_ray_length)
    return _mb_header(game_object_path_id, enabled) + payload


def raw_light_flicker(game_object_path_id=10, enabled=True, frequency=1.0, intensity=1.0,
                       intensity_shift=0.0, time_shift=0.0, keys=None, pre_infinity=1, post_infinity=1,
                       rotation_order=4, generate=0, random_time_shift=False, full_random_curve=False,
                       culling_coef=1.0, light=(0, 3373)):
    if keys is None:
        keys = [{"time": 0.0, "value": 0.5, "in_slope": 1.0, "out_slope": 1.0}]
    payload = _f32(frequency) + _f32(intensity) + _f32(intensity_shift) + _f32(time_shift)
    payload += _anim_curve(keys, pre_infinity, post_infinity, rotation_order)
    payload += _i32(generate) + _bool(random_time_shift) + _bool(full_random_curve) + _f32(culling_coef)
    payload += _pptr(*light)
    return _mb_header(game_object_path_id, enabled) + payload


# ---------------------------------------------------------------------------------------------
# Decode tests.
# ---------------------------------------------------------------------------------------------

class DecodeCullingLightObjectTests(unittest.TestCase):
    def test_decodes_verified_default_shape(self):
        raw = raw_culling_light_object()
        result = L.decode_culling_light_object(raw)
        self.assertEqual(result, {
            "type": "CullingLightObject", "schema_version": 2, "enabled": True,
            "radius": 10.0, "shift": [0.0, 0.0, 0.0],
            "cull_by_distance_only": True, "fade_start_distance": 55.0, "fade_end_distance": 65.0,
            "use_light_intensity_from_editor": True, "max_light_intensity": _f32_roundtrip(0.89),
            "take_flare_parameters_from_this_script": False, "cull_shadows_by_distance": True,
            "shadows_fade_start_distance": 12.0, "shadows_fade_end_distance": 17.0,
            "initial_shadows_mode": 0,
            "turn_off_component_count": 0, "turn_off_object_count": 0,
        })

    def test_culling_sphere_radius_and_shift_are_exported(self):
        # Equator's atrium floodlights: Spot lights with _cullByDistanceOnly=false, whose authored sphere must
        # cover the lit floor, not the lamp (user report 2026-09-27).
        raw = raw_culling_light_object(base_kwargs={"radius": 6.5, "shift": (0.0, -1.25, 3.5),
                                                    "cull_by_distance_only": False})
        result = L.decode_culling_light_object(raw)
        self.assertEqual(result["schema_version"], 2)
        self.assertAlmostEqual(result["radius"], 6.5, places=5)
        self.assertEqual([round(v, 5) for v in result["shift"]], [0.0, -1.25, 3.5])
        self.assertFalse(result["cull_by_distance_only"])

    def test_disabled_state_and_cull_by_distance_only_survive(self):
        raw = raw_culling_light_object(enabled=False, base_kwargs={"cull_by_distance_only": False})
        result = L.decode_culling_light_object(raw)
        self.assertFalse(result["enabled"])
        self.assertFalse(result["cull_by_distance_only"])

    def test_tolerates_non_uniform_fade_curve_weight(self):
        # Real Equator/Bay5 data: inWeight/outWeight vary (0.0 vs Unity's default 1/3) even on
        # otherwise-uniform curves, harmless because weighted_mode == 0 everywhere -- must not fail.
        keys = tuple(dict(k, in_weight=0.3333333432674408, out_weight=0.3333333432674408) for k in DEFAULT_FADE_KEYS)
        raw = raw_culling_light_object(own_kwargs={"keys": keys})
        L.decode_culling_light_object(raw)  # must not raise

    def test_one_component_to_turn_off_decodes_as_the_200_byte_shape(self):
        raw = raw_culling_light_object(base_kwargs={"comp_count": 1, "comp_elems": _pptr(0, 4444)})
        self.assertEqual(len(raw), 32 + 200)
        comp = L.decode_culling_light_object(raw)
        self.assertEqual(comp["turn_off_component_count"], 1)
        self.assertEqual(comp["turn_off_object_count"], 0)
        self.assertAlmostEqual(comp["max_light_intensity"], 0.89, places=5)

    def test_one_default_state_decodes_as_the_204_byte_shape(self):
        raw = raw_culling_light_object(base_kwargs={"comp_default_count": 1,
                                                    "comp_default_elems": _pptr(0, 4444) + _bool(True)})
        self.assertEqual(len(raw), 32 + 204)
        comp = L.decode_culling_light_object(raw)
        self.assertEqual(comp["turn_off_component_count"], 1)
        self.assertEqual(comp["initial_shadows_mode"], 0)

    def test_all_three_lists_populated_decode_with_summed_counts(self):
        raw = raw_culling_light_object(base_kwargs={
            "comp_count": 1, "comp_elems": _pptr(0, 4444),
            "comp_default_count": 1, "comp_default_elems": _pptr(0, 4444) + _bool(False),
            "go_count": 1, "go_elems": _pptr(0, 5555)})
        self.assertEqual(len(raw), 32 + 228)
        comp = L.decode_culling_light_object(raw)
        self.assertEqual(comp["turn_off_component_count"], 2)
        self.assertEqual(comp["turn_off_object_count"], 1)

    def test_list_count_outside_sane_range_is_hard_failure(self):
        raw = raw_culling_light_object(base_kwargs={"comp_count": 1000})
        with self.assertRaisesRegex(HardExtractError, "sane range"):
            L.decode_culling_light_object(raw)

    def test_misdeclared_list_element_size_is_hard_failure(self):
        # A count of 1 with no element bytes shifts every later field: the size guards trip.
        raw = raw_culling_light_object(base_kwargs={"comp_count": 1})
        with self.assertRaises(HardExtractError):
            L.decode_culling_light_object(raw)

    def test_fade_curve_key_count_mismatch_is_hard_failure(self):
        raw = raw_culling_light_object(own_kwargs={"keys": DEFAULT_FADE_KEYS + ({"time": 2.0, "value": 0.0},)})
        with self.assertRaisesRegex(HardExtractError, "key\\(s\\), expected"):
            L.decode_culling_light_object(raw)

    def test_fade_curve_wrap_mode_mismatch_is_hard_failure(self):
        raw = raw_culling_light_object(own_kwargs={"pre_infinity": 1})
        with self.assertRaisesRegex(HardExtractError, "wrap mode"):
            L.decode_culling_light_object(raw)

    def test_fade_curve_rotation_order_mismatch_is_hard_failure(self):
        raw = raw_culling_light_object(own_kwargs={"rotation_order": 0})
        with self.assertRaisesRegex(HardExtractError, "m_RotationOrder"):
            L.decode_culling_light_object(raw)

    def test_fade_curve_key_value_mismatch_is_hard_failure(self):
        keys = (dict(DEFAULT_FADE_KEYS[0], value=0.5), DEFAULT_FADE_KEYS[1])
        raw = raw_culling_light_object(own_kwargs={"keys": keys})
        with self.assertRaisesRegex(HardExtractError, "!= uniform default"):
            L.decode_culling_light_object(raw)

    def test_fade_curve_weighted_mode_mismatch_is_hard_failure(self):
        keys = (dict(DEFAULT_FADE_KEYS[0], weighted_mode=1), DEFAULT_FADE_KEYS[1])
        raw = raw_culling_light_object(own_kwargs={"keys": keys})
        with self.assertRaisesRegex(HardExtractError, "!= uniform default"):
            L.decode_culling_light_object(raw)

    def test_truncated_payload_is_hard_failure(self):
        raw = raw_culling_light_object()[:-4]
        with self.assertRaisesRegex(HardExtractError, "truncated read"):
            L.decode_culling_light_object(raw)

    def test_trailing_bytes_are_hard_failure(self):
        raw = raw_culling_light_object() + b"\x00\x00\x00\x00"
        with self.assertRaisesRegex(HardExtractError, "trailing byte"):
            L.decode_culling_light_object(raw)


class DecodeVolumetricLightTests(unittest.TestCase):
    def test_decodes_every_field_in_declaration_order(self):
        raw = raw_volumetric_light()
        result = L.decode_volumetric_light(raw)
        self.assertEqual(result, {
            "type": "VolumetricLight", "schema_version": 1, "enabled": True,
            "sample_count": 3, "scattering_coef": _f32_roundtrip(0.9), "extinction_coef": _f32_roundtrip(0.0086),
            "skybox_extinction_coef": _f32_roundtrip(0.417), "mie_g": 0.0, "height_fog": False,
            "height_scale": _f32_roundtrip(0.1), "ground_level": 0.0, "noise": False,
            "noise_scale": _f32_roundtrip(0.015), "noise_intensity": 1.0,
            "noise_intensity_offset": _f32_roundtrip(0.3), "noise_velocity": [3.0, 3.0],
            "max_ray_length": 400.0,
        })

    def test_disabled_state_survives(self):
        result = L.decode_volumetric_light(raw_volumetric_light(enabled=False))
        self.assertFalse(result["enabled"])

    def test_size_mismatch_is_hard_failure(self):
        with self.assertRaisesRegex(HardExtractError, "trailing byte"):
            L.decode_volumetric_light(raw_volumetric_light() + b"\x00\x00\x00\x00")
        with self.assertRaisesRegex(HardExtractError, "truncated read"):
            L.decode_volumetric_light(raw_volumetric_light()[:-1])


class DecodeLightFlickerTests(unittest.TestCase):
    def test_decodes_curve_generate_and_culling_coef(self):
        keys = [{"time": 0.0, "value": 0.169, "in_slope": 81.4, "out_slope": 81.4},
                {"time": 1.0, "value": 0.169, "in_slope": 81.4, "out_slope": 81.4}]
        raw = raw_light_flicker(keys=keys)
        result = L.decode_light_flicker(raw)
        self.assertEqual(result["type"], "LightFlicker")
        self.assertTrue(result["enabled"])
        self.assertEqual((result["frequency"], result["intensity"], result["intensity_shift"], result["time_shift"]),
                         (1.0, 1.0, 0.0, 0.0))
        self.assertEqual(result["curve"]["pre_infinity"], 1)
        self.assertEqual(result["curve"]["post_infinity"], 1)
        self.assertEqual(len(result["curve"]["keys"]), 2)
        self.assertEqual(result["curve"]["keys"][0]["weighted_mode"], 0)
        self.assertEqual((result["generate"], result["random_time_shift"], result["full_random_curve"],
                          result["culling_coef"]), (0, False, False, 1.0))
        # rotation_order/own trailing _light PPtr are consumed but never exported:
        self.assertNotIn("rotation_order", result["curve"])
        self.assertNotIn("light", result)

    def test_realistic_24_key_curve_round_trips(self):
        keys = [{"time": i / 23.0, "value": 0.5 + 0.4 * ((-1) ** i), "in_slope": 0.1 * i,
                 "out_slope": -0.1 * i, "weighted_mode": 0, "in_weight": 0.0, "out_weight": 0.0}
                for i in range(24)]
        raw = raw_light_flicker(keys=keys)
        self.assertEqual(len(raw), 32 + 732)  # the real Equator payload size (24-key curve)
        comp = L.decode_light_flicker(raw)
        self.assertEqual(len(comp["curve"]["keys"]), 24)
        for i, key in enumerate(comp["curve"]["keys"]):
            self.assertAlmostEqual(key["time"], i / 23.0, places=5)
            self.assertAlmostEqual(key["in_slope"], 0.1 * i, places=5)
        self.assertEqual((comp["curve"]["pre_infinity"], comp["curve"]["post_infinity"]), (1, 1))

    def test_unknown_wrap_code_is_hard_failure(self):
        raw = raw_light_flicker(pre_infinity=99)
        with self.assertRaisesRegex(HardExtractError, "unknown AnimationCurve wrap code"):
            L.decode_light_flicker(raw)
        raw = raw_light_flicker(post_infinity=0)
        with self.assertRaisesRegex(HardExtractError, "unknown AnimationCurve wrap code"):
            L.decode_light_flicker(raw)

    def test_size_mismatch_is_hard_failure(self):
        with self.assertRaisesRegex(HardExtractError, "trailing byte"):
            L.decode_light_flicker(raw_light_flicker() + b"\x00\x00\x00\x00")
        with self.assertRaisesRegex(HardExtractError, "truncated read"):
            L.decode_light_flicker(raw_light_flicker()[:-4])


def _f32_roundtrip(value):
    """The exact float a real decode produces for this literal (f32 precision loss included) --
    matches _f32()'s own struct.pack("<f", ...) truncation so assertEqual compares like for like."""
    return struct.unpack("<f", struct.pack("<f", value))[0]


# ---------------------------------------------------------------------------------------------
# augment_scene tests.
# ---------------------------------------------------------------------------------------------

def script_reader(class_name, namespace="", path_id=99):
    return NS(path_id=path_id, read_typetree=lambda: {"m_ClassName": class_name, "m_Namespace": namespace})


def mono_reader(path_id, script, gameobject_path_id, gameobject_file_id=0, enabled=True, raw=b""):
    head = NS(m_Script=script,
              m_GameObject=(NS(path_id=gameobject_path_id, file_id=gameobject_file_id)
                            if gameobject_path_id is not None else None),
              m_Enabled=enabled)
    return NS(path_id=path_id, type=NS(name="MonoBehaviour"), parse_monobehaviour_head=lambda: head,
              get_raw_data=lambda: raw)


def fixture(owner_id="10"):
    scene = {"scene_name": "Synthetic", "objects": [{"id": owner_id, "name": "Light (1)", "components": []}]}
    unity_file = NS(name="level55", objects={})
    return unity_file, scene


class AugmentSceneTests(unittest.TestCase):
    def test_tags_all_three_classes_on_one_owner(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("CullingLightObject"), 10,
                                             raw=raw_culling_light_object(10))
        unity_file.objects[21] = mono_reader(21, script_reader("VolumetricLight"), 10,
                                             raw=raw_volumetric_light(10))
        unity_file.objects[22] = mono_reader(22, script_reader("LightFlicker", namespace="EFT.Visual"), 10,
                                             raw=raw_light_flicker(10))
        counts = L.augment_scene(scene, unity_file)
        self.assertEqual(counts, {"culling_light_object_count": 1, "volumetric_light_count": 1,
                                  "light_flicker_count": 1})
        types = {c["type"] for c in scene["objects"][0]["components"]}
        self.assertEqual(types, {"CullingLightObject", "VolumetricLight", "LightFlicker"})

    def test_idempotent_second_pass_adds_zero(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("CullingLightObject"), 10,
                                             raw=raw_culling_light_object(10))
        L.augment_scene(scene, unity_file)
        original = deepcopy(scene)
        counts = L.augment_scene(scene, unity_file)
        self.assertEqual(counts, {"culling_light_object_count": 0, "volumetric_light_count": 0,
                                  "light_flicker_count": 0})
        self.assertEqual(scene, original)

    def test_conflicting_duplicate_component_rejected(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("CullingLightObject"), 10,
                                             raw=raw_culling_light_object(10))
        L.augment_scene(scene, unity_file)
        scene["objects"][0]["components"][-1]["enabled"] = False
        with self.assertRaisesRegex(HardExtractError, "conflicting existing"):
            L.augment_scene(scene, unity_file)

    def test_unrelated_or_null_script_is_ignored(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("SomeOtherClass"), 10)
        self.assertEqual(L.augment_scene(scene, unity_file),
                         {"culling_light_object_count": 0, "volumetric_light_count": 0, "light_flicker_count": 0})
        unity_file.objects[20] = mono_reader(20, NS(path_id=0), 10)  # null script
        self.assertEqual(L.augment_scene(scene, unity_file),
                         {"culling_light_object_count": 0, "volumetric_light_count": 0, "light_flicker_count": 0})

    def test_namespaced_light_flicker_matches_by_full_dotted_name(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("LightFlicker", namespace="EFT.Visual"), 10,
                                             raw=raw_light_flicker(10))
        self.assertEqual(L.augment_scene(scene, unity_file)["light_flicker_count"], 1)
        # A same-named class in the WRONG namespace must not match.
        unity_file2, scene2 = fixture()
        unity_file2.objects[20] = mono_reader(20, script_reader("LightFlicker", namespace="Some.Other"), 10,
                                              raw=raw_light_flicker(10))
        self.assertEqual(L.augment_scene(scene2, unity_file2)["light_flicker_count"], 0)

    def test_owner_missing_from_scene_is_rejected(self):
        unity_file, scene = fixture()
        scene["objects"] = []
        unity_file.objects[20] = mono_reader(20, script_reader("VolumetricLight"), 10, raw=raw_volumetric_light(10))
        with self.assertRaisesRegex(HardExtractError, "missing or duplicate"):
            L.augment_scene(scene, unity_file)

    def test_duplicate_owner_across_two_components_of_same_class_is_rejected(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("VolumetricLight"), 10, raw=raw_volumetric_light(10))
        unity_file.objects[21] = mono_reader(21, script_reader("VolumetricLight"), 10, raw=raw_volumetric_light(10))
        with self.assertRaisesRegex(HardExtractError, "missing or duplicate"):
            L.augment_scene(scene, unity_file)

    def test_cross_file_owner_is_rejected(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("VolumetricLight"), 10, gameobject_file_id=1,
                                             raw=raw_volumetric_light(10))
        with self.assertRaisesRegex(HardExtractError, "no scene-local owner"):
            L.augment_scene(scene, unity_file)

    def test_orphaned_marker_with_no_source_counterpart_is_rejected(self):
        unity_file, scene = fixture()
        scene["objects"][0]["components"].append(L.decode_volumetric_light(raw_volumetric_light(10)))
        unity_file.objects = {}  # no source MonoBehaviour claims this marker at all
        with self.assertRaisesRegex(HardExtractError, "no source counterpart"):
            L.augment_scene(scene, unity_file)

    def test_duplicate_scene_object_ids_rejected(self):
        unity_file, scene = fixture()
        scene["objects"].append(deepcopy(scene["objects"][0]))
        with self.assertRaisesRegex(HardExtractError, "duplicate scene object IDs"):
            L.augment_scene(scene, unity_file)

    def test_hard_failure_from_a_single_instance_propagates(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("CullingLightObject"), 10,
                                             raw=raw_culling_light_object(10, base_kwargs={"comp_count": 1}))
        with self.assertRaises(HardExtractError):
            L.augment_scene(scene, unity_file)


# ---------------------------------------------------------------------------------------------
# Manifest-shape sanity (the actual acceptance-validator tests live in
# test_native_features_validation.py, next to validate_native_features.py).
# ---------------------------------------------------------------------------------------------

class ManifestShapeTests(unittest.TestCase):
    def test_counts_compose_into_the_documented_manifest_entry(self):
        unity_file, scene = fixture()
        unity_file.objects[20] = mono_reader(20, script_reader("CullingLightObject"), 10,
                                             raw=raw_culling_light_object(10))
        unity_file.objects[21] = mono_reader(21, script_reader("VolumetricLight"), 10,
                                             raw=raw_volumetric_light(10))
        counts = L.augment_scene(scene, unity_file)
        entry = {"schema_version": L.SCHEMA_VERSION, **counts}
        self.assertEqual(entry, {"schema_version": 1, "culling_light_object_count": 1,
                                 "volumetric_light_count": 1, "light_flicker_count": 0})


if __name__ == "__main__":
    unittest.main()


class CullingSphereRoundtripTests(unittest.TestCase):
    """validate_bundle_roundtrip.culling_light_spheres_in_package: the package-side reference for the
    schema-2 (radius, shift) bundle cross-check (2026-09-27 floodlight fix)."""

    def test_counts_schema2_spheres_and_skips_schema1(self):
        import json, tempfile
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
        import validate_bundle_roundtrip as V
        with tempfile.TemporaryDirectory() as tmp:
            pkg = Path(tmp)
            (pkg / "scenes").mkdir()
            scene = {"objects": [
                {"id": 1, "components": [{"type": "CullingLightObject", "schema_version": 2,
                                          "radius": 24.0, "shift": [0.0, 0.0, 0.0]}]},
                {"id": 2, "components": [{"type": "CullingLightObject", "schema_version": 2,
                                          "radius": 24.0, "shift": [0.0, 0.0, 0.0]}]},
                {"id": 3, "components": [{"type": "CullingLightObject", "schema_version": 2,
                                          "radius": 0.92, "shift": [0.0, -0.5, 1.25]}]},
                {"id": 4, "components": [{"type": "CullingLightObject", "schema_version": 1}]},
            ]}
            (pkg / "scenes" / "S.json").write_text(json.dumps(scene), encoding="utf-8")
            spheres = V.culling_light_spheres_in_package(pkg, {"scenes": ["S"]})
        self.assertEqual(spheres[(24.0, 0.0, 0.0, 0.0)], 2)
        self.assertEqual(spheres[(0.92, 0.0, -0.5, 1.25)], 1)
        self.assertEqual(sum(spheres.values()), 3)
