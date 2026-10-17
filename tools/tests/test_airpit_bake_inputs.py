"""AirPit NavMesh bake inputs (docs/PLAN-AIRPIT.md item 8): opened_barriers, navmesh_bake_exclusions, bake_bounds,
navmesh_bake_layers in tools/maps/airpit.json.

Why: the Unity bake is a 15-minute batch run, so everything checkable offline is pinned here. The descriptor
validator keeps the sidecar and ArenaMapRegistry.AirPit in step and rejects malformed values; the sidecar keeps
Equator and Bay5 free of the new keys (their bundles are not rebuilt); when the ignored 0.6.0 package is present,
every path in the lists resolves to exactly the object the NavMesh model flagged, and the two barrier roots are
shaped the way both consumers (the client opens direct children, the bake drops descendants) agree on. The C# side
is ArenaMapPort.Server.Tests (AirPitMapTests, MapDescriptorConsistencyTests) and test_navmesh_source_layers.py
(BakeNavMesh's pure helpers, executed from the production text); the offline model is test_navmesh_diff.py.
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
# turnstiles and the red lane sheets) are held_gates since 2026-10-18: closed at raid start in every mode, sealed for
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
# from 2a1e0ce (2026-10-12) to 2026-10-18, when the original north team box (z 69.16..85.16) needed its pocket meshed
# again (the held passport gate keeps bots out of it in plain raids).
BOUNDS = [-57, 37, -2, 8, -83, 83]
PACKAGE = REPO / "work" / "extract" / "airpit-native-v9"   # v8 + the three SOO crates under the tarp (2026-10-18); v6/v7/v8 are archives
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
        self.assertEqual(len(sidecar("equator.json")["opened_barriers"]), 1)
        self.assertEqual(len(sidecar("bay5.json")["opened_barriers"]), 3)

    def test_no_other_sidecar_uses_the_new_keys(self):
        for path in sorted((REPO / "tools" / "maps").glob("*.json")):
            if "." in path.stem or path.stem == "airpit":
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
        # 2026-10-18: the original north team box lives behind the passport line; its ten markers (and the south ten)
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
        # The held passport gate (2026-10-18, the original north team box) takes its 16 lane leaves (layer 18) and their 16
        # ballistic twins out again: sources 7772 - 32 = 7740, mask 3036 - 16 = 3020. EMISSIVE holds no collider.
        # v9 (2026-10-18) adds the three SOO crates under the tarp: 7 collider owners (2 x COLLIDER + BALLISTIC, cardbox
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


if __name__ == "__main__":
    unittest.main()
