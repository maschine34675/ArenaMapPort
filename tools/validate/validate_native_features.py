"""Independent source/package/bundle proof for Bay5 material hits and harbor audio.

No extractor/importer is used as an oracle. Source MonoBehaviour payloads are
re-encoded from the package and compared byte for byte; native built fields and
resolved references are checked separately. Legacy packages remain unchanged.
"""
from __future__ import annotations

from collections import Counter, defaultdict
import hashlib
import io
import json
import math
from pathlib import Path
import re
import struct
import wave

FIELDS = ('penetration_level', 'penetration_chance', 'ricochet_chance',
          'fragmentation_chance', 'trajectory_deviation_chance', 'trajectory_deviation')
TARGET_FIELDS = ('PenetrationLevel', 'PenetrationChance', 'RicochetChance',
                 'FragmentationChance', 'TrajectoryDeviationChance', 'TrajectoryDeviation')
MATERIALS = ('None Asphalt Body Cardboard Chainfence Concrete Fabric GarbageMetal GarbagePaper '
             'GenericSoft Glass GlassShattered Grate GrassHigh GrassLow Gravel MetalThin MetalThick '
             'Mud Pebbles Plastic Stone Soil SoilForest Tile Water WaterPuddle WoodThin WoodThick '
             'Tyre Rubber GenericHard BodyArmor Swamp Helmet GlassVisor HelmetRicochet MetalNoDecal Snow').split()
SCENES = {84:'Main',83:'Light',81:'DesignStuff',89:'TeamFight',88:'TeamFight_DesignStuff',
          80:'DEPOT',86:'WAREHOUSE',82:'HANGAR',79:'Background'}
PLAYERS = {'AmbientSoundPlayer', 'SeasonAmbientSoundPlayer', 'LoopAmbientSoundPlayer'}
SUPPORT = {'AmbientSoundPlayerGroup', 'BezierSpline', 'AmbientSplineEmitterController',
           'SoundAmbientZoneCalculator', 'AmbientPlayerSplineMappedEmitter'}
AUDIO_ROOT = 'ArenaMapPort_Bay5NativeAudio'
SCRIPT_NS = {**{k:'Audio.AmbientSubsystem' for k in PLAYERS | {'AmbientSoundPlayerGroup'}},
             **{k:'Audio.AmbientSubsystem.AmbientSplineEmitter' for k in SUPPORT - {'AmbientSoundPlayerGroup','BezierSpline'}},
             'BezierSpline':'BezierSplineTools', 'SoundBank':'EFT', 'BallisticCollider':'EFT.Ballistics'}


def require(condition, message):
    if not condition:
        raise ValueError('Native features: ' + message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def file_digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def child(root, relative):
    root = Path(root).resolve()
    path = (root / relative).resolve()
    require(not Path(relative).is_absolute() and path.is_relative_to(root), 'path leaves package: ' + str(relative))
    return path


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def compare(actual, expected, context):
    """Recursive exact structure, with float32 storage tolerance only."""
    if isinstance(expected, dict):
        require(isinstance(actual, dict) and actual.keys() == expected.keys(), context + ': dictionary keys differ')
        for key in expected:
            compare(actual[key], expected[key], context + '.' + str(key))
    elif isinstance(expected, (list, tuple)):
        require(isinstance(actual, (list, tuple)) and len(actual) == len(expected), context + ': array length differs')
        for index, (got, want) in enumerate(zip(actual, expected)):
            compare(got, want, f'{context}[{index}]')
    elif isinstance(expected, float):
        require(isinstance(actual, (float, int)) and math.isfinite(actual) and math.isfinite(expected)
                and math.isclose(actual, expected, rel_tol=1e-6, abs_tol=1e-7), context + ': float differs')
    else:
        require(actual == expected, f'{context}: {actual!r} != {expected!r}')


def compare_storage(actual,expected,context):
    """Serialized payload fields must retain their exact float32 representation."""
    compare(actual,expected,context)
    if isinstance(expected,float):
        require(struct.pack('<f',actual)==struct.pack('<f',expected),context+': float32 bits differ')
    elif isinstance(expected,dict):
        for key,value in expected.items():compare_storage(actual[key],value,context+'.'+str(key))
    elif isinstance(expected,(list,tuple)):
        for index,value in enumerate(expected):compare_storage(actual[index],value,f'{context}[{index}]')


def ptr(reader, value):
    from UnityPy.classes import PPtr
    return PPtr(m_FileID=int(value.get('m_FileID', value.get('fileId', 0))),
                m_PathID=int(value.get('m_PathID', value.get('pathId', 0))), assetsfile=reader.assets_file).deref()


def identity(reader):
    return f'{Path(reader.assets_file.name).name}:{reader.path_id}'


def object_key(reader):
    return id(reader.assets_file), reader.path_id


def script_identity(reader):
    header = reader.parse_monobehaviour_head()
    if not header.m_Script.path_id:
        return '', '', ''
    script = header.m_Script.deref().read()
    return script.m_ClassName, script.m_Namespace, script.m_AssemblyName


def require_script(reader, kind):
    compare(script_identity(reader), (kind, SCRIPT_NS[kind], 'Assembly-CSharp'), kind + ' MonoScript identity')


def scene_objects(package, manifest):
    return {name:read_json(child(package, 'scenes/' + name + '.json'))['objects'] for name in manifest['scenes']}


def feature_enabled(manifest, scenes=None):
    present = {k for k in ('native_features','native_ballistics','native_audio') if k in manifest}
    if not present:
        if scenes is not None:
            require(not any(c['type']=='BallisticCollider' for objects in scenes.values() for o in objects for c in o['components']),
                    'unmarked ballistic components')
        return False
    require(len(present)==3, 'incomplete required feature contract')
    compare(manifest['native_features'], {'schema_version':1,'required':['ballistics','harbor_audio']}, 'feature contract')
    require(set(manifest['scenes']) == {'Arena_Bay5_' + n for n in SCENES.values()}, 'native features require full Bay5 scene set')
    if scenes is not None:
        require(not any(c['type']=='WindowBreaker' for objects in scenes.values() for o in objects for c in o['components']), 'glass was deferred by the user')
    return True


def load_audio(package, manifest):
    meta = manifest['native_audio']
    require(meta['schema_version']==1 and meta['file']=='audio/native-audio.json', 'audio index schema/path')
    path = child(package, meta['file'])
    require(file_digest(path)==meta['sha256'], 'audio index hash differs')
    data = read_json(path)
    require(data['schemaVersion']==1 and data['mapKey']=='arena_bay5' and data['sourceScene']=='level188', 'audio source identity')
    require(Counter(x['kind'] for x in data['players'])==Counter({'AmbientSoundPlayer':17,'SeasonAmbientSoundPlayer':6,'LoopAmbientSoundPlayer':1}), 'audio player census')
    require(Counter(x['kind'] for x in data['support'])==Counter({'AmbientSoundPlayerGroup':2,'BezierSpline':1,'AmbientSplineEmitterController':1,'SoundAmbientZoneCalculator':1,'AmbientPlayerSplineMappedEmitter':1}), 'audio support census')
    for field, count in [('players',24),('banks',13),('clips',106)]:
        require(meta[field]==len(data[field])==count, 'audio count: ' + field)
    for records in (data['players'] + data['support'], data['clips']):
        require(len({x['id'] for x in records})==len(records), 'duplicate audio source identity')
    require(all(k==v['id'] for k,v in data['banks'].items()), 'bank key/identity disagreement')
    require(Counter(p['resolved'] for s in data['support'] for p in s.get('soundPlayers',[]))==Counter(p['id'] for p in data['players']), 'audio group coverage')
    return data


def extra_counts(package, manifest):
    if not feature_enabled(manifest):
        return {}
    data = load_audio(package, manifest)
    paths = set()
    for record in data['players'] + data['support']:
        parts = record['path'].split('/')
        paths.update('/'.join(parts[:i]) for i in range(1,len(parts)+1))
    return {'GameObject':len(paths)+1, 'Transform':len(paths)+1, 'AudioSource':len(data['players'])}


def package_chain(obj, objects):
    result, seen = [], set()
    while obj is not None:
        require(obj['id'] not in seen, 'package ancestry cycle')
        seen.add(obj['id'])
        result.append((obj['name'], obj['local_position'], obj['local_rotation'], obj['local_scale']))
        obj = objects.get(str(obj['parent_id']))
    return result


def package_states(obj,objects):
    result=[];seen=set()
    while obj is not None:
        require(obj['id'] not in seen,'package ancestry cycle');seen.add(obj['id'])
        result.append((bool(obj['active']),obj['layer']));obj=objects.get(str(obj['parent_id']))
    return result


def unity_go(reader):
    """Full owner chain and actual attached components; names alone are ambiguous."""
    go = reader if reader.type.name=='GameObject' else reader.parse_monobehaviour_head().m_GameObject.deref()
    first = go.read_typetree()
    components = [ptr(go, c['component']) for c in first['m_Component']]
    if reader.type.name!='GameObject':
        require(sum(object_key(c)==object_key(reader) for c in components)==1, 'component not attached exactly once')
    chain, states, seen = [], [], set()
    while go is not None:
        require(object_key(go) not in seen, 'source/bundle ancestry cycle')
        seen.add(object_key(go)); tree = go.read_typetree()
        transforms = [ptr(go,c['component']) for c in tree['m_Component'] if ptr(go,c['component']).type.name=='Transform']
        require(len(transforms)==1, 'owner transform cardinality')
        transform = transforms[0]; t = transform.read_typetree()
        chain.append((tree['m_Name'], list(t['m_LocalPosition'].values()), list(t['m_LocalRotation'].values()), list(t['m_LocalScale'].values())))
        states.append((bool(tree['m_IsActive']), int(tree['m_Layer'])))
        go = ptr(transform,t['m_Father']).read().m_GameObject.deref() if t['m_Father']['m_PathID'] else None
    return {'chain':chain,'states':states,'components':components,'active':bool(first['m_IsActive']),'layer':first['m_Layer']}


def find_presets(raw, evidence=None):
    """Independently decode the named HardSettings table, without fixed offsets."""
    found=[]
    for match in re.finditer(re.escape(b'\x04\0\0\0None'),raw):
        start=match.start()-4
        if start<0 or start%4: continue
        try:
            count=struct.unpack_from('<i',raw,start)[0]
            require(count==47,'preset census changed'); pos=start+4; records=[]
            for index in range(count):
                size=struct.unpack_from('<i',raw,pos)[0]; pos+=4
                require(1<=size<=64,'preset name length')
                name=raw[pos:pos+size].decode('ascii'); pos=(pos+size+3)&~3
                material,n=struct.unpack_from('<ii',raw,pos);pos+=8
                require(n==6,'preset fields');values=struct.unpack_from('<6f',raw,pos);pos+=24
                require(struct.unpack_from('<5i',raw,pos)==(0,0,2,2,4),'preset scale curve changed');pos+=20
                records.append({'name':name,'material':material,'values':values,'index':index})
            require(set(MATERIALS).issubset(r['name'] for r in records),'canonical presets missing')
            require(len({r['name'] for r in records})==count,'duplicate preset names')
            found.append((records,start,pos))
        except (ValueError,struct.error,UnicodeDecodeError): pass
    require(len(found)==1,'ambiguous/missing named ballistic presets')
    records,start,end=found[0]
    if evidence is not None:
        evidence.update(offset=start,byte_length=end-start,sha256=digest(raw[start:end]),
                        presets=[{'index':r['index'],'name':r['name'],'material_id':r['material'],
                                  'values':dict(zip(FIELDS,r['values'])),'depend_on_scale':False} for r in records])
    return records


def source_presets(root, evidence=None):
    import UnityPy
    env=UnityPy.load(str(Path(root)/'resources.assets'),str(Path(root)/'globalgamemanagers.assets'))
    hits=[]
    for obj in list(env.objects):
        if Path(obj.assets_file.name).name=='resources.assets' and obj.type.name=='MonoBehaviour' and script_identity(obj)[:2]==('EFTHardSettings',''):
            raw=obj.get_raw_data();proof={};hits.append(find_presets(raw,proof))
            proof.update(file='resources.assets',component_id=str(obj.path_id),object_sha256=digest(raw))
            if evidence is not None:evidence.update(proof)
    require(len(hits)==1,'HardSettings source cardinality')
    return hits[0]


def ballistic_expected(reader, source_table, target_table):
    raw=reader.get_raw_data()
    require(len(raw)>=32,'truncated ballistic header')
    owner_file,owner=struct.unpack_from('<iq',raw); enabled=raw[12]
    length=struct.unpack_from('<i',raw,28)[0]; offset=(32+length+3)&~3
    require(owner_file==0 and owner>0 and enabled in (0,1) and 0<=length<=len(raw)-32 and len(raw)==offset+44,'ballistic ABI changed')
    surface,preset,net,hit,material,*values=struct.unpack_from('<5i6f',raw,offset)
    names={p['material']:p['name'] for p in source_table if p['name'] in MATERIALS}
    require(material in names,'unmapped source material'); name=names[material]
    target={p['name']:p for p in target_table}
    require(preset==-900 or 0<=preset<len(source_table),'unknown preset index')
    preset_name=source_table[preset]['name'] if preset!=-900 else None
    require(all(math.isfinite(v) for v in values) and values[0]>=0 and all(0<=v<=1 for v in values[1:]),'ballistic value domain')
    return {'type':'BallisticCollider','schema_version':1,'enabled':bool(enabled),'material':name,
            'target_material_id':target[name]['material'],'preset_name':preset_name,
            'target_preset_id':target[preset_name]['index'] if preset_name else -900,
            'values':dict(zip(FIELDS,values)), 'source':{'file':Path(reader.assets_file.name).name,
            'component_id':str(reader.path_id),'object_id':str(owner),'raw_sha256':digest(raw),
            'preset_id':preset,'material_id':material,'surface_sound_id':surface,'hit_type_id':hit,'net_id':net}}


def validate_ballistic_source(package, manifest, scenes):
    import UnityPy
    root=Path(manifest['source']['arena_data_root']); target=Path(manifest['source']['target_data_root'])
    source_proof={};target_proof={}
    source_table=source_presets(root,source_proof); target_table=source_presets(target,target_proof)
    compare_storage(manifest['native_ballistics'],{'schema_version':1,'source_presets':source_proof,'target_presets':target_proof},'independent named preset provenance')
    for name in MATERIALS:
        require(next(p for p in target_table if p['name']==name)['material']==MATERIALS.index(name),'target named material mapping changed')
    total=0
    for index,suffix in SCENES.items():
        scene='Arena_Bay5_'+suffix
        objects={o['id']:o for o in scenes[scene]}
        actual={}
        for obj in objects.values():
            for c in obj['components']:
                if c['type']=='BallisticCollider':
                    key=c['source']['component_id']; require(key not in actual,'duplicate ballistic source component')
                    actual[key]=(obj,c)
        env=UnityPy.load(str(root/f'level{index}'),str(root/'globalgamemanagers.assets'))
        found=set()
        for reader in list(env.objects):
            if Path(reader.assets_file.name).name!=f'level{index}' or reader.type.name!='MonoBehaviour': continue
            if script_identity(reader)[:2]!=('BallisticCollider','EFT.Ballistics'): continue
            key=str(reader.path_id); found.add(key)
            require(key in actual,scene+': missing ballistic component '+key)
            owner,c=actual[key]; expected=ballistic_expected(reader,source_table,target_table)
            compare_storage(c,expected,scene+': ballistic '+key)
            require(owner['id']==expected['source']['object_id'],'ballistic source owner changed')
            native=unity_go(reader)
            compare(package_chain(owner,objects),native['chain'],'ballistic source hierarchy '+key)
            compare((owner['active'],owner['layer']),(native['active'],native['layer']),'ballistic source owner state')
            compare(package_states(owner,objects),native['states'],'ballistic source ancestor states')
            require(any(x['type'] in ('MeshCollider','BoxCollider','SphereCollider','CapsuleCollider') for x in owner['components']),'ballistic owner lacks imported physical collider')
        require(set(actual)==found,scene+': extra ballistic source identities');total+=len(found)
    require(total==6322,'Bay5 ballistic source census changed')


class Wire:
    """Independent encoder: every package field must reproduce the source ABI."""
    def __init__(self): self.data=bytearray()
    def number(self,fmt,value): self.data.extend(struct.pack('<'+fmt,value))
    def align(self): self.data.extend(b'\0'*(-len(self.data)%4))
    def boolean(self,value):
        require(type(value) is bool,'nonboolean audio field');self.number('B',int(value));self.align()
    def string(self,value):
        raw=value.encode('utf-8');self.number('i',len(raw));self.data.extend(raw);self.align()
    def pointer(self,value): self.number('i',value['fileId']);self.number('q',value['pathId'])
    def floats(self,value):
        for v in value:
            require(math.isfinite(v),'nonfinite audio value');self.number('f',v)
    def array(self,values,emit):
        self.number('i',len(values))
        for value in values: emit(value)
    def curve(self,value):
        self.array(value['keys'],lambda k:(self.floats([k[n] for n in ('time','value','inSlope','outSlope')]),self.number('i',k['weightedMode']),self.floats([k['inWeight'],k['outWeight']])))
        for key in ('preInfinity','postInfinity','rotationOrder'):self.number('i',value[key])


def audio_wire(item,kind):
    w=Wire();w.pointer(item['gameObject']);w.boolean(item['enabled']);w.pointer(item['script']);w.string(item['name'])
    if kind in PLAYERS:
        w.floats([item['volume'],item['deltaVolume']]);w.floats(item['randomPitchRange'])
        w.floats([item['fadeInDurationSec'],item['fadeOutDurationSec']]);w.pointer(item['mixerGroup'])
        w.boolean(item['forceSetMixerGroup']);w.boolean(item['playOnAwake'])
        w.floats([item[k] for k in ('spread','spatialBlend','minDistance','maxDistance')])
        w.curve(item['rolloffCurve']);w.floats(item['startPlayingDelay']);w.curve(item['spreadCurve']);w.boolean(item['useCustomSpreadCurve'])
        if kind=='LoopAmbientSoundPlayer':w.pointer(item['loopClip'])
        else:
            w.floats(item['randomTimeRange'])
            if kind=='AmbientSoundPlayer':w.pointer(item['ambientBank'])
            else:
                for field,emit in [('seasonAmbientBanks',w.pointer),('seasonRandomTimeRange',w.floats)]:
                    w.array(list(item[field]),w.string);w.array(list(item[field].values()),emit)
    elif kind=='SoundBank':
        w.floats([item[k] for k in ('customLength','baseVolume','basePitch','deltaVolume','deltaPitch','rolloff')])
        for key in ('ignoreOcclusion','physical','hasEnvironment'):w.boolean(item[key])
        w.number('i',item['sourceType']);w.boolean(item['allowLimitedPlay']);w.boolean(item['limitIfVisible'])
        w.floats(item['limitRadius']);w.floats([item['limitPercentOfClip'],item['clipLength']])
        w.array(item['environments'],lambda e:w.array(e,lambda d:w.array(d,w.pointer)))
        w.pointer(item['blendOptions']);w.pointer(item['indoorBlendOptions']);w.boolean(item['discreteDistancePick'])
    elif kind=='AmbientSoundPlayerGroup':w.pointer(item['outputMixerGroup']);w.array(item['soundPlayers'],w.pointer)
    elif kind=='BezierSpline':w.array(item['points'],w.floats);w.array(item['modes'],lambda v:w.number('i',v));w.boolean(item['loop'])
    elif kind=='SoundAmbientZoneCalculator':
        w.pointer(item['splineEmitter']);w.pointer(item['spline']);w.number('i',item['resolution']);w.floats([item['calculationRadius'],item['blendSpeed']])
    elif kind=='AmbientPlayerSplineMappedEmitter':w.pointer(item['soundPlayer']);w.floats(item['spreadRange'])
    else:require(kind=='AmbientSplineEmitterController','unsupported source audio type')
    return bytes(w.data)


def pointers(value):
    if isinstance(value,dict):
        if 'fileId' in value and 'pathId' in value:yield value
        else:
            for v in value.values():yield from pointers(v)
    elif isinstance(value,list):
        for v in value:yield from pointers(v)


def wave_data(data):
    with wave.open(io.BytesIO(data),'rb') as wav:
        require(wav.getcomptype()=='NONE' and wav.getsampwidth()==2,'audio must remain PCM16')
        return (wav.getnchannels(),wav.getframerate(),wav.getnframes()),wav.readframes(wav.getnframes())


def check_wave(package,clip):
    d=clip['decoded'];payload=child(package,d['file']).read_bytes()
    require(digest(payload)==d['sha256'] and len(payload)==d['bytes'],'clip package hash/size '+clip['id'])
    dimensions,pcm=wave_data(payload)
    compare(dimensions,(d['channels'],d['sampleRate'],d['frames']),'PCM dimensions '+clip['id'])
    require(d['sampleWidth']==2 and pcm and any(pcm),'empty/silent decoded audio '+clip['id'])
    return dimensions,pcm


def validate_audio_source(package,manifest,data):
    import UnityPy
    root=Path(manifest['source']['arena_data_root'])
    require(file_digest(root/'level188')==data['sourceSceneSha256'],'audio scene source bytes changed')
    schema_path=child(root,data['bankSchema']['path'])
    require(file_digest(schema_path)==data['bankSchema']['sha256'],'SoundBank type schema source changed')
    env=UnityPy.load(str(root/'level188'),str(root/'globalgamemanagers.assets'))
    scene=next(f for f in env.files.values() if hasattr(f,'objects') and Path(f.name).name=='level188')
    components={};counts=Counter()
    for obj in list(scene.objects.values()):
        if obj.type.name!='MonoBehaviour':continue
        kind,namespace,_=script_identity(obj);counts[kind]+=1
        if kind in PLAYERS|SUPPORT:
            require(namespace==SCRIPT_NS[kind],'source ambient namespace changed');components[identity(obj)]=obj
    # Census labels are short class names; selected classes also require namespaces.
    compare(dict(counts),data['componentCounts'],'source audio complete component census')
    items=data['players']+data['support']
    require(set(components)=={r['id'] for r in items},'source audio component set differs')
    reached_banks=set();reached_clips=set()
    for item in items:
        obj=components[item['id']]
        require(obj.get_raw_data()==audio_wire(item,item['kind']),'source audio payload '+item['id'])
        require(len(obj.get_raw_data())==item['byteSize'],'source audio byte size')
        go=unity_go(obj);chain=list(reversed(go['chain']))
        compare('/'.join(t[0] for t in chain),item['path'],'source audio hierarchy names')
        expected_chain=[{'position':dict(zip('xyz',t[1])),'rotation':dict(zip('xyzw',t[2])),'scale':dict(zip('xyz',t[3]))} for t in chain]
        compare(item['transformChain'],expected_chain,'source audio hierarchy transforms')
        require(item['activeInHierarchy'] and all(active and layer==0 for active,layer in go['states']),'unsupported inactive/nondefault audio hierarchy')
        for p in pointers(item):
            if 'resolved' in p:
                actual=ptr(obj,p);require(identity(actual)==p['resolved'],'source audio reference target '+item['id'])
                if actual.type.name=='AudioClip':reached_clips.add(identity(actual))
                elif p['resolved'] in data['banks']:reached_banks.add(p['resolved'])
        if item['kind'] in PLAYERS:
            sources=[x for x in go['components'] if x.type.name=='AudioSource'];require(len(sources)==1,'source player AudioSource cardinality')
            compare(sources[0].read_typetree(),item['sourceAudio'],'source AudioSource '+item['id'])
            require(item['mixerGroup']['pathId']==0 and item['playOnAwake'] is False,'source mixer/start policy changed')
        if item['kind']=='AmbientSoundPlayerGroup':
            compare(ptr(obj,item['outputMixerGroup']).read().m_Name,item['mixerName'],'source native mixer name')
    require(reached_banks==set(data['banks']),'unreachable/missing source banks')
    schemas=UnityPy.load(str(schema_path))
    schema=[o for o in schemas.objects if o.type.name=='MonoBehaviour' and script_identity(o)[:2]==('SoundBank','EFT')]
    require(len(schema)==1,'bank schema component cardinality');node=schema[0].serialized_type.node
    source_script_hash=schema[0].parse_monobehaviour_head().m_Script.deref().read().m_PropertiesHash
    def source_object(identifier):
        filename,path_id=identifier.rsplit(':',1)
        require(Path(filename).name==filename,'invalid source identity path')
        candidates=[f for f in env.files.values() if hasattr(f,'objects') and Path(f.name).name==filename]
        if not candidates:
            env.load_file(str(root/filename));candidates=[f for f in env.files.values() if hasattr(f,'objects') and Path(f.name).name==filename]
        require(candidates and all(int(path_id) in f.objects for f in candidates),'missing source audio object '+identifier)
        # UnityPy may load one external under both its absolute and dependency
        # keys. Accept those aliases only when the requested original bytes agree.
        matches=[f.objects[int(path_id)] for f in candidates]
        require(len({digest(r.get_raw_data()) for r in matches})==1,'conflicting source file aliases '+identifier)
        return matches[0]
    for key,item in data['banks'].items():
        obj=source_object(key)
        require(script_identity(obj)[:2]==('SoundBank','EFT'),'source bank script')
        require(obj.get_raw_data()==audio_wire(item,'SoundBank'),'source bank payload '+key)
        compare(obj.parse_monobehaviour_head().m_Script.deref().read().m_PropertiesHash,source_script_hash,'source SoundBank schema hash')
        compare(obj.read_typetree(nodes=node,check_read=True),item['sourceTree'],'independent source bank typetree '+key)
        for environment in item['environments']:
            for distance in environment:
                for p in distance:
                    actual=ptr(obj,p);require(actual.type.name=='AudioClip' and identity(actual)==p['resolved'],'source bank clip link');reached_clips.add(identity(actual))
    require(reached_clips=={c['id'] for c in data['clips']},'unreachable/missing source clips')
    for clip in data['clips']:
        obj=source_object(clip['id']);require(obj.type.name=='AudioClip','source clip type')
        compare(obj.read_typetree(),clip['metadata'],'source audio metadata '+clip['id'])
        compare(obj.read().m_Name,clip['name'],'source clip name')
        samples=obj.read().samples;require(len(samples)==1,'source clip decode cardinality')
        compare(check_wave(package,clip),wave_data(next(iter(samples.values()))),'source decoded audio '+clip['id'])


def validate_package(package,manifest):
    try:
        package=Path(package);scenes=scene_objects(package,manifest)
        if not feature_enabled(manifest,scenes):return
        validate_ballistic_source(package,manifest,scenes)
        validate_audio_source(package,manifest,load_audio(package,manifest))
    except (KeyError,IndexError,TypeError,OSError,struct.error) as error:
        raise ValueError(f'Native features: invalid or incomplete source/package data: {error}') from error


def surface_for(material):
    groups={5:['Asphalt'],15:['GarbageMetal'],2:['Cardboard','GarbagePaper'],6:['Glass','GlassShattered'],
            7:['Gravel'],8:['MetalThin'],1:['Chainfence','Grate','MetalThick'],10:['Pebbles'],16:['Plastic'],
            3:['Mud','Soil','SoilForest'],11:['Tile'],9:['Water','WaterPuddle'],18:['WoodThin'],12:['WoodThick'],
            13:['Swamp'],4:['GrassHigh','GrassLow','Snow']}
    return next((value for value,names in groups.items() if material in names),0)


def ballistic_built_fields(component):
    return {'m_Enabled':int(component['enabled']), '_typeOfMaterial':component['target_material_id'],
            '_presetId':component['target_preset_id'],'NetId':0,'HitType':0,'SurfaceSound':surface_for(component['material']),
            **{target:component['values'][source] for source,target in zip(FIELDS,TARGET_FIELDS)}}


def check_fields(tree,expected,context):
    for field,value in expected.items():
        require(field in tree,context+': missing built field '+field)
        compare_storage(tree[field],value,context+'.'+field)


def vector(values):
    return dict(zip('xyzw',values))


def curve_fields(value):
    return {'m_Curve':value['keys'],'m_PreInfinity':value['preInfinity'],
            'm_PostInfinity':value['postInfinity'],'m_RotationOrder':value['rotationOrder']}


def player_built_fields(item):
    scalar=('volume','deltaVolume','fadeInDurationSec','fadeOutDurationSec','spread','spatialBlend',
            'minDistance','maxDistance','playOnAwake','useCustomSpreadCurve')
    expected={'m_Enabled':int(item['enabled']),'ForceSetMixerGroup':item['forceSetMixerGroup']}
    expected.update({'_'+k:item[k] for k in scalar})
    expected.update({'_'+k:vector(item[k]) for k in ('randomPitchRange','startPlayingDelay')})
    expected.update({'_'+k:curve_fields(item[k]) for k in ('rolloffCurve','spreadCurve')})
    if item['kind']!='LoopAmbientSoundPlayer':expected['_randomTimeRange']=vector(item['randomTimeRange'])
    return expected


def audio_source_built_fields(item):
    source=item['sourceAudio']
    fields=('Loop','Mute','Priority','DopplerLevel','Pan2D','Spatialize','SpatializePostEffects',
            'BypassEffects','BypassListenerEffects','BypassReverbZones','m_Enabled','m_Volume','m_Pitch',
            'MinDistance','MaxDistance','rolloffMode')
    return {**{k:source[k] for k in fields},'m_PlayOnAwake':False}


def require_null(tree,field,context):
    require(field in tree and tree[field]['m_PathID']==0,context+': expected deferred/null '+field)


def linked(reader,tree,field,target,context):
    require(field in tree and tree[field]['m_PathID']!=0,context+': missing '+field)
    require(object_key(ptr(reader,tree[field]))==object_key(target),context+': wrong link '+field)


def match_chain(actual,expected):
    try:compare(actual,expected,'chain');return True
    except ValueError:return False


def validate_ballistic_bundle(readers,scenes):
    native=[r for r in readers if r.type.name=='MonoBehaviour' and script_identity(r)[0]=='BallisticCollider']
    expected=[]
    for objects in scenes.values():
        lookup={o['id']:o for o in objects}
        for obj in objects:
            for component in obj['components']:
                if component['type']=='BallisticCollider':expected.append((obj,component,package_chain(obj,lookup),package_states(obj,lookup)))
    require(len(native)==len(expected),'built ballistic count differs')
    by_names=defaultdict(list)
    for reader in native:
        require_script(reader,'BallisticCollider');go=unity_go(reader)
        by_names[tuple(x[0] for x in go['chain'])].append((reader,go))
    for obj,component,chain,states in expected:
        candidates=by_names[tuple(x[0] for x in chain)]
        matches=[(r,g) for r,g in candidates if match_chain(g['chain'],chain)]
        require(len(matches)==1,'missing/ambiguous built ballistic owner '+component['source']['component_id'])
        reader,go=matches[0];candidates.remove(matches[0])
        compare((go['active'],go['layer']),(obj['active'],obj['layer']),'built ballistic owner state')
        compare(go['states'],states,'built ballistic ancestor state')
        require(any(c.type.name in ('MeshCollider','BoxCollider','SphereCollider','CapsuleCollider') for c in go['components']),'built ballistic owner physics absent')
        check_fields(reader.read_typetree(),ballistic_built_fields(component),'built ballistic '+component['source']['component_id'])


def validate_audio_bundle(readers,package,data):
    """Check native graph identity, exact hierarchy, all serialized payload fields and links."""
    native=defaultdict(list)
    for reader in readers:
        if reader.type.name=='MonoBehaviour':
            kind=script_identity(reader)[0]
            if kind in PLAYERS|SUPPORT|{'SoundBank'}:
                require_script(reader,kind);native[kind].append(reader)
    expected_counts=Counter(i['kind'] for i in data['players']+data['support']);expected_counts['SoundBank']=len(data['banks'])
    compare({k:len(v) for k,v in native.items()},dict(expected_counts),'built native audio component census')
    roots=[r for r in readers if r.type.name=='GameObject' and r.read_typetree()['m_Name']==AUDIO_ROOT]
    require(len(roots)==1,'built native audio root cardinality')
    root=unity_go(roots[0]);identity_transform=([0.,0.,0.],[0.,0.,0.,1.],[1.,1.,1.])
    compare(root['chain'],[(AUDIO_ROOT,*identity_transform),('ScriptsRoot',*identity_transform)],'owned audio root ancestry')
    # AuthorScriptsScene activates ScriptsRoot after authoring; the audio child
    # remains inactive until the runtime driver has listener/season/mixers.
    require(root['states']==[(False,0),(True,0)],'audio root must remain inactive beneath the active ScriptsRoot')
    objects={};object_readers={};components={};go_cache={}
    for reader in readers:
        if reader.type.name!='GameObject':continue
        go=unity_go(reader)
        positions=[i for i,t in enumerate(go['chain']) if t[0]==AUDIO_ROOT]
        if not positions or positions[0]==0:continue
        path='/'.join(t[0] for t in reversed(go['chain'][:positions[0]]))
        require(path not in objects,'duplicate built audio hierarchy path '+path)
        objects[path]=go;object_readers[path]=reader
    expected_paths={}
    for item in data['players']+data['support']:
        names=item['path'].split('/')
        for i in range(1,len(names)+1):
            path='/'.join(names[:i]);chain=[]
            for n,t in zip(names[:i],item['transformChain'][:i]):
                chain.append((n,list(t['position'].values()),list(t['rotation'].values()),list(t['scale'].values())))
            wanted=list(reversed(chain))+root['chain']
            if path in expected_paths:compare(expected_paths[path],wanted,'shared audio ancestor')
            expected_paths[path]=wanted
    require(objects.keys()==expected_paths.keys(),'missing/extra built audio hierarchy objects')
    for path,chain in expected_paths.items():
        compare(objects[path]['chain'],chain,'built audio transform '+path)
        require(objects[path]['states']==[(True,0)]*(len(chain)-2)+[(False,0),(True,0)],'built audio hierarchy active/layer state '+path)
        wanted=Counter({'Transform':1})
        for item in data['players']+data['support']:
            if item['path']==path:
                wanted[item['kind']]+=1
                if item['kind'] in PLAYERS:wanted['AudioSource']+=1
        actual=Counter(script_identity(r)[0] if r.type.name=='MonoBehaviour' else r.type.name for r in objects[path]['components'])
        compare(dict(actual),dict(wanted),'owned audio component set '+path)
    for item in data['players']+data['support']:
        go=objects[item['path']]
        candidates=[r for r in go['components'] if r.type.name=='MonoBehaviour' and script_identity(r)[0]==item['kind']]
        require(len(candidates)==1,'native component missing/duplicate on owner '+item['id'])
        components[item['id']]=candidates[0];go_cache[item['id']]=go
    banks={}
    for source_id,item in data['banks'].items():
        candidates=[r for r in native['SoundBank'] if r.read_typetree()['m_Name']==item['name']]
        require(len(candidates)==1,'bank name missing/ambiguous '+source_id);banks[source_id]=candidates[0]
    clips={};all_clips=[r for r in readers if r.type.name=='AudioClip']
    # Asset filename is the source identity, so equal clip names cannot alias.
    require(len(all_clips)==len(data['clips']),'built audio clip cardinality')
    for item in data['clips']:
        expected_name=item['id'].replace(':','_')
        candidates=[r for r in all_clips if r.read_typetree()['m_Name']==expected_name]
        require(len(candidates)==1,'built clip identity missing/ambiguous '+item['id'])
        reader=candidates[0];clips[item['id']]=reader;tree=reader.read_typetree();d=item['decoded']
        check_fields(tree,{'m_Channels':d['channels'],'m_Frequency':d['sampleRate'],'m_BitsPerSample':16,'m_CompressionFormat':0},'built PCM clip '+item['id'])
        samples=reader.read().samples;require(len(samples)==1,'built clip decode cardinality')
        compare(wave_data(next(iter(samples.values()))),check_wave(package,item),'built PCM payload '+item['id'])
    for source_id,item in data['banks'].items():
        reader=banks[source_id];tree=reader.read_typetree()
        mapping={'CustomLength':'customLength','BaseVolume':'baseVolume','BasePitch':'basePitch','DeltaVolume':'deltaVolume',
                 'DeltaPitch':'deltaPitch','Rolloff':'rolloff','IgnoreOcclusion':'ignoreOcclusion','Physical':'physical',
                 'HasEnvironment':'hasEnvironment','AllowLimitedPlay':'allowLimitedPlay','_clipLiength':'clipLength'}
        check_fields(tree,{**{k:item[v] for k,v in mapping.items()},'SourceType':4},'built bank '+source_id)
        require_null(tree,'BlendOptions','built bank')
        actual=tree['Environments'];expected=item['environments'];require(len(actual)==len(expected),'bank environment count')
        for env,source_env in zip(actual,expected):
            require(len(env['Clips'])==len(source_env),'bank distance count')
            for distance,source_distance in zip(env['Clips'],source_env):
                require(len(distance['Clips'])==len(source_distance),'bank clip slot count')
                for p,source_ptr in zip(distance['Clips'],source_distance):
                    require(object_key(ptr(reader,p))==object_key(clips[source_ptr['resolved']]),'built bank clip link/order')
    for item in data['players']+data['support']:
        kind=item['kind'];reader=components[item['id']];tree=reader.read_typetree();context='built '+item['id']
        check_fields(tree,{'m_Enabled':int(item['enabled'])},context)
        if kind in PLAYERS:
            check_fields(tree,player_built_fields(item),context);require_null(tree,'_mixerGroup',context)
            sources=[r for r in go_cache[item['id']]['components'] if r.type.name=='AudioSource'];require(len(sources)==1,'built player AudioSource cardinality')
            source_tree=sources[0].read_typetree();check_fields(source_tree,audio_source_built_fields(item),context+' AudioSource')
            require_null(source_tree,'m_audioClip',context);require_null(source_tree,'OutputAudioMixerGroup',context)
            if kind=='LoopAmbientSoundPlayer':linked(reader,tree,'_loopClip',clips[item['loopClip']['resolved']],context)
            elif kind=='AmbientSoundPlayer':linked(reader,tree,'_ambientBank',banks[item['ambientBank']['resolved']],context)
            else:
                for field,target,lookup in [('seasonAmbientBanks','_seasonAmbientBanks',banks),('seasonRandomTimeRange','_seasonRandomTimeRange',None)]:
                    mapping=tree[target];require(len(mapping['_keys'])==len(mapping['_values']) and len(set(mapping['_keys']))==len(mapping['_keys']),'invalid serialized seasonal dictionary')
                    pairs=dict(zip(mapping['_keys'],mapping['_values']));require(pairs.keys()==item[field].keys(),'seasonal bank key set')
                    for key,value in pairs.items():
                        if lookup is None:compare_storage(value,vector(item[field][key]),context+' season timing')
                        else:require(object_key(ptr(reader,value))==object_key(lookup[item[field][key]['resolved']]),context+' seasonal bank link')
        elif kind=='AmbientSoundPlayerGroup':
            require_null(tree,'_outputMixerGroup',context)
            actual=tree['_soundPlayers'];expected=item['soundPlayers'];require(len(actual)==len(expected),'built group player count')
            for p,want in zip(actual,expected):require(object_key(ptr(reader,p))==object_key(components[want['resolved']]),'built group player link/order')
        elif kind=='BezierSpline':check_fields(tree,{'points':[vector(p) for p in item['points']],'modes':item['modes'],'loop':item['loop']},context)
        elif kind=='SoundAmbientZoneCalculator':
            check_fields(tree,{'_resolution':item['resolution'],'_calculationRadius':item['calculationRadius'],'_blendSpeed':item['blendSpeed']},context)
            for field in ('splineEmitter','spline'):linked(reader,tree,'_'+field,components[item[field]['resolved']],context)
        elif kind=='AmbientPlayerSplineMappedEmitter':
            check_fields(tree,{'_spreadRange':vector(item['spreadRange'])},context)
            linked(reader,tree,'_soundPlayer',components[item['soundPlayer']['resolved']],context)


def validate_bundle(env,package,manifest):
    try:
        package=Path(package);scenes=scene_objects(package,manifest)
        if not feature_enabled(manifest,scenes):return
        readers=list(env.objects)
        require(not any(r.type.name=='MonoBehaviour' and script_identity(r)[0]=='WindowBreaker' for r in readers),'deferred glass was unexpectedly imported')
        validate_ballistic_bundle(readers,scenes)
        validate_audio_bundle(readers,package,load_audio(package,manifest))
    except (KeyError,IndexError,TypeError,OSError,struct.error) as error:
        raise ValueError(f'Native features: invalid or incomplete built native data: {error}') from error
