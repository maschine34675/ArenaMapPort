"""Negative controls for source-independent volume validation."""
import copy
import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validate"))
import native_volume as v


class NativeVolumeValidationTests(unittest.TestCase):
    def inspect(self, mutation=None):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory)
            (package / "textures").mkdir()
            source_file, path_id = "sharedassets7.assets", 19
            key = hashlib.sha1(f"{source_file}:{path_id}:Texture2D".encode()).hexdigest()[:16]
            raw = bytes(range(16))
            filename = f"textures/{key}.volume.bin"
            (package / filename).write_bytes(raw)
            sampler = dict(filter_mode=1, aniso_level=0, mip_bias=0, wrap_u=1, wrap_v=1, wrap_w=1)
            volume = dict(schema_version=1, encoding="Alpha8", layout="x-fastest-y-z-mips", file=filename,
                          source_file=source_file, source_path_id=path_id, width=4, height=2, depth=2,
                          mip_count=1, mips=[dict(width=4, height=2, depth=2, offset=0, byte_length=16)],
                          byte_length=16, sha256=hashlib.sha256(raw).hexdigest(), source_graphics_format=54,
                          color_space="Linear", sampler=sampler)
            meta = dict(native_volume=volume, dimension="Tex3D", width=4, height=4,
                        color_space="Linear", is_normal_map=False)
            tree = dict(m_Name=key, m_Format=54, m_Width=4, m_Height=2, m_Depth=2,
                        m_MipCount=1, m_ColorSpace=0, m_DataSize=16,
                        m_TextureSettings=dict(m_FilterMode=1, m_Aniso=0, m_MipBias=0,
                                               m_WrapU=1, m_WrapV=1, m_WrapW=1))
            source_tree = copy.deepcopy(tree)
            source = NS(type=NS(name="Texture3D"), path_id=path_id, assets_file=NS(name=source_file),
                        read_typetree=lambda: source_tree, read=lambda: NS(image_data=raw))
            built_data = NS(image_data=raw)
            built = NS(type=NS(name="Texture3D"), path_id=123, read_typetree=lambda: tree,
                       read=lambda: built_data)
            if mutation:
                mutation(package, meta, tree, built_data)
            with patch("UnityPy.load", return_value=NS(objects=[source])):
                return v.source_and_bundle_errors(NS(objects=[built]), package,
                    dict(source=dict(arena_data_root=directory), textures={key: meta}))

    def test_exact_native_alpha_payload_passes(self):
        self.assertEqual([], self.inspect())

    def test_lost_alpha_in_built_texture_fails(self):
        errors = self.inspect(lambda p, m, t, b: setattr(b, "image_data", bytes([255]) * 16))
        self.assertTrue(any("built alpha bytes" in e for e in errors))

    def test_reversed_slices_with_self_consistent_package_hash_still_fail_source(self):
        def mutate(package, meta, tree, built):
            raw = bytes(range(8, 16)) + bytes(range(8))
            volume = meta["native_volume"]
            (package / volume["file"]).write_bytes(raw)
            volume["sha256"] = hashlib.sha256(raw).hexdigest()
            built.image_data = raw
        self.assertTrue(any("original source bytes" in e for e in self.inspect(mutate)))

    def test_wrong_channel_format_fails_even_with_correct_bytes(self):
        self.assertTrue(any("m_Format" in e for e in self.inspect(lambda p, m, t, b: t.update(m_Format=5))))

    def test_sampler_drift_fails(self):
        self.assertTrue(any("sampler" in e for e in self.inspect(
            lambda p, m, t, b: t["m_TextureSettings"].update(m_WrapW=0))))

    def test_mip_layout_drift_fails(self):
        self.assertTrue(any("mip layout" in e for e in self.inspect(
            lambda p, m, t, b: m["native_volume"]["mips"][0].update(offset=1))))

    def test_untrusted_payload_path_fails(self):
        self.assertTrue(any("payload path" in e for e in self.inspect(
            lambda p, m, t, b: m["native_volume"].update(file="../outside.bin"))))

    def bindings(self, texture_class="Texture3D", texture_name="volume", assigned=True):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory)
            (package / "materials").mkdir()
            (package / "materials" / "dust.json").write_text(json.dumps(
                dict(tex_envs={"_ParticleTex": dict(texture_id="volume")})), encoding="utf-8")
            texture = NS(type=NS(name=texture_class), read_typetree=lambda: dict(m_Name=texture_name))
            pointer = NS(path_id=123 if assigned else 0, deref=lambda: texture)
            material = NS(type=NS(name="Material"), read_typetree=lambda: dict(m_Name="dust"),
                          read=lambda: NS(m_SavedProperties=NS(m_TexEnvs=[
                              ("_ParticleTex", NS(m_Texture=pointer))])))
            return v.material_binding_errors(NS(objects=[material]), package,
                                             dict(materials={"dust": {}}), "volume")

    def test_exact_material_volume_binding_passes(self):
        self.assertEqual([], self.bindings())

    def test_unassigned_material_volume_fails(self):
        self.assertTrue(self.bindings(assigned=False))

    def test_wrong_material_volume_dimension_fails(self):
        self.assertTrue(self.bindings(texture_class="Texture2D"))

    def test_wrong_material_volume_identity_fails(self):
        self.assertTrue(self.bindings(texture_name="unrelated"))


if __name__ == "__main__":
    unittest.main()
