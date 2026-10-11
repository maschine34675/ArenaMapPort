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

EXCLUSIONS = [
    "SBG_Arena_AirPit_ShootOut/SOO/fence_ema_01_white (18)",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_01_closed",
    "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed (1)",
]
# World positions of the four objects in the 0.6.0 package (x, z); the model's greedy list named them by path.
EXCLUSION_XZ = [(-23.21, -44.19), (-46.24, 29.41), (-43.27, -43.47), (22.11, -29.85)]
BARRIERS = ["BUS_DOORS", "PASSPORT_CONTROL_DOORS"]
ARENA_LAYERS = [9, 11, 13, 18, 30]            # NavMeshSurface mask 0x40042A00 of Arena's three AI scenes
BOUNDS = [-80, 56, -2, 8, -96, 92]
PACKAGE = REPO / "work" / "extract" / "airpit-native-v5"
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
        x0, x1, y0, y1, z0, z1 = self.map["bake_bounds"]
        bots, exits = sidecar("airpit.bots.json"), sidecar("airpit.exits.json")
        points = [p["position"] for p in bots["player_spawns"] + bots["bot_spawns"]] + [e["position"] for e in exits["exits"]]
        self.assertEqual(len(points), 20)
        for x, y, z in points:
            self.assertTrue(x0 + 20 <= x <= x1 - 20 and z0 + 15 <= z <= z1 - 5 and y0 < y < y1, (x, y, z))

    @unittest.skipUnless(ARENA_NAVMESH.is_file(), "installed Arena not present")
    def test_bounds_contain_arenas_play_space_component(self):
        import navmesh_diff as nd
        mesh, _meta = nd.load_arena_mesh()
        main = nd.oracle.component_summary(mesh)[0]
        x0, x1, y0, y1, z0, z1 = self.map["bake_bounds"]
        self.assertTrue(x0 < main["min"][0] and main["max"][0] < x1, (main["min"], main["max"]))
        self.assertTrue(z0 < main["min"][2] and main["max"][2] < z1, (main["min"], main["max"]))
        self.assertTrue(y0 < main["min"][1] and main["max"][1] < y1, (main["min"], main["max"]))


class ValidatorTests(unittest.TestCase):
    def test_the_shipped_descriptors_are_consistent(self):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        args = VD.registry_entries(text)["AirPit"]
        scope = {"ArenaEquatorLocation": {}}
        self.assertEqual(VD.resolve(args["OpenedBarriers"], scope), BARRIERS)
        self.assertEqual(VD.resolve(args["NavMeshBakeExclusions"], scope), EXCLUSIONS)
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
        self.assertTrue(any("navmesh_bake_exclusions mismatch" in e for e in self.check(lambda d: d["navmesh_bake_exclusions"].pop())))
        self.assertTrue(any("opened_barriers mismatch" in e for e in self.check(lambda d: d.pop("opened_barriers"))))

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
        overlap = errors(navmesh_bake_exclusions=["BUS_DOORS/Apron_bus_MAZ_doors"])
        self.assertTrue(any("inside an opened_barriers path" in e for e in overlap))
        self.assertTrue(any("inside an opened_barriers path" in e for e in errors(navmesh_bake_exclusions=["BUS_DOORS"])))

    def test_a_map_without_bake_inputs_is_valid(self):
        self.assertEqual(VD.bake_input_errors({}), [])
        self.assertEqual(VD.bake_input_errors(sidecar("equator.json")), [])
        self.assertEqual(VD.bake_input_errors(sidecar("bay5.json")), [])


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/airpit-native-v5 not present (PLAN-AIRPIT item 5)")
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

    def test_every_exclusion_resolves_to_exactly_the_flagged_object(self):
        for path, (x, z) in zip(EXCLUSIONS, EXCLUSION_XZ):
            found, unresolved = self.pc.resolve_barriers(self.scenes, [path])
            self.assertEqual((len(found), unresolved), (1, []), path)
            scene_name, key = found[0]
            self.assertEqual(scene_name, "Arena_AirPit_ShootOut")
            scene = self.scenes[scene_name]
            world = scene.world(key)
            self.assertAlmostEqual(world[0, 3], x, delta=0.05, msg=path)
            self.assertAlmostEqual(world[2, 3], z, delta=0.05, msg=path)
            colliders = self.live_colliders(scene, self.subtree(scene, key))
            self.assertTrue(colliders, path)
            # the movement collider is on LowPolyCollider (18), which Arena's layer mask collects
            self.assertIn(18, {layer for _k, layer in colliders}, path)
            self.assertTrue(scene.chain_active(key))

    def test_exclusion_paths_survive_a_duplicated_building_sibling(self):
        # SOO_LOD0 holds two siblings named BUILDING (52 and 8 children). Transform.Find takes the first one,
        # which holds all three BUILDING paths, so BakeNavMesh and this model resolve them to the same objects.
        scene = self.scenes["Arena_AirPit_ShootOut"]
        (lod0,) = [k for k, o in scene.objects.items() if o["name"] == "SOO_LOD0"]
        buildings = [c for c in scene.children[lod0] if scene.objects[c]["name"] == "BUILDING"]
        self.assertEqual(len(buildings), 2)
        for path in EXCLUSIONS[1:]:
            found, _unresolved = self.pc.resolve_barriers(self.scenes, [path])
            self.assertEqual(scene.objects[found[0][1]]["parent_id"], buildings[0], path)

    def test_barrier_roots_are_scene_roots_without_frame_parts(self):
        total = 0
        for root_name in BARRIERS:
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
        self.assertEqual(total, 92)

    def test_the_four_cutters_and_the_barriers_leave_no_markup_behind(self):
        # BakeNavMesh also drops NavMeshModifier/Volume markups under these objects; AirPit has none there.
        for path in EXCLUSIONS + BARRIERS:
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
        self.assertEqual(len(cset.owners), 5309)
        self.assertEqual(int(everything.sum()), 5139)         # 5309 - 67 LevelBorder - 2 SPAWN_BOUND - 92 barrier - 9 exclusion
        self.assertEqual(int(masked.sum()), 2011)
        self.assertEqual(layers, {0, 12, 22})                   # Default, HighPolyCollider (ballistic), Interactive
        self.assertFalse({cset.owners[i].layer for i in range(len(cset.owners)) if masked[i]} - set(ARENA_LAYERS))
        # AirPit has no live non-trigger collider on the Triggers layer, so keeping the owner rule costs nothing here
        self.assertFalse([o for o in cset.owners if o.layer == 13])


if __name__ == "__main__":
    unittest.main()
