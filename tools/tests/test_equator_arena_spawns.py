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

    # The ten rows of the first release line (2026-09-20 to 2026-10-25), in file order. They stay byte-identical (rule R1 of the
    # 2026-11-12 spawn-count change): ArenaMode pins the donor row bot-atrium by id and exact floats.
    ORIGINAL_BOT_IDS = [
        "arena_equator-bot-atrium", "arena_equator-bot-west", "arena_equator-bot-east", "arena_equator-bot-northeast",
        "arena_equator-bot-northwest", "arena_equator-bot-southwest", "arena_equator-bot-north-booth-east",
        "arena_equator-bot-north-hall", "arena_equator-bot-south-west", "arena_equator-bot-south-east",
    ]
    # The six rows appended on 2026-11-12, in file order (Arena's AICore points ID:12, 11, 10, 2 and the AI born points 21 and 26
    # of the TeamFight_AI scene).
    APPENDED_BOT_IDS = [
        "arena_equator-bot-core-12", "arena_equator-bot-born-21", "arena_equator-bot-born-26",
        "arena_equator-bot-core-11", "arena_equator-bot-core-10", "arena_equator-bot-core-02",
    ]

    def test_the_original_rows_come_first_and_the_new_rows_are_appended(self):
        ids = [b["id"] for b in self.bots["bot_spawns"]]
        self.assertEqual(ids, self.ORIGINAL_BOT_IDS + self.APPENDED_BOT_IDS)
        self.assertEqual(len(set(ids)), 16)
        for bot_id in self.APPENDED_BOT_IDS:
            self.assertNotIn("-tdm-v1-", bot_id)   # ArenaMode's own id family
        for row in self.bots["bot_spawns"][10:]:
            self.assertEqual(row["radius"], 25, row["id"])
            self.assertNotIn("rotation", row, row["id"])

    def test_the_original_ten_rows_keep_their_values(self):
        # R1: byte-identical values (ArenaMode pins bot-atrium exactly; the rest keep their hand-verified positions).
        expected = {
            "arena_equator-bot-atrium": ([0.0, 0.2, 2.0], 30),
            "arena_equator-bot-west": ([-12.0, 0.1, -8.0], 25),
            "arena_equator-bot-east": ([11.4, 0.1, 1.5], 25),
            "arena_equator-bot-northeast": ([9.3, 0.1, 19.6], 25),
            "arena_equator-bot-northwest": ([-17.4, 0.1, 13.0], 25),
            "arena_equator-bot-southwest": ([-9.9, 0.1, -11.7], 25),
            "arena_equator-bot-north-booth-east": ([4.5, 0.1, 38.5], 25),
            "arena_equator-bot-north-hall": ([-5.5, 0.1, 33.0], 25),
            "arena_equator-bot-south-west": ([-7.5, 0.1, -28.0], 25),
            "arena_equator-bot-south-east": ([7.5, 0.1, -29.0], 25),
        }
        rows = {b["id"]: b for b in self.bots["bot_spawns"]}
        for bot_id, (position, radius) in expected.items():
            self.assertEqual(rows[bot_id]["position"], position, bot_id)
            self.assertEqual(rows[bot_id]["radius"], radius, bot_id)

    def test_the_original_ten_rows_still_give_three_distant_points_everywhere(self):
        # The pre-2026-11-12 rule, kept for the original rows: ArenaMode's 25 m minimum with three distinct candidate points
        # (its injector requests two per burst). Testing the entire 1 m envelope also covers some non-walkable positions;
        # it is conservative distance coverage, not proof of a native NavMesh path.
        positions = {tuple(p["position"]) for p in self.bots["bot_spawns"][:10]}
        for x in range(-25, 26):
            for z in range(-46, 51):
                usable = sum(math.dist((x, 0.1, z), p) >= 25 for p in positions)
                self.assertGreaterEqual(usable, 3, f"Only {usable} distant points from ({x}, {z})")

    def test_sixteen_rows_give_five_distant_points_everywhere(self):
        # R2, successor of the three-point rule for the whole table: ArenaMode's Tactical mode needs five Bot rows at least 25 m
        # (3D) from wherever the leader stands, pickups need at least six usable rows. Offline measurement 2026-11-12: every
        # 1 m point of the envelope has at least six (median 11). Same conservative envelope as above.
        positions = {tuple(p["position"]) for p in self.bots["bot_spawns"]}
        self.assertEqual(len(positions), 16)
        for x in range(-25, 26):
            for z in range(-46, 51):
                usable = sum(math.dist((x, 0.1, z), p) >= 25 for p in positions)
                self.assertGreaterEqual(usable, 6, f"Only {usable} distant points from ({x}, {z})")

    def test_new_rows_stand_on_the_built_play_component_with_room(self):
        # The appended rows measure 1.45 m or more of mesh clearance on 2026-11-12 (the generic 0.5 m floor of
        # test_spawn_navmesh_clearance covers every row); a drift of a row toward the border fails here. Skips without the ignored
        # built bundle (the worktree reads it through ARENAMAPPORT_WORK_DIR) or numpy/UnityPy.
        import os
        import sys
        work = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
        bundle = work / "build/full/arena_equator_navmesh.bundle"
        if not bundle.is_file():
            self.skipTest(f"no built NavMesh bundle {bundle}")
        sys.path.insert(0, str(REPO / "tools/validate"))
        try:
            import numpy as np
            import navmesh_diff as nd
            mesh = nd.load_bundle_mesh(bundle)
        except ImportError:
            self.skipTest("numpy or UnityPy is not installed")
        finally:
            sys.path.remove(str(REPO / "tools/validate"))
        reference = np.array([self.bots["bot_spawns"][0]["position"]], dtype=float)
        polygons, _ = mesh.locate(reference, max_dy=1.5)
        self.assertGreaterEqual(polygons[0], 0)
        play = int(mesh.labels[polygons[0]])
        for row in self.bots["bot_spawns"][10:]:
            xyz = np.array([row["position"]], dtype=float)
            polygons, surface = mesh.locate(xyz, max_dy=1.5)
            self.assertGreaterEqual(polygons[0], 0, row["id"])
            self.assertEqual(int(mesh.labels[polygons[0]]), play, row["id"])
            clearance = float(mesh.clearance(np.array([[xyz[0][0], surface[0], xyz[0][2]]]), dy=1.2)[0])
            self.assertGreaterEqual(clearance, 1.4, f"{row['id']} is only {clearance:.2f} m from the NavMesh border")

    def test_new_rows_keep_their_distance_to_every_other_row(self):
        # Measured 2026-11-12: at least 9.8 m to any other row, 19.8 m to any Player row (the proposal's stricter checks).
        players = self.bots["player_spawns"]
        for row in self.bots["bot_spawns"][10:]:
            for other in self.bots["bot_spawns"] + players:
                if other["id"] == row["id"]:
                    continue
                limit = 19.5 if other in players else 9.5
                self.assertGreaterEqual(math.dist(row["position"], other["position"]), limit, f"{row['id']} / {other['id']}")

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

    def test_each_exit_stands_inside_the_spawn_pocket_of_its_booth(self):
        # R3 (2026-11-12, replaces "exits stand outside the pockets"): one exit per team pocket, the 4 x 3 x 4 m box centred inside the
        # pocket's initial spawn-bound footprint (equator.modes.json), so that the green signal flare marks the team's spawn area in
        # ArenaMode's team modes (where extraction is disabled). Equator's boxes already stood in the booths; no exit moved.
        # Known: the South Gate box touches the back-wall elevator frames of the south booth (left as it is).
        bounds = {b["side"]: b for b in self.modes["spawn_bounds"]}
        self.assertEqual(len(self.exits["exits"]), len(bounds))
        claimed = set()
        for exit_point in self.exits["exits"]:
            self.assertEqual(exit_point["box_size"], [4.0, 3.0, 4.0], exit_point["name"])
            self.assertEqual(exit_point["rotation"], 0.0, exit_point["name"])
            inside = [side for side, box in bounds.items()
                      if all(abs(exit_point["position"][a] - box["center"][a]) <= box["size"][a] / 2 for a in (0, 2))]
            self.assertEqual(len(inside), 1, f"'{exit_point['name']}' centre is not inside exactly one pocket footprint")
            claimed.add(inside[0])
        self.assertEqual(claimed, set(bounds), "every pocket has exactly one exit")

    def test_nothing_stands_in_the_fck_hall(self):
        points = [(p["id"], p["position"]) for p in self.bots["player_spawns"] + self.bots["bot_spawns"]]
        points += [(e["name"], e["position"]) for e in self.exits["exits"]]
        for name, position in points:
            in_hall = position[0] < self.FCK_HALL_EAST_X and position[2] > self.FCK_HALL_SOUTH_Z
            self.assertFalse(in_hall, f"{name} {position} stands in the FCK hall, which the closed shutter cuts off the NavMesh")

    def test_bot_north_hall_moved_onto_the_field(self):
        bots = {b["id"]: b for b in self.bots["bot_spawns"]}
        self.assertEqual(len(bots), 16)
        self.assertEqual(bots["arena_equator-bot-north-hall"]["position"], [-5.5, 0.1, 33.0])
        self.assertEqual(bots["arena_equator-bot-north-booth-east"]["position"], [4.5, 0.1, 38.5])

    def test_the_fck_shutter_is_not_opened(self):
        # Arena walls the shutter off (LevelBorder (83)); the bake keeps it as a wall and the client leaves it standing.
        self.assertEqual(self.descriptor["opened_barriers"], [])


if __name__ == "__main__":
    unittest.main()
