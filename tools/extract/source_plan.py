"""Validated source selection and provenance for the generic Arena map extractor.

Plans contain data only. Both extraction and validation reconstruct a fixed CLI from
the repository plan; package metadata can never choose an executable or command.
"""
from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path, PurePosixPath
import re

REPO = Path(__file__).resolve().parents[2]
PLAN_ROOT = REPO / "tools" / "extract" / "plans"
GENERIC_SCRIPT = "tools/extract/extract_map.py"
PLAYER_MARKER = "EFT.Game.Spawning.SpawnPointMarker"
SCENE_GROUPS = ("scenes", "anchor_scenes", "navmesh_scenes")


class PlanError(ValueError):
    pass


def finite_vector(value, label: str, length: int = 3) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) != length or any(
        isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in value
    ):
        raise PlanError(f"{label} must contain {length} finite numbers")
    return [float(v) for v in value]


def scene_record(record: dict) -> dict:
    if not isinstance(record, dict):
        raise PlanError("scene must be an object")
    name, asset, index = (record.get(k) for k in ("name", "asset_path", "index"))
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_ .()-]+", name):
        raise PlanError(f"invalid scene name: {name!r}")
    if (not isinstance(asset, str) or not asset.startswith("Assets/") or "\\" in asset
            or ".." in PurePosixPath(asset).parts or not asset.endswith(".unity")
            or PurePosixPath(asset).stem.casefold() != name.casefold()):
        raise PlanError(f"invalid exact asset_path for {name}: {asset!r}")
    if type(index) is not int or index < 0:
        raise PlanError(f"invalid BuildSettings index for {name}: {index!r}")
    return {"name": name, "asset_path": asset, "index": index}


def validate_plan(plan: dict) -> dict:
    if not isinstance(plan, dict) or type(plan.get("schema_version")) is not int or plan["schema_version"] != 1:
        raise PlanError("source plan schema_version must be 1")
    if not isinstance(plan.get("key"), str) or not re.fullmatch(r"arena_[a-z0-9_]+", plan["key"]):
        raise PlanError("source plan key must be arena_<lowercase_stem>")
    for field in ("source_preset", "source_mode"):
        if not isinstance(plan.get(field), str) or not plan[field].strip():
            raise PlanError(f"source plan {field} is required")
    finite_vector(plan.get("fog_color"), "fog_color")
    by_index, by_path, by_name = {}, {}, {}
    for group in SCENE_GROUPS:
        records = plan.get(group)
        if not isinstance(records, list) or (group != "navmesh_scenes" and not records):
            raise PlanError(f"source plan {group} must be an {'optionally empty' if group == 'navmesh_scenes' else 'nonempty'} array")
        group_indices = set()
        for raw in records:
            record = scene_record(raw)
            index, path, name = record["index"], record["asset_path"], record["name"]
            if index in group_indices:
                raise PlanError(f"duplicate scene index {index} in {group}")
            group_indices.add(index)
            for table, key in ((by_index, index), (by_path, path), (by_name, name.casefold())):
                if key in table and table[key] != record:
                    raise PlanError(f"conflicting scene mapping for {name}")
                table[key] = record
    if not isinstance(plan.get("excluded_scenes"), list):
        raise PlanError("source plan excluded_scenes must be an array with reasons")
    imported = {r["index"] for r in plan["scenes"]}
    for record in plan["excluded_scenes"]:
        if not isinstance(record, dict) or not isinstance(record.get("reason"), str) or not record["reason"].strip():
            raise PlanError("every excluded scene needs a reason")
        scene_record(record)
        if record.get("index") in imported:
            raise PlanError("an imported scene cannot also be excluded")
    return plan


def load_plan(path: Path) -> dict:
    try:
        return validate_plan(json.loads(Path(path).read_text(encoding="utf-8-sig")))
    except (OSError, json.JSONDecodeError) as exc:
        raise PlanError(f"cannot read source plan {path}: {exc}") from exc


def plan_digest(plan: dict) -> str:
    canonical = json.dumps(plan, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def plan_relative_path(path: Path, repo: Path = REPO) -> str:
    resolved = Path(path).resolve()
    root = (repo / "tools" / "extract" / "plans").resolve()
    if not resolved.is_relative_to(root) or resolved.suffix != ".json":
        raise PlanError(f"source plan must be a JSON file under {root}")
    return resolved.relative_to(repo.resolve()).as_posix()


def registry_for(plan: dict) -> list[dict]:
    return [scene_record(r) for r in plan["scenes"]]


def unique_records(plan: dict) -> list[dict]:
    records = {}
    for group in SCENE_GROUPS:
        for raw in plan[group]:
            record = scene_record(raw)
            records.setdefault(record["index"], record)
    return list(records.values())


def verify_scene_mapping(plan: dict, build_scenes: list[str], source: Path) -> None:
    """Exact asset paths; duplicate paths must have a unique largest full-data copy."""
    for record in unique_records(plan):
        candidates = [i for i, asset in enumerate(build_scenes) if asset == record["asset_path"]]
        if not candidates:
            raise PlanError(f"scene missing from source BuildSettings: {record['asset_path']}")
        sizes = {i: (source / f"sharedassets{i}.assets").stat().st_size
                 if (source / f"sharedassets{i}.assets").is_file() else -1 for i in candidates}
        largest = max(sizes.values())
        best = [i for i in candidates if sizes[i] == largest]
        if largest < 0 or len(best) != 1 or best[0] != record["index"]:
            raise PlanError(f"source scene mapping drift/ambiguity for {record['name']}: expected "
                            f"{record['index']}, candidates={sizes}")
        require_file(source / f"level{record['index']}")
        require_file(source / f"sharedassets{record['index']}.assets")


def require_file(path: Path) -> Path:
    if not path.is_file():
        raise PlanError(f"required source input missing: {path}")
    return path


def read_build_scenes(source: Path) -> list[str]:
    import UnityPy
    for path in (source / "globalgamemanagers", source / "globalgamemanagers.assets"):
        if not path.is_file():
            continue
        env = UnityPy.load(str(path))
        for obj in env.objects:
            if obj.type.name == "BuildSettings":
                scenes = obj.read_typetree().get("scenes")
                if scenes:
                    return scenes
    raise PlanError(f"source BuildSettings.scenes missing under {source}")


def provenance(plan: dict, plan_path: Path, source: Path, timestamp: str, repo: Path = REPO) -> dict:
    return {"arena_data_root": source.as_posix(), "extractor_script": GENERIC_SCRIPT,
            "extracted_utc": timestamp, "source_plan": plan_relative_path(plan_path, repo),
            "source_plan_sha256": plan_digest(plan), "map_key": plan["key"],
            "scene_registry": registry_for(plan)}


def validate_manifest_plan(manifest: dict, repo: Path = REPO, verify_source: bool = True) -> tuple[Path, dict]:
    source = manifest.get("source", {})
    if source.get("extractor_script") != GENERIC_SCRIPT:
        raise PlanError("generic source-plan package must use the fixed extract_map.py entrypoint")
    rel = source.get("source_plan")
    if not isinstance(rel, str) or not rel:
        raise PlanError("generic package source.source_plan missing")
    path = repo / rel
    if plan_relative_path(path, repo) != rel:
        raise PlanError("source.source_plan must be a normalized repository-relative plan path")
    plan = load_plan(path)
    if source.get("source_plan_sha256") != plan_digest(plan):
        raise PlanError("source plan digest drift: package was extracted from a different plan")
    if source.get("scene_registry") != registry_for(plan):
        raise PlanError("source scene_registry differs from the verified source plan")
    if source.get("map_key") != plan["key"] or manifest.get("scenes") != [r["name"] for r in plan["scenes"]]:
        raise PlanError("package map identity or ordered scenes differ from the source plan")
    if manifest.get("slice"):
        raise PlanError("generic full-map packages cannot contain slices")
    validate_anchors(manifest.get("anchors_reference", {}))
    if verify_source:
        root = source.get("arena_data_root")
        if not isinstance(root, str) or not Path(root).is_dir():
            raise PlanError("generic package source tree is unavailable; validation cannot skip provenance")
        verify_scene_mapping(plan, read_build_scenes(Path(root)), Path(root))
    return path.resolve(), plan


def validate_anchors(anchors: dict) -> None:
    player = anchors.get(PLAYER_MARKER) if isinstance(anchors, dict) else None
    if not isinstance(player, dict) or not player.get("sample_positions"):
        raise PlanError("no player SpawnPointMarker anchors; origin fallback is forbidden for full maps")
    for cls, entry in anchors.items():
        positions = entry.get("sample_positions") if isinstance(entry, dict) else None
        if (not isinstance(positions, list) or type(entry.get("count")) is not int
                or entry["count"] != len(positions)):
            raise PlanError(f"{cls}: anchors must contain every counted position")
        for i, position in enumerate(positions):
            finite_vector(position, f"{cls} anchor {i}")
        files = entry.get("sample_files")
        if (not isinstance(files, list) or not files or any(not isinstance(f, str) for f in files)
                or files != sorted(set(files))):
            raise PlanError(f"{cls}: sample_files must be unique and sorted")
