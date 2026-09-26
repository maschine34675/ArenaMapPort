"""Light-culling native components: CullingLightObject (per-light distance/shadow culling),
VolumetricLight (light-shaft fog volume) and EFT.Visual.LightFlicker (flicker animation) --
PLAN-TWO-MAPS follow-up item, decoded byte-exact from raw MonoBehaviour bytes and verified
2026-09-26 against every real instance on both maps (472+186 CullingLightObject, 17+1
VolumetricLight, 2+0 LightFlicker -- see scratchpad probe_full.py/probe_light_culling.py this
pass, never checked in).

Field order/layout (decompile: D:/SPT41/Development/_decompiled/CullingObject.cs,
CullingLightObject.cs, VolumetricLight.cs, EFT.Visual/Flicker.cs, EFT.Visual/LightFlicker.cs):
for an inherited MonoBehaviour, Unity serializes the BASE class's own fields first, then the
DERIVED class's own fields appended after (confirmed for both CullingLightObject : CullingObject
and, by direct probe since the brief asked to "verify order yourself", LightFlicker : Flicker --
LightFlicker's own `_light` PPtr sits 12 bytes AFTER Flicker's own nine fields, not before).

Generic MonoBehaviour header, same convention as extract_physics_props.py: GameObject PPtr (12 B)
+ m_Enabled (1 B + 3-byte align) + Script PPtr (12 B) + m_Name (int32 length + bytes, 4-aligned;
every real instance here has an empty name, so the header is 32 B in practice, but the length is
always read, never assumed).

CullingObject base fields (declaration order): CullDistance f32, _radius f32, _shift Vector3,
_drawSphere bool, _cullByDistanceOnly bool, _componentsToTurnOff List<PPtr>,
_componentsToTurnOffDefaultState List<{PPtr,bool}> (element layout never reverse-engineered --
unneeded, see below), _gameObjectsToTurnOff List<PPtr>, _transform PPtr, then four
*SwitchPerFrame* int counters. Only CullDistance/_radius/_shift/_drawSphere/_transform/the four
counters are Awake()-recomputed or editor-only and are never exported; _cullByDistanceOnly IS
exported (own runtime-read property). The three List<> fields are consumed and only their counts
are exported (turn_off_component_count / turn_off_object_count): 464/472 Equator + 186/186 Bay5
instances have all three empty, 8 Equator instances carry exactly one element (2 in level57 with a
`_componentsToTurnOffDefaultState` entry = {PPtr, bool} 16 B, 6 in level58 with a
`_componentsToTurnOff` PPtr 12 B), and their 204/200-byte payloads against the 188-byte baseline
confirm those element sizes (see _read_culling_object_lists). The "turn components off while
culled" side feature is not authored by the port.

CullingLightObject's own fields, appended after the base 68 B (with the three lists empty): _light
PPtr, _fadeStartDistance/_fadeEndDistance f32, _fadeCurve AnimationCurve, then five more
bool/float/bool/bool/float/float/int fields ending in _initialShadowsMode (LightShadows enum).
_light is resolved at raid time via the owner's own Light sibling (Awake() does exactly that) and
is never exported. _fadeCurve is meant to always be the AnimationCurve.EaseInOut(0,1,1,0) default
CullingLightObject.cs declares: this module hard-fails if it is not (times/values/slopes/
weightedMode/wrap-mode/rotation-order all default) -- EXCEPT the curve's inWeight/outWeight
fields, which the probe found are NOT byte-uniform (0.0 on most instances, Unity's default 1/3
"kAppleFactor" on others) even though weightedMode == WeightedMode.None on every real instance
makes AnimationCurve.Evaluate() ignore them entirely; this module tolerates that harmless,
functionally-inert variance rather than rejecting it.

AnimationCurve (both this class and LightFlicker's own Curve): int32 key count, then that many
28-byte Keyframes (time/value/inSlope/outSlope f32, weightedMode i32, inWeight/outWeight f32),
then THREE trailing int32s: m_PreInfinity, m_PostInfinity, m_RotationOrder. m_PreInfinity/
m_PostInfinity are Unity's internal wrap-mode codes, not the public WrapMode enum values: 2 =
ClampForever (every CullingLightObject default fade curve), 1 = Loop (both real LightFlicker
curves -- their Time.time-driven input must wrap). Any other code is unknown and a hard failure
(see WRAP_CODE_TO_NAME); the Unity importer maps these same two codes back to WrapMode elsewhere
and is not this module's concern.

VolumetricLight: 14 public fields, declaration order exactly matches VolumetricLight.cs (SampleCount
int, ScatteringCoef/ExtinctionCoef/SkyboxExtinctionCoef/MieG floats, HeightFog bool, HeightScale/
GroundLevel floats, Noise bool, NoiseScale/NoiseIntensity/NoiseIntensityOffset floats, NoiseVelocity
Vector2, MaxRayLength float) -- confirmed byte-exact (60 B payload, fully consumed) against all 18
real instances (17 Equator + 1 Bay5).

LightFlicker: Flicker's own nine fields (Frequency/Intensity/IntensityShift/TimeShift floats, Curve
AnimationCurve, Generate int [EFT.Visual.Flicker.ECurveType], RandomTimeShift/FullRandomCurve bools,
CullingCoef float -- CullingCoef has no [SerializeField] but IS a plain public field, which Unity
serializes by default), then LightFlicker's own _light PPtr appended last (see the base/derived note
above) -- confirmed byte-exact (732 B payload, fully consumed) against both real instances. _light is
dropped for the same reason CullingLightObject's own _light is: resolved via the owner's Light
sibling at raid time, never needed by the ported scene data.

Package schema (component dicts appended to the owner object's "components" list, like
SmallPhysicsObject/NavMeshModifier): see docs given to this module's author for the exact per-class
shape; CullDistance/_radius/_shift/_drawSphere/*SwitchPerFrame*/_transform/_light/_fadeCurve/Curve's
own rotation_order are intentionally never exported (Awake()-recomputed, editor-only, or resolved
via a sibling component at raid time).
"""
from __future__ import annotations

import os
import struct
import sys

from common_ids import HardExtractError

_HERE = os.path.dirname(os.path.abspath(__file__))
_ANALYSIS_SCRIPTS = os.path.normpath(os.path.join(_HERE, "..", "..", "analysis", "scripts"))
for _path in (_HERE, _ANALYSIS_SCRIPTS):  # standalone-importable regardless of caller sys.path setup
    if _path not in sys.path:
        sys.path.insert(0, _path)

import census  # analysis/scripts/census.py -- resolve_script_name()  # noqa: E402

SCHEMA_VERSION = 1

CULLING_LIGHT_OBJECT = "CullingLightObject"
VOLUMETRIC_LIGHT = "VolumetricLight"
LIGHT_FLICKER = "EFT.Visual.LightFlicker"  # namespaced; census resolves the full dotted name

# Unity's internal AnimationCurve wrap-mode codes (m_PreInfinity/m_PostInfinity) -- see module
# docstring. NOT UnityEngine.WrapMode's own enum values.
WRAP_CODE_TO_NAME = {1: "Loop", 2: "ClampForever"}

# CullingLightObject._fadeCurve's verified uniform default (AnimationCurve.EaseInOut(0,1,1,0)):
# inWeight/outWeight deliberately excluded from the comparison (see module docstring).
_FADE_CURVE_DEFAULT_KEYS = (
    {"time": 0.0, "value": 1.0, "in_slope": 0.0, "out_slope": 0.0, "weighted_mode": 0},
    {"time": 1.0, "value": 0.0, "in_slope": 0.0, "out_slope": 0.0, "weighted_mode": 0},
)
_FADE_CURVE_PRE_INFINITY = 2
_FADE_CURVE_POST_INFINITY = 2
_FADE_CURVE_ROTATION_ORDER = 4
_FLOAT_TOL = 1e-4


def _require(condition, message):
    if not condition:
        raise HardExtractError("LightCulling: " + message)


def _floats_close(a, b, tol=_FLOAT_TOL):
    return abs(a - b) <= tol


class _Cursor:
    """Sequential little-endian reader over one MonoBehaviour's raw payload bytes (the part
    after the generic header). Every read is bounds-checked; running past the end of the buffer
    is a HardExtractError, never a raw struct.error."""

    __slots__ = ("buf", "off")

    def __init__(self, buf):
        self.buf = buf
        self.off = 0

    def _take(self, n):
        _require(self.off + n <= len(self.buf),
                 f"truncated read ({n} byte(s) at offset {self.off}, {len(self.buf)} available)")
        start = self.off
        self.off += n
        return start

    def i32(self):
        o = self._take(4)
        return struct.unpack_from("<i", self.buf, o)[0]

    def f32(self):
        o = self._take(4)
        return struct.unpack_from("<f", self.buf, o)[0]

    def boolean(self):
        o = self._take(4)  # bool (1 B) + 3-byte align, kAlignBytes
        return bool(self.buf[o])

    def vector2(self):
        o = self._take(8)
        return struct.unpack_from("<2f", self.buf, o)

    def vector3(self):
        o = self._take(12)
        return struct.unpack_from("<3f", self.buf, o)

    def pptr(self):
        o = self._take(12)
        file_id = struct.unpack_from("<i", self.buf, o)[0]
        path_id = struct.unpack_from("<q", self.buf, o + 4)[0]
        return file_id, path_id

    def keyframe(self):
        o = self._take(28)
        time_, value, in_slope, out_slope = struct.unpack_from("<4f", self.buf, o)
        weighted_mode = struct.unpack_from("<i", self.buf, o + 16)[0]
        in_weight, out_weight = struct.unpack_from("<2f", self.buf, o + 20)
        return {"time": time_, "value": value, "in_slope": in_slope, "out_slope": out_slope,
                "weighted_mode": weighted_mode, "in_weight": in_weight, "out_weight": out_weight}

    def animation_curve(self):
        count = self.i32()
        _require(0 <= count <= 4096, f"AnimationCurve key count {count} out of range")
        keys = [self.keyframe() for _ in range(count)]
        pre_infinity = self.i32()
        post_infinity = self.i32()
        rotation_order = self.i32()
        return count, keys, pre_infinity, post_infinity, rotation_order

    def remaining(self):
        return len(self.buf) - self.off


def _mb_header(raw):
    """Generic MonoBehaviour header -- see module docstring. Returns (enabled, offset past the
    header)."""
    _require(len(raw) >= 28 + 4, "truncated MonoBehaviour header")
    enabled = bool(raw[12])
    off = 12 + 4 + 12
    name_len = struct.unpack_from("<i", raw, off)[0]
    _require(0 <= name_len <= len(raw) - off - 4, "invalid MonoBehaviour m_Name length")
    return enabled, (off + 4 + name_len + 3) & ~3


_MAX_TURN_OFF_LIST = 64  # sanity bound; Arena's largest real list has 1 element


def _read_culling_object_lists(cursor):
    """Consume CullingObject's three List<> fields and return their counts. Elements:
    _componentsToTurnOff = PPtr<Component> (12 B), _componentsToTurnOffDefaultState =
    DisablerCullingObject.ComponentState {Component PPtr 12 B, bool isEnabled 1 B + 3 align} (16 B),
    _gameObjectsToTurnOff = PPtr<GameObject> (12 B). The 8 real Equator instances that carry one
    element (payloads 200/204/216 B against the 188 B baseline) confirm those element sizes. The
    references themselves are not exported: the "turn other components off while culled" side
    feature is not authored by the port (the targets may not even exist in the built scene); only
    the counts are recorded for transparency. Misalignment still fails closed through the
    trailing-byte check in the caller."""
    counts = []
    for list_name, elem_size in (("_componentsToTurnOff", 12), ("_componentsToTurnOffDefaultState", 16),
                                 ("_gameObjectsToTurnOff", 12)):
        count = cursor.i32()
        _require(0 <= count <= _MAX_TURN_OFF_LIST,
                 f"CullingObject.{list_name} count {count} is outside the sane range 0..{_MAX_TURN_OFF_LIST}")
        for _ in range(count):
            cursor.pptr()
            if elem_size == 16:
                cursor.boolean()
        counts.append(count)
    return tuple(counts)


def _require_uniform_fade_curve(key_count, keys, pre_infinity, post_infinity, rotation_order):
    _require(key_count == 2, f"_fadeCurve has {key_count} key(s), expected the uniform 2-key default")
    _require(pre_infinity == _FADE_CURVE_PRE_INFINITY and post_infinity == _FADE_CURVE_POST_INFINITY,
             f"_fadeCurve wrap mode (pre={pre_infinity}, post={post_infinity}) != the verified "
             f"ClampForever default ({_FADE_CURVE_PRE_INFINITY}, {_FADE_CURVE_POST_INFINITY})")
    _require(rotation_order == _FADE_CURVE_ROTATION_ORDER,
             f"_fadeCurve m_RotationOrder {rotation_order} != verified default {_FADE_CURVE_ROTATION_ORDER}")
    for key, expected in zip(keys, _FADE_CURVE_DEFAULT_KEYS):
        _require(
            _floats_close(key["time"], expected["time"]) and _floats_close(key["value"], expected["value"]) and
            _floats_close(key["in_slope"], expected["in_slope"]) and
            _floats_close(key["out_slope"], expected["out_slope"]) and
            key["weighted_mode"] == expected["weighted_mode"],
            f"_fadeCurve key {key} != uniform default {expected} (inWeight/outWeight excluded, see docstring)")


def _curve_dict(keys, pre_infinity, post_infinity):
    """LightFlicker's own Curve, exported in full (unlike the fade curve, there is no known
    default to compare against) -- m_RotationOrder is decoded (to consume the right number of
    bytes) but not exported, matching the schema."""
    _require(pre_infinity in WRAP_CODE_TO_NAME and post_infinity in WRAP_CODE_TO_NAME,
             f"unknown AnimationCurve wrap code (pre={pre_infinity}, post={post_infinity})")
    return {
        "pre_infinity": pre_infinity, "post_infinity": post_infinity,
        "keys": [{"time": k["time"], "value": k["value"], "in_slope": k["in_slope"],
                  "out_slope": k["out_slope"], "weighted_mode": k["weighted_mode"],
                  "in_weight": k["in_weight"], "out_weight": k["out_weight"]} for k in keys],
    }


def decode_culling_light_object(raw):
    enabled, off = _mb_header(raw)
    c = _Cursor(raw[off:])
    c.f32()                        # CullDistance -- Awake() overwrites; not exported
    c.f32()                        # _radius -- Awake() overwrites; not exported
    c.vector3()                    # _shift -- editor-only gizmo offset; not exported
    c.boolean()                    # _drawSphere -- editor-only gizmo toggle; not exported
    cull_by_distance_only = c.boolean()
    turn_off_components, turn_off_default_states, turn_off_objects = _read_culling_object_lists(c)
    c.pptr()                       # _transform -- OnPreProcess()/Awake() re-resolve it; not exported
    c.i32(); c.i32(); c.i32(); c.i32()   # *SwitchPerFrame* counters -- editor-only; not exported

    c.pptr()                       # _light -- resolved via the owner's own Light sibling at raid time
    fade_start_distance = c.f32()
    fade_end_distance = c.f32()
    key_count, keys, pre_infinity, post_infinity, rotation_order = c.animation_curve()
    _require_uniform_fade_curve(key_count, keys, pre_infinity, post_infinity, rotation_order)
    use_light_intensity_from_editor = c.boolean()
    max_light_intensity = c.f32()
    take_flare_parameters_from_this_script = c.boolean()
    cull_shadows_by_distance = c.boolean()
    shadows_fade_start_distance = c.f32()
    shadows_fade_end_distance = c.f32()
    initial_shadows_mode = c.i32()
    _require(c.remaining() == 0, f"CullingLightObject payload has {c.remaining()} trailing byte(s)")
    return {
        "type": CULLING_LIGHT_OBJECT, "schema_version": SCHEMA_VERSION, "enabled": enabled,
        "cull_by_distance_only": cull_by_distance_only,
        "fade_start_distance": fade_start_distance, "fade_end_distance": fade_end_distance,
        "use_light_intensity_from_editor": use_light_intensity_from_editor,
        "max_light_intensity": max_light_intensity,
        "take_flare_parameters_from_this_script": take_flare_parameters_from_this_script,
        "cull_shadows_by_distance": cull_shadows_by_distance,
        "shadows_fade_start_distance": shadows_fade_start_distance,
        "shadows_fade_end_distance": shadows_fade_end_distance,
        "initial_shadows_mode": initial_shadows_mode,
        "turn_off_component_count": turn_off_components + turn_off_default_states,
        "turn_off_object_count": turn_off_objects,
    }


def decode_volumetric_light(raw):
    enabled, off = _mb_header(raw)
    c = _Cursor(raw[off:])
    sample_count = c.i32()
    scattering_coef = c.f32()
    extinction_coef = c.f32()
    skybox_extinction_coef = c.f32()
    mie_g = c.f32()
    height_fog = c.boolean()
    height_scale = c.f32()
    ground_level = c.f32()
    noise = c.boolean()
    noise_scale = c.f32()
    noise_intensity = c.f32()
    noise_intensity_offset = c.f32()
    noise_velocity = list(c.vector2())
    max_ray_length = c.f32()
    _require(c.remaining() == 0, f"VolumetricLight payload has {c.remaining()} trailing byte(s)")
    return {
        "type": VOLUMETRIC_LIGHT, "schema_version": SCHEMA_VERSION, "enabled": enabled,
        "sample_count": sample_count, "scattering_coef": scattering_coef,
        "extinction_coef": extinction_coef, "skybox_extinction_coef": skybox_extinction_coef,
        "mie_g": mie_g, "height_fog": height_fog, "height_scale": height_scale,
        "ground_level": ground_level, "noise": noise, "noise_scale": noise_scale,
        "noise_intensity": noise_intensity, "noise_intensity_offset": noise_intensity_offset,
        "noise_velocity": noise_velocity, "max_ray_length": max_ray_length,
    }


def decode_light_flicker(raw):
    enabled, off = _mb_header(raw)
    c = _Cursor(raw[off:])
    frequency = c.f32()
    intensity = c.f32()
    intensity_shift = c.f32()
    time_shift = c.f32()
    _key_count, keys, pre_infinity, post_infinity, _rotation_order = c.animation_curve()
    curve = _curve_dict(keys, pre_infinity, post_infinity)
    generate = c.i32()
    random_time_shift = c.boolean()
    full_random_curve = c.boolean()
    culling_coef = c.f32()
    c.pptr()                       # LightFlicker's own _light -- appended after Flicker's fields
                                    # (see module docstring); resolved via the owner's Light sibling
    _require(c.remaining() == 0, f"LightFlicker payload has {c.remaining()} trailing byte(s)")
    return {
        "type": "LightFlicker", "schema_version": SCHEMA_VERSION, "enabled": enabled,
        "frequency": frequency, "intensity": intensity, "intensity_shift": intensity_shift,
        "time_shift": time_shift, "curve": curve, "generate": generate,
        "random_time_shift": random_time_shift, "full_random_curve": full_random_curve,
        "culling_coef": culling_coef,
    }


_DECODERS = {
    CULLING_LIGHT_OBJECT: decode_culling_light_object,
    VOLUMETRIC_LIGHT: decode_volumetric_light,
    LIGHT_FLICKER: decode_light_flicker,
}
# component_dict["type"] the corresponding decoder writes -- LIGHT_FLICKER (the census-resolved,
# namespaced class name) tags its owner with the plain "LightFlicker" component type, matching
# the other two classes' own (non-namespaced) identity.
_COMPONENT_TYPE = {
    CULLING_LIGHT_OBJECT: CULLING_LIGHT_OBJECT,
    VOLUMETRIC_LIGHT: VOLUMETRIC_LIGHT,
    LIGHT_FLICKER: "LightFlicker",
}
_COUNT_KEY = {
    CULLING_LIGHT_OBJECT: "culling_light_object_count",
    VOLUMETRIC_LIGHT: "volumetric_light_count",
    LIGHT_FLICKER: "light_flicker_count",
}


def augment_scene(scene_dict, unity_file):
    """Tag GameObjects carrying a CullingLightObject/VolumetricLight/EFT.Visual.LightFlicker
    MonoBehaviour with the matching component dict (schema above); identical reruns add zero
    (pattern of extract_physics_props.augment_scene/extract_navmesh_modifiers.augment_scene). A
    single owner commonly carries more than one of these three component types at once (Arena's
    own CullingLightObject.Awake() looks up sibling LightFlicker/VolumetricLight components) --
    each is its own independent component dict, never merged. Returns
    {"culling_light_object_count": N, "volumetric_light_count": M, "light_flicker_count": K}."""
    objects = scene_dict["objects"]
    by_id = {o["id"]: o for o in objects}
    _require(len(by_id) == len(objects), "duplicate scene object IDs")
    source_owners = {cls: set() for cls in _DECODERS}
    pending = []
    for reader in unity_file.objects.values():
        if reader.type.name != "MonoBehaviour":
            continue
        name, status = census.resolve_script_name(reader)
        if status != "ok" or name not in _DECODERS:
            continue
        head = reader.parse_monobehaviour_head()
        gptr = head.m_GameObject
        _require(gptr is not None and gptr.path_id != 0 and gptr.file_id == 0,
                 f"{name} with no scene-local owner")
        owner_id = str(gptr.path_id)
        _require(owner_id in by_id and owner_id not in source_owners[name],
                 f"missing or duplicate {name} owner")
        source_owners[name].add(owner_id)
        owner = by_id[owner_id]
        component_type = _COMPONENT_TYPE[name]
        component = _DECODERS[name](reader.get_raw_data())
        existing = [comp for comp in owner["components"] if comp["type"] == component_type]
        _require(not existing or existing == [component], f"conflicting existing {component_type} component")
        if not existing:
            pending.append((owner, component, name))
    for owner in objects:
        for cls, component_type in _COMPONENT_TYPE.items():
            _require(
                owner["id"] in source_owners[cls] or
                not any(comp["type"] == component_type for comp in owner["components"]),
                f"{component_type} component has no source counterpart")
    counts = {key: 0 for key in _COUNT_KEY.values()}
    for owner, component, cls in pending:
        owner["components"].append(component)
        counts[_COUNT_KEY[cls]] += 1
    return counts
