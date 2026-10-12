"""Native-feature packaging for plan-driven maps (``extract_map.py --native-features``).

What. Equator and Bay5 each carry the native-feature steps inline: ``extract_equator.py``
(996-1099, gated on ALL5 and hard-coded to ``arena_equator``) and ``extract_bay5.py``
(275-398). Both do the same thing: read the ballistic preset contract once from the SPT
install, run the per-scene augmenters on the freshly extracted scene JSON, write the
post-extraction artifacts (rain-cover geometry, optional ambient audio) and add the manifest
blocks ``native_ballistics``, ``native_audio`` and ``native_features``. This module is that
sequence for any map described by a source plan, so the next map needs no extractor edit.
``extract_equator.py`` and ``extract_bay5.py`` are deliberately NOT changed: their accepted
packages stay reproducible byte for byte.

Order. Per scene: ballistics, NavMesh modifiers, static decals, SmallPhysicsObject, light
culling. Equator runs decals after the NavMesh step, Bay5 before ballistics; the two
differ only if one owner carried a decal plus another native component. Measured on the
accepted v5 packages: no owner does. The only owners with two native components are
Equator LightFlicker+CullingLightObject (2) and VolumetricLight+CullingLightObject (17),
Bay5 BallisticCollider+NavMeshModifier (113) and VolumetricLight+CullingLightObject (1),
and in every one of those pairs the relative order is the same under both legacy orders
(ballistics before NavMesh; the light-culling classes come from one augmenter). So this
order yields the same packages as both legacy orders.

Manifest. ``native_features`` = ``{schema_version 1, required, navmesh_areas, raincover,
light_culling, static_decals}``. ``required`` lists ``ballistics`` plus the audio feature
name when the map has a registered ambient-audio exporter (``AUDIO_EXPORTERS``); a map
without one gets ``required == ["ballistics"]`` and no ``native_audio`` key (the validator
accepts that for a contract with ``audio: null``). ``arena_airpit`` registers
``extract_bay5_audio.export`` (PLAN-AIRPIT item 13): its census, schema and states come from
``native_contracts/airpit.json`` and its source scene is the plan's ``sound`` role.
Equator and Bay5 are not registered: their own extractors call the same exporter directly.
``static_decals`` is written only when the map has decals, because the validator
(``validate_native_features.feature_enabled``) requires a non-empty census whenever the key
is present and requires none when it is absent. Equator and Bay5 always have decals, so
their legacy manifests are unaffected.

Failures. The augmenters raise ``HardExtractError``; the rain-cover and audio exporters raise
``ValueError`` when a map's authored data cannot be ported. ``manifest_blocks`` re-raises the latter
as ``HardExtractError`` so ``extract_map.main`` reports every native failure as ``[extract_map]
FAILED`` / exit 2 (and no manifest is written, so the package stays visibly incomplete).

Evidence. docs/progress/airpit-release.md, item 2: the dry run on ``arena_airpit`` against
Arena 0.6.0 and the SPT install gives BallisticCollider 3548, StaticDeferredDecal 481,
NavMeshModifier 20 + NavMeshModifierVolume 12, CullingLightObject 533, VolumetricLight 130,
LightFlicker 0, SmallPhysicsObject 0.
"""
from __future__ import annotations

import os
from pathlib import Path
import sys
from typing import Callable

_HERE = os.path.dirname(os.path.abspath(__file__))
_ANALYSIS_SCRIPTS = os.path.normpath(os.path.join(_HERE, "..", "..", "analysis", "scripts"))
for _path in (_HERE, _ANALYSIS_SCRIPTS):  # standalone-importable regardless of caller sys.path setup
    if _path not in sys.path:
        sys.path.insert(0, _path)

from common_ids import HardExtractError  # noqa: E402
import extract_ballistics  # noqa: E402
import extract_bay5_audio  # noqa: E402
import extract_decals  # noqa: E402
import extract_light_culling  # noqa: E402
import extract_navmesh_modifiers  # noqa: E402
import extract_physics_props  # noqa: E402
import extract_raincover  # noqa: E402
from native_contract_loader import load_contracts  # noqa: E402
from source_plan import scene_for_role  # noqa: E402

SCHEMA_VERSION = 1

# (source, output, map_key) -> manifest.native_audio entry. Writes under <output>/audio.
AudioExporter = Callable[[str, str, str], dict]

# map key -> (native_features.required entry, exporter) for plan-driven maps whose native contract
# declares ambient audio. The entry must equal the contract's ``audio.feature`` and the exporter reads
# its pins from the same contract (``for_plan`` checks both, and the plan's ``sound`` scene, before any
# extraction). Equator/Bay5 are not listed: extract_equator.py / extract_bay5.py call the exporter inline.
AUDIO_EXPORTERS: dict[str, tuple[str, AudioExporter]] = {
    "arena_airpit": ("ambient_audio", extract_bay5_audio.export),
}

_LIGHT_CULLING_KEYS = ("culling_light_object_count", "volumetric_light_count", "light_flicker_count")


def _log(message: str) -> None:
    print(f"[native_features] {message}", file=sys.stderr)


def _check_audio_registration(plan: dict, feature: str) -> None:
    """A registered exporter must agree with the map's contract and with the plan's ``sound`` role.

    The exporter takes its source scene from ``native_contracts/<stem>.json``; the plan names the
    same scene as the excluded scene with role ``sound``. Comparing them here fails a drifted pin
    (an Arena update that renumbers the scene, a contract edited without the plan) in seconds
    instead of after the multi-minute extraction. A plan without the role fails with PlanError.
    """
    key = plan["key"]
    sound = scene_for_role(plan, "sound")
    contract = load_contracts().get(key)
    audio = contract["audio"] if contract else None
    if audio is None:
        raise HardExtractError(f"native_features: {key} registers an ambient-audio exporter but its native "
                               "contract declares no audio")
    if audio["feature"] != feature:
        raise HardExtractError(f"native_features: {key} registers audio feature {feature!r} but its native "
                               f"contract says {audio['feature']!r}")
    if audio["scene"] != f"level{sound['index']}":
        raise HardExtractError(f"native_features: {key} contract audio scene {audio['scene']!r} is not the plan's "
                               f"sound scene {sound['name']!r} (level{sound['index']})")


def _fail_closed(label: str, call, *args, **kwargs):
    """Run a post-write exporter, turning its fail-closed ``ValueError`` into a ``HardExtractError``.

    ``extract_raincover.export`` and the ambient-audio exporters signal "this map's authored data
    does not match what I can port" with a bare ``ValueError`` (they predate ``HardExtractError``
    and Equator/Bay5 call them unchanged). They run after the multi-minute extraction, and
    ``extract_map.main`` reports only ``PlanError``/``HardExtractError``/``OSError`` as the
    ``[extract_map] FAILED`` exit 2, so without this a rejected map would end as an uncaught
    traceback with exit 1. The original exception stays chained as ``__cause__``.
    """
    try:
        return call(*args, **kwargs)
    except ValueError as exc:
        raise HardExtractError(f"native_features: {label} export failed: {exc}") from exc


class NativeFeatureExtraction:
    """One run's native-feature state: the ballistic contract (loaded once) and the totals."""

    def __init__(self, *, source: Path, target: Path, map_key: str, scripts_scene_index: int,
                 audio: tuple[str, AudioExporter] | None = None):
        self.source = Path(source)
        self.target = Path(target)
        self.map_key = map_key
        self.scripts_scene_index = scripts_scene_index
        self.audio = audio
        for name in ("resources.assets", "globalgamemanagers.assets"):
            if not (self.target / name).is_file():
                raise HardExtractError(f"native_features: --target is not an EscapeFromTarkov_Data directory, "
                                       f"{name} missing: {self.target}")
        # Both installs' EFTHardSettings preset tables, read exactly once for the whole map.
        self.contract = extract_ballistics.load_contract(self.source.as_posix(), self.target.as_posix())
        self.ballistics = 0
        self.navmesh_modifiers = 0
        self.navmesh_volumes = 0
        self.physics_objects = 0
        self.decals = 0
        self.light_culling = dict.fromkeys(_LIGHT_CULLING_KEYS, 0)

    @classmethod
    def for_plan(cls, plan: dict, source: Path, target: Path) -> "NativeFeatureExtraction":
        """Resolve the scripts scene (and, with an audio exporter, the sound scene) from the plan roles.

        A plan without the role, or a contract that disagrees with it, fails before any extraction."""
        scripts = scene_for_role(plan, "scripts")
        audio = AUDIO_EXPORTERS.get(plan["key"])
        if audio is not None:
            _check_audio_registration(plan, audio[0])
        return cls(source=source, target=target, map_key=plan["key"], scripts_scene_index=scripts["index"],
                   audio=audio)

    def source_fields(self) -> dict:
        """Extra manifest ``source`` entries (the reproducibility check re-passes ``--target``)."""
        return {"target_data_root": self.target.as_posix()}

    def augment_scene(self, scene_json: dict, unity_file, ctx, label: str = "") -> None:
        """Add every native component to one freshly extracted scene (Equator/Bay5 order)."""
        suffix = f" ({label})" if label else ""
        count = extract_ballistics.augment_scene(scene_json, unity_file, self.contract)
        self.ballistics += count
        _log(f"restored {count} original ballistic material components{suffix}")
        modifiers, volumes = extract_navmesh_modifiers.augment_scene(scene_json, unity_file)
        self.navmesh_modifiers += modifiers
        self.navmesh_volumes += volumes
        _log(f"restored {modifiers} NavMeshModifier + {volumes} NavMeshModifierVolume component(s){suffix}")
        decals = extract_decals.augment_scene(scene_json, unity_file, ctx)
        self.decals += decals
        _log(f"restored {decals} StaticDeferredDecal component(s){suffix}")
        physics = extract_physics_props.augment_scene(scene_json, unity_file)
        self.physics_objects += physics
        _log(f"tagged {physics} SmallPhysicsObject owners{suffix}")
        culling = extract_light_culling.augment_scene(scene_json, unity_file)
        for key in _LIGHT_CULLING_KEYS:
            self.light_culling[key] += culling[key]
        _log(f"tagged {culling['culling_light_object_count']} CullingLightObject + "
             f"{culling['volumetric_light_count']} VolumetricLight + "
             f"{culling['light_flicker_count']} LightFlicker owner(s){suffix}")

    def manifest_blocks(self, env, output: Path) -> dict:
        """Post-write steps (rain cover, audio hook, NavMesh areas) and the manifest blocks.

        Runs after the meshes/materials/textures are written so a failure leaves no manifest
        (extract_map writes it last as the completeness marker). Returns the top-level
        manifest keys to merge: ``native_ballistics``, ``native_features`` and, only when the
        map has a registered exporter, ``native_audio``.
        """
        if self.ballistics <= 0:
            raise HardExtractError(f"native_features: {self.map_key} produced no BallisticCollider component; "
                                   "a native package lists ballistics as required")
        source, out = self.source.as_posix(), Path(output).as_posix()
        required = ["ballistics"]
        blocks: dict = {"native_ballistics": self.contract}
        if self.audio is not None:
            feature, export_audio = self.audio
            blocks["native_audio"] = _fail_closed(feature, export_audio, source, out, self.map_key)
            required.append(feature)
        features: dict = {"schema_version": SCHEMA_VERSION, "required": required,
                          "navmesh_areas": extract_navmesh_modifiers.read_navmesh_areas(env, source)}
        features["raincover"] = _fail_closed("raincover", extract_raincover.export, source, out, self.map_key,
                                             scripts_scene_index=self.scripts_scene_index)
        features["light_culling"] = {"schema_version": extract_light_culling.SCHEMA_VERSION,
                                     **self.light_culling}
        if self.decals:
            features["static_decals"] = {"schema_version": extract_decals.SCHEMA_VERSION, "count": self.decals}
        blocks["native_features"] = features
        _log(f"{self.map_key}: {self.ballistics} BallisticCollider, {self.decals} StaticDeferredDecal, "
             f"{self.navmesh_modifiers} NavMeshModifier + {self.navmesh_volumes} NavMeshModifierVolume, "
             f"{self.light_culling['culling_light_object_count']} CullingLightObject, "
             f"{self.light_culling['volumetric_light_count']} VolumetricLight, "
             f"{self.light_culling['light_flicker_count']} LightFlicker, "
             f"{self.physics_objects} SmallPhysicsObject, required={required}")
        return blocks
