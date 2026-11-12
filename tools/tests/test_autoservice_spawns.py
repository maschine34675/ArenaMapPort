"""Autoservice data contract (docs/PLAN-AUTOSERVICE.md item 6, 2026-11-11): tools/maps/autoservice.bots.json, autoservice.exits.json,
autoservice.modes.json and autoservice.ai.json against each other, against Arena's own NavMesh and against the port's colliders.

Why: nobody has walked Autoservice yet, so everything checkable without the host is pinned here, in three layers (the Iceberg
pattern of test_iceberg_spawns.py).

1. Data only (the repository): the sidecars have the shape the released maps' have and the generator turns them into the
   base.json fields; the player rows are four of the 20 pocket markers of autoservice.modes.json, the bot rows are Arena's AI
   core points and born points under a stated rule (checked both ways; nine rows appended on 2026-11-12), nothing stands inside a team
   box, each pocket holds one exit (since 2026-11-12, see below) that keeps 2.5 m from every row.
2. Arena's NavMesh (the installed Arena 0.6.2, level63, ``sharedassets63.assets`` path 2, identity placement, 30 tiles, 695
   polygons): all 20 markers, the capture point, the 7 core points, the 16 born points and all 59 patrol points lie on the
   1,915.6 m2 main component; every row and exit stands on it with the documented clearance; 21 of the 23 mine points do.
3. The port's colliders (the ignored package work/extract/autoservice-native-v2 through tools/validate/spawn_placement_probe.py,
   the offline stand-in for SpawnPlacementRepairs.IsValid): every marker, row, core point and born point stands on a floor and
   passes, the exit boxes hold no solid above the floor, and the swept footprints of the four gates are the ones the rule uses.

The C# half (descriptor Exits mirror, server parse) is the lead's promotion test (item 12).

2026-11-12 (more spawn points and exits inside the pockets; offline, host check pending): nine Bot rows are appended after the seven original
ones and the two exits moved into their team pockets. Rules, replacing the retired ones where they changed:

* R1: the seven original rows stay byte-identical and first (ArenaMode pins ``core-03`` by id and floats); the appended rows follow in order.
* R5: the 2026-11-11 conventions (10 m to a gate's swept footprint and to every pocket marker, 5 m outside both spawn bounds, 10 m to every Player
  row) hold for the ORIGINAL rows only. The yard is 58 m long and ArenaMode's burst selector wants Bot rows 25 m or more from the player, so the
  appended rows stand at the yard's ends under the weaker rule: 2.5 m from a gate's swept footprint, 1.5 m outside both bounds, 5 m from every
  Player row, 4 m from every other row (what the decided rows measure, with a small margin).
* R3: the retired rule "exits stand outside the pockets, 10 m from every row, beyond the gates" is replaced by: every pocket holds exactly one exit
  whose centre lies inside the pocket's spawn-bound footprint of autoservice.modes.json (the green signal flare then marks the team's start area),
  every Player and Bot row keeps 2.5 m to the box EDGE (the Equator booth precedent), the box holds floor and no solid, and it never touches a
  gate's swept volume (it may stand 1.4 m from the footprint: the east box lies beside the big gate).
* R6: no literal exit coordinates here, only rules and mirrors; the C# tests pin descriptor == sidecar == base.json.
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
PACKAGE = WORK / "extract" / "autoservice-native-v2"

spec = importlib.util.spec_from_file_location("make_base_json", REPO / "tools/server/make_base_json.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)

import extract_mode_objects as M  # noqa: E402

NAVMESH = ("sharedassets63.assets", 2)
# Player row id suffix -> (sides, original pocket marker). East = SPAWN_BOUND_Fighter, west = SPAWN_BOUND_Fighter (1).
PLAYER_MARKERS = {
    "all": (["All"], "Usec house (1)"),
    "usec": (["Usec"], "Usec house (4)"),
    "bear": (["Bear"], "Usec house (6)"),
    "west-all": (["All"], "Usec house (14)"),
}
# Bot rows: id suffix -> the AI scene point they start from; the rows that do not sit on it, with the largest move allowed (m).
BOT_CORE_POINTS = {f"core-{n:02d}": n for n in (1, 2, 3, 4, 5, 7)}
BOT_BORN_POINTS = {"born-00": "p1"}
MOVED_FROM_POINT = {"core-01": 0.75, "core-03": 0.6, "core-04": 2.0, "born-00": 0.75}
# R1: the seven original rows, in file order, then the nine rows appended on 2026-11-12 (decision order). Appended rows: id suffix -> (source, largest
# move in metres). The LastHero markers are Arena's free-for-all spawn points (Arena_AutoService_LastHero_DesignStuff, not part of the package):
# their world positions are pinned here so that the move can be checked; the fill rows have no Arena source.
ORIGINAL_BOT_SUFFIXES = ["core-01", "core-02", "core-03", "core-04", "core-05", "core-07", "born-00"]
APPENDED_BOT_SUFFIXES = ["lasthero-04", "lasthero-13", "fill-01", "core-06", "lasthero-03", "lasthero-10", "fill-02", "fill-03", "fill-04"]
LASTHERO_MARKERS = {"lasthero-04": ("Usec house (4)", (-350.260, -3.759, 510.330)),
                    "lasthero-13": ("Usec house (13)", (-391.440, -3.800, 469.530)),
                    "lasthero-03": ("Usec house (3)", (-346.522, -3.805, 495.476)),
                    "lasthero-10": ("Usec house (10)", (-399.624, -3.805, 498.342))}
APPENDED_MOVE = {"lasthero-04": 1.0, "lasthero-13": 0.5, "lasthero-03": 0.51, "lasthero-10": 0.51, "core-06": 1.0}
# ArenaMode pins this donor row by id and exact floats (R1): never touch it.
DONOR_ROW = ("arena_autoservice-bot-core-03", [-367.44, -3.79, 492.11])
# R5, the rules of the appended rows (the decided rows measure 2.79 m, 2.67 m, 9.63 m and 6.0 m)
APPENDED_GATE_DISTANCE = 2.5
APPENDED_BOUND_DISTANCE = 1.5
APPENDED_PLAYER_DISTANCE = 5.0
APPENDED_MARKER_DISTANCE = 7.0         # to a pocket marker (the decided rows measure 8.0 m at the least; the original rule was 10 m)
# R1, the literal point of this pin: the seven original rows as they stood at e4b1d04, byte for byte (id, position, radius, categories)
ORIGINAL_ROWS = [("core-01", [-372.74, -3.79, 503.03]), ("core-02", [-368.43, -3.79, 501.75]), ("core-03", [-367.44, -3.79, 492.11]),
                 ("core-04", [-385.94, -3.79, 493.88]), ("core-05", [-384.98, -3.79, 476.7]), ("core-07", [-354.41, -3.79, 499.38]),
                 ("born-00", [-383.73, -3.79, 507.89])]
# Candidates the ORIGINAL 10 m rule skipped, and why (the rule is in autoservice.bots.json's comment; core 6 is used by an appended row under R5):
# core 6 and the born points beside a pocket marker or a gate are skipped by the 10 m rule; born point 1 duplicates born point 0 (1.8 m); born points 7 and 8 have no spot with
# 1.5 m of clearance within 3.5 m.
SKIPPED_CORE_POINTS = (6,)
SKIPPED_BORN_POINTS = (2, 4, 5, 6, 9, 10, 11, 12, 13, 14, 15, 16)
DROPPED_BORN_POINTS = {1: "duplicate", 7: "clearance", 8: "clearance"}
BOT_SKIP_RADIUS = 10.0
BOT_DUPLICATE_RADIUS = 4.0
# The four gates (package DesignStuff DOORS/<path>): closed collider footprint (x0, x1, z0, z1) and the drive that opens them, decoded in
# survey S2 (PLAN-AUTOSERVICE section 0): the big gate slides +4.40 m along world z, the rollers rise (+2.56 m, +4.11 m) so only the
# vertical extent changes.
GATES = {
    "DOORS/big_gate_metal": {"closed": (-344.20, -343.94, 474.85, 484.26), "y": (-3.74, 0.97), "drive": (0.0, 0.0, 4.40)},
    "DOORS/equator_roller_shutters_01": {"closed": (-344.77, -344.44, 505.89, 507.78), "y": (-4.34, -1.58), "drive": (0.0, 2.56, 0.0)},
    "DOORS/roller_shutters_techlight_01": {"closed": (-402.47, -402.05, 498.87, 503.57), "y": (-3.78, 0.92), "drive": (0.0, 4.11, 0.0)},
    "DOORS/roller_shutters_techlight_01 (1)": {"closed": (-402.47, -402.05, 475.16, 479.86), "y": (-3.78, 0.92), "drive": (0.0, 4.11, 0.0)},
}
MIN_PLAYER_CLEARANCE = 1.7         # border clearance in Arena's mesh (west-all is the tightest, 1.74 m)
MIN_BOT_CLEARANCE = 1.35           # the Iceberg bar; the original rows keep 1.50 m or more
# R5: Arena's own mesh gives three LastHero rows 1.32 to 1.35 m (the built mesh 1.37 m, MIN_BOT_CLEARANCE holds there for every row)
MIN_APPENDED_ARENA_CLEARANCE = 1.30
MIN_EXIT_ROW_EDGE = 2.5            # R3: metres between an exit box edge and any Player or Bot row
MIN_EXIT_GATE_EDGE = 1.4           # R3: metres between the box edge and a gate's swept footprint (was 2.5 until 2026-11-12: the east box lies beside the big gate)
MIN_EXIT_ARENA_CLEARANCE = 1.9     # border clearance of an exit centre in Arena's mesh (west 1.99 m, east 3.20 m)
EXIT_HALF_WIDTH = 2.0              # boxes are 4 x 3 x 4 m with rotation 0
MIN_EXIT_FLOOR_SHARE = 0.95        # share of a 0.5 m lattice under the box that has NavMesh (measured: east 100 %, west 79 of 81 = 97.5 %, the narrow warehouse)
# Mine points that lie off Arena's mesh (AMinePlace_15 and AMinePlace_44; the plan expected only _15)
OFF_MESH_MINES = ("AMinePlace_15", "AMinePlace_44")


def load(stem):
    return json.loads((REPO / "tools" / "maps" / stem).read_text(encoding="utf-8"))


def xz(point):
    return (point[0], point[2])


def swept_rect(gate):
    """Horizontal footprint (x0, x1, z0, z1) of a gate over its whole travel."""
    x0, x1, z0, z1 = gate["closed"]
    dx, _dy, dz = gate["drive"]
    return (min(x0, x0 + dx), max(x1, x1 + dx), min(z0, z0 + dz), max(z1, z1 + dz))


def rect_distance(point, rect):
    return math.hypot(max(rect[0] - point[0], 0.0, point[0] - rect[1]), max(rect[2] - point[2], 0.0, point[2] - rect[3]))


def nearest_gate(point):
    return min(rect_distance(point, swept_rect(g)) for g in GATES.values())


def box_edge_distance(point, exit_point):
    """Horizontal distance from a point to the EDGE of an exit box (4 x 4 m footprint, rotation 0); 0 inside the footprint."""
    return math.hypot(max(abs(point[0] - exit_point["position"][0]) - EXIT_HALF_WIDTH, 0.0),
                      max(abs(point[2] - exit_point["position"][2]) - EXIT_HALF_WIDTH, 0.0))


def exit_pocket(exit_point, bounds):
    """The spawn bounds whose footprint holds the exit's centre (R3: exactly one per exit)."""
    return [name for name, bound in bounds.items() if M.box_margin(bound, exit_point["position"]) >= 0.0]


class SidecarData:
    """The five tracked Autoservice sidecars, loaded once per test class."""

    @classmethod
    def load_data(cls):
        cls.bots, cls.exits, cls.map = load("autoservice.bots.json"), load("autoservice.exits.json"), load("autoservice.json")
        cls.modes, cls.ai = load("autoservice.modes.json"), load("autoservice.ai.json")
        cls.players, cls.bot_points = cls.bots["player_spawns"], cls.bots["bot_spawns"]
        cls.markers = {m["name"]: m for side in cls.modes["spawn_markers"].values() for m in side}
        cls.bounds = {b["name"]: b for b in cls.modes["spawn_bounds"]}
        cls.original_rows, cls.appended_rows = cls.bot_points[:7], cls.bot_points[7:]
        cls.core = {c["id"]: c["position"] for c in cls.ai["core_points"]}
        cls.born = {b["name"]: b["position"] for b in cls.ai["born_points"]}


def born_name(number):
    return "p1" if number == 0 else f"p1 ({number})"


class AutoserviceSidecarTests(SidecarData, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.load_data()

    # ------------------------------------------------------------------ shape
    def test_counts_and_ids(self):
        self.assertEqual((len(self.players), len(self.bot_points)), (4, 16))
        self.assertEqual((len(self.bots["waves"]), len(self.bots["boss_waves"]), len(self.exits["exits"])), (5, 4, 2))
        self.assertEqual((self.bots["zone"], self.bots["max_bots"]), ("BotZone_Main", 8))
        ids = [p["id"] for p in self.players + self.bot_points]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual({p["id"] for p in self.players}, {f"arena_autoservice-player-{s}" for s in PLAYER_MARKERS})
        self.assertEqual({p["id"] for p in self.original_rows},
                         {f"arena_autoservice-bot-{s}" for s in list(BOT_CORE_POINTS) + list(BOT_BORN_POINTS)})
        # R1: the seven original rows come first and unchanged, the nine appended rows follow in the decided order
        self.assertEqual([p["id"] for p in self.original_rows], [f"arena_autoservice-bot-{s}" for s in ORIGINAL_BOT_SUFFIXES])
        self.assertEqual([p["id"] for p in self.appended_rows], [f"arena_autoservice-bot-{s}" for s in APPENDED_BOT_SUFFIXES])
        self.assertFalse([i for i in ids if "tdm-v1" in i])          # ArenaMode's own ids are never reused
        donor = next(p for p in self.bot_points if p["id"] == DONOR_ROW[0])
        self.assertEqual(donor["position"], DONOR_ROW[1])

    def test_the_seven_original_rows_are_unchanged(self):
        """R1: existing rows never move (ArenaMode pins six donor rows by id and exact floats, one of them here). The point of this test is the literal."""
        self.assertEqual(self.original_rows,
                         [{"id": f"arena_autoservice-bot-{s}", "position": pos, "radius": 25, "categories": ["Bot", "BotPmc"]} for s, pos in ORIGINAL_ROWS])

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
        self.assertIn("2026-11-11", self.bots["_comment"])
        self.assertIn("2026-11-11", self.exits["_comment"])
        self.assertIn("2026-11-12", self.bots["_comment"])
        self.assertIn("2026-11-12", self.exits["_comment"])

    def test_waves_and_pmc_squads_are_the_released_maps(self):
        airpit, bowl, iceberg = load("airpit.bots.json"), load("bowl.bots.json"), load("iceberg.bots.json")
        for key in ("waves", "boss_waves"):
            for other in (airpit, bowl, iceberg):
                self.assertEqual(self.bots[key], other[key])
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
                         (20, 5, 4, 2))
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
        self.assertEqual({p["id"].split("player-")[1]: p["name"] for p in self.players},
                         {"all": "SpawnPoint_Player_All", "usec": "SpawnPoint_Player_Usec", "bear": "SpawnPoint_Player_Bear",
                          "west-all": "SpawnPoint_Player_West_All"})
        for exit_point in self.exits["exits"]:
            self.assertEqual(exit_point["box_size"], [4.0, 3.0, 4.0])
            self.assertEqual((exit_point["type"], exit_point["chance"], exit_point["exfiltration_time"], exit_point["rotation"]),
                             ("Individual", 100, 8, 0.0))
        self.assertEqual([e["name"] for e in self.exits["exits"]], ["Chop Shop East Gate", "Chop Shop West Gate"])
        self.assertGreater(self.exits["exits"][0]["position"][0], self.exits["exits"][1]["position"][0])

    # ------------------------------------------------------------------ player rows
    def test_player_rows_are_the_world_positions_of_four_pocket_markers(self):
        by_id = {p["id"]: p for p in self.players}
        for suffix, (sides, marker_name) in PLAYER_MARKERS.items():
            row, marker = by_id[f"arena_autoservice-player-{suffix}"], self.markers[marker_name]
            with self.subTest(row=suffix):
                self.assertEqual(row["sides"], sides)
                self.assertEqual(row["position"], [round(v, 2) for v in marker["position"]])
                self.assertEqual(row["rotation"], round(marker["yaw"], 1))

    def test_usec_and_all_start_in_the_east_pocket_bear_and_all_in_the_west_one(self):
        east, west = self.bounds["SPAWN_BOUND_Fighter"], self.bounds["SPAWN_BOUND_Fighter (1)"]
        for row in self.players:
            is_east = row["position"][0] > -370.0
            bound = east if is_east else west
            self.assertGreaterEqual(M.box_margin(bound, row["position"]), 2.0, row["id"])
            if row["sides"] == ["Usec"]:
                self.assertTrue(is_east, row["id"])
            if row["sides"] == ["Bear"]:
                self.assertFalse(is_east, row["id"])
        for side in ("Bear", "Usec"):
            xs = [p["position"][0] for p in self.players if side in p["sides"] or "All" in p["sides"]]
            self.assertTrue(any(x > -345 for x in xs), side)
            self.assertTrue(any(x < -400 for x in xs), side)
        self.assertEqual({tuple(p["sides"]) for p in self.players}, {("All",), ("Usec",), ("Bear",)})
        east_markers = {m["name"] for m in self.modes["spawn_markers"]["east"]}
        self.assertEqual({PLAYER_MARKERS[s][1] in east_markers for s in ("all", "usec")}, {True})
        self.assertEqual({PLAYER_MARKERS[s][1] in east_markers for s in ("bear", "west-all")}, {False})

    # ------------------------------------------------------------------ bot rows
    def test_bot_rows_sit_on_their_point_except_the_four_documented_moves(self):
        by_id = {p["id"]: p for p in self.original_rows}
        sources = {**{k: self.core[n] for k, n in BOT_CORE_POINTS.items()}, **{k: self.born[born_name(int(k[-2:]))]
                                                                              for k in BOT_BORN_POINTS}}
        for suffix, source in sources.items():
            row = by_id[f"arena_autoservice-bot-{suffix}"]["position"]
            moved = math.dist(xz(row), xz(source))
            with self.subTest(row=suffix):
                if suffix in MOVED_FROM_POINT:
                    self.assertGreater(moved, 0.5)
                    self.assertLessEqual(moved, MOVED_FROM_POINT[suffix])
                else:
                    self.assertAlmostEqual(moved, 0.0, delta=0.006)
                # y is Arena's NavMesh surface at the row: the point's own within 4 cm, within 8 cm after a move (born-00: 0.07 m)
                self.assertAlmostEqual(row[1], source[1], delta=0.08 if suffix in MOVED_FROM_POINT else 0.04)

    def test_appended_rows_come_from_arena_markers_or_are_mesh_fill_points(self):
        by_id = {p["id"].split("bot-")[1]: p["position"] for p in self.appended_rows}
        for suffix, (_name, source) in LASTHERO_MARKERS.items():
            moved = math.dist(xz(by_id[suffix]), xz(source))
            with self.subTest(row=suffix):
                self.assertGreater(moved, 0.4)
                self.assertLessEqual(moved, APPENDED_MOVE[suffix] + 0.005)
                self.assertAlmostEqual(by_id[suffix][1], source[1], delta=0.05)
        moved = math.dist(xz(by_id["core-06"]), xz(self.core[6]))
        self.assertGreater(moved, 0.9)
        self.assertLessEqual(moved, APPENDED_MOVE["core-06"] + 0.005)
        self.assertAlmostEqual(by_id["core-06"][1], self.core[6][1], delta=0.05)
        # the fill rows have no Arena source: they stand on a 2 m lattice of the mesh
        for suffix in ("fill-01", "fill-02", "fill-03", "fill-04"):
            self.assertEqual(by_id[suffix][0] % 2.0, 0.0, suffix)
            self.assertEqual(by_id[suffix][2] % 2.0, 0.0, suffix)

    def test_the_skip_rule_no_bot_spawn_within_ten_metres_of_a_gate_or_a_pocket_marker(self):
        # R5: the 10 m rule is the rule of the ORIGINAL rows; core point 6 still fails it (9.0 m from the east big gate) and is now used
        # by an appended row that moved 1.0 m (test above) under the weaker rule of test_appended_rows_obey_the_weaker_rule.
        pocket = [m["position"] for m in self.markers.values()]

        def nearest(position):
            return min(nearest_gate(position), min(math.dist(xz(position), xz(m)) for m in pocket))

        skipped_cores = {n for n, p in self.core.items() if nearest(p) < BOT_SKIP_RADIUS}
        self.assertEqual(skipped_cores, set(SKIPPED_CORE_POINTS))
        skipped_born = {int(name.split("(")[1].rstrip(")")) if "(" in name else 0
                        for name, p in self.born.items() if nearest(p) < BOT_SKIP_RADIUS}
        self.assertEqual(skipped_born, set(SKIPPED_BORN_POINTS))
        for number in set(self.core) - set(SKIPPED_CORE_POINTS):
            self.assertGreaterEqual(nearest(self.core[number]), BOT_SKIP_RADIUS, f"core {number} is on a gate approach")
        for row in self.original_rows:
            self.assertGreaterEqual(nearest(row["position"]), BOT_SKIP_RADIUS, row["id"])
        # the 16 born points are 10 in the pockets (skipped), 6 in the yard: one row, one duplicate, two without room, two at the gate
        self.assertEqual(len(self.born), 16)
        kept = set(range(0, 17)) - {3} - set(SKIPPED_BORN_POINTS)
        self.assertEqual(kept, {0} | set(DROPPED_BORN_POINTS))
        self.assertEqual(set(self.core) - set(SKIPPED_CORE_POINTS), set(BOT_CORE_POINTS.values()))

    def test_the_duplicate_born_point_is_within_four_metres_of_the_row_it_duplicates(self):
        row0 = next(p for p in self.original_rows if p["id"].endswith("born-00"))
        self.assertLess(math.dist(xz(self.born["p1 (1)"]), xz(self.born["p1"])), BOT_DUPLICATE_RADIUS)
        self.assertLess(math.dist(xz(self.born["p1 (1)"]), xz(row0["position"])), BOT_DUPLICATE_RADIUS)
        for number in (7, 8):          # the other two kept candidates are far from every row: they were dropped for room, not as duplicates
            for row in self.original_rows:
                self.assertGreater(math.dist(xz(self.born[born_name(number)]), xz(row["position"])), BOT_DUPLICATE_RADIUS)

    def test_no_row_or_core_point_lies_inside_a_team_box(self):
        pockets = list(self.bounds.values())
        for number, position in self.core.items():
            for bound in pockets:
                self.assertLess(M.box_margin(bound, position), 0.0, f"core {number} inside {bound['name']}")
        for row in self.original_rows:
            for bound in pockets:
                self.assertLess(M.box_margin(bound, row["position"]), -5.0, row["id"])
        # R5: the appended rows keep 1.5 m outside both bounds (the original 5 m cannot hold at the yard's ends)
        for row in self.appended_rows:
            for bound in pockets:
                self.assertLess(M.box_margin(bound, row["position"]), -APPENDED_BOUND_DISTANCE, row["id"])
        # R3 successor of "the exits clear the bounds": see test_every_pocket_holds_one_exit_inside_its_footprint

    def test_appended_rows_obey_the_weaker_rule(self):
        """R5: what the nine appended rows keep, stated once (the original seven keep the stricter rules above and below)."""
        pocket = [m["position"] for m in self.markers.values()]
        for row in self.appended_rows:
            position = row["position"]
            with self.subTest(row=row["id"]):
                self.assertGreaterEqual(nearest_gate(position), APPENDED_GATE_DISTANCE)
                self.assertGreaterEqual(min(math.dist(xz(position), xz(m)) for m in pocket), APPENDED_MARKER_DISTANCE)
                for player in self.players:
                    self.assertGreaterEqual(math.dist(position, player["position"]), APPENDED_PLAYER_DISTANCE, player["id"])

    def test_spacing_between_rows(self):
        for bot in self.bot_points:
            for player in self.players:       # R5: 10 m for the original rows, 5 m for the appended ones
                need = 10.0 if bot in self.original_rows else APPENDED_PLAYER_DISTANCE
                self.assertGreaterEqual(math.dist(bot["position"], player["position"]), need, f"{bot['id']} / {player['id']}")
            for other in self.bot_points:
                if other is not bot:
                    self.assertGreaterEqual(math.dist(bot["position"], other["position"]), BOT_DUPLICATE_RADIUS,
                                            f"{bot['id']} / {other['id']}")
        for a in self.players:
            for b in self.players:
                if a is not b:
                    self.assertGreaterEqual(math.dist(a["position"], b["position"]), 4.0, f"{a['id']} / {b['id']}")

    # ------------------------------------------------------------------ exits
    def test_every_pocket_holds_one_exit_inside_its_footprint(self):
        """R3 (2026-11-12): one exit per team pocket, its centre inside the pocket's spawn-bound footprint, so that the signal flare marks
        the team's start area. Replaces 'the exits stand outside the pockets, beyond the gates' of 2026-11-11."""
        self.assertEqual(len(self.exits["exits"]), len(self.bounds))
        by_name = {}
        for exit_point in self.exits["exits"]:
            holders = exit_pocket(exit_point, self.bounds)
            self.assertEqual(len(holders), 1, f"{exit_point['name']} is inside {holders}")
            by_name[exit_point["name"]] = holders[0]
            for dx in (-EXIT_HALF_WIDTH, EXIT_HALF_WIDTH):             # the whole 4 x 4 m footprint lies inside as well (measured, not demanded by R3)
                for dz in (-EXIT_HALF_WIDTH, EXIT_HALF_WIDTH):
                    corner = (exit_point["position"][0] + dx, exit_point["position"][1], exit_point["position"][2] + dz)
                    self.assertGreaterEqual(M.box_margin(self.bounds[holders[0]], corner), 0.0, exit_point["name"])
        self.assertEqual(set(by_name.values()), set(self.bounds))
        self.assertEqual(by_name, {"Chop Shop East Gate": "SPAWN_BOUND_Fighter", "Chop Shop West Gate": "SPAWN_BOUND_Fighter (1)"})

    def test_no_row_starts_inside_an_exit_box_and_the_box_keeps_clear_of_the_gates(self):
        """R3: every Player and Bot row keeps 2.5 m to the box EDGE (a plain-raid spawn must not start inside a trigger; the Equator booth
        precedent), and the box stays off every gate's swept footprint by 1.4 m (2.5 m until 2026-11-12)."""
        for exit_point in self.exits["exits"]:
            for row in self.players + self.bot_points:
                self.assertGreaterEqual(box_edge_distance(row["position"], exit_point), MIN_EXIT_ROW_EDGE,
                                        f"{row['id']} too close to {exit_point['name']}")
            edge = nearest_gate(exit_point["position"]) - EXIT_HALF_WIDTH            # conservative: centre distance minus the half width
            self.assertGreaterEqual(edge, MIN_EXIT_GATE_EDGE, f"{exit_point['name']}: the box edge is {edge:.2f} m from a gate's travel")

    def test_the_gate_table_is_consistent(self):
        self.assertEqual(len(GATES), 4)
        for got, want in zip(swept_rect(GATES["DOORS/big_gate_metal"]), (-344.20, -343.94, 474.85, 488.66)):
            self.assertAlmostEqual(got, want, places=6)
        self.assertEqual(swept_rect(GATES["DOORS/roller_shutters_techlight_01"]), GATES["DOORS/roller_shutters_techlight_01"]["closed"])


def mesh_available():
    return (ARENA_DATA / NAVMESH[0]).is_file()


@unittest.skipUnless(mesh_available(), "Arena is not installed")
class AutoserviceArenaMeshTests(SidecarData, unittest.TestCase):
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
        self.assertEqual((self.meta["tiles_total"], self.mesh.poly_count), (30, 695))
        components = self.no.component_summary(self.mesh)
        self.assertEqual([c["polygons"] for c in components], [675, 20])
        main, island = components
        self.assertAlmostEqual(main["area_xz"], 1915.6, delta=0.5)
        for got, want in zip(main["min"] + main["max"], [-412.4, -3.8, 466.6, -333.7, -0.4, 511.9]):
            self.assertAlmostEqual(got, want, delta=0.06)
        # the second island (33.8 m2, x -336.2..-324.2, z 521.3..527.7, outside the border cubes) holds none of our points
        self.assertAlmostEqual(island["area_xz"], 33.8, delta=0.3)

    def test_all_twenty_markers_stand_on_the_main_component_with_room(self):
        names = sorted(self.markers)
        self.assertEqual(len(names), 20)
        results = dict(zip(names, self.locate([self.markers[n]["position"] for n in names])))
        for name, (label, sy, clearance) in results.items():
            with self.subTest(marker=name):
                self.assertEqual(label, 0)
                self.assertLess(abs(sy - self.markers[name]["position"][1]), 0.2)       # (0) stands 0.15 m above the mesh
                self.assertGreaterEqual(clearance, 0.9)          # (19) is the tightest with 0.92 m

    def test_the_capture_point_and_all_the_ai_points_are_on_the_main_component(self):
        capture = self.modes["capture_point"]["position"]
        label, _sy, clearance = self.locate([capture])[0]
        self.assertEqual(label, 0)
        self.assertGreaterEqual(clearance, 2.0)
        core = self.core
        self.assertEqual(len(core), 7)
        for number, (label, sy, clearance) in zip(core, self.locate([core[n] for n in core])):
            self.assertEqual(label, 0, f"core {number}")
            self.assertLess(abs(sy - core[number][1]), 0.02, f"core {number}")
            self.assertGreaterEqual(clearance, 0.8, f"core {number}")
        for name, (label, sy, _c) in zip(self.born, self.locate(list(self.born.values()))):
            self.assertEqual(label, 0, name)
            self.assertLess(abs(sy - self.born[name][1]), 0.25, name)       # (6) stands 0.22 m off the surface, the others within 0.1 m
        points = [h["position"] for h in self.ai["patrol_ways"][0]["hubs"]] \
            + [s for h in self.ai["patrol_ways"][0]["hubs"] for s in h["sub_points"]]
        self.assertEqual(len(points), 59)
        for position, (label, _sy, _c) in zip(points, self.locate(points)):
            self.assertEqual(label, 0, f"patrol point {position}")

    def test_twenty_one_of_the_twenty_three_mine_points_are_on_the_main_component(self):
        mines = self.ai["mine_points"]
        self.assertEqual(len(mines), 23)
        results = dict(zip([m["name"] for m in mines], [r[0] for r in self.locate([m["position"] for m in mines])]))
        off = sorted(n for n, label in results.items() if label != 0)
        self.assertEqual(off, sorted(OFF_MESH_MINES))
        self.assertTrue(all(label == -1 for n, label in results.items() if n in OFF_MESH_MINES))

    def test_rows_and_exits_stand_on_the_main_component_with_room(self):
        rows = [(p["id"], p["position"], MIN_PLAYER_CLEARANCE, 0.3) for p in self.players]
        rows += [(p["id"], p["position"], MIN_BOT_CLEARANCE, 0.05) for p in self.original_rows]
        rows += [(p["id"], p["position"], MIN_APPENDED_ARENA_CLEARANCE, 0.05) for p in self.appended_rows]      # R5: 1.32 m is the least
        # R3: the exit boxes stand in the pockets, where Arena's mesh leaves less room than in the yard (west: 1.99 m at the centre, the box is not
        # clear of the border); the floor under the whole box is checked on the built mesh (AutoserviceBuiltMeshTests). The east centre y is the
        # built mesh's interpolated surface (-3.60), 0.19 m above Arena's (-3.79), so only the east exit has the dy bar 0.25; the west one keeps 0.05.
        rows += [(e["name"], e["position"], MIN_EXIT_ARENA_CLEARANCE, 0.25 if "East" in e["name"] else 0.05) for e in self.exits["exits"]]
        for (name, position, need, dy), (label, sy, clearance) in zip(rows, self.locate([r[1] for r in rows])):
            self.assertEqual(label, 0, f"{name} is not on Arena's main component")
            self.assertLessEqual(abs(sy - position[1]), dy, f"{name} is {sy - position[1]:+.2f} m off Arena's surface")
            self.assertGreaterEqual(clearance, need, f"{name} is only {clearance:.2f} m from the NavMesh border")

    def test_the_bot_moves_were_needed_and_the_dropped_candidates_have_no_room_nearby(self):
        # a row moves only when its point keeps less than 1.35 m of clearance; the points that stay keep more
        by_id = {p["id"].split("bot-")[1]: p for p in self.original_rows}
        for suffix in by_id:
            source = self.core[BOT_CORE_POINTS[suffix]] if suffix in BOT_CORE_POINTS else self.born[born_name(int(suffix[-2:]))]
            clearance = self.locate([source])[0][2]
            if suffix in MOVED_FROM_POINT:
                self.assertLess(clearance, 1.35, suffix)
            else:
                self.assertGreaterEqual(clearance, 1.35, suffix)
        # the core point at the gate and the two born points without room: no spot within 3.5 m that obeys the ORIGINAL rows' rules (10 m from
        # every gate's travel and pocket marker) keeps 1.5 m of clearance. The 2026-11-11 clause "and 10 m from an exit centre" is retired with
        # the old exit positions (R3: the exits moved into the pockets); the clause never excluded a spot there (core 6 is 10.4 m from the old east exit).
        np = self.np
        pocket = [m["position"] for m in self.markers.values()]
        for label, source in (("core 6", self.core[6]), ("born 7", self.born["p1 (7)"]), ("born 8", self.born["p1 (8)"])):
            grid = [(source[0] + dx, source[2] + dz) for dx in np.arange(-3.5, 3.51, 0.25) for dz in np.arange(-3.5, 3.51, 0.25)
                    if math.hypot(dx, dz) <= 3.5]
            grid = [(x, z) for x, z in grid if nearest_gate((x, 0.0, z)) >= BOT_SKIP_RADIUS
                    and min(math.dist((x, z), xz(m)) for m in pocket) >= BOT_SKIP_RADIUS]
            best = max((c for lab, _s, c in self.locate([(x, source[1], z) for x, z in grid]) if lab == 0), default=0.0) if grid else 0.0
            self.assertLess(best, 1.5, f"{label} has an admissible spot with {best:.2f} m of clearance within 3.5 m")
            self.assertLess(self.locate([source])[0][2], 0.9, label)

    def test_the_appended_rows_keep_the_least_clearance_of_arenas_mesh_but_core_6_moved_for_room(self):
        # R5: core point 6 keeps 0.83 m in Arena's mesh; its row moved 1.0 m onto open floor (1.5 m); three LastHero rows keep 1.32 to 1.35 m
        self.assertLess(self.locate([self.core[6]])[0][2], 0.9)
        by_id = {p["id"].split("bot-")[1]: p["position"] for p in self.appended_rows}
        self.assertGreaterEqual(self.locate([by_id["core-06"]])[0][2], 1.5)
        for suffix in ("fill-01", "fill-02", "fill-03", "fill-04"):
            self.assertGreaterEqual(self.locate([by_id[suffix]])[0][2], 1.6, suffix)


def built_navmesh_path():
    return WORK / "build" / "autoservice" / "arena_autoservice_navmesh.bundle"


@unittest.skipUnless(built_navmesh_path().is_file(), "no built Autoservice NavMesh bundle (set ARENAMAPPORT_WORK_DIR)")
class AutoserviceBuiltMeshTests(SidecarData, unittest.TestCase):
    """The NavMesh the player's raid uses (work/build/autoservice, baked from the port's colliders), not Arena's own."""

    @classmethod
    def setUpClass(cls):
        try:
            import numpy as np
            import navmesh_diff as nd
            import navmesh_oracle as no
        except ImportError:
            raise unittest.SkipTest("numpy or UnityPy is not installed")
        cls.np = np
        cls.mesh = nd.load_bundle_mesh(built_navmesh_path())
        cls.main_label = int(max(no.component_summary(cls.mesh), key=lambda c: c["polygons"])["label"])
        cls.load_data()

    def locate(self, position):
        np = self.np
        poly, surf = self.mesh.locate(np.array([position], dtype=float), max_dy=1.5)
        if poly[0] < 0:
            return None, None, None
        clearance = float(self.mesh.clearance(np.array([[position[0], surf[0], position[2]]]), dy=1.2)[0])
        return int(self.mesh.labels[poly[0]]), float(surf[0]), clearance

    def test_every_bot_row_stands_on_the_play_component_with_room(self):
        for row in self.bot_points:
            label, surf, clearance = self.locate(row["position"])
            with self.subTest(row=row["id"]):
                self.assertEqual(label, self.main_label)
                self.assertLessEqual(abs(surf - row["position"][1]), 0.05)
                self.assertGreaterEqual(clearance, MIN_BOT_CLEARANCE)

    def test_every_exit_box_has_floor_under_it(self):
        """R3: NavMesh under the centre (on the play component) and under at least 90 % of a 0.5 m lattice below the 4 x 4 m footprint."""
        np = self.np
        for exit_point in self.exits["exits"]:
            x, y, z = exit_point["position"]
            label, surf, _clearance = self.locate(exit_point["position"])
            self.assertEqual(label, self.main_label, exit_point["name"])
            self.assertLessEqual(abs(surf - y), 0.05, exit_point["name"])
            lattice = np.array([[x + dx, y, z + dz] for dx in np.arange(-2.0, 2.01, 0.5) for dz in np.arange(-2.0, 2.01, 0.5)], dtype=float)
            poly, _surf = self.mesh.locate(lattice, max_dy=1.5)
            share = float((poly >= 0).sum()) / len(lattice)
            self.assertGreaterEqual(share, MIN_EXIT_FLOOR_SHARE, f"{exit_point['name']}: floor under {share:.0%} of the box")


@unittest.skipUnless((PACKAGE / "scenes").is_dir() and (PACKAGE / "meshes").is_dir(), "no autoservice-native-v2 package")
class AutoservicePlacementTests(SidecarData, unittest.TestCase):
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
                self.assertLess(abs(verdict.floor_y - marker["position"][1]), 0.05)

    def test_every_row_and_ai_point_stands_on_a_floor_and_passes_the_placement_probe(self):
        rows = [(p["id"], p["position"]) for p in self.players + self.bot_points]
        rows += [(f"core {n}", p) for n, p in self.core.items()] + [(f"born {n}", p) for n, p in self.born.items()]
        self.assertEqual(len(rows), 4 + 16 + 7 + 16)
        for name, position in rows:
            verdict = self.probe.check(position)
            with self.subTest(row=name):
                self.assertTrue(verdict.valid, f"{name}: ground {verdict.ground_ok}, ceiling {verdict.ceiling}, blocked {verdict.blocked[:2]}")
                self.assertLess(abs(verdict.floor_y - position[1]), 0.2)

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

    def test_the_exit_boxes_clear_the_swept_volume_of_every_gate(self):
        for exit_point in self.exits["exits"]:
            x, y, z = exit_point["position"]
            for name, gate in GATES.items():
                x0, x1, z0, z1 = swept_rect(gate)
                dx, dy, dz = gate["drive"]
                y0, y1 = min(gate["y"][0], gate["y"][0] + dy), max(gate["y"][1], gate["y"][1] + dy)
                overlap = (x - 2.0 <= x1 and x + 2.0 >= x0 and z - 2.0 <= z1 and z + 2.0 >= z0 and y - 0.5 <= y1 and y + 2.5 >= y0)
                self.assertFalse(overlap, f"{exit_point['name']} touches the swept volume of {name}")

    def test_the_gate_table_equals_the_package(self):
        np = self.np
        for name, gate in GATES.items():
            idx = [i for i, o in enumerate(self.cset.owners) if o.path == name or o.path.startswith(name + "/")]
            self.assertTrue(idx, name)
            tris = self.cset.tris[np.isin(self.cset.tri_owner, idx)].reshape(-1, 3)
            lo, hi = tris.min(axis=0), tris.max(axis=0)
            x0, x1, z0, z1 = gate["closed"]
            for got, want in ((lo[0], x0), (hi[0], x1), (lo[2], z0), (hi[2], z1), (lo[1], gate["y"][0]), (hi[1], gate["y"][1])):
                self.assertAlmostEqual(got, want, delta=0.02, msg=name)

    def test_the_gate_roots_are_where_the_gate_table_says(self):
        # (until 2026-11-12 these roots were the reference of the exit distances 9.94 / 9.26 m, a rule R3 retired)
        scene = M.SceneGraph("DesignStuff", json.loads(
            (PACKAGE / "scenes" / "Arena_AutoService_TeamFight_DesignStuff.json").read_text(encoding="utf-8")))
        roots = {scene.path(o): scene.position(o) for o in scene.objects if scene.path(o).count("/") == 1
                 and scene.path(o).startswith("DOORS/") and "LIGHT" not in scene.path(o)}
        self.assertEqual(sorted(roots), sorted(GATES))
        for name, position in roots.items():
            self.assertTrue(all(abs(a - b) < 0.02 for a, b in zip(position, {
                "DOORS/big_gate_metal": (-344.09, -3.74, 479.86), "DOORS/equator_roller_shutters_01": (-344.6, -4.34, 506.83),
                "DOORS/roller_shutters_techlight_01": (-402.26, -3.78, 501.22),
                "DOORS/roller_shutters_techlight_01 (1)": (-402.26, -3.78, 477.51)}[name])), (name, position))


if __name__ == "__main__":
    unittest.main()
