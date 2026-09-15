"""Exact source material slots and explicit source-null collider negative controls."""
from collections import Counter
from contextlib import redirect_stdout, redirect_stderr
from copy import deepcopy
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools/extract'))
sys.path.insert(0, str(REPO / 'tools/validate'))
from common_ids import HardExtractError, content_id
from material_slots import preserve_material_slots
import validate_extract as V


def fixture(slots=(1, None, 2, None)):
    materials = {i: SimpleNamespace(path_id=i, source_file='sharedassets1.assets',
                                   type=SimpleNamespace(name='Material')) for i in (1, 2)}
    pointers = [SimpleNamespace(path_id=value or 0, deref=lambda v=value: materials[v]) for value in slots]
    tree = {'m_GameObject': {'m_FileID': 0, 'm_PathID': 10}}
    reader = SimpleNamespace(path_id=20, source_file='level1', read=lambda: SimpleNamespace(m_Materials=pointers),
                             read_typetree=lambda: tree, get_raw_data=lambda: b'real source renderer fixture')
    ctx = SimpleNamespace(owning_basename=lambda obj: obj.source_file)
    original = {'type': 'MeshRenderer', 'enabled': False, 'cast_shadows': 'ShadowsOnly',
                'material_ids': [content_id('sharedassets1.assets', value, 'Material') for value in slots if value is not None]}
    builder = lambda *args: original
    engine = SimpleNamespace(COMPONENT_BUILDERS={'MeshRenderer': builder, 'Other': object()})
    return reader, ctx, original, engine


def proof_fixture():
    # Independently stated expected source identity and slot list.
    ids = [content_id('sharedassets1.assets', 1, 'Material'), None,
           content_id('sharedassets1.assets', 2, 'Material'), None]
    record = {'schema_version': 1, 'scene': 'Main', 'source_file': 'level1',
              'source_renderer_path_id': 20, 'source_game_object_path_id': 10,
              'source_object_sha256': hashlib.sha256(b'real source renderer fixture').hexdigest(),
              'slot_count': 4, 'null_slots': [1, 3], 'material_ids': ids,
              'source_materials': [{'source_file': 'sharedassets1.assets', 'source_path_id': 1}, None,
                                   {'source_file': 'sharedassets1.assets', 'source_path_id': 2}, None]}
    scenes = {'Main': {'objects': [{'id': '10', 'components': [{'type': 'MeshRenderer', 'material_ids': ids.copy()}]}]}}
    return record, scenes, {value for value in ids if value is not None}


class MaterialSlotTests(unittest.TestCase):
    def test_mixed_slots_keep_exact_positions_fields_and_source_provenance(self):
        reader, ctx, original, engine = fixture()
        previous, other = engine.COMPONENT_BUILDERS.values()
        with preserve_material_slots(engine, {'level1': 'Main'}) as evidence:
            result = engine.COMPONENT_BUILDERS['MeshRenderer'](reader, None, ctx)
        expected, _, _ = proof_fixture()
        self.assertEqual(evidence, [expected])
        self.assertEqual(result, dict(original, material_ids=expected['material_ids']))
        self.assertEqual(len(original['material_ids']), 2)
        self.assertIs(engine.COMPONENT_BUILDERS['MeshRenderer'], previous)
        self.assertIs(engine.COMPONENT_BUILDERS['Other'], other)

    def test_leading_interior_trailing_and_repeated_assignments_keep_indices(self):
        reader, ctx, original, engine = fixture((None, 1, None, 2, None, 1, None))
        with preserve_material_slots(engine, {'level1': 'Main'}) as evidence:
            result = engine.COMPONENT_BUILDERS['MeshRenderer'](reader, None, ctx)
        self.assertEqual(evidence[0]['null_slots'], [0, 2, 4, 6])
        self.assertEqual(result['material_ids'][1], result['material_ids'][5])
        self.assertEqual([v for v in result['material_ids'] if v is not None], original['material_ids'])

    def test_empty_all_null_and_all_assigned_are_exact_legacy_objects_without_provenance(self):
        for slots in ((), (None,), (None, None), (1,), (1, 2, 1)):
            reader, ctx, original, engine = fixture(slots)
            with self.subTest(slots=slots), preserve_material_slots(engine, {'level1': 'Main'}) as evidence:
                self.assertIs(engine.COMPONENT_BUILDERS['MeshRenderer'](reader, None, ctx), original)
                self.assertEqual(evidence, [])

    def test_scope_restores_after_failed_legacy_queue_contract(self):
        reader, ctx, original, engine = fixture()
        original['material_ids'].reverse()
        previous = engine.COMPONENT_BUILDERS['MeshRenderer']
        with self.assertRaisesRegex(HardExtractError, 'queue/order changed'):
            with preserve_material_slots(engine, {'level1': 'Main'}):
                engine.COMPONENT_BUILDERS['MeshRenderer'](reader, None, ctx)
        self.assertIs(engine.COMPONENT_BUILDERS['MeshRenderer'], previous)

    def test_source_validator_accepts_exact_positions(self):
        record, scenes, ids = proof_fixture()
        self.assertEqual(V.material_slot_preservation_errors([record], [record], scenes, ids), [])

    def test_dropped_shifted_wrong_null_and_reassigned_slots_fail(self):
        record, scenes, ids = proof_fixture()
        a, _, b, _ = record['material_ids']
        for wrong in ([a, None, b], [a, b, None, None], [None, None, b, None], [a, None, a, None]):
            changed = deepcopy(scenes)
            changed['Main']['objects'][0]['components'][0]['material_ids'] = wrong
            with self.subTest(wrong=wrong):
                self.assertTrue(V.material_slot_preservation_errors([record], [record], changed, ids))

    def test_source_proof_cannot_be_deleted_or_rewritten_with_the_bad_array(self):
        record, scenes, ids = proof_fixture()
        scenes['Main']['objects'][0]['components'][0]['material_ids'].pop()
        for records in ([], [dict(record, slot_count=3, null_slots=[1], material_ids=record['material_ids'][:-1])],
                        [dict(record, source_object_sha256='0' * 64)], [dict(record, source_game_object_path_id=11)],
                        [record, record]):
            with self.subTest(records=records):
                self.assertTrue(V.material_slot_preservation_errors(records, [record], scenes, ids))

    def test_unproven_null_missing_material_or_missing_renderer_fail(self):
        record, scenes, ids = proof_fixture()
        self.assertTrue(V.material_slot_preservation_errors([], [], scenes, ids))
        self.assertTrue(V.material_slot_preservation_errors([record], [record], scenes, set()))
        scenes['Main']['objects'][0]['components'] = []
        self.assertTrue(V.material_slot_preservation_errors([record], [record], scenes, ids))


class NullColliderTests(unittest.TestCase):
    def validate(self, components, source=Counter({('Main', '10'): 1}), generic=True):
        manifest = {'source': {'extractor_script': 'tools/extract/extract_map.py' if generic else 'tools/extract/extract_equator.py'},
                    'scenes': ['Main'], 'meshes': {'assigned': {}}, 'materials': {}}
        scene = {'scene_name': 'Main', 'objects': [{'id': '10', 'name': 'fixture', 'components': components}]}
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory)
            (package / 'scenes').mkdir()
            (package / 'scenes/Main.json').write_text(json.dumps(scene), encoding='utf-8')
            with patch.object(V, 'FAILURES', []), patch.object(V, 'census_null_mesh_colliders', return_value=source), \
                 redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                V.validate_scenes(manifest, package)
                return list(V.FAILURES)

    def test_only_explicit_live_source_null_is_accepted(self):
        comp = {'type': 'MeshCollider', 'mesh_id': None}
        self.assertEqual(self.validate([comp]), [])
        self.assertTrue(self.validate([comp], Counter()))
        self.assertTrue(self.validate([comp], generic=False))
        self.assertTrue(self.validate([{'type': 'MeshCollider'}]))
        self.assertTrue(self.validate([{'type': 'MeshCollider', 'mesh_id': 'missing'}]))

    def test_null_component_deletion_duplication_and_geometry_substitution_fail(self):
        comp = {'type': 'MeshCollider', 'mesh_id': None}
        for components in ([], [comp, comp], [dict(comp, mesh_id='assigned')]):
            with self.subTest(components=components):
                self.assertTrue(self.validate(components))


if __name__ == '__main__':
    unittest.main()
