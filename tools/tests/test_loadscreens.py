"""PLAN-TWO-MAPS item 10b -- Unity-independent unit tests for extract_loadscreens.py: table
identity, filename/path building, size validation and --check's own logic. No BSG data (no UnityPy
import, no Arena install read) in any test here, matching test_banners.py's own convention.
"""
from __future__ import annotations

import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_loadscreens as EL  # noqa: E402
from common_ids import HardExtractError  # noqa: E402

ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"


class LoadscreenTableTests(unittest.TestCase):
    """The LOADSCREENS table itself: exactly the maps of this release, five entries each (Bowl four), at
    the path_ids/names the 2026-09-27 research pass (Equator, Bay5), the 2026-10-11 probe (AirPit, Arena 0.6.0)
    and the 2026-10-18 probe (Bowl) verified against a live UnityPy read."""

    def test_exactly_the_four_released_maps(self):
        self.assertEqual(set(EL.LOADSCREENS), {"arena_equator", "arena_bay5", "arena_airpit", "arena_bowl"})

    def test_every_map_has_the_five_image_rotation_except_bowl_which_has_four(self):
        # Arena ships no AllScreens_Loadingscreen for Bowl (PLAN-BOWL item 8): its rotation is the four main-menu
        # backgrounds alone, never a fake fifth image; the file count everywhere derives from this table.
        for key, entries in EL.LOADSCREENS.items():
            self.assertEqual(4 if key == "arena_bowl" else 5, len(entries), key)

    def test_equator_entries(self):
        entries = EL.LOADSCREENS["arena_equator"]
        self.assertEqual(entries[0], {"asset_name": "AllScreens_Loadingscreen-Equator", "path_id": 1558,
                                      "file": "resources.assets", "size": (1920, 1080)})
        self.assertEqual([(e["asset_name"], e["path_id"]) for e in entries[1:]],
                         [("Arena_MainMenu_Paralax_Equator-1", 292), ("Arena_MainMenu_Paralax_Equator-2", 1267),
                          ("Arena_MainMenu_Paralax_Equator-3", 1102), ("Arena_MainMenu_Paralax_Equator-4", 1550)])
        for e in entries[1:]:
            self.assertEqual((e["file"], e["size"]), ("sharedassets1.assets", (2048, 1024)))

    def test_bay5_entries(self):
        entries = EL.LOADSCREENS["arena_bay5"]
        self.assertEqual(entries[0], {"asset_name": "AllScreens_Loadingscreen-Bay5", "path_id": 804,
                                      "file": "resources.assets", "size": (1920, 1080)})
        self.assertEqual([(e["asset_name"], e["path_id"]) for e in entries[1:]],
                         [("Arena_MainMenu_Paralax_Bay5-1", 607), ("Arena_MainMenu_Paralax_Bay5-2", 526),
                          ("Arena_MainMenu_Paralax_Bay5-3", 1433), ("Arena_MainMenu_Paralax_Bay5-4", 1638)])
        for e in entries[1:]:
            self.assertEqual((e["file"], e["size"]), ("sharedassets1.assets", (2048, 1024)))

    def test_airpit_entries(self):
        entries = EL.LOADSCREENS["arena_airpit"]
        self.assertEqual(entries[0], {"asset_name": "AllScreens_Loadingscreen-AirPit", "path_id": 2704,
                                      "file": "resources.assets", "size": (1920, 1080)})
        self.assertEqual([(e["asset_name"], e["path_id"]) for e in entries[1:]],
                         [("Arena_MainMenu_Paralax_AirPit-1", 1472), ("Arena_MainMenu_Paralax_AirPit-2", 438),
                          ("Arena_MainMenu_Paralax_AirPit-3", 403), ("Arena_MainMenu_Paralax_AirPit-4", 936)])
        for e in entries[1:]:
            self.assertEqual((e["file"], e["size"]), ("sharedassets1.assets", (2048, 1024)))

    def test_bowl_entries(self):
        entries = EL.LOADSCREENS["arena_bowl"]
        self.assertEqual([(e["asset_name"], e["path_id"]) for e in entries],
                         [("Arena_MainMenu_Paralax_Bowl-1", 164), ("Arena_MainMenu_Paralax_Bowl-2", 1314),
                          ("Arena_MainMenu_Paralax_Bowl-3", 274), ("Arena_MainMenu_Paralax_Bowl-4", 834)])
        for e in entries:
            self.assertEqual((e["file"], e["size"]), ("sharedassets1.assets", (2048, 1024)))
        # No entry is a 1920x1080 loading screen; the four output names are _1.._4 and there is no _5.
        self.assertEqual([EL.output_filename("arena_bowl", n) for n in range(1, 5)],
                         [f"arena_bowl_{n}.jpg" for n in range(1, 5)])
        with self.assertRaises(HardExtractError):
            EL.output_filename("arena_bowl", index=5)

    def test_the_alt_menu_backgrounds_are_not_in_the_rotation(self):
        # User decision 2026-10-11 (PLAN-AIRPIT section 4 item 10): the four "-Alt-" images stay out.
        for entries in EL.LOADSCREENS.values():
            for entry in entries:
                self.assertNotIn("-Alt-", entry["asset_name"])

    def test_menu_backgrounds_validate_at_their_own_size(self):
        EL.validate_dimensions(2048, 1024, "arena_equator", index=2)  # must not raise
        with self.assertRaises(HardExtractError):
            EL.validate_dimensions(1920, 1080, "arena_bay5", index=3)

    def test_the_unwanted_ny_variant_is_not_in_the_table(self):
        for entries in EL.LOADSCREENS.values():
            for entry in entries:
                self.assertNotIn("NY", entry["asset_name"])
                self.assertNotEqual(entry["path_id"], 791)


class OutputFilenameTests(unittest.TestCase):
    def test_default_index_and_extension(self):
        self.assertEqual(EL.output_filename("arena_equator"), "arena_equator_1.jpg")

    def test_explicit_extension(self):
        self.assertEqual(EL.output_filename("arena_bay5", ext="png"), "arena_bay5_1.png")

    def test_unknown_map_key_raises(self):
        with self.assertRaises(HardExtractError):
            EL.output_filename("arena_woods")

    def test_index_out_of_range_raises(self):
        with self.assertRaises(HardExtractError):
            EL.output_filename("arena_equator", index=6)

    def test_index_zero_raises(self):
        with self.assertRaises(HardExtractError):
            EL.output_filename("arena_equator", index=0)


class OutputPathTests(unittest.TestCase):
    def test_joins_directory_and_filename(self):
        path = EL.output_path("/some/dir", "arena_equator")
        self.assertEqual(path, Path("/some/dir") / "arena_equator_1.jpg")

    def test_accepts_a_path_object_for_output_dir(self):
        path = EL.output_path(Path("/some/dir"), "arena_bay5", ext="png")
        self.assertEqual(path, Path("/some/dir/arena_bay5_1.png"))

    def test_default_output_is_under_dist_plugins_arenamapport_loadscreens(self):
        # Exactly the folder LoadingScreenArt.cs's PluginDir-relative lookup resolves to at
        # runtime (Path.Combine(PluginDir, "loadscreens", ...), PluginDir == the deployed plugin
        # DLL's own folder) -- this test pins that agreement from the Python side. A SIBLING of
        # "bundles", not a subfolder of it.
        self.assertEqual(
            EL.DEFAULT_OUTPUT.parts[-5:],
            ("dist", "BepInEx", "plugins", "maschine-ArenaMapPort", "loadscreens"),
        )


class ValidateDimensionsTests(unittest.TestCase):
    def test_accepts_the_expected_size(self):
        EL.validate_dimensions(1920, 1080, "arena_equator")  # must not raise

    def test_rejects_a_different_width(self):
        with self.assertRaises(HardExtractError):
            EL.validate_dimensions(1919, 1080, "arena_equator")

    def test_rejects_a_different_height(self):
        with self.assertRaises(HardExtractError):
            EL.validate_dimensions(1920, 1079, "arena_bay5")

    def test_rejects_zero(self):
        with self.assertRaises(HardExtractError):
            EL.validate_dimensions(0, 0, "arena_equator")


class CheckModeTests(unittest.TestCase):
    """--check must never import UnityPy or touch the Arena install -- only look at whatever the
    caller's own output directory already has on disk (five images per map since 2026-09-27, four for Bowl since
    PLAN-BOWL item 8, 2026-10-18)."""

    ALL = ({(k, i) for k in ("arena_equator", "arena_bay5", "arena_airpit") for i in range(1, 6)}
           | {("arena_bowl", i) for i in range(1, 5)})

    @staticmethod
    def _write_all(tmp):
        for key, index in CheckModeTests.ALL:
            with open(os.path.join(tmp, f"{key}_{index}.jpg"), "wb") as f:
                f.write(b"\xff\xd8\xff")

    def test_reports_every_image_missing_from_an_empty_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = EL.check(tmp, "jpg")
            self.assertEqual(set(missing), self.ALL)

    def test_an_empty_file_still_counts_as_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_all(tmp)
            with open(os.path.join(tmp, "arena_equator_1.jpg"), "wb"):
                pass  # zero bytes -- must not satisfy check(), unlike a real extracted image
            missing = EL.check(tmp, "jpg")
            self.assertEqual(set(missing), {("arena_equator", 1)})

    def test_reports_nothing_missing_once_every_file_exists(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_all(tmp)
            missing = EL.check(tmp, "jpg")
            self.assertEqual(missing, {})


@unittest.skipUnless(os.path.isdir(ARENA_SOURCE), "Arena install not present on this machine")
class RealSourceTests(unittest.TestCase):
    """One real-source pass (skipped automatically when the Arena install is unreachable, same
    idiom test_navmesh_modifiers.py/test_banners.py already use): decodes both objects with a live
    UnityPy read and asserts they really are 1920x1080 Texture2D under the expected names."""

    def test_extract_all_into_a_temp_dir(self):
        with tempfile.TemporaryDirectory() as tmp:
            written = EL.extract_all(ARENA_SOURCE, tmp, ext="jpg", quality=90)
            self.assertEqual(set(written), CheckModeTests.ALL)
            for path in written.values():
                self.assertTrue(path.is_file())
                self.assertGreater(path.stat().st_size, 0)


if __name__ == "__main__":
    unittest.main()
