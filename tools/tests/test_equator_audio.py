"""Synthetic Equator activation/loop contracts; no source-derived audio fixtures.

The pins the extractor checks come from tools/extract/native_contracts/equator.json (PLAN-AIRPIT
items 3 and 13); the synthetic graph below therefore carries the contract's source identities
(level56:965 electricity loop, level56:970/975 the two inactive far_dogs players).
The loop is pinned by leaf name as well as by id (the Unity importer and the client look it up as
'electricity'), and the single indoor group by name, mixer and member count.
"""
import copy
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import extract_bay5_audio as audio


def node(active=True, layer=0):
    return {'position': {'x': 0, 'y': 0, 'z': 0},
            'rotation': {'x': 0, 'y': 0, 'z': 0, 'w': 1},
            'scale': {'x': 1, 'y': 1, 'z': 1}, 'activeSelf': active, 'layer': layer}


# Source identities pinned by equator.json: the electricity loop and the two inactive dogs.
ELECTRICITY, DOG1, DOG2 = 'level56:965', 'level56:975', 'level56:970'


def graph():
    prefix='AmbientAudioSystem/AmbientPlayersGroup/CommonAmbientInGroup'
    players=[]
    for i in range(25):
        name='far_dogs1' if i==23 else 'far_dogs2' if i==24 else 'electricity' if i==0 else f'sound_{i}'
        active=i<23
        ident=DOG1 if i==23 else DOG2 if i==24 else ELECTRICITY if i==0 else f'level56:{100+i}'
        players.append({'id': ident, 'kind': 'AmbientSoundPlayer',
                        'path': prefix+'/'+name, 'activeInHierarchy': active,
                        'transformChain': [node(),node(),node(),node(active)], 'enabled': True,
                        'sourceAudio': {'m_Enabled': 1, 'Loop': i==0},
                        'playOnAwake': False, 'forceSetMixerGroup': False, 'mixerGroup': {'pathId': 0},
                        'ambientBank': {'resolved': f'bank:{i%11}'}})
    support=[{'id': 'level56:900', 'kind': 'AmbientSoundPlayerGroup', 'path': prefix,
              'transformChain': [node(),node(),node()], 'activeInHierarchy': True, 'enabled': True,
              'mixerName': 'CommonAmbInEffects', 'soundPlayers': [{'resolved': p['id']} for p in players[:23]]}]
    return {'schemaVersion': 2, 'mapKey': 'arena_equator', 'sourceScene': 'level56',
            'players': players, 'support': support,
            'banks': {f'bank:{i}': {'environments': [[[{'resolved': f'clip:{i}'}]]]} for i in range(11)},
            'clips': [{'id': f'clip:{i}'} for i in range(65)]}


class EquatorAudioTests(unittest.TestCase):
    def test_valid_graph_preserves_inactive_dogs_and_electricity_loop_without_mutation(self):
        data=graph(); before=copy.deepcopy(data)
        audio.validate_graph(data)
        self.assertEqual(before,data)
        equator=audio.audio_contract('arena_equator')
        self.assertEqual(23,len(audio.validate_audio_states(data['players'],data['support'],equator)))

    def test_activation_cannot_silently_start_dogs(self):
        data=graph(); dog=data['players'][-1]
        dog['activeInHierarchy']=True; dog['transformChain'][-1]['activeSelf']=True
        data['support'][0]['soundPlayers'].append({'resolved': dog['id']})
        with self.assertRaisesRegex(ValueError,'inactive source players'): audio.validate_graph(data)

    def test_group_cannot_include_inactive_dog(self):
        data=graph(); data['support'][0]['soundPlayers'].append({'resolved':DOG2})
        with self.assertRaisesRegex(ValueError,'group coverage'): audio.validate_graph(data)

    def test_group_cannot_omit_duplicate_or_substitute_active_player(self):
        for mutation in ('omit','duplicate','substitute'):
            data=graph(); members=data['support'][0]['soundPlayers']
            if mutation=='omit': members.pop()
            elif mutation=='duplicate': members.append(copy.deepcopy(members[0]))
            else: members[-1]={'resolved':DOG2}
            with self.subTest(mutation=mutation), self.assertRaisesRegex(ValueError,'group coverage'):
                audio.validate_graph(data)

    def test_hierarchy_state_cannot_be_replaced_by_component_enabled(self):
        data=graph(); data['players'][-1]['enabled']=False
        with self.assertRaisesRegex(ValueError,'component activation'): audio.validate_graph(data)
        data=graph(); data['players'][-1]['sourceAudio']['m_Enabled']=0
        with self.assertRaisesRegex(ValueError,'AudioSource activation'): audio.validate_graph(data)

    def test_derived_hierarchy_activation_is_checked(self):
        for player_index in (0,24):
            data=graph(); data['players'][player_index]['activeInHierarchy']^=True
            with self.subTest(player_index=player_index), self.assertRaisesRegex(ValueError,'activation mismatch'):
                audio.validate_graph(data)

    def test_per_node_states_and_layers_are_required_and_typed(self):
        for field,value in (('activeSelf',None),('activeSelf',1),('layer',None),('layer',True),('layer',-1),('layer',32)):
            data=graph(); leaf=data['players'][0]['transformChain'][-1]
            if value is None: leaf.pop(field)
            else: leaf[field]=value
            with self.subTest(field=field,value=value), self.assertRaisesRegex(ValueError,'hierarchy state'):
                audio.validate_graph(data)

    def test_shared_ancestor_state_transform_and_layer_must_agree(self):
        for field,value in (('activeSelf',False),('layer',2),('position',{'x':1,'y':0,'z':0})):
            data=graph(); data['players'][1]['transformChain'][0][field]=value
            with self.subTest(field=field), self.assertRaisesRegex(ValueError,'shared audio hierarchy'):
                audio.validate_graph(data)

    def test_malformed_paths_and_inactive_group_are_rejected(self):
        for mutation in ('path','chain','group'):
            data=graph()
            if mutation=='path': data['players'][0]['path']=data['players'][0]['path'].replace('/','//',1)
            elif mutation=='chain': data['players'][0]['transformChain'].pop()
            else: data['support'][0]['enabled']=False
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): audio.validate_graph(data)

    def test_indoor_mixer_cannot_use_exterior_route(self):
        for mixer in ('CommonAmbOutEffects','CommonAmbOutBypass',None):
            data=graph(); data['support'][0]['mixerName']=mixer
            with self.subTest(mixer=mixer), self.assertRaisesRegex(ValueError,'group mixer contract'):
                audio.validate_graph(data)

    def test_electricity_loop_is_preserved_and_not_added_to_random_sources(self):
        for index in (0,1,24):
            data=graph(); data['players'][index]['sourceAudio']['Loop']^=True
            with self.subTest(index=index), self.assertRaisesRegex(ValueError,'source loop'):
                audio.validate_graph(data)
        data=graph(); data['players'][1]['sourceAudio']['Loop']=2
        with self.assertRaisesRegex(ValueError,'source loop'): audio.validate_graph(data)

    def test_loop_player_leaf_name_is_pinned(self):
        # Review of item 13: the old validator tied the loop to the leaf 'electricity'; the id-only pin did not.
        # Bay5AudioImporter and the client both look the looping source up by that name.
        for leaf in ('electricityx', 'Electricity', 'electric', 'sound_0'):
            data=graph(); player=data['players'][0]
            player['path']=player['path'].rsplit('/',1)[0]+'/'+leaf
            with self.subTest(leaf=leaf), self.assertRaisesRegex(ValueError,'source loop'):
                audio.validate_graph(data)
        # the loop moved to another player that is named 'electricity' is a different source as well
        data=graph(); data['players'][0]['sourceAudio']['Loop']=False; data['players'][1]['sourceAudio']['Loop']=True
        data['players'][1]['path']=data['players'][1]['path'].rsplit('/',1)[0]+'/electricity'
        with self.assertRaisesRegex(ValueError,'source loop'): audio.validate_graph(data)

    def test_group_is_pinned_by_name_mixer_and_member_count(self):
        data=graph(); group=data['support'][0]
        group['path']=group['path']+'Renamed'
        with self.assertRaisesRegex(ValueError,'group mixer contract'): audio.validate_graph(data)
        # a member that does not sit under the group keeps coverage and the count intact but is a changed source
        data=graph(); player=data['players'][1]
        player['path']='AmbientAudioSystem/AmbientPlayersGroup/CommonAmbientOutGroup/'+player['path'].rsplit('/',1)[-1]
        with self.assertRaisesRegex(ValueError,'audio group membership'): audio.validate_graph(data)
        pins=audio.audio_contract('arena_equator')['states']
        self.assertEqual({'CommonAmbientInGroup':{'mixer':'CommonAmbInEffects','players':23}},pins['groups'])
        self.assertEqual({'level56:965':'electricity'},pins['loops'])

    def test_pins_are_source_identities_not_leaf_names(self):
        # equator.json pins ids: a player renamed in the source with the same leaf name is a changed source.
        for index,pattern in ((24,'inactive source players'),(0,'source loop')):
            data=graph(); old=data['players'][index]['id']; data['players'][index]['id']='level56:1'
            for group in data['support']:
                group['soundPlayers']=[{'resolved':'level56:1' if p['resolved']==old else p['resolved']} for p in group['soundPlayers']]
            with self.subTest(index=index), self.assertRaisesRegex(ValueError,pattern):
                audio.validate_graph(data)

    def test_player_cannot_enable_unowned_start_or_mixer_policy(self):
        for field in ('playOnAwake','forceSetMixerGroup','mixerGroup'):
            data=graph(); data['players'][0][field]={'pathId':42} if field=='mixerGroup' else True
            with self.subTest(field=field), self.assertRaisesRegex(ValueError,'start/mixer policy'):
                audio.validate_graph(data)

    def test_unknown_map_schema_scene_and_source_census_fail(self):
        for key,value in (('mapKey','arena_prison'),('schemaVersion',1),('sourceScene','level188')):
            data=graph(); data[key]=value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError,'identity'): audio.validate_graph(data)
        for collection in ('players','support','clips','banks'):
            data=graph()
            if collection=='banks': data[collection].pop('bank:0')
            else: data[collection].pop()
            with self.subTest(collection=collection), self.assertRaisesRegex(ValueError,'census'): audio.validate_graph(data)

    def test_inactive_assets_are_still_required(self):
        data=graph(); data['players'][-1]['ambientBank']['resolved']='missing'
        with self.assertRaisesRegex(ValueError,'unresolved player bank'): audio.validate_graph(data)
        data=graph(); data['banks']['bank:10']['environments'][0][0][0]['resolved']='missing'
        with self.assertRaisesRegex(ValueError,'unresolved bank clip'): audio.validate_graph(data)

    def test_no_map_can_write_inside_source_installation(self):
        with tempfile.TemporaryDirectory() as source:
            for map_key in ('arena_bay5','arena_equator'):
                for output in (Path(source),Path(source)/'nested'):
                    with self.subTest(map_key=map_key,output=output), self.assertRaises(ValueError):
                        audio.export(source,output,map_key)
            self.assertEqual([],list(Path(source).iterdir()))

    def test_unknown_map_is_rejected_before_loading_or_creating_output(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(audio.UnityPy,'load') as load:
            output=Path(directory)/'package'
            with self.assertRaisesRegex(ValueError,'unsupported native audio map'):
                audio.export(Path(directory)/'source',output,'arena_prison')
            self.assertFalse(output.exists()); load.assert_not_called()

    def test_map_whose_contract_declares_no_audio_is_rejected_the_same_way(self):
        contracts={'arena_x':{'audio':None}}
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(audio.UnityPy,'load') as load, \
             mock.patch.object(audio,'load_contracts',return_value=contracts):
            output=Path(directory)/'package'
            with self.assertRaisesRegex(ValueError,'unsupported native audio map'):
                audio.export(Path(directory)/'source',output,'arena_x')
            self.assertFalse(output.exists()); load.assert_not_called()
            data=graph(); data['mapKey']='arena_x'
            with self.assertRaisesRegex(ValueError,'audio package identity'): audio.validate_graph(data)

    def test_malformed_contract_is_not_reported_as_an_unsupported_map(self):
        class Broken(ValueError): pass
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(audio,'load_contracts',side_effect=Broken('bad contract')):
            with self.assertRaisesRegex(Broken,'bad contract'):
                audio.export(Path(directory)/'source',Path(directory)/'package','arena_equator')
            with self.assertRaisesRegex(Broken,'bad contract'): audio.validate_graph(graph())

    def test_extractor_pins_reproduce_the_retired_literals(self):
        # What extract_bay5_audio hard-coded before the contract data: (schema, scene), player census,
        # (banks, clips, support records) and the group coverage rule. Frozen as the golden reference.
        legacy={'arena_equator':(2,'level56',{'AmbientSoundPlayer':25},(11,65,1),'active_players'),
                'arena_bay5':(1,'level188',{'AmbientSoundPlayer':17,'SeasonAmbientSoundPlayer':6,'LoopAmbientSoundPlayer':1},(13,106,6),'all_players')}
        for key,(schema,scene,players,counts,coverage) in legacy.items():
            pins=audio.audio_contract(key)
            with self.subTest(key=key):
                self.assertEqual((schema,scene,players,coverage),(pins['schema'],pins['scene'],pins['players'],pins['group_coverage']))
                self.assertEqual(counts,(pins['banks'],pins['clips'],sum(pins['support'].values())))
        self.assertIsNotNone(audio.audio_contract('arena_equator')['states'])
        self.assertIsNone(audio.audio_contract('arena_bay5')['states'])


if __name__=='__main__': unittest.main()
