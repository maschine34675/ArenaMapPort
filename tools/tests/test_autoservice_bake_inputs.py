"""Autoservice NavMesh oracle row and bake inputs (docs/PLAN-AUTOSERVICE.md item 8): opened_barriers, start_gates, held_gates,
navmesh_bake_layers, keep_level_borders, bake_bounds, navmesh_bake_exclusions, uses_server_time and the puddle materials.

Why: the Unity bake is a 15-minute batch run (item 13), so everything checkable offline is pinned here, like test_iceberg_bake_inputs.py
did for Iceberg. The DECIDED_* constants below are the values the item-8 commit wrote into tools/maps/autoservice.json and into the
AutoService entry of ArenaMapPort.Shared/ArenaMapDescriptor.cs, where SidecarTests and the .NET mirror tests (MapDescriptorConsistencyTests)
hold them equal; every number has its evidence in docs/progress/autoservice-release.md ("Phase B item 8"). Autoservice is a Catalog-only map
until item 12, so none of this reaches a release. Groups, each skipped when its input is absent:

  * constants and code pins (repository only): the validators accept the decided values, ARENA_SOURCES["autoservice"] is the measured row;
  * the sidecar: tools/maps/autoservice.json carries every decided key, equal to the constants, and none of the omitted ones;
  * the extracted package work/extract/autoservice-native-v2 (override the work directory with ARENAMAPPORT_WORK_DIR when the tests run from
    a git worktree without work/): the 372 border cubes, the gates and their frames, the layer census, the layer-0 audit, the markers
    against the bounds, the puddle materials;
  * the installed Arena (0.6.2.0.48473, read-only, UnityPy): the identity placement and the measured mesh of Arena's NavMeshData, the
    markers and AI points on it, the gates it runs through, the second island, and the oracle runs themselves (navmesh_diff.analyse with the
    decided configuration, about 15 s each) with Dijkstra routes on the modelled mesh and a fresh-bake surface model of what the bake would
    make walkable above Arena's mesh.
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
import walk_model as wm  # noqa: E402

_CACHE: dict = {}
PACKAGE = WORK / "extract" / "autoservice-native-v2"
MAIN_SCENE = "Arena_AutoService"
DESIGN_STUFF = "Arena_AutoService_DesignStuff"                 # the 372 border cubes
TEAMFIGHT = "Arena_AutoService_TeamFight_DesignStuff"          # DOORS, SPAWNS, CAPTURE_POINT, SCAV_SPECTATORS
HAVE_PACKAGE = (PACKAGE / "scenes" / f"{TEAMFIGHT}.json").is_file()
ARENA_DATA = Path(nd.DEFAULT_ARENA_DATA)
# The Arena-dependent pins were measured on 0.6.2.0.48473 (2026-11-11). A newer build may legitimately differ, so those tests skip with their
# reason instead of turning red until its build is checked and added here (0.6.1 has the same level63 / sharedassets63 sizes, the key-source
# rows say so, but the mesh itself was not measured there).
PINNED_ARENA_BUILD = ("0.6.2.",)
HAVE_ARENA = (ARENA_DATA / "sharedassets63.assets").is_file() and (ARENA_DATA / "level63").is_file() \
    and door_poses.arena_version(ARENA_DATA).startswith(PINNED_ARENA_BUILD)
AUTOSERVICE = nd.ARENA_SOURCES["autoservice"]

# ---------------------------------------------------------------------------------------------------------------------
# The decided values (2026-11-11), sidecar spelling. The C# descriptor names are in the comments.
# ---------------------------------------------------------------------------------------------------------------------
# The four gates of the TeamFight DesignStuff scene's DOORS, as unit paths: the strings the rows of autoservice.doors.json carry (a row
# matches its barrier by the EXACT string, BakeNavMesh.ResolveOpenedBarriers). Each path names exactly one gate in the scene. Descriptor:
# OpenedBarriers, StartGates. Arena baked its mesh with all four open: the mesh crosses each pocket wall only at the gate openings.
OPENED_BARRIERS = ["DOORS/big_gate_metal", "DOORS/equator_roller_shutters_01", "DOORS/roller_shutters_techlight_01",
                   "DOORS/roller_shutters_techlight_01 (1)"]
START_GATES = list(OPENED_BARRIERS)
HELD_GATES: list = []                                       # HeldGates: nothing stays closed after the release (Arena authors no held gate here)
# Arena's NavMeshSurface mask 0x40042A00 (layers 9/11/13/18/30) plus 29: the 372 layer-29 cubes of Arena_AutoService_LevelBorder are the
# play space's only authored bound and stay active at runtime, so the bake treats them as walls (Bowl's and Iceberg's policy).
ARENA_LAYERS = [9, 11, 13, 18, 30]
NAVMESH_BAKE_LAYERS = [9, 11, 13, 18, 29, 30]               # NavMeshBakeLayers
KEEP_LEVEL_BORDERS = True                                   # KeepLevelBorders
# The AABB of the 372 cubes (x -413.15..-332.55, z 465.67..512.99) plus 1.55 to 2.0 m. y from 1.2 m below Arena's lowest floor (-3.82) to 2.0:
# Arena's highest floor is -0.43 (+1.7 m agent = 1.27); the ground component of the surface model is the same for every ceiling from 1.5 to
# 14 (FreshBakeTests), the ceiling only decides how many unreachable islands (tribune decks, roofs, cube tops) the bake carries.
BAKE_BOUNDS = [-415, -331, -5, 2, 464, 515]                 # BakeBounds
NAVMESH_BAKE_EXCLUSIONS: list = []                          # NavMeshBakeExclusions: none (no under-floor slab, the y floor keeps the rest out)
USES_SERVER_TIME = True                                     # UsesServerTime (user, 2026-11-08: "serverzeit ok")
# Water decals: the rain-driven 'puddle' (42 puddle2 quads, the material id of Equator, Air Pit, Bowl and Iceberg). The wet mold atlas
# (four renderer slots on inactive Heating_register_01_Decals LODs) is not a puddle anyone can step in.
PUDDLE_WATER_MATERIAL_NAMES = ["0877f2335044042e"]          # PuddleWaterMaterialNames
PUDDLE_EXTRA_SURFACES: list = []                            # PuddleExtraSurfaces: none, Concrete is 74 percent of the samples under the quads
PUDDLE_EXCLUDED = {"aa54d9752ed43430": "'arena_autoservice_mold_decal_atlas_01_wet': four slots on two inactive Heating_register_01_Decals LODs"}

DECIDED_SIDECAR_KEYS = {
    "opened_barriers": OPENED_BARRIERS,
    "start_gates": START_GATES,
    "held_gates": HELD_GATES,
    "navmesh_bake_layers": NAVMESH_BAKE_LAYERS,
    "keep_level_borders": KEEP_LEVEL_BORDERS,
    "bake_bounds": BAKE_BOUNDS,
    "navmesh_bake_exclusions": NAVMESH_BAKE_EXCLUSIONS,
    "uses_server_time": USES_SERVER_TIME,
    "puddle_water_material_names": PUDDLE_WATER_MATERIAL_NAMES,
}
# Keys the sidecar must NOT carry, and why: no extra puddle surface (Concrete suffices), no shader fallback row WHEN ITEM 8 WROTE THE FILE (item 11
# decided four names after the shader census and item 12 wrote them: tools/tests/test_autoservice_light.py pins the list), no authored_borders (the ExcludeLevelBorders polygon is a culling outline, not the movement border), no emission cap
# (the strongest emissive material is 3.88), no native water (the map has no water surface: decision "No native water").
OMITTED_SIDECAR_KEYS = ("puddle_extra_surfaces", "authored_borders", "emission_cap", "emission_floor", "has_native_water")

# Anchors outside the package markers. The capture point is Arena's CapturePointEntity 33001; the two exits are read from the exits sidecar
# (since 2026-11-12 they stand INSIDE the team pockets, rule R3; until then this file kept a copy of the 2026-11-11 drafts, which stood in the yard
# outside the gates). R6: no literal copy, so that a nudge of an exit touches only the sidecar and the descriptor.
CAPTURE_POINT = (-369.29, -3.80, 499.49)
EXIT_DRAFTS = {"exit_east": tuple(json.loads((REPO / "tools" / "maps" / "autoservice.exits.json").read_text(encoding="utf-8"))["exits"][0]["position"]),
               "exit_west": tuple(json.loads((REPO / "tools" / "maps" / "autoservice.exits.json").read_text(encoding="utf-8"))["exits"][1]["position"])}


def sidecar(name="autoservice.json"):
    return json.loads((REPO / "tools" / "maps" / name).read_text(encoding="utf-8"))


def door_rows():
    """The rows the bake reads (ArenaPaths.DoorRows) from tools/maps/autoservice.doors.json: the sidecar next to the descriptor."""
    if "rows" not in _CACHE:
        _CACHE["rows"] = pc.load_door_rows(REPO / "tools" / "maps" / "autoservice.doors.json", "arena_autoservice")
    return _CACHE["rows"]


# ---------------------------------------------------------------------------------------------------------------------
# 1. constants and code pins
# ---------------------------------------------------------------------------------------------------------------------
class DecidedValuesTests(unittest.TestCase):
    def decided(self):
        return {k: v for k, v in DECIDED_SIDECAR_KEYS.items()}

    def test_the_validators_accept_the_decided_values(self):
        self.assertEqual(VD.bake_input_errors(self.decided()), [])
        self.assertEqual(VD.puddle_errors({"puddle_water_material_names": PUDDLE_WATER_MATERIAL_NAMES,
                                           "puddle_extra_surfaces": PUDDLE_EXTRA_SURFACES}), [])

    def test_every_gate_is_a_start_gate_and_nothing_is_held(self):
        self.assertEqual(START_GATES, OPENED_BARRIERS)
        self.assertEqual(HELD_GATES, [])
        self.assertEqual(len(set(OPENED_BARRIERS)), 4)

    def test_the_barriers_are_exactly_the_motion_barriers_of_the_doors_sidecar(self):
        # The exact-string match of BakeNavMesh.ResolveOpenedBarriers and of the client. The sidecar also carries the four lamp switches of
        # DOORS (LIGHT_1..4, RED off / GREEN on) as switch rows of the group barrier "DOORS"; a lamp group is no opened barrier
        # (validate_map_descriptors.door_lamp_groups), so listing DOORS would drop the whole group below it with the legacy rule.
        rows = json.loads((REPO / "tools" / "maps" / "autoservice.doors.json").read_text(encoding="utf-8"))["poses"]
        motion = sorted({p["barrier"] for p in rows if "switch" not in p})
        lamps = [p for p in rows if "switch" in p]
        self.assertEqual(motion, sorted(OPENED_BARRIERS))
        self.assertEqual({p["barrier"] for p in lamps}, {"DOORS"})
        self.assertEqual(len(lamps), 4)
        self.assertNotIn("DOORS", OPENED_BARRIERS)

    def test_the_layer_list_is_arenas_surface_mask_plus_the_border_layer(self):
        self.assertEqual(sorted(ARENA_LAYERS + [29]), NAVMESH_BAKE_LAYERS)
        self.assertEqual(sum(1 << layer for layer in ARENA_LAYERS), AUTOSERVICE.layer_mask)
        self.assertEqual(AUTOSERVICE.layer_mask, 0x40042A00)
        self.assertEqual(AUTOSERVICE.layer_mask, nd.AIRPIT_ARENA_LAYER_MASK)       # Arena's AI scenes of AirPit, Iceberg and Autoservice share it
        self.assertIn(29, NAVMESH_BAKE_LAYERS)                                      # keep_level_borders without 29 is rejected
        self.assertNotIn(4, NAVMESH_BAKE_LAYERS)
        self.assertNotIn(0, NAVMESH_BAKE_LAYERS)        # layer 0 stays out: the one live layer-0 solid in the play space adds no blocked sample (OracleTests)
        self.assertTrue(KEEP_LEVEL_BORDERS)

    def test_keep_level_borders_without_layer_29_is_rejected(self):
        bad = dict(self.decided(), navmesh_bake_layers=ARENA_LAYERS)
        self.assertTrue(any("layer 29" in e for e in VD.bake_input_errors(bad)))

    def test_the_bake_box_is_ordered_and_sits_around_arenas_floors(self):
        x0, x1, y0, y1, z0, z1 = BAKE_BOUNDS
        self.assertTrue(x0 < x1 and y0 < y1 and z0 < z1)
        self.assertLessEqual(y0, -3.82 - 1.0)             # at least 1 m below Arena's lowest floor (-3.82)
        self.assertGreaterEqual(y1, -0.43 + 1.7)          # Arena's highest floor plus the agent
        self.assertLessEqual(y1, 2.0)                     # the tribune decks (y 4.4+), the roofs and most cube tops stay out
        # inside Arena's own NavMeshSurface volume (x -421.2..-319.4, z 446.0..527.65): the box is narrower than the volume on purpose
        self.assertGreaterEqual(x0, -421.2)
        self.assertLessEqual(x1, -319.4)
        self.assertGreaterEqual(z0, 446.0)
        self.assertLessEqual(z1, 527.65)

    def test_no_bake_exclusion_is_needed_and_none_overlaps_a_barrier(self):
        self.assertEqual(NAVMESH_BAKE_EXCLUSIONS, [])
        for path in NAVMESH_BAKE_EXCLUSIONS:
            self.assertFalse(any(path == b or path.startswith(b + "/") for b in OPENED_BARRIERS), path)

    def test_the_water_materials_are_distinct_and_the_mold_atlas_is_not_listed(self):
        self.assertEqual(len(set(PUDDLE_WATER_MATERIAL_NAMES)), len(PUDDLE_WATER_MATERIAL_NAMES))
        self.assertFalse(set(PUDDLE_EXCLUDED) & set(PUDDLE_WATER_MATERIAL_NAMES))
        self.assertEqual(PUDDLE_EXTRA_SURFACES, [])


class SidecarTests(unittest.TestCase):
    def test_the_sidecar_carries_every_decided_value_and_they_are_equal(self):
        data = sidecar()
        self.assertEqual([k for k in DECIDED_SIDECAR_KEYS if k not in data], [], "the item-8 commit wrote every decided key at once")
        for key, expected in DECIDED_SIDECAR_KEYS.items():
            self.assertEqual(data[key], expected, key)
        for key in OMITTED_SIDECAR_KEYS:
            self.assertNotIn(key, data, key)
        self.assertEqual(VD.bake_input_errors(data), [])
        self.assertEqual(data["key"], "arena_autoservice")

    def test_the_other_sidecars_keep_their_bake_inputs(self):
        # a regression guard: this commit touches no other map's inputs
        bowl, iceberg = sidecar("bowl.json"), sidecar("iceberg.json")
        self.assertIs(bowl["keep_level_borders"], True)
        self.assertEqual(bowl["navmesh_bake_layers"], [9, 11, 18, 29, 30])
        self.assertEqual(iceberg["bake_bounds"], [-62, 121, -3, 8, -131, -33])
        self.assertEqual(iceberg["navmesh_bake_layers"], [9, 11, 13, 18, 29, 30])
        for stem in ("equator", "bay5", "airpit"):
            self.assertNotIn("keep_level_borders", sidecar(f"{stem}.json"), stem)


class ArenaSourceRowTests(unittest.TestCase):
    def test_the_row_is_the_measured_one(self):
        self.assertEqual((AUTOSERVICE.key, AUTOSERVICE.assets, AUTOSERVICE.path_id), ("arena_autoservice", "sharedassets63.assets", 2))
        self.assertEqual((AUTOSERVICE.ai_scene_index, AUTOSERVICE.ai_scene_name), (63, "Arena_AutoService_AI"))
        self.assertEqual(AUTOSERVICE.layer_mask, 0x40042A00)
        self.assertEqual(AUTOSERVICE.package, "work/extract/autoservice-native-v2")

    def test_the_ai_scene_is_the_ai_role_of_the_source_plan(self):
        try:
            derived = nd.arena_source_from_plan(REPO / "tools" / "extract" / "plans" / "autoservice.json", AUTOSERVICE)
        except ImportError:
            self.skipTest("UnityPy is not installed")
        self.assertEqual(derived, AUTOSERVICE)
        plan = json.loads((REPO / "tools" / "extract" / "plans" / "autoservice.json").read_text(encoding="utf-8"))
        self.assertEqual([(s["index"], s["name"]) for s in plan["navmesh_scenes"]], [(63, "Arena_AutoService_AI")])

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

    def test_map_autoservice_selects_the_navmesh_mask_ai_scene_and_package(self):
        seen = self.run_main("--map", "autoservice", "--live-core-points")
        self.assertEqual(seen["package"], "work/extract/autoservice-native-v2")
        self.assertEqual((seen["assets"], seen["path_id"]), ("sharedassets63.assets", 2))
        self.assertEqual(seen["cfg"].arena_layer_mask, 0x40042A00)
        self.assertEqual(seen["live"], (nd.DEFAULT_ARENA_DATA, 63, "Arena_AutoService_AI", "arena_autoservice"))
        self.assertFalse(seen["keep_level_borders"])

    def test_the_sidecar_reaches_the_model_through_descriptor_and_doors(self):
        seen = self.run_main("--map", "autoservice", "--descriptor", str(REPO / "tools" / "maps" / "autoservice.json"), "--doors")
        self.assertEqual(seen["barriers"], OPENED_BARRIERS)
        self.assertEqual(seen["port_layer_mask"], sum(1 << layer for layer in NAVMESH_BAKE_LAYERS))
        self.assertTrue(seen["keep_level_borders"])
        self.assertEqual(len(seen["door_rows"]), 23)          # 1 + 6 + 6 + 6 motion rows of the four gates and the 4 lamp switches

    def test_the_plan_of_another_map_with_its_own_row_is_refused_for_autoservice(self):
        for map_key, plan in (("autoservice", "iceberg"), ("iceberg", "autoservice"), (None, "autoservice")):
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


def border_cubes(cset):
    return [o for o in cset.owners if o.layer == 29 and o.names[-1].startswith("Cube")]


def package_markers():
    """(name, world position) of the 20 SpawnPointMarker boxes 'Usec house' of the TeamFight DesignStuff scene (parent SPAWNS)."""
    cset = colliders()
    scene = cset.scenes[TEAMFIGHT]
    out = []
    for key, o in scene.objects.items():
        if o["name"].startswith("Usec house") and o.get("parent_id") is not None and scene.objects[o["parent_id"]]["name"] == "SPAWNS":
            w = scene.world(key)
            out.append((o["name"], (float(w[0, 3]), float(w[1, 3]), float(w[2, 3]))))
    return out


def marker_index(name):
    return -1 if name == "Usec house" else int(name[len("Usec house ("):-1])


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/autoservice-native-v2 is not available (set ARENAMAPPORT_WORK_DIR)")
class PackageBorderTests(unittest.TestCase):
    def test_372_cubes_on_layer_29_and_one_roof_cap(self):
        cset = colliders()
        cubes = border_cubes(cset)
        self.assertEqual(len(cubes), 372)
        others = [o for o in cset.owners if o.layer == 29 and o not in cubes]
        for o in cubes:
            self.assertEqual((o.scene, o.kind, o.enabled, o.trigger, o.active), (DESIGN_STUFF, "BoxCollider", True, False, True))
            self.assertEqual(o.names[-2], "Arena_AutoService_LevelBorder")
        # the single 'levelBoarder' collider of a warehouse prop (plan: noted, not a cube): a flat solid cap at y -0.5 over the south-east
        # warehouse, kept as a bake source because layer 29 is in the list; its top is a 96 m2 island (FreshBakeTests), never a route
        self.assertEqual([o.names[-3:] for o in others], [("Spawn_Road", "Village_WareHouse_01", "levelBoarder")])
        lo, hi = owner_box(cset, others[0])
        np.testing.assert_allclose(list(lo) + list(hi), [-344.04, -0.96, 465.07, -338.68, -0.50, 474.64], atol=0.02)

    def test_the_cube_aabb_is_inside_the_bake_box_with_a_margin(self):
        cset = colliders()
        boxes = [owner_box(cset, o) for o in border_cubes(cset)]
        lo = np.min([b[0] for b in boxes], axis=0)
        hi = np.max([b[1] for b in boxes], axis=0)
        np.testing.assert_allclose([lo[0], hi[0], lo[2], hi[2]], [-413.15, -332.55, 465.67, 512.99], atol=0.01)
        np.testing.assert_allclose([lo[1], hi[1]], [-5.85, 9.49], atol=0.01)
        x0, x1, _y0, _y1, z0, z1 = BAKE_BOUNDS
        self.assertGreaterEqual(lo[0] - x0, 1.8)
        self.assertGreaterEqual(x1 - hi[0], 1.5)
        self.assertGreaterEqual(lo[2] - z0, 1.6)
        self.assertGreaterEqual(z1 - hi[2], 2.0)

    def test_the_name_prefix_rule_cannot_touch_the_cubes_or_anything_else(self):
        # BakeNavMesh.RoundOpenedReason drops names starting with LevelBorder unless keep_level_borders; here the group is 'Arena_AutoService_LevelBorder'
        # and the cubes are 'Cube (N)' (Bowl's shape), so nothing in the package starts with the prefix and the exemption changes nothing.
        cset = colliders()
        self.assertEqual([o.index for o in cset.owners if any(n.startswith(pc.LEVEL_BORDER_PREFIX) for n in o.names)], [])
        cubes = border_cubes(cset)
        self.assertEqual({pc.bake_exclusion_reason(o) for o in cubes}, {"LevelBorder layer 29"})
        self.assertEqual({pc.bake_exclusion_reason(o, keep_borders=True) for o in cubes}, {None})
        kept = pc.source_owner_mask(cset, layer_mask=pc.layers_to_mask(NAVMESH_BAKE_LAYERS), keep_borders=True)
        self.assertEqual(sum(1 for o in cubes if kept[o.index]), 372)

    def test_every_cube_has_an_upward_face_and_the_ceiling_cuts_most_of_the_tops(self):
        cset = colliders()
        cos48 = math.cos(math.radians(48.0))
        projected = 0.0
        tops = []
        for o in border_cubes(cset):
            t = cset.tris[o.tri_start:o.tri_start + o.tri_count]
            n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
            up = n[:, 1] / np.maximum(np.linalg.norm(n, axis=1), 1e-12) >= cos48
            if up.any():
                projected += float((0.5 * np.abs(n[up][:, 1])).sum())
                tops.append(float(t[up][:, :, 1].mean()))
        tops = np.array(tops)
        self.assertEqual(len(tops), 372)
        self.assertAlmostEqual(projected, 1094.9, delta=0.5)
        self.assertAlmostEqual(float(tops.min()), -3.44, delta=0.01)
        self.assertAlmostEqual(float(tops.max()), 9.49, delta=0.01)
        # 94 cube tops lie at or below Arena's highest floor (-0.43): low curbs and covers inside the play space; 236 at or below the ceiling (2.0)
        self.assertEqual((int((tops <= -0.43).sum()), int((tops <= BAKE_BOUNDS[3]).sum())), (94, 236))


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/autoservice-native-v2 is not available (set ARENAMAPPORT_WORK_DIR)")
class PackageGateTests(unittest.TestCase):
    def test_each_barrier_path_names_one_gate_and_every_row_finds_its_part(self):
        cset = colliders()
        found = {p: pc.find_all_instances(cset.scenes, p) for p in OPENED_BARRIERS}
        self.assertEqual({p: len(v) for p, v in found.items()}, {p: 1 for p in OPENED_BARRIERS})
        self.assertTrue(all(s == TEAMFIGHT for v in found.values() for s, _k in v))
        legacy, moved, unresolved, posed = pc.resolve_opened_barriers(cset.scenes, OPENED_BARRIERS, door_rows())
        self.assertEqual((legacy, unresolved, posed), ([], [], 4))
        self.assertEqual(len(moved), 19)

    def census(self, owners):
        out: dict = {}
        for o in owners:
            key = (o.names[1], o.names[-1], o.layer)
            out[key] = out.get(key, 0) + 1
        return dict(sorted(out.items()))

    def test_the_bake_drops_nine_moved_colliders_and_keeps_eight_standing_ones(self):
        cset = colliders()
        try:
            pc.mark_barriers(cset, OPENED_BARRIERS, door_rows())
            under = [o for o in cset.owners if o.names[:1] == ("DOORS",) and len(o.names) > 2]
            dropped, kept = [o for o in under if o.barrier_blocked], [o for o in under if not o.barrier_blocked]
            self.assertEqual((len(under), len(dropped), len(kept)), (17, 9, 8))
            self.assertEqual(sum(1 for o in cset.owners if o.barrier_blocked), 9)          # nothing outside the gates is dropped
            self.assertEqual(self.census(dropped), {
                ("big_gate_metal", "Vishel_Plane", 0): 1, ("big_gate_metal", "colider 1", 18): 1, ("big_gate_metal", "gate 1", 12): 1,
                ("equator_roller_shutters_01", "roller_shutters_01_BALLISTIC_metalthin", 12): 1,
                ("equator_roller_shutters_01", "roller_shutters_01_COLLIDER", 18): 1,
                ("roller_shutters_techlight_01", "roller_shutters_techlight_01_BALLISTIC_metalthin", 12): 1,
                ("roller_shutters_techlight_01", "roller_shutters_techlight_01_COLLIDER", 18): 1,
                ("roller_shutters_techlight_01 (1)", "roller_shutters_techlight_01_BALLISTIC_metalthin", 12): 1,
                ("roller_shutters_techlight_01 (1)", "roller_shutters_techlight_01_COLLIDER", 18): 1})
            self.assertEqual(self.census(kept), {
                ("big_gate_metal", "colider", 18): 1, ("big_gate_metal", "gate_rail_lod", 12): 1,
                ("equator_roller_shutters_01", "roller_shutters_frame_01_COLLIDER", 18): 1,
                ("equator_roller_shutters_01", "roller_shutters_frame_BALLISTIC_metalthin", 12): 1,
                ("roller_shutters_techlight_01", "roller_shutters_frame_techlight_01_BALLISTIC_metalthin", 12): 1,
                ("roller_shutters_techlight_01", "roller_shutters_frame_techlight_01_COLLIDER", 18): 1,
                ("roller_shutters_techlight_01 (1)", "roller_shutters_frame_techlight_01_BALLISTIC_metalthin", 12): 1,
                ("roller_shutters_techlight_01 (1)", "roller_shutters_frame_techlight_01_COLLIDER", 18): 1})
            # the legacy rule (a path without rows) would also drop the big gate's header rail 'colider': rows keep it standing
            pc.mark_barriers(cset, OPENED_BARRIERS)
            legacy_dropped = {o.names[-1] for o in cset.owners if o.names[:1] == ("DOORS",) and o.barrier_blocked}
            self.assertIn("colider", legacy_dropped)
            self.assertNotIn("roller_shutters_frame_01_COLLIDER", legacy_dropped)       # 'frame' parts survive the legacy rule too
        finally:
            pc.mark_barriers(cset, [])

    def standing(self, scene_path_part, name):
        cset = colliders()
        o = next(o for o in cset.owners if o.layer == 18 and o.names[:1] == ("DOORS",) and o.names[1] == scene_path_part and o.names[-1] == name)
        t = cset.tris[o.tri_start:o.tri_start + o.tri_count].reshape(-1, 3)
        return o, t

    def test_the_standing_frames_leave_every_roller_doorway_open_and_the_big_gate_rail_hangs_above_head_height(self):
        # Measured from the vertices of the frame colliders (non-convex MeshColliders of 20 triangles: two posts and a header):
        #  * equator_roller_shutters_01: posts 0.04 m thick at z 505.89 and 507.73, so 1.80 m between them; the header's lower edge is y -1.75;
        #  * roller_shutters_techlight_01 and (1): posts 0.07 m thick, 4.56 m between them, header lower edge y 0.62;
        #  * big_gate_metal: no frame at all; its static 'colider' is a 9.4 x 0.12 x 0.1 m rail at y 0.85..0.97 above the doorway.
        # The floor under the gates is -3.78 (-3.74 at the big gate), so the clear heights are 2.0 m (Equator's shutter) and 4.4 / 4.6 m.
        for name, z0, z1, post, free, header_bottom in (("equator_roller_shutters_01", 505.89, 507.78, 0.04, 1.80, -1.75),
                                                       ("roller_shutters_techlight_01", 498.87, 503.57, 0.07, 4.56, 0.62),
                                                       ("roller_shutters_techlight_01 (1)", 475.16, 479.86, 0.07, 4.56, 0.62)):
            _o, t = self.standing(name, "roller_shutters_frame_01_COLLIDER" if name.startswith("equator") else "roller_shutters_frame_techlight_01_COLLIDER")
            zs = np.unique(np.round(t[:, 2], 2))
            self.assertAlmostEqual(float(zs.min()), z0, delta=0.01)
            self.assertAlmostEqual(float(zs.max()), z1, delta=0.01)
            self.assertAlmostEqual(float(zs[1] - zs[0]), post, delta=0.011)
            self.assertAlmostEqual(float(zs[-1] - zs[-2]), post, delta=0.011)
            self.assertAlmostEqual(float(zs[-2] - zs[1]), free, delta=0.02)
            # no triangle of the frame reaches into the doorway below the header: the eight triangles that overlap the free span in z (the
            # header's faces) all lie at or above its lower edge, so no diagonal face closes the opening
            tri = t.reshape(-1, 3, 3)
            over = (tri[:, :, 2].max(axis=1) > zs[1] + 0.01) & (tri[:, :, 2].min(axis=1) < zs[-2] - 0.01)
            self.assertEqual(int(over.sum()), 8)                             # the header's eight triangles (a post's faces stand at the span's edge)
            self.assertGreaterEqual(float(tri[over][:, :, 1].min()), header_bottom - 0.03)
        _o, rail = self.standing("big_gate_metal", "colider")
        np.testing.assert_allclose(list(rail.min(0)) + list(rail.max(0)), [-344.17, 0.85, 474.85, -344.07, 0.97, 484.26], atol=0.01)
        self.assertGreater(float(rail.min(0)[1]) - (-3.74), 4.5)             # 4.59 m of clearance


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/autoservice-native-v2 is not available (set ARENAMAPPORT_WORK_DIR)")
class PackageLayerAuditTests(unittest.TestCase):
    def test_layer_census_of_the_live_colliders(self):
        cset = colliders()
        counts: dict = {}
        for o in cset.owners:
            counts[o.layer] = counts.get(o.layer, 0) + 1
        self.assertEqual(len(cset.owners), 5649)
        self.assertEqual(counts, {0: 5, 8: 330, 9: 21, 12: 2805, 13: 20, 18: 1936, 22: 139, 29: 373, 30: 20})

    def test_the_bake_source_table_of_the_decided_configuration(self):
        cset = colliders()
        pc.mark_barriers(cset, OPENED_BARRIERS, door_rows())
        try:
            mask = pc.layers_to_mask(NAVMESH_BAKE_LAYERS)
            reasons: dict = {}
            for o in cset.owners:
                reason = pc.bake_exclusion_reason(o, keep_borders=True)
                if reason is None and not (mask >> o.layer) & 1:
                    reason = "not in navmesh_bake_layers"
                reasons[reason or "source"] = reasons.get(reason or "source", 0) + 1
            # the same table the oracle run prints: the 20 SpawnPointMarker boxes (Triggers layer 13), the two SPAWN_BOUND_Fighter boxes (by name),
            # the nine moved gate colliders, and 3,272 colliders on layers 0, 8, 12 and 22 that the layer list drops
            self.assertEqual(reasons, {"SPAWN_BOUND*": 2, "Triggers layer 13": 20, "not in navmesh_bake_layers": 3272,
                                       "opened_barriers": 9, "source": 2346})
            dropped = {o.layer for o in cset.owners if pc.bake_exclusion_reason(o, True) is None and not (mask >> o.layer) & 1}
            self.assertEqual(dropped, {0, 8, 12, 22})
            sources = pc.source_owner_mask(cset, layer_mask=mask, keep_borders=True)
            self.assertEqual({o.layer for o in cset.owners if sources[o.index]}, {9, 18, 29, 30})
            self.assertEqual(int(sources.sum()), 2346)
        finally:
            pc.mark_barriers(cset, [])

    def test_the_layer_zero_solids_are_the_front_door_of_one_car_and_three_that_the_bake_drops_anyway(self):
        # AGENTS.md Traps "Layer-0 blockout": Arena's mask never sees layer 0, so a live layer-0 solid in the play space is a wall for the player and
        # air for the bake. The package has five: the vaz_2106 right front door (two MeshColliders at (-353.72, -3.32, 483.75), inside the car's own
        # layer-18 housing: decided "keep out", it adds no blocked sample, see OracleTests), the two SPAWN_BOUND_Fighter boxes (dropped by name) and
        # 'Vishel_Plane' (200 triangles, a child of big_gate_metal/gate: it moves with the posed leaf and the rows drop it).
        cset = colliders()
        rows = sorted(("/".join(o.names[-2:]), o.kind) for o in cset.owners if o.layer == 0)
        self.assertEqual(rows, sorted([
            ("car_frontdoor_R/colider 3", "MeshCollider"), ("balistic_col 3/plastic 3", "MeshCollider"),
            ("gate/Vishel_Plane", "MeshCollider"),
            ("SPAWNS/SPAWN_BOUND_Fighter", "BoxCollider"), ("SPAWNS/SPAWN_BOUND_Fighter (1)", "BoxCollider")]))
        door = [o for o in cset.owners if o.layer == 0 and "car_frontdoor_R" in o.names]
        self.assertEqual(len(door), 2)
        self.assertTrue(all("vaz_2106" in o.names for o in door))
        lo = np.min([owner_box(cset, o)[0] for o in door], axis=0)
        hi = np.max([owner_box(cset, o)[1] for o in door], axis=0)
        np.testing.assert_allclose(list(lo) + list(hi), [-353.85, -3.71, 483.74, -353.42, -2.6, 484.73], atol=0.01)
        housing = next(o for o in cset.owners if o.layer == 18 and o.names[-3:] == ("vaz_2106", "car_housing", "colider 5"))
        hlo, hhi = owner_box(cset, housing)
        self.assertTrue((hlo[[0, 2]] <= lo[[0, 2]] + 0.01).all() and (hhi[[0, 2]] >= hi[[0, 2]] - 0.01).all())    # the door leaf lies inside the car body's footprint

    def test_layer_eight_is_the_spectator_rigs_and_layer_twenty_two_the_small_props(self):
        # Layer 8 (330 = 30 fans x 11 body parts of SCAV_SPECTATORS, crowdless) and layer 22 (139 SmallPhysicsObject colliders: bottles, cups,
        # cones, keyboards) are not in the list. Without the list the oracle would count them as walls.
        cset = colliders()
        layer8 = [o for o in cset.owners if o.layer == 8]
        self.assertEqual(len(layer8), 330)
        self.assertEqual({o.names[0] for o in layer8}, {"SCAV_SPECTATORS"})
        layer22 = [o for o in cset.owners if o.layer == 22]
        self.assertEqual(len(layer22), 139)


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/autoservice-native-v2 is not available (set ARENAMAPPORT_WORK_DIR)")
class PackageAnchorTests(unittest.TestCase):
    def test_twenty_markers_ten_per_pocket(self):
        markers = package_markers()
        self.assertEqual(len(markers), 20)
        east = [m for m in markers if m[1][0] > -345]
        west = [m for m in markers if m[1][0] < -400]
        self.assertEqual((len(east), len(west)), (10, 10))
        self.assertEqual(sorted(marker_index(m[0]) for m in east), [-1, 1, 2, 3, 4, 8, 16, 17, 18, 19])
        self.assertEqual(sorted(marker_index(m[0]) for m in west), [5, 6, 7, 9, 10, 11, 12, 13, 14, 15])
        self.assertGreater(min(m[1][0] for m in east), -341.0)          # x -334.8..-340.6 in the east pocket
        self.assertLess(max(m[1][0] for m in west), -405.0)             # x -405.9..-410.0 in the west pocket

    def test_the_bake_box_holds_every_marker_capture_point_and_exit_with_room(self):
        x0, x1, y0, y1, z0, z1 = BAKE_BOUNDS
        points = [m[1] for m in package_markers()] + [CAPTURE_POINT] + list(EXIT_DRAFTS.values())
        arr = np.array(points)
        self.assertGreaterEqual(arr[:, 0].min() - x0, 3.0)
        self.assertGreaterEqual(x1 - arr[:, 0].max(), 3.0)
        self.assertGreaterEqual(arr[:, 2].min() - z0, 3.0)
        self.assertGreaterEqual(z1 - arr[:, 2].max(), 3.0)
        self.assertGreaterEqual(arr[:, 1].min() - y0, 1.0)         # floors at -3.72..-3.84, the box floor at -5
        self.assertGreaterEqual(y1 - arr[:, 1].max(), 4.0)

    def test_the_exits_lie_inside_their_pockets(self):
        # R3 (2026-11-12), replacing "outside both pockets and beyond the gates": the east exit lies inside the east pocket (SPAWN_BOUND_Fighter,
        # past the big gate at x -344.09), the west exit inside the west pocket (SPAWN_BOUND_Fighter (1), past the rollers at x -402.26). The full
        # rule (one exit per footprint, 2.5 m from every row) is in test_autoservice_spawns.
        import extract_mode_objects as M
        bounds = {b["name"]: b for b in sidecar("autoservice.modes.json")["spawn_bounds"]}
        self.assertGreaterEqual(M.box_margin(bounds["SPAWN_BOUND_Fighter"], EXIT_DRAFTS["exit_east"]), 0.0)
        self.assertGreaterEqual(M.box_margin(bounds["SPAWN_BOUND_Fighter (1)"], EXIT_DRAFTS["exit_west"]), 0.0)
        self.assertGreater(EXIT_DRAFTS["exit_east"][0], -344.09)
        self.assertLess(EXIT_DRAFTS["exit_west"][0], -402.26)


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/autoservice-native-v2 is not available (set ARENAMAPPORT_WORK_DIR)")
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

    def water(self):
        return {k for k, m in self.materials.items() if m.get("shader") == self.WATER_SHADER}

    def test_listed_plus_excluded_is_exactly_the_water_shader_materials_of_the_package(self):
        # the census of tools/tests/test_puddle_water_census.py (which reads the staged shader sidecar, item 12), read from the package
        self.assertEqual(self.water(), set(PUDDLE_WATER_MATERIAL_NAMES) | set(PUDDLE_EXCLUDED))
        self.assertEqual(set(PUDDLE_WATER_MATERIAL_NAMES) & set(PUDDLE_EXCLUDED), set())

    def test_the_rain_puddle_is_the_material_the_other_four_maps_list(self):
        material = self.materials["0877f2335044042e"]
        self.assertEqual((material["source_name"], material["keywords"]), ("puddle", ["USERAIN"]))
        users = self.users["0877f2335044042e"]
        self.assertEqual(len(users), 42)
        self.assertTrue(all(name.split(" (")[0] == "puddle2" and live and mesh == "2e93310068beccf4" for _s, name, live, mesh in users))
        self.assertEqual({s for s, _n, _l, _m in users}, {MAIN_SCENE})

    def test_the_wet_mold_atlas_sits_on_inactive_radiator_decals_and_is_no_puddle(self):
        material = self.materials["aa54d9752ed43430"]
        self.assertEqual(material["source_name"], "arena_autoservice_mold_decal_atlas_01_wet")
        self.assertEqual(material["shader"], self.WATER_SHADER)
        users = self.users["aa54d9752ed43430"]
        self.assertEqual(len(users), 4)
        self.assertEqual(sorted(name for _s, name, _l, _m in users),
                         ["Heating_register_01_Decals_LOD0"] * 2 + ["Heating_register_01_Decals_LOD1"] * 2)
        self.assertFalse(any(live for _s, _n, live, _m in users))             # never rendered: nothing wet to step in

    def test_no_other_shader_renders_a_puddle_decal(self):
        names = {m.get("shader") for m in self.materials.values() if m.get("shader") and ("ater" in m["shader"] or "uddle" in m["shader"].lower())}
        self.assertEqual(names, {self.WATER_SHADER})

    def test_concrete_is_most_of_the_ground_under_the_rain_quads_so_no_extra_surface_is_listed(self):
        # PuddleFootstepRepairs: Concrete and Asphalt are eligible, everything else needs a listed extra surface (Bowl lists Soil). Samples 5 x 5 over
        # each of the 42 quads (5.2 m wide), the topmost Terrain/HighPoly/Transparent triangle within 16 cm of the quad plane, its BallisticCollider
        # material (the layer-12 twin wins a tie with its movement collider): Concrete 774, MetalThin 66, Plastic 53, WoodThin 40, MetalThick 25,
        # Soil 9, GenericSoft 5, Rubber 3, Fabric 1, 36 with no floor within 16 cm and 38 on a movement collider without a ballistic twin.
        cset = colliders()
        mat = {}
        for sname, sc in cset.scenes.items():
            for key, o in sc.objects.items():
                for c in o.get("components", []):
                    if c["type"] == "BallisticCollider":
                        mat[(sname, key)] = c["material"]
        sel = np.array([o.layer in (9, 12, 18) and o.active and o.enabled and not o.trigger for o in cset.owners])
        tri_sel = sel[cset.tri_owner]
        tris, tri_owner = cset.tris[tri_sel], cset.tri_owner[tri_sel]
        xmin, xmax = tris[:, :, 0].min(1), tris[:, :, 0].max(1)
        zmin, zmax = tris[:, :, 2].min(1), tris[:, :, 2].max(1)
        quads = [(s, k) for s, sc in cset.scenes.items() for k, o in sc.objects.items()
                 if o["name"].split(" (")[0] == "puddle2" and any(c["type"] == "MeshRenderer" and "0877f2335044042e" in c.get("material_ids", [])
                                                                 for c in o.get("components", []))]
        self.assertEqual(len(quads), 42)
        hist: dict = {}
        grid = np.linspace(0.1, 0.9, 5)
        for sname, key in quads:
            world = cset.scenes[sname].world(key)
            for a in grid:
                for b in grid:
                    w = (world @ np.array([-2.5 + 5.0 * a, -2.5 + 5.0 * b, 0.0, 1.0]))[:3]
                    idx = np.nonzero((xmin <= w[0]) & (xmax >= w[0]) & (zmin <= w[2]) & (zmax >= w[2]))[0]
                    best = "(none within 16 cm)"
                    if len(idx):
                        t = tris[idx]
                        v0, v1 = t[:, 1] - t[:, 0], t[:, 2] - t[:, 0]
                        d = v0[:, 0] * v1[:, 2] - v1[:, 0] * v0[:, 2]
                        good = np.abs(d) > 1e-12
                        d = np.where(good, d, 1.0)
                        u = ((w[0] - t[:, 0, 0]) * v1[:, 2] - v1[:, 0] * (w[2] - t[:, 0, 2])) / d
                        v = (v0[:, 0] * (w[2] - t[:, 0, 2]) - (w[0] - t[:, 0, 0]) * v0[:, 2]) / d
                        y = t[:, 0, 1] + u * v0[:, 1] + v * v1[:, 1]
                        hit = good & (u >= 0) & (v >= 0) & (u + v <= 1) & (np.abs(y - w[1]) <= 0.16)
                        if hit.any():
                            hy, ho = y[hit], tri_owner[idx][hit]
                            near = hy >= hy.max() - 0.02
                            owners = [cset.owners[i] for i in ho[near]]
                            with_mat = [o for o in owners if (o.scene, o.obj_id) in mat]
                            o = (with_mat or owners)[0]
                            best = mat.get((o.scene, o.obj_id), "no BallisticCollider (layer %d)" % o.layer)
                    hist[best] = hist.get(best, 0) + 1
        self.assertEqual(sum(hist.values()), 1050)
        self.assertAlmostEqual(hist["Concrete"] / 1050, 0.737, delta=0.01)
        self.assertLess(hist.get("Soil", 0) / 1050, 0.02)
        self.assertGreater(hist["Concrete"], 5 * max(v for k, v in hist.items() if k != "Concrete"))


# ---------------------------------------------------------------------------------------------------------------------
# 3. the installed Arena
# ---------------------------------------------------------------------------------------------------------------------
def arena_mesh():
    if "mesh" not in _CACHE:
        _CACHE["mesh"], _CACHE["meta"] = nd.load_arena_mesh(nd.DEFAULT_ARENA_DATA, AUTOSERVICE.assets, AUTOSERVICE.path_id)
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
        self.assertEqual(self.mesh.poly_count, 695)
        self.assertEqual(int((self.mesh.poly_nverts - 2).sum()), 1212)
        self.assertEqual(len(self.mesh.verts), 1250)
        self.assertEqual((len(self.mesh.tiles), self.meta["tiles_total"]), (19, 30))

    def test_two_components_the_main_one_and_a_second_island_outside_the_cubes(self):
        import navmesh_oracle as no
        components = no.component_summary(self.mesh)
        self.assertEqual([c["polygons"] for c in components], [675, 20])
        main, island = components
        self.assertAlmostEqual(main["area_xz"], 1915.6, delta=0.5)
        for got, want in zip(main["min"] + main["max"], [-412.4, -3.82, 466.6, -333.7, -0.43, 511.9]):
            self.assertAlmostEqual(got, want, delta=0.06)
        self.assertAlmostEqual(island["area_xz"], 33.8, delta=0.3)
        for got, want in zip(island["min"] + island["max"], [-336.2, -3.23, 521.3, -324.2, -3.09, 527.7]):
            self.assertAlmostEqual(got, want, delta=0.06)
        # the island lies north of the cubes' z 512.99 and outside the bake box (z 515): checked, not copied (no point of interest is on it)
        self.assertGreater(island["min"][2], BAKE_BOUNDS[5])
        self.assertGreater(island["min"][2], 512.99)
        # nothing of Arena's mesh lies above y -0.43: the tribunes, roofs and cube tops are the fresh bake's business (FreshBakeTests)
        self.assertLess(main["max"][1], -0.4)
        # Arena's own mesh lies inside the border cubes and inside the box
        x0, x1, y0, y1, z0, z1 = BAKE_BOUNDS
        self.assertTrue(x0 < main["min"][0] and main["max"][0] < x1 and z0 < main["min"][2] and main["max"][2] < z1)
        self.assertTrue(y0 < main["min"][1] and main["max"][1] < y1)

    def test_the_surface_decoded_from_level63_is_the_table_row(self):
        import probe_mode_toggler as probe
        surfaces = probe.scene_census(nd.DEFAULT_ARENA_DATA, AUTOSERVICE.ai_scene_index)["surfaces"]
        self.assertEqual(len(surfaces), 1)
        self.assertEqual(surfaces[0]["layer_mask"], AUTOSERVICE.layer_mask)
        self.assertEqual(surfaces[0]["layer_mask_bits"], ARENA_LAYERS)
        self.assertEqual(surfaces[0]["navmesh_data"]["path_id"], AUTOSERVICE.path_id)
        self.assertEqual(surfaces[0]["use_geometry"], "PhysicsColliders")
        self.assertEqual(surfaces[0]["collect_objects"], "Volume")
        np.testing.assert_allclose(surfaces[0]["center"], [-370.3, 2.0, 486.8], atol=1e-4)
        np.testing.assert_allclose(surfaces[0]["size"], [101.8, 121.99, 81.7], atol=1e-2)

    def test_the_ai_points_lie_on_the_main_component(self):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            points = nd.live_ai_points(nd.DEFAULT_ARENA_DATA, AUTOSERVICE.ai_scene_index, AUTOSERVICE.ai_scene_name, AUTOSERVICE.key)
        kinds = {k: sum(1 for p in points if p["kind"] == k) for k in ("core_point", "patrol_hub", "patrol_point")}
        self.assertEqual(kinds, {"core_point": 7, "patrol_hub": 5, "patrol_point": 54})
        pos = np.array([p["position"] for p in points])
        poly, surf = self.mesh.locate(pos, max_dy=1.5)
        self.assertTrue((poly >= 0).all() and (self.mesh.labels[poly] == 0).all())
        self.assertLess(float(np.abs(surf - pos[:, 1]).max()), 0.2)

    def test_the_gates_the_markers_the_capture_point_and_the_exit_drafts_stand_on_the_main_component(self):
        if not HAVE_PACKAGE:
            self.skipTest("work/extract/autoservice-native-v2 is not available")
        pos = np.array([m[1] for m in package_markers()] + [CAPTURE_POINT] + list(EXIT_DRAFTS.values()))
        poly, surf = self.mesh.locate(pos, max_dy=1.5)
        self.assertTrue((poly >= 0).all() and (self.mesh.labels[poly] == 0).all())
        self.assertLess(float(np.abs(surf - pos[:, 1]).max()), 0.2)        # measured 0.15 (the marker boxes stand a little off Arena's floor)

    def test_the_mesh_runs_through_the_closed_gate_leaves(self):
        # Arena baked with the gates open: along the closed leaf of every gate (a line at its foot, along its long axis) the main component lies
        # under most of the sample points: 8 of 9 (big gate), 9 of 9 (both techlight rollers), 5 of 9 (the Equator roller: the line also crosses its posts).
        if not HAVE_PACKAGE:
            self.skipTest("work/extract/autoservice-native-v2 is not available")
        cset = colliders()
        found = {}
        for o in cset.owners:
            if o.scene == TEAMFIGHT and o.layer == 18 and o.names[:1] == ("DOORS",) and o.names[-1] in (
                    "colider 1", "roller_shutters_01_COLLIDER", "roller_shutters_techlight_01_COLLIDER"):
                lo, hi = owner_box(cset, o)
                ext = hi - lo
                axis = 0 if ext[0] > ext[2] else 2
                pts = []
                for f in np.linspace(0.1, 0.9, 9):
                    p = 0.5 * (lo + hi)
                    p[axis] = lo[axis] + f * ext[axis]
                    p[1] = lo[1] + 0.2
                    pts.append(p)
                poly, _surf = self.mesh.locate(np.array(pts), max_dy=0.6)
                self.assertEqual(set(self.mesh.labels[poly[poly >= 0]].tolist()) - {0}, set())
                found[o.names[1]] = int((poly >= 0).sum())
        self.assertEqual(found, {"big_gate_metal": 8, "equator_roller_shutters_01": 5, "roller_shutters_techlight_01": 9,
                                 "roller_shutters_techlight_01 (1)": 9})
        # the Equator roller's doorway floor lies at y -3.62 (the shutter root at -4.34 sits below it): header lower edge -1.75 leaves 1.87 m
        poly, surf = self.mesh.locate(np.array([[-344.6, -3.78, 506.83]]), max_dy=0.6)
        self.assertTrue(poly[0] >= 0)
        self.assertAlmostEqual(float(surf[0]), -3.62, delta=0.02)
        self.assertGreater(-1.75 - float(surf[0]), 1.7)


# ---------------------------------------------------------------------------------------------------------------------
# 4. the oracle runs (navmesh_diff.analyse with the decided configuration) and the fresh-bake surface model
# ---------------------------------------------------------------------------------------------------------------------
def oracle_points():
    """The 20 markers, the capture point, the two exit drafts and Arena's own AI points (core points, patrol hubs and points): 89 probes."""
    if "points" not in _CACHE:
        pts = [{"name": m[0].replace(" ", "_"), "kind": "player_spawn", "position": list(m[1])} for m in package_markers()]
        pts.append({"name": "capture", "kind": "capture_point", "position": list(CAPTURE_POINT)})
        pts += [{"name": n, "kind": "exit", "position": list(p)} for n, p in EXIT_DRAFTS.items()]
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            pts += nd.live_ai_points(nd.DEFAULT_ARENA_DATA, AUTOSERVICE.ai_scene_index, AUTOSERVICE.ai_scene_name, AUTOSERVICE.key)
        _CACHE["points"] = pts
    return _CACHE["points"]


def oracle(variant):
    """One analyse() run, cached: 'decided' (the four gates with their rows, layers incl. 29, kept cubes), 'nocubes' (the same without layer 29),
    'closed' (no opened barrier, the gate leaves stay), 'layer0' (decided plus layer 0)."""
    if variant not in _CACHE:
        mesh, _meta = arena_mesh()
        cfg = nd.Config(arena_layer_mask=AUTOSERVICE.layer_mask)
        layers = {"decided": NAVMESH_BAKE_LAYERS, "nocubes": ARENA_LAYERS, "closed": NAVMESH_BAKE_LAYERS,
                  "layer0": [0] + NAVMESH_BAKE_LAYERS}[variant]
        barriers = [] if variant == "closed" else OPENED_BARRIERS
        rep = nd.analyse(str(PACKAGE), cfg, barriers, oracle_points(), nd.DEFAULT_ARENA_DATA, mesh=mesh, cset=colliders(),
                         port_layer_mask=pc.layers_to_mask(layers), keep_level_borders=variant != "nocubes",
                         door_rows=None if variant == "closed" else door_rows(), log=lambda *a: None, greedy_steps=0)
        _CACHE[variant] = rep
        pc.mark_barriers(colliders(), [])
    return _CACHE[variant]


def route_lengths(rep, pairs):
    """Dijkstra metres (4-neighbour lattice of 0.2 m: a Manhattan-like length, comparable between runs) on the model's passable samples."""
    graph, v = rep["_graph"], rep["_verdicts"]["port"]
    passable = ~v.blocked & ~v.unsupported
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


ROUTES = [("Usec_house_(1)", "Usec_house_(5)"), ("Usec_house_(1)", "capture"), ("Usec_house_(5)", "capture"), ("Usec_house_(1)", "exit_east"),
          ("Usec_house_(5)", "exit_west"), ("exit_east", "exit_west"), ("Usec_house_(16)", "Usec_house_(8)"), ("Usec_house_(13)", "Usec_house_(12)")]


@unittest.skipUnless(HAVE_ARENA and HAVE_PACKAGE, "the installed Arena 0.6.2 or the extracted package is not available")
class OracleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")

    def test_the_decided_configuration_keeps_one_component_with_every_probe(self):
        rep = oracle("decided")
        self.assertEqual(rep["port"]["sources"], 2346)
        self.assertEqual(rep["port"]["exclusion_reasons"], {"SPAWN_BOUND*": 2, "Triggers layer 13": 20, "not in navmesh_bake_layers": 3272,
                                                            "opened_barriers": 9, "source": 2346})
        s = rep["samples"]
        self.assertEqual((s["count"], s["passable_port"], s["unsupported"]), (47951, 47865, 0))
        # Arena's agent (0.28 / 1.6) meets 38 blocked samples (107 without the slack, 22 on Arena's own layers only): 12 prop instances in all,
        # five small props and seven border cubes, 1.7 m2 together, none of them a route
        self.assertEqual((s["blocked_arena_agent"], s["blocked_arena_agent_no_slack"], s["blocked_arena_agent_arena_layers"]), (38, 107, 22))
        self.assertEqual(s["blocked_port_agent"], 86)
        self.assertEqual(rep["port_component_count"], 1)
        self.assertAlmostEqual(rep["port_components"][0]["area_m2"], 1914.6, delta=0.3)       # Arena's main component: 1915.6
        reach = rep["reachability"]
        self.assertEqual((reach["probes"], reach["located"], reach["in_anchor_component"], reach["outside"]), (89, 89, 89, []))
        self.assertEqual(rep["config"]["door_rows"], 23)

    def test_arena_walks_through_twelve_small_things_and_none_is_a_route(self):
        rep = oracle("decided")
        clusters = rep["clusters_arena_agent"]
        self.assertEqual(len(clusters), 12)
        cubes = [c for c in clusters if "Arena_AutoService_LevelBorder/Cube" in c["path"]]
        props = [c for c in clusters if c not in cubes]
        self.assertEqual(len(cubes), 7)
        self.assertEqual(sorted(c["path"].rsplit("/", 1)[1] for c in props), sorted([
            "Ladder_03 (1)", "Office_cabin_frame_01 (7)", "garbage_mix6_set_reserve", "Garbage_set_04 (11)", "COPY_plywood_board_2 (1)"]))
        self.assertLess(sum(c["area_m2"] for c in clusters), 2.0)
        self.assertLess(max(c["area_m2"] for c in clusters), 0.5)

    def test_no_border_cube_cuts_a_route_between_the_pockets_the_capture_point_and_the_exits(self):
        decided, nocubes = oracle("decided"), oracle("nocubes")
        self.assertEqual(nocubes["port"]["sources"], 1973)
        self.assertEqual(nocubes["port_component_count"], 1)
        self.assertEqual(nocubes["samples"]["blocked_port_agent"], 31)
        self.assertAlmostEqual(nocubes["port_components"][0]["area_m2"], 1916.8, delta=0.3)          # 2.2 m2 more: the cubes' footprint at the edge
        with_cubes = route_lengths(decided, ROUTES)
        without = route_lengths(nocubes, ROUTES)
        self.assertEqual(with_cubes, without)
        # The exit routes depend on where the exits stand (R6: no literal copy of an exit position or of a length that follows from it): since
        # 2026-11-12 the exits stand inside the pockets (R3), so a pocket marker reaches its own pocket's exit within a short walk and the exits
        # reach each other; the lengths were 35.2 / 26.8 / 62.8 m for the 2026-11-11 yard drafts and 7.6 / 16.2 / 82.2 m for the 2026-11-12 positions.
        exit_routes = {k: with_cubes.pop(k) for k in ("Usec_house_(1)>exit_east", "Usec_house_(5)>exit_west", "exit_east>exit_west")}
        self.assertTrue(all(v is not None for v in exit_routes.values()), exit_routes)
        # a route may not detour: the lattice length is Manhattan-like (up to 1.42 x the straight line), so the bar is 1.5 x the straight line + 10 m
        # (measured 7.6 / 16.2 / 82.2 m against straight lines of 5 / 12 / 66 m); a border cube that lengthened a route past that would fail here
        points = {p["name"]: p["position"] for p in oracle_points()}
        for route, length in exit_routes.items():
            a, b = route.split(">")
            straight = math.dist(points[a], points[b])
            self.assertLessEqual(length, 1.5 * straight + 10.0, f"{route}: {length} m for a straight line of {straight:.1f} m")
        self.assertEqual(with_cubes, {
            "Usec_house_(1)>Usec_house_(5)": 106.0, "Usec_house_(1)>capture": 59.2, "Usec_house_(5)>capture": 56.4,
            "Usec_house_(16)>Usec_house_(8)": 33.2, "Usec_house_(13)>Usec_house_(12)": 26.4})
        # ... and they equal the lengths on Arena's own mesh (every sample passable): the model loses no metre of any route
        graph = decided["_graph"]
        arena_like = dict(decided, _verdicts={"port": mock.Mock(blocked=np.zeros(graph.n, bool), unsupported=np.zeros(graph.n, bool))})
        self.assertEqual(route_lengths(arena_like, ROUTES), {**with_cubes, **exit_routes})

    def test_the_pockets_are_sealed_without_the_gate_rows_and_open_with_them(self):
        closed = oracle("closed")
        self.assertEqual(closed["port"]["sources"], 2350)                    # the four 18 gate colliders (leaves) are bake sources again
        self.assertEqual(closed["port_component_count"], 3)
        self.assertAlmostEqual(closed["port_components"][0]["area_m2"], 1380.8, delta=0.3)
        self.assertEqual(closed["samples"]["blocked_port_agent"], 263)
        per = {p["name"]: p["model_component"] for p in closed["probes"] if p["kind"] == "player_spawn"}
        east = {c for n, c in per.items() if marker_index_name(n) in (-1, 1, 2, 3, 4, 8, 16, 17, 18, 19)}
        west = {c for n, c in per.items() if marker_index_name(n) in (5, 6, 7, 9, 10, 11, 12, 13, 14, 15)}
        self.assertEqual(len(east), 1)
        self.assertEqual(len(west), 1)
        self.assertEqual(len(east | west | {0}), 3)                          # three different components: yard, east pocket, west pocket
        self.assertNotIn(0, east | west)
        self.assertEqual(route_lengths(closed, [("Usec_house_(1)", "capture")])["Usec_house_(1)>capture"], None)
        self.assertEqual(route_lengths(closed, [("Usec_house_(5)", "capture")])["Usec_house_(5)>capture"], None)

    def test_every_doorway_stays_passable_for_the_port_agent_with_the_frames_standing(self):
        rep = oracle("decided")
        pts = rep["_pts"]
        v = rep["_verdicts"]["port"]
        ok = ~v.blocked & ~v.unsupported
        slabs = {"big gate": ((-344.3, -343.9), (475.3, 479.8), 36), "equator roller": ((-344.8, -344.4), (505.95, 507.72), 10),
                 "techlight (west, z 501)": ((-402.5, -402.0), (498.95, 503.5), 37), "techlight (1) (west, z 477)": ((-402.5, -402.0), (475.25, 479.8), 38)}
        for name, ((x0, x1), (z0, z1), count) in slabs.items():
            sel = (pts[:, 0] >= x0) & (pts[:, 0] <= x1) & (pts[:, 2] >= z0) & (pts[:, 2] <= z1)
            self.assertEqual((name, int(sel.sum()), int(ok[sel].sum())), (name, count, count))     # every Arena sample of the doorway is passable

    def test_layer_zero_adds_no_blocked_sample_so_the_car_door_stays_out_of_the_list(self):
        base, with0 = oracle("decided"), oracle("layer0")
        self.assertEqual(with0["port"]["sources"], 2348)                     # the two door colliders
        self.assertEqual(int((with0["_verdicts"]["port"].blocked & ~base["_verdicts"]["port"].blocked).sum()), 0)
        self.assertEqual(with0["samples"]["blocked_port_agent"], base["samples"]["blocked_port_agent"])
        self.assertEqual(with0["port_component_count"], 1)
        # Arena's mesh has a hole at the door (its car is a solid of layer 18 around it)
        mesh, _ = arena_mesh()
        poly, _surf = mesh.locate(np.array([[-353.64, -3.3, 484.2]]), max_dy=2.0)
        self.assertEqual(int(poly[0]), -1)


def marker_index_name(probe_name):
    """'Usec_house' -> -1, 'Usec_house_(7)' -> 7 (the oracle probe names)."""
    return -1 if probe_name == "Usec_house" else int(probe_name[len("Usec_house_("):-1])


def fresh_surfaces(ceiling):
    """What the bake would make walkable inside the box: floor candidates from the up-facing faces of the bake sources, tested with the port agent's
    column model (walk_model), grouped by 4-neighbour connectivity; a group is 'ground' when Arena's main component lies under any of its cells.
    The grid ends at `ceiling` (the box's y max): solids above it are not represented, as Unity clips the heightfield at the box top."""
    key = ("fresh", ceiling)
    if key in _CACHE:
        return _CACHE[key]
    cset = colliders()
    pc.mark_barriers(cset, OPENED_BARRIERS, door_rows())
    try:
        mask = pc.source_owner_mask(cset, layer_mask=pc.layers_to_mask(NAVMESH_BAKE_LAYERS), keep_borders=True)
    finally:
        pc.mark_barriers(cset, [])
    tris, owner = nd._source_arrays(cset, mask)
    x0, x1, y0, _y1, z0, z1 = BAKE_BOUNDS
    cs = 0.2
    grid = wm.make_grid(x0, x1, z0, z1, y0, ceiling, cs, 0.1)
    agent = wm.PORT_AGENT
    solid, floor, _n = wm.build_occupancy(tris, owner, grid, agent)
    cells, ys = [], []
    for w in range(grid.words):
        for b in range(64):
            bit = w * 64 + b
            if bit >= grid.nbits:
                break
            sel = np.nonzero((floor[:, w] >> np.uint64(b)) & np.uint64(1))[0]
            if len(sel):
                cells.append(sel)
                ys.append(np.full(len(sel), grid.y0 + (bit + 0.5) * grid.dy))
    cells, ys = np.concatenate(cells), np.concatenate(ys)
    ix, iz = cells % grid.nx, cells // grid.nx
    pts = np.stack([grid.x0 + (ix + 0.5) * cs, ys, grid.z0 + (iz + 0.5) * cs], axis=1)
    verdict = wm.evaluate_samples(pts, solid, floor, grid, agent, floor_margin=0.15, support_tol=0.6, shrink=0.1)
    ok = ~verdict.blocked & ~verdict.unsupported
    cellid = (iz * grid.nx + ix)[ok]
    pts = pts[ok]
    mesh, _meta = arena_mesh()
    poly, _surf = mesh.locate(pts, max_dy=0.4)
    on_arena = (poly >= 0) & (mesh.labels[np.clip(poly, 0, None)] == 0)
    graph = nd.SampleGraph(pts, np.zeros(len(pts), dtype=np.int64), cs, (grid.x0 + 0.5 * cs, grid.z0 + 0.5 * cs))
    comp, sizes = graph.components(np.ones(len(pts), dtype=bool))
    ground_ids = {int(c) for c in np.unique(comp[on_arena])}
    is_ground = np.isin(comp, list(ground_ids))
    result = {
        "pts": pts, "comp": comp, "ground_ids": ground_ids, "is_ground": is_ground,
        "ground_area": len(np.unique(cellid[is_ground])) * cs * cs, "island_area": len(np.unique(cellid[~is_ground])) * cs * cs,
        "ground_max_y": float(pts[is_ground][:, 1].max()), "ground_bbox": (pts[is_ground].min(0), pts[is_ground].max(0)),
        "island_above_floor": len(np.unique(cellid[~is_ground & (pts[:, 1] > -0.43)])) * cs * cs,
        "ground_above_floor": int((is_ground & (pts[:, 1] > -0.38)).sum()),
    }
    _CACHE[key] = result
    return result


@unittest.skipUnless(HAVE_ARENA and HAVE_PACKAGE, "the installed Arena 0.6.2 or the extracted package is not available")
class FreshBakeTests(unittest.TestCase):
    """Arena's mesh covers nothing above y -0.43; what would the fresh bake walk on there (tribune decks at y 4.4 to 5.3, roofs, cube tops)?
    Model answer (inferred, not a bake): everything above Arena's highest floor is an island that no ground route reaches, and the ground
    component does not depend on the ceiling of the box. The host walk and the U1 bundle decide the rest."""

    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")

    def test_the_ground_component_is_the_same_for_the_decided_ceiling_and_for_eight_metres(self):
        decided, tall = fresh_surfaces(BAKE_BOUNDS[3]), fresh_surfaces(8.0)
        self.assertAlmostEqual(decided["ground_area"], 1997.8, delta=1.0)       # unique 0.2 m cells (Arena's own polygons: 1915.6 m2)
        self.assertAlmostEqual(tall["ground_area"], decided["ground_area"], delta=0.01)
        np.testing.assert_allclose(decided["ground_bbox"][0], tall["ground_bbox"][0], atol=1e-6)
        np.testing.assert_allclose(decided["ground_bbox"][1], tall["ground_bbox"][1], atol=1e-6)
        lo, hi = decided["ground_bbox"]
        for got, want in zip(list(lo[[0, 2]]) + list(hi[[0, 2]]), [-412.3, 466.7, -333.7, 511.9]):
            self.assertAlmostEqual(float(got), want, delta=0.15)                # the model never walks beyond Arena's own footprint: the borders hold

    def test_nothing_above_arenas_highest_floor_is_reachable_from_the_ground(self):
        for ceiling in (BAKE_BOUNDS[3], 8.0):
            fresh = fresh_surfaces(ceiling)
            self.assertLess(fresh["ground_max_y"], -0.43 + 0.05)
            self.assertEqual(fresh["ground_above_floor"], 0)

    def test_the_islands_above_the_ground_floor_grow_with_the_ceiling_and_the_decided_one_is_the_low_choice(self):
        low, tall = fresh_surfaces(BAKE_BOUNDS[3]), fresh_surfaces(8.0)
        self.assertAlmostEqual(low["island_area"], 1631.5, delta=2.0)           # at ground level too: closed rooms and the outer yard behind the walls
        self.assertAlmostEqual(tall["island_area"], 1910.6, delta=2.0)
        self.assertGreater(low["island_above_floor"], 500.0)                    # roofs, cabin tops and cube tops between -0.43 and 2.0
        self.assertLess(low["island_area"], tall["island_area"])


if __name__ == "__main__":
    unittest.main()
