"""Iceberg's per-map emission cap (ArenaMapPort.Client/EmissionCapModel.cs + EmissionCapRepairs.cs, docs/progress/iceberg-release.md
"Per-map emission cap"): the sidecar/descriptor pair, the validator that holds them equal, the rule table the C# model implements, and the
census of the package materials that the cap takes down. Four groups, each skipped when its input is absent:

  * constants and code pins (repository only): tools/maps/iceberg.json carries emission_cap 4.0 and no released map has the key, the C# descriptor
    says the same, the validator accepts the pair and rejects a drift on either side or a malformed value, and the rule lists this test reads are
    the ones EmissionCapModel.cs compiles;
  * the extracted packages (override the work directory with ARENAMAPPORT_WORK_DIR when the tests run from a git worktree without work/): Iceberg has
    exactly the six materials above the cap of 4.0 and none of the released maps' strongest emissive materials is touched by it;
  * EFT's own shaders bundle (read-only, UnityPy; ARENAMAPPORT_EFT_DATA overrides D:/SPT41/EscapeFromTarkov_Data): the properties the emissive shaders
    declare, which is where the rule table comes from.

The level of a material is what EmissionCapModel computes: max RGB of _EmissionColor for a Standard-family material with the _EMISSION keyword (or a
p0 emissive shader that declares only _EmissionColor), and _EmissionPower x _EmissionVisibility x max RGB of _EmissiveColor (when the shader declares
it) for a p0 emissive shader that declares _EmissionPower.
"""
import glob
import json
import math
import os
import re
import sys
import unittest
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
EFT_DATA = Path(os.environ.get("ARENAMAPPORT_EFT_DATA", "D:/SPT41/EscapeFromTarkov_Data"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import validate_map_descriptors as VD  # noqa: E402

MODEL_CS = REPO / "ArenaMapPort.Client" / "EmissionCapModel.cs"
DESCRIPTOR_CS = REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs"
MAPS = REPO / "tools" / "maps"

CAP = 4.0
FLOOR = 3.0            # emission_floor (2026-11-01, "Emission floor")
LIFT_MINIMUM = 0.25    # EmissionCapModel.LiftMinimum: weaker materials are glass tints and dark light bars, never lifted
RELEASED = {"equator": "equator-native-v5", "bay5": "bay5-native-v5", "airpit": "airpit-native-v9", "bowl": "bowl-native-v2"}
ICEBERG_PACKAGE = WORK / "extract" / "iceberg-native-v2"
HAVE_ICEBERG = (ICEBERG_PACKAGE / "scenes" / "Arena_Sanatorium.json").is_file()

# The rule table of EmissionCapModel.Classify, spelled the way the package materials spell it. Shaders that declare _EmissionPower / _EmissiveColor
# in EFT's bundle (TestEftShaders reads them from the bundle itself).
STANDARD_SHADERS = ("Standard", "Standard (Specular setup)", "Standard (Roughness setup)")
HAS_POWER = {"p0/Reflective/Bumped Emissive Specular SMap", "p0/Reflective/Bumped Emissive Specular",
             "p0/Reflective/Bumped Animated Emissive Specular SMap"}
HAS_EMISSIVE_COLOR = {"p0/Reflective/Bumped Emissive Specular SMap"}


def level(material: dict):
    """(family, level) of one package material, or None when the cap does not know it."""
    shader = material.get("shader", "")
    colors, floats = material.get("colors") or {}, material.get("floats") or {}
    if shader in STANDARD_SHADERS:
        color = colors.get("_EmissionColor")
        if "_EMISSION" not in (material.get("keywords") or []) or not color:
            return None
        return "Color", max(0.0, max(color[:3]))
    if not shader.startswith("p0/") or "Emissive" not in shader:
        return None
    if shader in HAS_POWER:
        power, visibility = floats.get("_EmissionPower", 1.0), floats.get("_EmissionVisibility", 1.0)
        peak = max(colors["_EmissiveColor"][:3]) if shader in HAS_EMISSIVE_COLOR and colors.get("_EmissiveColor") else 1.0
        if power <= 0 or visibility <= 0 or peak <= 0:
            return "Power", 0.0
        return "Power", power * visibility * peak
    color = colors.get("_EmissionColor")
    return ("Color", max(0.0, max(color[:3]))) if color else None


def census(package: Path) -> list:
    """Every emissive material that a MeshRenderer of the package uses: (level, family, source_name, shader, renderer uses, id)."""
    uses: dict = {}
    for path in glob.glob(str(package / "scenes" / "*.json")):
        scene = json.loads(Path(path).read_text(encoding="utf-8"))
        for obj in scene["objects"]:
            for component in obj["components"]:
                if component["type"] == "MeshRenderer":
                    for material_id in component.get("material_ids") or []:
                        if material_id:
                            uses[material_id] = uses.get(material_id, 0) + 1
    rows = []
    for path in glob.glob(str(package / "materials" / "*.json")):
        material_id = Path(path).stem
        if material_id not in uses:
            continue
        material = json.loads(Path(path).read_text(encoding="utf-8"))
        found = level(material)
        if found is not None:
            rows.append((found[1], found[0], material.get("source_name"), material.get("shader"), uses[material_id], material_id))
    rows.sort(reverse=True)
    return rows


class SidecarAndDescriptorTests(unittest.TestCase):
    def test_iceberg_carries_the_cap_and_no_released_map_has_the_key(self):
        iceberg = json.loads((MAPS / "iceberg.json").read_text(encoding="utf-8"))
        self.assertEqual(iceberg["emission_cap"], CAP)
        for stem in RELEASED:
            self.assertNotIn("emission_cap", json.loads((MAPS / f"{stem}.json").read_text(encoding="utf-8")), stem)

    def test_the_descriptor_says_the_same(self):
        text = DESCRIPTOR_CS.read_text(encoding="utf-8")
        self.assertEqual(len(re.findall(r"\bEmissionCap:", text)), 1)
        entry = text[text.index("public static readonly ArenaMapDescriptor Iceberg"):text.index("public static readonly ArenaMapDescriptor Factory")]
        self.assertIn("EmissionCap: 4.0f,", entry)
        self.assertIn("EmissionFloor: 3.0f);", entry)
        self.assertIn("public float EmissionCap { get; }", text)
        self.assertIn("public float EmissionFloor { get; }", text)
        self.assertRegex(text, r"float EmissionCap = 0f[,)]")
        self.assertRegex(text, r"float EmissionFloor = 0f[,)]")
        self.assertEqual(len(re.findall(r"\bEmissionFloor:", text)), 1)

    def test_iceberg_carries_the_floor_inside_its_band_and_no_released_map_has_the_key(self):
        iceberg = json.loads((MAPS / "iceberg.json").read_text(encoding="utf-8"))
        self.assertEqual(iceberg["emission_floor"], FLOOR)
        self.assertLessEqual(iceberg["emission_floor"], iceberg["emission_cap"])
        for stem in RELEASED:
            self.assertNotIn("emission_floor", json.loads((MAPS / f"{stem}.json").read_text(encoding="utf-8")), stem)

    def test_the_validator_rejects_a_floor_drift_or_a_malformed_floor(self):
        original_load = VD.load_map
        for file, value in (("iceberg.json", 2.0), ("iceberg.json", 0), ("bowl.json", 3.0)):
            def read_map(path, file=file, value=value):
                data = original_load(path)
                if path.name == file:
                    data["emission_floor"] = value
                return data

            output = StringIO()
            with self.subTest(file=file, value=value), patch.object(sys, "argv", ["validate_map_descriptors.py"]), \
                    patch.object(VD, "load_map", side_effect=read_map), redirect_stdout(output), redirect_stderr(output):
                self.assertEqual(VD.main(), 1)
                self.assertIn("emission_floor mismatch", output.getvalue())
        for bad in (-1, 100.5, "3", None, True, float("nan")):
            self.assertEqual(len(VD.emission_cap_errors({"emission_floor": bad})), 1, repr(bad))
        self.assertEqual(VD.emission_cap_errors({"emission_cap": 4.0, "emission_floor": 3.0}), [])

    def test_the_lift_rules_here_are_the_ones_the_model_compiles(self):
        text = MODEL_CS.read_text(encoding="utf-8")
        self.assertIn(f"const float LiftMinimum = {LIFT_MINIMUM}f;", text)
        self.assertIn('shaderName.IndexOf("Animated", StringComparison.Ordinal) < 0', text)

    def test_the_validator_accepts_the_repository(self):
        output = StringIO()
        with patch.object(sys, "argv", ["validate_map_descriptors.py"]), redirect_stdout(output), redirect_stderr(output):
            self.assertEqual(VD.main(), 0, output.getvalue())

    def test_the_validator_rejects_a_drift_on_either_side(self):
        original_load = VD.load_map
        for file, value in (("iceberg.json", 3.0), ("iceberg.json", 0), ("equator.json", 4.0)):
            def read_map(path, file=file, value=value):
                data = original_load(path)
                if path.name == file:
                    data["emission_cap"] = value
                return data

            output = StringIO()
            with self.subTest(file=file, value=value), patch.object(sys, "argv", ["validate_map_descriptors.py"]), \
                    patch.object(VD, "load_map", side_effect=read_map), redirect_stdout(output), redirect_stderr(output):
                self.assertEqual(VD.main(), 1)
                self.assertIn("emission_cap mismatch", output.getvalue())

    def test_the_validator_rejects_a_malformed_cap(self):
        self.assertEqual(VD.emission_cap_errors({}), [])
        for good in (0, 0.0, 4, 4.0, 100, 100.0, 0.5):
            self.assertEqual(VD.emission_cap_errors({"emission_cap": good}), [], good)
        for bad in (-1, -0.001, 100.5, 1000, "4", None, True, False, float("nan"), float("inf"), [4.0], {"v": 4}):
            self.assertEqual(len(VD.emission_cap_errors({"emission_cap": bad})), 1, repr(bad))

    def test_the_rule_lists_here_are_the_ones_the_model_compiles(self):
        text = MODEL_CS.read_text(encoding="utf-8")
        listed = re.search(r"StandardShaders = \{([^}]*)\}", text).group(1)
        self.assertEqual(tuple(re.findall(r'"([^"]+)"', listed)), STANDARD_SHADERS)
        self.assertIn('shaderName.StartsWith("p0/", StringComparison.Ordinal)', text)
        self.assertIn('shaderName.IndexOf("Emissive", StringComparison.Ordinal)', text)
        for name, value in (("EmissionColorProperty", "_EmissionColor"), ("EmissionPowerProperty", "_EmissionPower"),
                            ("EmissionVisibilityProperty", "_EmissionVisibility"), ("EmissiveColorProperty", "_EmissiveColor"),
                            ("EmissionKeyword", "_EMISSION")):
            self.assertIn(f'const string {name} = "{value}";', text)
        self.assertIn("MaxCap = 100f;", text)
        self.assertEqual(VD.EMISSION_CAP_MAX, 100.0)

    def test_level_formulas_match_the_documented_examples(self):
        crystal = {"shader": "Standard", "keywords": ["_EMISSION"], "colors": {"_EmissionColor": [67.79352569580078, 17.392053604125977, 0.0, 1.0]}}
        self.assertEqual(level(crystal), ("Color", 67.79352569580078))
        chandelier = {"shader": "p0/Reflective/Bumped Emissive Specular SMap", "colors": {"_EmissiveColor": [0.68, 0.58, 0.10, 1.0]},
                      "floats": {"_EmissionPower": 10.0, "_EmissionVisibility": 1.0}}
        self.assertAlmostEqual(level(chandelier)[1], 6.8)
        self.assertEqual(level(dict(crystal, keywords=[])), None)                            # no _EMISSION keyword: the colour is not read
        self.assertEqual(level({"shader": "p0/Reflective/Bumped Specular SMap", "floats": {"_EmissionPower": 10.0}}), None)
        self.assertEqual(level({"shader": "p0/Cutout/Bumped Diffuse Emissive", "colors": {"_EmissionColor": [2.0, 1.0, 0.5, 1.0]}}), ("Color", 2.0))
        no_color = dict(chandelier, shader="p0/Reflective/Bumped Emissive Specular")          # this shader declares no _EmissiveColor: the leftover is ignored
        self.assertAlmostEqual(level(no_color)[1], 10.0)


@unittest.skipUnless(HAVE_ICEBERG, "needs work/extract/iceberg-native-v2 (ARENAMAPPORT_WORK_DIR)")
class IcebergPackageTests(unittest.TestCase):
    _rows = None

    @classmethod
    def rows(cls):
        if cls._rows is None:
            cls._rows = census(ICEBERG_PACKAGE)
        return cls._rows

    def test_exactly_six_materials_exceed_the_cap(self):
        over = [(name, family, value) for value, family, name, _, _, _ in self.rows() if value > CAP * (1 + 1e-5)]
        expected = [("Emission_Yellow", "Color", 67.794), ("Prison_Emission_red", "Power", 10.0), ("Prison_Emission_passive", "Power", 7.736),
                    ("Sanatorium_Emissive_Orange", "Power", 6.7925), ("Prison_Emission_blue", "Power", 6.226), ("Plafond_lamp_02_source_E_red", "Power", 4.277)]
        self.assertEqual([(name, family) for name, family, _ in over], [(name, family) for name, family, _ in expected])   # descending by level
        for (name, _, value), (_, _, want) in zip(over, expected):
            self.assertAlmostEqual(value, want, delta=0.001, msg=name)

    def test_the_two_materials_of_the_findings_are_the_ones_the_hosts_user_saw(self):
        by_name = {name: (value, family, shader, uses, material_id) for value, family, name, shader, uses, material_id in self.rows()}
        value, family, shader, uses, material_id = by_name["Emission_Yellow"]
        self.assertEqual((family, shader, material_id), ("Color", "Standard", "367e4a61931e682b"))
        self.assertAlmostEqual(value, 67.794, places=3)
        self.assertEqual(uses, 4)                                                              # Crystal_Lamp_1 and Crystal_Lamp_1 (1), LOD0 and one more slot
        value, family, shader, uses, material_id = by_name["Sanatorium_Emissive_Orange"]
        self.assertEqual((family, shader, material_id), ("Power", "p0/Reflective/Bumped Emissive Specular SMap", "d52be16e9d5d8091"))
        self.assertAlmostEqual(value, 6.7925, places=3)                                        # 10 x 1.0 x the red of _EmissiveColor (about 0.68)
        self.assertEqual(uses, 165)                                                            # the two chandeliers and 157 floor spot lamps among them

    def test_the_census_totals(self):
        rows = self.rows()
        self.assertEqual(len(rows), 37)
        self.assertEqual(sum(1 for row in rows if row[0] > 1.0), 18)
        self.assertEqual({family: sum(1 for row in rows if row[1] == family) for family in ("Color", "Power")}, {"Color": 14, "Power": 23})
        shaders = {}
        for row in rows:
            shaders[row[3]] = shaders.get(row[3], 0) + 1
        self.assertEqual(shaders, {"p0/Reflective/Bumped Emissive Specular SMap": 22, "Standard": 12, "p0/Cutout/Bumped Diffuse Emissive": 2,
                                   "p0/Reflective/Bumped Animated Emissive Specular SMap": 1})

    def test_the_floor_lifts_twenty_three_lamps_and_leaves_the_screen_the_tints_and_the_dark_bars(self):
        lifted = sorted(name for value, _, name, shader, _, _ in self.rows()
                        if LIFT_MINIMUM <= value < FLOOR * (1 - 1e-5) and "Animated" not in shader)
        self.assertEqual(len(lifted), 23)
        self.assertIn("Armstrong_lamp_03_A_source_E", lifted)                                 # 0.60, 64 renderers
        self.assertIn("Lift_emissive_draft", lifted)                                          # 0.29, the weakest lamp that is lifted
        untouched = sorted(name for value, _, name, shader, _, _ in self.rows() if value < LIFT_MINIMUM or "Animated" in shader)
        self.assertEqual(untouched, sorted(["lab_glow", "Sanatorium_Metal_ceiling_light", "Airpit_lamp_emissive_grey", "Glass_broken_new",
                                            "Sanatorium_Glass_broken_new", "Split_system_diffuse", "Concrete_Clean", "tile_floor_diffuse"]))

    def test_every_other_lamp_above_two_is_left_alone_by_the_cap(self):
        rest = {name: value for value, _, name, _, _, _ in self.rows() if 2.0 < value <= CAP * (1 + 1e-5)}
        expected = {"Searchlight_03_source_E": 2.925, "Sanatorium_Emissive_white": 2.83, "shopping_mall_ceiling_light_emission": 2.333,
                    "Spot_lamp_01_source_E": 2.333, "Floor_lamp_01-03_source_E": 2.31, "Preset_refill_container_emission_green": 2.119}
        self.assertEqual(set(rest), set(expected))
        for name, value in expected.items():
            self.assertAlmostEqual(rest[name], value, delta=0.002, msg=name)


@unittest.skipUnless(all((WORK / "extract" / package / "scenes").is_dir() for package in RELEASED.values()),
                     "needs the four released maps' packages under work/extract (ARENAMAPPORT_WORK_DIR)")
class ReleasedMapsTests(unittest.TestCase):
    def test_the_released_maps_strongest_emissive_materials_sit_at_the_cap_and_the_repair_stays_off(self):
        strongest = {}
        for stem, package in RELEASED.items():
            rows = census(WORK / "extract" / package)
            strongest[stem] = (rows[0][0], rows[0][2])
        # 4.13 (Bay5 '02 - Default', Standard), 3.93 (Equator and Bowl, Projector_portable_Albedo_emissive) and 3.62 (Air Pit): the level the cap of 4.0 borrows
        expected = {"equator": (3.932, "Projector_portable_Albedo_emissive"), "bay5": (4.128, "02 - Default"),
                    "airpit": (3.62, "Airpit_emissive_02"), "bowl": (3.932, "Projector_portable_Albedo_emissive")}
        self.assertEqual({stem: name for stem, (_, name) in strongest.items()}, {stem: name for stem, (_, name) in expected.items()})
        for stem, (value, _) in strongest.items():
            self.assertAlmostEqual(value, expected[stem][0], delta=0.002, msg=stem)
        self.assertLessEqual(max(value for value, _ in strongest.values()), 4.13)
        # only one material of the four released maps is above the cap at all, and the maps carry no cap
        over = [(stem, row[2]) for stem, package in RELEASED.items() for row in census(WORK / "extract" / package) if row[0] > CAP * (1 + 1e-5)]
        self.assertEqual(over, [("bay5", "02 - Default")])
        for stem in RELEASED:
            self.assertNotIn("emission_cap", json.loads((MAPS / f"{stem}.json").read_text(encoding="utf-8")))


def eft_shader_properties():
    """{shader name: {property: (type, flags, default, min, max)}} for the emissive shaders and Standard in EFT's own shaders bundle."""
    import UnityPy
    environment = UnityPy.load(str(EFT_DATA / "StreamingAssets" / "Windows" / "shaders"))
    found = {}
    for obj in environment.objects:
        if obj.type.name != "Shader":
            continue
        try:
            shader = obj.read()
            name = shader.m_ParsedForm.m_Name
        except Exception:
            continue
        if not (name.startswith("p0/") and "Emissive" in name) and name not in STANDARD_SHADERS:
            continue
        properties = {}
        for prop in shader.m_ParsedForm.m_PropInfo.m_Props:
            properties[prop.m_Name] = (prop.m_Type, prop.m_Flags, prop.m_DefValue_0_, prop.m_DefValue_1_, prop.m_DefValue_2_)
        found[name] = properties
    return found


try:
    import UnityPy  # noqa: F401
    HAVE_UNITYPY = True
except ImportError:
    HAVE_UNITYPY = False


@unittest.skipUnless(HAVE_UNITYPY and (EFT_DATA / "StreamingAssets" / "Windows" / "shaders").is_file(),
                     "needs UnityPy and EFT's StreamingAssets/Windows/shaders (ARENAMAPPORT_EFT_DATA)")
class EftShaderFactsTests(unittest.TestCase):
    """Where the rule table of EmissionCapModel comes from: the properties EFT's compiled shaders declare (read 2026-10-31)."""
    COLOR, FLOAT, RANGE, TEXTURE = 0, 2, 3, 4
    HDR = 16
    shaders = None

    @classmethod
    def setUpClass(cls):
        cls.shaders = eft_shader_properties()

    def test_the_power_shaders_declare_power_and_visibility_as_ranges(self):
        for name in HAS_POWER:
            with self.subTest(shader=name):
                props = self.shaders[name]
                self.assertEqual(props["_EmissionPower"][0], self.RANGE)
                self.assertEqual(props["_EmissionPower"][3:], (0.0, 10.0))        # the shader's own maximum is 10
                self.assertEqual(props["_EmissionVisibility"][0], self.RANGE)
                self.assertEqual(props["_EmissionVisibility"][3:], (0.0, 1.0))
                self.assertIn("_EmissionMap", props)
                self.assertNotIn("_EmissionColor", props)                           # the leftover _EmissionColor of a material is not read

    def test_only_the_smap_variant_declares_an_emissive_colour(self):
        for name in HAS_POWER:
            with self.subTest(shader=name):
                self.assertEqual("_EmissiveColor" in self.shaders[name], name in HAS_EMISSIVE_COLOR)
        self.assertEqual(self.shaders["p0/Reflective/Bumped Emissive Specular SMap"]["_EmissiveColor"][0], self.COLOR)

    def test_the_cutout_emissive_shader_has_only_an_hdr_emission_colour(self):
        props = self.shaders["p0/Cutout/Bumped Diffuse Emissive"]
        self.assertEqual(props["_EmissionColor"][0], self.COLOR)
        self.assertTrue(props["_EmissionColor"][1] & self.HDR)
        for absent in ("_EmissionPower", "_EmissionVisibility", "_EmissiveColor"):
            self.assertNotIn(absent, props)

    def test_the_standard_shaders_declare_the_colour_and_the_map_and_nothing_else_of_it(self):
        for name in ("Standard", "Standard (Specular setup)"):
            with self.subTest(shader=name):
                props = self.shaders[name]
                self.assertEqual(props["_EmissionColor"][0], self.COLOR)
                self.assertIn("_EmissionMap", props)
                for absent in ("_EmissionPower", "_EmissionVisibility", "_EmissiveColor"):
                    self.assertNotIn(absent, props)

    def test_every_p0_emissive_shader_of_the_bundle_is_classified(self):
        emissive = {name for name in self.shaders if name.startswith("p0/") and "Emissive" in name}
        self.assertEqual(emissive, HAS_POWER | {"p0/Cutout/Bumped Diffuse Emissive"})


if __name__ == "__main__":
    unittest.main()
