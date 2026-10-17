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
import extract_banners
import extract_loadscreens
import extract_selection_map
import extract_water
from PIL import Image


class DistributionSidecarTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repo = Path(temporary.name)
        self.maps = self.repo / "tools/maps"
        self.maps.mkdir(parents=True)
        (self.maps / "equator.json").write_text(
            json.dumps({"key": "arena_equator", "db_base_json_path": "arena_equator/base.json"}), encoding="utf-8")
        self.plugin = self.repo / "dist/BepInEx/plugins/maschine-ArenaMapPort"
        for directory in (self.plugin/'map-selection', self.repo/'work/extract/map-selection'):
            directory.mkdir(parents=True, exist_ok=True)
            image = Image.new('RGBA', extract_selection_map.TEXTURE_SIZE)
            image.paste((70, 80, 90, 255), extract_selection_map.ALPHA_BOUNDS)
            image.save(directory/extract_selection_map.FILENAME)
            (directory/extract_selection_map.MANIFEST).write_text(json.dumps({
                'schema_version': 1, 'file': extract_selection_map.FILENAME,
                'texture_name': extract_selection_map.TEXTURE_NAME,
                'texture_size': list(extract_selection_map.TEXTURE_SIZE),
                'sprite_rect': list(extract_selection_map.SPRITE_RECT),
                'sha256': hashlib.sha256((directory/extract_selection_map.FILENAME).read_bytes()).hexdigest()
            }))
        pixel_pin = patch.object(extract_selection_map, 'REVIEWED_PIXEL_SHA256',
                                 hashlib.sha256(image.tobytes()).hexdigest())
        pixel_pin.start()
        self.addCleanup(pixel_pin.stop)
        for name in ('Equator', 'Bay5', 'AirPit'):
            for file in (f'ArenaMapPort_{name}.jsonc', 'Layers/Ground.svg'):
                relative = f'ArenaMapPort_{name}/{file}'
                for root in (self.plugin/'dynamicmaps', self.repo/'work/dynamicmaps/release'):
                    path = root/relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(b'accepted overview fixture')
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
        for directory in (self.server/'locales', self.repo/'work/extract/map-descriptions'):
            directory.mkdir(parents=True, exist_ok=True)
            (directory/'map-descriptions.json').write_text(json.dumps({
                'schema_version': 1, 'descriptions': {'en': {
                    'arena_equator': 'Mall fixture', 'arena_bay5': 'Harbor fixture',
                    'arena_airpit': 'Terminal fixture'}}}), encoding='utf-8')
        self.put_art()
        self.put_ai()
        self.put_acoustics()
        self.put_water()
        for folder in (self.server / "db/arena_equator", self.repo / "ArenaMapPort.Server/db/arena_equator"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_equator"}', encoding="utf-8")
        # The release is exactly three maps, even when parked descriptors remain in source.
        (self.maps / "bay5.json").write_text(json.dumps({"key": "arena_bay5", "db_base_json_path": "arena_bay5/base.json"}), encoding="utf-8")
        (self.maps / "airpit.json").write_text(json.dumps({"key": "arena_airpit", "db_base_json_path": "arena_airpit/base.json"}), encoding="utf-8")
        (self.maps / "bowl.json").write_text(json.dumps({"key": "arena_bowl", "db_base_json_path": "arena_bowl/base.json"}), encoding="utf-8")
        for kind in ("scenes", "navmesh", "preset"):
            (bundles / f"arena_bay5_{kind}.bundle").write_bytes(b"fixture bundle")
        (self.plugin / "material-shaders.arena_bay5.json").write_text("{}", encoding="utf-8")
        for folder in (self.server / "db/arena_bay5", self.repo / "ArenaMapPort.Server/db/arena_bay5"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_bay5"}', encoding="utf-8")
        # AirPit: bundles, shader sidecar, database and art, but neither an overview pack nor flares.
        for kind in ("scenes", "navmesh", "preset"):
            (bundles / f"arena_airpit_{kind}.bundle").write_bytes(b"fixture bundle")
        (self.plugin / "material-shaders.arena_airpit.json").write_text("{}", encoding="utf-8")
        for folder in (self.server / "db/arena_airpit", self.repo / "ArenaMapPort.Server/db/arena_airpit"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_airpit"}', encoding="utf-8")
        self.built = {}
        for name, destination in (("Client", self.plugin), ("Server", self.server)):
            built = self.repo / f"ArenaMapPort.{name}/bin/Release/maschine-ArenaMapPort.{name}.dll"
            built.parent.mkdir(parents=True)
            built.write_bytes(name.encode())
            (destination / built.name).write_bytes(built.read_bytes())
            self.built[name] = built

    RELEASED = ("arena_equator", "arena_bay5", "arena_airpit")

    @staticmethod
    def banner_names():
        return {extract_banners.output_filename(key) for key in DistributionSidecarTests.RELEASED}

    @staticmethod
    def loadscreen_names():
        return {extract_loadscreens.output_filename(key, index) for key in DistributionSidecarTests.RELEASED
                for index in range(1, len(extract_loadscreens.LOADSCREENS[key]) + 1)}

    def put_art(self):
        """Banners and loading screens as the extractors leave them (only the JPEG start marker matters here)."""
        for root, names in ((self.server / "banners", self.banner_names()),
                            (self.plugin / "loadscreens", self.loadscreen_names())):
            root.mkdir(parents=True, exist_ok=True)
            for name in names:
                (root / name).write_bytes(b"\xff\xd8\xff fixture art")

    def put_ai(self):
        """The git-tracked tools/maps/<stem>.ai.json sources and their staged copies (one per released map)."""
        (self.plugin / "ai").mkdir(parents=True, exist_ok=True)
        for key in self.RELEASED:
            stem = key.removeprefix("arena_")
            body = json.dumps({"schema_version": 1, "map_key": stem, "core_points": [], "patrol_ways": []})
            for root in (self.maps, self.plugin / "ai"):
                (root / f"{stem}.ai.json").write_text(body, encoding="utf-8")

    def put_acoustics(self):
        """One acoustic package per released map: graph JSON, bake and one room-tone WAV, hashes as recorded."""
        for key in self.RELEASED:
            folder = self.plugin / "acoustics" / key
            folder.mkdir(parents=True, exist_ok=True)
            blobs = {"source.audiobakedata": b"bake " + key.encode(), f"{key}_tone.wav": b"RIFF tone " + key.encode()}
            for name, blob in blobs.items():
                (folder / name).write_bytes(blob)

            def listed(name):
                return {"file": name, "bytes": len(blobs[name]), "sha256": hashlib.sha256(blobs[name]).hexdigest()}

            (folder / "spatial-audio.json").write_text(json.dumps({
                "schema_version": 1, "map_key": key, "bake": listed("source.audiobakedata"),
                "clips": [{"id": "fixture:1", "decoded": listed(f"{key}_tone.wav")}]}), encoding="utf-8")

    def put_water(self):
        """Bay5's harbor water package as extract_water.py leaves it: water.json plus the four raw mip chains it
        lists, each sized by the extractor's own mip-chain arithmetic (8 x 8 px, four mips) and hashed as recorded."""
        folder = self.plugin / "water" / "arena_bay5"
        (folder / "textures").mkdir(parents=True, exist_ok=True)
        textures = {}
        for index, (field, fmt) in enumerate((("_rippleTexture", 5), ("_normals", 12), ("_normalsDetails", 12), ("_foam", 10))):
            relative = "textures/" + field.lstrip("_") + ".bin"
            blob = bytes([0x40 + index]) * extract_water.texture_byte_count(8, 8, 4, fmt)
            (folder / relative).write_bytes(blob)
            textures[field] = {"file": relative, "width": 8, "height": 8, "mip_count": 4, "format": fmt,
                               "linear": fmt == 12, "filter_mode": 1, "aniso_level": 1, "mip_map_bias": 0.0,
                               "wrap_u": 0, "wrap_v": 0, "wrap_w": 0,
                               "byte_count": len(blob), "sha256": hashlib.sha256(blob).hexdigest()}
        (folder / "water.json").write_text(json.dumps({
            "schema_version": 1, "map_key": "arena_bay5", "target_shader": extract_water.TARGET_SHADER,
            "source": {"file": "level84", "component_type": extract_water.SOURCE_TYPE, "owner_path": "Sea"},
            "target": {"scene": "Arena_Bay5_Main", "hierarchy_path": "Sea/Plane"},
            "settings": {}, "textures": textures}), encoding="utf-8")

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
        self.assertIn("DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars", output.getvalue())

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

    def test_unused_exports_and_old_equator_database_are_rejected(self):
        for path, message in (
                (self.plugin/'arena-audio/manifest.json', 'Unused development payload'),
                (self.plugin/'arena-ui/manifest.json', 'Unused development payload'),
                (self.server/'db/base.json', 'Server database set')):
            with self.subTest(path=path):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('{}', encoding='utf-8')
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    path.unlink()

    def test_invalid_json_descriptor_is_not_silently_skipped(self):
        (self.maps / "broken.json").write_text("{", encoding="utf-8")
        with self.assertRaises(json.JSONDecodeError):
            self.validate()

    def test_missing_stale_or_invalid_descriptions_are_rejected(self):
        path = self.server/'locales/map-descriptions.json'
        original = path.read_bytes()
        path.unlink()
        with self.assertRaisesRegex(ValueError, 'missing map description files'):
            self.validate()
        path.write_bytes(original.replace(b'Mall fixture', b'Changed fixture'))
        with self.assertRaisesRegex(ValueError, 'Stale map descriptions'):
            self.validate()
        path.write_bytes(original.replace(b'"schema_version": 1', b'"schema_version": 2'))
        with self.assertRaises(ValueError):
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

    def test_airpit_requires_its_overview_and_rejects_unlisted_flares_or_layers(self):
        self.assertFalse((self.plugin / "flares/arena_airpit").exists())
        self.assertTrue((self.plugin / "dynamicmaps/ArenaMapPort_AirPit/Layers/Ground.svg").exists())
        self.validate()
        (self.plugin / "flares/arena_airpit").mkdir()
        (self.plugin / "flares/arena_airpit/flares.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Unexpected or missing flare package files"):
            self.validate()
        shutil.rmtree(self.plugin / "flares/arena_airpit")
        (self.plugin / "dynamicmaps/ArenaMapPort_AirPit/Layers/Unexpected.svg").write_text("<svg/>", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Unexpected or missing overview"):
            self.validate()

    def test_known_stale_airpit_spike_bundles_are_rejected_by_their_bytes(self):
        real = distribution.STALE_SPIKE_BUNDLES
        self.assertEqual(set(real), {"arena_airpit_scenes.bundle", "arena_airpit_navmesh.bundle"})
        for size, digest in real.values():
            self.assertGreater(size, 0)
            self.assertRegex(digest, r"^[0-9a-f]{64}$")
        self.validate()  # the fixtures' placeholder bytes are not the real spike files
        for name in real:
            with self.subTest(name=name):
                path = self.plugin / "bundles" / name
                stale = {name: (path.stat().st_size, hashlib.sha256(path.read_bytes()).hexdigest())}
                with patch.object(distribution, "STALE_SPIKE_BUNDLES", stale):
                    with self.assertRaisesRegex(ValueError, f"Stale AirPit bundle: {name}"):
                        self.validate()
                    # Same name and size, other bytes: a rebuilt bundle is accepted.
                    original = path.read_bytes()
                    path.write_bytes(bytes([original[0] ^ 1]) + original[1:])
                    try:
                        self.validate()
                    finally:
                        path.write_bytes(original)

    def test_distribution_requires_every_released_maps_banner_and_loadscreens_exactly(self):
        for path, message in ((self.server / "banners/arena_airpit.jpg", "Banner file set mismatch"),
                              (self.plugin / "loadscreens/arena_airpit_5.jpg", "Loadscreen file set mismatch"),
                              (self.plugin / "loadscreens/arena_bay5_1.jpg", "Loadscreen file set mismatch")):
            with self.subTest(missing=path.name):
                original = path.read_bytes()
                path.unlink()
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    path.write_bytes(original)
        for path, message in ((self.server / "banners/arena_woods.jpg", "Banner file set mismatch"),
                              (self.server / "banners/arena_airpit.png", "Banner file set mismatch"),
                              (self.plugin / "loadscreens/arena_airpit_6.jpg", "Loadscreen file set mismatch"),
                              (self.plugin / "loadscreens/arena_airpit_Alt_1.jpg", "Loadscreen file set mismatch")):
            with self.subTest(unexpected=path.name):
                path.write_bytes(b"\xff\xd8\xff stray")
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    path.unlink()
        for path in (self.server / "banners/arena_airpit.jpg", self.plugin / "loadscreens/arena_airpit_1.jpg"):
            with self.subTest(corrupt=path.name):
                original = path.read_bytes()
                for bad in (b"", b"<html>"):
                    path.write_bytes(bad)
                    try:
                        with self.assertRaisesRegex(ValueError, "empty or not a JPEG"):
                            self.validate()
                    finally:
                        path.write_bytes(original)

    def test_distribution_requires_every_released_maps_ai_sidecar_exactly_and_current(self):
        staged = self.plugin / "ai/airpit.ai.json"
        source = self.maps / "airpit.ai.json"
        original = staged.read_bytes()
        staged.unlink()
        with self.assertRaisesRegex(ValueError, r"AI sidecar file set mismatch: missing=\['airpit.ai.json'\]"):
            self.validate()
        (self.plugin / "ai/woods.ai.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, r"AI sidecar file set mismatch: missing=\['airpit.ai.json'\] unexpected=\['woods.ai.json'\]"):
            self.validate()
        (self.plugin / "ai/woods.ai.json").unlink()
        staged.write_bytes(original)
        self.validate()
        # A sidecar edited after the last client build is stale in dist.
        source.write_text(source.read_text(encoding="utf-8").replace('"core_points": []', '"core_points": [1]'), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Stale AI sidecar: airpit.ai.json"):
            self.validate()
        staged.write_bytes(source.read_bytes())
        self.validate()
        # Equal bytes of the wrong map (a copy-paste between rows) are refused too.
        wrong = json.dumps({"schema_version": 1, "map_key": "bay5", "core_points": [], "patrol_ways": []})
        source.write_text(wrong, encoding="utf-8")
        staged.write_text(wrong, encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "AI sidecar map key mismatch: airpit.ai.json"):
            self.validate()

    def test_distribution_requires_every_released_maps_acoustics_exactly_and_intact(self):
        airpit = self.plugin / "acoustics/arena_airpit"
        package_path = airpit / "spatial-audio.json"
        package = package_path.read_bytes()
        bake = airpit / "source.audiobakedata"
        blob = bake.read_bytes()
        for path, message in ((airpit / "arena_airpit_tone.wav", "Acoustics file set mismatch"),
                              (bake, "Acoustics file set mismatch"),
                              (package_path, "Missing acoustic package: arena_airpit/spatial-audio.json")):
            with self.subTest(missing=path.name):
                original = path.read_bytes()
                path.unlink()
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    path.write_bytes(original)
        for path in (airpit / "stray.wav", self.plugin / "acoustics/arena_bowl/spatial-audio.json",
                     self.plugin / "acoustics/loose.json"):
            with self.subTest(unexpected=path.name):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"stray")
                try:
                    with self.assertRaisesRegex(ValueError, "Acoustics file set mismatch"):
                        self.validate()
                finally:
                    path.unlink()
        self.assertEqual(package_path.read_bytes(), package)
        # Same length, other bytes: only the recorded SHA-256 can tell.
        bake.write_bytes(bytes([blob[0] ^ 1]) + blob[1:])
        with self.assertRaisesRegex(ValueError, "Acoustic file length/hash mismatch: arena_airpit/source.audiobakedata"):
            self.validate()
        bake.write_bytes(blob + b"!")
        with self.assertRaisesRegex(ValueError, "Acoustic file length/hash mismatch"):
            self.validate()
        bake.write_bytes(blob)
        for change, message in ((lambda p: p.update(map_key="arena_bay5"), "Acoustic package map or schema mismatch"),
                                (lambda p: p.update(schema_version=2), "Acoustic package map or schema mismatch")):
            with self.subTest(message=message):
                mutated = json.loads(package)
                change(mutated)
                package_path.write_text(json.dumps(mutated), encoding="utf-8")
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    package_path.write_bytes(package)
        self.validate()

    def test_distribution_requires_bay5_water_exactly_and_intact(self):
        self.assertEqual(distribution.WATER_MAP_KEYS, ("arena_bay5",))
        for key in ("arena_equator", "arena_airpit"):
            self.assertFalse((self.plugin / "water" / key).exists())
        bay5 = self.plugin / "water/arena_bay5"
        package_path = bay5 / "water.json"
        package = package_path.read_bytes()
        normals = bay5 / "textures/normals.bin"
        blob = normals.read_bytes()
        for path, message in ((normals, r"Water file set mismatch: missing=\['arena_bay5/textures/normals.bin'\] unexpected=\[\]"),
                              (package_path, "Missing water package: arena_bay5/water.json")):
            with self.subTest(missing=path.name):
                original = path.read_bytes()
                path.unlink()
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    path.write_bytes(original)
        # Stray files beside the package, a released map without water, a parked map's package and a loose file.
        for path in (bay5 / "textures/stray.bin", bay5 / "settings.json", self.plugin / "water/arena_equator/water.json",
                     self.plugin / "water/arena_airpit2/water.json", self.plugin / "water/loose.json"):
            relative = path.relative_to(self.plugin / "water").as_posix()
            with self.subTest(unexpected=relative):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"stray")
                try:
                    with self.assertRaisesRegex(ValueError, rf"Water file set mismatch: missing=\[\] unexpected=\['{relative}'\]"):
                        self.validate()
                finally:
                    path.unlink()
        # Same length, other bytes: only the recorded SHA-256 can tell. A longer file fails on its length.
        normals.write_bytes(bytes([blob[0] ^ 1]) + blob[1:])
        with self.assertRaisesRegex(ValueError, "Water texture length/hash mismatch: arena_bay5/textures/normals.bin"):
            self.validate()
        normals.write_bytes(blob + b"!")
        with self.assertRaisesRegex(ValueError, "Water texture length/hash mismatch"):
            self.validate()
        normals.write_bytes(blob)
        for change, message in (
                (lambda p: p.update(map_key="arena_equator"), "Water package map or schema mismatch: arena_bay5"),
                (lambda p: p.update(schema_version=2), "Water package map or schema mismatch"),
                (lambda p: p.update(target_shader="Custom/SimpleWater4"), "Water package shader mismatch"),
                (lambda p: p["textures"].pop("_foam"), r"Water texture set mismatch: arena_bay5: missing=\['_foam'\] unexpected=\[\]"),
                (lambda p: p["textures"].update(_extra=dict(p["textures"]["_foam"])), r"unexpected=\['_extra'\]"),
                (lambda p: p["textures"]["_foam"].update(file="textures/normals.bin"), "Water textures share a file: arena_bay5"),
                (lambda p: p["textures"]["_foam"].update(file="foam.bin"), "Invalid water texture filename: arena_bay5/_foam"),
                (lambda p: p["textures"]["_foam"].update(file="textures/../water.json"), "Invalid water texture filename"),
                (lambda p: p["textures"]["_foam"].update(file="textures/sub/foam.bin"), "Invalid water texture filename"),
                (lambda p: p["textures"]["_foam"].update(file="textures\\foam.bin"), "Invalid water texture filename"),
                (lambda p: p["textures"]["_foam"].update(file="textures/"), "Invalid water texture filename"),
                # The recorded mip chain no longer explains the recorded byte count, or cannot exist at all.
                (lambda p: p["textures"]["_foam"].update(mip_count=3), "byte count does not match its mip chain: arena_bay5/textures/foam.bin"),
                (lambda p: p["textures"]["_foam"].update(format=99), "Water texture metadata invalid: arena_bay5/textures/foam.bin")):
            with self.subTest(message=message):
                mutated = json.loads(package)
                change(mutated)
                package_path.write_text(json.dumps(mutated), encoding="utf-8")
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    package_path.write_bytes(package)
        mutated = json.loads(package)
        del mutated["textures"]["_foam"]["sha256"]
        package_path.write_text(json.dumps(mutated), encoding="utf-8")
        try:
            with self.assertRaisesRegex(KeyError, "sha256"):
                self.validate()
        finally:
            package_path.write_bytes(package)
        self.validate()

    def test_art_tables_must_list_exactly_the_released_maps(self):
        without_airpit = {k: v for k, v in extract_banners.BANNERS.items() if k != "arena_airpit"}
        with patch.object(extract_banners, "BANNERS", without_airpit):
            with self.assertRaisesRegex(ValueError, "Banner table does not match"):
                self.validate()
        extra = {**extract_loadscreens.LOADSCREENS, "arena_bowl": extract_loadscreens.LOADSCREENS["arena_airpit"]}
        with patch.object(extract_loadscreens, "LOADSCREENS", extra):
            with self.assertRaisesRegex(ValueError, "Loadscreen table does not match"):
                self.validate()

    def test_distribution_rejects_missing_stale_or_unexpected_overview(self):
        path = self.plugin/'dynamicmaps/ArenaMapPort_Bay5/Layers/Ground.svg'
        original = path.read_bytes()
        path.unlink()
        with self.assertRaises(FileNotFoundError):
            self.validate()
        path.write_bytes(b'changed map')
        with self.assertRaisesRegex(ValueError, 'Stale overview map'):
            self.validate()
        path.write_bytes(original)
        (self.plugin/'dynamicmaps/unexpected.json').write_text('{}', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Unexpected or missing overview'):
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
