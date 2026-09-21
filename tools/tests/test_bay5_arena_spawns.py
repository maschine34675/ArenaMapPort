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
