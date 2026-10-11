"""PLAN-TWO-MAPS item 10 -- Unity-independent unit tests for extract_banners.py: name->file
mapping, size validation and path building. No BSG data (no UnityPy import, no Arena install
read) in any test here -- that matches this repo's rule that no game data lives in the test
suite, and its existing skip idiom (test_navmesh_modifiers.py's ARENA_SOURCE guard) for the one
optional real-source test at the bottom.
"""
from __future__ import annotations

import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_banners as EB  # noqa: E402
from common_ids import HardExtractError  # noqa: E402

ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"


class BannerTableTests(unittest.TestCase):
    """The BANNERS table itself: exactly the three maps in this release, at the path_ids/names the
    2026-09-25 research pass (Equator, Bay5) and the 2026-10-11 probe (AirPit) verified against a
    live UnityPy read."""

    def test_exactly_the_three_released_maps(self):
        self.assertEqual(set(EB.BANNERS), {"arena_equator", "arena_bay5", "arena_airpit"})

    def test_equator_asset_identity(self):
        self.assertEqual(EB.BANNERS["arena_equator"], {"asset_name": "Matchmaker_Banner_Equator", "path_id": 3030})

    def test_bay5_asset_identity(self):
        self.assertEqual(EB.BANNERS["arena_bay5"], {"asset_name": "Matchmaker_Banner_Bay5", "path_id": 1361})

    def test_airpit_asset_identity(self):
        # Arena 0.6.0.47752 resources.assets; the lookup is by the unique name, the path_id is a hint.
        self.assertEqual(EB.BANNERS["arena_airpit"], {"asset_name": "Matchmaker_Banner_AirPit", "path_id": 1584})

    def test_airpit_output_names(self):
        self.assertEqual(EB.output_filename("arena_airpit"), "arena_airpit.jpg")
        self.assertEqual(EB.output_filename("arena_airpit", "png"), "arena_airpit.png")


class OutputFilenameTests(unittest.TestCase):
    def test_default_extension_is_jpg(self):
        self.assertEqual(EB.output_filename("arena_equator"), "arena_equator.jpg")

    def test_explicit_extension(self):
        self.assertEqual(EB.output_filename("arena_bay5", "png"), "arena_bay5.png")

    def test_unknown_map_key_raises(self):
        with self.assertRaises(HardExtractError):
            EB.output_filename("arena_woods")  # a parked map


class OutputPathTests(unittest.TestCase):
    def test_joins_directory_and_filename(self):
        path = EB.output_path("/some/dir", "arena_equator", "jpg")
        self.assertEqual(path, Path("/some/dir") / "arena_equator.jpg")

    def test_accepts_a_path_object_for_output_dir(self):
        path = EB.output_path(Path("/some/dir"), "arena_bay5", "png")
        self.assertEqual(path, Path("/some/dir/arena_bay5.png"))

    def test_default_output_is_under_dist_mods_arenamapport_banners(self):
        # Exactly the folder ArenaMapPort.Server/ArenaMapBanners.cs's `bannersDir` resolves to at
        # runtime (Path.Combine(modDir, "banners"), modDir == the deployed mod DLL's own folder) --
        # this test pins that agreement from the Python side. A SIBLING of "db", not a subfolder
        # of it (see extract_banners.py's module docstring for why).
        self.assertEqual(
            EB.DEFAULT_OUTPUT.parts[-6:],
            ("dist", "SPT_Runtime", "user", "mods", "ArenaMapPort", "banners"),
        )


class ValidateDimensionsTests(unittest.TestCase):
    def test_accepts_the_expected_size(self):
        EB.validate_dimensions(769, 460, "arena_equator")  # must not raise

    def test_rejects_a_different_width(self):
        with self.assertRaises(HardExtractError):
            EB.validate_dimensions(765, 460, "arena_equator")

    def test_rejects_a_different_height(self):
        with self.assertRaises(HardExtractError):
            EB.validate_dimensions(769, 459, "arena_bay5")

    def test_rejects_zero(self):
        with self.assertRaises(HardExtractError):
            EB.validate_dimensions(0, 0, "arena_equator")


class CheckModeTests(unittest.TestCase):
    """--check must never import UnityPy or touch the Arena install -- only look at whatever the
    caller's own output directory already has on disk."""

    def test_reports_every_map_missing_from_an_empty_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = EB.check(tmp, "jpg")
            self.assertEqual(set(missing), {"arena_equator", "arena_bay5", "arena_airpit"})

    def test_an_empty_file_still_counts_as_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "arena_equator.jpg"), "wb"):
                pass  # zero bytes -- must not satisfy check(), unlike a real extracted image
            with open(os.path.join(tmp, "arena_bay5.jpg"), "wb") as f:
                f.write(b"\xff\xd8\xff")
            missing = EB.check(tmp, "jpg")
            self.assertIn("arena_equator", missing)
            self.assertNotIn("arena_bay5", missing)
            self.assertIn("arena_airpit", missing)

    def test_reports_nothing_missing_once_every_file_is_present(self):
        with tempfile.TemporaryDirectory() as tmp:
            for map_key in EB.BANNERS:
                with open(os.path.join(tmp, EB.output_filename(map_key, "jpg")), "wb") as f:
                    f.write(b"\xff\xd8\xff")  # a few bytes is enough; check() only looks at size > 0
            self.assertEqual(EB.check(tmp, "jpg"), {})

    def test_check_is_extension_sensitive(self):
        # A jpg on disk must not satisfy a --check run for a different requested extension --
        # otherwise a stale file from an earlier --ext run could mask a real gap.
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "arena_equator.jpg"), "wb") as f:
                f.write(b"x")
            missing = EB.check(tmp, "png")
            self.assertIn("arena_equator", missing)


@unittest.skipUnless(os.path.isdir(ARENA_SOURCE), "Arena source not reachable from this machine")
class RealSourceExtractionTests(unittest.TestCase):
    """One real-source, byte-producing pass per map against the live install, guarded the same way
    test_navmesh_modifiers.py guards its own real-source case. Writes into a throwaway temp
    directory, never into dist/ or work/, and never asserts on pixel content -- just that the
    named path_ids really are Texture2D objects of the expected name/size and decode to a
    non-trivial image, matching this repo's existing "skip gracefully when the Arena install isn't
    present" convention."""

    def test_every_banner_extracts_to_the_expected_size(self):
        with tempfile.TemporaryDirectory() as tmp:
            written = EB.extract_all(ARENA_SOURCE, tmp, ext="jpg", quality=90)
            self.assertEqual(set(written), {"arena_equator", "arena_bay5", "arena_airpit"})
            for map_key, path in written.items():
                self.assertTrue(path.is_file())
                self.assertGreater(path.stat().st_size, 1000, f"{map_key}: suspiciously small output file")

                from PIL import Image
                with Image.open(path) as img:
                    self.assertEqual(img.size, (EB.EXPECTED_WIDTH, EB.EXPECTED_HEIGHT))

    def test_check_passes_after_a_real_extraction(self):
        with tempfile.TemporaryDirectory() as tmp:
            EB.extract_all(ARENA_SOURCE, tmp, ext="jpg")
            self.assertEqual(EB.check(tmp, "jpg"), {})


if __name__ == "__main__":
    unittest.main()
