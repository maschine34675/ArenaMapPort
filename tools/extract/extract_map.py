#!/usr/bin/env python3
"""Extract one reviewed Arena source plan using the unchanged Equator asset engine.

Usage: python tools/extract/extract_map.py --plan tools/extract/plans/airpit.json
       --output <package-dir> [--source <EscapeFromTarkovArena_Data>]

Native features (original ballistic materials, NavMesh modifiers, static decals, light
culling, rain cover; PLAN-AIRPIT item 2) are opt-in and live in native_features.py:
       ... --native-features --target <SPT EscapeFromTarkov_Data>
The plain invocation is unchanged and writes no native block. The accepted AirPit package
(Arena 0.6.0, PLAN-AIRPIT item 5) is the native form written to work/extract/airpit-native-v8 (v8 = v7 + the whole
recoverable level38/37 decoration: twin descendants, exclusions and the wider decoration box; v7 = v6 + the
furnished-shop partial scenes; v6 = v5 + native ambient audio, item 13);
the superseded 0.5.4 spike package work/extract/airpit is never an --output.

A plan scene record with ``select`` is a *partial scene* (scene_select.py): only the selected
hierarchy of that level is written, the native augmenters see just those owners, and the
manifest gains ``partial_scenes`` (``slice`` stays empty). Full scenes are processed exactly as
before, in plan order; a partial scene always follows the full scene its twins are skipped against.
The block lists the selection counters (``excluded``, ``exclude_entries``, ``twin_descendants_added`` and the
sorted ``twin_descendant_paths`` next to the per-rule counters); twin carriers are written as bare GameObjects
and, when twin descendants were imported, listed in ``twin_carrier_ids``.
"""
from __future__ import annotations

import argparse
from collections import Counter, deque
from contextlib import contextmanager
import json
from pathlib import Path
import sys

import UnityPy

import extract_equator as EQ
import native_features as NF
import scene_select as SS
from source_plan import (PlanError, finite_vector, load_plan, partial_scene_records, plan_relative_path,
                         provenance, read_build_scenes, require_file, unique_records, validate_anchors,
                         verify_scene_mapping)

DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"


def external_path(source: Path, raw: str) -> Path | None:
    text = raw.replace("\\", "/")
    builtin = text.rsplit("/", 1)[-1].lower()
    if builtin in ("unity default resources", "unity_builtin_extra"):
        # These are actual readable files in this standalone Arena installation.
        # Loading them explicitly also keeps pointer resolution from searching the
        # repository cwd when an imported collider/renderer uses a built-in asset.
        return require_file(source / "Resources" / builtin)
    # Standalone player externals are source-root-relative filenames. Do not silently
    # interpret a bundle archive URL or a foreign installation's absolute path.
    if not text or text.startswith("archive:"):
        raise PlanError(f"unsupported source external reference: {raw!r}")
    path = (source / text).resolve()
    if not path.is_relative_to(source.resolve()):
        raise PlanError(f"external reference escapes source root: {raw!r}")
    return require_file(path)


def load_environment(source: Path, plan: dict):
    """Load the explicit scenes plus the transitive serialized external-file closure.

    UnityPy keeps individual readers and object metadata; never read the entire Arena
    directory or eagerly decode the textures belonging to unreferenced objects.
    """
    pending = deque([source / "globalgamemanagers.assets", source / "resources.assets"])
    if (source / "globalgamemanagers").is_file():
        pending.append(source / "globalgamemanagers")
    exact = {}
    for record in unique_records(plan):
        idx = record["index"]
        level, shared = source / f"level{idx}", source / f"sharedassets{idx}.assets"
        pending.extend((level, shared))
        exact[idx] = {"level": level.as_posix(), "sharedassets": shared.as_posix()}
    env, loaded = UnityPy.Environment(path=source.as_posix()), set()
    while pending:
        path = require_file(pending.popleft()).resolve()
        key = path.as_posix()
        if key in loaded:
            continue
        loaded.add(key)
        file = env.load_file(key)
        if file is None:
            raise PlanError(f"UnityPy could not load required file: {path}")
        for ext in getattr(file, "externals", []):
            dependency = external_path(source, str(ext.path))
            if dependency is not None and dependency.as_posix() not in loaded:
                pending.append(dependency)
    print(f"[extract_map] loaded {len(loaded)} source files", file=sys.stderr)
    return env, exact


def load_tags(env) -> list[str]:
    for obj in env.objects:
        if obj.type.name == "TagManager":
            tags = obj.read_typetree().get("tags")
            if isinstance(tags, list):
                return tags
    raise PlanError("live source TagManager.tags missing; frozen analysis tags are not substituted")


def reject_unsupported_ground(file, scene_name: str) -> None:
    counts = Counter(obj.type.name for obj in file.objects.values())
    unsupported = {key: counts[key] for key in ("Terrain", "TerrainCollider") if counts[key]}
    if unsupported:
        raise PlanError(f"{scene_name} contains required unsupported terrain: {unsupported}; "
                        "ground must be converted before this map can be ported")


def collect_anchors(env, plan: dict, exact: dict) -> dict:
    found = {}
    for record in plan["anchor_scenes"]:
        file = env.files[exact[record["index"]]["level"]]
        for obj in sorted(file.objects.values(), key=lambda o: o.path_id):
            if obj.type.name != "MonoBehaviour":
                continue
            cls, status = EQ.arena_census.resolve_script_name(obj)
            if status != "ok" or not cls:
                continue
            short = cls.rsplit(".", 1)[-1]
            if not any(keyword.lower() in short.lower() for keyword in EQ.arena_census.SPAWN_ZONE_CLASS_KEYWORDS):
                continue
            try:
                go = obj.parse_monobehaviour_head().m_GameObject.deref().read()
                transform = go.m_Components[0].deref()
                position, _rotation = EQ.arena_census.world_transform_of_transform(transform)
                position = finite_vector(position, f"{record['name']} {cls} {obj.path_id}")
            except Exception as exc:
                raise PlanError(f"cannot resolve anchor {cls} {obj.path_id} in {record['name']}: {exc}") from exc
            found.setdefault(cls, []).append({"path_id": obj.path_id,
                                              "file": f"level{record['index']}",
                                              "scene_name": record["name"], "position": position})
    anchors = {cls: {"count": len(records), "sample_positions": [r["position"] for r in records],
                     "sample_files": sorted({r["file"] for r in records}), "source_anchors": records}
               for cls, records in sorted(found.items())}
    validate_anchors(anchors)
    return anchors


def source_navmesh_reference(env, plan: dict, exact: dict) -> dict:
    refs = []
    for record in plan["navmesh_scenes"]:
        for file_key in ("level", "sharedassets"):
            file = env.files[exact[record["index"]][file_key]]
            for obj in sorted(file.objects.values(), key=lambda o: o.path_id):
                if obj.type.name != "NavMeshData":
                    continue
                tree = obj.read_typetree()
                refs.append({"scene": record["name"], "file": Path(exact[record["index"]][file_key]).name,
                             "path_id": obj.path_id, "tile_count": len(tree.get("m_NavMeshTiles", [])),
                             "build_settings": tree.get("m_NavMeshBuildSettings"),
                             "source_bounds": tree.get("m_SourceBounds"),
                             "position": tree.get("m_Position"), "rotation": tree.get("m_Rotation"),
                             "agent_type_id": tree.get("m_AgentTypeID")})
    return {"note": "Source reference only; Unity builds fresh NavMesh using SPT agent settings.",
            "source_scenes": plan["navmesh_scenes"], "navmeshes": refs}


def write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False, allow_nan=False), encoding="utf-8")


@contextmanager
def bare_game_objects(component_sets):
    """Make ``EQ.extract_scene`` emit the GameObjects whose component id tuple is in ``component_sets`` bare.

    A twin carrier (scene_select) stands in for a skipped unit: it keeps name, activity, layer and local
    transform but must carry no component, and its mesh and materials must not be queued for export. The engine
    builds components per GameObject from the id list of the GameObject, so the stand-in is recognised by that list.
    """
    sets = {tuple(ids) for ids in component_sets}
    if not sets:
        yield
        return
    original = EQ.build_components

    def build(f, comp_ids, scene_idx, ctx):
        if tuple(comp_ids) in sets:
            return []
        return original(f, comp_ids, scene_idx, ctx)
    EQ.build_components = build
    try:
        yield
    finally:
        EQ.build_components = original


def extract_partial_scene(record: dict, level_files: dict, graphs: dict, tags: list[str], ctx,
                          native: NF.NativeFeatureExtraction | None) -> tuple[dict, dict]:
    """Extract one partial scene; returns ``(scene JSON, manifest partial_scenes block)``.

    ``level_files`` maps a BuildSettings index to its UnityPy level file and ``graphs`` caches the
    scene_select graph per index (a reference level is read once). The selection comes from
    scene_select.select_nodes; the native augmenters run through a ScopedFile so that only
    extracted owners are scanned (not the bare twin carriers, whose scripts belong to skipped units), and the
    result is checked against the selection (every object present, every parent and LOD renderer resolvable,
    carriers without components) before the scene is accepted.
    """
    def graph_of(index: int) -> SS.Graph:
        if index not in graphs:
            graphs[index] = SS.build_graph(level_files[index])
        return graphs[index]

    twins_of = record["select"].get("skip_twins_of")
    graph = graph_of(record["index"])
    selection = SS.select_nodes(record, graph, graph_of(twins_of) if twins_of is not None else None)
    file = level_files[record["index"]]
    with bare_game_objects(graph.nodes[t].components for t in selection.twin_carrier_tf_ids):
        scene = EQ.extract_scene(record["index"], file, tags, list(selection.node_tf_ids), ctx)
    orphans: list[list[int]] = []
    if native is not None:
        scoped = SS.ScopedFile(file, selection.scope_go_ids)
        native.augment_scene(scene, scoped, ctx, record["name"])
        orphans = [list(pair) for pair in scoped.orphans]
    problems = SS.structure_problems(scene, selection)
    if problems:
        raise EQ.HardExtractError(f"partial scene {record['name']} is inconsistent with its selection: "
                                  f"{problems[:5]} ({len(problems)} problem(s))")
    report = selection.report
    print(f"[extract_map] {record['name']}: partial scene of level{record['index']}: {report['units']} units, "
          f"{report['carriers']} carriers ({report.get('twin_carriers', 0)} twin), "
          f"{len(selection.twins)} twin unit(s) of level{twins_of} skipped, "
          f"{report['twin_descendants_added']} twin descendant(s) imported, {report['excluded']} unit(s) excluded "
          f"by {report['exclude_entries']} entries, {len(orphans)} ballistic orphan(s) hidden, "
          f"{report['dropped_script_owners']} container(s) with a dropped script", file=sys.stderr)
    block = {"index": record["index"], "objects": report["objects"], "units": report["units"],
             "carriers": report["carriers"], "rules": report["rules"],
             "dropped_script_owners": report["dropped_script_owners"],
             "ballistic_orphans": orphans, "go_ids_sha256": report["go_ids_sha256"],
             "excluded": report["excluded"], "exclude_entries": report["exclude_entries"],
             "twin_descendants_added": report["twin_descendants_added"],
             "twin_descendant_paths": list(report["twin_descendant_paths"])}
    if report["twin_descendants_added"]:
        # the bare carriers of the imported descendants: their source nodes carry components the package lacks, so
        # the source censuses of the validators must leave them out (validate/partial_scope.py, SceneScope)
        block["twin_carrier_ids"] = list(report["twin_carrier_ids"])
    return scene, block


def extract(plan_path: Path, source: Path, output: Path | None, timestamp: str | None, census_only: bool,
            native_target: Path | None = None) -> None:
    source = source.resolve()
    plan = load_plan(plan_path)
    plan_relative_path(plan_path)
    verify_scene_mapping(plan, read_build_scenes(source), source)
    if not census_only:
        if output is None:
            raise PlanError("--output is required unless --census-only")
        output = output.resolve()
        if output.is_relative_to(source) or source.is_relative_to(output):
            raise PlanError("extraction output must be separate from the read-only source tree")
        if output.exists() and any(output.iterdir()):
            raise PlanError(f"output is not empty: {output}; use a fresh extraction directory")
    native = None
    if native_target is not None and not census_only:
        # Resolves the plan's scripts role and reads the ballistic contract now, so a missing
        # role or SPT install fails before the multi-minute extraction.
        native = NF.NativeFeatureExtraction.for_plan(plan, source, native_target.resolve())
    env, exact = load_environment(source, plan)
    terrain_present = False
    partial_names = {r["name"] for r in partial_scene_records(plan)}
    for record in plan["scenes"]:
        file = env.files[exact[record["index"]]["level"]]
        has_terrain = any(o.type.name in ("Terrain", "TerrainCollider") for o in file.objects.values())
        if has_terrain and record["name"] in partial_names:
            raise PlanError(f"partial scene {record['name']} is cut from a level with Terrain; "
                            "ground conversion covers whole scenes only")
        terrain_present |= has_terrain
        if census_only:
            print(f"{record['name']} (level{record['index']}): "
                  f"{dict(Counter(obj.type.name for obj in file.objects.values()).most_common())}")
    if census_only:
        return
    anchors = collect_anchors(env, plan, exact)
    navmesh_ref = source_navmesh_reference(env, plan, exact)
    tags = load_tags(env)
    ctx = EQ.ExtractionContext(env)
    EQ.ensure_dirs(str(output))
    old_registry = EQ.SCENE_REGISTRY
    total_objects = 0
    partial_blocks: dict[str, dict] = {}
    level_files = {r["index"]: env.files[exact[r["index"]]["level"]] for r in plan["scenes"]}
    graphs: dict[int, SS.Graph] = {}
    try:
        EQ.SCENE_REGISTRY = {r["index"]: r["name"] for r in plan["scenes"]}
        from material_slots import preserve_material_slots
        scene_by_file = {f"level{r['index']}": r['name'] for r in plan['scenes']}
        with preserve_material_slots(EQ, scene_by_file) as material_slot_preservation:
            for record in plan["scenes"]:
                file = level_files[record["index"]]
                if record["name"] in partial_names:
                    scene, partial_blocks[record["name"]] = extract_partial_scene(
                        record, level_files, graphs, tags, ctx, native)
                else:
                    _go, transforms, _tf = EQ.build_scene_graph(file)
                    scene = EQ.extract_scene(record["index"], file, tags, list(transforms), ctx)
                if not scene["objects"]:
                    raise PlanError(f"selected content scene is empty: {record['name']}")
                if native is not None and record["name"] not in partial_names:
                    native.augment_scene(scene, file, ctx, record["name"])
                total_objects += len(scene["objects"])
                write_json(output / "scenes" / (record["name"] + ".json"), scene)
                print(f"[extract_map] {record['name']}: {len(scene['objects'])} objects", file=sys.stderr)
        from mesh_compat import compressed_mesh_decoder
        with compressed_mesh_decoder(EQ) as mesh_compatibility:
            meshes = EQ.write_meshes(ctx, str(output))
        materials = EQ.write_materials(ctx, str(output))
        from texture_compat import bc6h_rgb_decoder
        with bc6h_rgb_decoder():
            textures = EQ.write_textures(ctx, str(output))
    finally:
        EQ.SCENE_REGISTRY = old_registry
    if ctx.errors:
        raise PlanError(f"extraction reported {len(ctx.errors)} errors: {ctx.errors[:10]}")
    terrain_conversion = None
    if terrain_present:
        from terrain import convert_terrains
        converted = convert_terrains(env, source, plan["scenes"], output)
        if not converted.get("provenance"):
            raise PlanError("source contains Terrain/TerrainCollider but no ground conversion was produced")
        for name, existing in (("meshes", meshes), ("materials", materials), ("textures", textures)):
            added = converted[name]
            overlap = existing.keys() & added.keys()
            if overlap:
                raise PlanError(f"generated terrain {name} collide with source asset IDs: {sorted(overlap)}")
            existing.update(added)
        terrain_conversion = converted["provenance"]
    manifest = {"schema_version": 1,
                "source": provenance(plan, plan_path, source, timestamp or EQ.utcnow_iso()),
                "scenes": [r["name"] for r in plan["scenes"]], "slice": {},
                "meshes": meshes, "materials": materials, "textures": textures,
                "anchors_reference": anchors, "errors": ctx.errors, "warnings": ctx.warnings}
    if partial_blocks:
        manifest["partial_scenes"] = partial_blocks
    if terrain_conversion is not None:
        manifest["terrain_conversion"] = terrain_conversion
    if native is not None:
        manifest["source"].update(native.source_fields())
        manifest.update(native.manifest_blocks(env, output))
    if mesh_compatibility:
        manifest["mesh_compatibility"] = mesh_compatibility
    if material_slot_preservation:
        manifest["material_slot_preservation"] = material_slot_preservation
    write_json(output / "navmesh_ref" / (plan["key"].removeprefix("arena_") + "_source_navmesh.json"), navmesh_ref)
    write_json(output / "manifest.json", manifest)  # Complete marker written only after every asset succeeds.
    print(f"[extract_map] DONE {plan['key']}: {total_objects} objects, {len(meshes)} meshes, "
          f"{len(materials)} materials, {len(textures)} textures, {len(ctx.warnings)} warnings", file=sys.stderr)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--source", default=DEFAULT_SOURCE, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--fixed-timestamp")
    parser.add_argument("--census-only", action="store_true")
    parser.add_argument("--native-features", action="store_true",
                        help="restore original ballistic materials, NavMesh modifiers, static decals, light "
                             "culling data and rain-cover geometry; needs --target and the plan's scripts scene role")
    parser.add_argument("--target", type=Path,
                        help="SPT EscapeFromTarkov_Data directory (read-only) for the ballistic preset mapping; "
                             "required with, and only valid with, --native-features")
    return parser


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.native_features and args.target is None:
        parser.error("--native-features requires --target <SPT EscapeFromTarkov_Data>")
    if args.target is not None and not args.native_features:
        parser.error("--target is only used with --native-features")
    if args.native_features and args.census_only:
        parser.error("--native-features cannot be combined with --census-only")
    return args


def main() -> int:
    args = parse_args()
    try:
        extract(args.plan, args.source, args.output, args.fixed_timestamp, args.census_only,
                args.target if args.native_features else None)
    except (PlanError, EQ.HardExtractError, OSError) as exc:
        print(f"[extract_map] FAILED: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
