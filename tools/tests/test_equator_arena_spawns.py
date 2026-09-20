"""Arena burst coverage on Equator; native walkability is a separate GPU check."""
import importlib.util
import json
import math
from pathlib import Path
import unittest

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("make_base_json", REPO / "tools/server/make_base_json.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class EquatorArenaSpawnTests(unittest.TestCase):
    def setUp(self):
        self.bots = json.loads((REPO / "tools/maps/equator.bots.json").read_text(encoding="utf-8"))
        self.descriptor = json.loads((REPO / "tools/maps/equator.json").read_text(encoding="utf-8"))
        self.base = json.loads((REPO / "ArenaMapPort.Server/db/base.json").read_text(encoding="utf-8"))

    def test_three_distant_bot_points_cover_playable_ground_envelope(self):
        # Preserve ArenaMode's 25 m minimum with three distinct candidate points;
        # its injector requests two per burst, leaving selection headroom.
        # Testing the entire 1 m envelope also covers some non-walkable positions;
        # it is conservative distance coverage, not proof of a native NavMesh path.
        positions = {tuple(p["position"]) for p in self.bots["bot_spawns"]}
        for x in range(-25, 26):
            for z in range(-46, 51):
                usable = sum(math.dist((x, 0.1, z), p) >= 25 for p in positions)
                self.assertGreaterEqual(usable, 3, f"Only {usable} distant points from ({x}, {z})")

    def test_bot_spawns_do_not_overlap_player_respawn_coordinates(self):
        # ArenaMode full-map respawn chooses Player markers directly; do not author
        # an AI arrival at the same physical position as a returning player.
        for bot in self.bots["bot_spawns"]:
            for player in self.bots["player_spawns"]:
                self.assertGreaterEqual(math.dist(bot["position"], player["position"]), 2,
                                        f"{bot["id"]} overlaps {player["id"]}")

    def test_ground_spawns_keep_native_zone_categories_and_generator_output(self):
        for point in self.bots["player_spawns"] + self.bots["bot_spawns"]:
            self.assertTrue(all(math.isfinite(v) for v in point["position"]))
            self.assertGreaterEqual(point["position"][1], 0)
            self.assertLessEqual(point["position"][1], 0.2)
        self.assertEqual(self.bots["zone"], "BotZone_Main")
        for point in self.bots["bot_spawns"]:
            self.assertEqual(set(point["categories"]), {"Bot", "BotPmc"})
        generated = {}
        generator.apply_bots(generated, self.descriptor, self.bots)
        for key, value in generated.items():
            self.assertEqual(self.base[key], value, f"Stale generated field: {key}")
        self.assertTrue(self.base["DisabledForScav"])

    def test_player_respawns_offer_both_ends_to_both_pmc_sides(self):
        for side in ("Usec", "Bear"):
            points = [p for p in self.bots["player_spawns"] if side in p["sides"] or "All" in p["sides"]]
            self.assertTrue(any(p["position"][2] > 30 for p in points), side)
            self.assertTrue(any(p["position"][2] < -25 for p in points), side)


if __name__ == "__main__":
    unittest.main()
