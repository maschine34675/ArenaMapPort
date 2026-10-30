"""tools/validate/spawn_placement_probe.py (PLAN-ICEBERG item 7, 2026-10-30): the offline stand-in for the physics half of
SpawnPlacementRepairs.IsValid, on synthetic triangles. The real-data use is in test_iceberg_spawns.py.
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

import numpy as np

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
import spawn_placement_probe as P  # noqa: E402


def quad(a, b, c, d):
    """Two triangles of the quad a-b-c-d (each a 3-vector)."""
    a, b, c, d = (np.array(v, dtype=float) for v in (a, b, c, d))
    return [np.stack([a, b, c]), np.stack([a, c, d])]


def floor(y=0.0, half=10.0):
    return quad((-half, y, -half), (half, y, -half), (half, y, half), (-half, y, half))


def wall_x(x, y0=0.0, y1=3.0, half=5.0):
    return quad((x, y0, -half), (x, y0, half), (x, y1, half), (x, y1, -half))


def probe(*groups, paths=None):
    tris = np.array([t for group in groups for t in group])
    owner = np.concatenate([np.full(len(g), i, dtype=np.int32) for i, g in enumerate(groups)])
    return P.PlacementProbe(tris, owner, paths or [f"Scene/Part{i}" for i in range(len(groups))])


class VerdictTests(unittest.TestCase):
    def test_a_point_on_open_floor_is_valid(self):
        verdict = probe(floor()).check((0.0, 0.05, 0.0))
        self.assertTrue(verdict.valid)
        self.assertAlmostEqual(verdict.floor_y, 0.0, places=6)
        self.assertTrue(verdict.ground_ok)
        self.assertIsNone(verdict.ceiling)
        self.assertEqual(verdict.blocked, [])
        self.assertEqual(verdict.floor_owner, "Scene/Part0")

    def test_the_ground_ray_reaches_one_metre_below_feet_plus_thirty_centimetres(self):
        floor_probe = probe(floor())
        self.assertTrue(floor_probe.check((0.0, 0.6, 0.0)).ground_ok)      # floor 0.9 below the ray start
        self.assertFalse(floor_probe.check((0.0, 1.5, 0.0)).ground_ok)     # 1.8 m of air under the start
        high = floor_probe.check((0.0, 1.5, 0.0))
        self.assertAlmostEqual(high.floor_y, 0.0, places=6)               # the floor is still found for the repair's search
        self.assertFalse(high.valid)

    def test_a_point_without_any_floor_is_invalid_and_has_no_floor_height(self):
        verdict = probe(floor(half=1.0)).check((5.0, 0.0, 5.0))
        self.assertIsNone(verdict.floor_y)
        self.assertIsNone(verdict.floor_owner)
        self.assertFalse(verdict.ground_ok)
        self.assertFalse(verdict.valid)

    def test_a_wall_inside_the_capsule_radius_blocks_and_one_outside_does_not(self):
        near = probe(floor(), wall_x(0.2)).check((0.0, 0.05, 0.0))
        self.assertFalse(near.valid)
        self.assertTrue(near.blocked)
        # every ring whose reach is at least 0.2 m: all but the lowest (reach 0.117 m: the bottom of the capsule's lower hemisphere)
        self.assertEqual({h for _a, h, _n, _d in near.blocked}, {0.25, 0.5, 0.85, 1.2, 1.55, 1.7})
        self.assertAlmostEqual(min(d for _a, _h, _n, d in near.blocked), 0.2, places=6)   # straight at the wall
        self.assertTrue(all(d <= P.CAPSULE_RADIUS for _a, _h, _n, d in near.blocked))
        far = probe(floor(), wall_x(0.5)).check((0.0, 0.05, 0.0))
        self.assertTrue(far.valid)

    def test_the_capsule_is_seven_rings_that_follow_the_two_hemispheres(self):
        # CheckCapsule(feet + 0.45, feet + 1.45, 0.35): 0.35 m wide from 0.45 to 1.45 m, narrower on the caps, 0.10 to 1.80 m in all
        self.assertEqual(P.SIDE_HEIGHTS, (0.12, 0.25, 0.5, 0.85, 1.2, 1.55, 1.7))
        reach = dict(P.SIDE_RINGS)
        self.assertAlmostEqual(reach[0.5], 0.35, places=9)
        self.assertAlmostEqual(reach[0.85], 0.35, places=9)
        self.assertAlmostEqual(reach[1.2], 0.35, places=9)
        self.assertAlmostEqual(reach[0.25], (0.35 ** 2 - 0.2 ** 2) ** 0.5, places=9)
        self.assertAlmostEqual(reach[1.7], (0.35 ** 2 - 0.25 ** 2) ** 0.5, places=9)
        self.assertLess(reach[0.12], reach[0.25])
        self.assertTrue(all(0.10 < h < 1.80 for h in P.SIDE_HEIGHTS))

    def test_a_curb_the_lower_hemisphere_touches_blocks_and_one_below_the_capsule_does_not(self):
        # review of 2026-10-30: a 0.3 m curb 0.2 m from the point passed with two rings at 0.5 and 1.2 m; the real capsule's lower
        # sphere (centre 0.45 m up) reaches it. The ring at 0.25 m (reach 0.287 m) sees it, the one at 0.12 m (0.117 m) does not.
        curb = probe(floor(), wall_x(0.2, 0.0, 0.3)).check((0.0, 0.05, 0.0))
        self.assertFalse(curb.valid)
        self.assertEqual({h for _a, h, _n, _d in curb.blocked}, {0.25})
        # a 0.08 m kerb is below the capsule's bottom (0.10 m above the feet): no overlap on the host either
        kerb = probe(floor(), wall_x(0.1, 0.0, 0.08)).check((0.0, 0.0, 0.0))
        self.assertTrue(kerb.valid)

    def test_a_wall_at_three_tenths_blocks_only_the_cylinder_and_the_upper_cap_rings_that_reach_it(self):
        wall = probe(floor(), wall_x(0.3)).check((0.0, 0.05, 0.0))
        self.assertEqual({h for _a, h, _n, _d in wall.blocked}, {0.5, 0.85, 1.2, 1.55})
        self.assertFalse(wall.valid)

    def test_headroom_counts_from_the_spawn_height_not_from_the_floor(self):
        # a 2.0 m ceiling: a spawn on the floor has 1.95 m, a spawn 0.3 m above it (the ground ray tolerates up to a metre) 1.7 m
        low = probe(floor(), floor(2.0))
        on_floor = low.check((0.0, 0.05, 0.0))
        self.assertAlmostEqual(on_floor.ceiling, 2.0, places=6)
        self.assertAlmostEqual(on_floor.headroom, 1.95, places=6)
        self.assertTrue(on_floor.valid)
        raised = low.check((0.0, 0.3, 0.0))
        self.assertAlmostEqual(raised.ceiling, 2.0, places=6)
        self.assertAlmostEqual(raised.headroom, 1.7, places=6)
        self.assertTrue(raised.ground_ok)
        self.assertFalse(raised.valid)
        self.assertIsNone(probe(floor()).check((0.0, 0.05, 0.0)).headroom)

    def test_a_ceiling_below_the_capsule_height_invalidates(self):
        low = probe(floor(), floor(1.5)).check((0.0, 0.05, 0.0))
        self.assertAlmostEqual(low.ceiling, 1.5, places=6)
        self.assertFalse(low.valid)
        high = probe(floor(), floor(2.5)).check((0.0, 0.05, 0.0))
        self.assertAlmostEqual(high.ceiling, 2.5, places=6)
        self.assertTrue(high.valid)

    def test_the_floor_under_the_feet_is_the_ground_rays_hit_and_a_platform_above_is_a_ceiling(self):
        on_platform = probe(floor(), floor(1.0)).check((0.0, 1.05, 0.0))
        self.assertAlmostEqual(on_platform.floor_y, 1.0, places=6)
        self.assertEqual(on_platform.floor_owner, "Scene/Part1")
        self.assertTrue(on_platform.valid)
        under_platform = probe(floor(), floor(1.0)).check((0.0, 0.05, 0.0))
        self.assertAlmostEqual(under_platform.floor_y, 0.0, places=6)
        self.assertAlmostEqual(under_platform.ceiling, 1.0, places=6)
        self.assertFalse(under_platform.valid)


class ColliderSetTests(unittest.TestCase):
    def test_runtime_nonsolid_names(self):
        self.assertTrue(P.runtime_nonsolid(("SPAWN_BOUND_FIGHTER",)))
        self.assertTrue(P.runtime_nonsolid(("SPAWNS", "SPAWN_BOUND_DEFENCE (1)")))
        self.assertTrue(P.runtime_nonsolid(("SPAWNS", "Bear house (5)")))            # a marker's own box is a trigger at raid time
        self.assertFalse(P.runtime_nonsolid(("SPAWNS",)))
        self.assertFalse(P.runtime_nonsolid(("SBG_Main", "OO", "SPAWNS_hall", "floor")))
        self.assertFalse(P.runtime_nonsolid(("SBG_Main", "OO", "wall")))

    def test_from_collider_set_drops_the_nonsolid_owners_and_keeps_the_rest(self):
        marker_box = quad((-1, 1.0, -1), (1, 1.0, -1), (1, 1.0, 1), (-1, 1.0, 1))      # a marker box top 1 m up
        owners = [SimpleNamespace(names=("Main", "Floor"), path="Main/Floor"),
                  SimpleNamespace(names=("SPAWNS", "Bear house (5)"), path="SPAWNS/Bear house (5)")]
        tris = np.array(floor() + marker_box)
        cset = SimpleNamespace(tris=tris, tri_owner=np.array([0, 0, 1, 1], dtype=np.int32), owners=owners)
        verdict = P.PlacementProbe.from_collider_set(cset).check((0.0, 0.05, 0.0))
        self.assertTrue(verdict.valid)
        self.assertAlmostEqual(verdict.floor_y, 0.0, places=6)
        self.assertEqual(verdict.floor_owner, "Main/Floor")
        kept = P.PlacementProbe.from_collider_set(cset, nonsolid=lambda names: False).check((0.0, 0.05, 0.0))
        self.assertFalse(kept.valid)                                                      # without the rule the box is the floor

    def test_a_probe_without_triangles_answers_instead_of_failing(self):
        empty = P.PlacementProbe(np.empty((0, 3, 3)), np.empty(0, dtype=np.int32), [])
        verdict = empty.check((0.0, 0.0, 0.0))
        self.assertFalse(verdict.valid)
        self.assertIsNone(verdict.floor_y)


if __name__ == "__main__":
    unittest.main()
