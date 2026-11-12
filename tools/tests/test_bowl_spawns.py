"""Bowl spawn rows and exits (user request 2026-11-12: more spawn points, exits inside the team pockets).

tools/maps/bowl.bots.json, bowl.exits.json, bowl.modes.json and bowl.ai.json against each other, against the built NavMesh
and against the port's colliders. Bowl had no spawn test before this module; the rules below are the successors of the
sentence in the sidecar comments ("core 7 lies inside the west pocket and core 17 on the east gate line, both skipped").

The rules, in three layers.

1. Data only (the repository).
   R1  The 17 original Bot rows and the four Player rows are byte-identical to the 2026-10-18 release (sha256 of their
       canonical JSON); four Bot rows are appended after them; ArenaMode's donor row ``arena_bowl-bot-core-02`` keeps its floats
       and no id starts with ``arena-bowl-tdm-v1-`` / ``arena_bowl-tdm-v1-``.
   R2  Why the extra rows: ArenaMode only uses Bot rows at least 25 m (3D) from the player. Bowl was already saturated; the
       rows lift the minimum number of distant rows per walkable cell (checked on the mesh in layer 2).
   R3  One exit per team pocket (the two initial spawn bounds of bowl.modes.json, rotated boxes, yaw 319): the 4 x 3 x 4 m box,
       rotation 0, stands INSIDE its pocket (centre inside the footprint) and every Player row keeps 2.5 m to the box EDGE.
       The green signal flare then marks the team's spawn area. Names, sizes, times and types are unchanged.
   R5  The original rows keep their original rule (an AI core point, y + 0.05; the two core points inside or on the gate line of a
       pocket are not rows). The appended rows are held to the measured rule (see NEW_ROW_RULES): outside both footprints by
       10 m or more, 25 m or more from every Player row and pocket marker, 7 m or more from every other row, 10 m or more
       from every gate footprint (layer 3) and 2 m of border clearance (layer 2; the originals keep 0.82 m or more, core 8).
   R6  Exit positions are not pinned as literals here: the rules above are computed from the sidecars, so a later host-driven nudge
       touches the sidecar and the descriptor only (BowlMapTests mirrors descriptor == sidecar == base.json).
2. Arena-independent built NavMesh (work/build/bowl/arena_bowl_navmesh.bundle, ignored; skipped when missing): rows and exits
   stand on the play component, the new rows with the clearance above, and the 25 m coverage per cell has not fallen.
3. The port's colliders (work/extract/bowl-native-v2, ignored): every row passes the offline SpawnPlacementRepairs probe, the
   exit boxes hold no solid from floor + 0.3 to floor + 2.5 (moved gate parts excluded) and keep clear of the swept gates.

Real-data layers read ARENAMAPPORT_WORK_DIR (default: the repository's work/); a skip is a missing input, not a pass.
"""
import hashlib
import json
import math
import os
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
sys.path.insert(0, str(REPO / "tools" / "extract"))

WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR") or REPO / "work")
PACKAGE = WORK / "extract" / "bowl-native-v2"
NAVMESH_BUNDLE = WORK / "build" / "bowl" / "arena_bowl_navmesh.bundle"

import extract_mode_objects as M  # noqa: E402

ORIGINAL_BOT_ROWS_SHA256 = "37acaa1cbd535bd83faac131f802666dd498e56a03ad7defdac714bd275f770c"
PLAYER_ROWS_SHA256 = "f4db5062b17d364e3a9c9cdf68c27cbd6ca726416cb95d4a42f73485f90527f2"
SKIPPED_CORE_POINTS = (7, 17)                      # inside the west pocket / on the east gate line (2026-10-18)
ORIGINAL_CORE_POINTS = tuple(n for n in range(1, 20) if n not in SKIPPED_CORE_POINTS)
NEW_ROWS = {                                        # appended 2026-11-12, in this order (decisions of the lead)
    "arena_bowl-bot-born-16": [21.0, 0.1, 4.0],
    "arena_bowl-bot-born-27": [28.0, 0.1, -64.0],
    "arena_bowl-bot-fill-01": [36.0, 0.11, -48.0],
    "arena_bowl-bot-fill-02": [-40.0, 0.1, -27.0],
}
DONOR_ROW = ("arena_bowl-bot-core-02", [2.5, 0.11, -21.1])
# The measured rule of the appended rows (R5): the decided rows satisfy it with a small margin (measured in brackets).
NEW_ROW_RULES = {
    "outside_footprints": 10.0,    # horizontal distance outside both pocket footprints (15.0)
    "player_row": 25.0,            # 3D distance to every Player row (30.0)
    "pocket_marker": 25.0,         # XZ distance to every pocket marker (27.0)
    "other_row": 7.0,              # 3D distance to every other row (17.9; the originals keep 5.3)
    "gate": 10.0,                  # distance to every swept gate footprint (17+)
    "clearance": 2.0,              # border clearance on the built mesh (2.08)
}
ORIGINAL_CLEARANCE = 0.8                            # the originals sit on Arena's AI core points; core 8 keeps 0.82 m (unchanged since 2026-10-18)
EXIT_BOX = 4.0
EXIT_HEIGHT = 3.0
MIN_PLAYER_ROW_TO_BOX_EDGE = 2.5                    # the Equator booth precedent (docs/progress/equator-booth-spawns.md)
MIN_EXIT_CENTRE_INSIDE = 2.0                        # centre at least this far inside the footprint (measured 7.4 and 4.9)
MIN_EXIT_GATE_EDGE = 2.0                            # box edge to the swept footprint of the pocket's gates (measured 3.84 / 2.58)
DRAWING_BOUNDS = (-66.0, -82.0, 62.0, 11.0)         # tools/dynamicmaps profile of Bowl


def load(name):
    return json.loads((REPO / "tools" / "maps" / name).read_text(encoding="utf-8"))


def canonical_sha256(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def footprint_margin(bound, position):
    """Horizontal distance INSIDE a rotated spawn-bound box (negative = outside): the sidecar's own centre, size and rotation,
    the height ignored (a team box is 4.3 m tall, an exit or a row stands on the floor)."""
    flat = dict(bound, size=[bound["size"][0], 1.0e6, bound["size"][2]])
    return M.box_margin(flat, [position[0], bound["center"][1], position[2]])


def xz(p):
    return (p[0], p[2])


class SidecarData:
    @classmethod
    def load_data(cls):
        cls.bots, cls.exits, cls.map = load("bowl.bots.json"), load("bowl.exits.json"), load("bowl.json")
        cls.modes, cls.ai = load("bowl.modes.json"), load("bowl.ai.json")
        cls.players, cls.bot_points = cls.bots["player_spawns"], cls.bots["bot_spawns"]
        cls.original = cls.bot_points[:17]
        cls.new = cls.bot_points[17:]
        cls.bounds = cls.modes["spawn_bounds"]
        cls.markers = [m for side in cls.modes["spawn_markers"].values() for m in side]
        cls.core = {c["id"]: c["position"] for c in cls.ai["core_points"]}
        cls.exit_rows = cls.exits["exits"]


class BowlSidecarTests(SidecarData, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.load_data()

    # ------------------------------------------------------------------ R1
    def test_counts_and_ids(self):
        self.assertEqual((len(self.players), len(self.bot_points), len(self.new)), (4, 21, 4))
        self.assertEqual((len(self.bots["waves"]), len(self.bots["boss_waves"]), len(self.exit_rows)), (5, 4, 2))
        ids = [p["id"] for p in self.players + self.bot_points]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual([p["id"] for p in self.original], [f"arena_bowl-bot-core-{n:02d}" for n in ORIGINAL_CORE_POINTS])
        self.assertEqual([p["id"] for p in self.new], list(NEW_ROWS))
        self.assertIn("2026-10-18", self.bots["_comment"])
        self.assertIn("2026-11-12", self.bots["_comment"])
        self.assertIn("2026-10-18", self.exits["_comment"])
        self.assertIn("2026-11-12", self.exits["_comment"])

    def test_the_original_rows_are_byte_identical_and_the_new_rows_are_appended(self):
        # R1: id, position, radius, rotation and order of every row of the 2026-10-18 release
        self.assertEqual(canonical_sha256(self.original), ORIGINAL_BOT_ROWS_SHA256)
        self.assertEqual(canonical_sha256(self.players), PLAYER_ROWS_SHA256)
        by_id = {p["id"]: p for p in self.bot_points}
        self.assertEqual(by_id[DONOR_ROW[0]]["position"], DONOR_ROW[1])      # ArenaMode's pinned donor row
        for row in self.new:
            self.assertEqual(row["position"], NEW_ROWS[row["id"]], row["id"])
            self.assertEqual(set(row), {"id", "position", "radius", "categories"})
            self.assertEqual((row["radius"], row["categories"]), (25, ["Bot", "BotPmc"]))
        for row in self.players + self.bot_points:
            self.assertFalse(row["id"].startswith(("arena-bowl-tdm-v1-", "arena_bowl-tdm-v1-")), row["id"])

    def test_original_rows_sit_on_their_ai_core_point(self):
        self.assertEqual(sorted(set(self.core) - set(ORIGINAL_CORE_POINTS)), list(SKIPPED_CORE_POINTS))
        for number, row in zip(ORIGINAL_CORE_POINTS, self.original):
            core = self.core[number]
            self.assertAlmostEqual(row["position"][0], core[0], delta=0.01, msg=row["id"])
            self.assertAlmostEqual(row["position"][2], core[2], delta=0.01, msg=row["id"])
            self.assertAlmostEqual(row["position"][1] - core[1], 0.05, delta=0.01, msg=row["id"])

    def test_schema_and_the_generated_base_json_fields(self):
        equator = load("equator.bots.json")
        self.assertEqual(set(self.bots), set(equator))
        self.assertEqual(self.bots["waves"], load("airpit.bots.json")["waves"])
        self.assertEqual(self.bots["boss_waves"], load("airpit.bots.json")["boss_waves"])
        self.assertEqual((self.bots["zone"], self.bots["max_bots"]), ("BotZone_Main", 8))

    # ------------------------------------------------------------------ R5 (data half)
    def test_the_original_rows_stay_out_of_both_pockets(self):
        for row in self.original:
            for bound in self.bounds:
                self.assertLess(footprint_margin(bound, row["position"]), -4.0, row["id"])     # core 16 keeps 4.2 m, the rest 20 m+
        # the two skipped core points: 7 lies inside the west (south) pocket, 17 sits on the east pocket's gate line (within 4 m of the footprint)
        self.assertGreater(max(footprint_margin(b, self.core[7]) for b in self.bounds), 0.0)
        self.assertGreater(max(footprint_margin(b, self.core[17]) for b in self.bounds), -4.0)

    def test_the_new_rows_follow_the_measured_rule(self):
        limit = NEW_ROW_RULES
        for row in self.new:
            p = row["position"]
            for bound in self.bounds:
                self.assertLessEqual(footprint_margin(bound, p), -limit["outside_footprints"], f"{row['id']} / {bound['side']}")
            for player in self.players:
                self.assertGreaterEqual(math.dist(p, player["position"]), limit["player_row"], f"{row['id']} / {player['id']}")
            for marker in self.markers:
                self.assertGreaterEqual(math.dist(xz(p), xz(marker["position"])), limit["pocket_marker"], f"{row['id']} / {marker['name']}")
            for other in self.bot_points:
                if other is not row:
                    self.assertGreaterEqual(math.dist(p, other["position"]), limit["other_row"], f"{row['id']} / {other['id']}")

    def test_the_new_rows_come_from_born_points_or_fill_points_and_are_spread(self):
        # born points p1 (16) and p1 (27) are moved by 1.4 to 1.7 m from the authored point; the fill points have no marker within 6 m
        new_xz = [xz(r["position"]) for r in self.new]
        for i, a in enumerate(new_xz):
            for b in new_xz[i + 1:]:
                self.assertGreater(math.dist(a, b), 15.0)      # closest pair: fill-01 / born-27, 17.9 m
        for row in self.new:
            nearest_old = min(math.dist(row["position"], o["position"]) for o in self.original)
            self.assertGreater(nearest_old, 15.0, row["id"])

    # ------------------------------------------------------------------ R3
    def test_exit_names_sizes_times_and_types_are_unchanged(self):
        self.assertEqual([e["name"] for e in self.exit_rows], ["Bowl BTR Gate", "Bowl Army Gate"])
        for e in self.exit_rows:
            self.assertEqual((e["box_size"], e["rotation"], e["exfiltration_time"], e["chance"], e["type"]),
                             ([EXIT_BOX, EXIT_HEIGHT, EXIT_BOX], 0.0, 8, 100, "Individual"), e["name"])

    def test_each_pocket_holds_exactly_one_exit_box_centre(self):
        self.assertEqual(len(self.bounds), 2)
        for bound in self.bounds:
            inside = [e["name"] for e in self.exit_rows if footprint_margin(bound, e["position"]) >= MIN_EXIT_CENTRE_INSIDE]
            self.assertEqual(len(inside), 1, f"{bound['side']} pocket holds {inside}")
        # the BTR gate belongs to the east (north) pocket, the Army gate to the west (south) one: the names follow the gates
        north = next(b for b in self.bounds if b["side"] == "north")
        south = next(b for b in self.bounds if b["side"] == "south")
        by_name = {e["name"]: e["position"] for e in self.exit_rows}
        self.assertGreaterEqual(footprint_margin(north, by_name["Bowl BTR Gate"]), MIN_EXIT_CENTRE_INSIDE)
        self.assertGreaterEqual(footprint_margin(south, by_name["Bowl Army Gate"]), MIN_EXIT_CENTRE_INSIDE)

    def test_the_whole_box_lies_inside_its_pocket_footprint(self):
        # stronger than the centre rule, true for both boxes: every corner of the 4 x 4 m box is inside the footprint
        for e in self.exit_rows:
            x, y, z = e["position"]
            for bound in self.bounds:
                if footprint_margin(bound, e["position"]) > 0:
                    for dx, dz in ((-2, -2), (-2, 2), (2, -2), (2, 2)):
                        self.assertGreater(footprint_margin(bound, [x + dx, y, z + dz]), 0.0, f"{e['name']} corner {dx},{dz}")

    def test_player_rows_keep_two_and_a_half_metres_to_the_box_edge(self):
        for e in self.exit_rows:
            x, _, z = e["position"]
            for player in self.players:
                px, _, pz = player["position"]
                edge = math.hypot(max(abs(px - x) - EXIT_BOX / 2, 0.0), max(abs(pz - z) - EXIT_BOX / 2, 0.0))
                self.assertGreaterEqual(edge, MIN_PLAYER_ROW_TO_BOX_EDGE, f"{player['id']} starts inside or next to {e['name']}")

    def test_no_bot_row_stands_in_an_exit_box(self):
        for e in self.exit_rows:
            for row in self.bot_points:
                self.assertGreater(max(abs(row["position"][0] - e["position"][0]), abs(row["position"][2] - e["position"][2])), EXIT_BOX / 2 + 5.0,
                                   f"{row['id']} / {e['name']}")

    def test_the_exits_lie_inside_the_dynamicmaps_drawing(self):
        x0, z0, x1, z1 = DRAWING_BOUNDS
        for e in self.exit_rows:
            self.assertTrue(x0 + 2.0 < e["position"][0] < x1 - 2.0 and z0 + 2.0 < e["position"][2] < z1 - 2.0, e["name"])

    def test_the_old_exit_rule_is_retired_in_the_comments(self):
        # the retired rule of 2026-10-18 must not be stated as current any more; its history sits behind a dated "OLD positions"
        comment = self.exits["_comment"]
        for phrase in ("nobody spawns inside a trigger box", "10 m from every Player row", "beyond the gates"):
            self.assertNotIn(phrase, comment)
        self.assertEqual(comment.count("one gate beyond each pocket"), 1)
        self.assertIn("OLD positions (one gate beyond each pocket)", comment)
        self.assertIn("INSIDE its pocket", comment)
        self.assertIn("Host-checked on GPU-2 2026-11-12", comment)


def mesh_available():
    try:
        import numpy  # noqa: F401
        import UnityPy  # noqa: F401
    except ImportError:
        return False
    return NAVMESH_BUNDLE.is_file()


@unittest.skipUnless(mesh_available(), "no built Bowl NavMesh bundle (work/build/bowl) or no numpy/UnityPy")
class BowlBuiltMeshTests(SidecarData, unittest.TestCase):
    """Layer 2: the port's own built NavMesh."""

    @classmethod
    def setUpClass(cls):
        import numpy as np
        import navmesh_diff
        import spawn_coverage as sc
        cls.np, cls.sc = np, sc
        cls.load_data()
        cls.mesh = navmesh_diff.load_bundle_mesh(NAVMESH_BUNDLE)
        cls.play = sc.PlayMesh(cls.mesh, [r["position"] for r in cls.original])

    def test_every_row_and_exit_stands_on_the_play_component(self):
        self.assertEqual(self.play.rows_on_component, 17)
        rows = [(r["id"], r["position"]) for r in self.bot_points + self.players] + [(e["name"], e["position"]) for e in self.exit_rows]
        poly, surface = self.play.locate([p for _, p in rows], 1.0)
        for (name, position), pid, sy in zip(rows, poly, surface):
            self.assertGreaterEqual(pid, 0, f"{name} is not on the play component")
            self.assertLessEqual(abs(sy - position[1]), 0.2, f"{name} is {sy - position[1]:+.2f} m off the mesh surface")

    def test_clearance_of_the_rows(self):
        np = self.np
        for rows, need in ((self.original, ORIGINAL_CLEARANCE), (self.new, NEW_ROW_RULES["clearance"])):
            clearance = self.play.clearance(np.array([r["position"] for r in rows]))
            for row, c in zip(rows, clearance):
                self.assertGreaterEqual(c, need, f"{row['id']} keeps {c:.2f} m")

    def test_the_exit_centres_keep_clearance_and_the_boxes_stand_on_the_mesh(self):
        np = self.np
        for e in self.exit_rows:
            self.assertGreaterEqual(float(self.play.clearance(np.array([e["position"]]))[0]), 2.0, e["name"])
            x, y, z = e["position"]
            corners = np.array([[x + dx, y, z + dz] for dx, dz in ((-2, -2), (-2, 2), (2, -2), (2, 2))])
            poly, _ = self.play.locate(corners, 1.2)
            near_edge = self.play.clearance(corners) <= 0.5
            self.assertTrue(np.all((poly >= 0) | near_edge), f"{e['name']}: a corner is off the mesh")

    def test_the_25_metre_coverage_has_not_fallen(self):
        np = self.np
        cells, _ = self.play.cells(2.0)
        inside = np.zeros(len(cells), bool)
        for bound in self.bounds:
            inside |= self.sc.Pocket.from_bound(bound).contains(cells, with_y=False)
        keep = ~inside
        rows = np.array([r["position"] for r in self.bot_points])
        old = np.array([r["position"] for r in self.original])

        def distant(rs):
            d = np.sqrt(((cells[keep][:, None, :] - rs[None, :, :]) ** 2).sum(axis=2))
            return (d > 25.0).sum(axis=1)
        before, after = distant(old), distant(rows)
        self.assertGreaterEqual(int(before.min()), 8)                         # saturated before: every cell had 8 distant rows
        self.assertTrue(np.all(after >= before))                              # rows only add
        self.assertGreater(int(np.median(after)), int(np.median(before)))     # and the median rose (15 -> 17)
        self.assertGreaterEqual(int(after.min()), 10)


@unittest.skipUnless((PACKAGE / "scenes").is_dir() and (PACKAGE / "meshes").is_dir() and mesh_available(),
                     "no bowl-native-v2 package, built NavMesh, numpy or UnityPy")
class BowlPlacementTests(SidecarData, unittest.TestCase):
    """Layer 3: the port's own colliders through the offline SpawnPlacementRepairs.IsValid and the gate footprints."""

    @classmethod
    def setUpClass(cls):
        import numpy as np
        import port_colliders
        import spawn_coverage as sc
        import spawn_placement_probe as probe
        cls.np, cls.sc = np, sc
        cls.load_data()
        cls.cset = port_colliders.load_package_colliders(PACKAGE)
        doors = load("bowl.doors.json")
        port_colliders.mark_barriers(cls.cset, list(cls.map.get("opened_barriers") or []), port_colliders.load_door_rows(REPO / "tools" / "maps" / "bowl.doors.json"))
        cls.probe = probe.PlacementProbe.from_collider_set(cls.cset)
        # the solid index of the exit boxes: like spawn_coverage.analyse, the moved parts of opened barriers and the runtime-nonsolid
        # objects (spawn bounds, markers) are not solid
        keep = np.array([not (probe.runtime_nonsolid(o.names) or o.barrier_blocked) for o in cls.cset.owners], bool)
        mask = keep[cls.cset.tri_owner]
        cls.solids = sc.SolidIndex(cls.cset.tris[mask], cls.cset.tri_owner[mask], [o.path for o in cls.cset.owners])
        cls.gates, cls.gate_notes = sc.build_gates(cls.map, doors, cls.cset)

    def test_every_row_stands_on_a_floor_and_passes_the_placement_probe(self):
        for p in self.players + self.bot_points:
            verdict = self.probe.check(p["position"])
            with self.subTest(row=p["id"]):
                self.assertTrue(verdict.valid, f"ground {verdict.ground_ok}, ceiling {verdict.ceiling}, blocked {verdict.blocked[:2]}")
                self.assertLess(abs(verdict.floor_y - p["position"][1]), 0.2)

    def test_the_new_rows_keep_ten_metres_from_every_gate(self):
        self.assertTrue(self.gates)
        np = self.np
        for row in self.new:
            d = float(self.sc.min_gate_distance(self.gates, np.array([row["position"]]), swept=True)[0])
            self.assertGreaterEqual(d, NEW_ROW_RULES["gate"], f"{row['id']} is {d:.1f} m from a swept gate footprint")

    def test_the_exit_boxes_hold_no_solid_above_the_floor(self):
        for e in self.exit_rows:
            x, y, z = e["position"]
            verdict = self.probe.check(e["position"])
            self.assertIsNotNone(verdict.floor_y, e["name"])
            self.assertLess(abs(verdict.floor_y - y), 0.25, e["name"])
            # the box is centred 1.0 m above the position (ExfilRepairs): floor - 0.5 .. floor + 2.5; the floor slab itself is below +0.3.
            # Exact triangle / box overlap (a bounding-box test flags a concrete barrier that only crosses the box's corner column).
            found = self.solids.solids_in_box([x, verdict.floor_y + 1.4, z], [EXIT_BOX, 2.2, EXIT_BOX])
            self.assertEqual(found, [], f"{e['name']}: solids inside the box: {found[:3]}")

    def test_the_exit_boxes_clear_the_swept_gate_footprints(self):
        for e in self.exit_rows:
            x, y, z = e["position"]
            d = self.sc.min_gate_box_distance(self.gates, x, y, z, EXIT_BOX / 2)
            self.assertGreaterEqual(d, MIN_EXIT_GATE_EDGE, f"{e['name']}: box edge {d:.2f} m from a swept gate")


if __name__ == "__main__":
    unittest.main()
