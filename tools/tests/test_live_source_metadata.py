"""Source metadata must not depend on unpublished game-manager/scene dumps."""
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_equator as EQ


class LiveSourceMetadataTests(unittest.TestCase):
    def test_tags_use_requested_source_only(self):
        tag = NS(type=NS(name="TagManager"), read_typetree=lambda: {"tags": ["Custom", "Water"]})
        with patch.object(EQ.UnityPy, "load", return_value=NS(objects=[tag])) as load:
            self.assertEqual(EQ.load_tags("chosen-install"), ["Custom", "Water"])
        load.assert_called_once_with("chosen-install/globalgamemanagers")

    def test_missing_or_invalid_tags_fail_without_frozen_fallback(self):
        for tags in (None, "invalid", [123]):
            tag = NS(type=NS(name="TagManager"), read_typetree=lambda: {"tags": tags})
            with patch.object(EQ.UnityPy, "load", return_value=NS(objects=[tag])):
                with self.assertRaises(EQ.HardExtractError):
                    EQ.load_tags("chosen-install")
        with patch.object(EQ.UnityPy, "load", return_value=NS(objects=[])):
            with self.assertRaises(EQ.HardExtractError):
                EQ.load_tags("chosen-install")

    def test_externals_are_live_deduplicated_and_keep_order(self):
        paths = ["b.assets", "unity default resources", "", "a.assets", "b.assets", "unity_builtin_extra"]
        env = NS(files={"scene": NS(externals=[NS(path=p) for p in paths]), "other": NS()})
        with patch.object(EQ.UnityPy, "load", return_value=env) as load:
            self.assertEqual(EQ.scene_external_names("source/level51"), ["b.assets", "a.assets"])
        load.assert_called_once_with("source/level51")

    def test_environment_loads_new_dependency_and_retains_exact_paths(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp).as_posix()
            names = ["globalgamemanagers.assets", "resources.assets", "level51", "sharedassets51.assets", "fresh.assets"]
            for name in names:
                (Path(temp) / name).touch()
            with patch.object(EQ, "scene_external_names", return_value=["fresh.assets", "fresh.assets"]) as ext:
                with patch.object(EQ.UnityPy, "load", return_value="loaded") as load:
                    env, exact = EQ.load_environment(source, [51])
            self.assertEqual(env, "loaded")
            self.assertEqual(exact, {51: {"level": f"{source}/level51", "sharedassets": f"{source}/sharedassets51.assets"}})
            ext.assert_called_once_with(f"{source}/level51")
            self.assertEqual(load.call_args.args, tuple(f"{source}/{name}" for name in names))


if __name__ == "__main__":
    unittest.main()
