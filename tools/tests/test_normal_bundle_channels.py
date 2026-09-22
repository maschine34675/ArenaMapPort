"""Synthetic decoded-texture fixtures for the packed-normal alpha-loss regression."""
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
import validate_bundle_roundtrip as v


class NormalBundleChannelsTests(unittest.TestCase):
    def inspect(self, mutate=None, source_format="DXT5", color_space="Linear", missing=False,
                duplicate=False, missing_source=False, required=True):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory)
            (package / "textures").mkdir()
            source = Image.new("RGBA", (8, 8))
            source.putdata([(255, 100 + x * 3, 102 + x * 3, 110 + x * 2) for x in range(64)])
            if source_format == "DXT1":
                source.putalpha(255)
            if not missing_source:
                source.save(package / "textures" / "fixture.png")
            texture = SimpleNamespace(m_Name="fixture", m_Width=8, m_Height=8,
                                      m_ColorSpace=0 if color_space == "Linear" else 1,
                                      m_TextureFormat={"DXT5": 12, "Format29": 12, "DXT5Crunched": 12,
                                                       "BC7": 25, "DXT1": 10}[source_format],
                                      image=source.copy())
            if mutate:
                mutate(texture)
            meta = dict(is_normal_map=True, dimension="Tex2D", color_space=color_space,
                        format_src=source_format, width=8, height=8, file="textures/fixture.png")
            expected = {"manifest": {"textures": {"fixture": meta}},
                        "texture_ids_in_scope_by_class": {"Texture2D": {"fixture"} if required else set()}}
            reader = SimpleNamespace(type=SimpleNamespace(name="Texture2D"), path_id=1,
                                     read=lambda: texture)
            # An unrelated built-in dependency must not create a package requirement.
            unrelated = SimpleNamespace(type=SimpleNamespace(name="Texture2D"), path_id=2,
                                        read=lambda: SimpleNamespace(m_Name="Builtin"))
            objects = [] if missing else [reader]
            if duplicate:
                objects.append(reader)
            outcomes = []
            with patch.object(v, "check", side_effect=lambda ok, msg: outcomes.append((ok, msg))):
                v.validate_normal_texture_channels(SimpleNamespace(objects=objects + [unrelated]), expected, package)
            source.close()
            texture.image.close()
            return [message for ok, message in outcomes if not ok]

    def test_preserved_source_formats_and_srgb_cloth_exceptions_pass(self):
        for fmt, space in [("DXT5", "Linear"), ("Format29", "Linear"), ("DXT5Crunched", "Linear"),
                           ("BC7", "Linear"), ("DXT5", "sRGB"), ("DXT1", "sRGB")]:
            with self.subTest(format=fmt, space=space):
                self.assertEqual([], self.inspect(source_format=fmt, color_space=space))

    def test_small_recompression_error_passes(self):
        self.assertEqual([], self.inspect(lambda t: t.image.putalpha(t.image.getchannel("A").point(lambda x: x + 2))))

    def test_original_normal_importer_opaque_alpha_fails(self):
        errors = self.inspect(lambda t: t.image.putalpha(255))
        self.assertTrue(any("alpha collapsed" in error for error in errors))

    def test_channel_swizzle_fails(self):
        def swizzle(texture):
            channels = texture.image.split()
            texture.image = Image.merge("RGBA", (channels[3], channels[1], channels[2], channels[0]))
        self.assertTrue(any("channel differs" in error for error in self.inspect(swizzle)))

    def test_incorrect_color_space_fails_in_both_directions(self):
        self.assertTrue(any("color space" in e for e in self.inspect(lambda t: setattr(t, "m_ColorSpace", 1))))
        self.assertTrue(self.inspect(lambda t: setattr(t, "m_ColorSpace", 0), color_space="sRGB"))

    def test_incorrect_compression_and_dimensions_fail(self):
        self.assertTrue(self.inspect(lambda t: setattr(t, "m_TextureFormat", 10)))
        self.assertTrue(self.inspect(lambda t: setattr(t, "m_Width", 4)))
        self.assertTrue(self.inspect(lambda t: setattr(t, "image", Image.new("RGBA", (4, 4)))))

    def test_missing_required_source_or_built_and_duplicates_fail(self):
        self.assertTrue(self.inspect(missing=True))
        self.assertTrue(self.inspect(missing_source=True))
        self.assertTrue(self.inspect(duplicate=True))

    def test_unreachable_normal_is_not_required(self):
        self.assertEqual([], self.inspect(required=False, missing=True, missing_source=True))


if __name__ == "__main__":
    unittest.main()
