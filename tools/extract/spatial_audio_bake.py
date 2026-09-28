"""Validate native bake integrity before enabling the coroutine-driven EFT reader.
The binary ABI is checked against current EFT AudioBakeDataReader, RoomPair.Route,
AudioRouteData and LocationBakeSettings. No Unity field alignment in this stream.
"""
from __future__ import annotations
import hashlib,json,math,struct
from pathlib import Path

class BakeReader:
 def __init__(self,data): self.data=bytes(data);self.pos=0
 def take(self,fmt):
  size=struct.calcsize('<'+fmt)
  if self.pos+size>len(self.data):raise ValueError('truncated audio bake')
  values=struct.unpack_from('<'+fmt,self.data,self.pos);self.pos+=size
  if any(isinstance(x,float) and not math.isfinite(x) for x in values):raise ValueError('nonfinite audio bake')
  return values[0] if len(values)==1 else list(values)
 def count(self,minimum_bytes=1):
  n=self.take('i')
  if n<0 or n>(len(self.data)-self.pos)//minimum_bytes:raise ValueError('invalid audio bake count')
  return n
 def unique(self,d,key,value):
  if key in d:raise ValueError('duplicate audio bake dictionary key')
  d[key]=value

def parse_bake(data,graph=None):
 r=BakeReader(data);pairs={}
 for _ in range(r.count(17)):
  key=r.take('I');pid=r.take('I');a=r.take('h');b=r.take('h')
  if pid!=key or a==b:raise ValueError('invalid room pair identity')
  routes=[]
  for _ in range(r.count(12)):
   ids=[r.take('h') for _ in range(r.count(2))];cost=r.take('f');distance=r.take('f')
   if not ids or cost<0 or distance<0:raise ValueError('invalid baked route')
   routes.append({'portal_ids':ids,'heuristic_cost':cost,'traverse_distance':distance})
  total=r.take('i');shortest=r.take('B')
  if not routes or total!=sum(len(x['portal_ids']) for x in routes) or shortest!=min(len(x['portal_ids']) for x in routes):raise ValueError('room pair aggregate mismatch')
  r.unique(pairs,key,{'first_room_id':a,'second_room_id':b,'routes':routes,'total_portals_count':total})
 aware={}
 for _ in range(r.count(6)):
  room=r.take('h');ids=[r.take('I') for _ in range(r.count(4))]
  if len(set(ids))!=len(ids) or any(x not in pairs for x in ids):raise ValueError('bad room pair awareness refs')
  if any(room not in (pairs[x]['first_room_id'],pairs[x]['second_room_id']) for x in ids):raise ValueError('awareness room not in pair')
  r.unique(aware,room,ids)
 indices={}
 for _ in range(r.count(8)):
  pid=r.take('I');items=[r.take('hfHH') for _ in range(r.count(10))]
  if pid not in pairs or len(items)!=len(pairs[pid]['routes']):raise ValueError('route index pair coverage')
  start=node_start=0
  for item,route in zip(items,pairs[pid]['routes']):
   length,cost,source_start,source_node_start=item
   if (length,cost,source_start,source_node_start)!=(len(route['portal_ids']),route['heuristic_cost'],start,node_start):raise ValueError('route index disagrees with main section')
   start+=length;node_start+=length+2
  r.unique(indices,pid,items)
 if set(indices)!=set(pairs):raise ValueError('route index key mismatch')
 names=('max_depth_indoor_to_indoor','max_depth_indoor_to_outdoor','max_depth_outdoor_to_outdoor','max_distance_outdoor_to_outdoor','max_distance_indoor_to_outdoor','max_distance_indoor_to_indoor')
 settings=dict(zip(names,r.take('iiiiii')));max_routes=r.take('I');max_portals=r.take('I')
 if r.pos!=len(r.data):raise ValueError('trailing audio bake bytes')
 if max_routes!=max(len(p['routes']) for p in pairs.values()) or max_portals!=max(p['total_portals_count'] for p in pairs.values()):raise ValueError('bake maximum capacities mismatch')
 roomids={x for p in pairs.values() for x in (p['first_room_id'],p['second_room_id'])};portalids={x for p in pairs.values() for route in p['routes'] for x in route['portal_ids']}
 result={'bytes':len(r.data),'sha256':hashlib.sha256(r.data).hexdigest(),'room_pairs_count':len(pairs),'routes_count':sum(len(p['routes']) for p in pairs.values()),'routes_aware_rooms_count':len(aware),'max_routes_count':max_routes,'max_route_portals_count':max_portals,'bake_settings':settings,'room_ids':sorted(roomids),'portal_ids':sorted(portalids)}
 if graph:
  active=lambda x:x['enabled'] and x['active_in_hierarchy']
  rooms={x['room_id']:x for x in graph['rooms'] if active(x)};portals={x['portal_id']:x for x in graph['portals'] if active(x)};component_room_ids={x['id']:x['room_id'] for x in rooms.values()}
  if len(rooms)!=sum(active(x) for x in graph['rooms']) or len(portals)!=sum(active(x) for x in graph['portals']):raise ValueError('duplicate active native ID')
  if set(rooms)!=roomids or set(portals)!=portalids:raise ValueError('bake source graph coverage mismatch')
  # Validate every portal sequence is a connected route between that pair's authored rooms.
  for pair in pairs.values():
   for route in pair['routes']:
    here=pair['first_room_id']
    for pid in route['portal_ids']:
     link=[component_room_ids[cid] for cid in portals[pid]['connected_room_ids']]
     if len(link)!=2 or here not in link:raise ValueError('route is disconnected from source graph')
     here=link[1] if here==link[0] else link[0]
    if here!=pair['second_room_id']:raise ValueError('route does not reach pair endpoint')
  info=graph['systems'][0]['location_info']
  for k in ('room_pairs_count','routes_count','max_routes_count','max_route_portals_count','bake_settings'):
   if result[k]!=info[k]:raise ValueError('location info disagrees with bake: '+k)
  if (info['rooms_count'],info['portals_count'])!=(len(rooms),len(portals)):raise ValueError('location info graph count mismatch')
 return result
