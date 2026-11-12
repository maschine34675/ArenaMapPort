"""tools/validate/spawn_coverage.py on synthetic meshes only (no work/ data, no Arena, no Unity)."""
from __future__ import annotations

import contextlib
import io
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

    def ctx(self, gates=(), reach=None, players=((-30.0, 0.0, 5.0),), **over):
        params = dict(sc.DEFAULTS)
        params.update(over)
        reach = reach or sc.Reach(self.play, [], [])
        return sc.Context(self.play, reach, [self.pocket], list(gates), np.array(players, float), np.array([[15.0, 0.0, 5.0]]), None, params)

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
        reach = sc.Reach(self.play, [gate], [(15.0, 0.0, 5.0)])
        near_seed = self.cells[:, 0] > 11.5       # the polygons that touch the gate (x 9..11) are removed with it
        cr = reach.cell_reachable(self.cpoly)
        self.assertTrue(cr[near_seed].all())
        self.assertFalse(cr[self.cells[:, 0] < 9.0].any())
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


# ---------------------------------------------------------------------------------------------------------------------
# regression tests, one group per defect of the tool's first six runs (D1 to D7) and the check mode (A1, A2)
# ---------------------------------------------------------------------------------------------------------------------
class Verdict:
    """A stand-in for spawn_placement_probe's verdict."""

    def __init__(self, valid, headroom=2.0):
        self.valid, self.ground_ok, self.headroom, self.blocked = valid, True, headroom, [("a", "b", "c")]


class StubProbe:
    def __init__(self, bad=()):
        self.bad = {tuple(b) for b in bad}
        self.asked = []

    def check(self, pos):
        self.asked.append(tuple(pos))
        return Verdict(tuple(pos) not in self.bad, headroom=0.5)


class ExitDefectTests(unittest.TestCase):
    def setUp(self):
        verts, idx = grid_mesh(0, 20, 0, 10)
        self.mesh = oracle.mesh_from_triangulation(verts, idx)
        self.play = sc.PlayMesh(self.mesh, [(15.0, 0.0, 5.0)])
        self.cells, self.cpoly = self.play.cells(1.0)
        self.pocket = sc.Pocket.from_bound({"name": "west", "side": "w", "center": [5, 0, 5], "size": [10, 4, 10], "yaw": 0.0})
        self.pocket.markers = np.array([[3.0, 0.0, 5.0], [7.0, 0.0, 5.0]])

    def ctx(self, players=((-30.0, 0.0, 5.0),), probe=None, **over):
        params = dict(sc.DEFAULTS)
        params.update(over)
        return sc.Context(self.play, sc.Reach(self.play, [], []), [self.pocket], [], np.array(players, float), np.array([[15.0, 0.0, 5.0]]), None, params, probe)

    def test_d1_the_solid_box_spans_floor_plus_0_3_to_plus_2_5(self):
        def solids_at(h):
            tri = np.array([[[4.5, h, 4.5], [5.5, h, 4.5], [4.5, h, 5.5]]], float)          # a flat triangle inside the box around (5, 0, 5)
            return sc.SolidIndex(tri, np.array([0]), ["P/solid"])
        for h, solid in ((0.1, False), (0.25, False), (0.35, True), (1.0, True), (2.4, True), (2.45, True), (2.6, False)):
            m = sc.exit_metrics(self.ctx(), self.pocket, (5.0, 0.0, 5.0), self.pocket.markers, solids_at(h))
            self.assertEqual(m["solid_free"], not solid, f"triangle {h} m above the floor point")

    def test_d2_a_player_row_near_the_box_edge_makes_the_cell_invalid(self):
        ctx = self.ctx(players=((2.0, 0.0, 5.0),))                       # box (5, 5) spans x 3..7: the row is 1.0 m from the edge
        m = sc.exit_metrics(ctx, self.pocket, (5.0, 0.0, 5.0), self.pocket.markers, None)
        self.assertAlmostEqual(m["nearest_player_row_box_edge"], 1.0)
        self.assertFalse(m["player_margin_ok"])
        self.assertFalse(m["valid"])
        far = self.ctx(players=((0.0, 0.0, 5.0),))                       # 3.0 m from the edge
        m = sc.exit_metrics(far, self.pocket, (5.0, 0.0, 5.0), self.pocket.markers, None)
        self.assertTrue(m["valid"])
        self.assertEqual(m["markers_in_box"], 2)                         # the two markers lie on the box edge: counted, a preference not a rule
        loose = self.ctx(players=((2.0, 0.0, 5.0),), exit_player_margin=0.5)
        self.assertTrue(sc.exit_metrics(loose, self.pocket, (5.0, 0.0, 5.0), self.pocket.markers, None)["valid"])

    def test_d2_no_option_keeps_a_player_row_inside_its_margin(self):
        ctx = self.ctx(players=((2.0, 0.0, 5.0),))
        out = sc.exit_options(ctx, self.pocket, self.cells, None, ctx.params)
        self.assertGreater(out["valid_cells"], 0)
        for name, opt in out["options"].items():
            if opt:
                self.assertGreaterEqual(opt["nearest_player_row_box_edge"], 2.5, name)

    def test_d7_standing_room_runs_on_every_exit_option_and_passes_over_a_failing_centre(self):
        base = self.ctx()
        out = sc.exit_options(base, self.pocket, self.cells, None, base.params)
        first = tuple(out["options"]["centre"]["position"])
        probe = StubProbe(bad=[first])
        ctx = self.ctx(probe=probe)
        out = sc.exit_options(ctx, self.pocket, self.cells, None, ctx.params)
        self.assertNotEqual(tuple(out["options"]["centre"]["position"]), first)
        self.assertEqual(out["standing_room_rejected"][0]["option"], "centre")
        self.assertIn("headroom", out["standing_room_rejected"][0]["reason"])
        self.assertGreaterEqual(ctx.probe_checks, 3)                     # far, centre (twice), at least


class ReachDefectTests(unittest.TestCase):
    def dumbbell(self):
        """Two 8 x 10 / 8 x 10 blocks joined by a strip 0.5 m wide (z 4.1..4.6) that holds no point of the 1 m lattice."""
        A = [(0, 0), (8, 0), (8, 4.1), (8, 4.6), (8, 10), (0, 10)]
        B = [(12, 0), (20, 0), (20, 10), (12, 10), (12, 4.6), (12, 4.1)]
        verts, idx = [], []

        def add(poly, centre):
            base = len(verts)
            verts.append((centre[0], 0.0, centre[1]))
            verts.extend((x, 0.0, z) for x, z in poly)
            n = len(poly)
            for i in range(n):
                idx.extend([base, base + 1 + i, base + 1 + (i + 1) % n])
        add(A, (4, 5))
        add(B, (16, 5))
        s0 = len(verts)
        verts.extend([(8, 0.0, 4.1), (12, 0.0, 4.1), (12, 0.0, 4.6), (8, 0.0, 4.6)])
        idx.extend([s0, s0 + 1, s0 + 2, s0, s0 + 2, s0 + 3])
        return oracle.mesh_from_triangulation(verts, idx)

    def test_d3_the_flood_runs_on_polygons_so_a_strip_between_lattice_points_connects(self):
        mesh = self.dumbbell()
        self.assertEqual(int(mesh.labels.max()), 0)                       # one component: the welding worked
        play = sc.PlayMesh(mesh, [(4.0, 0.0, 5.0)])
        leaf = np.array([[19.8, 9.0], [19.9, 9.0], [19.9, 10.0], [19.8, 10.0]])          # a leaf in the far corner: it blocks only the polygons it touches
        gate = sc.Gate("G", ["held"], [leaf], [leaf], -1.0, 3.0)
        reach = sc.Reach(play, [gate], [(4.0, 0.0, 5.0)])
        self.assertTrue(reach.active)
        self.assertTrue(reach.query((16.0, 0.0, 2.0)))                    # across the strip from the seed
        self.assertTrue(reach.query((2.0, 0.0, 8.0)))
        self.assertFalse(reach.query((19.85, 0.0, 9.5)))                  # inside the gate's own polygons
        cells, cpoly = play.cells(1.0)
        reachable = reach.cell_reachable(cpoly)
        self.assertTrue(reachable[(cells[:, 0] < 15.0) & (cells[:, 2] < 8.0)].all())              # every cell west of the gate is reachable, none "unreachable" by the lattice

    def test_d3_a_held_gate_cuts_the_mesh_in_two(self):
        verts, idx = grid_mesh(0, 20, 0, 10)
        mesh = oracle.mesh_from_triangulation(verts, idx)
        play = sc.PlayMesh(mesh, [(15.0, 0.0, 5.0)])
        leaf = np.array([[9.95, 0.0], [10.05, 0.0], [10.05, 10.0], [9.95, 10.0]])
        gate = sc.Gate("G", ["held"], [leaf], [leaf], -1.0, 3.0)
        reach = sc.Reach(play, [gate], [(15.0, 0.0, 5.0)])
        self.assertFalse(reach.query((3.0, 0.0, 5.0)))
        self.assertTrue(reach.query((17.0, 0.0, 5.0)))

    def test_d3_a_thin_leaf_crossing_a_big_polygon_blocks_it(self):
        verts, idx = grid_mesh(0, 20, 0, 30, step=10.0)
        mesh = oracle.mesh_from_triangulation(verts, idx)
        seg = sc.hull2d([(-1.0, 5.0), (21.0, 5.0)])                       # no end point inside any polygon; it crosses the lowest row of polygons
        near = sc.polygons_near_hulls(mesh, [seg], -1.0, 3.0, 0.0)
        centroids = mesh.verts[mesh.poly_verts[:, :3]].mean(axis=1)
        self.assertTrue(near[centroids[:, 2] < 10.0].all())
        self.assertFalse(near[centroids[:, 2] > 20.0].any())
        self.assertFalse(sc.polygons_near_hulls(mesh, [seg], 10.0, 12.0, 0.0).any())   # another height band

    def test_d5_without_a_held_gate_a_point_off_the_mesh_is_not_reachable(self):
        verts, idx = grid_mesh(0, 20, 0, 10)
        v2, i2 = grid_mesh(30, 32, 0, 4)
        mesh = oracle.mesh_from_triangulation(verts + v2, idx + [i + len(verts) for i in i2])
        play = sc.PlayMesh(mesh, [(10.0, 0.0, 5.0)])
        reach = sc.Reach(play, [], [])
        self.assertFalse(reach.active)
        self.assertTrue(reach.query((10.0, 0.0, 5.0)))
        self.assertFalse(reach.query((25.0, 0.0, 5.0)))                   # off the mesh
        self.assertFalse(reach.query((31.0, 0.0, 2.0)))                   # another component
        self.assertFalse(reach.query((10.0, 6.0, 5.0)))                   # 6 m above the surface


class GateDefectTests(unittest.TestCase):
    class Scene:
        def world(self, key):
            m = np.eye(4)
            m[:3, 3] = (-13.0, 0.0, 71.0)
            return m

    def cset(self):
        from types import SimpleNamespace
        return SimpleNamespace(scenes={"S": self.Scene()}, owners=[], tris=np.zeros((0, 3, 3)))

    def build(self, rows):
        import port_colliders as pcol
        from unittest import mock
        desc = {"held_gates": ["PASSPORT_CONTROL_EMISSIVE"]}
        with mock.patch.object(pcol, "find_all_instances", return_value=[("S", 1)]):
            return sc.build_gates(desc, {"poses": rows}, self.cset())

    def test_d4_a_lamp_group_is_no_gate(self):
        rows = [{"barrier": "PASSPORT_CONTROL_EMISSIVE", "part": "lamp", "node": "n", "switch": {"closed": False, "open": True}, "closed": {}, "open": {}}]
        gates, notes = self.build(rows)
        self.assertEqual(gates, [])
        self.assertTrue(any("lamp group" in n for n in notes))

    def test_d4_a_collider_less_group_without_any_door_row_is_no_gate_either(self):
        # AirPit's PASSPORT_CONTROL_EMISSIVE has no door row at all and no collider: the first cut made a 3 m disc of it
        gates, notes = self.build([])
        self.assertEqual(gates, [])
        self.assertTrue(any("without a collider" in n and "not a gate" in n for n in notes))

    def test_d4_the_no_package_fallback_skips_switch_rows(self):
        rows = [{"barrier": "B", "at": [1.0, 0.0, 2.0], "switch": {"closed": False, "open": True}},
                {"barrier": "B", "at": [5.0, 0.0, 6.0]}]
        gates = sc.gates_from_door_rows({"held_gates": ["B"]}, {"poses": rows})
        self.assertEqual(len(gates), 1)
        self.assertEqual(gates[0].closed[0][:, 0].mean(), 5.0)


class IdAndCandidateDefectTests(unittest.TestCase):
    def test_d6_tags_come_from_the_source(self):
        self.assertEqual(sc.source_tag("ai_core", "AICore ID:12 CG:17"), "core-12")
        self.assertEqual(sc.source_tag("ai_core", "AICore ID:2"), "core-02")
        self.assertEqual(sc.source_tag("ai_born", "p1 (21)"), "born-21")
        self.assertEqual(sc.source_tag("ai_born", "p1"), "born-00")
        self.assertEqual(sc.source_tag("lasthero", "Usec house (4)"), "lasthero-04")
        self.assertEqual(sc.source_tag("stage", "Bear house (46)"), "stage-bear-46")
        self.assertEqual(sc.source_tag("stage", "Usec house (3)"), "stage-usec-03")
        self.assertEqual(sc.source_tag("shootout", "Cover (7)"), "shootout-07")

    def ai(self, **extra):
        a = {"core_points": [{"id": 12, "name": "AICore ID:12 CG:17", "position": [10.0, 0.0, 10.0]},
                             {"id": 3, "name": "AICore ID:3 CG:5", "position": [30.0, 0.0, 10.0]},
                             {"id": 3, "name": "AICore ID:3 CG:5", "position": [30.0, 0.0, 10.0]}],
             "born_points": [{"name": "p1 (21)", "position": [50.0, 0.0, 10.0]}]}
        a.update(extra)
        return a

    def test_d6_d7_ids_duplicates_and_dropped_ids(self):
        params = dict(sc.DEFAULTS)
        cands, pre = sc.gather_candidates(self.ai(), None, np.zeros((0, 3)), params, "arena_t", ())
        self.assertEqual(sorted(c["id"] for c in cands), ["arena_t-bot-born-21", "arena_t-bot-core-03", "arena_t-bot-core-12"])
        self.assertEqual([why for _c, why in pre if why.startswith("duplicate_of_an_earlier_candidate")].__len__(), 1)     # D7: the AI scenes repeat points
        params["dropped_ids"] = ("arena_t-bot-core-12",)
        cands, pre = sc.gather_candidates(self.ai(), None, np.zeros((0, 3)), params, "arena_t", ())
        self.assertIn("dropped_on_purpose(arena_t-bot-core-12)", [why for _c, why in pre])
        self.assertNotIn("arena_t-bot-core-12", [c["id"] for c in cands])
        cands, pre = sc.gather_candidates(self.ai(), None, np.zeros((0, 3)), dict(sc.DEFAULTS), "arena_t", ("arena_t-bot-core-03",))
        self.assertIn("duplicate_id(arena_t-bot-core-03)", [why for _c, why in pre])

    def test_d6_two_sources_with_the_same_derived_id_are_refused_not_renamed(self):
        src = {"points": [{"source": "lasthero", "name": "Usec house (4)", "position": [10.0, 0.0, 10.0]},
                          {"source": "lasthero", "name": "Usec house (4)", "scene": "other", "position": [40.0, 0.0, 10.0]}]}
        cands, pre = sc.gather_candidates(None, src, np.zeros((0, 3)), dict(sc.DEFAULTS), "arena_t", ())
        self.assertEqual([c["id"] for c in cands], ["arena_t-bot-lasthero-04"])
        self.assertEqual([why for _c, why in pre], ["duplicate_id(arena_t-bot-lasthero-04)"])

    def test_d6_fill_ids_skip_existing_ids_and_native_ids_follow_the_source(self):
        t = AnalyseTests()
        inputs = t.inputs()
        inputs["bots"]["bot_spawns"][0]["id"] = "arena_test-bot-fill-01"
        inputs["ai"]["core_points"][0]["name"] = "AICore ID:12 CG:3"
        verts, idx = grid_mesh(0, 80, 0, 20)
        rep = sc.analyse(inputs, oracle.mesh_from_triangulation(verts, idx), None, {"probe": False, "min_rows": 4, "max_rows": 6, "need": 2}, stem="test")
        ids = [r["row"]["id"] for r in rep["selected_rows"]]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertNotIn("arena_test-bot-fill-01", ids)
        fills = [i for i in ids if "-fill-" in i]
        self.assertEqual(fills, [f"arena_test-bot-fill-{n:02d}" for n in range(2, 2 + len(fills))])
        for r in rep["selected_rows"]:
            if r["provenance"]["source"] == "ai_core":
                self.assertEqual(r["row"]["id"], "arena_test-bot-core-12")
            if r["provenance"]["source"] == "lasthero":
                self.assertEqual(r["row"]["id"], "arena_test-bot-lasthero-00")


class CandidateRuleDefectTests(unittest.TestCase):
    def setUp(self):
        verts, idx = grid_mesh(0, 20, 0, 4)
        self.play = sc.PlayMesh(oracle.mesh_from_triangulation(verts, idx), [(10.0, 0.0, 2.0)])

    def ctx(self, **over):
        params = dict(sc.DEFAULTS)
        params.update(over)
        return sc.Context(self.play, sc.Reach(self.play, [], []), [], [], np.zeros((0, 3)), np.zeros((0, 3)), None, params)

    def cand(self, pos, y_authored=0.3):
        return {"source": "ai_core", "name": "AICore ID:1", "scene": "ai", "path": "core_points", "position": list(pos), "native": True,
                "source_position": [pos[0], y_authored, pos[2]]}

    def test_d7_the_nudge_takes_the_best_spot_within_range_not_the_first_that_passes(self):
        rec, why = sc.evaluate_candidate(self.ctx(), self.cand((10.0, 0.0, 0.2)))
        self.assertIsNone(why)
        # the first spot that passes (r 1.5, z about 1.59, clearance 1.59) is not taken; the best within 2 m is r 2.0 at 67.5 degrees: z 2.05, clearance 1.95
        self.assertAlmostEqual(rec["position"][2], 2.05, places=2)
        self.assertAlmostEqual(rec["metrics"]["clearance"], 1.95, places=2)
        self.assertAlmostEqual(rec["metrics"]["nudged_m"], 2.0, places=2)

    def test_d7_both_heights_are_reported(self):
        rec, _ = sc.evaluate_candidate(self.ctx(), self.cand((10.0, 0.0, 2.0)))
        m = rec["metrics"]
        self.assertEqual((m["mesh_y"], m["authored_y"], m["dy_mesh_minus_authored"]), (0.0, 0.3, -0.3))

    def test_d7_a_keep_out_circle_rejects_candidates(self):
        ctx = self.ctx(keep_out=((10.0, 2.0, 6.0),))
        rec, why = sc.evaluate_candidate(ctx, self.cand((14.0, 0.0, 2.0)))
        self.assertIsNone(rec)
        self.assertTrue(why.startswith("keep_out"))
        rec, why = sc.evaluate_candidate(ctx, self.cand((17.0, 0.0, 2.0)))
        self.assertIsNone(why)

    def test_d7_ties_prefer_no_nudge_then_more_clearance(self):
        nat = np.ones(3, bool)
        ones = np.array([5, 5, 5])
        zero = np.zeros(3)
        mind = np.array([9.0, 9.0, 9.0])
        best, _, _ = sc.pick_best(np.arange(3), ones, zero, zero, mind, nat, nudged=np.array([1.0, 0.0, 0.0]), clr=np.array([3.0, 1.5, 2.0]))
        self.assertEqual(best, 2)                         # an unmoved spot beats a nudged one even with less clearance; of the unmoved, more clearance
        best, _, _ = sc.pick_best(np.arange(3), ones, zero, zero, mind, nat, nudged=np.array([0.0, 0.0, 0.0]), clr=np.array([1.5, 2.0, 2.0]))
        self.assertEqual(best, 1)                         # clearance tie 1 and 2: the larger minimum distance (equal) then the lower index
        best, _, _ = sc.pick_best(np.arange(3), np.array([5, 6, 5]), zero, zero, mind, nat, nudged=np.array([0.0, 1.0, 0.0]), clr=np.array([1.5, 1.4, 3.0]))
        self.assertEqual(best, 1)                         # the gain still decides first


class CheckLayoutTests(unittest.TestCase):
    """check_layout on a synthetic 80 x 20 m map with a west pocket (x 0..10) and an east pocket (x 70..80)."""

    def base(self):
        return {
            "descriptor": {"key": "arena_test"},
            "bots": {"bot_spawns": [{"id": "d1", "position": [30.0, 0.0, 10.0], "radius": 25, "categories": ["Bot", "BotPmc"]},
                                    {"id": "old", "position": [40.0, 0.0, 0.1], "radius": 25, "categories": ["Bot", "BotPmc"]},
                                    {"id": "new1", "position": [50.0, 0.0, 10.0], "radius": 25, "categories": ["Bot", "BotPmc"]}],
                     "player_spawns": [{"id": "p1", "position": [3.0, 0.0, 3.0], "radius": 5}, {"id": "p2", "position": [77.0, 0.0, 17.0], "radius": 5}]},
            "modes": {"spawn_bounds": [{"name": "W", "side": "west", "center": [5, 0, 10], "size": [10, 6, 20], "yaw": 0.0, "active": True},
                                       {"name": "E", "side": "east", "center": [75, 0, 10], "size": [10, 6, 20], "yaw": 0.0, "active": True}],
                      "spawn_markers": {"west": [{"position": [4.0, 0.0, 10.0]}], "east": [{"position": [76.0, 0.0, 10.0]}]}},
            "exits": {"exits": [{"name": "XW", "position": [6.0, 0.0, 14.0], "box_size": [4, 3, 4], "rotation": 0.0, "exfiltration_time": 8, "chance": 100, "type": "Individual"},
                                {"name": "XE", "position": [74.0, 0.0, 6.0], "box_size": [4, 3, 4], "rotation": 0.0, "exfiltration_time": 8, "chance": 100, "type": "Individual"}]},
            "ai": None, "doors": None, "sources": None,
        }

    def run_check(self, inputs, **kw):
        verts, idx = grid_mesh(0, 80, 0, 20)
        mesh = oracle.mesh_from_triangulation(verts, idx)
        kw.setdefault("donor_ids", ["d1"])
        kw.setdefault("require_solid_check", False)      # the synthetic map has no collider package
        kw.setdefault("appended_since", "new1")          # the row 'old' stands 0.1 m from the border: membership only, like Bay5's old rows
        return sc.check_layout(inputs, mesh, None, {"probe": False}, stem="test", **kw)

    def violations(self, inputs, **kw):
        return self.run_check(inputs, **kw)["violations"]

    def test_a_good_layout_has_no_violation_and_prints_coverage(self):
        rep = self.run_check(self.base())
        self.assertEqual(rep["violations"], [])
        text = sc.format_check(rep)
        self.assertIn("0 violation(s)", text)
        self.assertIn("coverage 3d outside pockets", text)
        self.assertIn("plain-raid reachable True", text)

    def test_exit_outside_its_pocket_the_mouth_exception_and_its_tolerance(self):
        inp = self.base()
        inp["exits"]["exits"][0]["position"] = [20.0, 0.0, 10.0]
        v = self.violations(inp)
        self.assertTrue(any("XW" in x and "outside pocket" in x and "rule R3" in x for x in v), v)
        inp["exits"]["exits"][0]["position"] = [10.8, 0.0, 10.0]                           # 0.8 m outside the footprint
        self.assertTrue(any("XW" in x for x in self.violations(inp)))
        rep = self.run_check(inp, mouth_exits={"XW"})
        self.assertEqual(rep["violations"], [])
        self.assertTrue(any("listed mouth exit" in i for i in rep["info"]))
        # the tolerance is measured on the nearest box EDGE (Air Pit South Gate: centre 2.41 m out, edge 0.41 m out)
        inp["exits"]["exits"][0]["position"] = [12.4, 0.0, 10.0]                           # centre 2.4 m out, nearest edge 0.4 m out: allowed
        rep = self.run_check(inp, mouth_exits={"XW"})
        self.assertEqual(rep["violations"], [])
        self.assertTrue(any("nearest box edge 0.40 m" in i for i in rep["info"]), rep["info"])
        inp["exits"]["exits"][0]["position"] = [13.5, 0.0, 10.0]                           # nearest edge 1.5 m out: beyond the tolerance
        v = self.violations(inp, mouth_exits={"XW"})
        self.assertTrue(any("mouth tolerance" in x and "nearest box edge is 1.50" in x for x in v), v)

    def test_a_player_row_must_keep_two_and_a_half_metres_from_the_box_edge(self):
        inp = self.base()
        inp["bots"]["player_spawns"][0]["position"] = [2.0, 0.0, 14.0]                    # box 4..8: the row is 2.0 m from its edge
        v = self.violations(inp)
        self.assertTrue(any("Player row is 2.000 m from the box edge" in x for x in v), v)
        inp["bots"]["player_spawns"][0]["position"] = [1.5, 0.0, 14.0]                    # 2.5 m: allowed
        self.assertEqual(self.violations(inp), [])

    def test_one_exit_per_pocket(self):
        inp = self.base()
        inp["exits"]["exits"][1]["position"] = [6.0, 0.0, 6.0]
        v = self.violations(inp)
        self.assertTrue(any("pocket 'west/W' has 2 exits" in x for x in v), v)
        self.assertTrue(any("pocket 'east/E' has 0 exits" in x for x in v), v)

    def test_box_size_rotation_and_floor(self):
        inp = self.base()
        inp["exits"]["exits"][0]["box_size"] = [4, 3, 5]
        inp["exits"]["exits"][0]["rotation"] = 45.0
        inp["exits"]["exits"][1]["position"] = [74.0, 0.0, 30.0]                           # off the mesh
        v = self.violations(inp)
        self.assertTrue(any("box_size" in x for x in v), v)
        self.assertTrue(any("rotation" in x for x in v), v)
        self.assertTrue(any("no floor" in x for x in v), v)

    def test_ids_donors_and_forbidden_ids(self):
        inp = self.base()
        inp["bots"]["bot_spawns"][2]["id"] = "old"
        inp["bots"]["bot_spawns"][1]["id"] = "old"
        inp["bots"]["bot_spawns"].append({"id": "arena-test-tdm-v1-x", "position": [60.0, 0.0, 10.0], "radius": 25})
        v = self.violations(inp, donor_ids=["nope"])
        self.assertTrue(any("'old' appears 2 times" in x for x in v), v)
        self.assertTrue(any("-tdm-v1-" in x for x in v), v)
        self.assertTrue(any("donor row 'nope'" in x for x in v), v)
        self.assertEqual(self.run_check(self.base())["violations"], [])        # the default donor table has no entry for the stem 'test'

    def test_rows_off_the_play_component_are_violations_for_bot_and_player_rows(self):
        inp = self.base()
        inp["bots"]["bot_spawns"][1]["position"] = [200.0, 0.0, 10.0]
        inp["bots"]["player_spawns"][1]["position"] = [77.0, 0.0, 40.0]
        v = self.violations(inp)
        self.assertTrue(any("Bot row 'old'" in x and "not on the play component" in x for x in v), v)
        self.assertTrue(any("Player row 'p2'" in x for x in v), v)

    def test_clearance_applies_to_appended_rows_only_when_appended_since_is_given(self):
        inp = self.base()
        inp["bots"]["bot_spawns"][2]["position"] = [50.0, 0.0, 0.5]                        # new1: 0.5 m from the border
        v = self.violations(inp, appended_since="new1")
        self.assertTrue(any("Bot row 'new1'" in x and "clearance 0.50" in x for x in v), v)
        self.assertFalse(any("'old'" in x for x in v), v)                                   # old stands 0.1 m from the border: membership only
        self.assertFalse(any("Player row" in x for x in v), v)
        v = self.violations(inp, appended_since=None)                                       # nothing grandfathered: every row is measured
        self.assertTrue(any("'old'" in x for x in v) and any("'new1'" in x for x in v), v)
        v = self.violations(inp, appended_since=None, grandfathered=["old", "new1"])
        self.assertEqual(v, [])

    def test_a_missing_appended_since_id_is_a_violation_not_a_silent_pass(self):
        inp = self.base()
        inp["bots"]["bot_spawns"][2]["position"] = [50.0, 0.0, 0.5]                        # would fail the clearance rule with the right id
        v = self.violations(inp, appended_since="new1x")
        self.assertTrue(any("--appended-since 'new1x' is not among the Bot rows" in x for x in v), v)
        rep = self.run_check(inp, appended_since="new1")
        self.assertEqual(rep["rows_checked_for_clearance"], 1)
        self.assertEqual(len(rep["violations"]), 1)

    def test_donor_rows_are_pinned_by_exact_floats(self):
        inp = self.base()
        saved = dict(sc.DONOR_ROWS)
        sc.DONOR_ROWS["test"] = {"d1": ([30.0, 0.0, 10.0], 25)}
        try:
            self.assertEqual(self.run_check(inp, donor_ids=None)["violations"], [])
            inp["bots"]["bot_spawns"][0]["position"] = [30.5, 0.0, 10.0]
            v = self.violations(inp, donor_ids=None)
            self.assertTrue(any("donor row 'd1'" in x and "changed" in x for x in v), v)
            inp["bots"]["bot_spawns"][0]["position"] = [30.0, 0.0, 10.0]
            inp["bots"]["bot_spawns"][0]["radius"] = 30
            self.assertTrue(any("donor row 'd1'" in x for x in self.violations(inp, donor_ids=None)))
        finally:
            sc.DONOR_ROWS.clear()
            sc.DONOR_ROWS.update(saved)

    def test_donor_pins_of_the_six_maps_match_the_committed_sidecars_when_unchanged(self):
        self.assertEqual(sorted(sc.DONOR_ROWS), sorted(sc.DONOR_IDS))
        for stem, rows in sc.DONOR_ROWS.items():
            self.assertEqual(len(rows), 1, stem)

    def test_the_player_margin_is_compared_unrounded(self):
        inp = self.base()
        inp["bots"]["player_spawns"][0]["position"] = [1.5025, 0.0, 14.0]                  # 2.4975 m from the box edge: prints as 2.50, still a violation
        v = self.violations(inp)
        self.assertTrue(any("Player row is 2.498" in x for x in v), v)

    def test_without_a_package_the_unevaluated_solid_rule_is_a_violation(self):
        v = self.violations(self.base(), require_solid_check=True)
        self.assertTrue(any("solid check was not run" in x for x in v), v)
        self.assertEqual(self.violations(self.base(), require_solid_check=False), [])
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            self.assertEqual(sc.main(["--stem", "equator", "--check"]), 2)
        self.assertIn("--no-solid-check", err.getvalue())

    def test_the_parser_takes_the_new_options(self):
        a = sc.build_parser().parse_args(["--stem", "s", "--check", "--appended-since", "x", "--mouth-exit", "A", "--mouth-exit", "B", "--keep-out", "1,2,3",
                                          "--grandfathered-ids", "a,b", "--grandfathered-ids", "c", "--maps-dir", "m", "--dropped-ids", "q"])
        self.assertTrue(a.check)
        self.assertEqual((a.appended_since, a.mouth_exit, a.keep_out, a.maps_dir), ("x", ["A", "B"], ["1,2,3"], "m"))
        self.assertEqual(sc._csv(a.grandfathered_ids), ["a", "b", "c"])
        self.assertEqual(a.exit_player_margin, 2.5)
        self.assertEqual(a.mouth_tolerance, 1.0)
        self.assertFalse(a.no_solid_check)


if __name__ == "__main__":
    unittest.main()
