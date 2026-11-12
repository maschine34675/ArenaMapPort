"""The spawn layout rules (R1 rows, R3 exits) of the six registered maps, checked on real data with tools/validate/spawn_coverage.py --check.

What it pins are RULES, not coordinates (a later host-driven nudge of an exit touches only the sidecar and the descriptor):
  R1  every Bot and Player row lies on the play component of the BUILT NavMesh; ids are unique; ArenaMode's pinned donor row of the map is
      still there with its exact floats (byte-identity of the whole original prefix belongs to the sidecar streams); no id carries '-tdm-v1-';
      the rows appended in the 2026-11-12 spawn increase (from the first-new id of the table below to the end; the count is asserted and must
      be above 0) keep the border clearance (1.35 m minus 0.02 m for the rounding of 2-decimal positions) while the older rows need
      membership only (Bay5 carries old rows 0.02 m from the border);
  R3  one exit per team pocket, the 4 x 3 x 4 box (rotation 0) INSIDE its pocket's initial spawn-bound footprint (the green signal flare then marks the
      team's spawn area), every Player row at least 2.5 m from the box EDGE, unrounded (the Equator booth precedent, docs/progress/equator-booth-spawns.md),
      the NavMesh floor under the centre and no solid in the box between floor+0.3 and floor+2.5.
Plain-raid reachability (Air Pit's north pocket lies behind the held passport gates and is not reachable until a mode plugin releases them), the
markers inside a box and the coverage numbers are printed by the tool and are information here.

Listed exceptions, with their reasons:
  Air Pit South Gate   a MOUTH exit: its nearest box EDGE stands at most 1 m outside the pocket footprint (decided position: edge 0.41 m, centre
                       2.41 m out; the first solid-free box on the built mesh has that centre, so a centre rule cannot be met). No in-pocket box keeps
                       the south Player rows 2.5 m from the box edge and boxes closer to the pocket hold solids or lie off the mesh.
  Equator South Gate   its box touches the back-wall elevator frames of the booth (the booth precedent); the solid check is information there.

Inputs (all optional; a missing bundle or package skips the map, a skipped test is not evidence, read the skip reason):
  ARENAMAPPORT_WORK_DIR   the folder that holds build/ and extract/ (default <repo>/work)
  ARENAMAPPORT_MAPS_DIR   the sidecar folder (default <repo>/tools/maps); point it at a scratch copy to check a layout before it is committed
"""
from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
import spawn_coverage as sc  # noqa: E402

WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR") or REPO / "work")
MAPS_DIR = Path(os.environ.get("ARENAMAPPORT_MAPS_DIR") or REPO / "tools" / "maps")

# stem -> (build folder under work/build, package under work/extract, first row appended by the spawn increase, mouth exits, solid exceptions)
LAYOUTS = {
    "equator": ("full", "equator-native-v5", "arena_equator-bot-core-12", (), ("Equator South Gate",)),
    "bay5": ("bay5", "bay5-native-v5", "arena_bay5-bot-core-08", (), ()),
    "airpit": ("airpit", "airpit-native-v10", "arena_airpit-bot-center-lane", ("Air Pit South Gate",), ()),
    "bowl": ("bowl", "bowl-native-v2", "arena_bowl-bot-born-16", (), ()),
    "iceberg": ("iceberg", "iceberg-native-v2", "arena_iceberg-bot-stage-bear-46", (), ()),
    "autoservice": ("autoservice", "autoservice-native-v2", "arena_autoservice-bot-lasthero-04", (), ()),
}


class SpawnLayoutRuleTests(unittest.TestCase):
    def check(self, stem: str) -> dict:
        build, package, first_new, mouth, solid_ok = LAYOUTS[stem]
        nav = WORK / "build" / build / f"arena_{stem}_navmesh.bundle"
        pkg = WORK / "extract" / package
        if not nav.is_file():
            self.skipTest(f"no built NavMesh bundle {nav}")
        if not (pkg / "scenes").is_dir():
            self.skipTest(f"no extracted package {pkg}")
        inputs, mesh, cset = sc.load_world(stem, MAPS_DIR, None, nav, pkg)
        rep = sc.check_layout(inputs, mesh, cset, {"probe": False}, stem=stem, appended_since=first_new, mouth_exits=set(mouth), solid_exceptions=set(solid_ok))
        self.assertEqual(rep["violations"], [], "\n" + sc.format_check(rep))
        # the clearance rule must have been applied to the appended rows (a drifted first-new id would switch it off silently)
        ids = [r["id"] for r in inputs["bots"]["bot_spawns"]]
        self.assertIn(first_new, ids)
        n_new = len(ids) - ids.index(first_new)
        self.assertGreater(n_new, 0)
        self.assertEqual(rep["rows_checked_for_clearance"], n_new)
        return rep

    def test_equator(self):
        rep = self.check("equator")
        south = next(e for e in rep["exits"] if e["name"] == "Equator South Gate")
        # the reason of the listed exception: the solids in that box are the booth's elevator frames and nothing else
        self.assertFalse(south["solid_free"])
        self.assertTrue(all("elevator_interactive" in s for s in south["solids_in_box"]), south["solids_in_box"])

    def test_bay5(self):
        self.check("bay5")

    def test_airpit(self):
        rep = self.check("airpit")
        by_name = {e["name"]: e for e in rep["exits"]}
        # information pinned as a consequence of the decision, not as a rule: the north gate lies behind the held passport gates
        self.assertFalse(by_name["Air Pit North Gate"]["plain_raid_reachable"])
        self.assertTrue(by_name["Air Pit South Gate"]["plain_raid_reachable"])

    def test_bowl(self):
        self.check("bowl")

    def test_iceberg(self):
        self.check("iceberg")

    def test_autoservice(self):
        self.check("autoservice")

    def test_the_table_names_every_registered_map(self):
        self.assertEqual(sorted(LAYOUTS), sorted(sc.DONOR_IDS))


if __name__ == "__main__":
    unittest.main()
