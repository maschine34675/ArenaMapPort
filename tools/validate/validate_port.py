"""Check a full map's build outputs (and optionally the staged distribution) for consistency."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

REPO = Path(__file__).resolve().parents[2]


def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify(stem, distribution=False, package=None):
    import UnityPy
    sys.path.insert(0, str(REPO / 'tools/extract'))

    descriptor_path = REPO / f'tools/maps/{stem}.json'
    descriptor = load(descriptor_path)
    key = descriptor['key']
    output = REPO / 'work/build' / stem
    # The accepted release packages carry a suffix (work/extract/<stem>-native-v5, AGENTS.md "Build, test,
    # deploy"); checking the plain work/extract/<stem> package against such a build reports a source plan
    # digest drift because that older package was extracted under the previous plan.
    package = Path(package) if package else REPO / 'work/extract' / stem
    from source_plan import validate_manifest_plan
    server_path = REPO / f'ArenaMapPort.Server/db/{descriptor["db_base_json_path"]}'
    for path in (output / f'{key}_scenes.bundle', output / f'{key}_navmesh.bundle',
                 output / 'material-shaders.json', output / 'editor_verify.json',
                 REPO / f'work/build/preset/{key}_preset.bundle', server_path):
        require(path.is_file(), f'Missing build output: {path}')
    run = load(output / 'unity-run.json')
    require(run.get('schema_version') == 1 and run.get('map_key') == key and run.get('exit_code') == 0,
            'Missing successful Unity run provenance')

    manifest = load(package / 'manifest.json')
    plan_path, plan = validate_manifest_plan(manifest)
    require(not manifest.get('errors'), 'Extraction reported errors')
    require(descriptor['all_scene_names'] == [descriptor['scripts_scene_name']] + manifest['scenes'],
            'Descriptor/extraction scene order mismatch')
    require(plan['key'] == key, 'Source plan/descriptor identity mismatch')
    require(all(manifest.get(kind) for kind in ('meshes', 'materials', 'textures')), 'Missing extracted assets')
    editor = load(output / 'editor_verify.json')
    expected_scenes = descriptor['all_scene_names']
    actual_scenes = [scene['scene_name'] for scene in editor['scenes']]
    require(len(actual_scenes) == len(expected_scenes) and set(actual_scenes) == set(expected_scenes),
            'Editor verification does not cover exactly the expected scenes')
    require(editor['scene_count'] == len(expected_scenes), 'Editor scene count mismatch')

    placement = load(output / 'spawn-placement.json')
    target = load(output / 'target-physics.json')
    require(placement.get('schema_version') == 1 and placement.get('map_key') == key
            and placement.get('status') == 'passed' and placement.get('barriers_restored') is True,
            'Offline player placement did not pass and restore source barriers')
    require(placement.get('source_plan_sha256') == manifest['source']['source_plan_sha256'],
            'Player placement source plan changed')
    require(placement.get('target_physics') == target and target.get('schema_version') == 1,
            'Player placement used different target physics')
    require(sha256(Path(target['source_path'])) == target['source_sha256'], 'Installed target physics changed since build')
    require(len(target['layers']) == len(target['layer_collision_matrix']) == 32, 'Incomplete target collision rules')
    player = target['player_layer']
    require(0 <= player < 32 and target['layers'][player] == 'Player', 'Invalid target Player layer')
    require(target['player_collision_mask'] == (target['layer_collision_matrix'][player] & ~(1 << player))
            and target['player_collision_mask'] > 0, 'Invalid target Player collision mask')
    require(placement.get('navmesh_vertices', 0) >= 3 and placement.get('navmesh_triangles', 0) > 0,
            'Player placement has no nonempty fresh NavMesh evidence')
    starts = placement.get('starts', [])
    require(len(starts) == 3 and {s.get('name') for s in starts} == {
        'SpawnPoint_Player_All','SpawnPoint_Player_Usec','SpawnPoint_Player_Bear'}, 'Missing authored player starts')
    anchors = manifest['anchors_reference']['EFT.Game.Spawning.SpawnPointMarker']['sample_positions']
    require(placement.get('source_anchor_count') == len(anchors), 'Player anchor evidence incomplete')
    accepted_positions = []
    for start in starts:
        position = start.get('accepted_position', [])
        require(start.get('accepted') is True and len(position) == 3
                and all(isinstance(v, (int,float)) and math.isfinite(v) for v in position), 'Invalid accepted player position')
        index = start.get('source_anchor_index', -1)
        require(isinstance(index,int) and 0 <= index < len(anchors), 'Invalid source anchor index')
        origin = anchors[index]
        require(math.dist(start['source_position'], origin) <= 0.001, 'Player source position does not match actual source anchor')
        require(math.dist(position, origin) <= 40.001, 'Player start exceeds source search radius')
        require(all(math.dist(position, other) >= 1.199 for other in accepted_positions), 'Player starts overlap')
        accepted_positions.append(position)
        proof = start.get('proof', {})
        require(proof.get('capsule_clear') is True and proof.get('outside_respawn_booths') is True
                and proof.get('ground_enabled') is True and proof.get('ground_active') is True
                and proof.get('ground_trigger') is False and proof.get('ground_collider'), 'Player ground/clearance proof incomplete')
        require(math.isfinite(proof.get('navmesh_distance', math.inf))
                and proof.get('navmesh_distance', math.inf) <= 0.171, 'Player ground does not agree with NavMesh')
        require(math.dist(position, proof['navmesh_position']) <= 0.171
                and abs(math.dist(position, proof['ground_position']) - 0.02) <= 0.001,
                'Accepted position does not match measured ground/NavMesh')

    nav_path = output / f'{key}_navmesh.bundle'
    env = UnityPy.load(str(nav_path))
    navs = [obj.read_typetree() for obj in env.objects if obj.type.name == 'NavMeshData']
    require(len(navs) == 1, 'Expected exactly one NavMeshData')
    tiles = navs[0].get('m_NavMeshTiles', [])
    require(len(tiles) > 0, 'NavMesh has no tiles')
    bounds = navs[0]['m_SourceBounds']
    require(all(math.isfinite(v) for block in bounds.values() for v in block.values()), 'Nonfinite NavMesh bounds')
    require(bounds['m_Extent']['x'] > 0 and bounds['m_Extent']['z'] > 0, 'Empty NavMesh horizontal bounds')
    base = load(server_path)
    require(base['Id'] == key and base['_Id'] == descriptor['id_field'], 'Server map identity mismatch')
    require(base['Scene'] == {'path': f'maps/{key}_preset.bundle', 'rcid': f'{key}.scenespreset.asset'},
            'Server preset reference mismatch')
    if distribution:
        plugin = REPO / 'dist/BepInEx/plugins/maschine-ArenaMapPort'
        # Stage 2 of the Arena gate (docs/PLAN-ENCRYPTION.md): the dist bundles are AMPB containers, so they can never equal
        # the plaintext under work/build. The staged copy must be exactly the encryption of its build output (the container is
        # deterministic), which the BundleCrypt tool compares without writing a temporary copy. Sidecar and base.json stay plain.
        sys.path.insert(0, str(REPO / 'tools/bundle'))
        import bundle_crypt
        bundle_pairs = [(output / f'{key}_{kind}.bundle', plugin / 'bundles' / f'{key}_{kind}.bundle')
                        for kind in ('scenes', 'navmesh')]
        bundle_pairs.append((REPO / f'work/build/preset/{key}_preset.bundle', plugin / 'bundles' / f'{key}_preset.bundle'))
        for source, target in bundle_pairs:
            require(target.is_file(), f'Staged artifact missing: {target}')
            try:
                bundle_crypt.verify(key, source, target, key_store=REPO / 'work' / 'release-keys')
            except bundle_crypt.BundleCryptError as exc:
                raise ValueError(f'Staged artifact mismatch: {target} is not the encryption of {source} ({exc.message})')
        pairs = [(output / 'material-shaders.json', plugin / f'material-shaders.{key}.json'),
                 (server_path, REPO / f'dist/SPT_Runtime/user/mods/ArenaMapPort/db/{descriptor["db_base_json_path"]}')]
        for source, target in pairs:
            require(target.is_file() and sha256(source) == sha256(target), f'Staged artifact mismatch: {target}')
    summary = {'map_key': key, 'scenes': len(expected_scenes), 'meshes': len(manifest['meshes']),
               'materials': len(manifest['materials']), 'textures': len(manifest['textures']),
               'navmesh_tiles': len(tiles), 'scene_bundle_bytes': (output / f'{key}_scenes.bundle').stat().st_size,
               'navmesh_triangles': placement['navmesh_triangles'], 'verified_starts':len(starts),
               'terrain_count':len(manifest.get('terrain_conversion',[])), 'warnings':len(manifest.get('warnings',[])),
               'distribution': distribution}
    print('PORT_VALIDATED ' + json.dumps(summary, sort_keys=True))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--map', required=True)
    parser.add_argument('--distribution', action='store_true')
    parser.add_argument('--package', help='extraction package the build was made from (default: work/extract/<map>)')
    args = parser.parse_args()
    try:
        require(args.map.isalnum(), 'Map stem must be alphanumeric')
        verify(args.map, args.distribution, args.package)
    except Exception as exc:
        print(f'PORT_FAILED: {exc}', file=sys.stderr)
        sys.exit(1)
