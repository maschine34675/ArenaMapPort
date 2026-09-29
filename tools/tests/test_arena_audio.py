"""Unit tests for extract_arena_audio.py: catalog integrity, category classification, output path
building, ffmpeg/ffprobe command construction, RMS silence detection, manifest schema and --check
logic. No BSG data (no UnityPy import, no Arena/EFT install read) in any test here except the one
real-source pass at the bottom, guarded the same way test_banners.py/test_loadscreens.py guard theirs.
"""
from __future__ import annotations

import io
import json
import math
import os
import struct
import sys
import tempfile
import unittest
import wave
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_arena_audio as EA  # noqa: E402
from common_ids import HardExtractError  # noqa: E402

ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
EFT_SOURCE = "D:/SPT41/EscapeFromTarkov_Data"


def _make_wav(channels=1, rate=44100, seconds=0.05, amplitude=10000):
    """A small real WAV file (a 440 Hz tone), built with the stdlib wave module -- no BSG data."""
    n = int(rate * seconds)
    frames = bytearray()
    for i in range(n):
        value = int(amplitude * math.sin(2 * math.pi * 440 * i / rate))
        for _ in range(channels):
            frames += struct.pack("<h", value)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(frames))
    return buf.getvalue()


def _make_silence(channels=1, rate=44100, seconds=0.05):
    n = int(rate * seconds) * channels
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"\x00\x00" * n)
    return buf.getvalue()


class CatalogIntegrityTests(unittest.TestCase):
    """The real, git-tracked arena_audio_catalog.json."""

    def setUp(self):
        self.catalog = EA.load_catalog()

    def test_schema_version(self):
        self.assertEqual(self.catalog["schema_version"], 1)

    def test_every_category_name_is_unique(self):
        names = [c["category"] for c in self.catalog["categories"]]
        self.assertEqual(len(names), len(set(names)))

    def test_every_category_has_a_non_empty_reason(self):
        for c in self.catalog["categories"]:
            self.assertIsInstance(c["reason"], str)
            self.assertTrue(c["reason"].strip())

    def test_blast_gang_excluded_categories_carry_the_right_family_and_reason(self):
        excluded = [c for c in self.catalog["categories"] if not c["included"]]
        blast_gang = [c for c in excluded if c["family"] == "blast_gang_excluded"]
        self.assertTrue(blast_gang)
        for c in blast_gang:
            self.assertIn("Blast Gang", c["reason"])

    def test_tm_sweep_team_is_excluded_as_arena_specific_not_blast_gang(self):
        # Tag evidence (2026-09-29): eng_commentator_tm_sweep_team's real tag is PurifiedTeamAppearance,
        # an Arena-specific 'Purified' team spawn event, not a team sweep/wipe -- excluded for that
        # reason, not because it is Blast-Gang-gated.
        by_name = {c["category"]: c for c in self.catalog["categories"]}
        entry = by_name["eng_commentator_tm_sweep_team"]
        self.assertFalse(entry["included"])
        self.assertEqual(entry["family"], "arena_specific_excluded")
        self.assertNotEqual(entry["family"], "blast_gang_excluded")
        self.assertIn("Purified", entry["reason"])
        self.assertEqual(entry["arena_tag"], ["PurifiedTeamAppearance"])

    def test_every_excluded_category_is_blast_gang_or_arena_specific(self):
        for c in self.catalog["categories"]:
            if not c["included"]:
                self.assertIn(c["family"], ("blast_gang_excluded", "arena_specific_excluded"), c["category"])

    def test_included_category_count_matches_researched_total(self):
        included = [c for c in self.catalog["categories"] if c["included"]]
        total = len(self.catalog["categories"])
        self.assertEqual(total, self.catalog["source"]["expected_category_count"])
        self.assertEqual(len(included) + len(excluded_of(self.catalog)), total)

    def test_known_included_categories_present(self):
        # A handful of the task brief's own named examples, spot-checked against the real catalog.
        included = {c["category"] for c in self.catalog["categories"] if c["included"]}
        for name in (
            "eng_commentator_start", "eng_commentator_count_down", "eng_commentator_first_blood",
            "eng_commentator_1_min_left", "eng_commentator_eng_final_battle",
            "eng_commentator_eng_kill_all_win_red", "eng_commentator_bg_kill_all_win_blue",
            "eng_round_draw", "eng_clash_draw", "eng_commentator_capture_point_live",
            "eng_commentator_all_points_red_team_captured",
            # Tag evidence (2026-09-29): OvertimeStart is a dedicated, non-Blast-Gang tag -- the bg_
            # prefix on this one category is naming only. See arena_audio_catalog.json's reason.
            "eng_commentator_bg_overtime_start",
        ):
            self.assertIn(name, included)

    def test_known_excluded_categories_present(self):
        excluded = {c["category"] for c in self.catalog["categories"] if not c["included"]}
        for name in (
            "eng_commentator_eng_bg_attack_team_win_blue", "eng_commentator_bg_special_item_planted",
            "eng_commentator_bg_roles_changed", "eng_commentator_bg_toxin_start",
            # bg_-prefixed with no confirmed generic-mode equivalent (review finding, 2026-09-29):
            # unlike eng_kill_all_win_red/bg_kill_all_win_blue's literal same-family color pair, this
            # one has no eng_ sibling at all -- see arena_audio_catalog.json's reason and
            # docs/progress/arena-audio.md.
            "eng_commentator_bg_match_end_draw",
            # Arena-specific, not Blast-Gang (tag evidence, 2026-09-29): see the dedicated test above.
            "eng_commentator_tm_sweep_team",
        ):
            self.assertIn(name, excluded)

    def test_every_category_has_a_non_empty_arena_tag_list(self):
        for c in self.catalog["categories"]:
            self.assertIsInstance(c.get("arena_tag"), list, c["category"])
            self.assertTrue(c["arena_tag"], c["category"])
            for tag in c["arena_tag"]:
                self.assertIn(tag, self.catalog["known_tags"], f"{c['category']}: unknown tag {tag!r}")

    def test_known_tags_have_mode_and_purpose(self):
        known_tags = self.catalog["known_tags"]
        self.assertTrue(known_tags)
        for tag, meta in known_tags.items():
            self.assertIn(meta.get("mode"), EA.KNOWN_TAG_MODES, tag)
            self.assertTrue(meta.get("purpose"), tag)

    def test_blast_gang_tags_correspond_exactly_to_blast_gang_excluded_categories(self):
        # Bidirectional: a category referenced only by blast_gang-mode tag(s) must be excluded with
        # family blast_gang_excluded, and every blast_gang_excluded category must be referenced only by
        # blast_gang-mode tag(s) -- "Blast-Gang tags <=> excluded".
        known_tags = self.catalog["known_tags"]
        for c in self.catalog["categories"]:
            modes = {known_tags[t]["mode"] for t in c["arena_tag"]}
            if modes == {"blast_gang"}:
                self.assertFalse(c["included"], c["category"])
                self.assertEqual(c["family"], "blast_gang_excluded", c["category"])
            if c["family"] == "blast_gang_excluded":
                self.assertFalse(c["included"], c["category"])
                self.assertEqual(modes, {"blast_gang"}, c["category"])

    def test_generic_tag_categories_are_included(self):
        known_tags = self.catalog["known_tags"]
        for c in self.catalog["categories"]:
            modes = {known_tags[t]["mode"] for t in c["arena_tag"]}
            if "generic" in modes:
                self.assertTrue(c["included"], c["category"])


def excluded_of(catalog):
    return [c for c in catalog["categories"] if not c["included"]]


class LoadCatalogValidationTests(unittest.TestCase):
    """load_catalog()'s own integrity checks, exercised against small fake catalog files. Each fixture
    below is otherwise-valid (a 'generic'-mode TagA on the included example, a 'blast_gang'-mode TagB on
    the excluded example) so the one field under test is the only thing that can trip a _require."""

    BASE_KNOWN_TAGS = {
        "TagA": {"mode": "generic", "purpose": "p"},
        "TagB": {"mode": "blast_gang", "purpose": "p"},
    }

    def _write(self, tmp, payload):
        path = Path(tmp) / "catalog.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        return path

    def _base_payload(self):
        return {
            "schema_version": 1,
            "known_tags": dict(self.BASE_KNOWN_TAGS),
            "categories": [
                {"category": "a", "included": True, "reason": "x", "family": "f", "arena_tag": ["TagA"]},
            ],
        }

    def test_missing_file_raises(self):
        with self.assertRaises(HardExtractError):
            EA.load_catalog("/does/not/exist.json")

    def test_wrong_schema_version_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["schema_version"] = 2
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_duplicate_category_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"].append(
                {"category": "a", "included": False, "reason": "y", "family": "blast_gang_excluded", "arena_tag": ["TagB"]})
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_missing_reason_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"][0]["reason"] = ""
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_non_bool_included_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"][0]["included"] = "yes"
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_missing_family_raises(self):
        # Review finding (2026-09-29): extract_commentator() unconditionally reads
        # reasons[category]["family"] for every included category, so a catalog missing 'family' on an
        # included entry must fail fast here, not with a raw KeyError deep into a real extraction run.
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            del payload["categories"][0]["family"]
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_empty_family_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"][0]["family"] = ""
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_missing_known_tags_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            del payload["known_tags"]
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_known_tag_missing_mode_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["known_tags"]["TagA"] = {"purpose": "p"}
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_known_tag_invalid_mode_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["known_tags"]["TagA"] = {"mode": "not_a_real_mode", "purpose": "p"}
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_known_tag_missing_purpose_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["known_tags"]["TagA"] = {"mode": "generic", "purpose": ""}
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_missing_arena_tag_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            del payload["categories"][0]["arena_tag"]
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_empty_arena_tag_list_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"][0]["arena_tag"] = []
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_arena_tag_naming_unknown_tag_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"][0]["arena_tag"] = ["TagMystery"]
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_blast_gang_only_category_must_be_excluded(self):
        # A category referenced only by a blast_gang-mode tag but marked included=True must be rejected.
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"][0]["arena_tag"] = ["TagB"]  # blast_gang mode
            # included stays True (the default from _base_payload) -- must raise
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_blast_gang_only_category_wrong_family_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"][0]["arena_tag"] = ["TagB"]
            payload["categories"][0]["included"] = False
            payload["categories"][0]["family"] = "not_blast_gang_excluded"
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_arena_specific_only_category_must_be_excluded(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["known_tags"]["TagC"] = {"mode": "arena_specific", "purpose": "p"}
            payload["categories"][0]["arena_tag"] = ["TagC"]
            # included stays True -- must raise
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_generic_tag_category_must_be_included(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"][0]["included"] = False  # TagA is generic -- must raise
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_blast_gang_plus_arena_specific_with_no_generic_tag_must_be_excluded(self):
        # Review finding (2026-09-29): a category tagged by both a blast_gang-mode AND an arena_specific-
        # mode tag, with no generic-mode tag at all, used to fall through the old
        # `if == {"blast_gang"} / elif == {"arena_specific"} / else: require included=True` chain into the
        # "else" arm and get wrongly forced to included=True, even though no generic tag justifies that.
        # No real category hits this combination today (none of the 30 known_tags mix those two modes
        # without a generic tag), so this is a fake-data regression test for the documented rule itself.
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["known_tags"]["TagC"] = {"mode": "arena_specific", "purpose": "p"}
            payload["categories"][0]["arena_tag"] = ["TagB", "TagC"]  # blast_gang + arena_specific, no generic
            payload["categories"][0]["included"] = True  # must raise: neither mode is generic
            path = self._write(tmp, payload)
            with self.assertRaises(HardExtractError):
                EA.load_catalog(path)

    def test_blast_gang_plus_arena_specific_with_no_generic_tag_excluded_loads(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["known_tags"]["TagC"] = {"mode": "arena_specific", "purpose": "p"}
            payload["categories"][0]["arena_tag"] = ["TagB", "TagC"]
            payload["categories"][0]["included"] = False
            path = self._write(tmp, payload)
            catalog = EA.load_catalog(path)  # must not raise
            self.assertFalse(catalog["categories"][0]["included"])

    def test_valid_minimal_catalog_loads(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = self._base_payload()
            payload["categories"].append(
                {"category": "b", "included": False, "reason": "y", "family": "blast_gang_excluded", "arena_tag": ["TagB"]})
            path = self._write(tmp, payload)
            catalog = EA.load_catalog(path)
            self.assertEqual(len(catalog["categories"]), 2)


class GroupSourcesTests(unittest.TestCase):
    """Every non-commentator group and the commentator source both declare a real source."""

    def test_every_group_has_a_root_file_and_clips(self):
        for name, spec in EA.GROUP_SOURCES.items():
            self.assertIn(spec["root"], ("arena", "eft"), name)
            self.assertTrue(spec["file"], name)
            self.assertTrue(spec["clips"], name)

    def test_commentator_source_defined(self):
        self.assertEqual(EA.COMMENTATOR_SOURCE["root"], "arena")
        self.assertTrue(EA.COMMENTATOR_SOURCE["file"])

    def test_all_groups_lists_every_group_once(self):
        self.assertEqual(set(EA.ALL_GROUPS), set(EA.GROUP_SOURCES) | {EA.COMMENTATOR_GROUP, EA.OTHER_GROUP})
        self.assertEqual(len(EA.ALL_GROUPS), len(set(EA.ALL_GROUPS)))


class CategoryOfTests(unittest.TestCase):
    def test_strips_a_two_digit_suffix(self):
        self.assertEqual(EA.category_of("eng_commentator_start_01"), "eng_commentator_start")

    def test_strips_a_single_digit_suffix(self):
        self.assertEqual(EA.category_of("eng_clash_draw_9"), "eng_clash_draw")

    def test_strips_a_three_digit_suffix(self):
        self.assertEqual(EA.category_of("eng_commentator_foo_123"), "eng_commentator_foo")

    def test_bare_name_with_no_suffix_is_its_own_category(self):
        self.assertEqual(EA.category_of("eng_commentator_count_down"), "eng_commentator_count_down")

    def test_category_with_a_leading_digit_word_is_not_stripped(self):
        # "eng_commentator_1_min_left" does not END in digits, so it must survive whole.
        self.assertEqual(EA.category_of("eng_commentator_1_min_left_24"), "eng_commentator_1_min_left")


class ClassifyCategoriesTests(unittest.TestCase):
    """Fake catalog, never the real one -- isolates the classification logic itself."""

    def setUp(self):
        self.catalog = {"schema_version": 1, "categories": [
            {"category": "cat_a", "included": True, "family": "f", "reason": "r"},
            {"category": "cat_b", "included": True, "family": "f", "reason": "r"},
            {"category": "cat_c", "included": False, "family": "f", "reason": "r"},
        ]}

    def test_included_and_excluded_split(self):
        included, excluded = EA.classify_categories(["cat_a", "cat_b", "cat_c"], self.catalog)
        self.assertEqual(included, {"cat_a", "cat_b"})
        self.assertEqual(excluded, {"cat_c"})

    def test_unknown_category_raises(self):
        with self.assertRaises(HardExtractError):
            EA.classify_categories(["cat_a", "cat_b", "cat_c", "cat_mystery"], self.catalog)

    def test_missing_included_category_raises(self):
        with self.assertRaises(HardExtractError):
            EA.classify_categories(["cat_a", "cat_c"], self.catalog)  # cat_b missing

    def test_excluded_category_absent_from_the_bank_is_not_an_error(self):
        included, excluded = EA.classify_categories(["cat_a", "cat_b"], self.catalog)  # cat_c absent
        self.assertEqual(included, {"cat_a", "cat_b"})
        self.assertEqual(excluded, set())


class WalkPptrCategoriesTests(unittest.TestCase):
    """_walk_pptr_categories() is a pure dict/list walk -- no UnityPy, no bank read."""

    def test_finds_a_pptr_nested_inside_lists_and_dicts(self):
        clip_categories = {101: "cat_a", 202: "cat_b"}
        tree = {"Clips": [{"m_PathID": 101, "m_FileID": 0}, {"m_PathID": 202, "m_FileID": 0}],
                "Importance": 1, "Nested": {"m_PathID": 101}}
        found = list(EA._walk_pptr_categories(tree, clip_categories))
        self.assertEqual(sorted(found), ["cat_a", "cat_a", "cat_b"])

    def test_pptr_to_unknown_path_id_is_ignored(self):
        clip_categories = {101: "cat_a"}
        tree = {"Clips": [{"m_PathID": 999}]}
        self.assertEqual(list(EA._walk_pptr_categories(tree, clip_categories)), [])


class InvertTagCategoryMapTests(unittest.TestCase):
    def test_single_tag_per_category(self):
        result = EA.invert_tag_category_map({"TagA": {"cat_x"}, "TagB": {"cat_y"}})
        self.assertEqual(result, {"cat_x": ["TagA"], "cat_y": ["TagB"]})

    def test_category_referenced_by_two_tags_lists_both_sorted(self):
        result = EA.invert_tag_category_map({"MatchWon": {"eng_clash_draw"}, "MatchEnd": {"eng_clash_draw"}})
        self.assertEqual(result, {"eng_clash_draw": ["MatchEnd", "MatchWon"]})


class ValidateTagBankTests(unittest.TestCase):
    """validate_tag_bank()'s three failure modes, exercised entirely with fake data -- never a live
    bank read. Matches the task brief's three named scenarios: disagreeing tag, unknown tag, unknown
    category in a tag."""

    def setUp(self):
        self.catalog = {
            "known_tags": {
                "TagA": {"mode": "generic", "purpose": "p"},
                "TagB": {"mode": "generic", "purpose": "p"},
            },
            "categories": [
                {"category": "cat_a", "included": True, "family": "f", "reason": "r", "arena_tag": ["TagA"]},
                {"category": "cat_b", "included": True, "family": "f", "reason": "r", "arena_tag": ["TagB"]},
            ],
        }

    def test_matching_bank_passes(self):
        EA.validate_tag_bank({"TagA": {"cat_a"}, "TagB": {"cat_b"}}, self.catalog)  # must not raise

    def test_multi_tag_category_matches_when_both_tags_agree(self):
        catalog = {
            "known_tags": {"TagA": {"mode": "generic", "purpose": "p"}, "TagB": {"mode": "generic", "purpose": "p"}},
            "categories": [
                {"category": "cat_shared", "included": True, "family": "f", "reason": "r",
                 "arena_tag": ["TagA", "TagB"]},
            ],
        }
        EA.validate_tag_bank({"TagA": {"cat_shared"}, "TagB": {"cat_shared"}}, catalog)  # must not raise

    def test_unknown_tag_raises(self):
        # The bank has a tag the catalog's known_tags has never heard of.
        with self.assertRaises(HardExtractError) as ctx:
            EA.validate_tag_bank({"TagA": {"cat_a"}, "TagMystery": {"cat_b"}}, self.catalog)
        self.assertIn("TagMystery", str(ctx.exception))

    def test_unknown_category_in_a_tag_raises(self):
        # A known tag's Clips resolve to a category the catalog's categories list does not know at all.
        with self.assertRaises(HardExtractError) as ctx:
            EA.validate_tag_bank({"TagA": {"cat_mystery"}}, self.catalog)
        self.assertIn("cat_mystery", str(ctx.exception))

    def test_disagreeing_tag_category_referenced_by_wrong_tag_raises(self):
        # The bank shows cat_a under TagB, but the catalog says cat_a's arena_tag is TagA.
        with self.assertRaises(HardExtractError) as ctx:
            EA.validate_tag_bank({"TagA": {"cat_b"}, "TagB": {"cat_a"}}, self.catalog)
        self.assertIn("cat_a", str(ctx.exception))

    def test_disagreeing_tag_category_not_referenced_by_any_tag_raises(self):
        # The catalog claims cat_a has arena_tag TagA, but the live bank's TagA no longer references it.
        with self.assertRaises(HardExtractError) as ctx:
            EA.validate_tag_bank({"TagA": set(), "TagB": {"cat_b"}}, self.catalog)
        self.assertIn("cat_a", str(ctx.exception))


class OutputPathTests(unittest.TestCase):
    def test_non_commentator_group_path(self):
        path = EA.clip_output_path("out", "stingers", "arena_start")
        self.assertEqual(path, Path("out") / "stingers" / "arena_start.ogg")

    def test_commentator_path_includes_category(self):
        path = EA.clip_output_path("out", "commentator", "eng_commentator_start_01", "eng_commentator_start")
        self.assertEqual(path, Path("out") / "commentator" / "eng_commentator_start" / "eng_commentator_start_01.ogg")

    def test_commentator_without_category_raises(self):
        with self.assertRaises(HardExtractError):
            EA.clip_output_path("out", "commentator", "eng_commentator_start_01")

    def test_unknown_group_raises(self):
        with self.assertRaises(HardExtractError):
            EA.clip_output_path("out", "not_a_group", "clip")

    def test_other_commentator_path(self):
        path = EA.other_commentator_output_path("out", "eng_commentator_bg_toxin_start", "eng_commentator_bg_toxin_start_01")
        self.assertEqual(path, Path("out") / "other" / "commentator" / "eng_commentator_bg_toxin_start"
                         / "eng_commentator_bg_toxin_start_01.ogg")

    def test_other_arena_path(self):
        path = EA.other_arena_output_path("out", "resources", "arena_button_click")
        self.assertEqual(path, Path("out") / "other" / "arena" / "resources" / "arena_button_click.ogg")


class GuessKindTests(unittest.TestCase):
    """guess_kind() -- name-only heuristic, always a guess for the 'other/arena' part."""

    def test_ui_tokens(self):
        for name in ("arena_button_click", "arena_button_over", "arena_tab_click", "arena_menu_checkbox",
                    "arena_menu_dropdown", "arena_menu_dropdown_select", "arena_menu_escape",
                    "arena_preset_click", "arena_presets_screen_show", "arena_inspect_click",
                    "arena_magnifier_click", "arena_popup_generic", "arena_wtask_complete"):
            self.assertEqual(EA.guess_kind(name), "ui", name)

    def test_music_tokens(self):
        for name in ("arena_track_02", "arena_track_03", "arena_wait_match_background_music"):
            self.assertEqual(EA.guess_kind(name), "music", name)

    def test_voice_tokens(self):
        for name in ("usec_03_ending_03", "bear_02_sergei_eng_ending_03", "terminal_cutscene_ending_03"):
            self.assertEqual(EA.guess_kind(name), "voice", name)

    def test_defaults_to_sfx(self):
        for name in ("blastgang_flare_explosion", "arena_bomb_exploded_signal_01",
                    "arena_railway_station_notification", "blast_gang_notebook_diffuse_loop"):
            self.assertEqual(EA.guess_kind(name), "sfx", name)

    def test_whole_token_match_not_substring(self):
        # "bolt_click" must not be mistaken for a UI "click" sound just because "click" is a substring --
        # it has no whole token in _OTHER_KIND_UI_TOKENS (only "button"/"tab"/... do, never "click" alone).
        self.assertEqual(EA.guess_kind("bolt_click"), "sfx")

    def test_case_insensitive(self):
        self.assertEqual(EA.guess_kind("ARENA_BUTTON_CLICK"), "ui")


class ArenaClaimedNamesTests(unittest.TestCase):
    def test_includes_commentator_names_and_arena_rooted_group_clips(self):
        claimed = EA.arena_claimed_names({"eng_commentator_start_01", "eng_commentator_bg_toxin_start_01"})
        # arena-rooted GROUP_SOURCES clips (crowd/spectator_glitch/capture/round_signal/result_music):
        self.assertIn("arena_fans_ambient_postmatch", claimed)
        self.assertIn("asrena_start_signal_dry", claimed)
        self.assertIn("result_music_start", claimed)
        # commentator bank names passed in:
        self.assertIn("eng_commentator_start_01", claimed)
        self.assertIn("eng_commentator_bg_toxin_start_01", claimed)

    def test_excludes_eft_rooted_group_clips(self):
        # stingers is root "eft" -- arena_claimed_names() only adds root=="arena" GROUP_SOURCES clips;
        # eft_audio_clip_names()'s own EFT-shared filter is what actually keeps these out of "other/arena".
        claimed = EA.arena_claimed_names(set())
        self.assertNotIn("arena_start", claimed)
        self.assertNotIn("arena_win", claimed)
        self.assertNotIn("arena_lose", claimed)


class PlanOtherArenaSelectionTests(unittest.TestCase):
    """Pure selection/dedupe/EFT-name-filter logic for the 'other/arena' part -- fake data only."""

    def _meta(self, path_id, name):
        return {"path_id": path_id, "name": name, "channels": 2, "frequency": 44100, "length_s": 1.0}

    def test_selects_unclaimed_non_eft_clips(self):
        source_clip_lists = [("resources", [self._meta(1, "arena_button_click"), self._meta(2, "footstep_grass")])]
        selected, duplicates = EA.plan_other_arena_selection(
            source_clip_lists, eft_names={"footstep_grass"}, claimed_names=set())
        self.assertEqual([n for _, m in selected for n in [m["name"]]], ["arena_button_click"])
        self.assertEqual(duplicates, [])

    def test_eft_shared_name_is_dropped_not_selected(self):
        source_clip_lists = [("resources", [self._meta(1, "footstep_grass")])]
        selected, duplicates = EA.plan_other_arena_selection(
            source_clip_lists, eft_names={"footstep_grass"}, claimed_names=set())
        self.assertEqual(selected, [])
        self.assertEqual(duplicates, [])  # dropped outright, never counted as a duplicate

    def test_already_claimed_name_is_dropped_not_selected(self):
        source_clip_lists = [("resources", [self._meta(1, "arena_fans_ambient_postmatch")])]
        selected, duplicates = EA.plan_other_arena_selection(
            source_clip_lists, eft_names=set(), claimed_names={"arena_fans_ambient_postmatch"})
        self.assertEqual(selected, [])
        self.assertEqual(duplicates, [])

    def test_duplicate_name_across_sources_keeps_first_records_rest(self):
        source_clip_lists = [
            ("resources", [self._meta(1, "big_score")]),
            ("arenaaudio", [self._meta(2, "big_score")]),
        ]
        selected, duplicates = EA.plan_other_arena_selection(source_clip_lists, eft_names=set(), claimed_names=set())
        self.assertEqual(len(selected), 1)
        self.assertEqual(selected[0], ("resources", self._meta(1, "big_score")))
        self.assertEqual(duplicates, [{"name": "big_score", "kept_source": "resources",
                                       "skipped_source": "arenaaudio", "skipped_path_id": 2}])

    def test_duplicate_name_within_the_same_source_is_also_recorded(self):
        source_clip_lists = [("resources", [self._meta(1, "dup"), self._meta(2, "dup")])]
        selected, duplicates = EA.plan_other_arena_selection(source_clip_lists, eft_names=set(), claimed_names=set())
        self.assertEqual(len(selected), 1)
        self.assertEqual(len(duplicates), 1)
        self.assertEqual(duplicates[0]["skipped_path_id"], 2)

    def test_source_order_determines_which_copy_is_kept(self):
        source_clip_lists = [
            ("blastgang", [self._meta(1, "shared")]),
            ("resources", [self._meta(2, "shared")]),
        ]
        selected, duplicates = EA.plan_other_arena_selection(source_clip_lists, eft_names=set(), claimed_names=set())
        self.assertEqual(selected[0][0], "blastgang")  # first in list order wins, regardless of key name
        self.assertEqual(duplicates[0]["kept_source"], "blastgang")


class ListAudioclipMetasTests(unittest.TestCase):
    """_list_audioclip_metas() against a small fake UnityPy-shaped environment -- no real install read."""

    class _FakeObj:
        def __init__(self, path_id, type_name, tree):
            self.path_id = path_id
            self.type = type("T", (), {"name": type_name})()
            self._tree = tree

        def read_typetree(self):
            return self._tree

    class _FakeEnv:
        def __init__(self, objects):
            self.objects = objects

    def test_lists_only_audioclips_sorted_by_path_id(self):
        env = self._FakeEnv([
            self._FakeObj(20, "AudioClip", {"m_Name": "b", "m_Channels": 1, "m_Frequency": 44100, "m_Length": 2.0}),
            self._FakeObj(10, "AudioClip", {"m_Name": "a", "m_Channels": 2, "m_Frequency": 44100, "m_Length": 1.0}),
            self._FakeObj(15, "MonoBehaviour", {"m_Name": "not_a_clip"}),
        ])
        metas = EA._list_audioclip_metas(env)
        self.assertEqual([m["path_id"] for m in metas], [10, 20])
        self.assertEqual(metas[0]["name"], "a")
        self.assertEqual(metas[0]["channels"], 2)


class FfmpegCommandTests(unittest.TestCase):
    def test_ffmpeg_command_shape(self):
        cmd = EA.build_ffmpeg_command("out/clip.ogg", quality=5, sample_rate=44100)
        self.assertEqual(cmd[0], "ffmpeg")
        self.assertIn("pipe:0", cmd)
        self.assertIn("libvorbis", cmd)
        self.assertIn("-q:a", cmd)
        self.assertEqual(cmd[cmd.index("-q:a") + 1], "5")
        self.assertIn("-ar", cmd)
        self.assertEqual(cmd[cmd.index("-ar") + 1], "44100")
        self.assertEqual(cmd[-1], "out/clip.ogg")
        self.assertNotIn("-ac", cmd)  # channel count must never be forced

    def test_ffprobe_command_shape(self):
        cmd = EA.build_ffprobe_command("out/clip.ogg")
        self.assertEqual(cmd[0], "ffprobe")
        self.assertIn("-show_entries", cmd)
        self.assertIn("out/clip.ogg", cmd)


class WavRmsTests(unittest.TestCase):
    def test_a_tone_has_high_rms(self):
        rms = EA.wav_rms(_make_wav(amplitude=10000))
        self.assertGreater(rms, EA.RMS_SILENCE_THRESHOLD)

    def test_silence_has_near_zero_rms(self):
        rms = EA.wav_rms(_make_silence())
        self.assertLess(rms, EA.RMS_SILENCE_THRESHOLD)

    def test_stereo_tone(self):
        rms = EA.wav_rms(_make_wav(channels=2, amplitude=8000))
        self.assertGreater(rms, EA.RMS_SILENCE_THRESHOLD)


class ManifestSchemaTests(unittest.TestCase):
    def test_build_manifest_shape(self):
        groups = {"stingers": {"clips": [{
            "name": "arena_start", "file": "stingers/arena_start.ogg", "duration_s": 10.0,
            "channels": 2, "sample_rate": 44100, "source_duration_s": 10.0, "source_rms": 100.0,
        }]}}
        commentator = {"eng_commentator_start": {"family": "general_time", "reason": "x", "clips": []}}
        excluded = {"eng_commentator_bg_toxin_start": {"reason": "y"}}
        manifest = EA.build_manifest(groups, commentator, excluded, [], generated_at="2026-09-29T00:00:00+00:00")

        self.assertEqual(manifest["schema_version"], EA.SCHEMA_VERSION)
        self.assertEqual(manifest["generator"], EA.GENERATOR)
        self.assertEqual(manifest["generated_at"], "2026-09-29T00:00:00+00:00")
        self.assertIn("stingers", manifest["groups"])
        self.assertEqual(manifest["groups"]["commentator"]["categories"], commentator)
        self.assertEqual(manifest["groups"]["commentator"]["excluded_categories"], excluded)
        self.assertIn("suggested_arenamode_cues", manifest)
        self.assertIs(manifest["suggested_arenamode_cues"], EA.SUGGESTED_ARENAMODE_CUES)

    def test_manifest_round_trips_through_json(self):
        manifest = EA.build_manifest({}, {}, {}, [])
        json.loads(json.dumps(manifest))  # must not raise

    def test_other_group_present_with_note_even_when_not_populated(self):
        # build_manifest() always adds the 'other' group, the same way it always adds 'commentator' --
        # empty sub-dicts/lists when this run did not (re-)build it, never a missing key.
        manifest = EA.build_manifest({}, {}, {}, [])
        other = manifest["groups"][EA.OTHER_GROUP]
        self.assertTrue(other["note"])
        self.assertEqual(other["commentator"], {})
        self.assertEqual(other["arena"], {})
        self.assertEqual(other["commentator_skipped_clips"], [])
        self.assertEqual(other["arena_skipped_clips"], [])
        self.assertEqual(other["arena_duplicate_clips"], [])

    def test_other_group_shape_with_both_parts_populated(self):
        other_commentator = {"eng_commentator_bg_toxin_start": {
            "family": "blast_gang_excluded", "reason": "Blast Gang mode not planned", "arena_tag": ["ToxinStart"],
            "clips": [{"name": "eng_commentator_bg_toxin_start_01",
                      "file": "other/commentator/eng_commentator_bg_toxin_start/eng_commentator_bg_toxin_start_01.ogg",
                      "duration_s": 1.0, "channels": 1, "sample_rate": 44100,
                      "source_duration_s": 1.0, "source_rms": 100.0}],
        }}
        other_arena = {"resources": {"clips": [{
            "name": "arena_button_click", "file": "other/arena/resources/arena_button_click.ogg",
            "duration_s": 0.1, "channels": 2, "sample_rate": 44100, "source_duration_s": 0.1,
            "source_rms": 50.0, "kind": "ui", "kind_guess": True, "source_file": "resources.assets",
        }]}}
        manifest = EA.build_manifest({}, {}, {}, [], other_commentator=other_commentator, other_arena=other_arena)
        other = manifest["groups"][EA.OTHER_GROUP]
        self.assertEqual(other["commentator"], other_commentator)
        self.assertEqual(other["arena"], other_arena)
        json.loads(json.dumps(manifest))  # must still round-trip through JSON


class SuggestedCuesTests(unittest.TestCase):
    """Ties SUGGESTED_ARENAMODE_CUES to the real catalog/group tables so a category rename elsewhere
    cannot silently leave the suggested mapping dangling."""

    def test_every_pick_has_a_known_group_and_the_right_key(self):
        for cue, picks in EA.SUGGESTED_ARENAMODE_CUES.items():
            self.assertTrue(picks, cue)
            for pick in picks:
                self.assertIn(pick["group"], EA.ALL_GROUPS, cue)
                if pick["group"] == EA.COMMENTATOR_GROUP:
                    self.assertIn("category", pick, cue)
                else:
                    self.assertIn("clip", pick, cue)

    def test_commentator_picks_reference_included_categories(self):
        catalog = EA.load_catalog()
        included = {c["category"] for c in catalog["categories"] if c["included"]}
        for cue, picks in EA.SUGGESTED_ARENAMODE_CUES.items():
            for pick in picks:
                if pick["group"] == EA.COMMENTATOR_GROUP:
                    self.assertIn(pick["category"], included, f"{cue}: {pick['category']}")

    def test_non_commentator_picks_reference_real_clips(self):
        for cue, picks in EA.SUGGESTED_ARENAMODE_CUES.items():
            for pick in picks:
                if pick["group"] != EA.COMMENTATOR_GROUP:
                    self.assertIn(pick["clip"], EA.GROUP_SOURCES[pick["group"]]["clips"], f"{cue}: {pick['clip']}")

    def test_shutdown_stays_unmapped(self):
        # ArenaMode's Shutdown cue is a per-kill Award (ending one opponent's 5+ kill streak while the
        # match continues, AnnouncerService.CalloutPriority), not a team-wipe/match-result event, so it
        # must not point at eng_kill_all_win_red/bg_kill_all_win_blue (arena_tag KillAllWin).
        self.assertNotIn("shutdown", EA.SUGGESTED_ARENAMODE_CUES)

    def test_draw_is_mapped_and_tied_for_the_lead_is_not(self):
        # Tag evidence (2026-09-29): eng_round_draw/eng_clash_draw are catalogued generic (arena_tag
        # MatchWon/MatchEnd), not Blast-Gang-only, so ArenaMode's Draw cue has a real source. Every TeamLead
        # line names a leading team, so tied_for_the_lead would announce a false lead: never map it.
        self.assertIn("draw", EA.SUGGESTED_ARENAMODE_CUES)
        self.assertNotIn("tied_for_the_lead", EA.SUGGESTED_ARENAMODE_CUES)

    def test_kill_tier_ladder_keys_are_not_real_arenamode_cues_and_stay_absent(self):
        # A prior version of this dict invented double_kill/triple_kill/monster_kill/multi_kill/
        # godlike/unstoppable/dominating/rampage/killing_spree cue keys that do not exist anywhere in
        # ArenaMode.Client.Modes.ArenaAnnouncerCue or AnnouncerCueCatalog.Classify() (verified against
        # D:/SPT41/Development/ArenaMode, read-only). ArenaMode already owns its own per-tier recordings
        # for these HUD kill-streak labels (AnnouncerService.CalloutPriority); Arena's bank has only one
        # generic eng_commentator_killing_streak line, so none of these must be (re-)mapped here.
        for bogus_cue in ("double_kill", "triple_kill", "monster_kill", "multi_kill", "godlike",
                          "unstoppable", "dominating", "rampage", "killing_spree"):
            self.assertNotIn(bogus_cue, EA.SUGGESTED_ARENAMODE_CUES, bogus_cue)

    def test_killing_streak_category_is_catalogued_but_never_picked(self):
        picked_categories = {pick.get("category") for picks in EA.SUGGESTED_ARENAMODE_CUES.values()
                             for pick in picks if pick["group"] == EA.COMMENTATOR_GROUP}
        self.assertNotIn("eng_commentator_killing_streak", picked_categories)


class CheckModeTests(unittest.TestCase):
    """--check must never import UnityPy or touch an install -- only look at manifest.json and the
    files it references under the given output directory."""

    def test_missing_manifest_is_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = EA.check(tmp)
            self.assertIn("manifest.json", missing)

    def test_unreadable_manifest_is_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "manifest.json").write_text("{not json", encoding="utf-8")
            missing = EA.check(tmp)
            self.assertIn("manifest.json", missing)

    def _write_manifest(self, tmp, with_files=True, empty_one=False):
        clip = {"name": "arena_start", "file": "stingers/arena_start.ogg", "duration_s": 1.0,
               "channels": 2, "sample_rate": 44100, "source_duration_s": 1.0, "source_rms": 1.0}
        commentator_clip = {"name": "eng_commentator_start_01", "file": "commentator/eng_commentator_start/eng_commentator_start_01.ogg",
                           "duration_s": 1.0, "channels": 1, "sample_rate": 44100, "source_duration_s": 1.0, "source_rms": 1.0}
        manifest = EA.build_manifest(
            {"stingers": {"clips": [clip]}},
            {"eng_commentator_start": {"family": "general_time", "reason": "x", "clips": [commentator_clip]}},
            {}, [])
        (Path(tmp) / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        if with_files:
            for rel, size in ((clip["file"], 100), (commentator_clip["file"], 0 if empty_one else 100)):
                path = Path(tmp) / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x" * size)
        return manifest

    def test_passes_when_every_referenced_file_exists_and_is_non_empty(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_manifest(tmp, with_files=True)
            self.assertEqual(EA.check(tmp), {})

    def test_reports_missing_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_manifest(tmp, with_files=False)
            missing = EA.check(tmp)
            self.assertEqual(len(missing), 2)

    def test_an_empty_file_still_counts_as_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_manifest(tmp, with_files=True, empty_one=True)
            missing = EA.check(tmp)
            self.assertEqual(set(missing), {"commentator/eng_commentator_start/eng_commentator_start_01.ogg"})

    def test_check_covers_the_other_group(self):
        with tempfile.TemporaryDirectory() as tmp:
            other_commentator_clip = {
                "name": "eng_commentator_bg_toxin_start_01",
                "file": "other/commentator/eng_commentator_bg_toxin_start/eng_commentator_bg_toxin_start_01.ogg",
                "duration_s": 1.0, "channels": 1, "sample_rate": 44100, "source_duration_s": 1.0, "source_rms": 100.0,
            }
            other_arena_clip = {
                "name": "arena_button_click", "file": "other/arena/resources/arena_button_click.ogg",
                "duration_s": 0.1, "channels": 2, "sample_rate": 44100, "source_duration_s": 0.1,
                "source_rms": 50.0, "kind": "ui", "kind_guess": True, "source_file": "resources.assets",
            }
            manifest = EA.build_manifest(
                {}, {}, {},
                [],
                other_commentator={"eng_commentator_bg_toxin_start": {
                    "family": "blast_gang_excluded", "reason": "x", "arena_tag": ["ToxinStart"],
                    "clips": [other_commentator_clip]}},
                other_arena={"resources": {"clips": [other_arena_clip]}})
            (Path(tmp) / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

            # Neither file exists yet -- both must be reported missing.
            missing = EA.check(tmp)
            self.assertEqual(set(missing), {other_commentator_clip["file"], other_arena_clip["file"]})

            for clip in (other_commentator_clip, other_arena_clip):
                path = Path(tmp) / clip["file"]
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x" * 100)
            self.assertEqual(EA.check(tmp), {})


@unittest.skipUnless(os.path.isdir(ARENA_SOURCE) and os.path.isdir(EFT_SOURCE),
                     "Arena/EFT installs not present on this machine")
class RealSourceExtractionTests(unittest.TestCase):
    """One real-source pass, guarded like test_banners.py's own: extracts a couple of small, cheap
    groups (round_signal: 1 clip, capture: 2 clips) into a throwaway temp dir and confirms the whole
    decode -> encode -> ffprobe-validate -> manifest -> check() pipeline works end to end."""

    def test_extract_two_small_groups(self):
        with tempfile.TemporaryDirectory() as tmp:
            manifest = EA.extract_all(ARENA_SOURCE, EFT_SOURCE, tmp, only=["round_signal", "capture"], workers=2)
            self.assertEqual(set(manifest["groups"]),
                             {"round_signal", "capture", EA.COMMENTATOR_GROUP, EA.OTHER_GROUP})
            round_signal_clips = manifest["groups"]["round_signal"]["clips"]
            capture_clips = manifest["groups"]["capture"]["clips"]
            self.assertEqual(len(round_signal_clips), 1)
            self.assertEqual(len(capture_clips), 2)
            for clip in round_signal_clips + capture_clips:
                path = Path(tmp) / clip["file"]
                self.assertTrue(path.is_file())
                self.assertGreater(path.stat().st_size, 0)
                self.assertEqual(clip["sample_rate"], EA.ENCODE_SAMPLE_RATE)
            self.assertEqual(EA.check(tmp), {})


if __name__ == "__main__":
    unittest.main()
