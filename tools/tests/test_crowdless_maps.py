"""The crowdless-map rule (PLAN-ICEBERG "No spectator crowd and no murmur bed", 2026-10-30).

Iceberg is a released map without a spectator crowd. Everything that would otherwise demand a crowd for every released map reads ONE list,
validate_map_descriptors.CROWDLESS_MAP_KEYS: the packer (its rules and its required files), validate_distribution (the partition of the release into crowd
maps and crowdless maps) and, by hand, the client (ArenaSpectators.DesignScene returns null for exactly these keys). The crowd maps themselves are
extract_spectator.MAP_SCENES, which stage_spectators stages. These tests pin the partition and each reader; the not-yet rules for the other Phase C
packages of Iceberg (flares, DynamicMaps overview) are pinned here too, so adding one later is a deliberate edit of the explicit lists. Its water package
joined on 2026-10-31 and is pinned as present.
"""
import re
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
sys.path.insert(0, str(REPO / "tools" / "release"))
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import make_release_zips as release  # noqa: E402
import validate_distribution as VDIST  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402
from test_release_zips import PLUGIN, fake_dist_files, write_tree  # noqa: E402

PROD_MAPS = tuple(zip(VD.RELEASE_MAP_KEYS, VD.RELEASE_MAP_NAMES))


def crowd_keys():
    import extract_spectator
    return {f"arena_{name}" for name in extract_spectator.MAP_SCENES}


class PartitionTests(unittest.TestCase):
    def test_the_crowd_maps_and_the_crowdless_maps_partition_the_released_maps(self):
        crowdless = set(VD.CROWDLESS_MAP_KEYS)
        self.assertEqual(crowdless, {"arena_iceberg"})
        self.assertEqual(len(crowdless), len(VD.CROWDLESS_MAP_KEYS))
        self.assertFalse(crowd_keys() & crowdless)
        self.assertEqual(crowd_keys() | crowdless, set(VD.RELEASE_MAP_KEYS))

    def test_stage_spectators_stages_exactly_the_crowd_maps(self):
        import extract_spectator
        import stage_spectators
        self.assertEqual(set(extract_spectator.MAP_SCENES), {"airpit", "equator", "bay5", "bowl"})
        text = (REPO / "tools" / "extract" / "stage_spectators.py").read_text(encoding="utf-8")
        self.assertNotIn("iceberg", text.lower())                  # no per-map literal: MAP_SCENES is the one list
        self.assertTrue(hasattr(stage_spectators, "MAP_SCENES"))

    def test_the_client_has_a_crowd_scene_for_exactly_the_crowd_maps(self):
        import extract_spectator
        source = (REPO / "ArenaMapPort.Client" / "ArenaSpectators.cs").read_text(encoding="utf-8")
        body = re.search(r"static string DesignScene\(string key\)\s*\{(?P<body>.*?)\n        \}\n", source, re.S).group("body")
        returns = dict(re.findall(r'case "(arena_[a-z0-9]+)": return (null|"[A-Za-z_0-9]+");', body))
        scenes = {key: value.strip('"') for key, value in returns.items() if value != "null"}
        self.assertEqual(scenes, {f"arena_{name}": scene for name, scene in extract_spectator.MAP_SCENES.items()})
        self.assertEqual({key for key, value in returns.items() if value == "null"}, set(VD.CROWDLESS_MAP_KEYS))
        self.assertIn("default: return null;", body)

    def test_the_csproj_names_no_map_for_the_spectator_staging(self):
        text = (REPO / "ArenaMapPort.Client" / "ArenaMapPort.Client.csproj").read_text(encoding="utf-8")
        start = text.index('<Target Name="ArenaMapPortStageSpectator"')
        target = text[start:text.index("</Target>", start)]
        self.assertIn("stage_spectators.py", target)
        self.assertNotIn("iceberg", target.lower())


class PackerTests(unittest.TestCase):
    def spec(self, maps=PROD_MAPS):
        return release.load_spec(REPO, "9.9.9", maps=maps)

    def test_a_crowdless_map_has_no_spectator_rule_and_no_required_spectator_file(self):
        spec = self.spec()
        required = release.required_files(spec)
        for key, _display in PROD_MAPS:
            stem = key.removeprefix("arena_")
            graph = f"{PLUGIN}/spectators/{key}/spectator.json.gz"
            self.assertEqual(graph in required[stem], key not in VD.CROWDLESS_MAP_KEYS, key)
        labels = {(rule.part, rule.label) for rule in release.build_rules(spec)}
        self.assertNotIn(("iceberg", "spectators"), labels)
        for stem in ("equator", "bay5", "airpit", "bowl"):
            self.assertIn((stem, "spectators"), labels)

    def test_a_stray_spectator_package_of_a_crowdless_map_is_unclassified(self):
        rules = release.build_rules(self.spec())
        for rel, part in ((f"{PLUGIN}/spectators/arena_bowl/spectator.json.gz", "bowl"),):
            self.assertEqual(release.classify(rel, rules).part, part)
        with self.assertRaises(release.ClassificationError):
            release.classify(f"{PLUGIN}/spectators/arena_iceberg/spectator.json.gz", rules)

    def test_iceberg_partitions_without_a_crowd_and_every_other_map_still_needs_one(self):
        files = fake_dist_files(PROD_MAPS)
        del files[f"{PLUGIN}/spectators/arena_iceberg/spectator.json.gz"]      # the fixture stages a crowd for every map: Iceberg has none
        spec = self.spec()
        with tempfile.TemporaryDirectory() as temporary:
            write_tree(Path(temporary), files)
            dist = release.scan_dist(Path(temporary))
            parts = release.partition_dist(dist, spec)
            by_id = {part.id: {f.rel for f in part.files} for part in parts}
            self.assertEqual(sorted(by_id), sorted(["core"] + [m.stem for m in spec.maps]))
            self.assertTrue(any(rel.endswith("/db/arena_iceberg/base.json") for rel in by_id["iceberg"]))
            self.assertFalse(any("spectators" in rel for rel in by_id["iceberg"]))
            self.assertTrue(any(rel.endswith("arena_bowl/spectator.json.gz") for rel in by_id["bowl"]))
            single = release.partition_single(dist, spec)
            self.assertEqual({f.rel for f in single[0].files}, set(files))
            # a crowd map without its crowd is still refused
            broken = [f for f in dist if not f.rel.endswith("arena_bowl/spectator.json.gz")]
            with self.assertRaisesRegex(release.PackagingError, "incomplete dist"):
                release.partition_dist(broken, spec)
            # and so is a crowd package of the crowdless map
            (Path(temporary) / PLUGIN / "spectators" / "arena_iceberg").mkdir(parents=True)
            (Path(temporary) / PLUGIN / "spectators" / "arena_iceberg" / "spectator.json.gz").write_bytes(b"x")
            with self.assertRaises(release.ClassificationError):
                release.partition_dist(release.scan_dist(Path(temporary)), spec)


class NotYetRulesTests(unittest.TestCase):
    """Iceberg's later packages are explicit absences (PLAN-ICEBERG Phase C), never fake data: the lists that name the maps that HAVE them leave it out.
    Its native sea water is no longer one of them (2026-10-31, docs/progress/iceberg-release.md "native sea water"); the water side is pinned below
    and in test_native_water.py."""

    def test_iceberg_has_no_overview_or_flares_yet(self):
        self.assertNotIn("Iceberg", VDIST.OVERVIEW_MAP_NAMES)
        self.assertNotIn("arena_iceberg", VDIST.FLARE_MAP_KEYS)
        self.assertEqual(VDIST.OVERVIEW_MAP_NAMES, ("Equator", "Bay5", "AirPit", "Bowl"))
        self.assertEqual(VDIST.FLARE_MAP_KEYS, ("arena_equator", "arena_bay5", "arena_bowl"))

    def test_iceberg_has_native_water_since_2026_10_31(self):
        self.assertIn("arena_iceberg", VDIST.WATER_MAP_KEYS)
        self.assertEqual(VDIST.WATER_MAP_KEYS, ("arena_bay5", "arena_iceberg"))
        self.assertEqual(VDIST.WATER_MAP_KEYS, VD.NATIVE_WATER_MAP_KEYS)

    def test_the_client_csproj_stages_no_iceberg_overview_or_flare_rows(self):
        text = (REPO / "ArenaMapPort.Client" / "ArenaMapPort.Client.csproj").read_text(encoding="utf-8")
        for target in ("ArenaMapPortStageDynamicMaps", "ArenaMapPortStageMultiFlare"):
            start = text.index(f'<Target Name="{target}"')
            body = text[start:text.index("</Target>", start)]
            self.assertNotIn("iceberg", body.lower(), target)

    def test_the_client_csproj_stages_icebergs_water_package(self):
        text = (REPO / "ArenaMapPort.Client" / "ArenaMapPort.Client.csproj").read_text(encoding="utf-8")
        start = text.index('<Target Name="ArenaMapPortStageWater"')
        body = text[start:text.index("</Target>", text.index("<Copy SourceFiles", start))]
        self.assertEqual(body.count("iceberg-water"), 5)           # water.json and the four mip chains
        self.assertIn("<MapKey>arena_iceberg</MapKey>", body)

    def test_the_packer_treats_flares_and_overview_as_optional_and_requires_the_water_package(self):
        spec = release.load_spec(REPO, "9.9.9", maps=PROD_MAPS)
        required = release.required_files(spec)["iceberg"]
        self.assertFalse([name for name in required if any(f"/{folder}/" in name for folder in ("flares", "dynamicmaps"))])
        self.assertEqual(sorted(name for name in required if "/water/" in name),
                         sorted([f"{PLUGIN}/water/arena_iceberg/water.json",
                                 *(f"{PLUGIN}/water/arena_iceberg/textures/{n}.bin" for n in release.WATER_TEXTURES)]))


if __name__ == "__main__":
    unittest.main()
