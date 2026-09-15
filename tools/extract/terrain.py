"""Convert source Unity Terrain into ordinary package mesh/material records.

The mesh keeps every source height sample. Tiles are a partition, not a height
decimation: shared border samples/normals agree and holes remove the same cells
from rendering and collision. Generated objects are children of the original
Terrain GameObject, preserving its complete source transform/activation chain.
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import struct

import numpy as np
from PIL import Image

from common_ids import HardExtractError, content_id, CAST_SHADOWS, reflection_probe_usage

HEIGHT_DENOMINATOR = 32766.0
TILE_CELLS = 128
SCHEMA_VERSION = 1


def decode_heightmap(heightmap: dict) -> tuple[np.ndarray, np.ndarray, tuple[float, float, float]]:
    """Return local Y heights[z,x], solid cells[z,x], and source sample scale.

    Unity's serialized height samples use signed-positive 16-bit normalization,
    not UNorm16. All seven Arena data objects independently corroborate /32766
    with their serialized min/max patch hierarchy (probe report).
    """
    n = heightmap.get('m_Resolution')
    if not isinstance(n, int) or n < 2 or (n - 1) & (n - 2):
        raise HardExtractError('Terrain height resolution must be 2^k + 1')
    raw = np.asarray(heightmap.get('m_Heights', []))
    if raw.size != n * n or not np.issubdtype(raw.dtype, np.integer):
        raise HardExtractError('Terrain height sample count/type does not match resolution')
    if raw.min() < 0 or raw.max() > 32767:
        raise HardExtractError('Terrain height sample is outside the source 0..32767 range')
    try:
        scale = tuple(float(heightmap['m_Scale'][k]) for k in ('x', 'y', 'z'))
    except (KeyError, TypeError, ValueError) as exc:
        raise HardExtractError('Terrain sample scale is missing or invalid') from exc
    if not all(math.isfinite(v) and v > 0 for v in scale):
        raise HardExtractError('Terrain sample scale must be finite and positive')
    heights = raw.reshape(n, n).astype(np.float32) * np.float32(scale[1] / HEIGHT_DENOMINATOR)
    holes = heightmap.get('m_Holes', [])
    if len(holes) == 0:
        solid = np.ones((n - 1, n - 1), dtype=np.bool_)
    else:
        arr = np.asarray(holes)
        if arr.size != (n - 1) ** 2 or not np.isin(arr, [0, 255]).all():
            raise HardExtractError('Unsupported terrain hole layout; expected one 0/255 byte per cell')
        solid = arr.reshape(n - 1, n - 1) != 0
    return heights, solid, scale


def height_normals(heights: np.ndarray, sx: float, sz: float) -> np.ndarray:
    dz, dx = np.gradient(heights, sz, sx)
    result = np.stack((-dx, np.ones_like(heights), -dz), axis=-1)
    result /= np.linalg.norm(result, axis=-1, keepdims=True)
    return result.astype('<f4')


def mesh_tile(heights, solid, scale, x0, z0, cells=TILE_CELLS):
    """Build one full-resolution tile, with upward winding and source-local UV."""
    n = heights.shape[0]
    if heights.shape != (n, n) or solid.shape != (n - 1, n - 1):
        raise HardExtractError('Terrain array dimensions disagree')
    if not (0 <= x0 < n - 1 and 0 <= z0 < n - 1) or cells < 1:
        raise HardExtractError('Terrain tile range is invalid')
    x1, z1 = min(x0 + cells, n - 1), min(z0 + cells, n - 1)
    width, depth = x1 - x0 + 1, z1 - z0 + 1
    xx, zz = np.meshgrid(np.arange(x0, x1 + 1), np.arange(z0, z1 + 1))
    position = np.stack((xx * scale[0], heights[z0:z1 + 1, x0:x1 + 1], zz * scale[2]), axis=-1).reshape(-1, 3).astype('<f4')
    # Read a one-sample apron for identical derivatives at chunk borders.
    ax0, az0 = max(0, x0 - 1), max(0, z0 - 1)
    ax1, az1 = min(n, x1 + 2), min(n, z1 + 2)
    normals = height_normals(heights[az0:az1, ax0:ax1], scale[0], scale[2])[z0-az0:z1-az0+1, x0-ax0:x1-ax0+1].reshape(-1, 3)
    tangent = np.empty((position.shape[0], 4), dtype='<f4')
    tangent[:, 0] = normals[:, 1]
    tangent[:, 1] = -normals[:, 0]
    tangent[:, 2] = 0
    tangent[:, :3] /= np.linalg.norm(tangent[:, :3], axis=1, keepdims=True)
    tangent[:, 3] = -1  # cross(normal,+X) is -Z, so +V/+Z needs -1.
    uv = np.stack((xx / (n - 1), zz / (n - 1)), axis=-1).reshape(-1, 2).astype('<f4')
    a = np.arange((depth - 1) * width, dtype=np.uint32).reshape(depth - 1, width)[:, :-1]
    a = a[solid[z0:z1, x0:x1]].reshape(-1)
    indices = np.stack((a, a + width, a + 1, a + 1, a + width, a + width + 1), axis=1).reshape(-1)
    if not all(np.isfinite(arr).all() for arr in (position, normals, tangent, uv)):
        raise HardExtractError('Terrain conversion produced non-finite geometry')
    return {'positions':position,'normals':normals,'tangents':tangent,'uv':uv,'indices':indices,'sample_rect':[x0,z0,x1,z1]}


def write_tile_mesh(path: Path, tile: dict) -> dict:
    count = len(tile['positions'])
    if len(tile['indices']) == 0:
        raise HardExtractError('An entirely holed terrain tile must be skipped, not emitted empty')
    use32 = count > 65535
    with path.open('wb') as fh:
        fh.write(b'AMPM' + struct.pack('<IIHBB', 1, count, 1 | 2 | 4 | 16, int(use32), 1))
        for channel in ('positions','normals','tangents','uv'):
            fh.write(np.asarray(tile[channel], dtype='<f4').tobytes())
        fh.write(struct.pack('<IIB', 0, len(tile['indices']), 0))
        fh.write(np.asarray(tile['indices'], dtype='<u4' if use32 else '<u2').tobytes())
    return {'vertex_count':count,'submesh_count':1,'triangle_count':len(tile['indices']) // 3}


def sample_bilinear(pixels: np.ndarray, u, v, repeat=False):
    """Sample array rows in Unity bottom-to-top orientation, texel-center aware."""
    h, w = pixels.shape[:2]
    u, v = np.broadcast_arrays(u, v)
    if not np.isfinite(u).all() or not np.isfinite(v).all():
        raise HardExtractError('Non-finite terrain texture UV')
    if repeat:
        u, v = np.mod(u, 1), np.mod(v, 1)
    else:
        u, v = np.clip(u, 0, 1), np.clip(v, 0, 1)
    x, y = u * w - 0.5, v * h - 0.5
    x0, y0 = np.floor(x).astype(np.int32), np.floor(y).astype(np.int32)
    fx, fy = (x-x0)[..., None], (y-y0)[..., None]
    x1, y1 = x0 + 1, y0 + 1
    if repeat:
        x0, x1, y0, y1 = x0 % w, x1 % w, y0 % h, y1 % h
    else:
        x0, x1, y0, y1 = np.clip(x0,0,w-1),np.clip(x1,0,w-1),np.clip(y0,0,h-1),np.clip(y1,0,h-1)
    return ((pixels[y0,x0]*(1-fx)+pixels[y0,x1]*fx)*(1-fy)+(pixels[y1,x0]*(1-fx)+pixels[y1,x1]*fx)*fy).astype(np.float32)


def srgb_to_linear(value):
    value = np.asarray(value, dtype=np.float32)
    return np.where(value <= 0.04045, value / 12.92, ((value + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(value):
    value = np.clip(np.asarray(value, dtype=np.float32), 0, 1)
    return np.where(value <= 0.0031308, value * 12.92, 1.055 * value ** (1 / 2.4) - 0.055)


def blend_layers(weights, diffuse_layers):
    """Linear-light blend; every nonzero source layer participates."""
    weights = np.asarray(weights, dtype=np.float32)
    diffuse_layers = np.asarray(diffuse_layers, dtype=np.float32)
    if diffuse_layers.shape[:-1] != weights.shape or diffuse_layers.shape[-1] != 3:
        raise HardExtractError('Terrain layer/weight array shapes disagree')
    total = weights.sum(axis=-1, keepdims=True)
    if not np.isfinite(weights).all() or np.any(weights < 0) or np.any(total <= 0):
        raise HardExtractError('Terrain splat weights are negative, non-finite, or sum to zero')
    if not np.isfinite(diffuse_layers).all():
        raise HardExtractError('Terrain layer pixels are non-finite')
    return linear_to_srgb((srgb_to_linear(diffuse_layers) * (weights / total)[..., None]).sum(axis=-2))


def microsplat_reference(weights, height_alpha, contrast):
    """Small CPU reference for the actual Arena shader's top-four height blend.

    This is validation support, not an atlas renderer. The package preserves
    source textures; its custom shader performs this operation at full detail.
    """
    weights=np.asarray(weights,dtype=np.float64)
    height_alpha=np.asarray(height_alpha,dtype=np.float64)
    if weights.shape!=(12,) or height_alpha.shape!=(12,) or not np.isfinite(weights).all() or not np.isfinite(height_alpha).all():
        raise HardExtractError('MicroSplat reference requires twelve finite weights/heights')
    if np.any(weights<0) or weights.max()<=0 or not math.isfinite(contrast) or contrast<=0:
        raise HardExtractError('Invalid MicroSplat blend weights or contrast')
    # The source shader uses >= while scanning layers, so later indices win ties.
    indices=np.lexsort((-np.arange(12),-weights))[:4]
    selected=weights[indices];selected/=np.linalg.norm(selected)
    h=np.maximum(height_alpha[indices],0.001)*selected
    maximum=h.max();distance=max(maximum*contrast,0.0001)
    blend=np.clip((h-(maximum-distance))/distance,0,1)
    blend/=max(blend.sum(),0.001)
    result=np.zeros(12,dtype=np.float64);result[indices]=blend
    return result


def validate_required_features(terrain: dict, collider: dict | None, data: dict):
    detail = data.get('m_DetailDatabase', {})
    if terrain.get('m_DrawTreesAndFoliage', True) and (detail.get('m_TreeInstances') or detail.get('m_DetailPrototypes')):
        raise HardExtractError('Enabled native terrain foliage requires explicit tree/detail conversion')
    if collider and collider.get('m_EnableTreeColliders') and detail.get('m_TreeInstances'):
        raise HardExtractError('Enabled terrain tree colliders require explicit prototype collision conversion')
    if collider and any(collider.get(k,{}).get('m_Bits',0) for k in ('m_IncludeLayers','m_ExcludeLayers')):
        raise HardExtractError('Terrain collider layer overrides are not representable by package MeshCollider')
    if terrain.get('m_MaterialTemplate', {}).get('m_PathID', 0):
        raise HardExtractError('Custom Terrain material requires an explicitly supported surface adapter')


class SourceResolver:
    """Resolve exact adjacent source file paths, reusing already-loaded files."""
    def __init__(self, env, source):
        self.env, self.source, self.extra = env, Path(source), {}
        self.exported_textures = set()

    def file(self, basename):
        if Path(basename).name != basename:
            raise HardExtractError(f'Terrain source reference is not an adjacent filename: {basename}')
        want = str(self.source / basename).replace('\\','/').lower()
        for key, value in list(self.env.files.items()):
            if str(key).replace('\\','/').lower() == want:
                return value
        if basename not in self.extra:
            import UnityPy
            sub = UnityPy.load(str(self.source / basename))
            self.extra[basename] = sub
        sub = self.extra[basename]
        return next(value for key,value in sub.files.items() if str(key).replace('\\','/').lower() == want)

    def pointer(self, owner, ptr, wanted_type=None):
        pid, fid = ptr.get('m_PathID',0), ptr.get('m_FileID',0)
        if not pid:
            return None
        f = owner.assets_file if hasattr(owner,'assets_file') else owner
        if fid:
            f = self.file(f.externals[fid-1].path)
        obj = f.objects.get(pid)
        if obj is None or (wanted_type and obj.type.name != wanted_type):
            raise HardExtractError(f'Terrain reference {ptr} did not resolve to {wanted_type}')
        return obj


def source_name(obj):
    return Path(obj.assets_file.name).name


def generated_id(obj, role):
    return content_id(source_name(obj), obj.path_id, 'TerrainV1:' + role)


def _ptr_bytes(raw, offset):
    fid,pid=struct.unpack_from('<iq',raw,offset)
    return {'m_FileID':fid,'m_PathID':pid}


def _source_microsplat(resolver, terrain_obj):
    f=terrain_obj.assets_file;go=terrain_obj.read_typetree()['m_GameObject']['m_PathID']
    matches=[]
    for obj in list(f.objects.values()):
        if obj.type.name!='MonoBehaviour':continue
        head=obj.parse_monobehaviour_head()
        if head.m_GameObject.file_id or head.m_GameObject.path_id!=go:continue
        script=resolver.pointer(obj,{'m_FileID':head.m_Script.file_id,'m_PathID':head.m_Script.path_id},'MonoScript')
        if script is None:continue
        st=script.read_typetree()
        if (st.get('m_Namespace'),st.get('m_ClassName'))!=('JBooth.MicroSplat','MicroSplatTerrain'):continue
        raw=obj.get_raw_data()
        if len(raw)!=276 or _ptr_bytes(raw,140)!={'m_FileID':0,'m_PathID':terrain_obj.path_id}:
            raise HardExtractError('Unknown source MicroSplatTerrain serialized layout')
        if any(_ptr_bytes(raw,offset)['m_PathID'] for offset in range(164,260,12)):
            raise HardExtractError('Custom MicroSplat controls require explicit conversion')
        matches.append((obj,resolver.pointer(obj,_ptr_bytes(raw,32),'Material')))
    if len(matches)!=1 or matches[0][1] is None:
        raise HardExtractError('Required source terrain MicroSplat material was not uniquely resolved')
    return matches[0]


def prepare_surface(resolver, terrain_obj, data_obj, data):
    """Read actual MicroSplat source material, controls and float property table.

    TerrainLayer.tileSize values in these Arena scenes are migration stubs with
    Infinity, so they must never be used as the real texture tiling contract.
    The actual source shader uses material _UVScale and PropTex rows zero/three.
    """
    micro_obj,material_obj=_source_microsplat(resolver,terrain_obj)
    material=material_obj.read_typetree();props=material['m_SavedProperties']
    tex=dict(props.get('m_TexEnvs',[]));colors=dict(props.get('m_Colors',[]));floats=dict(props.get('m_Floats',[]))
    diffuse=resolver.pointer(material_obj,tex['_Diffuse']['m_Texture'],'Texture2DArray')
    normal=resolver.pointer(material_obj,tex['_NormalSAO']['m_Texture'],'Texture2DArray')
    prop_obj=resolver.pointer(material_obj,tex['_PerTexProps']['m_Texture'],'Texture2D')
    if any(x is None for x in (diffuse,normal,prop_obj)):
        raise HardExtractError('MicroSplat surface texture or property table is null')
    prop_tex=prop_obj.read()
    if prop_tex.m_TextureFormat!=20 or prop_tex.m_Width!=32 or prop_tex.m_Height!=32:
        raise HardExtractError('MicroSplat property table is not supported 32x32 RGBAFloat')
    raw=prop_tex.get_image_data();needed=32*32*16
    if len(raw)<needed:raise HardExtractError('Truncated MicroSplat property table')
    values=np.frombuffer(raw,dtype='<f4',count=32*32*4).reshape(32,32,4)
    layer_uv=values[0,:12].copy();layer_adjust=values[3,:12].copy()
    if not np.isfinite(layer_uv).all() or not np.isfinite(layer_adjust).all() or np.any(layer_uv[:,:2]<=0):
        raise HardExtractError('Invalid MicroSplat UV or color properties')
    splat=data['m_SplatDatabase']
    if len(splat.get('m_TerrainLayers',[]))!=12 or len(splat.get('m_AlphaTextures',[]))!=3:
        raise HardExtractError('Current terrain shader requires all twelve source layers and three controls')
    controls=[resolver.pointer(data_obj,x,'Texture2D') for x in splat['m_AlphaTextures']]
    if any(x is None for x in controls):raise HardExtractError('Required terrain splat control is null')
    uv_scale=[float(colors['_UVScale'][k]) for k in ('r','g','b','a')]
    contrast=float(floats['_Contrast'])
    if not all(math.isfinite(x) for x in uv_scale+[contrast]) or min(uv_scale[:2])<=0 or contrast<=0:
        raise HardExtractError('Invalid source MicroSplat material scalars')
    for obj in (diffuse,normal):
        typed=obj.read()
        if typed.m_Depth!=12:raise HardExtractError('Source terrain array layer count is not twelve')
    return {'material_obj':material_obj,'diffuse':diffuse,'normal':normal,'controls':controls,
            'layer_uv':layer_uv.tolist(),'layer_adjust':layer_adjust.tolist(),'uv_scale':uv_scale,
            'contrast':contrast,'resolution':splat['m_AlphamapResolution'],
            'provenance':{'adapter':'ArenaMapPort/Terrain','source_microsplat_component':micro_obj.path_id,
                'source_material_file':source_name(material_obj),'source_material_id':material_obj.path_id,
                'source_material_name':material.get('m_Name'),'source_property_texture_file':source_name(prop_obj),
                'source_property_texture_id':prop_obj.path_id,'layer_count':12,'control_count':3,
                'uv_scale':uv_scale,'layer_uv':layer_uv.tolist(),'layer_adjust':layer_adjust.tolist(),
                'height_blend_contrast':contrast,'source_array_resolution':1024,
                'surface_model':'Source top-four height blend, tiled diffuse and NormalSAO; original control and layer pixels preserved.',
                'limitations':['Custom shader does not reproduce MicroSplat secondary distance, displacement, or seasonal runtime systems.']}}


def dxt5_mip_sizes(width, height, mip_count):
    if width<1 or height<1 or mip_count<1 or mip_count>1+int(math.log2(max(width,height))):
        raise HardExtractError('Invalid DXT5 mip dimensions/count')
    return [max(1,(max(1,width>>m)+3)//4)*max(1,(max(1,height>>m)+3)//4)*16 for m in range(mip_count)]


def source_array_bytes(resolver, obj):
    data=obj.read()
    if data.m_Format!=101:
        raise HardExtractError(f'Unsupported terrain array format {data.m_Format}; expected source BC3/DXT5 UNorm')
    mip_sizes=dxt5_mip_sizes(data.m_Width,data.m_Height,data.m_MipCount)
    expected=sum(mip_sizes)*data.m_Depth
    raw=bytes(data.image_data)
    if not raw:
        stream=data.m_StreamData
        if stream is None or Path(stream.path).name!=stream.path:
            raise HardExtractError('Invalid terrain array stream reference')
        with (resolver.source/stream.path).open('rb') as fh:
            fh.seek(stream.offset);raw=fh.read(stream.size)
    if len(raw)!=expected or data.m_DataSize!=expected:
        raise HardExtractError(f'Terrain array stream length mismatch: {len(raw)} vs {expected}')
    return data,raw,mip_sizes


def export_array(resolver, obj, output, role):
    """Export every source array slice base mip without flattening layer identity."""
    from UnityPy.export.Texture2DConverter import parse_image_data
    data,raw,mips=source_array_bytes(resolver,obj)
    expected_space=1 if role=='diffuse' else 0
    if data.m_ColorSpace!=expected_space:
        raise HardExtractError('Source terrain array color space disagrees with its shader role')
    cid=generated_id(obj,'array:'+role);stride=sum(mips);slices=[]
    for layer in range(data.m_Depth):
        name=cid+'.png' if layer==0 else cid+f'.slice{layer:02d}.png'
        path=output/'textures'/name
        cache=getattr(resolver,'exported_textures',set())
        if str(path) not in cache:
            start=layer*stride
            image=parse_image_data(raw[start:start+mips[0]],data.m_Width,data.m_Height,12,
                                  obj.version,obj.platform,None,True)
            if image is None:raise HardExtractError('Source terrain array decode failed')
            image.convert('RGBA').save(path)
            cache.add(str(path))
        slices.append('textures/'+name)
    record={'file':slices[0],'array_slices':slices,'source_name':data.m_Name,
            'width':data.m_Width,'height':data.m_Height,'depth':data.m_Depth,'dimension':'Tex2DArray',
            'format_src':'GraphicsFormat.RGBA_DXT5_UNorm','color_space':'sRGB' if role=='diffuse' else 'Linear',
            'is_normal_map':False,'generated_terrain_array':True,'terrain_role':role,
            'source_mip_count':data.m_MipCount,'source_color_space':data.m_ColorSpace,
            'source_file':source_name(obj),'source_path_id':obj.path_id}
    return cid,record


def export_control(obj, output, resolver=None):
    data=obj.read();cid=generated_id(obj,'control');path=output/'textures'/(cid+'.png')
    cache=getattr(resolver,'exported_textures',set())
    if str(path) not in cache:
        image=data.image
        if image is None:raise HardExtractError('Source terrain control decode failed')
        image.convert('RGBA').save(path)
        cache.add(str(path))
    return cid,{'file':'textures/'+path.name,'source_name':data.m_Name,
                'width':data.m_Width,'height':data.m_Height,'dimension':'Tex2D',
                'format_src':str(data.m_TextureFormat),'color_space':'Linear','is_normal_map':False,
                'generated_terrain_control':True,'source_file':source_name(obj),'source_path_id':obj.path_id}


def write_surface_tile(surface, resolver, data_obj, sample_rect, n, scale, output):
    """One tiled material per TerrainData, shared by every full-resolution chunk."""
    if '_exported' in surface:return surface['_exported']
    records={'materials':{},'textures':{}}
    diffuse_id,diffuse=export_array(resolver,surface['diffuse'],output,'diffuse')
    normal_id,normal=export_array(resolver,surface['normal'],output,'normalSAO')
    records['textures'].update({diffuse_id:diffuse,normal_id:normal})
    tex_ids={'_DiffuseArray':diffuse_id,'_NormalSAOArray':normal_id}
    for i,obj in enumerate(surface['controls']):
        cid,record=export_control(obj,output,resolver);records['textures'][cid]=record;tex_ids['_Control'+str(i)]=cid
        if record['width']!=surface['resolution'] or record['height']!=surface['resolution']:
            raise HardExtractError('Terrain control texture resolution disagrees with TerrainData')
    colors={'_UVScale':surface['uv_scale']}
    for i,row in enumerate(surface['layer_uv']):colors['_LayerUV'+str(i)]=row
    for i,row in enumerate(surface['layer_adjust']):colors['_LayerAdjust'+str(i)]=row
    material={'source_name':data_obj.read_typetree()['m_Name']+'_PortGround','shader':'ArenaMapPort/Terrain',
        'preserve_shader':True,'render_queue':1900,'keywords':[],
        'tex_envs':{name:{'texture_id':cid,'scale':[1,1],'offset':[0,0]} for name,cid in tex_ids.items()},
        'floats':{'_Contrast':surface['contrast'],'_LayerCount':12,'_ControlResolution':surface['resolution']},
        'colors':colors}
    cid=generated_id(data_obj,'material');path=output/'materials'/(cid+'.json')
    path.write_text(json.dumps(material,indent=2,sort_keys=True,allow_nan=False),encoding='utf-8')
    records['materials'][cid]={'file':'materials/'+path.name,'source_name':material['source_name'],
        'shader':material['shader'],'preserve_shader':True}
    surface['_exported']=(cid,records)
    return cid,records


def component_records(mesh_id: str, material_id: str, terrain: dict, collider: dict | None):
    records = [
        {'type':'MeshFilter','mesh_id':mesh_id},
        {'type':'MeshRenderer','material_ids':[material_id],
         'cast_shadows':CAST_SHADOWS.get(terrain.get('m_ShadowCastingMode',1),'On'),
         'receive_shadows':True,'light_probe_usage':'BlendProbes',
         'reflection_probe_usage':reflection_probe_usage(terrain.get('m_ReflectionProbeUsage',0)),
         'enabled':bool(terrain.get('m_Enabled',1)) and bool(terrain.get('m_DrawHeightmap',True))},
    ]
    if collider is not None:
        if collider.get('m_Material',{}).get('m_PathID',0):
            raise HardExtractError('Non-null Terrain physics material needs an explicit conversion')
        records.append({'type':'MeshCollider','mesh_id':mesh_id,'convex':False,
                        'is_trigger':False,'physic_material':None,'enabled':bool(collider.get('m_Enabled',1))})
    return records


def child_object(original: dict, terrain_obj, tile: dict, components: list[dict]):
    rect=tile['sample_rect']
    stable=int(generated_id(terrain_obj,'object:'+':'.join(map(str,rect))),16) & ((1<<63)-1)
    return {'id':str(-stable-1),'name':original['name']+f'_Ground_{rect[0]}_{rect[1]}',
            'active':True,'layer':original['layer'],'tag':original['tag'],'parent_id':original['id'],
            'local_position':[0,0,0],'local_rotation':[0,0,0,1],'local_scale':[1,1,1],
            'components':components}


def convert_terrains(env, source, scene_records: list[dict], output) -> dict:
    """Append full-resolution generated ground to emitted scenes and asset tables.

    Call after the ordinary engine's mesh/material/texture writers. Returned
    asset dictionaries merge into the normal manifest; provenance is added to
    its source conversion record. Source files are read-only.
    """
    output=Path(output);resolver=SourceResolver(env,source)
    result={'meshes':{},'materials':{},'textures':{},'provenance':[]}
    for folder in ('meshes','materials','textures'):(output/folder).mkdir(parents=True,exist_ok=True)
    for scene in scene_records:
        f=resolver.file(f"level{scene['index']}")
        objects=list(f.objects.values())
        terrain_objects=[x for x in objects if x.type.name=='Terrain']
        terrain_colliders={}
        for obj in objects:
            if obj.type.name=='TerrainCollider':
                tt=obj.read_typetree();go=tt['m_GameObject']['m_PathID']
                if go in terrain_colliders:raise HardExtractError('Multiple TerrainColliders on one source object')
                terrain_colliders[go]=(obj,tt)
        if not terrain_objects and not terrain_colliders:continue
        path=output/'scenes'/(scene['name']+'.json')
        scene_json=json.loads(path.read_text(encoding='utf-8'))
        by_id={x['id']:x for x in scene_json['objects']}
        existing_ids=set(by_id)
        handled=set()
        for terrain_obj in terrain_objects:
            tt=terrain_obj.read_typetree();go=tt['m_GameObject']['m_PathID']
            if go in handled:raise HardExtractError('Multiple Terrain components on one source object')
            handled.add(go)
            original=by_id.get(str(go))
            if original is None:raise HardExtractError('Required Terrain GameObject missing from emitted scene')
            collider_entry=terrain_colliders.get(go);collider=collider_entry[1] if collider_entry else None
            if collider and collider['m_TerrainData']!=tt['m_TerrainData']:
                raise HardExtractError('Terrain renderer/collider reference different data')
            data_obj=resolver.pointer(terrain_obj,tt['m_TerrainData'],'TerrainData')
            if data_obj is None:raise HardExtractError('Required TerrainData is null')
            data=data_obj.read_typetree();validate_required_features(tt,collider,data)
            heights,solid,scale=decode_heightmap(data['m_Heightmap'])
            surface=prepare_surface(resolver,terrain_obj,data_obj,data)
            n=heights.shape[0];meshes=[];triangle_count=0
            for z in range(0,n-1,TILE_CELLS):
                for x in range(0,n-1,TILE_CELLS):
                    tile=mesh_tile(heights,solid,scale,x,z)
                    if not len(tile['indices']):continue
                    mesh_id=generated_id(data_obj,f'mesh:{x}:{z}')
                    mesh_path=output/'meshes'/(mesh_id+'.mesh.bin')
                    info=write_tile_mesh(mesh_path,tile)
                    result['meshes'][mesh_id]={'file':'meshes/'+mesh_path.name,
                        'source_object_name':data.get('m_Name','Terrain')+f'_{x}_{z}',
                        'vertex_count':info['vertex_count'],'submesh_count':1}
                    material_id,surface_records=write_surface_tile(surface,resolver,data_obj,tile['sample_rect'],n,scale,output)
                    for table in ('materials','textures'):result[table].update(surface_records[table])
                    child=child_object(original,terrain_obj,tile,component_records(mesh_id,material_id,tt,collider))
                    if child['id'] in existing_ids:raise HardExtractError('Generated terrain object ID collision')
                    existing_ids.add(child['id']);scene_json['objects'].append(child)
                    meshes.append(mesh_id);triangle_count+=info['triangle_count']
            if triangle_count!=int(solid.sum())*2:raise HardExtractError('Terrain solid cell coverage mismatch')
            result['provenance'].append({'scene':scene['name'],'scene_index':scene['index'],
                'source_gameobject_id':go,'source_terrain_id':terrain_obj.path_id,
                'source_collider_id':collider_entry[0].path_id if collider_entry else None,
                'terrain_data_file':source_name(data_obj),'terrain_data_path_id':data_obj.path_id,
                'terrain_name':data.get('m_Name'),'height_resolution':n,'height_sample_count':n*n,
                'height_denominator':HEIGHT_DENOMINATOR,'sample_scale':list(scale),
                'height_min_max':[float(heights.min()),float(heights.max())],
                'hole_cell_count':int(solid.size-solid.sum()),'triangle_count':triangle_count,
                'mesh_ids':meshes,'renderer_enabled':bool(tt.get('m_Enabled',1)) and bool(tt.get('m_DrawHeightmap',True)),
                'collider_enabled':bool(collider.get('m_Enabled',1)) if collider else None,
                'source_native_foliage_enabled':bool(tt.get('m_DrawTreesAndFoliage',True)),
                'source_native_tree_count':len(data.get('m_DetailDatabase',{}).get('m_TreeInstances',[])),
                'runtime_foliage_status':'GPUInstancer detail placement caches are not converted by this ground/surface module; see source foliage probe.',
                'surface':surface['provenance']})
            del data,heights,solid,surface
        if set(terrain_colliders)-handled:raise HardExtractError('Unpaired required TerrainCollider cannot be silently omitted')
        scene_json['objects'].sort(key=lambda x:int(x['id']))
        path.write_text(json.dumps(scene_json,indent=2,sort_keys=True,ensure_ascii=False),encoding='utf-8')
    return result
