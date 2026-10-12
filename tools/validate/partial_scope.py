"""Source-scan scope of partial scenes, shared by the validators.

What. A *partial scene* is a plan scene record with a ``select`` block: the extractor takes only the
selected subtrees of one Arena level file (AirPit's furnished shops from level38 and the shop lights
from level37) and records the result under the manifest key ``partial_scenes``::

    {"<scene name>": {"index": N, "objects": N, "units": N, "carriers": N,
                      "rules": {"<rule>": {"selected": N, "twin_skipped": N}},
                      "dropped_script_owners": N,
                      "ballistic_orphans": [[<owner go id>, <component id>], ...],
                      "go_ids_sha256": "<64 hex>"}}

``go_ids_sha256`` is the SHA-256 of the selected GameObject path ids (decimal, ascending, one per line); it must
equal the digest of the object ids of the package scene file, which binds the scene file to the recorded
selection. ``slice`` stays ``{}`` for such a package, so the slice refusals of the contract and the source plan keep
working. A validator that re-reads the WHOLE Arena level file independently of the extractor (the
null-collider, mixed-material, special-mesh, disabled-component, ballistic and reference censuses)
must therefore count only what the package can hold: components whose owner GameObject is one of the
object ids of the package's own ``scenes/<name>.json``. Everything else in the level file is, by design,
not part of the package.

Scope of a partial scene.
  * ``owners``: the object ids of ``scenes/<name>.json`` (strings, the path ids of the source GameObjects).
  * ``orphans``: the ``(owner id, component id)`` pairs of ``BallisticCollider`` components the extractor
    hid on purpose because their owner has no physical collider (the augmenter refuses such an owner).
    They are *expected absent* from the package scene while still present in the source scan, and each
    listed owner must itself be in scope, so a hidden ballistic cannot silently widen the scope.

A scene that is not partial has no scope: ``in_scope`` returns True and every validator keeps its
whole-level behaviour byte for byte. The manifest entries are validated here (shape, scene membership,
registry index, object count against the scene file) so that a damaged ``partial_scenes`` block fails
instead of narrowing a census; the numbers themselves are pinned by the map contract
(``tools/extract/native_contracts/<stem>.json``, key ``partial_scenes``) in ``validate_native_features``.

Failures raise ``PartialScopeError`` (a ``ValueError``, which the validators already report as a failed check).
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re
import sys

PARTIAL_KEY = "partial_scenes"
_ENTRY_KEYS = ("index", "objects", "units", "carriers", "rules", "dropped_script_owners", "ballistic_orphans",
               "go_ids_sha256")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_ID = re.compile(r"^[0-9]+$")


class PartialScopeError(ValueError):
    """A manifest ``partial_scenes`` block is malformed or disagrees with the package scene it scopes."""


def _fail(scene, message: str):
    raise PartialScopeError(f"partial scene {scene}: {message}")


def _count(value, scene, field: str, minimum: int = 0) -> int:
    if type(value) is not int or value < minimum:
        _fail(scene, f"{field} must be an integer >= {minimum}, got {value!r}")
    return value


def source_id(value, scene, field: str) -> str:
    """A source path id as the package spells it (decimal string); ints from a typetree are accepted."""
    text = str(value) if type(value) is int else value
    if type(text) is not str or not _ID.match(text):
        _fail(scene, f"{field} must be a source path id, got {value!r}")
    return text


def manifest_entries(manifest: dict) -> dict:
    """The validated ``partial_scenes`` entries of a manifest, ``{}`` when it has none.

    Required keys are exactly the extractor's shape; unknown extra keys are ignored because only the
    keys read here (and by the contract pin check) carry meaning. ``ballistic_orphans`` is normalised to a
    list of ``(owner id, component id)`` string pairs."""
    raw = manifest.get(PARTIAL_KEY)
    if raw is None:
        return {}
    if type(raw) is not dict:
        raise PartialScopeError("manifest partial_scenes must be an object of scene name -> entry")
    listed = manifest.get("scenes", [])
    registry = {record.get("name"): record.get("index")
                for record in manifest.get("source", {}).get("scene_registry", []) if isinstance(record, dict)}
    result = {}
    for name, entry in raw.items():
        if name not in listed:
            _fail(name, "is not listed in manifest.scenes")
        if type(entry) is not dict:
            _fail(name, "entry must be an object")
        missing = [k for k in _ENTRY_KEYS if k not in entry]
        if missing:
            _fail(name, f"entry lacks keys {missing}")
        index = _count(entry["index"], name, "index")
        if name in registry and registry[name] != index:
            _fail(name, f"index {index} differs from the source scene registry ({registry[name]})")
        if type(entry["rules"]) is not dict or not entry["rules"]:
            _fail(name, "rules must be a non-empty object")
        for rule, counts in entry["rules"].items():
            if (type(counts) is not dict or not {"selected", "twin_skipped"} <= counts.keys()
                    or any(type(counts[k]) is not int or counts[k] < 0 for k in ("selected", "twin_skipped"))):
                _fail(name, f"rule {rule!r} must carry non-negative integer selected/twin_skipped counts")
        if type(entry["ballistic_orphans"]) is not list:
            _fail(name, "ballistic_orphans must be a list of [owner id, component id] pairs")
        orphans = []
        for pair in entry["ballistic_orphans"]:
            if not isinstance(pair, (list, tuple)) or len(pair) != 2:
                _fail(name, f"ballistic orphan {pair!r} is not an [owner id, component id] pair")
            orphans.append((source_id(pair[0], name, "ballistic orphan owner"),
                            source_id(pair[1], name, "ballistic orphan component")))
        if len(set(orphans)) != len(orphans):
            _fail(name, "duplicate ballistic orphans")
        if type(entry["go_ids_sha256"]) is not str or not _SHA256.match(entry["go_ids_sha256"]):
            _fail(name, "go_ids_sha256 must be a lowercase SHA-256 hex digest")
        result[name] = {"index": index, "objects": _count(entry["objects"], name, "objects", 1),
                        "units": _count(entry["units"], name, "units", 1),
                        "carriers": _count(entry["carriers"], name, "carriers"),
                        "dropped_script_owners": _count(entry["dropped_script_owners"], name,
                                                        "dropped_script_owners"),
                        "rules": {rule: {k: counts[k] for k in ("selected", "twin_skipped")}
                                  for rule, counts in entry["rules"].items()},
                        "ballistic_orphans": orphans, "go_ids_sha256": entry["go_ids_sha256"]}
    return result


@dataclass(frozen=True)
class SceneScope:
    """What a package can hold of one Arena level file: the owner ids plus the hidden-ballistic pairs."""
    name: str
    owners: frozenset
    orphans: frozenset

    def covers(self, owner) -> bool:
        """True when the source GameObject (int path id or its string spelling) is in the package scene."""
        return str(owner) in self.owners

    def is_orphan(self, owner, component) -> bool:
        """True for a hidden BallisticCollider: expected absent from the package, present in the source."""
        return (str(owner), str(component)) in self.orphans


def go_ids_digest(ids) -> str:
    """The extractor's ``go_ids_sha256`` (tools/extract/scene_select.py ``go_ids_digest``): ids ascending, one per line."""
    return hashlib.sha256("\n".join(str(i) for i in sorted(int(g) for g in ids)).encode("utf-8")).hexdigest()


def _object_list(scene) -> list:
    return scene["objects"] if isinstance(scene, dict) else scene


def scopes_from_objects(manifest: dict, objects_by_scene: dict) -> dict:
    """``{scene name: SceneScope}`` for the manifest's partial scenes; ``{}`` when it has none.

    ``objects_by_scene`` maps a scene name to its object list or to the whole scene JSON (``{"objects": [...]}``).
    Only partial scenes are read from it, so a package without partial scenes costs nothing."""
    scopes = {}
    for name, entry in manifest_entries(manifest).items():
        if name not in objects_by_scene:
            _fail(name, "scene is missing from the package scenes")
        ids = [str(obj["id"]) for obj in _object_list(objects_by_scene[name])]
        owners = frozenset(ids)
        if len(owners) != len(ids):
            _fail(name, "package scene object ids are not unique")
        if entry["objects"] != len(ids):
            _fail(name, f"manifest records {entry['objects']} objects, the package scene holds {len(ids)}")
        if not all(_ID.match(i) for i in ids):
            _fail(name, "package scene object ids are not source path ids")
        if go_ids_digest(ids) != entry["go_ids_sha256"]:
            _fail(name, "go_ids_sha256 differs from the object ids of the package scene")
        outside = sorted(go for go, _ in entry["ballistic_orphans"] if go not in owners)
        if outside:
            _fail(name, f"ballistic orphan owners {outside[:5]} are not objects of the scene")
        scopes[name] = SceneScope(name, owners, frozenset(entry["ballistic_orphans"]))
    return scopes


def scopes_from_package(manifest: dict, package) -> dict:
    """Like ``scopes_from_objects``, reading ``scenes/<name>.json`` of the partial scenes from ``package``.

    A manifest with partial scenes cannot be scoped without its package: ``package=None`` fails closed
    instead of letting the census silently count the whole level."""
    entries = manifest_entries(manifest)
    if not entries:
        return {}
    if package is None:
        raise PartialScopeError("partial scenes can only be scoped against their package directory")
    scenes = {}
    for name in entries:
        path = Path(package) / "scenes" / f"{name}.json"
        try:
            scenes[name] = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            _fail(name, f"cannot read {path}: {exc}")
    return scopes_from_objects(manifest, scenes)


def in_scope(scopes: dict, scene: str, owner) -> bool:
    """False only when ``scene`` is partial and the owner GameObject is not part of its package scene."""
    scope = scopes.get(scene)
    return scope is None or scope.covers(owner)


def contract_block(manifest: dict) -> dict:
    """The ``partial_scenes`` block a map contract pins for this manifest (the census, nothing else).

    ``orphans`` is the number of hidden ballistic colliders, ``dropped_script_owners`` the number of claimed
    containers whose script the selection could not carry (0 when every script owner is a unit or a carrier).
    Used to read the first real extraction into
    ``tools/extract/native_contracts/<stem>.json`` after reviewing it; the validators then hold every later
    package to exactly these numbers."""
    return {name: {"index": entry["index"], "objects": entry["objects"], "units": entry["units"],
                   "carriers": entry["carriers"], "orphans": len(entry["ballistic_orphans"]),
                   "dropped_script_owners": entry["dropped_script_owners"], "rules": entry["rules"]}
            for name, entry in manifest_entries(manifest).items()}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Print the contract partial_scenes block of an extracted package, "
                                                 "after checking the block against the package scene files.")
    parser.add_argument("package", type=Path, help="extracted package directory (holds manifest.json and scenes/)")
    args = parser.parse_args(argv)
    manifest = json.loads((args.package / "manifest.json").read_text(encoding="utf-8"))
    scopes_from_package(manifest, args.package)  # shape, object counts, digest and orphan owners
    json.dump(contract_block(manifest), sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
