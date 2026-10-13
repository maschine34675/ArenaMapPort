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
# PASSPORT_CONTROL_DOORS stays closed since 2026-10-12 (user decision, host raid 4/5): the closed passport-control look
# (eight waist-high turnstile gates with open floor between them, red PASSPORT_CONTROL_EMISSIVE lane lights above the
# posts); the authored facade wall is the actual north limit and the north spawns sit south of the gate line. Only
# the bus doors and the two portal gates open.
ROOT_BARRIERS = ["BUS_DOORS"]
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
BARRIERS = ROOT_BARRIERS + GATES + SHOP_SHELLS
# Paths under the duplicated BUILDING sibling, kept to pin the resolver's Transform.Find semantics (the shop shells above are inputs again).
BUILDING_PATHS = [
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_01_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed (1)",
]
ARENA_LAYERS = [9, 11, 13, 18, 30]            # NavMeshSurface mask 0x40042A00 of Arena's three AI scenes
# The AABB of the authored borders (Arena's TeamFight outline) rounded outwards and cut at z 70, just south of the
# closed passport-control line (z 69.4); was x -80..56, z -96..92 until host raid 4 (2026-10-12) showed the mesh
# running through the spawn pocket and 7 m into the unlit entrance hall north of the facade.
BOUNDS = [-57, 37, -2, 8, -83, 70]
PACKAGE = REPO / "work" / "extract" / "airpit-native-v8"   # v6 + the furnished shops and the rest of the hall decoration (Main_hall, Light); v6/v7 are archives
HAVE_PACKAGE = (PACKAGE / "scenes" / "Arena_AirPit_ShootOut.json").is_file()
ARENA_NAVMESH = Path("D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/sharedassets33.assets")


def sidecar(name):
    return json.loads((REPO / "tools" / "maps" / name).read_text(encoding="utf-8"))


class SidecarContentTests(unittest.TestCase):
    def setUp(self):
        self.map = sidecar("airpit.json")

    def test_airpit_lists(self):
        self.assertEqual(self.map["opened_barriers"], BARRIERS)
        self.assertEqual(self.map["navmesh_bake_exclusions"], EXCLUSIONS)
        self.assertEqual(self.map["bake_bounds"], BOUNDS)
        self.assertEqual(self.map["navmesh_bake_layers"], ARENA_LAYERS)

    def test_released_maps_keep_their_bake_unchanged(self):
        for name in ("equator.json", "bay5.json"):
            data = sidecar(name)
            for key in ("navmesh_bake_exclusions", "bake_bounds", "navmesh_bake_layers"):
                self.assertNotIn(key, data, f"{name} must not carry {key}")
        self.assertEqual(len(sidecar("equator.json")["opened_barriers"]), 1)
        self.assertEqual(len(sidecar("bay5.json")["opened_barriers"]), 3)

    def test_no_other_sidecar_uses_the_new_keys(self):
        for path in sorted((REPO / "tools" / "maps").glob("*.json")):
            if "." in path.stem or path.stem == "airpit":
                continue
            data = json.loads(path.read_text(encoding="utf-8"))
            for key in ("navmesh_bake_exclusions", "bake_bounds", "navmesh_bake_layers"):
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
        for bound, extreme in ((x0, min(xs)), (x1, max(xs)), (z0, min(zs))):
            self.assertLessEqual(abs(bound - extreme), 1.0, (bound, extreme))   # the box hugs the outline to within 1 m
        self.assertTrue(69.5 <= z1 <= max(zs) + 1, z1)   # the north is cut at the closed passport-control line (z 69.4)
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
        # The north spawn pocket (z 69.4..84.8, behind the closed passport-control doors) is deliberately left out;
        # the box ends at z 70 and must still hold everything south of the door line.
        self.assertTrue(z0 < main["min"][2] and 69.5 <= z1 < main["max"][2], (main["min"], main["max"], z1))
        self.assertTrue(y0 < main["min"][1] and main["max"][1] < y1, (main["min"], main["max"]))


class ValidatorTests(unittest.TestCase):
    def test_the_shipped_descriptors_are_consistent(self):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        args = VD.registry_entries(text)["AirPit"]
        scope = {"ArenaEquatorLocation": {}}
        self.assertEqual(VD.resolve(args["OpenedBarriers"], scope), BARRIERS)
        self.assertNotIn("NavMeshBakeExclusions", args)   # omitted on purpose: the descriptor defaults to an empty list
        self.assertEqual(VD.resolve(args["BakeBounds"], scope), BOUNDS)
        self.assertEqual(VD.resolve(args["NavMeshBakeLayers"], scope), ARENA_LAYERS)
        for name in ("Equator", "Bay5"):
            for field in ("NavMeshBakeExclusions", "BakeBounds", "NavMeshBakeLayers"):
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
        for name in ("opened_barriers", "navmesh_bake_exclusions"):
            for bad in ("a/b", [""], ["a//b"], ["/a"], ["a/"], [" a"], [3], ["a/b", "a/b"]):
                self.assertTrue(any(name in e for e in errors(**{name: bad})), (name, bad))
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


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/airpit-native-v8 not present (PLAN-AIRPIT item 5/13, furnished shops and hall decoration)")
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
        self.assertEqual(total, 60)   # BUS_DOORS: 24 on layer 18, 36 on layer 12 (the 32 passport-door colliders stay)

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
        # one mask-layer lamp collider above the bake box, y 9.3) = 3036.
        self.assertEqual(len(cset.owners), 7922)
        self.assertEqual(int(everything.sum()), 7775)
        self.assertEqual(int(masked.sum()), 3036)             # the 16 passport-door leaves on layer 18 are sources again
        per_scene = {}
        for i, owner in enumerate(cset.owners):
            counts = per_scene.setdefault(owner.scene, [0, 0, 0])
            counts[0] += 1
            counts[1] += int(everything[i])
            counts[2] += int(masked[i])
        self.assertEqual(per_scene["Arena_Airpit_Main_hall"], [2384, 2384, 966])
        self.assertEqual(per_scene["Arena_Airpit_Light"], [229, 229, 47])
        self.assertEqual(per_scene["Arena_AirPit_ShootOut"], [4625, 4540, 1891])   # 4554 / 1897 before the six shells were barriers
        self.assertEqual(layers, {0, 12, 22})                   # Default, HighPolyCollider (ballistic), Interactive
        self.assertFalse({cset.owners[i].layer for i in range(len(cset.owners)) if masked[i]} - set(ARENA_LAYERS))
        # AirPit has no live non-trigger collider on the Triggers layer, so keeping the owner rule costs nothing here
        self.assertFalse([o for o in cset.owners if o.layer == 13])


if __name__ == "__main__":
    unittest.main()
