"""Read the installed SPT collision rules for offline Unity spawn placement."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import UnityPy


def snapshot(source: Path) -> dict:
    source = source.resolve(strict=True)
    env = UnityPy.load(str(source))
    tags = [o.read_typetree() for o in env.objects if o.type.name == 'TagManager']
    physics = [o.read_typetree() for o in env.objects if o.type.name == 'PhysicsManager']
    if len(tags) != 1 or len(physics) != 1:
        raise ValueError('Expected exactly one target TagManager and PhysicsManager')
    layers = tags[0]['layers']
    matrix = physics[0]['m_LayerCollisionMatrix']
    if len(layers) != 32 or len(matrix) != 32 or layers.count('Player') != 1:
        raise ValueError('Target collision matrix or Player layer is missing/ambiguous')
    if any(not isinstance(v, int) or not 0 <= v <= 0xffffffff for v in matrix):
        raise ValueError('Target collision matrix is not 32 unsigned bitmasks')
    player = layers.index('Player')
    mask = matrix[player] & ~(1 << player)
    if not mask:
        raise ValueError('Target Player collision mask is empty')
    return {'schema_version': 1, 'source_path': source.as_posix(),
            'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
            'player_layer': player, 'player_collision_mask': mask,
            'layers': layers, 'layer_collision_matrix': matrix}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path,
                        default=Path('D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers'))
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = snapshot(args.source)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(f'TARGET_PHYSICS_OK player_layer={result["player_layer"]} mask={result["player_collision_mask"]}')
