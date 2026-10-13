"""Export Arena's original region-map texture, without altering its pixels.

The 1080-square texture contains the same geographic 800-square window used by
SPT's region_map_external. The client selects that window with Sprite.Create;
stretching the full Arena texture would displace every native location marker.
Artwork and generated metadata stay in ignored work/dist, never in Git.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys

DEFAULT_SOURCE = Path('D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data')
DEFAULT_OUTPUT = Path(__file__).resolve().parents[2] / 'work/extract/map-selection'
TEXTURE_NAME = 'Main_Map_Screen'
FILENAME = 'region-map.png'
MANIFEST = 'manifest.json'
TEXTURE_SIZE = (1080, 1080)
# Texture coordinates are bottom-up; PIL's alpha bounds are top-down.
SPRITE_RECT = (140, 280, 800, 800)
ALPHA_BOUNDS = (140, 0, 940, 800)
# Pin decoded RGBA pixels, independent of PNG encoder/version. A future Arena
# artwork revision requires a new geographic-alignment review, even at the same size.
REVIEWED_PIXEL_SHA256 = '6d0ff193b926226768a03a2f24dc11cc0d21b9927ddc2e356a65f7e7205df1f0'


def check_image(image):
    if image.mode != 'RGBA' or image.size != TEXTURE_SIZE:
        raise ValueError('Selection map texture must be the reviewed 1080-square RGBA image')
    if image.getchannel('A').getbbox() != ALPHA_BOUNDS:
        raise ValueError('Selection map framing changed; review alignment before exporting')
    if hashlib.sha256(image.tobytes()).hexdigest() != REVIEWED_PIXEL_SHA256:
        raise ValueError('Selection map artwork changed; recheck geographic alignment before exporting')


def named_texture(objects):
    matches = [obj.read() for obj in objects if obj.type.name == 'Texture2D'
               and obj.peek_name() == TEXTURE_NAME]
    if len(matches) != 1:
        raise ValueError(f'Expected exactly one {TEXTURE_NAME} texture, found {len(matches)}')
    return matches[0]


def validate(output):
    from PIL import Image
    output = Path(output)
    manifest = json.loads((output / MANIFEST).read_text(encoding='utf-8'))
    if (manifest.get('schema_version') != 1 or manifest.get('file') != FILENAME
            or manifest.get('texture_name') != TEXTURE_NAME
            or manifest.get('texture_size') != list(TEXTURE_SIZE)
            or manifest.get('sprite_rect') != list(SPRITE_RECT)):
        raise ValueError('Selection map manifest has an unsupported identity or layout')
    image_bytes = (output / FILENAME).read_bytes()
    if hashlib.sha256(image_bytes).hexdigest() != manifest.get('sha256'):
        raise ValueError('Selection map PNG does not match its manifest')
    with Image.open(output / FILENAME) as image:
        check_image(image)
    if {p.name for p in output.iterdir()} != {FILENAME, MANIFEST}:
        raise ValueError('Unexpected selection map payload')
    return manifest


def extract(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    # Never write into an Arena install, even when an alternate source is used.
    for protected in (source, DEFAULT_SOURCE.resolve().parent):
        if output == protected or protected in output.parents:
            raise ValueError('Selection map output must be outside the Arena source')
    import UnityPy
    source_file = source / 'sharedassets1.assets'
    env = UnityPy.load(str(source_file))
    texture = named_texture(env.objects)
    image = texture.image
    check_image(image)
    output.mkdir(parents=True, exist_ok=True)
    image.save(output / FILENAME)
    manifest = {'schema_version': 1, 'file': FILENAME, 'texture_name': TEXTURE_NAME,
                'texture_size': list(TEXTURE_SIZE), 'sprite_rect': list(SPRITE_RECT),
                'source_file': source_file.name,
                'sha256': hashlib.sha256((output / FILENAME).read_bytes()).hexdigest()}
    (output / MANIFEST).write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    validate(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=DEFAULT_SOURCE)
    parser.add_argument('--output', type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    if args.check:
        validate(args.output)
    else:
        extract(args.source, args.output)
    print('SELECTION_MAP_OK')


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        print(f'SELECTION_MAP_FAILED: {exc}', file=sys.stderr)
        sys.exit(1)
