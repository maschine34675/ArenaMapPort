"""Yard data contract (docs/PLAN-YARD.md item 8, 2026-11-20): tools/maps/yard.bots.json, yard.exits.json, yard.modes.json and yard.ai.json
against each other, against Arena's own NavMesh and against the port's colliders. Yard is Catalog-only: nothing here reads the descriptor
mirror, the registry or a built bundle (no Yard NavMesh is built before plan item 16; spawn_coverage.py on the built bundle and
test_spawn_layout_rules for Yard are plan item 20).

Three layers, as in test_autoservice_spawns.py:

1. Data only (the repository): the sidecars have the shape the released maps' have and the generator turns them into the base.json
   fields; the player rows are four of the 20 pocket markers of yard.modes.json, the bot rows are Arena's AI core points and born points
   under a stated rule plus four fill rows (checked both ways); nothing stands inside a team box; each pocket holds one exit (rule R3)
   that keeps 2.5 m to every row; the pocket split passes at the pinned ratio and the capture point is the light centroid.
2. Arena's NavMesh (the installed Arena 0.6.2, level320's NavMeshData = ``sharedassets281.assets`` path 2, identity placement, 66
   tiles, 1,911 polygons, 11 components): all 20 markers, the capture point, the 7 core points, the 11 born points and all 52 patrol points
   lie on the 8,064.5 m2 main component; every row and exit stands on it with the documented clearance; 40 of the 43 mine points do.
3. The port's colliders (the ignored package work/extract/yard-native-v2 through tools/validate/spawn_placement_probe.py, the offline
   stand-in for SpawnPlacementRepairs.IsValid): every marker, row, core point and born point stands on a floor and passes, the exit
   boxes hold no solid above the floor, and the swept footprints of the two gates are the ones the rules use.

Rules (the numbers are what the committed layout measures, with a small margin):

* R1: ids are unique, no id starts with arena_yard-tdm-v1- (ArenaMode's own prefix), every Bot row is in BotZone_Main with radius 25 and
  the categories Bot and BotPmc. Yard has no ArenaMode donor row. The layout is the first one: later changes are appended-only
  (spawn-rows-and-pocket-exits.md), the literal pin of the first rows is ``FIRST_ROWS`` below.
* The 14 rows from Arena's AI points keep: 8 m to a gate's swept footprint, 10 m to every pocket marker, 5 m outside both spawn bounds,
  5 m to every Player row; the four fill rows keep: 10 m to a gate, 7 m to a pocket marker, 1.5 m outside both bounds, 5 m to every
  Player row, 4 m to every other row, and stand north of z 17 (the south band is where Arena's mesh is cut straight at its bake volume).
* R3: every pocket holds exactly one exit whose centre lies inside the pocket's initial spawn bound (horizontally, the whole 4 x 4 m box
  inside as well), every Player and Bot row keeps 2.5 m to the box EDGE, there is floor under the centre and no solid stands in the box
  between floor + 0.3 and floor + 2.5 m.
* R6: no literal exit coordinates except the two pinned in ``EXIT_POSITIONS`` (a host nudge then changes them here, in the sidecar and in
  the descriptor together); the C# tests pin descriptor == sidecar == base.json at the promotion.
"""
import importlib.util
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
ARENA_DATA = Path(os.environ.get("ARENAMAPPORT_ARENA_DATA")
                  or "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data")
PACKAGE = WORK / "extract" / "yard-native-v2"

spec = importlib.util.spec_from_file_location("make_base_json", REPO / "tools/server/make_base_json.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)

import extract_mode_objects as M  # noqa: E402

NAVMESH = ("sharedassets281.assets", 2)
# Player row id suffix -> (sides, name, pocket marker). South pocket = the 'Usec house' markers, north pocket = the 'Bear house' markers.
PLAYER_MARKERS = {
    "all": (["All"], "SpawnPoint_Player_All", "Usec house (10)"),
    "usec": (["Usec"], "SpawnPoint_Player_Usec", "Usec house (9)"),
    "bear": (["Bear"], "SpawnPoint_Player_Bear", "Bear house (7)"),
    "north-all": (["All"], "SpawnPoint_Player_North_All", "Bear house (6)"),
}
BOT_CORE_POINTS = {f"core-{n:02d}": n for n in (1, 3, 4, 5, 6, 7)}
BOT_BORN_POINTS = {f"born-{n:02d}": n for n in (2, 3, 4, 5, 7, 8, 9, 10)}
NATIVE_SUFFIXES = list(BOT_CORE_POINTS) + list(BOT_BORN_POINTS)
FILL_SUFFIXES = ["fill-01", "fill-02", "fill-03", "fill-04"]
# Native points the rules drop: core 2 (6.6 m from the cradle), born p1 and p1 (1) (1.6 and 6.5 m from it), p1 (6) (2.3 m from the north gate leaves).
DROPPED_CORE_POINTS = (2,)
DROPPED_BORN_POINTS = (0, 1, 6)
# Born points that kept less than 1.35 m of border clearance in Arena's mesh moved onto open floor (largest move allowed, m); the others stand on their point.
MOVED_FROM_POINT = {"born-02": 1.85, "born-03": 0.8, "born-04": 1.85, "born-05": 1.85, "born-07": 1.95, "born-08": 1.55,
                    "born-09": 2.0, "born-10": 2.0}
# R1: the first layout, in file order (id suffix, position). A frozen record: rows are appended-only from here (docs/progress/yard-release.md).
FIRST_ROWS = [("core-01", [-61.97, 6.15, 54.27]), ("core-03", [-5.08, 6.15, 57.92]), ("core-04", [-12.88, 6.17, 87.03]),
              ("core-05", [-6.31, 6.16, 116.92]), ("core-06", [-36.5, 6.15, 112.19]), ("core-07", [-60.81, 6.15, 89.13]),
              ("born-02", [5.94, 6.18, 45.0]), ("born-03", [7.68, 6.2, 45.99]), ("born-04", [7.13, 7.38, 74.38]),
              ("born-05", [6.73, 7.38, 71.56]), ("born-07", [-56.14, 6.15, 98.7]), ("born-08", [-55.12, 6.15, 100.55]),
              ("born-09", [-59.78, 6.25, 49.32]), ("born-10", [-57.7, 6.25, 47.36]), ("fill-01", [-32.5, 6.15, 23.5]),
              ("fill-02", [10.5, 6.15, 25.5]), ("fill-03", [-56.5, 6.21, 22.5]), ("fill-04", [-35.5, 6.15, 65.5])]
EXIT_POSITIONS = {"Block South Gate": [-20.75, 7.48, 11.25], "Block North Gate": [-3.25, 6.15, 150.5]}
# The two gates (package DesignStuff DOORS/<path>), the horizontal footprint the rules use over the gate's whole travel (x0, x1, z0, z1):
# the cradle rises (+2.446 m, only the vertical extent changes), so its plate's footprint is the swept one; the north gate's two leaves
# swing +/-90 degrees about their hinges (doors sidecar); decoded in the 2026-11-20 review: both leaves swing toward -z (the yard side), so the swept
# footprint is one-sided, x -11.88..-7.55, z about 125.7..127.9 (derived from Unity's rotation convention, not run in Unity).
GATES = {
    "DOORS/Construction_Cradle_02/Construction_Cradle_02_A": (-24.4, -18.1, 16.2, 17.4),
    "DOORS/Metal_gate_1": (-11.9, -7.5, 125.65, 127.9),
}
GATE_ROOTS = {"DOORS/Construction_Cradle_02": "cradle", "DOORS/Metal_gate_1": "north gate"}
NATIVE_GATE_MIN, NATIVE_MARKER_MIN, NATIVE_BOUND_MIN = 8.0, 10.0, 5.0
FILL_GATE_MIN, FILL_MARKER_MIN, FILL_BOUND_MIN, FILL_ROW_GAP, FILL_MIN_Z = 10.0, 7.0, 1.5, 4.0, 17.0
PLAYER_ROW_MIN = 5.0
MIN_PLAYER_CLEARANCE = 1.2         # border clearance in Arena's mesh (the south platform is tight: 1.28 and 1.32 m)
MIN_BOT_CLEARANCE = 1.35           # the Iceberg bar; the committed rows keep 1.50 m or more
MIN_EXIT_ROW_EDGE = 2.5            # R3: metres between an exit box edge and any Player or Bot row
EXIT_HALF_WIDTH = 2.0              # boxes are 4 x 3 x 4 m with rotation 0
MIN_EXIT_FLOOR_SHARE = 0.95        # share of a 0.5 m lattice under the box that has NavMesh (measured: south 77 of 81 = 95.1 %, north 100 %)
# Mine points that lie off Arena's mesh (3 of 43)
OFF_MESH_MINES = (("AMinePlace_18", (-64.3218, 6.15, 94.8582)), ("AMinePlace_188", (-10.7393, 6.15, 154.42)),
                  ("AMinePlace_98", (-29.6373, 6.15, 19.9311)))


def load(name):
    return json.loads((REPO / "tools" / "maps" / name).read_text(encoding="utf-8"))


def xz(point):
    return (point[0], point[2])


def rect_distance(point, rect):
    return math.hypot(max(rect[0] - point[0], 0.0, point[0] - rect[1]), max(rect[2] - point[2], 0.0, point[2] - rect[3]))


def nearest_gate(point):
    return min(rect_distance(point, rect) for rect in GATES.values())


def box_edge_distance(point, exit_point):
    """Horizontal distance from a point to the EDGE of an exit box (4 x 4 m footprint, rotation 0); 0 inside the footprint."""
    return math.hypot(max(abs(point[0] - exit_point["position"][0]) - EXIT_HALF_WIDTH, 0.0),
                      max(abs(point[2] - exit_point["position"][2]) - EXIT_HALF_WIDTH, 0.0))


def horizontal_margin(bound, position):
    """How far a point lies inside a spawn bound's footprint (x / z only; the bounds are 5.6 m tall)."""
    return M.box_margin(bound, (position[0], bound["center"][1], position[2]))


def exit_pocket(exit_point, bounds):
    """The spawn bounds whose footprint holds the exit's centre (R3: exactly one per exit)."""
    return [name for name, bound in bounds.items() if horizontal_margin(bound, exit_point["position"]) >= 0.0]


def born_name(number):
    return "p1" if number == 0 else f"p1 ({number})"


class SidecarData:
    """The five tracked Yard sidecars, loaded once per test class."""

    @classmethod
    def load_data(cls):
        cls.bots, cls.exits, cls.map = load("yard.bots.json"), load("yard.exits.json"), load("yard.json")
        cls.modes, cls.ai = load("yard.modes.json"), load("yard.ai.json")
        cls.players, cls.bot_points = cls.bots["player_spawns"], cls.bots["bot_spawns"]
        cls.markers = {m["name"]: m for side in cls.modes["spawn_markers"].values() for m in side}
        cls.bounds = {b["name"]: b for b in cls.modes["spawn_bounds"]}
        cls.native_rows, cls.fill_rows = cls.bot_points[:14], cls.bot_points[14:]
        cls.core = {c["id"]: c["position"] for c in cls.ai["core_points"]}
        cls.born = {b["name"]: b["position"] for b in cls.ai["born_points"]}


class YardSidecarTests(SidecarData, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.load_data()

    # ------------------------------------------------------------------ modes: the pocket split and the capture point
    def test_the_pocket_rule_passes_at_ratio_7_9_and_there_are_twenty_markers(self):
        data = self.modes
        self.assertEqual(data["map_key"], "arena_yard")
        self.assertEqual(data["counts"], {"capture_zones": 2, "spawn_bounds": 2, "spawn_markers_north": 10, "spawn_markers_south": 10})
        self.assertEqual(data["side_rule"]["method"], "pocket_split")          # the default rule: no SideBoundRule, no TeamBoundRule
        self.assertNotIn("arena_yard", M.SIDE_BOUND_RULES)
        self.assertNotIn("arena_yard", M.TEAM_BOUND_RULES)
        points = [(m["position"][0], m["position"][2]) for side in data["spawn_markers"].values() for m in side]
        self.assertEqual(len(points), 20)
        pockets = M.split_pockets(points)
        self.assertIsNotNone(pockets)
        _a, _b, cut, spread = pockets
        ratio = cut / spread
        self.assertAlmostEqual(cut, 129.6377, places=3)
        self.assertAlmostEqual(spread, 16.4019, places=3)
        self.assertAlmostEqual(ratio, 7.9, delta=0.01)
        self.assertGreater(ratio, 3.0 * M.POCKET_SEPARATION)       # nowhere near the limit (Autoservice passed at 2.12 and needed a rule)
        record = data["side_rule"]
        self.assertEqual((record["cut_distance"], record["pocket_spread"]), (round(cut, 4), round(spread, 4)))
        self.assertEqual({k: v["markers"] for k, v in record["pockets"].items()}, {"north": 10, "south": 10})

    def test_the_pockets_are_the_two_fighter_bounds_ten_markers_each_all_inside(self):
        bounds = {b["side"]: b for b in self.modes["spawn_bounds"]}
        self.assertEqual({k: b["name"] for k, b in bounds.items()}, {"north": "SPAWN_BOUND_FIGHTER (1)", "south": "SPAWN_BOUND_FIGHTER"})
        self.assertEqual(bounds["south"]["center"], [-15.2116, 9.1217, 8.7239])
        self.assertEqual(bounds["north"]["center"], [-7.418, 8.1217, 148.4042])
        for side, stem in (("south", "Usec house"), ("north", "Bear house")):
            own, other = bounds[side], bounds["north" if side == "south" else "south"]
            self.assertEqual({m["stem"] for m in self.modes["spawn_markers"][side]}, {stem})
            for marker in self.modes["spawn_markers"][side]:
                with self.subTest(marker=marker["name"]):
                    self.assertGreaterEqual(M.box_margin(own, marker["position"]), 0.0)
                    self.assertLess(M.box_margin(other, marker["position"]), -100.0)
        self.assertEqual(sorted(m["index"] for s in self.modes["spawn_markers"].values() for m in s), sorted([5, 6, 7, 8, 9, 10, 11, 12, 13, 14] * 2))

    def test_the_capture_point_is_the_light_centroid_not_the_zone_root(self):
        capture = self.modes["capture_point"]
        self.assertEqual(capture["method"], "capture_point_light_centroid")
        self.assertEqual(capture["position"], [-45.9245, 6.1262, 71.9087])
        self.assertEqual(len(capture["lights"]), 2)
        lights = [light["position"] for light in capture["lights"]]
        for axis in range(3):
            self.assertAlmostEqual(sum(light[axis] for light in lights) / 2.0, capture["position"][axis], places=3)
        # the Capture_Zone root of both scenes lies 37.4 m away (its 13 children are graffiti decals), the two methods disagree on this map
        self.assertEqual([z["path"] for z in self.modes["capture_zones"]], ["Capture_Zone", "Capture_Zone"])
        for zone in self.modes["capture_zones"]:
            self.assertAlmostEqual(zone["distance_to_capture_point"], 37.44, delta=0.02)
            self.assertEqual(zone["anchor_position"], [-10.461, 6.1226, 83.9136])

    # ------------------------------------------------------------------ shape
    def test_counts_and_ids(self):
        self.assertEqual((len(self.players), len(self.bot_points)), (4, 18))
        self.assertEqual((len(self.bots["waves"]), len(self.bots["boss_waves"]), len(self.exits["exits"])), (5, 4, 2))
        self.assertEqual((self.bots["zone"], self.bots["max_bots"]), ("BotZone_Main", 8))
        ids = [p["id"] for p in self.players + self.bot_points]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual([p["id"] for p in self.players], [f"arena_yard-player-{s}" for s in PLAYER_MARKERS])
        self.assertEqual([p["id"] for p in self.native_rows], [f"arena_yard-bot-{s}" for s in NATIVE_SUFFIXES])
        self.assertEqual([p["id"] for p in self.fill_rows], [f"arena_yard-bot-{s}" for s in FILL_SUFFIXES])
        self.assertFalse([i for i in ids if "tdm-v1" in i])          # ArenaMode's own ids are never reused
        self.assertEqual(self.map["infiltration_name"], "ArenaYard")

    def test_the_first_layout_is_the_committed_one(self):
        """R1, the literal point of this pin: the 18 Bot rows as decided on 2026-11-20 (appended-only from here)."""
        self.assertEqual(self.bot_points,
                         [{"id": f"arena_yard-bot-{s}", "position": pos, "radius": 25, "categories": ["Bot", "BotPmc"]} for s, pos in FIRST_ROWS])

    def test_schema_matches_the_released_maps(self):
        equator = load("equator.bots.json")
        bay5_exits = load("bay5.exits.json")
        self.assertEqual(set(self.bots), set(equator))
        self.assertEqual({k for p in self.players for k in p}, {k for p in equator["player_spawns"] for k in p})
        self.assertLessEqual({k for p in self.bot_points for k in p}, {k for p in equator["bot_spawns"] for k in p})
        self.assertEqual(set(self.bots["waves"][0]), set(equator["waves"][0]))
        self.assertEqual(set(self.bots["boss_waves"][0]), set(equator["boss_waves"][0]))
        self.assertEqual(set(self.exits), {"_comment", "exits"})
        self.assertEqual(set(self.exits["exits"][0]), set(bay5_exits["exits"][0]))
        self.assertIn("2026-11-20", self.bots["_comment"])
        self.assertIn("2026-11-20", self.exits["_comment"])

    def test_waves_and_pmc_squads_are_the_released_maps(self):
        for key in ("waves", "boss_waves"):
            for other in ("airpit", "bowl", "iceberg", "autoservice"):
                self.assertEqual(self.bots[key], load(f"{other}.bots.json")[key], other)
        self.assertEqual(sorted(w["boss_name"] for w in self.bots["boss_waves"]), ["pmcBEAR", "pmcBEAR", "pmcUSEC", "pmcUSEC"])
        for wave in self.bots["boss_waves"]:
            self.assertEqual((wave["boss_zone"], wave["escort_type"]), (self.bots["zone"], wave["boss_name"]))

    def test_the_generator_turns_the_sidecars_into_the_base_json_fields(self):
        base = {}
        generator.apply_bots(base, self.map, self.bots)
        generator.apply_boss_waves(base, self.bots)
        generator.apply_exits(base, self.map, self.exits)
        self.assertEqual(set(base), {"SpawnPointParams", "waves", "OpenZones", "BotMax", "OfflineOldSpawn", "OfflineNewSpawn",
                                     "BossLocationSpawn", "exits"})
        self.assertEqual((len(base["SpawnPointParams"]), len(base["waves"]), len(base["BossLocationSpawn"]), len(base["exits"])),
                         (22, 5, 4, 2))
        self.assertEqual((base["BotMax"], base["OpenZones"]), (8, "BotZone_Main"))
        for point in base["SpawnPointParams"]:
            if "Player" in point["Categories"]:
                self.assertEqual(point["Infiltration"], self.map["infiltration_name"])
                self.assertEqual(point["BotZoneName"], "")
            else:
                self.assertEqual((point["BotZoneName"], point["Infiltration"]), ("BotZone_Main", ""))
                self.assertIn("BotPmc", point["Categories"])
        for exit_point in base["exits"]:
            self.assertEqual(exit_point["EntryPoints"], self.map["infiltration_name"])

    def test_row_values_follow_the_released_conventions(self):
        for point in self.players + self.bot_points:
            self.assertTrue(all(math.isfinite(v) for v in point["position"]), point["id"])
        for point in self.bot_points:
            self.assertEqual((set(point["categories"]), point["radius"]), ({"Bot", "BotPmc"}, 25))
        for point in self.players:
            self.assertEqual(point["radius"], 5)
        for exit_point in self.exits["exits"]:
            self.assertEqual(exit_point["box_size"], [4.0, 3.0, 4.0])
            self.assertEqual((exit_point["type"], exit_point["chance"], exit_point["exfiltration_time"], exit_point["rotation"]),
                             ("Individual", 100, 8, 0.0))
        self.assertEqual({e["name"]: e["position"] for e in self.exits["exits"]}, EXIT_POSITIONS)
        self.assertEqual([e["name"] for e in self.exits["exits"]], ["Block South Gate", "Block North Gate"])

    # ------------------------------------------------------------------ player rows
    def test_player_rows_are_the_world_positions_of_four_pocket_markers(self):
        by_id = {p["id"]: p for p in self.players}
        for suffix, (sides, name, marker_name) in PLAYER_MARKERS.items():
            row, marker = by_id[f"arena_yard-player-{suffix}"], self.markers[marker_name]
            with self.subTest(row=suffix):
                self.assertEqual((row["sides"], row["name"]), (sides, name))
                self.assertEqual(row["position"], [round(v, 2) for v in marker["position"]])
                self.assertEqual(row["rotation"], round(marker["yaw"], 1))

    def test_usec_and_all_start_in_the_south_pocket_bear_and_all_in_the_north_one(self):
        for row in self.players:
            south = row["position"][2] < 75.0
            bound = self.bounds["SPAWN_BOUND_FIGHTER" if south else "SPAWN_BOUND_FIGHTER (1)"]
            self.assertGreaterEqual(horizontal_margin(bound, row["position"]), 1.5, row["id"])
            if row["sides"] == ["Usec"]:
                self.assertTrue(south, row["id"])
            if row["sides"] == ["Bear"]:
                self.assertFalse(south, row["id"])
        self.assertEqual({tuple(p["sides"]) for p in self.players}, {("All",), ("Usec",), ("Bear",)})
        south = {m["name"] for m in self.modes["spawn_markers"]["south"]}
        self.assertEqual({PLAYER_MARKERS[s][2] in south for s in ("all", "usec")}, {True})
        self.assertEqual({PLAYER_MARKERS[s][2] in south for s in ("bear", "north-all")}, {False})
        self.assertEqual(sum(1 for p in self.players if p["position"][2] < 75.0), 2)

    # ------------------------------------------------------------------ bot rows
    def test_native_rows_sit_on_their_point_except_the_documented_moves(self):
        by_id = {p["id"].split("bot-")[1]: p for p in self.native_rows}
        sources = {**{k: self.core[n] for k, n in BOT_CORE_POINTS.items()}, **{k: self.born[born_name(n)] for k, n in BOT_BORN_POINTS.items()}}
        self.assertEqual(len(sources), 14)
        for suffix, source in sources.items():
            row = by_id[suffix]["position"]
            moved = math.dist(xz(row), xz(source))
            with self.subTest(row=suffix):
                if suffix in MOVED_FROM_POINT:
                    self.assertGreater(moved, 0.7)
                    self.assertLessEqual(moved, MOVED_FROM_POINT[suffix])
                else:
                    self.assertAlmostEqual(moved, 0.0, delta=0.006)
                    self.assertAlmostEqual(row[1], source[1], delta=0.011)

    def test_the_skip_rule_the_dropped_points_are_the_ones_near_a_gate_and_the_kept_ones_are_not(self):
        pocket = [m["position"] for m in self.markers.values()]

        def near_gate(position):
            return nearest_gate(position)

        self.assertEqual({n for n, p in self.core.items() if near_gate(p) < NATIVE_GATE_MIN}, set(DROPPED_CORE_POINTS))
        self.assertEqual({int(name.split("(")[1].rstrip(")")) if "(" in name else 0
                          for name, p in self.born.items() if near_gate(p) < NATIVE_GATE_MIN}, set(DROPPED_BORN_POINTS))
        self.assertEqual(set(self.core) - set(DROPPED_CORE_POINTS), set(BOT_CORE_POINTS.values()))
        self.assertEqual(set(range(11)) - set(DROPPED_BORN_POINTS), set(BOT_BORN_POINTS.values()))
        for source in list(self.core.values()) + list(self.born.values()):
            self.assertGreaterEqual(min(math.dist(xz(source), xz(m)) for m in pocket), 6.0)      # the dropped ones are 6.7 m or more from a marker
        # the 8 m gate bar keeps core point 5 (8.9 m from the north gate's swing): the 14 rows the plan counted
        self.assertGreater(near_gate(self.core[5]), NATIVE_GATE_MIN)
        self.assertLess(near_gate(self.core[5]), 10.0)

    def test_native_rows_obey_the_original_rules(self):
        pocket = [m["position"] for m in self.markers.values()]
        for row in self.native_rows:
            position = row["position"]
            with self.subTest(row=row["id"]):
                self.assertGreaterEqual(nearest_gate(position), NATIVE_GATE_MIN)
                self.assertGreaterEqual(min(math.dist(xz(position), xz(m)) for m in pocket), NATIVE_MARKER_MIN)
                for bound in self.bounds.values():
                    self.assertLess(horizontal_margin(bound, position), -NATIVE_BOUND_MIN)
                for player in self.players:
                    self.assertGreaterEqual(math.dist(position, player["position"]), PLAYER_ROW_MIN, player["id"])

    def test_fill_rows_have_no_arena_source_and_obey_the_weaker_rules(self):
        pocket = [m["position"] for m in self.markers.values()]
        sources = list(self.core.values()) + list(self.born.values())
        for row in self.fill_rows:
            position = row["position"]
            with self.subTest(row=row["id"]):
                self.assertGreaterEqual(position[2], FILL_MIN_Z)
                self.assertGreaterEqual(nearest_gate(position), FILL_GATE_MIN)
                self.assertGreaterEqual(min(math.dist(xz(position), xz(m)) for m in pocket), FILL_MARKER_MIN)
                for bound in self.bounds.values():
                    self.assertLess(horizontal_margin(bound, position), -FILL_BOUND_MIN)
                for player in self.players:
                    self.assertGreaterEqual(math.dist(position, player["position"]), PLAYER_ROW_MIN, player["id"])
                # a fill row is not an AI point of Arena's in disguise
                self.assertGreater(min(math.dist(xz(position), xz(s)) for s in sources), 3.0)

    def test_spacing_between_rows(self):
        for fill in self.fill_rows:
            for other in self.bot_points:
                if other is not fill:
                    self.assertGreaterEqual(math.dist(xz(fill["position"]), xz(other["position"])), FILL_ROW_GAP, f"{fill['id']} / {other['id']}")
        for a in self.bot_points:
            for b in self.bot_points:
                if a is not b:
                    self.assertGreaterEqual(math.dist(xz(a["position"]), xz(b["position"])), 2.0, f"{a['id']} / {b['id']}")   # born-point pairs stand 2.0 to 2.9 m apart
        for a in self.players:
            for b in self.players:
                if a is not b:
                    self.assertGreaterEqual(math.dist(a["position"], b["position"]), 3.0, f"{a['id']} / {b['id']}")

    def test_no_row_or_ai_point_lies_inside_a_team_box_and_the_rows_stand_in_the_yard(self):
        for number, position in self.core.items():
            for bound in self.bounds.values():
                self.assertLess(M.box_margin(bound, position), 0.0, f"core {number} inside {bound['name']}")
        for row in self.bot_points:
            for bound in self.bounds.values():
                self.assertLess(horizontal_margin(bound, row["position"]), -FILL_BOUND_MIN, row["id"])
        # the AI points, rows and markers all lie in Arena's NavMeshSurface volume (x -66.4..20.8, z -19.05..161.05)
        for row in self.players + self.bot_points:
            self.assertTrue(-66.4 <= row["position"][0] <= 20.8 and -19.05 <= row["position"][2] <= 161.05, row["id"])

    # ------------------------------------------------------------------ exits
    def test_every_pocket_holds_one_exit_inside_its_footprint(self):
        """R3: one exit per team pocket, its centre inside the pocket's spawn-bound footprint, so that the signal flare marks the team's start."""
        self.assertEqual(len(self.exits["exits"]), len(self.bounds))
        by_name = {}
        for exit_point in self.exits["exits"]:
            holders = exit_pocket(exit_point, self.bounds)
            self.assertEqual(len(holders), 1, f"{exit_point['name']} is inside {holders}")
            by_name[exit_point["name"]] = holders[0]
            for dx in (-EXIT_HALF_WIDTH, EXIT_HALF_WIDTH):             # the whole 4 x 4 m footprint lies inside as well (measured, not demanded by R3)
                for dz in (-EXIT_HALF_WIDTH, EXIT_HALF_WIDTH):
                    corner = (exit_point["position"][0] + dx, exit_point["position"][1], exit_point["position"][2] + dz)
                    self.assertGreaterEqual(horizontal_margin(self.bounds[holders[0]], corner), 0.0, exit_point["name"])
        self.assertEqual(set(by_name.values()), set(self.bounds))
        self.assertEqual(by_name, {"Block South Gate": "SPAWN_BOUND_FIGHTER", "Block North Gate": "SPAWN_BOUND_FIGHTER (1)"})

    def test_no_row_starts_inside_an_exit_box_and_the_box_keeps_clear_of_the_gates(self):
        """R3: every Player and Bot row keeps 2.5 m to the box EDGE (a plain-raid spawn must not start inside a trigger; the Equator booth precedent)."""
        for exit_point in self.exits["exits"]:
            for row in self.players + self.bot_points:
                self.assertGreaterEqual(box_edge_distance(row["position"], exit_point), MIN_EXIT_ROW_EDGE,
                                        f"{row['id']} too close to {exit_point['name']}")
            edge = nearest_gate(exit_point["position"]) - EXIT_HALF_WIDTH            # conservative: centre distance minus the half width
            self.assertGreaterEqual(edge, 2.5, f"{exit_point['name']}: the box edge is {edge:.2f} m from a gate's travel")
        # the nearest Player row to each box is what the sidecar comment says (3.89 m and 2.74 m)
        nearest = {e["name"]: min(box_edge_distance(p["position"], e) for p in self.players) for e in self.exits["exits"]}
        self.assertAlmostEqual(nearest["Block South Gate"], 3.89, delta=0.02)
        self.assertAlmostEqual(nearest["Block North Gate"], 2.74, delta=0.02)


def mesh_available():
    return (ARENA_DATA / NAVMESH[0]).is_file()


@unittest.skipUnless(mesh_available(), "Arena is not installed")
class YardArenaMeshTests(SidecarData, unittest.TestCase):
    """Arena's own NavMeshData of this map, the reference until the port's mesh is built (plan item 16)."""

    @classmethod
    def setUpClass(cls):
        try:
            import numpy as np
            import navmesh_diff as nd
            import navmesh_oracle as no
        except ImportError:
            raise unittest.SkipTest("numpy or UnityPy is not installed")
        cls.np, cls.nd, cls.no = np, nd, no
        cls.mesh, cls.meta = nd.load_arena_mesh(str(ARENA_DATA), *NAVMESH)
        cls.load_data()

    def locate(self, positions, max_dy=1.5):
        np = self.np
        xyz = np.array(positions, dtype=float)
        poly, surf = self.mesh.locate(xyz, max_dy=max_dy)
        out = []
        for position, pid, sy in zip(xyz, poly, surf):
            if pid < 0:
                out.append((-1, None, None))
                continue
            clearance = float(self.mesh.clearance(np.array([[position[0], sy, position[2]]]), dy=1.2)[0])
            out.append((int(self.mesh.labels[pid]), float(sy), clearance))
        return out

    def test_the_navmesh_is_the_one_the_survey_measured(self):
        self.assertTrue(self.mesh.placement.is_identity)
        self.assertEqual((self.meta["tiles_total"], self.mesh.poly_count), (66, 1911))
        components = self.no.component_summary(self.mesh)
        self.assertEqual(len(components), 11)
        main = components[0]
        self.assertEqual(main["polygons"], 1669)
        self.assertAlmostEqual(main["area_xz"], 8064.5, delta=0.5)
        for got, want in zip(main["min"] + main["max"], [-66.43, 6.15, -19.11, 20.86, 10.70, 154.70]):
            self.assertAlmostEqual(got, want, delta=0.06)
        # the other ten components are roofs (y 21 to 29) and one 7 m2 island at the deck height: none of our points lies on them
        self.assertTrue(all(c["min"][1] > 10.0 for c in components[1:] if c["area_xz"] > 8.0))

    def test_all_twenty_markers_stand_on_the_main_component_within_0_3_m(self):
        names = sorted(self.markers)
        self.assertEqual(len(names), 20)
        results = dict(zip(names, self.locate([self.markers[n]["position"] for n in names])))
        for name, (label, sy, clearance) in results.items():
            with self.subTest(marker=name):
                self.assertEqual(label, 0)
                self.assertLess(abs(sy - self.markers[name]["position"][1]), 0.3)       # (14) is the furthest, 0.28 m
                self.assertGreaterEqual(clearance, 0.1)                                 # (6) keeps 0.14 m: Arena's own marker

    def test_the_capture_point_and_all_the_ai_points_are_on_the_main_component(self):
        capture = self.modes["capture_point"]["position"]
        label, _sy, clearance = self.locate([capture])[0]
        self.assertEqual(label, 0)
        self.assertGreaterEqual(clearance, 0.5)
        core = self.core
        self.assertEqual(len(core), 7)
        for number, (label, sy, _c) in zip(core, self.locate([core[n] for n in core])):
            self.assertEqual(label, 0, f"core {number}")
            self.assertLess(abs(sy - core[number][1]), 0.02, f"core {number}")
        self.assertEqual(len(self.born), 11)
        for name, (label, sy, _c) in zip(self.born, self.locate(list(self.born.values()))):
            self.assertEqual(label, 0, name)
            self.assertLess(abs(sy - self.born[name][1]), 0.11, name)
        points = [h["position"] for h in self.ai["patrol_ways"][0]["hubs"]] \
            + [s for h in self.ai["patrol_ways"][0]["hubs"] for s in h["sub_points"]]
        self.assertEqual(len(points), 52)
        for position, (label, _sy, _c) in zip(points, self.locate(points)):
            self.assertEqual(label, 0, f"patrol point {position}")

    def test_forty_of_the_forty_three_mine_points_are_on_the_main_component(self):
        mines = self.ai["mine_points"]
        self.assertEqual(len(mines), 43)
        labels = [r[0] for r in self.locate([m["position"] for m in mines])]
        # Arena reuses the name AMinePlace_18 for two points (one is off the mesh), so the three off-mesh points are told apart by position
        off = sorted((m["name"], tuple(m["position"])) for m, label in zip(mines, labels) if label != 0)
        self.assertEqual(off, sorted(OFF_MESH_MINES))
        self.assertTrue(all(label == -1 for m, label in zip(mines, labels) if (m["name"], tuple(m["position"])) in OFF_MESH_MINES))

    def test_rows_and_exits_stand_on_the_main_component_with_room(self):
        rows = [(p["id"], p["position"], MIN_PLAYER_CLEARANCE, 0.25) for p in self.players]       # Arena's own markers: up to 0.25 m off the mesh
        rows += [(p["id"], p["position"], MIN_BOT_CLEARANCE, 0.03) for p in self.bot_points]
        # R3: the exit y is Arena's surface at the centre; the floor under the whole box is checked below (lattice share)
        rows += [(e["name"], e["position"], 1.0 if "South" in e["name"] else 2.0, 0.03) for e in self.exits["exits"]]       # the south platform is narrow: 1.19 m at the centre
        for (name, position, need, dy), (label, sy, clearance) in zip(rows, self.locate([r[1] for r in rows])):
            self.assertEqual(label, 0, f"{name} is not on Arena's main component")
            self.assertLessEqual(abs(sy - position[1]), dy, f"{name} is {sy - position[1]:+.2f} m off Arena's surface")
            self.assertGreaterEqual(clearance, need, f"{name} is only {clearance:.2f} m from the NavMesh border")

    def test_every_exit_box_has_floor_under_it(self):
        np = self.np
        for exit_point in self.exits["exits"]:
            x, y, z = exit_point["position"]
            lattice = np.array([[x + dx, y, z + dz] for dx in np.arange(-2.0, 2.01, 0.5) for dz in np.arange(-2.0, 2.01, 0.5)], dtype=float)
            poly, _surf = self.mesh.locate(lattice, max_dy=1.5)
            share = float((poly >= 0).sum()) / len(lattice)
            self.assertGreaterEqual(share, MIN_EXIT_FLOOR_SHARE, f"{exit_point['name']}: floor under {share:.0%} of the box")

    def test_the_native_moves_were_needed_and_the_kept_points_have_room(self):
        # a row moves only when its point keeps less than 1.35 m of clearance; the points that stay keep more
        by_id = {p["id"].split("bot-")[1]: p for p in self.native_rows}
        for suffix in by_id:
            source = self.core[BOT_CORE_POINTS[suffix]] if suffix in BOT_CORE_POINTS else self.born[born_name(BOT_BORN_POINTS[suffix])]
            clearance = self.locate([source])[0][2]
            if suffix in MOVED_FROM_POINT:
                self.assertLess(clearance, 1.35, suffix)
                self.assertGreaterEqual(self.locate([by_id[suffix]["position"]])[0][2], 1.45, f"{suffix} did not gain room")
            else:
                self.assertGreaterEqual(clearance, 1.35, suffix)

    def test_the_rows_cover_the_25_m_rule_on_every_cell_outside_the_pockets(self):
        """ArenaMode places a bot only on a Bot row farther than 25 m (3D) from the player: every walkable cell outside the pocket footprints
        keeps at least 5 such rows (measured: minimum 12, median 16 of 18; the 14 native rows alone gave 8 and 13)."""
        np = self.np
        points, polys = self.no.sample_polygons(self.mesh, 1.0, origin=(0.5, 0.5))
        main = points[self.mesh.labels[polys] == 0]
        self.assertEqual(len(main), 8058)
        footprints = list(self.bounds.values())

        def in_pocket(p):
            return any(abs(p[0] - b["center"][0]) <= b["size"][0] / 2 and abs(p[2] - b["center"][2]) <= b["size"][2] / 2 for b in footprints)

        cells = np.array([p for p in main if not in_pocket(p)])
        self.assertEqual(len(cells), 7729)
        counts = np.zeros(len(cells), int)
        for row in self.bot_points:
            counts += (np.linalg.norm(cells - np.array(row["position"]), axis=1) > 25.0)
        self.assertGreaterEqual(int(counts.min()), 12)
        self.assertGreaterEqual(int(np.median(counts)), 15)
        native_only = np.zeros(len(cells), int)
        for row in self.native_rows:
            native_only += (np.linalg.norm(cells - np.array(row["position"]), axis=1) > 25.0)
        self.assertGreaterEqual(int(native_only.min()), 8)


@unittest.skipUnless((PACKAGE / "scenes").is_dir() and (PACKAGE / "meshes").is_dir(), "no yard-native-v2 package")
class YardPlacementTests(SidecarData, unittest.TestCase):
    """The port's own colliders through the offline SpawnPlacementRepairs.IsValid (tools/validate/spawn_placement_probe.py)."""

    @classmethod
    def setUpClass(cls):
        try:
            import numpy as np
            import port_colliders
            import spawn_placement_probe as probe
        except ImportError:
            raise unittest.SkipTest("numpy is not installed")
        cls.np = np
        cls.cset = port_colliders.load_package_colliders(PACKAGE)
        cls.probe = probe.PlacementProbe.from_collider_set(cls.cset)
        cls.load_data()

    def test_all_twenty_markers_pass_and_stand_on_the_floor(self):
        self.assertEqual(len(self.markers), 20)
        for name, marker in sorted(self.markers.items()):
            verdict = self.probe.check(marker["position"])
            with self.subTest(marker=name):
                self.assertTrue(verdict.valid, f"ground {verdict.ground_ok}, ceiling {verdict.ceiling}, blocked {verdict.blocked[:2]}")
                self.assertLess(abs(verdict.floor_y - marker["position"][1]), 0.25)

    def test_every_row_and_ai_point_stands_on_a_floor_and_passes_the_placement_probe(self):
        rows = [(p["id"], p["position"]) for p in self.players + self.bot_points]
        rows += [(f"core {n}", p) for n, p in self.core.items()] + [(f"born {n}", p) for n, p in self.born.items()]
        self.assertEqual(len(rows), 4 + 18 + 7 + 11)
        for name, position in rows:
            verdict = self.probe.check(position)
            with self.subTest(row=name):
                self.assertTrue(verdict.valid, f"{name}: ground {verdict.ground_ok}, ceiling {verdict.ceiling}, blocked {verdict.blocked[:2]}")
                self.assertLess(abs(verdict.floor_y - position[1]), 0.25)

    def test_the_exit_boxes_hold_no_solid_above_the_floor(self):
        np = self.np
        for exit_point in self.exits["exits"]:
            x, y, z = exit_point["position"]
            verdict = self.probe.check(exit_point["position"])
            self.assertIsNotNone(verdict.floor_y, exit_point["name"])
            self.assertLess(abs(verdict.floor_y - y), 0.25, exit_point["name"])
            # the box is centred 1.0 m above the position (ExfilRepairs): floor - 0.5 .. floor + 2.5; the floor slab itself is below +0.3
            lo = np.array([x - 2.0, verdict.floor_y + 0.3, z - 2.0])
            hi = np.array([x + 2.0, verdict.floor_y + 2.5, z + 2.0])
            inside = np.all((self.probe.mn <= hi) & (self.probe.mx >= lo), axis=1)
            names = sorted({self.probe.owner_paths[int(i)] for i in self.probe.tri_owner[inside]})
            self.assertEqual(names, [], f"{exit_point['name']}: solids inside the box: {names[:3]}")

    def test_the_gate_table_contains_the_package_and_adds_only_the_leaf_swing(self):
        np = self.np
        for name, rect in GATES.items():
            idx = [i for i, o in enumerate(self.cset.owners) if o.path == name or o.path.startswith(name + "/")]
            self.assertTrue(idx, name)
            tris = self.cset.tris[np.isin(self.cset.tri_owner, idx)].reshape(-1, 3)
            lo, hi = tris.min(axis=0), tris.max(axis=0)
            if name.endswith("_A"):
                # the cradle: the plate's footprint is the whole swept footprint (the drive is vertical), within 0.1 m
                for got, want in ((lo[0], rect[0]), (hi[0], rect[1]), (lo[2], rect[2]), (hi[2], rect[3])):
                    self.assertAlmostEqual(got, want, delta=0.1, msg=name)
            else:
                # the north gate: the frame and both leaves lie in the gate plane z 127.78..127.91; x is the table's within 0.05 m, z is the plane
                # widened by the leaf length (2.0 m) and 0.1 m of slack to both sides
                self.assertAlmostEqual(lo[0], rect[0], delta=0.05, msg=name)
                self.assertAlmostEqual(hi[0], rect[1], delta=0.06, msg=name)
                self.assertAlmostEqual(rect[2], lo[2] - 2.1, delta=0.06, msg=name)
                self.assertAlmostEqual(rect[3], hi[2], delta=0.06, msg=name)   # one-sided: leaves swing toward -z only

    def test_the_exit_boxes_clear_the_swept_footprint_of_both_gates(self):
        for exit_point in self.exits["exits"]:
            x, _y, z = exit_point["position"]
            for name, (x0, x1, z0, z1) in GATES.items():
                overlap = x - 2.0 <= x1 and x + 2.0 >= x0 and z - 2.0 <= z1 and z + 2.0 >= z0
                self.assertFalse(overlap, f"{exit_point['name']} touches the swept footprint of {name}")

    def test_the_gate_roots_are_where_the_survey_found_them(self):
        scene = M.SceneGraph("DesignStuff", json.loads(
            (PACKAGE / "scenes" / "Arena_Yard_TeamFight_DesignStuff.json").read_text(encoding="utf-8")))
        roots = {scene.path(o): scene.position(o) for o in scene.objects if scene.path(o).count("/") == 1
                 and scene.path(o).startswith("DOORS/") and scene.path(o) in GATE_ROOTS}
        self.assertEqual(sorted(roots), sorted(GATE_ROOTS))
        self.assertTrue(all(abs(a - b) < 0.05 for a, b in zip(roots["DOORS/Metal_gate_1"], (-9.7, 6.12, 127.84))), roots["DOORS/Metal_gate_1"])


if __name__ == "__main__":
    unittest.main()
