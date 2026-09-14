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
import filecmp
import json
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path
from typing import Any, NoReturn

REPO_ROOT = Path("D:/ArenaMapPort")
DEFAULT_PACKAGE = REPO_ROOT / "work" / "extract" / "equator_s1"  # O7
EXTRACTOR_SCRIPT = REPO_ROOT / "tools" / "extract" / "extract_equator.py"

ID_RE = re.compile(r"^[0-9a-f]{16}$")

MESH_MAGIC = b"AMPM"
MESH_VERSION = 1
MESH_HEADER_SIZE = 16  # SS C.2: magic(4)+version(4)+vertex_count(4)+channel_mask(2)+index_format(1)+submesh_count(1)
SUBMESH_ENTRY_SIZE = 9  # uint32 index_start + uint32 index_count + uint8 topology, no padding

# SS C.6 -- the exhaustive component type set WP1 is allowed to record for the spike.
KNOWN_COMPONENT_TYPES = {
    "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "MeshCollider",
    "BoxCollider", "SphereCollider", "CapsuleCollider", "Light", "LODGroup",
    "ReflectionProbe",
}
RENDERER_COMPONENT_TYPES = {"MeshRenderer", "SkinnedMeshRenderer"}

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
                        if mat_id not in material_ids:
                            scene_bad_refs += 1
                            check(False, f"scenes/{scene_name}.json: object {oid!r} ({name}) "
                                          f"{ctype}.material_ids contains {mat_id!r}, not in manifest.materials")
                elif ctype == "MeshCollider":
                    stats["mesh_colliders"] += 1
                    stats["convex_colliders" if comp.get("convex") else "nonconvex_colliders"] += 1
                    mid = comp.get("mesh_id")
                    if mid not in mesh_ids:
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
                                scene_bad_refs += 1
                                check(False, f"scenes/{scene_name}.json: object {oid!r} ({name}) "
                                              f"LODGroup lod renderer_object_id {rid!r} points at an "
                                              f"object with no MeshRenderer/SkinnedMeshRenderer component "
                                              f"(SS B.WP2.5's two-pass renderer map would leave this LOD "
                                              f"slot null)")

        if scene_bad_refs == 0:
            print(f"[validate_extract]  ok : scenes/{scene_name}.json: {len(objects)} objects, "
                  f"all component references resolve")

    return stats


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


# ---------------------------------------------------------------------------
# 7: textures/<id>.png
# ---------------------------------------------------------------------------

TEXTURE_DIMENSIONS = {"Tex2D", "Cube", "Tex3D", "Tex2DArray"}


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
# 8: reproducibility -- literally re-run the extractor
# ---------------------------------------------------------------------------

def derive_extractor_args(manifest: dict) -> tuple[list[int], dict[int, list[int]], str] | None:
    """Recovers (scene_indices, slice_map, source_root) from the package's own manifest.json,
    using extract_equator.py's own SCENE_REGISTRY as the single source of truth for name<->index
    (imported read-only, never executed as __main__)."""
    if not EXTRACTOR_SCRIPT.is_file():
        warn(f"reproducibility check SKIPPED: extractor script not found at {EXTRACTOR_SCRIPT}")
        return None
    sys.path.insert(0, str(EXTRACTOR_SCRIPT.parent))
    try:
        import extract_equator as _ex  # type: ignore  # noqa: PLC0415
    except Exception as e:  # noqa: BLE001
        warn(f"reproducibility check SKIPPED: could not import extract_equator.py to recover its "
             f"SCENE_REGISTRY ({e})")
        return None

    name_to_idx = {v: k for k, v in _ex.SCENE_REGISTRY.items()}
    scene_names = manifest.get("scenes", [])
    try:
        scene_indices = sorted(name_to_idx[n] for n in scene_names)
    except KeyError as e:
        warn(f"reproducibility check SKIPPED: manifest scene name {e} is not in extract_equator.py's "
             f"SCENE_REGISTRY -- can't reconstruct the original --scenes argument")
        return None

    slice_raw = manifest.get("slice", {})
    slice_map = {int(k): list(v) for k, v in slice_raw.items()}

    source_root = manifest.get("source", {}).get("arena_data_root")
    if not source_root:
        warn("reproducibility check SKIPPED: manifest.source.arena_data_root missing")
        return None
    return scene_indices, slice_map, source_root


def run_extractor(scene_indices: list[int], slice_map: dict[int, list[int]], source_root: str,
                   out_dir: Path, fixed_timestamp: str, timeout_s: int) -> subprocess.CompletedProcess:
    cmd = [sys.executable, str(EXTRACTOR_SCRIPT),
           "--source", source_root,
           "--scenes", ",".join(str(i) for i in scene_indices),
           "--output", str(out_dir),
           "--fixed-timestamp", fixed_timestamp]
    for idx, ids in slice_map.items():
        cmd += ["--slice-roots", f"{idx}:" + ",".join(str(i) for i in ids)]
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
    scene_indices, slice_map, source_root = derived

    if not Path(source_root).is_dir():
        warn(f"reproducibility check SKIPPED: source root {source_root!r} is not reachable from this "
             f"machine right now (environment gap, not a WP1 defect) -- re-run with this path mounted "
             f"to exercise SS B.WP1's determinism acceptance criterion")
        return

    fixed_ts = "2026-01-01T00:00:00Z"
    tmp = Path(tempfile.mkdtemp(prefix="wp6_validate_extract_repro_"))
    try:
        out1, out2 = tmp / "run1", tmp / "run2"
        print(f"[validate_extract] reproducibility: re-running extractor twice into {tmp} "
              f"(scenes={scene_indices}, slice={slice_map or 'none (full scenes)'}) ...")
        p1 = run_extractor(scene_indices, slice_map, source_root, out1, fixed_ts, timeout_s)
        if not check(p1.returncode == 0,
                      f"reproducibility run #1 of extract_equator.py exits 0 "
                      f"(rc={p1.returncode}; stderr tail: {p1.stderr[-800:]})"):
            return
        p2 = run_extractor(scene_indices, slice_map, source_root, out2, fixed_ts, timeout_s)
        if not check(p2.returncode == 0,
                      f"reproducibility run #2 of extract_equator.py exits 0 "
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

    validate_manifest_shape(manifest, package)
    stats = validate_scenes(manifest, package)
    collect_shader_and_texture_stats(manifest, package, stats)
    validate_materials(manifest, package)
    validate_meshes(manifest, package)
    validate_textures(manifest, package)
    check_orphans(manifest, package)

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
