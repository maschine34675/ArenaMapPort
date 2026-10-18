"""Same-named sibling players share one hierarchy path (Bowl level111: two 'indoor_wind' AmbientSoundPlayers under
CommonAmbientInGroup); the exporter and the validator give the later duplicates unique leaves '~2', '~3' ... in source
id order and keep the authored leaf as sourceLeafName. Maps without duplicates are untouched."""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "extract")))
import extract_bay5_audio as audio  # noqa: E402


class SiblingNameTests(unittest.TestCase):
    def test_unique_paths_are_untouched(self):
        paths = {"level1:5": "A/B/wind", "level1:7": "A/B/rain", "level1:9": "A/C/wind"}
        self.assertEqual(audio.disambiguate_sibling_paths(paths), paths)

    def test_duplicates_are_renamed_in_source_id_order_from_two(self):
        paths = {"level1:597": "A/In/indoor_wind", "level1:560": "A/In/indoor_wind", "level1:9": "A/In/cracles"}
        self.assertEqual(audio.disambiguate_sibling_paths(paths),
                         {"level1:560": "A/In/indoor_wind", "level1:597": "A/In/indoor_wind~2", "level1:9": "A/In/cracles"})

    def test_an_ordinal_that_collides_with_an_authored_path_is_skipped(self):
        paths = {"level1:1": "A/x", "level1:2": "A/x", "level1:3": "A/x", "level1:4": "A/x~2"}
        self.assertEqual(audio.disambiguate_sibling_paths(paths),
                         {"level1:1": "A/x", "level1:2": "A/x~3", "level1:3": "A/x~4", "level1:4": "A/x~2"})

    def test_apply_renames_records_and_keeps_the_authored_leaf(self):
        items = [{"id": "level1:2", "path": "A/B/w"}, {"id": "level1:1", "path": "A/B/w"}, {"id": "level1:3", "path": "A/B/v"}]
        self.assertEqual(audio.apply_sibling_paths(items), ["level1:2"])
        self.assertEqual(items[0], {"id": "level1:2", "path": "A/B/w~2", "sourceLeafName": "w"})
        self.assertEqual(items[1], {"id": "level1:1", "path": "A/B/w"})
        self.assertNotIn("sourceLeafName", items[2])


if __name__ == "__main__":
    unittest.main()
