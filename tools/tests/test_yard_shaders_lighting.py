"""Yard's shader, lighting-default and flare decisions (PLAN-YARD item 13, 2026-11-20; record docs/progress/yard-release.md "Item 13").

Yard (Arena's Arena_Yard, key arena_yard, "Arena: Block") is registered since plan item 15 (2026-11-21). This file pins what item 13 decided offline, in four groups,
each skipped when its input is absent (override the work folder with ARENAMAPPORT_WORK_DIR, the EFT data with ARENAMAPPORT_EFT_DATA, the Arena data with
ARENAMAPPORT_ARENA_DATA):

  * source pins (repository only): the Yard rows of ShaderStandinPolicy, MapLightingDefaults and BloomTuning, and that Yard is in NO flare list and no
    release list yet (the lock-step promotion is item 15, so every one of these is expected to flip there);
  * the package census (work/extract/yard-native-v2): the shader names, the four names new to the accepted packages, the seven SMap Baked materials, who
    uses them, the emissive census next to the six released maps' maxima, and the light census (102 lights, the 27 STREET_FAKE_GI fills);
  * EFT's own data (read-only): StandardCrossfade is in no EFT file, the octahedron impostor shader is in sharedassets5;
  * Arena's source (read-only): the flare census of the Light scene, run through extract_multiflare with a scratch table row (the tracked table has none).

What this cannot show (inferred, host work): whether the substitutes look right, the ZeroLevel, the night look with the fake-GI fills, the bloom.
"""
import glob
import importlib.util
import json
import os
import re
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
EFT_DATA = Path(os.environ.get("ARENAMAPPORT_EFT_DATA", "D:/SPT41/EscapeFromTarkov_Data"))
ARENA_DATA = Path(os.environ.get("ARENAMAPPORT_ARENA_DATA", "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"))
PACKAGE = WORK / "extract" / "yard-native-v2"
HAVE_PACKAGE = (PACKAGE / "scenes" / "Arena_Yard.json").is_file()
CLIENT = REPO / "ArenaMapPort.Client"
sys.path.insert(0, str(REPO / "tools" / "validate"))
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "analysis" / "scripts"))


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def strip_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def method_body(text: str, header: str) -> str:
    m = re.search(header, text)
    assert m, header
    end = text.find("\n        }", m.end())
    nxt = text.find(";\n", m.end())
    return text[m.start(): (nxt if nxt != -1 and (end == -1 or nxt < end) else end)]


# The decided Yard allow-list: AutoService's four plus the two names EFT 0.16.9.5 carries in no file. Item 15 writes it into the descriptor and the sidecar.
YARD_FALLBACK_NAMES = [
    "Custom/Billboard_FogSheet_Simple", "Cloth/ClothShader", "Cloth/ClothShader_backface", "p0/Reflective/Bumped Specular SMap Baked",
    "Nature/SpeedTree", "Impostors/Examples/StandardCrossfade",
]
NEW_NAMES = {"Impostors/Examples/StandardCrossfade", "Hidden/Amplify Impostors/Octahedron Impostor", "Custom/WireNetting", "Nature/SpeedTree"}
BAKED = "p0/Reflective/Bumped Specular SMap Baked"


class SourcePinTests(unittest.TestCase):
    def setUp(self):
        self.policy = strip_comments(read(CLIENT / "ShaderStandinPolicy.cs"))
        self.lighting = strip_comments(read(CLIENT / "MapLightingDefaults.cs"))
        self.bloom = strip_comments(read(CLIENT / "BloomTuning.cs"))
        self.standins = strip_comments(read(CLIENT / "ShaderStandins.cs"))
        self.descriptor = read(REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs")

    def test_the_shipped_table_still_has_exactly_the_six_bay5_rows(self):
        table = re.search(r"static readonly Dictionary<string, string> Table = new Dictionary<string, string>\s*\{(.*?)\n        \};", self.policy, re.S).group(1)
        self.assertEqual(len(re.findall(r'\]\s*=\s*"', table)), 6)
        self.assertNotIn("SpeedTree", table)
        self.assertNotIn("StandardCrossfade", table)

    def test_yards_targets_and_switch_are_wired_into_the_policy(self):
        targets = re.search(r"static readonly Dictionary<string, string> YardTargets = new Dictionary<string, string>\s*\{(.*?)\n        \};", self.policy, re.S).group(1)
        rows = dict(re.findall(r"\[(\w+)\]\s*=\s*(\w+)", targets))
        self.assertEqual(rows, {"ClothBackfaceShader": "TwoSidedClothShader", "SpeedTreeShader": "TwoSidedClothShader", "StandardCrossfadeShader": "DiffuseShader"})
        self.assertIn('SpeedTreeShader = "Nature/SpeedTree"', self.policy)
        self.assertIn('StandardCrossfadeShader = "Impostors/Examples/StandardCrossfade"', self.policy)
        self.assertIn('DiffuseShader = "Diffuse"', self.policy)
        self.assertIn("IsYard(mapKey)", method_body(self.policy, r"internal static bool FallbacksActive\("))   # also what test_new_map_checklist.py reads
        self.assertIn("IsYard(mapKey) && YardTargets.TryGetValue", self.policy)
        self.assertIn("IsYard(mapKey)", method_body(self.policy, r"internal static bool UsesExtendedStandins\("))
        # FixFor is untouched: its switch names the cloth and rock names only, so the two extra names get no fix
        fix = method_body(self.policy, r"internal static StandinFix FixFor\(")
        self.assertNotIn("SpeedTree", fix)
        self.assertNotIn("StandardCrossfade", fix)

    def test_the_yard_switch_is_bound_since_the_promotion(self):
        # PLAN-YARD item 15: a developer key MaterialFixes/YardShaderStandins (default ShaderStandinPolicy.YardStandinsDefault), read by ShaderRebind's call site.
        self.assertRegex(self.standins, r'settings\.Bind\(\s*"MaterialFixes",\s*"YardShaderStandins",\s*ShaderStandinPolicy\.YardStandinsDefault')
        self.assertIn("_yardStandins != null ? _yardStandins.Value : ShaderStandinPolicy.YardStandinsDefault", self.standins)
        self.assertIn("ShaderStandins.YardStandinsEnabled", strip_comments(read(CLIENT / "ShaderRebind.cs")))

    def test_yards_lighting_row_exists_and_is_a_reserve_rig_with_a_high_zero_level(self):
        rig = self.lighting[self.lighting.index("internal static RigValues Rig(string mapKey)"):self.lighting.index("internal static bool BindsAuthoredSunKeys")]
        self.assertIn("bool yard = mapKey == ArenaMapRegistry.Yard.Key;", rig)
        self.assertRegex(re.search(r"bool rig = ([^;]*);", rig).group(1), r"\byard\b")
        zero = re.search(r"ZeroLevel = ([^;]*);", rig).group(1)
        self.assertIn("yard ? 1.7f", zero)
        self.assertIn("bay5 || autoService ? -8.2f : iceberg ? -3.7f : (equator || airPit || bowl) ? -4.4f", zero)   # the six released values did not move

    def test_yards_bloom_pair_is_the_donor_pair_after_the_released_lines(self):
        # the two released lines are byte-identical (EmissionCapSourcePinTests pins them); Yard's override follows them
        self.assertIn("float defaultThreshold = donorDefaults || iceberg ? DefaultThreshold : airPit ? AirPitThreshold : -1f;", self.bloom)
        self.assertIn("if (yard) { defaultThreshold = DefaultThreshold; defaultIntensity = DefaultIntensity; }", self.bloom)
        self.assertIn("const float DefaultThreshold = 2.1f;", self.bloom)
        self.assertIn("const float DefaultIntensity = 0.35f;", self.bloom)
        self.assertNotIn("Migrate", self.bloom.split("if (yard)")[1].split("\n")[0])   # a Catalog-only map has no saved keys to migrate

    def test_yard_is_released_and_has_no_emission_cap(self):
        all_block = re.search(r"public static readonly IReadOnlyList<ArenaMapDescriptor> All = new\[\] \{([^}]*)\}", self.descriptor).group(1)
        self.assertIn("Yard", all_block)                        # promoted by item 15
        sidecar = json.loads((REPO / "tools" / "maps" / "yard.json").read_text(encoding="utf-8"))
        self.assertIn(sidecar.get("emission_cap", 0), (0, 0.0))
        self.assertNotIn("emission_floor", sidecar)
        names = sidecar.get("shader_fallback_names")
        self.assertEqual(names, YARD_FALLBACK_NAMES)

    def test_yard_has_no_flare_package_and_stays_out_of_every_flare_list(self):
        import extract_multiflare as M
        import validate_distribution as VDIST
        self.assertNotIn("arena_yard", M.SCENES)
        self.assertNotIn("arena_yard", M.EXPECTED_CENSUS)
        self.assertNotIn("arena_yard", VDIST.FLARE_MAP_KEYS)
        foreach = re.search(r"foreach \(var map in new\[\] \{([^}]*)\}", read(CLIENT / "NativeMultiFlare.cs")).group(1)
        self.assertNotIn("Yard", foreach)
        csproj = read(CLIENT / "ArenaMapPort.Client.csproj")
        block = re.search(r'Name="ArenaMapPortStageMultiFlare".*?</Target>', csproj, re.S).group(0)
        self.assertNotIn("arena_yard", block)


@unittest.skipUnless(HAVE_PACKAGE, "needs work/extract/yard-native-v2")
class PackageCensusTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.materials = {}
        for path in glob.glob(str(PACKAGE / "materials" / "*.json")):
            cls.materials[Path(path).stem] = json.loads(Path(path).read_text(encoding="utf-8"))
        cls.users = {}                                           # material id -> [(scene, object path, active)]
        cls.lights = []
        for path in sorted(glob.glob(str(PACKAGE / "scenes" / "*.json"))):
            scene = json.loads(Path(path).read_text(encoding="utf-8"))
            objects = {o["id"]: o for o in scene["objects"]}

            def active(oid):
                while oid in objects:
                    if not objects[oid]["active"]:
                        return False
                    oid = objects[oid]["parent_id"]
                return True

            def full(oid):
                names = []
                while oid in objects:
                    names.append(objects[oid]["name"])
                    oid = objects[oid]["parent_id"]
                return "/".join(reversed(names))

            for o in scene["objects"]:
                comps = {c["type"]: c for c in o["components"]}
                for c in o["components"]:
                    for mid in c.get("material_ids") or []:
                        if mid:
                            cls.users.setdefault(mid, []).append((scene["scene_name"], full(o["id"]), active(o["id"]) and c.get("enabled", True)))
                if "Light" in comps:
                    cls.lights.append((scene["scene_name"], full(o["id"]), comps["Light"], comps.get("CullingLightObject")))

    def names(self):
        counts = {}
        for m in self.materials.values():
            counts[m["shader"]] = counts.get(m["shader"], 0) + 1
        return counts

    def test_thirty_shader_names_over_750_materials_and_four_are_new_to_the_accepted_packages(self):
        counts = self.names()
        self.assertEqual((len(self.materials), len(counts)), (750, 30))
        released = set()
        for sidecar in glob.glob(str(REPO / "dist" / "BepInEx" / "plugins" / "maschine-ArenaMapPort" / "material-shaders.*.json")):
            if Path(sidecar).name == "material-shaders.arena_yard.json":
                continue                                          # Yard's own sidecar is staged since its first build (2026-11-21): compare with the OTHER maps
            released |= {v["shader"] for v in json.loads(Path(sidecar).read_text(encoding="utf-8")).values()}
        if released:                                              # a checkout without the staged dist has no sidecars to compare with
            self.assertEqual(set(counts) - released, NEW_NAMES)
        for name in NEW_NAMES:
            self.assertEqual(counts[name], 1, name)
        self.assertEqual(counts[BAKED], 7)

    def test_who_uses_the_new_names_and_the_baked_shader(self):
        by_shader = {}
        for mid, m in self.materials.items():
            for scene, path, active in self.users.get(mid, []):
                by_shader.setdefault(m["shader"], []).append((path, active))
        # the two trees (TreePrefab (2) and (3), inside the yard): leaves and bark are active, the octahedron impostor is not
        leaves = by_shader["Nature/SpeedTree"]
        bark = by_shader["Impostors/Examples/StandardCrossfade"]
        impostor = by_shader["Hidden/Amplify Impostors/Octahedron Impostor"]
        self.assertEqual((len(leaves), sum(a for _, a in leaves)), (2, 2))
        self.assertEqual((len(bark), sum(a for _, a in bark)), (2, 2))
        self.assertTrue(all("SBG_Arena_Yard_Plants/TreePrefab" in p for p, _ in leaves + bark + impostor))
        self.assertEqual((len(impostor), sum(a for _, a in impostor)), (2, 0))
        # WireNetting: three LOD renderers of one scaffold (lesa_10m (1)), only LOD2 active, background right; it resolves from EFT's shader bundle
        wire = by_shader["Custom/WireNetting"]
        self.assertEqual((len(wire), sum(a for _, a in wire)), (3, 1))
        self.assertTrue(all("BACKGROUND/Back_right/lesa_10m (1)" in p for p, _ in wire))
        # SMap Baked: 7 materials, 28 renderers, the LODLAST meshes of the big background buildings (Tetris_*, Razvedchikov_9, Chekannaya_13, ...)
        baked = by_shader[BAKED]
        self.assertEqual(len(baked), 28)
        self.assertTrue(all("LODLAST" in p for p, _ in baked))
        self.assertEqual(sum(a for _, a in baked), 25)            # three inactive (Tetris_I and the two Tochka_Nadezhina wall renderers)

    def test_the_new_names_keep_the_properties_the_decided_substitutes_read(self):
        leaves = next(m for m in self.materials.values() if m["shader"] == "Nature/SpeedTree")
        self.assertEqual(leaves["floats"]["_Cutoff"], 0.5)
        self.assertEqual(leaves["floats"]["_Cull"], 0.0)               # two-sided, so the two-sided cutout is the substitute
        self.assertEqual(leaves["floats"]["_Mode"], 1.0)
        self.assertTrue(leaves["tex_envs"]["_MainTex"]["texture_id"] and leaves["tex_envs"]["_BumpMap"]["texture_id"])
        bark = next(m for m in self.materials.values() if m["shader"] == "Impostors/Examples/StandardCrossfade")
        self.assertNotIn("_Cutoff", bark["floats"])                      # no cutout to misread
        self.assertTrue(bark["tex_envs"]["_MainTex"]["texture_id"])

    def test_the_emissive_census_is_below_every_cap_and_level_with_the_released_maps(self):
        spec = importlib.util.spec_from_file_location("ice_cap_census", REPO / "tools" / "tests" / "test_iceberg_emission_cap.py")
        ice = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(ice)
        rows = ice.census(PACKAGE)
        self.assertEqual(len(rows), 12)
        self.assertAlmostEqual(rows[0][0], 3.932, places=3)
        self.assertEqual(rows[0][2], "Projector_portable_Albedo_emissive")
        self.assertEqual([round(r[0], 2) for r in rows[1:4]], [2.97, 2.58, 2.33])
        self.assertEqual(sum(1 for r in rows if r[0] > 4.6), 0)         # Iceberg's cap would take nothing down
        self.assertEqual(sum(1 for r in rows if r[0] > 3.0), 1)
        released = {"equator": "equator-native-v5", "bay5": "bay5-native-v5", "airpit": "airpit-native-v10", "bowl": "bowl-native-v2",
                    "autoservice": "autoservice-native-v2"}
        for stem, package in released.items():
            if not (WORK / "extract" / package / "scenes").is_dir():
                continue
            top = ice.census(WORK / "extract" / package)[0][0]
            self.assertLess(top, 4.2, stem)                             # the four maps without a cap peak at 3.6 to 4.1, like Yard's 3.93
        self.assertLess(rows[0][0], 4.2)

    def test_the_light_census(self):
        lights = self.lights
        self.assertEqual(len(lights), 102)
        self.assertEqual({s for s, _, _, _ in lights}, {"Arena_Yard_Light", "Arena_Yard_TeamFight_DesignStuff"})
        self.assertEqual(sum(1 for _, _, l, _ in lights if l["light_type"] == "Point"), 66)
        self.assertEqual(sum(1 for _, _, l, _ in lights if l["light_type"] == "Spot"), 36)
        self.assertEqual(sum(1 for _, _, l, _ in lights if l["light_type"] not in ("Point", "Spot")), 0)     # no Directional light
        self.assertEqual(sum(1 for _, _, _, c in lights if c), 96)       # six lights carry no CullingLightObject
        fills = [(p, l, c) for _, p, l, c in lights if "STREET_FAKE_GI" in p]
        self.assertEqual(len(fills), 27)
        self.assertEqual(sum(1 for _, l, _ in fills if l["light_type"] == "Spot"), 7)
        self.assertTrue(all(c for _, _, c in fills))
        self.assertEqual((round(min(l["range"] for _, l, _ in fills), 1), round(max(l["range"] for _, l, _ in fills), 1)), (18.9, 77.6))
        # warm: red above green above blue on every fill
        self.assertTrue(all(l["color"][0] > l["color"][1] > l["color"][2] for _, l, _ in fills))
        # the effective intensity is the CullingLightObject's maximum (the Light's own value is the culled 0 on most of them)
        effective = sorted(c["max_light_intensity"] if c["use_light_intensity_from_editor"] else l["intensity"] for _, l, c in fills)
        self.assertEqual(effective[-1], 8.0)
        self.assertAlmostEqual(sum(effective), 49.1, delta=0.3)
        # the ported lights keep the scene's own shadow flag (importer: shadow_type); 100 of 102 have none in the scene, 2 are Soft
        self.assertEqual(sorted({l["shadow_type"] for _, _, l, _ in lights}), ["None", "Soft"])

    def test_every_light_class_the_importer_does_not_port_is_absent_from_the_package(self):
        types = set()
        for path in glob.glob(str(PACKAGE / "scenes" / "*.json")):
            for o in json.loads(Path(path).read_text(encoding="utf-8"))["objects"]:
                types |= {c["type"] for c in o["components"]}
        self.assertTrue({"AreaLight", "CullingAdvancedLightObject", "AnalyticSource", "StencilShadow", "FlareLight", "MultiFlare"}.isdisjoint(types))


@unittest.skipUnless((EFT_DATA / "globalgamemanagers.assets").is_file() and (EFT_DATA / "sharedassets5.assets").is_file(), "needs the EFT data folder")
class EftShaderCarriageTests(unittest.TestCase):
    """Byte searches (inferred: a name can also sit in a registry table, so these pin absence and one presence, never a loaded shader). The full scan
    (UnityPy over all 1,120 data files and the global shader bundle, reading every Shader's parsed name) found: StandardCrossfade and Nature/SpeedTree
    in none, the octahedron impostor in sharedassets5 only, SMap Baked in sharedassets216 only, WireNetting in globalgamemanagers.assets and the bundle."""

    @staticmethod
    def count(relative: str, needle: bytes) -> int:
        import mmap
        path = EFT_DATA / relative
        if not path.is_file():
            raise unittest.SkipTest(relative)
        with open(path, "rb") as handle, mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ) as view:
            total, at = 0, view.find(needle)
            while at != -1:
                total += 1
                at = view.find(needle, at + 1)
            return total

    def test_standardcrossfade_is_in_no_eft_file(self):
        for relative in ("globalgamemanagers", "globalgamemanagers.assets", "StreamingAssets/Windows/shaders", "sharedassets3.assets",
                         "sharedassets5.assets", "sharedassets13.assets", "sharedassets15.assets", "sharedassets216.assets"):
            self.assertEqual(self.count(relative, b"StandardCrossfade"), 0, relative)

    def test_the_octahedron_impostor_shader_is_in_sharedassets5_which_the_donor_scripts_scene_lists(self):
        self.assertGreaterEqual(self.count("sharedassets5.assets", b"Hidden/Amplify Impostors/Octahedron Impostor"), 1)
        self.assertEqual(self.count("StreamingAssets/Windows/shaders", b"Hidden/Amplify Impostors/Octahedron Impostor"), 0)

    def test_wirenetting_is_in_the_global_shader_bundle(self):
        self.assertGreaterEqual(self.count("StreamingAssets/Windows/shaders", b"Custom/WireNetting"), 1)


@unittest.skipUnless((ARENA_DATA / "level74").is_file(), "needs the installed Arena")
class FlareCensusTests(unittest.TestCase):
    def test_yards_light_scene_has_fourteen_flare_lights_and_none_is_active(self):
        import extract_multiflare as M

        class Capture:
            seen = None

            def __getitem__(self, index):
                return Capture.seen[index]

            def __eq__(self, other):
                Capture.seen = other
                return True

        scenes = {72: "Arena_Yard", 73: "Arena_Yard_DesignStuff", 74: "Arena_Yard_Light", 75: "Arena_Yard_Scripts", 162: "Arena_Yard_Grass",
                  310: "Arena_Yard_TeamFight", 317: "Arena_Yard_TeamFight_DesignStuff"}
        with tempfile.TemporaryDirectory() as out, mock.patch.dict(M.SCENES, {"arena_yard": scenes}), \
                mock.patch.dict(M.EXPECTED_CENSUS, {"arena_yard": Capture()}):
            try:
                package = M.export(M.SOURCE, Path(out), "arena_yard")
            except ValueError as error:
                self.skipTest(f"the installed Arena differs from the measured 0.6.2 build: {error}")
        # (source lights, active lights, flare elements, active elements): the exporter's rule counts none of them, so there is no package to ship
        self.assertEqual(tuple(Capture.seen), (14, 0, 67, 0))
        self.assertEqual({light["scene"] for light in package["lights"]}, {"Arena_Yard_Light"})
        self.assertEqual(len(package["textures"]), 2)
        self.assertFalse(any(M.light_is_active(light) for light in package["lights"]))


if __name__ == "__main__":
    unittest.main()
