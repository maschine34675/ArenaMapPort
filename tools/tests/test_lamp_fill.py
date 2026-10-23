"""lamp_fill (LampFillRepairs, user decision 2026-10-13): the empty cells of the passport-hall ring-lamp lattice get dark
copies of one authored lamp at raid start.

tools/maps/airpit.json carries `lamp_fill` {template, y, cells [[x, z], ...]}; ArenaMapRegistry.AirPit.LampFill is its C#
twin (`new ArenaLampFillDescriptor(path, y, new[] { x0, z0, ... })`). The descriptor validator keeps the two in step and
rejects malformed values; this file pins the content and, when the ignored v8 package is present, checks the cells against
the authored lamps of Arena_Airpit_Light: every cell is at least 3.0 m (xz) from every authored `Airport_lamp_02` unit, the
template resolves to an authored DARK lamp (material `Airpit_emissive_01_off`) and the cells sit on the lamp lattice. The C#
side is ArenaMapPort.Server.Tests (AirPitMapTests.LampFillMatchesTheSidecar, MapDescriptorConsistencyTests.LampFill_ExistsForAirPitOnly,
LampFillDescriptorTests).
"""
import contextlib
import copy
import io
import json
import math
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
import validate_map_descriptors as VD  # noqa: E402

TEMPLATE = "SBG_Airpit_Light/OO/Airport_lamp_02/Airport_lamp_02_disabled"
FILL_Y = 6.83
CELL_COUNT = 82
OFF_MATERIAL = "69cb23528f367efc"      # Airpit_emissive_01_off: the "LIGHT OFF" variant (emission power 0), slot 1 of the LOD0 renderer
# The lattice of level37's 45 authored lamps (17 columns x 9 rows, pitch about 3.17 x 3.32 m); the authored units sit within 0.02 of it.
COLUMNS = [-36.95, -33.65, -30.37, -27.29, -24.13, -21.01, -17.87, -14.71, -11.57, -8.43, -5.21, -2.14, 1.11, 4.28, 7.42, 10.64, 13.81]
ROWS = [56.44, 59.72, 63.13, 66.38, 69.70, 73.13, 76.47, 79.76, 83.01]
LATTICE_TOLERANCE = 0.02
MIN_DISTANCE_TO_AUTHORED = 3.0
# Measured on the v8 meshes (test_no_lamp_disc_sinks_into_a_round_column re-measures them): the flat disc of Airport_lamp_02's LOD0
# (y 5.354-5.5) reaches 1.283 m from the lamp axis; Airport_column_02's LOD0 shaft is 0.913 m in radius from y 5.23 to 5.67 (the capital
# widens above that).
LAMP_DISC_RADIUS = 1.284
COLUMN_RADIUS_AT_THE_DISCS = 0.914
COLUMN_NAME = "Airport_column_02"
# The review of the first version dropped (-30.37, 73.13) because its rim sits 0.13 m inside the western column at
# (-32.44, 73.18); the user put it back (2026-10-13 midday: the lattice runs just as close past the eastern column, where
# (7.42, 73.13) is filled 2.64 m from the axis, and that look is fine). It is the only cell allowed inside the clearance.
ACCEPTED_COLUMN_CELL = [-30.37, 73.13]
ACCEPTED_DIP = 0.13
PACKAGE = REPO / "work" / "extract" / "airpit-native-v9"
HAVE_PACKAGE = (PACKAGE / "scenes" / "Arena_Airpit_Light.json").is_file()


def sidecar(name):
    return json.loads((REPO / "tools" / "maps" / name).read_text(encoding="utf-8"))


def map_sidecars():
    return {path.name: json.loads(path.read_text(encoding="utf-8")) for path in VD.map_descriptor_paths(REPO / "tools" / "maps")}


class SidecarContentTests(unittest.TestCase):
    def setUp(self):
        self.fill = sidecar("airpit.json")["lamp_fill"]

    def test_block_shape(self):
        self.assertEqual(set(self.fill), {"template", "y", "cells"})
        self.assertEqual(self.fill["template"], TEMPLATE)
        self.assertEqual(self.fill["y"], FILL_Y)
        self.assertEqual(len(self.fill["cells"]), CELL_COUNT)
        self.assertEqual(VD.lamp_fill_errors({"lamp_fill": self.fill}), [])

    def test_cells_lie_on_the_lattice_inside_the_passport_hall(self):
        for x, z in self.fill["cells"]:
            self.assertLessEqual(min(abs(x - c) for c in COLUMNS), LATTICE_TOLERANCE, (x, z))
            self.assertLessEqual(min(abs(z - r) for r in ROWS), LATTICE_TOLERANCE, (x, z))
            self.assertTrue(-38 <= x <= 15 and 55 <= z <= 84, (x, z))

    def test_cells_are_distinct_and_never_adjacent(self):
        cells = [tuple(c) for c in self.fill["cells"]]
        self.assertEqual(len(set(cells)), len(cells))
        nearest = min(math.hypot(a[0] - b[0], a[1] - b[1]) for i, a in enumerate(cells) for b in cells[i + 1:])
        self.assertGreaterEqual(nearest, 3.0)        # the lattice pitch; the validator's own floor is 2.0 m

    def test_the_cell_beside_the_western_column_is_filled_on_the_users_decision(self):
        self.assertIn(ACCEPTED_COLUMN_CELL, self.fill["cells"])
        self.assertEqual(self.fill["cells"].count(ACCEPTED_COLUMN_CELL), 1)

    def test_only_airpit_carries_lamp_fill(self):
        sidecars = map_sidecars()
        self.assertEqual([name for name, data in sidecars.items() if "lamp_fill" in data], ["airpit.json"])
        for name, data in sidecars.items():
            self.assertEqual(VD.lamp_fill_errors(data), [], name)

    def test_the_other_maps_declare_no_lamp_fill_in_the_registry(self):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        entries = VD.registry_entries(text)
        self.assertEqual([name for name, args in entries.items() if "LampFill" in args], ["AirPit"])


class ValidatorTests(unittest.TestCase):
    def check(self, mutate):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        entry = VD.registry_entries(text)["AirPit"]
        data = copy.deepcopy(sidecar("airpit.json"))
        mutate(data)
        errors: list = []
        VD.check_map_against_registry_entry(data, entry, {"ArenaEquatorLocation": {}}, Path("airpit.json"), "AirPit", errors)
        return errors

    def lamp_errors(self, **changes):
        return VD.lamp_fill_errors({"lamp_fill": {**sidecar("airpit.json")["lamp_fill"], **changes}})

    def test_the_registry_literal_is_parsed_into_the_sidecar_shape(self):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        literal = VD.registry_entries(text)["AirPit"]["LampFill"]
        self.assertEqual(literal, sidecar("airpit.json")["lamp_fill"])

    def test_untouched_sidecar_is_clean(self):
        self.assertEqual(self.check(lambda d: None), [])

    def test_sidecar_drift_from_the_runtime_descriptor_is_reported(self):
        self.assertTrue(any("lamp_fill mismatch" in e for e in self.check(lambda d: d["lamp_fill"]["cells"][0].__setitem__(0, -33.66))))
        self.assertTrue(any("lamp_fill mismatch" in e for e in self.check(lambda d: d["lamp_fill"]["cells"].pop())))
        self.assertTrue(any("lamp_fill mismatch" in e for e in self.check(lambda d: d["lamp_fill"].__setitem__("y", 6.8))))
        self.assertTrue(any("lamp_fill mismatch" in e for e in self.check(
            lambda d: d["lamp_fill"].__setitem__("template", TEMPLATE + "2"))))
        self.assertTrue(any("lamp_fill mismatch" in e for e in self.check(lambda d: d.pop("lamp_fill"))))

    def check_consistent_change(self, mutate):
        """The same change in the sidecar AND in the parsed registry literal: the two agree field for field, so the
        'lamp_fill mismatch' comparison stays silent and only lamp_fill_errors can refuse it on the CLI path."""
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        entry = VD.registry_entries(text)["AirPit"]
        data = copy.deepcopy(sidecar("airpit.json"))
        mutate(data["lamp_fill"])
        mutate(entry["LampFill"])
        errors: list = []
        VD.check_map_against_registry_entry(data, entry, {"ArenaEquatorLocation": {}}, Path("airpit.json"), "AirPit", errors)
        return errors

    def test_the_structural_checks_run_on_the_cli_path_even_when_sidecar_and_registry_agree(self):
        # control: an agreeing change that keeps the block valid is clean, so the two refusals below come from lamp_fill_errors
        self.assertEqual(self.check_consistent_change(lambda fill: fill.__setitem__("y", 6.9)), [])
        errors = self.check_consistent_change(lambda fill: fill["cells"].append(list(fill["cells"][0])))
        self.assertTrue(any("duplicates" in e for e in errors), errors)
        self.assertFalse(any("lamp_fill mismatch" in e for e in errors), errors)
        errors = self.check_consistent_change(lambda fill: fill["cells"].append([fill["cells"][0][0] + 1.0, fill["cells"][0][1]]))
        self.assertTrue(any("closer than" in e for e in errors), errors)
        self.assertFalse(any("lamp_fill mismatch" in e for e in errors), errors)
        errors = self.check_consistent_change(lambda fill: fill.__setitem__("y", math.inf))
        self.assertTrue(any("lamp_fill y" in e for e in errors), errors)

    def test_a_sidecar_lamp_fill_without_a_registry_literal_is_reported(self):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        entry = VD.registry_entries(text)["Bay5"]
        data = copy.deepcopy(sidecar("bay5.json"))
        errors: list = []
        VD.check_map_against_registry_entry(data, entry, {"ArenaEquatorLocation": {}}, REPO / "tools" / "maps" / "bay5.json", "Bay5", errors)
        self.assertEqual(errors, [])                                  # control: Bay5 as shipped is clean
        data["lamp_fill"] = copy.deepcopy(sidecar("airpit.json")["lamp_fill"])
        errors = []
        VD.check_map_against_registry_entry(data, entry, {"ArenaEquatorLocation": {}}, REPO / "tools" / "maps" / "bay5.json", "Bay5", errors)
        self.assertTrue(any("lamp_fill mismatch" in e for e in errors))

    def test_malformed_values_are_rejected(self):
        self.assertEqual(self.lamp_errors(), [])
        cells = sidecar("airpit.json")["lamp_fill"]["cells"]
        for bad in ("x", [], [[1.0]], [[1.0, 2.0, 3.0]], [[1.0, math.inf]], [[math.nan, 1.0]], [[1.0, True]], [["1", 2.0]], [None]):
            self.assertTrue(any("cells must be" in e for e in self.lamp_errors(cells=bad)), bad)
        self.assertTrue(any("duplicates" in e for e in self.lamp_errors(cells=cells + [list(cells[0])])))
        self.assertTrue(any("closer than" in e for e in self.lamp_errors(cells=cells + [[cells[0][0] + 1.0, cells[0][1]]])))
        self.assertEqual(self.lamp_errors(cells=cells + [[100.0, 100.0], [102.0, 100.0]]), [])         # exactly the 2.0 m floor is allowed
        for bad in ("", " a/b", "a", "a//b", "/a/b", "a/b/", 3, None):
            self.assertTrue(any("template" in e for e in self.lamp_errors(template=bad)), bad)
        for bad in (math.inf, math.nan, "6.83", None, True):
            self.assertTrue(any("lamp_fill y" in e for e in self.lamp_errors(y=bad)), bad)
        for bad in ({}, {"template": TEMPLATE}, [], "x", {**sidecar("airpit.json")["lamp_fill"], "extra": 1}):
            self.assertTrue(any("exactly template" in e for e in VD.lamp_fill_errors({"lamp_fill": bad})), bad)

    def test_a_map_without_lamp_fill_is_valid(self):
        self.assertEqual(VD.lamp_fill_errors({}), [])
        self.assertEqual(VD.lamp_fill_errors(sidecar("equator.json")), [])
        self.assertEqual(VD.lamp_fill_errors(sidecar("bay5.json")), [])

    def test_the_literal_parser_pairs_the_flat_array_and_refuses_an_odd_one(self):
        good = 'new ArenaLampFillDescriptor("A/B", 6.5f, new[] { 1.0f, 2.0f, -3.5f, 4.25f })'
        self.assertEqual(VD.parse_cs_value(good), {"template": "A/B", "y": 6.5, "cells": [[1.0, 2.0], [-3.5, 4.25]]})
        for bad in ('new ArenaLampFillDescriptor("A/B", 6.5f, new[] { 1.0f, 2.0f, 3.0f })',           # odd
                    'new ArenaLampFillDescriptor("A/B", 6.5f)',                                       # no cells
                    'new ArenaLampFillDescriptor(6.5f, "A/B", new[] { 1.0f, 2.0f })',                 # arguments swapped
                    'new ArenaLampFillDescriptor("A/B", 6.5f, new[] { 1.0f, 2.0f }'):                 # unbalanced
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit, msg=bad):
                VD.parse_cs_value(bad)


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/airpit-native-v9 not present (the furnished hall plus the crates under the tarp)")
class PackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import port_colliders as pc
        cls.pc = pc
        scene = pc.Scene(json.loads((PACKAGE / "scenes" / "Arena_Airpit_Light.json").read_text(encoding="utf-8")))
        cls.scene = scene
        found, unresolved = pc.resolve_barriers({scene.name: scene}, [TEMPLATE])
        cls.found, cls.unresolved = found, unresolved
        cls.template = found[0][1] if found else None
        cls.group = scene.objects[cls.template]["parent_id"] if found else None
        cls.units = [c for c in scene.children[cls.group] if scene.objects[c]["name"].startswith("Airport_lamp_02")] if found else []

    def lod0_materials(self, unit):
        (lod0,) = [c for c in self.scene.children[unit]
                   if self.scene.objects[c]["name"].endswith("_LOD0") and "SHADOW" not in self.scene.objects[c]["name"]]
        (renderer,) = [comp for comp in self.scene.objects[lod0]["components"] if comp["type"] == "MeshRenderer"]
        return renderer["material_ids"]

    def test_the_template_path_resolves_to_one_active_object_below_the_lamp_group(self):
        self.assertEqual((len(self.found), self.unresolved), (1, []))
        self.assertEqual(self.found[0][0], "Arena_Airpit_Light")
        scene = self.scene
        self.assertTrue(scene.chain_active(self.template))
        self.assertEqual(scene.objects[self.group]["name"], "Airport_lamp_02")      # the parent the copies are made under
        self.assertEqual(scene.objects[self.template]["name"], "Airport_lamp_02_disabled")

    def test_the_template_is_an_authored_dark_lamp_unit(self):
        scene = self.scene
        self.assertIn("LODGroup", [c["type"] for c in scene.objects[self.template]["components"]])
        self.assertIn(OFF_MATERIAL, self.lod0_materials(self.template))
        names = sorted(scene.objects[c]["name"] for c in scene.children[self.template])
        self.assertEqual(names, sorted(f"Airport_lamp_02_{part}" for part in
                                       ("LOD0", "LOD1", "LOD2", "LOD3", "SHADOW_LOD0", "SHADOW_LOD1", "SHADOW_LOD2", "BALLISTIC_plastic")))
        (ballistic,) = [c for c in scene.children[self.template] if scene.objects[c]["name"].endswith("_BALLISTIC_plastic")]
        self.assertEqual(scene.objects[ballistic]["layer"], 12)        # ballistic layer only: no movement layer, no NavMesh source
        self.assertEqual([c["type"] for c in scene.objects[ballistic]["components"]], ["MeshCollider", "BallisticCollider"])
        self.assertEqual(scene.objects[self.template]["local_scale"], [1.0, 1.0, 1.0])

    def test_level37_authors_45_units_two_of_them_dark(self):
        self.assertEqual(len(self.units), 45)
        dark = sorted(self.scene.objects[u]["name"] for u in self.units if OFF_MATERIAL in self.lod0_materials(u))
        self.assertEqual(dark, ["Airport_lamp_02", "Airport_lamp_02_disabled"])

    def test_every_cell_is_far_from_every_authored_lamp(self):
        cells = sidecar("airpit.json")["lamp_fill"]["cells"]
        authored = [(float(self.scene.world(u)[0, 3]), float(self.scene.world(u)[2, 3])) for u in self.units]
        self.assertEqual(len(authored), 45)
        nearest = min(math.hypot(x - ax, z - az) for x, z in cells for ax, az in authored)
        self.assertGreaterEqual(nearest, MIN_DISTANCE_TO_AUTHORED, nearest)

    def mesh_world_vertices(self, scene, unit, suffix):
        (part,) = [c for c in scene.children[unit] if scene.objects[c]["name"].endswith(suffix) and "SHADOW" not in scene.objects[c]["name"]]
        (mesh_filter,) = [comp for comp in scene.objects[part]["components"] if comp["type"] == "MeshFilter"]
        vertices, _ = self.pc.read_mesh_bin(PACKAGE / "meshes" / f"{mesh_filter['mesh_id']}.mesh.bin")
        world = scene.world(part)
        return (world[:3, :3] @ vertices.T).T + world[:3, 3]

    def test_only_the_accepted_cell_dips_into_a_round_column(self):
        import numpy as np
        # the lamp disc: LOD0 of the template, radius about its own axis
        lamp = self.mesh_world_vertices(self.scene, self.template, "lamp_02_LOD0")
        axis = self.scene.world(self.template)[:3, 3]
        disc = float(np.hypot(lamp[:, 0] - axis[0], lamp[:, 2] - axis[2]).max())
        self.assertLessEqual(disc, LAMP_DISC_RADIUS)
        self.assertGreater(disc, LAMP_DISC_RADIUS - 0.01)
        # the round columns standing in the lattice area (Arena_AirPit_ShootOut), measured the same way
        shoot = self.pc.Scene(json.loads((PACKAGE / "scenes" / "Arena_AirPit_ShootOut.json").read_text(encoding="utf-8")))
        columns = []
        for key, obj in shoot.objects.items():
            if obj["name"].split(" (")[0] != COLUMN_NAME or not shoot.chain_active(key):
                continue
            world = shoot.world(key)
            if -40 <= world[0, 3] <= 16 and 55 <= world[2, 3] <= 85 and world[1, 3] < 1:
                columns.append((key, float(world[0, 3]), float(world[2, 3])))
        self.assertEqual(len(columns), 2)
        for key, cx, cz in columns:
            mesh = self.mesh_world_vertices(shoot, key, "column_02_LOD0")
            low = mesh[(mesh[:, 1] >= 5.2) & (mesh[:, 1] <= 5.7)]   # the straight shaft ring band around the discs (y 5.35-5.5)
            radius = float(np.hypot(low[:, 0] - cx, low[:, 2] - cz).max())
            self.assertLessEqual(radius, COLUMN_RADIUS_AT_THE_DISCS)
            self.assertGreater(radius, COLUMN_RADIUS_AT_THE_DISCS - 0.01)
        cells = sidecar("airpit.json")["lamp_fill"]["cells"]
        clearance = LAMP_DISC_RADIUS + COLUMN_RADIUS_AT_THE_DISCS
        inside = [(x, z, cx, cz, math.hypot(x - cx, z - cz)) for x, z in cells for _, cx, cz in columns
                  if math.hypot(x - cx, z - cz) < clearance]
        # exactly the accepted cell, beside the western column, and by no more than the documented dip
        self.assertEqual([(x, z) for x, z, *_ in inside], [tuple(ACCEPTED_COLUMN_CELL)])
        (_, _, cx, cz, distance), = inside
        self.assertEqual((round(cx, 2), round(cz, 2)), (-32.44, 73.18))
        self.assertLessEqual(clearance - distance, ACCEPTED_DIP + 0.005, clearance - distance)
        # the eastern column has the lattice beside it too, but 2.64 m from its axis: a clear 0.4 m
        east = min(math.hypot(x - cx, z - cz) for x, z in cells for _, cx, cz in columns if cx > 0)
        self.assertGreaterEqual(east, clearance + 0.4, east)

    def test_authored_lamps_define_the_lattice_and_the_height(self):
        for u in self.units:
            world = self.scene.world(u)
            x, y, z = float(world[0, 3]), float(world[1, 3]), float(world[2, 3])
            self.assertLessEqual(min(abs(x - c) for c in COLUMNS), LATTICE_TOLERANCE, self.scene.objects[u]["name"])
            self.assertLessEqual(min(abs(z - r) for r in ROWS), LATTICE_TOLERANCE + 1e-3, self.scene.objects[u]["name"])
            self.assertAlmostEqual(y, FILL_Y, delta=0.005, msg=self.scene.objects[u]["name"])
        world = self.scene.world(self.template)
        self.assertAlmostEqual(float(world[1, 3]), FILL_Y, delta=0.005)


if __name__ == "__main__":
    unittest.main()
