"""Content-bound input snapshots for full-map validation and later resume.

Capture after Unity/identity patch, before mandatory validation; verify after all
checks. Neither operation writes source/package files or upgrades a receipt.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import importlib.metadata
import json
import ntpath
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

REPO = Path(__file__).resolve().parents[2]
SCHEMA = 2
SNAPSHOT_NAME = 'validation-inputs.json'


def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def file_entry(path):
    path = Path(path).absolute()
    require(not path.is_symlink(), f'Input symlinks are unsupported: {path}')
    path = path.resolve(strict=True)
    require(path.is_file(), f'Input is not a regular file: {path}')
    before = path.stat()
    with path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    after = path.stat()
    # Content hashes always decide identity. Stat is only a concurrent-write guard.
    require((before.st_size, before.st_mtime_ns, before.st_ino) ==
            (after.st_size, after.st_mtime_ns, after.st_ino), f'Input changed while hashing: {path}')
    return {'path': path.as_posix(), 'bytes': after.st_size, 'sha256': digest}


def tree_files(root, suffixes=None, excluded=()):
    root = Path(root).resolve(strict=True)
    result = []
    for directory, dirs, files in os.walk(root, followlinks=False):
        require(Path(directory).resolve(strict=True).is_relative_to(root), f'Input directory escapes tree root: {directory}')
        dirs[:] = sorted(d for d in dirs if d not in excluded)
        for name in dirs:
            child = Path(directory) / name
            require(not child.is_symlink() and not child.is_junction(), f'Input directory link/junction: {child}')
            require(child.resolve(strict=True).is_relative_to(root), f'Input directory escapes tree root: {child}')
        for name in sorted(files):
            path = Path(directory) / name
            if suffixes is None or path.suffix.lower() in suffixes:
                require(path.resolve(strict=True).is_relative_to(root), f'Input file escapes tree root: {path}')
                result.append(path)
    return result


def constant_path(script, name):
    for node in ast.parse(Path(script).read_text(encoding='utf-8-sig')).body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == name for t in node.targets):
            value = node.value
            if isinstance(value, ast.Call) and isinstance(value.func, ast.Name) and value.func.id == 'Path' and len(value.args) == 1:
                return Path(ast.literal_eval(value.args[0])).resolve(strict=True)
    raise ValueError(f'Cannot determine the actual fixed {name} input in {script}')


def arena_inputs(manifest, repo):
    sys.path.insert(0, str(repo / 'tools/extract'))
    from extract_map import load_environment
    from source_plan import validate_manifest_plan
    _, plan = validate_manifest_plan(manifest)
    source = Path(manifest['source']['arena_data_root']).resolve(strict=True)
    # The extractor and BuildSettings reader prefer this otherwise optional file.
    # Require the current standalone layout so its later appearance cannot add an
    # unbound entrypoint while unchanged parents are reused by verification.
    require((source / 'globalgamemanagers').is_file(), 'Input snapshots require the standalone globalgamemanagers entrypoint')
    env, _ = load_environment(source, plan)
    paths = set()
    for key in env.files:
        path = Path(key).resolve(strict=True)
        require(path.is_relative_to(source), f'Arena dependency escapes source root: {path}')
        paths.add(path)
    # Conservative file-level closure: retain every stream used by any resource
    # object in the serialized dependency closure. No pixels/vertices are decoded.
    # This also covers terrain's direct Texture2DArray stream reads, which bypass
    # UnityPy ResourceReader and therefore cannot be captured by patching that API.
    candidates = {p.name.casefold(): p for p in source.iterdir() if p.is_file()}
    for obj in env.objects:
        if obj.type.name not in {'Mesh', 'Texture2D', 'Cubemap', 'Texture3D', 'Texture2DArray'}:
            continue
        data = obj.read()
        stream = getattr(data, 'm_StreamData', None)
        raw = getattr(stream, 'path', '') if stream is not None else ''
        if not raw:
            continue
        # Matches UnityPy ResourceReader's candidate order. The terrain direct
        # reader uses the first exact source-root filename for its two arrays.
        name = ntpath.basename(raw)
        names = (name, name + '.resource', name + '.assets.resS', name + '.resS')
        found = next((candidates[n.casefold()] for n in names if n.casefold() in candidates), None)
        require(found is not None, f'Missing Arena resource stream {raw!r} for {obj.assets_file.name}:{obj.path_id}')
        require(found.name.casefold() == name.casefold(),
                f'Input snapshots require direct resource resolution, not mutable fallback search: {raw!r}')
        require(found.stat().st_size >= int(getattr(stream, 'offset', 0)) + int(getattr(stream, 'size', 0)),
                f'Truncated Arena resource stream: {found}')
        paths.add(found.resolve())
    return sorted(paths)


def managed_inputs(repo):
    script = (repo / 'tools/sync-game-refs.ps1').read_text(encoding='utf-8-sig')
    match = re.search(r'\[string\]\$ManagedDir\s*=\s*"([^"]+)"', script)
    require(match is not None, 'GameRefs source ManagedDir default is not explicit')
    managed = Path(match[1]).resolve(strict=True)
    tool = repo / 'tools/gamerefs-closure/bin/Release/net9.0/GameRefsClosure.dll'
    require(tool.is_file(), 'Existing GameRefs metadata closure tool is required; snapshots never build tools')
    with tempfile.TemporaryDirectory(prefix='arena-port-inputs-') as tmp:
        output = Path(tmp) / 'closure.tsv'
        result = subprocess.run(['dotnet', str(tool), str(managed), 'Assembly-CSharp;Assembly-CSharp-firstpass', str(output)],
                                capture_output=True, text=True, timeout=60)
        require(result.returncode == 0, f'GameRefs source closure failed: {result.stderr.strip()}')
        require('duplicate assembly names' not in result.stderr.lower(), 'Ambiguous GameRefs source assembly identity')
        paths = []
        for line in output.read_text(encoding='utf-8-sig').splitlines():
            _name, raw = line.split('\t', 1)
            path = Path(raw).resolve(strict=True)
            require(path.is_relative_to(managed), f'Managed dependency escapes source: {path}')
            paths.append(path)
    require(paths, 'Empty SPT Managed reference closure')
    # Full metadata closure includes the roots and framework references examined
    # by the real sync script. Mutable fixed-up GameRefs copies are excluded.
    return paths, [tool, tool.with_suffix('.deps.json'), tool.with_suffix('.runtimeconfig.json')]


def repository_inputs(repo):
    paths = []
    for name in ('tools/extract', 'tools/validate', 'tools/bundle', 'tools/preset', 'tools/server', 'analysis/scripts'):
        paths += tree_files(repo / name, {'.py'}, {'__pycache__', 'tests'})
    # ArenaMapPort.Client is runtime plugin code: it never influences extraction or bundle content, and
    # including it invalidated every map receipt on each client change. The staged DLL is checked by
    # the distribution validators instead.
    for name in ('tools/gamerefs-closure', 'tools/gamerefs-fixup', 'ArenaMapPort.Shared', 'ArenaMapPort.Server'):
        paths += tree_files(repo / name, {'.cs', '.csproj', '.props', '.targets', '.config'}, {'bin', 'obj', 'db'})
    paths += list((repo / 'tools').glob('*.ps1'))
    paths += tree_files(repo / 'tools/maps', {'.json', '.props'})
    paths += tree_files(repo / 'tools/extract/plans', {'.json'})
    unity_root = repo / 'UnityProject/Assets/ArenaMapPort'
    generated_folder_meta = {unity_root / 'StandinShaders.meta', unity_root / 'Editor/GameRefs.meta'}
    paths += [path for path in tree_files(unity_root,
              {'.cs', '.shader', '.cginc', '.hlsl', '.asmdef', '.meta'}, {'GameRefs', 'StandinShaders'})
              if path not in generated_folder_meta]
    # Whole hand-authored code/file sets make additions visible too. Generated
    # ArenaImport/StandinShaders and tests/docs are not validation inputs.
    for path in repo.glob('*'):
        if path.is_file() and (path.suffix.lower() in {'.props', '.targets', '.sln', '.slnx'}
                               or path.name.lower().startswith('requirements') or path.name in {'global.json', 'NuGet.Config'}):
            paths.append(path)
    return paths


def discover_groups(package, output, repo=REPO, arena_paths=None):
    package, output, repo = Path(package).resolve(strict=True), Path(output).resolve(strict=True), Path(repo).resolve(strict=True)
    require(not output.is_relative_to(package), 'Snapshot must be outside the package')
    manifest = load(package / 'manifest.json')
    key = manifest.get('source', {}).get('map_key')
    require(isinstance(key, str) and re.fullmatch(r'arena_[a-z0-9]+', key), 'Snapshot requires a generic full-map package')
    require(manifest['source'].get('extractor_script') == 'tools/extract/extract_map.py', 'Snapshot extractor is not generic')
    physics = load(output / 'target-physics.json')
    run = load(output / 'unity-run.json')
    require(run.get('map_key') == key and run.get('exit_code') == 0, 'Missing successful map Unity run provenance')
    log_name = run.get('log_file', '')
    require(log_name and Path(log_name).name == log_name, 'Invalid Unity run log name')
    with (output / log_name).open(encoding='utf-8-sig', errors='strict') as stream:
        log_header = stream.read(65536)
    match = re.search(r'COMMAND LINE ARGUMENTS:\s*\r?\n([^\r\n]+)', log_header)
    require(match is not None, 'Unity log does not identify its actual executable')
    unity_exe = Path(match[1].strip().strip('"')).resolve(strict=True)
    require(unity_exe.name.lower() == 'unity.exe', 'Unexpected Unity executable identity')
    managed, closure_tool = managed_inputs(repo)
    spt = managed + [Path(physics['source_path']),
                    constant_path(repo / 'tools/preset/patch_preset.py', 'DEFAULT_SRC'),
                    constant_path(repo / 'tools/server/make_base_json.py', 'DEFAULT_TEMPLATE')]
    # Cecil is the explicit read-only reference of the GameRefs fixup project.
    fixup = (repo / 'tools/gamerefs-fixup/GameRefsFixup.csproj').read_text(encoding='utf-8-sig')
    spt += [Path(value) for value in re.findall(r'<HintPath>([^<]+)</HintPath>', fixup)]
    settings = tree_files(repo / 'UnityProject/ProjectSettings') + tree_files(repo / 'UnityProject/Packages')
    # Hash the pinned installed package implementation, not generated Library
    # import/cache output. Only packages selected by packages-lock.json apply.
    lock = load(repo / 'UnityProject/Packages/packages-lock.json')['dependencies']
    for name, spec in lock.items():
        installed = repo / 'UnityProject/Library/PackageCache' / f"{name}@{spec['version']}"
        if installed.is_dir():
            settings += tree_files(installed, {'.cs', '.dll', '.asmdef', '.shader', '.cginc', '.hlsl', '.json'},
                                   {'Tests', 'Tests~', 'Documentation~', 'Samples~'})
    groups = {'package': tree_files(package),
              'arena_source': arena_inputs(manifest, repo) if arena_paths is None else arena_paths,
              'spt_source': spt, 'repository_code': repository_inputs(repo), 'unity_settings': settings,
              # Microsoft Store's sys.executable is an unreadable app alias;
              # base_prefix identifies its actual installed interpreter image.
              'toolchain': [unity_exe, Path(sys.base_prefix) / 'python.exe',
                            *Path(sys.base_prefix).glob('python*.dll'), *closure_tool]}
    versions = {}
    for name in ('UnityPy', 'numpy', 'Pillow', 'texture2ddecoder', 'lz4', 'Brotli', 'fsspec',
                 'etcpak', 'astc-encoder-py', 'fmod_toolkit', 'attrs'):
        distribution = importlib.metadata.distribution(name)
        versions[name] = distribution.version
        for entry in distribution.files or []:
            if (Path(entry).suffix.lower() in {'.py', '.pyd', '.dll'}
                    and not {'__pycache__', 'tests', 'docs'}.intersection(part.lower() for part in entry.parts)):
                groups['toolchain'].append(Path(distribution.locate_file(entry)))
    identity = {'map_key': key, 'package': package.as_posix(), 'output': output.as_posix(),
                'python_version': sys.version, 'dependencies': versions}
    return identity, groups


def create_snapshot(package, output, repo=REPO, verified_arena_entries=None):
    arena_paths = None if verified_arena_entries is None else [Path(e['path']) for e in verified_arena_entries]
    identity, paths = discover_groups(package, output, repo, arena_paths=arena_paths)
    groups = {}
    for name, files in sorted(paths.items()):
        entries = (verified_arena_entries if name == 'arena_source' and verified_arena_entries is not None
                   else [file_entry(path) for path in sorted(set(map(Path, files)))])
        require(entries, f'Input group is empty: {name}')
        groups[name] = {'sha256': hashlib.sha256(canonical(entries)).hexdigest(), 'files': entries}
        print(f'INPUT_GROUP {name} files={len(entries)} bytes={sum(e["bytes"] for e in entries)}', flush=True)
    return {'schema_version': SCHEMA, **identity, 'groups': groups}


def compare_snapshots(expected, actual):
    require(expected.get('schema_version') == SCHEMA, 'Legacy/incomplete validation input snapshot rejected')
    for key in ('map_key', 'package', 'output', 'python_version', 'dependencies'):
        require(expected.get(key) == actual.get(key), f'Input identity changed: {key}')
    require(set(expected.get('groups', {})) == set(actual['groups']), 'Validation input group set changed/incomplete')
    for name, fresh in actual['groups'].items():
        old = expected['groups'][name]
        entries = old.get('files', [])
        require(len({e['path'] for e in entries}) == len(entries), f'Duplicate snapshot paths in {name}')
        require(old.get('sha256') == hashlib.sha256(canonical(entries)).hexdigest(), f'Corrupt input group digest: {name}')
        if old != fresh:
            before = {e['path']: e for e in entries}
            after = {e['path']: e for e in fresh['files']}
            missing, added = sorted(before.keys() - after.keys()), sorted(after.keys() - before.keys())
            changed = sorted(path for path in before.keys() & after.keys() if before[path] != after[path])
            raise ValueError(f'Validation inputs changed in {name}: missing={missing[:5]}, added={added[:5]}, changed={changed[:5]}')


def verify_snapshot(package, snapshot, repo=REPO):
    snapshot = Path(snapshot).resolve(strict=True)
    expected = load(snapshot)
    require(expected.get('schema_version') == SCHEMA, 'Legacy/incomplete validation input snapshot rejected')
    group = expected.get('groups', {}).get('arena_source', {})
    entries = group.get('files', [])
    require(entries and len({e['path'] for e in entries}) == len(entries), 'Missing/duplicate Arena source inputs')
    require(group.get('sha256') == hashlib.sha256(canonical(entries)).hexdigest(), 'Corrupt Arena source input digest')
    source = Path(load(Path(package) / 'manifest.json')['source']['arena_data_root']).resolve(strict=True)
    fresh = []
    for item in entries:
        path = Path(item['path']).resolve(strict=True)
        require(path.is_relative_to(source), f'Snapshot Arena input escapes current source root: {path}')
        actual = file_entry(path)
        require(actual == item, f'Validation inputs changed in arena_source: {path}')
        fresh.append(actual)
    # Rehash every source byte, but do not re-decode unchanged serialized parents.
    # Capture proved a complete direct-path closure. New references require changed
    # parent/plan bytes; unreferenced source additions are not consumed inputs.
    # Plan/code/package/settings/toolchain membership is still freshly discovered.
    current = create_snapshot(package, snapshot.parent, repo, verified_arena_entries=fresh)
    compare_snapshots(expected, current)
    return current


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='action', required=True)
    capture = sub.add_parser('capture')
    capture.add_argument('--package', type=Path, required=True)
    capture.add_argument('--output', type=Path, required=True)
    verify = sub.add_parser('verify')
    verify.add_argument('--package', type=Path, required=True)
    verify.add_argument('--snapshot', type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.action == 'capture':
            require(not args.output.exists(), 'Snapshot already exists; archive it before a new validation attempt')
            result = create_snapshot(args.package, args.output.parent)
            with args.output.open('x', encoding='utf-8') as stream:
                json.dump(result, stream, indent=2, ensure_ascii=False)
                stream.write('\n')
        else:
            result = verify_snapshot(args.package, args.snapshot)
        print(f'PORT_INPUTS_OK {args.action} {result["map_key"]}')
    except Exception as exc:
        print(f'PORT_INPUTS_FAILED: {exc}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
