"""Distribution discovery must ignore map sidecars without hiding broken maps."""
from contextlib import redirect_stdout
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import wave
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
sys.path.insert(0, str(REPO / "tools" / "bundle"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bundle_crypt
import validate_distribution as distribution
from test_multiflare_extraction import graph as flare_graph
import extract_banners
import extract_loadscreens
import extract_monitor_templates as extract_monitors
import extract_selection_map
import extract_water
from PIL import Image


UNITYFS = b"UnityFS\0"
# the raw resource streams of tools/bundle/key-sources.json (index, kind)
KEY_SOURCES = {"arena_equator": ((51, "assets.resS"), (55, "assets.resS"), (57, "assets.resS")),
               "arena_bay5": ((79, "assets.resS"), (80, "assets.resS"), (82, "assets.resS"), (84, "assets.resS"), (84, "resource"),
                              (86, "assets.resS"), (88, "assets.resS"), (89, "assets.resS")),
               "arena_airpit": ((28, "assets.resS"), (28, "resource"), (29, "assets.resS"), (37, "assets.resS"), (38, "assets.resS"),
                                (40, "assets.resS"), (324, "assets.resS")),
               "arena_bowl": ((41, "assets.resS"), (41, "resource"), (45, "assets.resS"), (46, "assets.resS"), (47, "assets.resS"),
                              (48, "assets.resS"), (48, "resource"), (95, "assets.resS"))}


def source(n, kind="assets.resS", size=None):
    return {"path": f"EscapeFromTarkovArena_Data/sharedassets{n}.{kind}", "size": size if size is not None else 1000 + n}


def key_file_data(key, wraps=1, files=None):
    """A key file as the tool writes it (shape only: the hex values are not real). `files` replaces the file list."""
    listed = files if files is not None else [source(n, kind) for n, kind in KEY_SOURCES[key]]
    return {"schema": 2, "map_key": key,
            "wraps": [{"built_for": f"0.6.{w}.0.48206", "sample": {"block_size": 65536, "blocks": 16}, "files": listed,
                       "salt": f"{w:x}" * 64, "wrapped_key": "b" * 64, "check": "c" * 64}
                      for w in range(1, wraps + 1)]}


def key_file_text(data):
    return json.dumps(data, indent=2) + "\n"


class StubCrypt:
    """Stands in for tools/bundle/bundle_crypt.py. A fixture bundle is b"AMPB" + text and 'decrypts' to b"UnityFS\0" + that
    text; the key files are not really unlocked. The real tool and the real validators meet in test_bundle_crypt_tool.py."""
    BundleCryptError = bundle_crypt.BundleCryptError
    is_encrypted = staticmethod(bundle_crypt.is_encrypted)

    def __init__(self):
        self.calls = []
        self.fail = {}          # (function, file name) -> reason: raise BundleCryptError
        self.map_key_of = {}    # file name -> the map key the container header claims
        self.prefix_of = {}     # file name -> what read-range returns
        self.plaintext_of = {}  # file name -> a different plaintext

    def plaintext(self, path):
        path = Path(path)
        return self.plaintext_of.get(path.name, UNITYFS + path.read_bytes()[4:])

    def _fail_if_asked(self, function, name):
        reason = self.fail.get((function, name))
        if reason:
            raise bundle_crypt.BundleCryptError(reason)

    def inspect(self, path, key_store=None, verify_chunks=False):
        name = Path(path).name
        self.calls.append(("inspect", name, key_store, verify_chunks))
        self._fail_if_asked("inspect", name)
        key = self.map_key_of.get(name, re.sub(r"_(?:scenes|navmesh|preset)\.bundle$", "", name))
        return {"map_key": key, "plaintext_length": len(self.plaintext(path)), "mac_verified": True,
                "chunks": "verified" if verify_chunks else None}

    def read_range(self, path, offset, length, key_store=None, clip=False):
        name = Path(path).name
        self.calls.append(("read_range", name, offset, length))
        self._fail_if_asked("read_range", name)
        return self.prefix_of.get(name, self.plaintext(path)[offset:offset + length])

    def plain_sha256(self, path, key_store=None):
        name = Path(path).name
        self.calls.append(("plain_sha256", name))
        self._fail_if_asked("plain_sha256", name)
        plain = self.plaintext(path)
        return hashlib.sha256(plain).hexdigest(), len(plain)

    def verify_keys(self, map_key, keys_file, key_store=None, arena="auto", arena_unverified=None, built_for=None):
        self.calls.append(("verify_keys", map_key, Path(keys_file).name, key_store, arena))
        self._fail_if_asked("verify_keys", map_key)
        return f"OK verify-keys {map_key}"


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
        for name in ('Equator', 'Bay5', 'AirPit', 'Bowl'):
            for file in (f'ArenaMapPort_{name}.jsonc', 'Layers/Ground.svg'):
                relative = f'ArenaMapPort_{name}/{file}'
                for root in (self.plugin/'dynamicmaps', self.repo/'work/dynamicmaps/release'):
                    path = root/relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(b'accepted overview fixture')
        bundles = self.plugin / "bundles"
        bundles.mkdir(parents=True)
        self.crypt = StubCrypt()
        self.put_bundles("arena_equator")
        (self.plugin / "material-shaders.arena_equator.json").write_text("{}", encoding="utf-8")
        for key, scene in (("arena_equator", "Arena_Equator_Lighting"),
                           ("arena_bay5", "Arena_Bay5_Light"),
                           ("arena_bowl", "Arena_Bowl_Light")):
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
                    'arena_airpit': 'Terminal fixture', 'arena_bowl': 'Stadium fixture'}}}), encoding='utf-8')
        self.put_art()
        self.put_ai()
        self.put_acoustics()
        self.put_gate_sounds()
        self.put_monitors()
        self.put_water()
        self.put_key_files()
        for folder in (self.server / "db/arena_equator", self.repo / "ArenaMapPort.Server/db/arena_equator"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_equator"}', encoding="utf-8")
        # The release is exactly four maps (Bowl since PLAN-BOWL item 8, 2026-10-18), even when parked descriptors
        # remain in source.
        (self.maps / "bay5.json").write_text(json.dumps({"key": "arena_bay5", "db_base_json_path": "arena_bay5/base.json"}), encoding="utf-8")
        (self.maps / "airpit.json").write_text(json.dumps({"key": "arena_airpit", "db_base_json_path": "arena_airpit/base.json"}), encoding="utf-8")
        (self.maps / "bowl.json").write_text(json.dumps({"key": "arena_bowl", "db_base_json_path": "arena_bowl/base.json"}), encoding="utf-8")
        self.put_bundles("arena_bay5")
        (self.plugin / "material-shaders.arena_bay5.json").write_text("{}", encoding="utf-8")
        for folder in (self.server / "db/arena_bay5", self.repo / "ArenaMapPort.Server/db/arena_bay5"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_bay5"}', encoding="utf-8")
        # AirPit: bundles, shader sidecar, database and art, but neither an overview pack nor flares.
        self.put_bundles("arena_airpit")
        (self.plugin / "material-shaders.arena_airpit.json").write_text("{}", encoding="utf-8")
        for folder in (self.server / "db/arena_airpit", self.repo / "ArenaMapPort.Server/db/arena_airpit"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_airpit"}', encoding="utf-8")
        # Bowl (PLAN-BOWL item 8, overview and flares item 17): bundles, shader sidecar, database, art, AI sidecar,
        # acoustics, an overview pack (the loop above) and a flare package (the loop above); no water package, and four
        # loading images (Arena ships no loading screen for it).
        self.put_bundles("arena_bowl")
        (self.plugin / "material-shaders.arena_bowl.json").write_text("{}", encoding="utf-8")
        for folder in (self.server / "db/arena_bowl", self.repo / "ArenaMapPort.Server/db/arena_bowl"):
            folder.mkdir(parents=True)
            (folder / "base.json").write_text('{"Id": "arena_bowl"}', encoding="utf-8")
        self.built = {}
        for name, destination in (("Client", self.plugin), ("Server", self.server)):
            built = self.repo / f"ArenaMapPort.{name}/bin/Release/maschine-ArenaMapPort.{name}.dll"
            built.parent.mkdir(parents=True)
            built.write_bytes(name.encode())
            (destination / built.name).write_bytes(built.read_bytes())
            self.built[name] = built

    RELEASED = ("arena_equator", "arena_bay5", "arena_airpit", "arena_bowl")

    @staticmethod
    def banner_names():
        return {extract_banners.output_filename(key) for key in DistributionSidecarTests.RELEASED}

    @staticmethod
    def loadscreen_names():
        return {extract_loadscreens.output_filename(key, index) for key in DistributionSidecarTests.RELEASED
                for index in range(1, len(extract_loadscreens.LOADSCREENS[key]) + 1)}

    def put_bundles(self, key):
        """The three bundles of one map as AMPB containers (only the magic and a distinct body matter to the stubbed tool)."""
        for kind in ("scenes", "navmesh", "preset"):
            (self.plugin / "bundles" / f"{key}_{kind}.bundle").write_bytes(b"AMPB" + f"fixture container {key} {kind}".encode())

    def put_key_files(self):
        """keys/<key>.json for every released map, as the tool writes them."""
        (self.plugin / "keys").mkdir(parents=True, exist_ok=True)
        for key in self.RELEASED:
            (self.plugin / "keys" / f"{key}.json").write_text(key_file_text(key_file_data(key)), encoding="utf-8", newline="\n")

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

    def put_gate_sounds(self):
        """One gate sound package per released map as the extractor leaves it (work/extract/gate-sounds) and as the client build stages
        it (dist/.../gate-sounds): a manifest, one tiny PCM16 WAV per clip, the tracked row sidecar and a doors sidecar the row resolves
        to. AirPit's clip lives in sharedassets28.resource, a key-source file of the map, so it is pitch-baked."""
        (self.repo / "tools/bundle").mkdir(parents=True, exist_ok=True)
        shutil.copy(REPO / "tools/bundle/key-sources.json", self.repo / "tools/bundle/key-sources.json")
        for key in self.RELEASED:
            stem = key.removeprefix("arena_")
            clip_id = f"{key}_gate"
            wav = io.BytesIO()
            with wave.open(wav, "wb") as w:
                w.setnchannels(2)
                w.setsampwidth(2)
                w.setframerate(44100)
                w.writeframes(bytes(range(32)))
            blob = wav.getvalue()
            baked = key == "arena_airpit"
            row = {"barrier": f"{stem.upper()}/Gate", "part": None, "at": None, "closed_position": None, "clip": clip_id,
                   "emitter": [0.0, 1.0, 0.0], "source_volume": 1.0, "player_volume": 1.0, "authored_pitch": 1.25 if baked else 1.0,
                   "pitch": 1.0, "min_distance": 0.4, "max_distance": 30.0, "mixer": "ArenaGates", "mixer_db": -15.0,
                   "source_mixer": None, "origin": "level1 SOUND/Gate", "match_distance": 0.0}
            mixer = {"name": "ArenaGates", "db": -15.0, "path": "Master/ArenaGates"}
            pose = {"barrier": row["barrier"], "part": "Leaf", "node": "", "at": None, "closed": {"position": [0.0, 0.0, 0.0]}}
            (self.maps / f"{stem}.doors.json").write_text(json.dumps({"poses": [pose]}), encoding="utf-8")
            (self.maps / f"{stem}.gatesounds.json").write_text(json.dumps({"rows": [row], "mixer": mixer}), encoding="utf-8")
            manifest = {"schema_version": 1, "map_key": key, "arena_version": "0.6.1.0.48206", "mixer": mixer, "rows": [row],
                        "clips": [{"id": clip_id, "file": f"{clip_id}.wav", "channels": 2, "sampleRate": 44100, "frames": 8, "bytes": len(blob),
                                   "sha256": hashlib.sha256(blob).hexdigest(), "pitch_baked": baked, "baked_pitch": 1.25 if baked else 1.0,
                                   "source": {"asset": "sharedassets28.assets" if baked else "sharedassets56.assets", "path_id": 99, "format": "PCM16",
                                              "resource": {"file": "sharedassets28.resource" if baked else "sharedassets56.resource",
                                                           "offset": 0, "size": 705728}},
                                   "fit": {"mode": "as_is"}}]}
            for folder in (self.plugin / "gate-sounds" / key, self.repo / "work/extract/gate-sounds" / key):
                folder.mkdir(parents=True, exist_ok=True)
                (folder / f"{clip_id}.wav").write_bytes(blob)
                (folder / "gate-sounds.json").write_text(json.dumps(manifest), encoding="utf-8")

    def put_monitors(self):
        """The score monitor package as extract_monitor_templates.py leaves it (work/extract/monitors) and as the client build stages it
        (dist/.../monitors): a manifest and one PNG per sprite. The extractor's sprite table is replaced by three tiny sprites for the test (the
        real pins are of the real artwork); everything else, the validate of the extractor included, runs as shipped."""
        table = {}
        images = {}
        for index, (name, size, border, faces) in enumerate((("Fixture_Ground", (8, 8), (0, 0, 0, 0), ("timer",)),
                                                              ("Fixture_Frame", (16, 8), (2, 2, 2, 2), ("timer", "score", "alive")),
                                                              ("Fixture_Icon", (4, 4), (0, 0, 0, 0), ("score",)))):
            image = Image.new("RGBA", size, (10 + index, 20, 30, 255 if index == 0 else 128))
            image.putpixel((1, 1), (200, 100, 50 + index, 255))
            images[name] = image
            table[name] = {"size": size, "border": border, "used_by": faces, "pixels_sha256": extract_monitors.pixel_hash(image)}
        patcher = patch.object(extract_monitors, "SPRITES", table)
        patcher.start()
        self.addCleanup(patcher.stop)
        entries = []
        blobs = {}
        for name, image in images.items():
            blob = extract_monitors.png_bytes(image)
            blobs[extract_monitors.file_name(name)] = blob
            entries.append(extract_monitors.manifest_entry(name, blob, {"asset": "sharedassets1.assets", "sprite_path_id": 1, "texture_path_id": 2, "texture_format": 4}))
        manifest = extract_monitors.render_manifest("0.6.1.0.48206", entries)
        for folder in (self.plugin / "monitors", self.repo / "work/extract/monitors"):
            folder.mkdir(parents=True, exist_ok=True)
            for name, blob in blobs.items():
                (folder / name).write_bytes(blob)
            (folder / extract_monitors.MANIFEST).write_text(manifest, encoding="utf-8", newline="\n")

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
                patch.object(distribution, "bundle_crypt", self.crypt), \
                patch.object(distribution.subprocess, "run") as run, \
                patch.object(distribution.subprocess, "check_output", side_effect=target_path), \
                redirect_stdout(output):
            distribution.validate()
        self.assertEqual(run.call_count, 2)
        # The bundle-name validator is told where the key store is: it reads the encrypted bundles through the tool.
        self.assertEqual(run.call_args_list[1].args[0][-2:], ["--key-store", str(self.repo / "work" / "release-keys")])
        self.assertIn("DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted", output.getvalue())

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
                (self.plugin / "bundles/arena_prison_scenes.bundle", "Bundle set mismatch"),
                (self.plugin / "material-shaders.arena_prison.json", "Shader sidecar set"),
                (self.plugin / "material-shaders.json", "Shader sidecar set"),
                (self.server / "db/arena_prison/base.json", "Server database set")):
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
                # The recorded size and digest are those of the PLAINTEXT build; dist holds containers, so the tool's
                # decrypted length and streamed digest are compared (the stub's plaintext is b"UnityFS\0" + the body).
                plain = self.crypt.plaintext(path)
                stale = {name: (len(plain), hashlib.sha256(plain).hexdigest())}
                with patch.object(distribution, "STALE_SPIKE_BUNDLES", stale):
                    self.crypt.calls.clear()
                    with self.assertRaisesRegex(ValueError, f"Stale AirPit bundle: {name}"):
                        self.validate()
                    # Same name and size, other bytes: a rebuilt bundle is accepted.
                    original = path.read_bytes()
                    path.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
                    try:
                        self.validate()
                    finally:
                        path.write_bytes(original)

    def test_the_spike_check_hashes_only_a_bundle_whose_decrypted_length_matches(self):
        name = "arena_airpit_scenes.bundle"
        path = self.plugin / "bundles" / name
        plain = self.crypt.plaintext(path)
        for size, hashed in ((len(plain) + 1, False), (len(plain), True)):
            with self.subTest(size=size):
                self.crypt.calls.clear()
                with patch.object(distribution, "STALE_SPIKE_BUNDLES", {name: (size, "0" * 64)}), \
                        patch.object(distribution, "bundle_crypt", self.crypt):
                    distribution.validate_not_spike_build(self.plugin / "bundles")
                self.assertEqual(any(call[0] == "plain_sha256" for call in self.crypt.calls), hashed)

    def test_a_tool_failure_while_checking_for_the_spike_build_is_reported(self):
        name = "arena_airpit_navmesh.bundle"
        self.crypt.fail[("inspect", name)] = "FAIL header authentication failed"
        with self.assertRaisesRegex(ValueError, f"Bundle {name}: FAIL header authentication failed"):
            self.validate()

    def test_a_plain_bundle_is_still_compared_by_its_bytes_for_the_spike_check(self):
        name = "arena_airpit_scenes.bundle"
        path = self.plugin / "bundles" / name
        path.write_bytes(b"UnityFS\0 an old plaintext bundle")
        stale = {name: (path.stat().st_size, hashlib.sha256(path.read_bytes()).hexdigest())}
        with patch.object(distribution, "STALE_SPIKE_BUNDLES", stale), patch.object(distribution, "bundle_crypt", self.crypt):
            with self.assertRaisesRegex(ValueError, f"Stale AirPit bundle: {name}"):
                distribution.validate_not_spike_build(self.plugin / "bundles")
        self.assertEqual([c for c in self.crypt.calls if c[0] != "is_encrypted"], [])

    def test_every_bundle_must_be_an_ampb_container(self):
        for key in self.RELEASED:
            for kind in ("scenes", "navmesh", "preset"):
                name = f"{key}_{kind}.bundle"
                path = self.plugin / "bundles" / name
                original = path.read_bytes()
                for plaintext in (b"UnityFS\0\x00\x01 a plaintext bundle", b"", b"AMP", b"ampb container", b"\0AMPB"):
                    with self.subTest(name=name, head=plaintext[:8]):
                        path.write_bytes(plaintext)
                        try:
                            with self.assertRaisesRegex(ValueError, f"Plaintext bundle in dist .*: {name}"):
                                self.validate()
                        finally:
                            path.write_bytes(original)
        self.validate()

    def test_every_bundle_is_inspected_with_its_chunks_and_its_prefix_read(self):
        self.validate()
        calls = self.crypt.calls
        for key in self.RELEASED:
            for kind in ("scenes", "navmesh", "preset"):
                name = f"{key}_{kind}.bundle"
                self.assertIn(("inspect", name, self.repo / "work" / "release-keys", True), calls, name)
                self.assertIn(("read_range", name, 0, 8), calls, name)
        self.assertEqual(sorted(c[1] for c in calls if c[0] == "verify_keys"), sorted(self.RELEASED))
        self.assertTrue(all(c[4] == "auto" for c in calls if c[0] == "verify_keys"))

    def test_a_bundle_the_tool_rejects_is_a_distribution_failure(self):
        for reason in ("FAIL ChunkIntegrity: chunk 3 (offset 3145728) failed its integrity check; the file is damaged",
                       "FAIL HeaderAuth: header authentication failed (not the bundle set of this key, or a modified file)",
                       "FAIL there is no content key for 'arena_bay5' in work/release-keys"):
            with self.subTest(reason=reason):
                self.crypt.fail[("inspect", "arena_bay5_scenes.bundle")] = reason
                try:
                    with self.assertRaisesRegex(ValueError, "Bundle arena_bay5_scenes.bundle failed inspection: " + re.escape(reason)):
                        self.validate()
                finally:
                    self.crypt.fail.clear()

    def test_a_container_of_another_map_or_with_another_plaintext_is_refused(self):
        self.crypt.map_key_of["arena_equator_preset.bundle"] = "arena_bay5"
        with self.assertRaisesRegex(ValueError, "Bundle arena_equator_preset.bundle belongs to map 'arena_bay5', not 'arena_equator'"):
            self.validate()
        self.crypt.map_key_of.clear()
        self.crypt.prefix_of["arena_bowl_navmesh.bundle"] = b"PK\x03\x04\0\0\0\0"
        with self.assertRaisesRegex(ValueError, "Bundle arena_bowl_navmesh.bundle does not decrypt to a UnityFS bundle"):
            self.validate()
        self.crypt.prefix_of.clear()
        self.crypt.fail[("read_range", "arena_bowl_navmesh.bundle")] = "FAIL the offset is outside the plaintext"
        with self.assertRaisesRegex(ValueError, "Bundle arena_bowl_navmesh.bundle cannot be read: FAIL the offset"):
            self.validate()

    def test_the_tools_answer_must_say_the_header_and_chunks_were_verified(self):
        real_inspect = self.crypt.inspect
        for answer in ({"mac_verified": None, "chunks": "verified"}, {"mac_verified": True, "chunks": None},
                       {"mac_verified": True, "chunks": "consistent-with-unauthenticated-table"}):
            with self.subTest(answer=answer):
                self.crypt.inspect = lambda path, key_store=None, verify_chunks=False, answer=answer: {**real_inspect(path, key_store, verify_chunks), **answer}
                try:
                    with self.assertRaisesRegex(ValueError, "was not verified"):
                        self.validate()
                finally:
                    del self.crypt.inspect
        self.validate()

    def test_keys_hold_exactly_one_file_per_released_map(self):
        keys = self.plugin / "keys"
        for name in self.RELEASED:
            path = keys / f"{name}.json"
            original = path.read_bytes()
            path.unlink()
            try:
                with self.subTest(missing=name):
                    with self.assertRaisesRegex(ValueError, rf"Key file set mismatch: missing=\['{name}.json'\] unexpected=\[\]"):
                        self.validate()
            finally:
                path.write_bytes(original)
        for stray in ("arena_prison.json", "arena_equator.json.bak", "arena_equator.key", "notes.txt", "sub/arena_equator.json"):
            path = keys / stray
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("{}", encoding="utf-8")
            try:
                with self.subTest(unexpected=stray):
                    with self.assertRaisesRegex(ValueError, rf"Key file set mismatch: missing=\[\] unexpected=\['{re.escape(stray)}'\]"):
                        self.validate()
            finally:
                path.unlink()
                if path.parent != keys:
                    path.parent.rmdir()
        shutil.rmtree(keys)
        with self.assertRaisesRegex(ValueError, "Key file set mismatch: missing=\\['arena_airpit.json', 'arena_bay5.json', 'arena_bowl.json', 'arena_equator.json'\\]"):
            self.validate()

    def test_key_files_must_have_the_schema_and_nothing_else(self):
        level = lambda n, kind="assets.resS": source(n, kind, 1000)

        def mutate(change):
            def apply(data):
                change(data)
                return data
            return apply

        wrap0 = lambda d: d["wraps"][0]
        cases = {
            "the top level must hold exactly": mutate(lambda d: d.update(note="x")),
            "schema must be 2": mutate(lambda d: d.update(schema=1)),
            "schema must be 2 ": mutate(lambda d: d.update(schema=True)),
            "map_key must be 'arena_bowl'": mutate(lambda d: d.update(map_key="arena_bay5")),
            "wraps must list 1 to 16": mutate(lambda d: d.update(wraps=[])),
            "wraps must list 1 to 16 ": mutate(lambda d: d.update(wraps=[d["wraps"][0]] * 17)),
            "wrap 0 must hold exactly": mutate(lambda d: wrap0(d).update(sha256="a" * 64)),
            "wrap 0 must hold exactly ": mutate(lambda d: wrap0(d).pop("check")),
            "built_for must be a short printable string": mutate(lambda d: wrap0(d).update(built_for="")),
            "built_for must be a short printable string ": mutate(lambda d: wrap0(d).update(built_for="x" * 65)),
            "built_for must be a short printable string  ": mutate(lambda d: wrap0(d).update(built_for="0.6\n1")),
            "wrap 0 must hold exactly  ": mutate(lambda d: wrap0(d).pop("sample")),
            "sample must hold exactly block_size and blocks": mutate(lambda d: wrap0(d).update(sample=[65536, 16])),
            "sample must hold exactly block_size and blocks ": mutate(lambda d: wrap0(d)["sample"].update(offset=0)),
            "sample block_size must be a multiple of 4096": mutate(lambda d: wrap0(d)["sample"].update(block_size=65537)),
            "sample block_size must be a multiple of 4096 ": mutate(lambda d: wrap0(d)["sample"].update(block_size=2048)),
            "sample block_size must be a multiple of 4096  ": mutate(lambda d: wrap0(d)["sample"].update(block_size=True)),
            "sample blocks must be 2 to 1024": mutate(lambda d: wrap0(d)["sample"].update(blocks=1)),
            "sample blocks must be 2 to 1024 ": mutate(lambda d: wrap0(d)["sample"].update(blocks=1025)),
            "sample blocks must be 2 to 1024  ": mutate(lambda d: wrap0(d)["sample"].update(block_size=1 << 20, blocks=65)),
            "files must list 1 to 32": mutate(lambda d: wrap0(d).update(files=[])),
            "files must list 1 to 32 ": mutate(lambda d: wrap0(d).update(files=[level(n) for n in range(1, 34)])),
            "a file entry must hold exactly path and size": mutate(lambda d: wrap0(d)["files"][0].update(sha256="a" * 64)),
            "a file entry must hold exactly path and size ": mutate(lambda d: wrap0(d)["files"][0].pop("size")),
            "is not EscapeFromTarkovArena_Data/sharedassets<N>.assets.resS or .resource": mutate(
                lambda d: wrap0(d)["files"][0].update(path="EscapeFromTarkovArena_Data/../sharedassets41.assets.resS")),
            "is not EscapeFromTarkovArena_Data/sharedassets<N>.assets.resS or .resource ": mutate(
                lambda d: wrap0(d)["files"][0].update(path="EscapeFromTarkovArena_Data\\sharedassets41.assets.resS")),
            "is not EscapeFromTarkovArena_Data/sharedassets<N>.assets.resS or .resource  ": mutate(
                lambda d: wrap0(d)["files"][0].update(path="C:/sharedassets41.resource")),
            "is not EscapeFromTarkovArena_Data/sharedassets<N>.assets.resS or .resource   ": mutate(
                lambda d: wrap0(d)["files"][0].update(path="EscapeFromTarkovArena_Data/globalgamemanagers")),
            "is not EscapeFromTarkovArena_Data/sharedassets<N>.assets.resS or .resource    ": mutate(
                lambda d: wrap0(d)["files"][0].update(path="EscapeFromTarkovArena_Data/level41")),  # the retired level-file scheme
            "is not EscapeFromTarkovArena_Data/sharedassets<N>.assets.resS or .resource     ": mutate(
                lambda d: wrap0(d)["files"][0].update(path="EscapeFromTarkovArena_Data/sharedassets41.assets")),
            "ascending numeric order": mutate(lambda d: wrap0(d).update(files=[level(43), level(41)])),
            "ascending numeric order ": mutate(lambda d: wrap0(d).update(files=[level(41), level(41)])),
            "ascending numeric order  ": mutate(lambda d: wrap0(d).update(files=[level(9), level(10), level(2)])),
            "ascending numeric order, .assets.resS before .resource": mutate(
                lambda d: wrap0(d).update(files=[level(41, "resource"), level(41)])),
            "must be a positive integer": mutate(lambda d: wrap0(d)["files"][0].update(size=0)),
            "must be a positive integer ": mutate(lambda d: wrap0(d)["files"][0].update(size=-5)),
            "must be a positive integer  ": mutate(lambda d: wrap0(d)["files"][0].update(size=True)),
            "must be a positive integer   ": mutate(lambda d: wrap0(d)["files"][0].update(size="1000")),
            "salt must be 64 lowercase hex digits": mutate(lambda d: wrap0(d).update(salt="a" * 63)),
            "wrapped_key must be 64 lowercase hex digits": mutate(lambda d: wrap0(d).update(wrapped_key="A" * 64)),
            "check must be 64 lowercase hex digits": mutate(lambda d: wrap0(d).update(check="g" * 64)),
            "check must be 64 lowercase hex digits ": mutate(lambda d: wrap0(d).update(check=None)),
        }
        path = self.plugin / "keys/arena_bowl.json"
        original = path.read_bytes()
        for fragment, change in cases.items():
            with self.subTest(fragment=fragment):
                path.write_text(key_file_text(change(key_file_data("arena_bowl"))), encoding="utf-8", newline="\n")
                try:
                    with self.assertRaisesRegex(ValueError, r"Key file arena_bowl\.json is invalid: .*" + re.escape(fragment.strip())):
                        self.validate()
                finally:
                    path.write_bytes(original)
        self.validate()

    def test_key_files_must_be_canonical_utf8_json_of_a_sane_size(self):
        path = self.plugin / "keys/arena_airpit.json"
        original = path.read_bytes()
        text = original.decode("utf-8")
        for message, data in (("is not valid UTF-8 JSON", b"{"), ("is not valid UTF-8 JSON", b"\xff\xfe not utf-8\n"),
                              ("is not valid UTF-8 JSON", b""), ("is invalid: the top level must hold", b"[]\n"),
                              ("is not valid UTF-8 JSON", b"\xef\xbb\xbf" + original),
                              ("is not in canonical form", text.replace("\n", "\r\n").encode("utf-8")),
                              ("is not in canonical form", original.rstrip(b"\n")),
                              ("is larger than 64 KiB", original + b" " * (64 * 1024))):
            with self.subTest(message=message, head=data[:12]):
                path.write_bytes(data)
                try:
                    with self.assertRaisesRegex(ValueError, "Key file arena_airpit.json " + message):
                        self.validate()
                finally:
                    path.write_bytes(original)

    def test_a_key_file_that_does_not_unlock_fails_the_distribution(self):
        self.crypt.fail[("verify_keys", "arena_bay5")] = ("FAIL verify-keys arena_bay5: the key file does not unlock against Arena 0.6.1.0.48206 "
                                                           "(SizeDiffers: 'EscapeFromTarkovArena_Data/sharedassets84.assets.resS' is 1 bytes, 2 expected)")
        with self.assertRaisesRegex(ValueError, "Key file arena_bay5.json does not unlock: FAIL verify-keys arena_bay5: .*SizeDiffers"):
            self.validate()

    def test_a_plaintext_unityfs_file_anywhere_in_dist_is_refused(self):
        for relative in ("stray.dat", "BepInEx/plugins/other/copy.bin", "SPT_Runtime/user/mods/ArenaMapPort/backup/arena_bay5_scenes.bundle.bak",
                         "BepInEx/plugins/maschine-ArenaMapPort/UnityFS.txt"):
            path = self.repo / "dist" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"UnityFS\0" + b"\x06 plaintext bundle")
            try:
                with self.subTest(relative=relative):
                    with self.assertRaisesRegex(ValueError, "Plaintext UnityFS file in dist: " + re.escape(relative)):
                        self.validate()
            finally:
                path.unlink()
                if path.parent.name in ("other", "backup"):
                    path.parent.rmdir()
        # Only the exact 8-byte signature counts: a text file that merely mentions it, or a shorter prefix, does not.
        (self.repo / "dist/note.txt").write_bytes(b"this mentions UnityFS but does not start with it")
        (self.repo / "dist/short.bin").write_bytes(b"UnityFS")
        self.validate()

    def test_the_encryption_checks_run_after_the_bundle_set_check(self):
        (self.plugin / "bundles/unregistered_scenes.bundle").write_bytes(b"UnityFS\0 stray")
        with self.assertRaisesRegex(ValueError, "Bundle set mismatch"):
            self.validate()

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

    def test_bowl_carries_four_loadscreens_an_overview_and_flares_but_no_water(self):
        # PLAN-BOWL item 8: Arena ships no loading screen for Bowl, only the four main-menu backgrounds, so the count of
        # the rotation comes from its LOADSCREENS list and a fifth image (or a missing fourth) is a set mismatch.
        self.assertEqual(4, len(extract_loadscreens.LOADSCREENS["arena_bowl"]))
        self.assertTrue(all(len(extract_loadscreens.LOADSCREENS[k]) == 5 for k in ("arena_equator", "arena_bay5", "arena_airpit")))
        self.assertEqual({f"arena_bowl_{n}.jpg" for n in range(1, 5)},
                         {p.name for p in (self.plugin / "loadscreens").glob("arena_bowl_*.jpg")})
        self.validate()
        fifth = self.plugin / "loadscreens/arena_bowl_5.jpg"
        fifth.write_bytes(b"\xff\xd8\xff fake fifth image")
        try:
            with self.assertRaisesRegex(ValueError, "Loadscreen file set mismatch"):
                self.validate()
        finally:
            fifth.unlink()
        fourth = self.plugin / "loadscreens/arena_bowl_4.jpg"
        original = fourth.read_bytes()
        fourth.unlink()
        try:
            with self.assertRaisesRegex(ValueError, "Loadscreen file set mismatch"):
                self.validate()
        finally:
            fourth.write_bytes(original)
        # Since 0.3.0 Bowl ships an overview pack and a flare package; both are required, exactly. It has no water
        # package, and none may appear before it is listed on purpose.
        self.assertTrue((self.plugin / "dynamicmaps/ArenaMapPort_Bowl/Layers/Ground.svg").exists())
        self.assertTrue((self.plugin / "flares/arena_bowl/flares.json").exists())
        self.assertFalse((self.plugin / "water/arena_bowl").exists())
        stray = self.plugin / "dynamicmaps/ArenaMapPort_Bowl/Layers/Unexpected.svg"
        stray.write_text("<svg/>", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Unexpected or missing overview"):
            self.validate()
        stray.unlink()
        overview = self.plugin / "dynamicmaps/ArenaMapPort_Bowl/ArenaMapPort_Bowl.jsonc"
        original = overview.read_bytes()
        overview.unlink()
        try:
            with self.assertRaises(FileNotFoundError):
                self.validate()
        finally:
            overview.write_bytes(original)
        (self.plugin / "dynamicmaps/ArenaMapPort_Bowl/ArenaMapPort_Bowl.jsonc").write_bytes(b"stale bowl overview")
        with self.assertRaisesRegex(ValueError, "Stale overview map: ArenaMapPort_Bowl/ArenaMapPort_Bowl.jsonc"):
            self.validate()
        overview.write_bytes(original)
        flare = self.plugin / "flares/arena_bowl/textures/test.bin"
        flare_bytes = flare.read_bytes()
        flare.unlink()
        try:
            with self.assertRaises(FileNotFoundError):
                self.validate()
        finally:
            flare.write_bytes(flare_bytes)
        (self.plugin / "flares/arena_bowl/Unexpected.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Unexpected or missing flare package files"):
            self.validate()
        (self.plugin / "flares/arena_bowl/Unexpected.json").unlink()
        (self.plugin / "water/arena_bowl").mkdir(parents=True)
        (self.plugin / "water/arena_bowl/water.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Water file set mismatch"):
            self.validate()
        shutil.rmtree(self.plugin / "water/arena_bowl")
        self.validate()

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

    def gate_manifest(self, key, root=None):
        return (root or self.plugin) / "gate-sounds" / key / "gate-sounds.json"

    def change_gate_manifest(self, key, mutate, also_extracted=True):
        """Rewrite a staged manifest (and, by default, the extracted one it must equal) after `mutate(dict)`."""
        for path in (self.gate_manifest(key), self.repo / "work/extract/gate-sounds" / key / "gate-sounds.json"):
            if path == self.gate_manifest(key) or also_extracted:
                package = json.loads(path.read_text(encoding="utf-8"))
                mutate(package)
                path.write_text(json.dumps(package), encoding="utf-8")

    def test_distribution_requires_every_released_maps_gate_sounds_exactly_and_intact(self):
        self.validate()
        airpit = self.plugin / "gate-sounds/arena_airpit"
        wav = airpit / "arena_airpit_gate.wav"
        manifest = airpit / "gate-sounds.json"
        for path, message in ((wav, "Gate sounds file set mismatch"), (manifest, "Missing gate sound package: arena_airpit/gate-sounds.json")):
            with self.subTest(missing=path.name):
                original = path.read_bytes()
                path.unlink()
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    path.write_bytes(original)
        for path in (airpit / "stray.wav", self.plugin / "gate-sounds/arena_prison/gate-sounds.json", self.plugin / "gate-sounds/loose.json"):
            with self.subTest(unexpected=path.name):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"stray")
                try:
                    with self.assertRaisesRegex(ValueError, "Gate sounds file set mismatch"):
                        self.validate()
                finally:
                    path.unlink()
        blob = wav.read_bytes()
        wav.write_bytes(bytes([blob[0]]) + blob[1:-1] + bytes([blob[-1] ^ 1]))       # same length, other bytes: only the SHA-256 can tell
        with self.assertRaisesRegex(ValueError, "Gate sound file length/hash mismatch: arena_airpit/arena_airpit_gate.wav"):
            self.validate()
        wav.write_bytes(blob + b"!")
        with self.assertRaisesRegex(ValueError, "Gate sound file length/hash mismatch"):
            self.validate()
        wav.write_bytes(blob)
        self.validate()

    def test_a_staged_gate_sound_manifest_must_be_the_extracted_one(self):
        key = "arena_bay5"
        extracted = self.repo / "work/extract/gate-sounds" / key / "gate-sounds.json"
        extracted.write_text(extracted.read_text(encoding="utf-8").replace("arena_bay5_gate.wav", "arena_bay5_gate.wav "), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Stale gate sound package: arena_bay5"):
            self.validate()
        extracted.unlink()
        with self.assertRaisesRegex(ValueError, "Stale gate sound package: arena_bay5"):
            self.validate()

    def test_gate_sound_rows_must_be_the_tracked_rows_and_resolve_to_a_door_pose(self):
        key, stem = "arena_bowl", "bowl"
        with self.assertRaisesRegex(ValueError, "Gate sound rows differ from tools/maps/bowl.gatesounds.json"):
            self.change_gate_manifest(key, lambda p: p["rows"][0].update(max_distance=99.0))
            self.validate()
        self.change_gate_manifest(key, lambda p: p["rows"][0].update(max_distance=30.0))
        self.validate()
        # a row that names a barrier without a door pose is refused, even when the tracked sidecar says the same
        sidecar = self.maps / f"{stem}.gatesounds.json"
        document = json.loads(sidecar.read_text(encoding="utf-8"))
        document["rows"][0]["barrier"] = "NOWHERE/Gate"
        sidecar.write_text(json.dumps(document), encoding="utf-8")
        self.change_gate_manifest(key, lambda p: p["rows"][0].update(barrier="NOWHERE/Gate"))
        with self.assertRaisesRegex(ValueError, "Gate sound row matches no door pose: arena_bowl NOWHERE/Gate"):
            self.validate()

    def test_gate_sound_rows_play_at_pitch_one_and_the_mixer_is_the_gates_minus_fifteen(self):
        sidecar = self.maps / "equator.gatesounds.json"
        for label, mutate, message in (
                ("pitch", lambda row: row.update(pitch=1.25), "not playable at pitch 1"),
                ("mixer db", lambda row: row.update(mixer_db=-3.0), "not playable at pitch 1"),
                ("clip", lambda row: row.update(clip="other"), "not playable at pitch 1")):
            with self.subTest(label):
                original = sidecar.read_text(encoding="utf-8")
                document = json.loads(original)
                mutate(document["rows"][0])
                sidecar.write_text(json.dumps(document), encoding="utf-8")
                self.change_gate_manifest("arena_equator", lambda p: mutate(p["rows"][0]))
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    sidecar.write_text(original, encoding="utf-8")
                    self.change_gate_manifest("arena_equator", lambda p: p["rows"][0].update(pitch=1.0, mixer_db=-15.0, clip="arena_equator_gate"))
        self.validate()
        with self.assertRaisesRegex(ValueError, "Gate sound mixer is not ArenaGates at -15.0 dB: arena_equator"):
            self.change_gate_manifest("arena_equator", lambda p: p["mixer"].update(db=-12.0))
            self.validate()

    def test_a_gate_clip_that_is_a_key_source_file_must_be_pitch_baked(self):
        # AirPit's passport clip lives in sharedassets28.resource, a key source of the map: shipped as decoded it would publish the file
        def unbake(package):
            clip = package["clips"][0]
            clip.update(pitch_baked=False, baked_pitch=1.0)
        self.change_gate_manifest("arena_airpit", unbake)
        with self.assertRaisesRegex(ValueError, "copy of a key-source file of the map, bake its pitch: arena_airpit/arena_airpit_gate"):
            self.validate()
        # a clip of another file is fine unbaked (Equator's lives in sharedassets56.resource, no key source), but must not claim a bake it lacks
        self.change_gate_manifest("arena_airpit", lambda p: p["clips"][0].update(pitch_baked=True, baked_pitch=1.25))
        self.validate()
        self.change_gate_manifest("arena_equator", lambda p: p["clips"][0].update(pitch_baked=True))
        with self.assertRaisesRegex(ValueError, "pitch_baked disagrees with baked_pitch: arena_equator"):
            self.validate()

    def test_distribution_requires_the_score_monitor_artwork_exactly_and_intact(self):
        self.validate()
        root = self.plugin / "monitors"
        for path, message in ((root / "Fixture_Icon.png", "file set differs|is missing"), (root / "monitors.json", "is invalid|is missing")):
            with self.subTest(missing=path.name):
                original = path.read_bytes()
                path.unlink()
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate()
                finally:
                    path.write_bytes(original)
        for path in (root / "Fixture_Extra.png", root / "notes.txt", root / "nested" / "Fixture_Icon.png"):
            with self.subTest(unexpected=path.name):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"stray")
                try:
                    with self.assertRaisesRegex(ValueError, "Score monitor artwork is invalid"):
                        self.validate()
                finally:
                    path.unlink()
                    if path.parent != root:
                        path.parent.rmdir()
        shutil.rmtree(root)
        with self.assertRaisesRegex(ValueError, "Missing score monitor artwork"):
            self.validate()

    def test_a_changed_score_monitor_png_is_refused_by_length_hash_and_pixels(self):
        root = self.plugin / "monitors"
        png = root / "Fixture_Frame.png"
        original = png.read_bytes()
        png.write_bytes(original + b"!")
        with self.assertRaisesRegex(ValueError, "does not match its manifest entry"):
            self.validate()
        # same length, other bytes: only the SHA-256 can tell
        png.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
        with self.assertRaisesRegex(ValueError, "does not match its manifest entry|cannot identify|broken|invalid"):
            self.validate()
        png.write_bytes(original)
        # a repainted sprite with its manifest entry made consistent still fails the pinned pixels
        other = Image.new("RGBA", (16, 8), (1, 2, 3, 255))
        blob = extract_monitors.png_bytes(other)
        png.write_bytes(blob)
        manifest = json.loads((root / "monitors.json").read_text(encoding="utf-8"))
        entry = next(e for e in manifest["sprites"] if e["name"] == "Fixture_Frame")
        entry.update(bytes=len(blob), sha256=hashlib.sha256(blob).hexdigest())
        (root / "monitors.json").write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "artwork changed"):
            self.validate()

    def test_a_staged_score_monitor_file_must_be_the_extracted_one(self):
        extracted = self.repo / "work/extract/monitors"
        # the extracted package changed after staging (same manifest, other bytes in one PNG): the staged copy is stale
        png = extracted / "Fixture_Icon.png"
        data = png.read_bytes()
        png.write_bytes(data[:-1] + bytes([data[-1] ^ 1]))
        with self.assertRaisesRegex(ValueError, "Stale score monitor artwork file: Fixture_Icon.png"):
            self.validate()
        png.write_bytes(data)
        (extracted / "Fixture_Icon.png").unlink()
        with self.assertRaisesRegex(ValueError, "file set differs from work/extract/monitors"):
            self.validate()
        png.write_bytes(data)
        (extracted / "monitors.json").write_text((extracted / "monitors.json").read_text(encoding="utf-8").replace("0.6.1.0.48206", "0.6.2.0.1"),
                                                 encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Stale score monitor artwork file: monitors.json"):
            self.validate()

    def test_a_score_monitor_manifest_may_not_change_the_reviewed_table(self):
        root = self.plugin / "monitors"
        extracted = self.repo / "work/extract/monitors"
        original = (root / "monitors.json").read_text(encoding="utf-8")
        for label, mutate in (("size", lambda m: m["sprites"][0].update(width=9)),
                              ("order", lambda m: m["sprites"].reverse()),
                              ("border", lambda m: m["sprites"][1].update(border=[0, 0, 0, 0])),
                              ("pivot", lambda m: m["sprites"][0].update(pivot=[0.0, 0.0])),
                              ("faces", lambda m: m["sprites"][2].update(used_by=["alive"])),
                              ("extra key", lambda m: m["sprites"][0].update(note="x")),
                              ("schema", lambda m: m.update(schema_version=2)),
                              ("source", lambda m: m["sprites"][0].update(source={"asset": "other.assets"}))):
            with self.subTest(label):
                manifest = json.loads(original)
                mutate(manifest)
                text = json.dumps(manifest)
                for folder in (root, extracted):
                    (folder / "monitors.json").write_text(text, encoding="utf-8")
                try:
                    with self.assertRaisesRegex(ValueError, "Score monitor artwork is invalid"):
                        self.validate()
                finally:
                    for folder in (root, extracted):
                        (folder / "monitors.json").write_text(original, encoding="utf-8", newline="\n")
        self.validate()

    def test_a_score_monitor_sprite_from_a_key_source_file_is_refused(self):
        # the art is plaintext: it must never be sampled bytes of a stage-2 key source (the asset or its .resS stream)
        sources = self.repo / "tools/bundle/key-sources.json"
        original = sources.read_text(encoding="utf-8")
        document = json.loads(original)
        document["maps"]["arena_equator"][0]["files"].append("sharedassets1.assets.resS")
        sources.write_text(json.dumps(document), encoding="utf-8")
        try:
            with self.assertRaisesRegex(ValueError, "comes from a key-source file of the maps: Fixture_Ground"):
                self.validate()
        finally:
            sources.write_text(original, encoding="utf-8")
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
        for path in (airpit / "stray.wav", self.plugin / "acoustics/arena_prison/spatial-audio.json",
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
        extra = {**extract_loadscreens.LOADSCREENS, "arena_prison": extract_loadscreens.LOADSCREENS["arena_airpit"]}
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
