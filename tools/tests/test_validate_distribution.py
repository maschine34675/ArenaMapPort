"""Distribution discovery must ignore map sidecars without hiding broken maps."""
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
import validate_distribution as distribution


class DistributionSidecarTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repo = Path(temporary.name)
        self.maps = self.repo / "tools/maps"
        self.maps.mkdir(parents=True)
        (self.maps / "equator.json").write_text(
            json.dumps({"key": "arena_equator", "db_base_json_path": "base.json"}), encoding="utf-8")
        self.plugin = self.repo / "dist/BepInEx/plugins/ArenaMapPort"
        bundles = self.plugin / "bundles"
        bundles.mkdir(parents=True)
        for kind in ("scenes", "navmesh", "preset"):
            (bundles / f"arena_equator_{kind}.bundle").write_bytes(b"fixture bundle")
        (self.plugin / "material-shaders.json").write_text("{}", encoding="utf-8")
        self.server = self.repo / "dist/SPT_Runtime/user/mods/ArenaMapPort"
        for folder in (self.server / "db", self.repo / "ArenaMapPort.Server/db"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_equator"}', encoding="utf-8")
        self.built = {}
        for name, destination in (("Client", self.plugin), ("Server", self.server)):
            built = self.repo / f"ArenaMapPort.{name}/bin/Release/maschine-ArenaMapPort.{name}.dll"
            built.parent.mkdir(parents=True)
            built.write_bytes(name.encode())
            (destination / built.name).write_bytes(built.read_bytes())
            self.built[name] = built

    def validate(self):
        def target_path(command, **kwargs):
            name = Path(command[2]).stem.removeprefix("ArenaMapPort.")
            return str(self.built[name])

        output = io.StringIO()
        # The fixture exercises distribution discovery and all file-set/content checks.
        # Registry parsing is covered below; Unity bundle parsing and MSBuild need real artifacts.
        with patch.object(distribution, "REPO", self.repo), \
                patch.object(distribution.subprocess, "run") as run, \
                patch.object(distribution.subprocess, "check_output", side_effect=target_path), \
                redirect_stdout(output):
            distribution.validate()
        self.assertEqual(run.call_count, 2)
        self.assertIn("DISTRIBUTION_OK 1 maps 3 bundles 1 sidecars", output.getvalue())

    def test_distribution_accepts_bots_exits_and_other_dotted_sidecars(self):
        (self.maps / "equator.bots.json").write_text('{"player_spawns": []}', encoding="utf-8")
        (self.maps / "equator.exits.json").write_text("[]", encoding="utf-8")
        # Even a sidecar containing a key is not a registered map. Discovery is by filename.
        (self.maps / "equator.opened.json").write_text('{"key": "not_a_map"}', encoding="utf-8")
        self.validate()

    def test_descriptor_without_key_is_not_silently_skipped(self):
        (self.maps / "broken.json").write_text("{}", encoding="utf-8")
        with self.assertRaises(KeyError):
            self.validate()

    def test_invalid_json_descriptor_is_not_silently_skipped(self):
        (self.maps / "broken.json").write_text("{", encoding="utf-8")
        with self.assertRaises(json.JSONDecodeError):
            self.validate()

    def test_unexpected_bundle_is_still_rejected_when_sidecars_exist(self):
        (self.maps / "equator.bots.json").write_text("{}", encoding="utf-8")
        (self.plugin / "bundles/unregistered_scenes.bundle").write_bytes(b"unexpected")
        with self.assertRaisesRegex(ValueError, "Bundle set mismatch"):
            self.validate()


class RegistrySidecarTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.maps = Path(temporary.name) / "maps"
        shutil.copytree(REPO / "tools/maps", self.maps)

    def run_validator(self):
        return subprocess.run(
            [sys.executable, "-X", "utf8", str(REPO / "tools/validate/validate_map_descriptors.py"),
             "--maps-dir", str(self.maps)],
            cwd=REPO, capture_output=True, text=True, encoding="utf-8")

    def test_real_registry_accepts_sidecars(self):
        (self.maps / "equator.opened.json").write_text('{"key": "not_a_map"}', encoding="utf-8")
        result = self.run_validator()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("runtime registry entries", result.stdout)

    def test_real_registry_rejects_missing_key_descriptor(self):
        (self.maps / "equator.json").write_text("{}", encoding="utf-8")
        result = self.run_validator()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("equator.json: missing map key", result.stderr)


if __name__ == "__main__":
    unittest.main()
