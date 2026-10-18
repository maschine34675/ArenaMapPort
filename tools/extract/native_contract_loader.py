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
(``extract_bay5_audio``, PLAN-AIRPIT item 13) takes its supported maps, source scene, schema, census and
state pins from the same files.

Schema (version 1; every key is required unless marked optional, unknown keys are rejected)::

    schema_version            1
    map_key                   "arena_<stem>"
    notes                     optional list of strings, documentation only
    scenes                    {"<scene index>": "<scene name>"}  the full scene set, in validation order
    ballistics                {"<scene index>": N}               BallisticCollider count per scene
    require_unsliced          true: a manifest with a non-empty ``slice`` is rejected (Equator)
    partial_scenes            optional: {"<scene name>": {index, objects, units, carriers, orphans,
                              dropped_script_owners, excluded, exclude_entries, twin_descendants_added,
                              rules{"<rule>": {selected, twin_skipped, excluded, descendants_added}}}}  the
                              census of the scenes that take only a selected part of their Arena level
                              (plan record ``select``, manifest ``partial_scenes``; AirPit's furnished
                              shops). ``orphans`` is the number of manifest ``ballistic_orphans``,
                              ``dropped_script_owners`` the claimed containers whose script the
                              selection could not carry (a reviewed 0 today). ``excluded`` and
                              ``exclude_entries`` pin the plan record's ``select.exclude`` list (units it
                              dropped, entries it holds), ``twin_descendants_added`` the units that
                              ``select.twin_descendants: import_missing`` added below twin-skipped units;
                              all three are required (0 when the record uses neither mechanism).
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
                                            inactive_players{id: leaf name},
                                            groups{group leaf name: {mixer, players}},
                                            group_mixers[names], loops{id: leaf name},
                                            loop_players[ids],
                                            forced_mixer_players{id: Arena mixer name} (optional: players
                                            that force an Arena mixer group the port cannot provide; the
                                            exporter routes them like their group, marker mixerPolicy)}}

Rules beyond types (each one closes a way for a pin to be weakened silently): duplicate JSON keys
are an error; ``map_key`` must equal ``arena_`` + the file stem; ``ballistics`` must list exactly
the scenes of ``scenes`` and sum to more than zero; a schema-2 audio graph (which records the
original hierarchy states) must carry ``states``, a schema-1 graph must not; ``groups`` must have one
entry per ``AmbientSoundPlayerGroup``, keyed by the group GameObject's leaf name (the key the client
routes and the Unity importer checks by, so no pin depends on object-table order) with the mixer it
routes to and its member count; ``states.grouped_players`` must agree with the player census, the
coverage mode and the sum of the group member counts; ``loops`` pins each looping AudioSource by source
id AND leaf name (the Unity importer and the client look the loop up by name, so a renamed loop must
fail extraction, not the later build). ``group_mixers`` (the mixers in ``groups`` order) and
``loop_players`` (the ids of ``loops``) stay as the flat mirrors that ``AmbientAudioProfilesTests.cs`` reads
from the same file; the loader rejects a file whose mirrors disagree with ``groups``/``loops``. A missing,
unreadable or empty contract directory fails loading instead of producing an empty table.

``partial_scenes`` names scenes of ``scenes`` only, with the index the contract gives that scene; it requires
``require_unsliced`` (a partial scene is the only partial source selection a native package may carry, so the
slice refusal must stay active), every census count is an integer >= 0, ``rules`` is non-empty and ``objects`` is at
least ``units + carriers`` (every unit root and every carrier is its own object). The normalised contract always
carries the key (``{}`` when the file has none), so consumers never test for its presence.

Counting contract of the per-rule counters (the extractor's manifest block and this file use the same definition;
``tools/extract/scene_select.py`` ``select_nodes`` produces them, ``tools/validate/partial_scope.py`` reads them
back). Every counter counts UNITS. ``selected``: units a rule claimed that were not twin-skipped, counted BEFORE
the two later steps; ``twin_skipped``: claimed units skipped as twins of the reference scene (they are not part
of ``units``); ``descendants_added``: units that ``twin_descendants: import_missing`` added below a twin-skipped
unit and attributed to the rule of that unit; ``excluded``: units dropped by ``select.exclude`` (added
descendants included) and attributed to the rule that owned them. Hence, per rule, ``selected +
descendants_added - excluded`` is the number of units the rule leaves in the scene (never negative) and the
rules add up to the scene: ``sum(selected) + sum(descendants_added) - sum(excluded) == units``. The three
scene-level counters repeat the totals: ``twin_descendants_added == sum(descendants_added)`` and ``excluded ==
sum(rule excluded)``; ``exclude_entries`` (the number of ``select.exclude`` entries of the plan record) is 0
exactly when nothing was excluded, because an entry that drops no unit fails the extraction.

Failures raise ``NativeContractError``, a ``HardExtractError`` (extractors halt) and a ``ValueError``
(the validators report ``ValueError`` as a failed check).

Evidence. The Equator and Bay5 files reproduce the former literals exactly
(``tools/tests/test_native_contracts.py`` keeps a frozen copy of them as the golden reference);
the AirPit pins are the PLAN-AIRPIT item 2 dry run on Arena 0.6.0.47752
(docs/progress/airpit-release.md); its ``audio`` block (item 13) is the first real export of the
Arena_Airpit_Sound scene (level39), the first map with both an Out and an In group.
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
_PARTIAL_KEYS = ("index", "objects", "units", "carriers", "orphans", "dropped_script_owners", "excluded",
                 "exclude_entries", "twin_descendants_added", "rules")
_PARTIAL_RULE_KEYS = ("selected", "twin_skipped", "excluded", "descendants_added")
_FEATURE_KEYS = ("static_decals", "light_culling", "raincover", "navmesh_modifiers", "small_physics_objects")
_LIGHT_CULLING_KEYS = ("culling_light_object_count", "volumetric_light_count", "light_flicker_count")
_RAINCOVER_KEYS = ("indoor_trigger_count", "dry_plane_count", "weather_obstacle")
_NAVMESH_KEYS = ("modifiers", "volumes")
_AUDIO_KEYS = ("feature", "schema", "scene", "root", "players", "support", "banks", "clips",
               "group_coverage", "states")
_STATE_KEYS = ("grouped_players", "disabled_players", "inactive_players", "groups", "group_mixers",
               "loops", "loop_players")
_GROUP_KEYS = ("mixer", "players")
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


def _parse_partial_scenes(raw, where: str, scenes: dict, require_unsliced: bool) -> dict:
    """{scene name: census} for the scenes that take a selected part of their level (normalised, file order)."""
    if type(raw) is not dict:
        _fail(where, "expected an object of scene name -> census")
    if raw and not require_unsliced:
        _fail(where, "partial scenes require 'require_unsliced': the slice refusal must stay active beside them")
    by_name = {name: index for index, name in scenes.items()}
    result = {}
    for name, record in raw.items():
        at = f"{where}.{name}"
        if _text(name, where + " key") not in by_name:
            _fail(at, "is not a scene of 'scenes'")
        record = _object(record, at, _PARTIAL_KEYS)
        entry = {"index": _int(record["index"], at + ".index"),
                 "objects": _int(record["objects"], at + ".objects", 1),
                 "units": _int(record["units"], at + ".units", 1),
                 "carriers": _int(record["carriers"], at + ".carriers"),
                 "orphans": _int(record["orphans"], at + ".orphans"),
                 "dropped_script_owners": _int(record["dropped_script_owners"], at + ".dropped_script_owners"),
                 "excluded": _int(record["excluded"], at + ".excluded"),
                 "exclude_entries": _int(record["exclude_entries"], at + ".exclude_entries"),
                 "twin_descendants_added": _int(record["twin_descendants_added"], at + ".twin_descendants_added")}
        if entry["index"] != by_name[name]:
            _fail(at + ".index", f"{entry['index']} is not the contract index {by_name[name]} of the scene")
        if entry["objects"] < entry["units"] + entry["carriers"]:
            _fail(at + ".objects", f"{entry['objects']} objects cannot hold {entry['units']} units "
                                   f"and {entry['carriers']} carriers")
        if type(record["rules"]) is not dict or not record["rules"]:
            _fail(at + ".rules", "expected a non-empty object of rule -> "
                                 "{selected, twin_skipped, excluded, descendants_added}")
        rules = {}
        for rule, counts in record["rules"].items():
            rule_at = f"{at}.rules.{rule}"
            counts = _object(counts, rule_at, _PARTIAL_RULE_KEYS)
            rules[_text(rule, at + ".rules key")] = {k: _int(counts[k], f"{rule_at}.{k}") for k in _PARTIAL_RULE_KEYS}
            if counts["excluded"] > counts["selected"] + counts["descendants_added"]:
                _fail(rule_at + ".excluded", f"{counts['excluded']} units cannot be dropped from a rule that holds "
                                             f"{counts['selected']} selected and {counts['descendants_added']} added units")
        kept = (sum(r["selected"] for r in rules.values()) + sum(r["descendants_added"] for r in rules.values())
                - sum(r["excluded"] for r in rules.values()))
        if kept != entry["units"]:
            _fail(at + ".rules", f"selected + descendants_added - excluded must add up to the {entry['units']} units, "
                                 f"the rules leave {kept}")
        if sum(r["descendants_added"] for r in rules.values()) != entry["twin_descendants_added"]:
            _fail(at + ".twin_descendants_added", f"{entry['twin_descendants_added']} differs from the sum of the "
                                                  "rules' descendants_added")
        if sum(r["excluded"] for r in rules.values()) != entry["excluded"]:
            _fail(at + ".excluded", f"{entry['excluded']} differs from the sum of the rules' excluded counts")
        if (entry["exclude_entries"] == 0) != (entry["excluded"] == 0):
            _fail(at + ".exclude_entries", f"{entry['exclude_entries']} exclude entries cannot drop "
                                           f"{entry['excluded']} units (an entry that drops nothing is refused)")
        entry["rules"] = rules
        result[name] = entry
    return result


def _leaf_map(value, where: str, what: str) -> dict:
    """{source id: leaf name}, as ``inactive_players`` and ``loops`` pin a player by identity and by name."""
    if type(value) is not dict:
        _fail(where, f"expected an object of {what} id -> leaf name")
    return {_text(k, where + " key"): _text(v, f"{where}.{k}") for k, v in value.items()}


def _groups(value, where: str) -> dict:
    """{group leaf name: {mixer, players}} in file (= source) order; the name is the routing key."""
    if type(value) is not dict:
        _fail(where, "expected an object of group leaf name -> {mixer, players}")
    result = {}
    for name, record in value.items():
        at = f"{where}.{name}"
        record = _object(record, at, _GROUP_KEYS)
        result[_text(name, where + " key")] = {"mixer": _text(record["mixer"], at + ".mixer"),
                                              "players": _int(record["players"], at + ".players", 1)}
    return result


def _parse_states(raw, where: str, audio: dict):
    raw = _object(raw, where, _STATE_KEYS, optional=("forced_mixer_players",))
    states = {
        "forced_mixer_players": _leaf_map(raw.get("forced_mixer_players", {}), where + ".forced_mixer_players", "player"),
        "grouped_players": _int(raw["grouped_players"], where + ".grouped_players"),
        "disabled_players": _strings(raw["disabled_players"], where + ".disabled_players"),
        "inactive_players": _leaf_map(raw["inactive_players"], where + ".inactive_players", "player"),
        "groups": _groups(raw["groups"], where + ".groups"),
        "group_mixers": _strings(raw["group_mixers"], where + ".group_mixers", unique=False),  # two groups may share a mixer
        "loops": _leaf_map(raw["loops"], where + ".loops", "player"),
        "loop_players": _strings(raw["loop_players"], where + ".loop_players"),
    }
    players = sum(audio["players"].values())
    group_count = audio["support"].get(_GROUP_KIND, 0)
    for key in ("group_mixers", "groups"):
        if len(states[key]) != group_count:
            _fail(f"{where}.{key}", f"{len(states[key])} entries for {group_count} {_GROUP_KIND} support records")
    if states["group_mixers"] != [g["mixer"] for g in states["groups"].values()]:
        _fail(where + ".group_mixers", "must list the mixer of each entry of 'groups', in the same order")
    if sorted(states["loops"]) != sorted(states["loop_players"]):
        _fail(where + ".loops", "must name exactly the ids listed in 'loop_players'")
    if audio["group_coverage"] == "all_players":
        grouped = players
    else:
        grouped = players - len(set(states["inactive_players"]) | set(states["disabled_players"]))
    if states["grouped_players"] != grouped or not 0 <= grouped <= players:
        _fail(where + ".grouped_players",
              f"{states['grouped_players']} disagrees with {players} players under '{audio['group_coverage']}' coverage")
    members = sum(g["players"] for g in states["groups"].values())
    if members != states["grouped_players"]:
        _fail(where + ".groups", f"{members} group members disagree with grouped_players {states['grouped_players']}")
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
    raw = _object(raw, where, _TOP_REQUIRED, optional=("notes", "partial_scenes"))
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
    require_unsliced = _bool(raw["require_unsliced"], where + ".require_unsliced")
    return {
        "key": key,
        "notes": notes,
        "scenes": scenes,
        "ballistics": ballistics,
        "require_unsliced": require_unsliced,
        "partial_scenes": _parse_partial_scenes(raw.get("partial_scenes", {}), where + ".partial_scenes",
                                                scenes, require_unsliced),
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
