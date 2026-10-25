"""Spawn points off the NavMesh edge (2026-10-25; docs/progress/equator-booth-spawns.md and docs/progress/door-poses.md).

Why: the placement repair (SpawnPlacementRepairs) pushed four authored spawn rows 1.5 m on every raid because they stood on the
very rim of the port's own NavMesh: Equator `bot-south-west` (0.00 m of clearance) and `bot-south-east` (0.05 m), Bay5
`player-north-west` (0.03 m, outside every polygon) and `bot-east-lane` (0.09 m, outside). They moved to points with
1.25 to 1.50 m of clearance on the play component (no Unity rebuild, no rebake: the NavMesh is unchanged).

The data half runs everywhere: the moved coordinates, the distance to every other spawn of the map (nothing may crowd another
spawn, the ArenaMode pickup seeding and the 25 m burst rule rely on the spread) and the four donor rows that ArenaMode pins
(`ArenaTeamBoxCatalog`: Bay5 bot-west-centre, Equator bot-atrium, Air Pit bot-center-east, Bowl bot-core-02) staying exactly
where they are. The NavMesh half reads the built bundles under the ignored work/build and is skipped without UnityPy, numpy or
the bundle; re-run it after every rebake of Equator or Bay5, because a rebake can shift the borders.

Not asserted: the ten other Bay5 points that sit on the mesh edge (clearance 0.02 to 0.45 m, "Tier B" of the 2026-10-25 plan:
no host defect, a thin margin to the depot-leaf footprint test) - they are a recorded decision, not an oversight.
"""
import json
import math
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))

# stem -> {spawn id: (position, minimum clearance in the built NavMesh in metres)}
MOVED = {
    "equator": {
        "arena_equator-bot-south-west": ([-7.5, 0.1, -28.0], 1.0),
        "arena_equator-bot-south-east": ([7.5, 0.1, -29.0], 1.0),
    },
    "bay5": {
        "arena_bay5-player-north-west": ([55.2, -3.8574, 53.0], 1.0),
        "arena_bay5-bot-east-lane": ([118.3, -3.8574, 44.4], 1.0),
    },
}
# The donors ArenaMode pins (ArenaTeamBoxCatalog.cs): not to be moved by spawn hygiene.
DONORS = {
    "bay5": {"arena_bay5-bot-west-centre": [63.497, -3.8574, 42.2217]},
    "equator": {"arena_equator-bot-atrium": [0.0, 0.2, 2.0]},
    "airpit": {"arena_airpit-bot-center-east": [-3.21, 0.1, -12.09]},
    "bowl": {"arena_bowl-bot-core-02": [2.5, 0.11, -21.1]},
}
BUILT_NAVMESH = {
    "equator": REPO / "work/build/full/arena_equator_navmesh.bundle",
    "bay5": REPO / "work/build/bay5/arena_bay5_navmesh.bundle",
}
# A spawn that is not moved on purpose still has to stand inside the mesh with this much room on Equator (the lowest
# unmoved Equator point is bot-east with 0.78 m on 2026-10-25).
EQUATOR_MIN_CLEARANCE = 0.5
# Distance of a moved point to every other spawn of its map (the smallest on 2026-10-25 is 10.4 m).
MIN_NEIGHBOUR_DISTANCE = 10.0


def spawn_rows(stem):
    data = json.loads((REPO / f"tools/maps/{stem}.bots.json").read_text(encoding="utf-8"))
    return {row["id"]: row for row in data.get("player_spawns", []) + data.get("bot_spawns", [])}


class MovedSpawnDataTests(unittest.TestCase):
    def test_the_four_edge_spawns_hold_their_inboard_coordinates(self):
        for stem, moved in MOVED.items():
            rows = spawn_rows(stem)
            for sid, (position, _) in moved.items():
                with self.subTest(spawn=sid):
                    self.assertEqual(rows[sid]["position"], position)

    def test_no_moved_spawn_crowds_another_spawn_of_its_map(self):
        for stem, moved in MOVED.items():
            rows = spawn_rows(stem)
            for sid, (position, _) in moved.items():
                for other_id, other in rows.items():
                    if other_id == sid:
                        continue
                    with self.subTest(spawn=sid, other=other_id):
                        self.assertGreaterEqual(math.dist(position, other["position"]), MIN_NEIGHBOUR_DISTANCE)

    def test_the_arenamode_donor_rows_stay_where_they_are(self):
        for stem, donors in DONORS.items():
            rows = spawn_rows(stem)
            for sid, position in donors.items():
                with self.subTest(spawn=sid):
                    self.assertEqual(rows[sid]["position"], position)

    def test_the_moves_are_horizontal_and_keep_the_authored_height(self):
        # The moves are horizontal: y stays the authored value (the placement repair snaps to the NavMesh at raid time).
        old_y = {"arena_equator-bot-south-west": 0.1, "arena_equator-bot-south-east": 0.1,
                 "arena_bay5-player-north-west": -3.8574, "arena_bay5-bot-east-lane": -3.8574}
        for stem, moved in MOVED.items():
            rows = spawn_rows(stem)
            for sid in moved:
                self.assertEqual(rows[sid]["position"][1], old_y[sid])


class BuiltNavMeshClearanceTests(unittest.TestCase):
    """The same points against the built NavMesh bundles (ignored work/build): inside a polygon of the play component with room to spare."""

    @staticmethod
    def load(stem):
        path = BUILT_NAVMESH[stem]
        if not path.is_file():
            raise unittest.SkipTest(f"no built NavMesh bundle {path.relative_to(REPO)}")
        try:
            import numpy  # noqa: F401
            import navmesh_diff as nd
            return nd.load_bundle_mesh(path)
        except ImportError:
            raise unittest.SkipTest("numpy or UnityPy is not installed")

    @staticmethod
    def probe(mesh, position):
        import numpy as np
        xyz = np.array([position], dtype=float)
        polygons, surface = mesh.locate(xyz, max_dy=1.5)
        if polygons[0] < 0:
            return -1, None, 0.0
        clearance = float(mesh.clearance(np.array([[position[0], surface[0], position[2]]]), dy=1.2)[0])
        return int(mesh.labels[polygons[0]]), float(surface[0]), clearance

    def check_map(self, stem, reference_id, minimum_for_unmoved):
        mesh = self.load(stem)
        rows = spawn_rows(stem)
        play_component, _, _ = self.probe(mesh, rows[reference_id]["position"])
        self.assertGreaterEqual(play_component, 0, f"{reference_id} is outside the built NavMesh")
        for sid, row in rows.items():
            component, surface, clearance = self.probe(mesh, row["position"])
            with self.subTest(spawn=sid):
                self.assertGreaterEqual(component, 0, f"{sid} is outside every polygon of the built NavMesh")
                self.assertEqual(component, play_component, f"{sid} is not on the play component")
                if sid in MOVED.get(stem, {}):
                    self.assertGreaterEqual(clearance, MOVED[stem][sid][1],
                                            f"{sid} is only {clearance:.2f} m from the NavMesh border")
                elif minimum_for_unmoved is not None:
                    self.assertGreaterEqual(clearance, minimum_for_unmoved,
                                            f"{sid} is only {clearance:.2f} m from the NavMesh border")

    def test_equator_spawns_stand_inside_the_play_component_with_room(self):
        self.check_map("equator", "arena_equator-player-all", EQUATOR_MIN_CLEARANCE)

    def test_bay5_spawns_are_inside_the_mesh_and_the_moved_ones_have_room(self):
        # Before 2026-10-25 player-north-west and bot-east-lane lay OUTSIDE every polygon. The other ten edge points of Bay5 are
        # inside the mesh with 0.02 to 0.45 m (Tier B, a recorded decision), so no minimum is asserted for them.
        self.check_map("bay5", "arena_bay5-player-north-east", None)


if __name__ == "__main__":
    unittest.main()
