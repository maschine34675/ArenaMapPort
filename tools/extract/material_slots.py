"""Preserve null positions in mixed MeshRenderer arrays for generic source plans.

The legacy builder still owns renderer fields and material work queues. Only an
array containing both assigned and null source PPtrs receives the positional
repair and provenance; empty/all-null arrays and legacy entrypoints stay intact.
"""
from contextlib import contextmanager
import hashlib

from common_ids import HardExtractError, content_id


@contextmanager
def preserve_material_slots(engine, scene_by_file):
    previous = engine.COMPONENT_BUILDERS['MeshRenderer']
    evidence = []

    def build(obj, file, ctx):
        result = previous(obj, file, ctx)
        pointers = obj.read().m_Materials
        if not any(p.path_id == 0 for p in pointers) or not any(p.path_id != 0 for p in pointers):
            return result
        basename = ctx.owning_basename(obj)
        if basename not in scene_by_file:
            raise HardExtractError(f'MeshRenderer {basename}:{obj.path_id} is outside selected scenes')
        material_ids, source_materials = [], []
        for pointer in pointers:
            if pointer.path_id == 0:
                material_ids.append(None)
                source_materials.append(None)
                continue
            material = pointer.deref()
            if material.type.name != 'Material':
                raise HardExtractError(f'MeshRenderer {basename}:{obj.path_id} references a non-Material')
            name = ctx.owning_basename(material)
            if name == 'unknown':
                raise HardExtractError(f'MeshRenderer {basename}:{obj.path_id} material has no source file')
            material_ids.append(content_id(name, material.path_id, 'Material'))
            source_materials.append({'source_file': name, 'source_path_id': material.path_id})
        if (not isinstance(result, dict) or result.get('type') != 'MeshRenderer'
                or result.get('material_ids') != [value for value in material_ids if value is not None]):
            raise HardExtractError(f'MeshRenderer {basename}:{obj.path_id} legacy material queue/order changed')
        owner = obj.read_typetree()['m_GameObject']
        if owner.get('m_FileID') != 0 or not owner.get('m_PathID'):
            raise HardExtractError(f'MeshRenderer {basename}:{obj.path_id} has no local GameObject owner')
        result = dict(result, material_ids=material_ids)
        evidence.append({'schema_version': 1, 'scene': scene_by_file[basename], 'source_file': basename,
                         'source_renderer_path_id': obj.path_id,
                         'source_game_object_path_id': owner['m_PathID'],
                         'source_object_sha256': hashlib.sha256(obj.get_raw_data()).hexdigest(),
                         'slot_count': len(material_ids),
                         'null_slots': [i for i, value in enumerate(material_ids) if value is None],
                         'material_ids': material_ids, 'source_materials': source_materials})
        return result

    engine.COMPONENT_BUILDERS['MeshRenderer'] = build
    try:
        yield evidence
    finally:
        engine.COMPONENT_BUILDERS['MeshRenderer'] = previous
