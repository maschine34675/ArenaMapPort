"""tools/validate/distant_shadow_census.py: the offline mirror of EFT's DistantShadow draw list and the coverage test
(docs/progress/lighting-followup.md, 2026-11-12, "Iceberg: the interior shadow proxies").

Groups:
  * synthetic packages (always run): the LOD pick rule, the filters, the sidecar enrolment and the coverage test on quads;
  * the sun and cascade model against the GPU-2 readings of 2026-11-12;
  * the accepted packages (skipped when work/extract is absent; override the folder with ARENAMAPPORT_WORK_DIR): Iceberg's
    list equals the host's count without its sidecar rows (1,571) and with them (1,574), its interior proxies are covered at
    midday and the three cafe rows close the morning stripes; Chop Shop and Equator are clean WITH their sidecar rows and
    show the known defects without them.
"""
import contextlib
import io
import json
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[2]
WORK = Path(os.environ.get("ARENAMAPPORT_WORK_DIR", REPO / "work"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import distant_shadow_census as ds  # noqa: E402

UP = [(0, 3, 1), (1, 3, 2)]      # cross(b - a, c - a) points +y: the front side faces up
DOWN = [(0, 1, 3), (1, 2, 3)]
QUAD = [(-0.5, 0, -0.5), (0.5, 0, -0.5), (0.5, 0, 0.5), (-0.5, 0, 0.5)]


def write_mesh(path, verts, tris):
    verts = np.asarray(verts, dtype="<f4")
    idx = np.asarray(tris, dtype="<u2").reshape(-1)
    with open(path, "wb") as f:
        f.write(b"AMPM" + struct.pack("<IIHBB", 1, len(verts), 1, 0, 1))
        f.write(verts.tobytes())
        f.write(struct.pack("<IIB", 0, len(idx), 0))
        f.write(idx.tobytes())


class Package:
    """A one-scene package of quads: each renderer is a unit quad in the xz plane, scaled and placed by its object."""

    def __init__(self, root):
        self.root = Path(root)
        (self.root / "scenes").mkdir()
        (self.root / "meshes").mkdir()
        write_mesh(self.root / "meshes" / "up.mesh.bin", QUAD, UP)
        write_mesh(self.root / "meshes" / "down.mesh.bin", QUAD, DOWN)
        self.objects = []
        self.manifest = {"materials": {"shadow": {"shader": "shadow"}, "back": {"shader": "shadow_backside"},
                                       "std": {"shader": "Standard"}},
                         "meshes": {"up": {"submesh_count": 1}, "down": {"submesh_count": 1}}}

    def node(self, oid, name, parent=None, pos=(0, 0, 0), scale=(1, 1, 1), active=True, comps=()):
        self.objects.append({"id": oid, "name": name, "parent_id": parent, "active": active, "layer": 0, "tag": "Untagged",
                             "local_position": list(pos), "local_rotation": [0, 0, 0, 1], "local_scale": list(scale),
                             "components": list(comps)})
        return oid

    def quad(self, oid, name, parent=None, pos=(0, 0, 0), size=10.0, cast="ShadowsOnly", material="shadow", mesh="up",
             enabled=True, active=True):
        comps = [{"type": "MeshFilter", "mesh_id": mesh},
                 {"type": "MeshRenderer", "cast_shadows": cast, "enabled": enabled, "material_ids": [material] if material else []}]
        return self.node(oid, name, parent, pos, (size, 1, size), active, comps)

    def group(self, oid, name, lods, parent=None):
        comp = {"type": "LODGroup", "enabled": True, "lods": [{"renderer_object_ids": list(ids)} for ids in lods]}
        return self.node(oid, name, parent, comps=[comp])

    def census(self, enrolled=(), two_sided=False):
        (self.root / "scenes" / "S.json").write_text(json.dumps({"scene_name": "S", "objects": self.objects}), encoding="utf-8")
        (self.root / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        return ds.census(self.root, enrolled, two_sided)


class Synthetic(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.pkg = Package(self._tmp.name)

    @staticmethod
    def names(records):
        return sorted(r["name"] for r in records)


class PickRuleTests(Synthetic):
    def test_second_to_last_lod_is_drawn(self):
        p = self.pkg
        p.group("g", "three", [["a"], ["b"], ["c"]])
        for oid in "abc":
            p.quad(oid, oid, "g")
        result = p.census()
        self.assertEqual(self.names(result.native), ["b"])
        self.assertEqual(self.names(result.other_lod), ["a", "c"])
        self.assertEqual(result.unreferenced, [])
        self.assertEqual((result.draw_records, result.lod_groups), (1, 1))

    def test_walk_goes_down_to_the_first_lod_with_a_caster(self):
        p = self.pkg
        p.group("g", "three", [["a"], ["b"], ["c"]])
        p.quad("a", "a", "g")
        p.quad("b", "b", "g", cast="Off")
        p.quad("c", "c", "g")
        result = p.census()
        self.assertEqual(self.names(result.native), ["a"])
        self.assertEqual(self.names(result.other_lod), ["c"])        # "b" casts nothing and is no caster at all

    def test_one_and_two_lods_draw_lod0(self):
        p = self.pkg
        p.group("g1", "one", [["a"]])
        p.group("g2", "two", [["b"], ["c"]])
        for oid, parent in (("a", "g1"), ("b", "g2"), ("c", "g2")):
            p.quad(oid, oid, parent)
        self.assertEqual(self.names(p.census().native), ["a", "b"])

    def test_a_caster_only_in_the_last_lod_is_never_drawn(self):
        p = self.pkg
        p.group("g", "two", [["a"], ["b"]])
        p.quad("a", "a", "g", cast="Off")
        p.quad("b", "b", "g")
        result = p.census()
        self.assertEqual(result.native, [])
        self.assertEqual(self.names(result.other_lod), ["b"])
        self.assertIsNone(result.other_lod[0]["lod_refs"][0]["picked"])

    def test_one_record_per_submesh(self):
        p = self.pkg
        p.manifest["meshes"]["up"]["submesh_count"] = 3
        p.group("g", "one", [["a"]])
        p.quad("a", "a", "g")
        self.assertEqual(p.census().draw_records, 3)


class FilterTests(Synthetic):
    def test_what_the_list_and_the_candidates_leave_out(self):
        p = self.pkg
        p.group("g", "one", [["drawn_disabled", "no_material", "small_in_lod"]])
        p.quad("drawn_disabled", "drawn_disabled", "g", enabled=False)     # DistantShadow does not read Renderer.enabled
        p.quad("no_material", "no_material", "g", material=None)
        p.quad("small_in_lod", "small_in_lod", "g", size=0.9)
        p.quad("free", "free")
        p.quad("free_small", "free_small", size=0.9)
        p.quad("free_disabled", "free_disabled", enabled=False)             # the cascades do not draw it either
        p.quad("free_off", "free_off", cast="Off")
        p.node("hidden", "hidden", active=False)
        p.quad("free_inactive", "free_inactive", "hidden")
        result = p.census()
        self.assertEqual(self.names(result.native), ["drawn_disabled"])
        self.assertEqual(self.names(result.unreferenced), ["free"])
        self.assertEqual(result.other_lod, [])
        self.assertEqual(result.draw_records, 1)

    def test_bounds_are_the_world_box_of_the_local_box(self):
        p = self.pkg
        p.node("root", "root", pos=(100, 2, -50))
        p.quad("free", "free", "root", pos=(1, 0, 0), size=4.0)
        rec = p.census().unreferenced[0]
        np.testing.assert_allclose(rec["lo"], (99, 2, -52))
        np.testing.assert_allclose(rec["hi"], (103, 2, -48))
        self.assertEqual(rec["path"], "root/free")


class EnrolTests(Synthetic):
    def test_sidecar_paths_join_the_list(self):
        p = self.pkg
        p.node("root", "Root")
        p.node("first", "Hall", "root")
        p.node("second", "Hall", "root")
        p.quad("hull", "Hull", "first")
        p.quad("twin", "Hull", "second", pos=(50, 0, 0))                   # Transform.Find takes the first 'Hall'
        result = p.census(["Root/Hall/Hull"])
        self.assertEqual([r["id"] for r in result.native], ["hull"])
        self.assertTrue(result.native[0]["enrolled"])
        self.assertEqual(result.native[0]["sides"], "front")
        self.assertEqual([r["id"] for r in result.unreferenced], ["twin"])
        self.assertEqual((result.draw_records, result.skipped_paths), (1, []))
        self.assertEqual(p.census(["Root/Hall/Hull"], two_sided=True).native[0]["sides"], "both")

    def test_paths_the_client_would_not_enrol_are_reported(self):
        p = self.pkg
        p.node("root", "Root")
        p.quad("on", "CastsOn", "root", cast="On")
        p.group("g", "Group", [["in_group"]], "root")
        p.quad("in_group", "InGroup", "g")
        p.quad("orphan", "Orphan", "g")                                    # under a LODGroup, in none of its LODs
        result = p.census(["Root/CastsOn", "Root/Group/Orphan", "Root/Missing"])
        self.assertEqual(dict(result.skipped_paths), {
            "Root/CastsOn": "not an active, enabled ShadowsOnly mesh renderer",
            "Root/Group/Orphan": "already under a LODGroup",
            "Root/Missing": "not found in any scene"})
        self.assertEqual(self.names(result.native), ["InGroup"])
        self.assertEqual(self.names(result.unreferenced), ["CastsOn", "Orphan"])


class CoverageTests(Synthetic):
    SUN = np.array([0.3, 0.9, 0.2])

    def candidate(self):
        self.pkg.quad("cand", "cand", pos=(0, 5, 0), size=10.0)

    def roof(self, y=10.0, mesh="up", material="shadow", in_group=True):
        if in_group:
            self.pkg.group("g", "roofgroup", [["roof"]])
        self.pkg.quad("roof", "roof", "g" if in_group else None, pos=(0, y, 0), size=40.0, mesh=mesh, material=material)

    def uncovered(self, result, **kw):
        rows = ds.Coverage(result, min_edge=3.0, texel=0.25).at(self.SUN, **kw)
        return {r["record"]["name"]: r for r in rows}

    def test_a_free_candidate_is_uncovered_over_its_whole_sunward_area(self):
        self.candidate()
        row = self.uncovered(self.pkg.census())["cand"]
        expected = 100.0 * 0.9 / np.linalg.norm(self.SUN)                   # area across the sun
        self.assertGreaterEqual(row["facing_m2"], expected)                 # the outline is inflated by half a texel
        self.assertLess(row["facing_m2"], expected + 0.125 * 2 * (10.0 + 10.0) + 1.0)
        self.assertAlmostEqual(row["uncovered_m2"], row["facing_m2"], delta=0.5)

    def test_a_listed_roof_above_covers_it(self):
        self.candidate()
        self.roof()
        self.assertEqual(self.uncovered(self.pkg.census())["cand"]["uncovered_m2"], 0.0)

    def test_faces_block_as_their_shader_culls(self):
        for mesh, material, covered in (("down", "shadow", False), ("down", "back", True), ("up", "back", False),
                                        ("down", "std", False)):
            with self.subTest(mesh=mesh, material=material):
                self.pkg.objects.clear()
                self.candidate()
                self.roof(mesh=mesh, material=material)
                row = self.uncovered(self.pkg.census())["cand"]
                self.assertEqual(row["uncovered_m2"] < 1.0, covered)

    def test_an_enrolled_soffit_needs_the_two_sided_flag(self):
        self.candidate()
        self.roof(mesh="down", in_group=False)
        self.assertGreater(self.uncovered(self.pkg.census(["roof"]))["cand"]["uncovered_m2"], 80.0)
        self.assertEqual(self.uncovered(self.pkg.census(["roof"], two_sided=True))["cand"]["uncovered_m2"], 0.0)
        # not enrolled at all: the big soffit is a candidate itself and the small quad stays uncovered
        rows = self.uncovered(self.pkg.census())
        self.assertGreater(rows["cand"]["uncovered_m2"], 80.0)
        self.assertEqual(rows["roof"]["facing_m2"], 0.0)                    # its faces point away from the sun

    def test_a_listed_caster_behind_counts_only_past_the_gap(self):
        self.candidate()
        self.roof(y=5.0 - 0.2)                                              # the visual floor right under its own proxy
        self.assertEqual(self.uncovered(self.pkg.census())["cand"]["uncovered_m2"], 0.0)
        self.pkg.objects.clear()
        self.candidate()
        self.roof(y=2.0)
        row = self.uncovered(self.pkg.census(), clusters=True)["cand"]
        self.assertGreater(row["uncovered_m2"], 80.0)
        cluster = row["clusters"][0]
        self.assertAlmostEqual(cluster["area_m2"], row["uncovered_m2"], delta=0.5)
        np.testing.assert_allclose(cluster["entry_lo"], (-5, 5, -5), atol=0.3)
        np.testing.assert_allclose(cluster["entry_hi"], (5, 5, 5), atol=0.3)
        self.assertAlmostEqual(cluster["land_lo"][1], 2.0, delta=0.05)      # the rays go on to the listed slab 3 m below
        self.assertAlmostEqual(cluster["gap_median"], 3.0 / (0.9 / np.linalg.norm(self.SUN)), delta=0.1)
        self.assertEqual(cluster["open_share"], 0.0)

    def test_the_report_and_the_gate(self):
        self.candidate()
        self.pkg.census()
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ds.main([str(self.pkg.root), "--sun", "0.3,0.9,0.2", "--clusters", "--fail-above", "50"])
        text = out.getvalue()
        self.assertEqual(code, 1)
        self.assertIn("DistantShadow list: 0 renderers, 0 draw records", text)
        self.assertIn("1 referenced by no LODGroup", text)
        self.assertIn("meet none", text)
        self.assertIn("FAIL:", text)
        self.roof()
        (self.pkg.root / "scenes" / "S.json").write_text(json.dumps({"scene_name": "S", "objects": self.pkg.objects}), encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(ds.main([str(self.pkg.root), "--hours", "13.8,3", "--fail-above", "50"]), 0)
        self.assertIn("nothing uncovered", out.getvalue())
        self.assertIn("below the horizon", out.getvalue())


class SunModelTests(unittest.TestCase):
    # TOD_Sky.Instance.SunDirection read on GPU-2 on 2026-11-12 (Iceberg and Chop Shop give the same vectors)
    READINGS = {7.0: (0.93, 0.086, -0.358), 8.0: (0.949, 0.257, -0.18), 15.5: (-0.409, 0.819, 0.403)}

    def test_sun_direction_matches_the_host_readings(self):
        for hour, measured in self.READINGS.items():
            with self.subTest(hour=hour):
                sun = ds.sun_direction(hour)
                self.assertAlmostEqual(float(np.linalg.norm(sun)), 1.0, places=6)
                np.testing.assert_allclose(sun, measured, atol=0.004)

    def test_cascade_distance_matches_the_host_readings(self):
        # QualitySettings.shadowDistance on GPU-2 (shadow quality 3 = 80 m): 53.33 at 07:30 and 58.67 at 08:00 outdoors,
        # 80 at 15:30 outdoors and 80 indoors at 08:00; the 55.33 of the Chop Shop record belongs to 07:41 or 19:46.
        self.assertAlmostEqual(ds.cascade_distance(7.5), 53.3333, places=3)
        self.assertAlmostEqual(ds.cascade_distance(8.0), 58.6667, places=3)
        self.assertEqual(ds.cascade_distance(15.5), 80.0)
        self.assertEqual(ds.cascade_distance(8.0, outdoor=False), 80.0)
        self.assertAlmostEqual(ds.cascade_distance(19.775), 55.33, places=2)
        self.assertAlmostEqual(ds.cascade_distance(7.6875), 55.33, places=2)
        self.assertEqual(ds.cascade_distance(5.0), 80.0)                    # the interval ends are open
        self.assertAlmostEqual(ds.cascade_distance(22.999, 30.0), 10.0, delta=0.01)
        self.assertEqual(ds.QUALITY_DISTANCES, (30.0, 40.0, 60.0, 80.0))

    def test_the_sun_sets_between_21_and_2130(self):
        self.assertGreater(ds.sun_direction(21.0)[1], 0.0)
        self.assertLess(ds.sun_direction(21.5)[1], 0.0)
        self.assertLess(ds.sun_direction(6.0)[1], 0.0)


ICEBERG = WORK / "extract" / "iceberg-native-v2"
AUTOSERVICE = WORK / "extract" / "autoservice-native-v2"
EQUATOR = WORK / "extract" / "equator-native-v5"


def sidecar_paths(stem):
    return json.loads((REPO / "tools" / "maps" / f"{stem}.json").read_text(encoding="utf-8")).get("distant_shadow_caster_paths", [])


def worst(rows, cast="ShadowsOnly"):
    rows = [r for r in rows if r["record"]["cast"] == cast]
    return max(rows, key=lambda r: r["uncovered_m2"]) if rows else None


@unittest.skipUnless(ICEBERG.is_dir(), "work/extract/iceberg-native-v2 is not available (set ARENAMAPPORT_WORK_DIR)")
class IcebergTests(unittest.TestCase):
    CAFE = ("Sanatorium_Cafe_second_floor_SHADOW_LOD0", "Sanatorium_Cafe_ground_floor_SHADOW_LOD0",
            "Sanatorium_Cafe_constructions_SHADOW_LOD0")

    @classmethod
    def setUpClass(cls):
        cls.bare = ds.census(ICEBERG)                                       # the released 0.5.0 state: no row
        cls.result = ds.census(ICEBERG, sidecar_paths("iceberg"))
        cls.bare_cover = ds.Coverage(cls.bare, min_edge=3.0)
        cls.cover = ds.Coverage(cls.result, min_edge=3.0)

    def test_the_list_equals_the_host_count(self):
        # DistantShadow._renderList.Count on GPU-2, 2026-11-12: 1571 on the build without rows and with the live key off,
        # 1574 with the three cafe rows enrolled
        self.assertEqual(self.bare.draw_records, 1571)
        self.assertEqual(len(self.bare.native), 1485)
        self.assertEqual(self.result.draw_records, 1574)
        self.assertEqual(len(self.result.native), 1488)
        self.assertEqual(self.result.skipped_paths, [])
        self.assertEqual(sorted(r["name"] for r in self.result.native if r.get("enrolled")), sorted(self.CAFE))
        self.assertEqual({r["sides"] for r in self.result.native if r.get("enrolled")}, {"front"})

    def test_the_candidates(self):
        self.assertEqual(len(self.result.unreferenced), 56)
        free = self.bare.unreferenced
        self.assertEqual(len(free), 59)
        self.assertEqual({r["cast"] for r in free}, {"ShadowsOnly"})
        self.assertEqual(sum(1 for r in free if r["size"].max() >= 3.0), 28)
        self.assertEqual(sum(1 for r in free if r["size"].max() >= 5.0), 19)
        self.assertEqual([r["name"] for r in free[:4]], [
            "Sanatorium_Cafe_second_floor_SHADOW_LOD0", "Sanatorium_hallway_big_01_SHADOW_LOD0",
            "Sanatorium_Cafe_ground_floor_SHADOW_LOD0", "Sanatorium_Cafe_constructions_SHADOW_LOD0"])

    def test_the_roof_and_the_facades_are_listed(self):
        listed = {r["name"]: r for r in self.bare.native}
        roof = listed["sanatorium_roof_SHADOW_LOD0"]
        self.assertGreater(roof["size"][0], 98.0)
        self.assertGreater(roof["size"][2], 99.0)
        for name in ("sanatorium_facade_entrance_SHADOW_LOD0", "sanatorium_facade_01__SHADOW_LOD0", "sanatorium_facade_02_SHADOW_LOD0",
                     "sanatorium_facade_03_SHADOW_LOD0", "sanatorium_facade_05_SHADOW_LOD0", "sanatorium_facade_06_SHADOW_LOD0"):
            self.assertIn(name, listed)

    def test_midday_sun_is_kept_out_by_the_listed_casters(self):
        for hour in (12.0, 15.5, 17.0):
            with self.subTest(hour=hour):
                rows = [r for r in self.bare_cover.at(ds.sun_direction(hour)) if r["record"]["path"].startswith("SBG_Arena_Sanatorium_Main/")]
                self.assertEqual(len(rows), 24)                             # the building's proxies; the rest are lamps and the tunnel box
                self.assertLess(worst(rows)["uncovered_m2"], 6.0)
                self.assertLess(sum(r["uncovered_m2"] for r in rows), 20.0)

    def test_the_cafe_rows_close_the_morning_stripes(self):
        # 2026-11-12: low sun through the east glazing meets the cafe shells first. GPU-2 at shadow quality Low (22 m cascade
        # outdoors at 08:00) shows the lobby behind the ground-floor door sun-lit without the rows and dark with them.
        bare = {r["record"]["name"]: r["uncovered_m2"] for r in self.bare_cover.at(ds.sun_direction(9.0))}
        self.assertTrue(25.0 < bare["Sanatorium_Cafe_second_floor_SHADOW_LOD0"] < 36.0, bare)
        self.assertTrue(6.0 < bare["Sanatorium_Cafe_ground_floor_SHADOW_LOD0"] < 13.0, bare)
        for hour in (8.0, 9.0, 10.0):
            with self.subTest(hour=hour):
                rows = {r["record"]["name"]: r["uncovered_m2"] for r in self.cover.at(ds.sun_direction(hour))}
                for name in self.CAFE:
                    self.assertNotIn(name, rows)                            # listed now, no candidates any more
                self.assertTrue(35.0 < rows["STENCIL_TUNNEL"] < 125.0, rows)   # under the east terrace, outside the play space
                building = {n: a for n, a in rows.items() if n != "STENCIL_TUNNEL"}
                self.assertLess(max(building.values()), 8.0)
                self.assertLess(sum(building.values()), 16.0)


@unittest.skipUnless(AUTOSERVICE.is_dir(), "work/extract/autoservice-native-v2 is not available (set ARENAMAPPORT_WORK_DIR)")
class AutoServiceTests(unittest.TestCase):
    def test_the_hall_hulls_are_the_defect_and_the_sidecar_closes_it(self):
        sun = ds.sun_direction(15.5)
        bare = ds.census(AUTOSERVICE)
        row = worst(ds.Coverage(bare, min_edge=5.0).at(sun))
        self.assertEqual(row["record"]["name"], "AutoService_Building_OUTDOOR_SHADOW_LOD0")
        self.assertGreater(row["uncovered_m2"], 0.9 * row["facing_m2"])
        self.assertGreater(row["uncovered_m2"], 3000.0)
        fixed = ds.census(AUTOSERVICE, sidecar_paths("autoservice"))
        self.assertEqual(fixed.skipped_paths, [])
        self.assertEqual(sum(1 for r in fixed.native if r.get("enrolled")), 2)
        self.assertEqual(fixed.draw_records, bare.draw_records + 2)
        rows = ds.Coverage(fixed, min_edge=5.0).at(sun)
        self.assertLess(max(r["uncovered_m2"] for r in rows), 1.0)


@unittest.skipUnless(EQUATOR.is_dir(), "work/extract/equator-native-v5 is not available (set ARENAMAPPORT_WORK_DIR)")
class EquatorTests(unittest.TestCase):
    def test_the_three_floor_proxies_are_the_defect_and_the_sidecar_closes_it(self):
        sun = ds.sun_direction(15.5)
        bare = ds.census(EQUATOR)
        rows = {r["record"]["name"]: r["uncovered_m2"] for r in ds.Coverage(bare, min_edge=5.0).at(sun) if r["record"]["cast"] == "ShadowsOnly"}
        self.assertEqual(sorted(rows), ["equator_floor_01_SHADOW_LOD0", "equator_floor_02_SHADOW_LOD0", "equator_floor_03_SHADOW_LOD0"])
        self.assertGreater(min(rows.values()), 300.0)
        fixed = ds.census(EQUATOR, sidecar_paths("equator"), two_sided=True)   # DistantShadowTwoSided is C#-only: Equator true
        self.assertEqual(fixed.skipped_paths, [])
        self.assertEqual({r["sides"] for r in fixed.native if r.get("enrolled")}, {"both"})
        self.assertIsNone(worst(ds.Coverage(fixed, min_edge=5.0).at(sun)))    # no ShadowsOnly candidate of that size is left


if __name__ == "__main__":
    unittest.main()
