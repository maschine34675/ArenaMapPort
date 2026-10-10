"""Distribution discovery must ignore map sidecars without hiding broken maps."""
from contextlib import redirect_stdout
import hashlib
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
sys.path.insert(0, str(Path(__file__).resolve().parent))
import validate_distribution as distribution
from test_multiflare_extraction import graph as flare_graph


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
        for key, scene in (("arena_equator", "Arena_Equator_Lighting"),
                           ("arena_bay5", "Arena_Bay5_Light")):
            package = flare_graph()
            package["map_key"] = key
            package["lights"][0]["scene"] = scene
            flare_dir = self.plugin / "flares" / key
            for texture in package["textures"]:
                blob = b"\x7a" * texture["byte_count"]
                texture["sha256"] = hashlib.sha256(blob).hexdigest()
                texture_path = flare_dir / texture["file"]
                texture_path.parent.mkdir(parents=True, exist_ok=True)
                texture_path.write_bytes(blob)
            (flare_dir / "flares.json").write_text(json.dumps(package), encoding="utf-8")
        self.server = self.repo / "dist/SPT_Runtime/user/mods/ArenaMapPort"
        for folder in (self.server / "db", self.repo / "ArenaMapPort.Server/db"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_equator"}', encoding="utf-8")
        # The release is exactly two maps, even when parked descriptors remain in source.
        (self.maps / "bay5.json").write_text(json.dumps({"key": "arena_bay5", "db_base_json_path": "arena_bay5/base.json"}), encoding="utf-8")
        (self.maps / "bowl.json").write_text(json.dumps({"key": "arena_bowl", "db_base_json_path": "arena_bowl/base.json"}), encoding="utf-8")
        for kind in ("scenes", "navmesh", "preset"):
            (bundles / f"arena_bay5_{kind}.bundle").write_bytes(b"fixture bundle")
        (self.plugin / "material-shaders.arena_bay5.json").write_text("{}", encoding="utf-8")
        for folder in (self.server / "db/arena_bay5", self.repo / "ArenaMapPort.Server/db/arena_bay5"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_bay5"}', encoding="utf-8")
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
        self.assertIn("DISTRIBUTION_OK 2 maps 6 bundles 2 sidecars", output.getvalue())

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

    def test_distribution_rejects_every_kind_of_parked_map_output(self):
        for path, message in (
                (self.plugin / "bundles/arena_bowl_scenes.bundle", "Bundle set mismatch"),
                (self.plugin / "material-shaders.arena_bowl.json", "Shader sidecar set"),
                (self.server / "db/arena_bowl/base.json", "Server database set")):
            with self.subTest(path=path):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("{}", encoding="utf-8")
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    path.unlink()

    def test_distribution_rejects_missing_flare_files_or_texture_hash(self):
        flare_dir = self.plugin / "flares/arena_equator"
        package_path = flare_dir / "flares.json"
        for path in (package_path, flare_dir / "textures/test.bin"):
            with self.subTest(missing=path.name):
                original = path.read_bytes()
                path.unlink()
                try:
                    with self.assertRaises(FileNotFoundError):
                        self.validate()
                finally:
                    path.write_bytes(original)
        package = json.loads(package_path.read_text(encoding="utf-8"))
        del package["textures"][0]["sha256"]
        package_path.write_text(json.dumps(package), encoding="utf-8")
        with self.assertRaisesRegex(KeyError, "sha256"):
            self.validate()

    def test_distribution_rejects_same_length_modified_flare_texture(self):
        texture_path = self.plugin / "flares/arena_bay5/textures/test.bin"
        blob = texture_path.read_bytes()
        texture_path.write_bytes(bytes([blob[0] ^ 1]) + blob[1:])
        with self.assertRaisesRegex(ValueError, "Flare texture length/hash mismatch"):
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
        self.assertIn("catalog registry entries", result.stdout)

    def test_real_registry_rejects_missing_key_descriptor(self):
        (self.maps / "equator.json").write_text("{}", encoding="utf-8")
        result = self.run_validator()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("equator.json: missing map key", result.stderr)


if __name__ == "__main__":
    unittest.main()
