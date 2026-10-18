"""Non-proprietary mutation fixtures for independent native-feature acceptance."""
from copy import deepcopy
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import wave

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'validate'))
import validate_native_features as v
import native_contract_loader as L  # validate_native_features puts tools/extract on sys.path
import partial_scope


class Reader:
    def __init__(self,scene,kind,tree,owner=None,script=None):
        self.path_id=max(scene.objects,default=0)+1;self.assets_file=scene;self.type=SimpleNamespace(name=kind)
        self.tree=deepcopy(tree);self.owner=owner;self.script=script;self.assembly='Assembly-CSharp';self.samples={}
        scene.objects[self.path_id]=self
    def read_typetree(self):return deepcopy(self.tree)
    def read(self):
        if self.type.name=='Transform':return SimpleNamespace(m_GameObject=SimpleNamespace(deref=lambda:self.owner))
        return SimpleNamespace(samples=self.samples)
    def parse_monobehaviour_head(self):
        script=SimpleNamespace(m_ClassName=self.script,m_Namespace=v.SCRIPT_NS.get(self.script,''),m_AssemblyName=self.assembly)
        return SimpleNamespace(m_GameObject=SimpleNamespace(deref=lambda:self.owner),
            m_Script=SimpleNamespace(path_id=999,deref=lambda:SimpleNamespace(read=lambda:script)))


class Scene:
    def __init__(self):self.objects={};self.name='fixture.assets'
    def ref(self,reader):return {'m_FileID':0,'m_PathID':reader.path_id if reader else 0}
    def deref(self,reader,p):return self.objects[p.get('m_PathID',p.get('pathId'))]
    def go(self,name,parent=None,active=True,position=(0.,0.,0.)):
        go=Reader(self,'GameObject',{'m_Name':name,'m_IsActive':active,'m_Layer':0,'m_Component':[]})
        transform=Reader(self,'Transform',{'m_LocalPosition':dict(zip('xyz',position)),
            'm_LocalRotation':dict(zip('xyzw',(0.,0.,0.,1.))),'m_LocalScale':dict(zip('xyz',(1.,1.,1.))),
            'm_Father':self.ref(parent.transform if parent else None)},go)
        go.transform=transform;go.tree['m_Component'].append({'component':self.ref(transform)})
        return go
    def component(self,owner,kind,fields,script=None):
        reader=Reader(self,kind,fields,owner,script)
        owner.tree['m_Component'].append({'component':self.ref(reader)})
        return reader


def pcm_wave():
    output=io.BytesIO()
    with wave.open(output,'wb') as wav:
        wav.setnchannels(1);wav.setsampwidth(2);wav.setframerate(8000)
        wav.writeframes(struct.pack('<4h',-1500,700,1900,-800))
    return output.getvalue()


def curve():
    return {'keys':[{'time':0.,'value':1.,'inSlope':0.,'outSlope':0.,'weightedMode':0,'inWeight':0.,'outWeight':0.}],
            'preInfinity':2,'postInfinity':2,'rotationOrder':4}


def player(kind,path):
    return {'id':kind,'kind':kind,'path':path,'enabled':True,'volume':.2,'deltaVolume':.01,
            'randomPitchRange':[.9,1.1],'fadeInDurationSec':0.,'fadeOutDurationSec':1.,
            'spread':.5,'spatialBlend':1.,'minDistance':1.,'maxDistance':70.,'playOnAwake':False,
            'forceSetMixerGroup':False,'useCustomSpreadCurve':False,'startPlayingDelay':[0.,2.],
            'rolloffCurve':curve(),'spreadCurve':curve(),'randomTimeRange':[2.,5.],
            'sourceAudio':{'Loop':False,'Mute':False,'Priority':128,'DopplerLevel':1.,'Pan2D':0.,
                'Spatialize':False,'SpatializePostEffects':False,'BypassEffects':False,'BypassListenerEffects':False,
                'BypassReverbZones':False,'m_Enabled':1,'m_Volume':.7,'m_Pitch':1.,'MinDistance':1.,'MaxDistance':30.,'rolloffMode':2}}


BAY5_AUDIO_ROOT=v.MAPS['arena_bay5']['audio']['root']


def audio_fixture(directory):
    scene=Scene();scripts=scene.go('ScriptsRoot',active=True);root=scene.go(BAY5_AUDIO_ROOT,scripts,False)
    source_root=scene.go('SourceAmbient',root);gos={};native={}
    names=['Random','Season','Water','Group','Spline','Controller','Calculator','Emitter']
    for name in names:gos[name]=scene.go(name,source_root)
    players=[player(k,'SourceAmbient/'+n) for k,n in zip(('AmbientSoundPlayer','SeasonAmbientSoundPlayer','LoopAmbientSoundPlayer'),names[:3])]
    clip_id='sounds.assets:10';bank_id='sounds.assets:20'
    players[0]['ambientBank']={'resolved':bank_id};players[1]['seasonAmbientBanks']={'Winter':{'resolved':bank_id}}
    players[1]['seasonRandomTimeRange']={'Winter':[3.,6.]};players[2]['loopClip']={'resolved':clip_id}
    clip_bytes=pcm_wave();path=Path(directory)/'audio';path.mkdir();(path/'fixture.wav').write_bytes(clip_bytes)
    clip={'id':clip_id,'decoded':{'file':'audio/fixture.wav','sha256':v.digest(clip_bytes),'bytes':len(clip_bytes),
            'channels':1,'sampleRate':8000,'frames':4,'sampleWidth':2}}
    clip_reader=Reader(scene,'AudioClip',{'m_Name':'sounds.assets_10','m_Channels':1,'m_Frequency':8000,'m_BitsPerSample':16,'m_CompressionFormat':0})
    clip_reader.samples={'fixture.wav':clip_bytes}
    bank={'id':bank_id,'name':'FixtureBank','customLength':0.,'baseVolume':.3,'basePitch':1.,'deltaVolume':.1,
          'deltaPitch':0.,'rolloff':80.,'ignoreOcclusion':False,'physical':False,'hasEnvironment':False,
          'allowLimitedPlay':False,'clipLength':0.,'environments':[[[{'resolved':clip_id}]]]}
    bank_reader=Reader(scene,'MonoBehaviour',{'m_Name':'FixtureBank','CustomLength':0.,'BaseVolume':.3,'BasePitch':1.,'DeltaVolume':.1,
          'DeltaPitch':0.,'Rolloff':80.,'IgnoreOcclusion':False,'Physical':False,'HasEnvironment':False,'AllowLimitedPlay':False,
          '_clipLiength':0.,'SourceType':4,'BlendOptions':scene.ref(None),'Environments':[{'Clips':[{'Clips':[scene.ref(clip_reader)]}]}]},script='SoundBank')
    for item,name in zip(players,names[:3]):
        fields=v.player_built_fields(item);fields['_mixerGroup']=scene.ref(None)
        if name=='Random':fields['_ambientBank']=scene.ref(bank_reader)
        elif name=='Season':
            fields['_seasonAmbientBanks']={'_keys':['Winter'],'_values':[scene.ref(bank_reader)]}
            fields['_seasonRandomTimeRange']={'_keys':['Winter'],'_values':[{'x':3.,'y':6.}]}
        else:fields['_loopClip']=scene.ref(clip_reader)
        native[item['id']]=scene.component(gos[name],'MonoBehaviour',fields,item['kind'])
        audio=v.audio_source_built_fields(item);audio.update(m_audioClip=scene.ref(None),OutputAudioMixerGroup=scene.ref(None))
        scene.component(gos[name],'AudioSource',audio)
    support=[{'id':'group','kind':'AmbientSoundPlayerGroup','path':'SourceAmbient/Group','enabled':True,
              'soundPlayers':[{'resolved':p['id']} for p in players]},
             {'id':'spline','kind':'BezierSpline','path':'SourceAmbient/Spline','enabled':True,
              'points':[[0.,0.,0.],[1.,0.,0.],[2.,0.,0.],[3.,0.,0.]],'modes':[0,1,2],'loop':False},
             {'id':'controller','kind':'AmbientSplineEmitterController','path':'SourceAmbient/Controller','enabled':True},
             {'id':'calculator','kind':'SoundAmbientZoneCalculator','path':'SourceAmbient/Calculator','enabled':True,
              'splineEmitter':{'resolved':'emitter'},'spline':{'resolved':'spline'},'resolution':80,'calculationRadius':75.,'blendSpeed':3.},
             {'id':'emitter','kind':'AmbientPlayerSplineMappedEmitter','path':'SourceAmbient/Emitter','enabled':True,
              'soundPlayer':{'resolved':'LoopAmbientSoundPlayer'},'spreadRange':[0.,1.]}]
    for item,name in zip(support,names[3:]):native[item['id']]=scene.component(gos[name],'MonoBehaviour',{'m_Enabled':1},item['kind'])
    native['group'].tree.update(_outputMixerGroup=scene.ref(None),_soundPlayers=[scene.ref(native[p['id']]) for p in players])
    native['spline'].tree.update(points=[dict(zip('xyz',p)) for p in support[1]['points']],modes=[0,1,2],loop=False)
    native['calculator'].tree.update(_resolution=80,_calculationRadius=75.,_blendSpeed=3.,_splineEmitter=scene.ref(native['emitter']),_spline=scene.ref(native['spline']))
    native['emitter'].tree.update(_spreadRange={'x':0.,'y':1.},_soundPlayer=scene.ref(native['LoopAmbientSoundPlayer']))
    transform={'position':{'x':0.,'y':0.,'z':0.},'rotation':{'x':0.,'y':0.,'z':0.,'w':1.},'scale':{'x':1.,'y':1.,'z':1.}}
    for item in players+support:
        item['transformChain']=[deepcopy(transform),deepcopy(transform)];item['activeInHierarchy']=True
    data={'schemaVersion':1,'mapKey':'arena_bay5','sourceScene':'level188',
          'players':players,'support':support,'clips':[clip],'banks':{bank_id:bank}}
    return scene,data,native,gos,clip_reader


def equator_fixture(directory):
    """Synthetic indoor graph with source identity labels, never proprietary assets."""
    scene,data,_,_,clip_reader=audio_fixture(directory)
    bank_reader=next(r for r in scene.objects.values() if r.script=='SoundBank')
    keep={clip_reader.path_id,bank_reader.path_id}
    scene.objects={k:r for k,r in scene.objects.items() if k in keep}
    scripts=scene.go('ScriptsRoot',active=True)
    root=scene.go(v.MAPS['arena_equator']['audio']['root'],scripts,False)
    source=scene.go('AmbientAudioSystem',root)
    container=scene.go('AmbientPlayersGroup',source)
    group_go=scene.go('CommonAmbientInGroup',container)
    path='AmbientAudioSystem/AmbientPlayersGroup/CommonAmbientInGroup'
    transform={'position':{'x':0.,'y':0.,'z':0.},'rotation':{'x':0.,'y':0.,'z':0.,'w':1.},
               'scale':{'x':1.,'y':1.,'z':1.},'activeSelf':True,'layer':0}
    bank_id=next(iter(data['banks']));players=[];native={};gos={}
    labels=[('level56:965','electricity'),('level56:970','far_dogs2'),('level56:975','far_dogs1')]
    labels.extend(('level56:'+str(1000+i),'incidental'+str(i)) for i in range(22))
    for identifier,name in labels:
        active=not name.startswith('far_dogs')
        go=scene.go(name,group_go,active);gos[name]=go
        item=player('AmbientSoundPlayer',path+'/'+name)
        item.update(id=identifier,activeInHierarchy=active,ambientBank={'resolved':bank_id},
                    transformChain=[deepcopy(transform) for _ in range(4)])
        item['transformChain'][-1]['activeSelf']=active
        item['sourceAudio']['Loop']=name=='electricity'
        fields=v.player_built_fields(item)
        fields.update(_mixerGroup=scene.ref(None),_ambientBank=scene.ref(bank_reader))
        native[identifier]=scene.component(go,'MonoBehaviour',fields,'AmbientSoundPlayer')
        audio=v.audio_source_built_fields(item);audio.update(m_audioClip=scene.ref(None),OutputAudioMixerGroup=scene.ref(None))
        scene.component(go,'AudioSource',audio);players.append(item)
    group={'id':'level56:780','kind':'AmbientSoundPlayerGroup','path':path,'enabled':True,
           'activeInHierarchy':True,'transformChain':[deepcopy(transform) for _ in range(3)],
           'mixerName':'CommonAmbInEffects','soundPlayers':[{'resolved':p['id']} for p in players if p['activeInHierarchy']]}
    native[group['id']]=scene.component(group_go,'MonoBehaviour',{'m_Enabled':1,'_outputMixerGroup':scene.ref(None),
        '_soundPlayers':[scene.ref(native[p['resolved']]) for p in group['soundPlayers']]},group['kind'])
    gos['group']=group_go
    data.update(schemaVersion=2,mapKey='arena_equator',sourceScene='level56',players=players,support=[group])
    return scene,data,native,gos,clip_reader


def manifest_for(key):
    contract=v.MAPS[key];audio=contract['audio']
    manifest={'source':{'map_key':key},'scenes':list(contract['scenes'].values()),
              'native_features':{'schema_version':1,'required':['ballistics']+([audio['feature']] if audio else [])},
              'native_ballistics':{}}
    if audio:manifest['native_audio']={}
    return manifest


def write_audio_index(directory,data):
    """Index-only fixture; bundle/source tests separately verify actual assets."""
    key=data['mapKey'];contract=v.MAPS[key]['audio'];data=deepcopy(data)
    first_bank=next(iter(data['banks'].values()));first_clip=data['clips'][0]
    data['banks']={**data['banks'],**{'fixture-bank'+str(i):dict(first_bank,id='fixture-bank'+str(i))
                                     for i in range(contract['banks']-len(data['banks']))}}
    data['clips'].extend(dict(first_clip,id='fixture-clip'+str(i)) for i in range(contract['clips']-len(data['clips'])))
    path=Path(directory)/'audio/native-audio.json';path.write_text(json.dumps(data),encoding='utf-8')
    manifest=manifest_for(key)
    manifest['native_audio']={'schema_version':contract['schema'],'file':'audio/native-audio.json',
                             'sha256':v.file_digest(path),'players':len(data['players']),
                             'banks':len(data['banks']),'clips':len(data['clips'])}
    return manifest


class NativeFeatureTests(unittest.TestCase):
    def test_float32_payload_bit_changes_fail(self):
        original=struct.unpack('<f',struct.pack('<I',0x3F000000))[0]
        changed=struct.unpack('<f',struct.pack('<I',0x3F000001))[0]
        with self.assertRaisesRegex(ValueError,'bits differ'):v.check_fields({'value':changed},{'value':original},'one ULP mutation')

    def test_ballistic_name_mapping_uses_both_tables(self):
        source=[{'index':i,'name':name,'material':100+i,'values':(0.,)*6} for i,name in enumerate(v.MATERIALS)]
        target=[{'index':i,'name':name,'material':v.MATERIALS.index(name),'values':(0.,)*6} for i,name in enumerate(reversed(v.MATERIALS))]
        raw=struct.pack('<iqB3xiqi',0,77,1,1,999,0)+struct.pack('<5i6f',0,27,0,0,127,3.,.6,.1,.2,.3,.04)
        reader=SimpleNamespace(path_id=88,assets_file=SimpleNamespace(name='level84'),get_raw_data=lambda:raw)
        expected=v.ballistic_expected(reader,source,target)
        self.assertEqual(expected['material'],'WoodThin');self.assertEqual(expected['target_material_id'],27)
        self.assertEqual(expected['target_preset_id'],len(v.MATERIALS)-28)
        self.assertEqual(expected['source']['material_id'],127)

    def test_preset_table_duplicate_or_unknown_scale_curve_fails(self):
        wire=v.Wire();wire.number('i',47)
        for i,name in enumerate(v.MATERIALS+['FixtureVariant'+str(n) for n in range(8)]):
            wire.string(name);wire.number('i',min(i,38));wire.number('i',6);wire.floats([0.]*6)
            for value in (0,0,2,2,4):wire.number('i',value)
        raw=bytes(wire.data);self.assertEqual(len(v.find_presets(raw)),47)
        with self.assertRaises(ValueError):v.find_presets(raw+raw)
        changed=bytearray(raw);changed[-20]=1
        with self.assertRaises(ValueError):v.find_presets(changed)

    def test_required_contract_rejects_partial_and_unmarked(self):
        with self.assertRaises(ValueError):v.feature_enabled({'native_audio':{}})
        with self.assertRaises(ValueError):v.feature_enabled({}, {'scene':[{'components':[{'type':'BallisticCollider'}]}]})
        self.assertFalse(v.feature_enabled({}, {'scene':[{'components':[]}]}))

    def test_light_culling_optional_summary_shape(self):
        manifest=manifest_for('arena_equator')
        self.assertTrue(v.feature_enabled(manifest))  # absent entirely: still validates (optional)
        manifest['native_features']['light_culling']={'schema_version':1,'culling_light_object_count':472,
            'volumetric_light_count':17,'light_flicker_count':2}
        self.assertTrue(v.feature_enabled(manifest))
        for bad in ({'schema_version':2,'culling_light_object_count':0,'volumetric_light_count':0,'light_flicker_count':0},
                    {'schema_version':1,'culling_light_object_count':-1,'volumetric_light_count':0,'light_flicker_count':0},
                    {'schema_version':1,'culling_light_object_count':0,'volumetric_light_count':0},
                    {'schema_version':1,'culling_light_object_count':0.5,'volumetric_light_count':0,'light_flicker_count':0}):
            broken=manifest_for('arena_equator');broken['native_features']['light_culling']=bad
            with self.assertRaisesRegex(ValueError,'light_culling feature summary shape'):v.feature_enabled(broken)

    def test_independent_wire_fixed_header_and_payload(self):
        item={'gameObject':{'fileId':0,'pathId':12},'enabled':False,'script':{'fileId':1,'pathId':99},'name':'',
              'soundPlayer':{'fileId':0,'pathId':31},'spreadRange':[.25,.75]}
        expected=struct.pack('<iqB3xiqi',0,12,0,1,99,0)+struct.pack('<iqff',0,31,.25,.75)
        self.assertEqual(v.audio_wire(item,'AmbientPlayerSplineMappedEmitter'),expected)
        item['spreadRange'][1]=.5
        self.assertNotEqual(v.audio_wire(item,'AmbientPlayerSplineMappedEmitter'),expected)

    def test_ballistic_same_count_field_owner_and_identity_changes(self):
        scene=Scene();go=scene.go('Wall');scene.component(go,'BoxCollider',{'m_Enabled':1,'m_IsTrigger':False})
        c={'enabled':False,'target_material_id':27,'target_preset_id':27,'material':'WoodThin',
           'values':dict(zip(v.FIELDS,[3.,.6,.1,.2,.3,.04])),'source':{'component_id':'source-1'},'type':'BallisticCollider'}
        obj={'id':'1','parent_id':None,'name':'Wall','active':True,'layer':0,'local_position':[0.,0.,0.],
             'local_rotation':[0.,0.,0.,1.],'local_scale':[1.,1.,1.],
             'components':[{'type':'BoxCollider','enabled':True,'is_trigger':False},c]}
        reader=scene.component(go,'MonoBehaviour',v.ballistic_built_fields(c),'BallisticCollider')
        with patch.object(v,'ptr',scene.deref):
            v.validate_ballistic_bundle(list(scene.objects.values()),{'scene':[obj]})
            collider=next(r for r in scene.objects.values() if r.type.name=='BoxCollider')
            for field,value in [('m_Enabled',0),('m_IsTrigger',True)]:
                old=collider.tree[field];collider.tree[field]=value
                with self.subTest(field=field),self.assertRaises(ValueError):v.validate_ballistic_bundle(list(scene.objects.values()),{'scene':[obj]})
                collider.tree[field]=old
            for field,value in [('PenetrationLevel',4.),('_presetId',28),('HitType',2),('m_Enabled',1)]:
                old=reader.tree[field];reader.tree[field]=value
                with self.subTest(field=field),self.assertRaises(ValueError):v.validate_ballistic_bundle(list(scene.objects.values()),{'scene':[obj]})
                reader.tree[field]=old
            reader.assembly='SptGameRefs-AssemblyCSharp.dll'
            with self.assertRaises(ValueError):v.validate_ballistic_bundle(list(scene.objects.values()),{'scene':[obj]})
            reader.assembly='Assembly-CSharp';go.transform.tree['m_LocalPosition']['x']=1.
            with self.assertRaises(ValueError):v.validate_ballistic_bundle(list(scene.objects.values()),{'scene':[obj]})

    def test_owner_component_must_be_attached(self):
        scene=Scene();go=scene.go('Wall');reader=scene.component(go,'MonoBehaviour',{},'BallisticCollider')
        go.tree['m_Component'].pop()
        with patch.object(v,'ptr',scene.deref),self.assertRaises(ValueError):v.unity_go(reader)

    def audio_mutation(self,change):
        with tempfile.TemporaryDirectory() as directory:
            scene,data,native,gos,clip=audio_fixture(directory)
            with patch.object(v,'ptr',scene.deref):
                v.validate_audio_bundle(list(scene.objects.values()),directory,data)
                change(scene,data,native,gos,clip)
                with self.assertRaises(ValueError):v.validate_audio_bundle(list(scene.objects.values()),directory,data)

    def test_audio_wrong_bank_reference(self):
        self.audio_mutation(lambda s,d,n,g,c:n['AmbientSoundPlayer'].tree.update(_ambientBank=s.ref(c)))

    def test_audio_missing_season_key(self):
        self.audio_mutation(lambda s,d,n,g,c:n['SeasonAmbientSoundPlayer'].tree['_seasonAmbientBanks'].update(_keys=[],_values=[]))

    def test_audio_changed_curve_and_spline(self):
        self.audio_mutation(lambda s,d,n,g,c:n['AmbientSoundPlayer'].tree['_rolloffCurve']['m_Curve'][0].update(value=.9))
        self.audio_mutation(lambda s,d,n,g,c:n['spline'].tree['points'][2].update(x=5.))

    def test_audio_changed_delay(self):
        self.audio_mutation(lambda s,d,n,g,c:n['SeasonAmbientSoundPlayer'].tree['_seasonRandomTimeRange']['_values'][0].update(y=7.))

    def test_audio_duplicate_component(self):
        self.audio_mutation(lambda s,d,n,g,c:s.component(g['Water'],'MonoBehaviour',deepcopy(n['LoopAmbientSoundPlayer'].tree),'LoopAmbientSoundPlayer'))

    def test_audio_missing_component(self):
        self.audio_mutation(lambda s,d,n,g,c:s.objects.pop(n['emitter'].path_id))

    def test_audio_detached_component(self):
        self.audio_mutation(lambda s,d,n,g,c:g['Emitter'].tree['m_Component'].pop())

    def test_audio_changed_hierarchy_state(self):
        self.audio_mutation(lambda s,d,n,g,c:g['Water'].tree.update(m_IsActive=False))
        self.audio_mutation(lambda s,d,n,g,c:g['Water'].transform.tree['m_LocalPosition'].update(x=2.))

    def test_audio_saved_startup_root_states(self):
        def set_root_state(scene,name,active):
            reader=next(r for r in scene.objects.values() if r.type.name=='GameObject' and r.tree['m_Name']==name)
            reader.tree['m_IsActive']=active
        self.audio_mutation(lambda s,d,n,g,c:set_root_state(s,'ScriptsRoot',False))
        self.audio_mutation(lambda s,d,n,g,c:set_root_state(s,BAY5_AUDIO_ROOT,True))

    def test_audio_wrong_group_order(self):
        self.audio_mutation(lambda s,d,n,g,c:n['group'].tree['_soundPlayers'].reverse())

    def test_audio_extra_component_on_owned_hierarchy(self):
        self.audio_mutation(lambda s,d,n,g,c:s.component(g['Water'],'Light',{'m_Enabled':1}))

    def test_audio_wrong_script_assembly(self):
        self.audio_mutation(lambda s,d,n,g,c:setattr(n['calculator'],'assembly','SptGameRefs-AssemblyCSharp.dll'))

    def test_audio_same_length_pcm_mutation(self):
        def change(s,d,n,g,c):
            content=bytearray(next(iter(c.samples.values())));content[-1]^=1;c.samples={'fixture.wav':bytes(content)}
        self.audio_mutation(change)

    def test_package_wave_changed_after_recording_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            _,data,_,_,_=audio_fixture(directory);clip=data['clips'][0];path=Path(directory)/clip['decoded']['file']
            v.check_wave(directory,clip);path.write_bytes(path.read_bytes()+b'changed')
            with self.assertRaises(ValueError):v.check_wave(directory,clip)

    def test_map_contract_keeps_legacy_bay5_and_requires_explicit_equator(self):
        bay5=manifest_for('arena_bay5');bay5.pop('source')
        self.assertTrue(v.feature_enabled(bay5))
        equator=manifest_for('arena_equator');self.assertTrue(v.feature_enabled(equator))
        self.assertFalse(v.feature_enabled({'scenes':['AnyLegacyMap']}))
        for change in (lambda m:m.pop('source'),lambda m:m['source'].update(map_key='arena_prison'),
                       lambda m:m['source'].update(map_key='arena_bay5'),
                       lambda m:m['native_features'].update(required=['ballistics','harbor_audio']),
                       lambda m:m['scenes'].pop(),lambda m:m['scenes'].append(m['scenes'][0])):
            changed=deepcopy(equator);change(changed)
            with self.subTest(change=change),self.assertRaises(ValueError):v.feature_enabled(changed)

    def test_equator_native_contract_rejects_slice_with_all_scene_names(self):
        manifest=manifest_for('arena_equator')
        self.assertTrue(v.feature_enabled(manifest))
        manifest['slice']={};self.assertTrue(v.feature_enabled(manifest))
        manifest['slice']={'51':[5]}
        self.assertEqual(set(manifest['scenes']),set(v.MAPS['arena_equator']['scenes'].values()))
        with self.assertRaisesRegex(ValueError,'require unsliced scenes'):v.feature_enabled(manifest)
        legacy={'source':manifest['source'],'scenes':manifest['scenes'],'slice':manifest['slice']}
        self.assertFalse(v.feature_enabled(legacy))

    def equator_bundle_mutation(self,change):
        with tempfile.TemporaryDirectory() as directory:
            scene,data,native,gos,clip=equator_fixture(directory)
            with patch.object(v,'ptr',scene.deref):
                v.validate_audio_bundle(list(scene.objects.values()),directory,data)
                change(scene,data,native,gos,clip)
                with self.assertRaises(ValueError):v.validate_audio_bundle(list(scene.objects.values()),directory,data)

    def equator_index_mutation(self,change):
        with tempfile.TemporaryDirectory() as directory:
            _,data,_,_,_=equator_fixture(directory)
            manifest=write_audio_index(directory,data);v.load_audio(directory,manifest)
            changed=v.read_json(Path(directory)/'audio/native-audio.json');change(changed)
            path=Path(directory)/'audio/native-audio.json';path.write_text(json.dumps(changed),encoding='utf-8')
            manifest['native_audio']['sha256']=v.file_digest(path)
            with self.assertRaises(ValueError):v.load_audio(directory,manifest)

    def test_equator_schema_map_scene_and_interior_route(self):
        for change in (lambda d:d.update(schemaVersion=1),lambda d:d.update(mapKey='arena_bay5'),
                       lambda d:d.update(sourceScene='level188'),
                       lambda d:d['support'][0].update(mixerName='CommonAmbOutEffects')):
            with self.subTest(change=change):self.equator_index_mutation(change)

    def test_equator_inactive_dogs_cannot_join_or_replace_active_members(self):
        self.equator_index_mutation(lambda d:d['support'][0]['soundPlayers'].append({'resolved':'level56:970'}))
        self.equator_index_mutation(lambda d:d['support'][0]['soundPlayers'].__setitem__(0,{'resolved':'level56:975'}))
        self.equator_index_mutation(lambda d:d['support'][0]['soundPlayers'].pop())

    def test_equator_source_ancestry_and_enabled_consistency(self):
        self.equator_index_mutation(lambda d:d['players'][1]['transformChain'][-1].update(activeSelf=True))
        self.equator_index_mutation(lambda d:d['players'][0]['transformChain'][0].update(layer=3))
        self.equator_index_mutation(lambda d:d['players'][0].update(enabled=False))
        self.equator_index_mutation(lambda d:d['players'][0]['transformChain'][-1].update(activeSelf=1))
        self.equator_index_mutation(lambda d:d['players'][0]['transformChain'][-1].update(layer=True))

    def test_equator_original_electricity_loop_required(self):
        self.equator_index_mutation(lambda d:d['players'][0]['sourceAudio'].update(Loop=False))
        self.equator_bundle_mutation(lambda s,d,n,g,c:next(r for r in s.objects.values()
            if r.type.name=='AudioSource' and r.owner==g['electricity']).tree.update(Loop=False))

    def test_equator_built_dog_activation_and_layer_mutations(self):
        self.equator_bundle_mutation(lambda s,d,n,g,c:g['far_dogs2'].tree.update(m_IsActive=True))
        self.equator_bundle_mutation(lambda s,d,n,g,c:g['far_dogs1'].tree.update(m_Layer=2))

    def test_equator_built_group_order_member_and_missing_player(self):
        self.equator_bundle_mutation(lambda s,d,n,g,c:n['level56:780'].tree['_soundPlayers'].reverse())
        self.equator_bundle_mutation(lambda s,d,n,g,c:n['level56:780'].tree['_soundPlayers'].__setitem__(0,s.ref(n['level56:970'])))
        self.equator_bundle_mutation(lambda s,d,n,g,c:s.objects.pop(n['level56:965'].path_id))

    def test_equator_built_pcm_and_map_root_mutations(self):
        def corrupt(s,d,n,g,c):
            payload=bytearray(next(iter(c.samples.values())));payload[-1]^=1;c.samples={'fixture.wav':bytes(payload)}
        self.equator_bundle_mutation(corrupt)
        self.equator_bundle_mutation(lambda s,d,n,g,c:d.update(mapKey='arena_bay5'))
        self.equator_bundle_mutation(lambda s,d,n,g,c:next(r for r in s.objects.values()
            if r.type.name=='GameObject' and r.tree['m_Name']==v.MAPS['arena_equator']['audio']['root']).tree.update(m_Name=BAY5_AUDIO_ROOT))

    def test_physics_states_observe_disabled_and_trigger_colliders(self):
        scene=Scene();go=scene.go('Wall');native=scene.component(go,'BoxCollider',{'m_Enabled':1,'m_IsTrigger':False})
        expected=[{'type':'BoxCollider','enabled':True,'is_trigger':False}]
        self.assertEqual(v.physics_states(expected),v.physics_states([native],True))
        for field,value in [('m_Enabled',0),('m_IsTrigger',True)]:
            old=native.tree[field];native.tree[field]=value
            self.assertNotEqual(v.physics_states(expected),v.physics_states([native],True))
            native.tree[field]=old



# ---------------------------------------------------------------------------------------------
# Partial scenes: the whole-level ballistic source scan is scoped to the package scene
# ---------------------------------------------------------------------------------------------

def _owner_object(oid,parent=None,components=(),name=None):
    return {'id':oid,'parent_id':parent,'name':name or 'go'+oid,'active':True,'layer':0,'local_position':[0.,0.,0.],
            'local_rotation':[0.,0.,0.,1.],'local_scale':[1.,1.,1.],'components':list(components)}


def _box():
    return {'type':'BoxCollider','enabled':True,'is_trigger':False}


def _ballistic(component,owner):
    return {'type':'BallisticCollider','source':{'component_id':component,'object_id':owner}}


class PartialBallisticSourceWorld:
    """A synthetic Arena level pair: scene 7 is a full scene, scene 8 a partial one.

    Package scene 8 holds a carrier (10), a shelf (11: box + ballistic 201) and a screen (12: renderer only; its
    source BallisticCollider 202 is hidden on purpose). The source level also carries a BallisticCollider (203) on
    owner 99, which the package never imported. ``twin_carriers`` makes the carrier 10 the bare chain above an imported
    twin descendant: its source node owns a BallisticCollider (205) that the package deliberately lacks."""
    def __init__(self):
        self.full=[_owner_object('1',components=[_box(),_ballistic('101','1')])]
        self.part=[_owner_object('10'),
                   _owner_object('11','10',[_box(),_ballistic('201','11')]),
                   _owner_object('12','10',[{'type':'MeshRenderer'}])]
        self.source={7:[(101,1)],8:[(201,11),(202,12),(203,99)]}  # level -> [(component id, owner id)]
        self.orphans=[[12,202]]
        self.units=2;self.carriers=1;self.dropped=0;self.ballistics={7:1,8:1}
        self.rules={'roots:Root/Shelves':{'selected':2,'twin_skipped':3,'excluded':0,'descendants_added':0}}
        self.excluded=0;self.exclude_entries=0;self.added=0;self.twin_carriers=None
        self.digest=None;self.entry_objects=None;self.orphan_pins=None;self.dropped_pins=None

    def objects(self):return {'Arena_X_Full':self.full,'Arena_X_Part':self.part}

    def manifest(self):
        part_ids=[o['id'] for o in self.part]
        entry={'index':8,'objects':self.entry_objects if self.entry_objects is not None else len(part_ids),
               'units':self.units,'carriers':self.carriers,'rules':deepcopy(self.rules),
               'dropped_script_owners':self.dropped,'ballistic_orphans':deepcopy(self.orphans),
               'go_ids_sha256':self.digest or partial_scope.go_ids_digest(part_ids),
               'excluded':self.excluded,'exclude_entries':self.exclude_entries,'twin_descendants_added':self.added,
               'twin_descendant_paths':['Root/Twin/D%02d'%i for i in range(self.added)]}
        if self.twin_carriers is not None:entry['twin_carrier_ids']=list(self.twin_carriers)
        return {'source':{'map_key':'arena_x','arena_data_root':'unused-root','target_data_root':'unused-target'},
                'scenes':['Arena_X_Full','Arena_X_Part'],
                'native_ballistics':{'schema_version':1,'source_presets':{},'target_presets':{}},
                'partial_scenes':{'Arena_X_Part':entry}}

    def contract(self,manifest):
        entry=manifest['partial_scenes']['Arena_X_Part']
        raw={'schema_version':1,'map_key':'arena_x','scenes':{'7':'Arena_X_Full','8':'Arena_X_Part'},
             'ballistics':{str(i):n for i,n in self.ballistics.items()},'require_unsliced':True,
             'implicit_scene_identity':False,'features':None,'audio':None,
             'partial_scenes':{'Arena_X_Part':{'index':8,'objects':entry['objects'],'units':entry['units'],
                 'carriers':entry['carriers'],'orphans':len(entry['ballistic_orphans']) if self.orphan_pins is None else self.orphan_pins,
                 'dropped_script_owners':entry['dropped_script_owners'] if self.dropped_pins is None else self.dropped_pins,
                 'excluded':entry['excluded'],'exclude_entries':entry['exclude_entries'],
                 'twin_descendants_added':entry['twin_descendants_added'],
                 'rules':deepcopy(entry['rules'])}}}
        return L.parse_contract(raw,'x','world.json')

    def readers(self,level):
        return [SimpleNamespace(path_id=component,assets_file=SimpleNamespace(name='level'+str(level)),
                                type=SimpleNamespace(name='MonoBehaviour'),
                                get_raw_data=lambda owner=owner:struct.pack('<iq',0,owner)+bytes(24))
                for component,owner in self.source[level]]

    def native(self,reader):
        """What the source level says about the owner: the same hierarchy and the same physical colliders."""
        owner=str(struct.unpack_from('<iq',reader.get_raw_data())[1])
        for objects in self.objects().values():
            lookup={o['id']:o for o in objects}
            if owner in lookup:
                obj=lookup[owner]
                components=[SimpleNamespace(type=SimpleNamespace(name=c['type']),
                                            read_typetree=lambda:{'m_Enabled':1,'m_IsTrigger':0})
                            for c in obj['components'] if c['type'] in v.PHYSICS]
                return {'chain':v.package_chain(obj,lookup),'states':v.package_states(obj,lookup),
                        'active':obj['active'],'layer':obj['layer'],'components':components}
        return {'chain':[],'states':[],'active':True,'layer':0,'components':[]}

    def expected(self,reader):
        owner=str(struct.unpack_from('<iq',reader.get_raw_data())[1])
        return _ballistic(str(reader.path_id),owner)

    def run(self):
        """validate_ballistic_source on the world; returns normally when it accepts."""
        manifest=self.manifest();contract=self.contract(manifest)
        table=[{'name':n,'material':i,'index':i} for i,n in enumerate(v.MATERIALS)]
        env=lambda *paths:SimpleNamespace(objects=self.readers(int(Path(paths[0]).name[len('level'):])))
        import UnityPy
        with patch.dict(v.MAPS,{'arena_x':contract}),patch.object(v,'source_presets',return_value=table), \
             patch.object(UnityPy,'load',side_effect=env),patch.object(v,'ballistic_expected',lambda r,s,t:self.expected(r)), \
             patch.object(v,'script_identity',return_value=('BallisticCollider','EFT.Ballistics','Assembly-CSharp')), \
             patch.object(v,'unity_go',self.native):
            return v.validate_ballistic_source('unused-package',manifest,self.objects())


class PartialBallisticSourceTests(unittest.TestCase):
    def reject(self,world,pattern):
        with self.assertRaisesRegex(ValueError,pattern):world.run()

    def test_baseline_world_is_accepted(self):
        PartialBallisticSourceWorld().run()

    def test_a_ballistic_collider_outside_the_package_scene_is_not_part_of_the_package(self):
        world=PartialBallisticSourceWorld()
        self.assertIn((203,99),world.source[8])  # present in the level, owner 99 is no object of the package scene
        world.source[8].append((204,98))
        world.run()

    def test_a_full_scene_keeps_the_whole_level_scan(self):
        world=PartialBallisticSourceWorld()
        world.source[7].append((104,98))  # scene 7 is not partial: every component of its level must be in the package
        self.reject(world,'Arena_X_Full: missing ballistic component 104')

    def test_a_component_on_an_in_scope_owner_must_be_imported_or_listed_as_hidden(self):
        world=PartialBallisticSourceWorld()
        world.orphans=[]  # the screen's ballistic 202 is neither in the package nor listed
        self.reject(world,'missing ballistic component 202')

    def test_a_listed_orphan_must_exist_in_the_source(self):
        world=PartialBallisticSourceWorld()
        world.source[8].remove((202,12))
        self.reject(world,'hidden ballistic orphans differ from the source')

    def test_a_hidden_orphan_must_not_have_a_physical_collider(self):
        world=PartialBallisticSourceWorld()
        world.part[2]['components'].append(_box())  # the screen can take a collider: hiding it hides a real ballistic
        self.reject(world,'hidden ballistic orphan owner has a physical collider 202')

    def test_a_hidden_orphan_must_not_be_present_in_the_package(self):
        world=PartialBallisticSourceWorld()
        world.part[2]['components'].append(_ballistic('202','12'))
        self.reject(world,'hidden ballistic orphan is present in the package 202')

    def test_an_imported_component_missing_from_the_source_fails(self):
        world=PartialBallisticSourceWorld()
        world.source[8].remove((201,11))
        self.reject(world,'extra ballistic source identities')

    def test_the_scene_census_counts_the_imported_components_only(self):
        world=PartialBallisticSourceWorld()
        world.ballistics[8]=2  # the contract counts the 201 only, never the hidden 202 or the foreign 203
        self.reject(world,'ballistic source census changed')

    def test_orphan_owners_must_be_objects_of_the_scene(self):
        world=PartialBallisticSourceWorld()
        world.orphans=[[99,203]]
        self.reject(world,'ballistic orphan owners .* are not objects of the scene')

    def test_the_recorded_selection_hash_must_match_the_scene_objects(self):
        world=PartialBallisticSourceWorld()
        world.digest='0'*64
        self.reject(world,'go_ids_sha256 differs')
        world=PartialBallisticSourceWorld()
        world.part.append(_owner_object('13','10'))  # an object the recorded selection does not know
        world.entry_objects=3
        self.reject(world,'manifest records 3 objects, the package scene holds 4')

    def test_the_contract_pins_gate_the_scan(self):
        world=PartialBallisticSourceWorld()
        world.orphan_pins=0
        self.reject(world,'census differs from the map contract')
        world=PartialBallisticSourceWorld()
        world.dropped_pins=1  # the contract reviewed one dropped script owner, the manifest reports none
        self.reject(world,'census differs from the map contract')
        world=PartialBallisticSourceWorld()
        world.dropped=1  # a script owner the selection could not carry appeared; the reviewed pin is 0
        world.dropped_pins=0
        self.reject(world,'census differs from the map contract')
        world=PartialBallisticSourceWorld()
        world.rules={'roots:Root/Shelves':{'selected':2,'twin_skipped':3,'excluded':0,'descendants_added':0}}
        world.run()

    def twin_world(self):
        """Screen 12 is an imported twin descendant; carrier 10 is the bare chain above it, its source owns ballistic 205."""
        world=PartialBallisticSourceWorld()
        world.source[8].append((205,10))
        world.rules={'roots:Root/Shelves':{'selected':1,'twin_skipped':3,'excluded':0,'descendants_added':1}}
        world.added=1
        return world

    def test_the_source_components_of_a_twin_carrier_are_not_part_of_the_package(self):
        world=self.twin_world()
        world.twin_carriers=['10']
        world.run()  # 205 sits on an object of the package scene, but the package carries that object bare on purpose

    def test_without_the_twin_carrier_ids_the_carriers_source_component_is_demanded(self):
        world=self.twin_world()
        world.twin_carriers=[]  # the record says: no bare carrier remains, so owner 10 must hold its ballistic 205
        self.reject(world,'missing ballistic component 205')

    def test_imported_twin_descendants_without_a_carrier_record_are_refused(self):
        world=self.twin_world()  # twin_carriers stays None: the manifest block has no twin_carrier_ids at all
        self.reject(world,'lacks twin_carrier_ids')

    def test_twin_carriers_must_be_bare_objects_of_the_scene_without_orphans(self):
        world=self.twin_world()
        world.twin_carriers=['11']  # the shelf holds a collider and a ballistic in the package
        self.reject(world,'must be bare in the package')
        world=self.twin_world()
        world.twin_carriers=['99']
        self.reject(world,'are not objects of the scene')
        world=self.twin_world()
        world.twin_carriers=['12']  # the screen owns the hidden orphan 202
        world.part[2]['components']=[]
        self.reject(world,'cannot own hidden ballistic orphans')

    def test_twin_carriers_only_exist_beside_imported_twin_descendants(self):
        world=PartialBallisticSourceWorld()
        world.twin_carriers=['10']
        self.reject(world,'only emitted for imported twin descendants')

    def test_ballistic_owner_reads_the_header_only(self):
        good=SimpleNamespace(get_raw_data=lambda:struct.pack('<iq',0,77)+bytes(20))
        self.assertEqual(v.ballistic_owner(good),77)
        with self.assertRaisesRegex(ValueError,'truncated'):v.ballistic_owner(SimpleNamespace(get_raw_data=lambda:bytes(31)))
        with self.assertRaisesRegex(ValueError,'ABI'):v.ballistic_owner(SimpleNamespace(get_raw_data=lambda:struct.pack('<iq',1,77)+bytes(20)))
        with self.assertRaisesRegex(ValueError,'ABI'):v.ballistic_owner(SimpleNamespace(get_raw_data=lambda:struct.pack('<iq',0,0)+bytes(20)))


if __name__=='__main__':unittest.main()
