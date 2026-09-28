"""Check the exact registered map bundle/sidecar/database set in dist."""
import json
import hashlib
from pathlib import Path
import subprocess
import sys

from validate_map_descriptors import map_descriptor_paths

REPO = Path(__file__).resolve().parents[2]


def validate():
    # This check also runs on fully resumed distributions, when no fresh map build
    # would otherwise compare the current C# registry with the JSON descriptors.
    subprocess.run([sys.executable, str(REPO/'tools/validate/validate_map_descriptors.py')],
                   cwd=REPO, check=True)
    descriptors = [json.loads(p.read_text(encoding='utf-8'))
                   for p in map_descriptor_paths(REPO/'tools/maps')]
    keys = [m['key'] for m in descriptors]
    if len(keys) != len(set(keys)):
        raise ValueError('Duplicate descriptor keys')
    plugin = REPO/'dist/BepInEx/plugins/ArenaMapPort'
    sys.path.insert(0, str(REPO/'tools/extract'))
    from extract_multiflare import validate_graph
    expected_flare_files = set()
    for key in ('arena_equator', 'arena_bay5'):
        flare_dir = plugin/'flares'/key
        package = json.loads((flare_dir/'flares.json').read_text(encoding='utf-8'))
        if package['map_key'] != key:
            raise ValueError('Flare package map mismatch')
        validate_graph(package)
        expected_flare_files.add(f'{key}/flares.json')
        for texture in package['textures']:
            blob = (flare_dir/texture['file']).read_bytes()
            if len(blob) != texture['byte_count'] or hashlib.sha256(blob).hexdigest() != texture['sha256']:
                raise ValueError('Flare texture length/hash mismatch')
            expected_flare_files.add(f'{key}/{texture["file"]}')
    if {p.relative_to(plugin/'flares').as_posix() for p in (plugin/'flares').rglob('*') if p.is_file()} != expected_flare_files:
        raise ValueError('Unexpected or missing flare package files')
    expected_bundles = {f'{key}_{kind}.bundle' for key in keys for kind in ('scenes','navmesh','preset')}
    actual_bundles = {p.name for p in (plugin/'bundles').iterdir() if p.is_file()}
    if actual_bundles != expected_bundles:
        raise ValueError(f'Bundle set mismatch: missing={sorted(expected_bundles-actual_bundles)} unexpected={sorted(actual_bundles-expected_bundles)}')
    # fix-preset-cab-and-bundle-lifetime: every dist bundle's internal UnityFS directory-table
    # name (its 'CAB-...'/'BuildPlayer-...' entry) must be unique across the WHOLE distribution --
    # a duplicate (as every *_preset.bundle used to share, before that fix) makes Unity's own
    # AssetBundle.LoadFromFile refuse to load the second bundle while the first is still resident
    # in the same client session. See tools/validate/validate_bundle_names.py's module docstring
    # for the full root-cause evidence.
    subprocess.run([sys.executable, str(REPO/'tools/validate/validate_bundle_names.py'),
                    '--bundles-dir', str(plugin/'bundles')], cwd=REPO, check=True)
    expected_sidecars = {'material-shaders.json' if key == 'arena_equator' else f'material-shaders.{key}.json' for key in keys}
    actual_sidecars = {p.name for p in plugin.glob('material-shaders*.json')}
    if actual_sidecars != expected_sidecars:
        raise ValueError('Shader sidecar set does not exactly match registered maps')
    db = REPO/'dist/SPT_Runtime/user/mods/ArenaMapPort/db'
    expected_db = {m['db_base_json_path'] for m in descriptors}
    actual_db = {p.relative_to(db).as_posix() for p in db.rglob('*.json')}
    if actual_db != expected_db:
        raise ValueError('Server database set does not exactly match registered maps')
    for m in descriptors:
        rel = m['db_base_json_path']
        if json.loads((db/rel).read_text(encoding='utf-8')) != json.loads((REPO/'ArenaMapPort.Server/db'/rel).read_text(encoding='utf-8')):
            raise ValueError(f'Staged server data mismatch: {rel}')
    for name, destination in (('Client',plugin), ('Server',REPO/'dist/SPT_Runtime/user/mods/ArenaMapPort')):
        project = REPO/f'ArenaMapPort.{name}/ArenaMapPort.{name}.csproj'
        # Ask MSBuild for the effective output path; Client intentionally disables
        # the framework subdirectory while Server retains it.
        built = Path(subprocess.check_output(['dotnet','msbuild',str(project),'-nologo',
                         '-p:Configuration=Release','-getProperty:TargetPath'],cwd=REPO,text=True).strip())
        staged = destination/built.name
        if not built.is_file() or not staged.is_file():
            raise ValueError(f'Missing built/staged {name} DLL')
        with built.open('rb') as source_stream, staged.open('rb') as target_stream:
            if hashlib.file_digest(source_stream,'sha256').digest() != hashlib.file_digest(target_stream,'sha256').digest():
                raise ValueError(f'Stale staged {name} DLL')
    print(f'DISTRIBUTION_OK {len(keys)} maps {len(expected_bundles)} bundles {len(expected_sidecars)} sidecars')


if __name__ == '__main__':
    try:
        validate()
    except Exception as exc:
        print(f'DISTRIBUTION_FAILED: {exc}', file=sys.stderr)
        sys.exit(1)
