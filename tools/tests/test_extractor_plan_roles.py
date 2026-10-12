"""PLAN-AIRPIT item 4 -- the per-map extractors read their scene from the source plan's roles.

Covers extract_raincover.py (``scripts`` role), extract_spatial_audio.py (``sound`` role and
``--map``) and the generic-package path of extract_decals.py's refresh CLI. The AI-anchor
table lives in test_ai_anchors.py and the mode-object sidecar in test_mode_objects.py. Plans
and fakes only; the last class of each section reads the live Arena install and skips on a
machine without it, like test_ai_anchors.py.
"""
from __future__ import annotations

import contextlib
import inspect
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "extract"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import extract_decals as D  # noqa: E402
import extract_raincover as R  # noqa: E402
import extract_spatial_audio as S  # noqa: E402
from common_ids import HardExtractError  # noqa: E402
from source_plan import PLAN_ROOT, PlanError, load_plan, scene_for_role  # noqa: E402

ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"


def quiet(call, *args, **kwargs):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        result = call(*args, **kwargs)
    return result, out.getvalue(), err.getvalue()


# --------------------------------------------------------------------------
# Rain cover: Scripts scene from the plan role
# --------------------------------------------------------------------------

class RaincoverPlanRoleTests(unittest.TestCase):
    def test_scripts_index_comes_from_the_plan_role(self):
        plan = load_plan(PLAN_ROOT / "airpit.json")
        self.assertEqual(R.scripts_scene_index_for_plan(plan), scene_for_role(plan, "scripts")["index"])
        self.assertEqual(R.scripts_scene_index_for_plan(plan), 36)

    def test_plan_without_the_role_is_an_error_not_a_table_fallback(self):
        with self.assertRaises(PlanError):
            R.scripts_scene_index_for_plan(load_plan(PLAN_ROOT / "bowl.json"))

    def test_legacy_table_is_untouched(self):
        self.assertEqual(R.SCRIPTS_SCENE_INDEX, {"arena_equator": 54, "arena_bay5": 85})

    def test_export_for_plan_passes_the_role_index_and_the_plan_key(self):
        plan = load_plan(PLAN_ROOT / "airpit.json")
        with patch.object(R, "export", return_value={"ok": 1}) as export:
            self.assertEqual(R.export_for_plan("src", "pkg", plan), {"ok": 1})
        export.assert_called_once_with("src", "pkg", "arena_airpit", scripts_scene_index=36)

    def test_parser_needs_exactly_one_of_plan_or_map(self):
        parser = R.build_parser()
        self.assertEqual(parser.parse_args(["--plan", "p.json", "--output", "o"]).plan, "p.json")
        self.assertEqual(parser.parse_args(["--map", "arena_bay5", "--output", "o"]).map, "arena_bay5")
        for argv in (["--output", "o"], ["--plan", "p", "--map", "arena_bay5", "--output", "o"],
                     ["--map", "arena_airpit", "--output", "o"], ["--plan", "p"]):
            with self.subTest(argv=argv), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                parser.parse_args(argv)

    def test_main_plan_path_verifies_the_mapping_then_exports(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output = Path(tmp) / "arena", Path(tmp) / "out"
            source.mkdir()
            summary = {"indoor_trigger_count": 2}
            with patch("source_plan.read_build_scenes", return_value=["x"]) as read, \
                 patch("source_plan.verify_scene_mapping") as verify, \
                 patch.object(R, "export_for_plan", return_value=summary) as export:
                code, out, _err = quiet(R.main, ["--plan", str(PLAN_ROOT / "airpit.json"),
                                                 "--source", str(source), "--output", str(output)])
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out), summary)
        read.assert_called_once()
        verify.assert_called_once()
        self.assertEqual(export.call_args.args[2]["key"], "arena_airpit")

    def test_main_failures_exit_2(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "arena"
            source.mkdir()
            code, _out, err = quiet(R.main, ["--plan", str(Path(tmp) / "missing.json"),
                                             "--source", str(source), "--output", str(Path(tmp) / "o")])
            self.assertEqual((code, "[extract_raincover] FAILED" in err), (2, True))
            code, _out, err = quiet(R.main, ["--plan", str(PLAN_ROOT / "airpit.json"),
                                             "--source", str(source), "--output", str(source / "inside")])
            self.assertEqual((code, "separate" in err), (2, True))
            with patch("source_plan.read_build_scenes", return_value=[]), patch("source_plan.verify_scene_mapping"), \
                 patch.object(R, "export_for_plan", side_effect=ValueError("raincover: boom")):
                code, _out, err = quiet(R.main, ["--plan", str(PLAN_ROOT / "airpit.json"),
                                                 "--source", str(source), "--output", str(Path(tmp) / "o2")])
            self.assertEqual((code, "boom" in err), (2, True))

    @unittest.skipUnless(Path(ARENA_SOURCE).is_dir(), "Arena source not reachable from this machine")
    def test_airpit_against_the_live_install(self):
        plan = load_plan(PLAN_ROOT / "airpit.json")
        with tempfile.TemporaryDirectory() as tmp:
            summary, _out, err = quiet(R.export_for_plan, ARENA_SOURCE, tmp, plan)
            data = json.loads((Path(tmp) / "raincover" / "raincover.json").read_text(encoding="utf-8"))
        self.assertEqual((summary["indoor_trigger_count"], summary["dry_plane_count"], summary["weather_obstacle"]),
                         (2, 0, False))
        self.assertEqual(summary["source_scene"], "Scripts (level36)")
        self.assertEqual(sorted(t["name"] for t in data["indoor_triggers"]), ["Bus_IndoorTrigger", "IndoorTrigger 11"])
        self.assertEqual(data["dry_planes"], [])
        # The Bus trigger's baked centre vs its live transform: 125.76 m is the largest single-axis offset
        # (the old log line presented it as the distance), 155.2 m is the Euclidean distance the plan quotes.
        bus = next(t for t in data["indoor_triggers"] if t["name"] == "Bus_IndoorTrigger")
        offset = [a - b for a, b in zip(bus["bounds_center"], bus["world_position"])]
        self.assertAlmostEqual(max(abs(v) for v in offset), 125.76, places=2)
        self.assertAlmostEqual(sum(v * v for v in offset) ** 0.5, 155.22, places=2)
        self.assertEqual(summary["bounds_transform_mismatches"], 1)
        self.assertIn("disagree by 125.76m on the largest axis (155.22m apart)", err)


# --------------------------------------------------------------------------
# Spatial audio: Sound scene from the plan role, --map
# --------------------------------------------------------------------------

class SpatialAudioMapTests(unittest.TestCase):
    def test_legacy_maps_keep_their_table(self):
        self.assertEqual(S.LEGACY_SOUND_SCENES, {"arena_equator": "level56", "arena_bay5": "level188"})
        self.assertEqual(S.scene_for_map("arena_equator"), "level56")
        self.assertEqual(S.scene_for_map("arena_bay5"), "level188")
        self.assertEqual(S.DEFAULT_MAPS, ("arena_equator", "arena_bay5"))

    def test_airpit_sound_scene_is_the_plan_role(self):
        plan = load_plan(PLAN_ROOT / "airpit.json")
        record = scene_for_role(plan, "sound")
        self.assertEqual((record["index"], record["name"]), (39, "Arena_Airpit_Sound"))
        self.assertEqual(S.scene_for_map("arena_airpit"), "level39")
        self.assertEqual(S.scene_for_map("arena_airpit", plan), "level39")
        self.assertEqual(S.plan_path("arena_airpit"), PLAN_ROOT / "airpit.json")

    def test_a_map_without_a_sound_role_or_plan_is_an_error(self):
        with self.assertRaises(PlanError):
            S.scene_for_map("arena_bowl")          # plan exists, declares no roles
        with self.assertRaises(PlanError):
            S.scene_for_map("arena_does_not_exist")  # no plan file
        with self.assertRaises(PlanError):
            S.scene_for_map("arena_other", load_plan(PLAN_ROOT / "airpit.json"))  # plan describes another map

    def test_invalid_map_keys_are_refused(self):
        for key in ("equator", "Arena_X", "arena_../x", "arena_", "arena_a b"):
            with self.subTest(key=key), self.assertRaises(ValueError):
                S.plan_path(key)

    def test_export_takes_an_optional_scene(self):
        parameters = inspect.signature(S.export).parameters
        self.assertEqual(list(parameters), ["source", "output", "key", "scene"])
        self.assertIsNone(parameters["scene"].default)

    def test_graph_identity_accepts_plan_driven_keys_and_rejects_malformed_ones(self):
        from test_spatial_audio import graph
        for key in ("arena_equator", "arena_bay5", "arena_airpit"):
            with self.subTest(key=key):
                data = graph()
                data["map_key"] = key
                S.validate_graph(data)
        for key in ("airpit", "Arena_Airpit", "arena_", None, 7):
            with self.subTest(key=key):
                data = graph()
                data["map_key"] = key
                with self.assertRaisesRegex(ValueError, "acoustic identity"):
                    S.validate_graph(data)

    def test_main_failures_exit_2_before_touching_the_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            for key in ("arena_bowl", "arena_does_not_exist"):
                with self.subTest(key=key):
                    code, _out, err = quiet(S.main, ["--map", key, "--source", tmp, "--output", tmp])
                    self.assertEqual(code, 2)
                    self.assertIn("[extract_spatial_audio] FAILED", err)

    def test_main_airpit_verifies_the_plan_mapping_and_exports_the_sound_scene(self):
        result = {"bake": {"bytes": 1, "room_pairs_count": 2, "routes_count": 3, "sha256": "x"},
                  "source_scene": "level39", "rooms": [1], "portals": [1, 2], "areas": [], "clips": [1],
                  "mixer_effects": {"a": 1}}
        with tempfile.TemporaryDirectory() as tmp:
            with patch.object(S, "read_build_scenes", return_value=["x"]) as read, \
                 patch.object(S, "verify_scene_mapping") as verify, \
                 patch.object(S, "export", return_value=result) as export:
                code, out, _err = quiet(S.main, ["--map", "arena_airpit", "--source", tmp, "--output", tmp])
        self.assertEqual(code, 0)
        read.assert_called_once()
        verify.assert_called_once()
        self.assertEqual(export.call_args.args[2:], ("arena_airpit", "level39"))
        self.assertEqual(Path(export.call_args.args[1]).name, "arena_airpit")
        self.assertIn('"rooms": 1', out)

    def test_main_default_runs_the_two_legacy_maps_without_a_plan_check(self):
        result = {"bake": {"bytes": 1, "room_pairs_count": 2, "routes_count": 3, "sha256": "x"},
                  "source_scene": "s", "rooms": [], "portals": [], "areas": [], "clips": [], "mixer_effects": {}}
        with tempfile.TemporaryDirectory() as tmp:
            with patch.object(S, "verify_scene_mapping") as verify, patch.object(S, "export", return_value=result) as export:
                code, _out, _err = quiet(S.main, ["--source", tmp, "--output", tmp])
        self.assertEqual(code, 0)
        verify.assert_not_called()
        self.assertEqual([call.args[2:] for call in export.call_args_list],
                         [("arena_equator", "level56"), ("arena_bay5", "level188")])

    @unittest.skipUnless(Path(ARENA_SOURCE).is_dir(), "Arena source not reachable from this machine")
    def test_airpit_against_the_live_install(self):
        with tempfile.TemporaryDirectory() as tmp:
            data, _out, _err = quiet(S.export, ARENA_SOURCE, tmp, "arena_airpit")
            files = sorted(p.name for p in Path(tmp).iterdir())
            S.validate_graph(json.loads((Path(tmp) / "spatial-audio.json").read_text(encoding="utf-8")))
        self.assertEqual(data["source_scene"], "level39")
        self.assertEqual((len(data["rooms"]), len(data["portals"]), len(data["areas"])), (24, 94, 78))
        self.assertEqual((data["bake"]["bytes"], data["bake"]["room_pairs_count"], data["bake"]["routes_count"]),
                         (481738, 276, 16304))
        self.assertEqual(len(data["clips"]), 1)
        self.assertTrue(data["mixer_effects"])
        self.assertIn("source.audiobakedata", files)
        self.assertIn("spatial-audio.json", files)


# --------------------------------------------------------------------------
# Decals refresh CLI: generic packages
# --------------------------------------------------------------------------

GENERIC_SCRIPT = "tools/extract/extract_map.py"


class DecalsGenericPackageTests(unittest.TestCase):
    def test_package_kind(self):
        self.assertEqual(D.package_kind({"source": {"extractor_script": GENERIC_SCRIPT, "map_key": "arena_airpit"}}), "generic")
        self.assertEqual(D.package_kind({"source": {"extractor_script": "tools/extract/extract_bay5.py"}}), "legacy")
        self.assertEqual(D.package_kind({"source": {"extractor_script": "tools/extract/extract_equator.py",
                                                    "map_key": "arena_equator"}}), "legacy")
        for source in ({}, {"extractor_script": "tools/extract/extract_other.py"},
                       {"extractor_script": "tools/extract/extract_equator.py", "map_key": "arena_bowl"}):
            with self.subTest(source=source), self.assertRaisesRegex(HardExtractError, "unsupported map"):
                D.package_kind({"source": source})

    def test_a_generic_package_must_still_match_its_plan(self):
        manifest = {"scenes": [], "source": {"extractor_script": GENERIC_SCRIPT, "arena_data_root": "Z:/none",
                                            "source_plan": "tools/extract/plans/airpit.json",
                                            "source_plan_sha256": "0" * 64, "map_key": "arena_airpit",
                                            "scene_registry": []}}
        with self.assertRaisesRegex(HardExtractError, "does not match its source plan"):
            D.open_source(manifest)

    def test_dump_formats_follow_the_extractor_that_wrote_the_package(self):
        value = {"b": 1, "a": "caf\u00e9"}
        self.assertEqual(D.dump_json(value, True), '{\n  "a": "caf\u00e9",\n  "b": 1\n}')
        self.assertEqual(D.dump_json(value, False), '{\n  "a": "caf\\u00e9",\n  "b": 1\n}')
        with self.assertRaises(ValueError):
            D.dump_json({"x": float("nan")}, True)

    def make_package(self, root, generic=True, with_features=True):
        package = Path(root) / "pkg"
        (package / "scenes").mkdir(parents=True)
        manifest = {"scenes": ["S1", "S2"], "materials": {"old": {}}, "textures": {"old": {}},
                    "source": {"extractor_script": GENERIC_SCRIPT if generic else "tools/extract/extract_bay5.py"}}
        if with_features:
            manifest["native_features"] = {"schema_version": 1, "required": ["ballistics"]}
        (package / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        for name in ("S1", "S2"):
            (package / "scenes" / f"{name}.json").write_text(
                json.dumps({"scene_name": name, "objects": [{"id": "1", "name": "caf\u00e9", "components": []}]}),
                encoding="utf-8")
        return package

    def refresh(self, package, output, generic, per_scene=2):
        source = D.PackageSource("arena_x", object(), {1: "S1", 2: "S2"}, {1: object(), 2: object()}, generic)
        entered = []

        @contextlib.contextmanager
        def decoder():
            entered.append(True)
            yield

        with patch.object(D, "open_source", return_value=source), \
             patch.object(D, "augment_scene", return_value=per_scene), \
             patch("extract_equator.write_materials", return_value={"m": {"file": "m.json"}}), \
             patch("extract_equator.write_textures", return_value={"t": {"file": "t.png"}}), \
             patch("texture_compat.bc6h_rgb_decoder", decoder):
            count, out, _err = quiet(D.refresh, package, output)
        return count, out, entered

    def test_generic_refresh_updates_manifest_and_uses_extract_map_formatting_and_bc6h(self):
        with tempfile.TemporaryDirectory() as tmp:
            package, output = self.make_package(tmp), Path(tmp) / "out"
            count, out, entered = self.refresh(package, output, generic=True)
            manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
            scene_text = (output / "scenes" / "S1.json").read_text(encoding="utf-8")
            original = (package / "manifest.json").read_text(encoding="utf-8")
        self.assertEqual(count, 4)
        self.assertIn("DECALS VERIFIED: arena_x: 4 instances", out)
        self.assertEqual(manifest["native_features"]["static_decals"], {"schema_version": 1, "count": 4})
        self.assertEqual(sorted(manifest["materials"]), ["m", "old"])
        self.assertEqual(sorted(manifest["textures"]), ["old", "t"])
        self.assertIn("caf\u00e9", scene_text)       # ensure_ascii=False, as extract_map.write_json
        self.assertEqual(entered, [True])           # textures written under the BC6H decoder
        self.assertNotIn("static_decals", original)  # the input package is not modified

    def test_legacy_refresh_keeps_the_original_formatting_and_no_decoder(self):
        with tempfile.TemporaryDirectory() as tmp:
            package, output = self.make_package(tmp, generic=False), Path(tmp) / "out"
            count, _out, entered = self.refresh(package, output, generic=False, per_scene=1)
            scene_text = (output / "scenes" / "S2.json").read_text(encoding="utf-8")
        self.assertEqual(count, 2)
        self.assertIn("caf\\u00e9", scene_text)
        self.assertEqual(entered, [])

    def test_refresh_refuses_an_existing_output_a_package_without_features_and_an_empty_census(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = self.make_package(tmp)
            existing = Path(tmp) / "exists"
            existing.mkdir()
            with self.assertRaisesRegex(HardExtractError, "new package directory"):
                D.refresh(package, existing)
            plain = self.make_package(Path(tmp) / "plain", with_features=False)
            with self.assertRaisesRegex(HardExtractError, "native_features"):
                D.refresh(plain, Path(tmp) / "never")
            self.assertFalse((Path(tmp) / "never").exists())
            with self.assertRaisesRegex(HardExtractError, "empty source census"):
                self.refresh(package, Path(tmp) / "empty", generic=True, per_scene=0)
            self.assertFalse((Path(tmp) / "empty").exists())

    def test_partial_scenes_are_scanned_through_a_scope_of_their_own_objects(self):
        scoped = []

        class FakeScoped:
            def __init__(self, unity_file, owners):
                self.unity_file, self.owners = unity_file, sorted(owners)
                scoped.append(self)
        seen = {}

        def augment(scene, unity_file, _ctx):
            seen[scene["scene_name"]] = unity_file
            return 1
        files = {1: object(), 2: object()}
        with tempfile.TemporaryDirectory() as tmp:
            package, output = self.make_package(tmp), Path(tmp) / "out"
            manifest = json.loads((package / "manifest.json").read_text(encoding="utf-8"))
            manifest["partial_scenes"] = {"S2": {"index": 2}}
            (package / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            scene = json.loads((package / "scenes" / "S2.json").read_text(encoding="utf-8"))
            scene["objects"].append({"id": "42", "name": "other", "components": []})
            (package / "scenes" / "S2.json").write_text(json.dumps(scene), encoding="utf-8")
            source = D.PackageSource("arena_x", object(), {1: "S1", 2: "S2"}, files, True)
            with patch.object(D, "open_source", return_value=source), patch.object(D, "augment_scene", augment), \
                 patch("scene_select.ScopedFile", FakeScoped), \
                 patch("extract_equator.write_materials", return_value={}), \
                 patch("extract_equator.write_textures", return_value={}), \
                 patch("texture_compat.bc6h_rgb_decoder", contextlib.nullcontext):
                count, _out, _err = quiet(D.refresh, package, output)
        self.assertEqual(count, 2)
        self.assertIs(seen["S1"], files[1], "a full scene is scanned on the plain level file")
        self.assertIs(seen["S2"], scoped[0], "a partial scene only through the scoped view")
        self.assertIs(scoped[0].unity_file, files[2])
        self.assertEqual(scoped[0].owners, [1, 42], "the scope is the object ids of the package's own scene JSON")
        self.assertEqual(len(scoped), 1)

    def test_incomplete_scene_set_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = self.make_package(tmp)
            source = D.PackageSource("arena_x", object(), {1: "S1"}, {1: object()}, True)
            with patch.object(D, "open_source", return_value=source), self.assertRaisesRegex(HardExtractError, "incomplete scene set"):
                D.refresh(package, Path(tmp) / "out")


if __name__ == "__main__":
    unittest.main()
