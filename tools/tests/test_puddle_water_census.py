"""Every water decal material of a released map is either listed for the puddle footsteps or a documented exclusion.

Why (docs/progress/puddle-footsteps-all-maps.md, 2026-10-25): PuddleFootstepRepairs turns the native step sound into the Puddle set
inside the mask of the materials a map lists in `puddle_water_material_names`. The same shader (`Decal/Water Deferred Decal`) also
renders OIL decals, so a material is never recognised by its shader alone; it is classified once, by hand, and listed or excluded here.
A rebuilt package that brings a new water-shader material (or drops a listed one) must therefore fail until someone classifies it.

The census reads the staged shader sidecar `material-shaders.arena_<stem>.json` (every material of the map with its shader), which the
client build stages from the package into the ignored dist/. Skipped where dist/ is not present.
"""
import json
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SIDECAR_DIR = REPO / "dist" / "BepInEx" / "plugins" / "maschine-ArenaMapPort"
WATER_SHADER = "Decal/Water Deferred Decal"
STEMS = ("equator", "bay5", "airpit", "bowl")
# Water-shader materials that stay out of the list on purpose, id -> why.
EXCLUDED = {
    "bay5": {"25519639aa567c85": "'puddle benz': oil (ring/crater mask, opaque; two renderers are even named puddle_oil)"},
    "airpit": {"823c859e2c5d11bc": "'City_puddle_atlas_OUTDOOR': two runway decals, both inactive and off the map"},
}


def listed(stem):
    data = json.loads((REPO / f"tools/maps/{stem}.json").read_text(encoding="utf-8"))
    return data.get("puddle_water_material_names", [])


def water_materials(stem):
    path = SIDECAR_DIR / f"material-shaders.arena_{stem}.json"
    if not path.is_file():
        raise unittest.SkipTest(f"{path.relative_to(REPO)} is not staged here")
    data = json.loads(path.read_text(encoding="utf-8"))
    return {material for material, row in data.items() if row.get("shader") == WATER_SHADER}


class PuddleWaterCensusTests(unittest.TestCase):
    def test_listed_plus_excluded_is_exactly_the_water_shader_materials_of_the_package(self):
        for stem in STEMS:
            with self.subTest(map=stem):
                found = water_materials(stem)
                accounted = set(listed(stem)) | set(EXCLUDED.get(stem, {}))
                self.assertEqual(found - accounted, set(), "a water-shader material nobody classified as water or oil")
                self.assertEqual(accounted - found, set(), "a listed or excluded id that the package no longer has")

    def test_an_excluded_material_is_never_listed(self):
        for stem, excluded in EXCLUDED.items():
            with self.subTest(map=stem):
                self.assertEqual(set(listed(stem)) & set(excluded), set())

    def test_the_rain_driven_water_material_keeps_its_keyword(self):
        # Equator, Air Pit and Bowl list the rain-driven 'puddle' (USERAIN reads the global _WaterLevel); a package that lost the keyword
        # would make the repair read the editor level of a material that never gets wet. Static materials carry no keyword.
        for stem in ("equator", "airpit", "bowl"):
            with self.subTest(map=stem):
                path = SIDECAR_DIR / f"material-shaders.arena_{stem}.json"
                if not path.is_file():
                    self.skipTest(f"{path.relative_to(REPO)} is not staged here")
                row = json.loads(path.read_text(encoding="utf-8"))["0877f2335044042e"]
                self.assertEqual(row["shader"], WATER_SHADER)
                self.assertIn("USERAIN", row["keywords"])


if __name__ == "__main__":
    unittest.main()
