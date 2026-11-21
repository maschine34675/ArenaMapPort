"""Yard NavMesh oracle row, bake inputs and border audit (docs/PLAN-YARD.md item 10, part 1): opened_barriers, start_gates, held_gates,
navmesh_bake_layers, keep_level_borders, bake_bounds, navmesh_bake_exclusions, uses_server_time and the puddle materials, and what the
oracle says about the pockets, the gates, the layer-0 solids, the south band, the roof islands and Arena's 118 border cubes.

Why: the Unity bake is a 15-minute batch run (item 16), so everything checkable offline is pinned here, as test_autoservice_bake_inputs.py
did for Autoservice. The DECIDED_* constants are the values tools/maps/yard.json carries since item 10 part 1; the Yard descriptor row of
ArenaMapPort.Shared/ArenaMapDescriptor.cs must mirror them number for number (part 2, which also adds the C# mirror tests and
validate_map_descriptors' view of the sidecar: until then the sidecar and the still-stub descriptor DISAGREE on purpose). Yard is registered since item 15 but not built
until plan item 15, so none of this reaches a release. Groups, each skipped when its input is absent:

  * constants and code pins (repository only): the validators accept the decided values, ARENA_SOURCES["yard"] is the measured row;
  * the sidecar: tools/maps/yard.json carries every decided key, equal to the constants, and none of the omitted ones;
  * the extracted package work/extract/yard-native-v2 (override the work directory with ARENAMAPPORT_WORK_DIR when the tests run from a git
    worktree without work/): the border cubes, the gates and their frames, the layer census, the layer-0 audit, the anchors against the bounds,
    the puddle materials;
  * the installed Arena (0.6.2.0.48473, read-only, UnityPy): Arena's NavMeshData (identity placement, 1,911 polygons, 11 components), the
    anchors on it, and the oracle runs themselves (navmesh_diff.analyse with the decided configuration and ``split_edge`` 1.0, about 30 s
    each) with Dijkstra routes on the modelled mesh.

The border measurement (the second half of the item) is in BorderSidecarTests, BorderOnArenasMeshTests and RegionModelTests at the end.
"""
import contextlib
import heapq
import io
import json
import math
import os
import sys
import unittest
from pathlib import Path
from unittest import mock

import numpy as np

REPO = Path(__file__).resolve().parents[2]
WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
sys.path.insert(0, str(REPO / "tools" / "extract"))
import extract_door_poses as door_poses  # noqa: E402
import navmesh_diff as nd  # noqa: E402
import port_colliders as pc  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402

_CACHE: dict = {}
PACKAGE = WORK / "extract" / "yard-native-v2"
MAIN_SCENE = "Arena_Yard"
TEAMFIGHT = "Arena_Yard_TeamFight"                                  # the 11 TeamFight_LevelBorders cubes
TEAMFIGHT_DS = "Arena_Yard_TeamFight_DesignStuff"                   # DOORS, SPAWNS, CAPTURE_POINT, SCAV_SPECTATORS
HAVE_PACKAGE = (PACKAGE / "scenes" / f"{TEAMFIGHT_DS}.json").is_file()
ARENA_DATA = Path(nd.DEFAULT_ARENA_DATA)
# The Arena-dependent pins were measured on 0.6.2.0.48473 (2026-11-20). A newer build may legitimately differ, so those tests skip with their
# reason instead of turning red until its build is checked and added here.
PINNED_ARENA_BUILD = ("0.6.2.",)
HAVE_ARENA = (ARENA_DATA / "sharedassets281.assets").is_file() and (ARENA_DATA / "level320").is_file() \
    and door_poses.arena_version(ARENA_DATA).startswith(PINNED_ARENA_BUILD)
YARD = nd.ARENA_SOURCES["yard"]
BORDERS = REPO / "tools" / "maps" / "yard.borders.json"

# ---------------------------------------------------------------------------------------------------------------------
# The decided values (2026-11-20/21), sidecar spelling. The C# descriptor names are in the comments.
# ---------------------------------------------------------------------------------------------------------------------
# The two gates of the TeamFight DesignStuff scene's DOORS, as unit paths: the strings the rows of yard.doors.json carry (a row matches its
# barrier by the EXACT string, BakeNavMesh.ResolveOpenedBarriers). Each path names exactly one gate in the scene. Descriptor: OpenedBarriers,
# StartGates. Arena baked its mesh with both open: the mesh crosses the north pocket's wall only through gate A and the south pocket's only
# through the cradle gap. Nothing is held (HeldGates): Arena authors no gate that stays shut after the release.
OPENED_BARRIERS = ["DOORS/Metal_gate_1", "DOORS/Construction_Cradle_02"]
START_GATES = list(OPENED_BARRIERS)
HELD_GATES: list = []
# Arena's NavMeshSurface mask 0x40042A00 (layers 9/11/13/18/30) plus 29 and 0:
#  * 29: the 11 TeamFight_LevelBorders cubes of the package stay active at runtime (KeepLevelBorders; they are 14 of Arena's 118 border cubes,
#    the other 104 are the sidecar tools/maps/yard.borders.json and reach the raid as invisible walls, see the border tests at the end); with 29
#    in the list and the flag set they are bake sources, exactly Bowl's, Iceberg's and Autoservice's policy. The three more cubes under the
#    gate parts move with their part and are dropped by the door rows. The oracle says the 11 cubes cut one sample of the model: bake-neutral.
#  * 0: the excavator's collider (x -18.84..-9.42, z 16.15..20.18, y 6.29..12.30, layer 0) is a solid the player collides with (EFT's physics
#    matrix: Player x Default) and Arena's own mesh walks through it (21 m2 of Arena's polygons lie in its footprint, 11.2 m2 of samples are blocked
#    for the agent); the mask never sees layer 0.
#    Layer 0 holds eight package colliders: the excavator, five wall lamps (above head height, beyond the play space) and the two SPAWN_BOUND
#    boxes, which the bake drops by name anyway.
ARENA_LAYERS = [9, 11, 13, 18, 30]
NAVMESH_BAKE_LAYERS = [0, 9, 11, 13, 18, 29, 30]            # NavMeshBakeLayers
KEEP_LEVEL_BORDERS = True                                   # KeepLevelBorders
# x, z: Arena's own NavMeshSurface volume (x -66.4..20.8, z -19.0..161.1) rounded outward, except the south edge: z -1 instead of -20.
# The south band (z < 17) lies behind the 76 m long wall of the Respawn building, which is a solid layer-18 collider with no opening but the
# cradle gap; the band is part of an island of the port's NavMesh that nobody can reach (OracleTests), 3,740 m2 at z -20 (the model before layer 0 and 29 joined the list) and 2,085 m2 at z -1. z -1 keeps
# the pocket (z 2.2..17.9), its closing cubes (z -0.2) and a margin. y: 4 is 2.15 m under the floor (6.15); 14 is 3.3 m over the highest
# floor of the yard component (10.65, the transformer roof strip 10.7) + the agent: the nine roof islands of Arena's mesh (y 21 to 29) and every
# roof and scaffold the port model finds above the yard stay out; nothing walkable above y 10.65 is connected to the yard (RegionModelTests).
BAKE_BOUNDS = [-67, 21, 4, 14, -1, 162]                     # BakeBounds
NAVMESH_BAKE_EXCLUSIONS: list = []                          # NavMeshBakeExclusions: none (BakeBounds keep the under-floor and roof geometry out)
USES_SERVER_TIME = True                                     # UsesServerTime (user, 2026-11-20: "defaults passen so")
LOCALE_NAME = "Arena: Block"                                # LocaleName (user, 2026-11-20); the description text is part 2 / item 11
# Water decals: the rain-driven 'puddle' (12 puddle2 quads, the material id of Equator, Air Pit, Bowl, Iceberg and Autoservice); it is the only
# material of the package with the water decal shader.
PUDDLE_WATER_MATERIAL_NAMES = ["0877f2335044042e"]          # PuddleWaterMaterialNames
# ShaderFallbackNames, in the order of the item-13 hand-off (the four after the Autoservice three are Yard's rows)
SHADER_FALLBACK_NAMES = ["Custom/Billboard_FogSheet_Simple", "Cloth/ClothShader", "Cloth/ClothShader_backface",
                         "p0/Reflective/Bumped Specular SMap Baked", "Nature/SpeedTree", "Impostors/Examples/StandardCrossfade"]

DECIDED_SIDECAR_KEYS = {
    "locale_name": LOCALE_NAME,
    "opened_barriers": OPENED_BARRIERS,
    "start_gates": START_GATES,
    "held_gates": HELD_GATES,
    "navmesh_bake_layers": NAVMESH_BAKE_LAYERS,
    "keep_level_borders": KEEP_LEVEL_BORDERS,
    "bake_bounds": BAKE_BOUNDS,
    "navmesh_bake_exclusions": NAVMESH_BAKE_EXCLUSIONS,
    "uses_server_time": USES_SERVER_TIME,
    "puddle_water_material_names": PUDDLE_WATER_MATERIAL_NAMES,
    "shader_fallback_names": SHADER_FALLBACK_NAMES,
}
# Keys the sidecar must NOT carry, and why: no extra puddle surface (decided on the host, plan section 0), no authored_borders (the AirPit shape is a
# list of wall segments; Yard's 104 cubes are oriented boxes with their own data, see the hand-off of item 10 part 1), no emission cap (the strongest
# emissive material is 3.93), no native water (no WaterContainer, no Terrain).
OMITTED_SIDECAR_KEYS = ("puddle_extra_surfaces", "authored_borders", "emission_cap", "emission_floor", "has_native_water")

# Anchors outside the package: the capture point and both exits are read from the tracked sidecars (R6: no literal copy of a position).
MODES = json.loads((REPO / "tools" / "maps" / "yard.modes.json").read_text(encoding="utf-8"))
BOTS = json.loads((REPO / "tools" / "maps" / "yard.bots.json").read_text(encoding="utf-8"))
EXITS = json.loads((REPO / "tools" / "maps" / "yard.exits.json").read_text(encoding="utf-8"))["exits"]
CAPTURE_POINT = tuple(MODES["capture_point"]["position"])
# The outer ring: the 3,426 m2 region of the port model that is walled off from the yard (the south band, the strips west of the SW fence and
# east of the SE fence). The first draft of item 8 (Arena's mesh only) had two fill rows in it, arena_yard-bot-fill-02 at (10.5, 6.15, 25.5) and
# arena_yard-bot-fill-03 at (-56.5, 6.21, 22.5); the lead moved them into the yard on 2026-11-21, before any build, so the tuple is empty and the
# tests below pin "no row outside the yard". A row that lands in the ring again fails them.
RING_ROWS = ()
SIDECAR_ROWS_TOUCHING = 38           # of the 104 rows, the number whose box meets a standing point of the yard (edge grazes, 1.0 m2 at most each)


def sidecar(name="yard.json"):
    return json.loads((REPO / "tools" / "maps" / name).read_text(encoding="utf-8"))


def door_rows():
    """The rows the bake reads (ArenaPaths.DoorRows) from tools/maps/yard.doors.json: the sidecar next to the descriptor."""
    if "rows" not in _CACHE:
        _CACHE["rows"] = pc.load_door_rows(REPO / "tools" / "maps" / "yard.doors.json", "arena_yard")
    return _CACHE["rows"]


def anchors():
    """name -> position of the 20 markers, the capture point, the 4 Player rows, the 18 Bot rows and the 2 exits (45 probes)."""
    out = {"capture": CAPTURE_POINT}
    for side in ("south", "north"):
        for m in MODES["spawn_markers"][side]:
            out[m["name"].replace(" ", "_")] = tuple(m["position"])
    for p in BOTS["player_spawns"]:
        out[p["name"]] = tuple(p["position"])
    for p in BOTS["bot_spawns"]:
        out[p["id"]] = tuple(p["position"])
    for e in EXITS:
        out[e["name"]] = tuple(e["position"])
    return out


# ---------------------------------------------------------------------------------------------------------------------
# 1. constants and code pins
# ---------------------------------------------------------------------------------------------------------------------
class DecidedValuesTests(unittest.TestCase):
    def decided(self):
        return {k: v for k, v in DECIDED_SIDECAR_KEYS.items()}

    def test_the_validators_accept_the_decided_values(self):
        self.assertEqual(VD.bake_input_errors(self.decided()), [])
        self.assertEqual(VD.puddle_errors({"puddle_water_material_names": PUDDLE_WATER_MATERIAL_NAMES}), [])

    def test_every_gate_is_a_start_gate_and_nothing_is_held(self):
        self.assertEqual(START_GATES, OPENED_BARRIERS)
        self.assertEqual(HELD_GATES, [])
        self.assertEqual(len(set(OPENED_BARRIERS)), 2)

    def test_the_barriers_are_exactly_the_motion_barriers_of_the_doors_sidecar(self):
        rows = json.loads((REPO / "tools" / "maps" / "yard.doors.json").read_text(encoding="utf-8"))["poses"]
        self.assertEqual(len(rows), 3)                                          # the cradle plate and the two gate leaves
        self.assertEqual(sorted({p["barrier"] for p in rows if "switch" not in p}), sorted(OPENED_BARRIERS))
        self.assertEqual([p for p in rows if "switch" in p], [])                # Yard has no lamp group

    def test_the_layer_list_is_arenas_surface_mask_plus_the_border_layer_and_the_default_layer(self):
        self.assertEqual(sorted(ARENA_LAYERS + [0, 29]), NAVMESH_BAKE_LAYERS)
        self.assertEqual(sum(1 << layer for layer in ARENA_LAYERS), YARD.layer_mask)
        self.assertEqual(YARD.layer_mask, 0x40042A00)
        self.assertEqual(YARD.layer_mask, nd.AIRPIT_ARENA_LAYER_MASK)           # the AI scenes of AirPit, Iceberg, Autoservice and Yard share it
        self.assertIn(29, NAVMESH_BAKE_LAYERS)                                  # keep_level_borders without 29 is rejected
        self.assertNotIn(4, NAVMESH_BAKE_LAYERS)
        self.assertIn(0, NAVMESH_BAKE_LAYERS)
        self.assertTrue(KEEP_LEVEL_BORDERS)

    def test_keep_level_borders_without_layer_29_is_rejected(self):
        bad = dict(self.decided(), navmesh_bake_layers=[0] + ARENA_LAYERS)
        self.assertTrue(any("layer 29" in e for e in VD.bake_input_errors(bad)))

    def test_the_bake_box_is_ordered_and_sits_inside_arenas_own_volume_except_the_south_cut(self):
        x0, x1, y0, y1, z0, z1 = BAKE_BOUNDS
        self.assertTrue(x0 < x1 and y0 < y1 and z0 < z1)
        # Arena's NavMeshSurface volume: centre (-22.8, 12, 71), size 87.2 x 121.99 x 180.1 -> x -66.4..20.8, z -19.0..161.1 (ArenaMeshTests)
        self.assertLessEqual(x0, -66.4)
        self.assertGreaterEqual(x1, 20.8)
        self.assertLessEqual(x1 - 20.8, 1.0)
        self.assertLessEqual(-66.4 - x0, 1.0)
        self.assertGreaterEqual(z1, 161.1 - 1.0)
        self.assertLessEqual(z1 - 161.1, 1.0)
        # the south cut: the pocket (Arena's mesh from z 2.2) and its closing cubes (z -0.2) stay inside with a margin
        self.assertLessEqual(z0, -0.2 - 0.5)
        self.assertGreater(z0, -19.0)
        self.assertLessEqual(y0, 6.15 - 2.0)                  # floor 6.15; the pocket platform 6.9..7.5 and the decks (to 10.65) lie above
        self.assertGreaterEqual(y1, 10.7 + 1.7)               # Arena's highest main-component surface (the transformer roof strip) plus the agent
        self.assertLessEqual(y1, 14.0)                        # the nine roof islands (y 21..29) stay out

    def test_no_bake_exclusion_is_needed_and_none_overlaps_a_barrier(self):
        self.assertEqual(NAVMESH_BAKE_EXCLUSIONS, [])
        for path in NAVMESH_BAKE_EXCLUSIONS:
            self.assertFalse(any(path == b or path.startswith(b + "/") for b in OPENED_BARRIERS), path)

    def test_the_water_material_is_unique(self):
        self.assertEqual(len(set(PUDDLE_WATER_MATERIAL_NAMES)), len(PUDDLE_WATER_MATERIAL_NAMES))


class SidecarTests(unittest.TestCase):
    def test_the_sidecar_carries_every_decided_value_and_they_are_equal(self):
        data = sidecar()
        self.assertEqual([k for k in DECIDED_SIDECAR_KEYS if k not in data], [])
        for key, expected in DECIDED_SIDECAR_KEYS.items():
            self.assertEqual(data[key], expected, key)
        for key in OMITTED_SIDECAR_KEYS:
            self.assertNotIn(key, data, key)
        self.assertEqual(VD.bake_input_errors(data), [])
        self.assertEqual(data["key"], "arena_yard")
        self.assertEqual((data["icon_x"], data["icon_y"]), (691, 799))          # the selection marker stays at the measured value until item 25
        self.assertNotIn("(spike)", data["locale_name"])
        self.assertNotIn("Internal test build", data["locale_description"])

    def test_the_other_sidecars_keep_their_bake_inputs(self):
        # a regression guard: this commit touches no other map's inputs
        bowl, iceberg, auto = sidecar("bowl.json"), sidecar("iceberg.json"), sidecar("autoservice.json")
        self.assertIs(bowl["keep_level_borders"], True)
        self.assertEqual(bowl["navmesh_bake_layers"], [9, 11, 18, 29, 30])
        self.assertEqual(iceberg["bake_bounds"], [-62, 121, -3, 8, -131, -33])
        self.assertEqual(iceberg["navmesh_bake_layers"], [9, 11, 13, 18, 29, 30])
        self.assertEqual(auto["bake_bounds"], [-415, -331, -5, 2, 464, 515])
        self.assertEqual(auto["navmesh_bake_layers"], [9, 11, 13, 18, 29, 30])
        self.assertIs(auto["keep_level_borders"], True)
        for stem in ("equator", "bay5", "airpit"):
            self.assertNotIn("keep_level_borders", sidecar(f"{stem}.json"), stem)


class ArenaSourceRowTests(unittest.TestCase):
    def test_the_row_is_the_measured_one(self):
        self.assertEqual((YARD.key, YARD.assets, YARD.path_id), ("arena_yard", "sharedassets281.assets", 2))
        self.assertEqual((YARD.ai_scene_index, YARD.ai_scene_name), (320, "Arena_Yard_AI"))
        self.assertEqual(YARD.layer_mask, 0x40042A00)
        self.assertEqual(YARD.package, "work/extract/yard-native-v2")

    def test_the_ai_scene_is_the_ai_role_of_the_source_plan(self):
        try:
            derived = nd.arena_source_from_plan(REPO / "tools" / "extract" / "plans" / "yard.json", YARD)
        except ImportError:
            self.skipTest("UnityPy is not installed")
        self.assertEqual(derived, YARD)
        plan = json.loads((REPO / "tools" / "extract" / "plans" / "yard.json").read_text(encoding="utf-8"))
        self.assertEqual([(s["index"], s["name"]) for s in plan["navmesh_scenes"]], [(320, "Arena_Yard_AI")])

    def run_main(self, *argv):
        seen = {}

        def analyse(package, cfg, barriers, points, arena_data, **kw):
            seen.update(package=package, cfg=cfg, barriers=list(barriers), points=points, **kw)
            return {}

        def live(arena_data, scene_index, scene_name, map_key):
            seen["live"] = (arena_data, scene_index, scene_name, map_key)
            return []

        with mock.patch.object(nd, "analyse", analyse), mock.patch.object(nd, "format_report", lambda report, top: ""), \
                mock.patch.object(nd, "live_ai_points", live):
            self.assertEqual(nd.main(list(argv)), 0)
        return seen

    def test_map_yard_selects_the_navmesh_mask_ai_scene_and_package(self):
        seen = self.run_main("--map", "yard", "--live-core-points")
        self.assertEqual(seen["package"], "work/extract/yard-native-v2")
        self.assertEqual((seen["assets"], seen["path_id"]), ("sharedassets281.assets", 2))
        self.assertEqual(seen["cfg"].arena_layer_mask, 0x40042A00)
        self.assertEqual(seen["live"], (nd.DEFAULT_ARENA_DATA, 320, "Arena_Yard_AI", "arena_yard"))
        self.assertFalse(seen["keep_level_borders"])

    def test_the_sidecar_reaches_the_model_through_descriptor_and_doors(self):
        seen = self.run_main("--map", "yard", "--descriptor", str(REPO / "tools" / "maps" / "yard.json"), "--doors", "--split-edge", "1.0")
        self.assertEqual(seen["barriers"], OPENED_BARRIERS)
        self.assertEqual(seen["port_layer_mask"], sum(1 << layer for layer in NAVMESH_BAKE_LAYERS))
        self.assertTrue(seen["keep_level_borders"])
        self.assertEqual(len(seen["door_rows"]), 3)           # the cradle plate and the two gate leaves
        self.assertEqual(seen["cfg"].split_edge, 1.0)

    def test_the_plan_of_another_map_with_its_own_row_is_refused_for_yard(self):
        for map_key, plan in (("yard", "autoservice"), ("autoservice", "yard"), (None, "yard")):
            argv = (["--map", map_key] if map_key else []) + ["--plan", str(REPO / "tools" / "extract" / "plans" / f"{plan}.json"),
                                                              "--live-core-points"]
            with mock.patch("sys.stderr", new_callable=io.StringIO) as err:
                try:
                    with self.assertRaises(SystemExit) as raised:
                        self.run_main(*argv)
                except ImportError:
                    self.skipTest("UnityPy is not installed")
            self.assertEqual(raised.exception.code, 2, argv)
            self.assertIn(f"use --map {plan}", err.getvalue())


# ---------------------------------------------------------------------------------------------------------------------
# 2. the extracted package
# ---------------------------------------------------------------------------------------------------------------------
def colliders():
    if "cset" not in _CACHE:
        _CACHE["cset"] = pc.load_package_colliders(PACKAGE)
    return _CACHE["cset"]


def owner_box(cset, o):
    t = cset.tris[o.tri_start:o.tri_start + o.tri_count].reshape(-1, 3)
    return t.min(axis=0), t.max(axis=0)


def decided_marks(cset, layers=None, keep=True):
    """Mark the colliders as the decided bake would (barriers with the door rows); returns the source mask for `layers`."""
    pc.mark_barriers(cset, OPENED_BARRIERS, door_rows())
    pc.mark_bake_exclusions(cset, NAVMESH_BAKE_EXCLUSIONS)
    return pc.source_owner_mask(cset, layer_mask=pc.layers_to_mask(NAVMESH_BAKE_LAYERS if layers is None else layers), keep_borders=keep)


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/yard-native-v2 is not available")
class PackageBorderTests(unittest.TestCase):
    def test_fourteen_cubes_on_layer_29_eleven_in_teamfight_and_three_on_the_gate_parts(self):
        cset = colliders()
        cubes = [o for o in cset.owners if o.layer == 29]
        self.assertEqual(len(cubes), 14)
        by_scene = {}
        for o in cubes:
            by_scene.setdefault(o.scene, []).append(o)
        self.assertEqual({k: len(v) for k, v in by_scene.items()}, {TEAMFIGHT: 11, TEAMFIGHT_DS: 3})
        self.assertTrue(all(o.path.startswith("TeamFight_LevelBorders/Cube") for o in by_scene[TEAMFIGHT]))
        self.assertEqual(sorted(o.path for o in by_scene[TEAMFIGHT_DS]), [
            "DOORS/Construction_Cradle_02/Construction_Cradle_02_A/Cube", "DOORS/Metal_gate_1/gate_door_1/Cube", "DOORS/Metal_gate_1/gate_door_2/Cube"])
        self.assertTrue(all(o.collider == "BoxCollider" for o in cubes) if hasattr(cubes[0], "collider") else True)

    def test_the_name_prefix_rule_cannot_touch_the_cubes(self):
        # BakeNavMesh's LevelBorder* name prefix: none of the 14 paths has a segment that starts with it ('TeamFight_LevelBorders' does not)
        for o in colliders().owners:
            if o.layer == 29:
                self.assertFalse(any(n.startswith("LevelBorder") for n in o.names), o.path)

    def test_the_decided_bake_keeps_the_eleven_and_drops_the_three_gate_cubes(self):
        cset = colliders()
        decided_marks(cset)
        reasons = {o.path: pc.bake_exclusion_reason(o, True) for o in cset.owners if o.layer == 29}
        self.assertEqual(sorted(p for p, r in reasons.items() if r is None).__len__(), 11)
        self.assertEqual({r for r in reasons.values() if r}, {"opened_barriers"})
        self.assertEqual(sum(1 for r in reasons.values() if r == "opened_barriers"), 3)
        # without the flag every one of the 14 is "LevelBorder layer 29"
        self.assertEqual({pc.bake_exclusion_reason(o, False) for o in cset.owners if o.layer == 29 and not o.barrier_blocked}, {"LevelBorder layer 29"})
        pc.mark_barriers(cset, [])

    def test_the_cubes_carry_no_ballistic_component_and_no_physics_material(self):
        scene = colliders().scenes[TEAMFIGHT]
        for key, o in scene.objects.items():
            if o["layer"] == 29:
                self.assertEqual(sorted(c["type"] for c in o["components"]), ["BoxCollider", "MeshFilter", "MeshRenderer"], o["name"])


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/yard-native-v2 is not available")
class PackageGateTests(unittest.TestCase):
    def find(self, scene, path):
        parts = path.split("/")
        cur = [k for k in scene.roots() if scene.objects[k]["name"] == parts[0]]
        for p in parts[1:]:
            cur = [c for k in cur for c in scene.children.get(k, []) if scene.objects[c]["name"] == p]
        return cur

    def test_each_barrier_path_names_one_gate_and_every_row_finds_its_part(self):
        scene = colliders().scenes[TEAMFIGHT_DS]
        for path in OPENED_BARRIERS:
            self.assertEqual(len(self.find(scene, path)), 1, path)
        parts = sorted((r["barrier"], r["part"]) for r in json.loads((REPO / "tools" / "maps" / "yard.doors.json").read_text(encoding="utf-8"))["poses"])
        self.assertEqual(parts, [("DOORS/Construction_Cradle_02", "Construction_Cradle_02_A"), ("DOORS/Metal_gate_1", "gate_door_1"),
                                 ("DOORS/Metal_gate_1", "gate_door_2")])
        for barrier, part in parts:
            self.assertEqual(len(self.find(scene, f"{barrier}/{part}")), 1, (barrier, part))

    def test_the_bake_drops_the_moved_parts_and_keeps_the_standing_frame(self):
        cset = colliders()
        decided_marks(cset)
        dropped = [o for o in cset.owners if o.barrier_blocked]
        self.assertEqual(len(dropped), 56)
        self.assertEqual({o.layer: sum(1 for x in dropped if x.layer == o.layer) for o in dropped}, {12: 24, 18: 25, 29: 3, 30: 4})
        # the cradle plate's own collider starts at y 7.16, 1.0 m above the yard floor: without the row the port bake closes the south pocket
        plate = [o for o in dropped if o.path.endswith("Construction_Cradle_02_A/Construction_Cradle_02_A_COLLIDER")]
        self.assertEqual(len(plate), 1)
        lo, hi = owner_box(cset, plate[0])
        self.assertAlmostEqual(float(lo[1]), 7.16, delta=0.02)
        self.assertAlmostEqual(float(hi[1]), 10.09, delta=0.02)
        # what stays: the frame collider of gate A (two posts), its HighPoly model and the upper cradle's ballistic collider (y 19..20)
        standing = sorted(o.path for o in cset.owners if o.path.startswith("DOORS") and not o.barrier_blocked and pc.bake_exclusion_reason(o, True) is None)
        self.assertEqual(standing, ["DOORS/Construction_Cradle_02/Construction_Cradle_02_B_BALLISTIC_Metalthin", "DOORS/Metal_gate_1/collider",
                                    "DOORS/Metal_gate_1/model_LOD1"])
        pc.mark_barriers(cset, [])

    def test_the_standing_frame_is_two_posts_and_leaves_4_15_m_of_clear_width(self):
        cset = colliders()
        frame = [o for o in cset.owners if o.path == "DOORS/Metal_gate_1/collider"]
        self.assertEqual((len(frame), frame[0].layer, frame[0].tri_count), (1, 18, 16))                 # two boxes of 8 triangles each
        tris = cset.tris[frame[0].tri_start:frame[0].tri_start + frame[0].tri_count].reshape(-1, 3)
        xs = sorted(set(np.round(tris[:, 0], 2).tolist()))
        self.assertEqual(xs, [-11.88, -11.79, -7.64, -7.55])           # posts of 0.09 m at both ends: the opening between them is 4.15 m
        self.assertAlmostEqual(xs[2] - xs[1], 4.15, places=2)
        self.assertAlmostEqual(float(tris[:, 1].max() - tris[:, 1].min()), 2.26, delta=0.01)
        # no lintel: the highest point of the frame is a post top, and the posts are 0.13 m deep
        self.assertAlmostEqual(float(tris[:, 2].max() - tris[:, 2].min()), 0.13, delta=0.01)

    def test_the_excavator_is_the_only_live_layer_zero_solid_in_the_play_space(self):
        cset = colliders()
        decided_marks(cset)
        zero = [o for o in cset.owners if o.layer == 0]
        self.assertEqual(len(zero), 8)
        names = sorted(o.names[-1] for o in zero)
        self.assertEqual(names.count("StreetWallLight_LOD0"), 5)
        self.assertEqual(sorted(n for n in names if n != "StreetWallLight_LOD0"), ["SPAWN_BOUND_FIGHTER", "SPAWN_BOUND_FIGHTER (1)", "colider01"])
        live = [o for o in zero if pc.bake_exclusion_reason(o, True) is None]
        self.assertEqual(len(live), 6)                                          # the SPAWN_BOUND boxes go by name (SpawnPlacementRepairs disables them)
        excavator = [o for o in live if o.names[-1] == "colider01"][0]
        lo, hi = owner_box(cset, excavator)
        np.testing.assert_allclose(np.concatenate([lo, hi]), [-18.84, 6.29, 16.15, -9.42, 12.30, 20.18], atol=0.02)
        self.assertEqual(excavator.path, "SBG_Arena_Yard/SOO/excavator/GameObject/colider01")
        self.assertEqual(sum(1 for o in zero if o.names[-1].startswith("SPAWN_BOUND")), 2)
        # the five lamps hang on walls: lowest point 8.05 m, beyond or at the west edge of the yard (x -64.7 and -75.9), above head height at the floor
        for o in live:
            if o.names[-1] == "StreetWallLight_LOD0":
                lo, hi = owner_box(cset, o)
                self.assertGreater(float(lo[1]), 8.0)
                self.assertLess(float(hi[0]), -64.0)
        pc.mark_barriers(cset, [])

    def test_the_two_spawn_bounds_are_dropped_by_name_and_cover_both_pockets(self):
        cset = colliders()
        decided_marks(cset)
        bounds = [o for o in cset.owners if o.names[-1].startswith("SPAWN_BOUND")]
        self.assertEqual(len(bounds), 2)
        self.assertEqual({pc.bake_exclusion_reason(o, True) for o in bounds}, {"SPAWN_BOUND*"})
        boxes = [owner_box(cset, o) for o in bounds]
        for side in ("south", "north"):
            # each pocket's markers lie inside one of the two boxes (x, z)
            for m in MODES["spawn_markers"][side]:
                x, _y, z = m["position"]
                self.assertTrue(any(lo[0] <= x <= hi[0] and lo[2] <= z <= hi[2] for lo, hi in boxes), m["name"])
        pc.mark_barriers(cset, [])


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/yard-native-v2 is not available")
class PackageLayerAuditTests(unittest.TestCase):
    def test_layer_census_of_the_live_colliders(self):
        cset = colliders()
        decided_marks(cset)
        census = {}
        for o in cset.owners:
            census[o.layer] = census.get(o.layer, 0) + 1
        self.assertEqual(census, {0: 8, 9: 35, 12: 4169, 13: 20, 18: 3344, 22: 15, 29: 14, 30: 139})
        pc.mark_barriers(cset, [])

    def test_the_bake_source_table_of_the_decided_configuration(self):
        cset = colliders()
        mask = decided_marks(cset)
        self.assertEqual(int(mask.sum()), 3506)
        table = {}
        for o, ok in zip(cset.owners, mask):
            if ok:
                table[o.layer] = table.get(o.layer, 0) + 1
        self.assertEqual(table, {0: 6, 9: 35, 18: 3319, 29: 11, 30: 135})      # 12 (HighPoly), 13 (triggers) and 22 are not walls for the player
        # EFT's physics matrix (globalgamemanagers of EFT 0.16.9.5 and of Arena, identical): the Player layer collides with 0, 9, 11, 18 and 29, and
        # not with 12, 22 or 30; layer 29 collides with Player only. Every layer of the list is therefore a wall for a player or a bot except 30
        # (Arena's own mask, kept: its 135 colliders are Arena's transparent walls, bullets only in EFT's matrix and bake sources in Arena's own mesh).
        pc.mark_barriers(cset, [])

    def test_no_layer_twelve_or_twentytwo_collider_is_a_bake_source(self):
        cset = colliders()
        mask = decided_marks(cset)
        for o, ok in zip(cset.owners, mask):
            if o.layer in (12, 13, 22):
                self.assertFalse(ok, o.path)
        pc.mark_barriers(cset, [])


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/yard-native-v2 is not available")
class PackageAnchorTests(unittest.TestCase):
    def test_the_bake_box_holds_every_marker_row_exit_and_the_capture_point_with_room(self):
        x0, x1, y0, y1, z0, z1 = BAKE_BOUNDS
        points = anchors()
        self.assertEqual(len(points), 45)
        for name, (x, y, z) in points.items():
            self.assertTrue(x0 + 3.0 <= x <= x1 - 3.0, name)
            self.assertTrue(z0 + 3.0 <= z <= z1 - 3.0, name)
            self.assertTrue(y0 + 1.5 <= y <= y1 - 3.0, name)


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/yard-native-v2 is not available")
class PackagePuddleTests(unittest.TestCase):
    WATER_SHADER = "Decal/Water Deferred Decal"

    @classmethod
    def setUpClass(cls):
        cls.materials = {f.stem: json.loads(f.read_text(encoding="utf-8")) for f in (PACKAGE / "materials").glob("*.json")}
        cls.users: dict = {}
        for f in sorted((PACKAGE / "scenes").glob("*.json")):
            data = json.loads(f.read_text(encoding="utf-8"))
            objs = {o["id"]: o for o in data["objects"]}

            def active(o):
                while o is not None:
                    if not o.get("active", True):
                        return False
                    o = objs.get(o.get("parent_id"))
                return True

            for o in data["objects"]:
                for c in o.get("components", []):
                    if c["type"] != "MeshRenderer":
                        continue
                    mesh = next((x for x in o["components"] if x["type"] == "MeshFilter"), {}).get("mesh_id")
                    for mid in c.get("material_ids", []):
                        cls.users.setdefault(mid, []).append((data["scene_name"], o["name"], active(o) and c.get("enabled", True), mesh))

    def test_the_water_shader_materials_of_the_package_are_exactly_the_listed_ones(self):
        water = {k for k, m in self.materials.items() if m.get("shader") == self.WATER_SHADER}
        self.assertEqual(water, set(PUDDLE_WATER_MATERIAL_NAMES))
        names = {m.get("shader") for m in self.materials.values() if m.get("shader") and ("ater" in m["shader"] or "uddle" in m["shader"].lower())}
        self.assertEqual(names, {self.WATER_SHADER})

    def test_the_rain_puddle_is_the_material_the_other_five_maps_list(self):
        material = self.materials["0877f2335044042e"]
        self.assertEqual((material["source_name"], material["keywords"]), ("puddle", ["USERAIN"]))
        users = self.users["0877f2335044042e"]
        self.assertEqual(len(users), 12)
        self.assertTrue(all(name.split(" (")[0] == "puddle2" and live and mesh == "2e93310068beccf4" for _s, name, live, mesh in users))
        self.assertEqual({s for s, _n, _l, _m in users}, {MAIN_SCENE, TEAMFIGHT})
        self.assertEqual(sum(1 for u in users if u[0] == MAIN_SCENE), 7)


# ---------------------------------------------------------------------------------------------------------------------
# 3. the installed Arena
# ---------------------------------------------------------------------------------------------------------------------
def arena_mesh():
    if "mesh" not in _CACHE:
        _CACHE["mesh"], _CACHE["meta"] = nd.load_arena_mesh(nd.DEFAULT_ARENA_DATA, YARD.assets, YARD.path_id)
    return _CACHE["mesh"], _CACHE["meta"]


@unittest.skipUnless(HAVE_ARENA, "no checked Arena build is installed (the installed build must start with one of " + ", ".join(PINNED_ARENA_BUILD) + ")")
class ArenaMeshTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")
        cls.mesh, cls.meta = arena_mesh()

    def test_the_placement_is_the_identity_so_the_oracle_needs_no_yaw(self):
        self.assertEqual((self.meta["position"], self.meta["rotation"]), ([0.0, 0.0, 0.0], [-0.0, -0.0, -0.0, 1.0]))
        self.assertTrue(self.mesh.placement.is_identity)
        settings = self.meta["settings"]
        self.assertAlmostEqual(settings["agentRadius"], 0.3, places=6)
        self.assertAlmostEqual(settings["agentHeight"], 1.7, places=6)
        self.assertAlmostEqual(settings["agentSlope"], 48.0, places=3)
        self.assertAlmostEqual(settings["agentClimb"], 0.38, places=6)
        self.assertAlmostEqual(settings["cellSize"], 0.07, places=6)

    def test_polygons_triangles_vertices_and_tiles(self):
        self.assertEqual(self.mesh.poly_count, 1911)
        self.assertEqual(int((self.mesh.poly_nverts - 2).sum()), 3187)
        self.assertEqual(len(self.mesh.verts), 3407)
        self.assertEqual((len(self.mesh.tiles), self.meta["tiles_total"]), (59, 66))

    def test_eleven_components_the_yard_and_ten_islands_nine_of_them_roofs(self):
        import navmesh_oracle as no
        components = no.component_summary(self.mesh)
        self.assertEqual([c["polygons"] for c in components], [1669, 52, 84, 3, 17, 46, 17, 4, 9, 8, 2])
        main = components[0]
        self.assertAlmostEqual(main["area_xz"], 8064.5, delta=0.5)
        for got, want in zip(main["min"] + main["max"], [-66.43, 6.15, -19.11, 20.86, 10.70, 154.70]):
            self.assertAlmostEqual(got, want, delta=0.06)
        roofs = [c for c in components[1:] if c["min"][1] > 20.0]
        self.assertEqual(len(roofs), 9)                                     # y 21.0 to 29.3: the roofs of the buildings (622 m2 the largest)
        self.assertAlmostEqual(max(c["area_xz"] for c in roofs), 622.3, delta=0.5)
        # the tenth island: 2 polygons, 7.2 m2, y 10.67..10.70, x -15.33..-14.49, z 71.26..79.87 = the top edge of the transformer substation
        # (SBG_Arena_Yard/SOO_LOD0/.../Transformer_substation_01_COLLIDER, a layer-18 box x -20.56..-9.56, z 70.08..81.08, y 5.71..10.72)
        small = components[-1]
        self.assertEqual(small["polygons"], 2)
        self.assertAlmostEqual(small["area_xz"], 7.2, delta=0.2)
        self.assertTrue(10.6 < small["min"][1] and small["max"][1] < 10.75)
        # every roof island lies above the decided bake box, the strip does not and is a bake source of the box (the box top is 14)
        for c in roofs:
            self.assertGreater(c["min"][1], BAKE_BOUNDS[3])

    def test_the_surface_decoded_from_level320_is_the_table_row(self):
        import probe_mode_toggler as probe
        surfaces = probe.scene_census(nd.DEFAULT_ARENA_DATA, YARD.ai_scene_index)["surfaces"]
        self.assertEqual(len(surfaces), 1)
        self.assertEqual(surfaces[0]["layer_mask"], YARD.layer_mask)
        self.assertEqual(surfaces[0]["layer_mask_bits"], ARENA_LAYERS)
        self.assertEqual(surfaces[0]["navmesh_data"]["path_id"], YARD.path_id)
        self.assertEqual(surfaces[0]["navmesh_data"]["file_id"], 4)          # externals of level320: file 4 = sharedassets281.assets
        self.assertEqual(surfaces[0]["use_geometry"], "PhysicsColliders")
        self.assertEqual(surfaces[0]["collect_objects"], "Volume")
        np.testing.assert_allclose(surfaces[0]["center"], [-22.8, 12.0, 71.0], atol=1e-4)
        np.testing.assert_allclose(surfaces[0]["size"], [87.2, 121.99, 180.1], atol=1e-2)
        # the rounded volume: x -66.4..20.8, z -19.0..161.1 against the decided box
        x0, x1, _y0, _y1, z0, z1 = BAKE_BOUNDS
        self.assertLessEqual(x0, -22.8 - 87.2 / 2 + 0.01)
        self.assertGreaterEqual(x1, -22.8 + 87.2 / 2 - 0.01)
        self.assertGreaterEqual(z1, 71.0 + 180.1 / 2 - 1.0)

    def test_the_ai_points_lie_on_the_main_component(self):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            points = nd.live_ai_points(nd.DEFAULT_ARENA_DATA, YARD.ai_scene_index, YARD.ai_scene_name, YARD.key)
        kinds = {k: sum(1 for p in points if p["kind"] == k) for k in ("core_point", "patrol_hub", "patrol_point")}
        self.assertEqual(kinds, {"core_point": 7, "patrol_hub": 4, "patrol_point": 48})
        pos = np.array([p["position"] for p in points])
        poly, surf = self.mesh.locate(pos, max_dy=1.5)
        self.assertTrue((poly >= 0).all() and (self.mesh.labels[poly] == 0).all())
        self.assertLess(float(np.abs(surf - pos[:, 1]).max()), 0.2)

    def test_the_markers_rows_exits_and_the_capture_point_stand_on_the_main_component(self):
        pos = np.array(list(anchors().values()))
        poly, surf = self.mesh.locate(pos, max_dy=1.5)
        self.assertTrue((poly >= 0).all() and (self.mesh.labels[poly] == 0).all())
        self.assertLess(float(np.abs(surf - pos[:, 1]).max()), 0.3)        # measured 0.28 (the marker boxes stand a little off Arena's floor)

    def test_the_mesh_runs_through_both_gate_openings(self):
        # Arena baked with both gates open. North: along the closed leaves' line (x -11.6..-7.8, z 127.83) 7 of 9 points lie on the main component, all
        # at the floor (6.15); south: along the cradle plate's footprint (x -23.3..-18.7, z 16.7, y 7.3) 7 of 9, the slot surface at 6.92..6.98
        # (the plate's underside is 7.16: the gap is 0.2 m above the mesh, which is why the cradle needs its row).
        poly, surf = self.mesh.locate(np.array([(x, 6.3, 127.83) for x in np.linspace(-11.6, -7.8, 9)]), max_dy=1.0)
        self.assertEqual(int((poly >= 0).sum()), 7)
        np.testing.assert_allclose(surf[poly >= 0], 6.15, atol=0.01)
        poly, surf = self.mesh.locate(np.array([(x, 7.3, 16.7) for x in np.linspace(-23.3, -18.7, 9)]), max_dy=1.0)
        self.assertEqual(int((poly >= 0).sum()), 7)
        self.assertTrue(((surf[poly >= 0] > 6.9) | (surf[poly >= 0] < 6.4)).all())
        self.assertAlmostEqual(float(surf[poly >= 0].max()), 6.98, delta=0.02)
        self.assertLess(float(surf[poly >= 0].max()), 7.16)

    def test_the_excavator_stands_over_21_square_metres_of_arenas_walkable_floor(self):
        # layer 0 is invisible to Arena's mask: 21.0 m2 of Arena's main component lie inside the footprint of the excavator's collider (x -18.84..-9.42,
        # z 16.15..20.18; the 37.9 m2 box is partly over the wall line and the eroded edges); 279 of those samples (11.2 m2) are blocked for the port
        # agent in the decided run (OracleTests)
        import navmesh_oracle as no
        pts, polys = no.sample_polygons(self.mesh, 0.2, origin=(0.05, 0.05))
        main = self.mesh.labels[polys] == 0
        sel = main & (pts[:, 0] >= -18.84) & (pts[:, 0] <= -9.42) & (pts[:, 2] >= 16.15) & (pts[:, 2] <= 20.18) & (pts[:, 1] < 12.3)
        self.assertAlmostEqual(float(sel.sum()) * 0.04, 21.0, delta=1.0)


# ---------------------------------------------------------------------------------------------------------------------
# 4. the oracle runs (navmesh_diff.analyse with the decided configuration)
# ---------------------------------------------------------------------------------------------------------------------
def oracle_points():
    if "points" not in _CACHE:
        pts = [{"name": n, "kind": "capture_point" if n == "capture" else "probe", "position": list(p)} for n, p in anchors().items()]
        _CACHE["points"] = pts
    return _CACHE["points"]


def oracle(variant):
    """One analyse() run, cached. 'decided' (both gates with their rows, layers 0/9/11/13/18/29/30, kept cubes, split 1.0), 'arena_mask' (the
    same without layers 0 and 29: Arena's own mask), 'closed' (no opened barrier, the leaves stay), 'unsplit' (decided with split_edge 0: the
    model's artifact at the Respawn building's facade)."""
    if variant not in _CACHE:
        mesh, _meta = arena_mesh()
        layers = {"decided": NAVMESH_BAKE_LAYERS, "arena_mask": ARENA_LAYERS, "closed": NAVMESH_BAKE_LAYERS, "unsplit": NAVMESH_BAKE_LAYERS}[variant]
        barriers = [] if variant == "closed" else OPENED_BARRIERS
        cfg = nd.Config(arena_layer_mask=YARD.layer_mask, split_edge=0.0 if variant == "unsplit" else 1.0)
        rep = nd.analyse(str(PACKAGE), cfg, barriers, oracle_points(), nd.DEFAULT_ARENA_DATA, mesh=mesh, cset=colliders(),
                         port_layer_mask=pc.layers_to_mask(layers), keep_level_borders=variant != "arena_mask",
                         door_rows=None if variant == "closed" else door_rows(), log=lambda *a: None, greedy_steps=4)
        _CACHE[variant] = rep
        pc.mark_barriers(colliders(), [])
    return _CACHE[variant]


def route_lengths(rep, pairs, passable_everywhere=False):
    """Dijkstra metres (4-neighbour lattice of 0.2 m: a Manhattan-like length, comparable between runs) on the model's passable samples."""
    graph, v = rep["_graph"], rep["_verdicts"]["port"]
    passable = np.ones(graph.n, dtype=bool) if passable_everywhere else ~v.blocked & ~v.unsupported
    sample = {p["name"]: p["sample"] for p in rep["probes"]}
    out = {}
    for a, b in pairs:
        dist = np.full(graph.n, np.inf)
        dist[sample[a]] = 0.0
        heap = [(0.0, sample[a])]
        while heap:
            d, i = heapq.heappop(heap)
            if d > dist[i]:
                continue
            for j in graph.neighbors[i]:
                if passable[j] and d + 0.2 < dist[j]:
                    dist[j] = d + 0.2
                    heapq.heappush(heap, (d + 0.2, j))
        out[f"{a}>{b}"] = round(float(dist[sample[b]]), 1) if np.isfinite(dist[sample[b]]) else None
    return out


EXIT_SOUTH, EXIT_NORTH = EXITS[0]["name"], EXITS[1]["name"]
ROUTES = [("Usec_house_(8)", "capture"), ("Bear_house_(8)", "capture"), ("Usec_house_(8)", "Bear_house_(8)"), (EXIT_SOUTH, EXIT_NORTH),
          (EXIT_SOUTH, "capture"), (EXIT_NORTH, "capture")]


def corridor(rep, x0, x1, z0, z1):
    pts, v = rep["_pts"], rep["_verdicts"]["port"]
    ok = ~v.blocked & ~v.unsupported
    sel = (pts[:, 0] >= x0) & (pts[:, 0] <= x1) & (pts[:, 2] >= z0) & (pts[:, 2] <= z1) & (pts[:, 1] < 12)
    return int(sel.sum()), int(ok[sel].sum())


@unittest.skipUnless(HAVE_ARENA and HAVE_PACKAGE, "the installed Arena 0.6.2 or the extracted package is not available")
class OracleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")

    def test_the_decided_configuration_joins_both_pockets_to_the_yard_and_the_capture_point(self):
        rep = oracle("decided")
        self.assertEqual(rep["port"]["sources"], 3506)
        self.assertEqual(rep["port"]["exclusion_reasons"], {"SPAWN_BOUND*": 2, "Triggers layer 13": 20, "not in navmesh_bake_layers": 4160,
                                                            "opened_barriers": 56, "source": 3506})
        s = rep["samples"]
        self.assertEqual((s["count"], s["passable_port"], s["unsupported"]), (201647, 200800, 86))
        # Arena's agent (0.28 / 1.6) meets 651 blocked samples (844 without the slack, 372 on Arena's own layers only): the props Arena walks through
        # and, with layer 0, the excavator; the port agent 811. The yard is ONE component of 4,381 m2 (Arena's main component is 8,064.5 m2 and holds
        # the yard, both pockets and the outer ring); the ring is the second component, 3,426 m2.
        self.assertEqual((s["blocked_arena_agent"], s["blocked_arena_agent_no_slack"], s["blocked_arena_agent_arena_layers"]), (651, 844, 372))
        self.assertEqual(s["blocked_port_agent"], 811)
        self.assertEqual(rep["port_component_count"], 13)
        areas = [c["area_m2"] for c in rep["port_components"]]
        self.assertAlmostEqual(areas[0], 4381.3, delta=0.5)
        self.assertAlmostEqual(areas[1], 3426.3, delta=0.5)
        reach = rep["reachability"]
        self.assertEqual((reach["probes"], reach["located"], reach["in_anchor_component"]), (45, 45, 45))
        # both pockets (20 markers, the 4 Player rows, both exits) and the capture point are in the yard component
        self.assertEqual([o.split(":", 1)[1] for o in reach["outside"]], list(RING_ROWS))
        self.assertEqual(rep["config"]["door_rows"], 3)

    def test_no_row_stands_in_the_outer_ring(self):
        # the two fill rows of the first draft stood in the ring component (1, 3,426 m2) although they were on Arena's main component; since their
        # move of 2026-11-21 every probe is in the yard component of the port model
        rep = oracle("decided")
        status = {p["name"]: p["model_component"] for p in rep["probes"]}
        self.assertEqual(sorted(n for n, c in status.items() if c != 0), sorted(RING_ROWS))
        self.assertEqual(RING_ROWS, ())
        for probe in rep["probes"]:
            self.assertEqual(probe["arena_component"], 0, probe["name"])    # and on Arena's main component

    def test_the_greedy_list_is_empty_and_no_bake_exclusion_is_needed(self):
        # every probe is in the yard component, so the greedy search has nothing to join and proposes no exclusion. While two fill rows stood in the
        # ring (first draft, until 2026-11-21) it named ONE: the closed-box hull of the background building Bulding_515_3_Arena_Background on the east
        # edge. Arena's mesh crosses two of its walls (x 9.5..9.8 and z 38.9) because the hull is not in Arena's own bake; for players it is a wall
        # (layer 18), and the ring is not play space, so the port keeps it a bake source. The hull is still the largest thing Arena's agent walks through
        # (test_arena_walks_through_small_things_and_none_is_a_route).
        rep = oracle("decided")
        self.assertEqual(rep["greedy"]["steps"], [])
        self.assertEqual(rep["greedy"]["probes_joined"], 45)

    def test_arena_walks_through_small_things_and_none_is_a_route(self):
        rep = oracle("decided")
        clusters = rep["clusters_arena_agent"]
        mask_only = oracle("arena_mask")["clusters_arena_agent"]
        # Arena's agent (0.28 / 1.6) meets 39 prop instances on Arena's own layers (26.8 m2 together): boards, scaffolds, barricades, the hull of a
        # background building (the largest, 135 samples = 5.4 m2) and so on. The greedy list says that only the building hull joins the ring (below).
        self.assertEqual(len(mask_only), 39)
        names = {c["path"].rsplit("/", 1)[-1] for c in mask_only}
        self.assertIn("Bulding_515_3_Arena_Background", names)
        self.assertTrue({"wood_board01_long (9)", "plywood_board_1", "Barricade_Fanera_01_(ARENA) (1)"} <= names)
        self.assertAlmostEqual(sum(c["area_m2"] for c in mask_only), 26.8, delta=0.2)
        self.assertLess(max(c["area_m2"] for c in mask_only if not c["path"].endswith("Bulding_515_3_Arena_Background")), 4.0)
        # the decided run adds three: the excavator (layer 0, 279 samples = 11.2 m2) and the two west blocks of the kept cubes, TeamFight_LevelBorders
        # Cube (87) and Cube (90) (68 and 25 samples) that stand over Arena's floor at x -54.8..-52.4, z 52.2..69.4; the port agent was blocked there
        # already (its blocked count rises by one sample for all eleven cubes)
        extra = {c["path"]: c for c in clusters if c["path"] not in {m["path"] for m in mask_only}}
        self.assertEqual(sorted(extra), ["SBG_Arena_Yard/SOO/excavator", "TeamFight_LevelBorders/Cube (87)", "TeamFight_LevelBorders/Cube (90)"])
        self.assertEqual([extra[k]["sample_count"] for k in sorted(extra)], [279, 68, 25])
        self.assertEqual(extra["SBG_Arena_Yard/SOO/excavator"]["layers"], {0: 1})
        self.assertEqual(len(clusters), 42)
        self.assertAlmostEqual(sum(c["area_m2"] for c in clusters), 41.7, delta=0.2)

    def test_the_door_rows_and_the_standing_frame_leave_both_doorways_open(self):
        rep = oracle("decided")
        # every Arena sample of the north corridor between the posts (x -11.95..-7.5, z 126..130) is passable for the port agent with the frame
        # standing; of the 222 samples of the cradle gap 214 are (the east edge, 0.3 m beside the excavator, is the 8 that are not; 221 without layer 0)
        self.assertEqual(corridor(rep, -11.95, -7.5, 126.0, 130.0), (348, 348))
        self.assertEqual(corridor(rep, -23.6, -18.2, 15.5, 18.5), (222, 214))
        self.assertEqual(corridor(oracle("arena_mask"), -23.6, -18.2, 15.5, 18.5), (222, 221))

    def test_the_pockets_are_sealed_without_the_gate_rows_and_open_with_them(self):
        closed = oracle("closed")
        self.assertEqual(closed["port"]["sources"], 3538)                  # the 32 colliders of the gate parts (three of them cubes) are bake sources again
        reach = closed["reachability"]
        self.assertEqual(reach["in_anchor_component"], 19)                 # 45 probes less the 26 of the two pockets (20 markers, 4 Player rows, 2 exits)
        out = {o.split(":", 1)[1] for o in reach["outside"]}
        self.assertEqual({n for n in out if n.startswith(("Usec_house", "Bear_house"))}, {f"Usec_house_({i})" for i in range(5, 15)} | {f"Bear_house_({i})" for i in range(5, 15)})
        self.assertTrue({EXIT_SOUTH, EXIT_NORTH} <= out)
        self.assertEqual(corridor(closed, -11.95, -7.5, 126.0, 130.0), (348, 299))        # the leaves close 49 samples of the corridor
        self.assertEqual(corridor(closed, -23.6, -18.2, 15.5, 18.5), (222, 124))          # the plate closes 98 of the cradle gap
        self.assertIsNone(route_lengths(closed, [("Usec_house_(8)", "capture")])["Usec_house_(8)>capture"])
        self.assertIsNone(route_lengths(closed, [("Bear_house_(8)", "capture")])["Bear_house_(8)>capture"])

    def test_the_unsplit_model_closes_the_cradle_gap_with_a_facade_sliver_that_the_split_removes(self):
        # one 75 m x 20 m near-vertical triangle of Respawn_Outdoor_COLLIDER fills whole columns of walk_model (its y interval per cell is the
        # triangle's whole y range): 12 samples of the gap, which seals the south pocket in the unsplit model. Unity's voxelizer does not do that.
        rep = oracle("unsplit")
        self.assertEqual(rep["reachability"]["in_anchor_component"], 32)        # 45 less the 13 of the south pocket (10 markers, 2 Player rows, 1 exit)
        self.assertEqual(corridor(rep, -23.6, -18.2, 15.5, 18.5), (222, 202))
        self.assertEqual([g["path"].rsplit("/", 1)[-1] for g in rep["greedy"]["steps"]], ["Respawn_Outdoor"])     # the facade sliver alone; the hull of the background building was the second step while two rows stood in the ring
        self.assertIsNone(route_lengths(rep, [("Usec_house_(8)", "capture")])["Usec_house_(8)>capture"])

    def test_the_routes_of_the_port_model_equal_those_on_arenas_own_mesh_within_four_metres(self):
        rep = oracle("decided")
        port = route_lengths(rep, ROUTES)
        arena = route_lengths(rep, ROUTES, passable_everywhere=True)
        self.assertTrue(all(v is not None for v in port.values()), port)
        for key in port:
            self.assertLessEqual(port[key] - arena[key], 4.0, key)
            self.assertGreaterEqual(port[key], arena[key] - 0.01, key)
        # R6: no literal exit coordinate; the lengths below move with a nudged exit, the bar is 1.5 x the straight line + 10 m
        points = anchors()
        for key, length in port.items():
            a, b = key.split(">")
            straight = math.dist(points[a], points[b])
            self.assertLessEqual(length, 1.5 * straight + 10.0, f"{key}: {length} m for a straight line of {straight:.1f} m")
        self.assertEqual(port["Usec_house_(8)>capture"], 97.2)
        self.assertEqual(port["Bear_house_(8)>capture"], 116.6)
        self.assertEqual(arena["Usec_house_(8)>capture"], 96.8)

    def test_the_eleven_kept_cubes_cut_one_sample_so_keeping_them_is_bake_neutral(self):
        kept, mask_only = oracle("decided"), oracle("arena_mask")
        # arena_mask differs from decided in layer 0 (the excavator) and layer 29 (the 11 cubes): the cubes alone add 1 blocked sample (variants run,
        # layers + 29 only, 507 -> 508); here the sum of both is what the decided configuration blocks over Arena's mask
        self.assertEqual(mask_only["samples"]["blocked_port_agent"], 507)
        self.assertEqual(kept["samples"]["blocked_port_agent"], 811)
        self.assertEqual(kept["port"]["sources"] - mask_only["port"]["sources"], 17)       # six layer-0 colliders and the eleven cubes
        self.assertEqual(mask_only["reachability"]["in_anchor_component"], kept["reachability"]["in_anchor_component"])
        self.assertAlmostEqual(mask_only["port_components"][0]["area_m2"] - kept["port_components"][0]["area_m2"], 1.2, delta=0.5)


# ---------------------------------------------------------------------------------------------------------------------
# 5. the region model: the port bake over the whole box, and Arena's 118 border cubes
# ---------------------------------------------------------------------------------------------------------------------
# What the two questions of the item need is not "is Arena's footprint passable" (OracleTests) but "where could the port's agent stand in the whole
# bake box, which of it is connected, and what do the cubes take away": a standing point is the top of every upward floor run of walk_model's column
# grid (cell 0.1 m, y step 0.1 m, triangles bisected to 1 m), blocked when its body (r 0.2 after the 0.1 m slack, from +0.53 to +1.7) meets a solid,
# connected to a 4-neighbour at |dy| <= 0.45. It is an approximation of Recast (no region merging, no simplification) and the cubes enter as solids
# only (their tops are no floor); the model was calibrated against the real bakes in docs/progress/airpit-release.md. About 60 s and 1.5 GB.
AGENT_RADIUS, AGENT_SLACK, AGENT_HEIGHT, FLOOR_MARGIN, SPLIT = 0.30, 0.10, 1.70, 0.15, 1.0


def _components(n, u, v):
    """Union-find by min-label propagation with pointer jumping (no scipy): the component index of every node."""
    label = np.arange(n, dtype=np.int64)
    for _ in range(300):
        m = np.minimum(label[u], label[v])
        new = label.copy()
        np.minimum.at(new, u, m)
        np.minimum.at(new, v, m)
        for _j in range(6):
            new = new[new]
        if np.array_equal(new, label):
            break
        label = new
    return np.unique(label, return_inverse=True)[1]


def _box_triangles(center, size, rot):
    qx, qy, qz, qw = rot
    u = np.array([qx, qy, qz])
    corners = []
    for x in (-1, 1):
        for y in (-1, 1):
            for z in (-1, 1):
                c = np.array([x, y, z], dtype=float) * 0.5 * np.array(size)
                t = 2 * np.cross(u, c)
                corners.append(np.array(center) + c + qw * t + np.cross(u, t))
    idx = [(0, 1, 3), (0, 3, 2), (4, 6, 7), (4, 7, 5), (0, 4, 5), (0, 5, 1), (2, 3, 7), (2, 7, 6), (0, 2, 6), (0, 6, 4), (1, 5, 7), (1, 7, 3)]
    return np.array([[corners[a], corners[b], corners[c]] for a, b, c in idx])


class RegionModel:
    """The port bake's walkable standing points over an xz box up to `ytop`, with the decided bake sources (door rows, layers, kept cubes)."""

    def __init__(self, box, ytop, kept_cubes=True):
        import walk_model as wm
        self.wm = wm
        self.agent = wm.AgentParams(AGENT_RADIUS, AGENT_HEIGHT, 0.38, 48.0)
        cset = colliders()
        # kept_cubes False: the decided bake without layer 29, i.e. Arena's own surface (plus layer 0) with none of the cubes: the baseline for "what do the cubes take"
        mask = decided_marks(cset, layers=NAVMESH_BAKE_LAYERS if kept_cubes else [l for l in NAVMESH_BAKE_LAYERS if l != 29], keep=kept_cubes)
        pc.mark_barriers(cset, [])
        x0, x1, z0, z1 = box
        self.box, self.ytop = box, ytop
        self.grid = g = wm.make_grid(x0 - 1.5, x1 + 1.5, z0 - 1.5, z1 + 1.5, 4.0, ytop + AGENT_HEIGHT, 0.1, 0.1)
        tris, owner = nd._sources(cset, mask, nd.Config(cell=0.1, split_edge=SPLIT), g)
        keep = (tris[:, :, 1].max(axis=1) >= 4.0 - 0.4) & (tris[:, :, 1].min(axis=1) <= ytop + AGENT_HEIGHT + 0.1)
        self.solid, self.floor, _n = wm.build_occupancy(tris[keep], owner[keep], g, self.agent)
        self.dil = wm.dilate_disc(self.solid, g, AGENT_RADIUS - AGENT_SLACK)
        nxt = self.floor >> np.uint64(1)
        nxt[:, :-1] |= (self.floor[:, 1:] & np.uint64(1)) << np.uint64(63)
        tops = self.floor & ~nxt
        cells, ys = [], []
        for w in range(g.words):
            idx = np.nonzero(tops[:, w])[0]
            if not len(idx):
                continue
            bits = np.unpackbits(tops[idx, w].view(np.uint8).reshape(-1, 8), axis=1, bitorder="little")
            r, b = np.nonzero(bits)
            cells.append(idx[r])
            ys.append(g.y0 + (64 * w + b) * g.dy + 0.5 * g.dy)
        cell, y = np.concatenate(cells), np.concatenate(ys)
        order = np.argsort(cell, kind="stable")
        self.cell, self.y = cell[order], y[order]
        self.ix, self.iz = self.cell % g.nx, self.cell // g.nx
        self.x = g.x0 + (self.ix + 0.5) * g.cs
        self.z = g.z0 + (self.iz + 0.5) * g.cs
        self.inbox = (self.x >= x0) & (self.x <= x1) & (self.z >= z0) & (self.z <= z1) & (self.y <= ytop)
        count = np.bincount(self.cell, minlength=g.cells)
        self.start, self.count = np.concatenate([[0], np.cumsum(count)[:-1]]), count
        body = wm.column_masks(self.y, g, self.agent, FLOOR_MARGIN)
        self.blocked = ((self.dil[self.cell] & body) != 0).any(axis=1)
        self.ok = self.inbox & ~self.blocked
        self.edges = self._edges(self.ok)

    def _edges(self, ok):
        g = self.grid
        us, vs = [], []
        for dcell, valid in ((1, self.ix + 1 < g.nx), (g.nx, self.iz + 1 < g.nz)):
            nb = np.where(valid, self.cell + dcell, 0)
            for t in range(int(self.count.max())):
                has = valid & (self.count[nb] > t)
                j = np.where(has, self.start[nb] + t, 0)
                good = has & ok & ok[j] & (np.abs(self.y - self.y[j]) <= 0.45)
                us.append(np.nonzero(good)[0])
                vs.append(j[good])
        return np.concatenate(us), np.concatenate(vs)

    def components(self, ok):
        u, v = self.edges
        keep = ok[u] & ok[v]
        lab = _components(len(self.y), u[keep], v[keep])
        comp = np.full(len(self.y), -1, dtype=np.int64)
        idx = np.nonzero(ok)[0]
        uniq, inv, counts = np.unique(lab[idx], return_inverse=True, return_counts=True)
        order = np.argsort(-counts, kind="stable")
        rank = np.empty(len(uniq), dtype=np.int64)
        rank[order] = np.arange(len(uniq))
        comp[idx] = rank[inv]
        return comp, counts[order]

    def node_near(self, x, y, z, rad=1.0, dy=1.5):
        sel = np.nonzero(self.inbox & (np.abs(self.x - x) <= rad) & (np.abs(self.z - z) <= rad) & (np.abs(self.y - y) <= dy))[0]
        d = np.hypot(self.x[sel] - x, self.z[sel] - z) + 0.2 * np.abs(self.y[sel] - y)
        return int(sel[np.argmin(d)])

    def blocked_by(self, triangles):
        """Indices of the in-box standing points whose body meets a solid made of `triangles` (rasterised in a window around them)."""
        wm, g = self.wm, self.grid
        # bisected like the sources of the model: a big face of a box would otherwise fill columns the real face does not touch (Config.split_edge)
        triangles = nd.split_long_triangles(triangles, np.zeros(len(triangles), dtype=np.int32), SPLIT)[0]
        lo, hi = triangles.reshape(-1, 3).min(axis=0), triangles.reshape(-1, 3).max(axis=0)
        ix0 = max(0, int(np.floor((lo[0] - 1.0 - g.x0) / g.cs)))
        ix1 = min(g.nx, int(np.ceil((hi[0] + 1.0 - g.x0) / g.cs)))
        iz0 = max(0, int(np.floor((lo[2] - 1.0 - g.z0) / g.cs)))
        iz1 = min(g.nz, int(np.ceil((hi[2] + 1.0 - g.z0) / g.cs)))
        if ix1 <= ix0 or iz1 <= iz0:
            return np.zeros(0, dtype=np.int64)
        wg = wm.Grid(g.x0 + ix0 * g.cs, g.z0 + iz0 * g.cs, g.cs, ix1 - ix0, iz1 - iz0, g.y0, g.dy, g.nbits)
        solid, _f, _n = wm.build_occupancy(triangles, np.zeros(len(triangles), dtype=np.int32), wg, self.agent, None, np.zeros(len(triangles), dtype=bool))
        dil = wm.dilate_disc(solid, wg, AGENT_RADIUS - AGENT_SLACK)
        sel = np.nonzero(self.inbox & (self.ix >= ix0) & (self.ix < ix1) & (self.iz >= iz0) & (self.iz < iz1))[0]
        cell = (self.iz[sel] - iz0) * wg.nx + (self.ix[sel] - ix0)
        body = wm.column_masks(self.y[sel], wg, self.agent, FLOOR_MARGIN)
        return sel[((dil[cell] & body) != 0).any(axis=1)]


def region_model(ytop, kept_cubes=True):
    key = ("region", ytop, kept_cubes)
    if key not in _CACHE:
        _CACHE[key] = RegionModel((BAKE_BOUNDS[0], BAKE_BOUNDS[1], BAKE_BOUNDS[4], BAKE_BOUNDS[5]), ytop, kept_cubes)
    return _CACHE[key]


def border_rows():
    return json.loads(BORDERS.read_text(encoding="utf-8"))["rows"]


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/yard-native-v2 is not available")
class BorderSidecarTests(unittest.TestCase):
    """tools/maps/yard.borders.json (written by extract_level_borders.py): the 104 cubes of Arena's Scripts scene, tracked data only."""

    def test_104_rows_all_live_solid_boxes_on_layer_29(self):
        data = json.loads(BORDERS.read_text(encoding="utf-8"))
        self.assertEqual((data["key"], data["root"], data["count"], len(data["rows"])), ("arena_yard", "Arena_Yard_LevelBorders", 104, 104))
        for r in data["rows"]:
            self.assertEqual((r["layer"], r["active"], r["enabled"], r["is_trigger"]), (29, True, True, False), r["path"])
        self.assertEqual(len({r["path"] for r in data["rows"]}), 104)
        self.assertGreater(min(min(r["size"]) for r in data["rows"]), 0.12)           # no degenerate box: the thinnest is 0.1225 m
        self.assertEqual(sum(1 for r in data["rows"] if any(abs(v) > 1e-4 for v in r["rotation"][:3])), 95)    # 95 of 104 are turned out of axis

    def test_together_with_the_package_cubes_they_are_arenas_118(self):
        cset = colliders()
        package = [o for o in cset.owners if o.layer == 29]
        self.assertEqual(len(border_rows()) + len(package), 118)
        # the 104 do not duplicate a package cube: no row's AABB equals a package cube's within 0.05 m
        boxes = [owner_box(cset, o) for o in package]
        for r in border_rows():
            lo, hi = np.array(r["aabb"][0]), np.array(r["aabb"][1])
            for blo, bhi in boxes:
                self.assertFalse(np.allclose(lo, blo, atol=0.05) and np.allclose(hi, bhi, atol=0.05), r["path"])

    def test_the_rows_lie_inside_the_bake_box_footprint_and_the_two_west_of_it_float_over_the_alley(self):
        rows = border_rows()
        x0, _x1, _y0, _y1, z0, z1 = BAKE_BOUNDS
        lo = np.min([r["aabb"][0] for r in rows], axis=0)
        hi = np.max([r["aabb"][1] for r in rows], axis=0)
        np.testing.assert_allclose([lo[0], hi[0], lo[2], hi[2]], [-71.69, 10.6, -0.21, 151.2], atol=0.05)
        west = [r for r in rows if r["aabb"][0][0] < x0]
        self.assertEqual([r["path"] for r in west], ["Arena_Yard_LevelBorders/Cube", "Arena_Yard_LevelBorders/Cube (1)"])    # x -71.7..-71.0, west of the box
        # both float over the dead-end alley west of the yard (x -76..-67, z 52..92, floor 6.15..6.95): y 15.79..20.33 and 9.13..13.67, so a player
        # on the floor (head at 8.65) walks under them; they bound what he can climb or vault over, not the ground
        self.assertAlmostEqual(west[0]["aabb"][0][1], 15.79, delta=0.02)
        self.assertAlmostEqual(west[0]["aabb"][1][1], 20.33, delta=0.02)
        self.assertAlmostEqual(west[1]["aabb"][0][1], 9.13, delta=0.02)
        self.assertGreater(west[1]["aabb"][0][1], 6.95 + 1.7)
        self.assertTrue(z0 < lo[2] and hi[2] < z1)

    def test_heights_follow_three_classes(self):
        rows = border_rows()
        ground = [r for r in rows if r["aabb"][0][1] <= 7.0]
        mid = [r for r in rows if 7.0 < r["aabb"][0][1] < 12.0]
        high = [r for r in rows if r["aabb"][0][1] >= 12.0]
        self.assertEqual((len(ground), len(mid), len(high)), (69, 34, 1))


@unittest.skipUnless(HAVE_ARENA and HAVE_PACKAGE, "the installed Arena 0.6.2 or the extracted package is not available")
class BorderOnArenasMeshTests(unittest.TestCase):
    """The cheap half of the measurement, on Arena's own mesh only: how much of its walkable floor lies under the cubes."""

    @classmethod
    def setUpClass(cls):
        try:
            import navmesh_oracle as no
        except ImportError:
            raise unittest.SkipTest("numpy or UnityPy missing")
        cls.mesh, _ = arena_mesh()
        pts, polys = no.sample_polygons(cls.mesh, 0.2, origin=(0.05, 0.05))
        cls.pts = pts[cls.mesh.labels[polys] == 0]

    def under(self, lo, hi, pad=0.3):
        """Samples of Arena's main component within `pad` of an AABB whose y range meets the agent's body slab [y + 0.53, y + 1.7]."""
        p = self.pts
        sel = (p[:, 0] >= lo[0] - pad) & (p[:, 0] <= hi[0] + pad) & (p[:, 2] >= lo[2] - pad) & (p[:, 2] <= hi[2] + pad)
        sel &= (p[:, 1] + 0.53 <= hi[1]) & (p[:, 1] + 1.7 >= lo[1])
        return p[sel]

    def under_row(self, row, pad=0.2):
        """Samples of Arena's main component whose body (four points from +0.53 to +1.7) is inside the row's turned box widened by `pad` (the port
        agent's radius after the slack)."""
        qx, qy, qz, qw = row["rotation"]
        rot = np.array([[1 - 2 * qy * qy - 2 * qz * qz, 2 * qx * qy - 2 * qz * qw, 2 * qx * qz + 2 * qy * qw],
                        [2 * qx * qy + 2 * qz * qw, 1 - 2 * qx * qx - 2 * qz * qz, 2 * qy * qz - 2 * qx * qw],
                        [2 * qx * qz - 2 * qy * qw, 2 * qy * qz + 2 * qx * qw, 1 - 2 * qx * qx - 2 * qy * qy]])
        half = np.array(row["size"]) / 2 + pad
        hit = np.zeros(len(self.pts), dtype=bool)
        for h in (0.53, 0.9, 1.3, 1.7):
            local = (self.pts + np.array([0.0, h, 0.0]) - np.array(row["center"])) @ rot            # rot.T applied to the row vectors
            hit |= (np.abs(local) <= half).all(axis=1)
        return self.pts[hit]

    def test_the_104_rows_stand_over_little_of_arenas_walkable_floor(self):
        counts = [len(self.under_row(r)) for r in border_rows()]
        # 0.2 m lattice samples of Arena's mesh (4 cm2 each) within the turned box widened by 0.2 m: 10 of the 104 rows meet some, 26.1 m2 together,
        # and 20.1 of those are ONE row, sidecar row 5 'Cube (102)': the closing wall south of the team pocket (x -17.5..-6.3, z -0.2..2.0, y 6.75..10.73)
        # that stands over the south band = the outer ring, which is no part of the yard component (RegionModelTests). Row 93 'Cube (88)' is a 1 m slab
        # at head height (y 7.54..8.54) over a ramp at x -54.8..-52.4, z 77..84 (3.9 m2), row 10 'Cube (107)' stands at the east edge (1.2 m2).
        self.assertEqual(sum(1 for c in counts if c), 10)
        self.assertAlmostEqual(sum(counts) * 0.04, 26.1, delta=0.5)
        self.assertEqual(counts.index(max(counts)), 5)
        self.assertAlmostEqual(max(counts) * 0.04, 20.1, delta=0.2)
        self.assertEqual(sorted(i for i, c in enumerate(counts) if c * 0.04 > 1.0), [5, 10, 93])

    def test_the_eleven_kept_package_cubes_stand_over_a_few_square_metres_of_it(self):
        cset = colliders()
        cubes = [o for o in cset.owners if o.layer == 29 and o.scene == TEAMFIGHT]
        self.assertEqual(len(cubes), 11)
        by_name = {o.names[-1]: len(self.under(*owner_box(cset, o))) * 0.04 for o in cubes}
        # the two west blocks are the ones that touch Arena's floor (the oracle lists them as 68 and 25 blocked samples of Arena's agent)
        self.assertEqual(sorted(k for k, v in by_name.items() if v > 3.0), ["Cube (87)", "Cube (90)"])


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/yard-native-v2 is not available")
class RegionModelTests(unittest.TestCase):
    """The port bake over the whole decided box (about 60 s and 2 GB for the models, cached for the tests of this class)."""

    def test_the_yard_is_one_component_of_4966_m2_and_every_anchor_is_in_it(self):
        rm = region_model(BAKE_BOUNDS[3])
        comp, counts = rm.components(rm.ok)
        # the decided bake (the 11 kept cubes are sources): the yard, then the outer ring, 2,085 m2 at z -1 (3,740 m2 at z -20), then the walled-off
        # courtyards north and west of the yard (1,078, 877, 684, ... m2)
        self.assertAlmostEqual(counts[0] * 0.01, 4965.9, delta=1.0)
        self.assertAlmostEqual(counts[1] * 0.01, 2085.0, delta=1.0)
        yard = comp == 0
        self.assertAlmostEqual(float(rm.y[yard].min()), 5.75, delta=0.06)
        self.assertAlmostEqual(float(rm.y[yard].max()), 10.65, delta=0.06)               # the decks; nothing walkable above, up to the box top
        self.assertAlmostEqual(float(rm.z[yard].min()), 2.2, delta=0.15)                 # the south pocket's south end: the box cut at z -1 loses none of it
        outside = [name for name, (x, y, z) in anchors().items() if comp[rm.node_near(x, y, z)] != 0]
        self.assertEqual(sorted(outside), sorted(RING_ROWS))

    def test_nothing_above_the_decks_joins_the_yard_whatever_the_ceiling(self):
        # the same model with the ceiling at 36 m: the yard component is the same 4,966 m2 and y 5.75..10.65; above the box top lie roofs and scaffolds,
        # all of them islands: the largest (1,368 m2) is a roof at y 26..29, and 243 more islands lie wholly above 20 m
        low, high = region_model(BAKE_BOUNDS[3]), region_model(36.0)
        _c, n_low = low.components(low.ok)
        c_high, n_high = high.components(high.ok)
        self.assertAlmostEqual(n_high[0] * 0.01, n_low[0] * 0.01, delta=0.5)
        yard = c_high == 0
        self.assertLess(float(high.y[yard].max()), 10.7)
        self.assertAlmostEqual(n_high[2] * 0.01, 1367.8, delta=1.0)
        self.assertGreater(float(high.y[c_high == 2].min()), 20.0)
        islands = [k for k in np.unique(c_high[c_high >= 0]) if high.y[c_high == k].min() > 20.0]
        self.assertGreaterEqual(len(islands), 200)
        self.assertNotIn(0, islands)

    def test_the_kept_cubes_add_a_little_standing_room_on_their_tops(self):
        # with the 11 TeamFight cubes as bake sources their tops are floor: 2.9 m2 more than without (the west blocks at y 8.54 beside the ramp)
        kept, none = region_model(BAKE_BOUNDS[3]), region_model(BAKE_BOUNDS[3], kept_cubes=False)
        self.assertAlmostEqual(none.components(none.ok)[1][0] * 0.01, 4963.0, delta=1.0)
        self.assertAlmostEqual(kept.components(kept.ok)[1][0] * 0.01 - none.components(none.ok)[1][0] * 0.01, 2.9, delta=0.6)

    def test_the_cubes_take_under_5_m2_of_the_yard_and_cut_nothing_off(self):
        rm = region_model(BAKE_BOUNDS[3], kept_cubes=False)          # Arena's surface plus layer 0, no cube at all: the cubes enter as solids
        cset = colliders()
        n_block = np.zeros(len(rm.y), dtype=np.int16)
        per_row = []
        for r in border_rows():
            idx = rm.blocked_by(_box_triangles(r["center"], r["size"], r["rotation"]))
            n_block[idx] += 1
            per_row.append(idx)
        per_package = {o.path: rm.blocked_by(cset.tris[o.tri_start:o.tri_start + o.tri_count]) for o in cset.owners if o.layer == 29}
        comp0, counts0 = rm.components(rm.ok)
        yard0 = comp0 == 0
        gate_cubes = [p for p in per_package if p.startswith("DOORS/")]
        self.assertEqual(len(gate_cubes), 3)

        def cut(n):
            comp, counts = rm.components(rm.ok & (n == 0))
            keep = comp == comp[rm.node_near(*CAPTURE_POINT)]
            return float((yard0 & (n > 0)).sum()) * 0.01, float((yard0 & ~keep & (n == 0)).sum()) * 0.01, float(counts[comp[rm.node_near(*CAPTURE_POINT)]]) * 0.01
        # (a) the 104 rows alone: 4.1 m2 of standing room go, nothing is cut off
        blocked, cut_off, remaining = cut(n_block)
        self.assertAlmostEqual(counts0[0] * 0.01, 4963.0, delta=1.0)
        self.assertAlmostEqual(blocked, 4.08, delta=0.2)
        self.assertEqual(cut_off, 0.0)
        self.assertAlmostEqual(counts0[0] * 0.01 - remaining, blocked, delta=0.05)
        # (b) with the 11 kept cubes too: 4.8 m2, nothing cut off
        n_kept = n_block.copy()
        for p, idx in per_package.items():
            if p not in gate_cubes:
                n_kept[idx] += 1
        blocked, cut_off, _remaining = cut(n_kept)
        self.assertAlmostEqual(blocked, 4.79, delta=0.2)
        self.assertEqual(cut_off, 0.0)
        # row by row: 38 of the 104 rows meet standing room of the yard, at the edges only: 1.0 m2 at the most (sidecar row 56, 'Cube (5)'), then 0.7
        # (row 3) and 0.36 (row 13); of the 11 kept cubes five do, 0.33 m2 at the most
        areas = [float(yard0[idx].sum()) * 0.01 for idx in per_row]
        self.assertEqual(sum(1 for a in areas if a > 0), SIDECAR_ROWS_TOUCHING)
        self.assertEqual(sorted(range(len(areas)), key=lambda i: -areas[i])[:3], [56, 3, 13])
        self.assertAlmostEqual(areas[56], 1.00, delta=0.05)
        pk = {p: float(yard0[idx].sum()) * 0.01 for p, idx in per_package.items()}
        self.assertEqual(sum(1 for p, a in pk.items() if a > 0 and p not in gate_cubes), 5)
        self.assertEqual(max((p for p in pk if p not in gate_cubes), key=pk.get), "TeamFight_LevelBorders/Cube (87)")
        self.assertAlmostEqual(pk["TeamFight_LevelBorders/Cube (87)"], 0.33, delta=0.05)
        # (c) with the three gate cubes at their CLOSED pose the same set seals what lies behind the gates: 321 m2 cut off, which a plain raid never
        # has (the door rows park the gates open and the bake drops the cubes with their parts)
        n_all = n_kept.copy()
        for p in gate_cubes:
            n_all[per_package[p]] += 1
        blocked, cut_off, _remaining = cut(n_all)
        self.assertAlmostEqual(cut_off, 321.18, delta=1.0)
        self.assertAlmostEqual(blocked, 12.81, delta=0.4)


if __name__ == "__main__":
    unittest.main()
