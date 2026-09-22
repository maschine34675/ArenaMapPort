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


class Reader:
    def __init__(self,scene,kind,tree,owner=None,script=None):
        self.path_id=len(scene.objects)+1;self.assets_file=scene;self.type=SimpleNamespace(name=kind)
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


def audio_fixture(directory):
    scene=Scene();scripts=scene.go('ScriptsRoot',active=True);root=scene.go(v.AUDIO_ROOT,scripts,False)
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
    for item in players+support:item['transformChain']=[deepcopy(transform),deepcopy(transform)]
    data={'players':players,'support':support,'clips':[clip],'banks':{bank_id:bank}}
    return scene,data,native,gos,clip_reader


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

    def test_independent_wire_fixed_header_and_payload(self):
        item={'gameObject':{'fileId':0,'pathId':12},'enabled':False,'script':{'fileId':1,'pathId':99},'name':'',
              'soundPlayer':{'fileId':0,'pathId':31},'spreadRange':[.25,.75]}
        expected=struct.pack('<iqB3xiqi',0,12,0,1,99,0)+struct.pack('<iqff',0,31,.25,.75)
        self.assertEqual(v.audio_wire(item,'AmbientPlayerSplineMappedEmitter'),expected)
        item['spreadRange'][1]=.5
        self.assertNotEqual(v.audio_wire(item,'AmbientPlayerSplineMappedEmitter'),expected)

    def test_ballistic_same_count_field_owner_and_identity_changes(self):
        scene=Scene();go=scene.go('Wall');scene.component(go,'BoxCollider',{})
        c={'enabled':False,'target_material_id':27,'target_preset_id':27,'material':'WoodThin',
           'values':dict(zip(v.FIELDS,[3.,.6,.1,.2,.3,.04])),'source':{'component_id':'source-1'},'type':'BallisticCollider'}
        obj={'id':'1','parent_id':None,'name':'Wall','active':True,'layer':0,'local_position':[0.,0.,0.],
             'local_rotation':[0.,0.,0.,1.],'local_scale':[1.,1.,1.],'components':[c]}
        reader=scene.component(go,'MonoBehaviour',v.ballistic_built_fields(c),'BallisticCollider')
        with patch.object(v,'ptr',scene.deref):
            v.validate_ballistic_bundle(list(scene.objects.values()),{'scene':[obj]})
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
        self.audio_mutation(lambda s,d,n,g,c:set_root_state(s,v.AUDIO_ROOT,True))

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


if __name__=='__main__':unittest.main()
