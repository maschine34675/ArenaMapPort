"""Check the exact registered map bundle/sidecar/database set in dist.

Since stage 2 of the Arena gate (docs/PLAN-ENCRYPTION.md) every distributed bundle is an AMPB container, so this
also requires: the magic on every bundle (a plaintext UnityFS bundle is refused), a chunk-verified container whose
header names the right map and whose plaintext starts with UnityFS, exactly one key file keys/<map>.json per
released map that has the schema, plausible resource-file names, sizes and sample parameters and no hash, and that unlocks the content
key against the Arena installed on this machine (the build machine has it), and no file anywhere in dist that
starts with "UnityFS". The container is read through tools/bundle/bundle_crypt.py (the C# tool), not in Python.

Every bundle's block table is also checked (tools/validate/validate_bundle_blocks.py, 2026-10-29): a data block with a
compression flag and csize >= usize evidently reaches Unity's player as raw LZ4 bytes (the intermittent Equator noise)."""
import json
import hashlib
from pathlib import Path
import re
import subprocess
import sys

from validate_map_descriptors import map_descriptor_paths, RELEASE_MAP_KEYS, gate_row_resolves
import validate_bundle_blocks

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO/'tools/bundle'))
import bundle_crypt  # noqa: E402 -- tests replace this module attribute with a stub

BUNDLE_KINDS = ('scenes', 'navmesh', 'preset')
AMPB_MAGIC = b'AMPB'
UNITYFS_MAGIC = b'UnityFS\0'
KEY_FILE_PATH_RE = re.compile(r'EscapeFromTarkovArena_Data/sharedassets([0-9]+)\.(assets\.resS|resource)')
KEY_FILE_KIND_ORDER = {'assets.resS': 0, 'resource': 1}
HEX64_RE = re.compile(r'[0-9a-f]{64}')
KEY_FILE_MAX_BYTES = 64 * 1024

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


def key_store_dir():
    """The build machine's key store (ignored by git); a function so a test that patches REPO moves it too."""
    return REPO/'work'/'release-keys'


def _tool_call(what, call):
    """Run one bundle_crypt call; any tool failure becomes a ValueError carrying the tool's own reason."""
    try:
        return call()
    except bundle_crypt.BundleCryptError as exc:
        raise ValueError(f'{what}: {exc.message}') from exc


def validate_not_spike_build(bundles, key_store=None):
    """Refuse a distribution that still carries the AirPit spike bundles under the final map name. The recorded size
    and SHA-256 are those of the PLAINTEXT build, and dist/ holds AMPB containers, so the decrypted length (the tool's
    `inspect`) is compared first and the 665 MB scenes bundle is only decrypted and hashed (`plain-sha256`, streamed)
    when it could be the stale file. A file that is still a plain bundle is compared as it always was."""
    for name, (size, digest) in STALE_SPIKE_BUNDLES.items():
        path = bundles/name
        if not path.is_file():
            continue
        if bundle_crypt.is_encrypted(path):
            plain_size = _tool_call(f'Bundle {name}', lambda: bundle_crypt.inspect(path, key_store=key_store)['plaintext_length'])
            if plain_size != size:
                continue
            actual = _tool_call(f'Bundle {name}', lambda: bundle_crypt.plain_sha256(path, key_store=key_store)[0])
        else:
            if path.stat().st_size != size:
                continue
            with path.open('rb') as stream:
                actual = hashlib.file_digest(stream, 'sha256').hexdigest()
        if actual == digest:
            raise ValueError(
                f'Stale AirPit bundle: {name} is the 2026-09-15 spike build (Arena 0.5.4 content, no native '
                f'features). Run the AirPit Unity build (docs/progress/airpit-release.md), then a full, non-DllOnly '
                f'client build so dist/ is restaged from work/build/airpit.')


def validate_encrypted_bundles(bundles, keys, key_store=None):
    """Every released map's three bundles are AMPB containers: the magic, a header that authenticates with the key
    store's content key and names this map, every chunk hash intact (`inspect --verify-chunks`) and a plaintext that
    starts with UnityFS (`read-range`). A plaintext bundle in dist is the one thing the release must never carry."""
    for key in keys:
        for kind in BUNDLE_KINDS:
            name = f'{key}_{kind}.bundle'
            path = bundles/name
            with path.open('rb') as stream:
                head = stream.read(4)
            if head != AMPB_MAGIC:
                raise ValueError(f'Plaintext bundle in dist (not an AMPB container): {name}; run a full, non-DllOnly '
                                 f'client build so dist/ is restaged encrypted')
            info = _tool_call(f'Bundle {name} failed inspection',
                              lambda: bundle_crypt.inspect(path, key_store=key_store, verify_chunks=True))
            if info.get('map_key') != key:
                raise ValueError(f'Bundle {name} belongs to map {info.get("map_key")!r}, not {key!r}')
            if info.get('mac_verified') is not True or info.get('chunks') != 'verified':
                raise ValueError(f'Bundle {name} was not verified (header MAC {info.get("mac_verified")!r}, '
                                 f'chunks {info.get("chunks")!r})')
            prefix = _tool_call(f'Bundle {name} cannot be read', lambda: bundle_crypt.read_range(path, 0, 8, key_store=key_store))
            if prefix != UNITYFS_MAGIC:
                raise ValueError(f'Bundle {name} does not decrypt to a UnityFS bundle')


def validate_block_tables(bundles, keys, key_store=None):
    """No distributed bundle may carry a data block with a compression flag and csize >= usize. Unity's player evidently reads such a
    block as stored data, so the raw LZ4 token stream reaches the consumer: block 6657 of the Equator scenes bundle held the
    tail of the AudioClip moll_indoor_wind_04 and played as static every wind cycle (released in 0.3.0). Only the UnityFS
    header and the block table are read, through the tool (`read-range`), once per bundle. UnityPy's chunk compressor
    writes such blocks; tools/bundle/fix_stored_blocks.py repairs the plaintext bundle, build_all.ps1 runs it as step 3b."""
    for key in keys:
        for kind in BUNDLE_KINDS:
            name = f'{key}_{kind}.bundle'
            report = validate_bundle_blocks.check_bundle(bundles/name, key_store=key_store, encrypted=True, crypt=bundle_crypt)
            if report.problems:
                raise ValueError(f'Bundle {name} fails the block table check: {"; ".join(report.problems)}; repair the plaintext '
                                 f'bundle with tools/bundle/fix_stored_blocks.py and stage it again')


def check_key_file_schema(path, key):
    """The shipped key file keys/<key>.json, shape only: schema 2, this map, between 1 and 16 wraps of exactly
    built_for / sample / files / salt / wrapped_key / check, a sample of exactly block_size (a multiple of 4096 from 4096
    to 1 MiB) and blocks (2..1024, at most 64 MiB per file), 1..32 files per wrap named
    EscapeFromTarkovArena_Data/sharedassets<N>.assets.resS or .resource in ascending numeric order (.assets.resS before
    .resource of the same N) with a positive size, and 64 lowercase hex digits for the three values. Nothing else may
    appear (the client would ignore an unknown field, the release must not carry one: a hash of an Arena file would let
    anyone derive the key). Canonical bytes: UTF-8, LF, no byte order mark, a final newline (docs/PLAN-ENCRYPTION.md
    section 6)."""
    raw = path.read_bytes()
    if len(raw) > KEY_FILE_MAX_BYTES:
        raise ValueError(f'Key file {path.name} is larger than 64 KiB')
    try:
        text = raw.decode('utf-8')
        data = json.loads(text)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f'Key file {path.name} is not valid UTF-8 JSON: {exc}') from exc
    if text.startswith('﻿') or '\r' in text or not text.endswith('\n'):
        raise ValueError(f'Key file {path.name} is not in canonical form (byte order mark, CR or missing final newline)')

    def require(condition, message):
        if not condition:
            raise ValueError(f'Key file {path.name} is invalid: {message}')

    def integer(value):
        return isinstance(value, int) and not isinstance(value, bool)

    require(isinstance(data, dict) and set(data) == {'schema', 'map_key', 'wraps'},
            'the top level must hold exactly schema, map_key and wraps')
    require(integer(data['schema']) and data['schema'] == 2, 'schema must be 2')
    require(data['map_key'] == key, f'map_key must be {key!r}')
    wraps = data['wraps']
    require(isinstance(wraps, list) and 1 <= len(wraps) <= 16, 'wraps must list 1 to 16 entries')
    for number, wrap in enumerate(wraps):
        where = f'wrap {number}'
        require(isinstance(wrap, dict) and set(wrap) == {'built_for', 'sample', 'files', 'salt', 'wrapped_key', 'check'},
                f'{where} must hold exactly built_for, sample, files, salt, wrapped_key and check')
        built_for = wrap['built_for']
        require(isinstance(built_for, str) and 0 < len(built_for) <= 64 and built_for.isprintable(),
                f'{where}: built_for must be a short printable string')
        sample = wrap['sample']
        require(isinstance(sample, dict) and set(sample) == {'block_size', 'blocks'}, f'{where}: sample must hold exactly block_size and blocks')
        block_size, blocks = sample['block_size'], sample['blocks']
        require(integer(block_size) and 4096 <= block_size <= 1 << 20 and block_size % 4096 == 0,
                f'{where}: sample block_size must be a multiple of 4096 from 4096 to 1048576')
        require(integer(blocks) and 2 <= blocks <= 1024 and block_size * blocks <= 64 << 20,
                f'{where}: sample blocks must be 2 to 1024 and block_size * blocks at most 64 MiB')
        files = wrap['files']
        require(isinstance(files, list) and 1 <= len(files) <= 32, f'{where}: files must list 1 to 32 entries')
        previous = (-1, -1)
        for entry in files:
            require(isinstance(entry, dict) and set(entry) == {'path', 'size'}, f'{where}: a file entry must hold exactly path and size')
            match = KEY_FILE_PATH_RE.fullmatch(entry['path']) if isinstance(entry['path'], str) else None
            require(match is not None, f'{where}: {entry["path"]!r} is not EscapeFromTarkovArena_Data/sharedassets<N>.assets.resS or .resource')
            order = (int(match.group(1)), KEY_FILE_KIND_ORDER[match.group(2)])
            require(order > previous, f'{where}: files must be unique and in ascending numeric order, .assets.resS before .resource')
            previous = order
            require(integer(entry['size']) and entry['size'] > 0, f'{where}: the size of {entry["path"]} must be a positive integer')
        for field in ('salt', 'wrapped_key', 'check'):
            require(isinstance(wrap[field], str) and HEX64_RE.fullmatch(wrap[field]) is not None,
                    f'{where}: {field} must be 64 lowercase hex digits')


def validate_key_files(plugin, keys, key_store=None, arena_unverified=None):
    """keys/ holds exactly one file per released map. Each passes check_key_file_schema and unlocks the content key held
    in the key store against the Arena installed on this machine (`verify-keys`: the player's own derivation, the full
    chain). `arena_unverified` is the tool's test seam (a synthetic Arena folder; the environment must allow it)."""
    root = plugin/'keys'
    expected = {f'{key}.json' for key in keys}
    actual = _files_under(root)
    if actual != expected:
        raise ValueError(f'Key file set mismatch: missing={sorted(expected - actual)} unexpected={sorted(actual - expected)}')
    for key in keys:
        check_key_file_schema(root/f'{key}.json', key)
        _tool_call(f'Key file {key}.json does not unlock',
                   lambda: bundle_crypt.verify_keys(key, root/f'{key}.json', key_store=key_store, arena_unverified=arena_unverified))


def validate_no_plaintext_unityfs(dist):
    """No file anywhere below dist/ may start with "UnityFS" + NUL (8 bytes read per file): a plaintext bundle under any
    name or folder (a stray copy, a renamed one, a backup) is a release that gives the maps away."""
    if not dist.is_dir():
        return
    for path in sorted(p for p in dist.rglob('*') if p.is_file()):
        with path.open('rb') as stream:
            if stream.read(8) == UNITYFS_MAGIC:
                raise ValueError(f'Plaintext UnityFS file in dist: {path.relative_to(dist).as_posix()}')


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


GATE_SOUND_MANIFEST_KEYS = {'schema_version', 'map_key', 'arena_version', 'mixer', 'rows', 'clips'}
GATE_SOUND_CLIP_KEYS = {'id', 'file', 'channels', 'sampleRate', 'frames', 'bytes', 'sha256', 'pitch_baked', 'baked_pitch', 'source', 'fit'}


def validate_gate_sounds(plugin, keys):
    """Gate and door sounds (docs/progress/gate-sounds.md), staged by the client csproj (Release and DllOnly) from the ignored
    work/extract/gate-sounds input of tools/extract/extract_gate_sounds.py. GateSounds.cs reads <plugin>/gate-sounds/<key>/gate-sounds.json at
    raid start: schema 1, the map's own key, the rows (equal to the tracked tools/maps/<stem>.gatesounds.json and every one of them resolving
    to a transform row of tools/maps/<stem>.doors.json), the mixer at -15 dB, and one PCM16 WAV per clip with the recorded length and SHA-256.
    The staged manifest must equal the extracted one byte for byte, the directory must hold exactly those files for the released maps, and a
    clip whose resource file is a stage-2 key-source file of the map (tools/bundle/key-sources.json) must be pitch-baked: the passport clip of
    AirPit is sharedassets28.resource itself, and its PCM payload would otherwise ship byte for byte in plaintext."""
    root = plugin/'gate-sounds'
    sources = json.loads((REPO/'tools/bundle/key-sources.json').read_text(encoding='utf-8'))['maps']
    expected_files = set()
    packages = {}
    for key in keys:
        stem = key.removeprefix('arena_')
        path = root/key/'gate-sounds.json'
        if not path.is_file():
            raise ValueError(f'Missing gate sound package: {key}/gate-sounds.json')
        staged = path.read_bytes()
        package = json.loads(staged.decode('utf-8'))
        if not isinstance(package, dict) or set(package) != GATE_SOUND_MANIFEST_KEYS or package['schema_version'] != 1 or package['map_key'] != key:
            raise ValueError(f'Gate sound package map or schema mismatch: {key}')
        extracted = REPO/'work/extract/gate-sounds'/key/'gate-sounds.json'
        if not extracted.is_file() or extracted.read_bytes() != staged:
            raise ValueError(f'Stale gate sound package: {key}/gate-sounds.json differs from work/extract/gate-sounds/{key}')
        mixer = package['mixer']
        if mixer.get('name') != 'ArenaGates' or mixer.get('db') != -15.0:
            raise ValueError(f'Gate sound mixer is not ArenaGates at -15.0 dB: {key}')
        tracked = json.loads((REPO/'tools/maps'/f'{stem}.gatesounds.json').read_text(encoding='utf-8'))
        if package['rows'] != tracked['rows'] or package['mixer'] != tracked['mixer']:
            raise ValueError(f'Gate sound rows differ from tools/maps/{stem}.gatesounds.json: {key}')
        poses = json.loads((REPO/'tools/maps'/f'{stem}.doors.json').read_text(encoding='utf-8'))['poses']
        ids = [clip['id'] for clip in package['clips']]
        if not package['rows'] or not ids or len(ids) != len(set(ids)):
            raise ValueError(f'Gate sound package has no rows or duplicate clips: {key}')
        for row in package['rows']:
            if row['pitch'] != 1.0 or row['clip'] not in ids or row['mixer_db'] != (-15.0 if row['mixer'] == 'ArenaGates' else 0.0):
                raise ValueError(f'Gate sound row is not playable at pitch 1 with its mixer level: {key} {row["barrier"]}/{row["part"]}')
            if not gate_row_resolves(row, poses):
                raise ValueError(f'Gate sound row matches no door pose: {key} {row["barrier"]}/{row["part"]}')
        protected = {Path(name).name for entry in sources[key] for name in entry['files']}
        listed = []
        for clip in package['clips']:
            if set(clip) != GATE_SOUND_CLIP_KEYS or clip['file'] != clip['id'] + '.wav':
                raise ValueError(f'Gate sound clip entry is malformed: {key}/{clip.get("id")}')
            if clip['source']['resource']['file'] in protected and not (clip['pitch_baked'] and clip['baked_pitch'] != 1.0):
                raise ValueError(f'Gate sound clip is a copy of a key-source file of the map, bake its pitch: {key}/{clip["id"]}')
            if clip['pitch_baked'] != (clip['baked_pitch'] != 1.0):
                raise ValueError(f'Gate sound clip pitch_baked disagrees with baked_pitch: {key}/{clip["id"]}')
            listed.append(clip)
        packages[key] = listed
        expected_files.add(f'{key}/gate-sounds.json')
        expected_files.update(f'{key}/{clip["file"]}' for clip in listed)
    actual_files = _files_under(root)
    if actual_files != expected_files:
        raise ValueError(f'Gate sounds file set mismatch: missing={sorted(expected_files - actual_files)} '
                         f'unexpected={sorted(actual_files - expected_files)}')
    for key, listed in packages.items():
        for clip in listed:
            blob = (root/key/clip['file']).read_bytes()
            if len(blob) != clip['bytes'] or hashlib.sha256(blob).hexdigest() != clip['sha256']:
                raise ValueError(f'Gate sound file length/hash mismatch: {key}/{clip["file"]}')
            if blob[:4] != b'RIFF' or blob[8:12] != b'WAVE' or len(blob) != 44 + clip['frames'] * clip['channels'] * 2:
                raise ValueError(f'Gate sound file is not a PCM16 WAV of the recorded size: {key}/{clip["file"]}')


def validate_monitors(plugin):
    """Score monitor artwork (docs/progress/score-monitors.md), staged by the client csproj (Release and DllOnly) from the ignored
    work/extract/monitors input of tools/extract/extract_monitor_templates.py. ScoreMonitors reads <plugin>/monitors/monitors.json when a mode plugin
    claims the TV screens: schema 1, the nine sprites of the reviewed table in its order (name, file, size, pivot, pixels per unit, border, faces), every PNG
    with the recorded length and SHA-256 and the pinned decoded pixels, and nothing else in the folder (the extractor's own validate). The staged files must
    equal the extracted ones byte for byte, and no sprite may come from a stage-2 key-source file (tools/bundle/key-sources.json): the art is plaintext, so
    a sampled stream of the content keys must never be among its sources (the asset and its .resS stream are both checked)."""
    import extract_monitor_templates as monitors
    root = plugin/'monitors'
    extracted = REPO/'work/extract/monitors'
    if not root.is_dir():
        raise ValueError('Missing score monitor artwork: monitors/')
    try:
        manifest = monitors.validate(root)
    except monitors.MonitorTemplateError as exc:
        raise ValueError(f'Score monitor artwork is invalid: {exc}') from exc
    actual = _files_under(root)
    if actual != _files_under(extracted):
        raise ValueError(f'Score monitor file set differs from work/extract/monitors: '
                         f'missing={sorted(_files_under(extracted) - actual)} unexpected={sorted(actual - _files_under(extracted))}')
    for name in sorted(actual):
        if (root/name).read_bytes() != (extracted/name).read_bytes():
            raise ValueError(f'Stale score monitor artwork file: {name}')
    sources = json.loads((REPO/'tools/bundle/key-sources.json').read_text(encoding='utf-8'))['maps']
    protected = {Path(name).name for entries in sources.values() for entry in entries for name in entry['files']}
    for sprite in manifest['sprites']:
        asset = sprite['source']['asset']
        if asset in protected or f'{asset}.resS' in protected:
            raise ValueError(f'Score monitor sprite comes from a key-source file of the maps: {sprite["name"]} ({asset})')


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
    subprocess.run(['pwsh', '-NoProfile', '-File', str(REPO/'tools/validate/validate_dll_versions.ps1'),
                    '-RepoRoot', str(REPO)], cwd=REPO, check=True)
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
    validate_gate_sounds(plugin, keys)
    validate_monitors(plugin)
    from extract_spectator_audio import validate as verify_spectator_audio
    audio = plugin/'spectator-audio'
    # The 13 calls and, since 0.4.0, the crowd murmur bed (arena_fans_ambient_postmatch.wav, manifest key 'bed'): the file set and
    # every byte below are compared with the extractor's output, so a bed that is stale, missing or not the extracted one is refused.
    verify_spectator_audio(audio, require_bed=True)
    extracted_audio = REPO/'work/extract/spectator-audio'
    if _files_under(audio) != _files_under(extracted_audio):
        raise ValueError('Spectator audio file set differs from extracted data')
    for name in _files_under(audio):
        if (audio/name).read_bytes() != (extracted_audio/name).read_bytes():
            raise ValueError(f'Stale spectator audio: {name}')
    from extract_spectator import MAP_SCENES, package_path
    from stage_spectators import validate_staged
    validate_staged(plugin/'spectators', {name: package_path(name) for name in MAP_SCENES})
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
    validate_not_spike_build(plugin/'bundles', key_store_dir())
    validate_encrypted_bundles(plugin/'bundles', keys, key_store_dir())
    validate_block_tables(plugin/'bundles', keys, key_store_dir())
    validate_key_files(plugin, keys, key_store_dir())
    validate_no_plaintext_unityfs(REPO/'dist')
    # fix-preset-cab-and-bundle-lifetime: every dist bundle's internal UnityFS directory-table
    # name (its 'CAB-...'/'BuildPlayer-...' entry) must be unique across the WHOLE distribution --
    # a duplicate (as every *_preset.bundle used to share, before that fix) makes Unity's own
    # AssetBundle.LoadFromFile refuse to load the second bundle while the first is still resident
    # in the same client session. See tools/validate/validate_bundle_names.py's module docstring
    # for the full root-cause evidence.
    subprocess.run([sys.executable, str(REPO/'tools/validate/validate_bundle_names.py'),
                    '--bundles-dir', str(plugin/'bundles'), '--key-store', str(key_store_dir())], cwd=REPO, check=True)
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
    print(f'DISTRIBUTION_OK {len(keys)} maps {len(expected_bundles)} bundles {len(expected_sidecars)} sidecars encrypted')


if __name__ == '__main__':
    try:
        validate()
    except Exception as exc:
        print(f'DISTRIBUTION_FAILED: {exc}', file=sys.stderr)
        sys.exit(1)
