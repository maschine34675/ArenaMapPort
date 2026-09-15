import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'extract'))
from texture_compat import bc6h_rgb_decoder
from UnityPy.enums import TextureFormat
from UnityPy.export import Texture2DConverter as converter


class BC6HCompatibilityTests(unittest.TestCase):
    def test_bc6h_block_decodes_without_rgba_mode_error_and_restores_table(self):
        original = converter.CONV_TABLE.copy()
        with bc6h_rgb_decoder():
            image = converter.parse_image_data(bytes(16), 4, 4, TextureFormat.BC6H, (6000,3,6,0), 0, None, True)
            self.assertEqual(image.size, (4,4))
            self.assertEqual(image.convert('RGBA').getpixel((0,0)), (0,0,0,255))
            for key, value in original.items():
                if key != TextureFormat.BC6H:
                    self.assertEqual(converter.CONV_TABLE[key], value)
        self.assertEqual(converter.CONV_TABLE, original)

    def test_decoder_table_restores_even_when_export_fails(self):
        original = converter.CONV_TABLE.copy()
        with self.assertRaisesRegex(RuntimeError, 'export failed'):
            with bc6h_rgb_decoder():
                raise RuntimeError('export failed')
        self.assertEqual(converter.CONV_TABLE, original)


if __name__ == '__main__':
    unittest.main()
