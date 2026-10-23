"""AirPit NavMesh bake inputs (docs/PLAN-AIRPIT.md item 8): opened_barriers, navmesh_bake_exclusions, bake_bounds,
navmesh_bake_layers in tools/maps/airpit.json.

Why: the Unity bake is a 15-minute batch run, so everything checkable offline is pinned here. The descriptor
validator keeps the sidecar and ArenaMapRegistry.AirPit in step and rejects malformed values; the sidecar keeps
Equator and Bay5 free of the new keys (their bundles are not rebuilt); when the ignored 0.6.0 package is present,
every path in the lists resolves to exactly the object the NavMesh model flagged, and the two barrier roots are
shaped the way both consumers (the client opens direct children, the bake drops descendants) agree on. The C# side
is ArenaMapPort.Server.Tests (AirPitMapTests, MapDescriptorConsistencyTests) and test_navmesh_source_layers.py
(BakeNavMesh's pure helpers, executed from the production text); the offline model is test_navmesh_diff.py.

Since 2026-10-23 the file also holds the door-pose side of the bake (docs/progress/door-poses.md, "NavMesh bake"): Equator and
Bay5's barrier lists, the Python mirror of BakeNavMesh.ResolveOpenedBarriers (BarrierBake) and its runs over the real packages,
which reproduce the last real bake logs for the old configuration and count what the new rule changes.
"""
import copy
import json
import math
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
import validate_map_descriptors as VD  # noqa: E402

# Host raid 1 (2026-10-12) retired the four bake-only cutters (DonB_Ramen_closed x2, TH_Roll_door_01_closed,
# fence_ema_01_white (18)): a NavMesh through shutters that stay solid walked the player into one. Every solid is baked.
EXCLUSIONS: list = []
# The two metal-detector portal gates of the z 50.5 facade (closed fence active, swung-open halves inactive) seal the
# north spawn field and the east wing from the hall; they are opened like the two round-start gate roots.
GATES = [
    "SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (2)",
    "SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (3)",
]
GATE_XZ = [(-23.50, 50.54), (-0.38, 50.54)]
# The three GateEntity roots of Arena's TeamFight round start. BUS_DOORS (the south team box) opens at raid start unless
# a mode plugin holds it (start_gates). PASSPORT_CONTROL_DOORS + PASSPORT_CONTROL_EMISSIVE (the north team box: 16 lane
# turnstiles and the red lane sheets) are held_gates since 2026-10-17: closed at raid start in every mode, sealed for
# bots by SpawnGates' carvers, opened only by SpawnGates.Release. Since package v8 the furnished passport booth row
# fills the floor between the lanes, so the turnstiles are the only way through (host raid 5 of 2026-10-12 saw open
# floor there on package v6). All three leave the bake like every opened barrier.
ROOT_BARRIERS = ["BUS_DOORS", "PASSPORT_CONTROL_DOORS", "PASSPORT_CONTROL_EMISSIVE"]
START_GATES = ["BUS_DOORS"]
HELD_GATES = ["PASSPORT_CONTROL_DOORS", "PASSPORT_CONTROL_EMISSIVE"]
# The six closed shop shells of the ShootOut build (2026-10-12, furnished shops): level324 imports the four shop
# interiors only as shutters/shells (Airport_Shop_BS_closed_closed, DonB_Ramen_closed x2, TH_Roll_door_01..03_closed,
# all under the first BUILDING sibling). The furnished shops come from level38 (Arena_Airpit_Main_hall) with the
# rolled-up shutters, so the closed shells are opened at raid start (BoothDoorRepairs) and left out of the bake;
# extraction keeps them (dropping them would change level324's accepted JSON).
SHOP_SHELLS = [
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/Airport_Shop_BS_closed_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed (1)",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_01_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_02_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_03_closed",
]
SHELL_LIVE_COLLIDER_LAYERS = [[12, 12, 12, 18], [12, 18], [12, 18], [12, 18], [12, 18], [12, 18]]   # 14 colliders: 8 ballistic, 6 movement
# Three white blockout panels (layer 0 Default, 12.7-14 x 3.3 x 0.16 m, MeshRenderer + BoxCollider) that the ShootOut build lays
# over the south-west shop's three roll-door lines (z -10.15; x -36.8 at z -16.6 and -33.75) on top of the shells. Arena's
# bake mask never saw them (layer 0 is not in 9/11/13/18/30), so the U6 NavMesh ran through them while the player collided
# (host raid 2026-10-13, F10 at (-44, -7.5) facing south). They are opened like the shells; the bake does not change.
COVER_CUBES = ["SBG_Arena_AirPit_ShootOut/SOO/Cube (10) Group"]
BARRIERS = ROOT_BARRIERS + GATES + SHOP_SHELLS + COVER_CUBES
# Paths under the duplicated BUILDING sibling, kept to pin the resolver's Transform.Find semantics (the shop shells above are inputs again).
BUILDING_PATHS = [
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_01_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed (1)",
]
ARENA_LAYERS = [9, 11, 13, 18, 30]            # NavMeshSurface mask 0x40042A00 of Arena's three AI scenes
# The AABB of the authored borders (Arena's TeamFight outline) rounded outwards; was x -80..56, z -96..92 until host
# raid 4 (2026-10-12) showed the mesh running 7 m into the unlit entrance hall north of the facade, and cut at z 70
# from 2a1e0ce (2026-10-12) to 2026-10-17, when the original north team box (z 69.16..85.16) needed its pocket meshed
# again (the held passport gate keeps bots out of it in plain raids).
BOUNDS = [-57, 37, -2, 8, -83, 83]
PACKAGE = REPO / "work" / "extract" / "airpit-native-v9"   # v8 + the three SOO crates under the tarp (2026-10-17); v6/v7/v8 are archives
HAVE_PACKAGE = (PACKAGE / "scenes" / "Arena_AirPit_ShootOut.json").is_file()
ARENA_NAVMESH = Path("D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/sharedassets33.assets")


def sidecar(name):
    return json.loads((REPO / "tools" / "maps" / name).read_text(encoding="utf-8"))


class SidecarContentTests(unittest.TestCase):
    def setUp(self):
        self.map = sidecar("airpit.json")

    def test_airpit_lists(self):
        self.assertEqual(self.map["opened_barriers"], BARRIERS)
        self.assertEqual(self.map["start_gates"], START_GATES)
        self.assertEqual(self.map["held_gates"], HELD_GATES)
        self.assertEqual(self.map["navmesh_bake_exclusions"], EXCLUSIONS)
        self.assertEqual(self.map["bake_bounds"], BOUNDS)
        self.assertEqual(self.map["navmesh_bake_layers"], ARENA_LAYERS)

    def test_released_maps_keep_their_bake_unchanged(self):
        for name in ("equator.json", "bay5.json"):
            data = sidecar(name)
            for key in ("navmesh_bake_exclusions", "bake_bounds", "navmesh_bake_layers", "start_gates", "held_gates"):
                self.assertNotIn(key, data, f"{name} must not carry {key}")
        # 2026-10-23 (docs/progress/equator-booth-spawns.md): Equator's FCK shutter is a wall again (no opened barriers), Bay5 opens
        # every gate Arena moves plus Door_01 (3): four warehouse rollers and the depot frame (listed once, two instances).
        self.assertEqual(sidecar("equator.json")["opened_barriers"], [])
        self.assertEqual(sidecar("bay5.json")["opened_barriers"], [
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)",
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)",
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)",
            "WAREHOUSE_DOORS (1)/One_man_Door",
            "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame",
        ])

    def test_no_other_sidecar_uses_the_new_keys(self):
        # AirPit and, since PLAN-BOWL item 7 (2026-10-18), Bowl carry bake inputs and gate roles; no other sidecar does.
        bowl = sidecar("bowl.json")
        self.assertEqual(bowl["opened_barriers"], ["DOORS_SPAWN_A", "DOORS_SPAWN_B"])
        self.assertEqual(bowl["start_gates"], bowl["opened_barriers"])
        self.assertEqual(bowl["held_gates"], [])
        self.assertEqual(bowl["navmesh_bake_exclusions"], ["Arena_Bowl_LevelBorders/Cube"])
        self.assertEqual(bowl["bake_bounds"], [-66, 62, -2, 10, -82, 11])
        self.assertEqual(bowl["navmesh_bake_layers"], [9, 11, 18, 29, 30])
        self.assertIs(bowl["keep_level_borders"], True)
        for path in sorted((REPO / "tools" / "maps").glob("*.json")):
            if "." in path.stem or path.stem in ("airpit", "bowl"):
                continue
            data = json.loads(path.read_text(encoding="utf-8"))
            for key in ("navmesh_bake_exclusions", "bake_bounds", "navmesh_bake_layers", "start_gates", "held_gates"):
                self.assertNotIn(key, data, f"{path.name} must not carry {key}")

    def test_exclusions_are_disjoint_from_the_barriers(self):
        for path in EXCLUSIONS:
            self.assertFalse(any(path == b or path.startswith(b + "/") for b in BARRIERS), path)

    def test_bounds_hold_every_spawn_and_exit_with_room(self):
        # 3 m of mesh around every point; the box is the walls' AABB now, so the old 20/15 m margins no longer apply.
        x0, x1, y0, y1, z0, z1 = self.map["bake_bounds"]
        bots, exits = sidecar("airpit.bots.json"), sidecar("airpit.exits.json")
        points = [p["position"] for p in bots["player_spawns"] + bots["bot_spawns"]] + [e["position"] for e in exits["exits"]]
        self.assertEqual(len(points), 20)
        for x, y, z in points:
            self.assertTrue(x0 + 3 <= x <= x1 - 3 and z0 + 3 <= z <= z1 - 3 and y0 < y < y1, (x, y, z))

    def test_bounds_hold_every_team_box_marker_with_room(self):
        # 2026-10-17: the original north team box lives behind the passport line; its ten markers (and the south ten)
        # must stay inside the bake volume with 2.5 m of mesh around them (the northernmost, ScavPMCspawn (8), stands 2.9 m
        # from the z 83 edge), or the box is unusable before any rebake shows it.
        x0, x1, y0, y1, z0, z1 = self.map["bake_bounds"]
        modes = sidecar("airpit.modes.json")
        markers = modes["spawn_markers"]["north"] + modes["spawn_markers"]["south"]
        self.assertEqual(len(markers), 20)
        for m in markers:
            x, y, z = m["position"]
            self.assertTrue(x0 + 2.5 <= x <= x1 - 2.5 and z0 + 2.5 <= z <= z1 - 2.5 and y0 < y < y1, (m["name"], x, y, z))

    def test_authored_borders_form_a_closed_outline_inside_the_bake_box(self):
        # AuthoredBorderRepairs raises one invisible wall per entry; the ring is Arena's TeamFight outline (the
        # ExcludeLevelBorders polygon of its culling scene) with the north edge pulled onto the facade line z 82.5.
        borders = self.map["authored_borders"]
        self.assertEqual(len(borders), 12)
        self.assertEqual(len({b["name"] for b in borders}), 12)
        for i, b in enumerate(borders):
            nxt = borders[(i + 1) % len(borders)]
            self.assertEqual(b["to"], nxt["from"], f"{b['name']} does not meet {nxt['name']}")
            self.assertLess(b["y"][0], b["y"][1])
            self.assertGreaterEqual(math.hypot(b["to"][0] - b["from"][0], b["to"][1] - b["from"][1]), 1.0, b["name"])
        xs = [c for b in borders for c in (b["from"][0], b["to"][0])]
        zs = [c for b in borders for c in (b["from"][1], b["to"][1])]
        x0, x1, _y0, _y1, z0, z1 = self.map["bake_bounds"]
        for bound, extreme in ((x0, min(xs)), (x1, max(xs)), (z0, min(zs)), (z1, max(zs))):
            self.assertLessEqual(abs(bound - extreme), 1.0, (bound, extreme))   # the box hugs the outline to within 1 m
        facade = [b for b in borders if b["name"] == "north-facade-gap"][0]
        self.assertEqual((facade["from"], facade["to"]), ([-28.5, 82.5], [4.6, 82.5]))   # the 32 m opening at z 82.47
        for name in ("equator.json", "bay5.json"):
            self.assertNotIn("authored_borders", sidecar(name))

    @unittest.skipUnless(ARENA_NAVMESH.is_file(), "installed Arena not present")
    def test_bounds_contain_arenas_play_space_component(self):
        import navmesh_diff as nd
        mesh, _meta = nd.load_arena_mesh()
        main = nd.oracle.component_summary(mesh)[0]
        x0, x1, y0, y1, z0, z1 = self.map["bake_bounds"]
        self.assertTrue(x0 < main["min"][0] and main["max"][0] < x1, (main["min"], main["max"]))
        # Arena's north edge is ragged (z 82.2..84.8 behind the facade-gap wall at z 82.5); the box ends 0.5 m past the wall.
        self.assertTrue(z0 < main["min"][2] and main["max"][2] < z1 + 2.0, (main["min"], main["max"], z1))
        self.assertTrue(y0 < main["min"][1] and main["max"][1] < y1, (main["min"], main["max"]))


class ValidatorTests(unittest.TestCase):
    def test_the_shipped_descriptors_are_consistent(self):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        args = VD.registry_entries(text)["AirPit"]
        scope = {"ArenaEquatorLocation": {}}
        self.assertEqual(VD.resolve(args["OpenedBarriers"], scope), BARRIERS)
        self.assertEqual(VD.resolve(args["StartGates"], scope), START_GATES)
        self.assertEqual(VD.resolve(args["HeldGates"], scope), HELD_GATES)
        self.assertNotIn("NavMeshBakeExclusions", args)   # omitted on purpose: the descriptor defaults to an empty list
        self.assertEqual(VD.resolve(args["BakeBounds"], scope), BOUNDS)
        self.assertEqual(VD.resolve(args["NavMeshBakeLayers"], scope), ARENA_LAYERS)
        for name in ("Equator", "Bay5"):
            for field in ("NavMeshBakeExclusions", "BakeBounds", "NavMeshBakeLayers", "StartGates", "HeldGates"):
                self.assertNotIn(field, VD.registry_entries(text)[name])

    def check(self, mutate):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        entry = VD.registry_entries(text)["AirPit"]
        data = copy.deepcopy(sidecar("airpit.json"))
        mutate(data)
        errors: list = []
        VD.check_map_against_registry_entry(data, entry, {"ArenaEquatorLocation": {}}, Path("airpit.json"), "AirPit", errors)
        return errors

    def test_untouched_sidecar_is_clean(self):
        self.assertEqual(self.check(lambda d: None), [])

    def test_sidecar_drift_from_the_runtime_descriptor_is_reported_per_field(self):
        self.assertTrue(any("bake_bounds mismatch" in e for e in self.check(lambda d: d["bake_bounds"].__setitem__(1, 57))))
        self.assertTrue(any("navmesh_bake_layers mismatch" in e for e in self.check(lambda d: d["navmesh_bake_layers"].pop())))
        self.assertTrue(any("navmesh_bake_exclusions mismatch" in e
                            for e in self.check(lambda d: d["navmesh_bake_exclusions"].append("SBG_Arena_AirPit_ShootOut/SOO/x"))))
        self.assertTrue(any("opened_barriers mismatch" in e for e in self.check(lambda d: d.pop("opened_barriers"))))
        self.assertTrue(any("start_gates mismatch" in e
                            for e in self.check(lambda d: d["start_gates"].append("SBG_Arena_AirPit_ShootOut/SOO/Cube (10) Group"))))
        self.assertTrue(any("held_gates mismatch" in e for e in self.check(lambda d: d["held_gates"].pop())))
        self.assertTrue(any("authored_borders mismatch" in e
                            for e in self.check(lambda d: d["authored_borders"][0].__setitem__("y", [-1.0, 11.0]))))

    def test_malformed_values_are_rejected(self):
        def errors(**changes):
            return VD.bake_input_errors({**sidecar("airpit.json"), **changes})

        self.assertEqual(errors(), [])
        for bad in ([-80, 56, -2, 8, -96], [-80, 56, -2, 8, -96, 92, 1], [56, -80, -2, 8, -96, 92], [-80, 56, 8, 8, -96, 92],
                    [-80, 56, -2, 8, -96, math.inf], [-80, 56, -2, 8, -96, True], "x", {}):
            self.assertTrue(any("bake_bounds" in e for e in errors(bake_bounds=bad)), bad)
        for bad in ([], [32], [-1], [18, 18], [True], [18.0], "18", [4, 18]):
            self.assertTrue(any("navmesh_bake_layers" in e for e in errors(navmesh_bake_layers=bad)), bad)
        for name in ("opened_barriers", "navmesh_bake_exclusions", "start_gates", "held_gates"):
            for bad in ("a/b", [""], ["a//b"], ["/a"], ["a/"], [" a"], [3], ["a/b", "a/b"]):
                self.assertTrue(any(name in e for e in errors(**{name: bad})), (name, bad))
        # a gate must be an opened barrier (the bake drops it through that list) and cannot be both start and held
        self.assertTrue(any("start_gates must be a subset" in e for e in errors(start_gates=["NOT_A_BARRIER"])))
        self.assertTrue(any("held_gates must be a subset" in e for e in errors(held_gates=["NOT_A_BARRIER"])))
        self.assertTrue(any("overlap" in e for e in errors(held_gates=["BUS_DOORS"])))
        self.assertTrue(any("held_gates without start_gates" in e for e in errors(start_gates=[])))
        self.assertEqual(errors(start_gates=[], held_gates=[]), [])
        good = {"name": "n", "from": [0, 0], "to": [1, 0], "y": [0, 2]}
        for bad in ("x", [{}], [{**good, "name": " n"}], [{**good, "from": [0]}], [{**good, "to": [0, 0.2]}],
                    [{**good, "y": [2, 0]}], [{**good, "from": [math.inf, 0]}], [good, good]):
            self.assertTrue(any("authored_borders" in e for e in errors(authored_borders=bad)), bad)
        self.assertEqual(errors(authored_borders=[good]), [])
        overlap = errors(navmesh_bake_exclusions=["BUS_DOORS/Apron_bus_MAZ_doors"])
        self.assertTrue(any("inside an opened_barriers path" in e for e in overlap))
        self.assertTrue(any("inside an opened_barriers path" in e for e in errors(navmesh_bake_exclusions=["BUS_DOORS"])))

    def test_keep_level_borders_needs_layer_29_in_an_explicit_layer_list(self):
        # Bowl (docs/PLAN-BOWL.md): a map that keeps its layer-29 LevelBorder cubes bakes them as walls, so an explicit
        # navmesh_bake_layers list must contain 29; no list (every layer but Water) is fine, and the flag is a boolean.
        self.assertEqual(VD.bake_input_errors({"keep_level_borders": True}), [])
        self.assertEqual(VD.bake_input_errors({"keep_level_borders": False, "navmesh_bake_layers": [9, 11, 18, 30]}), [])
        self.assertEqual(VD.bake_input_errors({"keep_level_borders": True, "navmesh_bake_layers": [9, 11, 18, 29, 30]}), [])
        self.assertTrue(any("include layer 29" in e for e in
                            VD.bake_input_errors({"keep_level_borders": True, "navmesh_bake_layers": [9, 11, 18, 30]})))
        self.assertTrue(any("boolean" in e for e in VD.bake_input_errors({"keep_level_borders": "yes"})))

    def test_a_map_without_bake_inputs_is_valid(self):
        self.assertEqual(VD.bake_input_errors({}), [])
        self.assertEqual(VD.bake_input_errors(sidecar("equator.json")), [])
        self.assertEqual(VD.bake_input_errors(sidecar("bay5.json")), [])


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/airpit-native-v9 not present (furnished hall plus the crates under the tarp)")
class PackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import port_colliders as pc
        cls.pc = pc
        cls.scenes = {}
        for f in sorted((PACKAGE / "scenes").glob("*.json")):
            scene = pc.Scene(json.loads(f.read_text(encoding="utf-8")))
            cls.scenes[scene.name] = scene

    def subtree(self, scene, key):
        out = [key]
        for child in scene.children.get(key, []):
            out += self.subtree(scene, child)
        return out

    def live_colliders(self, scene, keys):
        out = []
        for key in keys:
            for comp in scene.objects[key].get("components", []):
                if comp["type"] in ("MeshCollider", "BoxCollider", "SphereCollider", "CapsuleCollider") \
                        and comp.get("enabled", True) and not comp.get("is_trigger", False) and scene.chain_active(key):
                    out.append((key, scene.objects[key]["layer"]))
        return out

    def test_each_portal_gate_is_a_closed_fence_with_inactive_open_halves(self):
        # The gate object itself carries no collider; its only active child is the closed fence (movement
        # collider on LowPolyCollider 18 plus the ballistic twin on 12), the two swung-open halves are inactive.
        # BoothDoorRepairs deactivates the direct children (none is called frame), so the portal frame
        # (Entrance_portal, a separate object) stays and the passage opens; the bake drops the fence colliders.
        for path, (x, z) in zip(GATES, GATE_XZ):
            found, unresolved = self.pc.resolve_barriers(self.scenes, [path])
            self.assertEqual((len(found), unresolved), (1, []), path)
            scene_name, key = found[0]
            self.assertEqual(scene_name, "Arena_AirPit_ShootOut")
            scene = self.scenes[scene_name]
            self.assertNotIn(key, scene.roots())
            self.assertTrue(scene.chain_active(key))
            world = scene.world(key)
            self.assertAlmostEqual(world[0, 3], x, delta=0.05, msg=path)
            self.assertAlmostEqual(world[2, 3], z, delta=0.05, msg=path)
            children = {scene.objects[c]["name"]: c for c in scene.children[key]}
            self.assertEqual(set(children), {"Prison_Metal_Fence", "Prison_Metal_Fence_PART_L", "Prison_Metal_Fence_PART_R"}, path)
            self.assertTrue(scene.objects[children["Prison_Metal_Fence"]]["active"], path)
            self.assertFalse(scene.objects[children["Prison_Metal_Fence_PART_L"]]["active"], path)
            self.assertFalse(scene.objects[children["Prison_Metal_Fence_PART_R"]]["active"], path)
            self.assertEqual(self.live_colliders(scene, [key]), [], "the gate object itself must not be a source")
            layers = sorted(layer for _k, layer in self.live_colliders(scene, self.subtree(scene, key)))
            self.assertEqual(layers, [12, 18], path)
            self.assertFalse([k for k in self.subtree(scene, key) if "frame" in scene.objects[k]["name"].lower()], path)

    def test_building_paths_resolve_to_the_first_duplicated_sibling(self):
        # SOO_LOD0 holds two siblings named BUILDING (52 and 8 children). Transform.Find takes the first one,
        # which holds all three BUILDING paths, so BakeNavMesh and this model resolve them to the same objects.
        scene = self.scenes["Arena_AirPit_ShootOut"]
        (lod0,) = [k for k, o in scene.objects.items() if o["name"] == "SOO_LOD0"]
        buildings = [c for c in scene.children[lod0] if scene.objects[c]["name"] == "BUILDING"]
        self.assertEqual(len(buildings), 2)
        for path in BUILDING_PATHS:
            found, _unresolved = self.pc.resolve_barriers(self.scenes, [path])
            self.assertEqual(scene.objects[found[0][1]]["parent_id"], buildings[0], path)

    def test_shop_shells_are_first_building_children_without_frame_parts(self):
        # The six closed shells resolve (Transform.Find semantics) to the first BUILDING sibling, hold no frame-named
        # part (BoothDoorRepairs deactivates every direct child without "frame", BakeNavMesh drops their colliders
        # the same way) and carry 14 live colliders in total: 8 ballistic (L12) and 6 movement (L18).
        scene = self.scenes["Arena_AirPit_ShootOut"]
        (lod0,) = [k for k, o in scene.objects.items() if o["name"] == "SOO_LOD0"]
        first_building = [c for c in scene.children[lod0] if scene.objects[c]["name"] == "BUILDING"][0]
        for path, expected in zip(SHOP_SHELLS, SHELL_LIVE_COLLIDER_LAYERS):
            found, unresolved = self.pc.resolve_barriers(self.scenes, [path])
            self.assertEqual((len(found), unresolved), (1, []), path)
            scene_name, key = found[0]
            self.assertEqual(scene_name, "Arena_AirPit_ShootOut", path)
            self.assertEqual(scene.objects[key]["parent_id"], first_building, path)
            self.assertTrue(scene.chain_active(key), path)
            tree = self.subtree(scene, key)
            self.assertFalse([k for k in tree if "frame" in scene.objects[k]["name"].lower()], path)
            self.assertEqual(self.live_colliders(scene, [key]), [], "the shell object itself must not be a source")
            self.assertEqual(sorted(layer for _k, layer in self.live_colliders(scene, tree)), expected, path)
        self.assertEqual(sum(len(layers) for layers in SHELL_LIVE_COLLIDER_LAYERS), 14)

    def test_the_cover_cubes_are_three_layer_zero_panels_without_frame_parts(self):
        # The group resolves once, under SOO of the main scene; its three direct children are the white panels (layer 0,
        # BoxCollider each, no frame-named part), so BoothDoorRepairs deactivates all three and nothing else.
        (path,) = COVER_CUBES
        found, unresolved = self.pc.resolve_barriers(self.scenes, [path])
        self.assertEqual((len(found), unresolved), (1, []), path)
        scene_name, key = found[0]
        self.assertEqual(scene_name, "Arena_AirPit_ShootOut")
        scene = self.scenes[scene_name]
        self.assertEqual(scene.objects[scene.objects[key]["parent_id"]]["name"], "SOO")
        self.assertTrue(scene.chain_active(key))
        children = scene.children[key]
        self.assertEqual([scene.objects[c]["name"] for c in children], ["Cube", "Cube", "Cube"])
        self.assertFalse([k for k in self.subtree(scene, key) if "frame" in scene.objects[k]["name"].lower()])
        self.assertEqual(self.live_colliders(scene, [key]), [], "the group object itself must not be a source")
        self.assertEqual(sorted(layer for _k, layer in self.live_colliders(scene, children)), [0, 0, 0])
        for c in children:
            self.assertIn("BoxCollider", [comp["type"] for comp in scene.objects[c]["components"]])

    def test_barrier_roots_are_scene_roots_without_frame_parts(self):
        total = 0
        for root_name in ROOT_BARRIERS:
            found, unresolved = self.pc.resolve_barriers(self.scenes, [root_name])
            self.assertEqual((len(found), unresolved), (1, []), root_name)
            scene_name, key = found[0]
            scene = self.scenes[scene_name]
            self.assertEqual(scene_name, "Arena_Airpit_TeamFight_DesignStuff")
            self.assertIn(key, scene.roots())
            tree = self.subtree(scene, key)
            # BoothDoorRepairs deactivates the DIRECT children whose name lacks "frame"; BakeNavMesh drops every
            # descendant with no frame-named object on its chain. Both agree because nothing here is called frame.
            self.assertFalse([k for k in tree if "frame" in scene.objects[k]["name"].lower()], root_name)
            self.assertEqual(self.live_colliders(scene, [key]), [], "the barrier object itself would stay a source")
            total += len(self.live_colliders(scene, tree))
        self.assertEqual(total, 92)   # BUS_DOORS 24 on layer 18 + 36 on layer 12; PASSPORT_CONTROL_DOORS 16 + 16; EMISSIVE none (fog sheets)

    def test_the_barriers_leave_no_markup_behind(self):
        # BakeNavMesh also drops NavMeshModifier/Volume markups under these objects; AirPit has none there.
        for path in BARRIERS:
            found, _unresolved = self.pc.resolve_barriers(self.scenes, [path])
            scene_name, key = found[0]
            scene = self.scenes[scene_name]
            markups = [k for k in self.subtree(scene, key) for c in scene.objects[k].get("components", [])
                       if c["type"] in ("NavMeshModifier", "NavMeshModifierVolume")]
            self.assertEqual(markups, [], path)

    def test_the_layer_mask_keeps_every_movement_collider_of_the_package_and_drops_only_other_layers(self):
        cset = self.pc.load_package_colliders(PACKAGE)
        self.pc.mark_barriers(cset, BARRIERS)
        self.pc.mark_bake_exclusions(cset, EXCLUSIONS)
        everything = self.pc.source_owner_mask(cset)
        masked = self.pc.source_owner_mask(cset, layer_mask=self.pc.layers_to_mask(ARENA_LAYERS))
        layers = {cset.owners[i].layer for i in range(len(cset.owners)) if everything[i] and not masked[i]}
        # v6 (five scenes): 5309 owners, 5176 sources (- 67 LevelBorder - 2 SPAWN_BOUND - 60 BUS_DOORS - 4 gate), 2029 on the mask.
        # v7 added the two furnished-shop scenes: Arena_Airpit_Main_hall 1107 owners (673 L12, 421 L18, 7 L9, 3 L30, 3 L22) and
        # Arena_Airpit_Light 178 (132 L12, 46 L18), none of them dropped by a name rule or barrier, so owners +1285 = 6594.
        # Main_hall included the 28 SOO_LOD0/DOORS units (closed static leaves: 34 L12 + 14 L18 + 7 L9 live colliders). The six
        # opened shop shells of level324 take 14 colliders out of the bake (8 L12, 6 L18): sources 5176 + 1285 - 14 = 6447,
        # layer-masked 2029 + (431 Main_hall + 46 Light) - 6 = 2500.
        # v8 widens both scenes to the whole level38/37 decoration (decoration box, 632 exclusions, 22 + 4 twin descendants):
        # Main_hall 2384 owners (1415 L12, 948 L18, 7 L9, 11 L30, 3 L22), Light 229 (182 L12, 47 L18); owners +1328 = 7922,
        # sources 6447 + 1328 = 7775 (no new name rule or barrier), layer-masked 2500 + 535 (Main_hall) + 1 (Light, the
        # one mask-layer lamp collider above the bake box, y 9.3) = 3036. The three layer-0 cover cubes of the south-west
        # shop (COVER_CUBES, 2026-10-13) are barriers since the first U6 host raid: sources 7775 - 3 = 7772, mask unchanged.
        # The held passport gate (2026-10-17, the original north team box) takes its 16 lane leaves (layer 18) and their 16
        # ballistic twins out again: sources 7772 - 32 = 7740, mask 3036 - 16 = 3020. EMISSIVE holds no collider.
        # v9 (2026-10-17) adds the three SOO crates under the tarp: 7 collider owners (2 x COLLIDER + BALLISTIC, cardbox
        # COLLIDER + 2 BALLISTIC), 3 of them on layer 18: owners 7929, sources 7747, mask 3023.
        self.assertEqual(len(cset.owners), 7929)
        self.assertEqual(int(everything.sum()), 7747)
        self.assertEqual(int(masked.sum()), 3023)
        per_scene = {}
        for i, owner in enumerate(cset.owners):
            counts = per_scene.setdefault(owner.scene, [0, 0, 0])
            counts[0] += 1
            counts[1] += int(everything[i])
            counts[2] += int(masked[i])
        self.assertEqual(per_scene["Arena_Airpit_Main_hall"], [2391, 2391, 969])   # [2384, 2384, 966] before the v9 crates
        self.assertEqual(per_scene["Arena_Airpit_Light"], [229, 229, 47])
        self.assertEqual(per_scene["Arena_AirPit_ShootOut"], [4625, 4537, 1891])   # 4554 / 1897 before the six shells, 4540 before the cubes
        self.assertEqual(per_scene["Arena_Airpit_TeamFight_DesignStuff"], [97, 3, 0])   # [97, 35, 16] while the passport leaves were sources
        # HighPolyCollider (ballistic), Interactive; the only live layer-0 (Default) sources were the three cover cubes,
        # which are barriers now (the two layer-0 owners of TeamFight_DesignStuff are the SPAWN_BOUND boxes, dropped by name)
        self.assertEqual(layers, {12, 22})
        self.assertFalse({cset.owners[i].layer for i in range(len(cset.owners)) if masked[i]} - set(ARENA_LAYERS))
        # AirPit has no live non-trigger collider on the Triggers layer, so keeping the owner rule costs nothing here
        self.assertFalse([o for o in cset.owners if o.layer == 13])


# ---------------------------------------------------------------------------------------------------------------------
# Door poses (2026-10-23): the offline mirror of BakeNavMesh.ResolveOpenedBarriers / RoundOpenedReason.
#
# A barrier of opened_barriers that has rows in tools/maps/<stem>.doors.json is one the client no longer hides: it parks or
# drives the posed parts and leaves every other part standing. The bake therefore drops only the posed parts' subtrees (the
# barrier root itself for a row with part "", as for Bay5's rollers) and bakes the rest as walls; a barrier without rows keeps
# the old rule (every non-frame descendant goes). Repeated paths (Bay5's two depot gates) are told apart by the rows' 'at'.
# The C# is executed by test_navmesh_source_layers.py (production text on a stand-in hierarchy); this mirror lets the same rule
# run over the real packages, where it reproduces the numbers of the last real bake logs for the old configuration.
# ---------------------------------------------------------------------------------------------------------------------
try:
    import port_colliders as pc
except ImportError:                    # numpy missing: the model tests skip, the sidecar pins above still run
    pc = None

EQUATOR_BARRIERS: list = []            # the FCK hall shutter stays shut (user decision 2026-10-23)
BAY5_BARRIERS = [
    "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)",
    "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)",
    "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)",
    "WAREHOUSE_DOORS (1)/One_man_Door",
    "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame",
]
OLD_BAY5_BARRIERS = BAY5_BARRIERS[1:3] + BAY5_BARRIERS[4:]            # (3), (4) and the depot frame: the 0.2.x configuration
OLD_EQUATOR_BARRIERS = ["SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)"]
AT_TOLERANCE = 0.05                    # ArenaDoorPose.AtTolerance == BakeNavMesh.DoorAtTolerance
EQUATOR_PACKAGE = REPO / "work" / "extract" / "equator-native-v5"
BAY5_PACKAGE = REPO / "work" / "extract" / "bay5-native-v5"
BOWL_PACKAGE = REPO / "work" / "extract" / "bowl-native-v2"


def door_rows(stem):
    """The rows the bake reads (ArenaPaths.DoorRows): barrier, part, node, at of tools/maps/<stem>.doors.json; none without the file."""
    path = REPO / "tools" / "maps" / f"{stem}.doors.json"
    if not path.is_file():
        return []
    doc = json.loads(path.read_text(encoding="utf-8"))
    if doc["schema_version"] != 2:
        raise AssertionError(f"{path.name}: the bake reads schema 2")
    return [{"barrier": p["barrier"], "part": p["part"], "node": p["node"], "at": p["at"]} for p in doc["poses"]]


def find_all_instances(scenes, path):
    """BakeNavMesh.FindAllInstances: every object the path names (all roots of the first name, all children per level)."""
    segments = path.split("/")
    found = []
    for scene in scenes.values():
        level = [r for r in scene.roots() if scene.objects[r]["name"] == segments[0]]
        for segment in segments[1:]:
            level = [c for k in level for c in scene.children.get(k, []) if scene.objects[c]["name"] == segment]
        found += [(scene.name, k) for k in level]
    return found


def row_applies(row, position):
    """ArenaDoorPose.AppliesTo / BakeNavMesh.DoorRowAppliesTo: no 'at' = every instance, else Euclidean within 0.05 m."""
    if row["at"] is None:
        return True
    return sum((float(position[i]) - row["at"][i]) ** 2 for i in range(3)) <= AT_TOLERANCE ** 2


def resolve_opened_barriers(scenes, paths, rows):
    """BakeNavMesh.ResolveOpenedBarriers -> (legacy paths, moved [(scene, key)], unresolved [str], posed count, uncovered [(path, scene, key)])."""
    legacy, moved, unresolved, uncovered, posed = [], [], [], [], 0
    for path in paths or []:
        if not path:
            continue
        path_rows = [r for r in rows if r["barrier"] == path]
        if not path_rows:
            legacy.append(path)                      # resolved by pc.mark_barriers (Transform.Find semantics), as before
            continue
        posed += 1
        if any(r["at"] is not None for r in path_rows):
            instances = find_all_instances(scenes, path)
        else:
            instances, _missing = pc.resolve_barriers(scenes, [path])
        if not instances:
            unresolved.append(path)
            continue
        used = [False] * len(path_rows)
        for scene_name, key in instances:
            scene = scenes[scene_name]
            position = scene.world(key)[:3, 3]
            covered = False
            for i, row in enumerate(path_rows):
                if not row_applies(row, position):
                    continue
                used[i] = covered = True
                target = key if not row["part"] else scene.find(key, row["part"])
                if target is None:
                    unresolved.append(f"{path}/{row['part']}")
                elif (scene_name, target) not in moved:
                    moved.append((scene_name, target))
            if not covered:
                uncovered.append((path, scene_name, key))
        unresolved += [f"{path} (row {i} matches no instance)" for i, hit in enumerate(used) if not hit]
    return legacy, moved, unresolved, posed, uncovered


class BarrierBake:
    """The barrier part of the port bake for one loaded package: which collider owners the rule drops, and under which label."""

    def __init__(self, cset, paths, rows):
        self.cset = cset
        self.legacy_paths, self.moved, self.unresolved, self.posed, self.uncovered = resolve_opened_barriers(cset.scenes, paths, rows)
        pc.mark_barriers(cset, self.legacy_paths)
        self.unresolved += list(cset.unresolved_barriers)
        names = {}
        for scene_name, key in self.moved:
            names.setdefault(scene_name, {})[key] = cset.scenes[scene_name].objects[key]["name"]
        self.reason = {}
        for o in cset.owners:
            label = None
            for key in cset.scenes[o.scene].path(o.obj_id):                      # root .. owner; a moved subtree contains its owners
                if key in names.get(o.scene, {}):
                    label = "door_poses:" + names[o.scene][key]
                    break
            base = pc.bake_exclusion_reason(o)
            if label and base in (None, "opened_barriers", "navmesh_bake_exclusions"):
                base = label                                                       # RoundOpenedReason checks the moved subtrees first
            self.reason[o.index] = base

    def sources(self):
        return {o.index for o in self.cset.owners if self.reason[o.index] is None}

    def dropped_by_barriers(self):
        return {i for i, r in self.reason.items() if r == "opened_barriers" or (r or "").startswith("door_poses:")}

    def counts(self):
        out = {}
        for r in self.reason.values():
            if r is not None:
                out[r] = out.get(r, 0) + 1
        return out

    def owners_named(self, fragment):
        return {o.index for o in self.cset.owners if fragment in o.path}

    def static_twins(self, tolerance=0.05):
        """(dropped, twin) path pairs: a collider the barrier rule drops (or the booth leaves, dropped by name) that has a SOURCE collider
        of the same object name, layer and kind and an equal world AABB (centre and extent within `tolerance`) in the package. That
        is a copy of the gate the bake keeps as a wall: a second scene's static slab stays shut when only this copy is opened, in the
        NavMesh and (the twin's renderer stays up too) on screen. Needs numpy (the model tests skip without it)."""
        import numpy as np

        def box(o):
            t = self.cset.tris[o.tri_start:o.tri_start + o.tri_count].reshape(-1, 3)
            return t.min(0), t.max(0)

        def dropped_gate(reason):
            return reason is not None and (reason.startswith("door_poses:") or reason in ("opened_barriers", "Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R"))

        kept = {}
        for q in self.cset.owners:
            if self.reason[q.index] is None and q.tri_count:
                kept.setdefault((q.names[-1], q.layer, q.kind), []).append(q)
        pairs = []
        for o in self.cset.owners:
            if not o.tri_count or not dropped_gate(self.reason[o.index]):
                continue
            lo, hi = box(o)
            for q in kept.get((o.names[-1], o.layer, o.kind), []):
                qlo, qhi = box(q)
                if max(np.abs(qlo - lo).max(), np.abs(qhi - hi).max()) <= tolerance:
                    pairs.append((f"{o.scene}:{o.path}", f"{q.scene}:{q.path}"))
        return pairs


def load_colliders(package):
    return pc.load_package_colliders(package)


def synthetic_package(directory):
    """Two depot gates (same path, different positions), three rollers, a legacy barrier and one unlisted gate, all box colliders."""
    def obj(oid, name, parent=None, pos=(0, 0, 0), layer=18, comps=()):
        return {"id": oid, "name": name, "parent_id": parent, "active": True, "layer": layer, "tag": "Untagged",
                "local_position": list(pos), "local_rotation": [0, 0, 0, 1], "local_scale": [1, 1, 1], "components": list(comps)}

    box = [{"type": "BoxCollider", "size": [1, 1, 1], "center": [0, 0, 0], "enabled": True, "is_trigger": False}]
    objects = [obj("1", "OLD_WAREHOUSE_DOORS", layer=0)]
    n = 10
    for gate, (x, z) in enumerate(((50.574, 7.525), (69.045, -6.317))):
        base = n
        objects += [
            obj(str(base), "Gates_Depot_Frame", "1", (x, -3.895, z), 0),
            obj(str(base + 1), "Telfer_Help (2)", str(base), (1, 0, 0), 0),
            obj(str(base + 2), "Telfer_Help_Collider", str(base + 1), comps=box),
            obj(str(base + 3), "Gates_Depot_Frame_COLLIDER", str(base), comps=box),
            obj(str(base + 4), "Gates_Depot_Door_L", str(base), (0, 0, 0), 0),
            obj(str(base + 5), "Gates_Depot_Door_L_Leaf", str(base + 4), (2, 0, 0), 0),
            obj(str(base + 6), "Gates_Depot_Door_L_COLLIDER", str(base + 5), comps=box),
            obj(str(base + 7), "Gates_Depot_Door_R", str(base), (0, 0, 0), 0),
            obj(str(base + 8), "Gates_Depot_Door_R_Leaf", str(base + 7), (-2, 0, 0), 0),
            obj(str(base + 9), "Gates_Depot_Door_R_COLLIDER", str(base + 8), comps=box),
        ]
        n += 20
    objects += [obj("500", "WAREHOUSE_DOORS (1)", layer=0)]
    for i, name in enumerate(("Roller (2)", "Roller (3)", "Roller (5)")):
        objects += [obj(str(510 + 2 * i), name, "500", (100 + 10 * i, 0, 40), 0),
                    obj(str(511 + 2 * i), "Rollete_COLLIDER", str(510 + 2 * i), comps=box)]
    objects += [obj("600", "Legacy", layer=0), obj("601", "Leaf_COLLIDER", "600", comps=box), obj("602", "Legacy_frame_COLLIDER", "600", comps=box)]
    (directory / "scenes").mkdir(parents=True)
    (directory / "meshes").mkdir()
    (directory / "scenes" / "Test.json").write_text(json.dumps({"scene_name": "Test", "objects": objects}), encoding="utf-8")
    return directory


@unittest.skipIf(pc is None, "numpy / port_colliders not available")
class DoorPoseBakeModelTests(unittest.TestCase):
    DEPOT = "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame"
    ROW_L1 = {"barrier": DEPOT, "part": "Gates_Depot_Door_L", "node": "Gates_Depot_Door_L_Leaf", "at": [50.574, -3.895, 7.525]}
    ROW_R1 = {"barrier": DEPOT, "part": "Gates_Depot_Door_R", "node": "Gates_Depot_Door_R_Leaf", "at": [50.574, -3.895, 7.525]}
    ROW_L2 = {"barrier": DEPOT, "part": "Gates_Depot_Door_L", "node": "Gates_Depot_Door_L_Leaf", "at": [69.045, -3.883, -6.317]}
    ROW_R2 = {"barrier": DEPOT, "part": "Gates_Depot_Door_R", "node": "Gates_Depot_Door_R_Leaf", "at": [69.045, -3.883, -6.317]}

    def bake(self, paths, rows):
        import tempfile
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        cset = pc.load_package_colliders(synthetic_package(Path(directory.name) / "pkg"))
        return BarrierBake(cset, paths, rows)

    def names(self, bake, indexes):
        return sorted(bake.cset.owners[i].path.split("/", 1)[1] for i in indexes)

    def test_both_depot_instances_are_posed_and_only_their_door_parts_go(self):
        b = self.bake([self.DEPOT], [self.ROW_L1, self.ROW_R1, self.ROW_L2, self.ROW_R2])
        self.assertEqual((b.posed, b.legacy_paths, b.unresolved, b.uncovered), (1, [], [], []))
        self.assertEqual(len(b.moved), 4)
        self.assertEqual(sorted(b.counts().items()), [("door_poses:Gates_Depot_Door_L", 2), ("door_poses:Gates_Depot_Door_R", 2)])
        dropped = self.names(b, b.dropped_by_barriers())
        self.assertEqual(dropped, sorted(f"Gates_Depot_Frame/Gates_Depot_Door_{s}/Gates_Depot_Door_{s}_Leaf/Gates_Depot_Door_{s}_COLLIDER"
                                         for s in "LRLR"))
        # posts and the frame collider of BOTH instances stay walls
        stays = {o.path.split("/", 1)[1] for o in b.cset.owners if b.reason[o.index] is None and "Gates_Depot_Frame" in o.path}
        self.assertEqual(stays, {"Gates_Depot_Frame/Telfer_Help (2)/Telfer_Help_Collider", "Gates_Depot_Frame/Gates_Depot_Frame_COLLIDER"})

    def test_the_old_rule_drops_the_posts_and_misses_the_second_instance(self):
        b = self.bake([self.DEPOT], [])
        self.assertEqual((b.posed, b.legacy_paths), (0, [self.DEPOT]))
        self.assertEqual(self.names(b, b.dropped_by_barriers()), sorted([
            "Gates_Depot_Frame/Telfer_Help (2)/Telfer_Help_Collider", "Gates_Depot_Frame/Gates_Depot_Door_L/Gates_Depot_Door_L_Leaf/Gates_Depot_Door_L_COLLIDER",
            "Gates_Depot_Frame/Gates_Depot_Door_R/Gates_Depot_Door_R_Leaf/Gates_Depot_Door_R_COLLIDER"]))   # first instance only

    def test_a_row_for_one_instance_leaves_the_other_a_wall(self):
        b = self.bake([self.DEPOT], [self.ROW_L2])
        self.assertEqual(len(b.moved), 1)
        self.assertEqual([u[0] for u in b.uncovered], [self.DEPOT])
        self.assertEqual(b.unresolved, [])
        self.assertEqual(len(b.dropped_by_barriers()), 1)

    def test_rows_that_resolve_to_nothing_are_reported(self):
        stale = dict(self.ROW_L1, at=[51.0, -3.895, 7.525])
        self.assertEqual(self.bake([self.DEPOT], [stale, self.ROW_L2]).unresolved, [f"{self.DEPOT} (row 0 matches no instance)"])
        self.assertEqual(self.bake([self.DEPOT], [dict(self.ROW_L1, part="Gates_Depot_Door_Q")]).unresolved, [f"{self.DEPOT}/Gates_Depot_Door_Q"])
        self.assertEqual(self.bake(["OLD_WAREHOUSE_DOORS/Gates_Missing"], [dict(self.ROW_L1, barrier="OLD_WAREHOUSE_DOORS/Gates_Missing")]).unresolved,
                         ["OLD_WAREHOUSE_DOORS/Gates_Missing"])

    def test_the_at_match_is_euclidean_within_five_centimetres(self):
        near = dict(self.ROW_L1, at=[50.574 + 0.03, -3.895 + 0.03, 7.525])
        far = dict(self.ROW_L1, at=[50.574 + 0.04, -3.895 + 0.04, 7.525])         # 0.0566 m away
        self.assertEqual(len(self.bake([self.DEPOT], [near]).moved), 1)
        self.assertEqual(len(self.bake([self.DEPOT], [far]).moved), 0)

    def test_rollers_move_as_a_whole_root_and_neighbours_stay(self):
        paths = ["WAREHOUSE_DOORS (1)/Roller (2)", "WAREHOUSE_DOORS (1)/Roller (3)"]
        rows = [{"barrier": p, "part": "", "node": "", "at": None} for p in paths]
        b = self.bake(paths, rows)
        self.assertEqual((b.posed, b.legacy_paths, b.unresolved), (2, [], []))
        self.assertEqual(sorted(b.counts().items()), [("door_poses:Roller (2)", 1), ("door_poses:Roller (3)", 1)])
        unlisted = b.owners_named("Roller (5)")
        self.assertEqual(len(unlisted), 1)
        self.assertTrue(all(b.reason[i] is None for i in unlisted))

    def test_mixed_list_applies_each_rule_to_its_own_barrier(self):
        rows = [self.ROW_L1, self.ROW_R1, self.ROW_L2, self.ROW_R2]
        b = self.bake([self.DEPOT, "Legacy"], rows)
        self.assertEqual((b.posed, b.legacy_paths), (1, ["Legacy"]))
        legacy_leaf = next(o.index for o in b.cset.owners if o.path.endswith("Legacy/Leaf_COLLIDER"))
        legacy_frame = next(o.index for o in b.cset.owners if o.path.endswith("Legacy_frame_COLLIDER"))
        self.assertEqual((b.reason[legacy_leaf], b.reason[legacy_frame]), ("opened_barriers", None))

    def test_rows_of_unlisted_barriers_are_ignored(self):
        b = self.bake([], [self.ROW_L1, self.ROW_R1])
        self.assertEqual((b.posed, b.legacy_paths, b.moved, b.unresolved), (0, [], [], []))
        self.assertEqual(b.dropped_by_barriers(), set())


def package_cases(*packages):
    return unittest.skipUnless(pc is not None and all((p / "scenes").is_dir() for p in packages),
                               "numpy or work/extract packages not present")


@package_cases(EQUATOR_PACKAGE, BAY5_PACKAGE, BOWL_PACKAGE)
class DoorPoseBakePackageTests(unittest.TestCase):
    """The rule over the real packages. The old configuration reproduces the last real bake logs (Equator: 4104 sources, Bay5:
    10501 = 10367 + 134 modifier volumes), so the model and the Unity bake count the same colliders."""

    def test_the_model_reproduces_the_last_real_bake_of_the_old_configuration(self):
        equator = BarrierBake(load_colliders(EQUATOR_PACKAGE), OLD_EQUATOR_BARRIERS, [])
        self.assertEqual((len(equator.cset.owners), len(equator.sources())), (4169, 4104))             # "collected 4169 ... sources=4104"
        self.assertEqual(equator.counts(), {"SPAWN_BOUND*": 2, "LevelBorder layer 29": 41, "Door_Sliding_Respawn_L": 10,
                                            "Door_Sliding_Respawn_R": 10, "opened_barriers": 2})        # the 2026-10-20 build log's lines
        bay5 = BarrierBake(load_colliders(BAY5_PACKAGE), OLD_BAY5_BARRIERS, [])
        self.assertEqual(len(bay5.sources()) + 134, 10501)                                              # + 134 NavMeshModifierVolume sources
        self.assertEqual(bay5.counts()["opened_barriers"], 14)                                          # (4) 2 + (3) 2 + depot 10

    def test_equator_has_no_opened_barrier_and_the_fck_shutter_is_a_wall(self):
        old = BarrierBake(load_colliders(EQUATOR_PACKAGE), OLD_EQUATOR_BARRIERS, [])
        new = BarrierBake(load_colliders(EQUATOR_PACKAGE), EQUATOR_BARRIERS, door_rows("equator"))
        self.assertEqual((new.posed, new.legacy_paths, new.moved, new.unresolved, new.uncovered), (0, [], [], [], []))
        self.assertEqual(new.dropped_by_barriers(), set())
        # the booth leaves are dropped by name, ten colliders per side (five assemblies x COLLIDER + BALLISTIC), exactly as before
        self.assertEqual(new.counts(), {"SPAWN_BOUND*": 2, "LevelBorder layer 29": 41, "Door_Sliding_Respawn_L": 10, "Door_Sliding_Respawn_R": 10})
        # the shutter's four colliders (two non-frame, two frame) are all sources now; before the two non-frame ones were dropped
        shutter = new.owners_named("equator_roller_shutters_02 (9)")
        self.assertEqual(sorted(new.cset.owners[i].names[-1] for i in shutter), [
            "roller_shutters_02_BALLISTIC_metalthin", "roller_shutters_02_COLLIDER",
            "roller_shutters_frame_02_BALLISTIC_metalthin", "roller_shutters_frame_02_COLLIDER"])
        self.assertTrue(shutter <= new.sources())
        self.assertEqual(old.owners_named("equator_roller_shutters_02 (9)"), shutter)
        self.assertEqual(shutter - old.sources(), old.dropped_by_barriers())
        self.assertEqual((len(old.sources()), len(new.sources())), (4104, 4106))                         # the real bake: sources=4104 -> 4106

    def test_equator_booth_assemblies_keep_their_surround_as_a_wall(self):
        new = BarrierBake(load_colliders(EQUATOR_PACKAGE), EQUATOR_BARRIERS, door_rows("equator"))
        booths = [o for o in new.cset.owners if "/Door_Sliding_Respawn" in o.path or o.path.startswith("SPAWN_DOORS/")]
        leaves = [o for o in booths if new.reason[o.index] in ("Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R")]
        self.assertEqual(len(leaves), 20)
        # root-level colliders of an assembly (surround, ballistic plastic/metal) are sources; only the leaf subtrees go
        surround = [o for o in booths if o.names[-1] in ("Door_Sliding_Respawn_COLLIDER", "Door_Sliding_Respawn_BALLISTIC_metalthin",
                                                         "Door_Sliding_Respawn_BALLISTIC_plastic")]
        self.assertEqual(len(surround), 15)                                                               # five assemblies x three
        self.assertTrue(all(new.reason[o.index] is None for o in surround))

    def test_bay5_opens_every_gate_and_keeps_the_depot_frame_and_posts_standing(self):
        old = BarrierBake(load_colliders(BAY5_PACKAGE), OLD_BAY5_BARRIERS, [])
        new = BarrierBake(load_colliders(BAY5_PACKAGE), BAY5_BARRIERS, door_rows("bay5"))
        self.assertEqual((new.posed, new.legacy_paths, new.unresolved, new.uncovered), (5, [], [], []))
        self.assertEqual(sorted(new.cset.scenes[s].objects[k]["name"] for s, k in new.moved), [
            "Bay5_Warehouse_Gate_Door_01 (2)", "Bay5_Warehouse_Gate_Door_01 (3)", "Bay5_Warehouse_Gate_Door_01 (4)", "Gates_Depot_Door_L",
            "Gates_Depot_Door_L", "Gates_Depot_Door_R", "Gates_Depot_Door_R", "One_man_Door"])
        self.assertEqual(new.counts()["door_poses:Gates_Depot_Door_L"], 8)         # per gate: leaf, second panel, and their two ballistic twins
        self.assertEqual(new.counts()["door_poses:Gates_Depot_Door_R"], 4)
        for name in ("Bay5_Warehouse_Gate_Door_01 (2)", "Bay5_Warehouse_Gate_Door_01 (3)", "Bay5_Warehouse_Gate_Door_01 (4)"):
            self.assertEqual(new.counts()["door_poses:" + name], 2, name)         # COLLIDER + BALLISTIC_metalthin
        self.assertEqual(new.counts()["door_poses:One_man_Door"], 4)              # top and bottom panel, two colliders each
        self.assertNotIn("opened_barriers", new.counts())
        self.assertEqual(len(new.dropped_by_barriers()), 22)
        self.assertEqual(len(old.dropped_by_barriers()), 14)
        # newly walls: the first depot gate's two posts (a Telfer_Help_Collider on 18 and a Telfer_Help_Metal on 12 each);
        # newly gone: the second depot gate's leaves (the old rule never found that instance), Door_01 (2) and the one-man door
        newly_walls = old.dropped_by_barriers() - new.dropped_by_barriers()
        newly_gone = new.dropped_by_barriers() - old.dropped_by_barriers()
        self.assertEqual((len(newly_walls), len(newly_gone)), (4, 12))
        self.assertTrue(all("Telfer_Help" in new.cset.owners[i].path for i in newly_walls))
        self.assertEqual(sorted(new.cset.owners[i].layer for i in newly_walls), [12, 12, 18, 18])
        self.assertEqual(len([i for i in newly_gone if "Gates_Depot_Door_" in new.cset.owners[i].path]), 6)
        self.assertEqual(len([i for i in newly_gone if "Bay5_Warehouse_Gate_Door_01 (2)" in new.cset.owners[i].path]), 2)
        self.assertEqual(len([i for i in newly_gone if "One_man_Door" in new.cset.owners[i].path]), 4)
        # every Telfer_Help post and every Gates_Depot_Frame_* collider of both depot gates is a source
        for fragment in ("Telfer_Help", "Gates_Depot_Frame_COLLIDER", "Gates_Depot_Frame_BALLISTIC_metalthick"):
            owners = new.owners_named("Gates_Depot_Frame/") if fragment == "Telfer_Help" else new.owners_named(fragment)
            owners = {i for i in owners if fragment in new.cset.owners[i].path}
            self.assertEqual(len(owners), {"Telfer_Help": 8, "Gates_Depot_Frame_COLLIDER": 2, "Gates_Depot_Frame_BALLISTIC_metalthick": 2}[fragment], fragment)
            self.assertTrue(owners <= new.sources(), fragment)
        self.assertEqual((len(old.sources()), len(new.sources())), (10367, 10359))                       # + 134 volume sources in the real log

    def test_bay5_gates_carry_no_navmesh_markup_that_the_rule_would_drop_or_keep_by_accident(self):
        new = BarrierBake(load_colliders(BAY5_PACKAGE), BAY5_BARRIERS, door_rows("bay5"))
        for scene_name, key in new.moved:
            scene = new.cset.scenes[scene_name]
            stack = [key]
            while stack:
                k = stack.pop()
                stack += scene.children.get(k, [])
                for comp in scene.objects[k].get("components", []):
                    self.assertNotIn(comp["type"], ("NavMeshModifier", "NavMeshModifierVolume"), scene.path_names(k))

    def test_bowl_keeps_exactly_the_colliders_it_dropped_before(self):
        # Bowl's two barriers have rows now (schema 2); every direct child of DOORS_SPAWN_A/B is a posed part, so the per-part rule
        # drops the same owners as the old non-frame rule did (a finer per-node rule would have turned the army gate's frame,
        # its two Telfer_Help posts and the gate rail into walls and changed the host-verified Bowl NavMesh).
        barriers = sidecar("bowl.json")["opened_barriers"]
        old = BarrierBake(load_colliders(BOWL_PACKAGE), barriers, [])
        new = BarrierBake(load_colliders(BOWL_PACKAGE), barriers, door_rows("bowl"))
        self.assertEqual((new.posed, new.legacy_paths, new.unresolved, new.uncovered), (2, [], [], []))
        self.assertEqual(new.dropped_by_barriers(), old.dropped_by_barriers())
        self.assertEqual(new.sources(), old.sources())
        self.assertEqual(sorted(new.cset.scenes[s].objects[k]["name"] for s, k in new.moved), [
            "BTR_82_closed", "Bowl_Gates_Respawn_01", "Bowl_Respawn_Ambulance_Animated Group", "Gates_Army_01"])

    def test_no_opened_gate_has_a_static_twin_left_standing(self):
        # A gate that is opened in one scene while an identical, active copy stays in another scene is not opened at all: the
        # copy is baked as a wall and drawn. None of the listed barriers and none of Equator's booth leaves has one.
        for package, stem, barriers in ((EQUATOR_PACKAGE, "equator", EQUATOR_BARRIERS), (BAY5_PACKAGE, "bay5", BAY5_BARRIERS),
                                        (BOWL_PACKAGE, "bowl", sidecar("bowl.json")["opened_barriers"])):
            bake = BarrierBake(load_colliders(package), barriers, door_rows(stem))
            self.assertEqual(bake.static_twins(), [], stem)

    def test_the_unnumbered_bay5_door_01_has_a_static_twin_so_listing_it_alone_would_not_open_it(self):
        # Positive control for the check above, and the evidence for the open question whether the east-wall gates Arena keeps shut
        # join Bay5's opened_barriers: the WAREHOUSE scene keeps its own Door_01 slab under GATES/FOR_ANIMATIONS at the same spot
        # (146.5, -2.91, 56.92), active, with the same COLLIDER and BALLISTIC_metalthin, so listing the TeamFight copy alone changes
        # neither the NavMesh nor the picture. Door_01 (1) has no such copy (its FOR_ANIMATIONS entry is the frame only).
        door = "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01"
        rows = [{"barrier": door, "part": "", "node": "", "at": None}]
        bake = BarrierBake(load_colliders(BAY5_PACKAGE), BAY5_BARRIERS + [door], door_rows("bay5") + rows)
        self.assertEqual((bake.unresolved, bake.uncovered), ([], []))
        twins = bake.static_twins()
        self.assertEqual(sorted((a.split(":", 1)[1].rsplit("/", 1)[1], b.split(":", 1)[0]) for a, b in twins),
                         [("Rollete_Gate_Door_01_BALLISTIC_metalthin", "Arena_Bay5_WAREHOUSE"), ("Rollete_Gate_Door_01_COLLIDER", "Arena_Bay5_WAREHOUSE")])
        self.assertTrue(all(b.split(":", 1)[1] == "SBG_Arena_Bay5_WAREHOUSE/OO/GATES/FOR_ANIMATIONS/Bay5_Warehouse_Gate_Door_01/" + a.rsplit("/", 1)[1]
                            for a, b in twins))
        one = "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (1)"
        bake = BarrierBake(load_colliders(BAY5_PACKAGE), BAY5_BARRIERS + [one], door_rows("bay5") + [dict(rows[0], barrier=one)])
        self.assertEqual(bake.static_twins(), [])

    def test_every_row_of_every_listed_barrier_resolves_to_a_part(self):
        for package, stem, barriers in ((EQUATOR_PACKAGE, "equator", EQUATOR_BARRIERS), (BAY5_PACKAGE, "bay5", BAY5_BARRIERS),
                                        (BOWL_PACKAGE, "bowl", sidecar("bowl.json")["opened_barriers"])):
            bake = BarrierBake(load_colliders(package), barriers, door_rows(stem))
            self.assertEqual((bake.unresolved, bake.uncovered), ([], []), stem)


if __name__ == "__main__":
    unittest.main()
