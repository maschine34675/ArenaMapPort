"""Positive current binaries and a deliberate stale-DLL negative control."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]
FILES = ['ArenaMapPort.Client/ArenaMapPort.Client.csproj', 'ArenaMapPort.Client/Plugin.cs',
         'ArenaMapPort.Server/ArenaMapPort.Server.csproj', 'ArenaMapPort.Server/ModMetadata.cs',
         'dist/BepInEx/plugins/maschine-ArenaMapPort/maschine-ArenaMapPort.Client.dll',
         'dist/SPT_Runtime/user/mods/ArenaMapPort/maschine-ArenaMapPort.Server.dll']


class DllVersions(unittest.TestCase):
    def check(self, root):
        return subprocess.run(['pwsh', '-NoProfile', '-File', str(REPO/'tools/validate/validate_dll_versions.ps1'),
                               '-RepoRoot', str(root)], capture_output=True, text=True)

    def test_actual_candidate_and_stale_binary_control(self):
        result = self.check(REPO)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in FILES:
                target = root/name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(REPO/name, target)
            self.assertEqual(self.check(root).returncode, 0)
            for name in FILES[:4]:
                file = root/name
                text = file.read_text(encoding='utf-8').replace('0.4.0', '99.99.99')
                file.write_text(text, encoding='utf-8')
            result = self.check(root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Stale Client DLL', result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
