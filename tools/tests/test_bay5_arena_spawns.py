"""Bay5 data contract; native reachability/standing room is tested separately."""
import hashlib
import importlib.util
import json
import math
import os
import sys
from pathlib import Path
import unittest

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("bay5_generator", REPO / "tools/server/make_base_json.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)
sys.path.insert(0, str(REPO / "tools" / "validate"))

# The ignored inputs of the main checkout; a git worktree has none, so ARENAMAPPORT_WORK_DIR points at them (same convention as the other tests).
WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
PACKAGE = WORK / "extract" / "bay5-native-v5"
BUILT_NAVMESH = WORK / "build" / "bay5" / "arena_bay5_navmesh.bundle"

# The ten Bot rows of the first release line (ArenaMode pins bot-west-centre by id and exact floats) stay in place and in order;
# the rows of 2026-11-12 are appended after them (rule R1 of the spawn-count work).
ORIGINAL_BOT_IDS = [
    "arena_bay5-bot-south-east", "arena_bay5-bot-north-west", "arena_bay5-bot-south-lane", "arena_bay5-bot-west-lane",
    "arena_bay5-bot-north-east-lane", "arena_bay5-bot-north-lane", "arena_bay5-bot-south-west-lane",
    "arena_bay5-bot-south-east-lane", "arena_bay5-bot-east-lane", "arena_bay5-bot-west-centre",
]
APPENDED_BOT_IDS = [
    "arena_bay5-bot-core-08", "arena_bay5-bot-core-07", "arena_bay5-bot-core-03",
    "arena_bay5-bot-core-06", "arena_bay5-bot-fill-01", "arena_bay5-bot-fill-02",
]
# R1 pin: sha256 of the canonical JSON ({"player": player_spawns, "bot": first ten bot_spawns}, sort_keys, compact separators) as
# they stood before 2026-11-12. This literal is the point of the test (rule R6): ArenaMode pins bot-west-centre by id and exact
# floats and the first release line's other rows are what players have spawned on; change it only on a deliberate decision.
ORIGINAL_ROWS_SHA256 = "229dd1dc1556e8b0bcf780099e847390394dbd4e3238de84444950748f525886"
EXIT_BOX = 4.0
EXIT_PLAYER_EDGE_MIN = 2.5          # a plain-raid spawn must not start inside a trigger (the Equator booth precedent)
BURST_ROWS_NEEDED = 5               # ArenaMode's Tactical mode: five Bot rows at least 25 m from wherever the leader stands


def pocket_footprints(modes):
    """(side, centre x, centre z, half x, half z, yaw degrees) of the initial spawn bound of each team pocket (bay5.modes.json)."""
    return [(b["side"], b["center"][0], b["center"][2], b["size"][0] / 2.0, b["size"][2] / 2.0, b["yaw"]) for b in modes["spawn_bounds"]]


def inside_footprint(foot, x, z):
    _, cx, cz, hx, hz, yaw = foot
    a = math.radians(yaw)
    dx, dz = x - cx, z - cz
    lx = dx * math.cos(a) - dz * math.sin(a)
    lz = dx * math.sin(a) + dz * math.cos(a)
    return abs(lx) <= hx and abs(lz) <= hz


def box_corners(position, rotation_degrees):
    a = math.radians(rotation_degrees)
    h = EXIT_BOX / 2.0
    return [(position[0] + sx * h * math.cos(a) - sz * h * math.sin(a), position[2] + sx * h * math.sin(a) + sz * h * math.cos(a))
            for sx in (-1, 1) for sz in (-1, 1)]


class Bay5ArenaSpawnTests(unittest.TestCase):
    def setUp(self):
        self.bots = json.loads((REPO / "tools/maps/bay5.bots.json").read_text(encoding="utf-8"))
        self.exits = json.loads((REPO / "tools/maps/bay5.exits.json").read_text(encoding="utf-8"))
        self.map = json.loads((REPO / "tools/maps/bay5.json").read_text(encoding="utf-8"))
        self.base = json.loads((REPO / "ArenaMapPort.Server/db/arena_bay5/base.json").read_text(encoding="utf-8"))
        self.modes = json.loads((REPO / "tools/maps/bay5.modes.json").read_text(encoding="utf-8"))

    def test_distant_burst_candidates_cover_the_source_combat_envelope(self):
        # Source's 2208 AI bearing points span x44.415..146.295,z-14.037..71.174.
        # All samples of this containing rectangle (including non-walkable cells)
        # must leave BURST_ROWS_NEEDED distinct choices for ArenaMode's 25 m burst rule. Until 2026-11-12 the rule was "three"
        # (the ten original rows give 6 at the worst sample); the six appended rows lift the worst sample to 9, and the rule is now
        # the five that Tactical needs from wherever its leader stands (spawn-count work, user request 2026-11-12).
        points = {tuple(p["position"]) for p in self.bots["bot_spawns"]}
        for x in range(44, 148):
            for z in range(-15, 73):
                self.assertGreaterEqual(sum(math.dist((x, -3.85, z), p) >= 25 for p in points), BURST_ROWS_NEEDED,
                                        f"Insufficient distant points at {x},{z}")

    def test_the_bot_rows_of_2026_11_12_are_appended_after_the_original_ten(self):
        ids = [p["id"] for p in self.bots["bot_spawns"]]
        self.assertEqual(ids, ORIGINAL_BOT_IDS + APPENDED_BOT_IDS)
        for sid in APPENDED_BOT_IDS:
            self.assertFalse(sid.startswith("arena_bay5-tdm-v1-"), sid)
        for point in self.bots["bot_spawns"][len(ORIGINAL_BOT_IDS):]:
            self.assertEqual(point["radius"], 25)
            self.assertEqual(point["categories"], ["Bot", "BotPmc"])
        self.assertEqual(len(self.bots["player_spawns"]), 4)

    def test_the_original_rows_are_byte_identical(self):
        # R1: id, position, radius, categories/sides and order of the four Player rows and the ten original Bot rows.
        canonical = json.dumps({"player": self.bots["player_spawns"], "bot": self.bots["bot_spawns"][:len(ORIGINAL_BOT_IDS)]},
                               sort_keys=True, separators=(",", ":"))
        self.assertEqual(hashlib.sha256(canonical.encode()).hexdigest(), ORIGINAL_ROWS_SHA256)

    def test_appended_rows_keep_their_distance_from_every_other_row_and_the_capture_point(self):
        # Measured on 2026-11-12: nearest other row 10.1 m (fill-02, which is also the nearest to a Player row), capture-point
        # light 12.5 m (fill-01), none in a pocket. The floors below leave a small margin.
        rows = self.bots["player_spawns"] + self.bots["bot_spawns"]
        capture = self.modes["capture_point"]["position"]
        for point in self.bots["bot_spawns"][len(ORIGINAL_BOT_IDS):]:
            others = [r for r in rows if r["id"] != point["id"]]
            self.assertGreaterEqual(min(math.dist(point["position"], r["position"]) for r in others), 10.0, point["id"])
            self.assertGreaterEqual(math.hypot(point["position"][0] - capture[0], point["position"][2] - capture[2]), 12.0, point["id"])
            for foot in pocket_footprints(self.modes):
                self.assertFalse(inside_footprint(foot, point["position"][0], point["position"][2]), f"{point['id']} stands in the {foot[0]} pocket")
            # Measured 25.3 m (core-03) and more from every opened gate part's parked footprint; the bots comment says "at least 15 m".
            for name, box in self.PARKED_FOOTPRINTS.items():
                gap = math.hypot(max(box[0] - point["position"][0], 0.0, point["position"][0] - box[1]),
                                 max(box[2] - point["position"][2], 0.0, point["position"][2] - box[3]))
                self.assertGreaterEqual(gap, 15.0, f"{point['id']} is within 15 m of {name}")

    def test_respawns_do_not_overlap_ai_arrivals(self):
        for player in self.bots["player_spawns"]:
            for bot in self.bots["bot_spawns"]:
                self.assertGreaterEqual(math.dist(player["position"], bot["position"]), 2)
        for side in ("Usec", "Bear"):
            points = [p for p in self.bots["player_spawns"] if "All" in p["sides"] or side in p["sides"]]
            self.assertTrue(any(p["position"][2] < 10 for p in points), side)
            self.assertTrue(any(p["position"][2] > 50 for p in points), side)

    def test_each_exit_box_stands_inside_its_team_pocket_clear_of_the_player_rows(self):
        # Rule R3 (2026-11-12; replaces "Player rows at least 10 m from an exit, exits outside the pockets"): one exit per team pocket,
        # its 4 x 3 x 4 m box inside the pocket's initial spawn-bound footprint (bay5.modes.json), so that the green signal flare marks
        # the team's spawn area in ArenaMode's team modes (extraction is disabled there). A plain-raid spawn must not start inside the
        # trigger: every Player row keeps EXIT_PLAYER_EDGE_MIN to the box EDGE. The positions live in the sidecar only; this tests the rule.
        footprints = pocket_footprints(self.modes)
        self.assertEqual(len(self.exits["exits"]), len(footprints))
        taken = set()
        for point in self.exits["exits"]:
            self.assertEqual(point["box_size"], [4.0, 3.0, 4.0])
            self.assertEqual(point["rotation"], 0.0)
            x, _, z = point["position"]
            homes = [f for f in footprints if inside_footprint(f, x, z)
                     and all(inside_footprint(f, cx, cz) for cx, cz in box_corners(point["position"], point["rotation"]))]
            self.assertEqual(len(homes), 1, f"{point['name']} does not stand wholly inside one pocket")
            self.assertNotIn(homes[0][0], taken, "two exits in one pocket")
            taken.add(homes[0][0])
            for player in self.bots["player_spawns"]:
                px, _, pz = player["position"]
                edge = math.hypot(max(abs(px - x) - EXIT_BOX / 2, 0.0), max(abs(pz - z) - EXIT_BOX / 2, 0.0))
                self.assertGreaterEqual(edge, EXIT_PLAYER_EDGE_MIN, f"{player['id']} would spawn inside {point['name']}")
        self.assertEqual(taken, {f[0] for f in footprints})

    def test_every_gate_arena_moves_is_an_opened_barrier_with_a_pose(self):
        # 2026-10-23 (docs/progress/equator-booth-spawns.md, user: "alle Tore aufmachen"): the warehouse rollers and the depot
        # frame (two instances behind one path). Door_01 (3) left the list on 2026-10-24 (user: Arena authors no motion and no sound
        # for it, a tarp with a carton stack stands in its doorway; it stays shut, a baked wall again). The pose table names exactly
        # these barriers.
        barriers = self.map["opened_barriers"]
        self.assertEqual(barriers, [
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)",
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)",
            "WAREHOUSE_DOORS (1)/One_man_Door",
            "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame",
        ])
        self.assertEqual(len(barriers), len(set(barriers)))
        doors = json.loads((REPO / "tools/maps/bay5.doors.json").read_text(encoding="utf-8"))
        self.assertEqual(sorted({pose["barrier"] for pose in doors["poses"]}), sorted(barriers))

    # World footprints (x0, x1, z0, z1) of every opened gate part at its OPEN pose, measured from the colliders of the
    # bay5-native-v5 package moved by bay5.doors.json (2026-10-23, the survey's depot numbers reproduce to 0.1 m): the depot leaves
    # after their swing, the roller slabs after their lift (xz does not change). Derived numbers only. Roller (3) stays shut since
    # 2026-10-24 and parks nowhere (its old footprint (129.3, 129.5, 41.0, 45.4) is a closed wall again).
    PARKED_FOOTPRINTS = {
        "depot 821 L leaf": (52.9, 55.3, 7.5, 8.6),
        "depot 821 R leaf": (46.2, 48.3, 7.5, 9.3),
        "depot 822 L leaf": (69.1, 70.9, -10.5, -8.6),
        "depot 822 R leaf": (69.1, 71.6, -4.1, -3.5),
        "roller (2)": (140.8, 145.2, 39.9, 40.1),
        "roller (4)": (129.3, 129.5, 45.9, 50.4),
        "One_man_Door": (129.3, 129.4, 68.3, 70.7),
    }

    def test_no_spawn_stands_where_an_opened_gate_part_parks(self):
        def gap(position, box):
            dx = max(box[0] - position[0], 0.0, position[0] - box[1])
            dz = max(box[2] - position[2], 0.0, position[2] - box[3])
            return math.hypot(dx, dz)

        for name, box in self.PARKED_FOOTPRINTS.items():
            for spawn in self.bots["player_spawns"]:
                self.assertGreaterEqual(gap(spawn["position"], box), 5.0, f"{spawn['id']} is within its radius of {name}")
            for spawn in self.bots["bot_spawns"]:
                self.assertGreaterEqual(gap(spawn["position"], box), 3.0, f"{spawn['id']} is too close to {name}")

    def test_finite_spawns_keep_exact_zone_categories_and_generated_fields(self):
        for point in self.bots["player_spawns"] + self.bots["bot_spawns"]:
            self.assertTrue(all(math.isfinite(v) for v in point["position"]))
            self.assertGreaterEqual(point["position"][1], -4)
            self.assertLessEqual(point["position"][1], -2)
        self.assertEqual(self.bots["zone"], "BotZone_Main")
        for point in self.bots["bot_spawns"]:
            self.assertEqual(set(point["categories"]), {"Bot", "BotPmc"})
        generated = {}
        generator.apply_bots(generated, self.map, self.bots)
        generator.apply_exits(generated, self.map, self.exits)
        for key, value in generated.items():
            self.assertEqual(self.base[key], value, f"Stale generated field: {key}")
        self.assertTrue(self.base["DisabledForScav"])


class Bay5BuiltDataTests(unittest.TestCase):
    """The appended rows and the exit boxes against the built NavMesh and the package colliders (ignored inputs; WORK points at them)."""

    @classmethod
    def setUpClass(cls):
        if not BUILT_NAVMESH.is_file() or not PACKAGE.is_dir():
            raise unittest.SkipTest(f"no {BUILT_NAVMESH} / {PACKAGE} (set ARENAMAPPORT_WORK_DIR)")
        try:
            import numpy as np
            import navmesh_diff as nd
            import port_colliders
            import spawn_coverage as sc
            import spawn_placement_probe as probe
        except ImportError:
            raise unittest.SkipTest("numpy or UnityPy is not installed")
        cls.np = np
        cls.mesh = nd.load_bundle_mesh(BUILT_NAVMESH)
        cset = port_colliders.load_package_colliders(PACKAGE)
        cls.placement = probe.PlacementProbe.from_collider_set(cset)
        keep = np.array([not probe.runtime_nonsolid(o.names) for o in cset.owners], dtype=bool)
        mask = keep[cset.tri_owner]
        cls.solids = sc.SolidIndex(cset.tris[mask], cset.tri_owner[mask], [o.path for o in cset.owners])

    def locate(self, position):
        xyz = self.np.array([position], dtype=float)
        polygons, surface = self.mesh.locate(xyz, max_dy=1.5)
        if polygons[0] < 0:
            return -1, None, 0.0
        clearance = float(self.mesh.clearance(self.np.array([[position[0], surface[0], position[2]]]), dy=1.2)[0])
        return int(self.mesh.labels[polygons[0]]), float(surface[0]), clearance

    def setUp(self):
        self.bots = json.loads((REPO / "tools/maps/bay5.bots.json").read_text(encoding="utf-8"))
        self.exits = json.loads((REPO / "tools/maps/bay5.exits.json").read_text(encoding="utf-8"))

    def test_appended_rows_lie_on_the_play_component_with_room_and_standing_room(self):
        play = self.locate(self.bots["player_spawns"][3]["position"])[0]
        self.assertGreaterEqual(play, 0)
        for point in self.bots["bot_spawns"][len(ORIGINAL_BOT_IDS):]:
            component, _, clearance = self.locate(point["position"])
            with self.subTest(row=point["id"]):
                self.assertEqual(component, play, "not on the play component")
                self.assertGreaterEqual(clearance, 1.3, "too close to the NavMesh border")  # measured 1.42 to 3.22 m on 2026-11-12
                verdict = self.placement.check(point["position"])
                self.assertTrue(verdict.valid, f"no standing room: {verdict}")

    def test_exit_boxes_hold_floor_and_no_solid(self):
        for point in self.exits["exits"]:
            x, y, z = point["position"]
            with self.subTest(exit=point["name"]):
                component, surface, clearance = self.locate([x, y, z])
                self.assertGreaterEqual(component, 0, "no NavMesh under the box centre")
                self.assertLessEqual(abs(surface - y), 0.1, "the box centre is not on the floor")
                self.assertGreaterEqual(clearance, 1.0)
                # floor + 0.3 to floor + 2.5 over the box footprint (rotation 0); the moved gate parts stand 5 m and more away.
                found = self.solids.solids_in_box([x, surface + 1.4, z], [EXIT_BOX, 2.2, EXIT_BOX])
                self.assertEqual(found, [], f"solid in the box: {found[:3]}")


if __name__ == "__main__":
    unittest.main()
