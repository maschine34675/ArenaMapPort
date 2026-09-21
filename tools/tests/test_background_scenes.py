"""Guard decorative scene selection against missing bundles and gameplay exclusions."""
import copy
import json
from pathlib import Path
import sys
import unittest

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools' / 'validate'))
sys.path.insert(0, str(REPO / 'tools' / 'extract'))
import validate_map_descriptors as validator
import extract_bay5


class BackgroundSceneTests(unittest.TestCase):
    def test_bay5_extraction_and_load_order_include_original_background(self):
        descriptor = json.loads((REPO / 'tools/maps/bay5.json').read_text(encoding='utf-8'))
        selected = extract_bay5.BAY5_SCENE_NAMES
        self.assertEqual('Arena_Bay5_Background', selected[79])
        self.assertEqual([descriptor['scripts_scene_name']] + list(selected.values()), descriptor['all_scene_names'])
        self.assertEqual(list(selected), extract_bay5.ALL_SCENES)

    def test_invalid_background_configuration_is_rejected(self):
        valid = {'scripts_scene_name': 'Scripts', 'all_scene_names': ['Scripts', 'Main', 'Backdrop'],
                 'background_scene_names': ['Backdrop']}
        runtime = {'BackgroundSceneNames': ['Backdrop']}
        self.assertEqual([], validator.background_scene_errors(valid, runtime, {}))
        for invalid in ('Backdrop', None, [None], [''], ['Missing'], ['Scripts'], ['Backdrop', 'Backdrop']):
            changed = copy.deepcopy(valid)
            changed['background_scene_names'] = invalid
            with self.subTest(invalid=invalid):
                self.assertTrue(validator.background_scene_errors(changed, runtime, {}))
        self.assertTrue(validator.background_scene_errors(valid, {}, {}))
        self.assertEqual([], validator.background_scene_errors({}, {}, {}))


if __name__ == '__main__':
    unittest.main()
