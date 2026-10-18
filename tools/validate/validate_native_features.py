"""Independent source/package/bundle proof for native features (Equator, Bay5, AirPit).

No extractor/importer is used as an oracle. Source MonoBehaviour payloads are
re-encoded from the package and compared byte for byte; native built fields and
resolved references are checked separately. Legacy packages remain unchanged.

The per-map census (scenes, ballistic colliders per scene, optional feature pins,
ambient-audio graph shape and states) is data, not code: one JSON file per map under
``tools/extract/native_contracts`` read by ``native_contract_loader`` (PLAN-AIRPIT item 3).
A map whose contract says ``audio: null`` is a ballistics-only package: it must carry no
``native_audio`` entry, ``native_features.required`` is exactly ``['ballistics']``, the
audio source/bundle proofs do not run, and the bundle must not contain native audio.

Partial scenes (plan record ``select``, manifest ``partial_scenes``, contract ``partial_scenes``) take only a
selected part of one Arena level file. ``map_contract`` pins their census (objects, units, carriers, hidden
ballistic orphans, dropped script owners, per-rule counts) exactly, and ``validate_ballistic_source`` scopes its whole-level scan of
that file to the owners of the package scene (tools/validate/partial_scope.py): a ``BallisticCollider`` outside
the scene is not part of the package, one the extractor hid because its owner has no physical collider must be
listed in ``ballistic_orphans`` and must really have none. A scene that is not partial keeps the whole-level scan.
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
import sys
import wave

_EXTRACT_DIR = str(Path(__file__).resolve().parents[1] / 'extract')
if _EXTRACT_DIR not in sys.path:  # native_contract_loader (+ common_ids) live next to the extractors
    sys.path.append(_EXTRACT_DIR)
from native_contract_loader import load_contracts  # noqa: E402
_VALIDATE_DIR = str(Path(__file__).resolve().parent)
if _VALIDATE_DIR not in sys.path:  # partial_scope lives next to this file
    sys.path.append(_VALIDATE_DIR)
import partial_scope  # noqa: E402

FIELDS = ('penetration_level', 'penetration_chance', 'ricochet_chance',
          'fragmentation_chance', 'trajectory_deviation_chance', 'trajectory_deviation')
TARGET_FIELDS = ('PenetrationLevel', 'PenetrationChance', 'RicochetChance',
                 'FragmentationChance', 'TrajectoryDeviationChance', 'TrajectoryDeviation')
MATERIALS = ('None Asphalt Body Cardboard Chainfence Concrete Fabric GarbageMetal GarbagePaper '
             'GenericSoft Glass GlassShattered Grate GrassHigh GrassLow Gravel MetalThin MetalThick '
             'Mud Pebbles Plastic Stone Soil SoilForest Tile Water WaterPuddle WoodThin WoodThick '
             'Tyre Rubber GenericHard BodyArmor Swamp Helmet GlassVisor HelmetRicochet MetalNoDecal Snow').split()
PLAYERS = {'AmbientSoundPlayer', 'SeasonAmbientSoundPlayer', 'LoopAmbientSoundPlayer'}
SUPPORT = {'AmbientSoundPlayerGroup', 'BezierSpline', 'AmbientSplineEmitterController',
           'SoundAmbientZoneCalculator', 'AmbientPlayerSplineMappedEmitter'}
PHYSICS = {'MeshCollider', 'BoxCollider', 'SphereCollider', 'CapsuleCollider'}
# map key -> contract (native_contract_loader schema: scenes, ballistics, features, audio, ...),
# read once from tools/extract/native_contracts/*.json; a malformed contract fails the import.
MAPS = load_contracts()
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


def map_contract(manifest):
    # A contract with implicit_scene_identity (released Bay5 schema 1 predates
    # source.map_key) is selected by its exact scene set, and only when the manifest
    # names no key at all; every other manifest must declare its key.
    key=manifest.get('source',{}).get('map_key')
    if key is None:
        scene_set=set(manifest.get('scenes',[]))
        implicit=[k for k,c in MAPS.items() if c['implicit_scene_identity'] and scene_set==set(c['scenes'].values())]
        key=implicit[0] if len(implicit)==1 else None
    require(key in MAPS,'unsupported or missing native map identity')
    contract=MAPS[key]
    require(len(manifest['scenes'])==len(contract['scenes']) and
            set(manifest['scenes'])==set(contract['scenes'].values()), 'native features require full '+key+' scene set')
    if contract['require_unsliced']:
        require(not manifest.get('slice'), key+' native features require unsliced scenes')
    check_partial_scene_pins(key,contract,manifest)
    return key,contract


def check_partial_scene_pins(key, contract, manifest):
    """The manifest's ``partial_scenes`` census against the contract's reviewed pins.

    Both directions fail closed: a contract that pins partial scenes needs exactly that set in the manifest, and a
    manifest that carries partial scenes needs a contract that pins them (a block nobody reviewed would otherwise
    narrow the source scans unchecked). Counts compare by exact type, rule names and counts included (the four
    per-rule counters selected / twin_skipped / excluded / descendants_added, and the scene-level excluded /
    exclude_entries / twin_descendants_added of the plan record's exclude list and twin-descendant import); the
    hidden ballistic orphans are pinned by their number (the pairs themselves are proven against the source in
    ``validate_ballistic_source``) and the hierarchy paths of the added twin descendants are review material that the
    contract does not pin."""
    try:
        entries=partial_scope.manifest_entries(manifest)
    except partial_scope.PartialScopeError as error:
        raise ValueError('Native features: '+str(error)) from error
    pins=contract.get('partial_scenes') or {}
    if not pins:
        require(not entries,key+' contract pins no partial scenes, but the manifest carries partial_scenes')
        return
    require(set(entries)==set(pins),'partial scenes differ from the map contract: manifest '+str(sorted(entries))
            +' != contract '+str(sorted(pins)))
    for name,pin in pins.items():
        entry=entries[name]
        census_keys=('index','objects','units','carriers','orphans','dropped_script_owners','excluded',
                     'exclude_entries','twin_descendants_added')
        actual={k:entry[k] for k in census_keys if k!='orphans'}
        actual['orphans']=len(entry['ballistic_orphans'])
        actual['rules']=entry['rules']
        require(all(same(actual[k],pin[k]) for k in census_keys),
                'partial scene '+name+' census differs from the map contract: '
                +str({k:actual[k] for k in census_keys})+' != '+str({k:pin[k] for k in census_keys}))
        require(sorted(actual['rules'])==sorted(pin['rules']),
                'partial scene '+name+' selection rules differ from the map contract: '+str(sorted(actual['rules']))
                +' != '+str(sorted(pin['rules'])))
        for rule,counts in pin['rules'].items():
            require(all(same(actual['rules'][rule][k],v) for k,v in counts.items()),
                    'partial scene '+name+' rule '+rule+' census differs from the map contract: '
                    +str(actual['rules'][rule])+' != '+str(counts))


def same(actual, expected):
    """Strict census equality: 1 is not True, 2.0 is not 2."""
    return type(actual) is type(expected) and actual==expected


def check_feature_pins(pins, static_decals, light_culling, raincover):
    """Contract census pins against the manifest summaries (PLAN-AIRPIT item 3).

    The summaries are optional for maps without pins; a map WITH pins must carry every one
    of them with exactly the reviewed numbers, so a missing or drifted feature fails closed."""
    if pins['static_decals']==0:  # the extractor omits the entry when a map has no decals
        require(static_decals is None,'static decal census differs from the map contract')
    else:
        require(isinstance(static_decals,dict) and same(static_decals.get('count'),pins['static_decals']),
                'static decal census differs from the map contract')
    require(isinstance(light_culling,dict) and all(same(light_culling.get(k),v) for k,v in pins['light_culling'].items()),
            'light_culling census differs from the map contract')
    require(isinstance(raincover,dict) and all(same(raincover.get(k),v) for k,v in pins['raincover'].items()),
            'raincover census differs from the map contract')


def pinned_component_census(pins):
    """Component type -> pinned count, for the types the scenes themselves must carry."""
    light=pins['light_culling']
    return {'StaticDeferredDecal':pins['static_decals'],
            'NavMeshModifier':pins['navmesh_modifiers']['modifiers'],
            'NavMeshModifierVolume':pins['navmesh_modifiers']['volumes'],
            'SmallPhysicsObject':pins['small_physics_objects'],
            'CullingLightObject':light['culling_light_object_count'],
            'VolumetricLight':light['volumetric_light_count'],
            'LightFlicker':light['light_flicker_count']}


def feature_enabled(manifest, scenes=None):
    present = {k for k in ('native_features','native_ballistics','native_audio') if k in manifest}
    if not present:
        if scenes is not None:
            require(not any(c['type']=='BallisticCollider' for objects in scenes.values() for o in objects for c in o['components']),
                    'unmarked ballistic components')
        return False
    require('native_features' in manifest and 'native_ballistics' in manifest, 'incomplete required feature contract')
    key,contract=map_contract(manifest)
    audio=contract['audio']
    # The contract decides whether audio is part of the package: all-or-nothing, never optional.
    if audio is None:
        require('native_audio' not in manifest, key+' contract declares no native audio, but the manifest carries native_audio')
    else:
        require('native_audio' in manifest, 'incomplete required feature contract')
    # PLAN-TWO-MAPS item 6: 'raincover' is an OPTIONAL extra native_features entry (extract_raincover.py) --
    # popped before the strict key-equality compare() below so packages with/without it both validate.
    features=dict(manifest['native_features']);raincover=features.pop('raincover',None)
    # PLAN-TWO-MAPS item 4: 'navmesh_areas' (extract_navmesh_modifiers.read_navmesh_areas: the source project's
    # NavMesh area table, index -> {name, cost}) is OPTIONAL in the same way.
    navmesh_areas=features.pop('navmesh_areas',None)
    # PLAN-TWO-MAPS follow-up: 'light_culling' (extract_light_culling.augment_scene's summed per-map
    # counts) is an OPTIONAL extra native_features entry in the same way -- it tags existing owner
    # GameObjects only, no new GameObjects are created, so extra_counts() below is untouched.
    light_culling=features.pop('light_culling',None)
    static_decals=features.pop('static_decals',None)
    compare(features, {'schema_version':1,'required':['ballistics']+([audio['feature']] if audio else [])}, 'feature contract')
    if raincover is not None:
        require(isinstance(raincover,dict) and raincover.get('schema_version')==1 and 'indoor_trigger_count' in raincover,
                'raincover feature summary shape')
    if navmesh_areas is not None:
        require(isinstance(navmesh_areas,dict) and len(navmesh_areas)>0
                and all(str(k).isdigit() and isinstance(a,dict) and isinstance(a.get('name'),str) and isinstance(a.get('cost'),(int,float))
                        for k,a in navmesh_areas.items())
                and navmesh_areas.get('0',{}).get('name')=='Walkable' and navmesh_areas.get('1',{}).get('name')=='Not Walkable',
                'navmesh_areas feature summary shape')
    if light_culling is not None:
        require(isinstance(light_culling,dict) and light_culling.get('schema_version')==1
                and all(isinstance(light_culling.get(k),int) and light_culling[k]>=0 for k in
                        ('culling_light_object_count','volumetric_light_count','light_flicker_count')),
                'light_culling feature summary shape')
    pins=contract['features']  # PLAN-AIRPIT item 3: reviewed census of the optional entries (None: shape rules only)
    if pins is not None:
        check_feature_pins(pins,static_decals,light_culling,raincover)
    if scenes is not None:
        decals=[c for objects in scenes.values() for o in objects for c in o['components'] if c['type']=='StaticDeferredDecal']
        if static_decals is not None:
            require(static_decals.get('schema_version')==1 and static_decals.get('count')==len(decals) and len(decals)>0,
                    'static decal census differs')
            require(all(c.get('schema_version')==1 and c.get('material_id') in manifest['materials'] for c in decals),
                    'static decal material/schema')
        else:
            require(not decals, 'unmarked static decal components')
        require(not any(c['type']=='WindowBreaker' for objects in scenes.values() for o in objects for c in o['components']), 'glass was deferred by the user')
        if pins is not None:
            wanted=pinned_component_census(pins)
            census=Counter(c['type'] for objects in scenes.values() for o in objects for c in o['components'] if c['type'] in wanted)
            compare({kind:census[kind] for kind in wanted},wanted,'native component census')
    return True


def audio_contract(key, contract):
    """The ambient-audio part of a map contract; a map declared ballistics-only has none."""
    require(contract['audio'] is not None, key+' contract declares no native audio')
    return contract['audio']


def check_audio_group_states(audio, data, grouped):
    """The contract's group coverage and state pins against the audio index (Equator-style
    schema-2 graphs; a schema-1 graph has ``states: null`` and only the coverage rule). Groups
    are pinned by leaf name (mixer and member count), loops by id and leaf name, never by order."""
    players=data['players']
    if audio['group_coverage']=='active_players':
        require(grouped==Counter(p['id'] for p in players if p['enabled'] and p['activeInHierarchy']),'audio active group coverage')
    else:
        require(grouped==Counter(p['id'] for p in players), 'audio group coverage')
    states=audio['states']
    if states is None:return
    require(sum(grouped.values())==states['grouped_players'],'audio grouped player count')
    compare({p['id']:p['path'].split('/')[-1] for p in players if not p['activeInHierarchy']},
            states['inactive_players'],'original inactive audio players')
    compare(sorted(p['id'] for p in players if not p['enabled']),sorted(states['disabled_players']),'disabled audio players')
    groups=[s for s in data['support'] if s['kind']=='AmbientSoundPlayerGroup']
    named={g['path'].split('/')[-1]:g for g in groups}  # the leaf name is the key the client and the importer route by
    require(len(named)==len(groups),'audio group leaf names are not unique')
    compare({n:g['mixerName'] for n,g in named.items()},{n:r['mixer'] for n,r in states['groups'].items()},'audio group mixer routes')
    require(all(g['enabled'] and g['activeInHierarchy'] for g in groups),'audio group route/state')
    # Coverage above proves every player is grouped once; these pin which group holds which players.
    compare({n:len(g['soundPlayers']) for n,g in named.items()},{n:r['players'] for n,r in states['groups'].items()},'audio group member counts')
    paths={p['id']:p['path'] for p in players}
    require(all(paths.get(m['resolved'],'').startswith(g['path']+'/') for g in groups for m in g['soundPlayers']),
            'audio group members outside their group hierarchy')
    compare({p['id']:p['path'].split('/')[-1] for p in players if p['sourceAudio']['Loop']},states['loops'],'original looping audio players')


def load_audio(package, manifest):
    key,contract=map_contract(manifest)
    audio=audio_contract(key,contract)
    meta = manifest['native_audio']
    require(meta['schema_version']==audio['schema'] and meta['file']=='audio/native-audio.json', 'audio index schema/path')
    path = child(package, meta['file'])
    require(file_digest(path)==meta['sha256'], 'audio index hash differs')
    data = read_json(path)
    require(data['schemaVersion']==audio['schema'] and data['mapKey']==key and data['sourceScene']==audio['scene'], 'audio source identity')
    require(Counter(x['kind'] for x in data['players'])==Counter(audio['players']), 'audio player census')
    require(Counter(x['kind'] for x in data['support'])==Counter(audio['support']), 'audio support census')
    for field, count in [('players',sum(audio['players'].values())),('banks',audio['banks']),('clips',audio['clips'])]:
        require(meta[field]==len(data[field])==count, 'audio count: ' + field)
    for records in (data['players'] + data['support'], data['clips']):
        require(len({x['id'] for x in records})==len(records), 'duplicate audio source identity')
    require(all(k==v['id'] for k,v in data['banks'].items()), 'bank key/identity disagreement')
    validate_audio_hierarchy(data)
    check_audio_group_states(audio,data,Counter(p['resolved'] for s in data['support'] for p in s.get('soundPlayers',[])))
    return data


def audio_states(item,schema):
    """Source-order ancestor states; schema 1 only supports active layer zero."""
    chain=item['transformChain']
    require(len(chain)==len(item['path'].split('/')),'audio hierarchy depth')
    if schema==1:return [(True,0)]*len(chain)
    require(schema==2,'unsupported audio hierarchy schema')
    for node in chain:
        require(type(node['activeSelf']) is bool and type(node['layer']) is int and 0<=node['layer']<=31,
                'invalid audio hierarchy state')
    return [(node['activeSelf'],node['layer']) for node in chain]


def validate_audio_hierarchy(data):
    ancestors={};schema=data['schemaVersion']
    for item in data['players']+data['support']:
        states=audio_states(item,schema)
        require(type(item['activeInHierarchy']) is bool and item['activeInHierarchy']==all(s[0] for s in states),
                'audio activeInHierarchy disagrees with original ancestors')
        parts=item['path'].split('/')
        require(all(parts),'empty audio hierarchy path')
        for index,node in enumerate(item['transformChain']):
            path='/'.join(parts[:index+1])
            if path in ancestors:compare_storage(node,ancestors[path],'shared audio source ancestor '+path)
            ancestors[path]=node


def raincover_extra_counts(manifest):
    """PLAN-TWO-MAPS item 6: RainCoverImporter authors, under the Scripts scene's ScriptsRoot,
    EnvironmentManager > TriggerGroup > N IndoorTrigger and (weather_obstacle only) WeatherObstacle >
    M DryPlane -- plain GameObjects with one Transform each and no collider or scene-embedded mesh
    (DryPlane.Mesh is Unity's built-in Quad). Sized from manifest.native_features.raincover, the
    summary extract_raincover.py wrote from the same raincover.json the importer reads."""
    raincover = manifest.get('native_features', {}).get('raincover')
    if raincover is None:
        return 0
    extra = 2 + int(raincover['indoor_trigger_count'])
    if raincover.get('weather_obstacle'):
        extra += 1 + int(raincover['dry_plane_count'])
    return extra


def extra_counts(package, manifest):
    if not feature_enabled(manifest):
        return {}
    raincover = raincover_extra_counts(manifest)
    _,contract = map_contract(manifest)
    if contract['audio'] is None:  # ballistics-only: the importer authors no audio root and no AudioSource
        return {'GameObject':raincover, 'Transform':raincover, 'AudioSource':0}
    data = load_audio(package, manifest)
    paths = set()
    for record in data['players'] + data['support']:
        parts = record['path'].split('/')
        paths.update('/'.join(parts[:i]) for i in range(1,len(parts)+1))
    return {'GameObject':len(paths)+1+raincover, 'Transform':len(paths)+1+raincover, 'AudioSource':len(data['players'])}


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


def physics_states(components, native=False):
    states=[]
    for component in components:
        kind=component.type.name if native else component['type']
        if kind not in PHYSICS:continue
        tree=component.read_typetree() if native else component
        states.append((kind,bool(tree['m_Enabled' if native else 'enabled']),
                       bool(tree['m_IsTrigger' if native else 'is_trigger'])))
    return Counter(states)


def ballistic_owner(reader):
    """Owner GameObject path id of a source BallisticCollider, from the header alone (no preset decoding)."""
    raw=reader.get_raw_data()
    require(len(raw)>=32,'truncated ballistic header')
    owner_file,owner=struct.unpack_from('<iq',raw)
    require(owner_file==0 and owner>0,'ballistic ABI changed')
    return owner


def validate_ballistic_source(package, manifest, scenes):
    import UnityPy
    root=Path(manifest['source']['arena_data_root']); target=Path(manifest['source']['target_data_root'])
    source_proof={};target_proof={}
    source_table=source_presets(root,source_proof); target_table=source_presets(target,target_proof)
    compare_storage(manifest['native_ballistics'],{'schema_version':1,'source_presets':source_proof,'target_presets':target_proof},'independent named preset provenance')
    for name in MATERIALS:
        require(next(p for p in target_table if p['name']==name)['material']==MATERIALS.index(name),'target named material mapping changed')
    key,contract=map_contract(manifest);total=0
    scopes=partial_scope.scopes_from_objects(manifest,scenes)  # {} unless the manifest has partial scenes
    for index,scene in contract['scenes'].items():
        objects={o['id']:o for o in scenes[scene]}
        scope=scopes.get(scene);hidden=set()
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
            key=str(reader.path_id)
            if scope is not None:  # partial scene: only components on package objects are in scope
                owner_id=ballistic_owner(reader)
                if not scope.covers(owner_id):continue
                if scope.is_orphan(owner_id,key):
                    # Hidden on purpose: the owner has no physical collider, in the source or in the package.
                    require(key not in actual,scene+': hidden ballistic orphan is present in the package '+key)
                    require(not physics_states(unity_go(reader)['components'],True) and
                            not any(x['type'] in PHYSICS for x in objects[str(owner_id)]['components']),
                            scene+': hidden ballistic orphan owner has a physical collider '+key)
                    hidden.add(key);continue
            found.add(key)
            require(key in actual,scene+': missing ballistic component '+key)
            owner,c=actual[key]; expected=ballistic_expected(reader,source_table,target_table)
            compare_storage(c,expected,scene+': ballistic '+key)
            require(owner['id']==expected['source']['object_id'],'ballistic source owner changed')
            native=unity_go(reader)
            compare(package_chain(owner,objects),native['chain'],'ballistic source hierarchy '+key)
            compare((owner['active'],owner['layer']),(native['active'],native['layer']),'ballistic source owner state')
            compare(package_states(owner,objects),native['states'],'ballistic source ancestor states')
            require(any(x['type'] in PHYSICS for x in owner['components']),'ballistic owner lacks imported physical collider')
            compare(dict(physics_states(owner['components'])),dict(physics_states(native['components'],True)),
                    'ballistic source physical collider states '+key)
        require(set(actual)==found,scene+': extra ballistic source identities')
        if scope is not None:
            require(hidden=={component for _,component in scope.orphans},scene+': hidden ballistic orphans differ from the source')
        require(len(found)==contract['ballistics'][index],scene+': ballistic source census changed');total+=len(found)
    require(total==sum(contract['ballistics'].values()),key+' ballistic source census changed')


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
    key,contract=map_contract(manifest);audio=audio_contract(key,contract);source_scene=audio['scene']
    require(data['mapKey']==key and data['sourceScene']==source_scene and data['schemaVersion']==audio['schema'],
            'audio source identity')
    validate_audio_hierarchy(data)
    require(file_digest(root/source_scene)==data['sourceSceneSha256'],'audio scene source bytes changed')
    schema_path=child(root,data['bankSchema']['path'])
    require(file_digest(schema_path)==data['bankSchema']['sha256'],'SoundBank type schema source changed')
    env=UnityPy.load(str(root/source_scene),str(root/'globalgamemanagers.assets'))
    scene=next(f for f in env.files.values() if hasattr(f,'objects') and Path(f.name).name==source_scene)
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
    # Same-named siblings get unique paths in the package (extract_bay5_audio.disambiguate_sibling_paths; Bowl's two
    # indoor_wind players); the source recheck applies the same rule before comparing names.
    from extract_bay5_audio import disambiguate_sibling_paths
    source_gos={item['id']:unity_go(components[item['id']]) for item in items}
    source_paths=disambiguate_sibling_paths({k:'/'.join(t[0] for t in reversed(g['chain'])) for k,g in source_gos.items()})
    for item in items:
        obj=components[item['id']]
        require(obj.get_raw_data()==audio_wire(item,item['kind']),'source audio payload '+item['id'])
        require(len(obj.get_raw_data())==item['byteSize'],'source audio byte size')
        go=source_gos[item['id']];chain=list(reversed(go['chain']))
        compare(source_paths[item['id']],item['path'],'source audio hierarchy names')
        authored_leaf=chain[-1][0]
        if source_paths[item['id']]!='/'.join(t[0] for t in chain):
            compare(item.get('sourceLeafName'),authored_leaf,'renamed sibling keeps its authored leaf name')
        else:
            require('sourceLeafName' not in item,'sourceLeafName on a player whose path was not renamed')
        expected_chain=[{'position':dict(zip('xyz',t[1])),'rotation':dict(zip('xyzw',t[2])),'scale':dict(zip('xyz',t[3]))} for t in chain]
        if audio['schema']==2:
            for node,(active,layer) in zip(expected_chain,reversed(go['states'])):
                node.update(activeSelf=active,layer=layer)
        compare_storage(item['transformChain'],expected_chain,'source audio hierarchy transforms/states')
        compare(audio_states(item,audio['schema']),list(reversed(go['states'])),'source audio hierarchy states')
        for p in pointers(item):
            if 'resolved' in p:
                actual=ptr(obj,p);require(identity(actual)==p['resolved'],'source audio reference target '+item['id'])
                if actual.type.name=='AudioClip':reached_clips.add(identity(actual))
                elif p['resolved'] in data['banks']:reached_banks.add(p['resolved'])
        if item['kind'] in PLAYERS:
            sources=[x for x in go['components'] if x.type.name=='AudioSource'];require(len(sources)==1,'source player AudioSource cardinality')
            compare_storage(sources[0].read_typetree(),item['sourceAudio'],'source AudioSource '+item['id'])
            forced=(audio.get('states') or {}).get('forced_mixer_players',{})
            if item['id'] in forced:  # pinned player forcing an Arena mixer the port cannot provide; routed like its group
                require(item.get('mixerPolicy')=='group' and item['mixerGroup']['pathId']!=0 and item['playOnAwake'] is False
                        and ptr(obj,item['mixerGroup']).read().m_Name==forced[item['id']]==item.get('mixerGroupName'),
                        'pinned forced-mixer player '+item['id'])
            else:
                require(item['mixerGroup']['pathId']==0 and item['playOnAwake'] is False and 'mixerPolicy' not in item,
                        'source mixer/start policy changed')
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
        compare_storage(obj.read_typetree(nodes=node,check_read=True),item['sourceTree'],'independent source bank typetree '+key)
        for environment in item['environments']:
            for distance in environment:
                for p in distance:
                    actual=ptr(obj,p);require(actual.type.name=='AudioClip' and identity(actual)==p['resolved'],'source bank clip link');reached_clips.add(identity(actual))
    require(reached_clips=={c['id'] for c in data['clips']},'unreachable/missing source clips')
    for clip in data['clips']:
        obj=source_object(clip['id']);require(obj.type.name=='AudioClip','source clip type')
        compare_storage(obj.read_typetree(),clip['metadata'],'source audio metadata '+clip['id'])
        compare(obj.read().m_Name,clip['name'],'source clip name')
        samples=obj.read().samples;require(len(samples)==1,'source clip decode cardinality')
        compare(check_wave(package,clip),wave_data(next(iter(samples.values()))),'source decoded audio '+clip['id'])


def validate_package(package,manifest):
    try:
        package=Path(package);scenes=scene_objects(package,manifest)
        if not feature_enabled(manifest,scenes):return
        validate_ballistic_source(package,manifest,scenes)
        if map_contract(manifest)[1]['audio'] is not None:  # ballistics-only maps have no audio graph to prove
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
    # a pinned forced-mixer player (mixerPolicy 'group') is built without the force, routed like its group
    expected={'m_Enabled':int(item['enabled']),'ForceSetMixerGroup':item['forceSetMixerGroup'] and item.get('mixerPolicy')!='group'}
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
        if len(matches)>1:
            # Siblings whose whole chain agrees within the float32 tolerance (Bowl's six Lum_lamp_intact lamps carry two
            # 'ballistic' children at the same pose, rotations 1e-7 apart, with different meshes): the component's own
            # built fields pick the twin; twins that agree in those too are interchangeable, so the first is taken and
            # the other package twin consumes the remaining one (candidates.remove below).
            wanted=ballistic_built_fields(component)
            def fields_ok(reader):
                try: check_fields(reader.read_typetree(),wanted,'twin'); return True
                except ValueError: return False
            matches=([m for m in matches if fields_ok(m[0])] or matches)[:1]
        require(len(matches)==1,'missing/ambiguous built ballistic owner '+component['source']['component_id'])
        reader,go=matches[0];candidates.remove(matches[0])
        compare((go['active'],go['layer']),(obj['active'],obj['layer']),'built ballistic owner state')
        compare(go['states'],states,'built ballistic ancestor state')
        require(any(c.type.name in PHYSICS for c in go['components']),'built ballistic owner physics absent')
        compare(dict(physics_states(go['components'],True)),dict(physics_states(obj['components'])),
                'built ballistic physical collider states '+component['source']['component_id'])
        check_fields(reader.read_typetree(),ballistic_built_fields(component),'built ballistic '+component['source']['component_id'])


def reject_undeclared_audio(readers):
    """A ballistics-only contract (audio: null) must not hide a native audio graph in the bundle."""
    found=sorted({script_identity(r)[0] for r in readers if r.type.name=='MonoBehaviour'} & (PLAYERS|SUPPORT|{'SoundBank'}))
    require(not found,'built bundle carries native audio components the map contract does not declare: '+', '.join(found))


def validate_audio_bundle(readers,package,data):
    """Check native graph identity, exact hierarchy, all serialized payload fields and links."""
    key=data['mapKey'];require(key in MAPS,'unsupported built audio map identity')
    audio=audio_contract(key,MAPS[key]);audio_root=audio['root']
    require(data['schemaVersion']==audio['schema'],'built audio schema/map disagreement')
    validate_audio_hierarchy(data)
    native=defaultdict(list)
    for reader in readers:
        if reader.type.name=='MonoBehaviour':
            kind=script_identity(reader)[0]
            if kind in PLAYERS|SUPPORT|{'SoundBank'}:
                require_script(reader,kind);native[kind].append(reader)
    expected_counts=Counter(i['kind'] for i in data['players']+data['support']);expected_counts['SoundBank']=len(data['banks'])
    compare({k:len(v) for k,v in native.items()},dict(expected_counts),'built native audio component census')
    roots=[r for r in readers if r.type.name=='GameObject' and r.read_typetree()['m_Name']==audio_root]
    require(len(roots)==1,'built native audio root cardinality')
    root=unity_go(roots[0]);identity_transform=([0.,0.,0.],[0.,0.,0.,1.],[1.,1.,1.])
    compare(root['chain'],[(audio_root,*identity_transform),('ScriptsRoot',*identity_transform)],'owned audio root ancestry')
    # AuthorScriptsScene activates ScriptsRoot after authoring; the audio child
    # remains inactive until the runtime driver has listener/season/mixers.
    require(root['states']==[(False,0),(True,0)],'audio root must remain inactive beneath the active ScriptsRoot')
    objects={};object_readers={};components={};go_cache={}
    for reader in readers:
        if reader.type.name!='GameObject':continue
        go=unity_go(reader)
        positions=[i for i,t in enumerate(go['chain']) if t[0]==audio_root]
        if not positions or positions[0]==0:continue
        path='/'.join(t[0] for t in reversed(go['chain'][:positions[0]]))
        require(path not in objects,'duplicate built audio hierarchy path '+path)
        objects[path]=go;object_readers[path]=reader
    expected_paths={};expected_states={}
    for item in data['players']+data['support']:
        names=item['path'].split('/')
        states=audio_states(item,audio['schema'])
        for i in range(1,len(names)+1):
            path='/'.join(names[:i]);chain=[]
            for n,t in zip(names[:i],item['transformChain'][:i]):
                chain.append((n,list(t['position'].values()),list(t['rotation'].values()),list(t['scale'].values())))
            wanted=list(reversed(chain))+root['chain']
            if path in expected_paths:compare(expected_paths[path],wanted,'shared audio ancestor')
            expected_paths[path]=wanted
            wanted_states=list(reversed(states[:i]))+root['states']
            if path in expected_states:compare(expected_states[path],wanted_states,'shared audio ancestor states')
            expected_states[path]=wanted_states
    require(objects.keys()==expected_paths.keys(),'missing/extra built audio hierarchy objects')
    for path,chain in expected_paths.items():
        compare(objects[path]['chain'],chain,'built audio transform '+path)
        require(objects[path]['states']==expected_states[path],'built audio hierarchy active/layer state '+path)
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
        if map_contract(manifest)[1]['audio'] is not None:
            validate_audio_bundle(readers,package,load_audio(package,manifest))
        else:
            reject_undeclared_audio(readers)
    except (KeyError,IndexError,TypeError,OSError,struct.error) as error:
        raise ValueError(f'Native features: invalid or incomplete built native data: {error}') from error
