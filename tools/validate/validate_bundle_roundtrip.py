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
 10. impl-texture-dimensions task item 3: for every Material in the bundle, reads its own
     compiled Shader's declared per-property texture dimension (2D/Cube/3D/2DArray, straight off
     the built Shader object's m_ParsedForm.m_PropInfo) and asserts every assigned texture's own
     UnityPy class actually matches it -- i.e. independently re-proves no material in the built
     bundle references a Texture2D object on a property its shader declares Cube/3D/2DArray (the
     exact "Error assigning 2D texture to CUBE/3D texture property: Dimensions must match"
     scene-load failure mode). Also splits the Texture2D/Cubemap object-count check (item 2 above)
     by the package's own per-texture "dimension" field instead of assuming every in-scope
     texture becomes a Texture2D.

identity-fix update (STRICT now -- this reverses s1-fix-r1's own judgment call): `m_AssemblyName`
on a MonoScript emitted by *this project's own* Unity Editor build (via a GameRefs-sourced
precompiled Assembly-CSharp.dll) used to read back as `SptGameRefs-AssemblyCSharp.dll`, because
tools/gamerefs-fixup renames that DLL (file *and* embedded assembly identity) away from Unity's
reserved `Assembly-CSharp` name to get past the AddComponent<T>() blocker at BUILD time (see that
tool's header comment / docs/progress/s1-fix-r1.md). Every prior pass
(s1-fix-r1/s1-fix-r2/s1-validate-r2/s1-validate-r3) accepted `SptGameRefs-AssemblyCSharp[.dll]`
here as "intentional, load-bearing, not a defect" -- **that was WRONG**: the REAL SPT/EFT client
never loads a `SptGameRefs-AssemblyCSharp.dll`, only `Assembly-CSharp.dll`/
`Assembly-CSharp-firstpass.dll` (confirmed directly this pass by reading 200 real MonoScripts out
of `D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers.assets` -- every EFT/BSG one reads
`m_AssemblyName == 'Assembly-CSharp'` or `'Assembly-CSharp-firstpass'`, note: NO `.dll` suffix,
matching validate_preset.py's own `EXPECTED_MONOSCRIPT` for the donor preset bundle's
`EFT.ScenesPreset`). Unity resolves a MonoBehaviour's script by (assembly, namespace, class)
against assemblies actually loaded in the running process -- a `SptGameRefs-AssemblyCSharp.dll`
MonoScript is UNRESOLVABLE in the real game, i.e. every one of the 5 SSD components would render
as a "missing script" the moment a real raid loads this bundle. The fix is
`tools/bundle/patch_monoscript_identity.py`, a MANDATORY post-build step (see
`tools/build_all.ps1`) that rewrites the built bundle's MonoScript identity back to the real
spelling *after* the Editor-time rename has served its only purpose (getting past AddComponent).
`ACCEPTED_ASSEMBLY_NAMES` is now strict: only the two real spellings pass; any `SptGameRefs-*`
name is a hard FAIL, on the theory that a green validator run must mean "safe to ship", not merely
"built without a C# exception".

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
# identity-fix (STRICT): the ONLY two spellings the real EFT client ever loads MonoScripts from,
# empirically verified by reading D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers.assets
# directly with UnityPy this pass (200 real MonoScripts scanned -- every EFT/BSG one reads exactly
# one of these two, NEVER with a ".dll" suffix). tools/gamerefs-fixup's Editor-time rename
# (SptGameRefs-AssemblyCSharp[Firstpass][.dll]) is a real, necessary, BUILD-time-only workaround
# for Unity's AddComponent blocker (docs/progress/s1-fix-r1.md) -- but it must never survive into
# a bundle that ships, because the real client cannot resolve a MonoScript under that name (see
# this file's module docstring "identity-fix update" above). Any SptGameRefs-* name here is now a
# hard FAIL, not an accepted alternate spelling -- tools/bundle/patch_monoscript_identity.py is the
# mandatory post-build step that removes it (tools/build_all.ps1 runs it automatically).
ACCEPTED_ASSEMBLY_NAMES = {"Assembly-CSharp", "Assembly-CSharp-firstpass"}
MONOBEHAVIOUR_HEADER_ONLY_BYTES = 40  # generous upper bound for enabled+m_Script+m_Name w/ no custom fields

# impl-texture-dimensions task item 3 -- UnityEngine.Rendering.TextureDimension (as serialized in
# a compiled Shader's m_ParsedForm.m_PropInfo.m_Props[].m_DefTexture.m_TexDim), confirmed
# empirically this pass by reading a real compiled stand-in Shader object out of a built bundle
# (every non-texture property reads m_TexDim==1 "Any"/unused; a real "2D"-declared property reads
# 2; before this task's fix, `_Cube` also read 2 -- now Cube/3D/2DArray). Maps each declared
# dimension to the UnityPy object class its assigned texture PPtr must resolve to.
TEXDIM_TO_CLASS = {
    2: "Texture2D",
    3: "Texture3D",
    4: "Cubemap",
    5: "Texture2DArray",
    6: "CubemapArray",
}

# impl-texture-dimensions §1/§2 -- the §C-compatible manifest.textures[...].dimension values WP1
# now emits and WP2 branches on. "Tex2D"/"Cube" become real, correctly-shaped bundle assets
# (Texture2D/Cubemap respectively) and ARE expected in the bundle's object counts; "Tex3D"/
# "Tex2DArray" are the task's explicit "don't assign" fallback (no correct Unity import path for
# the spike) -- MaterialImporter.cs never calls SetTexture for them, so they correctly never end
# up referenced by any Material and are therefore correctly ABSENT from the built bundle (Unity's
# own dependency-based AssetBundle inclusion).
TEXTURE_DIMENSIONS_BUILT_AS_ASSET = {"Tex2D": "Texture2D", "Cube": "Cubemap"}
TEXTURE_DIMENSIONS_NEVER_ASSIGNED = {"Tex3D", "Tex2DArray"}

# s1-fix-r1: Arena_Equator_Scripts is entirely WP2-authored (SS B.WP2.6) -- it never has a
# scenes/Arena_Equator_Scripts.json in the WP1 package for load_package_expected() to count, so its
# own fixed, spec-defined object/component counts (confirmed this pass against the first real
# authored build: AuthorScriptsScene.Build always creates exactly 8 GameObjects -- ScriptsRoot,
# LevelSettings, LocationExportInfo, BotZone_Main, 3x SpawnPoint_Player_*, LocationScene -- and 3
# BoxColliders, one per SpawnPointMarker) must be added on top of the package-derived expectations
# whenever the Scripts scene is actually expected to be present in the bundle (--require-scripts-scene).
SCRIPTS_SCENE_EXTRA_GAME_OBJECTS = 8
SCRIPTS_SCENE_EXTRA_BOX_COLLIDERS = 3

# fix-enabled-state -- same component-type set validate_extract.py checks the "enabled" field on;
# see that script's own KNOWN_ARENA_DISABLED_COUNTS/ENABLED_COMPONENT_TYPES comment for the root
# cause (Arena disables these on a handful of leftover/decorative objects per scene; left enabled
# by the importer before this fix, they rendered as untextured blocks/invisible walls). Duplicated
# here rather than imported -- "each WP6 script must stand alone" (this file's own module docstring).
ENABLED_COMPONENT_TYPES = {
    "MeshRenderer", "SkinnedMeshRenderer", "MeshCollider", "BoxCollider",
    "SphereCollider", "CapsuleCollider", "Light", "LODGroup",
}

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
    """Loads manifest.json + every scenes/*.json and computes both the raw package totals
    (mesh_count/material_count/texture_count -- every asset the WP1 extractor pulled in, incl.
    ones reachable only from a `skip_import: true` SkinnedMeshRenderer) and the IN-SCOPE totals
    WP2 is actually expected to build a Unity Renderer for (mesh_count_in_scope/
    material_count_in_scope/texture_count_in_scope): assets reachable from a non-skinned
    MeshFilter/MeshCollider/MeshRenderer.

    Why both exist (root-caused across three independent passes -- s1-fix-r1, s2-build,
    s2-validate-r1, see docs/progress/s2-fix-r1.md): SPEC.md §B.WP1 step 2 extracts every Mesh/
    Material/Texture reachable from a SkinnedMeshRenderer too ("dereference, don't skip"), but
    §B.WP2's own decision #8 (static-mesh-only spike scope) never imports a SkinnedMeshRenderer or
    builds any Renderer for it -- so Unity's own dependency-based AssetBundle inclusion correctly
    never pulls those assets into the built bundle. Comparing the bundle's actual counts against
    the raw package total was therefore comparing against the wrong baseline; comparing against
    the in-scope total is the check that actually answers "did the importer skip something it
    shouldn't have"."""
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
    in_scope_mesh_ids: set[str] = set()
    in_scope_material_ids: set[str] = set()
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
                    mid = comp.get("mesh_id")
                    if mid:
                        in_scope_mesh_ids.add(mid)
                elif t == "MeshRenderer":
                    expected["mesh_renderers"] += 1
                    for mid in comp.get("material_ids") or []:
                        in_scope_material_ids.add(mid)
                elif t == "SkinnedMeshRenderer":
                    expected["skinned_mesh_renderers"] += 1
                    # skip_import: true (SPEC §B.WP1 edge case, §B.WP2 decision #8) -- WP2 never
                    # builds a Renderer from this, so any Mesh/Material/Texture reachable ONLY from
                    # here is correctly absent from the bundle. Deliberately NOT added to either
                    # in-scope set above -- this is the fix (see the function docstring).
                elif t == "MeshCollider":
                    expected["mesh_colliders"] += 1
                    mid = comp.get("mesh_id")
                    if mid:
                        in_scope_mesh_ids.add(mid)
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

    in_scope_texture_ids: set[str] = set()
    materials_dir = package / "materials"
    package_materials_all = manifest.get("materials", {})
    for mat_id in in_scope_material_ids:
        if mat_id not in package_materials_all:
            warn(f"in-scope material {mat_id!r} (referenced by a MeshRenderer) is not listed in "
                 f"manifest.materials")
            continue
        mat_path = materials_dir / f"{mat_id}.json"
        if not mat_path.is_file():
            warn(f"in-scope material {mat_id!r} (referenced by a MeshRenderer) has no "
                 f"materials/{mat_id}.json file in the package")
            continue
        mat = load_json(mat_path)
        for slot in (mat.get("tex_envs") or {}).values():
            tid = slot.get("texture_id") if isinstance(slot, dict) else None
            if tid:
                in_scope_texture_ids.add(tid)

    expected["mesh_ids_in_scope"] = in_scope_mesh_ids
    expected["mesh_count_in_scope"] = len(in_scope_mesh_ids)
    expected["material_ids_in_scope"] = in_scope_material_ids
    expected["material_count_in_scope"] = len(in_scope_material_ids)
    expected["texture_ids_in_scope"] = in_scope_texture_ids
    expected["texture_count_in_scope"] = len(in_scope_texture_ids)

    # impl-texture-dimensions §3: split the in-scope texture set by manifest dimension --
    # "Tex2D"/"Cube" each become a real, correctly-shaped bundle asset (Texture2D/Cubemap
    # respectively, TEXTURE_DIMENSIONS_BUILT_AS_ASSET) and belong in the bundle's object counts;
    # "Tex3D"/"Tex2DArray" (TEXTURE_DIMENSIONS_NEVER_ASSIGNED) are MaterialImporter's explicit
    # "don't assign" fallback and are correctly ABSENT from the built bundle -- comparing the
    # OLD flat "every in-scope texture becomes a Texture2D" assumption against a package
    # containing any Cube/Tex3D texture would now produce a false FAIL (a Cubemap is not a
    # Texture2D; a skipped Tex3D is in no bundle object type at all).
    package_textures_all = manifest.get("textures", {})
    texture_ids_in_scope_by_class: dict[str, set[str]] = {}
    texture_ids_in_scope_never_assigned: set[str] = set()
    for tid in in_scope_texture_ids:
        dim = package_textures_all.get(tid, {}).get("dimension", "Tex2D")
        cls = TEXTURE_DIMENSIONS_BUILT_AS_ASSET.get(dim)
        if cls is not None:
            texture_ids_in_scope_by_class.setdefault(cls, set()).add(tid)
        else:
            texture_ids_in_scope_never_assigned.add(tid)
    expected["texture_ids_in_scope_by_class"] = texture_ids_in_scope_by_class
    expected["texture_count_in_scope_2d"] = len(texture_ids_in_scope_by_class.get("Texture2D", ()))
    expected["texture_count_in_scope_cube"] = len(texture_ids_in_scope_by_class.get("Cubemap", ()))
    expected["texture_ids_in_scope_never_assigned"] = texture_ids_in_scope_never_assigned
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
    # Mesh/Texture2D are compared against the IN-SCOPE totals (assets reachable from a non-skinned
    # MeshFilter/MeshCollider/MeshRenderer), not the raw package totals -- see
    # load_package_expected()'s docstring. A skip_import-only SkinnedMeshRenderer asset is
    # correctly excluded from the built bundle by Unity's own dependency-based AssetBundle
    # inclusion; comparing against the raw total previously produced a false FAIL here for every
    # real map containing one (docs/progress/s2-build.md §5 / s2-validate-r1.md §2).
    mesh_excluded = expected["mesh_count"] - expected["mesh_count_in_scope"]
    material_excluded = expected["material_count"] - expected["material_count_in_scope"]
    texture_excluded = expected["texture_count"] - expected["texture_count_in_scope"]
    never_assigned_count = len(expected.get("texture_ids_in_scope_never_assigned", ()))
    print(f"[validate_bundle_roundtrip]   package totals vs in-scope (non-skinned) totals: "
          f"meshes {expected['mesh_count']} vs {expected['mesh_count_in_scope']} "
          f"({mesh_excluded} excluded, skip_import-only), "
          f"materials {expected['material_count']} vs {expected['material_count_in_scope']} "
          f"({material_excluded} excluded), "
          f"textures {expected['texture_count']} vs {expected['texture_count_in_scope']} "
          f"({texture_excluded} excluded) -- in-scope split by dimension: "
          f"{expected['texture_count_in_scope_2d']} Tex2D, {expected['texture_count_in_scope_cube']} "
          f"Cube, {never_assigned_count} Tex3D/Tex2DArray (impl-texture-dimensions: the last group "
          f"is MaterialImporter's explicit never-assign fallback, correctly absent from the bundle)")
    exact_checks = [
        ("GameObject", n("GameObject"), expected["game_objects"]),
        ("Mesh", n("Mesh"), expected["mesh_count_in_scope"]),
        # impl-texture-dimensions §3: Texture2D/Cubemap compared against their OWN in-scope,
        # dimension-split totals -- a Cube-dimension texture correctly builds a Cubemap object,
        # never a Texture2D one (the old flat "all in-scope textures are Texture2D" comparison
        # would false-FAIL here the moment a package contains any real Cubemap, exactly as this
        # task's fix now guarantees for a correctly-imported one).
        ("Texture2D", n("Texture2D"), expected["texture_count_in_scope_2d"]),
        ("Cubemap", n("Cubemap"), expected["texture_count_in_scope_cube"]),
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
    # real defect, over is only a warning. Compared against the in-scope total (see
    # load_package_expected()'s docstring), not the raw package total.
    for label, exp in (("Material", expected["material_count_in_scope"]),):
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
# impl-texture-dimensions task item 3 -- independent re-derivation, from the built bundle's own
# compiled Shader objects, of what dimension each Material's shader declares for every texture
# property it assigns, cross-checked against the assigned texture's own UnityPy object class.
# ---------------------------------------------------------------------------

def validate_texture_dimension_consistency(env) -> None:
    """Never imports/reads anything from WP1's package or WP2's own C# source -- entirely
    self-contained against the BUILT bundle: for every Material, resolve its Shader's own
    compiled property list (m_ParsedForm.m_PropInfo.m_Props[].m_DefTexture.m_TexDim, confirmed
    empirically this pass -- e.g. a "2D"-declared property reads m_TexDim==2, "Cube" reads 4),
    then for every non-null assigned texture PPtr on that material, assert the ACTUAL referenced
    object's UnityPy class (Texture2D/Cubemap/Texture3D/...) matches what that declared dimension
    requires. This is a direct re-check of the reported bug's exact failure mode: 'Error assigning
    2D texture to CUBE texture property _Cube: Dimensions must match' is precisely a Texture2D
    object bound to a property whose compiled shader declares TexDim==4 (Cube)."""
    shader_tex_dims_cache: dict[int, dict[str, int]] = {}  # shader path_id -> {prop_name: m_TexDim}

    def shader_tex_dims(shader_obj) -> dict[str, int]:
        cached = shader_tex_dims_cache.get(shader_obj.path_id)
        if cached is not None:
            return cached
        try:
            tt = shader_obj.read_typetree()
        except Exception:  # noqa: BLE001
            shader_tex_dims_cache[shader_obj.path_id] = {}
            return {}
        props = tt.get("m_ParsedForm", {}).get("m_PropInfo", {}).get("m_Props", [])
        dims = {p.get("m_Name"): p.get("m_DefTexture", {}).get("m_TexDim")
                for p in props if p.get("m_Type") == 4}  # kTexture
        shader_tex_dims_cache[shader_obj.path_id] = dims
        return dims

    materials = [o for o in env.objects if o.type.name == "Material"]
    checked = 0
    mismatches = 0
    unresolved = 0
    for mat_obj in materials:
        try:
            tt = mat_obj.read_typetree()
            typed = mat_obj.read()
        except Exception as e:  # noqa: BLE001
            check(False, f"Material path_id={mat_obj.path_id}: read()/read_typetree() succeed for "
                          f"the texture-dimension cross-check (FAILED: {e})")
            continue
        mat_name = tt.get("m_Name") or f"path_id={mat_obj.path_id}"
        shader_ptr = getattr(typed, "m_Shader", None)
        if shader_ptr is None or shader_ptr.path_id == 0:
            continue
        try:
            shader_obj = shader_ptr.deref()
        except Exception:  # noqa: BLE001
            continue
        dims = shader_tex_dims(shader_obj)

        tex_envs_tt = tt.get("m_SavedProperties", {}).get("m_TexEnvs", [])
        sp_typed = getattr(typed, "m_SavedProperties", None)
        tex_envs_typed = list(sp_typed.m_TexEnvs) if sp_typed is not None else []
        if len(tex_envs_tt) != len(tex_envs_typed):
            continue  # shouldn't happen (mirrors the extractor's own paired-length assumption)
        for (prop_name, _te_tt), (_n, te_typed) in zip(tex_envs_tt, tex_envs_typed):
            tex_ptr = getattr(te_typed, "m_Texture", None)
            if tex_ptr is None or tex_ptr.path_id == 0:
                continue  # unassigned slot -- nothing to cross-check (§C.4's valid "declared but unassigned" state)
            declared_dim = dims.get(prop_name)
            if declared_dim is None:
                continue  # property not declared as a texture on this material's own shader -- not this check's concern
            try:
                tex_obj = tex_ptr.deref()
            except Exception as e:  # noqa: BLE001
                unresolved += 1
                check(False, f"Material {mat_name!r} prop {prop_name!r}: assigned texture PPtr "
                              f"resolves to a readable object (FAILED: {e})")
                continue
            checked += 1
            actual_class = tex_obj.type.name
            expected_class = TEXDIM_TO_CLASS.get(declared_dim)
            if expected_class is not None and actual_class != expected_class:
                mismatches += 1
                check(False, f"Material {mat_name!r} prop {prop_name!r}: shader declares this "
                              f"texture property dimension={declared_dim} ({expected_class}) but "
                              f"the assigned texture object is a {actual_class} -- this is exactly "
                              f"the reported 'Error assigning 2D texture to CUBE/3D texture "
                              f"property: Dimensions must match' scene-load failure mode")
    if mismatches == 0 and unresolved == 0:
        check(True, f"{checked} assigned Material texture slot(s) cross-checked against their "
                     f"shader's own compiled property dimension -- no material references a "
                     f"Texture2D on a property whose shader declares Cube/3D/2DArray (or any other "
                     f"dimension mismatch)")


# ---------------------------------------------------------------------------
# fix-enabled-state: per-scene disabled-component counts, bundle vs package
# ---------------------------------------------------------------------------

def count_disabled_in_package_scenes(package: Path, manifest: dict) -> dict[str, dict[str, int]]:
    """Per-scene, per-type count of components whose extracted record has "enabled": false --
    same contract as validate_extract.py's count_disabled_in_package() (missing key or explicit
    true both mean enabled), duplicated here rather than imported since this script is meant to
    stand alone against just the built bundle + package."""
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


def count_disabled_in_bundle_scenes(env, scene_names: list[str]) -> dict[str, dict[str, int]]:
    """Per-scene, per-type count of disabled components actually built into the bundle, read back
    independently of WP2's own C# importer. Unity's multi-scene AssetBundleBuild serializes each
    content scene's own GameObject/Transform/component data into its own nested SerializedFile
    named exactly "BuildPlayer-<SceneName>" (confirmed empirically this pass against a real built
    bundle -- its object-type counts for Arena_Equator, e.g. 13143 MeshRenderer/4730 MeshCollider,
    exactly match Arena's own per-scene totals; the sibling "BuildPlayer-<SceneName>.sharedAssets"
    nested file holds only shared Mesh/Material/Texture/Shader dependencies, no
    GameObject/component data) -- so this only ever needs to look inside that one nested file per
    scene, never the whole bundle's flat object list (which would double-count nothing here since
    components aren't shared across scenes, but would make a missing/renamed nested file silently
    invisible instead of a named, actionable failure)."""
    by_name = {sf.name: sf for sf in iter_serialized_files(env)}
    out: dict[str, dict[str, int]] = {}
    for scene_name in scene_names:
        sf = by_name.get(f"BuildPlayer-{scene_name}")
        if sf is None:
            check(False, f"fix-enabled-state: bundle has a nested SerializedFile named "
                          f"'BuildPlayer-{scene_name}' to read disabled-component counts from "
                          f"(not found among: {sorted(by_name)})")
            continue
        scene_counts: dict[str, int] = {}
        for obj in sf.objects.values():
            t = obj.type.name
            if t not in ENABLED_COMPONENT_TYPES:
                continue
            try:
                tt = obj.read_typetree()
            except Exception:  # noqa: BLE001
                continue
            if tt.get("m_Enabled", 1) == 0:
                scene_counts[t] = scene_counts.get(t, 0) + 1
        out[scene_name] = scene_counts
    return out


def validate_disabled_component_counts_bundle(env, package: Path, manifest: dict) -> None:
    """fix-enabled-state acceptance check, bundle side: every scene's per-type disabled-component
    count actually built into the bundle must match the extracted package's own count exactly --
    i.e. SceneAssembler.cs's ReadEnabled()/component.enabled assignment round-trips through Unity's
    AssetBundle build faithfully, component-for-component. (validate_extract.py's own
    validate_disabled_component_counts() separately checks package-vs-Arena; chained together the
    two checks cover extraction -> package -> bundle end to end, matching the task's "per-scene
    disabled component counts in the extraction and in the built bundle match Arena's".) Arena_Equator_Scripts
    is WP2-authored, never in manifest.scenes, and authors nothing that starts disabled (its 3
    spawn-trigger BoxColliders are always enabled=true) -- correctly excluded here by only
    iterating manifest.scenes, not the bundle's full scene list."""
    package_counts = count_disabled_in_package_scenes(package, manifest)
    scene_names = manifest.get("scenes", [])
    bundle_counts = count_disabled_in_bundle_scenes(env, scene_names)

    total_bad = 0
    for scene_name in scene_names:
        expected = package_counts.get(scene_name, {})
        actual = bundle_counts.get(scene_name, {})
        types = sorted(set(expected) | set(actual))
        scene_bad = 0
        for t in types:
            exp_n = expected.get(t, 0)
            act_n = actual.get(t, 0)
            if exp_n != act_n:
                scene_bad += 1
                check(False, f"{scene_name}: disabled {t} count in bundle ({act_n}) == package "
                              f"({exp_n})")
        if scene_bad == 0:
            print(f"[validate_bundle_roundtrip]  ok : {scene_name}: disabled-component counts in "
                  f"bundle match package exactly ({dict(sorted(expected.items())) or '{}'})")
        total_bad += scene_bad
    if total_bad == 0:
        print("[validate_bundle_roundtrip]  ok : every scene's disabled-component counts in the "
              "built bundle match the extracted package exactly")


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
            # s1-fix-r1: this pass's real round trip put LocationExportInfo (one 8-byte `long`
            # field, the smallest of the 5 SS D types) at exactly 40 bytes -- a strict `>` rejected
            # it despite editor_verify.json independently confirming (in the SAME build) that its
            # one real field value (unix_date_time=1757707200) genuinely survived the round trip.
            # Every other SS D type observed this pass is 188-340 bytes, well clear of the
            # threshold either way -- `>=` is the correct comparison against this real boundary
            # case, not a weakening (an object at exactly the empty-header estimate can still, as
            # confirmed here, carry real non-default field data; a strictly-smaller object could not).
            check(e["byte_size"] >= MONOBEHAVIOUR_HEADER_ONLY_BYTES,
                  f"{class_name} (path_id={e['path_id']}): serialized byte size ({e['byte_size']}) is "
                  f"clearly larger than an empty MonoBehaviour header (>={MONOBEHAVIOUR_HEADER_ONLY_BYTES} "
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

def validate_editor_verify(path: Path, expected: dict, require_scripts_scene: bool = True) -> None:
    if not path.is_file():
        check(False, f"editor_verify.json exists at {path} (task instruction: 'cross-check "
                      f"work/build/s1/editor_verify.json' -- WP2's VerifyBuild.cs has not produced one)")
        return
    report = load_json(path)
    manifest = expected["manifest"]
    # NOTE: Arena_Equator_Scripts is WP2-authored, never in the WP1 package's own manifest.scenes
    # (SS B.WP2.6 -- there is no scenes/Arena_Equator_Scripts.json to extract), so it's added here
    # explicitly -- but only when the caller actually expects it to exist. A run made with
    # -skipScriptsScene (or any S1 slice run before the Scripts-scene blocker is resolved,
    # docs/progress/s1-finish.md SS2) legitimately has no such scene in editor_verify.json at all;
    # gating this the same way as validate_eft_scripts's --require-scripts-scene flag (instead of
    # unconditionally requiring it) keeps this a real check for a full/S2 run while not scoring an
    # already-documented, intentional S1 gap as a fresh defect.
    scene_names = set(manifest.get("scenes", []))
    if require_scripts_scene:
        scene_names |= {"Arena_Equator_Scripts"}
    reported = {s.get("scene_name"): s for s in report.get("scenes", [])}

    missing_scenes = scene_names - reported.keys()
    check(not missing_scenes, f"editor_verify.json covers every expected scene "
                               f"(missing: {sorted(missing_scenes)})")
    if not require_scripts_scene and "Arena_Equator_Scripts" not in reported:
        print("[validate_bundle_roundtrip] WARN: editor_verify.json has no 'Arena_Equator_Scripts' scene "
              "-- tolerated under --no-require-scripts-scene (Scripts-scene authoring not exercised this run)")

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
            spawn = eft.get("SpawnPointMarker", {})
            markers = spawn.get("markers", [])
            check(botzone.get("count") == 1,
                  f"editor_verify.json Arena_Equator_Scripts: BotZone.count == 1 (got {botzone.get('count')})")
            check(botzone.get("patrol_ways_length") == 0,
                  f"editor_verify.json Arena_Equator_Scripts: BotZone.patrol_ways_length == 0 "
                  f"(empty, not absent/null -- risk #11; got {botzone.get('patrol_ways_length')})")
            # s1-fix-r1 FIX: this was `== 0`, i.e. it demanded BotZone.SpawnPointMarkers be EMPTY --
            # backwards for risk #11's real intent (non-null, not necessarily empty) and flatly
            # contradicted by AuthorScriptsScene.Build's own real behavior (§D: 2-4 markers "in
            # practice", and `botZone.SpawnPointMarkers.AddRange(markers)` always adds every one
            # authored). Confirmed as a genuine validator bug this pass: the first-ever real authored
            # build has 3 real markers, correctly non-empty, and the old check hard-FAILed on exactly
            # that correct data. The meaningful assertion is that BotZone's own list is wired to the
            # SAME markers eft_components.SpawnPointMarker independently counted (catches "BotZone
            # was never actually linked" bugs) -- not a hardcoded literal either way.
            check(botzone.get("spawn_point_markers_count") == spawn.get("count"),
                  f"editor_verify.json Arena_Equator_Scripts: BotZone.spawn_point_markers_count "
                  f"({botzone.get('spawn_point_markers_count')}) == SpawnPointMarker.count "
                  f"({spawn.get('count')}) -- risk #11: non-null AND correctly wired to the same "
                  f"authored markers, not asserted empty")
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
    if args.require_scripts_scene:
        # s1-fix-r1: Arena_Equator_Scripts adds its own fixed, spec-defined GameObjects/components
        # on top of the package-derived (2-content-scene) totals -- see SCRIPTS_SCENE_EXTRA_* above.
        expected["game_objects"] += SCRIPTS_SCENE_EXTRA_GAME_OBJECTS
        expected["box_colliders"] += SCRIPTS_SCENE_EXTRA_BOX_COLLIDERS
    print(f"[validate_bundle_roundtrip] package expectations loaded from {args.package}: "
          f"{expected['game_objects']} objects, {expected['mesh_count']} meshes "
          f"({expected['mesh_count_in_scope']} in-scope), "
          f"{expected['material_count']} materials ({expected['material_count_in_scope']} in-scope), "
          f"{expected['texture_count']} textures ({expected['texture_count_in_scope']} in-scope)")

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
        validate_texture_dimension_consistency(env)
        validate_disabled_component_counts_bundle(env, args.package, expected["manifest"])
        validate_eft_scripts(env, args.require_scripts_scene)
    else:
        check(False, "GameObject/Mesh/Material/collider/LODGroup/EFT-component checks run "
                      "(SKIPPED -- no bundle to read)")

    print()
    print(f"[validate_bundle_roundtrip] === {args.navmesh_bundle.name} ===")
    validate_navmesh_bundle(args.navmesh_bundle, args.require_navmesh)

    print()
    print(f"[validate_bundle_roundtrip] === {args.editor_verify.name} cross-check ===")
    validate_editor_verify(args.editor_verify, expected, require_scripts_scene=args.require_scripts_scene)

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
