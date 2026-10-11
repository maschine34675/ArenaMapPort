"""Per-map native-feature contracts: loader and strict schema for ``native_contracts/<stem>.json``.

What. Every native-feature package (``manifest.native_features`` + ``native_ballistics`` and,
for maps that carry it, ``native_audio``) is accepted only against a census that was reviewed
for that map: which scenes the package holds, how many ballistic colliders each scene has,
how many decals / culling lights / NavMesh modifiers the package carries, and the shape of
the ambient-audio graph. Until PLAN-AIRPIT item 3 those pins were literals inside
``tools/validate/validate_native_features.py`` (a ``MAPS`` table with an Equator-only audio
branch), so a third map meant editing validator code. Now the pins are data, one file per map
(``equator.json``, ``bay5.json``, ``airpit.json``, named like ``tools/maps/<stem>.json``) and
this module is the only reader. The validator imports it; the ambient-audio extractor
(PLAN-AIRPIT item 13) takes its census from the same files.

Schema (version 1; every key is required unless marked optional, unknown keys are rejected)::

    schema_version            1
    map_key                   "arena_<stem>"
    notes                     optional list of strings, documentation only
    scenes                    {"<scene index>": "<scene name>"}  the full scene set, in validation order
    ballistics                {"<scene index>": N}               BallisticCollider count per scene
    require_unsliced          true: a manifest with a non-empty ``slice`` is rejected (Equator)
    implicit_scene_identity   true: a manifest without ``source.map_key`` is this map when its
                              scene names equal ``scenes`` (released Bay5 predates ``map_key``)
    features                  null (shape-only checks, Equator/Bay5) or census pins:
                              {static_decals, light_culling{culling_light_object_count,
                               volumetric_light_count, light_flicker_count},
                               raincover{indoor_trigger_count, dry_plane_count, weather_obstacle},
                               navmesh_modifiers{modifiers, volumes}, small_physics_objects}
    audio                     null (ballistics-only package: ``native_audio`` must be absent and
                              ``native_features.required`` is ["ballistics"]) or
                              {feature, schema 1|2, scene "level<N>", root, players{kind: N},
                               support{kind: N}, banks, clips,
                               group_coverage "all_players"|"active_players",
                               states null|{grouped_players, disabled_players[ids],
                                            inactive_players{id: leaf name}, group_mixers[names],
                                            loop_players[ids]}}

Rules beyond types (each one closes a way for a pin to be weakened silently): duplicate JSON keys
are an error; ``map_key`` must equal ``arena_`` + the file stem; ``ballistics`` must list exactly
the scenes of ``scenes`` and sum to more than zero; a schema-2 audio graph (which records the
original hierarchy states) must carry ``states``, a schema-1 graph must not; ``group_mixers`` must
have one entry per ``AmbientSoundPlayerGroup``; ``states.grouped_players`` must agree with the
player census and the coverage mode. A missing, unreadable or empty contract directory fails
loading instead of producing an empty table.

Failures raise ``NativeContractError``, a ``HardExtractError`` (extractors halt) and a ``ValueError``
(the validators report ``ValueError`` as a failed check).

Evidence. The Equator and Bay5 files reproduce the former literals exactly
(``tools/tests/test_native_contracts.py`` keeps a frozen copy of them as the golden reference);
the AirPit pins are the PLAN-AIRPIT item 2 dry run on Arena 0.6.0.47752
(docs/progress/airpit-release.md).
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:  # standalone-importable regardless of caller sys.path setup
    sys.path.insert(0, _HERE)

from common_ids import HardExtractError  # noqa: E402

SCHEMA_VERSION = 1
CONTRACT_DIR = Path(_HERE) / "native_contracts"

_TOP_REQUIRED = ("schema_version", "map_key", "scenes", "ballistics", "require_unsliced",
                 "implicit_scene_identity", "features", "audio")
_FEATURE_KEYS = ("static_decals", "light_culling", "raincover", "navmesh_modifiers", "small_physics_objects")
_LIGHT_CULLING_KEYS = ("culling_light_object_count", "volumetric_light_count", "light_flicker_count")
_RAINCOVER_KEYS = ("indoor_trigger_count", "dry_plane_count", "weather_obstacle")
_NAVMESH_KEYS = ("modifiers", "volumes")
_AUDIO_KEYS = ("feature", "schema", "scene", "root", "players", "support", "banks", "clips",
               "group_coverage", "states")
_STATE_KEYS = ("grouped_players", "disabled_players", "inactive_players", "group_mixers", "loop_players")
_GROUP_KIND = "AmbientSoundPlayerGroup"
_COVERAGE = ("all_players", "active_players")


class NativeContractError(HardExtractError, ValueError):
    """A native contract file is missing, malformed or inconsistent."""


def _fail(where: str, message: str):
    raise NativeContractError(f"native contract {where}: {message}")


def _no_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise NativeContractError(f"duplicate JSON key {key!r}")
        result[key] = value
    return result


def _int(value, where: str, minimum: int = 0) -> int:
    if type(value) is not int or value < minimum:
        _fail(where, f"expected an integer >= {minimum}, got {value!r}")
    return value


def _bool(value, where: str) -> bool:
    if type(value) is not bool:
        _fail(where, f"expected a boolean, got {value!r}")
    return value


def _text(value, where: str) -> str:
    if type(value) is not str or not value:
        _fail(where, f"expected a non-empty string, got {value!r}")
    return value


def _object(value, where: str, required, optional=()) -> dict:
    if type(value) is not dict:
        _fail(where, "expected an object")
    missing = [k for k in required if k not in value]
    unknown = [k for k in value if k not in required and k not in optional]
    if missing or unknown:
        _fail(where, f"missing keys {missing}, unknown keys {unknown}")
    return value


def _counts(value, where: str) -> dict:
    """{class name: positive count}, non-empty."""
    if type(value) is not dict or not value:
        _fail(where, "expected a non-empty object of class name -> count")
    return {_text(k, where + " key"): _int(v, f"{where}.{k}", 1) for k, v in value.items()}


def _strings(value, where: str, unique: bool = True) -> list:
    if type(value) is not list:
        _fail(where, "expected a list of strings")
    result = [_text(v, where) for v in value]
    if unique and len(set(result)) != len(result):
        _fail(where, "duplicate entries")
    return result


def _index(key, where: str) -> int:
    if type(key) is not str or not (key.isascii() and key.isdigit()) or str(int(key)) != key:
        _fail(where, f"scene index key must be a canonical decimal string, got {key!r}")
    return int(key)


def _parse_features(raw, where: str):
    if raw is None:
        return None
    raw = _object(raw, where, _FEATURE_KEYS)
    light = _object(raw["light_culling"], where + ".light_culling", _LIGHT_CULLING_KEYS)
    rain = _object(raw["raincover"], where + ".raincover", _RAINCOVER_KEYS)
    navmesh = _object(raw["navmesh_modifiers"], where + ".navmesh_modifiers", _NAVMESH_KEYS)
    return {
        "static_decals": _int(raw["static_decals"], where + ".static_decals"),
        "light_culling": {k: _int(light[k], f"{where}.light_culling.{k}") for k in _LIGHT_CULLING_KEYS},
        "raincover": {"indoor_trigger_count": _int(rain["indoor_trigger_count"], where + ".raincover.indoor_trigger_count"),
                      "dry_plane_count": _int(rain["dry_plane_count"], where + ".raincover.dry_plane_count"),
                      "weather_obstacle": _bool(rain["weather_obstacle"], where + ".raincover.weather_obstacle")},
        "navmesh_modifiers": {k: _int(navmesh[k], f"{where}.navmesh_modifiers.{k}") for k in _NAVMESH_KEYS},
        "small_physics_objects": _int(raw["small_physics_objects"], where + ".small_physics_objects"),
    }


def _parse_states(raw, where: str, audio: dict):
    raw = _object(raw, where, _STATE_KEYS)
    inactive = raw["inactive_players"]
    if type(inactive) is not dict:
        _fail(where + ".inactive_players", "expected an object of player id -> leaf name")
    states = {
        "grouped_players": _int(raw["grouped_players"], where + ".grouped_players"),
        "disabled_players": _strings(raw["disabled_players"], where + ".disabled_players"),
        "inactive_players": {_text(k, where + ".inactive_players key"): _text(v, f"{where}.inactive_players.{k}")
                             for k, v in inactive.items()},
        "group_mixers": _strings(raw["group_mixers"], where + ".group_mixers", unique=False),  # two groups may share a mixer
        "loop_players": _strings(raw["loop_players"], where + ".loop_players"),
    }
    players = sum(audio["players"].values())
    groups = audio["support"].get(_GROUP_KIND, 0)
    if len(states["group_mixers"]) != groups:
        _fail(where + ".group_mixers", f"{len(states['group_mixers'])} entries for {groups} {_GROUP_KIND} support records")
    if audio["group_coverage"] == "all_players":
        grouped = players
    else:
        grouped = players - len(set(states["inactive_players"]) | set(states["disabled_players"]))
    if states["grouped_players"] != grouped or not 0 <= grouped <= players:
        _fail(where + ".grouped_players",
              f"{states['grouped_players']} disagrees with {players} players under '{audio['group_coverage']}' coverage")
    return states


def _parse_audio(raw, where: str):
    if raw is None:
        return None
    raw = _object(raw, where, _AUDIO_KEYS)
    schema = raw["schema"]
    if type(schema) is not int or schema not in (1, 2):
        _fail(where + ".schema", f"unsupported audio schema {schema!r}")
    scene = _text(raw["scene"], where + ".scene")
    if not re.fullmatch(r"level\d+", scene):
        _fail(where + ".scene", f"expected a 'level<N>' source scene, got {scene!r}")
    coverage = raw["group_coverage"]
    if coverage not in _COVERAGE:
        _fail(where + ".group_coverage", f"expected one of {_COVERAGE}, got {coverage!r}")
    audio = {
        "feature": _text(raw["feature"], where + ".feature"),
        "schema": schema,
        "scene": scene,
        "root": _text(raw["root"], where + ".root"),
        "players": _counts(raw["players"], where + ".players"),
        "support": _counts(raw["support"], where + ".support"),
        "banks": _int(raw["banks"], where + ".banks", 1),
        "clips": _int(raw["clips"], where + ".clips", 1),
        "group_coverage": coverage,
        "states": None,
    }
    if audio["feature"] == "ballistics":
        _fail(where + ".feature", "'ballistics' is reserved for the ballistic feature")
    if (raw["states"] is None) != (schema == 1):
        _fail(where + ".states", "a schema-2 audio graph records hierarchy states and must pin them; "
                                 "a schema-1 graph has none and must use null")
    if raw["states"] is not None:
        audio["states"] = _parse_states(raw["states"], where + ".states", audio)
    return audio


def parse_contract(raw, stem: str, source: str = "<memory>") -> dict:
    """Validate one decoded contract and return it normalised (scene indices become ints)."""
    where = source
    raw = _object(raw, where, _TOP_REQUIRED, optional=("notes",))
    if raw["schema_version"] != SCHEMA_VERSION or type(raw["schema_version"]) is not int:
        _fail(where, f"unsupported schema_version {raw['schema_version']!r}")
    key = _text(raw["map_key"], where + ".map_key")
    if key != "arena_" + stem:
        _fail(where, f"map_key {key!r} must be 'arena_{stem}' for the file name")
    notes = _strings(raw.get("notes", []), where + ".notes", unique=False)
    if type(raw["scenes"]) is not dict or not raw["scenes"]:
        _fail(where + ".scenes", "expected a non-empty object of scene index -> name")
    scenes = {_index(k, where + ".scenes"): _text(v, f"{where}.scenes.{k}") for k, v in raw["scenes"].items()}
    if len(set(scenes.values())) != len(scenes):
        _fail(where + ".scenes", "duplicate scene names")
    if type(raw["ballistics"]) is not dict:
        _fail(where + ".ballistics", "expected an object of scene index -> count")
    ballistics = {_index(k, where + ".ballistics"): _int(v, f"{where}.ballistics.{k}")
                  for k, v in raw["ballistics"].items()}
    if set(ballistics) != set(scenes):
        _fail(where + ".ballistics", "must list exactly the scenes of 'scenes'")
    if sum(ballistics.values()) <= 0:
        _fail(where + ".ballistics", "a native package lists ballistics as required; the total must be positive")
    return {
        "key": key,
        "notes": notes,
        "scenes": scenes,
        "ballistics": ballistics,
        "require_unsliced": _bool(raw["require_unsliced"], where + ".require_unsliced"),
        "implicit_scene_identity": _bool(raw["implicit_scene_identity"], where + ".implicit_scene_identity"),
        "features": _parse_features(raw["features"], where + ".features"),
        "audio": _parse_audio(raw["audio"], where + ".audio"),
    }


def load_contract_file(path) -> dict:
    """Read, parse and validate one ``native_contracts/<stem>.json``."""
    path = Path(path)
    try:
        raw = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_no_duplicate_keys)
    except (OSError, ValueError) as exc:  # NativeContractError is a ValueError: duplicate keys land here too
        raise NativeContractError(f"native contract {path.name}: cannot read: {exc}") from exc
    return parse_contract(raw, path.stem, path.name)


def load_contracts(directory=None) -> dict:
    """All contracts in ``directory`` (default: the repository's), keyed by ``map_key``."""
    directory = Path(CONTRACT_DIR if directory is None else directory)
    if not directory.is_dir():
        raise NativeContractError(f"native contract directory missing: {directory}")
    contracts: dict = {}
    for path in sorted(directory.glob("*.json")):
        contract = load_contract_file(path)
        contracts[contract["key"]] = contract
    if not contracts:
        raise NativeContractError(f"no native contract files in {directory}")
    return contracts


def load_contract(map_key: str, directory=None) -> dict:
    """The contract of one map; unknown keys fail instead of falling back."""
    contracts = load_contracts(directory)
    if map_key not in contracts:
        raise NativeContractError(f"no native contract for map {map_key!r}; known: {sorted(contracts)}")
    return contracts[map_key]
