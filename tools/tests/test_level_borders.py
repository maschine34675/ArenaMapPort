"""Yard's border cubes (docs/PLAN-YARD.md item 4): the fallback sidecar read from the excluded Scripts scene."""
import json
import math
from pathlib import Path
import sys
import unittest

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
import extract_level_borders as LB  # noqa: E402
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


if __name__ == "__main__":
    unittest.main()
