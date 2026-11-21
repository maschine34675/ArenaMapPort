"""Yard's border cubes (docs/PLAN-YARD.md item 4): the fallback sidecar read from the excluded Scripts scene."""
import copy
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import extract_level_borders as LB  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402
import source_plan  # noqa: E402

SIDECAR = REPO / "tools" / "maps" / "yard.borders.json"
ARENA = Path(LB.DEFAULT_SOURCE)


def rotation_y(degrees):
    c, s = math.cos(math.radians(degrees)), math.sin(math.radians(degrees))
    return (c, 0, s, 0, 1, 0, -s, 0, c)


class WorldBoxTests(unittest.TestCase):
    def test_identity_box(self):
        box = LB.world_box((1, 0, 0, 0, 1, 0, 0, 0, 1), (10, 20, 30), (0, 1, 0), (2, 4, 6))
        self.assertEqual(box["center"], (10, 21, 30))
        self.assertEqual(box["size"], (2, 4, 6))
        self.assertEqual(box["aabb"], ((9, 19, 27), (11, 23, 33)))
        self.assertAlmostEqual(box["shear"], 0)
        for got, want in zip(box["rotation"], (0, 0, 0, 1)):
            self.assertAlmostEqual(got, want)

    def test_scaled_and_rotated_box(self):
        r = rotation_y(90)
        linear = tuple(r[3 * i + j] * (2, 1, 3)[j] for i in range(3) for j in range(3))  # rotation times scale (2, 1, 3)
        box = LB.world_box(linear, (0, 0, 0), (0, 0, 0), (1, 1, 1))
        self.assertAlmostEqual(box["shear"], 0)
        self.assertEqual([round(v, 6) for v in box["size"]], [2, 1, 3])
        low, high = box["aabb"]
        self.assertEqual([round(v, 6) for v in low], [-1.5, -0.5, -1.0])  # 90 degrees about y swaps the x and z extents
        self.assertEqual([round(v, 6) for v in high], [1.5, 0.5, 1.0])
        qx, qy, qz, qw = box["rotation"]
        self.assertAlmostEqual(qy, math.sin(math.radians(45)))
        self.assertAlmostEqual(qw, math.cos(math.radians(45)))

    def test_a_sheared_matrix_is_reported(self):
        box = LB.world_box((1, 1, 0, 0, 1, 0, 0, 0, 1), (0, 0, 0), (0, 0, 0), (1, 1, 1))
        self.assertGreater(box["shear"], 1e-4)


class YardSidecarTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = json.loads(SIDECAR.read_text(encoding="utf-8"))

    def test_the_sidecar_holds_arenas_104_cubes_on_layer_29(self):
        d = self.data
        self.assertEqual((d["schema"], d["key"], d["source_scene"], d["root"]),
                         (1, "arena_yard", "Arena_Yard_Scripts", "Arena_Yard_LevelBorders"))
        self.assertEqual((d["count"], len(d["rows"]), d["layers"]), (104, 104, [29]))
        self.assertTrue(all(r["layer"] == 29 and r["active"] and r["enabled"] and not r["is_trigger"] for r in d["rows"]))
        paths = [r["path"] for r in d["rows"]]
        self.assertEqual(len(set(paths)), 104)
        self.assertEqual(paths, sorted(paths))
        self.assertTrue(all(p.startswith("Arena_Yard_LevelBorders/Cube") for p in paths))

    def test_bounds_and_heights_match_the_survey(self):
        d = self.data
        self.assertEqual(d["bounds"], [[-71.6863, 5.087, -0.2058], [10.6488, 20.3267, 151.1742]])
        heights = [r["aabb"][1][1] - r["aabb"][0][1] for r in d["rows"]]
        self.assertAlmostEqual(min(heights), 0.3372, places=3)
        self.assertAlmostEqual(max(heights), 8.6697, places=3)
        for r in d["rows"]:
            for axis in range(3):
                self.assertLessEqual(d["bounds"][0][axis], r["aabb"][0][axis])
                self.assertGreaterEqual(d["bounds"][1][axis], r["aabb"][1][axis])
                self.assertLessEqual(r["aabb"][0][axis], r["aabb"][1][axis])

    @unittest.skipUnless((ARENA / "level75").is_file(), "needs the installed Arena")
    def test_the_sidecar_is_what_the_installed_arena_yields(self):
        # Index 75 is the plan's scripts role; skipped when a later Arena update moved it (the plan's mapping check says so).
        plan = REPO / "tools" / "extract" / "plans" / "yard.json"
        try:
            source_plan.verify_scene_mapping(source_plan.load_plan(plan), source_plan.read_build_scenes(ARENA), ARENA)
        except source_plan.PlanError as error:
            self.skipTest(f"scene mapping drifted: {error}")
        fresh = LB.read_borders(plan, ARENA, "Arena_Yard_LevelBorders")
        self.assertEqual(fresh, self.data)


DESCRIPTOR = REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs"
MAPS = REPO / "tools" / "maps"


def yard_map():
    return json.loads((MAPS / "yard.json").read_text(encoding="utf-8"))


class CsharpLiteralTests(unittest.TestCase):
    """PLAN-YARD item 10 part 2: --csharp writes the ArenaBorderBox rows of ArenaMapRegistry.Yard.LevelBorderBoxes."""

    @classmethod
    def setUpClass(cls):
        cls.data = json.loads(SIDECAR.read_text(encoding="utf-8"))
        cls.rows = cls.data["rows"]

    def test_floats_are_plain_literals_that_read_back_exactly(self):
        for value, text in ((0, "0.0f"), (-0.0, "0.0f"), (1, "1.0f"), (-71.4257, "-71.4257f"), (0.000012, "0.000012f"), (1e-05, "0.00001f"),
                            (0.025267, "0.025267f"), (123456.5, "123456.5f")):
            self.assertEqual(LB.csharp_float(value), text)
        for row in self.rows:
            for v in (*row["center"], *row["size"], *row["rotation"]):
                text = LB.csharp_float(v)
                self.assertNotRegex(text, r"[eE]")
                self.assertEqual(float(text[:-1]), v)

    def test_one_line_per_row_in_row_order_and_the_registry_carries_exactly_this_text(self):
        text = LB.render_csharp(self.rows)
        lines = text.split("\n")
        self.assertEqual(len(lines), 104)
        self.assertTrue(lines[0].lstrip().startswith("new ArenaBorderBox(-71.4257f, 18.0591f, 71.3217f, 0.1515f, 4.5352f, 32.9876f, 0.0f, 0.0f, 0.0f, 1.0f)"))
        self.assertIn(text, DESCRIPTOR.read_text(encoding="utf-8"))                      # text mode: CRLF reads as LF

    def test_the_literals_parse_back_to_the_sidecar_numbers_and_the_registry_mirrors_them(self):
        parsed = [VD.parse_cs_value(line.strip().rstrip(",")) for line in LB.render_csharp(self.rows).split("\n")]
        self.assertEqual(parsed, [{k: r[k] for k in ("center", "size", "rotation")} for r in self.rows])
        registry = VD.registry_entries(DESCRIPTOR.read_text(encoding="utf-8"))["Yard"]["LevelBorderBoxes"]
        self.assertEqual(registry, parsed)
        self.assertEqual(VD.level_border_errors(yard_map(), SIDECAR, registry), [])

    def test_a_row_the_client_could_not_raise_is_refused(self):
        for change in ({"active": False}, {"enabled": False}, {"is_trigger": True}, {"layer": 18}, {"size": [1.0, 0.0, 2.0]}, {"rotation": [0.0, 0.0, 1.0]}):
            row = dict(self.rows[0], **change)
            with self.subTest(change=change), self.assertRaises(LB.BorderError):
                LB.render_csharp([row])

    def test_the_cli_prints_the_literal_from_the_sidecar_without_arena(self):
        script = str(REPO / "tools" / "extract" / "extract_level_borders.py")
        done = subprocess.run([sys.executable, script, "--from-sidecar", str(SIDECAR), "--csharp"], capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.replace("\r\n", "\n"), "// arena_yard: 104 rows\n" + LB.render_csharp(self.rows) + "\n")
        bad = subprocess.run([sys.executable, script, "--from-sidecar", str(SIDECAR)], capture_output=True, text=True)
        self.assertNotEqual(bad.returncode, 0)                                            # --from-sidecar alone is a usage error
        bad = subprocess.run([sys.executable, script, "--csharp"], capture_output=True, text=True)
        self.assertNotEqual(bad.returncode, 0)                                            # the Arena run needs --plan, --root and --out

    def test_yard_is_the_one_map_with_border_boxes_and_a_border_sidecar(self):
        entries = VD.registry_entries(DESCRIPTOR.read_text(encoding="utf-8"))
        for name, entry in entries.items():
            if name != "Yard":
                self.assertNotIn("LevelBorderBoxes", entry, name)
        self.assertEqual(sorted(p.name for p in MAPS.glob("*.borders.json")), ["yard.borders.json"])
        for stem in ("equator", "bay5", "airpit", "bowl", "iceberg", "autoservice"):
            self.assertEqual(VD.level_border_errors(json.loads((MAPS / f"{stem}.json").read_text(encoding="utf-8")), MAPS / f"{stem}.borders.json", None), [], stem)


class AabbTests(unittest.TestCase):
    def test_every_sidecar_aabb_follows_from_its_oriented_box(self):
        data = json.loads(SIDECAR.read_text(encoding="utf-8"))
        worst = 0.0
        for row in data["rows"]:
            low, high = VD.border_box_aabb(row["center"], row["size"], row["rotation"])
            for got, want in zip(low + high, row["aabb"][0] + row["aabb"][1]):
                worst = max(worst, abs(got - want))
        self.assertLess(worst, VD.LEVEL_BORDER_AABB_TOLERANCE)

    def test_a_yawed_box(self):
        c, s = math.cos(math.radians(22.5)), math.sin(math.radians(22.5))   # the quaternion holds the half angle
        low, high = VD.border_box_aabb([10.0, 5.0, -3.0], [2.0, 4.0, 6.0], [0.0, s, 0.0, c])      # 45 degrees about y: the x and z extents become (2 + 6) / sqrt(2) / 2
        half = (2.0 + 6.0) / math.sqrt(2) / 2
        for got, want in zip(low + high, [10 - half, 3.0, -3 - half, 10 + half, 7.0, -3 + half]):
            self.assertAlmostEqual(got, want, places=9)


class ValidatorTests(unittest.TestCase):
    """validate_map_descriptors.level_border_errors: one negative test per error class."""

    @classmethod
    def setUpClass(cls):
        cls.doc = json.loads(SIDECAR.read_text(encoding="utf-8"))
        cls.registry = VD.registry_entries(DESCRIPTOR.read_text(encoding="utf-8"))["Yard"]["LevelBorderBoxes"]

    def run_with(self, change, registry=None):
        doc = copy.deepcopy(self.doc)
        change(doc)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "yard.borders.json"
            path.write_text(json.dumps(doc), encoding="utf-8")
            return VD.level_border_errors(yard_map(), path, self.registry if registry is None else registry)

    def test_the_tracked_sidecar_passes_and_the_untouched_copy_too(self):
        self.assertEqual(VD.level_border_errors(yard_map(), SIDECAR, self.registry), [])
        self.assertEqual(self.run_with(lambda d: None), [])

    def test_the_mirror_is_compared_number_for_number(self):
        changed = copy.deepcopy(self.registry)
        changed[7]["size"][1] += 0.0001
        errors = VD.level_border_errors(yard_map(), SIDECAR, changed)
        self.assertEqual(len(errors), 1, errors)
        self.assertIn("rows[7]", errors[0])
        self.assertIn("differs from the registry in size", errors[0])
        changed = copy.deepcopy(self.registry)
        changed[0]["rotation"][3] = 0.9
        self.assertTrue(any("differs from the registry in rotation" in e for e in VD.level_border_errors(yard_map(), SIDECAR, changed)))
        swapped = copy.deepcopy(self.registry)
        swapped[3], swapped[4] = swapped[4], swapped[3]
        self.assertEqual(len(VD.level_border_errors(yard_map(), SIDECAR, swapped)), 2)       # the ORDER is part of the mirror
        self.assertTrue(any("the registry's LevelBorderBoxes 103" in e for e in VD.level_border_errors(yard_map(), SIDECAR, self.registry[:-1])))
        self.assertTrue(any("the registry's LevelBorderBoxes 0" in e for e in VD.level_border_errors(yard_map(), SIDECAR, None)))
        self.assertTrue(any("is not an ArenaBorderBox literal" in e for e in VD.level_border_errors(yard_map(), SIDECAR, ["x"] + self.registry[1:])))

    def test_registry_rows_without_a_sidecar_are_an_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            errors = VD.level_border_errors(yard_map(), Path(tmp) / "yard.borders.json", self.registry)
        self.assertEqual(len(errors), 1)
        self.assertIn("does not exist", errors[0])

    def test_the_sidecar_is_judged_in_itself(self):
        def key(d): d["key"] = "arena_bowl"
        def scene(d): d["source_scene"] = "Nope"
        def count(d): d["count"] = 103
        def keys(d): del d["rows"][2]["aabb"]
        def layer(d): d["rows"][0]["layer"] = 18
        def inactive(d): d["rows"][1]["active"] = False
        def trigger(d): d["rows"][1]["is_trigger"] = True
        def flat(d): d["rows"][4]["size"][2] = 0.0
        def nonunit(d): d["rows"][5]["rotation"] = [0.0, 0.0, 0.5, 0.5]
        def aabb(d): d["rows"][6]["aabb"][1][0] += 0.5
        def duplicate(d): d["rows"][9]["path"] = d["rows"][8]["path"]
        def bounds(d): d["bounds"][1][0] += 1.0
        def path(d): d["rows"][3]["path"] = "A//B"
        for name, change, text in (("key", key, "is not the map's"), ("scene", scene, "is not a scene of the map"), ("count", count, "is not the number of rows"),
                                   ("keys", keys, "needs exactly the keys"), ("layer", layer, "layer must be 29"), ("inactive", inactive, "active, enabled"),
                                   ("trigger", trigger, "active, enabled"), ("flat", flat, "size must be"), ("nonunit", nonunit, "unit quaternion"),
                                   ("aabb", aabb, "does not follow from the box"), ("duplicate", duplicate, "duplicate path"), ("bounds", bounds, "bounds"),
                                   ("path", path, "hierarchy path")):
            with self.subTest(name):
                errors = self.run_with(change)
                self.assertTrue(any(text in e for e in errors), errors)

    def test_an_empty_or_malformed_file(self):
        self.assertTrue(any("non-empty" in e for e in self.run_with(lambda d: d.update(rows=[]))))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "yard.borders.json"
            path.write_text("{", encoding="utf-8")
            self.assertTrue(any("not valid JSON" in e for e in VD.level_border_errors(yard_map(), path, None)))
            path.write_text("[]", encoding="utf-8")
            self.assertTrue(any("must be an object" in e for e in VD.level_border_errors(yard_map(), path, None)))


if __name__ == "__main__":
    unittest.main()
