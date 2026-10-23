"""Bay5 data contract; native reachability/standing room is tested separately."""
import importlib.util
import json
import math
from pathlib import Path
import unittest

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("bay5_generator", REPO / "tools/server/make_base_json.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class Bay5ArenaSpawnTests(unittest.TestCase):
    def setUp(self):
        self.bots = json.loads((REPO / "tools/maps/bay5.bots.json").read_text(encoding="utf-8"))
        self.exits = json.loads((REPO / "tools/maps/bay5.exits.json").read_text(encoding="utf-8"))
        self.map = json.loads((REPO / "tools/maps/bay5.json").read_text(encoding="utf-8"))
        self.base = json.loads((REPO / "ArenaMapPort.Server/db/arena_bay5/base.json").read_text(encoding="utf-8"))

    def test_distant_burst_candidates_cover_the_source_combat_envelope(self):
        # Source's 2208 AI bearing points span x44.415..146.295,z-14.037..71.174.
        # All samples of this containing rectangle (including non-walkable cells)
        # must leave three distinct choices for ArenaMode's two-bot/25m burst.
        points = {tuple(p["position"]) for p in self.bots["bot_spawns"]}
        for x in range(44, 148):
            for z in range(-15, 73):
                self.assertGreaterEqual(sum(math.dist((x, -3.85, z), p) >= 25 for p in points), 3,
                                        f"Insufficient distant points at {x},{z}")

    def test_respawns_do_not_overlap_ai_arrivals_or_extract_on_entry(self):
        for player in self.bots["player_spawns"]:
            for bot in self.bots["bot_spawns"]:
                self.assertGreaterEqual(math.dist(player["position"], bot["position"]), 2)
            for exit_point in self.exits["exits"]:
                self.assertGreaterEqual(math.dist(player["position"], exit_point["position"]), 10)
        for side in ("Usec", "Bear"):
            points = [p for p in self.bots["player_spawns"] if "All" in p["sides"] or side in p["sides"]]
            self.assertTrue(any(p["position"][2] < 10 for p in points), side)
            self.assertTrue(any(p["position"][2] > 50 for p in points), side)

    def test_every_gate_arena_moves_is_an_opened_barrier_with_a_pose(self):
        # 2026-10-23 (docs/progress/equator-booth-spawns.md, user: "alle Tore aufmachen"): the four warehouse rollers and the depot
        # frame (two instances behind one path). The pose table names exactly these barriers.
        barriers = self.map["opened_barriers"]
        self.assertEqual(barriers, [
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)",
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)",
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)",
            "WAREHOUSE_DOORS (1)/One_man_Door",
            "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame",
        ])
        self.assertEqual(len(barriers), len(set(barriers)))
        doors = json.loads((REPO / "tools/maps/bay5.doors.json").read_text(encoding="utf-8"))
        self.assertEqual(sorted({pose["barrier"] for pose in doors["poses"]}), sorted(barriers))

    # World footprints (x0, x1, z0, z1) of every opened gate part at its OPEN pose, measured from the colliders of the
    # bay5-native-v5 package moved by bay5.doors.json (2026-10-23, the survey's depot numbers reproduce to 0.1 m): the depot leaves
    # after their swing, the roller slabs after their lift (xz does not change). Derived numbers only.
    PARKED_FOOTPRINTS = {
        "depot 821 L leaf": (52.9, 55.3, 7.5, 8.6),
        "depot 821 R leaf": (46.2, 48.3, 7.5, 9.3),
        "depot 822 L leaf": (69.1, 70.9, -10.5, -8.6),
        "depot 822 R leaf": (69.1, 71.6, -4.1, -3.5),
        "roller (2)": (140.8, 145.2, 39.9, 40.1),
        "roller (3)": (129.3, 129.5, 41.0, 45.4),
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


if __name__ == "__main__":
    unittest.main()
