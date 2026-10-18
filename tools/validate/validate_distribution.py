"""Check the exact registered map bundle/sidecar/database set in dist."""
import json
import hashlib
from pathlib import Path
import subprocess
import sys

from validate_map_descriptors import map_descriptor_paths, RELEASE_MAP_KEYS

REPO = Path(__file__).resolve().parents[2]

# Not every released map carries every optional payload. The key set of the release itself comes from
# validate_map_descriptors.RELEASE_MAP_KEYS (four maps since Bowl, PLAN-BOWL item 8, 2026-10-18). All four have an
# overview pack (Bowl's joined with PLAN-BOWL item 17, 0.3.0). AirPit has no active flare light (item 22), so it has no
# flare package; Bowl ships one (4 of its 28 lights are active, item 17).
OVERVIEW_MAP_NAMES = ('Equator', 'Bay5', 'AirPit', 'Bowl')
FLARE_MAP_KEYS = ('arena_equator', 'arena_bay5', 'arena_bowl')
# Only Bay5 has a native water surface (the harbor's Sea/Plane mesh, NativeWaterRepairs.cs); Equator, AirPit and Bowl
# carry no water package, so a water directory for any other key is parked or stray output.
WATER_MAP_KEYS = ('arena_bay5',)


# Bundle contents are not otherwise inspected here (the build's roundtrip validators cover them), so the one stale
# state that is known to exist is refused by its bytes: the 2026-09-15 AirPit spike build (Arena 0.5.4 content, no
# native features, first-guess NavMesh). work/build/airpit keeps holding it until the Phase B Unity build (U1)
# replaces it, and a full client build stages whatever work/build/airpit holds under the final map name. File name
# -> (size, sha256), as recorded by that build's work/build/airpit/full-map-validation.json. The preset bundle is
# left out on purpose: a correct rebuild from unchanged sidecars may reproduce it byte for byte.
STALE_SPIKE_BUNDLES = {
    'arena_airpit_scenes.bundle': (664670450, '31100570f3109e7431ff2e59783dc2fa58a76df39c5f0d58e03c349a2b3495aa'),
    'arena_airpit_navmesh.bundle': (460078, '635762d07b68d6c053fa406d5e191bf37347b09973db6e6d86ae1e229d279687'),
}


def validate_not_spike_build(bundles):
    """Refuse a distribution that still carries the AirPit spike bundles under the final map name. The size is
    compared first so the 665 MB scenes bundle is only hashed when it could be the stale file."""
    for name, (size, digest) in STALE_SPIKE_BUNDLES.items():
        path = bundles/name
        if not path.is_file() or path.stat().st_size != size:
            continue
        with path.open('rb') as stream:
            actual = hashlib.file_digest(stream, 'sha256').hexdigest()
        if actual == digest:
            raise ValueError(
                f'Stale AirPit bundle: {name} is the 2026-09-15 spike build (Arena 0.5.4 content, no native '
                f'features). Run the AirPit Unity build (docs/progress/airpit-release.md), then a full, non-DllOnly '
                f'client build so dist/ is restaged from work/build/airpit.')


def _files_under(root):
    """Relative posix names of every file below root; an absent directory has none."""
    if not root.is_dir():
        return set()
    return {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}


def _check_art_files(root, expected, label):
    """Exact set of non-empty JPEG files (the extractors' default format) under one art directory."""
    actual = _files_under(root)
    if actual != expected:
        raise ValueError(f'{label} file set mismatch: missing={sorted(expected - actual)} unexpected={sorted(actual - expected)}')
    for name in sorted(expected):
        with (root/name).open('rb') as stream:
            head = stream.read(3)
        if head != b'\xff\xd8\xff':
            raise ValueError(f'{label} file is empty or not a JPEG: {name}')


def validate_art(plugin, server, keys):
    """Banners (server mod) and loading screens (client plugin) are written by their extractors straight into
    dist/ and staged by no csproj target, so only this check keeps a map from shipping without its art or with
    stray leftovers. The extractor tables must list exactly the released maps."""
    sys.path.insert(0, str(REPO/'tools/extract'))
    import extract_banners
    import extract_loadscreens
    if set(extract_banners.BANNERS) != set(keys):
        raise ValueError('Banner table does not match the released maps')
    if set(extract_loadscreens.LOADSCREENS) != set(keys):
        raise ValueError('Loadscreen table does not match the released maps')
    _check_art_files(server/'banners', {extract_banners.output_filename(key) for key in keys}, 'Banner')
    _check_art_files(plugin/'loadscreens',
                     {extract_loadscreens.output_filename(key, index)
                      for key in keys for index in range(1, len(extract_loadscreens.LOADSCREENS[key]) + 1)},
                     'Loadscreen')


def validate_ai_sidecars(plugin, keys):
    """AiAnchors.cs reads <plugin>/ai/<key without "arena_">.ai.json. The git-tracked tools/maps sidecars are staged
    by a csproj row per released map (DllOnly builds included), so the staged set must be exactly one per released map
    and byte-equal to its source: a missing row silently keeps the NavMesh-synthesised AI, a stray file is parked
    output."""
    expected = {f'{key.removeprefix("arena_")}.ai.json' for key in keys}
    actual = _files_under(plugin/'ai')
    if actual != expected:
        raise ValueError(f'AI sidecar file set mismatch: missing={sorted(expected - actual)} unexpected={sorted(actual - expected)}')
    for name in sorted(expected):
        staged = (plugin/'ai'/name).read_bytes()
        if staged != (REPO/'tools/maps'/name).read_bytes():
            raise ValueError(f'Stale AI sidecar: {name}')
        if json.loads(staged).get('map_key') != name.removesuffix('.ai.json'):
            raise ValueError(f'AI sidecar map key mismatch: {name}')


def validate_acoustics(plugin, keys):
    """Original acoustic graph, bake and room tones, staged from the ignored work/extract/spatial-audio inputs (one
    directory per released map). Each package must name its own map, and the directory must hold exactly the graph
    JSON plus the bake and room-tone files the graph lists, each with the recorded length and SHA-256."""
    root = plugin/'acoustics'
    expected_files = set()
    packages = {}
    for key in keys:
        path = root/key/'spatial-audio.json'
        if not path.is_file():
            raise ValueError(f'Missing acoustic package: {key}/spatial-audio.json')
        package = json.loads(path.read_text(encoding='utf-8'))
        if package['schema_version'] != 1 or package['map_key'] != key:
            raise ValueError(f'Acoustic package map or schema mismatch: {key}')
        listed = [package['bake']] + [clip['decoded'] for clip in package['clips']]
        packages[key] = listed
        expected_files.add(f'{key}/spatial-audio.json')
        expected_files.update(f'{key}/{item["file"]}' for item in listed)
    actual_files = _files_under(root)
    if actual_files != expected_files:
        raise ValueError(f'Acoustics file set mismatch: missing={sorted(expected_files - actual_files)} '
                         f'unexpected={sorted(actual_files - expected_files)}')
    for key, listed in packages.items():
        for item in listed:
            blob = (root/key/item['file']).read_bytes()
            if len(blob) != item['bytes'] or hashlib.sha256(blob).hexdigest() != item['sha256']:
                raise ValueError(f'Acoustic file length/hash mismatch: {key}/{item["file"]}')


def validate_water(plugin):
    """Original harbor water settings and raw texture mip chains, staged by the client csproj (Release and DllOnly)
    from the ignored work/extract/bay5-water input of tools/extract/extract_water.py (docs/progress/bay5-water.md).
    NativeWaterRepairs.cs reads <plugin>/water/<key>/water.json at raid start: schema 1, the map's own key, and the
    four WaterSettings textures as textures/<name>.bin with the recorded byte count and SHA-256; anything else makes
    it refuse the package and leaves the harbor on the unlit placeholder. So the directory must hold exactly those
    files for WATER_MAP_KEYS, with each mip chain intact and as long as its recorded dimensions, format and mip count
    require (the extractor's own arithmetic). A stray or parked map's water directory is rejected by the exact set."""
    import extract_water
    root = plugin/'water'
    expected_files = set()
    packages = {}
    for key in WATER_MAP_KEYS:
        path = root/key/'water.json'
        if not path.is_file():
            raise ValueError(f'Missing water package: {key}/water.json')
        package = json.loads(path.read_text(encoding='utf-8'))
        if package['schema_version'] != extract_water.SCHEMA_VERSION or package['map_key'] != key:
            raise ValueError(f'Water package map or schema mismatch: {key}')
        if package['target_shader'] != extract_water.TARGET_SHADER:
            raise ValueError(f'Water package shader mismatch: {key}')
        textures = package['textures']
        if set(textures) != set(extract_water.TEXTURE_FIELDS):
            raise ValueError(f'Water texture set mismatch: {key}: '
                             f'missing={sorted(set(extract_water.TEXTURE_FIELDS) - set(textures))} '
                             f'unexpected={sorted(set(textures) - set(extract_water.TEXTURE_FIELDS))}')
        for field, texture in textures.items():
            # The same rule as the client's ReadTexture: a bare file name directly below textures/.
            file = texture['file']
            name = file.removeprefix('textures/')
            if not file.startswith('textures/') or not name or '/' in name or '\\' in name:
                raise ValueError(f'Invalid water texture filename: {key}/{field}: {file!r}')
        if len({texture['file'] for texture in textures.values()}) != len(textures):
            raise ValueError(f'Water textures share a file: {key}')
        packages[key] = textures
        expected_files.add(f'{key}/water.json')
        expected_files.update(f'{key}/{texture["file"]}' for texture in textures.values())
    actual_files = _files_under(root)
    if actual_files != expected_files:
        raise ValueError(f'Water file set mismatch: missing={sorted(expected_files - actual_files)} '
                         f'unexpected={sorted(actual_files - expected_files)}')
    for key, textures in packages.items():
        for texture in textures.values():
            try:
                chain = extract_water.texture_byte_count(texture['width'], texture['height'],
                                                        texture['mip_count'], texture['format'])
            except extract_water.HardExtractError as exc:
                raise ValueError(f'Water texture metadata invalid: {key}/{texture["file"]}: {exc}') from exc
            if chain != texture['byte_count']:
                raise ValueError(f'Water texture byte count does not match its mip chain: {key}/{texture["file"]}')
            blob = (root/key/texture['file']).read_bytes()
            if len(blob) != texture['byte_count'] or hashlib.sha256(blob).hexdigest() != texture['sha256']:
                raise ValueError(f'Water texture length/hash mismatch: {key}/{texture["file"]}')


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
    descriptors = [m for m in descriptors if m['key'] in RELEASE_MAP_KEYS]
    keys = [m['key'] for m in descriptors]
    if set(keys) != set(RELEASE_MAP_KEYS):
        raise ValueError('Missing release map descriptor')
    plugin = REPO/'dist/BepInEx/plugins/maschine-ArenaMapPort'
    for optional in ('arena-audio', 'arena-ui'):
        if any(p.is_file() for p in (plugin/optional).rglob('*')):
            raise ValueError(f'Unused development payload in release: {optional}')
    expected_maps = set()
    for name in OVERVIEW_MAP_NAMES:
        folder = f'ArenaMapPort_{name}'
        for file in (f'{folder}.jsonc', 'Layers/Ground.svg'):
            relative = f'{folder}/{file}'
            expected_maps.add(relative)
            if (plugin/'dynamicmaps'/relative).read_bytes() != (REPO/'work/dynamicmaps/release'/relative).read_bytes():
                raise ValueError(f'Stale overview map: {relative}')
    actual_maps = {p.relative_to(plugin/'dynamicmaps').as_posix() for p in (plugin/'dynamicmaps').rglob('*') if p.is_file()}
    if actual_maps != expected_maps:
        raise ValueError('Unexpected or missing overview map files')
    sys.path.insert(0, str(REPO/'tools/extract'))
    from extract_selection_map import validate as validate_selection_map, FILENAME, MANIFEST
    selection_dir = plugin/'map-selection'
    validate_selection_map(selection_dir)
    for name in (FILENAME, MANIFEST):
        if (selection_dir/name).read_bytes() != (REPO/'work/extract/map-selection'/name).read_bytes():
            raise ValueError(f'Stale selection map file: {name}')
    from extract_map_descriptions import validate_pack
    locale_dir = REPO/'dist/SPT_Runtime/user/mods/ArenaMapPort/locales'
    if {p.relative_to(locale_dir).as_posix() for p in locale_dir.rglob('*') if p.is_file()} != {'map-descriptions.json'}:
        raise ValueError('Unexpected or missing map description files')
    descriptions = (locale_dir/'map-descriptions.json').read_bytes()
    validate_pack(json.loads(descriptions))
    if descriptions != (REPO/'work/extract/map-descriptions/map-descriptions.json').read_bytes():
        raise ValueError('Stale map descriptions')
    validate_art(plugin, REPO/'dist/SPT_Runtime/user/mods/ArenaMapPort', keys)
    validate_ai_sidecars(plugin, keys)
    validate_acoustics(plugin, keys)
    from extract_multiflare import validate_graph
    expected_flare_files = set()
    for key in FLARE_MAP_KEYS:
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
    validate_water(plugin)
    expected_bundles = {f'{key}_{kind}.bundle' for key in keys for kind in ('scenes','navmesh','preset')}
    actual_bundles = {p.relative_to(plugin/'bundles').as_posix() for p in (plugin/'bundles').rglob('*') if p.is_file()}
    if actual_bundles != expected_bundles:
        raise ValueError(f'Bundle set mismatch: missing={sorted(expected_bundles-actual_bundles)} unexpected={sorted(actual_bundles-expected_bundles)}')
    validate_not_spike_build(plugin/'bundles')
    # fix-preset-cab-and-bundle-lifetime: every dist bundle's internal UnityFS directory-table
    # name (its 'CAB-...'/'BuildPlayer-...' entry) must be unique across the WHOLE distribution --
    # a duplicate (as every *_preset.bundle used to share, before that fix) makes Unity's own
    # AssetBundle.LoadFromFile refuse to load the second bundle while the first is still resident
    # in the same client session. See tools/validate/validate_bundle_names.py's module docstring
    # for the full root-cause evidence.
    subprocess.run([sys.executable, str(REPO/'tools/validate/validate_bundle_names.py'),
                    '--bundles-dir', str(plugin/'bundles')], cwd=REPO, check=True)
    expected_sidecars = {f'material-shaders.{key}.json' for key in keys}
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
