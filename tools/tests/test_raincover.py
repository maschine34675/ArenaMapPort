"""PLAN-TWO-MAPS item 6 -- rain-cover extractor (tools/extract/extract_raincover.py).

Fixture-only, like this suite's other extractor tests (test_native_features_validation.py):
exercises the hand-built typetree node shapes and the TRS math against synthetic fixtures, never
the real Arena install (that offline run and its counts -- 669 DryPlane/30 IndoorTrigger/1
WeatherObstacle on Bay5, 0/1/0 on Equator, one baked-vs-live Bounds mismatch on Bay5 -- are
recorded with full evidence in docs/progress/two-maps-item-6-raincover.md instead).
"""
import math
from pathlib import Path
import sys
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
import extract_raincover as R  # noqa: E402


class FakeAssetsFile:
    def __init__(self):
        self.objects = {}


class FakeTransform:
    """Just enough of a UnityPy ObjectReader for _world_matrix_of_transform's own read_typetree()
    + m_Father walk -- no UnityPy dependency needed for this pure-geometry test."""

    def __init__(self, assets_file, path_id, position, rotation, scale, father_path_id=0):
        self.assets_file = assets_file
        self.path_id = path_id
        self._tree = {
            "m_LocalPosition": dict(zip("xyz", position)),
            "m_LocalRotation": dict(zip("xyzw", rotation)),
            "m_LocalScale": dict(zip("xyz", scale)),
            "m_Father": {"m_FileID": 0, "m_PathID": father_path_id},
        }
        assets_file.objects[path_id] = self

    def read_typetree(self):
        return self._tree


def quat_from_axis_angle(axis, degrees):
    axis = np.array(axis, dtype=float)
    axis = axis / np.linalg.norm(axis)
    half = math.radians(degrees) / 2.0
    s = math.sin(half)
    return [axis[0] * s, axis[1] * s, axis[2] * s, math.cos(half)]


class DecomposeTests(unittest.TestCase):
    def test_identity_matrix(self):
        pos, rot, scale = R._decompose(np.eye(4))
        self.assertEqual(pos, [0.0, 0.0, 0.0])
        self.assertAlmostEqual(rot[3], 1.0, places=6)
        self.assertEqual(scale, [1.0, 1.0, 1.0])

    def test_translation_only(self):
        m = np.eye(4)
        m[:3, 3] = [1.0, 2.0, 3.0]
        pos, _rot, scale = R._decompose(m)
        self.assertEqual(pos, [1.0, 2.0, 3.0])
        self.assertEqual(scale, [1.0, 1.0, 1.0])

    def test_local_trs_then_decompose_round_trips(self):
        rot_in = quat_from_axis_angle((0, 1, 0), 90.0)
        t = {
            "m_LocalPosition": {"x": 1.0, "y": 2.0, "z": 3.0},
            "m_LocalRotation": dict(zip("xyzw", rot_in)),
            "m_LocalScale": {"x": 2.0, "y": 3.0, "z": 4.0},
        }
        m = R._local_trs_matrix(t)
        pos, rot, scale = R._decompose(m)
        self.assertAlmostEqual(pos[0], 1.0, places=5)
        self.assertAlmostEqual(pos[1], 2.0, places=5)
        self.assertAlmostEqual(pos[2], 3.0, places=5)
        for expected, got in zip([2.0, 3.0, 4.0], scale):
            self.assertAlmostEqual(expected, got, places=5)
        # quaternion may come back negated (q and -q represent the same rotation) -- compare both.
        same = all(abs(a - b) < 1e-5 for a, b in zip(rot_in, rot))
        negated = all(abs(a + b) < 1e-5 for a, b in zip(rot_in, rot))
        self.assertTrue(same or negated, f"{rot_in} vs {rot}")


class WorldMatrixCompositionTests(unittest.TestCase):
    def test_two_level_parent_chain_composes_position_and_scale(self):
        f = FakeAssetsFile()
        parent = FakeTransform(f, 1, (10.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
        child = FakeTransform(f, 2, (0.0, 5.0, 0.0), (0.0, 0.0, 0.0, 1.0), (2.0, 2.0, 2.0), father_path_id=1)
        pos, _rot, scale = R._decompose(R._world_matrix_of_transform(child))
        self.assertAlmostEqual(pos[0], 10.0, places=5)
        self.assertAlmostEqual(pos[1], 5.0, places=5)
        self.assertAlmostEqual(scale[0], 2.0, places=5)

    def test_rotated_parent_carries_child_offset(self):
        # Parent rotated 90 deg about Y: a child offset (1,0,0) in parent-local space lands at
        # world (0,0,-1) relative to the parent (right-handed convention this module's rotation
        # matrix uses consistently on both the compose and decompose side).
        f = FakeAssetsFile()
        rot = quat_from_axis_angle((0, 1, 0), 90.0)
        parent = FakeTransform(f, 1, (0.0, 0.0, 0.0), tuple(rot), (1.0, 1.0, 1.0))
        child = FakeTransform(f, 2, (1.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0), father_path_id=1)
        pos, _rot, _scale = R._decompose(R._world_matrix_of_transform(child))
        self.assertAlmostEqual(pos[0], 0.0, places=5)
        self.assertAlmostEqual(pos[1], 0.0, places=5)
        self.assertNotAlmostEqual(pos[2], 0.0, places=2)  # rotated off the X axis, not left at (1,0,0)

    def test_single_root_no_father(self):
        f = FakeAssetsFile()
        leaf = FakeTransform(f, 1, (3.0, 4.0, 5.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0), father_path_id=0)
        pos, _rot, _scale = R._decompose(R._world_matrix_of_transform(leaf))
        self.assertEqual([round(v, 5) for v in pos], [3.0, 4.0, 5.0])


class TypetreeNodeShapeTests(unittest.TestCase):
    """These node-builder shapes are copied from the proven rain-cover spike (see module
    docstring) -- lock the field names/order/byte sizes the real TypeTreeNode.from_list() call
    depends on, independent of ever loading a real MonoBehaviour object."""

    def test_bounds_node_is_center_then_extent(self):
        nodes = R._bounds("Bounds")
        names = [n["m_Name"] for n in nodes]
        self.assertEqual(names, ["Bounds", "m_Center", "x", "y", "z", "m_Extent", "x", "y", "z"])

    def test_pptr_node_has_fileid_then_pathid(self):
        nodes = R._pptr("Mesh")
        self.assertEqual([n["m_Name"] for n in nodes], ["Mesh", "m_FileID", "m_PathID"])

    def test_indoor_trigger_field_order_matches_decompile(self):
        # EFT.EnvironmentEffect/IndoorTrigger.cs field declaration order (Unity serializes
        # [SerializeField] members in source order) -- IsBunker, FadeTime, BunkerDepth,
        # BunkerLowPass, ExposureSpeed, ExposureOffset, RainVolume, Bounds, AreaAutoId.
        fields = [R._bool("IsBunker"), R._float("FadeTime"), R._float("BunkerDepth"), R._float("BunkerLowPass"),
                  R._float("ExposureSpeed"), R._float("ExposureOffset"), R._float("RainVolume"),
                  R._bounds("Bounds"), R._int("AreaAutoId")]
        flat = [n["m_Name"] for group in fields for n in group if n["m_Level"] == 1]
        self.assertEqual(flat, ["IsBunker", "FadeTime", "BunkerDepth", "BunkerLowPass", "ExposureSpeed",
                                "ExposureOffset", "RainVolume", "Bounds", "AreaAutoId"])


class ExportGuardTests(unittest.TestCase):
    def test_rejects_unknown_map_key(self):
        with self.assertRaises(ValueError):
            R.export("Z:/does-not-exist", "Z:/does-not-exist-out", "arena_reserve")


class FeatureEnabledAcceptsRaincoverKeyTests(unittest.TestCase):
    """validate_native_features.feature_enabled() must accept the extra manifest.native_features
    ['raincover'] entry this module's export() adds (task item d: 'validate_extract must accept
    the new package folder and manifest key') without breaking its strict key-equality compare()
    for packages that DON'T carry it (the other eleven deferred maps, and pre-item-6 packages)."""

    def setUp(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
        import validate_native_features as v
        self.v = v

    def _bay5_manifest(self, with_raincover):
        contract = self.v.MAPS["arena_bay5"]
        native_features = {"schema_version": 1, "required": ["ballistics", contract["feature"]]}
        if with_raincover:
            native_features["raincover"] = {"schema_version": 1, "indoor_trigger_count": 30,
                                             "dry_plane_count": 669, "weather_obstacle": True}
        return {
            "scenes": list(contract["scenes"].values()),
            "native_features": native_features,
            "native_ballistics": {}, "native_audio": {},
        }

    def test_accepts_manifest_without_raincover(self):
        self.assertTrue(self.v.feature_enabled(self._bay5_manifest(with_raincover=False)))

    def test_accepts_manifest_with_raincover(self):
        self.assertTrue(self.v.feature_enabled(self._bay5_manifest(with_raincover=True)))

    def test_rejects_malformed_raincover_summary(self):
        manifest = self._bay5_manifest(with_raincover=True)
        manifest["native_features"]["raincover"] = {"schema_version": 1}  # missing indoor_trigger_count
        with self.assertRaises(ValueError):
            self.v.feature_enabled(manifest)


if __name__ == "__main__":
    unittest.main()
