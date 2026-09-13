#!/usr/bin/env python3
"""WP6 validator for WP2's built AssetBundle(s) (docs/SPEC.md SS B.WP2.8 / SS D / SS G.S1 / SS
B.WP6 table row 2 + LEAD OVERRIDES O1). Re-reads the built bundle(s) with UnityPy -- independent
of WP2's own C# importer code, this script never imports or modifies anything under
UnityProject/Assets/ArenaMapPort/Editor/** -- and asserts:

  1. `arena_equator_scenes.bundle` exists and its serialized-file header reports target platform
     `StandaloneWindows64` (SS B.WP2.8 / risk register #12).
  2. GameObject / Mesh / Texture2D / MeshFilter / MeshRenderer / MeshCollider / BoxCollider /
     SphereCollider / CapsuleCollider / Light / LODGroup counts in the bundle are consistent with
     the extracted package's own manifest.json + scenes/*.json (catches "importer silently
     skipped everything"; Material/Shader are allowed to exceed the package count, since a scene
     load can pull in one built-in default Material/Shader each -- WP2's own prior run observed
     exactly this and it is not a defect).
  3. Every Mesh object's name matches a package mesh_id, and its vertex_count/submesh_count match
     the manifest entry exactly.
  4. Every Material object's name matches a package mat_id, and its shader name is the O1-prefixed
     stand-in ("Hidden/ArenaMapPort/" + <original>) whose original name matches
     manifest.materials[mat_id].shader exactly -- never a bare/unprefixed name (O1's whole point:
     an unprefixed name would let the SDK Editor's own Shader.Find silently resolve to a *real*
     compiled shader instead of the intended stand-in). Cross-checked against
     work/build/s1/material-shaders.json's sidecar.
  5. MeshCollider convex/non-convex counts and is_trigger distribution (aggregate, not object-by-
     object -- Unity's own build reassigns path_ids, so there is no persistent per-object id to
     match 1:1 against the package; SS G.S1's own pass condition is itself phrased at the
     aggregate "every collider's convex/isTrigger flags match" level) match the package.
  6. No `LODGroup` in the bundle has a null renderer slot (SS B.WP2.5's two-pass ordering
     requirement; risk: a single-pass implementation silently nulls a late-attached sibling).
  7. If an `arena_equator_navmesh.bundle` is present: exactly one `NavMeshData` asset, and (best-
     effort, see caveat below) a tile-count sanity check; if absent, this is only a hard failure
     when --require-navmesh is passed (SS G.S1 explicitly allows skipping the NavMesh bake for
     the S1 slice).
  8. The Scripts-scene EFT MonoBehaviours (SS D: LevelSettings, LocationScene, BotZone,
     LocationExportInfo, EFT.Game.Spawning.SpawnPointMarker) are present with an `m_Script` PPtr
     that resolves, within the bundle, to a `MonoScript` whose (m_ClassName, m_Namespace) match SS
     D's real decompiled types, with a serialized byte size clearly larger than an empty
     MonoBehaviour header (SS G.S1's explicit, previously-untested "does a GameRefs-sourced
     Assembly-CSharp MonoBehaviour survive serialization into a StandaloneWindows64 bundle at
     all" check) -- and, where a typetree is available, the specific risk-register fields
     (BotZone.PatrolWays / BotZone.SpawnPointMarkers, risk #11) are read back and asserted
     non-null. --require-scripts-scene (default on) fails the run if none of the 5 are found.
  9. Cross-checks `editor_verify.json` against the package when present (both are supposed to
     describe the same built scenes) -- absence is reported as a named, non-crashing failure
     rather than silently skipped, since SS B.WP6's task instruction explicitly asks for this
     cross-check.

Known, honest caveat: `m_AssemblyName` on a MonoScript emitted by *this project's own* Unity
Editor build (via a GameRefs-sourced precompiled Assembly-CSharp.dll) has never been observed on
real output -- WP3's validate_preset.py confirmed "Assembly-CSharp" (no ".dll") empirically for a
donor SPT-built bundle, a *different* build pipeline. This script accepts either spelling and
logs which one it actually saw.

Exit code 0 + "OK" summary on success. Exit code 1 with specific, actionable messages (never a
bare stack trace) on any failure -- including "the bundle does not exist", which is reported as a
named FAIL with the exact expected path, not a stack trace or a silent skip.

Usage:
    python tools/validate/validate_bundle_roundtrip.py
    python tools/validate/validate_bundle_roundtrip.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle
    python tools/validate/validate_bundle_roundtrip.py --require-navmesh
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import traceback
from pathlib import Path
from typing import Any, NoReturn

REPO_ROOT = Path("D:/ArenaMapPort")
DEFAULT_OUTPUT_DIR = REPO_ROOT / "work" / "build" / "s1"           # O7
DEFAULT_PACKAGE = REPO_ROOT / "work" / "extract" / "equator_s1"    # O7
DEFAULT_SCENES_BUNDLE = DEFAULT_OUTPUT_DIR / "arena_equator_scenes.bundle"
DEFAULT_NAVMESH_BUNDLE = DEFAULT_OUTPUT_DIR / "arena_equator_navmesh.bundle"
DEFAULT_MATERIAL_SHADERS = DEFAULT_OUTPUT_DIR / "material-shaders.json"
DEFAULT_EDITOR_VERIFY = DEFAULT_OUTPUT_DIR / "editor_verify.json"

SHADER_PREFIX = "Hidden/ArenaMapPort/"  # O1
ID_RE = re.compile(r"^[0-9a-f]{16}$")
STANDALONE_WINDOWS_64 = 19  # UnityPy/Unity BuildTarget enum value for StandaloneWindows64

# SS D -- the five required EFT scene components, class name -> real decompiled namespace
# (D:/SPT41/Development/_decompiled, re-confirmed this pass by direct grep of each source file's
# `namespace`/`public class` lines): LevelSettings/LocationScene/BotZone are in the GLOBAL
# namespace (no `namespace` statement in their .cs files) -- "" here, not None, matching how
# MonoScript.m_Namespace serializes an empty/global namespace.
EXPECTED_EFT_COMPONENTS = {
    "LevelSettings": "",
    "LocationScene": "",
    "BotZone": "",
    "LocationExportInfo": "EFT",
    "SpawnPointMarker": "EFT.Game.Spawning",
}
ACCEPTED_ASSEMBLY_NAMES = {"Assembly-CSharp", "Assembly-CSharp.dll"}
MONOBEHAVIOUR_HEADER_ONLY_BYTES = 40  # generous upper bound for enabled+m_Script+m_Name w/ no custom fields

FAILURES: list[str] = []
WARNINGS: list[str] = []


def check(cond: bool, msg: str) -> bool:
    if not cond:
        FAILURES.append(msg)
        print(f"[validate_bundle_roundtrip] FAIL: {msg}", file=sys.stderr)
    else:
        print(f"[validate_bundle_roundtrip]  ok : {msg}")
    return cond


def warn(msg: str) -> None:
    WARNINGS.append(msg)
    print(f"[validate_bundle_roundtrip] WARN: {msg}", file=sys.stderr)


def die(msg: str) -> NoReturn:
    print(f"[validate_bundle_roundtrip] FATAL: {msg}", file=sys.stderr)
    sys.exit(1)


def load_json(path: Path) -> Any:
    if not path.is_file():
        die(f"expected file not found: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        die(f"{path} is not valid JSON: {e}")


# ---------------------------------------------------------------------------
# Package-derived expected values (self-contained -- does not require
# validate_extract.py to have run first; each WP6 script must stand alone).
# ---------------------------------------------------------------------------

def load_package_expected(package: Path) -> dict:
    manifest = load_json(package / "manifest.json")
    expected = {
        "manifest": manifest,
        "mesh_count": len(manifest.get("meshes", {})),
        "material_count": len(manifest.get("materials", {})),
        "texture_count": len(manifest.get("textures", {})),
        "mesh_filters": 0, "mesh_renderers": 0, "skinned_mesh_renderers": 0,
        "mesh_colliders": 0, "convex_colliders": 0, "nonconvex_colliders": 0,
        "box_colliders": 0, "sphere_colliders": 0, "capsule_colliders": 0,
        "lights": 0, "lod_groups": 0, "game_objects": 0,
    }
    for scene_name in manifest.get("scenes", []):
        scene_path = package / "scenes" / f"{scene_name}.json"
        if not scene_path.is_file():
            warn(f"package scene file missing: {scene_path} (manifest.scenes lists {scene_name!r})")
            continue
        scene = load_json(scene_path)
        objects = scene.get("objects", [])
        expected["game_objects"] += len(objects)
        for obj in objects:
            for comp in obj.get("components", []):
                t = comp.get("type")
                if t == "MeshFilter":
                    expected["mesh_filters"] += 1
                elif t == "MeshRenderer":
                    expected["mesh_renderers"] += 1
                elif t == "SkinnedMeshRenderer":
                    expected["skinned_mesh_renderers"] += 1
                elif t == "MeshCollider":
                    expected["mesh_colliders"] += 1
                    if comp.get("convex"):
                        expected["convex_colliders"] += 1
                    else:
                        expected["nonconvex_colliders"] += 1
                elif t == "BoxCollider":
                    expected["box_colliders"] += 1
                elif t == "SphereCollider":
                    expected["sphere_colliders"] += 1
                elif t == "CapsuleCollider":
                    expected["capsule_colliders"] += 1
                elif t == "Light":
                    expected["lights"] += 1
                elif t == "LODGroup":
                    expected["lod_groups"] += 1
    return expected


# ---------------------------------------------------------------------------
# UnityPy bundle loading helpers
# ---------------------------------------------------------------------------

def load_bundle(path: Path, label: str):
    """Returns a UnityPy Environment, or None (with a FAIL already recorded) if the bundle is
    missing/unreadable -- callers must handle None rather than crashing."""
    if not path.is_file():
        check(False, f"{label} exists at {path}")
        return None
    try:
        import UnityPy
        env = UnityPy.load(str(path))
    except Exception as e:  # noqa: BLE001
        check(False, f"{label} loads with UnityPy (FAILED: {e})")
        return None
    check(True, f"{label} exists and loads with UnityPy ({path})")
    return env


def iter_serialized_files(env):
    """Recursively walks a UnityPy Environment's file tree, yielding every nested
    SerializedFile (a .bundle's own top-level `files` are typically BundleFile containers; the
    actual serialized-object data with a target_platform/unity_version header lives one level
    deeper)."""
    from UnityPy.files.SerializedFile import SerializedFile

    def _walk(f):
        if isinstance(f, SerializedFile):
            yield f
        nested = getattr(f, "files", None)
        if nested:
            for f2 in nested.values():
                yield from _walk(f2)

    for f in env.files.values():
        yield from _walk(f)


def validate_target_platform(env, bundle_label: str) -> None:
    sfiles = list(iter_serialized_files(env))
    if not check(len(sfiles) > 0, f"{bundle_label}: at least one SerializedFile found inside the bundle"):
        return
    bad = [(sf.name, sf.target_platform, sf.unity_version) for sf in sfiles
           if int(sf.target_platform) != STANDALONE_WINDOWS_64]
    check(not bad,
          f"{bundle_label}: every SerializedFile's target platform == StandaloneWindows64 "
          f"({STANDALONE_WINDOWS_64}) -- SS B.WP2.8 / risk #12 (mismatches: {bad})")
    for sf in sfiles:
        print(f"[validate_bundle_roundtrip]   {bundle_label}: {sf.name}: "
              f"target_platform={int(sf.target_platform)}, unity_version={sf.unity_version}")


def shader_display_name(shader_obj) -> str | None:
    """Same proven resolution path analysis/scripts/census.py's material_stats() uses:
    m_ParsedForm.m_Name for a real compiled shader, falling back to top-level m_Name (needed for
    an editor-authored stand-in .shader asset, whose typetree shape may differ)."""
    if shader_obj is None:
        return None
    try:
        tt = shader_obj.read_typetree()
    except Exception:
        return None
    pf = tt.get("m_ParsedForm", {}) or {}
    return pf.get("m_Name") or tt.get("m_Name") or None


# ---------------------------------------------------------------------------
# Object-count / mesh / material / collider / LODGroup checks
# ---------------------------------------------------------------------------

def validate_scenes_bundle_contents(env, expected: dict, material_shaders: dict | None) -> None:
    objs_by_type: dict[str, list] = {}
    for obj in env.objects:
        objs_by_type.setdefault(obj.type.name, []).append(obj)

    def n(type_name: str) -> int:
        return len(objs_by_type.get(type_name, []))

    # --- aggregate counts vs package -------------------------------------------------
    exact_checks = [
        ("GameObject", n("GameObject"), expected["game_objects"]),
        ("Mesh", n("Mesh"), expected["mesh_count"]),
        ("Texture2D", n("Texture2D"), expected["texture_count"]),
        ("MeshFilter", n("MeshFilter"), expected["mesh_filters"]),
        ("MeshRenderer", n("MeshRenderer"), expected["mesh_renderers"]),
        ("MeshCollider", n("MeshCollider"), expected["mesh_colliders"]),
        ("BoxCollider", n("BoxCollider"), expected["box_colliders"]),
        ("SphereCollider", n("SphereCollider"), expected["sphere_colliders"]),
        ("CapsuleCollider", n("CapsuleCollider"), expected["capsule_colliders"]),
        ("Light", n("Light"), expected["lights"]),
        ("LODGroup", n("LODGroup"), expected["lod_groups"]),
    ]
    for label, actual, exp in exact_checks:
        check(actual == exp, f"{label} count in bundle ({actual}) == package ({exp})")

    # Material/Shader: extra built-in defaults (Skybox/RenderSettings dependencies) are a known,
    # benign artifact (WP2's own prior partial-pipeline run observed exactly +1/+1) -- under is a
    # real defect, over is only a warning.
    for label, exp in (("Material", expected["material_count"]),):
        actual = n(label)
        if actual < exp:
            check(False, f"{label} count in bundle ({actual}) >= package ({exp})")
        elif actual > exp:
            warn(f"{label} count in bundle ({actual}) exceeds package ({exp}) -- expected if Unity "
                 f"pulled in built-in Skybox/RenderSettings defaults per scene; verify the extra "
                 f"{actual - exp} aren't package materials silently duplicated")
        else:
            check(True, f"Material count in bundle ({actual}) == package ({exp})")

    # --- convex/non-convex MeshCollider aggregate -------------------------------------
    convex_count = 0
    nonconvex_count = 0
    for obj in objs_by_type.get("MeshCollider", []):
        try:
            tt = obj.read_typetree()
        except Exception as e:  # noqa: BLE001
            check(False, f"MeshCollider path_id={obj.path_id}: read_typetree() succeeds (FAILED: {e})")
            continue
        if tt.get("m_Convex"):
            convex_count += 1
        else:
            nonconvex_count += 1
    check(convex_count == expected["convex_colliders"],
          f"convex MeshCollider count in bundle ({convex_count}) == package ({expected['convex_colliders']})")
    check(nonconvex_count == expected["nonconvex_colliders"],
          f"non-convex MeshCollider count in bundle ({nonconvex_count}) == package "
          f"({expected['nonconvex_colliders']})")

    # --- Mesh detail: name -> vertex_count/submesh_count -------------------------------
    package_meshes = expected["manifest"].get("meshes", {})
    matched = 0
    mismatched = 0
    unmatched_names = []
    for obj in objs_by_type.get("Mesh", []):
        try:
            tt = obj.read_typetree()
        except Exception as e:  # noqa: BLE001
            check(False, f"Mesh path_id={obj.path_id}: read_typetree() succeeds (FAILED: {e})")
            continue
        name = tt.get("m_Name", "")
        if not ID_RE.match(name):
            unmatched_names.append(name)
            continue  # a built-in/default mesh Unity pulled in, not one of ours
        meta = package_meshes.get(name)
        if meta is None:
            mismatched += 1
            check(False, f"Mesh {name!r} in bundle has an id-shaped name but is not in "
                          f"manifest.meshes -- MeshImporter.cs names assets by mesh_id (SS B.WP2.2), "
                          f"this looks like a stray/leftover asset")
            continue
        vcount = tt.get("m_VertexData", {}).get("m_VertexCount")
        scount = len(tt.get("m_SubMeshes", []))
        local_bad = False
        if vcount != meta["vertex_count"]:
            local_bad = True
            check(False, f"Mesh {name!r}: bundle vertex_count ({vcount}) == package ({meta['vertex_count']})")
        if scount != meta["submesh_count"]:
            local_bad = True
            check(False, f"Mesh {name!r}: bundle submesh_count ({scount}) == package ({meta['submesh_count']})")
        if local_bad:
            mismatched += 1
        else:
            matched += 1
    check(mismatched == 0, f"all {matched + mismatched} id-named Mesh objects in the bundle match the "
                            f"package's vertex_count/submesh_count ({mismatched} mismatch(es))")
    if unmatched_names:
        print(f"[validate_bundle_roundtrip]   ({len(unmatched_names)} Mesh object(s) with non-id names, "
              f"presumably Unity built-ins, ignored for this check: {unmatched_names[:10]})")

    # --- Material detail: name -> O1-prefixed stand-in shader --------------------------
    package_materials = expected["manifest"].get("materials", {})
    mat_matched = 0
    mat_mismatched = 0
    for obj in objs_by_type.get("Material", []):
        try:
            tt = obj.read_typetree()
            typed = obj.read()
        except Exception as e:  # noqa: BLE001
            check(False, f"Material path_id={obj.path_id}: read()/read_typetree() succeed (FAILED: {e})")
            continue
        name = tt.get("m_Name", "")
        if not ID_RE.match(name):
            continue  # built-in default material, not one of ours
        meta = package_materials.get(name)
        if meta is None:
            mat_mismatched += 1
            check(False, f"Material {name!r} in bundle has an id-shaped name but is not in "
                          f"manifest.materials")
            continue
        shader_name = shader_display_name(getattr(typed, "m_Shader", None))
        expected_original = meta["shader"]
        expected_standin = SHADER_PREFIX + expected_original
        local_bad = False
        if shader_name is None:
            local_bad = True
            check(False, f"Material {name!r}: m_Shader PPtr resolves to a readable Shader object "
                          f"(got None -- dangling/unresolved reference)")
        elif not shader_name.startswith(SHADER_PREFIX):
            local_bad = True
            check(False, f"Material {name!r}: shader name {shader_name!r} starts with O1's "
                          f"{SHADER_PREFIX!r} prefix (a bare/unprefixed name means the SDK Editor's own "
                          f"Shader.Find could have resolved to a REAL compiled shader instead of the "
                          f"intended stand-in -- O1's whole reason for the prefix)")
        elif shader_name != expected_standin:
            local_bad = True
            check(False, f"Material {name!r}: shader name {shader_name!r} == {expected_standin!r} "
                          f"(package's declared original shader for this material is {expected_original!r})")
        if material_shaders is not None:
            sidecar_entry = material_shaders.get(name)
            if sidecar_entry is None:
                warn(f"Material {name!r}: no entry in material-shaders.json sidecar (O1 requires one "
                     f"per material)")
            elif sidecar_entry.get("shader") != expected_original:
                local_bad = True
                check(False, f"Material {name!r}: material-shaders.json sidecar shader "
                              f"({sidecar_entry.get('shader')!r}) == package original ({expected_original!r})")
        if local_bad:
            mat_mismatched += 1
        else:
            mat_matched += 1
    check(mat_mismatched == 0, f"all {mat_matched + mat_mismatched} id-named Material objects in the "
                                f"bundle carry an O1-compliant stand-in shader name matching the package "
                                f"({mat_mismatched} mismatch(es))")

    # --- LODGroup null-renderer check ---------------------------------------------------
    null_slots = 0
    for obj in objs_by_type.get("LODGroup", []):
        try:
            tt = obj.read_typetree()
        except Exception as e:  # noqa: BLE001
            check(False, f"LODGroup path_id={obj.path_id}: read_typetree() succeeds (FAILED: {e})")
            continue
        for lod in tt.get("m_LODs", []):
            for renderer_entry in lod.get("m_Renderers", []):
                ptr = renderer_entry.get("renderer", {}) if isinstance(renderer_entry, dict) else {}
                if ptr.get("m_FileID", 0) == 0 and ptr.get("m_PathID", 0) == 0:
                    null_slots += 1
    check(null_slots == 0, f"no LODGroup.GetLODs() renderer slot is null in the bundle (SS B.WP2.5's "
                            f"two-pass ordering requirement; found {null_slots} null slot(s))")


# ---------------------------------------------------------------------------
# NavMesh bundle (optional for S1)
# ---------------------------------------------------------------------------

def validate_navmesh_bundle(path: Path, require: bool) -> None:
    if not path.is_file():
        if require:
            check(False, f"arena_equator_navmesh.bundle exists at {path} (--require-navmesh was passed)")
        else:
            warn(f"arena_equator_navmesh.bundle not found at {path} -- SS G.S1 allows skipping the "
                 f"NavMesh bake for the S1 slice, treating as informational, not a failure")
        return
    env = load_bundle(path, "arena_equator_navmesh.bundle")
    if env is None:
        return
    validate_target_platform(env, "arena_equator_navmesh.bundle")
    navmesh_objs = [o for o in env.objects if o.type.name == "NavMeshData"]
    check(len(navmesh_objs) == 1,
          f"arena_equator_navmesh.bundle contains exactly 1 NavMeshData asset (found {len(navmesh_objs)})")
    if len(navmesh_objs) == 1:
        try:
            tt = navmesh_objs[0].read_typetree()
        except Exception as e:  # noqa: BLE001
            check(False, f"NavMeshData: read_typetree() succeeds (FAILED: {e})")
            return
        # Best-effort: Unity's NavMeshData typetree does not expose a documented public
        # "tile_count" field name (the tile data is an opaque binary blob, `m_NavMeshTiles` or
        # similar depending on version); fall back to raw byte size as a non-empty-data proxy
        # and say so plainly rather than assert a field name never confirmed against real output.
        raw_len = navmesh_objs[0].byte_size
        check(raw_len > 64, f"NavMeshData raw serialized size ({raw_len} bytes) is well beyond an "
                             f"empty/near-empty asset -- proxy for 'tile_count > 0' since NavMeshData's "
                             f"real typetree field name for tile data was not empirically confirmed "
                             f"this pass (no baked sample existed to inspect)")


# ---------------------------------------------------------------------------
# Scripts-scene EFT MonoBehaviours (SS D / SS G.S1's explicit GameRefs round-trip gate)
# ---------------------------------------------------------------------------

def validate_eft_scripts(env, require: bool) -> None:
    mono_objs = [o for o in env.objects if o.type.name == "MonoBehaviour"]
    monoscripts = {o.path_id: o for o in env.objects if o.type.name == "MonoScript"}

    found: dict[str, list[dict]] = {k: [] for k in EXPECTED_EFT_COMPONENTS}
    unresolved = 0
    other_classes: list[str] = []

    for obj in mono_objs:
        try:
            tt = obj.read_typetree()
        except Exception:
            tt = None
        script_ptr = (tt or {}).get("m_Script") or {}
        path_id = script_ptr.get("m_PathID")
        script_obj = monoscripts.get(path_id)
        if script_obj is None:
            unresolved += 1
            continue
        try:
            script_tt = script_obj.read_typetree()
        except Exception:
            unresolved += 1
            continue
        class_name = script_tt.get("m_ClassName")
        namespace = script_tt.get("m_Namespace", "")
        assembly = script_tt.get("m_AssemblyName")
        if class_name in EXPECTED_EFT_COMPONENTS:
            found[class_name].append({
                "path_id": obj.path_id, "namespace": namespace, "assembly": assembly,
                "byte_size": obj.byte_size, "typetree": tt,
            })
        else:
            other_classes.append(f"{namespace + '.' if namespace else ''}{class_name}")

    if unresolved:
        warn(f"{unresolved} MonoBehaviour object(s) in the bundle had an m_Script PPtr that did not "
             f"resolve to a MonoScript in this bundle (could be legitimate Unity built-ins, e.g. a "
             f"default Skybox component -- only flagged if one of the 5 SS D components is missing "
             f"below)")

    any_found = any(found.values())
    if not any_found:
        msg = (f"none of the 5 SS D EFT components (LevelSettings, LocationScene, BotZone, "
               f"LocationExportInfo, SpawnPointMarker) were found as MonoBehaviours in the bundle "
               f"-- SS G.S1's explicit 'does a GameRefs-sourced Assembly-CSharp MonoBehaviour survive "
               f"serialization into a StandaloneWindows64 bundle' gate is UNVERIFIED, not passed")
        if require:
            check(False, msg)
        else:
            warn(msg)
        return

    for class_name, expected_ns in EXPECTED_EFT_COMPONENTS.items():
        entries = found[class_name]
        if not check(len(entries) >= 1, f"at least one {class_name} MonoBehaviour found in the bundle"):
            continue
        for e in entries:
            check(e["namespace"] == expected_ns,
                  f"{class_name} (path_id={e['path_id']}): MonoScript namespace == {expected_ns!r} "
                  f"(got {e['namespace']!r})")
            check(e["assembly"] in ACCEPTED_ASSEMBLY_NAMES,
                  f"{class_name} (path_id={e['path_id']}): MonoScript assembly is "
                  f"{sorted(ACCEPTED_ASSEMBLY_NAMES)} (got {e['assembly']!r})")
            check(e["byte_size"] > MONOBEHAVIOUR_HEADER_ONLY_BYTES,
                  f"{class_name} (path_id={e['path_id']}): serialized byte size ({e['byte_size']}) is "
                  f"clearly larger than an empty MonoBehaviour header (>{MONOBEHAVIOUR_HEADER_ONLY_BYTES} "
                  f"bytes) -- evidence real field data survived the round trip, not just the header")

        if class_name == "BotZone":
            for e in entries:
                tt = e["typetree"] or {}
                patrol = tt.get("PatrolWays")
                spawn_markers = tt.get("SpawnPointMarkers")
                if "PatrolWays" not in tt and "SpawnPointMarkers" not in tt:
                    warn(f"BotZone (path_id={e['path_id']}): typetree has no PatrolWays/SpawnPointMarkers "
                         f"fields to read back (no typetree written into this bundle for this type, or "
                         f"UnityPy could not infer one) -- risk #11's null-vs-empty distinction can only "
                         f"be confirmed via VerifyBuild.cs's live in-Editor read (see editor_verify.json "
                         f"cross-check), not from this offline bundle re-read alone")
                else:
                    check(patrol is not None,
                          f"BotZone (path_id={e['path_id']}): PatrolWays is non-null (risk #11: a null "
                          f"here throws NullReferenceException in BotZone.Init on every raid start)")
                    check(spawn_markers is not None,
                          f"BotZone (path_id={e['path_id']}): SpawnPointMarkers is non-null (risk #11: "
                          f"a null here throws NullReferenceException in "
                          f"SpawnPointsCollection.CalculateCanSpawnPmcBots/BotsController.Init)")

    if other_classes:
        print(f"[validate_bundle_roundtrip]   ({len(other_classes)} other MonoBehaviour class(es) in "
              f"the bundle, not one of the 5 SS D types: {sorted(set(other_classes))[:10]})")


# ---------------------------------------------------------------------------
# editor_verify.json cross-check
# ---------------------------------------------------------------------------

def validate_editor_verify(path: Path, expected: dict) -> None:
    if not path.is_file():
        check(False, f"editor_verify.json exists at {path} (task instruction: 'cross-check "
                      f"work/build/s1/editor_verify.json' -- WP2's VerifyBuild.cs has not produced one)")
        return
    report = load_json(path)
    manifest = expected["manifest"]
    scene_names = set(manifest.get("scenes", [])) | {"Arena_Equator_Scripts"}
    reported = {s.get("scene_name"): s for s in report.get("scenes", [])}

    missing_scenes = scene_names - reported.keys()
    check(not missing_scenes, f"editor_verify.json covers every expected scene "
                               f"(missing: {sorted(missing_scenes)})")

    for scene_name, s in reported.items():
        if scene_name == "Arena_Equator_Scripts":
            eft = s.get("eft_components", {})
            level_settings = eft.get("LevelSettings", {})
            check(level_settings.get("count") == 1,
                  f"editor_verify.json Arena_Equator_Scripts: LevelSettings.count == 1 "
                  f"(got {level_settings.get('count')})")
            loc_scene = eft.get("LocationScene", {})
            check(loc_scene.get("count") == 1,
                  f"editor_verify.json Arena_Equator_Scripts: LocationScene.count == 1 "
                  f"(got {loc_scene.get('count')})")
            check(loc_scene.get("world_interactive_objects_length", -1) != -1,
                  f"editor_verify.json Arena_Equator_Scripts: LocationScene."
                  f"world_interactive_objects_length != -1/null (this is the exact SS D-cited NRE site, "
                  f"LocationScene.Awake -- a -1 here means FillArrays() never ran or ran too late)")
            botzone = eft.get("BotZone", {})
            check(botzone.get("count") == 1,
                  f"editor_verify.json Arena_Equator_Scripts: BotZone.count == 1 (got {botzone.get('count')})")
            check(botzone.get("patrol_ways_length") == 0,
                  f"editor_verify.json Arena_Equator_Scripts: BotZone.patrol_ways_length == 0 "
                  f"(empty, not absent/null -- risk #11; got {botzone.get('patrol_ways_length')})")
            check(botzone.get("spawn_point_markers_count") == 0,
                  f"editor_verify.json Arena_Equator_Scripts: BotZone.spawn_point_markers_count == 0 "
                  f"(empty, not absent/null -- risk #11; got {botzone.get('spawn_point_markers_count')})")
            spawn = eft.get("SpawnPointMarker", {})
            markers = spawn.get("markers", [])
            check(spawn.get("count", 0) >= 1,
                  f"editor_verify.json Arena_Equator_Scripts: at least 1 SpawnPointMarker authored "
                  f"(got {spawn.get('count')})")
            has_player_all = any(
                "player" in (m.get("categories") or "").lower() and m.get("collider_attached")
                and m.get("collider_enabled")
                for m in markers
            )
            check(has_player_all,
                  f"editor_verify.json Arena_Equator_Scripts: at least one SpawnPointMarker has "
                  f"Categories including Player with an attached+enabled collider (markers: {markers})")
            continue

        pkg_scene_path = DEFAULT_PACKAGE_HOLDER["package"] / "scenes" / f"{scene_name}.json"
        if not pkg_scene_path.is_file():
            continue
        pkg_scene = load_json(pkg_scene_path)
        pkg_object_count = len(pkg_scene.get("objects", []))
        check(s.get("game_object_count") == pkg_object_count,
              f"editor_verify.json {scene_name}: game_object_count ({s.get('game_object_count')}) == "
              f"package object count ({pkg_object_count})")
        mf = s.get("mesh_filters", {})
        check(mf.get("null_mesh", -1) == 0,
              f"editor_verify.json {scene_name}: mesh_filters.null_mesh == 0 (got {mf.get('null_mesh')})")
        renderers = s.get("renderers", {})
        check(renderers.get("has_null_material_slot", -1) == 0,
              f"editor_verify.json {scene_name}: renderers.has_null_material_slot == 0 "
              f"(got {renderers.get('has_null_material_slot')})")
        lg = s.get("lod_groups", {})
        check(lg.get("null_renderer_entries", -1) == 0,
              f"editor_verify.json {scene_name}: lod_groups.null_renderer_entries == 0 "
              f"(got {lg.get('null_renderer_entries')})")


DEFAULT_PACKAGE_HOLDER: dict[str, Path] = {}


# ---------------------------------------------------------------------------

def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--scenes-bundle", type=Path, default=DEFAULT_SCENES_BUNDLE)
    ap.add_argument("--navmesh-bundle", type=Path, default=DEFAULT_NAVMESH_BUNDLE)
    ap.add_argument("--package", type=Path, default=DEFAULT_PACKAGE,
                     help="WP1 package to cross-check counts against (default: O7's S1 path)")
    ap.add_argument("--material-shaders", type=Path, default=DEFAULT_MATERIAL_SHADERS)
    ap.add_argument("--editor-verify", type=Path, default=DEFAULT_EDITOR_VERIFY)
    ap.add_argument("--require-navmesh", action="store_true",
                     help="fail if arena_equator_navmesh.bundle is missing (default: optional for S1)")
    ap.add_argument("--require-scripts-scene", dest="require_scripts_scene", action="store_true", default=True,
                     help="fail if none of the 5 SS D EFT MonoBehaviours are found (default: on, SS G.S1)")
    ap.add_argument("--no-require-scripts-scene", dest="require_scripts_scene", action="store_false")
    args = ap.parse_args()

    DEFAULT_PACKAGE_HOLDER["package"] = args.package

    if not args.package.is_dir():
        die(f"--package {args.package} is not a directory")
    expected = load_package_expected(args.package)
    print(f"[validate_bundle_roundtrip] package expectations loaded from {args.package}: "
          f"{expected['game_objects']} objects, {expected['mesh_count']} meshes, "
          f"{expected['material_count']} materials, {expected['texture_count']} textures")

    material_shaders = None
    if args.material_shaders.is_file():
        material_shaders = load_json(args.material_shaders)
        check(True, f"material-shaders.json found and parses ({args.material_shaders})")
    else:
        warn(f"material-shaders.json not found at {args.material_shaders} -- O1 sidecar cross-check "
             f"for materials will be skipped")

    print()
    print(f"[validate_bundle_roundtrip] === {args.scenes_bundle.name} ===")
    env = load_bundle(args.scenes_bundle, "arena_equator_scenes.bundle")
    if env is not None:
        validate_target_platform(env, "arena_equator_scenes.bundle")
        validate_scenes_bundle_contents(env, expected, material_shaders)
        validate_eft_scripts(env, args.require_scripts_scene)
    else:
        check(False, "GameObject/Mesh/Material/collider/LODGroup/EFT-component checks run "
                      "(SKIPPED -- no bundle to read)")

    print()
    print(f"[validate_bundle_roundtrip] === {args.navmesh_bundle.name} ===")
    validate_navmesh_bundle(args.navmesh_bundle, args.require_navmesh)

    print()
    print(f"[validate_bundle_roundtrip] === {args.editor_verify.name} cross-check ===")
    validate_editor_verify(args.editor_verify, expected)

    print()
    if FAILURES:
        print(f"[validate_bundle_roundtrip] {len(FAILURES)} check(s) FAILED, {len(WARNINGS)} warning(s):")
        for f in FAILURES:
            print(f"  FAIL: {f}")
        for w in WARNINGS:
            print(f"  WARN: {w}")
        sys.exit(1)
    if WARNINGS:
        print(f"[validate_bundle_roundtrip] OK -- all checks passed ({len(WARNINGS)} non-blocking warning(s)):")
        for w in WARNINGS:
            print(f"  WARN: {w}")
    else:
        print("[validate_bundle_roundtrip] OK -- all checks passed, no warnings")


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:  # noqa: BLE001
        print("[validate_bundle_roundtrip] FATAL: unexpected exception", file=sys.stderr)
        traceback.print_exc()
        sys.exit(1)
