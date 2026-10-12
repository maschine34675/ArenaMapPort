"""Recover Arena StaticDeferredDecal data, including its own material pointer.

Arena 0.5.4 stores LastTexSize BEFORE _material, unlike EFT's field order.
Layout is verified by exact payload consumption, typed Material/Texture2D pointers,
and texture dimensions on every original instance. No MeshRenderer is required.
An existing complete package can be augmented into a NEW directory with --package
and --output; ordinary full extraction also uses augment_scene below.

Partial scenes. A plan scene with ``select`` (scene_select.py) is extracted from part of its
level. ``augment_scene`` then runs on a ``scene_select.ScopedFile`` over the level, scoped to the
object ids of the package's own scene JSON, so only the owners that were extracted are scanned;
the refresh CLI does the same for every scene listed in the manifest's ``partial_scenes``.

Packages. The refresh CLI accepts the two legacy full-map packages (extract_equator.py,
extract_bay5.py) and, since PLAN-AIRPIT item 4, any generic package written by
extract_map.py --native-features. A generic package names its repository source plan in
its manifest; the plan (never the package) chooses the scenes and the source files, and
validate_manifest_plan rejects a package whose plan digest, registry or scene order
drifted. Generic output keeps extract_map.py's JSON formatting, and the texture write runs
under the same BC6H decoder, so refreshing a package that already carries its decals
reproduces it byte for byte. A package without a ``native_features`` block (plain
extraction) cannot be refreshed: extract it again with --native-features.
"""
from __future__ import annotations

import argparse
import contextlib
import json
import math
from pathlib import Path
import shutil
import struct
from typing import NamedTuple

from UnityPy.classes import PPtr
from common_ids import HardExtractError
from extract_light_culling import _mb_header
import census

TYPE = "StaticDeferredDecal"
SOURCE_TYPE = "EFT.Visual.StaticDecal.StaticDeferredDecal"
SHADER = "Decal/DeferredDecalDiffuseNormalsDynamicEditor"
SCHEMA_VERSION = 1


def require(condition, message):
    if not condition:
        raise HardExtractError("StaticDeferredDecal: " + message)


def decode(raw):
    enabled, offset = _mb_header(raw)
    require(len(raw) - offset == 72, "unexpected payload length")
    v = struct.unpack_from("<6fiqi8f", raw, offset)
    require(all(math.isfinite(x) for x in v), "non-finite field")
    require(v[6] >= 0 and v[7] != 0, "missing material reference")
    require(0 <= v[8] <= 9, "invalid decal queue")
    require(-1 <= v[9] <= 1 and 0 <= v[11] <= 1, "invalid clipping values")
    require(v[10] >= 0 and v[12] >= 0, "negative fade")
    require(v[4] > 0 and v[5] > 0, "invalid texture size")
    return dict(type=TYPE, schema_version=SCHEMA_VERSION, enabled=enabled,
                lower_left=list(v[:2]), upper_right=list(v[2:4]), texture_size=list(v[4:6]),
                material_pointer=dict(file_id=v[6], path_id=v[7]), queue=v[8],
                normal_clip=v[9], normal_fade=v[10], edge_clip=v[11], edge_fade=v[12], tint=list(v[13:17]))


def augment_scene(scene, unity_file, ctx):
    by_id = {o['id']: o for o in scene['objects']}
    require(len(by_id) == len(scene['objects']), "duplicate owner IDs")
    pending = []
    owners = set()
    for reader in unity_file.objects.values():
        if reader.type.name != 'MonoBehaviour':
            continue
        name, status = census.resolve_script_name(reader)
        if status != 'ok' or name != SOURCE_TYPE:
            continue
        head = reader.parse_monobehaviour_head()
        require(head.m_GameObject.file_id == 0, "non-local owner")
        owner_id = str(head.m_GameObject.path_id)
        require(owner_id in by_id and owner_id not in owners, "missing/duplicate owner")
        owners.add(owner_id)
        comp = decode(reader.get_raw_data())
        ptr = comp.pop('material_pointer')
        try:
            material = PPtr(m_FileID=ptr['file_id'], m_PathID=ptr['path_id'], assetsfile=reader.assets_file).deref()
            require(material.type.name == 'Material', "reference is not a Material")
            typed = material.read()
            from extract_equator import resolve_shader_name
            require(resolve_shader_name(typed) == SHADER, "unexpected source shader")
            textures = dict(typed.m_SavedProperties.m_TexEnvs)
            main = textures['_MainTex'].m_Texture.deref()
            require(main.type.name == 'Texture2D', "_MainTex is not Texture2D")
            tt = main.read_typetree()
            ratio_x = comp['texture_size'][0] / tt['m_Width']
            ratio_y = comp['texture_size'][1] / tt['m_Height']
            # Fourteen source components retain authoring resolution although Arena ships
            # a half-size texture. Preserve the atlas region in normalized coordinates.
            # AirPit's furnished shops (level38) add the opposite case, ratio 0.5: nine
            # Graffiti6_ShadowPlaneBox quads author 32x32 against a 64x64 texture; the
            # region is the whole texture and the importer scales the corners the same way.
            require(ratio_x == ratio_y and ratio_x in (0.5, 1, 2), "unexpected texture size ratio")
            comp['source_texture_size'] = [tt['m_Width'], tt['m_Height']]
        except HardExtractError:
            raise
        except Exception as exc:
            raise HardExtractError(f"StaticDeferredDecal: unresolved material {ptr}: {exc}") from exc
        comp['material_id'] = ctx.queue_material(material)
        comp['source_component_id'] = str(reader.path_id)
        comp['source_material'] = dict(file=ctx.owning_basename(material), path_id=material.path_id, name=typed.m_Name)
        existing = [c for c in by_id[owner_id]['components'] if c['type'] == TYPE]
        require(not existing or existing == [comp], "conflicting existing decal")
        if not existing:
            pending.append((by_id[owner_id], comp))
    for owner in scene['objects']:
        require(owner['id'] in owners or not any(c['type'] == TYPE for c in owner['components']),
                "decal has no source counterpart")
    for owner, comp in pending:
        owner['components'].append(comp)
    return len(owners)


LEGACY_MAP_KEYS = {
    'tools/extract/extract_bay5.py': 'arena_bay5',
    'tools/extract/extract_equator.py': 'arena_equator',
}


class PackageSource(NamedTuple):
    """The live Arena scenes behind one package: what refresh() augments."""
    key: str
    env: object
    registry: dict            # BuildSettings index -> scene name
    level_files: dict         # BuildSettings index -> UnityPy level file
    generic: bool


def package_kind(manifest):
    """'generic' for an extract_map.py package, 'legacy' for Equator/Bay5; anything else is refused."""
    script = manifest['source'].get('extractor_script')
    if script == 'tools/extract/extract_map.py':
        return 'generic'
    key = manifest['source'].get('map_key') or LEGACY_MAP_KEYS.get(script)
    require(key in ('arena_equator', 'arena_bay5'), "unsupported map")
    return 'legacy'


def open_source(manifest):
    kind = package_kind(manifest)
    source = manifest['source']['arena_data_root']
    if kind == 'generic':
        import extract_map as em
        from source_plan import PlanError, validate_manifest_plan
        try:
            # Digest, registry and scene order must still equal the repository plan; the source
            # BuildSettings must still map every plan scene.
            _plan_path, plan = validate_manifest_plan(manifest)
        except PlanError as exc:
            raise HardExtractError(f"StaticDeferredDecal: package does not match its source plan: {exc}") from exc
        env, exact = em.load_environment(Path(source), plan)
        registry = {r['index']: r['name'] for r in plan['scenes']}
        return PackageSource(plan['key'], env, registry,
                             {idx: env.files[exact[idx]['level']] for idx in registry}, True)
    import extract_equator as eq
    import extract_bay5 as bay
    key = manifest['source'].get('map_key') or LEGACY_MAP_KEYS[manifest['source']['extractor_script']]
    registry = eq.SCENE_REGISTRY if key == 'arena_equator' else bay.resolve_scene_indices(source)
    loader = eq.load_environment if key == 'arena_equator' else bay.load_environment_bay5
    env, paths = loader(source, list(registry))
    return PackageSource(key, env, dict(registry), {idx: env.files[paths[idx]['level']] for idx in registry}, False)


def dump_json(value, generic):
    """Generic packages are written by extract_map.write_json; the legacy pair by plain json.dumps."""
    if generic:
        return json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False, allow_nan=False)
    return json.dumps(value, indent=2, sort_keys=True)


def refresh(package: Path, output: Path) -> int:
    """Augment `package` into the NEW directory `output`; returns the decal count."""
    require(not output.exists(), "output must be a new package directory")
    import extract_equator as eq
    manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8'))
    require('native_features' in manifest,
            "package has no native_features block; extract it again with --native-features")
    src = open_source(manifest)
    require(set(manifest['scenes']) == set(src.registry.values()), "incomplete scene set")
    ctx = eq.ExtractionContext(src.env)
    scenes = {}
    count = 0
    partial = manifest.get('partial_scenes') or {}
    for idx, name in src.registry.items():
        scene = json.loads((package / 'scenes' / (name + '.json')).read_text(encoding='utf-8'))
        level_file = src.level_files[idx]
        if name in partial:
            from scene_select import ScopedFile
            level_file = ScopedFile(level_file, [int(o['id']) for o in scene['objects']])
        count += augment_scene(scene, level_file, ctx)
        scenes[name] = scene
    require(count > 0, "empty source census")
    shutil.copytree(package, output)
    manifest['materials'].update(eq.write_materials(ctx, str(output)))
    if src.generic:
        from texture_compat import bc6h_rgb_decoder
        texture_scope = bc6h_rgb_decoder()
    else:
        texture_scope = contextlib.nullcontext()
    with texture_scope:
        manifest['textures'].update(eq.write_textures(ctx, str(output)))
    manifest['native_features']['static_decals'] = dict(schema_version=SCHEMA_VERSION, count=count)
    for name, scene in scenes.items():
        (output / 'scenes' / (name + '.json')).write_text(dump_json(scene, src.generic), encoding='utf-8')
    (output / 'manifest.json').write_text(dump_json(manifest, src.generic), encoding='utf-8')
    print(f"DECALS VERIFIED: {src.key}: {count} instances, {len(ctx.material_work)} materials, {len(ctx.texture_work)} textures")
    return count


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--package', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args(argv)
    refresh(args.package, args.output)


if __name__ == '__main__':
    main()
