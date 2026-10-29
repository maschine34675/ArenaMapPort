#!/usr/bin/env python3
"""Stage schema-3 source exports as schema-4 map graphs and one shared texture set.

Source exports stay untouched. IDs identify source objects, not image content: a filename
collision with different bytes is an error. Only verified legacy copies in the output are removed.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re

from extract_spectator import MAP_SCENES, REPO, package_path, validate, verify

GRAPH = 'spectator.json.gz'
MANIFEST = 'spectator-textures.json'
IMAGE = re.compile(r'[0-9a-f]{16}(?:-[0-5])?\.png')
OUTPUT = REPO / 'dist/BepInEx/plugins/maschine-ArenaMapPort/spectators'


def plain(path):
    path = Path(path).absolute()
    for node in (path, *path.parents):
        if node.is_symlink() or (node.exists() and (
                getattr(node.stat(), 'st_file_attributes', 0) & 0x400 or
                (node.is_file() and node.stat().st_nlink != 1))):
            raise ValueError(f'linked spectator path: {node}')
    return path.resolve()


def texture_files(data):
    names = set()
    for texture in data['textures'].values():
        if len(texture['files']) not in (1, 6):
            raise ValueError('invalid texture dimension')
        for name in texture['files']:
            if not isinstance(name, str) or not IMAGE.fullmatch(name):
                raise ValueError('invalid spectator texture filename')
            names.add(name)
    return names


def collect(sources):
    graphs, images = {}, {}
    for name, folder in sources.items():
        folder = plain(folder)
        data = json.loads(gzip.decompress(plain(folder / GRAPH).read_bytes()))
        names = texture_files(data)  # Validate leaves before the source verifier opens anything.
        for file in names:
            plain(folder / file)
        verify(folder, f'arena_{name}')
        for file in sorted(names):
            payload = (folder / file).read_bytes()
            if file in images and images[file] != payload:
                raise ValueError(f'spectator texture collision: {file} ({name})')
            images[file] = payload
        data['schema'] = 4
        graphs[f'arena_{name}'] = data
    manifest = dict(schema=1, files=[dict(file=name, bytes=len(payload), sha256=hashlib.sha256(payload).hexdigest())
                                   for name, payload in sorted(images.items())])
    encoded = (json.dumps(manifest, indent=2) + '\n').encode()
    for key, data in graphs.items():
        data['texture_manifest_sha256'] = hashlib.sha256(encoded).hexdigest()
        graphs[key] = gzip.compress(json.dumps(data, separators=(',', ':'), allow_nan=False).encode(), mtime=0)
    return graphs, images, encoded


def validate_staged(output, sources=None):
    output = plain(output)
    expected_maps = {f'arena_{name}' for name in MAP_SCENES}
    if {p.name for p in output.iterdir()} != expected_maps | {'shared'}:
        raise ValueError('unexpected or missing spectator folder')
    shared = plain(output / 'shared')
    manifest_bytes = plain(shared / MANIFEST).read_bytes()
    manifest = json.loads(manifest_bytes)
    if manifest.get('schema') != 1 or not isinstance(manifest.get('files'), list):
        raise ValueError('unsupported shared spectator manifest')
    names = set()
    from PIL import Image
    for row in manifest['files']:
        name = row['file']
        if not IMAGE.fullmatch(name) or name in names:
            raise ValueError('invalid or duplicate shared texture')
        payload = plain(shared / name).read_bytes()
        if len(payload) != row['bytes'] or hashlib.sha256(payload).hexdigest() != row['sha256']:
            raise ValueError(f'damaged shared spectator texture: {name}')
        with Image.open(shared / name) as image:
            image.verify()
        names.add(name)
    if {p.name for p in shared.iterdir()} != names | {MANIFEST}:
        raise ValueError('unexpected shared texture file')
    used = set()
    for key in expected_maps:
        folder = plain(output / key)
        if {p.name for p in folder.iterdir()} != {GRAPH}:
            raise ValueError('legacy or unexpected spectator map file')
        data = json.loads(gzip.decompress(plain(folder / GRAPH).read_bytes()))
        if data['schema'] != 4:
            raise ValueError('expected shared spectator schema 4')
        if data.get('texture_manifest_sha256') != hashlib.sha256(manifest_bytes).hexdigest():
            raise ValueError('map and shared spectator manifest mismatch')
        data['schema'] = 3  # Same source graph; only the texture storage contract changed.
        validate(data, key)
        used.update(texture_files(data))
    if used != names:
        raise ValueError('missing or unreferenced shared texture')
    if sources is not None:
        graphs, images, encoded = collect(sources)
        if (shared / MANIFEST).read_bytes() != encoded:
            raise ValueError('stale shared texture manifest')
        for key, payload in graphs.items():
            if (output / key / GRAPH).read_bytes() != payload:
                raise ValueError(f'stale spectator graph: {key}')
        for name, payload in images.items():
            if (shared / name).read_bytes() != payload:
                raise ValueError(f'stale shared texture: {name}')
    return len(names)


def stage(sources, output):
    if set(sources) != set(MAP_SCENES):
        raise ValueError('all four spectator exports are required')
    graphs, images, manifest = collect(sources)
    output = plain(output)
    if any(output.is_relative_to(plain(p)) or plain(p).is_relative_to(output) for p in sources.values()):
        raise ValueError('staging must not overlap source exports')
    if output.exists() and {p.name for p in output.iterdir()} - (set(graphs) | {'shared'}):
        raise ValueError('unexpected spectator output folder')
    legacy = []
    # Preflight every existing output before any write or deletion.
    for folder, allowed in [(output / key, {GRAPH} | set(images)) for key in graphs] + [
            (output / 'shared', {MANIFEST} | set(images))]:
        plain(folder)
        if folder.exists():
            for file in folder.iterdir():
                plain(file)
                if not file.is_file() or file.name not in allowed:
                    raise ValueError(f'unexpected spectator output: {file}')
                if folder.name != 'shared' and file.name != GRAPH:
                    if file.read_bytes() != images[file.name]:
                        raise ValueError(f'legacy spectator texture differs: {file}')
                    legacy.append(file)
    shared = output / 'shared'
    shared.mkdir(parents=True, exist_ok=True)
    for name, payload in images.items():
        (shared / name).write_bytes(payload)
    (shared / MANIFEST).write_bytes(manifest)
    for key, payload in graphs.items():
        (output / key).mkdir(parents=True, exist_ok=True)
        (output / key / GRAPH).write_bytes(payload)
    for file in legacy:
        plain(file)
        if not file.resolve().is_relative_to(output.resolve()) or file.read_bytes() != images[file.name]:
            raise ValueError('legacy texture changed during staging')
        file.unlink()
    return validate_staged(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['stage', 'verify'])
    parser.add_argument('--output', type=Path, default=OUTPUT)
    args = parser.parse_args()
    sources = {name: package_path(name) for name in MAP_SCENES}
    count = stage(sources, args.output) if args.action == 'stage' else validate_staged(args.output, sources)
    print(f'SHARED_SPECTATOR_PACKAGE_OK {count} unique textures, {len(sources)} maps')
