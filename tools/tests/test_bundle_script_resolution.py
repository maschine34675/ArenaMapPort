"""validate_bundle_roundtrip.py resolves an m_Script PPtr through its file id (2026-11-21).

A path id is unique only inside one SerializedFile. Yard's scenes bundle was the first whose shared-asset files
repeat MonoScript path ids (the Scripts scene's BotZone and the Light scene's VolumetricLight both sit at 156),
and the old lookup by path id alone read the BotZone as a VolumetricLight: "at least one BotZone MonoBehaviour
found in the bundle" failed on a bundle that has one, and which script won depended on the order of the files.
Synthetic objects only; no game data."""
from __future__ import annotations

from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path
import sys
from types import SimpleNamespace as NS
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
import validate_bundle_roundtrip as VB  # noqa: E402


def script(path_id, class_name, namespace=""):
    tree = {"m_ClassName": class_name, "m_Namespace": namespace, "m_AssemblyName": "Assembly-CSharp"}
    return NS(type=NS(name="MonoScript"), path_id=path_id, read_typetree=lambda: tree)


def behaviour(path_id, file_id, script_path_id, **fields):
    tree = {"m_Script": {"m_FileID": file_id, "m_PathID": script_path_id}, **fields}
    return NS(type=NS(name="MonoBehaviour"), path_id=path_id, byte_size=200, read_typetree=lambda: tree)


def serialized(name, objects, externals=()):
    return NS(name=name, objects={o.path_id: o for o in objects},
              externals=[NS(path="archive:/CAB-0123456789abcdef/" + e) for e in externals])


def yard_like(order):
    """Two scenes whose shared-asset files both carry a MonoScript at path id 156, as Yard's do."""
    scripts_shared = serialized("BuildPlayer-Map_Scripts.sharedAssets", [
        script(150, "LocationScene"), script(155, "SeasonAmbientSoundPlayer", "Audio.AmbientSubsystem"),
        script(156, "BotZone"), script(158, "SpawnPointMarker", "EFT.Game.Spawning"),
        script(160, "LocationExportInfo", "EFT"), script(162, "LevelSettings")])
    light_shared = serialized("BuildPlayer-Map_Light.sharedAssets", [
        script(155, "CullingLightObject"), script(156, "VolumetricLight"), script(157, "LightFlicker", "EFT.Visual")])
    scripts_scene = serialized("BuildPlayer-Map_Scripts", [
        behaviour(208, 1, 150), behaviour(211, 1, 158), behaviour(220, 1, 162), behaviour(257, 1, 156),
        behaviour(274, 1, 160), behaviour(300, 1, 155), behaviour(301, 1, 155)],
        externals=["BuildPlayer-Map_Scripts.sharedAssets"])
    light_scene = serialized("BuildPlayer-Map_Light", [
        behaviour(10, 2, 155, _radius=1.0, _shift={"x": 0.0, "y": 0.0, "z": 0.0}),
        behaviour(11, 2, 156), behaviour(12, 2, 156),
        behaviour(13, 2, 157, Curve={"m_Curve": [1, 2], "m_PreInfinity": 2, "m_PostInfinity": 2})],
        # file id 1 is a built-in the bundle does not contain, file id 2 the scene's own shared assets
        externals=["unity default resources", "BuildPlayer-Map_Light.sharedAssets"])
    files = [scripts_shared, light_shared, scripts_scene, light_scene]
    return [files[i] for i in order], scripts_scene, light_scene


ORDERS = ([0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 0, 3, 1])


class ResolverTests(unittest.TestCase):
    def test_a_script_is_found_through_the_owning_files_externals_in_every_file_order(self):
        for order in ORDERS:
            with self.subTest(order=order):
                files, scripts_scene, light_scene = yard_like(order)
                with patch.object(VB, "iter_serialized_files", return_value=files):
                    resolve = VB.monoscript_resolver(object())
                name = lambda owner, obj: resolve(owner, obj.read_typetree()["m_Script"]).read_typetree()["m_ClassName"]
                self.assertEqual(name(scripts_scene, scripts_scene.objects[257]), "BotZone")
                self.assertEqual(name(light_scene, light_scene.objects[11]), "VolumetricLight")
                self.assertEqual(name(scripts_scene, scripts_scene.objects[300]), "SeasonAmbientSoundPlayer")
                self.assertEqual(name(light_scene, light_scene.objects[10]), "CullingLightObject")

    def test_what_does_not_resolve_is_none_and_never_another_files_script(self):
        files, scripts_scene, light_scene = yard_like(ORDERS[0])
        with patch.object(VB, "iter_serialized_files", return_value=files):
            resolve = VB.monoscript_resolver(object())
        self.assertIsNone(resolve(light_scene, {"m_FileID": 1, "m_PathID": 156}))     # an external outside the bundle
        self.assertIsNone(resolve(light_scene, {"m_FileID": 3, "m_PathID": 156}))     # no such external
        self.assertIsNone(resolve(light_scene, {"m_FileID": -1, "m_PathID": 156}))
        self.assertIsNone(resolve(light_scene, {"m_FileID": 0, "m_PathID": 156}))     # file 0 is the scene file itself
        self.assertIsNone(resolve(scripts_scene, {"m_FileID": 1, "m_PathID": 157}))   # 157 exists in the other file only
        self.assertIsNone(resolve(scripts_scene, None))
        self.assertIsNone(resolve(scripts_scene, {}))
        own = serialized("BuildPlayer-Own", [script(7, "LevelSettings"), behaviour(8, 0, 7)])
        with patch.object(VB, "iter_serialized_files", return_value=[own]):
            resolve = VB.monoscript_resolver(object())
        self.assertEqual(resolve(own, {"m_FileID": 0, "m_PathID": 7}).read_typetree()["m_ClassName"], "LevelSettings")
        self.assertEqual(resolve(own, {"m_PathID": 7}).read_typetree()["m_ClassName"], "LevelSettings")

    def test_an_external_path_with_backslashes_or_another_case_still_matches(self):
        shared = serialized("BuildPlayer-Map_Scripts.sharedAssets", [script(156, "BotZone")])
        scene = NS(name="BuildPlayer-Map_Scripts", objects={},
                   externals=[NS(path="archive:\\CAB-0\\buildplayer-map_scripts.sharedassets")])
        with patch.object(VB, "iter_serialized_files", return_value=[shared, scene]):
            resolve = VB.monoscript_resolver(object())
        self.assertEqual(resolve(scene, {"m_FileID": 1, "m_PathID": 156}).read_typetree()["m_ClassName"], "BotZone")


class CallerTests(unittest.TestCase):
    def test_the_scripts_scene_components_are_all_found_in_every_file_order(self):
        for order in ORDERS:
            with self.subTest(order=order):
                files, _, _ = yard_like(order)
                outcomes = []
                with patch.object(VB, "iter_serialized_files", return_value=files), \
                     patch.object(VB, "check", side_effect=lambda ok, message: outcomes.append((ok, message)) or ok), \
                     patch.object(VB, "warn", side_effect=lambda message: None), \
                     redirect_stdout(StringIO()), redirect_stderr(StringIO()):
                    VB.validate_eft_scripts(object(), True)
                # (the synthetic BotZone carries no PatrolWays field, which the validator only warns about)
                failed = [message for ok, message in outcomes if not ok]
                self.assertEqual(failed, [])
                for class_name in VB.EXPECTED_EFT_COMPONENTS:
                    self.assertIn((True, f"at least one {class_name} MonoBehaviour found in the bundle"), outcomes)

    def test_the_light_counts_are_the_light_scenes_own_in_every_file_order(self):
        for order in ORDERS:
            with self.subTest(order=order):
                files, _, _ = yard_like(order)
                with patch.object(VB, "iter_serialized_files", return_value=files), \
                     redirect_stdout(StringIO()), redirect_stderr(StringIO()):
                    counts, flicker, spheres = VB.count_light_culling_in_bundle_scenes(object(), ["Map_Light", "Map_Scripts"])
                self.assertEqual(counts["Map_Light"], {"CullingLightObject": 1, "VolumetricLight": 2, "LightFlicker": 1})
                # the Scripts scene's two season players and its BotZone share path ids with light scripts and are not lights
                self.assertEqual(counts["Map_Scripts"], {"CullingLightObject": 0, "VolumetricLight": 0, "LightFlicker": 0})
                self.assertEqual(sum(flicker.values()), 1)
                self.assertEqual(sum(spheres.values()), 1)


if __name__ == "__main__":
    unittest.main()
