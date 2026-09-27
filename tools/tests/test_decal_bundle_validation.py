import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace as NS
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'validate'))
import validate_decals as V


class DecalBundleTests(unittest.TestCase):
    def check_fixture(self, mutation=None, missing=False, assembly='Assembly-CSharp'):
        comp=dict(type='StaticDeferredDecal', material_id='synthetic-material',enabled=True,
                  lower_left=[0,-2],upper_right=[2048,1024],texture_size=[2048,1024],queue=1,
                  normal_clip=.3,normal_fade=10,edge_clip=.4,edge_fade=1,tint=[1,1,1,.5])
        tree=dict(m_Script={'m_FileID':0,'m_PathID':1},_material={'m_FileID':0,'m_PathID':2},m_Enabled=1,
                  LowerLeftPixel={'x':0,'y':-1},UpperRightPixel={'x':1024,'y':512},_decalQueue=1,
                  _normalClip=.3,_normalAlphaFadeoff=10,_edgeClip=.4,_edgeAlphaFadeoff=1,
                  _tint=dict(r=1,g=1,b=1,a=.5))
        if mutation: mutation(tree)
        reader=NS(type=NS(name='MonoBehaviour'),read_typetree=lambda:tree,assets_file=object())
        texture=NS(read=lambda:NS(m_Width=1024,m_Height=512))
        materials=NS(read=lambda:NS(m_Name='synthetic-material',m_SavedProperties=NS(m_TexEnvs=[
            ('_MainTex',NS(m_Texture=NS(deref=lambda:texture)))])))
        script=NS(read_typetree=lambda:dict(m_ClassName='StaticDeferredDecal',m_Namespace='',m_AssemblyName=assembly))
        targets={1:script,2:materials}
        pp=lambda **kw:NS(deref=lambda:targets[kw['m_PathID']])
        manifest=dict(scenes=['Synthetic'],native_features=dict(static_decals=dict(schema_version=1,count=1)))
        chain=[('Owner',[1,2,3],[0,0,0,1],[1,1,1])]
        with tempfile.TemporaryDirectory() as temp, patch.object(V,'PPtr',side_effect=pp):
            path=Path(temp);(path/'scenes').mkdir()
            (path/'scenes/Synthetic.json').write_text(json.dumps(dict(objects=[dict(id='1',active=True,components=[comp])])),encoding='utf-8')
            sf=NS(name='BuildPlayer-Synthetic',objects={} if missing else {3:reader})
            return V.bundle_errors(None,path,manifest,[sf],lambda o,owners:chain,lambda r,cache:dict(chain=chain,active=True))

    def test_real_uv_region_survives_downscaled_texture(self):
        self.assertEqual(self.check_fixture(),[])

    def test_missing_native_component_fails(self):
        self.assertTrue(self.check_fixture(missing=True))

    def test_editor_reference_assembly_is_rejected(self):
        self.assertTrue(self.check_fixture(assembly='SptGameRefs-AssemblyCSharp.dll'))

    def test_wrong_pixel_scaling_and_changed_fields_fail(self):
        for mutation in (lambda t:t.update(UpperRightPixel=dict(x=2048,y=1024)),
                         lambda t:t.update(_decalQueue=0), lambda t:t.update(m_Enabled=0),
                         lambda t:t.update(_tint=dict(r=1,g=1,b=1,a=1))):
            with self.subTest(mutation=mutation): self.assertTrue(self.check_fixture(mutation))


if __name__ == '__main__': unittest.main()
