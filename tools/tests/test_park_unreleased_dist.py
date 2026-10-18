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
        self.client = self.root / 'dist/BepInEx/plugins/maschine-ArenaMapPort'
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
        # Ten parked maps: Bowl left the list with PLAN-BOWL item 8 (2026-10-18).
        for stem in ('airpit2', 'autoservice', 'prison', 'railwaystation',
                     'yard', 'iceberg', 'factory', 'custom', 'shoreline', 'woods'):
            for path in [*(self.client / f'bundles/arena_{stem}_{kind}.bundle' for kind in ('scenes', 'navmesh', 'preset')),
                         self.client / f'material-shaders.arena_{stem}.json', self.server / f'db/arena_{stem}/base.json']:
                data = str(path.relative_to(self.root)).encode()
                self.put(path, data)
                expected[os.path.normcase(os.path.realpath(path))] = hashlib.sha256(data).hexdigest().upper()
        # The four released maps (Equator, Bay5, AirPit, Bowl) are never archived.
        preserved = [self.client / 'bundles/arena_equator_scenes.bundle', self.client / 'bundles/arena_bay5_scenes.bundle',
                     self.client / 'bundles/arena_airpit_scenes.bundle', self.client / 'material-shaders.arena_airpit.json',
                     self.client / 'bundles/unknown.bundle', self.server / 'db/arena_equator/base.json',
                     self.server / 'db/arena_airpit/base.json',
                     self.client / 'bundles/arena_bowl_scenes.bundle', self.client / 'bundles/arena_bowl_navmesh.bundle',
                     self.client / 'bundles/arena_bowl_preset.bundle', self.client / 'material-shaders.arena_bowl.json',
                     self.server / 'db/arena_bowl/base.json',
                     self.root / 'work/build/bowl/arena_bowl_scenes.bundle', self.root / 'ArenaMapPort.Server/db/arena_bowl/base.json']
        for path in preserved:
            self.put(path, b'preserved')
        self.run_script('Client'); self.run_script('Server')
        manifests = list((self.root / 'work/release-parked').rglob('manifest.json'))
        self.assertEqual(2, len(manifests))
        rows = [row for path in manifests for row in json.loads(path.read_text(encoding='utf-8-sig'))]
        self.assertEqual(50, len(rows))
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
        first = self.client / 'bundles/arena_prison_scenes.bundle'
        self.put(first)
        (self.client / 'material-shaders.arena_woods.json').mkdir()
        self.run_script('Client', success=False)
        self.assertTrue(first.exists())
        self.assertFalse((self.root / 'work/release-parked').exists())

    def test_unused_exports_and_legacy_db_are_archived_but_inputs_survive(self):
        removed = [self.client/'arena-audio/commentator/test.ogg', self.client/'arena-ui/icons/test.png',
                   self.server/'db/base.json']
        kept = self.root/'work/extract/arena-audio/commentator/test.ogg'
        for path in [*removed, kept]:
            self.put(path, b'original')
        self.run_script('Client'); self.run_script('Server')
        self.assertTrue(all(not p.exists() for p in removed))
        self.assertEqual(b'original', kept.read_bytes())
        archived = [p for p in (self.root/'work/release-parked').rglob('*') if p.is_file() and p.name != 'manifest.json']
        self.assertEqual(3, len(archived))
        self.assertTrue(all(p.read_bytes() == b'original' for p in archived))

    def test_linked_ancestor_is_rejected(self):
        outside = self.root / 'outside'
        outside.mkdir()
        self.put(outside / 'arena_prison_scenes.bundle')
        try:
            os.symlink(outside, self.client / 'bundles', target_is_directory=True)
        except OSError as error:
            self.skipTest(f'Symlink creation unavailable: {error}')
        self.run_script('Client', success=False)
        self.assertTrue((outside / 'arena_prison_scenes.bundle').exists())
        self.assertFalse((self.root / 'work/release-parked').exists())


if __name__ == '__main__':
    unittest.main()
