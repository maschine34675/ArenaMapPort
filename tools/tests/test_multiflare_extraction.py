"""Synthetic MultiFlare serialization checks; no proprietary source fixtures."""
from copy import deepcopy
import json
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_multiflare as M


def string(value):
    raw = value.encode()
    return struct.pack("<i", len(raw)) + raw + b"\0" * (-len(raw) % 4)


def header(enabled=1, name=""):
    return struct.pack("<iqB3xiq", 0, 50, enabled, 1, 60) + string(name)


def light(**changes):
    p = dict(enabled=1, total_scale=.5, total_alpha=.75, cull_distance=500,
             scale=(2, 1), alpha=.8, color=(1, .5, .1, 1), flare_type=2,
             min_dist=.1, max_dist=40, min_scale=2.5, max_scale=1,
             min_alpha=3, max_alpha=.3, tex_id=0)
    p.update(changes)
    return (header(p["enabled"]) + struct.pack("<3fi", p["total_scale"], p["total_alpha"], p["cull_distance"], 1)
            + struct.pack("<2ff4fi6fi", *p["scale"], p["alpha"], *p["color"], p["flare_type"],
                          p["min_dist"], p["max_dist"], p["min_scale"], p["max_scale"],
                          p["min_alpha"], p["max_alpha"], p["tex_id"]))


def settings(extra=1, material_id=44, capacity=2048, blind=1, atlas_id=21):
    raw = header() + struct.pack("<iq", 4, atlas_id)
    for c in (capacity, 512, 256):
        raw += struct.pack("<iiqff", c, 4, material_id, blind, 1)
    return raw + struct.pack("<ifff", 5, .5, .25, extra)


def atlas(rect=(.5, .25, .25, .25)):
    return header(name="Test Atlas") + struct.pack("<i", 1) + string("Test Element") + struct.pack("<4f", *rect)


def graph():
    s = M.decode_settings(settings())
    s.pop("atlas_pointer")
    s["active_in_hierarchy"] = True
    for k in ("normal_batch", "noise_batch", "overlap_batch"):
        s[k].pop("material_pointer")
        s[k]["material_id"] = "m"
    l = M.decode_light(light())
    l.update(id="l", scene="Arena_Equator_Lighting", active_in_hierarchy=True,
             transform_chain=[dict(position=[1, 2, 3], rotation=[0, 0, 0, 1], scale=[1, 1, 1], active_self=True, layer=0)])
    return dict(schema_version=1, map_key="arena_equator", settings=s, atlas=M.decode_atlas(atlas()),
                materials=[dict(id="m", shader="Custom/multiFlare", textures=[dict(texture_id="t")])],
                textures=[dict(id="t", file="textures/test.bin", width=8, height=8, mip_count=4, format=25, byte_count=112)],
                lights=[l])


class MultiFlareExtractionTests(unittest.TestCase):
    def test_light_preserves_all_fields_including_new_source_cull_distance(self):
        d = M.decode_light(light())
        self.assertEqual(len(light()), 108)
        self.assertEqual((d["total_scale"], d["total_alpha"], d["cull_distance"]), (.5, .75, 500))
        f = d["flares"][0]
        self.assertEqual(f["scale"], [2, 1])
        self.assertEqual(f["flare_type"], 2)
        self.assertAlmostEqual(f["color"][2], .1)
        self.assertEqual((f["min_scale"], f["max_scale"], f["min_alpha"]), (2.5, 1, 3))
        self.assertAlmostEqual(f["max_alpha"], .3)
        self.assertEqual(f["tex_id"], 0)

    def test_disabled_light_is_preserved(self):
        self.assertFalse(M.decode_light(light(enabled=0))["enabled"])

    def test_source_settings_preserve_all_three_batches_and_extension(self):
        d = M.decode_settings(settings())
        self.assertEqual(len(settings()), 132)
        self.assertEqual(d["atlas_pointer"], dict(fileId=4, pathId=21))
        self.assertEqual(d["normal_batch"]["capacity"], 2048)
        self.assertEqual(d["noise_batch"]["capacity"], 512)
        self.assertEqual(d["overlap_batch"]["search_range"], .5)
        self.assertEqual(d["overlap_batch"]["max_scale_multiplier"], .25)
        self.assertEqual(d["source_extra_scalar"], 1)

    def test_exact_consumption_rejects_missing_or_unknown_bytes(self):
        for decoder, raw in ((M.decode_light, light()), (M.decode_settings, settings()), (M.decode_atlas, atlas())):
            for altered in (raw[:15], raw[:-1], raw+b"\0\0\0\0"):
                with self.subTest(decoder=decoder.__name__, length=len(altered)), self.assertRaises(ValueError):
                    decoder(altered)

    def test_old_target_layout_cannot_be_mistaken_for_source_layout(self):
        raw = light()
        with self.assertRaises(ValueError):
            M.decode_light(raw[:40]+raw[44:])

    def test_invalid_light_values_fail_closed(self):
        for change in (dict(total_scale=-1), dict(total_alpha=2), dict(cull_distance=0), dict(alpha=float("nan")),
                       dict(max_dist=.05), dict(min_scale=-1), dict(min_scale=0), dict(min_alpha=0), dict(flare_type=1), dict(tex_id=-1),
                       dict(enabled=2), dict(color=(1, 1, 1, float("inf")))):
            with self.subTest(change=change), self.assertRaises(ValueError):
                M.decode_light(light(**change))

    def test_unconfirmed_settings_extensions_and_dangling_refs_rejected(self):
        for change in (dict(extra=.5), dict(extra=float("nan")), dict(material_id=0),
                       dict(atlas_id=0), dict(capacity=0), dict(blind=2)):
            with self.subTest(change=change), self.assertRaises(ValueError):
                M.decode_settings(settings(**change))

    def test_atlas_rect_and_names_preserved(self):
        d = M.decode_atlas(atlas())
        self.assertEqual(d["name"], "Test Atlas")
        self.assertEqual(d["containers"], [dict(name="Test Element", rect=[.5, .25, .25, .25])])

    def test_invalid_atlas_rectangles_rejected(self):
        for rect in ((0, 0, 0, 1), (-.1, 0, 1, 1), (.9, 0, .2, 1), (0, 0, 1, float("nan"))):
            with self.subTest(rect=rect), self.assertRaises(ValueError):
                M.decode_atlas(atlas(rect))

    def test_bc7_chain_keeps_small_mips_as_whole_blocks(self):
        self.assertEqual(M.texture_byte_count(8, 8, 4, 25), 64+16+16+16)
        self.assertEqual(M.texture_byte_count(1024, 1024, 1, 25), 1048576)
        self.assertEqual(M.texture_byte_count(1024, 1024, 11, 25), 1398128)

    def test_unknown_texture_formats_and_invalid_mip_counts_rejected(self):
        for args in ((8, 8, 4, 12), (0, 8, 1, 25), (8, 8, 5, 25), (8, 8, 0, 25)):
            with self.subTest(args=args), self.assertRaises(ValueError):
                M.texture_byte_count(*args)

    def test_valid_graph_and_inactive_ancestors(self):
        d = graph()
        M.validate_graph(d)
        d["lights"][0]["active_in_hierarchy"] = False
        d["lights"][0]["transform_chain"][0]["active_self"] = False
        M.validate_graph(d)

    def test_graph_rejects_duplicate_identity_and_dangling_edges(self):
        for action in (lambda d: d["lights"].append(deepcopy(d["lights"][0])),
                       lambda d: d["settings"]["noise_batch"].update(material_id="missing"),
                       lambda d: d["materials"][0]["textures"][0].update(texture_id="missing"),
                       lambda d: d["lights"][0]["flares"][0].update(tex_id=1)):
            d = graph()
            action(d)
            with self.assertRaises(ValueError):
                M.validate_graph(d)

    def test_graph_rejects_bad_transforms_activity_and_shader(self):
        for action in (lambda d: d["lights"][0].update(active_in_hierarchy=False),
                       lambda d: d["lights"][0]["transform_chain"][0].update(rotation=[0, 0, 0, 0]),
                       lambda d: d["lights"][0]["transform_chain"][0].update(scale=[1, 0, 1]),
                       lambda d: d["lights"][0].update(scene="unowned"),
                       lambda d: d["materials"][0].update(shader="unknown")):
            d = graph()
            action(d)
            with self.assertRaises(ValueError):
                M.validate_graph(d)

    def test_graph_rejects_corrupt_texture_shape_and_output_path(self):
        for changes in (dict(byte_count=64), dict(file="../test.bin"), dict(file="textures/../../test.bin")):
            d = graph()
            d["textures"][0].update(changes)
            with self.assertRaises(ValueError):
                M.validate_graph(d)


class InactiveLightRuleAndBowlPinTests(unittest.TestCase):
    """2026-10-18: Bowl joins; its six StreetLampFlareDistant lights are inactive and carry min 50 > max 15."""

    @staticmethod
    def bowl_graph(inactive_inverted=6, active=4):
        d = graph()
        d["map_key"] = "arena_bowl"
        template = d["lights"][0]
        lights = []
        for i in range(active):
            l = deepcopy(template)
            l.update(id=f"active{i}", scene="Arena_Bowl_Light")
            lights.append(l)
        for i in range(inactive_inverted):
            l = deepcopy(template)
            l.update(id=f"inactive{i}", scene="Arena_Bowl_Light", active_in_hierarchy=False)
            l["transform_chain"][0]["active_self"] = False
            l["flares"][0].update(min_dist=50.0, max_dist=15.0)
            lights.append(l)
        d["lights"] = lights
        return d

    def test_light_is_active_needs_enabled_component_and_active_chain(self):
        for enabled, in_hierarchy, expected in ((True, True, True), (True, False, False),
                                                (False, True, False), (False, False, False)):
            with self.subTest(enabled=enabled, in_hierarchy=in_hierarchy):
                self.assertIs(M.light_is_active(dict(enabled=enabled, active_in_hierarchy=in_hierarchy)), expected)

    def test_inverted_range_is_accepted_only_on_inactive_lights(self):
        inverted = dict(min_dist=50.0, max_dist=15.0)
        self.assertEqual(M.decode_light(light(enabled=0, **inverted))["flares"][0]["min_dist"], 50.0)
        self.assertEqual(M.decode_light(light(**inverted), in_hierarchy=False)["flares"][0]["max_dist"], 15.0)
        self.assertEqual(M.decode_light(light(enabled=0, **inverted), in_hierarchy=False)["enabled"], False)
        for kwargs in ({}, dict(in_hierarchy=True)):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                M.decode_light(light(**inverted), **kwargs)
        for equal in (dict(min_dist=15.0, max_dist=15.0), dict(min_dist=0.0, max_dist=0.0)):
            with self.subTest(equal=equal), self.assertRaises(ValueError):
                M.decode_light(light(**equal))

    def test_inactive_lights_keep_every_other_requirement(self):
        for change in (dict(total_scale=-1), dict(total_alpha=2), dict(cull_distance=0), dict(alpha=float("nan")),
                       dict(min_dist=-1), dict(max_dist=-1), dict(min_scale=-1), dict(min_scale=0), dict(min_alpha=0),
                       dict(flare_type=1), dict(tex_id=-1), dict(color=(1, 1, 1, float("inf")))):
            with self.subTest(change=change, case="disabled component"), self.assertRaises(ValueError):
                M.decode_light(light(enabled=0, **change))
            with self.subTest(change=change, case="inactive chain"), self.assertRaises(ValueError):
                M.decode_light(light(**change), in_hierarchy=False)

    def test_graph_accepts_bowl_shaped_inactive_inversions_and_activity_follows_the_chain(self):
        d = self.bowl_graph()
        M.validate_graph(d)
        self.assertEqual(sum(M.light_is_active(l) for l in d["lights"]), 4)
        disabled = self.bowl_graph(inactive_inverted=0)
        disabled["lights"][0]["enabled"] = False  # component off, chain active: still inactive
        disabled["lights"][0]["flares"][0].update(min_dist=50.0, max_dist=15.0)
        M.validate_graph(disabled)

    def test_graph_rejects_inverted_range_on_a_light_the_runtime_would_consume(self):
        d = self.bowl_graph(inactive_inverted=0)
        d["lights"][0]["flares"][0].update(min_dist=50.0, max_dist=15.0)
        with self.assertRaises(ValueError):
            M.validate_graph(d)
        # An inverted range does not excuse a hierarchy flag that contradicts the transform chain.
        d = self.bowl_graph()
        d["lights"][-1]["active_in_hierarchy"] = True
        with self.assertRaises(ValueError):
            M.validate_graph(d)

    def test_graph_still_checks_the_rest_of_an_inactive_light(self):
        for change in (lambda f: f.update(min_scale=0), lambda f: f.update(tex_id=5), lambda f: f.update(max_dist=-2.0)):
            d = self.bowl_graph()
            change(d["lights"][-1]["flares"][0])
            with self.assertRaises(ValueError):
                M.validate_graph(d)

    def test_every_supported_map_has_scenes_matching_its_sidecar_and_a_census_pin(self):
        maps = Path(__file__).resolve().parents[1] / "maps"
        self.assertEqual(set(M.SCENES), set(M.EXPECTED_CENSUS))
        for key, scenes in M.SCENES.items():
            sidecar = json.loads((maps / (key.removeprefix("arena_") + ".json")).read_text(encoding="utf-8"))
            with self.subTest(map=key):
                self.assertEqual(set(sidecar["all_scene_names"]), set(scenes.values()))
                self.assertEqual(len(set(scenes)), len(scenes))
                lights, active, flares, active_flares = M.EXPECTED_CENSUS[key]
                self.assertTrue(0 < active <= lights and 0 < active_flares <= flares)

    def test_bowl_pins_scene_table_and_census(self):
        self.assertEqual(M.SCENES["arena_bowl"], {
            41: "Arena_Bowl", 43: "Arena_Bowl_DesignStuff", 44: "Arena_Bowl_Scripts", 45: "Arena_Bowl_Light",
            46: "Arena_Bowl_Building", 47: "Arena_Bowl_TeamFight", 48: "Arena_Bowl_TeamFight_DesignStuff",
            95: "Arena_Bowl_Grass"})
        self.assertEqual(M.EXPECTED_CENSUS["arena_bowl"], (28, 4, 130, 20))
        # The two accepted maps keep their original pins.
        self.assertEqual(M.EXPECTED_CENSUS["arena_equator"], (28, 25, 48, 33))
        self.assertEqual(M.EXPECTED_CENSUS["arena_bay5"], (25, 24, 116, 111))
        self.assertEqual(set(M.SCENES["arena_equator"]), {51, 53, 54, 55, 57, 58})
        self.assertEqual(set(M.SCENES["arena_bay5"]), {79, 80, 81, 82, 83, 84, 85, 86, 88, 89})

    def test_graph_scene_ownership_is_per_map(self):
        d = self.bowl_graph()
        d["lights"][0]["scene"] = "Arena_Equator_Lighting"
        with self.assertRaises(ValueError):
            M.validate_graph(d)


if __name__ == "__main__":
    unittest.main()
