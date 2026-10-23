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
        self.base = json.loads((REPO / "ArenaMapPort.Server/db/arena_equator/base.json").read_text(encoding="utf-8"))

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


class EquatorBoothSpawnTests(unittest.TestCase):
    """2026-10-23 (docs/progress/equator-booth-spawns.md): the player spawns are Arena's own TeamFight markers behind the booth
    doors, the FCK roller shutter is a wall again and nothing stands in the FCK hall behind it."""

    # Where the FCK hall lies (built Equator NavMesh with the shutter closed: x -35..-10, z 34.6..49.6): west of the shutter slab
    # (x -9.69..-9.61) and north of Arena's LevelBorder row at z 33.8..34.5 (borders 79..82). The doorway is x -9.8..-9.5.
    FCK_HALL_EAST_X = -9.6
    FCK_HALL_SOUTH_Z = 33.9
    # Player spawn id -> (the booth it belongs to, the sides it offers). The doors of a booth stand at z 40.02 (north) / -35.05 (south).
    EXPECTED = {
        "arena_equator-player-all": ("north", ["All"]),
        "arena_equator-player-usec": ("north", ["Usec"]),
        "arena_equator-player-bear": ("south", ["Bear"]),
        "arena_equator-player-south-all": ("south", ["All"]),
    }

    def setUp(self):
        self.bots = json.loads((REPO / "tools/maps/equator.bots.json").read_text(encoding="utf-8"))
        self.exits = json.loads((REPO / "tools/maps/equator.exits.json").read_text(encoding="utf-8"))
        self.descriptor = json.loads((REPO / "tools/maps/equator.json").read_text(encoding="utf-8"))
        self.modes = json.loads((REPO / "tools/maps/equator.modes.json").read_text(encoding="utf-8"))

    def _marker_at(self, side, position):
        for marker in self.modes["spawn_markers"][side]:
            if abs(marker["position"][0] - position[0]) < 0.01 and abs(marker["position"][2] - position[2]) < 0.01:
                return marker
        return None

    def test_player_spawns_are_arena_markers_inside_the_booths(self):
        bounds = {b["side"]: b for b in self.modes["spawn_bounds"]}
        spawns = {p["id"]: p for p in self.bots["player_spawns"]}
        self.assertEqual(set(spawns), set(self.EXPECTED))
        for spawn_id, (side, sides) in self.EXPECTED.items():
            spawn = spawns[spawn_id]
            self.assertEqual(spawn["sides"], sides, spawn_id)
            marker = self._marker_at(side, spawn["position"])
            self.assertIsNotNone(marker, f"{spawn_id} is not on one of Arena's {side} spawn markers")
            self.assertAlmostEqual(spawn["position"][1], marker["position"][1], places=2, msg=spawn_id)
            self.assertAlmostEqual(spawn["rotation"], marker["yaw"], places=1, msg=spawn_id)
            box = bounds[side]
            for axis in (0, 2):                       # inside the SPAWN_BOUND_FIGHTER box of its booth, 1 m from its walls
                self.assertLess(abs(spawn["position"][axis] - box["center"][axis]), box["size"][axis] / 2 - 1.0, f"{spawn_id} axis {axis}")

    def test_players_face_the_doors_of_their_booth(self):
        for spawn in self.bots["player_spawns"]:
            side, _sides = self.EXPECTED[spawn["id"]]
            self.assertEqual(spawn["rotation"], 180.0 if side == "north" else 0.0, spawn["id"])
            door_z = 40.02 if side == "north" else -35.05
            self.assertGreater(abs(spawn["position"][2] - door_z), 3.0, f"{spawn['id']} stands at the door line")
            self.assertEqual(spawn["position"][2] > door_z, side == "north", f"{spawn['id']} is on the wrong side of its door line")

    def test_the_two_spawns_of_a_booth_stand_behind_different_door_openings(self):
        # The door assemblies of a booth span x -7.3..-1.5 and 1.6..7.4 (collider extents of SPAWN_DOORS/Door_Sliding_Respawn*,
        # the north and the south pair): one spawn stands behind each of the two openings.
        for side in ("north", "south"):
            xs = sorted(p["position"][0] for p in self.bots["player_spawns"] if self.EXPECTED[p["id"]][0] == side)
            self.assertEqual(len(xs), 2)
            self.assertLess(xs[0], -1.6)
            self.assertGreater(xs[1], 1.6)

    def test_players_do_not_start_inside_or_beside_an_exit_box(self):
        for exit_point in self.exits["exits"]:
            half_x, half_z = exit_point["box_size"][0] / 2, exit_point["box_size"][2] / 2
            for spawn in self.bots["player_spawns"]:
                dx = max(abs(spawn["position"][0] - exit_point["position"][0]) - half_x, 0.0)
                dz = max(abs(spawn["position"][2] - exit_point["position"][2]) - half_z, 0.0)
                self.assertGreaterEqual(math.hypot(dx, dz), 2.5, f"{spawn['id']} is too close to '{exit_point['name']}'")

    def test_nothing_stands_in_the_fck_hall(self):
        points = [(p["id"], p["position"]) for p in self.bots["player_spawns"] + self.bots["bot_spawns"]]
        points += [(e["name"], e["position"]) for e in self.exits["exits"]]
        for name, position in points:
            in_hall = position[0] < self.FCK_HALL_EAST_X and position[2] > self.FCK_HALL_SOUTH_Z
            self.assertFalse(in_hall, f"{name} {position} stands in the FCK hall, which the closed shutter cuts off the NavMesh")

    def test_bot_north_hall_moved_onto_the_field(self):
        bots = {b["id"]: b for b in self.bots["bot_spawns"]}
        self.assertEqual(len(bots), 10)
        self.assertEqual(bots["arena_equator-bot-north-hall"]["position"], [-5.5, 0.1, 33.0])
        self.assertEqual(bots["arena_equator-bot-north-booth-east"]["position"], [4.5, 0.1, 38.5])

    def test_the_fck_shutter_is_not_opened(self):
        # Arena walls the shutter off (LevelBorder (83)); the bake keeps it as a wall and the client leaves it standing.
        self.assertEqual(self.descriptor["opened_barriers"], [])


if __name__ == "__main__":
    unittest.main()
