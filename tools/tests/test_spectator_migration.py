"""Exercise the sanctioned migration in temporary fake installations only."""
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]
PLUGIN = Path('BepInEx/plugins/maschine-ArenaMapPort')
MAPS = ['arena_airpit', 'arena_equator', 'arena_bay5', 'arena_bowl']


class SpectatorMigration(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.script = self.root / 'tools/deploy-to-spt.ps1'
        self.script.parent.mkdir()
        shutil.copy2(REPO / 'tools/deploy-to-spt.ps1', self.script)
        self.install = self.root / 'game'
        staged = self.root / 'dist' / PLUGIN
        shared = staged / 'spectators/shared'
        shared.mkdir(parents=True)
        self.image = '0123456789abcdef.png'
        self.payload = b'identical synthetic texture'
        (staged / 'maschine-ArenaMapPort.Client.dll').write_bytes(b'candidate DLL')
        (shared / self.image).write_bytes(self.payload)
        (shared / 'spectator-textures.json').write_text(json.dumps(dict(schema=1, files=[dict(
            file=self.image, bytes=len(self.payload), sha256=hashlib.sha256(self.payload).hexdigest())])))
        for key in MAPS:
            folder = staged / 'spectators' / key
            folder.mkdir()
            (folder / 'spectator.json.gz').write_bytes(gzip.compress(json.dumps(dict(schema=4, map=key)).encode()))
        shutil.copytree(staged, self.install / PLUGIN)
        self.legacy = [self.install / PLUGIN / 'spectators' / key / self.image for key in MAPS]
        for file in self.legacy:
            file.write_bytes(self.payload)
        self.unrelated = self.install / 'BepInEx/plugins/other/settings.txt'
        self.unrelated.parent.mkdir()
        self.unrelated.write_text('preserve me')

    def run_migration(self, *args):
        return subprocess.run(['pwsh', '-NoProfile', '-File', str(self.script), '-SptRoot', str(self.install),
                               '-RemoveLegacySpectatorTextures', *args], capture_output=True, text=True)

    def test_dry_run_confirm_and_idempotence(self):
        result = self.run_migration()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(all(p.exists() for p in self.legacy))
        result = self.run_migration('-Confirm')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('removed=4', result.stdout)
        self.assertTrue(all(not p.exists() for p in self.legacy))
        self.assertEqual(self.unrelated.read_text(), 'preserve me')
        self.assertEqual((self.install / PLUGIN / 'spectators/shared' / self.image).read_bytes(), self.payload)
        result = self.run_migration('-Confirm')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('removed=0', result.stdout)

    def test_modified_legacy_refused_before_removal(self):
        self.legacy[-1].write_bytes(b'custom file')
        self.assertNotEqual(self.run_migration('-Confirm').returncode, 0)
        self.assertTrue(all(p.exists() for p in self.legacy))

    def test_missing_replacement_refused_before_removal(self):
        (self.install / PLUGIN / 'spectators/shared' / self.image).unlink()
        self.assertNotEqual(self.run_migration('-Confirm').returncode, 0)
        self.assertTrue(all(p.exists() for p in self.legacy))

    def test_unknown_file_and_combined_modes_refused(self):
        (self.legacy[0].parent / 'notes.txt').write_text('preserve me')
        self.assertNotEqual(self.run_migration('-Confirm').returncode, 0)
        self.assertNotEqual(self.run_migration('-Confirm', '-DllOnly').returncode, 0)
        self.assertTrue(all(p.exists() for p in self.legacy))


if __name__ == '__main__':
    unittest.main()
