"""Iceberg data contract (docs/PLAN-ICEBERG.md item 7, 2026-10-30): tools/maps/iceberg.bots.json, iceberg.exits.json,
iceberg.modes.json and iceberg.ai.json against each other, against Arena's own NavMesh and against the port's colliders.

Why: nobody has walked Iceberg yet, so everything checkable without the host is pinned here, in three layers.

1. Data only (the repository): the sidecars have the shape the released maps' have and the generator turns them into the
   base.json fields; the player rows are four of the 20 pocket markers of iceberg.modes.json, the bot rows are Arena's AI
   core points minus the two on a pocket's gate approach (the rule is stated and checked both ways) plus, appended on
   2026-11-12, eight rows for ArenaMode's 25 m burst selector (seven CheckPoint stage markers and one fill point), nothing
   stands inside a team box, and since 2026-11-12 (rule R3) each exit box stands INSIDE its team pocket, 2.5 m or more from
   every player row, so that the green signal flare marks the team's spawn area (the 2026-10-30 rule put both exits
   outside the gates and 10 m from every player row; it is retired).
2. Arena's NavMesh (the installed Arena 0.6.1, level377, ``sharedassets377.assets`` path 2, identity placement): 64 of the 70
   markers lie on the 4,788 m2 main component, the six that do not are the known Bear stage markers, every row and exit
   stands on it with the documented clearance (one appended row, the moved stage marker Bear house (49), is the exception:
   the port's own bake has it, Arena's mesh does not; IcebergBuiltMeshTests checks the rows on the built bundle), and the
   4-hub PatrolWay and ten Savage markers of the AI scene lie off it.
3. The port's colliders (the ignored package work/extract/iceberg-native-v2 through tools/validate/spawn_placement_probe.py,
   the offline stand-in for SpawnPlacementRepairs.IsValid): every row stands on a floor, 69 of 70 markers pass, and
   ``Bear house (38)`` touches a LevelBorder cube.

SpawnPlacementRepairs never sees the 70 markers: it runs over the SpawnPointsCollection, which make_base_json.py fills with
exactly the player and bot rows of the bots sidecar (CreateFromScene -> FixSpawns drops every other scene marker). The six
off-mesh Bear markers are therefore inert in a plain raid and matter only to a mode plugin that places players on them.

The C# half (descriptor Exits mirror, server parse) is the lead's promotion test: ArenaMapPort.Server.Tests/IcebergMapTests.cs.
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
PACKAGE = WORK / "extract" / "iceberg-native-v2"

spec = importlib.util.spec_from_file_location("make_base_json", REPO / "tools/server/make_base_json.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)

import extract_mode_objects as M  # noqa: E402

NAVMESH = ("sharedassets377.assets", 2)
# the AI scene of the plan (plans/iceberg.json role `ai`): level377, Arena_Sanatorium_CheckPoint_AI
AI_SCENE = (377, "Arena_Sanatorium_CheckPoint_AI")

# Player row id suffix -> (sides, original pocket marker). East = Bear pocket (SPAWN_BOUND_FIGHTER), west = Usec pocket.
PLAYER_MARKERS = {
    "all": (["All"], "Bear house (14)"),
    "bear": (["Bear"], "Bear house (8)"),
    "usec": (["Usec"], "Usec house (8)"),
    "west-all": (["All"], "Usec house (14)"),
}
# Bot rows: id suffix -> AI core point id; the rows that do not sit on their core point, with the largest move allowed (m).
BOT_CORE_POINTS = {f"core-{n:02d}": n for n in (1, 3, 4, 5, 6, 7, 8, 9, 10)}
# 2026-11-12 (rules R1, R2, R5 of the spawn-count change): eight Bot rows APPENDED after the nine core rows, in this order. suffix ->
# (the pocket-less stage marker it stands on or next to, the largest horizontal move from that marker in metres, 1.9 m = the
# decided moves of 1.1 to 1.8 m plus a 0.1 m margin). The fill row stands on no marker: it replaces stage marker Bear house (26),
# which stands on the balcony ladder's concrete part, by a mesh point in the open east yard.
NEW_BOT_ROWS = {
    "stage-bear-46": ("Bear house (46)", 0.05), "stage-bear-49": ("Bear house (49)", 1.9), "fill-01": (None, None),
    "stage-bear-36": ("Bear house (36)", 1.9), "stage-bear-33": ("Bear house (33)", 1.9),
    "stage-usec-35": ("Usec house (35)", 1.9), "stage-usec-32": ("Usec house (32)", 1.9), "stage-usec-36": ("Usec house (36)", 1.9),
}
# The one appended row that lies outside Arena's own NavMesh (a moved Bear stage marker (49), 2.3 to 7.7 m outside it): the port's
# bake carries it on the main component with 2.23 m of clearance (IcebergBuiltMeshTests).
OFF_ARENA_MESH_ROWS = ("arena_iceberg-bot-stage-bear-49",)
# ArenaMode's burst selector wants five Bot rows 25 m or more from wherever the leader stands (Tactical); measured on the 1 m grid
# of Arena's footprint the appended rows give nine everywhere (before: four).
MIN_DISTANT_ROWS = 5
SKIPPED_CORE_POINTS = (2, 11)
MOVED_FROM_CORE_POINT = {"core-06": 1.5, "core-09": 3.6}
BOT_SKIP_RADIUS = 10.0
# The leaves of the pockets' sliding gates and the motor gate (world, level376 DOORS/GATES_A, GATES_B; survey S2, checked
# against the package below): the east pocket opens west through x 82.38, the west pocket east through x -36.04.
GATE_LEAVES = {
    "GATES_A/1": (82.38, 2.18, -93.69), "GATES_A/2": (82.38, 2.18, -86.13),
    "GATES_B/1": (-36.04, 3.09, -85.93), "GATES_B/2": (-36.04, 3.09, -81.78), "GATES_B/3": (-36.04, 3.09, -77.66),
    "GATES_B/motor": (-38.91, 6.44, -75.66),
}
# Bear stage markers that lie outside Arena's own NavMesh: (38) 0.29 m beyond its border, (47)-(51) 2.3 to 7.7 m from it.
OFF_MESH_MARKERS = ("Bear house (38)", "Bear house (47)", "Bear house (48)", "Bear house (49)", "Bear house (50)", "Bear house (51)")
MIN_PLAYER_CLEARANCE = 1.4         # border clearance in Arena's mesh (west-all is the tightest, 1.42 m)
MIN_BOT_CLEARANCE = 1.35           # core 10 keeps 1.38 m, core 7 1.46 m; core 6 (0.58 m) is moved to 1.98 m
EXIT_HALF_EDGE = 2.0               # the exit box is 4 x 3 x 4 m, rotation 0
MIN_EXIT_PLAYER_EDGE = 2.5         # rule R3: every Player row keeps 2.5 m to the box EDGE (the Equator booth precedent), 2.70 m is the tightest
MIN_EXIT_BOT_DISTANCE = 10.0       # no Bot row spawns in or beside an exit (17.9 m is the nearest, west exit to core-01)
MIN_EXIT_LEAF_GAP = 0.5            # the box clears a gate leaf's centre (not its swept footprint) by 0.5 m in plan; the west box is 0.75 m from the closed leaves' plane
# which team pocket (initial spawn bound of iceberg.modes.json) each exit belongs to: rule R3, one exit per team pocket
EXIT_POCKETS = {"Iceberg East Gate": "SPAWN_BOUND_FIGHTER", "Iceberg West Gate": "SPAWN_BOUND_DEFENCE"}


def load(stem):
    return json.loads((REPO / "tools" / "maps" / stem).read_text(encoding="utf-8"))


def xz(point):
    return (point[0], point[2])


class SidecarData:
    """The five tracked Iceberg sidecars, loaded once per test class."""

    @classmethod
    def load_data(cls):
        cls.bots, cls.exits, cls.map = load("iceberg.bots.json"), load("iceberg.exits.json"), load("iceberg.json")
        cls.modes, cls.ai = load("iceberg.modes.json"), load("iceberg.ai.json")
        cls.players, cls.bot_points = cls.bots["player_spawns"], cls.bots["bot_spawns"]
        cls.markers = {m["name"]: m for side in cls.modes["spawn_markers"].values() for m in side}
        cls.pocket = [m for m in cls.markers.values() if m["pocket"]]
        cls.bounds = {b["name"]: b for b in cls.modes["spawn_bounds"]}
        cls.core = {c["id"]: c["position"] for c in cls.ai["core_points"]}


class IcebergSidecarTests(SidecarData, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.load_data()

    # ------------------------------------------------------------------ shape
    def test_counts_and_ids(self):
        self.assertEqual((len(self.players), len(self.bot_points)), (4, 17))
        self.assertEqual((len(self.bots["waves"]), len(self.bots["boss_waves"]), len(self.exits["exits"])), (5, 4, 2))
        self.assertEqual((self.bots["zone"], self.bots["max_bots"]), ("BotZone_Main", 8))
        ids = [p["id"] for p in self.players + self.bot_points]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual({p["id"] for p in self.players}, {f"arena_iceberg-player-{s}" for s in PLAYER_MARKERS})
        self.assertEqual({p["id"] for p in self.bot_points},
                         {f"arena_iceberg-bot-{s}" for s in list(BOT_CORE_POINTS) + list(NEW_BOT_ROWS)})
        # rule R1: the nine core rows come first, in their original order, and the eight new rows are appended in the decided order
        # (ArenaMode pins arena_iceberg-bot-core-01 by id and exact floats); none of them is a Team Deathmatch row of ArenaMode
        self.assertEqual([p["id"] for p in self.bot_points],
                         [f"arena_iceberg-bot-{s}" for s in list(BOT_CORE_POINTS) + list(NEW_BOT_ROWS)])
        self.assertFalse(any("tdm-v1-" in i for i in ids))
        self.assertEqual(self.bot_points[0]["position"], [-10.88, 3.25, -80.66])

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
        self.assertIn("2026-10-30", self.bots["_comment"])
        self.assertIn("2026-10-30", self.exits["_comment"])
        self.assertIn("2026-11-12", self.bots["_comment"])
        self.assertIn("2026-11-12", self.exits["_comment"])

    def test_waves_and_pmc_squads_are_the_released_maps(self):
        airpit, bowl = load("airpit.bots.json"), load("bowl.bots.json")
        for key in ("waves", "boss_waves"):
            self.assertEqual(self.bots[key], airpit[key])
            self.assertEqual(self.bots[key], bowl[key])
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
                         (21, 5, 4, 2))
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
                         {"all": "SpawnPoint_Player_All", "bear": "SpawnPoint_Player_Bear", "usec": "SpawnPoint_Player_Usec",
                          "west-all": "SpawnPoint_Player_West_All"})
        for exit_point in self.exits["exits"]:
            self.assertEqual(exit_point["box_size"], [4.0, 3.0, 4.0])
            self.assertEqual((exit_point["type"], exit_point["chance"], exit_point["exfiltration_time"]), ("Individual", 100, 8))
        self.assertEqual([e["name"] for e in self.exits["exits"]], ["Iceberg East Gate", "Iceberg West Gate"])
        self.assertGreater(self.exits["exits"][0]["position"][0], 0)
        self.assertLess(self.exits["exits"][1]["position"][0], 0)

    # ------------------------------------------------------------------ player rows
    def test_player_rows_are_the_world_positions_of_four_pocket_markers(self):
        by_id = {p["id"]: p for p in self.players}
        for suffix, (sides, marker_name) in PLAYER_MARKERS.items():
            row, marker = by_id[f"arena_iceberg-player-{suffix}"], self.markers[marker_name]
            with self.subTest(row=suffix):
                self.assertTrue(marker["pocket"], marker_name)
                self.assertEqual(row["sides"], sides)
                self.assertEqual(row["position"], [round(v, 2) for v in marker["position"]])
                self.assertEqual(row["rotation"], round(marker["yaw"], 1))

    def test_bear_rows_stand_in_the_east_pocket_usec_rows_in_the_west_one_and_every_side_gets_both_ends(self):
        fighter, defence = self.bounds["SPAWN_BOUND_FIGHTER"], self.bounds["SPAWN_BOUND_DEFENCE"]
        for row in self.players:
            east = row["position"][0] > 0
            bound = fighter if east else defence
            self.assertGreaterEqual(M.box_margin(bound, row["position"]), -M.BOUND_EDGE_TOLERANCE, row["id"])
            if row["sides"] == ["Bear"]:
                self.assertTrue(east, row["id"])
            if row["sides"] == ["Usec"]:
                self.assertFalse(east, row["id"])
        for side in ("Bear", "Usec"):
            xs = [p["position"][0] for p in self.players if side in p["sides"] or "All" in p["sides"]]
            self.assertTrue(any(x > 85 for x in xs), side)
            self.assertTrue(any(x < -35 for x in xs), side)
        self.assertEqual({tuple(p["sides"]) for p in self.players}, {("All",), ("Usec",), ("Bear",)})

    # ------------------------------------------------------------------ bot rows
    def test_bot_rows_sit_on_their_core_points_except_the_two_documented_moves(self):
        by_id = {p["id"]: p for p in self.bot_points}
        for suffix, number in BOT_CORE_POINTS.items():
            row, core = by_id[f"arena_iceberg-bot-{suffix}"]["position"], self.core[number]
            moved = math.dist(xz(row), xz(core))
            with self.subTest(row=suffix):
                if suffix in MOVED_FROM_CORE_POINT:
                    self.assertGreater(moved, 1.0)
                    self.assertLessEqual(moved, MOVED_FROM_CORE_POINT[suffix])
                else:
                    self.assertAlmostEqual(moved, 0.0, delta=0.006)
                    self.assertAlmostEqual(row[1], core[1], delta=0.02)    # y is Arena's NavMesh surface, the AICore's own within 2 cm

    def test_the_appended_rows_stand_on_or_beside_their_stage_marker(self):
        by_id = {p["id"]: p for p in self.bot_points}
        for suffix, (marker_name, max_move) in NEW_BOT_ROWS.items():
            row = by_id[f"arena_iceberg-bot-{suffix}"]
            with self.subTest(row=suffix):
                self.assertEqual((row["radius"], row["categories"]), (25, ["Bot", "BotPmc"]))
                if marker_name is None:
                    self.assertTrue(suffix.startswith("fill"))
                    continue
                marker = self.markers[marker_name]
                self.assertFalse(marker["pocket"], marker_name)          # a stage marker of the CheckPoint scene, not a pocket marker
                self.assertLessEqual(math.dist(xz(row["position"]), xz(marker["position"])), max_move, marker_name)

    def test_the_skip_rule_no_bot_spawn_within_ten_metres_of_a_gate_leaf_or_a_pocket_marker(self):
        # applies to ALL 17 rows, the appended ones included (the nearest is the fill row, 22.4 m from a Bear gate leaf)
        def nearest(position):
            leaf = min(math.dist(xz(position), xz(g)) for g in GATE_LEAVES.values())
            marker = min(math.dist(xz(position), xz(m["position"])) for m in self.pocket)
            return min(leaf, marker)

        for number in SKIPPED_CORE_POINTS:
            self.assertLess(nearest(self.core[number]), BOT_SKIP_RADIUS, f"core {number} should have been skipped")
        for number in set(self.core) - set(SKIPPED_CORE_POINTS):
            self.assertGreaterEqual(nearest(self.core[number]), BOT_SKIP_RADIUS, f"core {number} is on a gate approach")
        for row in self.bot_points:
            self.assertGreaterEqual(nearest(row["position"]), BOT_SKIP_RADIUS, row["id"])
        self.assertEqual(set(self.core) - set(SKIPPED_CORE_POINTS), set(BOT_CORE_POINTS.values()))

    def test_no_core_point_lies_inside_a_team_box_and_no_bot_row_does_either(self):
        pockets = [self.bounds["SPAWN_BOUND_FIGHTER"], self.bounds["SPAWN_BOUND_DEFENCE"]]
        for number, position in self.core.items():
            for bound in pockets:
                self.assertLess(M.box_margin(bound, position), 0.0, f"core {number} inside {bound['name']}")
        for row in self.bot_points:
            for bound in pockets:
                self.assertLess(M.box_margin(bound, row["position"]), 0.0, row["id"])
        # the 2026-10-30 clause "the 4 x 4 m exit box clears both bounds by 2 m" is retired (rule R3): the exits stand INSIDE
        # their pocket now, see test_each_exit_box_stands_inside_its_own_team_pocket

    def test_spacing_between_rows(self):
        for bot in self.bot_points:
            for player in self.players:
                self.assertGreaterEqual(math.dist(bot["position"], player["position"]), 10.0, f"{bot['id']} / {player['id']}")
            for other in self.bot_points:
                if other is not bot:
                    self.assertGreaterEqual(math.dist(bot["position"], other["position"]), 12.0, f"{bot['id']} / {other['id']}")
        for a in self.players:
            for b in self.players:
                if a is not b:
                    self.assertGreaterEqual(math.dist(a["position"], b["position"]), 4.0, f"{a['id']} / {b['id']}")

    def test_five_distant_bot_points_cover_the_playable_footprint(self):
        # ArenaMode's burst selector uses only Bot rows 25 m or more from the player (rule R2): Tactical needs five of them from
        # wherever the leader stands, the other modes one, pickups six usable rows. Every cell of Arena's walkable footprint (main
        # component x -58.7..108.7, z -126.5..-34.8, 1 m grid) keeps MIN_DISTANT_ROWS distinct candidates. Until 2026-11-12 the rule
        # was three (nine rows, four distant at the worst cell); the 17 rows give nine at the worst cell, (10, -68).
        points = [row["position"] for row in self.bot_points]
        worst = None
        for x in range(-59, 110):
            for z in range(-127, -34):
                usable = sum(math.hypot(x - p[0], z - p[2]) >= 25 for p in points)
                worst = usable if worst is None else min(worst, usable)
                self.assertGreaterEqual(usable, MIN_DISTANT_ROWS, f"Only {usable} distant points from ({x}, {z})")
        self.assertEqual(worst, 9)

    # ------------------------------------------------------------------ exits
    def test_each_exit_box_stands_inside_its_own_team_pocket(self):
        # Rule R3 (2026-11-12, replaces "exits stand outside the pockets, 10 m from every Player row, beyond the gates"): one exit per
        # team pocket, the box centre inside the pocket's INITIAL spawn-bound footprint of iceberg.modes.json, so that the green
        # signal flare (ExfilSignals) marks the team's spawn area in ArenaMode's team modes, where extraction is disabled. The
        # pocket of an exit is told by its name: East Gate = the Bear pocket behind GATES_A, West Gate = the Usec pocket behind GATES_B.
        self.assertEqual({e["name"] for e in self.exits["exits"]}, set(EXIT_POCKETS))
        for exit_point in self.exits["exits"]:
            own = self.bounds[EXIT_POCKETS[exit_point["name"]]]
            self.assertTrue(own["initial"], own["name"])
            with self.subTest(exit=exit_point["name"]):
                self.assertEqual(exit_point["rotation"], 0.0)
                self.assertGreaterEqual(M.box_margin(own, exit_point["position"]), 0.0)
                for other in (b for n, b in self.bounds.items() if b["initial"] and n != own["name"]):
                    self.assertLess(M.box_margin(other, exit_point["position"]), 0.0)
        # behind its gates as seen from the map: east of the Bear gate plane (x 82.38), west of the Usec one (x -36.04)
        east, west = self.exits["exits"]
        self.assertGreater(east["position"][0] - EXIT_HALF_EDGE, 82.38)
        self.assertLess(west["position"][0] + EXIT_HALF_EDGE, -36.04)

    def test_no_player_row_starts_inside_an_exit_trigger_and_no_bot_row_beside_one(self):
        # rule R3: every Player row keeps 2.5 m to the box EDGE (not the centre), the Equator booth precedent
        # (docs/progress/equator-booth-spawns.md): a plain-raid spawn must not start inside a trigger. Boxes are axis aligned.
        for exit_point in self.exits["exits"]:
            ex, ez = exit_point["position"][0], exit_point["position"][2]
            for player in self.players:
                px, pz = player["position"][0], player["position"][2]
                edge = math.hypot(max(abs(px - ex) - EXIT_HALF_EDGE, 0.0), max(abs(pz - ez) - EXIT_HALF_EDGE, 0.0))
                self.assertGreaterEqual(edge, MIN_EXIT_PLAYER_EDGE, f"{player['id']} is {edge:.2f} m from the box of {exit_point['name']}")
            for bot in self.bot_points:
                self.assertGreaterEqual(math.dist(xz(exit_point["position"]), xz(bot["position"])), MIN_EXIT_BOT_DISTANCE, bot["id"])
            # successor of "the box is within 3 m of no gate leaf" (the box now lies behind the gates, the west one 0.75 m from the leaves'
            # plane): the box keeps a gap to every leaf CENTRE in plan (not the swept footprint, 0.61 m for the west box); the gates slide along z,
            # so what matters is the x gap to the leaf plane (x -36.04 west, 82.38 east), which stays outside the trigger
            for leaf in GATE_LEAVES.values():
                gap = math.hypot(max(abs(leaf[0] - ex) - EXIT_HALF_EDGE, 0.0), max(abs(leaf[2] - ez) - EXIT_HALF_EDGE, 0.0))
                self.assertGreaterEqual(gap, MIN_EXIT_LEAF_GAP, f"the box of {exit_point['name']} touches a gate leaf")

    def test_the_gate_leaf_table_equals_the_package(self):
        if not (PACKAGE / "scenes" / "Arena_Sanatorium_CheckPoint_DesignStuff.json").is_file():
            self.skipTest("iceberg-native-v2 package is local only")
        scene = M.SceneGraph("Arena_Sanatorium_CheckPoint_DesignStuff", json.loads(
            (PACKAGE / "scenes" / "Arena_Sanatorium_CheckPoint_DesignStuff.json").read_text(encoding="utf-8")))
        found = {}
        for obj in scene.objects:
            path = scene.path(obj)
            if path.startswith("DOORS/GATES_A/sanatorium_resp_gate") and obj["name"] == "sanatorium_resp_gate":
                found.setdefault("GATES_A", []).append(scene.position(obj))
            elif path.startswith("DOORS/GATES_B/sanatorium_resp_gate") and obj["name"] == "sanatorium_resp_gate":
                found.setdefault("GATES_B", []).append(scene.position(obj))
            elif path == "DOORS/GATES_B/Sanatorium_CheckPoint_Gate":
                found["motor"] = [scene.position(obj)]
        table = {"GATES_A": [v for k, v in GATE_LEAVES.items() if k.startswith("GATES_A")],
                 "GATES_B": [v for k, v in GATE_LEAVES.items() if k.startswith("GATES_B/") and k != "GATES_B/motor"],
                 "motor": [GATE_LEAVES["GATES_B/motor"]]}
        for key, expected in table.items():
            got = sorted(found[key], key=lambda p: p[2])
            self.assertEqual(len(got), len(expected), key)
            for g, e in zip(got, sorted(expected, key=lambda p: p[2])):
                self.assertTrue(all(abs(a - b) < 0.02 for a, b in zip(g, e)), f"{key}: {g} != {e}")


def mesh_available():
    return (ARENA_DATA / NAVMESH[0]).is_file()


@unittest.skipUnless(mesh_available(), "Arena 0.6.1 is not installed")
class IcebergArenaMeshTests(SidecarData, unittest.TestCase):
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
        self.assertEqual((self.meta["tiles_total"], self.mesh.poly_count), (64, 2473))
        components = self.no.component_summary(self.mesh)
        self.assertEqual((len(components), components[0]["polygons"]), (93, 1681))
        main = components[0]
        self.assertAlmostEqual(main["area_xz"], 4787.6, delta=0.5)
        for got, want in zip(main["min"] + main["max"], [-58.7, -1.8, -126.5, 108.7, 6.15, -34.8]):
            self.assertAlmostEqual(got, want, delta=0.06)

    def test_64_of_the_70_markers_stand_on_the_main_component_and_the_six_known_ones_do_not(self):
        markers = self.markers
        names = sorted(markers)
        results = dict(zip(names, self.locate([markers[n]["position"] for n in names])))
        self.assertEqual(len(results), 70)
        off = sorted(n for n, (label, _sy, _c) in results.items() if label != 0)
        self.assertEqual(off, sorted(OFF_MESH_MARKERS))
        self.assertTrue(all(label == -1 for n, (label, _s, _c) in results.items() if n in OFF_MESH_MARKERS))
        for name, (label, sy, clearance) in results.items():
            if label == 0:
                self.assertLess(abs(sy - markers[name]["position"][1]), 0.7, name)      # Bear pocket: the mesh is up to 0.57 m above
                self.assertGreaterEqual(clearance, 0.15, name)       # Bear (40) is the tightest with 0.20 m

    def test_the_pocket_markers_have_room(self):
        pocket = self.pocket
        for marker, (label, sy, clearance) in zip(pocket, self.locate([m["position"] for m in pocket])):
            self.assertEqual(label, 0, marker["name"])
            self.assertGreaterEqual(clearance, 0.6, marker["name"])          # Usec (7) is the tightest with 0.67 m
        self.assertEqual(len(pocket), 20)

    def test_all_eleven_core_points_are_on_the_main_component(self):
        core = self.core
        self.assertEqual(len(core), 11)
        for number, (label, sy, clearance) in zip(core, self.locate([core[n] for n in core])):
            self.assertEqual(label, 0, f"core {number}")
            self.assertGreaterEqual(clearance, 0.5, f"core {number}")
            if number != 9:                                                  # core 9's AICore stands in the pool basin
                self.assertLess(abs(sy - core[number][1]), 0.05, f"core {number}")
        self.assertAlmostEqual(self.locate([core[9]])[0][1] - core[9][1], 0.47, delta=0.05)

    def test_rows_and_exits_stand_on_the_main_component_with_room(self):
        rows = [(p["id"], p["position"], MIN_PLAYER_CLEARANCE, 0.3) for p in self.players]
        rows += [(p["id"], p["position"], MIN_BOT_CLEARANCE, 0.05) for p in self.bot_points if p["id"] not in OFF_ARENA_MESH_ROWS]
        # exits: the 2026-10-30 draft kept the whole box (half diagonal 2.83 m + 0.5) on the mesh; the in-pocket boxes of 2026-11-12 stand
        # 2.16 m and 2.11 m from the border, which puts the four edge midpoints (half edge 2.0 m) on the mesh and lets a corner overhang
        # by up to 0.7 m (the corners are on the port's own mesh within 0.5 m, see IcebergBuiltMeshTests)
        rows += [(e["name"], e["position"], EXIT_HALF_EDGE, 0.05) for e in self.exits["exits"]]
        for (name, position, need, dy), (label, sy, clearance) in zip(rows, self.locate([r[1] for r in rows])):
            self.assertEqual(label, 0, f"{name} is not on Arena's main component")
            self.assertLessEqual(abs(sy - position[1]), dy, f"{name} is {sy - position[1]:+.2f} m off Arena's surface")
            self.assertGreaterEqual(clearance, need, f"{name} is only {clearance:.2f} m from the NavMesh border")

    def test_the_one_appended_row_off_arenas_mesh_is_the_moved_bear_49(self):
        by_id = {p["id"]: p for p in self.bot_points}
        for row_id in OFF_ARENA_MESH_ROWS:
            self.assertEqual(self.locate([by_id[row_id]["position"]])[0][0], -1, row_id)
        self.assertIn("Bear house (49)", OFF_MESH_MARKERS)

    def test_the_stale_patrol_hubs_and_savage_markers_are_off_arenas_mesh_and_off_the_map(self):
        try:
            import extract_ai_anchors as A
        except ImportError:
            self.skipTest("UnityPy is not installed")
        from unittest import mock
        with mock.patch.dict(A.PLAY_AREA, clear=True):
            raw = A.extract_map("iceberg", AI_SCENE[0], AI_SCENE[1], A.DEFAULT_SOURCE)
        hubs = raw["patrol_ways"][0]["hubs"]
        stale = [h["position"] for h in hubs] + [s for h in hubs for s in h["sub_points"]]
        self.assertEqual(len(stale), 44)
        paths = A.scene_files({AI_SCENE[0]: "ai"}, base=A.DEFAULT_SOURCE)
        level = A.UnityPy.load(*paths, f"{A.DEFAULT_SOURCE}/globalgamemanagers.assets").files[f"{A.DEFAULT_SOURCE}/level{AI_SCENE[0]}"]
        markers = []
        for obj in list(level.objects.values()):
            if obj.type.name == "MonoBehaviour":
                cls, status = A.arena_census.resolve_script_name(obj)
                if status == "ok" and cls and cls.rsplit(".", 1)[-1] == "SpawnPointMarker":
                    markers.append(A._world_position(A._transform_of(obj, level)))
        self.assertEqual(len(markers), 10)
        for label, group in (("hub/sub point", stale), ("Savage marker", markers)):
            for position, (mesh_label, _sy, _c) in zip(group, self.locate(group, max_dy=3.0)):
                self.assertEqual(mesh_label, -1, f"{label} {position} is on Arena's mesh")
                self.assertGreater(position[2], -33.0, f"{label} {position} is inside the play area")


BUILT_NAVMESH = WORK / "build" / "iceberg" / "arena_iceberg_navmesh.bundle"


@unittest.skipUnless(BUILT_NAVMESH.is_file(), "no built Iceberg NavMesh bundle (set ARENAMAPPORT_WORK_DIR)")
class IcebergBuiltMeshTests(SidecarData, unittest.TestCase):
    """The NavMesh the client really installs (work/build/iceberg, baked from the port's colliders), for the rows appended on
    2026-11-12 and the exits. The original nine rows are pinned against Arena's own mesh above (the built mesh differs from it at
    core 4, 0.24 m up and 0.80 m of clearance, a fact of the original rows that this test does not restate)."""

    @classmethod
    def setUpClass(cls):
        try:
            import numpy as np
            import navmesh_diff as nd
            import spawn_coverage as sc
        except ImportError:
            raise unittest.SkipTest("numpy or UnityPy is not installed")
        cls.np = np
        cls.mesh = nd.load_bundle_mesh(BUILT_NAVMESH)
        cls.load_data()
        cls.play = sc.PlayMesh(cls.mesh, [p["position"] for p in cls.bot_points[:9]])

    def test_the_play_component_is_the_one_the_original_rows_stand_on(self):
        self.assertEqual(self.play.rows_on_component, 9)

    def test_every_appended_row_stands_on_the_play_component_with_room(self):
        # decided data: clearance 1.64 m (Bear 33, a dressing-room hallway) to 7.23 m (the fill row), the mesh surface within 0.01 m
        rows = self.bot_points[9:]
        self.assertEqual(len(rows), len(NEW_BOT_ROWS))
        positions = self.np.array([r["position"] for r in rows])
        poly, surface = self.play.locate(positions, max_dy=1.0)
        clearance = self.play.clearance(positions)
        for row, pid, sy, clear in zip(rows, poly, surface, clearance):
            with self.subTest(row=row["id"]):
                self.assertGreaterEqual(pid, 0, "not on the play component")
                self.assertLessEqual(abs(sy - row["position"][1]), 0.05)
                self.assertGreaterEqual(clear, MIN_BOT_CLEARANCE)

    def test_each_exit_box_has_floor_under_its_centre_and_its_corners_on_the_mesh(self):
        for exit_point in self.exits["exits"]:
            x, y, z = exit_point["position"]
            centre = self.np.array([[x, y, z]])
            corners = self.np.array([[x + dx, y, z + dz] for dx in (-EXIT_HALF_EDGE, EXIT_HALF_EDGE) for dz in (-EXIT_HALF_EDGE, EXIT_HALF_EDGE)])
            with self.subTest(exit=exit_point["name"]):
                self.assertGreaterEqual(self.play.locate(centre, max_dy=1.2)[0][0], 0)
                self.assertTrue((self.play.locate(corners, max_dy=1.2)[0] >= 0).all())
                self.assertGreaterEqual(self.play.clearance(centre)[0], EXIT_HALF_EDGE)


@unittest.skipUnless((PACKAGE / "scenes").is_dir() and (PACKAGE / "meshes").is_dir(), "no iceberg-native-v2 package")
class IcebergPlacementTests(SidecarData, unittest.TestCase):
    """The port's own colliders through the offline SpawnPlacementRepairs.IsValid (tools/validate/spawn_placement_probe.py)."""

    @classmethod
    def setUpClass(cls):
        try:
            import numpy  # noqa: F401
            import spawn_placement_probe as probe
        except ImportError:
            raise unittest.SkipTest("numpy is not installed")
        cls.probe = probe.PlacementProbe.from_package(PACKAGE)
        cls.load_data()

    def test_every_row_stands_on_a_floor_and_passes_the_placement_probe(self):
        rows = [(p["id"], p["position"]) for p in self.players + self.bot_points]
        for name, position in rows:
            verdict = self.probe.check(position)
            with self.subTest(row=name):
                self.assertTrue(verdict.valid, f"{name}: ground {verdict.ground_ok}, ceiling {verdict.ceiling}, blocked {verdict.blocked[:2]}")
                self.assertLess(abs(verdict.floor_y - position[1]), 0.2)

    def test_the_exit_centres_have_a_floor_within_a_quarter_metre(self):
        for exit_point in self.exits["exits"]:
            verdict = self.probe.check(exit_point["position"])
            self.assertIsNotNone(verdict.floor_y, exit_point["name"])
            self.assertLess(abs(verdict.floor_y - exit_point["position"][1]), 0.25, exit_point["name"])

    def test_no_solid_stands_in_an_exit_box(self):
        # rule R3: the box holds floor and no solid from 0.3 m above the floor point to its top at +2.5 m (ExfilRepairs spans -0.5 .. +2.5);
        # runtime-solid triangles only (spawn markers and the moved parts of opened barriers excluded, as the placement probe sees them).
        # The floor point is the NavMesh surface at the centre. The east box stands on the ramp: its collider floor is 1.89 m at the
        # centre against a mesh surface of 1.67 m, which is why the check starts 0.3 m above the SURFACE and still finds the floor clear.
        import numpy as np
        import port_colliders as pcol
        import spawn_coverage as sc
        import spawn_placement_probe as spp
        cset = pcol.load_package_colliders(PACKAGE)
        pcol.mark_barriers(cset, list(self.map.get("opened_barriers") or []), pcol.load_door_rows(REPO / "tools" / "maps" / "iceberg.doors.json"))
        keep = np.array([not (spp.runtime_nonsolid(o.names) or o.barrier_blocked) for o in cset.owners], bool)
        mask = keep[cset.tri_owner]
        solids = sc.SolidIndex(cset.tris[mask], cset.tri_owner[mask], [o.path for o in cset.owners])
        for exit_point in self.exits["exits"]:
            x, y, z = exit_point["position"]
            height = 2.5 - 0.3
            self.assertEqual(solids.solids_in_box([x, y + 0.3 + height / 2, z], [2 * EXIT_HALF_EDGE, height, 2 * EXIT_HALF_EDGE]), [], exit_point["name"])

    def test_the_bot_spawn_of_core_nine_stands_on_the_pool_deck_not_in_the_basin(self):
        # AICore 9 (16.55, 4.5, -91.59) is inside the swimming pool: collider floor 4.40 against a deck and an Arena mesh at
        # about 5.0 (a 0.6 m step). The bot row stands on the deck, where the collider floor and Arena's mesh agree.
        row = next(p for p in self.bot_points if p["id"].endswith("core-09"))
        deck = self.probe.check(row["position"])
        self.assertTrue(deck.valid)
        self.assertGreater(deck.floor_y, 4.9)
        basin = self.probe.check(self.core[9])
        self.assertLess(basin.floor_y, 4.45)

    def test_69_of_the_70_markers_pass_and_bear_38_touches_a_border_cube(self):
        failing = {}
        for name, marker in sorted(self.markers.items()):
            verdict = self.probe.check(marker["position"])
            self.assertIsNotNone(verdict.floor_y, name)
            if not verdict.valid:
                failing[name] = {leaf for _a, _h, leaf, _d in verdict.blocked}
        # SpawnPlacementRepairs would push marker (38) 1.5 m or more away if it ever saw it (it does not: see the module docstring)
        self.assertEqual(failing, {"Bear house (38)": {"LevelBorder (238)"}})

    def test_the_six_off_mesh_markers_stand_on_real_floor(self):
        for name in OFF_MESH_MARKERS:
            verdict = self.probe.check(self.markers[name]["position"])
            self.assertIsNotNone(verdict.floor_y, name)
            self.assertLess(abs(verdict.floor_y - self.markers[name]["position"][1]), 0.05, name)


if __name__ == "__main__":
    unittest.main()
