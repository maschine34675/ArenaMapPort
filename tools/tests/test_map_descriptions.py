"""Synthetic contract and extraction tests; no game text or Arena dependency."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_map_descriptions as descriptions


def synthetic_pack():
    return {
        "schema_version": 1,
        "descriptions": {
            "en": {"arena_equator": "Synthetic mall text.", "arena_bay5": "Synthetic harbor text.",
                   "arena_airpit": "Synthetic terminal text."},
            "ge": {"arena_equator": "Synthetic ä text.", "arena_bay5": "Synthetic ß text.",
                   "arena_airpit": "Synthetic ö text."},
        },
    }


def synthetic_source():
    objects = {}
    for index, (locale, name) in enumerate(descriptions.LOCALE_ASSETS.items()):
        asset = SimpleNamespace(m_Name=name, m_Script=json.dumps({
            "data": {key: f"  Synthetic {locale} {map_key} text.  "
                     for map_key, key in descriptions.DESCRIPTION_KEYS.items()}
        }))
        objects[index] = SimpleNamespace(type=SimpleNamespace(name="TextAsset"),
                                         read=lambda asset=asset: asset)
    return SimpleNamespace(objects=objects)


class DescriptionContractTests(unittest.TestCase):
    def test_keys_are_exactly_the_three_released_maps(self):
        self.assertEqual(set(descriptions.DESCRIPTION_KEYS), {"arena_equator", "arena_bay5", "arena_airpit"})

    def test_airpit_uses_arenas_location_id_not_the_sidecar_id_field(self):
        # Arena's own location id (its "Name" key reads Air Pit); the ported location's id_field is another value.
        self.assertEqual(descriptions.DESCRIPTION_KEYS["arena_airpit"], "61e800b841ee214fbf1f0e53 Description")
        sidecar = json.loads((Path(__file__).resolve().parents[1] / "maps" / "airpit.json").read_text(encoding="utf-8"))
        self.assertNotIn(sidecar["id_field"], descriptions.DESCRIPTION_KEYS["arena_airpit"])

    def test_a_pack_without_airpit_is_rejected(self):
        # A pack generated before AirPit was promoted must fail validation (the server discards it wholesale).
        pack = synthetic_pack()
        for maps in pack["descriptions"].values():
            del maps["arena_airpit"]
        with self.assertRaises(ValueError):
            descriptions.validate_pack(pack)

    def test_returns_mapping_without_changing_translated_text(self):
        pack = synthetic_pack()
        self.assertIs(descriptions.validate_pack(pack), pack["descriptions"])

    def test_english_only_is_valid(self):
        pack = synthetic_pack()
        del pack["descriptions"]["ge"]
        self.assertEqual(set(descriptions.validate_pack(pack)), {"en"})

    def test_rejects_invalid_envelopes_and_schema(self):
        for payload in (None, [], {}, {"schema_version": 1},
                        {**synthetic_pack(), "unexpected": True}):
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                descriptions.validate_pack(payload)
        for version in (True, "1", 1.0, 0, 2, None):
            with self.subTest(version=version), self.assertRaises(ValueError):
                descriptions.validate_pack({**synthetic_pack(), "schema_version": version})

    def test_requires_english_and_valid_locale_maps(self):
        for maps in (None, [], {}, {"ge": synthetic_pack()["descriptions"]["ge"]},
                     {"en": []}, {"en": {}}, {"en": {"arena_equator": "Synthetic"}}):
            with self.subTest(maps=maps), self.assertRaises(ValueError):
                descriptions.validate_pack({"schema_version": 1, "descriptions": maps})

    def test_rejects_unreleased_map_and_incomplete_translation(self):
        for locale in ("en", "ge"):
            for change in ("extra", "missing"):
                pack = deepcopy(synthetic_pack())
                if change == "extra":
                    pack["descriptions"][locale]["arena_other"] = "Synthetic extra."
                else:
                    del pack["descriptions"][locale]["arena_bay5"]
                with self.subTest(locale=locale, change=change), self.assertRaises(ValueError):
                    descriptions.validate_pack(pack)

    def test_rejects_blank_and_non_string_descriptions(self):
        for text in ("", " \n\t", None, 123, [], {}):
            pack = synthetic_pack()
            pack["descriptions"]["ge"]["arena_bay5"] = text
            with self.subTest(text=text), self.assertRaises(ValueError):
                descriptions.validate_pack(pack)

    def test_rejects_invalid_language_keys(self):
        for language in ("", " ", " en", 1):
            pack = synthetic_pack()
            pack["descriptions"][language] = pack["descriptions"]["en"]
            with self.subTest(language=language), self.assertRaises(ValueError):
                descriptions.validate_pack(pack)


class DescriptionExtractionTests(unittest.TestCase):
    def test_extracts_by_asset_identity_and_preserves_whitespace(self):
        pack = descriptions.pack_from_assets(synthetic_source())
        self.assertEqual(set(pack["descriptions"]), {"en", "ge", "ru"})
        self.assertEqual(pack["descriptions"]["en"]["arena_equator"],
                         "  Synthetic en arena_equator text.  ")

    def test_missing_and_duplicate_named_assets_are_errors(self):
        for duplicate in (False, True):
            source = synthetic_source()
            if duplicate:
                source.objects[99] = source.objects[0]
            else:
                del source.objects[0]
            with self.subTest(duplicate=duplicate), self.assertRaises(ValueError):
                descriptions.pack_from_assets(source)

    def test_wrong_asset_type_cannot_satisfy_identity(self):
        source = synthetic_source()
        source.objects[0].type.name = "MonoBehaviour"
        with self.assertRaises(ValueError):
            descriptions.pack_from_assets(source)

    def test_rejects_invalid_source_json_and_missing_key(self):
        for text in ("invalid json", "[]", "{}", '{"data":{}}'):
            source = synthetic_source()
            source.objects[0].read().m_Script = text
            with self.subTest(text=text), self.assertRaises(ValueError):
                descriptions.pack_from_assets(source)

    def test_source_guard_covers_data_and_install_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "Arena" / "EscapeFromTarkovArena_Data"
            for output in (source / "map-descriptions.json",
                           source / "nested" / "map-descriptions.json",
                           source.parent / "map-descriptions.json",
                           source / "nested" / ".." / "map-descriptions.json"):
                with self.subTest(output=output), self.assertRaises(ValueError):
                    descriptions.ensure_safe_output(source, output)
            safe = Path(tmp) / "work" / "map-descriptions.json"
            self.assertEqual(descriptions.ensure_safe_output(source, safe), safe.resolve())

    def test_check_does_not_load_source_or_unitypy(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "map-descriptions.json"
            output.write_text(json.dumps(synthetic_pack()), encoding="utf-8")
            with patch.object(descriptions, "extract", side_effect=AssertionError("must not extract")):
                self.assertEqual(descriptions.main(["--check", "--output", str(output)]), 0)


if __name__ == "__main__":
    unittest.main()
