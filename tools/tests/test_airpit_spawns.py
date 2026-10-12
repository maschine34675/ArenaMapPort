"""AirPit data contract (docs/PLAN-AIRPIT.md item 6): tools/maps/airpit.bots.json, airpit.exits.json and the
generated ArenaMapPort.Server/db/arena_airpit/base.json.

Why: the sidecars are authored offline (nobody has walked AirPit yet), so everything checkable without the
host is pinned here: the generated base.json is not stale, the spawn table keeps the geometric rules the other
maps learned on the host (distant burst candidates for ArenaMode, no bot arrival on a player respawn, no spawn
inside an exit box, both PMC sides get both ends), and, when the ignored extracts and the installed Arena are
present, every position still derives from Arena's own data (marker world transforms, AI core points) and lies
in Arena's NavMesh with the documented border clearance. The C# side (descriptor mirror, server parse) is
ArenaMapPort.Server.Tests/AirPitMapTests.cs. Native reachability in the port's own bake is a host check.
"""
import importlib.util
import json
import math
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))

spec = importlib.util.spec_from_file_location("make_base_json", REPO / "tools/server/make_base_json.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)

# Plan Appendix A / the sidecar: player spawn id suffix -> original SPAWNS/ScavPMCspawn (n) marker; the three
# bot spawns that also come from markers; the bot spawns that come from Arena AI core points (id -> core id).
PLAYER_MARKERS = {"all": 19, "usec": 16, "bear": 4, "south-all": 13}
BOT_MARKERS = {"north-end-west": 9, "north-end-east": 17, "south-end": 15}
# 2026-10-12 (user decision, host raid 4): the passport-control doors stay closed and are the north limit of the
# play space, so the four points of the north spawn pocket (markers 19, 16, 9, 17; z 75..80) moved south of the
# door line (z 69.4) onto open Arena floor with >= 1 m border clearance; id -> upper bound of the move in metres.
MOVED_FROM_MARKER = {"arena_airpit-player-all": 20.0, "arena_airpit-player-usec": 21.0,
                     "arena_airpit-bot-north-end-west": 18.5, "arena_airpit-bot-north-end-east": 18.0}
DOOR_LINE_Z = 66.0   # the southern leaves of the closed passport doors sit at z 66.28
BOT_CORE_POINTS = {
    "north-approach": 6, "north-hall": 5, "north-hall-west": 17, "east-north": 2, "east-mid": 1,
    "east-south": 18, "west-mid": 15, "west-south": 16, "center-east": 3, "plane-south": 12, "south-apron": 13,
}
# Core points that sit within 0.8 m of the border of Arena's NavMesh (0.30 m and 0.75 m) and were moved inward.
MOVED_FROM_CORE_POINT = {"east-north": 1.6, "west-south": 6.2}   # id -> upper bound of the move in metres
MIN_ARENA_CLEARANCE = 1.0          # border clearance every spawn point keeps in Arena's own NavMesh
EXIT_HALF_BOX = 2.0


def package_dir():
    """The accepted 0.6.0 package only (PLAN-AIRPIT item 5); the superseded 0.5.4 spike package
    ``work/extract/airpit`` is deliberately not a fallback, the test skips when the accepted one is absent."""
    path = REPO / "work" / "extract" / "airpit-native-v5"
    if (path / "scenes" / "Arena_Airpit_TeamFight_DesignStuff.json").is_file():
        return path
    return None


class AirPitSidecarTests(unittest.TestCase):
    def setUp(self):
        self.bots = json.loads((REPO / "tools/maps/airpit.bots.json").read_text(encoding="utf-8"))
        self.exits = json.loads((REPO / "tools/maps/airpit.exits.json").read_text(encoding="utf-8"))
        self.map = json.loads((REPO / "tools/maps/airpit.json").read_text(encoding="utf-8"))
        self.base = json.loads((REPO / "ArenaMapPort.Server/db/arena_airpit/base.json").read_text(encoding="utf-8"))
        self.players = self.bots["player_spawns"]
        self.bot_points = self.bots["bot_spawns"]

    # ------------------------------------------------------------------ shape and generator freshness
    def test_counts_and_ids(self):
        self.assertEqual(len(self.players), 4)
        self.assertEqual(len(self.bot_points), 14)
        self.assertEqual(len(self.bots["waves"]), 5)
        self.assertEqual(len(self.bots["boss_waves"]), 4)
        self.assertEqual(len(self.exits["exits"]), 2)
        self.assertEqual(self.bots["zone"], "BotZone_Main")
        self.assertEqual(self.bots["max_bots"], 8)
        ids = [p["id"] for p in self.players + self.bot_points]
        self.assertEqual(len(ids), len(set(ids)))
        for pid in ids:
            self.assertTrue(pid.startswith("arena_airpit-"), pid)
        self.assertEqual({p["id"] for p in self.players},
                         {f"arena_airpit-player-{suffix}" for suffix in PLAYER_MARKERS})
        self.assertEqual({p["id"] for p in self.bot_points},
                         {f"arena_airpit-bot-{suffix}" for suffix in {**BOT_MARKERS, **BOT_CORE_POINTS}})

    def test_schema_matches_the_released_maps(self):
        equator = json.loads((REPO / "tools/maps/equator.bots.json").read_text(encoding="utf-8"))
        bay5_exits = json.loads((REPO / "tools/maps/bay5.exits.json").read_text(encoding="utf-8"))
        self.assertEqual(set(self.bots), set(equator))
        self.assertEqual({k for p in self.players for k in p}, {k for p in equator["player_spawns"] for k in p})
        self.assertLessEqual({k for p in self.bot_points for k in p}, {k for p in equator["bot_spawns"] for k in p})
        self.assertEqual(set(self.bots["waves"][0]), set(equator["waves"][0]))
        self.assertEqual(set(self.bots["boss_waves"][0]), set(equator["boss_waves"][0]))
        self.assertLessEqual(set(self.exits), {"_comment", "exits"})
        self.assertEqual(set(self.exits["exits"][0]), set(bay5_exits["exits"][0]))
        self.assertIn("2026-10-11", self.bots["_comment"])
        self.assertIn("2026-10-11", self.exits["_comment"])

    def test_pmc_squads_use_the_authored_zone_and_both_sides(self):
        names = sorted(w["boss_name"] for w in self.bots["boss_waves"])
        self.assertEqual(names, ["pmcBEAR", "pmcBEAR", "pmcUSEC", "pmcUSEC"])
        for wave in self.bots["boss_waves"]:
            self.assertEqual(wave["boss_zone"], self.bots["zone"])
            self.assertEqual(wave["escort_type"], wave["boss_name"])
        for wave in self.bots["waves"]:
            self.assertLess(wave["time_min"], wave["time_max"])
            self.assertLessEqual(wave["slots_min"], wave["slots_max"])

    def test_generated_base_json_fields_are_current(self):
        generated = {}
        generator.apply_bots(generated, self.map, self.bots)
        generator.apply_boss_waves(generated, self.bots)
        generator.apply_exits(generated, self.map, self.exits)
        self.assertEqual({"SpawnPointParams", "waves", "OpenZones", "BotMax", "OfflineOldSpawn", "OfflineNewSpawn",
                          "BossLocationSpawn", "exits"}, set(generated))
        for key, value in generated.items():
            self.assertEqual(self.base[key], value, f"Stale generated field: {key}")
        self.assertEqual(len(self.base["SpawnPointParams"]), 18)
        self.assertEqual(len(self.base["waves"]), 5)
        self.assertEqual(len(self.base["BossLocationSpawn"]), 4)
        self.assertEqual(len(self.base["exits"]), 2)
        self.assertEqual(self.base["BotMax"], 8)
        self.assertEqual(self.base["OpenZones"], "BotZone_Main")

    def test_base_json_keeps_the_map_policy(self):
        self.assertTrue(self.base["DisabledForScav"])
        self.assertEqual(self.base["Loot"], [])
        self.assertEqual(self.base["Banners"], [])
        self.assertEqual(self.base["Id"], "arena_airpit")
        self.assertEqual(self.base["_Id"], self.map["id_field"])
        for exit_point in self.base["exits"]:
            self.assertEqual(exit_point["EntryPoints"], self.map["infiltration_name"])
        for point in self.base["SpawnPointParams"]:
            if "Player" in point["Categories"]:
                self.assertEqual(point["Infiltration"], self.map["infiltration_name"])
            else:
                self.assertEqual(point["BotZoneName"], "BotZone_Main")
                self.assertIn("BotPmc", point["Categories"])

    # ------------------------------------------------------------------ geometry rules
    def test_positions_are_finite_ground_floor_points(self):
        for point in self.players + self.bot_points:
            self.assertTrue(all(math.isfinite(v) for v in point["position"]), point["id"])
            self.assertGreaterEqual(point["position"][1], 0.0)
            self.assertLessEqual(point["position"][1], 0.2)
        for point in self.bot_points:
            self.assertEqual(set(point["categories"]), {"Bot", "BotPmc"})
            self.assertEqual(point["radius"], 25)
        for point in self.players:
            self.assertEqual(point["radius"], 5)
        for exit_point in self.exits["exits"]:
            self.assertEqual(exit_point["box_size"], [4.0, 3.0, 4.0])
            self.assertEqual(exit_point["type"], "Individual")
            self.assertEqual(exit_point["chance"], 100)
            self.assertGreater(exit_point["exfiltration_time"], 0)

    def test_three_distant_bot_points_cover_the_playable_envelope(self):
        # ArenaMode's injector asks for two bots per burst and keeps a 25 m floor to the player: every cell of
        # Arena's walkable footprint (x -56..32, z -78..85, 1 m grid) must keep three distinct distant candidates.
        # Conservative distance coverage (it also samples non-walkable cells), not proof of a native path.
        points = [tuple(p["position"]) for p in self.bot_points]
        for x in range(-57, 34):
            for z in range(-79, 87):
                usable = sum(math.hypot(x - p[0], z - p[2]) >= 25 for p in points)
                self.assertGreaterEqual(usable, 3, f"Only {usable} distant points from ({x}, {z})")

    def test_bot_spawns_do_not_overlap_player_respawn_coordinates(self):
        for bot in self.bot_points:
            for player in self.players:
                self.assertGreaterEqual(math.dist(bot["position"], player["position"]), 2,
                                        f"{bot['id']} overlaps {player['id']}")

    def test_players_never_spawn_inside_or_near_an_exit_box(self):
        for player in self.players:
            for exit_point in self.exits["exits"]:
                self.assertGreaterEqual(math.dist(player["position"], exit_point["position"]), 10,
                                        f"{player['id']} too close to {exit_point['name']}")

    def test_player_respawns_offer_both_ends_to_both_pmc_sides(self):
        for side in ("Usec", "Bear"):
            points = [p for p in self.players if side in p["sides"] or "All" in p["sides"]]
            self.assertTrue(any(p["position"][2] > 60 for p in points), side)
            self.assertTrue(any(p["position"][2] < -60 for p in points), side)
        self.assertEqual({tuple(p["sides"]) for p in self.players}, {("All",), ("Usec",), ("Bear",)})

    def test_exit_names_are_unique_locale_keys(self):
        names = [e["name"] for e in self.exits["exits"]]
        self.assertEqual(len(names), len(set(names)))
        self.assertEqual(names, ["Air Pit North Gate", "Air Pit South Gate"])
        self.assertGreater(self.exits["exits"][0]["position"][2], 0)
        self.assertLess(self.exits["exits"][1]["position"][2], 0)

    # ------------------------------------------------------------------ source derivation (ignored extracts)
    @unittest.skipIf(package_dir() is None, "no extracted AirPit package")
    def test_marker_derived_positions_equal_the_original_marker_world_transforms(self):
        from port_colliders import Scene
        pkg = package_dir()
        scene = Scene(json.loads((pkg / "scenes/Arena_Airpit_TeamFight_DesignStuff.json").read_text(encoding="utf-8")))
        world = {}
        for oid, obj in scene.objects.items():
            if obj["name"].startswith("ScavPMCspawn ("):
                m = scene.world(oid)
                world[int(obj["name"].split("(")[1].rstrip(")"))] = (float(m[0, 3]), float(m[1, 3]), float(m[2, 3]))
        self.assertEqual(sorted(world), list(range(1, 21)))
        expected = {f"arena_airpit-player-{k}": n for k, n in PLAYER_MARKERS.items()}
        expected.update({f"arena_airpit-bot-{k}": n for k, n in BOT_MARKERS.items()})
        by_id = {p["id"]: p for p in self.players + self.bot_points}
        for pid, number in expected.items():
            x, y, z = world[number]
            px, py, pz = by_id[pid]["position"]
            if pid in MOVED_FROM_MARKER:
                self.assertLess(pz, DOOR_LINE_Z, f"{pid} must lie south of the closed passport-control line")
                self.assertGreater(z, DOOR_LINE_Z, f"marker {number} is not in the north pocket")
                self.assertLessEqual(math.hypot(px - x, pz - z), MOVED_FROM_MARKER[pid], pid)
                continue
            self.assertAlmostEqual(px, x, delta=0.006, msg=pid)
            self.assertAlmostEqual(pz, z, delta=0.006, msg=pid)
            self.assertAlmostEqual(py, y, delta=0.05, msg=pid)
        # Pockets: markers 1-5 and 11-15 are the south pocket (z -75..-71), 6-10 and 16-20 the north one (z 75..81).
        self.assertTrue(all(z < -70 for n, (x, y, z) in world.items() if n in (1, 2, 3, 4, 5, 11, 12, 13, 14, 15)))
        self.assertTrue(all(z > 75 for n, (x, y, z) in world.items() if n in (6, 7, 8, 9, 10, 16, 17, 18, 19, 20)))

    @unittest.skipUnless(Path("D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/sharedassets33.assets").is_file(),
                         "Arena source not installed")
    def test_core_point_spawns_and_every_point_stay_inside_arenas_navmesh(self):
        import numpy as np
        import navmesh_diff as nd
        core = {int(p["name"][4:]): p["position"] for p in nd.live_ai_points() if p["kind"] == "core_point"}
        self.assertEqual(len(core), 19)
        by_id = {p["id"]: p["position"] for p in self.bot_points}
        for suffix, number in BOT_CORE_POINTS.items():
            px, _py, pz = by_id[f"arena_airpit-bot-{suffix}"]
            cx, _cy, cz = core[number]
            moved = math.hypot(px - cx, pz - cz)
            if suffix in MOVED_FROM_CORE_POINT:
                self.assertGreater(moved, 1.0, suffix)             # really moved off the tight core point
                self.assertLessEqual(moved, MOVED_FROM_CORE_POINT[suffix], suffix)
            else:
                self.assertAlmostEqual(moved, 0.0, delta=0.01, msg=suffix)
        mesh, _meta = nd.load_arena_mesh()
        points = [(p["id"], p["position"], MIN_ARENA_CLEARANCE) for p in self.players + self.bot_points]
        points += [(e["name"], e["position"], EXIT_HALF_BOX + 0.5) for e in self.exits["exits"]]
        xyz = np.array([pos for _n, pos, _c in points], dtype=float)
        poly, surf = mesh.locate(xyz, max_dy=1.5)
        for (name, pos, need), pid, sy in zip(points, poly, surf):
            self.assertGreaterEqual(pid, 0, f"{name} is outside Arena's NavMesh")
            self.assertEqual(int(mesh.labels[pid]), 0, f"{name} is not in Arena's play-space component")
            self.assertLess(abs(sy - pos[1]), 0.2, f"{name} is not on the ground floor")
            clearance = float(mesh.clearance(np.array([[pos[0], sy, pos[2]]]))[0])
            self.assertGreaterEqual(clearance, need, f"{name} is only {clearance:.2f} m from the NavMesh border")


if __name__ == "__main__":
    unittest.main()
