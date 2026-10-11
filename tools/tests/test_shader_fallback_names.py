"""shader_fallback_names (docs/PLAN-AIRPIT.md item 11): the per-map allow-list of the client's shader fallback table.

The names live in ArenaMapRegistry (ShaderFallbackNames) and mirror tools/maps/<key>.json; only the C# runtime
(ShaderStandinPolicy) reads them. validate_map_descriptors.py keeps the two copies in step and rejects a malformed
list; ArenaMapPort.Server.Tests/ShaderStandinPolicyTests pins the gating itself. Equator and every parked map carry
no key, so they never substitute a shader.
"""
import json
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
import validate_map_descriptors as VD  # noqa: E402

AIRPIT_NAMES = [
    "Custom/Billboard_FogSheet_Simple",
    "Cloth/ClothShader",
    "Cloth/ClothShader_backface",
    "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)",
]
BAY5_NAMES = [
    "Custom/WorldTriplanarFresnelPro",
    "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)",
    "Cloth/ClothShader",
    "Cloth/ClothShader_backface",
    "Custom/Billboard_FogSheet_Simple",
    "p0/Reflective/Bumped Specular SMap Baked",
]


def sidecar(stem):
    return json.loads((REPO / "tools" / "maps" / f"{stem}.json").read_text(encoding="utf-8"))


class ShaderFallbackNamesSidecarTests(unittest.TestCase):
    def test_only_bay5_and_airpit_carry_the_key(self):
        self.assertEqual(sidecar("airpit")["shader_fallback_names"], AIRPIT_NAMES)
        self.assertEqual(sidecar("bay5")["shader_fallback_names"], BAY5_NAMES)
        for path in sorted((REPO / "tools" / "maps").glob("*.json")):
            if "." in path.stem or path.stem in ("airpit", "bay5"):
                continue
            self.assertNotIn("shader_fallback_names", json.loads(path.read_text(encoding="utf-8")), path.name)

    def test_the_shipped_sidecars_and_registry_agree(self):
        # The full validator run covers every map; this pins that the new field is part of it.
        self.assertEqual(VD.shader_fallback_errors(sidecar("airpit")), [])
        self.assertEqual(VD.shader_fallback_errors(sidecar("bay5")), [])
        self.assertEqual(VD.shader_fallback_errors(sidecar("equator")), [])
        self.assertEqual(VD.shader_fallback_errors({}), [])


class ShaderFallbackNamesShapeTests(unittest.TestCase):
    def errors(self, value):
        return VD.shader_fallback_errors({"shader_fallback_names": value})

    def test_a_valid_list_passes(self):
        self.assertEqual(self.errors(["Cloth/ClothShader"]), [])
        self.assertEqual(self.errors([]), [])

    def test_rejects_non_arrays_empty_padded_and_non_string_names(self):
        for bad in ("Cloth/ClothShader", {"a": 1}, [""], [" Cloth/ClothShader"], ["Cloth/ClothShader "], [1], [None]):
            self.assertEqual(len(self.errors(bad)), 1, repr(bad))

    def test_rejects_duplicates(self):
        errors = self.errors(["Cloth/ClothShader", "Cloth/ClothShader"])
        self.assertEqual(len(errors), 1)
        self.assertIn("duplicate", errors[0])


class ShaderFallbackNamesMirrorTests(unittest.TestCase):
    def run_check(self, map_json, registry_args):
        errors = []
        VD.check_map_against_registry_entry(map_json, registry_args, {}, Path("airpit.json"), "AirPit", errors)
        return [e for e in errors if "shader_fallback_names" in e]

    def test_a_drifted_registry_list_is_reported(self):
        map_json = sidecar("airpit")
        self.assertEqual(self.run_check(map_json, {"ShaderFallbackNames": list(AIRPIT_NAMES)}), [])
        drifted = self.run_check(map_json, {"ShaderFallbackNames": AIRPIT_NAMES[:3]})
        self.assertEqual(len(drifted), 1)
        self.assertIn("mismatch", drifted[0])
        # A registry entry that lists names the sidecar lacks is drift too.
        no_key = {k: v for k, v in map_json.items() if k != "shader_fallback_names"}
        self.assertEqual(len(self.run_check(no_key, {"ShaderFallbackNames": AIRPIT_NAMES})), 1)
        self.assertEqual(self.run_check(no_key, {}), [])


if __name__ == "__main__":
    unittest.main()
