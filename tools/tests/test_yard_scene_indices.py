"""Yard's BuildSettings scene indices (docs/PLAN-YARD.md item 7, 2026-11-20).

Yard's scenes moved by one between the Arena 0.5.4 spike and 0.6.2 (the spike plan had them at lower numbers), and every later step of the
port (the plan's `index` fields, the key-source row, the extraction packages, the Unity importer's identity patch) addresses a scene by its
BuildSettings index. This module pins the nine indices that matter, by asset path, against

  * always: the tracked plan tools/extract/plans/yard.json and the key-source row of tools/bundle/key-sources.json agree with the pinned table
    (no Arena needed);
  * the installed Arena's own BuildSettings (the reader of the extractor, source_plan.read_build_scenes) when its build is 0.6.2.x, the build
    the table was measured on (0.6.2.0.48473, 2026-11-20). A newer build may legitimately move a scene, so these tests skip with their reason
    until the build is checked and added to PINNED_ARENA_BUILD; they skip as well without an Arena installation.

Nothing here hashes or copies Arena content: paths, indices and file existence only.
"""
import json
import os
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))

ARENA_ROOT = Path(os.environ.get("ARENAMAPPORT_ARENA_DIR", r"D:\Battlestate Games\Escape from Tarkov Arena"))
ARENA_DATA = ARENA_ROOT / "EscapeFromTarkovArena_Data"
# The pins were measured on 0.6.2.0.48473 (2026-11-20). 0.6.2.0.48598 and 0.6.2.0.48767 (other hosts) share the resource streams; their
# BuildSettings were not read, so a different 0.6.2 build is accepted here only because a moved scene would fail loudly, not silently.
PINNED_ARENA_BUILD = ("0.6.2.",)
FOLDER = "Assets/Content/Locations/Arena_Yard/"

# index -> scene name, the whole Yard set the port touches. Roles as in tools/extract/plans/yard.json.
SCENES = {
    72: "Arena_Yard",
    73: "Arena_Yard_DesignStuff",
    74: "Arena_Yard_Light",
    75: "Arena_Yard_Scripts",                 # role scripts (excluded, replaced by the authored Scripts scene)
    153: "Arena_Yard_TeamFight_Culling",     # role culling
    162: "Arena_Yard_Grass",
    163: "Arena_Yard_Sound",                 # role sound
    254: "Arena_Yard_Scripts_Winter",        # not exported, no role (like Autoservice's Scripts_Winter)
    310: "Arena_Yard_TeamFight",
    317: "Arena_Yard_TeamFight_DesignStuff",
    320: "Arena_Yard_AI",                    # role ai, the NavMesh reference scene
}
# the six scenes of the key-source row and of the plan's content set, the AI scene, the two excluded role scenes with a pin of their own
KEY_ROW = (72, 73, 74, 162, 310, 317)
ROLES = {"scripts": 75, "sound": 163, "culling": 153, "ai": 320}


def installed_version() -> str:
    try:
        return str(json.loads((ARENA_ROOT / "ConsistencyInfo").read_bytes().decode("utf-8-sig")).get("Version", ""))
    except (OSError, ValueError):
        return ""


HAVE_ARENA = (ARENA_DATA / "globalgamemanagers").is_file() and installed_version().startswith(PINNED_ARENA_BUILD)
_CACHE: dict = {}


def build_scenes() -> list:
    if "scenes" not in _CACHE:
        import source_plan
        _CACHE["scenes"] = source_plan.read_build_scenes(ARENA_DATA)
    return _CACHE["scenes"]


class PlanAndKeyRowAgreeTests(unittest.TestCase):
    """No Arena needed: the tracked files say what the table says."""

    def test_the_plan_lists_the_pinned_scenes_at_the_pinned_indices(self):
        plan = json.loads((REPO / "tools/extract/plans/yard.json").read_text(encoding="utf-8"))
        records = plan["scenes"] + plan["anchor_scenes"] + plan["navmesh_scenes"] + plan["excluded_scenes"]
        seen = {}
        for r in records:
            self.assertEqual(r["asset_path"], f"{FOLDER}{r['name']}.unity")
            self.assertEqual(seen.setdefault(r["index"], r["name"]), r["name"], "one name per index")
        self.assertEqual(seen, SCENES)
        self.assertEqual(sorted(r["index"] for r in plan["scenes"]), sorted(KEY_ROW))
        roles = {r["role"]: r["index"] for r in plan["excluded_scenes"] if "role" in r}
        self.assertEqual(roles, ROLES)

    def test_the_key_source_row_names_exactly_the_content_scenes(self):
        table = json.loads((REPO / "tools/bundle/key-sources.json").read_text(encoding="utf-8"))
        row = table["maps"]["arena_yard"]
        self.assertEqual([e["index"] for e in row], list(KEY_ROW))
        self.assertEqual({e["index"]: e["scene"] for e in row}, {i: SCENES[i] for i in KEY_ROW})
        # the three raw resource streams of the row, sizes are pinned in KeySourcesTests.LISTED (3 files, 639,778,432 B, no .resource file)
        self.assertEqual([f for e in row for f in e["files"]],
                         ["sharedassets72.assets.resS", "sharedassets74.assets.resS", "sharedassets310.assets.resS"])


@unittest.skipUnless(HAVE_ARENA, "the installed Arena is not 0.6.2.x (or not installed): the Yard indices were measured on 0.6.2.0.48473")
class InstalledArenaIndexTests(unittest.TestCase):
    def test_every_pinned_index_holds_its_yard_scene(self):
        scenes = build_scenes()
        for index, name in SCENES.items():
            with self.subTest(index=index, scene=name):
                self.assertLess(index, len(scenes))
                self.assertEqual(scenes[index], f"{FOLDER}{name}.unity")

    # BuildSettings lists most Yard scenes more than once (stripped copies of the other season/variant sets, measured 2026-11-20 on 0.6.2.0.48473).
    # The plan's index is the entry with the LARGEST sharedassets<N>.assets (source_plan.verify_scene_mapping); every pinned scene is the largest by a
    # wide margin except the culling scene, whose two entries (153 and 319) are both 433 bytes: a tie the plan check does not decide, and why the
    # culling scene stays an excluded role scene whose index is pinned by path here, never read from its size.
    DUPLICATES = {"Arena_Yard": [72, 165], "Arena_Yard_DesignStuff": [73, 168], "Arena_Yard_Light": [74, 167, 318], "Arena_Yard_Scripts": [75, 164],
                  "Arena_Yard_TeamFight_Culling": [153, 319], "Arena_Yard_TeamFight": [310, 316]}

    def test_the_duplicate_entries_are_exactly_the_known_ones(self):
        scenes = build_scenes()
        for index, name in SCENES.items():
            with self.subTest(scene=name):
                self.assertEqual([i for i, s in enumerate(scenes) if s == f"{FOLDER}{name}.unity"], self.DUPLICATES.get(name, [index]))

    def test_the_pinned_entry_is_the_largest_copy_except_the_culling_tie(self):
        scenes = build_scenes()

        def size(i):
            f = ARENA_DATA / f"sharedassets{i}.assets"
            return f.stat().st_size if f.is_file() else -1
        for name, copies in self.DUPLICATES.items():
            pinned = next(i for i, n in SCENES.items() if n == name)
            with self.subTest(scene=name):
                sizes = {i: size(i) for i in copies}
                self.assertEqual(scenes[pinned], f"{FOLDER}{name}.unity")
                if name == "Arena_Yard_TeamFight_Culling":
                    self.assertEqual(len(set(sizes.values())), 1, "the tie that makes size useless for this scene")
                else:
                    self.assertEqual([i for i in copies if sizes[i] == max(sizes.values())], [pinned], sizes)

    def test_the_level_files_of_the_content_scenes_exist(self):
        for index in list(KEY_ROW) + [320]:
            with self.subTest(index=index):
                self.assertTrue((ARENA_DATA / f"level{index}").is_file())
                self.assertTrue((ARENA_DATA / f"sharedassets{index}.assets").is_file())

    def test_the_source_plan_verifies_against_the_installed_arena(self):
        # the extractor's own scene mapping check (index, path and the largest-sharedassets rule), the one that fails if a scene moved
        import source_plan
        plan = source_plan.load_plan(REPO / "tools/extract/plans/yard.json")
        source_plan.verify_scene_mapping(plan, build_scenes(), ARENA_DATA)


if __name__ == "__main__":
    unittest.main()
