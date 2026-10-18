"""Bowl in the Arena NavMesh oracle (docs/PLAN-BOWL.md item 6).

Why: tools/validate/navmesh_oracle.py read Arena's Detour tiles as if their vertices were world coordinates.
That holds for AirPit (NavMeshData placement = identity) but not for Bowl, whose NavMeshData (level128
Arena_Bowl_TeamFight_AI, sharedassets128.assets path id 2, 80 tiles, agent 0.3 / 1.7) is rotated 90 degrees about
Y: read raw, Bowl's mesh lies 90 degrees away from its spawns, AI core points and colliders, and every oracle
answer is wrong. The oracle now applies m_Position / m_Rotation; navmesh_diff.py takes the per-map inputs from
ARENA_SOURCES (`--map`) with AirPit's row equal to what it hard-wired before.

Three groups. The first needs nothing but the repository (the per-map table, the CLI defaults, the plan
roles). The second needs Arena 0.6.0.47752 installed read-only and pins the measured numbers: Bowl's world
extent, components and area, and that every Arena anchor lies on its main component only AFTER the placement
(with a raw-frame control), plus AirPit's unchanged identity case. The third reads the 20 spawn markers from an
extracted Bowl package (the 0.5.4 spike package and the 0.6.0 v1 package agree to 0.0 m).
"""
import io
import json
import sys
import unittest
from pathlib import Path
from unittest import mock

import numpy as np

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
import navmesh_diff as nd
import navmesh_oracle as no

ARENA_DATA = Path(nd.DEFAULT_ARENA_DATA)
HAVE_BOWL = (ARENA_DATA / "sharedassets128.assets").is_file() and (ARENA_DATA / "level128").is_file()
HAVE_AIRPIT = (ARENA_DATA / "sharedassets33.assets").is_file()
BOWL = nd.ARENA_SOURCES["bowl"]
AIRPIT = nd.ARENA_SOURCES["airpit"]


def marker_package():
    """An extracted Bowl package that holds the DesignStuff scene (v1 = Arena 0.6.0, spike = 0.5.4)."""
    for name in ("bowl-native-v1", "bowl"):
        if (REPO / "work" / "extract" / name / "scenes" / "Arena_Bowl_TeamFight_DesignStuff.json").is_file():
            return REPO / "work" / "extract" / name
    return None


class ArenaSourceTableTests(unittest.TestCase):
    def test_airpit_keeps_the_values_that_were_hard_wired_before_the_table(self):
        self.assertEqual((AIRPIT.assets, AIRPIT.path_id), ("sharedassets33.assets", 2))
        self.assertEqual((AIRPIT.assets, AIRPIT.path_id), nd.AIRPIT_NAVMESH)
        self.assertEqual(AIRPIT.layer_mask, nd.AIRPIT_ARENA_LAYER_MASK)
        self.assertEqual(AIRPIT.layer_mask, 0x40042A00)
        self.assertEqual((AIRPIT.ai_scene_index, AIRPIT.ai_scene_name), (33, "Arena_Airpit_TeamFight_AI"))
        self.assertEqual((AIRPIT.key, AIRPIT.package), ("arena_airpit", "work/extract/airpit-native-v8"))
        self.assertEqual(nd.Config().arena_layer_mask, nd.AIRPIT_ARENA_LAYER_MASK)
        self.assertEqual(nd.load_arena_mesh.__defaults__, (nd.DEFAULT_ARENA_DATA, "sharedassets33.assets", 2))

    def test_bowls_row_is_the_measured_one(self):
        self.assertEqual((BOWL.assets, BOWL.path_id), ("sharedassets128.assets", 2))
        self.assertEqual((BOWL.ai_scene_index, BOWL.ai_scene_name), (128, "Arena_Bowl_TeamFight_AI"))
        self.assertEqual(BOWL.key, "arena_bowl")
        # NavMeshSurface layers 9, 11, 18, 30 (AirPit's set without 13), the same list the plan gives navmesh_bake_layers
        self.assertEqual(BOWL.layer_mask, sum(1 << layer for layer in (9, 11, 18, 30)))
        self.assertEqual(BOWL.layer_mask, 1074006528)

    def test_the_ai_scene_of_each_row_is_the_ai_role_of_its_source_plan(self):
        for stem, source in nd.ARENA_SOURCES.items():
            try:
                derived = nd.arena_source_from_plan(REPO / "tools" / "extract" / "plans" / f"{stem}.json", source)
            except ImportError:
                self.skipTest("UnityPy is not installed")
            self.assertEqual(derived, source, stem)

    def test_a_plan_replaces_only_the_ai_scene_and_the_key(self):
        try:
            derived = nd.arena_source_from_plan(REPO / "tools" / "extract" / "plans" / "bowl.json", AIRPIT)
        except ImportError:
            self.skipTest("UnityPy is not installed")
        self.assertEqual((derived.key, derived.ai_scene_index, derived.ai_scene_name), (BOWL.key, 128, "Arena_Bowl_TeamFight_AI"))
        self.assertEqual((derived.assets, derived.path_id, derived.layer_mask, derived.package),
                         (AIRPIT.assets, AIRPIT.path_id, AIRPIT.layer_mask, AIRPIT.package))


class CommandLineDefaultsTests(unittest.TestCase):
    """main() resolves the per-map inputs before it touches Arena or a package (analyse is stubbed here)."""

    def run_main(self, *argv):
        seen = {}

        def analyse(package, cfg, barriers, points, arena_data, **kw):
            seen.update(package=package, cfg=cfg, points=points, arena_data=arena_data, **kw)
            return {}

        def live(arena_data, scene_index, scene_name, map_key):
            seen["live"] = (arena_data, scene_index, scene_name, map_key)
            return []

        with mock.patch.object(nd, "analyse", analyse), mock.patch.object(nd, "format_report", lambda report, top: ""), \
                mock.patch.object(nd, "live_ai_points", live):
            self.assertEqual(nd.main(list(argv)), 0)
        return seen

    def test_without_a_map_it_is_airpit_exactly_as_before(self):
        seen = self.run_main("--live-core-points")
        self.assertEqual(seen["package"], "work/extract/airpit-native-v8")
        self.assertEqual((seen["assets"], seen["path_id"]), ("sharedassets33.assets", 2))
        self.assertEqual(seen["cfg"].arena_layer_mask, 0x40042A00)
        self.assertEqual(seen["live"], (nd.DEFAULT_ARENA_DATA, 33, "Arena_Airpit_TeamFight_AI", "arena_airpit"))

    def test_map_bowl_selects_bowls_navmesh_mask_ai_scene_and_package(self):
        seen = self.run_main("--map", "bowl", "--live-core-points")
        self.assertEqual(seen["package"], "work/extract/bowl-native-v1")
        self.assertEqual((seen["assets"], seen["path_id"]), ("sharedassets128.assets", 2))
        self.assertEqual(seen["cfg"].arena_layer_mask, 0x40040A00)
        self.assertEqual(seen["live"], (nd.DEFAULT_ARENA_DATA, 128, "Arena_Bowl_TeamFight_AI", "arena_bowl"))

    def test_single_options_override_the_map_row(self):
        seen = self.run_main("--map", "bowl", "--package", "somewhere", "--assets", "sharedassets9.assets", "--path-id", "7",
                             "--arena-layer-mask", "0x10")
        self.assertEqual((seen["package"], seen["assets"], seen["path_id"], seen["cfg"].arena_layer_mask),
                         ("somewhere", "sharedassets9.assets", 7, 0x10))

    def test_path_id_zero_is_an_override_not_a_missing_value(self):
        self.assertEqual(self.run_main("--path-id", "0")["path_id"], 0)

    def test_the_plan_of_the_selected_map_gives_the_same_inputs_as_the_row(self):
        try:
            seen = self.run_main("--map", "bowl", "--plan", str(REPO / "tools" / "extract" / "plans" / "bowl.json"),
                                 "--live-core-points")
        except ImportError:
            self.skipTest("UnityPy is not installed")
        self.assertEqual(seen["live"][1:], (128, "Arena_Bowl_TeamFight_AI", "arena_bowl"))
        self.assertEqual((seen["assets"], seen["path_id"]), ("sharedassets128.assets", 2))

    def test_the_plan_of_another_map_with_its_own_row_is_refused(self):
        # Bowl's AI points on AirPit's NavMeshData would silently report most points as off the mesh
        for map_key, plan in (("airpit", "bowl"), ("bowl", "airpit"), (None, "bowl")):
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


@unittest.skipUnless(HAVE_BOWL, "Arena 0.6.0 is not installed")
class BowlMeshTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")
        cls.tiles, cls.meta = no.load_navmesh_tiles(f"{nd.DEFAULT_ARENA_DATA}/{BOWL.assets}", BOWL.path_id)
        cls.mesh, _meta = nd.load_arena_mesh(nd.DEFAULT_ARENA_DATA, BOWL.assets, BOWL.path_id)
        cls.raw = no.build_navmesh(cls.tiles, no.Placement())
        cls.core = np.array([p["position"] for p in nd.live_ai_points(nd.DEFAULT_ARENA_DATA, BOWL.ai_scene_index, BOWL.ai_scene_name,
                                                                      BOWL.key) if p["kind"] == "core_point"])

    def test_the_navmesh_data_is_rotated_90_degrees_about_y_and_not_translated(self):
        self.assertEqual(self.meta["position"], [0.0, 0.0, 0.0])
        placement = self.tiles[0].placement
        self.assertAlmostEqual(placement.yaw_degrees(), 90.0, places=3)
        self.assertEqual((self.meta["tiles_total"], len(self.tiles)), (80, 47))
        settings = self.meta["settings"]
        self.assertAlmostEqual(settings["agentRadius"], 0.3, places=6)
        self.assertAlmostEqual(settings["agentHeight"], 1.7, places=6)

    def test_world_extent_components_and_area_of_the_main_component(self):
        self.assertEqual(self.mesh.poly_count, 1525)
        components = no.component_summary(self.mesh)
        self.assertEqual((len(components), components[0]["polygons"]), (11, 1477))
        main = components[0]
        self.assertAlmostEqual(main["area_xz"], 4987.5, delta=0.5)
        for got, want in zip(main["min"] + main["max"], [-63.84, 0.04, -79.45, 59.64, 9.25, 8.75]):
            self.assertAlmostEqual(got, want, delta=0.05)
        # the second component is a 57 m2 raised strip (y 4.5..5.5, x 27..44, z -77..-73); the rest are 1-5 polygon scraps
        self.assertAlmostEqual(components[1]["area_xz"], 56.95, delta=0.5)
        self.assertLess(components[2]["area_xz"], 11.0)

    def test_the_world_mesh_is_the_raw_tile_frame_turned_a_quarter_circle(self):
        # world (x, z) = (raw z, -raw x), to the 0.2 mm that Arena's stored quaternion is off a true quarter turn
        np.testing.assert_allclose(self.mesh.verts, np.stack([self.raw.verts[:, 2], self.raw.verts[:, 1], -self.raw.verts[:, 0]], axis=1),
                                   atol=5e-4)
        self.assertTrue(np.array_equal(self.mesh.labels, self.raw.labels))
        self.assertEqual((self.mesh.portal_pairs, self.mesh.unmatched_portal_edges), (self.raw.portal_pairs, self.raw.unmatched_portal_edges))

    def test_every_ai_core_point_lies_on_the_main_component_only_after_the_placement(self):
        self.assertEqual(len(self.core), 19)
        poly, surf = self.mesh.locate(self.core, max_dy=1.5)
        self.assertTrue((poly >= 0).all(), "a core point is outside Arena's NavMesh")
        self.assertTrue((self.mesh.labels[poly] == 0).all())
        self.assertLess(float(np.abs(surf - self.core[:, 1]).max()), 0.1)    # the mesh floor is where the anchors stand
        # control: the raw tile frame (the oracle before PLAN-BOWL item 6) misses 16 of the 19
        raw_poly, _raw_surf = self.raw.locate(self.core, max_dy=1.5)
        self.assertLessEqual(int((raw_poly >= 0).sum()), 4)

    def test_the_opposite_yaw_fits_nothing(self):
        mirrored = no.build_navmesh(self.tiles, no.Placement(rotation=(0.0, -0.7071057558059692, 0.0, 0.7071078419685364)))
        self.assertEqual(int((mirrored.locate(self.core, max_dy=1.5)[0] >= 0).sum()), 0)

    def test_patrol_hubs_are_on_the_main_component_too(self):
        points = nd.live_ai_points(nd.DEFAULT_ARENA_DATA, BOWL.ai_scene_index, BOWL.ai_scene_name, BOWL.key)
        hubs = np.array([p["position"] for p in points if p["kind"] == "patrol_hub"])
        subs = np.array([p["position"] for p in points if p["kind"] == "patrol_point"])
        self.assertEqual((len(hubs), len(subs)), (7, 79))
        poly, _surf = self.mesh.locate(hubs, max_dy=1.5)
        self.assertTrue((poly >= 0).all() and (self.mesh.labels[poly] == 0).all())
        sub_poly, _sub_surf = self.mesh.locate(subs, max_dy=1.5)
        self.assertEqual(int((sub_poly >= 0).sum()), 78)                      # hub6.2 stands 0.11 m outside the border
        self.assertTrue((self.mesh.labels[sub_poly[sub_poly >= 0]] == 0).all())

    def test_the_table_row_matches_the_decoded_navmesh_surface(self):
        import probe_mode_toggler as probe
        for source in (BOWL, AIRPIT):
            if not (ARENA_DATA / f"sharedassets{source.ai_scene_index}.assets").is_file():
                continue
            surfaces = probe.scene_census(nd.DEFAULT_ARENA_DATA, source.ai_scene_index)["surfaces"]
            self.assertEqual(len(surfaces), 1, source.key)
            self.assertEqual(surfaces[0]["layer_mask"], source.layer_mask, source.key)
            self.assertEqual(surfaces[0]["navmesh_data"]["path_id"], source.path_id, source.key)


@unittest.skipUnless(HAVE_BOWL and marker_package() is not None, "Arena 0.6.0 or an extracted Bowl package is not available")
class BowlSpawnMarkerTests(unittest.TestCase):
    def test_the_twenty_spawn_markers_stand_on_the_main_component_in_two_pockets(self):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            self.skipTest("UnityPy is not installed")
        from port_colliders import Scene
        scene = Scene(json.loads((marker_package() / "scenes" / "Arena_Bowl_TeamFight_DesignStuff.json").read_text(encoding="utf-8")))
        root = [k for k, o in scene.objects.items() if o["name"] == "SPAWNS"]
        self.assertEqual(len(root), 1)
        points = []
        for key in scene.children[root[0]]:
            if scene.objects[key]["name"].startswith("Bear house"):
                m = scene.world(key)
                points.append((float(m[0, 3]), float(m[1, 3]), float(m[2, 3])))
        self.assertEqual(len(points), 20)
        self.assertEqual(sum(1 for x, _y, _z in points if x < -40), 10)       # west pocket
        self.assertEqual(sum(1 for x, _y, _z in points if x > 40), 10)        # east pocket
        self.assertTrue(all(z < 0 for _x, _y, z in points))                   # both pockets lie south of z 0
        mesh, _meta = nd.load_arena_mesh(nd.DEFAULT_ARENA_DATA, BOWL.assets, BOWL.path_id)
        poly, surf = mesh.locate(np.array(points), max_dy=1.5)
        self.assertTrue((poly >= 0).all(), "a spawn marker is outside Arena's NavMesh")
        self.assertTrue((mesh.labels[poly] == 0).all())
        self.assertLess(float(np.abs(surf - np.array(points)[:, 1]).max()), 0.2)


@unittest.skipUnless(HAVE_AIRPIT, "Arena 0.6.0 is not installed")
class AirPitIdentityTests(unittest.TestCase):
    """AirPit's NavMeshData placement is the identity; the oracle must read it exactly as before (PLAN-BOWL item 6)."""

    def test_airpits_mesh_is_unchanged(self):
        try:
            mesh, meta = nd.load_arena_mesh()
        except ImportError:
            self.skipTest("UnityPy is not installed")
        self.assertEqual((meta["position"], meta["rotation"]), ([0.0, 0.0, 0.0], [-0.0, -0.0, -0.0, 1.0]))
        self.assertTrue(mesh.placement.is_identity)
        self.assertEqual((len(mesh.verts), mesh.poly_count), (6338, 3853))
        components = no.component_summary(mesh)
        self.assertEqual((len(components), components[0]["polygons"]), (34, 2890))
        main = components[0]
        self.assertAlmostEqual(main["area_xz"], 8168.163, places=3)
        np.testing.assert_allclose(main["min"] + main["max"], [-56.14, 0.0235, -77.70, 32.41, 6.4635, 84.77], atol=0.005)
        self.assertEqual((mesh.portal_pairs, mesh.unmatched_portal_edges), (199, 18))


if __name__ == "__main__":
    unittest.main()
