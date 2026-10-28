"""Synthetic images test shared storage; no game assets in these fixtures."""
import hashlib
import gzip
import json
from pathlib import Path
import sys
import tempfile
import unittest
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import stage_spectators as S
from test_spectator import fixture


class SharedSpectators(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.sources = {name: self.root / name for name in S.MAP_SCENES}
        self.output = self.root / 'staged'
        self.image = '0123456789abcdef.png'
        for name, folder in self.sources.items():
            folder.mkdir()
            data = fixture()
            data.update(map=f'arena_{name}', source_scene=S.MAP_SCENES[name])
            data['textures'] = {'t': dict(files=[self.image], linear=False, wrap=0, filter=1)}
            data['materials']['m']['textures'] = {'_MainTex': dict(id='t')}
            (folder / S.GRAPH).write_bytes(gzip.compress(json.dumps(data).encode(), mtime=0))
            Image.new('RGBA', (2, 2), 'red').save(folder / self.image)

    def test_four_maps_share_one_identical_image_and_preserve_sources(self):
        before = {p: p.read_bytes() for f in self.sources.values() for p in f.iterdir()}
        self.assertEqual(S.stage(self.sources, self.output), 1)
        self.assertEqual(len(list(self.output.rglob('*.png'))), 1)
        self.assertEqual((self.output / 'shared' / self.image).read_bytes(), before[self.sources['airpit'] / self.image])
        for key in S.MAP_SCENES:
            data = json.loads(gzip.decompress((self.output / f'arena_{key}' / S.GRAPH).read_bytes()))
            original = json.loads(gzip.decompress(before[self.sources[key] / S.GRAPH]))
            original['schema'] = 4
            original['texture_manifest_sha256'] = hashlib.sha256(
                (self.output / 'shared' / S.MANIFEST).read_bytes()).hexdigest()
            self.assertEqual(data, original)
        self.assertEqual(before, {p: p.read_bytes() for p in before})
        S.validate_staged(self.output, self.sources)

    def test_collision_fails_before_any_output_write(self):
        Image.new('RGBA', (2, 2), 'blue').save(self.sources['bowl'] / self.image)
        with self.assertRaisesRegex(ValueError, 'collision'):
            S.stage(self.sources, self.output)
        self.assertFalse(self.output.exists())

    def test_existing_legacy_copies_removed_only_after_identical_replacement(self):
        import shutil
        for name, folder in self.sources.items():
            shutil.copytree(folder, self.output / f'arena_{name}')
        S.stage(self.sources, self.output)
        self.assertEqual(len(list(self.output.rglob('*.png'))), 1)
        first = {p.relative_to(self.output): p.read_bytes() for p in self.output.rglob('*') if p.is_file()}
        S.stage(self.sources, self.output)
        self.assertEqual(first, {p.relative_to(self.output): p.read_bytes() for p in self.output.rglob('*') if p.is_file()})

    def test_unknown_or_changed_legacy_file_is_preserved_and_refused(self):
        folder = self.output / 'arena_airpit'
        folder.mkdir(parents=True)
        for name in ['notes.txt', self.image]:
            file = folder / name
            file.write_bytes(b'keep me')
            with self.assertRaises(ValueError):
                S.stage(self.sources, self.output)
            self.assertEqual(file.read_bytes(), b'keep me')
            self.assertFalse((self.output / 'shared').exists())
            file.unlink()

    def test_source_path_traversal_and_incomplete_set_refused(self):
        folder = self.sources['airpit']
        data = json.loads(gzip.decompress((folder / S.GRAPH).read_bytes()))
        data['textures']['t']['files'] = ['../outside.png']
        (folder / S.GRAPH).write_bytes(gzip.compress(json.dumps(data).encode()))
        with self.assertRaisesRegex(ValueError, 'filename'):
            S.stage(self.sources, self.output)
        with self.assertRaisesRegex(ValueError, 'all four'):
            S.stage({'airpit': folder}, self.output)

    def test_missing_corrupt_extra_or_old_schema_staged_data_refused(self):
        S.stage(self.sources, self.output)
        image = self.output / 'shared' / self.image
        payload = image.read_bytes()
        image.write_bytes(b'bad')
        with self.assertRaisesRegex(ValueError, 'damaged'):
            S.validate_staged(self.output)
        image.unlink()
        with self.assertRaises(FileNotFoundError):
            S.validate_staged(self.output)
        image.write_bytes(payload)
        extra = self.output / 'arena_airpit' / self.image
        extra.write_bytes(payload)
        with self.assertRaisesRegex(ValueError, 'legacy'):
            S.validate_staged(self.output)
        extra.unlink()
        graph = self.output / 'arena_airpit' / S.GRAPH
        graph.write_bytes((self.sources['airpit'] / S.GRAPH).read_bytes())
        with self.assertRaisesRegex(ValueError, 'schema 4'):
            S.validate_staged(self.output)

    def test_output_must_not_overwrite_source(self):
        with self.assertRaisesRegex(ValueError, 'overlap'):
            S.stage(self.sources, self.root)
        with self.assertRaisesRegex(ValueError, 'overlap'):
            S.stage(self.sources, self.sources['airpit'] / '..' / 'airpit' / 'nested')

    def test_graph_rejects_an_individually_valid_but_different_shared_manifest(self):
        S.stage(self.sources, self.output)
        manifest = self.output / 'shared' / S.MANIFEST
        manifest.write_bytes(manifest.read_bytes() + b'\n')
        with self.assertRaisesRegex(ValueError, 'manifest mismatch'):
            S.validate_staged(self.output)

    def test_linked_output_refused(self):
        target = self.root / 'elsewhere'
        target.mkdir()
        try:
            self.output.symlink_to(target, target_is_directory=True)
        except OSError:
            self.skipTest('symlink creation unavailable')
        with self.assertRaisesRegex(ValueError, 'linked'):
            S.stage(self.sources, self.output)
        self.assertEqual(list(target.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
