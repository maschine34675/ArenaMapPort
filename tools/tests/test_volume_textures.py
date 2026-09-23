"""Native-volume encoding/layout regressions; fixtures contain no game assets."""
import copy
import hashlib
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extract"))
from volume_textures import alpha8_preview, alpha8_volume_metadata, write_volume_texture, UnsupportedVolumeFormat


def fixture(width=3, height=2, depth=2, mip_count=1):
    size = sum(max(1, width >> level) * max(1, height >> level) * max(1, depth >> level)
               for level in range(mip_count))
    return {
        "m_Format": 54, "m_ColorSpace": 0, "m_Width": width, "m_Height": height,
        "m_Depth": depth, "m_MipCount": mip_count, "m_DataSize": size,
        "m_TextureSettings": {"m_FilterMode": 1, "m_Aniso": 0, "m_MipBias": 0.0,
                              "m_WrapU": 1, "m_WrapV": 2, "m_WrapW": 3},
        "image data": bytes(range(size)), "m_StreamData": {"size": 0},
    }


class FakeTexture:
    type = SimpleNamespace(name="Texture3D")
    assets_file = SimpleNamespace(name="sharedassets1.assets")
    path_id = 23

    def __init__(self, tree):
        self.tree = tree

    def read_typetree(self):
        return self.tree


TEXTURE_ID = hashlib.sha1(b"sharedassets1.assets:23:Texture2D").hexdigest()[:16]


class NativeVolumeTests(unittest.TestCase):
    def test_native_alpha8_payload_keeps_all_channels_and_axes(self):
        tree = fixture()
        raw = tree["image data"]
        with tempfile.TemporaryDirectory() as folder:
            metadata, preview = write_volume_texture(FakeTexture(tree), TEXTURE_ID, folder)
            self.assertEqual((Path(folder) / metadata["file"]).read_bytes(), raw)
            self.assertEqual(metadata["sha256"], hashlib.sha256(raw).hexdigest())
            self.assertEqual(metadata["source_graphics_format"], 54)
            self.assertEqual(metadata["source_file"], "sharedassets1.assets")
            self.assertEqual(metadata["source_path_id"], 23)
            self.assertEqual(metadata["encoding"], "Alpha8")
            self.assertEqual(metadata["layout"], "x-fastest-y-z-mips")
            self.assertEqual(metadata["color_space"], "Linear")
            # Each PNG slice flips Y for display, but Z must NOT reverse. Distinct
            # xyz test values expose the previous whole-strip flip/ASTC mistake.
            self.assertEqual(preview.size, (3, 4))
            self.assertEqual(list(preview.getchannel("A").tobytes()),
                             [3, 4, 5, 0, 1, 2, 9, 10, 11, 6, 7, 8])
            self.assertEqual(preview.getpixel((0, 0)), (255, 255, 255, 3))
            self.assertEqual(preview.getpixel((0, 1)), (255, 255, 255, 0))

    def test_non_cubic_source_mips_are_preserved_without_regeneration(self):
        tree = fixture(4, 2, 2, 3)
        metadata = alpha8_volume_metadata(tree, tree["image data"])
        self.assertEqual(metadata["mips"], [
            dict(width=4, height=2, depth=2, offset=0, byte_length=16),
            dict(width=2, height=1, depth=1, offset=16, byte_length=2),
            dict(width=1, height=1, depth=1, offset=18, byte_length=1)])
        self.assertEqual(metadata["byte_length"], 19)
        # The preview excludes smaller mip levels.
        self.assertEqual(alpha8_preview(metadata, tree["image data"]).size, (4, 4))

    def test_partial_mip_chain_stays_partial(self):
        tree = fixture(4, 2, 2, 2)
        self.assertEqual(alpha8_volume_metadata(tree, tree["image data"])["mip_count"], 2)

    def test_sampler_values_survive_independently_for_all_axes(self):
        tree = fixture()
        tree["m_TextureSettings"].update(m_FilterMode=2, m_Aniso=7, m_MipBias=-0.375)
        self.assertEqual(alpha8_volume_metadata(tree, tree["image data"])["sampler"],
                         dict(filter_mode=2, aniso_level=7, mip_bias=-0.375, wrap_u=1, wrap_v=2, wrap_w=3))

    def test_unknown_source_format_is_explicitly_unsupported(self):
        tree = fixture()
        tree["m_Format"] = 8
        with self.assertRaises(UnsupportedVolumeFormat):
            alpha8_volume_metadata(tree, tree["image data"])

    def test_wrong_color_space_rejected(self):
        for color_space in (1, None, "Linear", False):
            with self.subTest(color_space=color_space):
                tree = fixture()
                tree["m_ColorSpace"] = color_space
                with self.assertRaises(ValueError):
                    alpha8_volume_metadata(tree, tree["image data"])

    def test_zero_negative_fractional_boolean_and_huge_dimensions_rejected(self):
        for field in ("m_Width", "m_Height", "m_Depth"):
            for value in (0, -1, 1.5, True, "2", 2049, None):
                with self.subTest(field=field, value=value):
                    tree = fixture()
                    tree[field] = value
                    with self.assertRaises(ValueError):
                        alpha8_volume_metadata(tree, tree["image data"])

    def test_missing_empty_extra_and_truncated_voxels_rejected(self):
        tree = fixture()
        for raw in (b"", tree["image data"][:-1], tree["image data"] + b"\0"):
            with self.subTest(length=len(raw)), self.assertRaises(ValueError):
                alpha8_volume_metadata(tree, raw)

    def test_reported_source_size_must_agree(self):
        for size in (0, 11, 13, "12", True, None):
            tree = fixture()
            tree["m_DataSize"] = size
            with self.subTest(size=size), self.assertRaises(ValueError):
                alpha8_volume_metadata(tree, tree["image data"])

    def test_invalid_mip_counts_rejected(self):
        for count in (0, -1, 3, True, "1", None):
            tree = fixture()
            tree["m_MipCount"] = count
            with self.subTest(count=count), self.assertRaises(ValueError):
                alpha8_volume_metadata(tree, tree["image data"])

    def test_sampler_corruption_rejected(self):
        invalid = {"m_FilterMode": [-1, 3, "1", None], "m_Aniso": [-1, 17, True],
                   "m_WrapU": [-1, 4], "m_WrapV": [-1, 4], "m_WrapW": [-1, 4],
                   "m_MipBias": [float("nan"), float("inf"), -17, 17, True, "0", None]}
        for field, values in invalid.items():
            for value in values:
                tree = fixture()
                tree["m_TextureSettings"][field] = value
                with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                    alpha8_volume_metadata(tree, tree["image data"])

    def test_missing_sampler_rejected(self):
        tree = fixture()
        del tree["m_TextureSettings"]
        with self.assertRaisesRegex(ValueError, "sampler"):
            alpha8_volume_metadata(tree, tree["image data"])

    def test_oversized_volume_rejected_before_io(self):
        tree = fixture()
        tree.update(m_Width=2048, m_Height=2048, m_Depth=2048)
        with self.assertRaisesRegex(ValueError, "64MiB"):
            alpha8_volume_metadata(tree, b"")

    def test_wrong_object_class_rejected(self):
        obj = FakeTexture(fixture())
        obj.type = SimpleNamespace(name="Texture2D")
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(ValueError, "requires Texture3D"):
            write_volume_texture(obj, TEXTURE_ID, folder)

    def test_bad_identity_cannot_write_payload_outside_owned_path(self):
        for texture_id in ("../bad", "A" * 16, "0" * 16, TEXTURE_ID + ".bin"):
            with tempfile.TemporaryDirectory() as folder:
                with self.subTest(texture_id=texture_id), self.assertRaises(ValueError):
                    write_volume_texture(FakeTexture(fixture()), texture_id, folder)
                self.assertEqual(list(Path(folder).iterdir()), [])

    def test_source_id_mismatch_rejected(self):
        obj = FakeTexture(fixture())
        obj.path_id = 24
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(ValueError, "provenance"):
            write_volume_texture(obj, TEXTURE_ID, folder)

    def test_corrupt_source_cannot_leave_plausible_payload(self):
        tree = fixture()
        tree["image data"] = tree["image data"][:-1]
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(ValueError):
                write_volume_texture(FakeTexture(tree), TEXTURE_ID, folder)
            self.assertEqual(list(Path(folder).iterdir()), [])

    def test_streamed_payload_uses_same_raw_contract(self):
        tree = fixture()
        raw = tree["image data"]
        tree["image data"] = b""
        tree["m_StreamData"] = dict(size=len(raw), offset=17, path="archive:/volume.resS")
        with patch("UnityPy.helpers.ResourceReader.get_resource_data", return_value=raw) as reader:
            with tempfile.TemporaryDirectory() as folder:
                metadata, _ = write_volume_texture(FakeTexture(tree), TEXTURE_ID, folder)
                self.assertEqual((Path(folder) / metadata["file"]).read_bytes(), raw)
                reader.assert_called_once_with("archive:/volume.resS", FakeTexture.assets_file, 17, len(raw))

    def test_ambiguous_inline_and_streamed_payload_rejected(self):
        tree = fixture()
        tree["m_StreamData"] = dict(size=12, offset=17, path="archive:/volume.resS")
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(ValueError, "ambiguous"):
            write_volume_texture(FakeTexture(tree), TEXTURE_ID, folder)

    def test_invalid_stream_metadata_rejected_before_resource_access(self):
        invalid = [None, dict(size=-1), dict(size=True), dict(size=64 * 1024 * 1024 + 1),
                   dict(size=12, offset=-1, path="x"), dict(size=12, offset=0, path="")]
        for stream in invalid:
            tree = fixture()
            tree["image data"] = b""
            tree["m_StreamData"] = stream
            with self.subTest(stream=stream), tempfile.TemporaryDirectory() as folder:
                with patch("UnityPy.helpers.ResourceReader.get_resource_data") as reader:
                    with self.assertRaises(ValueError):
                        write_volume_texture(FakeTexture(tree), TEXTURE_ID, folder)
                    reader.assert_not_called()

    def test_two_exports_are_byte_identical_and_do_not_mutate_source(self):
        tree = fixture(4, 2, 2, 3)
        untouched = copy.deepcopy(tree)
        with tempfile.TemporaryDirectory() as folder:
            first, first_preview = write_volume_texture(FakeTexture(tree), TEXTURE_ID, folder)
            second, second_preview = write_volume_texture(FakeTexture(tree), TEXTURE_ID, folder)
            self.assertEqual(first, second)
            self.assertEqual(first_preview.tobytes(), second_preview.tobytes())
            self.assertEqual(tree, untouched)


if __name__ == "__main__":
    unittest.main()
