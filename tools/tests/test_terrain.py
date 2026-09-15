"""Terrain invariants use asymmetric source fixtures and failure controls."""
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from types import SimpleNamespace

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import terrain
from common_ids import HardExtractError


def fixture():
    return {'m_Resolution':3,'m_Heights':[0,32766,0,8192,16384,24576,32766,0,8192],
            'm_Scale':{'x':2,'y':10,'z':3},'m_Holes':[]}


class TerrainGeometryTests(unittest.TestCase):
    def test_height_axes_range_and_resolution_are_preserved(self):
        y, solid, scale = terrain.decode_heightmap(fixture())
        tile = terrain.mesh_tile(y,solid,scale,0,0,2)
        self.assertEqual(y.shape,(3,3))
        np.testing.assert_allclose(tile['positions'][1],[2,10,0])
        np.testing.assert_allclose(tile['positions'][6],[0,10,6])
        self.assertEqual(len(tile['indices']),24)
        self.assertAlmostEqual(float(y[1,0]),8192 / 32766 * 10,places=5)
        # A /65535 implementation would produce 5m, not the required 10m.
        self.assertGreater(float(y.max()),9.99)

    def test_clockwise_viewed_from_above_has_upward_geometric_normal(self):
        y, solid, scale = terrain.decode_heightmap(fixture())
        tile = terrain.mesh_tile(y,solid,scale,0,0,2)
        triangles=tile['positions'][tile['indices'].reshape(-1,3)]
        normals=np.cross(triangles[:,1]-triangles[:,0],triangles[:,2]-triangles[:,0])
        self.assertTrue(np.all(normals[:,1] > 0))
        self.assertTrue(np.all(tile['normals'][:,1] > 0))
        np.testing.assert_allclose(np.linalg.norm(tile['normals'],axis=1),1,atol=1e-6)

    def test_hole_removes_both_render_and_collision_triangles(self):
        data=fixture();data['m_Holes']=[255,0,255,255]
        y,solid,scale=terrain.decode_heightmap(data)
        tile=terrain.mesh_tile(y,solid,scale,0,0,2)
        self.assertEqual(len(tile['indices']),18)
        # The top-right cell's outer corner is referenced by no remaining cell.
        self.assertNotIn(2,tile['indices'].tolist())
        # One single shared generated mesh is the render and collider source.
        self.assertFalse(solid[0,1])
        data['m_Holes']=[0]*4
        y,solid,scale=terrain.decode_heightmap(data)
        empty=terrain.mesh_tile(y,solid,scale,0,0,2)
        self.assertEqual(len(empty['indices']),0)
        with tempfile.TemporaryDirectory() as d:
            with self.assertRaises(HardExtractError):terrain.write_tile_mesh(Path(d)/'empty.bin',empty)

    def test_partition_keeps_samples_and_seam_normals(self):
        y=np.arange(81,dtype=np.float32).reshape(9,9)**2
        solid=np.ones((8,8),dtype=bool)
        left=terrain.mesh_tile(y,solid,(1,1,2),0,0,4)
        right=terrain.mesh_tile(y,solid,(1,1,2),4,0,4)
        np.testing.assert_array_equal(left['positions'].reshape(5,5,3)[:,-1],right['positions'].reshape(5,5,3)[:,0])
        np.testing.assert_array_equal(left['normals'].reshape(5,5,3)[:,-1],right['normals'].reshape(5,5,3)[:,0])
        self.assertEqual(len(left['indices'])+len(right['indices']),8*4*6)

    def test_written_mesh_binary_consumes_exactly_and_retains_uint16_indices(self):
        y,solid,scale=terrain.decode_heightmap(fixture())
        tile=terrain.mesh_tile(y,solid,scale,0,0,2)
        with tempfile.TemporaryDirectory() as d:
            p=Path(d)/'ground.bin';meta=terrain.write_tile_mesh(p,tile);raw=p.read_bytes()
        magic,version,count,channels,idx32,subcount=struct.unpack_from('<4sIIHBB',raw)
        self.assertEqual((magic,version,count,channels,idx32,subcount),(b'AMPM',1,9,23,0,1))
        suboffset=16+count*(3+3+4+2)*4
        self.assertEqual(struct.unpack_from('<IIB',raw,suboffset),(0,24,0))
        self.assertEqual(len(raw),suboffset+9+24*2)
        self.assertEqual(meta['triangle_count'],8)
        np.testing.assert_array_equal(np.frombuffer(raw,dtype='<u2',offset=suboffset+9),tile['indices'])

    def test_bad_source_layouts_fail_instead_of_flattening_ground(self):
        for key,value in [('m_Resolution',4),('m_Heights',[0]*8),('m_Heights',[65535]*9),('m_Heights',[0.5]*9),('m_Holes',[255]*3),('m_Holes',[1]*4),('m_Scale',{'x':1,'y':float('inf'),'z':1})]:
            data=fixture();data[key]=value
            with self.subTest(key=key,value=value):
                with self.assertRaises(HardExtractError):terrain.decode_heightmap(data)


class TerrainSurfaceTests(unittest.TestCase):
    def test_actual_top_four_height_blend_does_not_reduce_to_plain_splat_mix(self):
        weights=np.array([0.4,0.3,0.2,0.1]+[0]*8)
        alpha=np.array([0.1,1,0.1,0.1]+[1]*8)
        blend=terrain.microsplat_reference(weights,alpha,0.492)
        self.assertEqual(int(np.argmax(blend)),1)
        self.assertGreater(blend[1],0.99)
        self.assertEqual(blend[0],0)
        self.assertAlmostEqual(float(blend.sum()),1)
        # Equal heights/weights keep four equal contributors, never all twelve.
        equal=terrain.microsplat_reference(np.ones(12),np.ones(12),0.492)
        np.testing.assert_allclose(equal[8:],[0.25]*4)
        self.assertEqual(np.count_nonzero(equal),4)
        with self.assertRaises(HardExtractError):terrain.microsplat_reference(np.zeros(12),np.ones(12),0.492)

    def test_bc3_array_slices_preserve_layer_order_and_height_alpha(self):
        block0=struct.pack('<BB6sHHI',64,0,bytes(6),0xf800,0,0)
        block1=struct.pack('<BB6sHHI',192,0,bytes(6),0x07e0,0,0)
        data=SimpleNamespace(m_Format=101,m_Width=4,m_Height=4,m_MipCount=1,m_Depth=2,
            m_DataSize=32,image_data=block0+block1,m_ColorSpace=1,m_Name='fixture',m_StreamData=None)
        obj=SimpleNamespace(read=lambda:data,path_id=42,assets_file=SimpleNamespace(name='fixture.assets'),
                            version=(6000,3,0,0),platform=0)
        with tempfile.TemporaryDirectory() as d:
            output=Path(d);(output/'textures').mkdir()
            cid,record=terrain.export_array(None,obj,output,'diffuse')
            from PIL import Image
            first=Image.open(output/record['array_slices'][0]).getpixel((0,0))
            second=Image.open(output/record['array_slices'][1]).getpixel((0,0))
            self.assertEqual(first,(255,0,0,64));self.assertEqual(second,(0,255,0,192))
            self.assertEqual(record['depth'],2);self.assertEqual(record['color_space'],'sRGB')
            self.assertEqual(record['file'],record['array_slices'][0])
        data.m_DataSize=31
        with self.assertRaises(HardExtractError):terrain.source_array_bytes(None,obj)
        data.m_DataSize=32;data.m_Format=100
        with self.assertRaises(HardExtractError):terrain.source_array_bytes(None,obj)

    def test_bc3_mip_stride_counts_minimum_blocks_for_last_two_mips(self):
        sizes=terrain.dxt5_mip_sizes(1024,1024,11)
        self.assertEqual(sum(sizes),1398128)
        self.assertEqual(sizes[-3:],[16,16,16])
        self.assertEqual(sum(sizes)*12,16777536)
        with self.assertRaises(HardExtractError):terrain.dxt5_mip_sizes(4,4,4)

    def test_renderer_and_collider_enabled_flags_remain_independent(self):
        t={'m_Enabled':0,'m_DrawHeightmap':True}
        c={'m_Enabled':True}
        records=terrain.component_records('mesh','material',t,c)
        self.assertFalse(records[1]['enabled']);self.assertTrue(records[2]['enabled'])
        self.assertEqual(records[0]['mesh_id'],records[2]['mesh_id'])
        t['m_Enabled']=1;t['m_DrawHeightmap']=False;c['m_Enabled']=False
        records=terrain.component_records('mesh','material',t,c)
        self.assertFalse(records[1]['enabled']);self.assertFalse(records[2]['enabled'])

    def test_blends_every_layer_in_linear_light(self):
        weights=np.array([[0.5,0.5,0]])
        diffuse=np.array([[[1,0,0],[0,0,1],[0,1,0]]])
        pixel=terrain.blend_layers(weights,diffuse)
        self.assertAlmostEqual(float(pixel[0,0]),0.735357,places=5)
        self.assertAlmostEqual(float(pixel[0,2]),0.735357,places=5)
        self.assertEqual(float(pixel[0,1]),0)
        changed=terrain.blend_layers([[0,0,1]],diffuse)
        np.testing.assert_allclose(changed,[[0,1,0]],atol=1e-6)

    def test_invalid_splat_weights_fail(self):
        diffuse=np.zeros((1,2,3))
        for weights in ([[0,0]],[[-1,2]],[[float('nan'),1]]):
            with self.assertRaises(HardExtractError):terrain.blend_layers(weights,diffuse)

    def test_uv_axes_and_repeat_texel_centers(self):
        pixels=np.array([[[1,0,0],[0,1,0]],[[0,0,1],[1,1,1]]],dtype=np.float32)
        np.testing.assert_allclose(terrain.sample_bilinear(pixels,0.25,0.75),[0,0,1])
        np.testing.assert_allclose(terrain.sample_bilinear(pixels,1.25,-0.75,repeat=True),[1,0,0])
        np.testing.assert_allclose(terrain.sample_bilinear(pixels,0.5,0.5),[0.5]*3)
        with self.assertRaises(HardExtractError):terrain.sample_bilinear(pixels,float('inf'),0)

    def test_required_foliage_or_unknown_material_is_never_silently_dropped(self):
        data={'m_DetailDatabase':{'m_TreeInstances':[{}]}}
        with self.assertRaises(HardExtractError):terrain.validate_required_features({'m_DrawTreesAndFoliage':True},None,data)
        with self.assertRaises(HardExtractError):terrain.validate_required_features({'m_DrawTreesAndFoliage':False},{'m_EnableTreeColliders':True},data)
        with self.assertRaises(HardExtractError):terrain.validate_required_features({'m_DrawTreesAndFoliage':False,'m_MaterialTemplate':{'m_PathID':42}},None,data)
        terrain.validate_required_features({'m_DrawTreesAndFoliage':False},{'m_EnableTreeColliders':False},data)


if __name__=='__main__':unittest.main()
