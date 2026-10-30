"""Recover native acoustic topology from the user's Arena installation.

Arena adds a cached OBB array to rooms and an extra flag to trigger areas. Both
are decoded and retained as provenance; EFT uses the authored box hierarchy.
Every raw component is consumed exactly. BSG-derived output stays in work/dist.

Which scene (PLAN-AIRPIT item 4). Equator (level56) and Bay5 (level188) keep the table
below. A plan-driven map names its ambient Sound scene by the ``sound`` role on an
``excluded_scenes`` record of tools/extract/plans/<stem>.json, so the next map needs no
edit here: ``--map arena_airpit`` reads level39 (Arena_Airpit_Sound). Measured against the
live 0.6.0 install, AirPit gives 24 rooms, 94 portals, 78 audio trigger areas and a
481,738 B bake (276 room pairs, 16,304 routes) with one room-tone clip.

Iceberg (PLAN-ICEBERG item 11; ``--map arena_iceberg`` reads level259, Arena_Sanatorium_Sound) needs two reviewed rules that no other
map uses, both documented at their code and in docs/progress/iceberg-release.md: ten mirrored AudioTriggerArea volumes (a negative local
scale; ``mirrored_volume_nodes``, proven against Arena's own signed OBB record and the room bounds Unity cached) and 533 of 17,751
baked routes that use the portal ID 66 for a second doorway (``REVIEWED_BAKE_REPAIRS``, ``spatial_audio_bake.repair_portal_ids``; the
shipped ``source.audiobakedata`` keeps its size and name and differs from Arena's file in those 533 IDs, the export records the
original SHA-256 and the rewrite counts under ``bake_repair``). A map without a reviewed rule keeps the strict checks unchanged.

    python tools/extract/extract_spatial_audio.py                       (equator + bay5, as before)
    python tools/extract/extract_spatial_audio.py --map arena_airpit    (work/extract/spatial-audio/arena_airpit)

Name the map you want. Without ``--map`` the script rewrites the accepted arena_equator and
arena_bay5 directories from whatever Arena install ``--source`` points at. Against the 0.6.0
install that changes nine provenance leaves per map (source scene and clip hashes, six
mixer_effects hashes, ``systems[0].location_info.id``) while the graph, bake and room-tone WAVs
stay identical; the accepted inputs were exported from 0.5.4. Use ``--output`` to write
elsewhere when only a comparison is wanted.
"""
import sys,json,collections,struct,hashlib,math,argparse,shutil,re
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parent))
from extract_bay5_audio import Reader,deref,script,identity,hierarchy,validate_wav
from source_plan import PLAN_ROOT,PlanError,load_plan,read_build_scenes,scene_for_role,verify_scene_mapping
import UnityPy
from spatial_audio_bake import parse_bake,repair_portal_ids,RouteGraphError
import room_reverb
ROOT=Path('D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data')

def short(r):
 v=r.take('h');r.align();return v

def refid(obj,p,expected=None):
 o=deref(obj,p)
 if o is None:return None
 if expected and ((script(o) if o.type.name=='MonoBehaviour' else o.type.name)!=expected):raise ValueError(('wrong ref',identity(obj),p,expected))
 if Path(o.assets_file.name).name!=Path(obj.assets_file.name).name:raise ValueError(('crossscene ref',p))
 return o.path_id

def localref(r,expected=None):return refid(r.obj,r.ptr(),expected)
def refs(r,expected=None):return [localref(r,expected) for _ in range(r.count())]

def ambient(r):
 d={k:r.take('f') for k in ('fade_in_seconds','fade_out_seconds','room_tone_volume')}
 for key in ('room_tone',):
  p=r.ptr();o=deref(r.obj,p,'AudioClip');d[key]=identity(o) if o else None
 for k in ('outdoor_ambient_volume','outdoor_ambient_high_cut_freq'):d[k]=r.take('f')
 p=r.ptr();o=deref(r.obj,p);d['season_sound_preset']=identity(o) if o else None
 d['use_volume_range']=r.boolean();d['precipitation_volume']=r.take('f');d['precipitation_volume_range']=r.take('ff')
 d['reverb_preset']=r.string();d['reverb_send_volume']=r.take('f')
 return d

def parse(obj,kind):
 r=Reader(obj);h=r.header();d={'id':obj.path_id,'node_id':localref_from_header(obj,h),'enabled':h['enabled'],'raw_size':len(r.data)}
 if kind=='AudioTriggerArea':
  d['source_unknown_flag']=r.boolean();d['collider_id']=localref(r,'BoxCollider');d['validate_player_center_inside']=r.boolean()
 elif kind=='SpatialAudioPortal':
  d['transmission']=r.take('f');d['depth']=r.take('f');d['portal_name']=r.string();d['fit_to_geometry']=r.boolean();d['auto_rotate']=r.boolean();d['portal_type']=r.take('i');d['state']=r.take('B');r.align()
  d['collider_id']=localref(r,'BoxCollider');d['traversal_max_cost']=r.take('f');d['to_outdoor']=r.boolean();d['open_envelope']=r.curve();d['close_envelope']=r.curve();d['open_fade_time']=r.take('f');d['close_fade_time']=r.take('f');d['portal_id']=short(r);d['connected_room_ids']=refs(r,'SpatialAudioRoom');d['door_id']=r.string()
 elif kind=='SpatialAudioRoom':
  d['priority']=r.take('i');d['wall_occlusion']=r.take('f');d['area_ids']=refs(r,'AudioTriggerArea');d['outdoor']=r.boolean();d['isolated']=r.boolean();d['type']=r.take('i');d['fit_to_geometry']=r.boolean()
  d['connections']=[{'room_id':localref(r,'SpatialAudioRoom'),'portal_ids':refs(r,'SpatialAudioPortal')} for _ in range(r.count())]
  d['room_id']=short(r);d['room_size']=r.take('f');d['bounds']={'center':r.take('fff'),'extents':r.take('fff')}
  # New Arena cached OBB list, absent in current EFT. Check against area collider hierarchy below.
  d['source_area_obbs']=[{'center':r.take('fff'),'rotation':r.take('ffff'),'extents':r.take('fff')} for _ in range(r.count())]
  d['ambient_data']=ambient(r);d['only_wired']=r.boolean()
 elif kind=='SpatialAudioCrossSceneGroup':
  n=r.count();d['cross_scene_guid']=[r.take('B') for _ in range(n)];r.align();d['room_ids']=refs(r,'SpatialAudioRoom');d['portal_ids']=refs(r,'SpatialAudioPortal')
 elif kind=='SpatialAudioSystem':
  d['pools_config']=dict(zip(('parallel_propagation_calculators_pre_cache_count','combined_calculators_pre_cache_count','transmission_calculators_pool_count','pending_complete_calculators_count'),r.take('iiii')))
  o=deref(obj,r.ptr());d['occlusion_settings']=identity(o)
  o=deref(obj,r.ptr());d['location_info']=parse_location(o)
 else:raise ValueError(kind)
 r.done();return d

def localref_from_header(obj,h):return refid(obj,h['gameObject'],'GameObject')
def parse_location(obj):
 r=Reader(obj);h=r.header();d={'id':identity(obj),'name':h['name'],'relative_bake_data_path':r.string()}
 keys=('max_depth_indoor_to_indoor','max_depth_indoor_to_outdoor','max_depth_outdoor_to_outdoor','max_distance_outdoor_to_outdoor','max_distance_indoor_to_outdoor','max_distance_indoor_to_indoor')
 d['bake_settings']=dict(zip(keys,r.take('iiiiii')))
 for k in ('rooms_count','portals_count','routes_count','room_pairs_count','max_routes_count','max_route_portals_count'):d[k]=r.take('I')
 r.done();return d

# Legacy maps; every other map reads the sound scene from its source plan (scene_for_map).
LEGACY_SOUND_SCENES={'arena_equator':'level56','arena_bay5':'level188'}
DEFAULT_MAPS=('arena_equator','arena_bay5')
MAP_KEY=re.compile(r'arena_[a-z0-9_]+')

def plan_path(key):
 """tools/extract/plans/<stem>.json for arena_<stem>."""
 if not MAP_KEY.fullmatch(key):raise ValueError('invalid map key %r'%(key,))
 return PLAN_ROOT/(key[len('arena_'):]+'.json')

def scene_for_map(key,plan=None):
 """Level file name of the map's ambient Sound scene.

 Legacy maps use the table. Any other map needs its source plan with a ``sound`` scene role;
 a missing plan or role is an error, never a guessed index."""
 if plan is None and key in LEGACY_SOUND_SCENES:return LEGACY_SOUND_SCENES[key]
 if plan is None:plan=load_plan(plan_path(key))
 if plan['key']!=key:raise PlanError('plan %r does not describe map %r'%(plan['key'],key))
 return 'level%d'%scene_for_role(plan,'sound')['index']

# Maps whose original route table may be rewritten (spatial_audio_bake.repair_portal_ids), each pinned to the exact bake, the
# portal IDs the rule names and its outcome. A different bake (an Arena update), a different number of repaired routes or a
# different result stops the export until a human has reviewed the new data; every other map keeps the strict check alone.
#  arena_iceberg: two portals carried the ID 66 when Arena baked Sanatorium; the scene ships one. 533 of 17,751 routes use 66 for
#  the elevators <-> boiler doorway the scene knows as portal 30 (docs/progress/iceberg-release.md, 2026-10-30 "Spatial acoustics").
REVIEWED_BAKE_REPAIRS={
 'arena_iceberg':{'portal_ids':(66,),'original_sha256':'58995949130c5a2766c7a98f16850e3bbd934702902d42a25b2fd0db946ffd3f',
  'routes_repaired':533,'rewrites':{'66->30':533},'repaired_sha256':'e7650b6e673a76216d89eab8ea7612a50a49a5f44ea139d364da53dfce775169'},
}

def parse_or_repair_bake(key,bake,graph,rules=None):
 """(bake bytes to ship, parse_bake result, repair report or None). The strict parse_bake runs first and stays the only judge: a
 map without a reviewed rule, and any failure other than the route/graph pairing, raises exactly as before."""
 rules=REVIEWED_BAKE_REPAIRS if rules is None else rules
 try:return bake,parse_bake(bake,graph),None
 except RouteGraphError:
  rule=rules.get(key)
  if rule is None:raise
 digest=hashlib.sha256(bake).hexdigest()
 if digest!=rule['original_sha256']:
  raise ValueError('the bake of %s is not the one the route repair was reviewed for (sha256 %s); review the new data first'%(key,digest))
 fixed,report=repair_portal_ids(bake,graph,rule['portal_ids'])
 if (report['routes_repaired'],report['rewrites'],report['repaired']['sha256'])!=(rule['routes_repaired'],rule['rewrites'],rule['repaired_sha256']):
  raise ValueError('the route repair of %s gave a result other than the reviewed one: %s'%(key,json.dumps(report,sort_keys=True)))
 return fixed,parse_bake(fixed,graph),report

def export(source, output, key, scene=None):
 ROOT,OUT=Path(source).resolve(),Path(output).resolve()
 if ROOT==OUT or ROOT in OUT.parents:raise ValueError('output must be outside source installation')
 if scene is None:scene=scene_for_map(key)
 for key,scene in [(key,scene)]:
  env=UnityPy.load(str(ROOT/scene),str(ROOT/'globalgamemanagers.assets'));f=env.files[str(ROOT/scene)]
  result={'schema_version':1,'map_key':key,'source_scene':scene,'nodes':[],'colliders':[],'rooms':[],'portals':[],'areas':[],'groups':[],'systems':[]}
  slots={'SpatialAudioRoom':'rooms','SpatialAudioPortal':'portals','AudioTriggerArea':'areas','SpatialAudioCrossSceneGroup':'groups','SpatialAudioSystem':'systems'}
  relevant_nodes=set();relevant_colliders=set()
  for o in f.objects.values():
   if o.type.name!='MonoBehaviour':continue
   kind=script(o)
   if kind not in slots:continue
   try:d=parse(o,kind)
   except Exception as e:print(scene,kind,o.path_id,'FAIL',repr(e));raise
   h=hierarchy(o);d['path']=h['path'];d['active_in_hierarchy']=h['activeInHierarchy'];result[slots[kind]].append(d);relevant_nodes.add(d['node_id'])
   if 'collider_id' in d:relevant_colliders.add(d['collider_id'])
  for pid in sorted(relevant_colliders):
   o=f.objects[pid];t=o.read_typetree();node_id=t['m_GameObject']['m_PathID'];relevant_nodes.add(node_id)
   result['colliders'].append({'id':pid,'node_id':node_id,'enabled':bool(t['m_Enabled']),'is_trigger':bool(t['m_IsTrigger']),'center':t['m_Center'],'size':t['m_Size']})
  def ensure_node(pid):
   if any(x['id']==pid for x in result['nodes']):return
   o=f.objects[pid];t=o.read_typetree();trs=[f.objects[c['component']['m_PathID']] for c in t['m_Component'] if f.objects[c['component']['m_PathID']].type.name=='Transform'];assert len(trs)==1
   tr=trs[0].read_typetree();parent=tr['m_Father']['m_PathID'];parent_go=f.objects[parent].read_typetree()['m_GameObject']['m_PathID'] if parent else None
   if parent_go:ensure_node(parent_go)
   result['nodes'].append({'id':pid,'parent_id':parent_go,'name':t['m_Name'],'active_self':bool(t['m_IsActive']),'layer':t['m_Layer'],'position':tr['m_LocalPosition'],'rotation':tr['m_LocalRotation'],'scale':tr['m_LocalScale']})
  for pid in sorted(relevant_nodes):ensure_node(pid)
  validate_graph(result)
  result['mixer_effects']=room_reverb.extract(f,ROOT,result)
  info=result['systems'][0]['location_info']
  bakepath=ROOT/'StreamingAssets'/Path(info['relative_bake_data_path'].replace('\\','/'))
  bake=bakepath.read_bytes()
  bake,result['bake'],repair=parse_or_repair_bake(key,bake,result)
  result['bake']['file']='source.audiobakedata'
  if repair:result['bake_repair']=repair
  result['source_scene_sha256']=hashlib.sha256((ROOT/scene).read_bytes()).hexdigest()
  result['clips']=[]
  clipfiles={}
  for cid in sorted({r['ambient_data']['room_tone'] for r in result['rooms']} - {None}):
   asset,pid=cid.split(':')
   clipenv=UnityPy.load(str(ROOT/asset));obj=clipenv.files[str(ROOT/asset)].objects[int(pid)]
   clip=obj.read();samples=clip.samples
   if len(samples)!=1:raise ValueError('room tone must decode to one WAV')
   raw=next(iter(samples.values()));filename=cid.replace(':','_')+'.wav'
   decoded=validate_wav(raw,obj.read_typetree(),filename)
   metadata=obj.read_typetree();resource=metadata['m_Resource']
   with (ROOT/resource['m_Source']).open('rb') as stream:
    stream.seek(resource['m_Offset']);compressed=stream.read(resource['m_Size'])
   if len(compressed)!=resource['m_Size']:raise ValueError('truncated source room tone resource')
   result['clips'].append({'id':cid,'name':clip.m_Name,'decoded':decoded,
      'source_asset_sha256':hashlib.sha256((ROOT/asset).read_bytes()).hexdigest(),
      'source_resource_slice_sha256':hashlib.sha256(compressed).hexdigest(),
      'source_metadata':metadata})
   clipfiles[filename]=raw
  payload=json.dumps(result,indent=2,allow_nan=False).encode('utf-8')
  OUT.mkdir(parents=True,exist_ok=True)
  for name,raw in clipfiles.items():(OUT/name).write_bytes(raw)
  (OUT/'source.audiobakedata').write_bytes(bake)
  tmp=OUT/'spatial-audio.json.tmp';tmp.write_bytes(payload);tmp.replace(OUT/'spatial-audio.json')
  return result


def mirrored_volume_nodes(data):
 """Node IDs that may carry a NEGATIVE local scale: the own node of an AudioTriggerArea volume, when nothing else hangs on it
 (no child node, no room, portal, group or system component on the node itself).

 Arena authors some trigger volumes mirrored (a scale of -1.467 on X): Iceberg has ten, in the rooms corridor_2_1 and coffe_hall
 (docs/progress/iceberg-release.md). A box is symmetric about its centre, so the mirrored world box is the one of the absolute
 scale around the mirrored centre. Unity's BoxCollider.bounds, the trigger shape and the InverseTransformPoint test of
 AIUtility.PointInOABB (EFT's 'player centre inside' check) all work on the transform, and AcousticVolumeRules.cs in the client lets
 exactly this node class keep its sign. A mirrored axis must also have a collider centre of 0 on that axis (the reflection then maps the
 box onto itself, so the sign cannot move it). validate_graph still demands that Arena's cached OBB of the room matches this box
 (centre and signed extents, 0.005 m; Arena caches lossyScale x size / 2, so the mirrored axis is negative there too): the sign
 is accepted only where the hierarchy reproduces Arena's own volume. Mirrored portals,
 rooms, groups, parents and zero scales stay refused (portal geometry reads the signed localScale in BaseSpatialAudioPortal)."""
 elsewhere={item['node_id'] for key in ('rooms','portals','groups','systems') for item in data[key]}
 parents={n['parent_id'] for n in data['nodes'] if n['parent_id'] is not None}
 return {a['node_id'] for a in data['areas']}-elsewhere-parents

def validate_graph(data):
 """Check source topology, activity and volume transforms before invoking native EFT."""
 import itertools
 import numpy as np
 from extract_raincover import _local_trs_matrix
 if data['schema_version']!=1 or not isinstance(data['map_key'],str) or not MAP_KEY.fullmatch(data['map_key']):raise ValueError('acoustic identity')
 slots={}
 for key in ('nodes','colliders','rooms','portals','areas','groups','systems'):
  slots[key]={item['id']:item for item in data[key]}
  if len(slots[key])!=len(data[key]):raise ValueError('duplicate component identity')
 nodes=slots['nodes'];cols=slots['colliders'];rooms=slots['rooms'];portals=slots['portals'];areas=slots['areas']
 mirrored=mirrored_volume_nodes(data)
 negative={nid for nid,node in nodes.items() if any(x<0 for x in node['scale'].values())}
 matrices={};states={};visiting=set()
 def world(nid):
  if nid in matrices:return matrices[nid]
  if nid in visiting:raise ValueError('cyclic acoustic hierarchy')
  visiting.add(nid);node=nodes[nid]
  if any(not math.isfinite(x) for k in ('position','rotation','scale') for x in node[k].values()):raise ValueError('nonfinite transform')
  # Portals are authored as zero-depth planes; room areas must still have volume. A negative local scale is accepted only on
  # a mirrored volume node (mirrored_volume_nodes); everything else keeps the strict rule.
  if (any(x<0 for x in node['scale'].values()) and nid not in mirrored) or abs(sum(x*x for x in node['rotation'].values())-1)>0.01:raise ValueError('invalid transform')
  if type(node['active_self']) is not bool or not 0<=node['layer']<=31:raise ValueError('invalid node state')
  matrix=_local_trs_matrix({'m_LocalPosition':node['position'],'m_LocalRotation':node['rotation'],'m_LocalScale':node['scale']})
  parent=node['parent_id']
  if parent is not None:matrix=world(parent)@matrix
  states[nid]=node['active_self'] and (parent is None or states[parent]);matrices[nid]=matrix;visiting.remove(nid)
  return matrix
 for nid in nodes:world(nid)
 active=lambda item:item['enabled'] and item['active_in_hierarchy']
 for key in ('rooms','portals','areas','groups','systems'):
  for item in data[key]:
   if type(item['enabled']) is not bool or item['active_in_hierarchy']!=states[item['node_id']]:raise ValueError('component activation mismatch')
 for c in cols.values():
  if not c['is_trigger'] or not c['enabled'] or any(not math.isfinite(x) for x in [*c['center'].values(),*c['size'].values()]) or c['size']['x']<=0 or c['size']['y']<=0 or c['size']['z']<0:raise ValueError('invalid acoustic collider')
 for a in areas.values():
  if a['collider_id'] not in cols or cols[a['collider_id']]['node_id']!=a['node_id']:raise ValueError('invalid area collider reference')
 ar=[r for r in rooms.values() if active(r)];ap=[p for p in portals.values() if active(p)]
 # A mirrored volume is trusted only because Arena's own cached OBB of the room agrees with the hierarchy (checked below).
 unchecked={a['id'] for a in areas.values() if a['node_id'] in negative}-{aid for r in ar for aid in r['area_ids']}
 if unchecked:raise ValueError('mirrored volume without source OBB')
 if len({r['room_id'] for r in ar})!=len(ar) or len({p['portal_id'] for p in ap})!=len(ap):raise ValueError('duplicate active native ID')
 for r in ar:
  if not r['area_ids'] or len(r['area_ids'])!=len(set(r['area_ids'])) or not 0<=r['wall_occlusion']<=1:raise ValueError('invalid room geometry')
  if len(r['area_ids'])!=len(r['source_area_obbs']):raise ValueError('source OBB count')
  for aid,obb in zip(r['area_ids'],r['source_area_obbs']):
   area=areas[aid]
   if not active(area):raise ValueError('inactive room area')
   c=cols[area['collider_id']];m=world(c['node_id']);center=m@np.array([c['center'][axis] for axis in ('x','y','z')]+[1]);size=np.linalg.norm(m[:3,:3],axis=0)*np.array([c['size'][axis] for axis in ('x','y','z')])/2
   # A mirrored axis must leave the box where it is: a centre offset of 0 on that axis makes the reflection map the box onto itself, so the
   # sign cannot move the volume whatever the engine does with it (all ten of Iceberg's: size 1, centre 0, the shape comes from the scale).
   if any(nodes[c['node_id']]['scale'][axis]<0 and c['center'][axis]!=0 for axis in ('x','y','z')):raise ValueError('mirrored volume off its pivot')
   # Arena caches lossyScale x size / 2, so a mirrored axis carries a NEGATIVE extent (all ten of Iceberg's: the sign of the node's own scale).
   # The sign is compared too: a mirrored volume is accepted only where Arena's own record mirrors the same axis.
   extents=size*np.array([-1.0 if nodes[c['node_id']]['scale'][axis]<0 else 1.0 for axis in ('x','y','z')])
   if any(size<=0) or max(abs(center[:3]-obb['center']))>0.005 or max(abs(extents-obb['extents']))>0.005:raise ValueError('source OBB differs from hierarchy')
  # Room bounds are what the client finds a room by (SpatialAudioRoom.GetRoomBounds = union of Collider.bounds). Arena stored the ones
  # Unity computed for it, so for a room with a mirrored volume they are an independent record of how Unity bounds a mirrored box.
  if any(areas[aid]['node_id'] in negative for aid in r['area_ids']):
   lo=hi=None
   for aid in r['area_ids']:
    c=cols[areas[aid]['collider_id']];m=world(c['node_id']);half=np.array([c['size'][axis] for axis in ('x','y','z')])/2;mid=np.array([c['center'][axis] for axis in ('x','y','z')])
    corners=np.array([(m@np.array([*(mid+np.array(sign)*half),1]))[:3] for sign in itertools.product((-1,1),repeat=3)])
    lo=corners.min(axis=0) if lo is None else np.minimum(lo,corners.min(axis=0));hi=corners.max(axis=0) if hi is None else np.maximum(hi,corners.max(axis=0))
   if max(abs((lo+hi)/2-r['bounds']['center']))>0.005 or max(abs((hi-lo)/2-r['bounds']['extents']))>0.005:raise ValueError('mirrored room bounds differ from the source')
  for c in r['connections']:
   other=rooms[c['room_id']]
   if not active(other):raise ValueError('inactive connected room')
   for pid in c['portal_ids']:
    p=portals[pid]
    if not active(p) or set(p['connected_room_ids'])!={r['id'],other['id']}:raise ValueError('nonreciprocal room portal')
  ambient=r['ambient_data']
  if ambient['season_sound_preset'] is not None:raise ValueError('unsupported room precipitation asset')
 for p in ap:
  if len(p['connected_room_ids'])!=2 or len(set(p['connected_room_ids']))!=2 or p['collider_id'] not in cols:raise ValueError('invalid portal endpoints')
  if not all(active(rooms[rid]) for rid in p['connected_room_ids']):raise ValueError('inactive portal endpoint')
  if p['portal_type'] not in (0,1,2) or p['state'] not in (0,1) or not 0<=p['transmission']<=1 or not 0<=p['traversal_max_cost']<=1 or p['depth']<=0:raise ValueError('invalid portal state')
 if len(data['groups'])!=1 or len(data['systems'])!=1:raise ValueError('one source graph required')
 g=data['groups'][0]
 if not active(g) or set(g['room_ids'])!={r['id'] for r in ar} or set(g['portal_ids'])!={p['id'] for p in ap}:raise ValueError('source group coverage')


def main(argv=None):
 parser=argparse.ArgumentParser(description=__doc__,formatter_class=argparse.RawDescriptionHelpFormatter)
 parser.add_argument('--source',type=Path,default=ROOT)
 parser.add_argument('--output',type=Path,default=Path(__file__).resolve().parents[2]/'work/extract/spatial-audio')
 parser.add_argument('--map',action='append',dest='maps',metavar='ARENA_KEY',
  help='map key such as arena_airpit; repeatable. Plan-driven maps need a "sound" scene role in tools/extract/plans. Default: arena_equator and arena_bay5, which OVERWRITES the accepted inputs of those two maps; name the map (or set --output) unless that is intended.')
 args=parser.parse_args(argv)
 for key in args.maps or DEFAULT_MAPS:
  try:
   scene=scene_for_map(key)
   if key not in LEGACY_SOUND_SCENES:
    # Fail closed if an Arena update moved the scenes the plan names (the sound role included).
    verify_scene_mapping(load_plan(plan_path(key)),read_build_scenes(args.source),args.source)
  except (PlanError,ValueError,OSError) as e:
   print('[extract_spatial_audio] FAILED: %s'%e,file=sys.stderr);return 2
  data=export(args.source,args.output/key,key,scene)
  print(key,json.dumps({k:data['bake'][k] for k in ('bytes','room_pairs_count','routes_count','sha256')}))
  print(key,json.dumps({'source_scene':data['source_scene'],'rooms':len(data['rooms']),'portals':len(data['portals']),'areas':len(data['areas']),'clips':len(data['clips']),'mixer_effects':bool(data['mixer_effects'])}))
  if data.get('bake_repair'):print(key,json.dumps({'bake_repair':{k:data['bake_repair'][k] for k in ('rule','routes_repaired','rewrites')}}))
 return 0

if __name__=='__main__':sys.exit(main())
