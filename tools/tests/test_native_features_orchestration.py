"""PLAN-AIRPIT item 2: native-feature orchestration for plan-driven maps.

Covers tools/extract/native_features.py, the --native-features/--target CLI of extract_map.py and
the reproducibility command validate_extract.py builds for generic native packages. The augmenters
and exporters have their own suites; here they are replaced by recorders, so everything runs without
an Arena or SPT install. The legacy-shape tests compare the orchestration's manifest blocks with the
accepted Equator/Bay5 packages (skipped when those ignored work/ packages are absent).
"""
from __future__ import annotations

from contextlib import ExitStack
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import extract_bay5_audio
import extract_map as EX
import native_contract_loader
import native_features as NF
import source_plan as SP
import validate_extract as VE
from common_ids import HardExtractError

CONTRACT = {"schema_version": 1, "source_presets": {"presets": []}, "target_presets": {"presets": []}}
LIGHT = {"culling_light_object_count": 0, "volumetric_light_count": 0, "light_flicker_count": 0}


def plan_with_scripts_role(index: int = 7, sound_index: int | None = None) -> dict:
    def scene(name, idx):
        return {"name": name, "asset_path": f"Assets/Content/{name}.unity", "index": idx}
    excluded = [dict(scene("Arena_Test_Scripts", index), role="scripts", reason="authored scripts")]
    if sound_index is not None:
        excluded.append(dict(scene("Arena_Test_Sound", sound_index), role="sound", reason="native audio"))
    return {"schema_version": 1, "key": "arena_test", "source_preset": "arena_test_preset.bundle",
            "source_mode": "TeamFight", "scenes": [scene("Arena_Test_Main", 1)],
            "anchor_scenes": [scene("Arena_Test_Main", 1)], "navmesh_scenes": [], "fog_color": [0.1, 0.2, 0.3],
            "excluded_scenes": excluded}


def fake_contracts(feature="ambient_audio", scene="level39", audio=True) -> dict:
    """Minimal stand-in for native_contract_loader.load_contracts(): only what the registration check reads."""
    return {"arena_test": {"audio": {"feature": feature, "scene": scene} if audio else None}}


class NativeFeatureTestCase(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.target = self.root / "EscapeFromTarkov_Data"
        self.target.mkdir()
        for name in ("resources.assets", "globalgamemanagers.assets"):
            (self.target / name).write_bytes(b"")
        loader = patch.object(NF.extract_ballistics, "load_contract", return_value=CONTRACT)
        self.load_contract = loader.start()
        self.addCleanup(loader.stop)
        quiet = patch.object(NF, "_log")  # keep the unittest output free of progress lines
        quiet.start()
        self.addCleanup(quiet.stop)

    def run_for(self, audio=None, scripts_index=7) -> NF.NativeFeatureExtraction:
        return NF.NativeFeatureExtraction(source=self.root / "arena", target=self.target, map_key="arena_test",
                                          scripts_scene_index=scripts_index, audio=audio)


class SceneAugmentationTests(NativeFeatureTestCase):
    def install_augmenters(self, calls: list) -> None:
        """Replace the five per-scene augmenters with recorders returning fixed counts."""
        def record(name, result):
            def augment(*args):
                calls.append((name, args))
                return result
            return augment
        stack = ExitStack()
        self.addCleanup(stack.close)
        for module, name, result in (
                (NF.extract_ballistics, "ballistics", 4), (NF.extract_navmesh_modifiers, "navmesh", (2, 1)),
                (NF.extract_decals, "decals", 6), (NF.extract_physics_props, "physics", 3),
                (NF.extract_light_culling, "light", {"culling_light_object_count": 5,
                                                     "volumetric_light_count": 2, "light_flicker_count": 1})):
            stack.enter_context(patch.object(module, "augment_scene", side_effect=record(name, result)))

    def test_augmenters_run_in_the_equator_bay5_order_and_totals_accumulate(self):
        calls = []
        self.install_augmenters(calls)
        run, scene, unity_file, ctx = self.run_for(), {"objects": []}, object(), object()
        run.augment_scene(scene, unity_file, ctx, "S1")
        run.augment_scene(scene, unity_file, ctx, "S2")
        self.assertEqual([name for name, _ in calls], ["ballistics", "navmesh", "decals", "physics", "light"] * 2)
        by_name = dict(calls)
        self.assertEqual(by_name["ballistics"], (scene, unity_file, CONTRACT))
        self.assertEqual(by_name["decals"], (scene, unity_file, ctx), "only decals need the extraction context")
        self.assertEqual(by_name["navmesh"], (scene, unity_file))
        self.assertEqual((run.ballistics, run.navmesh_modifiers, run.navmesh_volumes, run.decals, run.physics_objects),
                         (8, 4, 2, 12, 6))
        self.assertEqual(run.light_culling, {"culling_light_object_count": 10, "volumetric_light_count": 4,
                                             "light_flicker_count": 2})

    def test_contract_is_read_once_for_the_whole_map(self):
        self.install_augmenters([])
        run = self.run_for()
        for index in range(5):
            run.augment_scene({}, object(), object(), f"S{index}")
        self.load_contract.assert_called_once()
        self.assertEqual(self.load_contract.call_args.args[1], self.target.as_posix())
        self.assertIs(run.contract, CONTRACT)

    def test_target_that_is_not_an_spt_data_directory_fails_before_the_contract_is_read(self):
        (self.target / "resources.assets").unlink()
        with self.assertRaisesRegex(HardExtractError, "resources.assets missing"):
            self.run_for()
        self.load_contract.assert_not_called()


class ManifestBlockTests(NativeFeatureTestCase):
    def blocks(self, run, raincover=None, areas=None):
        raincover = raincover or {"schema_version": 1, "indoor_trigger_count": 2}
        with patch.object(NF.extract_navmesh_modifiers, "read_navmesh_areas",
                          return_value=areas or {"0": {"name": "Walkable", "cost": 1}}) as read_areas, \
             patch.object(NF.extract_raincover, "export", return_value=raincover) as export:
            result = run.manifest_blocks("ENV", self.root / "out")
        self.read_areas, self.export = read_areas, export
        return result

    def counted_run(self, **kwargs) -> NF.NativeFeatureExtraction:
        run = self.run_for(**kwargs)
        run.ballistics, run.decals = 10, 4
        run.light_culling = {"culling_light_object_count": 5, "volumetric_light_count": 2, "light_flicker_count": 0}
        return run

    def test_map_without_audio_exporter_requires_only_ballistics_and_writes_no_native_audio(self):
        blocks = self.blocks(self.counted_run())
        self.assertEqual(sorted(blocks), ["native_ballistics", "native_features"])
        self.assertIs(blocks["native_ballistics"], CONTRACT)
        features = blocks["native_features"]
        self.assertEqual(features["schema_version"], 1)
        self.assertEqual(features["required"], ["ballistics"])
        self.assertEqual(sorted(features), ["light_culling", "navmesh_areas", "raincover", "required",
                                            "schema_version", "static_decals"])
        self.assertEqual(features["light_culling"], {"schema_version": 1, "culling_light_object_count": 5,
                                                     "volumetric_light_count": 2, "light_flicker_count": 0})
        self.assertEqual(features["static_decals"], {"schema_version": 1, "count": 4})

    def test_registered_audio_exporter_adds_the_required_feature_and_the_native_audio_block(self):
        seen = []
        def export_audio(source, output, map_key):
            seen.append((source, output, map_key))
            return {"schema_version": 2, "file": "audio/native-audio.json"}
        blocks = self.blocks(self.counted_run(audio=("ambient_audio", export_audio)))
        self.assertEqual(blocks["native_features"]["required"], ["ballistics", "ambient_audio"])
        self.assertEqual(blocks["native_audio"], {"schema_version": 2, "file": "audio/native-audio.json"})
        self.assertEqual(seen, [((self.root / "arena").as_posix(), (self.root / "out").as_posix(), "arena_test")])

    def test_rain_cover_uses_the_plan_scripts_scene_not_a_per_map_table(self):
        self.blocks(self.counted_run(scripts_index=36))
        self.assertEqual(self.export.call_args.args, ((self.root / "arena").as_posix(),
                                                      (self.root / "out").as_posix(), "arena_test"))
        self.assertEqual(self.export.call_args.kwargs, {"scripts_scene_index": 36})
        self.assertEqual(self.read_areas.call_args.args, ("ENV", (self.root / "arena").as_posix()))

    def test_static_decals_block_is_written_only_when_the_map_has_decals(self):
        run = self.counted_run()
        run.decals = 0
        self.assertNotIn("static_decals", self.blocks(run)["native_features"])

    def test_a_native_package_without_any_ballistic_component_is_a_hard_failure(self):
        run = self.counted_run()
        run.ballistics = 0
        with self.assertRaisesRegex(HardExtractError, "no BallisticCollider"):
            self.blocks(run)

    def test_rain_cover_value_error_becomes_a_hard_extract_error_with_the_cause_chained(self):
        run = self.counted_run()
        with patch.object(NF.extract_navmesh_modifiers, "read_navmesh_areas", return_value={}), \
             patch.object(NF.extract_raincover, "export",
                          side_effect=ValueError("raincover: arena_test has zero IndoorTrigger instances")):
            with self.assertRaises(HardExtractError) as raised:
                run.manifest_blocks("ENV", self.root / "out")
        self.assertIn("raincover export failed: raincover: arena_test has zero IndoorTrigger instances",
                      str(raised.exception))
        self.assertIsInstance(raised.exception.__cause__, ValueError)

    def test_audio_exporter_value_error_becomes_a_hard_extract_error_naming_the_feature(self):
        def reject(*_):
            raise ValueError("unsupported native audio map")
        run = self.counted_run(audio=("ambient_audio", reject))
        with self.assertRaisesRegex(HardExtractError, "ambient_audio export failed: unsupported native audio map"):
            self.blocks(run)

    def test_exporter_hard_extract_error_passes_through_unwrapped(self):
        def reject(*_):
            raise HardExtractError("already hard")
        run = self.counted_run(audio=("ambient_audio", reject))
        with self.assertRaises(HardExtractError) as raised:
            self.blocks(run)
        self.assertEqual(str(raised.exception), "already hard")

    def test_source_fields_record_the_target_for_the_reproducibility_rerun(self):
        self.assertEqual(self.run_for().source_fields(), {"target_data_root": self.target.as_posix()})

    def test_for_plan_resolves_the_scripts_role_and_fails_without_one(self):
        run = NF.NativeFeatureExtraction.for_plan(plan_with_scripts_role(36), self.root / "arena", self.target)
        self.assertEqual((run.map_key, run.scripts_scene_index, run.audio), ("arena_test", 36, None))
        plan = plan_with_scripts_role()
        del plan["excluded_scenes"][0]["role"]
        with self.assertRaisesRegex(SP.PlanError, "declares no scene with role 'scripts'"):
            NF.NativeFeatureExtraction.for_plan(plan, self.root / "arena", self.target)

    def test_for_plan_picks_up_a_registered_audio_exporter(self):
        exporter = ("ambient_audio", lambda *_: {})
        with patch.dict(NF.AUDIO_EXPORTERS, {"arena_test": exporter}), \
             patch.object(NF, "load_contracts", return_value=fake_contracts()):
            run = NF.NativeFeatureExtraction.for_plan(plan_with_scripts_role(sound_index=39), self.root / "arena",
                                                      self.target)
        self.assertIs(run.audio, exporter)

    def for_registered_plan(self, plan, contracts, feature="ambient_audio"):
        with patch.dict(NF.AUDIO_EXPORTERS, {"arena_test": (feature, lambda *_: {})}), \
             patch.object(NF, "load_contracts", return_value=contracts):
            return NF.NativeFeatureExtraction.for_plan(plan, self.root / "arena", self.target)

    def test_registered_exporter_needs_the_plans_sound_role(self):
        with self.assertRaisesRegex(SP.PlanError, "declares no scene with role 'sound'"):
            self.for_registered_plan(plan_with_scripts_role(), fake_contracts())

    def test_registered_exporter_must_match_the_contract_before_any_extraction(self):
        plan = plan_with_scripts_role(sound_index=39)
        cases = (("sound scene drift", plan_with_scripts_role(sound_index=40), fake_contracts(), "ambient_audio",
                  "contract audio scene 'level39' is not the plan's sound scene"),
                 ("contract scene drift", plan, fake_contracts(scene="level38"), "ambient_audio",
                  "contract audio scene 'level38' is not the plan's sound scene"),
                 ("feature name drift", plan, fake_contracts(feature="harbor_audio"), "ambient_audio",
                  "registers audio feature 'ambient_audio' but its native contract says 'harbor_audio'"),
                 ("contract without audio", plan, fake_contracts(audio=False), "ambient_audio",
                  "contract declares no audio"),
                 ("no contract at all", plan, {}, "ambient_audio", "contract declares no audio"))
        for name, case_plan, contracts, feature, pattern in cases:
            with self.subTest(name), self.assertRaisesRegex(HardExtractError, pattern):
                self.for_registered_plan(case_plan, contracts, feature)

    def test_airpit_registers_the_shared_ambient_audio_exporter(self):
        self.assertEqual(NF.AUDIO_EXPORTERS["arena_airpit"], ("ambient_audio", extract_bay5_audio.export))
        contracts = native_contract_loader.load_contracts()
        for key, (feature, _exporter) in NF.AUDIO_EXPORTERS.items():
            with self.subTest(key=key):
                self.assertEqual(contracts[key]["audio"]["feature"], feature)

    def test_airpit_plan_sound_scene_is_the_contract_scene_level39(self):
        plan = SP.load_plan(REPO / "tools/extract/plans/airpit.json")
        self.assertEqual(SP.scene_for_role(plan, "sound")["index"], 39)
        run = NF.NativeFeatureExtraction.for_plan(plan, self.root / "arena", self.target)
        self.assertEqual(run.audio, ("ambient_audio", extract_bay5_audio.export))
        self.assertEqual(native_contract_loader.load_contract("arena_airpit")["audio"]["scene"], "level39")

    def test_airpit_plan_scripts_scene_is_index_36(self):
        plan = SP.load_plan(REPO / "tools/extract/plans/airpit.json")
        run = NF.NativeFeatureExtraction.for_plan(plan, self.root / "arena", self.target)
        self.assertEqual((run.map_key, run.scripts_scene_index), ("arena_airpit", 36))


class AcceptedPackageShapeTests(NativeFeatureTestCase):
    """Golden shape: feeding an accepted package's own values through the orchestration reproduces its
    native blocks exactly, so a generic package has the same manifest contract as Equator and Bay5."""

    def check_package(self, name):
        path = REPO / "work" / "extract" / name / "manifest.json"
        if not path.is_file():
            self.skipTest(f"{path} not present (ignored work/ package)")
        manifest = json.loads(path.read_text(encoding="utf-8"))
        features = manifest["native_features"]
        with patch.object(NF.extract_ballistics, "load_contract", return_value=manifest["native_ballistics"]):
            run = self.run_for(audio=(features["required"][1], lambda *_: manifest["native_audio"]))
        run.ballistics, run.decals = 1, features["static_decals"]["count"]
        run.light_culling = {key: features["light_culling"][key] for key in NF._LIGHT_CULLING_KEYS}
        with patch.object(NF.extract_navmesh_modifiers, "read_navmesh_areas", return_value=features["navmesh_areas"]), \
             patch.object(NF.extract_raincover, "export", return_value=features["raincover"]):
            blocks = run.manifest_blocks("ENV", self.root / "out")
        self.assertEqual(blocks["native_features"], features)
        self.assertEqual(blocks["native_audio"], manifest["native_audio"])
        self.assertEqual(blocks["native_ballistics"], manifest["native_ballistics"])
        self.assertEqual(sorted(blocks), ["native_audio", "native_ballistics", "native_features"])

    def test_equator_native_v5_blocks_are_reproduced(self):
        self.check_package("equator-native-v5")

    def test_bay5_native_v5_blocks_are_reproduced(self):
        self.check_package("bay5-native-v5")

    def test_airpit_native_v6_blocks_are_reproduced(self):
        self.check_package("airpit-native-v6")


class ExtractMapCommandLineTests(unittest.TestCase):
    PLAN = ["--plan", "tools/extract/plans/airpit.json"]

    def rejected(self, *flags):
        with patch.object(sys, "stderr"), self.assertRaises(SystemExit) as stopped:
            EX.parse_args([*self.PLAN, *flags])
        self.assertEqual(stopped.exception.code, 2)

    def test_native_features_need_a_target(self):
        self.rejected("--native-features")

    def test_target_is_rejected_without_native_features(self):
        self.rejected("--target", "D:/SPT41/EscapeFromTarkov_Data")

    def test_native_features_cannot_be_census_only(self):
        self.rejected("--native-features", "--target", "D:/SPT41/EscapeFromTarkov_Data", "--census-only")

    def test_plain_invocation_has_no_native_options(self):
        args = EX.parse_args([*self.PLAN, "--output", "out"])
        self.assertEqual((args.native_features, args.target), (False, None))

    def test_main_hands_the_target_to_extract_only_for_native_runs(self):
        with patch.object(EX, "extract") as extract:
            argv = ["extract_map.py", *self.PLAN, "--output", "out", "--fixed-timestamp", "T"]
            with patch.object(sys, "argv", argv):
                self.assertEqual(EX.main(), 0)
            with patch.object(sys, "argv", [*argv, "--native-features", "--target", "spt-data"]):
                self.assertEqual(EX.main(), 0)
        plain, native = extract.call_args_list
        self.assertIsNone(plain.args[5])
        self.assertEqual(native.args[5], Path("spt-data"))

    def test_native_failure_exits_2_like_any_other_extraction_error(self):
        with patch.object(EX, "extract", side_effect=HardExtractError("boom")), \
             patch.object(sys, "argv", ["extract_map.py", *self.PLAN, "--output", "out",
                                        "--native-features", "--target", "spt-data"]), \
             patch.object(sys, "stderr"):
            self.assertEqual(EX.main(), 2)


class ExtractMapFailureExitTests(NativeFeatureTestCase):
    """A rejected rain-cover scene (a bare ValueError in the exporter, raised after the long extraction)
    must end like every other native failure: ``[extract_map] FAILED`` on stderr and exit 2, no traceback."""

    def test_rain_cover_value_error_exits_2_through_main(self):
        run = self.run_for()
        run.ballistics = 1

        def extract(*_):
            with patch.object(NF.extract_navmesh_modifiers, "read_navmesh_areas", return_value={}), \
                 patch.object(NF.extract_raincover, "export",
                              side_effect=ValueError("raincover: unsupported map_key 'arena_test'")):
                run.manifest_blocks("ENV", self.root / "out")

        argv = ["extract_map.py", "--plan", "tools/extract/plans/airpit.json", "--output", "out",
                "--native-features", "--target", "spt-data"]
        with patch.object(EX, "extract", side_effect=extract), patch.object(sys, "argv", argv), \
             patch.object(sys, "stderr") as stderr:
            self.assertEqual(EX.main(), 2)
        printed = "".join(call.args[0] for call in stderr.write.call_args_list)
        self.assertIn("[extract_map] FAILED: native_features: raincover export failed", printed)


class ExtractOrderTests(unittest.TestCase):
    """The native run is built before the multi-minute environment load, and only on request."""

    class Reached(Exception):
        pass

    def run_extract(self, native_target, for_plan):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "source").mkdir()
            with patch.object(EX, "load_plan", return_value=plan_with_scripts_role()), \
                 patch.object(EX, "plan_relative_path"), patch.object(EX, "read_build_scenes"), \
                 patch.object(EX, "verify_scene_mapping"), \
                 patch.object(EX, "load_environment", side_effect=self.Reached), \
                 patch.object(EX.NF.NativeFeatureExtraction, "for_plan", for_plan):
                EX.extract(Path("plan.json"), root / "source", root / "out", "T", False, native_target)

    def test_native_run_is_resolved_before_the_environment_is_loaded(self):
        def refuse(*_):
            raise HardExtractError("no scripts role")
        with self.assertRaisesRegex(HardExtractError, "no scripts role"):
            self.run_extract(Path("spt-data"), refuse)

    def test_plain_extraction_never_builds_a_native_run(self):
        def forbidden(*_):
            raise AssertionError("plain extraction must not touch the native-feature path")
        with self.assertRaises(self.Reached):
            self.run_extract(None, forbidden)


class GenericReproducibilityCommandTests(unittest.TestCase):
    SCRIPT = REPO / "tools/extract/extract_map.py"

    def command(self, **kwargs):
        with patch.object(VE.subprocess, "run") as run:
            VE.run_extractor(self.SCRIPT, [1], {}, "source", Path("out"), "fixed", 90, Path("plan.json"), **kwargs)
        return run.call_args.args[0]

    def test_generic_native_package_is_rerun_with_native_features_and_its_recorded_target(self):
        cmd = self.command(target_root="custom SPT/EscapeFromTarkov_Data", native_features=True)
        self.assertIn("--native-features", cmd)
        self.assertEqual(cmd[cmd.index("--target") + 1], "custom SPT/EscapeFromTarkov_Data")
        self.assertEqual(cmd[cmd.index("--plan") + 1], "plan.json")
        self.assertNotIn("--scenes", cmd)

    def test_generic_plain_package_keeps_the_unchanged_command(self):
        for kwargs in ({}, {"target_root": "ignored without native_features"}):
            with self.subTest(kwargs=kwargs):
                cmd = self.command(**kwargs)
                self.assertNotIn("--native-features", cmd)
                self.assertNotIn("--target", cmd)

    def test_generic_native_package_without_a_recorded_target_cannot_be_reproduced(self):
        with patch.object(VE.subprocess, "run") as run, patch.object(sys, "stderr"), self.assertRaises(SystemExit):
            VE.run_extractor(self.SCRIPT, [1], {}, "source", Path("out"), "fixed", 90, Path("plan.json"),
                             native_features=True)
        run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
