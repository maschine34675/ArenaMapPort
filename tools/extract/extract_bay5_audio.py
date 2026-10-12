"""Export native ambient graphs (Bay5, Equator, AirPit) from the user's install, never match audio.

export(source, package, map_key) returns the optional manifest.native_audio entry. The caller
publishes that entry only after its other extraction steps pass. Source custom player
layouts are pinned by full byte consumption; SoundBanks use an actual source bundle
type tree as an independent schema/graph check. Unsupported source changes fail closed.

Per-map data is not coded here. Which maps are supported, the source scene (``level<N>``),
the graph schema, the player/support/bank/clip census, the group coverage rule and, for a
schema-2 graph, the original hierarchy states (inactive players, disabled components, loops,
per-group mixer routes) all come from ``tools/extract/native_contracts/<stem>.json`` through
native_contract_loader (PLAN-AIRPIT items 3 and 13), the same pins validate_native_features
checks on the finished package. The scene must be the one the map's source plan marks with
role ``sound`` (native_features.for_plan cross-checks that before the extraction starts).
A map whose contract has ``audio: null`` is rejected here before anything is read or written.
"""
from __future__ import annotations
import argparse, collections, hashlib, io, json, math, os, struct, sys, wave
from pathlib import Path
import UnityPy
from UnityPy.classes import PPtr

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:  # standalone-importable regardless of caller sys.path setup
    sys.path.insert(0, _HERE)

from native_contract_loader import load_contracts  # noqa: E402

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

def hierarchy(obj, preserve_states=False):
    go=obj.parse_monobehaviour_head().m_GameObject.deref()
    path=[]; active=True; chain=[]
    while go is not None:
        t=go.read_typetree(); path.append(t['m_Name']); active &= bool(t['m_IsActive'])
        tr=next(deref(go,{'fileId':x['component']['m_FileID'],'pathId':x['component']['m_PathID']}) for x in t['m_Component'] if deref(go,{'fileId':x['component']['m_FileID'],'pathId':x['component']['m_PathID']}).type.name=='Transform')
        tt=tr.read_typetree(); node={'position':tt['m_LocalPosition'],'rotation':tt['m_LocalRotation'],'scale':tt['m_LocalScale']}
        if preserve_states: node.update(activeSelf=bool(t['m_IsActive']),layer=t['m_Layer'])
        chain.append(node)
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

def find_audio_contract(map_key):
    """The map's ambient-audio contract (native_contracts/<stem>.json ``audio``), or None.

    None means the map is unknown or declares no native audio. A malformed contract file is not
    "unsupported": load_contracts raises NativeContractError for it and that propagates.
    """
    contract=load_contracts().get(map_key)
    return contract['audio'] if contract else None

def audio_contract(map_key):
    audio=find_audio_contract(map_key)
    if audio is None: raise ValueError(f'unsupported native audio map: {map_key!r} declares no ambient audio contract')
    return audio

def expected_group_members(players, audio):
    """Player ids the AmbientSoundPlayerGroup records must cover exactly once, per the contract rule."""
    if audio['group_coverage']=='all_players': return [d['id'] for d in players]
    return [d['id'] for d in players if d['activeInHierarchy'] and d['enabled']]

def export(source, package, map_key='arena_bay5'):
    """Return a manifest entry after fully decoding/validating the map-owned audio graph."""
    contract=audio_contract(map_key)
    source_scene=contract['scene']; schema=contract['schema']; preserve_states=schema==2  # schema 2 records the original hierarchy states
    source=Path(source).resolve(); package=Path(package).resolve(); output=package/'audio'
    if source==package or source in package.parents: raise ValueError('output must not be in source installation')
    output.mkdir(parents=True,exist_ok=True)
    env=UnityPy.load(str(source/source_scene),str(source/'globalgamemanagers.assets'))
    scene=env.files[str(source/source_scene)]
    players=[]; support=[]; banks={}; clips={}; counts=collections.Counter()
    bank_schema_path=source/'StreamingAssets/Windows/assets/content/audio/prefabs/movement/concrete/pronesoundbank.bundle'
    schema_env=UnityPy.load(str(bank_schema_path))
    schema_obj=next(o for o in schema_env.objects if o.type.name=='MonoBehaviour' and script(o)=='SoundBank')
    bank_nodes=schema_obj.serialized_type.node
    for obj in list(scene.objects.values()):
        if obj.type.name!='MonoBehaviour': continue
        kind=script(obj); counts[kind]+=1
        if kind in SUPPORT:
            d=parse_support(obj,kind); d.update(hierarchy(obj,preserve_states)); d.update(id=identity(obj),kind=kind,byteSize=obj.byte_size); support.append(d)
        if kind not in PLAYERS: continue
        d=parse_player(obj,kind); d.update(hierarchy(obj,preserve_states)); d.update(id=identity(obj),kind=kind,byteSize=obj.byte_size,sourceAudio=source_audio(obj))
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
    expected_players=expected_group_members(players,contract)
    if collections.Counter(grouped)!=collections.Counter(expected_players): raise ValueError('group coverage')
    result={'schemaVersion':schema,'mapKey':map_key,'sourceScene':source_scene,'componentCounts':dict(counts),'players':players,'support':support,'banks':banks,'clips':[], 'bankSchema':{'path':bank_schema_path.relative_to(source).as_posix(),'sha256':hashlib.sha256(bank_schema_path.read_bytes()).hexdigest()},'sourceSceneSha256':hashlib.sha256((source/source_scene).read_bytes()).hexdigest()}
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
    return {'file':'audio/native-audio.json','schema_version':schema,'players':len(players),'banks':len(banks),'clips':len(clips),'sha256':hashlib.sha256(payload).hexdigest()}

def validate_wav(data, metadata, filename):
    if data[:4]!=b'RIFF': raise ValueError('expected decoded WAV')
    with wave.open(io.BytesIO(data)) as wav:
        if wav.getnchannels()!=metadata['m_Channels'] or wav.getframerate()!=metadata['m_Frequency'] or wav.getsampwidth()!=2: raise ValueError('decoded format drift')
        if abs(wav.getnframes()/wav.getframerate()-metadata['m_Length'])>2/wav.getframerate(): raise ValueError('decoded duration drift')
        if not any(wav.readframes(wav.getnframes())): raise ValueError('silent decoded clip')
        return {'file':filename,'channels':wav.getnchannels(),'sampleRate':wav.getframerate(),'frames':wav.getnframes(),'sampleWidth':2,'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()}

def validate_graph(data):
    key=data['mapKey']; audio=find_audio_contract(key)
    if audio is None or (data['schemaVersion'],data['sourceScene'])!=(audio['schema'],audio['scene']): raise ValueError('audio package identity')
    players=data['players']; banks=data['banks']; support=data['support']; clips=data['clips']
    if collections.Counter(x['kind'] for x in players)!=collections.Counter(audio['players']): raise ValueError('source player census changed')
    if (len(banks),len(clips))!=(audio['banks'],audio['clips']) or collections.Counter(s.get('kind') for s in support)!=collections.Counter(audio['support']): raise ValueError('source audio census changed')
    player_ids=[p['id'] for p in players]; clip_ids={c['id'] for c in clips}
    if len(set(player_ids))!=len(players) or len(clip_ids)!=len(clips): raise ValueError('duplicate source identity')
    grouped=[p['resolved'] for s in support for p in s.get('soundPlayers',[])]
    if audio['states'] is not None: validate_audio_states(players,support,audio)
    if collections.Counter(grouped)!=collections.Counter(expected_group_members(players,audio)): raise ValueError('group coverage')
    if audio['states'] is not None and len(grouped)!=audio['states']['grouped_players']: raise ValueError('audio grouped player count changed')
    if audio['states'] is not None: validate_audio_group_members(players,support,audio['states'])  # after coverage: only a player moved between groups reaches it
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

def validate_audio_states(players, support, audio):
    """Reject lost states or graph activation; hierarchy state is not component enabled state.

    Runs for the schema-2 graphs, whose contract carries ``states`` (Equator, AirPit): the original
    inactive players, the components disabled in the source, the AudioSources that loop (by id AND
    leaf name: the Unity importer and the client look the loop up by name) and the mixer each
    AmbientSoundPlayerGroup routes to, keyed by the group's leaf name, never by object-table order.
    Returns the player ids the groups must cover under the contract's coverage rule.
    """
    states=audio['states']; disabled=set(states['disabled_players'])
    if not disabled<={p['id'] for p in players}: raise ValueError('audio disabled player pin missing')
    nodes={}
    for item in players+support:
        names=item['path'].split('/'); chain=item['transformChain']
        if len(names)!=len(chain) or not names or any(not name for name in names): raise ValueError('audio hierarchy shape')
        active=True
        for index,node in enumerate(chain):
            if type(node.get('activeSelf')) is not bool or type(node.get('layer')) is not int or not 0<=node['layer']<=31: raise ValueError('audio hierarchy state')
            active &= node['activeSelf']; path='/'.join(names[:index+1])
            if path in nodes and nodes[path]!=node: raise ValueError('inconsistent shared audio hierarchy')
            nodes[path]=node
        if type(item['activeInHierarchy']) is not bool or active!=item['activeInHierarchy']: raise ValueError('audio hierarchy activation mismatch')
        if item['enabled'] is not (item['id'] not in disabled): raise ValueError('audio component activation changed')
    groups=group_records(support)
    if {n:g['mixerName'] for n,g in groups.items()}!={n:r['mixer'] for n,r in states['groups'].items()} or not all(g['activeInHierarchy'] for g in groups.values()): raise ValueError('audio group mixer contract changed')
    inactive={p['id']:p['path'].rsplit('/',1)[-1] for p in players if not p['activeInHierarchy']}
    if inactive!=states['inactive_players']: raise ValueError('audio inactive source players changed')
    for player in players:
        source=player['sourceAudio']
        if source['m_Enabled']!=1: raise ValueError('audio AudioSource activation changed')
        if source['Loop'] not in (0,1): raise ValueError('audio source loop changed')
        if player['playOnAwake'] or player['forceSetMixerGroup'] or player['mixerGroup']['pathId']!=0: raise ValueError('audio source start/mixer policy changed')
    if {p['id']:p['path'].rsplit('/',1)[-1] for p in players if p['sourceAudio']['Loop']}!=states['loops']: raise ValueError('audio source loop changed')
    return expected_group_members(players,audio)

def group_records(support):
    """AmbientSoundPlayerGroup records keyed by the group GameObject's leaf name, the key the client and importer route by."""
    groups={}
    for record in support:
        if record.get('kind')!='AmbientSoundPlayerGroup': continue
        name=record['path'].rsplit('/',1)[-1]
        if name in groups: raise ValueError('audio group names not unique')
        groups[name]=record
    return groups

def validate_audio_group_members(players, support, states):
    """Pin each group's membership. Coverage only proves every player is grouped once, so a player moved
    from one group to another (a different mixer bus) passes it; here the per-group member counts of the
    contract must match and every listed member must sit under its group's hierarchy path (Arena parents
    each player under its group, which also catches two players swapped between equal-sized groups).
    An id that is no player counts as outside every group."""
    paths={p['id']:p['path'] for p in players}
    groups=group_records(support)
    if {n:len(g.get('soundPlayers',[])) for n,g in groups.items()}!={n:r['players'] for n,r in states['groups'].items()}: raise ValueError('audio group membership changed')
    for group in groups.values():
        if not all(paths.get(m['resolved'],'').startswith(group['path']+'/') for m in group.get('soundPlayers',[])): raise ValueError('audio group membership changed')

def main():
    ap=argparse.ArgumentParser(); ap.add_argument('--source',type=Path,required=True); ap.add_argument('--package',type=Path,required=True)
    maps=sorted(k for k,c in load_contracts().items() if c['audio'] is not None)
    ap.add_argument('--map-key',choices=maps,default='arena_bay5'); args=ap.parse_args()
    print(json.dumps(export(args.source,args.package,args.map_key)))

if __name__=='__main__': main()
