#!/usr/bin/env python3
"""ArenaMapPort WP1 extractor -- docs/SPEC.md §B.WP1 (algorithm) / §C (package
format contract).

Reuses, per §B.WP1's explicit instruction, analysis/scripts/common.py's
EQUATOR scene-index->suffix table and analysis/scripts/census.py's
spawn_zone_anchors()/resolve_script_name() machinery -- the proven
exact-full-path file matching and MonoScript-pool dereferencing this project
already validated against real Arena data (arena-census.md Method notes).

CLI:
    python extract_equator.py --source <ArenaData dir> --scenes <51,53,...|all5>
        [--slice-roots "SCENE_IDX:id1,id2,..."]... --output <package dir>
        [--fixed-timestamp <ISO8601>] [--native-features --target <SptData dir>]

    --native-features requires all five complete scenes. It adds original
    ballistic fields and the separately reconstructed environmental audio graph;
    legacy slice/full extraction remains unchanged without this explicit opt-in.

    python extract_equator.py --source <dir> --scenes all5 --census-only
        (fast, no writes -- proves the same code path covers the full 5-scene
        set per §B.WP1's "make sure the full 5-scene run is supported by the
        same code" requirement, without doing a full multi-hundred-MB extract.)

See docs/progress/wp1.md for the S1 slice's exact object selection and the
ground-truth field-shape findings this implementation is based on.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
from collections import Counter

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)
sys.path.insert(0, os.path.normpath(os.path.join(_HERE, "..", "..", "analysis", "scripts")))

import UnityPy  # noqa: E402
from PIL import Image  # noqa: E402

from common_ids import (  # noqa: E402
    CAST_SHADOWS,
    COLOR_SPACE,
    HardExtractError,
    LIGHT_PROBE_USAGE,
    LIGHT_TYPE,
    PHYSIC_COMBINE,
    SHADOW_TYPE,
    content_id,
    reflection_probe_usage,
    resolve_tag,
    texture_format_name,
)
from mesh_bin import write_mesh_bin  # noqa: E402
from volume_textures import write_volume_texture  # noqa: E402

import common as arena_common  # analysis/scripts/common.py -- EQUATOR table  # noqa: E402
import census as arena_census  # analysis/scripts/census.py -- spawn_zone_anchors  # noqa: E402
import extract_physics_props as physics_props  # PLAN-TWO-MAPS item 5  # noqa: E402
import extract_light_culling as light_culling  # PLAN-TWO-MAPS follow-up  # noqa: E402

SCHEMA_VERSION = 1

# §E.1 -- the 5 content scenes WP1 is ever asked to extract. "Scripts" (54)
# is WP2-authored fresh (never extracted); TeamFight_AI(52)/Sound(56)/
# TeamFight_Culling(59) are dropped per §E.1's own table. The optional native-audio
# pass reads selected Sound(56) components into a separate graph, not scene geometry.
SCENE_REGISTRY = {
    51: "Arena_Equator",
    53: "Arena_Equator_DesignStuff",
    55: "Arena_Equator_Lighting",
    57: "Arena_Equator_TeamFight_DesignStuff",
    58: "Arena_Equator_TeamFight",
}
ALL5 = [51, 53, 55, 57, 58]

DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"


# --------------------------------------------------------------------------
# Environment loading
# --------------------------------------------------------------------------

def load_tags(source: str):
    """Read the selected install; raw game-manager dumps are local-only evidence."""
    env = UnityPy.load(f"{source}/globalgamemanagers")
    for obj in env.objects:
        if obj.type.name == "TagManager":
            tags = obj.read_typetree().get("tags")
            if isinstance(tags, list) and all(isinstance(tag, str) for tag in tags):
                return tags
    raise HardExtractError(f"TagManager.tags missing or invalid in {source}/globalgamemanagers")


def scene_external_names(level_path: str) -> list[str]:
    """Keep source order while skipping Unity's built-in, non-file resources."""
    env = UnityPy.load(level_path)
    names = []
    for asset in env.files.values():
        for external in getattr(asset, "externals", []):
            name = str(external.path)
            if not name or "unity default resources" in name.lower() or "unity_builtin_extra" in name.lower():
                continue
            if name not in names:
                names.append(name)
    return names


def load_environment(source: str, scene_indices: list[int]):
    """Loads level<N>+sharedassets<N>.assets for every requested scene, plus
    every external file referenced by that source scene (§B.WP1 step 1),
    plus globalgamemanagers.assets/resources.assets
    (needed for the MonoScript pool census.spawn_zone_anchors uses). Returns
    (env, exact_paths) where exact_paths[idx] = {"level":..., "sharedassets":...}
    -- the exact-full-path keys is_scene_file()-style code must match against
    (analysis/arena-census.md Method note 3's double-registration pitfall)."""
    paths: list[str] = []
    seen: set[str] = set()

    def add(p: str):
        if p not in seen:
            seen.add(p)
            paths.append(p)

    add(f"{source}/globalgamemanagers.assets")
    add(f"{source}/resources.assets")

    exact_paths: dict[int, dict[str, str]] = {}
    for idx in scene_indices:
        lvl = f"{source}/level{idx}"
        sa = f"{source}/sharedassets{idx}.assets"
        add(lvl)
        add(sa)
        exact_paths[idx] = {"level": lvl, "sharedassets": sa}
        for ext_name in scene_external_names(lvl):
            add(f"{source}/{ext_name}")

    for p in paths:
        if not os.path.exists(p):
            print(f"WARNING: expected input file missing, skipping: {p}", file=sys.stderr)
    paths = [p for p in paths if os.path.exists(p)]

    env = UnityPy.load(*paths)
    return env, exact_paths


def compute_spawn_anchors(env, scene_dict: dict[int, str], source: str, warnings: list[str]) -> dict:
    """Wraps analysis/scripts/census.py's spawn_zone_anchors(), matching it against the files WE
    actually loaded (--source) instead of census.py's own hardcoded common.BASE.

    Real, confirmed bug this fixes (docs/progress/s2-validate-r1.md SS1, re-verified ground truth
    2026-09-13: X: is not mounted in this session at all -- only C:/D:/Y:/Z: are -- confirming
    docs/progress/SOURCE-PATH.md's lead note that X: really is gone and D:/Battlestate Games/... is
    now the correct, current --source): census.py's is_scene_file()/scene_exact_paths() build their
    match-set from `common.BASE` ("X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"), a
    constant imported into census.py's own module namespace at `from common import BASE` time.
    analysis/ is READ-ONLY for every WP (SPEC.md's directory table, "nobody writes here except
    re-running an already-documented script") so that file cannot be edited here -- but every file
    UnityPy actually loaded into `env` is keyed by our real, source-prefixed path, so comparing
    against X:-prefixed paths silently matched ZERO loaded files whenever --source != X: (true for
    every run on this machine right now), and spawn_zone_anchors() returned {} with no exception,
    no warning -- manifest.anchors_reference silently lost all 20 real SpawnPointMarker entries.

    Fix: monkey-patch the one name census.py's own functions actually read at call time
    (`census.BASE`, a plain module attribute, looked up fresh on every call -- reassigning it here
    does not touch or persist beyond this process, and does not edit analysis/scripts/*.py on disk)
    to our real --source for the duration of this one call, then restore it. This makes
    is_scene_file()'s exact-path matching agree with what `env` was actually built from, regardless
    of where on disk the Arena install currently lives.

    Also: the original code's silent-empty-on-mismatch behavior is exactly why the s2-build/
    s2-validate-r1 bug went unnoticed for two whole passes -- so besides fixing the match, an empty
    result is now always surfaced as an explicit warning rather than treated as an unremarkable
    'this map has no anchors'.
    """
    prev_base = getattr(arena_census, "BASE", None)
    arena_census.BASE = source
    try:
        anchors = arena_census.spawn_zone_anchors(env, scene_dict)
    except Exception as e:
        warnings.append(f"spawn_zone_anchors failed: {e} -- anchors_reference left empty")
        return {}
    finally:
        if prev_base is not None:
            arena_census.BASE = prev_base
    if not anchors:
        warnings.append(
            "anchors_reference came back EMPTY after spawn_zone_anchors() -- this is either a "
            "genuine 'this scene set has no spawn/zone MonoBehaviours' or a silent --source/BASE "
            "path mismatch (docs/progress/s2-validate-r1.md SS1); do not assume the former without "
            "checking -- a raw scan for e.g. EFT.Game.Spawning.SpawnPointMarker is cheap"
        )
    return anchors


# --------------------------------------------------------------------------
# Scene graph (GameObject/Transform) -- built once per scene's level<N> file
# --------------------------------------------------------------------------

def build_scene_graph(f):
    """f: the level<N> SerializedFile (env.files[exact_level_path]).
    Returns (go_map, tf_map, go_of_tf). Only GameObject/Transform objects
    live in level<N> (confirmed, arena-census.md §2.1's per-file breakdown --
    Mesh/Texture2D/Material/Shader live in sharedassets<N>.assets or an
    external shared-pool file instead)."""
    go_map: dict[int, dict] = {}
    tf_map: dict[int, dict] = {}
    for obj in f.objects.values():
        tn = obj.type.name
        if tn == "GameObject":
            t = obj.read_typetree()
            comps = []
            for c in t.get("m_Component", []):
                cp = c.get("component", c)
                pid = cp.get("m_PathID", 0)
                if pid:
                    comps.append(pid)
            go_map[obj.path_id] = {
                "name": t.get("m_Name", ""),
                "layer": t.get("m_Layer", 0),
                "tag_raw": t.get("m_Tag", 0),
                "active": bool(t.get("m_IsActive", True)),
                "components": comps,
            }
        elif tn == "Transform":
            t = obj.read_typetree()
            father = t.get("m_Father", {})
            father_id = father.get("m_PathID", 0) if father.get("m_FileID", 0) == 0 else -1
            children = [c.get("m_PathID", 0) for c in t.get("m_Children", []) if c.get("m_FileID", 0) == 0]
            go_id = t.get("m_GameObject", {}).get("m_PathID", 0)
            lp, lr, ls = t["m_LocalPosition"], t["m_LocalRotation"], t["m_LocalScale"]
            tf_map[obj.path_id] = {
                "go_id": go_id,
                "father_id": father_id,
                "children": children,
                "pos": [lp["x"], lp["y"], lp["z"]],
                "rot": [lr["x"], lr["y"], lr["z"], lr["w"]],
                "scale": [ls["x"], ls["y"], ls["z"]],
            }
    go_of_tf = {t["go_id"]: tid for tid, t in tf_map.items()}
    return go_map, tf_map, go_of_tf


def subtree_ids(tf_map: dict, root: int) -> list[int]:
    out: list[int] = []
    seen: set[int] = set()
    stack = [root]
    while stack:
        cur = stack.pop()
        if cur in seen or cur not in tf_map:
            continue
        seen.add(cur)
        out.append(cur)
        stack.extend(tf_map[cur]["children"])
    return out


# --------------------------------------------------------------------------
# Extraction context (mutable dedup work-queues + diagnostics)
# --------------------------------------------------------------------------

class ExtractionContext:
    def __init__(self, env):
        self.env = env
        self.mesh_work: dict[str, tuple] = {}      # cid -> (mesh_obj, basename)
        self.material_work: dict[str, tuple] = {}  # cid -> (mat_obj, basename)
        self.texture_work: dict[str, dict] = {}    # cid -> {"obj","basename","prop_names"}
        self.errors: list[str] = []
        self.warnings: list[str] = []
        self._basename_cache: dict[int, str] = {}

    def owning_basename(self, obj) -> str:
        af = obj.assets_file
        cached = self._basename_cache.get(id(af))
        if cached:
            return cached
        for fname, f2 in self.env.files.items():
            if f2 is af:
                b = os.path.basename(fname)
                self._basename_cache[id(af)] = b
                return b
        return "unknown"

    def queue_mesh(self, mesh_obj) -> str:
        basename = self.owning_basename(mesh_obj)
        cid = content_id(basename, mesh_obj.path_id, "Mesh")
        self.mesh_work.setdefault(cid, (mesh_obj, basename))
        return cid

    def queue_material(self, mat_obj) -> str:
        basename = self.owning_basename(mat_obj)
        cid = content_id(basename, mat_obj.path_id, "Material")
        self.material_work.setdefault(cid, (mat_obj, basename))
        return cid


# --------------------------------------------------------------------------
# Component builders (§C.6)
# --------------------------------------------------------------------------

def resolve_physic_material(mptr, ctx: ExtractionContext):
    if mptr is None or mptr.path_id == 0:
        return None
    pm_obj = mptr.deref()
    pt = pm_obj.read_typetree()
    return {
        "dynamic_friction": pt.get("m_DynamicFriction", 0.6),
        "static_friction": pt.get("m_StaticFriction", 0.6),
        "bounciness": pt.get("m_Bounciness", 0.0),
        "friction_combine": PHYSIC_COMBINE.get(pt.get("m_FrictionCombine", 0), "Average"),
        "bounce_combine": PHYSIC_COMBINE.get(pt.get("m_BounceCombine", 0), "Average"),
    }


def build_meshfilter(obj, ctx: ExtractionContext):
    typed = obj.read()
    if typed.m_Mesh.path_id == 0:
        ctx.warnings.append(f"MeshFilter {obj.path_id}: no mesh assigned, component skipped")
        return None
    mesh_id = ctx.queue_mesh(typed.m_Mesh.deref())
    return {"type": "MeshFilter", "mesh_id": mesh_id}


def build_meshrenderer(obj, ctx: ExtractionContext):
    tt = obj.read_typetree()
    typed = obj.read()
    mat_ids = [ctx.queue_material(mp.deref()) for mp in typed.m_Materials if mp.path_id != 0]
    return {
        "type": "MeshRenderer",
        "material_ids": mat_ids,
        "cast_shadows": CAST_SHADOWS.get(tt.get("m_CastShadows", 1), "On"),
        "receive_shadows": bool(tt.get("m_ReceiveShadows", 1)),
        "light_probe_usage": LIGHT_PROBE_USAGE.get(tt.get("m_LightProbeUsage", 1), "Off"),
        "reflection_probe_usage": reflection_probe_usage(tt.get("m_ReflectionProbeUsage", 1)),
        # fix-enabled-state: Arena disables a handful of renderers per-object (e.g. stencil
        # ceiling/floor cutouts, leftover 'colider N'/'Cube' placeholder meshes) rather than
        # removing them -- the importer never read this before, so every one of them came back
        # enabled in our bundle: large flat untextured blocks sitting in walkways. Default true
        # when the field is absent (back-compat with any package extracted before this field
        # existed) via tt.get's own default below.
        "enabled": bool(tt.get("m_Enabled", 1)),
    }


def build_skinnedmeshrenderer(obj, ctx: ExtractionContext):
    tt = obj.read_typetree()
    typed = obj.read()
    mat_ids = [ctx.queue_material(mp.deref()) for mp in typed.m_Materials if mp.path_id != 0]
    # §B.WP1 step 2: extract every Mesh reachable from a SkinnedMeshRenderer
    # too ("dereference, don't skip") even though skip_import means the
    # scene JSON never references it by id -- shares the meshes/ dedup pool
    # with anything else that happens to use the same source mesh.
    if typed.m_Mesh.path_id != 0:
        ctx.queue_mesh(typed.m_Mesh.deref())
    bone_names = []
    for bp in (typed.m_Bones or []):
        if bp.path_id == 0:
            continue
        try:
            bone_tf_obj = bp.deref()
            btt = bone_tf_obj.read_typetree()
            go_pid = btt.get("m_GameObject", {}).get("m_PathID", 0)
            if go_pid:
                go_obj = bone_tf_obj.assets_file.objects.get(go_pid)
                if go_obj is not None:
                    bone_names.append(go_obj.read_typetree().get("m_Name", ""))
        except Exception as e:  # pragma: no cover -- defensive, not hit by S1 slice
            ctx.warnings.append(f"SkinnedMeshRenderer {obj.path_id}: bone deref failed: {e}")
    return {
        "type": "SkinnedMeshRenderer",
        "material_ids": mat_ids,
        "cast_shadows": CAST_SHADOWS.get(tt.get("m_CastShadows", 1), "On"),
        "receive_shadows": bool(tt.get("m_ReceiveShadows", 1)),
        "light_probe_usage": LIGHT_PROBE_USAGE.get(tt.get("m_LightProbeUsage", 1), "Off"),
        "reflection_probe_usage": reflection_probe_usage(tt.get("m_ReflectionProbeUsage", 1)),
        "bind_pose_bone_names": bone_names,
        "skip_import": True,
        # fix-enabled-state: recorded for schema completeness/parity with MeshRenderer even
        # though skip_import means WP2 never attaches a SkinnedMeshRenderer at all right now.
        "enabled": bool(tt.get("m_Enabled", 1)),
    }


def build_meshcollider(obj, ctx: ExtractionContext):
    tt = obj.read_typetree()
    typed = obj.read()
    if typed.m_Mesh.path_id == 0:
        ctx.warnings.append(f"MeshCollider {obj.path_id}: no mesh assigned")
        mesh_id = None
    else:
        mesh_id = ctx.queue_mesh(typed.m_Mesh.deref())
    return {
        "type": "MeshCollider",
        "mesh_id": mesh_id,
        "convex": bool(tt.get("m_Convex")),
        "is_trigger": bool(tt.get("m_IsTrigger")),
        "physic_material": resolve_physic_material(typed.m_Material, ctx),
        # fix-enabled-state: Arena disables ~25 'colider N'/'ballistic' MeshColliders per scene
        # (leftover/duplicate collision meshes) -- BakeNavMesh.Bake's CollectSources filter
        # already honors Collider.enabled at bake time, so once SceneAssembler applies this the
        # NavMesh bake is correct for free.
        "enabled": bool(tt.get("m_Enabled", 1)),
    }


def build_boxcollider(obj, ctx: ExtractionContext):
    tt = obj.read_typetree()
    typed = obj.read()
    c, s = tt["m_Center"], tt["m_Size"]
    return {
        "type": "BoxCollider",
        "center": [c["x"], c["y"], c["z"]],
        "size": [s["x"], s["y"], s["z"]],
        "is_trigger": bool(tt.get("m_IsTrigger")),
        "physic_material": resolve_physic_material(typed.m_Material, ctx),
        # fix-enabled-state: e.g. Arena_Equator_DesignStuff disables 380 mannequin-gear
        # BoxColliders (decorative bodies, not meant to block movement) and
        # Arena_Equator_Lighting disables 19 stencil-cube BoxColliders.
        "enabled": bool(tt.get("m_Enabled", 1)),
    }


def build_spherecollider(obj, ctx: ExtractionContext):
    tt = obj.read_typetree()
    typed = obj.read()
    c = tt["m_Center"]
    return {
        "type": "SphereCollider",
        "center": [c["x"], c["y"], c["z"]],
        "radius": tt.get("m_Radius", 0.5),
        "is_trigger": bool(tt.get("m_IsTrigger")),
        "physic_material": resolve_physic_material(typed.m_Material, ctx),
        # fix-enabled-state: none of Equator's real SphereColliders are disabled (recomputed via
        # the same census this task's root-cause analysis used), recorded for schema parity.
        "enabled": bool(tt.get("m_Enabled", 1)),
    }


def build_capsulecollider(obj, ctx: ExtractionContext):
    tt = obj.read_typetree()
    typed = obj.read()
    c = tt["m_Center"]
    return {
        "type": "CapsuleCollider",
        "center": [c["x"], c["y"], c["z"]],
        "radius": tt.get("m_Radius", 0.5),
        "height": tt.get("m_Height", 2.0),
        "direction": tt.get("m_Direction", 1),
        "is_trigger": bool(tt.get("m_IsTrigger")),
        "physic_material": resolve_physic_material(typed.m_Material, ctx),
        # fix-enabled-state: none of Equator's real CapsuleColliders are disabled, recorded for
        # schema parity.
        "enabled": bool(tt.get("m_Enabled", 1)),
    }


def build_light(obj, ctx: ExtractionContext):
    tt = obj.read_typetree()
    color = tt.get("m_Color", {"r": 1, "g": 1, "b": 1, "a": 1})
    shadows = tt.get("m_Shadows", {}) or {}
    return {
        "type": "Light",
        "light_type": LIGHT_TYPE.get(tt.get("m_Type"), f"Unknown({tt.get('m_Type')})"),
        "color": [color["r"], color["g"], color["b"], color["a"]],
        "intensity": tt.get("m_Intensity", 1.0),
        "range": tt.get("m_Range", 10.0),
        "spot_angle": tt.get("m_SpotAngle", 30.0),
        "lightmapping_mode": tt.get("m_Lightmapping", 4),
        "shadow_type": SHADOW_TYPE.get(shadows.get("m_Type"), f"Unknown({shadows.get('m_Type')})"),
        # fix-enabled-state: no Light is disabled in any of Equator's 5 content scenes (recomputed
        # this pass), recorded for schema parity.
        "enabled": bool(tt.get("m_Enabled", 1)),
    }


def build_lodgroup(obj, f, ctx: ExtractionContext):
    tt = obj.read_typetree()
    lods_out = []
    for lod in tt.get("m_LODs", []):
        renderer_ids = []
        for r in lod.get("renderers", []):
            rp = r.get("renderer", r)
            pid = rp.get("m_PathID", 0)
            if not pid:
                continue  # a null renderer slot -- §B.WP2.5 notes LODGroup.SetLODs accepts this silently
            if rp.get("m_FileID", 0) != 0:
                ctx.warnings.append(f"LODGroup {obj.path_id}: cross-file renderer ref {rp} skipped (not expected, §B.WP1 step 5)")
                continue
            robj = f.objects.get(pid)
            if robj is None:
                ctx.warnings.append(f"LODGroup {obj.path_id}: renderer path_id {pid} missing from file")
                continue
            go_pid = robj.read_typetree().get("m_GameObject", {}).get("m_PathID", 0)
            if go_pid:
                renderer_ids.append(str(go_pid))
        lods_out.append({
            "screen_relative_height": lod.get("screenRelativeHeight", 0.0),
            "renderer_object_ids": renderer_ids,
            "fade_transition_width": lod.get("fadeTransitionWidth", 0.0),
        })
    return {
        "type": "LODGroup",
        "lods": lods_out,
        "size": tt.get("m_Size", 1.0),
        "fade_mode": tt.get("m_FadeMode", 0),
        # fix-enabled-state: no LODGroup is disabled in any of Equator's 5 content scenes
        # (recomputed this pass), recorded for schema parity/completeness with every other
        # component type this fix covers.
        "enabled": bool(tt.get("m_Enabled", 1)),
    }


COMPONENT_BUILDERS = {
    "MeshFilter": lambda obj, f, ctx: build_meshfilter(obj, ctx),
    "MeshRenderer": lambda obj, f, ctx: build_meshrenderer(obj, ctx),
    "SkinnedMeshRenderer": lambda obj, f, ctx: build_skinnedmeshrenderer(obj, ctx),
    "MeshCollider": lambda obj, f, ctx: build_meshcollider(obj, ctx),
    "BoxCollider": lambda obj, f, ctx: build_boxcollider(obj, ctx),
    "SphereCollider": lambda obj, f, ctx: build_spherecollider(obj, ctx),
    "CapsuleCollider": lambda obj, f, ctx: build_capsulecollider(obj, ctx),
    "Light": lambda obj, f, ctx: build_light(obj, ctx),
    "LODGroup": lambda obj, f, ctx: build_lodgroup(obj, f, ctx),
    "ReflectionProbe": lambda obj, f, ctx: {"type": "ReflectionProbe", "skip_import": True},
    "Rigidbody": lambda obj, f, ctx: physics_props.build_rigidbody(obj, ctx),  # PLAN-TWO-MAPS item 5
}


def build_components(f, comp_ids: list[int], scene_idx: int, ctx: ExtractionContext) -> list[dict]:
    out = []
    for cid in comp_ids:
        obj = f.objects.get(cid)
        if obj is None:
            ctx.warnings.append(f"scene {scene_idx}: component path_id {cid} missing from file")
            continue
        builder = COMPONENT_BUILDERS.get(obj.type.name)
        if builder is None:
            continue  # §C.6: exhaustive list for the spike, everything else intentionally unrecorded
        rec = builder(obj, f, ctx)
        if rec is not None:
            out.append(rec)
    return out


# --------------------------------------------------------------------------
# Per-scene hierarchy walk (§C.5)
# --------------------------------------------------------------------------

def extract_scene(scene_idx: int, f, tags_array: list[str], node_tf_ids: list[int], ctx: ExtractionContext) -> dict:
    go_map, tf_map, _go_of_tf = build_scene_graph(f)
    node_set = set(node_tf_ids)
    objects = []
    for tf_id in node_tf_ids:
        tf = tf_map.get(tf_id)
        if tf is None:
            ctx.warnings.append(f"scene {scene_idx}: requested transform {tf_id} not found")
            continue
        go_id = tf["go_id"]
        go = go_map.get(go_id)
        if go is None:
            ctx.warnings.append(f"scene {scene_idx}: transform {tf_id} has no owning GameObject in-file, skipped")
            continue
        parent_tf = tf["father_id"]
        parent_go_id = None
        if parent_tf not in (0, -1) and parent_tf in node_set:
            parent_tf_rec = tf_map.get(parent_tf)
            if parent_tf_rec is not None:
                parent_go_id = parent_tf_rec["go_id"]
        tag_name, tag_ok = resolve_tag(go["tag_raw"], tags_array)
        if not tag_ok:
            ctx.warnings.append(
                f"scene {scene_idx}: GameObject {go_id} ({go['name']!r}) has unresolved tag index {go['tag_raw']}"
            )
        objects.append({
            "id": str(go_id),
            "name": go["name"],
            "active": go["active"],
            "layer": go["layer"],
            "tag": tag_name,
            "parent_id": str(parent_go_id) if parent_go_id is not None else None,
            "local_position": tf["pos"],
            "local_rotation": tf["rot"],
            "local_scale": tf["scale"],
            "components": build_components(f, go["components"], scene_idx, ctx),
        })
    objects.sort(key=lambda o: int(o["id"]))
    return {"scene_name": SCENE_REGISTRY[scene_idx], "objects": objects}


# --------------------------------------------------------------------------
# Dedup write pass (§C.2/§C.3/§C.4)
# --------------------------------------------------------------------------

def write_meshes(ctx: ExtractionContext, output: str) -> dict:
    out = {}
    for cid, (mesh_obj, basename) in sorted(ctx.mesh_work.items()):
        out_path = os.path.join(output, "meshes", f"{cid}.mesh.bin")
        res = write_mesh_bin(mesh_obj, basename, out_path)  # raises HardExtractError on m_MeshCompression != 0
        assert res.content_id == cid, f"mesh id mismatch: queued {cid} != computed {res.content_id}"
        if res.had_nonzero_topology:
            ctx.warnings.append(f"mesh {cid} ({res.source_object_name}): non-triangle submesh topology present")
        out[cid] = {
            "file": f"meshes/{cid}.mesh.bin",
            "source_object_name": res.source_object_name,
            "vertex_count": res.vertex_count,
            "submesh_count": res.submesh_count,
        }
    return out


def resolve_shader_name(mat_typed):
    sptr = mat_typed.m_Shader
    if sptr.path_id == 0:
        return None
    try:
        sh_obj = sptr.deref()
        sh_tt = sh_obj.read_typetree()
    except Exception:
        return None
    if sh_tt is None:
        return None
    pf = sh_tt.get("m_ParsedForm") or {}
    return pf.get("m_Name") or sh_tt.get("m_Name")


def write_materials(ctx: ExtractionContext, output: str) -> dict:
    out = {}
    materials_dir = os.path.join(output, "materials")
    for cid, (mat_obj, basename) in sorted(ctx.material_work.items()):
        tt = mat_obj.read_typetree()
        typed = mat_obj.read()
        shader_name = resolve_shader_name(typed)
        if not shader_name:
            raise HardExtractError(
                f"material {cid} (path_id={mat_obj.path_id} in {basename}, "
                f"name={tt.get('m_Name')!r}): UNRESOLVED shader -- §B.WP1 step 4 hard failure"
            )
        sp = tt.get("m_SavedProperties", {}) or {}
        tex_envs_tt = sp.get("m_TexEnvs", [])
        sp_typed = getattr(typed, "m_SavedProperties", None)
        tex_envs_typed = list(sp_typed.m_TexEnvs) if sp_typed is not None else []
        assert len(tex_envs_tt) == len(tex_envs_typed), (
            f"material {cid}: m_TexEnvs typetree/typed length mismatch "
            f"({len(tex_envs_tt)} vs {len(tex_envs_typed)})"
        )

        tex_envs_out = {}
        for (name_tt, te_tt), (_name_typed, te_typed) in zip(tex_envs_tt, tex_envs_typed):
            tref = te_tt.get("m_Texture", {})
            scale = te_tt.get("m_Scale", {"x": 1, "y": 1})
            offset = te_tt.get("m_Offset", {"x": 0, "y": 0})
            tex_id = None
            if tref.get("m_PathID", 0) and te_typed.m_Texture.path_id != 0:
                tex_obj = te_typed.m_Texture.deref()
                tex_basename = ctx.owning_basename(tex_obj)
                tex_id = content_id(tex_basename, tex_obj.path_id, "Texture2D")
                entry = ctx.texture_work.setdefault(
                    tex_id, {"obj": tex_obj, "basename": tex_basename, "prop_names": set()}
                )
                entry["prop_names"].add(name_tt)
            tex_envs_out[name_tt] = {
                "texture_id": tex_id,
                "scale": [scale.get("x", 1), scale.get("y", 1)],
                "offset": [offset.get("x", 0), offset.get("y", 0)],
            }

        floats_out = {n: v for n, v in sp.get("m_Floats", [])}
        colors_out = {
            n: [c.get("r", 0.0), c.get("g", 0.0), c.get("b", 0.0), c.get("a", 1.0)]
            for n, c in sp.get("m_Colors", [])
        }
        keywords = set(tt.get("m_ValidKeywords") or [])
        keywords.update((tt.get("m_ShaderKeywords") or "").split())

        mat_json = {
            "source_name": tt.get("m_Name", ""),
            "shader": shader_name,
            "render_queue": tt.get("m_CustomRenderQueue", -1),
            "keywords": sorted(keywords),
            "tex_envs": tex_envs_out,
            "floats": floats_out,
            "colors": colors_out,
        }
        with open(os.path.join(materials_dir, f"{cid}.json"), "w", encoding="utf-8") as fh:
            json.dump(mat_json, fh, indent=2, sort_keys=True, ensure_ascii=False)
        out[cid] = {"file": f"materials/{cid}.json", "source_name": mat_json["source_name"], "shader": shader_name}
    return out


def _is_normal_map_heuristic(src_name: str, prop_names: set) -> bool:
    lname = (src_name or "").lower()
    if lname.endswith(("_n", "_normal", "_nrm")):
        return True
    for p in prop_names:
        pl = p.lower()
        if "bump" in pl or "normal" in pl:
            return True
    return False


# impl-texture-dimensions: Cubemap face order assumption -- Unity's own documented "6 Frames"
# strip/cross layout convention (and every community tool that round-trips Cubemap assets:
# AssetStudio/UABEA) treats a Cubemap's 6 serialized faces, in raw storage order, as
# (+X, -X, +Y, -Y, +Z, -Z) -- the same order as the UnityEngine.CubemapFace enum
# (PositiveX=0..NegativeZ=5). Confirmed structurally this pass (not pixel-exact, which the spike
# doesn't require, SPEC §B.WP2.4): decoding all 6 of a real Equator-referenced Cubemap's faces
# (`patron_cubemap_metall`, resources.assets path_id=5546) each independently produced a full,
# non-degenerate 256x256 RGBA image (no garbage/misaligned bytes), and 6 * m_CompleteImageSize
# exactly equals the raw image_data blob length for every one of the 18 real Cubemaps this
# extractor now handles (verified for all of them, not just the one sampled above).
CUBE_FACE_ORDER = ["+X", "-X", "+Y", "-Y", "+Z", "-Z"]


def _decode_cubemap_faces(tex_obj, data, tt, width: int, height: int) -> list:
    """Decode all 6 faces of a Cubemap (top mip level only -- WP2 only ever needs one importable
    2D layout per texture id, §C.3) and return them as 6 PIL Images in CUBE_FACE_ORDER.

    Storage layout (confirmed empirically, see CUBE_FACE_ORDER's comment): image_data is 6
    consecutive faces, each `m_CompleteImageSize` bytes -- the FULL per-face mip chain (base level
    first) -- so the base/top mip for face i starts at `i * m_CompleteImageSize` and
    parse_image_data (the same decoder every other texture in this pipeline already uses) reads
    exactly the bytes it needs from the front of that slice, ignoring the smaller mips appended
    after it, exactly as it already does for a normal streamed Texture2D.
    """
    from UnityPy.export.Texture2DConverter import parse_image_data

    raw = bytes(data.get_image_data())
    face_size = tt.get("m_CompleteImageSize") or 0
    if not face_size or face_size * 6 != len(raw):
        # Defensive fallback -- not observed on any of Equator's 18 real Cubemaps (all satisfy
        # face_size*6==len(raw) exactly), but don't hard-fail a differently-authored source
        # Cubemap that happens to omit/misreport m_CompleteImageSize.
        if len(raw) % 6 != 0:
            raise ValueError(
                f"Cubemap image_data length {len(raw)} is not evenly divisible by 6 faces "
                f"(m_CompleteImageSize={tt.get('m_CompleteImageSize')})"
            )
        face_size = len(raw) // 6
    fmt = data.m_TextureFormat
    ver = getattr(tex_obj, "version", (0, 0, 0, 0))
    plat = getattr(tex_obj, "platform", 0)
    faces = []
    for i in range(6):
        face_bytes = raw[i * face_size:(i + 1) * face_size]
        img = parse_image_data(face_bytes, width, height, fmt, ver, plat, None, True)
        if img is None:
            raise ValueError(f"parse_image_data returned no image data for cubemap face {i} ({CUBE_FACE_ORDER[i]})")
        faces.append(img.convert("RGBA"))
    return faces


def write_textures(ctx: ExtractionContext, output: str) -> dict:
    out = {}
    textures_dir = os.path.join(output, "textures")
    for cid, entry in sorted(ctx.texture_work.items()):
        tex_obj = entry["obj"]
        prop_names = entry["prop_names"]
        tt = tex_obj.read_typetree()
        width, height = tt.get("m_Width", 0), tt.get("m_Height", 0)
        # Texture3D's typetree names the same concept "m_Format", not
        # "m_TextureFormat" (confirmed this pass on the real object below) --
        # fall back so volume textures don't silently report "FormatNone".
        fmt_code = tt.get("m_TextureFormat", tt.get("m_Format"))
        cs_code = tt.get("m_ColorSpace")
        src_name = tt.get("m_Name", "")
        is_normal = _is_normal_map_heuristic(src_name, prop_names)
        out_path = os.path.join(textures_dir, f"{cid}.png")
        is_volume = tex_obj.type.name == "Texture3D"
        is_cube = tex_obj.type.name == "Cubemap"
        # impl-texture-dimensions §1: record every texture's actual runtime shape so WP2 never
        # imports/assigns a Cubemap or Texture3D source as a plain 2D texture (the root cause of
        # the reported "Error assigning 2D texture to CUBE/3D texture property" scene-load errors
        # -- a Cubemap/Texture3D object decoded via the generic Texture2D path only ever yields
        # ONE face/slice's worth of pixels, silently, with no indication it came from a
        # multi-face/volume source). "Tex2DArray" is recorded defensively even though the census
        # (arena-census.md §2/§6, and this extractor's own full in-scope material scan this pass)
        # found none reachable from Equator's 5 content scenes -- WP2 must still handle it
        # gracefully (skip + log, same as Tex3D) rather than assume it can't occur.
        if is_cube:
            dimension = "Cube"
        elif is_volume:
            dimension = "Tex3D"
        elif tex_obj.type.name == "Texture2DArray":
            dimension = "Tex2DArray"
        else:
            dimension = "Tex2D"
        volume_depth = int(tt.get("m_Depth", 1)) or 1 if is_volume else 1
        native_volume = None
        try:
            data = tex_obj.read()
            if is_cube:
                # §1: decode all 6 faces (real texel data, not a placeholder/single-face crop)
                # and stitch them into one vertical 6-frame strip -- the layout WP2's
                # TextureImporter recognizes via TextureImporterGenerateCubemap.AutoCubemap
                # (§B.WP2.4/task item 2) to rebuild a real TextureCube-shaped asset, instead of
                # importing face 0 alone as an ordinary 2D texture.
                faces = _decode_cubemap_faces(tex_obj, data, tt, width, height)
                img = Image.new("RGBA", (width, height * 6))
                for i, face in enumerate(faces):
                    img.paste(face, (0, i * height))
            elif is_volume:
                # m_Format belongs to GraphicsFormat, not TextureFormat: source54
                # is Alpha8, not ASTC. Preserve native voxels and every source mip;
                # only the inspection PNG receives a per-slice display flip.
                native_volume, img = write_volume_texture(tex_obj, cid, output)
            else:
                img = data.image
                if img is None:
                    raise ValueError("UnityPy returned no image data")
            img = img.convert("RGBA")
            img.save(out_path)
        except Exception as e:
            ctx.errors.append(
                f"texture {cid} ({src_name!r}, path_id={tex_obj.path_id} in {entry['basename']}): "
                f"decode failed: {e} -- placeholder emitted (§B.WP1 edge case)"
            )
            Image.new("RGBA", (64, 64), (255, 0, 255, 255)).save(out_path)
        # Manifest width/height must match the SAVED PNG's actual pixel size
        # (validate_extract.py asserts this) -- for a flattened volume texture that is
        # width x (height*depth), for a cubemap strip that is width x (height*6), not the source
        # m_Width/m_Height alone.
        if is_volume:
            out_width, out_height = width, height * volume_depth
        elif is_cube:
            out_width, out_height = width, height * 6
        else:
            out_width, out_height = width, height
        entry_out = {
            "file": f"textures/{cid}.png",
            "source_name": src_name,
            "width": out_width,
            "height": out_height,
            "format_src": "GraphicsFormat54_Alpha8" if native_volume else texture_format_name(fmt_code),
            "color_space": COLOR_SPACE.get(cs_code, f"Unknown({cs_code})"),
            "is_normal_map": is_normal,
            # impl-texture-dimensions §1: §C-compatible extension -- "Tex2D" (default, matches
            # every pre-existing package that predates this field), "Cube", "Tex3D", or
            # "Tex2DArray". A reader that doesn't know this key can keep treating every texture as
            # a plain 2D PNG exactly as before; WP2's importer is the one consumer that now
            # branches on it (task item 2).
            "dimension": dimension,
        }
        if is_cube:
            entry_out["cube_layout"] = "vertical_strip"
            entry_out["cube_face_order"] = CUBE_FACE_ORDER
            entry_out["cube_face_width"] = width
            entry_out["cube_face_height"] = height
            ctx.warnings.append(
                f"texture {cid} ({src_name!r}): Cubemap ({width}x{height} per face) exported as a "
                f"{out_width}x{out_height} vertical 6-frame strip (real decoded data for all 6 "
                f"faces, not a placeholder) -- WP2 imports it as a real TextureCube-shaped asset "
                f"via TextureImporterGenerateCubemap.AutoCubemap, never as a plain 2D texture."
            )
        if is_volume:
            entry_out["is_volume_texture"] = True
            entry_out["volume_source_depth"] = volume_depth
            entry_out["volume_source_height"] = height
            if native_volume is not None:
                entry_out["native_volume"] = native_volume
            ctx.warnings.append(
                f"texture {cid} ({src_name!r}): Texture3D (depth={volume_depth}); native_volume "
                f"contains validated, unchanged Alpha8 voxels and source mip/sampler metadata. "
                f"The {out_width}x{out_height} PNG is inspection only; Unity imports the native "
                f"payload as Texture3D. Failed validation remains an extraction error."
            )
        out[cid] = entry_out
    return out


# --------------------------------------------------------------------------
# navmesh_ref (§C.8) -- static copy of Arena's own bake settings, reference only
# --------------------------------------------------------------------------

def write_navmesh_ref(output: str):
    data = {
        "note": (
            "Arena's OWN NavMesh bake settings for Equator, reference only -- "
            "NOT consumed by WP2's bake (decision #6, §B.WP2.7 bakes fresh "
            "for SPT's agent-0 values). Source: analysis/arena-census.md §10."
        ),
        "agent_type_id": 287145453,
        "agent_radius": 0.28,
        "agent_height": 1.60,
        "agent_slope": 48,
        "agent_climb": 0.38,
        "cell_size": 0.07,
        "tile_size": 256,
        "source_bounds_center": [0.0, 2.0, 0.0],
        "source_bounds_extent": [71.65, 10.05, 86.35],
        "tile_count": 41,
    }
    with open(os.path.join(output, "navmesh_ref", "equator_source_navmesh.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, sort_keys=True, ensure_ascii=False)


def ensure_dirs(output: str):
    for d in ("meshes", "textures", "materials", "scenes", "navmesh_ref"):
        os.makedirs(os.path.join(output, d), exist_ok=True)


def utcnow_iso() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# --------------------------------------------------------------------------
# census-only mode
# --------------------------------------------------------------------------

def run_census_only(env, scene_indices: list[int], exact_paths: dict):
    for idx in scene_indices:
        f = env.files.get(exact_paths[idx]["level"])
        if f is None:
            print(f"{SCENE_REGISTRY[idx]} (level{idx}): FILE NOT LOADED", file=sys.stderr)
            continue
        counts = Counter(obj.type.name for obj in f.objects.values())
        print(f"{SCENE_REGISTRY[idx]} (level{idx}): {dict(counts.most_common())}")
        saf = env.files.get(exact_paths[idx]["sharedassets"])
        if saf is not None:
            sa_counts = Counter(obj.type.name for obj in saf.objects.values())
            print(f"  sharedassets{idx}.assets: {dict(sa_counts.most_common())}")


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------

def parse_slice_roots(entries: list[str]) -> dict[int, list[int]]:
    slice_map: dict[int, list[int]] = {}
    for entry in entries:
        if ":" not in entry:
            print(f"ERROR: --slice-roots entry {entry!r} must be SCENE_IDX:id1,id2,...", file=sys.stderr)
            sys.exit(2)
        scene_str, ids_str = entry.split(":", 1)
        idx = int(scene_str)
        ids = [int(x) for x in ids_str.split(",") if x.strip()]
        slice_map.setdefault(idx, []).extend(ids)
    return slice_map


def main():
    parser = argparse.ArgumentParser(description="ArenaMapPort WP1 extractor (docs/SPEC.md §B.WP1/§C)")
    parser.add_argument("--source", default=DEFAULT_SOURCE, help="Arena EscapeFromTarkovArena_Data dir (read-only)")
    parser.add_argument("--target", default="D:/SPT41/EscapeFromTarkov_Data", help="SPT data directory for native material mapping (read-only)")
    parser.add_argument("--native-features", action="store_true", help="restore original material and environmental audio data; requires all five full scenes")
    parser.add_argument("--scenes", default="all5", help="comma-separated scene indices from {51,53,55,57,58}, or 'all5'")
    parser.add_argument(
        "--slice-roots", action="append", default=[],
        help='SCENE_IDX:id1,id2,... (Transform OR GameObject path_id) -- restrict that scene to these '
             "root subtrees. Repeatable. A scene index with no --slice-roots entry is extracted in full.",
    )
    parser.add_argument("--output", default=None, help="package output directory (required unless --census-only)")
    parser.add_argument("--census-only", action="store_true", help="print per-scene object counts, write nothing")
    parser.add_argument(
        "--fixed-timestamp", default=None,
        help="override manifest.json's source.extracted_utc (ISO8601) -- for a true byte-identical "
             "determinism check across two runs; omit for a real wall-clock timestamp",
    )
    args = parser.parse_args()

    if args.scenes.strip().lower() == "all5":
        scene_indices = list(ALL5)
    else:
        scene_indices = [int(x) for x in args.scenes.split(",") if x.strip()]
    for idx in scene_indices:
        if idx not in SCENE_REGISTRY:
            print(f"ERROR: scene index {idx} is not in the §E.1 content-scene set {sorted(SCENE_REGISTRY)}", file=sys.stderr)
            sys.exit(2)

    slice_map = parse_slice_roots(args.slice_roots)
    for idx in slice_map:
        if idx not in scene_indices:
            print(f"ERROR: --slice-roots given for scene {idx} which is not in --scenes {scene_indices}", file=sys.stderr)
            sys.exit(2)

    if args.native_features and (slice_map or sorted(scene_indices) != sorted(ALL5)):
        parser.error("--native-features requires all five full Equator scenes without --slice-roots")

    print(f"Loading environment for scenes {scene_indices} from {args.source} ...", file=sys.stderr)
    tags_array = load_tags(args.source)
    env, exact_paths = load_environment(args.source, scene_indices)
    print(f"Loaded {len(env.files)} files.", file=sys.stderr)

    if args.census_only:
        run_census_only(env, scene_indices, exact_paths)
        return

    if not args.output:
        print("ERROR: --output is required unless --census-only", file=sys.stderr)
        sys.exit(2)

    ctx = ExtractionContext(env=env)
    ensure_dirs(args.output)
    ballistic_contract = None
    if args.native_features:
        from extract_ballistics import load_contract, augment_scene
        ballistic_contract = load_contract(args.source, args.target)
        # PLAN-TWO-MAPS item 4: NavMesh modifier/volume components (no cross-version contract needed)
        from extract_navmesh_modifiers import augment_scene as augment_navmesh, read_navmesh_areas

    light_culling_totals = {"culling_light_object_count": 0, "volumetric_light_count": 0, "light_flicker_count": 0}
    decal_count = 0
    scenes_out: dict[int, dict] = {}
    for idx in scene_indices:
        f = env.files[exact_paths[idx]["level"]]
        roots = slice_map.get(idx)
        if roots:
            _go_map, tf_map, go_of_tf = build_scene_graph(f)
            node_ids: set[int] = set()
            for r in roots:
                rr = r
                if rr not in tf_map:
                    rr = go_of_tf.get(r)
                    if rr is None:
                        print(f"ERROR: --slice-roots id {r} for scene {idx} is not a known Transform or GameObject id", file=sys.stderr)
                        sys.exit(2)
                node_ids.update(subtree_ids(tf_map, rr))
            node_id_list = sorted(node_ids)
            print(f"  scene {idx} ({SCENE_REGISTRY[idx]}): slice of {len(node_id_list)} objects from {len(roots)} root(s)", file=sys.stderr)
        else:
            _go_map, tf_map, _go_of_tf = build_scene_graph(f)
            node_id_list = list(tf_map.keys())
            print(f"  scene {idx} ({SCENE_REGISTRY[idx]}): FULL scene, {len(node_id_list)} objects", file=sys.stderr)
        scenes_out[idx] = extract_scene(idx, f, tags_array, node_id_list, ctx)
        if ballistic_contract is not None:
            count = augment_scene(scenes_out[idx], f, ballistic_contract)
            print(f"    restored {count} original ballistic material components", file=sys.stderr)
            # PLAN-TWO-MAPS item 4
            nm_mod, nm_vol = augment_navmesh(scenes_out[idx], f)
            print(f"    restored {nm_mod} NavMeshModifier + {nm_vol} NavMeshModifierVolume component(s)", file=sys.stderr)
        if args.native_features:
            from extract_decals import augment_scene as augment_decals
            decal_count += augment_decals(scenes_out[idx], f, ctx)
            # PLAN-TWO-MAPS item 5: tag SmallPhysicsObject owners (no contract needed)
            physics_count = physics_props.augment_scene(scenes_out[idx], f)
            print(f"    tagged {physics_count} SmallPhysicsObject owners", file=sys.stderr)
            # PLAN-TWO-MAPS follow-up: tag CullingLightObject/VolumetricLight/LightFlicker owners
            lc_counts = light_culling.augment_scene(scenes_out[idx], f)
            for key, value in lc_counts.items():
                light_culling_totals[key] += value
            print(f"    tagged {lc_counts['culling_light_object_count']} CullingLightObject + "
                  f"{lc_counts['volumetric_light_count']} VolumetricLight + "
                  f"{lc_counts['light_flicker_count']} LightFlicker owner(s)", file=sys.stderr)

    try:
        print(f"Writing {len(ctx.mesh_work)} meshes...", file=sys.stderr)
        meshes_manifest = write_meshes(ctx, args.output)
        print(f"Writing {len(ctx.material_work)} materials...", file=sys.stderr)
        materials_manifest = write_materials(ctx, args.output)
        print(f"Writing {len(ctx.texture_work)} textures...", file=sys.stderr)
        textures_manifest = write_textures(ctx, args.output)
    except HardExtractError as e:
        print(f"EXTRACTION FAILED (hard error, §B.WP1): {e}", file=sys.stderr)
        sys.exit(2)

    write_navmesh_ref(args.output)

    for idx, scene_json in scenes_out.items():
        path = os.path.join(args.output, "scenes", f"{scene_json['scene_name']}.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(scene_json, f, indent=2, sort_keys=True, ensure_ascii=False)

    print("Computing spawn/zone anchor reference block...", file=sys.stderr)
    scene_dict_for_anchors = {idx: arena_common.EQUATOR[idx] for idx in scene_indices}
    anchors = compute_spawn_anchors(env, scene_dict_for_anchors, args.source, ctx.warnings)

    manifest = {
        "schema_version": SCHEMA_VERSION,
        "source": {
            "arena_data_root": args.source,
            "extractor_script": "tools/extract/extract_equator.py",
            "extracted_utc": args.fixed_timestamp or utcnow_iso(),
        },
        "scenes": [SCENE_REGISTRY[idx] for idx in scene_indices],
        "slice": {str(idx): sorted(slice_map[idx]) for idx in slice_map},
        "meshes": meshes_manifest,
        "textures": textures_manifest,
        "materials": materials_manifest,
        "anchors_reference": anchors,
        "errors": ctx.errors,
        "warnings": ctx.warnings,
    }
    if args.native_features:
        from extract_bay5_audio import export as export_audio
        manifest["source"].update(target_data_root=args.target, map_key="arena_equator")
        manifest["native_ballistics"] = ballistic_contract
        manifest["native_audio"] = export_audio(args.source, args.output, map_key="arena_equator")
        manifest["native_features"] = {"schema_version": 1, "required": ["ballistics", "ambient_audio"]}
        manifest["native_features"]["navmesh_areas"] = read_navmesh_areas(env, args.source)  # PLAN-TWO-MAPS item 4
        # PLAN-TWO-MAPS item 6 -- rain-cover geometry (DryPlane/IndoorTrigger) from the Scripts scene.
        from extract_raincover import export as export_raincover
        manifest["native_features"]["raincover"] = export_raincover(args.source, args.output, "arena_equator")
        # PLAN-TWO-MAPS follow-up -- CullingLightObject/VolumetricLight/LightFlicker, summed across
        # all scenes by the per-scene augment_scene() calls above.
        manifest["native_features"]["light_culling"] = {
            "schema_version": light_culling.SCHEMA_VERSION, **light_culling_totals,
        }
        manifest["native_features"]["static_decals"] = {"schema_version": 1, "count": decal_count}
    with open(os.path.join(args.output, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, sort_keys=True, ensure_ascii=False)

    total_objects = sum(len(s["objects"]) for s in scenes_out.values())
    print(
        f"DONE. {total_objects} objects, {len(meshes_manifest)} meshes, "
        f"{len(materials_manifest)} materials, {len(textures_manifest)} textures. "
        f"{len(ctx.errors)} errors, {len(ctx.warnings)} warnings.",
        file=sys.stderr,
    )
    if ctx.errors:
        print(f"WARNING: manifest.json.errors is non-empty ({len(ctx.errors)}) -- this run fails WP6's acceptance gate.", file=sys.stderr)


if __name__ == "__main__":
    main()
