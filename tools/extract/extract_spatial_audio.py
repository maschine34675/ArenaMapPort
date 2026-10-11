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
from spatial_audio_bake import parse_bake
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
  result['bake']=parse_bake(bake,result)
  result['bake']['file']='source.audiobakedata'
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


def validate_graph(data):
 """Check source topology, activity and volume transforms before invoking native EFT."""
 import numpy as np
 from extract_raincover import _local_trs_matrix
 if data['schema_version']!=1 or not isinstance(data['map_key'],str) or not MAP_KEY.fullmatch(data['map_key']):raise ValueError('acoustic identity')
 slots={}
 for key in ('nodes','colliders','rooms','portals','areas','groups','systems'):
  slots[key]={item['id']:item for item in data[key]}
  if len(slots[key])!=len(data[key]):raise ValueError('duplicate component identity')
 nodes=slots['nodes'];cols=slots['colliders'];rooms=slots['rooms'];portals=slots['portals'];areas=slots['areas']
 matrices={};states={};visiting=set()
 def world(nid):
  if nid in matrices:return matrices[nid]
  if nid in visiting:raise ValueError('cyclic acoustic hierarchy')
  visiting.add(nid);node=nodes[nid]
  if any(not math.isfinite(x) for k in ('position','rotation','scale') for x in node[k].values()):raise ValueError('nonfinite transform')
  # Portals are authored as zero-depth planes; room areas must still have volume.
  if any(x<0 for x in node['scale'].values()) or abs(sum(x*x for x in node['rotation'].values())-1)>0.01:raise ValueError('invalid transform')
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
 if len({r['room_id'] for r in ar})!=len(ar) or len({p['portal_id'] for p in ap})!=len(ap):raise ValueError('duplicate active native ID')
 for r in ar:
  if not r['area_ids'] or len(r['area_ids'])!=len(set(r['area_ids'])) or not 0<=r['wall_occlusion']<=1:raise ValueError('invalid room geometry')
  if len(r['area_ids'])!=len(r['source_area_obbs']):raise ValueError('source OBB count')
  for aid,obb in zip(r['area_ids'],r['source_area_obbs']):
   area=areas[aid]
   if not active(area):raise ValueError('inactive room area')
   c=cols[area['collider_id']];m=world(c['node_id']);center=m@np.array([c['center'][axis] for axis in ('x','y','z')]+[1]);extents=np.linalg.norm(m[:3,:3],axis=0)*np.array([c['size'][axis] for axis in ('x','y','z')])/2
   if any(extents<=0) or max(abs(center[:3]-obb['center']))>0.005 or max(abs(extents-obb['extents']))>0.005:raise ValueError('source OBB differs from hierarchy')
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
 return 0

if __name__=='__main__':sys.exit(main())
