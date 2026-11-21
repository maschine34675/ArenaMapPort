"""tools/validate/normal_texture_preflight.py: the offline pre-flight of NormalTextureImportPolicy.

Groups:
  * synthetic packages in a temp directory (always run when pwsh exists): the verdict comes from the production C# policy
    through the PowerShell bridge, so these tests also prove the bridge feeds Usage and Pixels the way the Unity driver does;
  * the seven accepted packages (skipped when absent; override the work folder with ARENAMAPPORT_WORK_DIR): zero rejections
    and the plan counts that were measured on 2026-11-21 (a policy change that moves a plan moves a pin here on purpose);
  * the policy's own PowerShell regression (tools/tests/test_normal_texture_import.ps1), so it runs inside this suite.
Real-data part: about 100 s on this VM for all seven packages (PNG decode dominates); nothing is cached.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import normal_texture_preflight as pf  # noqa: E402

PWSH = shutil.which("pwsh")
BUMPED = "p0/Reflective/Bumped Specular"
CLOTH = "Cloth/ClothShader"
UNSUPPORTED_OR_MIXED = "invalid dimensions/pixels or unsupported/mixed material usage"


def write_png(path, pixels, size, mode="RGBA"):
    from PIL import Image
    path.parent.mkdir(parents=True, exist_ok=True)
    im = Image.new(mode, size)
    im.putdata(pixels)
    im.save(path)


class Pkg:
    """A throwaway package: textures with 2x1 PNGs and materials with tex_envs."""

    def __init__(self, root):
        self.root = Path(root)
        self.manifest = {"textures": {}, "materials": {}, "scenes": [], "errors": []}

    def texture(self, tid, pixels=((255, 124, 126, 121), (255, 128, 128, 128)), fmt="DXT5", space="Linear",
                normal=True, size=(2, 1), manifest_size=None, mode="RGBA", **extra):
        write_png(self.root / "textures" / (tid + ".png"), list(pixels), size, mode)
        w, h = manifest_size or size
        self.manifest["textures"][tid] = dict({"file": "textures/%s.png" % tid, "format_src": fmt, "color_space": space,
                                               "is_normal_map": normal, "width": w, "height": h, "source_name": "src_" + tid,
                                               "dimension": "Tex2D"}, **extra)

    def material(self, mid, shader, slots, keywords=()):
        (self.root / "materials").mkdir(parents=True, exist_ok=True)
        envs = {prop: {"texture_id": tid} for prop, tid in slots.items()}
        (self.root / "materials" / (mid + ".json")).write_text(json.dumps(
            {"shader": "IGNORED-BY-THE-LOADER", "tex_envs": envs, "keywords": list(keywords)}), encoding="utf-8")
        self.manifest["materials"][mid] = {"file": "materials/%s.json" % mid, "shader": shader, "source_name": "mat_" + mid}

    def finish(self):
        (self.root / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        return {r["id"]: r for r in pf.check_package(self.root)}


@unittest.skipUnless(PWSH, "pwsh (PowerShell 7) is not installed")
class SyntheticTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.pkg = Pkg(self._tmp.name)

    def test_a_packed_dxt5_normal_on_a_bump_map_passes(self):
        self.pkg.texture("n1")
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1"})
        r = self.pkg.finish()["n1"]
        self.assertEqual((r["plan"], r["compression"], r["srgb"], r["message"]), ("PackedRaG", "DXT5", False, None))

    def test_a_texture_on_an_unsupported_slot_is_rejected_with_the_policys_message(self):
        self.pkg.texture("n1")
        self.pkg.material("m1", BUMPED, {"_MainTex": "n1"})
        r = self.pkg.finish()["n1"]
        self.assertIsNone(r["plan"])
        self.assertIn("Normal texture n1 rejected: " + UNSUPPORTED_OR_MIXED, r["message"])
        self.assertEqual([s["property"] for s in r["usage"]], ["_MainTex"])

    def test_the_shader_name_comes_from_the_manifest_entry(self):
        # _Normalmap is a normal slot on MK4/Rock only; the material file's own shader field is a decoy.
        self.pkg.texture("n1")
        self.pkg.material("m1", "MK4/Rock", {"_Normalmap": "n1"})
        self.assertEqual(self.pkg.finish()["n1"]["plan"], "PackedRaG")

    def test_empty_slots_and_ids_outside_the_manifest_are_no_usage(self):
        # CollectNormalTextureUsages skips a slot whose texture_id is null or names no texture of the manifest
        self.pkg.texture("n1")
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1", "_MainTex": None, "_SpecMap": "not-in-the-manifest"})
        r = self.pkg.finish()["n1"]
        self.assertEqual(r["plan"], "PackedRaG")
        self.assertEqual([s["property"] for s in r["usage"]], ["_BumpMap"])

    def test_a_texture_nobody_binds_is_rejected(self):
        self.pkg.texture("n1")
        r = self.pkg.finish()["n1"]
        self.assertIsNone(r["plan"])
        self.assertIsNone(r["usage"])
        self.assertIn(UNSUPPORTED_OR_MIXED, r["message"])

    def test_a_png_that_is_not_rgba8_is_rejected_before_the_policy(self):
        self.pkg.texture("n1", pixels=[(255, 1, 2), (255, 3, 4)], mode="RGB")
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1"})
        r = self.pkg.finish()["n1"]
        self.assertIsNone(r["plan"])
        self.assertIn("not an RGBA8 PNG", r["message"])

    def test_a_size_mismatch_with_the_manifest_is_rejected(self):
        self.pkg.texture("n1", manifest_size=(4, 4))
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1"})
        self.assertIn("dimensions/decode mismatch", self.pkg.finish()["n1"]["message"])

    def test_two_kinds_of_reader_on_one_texture_are_rejected(self):
        self.pkg.texture("n1", fmt="DXT5", space="sRGB", pixels=((5, 8, 5, 11), (5, 8, 5, 11)))
        self.pkg.material("m1", CLOTH, {"_NormalsMask": "n1", "_BumpMap": "n1"})
        self.assertIn(UNSUPPORTED_OR_MIXED, self.pkg.finish()["n1"]["message"])

    def test_a_cloth_mask_alone_is_preserved_as_a_mask(self):
        self.pkg.texture("n1", fmt="DXT5", space="sRGB", pixels=((5, 8, 5, 11), (5, 8, 5, 11)))
        self.pkg.material("m1", CLOTH, {"_NormalsMask": "n1"})
        self.assertEqual(self.pkg.finish()["n1"]["plan"], "ClothNormalsMask")

    def test_the_dxt1_keyword_reaches_the_usage(self):
        self.pkg.texture("n1", fmt="DXT1", pixels=((128, 128, 255, 255), (128, 128, 255, 255)))
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1"}, keywords=["DXT1_NM"])
        self.assertEqual(self.pkg.finish()["n1"]["plan"], "RgbNormal")
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1"})  # same texture without the keyword: no RGB reader
        self.assertIsNone(self.pkg.finish()["n1"]["plan"])

    def test_the_pixel_statistics_reach_the_policy(self):
        # An RGB normal with 1 percent backward-leaning blue is beyond the 1-in-1000 bound; none is within it.
        px = [(128, 128, 255, 255)] * 99 + [(128, 128, 100, 255)]
        self.pkg.texture("n1", fmt="DXT1", pixels=px, size=(100, 1))
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1"}, keywords=["DXT1_NM"])
        r = self.pkg.finish()["n1"]
        self.assertIsNone(r["plan"])
        self.assertIn("belowHalfBlue=1/100", r["message"])
        self.assertEqual(r["stats"]["BelowHalfBlue"], 1)

    def test_volumes_terrain_arrays_and_non_normals_are_not_inspected(self):
        self.pkg.texture("vol", native_volume={"x": 1})
        self.pkg.texture("terr", generated_terrain_array=True)
        self.pkg.texture("plain", normal=False)
        self.pkg.texture("n1")
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1", "_MainTex": "plain"})
        self.assertEqual(list(self.pkg.finish()), ["n1"])

    def test_the_failure_line_and_exit_code(self):
        self.pkg.texture("n1")
        self.pkg.finish()
        proc = subprocess.run([sys.executable, str(REPO / "tools/validate/normal_texture_preflight.py"), self._tmp.name],
                              capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(proc.returncode, 1)
        self.assertIn("NORMAL_TEXTURE_PREFLIGHT_FAILED 1 of 1", proc.stdout)
        self.assertIn("REJECTED n1 source_name=src_n1", proc.stdout)
        self.assertIn("slots: none", proc.stdout)

    def test_the_ok_line_and_exit_code(self):
        self.pkg.texture("n1")
        self.pkg.material("m1", BUMPED, {"_BumpMap": "n1"})
        self.pkg.finish()
        proc = subprocess.run([sys.executable, str(REPO / "tools/validate/normal_texture_preflight.py"), self._tmp.name],
                              capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        self.assertIn("NORMAL_TEXTURE_PREFLIGHT_OK 1 textures", proc.stdout)


class FailsLoudlyTests(unittest.TestCase):
    def test_a_missing_pwsh_is_an_error_and_no_fallback(self):
        orig = pf.shutil.which
        pf.shutil.which = lambda name: None
        try:
            with self.assertRaises(pf.PreflightError) as ctx:
                pf.run_policy([])
        finally:
            pf.shutil.which = orig
        self.assertIn("no Python fallback", str(ctx.exception))

    def test_a_missing_policy_file_is_an_error(self):
        orig = pf.POLICY
        pf.POLICY = REPO / "does-not-exist.cs"
        try:
            with self.assertRaises(pf.PreflightError):
                pf.run_policy([])
        finally:
            pf.POLICY = orig


# measured 2026-11-21 with the policy that has the ImpostorNormalDepth plan
PACKAGES = {
    "equator-native-v5": (239, {"ClothNormalsMask/DXT5": 1, "PackedRaG/BC7": 14, "PackedRaG/DXT5": 223, "RgbNormal/DXT1": 1}),
    "bay5-native-v5": (341, {"PackedRaG/BC7": 6, "PackedRaG/DXT5": 335}),
    "airpit-native-v10": (316, {"ClothNormalsMask/DXT1": 1, "PackedRaG/BC7": 12, "PackedRaG/DXT5": 302, "RgbNormal/DXT1": 1}),
    "bowl-native-v2": (291, {"ClothNormalsMask/DXT5": 1, "PackedRaG/BC7": 10, "PackedRaG/DXT5": 279, "RgbNormal/DXT1": 1}),
    "iceberg-native-v2": (482, {"PackedRaG/BC7": 21, "PackedRaG/DXT5": 461}),
    "autoservice-native-v2": (320, {"PackedRaG/BC7": 7, "PackedRaG/DXT5": 312, "RgbNormal/DXT1": 1}),
    "yard-native-v2": (478, {"ClothNormalsMask/DXT1": 1, "ImpostorNormalDepth/DXT5": 1, "PackedRaG/BC7": 4,
                             "PackedRaG/DXT5": 470, "RgbNormal/DXT1": 2}),
}


@unittest.skipUnless(PWSH, "pwsh (PowerShell 7) is not installed")
class AcceptedPackageTests(unittest.TestCase):
    def test_every_accepted_package_passes_with_its_measured_plans(self):
        seen = 0
        for name, (count, plans) in PACKAGES.items():
            pkg = WORK / "extract" / name
            if not (pkg / "manifest.json").is_file():
                continue
            with self.subTest(package=name):
                seen += 1
                rows = pf.check_package(pkg)
                rejected = [r["id"] + ": " + r["message"] for r in rows if r["plan"] is None]
                self.assertEqual(rejected, [])
                self.assertEqual(len(rows), count)
                got = {}
                for r in rows:
                    key = "%s/%s" % (r["plan"], r["compression"])
                    got[key] = got.get(key, 0) + 1
                self.assertEqual(got, plans)
        if not seen:
            self.skipTest("no accepted package under %s" % (WORK / "extract"))


@unittest.skipUnless(PWSH, "pwsh (PowerShell 7) is not installed")
class PolicyRegressionTests(unittest.TestCase):
    def test_the_policys_powershell_regression_passes(self):
        proc = subprocess.run([PWSH, "-NoProfile", "-File", str(REPO / "tools/tests/test_normal_texture_import.ps1")],
                              capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        self.assertIn("NORMAL_TEXTURE_IMPORT_POLICY_PASSED", proc.stdout)


if __name__ == "__main__":
    unittest.main()
