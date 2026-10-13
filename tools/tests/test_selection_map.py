"""Selection art rejects ambiguous sources, framing drift and damaged packages."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import extract_selection_map as selection


class SelectionMapTests(unittest.TestCase):
    def setUp(self):
        fixture_hash = hashlib.sha256(self.image().tobytes()).hexdigest()
        self.pin = patch.object(selection, 'REVIEWED_PIXEL_SHA256', fixture_hash)
        self.pin.start()
        self.addCleanup(self.pin.stop)

    def image(self):
        image = Image.new('RGBA', selection.TEXTURE_SIZE)
        image.paste((120, 110, 100, 255), selection.ALPHA_BOUNDS)
        return image

    def package(self, folder):
        self.image().save(folder / selection.FILENAME)
        manifest = {'schema_version': 1, 'file': selection.FILENAME,
                    'texture_name': selection.TEXTURE_NAME,
                    'texture_size': list(selection.TEXTURE_SIZE),
                    'sprite_rect': list(selection.SPRITE_RECT),
                    'sha256': hashlib.sha256((folder / selection.FILENAME).read_bytes()).hexdigest()}
        (folder / selection.MANIFEST).write_text(json.dumps(manifest))
        return manifest

    def test_native_padding_is_excluded_by_bottom_up_sprite_rectangle(self):
        image = self.image()
        selection.check_image(image)
        x, y, width, height = selection.SPRITE_RECT
        self.assertEqual((x, image.height - y - height, x + width, image.height - y),
                         image.getchannel('A').getbbox())

    def test_changed_framing_is_rejected(self):
        image = self.image()
        image.putpixel((139, 400), (10, 20, 30, 255))
        with self.assertRaisesRegex(ValueError, 'framing changed'):
            selection.check_image(image)

    def test_changed_artwork_with_identical_framing_is_rejected(self):
        image = self.image()
        image.putpixel((300, 400), (11, 22, 33, 255))
        with self.assertRaisesRegex(ValueError, 'artwork changed'):
            selection.check_image(image)

    def test_dimensions_and_missing_alpha_are_rejected(self):
        for image in (Image.new('RGBA', (800, 800)), self.image().convert('RGB')):
            with self.assertRaisesRegex(ValueError, '1080-square RGBA'):
                selection.check_image(image)

    def test_named_texture_requires_exactly_one_match(self):
        data = object()
        obj = SimpleNamespace(type=SimpleNamespace(name='Texture2D'),
                              peek_name=lambda: selection.TEXTURE_NAME, read=lambda: data)
        self.assertIs(selection.named_texture([obj]), data)
        for objects in ([], [obj, obj]):
            with self.assertRaisesRegex(ValueError, 'exactly one'):
                selection.named_texture(objects)

    def test_complete_package_then_wrong_hash_are_distinguished(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            self.package(folder)
            selection.validate(folder)
            with (folder / selection.FILENAME).open('ab') as f:
                f.write(b'changed')
            with self.assertRaisesRegex(ValueError, 'does not match'):
                selection.validate(folder)

    def test_manifest_cannot_redirect_texture_or_sprite_window(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            good = self.package(folder)
            for key, value in (('file', '../other.png'), ('sprite_rect', [0, 0, 800, 800])):
                manifest = dict(good, **{key: value})
                (folder / selection.MANIFEST).write_text(json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, 'unsupported identity or layout'):
                    selection.validate(folder)

    def test_extra_payload_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            self.package(folder)
            (folder / 'unused.bin').write_bytes(b'not shipped')
            with self.assertRaisesRegex(ValueError, 'Unexpected selection map payload'):
                selection.validate(folder)

    def test_cannot_export_into_source_install(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(ValueError, 'outside the Arena source'):
                selection.extract(tmp, Path(tmp) / 'output')


if __name__ == '__main__':
    result = unittest.main(exit=False).result
    if result.wasSuccessful():
        print('SELECTION_MAP_TESTS_PASSED')
    sys.exit(0 if result.wasSuccessful() else 1)
