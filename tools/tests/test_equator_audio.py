"""Synthetic Equator activation/loop contracts; no source-derived audio fixtures."""
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


def graph():
    prefix='AmbientAudioSystem/AmbientPlayersGroup/CommonAmbientInGroup'
    players=[]
    for i in range(25):
        name='far_dogs1' if i==23 else 'far_dogs2' if i==24 else 'electricity' if i==0 else f'sound_{i}'
        active=i<23
        players.append({'id': f'player:{i}', 'kind': 'AmbientSoundPlayer',
                        'path': prefix+'/'+name, 'activeInHierarchy': active,
                        'transformChain': [node(),node(),node(),node(active)], 'enabled': True,
                        'sourceAudio': {'m_Enabled': 1, 'Loop': i==0},
                        'playOnAwake': False, 'forceSetMixerGroup': False, 'mixerGroup': {'pathId': 0},
                        'ambientBank': {'resolved': f'bank:{i%11}'}})
    support=[{'id': 'group:1', 'kind': 'AmbientSoundPlayerGroup', 'path': prefix,
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
        self.assertEqual(23,len(audio.validate_equator_states(data['players'],data['support'])))

    def test_activation_cannot_silently_start_dogs(self):
        data=graph(); dog=data['players'][-1]
        dog['activeInHierarchy']=True; dog['transformChain'][-1]['activeSelf']=True
        data['support'][0]['soundPlayers'].append({'resolved': dog['id']})
        with self.assertRaisesRegex(ValueError,'inactive source players'): audio.validate_graph(data)

    def test_group_cannot_include_inactive_dog(self):
        data=graph(); data['support'][0]['soundPlayers'].append({'resolved':'player:24'})
        with self.assertRaisesRegex(ValueError,'group coverage'): audio.validate_graph(data)

    def test_group_cannot_omit_duplicate_or_substitute_active_player(self):
        for mutation in ('omit','duplicate','substitute'):
            data=graph(); members=data['support'][0]['soundPlayers']
            if mutation=='omit': members.pop()
            elif mutation=='duplicate': members.append(copy.deepcopy(members[0]))
            else: members[-1]={'resolved':'player:24'}
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
            with self.subTest(mixer=mixer), self.assertRaisesRegex(ValueError,'indoor group'):
                audio.validate_graph(data)

    def test_electricity_loop_is_preserved_and_not_added_to_random_sources(self):
        for index in (0,1,24):
            data=graph(); data['players'][index]['sourceAudio']['Loop']^=True
            with self.subTest(index=index), self.assertRaisesRegex(ValueError,'source loop'):
                audio.validate_graph(data)

    def test_player_cannot_enable_unowned_start_or_mixer_policy(self):
        for field in ('playOnAwake','forceSetMixerGroup','mixerGroup'):
            data=graph(); data['players'][0][field]={'pathId':42} if field=='mixerGroup' else True
            with self.subTest(field=field), self.assertRaisesRegex(ValueError,'start/mixer policy'):
                audio.validate_graph(data)

    def test_unknown_map_schema_scene_and_source_census_fail(self):
        for key,value in (('mapKey','arena_bowl'),('schemaVersion',1),('sourceScene','level188')):
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
                audio.export(Path(directory)/'source',output,'arena_bowl')
            self.assertFalse(output.exists()); load.assert_not_called()


if __name__=='__main__': unittest.main()
