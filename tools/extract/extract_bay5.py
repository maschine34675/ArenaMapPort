#!/usr/bin/env python3
"""ArenaMapPort Bay5 (TeamFight) extractor.

Reuses tools/extract/extract_equator.py's proven extraction engine (scene-graph walk,
mesh/material/texture dedup+write, spawn-anchor computation, the exhaustive
COMPONENT_BUILDERS allowlist -- critic amendment #4: Water/impostor/gameplay MonoBehaviours
are already dropped by that allowlist, no new skip logic needed here) by importing it as a
library and monkey-patching its scene-selection global (SCENE_REGISTRY) for the duration of
this run -- the exact same technique extract_equator.py's own compute_spawn_anchors() already
uses on census.BASE. extract_equator.py ITSELF IS NOT EDITED by this (a module-attribute
reassignment on the imported module object, not a file change) -- kept fully untouched so
Equator's byte-reproducibility guarantee is never put at risk by this file
(docs/progress/multimap-generalize.md open item #3 deferred generalizing the extractor to
"whenever Bay5 is actually extracted" -- this is that pass).

Scene set: 9 always-loaded / TeamFight-mode Bay5 scenes --
Main(84), DEPOT(80), WAREHOUSE(86), HANGAR(82), DesignStuff(81), TeamFight_DesignStuff(88),
TeamFight(89), Light(83), Background(79). The original background is decorative; the map
descriptor excludes its renderers from navigation and gameplay cover bounds, while original
colliders remain navigation sources within the gameplay bake volume. Excluded:
Scripts(85)/Scripts_Winter(247) (authored fresh in Unity, never extracted -- Equator
precedent), TeamFight_AI(78) (fresh bot support is authored for SPT), Winter
geometry (confirmed absent from the TeamFight scene set), TeamFight_Culling(90) (PerfectCulling
data, out of scope). Source-backed ballistic materials are added after the geometry pass.
Eligible Sound(188) ambient players and their clip/spline graph are separately exported and
authored under the existing Scripts scene; Arena match/event audio remains excluded.

Duplicate-BuildSettings-index resolver (critic amendment: "the resolver must prefer the full
copy and never silently take the last match" -- Bay5's own BuildSettings.scenes lists
TeamFight_AI at BOTH idx 78 and 222, and Sound at BOTH idx 188 and 225, same
repeated-basename-different-full-path-list trap the WP1 brief originally warned about for
Equator): resolve_scene_indices() below reads BuildSettings.scenes fresh from
globalgamemanagers, groups by EXACT full asset path (never basename), and for any selected
target scenes that turns out to have more than one BuildSettings entry, prefers the index
whose own sharedassets<N>.assets is the LARGER file (the full, non-stripped copy -- confirmed
this is what distinguishes the real entry from the empty duplicate for both known pairs, see
scratchpad bay5's 05_investigate_duplicate_indices.py). None of the selected targets has a
duplicate (verified below via an exact-match assertion against the pre-verified expected
index) -- this still runs the general probe-and-prefer logic so the same function is safe to
reuse unmodified if a future pass ever adds TeamFight_AI or Sound to the extracted set.

External file dependencies: Bay5 has no analysis/scene_externals.json precomputed entry (that
file is Equator/AutoService-only, and analysis/ is read-only per docs/SPEC.md) -- this script
computes each selected scene's external file dependencies directly via UnityPy's own
SerializedFile.externals (compute_scene_externals() below). The background joins the same
deduplicated mesh/material/texture pool as the playable scenes.

CLI:
    python extract_bay5.py --output <package dir> [--source <ArenaData dir>]
    python extract_bay5.py --census-only   (fast, no writes -- per-scene object-type counts)
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

import extract_equator as EQ  # noqa: E402 -- the proven engine, reused unmodified (see module docstring)
import extract_physics_props as physics_props  # PLAN-TWO-MAPS item 5  # noqa: E402
import extract_light_culling as light_culling  # PLAN-TWO-MAPS follow-up  # noqa: E402

SCHEMA_VERSION = 1
DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"

# Selected scenes -- BuildSettings idx -> full Unity scene
# name. Order here is the manifest's "scenes" array order (cosmetic -- SceneAssembler.cs just
# iterates it; the load-bearing rcid ORDER for raid boot lives in tools/maps/bay5.json's
# all_scene_names, consumed by patch_preset.py, independently of this file).
BAY5_SCENE_NAMES: dict[int, str] = {
    84: "Arena_Bay5_Main",
    83: "Arena_Bay5_Light",
    81: "Arena_Bay5_DesignStuff",
    89: "Arena_Bay5_TeamFight",
    88: "Arena_Bay5_TeamFight_DesignStuff",
    80: "Arena_Bay5_DEPOT",
    86: "Arena_Bay5_WAREHOUSE",
    82: "Arena_Bay5_HANGAR",
    79: "Arena_Bay5_Background",
}
ALL_SCENES = list(BAY5_SCENE_NAMES.keys())

# Static alias, same shape/semantics as extract_equator.SCENE_REGISTRY (idx -> full scene name).
# tools/validate/validate_extract.py's disabled-count/reproducibility checks import whichever
# module manifest.json.source.extractor_script names and read ITS OWN .SCENE_REGISTRY generically
# -- exposing this as a plain module-level attribute (not just resolve_scene_indices()'s local
# return value) is what makes that generalization work for a Bay5 package (critic amendment #3).
SCENE_REGISTRY: dict[int, str] = dict(BAY5_SCENE_NAMES)


def resolve_scene_indices(source: str) -> dict[int, str]:
    """See module docstring's "Duplicate-BuildSettings-index resolver" section."""
    gm_path = f"{source}/globalgamemanagers"
    if not os.path.exists(gm_path):
        gm_path = f"{source}/globalgamemanagers.assets"
    env = UnityPy.load(gm_path)
    scenes: list[str] = []
    for obj in env.objects:
        if obj.type.name == "BuildSettings":
            scenes = obj.read_typetree().get("scenes") or []
            break
    if not scenes:
        raise RuntimeError(f"no BuildSettings.scenes found in {gm_path}")

    by_path: dict[str, list[int]] = {}
    for i, s in enumerate(scenes):
        by_path.setdefault(s, []).append(i)

    resolved: dict[int, str] = {}
    for want_idx, want_name in BAY5_SCENE_NAMES.items():
        full_path = f"Assets/Content/Locations/Arena_Bay5/{want_name}.unity"
        candidates = by_path.get(full_path, [])
        if not candidates:
            raise RuntimeError(f"scene {want_name!r} ({full_path}) not found anywhere in BuildSettings.scenes")
        if len(candidates) == 1:
            chosen = candidates[0]
        else:
            def sa_size(idx: int) -> int:
                p = f"{source}/sharedassets{idx}.assets"
                return os.path.getsize(p) if os.path.exists(p) else -1

            chosen = max(candidates, key=sa_size)
            print(
                f"[extract_bay5] {want_name}: {len(candidates)} BuildSettings entries {candidates} "
                f"(duplicate path, same as the known TeamFight_AI/Sound pattern) -- chose idx {chosen} "
                f"(largest sharedassets<N>.assets: the full, non-stripped copy)",
                file=sys.stderr,
            )
        if chosen != want_idx:
            raise RuntimeError(
                f"resolver picked idx {chosen} for {want_name!r}, but the pre-verified expected index "
                f"is {want_idx} (scratchpad bay5/resolved_indices.json) -- investigate before proceeding, "
                f"do not silently trust either value"
            )
        resolved[chosen] = want_name
    return resolved


def compute_scene_externals(source: str, scene_indices: list[int]) -> list[str]:
    """See module docstring's "External file dependencies" section."""
    names: set[str] = set()
    for idx in scene_indices:
        lvl = f"{source}/level{idx}"
        env = UnityPy.load(lvl)
        for f in env.files.values():
            if not hasattr(f, "externals"):
                continue
            for ext in f.externals:
                p = str(ext.path)
                if not p or "unity default resources" in p.lower() or "unity_builtin_extra" in p.lower():
                    continue  # built-in engine resource, not a file on disk
                names.add(p)
    return sorted(names)


def load_environment_bay5(source: str, scene_indices: list[int]):
    paths: list[str] = []
    seen: set[str] = set()

    def add(p: str) -> None:
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

    print("Resolving external file dependencies (per-scene UnityPy scan, no precomputed table available)...", file=sys.stderr)
    externals = compute_scene_externals(source, scene_indices)
    print(f"  {len(externals)} unique external file(s)", file=sys.stderr)
    for name in externals:
        add(f"{source}/{name}")

    for p in paths:
        if not os.path.exists(p):
            print(f"WARNING: expected input file missing, skipping: {p}", file=sys.stderr)
    paths = [p for p in paths if os.path.exists(p)]

    env = UnityPy.load(*paths)
    return env, exact_paths


def run_census_only(env, scene_indices: list[int], exact_paths: dict, registry: dict[int, str]) -> None:
    for idx in scene_indices:
        f = env.files.get(exact_paths[idx]["level"])
        if f is None:
            print(f"{registry[idx]} (level{idx}): FILE NOT LOADED", file=sys.stderr)
            continue
        counts = Counter(obj.type.name for obj in f.objects.values())
        print(f"{registry[idx]} (level{idx}): {dict(counts.most_common())}")


def write_navmesh_ref_bay5(output: str) -> None:
    """Bay5's OWN source NavMesh bake reference (sharedassets78.assets / Arena_Bay5_TeamFight_AI,
    which is EXCLUDED from this extraction) -- reference only, NOT consumed by the fresh bake
    (BakeNavMesh.Bake uses SPT's own agent-0 values, same decision already made for Equator).
    Real numbers from this session's own Bay5 census (scratchpad bay5/bay5_census.json's
    "navmesh" section), not copy-pasted from Equator's unrelated values."""
    data = {
        "note": (
            "Arena's OWN NavMesh bake settings for Bay5, read from Arena_Bay5_TeamFight_AI "
            "(idx78 / sharedassets78.assets), which is EXCLUDED from this package -- reference "
            "only, NOT consumed by the fresh bake (same decision as Equator: bake fresh for "
            "SPT's agent-0 values, do not port Arena's own baked NavMeshData)."
        ),
        "source_scene": "Arena_Bay5_TeamFight_AI (excluded from this package)",
        "tile_count": 121,
        "agent_type_id": 0,
        "agent_radius": 0.30000001192092896,
        "agent_height": 1.7000000476837158,
        "agent_slope": 48.0,
        "agent_climb": 0.3799999952316284,
        "cell_size": 0.07000000029802322,
        "tile_size": 256,
        "source_bounds_center": [62.93000030517578, 4.0, 35.0],
        "source_bounds_extent": [90.0, 40.0, 90.0],
    }
    with open(os.path.join(output, "navmesh_ref", "bay5_source_navmesh.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, sort_keys=True, ensure_ascii=False)


def main() -> None:
    parser = argparse.ArgumentParser(description="ArenaMapPort Bay5 (TeamFight) extractor")
    parser.add_argument("--source", default=DEFAULT_SOURCE, help="Arena EscapeFromTarkovArena_Data dir (read-only)")
    parser.add_argument("--target", default="D:/SPT41/EscapeFromTarkov_Data",
                        help="SPT EscapeFromTarkov_Data for named ballistic preset mapping (read-only)")
    parser.add_argument("--output", default=None, help="package output directory (required unless --census-only)")
    parser.add_argument("--census-only", action="store_true", help="print per-scene object counts, write nothing")
    parser.add_argument(
        "--fixed-timestamp", default=None,
        help="override manifest.json's source.extracted_utc (ISO8601) -- for a determinism check across runs",
    )
    args = parser.parse_args()

    print("Resolving scene BuildSettings indices (duplicate-safe)...", file=sys.stderr)
    registry = resolve_scene_indices(args.source)
    scene_indices = ALL_SCENES
    assert set(registry.keys()) == set(scene_indices), f"resolver mismatch: {sorted(registry.keys())} vs {sorted(scene_indices)}"
    for idx in scene_indices:
        print(f"  idx {idx} -> {registry[idx]}", file=sys.stderr)

    # Monkey-patch extract_equator's scene-selection global for the duration of this run (see
    # module docstring) -- extract_equator.py's own source file is NOT modified by this.
    EQ.SCENE_REGISTRY = registry

    print(f"Loading environment for scenes {scene_indices} from {args.source} ...", file=sys.stderr)
    tags_array = EQ.load_tags()
    env, exact_paths = load_environment_bay5(args.source, scene_indices)
    print(f"Loaded {len(env.files)} files.", file=sys.stderr)

    if args.census_only:
        run_census_only(env, scene_indices, exact_paths, registry)
        return

    if not args.output:
        print("ERROR: --output is required unless --census-only", file=sys.stderr)
        sys.exit(2)

    from extract_ballistics import load_contract, augment_scene
    from extract_bay5_audio import export as export_audio
    ballistic_contract = load_contract(args.source, args.target)
    # PLAN-TWO-MAPS item 4: NavMesh modifier/volume components (no cross-version contract needed)
    from extract_navmesh_modifiers import augment_scene as augment_navmesh, read_navmesh_areas
    ctx = EQ.ExtractionContext(env=env)
    EQ.ensure_dirs(args.output)

    light_culling_totals = {"culling_light_object_count": 0, "volumetric_light_count": 0, "light_flicker_count": 0}
    decal_count = 0
    scenes_out: dict[int, dict] = {}
    for idx in scene_indices:
        f = env.files[exact_paths[idx]["level"]]
        _go_map, tf_map, _go_of_tf = EQ.build_scene_graph(f)
        node_id_list = list(tf_map.keys())
        print(f"  scene {idx} ({registry[idx]}): FULL scene, {len(node_id_list)} objects", file=sys.stderr)
        scenes_out[idx] = EQ.extract_scene(idx, f, tags_array, node_id_list, ctx)
        from extract_decals import augment_scene as augment_decals
        decal_count += augment_decals(scenes_out[idx], f, ctx)
        count = augment_scene(scenes_out[idx], f, ballistic_contract)
        print(f"    restored {count} original ballistic material components", file=sys.stderr)
        # PLAN-TWO-MAPS item 4
        nm_mod, nm_vol = augment_navmesh(scenes_out[idx], f)
        print(f"    restored {nm_mod} NavMeshModifier + {nm_vol} NavMeshModifierVolume component(s)", file=sys.stderr)
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
        meshes_manifest = EQ.write_meshes(ctx, args.output)
        print(f"Writing {len(ctx.material_work)} materials...", file=sys.stderr)
        materials_manifest = EQ.write_materials(ctx, args.output)
        print(f"Writing {len(ctx.texture_work)} textures...", file=sys.stderr)
        textures_manifest = EQ.write_textures(ctx, args.output)
    except EQ.HardExtractError as e:
        print(f"EXTRACTION FAILED (hard error): {e}", file=sys.stderr)
        sys.exit(2)

    write_navmesh_ref_bay5(args.output)

    for idx, scene_json in scenes_out.items():
        path = os.path.join(args.output, "scenes", f"{scene_json['scene_name']}.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(scene_json, f, indent=2, sort_keys=True, ensure_ascii=False)

    print("Computing spawn/zone anchor reference block...", file=sys.stderr)
    scene_dict_for_anchors = {idx: registry[idx] for idx in scene_indices}
    anchors = EQ.compute_spawn_anchors(env, scene_dict_for_anchors, args.source, ctx.warnings)

    # Critic amendment #6: assert the SpawnPointMarker anchors resolve from the IMPORTED scenes,
    # non-empty -- not a silent empty-and-fallback-to-near-origin like the "previous failure mode"
    # this amendment warns about. Ground truth (verified this session via a direct per-file
    # breakdown, scratchpad bay5): the source game has 30 SpawnPointMarker objects total, but only
    # 20 of them live in Arena_Bay5_TeamFight_DesignStuff (level88, IMPORTED here); the other 10
    # ("p1 (N)"-named) live in Arena_Bay5_TeamFight_AI (level78), which is excluded from this
    # extraction (bots/AI out of scope for both maps, docs/progress/multimap-generalize.md) -- so
    # this check expects exactly the 20 level88 anchors, not 30, and fails loudly if even that
    # smaller set comes back empty (which would mean the level88 import itself is broken, not just
    # the known/expected TeamFight_AI exclusion).
    marker_entry = anchors.get("EFT.Game.Spawning.SpawnPointMarker")
    if not marker_entry or not marker_entry.get("sample_positions"):
        print(
            "FATAL: anchors_reference['EFT.Game.Spawning.SpawnPointMarker'] is empty after "
            "extraction -- AuthorScriptsScene.ResolveSpawnPositions would silently fall back to "
            "near-origin placeholder positions (exactly the 'previous failure mode' critic "
            "amendment #6 warns about). Refusing to write a manifest that would cause that "
            "silently -- investigate scene 88 / level88's inclusion before retrying.",
            file=sys.stderr,
        )
        sys.exit(2)
    if marker_entry["count"] != 20 or set(marker_entry["sample_files"]) - {"level88"}:
        print(
            f"WARNING: expected exactly 20 SpawnPointMarker objects all from level88, got "
            f"count={marker_entry['count']} sample_files={marker_entry['sample_files']} -- "
            f"not fatal (positions are still usable) but the known-good baseline shifted, "
            f"re-verify against a fresh scratchpad bay5 per-file breakdown before trusting this.",
            file=sys.stderr,
        )
    print(
        f"  OK: {marker_entry['count']} SpawnPointMarker anchor(s) resolved from "
        f"{marker_entry['sample_files']} -- {len(marker_entry['sample_positions'])} sample position(s) available "
        f"for AuthorScriptsScene.ResolveSpawnPositions",
        file=sys.stderr,
    )

    native_audio = export_audio(args.source, args.output)
    manifest = {
        "schema_version": SCHEMA_VERSION,
        "source": {
            "arena_data_root": args.source,
            "target_data_root": args.target,
            "extractor_script": "tools/extract/extract_bay5.py",
            "extracted_utc": args.fixed_timestamp or EQ.utcnow_iso(),
        },
        "scenes": [registry[idx] for idx in scene_indices],
        "slice": {},
        "meshes": meshes_manifest,
        "textures": textures_manifest,
        "materials": materials_manifest,
        "anchors_reference": anchors,
        "errors": ctx.errors,
        "warnings": ctx.warnings,
        "native_ballistics": ballistic_contract,
        "native_audio": native_audio,
        "native_features": {"schema_version": 1, "required": ["ballistics", "harbor_audio"],
                            "navmesh_areas": read_navmesh_areas(env, args.source)},  # PLAN-TWO-MAPS item 4
    }
    # PLAN-TWO-MAPS item 6 -- rain-cover geometry (DryPlane/IndoorTrigger) from the Scripts scene.
    from extract_raincover import export as export_raincover
    manifest["native_features"]["raincover"] = export_raincover(args.source, args.output, "arena_bay5")
    # PLAN-TWO-MAPS follow-up -- CullingLightObject/VolumetricLight/LightFlicker, summed across all
    # scenes by the per-scene augment_scene() calls above.
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
        print(f"WARNING: manifest.json.errors is non-empty ({len(ctx.errors)}) -- investigate before building.", file=sys.stderr)


if __name__ == "__main__":
    main()
