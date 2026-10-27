#!/usr/bin/env python3
"""The original decorative AirPit crowd, from the owner's Arena installation. No bots or game scripts.

The ignored payload contains original meshes, skin weights, materials, and sampled Transform
animation. Animator root-motion channels are deliberately not applied: the source Animator
also has ApplyRootMotion=false. Meshes, materials and clips are shared; each original placement keeps its own skeleton.
"""
from __future__ import annotations

import argparse
import gzip
import json
import math
from pathlib import Path
import zlib

from extract_door_poses import build_clip

REPO = Path(__file__).resolve().parents[2]
DEFAULT_SOURCE = Path('D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data')
DEFAULT_PACKAGE = REPO / 'work/extract/spectator-airpit'
PROTOTYPE_ID = 5434
CLIPS = ('ArenaFan_Idle_000_loop', 'ArenaFan_Idle_Cheer_001')
# One optional target absent from every fan and every Transform in source level40.
# No renderer bone references it. The three P/R/S curves are retained as provenance below.
ABSENT_OPTIONAL_PATH = 1390682545

# These scene renderers have a null mesh in Arena's level40. Resolve the corresponding original
# equipment/head mesh in the owner's current character bundles, preserving the scene placement.
# A missing/renamed bundle is an extraction failure, never an invisible spectator part.
RECOVERED_PARTS = {
    'wildman_head_1': ('characters/character/prefabs/wild_head_1.bundle', 'wildman_head_1_lod1_base'),
    'wildman_head_2_lod1': ('characters/character/prefabs/wild_head_2.bundle', 'wildman_head_2_lod1_base'),
    'USEC_head_1_lod1': ('characters/character/prefabs/usec_head_1.bundle', 'USEC_head_1_lod1_base'),
    'USEC_head_4_lod1': ('characters/character/prefabs/usec_head_4.bundle', 'USEC_head_4_lod1_base'),
    'Wild_head_drozd_lod1': ('characters/character/prefabs/wild_head_drozd.bundle', 'Wild_head_drozd_lod1_base'),
    'Wild_head_Misha_lod1': ('characters/character/prefabs/wild_head_misha.bundle', 'Wild_head_Misha_lod1_base'),
    'Bear_Head_Slava_lod1': ('characters/character/prefabs/bear_head_slava.bundle', 'Bear_Head_Slava_lod1_base'),
    'Bear_Head_5_LOD0': ('characters/character/prefabs/bear_head_5.bundle', 'Bear_Head_5_LOD0_base'),
    'Bear_Head_5_LOD1': ('characters/character/prefabs/bear_head_5.bundle', 'Bear_Head_5_LOD1_base'),
    'Cultist_Head_lod1': ('characters/character/prefabs/head_cultist_01.bundle', 'Cultist_Head_lod1_base'),
    'Top_wild_vest1_base_lod1': ('characters/character/prefabs/wild_body_3.bundle', 'Top_wild_vest2_base_lod1'),
    'hats_cowboy_LOD1': ('items/equipment/head_cowboy/item_equipment_head_cowboy.bundle', 'item_equipment_head_cowboy_custom_LOD1'),
    'head_kotton_LOD1': ('items/equipment/head_kotton/item_equipment_head_kotton.bundle', 'item_equipment_head_kotton_LOD1_base'),
    'head_leather_cap_LOD1': ('items/equipment/head_wild_security_leathercap/item_equipment_head_wild_security_leathercap.bundle', 'item_equipment_head_security_LeatherCap_lod1_base'),
    # The legacy lower-face cloth mesh is absent from the current installation. Arena's current
    # half-mask keeps the same face coverage/bones; this is a documented visual substitution.
    'mask_0_lod1': ('items/equipment/facecover_shroud/item_equipment_facecover_shroud.bundle', 'item_equipment_Half_Mask_shroud_lod1_base'),
}


def euler_quaternion(degrees, order):
    """Mecanim Euler binding.customType is Unity RotationOrder (0=XYZ, NOT default ZXY).

    AssetRipper AnimationClipConverter.AddTransformCurve preserves this field, and UnityCsReference
    Transform.bindings.cs enumerates XYZ,XZY,YZX,YXZ,ZXY,ZYX. Convert before sampling playback so
    quaternion interpolation never takes an Euler wrap-around detour.
    """
    if order not in range(6):
        raise ValueError('unknown Euler rotation order')
    result = [0., 0., 0., 1.]
    for axis in ((0, 1, 2), (0, 2, 1), (1, 2, 0), (1, 0, 2), (2, 0, 1), (2, 1, 0))[order]:
        half = math.radians(degrees[axis]) / 2
        rotation = [0., 0., 0., math.cos(half)]
        rotation[axis] = math.sin(half)
        x, y, z, w = rotation
        a, b, c, d = result
        result = [w*a+x*d+y*c-z*b, w*b-x*c+y*d+z*a, w*c+x*b-y*a+z*d, w*d-x*a-y*b-z*c]
    return result


def vec(value, names='xyz'):
    return [float(getattr(value, n)) for n in names]


def finite(values):
    if not all(math.isfinite(float(v)) for v in values):
        raise ValueError('non-finite data')


def validate_skin(mesh, bone_count):
    n = len(mesh['vertices'])
    if not n or len(mesh['weights']) != n or len(mesh['indices']) != n:
        raise ValueError('skin vertex count mismatch')
    if len(mesh['bindposes']) != bone_count or not bone_count:
        raise ValueError('bind pose count mismatch')
    for weights, indices in zip(mesh['weights'], mesh['indices']):
        finite(weights)
        if len(weights) != 4 or len(indices) != 4 or any(w < 0 for w in weights) or abs(sum(weights) - 1) > .002:
            raise ValueError('invalid skin weights')
        if any(i < 0 or i >= bone_count for i in indices):
            raise ValueError('bone index out of range')
    for pose in mesh['bindposes']:
        finite(pose)
        if len(pose) != 16:
            raise ValueError('invalid bind pose')


def skin_channels(weights, indices, bindpose_count):
    # Unity stores single-influence vertices with only the blend-index channel; their weight is 1.
    if weights is None:
        if not indices or any(len(i) != 1 or not 0 <= i[0] < bindpose_count for i in indices):
            raise ValueError('missing non-rigid skin weights')
        weights = [[1.] for _ in indices]
    if indices is None or len(weights) != len(indices):
        raise ValueError('skin channel length mismatch')
    if any(not 1 <= len(w) <= 4 or len(w) != len(i) for w, i in zip(weights, indices)):
        raise ValueError('unsupported skin channel width')
    return ([list(w) + [0.] * (4 - len(w)) for w in weights],
            [list(i) + [0] * (4 - len(i)) for i in indices])


def validate(data):
    if data['schema'] != 2 or data['map'] != 'arena_airpit':
        raise ValueError('wrong schema/map')
    actors = data['actors']
    if not 1 <= len(actors) <= 200 or len(actors) != data['source_count']:
        raise ValueError('source crowd count mismatch')
    if len({a['source_id'] for a in actors}) != len(actors):
        raise ValueError('duplicate spectator placement')
    if len(data['clips']) != 2:
        raise ValueError('idle and cheer required')
    for clip in data['clips']:
        if not 0 < clip['duration'] < 60 or clip['frames'] < 2:
            raise ValueError('invalid clip duration')
        seen = set()
        for track in clip['tracks']:
            key = (track['path_hash'], track['kind'])
            if key in seen or key[1] not in (1, 2, 3):
                raise ValueError('invalid/duplicate track')
            seen.add(key)
            width = 4 if key[1] == 2 else 3
            if len(track['values']) != clip['frames'] * width:
                raise ValueError('animation sample count mismatch')
            finite(track['values'])
            if width == 4 and any(abs(sum(x*x for x in track['values'][i:i+4]) - 1) > .002
                                  for i in range(0, len(track['values']), 4)):
                raise ValueError('invalid animation quaternion')
        if not seen:
            raise ValueError('empty animation')
    bindings = [[(t['path_hash'], t['kind']) for t in c['tracks']] for c in data['clips']]
    if bindings[0] != bindings[1]:
        raise ValueError('inconsistent clip bindings/order')
    if [c['name'] for c in data['clips']] != list(CLIPS):
        raise ValueError('wrong idle/cheer identity')
    for mesh in data['meshes'].values():
        for key, width in [('vertices', 3), ('normals', 3), ('tangents', 4), ('uv', 2)]:
            if not len(mesh['vertices']) or len(mesh[key]) != len(mesh['vertices']):
                raise ValueError('mesh attribute count mismatch')
            for v in mesh[key]:
                finite(v)
                if len(v) != width:
                    raise ValueError('mesh attribute width mismatch')
        if any(i < 0 or i >= len(mesh['vertices']) for sm in mesh['triangles'] for i in sm):
            raise ValueError('triangle outside mesh')
        if any(len(sm) % 3 for sm in mesh['triangles']):
            raise ValueError('non-triangle mesh')
    used_meshes, used_materials = set(), set()
    for actor in actors:
        nodes = actor['nodes']
        if not 1 <= len(nodes) <= 400:
            raise ValueError('invalid hierarchy size')
        if actor.get('missing'):
            raise ValueError(f'unresolved spectator meshes: {actor["name"]}')
        if not actor['parts']:
            raise ValueError('empty spectator appearance')
        hashes = {n['path_hash'] for n in nodes}
        if len(hashes) != len(nodes):
            raise ValueError('duplicate animation path/hash')
        paths = []
        for i, node in enumerate(nodes):
            if not (-1 <= node['parent'] < i) or (node['parent'] == -1) != (i == 0):
                raise ValueError('invalid hierarchy order')
            for prop, count in [('position', 3), ('rotation', 4), ('scale', 3)]:
                finite(node[prop])
                if len(node[prop]) != count:
                    raise ValueError('invalid transform')
            path = '' if i == 0 else '/'.join(p for p in [paths[node['parent']], node['name']] if p)
            paths.append(path)
            if zlib.crc32(path.encode()) != node['path_hash']:
                raise ValueError('node path hash mismatch')
            if abs(sum(v*v for v in node['rotation']) - 1) > .002:
                raise ValueError('invalid node quaternion')
        for clip in data['clips']:
            if any(t['path_hash'] not in hashes for t in clip['tracks']):
                raise ValueError('animation track outside hierarchy')
        for part in actor['parts']:
            if not 0 <= part['node'] < len(nodes) or not 0 <= part['root'] < len(nodes):
                raise ValueError('invalid renderer node')
            if part['mesh_id'] not in data['meshes'] or any(m not in data['materials'] for m in part['materials']):
                raise ValueError('missing shared mesh/material')
            mesh = data['meshes'][part['mesh_id']]
            used_meshes.add(part['mesh_id'])
            used_materials.update(part['materials'])
            if len(part['materials']) != len(mesh['triangles']):
                raise ValueError('material/submesh mismatch')
            if bool(part['bones']) != bool(mesh.get('bindposes')):
                raise ValueError('skin binding missing or unexpected')
            if part['bones']:
                if any(not 0 <= i < len(nodes) for i in part['bones']):
                    raise ValueError('bone outside hierarchy')
                validate_skin(mesh, len(part['bones']))
                if [nodes[i]['path_hash'] for i in part['bones']] != mesh['bone_hashes']:
                    raise ValueError('skin bone path order mismatch')
    if used_meshes != set(data['meshes']) or used_materials != set(data['materials']):
        raise ValueError('unreferenced shared mesh/material')
    used_textures = {te['id'] for m in data['materials'].values() for te in m['textures'].values()}
    if used_textures != set(data['textures']):
        raise ValueError('unresolved or unreferenced material texture')


def extract(source, output):
    import UnityPy
    from UnityPy.helpers.MeshHelper import MeshHandler
    from common_ids import content_id
    from extract_equator import resolve_shader_name, _decode_cubemap_faces

    env = UnityPy.load(str(source / 'level40'), str(source / 'sharedassets40.assets'))
    scene = next(f for k, f in env.files.items() if Path(k).name == 'level40')
    output.mkdir(parents=True, exist_ok=True)
    materials, textures = {}, {}
    recovery_cache = {}
    def recover(name):
        if name in recovery_cache:
            return recovery_cache[name]
        bundle, mesh_name = RECOVERED_PARTS[name]
        content = source / 'StreamingAssets/Windows/assets/content'
        paths = [str(content / bundle)]
        if name in ('mask_0_lod1', 'head_leather_cap_LOD1'):
            paths.extend(str(content.parent.parent / p) for p in ('shaders', 'cubemaps'))
        if name == 'mask_0_lod1':
            paths.append(str(content / 'facecover_shroud.bundle'))
        benv = UnityPy.load(*paths)
        matches = [r for r in benv.objects if r.type.name == 'Mesh' and r.read().m_Name == mesh_name]
        if len(matches) != 1:
            raise ValueError(f'expected one recovery mesh {name}: {bundle}/{mesh_name}')
        replacement_materials = None
        if name in ('mask_0_lod1', 'head_leather_cap_LOD1'):
            renderers = []
            for r in benv.objects:
                if r.type.name == 'SkinnedMeshRenderer' and r.read().m_Mesh.path_id == matches[0].path_id:
                    renderers.append(r.read())
                elif r.type.name == 'MeshFilter' and r.read().m_Mesh.path_id == matches[0].path_id:
                    go = r.read().m_GameObject.deref().read()
                    renderers.extend(c.component.deref().read() for c in go.m_Component
                                     if c.component.deref().type.name == 'MeshRenderer')
            if len(renderers) != 1:
                raise ValueError(f'expected one current replacement renderer: {name}')
            replacement_materials = renderers[0].m_Materials
        recovery_cache[name] = (matches[0], replacement_materials)
        return recovery_cache[name]
    def texture(ptr):
        reader = ptr.deref()
        cid = content_id(Path(reader.assets_file.name).name, reader.path_id, reader.type.name)
        if cid in textures:
            return cid
        tex, tt = reader.read(), reader.read_typetree()
        row = dict(name=tex.m_Name, linear=tt['m_ColorSpace'] == 0,
                   wrap=int(tt['m_TextureSettings']['m_WrapU']), filter=int(tt['m_TextureSettings']['m_FilterMode']))
        if reader.type.name == 'Cubemap':
            imgs = _decode_cubemap_faces(reader, tex, tt, tt['m_Width'], tt['m_Height'])
            row['files'] = [f'{cid}-{i}.png' for i in range(6)]
        elif reader.type.name == 'Texture2D':
            imgs, row['files'] = [tex.image], [f'{cid}.png']
        else:
            raise ValueError(f'unsupported texture {reader.type.name}')
        for img, file in zip(imgs, row['files']):
            img.convert('RGBA').save(output / file)
        textures[cid] = row
        return cid

    def material(ptr):
        reader = ptr.deref()
        cid = content_id(Path(reader.assets_file.name).name, reader.path_id, 'Material')
        if cid in materials:
            return cid
        mat, tt = reader.read(), reader.read_typetree()
        sp = tt['m_SavedProperties']
        row = dict(name=mat.m_Name, shader=resolve_shader_name(mat), queue=tt['m_CustomRenderQueue'],
                   keywords=sorted(set(tt.get('m_ValidKeywords', []) + tt.get('m_ShaderKeywords', '').split())),
                   floats=dict(sp['m_Floats']), colors={k: [v[c] for c in 'rgba'] for k, v in sp['m_Colors']}, textures={})
        if not row['shader']:
            raise ValueError('unresolved shader')
        for prop, te in mat.m_SavedProperties.m_TexEnvs:
            if te.m_Texture.path_id:
                row['textures'][prop] = dict(id=texture(te.m_Texture), scale=vec(te.m_Scale, 'xy'), offset=vec(te.m_Offset, 'xy'))
        materials[cid] = row
        return cid

    def actor_data(game_object):
        root = next(c.component.deref().read() for c in game_object.m_Component
                    if c.component.deref().type.name == 'Transform')
        animator = next(c.component.deref().read() for c in game_object.m_Component
                        if c.component.deref().type.name == 'Animator')
        if animator.m_ApplyRootMotion or animator.m_Avatar.path_id:
            raise ValueError('prototype expects a generic in-place Animator')

        nodes, transforms, gos, paths, lookup = [], [], [], [], {}
        def walk(tf, parent, path):
            go = tf.m_GameObject.deref().read()
            idx = len(nodes)
            lookup[tf.object_reader.path_id] = idx
            nodes.append(dict(name=go.m_Name, path_hash=zlib.crc32(path.encode()), parent=parent, position=vec(tf.m_LocalPosition),
                              rotation=vec(tf.m_LocalRotation, 'xyzw'), scale=vec(tf.m_LocalScale)))
            transforms.append(tf)
            gos.append(go)
            paths.append(path)
            for child in tf.m_Children:
                ct = child.deref().read()
                name = ct.m_GameObject.deref().read().m_Name
                walk(ct, idx, path + '/' + name if path else name)
        walk(root, -1, '')
        hashes = {}
        for i, path in enumerate(paths):
            key = zlib.crc32(path.encode())
            if key in hashes:
                raise ValueError('duplicate animation path/hash')
            hashes[key] = i

        # Keep only the first LOD of every accessory; source enables all LOD renderers simultaneously.
        excluded = set()
        for go in gos:
            for comp in go.m_Component:
                r = comp.component.deref()
                if r.type.name == 'LODGroup':
                    for lod in r.read().m_LODs[1:]:
                        excluded.update(x.renderer.path_id for x in lod.renderers)
        def active(index):
            while index >= 0:
                if not gos[index].m_IsActive:
                    return False
                index = nodes[index]['parent']
            return True

        parts, missing = [], []
        for index, go in enumerate(gos):
            if not active(index):
                continue
            for comp in go.m_Component:
                reader = comp.component.deref()
                if reader.type.name not in ('SkinnedMeshRenderer', 'MeshRenderer') or reader.path_id in excluded:
                    continue
                renderer = reader.read()
                if not renderer.m_Enabled:
                    continue
                if reader.type.name == 'SkinnedMeshRenderer':
                    meshptr = renderer.m_Mesh
                    bones = [lookup[b.path_id] for b in renderer.m_Bones]
                else:
                    filters = [c.component.deref().read() for c in go.m_Component if c.component.deref().type.name == 'MeshFilter']
                    if not filters:
                        continue
                    meshptr, bones = filters[0].m_Mesh, []
                replacement_materials = None
                if not meshptr.path_id:
                    mr, replacement_materials = recover(go.m_Name)
                else:
                    mr = meshptr.deref()
                mesh_id = content_id(Path(mr.assets_file.name).name, mr.path_id, 'Mesh')
                mt, m = mr.read_typetree(), mr.read()
                if reader.type.name == 'SkinnedMeshRenderer':
                    # Serialized renderer arrays include unused or obsolete bones. Mesh hashes are
                    # the exact bindpose order, and also resolve recovered heads' additional bones.
                    bones = [hashes[h] for h in mt['m_BoneNameHashes']]
                if mesh_id not in meshes:
                    handler = MeshHandler(m)
                    handler.process()
                    if any(sm['topology'] != 0 for sm in mt['m_SubMeshes']):
                        raise ValueError('only triangle meshes supported')
                    mesh = dict(name=m.m_Name, vertices=handler.m_Vertices, normals=handler.m_Normals,
                                tangents=handler.m_Tangents, uv=handler.m_UV0, triangles=[])
                    stride = 2 if mt['m_IndexFormat'] == 0 else 4
                    for sm in mt['m_SubMeshes']:
                        start = sm['firstByte'] // stride
                        mesh['triangles'].append([int(x) + sm.get('baseVertex', 0)
                                                  for x in handler.m_IndexBuffer[start:start + sm['indexCount']]])
                    if bones:
                        try:
                            weights, indices = skin_channels(handler.m_BoneWeights, handler.m_BoneIndices, len(bones))
                        except ValueError as exc:
                            raise ValueError(f'{m.m_Name}: {exc}; bones={len(bones)}, '
                                             f'first indices={(handler.m_BoneIndices or [])[:2]}') from exc
                        mesh.update(weights=weights, indices=indices,
                                    bone_hashes=mt['m_BoneNameHashes'],
                                    bindposes=[[p[f'e{r}{c}'] for r in range(4) for c in range(4)] for p in mt['m_BindPose']])
                    meshes[mesh_id] = mesh
                parts.append(dict(node=index, bones=bones, root=lookup.get(getattr(renderer, 'm_RootBone', None).path_id, 0) if bones else 0,
                                  source_renderer_id=reader.path_id,
                                  recovered_from=RECOVERED_PARTS[go.m_Name][0] if not meshptr.path_id else None,
                                  mesh_id=mesh_id, materials=[material(mp) for mp in (replacement_materials or renderer.m_Materials)]))

        return dict(source_id=game_object.object_reader.path_id, name=game_object.m_Name,
                    nodes=nodes, parts=parts, missing=missing)

    # The original crowd container is disabled as a whole; all of its authored figures are restored.
    containers = [o.read() for o in scene.objects.values() if o.type.name == 'GameObject' and o.read().m_Name == 'SCAV_SPECTATORS']
    if len(containers) != 1:
        raise ValueError('expected one original SCAV_SPECTATORS container')
    container = next(c.component.deref().read() for c in containers[0].m_Component if c.component.deref().type.name == 'Transform')
    if vec(container.m_LocalPosition) != [0, 0, 0] or vec(container.m_LocalRotation, 'xyzw') != [0, 0, 0, 1] or vec(container.m_LocalScale) != [1, 1, 1]:
        raise ValueError('crowd container transform changed; review world-space placement')
    meshes = {}
    actors = [actor_data(c.deref().read().m_GameObject.deref().read()) for c in container.m_Children]
    all_hashes = {n['path_hash'] for actor in actors for n in actor['nodes']}
    for actor in actors:
        if actor['missing']:
            print('Missing source meshes:', actor['name'], actor['missing'])
    prototype = scene.objects[PROTOTYPE_ID].read()
    animator = next(c.component.deref().read() for c in prototype.m_Component if c.component.deref().type.name == 'Animator')
    clips = []
    refs = {p.path_id: p for p in animator.m_Controller.deref().read().m_AnimationClips}
    for name in CLIPS:
        matches = [p.deref() for p in refs.values() if p.deref().read().m_Name == name]
        if len(matches) != 1:
            raise ValueError(f'expected one clip {name}, found {len(matches)}')
        tt = matches[0].read_typetree()
        for binding in tt['m_ClipBindingConstant']['genericBindings']:
            if binding['typeID'] == 95 and binding['attribute'] in range(7, 14) and not binding['isPPtrCurve']:
                continue  # Animator root/motion scalars; root motion is off in the source
            if binding['typeID'] != 4 or binding['attribute'] not in (1, 2, 3, 4):
                raise ValueError(f'unsupported animation binding {binding}')
        decoded = build_clip(tt)
        euler_orders = {int(b['path']): int(b['customType']) for b in tt['m_ClipBindingConstant']['genericBindings']
                        if b['typeID'] == 4 and b['attribute'] == 4}
        frames = math.ceil(decoded.stop * 60) + 1
        tracks = []
        absent = []
        for channel in decoded.channels:
            if channel.path_hash not in all_hashes:
                if channel.path_hash == ABSENT_OPTIONAL_PATH and channel.kind in (1, 2, 3):
                    absent.append(dict(path_hash=channel.path_hash, kind=channel.kind))
                    continue
                raise ValueError(f'unresolved animation path {channel.path_hash} in {name}')
            samples = [channel.values_at(decoded.stop * frame / (frames - 1)) for frame in range(frames)]
            kind = channel.kind
            if kind == 4:
                samples = [euler_quaternion(v, euler_orders[channel.path_hash]) for v in samples]
                kind = 2
            if kind == 2:
                samples = [[v / math.sqrt(sum(x*x for x in q)) for v in q] for q in samples]
            tracks.append(dict(path_hash=channel.path_hash, kind=kind, values=[v for sample in samples for v in sample]))
        clips.append(dict(name=name, source_id=matches[0].path_id, duration=decoded.stop, frames=frames,
                          tracks=sorted(tracks, key=lambda t: (t['path_hash'], t['kind'])),
                          absent_optional_tracks=absent, root_motion=False))
    data = dict(schema=2, map='arena_airpit', source_scene='Arena_Airpit_DesignStuff',
                source_count=len(container.m_Children), actors=actors, meshes=meshes,
                materials=materials, textures=textures, clips=clips)
    validate(data)
    raw = json.dumps(data, separators=(',', ':'), allow_nan=False).encode()
    (output / 'spectator.json.gz').write_bytes(gzip.compress(raw, mtime=0))
    print(f'Extracted {len(actors)} original spectators, {len(meshes)} shared meshes, '
          f'{len(materials)} materials, {len(textures)} textures, {len(clips)} shared animations')



def verify(package):
    data = json.loads(gzip.decompress((package / 'spectator.json.gz').read_bytes()))
    validate(data)
    from PIL import Image
    expected = {'spectator.json.gz'}
    for texture in data['textures'].values():
        if len(texture['files']) not in (1, 6):
            raise ValueError('invalid texture dimension')
        for file in texture['files']:
            if Path(file).name != file:
                raise ValueError('invalid texture path')
            with Image.open(package / file) as image:
                image.verify()
            expected.add(file)
    if {p.name for p in package.iterdir()} != expected:
        raise ValueError('unreferenced or missing package file')
    print('SPECTATOR_PACKAGE_OK')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('extract', 'verify'))
    parser.add_argument('--source', type=Path, default=DEFAULT_SOURCE)
    parser.add_argument('--package', type=Path, default=DEFAULT_PACKAGE)
    args = parser.parse_args()
    if args.command == 'extract':
        extract(args.source, args.package)
    else:
        verify(args.package)
