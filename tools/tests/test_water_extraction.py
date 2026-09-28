"""Synthetic serialization and native mip-chain checks; no BSG data fixtures."""
from pathlib import Path
import math
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_water as W
from common_ids import HardExtractError


def container(**changes):
    values = dict(mip_bias=-1.5, foam_size=.2, border_range=25, blend=.25,
                  texture_id=44, renderer_id=701, filter_id=702, weighted_mode=0,
                  second_key_time=1, pre=2, enabled=1)
    values.update(changes)
    raw = bytearray(struct.pack("<iqB3xiqi", 0, 50, values["enabled"], 1, 60, 0))
    for file_id, path_id in ((3, values["texture_id"]), (4, 45), (4, 46)):
        raw += struct.pack("<iq", file_id, path_id)
    raw += struct.pack("<fiq", values["mip_bias"], 4, 47)
    for blend in (values["blend"], .25, .5, .75, .25):
        raw += struct.pack("<4f", .2, -3, 5, blend)
    raw += struct.pack("<6f", 1, 2, values["border_range"], 3, 4, 5)
    raw += struct.pack("<8f", .1, .2, .3, 1, .4, .5, .6, 1)
    raw += struct.pack("<5f", .5, 10, .25, values["foam_size"], 6)
    raw += struct.pack("<4f", 1, .8, .7, .3)
    raw += struct.pack("<4f", .4, 5, .05, .35)
    raw += struct.pack("<i", 2)
    for time, value in ((0, 0), (values["second_key_time"], 1)):
        raw += struct.pack("<4fi2f", time, value, 0, 0, values["weighted_mode"], 1/3, 1/3)
    raw += struct.pack("<3i", values["pre"], 2, 4)
    raw += struct.pack("<8f", .9, .8, .7, 1, .1, .1, .1, 1)
    raw += struct.pack("<iiqiq", 1, 0, values["renderer_id"], 0, values["filter_id"])
    return bytes(raw)


class WaterExtractionTests(unittest.TestCase):
    def test_full_settings_and_separate_target_refs_survive(self):
        result = W.decode_container(container())
        self.assertEqual(len(container()), 404)
        self.assertEqual(result["texture_pointers"]["_rippleTexture"], {"file_id": 3, "path_id": 44})
        self.assertEqual(result["settings"]["_normalsDetailsMipMapBias"], -1.5)
        self.assertEqual(result["settings"]["_normalsA"]["MovementDirection"], [-3, 5])
        self.assertAlmostEqual(result["settings"]["_depthColorDeep"][2], .3)
        self.assertAlmostEqual(result["settings"]["_foamColor"][3], .3)
        self.assertEqual(result["targets"], [{"renderer": {"file_id": 0, "path_id": 701},
                                            "filter": {"file_id": 0, "path_id": 702}}])
        self.assertEqual(result["settings"]["_reflectionWettingFunc"]["keys"][1]["time"], 1)

    def test_unused_null_target_and_disabled_component_are_preserved(self):
        result = W.decode_container(container(renderer_id=0, filter_id=0, enabled=0))
        self.assertFalse(result["enabled"])
        self.assertEqual(result["targets"][0]["renderer"]["path_id"], 0)

    def test_truncation_and_extra_unknown_fields_fail_closed(self):
        for raw in (container()[:15], container()[:-1], container() + b"\0\0\0\0"):
            with self.subTest(length=len(raw)), self.assertRaises(HardExtractError):
                W.decode_container(raw)

    def test_missing_texture_and_half_target_rejected(self):
        for changes in ({"texture_id": 0}, {"renderer_id": 0}, {"filter_id": 0}, {"texture_id": -1}):
            with self.subTest(changes=changes), self.assertRaises(HardExtractError):
                W.decode_container(container(**changes))

    def test_nonfinite_and_division_by_zero_settings_rejected(self):
        for changes in ({"mip_bias": math.nan}, {"foam_size": 0}, {"border_range": 0},
                        {"blend": .6}, {"foam_size": math.inf}):
            with self.subTest(changes=changes), self.assertRaises(HardExtractError):
                W.decode_container(container(**changes))

    def test_unknown_curve_layout_and_unordered_keys_rejected(self):
        for changes in ({"weighted_mode": 5}, {"second_key_time": 0}, {"pre": 99}):
            with self.subTest(changes=changes), self.assertRaises(HardExtractError):
                W.decode_container(container(**changes))

    def test_complete_compressed_mip_chains_include_small_blocks(self):
        # DXT1's final 2x2 and 1x1 mips still each occupy one 8-byte block.
        self.assertEqual(W.texture_byte_count(8, 8, 4, 10), 32 + 8 + 8 + 8)
        self.assertEqual(W.texture_byte_count(8, 8, 4, 12), 64 + 16 + 16 + 16)
        self.assertEqual(W.texture_byte_count(8, 8, 4, 5), (64 + 16 + 4 + 1) * 4)
        self.assertEqual(W.texture_byte_count(512, 512, 10, 12), 349552)

    def test_invalid_texture_metadata_rejected(self):
        for args in ((0, 512, 10, 12), (512, 512, 11, 12), (512, 512, 0, 12), (512, 512, 10, 99)):
            with self.subTest(args=args), self.assertRaises(HardExtractError):
                W.texture_byte_count(*args)


if __name__ == "__main__":
    unittest.main()
