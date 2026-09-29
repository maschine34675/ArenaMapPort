"""Unit tests for extract_arena_ui.py: catalog integrity, strip-grid derivation/slicing on synthetic
RGBA images, name-resolution/disambiguation logic, output-path building, manifest --check logic. No BSG
data (no UnityPy import, no Arena install read) in any test here except the one real-source pass at the
bottom, guarded the same way test_arena_audio.py/test_banners.py guard theirs.
"""
from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_arena_ui as EU  # noqa: E402
from common_ids import HardExtractError  # noqa: E402

ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"


class CatalogIntegrityTests(unittest.TestCase):
    """The real, git-tracked arena_ui_catalog.json."""

    def setUp(self):
        self.catalog = EU.load_catalog()

    def test_schema_version(self):
        self.assertEqual(self.catalog["schema_version"], 1)

    def test_expected_groups_present(self):
        self.assertEqual(set(self.catalog["groups"]),
                         {"killfeed", "kda", "mode_logos", "kit_badges", "objective", "scoreboard", "mvp"})

    def test_every_item_key_unique_within_its_group(self):
        for group_name, group in self.catalog["groups"].items():
            keys = [item["key"] for item in group["items"]]
            self.assertEqual(len(keys), len(set(keys)), group_name)

    def test_every_item_has_required_common_fields(self):
        for group_name, group in self.catalog["groups"].items():
            for item in group["items"]:
                tag = f"{group_name}/{item['key']}"
                self.assertIn(item["kind"], EU.KNOWN_KINDS, tag)
                self.assertIn(item["root"], EU.KNOWN_ROOTS, tag)
                self.assertTrue(item["file"], tag)
                self.assertTrue(item["label"], tag)
                self.assertIn(item["confidence"], EU.KNOWN_CONFIDENCE, tag)
                self.assertTrue(item["arenamode_use"], tag)

    def test_sprite_items_have_name_and_path_id(self):
        for group_name, group in self.catalog["groups"].items():
            for item in group["items"]:
                if item["kind"] == "sprite":
                    self.assertTrue(item.get("name"), f"{group_name}/{item['key']}")
                    self.assertIsInstance(item.get("path_id"), int, f"{group_name}/{item['key']}")

    def test_strip_slot_items_reference_a_known_strip(self):
        strips = self.catalog["strips"]
        for group_name, group in self.catalog["groups"].items():
            for item in group["items"]:
                if item["kind"] == "strip_slot":
                    self.assertIn(item["strip"]["strip_key"], strips, f"{group_name}/{item['key']}")

    def test_kda_group_has_kill_death_assist(self):
        keys = {item["key"] for item in self.catalog["groups"]["kda"]["items"]}
        self.assertEqual(keys, {"kill", "death", "assist"})

    def test_mode_logos_covers_nine_names(self):
        names = {item["name"] for item in self.catalog["groups"]["mode_logos"]["items"]}
        self.assertEqual(names, {"teamfight", "checkpoint", "duel", "onemanarmy", "blastgang",
                                 "shootout", "lasthero", "lastHeroNewYear", "finalrun"})

    def test_mode_logos_items_all_carry_a_path_id_hint(self):
        # Several mode_logos sprites legitimately share one m_Name (one per size tier) -- path_id is the
        # only disambiguator, so every item here must carry one (never left to a first-match guess).
        for item in self.catalog["groups"]["mode_logos"]["items"]:
            self.assertIsInstance(item.get("path_id"), int, item["key"])

    def test_kit_badges_has_26_tacmap_plus_5_presets(self):
        items = self.catalog["groups"]["kit_badges"]["items"]
        tacmap = [i for i in items if i["name"].startswith("Popup_Item_Icon_TacMap_")]
        presets = [i for i in items if i["name"].startswith("PresetsALL_Classes_151123")]
        self.assertEqual(len(tacmap), 26)
        self.assertEqual(len(presets), 5)

    def test_mvp_has_36_frames(self):
        self.assertEqual(len(self.catalog["groups"]["mvp"]["items"]), 36)

    def test_killmetod_strip_has_15_slots_5_named(self):
        items = self.catalog["groups"]["killfeed"]["items"]
        slots = [i for i in items if i["kind"] == "strip_slot"]
        self.assertEqual(len(slots), 15)
        named = [i for i in slots if i["strip"]["has_named_sprite"]]
        self.assertEqual(len(named), 5)
        self.assertEqual({i["strip"]["index"] for i in named}, {10, 11, 12, 13, 14})

    def test_killmetod_strip_definition_matches_live_verified_grid(self):
        # Verified this session (2026-09-29) against the real Arena install: 480x30 texture, uniform
        # 32px cells, 15 cells total -- see extract_arena_ui.py's module docstring.
        strip = self.catalog["strips"]["killmetod_icon"]
        self.assertEqual(strip["texture_width"], 480)
        self.assertEqual(strip["texture_height"], 30)
        self.assertEqual(strip["expected_cell_count"], 15)

    def test_low_confidence_items_are_flagged_not_silently_dropped(self):
        # Skip nothing silently (task requirement): ambiguous glyphs stay in the catalog with a low
        # confidence rating rather than being omitted.
        low = [f"{g}/{i['key']}" for g, group in self.catalog["groups"].items() for i in group["items"]
               if i["confidence"] == "low"]
        self.assertTrue(low)
        self.assertIn("killfeed/method_icon_1", low)


class LoadCatalogValidationTests(unittest.TestCase):
    """load_catalog()'s own integrity checks, exercised against small fake catalog files."""

    def _write(self, tmp, payload):
        path = Path(tmp) / "catalog.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        return path

    def _base_payload(self):
        return {
            "schema_version": 1,
            "groups": {
                "g": {
                    "purpose": "p",
                    "items": [
                        {"key": "a", "kind": "sprite", "root": "arena", "file": "resources.assets",
                         "name": "Foo", "path_id": 1, "label": "l", "confidence": "high", "arenamode_use": "u"},
                    ],
                },
            },
            "strips": {},
        }

    def test_missing_file_raises(self):
        with self.assertRaises(HardExtractError):
            EU.load_catalog("/does/not/exist.json")

    def test_wrong_schema_version_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["schema_version"] = 2
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_empty_groups_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["groups"] = {}
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_group_missing_purpose_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            del payload["groups"]["g"]["purpose"]
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_group_empty_items_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["groups"]["g"]["items"] = []
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_item_missing_key_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            del payload["groups"]["g"]["items"][0]["key"]
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_duplicate_key_within_group_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["groups"]["g"]["items"].append(dict(payload["groups"]["g"]["items"][0]))
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_invalid_kind_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["groups"]["g"]["items"][0]["kind"] = "texture_atlas"
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_invalid_confidence_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["groups"]["g"]["items"][0]["confidence"] = "certain"
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_missing_arenamode_use_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            del payload["groups"]["g"]["items"][0]["arenamode_use"]
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_sprite_item_missing_name_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            del payload["groups"]["g"]["items"][0]["name"]
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_sprite_item_missing_path_id_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            del payload["groups"]["g"]["items"][0]["path_id"]
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_strip_slot_item_missing_strip_dict_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            item = payload["groups"]["g"]["items"][0]
            item["kind"] = "strip_slot"
            del item["name"]
            del item["path_id"]
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_strip_slot_item_references_unknown_strip_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            item = payload["groups"]["g"]["items"][0]
            item["kind"] = "strip_slot"
            del item["name"]
            del item["path_id"]
            item["strip"] = {"strip_key": "does_not_exist", "index": 0}
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def _valid_strip_def(self):
        return {"root": "arena", "file": "sharedassets1.assets", "texture_name": "Tex",
               "texture_path_id": 42, "texture_width": 64, "texture_height": 30, "expected_cell_count": 2}

    def test_valid_strip_slot_without_named_sprite_loads(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            item = payload["groups"]["g"]["items"][0]
            item["kind"] = "strip_slot"
            del item["name"]
            del item["path_id"]
            item["strip"] = {"strip_key": "s", "index": 0, "has_named_sprite": False}
            payload["strips"]["s"] = self._valid_strip_def()
            catalog = EU.load_catalog(self._write(tmp, payload))  # must not raise
            self.assertEqual(catalog["groups"]["g"]["items"][0]["kind"], "strip_slot")

    def test_strip_slot_with_named_sprite_but_no_name_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            item = payload["groups"]["g"]["items"][0]
            item["kind"] = "strip_slot"
            del item["name"]
            item["strip"] = {"strip_key": "s", "index": 0, "has_named_sprite": True}
            payload["strips"]["s"] = self._valid_strip_def()
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_strip_definition_missing_texture_width_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            strip_def = self._valid_strip_def()
            del strip_def["texture_width"]
            payload["strips"]["s"] = strip_def
            with self.assertRaises(HardExtractError):
                EU.load_catalog(self._write(tmp, payload))

    def test_duplicate_group_key_pair_is_not_possible_but_cross_group_same_key_is_fine(self):
        # (group, key) must be unique, but the same key string in two DIFFERENT groups is fine.
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["groups"]["h"] = {
                "purpose": "p2",
                "items": [dict(payload["groups"]["g"]["items"][0])],
            }
            catalog = EU.load_catalog(self._write(tmp, payload))  # must not raise
            self.assertEqual(catalog["groups"]["g"]["items"][0]["key"], catalog["groups"]["h"]["items"][0]["key"])


class OutputPathTests(unittest.TestCase):
    def test_output_path_uses_group_and_key(self):
        self.assertEqual(EU.output_path("out", "killfeed", "weapon_icon_0"),
                         Path("out") / "killfeed" / "weapon_icon_0.png")

    def test_preview_path(self):
        self.assertEqual(EU.preview_path("out", "kda"), Path("out") / "previews" / "kda.png")


class DeriveStripGridTests(unittest.TestCase):
    """Pure logic, no UnityPy, no PIL -- (index, x, y, width, height) tuples only."""

    def _slots(self, indices, cell_width=32, cell_height=30):
        return [(i, i * cell_width, 0, cell_width, cell_height) for i in indices]

    def test_uniform_grid_derives_correctly(self):
        cw, ch, count = EU.derive_strip_grid(self._slots([10, 11, 12, 13, 14]), 480, 30, 15)
        self.assertEqual((cw, ch, count), (32, 30, 15))

    def test_disagreeing_widths_raise(self):
        slots = self._slots([10, 11])
        slots[1] = (11, 11 * 32, 0, 40, 30)  # width drift
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid(slots, 480, 30, 15)

    def test_disagreeing_heights_raise(self):
        slots = self._slots([10, 11])
        slots[1] = (11, 11 * 32, 0, 32, 20)  # height drift
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid(slots, 480, 30, 15)

    def test_cell_height_not_matching_texture_height_raises(self):
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid(self._slots([10, 11]), 480, 40, 15)

    def test_nonzero_y_raises(self):
        slots = [(10, 320, 3, 32, 30)]
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid(slots, 480, 30, 15)

    def test_x_not_matching_index_times_cell_width_raises(self):
        slots = [(10, 999, 0, 32, 30)]
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid(slots, 480, 30, 15)

    def test_texture_width_not_divisible_raises(self):
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid(self._slots([1, 2]), 481, 30, 15)

    def test_cell_count_mismatch_raises(self):
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid(self._slots([10, 11, 12, 13, 14]), 480, 30, 16)

    def test_no_named_slots_raises(self):
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid([], 480, 30, 15)

    def test_non_integer_cell_size_raises(self):
        slots = [(0, 0.0, 0.0, 31.5, 30.0)]
        with self.assertRaises(HardExtractError):
            EU.derive_strip_grid(slots, 480, 30, 15)


class SliceUniformGridTests(unittest.TestCase):
    """Synthetic RGBA images -- exactly what the task calls for, no UnityPy involved."""

    def _synthetic_strip(self, colors, cell_width=4, cell_height=3):
        from PIL import Image

        img = Image.new("RGBA", (cell_width * len(colors), cell_height), (0, 0, 0, 0))
        for i, color in enumerate(colors):
            for x in range(cell_width):
                for y in range(cell_height):
                    img.putpixel((i * cell_width + x, y), color)
        return img

    def test_slices_three_distinct_cells(self):
        colors = [(255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255)]
        img = self._synthetic_strip(colors)
        crops = EU.slice_uniform_grid(img, 4, 3)
        self.assertEqual(len(crops), 3)
        for crop, color in zip(crops, colors):
            self.assertEqual(crop.size, (4, 3))
            self.assertEqual(crop.getpixel((0, 0)), color)
            self.assertEqual(crop.getpixel((3, 2)), color)

    def test_partial_trailing_pixels_are_dropped_not_padded(self):
        # image.width // cell_width rounds down -- a strip that isn't an exact multiple of cell_width
        # yields one fewer cell rather than a partial/garbage one.
        from PIL import Image

        img = Image.new("RGBA", (10, 3), (1, 2, 3, 255))  # 10 // 4 == 2 cells, 2px left over
        crops = EU.slice_uniform_grid(img, 4, 3)
        self.assertEqual(len(crops), 2)

    def test_matches_a_live_verified_case(self):
        # Mirrors this session's own live cross-check (extract_arena_ui.py's docstring): cropping a
        # uniform grid must reproduce exactly what a real named sprite at that same cell renders.
        colors = [(10, 20, 30, 255)] * 10 + [(40, 50, 60, 255)] * 5
        img = self._synthetic_strip(colors, cell_width=32, cell_height=30)
        crops = EU.slice_uniform_grid(img, 32, 30)
        self.assertEqual(len(crops), 15)
        self.assertEqual(crops[10].getpixel((0, 0)), (40, 50, 60, 255))


class ResolveNameTests(unittest.TestCase):
    """_resolve()'s disambiguation logic against a fake {(type, name): [obj, ...]} index -- no UnityPy."""

    def _obj(self, path_id):
        return SimpleNamespace(path_id=path_id)

    def test_single_candidate_resolves_by_name_alone(self):
        index = {("Sprite", "Foo"): [self._obj(1)]}
        obj = EU._resolve(index, "Sprite", "Foo", path_id_hint=999, tag="t")  # hint ignored/mismatched, still resolves
        self.assertEqual(obj.path_id, 1)

    def test_missing_name_raises(self):
        with self.assertRaises(HardExtractError):
            EU._resolve({}, "Sprite", "Missing", None, "t")

    def test_ambiguous_name_resolved_by_matching_path_id(self):
        index = {("Sprite", "teamfight"): [self._obj(100), self._obj(200), self._obj(300)]}
        obj = EU._resolve(index, "Sprite", "teamfight", path_id_hint=200, tag="t")
        self.assertEqual(obj.path_id, 200)

    def test_ambiguous_name_with_no_matching_path_id_raises(self):
        index = {("Sprite", "teamfight"): [self._obj(100), self._obj(200)]}
        with self.assertRaises(HardExtractError):
            EU._resolve(index, "Sprite", "teamfight", path_id_hint=999, tag="t")

    def test_ambiguous_name_with_no_hint_at_all_raises(self):
        index = {("Sprite", "teamfight"): [self._obj(100), self._obj(200)]}
        with self.assertRaises(HardExtractError):
            EU._resolve(index, "Sprite", "teamfight", path_id_hint=None, tag="t")


class NaturalKeyTests(unittest.TestCase):
    def test_embedded_numbers_sort_numerically(self):
        keys = ["mvp_frame_10", "mvp_frame_2", "mvp_frame_1", "killmetod_icon_14", "killmetod_icon_9"]
        self.assertEqual(sorted(keys, key=EU.natural_key),
                         ["killmetod_icon_9", "killmetod_icon_14", "mvp_frame_1", "mvp_frame_2", "mvp_frame_10"])


class CheckModeTests(unittest.TestCase):
    """--check must never import UnityPy or touch an install -- only look at manifest.json and the
    files it references under the given output directory."""

    def test_missing_manifest_is_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = EU.check(tmp)
            self.assertIn("manifest.json", missing)

    def test_unreadable_manifest_is_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "manifest.json").write_text("{not json", encoding="utf-8")
            missing = EU.check(tmp)
            self.assertIn("manifest.json", missing)

    def _write_manifest(self, tmp, with_files=True, empty_one=False):
        item_a = {"group": "kda", "key": "kill", "file": "kda/kill.png"}
        item_b = {"group": "kda", "key": "death", "file": "kda/death.png"}
        manifest = {
            "schema_version": EU.SCHEMA_VERSION, "generator": EU.GENERATOR,
            "generated_at": "2026-09-29T00:00:00+00:00", "source": {"root": "arena", "dir": "x"},
            "groups": {"kda": {"purpose": "p", "items": [item_a, item_b]}},
        }
        (Path(tmp) / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        if with_files:
            for item, size in ((item_a, 100), (item_b, 0 if empty_one else 100)):
                path = Path(tmp) / item["file"]
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x" * size)
        return manifest

    def test_passes_when_every_referenced_file_exists_and_is_non_empty(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_manifest(tmp, with_files=True)
            self.assertEqual(EU.check(tmp), {})

    def test_reports_missing_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_manifest(tmp, with_files=False)
            missing = EU.check(tmp)
            self.assertEqual(len(missing), 2)

    def test_an_empty_file_still_counts_as_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_manifest(tmp, with_files=True, empty_one=True)
            missing = EU.check(tmp)
            self.assertEqual(set(missing), {"kda/death.png"})

    def test_manifest_entry_missing_file_field_is_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            manifest = {
                "schema_version": 1, "generator": "g", "generated_at": "t", "source": {},
                "groups": {"kda": {"purpose": "p", "items": [{"group": "kda", "key": "kill"}]}},
            }
            (Path(tmp) / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            missing = EU.check(tmp)
            self.assertIn("kda:kill", missing)


@unittest.skipUnless(os.path.isdir(ARENA_SOURCE), "Arena install not present on this machine")
class RealSourceExtractionTests(unittest.TestCase):
    """One real-source pass, guarded like test_arena_audio.py's own: extracts the cheapest group (kda,
    3 sprites) into a throwaway temp dir and confirms the whole read -> PNG -> manifest -> check()
    pipeline works end to end, plus the strip group (killfeed) to exercise derive_strip_grid/
    slice_uniform_grid against the real Killmetod_icon texture."""

    def test_extract_kda_and_killfeed(self):
        with tempfile.TemporaryDirectory() as tmp:
            manifest = EU.extract_all(ARENA_SOURCE, tmp, only=["kda", "killfeed"], make_previews=False)
            self.assertEqual(set(manifest["groups"]), {"kda", "killfeed"})
            kda_items = manifest["groups"]["kda"]["items"]
            self.assertEqual(len(kda_items), 3)
            killfeed_items = manifest["groups"]["killfeed"]["items"]
            self.assertEqual(len(killfeed_items), 40)
            for item in kda_items + killfeed_items:
                path = Path(tmp) / item["file"]
                self.assertTrue(path.is_file())
                self.assertGreater(path.stat().st_size, 0)
                self.assertEqual(len(item["pixel_size"]), 2)
                self.assertGreater(item["pixel_size"][0], 0)
                self.assertGreater(item["pixel_size"][1], 0)
            for item in kda_items:
                # every kda item is a plain named Sprite -- must always carry a real pivot
                self.assertIsNotNone(item["sprite_pivot"])
                self.assertIn("x", item["sprite_pivot"])
            named_strip_items = [i for i in killfeed_items if i["kind"] == "strip_slot" and i["name"]]
            unnamed_strip_items = [i for i in killfeed_items if i["kind"] == "strip_slot" and not i["name"]]
            self.assertEqual(len(named_strip_items), 5)
            self.assertEqual(len(unnamed_strip_items), 10)
            for item in named_strip_items:
                self.assertIsNotNone(item["sprite_pivot"])
            for item in unnamed_strip_items:
                self.assertIsNone(item["sprite_pivot"])
            self.assertEqual(EU.check(tmp), {})


if __name__ == "__main__":
    unittest.main()
