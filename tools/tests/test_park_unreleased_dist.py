"""Exercise release cleanup only inside disposable repository fixtures."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell required')
class ParkedDistributionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / 'tools').mkdir()
        self.script = self.root / 'tools/park-unreleased-dist.ps1'
        shutil.copy2(REPO / 'tools/park-unreleased-dist.ps1', self.script)
        self.client = self.root / 'dist/BepInEx/plugins/ArenaMapPort'
        self.server = self.root / 'dist/SPT_Runtime/user/mods/ArenaMapPort'
        self.client.mkdir(parents=True)
        self.server.mkdir(parents=True)

    def run_script(self, scope, success=True):
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(self.script), '-Scope', scope],
                                capture_output=True, text=True, encoding='utf-8')
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
        return result.stdout

    @staticmethod
    def put(path, content=b'fixture'):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def test_both_scopes_archive_exact_outputs_preserve_hashes_and_are_idempotent(self):
        expected = {}
        for stem in ('airpit', 'airpit2', 'autoservice', 'bowl', 'prison', 'railwaystation',
                     'yard', 'iceberg', 'factory', 'custom', 'shoreline', 'woods'):
            for path in [*(self.client / f'bundles/arena_{stem}_{kind}.bundle' for kind in ('scenes', 'navmesh', 'preset')),
                         self.client / f'material-shaders.arena_{stem}.json', self.server / f'db/arena_{stem}/base.json']:
                data = str(path.relative_to(self.root)).encode()
                self.put(path, data)
                expected[os.path.normcase(os.path.realpath(path))] = hashlib.sha256(data).hexdigest().upper()
        preserved = [self.client / 'bundles/arena_equator_scenes.bundle', self.client / 'bundles/arena_bay5_scenes.bundle',
                     self.client / 'bundles/unknown.bundle', self.server / 'db/base.json',
                     self.root / 'work/build/bowl/arena_bowl_scenes.bundle', self.root / 'ArenaMapPort.Server/db/arena_bowl/base.json']
        for path in preserved:
            self.put(path, b'preserved')
        self.run_script('Client'); self.run_script('Server')
        manifests = list((self.root / 'work/release-parked').rglob('manifest.json'))
        self.assertEqual(2, len(manifests))
        rows = [row for path in manifests for row in json.loads(path.read_text(encoding='utf-8-sig'))]
        self.assertEqual(60, len(rows))
        for row in rows:
            self.assertTrue(row['Moved'])
            self.assertFalse(Path(row['Source']).exists())
            self.assertEqual(expected[os.path.normcase(os.path.realpath(row['Source']))], row['Sha256'])
            self.assertEqual(expected[os.path.normcase(os.path.realpath(row['Source']))], hashlib.sha256(Path(row['Destination']).read_bytes()).hexdigest().upper())
        self.assertIn('0 files', self.run_script('Client'))
        self.assertIn('0 files', self.run_script('Server'))
        self.assertEqual(manifests, list((self.root / 'work/release-parked').rglob('manifest.json')))
        for path in preserved:
            self.assertEqual(b'preserved', path.read_bytes())

    def test_complete_plan_is_checked_before_moving_first_file(self):
        first = self.client / 'bundles/arena_airpit_scenes.bundle'
        self.put(first)
        (self.client / 'material-shaders.arena_woods.json').mkdir()
        self.run_script('Client', success=False)
        self.assertTrue(first.exists())
        self.assertFalse((self.root / 'work/release-parked').exists())

    def test_linked_ancestor_is_rejected(self):
        outside = self.root / 'outside'
        outside.mkdir()
        self.put(outside / 'arena_airpit_scenes.bundle')
        try:
            os.symlink(outside, self.client / 'bundles', target_is_directory=True)
        except OSError as error:
            self.skipTest(f'Symlink creation unavailable: {error}')
        self.run_script('Client', success=False)
        self.assertTrue((outside / 'arena_airpit_scenes.bundle').exists())
        self.assertFalse((self.root / 'work/release-parked').exists())


if __name__ == '__main__':
    unittest.main()
