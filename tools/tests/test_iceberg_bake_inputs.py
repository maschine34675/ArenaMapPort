"""Iceberg NavMesh oracle row and bake inputs (docs/PLAN-ICEBERG.md item 9): opened_barriers, start_gates, held_gates,
navmesh_bake_layers, keep_level_borders, bake_bounds, navmesh_bake_exclusions, the puddle materials and the shader decision.

Why: the Unity bake is a 15-minute batch run, so everything checkable offline is pinned here, like test_airpit_bake_inputs.py
did for AirPit and Bowl's analysis (docs/progress/bowl-release.md) did by hand. The DECIDED_* constants below are the values the
lead writes into tools/maps/iceberg.json and the C# descriptor in the promotion commit; every number has its evidence in
docs/progress/iceberg-release.md ("NavMesh oracle and bake inputs"). Four groups, each skipped when its input is absent:

  * constants and code pins (repository only): the validators accept the decided values, ARENA_SOURCES["iceberg"] is the measured row,
    the bake code keeps kept border cubes whatever they are called;
  * the sidecar: once tools/maps/iceberg.json carries any of the decided keys it must carry all of them, equal to the constants;
  * the extracted package work/extract/iceberg-native-v1 (override the work directory with ARENAMAPPORT_WORK_DIR when the tests
    run from a git worktree without work/): the 365 border cubes, the KillTrigger, the gates, the layer census, the layer-0 audit,
    the cube-top islands against the y ceiling, the puddle materials, the marker positions against the bounds;
  * the installed Arena (0.6.1.0.48206, read-only, UnityPy): the identity placement of the NavMeshData, the measured mesh, the
    markers on it, the gates it runs through, the layer-12 audit and the evidence that its cover holes belong to the 0.5.4 layout.

The oracle runs themselves (navmesh_diff.py, about ten minutes each) are not repeated here; their numbers are in the progress doc.
"""
import io
import json
import math
import os
import re
import sys
import unittest
from pathlib import Path
from unittest import mock

import numpy as np

REPO = Path(__file__).resolve().parents[2]
WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import navmesh_diff as nd  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402

try:
    import port_colliders as pc  # noqa: E402
except ImportError:                      # numpy missing: only the constant/sidecar/code pins run
    pc = None

PACKAGE = WORK / "extract" / "iceberg-native-v1"
SPIKE_PACKAGE = WORK / "extract" / "iceberg"          # the Arena 0.5.4 spike package (old barricade layout)
DESIGN_STUFF = "Arena_Sanatorium_CheckPoint_DesignStuff"
HAVE_PACKAGE = pc is not None and (PACKAGE / "scenes" / f"{DESIGN_STUFF}.json").is_file()
ARENA_DATA = Path(nd.DEFAULT_ARENA_DATA)
HAVE_ARENA = (ARENA_DATA / "sharedassets377.assets").is_file() and (ARENA_DATA / "level377").is_file()
ICEBERG = nd.ARENA_SOURCES["iceberg"]
BAKE_NAVMESH = REPO / "UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs"

# ---------------------------------------------------------------------------------------------------------------------
# The decided values (2026-10-30), sidecar spelling. The C# descriptor names are in the comments.
# ---------------------------------------------------------------------------------------------------------------------
# Arena opens the two respawn gate roots of CheckPoint (level376 DOORS): GATES_A (two sliding gates, x 82.4, the east pocket) and
# GATES_B (three sliding gates at x -36.0 plus the rotating Sanatorium_CheckPoint_Gate, the west pocket). Arena's NavMesh was baked
# with them open (the oracle's first two greedy steps, +181 and +30 samples). Descriptor: OpenedBarriers, StartGates.
# CROSS-CHECK with iceberg.doors.json: BakeNavMesh.ResolveOpenedBarriers and the client match a door row to a barrier by the EXACT string
# row.barrier == opened_barriers entry. If the doors rows carry unit-level barriers (DOORS/GATES_A/sanatorium_resp_gate with `at`,
# DOORS/GATES_B/sanatorium_resp_gate, DOORS/GATES_B/Sanatorium_CheckPoint_Gate; the survey's dry run) instead of the two roots, these
# three strings are the opened barriers and start gates. The bake drops the same 42 colliders either way
# (PackageGateTests.test_the_gates_hold_16_and_26_colliders...), so only this constant and the sidecar change.
OPENED_BARRIERS = ["DOORS/GATES_A", "DOORS/GATES_B"]
START_GATES = ["DOORS/GATES_A", "DOORS/GATES_B"]
HELD_GATES: list = []                                       # HeldGates
# Arena's NavMeshSurface mask 0x40042A00 (layers 9/11/13/18/30) plus 29: the 365 layer-29 LevelBorder cubes are the play space's only
# bound and stay active at runtime, so the bake treats them as walls (Bowl's policy). Descriptor: NavMeshBakeLayers.
ARENA_LAYERS = [9, 11, 13, 18, 30]
NAVMESH_BAKE_LAYERS = [9, 11, 13, 18, 29, 30]
KEEP_LEVEL_BORDERS = True                                   # KeepLevelBorders
# The AABB of the 365 cubes (x -60.11..119.53, z -128.87..-34.86) plus 1.5 to 2.1 m; y from 1.2 m below Arena's lowest floor (-1.79)
# and above the solid KillTrigger box (top y -6.14) to 8 (Arena's highest floor is 6.15, plus the head room). Descriptor: BakeBounds.
BAKE_BOUNDS = [-62, 121, -3, 8, -131, -33]
NAVMESH_BAKE_EXCLUSIONS: list = []                          # NavMeshBakeExclusions: none (the y floor keeps the KillTrigger out)
USES_SERVER_TIME = True                                     # UsesServerTime
# Water decals: the rain-driven 'puddle' (23 'puddle2' quads, the same material id as Equator, Air Pit and Bowl) and the static
# 'City_puddle_atlas_white' (4 quads in the pool hall). 'puddle fuel' (3 renderers named puddle_oil) is oil and stays out.
PUDDLE_WATER_MATERIAL_NAMES = ["0877f2335044042e", "34f527e8925ffebb"]     # PuddleWaterMaterialNames
PUDDLE_EXTRA_SURFACES: list = []                            # PuddleExtraSurfaces: none, the floor under the wet quads is Concrete
PUDDLE_EXCLUDED = {"299a841bb64d31bd": "'puddle fuel': three renderers named puddle_oil (oil)"}
# No shader fallback row for the promotion: the cloth, rock and fog-sheet shaders resolve in process (AirPit/Bowl host evidence) and
# the two unproven names (MK4/Rock Simple, Nature/SpeedTree8) are decided after the first host raid; see the progress doc.
SHADER_FALLBACK_NAMES: list = []                            # ShaderFallbackNames (key omitted from the sidecar)

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
# Keys the sidecar must NOT carry (empty lists are omitted; the C# default is the same).
OMITTED_SIDECAR_KEYS = ("puddle_extra_surfaces", "shader_fallback_names")

# Anchors outside the package markers (tools/maps/iceberg.exits.json and the modes sidecar will carry the same numbers):
CAPTURE_POINTS = {"A": (16.77, 3.90, -91.39), "B": (10.614, 2.69, -77.727), "C": (21.91, 2.85, -47.30)}
EXIT_CANDIDATES = {"east": (72.0, 1.95, -94.0), "west": (-30.5, 2.92, -85.0)}    # the draft exits of item 7: on Arena's main component, outside the gates


def sidecar(name="iceberg.json"):
    return json.loads((REPO / "tools" / "maps" / name).read_text(encoding="utf-8"))


# ---------------------------------------------------------------------------------------------------------------------
# 1. constants and code pins
# ---------------------------------------------------------------------------------------------------------------------
class DecidedValuesTests(unittest.TestCase):
    def decided(self):
        return {k: v for k, v in DECIDED_SIDECAR_KEYS.items()}

    def test_the_validators_accept_the_decided_values(self):
        self.assertEqual(VD.bake_input_errors(self.decided()), [])
        self.assertEqual(SHADER_FALLBACK_NAMES, [])
        self.assertEqual(VD.shader_fallback_errors({"shader_fallback_names": SHADER_FALLBACK_NAMES}), [])
        self.assertEqual(VD.puddle_errors({"puddle_water_material_names": PUDDLE_WATER_MATERIAL_NAMES,
                                           "puddle_extra_surfaces": PUDDLE_EXTRA_SURFACES}), [])

    def test_both_gate_roots_are_start_gates_and_nothing_is_held(self):
        # SpawnGates: a mode plugin's hold keeps every start gate closed; Iceberg has no gate that stays closed after the release
        self.assertEqual(START_GATES, OPENED_BARRIERS)
        self.assertEqual(HELD_GATES, [])
        self.assertTrue(set(START_GATES) <= set(OPENED_BARRIERS))

    def test_the_layer_list_is_arenas_surface_mask_plus_the_border_layer(self):
        self.assertEqual(sorted(ARENA_LAYERS + [29]), NAVMESH_BAKE_LAYERS)
        self.assertEqual(sum(1 << layer for layer in ARENA_LAYERS), ICEBERG.layer_mask)
        self.assertEqual(ICEBERG.layer_mask, 0x40042A00)
        self.assertEqual(ICEBERG.layer_mask, nd.AIRPIT_ARENA_LAYER_MASK)       # Arena's three AI scenes of AirPit and Iceberg share it
        self.assertIn(29, NAVMESH_BAKE_LAYERS)                                  # keep_level_borders without 29 is rejected
        self.assertNotIn(4, NAVMESH_BAKE_LAYERS)
        self.assertTrue(KEEP_LEVEL_BORDERS)

    def test_keep_level_borders_without_layer_29_is_rejected_for_iceberg_too(self):
        bad = dict(self.decided(), navmesh_bake_layers=ARENA_LAYERS)
        self.assertTrue(any("layer 29" in e for e in VD.bake_input_errors(bad)))

    def test_the_bake_box_is_ordered_and_keeps_the_kill_trigger_out(self):
        x0, x1, y0, y1, z0, z1 = BAKE_BOUNDS
        self.assertTrue(x0 < x1 and y0 < y1 and z0 < z1)
        self.assertGreater(y0, -6.14)          # the KillTrigger's top face (y -10.98..-6.14) would be a 190 m2 island at the box floor
        self.assertLess(y0, -1.79)             # below Arena's lowest floor (the east tunnel end)
        self.assertGreaterEqual(y1, 6.15 + 1.7)   # Arena's highest floor plus the agent height

    def test_no_bake_exclusion_is_needed_and_none_overlaps_a_barrier(self):
        self.assertEqual(NAVMESH_BAKE_EXCLUSIONS, [])
        for path in NAVMESH_BAKE_EXCLUSIONS:
            self.assertFalse(any(path == b or path.startswith(b + "/") for b in OPENED_BARRIERS), path)

    def test_the_water_materials_are_unpadded_distinct_and_the_oil_one_is_not_listed(self):
        self.assertEqual(len(set(PUDDLE_WATER_MATERIAL_NAMES)), len(PUDDLE_WATER_MATERIAL_NAMES))
        self.assertFalse(set(PUDDLE_EXCLUDED) & set(PUDDLE_WATER_MATERIAL_NAMES))
        self.assertEqual(PUDDLE_EXTRA_SURFACES, [])


class SidecarTests(unittest.TestCase):
    def test_the_sidecar_carries_all_or_none_of_the_decided_values_and_they_are_equal(self):
        data = sidecar()
        present = [k for k in DECIDED_SIDECAR_KEYS if k in data]
        if not present:
            self.skipTest("tools/maps/iceberg.json has no bake inputs yet (the lead's promotion commit writes them)")
        self.assertEqual([k for k in DECIDED_SIDECAR_KEYS if k not in data], [], "the promotion writes every decided key at once")
        for key, expected in DECIDED_SIDECAR_KEYS.items():
            self.assertEqual(data[key], expected, key)
        for key in OMITTED_SIDECAR_KEYS:
            self.assertNotIn(key, data, key)
        self.assertEqual(VD.bake_input_errors(data), [])

    def test_the_other_sidecars_keep_their_bake_inputs(self):
        # a regression guard for the shared code change of this item (keep_level_borders no longer meets the LevelBorder name prefix):
        # Bowl is the only released map that keeps its borders and its cubes are called "Cube"
        bowl = sidecar("bowl.json")
        self.assertIs(bowl["keep_level_borders"], True)
        self.assertEqual(bowl["navmesh_bake_layers"], [9, 11, 18, 29, 30])
        for stem in ("equator", "bay5", "airpit"):
            self.assertNotIn("keep_level_borders", sidecar(f"{stem}.json"), stem)


class ArenaSourceRowTests(unittest.TestCase):
    def test_the_row_is_the_measured_one(self):
        self.assertEqual((ICEBERG.key, ICEBERG.assets, ICEBERG.path_id), ("arena_iceberg", "sharedassets377.assets", 2))
        self.assertEqual((ICEBERG.ai_scene_index, ICEBERG.ai_scene_name), (377, "Arena_Sanatorium_CheckPoint_AI"))
        self.assertEqual(ICEBERG.package, "work/extract/iceberg-native-v1")

    def test_the_ai_scene_is_the_ai_role_of_the_source_plan(self):
        try:
            derived = nd.arena_source_from_plan(REPO / "tools" / "extract" / "plans" / "iceberg.json", ICEBERG)
        except ImportError:
            self.skipTest("UnityPy is not installed")
        self.assertEqual(derived, ICEBERG)
        plan = json.loads((REPO / "tools" / "extract" / "plans" / "iceberg.json").read_text(encoding="utf-8"))
        self.assertEqual([(s["index"], s["name"]) for s in plan["navmesh_scenes"]], [(377, "Arena_Sanatorium_CheckPoint_AI")])

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

    def test_map_iceberg_selects_the_navmesh_mask_ai_scene_and_package(self):
        seen = self.run_main("--map", "iceberg", "--live-core-points")
        self.assertEqual(seen["package"], "work/extract/iceberg-native-v1")
        self.assertEqual((seen["assets"], seen["path_id"]), ("sharedassets377.assets", 2))
        self.assertEqual(seen["cfg"].arena_layer_mask, 0x40042A00)
        self.assertEqual(seen["live"], (nd.DEFAULT_ARENA_DATA, 377, "Arena_Sanatorium_CheckPoint_AI", "arena_iceberg"))
        self.assertFalse(seen["keep_level_borders"])

    def test_the_decided_descriptor_values_reach_the_model(self):
        # the candidate descriptor of the oracle runs: the barriers, the layer mask and keep_level_borders arrive at analyse()
        candidate = {"opened_barriers": OPENED_BARRIERS, "navmesh_bake_layers": NAVMESH_BAKE_LAYERS, "keep_level_borders": True}
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "candidate.json"
            path.write_text(json.dumps(candidate), encoding="utf-8")
            seen = self.run_main("--map", "iceberg", "--descriptor", str(path))
        self.assertEqual(seen["barriers"], OPENED_BARRIERS)
        self.assertEqual(seen["port_layer_mask"], sum(1 << layer for layer in NAVMESH_BAKE_LAYERS))
        self.assertTrue(seen["keep_level_borders"])

    def test_the_plan_of_another_map_with_its_own_row_is_refused_for_iceberg(self):
        for map_key, plan in (("iceberg", "bowl"), ("bowl", "iceberg"), (None, "iceberg")):
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


class BakeCodeTests(unittest.TestCase):
    def test_the_level_border_name_prefix_does_not_drop_kept_cubes(self):
        # Iceberg names its 365 cubes "LevelBorder (N)" under "LevelBorders": the unconditional name rule of RoundOpenedReason
        # would remove every one of them although keep_level_borders and layer 29 let them in. Executed by
        # test_navmesh_source_layers (kept-border-iceberg-cube); this pins the production line so the pair cannot drift apart.
        text = BAKE_NAVMESH.read_text(encoding="utf-8")
        start = text.index("static string RoundOpenedReason(")
        body = text[start:start + 1600]
        self.assertIn('prefix == "LevelBorder" && ArenaPaths.Map.keep_level_borders', body)
        self.assertIn('static readonly string[] ExcludedNamePrefixes = { "SPAWN_BOUND", "LevelBorder" };', text)

    def test_the_python_model_applies_the_same_rule(self):
        self.assertEqual(pc.EXCLUDED_NAME_PREFIXES, ("SPAWN_BOUND", "LevelBorder"))
        self.assertEqual(pc.LEVEL_BORDER_PREFIX, "LevelBorder")


# ---------------------------------------------------------------------------------------------------------------------
# 2. the extracted package
# ---------------------------------------------------------------------------------------------------------------------
_CACHE: dict = {}


def colliders():
    if "cset" not in _CACHE:
        _CACHE["cset"] = pc.load_package_colliders(PACKAGE)
    return _CACHE["cset"]


def owner_box(cset, o):
    t = cset.tris[o.tri_start:o.tri_start + o.tri_count].reshape(-1, 3)
    return t.min(axis=0), t.max(axis=0)


def border_cubes(cset):
    return [o for o in cset.owners if o.layer == 29 and o.names[-1].startswith("LevelBorder")]


def kill_triggers(cset):
    return [o for o in cset.owners if o.layer == 29 and o.names[-1] == "KillTrigger"]


def package_markers():
    """(team, number, world position) of the 70 SpawnPointMarker boxes of the CheckPoint DesignStuff scene (parent SPAWNS)."""
    cset = colliders()
    scene = cset.scenes[DESIGN_STUFF]
    out = []
    for key, o in scene.objects.items():
        m = re.match(r"(Bear|Usec) house \((\d+)\)$", o["name"])
        if m and o.get("parent_id") is not None and scene.objects[o["parent_id"]]["name"] == "SPAWNS":
            w = scene.world(key)
            out.append((m.group(1), int(m.group(2)), (float(w[0, 3]), float(w[1, 3]), float(w[2, 3]))))
    return out


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/iceberg-native-v1 is not available (set ARENAMAPPORT_WORK_DIR)")
class PackageBorderTests(unittest.TestCase):
    def test_365_cubes_and_one_solid_kill_trigger_on_layer_29(self):
        cset = colliders()
        cubes, kills = border_cubes(cset), kill_triggers(cset)
        self.assertEqual((len(cubes), len(kills)), (365, 1))
        self.assertEqual([o for o in cset.owners if o.layer == 29 and o not in cubes and o not in kills], [])
        for o in cubes + kills:
            self.assertEqual((o.scene, o.kind, o.enabled, o.trigger, o.active), (DESIGN_STUFF, "BoxCollider", True, False, True))
        self.assertTrue(all(o.names[-3:-1] == ("Arena_Sanatorium_ChP_LevelBorders", "LevelBorders") for o in cubes))
        self.assertEqual(kills[0].names, ("KillTrigger",))                       # a scene root, not a child of the cube group

    def test_the_cube_aabb_is_inside_the_bake_box_with_a_margin(self):
        cset = colliders()
        boxes = [owner_box(cset, o) for o in border_cubes(cset)]
        lo = np.min([b[0] for b in boxes], axis=0)
        hi = np.max([b[1] for b in boxes], axis=0)
        np.testing.assert_allclose([lo[0], hi[0], lo[2], hi[2]], [-60.11, 119.53, -128.87, -34.86], atol=0.01)
        np.testing.assert_allclose([lo[1], hi[1]], [-18.84, 13.13], atol=0.01)
        x0, x1, _y0, _y1, z0, z1 = BAKE_BOUNDS
        self.assertGreaterEqual(lo[0] - x0, 1.5)
        self.assertGreaterEqual(x1 - hi[0], 1.4)
        self.assertGreaterEqual(lo[2] - z0, 2.0)
        self.assertGreaterEqual(z1 - hi[2], 1.8)

    def test_the_kill_trigger_box_lies_below_the_bake_floor(self):
        cset = colliders()
        lo, hi = owner_box(cset, kill_triggers(cset)[0])
        np.testing.assert_allclose(list(lo) + list(hi), [-53.02, -10.98, -70.69, -41.91, -6.14, -53.48], atol=0.01)
        self.assertLess(hi[1], BAKE_BOUNDS[2])           # its top face is below y -3: no island at the floor of the box
        # as a source it would be one: layer 29 is in the list and the model keeps it (keep_borders), only the y floor keeps it out
        o = kill_triggers(cset)[0]
        self.assertIsNone(pc.bake_exclusion_reason(o, keep_borders=True))
        self.assertEqual(pc.bake_exclusion_reason(o), "LevelBorder layer 29")

    def test_kept_cubes_are_sources_and_the_name_prefix_would_have_dropped_them_all(self):
        cset = colliders()
        cubes = border_cubes(cset)
        self.assertEqual({pc.bake_exclusion_reason(o) for o in cubes}, {"LevelBorder layer 29"})
        self.assertEqual({pc.bake_exclusion_reason(o, keep_borders=True) for o in cubes}, {None})
        # every one of the 365 names starts with the prefix of RoundOpenedReason, and so does the group above them
        self.assertTrue(all(o.names[-1].startswith("LevelBorder") and o.names[-2].startswith("LevelBorder") for o in cubes))
        mask = pc.layers_to_mask(NAVMESH_BAKE_LAYERS)
        kept = pc.source_owner_mask(cset, layer_mask=mask, keep_borders=True)
        self.assertEqual(sum(1 for o in cubes if kept[o.index]), 365)

    def test_cube_tops_are_islands_that_the_y_ceiling_removes(self):
        cset = colliders()
        cos48 = math.cos(math.radians(48.0))
        eroded = projected = 0.0
        ceiling = {7.0: 0.0, 8.0: 0.0, 10.0: 0.0}
        with_top = 0
        for o in border_cubes(cset):
            t = cset.tris[o.tri_start:o.tri_start + o.tri_count]
            n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
            nz = n[:, 1] / np.maximum(np.linalg.norm(n, axis=1), 1e-12)
            up = nz >= cos48
            if not up.any():
                continue
            with_top += 1
            projected += float((0.5 * np.abs(n[up][:, 1])).sum())
            tu = t[up].reshape(-1, 3)
            p = tu[:, [0, 2]] - tu[:, [0, 2]].mean(axis=0)
            _w, v = np.linalg.eigh(np.cov(p.T))
            ext = np.ptp(p @ v, axis=0)
            short, long_ = sorted(float(e) for e in ext)
            area = max(0.0, short - 0.6) * max(0.0, long_ - 0.6)       # the agent radius 0.3 erodes a top narrower than 0.6 m away
            eroded += area
            for limit in ceiling:
                if float(tu[:, 1].mean()) <= limit:
                    ceiling[limit] += area
        self.assertEqual(with_top, 365)
        self.assertAlmostEqual(projected, 857.7, delta=0.5)
        self.assertAlmostEqual(eroded, 564.3, delta=0.5)                # Bowl accepted about 608 m2 of such islands
        # a y ceiling of 8 leaves a sixth of it; 10 would keep most
        self.assertAlmostEqual(ceiling[8.0], 84.6, delta=0.5)
        self.assertLess(ceiling[8.0], 0.2 * eroded)
        self.assertGreater(ceiling[10.0], 0.8 * eroded)
        self.assertLess(ceiling[7.0], ceiling[8.0])
        self.assertEqual(BAKE_BOUNDS[3], 8)


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/iceberg-native-v1 is not available (set ARENAMAPPORT_WORK_DIR)")
class PackageGateTests(unittest.TestCase):
    def test_each_barrier_path_resolves_to_exactly_one_root_in_the_design_scene(self):
        cset = colliders()
        found, unresolved = pc.resolve_barriers(cset.scenes, OPENED_BARRIERS)
        self.assertEqual(unresolved, [])
        self.assertEqual([s for s, _k in found], [DESIGN_STUFF, DESIGN_STUFF])
        scene = cset.scenes[DESIGN_STUFF]
        self.assertEqual([scene.objects[k]["name"] for _s, k in found], ["GATES_A", "GATES_B"])
        doors = [k for k in scene.roots() if scene.objects[k]["name"] == "DOORS"]
        self.assertEqual(len(doors), 1)

    def test_the_gates_hold_16_and_26_colliders_and_nothing_below_them_is_a_frame(self):
        cset = colliders()
        pc.mark_barriers(cset, OPENED_BARRIERS)
        try:
            by_barrier = {"GATES_A": [], "GATES_B": []}
            for o in cset.owners:
                if o.names[:1] == ("DOORS",) and o.names[1] in by_barrier:
                    by_barrier[o.names[1]].append(o)
            self.assertEqual({k: len(v) for k, v in by_barrier.items()}, {"GATES_A": 16, "GATES_B": 26})
            for k, v in by_barrier.items():
                self.assertEqual(sorted({o.layer for o in v}), [12, 18], k)
                self.assertEqual(sum(1 for o in v if o.layer == 18), len(v) // 2, k)
                self.assertTrue(all(o.barrier_blocked for o in v), k)
                self.assertFalse(any("frame" in n.lower() for o in v for n in o.names), k)       # the legacy rule drops every part
            # two units in A, three plus the rotating gate in B: the direct children of the two roots
            scene = cset.scenes[DESIGN_STUFF]
            for name, children in (("GATES_A", ["sanatorium_resp_gate"] * 2),
                                   ("GATES_B", ["sanatorium_resp_gate"] * 3 + ["Sanatorium_CheckPoint_Gate"])):
                root = next(k for k, o in scene.objects.items() if o["name"] == name and scene.objects[o["parent_id"]]["name"] == "DOORS")
                self.assertEqual(sorted(scene.objects[c]["name"] for c in scene.children[root]), sorted(children), name)
            # the dropped total equals the 42 of the oracle run's exclusion table
            self.assertEqual(sum(1 for o in cset.owners if o.barrier_blocked), 42)
        finally:
            pc.mark_barriers(cset, [])

    def test_the_two_electric_engines_stand_above_head_height_beside_the_gate_and_stay(self):
        cset = colliders()
        engines = [o for o in cset.owners if o.names[:1] == ("DOORS",) and o.names[1].startswith("Sanatorium_Electric_engine")]
        self.assertEqual(len(engines), 4)                             # COLLIDER (18) + BALLISTIC (12) on each of the two props
        for o in engines:
            lo, hi = owner_box(cset, o)
            self.assertGreater(lo[1], 6.2)                           # 3 m above the pocket floor (y 3.25): no walkway through them
            self.assertLess(hi[1] - lo[1], 0.5)


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/iceberg-native-v1 is not available (set ARENAMAPPORT_WORK_DIR)")
class PackageLayerAuditTests(unittest.TestCase):
    def test_layer_census_of_the_live_colliders(self):
        cset = colliders()
        counts: dict = {}
        for o in cset.owners:
            counts[o.layer] = counts.get(o.layer, 0) + 1
        self.assertEqual(len(cset.owners), 12255)
        self.assertEqual(counts, {0: 16, 4: 1, 9: 63, 12: 6972, 13: 83, 18: 4686, 21: 22, 22: 5, 29: 366, 30: 41})

    def test_the_bake_source_table_of_the_decided_configuration(self):
        cset = colliders()
        pc.mark_barriers(cset, OPENED_BARRIERS)
        try:
            mask = pc.layers_to_mask(NAVMESH_BAKE_LAYERS)
            reasons: dict = {}
            for o in cset.owners:
                reason = pc.bake_exclusion_reason(o, keep_borders=True)
                if reason is None and not (mask >> o.layer) & 1:
                    reason = "not in navmesh_bake_layers"
                reasons[reason or "source"] = reasons.get(reason or "source", 0) + 1
            # the same table the oracle run prints (docs/progress/iceberg-release.md)
            self.assertEqual(reasons, {"SPAWN_BOUND*": 1, "Triggers layer 13": 83, "Water layer": 1, "not in navmesh_bake_layers": 6993,
                                       "opened_barriers": 42, "source": 5135})
            # what the layer list drops are ballistic (12), interactive (22), culling-mask (21) and Default (0) solids; what it keeps
            # is Arena's own set plus the cubes (the package has no live layer-11 collider; layer 13 is the Triggers rule)
            dropped = {o.layer for o in cset.owners if pc.bake_exclusion_reason(o, True) is None and not (mask >> o.layer) & 1}
            self.assertEqual(dropped, {0, 12, 21, 22})
            sources = pc.source_owner_mask(cset, layer_mask=mask, keep_borders=True)
            self.assertEqual({o.layer for o in cset.owners if sources[o.index]}, {9, 18, 29, 30})
        finally:
            pc.mark_barriers(cset, [])

    def test_the_play_space_holds_no_layer_zero_blockout(self):
        # AGENTS.md Traps "Layer-0 blockout": Arena's mask never sees layer 0, so a live layer-0 solid in the play space is a wall for the
        # player and air for the bake. Inside the cube footprint there are six; two stand on Arena's floor, the other four are cliff
        # rocks 16 to 38 m below the play space (see PackageArenaAuditTests for the footprint check).
        cset = colliders()
        boxes = [owner_box(cset, o) for o in border_cubes(cset)]
        x0, x1 = min(b[0][0] for b in boxes), max(b[1][0] for b in boxes)
        z0, z1 = min(b[0][2] for b in boxes), max(b[1][2] for b in boxes)
        rows = []
        for o in cset.owners:
            if o.layer != 0:
                continue
            lo, hi = owner_box(cset, o)
            if hi[0] >= x0 and lo[0] <= x1 and hi[2] >= z0 and lo[2] <= z1:
                rows.append((o.names[-1], round(float(lo[1]), 1)))
        self.assertEqual(sorted(rows), sorted([
            ("SPAWN_BOUND_FIGHTER (3)", 1.7),                        # dropped by name, disabled at raid start by SpawnPlacementRepairs
            ("Goshan_garbage_10_COLLIDER", 3.2),                     # a 0.6 x 0.6 x 0.7 m prop beside the Bear stage field: left alone
            ("Rock_group_12(Clone)", -30.9), ("Rock_group_14(Clone)", -28.5),
            ("Rock_group_11(Clone)", -37.3), ("Rock_group_11(Clone) (1)", -38.8),
        ]))

    def test_the_interactive_layer_holds_three_refill_cases_and_two_car_doors(self):
        # layer 22 (Interactive) is not in the layer list: the three ArenaPresetRefillContainer suitcases of REFILL_CASE and the
        # trunk/door boxes of two cars. Without the list the oracle's greedy step would list the two suitcases that lie on a route.
        cset = colliders()
        layer22 = [o for o in cset.owners if o.layer == 22]
        self.assertEqual(len(layer22), 5)
        self.assertEqual(sum(1 for o in layer22 if o.names[0] == "REFILL_CASE"), 3)
        self.assertEqual({o.names[0] for o in layer22 if o.names[0] != "REFILL_CASE"}, {"PREFAB_VARIANTS"})


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/iceberg-native-v1 is not available (set ARENAMAPPORT_WORK_DIR)")
class PackageAnchorTests(unittest.TestCase):
    def test_seventy_markers_forty_bear_thirty_usec_twenty_initial(self):
        markers = package_markers()
        self.assertEqual(len(markers), 70)
        self.assertEqual(sum(1 for m in markers if m[0] == "Bear"), 40)
        self.assertEqual(sum(1 for m in markers if m[0] == "Usec"), 30)
        initial = [m for m in markers if 5 <= m[1] <= 14]
        self.assertEqual((len(initial), sum(1 for m in initial if m[0] == "Bear")), (20, 10))
        bear = np.array([m[2] for m in initial if m[0] == "Bear"])
        usec = np.array([m[2] for m in initial if m[0] == "Usec"])
        self.assertTrue((bear[:, 0] > 89).all() and (bear[:, 0] < 98).all())         # the east pocket
        self.assertTrue((usec[:, 0] > -45).all() and (usec[:, 0] < -37).all())       # the west pocket

    def test_the_bake_box_holds_every_marker_capture_point_and_exit_with_room(self):
        x0, x1, y0, y1, z0, z1 = BAKE_BOUNDS
        points = [m[2] for m in package_markers()] + list(CAPTURE_POINTS.values()) + list(EXIT_CANDIDATES.values())
        arr = np.array(points)
        self.assertGreaterEqual(arr[:, 0].min() - x0, 3.0)
        self.assertGreaterEqual(x1 - arr[:, 0].max(), 3.0)
        self.assertGreaterEqual(arr[:, 2].min() - z0, 3.0)
        self.assertGreaterEqual(z1 - arr[:, 2].max(), 3.0)
        self.assertGreaterEqual(arr[:, 1].min() - y0, 3.0)
        self.assertGreaterEqual(y1 - arr[:, 1].max(), 3.0)          # the highest marker stands at y 4.99

    def test_the_exit_candidates_lie_outside_both_pockets_and_beyond_the_gates(self):
        # east exit x 72 < the east gates at x 82.4 < the pocket markers x 89..98; west exit x -30 > the west gates at x -36 > the pocket x -45..-37
        east, west = EXIT_CANDIDATES["east"], EXIT_CANDIDATES["west"]
        self.assertLess(east[0], 82.38 - 5.0)
        self.assertGreater(west[0], -36.04 + 3.0)


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/iceberg-native-v1 is not available (set ARENAMAPPORT_WORK_DIR)")
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
        # the census of tools/tests/test_puddle_water_census.py, read from the package instead of the staged sidecar
        self.assertEqual(self.water(), set(PUDDLE_WATER_MATERIAL_NAMES) | set(PUDDLE_EXCLUDED))
        self.assertEqual(set(PUDDLE_WATER_MATERIAL_NAMES) & set(PUDDLE_EXCLUDED), set())

    def test_the_rain_puddle_is_the_material_the_other_three_maps_list(self):
        material = self.materials["0877f2335044042e"]
        self.assertEqual((material["source_name"], material["keywords"]), ("puddle", ["USERAIN"]))
        users = self.users["0877f2335044042e"]
        self.assertEqual(len(users), 23)
        self.assertTrue(all(name.split(" (")[0] == "puddle2" and live and mesh == "2e93310068beccf4" for _s, name, live, mesh in users))
        self.assertEqual(sorted({s: sum(1 for u in users if u[0] == s) for s in {u[0] for u in users}}.items()),
                         [("Arena_Sanatorium", 14), ("Arena_Sanatorium_Light", 9)])

    def test_the_static_atlas_puddle_is_water_and_the_fuel_puddle_is_oil(self):
        atlas, fuel = self.materials["34f527e8925ffebb"], self.materials["299a841bb64d31bd"]
        self.assertEqual((atlas["source_name"], atlas["keywords"]), ("City_puddle_atlas_white", []))
        self.assertEqual((fuel["source_name"], fuel["keywords"]), ("puddle fuel", []))
        atlas_users, fuel_users = self.users["34f527e8925ffebb"], self.users["299a841bb64d31bd"]
        self.assertEqual(sorted(name for _s, name, _l, _m in atlas_users),
                         ["Puddle_atlas_01_LOD0", "Puddle_atlas_01_LOD0", "Puddle_atlas_03_LOD0", "Puddle_atlas_04_LOD0"])
        self.assertTrue(all(live for _s, _n, live, _m in atlas_users + fuel_users))
        self.assertEqual([name.split(" (")[0] for _s, name, _l, _m in fuel_users], ["puddle_oil"] * 3)    # the renderers say it too

    def test_no_other_shader_renders_a_puddle_decal(self):
        # the only water-ish shaders of the package: the decal shader (30 renderers in all) and the sea/pool FX/SimpleWater4
        names = {m.get("shader") for m in self.materials.values() if m.get("shader") and ("ater" in m["shader"] or "uddle" in m["shader"].lower())}
        self.assertEqual(names, {self.WATER_SHADER, "FX/SimpleWater4"})
        self.assertEqual(sum(len(self.users[m]) for m in self.water()), 30)


# ---------------------------------------------------------------------------------------------------------------------
# 3. the installed Arena
# ---------------------------------------------------------------------------------------------------------------------
def arena_mesh():
    if "mesh" not in _CACHE:
        _CACHE["mesh"], _CACHE["meta"] = nd.load_arena_mesh(nd.DEFAULT_ARENA_DATA, ICEBERG.assets, ICEBERG.path_id)
    return _CACHE["mesh"], _CACHE["meta"]


@unittest.skipUnless(HAVE_ARENA, "Arena 0.6.1 is not installed")
class ArenaMeshTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")
        cls.mesh, cls.meta = arena_mesh()

    def test_the_placement_is_the_identity_so_the_oracle_needs_no_yaw(self):
        # Bowl's NavMeshData is turned 90 degrees about Y (navmesh_oracle applies it); AirPit's and Iceberg's are the identity
        self.assertEqual((self.meta["position"], self.meta["rotation"]), ([0.0, 0.0, 0.0], [-0.0, -0.0, -0.0, 1.0]))
        self.assertTrue(self.mesh.placement.is_identity)
        settings = self.meta["settings"]
        self.assertAlmostEqual(settings["agentRadius"], 0.3, places=6)
        self.assertAlmostEqual(settings["agentHeight"], 1.7, places=6)
        self.assertAlmostEqual(settings["agentSlope"], 48.0, places=3)
        self.assertAlmostEqual(settings["agentClimb"], 0.38, places=6)

    def test_world_extent_components_and_area_of_the_main_component(self):
        import navmesh_oracle as no
        self.assertEqual(self.mesh.poly_count, 2473)
        components = no.component_summary(self.mesh)
        self.assertEqual((len(components), components[0]["polygons"]), (93, 1681))
        main = components[0]
        self.assertAlmostEqual(main["area_xz"], 4787.6, delta=0.5)
        for got, want in zip(main["min"] + main["max"], [-58.7, -1.80, -126.5, 108.7, 6.15, -34.8]):
            self.assertAlmostEqual(got, want, delta=0.06)
        # the x maximum is the edge of Arena's own 180 m NavMeshSurface volume (centre x 18.7), not of the play space: the Bear
        # pocket's tunnel runs on to x 118 inside the border cubes, which is why bake_bounds follows the cubes, not the mesh
        self.assertAlmostEqual(main["max"][0], 18.7 + 90.0, delta=0.06)
        # the other components inside the play box: a 149 m2 roof at y 11..13 above the bake ceiling, ledges and cliff scraps far below the
        # bake floor, and six small pieces inside the box (the largest 31 m2, a raised yard strip at x 82..87)
        inside = [c for c in components if c["min"][0] >= -61 and c["max"][0] <= 121 and c["min"][2] >= -130 and c["max"][2] <= -33]
        self.assertEqual((inside[0]["label"], inside[1]["label"]), (0, 1))
        self.assertAlmostEqual(inside[1]["area_xz"], 149.4, delta=0.5)
        self.assertGreater(inside[1]["min"][1], BAKE_BOUNDS[3])
        within = [c for c in inside[1:] if c["min"][1] >= BAKE_BOUNDS[2] and c["max"][1] <= BAKE_BOUNDS[3]]
        self.assertEqual(sorted(round(c["area_xz"], 1) for c in within), [1.3, 2.2, 2.4, 3.2, 25.3, 31.2])
        self.assertEqual(len(inside) - 1 - len(within), 31)                      # the rest lies above y 8 or below y -3

    def test_the_surface_decoded_from_level377_is_the_table_row(self):
        import probe_mode_toggler as probe
        surfaces = probe.scene_census(nd.DEFAULT_ARENA_DATA, ICEBERG.ai_scene_index)["surfaces"]
        self.assertEqual(len(surfaces), 1)
        self.assertEqual(surfaces[0]["layer_mask"], ICEBERG.layer_mask)
        self.assertEqual(surfaces[0]["layer_mask_bits"], ARENA_LAYERS)
        self.assertEqual(surfaces[0]["navmesh_data"]["path_id"], ICEBERG.path_id)
        self.assertEqual(surfaces[0]["use_geometry"], "PhysicsColliders")     # layers of the COLLIDER objects, as the port bake reads them
        self.assertEqual(surfaces[0]["collect_objects"], "Volume")
        self.assertEqual((surfaces[0]["center"], surfaces[0]["size"]), ([18.700000762939453, 4.0, -73.0], [180.0, 80.0, 180.0]))

    def test_the_core_points_lie_on_the_main_component_at_the_mesh_floor(self):
        points = nd.live_ai_points(nd.DEFAULT_ARENA_DATA, ICEBERG.ai_scene_index, ICEBERG.ai_scene_name, ICEBERG.key)
        core = np.array([p["position"] for p in points if p["kind"] == "core_point"])
        self.assertEqual((len(core), sum(1 for p in points if p["kind"] == "patrol_hub")), (11, 4))
        poly, surf = self.mesh.locate(core, max_dy=1.5)
        self.assertTrue((poly >= 0).all() and (self.mesh.labels[poly] == 0).all())
        self.assertLess(float(np.abs(surf - core[:, 1]).max()), 0.5)         # Bowl's anchors stand within 0.1 m; Iceberg's up to 0.47 m off
        # the four patrol hubs (and their 40 points) stand at y -3.9, 30 m and more outside the play area: off Arena's own mesh
        hubs = np.array([p["position"] for p in points if p["kind"] in ("patrol_hub", "patrol_point")])
        self.assertEqual(len(hubs), 44)
        self.assertTrue((hubs[:, 2] > 0).all() and (hubs[:, 1] < -3.0).all())
        self.assertTrue((self.mesh.locate(hubs, max_dy=1.5)[0] < 0).all())

    def test_the_mesh_runs_through_the_closed_gates(self):
        # Arena baked with the gates open: the closed motor gate (a 4 m sheet at z -75.66) lies on its main component along its
        # whole width, and the sliding gates of both roots at their base height on part of theirs (the rest is post and wall).
        if not HAVE_PACKAGE:
            self.skipTest("work/extract/iceberg-native-v1 is not available")
        cset = colliders()
        rows = {}
        for o in cset.owners:
            if o.scene == DESIGN_STUFF and o.layer == 18 and o.names[0] == "DOORS" and o.names[-1] in (
                    "sanatorium_resp_gate_COLLIDER", "Sanatorium_CheckPoint_Gate_COLLIDER"):
                lo, hi = owner_box(cset, o)
                ext = hi - lo
                axis = 0 if ext[0] > ext[2] else 2
                pts = []
                for f in np.linspace(0.1, 0.9, 5):
                    p = 0.5 * (lo + hi)
                    p[axis] = lo[axis] + f * ext[axis]
                    p[1] = lo[1] + 0.1
                    pts.append(p)
                poly, _surf = self.mesh.locate(np.array(pts), max_dy=0.6)
                self.assertEqual(set(self.mesh.labels[poly[poly >= 0]].tolist()) - {0}, set())
                rows.setdefault((o.names[1], o.names[-1]), []).append(int((poly >= 0).sum()))
        self.assertEqual({k: len(v) for k, v in rows.items()}, {("GATES_A", "sanatorium_resp_gate_COLLIDER"): 2,
                                                                  ("GATES_B", "sanatorium_resp_gate_COLLIDER"): 3,
                                                                  ("GATES_B", "Sanatorium_CheckPoint_Gate_COLLIDER"): 1})
        self.assertEqual(rows[("GATES_B", "Sanatorium_CheckPoint_Gate_COLLIDER")], [5])
        self.assertTrue(all(n >= 2 for v in rows.values() for n in v), rows)


@unittest.skipUnless(HAVE_ARENA and HAVE_PACKAGE, "the installed Arena or the extracted package is not available")
class PackageArenaAuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")
        cls.mesh, _meta = arena_mesh()
        cls.cset = colliders()

    def cells_on_mesh(self, lo, hi):
        """How many of 25 points over the box have the main component's floor inside the box's height span: the solid stands where
        Arena's agents walk."""
        xs, zs = np.linspace(lo[0], hi[0], 5), np.linspace(lo[2], hi[2], 5)
        pts = np.array([(x, 0.5 * (lo[1] + hi[1]), z) for x in xs for z in zs])
        poly, surf = self.mesh.locate(pts, max_dy=0.5 * (hi[1] - lo[1]) + 1.0)
        main = (poly >= 0) & (self.mesh.labels[np.clip(poly, 0, None)] == 0)
        return int((main & (surf >= lo[1] - 0.3) & (surf <= hi[1])).sum())

    def test_the_markers_stand_on_the_main_component_except_six_bear_stage_markers(self):
        markers = package_markers()
        pos = np.array([m[2] for m in markers])
        poly, surf = self.mesh.locate(pos, max_dy=1.5)
        on = poly >= 0
        self.assertTrue((self.mesh.labels[poly[on]] == 0).all())
        self.assertEqual(sorted((m[0], m[1]) for m, ok in zip(markers, on) if not ok),
                         [("Bear", 38), ("Bear", 47), ("Bear", 48), ("Bear", 49), ("Bear", 50), ("Bear", 51)])
        initial = [i for i, m in enumerate(markers) if 5 <= m[1] <= 14]
        self.assertTrue(all(on[i] for i in initial))                              # all 20 pocket markers are on Arena's main component
        self.assertLess(max(abs(float(surf[i]) - float(pos[i, 1])) for i in initial), 0.6)

    def test_layer_twelve_solids_without_a_movement_twin_never_touch_arenas_footprint(self):
        cset = self.cset
        owners = cset.owners
        n = len(owners)
        lo = np.zeros((n, 3)); hi = np.zeros((n, 3))
        for o in owners:
            lo[o.index], hi[o.index] = owner_box(cset, o)
        layer = np.array([o.layer for o in owners])
        cubes = np.zeros(n, dtype=bool)
        for o in border_cubes(cset) + kill_triggers(cset):
            cubes[o.index] = True
        x0, x1 = lo[cubes][:, 0].min(), hi[cubes][:, 0].max()
        z0, z1 = lo[cubes][:, 2].min(), hi[cubes][:, 2].max()
        box = (hi[:, 0] >= x0) & (lo[:, 0] <= x1) & (hi[:, 2] >= z0) & (lo[:, 2] <= z1)
        mv = np.nonzero(box & np.isin(layer, [9, 11, 18, 30]) & ~cubes)[0]
        l12 = np.nonzero(box & (layer == 12))[0]
        self.assertEqual((len(l12), len(mv)), (6518, 4390))

        def vol(a, b):
            d = np.maximum(b - a, 1e-3)
            return d[..., 0] * d[..., 1] * d[..., 2]

        orphans = []
        for i in l12:
            ilo, ihi = np.maximum(lo[mv], lo[i]), np.minimum(hi[mv], hi[i])
            ok = (ihi > ilo).all(axis=1)
            if not ok.any() or (vol(ilo[ok], ihi[ok]) / np.minimum(vol(lo[mv][ok], hi[mv][ok]), vol(lo[i], hi[i]))).max() < 0.6:
                orphans.append(i)
        self.assertEqual(len(orphans), 37)
        self.assertEqual(max(self.cells_on_mesh(lo[i], hi[i]) for i in orphans), 0)

    def test_only_the_spawn_bound_and_one_prop_of_the_layer_zero_solids_stand_on_arenas_floor(self):
        cset = self.cset
        stand = {}
        for o in cset.owners:
            if o.layer == 0:
                lo, hi = owner_box(cset, o)
                cells = self.cells_on_mesh(lo, hi)
                if cells:
                    stand[o.names[-1]] = cells
        # the SPAWN_BOUND box is dropped by name and disabled at raid start; the 0.6 m garbage prop is left alone (a bot may brush it)
        self.assertEqual(sorted(stand), ["Goshan_garbage_10_COLLIDER", "SPAWN_BOUND_FIGHTER (3)"])

    def test_arenas_cover_holes_belong_to_the_old_barricade_layout(self):
        # Arena 0.6.0 replaced the CheckPoint cover (366-369 -> 375-378, "the old barricade groups are gone, BARRICADE_03..37 are new"),
        # and Arena's NavMeshData still has the old holes: a cover-sized layer-18 collider of the 0.5.4 scene sits on the mesh at
        # floor level in 2 percent of the cases (hole respected), one of the 0.6.1 scene in 36 percent (walked through). So the
        # oracle's "Arena walks through BARRICADE_NN" list (48 SOO + 13 OO instances) is staleness, not an opened gate: the port bake
        # keeps the new cover as the walls the player collides with.
        def cover_on_mesh(cset, scene):
            total = hit = 0
            for o in cset.owners:
                if o.scene != scene or o.layer != 18:
                    continue
                lo, hi = owner_box(cset, o)
                ext = hi - lo
                if ext[1] < 0.5 or ext[1] > 4 or max(ext[0], ext[2]) > 12:
                    continue
                c = 0.5 * (lo + hi)
                poly, _ = self.mesh.locate(np.array([[c[0], lo[1] + 0.3, c[2]]]), max_dy=0.9)
                total += 1
                hit += int(poly[0] >= 0)
            return total, hit

        self.assertEqual(cover_on_mesh(self.cset, "Arena_Sanatorium_CheckPoint"), (193, 70))
        if not (SPIKE_PACKAGE / "scenes" / "Arena_Sanatorium_CheckPoint.json").is_file():
            return
        spike = pc.load_package_colliders(SPIKE_PACKAGE)
        self.assertEqual(cover_on_mesh(spike, "Arena_Sanatorium_CheckPoint"), (155, 3))


if __name__ == "__main__":
    unittest.main()
