"""Art identity remains safe when an Arena update reuses old path IDs."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
from common_ids import HardExtractError
from named_assets import unique_named_texture


def reader(name, kind="Texture2D"):
    data = SimpleNamespace(m_Name=name)
    return SimpleNamespace(type=SimpleNamespace(name=kind),
                           read_typetree=lambda: {"m_Name": name}, read=lambda: data)


class NamedTextureTests(unittest.TestCase):
    def test_recycled_old_id_does_not_select_wrong_art(self):
        source = SimpleNamespace(objects={3030: reader("GrayscaleRamp"), 3501: reader("Banner")})
        self.assertEqual(unique_named_texture(source, "Banner", "test").m_Name, "Banner")

    def test_missing_and_wrong_type_are_rejected(self):
        for objects in ({}, {1: reader("Banner", "Sprite")}):
            with self.subTest(objects=objects), self.assertRaises(HardExtractError):
                unique_named_texture(SimpleNamespace(objects=objects), "Banner", "test")

    def test_duplicate_texture_names_fail_instead_of_first_match(self):
        source = SimpleNamespace(objects={1: reader("Banner"), 2: reader("Banner")})
        with self.assertRaisesRegex(HardExtractError, "found 2"):
            unique_named_texture(source, "Banner", "test")

    def test_same_name_sprite_does_not_ambiguate_texture(self):
        source = SimpleNamespace(objects={1: reader("Banner"), 2: reader("Banner", "Sprite")})
        self.assertEqual(unique_named_texture(source, "Banner", "test").m_Name, "Banner")
