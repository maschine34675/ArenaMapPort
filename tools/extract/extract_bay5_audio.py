"""Export Bay5's native ambient graph from the user's Arena install, never match audio.

export(source, package) returns the optional manifest.native_audio entry. The caller
publishes that entry only after its other extraction steps pass. Source custom player
layouts are pinned by full byte consumption; SoundBanks use an actual source bundle
type tree as an independent schema/graph check. Unsupported source changes fail closed.
"""
from __future__ import annotations
import argparse, collections, hashlib, io, json, math, struct, wave
from pathlib import Path
import UnityPy
from UnityPy.classes import PPtr

PLAYERS = {'AmbientSoundPlayer', 'SeasonAmbientSoundPlayer', 'LoopAmbientSoundPlayer'}
SUPPORT = {'AmbientSoundPlayerGroup', 'BezierSpline', 'AmbientSplineEmitterController', 'SoundAmbientZoneCalculator', 'AmbientPlayerSplineMappedEmitter'}

class Reader:
    def __init__(self, obj):
        self.obj, self.data, self.pos = obj, obj.get_raw_data(), 0
    def take(self, fmt):
        size = struct.calcsize('<'+fmt)
        if self.pos+size > len(self.data): raise ValueError('truncated '+str(self.obj.path_id))
        v=struct.unpack_from('<'+fmt,self.data,self.pos); self.pos+=size
        return v[0] if len(v)==1 else list(v)
    def align(self):
        end=(self.pos+3)&~3
        if any(self.data[self.pos:end]): raise ValueError('nonzero alignment')
        self.pos=end
    def string(self):
        n=self.take('i')
        if not 0<=n<4096: raise ValueError('string length')
        s=self.data[self.pos:self.pos+n].decode('utf-8'); self.pos+=n; self.align(); return s
    def count(self):
        n=self.take('i')
        if not 0<=n<1024: raise ValueError('count')
        return n
    def boolean(self):
        v=self.take('B')
        if v not in (0,1): raise ValueError('bool')
        self.align(); return bool(v)
    def ptr(self):
        a,b=self.take('iq'); return {'fileId':a,'pathId':b}
    def curve(self):
        keys=[]
        for _ in range(self.count()):
            values=self.take('ffffiff')
            if not all(math.isfinite(v) for v in values): raise ValueError('nonfinite curve')
            keys.append(dict(zip(('time','value','inSlope','outSlope','weightedMode','inWeight','outWeight'),values)))
        return {'keys':keys,'preInfinity':self.take('i'),'postInfinity':self.take('i'),'rotationOrder':self.take('i')}
    def header(self):
        return {'gameObject':self.ptr(),'enabled':self.boolean(),'script':self.ptr(),'name':self.string()}
    def done(self):
        if self.pos!=len(self.data): raise ValueError(f'{self.obj.path_id}: consumed {self.pos}/{len(self.data)}')

def deref(owner, ptr, expected=None):
    if ptr['pathId']==0: return None
    obj=PPtr(m_FileID=ptr['fileId'],m_PathID=ptr['pathId'],assetsfile=owner.assets_file).deref()
    if obj is None or (expected and obj.type.name!=expected): raise ValueError('invalid ref '+str(ptr))
    return obj

def script(obj):
    tree=obj.parse_monobehaviour_head().m_Script.read_typetree()
    return tree['m_ClassName']

def identity(obj):
    return f'{Path(obj.assets_file.name).name}:{obj.path_id}'

def script_hash(obj):
    return obj.parse_monobehaviour_head().m_Script.read_typetree()['m_PropertiesHash']

def parse_player(obj, kind):
    r=Reader(obj); d=r.header()
    for key in ('volume','deltaVolume'): d[key]=r.take('f')
    d['randomPitchRange']=r.take('ff')
    d['fadeInDurationSec']=r.take('f'); d['fadeOutDurationSec']=r.take('f')
    d['mixerGroup']=r.ptr(); d['forceSetMixerGroup']=r.boolean(); d['playOnAwake']=r.boolean()
    for key in ('spread','spatialBlend','minDistance','maxDistance'): d[key]=r.take('f')
    d['rolloffCurve']=r.curve(); d['startPlayingDelay']=r.take('ff'); d['spreadCurve']=r.curve(); d['useCustomSpreadCurve']=r.boolean()
    if kind=='LoopAmbientSoundPlayer': d['loopClip']=r.ptr()
    else:
        d['randomTimeRange']=r.take('ff')
        if kind=='AmbientSoundPlayer': d['ambientBank']=r.ptr()
        else:
            keys=[r.string() for _ in range(r.count())]; values=[r.ptr() for _ in range(r.count())]
            if len(keys)!=len(values) or len(set(keys))!=len(keys): raise ValueError('season banks')
            d['seasonAmbientBanks']=dict(zip(keys,values))
            keys=[r.string() for _ in range(r.count())]; values=[r.take('ff') for _ in range(r.count())]
            if len(keys)!=len(values) or len(set(keys))!=len(keys): raise ValueError('season times')
            d['seasonRandomTimeRange']=dict(zip(keys,values))
    r.done()
    if not 0<=d['volume']<=1 or not 0<=d['spatialBlend']<=1 or not 0<d['minDistance']<=d['maxDistance']: raise ValueError('player bounds')
    return d

def parse_bank(obj):
    if script(obj)!='SoundBank': raise ValueError('not SoundBank')
    r=Reader(obj); d=r.header()
    for key in ('customLength','baseVolume','basePitch','deltaVolume','deltaPitch','rolloff'): d[key]=r.take('f')
    # Adjacent booleans each carry SerializeField alignment in this source ABI.
    d['ignoreOcclusion']=r.boolean(); d['physical']=r.boolean(); d['hasEnvironment']=r.boolean()
    d['sourceType']=r.take('i'); d['allowLimitedPlay']=r.boolean()
    d['limitIfVisible']=r.boolean(); d['limitRadius']=r.take('ff'); d['limitPercentOfClip']=r.take('f'); d['clipLength']=r.take('f')
    d['environments']=[[[r.ptr() for _ in range(r.count())] for _ in range(r.count())] for _ in range(r.count())]
    d['blendOptions']=r.ptr(); d['indoorBlendOptions']=r.ptr(); d['discreteDistancePick']=r.boolean(); r.done(); return d

def parse_support(obj, kind):
    r=Reader(obj); d=r.header()
    if kind=='AmbientSoundPlayerGroup':
        d['outputMixerGroup']=r.ptr(); d['soundPlayers']=[r.ptr() for _ in range(r.count())]
        mixer=deref(obj,d['outputMixerGroup'])
        d['mixerName']=mixer.read_typetree()['m_Name'] if mixer else None
    elif kind=='BezierSpline':
        d['points']=[r.take('fff') for _ in range(r.count())]; d['modes']=[r.take('i') for _ in range(r.count())]; d['loop']=r.boolean()
    elif kind=='SoundAmbientZoneCalculator':
        d['splineEmitter']=r.ptr(); d['spline']=r.ptr(); d['resolution']=r.take('i'); d['calculationRadius']=r.take('f'); d['blendSpeed']=r.take('f')
    elif kind=='AmbientPlayerSplineMappedEmitter':
        d['soundPlayer']=r.ptr(); d['spreadRange']=r.take('ff')
    r.done(); return d

def hierarchy(obj):
    go=obj.parse_monobehaviour_head().m_GameObject.deref()
    path=[]; active=True; chain=[]
    while go is not None:
        t=go.read_typetree(); path.append(t['m_Name']); active &= bool(t['m_IsActive'])
        tr=next(deref(go,{'fileId':x['component']['m_FileID'],'pathId':x['component']['m_PathID']}) for x in t['m_Component'] if deref(go,{'fileId':x['component']['m_FileID'],'pathId':x['component']['m_PathID']}).type.name=='Transform')
        tt=tr.read_typetree(); chain.append({'position':tt['m_LocalPosition'],'rotation':tt['m_LocalRotation'],'scale':tt['m_LocalScale']})
        parent=deref(tr,{'fileId':tt['m_Father']['m_FileID'],'pathId':tt['m_Father']['m_PathID']})
        go=parent.read().m_GameObject.deref() if parent else None
    return {'path':'/'.join(reversed(path)),'activeInHierarchy':active,'transformChain':list(reversed(chain))}

def source_audio(obj):
    go=obj.parse_monobehaviour_head().m_GameObject.deref()
    sources=[]
    for comp in go.read_typetree()['m_Component']:
        ptr=comp['component']; co=deref(go,{'fileId':ptr['m_FileID'],'pathId':ptr['m_PathID']})
        if co.type.name=='AudioSource': sources.append(co.read_typetree())
    if len(sources)!=1: raise ValueError('player needs exactly one AudioSource')
    return sources[0]

def export(source, package):
    """Return a manifest entry after fully decoding/validating the map-owned audio graph."""
    source=Path(source).resolve(); package=Path(package).resolve(); output=package/'audio'
    if source==package or source in package.parents: raise ValueError('output must not be in source installation')
    output.mkdir(parents=True,exist_ok=True)
    env=UnityPy.load(str(source/'level188'),str(source/'globalgamemanagers.assets'))
    scene=env.files[str(source/'level188')]
    players=[]; support=[]; banks={}; clips={}; counts=collections.Counter()
    bank_schema_path=source/'StreamingAssets/Windows/assets/content/audio/prefabs/movement/concrete/pronesoundbank.bundle'
    schema_env=UnityPy.load(str(bank_schema_path))
    schema_obj=next(o for o in schema_env.objects if o.type.name=='MonoBehaviour' and script(o)=='SoundBank')
    bank_nodes=schema_obj.serialized_type.node
    for obj in list(scene.objects.values()):
        if obj.type.name!='MonoBehaviour': continue
        kind=script(obj); counts[kind]+=1
        if kind in SUPPORT:
            d=parse_support(obj,kind); d.update(hierarchy(obj)); d.update(id=identity(obj),kind=kind,byteSize=obj.byte_size); support.append(d)
        if kind not in PLAYERS: continue
        d=parse_player(obj,kind); d.update(hierarchy(obj)); d.update(id=identity(obj),kind=kind,byteSize=obj.byte_size,sourceAudio=source_audio(obj))
        refs=[d['ambientBank']] if kind=='AmbientSoundPlayer' else list(d['seasonAmbientBanks'].values()) if kind=='SeasonAmbientSoundPlayer' else []
        for ptr in refs:
            bank=deref(obj,ptr,'MonoBehaviour')
            if bank is None: continue
            key=identity(bank)
            if key not in banks:
                if script_hash(bank)!=script_hash(schema_obj): raise ValueError('SoundBank schema hash differs')
                bd=parse_bank(bank); bd['id']=key; bd['sourceTree']=bank.read_typetree(nodes=bank_nodes,check_read=True); banks[key]=bd
                if bd['name']!=bd['sourceTree']['m_Name']: raise ValueError('bank tree name mismatch')
                original=bd['sourceTree']['Environments']
                refs_from_tree=[[[{'fileId':p['m_FileID'],'pathId':p['m_PathID']} for p in distance['Clips']] for distance in environ['Clips']] for environ in original]
                if refs_from_tree!=bd['environments']: raise ValueError('bank graph decoder mismatch')
                for environ in bd['environments']:
                    for distance in environ:
                        for cp in distance:
                            co=deref(bank,cp,'AudioClip')
                            if co: clips[identity(co)]=co; cp['resolved']=identity(co)
            ptr['resolved']=key
        if 'loopClip' in d:
            co=deref(obj,d['loopClip'],'AudioClip')
            if co: clips[identity(co)]=co; d['loopClip']['resolved']=identity(co)
        players.append(d)
    # Validate all support edges against class identity; importing half a water graph is invalid.
    grouped=[]
    expected_refs={'splineEmitter':'AmbientPlayerSplineMappedEmitter','spline':'BezierSpline','soundPlayer':'LoopAmbientSoundPlayer'}
    for d in support:
        owner=scene.objects[int(d['id'].split(':')[1])]
        for key,expect in expected_refs.items():
            if key not in d: continue
            target=deref(owner,d[key],'MonoBehaviour')
            if not target or script(target)!=expect: raise ValueError('support edge '+key)
            d[key]['resolved']=identity(target)
        for ptr in d.get('soundPlayers',[]):
            target=deref(owner,ptr,'MonoBehaviour')
            if not target or script(target) not in PLAYERS: raise ValueError('group edge')
            ptr['resolved']=identity(target); grouped.append(identity(target))
    if collections.Counter(grouped)!=collections.Counter(d['id'] for d in players): raise ValueError('group coverage')
    result={'schemaVersion':1,'mapKey':'arena_bay5','sourceScene':'level188','componentCounts':dict(counts),'players':players,'support':support,'banks':banks,'clips':[], 'bankSchema':{'path':bank_schema_path.relative_to(source).as_posix(),'sha256':hashlib.sha256(bank_schema_path.read_bytes()).hexdigest()},'sourceSceneSha256':hashlib.sha256((source/'level188').read_bytes()).hexdigest()}
    for key,obj in clips.items():
        clip=obj.read(); tree=obj.read_typetree(); cd={'id':key,'name':clip.m_Name,'metadata':tree}
        samples=clip.samples
        if len(samples)!=1: raise ValueError(f'{key}: expected one clip, got {len(samples)}')
        _,data=next(iter(samples.items()))
        filename=key.replace(':','_')+'.wav'
        cd['decoded']=validate_wav(data,tree,'audio/'+filename)
        (output/filename).write_bytes(data)
        result['clips'].append(cd)
    validate_graph(result)
    payload=json.dumps(result,indent=2,allow_nan=False).encode('utf-8')
    tmp=output/'native-audio.json.tmp'; tmp.write_bytes(payload); tmp.replace(output/'native-audio.json')
    return {'file':'audio/native-audio.json','schema_version':1,'players':len(players),'banks':len(banks),'clips':len(clips),'sha256':hashlib.sha256(payload).hexdigest()}

def validate_wav(data, metadata, filename):
    if data[:4]!=b'RIFF': raise ValueError('expected decoded WAV')
    with wave.open(io.BytesIO(data)) as wav:
        if wav.getnchannels()!=metadata['m_Channels'] or wav.getframerate()!=metadata['m_Frequency'] or wav.getsampwidth()!=2: raise ValueError('decoded format drift')
        if abs(wav.getnframes()/wav.getframerate()-metadata['m_Length'])>2/wav.getframerate(): raise ValueError('decoded duration drift')
        if not any(wav.readframes(wav.getnframes())): raise ValueError('silent decoded clip')
        return {'file':filename,'channels':wav.getnchannels(),'sampleRate':wav.getframerate(),'frames':wav.getnframes(),'sampleWidth':2,'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()}

def validate_graph(data):
    if data['schemaVersion']!=1 or data['mapKey']!='arena_bay5' or data['sourceScene']!='level188': raise ValueError('audio package identity')
    players=data['players']; banks=data['banks']; support=data['support']; clips=data['clips']
    if collections.Counter(x['kind'] for x in players)!={'AmbientSoundPlayer':17,'SeasonAmbientSoundPlayer':6,'LoopAmbientSoundPlayer':1}: raise ValueError('source player census changed')
    if len(banks)!=13 or len(clips)!=106 or len(support)!=6: raise ValueError('source audio census changed')
    player_ids=[p['id'] for p in players]; clip_ids={c['id'] for c in clips}
    if len(set(player_ids))!=len(players) or len(clip_ids)!=len(clips): raise ValueError('duplicate source identity')
    grouped=[p['resolved'] for s in support for p in s.get('soundPlayers',[])]
    if collections.Counter(grouped)!=collections.Counter(player_ids): raise ValueError('group coverage')
    for bank in banks.values():
        if not bank['environments'] or not bank['environments'][0] or not bank['environments'][0][0]: raise ValueError('empty native PickSingleClip bank')
        for refs in bank['environments']:
            for distance in refs:
                for ptr in distance:
                    if ptr.get('resolved') not in clip_ids: raise ValueError('unresolved bank clip')
    for p in players:
        refs=[p['ambientBank']] if p['kind']=='AmbientSoundPlayer' else p.get('seasonAmbientBanks',{}).values()
        for ptr in refs:
            if ptr.get('resolved') not in banks: raise ValueError('unresolved player bank')
        if 'loopClip' in p and p['loopClip'].get('resolved') not in clip_ids: raise ValueError('unresolved loop clip')

def main():
    ap=argparse.ArgumentParser(); ap.add_argument('--source',type=Path,required=True); ap.add_argument('--package',type=Path,required=True); args=ap.parse_args()
    print(json.dumps(export(args.source,args.package)))

if __name__=='__main__': main()
