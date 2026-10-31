"""The one list of maps with a native sea (Bay5's harbor since 2026-09-28, Iceberg's sea since 2026-10-31) and every place that must agree with it.

validate_map_descriptors.NATIVE_WATER_MAP_KEYS is the list; the sidecars' has_native_water, the C# descriptor's HasNativeWater (both held equal to it by
native_water_errors), the distribution validator's WATER_MAP_KEYS, the packer's rules and required files, the extractor's table (extract_water.MAPS) and the
client build's staging target are each pinned to it here, so adding a map to one of them without the others fails a test instead of a raid.
The C# side (the switch table, the identity rule, the source of NativeWaterRepairs) is pinned by ArenaMapPort.Server.Tests/NativeWaterTests.cs."""
import json
import re
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
for folder in ("validate", "release", "extract"):
    sys.path.insert(0, str(REPO / "tools" / folder))

import extract_water as W  # noqa: E402
import make_release_zips as release  # noqa: E402
import validate_distribution as VDIST  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402

DESCRIPTOR_CS = REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs"
CSPROJ = REPO / "ArenaMapPort.Client" / "ArenaMapPort.Client.csproj"
WATER_KEYS = ("arena_bay5", "arena_iceberg")


def sidecars():
    return {path.stem: json.loads(path.read_text(encoding="utf-8")) for path in VD.map_descriptor_paths(REPO / "tools" / "maps")}


class ListAgreementTests(unittest.TestCase):
    def test_the_list_is_bay5_and_iceberg(self):
        self.assertEqual(VD.NATIVE_WATER_MAP_KEYS, WATER_KEYS)
        self.assertTrue(set(VD.NATIVE_WATER_MAP_KEYS) <= set(VD.RELEASE_MAP_KEYS))
        self.assertEqual(len(VD.NATIVE_WATER_MAP_KEYS), len(set(VD.NATIVE_WATER_MAP_KEYS)))

    def test_every_reader_of_the_list_agrees(self):
        self.assertEqual(VDIST.WATER_MAP_KEYS, VD.NATIVE_WATER_MAP_KEYS)
        self.assertEqual(sorted(W.MAPS), sorted(VD.NATIVE_WATER_MAP_KEYS))
        self.assertIs(release.NATIVE_WATER_MAP_KEYS, VD.NATIVE_WATER_MAP_KEYS)

    def test_the_sidecar_flag_is_true_for_exactly_the_listed_maps(self):
        documents = sidecars()
        self.assertEqual(len(documents), 14)
        for stem, document in documents.items():
            with self.subTest(sidecar=stem):
                self.assertEqual(document.get("has_native_water", False), document["key"] in VD.NATIVE_WATER_MAP_KEYS)
                if "has_native_water" in document:
                    self.assertIs(type(document["has_native_water"]), bool)

    def test_the_extractor_rows_name_the_sidecars_of_the_listed_maps(self):
        documents = sidecars()
        for key, row in W.MAPS.items():
            with self.subTest(map=key):
                self.assertEqual(documents[row.stem]["key"], key)
                self.assertTrue(documents[row.stem]["has_native_water"])
                self.assertIn(row.scene, documents[row.stem]["all_scene_names"])

    def test_the_packers_texture_names_are_the_extractors(self):
        self.assertEqual(sorted(release.WATER_TEXTURES), sorted(name.lstrip("_") for name in W.TEXTURE_FIELDS))


class ClientStagingTests(unittest.TestCase):
    def target(self):
        text = CSPROJ.read_text(encoding="utf-8")
        start = text.index('<Target Name="ArenaMapPortStageWater"')
        return text[start:text.index("</Target>", text.index("<Copy SourceFiles", start))]

    def test_the_target_stages_the_listed_maps_from_their_default_export_folders(self):
        target = self.target()
        folders = re.findall(r"\\([A-Za-z0-9]+-water)\\", target)
        self.assertEqual(sorted(set(folders)), sorted(f"{row.stem}-water" for row in W.MAPS.values()))
        for row in W.MAPS.values():
            self.assertEqual(target.count(f"\\{row.stem}-water\\"), 5)      # water.json and the four mip chains
            self.assertEqual(target.count(f"<MapKey>{row.map_key}</MapKey>"), 5)
            self.assertEqual(row.default_output.name, f"{row.stem}-water")

    def test_every_staged_file_name_is_one_the_extractor_writes(self):
        target = self.target()
        written = {"water.json", *("textures\\" + field.lstrip("_") + ".bin" for field in W.TEXTURE_FIELDS)}
        self.assertEqual(set(re.findall(r"<Dest>([^<]+)</Dest>", target)), written)

    def test_the_target_runs_in_dll_only_builds_and_fails_on_a_missing_input(self):
        target = self.target()
        header = target[:target.index(">") + 1]
        self.assertIn("Condition=\"'$(Configuration)'=='Release'\"", header)
        self.assertNotIn("DllOnly", target)
        self.assertIn("Missing native water input", target)
        self.assertIn("<Error Condition=\"!Exists('%(ArenaMapPortWaterFile.Identity)')\"", target)


REGISTRY = DESCRIPTOR_CS.read_text(encoding="utf-8")


class MirrorCheckTests(unittest.TestCase):
    """native_water_errors: the sidecar flag, the descriptor's HasNativeWater and NATIVE_WATER_MAP_KEYS must say the same."""

    def entries(self, text=REGISTRY):
        return VD.registry_entries(text)

    def test_the_real_sidecars_and_descriptors_agree(self):
        entries = self.entries()
        documents = sidecars()
        for stem, name in (("bay5", "Bay5"), ("iceberg", "Iceberg"), ("equator", "Equator"), ("airpit", "AirPit"), ("bowl", "Bowl")):
            with self.subTest(map=name):
                self.assertEqual(VD.native_water_errors(documents[stem], entries[name]), [])
        self.assertEqual(VD.cs_bool(entries["Iceberg"], "HasNativeWater"), True)
        self.assertEqual(VD.cs_bool(entries["Bay5"], "HasNativeWater"), True)
        self.assertIsNone(VD.cs_bool(entries["Equator"], "HasNativeWater"))

    def test_a_descriptor_without_the_flag_for_a_listed_map_is_a_mismatch(self):
        text = REGISTRY.replace("            HasNativeWater: true,\n", "", 1) if "\r\n" not in REGISTRY else REGISTRY.replace("            HasNativeWater: true,\r\n", "", 1)
        self.assertNotEqual(text, REGISTRY)
        entries = self.entries(text)
        documents = sidecars()
        broken = [name for name, stem in (("Bay5", "bay5"), ("Iceberg", "iceberg")) if VD.native_water_errors(documents[stem], entries[name])]
        self.assertEqual(len(broken), 1)
        errors = VD.native_water_errors(documents["bay5" if broken[0] == "Bay5" else "iceberg"], entries[broken[0]])
        self.assertTrue(any("mismatch" in error and "HasNativeWater" in error for error in errors), errors)

    def test_each_wrong_combination_is_named(self):
        listed = {"key": "arena_iceberg"}
        unlisted = {"key": "arena_equator"}
        yes, no = {"HasNativeWater": ("localref", "true")}, {"HasNativeWater": ("localref", "false")}
        self.assertEqual(VD.native_water_errors({**listed, "has_native_water": True}, yes), [])
        self.assertEqual(VD.native_water_errors(unlisted, {}), [])
        self.assertEqual(VD.native_water_errors({**unlisted, "has_native_water": False}, no), [])
        self.assertTrue(any("sidecar False" in e for e in VD.native_water_errors({**listed, "has_native_water": False}, yes)))
        self.assertTrue(any("sidecar True" in e and "HasNativeWater False" in e for e in VD.native_water_errors({**listed, "has_native_water": True}, no)))
        self.assertTrue(any("does not list" in e for e in VD.native_water_errors({**unlisted, "has_native_water": True}, yes)))
        self.assertTrue(any("lists" in e for e in VD.native_water_errors(listed, {})))
        self.assertTrue(any("boolean" in e for e in VD.native_water_errors({**listed, "has_native_water": "yes"}, yes)))
        self.assertTrue(any("boolean" in e for e in VD.native_water_errors({**listed, "has_native_water": 1}, yes)))

    def test_an_argument_that_is_no_boolean_literal_is_a_parse_failure(self):
        with self.assertRaises(SystemExit):
            VD.cs_bool({"HasNativeWater": ("localref", "maybe")}, "HasNativeWater")
        with self.assertRaises(SystemExit):
            VD.cs_bool({"HasNativeWater": 1}, "HasNativeWater")


class PackerTests(unittest.TestCase):
    def test_a_water_rule_exists_only_for_the_listed_maps(self):
        spec = release.load_spec(REPO, "9.9.9", maps=tuple(zip(VD.RELEASE_MAP_KEYS, VD.RELEASE_MAP_NAMES)))
        labels = {(rule.part, rule.label) for rule in release.build_rules(spec)}
        for key in VD.RELEASE_MAP_KEYS:
            stem = key.removeprefix("arena_")
            self.assertEqual((stem, "water") in labels, key in VD.NATIVE_WATER_MAP_KEYS, key)

    def test_the_water_rule_accepts_what_the_build_stages_and_nothing_else(self):
        spec = release.load_spec(REPO, "9.9.9", maps=tuple(zip(VD.RELEASE_MAP_KEYS, VD.RELEASE_MAP_NAMES)))
        rules = release.build_rules(spec)
        plugin = "BepInEx/plugins/maschine-ArenaMapPort"
        for key in VD.NATIVE_WATER_MAP_KEYS:
            stem = key.removeprefix("arena_")
            for name in ("water.json", *(f"textures/{texture}.bin" for texture in release.WATER_TEXTURES)):
                self.assertEqual(release.classify(f"{plugin}/water/{key}/{name}", rules).part, stem)
            for stray in ("water.json~", "textures/desktop.ini", "textures/a/b.bin", "extra.json", "textures/foam.bin.bak"):
                with self.assertRaises(release.ClassificationError, msg=f"{key}/{stray}"):
                    release.classify(f"{plugin}/water/{key}/{stray}", rules)
        for key in ("arena_equator", "arena_airpit", "arena_bowl", "arena_airpit2"):
            with self.assertRaises(release.ClassificationError, msg=key):
                release.classify(f"{plugin}/water/{key}/water.json", rules)


if __name__ == "__main__":
    unittest.main()
