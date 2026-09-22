#!/usr/bin/env python3
"""WP6 validator for WP1's extracted package (docs/SPEC.md SS B.WP1 algorithm / SS C package
format contract / SS B.WP6 table row 1: "WP1's acceptance criteria (SS B.WP1) programmatically:
errors == [], every reference resolves, id determinism (run twice, diff)").

Everything here is independent of tools/extract/*.py's own internals: the mesh.bin binary
layout is re-parsed from scratch against SS C.2's byte table (not by importing mesh_bin.py),
texture PNGs are independently opened and measured, and the reproducibility check literally
re-invokes the extractor as a subprocess and diffs its output -- this script does not import or
modify any WP1 code, per the work order ("Do not modify WP1/WP2 code").

Checks performed against --package (default: work/extract/equator_s1, O7's S1 path):
  1. manifest.json parses and has the SS C.1 shape (tolerating WP1's documented extra "slice"/
     "warnings" keys).
  2. manifest.json.errors == [] (hard SS B.WP1 acceptance gate).
  3. Every id key (mesh/material/texture) is a 16-char lowercase-hex string (SS C's id contract).
  4. Every scenes/<name>.json listed in manifest.scenes exists, parses, and every object's
     component references resolve:
       - MeshFilter.mesh_id / MeshCollider.mesh_id  -> manifest.meshes
       - MeshRenderer/SkinnedMeshRenderer.material_ids[] -> manifest.materials
       - LODGroup.lods[].renderer_object_ids[] -> an object id present in the SAME scene that
         itself carries a MeshRenderer/SkinnedMeshRenderer component (SS B.WP2.5's two-pass
         renderer map depends on this holding)
       - object.parent_id -> null or another object id in the same scene (no dangling parents)
       - object ids are unique within a scene
       - every component "type" is one of SS C.6's recorded set (nothing out-of-scope leaked in)
  5. Every materials/<id>.json parses, has a non-empty "shader" string, and every non-null
     tex_envs[...].texture_id resolves to manifest.textures.
  6. Every meshes/<id>.mesh.bin is independently re-parsed against SS C.2's binary layout:
     magic "AMPM", version 1, vertex_count/submesh_count match the manifest entry, and the
     file's total byte size exactly matches offset(16) + per-channel vertex data + submesh
     table + (sum of submeshes' index_count) * index element size -- catches truncation, a
     wrong channel_mask, or a stray/short index buffer.
  7. Every textures/<id>.png opens with Pillow and its (width, height) matches the manifest
     entry.
  8. Reproducibility (SS B.WP1's explicit acceptance criterion + SS G.S1's "required for WP6's
     reproducibility check"): unless --skip-reproducibility, re-invoke
     tools/extract/extract_equator.py TWICE with the exact --source/--scenes/--slice-roots
     implied by the package's own manifest.json (scene indices recovered via
     extract_equator.SCENE_REGISTRY, slice roots from manifest["slice"]), each with a shared
     --fixed-timestamp, into fresh temp dirs, then:
       (a) diffs the two fresh runs against each other (pure determinism -- must be byte-
           identical, independent of what happens to be checked in);
       (b) diffs one fresh run against the checked-in --package (with extracted_utc normalized
           in both), i.e. "is the package on disk what today's extractor actually produces from
           the current source data" (package-freshness / drift check).
     Both re-invocations need the read-only Arena source tree (SS0/SS B.WP1) to be reachable;
     if it is not, this step is SKIPPED with a clear message (an environment gap, not a WP1
     defect) rather than failed.

Exit code 0 + "OK" summary on success. Exit code 1 with specific, actionable messages (never a
bare stack trace) on any failure, per SS B.WP6's own acceptance criterion for validators.

Usage:
    python tools/validate/validate_extract.py
    python tools/validate/validate_extract.py --package work/extract/equator_s1
    python tools/validate/validate_extract.py --package work/extract/equator --skip-reproducibility
"""
from __future__ import annotations

import argparse
from collections import Counter
import filecmp
import hashlib
import importlib
import json
import math
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path
from typing import Any, NoReturn

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PACKAGE = REPO_ROOT / "work" / "extract" / "equator_s1"  # O7
EXTRACTOR_SCRIPT = REPO_ROOT / "tools" / "extract" / "extract_equator.py"  # back-compat default only, see extractor_script_for()


def extractor_script_for(manifest: dict) -> Path:
    """Resolves which extractor script produced a package, generalizing the disabled-count/
    reproducibility checks below to run against whichever map's own extractor a package says it
    came from (critic amendment #3: "validate per imported scene against Arena" must work for
    Bay5's own extraction too, not just Equator's) instead of hardcoding extract_equator.py.
    manifest.source.extractor_script is written by every extractor since this field was added
    (extract_equator.py writes "tools/extract/extract_equator.py", extract_bay5.py writes
    "tools/extract/extract_bay5.py"); an older package predating the field falls back to
    EXTRACTOR_SCRIPT (extract_equator.py), preserving this validator's pre-generalization
    behavior for byte-identical Equator packages."""
    rel = manifest.get("source", {}).get("extractor_script")
    if not rel:
        return EXTRACTOR_SCRIPT
    allowed = {f"tools/extract/{name}.py" for name in ("extract_equator", "extract_bay5", "extract_map")}
    if rel not in allowed:
        die(f"unrecognized extractor entrypoint: {rel!r}; package metadata cannot choose code to execute")
    return REPO_ROOT / rel


def is_generic_package(manifest: dict) -> bool:
    return manifest.get("source", {}).get("extractor_script") == "tools/extract/extract_map.py"


def generic_plan_for(manifest: dict):
    sys.path.insert(0, str(REPO_ROOT / "tools" / "extract"))
    from source_plan import PlanError, validate_manifest_plan
    try:
        if not (REPO_ROOT / "tools" / "extract" / "extract_map.py").is_file():
            raise PlanError("generic extractor entrypoint missing")
        return validate_manifest_plan(manifest, repo=REPO_ROOT)
    except PlanError as exc:
        die(f"generic package source provenance FAILED: {exc}")

ID_RE = re.compile(r"^[0-9a-f]{16}$")

MESH_MAGIC = b"AMPM"
MESH_VERSION = 1
MESH_HEADER_SIZE = 16  # SS C.2: magic(4)+version(4)+vertex_count(4)+channel_mask(2)+index_format(1)+submesh_count(1)
SUBMESH_ENTRY_SIZE = 9  # uint32 index_start + uint32 index_count + uint8 topology, no padding

# SS C.6 -- the exhaustive component type set WP1 is allowed to record for the spike.
KNOWN_COMPONENT_TYPES = {
    "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "MeshCollider",
    "BoxCollider", "SphereCollider", "CapsuleCollider", "Light", "LODGroup",
    "ReflectionProbe", "BallisticCollider",
}
RENDERER_COMPONENT_TYPES = {"MeshRenderer", "SkinnedMeshRenderer"}

# fix-enabled-state -- the component types §C.6's schema records an "enabled" field for (every
# type with a real Unity m_Enabled field; MeshFilter has none, ReflectionProbe is never imported).
ENABLED_COMPONENT_TYPES = {
    "MeshRenderer", "SkinnedMeshRenderer", "MeshCollider", "BoxCollider",
    "SphereCollider", "CapsuleCollider", "Light", "LODGroup",
}

# fix-enabled-state -- ground truth, computed directly against Arena's own level51/53/55/57/58 via
# UnityPy during this task's root-cause investigation (docs/progress/fix-enabled-state.md; the
# playtest finding of large white untextured blocks blocking walkways traced to these components
# being enabled in our bundle despite being disabled in Arena). Used as a fallback ONLY when
# Arena's source tree is not reachable from this machine right now (same SKIP-not-FAIL pattern as
# run_reproducibility_checks below) -- count_disabled_in_arena() recomputes this fresh from source
# whenever it is reachable, since a stale hardcoded table would silently stop catching a real
# regression (e.g. a future Arena content update, or this check itself rotting).
KNOWN_ARENA_DISABLED_COUNTS: dict[str, dict[str, int]] = {
    "Arena_Equator": {"MeshRenderer": 4, "MeshCollider": 25},
    "Arena_Equator_DesignStuff": {"MeshRenderer": 2, "MeshCollider": 6, "BoxCollider": 380},
    "Arena_Equator_Lighting": {"MeshRenderer": 25, "BoxCollider": 19},
    "Arena_Equator_TeamFight_DesignStuff": {"MeshRenderer": 49},
    "Arena_Equator_TeamFight": {},
}

FAILURES: list[str] = []
WARNINGS: list[str] = []


def check(cond: bool, msg: str) -> bool:
    if not cond:
        FAILURES.append(msg)
        print(f"[validate_extract] FAIL: {msg}", file=sys.stderr)
    else:
        print(f"[validate_extract]  ok : {msg}")
    return cond


def warn(msg: str) -> None:
    WARNINGS.append(msg)
    print(f"[validate_extract] WARN: {msg}", file=sys.stderr)


def die(msg: str) -> NoReturn:
    print(f"[validate_extract] FATAL: {msg}", file=sys.stderr)
    sys.exit(1)


def load_json(path: Path) -> Any:
    if not path.is_file():
        die(f"expected file not found: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        die(f"{path} is not valid JSON: {e}")


# ---------------------------------------------------------------------------
# 1-3: manifest shape / errors / id format
# ---------------------------------------------------------------------------

def validate_manifest_shape(manifest: dict, package: Path) -> None:
    required = {"schema_version", "source", "scenes", "meshes", "textures", "materials",
                "anchors_reference", "errors"}
    missing = required - manifest.keys()
    check(not missing, f"manifest.json has all SS C.1 required top-level keys (missing: {sorted(missing)})")

    errors = manifest.get("errors")
    check(errors == [], f"manifest.json.errors == [] (SS B.WP1 hard acceptance gate; got {errors!r})")

    warnings = manifest.get("warnings")
    if warnings:
        warn(f"manifest.json.warnings is non-empty ({len(warnings)}): {warnings!r} "
             f"(not a SS B.WP1 acceptance-gate failure, but worth the lead's attention)")

    for section in ("meshes", "materials", "textures"):
        ids = manifest.get(section, {})
        bad_ids = [i for i in ids if not ID_RE.match(i)]
        check(not bad_ids,
              f"every {section} id matches SS C's 16-char lowercase-hex contract (bad: {bad_ids[:10]}"
              f"{'...' if len(bad_ids) > 10 else ''})")


# ---------------------------------------------------------------------------
# 4: scenes -- structural + cross-reference integrity
# ---------------------------------------------------------------------------

def census_null_mesh_colliders(manifest: dict) -> Counter:
    """Read null collider references from every selected source scene, not package claims."""
    import UnityPy
    _, plan = generic_plan_for(manifest)
    source = Path(manifest["source"]["arena_data_root"]).resolve()
    env = UnityPy.Environment(path=str(source))
    result = Counter()
    for scene in plan["scenes"]:
        file = env.load_file(str(source / f"level{scene['index']}"))
        for obj in file.objects.values():
            if obj.type.name != "MeshCollider":
                continue
            tree = obj.read_typetree()
            pointer = tree.get("m_Mesh")
            if not isinstance(pointer, dict) or type(pointer.get("m_PathID")) is not int:
                raise ValueError("source MeshCollider has no readable mesh pointer")
            if pointer["m_PathID"] != 0:
                continue
            owner = tree["m_GameObject"]
            if owner.get("m_FileID") != 0 or not owner.get("m_PathID"):
                raise ValueError("source null MeshCollider owner is not a local GameObject")
            result[(scene["name"], str(owner["m_PathID"]))] += 1
    return result


def validate_scenes(manifest: dict, package: Path) -> dict:
    """Returns aggregate stats for the closing summary."""
    stats = {
        "objects": 0, "mesh_filters": 0, "mesh_renderers": 0, "skinned_mesh_renderers": 0,
        "mesh_colliders": 0, "box_colliders": 0, "sphere_colliders": 0, "capsule_colliders": 0,
        "lights": 0, "lod_groups": 0, "reflection_probes": 0,
        "convex_colliders": 0, "nonconvex_colliders": 0,
        "distinct_shaders": set(), "normal_map_textures": 0,
    }
    mesh_ids = set(manifest.get("meshes", {}).keys())
    material_ids = set(manifest.get("materials", {}).keys())
    source_null_colliders, actual_null_colliders = Counter(), Counter()
    generic = is_generic_package(manifest)
    if generic:
        try:
            source_null_colliders = census_null_mesh_colliders(manifest)
        except Exception as exc:
            check(False, f"source null MeshCollider census failed: {exc}")

    for scene_name in manifest.get("scenes", []):
        scene_path = package / "scenes" / f"{scene_name}.json"
        if not check(scene_path.is_file(), f"scenes/{scene_name}.json exists (listed in manifest.scenes)"):
            continue
        scene = load_json(scene_path)
        check(scene.get("scene_name") == scene_name,
              f"scenes/{scene_name}.json: scene_name field matches filename (got {scene.get('scene_name')!r})")

        objects = scene.get("objects", [])
        obj_by_id: dict[str, dict] = {}
        dup_ids = []
        for obj in objects:
            oid = obj.get("id")
            if oid in obj_by_id:
                dup_ids.append(oid)
            obj_by_id[oid] = obj
        check(not dup_ids, f"scenes/{scene_name}.json: object ids are unique (duplicates: {dup_ids[:10]})")

        # renderer presence map, needed for the LODGroup cross-check below
        has_renderer: dict[str, bool] = {
            obj.get("id"): any(c.get("type") in RENDERER_COMPONENT_TYPES for c in obj.get("components", []))
            for obj in objects
        }

        stats["objects"] += len(objects)
        scene_bad_refs = 0
        for obj in objects:
            oid = obj.get("id")
            name = obj.get("name", "<unnamed>")

            parent_id = obj.get("parent_id")
            if parent_id is not None and parent_id not in obj_by_id:
                scene_bad_refs += 1
                check(False, f"scenes/{scene_name}.json: object {oid!r} ({name}) parent_id "
                              f"{parent_id!r} resolves within the scene")

            for comp in obj.get("components", []):
                ctype = comp.get("type")
                if ctype not in KNOWN_COMPONENT_TYPES:
                    scene_bad_refs += 1
                    check(False, f"scenes/{scene_name}.json: object {oid!r} ({name}) has an "
                                  f"out-of-scope component type {ctype!r} (SS C.6's set is exhaustive)")
                    continue

                if ctype == "MeshFilter":
                    stats["mesh_filters"] += 1
                    mid = comp.get("mesh_id")
                    if mid not in mesh_ids:
                        scene_bad_refs += 1
                        check(False, f"scenes/{scene_name}.json: object {oid!r} ({name}) "
                                      f"MeshFilter.mesh_id {mid!r} resolves under manifest.meshes")
                elif ctype in ("MeshRenderer", "SkinnedMeshRenderer"):
                    stats["mesh_renderers" if ctype == "MeshRenderer" else "skinned_mesh_renderers"] += 1
                    for mat_id in comp.get("material_ids", []):
                        if generic and ctype == "MeshRenderer" and mat_id is None:
                            # Accepted only after the independent exact source-slot check below.
                            continue
                        if mat_id not in material_ids:
                            scene_bad_refs += 1
                            check(False, f"scenes/{scene_name}.json: object {oid!r} ({name}) "
                                          f"{ctype}.material_ids contains {mat_id!r}, not in manifest.materials")
                elif ctype == "MeshCollider":
                    stats["mesh_colliders"] += 1
                    stats["convex_colliders" if comp.get("convex") else "nonconvex_colliders"] += 1
                    mid = comp.get("mesh_id")
                    source_null = (generic and "mesh_id" in comp and mid is None
                                   and source_null_colliders[(scene_name, oid)] > 0)
                    if source_null:
                        actual_null_colliders[(scene_name, oid)] += 1
                    elif mid not in mesh_ids:
                        scene_bad_refs += 1
                        check(False, f"scenes/{scene_name}.json: object {oid!r} ({name}) "
                                      f"MeshCollider.mesh_id {mid!r} resolves under manifest.meshes")
                elif ctype == "BoxCollider":
                    stats["box_colliders"] += 1
                elif ctype == "SphereCollider":
                    stats["sphere_colliders"] += 1
                elif ctype == "CapsuleCollider":
                    stats["capsule_colliders"] += 1
                elif ctype == "Light":
                    stats["lights"] += 1
                elif ctype == "ReflectionProbe":
                    stats["reflection_probes"] += 1
                elif ctype == "LODGroup":
                    stats["lod_groups"] += 1
                    for lod in comp.get("lods", []):
                        for rid in lod.get("renderer_object_ids", []):
                            if rid not in obj_by_id:
                                scene_bad_refs += 1
                                check(False, f"scenes/{scene_name}.json: object {oid!r} ({name}) "
                                              f"LODGroup lod renderer_object_id {rid!r} is not an object "
                                              f"id in this scene")
                            elif not has_renderer.get(rid):
                                # Not a dangling reference (the id DOES exist in this scene, unlike
                                # the branch above) -- the referenced object's own in-source renderer
                                # is a type outside COMPONENT_BUILDERS' exhaustive allowlist (e.g. a
                                # ParticleSystemRenderer -- observed on Bay5's real
                                # "Land_Cruiser_close" prop, whose exhaust-particle child is a real,
                                # legitimate LOD slot in Arena's own authored data, not an extractor
                                # defect). SceneAssembler.AttachLodGroup already treats this as
                                # non-fatal by design ("intentionally omits unresolved ids rather
                                # than inserting null entries", its own comment) -- match that
                                # severity here: WARN, not FAIL, since the built bundle's actual
                                # behavior for this case is a graceful, logged omission, never a
                                # crash or a null LOD slot.
                                warn(f"scenes/{scene_name}.json: object {oid!r} ({name}) LODGroup lod "
                                     f"renderer_object_id {rid!r} points at an object with no "
                                     f"MeshRenderer/SkinnedMeshRenderer component (its own renderer, if "
                                     f"any, is an out-of-scope type not in COMPONENT_BUILDERS) -- "
                                     f"SceneAssembler.AttachLodGroup omits this LOD slot gracefully "
                                     f"(logged, non-fatal), so this is not a SS B.WP1 acceptance-gate "
                                     f"failure")

        if scene_bad_refs == 0:
            print(f"[validate_extract]  ok : scenes/{scene_name}.json: {len(objects)} objects, "
                  f"all component references resolve")

    if generic:
        check(actual_null_colliders == source_null_colliders,
              "explicit null MeshCollider references exactly match source scene/object counts")
    return stats


def census_mixed_material_slots(manifest: dict) -> list[dict]:
    """Independently reconstruct mixed arrays from all selected source renderers.

    Discovery never relies on package provenance: dropping the record together
    with a null slot must still fail. Only mixed arrays are the new contract.
    """
    import UnityPy
    _, plan = generic_plan_for(manifest)
    source = Path(manifest['source']['arena_data_root']).resolve()
    env = UnityPy.Environment(path=str(source))
    result = []
    for scene in plan['scenes']:
        basename = f"level{scene['index']}"
        file = env.load_file(str(source / basename))
        for renderer in sorted(file.objects.values(), key=lambda obj: obj.path_id):
            if renderer.type.name != 'MeshRenderer':
                continue
            tree = renderer.read_typetree()
            pointers = tree['m_Materials']
            null_slots = [i for i, pointer in enumerate(pointers) if pointer['m_PathID'] == 0]
            if not null_slots or len(null_slots) == len(pointers):
                continue
            owner = tree['m_GameObject']
            if owner['m_FileID'] != 0 or not owner['m_PathID']:
                raise ValueError('source mixed renderer has no local GameObject owner')
            ids, materials = [], []
            for pointer in renderer.read().m_Materials:
                if pointer.path_id == 0:
                    ids.append(None)
                    materials.append(None)
                    continue
                material = pointer.deref()
                if material.type.name != 'Material':
                    raise ValueError('source renderer points to a non-Material asset')
                name = Path(material.assets_file.name).name
                ids.append(hashlib.sha1(f'{name}:{material.path_id}:Material'.encode()).hexdigest()[:16])
                materials.append({'source_file': name, 'source_path_id': material.path_id})
            if len(ids) != len(pointers) or [i for i, value in enumerate(ids) if value is None] != null_slots:
                raise ValueError('typed source material array disagrees with serialized PPtrs')
            result.append({'schema_version': 1, 'scene': scene['name'], 'source_file': basename,
                           'source_renderer_path_id': renderer.path_id,
                           'source_game_object_path_id': owner['m_PathID'],
                           'source_object_sha256': hashlib.sha256(renderer.get_raw_data()).hexdigest(),
                           'slot_count': len(ids), 'null_slots': null_slots,
                           'material_ids': ids, 'source_materials': materials})
    return result


def material_slot_preservation_errors(records, required: list[dict], scenes: dict,
                                      material_ids: set) -> list[str]:
    """Compare manifest and exact scene slots to fresh, independently read source."""
    errors = []
    if not isinstance(records, list) or any(not isinstance(record, dict) for record in records):
        return ['material_slot_preservation must be a list of source records']
    if any(not isinstance(record.get('scene'), str) or type(record.get('source_renderer_path_id')) is not int
           for record in records):
        return ['material slot provenance has invalid renderer identity']
    key = lambda record: (record['scene'], record['source_renderer_path_id'])
    expected = {key(record): record for record in required}
    recorded = {key(record): record for record in records}
    if len(recorded) != len(records) or set(recorded) != set(expected):
        errors.append('material slot provenance does not exactly cover source mixed renderers')
    for identity, record in recorded.items():
        if record != expected.get(identity):
            errors.append(f'{identity}: material slot provenance differs from actual source')
    expected_owners = {(record['scene'], str(record['source_game_object_path_id'])) for record in required}
    for record in required:
        scene, owner = record['scene'], str(record['source_game_object_path_id'])
        objects = [obj for obj in scenes.get(scene, {}).get('objects', []) if obj.get('id') == owner]
        if len(objects) != 1:
            errors.append(f'{scene}:{owner}: source mixed renderer owner missing or duplicated')
            continue
        components = [comp for comp in objects[0].get('components', []) if comp.get('type') == 'MeshRenderer']
        if len(components) != 1 or components[0].get('material_ids') != record['material_ids']:
            errors.append(f'{scene}:{owner}: material slot positions/assignments differ from actual source')
        if any(value is not None and value not in material_ids for value in record['material_ids']):
            errors.append(f'{scene}:{owner}: assigned source material is absent from the package')
    for scene, payload in scenes.items():
        for obj in payload.get('objects', []):
            for comp in obj.get('components', []):
                if (comp.get('type') == 'MeshRenderer' and None in comp.get('material_ids', [])
                        and (scene, obj.get('id')) not in expected_owners):
                    errors.append(f'{scene}:{obj.get("id")}: null material slots lack a matching source mixed renderer')
    return errors


def validate_material_slot_preservation(manifest: dict, package: Path) -> None:
    records = manifest.get('material_slot_preservation', [])
    if not is_generic_package(manifest):
        if records:
            check(False, 'material slot preservation belongs only to generic source-plan packages')
        return
    try:
        required = census_mixed_material_slots(manifest)
        scenes = {name: load_json(package / 'scenes' / (name + '.json')) for name in manifest['scenes']}
        errors = material_slot_preservation_errors(records, required, scenes, set(manifest['materials']))
        for error in errors:
            check(False, error)
        if not errors:
            check(True, f'exact mixed material slot positions match all selected source renderers ({len(required)} records)')
    except Exception as exc:
        check(False, f'material slot source validation failed: {exc}')


def collect_shader_and_texture_stats(manifest: dict, package: Path, stats: dict) -> None:
    for mat_id, meta in manifest.get("materials", {}).items():
        stats["distinct_shaders"].add(meta.get("shader"))
    for tex_id, meta in manifest.get("textures", {}).items():
        if meta.get("is_normal_map"):
            stats["normal_map_textures"] += 1


# ---------------------------------------------------------------------------
# 5: materials/<id>.json
# ---------------------------------------------------------------------------

def validate_materials(manifest: dict, package: Path) -> None:
    texture_ids = set(manifest.get("textures", {}).keys())
    bad = 0
    n = 0
    for mat_id, meta in manifest.get("materials", {}).items():
        n += 1
        mat_path = package / meta["file"]
        if not mat_path.is_file():
            bad += 1
            check(False, f"materials/{mat_id}.json: file exists at manifest-declared path ({meta['file']})")
            continue
        mat = load_json(mat_path)
        shader = mat.get("shader")
        if not shader or not isinstance(shader, str):
            bad += 1
            check(False, f"materials/{mat_id}.json: has a non-empty string 'shader' field (got {shader!r})")
        declared_shader = meta.get("shader")
        if declared_shader != shader:
            bad += 1
            check(False, f"materials/{mat_id}.json: manifest.materials[{mat_id}].shader "
                          f"({declared_shader!r}) matches the material file's own shader field ({shader!r})")
        for error in generated_terrain_material_errors(mat, meta, manifest.get("textures", {})):
            bad += 1
            check(False, f"materials/{mat_id}.json: {error}")
        for prop, tex_env in (mat.get("tex_envs") or {}).items():
            tid = tex_env.get("texture_id")
            if tid is not None and tid not in texture_ids:
                bad += 1
                check(False, f"materials/{mat_id}.json: tex_envs[{prop!r}].texture_id {tid!r} "
                              f"resolves under manifest.textures")
    if bad == 0:
        print(f"[validate_extract]  ok : all {n} materials/*.json parse, shader fields match manifest, "
              f"every non-null tex_envs texture_id resolves")


# ---------------------------------------------------------------------------
# 6: meshes/<id>.mesh.bin -- independent SS C.2 binary re-parse
# ---------------------------------------------------------------------------

def parse_mesh_bin(path: Path) -> dict:
    """Independent re-implementation of SS C.2's byte layout -- deliberately not importing
    tools/extract/mesh_bin.py, so this is a genuine second decoder, not a re-run of WP1's own
    write-side logic against itself."""
    data = path.read_bytes()
    if len(data) < MESH_HEADER_SIZE:
        raise ValueError(f"file is only {len(data)} bytes, smaller than the {MESH_HEADER_SIZE}-byte header")

    magic = data[0:4]
    version, vertex_count = struct.unpack_from("<II", data, 4)
    channel_mask, = struct.unpack_from("<H", data, 12)
    index_format, submesh_count = struct.unpack_from("<BB", data, 14)

    off = MESH_HEADER_SIZE
    off += vertex_count * 3 * 4  # positions, always present
    channel_vertex_bytes = {1: 12, 2: 16, 3: 16}  # normal, tangent, color -> bytes/vertex
    for bit, nbytes in channel_vertex_bytes.items():
        if channel_mask & (1 << bit):
            off += vertex_count * nbytes
    for n in range(8):
        if channel_mask & (1 << (4 + n)):
            off += vertex_count * 2 * 4  # uvN: 2 floats/vertex

    submeshes = []
    for _ in range(submesh_count):
        if off + SUBMESH_ENTRY_SIZE > len(data):
            raise ValueError(f"truncated before submesh table finished (need {submesh_count} entries)")
        index_start, index_count, topology = struct.unpack_from("<IIB", data, off)
        submeshes.append((index_start, index_count, topology))
        off += SUBMESH_ENTRY_SIZE

    index_elem_size = 2 if index_format == 0 else 4
    remaining = len(data) - off
    expected_total_indices = sum(c for _, c, _ in submeshes)
    actual_total_indices = remaining // index_elem_size if index_elem_size else -1

    return {
        "magic": magic, "version": version, "vertex_count": vertex_count,
        "channel_mask": channel_mask, "index_format": index_format,
        "submesh_count": submesh_count, "submeshes": submeshes,
        "file_size": len(data), "header_and_data_end_offset": off,
        "remaining_bytes": remaining, "index_elem_size": index_elem_size,
        "expected_total_indices": expected_total_indices,
        "actual_total_indices": actual_total_indices,
    }


def validate_meshes(manifest: dict, package: Path) -> None:
    bad = 0
    n = 0
    for mesh_id, meta in manifest.get("meshes", {}).items():
        n += 1
        mesh_path = package / meta["file"]
        if not mesh_path.is_file():
            bad += 1
            check(False, f"meshes/{mesh_id}.mesh.bin: file exists at manifest-declared path ({meta['file']})")
            continue
        try:
            parsed = parse_mesh_bin(mesh_path)
        except (ValueError, struct.error) as e:
            bad += 1
            check(False, f"meshes/{mesh_id}.mesh.bin: independently parses per SS C.2's binary layout "
                          f"(FAILED: {e})")
            continue

        local_bad = False
        if parsed["magic"] != MESH_MAGIC:
            local_bad = True
            check(False, f"meshes/{mesh_id}.mesh.bin: magic == b'AMPM' (got {parsed['magic']!r})")
        if parsed["version"] != MESH_VERSION:
            local_bad = True
            check(False, f"meshes/{mesh_id}.mesh.bin: version == 1 (got {parsed['version']})")
        if parsed["vertex_count"] != meta["vertex_count"]:
            local_bad = True
            check(False, f"meshes/{mesh_id}.mesh.bin: decoded vertex_count ({parsed['vertex_count']}) "
                          f"matches manifest ({meta['vertex_count']})")
        if parsed["submesh_count"] != meta["submesh_count"]:
            local_bad = True
            check(False, f"meshes/{mesh_id}.mesh.bin: decoded submesh_count ({parsed['submesh_count']}) "
                          f"matches manifest ({meta['submesh_count']})")
        if parsed["expected_total_indices"] != parsed["actual_total_indices"]:
            local_bad = True
            check(False, f"meshes/{mesh_id}.mesh.bin: index buffer length ({parsed['actual_total_indices']}"
                          f" elements, {parsed['remaining_bytes']} bytes) == sum of submeshes' index_count "
                          f"({parsed['expected_total_indices']}) -- SS C.2's declared total-length contract")
        if parsed["remaining_bytes"] % max(parsed["index_elem_size"], 1) != 0:
            local_bad = True
            check(False, f"meshes/{mesh_id}.mesh.bin: trailing index bytes ({parsed['remaining_bytes']}) "
                          f"are an exact multiple of the index element size ({parsed['index_elem_size']})")
        if local_bad:
            bad += 1

    if bad == 0:
        print(f"[validate_extract]  ok : all {n} meshes/*.mesh.bin independently re-parse per SS C.2, "
              f"vertex/submesh counts match manifest, index buffer length matches submesh totals exactly")


MESH_CHANNELS = [("positions", 3), ("normals", 3), ("tangents", 4), ("colors", 4)] + [(f"uv{i}", 2) for i in range(8)]


def independent_packed_numbers(field: dict, floating: bool = False):
    """Independent bit-matrix decoder; never calls the extractor or UnityPy's decoder."""
    import numpy as np
    n, width = field["m_NumItems"], field["m_BitSize"]
    data = bytes(field["m_Data"])
    if type(n) is not int or n < 0 or type(width) is not int or not 0 <= width <= 32 or len(data) * 8 < n * width:
        raise ValueError("invalid/truncated source packed bit vector")
    if width:
        bits = np.unpackbits(np.frombuffer(data, np.uint8), bitorder="little")[:n * width]
        values = bits.reshape(n, width).astype(np.uint64) @ (np.uint64(1) << np.arange(width, dtype=np.uint64))
    else:
        values = np.zeros(n, dtype=np.uint64)
    if not floating:
        return values
    start, span = field["m_Start"], field["m_Range"]
    if not math.isfinite(start) or not math.isfinite(span) or span < 0:
        raise ValueError("nonfinite/negative packed float range")
    return values.astype(np.float64) * (span / ((1 << width) - 1) if width else 0) + start


def independent_special_mesh(tree: dict, kind: str) -> dict:
    """Reconstruct the supported source geometry directly from serialized fields."""
    import numpy as np
    raw = tree.get("m_VertexData") or {}
    stream = tree.get("m_StreamData") or {}
    packed = tree.get("m_CompressedMesh") or {}
    if raw.get("m_VertexCount") != 0 or raw.get("m_DataSize") or tree.get("m_IndexBuffer") or stream.get("size") or stream.get("path"):
        raise ValueError("special source mesh contains unexpected raw/streamed geometry")
    if tree.get("m_BindPose") or tree.get("m_BoneNameHashes") or (tree.get("m_VariableBoneCountWeights") or {}).get("m_Data"):
        raise ValueError("special source mesh contains unsupported skin data")
    if any((tree.get("m_Shapes") or {}).get(key) for key in ("vertices", "shapes", "channels", "fullWeights")):
        raise ValueError("special source mesh contains unsupported blendshape data")
    fmt = tree.get("m_IndexFormat")
    if fmt not in (0, 1):
        raise ValueError("invalid source mesh index format")
    submeshes = [(sm["firstByte"] // (2 if fmt == 0 else 4), sm["indexCount"], sm["topology"])
                 for sm in tree["m_SubMeshes"]]
    if any(sm["firstByte"] % (2 if fmt == 0 else 4) or sm.get("baseVertex", 0) for sm in tree["m_SubMeshes"]):
        raise ValueError("unaligned source submesh or unsupported base vertex")
    if kind == "source_empty":
        if tree.get("m_MeshCompression") != 0:
            raise ValueError("source_empty provenance cannot conceal compressed geometry")
        for key in ("m_Vertices", "m_Normals", "m_Tangents", "m_Colors", "m_UV", "m_UV1", "m_Skin", "m_ShapeVertices", "m_CollisionTriangles"):
            if tree.get(key):
                raise ValueError(f"source_empty mesh contains {key}")
        for key, field in packed.items():
            if key == "m_UVInfo":
                if field:
                    raise ValueError("source_empty mesh has compressed UV declarations")
            elif field and (field.get("m_NumItems") or field.get("m_Data")):
                raise ValueError("source_empty mesh contains packed geometry")
        if any(any(sm.get(key, 0) for key in ("firstByte", "indexCount", "topology", "baseVertex", "firstVertex", "vertexCount"))
               for sm in tree["m_SubMeshes"]):
            raise ValueError("source_empty mesh contains nonempty submesh records")
        channels = {"positions": np.empty((0, 3), dtype=np.float64)}
        for index, channel in enumerate(raw.get("m_Channels", [])):
            dimension = channel.get("dimension", 0)
            if dimension:
                if index >= len(MESH_CHANNELS) or dimension != MESH_CHANNELS[index][1]:
                    raise ValueError("source_empty vertex declaration is unsupported")
                channels[MESH_CHANNELS[index][0]] = np.empty((0, dimension), dtype=np.float64)
        indices = np.empty(0, dtype=np.uint64)
    elif kind == "packed_bit_vectors":
        if tree.get("m_MeshCompression") not in (1, 2, 3) or any(c.get("dimension") for c in raw.get("m_Channels", [])):
            raise ValueError("packed provenance must describe a completely compressed mesh")
        for key in ("m_Weights", "m_BoneIndices", "m_BindPoses", "m_Colors"):
            if (packed.get(key) or {}).get("m_NumItems"):
                raise ValueError(f"unsupported packed channel {key}")
        positions = independent_packed_numbers(packed["m_Vertices"], True).reshape(-1, 3)
        count = len(positions)
        if count == 0:
            raise ValueError("packed positions are empty")
        channels = {"positions": positions}
        for key, name, sign_key, sign_width in (("m_Normals", "normals", "m_NormalSigns", 1),
                                                ("m_Tangents", "tangents", "m_TangentSigns", 2)):
            if not packed[key]["m_NumItems"]:
                if packed[sign_key]["m_NumItems"]:
                    raise ValueError("packed signs without their channel")
                continue
            xy = independent_packed_numbers(packed[key], True).reshape(count, 2)
            signs = independent_packed_numbers(packed[sign_key]).reshape(count, sign_width)
            if np.any(signs > 1):
                raise ValueError("invalid packed normal/tangent signs")
            length_squared = np.sum(xy * xy, axis=1)
            z = np.sqrt(np.maximum(0, 1 - length_squared))
            outside = length_squared > 1
            xy[outside] /= np.sqrt(length_squared[outside])[:, None]
            z[signs[:, 0] == 0] *= -1
            channels[name] = np.column_stack((xy, z, np.where(signs[:, 1] > 0, 1., -1.))) if sign_width == 2 else np.column_stack((xy, z))
        if packed["m_FloatColors"]["m_NumItems"]:
            channels["colors"] = independent_packed_numbers(packed["m_FloatColors"], True).reshape(count, 4)
        uv = independent_packed_numbers(packed["m_UV"], True)
        info = packed.get("m_UVInfo") or 0
        declarations = [(number, (info >> (number * 4)) & 15) for number in range(8)] if info else [(number, 5) for number in range(len(uv) // (count * 2))]
        offset = 0
        for number, declaration in declarations:
            if declaration == 0:
                continue
            if declaration != 5 or number >= 8:
                raise ValueError("unsupported source packed UV declaration")
            channels[f"uv{number}"] = uv[offset:offset + count * 2].reshape(count, 2)
            offset += count * 2
        if offset != len(uv):
            raise ValueError("source UV data is not completely accounted for")
        indices = independent_packed_numbers(packed["m_Triangles"])
        if not len(indices) or np.any(indices >= count):
            raise ValueError("packed indices missing or outside source vertex buffer")
    else:
        raise ValueError(f"unknown mesh compatibility kind: {kind}")
    if sum(sm[1] for sm in submeshes) != len(indices) or any(sm[2] != 0 for sm in submeshes):
        raise ValueError("source submesh ranges do not account for all triangle indices")
    mask = sum(1 << index for index, (name, _) in enumerate(MESH_CHANNELS) if name in channels)
    return {"channels": channels, "indices": indices, "submeshes": submeshes, "channel_mask": mask,
            "index_format": fmt, "vertex_count": len(channels["positions"]), "submesh_count": len(submeshes)}


def special_mesh_output_errors(tree: dict, record: dict, path: Path) -> list[str]:
    import numpy as np
    expected = independent_special_mesh(tree, record["kind"])
    parsed = parse_mesh_bin(path)
    errors = []
    for field in ("vertex_count", "submesh_count", "channel_mask", "index_format"):
        if record.get(field) != expected[field] or parsed[field] != expected[field]:
            errors.append(f"{field} differs from actual source geometry")
    if parsed["magic"] != MESH_MAGIC or parsed["version"] != MESH_VERSION or parsed["submeshes"] != expected["submeshes"]:
        errors.append("AMPM header/submesh table differs from source")
    raw = path.read_bytes()
    offset = 16
    for bit, (name, width) in enumerate(MESH_CHANNELS):
        if not parsed["channel_mask"] & (1 << bit):
            continue
        values = np.frombuffer(raw, "<f4", parsed["vertex_count"] * width, offset).reshape(-1, width)
        offset += values.size * 4
        target = expected["channels"].get(name)
        if target is None or values.shape != target.shape or not np.isfinite(values).all() or not np.allclose(values, target.astype("<f4"), rtol=1e-6, atol=1e-7):
            errors.append(f"{name} values differ from independently decoded source")
    indices = np.frombuffer(raw, "<u2" if parsed["index_format"] == 0 else "<u4", offset=parsed["header_and_data_end_offset"])
    if not np.array_equal(indices, expected["indices"]):
        errors.append("index buffer differs from source")
    return errors


def census_special_source_meshes(manifest: dict):
    """Find special meshes through the selected scenes' real MF/MC references.

    Source objects are deduplicated before reading mesh metadata. This visits the
    imported scene scope only; source skinned renderers remain the legacy exclusion.
    """
    from collections import Counter
    import UnityPy
    _, plan = generic_plan_for(manifest)
    source = Path(manifest["source"]["arena_data_root"]).resolve()
    env = UnityPy.Environment(path=str(source))
    seen, special = set(), {}
    bindings = Counter()
    for scene in plan["scenes"]:
        file = env.load_file(str(source / f"level{scene['index']}"))
        for component in file.objects.values():
            kind = component.type.name
            if kind not in ("MeshFilter", "MeshCollider"):
                continue
            pointer = component.read().m_Mesh
            if pointer is None or pointer.path_id == 0:
                continue
            reader = pointer.deref()
            if reader.type.name != "Mesh":
                raise ValueError("source mesh component points to a non-Mesh asset")
            name = Path(reader.assets_file.name).name
            mesh_id = hashlib.sha1(f"{name}:{reader.path_id}:Mesh".encode()).hexdigest()[:16]
            bindings[(kind, mesh_id)] += 1
            if mesh_id in seen:
                continue
            seen.add(mesh_id)
            tree = reader.read_typetree()
            if tree.get("m_MeshCompression") != 0 or (tree.get("m_VertexData") or {}).get("m_VertexCount") == 0:
                special[mesh_id] = (name, reader.path_id, reader, tree)
    return special, Counter({key: count for key, count in bindings.items() if key[1] in special})


def validate_mesh_compatibility(manifest: dict, package: Path) -> None:
    from collections import Counter
    records = manifest.get("mesh_compatibility", [])
    zeros = {key for key, meta in manifest.get("meshes", {}).items() if meta.get("vertex_count") == 0}
    if not is_generic_package(manifest) and not records:
        return
    if not check(is_generic_package(manifest) and isinstance(records, list), "mesh compatibility provenance belongs to a generic source-plan package"):
        return
    try:
        required, source_bindings = census_special_source_meshes(manifest)
    except Exception as exc:
        check(False, f"selected source scene mesh census failed: {exc}")
        return
    zero_records = {r.get("mesh_id") for r in records if r.get("kind") == "source_empty"}
    check(zero_records == zeros, "every zero-vertex mesh has exact source_empty provenance")
    ids = [r.get("mesh_id") for r in records]
    check(len(ids) == len(set(ids)), "mesh compatibility records have unique asset identities")
    check(set(ids) == set(required), "mesh compatibility provenance exactly covers special meshes required by selected source scenes")
    references = Counter()
    for name in manifest["scenes"]:
        scene = load_json(package / "scenes" / (name + ".json"))
        references.update((c["type"], c["mesh_id"]) for obj in scene["objects"] for c in obj.get("components", [])
                          if c.get("type") in ("MeshFilter", "MeshCollider") and c.get("mesh_id") in required)
    check(references == source_bindings, "every special mesh preserves exact selected-source MeshFilter/MeshCollider attachment counts")
    source = Path(manifest["source"]["arena_data_root"]).resolve()
    for record in records:
        mesh_id = record.get("mesh_id")
        try:
            name, source_id = record["source_file"], record["source_path_id"]
            path = (source / name).resolve()
            if not isinstance(name, str) or Path(name).name != name or not path.is_relative_to(source) or type(source_id) is not int:
                raise ValueError("invalid mesh source file/path ID")
            expected_id = hashlib.sha1(f"{name}:{source_id}:Mesh".encode()).hexdigest()[:16]
            if mesh_id != expected_id or mesh_id not in manifest["meshes"] or mesh_id not in required:
                raise ValueError("source mesh identity is missing, substituted or detached from the package scenes")
            actual_name, actual_id, reader, tree = required[mesh_id]
            if name != actual_name or source_id != actual_id:
                raise ValueError("provenance source identity differs from actual scene mesh reference")
            if reader.type.name != "Mesh" or hashlib.sha256(reader.get_raw_data()).hexdigest() != record.get("source_object_sha256"):
                raise ValueError("mesh source type/hash differs from provenance")
            if record.get("schema_version") != 1 or record.get("geometry_substituted") is not False or record.get("source_name") != tree["m_Name"] or record.get("source_mesh_compression") != tree["m_MeshCompression"]:
                raise ValueError("mesh compatibility provenance does not match source metadata")
            decoder = "source_empty" if record["kind"] == "source_empty" else "UnityPy.MeshHandler"
            if record.get("decoder") != decoder:
                raise ValueError("unsupported mesh decoder provenance")
            output = (package / manifest["meshes"][mesh_id]["file"]).resolve()
            if not output.is_relative_to((package / "meshes").resolve()):
                raise ValueError("special mesh output escapes package meshes")
            errors = special_mesh_output_errors(tree, record, output)
            for error in errors:
                check(False, f"mesh {mesh_id}: {error}")
            if not errors:
                check(True, f"mesh {mesh_id}: source hash, geometry/channels/indices and attached identity preserved ({record['kind']})")
        except Exception as exc:
            check(False, f"mesh {mesh_id}: source compatibility validation failed: {exc}")


# ---------------------------------------------------------------------------
# 7: textures/<id>.png
# ---------------------------------------------------------------------------

TEXTURE_DIMENSIONS = {"Tex2D", "Cube", "Tex3D", "Tex2DArray"}


def generated_terrain_texture_errors(meta: dict, package: Path) -> list[str]:
    """Strict, narrow exception to the legacy unsupported arbitrary-array fallback."""
    array = meta.get("generated_terrain_array") is True
    control = meta.get("generated_terrain_control") is True
    if not array and not control:
        return []
    errors = []
    if array and control:
        errors.append("texture cannot be both a terrain array and a control")
    if meta.get("dimension", "Tex2D") != ("Tex2DArray" if array else "Tex2D"):
        errors.append("generated terrain texture has wrong dimension")
    expected_color = "sRGB" if array and meta.get("terrain_role") == "diffuse" else "Linear"
    if meta.get("color_space") != expected_color or meta.get("is_normal_map") is not False:
        errors.append("generated terrain color-space/normal import flags are incorrect")
    if array and meta.get("terrain_role") not in ("diffuse", "normalSAO"):
        errors.append("generated terrain array role must be diffuse or normalSAO")
    width, height = meta.get("width"), meta.get("height")
    if type(width) is not int or type(height) is not int or min(width, height) <= 0:
        errors.append("generated terrain texture dimensions must be positive integers")
    if array:
        slices = meta.get("array_slices")
        depth = meta.get("depth")
        if type(depth) is not int or depth != 12:
            errors.append("generated terrain array must preserve all 12 source layers")
        if (not isinstance(slices, list) or len(slices) != depth or not slices
                or any(not isinstance(s, str) for s in slices)):
            errors.append("array_slices must contain exactly depth file paths")
            return errors
        if len(set(slices)) != len(slices) or meta.get("file") != slices[0]:
            errors.append("array slices must be unique and file must identify slice zero")
    else:
        slices = [meta.get("file")]
    from PIL import Image
    for rel in slices:
        if not isinstance(rel, str):
            errors.append("generated terrain texture path missing")
            continue
        path = (package / rel).resolve()
        if not path.is_relative_to((package / "textures").resolve()):
            errors.append(f"generated terrain texture escapes package textures: {rel}")
            continue
        try:
            with Image.open(path) as img:
                img.load()
                if img.size != (width, height) or img.mode != "RGBA":
                    errors.append(f"terrain slice {rel} must be RGBA {width}x{height}; got {img.mode} {img.size}")
        except (OSError, ValueError) as exc:
            errors.append(f"terrain slice {rel} is missing or unreadable: {exc}")
    return errors


def generated_terrain_material_errors(mat: dict, meta: dict, textures: dict) -> list[str]:
    shader = mat.get("shader")
    preserve = mat.get("preserve_shader") is True or meta.get("preserve_shader") is True
    if shader != "ArenaMapPort/Terrain" and not preserve:
        return []
    errors = []
    if shader != "ArenaMapPort/Terrain" or mat.get("preserve_shader") is not True or meta.get("preserve_shader") is not True:
        errors.append("only the exact owned ArenaMapPort/Terrain shader may be preserved")
    tex_envs, floats, colors = (mat.get(k, {}) for k in ("tex_envs", "floats", "colors"))
    for slot, role in (("_DiffuseArray", "diffuse"), ("_NormalSAOArray", "normalSAO")):
        tex = textures.get(tex_envs.get(slot, {}).get("texture_id"), {})
        if tex.get("generated_terrain_array") is not True or tex.get("terrain_role") != role or tex.get("depth") != 12:
            errors.append(f"{slot} must reference the complete generated {role} array")
    resolution = floats.get("_ControlResolution")
    if not isinstance(resolution, (float, int)) or not math.isfinite(resolution) or resolution < 2:
        errors.append("terrain control resolution must be finite and >= 2")
    for slot in ("_Control0", "_Control1", "_Control2"):
        tex = textures.get(tex_envs.get(slot, {}).get("texture_id"), {})
        if (tex.get("generated_terrain_control") is not True or tex.get("color_space") != "Linear"
                or tex.get("width") != resolution or tex.get("height") != resolution):
            errors.append(f"{slot} must reference a linear control at the declared resolution")
    if floats.get("_LayerCount") != 12:
        errors.append("terrain material must use all 12 layers")
    contrast = floats.get("_Contrast")
    if not isinstance(contrast, (int, float)) or not math.isfinite(contrast) or contrast <= 0:
        errors.append("terrain height-blend contrast must be finite and positive")
    for name in ["_UVScale"] + [f"_LayerUV{i}" for i in range(12)] + [f"_LayerAdjust{i}" for i in range(12)]:
        vec = colors.get(name)
        if (not isinstance(vec, list) or len(vec) != 4 or any(
                isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in vec)):
            errors.append(f"terrain material {name} must contain four finite source values")
    return errors


def validate_textures(manifest: dict, package: Path) -> None:
    try:
        from PIL import Image
    except ImportError:
        warn("Pillow not importable -- skipping texture dimension/mode checks (file-existence only)")
        Image = None  # noqa: N806

    bad = 0
    n = 0
    dim_counts: dict[str, int] = {}
    for tex_id, meta in manifest.get("textures", {}).items():
        n += 1
        tex_path = package / meta["file"]
        terrain_errors = generated_terrain_texture_errors(meta, package)
        for error in terrain_errors:
            bad += 1
            check(False, f"texture {tex_id}: {error}")

        # impl-texture-dimensions §1: manifest.textures[tex_id].dimension (§C-compatible
        # extension) must be one of the known shapes WP2's importer branches on -- an unknown
        # value here would silently import as a plain 2D texture downstream (WP2's own
        # ArenaPackage.TextureDimension() defaults unknown/absent to "Tex2D"), reintroducing
        # exactly the "2D texture assigned to a CUBE/3D property" bug this task fixes.
        dimension = meta.get("dimension", "Tex2D")
        dim_counts[dimension] = dim_counts.get(dimension, 0) + 1
        if dimension not in TEXTURE_DIMENSIONS:
            bad += 1
            check(False, f"textures/{tex_id}.png: manifest dimension {dimension!r} is one of "
                          f"{sorted(TEXTURE_DIMENSIONS)}")

        if dimension == "Cube":
            face_w = meta.get("cube_face_width")
            face_h = meta.get("cube_face_height")
            layout = meta.get("cube_layout")
            face_order = meta.get("cube_face_order")
            if layout != "vertical_strip" or not isinstance(face_order, list) or len(face_order) != 6:
                bad += 1
                check(False, f"textures/{tex_id}.png: Cube dimension has cube_layout=='vertical_strip' "
                              f"and a 6-entry cube_face_order (got layout={layout!r}, "
                              f"face_order={face_order!r})")
            if not (isinstance(face_w, int) and isinstance(face_h, int) and face_w > 0 and face_h > 0):
                bad += 1
                check(False, f"textures/{tex_id}.png: Cube dimension has positive integer "
                              f"cube_face_width/cube_face_height (got {face_w!r}/{face_h!r})")
            elif meta.get("width") != face_w or meta.get("height") != face_h * 6:
                bad += 1
                check(False, f"textures/{tex_id}.png: Cube strip width/height ({meta.get('width')}x"
                              f"{meta.get('height')}) == cube_face_width x (cube_face_height*6) "
                              f"({face_w}x{face_h * 6})")

        if not tex_path.is_file():
            bad += 1
            check(False, f"textures/{tex_id}.png: file exists at manifest-declared path ({meta['file']})")
            continue
        if Image is None:
            continue
        try:
            with Image.open(tex_path) as im:
                im.load()
                size_ok = im.size == (meta["width"], meta["height"])
                if not size_ok:
                    bad += 1
                    check(False, f"textures/{tex_id}.png: dimensions {im.size} match manifest "
                                  f"({meta['width']}x{meta['height']})")
                if im.mode not in ("RGBA", "RGB"):
                    bad += 1
                    check(False, f"textures/{tex_id}.png: decodes to RGBA/RGB (got mode {im.mode!r}, "
                                  f"SS C.3 requires RGBA8)")
        except Exception as e:  # noqa: BLE001 -- want a diagnosis, not a bare trace
            bad += 1
            check(False, f"textures/{tex_id}.png: opens as a valid image (Pillow raised: {e})")

    if bad == 0:
        print(f"[validate_extract]  ok : all {n} textures/*.png open cleanly, dimensions match manifest, "
              f"every dimension field valid ({dict(sorted(dim_counts.items()))})")


# ---------------------------------------------------------------------------
# Coverage: every manifest entry referenced by at least one scene/material (informational)
# ---------------------------------------------------------------------------

def check_orphans(manifest: dict, package: Path) -> None:
    referenced_meshes: set[str] = set()
    referenced_materials: set[str] = set()
    referenced_textures: set[str] = set()

    for scene_name in manifest.get("scenes", []):
        scene_path = package / "scenes" / f"{scene_name}.json"
        if not scene_path.is_file():
            continue
        scene = load_json(scene_path)
        for obj in scene.get("objects", []):
            for comp in obj.get("components", []):
                t = comp.get("type")
                if t in ("MeshFilter", "MeshCollider") and comp.get("mesh_id"):
                    referenced_meshes.add(comp["mesh_id"])
                if t in RENDERER_COMPONENT_TYPES:
                    referenced_materials.update(comp.get("material_ids", []))

    for mat_id in manifest.get("materials", {}):
        mat_path = package / manifest["materials"][mat_id]["file"]
        if not mat_path.is_file():
            continue
        mat = load_json(mat_path)
        for tex_env in (mat.get("tex_envs") or {}).values():
            tid = tex_env.get("texture_id")
            if tid:
                referenced_textures.add(tid)

    orphan_meshes = set(manifest.get("meshes", {})) - referenced_meshes
    orphan_materials = set(manifest.get("materials", {})) - referenced_materials
    orphan_textures = set(manifest.get("textures", {})) - referenced_textures

    if orphan_meshes:
        warn(f"{len(orphan_meshes)} mesh id(s) in manifest.meshes are never referenced by any scene's "
             f"MeshFilter/MeshCollider: {sorted(orphan_meshes)[:10]}"
             f"{'...' if len(orphan_meshes) > 10 else ''} (SS B.WP1 step 7's dedup contract implies "
             f"every emitted mesh is used by something; not a hard acceptance-gate failure)")
    if orphan_materials:
        warn(f"{len(orphan_materials)} material id(s) in manifest.materials are never referenced by any "
             f"scene's MeshRenderer/SkinnedMeshRenderer: {sorted(orphan_materials)[:10]}"
             f"{'...' if len(orphan_materials) > 10 else ''}")
    if orphan_textures:
        warn(f"{len(orphan_textures)} texture id(s) in manifest.textures are never referenced by any "
             f"material's tex_envs: {sorted(orphan_textures)[:10]}"
             f"{'...' if len(orphan_textures) > 10 else ''}")
    if not (orphan_meshes or orphan_materials or orphan_textures):
        print("[validate_extract]  ok : no orphaned mesh/material/texture entries "
              "(every manifest entry is referenced from at least one scene/material)")


# ---------------------------------------------------------------------------
# fix-enabled-state: per-scene disabled-component counts match Arena's own authored state
# ---------------------------------------------------------------------------

def count_disabled_in_package(manifest: dict, package: Path) -> dict[str, dict[str, int]]:
    """Per-scene, per-type count of components whose extracted record has "enabled": false --
    an explicit false only; a missing key or true both mean enabled, matching the importer's
    (SceneAssembler.cs's ReadEnabled) and this validator's own default-true contract, so a package
    extracted before this field existed correctly counts as "nothing disabled" here rather than
    erroring."""
    counts: dict[str, dict[str, int]] = {}
    for scene_name in manifest.get("scenes", []):
        scene_path = package / "scenes" / f"{scene_name}.json"
        if not scene_path.is_file():
            continue
        scene = load_json(scene_path)
        scene_counts: dict[str, int] = {}
        for obj in scene.get("objects", []):
            for comp in obj.get("components", []):
                ctype = comp.get("type")
                if ctype not in ENABLED_COMPONENT_TYPES:
                    continue
                if comp.get("enabled", True) is False:
                    scene_counts[ctype] = scene_counts.get(ctype, 0) + 1
        counts[scene_name] = scene_counts
    return counts


def terrain_scene_adjustments(scene_name: str, file, manifest: dict, package: Path) -> dict[str, int]:
    """Derive synthetic terrain counts from live source cells and inspect their output.

    Never subtract generated objects wholesale: every generated mesh and collider must
    correspond to a real source Terrain and preserve all its non-hole cells.
    """
    import numpy as np
    records = [r for r in manifest.get("terrain_conversion", []) if r.get("scene") == scene_name]
    terrains = [o for o in file.objects.values() if o.type.name == "Terrain"]
    terrain_colliders = [o for o in file.objects.values() if o.type.name == "TerrainCollider"]
    if not records and not terrains and not terrain_colliders:
        return {}
    by_terrain = {r.get("source_terrain_id"): r for r in records}
    if not check(len(by_terrain) == len(records) and set(by_terrain) == {o.path_id for o in terrains},
                 f"{scene_name}: converted Terrain records exactly cover source Terrain components"):
        return {}
    scene = load_json(package / "scenes" / (scene_name + ".json"))
    objects = {o["id"]: o for o in scene["objects"]}
    collider_by_go = {}
    for obj in terrain_colliders:
        tt = obj.read_typetree()
        collider_go = tt["m_GameObject"]["m_PathID"]
        check(collider_go not in collider_by_go, f"{scene_name}: no duplicate TerrainCollider on source object {collider_go}")
        collider_by_go[collider_go] = (obj, tt)
    terrain_gos = {obj.read_typetree()["m_GameObject"]["m_PathID"] for obj in terrains}
    check(set(collider_by_go) <= terrain_gos, f"{scene_name}: every source TerrainCollider has a converted Terrain")
    extras = {}
    for terrain_obj in terrains:
        record = by_terrain[terrain_obj.path_id]
        typed = terrain_obj.read()
        terrain = terrain_obj.read_typetree()
        data_obj = typed.m_TerrainData.deref()
        data = data_obj.read_typetree()
        heightmap = data["m_Heightmap"]
        n = heightmap["m_Resolution"]
        heights = np.asarray(heightmap["m_Heights"], dtype=np.float64).reshape(n, n)
        scale = [heightmap["m_Scale"][axis] for axis in ("x", "y", "z")]
        holes = heightmap.get("m_Holes", [])
        solid = np.asarray(holes).reshape(n - 1, n - 1) != 0 if holes else np.ones((n - 1, n - 1), dtype=bool)
        go = terrain["m_GameObject"]["m_PathID"]
        coll_entry = collider_by_go.get(go)
        enabled = bool(terrain.get("m_Enabled", 1)) and bool(terrain.get("m_DrawHeightmap", True))
        coll_enabled = bool(coll_entry[1].get("m_Enabled", 1)) if coll_entry else None
        source_data_file = Path(data_obj.assets_file.name).name
        expected_identity = {"source_gameobject_id": go, "source_terrain_id": terrain_obj.path_id,
                             "source_collider_id": coll_entry[0].path_id if coll_entry else None,
                             "terrain_data_file": source_data_file, "terrain_data_path_id": data_obj.path_id,
                             "height_resolution": n, "height_sample_count": n * n,
                             "height_denominator": 32766.0, "renderer_enabled": enabled,
                             "collider_enabled": coll_enabled, "triangle_count": int(solid.sum()) * 2,
                             "hole_cell_count": int(solid.size - solid.sum())}
        for key, value in expected_identity.items():
            check(record.get(key) == value, f"{scene_name} Terrain {terrain_obj.path_id}: source {key} preserved")
        expected_meshes = []
        for z in range(0, n - 1, 128):
            for x in range(0, n - 1, 128):
                x1, z1 = min(x + 128, n - 1), min(z + 128, n - 1)
                cell_count = int(solid[z:z1, x:x1].sum())
                if cell_count == 0:
                    continue
                mesh_id = hashlib.sha1(f"{source_data_file}:{data_obj.path_id}:TerrainV1:mesh:{x}:{z}".encode()).hexdigest()[:16]
                expected_meshes.append(mesh_id)
                raw_id = hashlib.sha1(f"{Path(file.name).name}:{terrain_obj.path_id}:TerrainV1:object:{x}:{z}:{x1}:{z1}".encode()).hexdigest()[:16]
                child_id = str(-(int(raw_id, 16) & ((1 << 63) - 1)) - 1)
                child = objects.get(child_id, {})
                check(child.get("parent_id") == str(go) and child.get("local_position") == [0, 0, 0]
                      and child.get("local_rotation") == [0, 0, 0, 1] and child.get("local_scale") == [1, 1, 1],
                      f"{scene_name} Terrain chunk {x},{z}: source parent and identity child transform preserved")
                comps = {c.get("type"): c for c in child.get("components", [])}
                check(comps.get("MeshFilter", {}).get("mesh_id") == mesh_id
                      and comps.get("MeshRenderer", {}).get("enabled") == enabled,
                      f"{scene_name} Terrain chunk {x},{z}: exact source mesh/render state")
                if coll_entry:
                    coll = comps.get("MeshCollider", {})
                    check(coll.get("mesh_id") == mesh_id and coll.get("enabled") == coll_enabled
                          and coll.get("convex") is False and coll.get("is_trigger") is False,
                          f"{scene_name} Terrain chunk {x},{z}: collision uses the identical nonconvex ground mesh")
                else:
                    check("MeshCollider" not in comps, f"{scene_name} Terrain chunk {x},{z}: no extra source-absent collider")
                mesh_meta = manifest.get("meshes", {}).get(mesh_id)
                if not check(mesh_meta is not None, f"{scene_name} Terrain chunk {x},{z}: mesh manifest entry exists"):
                    continue
                path = package / mesh_meta["file"]
                parsed = parse_mesh_bin(path)
                vertices = (x1 - x + 1) * (z1 - z + 1)
                check(parsed["vertex_count"] == vertices and parsed["actual_total_indices"] == cell_count * 6,
                      f"{scene_name} Terrain chunk {x},{z}: every height vertex and solid-cell triangle retained")
                raw = path.read_bytes()
                positions = np.frombuffer(raw, dtype="<f4", count=vertices * 3, offset=16).reshape(-1, 3)
                xx, zz = np.meshgrid(np.arange(x, x1 + 1), np.arange(z, z1 + 1))
                expected_positions = np.stack((xx * scale[0], heights[z:z1 + 1, x:x1 + 1] * scale[1] / 32766.0,
                                               zz * scale[2]), axis=-1).reshape(-1, 3)
                check(np.allclose(positions, expected_positions, rtol=1e-6, atol=1e-5),
                      f"{scene_name} Terrain chunk {x},{z}: all positions independently match source heights")
                expected_indices = []
                width = x1 - x + 1
                for row in range(z, z1):
                    for col in range(x, x1):
                        if solid[row, col]:
                            corner = (row - z) * width + col - x
                            expected_indices.extend((corner, corner + width, corner + 1,
                                                     corner + 1, corner + width, corner + width + 1))
                indices = np.frombuffer(raw, dtype="<u2" if parsed["index_format"] == 0 else "<u4",
                                        offset=parsed["header_and_data_end_offset"])
                check(np.array_equal(indices, expected_indices),
                      f"{scene_name} Terrain chunk {x},{z}: exact upward cell triangles and source holes preserved")
                has_channels = parsed["channel_mask"] == 23
                check(has_channels, f"{scene_name} Terrain chunk {x},{z}: normals, tangents and full terrain UV0 retained")
                if has_channels:
                    uv = np.frombuffer(raw, dtype="<f4", count=vertices * 2, offset=16 + vertices * 40).reshape(-1, 2)
                    expected_uv = np.stack((xx / (n - 1), zz / (n - 1)), axis=-1).reshape(-1, 2)
                    check(np.allclose(uv, expected_uv, rtol=1e-6, atol=1e-7),
                          f"{scene_name} Terrain chunk {x},{z}: UV0 addresses the entire source terrain")
                if not enabled:
                    extras["MeshRenderer"] = extras.get("MeshRenderer", 0) + 1
                if coll_entry and not coll_enabled:
                    extras["MeshCollider"] = extras.get("MeshCollider", 0) + 1
        check(record.get("mesh_ids") == expected_meshes, f"{scene_name} Terrain {terrain_obj.path_id}: complete ordered chunk mesh set")
    return extras


def count_disabled_in_arena(source_root: str | None, manifest: dict, package: Path | None = None) -> dict[str, dict[str, int]] | None:
    """Recomputes ground-truth per-scene disabled-component counts directly against Arena's own
    level<N> files via UnityPy -- independent of tools/extract/extract_equator.py's own internals
    (imports it read-only for SCENE_REGISTRY only, never re-executes its extraction logic), mirroring
    run_reproducibility_checks' own "trust the real source, not the pipeline" approach. Component
    records (GameObject/Transform/MeshRenderer/etc.) live entirely inside level<N> itself, never in
    sharedassets<N>.assets or an external file (extract_equator.py's own build_scene_graph()/
    build_components() only ever read from the level<N> SerializedFile) -- so this only needs to
    load that one file per scene, not the full multi-file environment extraction needs. Returns
    None (with a warning already recorded) if the source tree is not reachable right now."""
    strict = is_generic_package(manifest)
    generic_plan = generic_plan_for(manifest)[1] if strict else None
    if not source_root:
        warn("disabled-component ground-truth check: manifest.source.arena_data_root missing -- "
             "falling back to the hardcoded reference table (KNOWN_ARENA_DISABLED_COUNTS)")
        return None
    if not Path(source_root).is_dir():
        warn(f"disabled-component ground-truth check: Arena source root {source_root!r} is not "
             f"reachable from this machine right now -- falling back to the hardcoded reference "
             f"table from docs/progress/fix-enabled-state.md's root-cause pass "
             f"(KNOWN_ARENA_DISABLED_COUNTS, Equator only -- see that table's own note re: Bay5) "
             f"rather than failing outright (an environment gap, not necessarily a WP1 defect)")
        return None
    extractor_script = extractor_script_for(manifest)
    if not extractor_script.is_file():
        warn(f"disabled-component ground-truth check: extractor script not found at "
             f"{extractor_script} -- falling back to the hardcoded reference table")
        return None
    if generic_plan is not None:
        name_to_idx = {r["name"]: r["index"] for r in generic_plan["scenes"]}
    else:
        sys.path.insert(0, str(extractor_script.parent))
        try:
            _ex = importlib.import_module(extractor_script.stem)  # type: ignore  # noqa: PLC0415
        except Exception as e:  # noqa: BLE001
            warn(f"disabled-component ground-truth check: could not import {extractor_script.name} to "
                 f"recover its SCENE_REGISTRY ({e}) -- falling back to the hardcoded reference table")
            return None
        name_to_idx = {v: k for k, v in _ex.SCENE_REGISTRY.items()}
    try:
        import UnityPy  # noqa: PLC0415
    except ImportError:
        if strict:
            die("generic package disabled-component check requires UnityPy")
        warn("disabled-component ground-truth check: UnityPy not importable -- falling back to "
             "the hardcoded reference table")
        return None

    out: dict[str, dict[str, int]] = {}
    for scene_name in manifest.get("scenes", []):
        idx = name_to_idx.get(scene_name)
        if idx is None:
            if strict:
                die(f"generic package scene absent from verified registry: {scene_name}")
            warn(f"disabled-component ground-truth check: {scene_name!r} not in "
                 f"{extractor_script.name}'s SCENE_REGISTRY -- skipped")
            continue
        level_path = Path(source_root) / f"level{idx}"
        if not level_path.is_file():
            if strict:
                die(f"required generic source scene missing: {level_path}")
            warn(f"disabled-component ground-truth check: {level_path} not found -- skipped {scene_name}")
            continue
        env = UnityPy.load(str(level_path))
        scene_counts: dict[str, int] = {}
        for obj in env.objects:
            t = obj.type.name
            if t not in ENABLED_COMPONENT_TYPES:
                continue
            try:
                tt = obj.read_typetree()
            except Exception as exc:  # noqa: BLE001 -- defensive; not expected against a real level file
                if strict:
                    die(f"cannot read disabled-component source {level_path} {obj.path_id}: {exc}")
                continue
            if tt.get("m_Enabled", 1) == 0:
                scene_counts[t] = scene_counts.get(t, 0) + 1
        if strict and package is not None:
            source_file = env.files[str(level_path)]
            for kind, count in terrain_scene_adjustments(scene_name, source_file, manifest, package).items():
                scene_counts[kind] = scene_counts.get(kind, 0) + count
        out[scene_name] = scene_counts
    return out


def validate_disabled_component_counts(manifest: dict, package: Path) -> None:
    """fix-enabled-state acceptance check: every scene's per-type disabled-component count in the
    extracted package must match Arena's own authored state exactly. Catches both "the extractor
    stopped recording m_Enabled" (a silent regression back to the reported root cause -- large
    untextured blocks blocking walkways) and "the extractor's node selection silently dropped a
    disabled object" (a slice/hierarchy-walk bug). A full (--scenes all5, no --slice-roots) package
    is expected to match exactly, with zero difference to explain -- every in-scope component type
    is recorded unconditionally by the corresponding build_* function (§C.6), regardless of its
    enabled state, so nothing this check compares is ever intentionally skipped for a full run. A
    sliced package (--slice-roots) legitimately differs whenever a disabled object falls outside
    the requested subtree(s); reported as a WARN with the slice noted, not a FAIL, since that is
    expected slicing behavior, not a defect."""
    package_counts = count_disabled_in_package(manifest, package)
    source_root = manifest.get("source", {}).get("arena_data_root")
    arena_counts = count_disabled_in_arena(source_root, manifest, package)
    used_fallback = arena_counts is None
    if arena_counts is None:
        arena_counts = KNOWN_ARENA_DISABLED_COUNTS
    is_sliced = bool(manifest.get("slice"))

    total_bad = 0
    for scene_name in manifest.get("scenes", []):
        expected = arena_counts.get(scene_name, {})
        actual = package_counts.get(scene_name, {})
        types = sorted(set(expected) | set(actual))
        scene_bad = 0
        for t in types:
            exp_n = expected.get(t, 0)
            act_n = actual.get(t, 0)
            if exp_n == act_n:
                continue
            if is_sliced and act_n <= exp_n:
                warn(f"scenes/{scene_name}.json: disabled {t} count ({act_n}) is less than Arena's "
                     f"full-scene count ({exp_n}) -- expected for a --slice-roots package (some "
                     f"disabled objects fall outside the requested subtree(s)), not a defect")
                continue
            scene_bad += 1
            fallback_note = " [Arena side: hardcoded reference table, source unreachable]" if used_fallback else ""
            check(False, f"scenes/{scene_name}.json: disabled {t} count ({act_n}) == Arena's own "
                          f"authored count ({exp_n}){fallback_note}")
        if scene_bad == 0:
            print(f"[validate_extract]  ok : scenes/{scene_name}.json: disabled-component counts "
                  f"match Arena exactly ({dict(sorted(expected.items())) or '{}'})")
        total_bad += scene_bad

    if total_bad == 0:
        source_desc = ("recomputed fresh from Arena's own level<N> files" if not used_fallback else
                        "compared against the hardcoded fix-enabled-state.md reference table "
                        "(Arena source unreachable this run)")
        print(f"[validate_extract]  ok : every scene's disabled-component counts match Arena "
              f"exactly ({source_desc})")


# ---------------------------------------------------------------------------
# 8: reproducibility -- literally re-run the extractor
# ---------------------------------------------------------------------------

def derive_extractor_args(manifest: dict) -> tuple[Path, list[int], dict[int, list[int]], str, Path | None] | None:
    """Recovers (extractor_script, scene_indices, slice_map, source_root) from the package's own
    manifest.json, using that map's own extractor module's SCENE_REGISTRY as the single source of
    truth for name<->index (imported read-only, never executed as __main__). Which module is
    "that map's own" is resolved via extractor_script_for() (critic amendment #3 -- generalized
    so this works for Bay5's own extract_bay5.py package too, not just Equator's)."""
    extractor_script = extractor_script_for(manifest)
    if is_generic_package(manifest):
        plan_path, plan = generic_plan_for(manifest)
        if not extractor_script.is_file():
            die(f"generic extractor entrypoint missing: {extractor_script}")
        return (extractor_script, [r["index"] for r in plan["scenes"]], {},
                manifest["source"]["arena_data_root"], plan_path)
    if not extractor_script.is_file():
        warn(f"reproducibility check SKIPPED: extractor script not found at {extractor_script}")
        return None
    sys.path.insert(0, str(extractor_script.parent))
    try:
        _ex = importlib.import_module(extractor_script.stem)  # type: ignore  # noqa: PLC0415
    except Exception as e:  # noqa: BLE001
        warn(f"reproducibility check SKIPPED: could not import {extractor_script.name} to recover "
             f"its SCENE_REGISTRY ({e})")
        return None

    name_to_idx = {v: k for k, v in _ex.SCENE_REGISTRY.items()}
    scene_names = manifest.get("scenes", [])
    try:
        scene_indices = sorted(name_to_idx[n] for n in scene_names)
    except KeyError as e:
        warn(f"reproducibility check SKIPPED: manifest scene name {e} is not in "
             f"{extractor_script.name}'s SCENE_REGISTRY -- can't reconstruct the original invocation")
        return None

    slice_raw = manifest.get("slice", {})
    slice_map = {int(k): list(v) for k, v in slice_raw.items()}

    source_root = manifest.get("source", {}).get("arena_data_root")
    if not source_root:
        warn("reproducibility check SKIPPED: manifest.source.arena_data_root missing")
        return None
    return extractor_script, scene_indices, slice_map, source_root, None


def run_extractor(extractor_script: Path, scene_indices: list[int], slice_map: dict[int, list[int]],
                   source_root: str, out_dir: Path, fixed_timestamp: str, timeout_s: int,
                   plan_path: Path | None = None, target_root: str | None = None,
                   native_features: bool = False) -> subprocess.CompletedProcess:
    cmd = [sys.executable, str(extractor_script),
           "--source", source_root,
           "--output", str(out_dir),
           "--fixed-timestamp", fixed_timestamp]
    # extract_equator.py exposes --scenes/--slice-roots (a configurable subset/slice of its
    # 5-scene set); extract_bay5.py has a fixed, unparameterized 8-scene set (no slicing support
    # at all, see its own module docstring) -- these flags simply don't exist on that script, so
    # they're only added for the script that accepts them.
    if extractor_script.name == "extract_map.py":
        if plan_path is None:
            die("generic reproducibility requires a verified source plan")
        cmd += ["--plan", str(plan_path)]
    elif extractor_script.name == "extract_bay5.py":
        if target_root is not None:
            cmd += ["--target", target_root]
    else:
        cmd += ["--scenes", ",".join(str(i) for i in scene_indices)]
        for idx, ids in slice_map.items():
            cmd += ["--slice-roots", f"{idx}:" + ",".join(str(i) for i in ids)]
        if native_features:
            cmd += ["--native-features"]
            if target_root is not None:
                cmd += ["--target", target_root]
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout_s)


def diff_trees(a: Path, b: Path, ignore_manifest_timestamp: bool) -> list[str]:
    """Returns a list of human-readable diffs; empty means identical (modulo the manifest.json
    extracted_utc field when ignore_manifest_timestamp is set)."""
    problems: list[str] = []

    def _walk(dcmp: filecmp.dircmp, rel: str) -> None:
        if dcmp.left_only:
            problems.append(f"only in {a}/{rel}: {sorted(dcmp.left_only)}")
        if dcmp.right_only:
            problems.append(f"only in {b}/{rel}: {sorted(dcmp.right_only)}")
        for name in dcmp.diff_files:
            relpath = f"{rel}/{name}" if rel else name
            if ignore_manifest_timestamp and name == "manifest.json":
                ja = json.loads((a / relpath).read_text(encoding="utf-8"))
                jb = json.loads((b / relpath).read_text(encoding="utf-8"))
                ja.setdefault("source", {})["extracted_utc"] = "NORMALIZED"
                jb.setdefault("source", {})["extracted_utc"] = "NORMALIZED"
                if ja != jb:
                    problems.append(f"{relpath} differs (beyond just source.extracted_utc)")
                continue
            problems.append(f"{relpath} differs")
        for sub, sub_dcmp in dcmp.subdirs.items():
            _walk(sub_dcmp, f"{rel}/{sub}" if rel else sub)

    _walk(filecmp.dircmp(a, b), "")
    return problems


def run_reproducibility_checks(manifest: dict, package: Path, timeout_s: int) -> None:
    derived = derive_extractor_args(manifest)
    if derived is None:
        return
    extractor_script, scene_indices, slice_map, source_root, plan_path = derived

    if not Path(source_root).is_dir():
        if is_generic_package(manifest):
            die("generic package source disappeared before reproducibility; cannot skip")
        warn(f"reproducibility check SKIPPED: source root {source_root!r} is not reachable from this "
             f"machine right now (environment gap, not a WP1 defect) -- re-run with this path mounted "
             f"to exercise SS B.WP1's determinism acceptance criterion")
        return

    fixed_ts = "2026-01-01T00:00:00Z"
    tmp = Path(tempfile.mkdtemp(prefix="wp6_validate_extract_repro_"))
    try:
        out1, out2 = tmp / "run1", tmp / "run2"
        print(f"[validate_extract] reproducibility: re-running {extractor_script.name} twice into {tmp} "
              f"(scenes={scene_indices}, slice={slice_map or 'none (full scenes)'}) ...")
        target_root = manifest.get("source", {}).get("target_data_root")
        native_features = "native_features" in manifest
        p1 = run_extractor(extractor_script, scene_indices, slice_map, source_root, out1, fixed_ts, timeout_s, plan_path, target_root, native_features)
        if not check(p1.returncode == 0,
                      f"reproducibility run #1 of {extractor_script.name} exits 0 "
                      f"(rc={p1.returncode}; stderr tail: {p1.stderr[-800:]})"):
            return
        p2 = run_extractor(extractor_script, scene_indices, slice_map, source_root, out2, fixed_ts, timeout_s, plan_path, target_root, native_features)
        if not check(p2.returncode == 0,
                      f"reproducibility run #2 of {extractor_script.name} exits 0 "
                      f"(rc={p2.returncode}; stderr tail: {p2.stderr[-800:]})"):
            return

        diffs_1v2 = diff_trees(out1, out2, ignore_manifest_timestamp=False)
        check(not diffs_1v2,
              f"two independent fresh extractor runs (same source, same --fixed-timestamp) produce a "
              f"byte-identical package (SS B.WP1's determinism acceptance criterion) -- "
              f"{len(diffs_1v2)} diff(s): {diffs_1v2[:15]}")

        diffs_pkg = diff_trees(out2, package, ignore_manifest_timestamp=True)
        check(not diffs_pkg,
              f"the checked-in --package ({package}) matches what the current extractor actually "
              f"produces from today's source data (package-freshness check) -- "
              f"{len(diffs_pkg)} diff(s): {diffs_pkg[:15]}")
    except subprocess.TimeoutExpired:
        check(False, f"reproducibility runs complete within {timeout_s}s each (timed out)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ---------------------------------------------------------------------------

def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--package", type=Path, default=DEFAULT_PACKAGE,
                     help="extracted package directory to validate (default: O7's S1 path)")
    ap.add_argument("--skip-reproducibility", action="store_true",
                     help="skip re-invoking the extractor twice (structural/integrity checks only)")
    ap.add_argument("--extractor-timeout", type=int, default=300,
                     help="seconds to allow each reproducibility re-run (default 300)")
    args = ap.parse_args()

    package: Path = args.package
    if not package.is_dir():
        die(f"--package {package} is not a directory")

    manifest_path = package / "manifest.json"
    manifest = load_json(manifest_path)
    print(f"[validate_extract] validating {package} (schema_version={manifest.get('schema_version')})")

    extractor_script_for(manifest)  # Reject executable-path injection even with reproducibility disabled.
    if is_generic_package(manifest):
        generic_plan_for(manifest)  # Source, plan digest, registry and finite complete anchors are mandatory.

    validate_manifest_shape(manifest, package)
    from validate_native_features import validate_package as validate_native_package
    try:
        validate_native_package(package, manifest)
        check(True, "optional native feature source/clip/reference contract validates")
    except (ValueError, OSError, KeyError) as error:
        check(False, f"native feature package validation: {error}")
    stats = validate_scenes(manifest, package)
    validate_material_slot_preservation(manifest, package)
    collect_shader_and_texture_stats(manifest, package, stats)
    validate_materials(manifest, package)
    validate_meshes(manifest, package)
    validate_mesh_compatibility(manifest, package)
    validate_textures(manifest, package)
    check_orphans(manifest, package)
    validate_disabled_component_counts(manifest, package)

    if args.skip_reproducibility:
        print("[validate_extract] reproducibility check SKIPPED (--skip-reproducibility)")
    else:
        run_reproducibility_checks(manifest, package, args.extractor_timeout)

    print()
    print("[validate_extract] --- summary ---")
    print(f"  scenes: {len(manifest.get('scenes', []))}, objects: {stats['objects']}")
    print(f"  meshes: {len(manifest.get('meshes', {}))}, materials: {len(manifest.get('materials', {}))}, "
          f"textures: {len(manifest.get('textures', {}))}")
    print(f"  MeshRenderer: {stats['mesh_renderers']}, SkinnedMeshRenderer: {stats['skinned_mesh_renderers']}, "
          f"MeshCollider: {stats['mesh_colliders']} (convex {stats['convex_colliders']} / "
          f"non-convex {stats['nonconvex_colliders']}), BoxCollider: {stats['box_colliders']}, "
          f"SphereCollider: {stats['sphere_colliders']}, CapsuleCollider: {stats['capsule_colliders']}, "
          f"Light: {stats['lights']}, LODGroup: {stats['lod_groups']}, "
          f"ReflectionProbe: {stats['reflection_probes']}")
    print(f"  distinct shaders: {len(stats['distinct_shaders'])}, normal-map textures: "
          f"{stats['normal_map_textures']}")

    print()
    if FAILURES:
        print(f"[validate_extract] {len(FAILURES)} check(s) FAILED, {len(WARNINGS)} warning(s):")
        for f in FAILURES:
            print(f"  FAIL: {f}")
        for w in WARNINGS:
            print(f"  WARN: {w}")
        sys.exit(1)
    if WARNINGS:
        print(f"[validate_extract] OK -- all checks passed for {package} ({len(WARNINGS)} non-blocking warning(s)):")
        for w in WARNINGS:
            print(f"  WARN: {w}")
    else:
        print(f"[validate_extract] OK -- all checks passed for {package}, no warnings")


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:  # noqa: BLE001
        print("[validate_extract] FATAL: unexpected exception", file=sys.stderr)
        traceback.print_exc()
        sys.exit(1)
