"""Validate native bake integrity before enabling the coroutine-driven EFT reader.
The binary ABI is checked against current EFT AudioBakeDataReader, RoomPair.Route,
AudioRouteData and LocationBakeSettings. No Unity field alignment in this stream.
"""
from __future__ import annotations
import hashlib,json,math,struct
from pathlib import Path

class RouteGraphError(ValueError):
 """A baked route that does not follow the source graph (a portal that does not touch the room the route is in, or an end
 other than the pair's second room). The file itself is well formed; only the pairing with the graph is wrong."""

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
     if len(link)!=2 or here not in link:raise RouteGraphError('route is disconnected from source graph')
     here=link[1] if here==link[0] else link[0]
    if here!=pair['second_room_id']:raise RouteGraphError('route does not reach pair endpoint')
  info=graph['systems'][0]['location_info']
  for k in ('room_pairs_count','routes_count','max_routes_count','max_route_portals_count','bake_settings'):
   if result[k]!=info[k]:raise ValueError('location info disagrees with bake: '+k)
  if (info['rooms_count'],info['portals_count'])!=(len(rooms),len(portals)):raise ValueError('location info graph count mismatch')
 return result


# ---------------------------------------------------------------------------------------------------------------------
# Reviewed repair of duplicated portal IDs (PLAN-ICEBERG item 11, docs/progress/iceberg-release.md "Spatial acoustics").
#
# Arena's Sanatorium bake was made while two different portals carried the ID 66: the shipped scene has one portal 66
# (elevators <-> boiler (2), rooms 27 and 24) and 533 of the 17,751 baked routes use that ID for a second portal that
# joins elevators and boiler (rooms 27 and 11), a doorway the shipped scene knows as portal 30. The loader never walks
# a route (PropagationJob takes each portal ID's data and measures the polyline), so the routes load and play, through
# the wrong doorway. parse_bake keeps refusing such a file. This module rewrites the offending IDs only when a reviewed
# rule names the ID, every failing route has exactly one fix, and the result passes the unchanged strict check.
# ---------------------------------------------------------------------------------------------------------------------

def scan_routes(data):
 """Every baked route as (pair key, first room, second room, route index, byte offset of its first portal ID, IDs).
 The same walk as parse_bake over the room-pair section; nothing is validated here beyond what the reader enforces."""
 r=BakeReader(data);out=[]
 for _ in range(r.count(17)):
  key=r.take('I');r.take('I');a=r.take('h');b=r.take('h')
  for index in range(r.count(12)):
   n=r.count(2);offset=r.pos;ids=[r.take('h') for _ in range(n)];r.take('f');r.take('f')
   out.append((key,a,b,index,offset,ids))
  r.take('i');r.take('B')
 return out

def portal_room_links(graph):
 """Active portal ID -> the two native room IDs it joins, in the graph's order."""
 active=lambda x:x['enabled'] and x['active_in_hierarchy']
 room_of={x['id']:x['room_id'] for x in graph['rooms'] if active(x)}
 return {p['portal_id']:tuple(room_of[c] for c in p['connected_room_ids']) for p in graph['portals'] if active(p)}

def route_follows_graph(links,first,second,ids):
 """parse_bake's route check: every portal touches the room the route is in, and the last one leads to the second room."""
 here=first
 for pid in ids:
  link=links.get(pid)
  if link is None or len(link)!=2 or here not in link:return False
  here=link[1] if here==link[0] else link[0]
 return here==second

def _unique_neighbours(links):
 """room -> {other room: [portal IDs joining them]}."""
 near={}
 for pid,(x,y) in sorted(links.items()):
  near.setdefault(x,{}).setdefault(y,[]).append(pid)
  near.setdefault(y,{}).setdefault(x,[]).append(pid)
 return near

def resolve_route(links,near,first,second,ids,allowed):
 """The cheapest way to read a failing route as a walk of the graph when only portal IDs in ``allowed`` may stand for another
 portal. A substitution replaces the ID by THE portal that joins the two rooms the walk is between, and only where exactly one
 portal joins them (parallel portals would be a guess). Returns (cost, [assignments]); every assignment is the full ID list the
 walk uses. ``None`` when no reading reaches the second room."""
 states={first:(0,[()])}
 for pid in ids:
  nxt={}
  def offer(room,cost,assign):
   have=nxt.get(room)
   if have is None or cost<have[0]:nxt[room]=(cost,[assign])
   elif cost==have[0] and assign not in have[1]:have[1].append(assign)
  for room,(cost,assigns) in states.items():
   link=links.get(pid)
   if link is not None and room in link:
    other=link[1] if room==link[0] else link[0]
    for a in assigns:offer(other,cost,a+(pid,))
   if pid in allowed:
    for other,joining in near.get(room,{}).items():
     if len(joining)==1 and joining[0]!=pid:
      for a in assigns:offer(other,cost+1,a+(joining[0],))
  states=nxt
 return states.get(second)

def repair_portal_ids(data,graph,portal_ids):
 """Rewrite the portal IDs of failing routes under the duplicated-ID rule and return (repaired bytes, report).

 ``portal_ids`` names the IDs that may stand for another portal (the reviewed rule of one map). A route that already follows
 the graph is never touched. A failing route must have exactly one reading with the fewest substitutions, else RouteGraphError
 (no reading, or an ambiguous one). The file keeps its size, structure and every route length, so the route index, the
 aggregates and the counts stay valid; the result is checked with the unchanged parse_bake before it is returned."""
 allowed=frozenset(portal_ids)
 if not allowed:raise ValueError('no portal ID named for the repair')
 links=portal_room_links(graph);near=_unique_neighbours(links)
 buf=bytearray(data);rewrites={};routes_repaired=0;ids_rewritten=0
 for key,a,b,index,offset,ids in scan_routes(data):
  if route_follows_graph(links,a,b,ids):continue
  found=resolve_route(links,near,a,b,ids,allowed)
  if found is None:raise RouteGraphError('route %d of room pair %d has no reading under the duplicated-ID rule'%(index,key))
  cost,assigns=found
  if len(assigns)!=1:raise RouteGraphError('route %d of room pair %d has %d equally cheap readings'%(index,key,len(assigns)))
  routes_repaired+=1
  for i,(old,new) in enumerate(zip(ids,assigns[0])):
   if old==new:continue
   struct.pack_into('<h',buf,offset+2*i,new);ids_rewritten+=1
   rewrites['%d->%d'%(old,new)]=rewrites.get('%d->%d'%(old,new),0)+1
 repaired=bytes(buf)
 parse_bake(repaired,graph)
 report={'rule':'duplicated-portal-id','portal_ids':sorted(allowed),'routes_repaired':routes_repaired,'ids_rewritten':ids_rewritten,
  'rewrites':dict(sorted(rewrites.items())),'original':{'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()},
  'repaired':{'bytes':len(repaired),'sha256':hashlib.sha256(repaired).hexdigest()}}
 return repaired,report
