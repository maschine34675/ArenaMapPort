"""tools/validate/spawn_coverage.py on synthetic meshes only (no work/ data, no Arena, no Unity)."""
from __future__ import annotations

import json
import math
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
import navmesh_oracle as oracle  # noqa: E402
import spawn_coverage as sc  # noqa: E402


def grid_mesh(x0, x1, z0, z1, y=0.0, step=1.0):
    """A flat NavMesh of the rectangle, two consistently wound triangles per square, welded by mesh_from_triangulation."""
    xs = np.arange(x0, x1 + 1e-9, step)
    zs = np.arange(z0, z1 + 1e-9, step)
    verts = [(x, y, z) for x in xs for z in zs]
    nz = len(zs)
    idx = []
    for i in range(len(xs) - 1):
        for j in range(nz - 1):
            a, b, c, d = i * nz + j, (i + 1) * nz + j, (i + 1) * nz + j + 1, i * nz + j + 1
            idx += [a, b, c, a, c, d]
    return verts, idx


def strip_mesh(with_island=False):
    verts, idx = grid_mesh(0, 20, 0, 4)
    if with_island:
        v2, i2 = grid_mesh(30, 32, 0, 4)
        idx += [i + len(verts) for i in i2]
        verts += v2
    return oracle.mesh_from_triangulation(verts, idx)


class ComponentAndClearanceTests(unittest.TestCase):
    def test_play_component_is_the_one_with_the_rows_not_the_largest(self):
        verts, idx = grid_mesh(0, 4, 0, 4)              # small, holds the rows
        v2, i2 = grid_mesh(10, 30, 0, 10)               # larger, no rows
        mesh = oracle.mesh_from_triangulation(verts + v2, idx + [i + len(verts) for i in i2])
        play = sc.PlayMesh(mesh, [(2.0, 0.0, 2.0)])
        self.assertEqual(int(mesh.labels[mesh.locate([(2, 0, 2)])[0][0]]), play.label)
        self.assertNotEqual(play.label, 0)
        cells, _ = play.cells(1.0)
        self.assertTrue((cells[:, 0] <= 4.0).all())

    def test_clearance_on_a_strip_and_island_excluded(self):
        mesh = strip_mesh(with_island=True)
        play = sc.PlayMesh(mesh, [(10.0, 0.0, 2.0)])
        c = play.clearance([(10, 0, 2), (10, 0, 0.5), (1.0, 0, 2.0), (31.0, 0, 2.0)])
        self.assertAlmostEqual(c[0], 2.0, places=6)
        self.assertAlmostEqual(c[1], 0.5, places=6)
        self.assertAlmostEqual(c[2], 1.0, places=6)
        # the island is another component: its edges are not the play component's boundary, and its cells are not play cells
        cells, _ = play.cells(1.0)
        self.assertTrue((cells[:, 0] <= 20.0).all())
        self.assertGreater(c[3], 5.0)

    def test_snap_accepts_half_a_metre_and_rejects_the_rest(self):
        play = sc.PlayMesh(strip_mesh(), [(10.0, 0.0, 2.0)])
        got, why = play.snap(10.0, 0.2, 4.4)
        self.assertIsNotNone(got)
        self.assertLessEqual(got[3], 0.5 + 1e-9)
        got, why = play.snap(10.0, 0.0, 5.2)
        self.assertIsNone(got)
        self.assertEqual(why, "off_mesh")
        got, why = play.snap(10.0, 3.0, 2.0)                  # 3 m above the surface: outside the vertical window
        self.assertIsNone(got)


class DistanceFloorTests(unittest.TestCase):
    def test_strictly_farther_than_the_floor_counts(self):
        cells = np.array([[0.0, 0.0, 0.0], [25.0, 0.0, 0.0], [25.001, 0.0, 0.0]])
        rows = np.array([[0.0, 0.0, 0.0]])
        cnt = sc.distant_counts(cells, rows, 25.0, "3d")
        self.assertEqual(cnt["3d"].tolist(), [0, 0, 1])
        self.assertEqual(cnt["xz"].tolist(), [0, 0, 1])

    def test_xz_and_3d_differ_by_height(self):
        cells = np.array([[20.0, 0.0, 0.0]])
        rows = np.array([[0.0, 16.0, 0.0], [0.0, 0.0, 0.0]])        # 3d 25.6 / 20, xz 20 / 20
        cnt = sc.distant_counts(cells, rows, 25.0, "3d")
        self.assertEqual((int(cnt["xz"][0]), int(cnt["3d"][0])), (0, 1))

    def test_stats_report_shares_and_worst_region(self):
        cells = np.array([[x, 0.0, 0.0] for x in range(10)], float)
        cnt = np.array([0, 0, 1, 2, 3, 4, 5, 5, 6, 7])
        st = sc.cover_stats(cells, cnt, np.ones(10, bool), 5, 1.0)
        self.assertEqual(st["min"], 0)
        self.assertEqual(st["median"], 3.5)
        self.assertAlmostEqual(st["share_ge_need"], 0.4)
        self.assertAlmostEqual(st["share_ge_2"], 0.7)
        self.assertAlmostEqual(st["share_zero"], 0.2)
        self.assertEqual(st["worst_region"]["cells"], 2)
        self.assertEqual(st["worst_region"]["bbox_xz"], [0.0, 0.0, 1.0, 0.0])


class GeometryTests(unittest.TestCase):
    def test_rotated_pocket_containment_yaw_and_quaternion(self):
        # a box 10 (local x) by 2 (local z), yawed 90 degrees: its long axis runs along world z
        yaw = sc.Pocket.from_bound({"name": "p", "side": "a", "center": [0, 0, 0], "size": [10, 4, 2], "yaw": 90.0})
        s = math.sqrt(0.5)
        quat = sc.Pocket.from_bound({"name": "q", "side": "a", "center": [0, 0, 0], "size": [10, 4, 2], "rotation": [0, s, 0, s]})
        for pk in (yaw, quat):
            inside = pk.contains([[0, 0, 4.5], [0.9, 0, -4.9], [0, 1.9, 0]])
            self.assertTrue(inside.all(), pk.name)
            outside = pk.contains([[4.5, 0, 0], [0, 0, 5.1], [1.1, 0, 0], [0, 2.1, 0]])
            self.assertFalse(outside.any(), pk.name)
        self.assertAlmostEqual(float(yaw.xz_outside_distance([[0, 0, 7.0]])[0]), 2.0, places=6)
        self.assertAlmostEqual(float(yaw.xz_outside_distance([[2.0, 0, 8.0]])[0]), math.hypot(1.0, 3.0), places=6)
        self.assertEqual(float(yaw.xz_outside_distance([[0.5, 0, 1.0]])[0]), 0.0)

    def test_the_real_east_pocket_quaternion_runs_its_long_axis_along_z(self):
        q = [0.0, -0.7071, 0.0, 0.7071]                              # Chop Shop's pockets: yaw 270
        pk = sc.Pocket.from_bound({"name": "e", "side": "east", "center": [-338.4, -2.8, 492.8], "size": [38.6, 17.1, 11.6], "rotation": q})
        self.assertTrue(pk.contains([[-338.4, -3.8, 492.8 + 19.0]]).all())
        self.assertFalse(pk.contains([[-338.4 + 19.0, -3.8, 492.8]]).any())

    def test_hull_and_distance(self):
        h = sc.hull2d([(0, 0), (4, 0), (4, 2), (0, 2), (2, 1)])
        self.assertEqual(len(h), 4)
        d = sc.dist_to_hull([(2, 1), (6, 1), (0, -3), (7, 6)], h)
        self.assertEqual(d.tolist()[:3], [0.0, 2.0, 3.0])
        self.assertAlmostEqual(d[3], 5.0)
        seg = sc.hull2d([(0, 0), (4, 0)])                             # a thin leaf seen from above
        self.assertAlmostEqual(float(sc.dist_to_hull([(2, 3)], seg)[0]), 3.0)

    def test_hull_box_distance_sees_a_thin_gate_through_the_middle(self):
        leaf = sc.hull2d([(8.5, 0.0), (9.0, 0.0), (9.0, 10.0), (8.5, 10.0)])
        self.assertEqual(sc.hull_box_distance(leaf, 8.0, 5.0, 2.0), 0.0)             # the box (6..10) straddles it, no vertex inside the box
        self.assertAlmostEqual(sc.hull_box_distance(leaf, 6.0, 5.0, 2.0), 0.5)
        self.assertAlmostEqual(sc.hull_box_distance(leaf, 12.0, 5.0, 2.0), 1.0)
        self.assertEqual(sc.hull_box_distance(sc.hull2d([(0, 0), (10, 0)]), 5.0, 0.0, 1.0), 0.0)   # a segment through the box

    def test_triangle_box_overlap(self):
        tri = np.array([[[0, 0, 0], [8, 0, 0], [0, 0, 8]]], float)    # a floor triangle at y 0
        self.assertTrue(sc.tri_box_overlap(tri, [1, 0, 1], [1, 1, 1]).all())
        self.assertFalse(sc.tri_box_overlap(tri, [1, 2.5, 1], [1, 1, 1]).any())
        self.assertFalse(sc.tri_box_overlap(tri, [7, 0, 7], [0.5, 0.5, 0.5]).any())     # beyond the hypotenuse
        wall = np.array([[[2, 0, -5], [2, 3, -5], [2, 0, 5]]], float)
        self.assertTrue(sc.tri_box_overlap(wall, [2, 1, 0], [1, 1, 1]).all())


class TieRuleTests(unittest.TestCase):
    def arr(self, *v):
        return np.array(v)

    def test_gain_then_ge2_then_deficit_then_min_distance_then_index(self):
        idx = np.arange(5)
        nat = np.zeros(5, bool)
        # g1 decides first
        best, _, top = sc.pick_best(idx, self.arr(5, 9, 9, 9, 9), self.arr(0, 0, 3, 3, 3), self.arr(0, 0, 1, 2, 2), self.arr(1, 1, 1, 1, 9), nat)
        self.assertEqual((best, top), (4, 9))            # g1 tie of 1..4, g2 tie of 2..4, deficit tie of 3..4, min distance picks 4
        best, _, _ = sc.pick_best(idx, self.arr(9, 9, 9, 9, 9), self.arr(0, 1, 2, 2, 0), self.arr(0, 0, 5, 7, 9), self.arr(1, 1, 1, 1, 1), nat)
        self.assertEqual(best, 3)                         # g2 ties 2 and 3, deficit picks 3
        best, _, _ = sc.pick_best(idx, self.arr(9, 9, 9, 9, 9), self.arr(2, 2, 2, 2, 2), self.arr(4, 4, 4, 4, 4), self.arr(7, 3, 7, 7, 7), nat)
        self.assertEqual(best, 0)                         # complete tie: the lowest index

    def test_a_native_candidate_within_five_percent_beats_a_fill_point(self):
        nat = np.array([False, True, True])
        best, note, _ = sc.pick_best(np.arange(3), self.arr(100, 95, 94), self.arr(0, 0, 0), self.arr(0, 0, 0), self.arr(1, 1, 1), nat)
        self.assertEqual(best, 1)                         # 95 >= 0.95 * 100
        self.assertIn("native", note)
        best, _, _ = sc.pick_best(np.arange(3), self.arr(100, 94, 94), self.arr(0, 0, 0), self.arr(0, 0, 0), self.arr(1, 1, 1), nat)
        self.assertEqual(best, 0)                         # 94 < 95
        best, _, _ = sc.pick_best(np.arange(3), self.arr(100, 98, 98), self.arr(0, 0, 5), self.arr(0, 0, 0), self.arr(1, 1, 1), nat)
        self.assertEqual(best, 2)                         # both natives qualify, the normal order (g2) picks the better one

    def make_ctx(self, **over):
        params = dict(sc.DEFAULTS)
        params.update(need=1, floor=25.0, spacing=7.0, min_rows=1, max_rows=3)
        params.update(over)
        return sc.Context(None, None, [], [], np.zeros((0, 3)), np.zeros((0, 3)), None, params)

    def test_greedy_stops_at_max_rows_and_enforces_spacing_between_selected(self):
        cells = np.array([[0.0, 0.0, float(z)] for z in range(101)])
        cands = [{"name": f"c{z}", "source": "fill", "position": [0.0, 0.0, float(z)], "native": False} for z in (0, 3, 50, 100)]
        ctx = self.make_ctx(max_rows=2, min_rows=2)
        chosen, rejected, steps = sc.greedy_select(cells, np.zeros((0, 3)), cands, ctx, ctx.params)
        self.assertEqual(len(chosen), 2)
        self.assertEqual(steps[0]["index"], 1)
        names = {c["name"] for c in chosen}
        self.assertFalse({"c0", "c3"} <= names)           # 3 m apart is under the 7 m spacing
        # c0 and c3 conflict: whichever was not chosen is rejected for spacing only if it was alive when the other was taken
        for c, why in rejected:
            self.assertTrue(why.startswith("spacing_selected"))

    def test_greedy_stops_at_min_rows_when_nothing_improves(self):
        cells = np.array([[0.0, 0.0, float(z)] for z in range(30)])      # a 30 m strip: every candidate reaches the cells it can
        cands = [{"name": f"c{z}", "source": "fill", "position": [0.0, 0.0, float(z)], "native": False} for z in (0, 10, 20)]
        ctx = self.make_ctx(max_rows=10, min_rows=1)
        chosen, _, steps = sc.greedy_select(cells, np.array([[0.0, 0.0, 0.0]]), cands, ctx, ctx.params)
        self.assertEqual(chosen, [])                      # the base row already gives every cell its row where one can be; no gain, min_rows reached


class ExitAndReachTests(unittest.TestCase):
    def setUp(self):
        verts, idx = grid_mesh(0, 20, 0, 10)
        self.mesh = oracle.mesh_from_triangulation(verts, idx)
        self.play = sc.PlayMesh(self.mesh, [(15.0, 0.0, 5.0)])
        self.cells, self.cpoly = self.play.cells(1.0)
        self.pocket = sc.Pocket.from_bound({"name": "west", "side": "w", "center": [5, 0, 5], "size": [10, 4, 10], "yaw": 0.0})
        self.pocket.markers = np.array([[3.0, 0.0, 5.0], [7.0, 0.0, 5.0]])

    def ctx(self, gates=(), reach=None, **over):
        params = dict(sc.DEFAULTS)
        params.update(over)
        reach = reach or sc.Reach(self.play, self.cells, self.cpoly, 1.0, [], [])
        return sc.Context(self.play, reach, [self.pocket], list(gates), np.array([[2.0, 0.0, 5.0]]), np.array([[15.0, 0.0, 5.0]]), None, params)

    def test_exit_box_must_lie_inside_the_pocket(self):
        ctx = self.ctx()
        out = sc.exit_options(ctx, self.pocket, self.cells, None, ctx.params)
        # centres x 2..8 and z 2..8 on the 1 m lattice: 7 x 7 boxes whose corners stay inside the 10 x 10 pocket
        self.assertEqual(out["box_cells_inside_pocket"], 49)
        self.assertEqual(out["valid_cells"], 49)
        for opt in out["options"].values():
            if opt:
                x, _, z = opt["position"]
                self.assertTrue(2.0 <= x <= 8.0 and 2.0 <= z <= 8.0)
        centre = out["options"]["centre"]
        self.assertEqual(centre["position"][:1] + centre["position"][2:], [5.0, 5.0])          # the marker centroid is (5, 5)
        far = out["options"]["far"]
        self.assertGreater(far["nearest_marker"], 2.0)
        self.assertIn("no gate", out["gate_note"])

    def test_box_corners_off_the_mesh_invalidate_a_cell(self):
        ctx = self.ctx(min_corners=4)
        wide = sc.Pocket.from_bound({"name": "wide", "side": "w", "center": [3, 0, 5], "size": [12, 4, 10], "yaw": 0.0})   # sticks out past x = 0
        wide.markers = self.pocket.markers
        out = sc.exit_options(ctx, wide, self.cells, None, ctx.params)
        # cells at x = 0 lie on the mesh edge, but a box centred at x <= 1 has corners at x <= -1: not inside the footprint of the MESH
        valid_x = [m["position"][0] for m in sc.exit_options(ctx, wide, self.cells, None, ctx.params)["options"].values() if m]
        self.assertTrue(all(x >= 2.0 for x in valid_x))
        self.assertLessEqual(out["valid_cells"], out["box_cells_inside_pocket"])

    def test_held_gate_closes_the_far_side_in_a_plain_raid(self):
        gate = sc.Gate("G", ["held"], [np.array([[9.9, 0.0], [10.1, 0.0], [10.1, 10.0], [9.9, 10.0]])], [np.array([[9.9, 0.0], [10.1, 0.0], [10.1, 10.0], [9.9, 10.0]])], -1.0, 3.0)
        reach = sc.Reach(self.play, self.cells, self.cpoly, 1.0, [gate], [(15.0, 0.0, 5.0)])
        near_seed = self.cells[:, 0] > 10.5
        self.assertTrue(reach.reach[near_seed].all())
        self.assertFalse(reach.reach[self.cells[:, 0] < 9.0].any())
        self.assertTrue(reach.query((15.0, 0.0, 5.0)))
        self.assertFalse(reach.query((3.0, 0.0, 5.0)))
        ctx = self.ctx(gates=[gate], reach=reach)
        out = sc.exit_options(ctx, self.pocket, self.cells, None, ctx.params)
        self.assertFalse(out["plain_raid_reachable"])
        self.assertEqual(out["valid_cells_plain_raid_reachable"], 0)
        self.assertGreater(out["valid_cells"], 0)                 # still valid for ArenaMode unless --exit-require-reach
        strict = self.ctx(gates=[gate], reach=reach, exit_require_reach=True)
        self.assertEqual(sc.exit_options(strict, self.pocket, self.cells, None, strict.params)["valid_cells"], 0)

    def test_gate_footprint_keeps_exit_boxes_away_and_defines_the_gate_option(self):
        hull = np.array([[8.5, 0.0], [9.0, 0.0], [9.0, 10.0], [8.5, 10.0]])
        gate = sc.Gate("G", ["start"], [hull], [hull], -1.0, 3.0)
        ctx = self.ctx(gates=[gate])
        out = sc.exit_options(ctx, self.pocket, self.cells, None, ctx.params)
        self.assertEqual(out["gates"], ["G"])
        gate_opt = out["options"]["gate"]
        self.assertIsNotNone(gate_opt)
        self.assertGreaterEqual(gate_opt["gate_distance_box_edge"], 0.5)
        self.assertEqual(gate_opt["position"][0], 6.0)            # the box edge (x + 2) is the closest valid one to the gate at x 8.5


class AnalyseTests(unittest.TestCase):
    def inputs(self):
        s = math.sqrt(0.5)
        return {
            "descriptor": {"key": "arena_test"},
            "bots": {"bot_spawns": [{"id": "arena_test-bot-core-01", "position": [30.0, 0.0, 10.0], "radius": 25, "categories": ["Bot", "BotPmc"]}],
                     "player_spawns": [{"id": "p", "position": [4.0, 0.0, 10.0], "radius": 5}, {"id": "q", "position": [76.0, 0.0, 10.0], "radius": 5}]},
            "modes": {"spawn_bounds": [{"name": "W", "side": "west", "center": [5, 0, 10], "size": [10, 6, 20], "yaw": 0.0, "active": True},
                                       {"name": "E", "side": "east", "center": [75, 0, 10], "size": [10, 6, 20], "yaw": 0.0, "active": True}],
                      "spawn_markers": {"west": [{"position": [4.0, 0.0, 10.0]}], "east": [{"position": [76.0, 0.0, 10.0]}]}},
            "exits": {"exits": [{"name": "X", "position": [20.0, 0.0, 10.0], "box_size": [4, 3, 4], "rotation": 0.0, "exfiltration_time": 8, "chance": 100, "type": "Individual"}]},
            "ai": {"core_points": [{"id": 1, "name": "AICore ID:1", "position": [40.0, 0.0, 15.0]}], "born_points": []},
            "doors": None, "sources": {"points": [{"source": "lasthero", "scene": "s", "path": "p", "name": "LH1", "position": [60.0, 0.0, 5.0], "yaw": 90}]},
        }

    def run_it(self, **p):
        verts, idx = grid_mesh(0, 80, 0, 20)
        mesh = oracle.mesh_from_triangulation(verts, idx)
        params = {"min_rows": 4, "max_rows": 6, "need": 2, "probe": False}
        params.update(p)
        return sc.analyse(self.inputs(), mesh, None, params, stem="test")

    def test_selection_invariants(self):
        rep = self.run_it()
        self.assertGreater(len(rep["selected_rows"]), 0)
        existing = np.array([[30.0, 0.0, 10.0]])
        rows = [r["row"] for r in rep["selected_rows"]]
        pos = np.array([r["position"] for r in rows])
        self.assertEqual(len(set(r["id"] for r in rows)), len(rows))
        self.assertTrue(all(r["radius"] == 25 and r["categories"] == ["Bot", "BotPmc"] for r in rows))
        self.assertTrue(rows[0]["id"].startswith("arena_test-bot-"))
        allp = np.concatenate([existing, pos])
        for i in range(len(allp)):
            for j in range(i + 1, len(allp)):
                self.assertGreaterEqual(float(np.linalg.norm(allp[i] - allp[j])), 7.0 - 1e-6)
        self.assertTrue((pos[:, 0] > 10.0 + 1.5 - 1e-6).all() and (pos[:, 0] < 70.0 - 1.5 + 1e-6).all())      # outside both pockets by the margin
        for pl in ([4.0, 0.0, 10.0], [76.0, 0.0, 10.0]):
            self.assertGreaterEqual(float(np.linalg.norm(pos - np.array(pl), axis=1).min()), 5.0)
        self.assertLessEqual(rep["total_rows_after"], 6)
        before = rep["coverage_before"]["3d"]["outside_pockets"]
        after = rep["coverage_after"]["3d"]["outside_pockets"]
        self.assertGreaterEqual(after["share_ge_need"], before["share_ge_need"])
        self.assertTrue(any(r["reason"] for r in rep["rejected"]) or rep["rejected"] == [])
        # the AI point and the sources point are candidates with their provenance
        srcs = {r["source"] for r in rep["rejected"]} | {r["provenance"]["source"] for r in rep["selected_rows"]}
        self.assertTrue({"ai_core", "lasthero"} & srcs)

    def test_a_rejected_native_candidate_names_its_reason(self):
        inputs = self.inputs()
        inputs["sources"] = {"points": [{"source": "shootout", "scene": "s", "path": "p", "name": "in-pocket", "position": [4.0, 0.0, 14.0]},
                                        {"source": "stage", "scene": "s", "path": "p", "name": "near-row", "position": [31.0, 0.0, 10.0]},
                                        {"source": "other", "scene": "s", "path": "p", "name": "off", "position": [200.0, 0.0, 10.0]}]}
        verts, idx = grid_mesh(0, 80, 0, 20)
        rep = sc.analyse(inputs, oracle.mesh_from_triangulation(verts, idx), None, {"probe": False, "min_rows": 2, "max_rows": 3, "need": 2}, stem="test")
        why = {r["name"]: r["reason"] for r in rep["rejected"]}
        self.assertTrue(why["in-pocket"].startswith("inside_pocket_margin"))
        self.assertEqual(why["near-row"], "spacing_existing")
        self.assertEqual(why["off"], "off_mesh")

    def test_output_is_deterministic(self):
        a = self.run_it()
        b = self.run_it()
        for r in (a, b):
            r.pop("_internal")
        ja = json.dumps(a, sort_keys=True, default=lambda o: o.tolist() if hasattr(o, "tolist") else str(o))
        jb = json.dumps(b, sort_keys=True, default=lambda o: o.tolist() if hasattr(o, "tolist") else str(o))
        self.assertEqual(ja, jb)

    def test_current_exit_metrics_and_png(self):
        rep = self.run_it()
        cur = rep["exits"]["current"][0]
        self.assertEqual(cur["name"], "X")
        self.assertFalse(cur["inside_pocket"])
        self.assertEqual(cur["nearest_pocket"], "W")
        self.assertAlmostEqual(cur["pocket_edge_distance_xz"], 10.0, places=2)
        out = Path(__file__).with_name("_spawn_coverage_test.png")
        try:
            sc.write_png(out, rep)
            self.assertTrue(out.read_bytes().startswith(b"\x89PNG"))
        finally:
            out.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
