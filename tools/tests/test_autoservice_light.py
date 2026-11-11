"""Autoservice light, flares and shaders offline (PLAN-AUTOSERVICE item 11, 2026-11-11).

Pins the measurements the later promotion (item 12) and the first U1 build (item 13) stand on. Nothing here wires a client switch: the
client rows (MapLightingDefaults, the shader stand-in switch) are item 12's, and a test of the released set would break if they were
added to a Catalog-only key. Four groups, each skipped when its input is absent (override the folders with ARENAMAPPORT_WORK_DIR,
ARENAMAPPORT_EXTRACT_ROOT, ARENAMAPPORT_ARENA_DATA and ARENAMAPPORT_EFT_DATA when the tests run from a git worktree):

  * the package v2 measurements: the spawn markers' floor and the lighting zero level derived from it, the strongest emissive material
    under the EmissionCapModel rules (no cap), the shader census of 28 names;
  * EFT 0.16.9.5's shaders (read-only, UnityPy over EscapeFromTarkov_Data): which of the 28 names EFT carries and in which file;
  * the scene dependencies that decide whether a scene-only shader is loaded at raid time on a ported map;
  * the light-component census of Arena's own level files (the classes no exporter covers).
"""
import glob
import json
import os
import re
import sys
import unittest
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(REPO / "tools" / "extract"))

WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
EXTRACT = Path(os.environ.get("ARENAMAPPORT_EXTRACT_ROOT", WORK / "extract"))
PACKAGE = EXTRACT / "autoservice-native-v2"
ARENA_DATA = Path(os.environ.get("ARENAMAPPORT_ARENA_DATA")
                  or "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data")
EFT_DATA = Path(os.environ.get("ARENAMAPPORT_EFT_DATA", "D:/SPT41/EscapeFromTarkov_Data"))
MAPS = REPO / "tools" / "maps"
CLIENT = REPO / "ArenaMapPort.Client"

# The 28 distinct shader names of package v2 and where EFT 0.16.9.5 carries the four that its global shaders bundle does not.
SCENE_ONLY = {
    "Custom/Billboard_FogSheet_Simple": "sharedassets13.assets",           # custom_Light
    "Cloth/ClothShader": "sharedassets5.assets",                           # custom_multiScene
    "Cloth/ClothShader_backface": "sharedassets5.assets",
    "p0/Reflective/Bumped Specular SMap Baked": "sharedassets216.assets",  # City_NE_02_buildings_back
}
SPAWN_FLOOR_RULE = 4.4   # MapLightingDefaults: the dome zero level lies 4.4 m below the lowest spawn-marker floor


class PackageMeasurementTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not (PACKAGE / "manifest.json").is_file():
            raise unittest.SkipTest("work/extract/autoservice-native-v2 not present")
        cls.manifest = json.loads((PACKAGE / "manifest.json").read_text(encoding="utf-8"))

    def test_the_zero_level_follows_the_spawn_floor_rule_and_equals_bay5s(self):
        markers = self.manifest["anchors_reference"]["EFT.Game.Spawning.SpawnPointMarker"]
        self.assertEqual(markers["count"], 20)
        ys = [p[1] for p in markers["sample_positions"]]
        self.assertEqual(len(ys), 20)
        self.assertAlmostEqual(min(ys), -3.84, places=2)
        self.assertAlmostEqual(max(ys), -3.72, places=2)
        # Both pockets stand on the yard floor at y -3.8 (the capture point is at -3.80): the lowest floor minus 4.4 is -8.24, i.e. -8.2.
        self.assertEqual(round(min(ys) - SPAWN_FLOOR_RULE, 1), -8.2)
        # Bay5's spawn hall floor is also y -3.8 and carries the same value (WeatherRigDefaultsTests pins -8.2 for it).
        text = (CLIENT / "MapLightingDefaults.cs").read_text(encoding="utf-8")
        # (the promotion, PLAN-AUTOSERVICE item 12, gave Autoservice its own row in the same expression; WeatherRigDefaultsTests pins the values)
        self.assertIn("bay5 || autoService ? -8.2f", text)
        self.assertIn("bool autoService = mapKey == ArenaMapRegistry.AutoService.Key;", text)

    def test_no_emission_cap_is_needed(self):
        import test_iceberg_emission_cap as emission
        rows = emission.census(PACKAGE)
        self.assertEqual(len(rows), 15)
        strongest = rows[0]
        self.assertEqual(strongest[2], "Material #37")
        self.assertEqual(strongest[1], "Power")
        self.assertAlmostEqual(strongest[0], 3.8833, places=3)
        # Below the strongest emissive of every released map except AirPit's 3.62 (Bay5 4.128, Equator and Bowl 3.932): the same family of
        # maps the donor bloom pair 2.10 / 0.35 was accepted on. Iceberg's 67.8 is what the cap exists for.
        self.assertLess(strongest[0], 4.128)
        # Deviation from the plan text: eight Standard materials DO carry the _EMISSION keyword and all are used by renderers; the
        # strongest is stones_pack_rebake_D at 2.738, so none of them comes near the cap range and the answer (no cap) stays.
        standard = [r for r in rows if r[3] == "Standard"]
        self.assertEqual(sorted(r[2] for r in standard),
                         sorted(["24 - Default", "AreaLightSource", "City_emissive", "No_Name_01", "barrel_metal_fire_wfire",
                                 "chemlight_d", "projector_emission", "stones_pack_rebake_D"]))
        self.assertAlmostEqual(max(r[0] for r in standard), 2.738, places=3)

    def test_sidecar_and_descriptor_carry_no_cap_and_the_four_name_stand_in_list(self):
        # Item 12 (the promotion, 2026-11-11) flipped the pending pin: the list the light hand-off decided is in the sidecar AND the descriptor
        # (validate_map_descriptors mirrors them; the C# side is pinned by ShaderStandinPolicyTests), the cap and floor stay absent.
        sidecar = json.loads((MAPS / "autoservice.json").read_text(encoding="utf-8"))
        for key in ("emission_cap", "emission_floor"):
            self.assertNotIn(key, sidecar)
        self.assertEqual(sidecar["shader_fallback_names"], list(SCENE_ONLY))
        descriptor = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        block = descriptor[descriptor.index("public static readonly ArenaMapDescriptor AutoService"):descriptor.index("public static readonly ArenaMapDescriptor Bowl")]
        for name in SCENE_ONLY:
            self.assertIn(f'"{name}"', block)
        self.assertNotIn("EmissionCap:", block)
        self.assertNotIn("EmissionFloor:", block)

    def test_package_shader_census_is_28_names_with_one_baked_material(self):
        names = Counter(json.loads(Path(p).read_text(encoding="utf-8"))["shader"] for p in glob.glob(str(PACKAGE / "materials" / "*.json")))
        self.assertEqual(len(names), 28)
        self.assertEqual(names["p0/Reflective/Bumped Specular SMap Baked"], 1)
        self.assertEqual(names["Custom/Billboard_FogSheet_Simple"], 2)
        self.assertEqual(names["Cloth/ClothShader"] + names["Cloth/ClothShader_backface"], 3)
        for name in SCENE_ONLY:
            self.assertIn(name, names)

    def test_the_baked_stand_in_row_exists_for_item_12(self):
        text = (CLIENT / "ShaderStandinPolicy.cs").read_text(encoding="utf-8")
        self.assertIn('["p0/Reflective/Bumped Specular SMap Baked"] = "p0/Reflective/Bumped Specular SMap"', text)


class EftShaderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not (EFT_DATA / "globalgamemanagers.assets").is_file() or not (PACKAGE / "manifest.json").is_file():
            raise unittest.SkipTest("EscapeFromTarkov_Data or the Autoservice package not present")
        import UnityPy
        cls.UnityPy = UnityPy
        cls.wanted = {json.loads(Path(p).read_text(encoding="utf-8"))["shader"] for p in glob.glob(str(PACKAGE / "materials" / "*.json"))}

    def names_in(self, file):
        env = self.UnityPy.load(str(EFT_DATA / file))
        found = {}
        for obj in env.objects:
            if obj.type.name == "Shader":
                name = obj.read_typetree().get("m_ParsedForm", {}).get("m_Name")
                if name in self.wanted:
                    found[name] = obj.path_id
        return found

    def test_the_24_other_names_are_in_the_global_shader_set_and_the_four_are_not(self):
        found = self.names_in("globalgamemanagers.assets")
        self.assertEqual(set(found), self.wanted - set(SCENE_ONLY))
        self.assertEqual(len(found), 24)

    def test_the_four_scene_only_shaders_sit_in_their_scene_shared_assets(self):
        for name, file in SCENE_ONLY.items():
            with self.subTest(shader=name):
                self.assertEqual(set(self.names_in(file)) & set(SCENE_ONLY), {n for n, f in SCENE_ONLY.items() if f == file})


class SceneDependencyTests(unittest.TestCase):
    """The donor-rig scene (Reserve_Base_Scripts, level139) references the Customs shader files; nothing a ported raid loads references 216."""

    @classmethod
    def setUpClass(cls):
        if not (EFT_DATA / "level139").is_file():
            raise unittest.SkipTest("EscapeFromTarkov_Data not present")
        import UnityPy
        cls.UnityPy = UnityPy

    def externals(self, file):
        env = self.UnityPy.load(str(EFT_DATA / file))
        for f in env.files.values():
            ex = getattr(f, "externals", None)
            if ex:
                return {(getattr(e, "path", None) or getattr(e, "name", None) or str(e)).replace("\\", "/").split("/")[-1] for e in ex}
            break
        return set()

    def test_the_donor_rig_scene_references_the_cloth_and_rock_files_but_not_the_street_file(self):
        ext = self.externals("level139")
        self.assertIn("sharedassets5.assets", ext)
        self.assertIn("sharedassets15.assets", ext)
        self.assertNotIn("sharedassets216.assets", ext)

    def test_the_menu_scene_does_not_reference_the_street_file(self):
        self.assertNotIn("sharedassets216.assets", self.externals("level48"))


class LightComponentCensusTests(unittest.TestCase):
    """Component classes no exporter covers, counted in Arena's own level files (PLAN-AUTOSERVICE risk 12, measured 2026-11-11)."""

    @classmethod
    def setUpClass(cls):
        if not (ARENA_DATA / "level66").is_file():
            raise unittest.SkipTest("the Arena data folder is not present")
        import UnityPy
        from extract_bay5_audio import script
        counts = {}
        for index in (62, 65, 66, 136):
            env = UnityPy.load(str(ARENA_DATA / f"level{index}"), str(ARENA_DATA / "globalgamemanagers.assets"))
            level = env.files[str(ARENA_DATA / f"level{index}")]
            c = Counter()
            for obj in level.objects.values():
                if obj.type.name == "MonoBehaviour":
                    try:
                        c[script(obj)] += 1
                    except Exception:
                        pass
            counts[index] = c
        cls.counts = counts

    def total(self, name):
        return sum(c[name] for c in self.counts.values())

    def test_uncovered_classes_match_the_survey(self):
        self.assertEqual(self.total("CullingAdvancedLightObject"), 17)
        self.assertEqual(self.total("AreaLight"), 29)
        self.assertEqual(self.total("StencilShadow"), 6)
        self.assertEqual(self.total("AnalyticSource"), 2)
        self.assertEqual(self.total("PerfectCullingLightGroupPreProcess"), 1)

    def test_covered_classes_match_the_package(self):
        self.assertEqual(self.total("CullingLightObject"), 184)
        self.assertEqual(self.total("VolumetricLight"), 78)
        self.assertEqual(self.total("FlareLight"), 21)
        self.assertEqual(self.counts[66]["FlareLight"], 21)
        self.assertEqual(self.total("LightFlicker"), 0)


if __name__ == "__main__":
    unittest.main()
