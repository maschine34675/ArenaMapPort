"""The artwork package of the score monitor faces (docs/progress/score-monitors.md): tools/extract/extract_monitor_templates.py.

The package rules run on synthetic sprites (no Arena file) with the extractor's sprite table replaced by a tiny one; the layout agreement reads the tracked
ArenaMapPort.Client/ScoreMonitorLayout.cs. One real-data class reads sharedassets1.assets of the installed Arena and skips without Arena or UnityPy.
"""
import contextlib
import copy
import hashlib
import io
import json
import re
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from PIL import Image

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
import extract_monitor_templates as E  # noqa: E402

LAYOUT = REPO / "ArenaMapPort.Client" / "ScoreMonitorLayout.cs"
ARENA_DATA = E.DEFAULT_SOURCE
HAVE_ARENA = (ARENA_DATA / "sharedassets1.assets").is_file()

# The sprites each face draws (ScoreMonitorLayout.SpritesOf pins the same lists on the C# side): the timer, the score face (also the final-score word),
# the alive face with its toggles. The extractor's `used_by` column must be exactly this.
FACE_SPRITES = {
    "timer": {"Arena_Scoreboard_AllBoards_Background", "Arena_Scoreboard_Timer_Glow_White", "Arena_Scoreboard_AllBoards_Vignette"},
    "score": {"Arena_Scoreboard_AllBoards_Icon_Glow", "Arena_Scoreboard_AllBoards_Icon_Background", "Arena_Scoreboard_Score_Icon_Scull",
              "Arena_Scoreboard_AllBoards_Vignette"},
    "alive": {"Arena_Scoreboard_AllBoards_Icon_Glow", "Arena_Scoreboard_AllBoards_Icon_Background", "Arena_Scoreboard_Status_Icon_Scull",
              "Arena_Scoreboard_Status_Toggle_On", "Arena_Scoreboard_Status_Toggle_Off", "Arena_Scoreboard_AllBoards_Vignette"},
}


def tiny_table():
    """Three tiny sprites with pins of their own pixels, standing in for the real artwork."""
    images, table = {}, {}
    for index, (name, size, border, faces) in enumerate((("Tiny_Ground", (8, 8), (0, 0, 0, 0), ("timer",)),
                                                         ("Tiny_Frame", (16, 8), (2, 2, 2, 2), ("timer", "score", "alive")),
                                                         ("Tiny_Icon", (4, 4), (0, 0, 0, 0), ("score",)))):
        image = Image.new("RGBA", size, (10 + index, 20, 30, 255 if index == 0 else 128))
        image.putpixel((1, 1), (200, 100, 50 + index, 255))
        images[name] = image
        table[name] = {"size": size, "border": border, "used_by": faces, "pixels_sha256": E.pixel_hash(image)}
    return images, table


def write_package(folder: Path, images, version="0.6.1.0.48206"):
    entries = []
    for name, image in images.items():
        blob = E.png_bytes(image)
        (folder / E.file_name(name)).write_bytes(blob)
        entries.append(E.manifest_entry(name, blob, {"asset": "sharedassets1.assets", "sprite_path_id": 1, "texture_path_id": 2, "texture_format": 4}))
    (folder / E.MANIFEST).write_text(E.render_manifest(version, entries), encoding="utf-8", newline="\n")


class PinTests(unittest.TestCase):
    def test_every_sprite_has_a_pixel_pin_a_known_face_list_and_a_border_that_fits(self):
        E.check_pins_complete()
        self.assertEqual(len(E.SPRITES), 9)
        for name, pins in E.SPRITES.items():
            with self.subTest(name):
                self.assertRegex(pins["pixels_sha256"], r"^[0-9a-f]{64}$")
                self.assertTrue(name.startswith("Arena_Scoreboard_"))
        self.assertEqual({n for n, p in E.SPRITES.items() if any(p["border"])}, {"Arena_Scoreboard_AllBoards_Vignette"})
        self.assertEqual(E.SPRITES["Arena_Scoreboard_AllBoards_Vignette"]["border"], (80, 80, 80, 80))

    def test_the_noise_overlays_and_the_interference_texture_are_not_exported(self):
        for name in E.SPRITES:
            self.assertNotIn("Noise", name)
            self.assertNotIn("Interference", name)

    def test_incomplete_pins_stop_the_tool(self):
        _images, table = tiny_table()
        for label, change in (("no pin", lambda t: t["Tiny_Icon"].update(pixels_sha256="")),
                              ("short pin", lambda t: t["Tiny_Icon"].update(pixels_sha256="abc")),
                              ("unknown face", lambda t: t["Tiny_Icon"].update(used_by=("splash",))),
                              ("no face", lambda t: t["Tiny_Icon"].update(used_by=())),
                              ("border too wide", lambda t: t["Tiny_Icon"].update(border=(3, 0, 3, 0))),
                              ("negative border", lambda t: t["Tiny_Icon"].update(border=(-1, 0, 0, 0)))):
            broken = copy.deepcopy(table)
            change(broken)
            with self.subTest(label), patch.object(E, "SPRITES", broken), self.assertRaises(E.MonitorTemplateError):
                E.check_pins_complete()

    def test_used_by_is_exactly_what_the_three_faces_draw(self):
        for face, names in FACE_SPRITES.items():
            self.assertEqual({n for n, p in E.SPRITES.items() if face in p["used_by"]}, names, face)
        self.assertEqual(set().union(*FACE_SPRITES.values()), set(E.SPRITES))


class LayoutAgreementTests(unittest.TestCase):
    """ScoreMonitorLayout.cs and the extractor's table name the same sprites at the same sizes: a face can only be drawn from what the package holds."""

    @classmethod
    def setUpClass(cls):
        cls.text = LAYOUT.read_text(encoding="utf-8")
        cls.constants = dict(re.findall(r'internal const string (Sprite\w+) = "([^"]+)";', cls.text))
        table = cls.text[cls.text.index("internal static readonly ScoreMonitorSprite[] Sprites"):]
        table = table[:table.index("};")]
        cls.rows = [(cls.constants[c], int(w), int(h), int(b))
                    for c, w, h, b in re.findall(r"new ScoreMonitorSprite\((Sprite\w+), (\d+), (\d+), (\d+)\)", table)]

    def test_the_sprite_names_sizes_and_borders_agree_in_order(self):
        self.assertEqual(self.rows, [(name, p["size"][0], p["size"][1], p["border"][0]) for name, p in E.SPRITES.items()])
        for _name, _w, _h, border in self.rows:
            self.assertIn(border, (0, 80))
        for name, pins in E.SPRITES.items():
            self.assertEqual(len(set(pins["border"])), 1, name)           # one border value on all four sides, the layout stores one number

    def test_every_constant_is_in_the_table_and_the_other_way_round(self):
        self.assertEqual(set(self.constants.values()), set(E.SPRITES))
        self.assertEqual(len(self.constants), 9)

    def test_the_canvas_and_the_reader_side_note_are_documented(self):
        self.assertIn("1930 x 1163", self.text)
        self.assertIn("Arena's own", self.text)


class SourceGuardTests(unittest.TestCase):
    def test_the_sprites_source_is_no_stage_two_key_source(self):
        protected = E.key_source_files()
        self.assertIn("sharedassets28.resource", protected)               # the table is read
        self.assertNotIn(E.SOURCE_ASSET, protected)
        self.assertNotIn(E.SOURCE_ASSET + ".resS", protected)
        self.assertNotIn("sharedassets1.resource", protected)
        # no listed map scene is the sprites' own asset: index 1 is the menu / bootstrap data, never a map scene
        table = json.loads(E.KEY_SOURCES.read_text(encoding="utf-8"))["maps"]
        self.assertFalse([e for entries in table.values() for e in entries if e["index"] == 1])

    def test_a_key_source_asset_would_stop_the_export(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / E.SOURCE_ASSET).write_bytes(b"x")
            with patch.object(E, "key_source_files", lambda: {E.SOURCE_ASSET + ".resS", "sharedassets28.resource"}), \
                    patch.dict(sys.modules, {"UnityPy": type(sys)("UnityPy")}):
                with self.assertRaisesRegex(E.MonitorTemplateError, "key-source file now"):
                    E.read_sprites(Path(tmp))

    def test_the_package_cannot_be_written_into_the_arena_install(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(E.MonitorTemplateError, "outside the Arena source"):
                E.extract(tmp, Path(tmp) / "output")
        with self.assertRaisesRegex(E.MonitorTemplateError, "outside the Arena source"):
            E.extract(Path("D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"),
                      Path("D:/Battlestate Games/Escape from Tarkov Arena/monitors-test"))

    def test_a_missing_source_is_an_error_not_a_crash(self):
        with tempfile.TemporaryDirectory() as tmp, self.assertRaises(E.MonitorTemplateError):
            E.extract(Path(tmp) / "no-arena", Path(tmp) / "out")


class PngTests(unittest.TestCase):
    def test_an_opaque_sprite_is_saved_as_rgb_and_one_with_alpha_as_rgba(self):
        opaque = Image.new("RGBA", (6, 4), (1, 2, 3, 255))
        translucent = Image.new("RGBA", (6, 4), (1, 2, 3, 254))
        self.assertEqual(Image.open(io.BytesIO(E.png_bytes(opaque))).mode, "RGB")
        self.assertEqual(Image.open(io.BytesIO(E.png_bytes(translucent))).mode, "RGBA")

    def test_the_bytes_are_deterministic_and_the_pixel_hash_ignores_the_colour_mode(self):
        image = Image.new("RGBA", (9, 5), (7, 8, 9, 255))
        image.putpixel((2, 2), (200, 1, 2, 255))
        self.assertEqual(E.png_bytes(image), E.png_bytes(image.copy()))
        again = Image.open(io.BytesIO(E.png_bytes(image)))
        self.assertEqual(E.pixel_hash(again), E.pixel_hash(image))
        self.assertEqual(E.pixel_hash(image.convert("RGB")), E.pixel_hash(image))           # a fully opaque RGB image hashes like its RGBA twin
        other = image.copy()
        other.putpixel((0, 0), (1, 1, 1, 255))
        self.assertNotEqual(E.pixel_hash(other), E.pixel_hash(image))

    def test_check_image_rejects_another_size_and_changed_pixels(self):
        images, table = tiny_table()
        with patch.object(E, "SPRITES", table):
            for name, image in images.items():
                E.check_image(name, image)
            with self.assertRaisesRegex(E.MonitorTemplateError, "is .* expected"):
                E.check_image("Tiny_Icon", Image.new("RGBA", (5, 4)))
            changed = images["Tiny_Icon"].copy()
            changed.putpixel((3, 3), (9, 9, 9, 255))
            with self.assertRaisesRegex(E.MonitorTemplateError, "artwork changed"):
                E.check_image("Tiny_Icon", changed)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.images, table = tiny_table()
        patcher = patch.object(E, "SPRITES", table)
        patcher.start()
        self.addCleanup(patcher.stop)
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        write_package(self.folder, self.images)

    def manifest(self):
        return json.loads((self.folder / E.MANIFEST).read_text(encoding="utf-8"))

    def put_manifest(self, manifest):
        (self.folder / E.MANIFEST).write_text(json.dumps(manifest), encoding="utf-8")

    def test_a_complete_package_validates_and_the_manifest_is_one_sprite_per_line(self):
        manifest = E.validate(self.folder)
        self.assertEqual([s["name"] for s in manifest["sprites"]], list(self.images))
        lines = (self.folder / E.MANIFEST).read_text(encoding="utf-8").splitlines()
        self.assertEqual(sum(1 for l in lines if l.startswith('    {"name"')), 3)
        entry = manifest["sprites"][1]
        self.assertEqual((entry["width"], entry["height"], entry["border"], entry["pivot"], entry["ppu"]), (16, 8, [2, 2, 2, 2], [0.5, 0.5], 100.0))
        self.assertEqual(entry["file"], "Tiny_Frame.png")
        self.assertEqual(entry["used_by"], ["timer", "score", "alive"])
        data = (self.folder / entry["file"]).read_bytes()
        self.assertEqual((entry["bytes"], entry["sha256"]), (len(data), hashlib.sha256(data).hexdigest()))

    def test_a_missing_manifest_or_png_is_refused(self):
        (self.folder / "Tiny_Icon.png").unlink()
        with self.assertRaisesRegex(E.MonitorTemplateError, "missing"):
            E.validate(self.folder)
        write_package(self.folder, self.images)
        (self.folder / E.MANIFEST).unlink()
        with self.assertRaisesRegex(E.MonitorTemplateError, "missing"):
            E.validate(self.folder)

    def test_extra_payload_is_refused(self):
        for name in ("notes.txt", "Tiny_Extra.png", "Thumbs.db"):
            with self.subTest(name):
                (self.folder / name).write_bytes(b"x")
                try:
                    with self.assertRaisesRegex(E.MonitorTemplateError, "unexpected monitor package payload"):
                        E.validate(self.folder)
                finally:
                    (self.folder / name).unlink()
        (self.folder / "nested").mkdir()
        with self.assertRaisesRegex(E.MonitorTemplateError, "unexpected monitor package payload"):
            E.validate(self.folder)

    def test_a_damaged_or_swapped_png_is_refused(self):
        png = self.folder / "Tiny_Frame.png"
        original = png.read_bytes()
        png.write_bytes(original + b"!")
        with self.assertRaisesRegex(E.MonitorTemplateError, "does not match its manifest entry"):
            E.validate(self.folder)
        png.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
        with self.assertRaisesRegex(E.MonitorTemplateError, "does not match its manifest entry"):
            E.validate(self.folder)
        # another picture of the same size, with the manifest made consistent: the pins catch it
        other = E.png_bytes(Image.new("RGBA", (16, 8), (9, 9, 9, 128)))
        png.write_bytes(other)
        manifest = self.manifest()
        manifest["sprites"][1].update(bytes=len(other), sha256=hashlib.sha256(other).hexdigest())
        self.put_manifest(manifest)
        with self.assertRaisesRegex(E.MonitorTemplateError, "artwork changed"):
            E.validate(self.folder)

    def test_the_manifest_cannot_redirect_or_reorder_or_extend(self):
        for label, change in (("schema", lambda m: m.update(schema_version=2)),
                              ("top level key", lambda m: m.update(extra=1)),
                              ("no version", lambda m: m.pop("arena_version")),
                              ("order", lambda m: m["sprites"].reverse()),
                              ("missing sprite", lambda m: m["sprites"].pop()),
                              ("extra sprite", lambda m: m["sprites"].append(copy.deepcopy(m["sprites"][0]))),
                              ("file", lambda m: m["sprites"][0].update(file="../Tiny_Ground.png")),
                              ("width", lambda m: m["sprites"][0].update(width=9)),
                              ("pivot", lambda m: m["sprites"][0].update(pivot=[0.0, 0.0])),
                              ("ppu", lambda m: m["sprites"][0].update(ppu=50.0)),
                              ("border", lambda m: m["sprites"][1].update(border=[0, 0, 0, 0])),
                              ("faces", lambda m: m["sprites"][2].update(used_by=["alive"])),
                              ("pin", lambda m: m["sprites"][0].update(pixels_sha256="0" * 64)),
                              ("entry key", lambda m: m["sprites"][0].update(note="x")),
                              ("source asset", lambda m: m["sprites"][0]["source"].update(asset="level53")),
                              ("source keys", lambda m: m["sprites"][0]["source"].pop("texture_format"))):
            with self.subTest(label):
                manifest = self.manifest()
                change(manifest)
                self.put_manifest(manifest)
                try:
                    with self.assertRaises(E.MonitorTemplateError):
                        E.validate(self.folder)
                finally:
                    write_package(self.folder, self.images)
        E.validate(self.folder)

    def test_extract_replaces_a_stale_file_and_keeps_the_package_exact(self):
        # the package writer used by extract(): feed it the synthetic sprites through read_sprites
        info = {"asset": "sharedassets1.assets", "sprite_path_id": 7, "texture_path_id": 8, "texture_format": 4}
        sprites = {n: (img, info) for n, img in self.images.items()}
        (self.folder / "stale.png").write_bytes(b"old")
        with patch.object(E, "read_sprites", lambda source: (sprites, "0.6.1.0.48206")), tempfile.TemporaryDirectory() as source:
            manifest = E.extract(source, self.folder)
        self.assertEqual({p.name for p in self.folder.iterdir()}, {E.MANIFEST, "Tiny_Ground.png", "Tiny_Frame.png", "Tiny_Icon.png"})
        self.assertEqual(manifest["sprites"][0]["source"], info)
        self.assertEqual(manifest["arena_version"], "0.6.1.0.48206")

    def test_the_cli_reports_success_and_failure(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(E.main(["--check", "--output", str(self.folder)]), 0)
        self.assertIn("MONITOR_TEMPLATES_OK", out.getvalue())
        (self.folder / "Tiny_Icon.png").unlink()
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            self.assertEqual(E.main(["--check", "--output", str(self.folder)]), 1)
        self.assertIn("MONITOR_TEMPLATES_FAILED", err.getvalue())


@unittest.skipUnless(HAVE_ARENA, "Arena is not installed")
class RealArenaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")
        cls.sprites, cls.version = E.read_sprites(ARENA_DATA)

    def test_the_installed_sprites_are_the_reviewed_artwork(self):
        self.assertEqual(list(self.sprites), list(E.SPRITES))
        for name, (image, info) in self.sprites.items():
            with self.subTest(name):
                self.assertEqual(image.size, tuple(E.SPRITES[name]["size"]))
                self.assertEqual(E.pixel_hash(image), E.SPRITES[name]["pixels_sha256"])
                self.assertEqual(info["asset"], "sharedassets1.assets")

    def test_the_exported_package_validates_and_is_the_one_the_client_stages(self):
        with tempfile.TemporaryDirectory() as tmp:
            manifest = E.extract(ARENA_DATA, Path(tmp) / "out")
            self.assertEqual(len(manifest["sprites"]), 9)
            for entry in manifest["sprites"]:
                with Image.open(Path(tmp) / "out" / entry["file"]) as image:
                    self.assertEqual(E.pixel_hash(image), entry["pixels_sha256"])
            staged = E.DEFAULT_OUTPUT
            if (staged / E.MANIFEST).is_file():
                # the extracted package of this machine: same sprites and pins (the Arena build string may differ after a hotfix)
                other = E.validate(staged)
                self.assertEqual([e["pixels_sha256"] for e in other["sprites"]], [e["pixels_sha256"] for e in manifest["sprites"]])

    def test_a_sprite_is_the_whole_texture_with_transparent_padding_where_unity_draws_a_tight_mesh(self):
        # the trophy and the skull are 166 x 185 / 182 x 225 meshes inside a 250 x 250 rect: the export keeps the rect
        for name in ("Arena_Scoreboard_Score_Icon_Scull", "Arena_Scoreboard_Status_Icon_Scull", "Arena_Scoreboard_Status_Toggle_Off", "Arena_Scoreboard_Timer_Glow_White"):
            image = self.sprites[name][0]
            self.assertEqual(image.size, tuple(E.SPRITES[name]["size"]), name)
            box = image.getchannel("A").getbbox()
            self.assertNotEqual(box, (0, 0) + image.size, name)
        self.assertEqual(self.sprites["Arena_Scoreboard_Status_Toggle_Off"][0].getchannel("A").getbbox(), (39, 39, 139, 139))     # inside the 104 px mesh at (37, 37)


if __name__ == "__main__":
    unittest.main()
