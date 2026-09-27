import math
from pathlib import Path
import struct
import sys
from types import SimpleNamespace as NS
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import extract_decals as D
from common_ids import HardExtractError


def payload(**changes):
    values = dict(lower=(10, -2), upper=(400, 1025), size=(1024, 1024), file=3, path=456,
                  queue=2, normal=-.2, nf=10, edge=.3, ef=1, tint=(.2, .3, .4, .7))
    values.update(changes)
    header = struct.pack('<iqB3xiqi', 0, 9, 1, 1, 88, 0)
    return header + struct.pack('<6fiqi8f', *values['lower'], *values['upper'], *values['size'],
                               values['file'], values['path'], values['queue'], values['normal'],
                               values['nf'], values['edge'], values['ef'], *values['tint'])


class DecalTests(unittest.TestCase):
    def test_preserves_original_values_and_separate_material_pointer(self):
        data = D.decode(payload())
        self.assertEqual(data['material_pointer'], dict(file_id=3, path_id=456))
        self.assertEqual(data['lower_left'], [10, -2])
        self.assertEqual(data['upper_right'], [400, 1025])  # deliberately not clamped
        self.assertEqual(data['texture_size'], [1024, 1024])
        self.assertEqual(data['queue'], 2)
        self.assertAlmostEqual(data['tint'][3], .7)

    def test_truncated_or_extended_payload_rejected(self):
        for raw in (payload()[:-1], payload()+b'\0', payload()[:16]):
            with self.subTest(length=len(raw)), self.assertRaises(HardExtractError):
                D.decode(raw)

    def test_invalid_scalars_and_references_rejected(self):
        for changes in ({'path':0}, {'file':-1}, {'normal':math.nan}, {'normal':2},
                        {'edge':-1}, {'queue':10}, {'nf':-1}, {'size':(0,1024)}):
            with self.subTest(changes=changes), self.assertRaises(HardExtractError):
                D.decode(payload(**changes))

    def test_original_disabled_state_preserved(self):
        raw = bytearray(payload()); raw[12] = 0
        self.assertFalse(D.decode(raw)['enabled'])

    def fixtures(self, size=(512,512), material_type='Material'):
        texture = NS(type=NS(name='Texture2D'), read_typetree=lambda:dict(m_Width=size[0],m_Height=size[1]))
        mat = NS(type=NS(name=material_type), path_id=456, read=lambda:NS(m_Name='synthetic',
                 m_SavedProperties=NS(m_TexEnvs=[('_MainTex',NS(m_Texture=NS(deref=lambda:texture)))])))
        reader = NS(type=NS(name='MonoBehaviour'), path_id=30, assets_file=object(),
                    get_raw_data=payload, parse_monobehaviour_head=lambda:NS(m_GameObject=NS(file_id=0,path_id=9)))
        file = NS(objects={30:reader})
        ctx = NS(queue_material=lambda m:'synthetic-id',owning_basename=lambda m:'fixture.assets')
        scene = {'objects':[{'id':'9','components':[]}]}
        return mat,file,ctx,scene

    def augment(self, **kwargs):
        mat,file,ctx,scene = self.fixtures(**kwargs)
        with patch.object(D.census,'resolve_script_name',return_value=(D.SOURCE_TYPE,'ok')), \
             patch.object(D,'PPtr',return_value=NS(deref=lambda:mat)), \
             patch('extract_equator.resolve_shader_name',return_value=D.SHADER):
            self.assertEqual(D.augment_scene(scene,file,ctx),1)
            self.assertEqual(D.augment_scene(scene,file,ctx),1)
            self.assertEqual(len(scene['objects'][0]['components']),1)
        return scene['objects'][0]['components'][0]

    def test_source_material_dereferenced_and_half_resolution_preserved(self):
        comp=self.augment()
        self.assertEqual(comp['material_id'],'synthetic-id')
        self.assertEqual(comp['source_texture_size'],[512,512])
        self.assertEqual(comp['texture_size'],[1024,1024])

    def test_wrong_asset_type_is_not_accepted_as_material(self):
        with self.assertRaises(HardExtractError): self.augment(material_type='Mesh')

    def test_aspect_or_unexpected_resolution_change_rejected(self):
        for size in [(512,1024),(256,256)]:
            with self.subTest(size=size), self.assertRaises(HardExtractError): self.augment(size=size)


if __name__ == '__main__': unittest.main()
