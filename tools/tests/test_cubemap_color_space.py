"""Reject the observed source-Linear/built-sRGB reflection-map mismatch."""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
import validate_bundle_roundtrip as validator


class CubemapColorSpaceTests(unittest.TestCase):
    def inspect(self, cubes, source=None):
        source = source or {"linear": "Linear", "encoded": "sRGB"}
        expected = {"texture_ids_in_scope_by_class": {"Cubemap": set(source)},
                    "manifest": {"textures": {key: {"color_space": space} for key, space in source.items()}}}
        objects = [SimpleNamespace(type=SimpleNamespace(name="Cubemap"), path_id=index,
                                  read_typetree=lambda k=key, s=space: {"m_Name": k, "m_ColorSpace": s})
                   for index, (key, space) in enumerate(cubes)]
        outcomes = []
        with patch.object(validator, "check", side_effect=lambda ok, message: outcomes.append((ok, message))):
            validator.validate_cubemap_color_space(SimpleNamespace(objects=objects), expected)
        return [message for ok, message in outcomes if not ok]

    def test_mixed_source_spaces_and_unrelated_builtin_pass(self):
        self.assertEqual([], self.inspect([("linear", 0), ("encoded", 1), ("BuiltinSky", 1)]))

    def test_original_forced_srgb_bug_fails(self):
        self.assertTrue(any("preserves source Linear" in e for e in self.inspect([("linear", 1), ("encoded", 1)])))

    def test_srgb_must_not_become_linear(self):
        self.assertTrue(self.inspect([("linear", 0), ("encoded", 0)]))

    def test_missing_and_duplicate_package_cubes_fail(self):
        self.assertTrue(self.inspect([("linear", 0)]))
        self.assertTrue(self.inspect([("linear", 0), ("linear", 0), ("encoded", 1)]))

    def test_unknown_source_or_missing_built_flag_fails(self):
        self.assertTrue(self.inspect([("linear", 0)], {"linear": "Unknown(2)"}))
        self.assertTrue(self.inspect([("linear", None), ("encoded", 1)]))


if __name__ == "__main__":
    unittest.main()
