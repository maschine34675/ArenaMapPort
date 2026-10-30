#!/usr/bin/env python3
"""Mode-object sidecar: capture point, spawn bounds and spawn markers of an extracted package.

Why. ArenaMode (the user's mod, which owns team starts, pickups and the objective) needs the
positions of Arena's TeamFight mode objects on every released map. They sit in the
extracted scenes as ordinary objects whose own transforms are mostly the origin (the
``CAPTURE_POINT`` container is at local 0,0,0) and whose useful position only appears
after composing the parent chain. This tool reads ``scenes/*.json`` of one package
(full hierarchy with local transforms), composes world transforms and writes
``tools/maps/<stem>.modes.json``: positions only, no BSG assets, git-tracked data.

Input. A package directory written by extract_equator.py, extract_bay5.py or
extract_map.py (``manifest.json`` + ``scenes/*.json``). The offline geometry of an older
package is as good as a new one: positions do not depend on the native-feature level.
Nothing is read from the Arena install (the one exception is the optional Iceberg stage table, see
"Team bounds") and nothing is written outside ``--output``.

What is extracted (all scenes of the package are scanned; names are compared stripped and
case-folded, because Arena has ``SPAWN_BOUND_FIGHTER `` and ``EQUATOR_CAPTURE_POINT_LIGHT ``):

* ``capture_point`` - the objective position. Measured on all three maps: the
  ``CAPTURE_POINT`` container is at the origin and its direct children named
  ``*CAPTURE_POINT_LIGHT*`` (Equator 1, Bay5 1, AirPit 2) mark the point, so the position is
  the mean of those lights' world positions (``method`` ``capture_point_light_centroid``).
  AirPit's two lights are 64 m apart, 32 m on either side of the point; their midpoint
  (-13.21, -0.05, 1.29) agrees with the TeamFight scene's own ``Capture_Zone`` root
  (-13.16, -0.14, 1.33) to 0.06 m horizontally (0.11 m in 3-D). A map without such lights falls back to the
  first ``Capture_Zone`` whose ``anchor_position`` exists (``method`` ``capture_zone_anchor``);
  a map with neither gets ``capture_point: null`` and a warning, never an invented point.
* ``capture_zones`` - every ``Capture_Zone`` / ``CAPTURE_ZONE`` object with its world
  position, the world position of its ``PROJECTION`` child (the decal group; for AirPit's
  DesignStuff scene the root sits at (-186, 18, -24) and only the child lands on the
  point), ``anchor_position`` (the child's position, else the root's, each only when it is
  not the origin, else null) and its distance to ``capture_point``. They are evidence for
  the capture point, not alternative answers: Equator's and Bay5's roots are at the origin
  (anchor null), AirPit's base-scene ``CAPTURE_ZONE`` (-11.44, -0.07, 3.27) belongs to the
  ShootOut mode and is 2.6 m from the TeamFight point.
* ``spawn_bounds`` - every ``SPAWN_BOUND*`` object with a BoxCollider: world ``center``,
  world ``size`` (collider size times lossy scale; the boxes are scaled 1.01 x 1 x 3.53 in
  the source), world ``rotation`` quaternion, ``yaw``, and ``side`` (see "Sides" below).
* ``spawn_markers`` - the direct children of the object named ``SPAWNS`` other than the
  bounds: Equator and AirPit ``ScavPMCspawn (1..20)`` (a SphereCollider each), Bay5
  ``Bear house (n)`` / ``Usec house (n)`` (a BoxCollider each). World ``position``, ``yaw`` in
  degrees [0, 360) (Unity convention: 0 looks along +z, increasing towards +x), ``stem``
  and ``index`` from the ``name (n)`` suffix, ``active`` (active in hierarchy). They are
  grouped into the two sides ``north`` and ``south`` (see "Sides" below).

Sides. A TeamFight map has two spawn pockets, one per team. The original rule put a marker
on ``north`` when its world z is positive and on ``south`` when it is negative, which is only
true to the layout while the origin lies between the pockets: Equator (z 44..49 / -42..-38),
Bay5 (z 44..58 / -12..-5) and AirPit (z 75..80 / -75..-72) all straddle z == 0. Bowl's pockets
(east x 46..56, z -11..-3; west x -58..-49, z -69..-60) both have z < 0, so the rule reported
north 0 / south 20. The pockets themselves are therefore found from the data: the markers'
(x, z) positions are cut into two groups by single linkage (the cut falls on the shortest
distance that joins the last two groups), and the cut counts as two pockets only when that
distance exceeds ``POCKET_SEPARATION`` (2) times the wider group's diameter - measured (cut /
wider pocket): Bowl 112.6 m / 10.2 m = 11.0, Equator 82.9 / 14.5 = 5.7, Bay5 97.4 / 14.4 = 6.7,
AirPit 147.1 / 22.8 = 6.5. (Splitting along the axis with the larger spread of the pocket centres
would not do: Bay5's centres are 87.6 m apart in x but 60.2 m in z, so its sides would turn
east/west and break the released sidecar; Bay5 and Bowl are the same kind of diagonal layout.)
* Pockets equal the sign-of-z groups (all three released maps, a test pins it): the original
  rule stands, with its leniency about unequal counts, and the output is byte-identical to the
  sidecars of release 0.2.x (no new key). So is a layout with no clear pockets that straddles
  z == 0 (only synthetic fixtures); the sign rule is the fallback there.
* Pockets differ from the sign-of-z groups, or no marker can be grouped by sign (every marker
  on one side of z == 0, Bowl): the pockets are the sides. They must hold the same number of
  markers, and the pocket with the larger mean z is ``north`` (the other ``south``), so a key
  means the same on every map ("north" = the +z pocket); Bowl's ``north`` is therefore the
  east pocket and ``south`` the west one. A bound belongs to
  the side whose pocket centroid is nearest (at most half as far as the other). The sidecar
  then carries one extra key, ``side_rule`` (method ``pocket_split``, the cut distance, the
  pocket spread, per side the centroid (x, z) and marker count); sidecars made by the original
  rule have none.
* Neither works (every marker on one side of z == 0 and no two separated pockets, an odd or
  unequal split, equal mean z so the names would be arbitrary, a bound between the pockets, a
  marker on z == 0 while no two pockets are separated): the run fails; nothing is guessed.

Team bounds (Iceberg, PLAN-ICEBERG item 7, 2026-10-30). Iceberg's CheckPoint scene is not a
two-equal-pockets layout: 70 markers (40 ``Bear house``, 30 ``Usec house``) and 12 spawn bounds
(``SPAWN_BOUND_FIGHTER`` + 6 numbered ones for Bear, ``SPAWN_BOUND_DEFENCE`` + 4 numbered ones for
Usec). Only the two UNSUFFIXED bounds are the initial pockets (Bear east, x 89..98; Usec west,
x -45..-37, 10 markers each); the other ten are STAGE bounds whose markers are the respawn
points of a capture stage. The pocket rule above cannot see that (70 markers on one side of
z == 0, stage sets 70 m from both pockets) and its mean-z naming would call the pockets north/south on
a 1 m difference. A map listed in ``TEAM_BOUND_RULES`` therefore uses a per-map team rule instead:

* the side is the TEAM, written ``bear`` / ``usec`` (never north/south, never by mean z): a marker
  belongs to the team whose marker stem it carries, a bound to the team whose bound name it
  carries (``SPAWN_BOUND_FIGHTER`` or ``SPAWN_BOUND_FIGHTER (n)``); any other name fails the run;
* each team has exactly one unsuffixed bound; a marker is a ``pocket`` marker when it lies inside
  that bound's published box (``center``, ``size``, ``rotation``) with ``BOUND_EDGE_TOLERANCE``
  (0.05 m) to spare - measured on Arena 0.6.1: Bear 10/10 inside by 2.7..7.3 m, Usec 9 of 10 by
  0.92 m and marker (6) on the edge at -0.002 m, no other marker inside any unsuffixed bound;
  a marker inside the OTHER team's unsuffixed bound, or a team without a pocket marker, fails the run;
* there is no equal-pocket requirement (10 against 10 here, but nothing checks it);
* ``capture_point`` is null and ``capture_points`` lists every ``CAPTURE_POINT_LIGHT*_<A|B|C>`` light
  individually (``label``, ``position``): they are the three ``CapturePointEntity`` objects, whose
  centroid (16.43, 3.15, -72.14) is meaningless;
* ``side_rule`` has method ``team_bounds`` and per team the marker stem, bound, marker / pocket /
  stage-bound counts and the pocket centroid;
* ``spawn_markers`` holds ALL markers of a team (the legacy meaning: the children of SPAWNS), each with
  ``pocket`` true or false; ``counts`` has ``spawn_markers_<team>`` and ``pocket_markers_<team>``.

The stage graph (``stages``) is the one thing the package cannot say: which marker belongs to
which stage bound, how the bounds link and which capture points a stage serves live in Arena's
``Game.Modes.Data.ArenaSpawnBoundCheckPointData`` MonoBehaviours, which the package does not carry.
With ``--arena-data`` the tool raw-decodes them from the installed ``level<N>`` file (read-only; the
scene index is the plan's, ``tools/extract/plans/<stem>.json``): ``int team`` (1 = FIGHTER/Bear,
0 = DEFENCE/Usec), ``int initial``, ``int flag`` (1 on all 12; meaning unknown, kept as read), the
linked bounds (count + MonoBehaviour PPtrs), one null PPtr, the markers (count + GameObject PPtrs) and
the capture points served (count + CapturePointEntity PPtrs); the end offset must equal the object
size. The GameObject path ids of the file are the package ids, so every PPtr resolves through the package
scene. Cross-checks that fail the run: a bound without a record or a record without a bound, a team
or ``initial`` value that disagrees with the bound's name, a marker listed twice or never or under the
other team, and an ``initial`` record whose marker list is not exactly the geometric pocket. Without
``--arena-data`` the sidecar has no ``stages`` key (the tracked one is made with it).

Transforms are composed as translate * rotate * scale with unit quaternions and no shear
handling (Unity's lossy-scale approximation); values are rounded to 1e-4 m / degree, and
-0.0 is written as 0.0, so the output is byte-reproducible.

Fail closed (``HardExtractError``): a parent id that names no object, a parent cycle, a
spawn bound without a BoxCollider, a malformed vector, a marker that cannot be grouped (z == 0
without separated pockets, or any of the side failures above). Missing categories only warn
(stderr) and appear as empty lists / null.

CLI:
    python tools/extract/extract_mode_objects.py --package work/extract/equator-native-v5
    python tools/extract/extract_mode_objects.py --package work/extract/bay5-native-v5
    python tools/extract/extract_mode_objects.py --package work/extract/airpit-native-v9
    python tools/extract/extract_mode_objects.py --package work/extract/bowl-native-v<N>
    python tools/extract/extract_mode_objects.py --package work/extract/iceberg-native-v1
        --arena-data "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
    (Iceberg only: --arena-data adds the stage graph; one line in practice)
    (the map key comes from the manifest, or from the extractor script for the two legacy
     packages, or from --map-key; the output defaults to tools/maps/<stem>.modes.json)
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import struct
import sys
from dataclasses import dataclass
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from common_ids import HardExtractError  # noqa: E402

SCHEMA_VERSION = 1
DEFAULT_MAPS_DIR = Path(_HERE).parent / "maps"
DEFAULT_PLANS_DIR = Path(_HERE) / "plans"
ANALYSIS_SCRIPTS = os.path.normpath(os.path.join(_HERE, "..", "..", "analysis", "scripts"))
LEGACY_MAP_KEYS = {
    "tools/extract/extract_equator.py": "arena_equator",
    "tools/extract/extract_bay5.py": "arena_bay5",
}
MAP_KEY = re.compile(r"arena_[a-z0-9_]+")
INDEX_SUFFIX = re.compile(r"^(?P<stem>.*?)\s*\((?P<index>\d+)\)$")
ROUND = 4  # decimals: 1e-4 m / degree
POCKET_SEPARATION = 2.0  # pocket gap must exceed this multiple of the wider pocket's diameter
POCKET_MIN_DZ = 1.0      # metres of mean-z difference below which north/south would be arbitrary
BOUND_DOMINANCE = 2.0    # a bound's own pocket must be at least this many times nearer than the other
BOUND_EDGE_TOLERANCE = 0.05  # metres a pocket marker may lie outside its team's unsuffixed bound (Iceberg Usec (6): 0.002)
CAPTURE_LABEL = re.compile(r"_(?P<label>[A-Z])\s*$")
STAGE_CLASS = "ArenaSpawnBoundCheckPointData"


@dataclass(frozen=True)
class TeamRule:
    """One team of a team-bound map: how its markers and bounds are told apart."""
    key: str         # the side name written to the sidecar ("bear")
    stem: str        # marker name stem ("Bear house"); compared stripped and case-folded
    bound: str       # the UNSUFFIXED pocket bound ("SPAWN_BOUND_FIGHTER"); "<bound> (n)" are its stage bounds
    arena_team: int  # the ``team`` int of Arena's ArenaSpawnBoundCheckPointData (1 = FIGHTER, 0 = DEFENCE)


@dataclass(frozen=True)
class TeamBoundRule:
    """Per-map rule that replaces the two-pocket side rule (module docstring, "Team bounds")."""
    mode: str                # the Arena mode the scene belongs to, for the sidecar comment
    scene: str               # the scene whose SPAWNS container holds every marker and bound
    teams: tuple             # of TeamRule; the order is the output order


TEAM_BOUND_RULES = {
    # Arena_Sanatorium CheckPoint (level376, Arena 0.6.1): FIGHTER = Bear = the east pocket, DEFENCE = Usec = the west one.
    "arena_iceberg": TeamBoundRule(
        mode="CheckPoint",
        scene="Arena_Sanatorium_CheckPoint_DesignStuff",
        teams=(TeamRule("bear", "Bear house", "SPAWN_BOUND_FIGHTER", 1),
               TeamRule("usec", "Usec house", "SPAWN_BOUND_DEFENCE", 0))),
}


# --------------------------------------------------------------------------
# Small matrix helpers (plain Python: the package JSON is the only dependency)
# --------------------------------------------------------------------------

def _identity() -> list:
    return [[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]]


def _mul(a: list, b: list) -> list:
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def _finite(values, count: int, label: str) -> list:
    if (not isinstance(values, (list, tuple)) or len(values) != count
            or any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in values)):
        raise HardExtractError(f"mode objects: {label} must be {count} finite numbers, got {values!r}")
    return [float(v) for v in values]


def local_matrix(obj: dict) -> list:
    """translate * rotate * scale of one scene object (quaternion xyzw, normalised)."""
    where = f"object {obj.get('id')!r} ({obj.get('name')!r})"
    px, py, pz = _finite(obj["local_position"], 3, where + " local_position")
    x, y, z, w = _finite(obj["local_rotation"], 4, where + " local_rotation")
    sx, sy, sz = _finite(obj["local_scale"], 3, where + " local_scale")
    norm = math.sqrt(x * x + y * y + z * z + w * w)
    if norm == 0.0:
        raise HardExtractError(f"mode objects: {where} has a zero quaternion")
    x, y, z, w = x / norm, y / norm, z / norm, w / norm
    rot = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
           [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
           [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    return [[rot[0][0] * sx, rot[0][1] * sy, rot[0][2] * sz, px],
            [rot[1][0] * sx, rot[1][1] * sy, rot[1][2] * sz, py],
            [rot[2][0] * sx, rot[2][1] * sy, rot[2][2] * sz, pz],
            [0.0, 0.0, 0.0, 1.0]]


def _clean(value: float) -> float:
    return round(value, ROUND) + 0.0  # + 0.0 turns -0.0 into 0.0


def _vec(values) -> list:
    return [_clean(v) for v in values]


def point(matrix: list, local: tuple = (0.0, 0.0, 0.0)) -> list:
    x, y, z = local
    return [matrix[i][0] * x + matrix[i][1] * y + matrix[i][2] * z + matrix[i][3] for i in range(3)]


def lossy_scale(matrix: list) -> list:
    return [math.sqrt(sum(matrix[r][c] ** 2 for r in range(3))) for c in range(3)]


def yaw_degrees(matrix: list) -> float:
    """Heading of the object's +z axis around +y, Unity convention, in [0, 360)."""
    degrees = math.degrees(math.atan2(matrix[0][2], matrix[2][2])) % 360.0
    return _clean(degrees) % 360.0


def rotation_quaternion(matrix: list) -> list:
    """World rotation (x, y, z, w) from the column-normalised 3x3 (positive scale, no shear)."""
    scale = lossy_scale(matrix)
    if min(scale) <= 0.0:
        raise HardExtractError("mode objects: degenerate scale in a world matrix")
    r = [[matrix[i][j] / scale[j] for j in range(3)] for i in range(3)]
    trace = r[0][0] + r[1][1] + r[2][2]
    if trace > 0.0:
        s = math.sqrt(trace + 1.0) * 2.0
        q = [(r[2][1] - r[1][2]) / s, (r[0][2] - r[2][0]) / s, (r[1][0] - r[0][1]) / s, 0.25 * s]
    elif r[0][0] > r[1][1] and r[0][0] > r[2][2]:
        s = math.sqrt(1.0 + r[0][0] - r[1][1] - r[2][2]) * 2.0
        q = [0.25 * s, (r[0][1] + r[1][0]) / s, (r[0][2] + r[2][0]) / s, (r[2][1] - r[1][2]) / s]
    elif r[1][1] > r[2][2]:
        s = math.sqrt(1.0 + r[1][1] - r[0][0] - r[2][2]) * 2.0
        q = [(r[0][1] + r[1][0]) / s, 0.25 * s, (r[1][2] + r[2][1]) / s, (r[0][2] - r[2][0]) / s]
    else:
        s = math.sqrt(1.0 + r[2][2] - r[0][0] - r[1][1]) * 2.0
        q = [(r[0][2] + r[2][0]) / s, (r[1][2] + r[2][1]) / s, 0.25 * s, (r[1][0] - r[0][1]) / s]
    if q[3] < 0.0:  # canonical hemisphere: w >= 0
        q = [-v for v in q]
    return _vec(q)


# --------------------------------------------------------------------------
# One scene: parent chain, world matrices, paths
# --------------------------------------------------------------------------

def norm_name(name: str) -> str:
    return name.strip().casefold()


class SceneGraph:
    """World transforms and hierarchy paths of one extracted scene JSON."""

    def __init__(self, scene_name: str, data: dict):
        objects = data.get("objects")
        if not isinstance(objects, list):
            raise HardExtractError(f"mode objects: scene {scene_name} has no objects array")
        self.name = scene_name
        self.objects = objects
        self.by_id: dict = {}
        self.children: dict = {}
        for obj in objects:
            if obj["id"] in self.by_id:
                raise HardExtractError(f"mode objects: scene {scene_name} repeats object id {obj['id']!r}")
            self.by_id[obj["id"]] = obj
        for obj in objects:
            parent = obj.get("parent_id")
            if parent is not None and parent not in self.by_id:
                raise HardExtractError(f"mode objects: scene {scene_name} object {obj['id']!r} ({obj['name']!r}) "
                                       f"names missing parent {parent!r}")
            self.children.setdefault(parent, []).append(obj)
        self._world: dict = {}
        self._active: dict = {}

    def chain(self, obj: dict) -> list:
        """obj and its ancestors, root last; a cycle fails the run."""
        out, seen = [], set()
        while obj is not None:
            if obj["id"] in seen:
                raise HardExtractError(f"mode objects: scene {self.name} has a parent cycle at {obj['id']!r}")
            seen.add(obj["id"])
            out.append(obj)
            parent = obj.get("parent_id")
            obj = self.by_id[parent] if parent is not None else None
        return out

    def world(self, obj: dict) -> list:
        cached = self._world.get(obj["id"])
        if cached is not None:
            return cached
        matrix = _identity()
        for ancestor in reversed(self.chain(obj)):
            known = self._world.get(ancestor["id"])
            matrix = known if known is not None else _mul(matrix, local_matrix(ancestor))
            self._world[ancestor["id"]] = matrix
        return matrix

    def active_in_hierarchy(self, obj: dict) -> bool:
        cached = self._active.get(obj["id"])
        if cached is None:
            cached = all(bool(o.get("active", True)) for o in self.chain(obj))
            self._active[obj["id"]] = cached
        return cached

    def path(self, obj: dict) -> str:
        return "/".join(o["name"] for o in reversed(self.chain(obj)))

    def position(self, obj: dict) -> list:
        return point(self.world(obj))


# --------------------------------------------------------------------------
# Extraction
# --------------------------------------------------------------------------

def _warn(message: str) -> None:
    print(f"[extract_mode_objects] WARNING: {message}", file=sys.stderr)


def side_of(z: float, label: str) -> str:
    """The original rule: sign of the world z (unrounded). Valid while the origin lies between the pockets."""
    if z > 0.0:
        return "north"
    if z < 0.0:
        return "south"
    raise HardExtractError(f"mode objects: {label} lies on z == 0; it cannot be grouped north/south")


def split_pockets(points: list):
    """Cut (x, z) points into two pockets by single linkage; None unless they are clearly two.

    Kruskal on the complete graph, stopped at two components: the next edge that joins them is the
    shortest distance between the pockets (``cut``). The groups count as pockets only when
    ``cut > POCKET_SEPARATION * spread``, the larger of the two groups' diameters (0 for a single
    point). That is also what makes the cut unambiguous: every distance inside a pocket is then
    below half of ``cut``, so no tie between equally long edges can move the partition.
    Returns ``(group_a, group_b, cut, spread)`` with sorted index lists, group_a holding point 0."""
    n = len(points)
    if n < 2:
        return None
    parent = list(range(n))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    edges = sorted((math.dist(points[i], points[j]), i, j) for i in range(n) for j in range(i + 1, n))
    components, cut = n, None
    for distance, i, j in edges:
        ri, rj = find(i), find(j)
        if ri == rj:
            continue
        if components == 2:
            cut = distance
            break
        parent[max(ri, rj)] = min(ri, rj)
        components -= 1
    first = find(0)
    group_a = [i for i in range(n) if find(i) == first]
    group_b = [i for i in range(n) if find(i) != first]
    spread = max((math.dist(points[i], points[j]) for group in (group_a, group_b)
                  for i in group for j in group), default=0.0)
    if cut is None or not cut > POCKET_SEPARATION * spread:
        return None
    return group_a, group_b, cut, spread


def _label(item: dict) -> str:
    return f"{item['scene']}/{item['path']}"


def _pocket_sides(markers: list, bounds: list, pockets: tuple) -> dict:
    """Name the two pockets north/south by mean z, set every side, return the ``side_rule`` record."""
    group_a, group_b, cut, spread = pockets
    if len(group_a) != len(group_b):
        raise HardExtractError(
            f"mode objects: the {len(markers)} spawn markers form two pockets of {len(group_a)} and "
            f"{len(group_b)}; the sides must hold the same number of markers (a missing or stray marker?)")

    def centroid(group):
        return (sum(markers[i]["position"][0] for i in group) / len(group),
                sum(markers[i]["position"][2] for i in group) / len(group))

    centroid_a, centroid_b = centroid(group_a), centroid(group_b)
    if abs(centroid_a[1] - centroid_b[1]) < POCKET_MIN_DZ:
        raise HardExtractError(
            f"mode objects: the two spawn pockets (centroids {_vec(centroid_a)} and {_vec(centroid_b)}) differ by "
            f"less than {POCKET_MIN_DZ} m in z, so 'north'/'south' would be arbitrary; name the sides by hand")
    north, south = ((group_a, group_b) if centroid_a[1] > centroid_b[1] else (group_b, group_a))
    centroids = {"north": centroid(north), "south": centroid(south)}
    for side, group in (("north", north), ("south", south)):
        for i in group:
            markers[i]["side"] = side
    for bound in bounds:
        near = {side: math.dist((bound["center"][0], bound["center"][2]), c) for side, c in centroids.items()}
        own, other = sorted(near, key=near.get)
        if not near[own] * BOUND_DOMINANCE <= near[other]:
            raise HardExtractError(
                f"mode objects: spawn bound {_label(bound)} lies {near['north']:.1f} m from the north and "
                f"{near['south']:.1f} m from the south pocket; it cannot be assigned to a side")
        bound["side"] = own
    return {"method": "pocket_split",
            "naming": "north = the pocket with the larger mean z, south = the other",
            "cut_distance": _clean(cut), "pocket_spread": _clean(spread),
            "pockets": {side: {"centroid_xz": [_clean(centroids[side][0]), _clean(centroids[side][1])],
                               "markers": len(group)}
                        for side, group in (("north", north), ("south", south))}}


def assign_sides(markers: list, bounds: list):
    """Set ``side`` on every marker and bound; return the ``side_rule`` record, or None for the sign rule.

    The pockets decide when they differ from the sign-of-z groups (or no marker can be grouped by
    sign because all lie on one side of z == 0); otherwise the original sign rule stands, so the
    sidecars of the three released maps come out byte-identical. See the module docstring."""
    points = [(m["position"][0], m["position"][2]) for m in markers]
    zs = [m["_z"] for m in markers]
    pockets = split_pockets(points)
    sign_groups = None
    if all(z != 0.0 for z in zs):
        sign_groups = {frozenset(i for i, z in enumerate(zs) if z > 0.0),
                       frozenset(i for i, z in enumerate(zs) if z < 0.0)}
    rule = None
    if pockets is not None and {frozenset(pockets[0]), frozenset(pockets[1])} != sign_groups:
        rule = _pocket_sides(markers, bounds, pockets)
    else:
        if pockets is None and markers and (all(z > 0.0 for z in zs) or all(z < 0.0 for z in zs)):
            raise HardExtractError(
                f"mode objects: all {len(markers)} spawn markers lie on one side of z == 0 and form no two "
                f"separated pockets (the gap between them must exceed {POCKET_SEPARATION:g} times the wider "
                f"pocket); the sides cannot be told apart")
        for item in markers + bounds:
            item["side"] = side_of(item["_z"], _label(item))
    for item in markers + bounds:
        del item["_z"]
    return rule


# --------------------------------------------------------------------------
# Team bounds (module docstring, "Team bounds"): sides by team, pockets by the unsuffixed bound
# --------------------------------------------------------------------------

def box_margin(bound: dict, position) -> float:
    """How far a point lies INSIDE a published spawn-bound box, in metres: the smallest distance to a
    face along the box axes (negative = outside). Uses only the sidecar's own ``center``, ``size`` and
    ``rotation``, so a consumer can repeat the test from the file."""
    qx, qy, qz = (-v for v in bound["rotation"][:3])  # conjugate: world -> box frame
    w = bound["rotation"][3]
    d = [position[i] - bound["center"][i] for i in range(3)]
    tx, ty, tz = (2.0 * (qy * d[2] - qz * d[1]), 2.0 * (qz * d[0] - qx * d[2]), 2.0 * (qx * d[1] - qy * d[0]))
    local = (d[0] + w * tx + (qy * tz - qz * ty),
             d[1] + w * ty + (qz * tx - qx * tz),
             d[2] + w * tz + (qx * ty - qy * tx))
    return min(bound["size"][i] / 2.0 - abs(local[i]) for i in range(3))


def _team_of_marker(marker: dict, rule: TeamBoundRule) -> TeamRule:
    stem = norm_name(marker["stem"])
    for team in rule.teams:
        if stem == norm_name(team.stem):
            return team
    raise HardExtractError(
        f"mode objects: spawn marker {_label(marker)} has the stem {marker['stem']!r}, which belongs to none of the "
        f"teams {[t.stem for t in rule.teams]}")


def _team_of_bound(bound: dict, rule: TeamBoundRule):
    """(TeamRule, is_unsuffixed) of a spawn bound: ``<bound>`` is the pocket, ``<bound> (n)`` a stage bound."""
    name = norm_name(bound["name"])
    for team in rule.teams:
        base = norm_name(team.bound)
        if name == base:
            return team, True
        if re.fullmatch(re.escape(base) + r"\s*\(\d+\)", name):
            return team, False
    raise HardExtractError(
        f"mode objects: spawn bound {_label(bound)} is neither {[t.bound for t in rule.teams]} nor one of their "
        f"numbered stage bounds")


def assign_team_bounds(markers: list, bounds: list, rule: TeamBoundRule) -> dict:
    """Set ``side`` (the team key) and ``pocket`` on every marker and ``side`` and ``initial`` on every bound;
    return the ``side_rule`` record. Fails closed on anything the rule cannot explain."""
    for item in markers + bounds:
        if item["scene"] != rule.scene:
            raise HardExtractError(
                f"mode objects: {_label(item)} is not in the scene {rule.scene} of the team rule (a second SPAWNS "
                f"container would need its own rule)")
    pocket_bound = {}
    stage_bounds = {team.key: 0 for team in rule.teams}
    for bound in bounds:
        team, unsuffixed = _team_of_bound(bound, rule)
        bound["side"], bound["initial"] = team.key, unsuffixed
        if unsuffixed:
            if team.key in pocket_bound:
                raise HardExtractError(f"mode objects: team {team.key} has two unsuffixed spawn bounds named {team.bound}")
            pocket_bound[team.key] = bound
        else:
            stage_bounds[team.key] += 1
    for team in rule.teams:
        if team.key not in pocket_bound:
            raise HardExtractError(f"mode objects: team {team.key} has no unsuffixed spawn bound {team.bound}")
    totals = {team.key: 0 for team in rule.teams}
    pockets = {team.key: [] for team in rule.teams}
    for marker in markers:
        team = _team_of_marker(marker, rule)
        marker["side"] = team.key
        totals[team.key] += 1
        marker["pocket"] = False
        for other in rule.teams:
            if box_margin(pocket_bound[other.key], marker["position"]) < -BOUND_EDGE_TOLERANCE:
                continue
            if other.key != team.key:
                raise HardExtractError(
                    f"mode objects: spawn marker {_label(marker)} of team {team.key} lies inside the pocket bound "
                    f"{other.bound} of team {other.key}")
            marker["pocket"] = True
            pockets[team.key].append(marker)
    for team in rule.teams:
        if not pockets[team.key]:
            raise HardExtractError(f"mode objects: no {team.stem} marker lies inside the pocket bound {team.bound}")
    for item in markers + bounds:
        del item["_z"]

    def centroid(items):
        return [_clean(sum(m["position"][axis] for m in items) / len(items)) for axis in (0, 2)]

    return {"method": "team_bounds",
            "naming": "side = the team (bear, usec) by marker stem and bound name; never north/south, never by mean z",
            "pocket": ("a marker is a pocket (initial start) marker when it lies inside its own team's unsuffixed "
                       "spawn bound (published box, edge_tolerance); every other marker belongs to a stage bound"),
            "edge_tolerance": BOUND_EDGE_TOLERANCE,
            "teams": {team.key: {"marker_stem": team.stem, "pocket_bound": team.bound,
                                 "markers": totals[team.key], "pocket_markers": len(pockets[team.key]),
                                 "stage_bounds": stage_bounds[team.key],
                                 "pocket_centroid_xz": centroid(pockets[team.key])}
                      for team in rule.teams}}


def capture_points_of(lights: list) -> list:
    """The capture-point lights as individual points: ``label`` (the ``_A`` / ``_B`` / ``_C`` suffix of the name),
    ``scene``, ``path``, ``position``. A light without a label, or two with one label, fails the run."""
    points = []
    for light in lights:
        match = CAPTURE_LABEL.search(light["path"].rsplit("/", 1)[-1])
        if match is None:
            raise HardExtractError(
                f"mode objects: capture point light {light['scene']}/{light['path']} has no _A/_B/_C label in its name")
        points.append({"label": match.group("label"), "scene": light["scene"], "path": light["path"],
                       "position": list(light["position"])})
    labels = [p["label"] for p in points]
    if len(set(labels)) != len(labels):
        raise HardExtractError(f"mode objects: capture point labels {sorted(labels)} repeat")
    return sorted(points, key=lambda p: p["label"])


# --------------------------------------------------------------------------
# Stage graph (module docstring, "Team bounds"): Arena's ArenaSpawnBoundCheckPointData, raw-decoded
# --------------------------------------------------------------------------

class _Cursor:
    def __init__(self, raw: bytes, off: int):
        self.raw, self.off = raw, off

    def i32(self) -> int:
        value = struct.unpack_from("<i", self.raw, self.off)[0]
        self.off += 4
        return value

    def pptr(self) -> tuple:
        value = struct.unpack_from("<iq", self.raw, self.off)
        self.off += 12
        return value

    def pptr_list(self, what: str) -> list:
        count = self.i32()
        if not 0 <= count <= (len(self.raw) - self.off) // 12:
            raise HardExtractError(f"mode objects: stage record {what} list claims {count} entries in "
                                   f"{len(self.raw) - self.off} remaining bytes")
        return [self.pptr() for _ in range(count)]


def _monobehaviour_body_offset(raw: bytes) -> int:
    """First script-specific byte: m_GameObject (12), m_Enabled (1, padded to 4), m_Script (12), m_Name (string, aligned 4)."""
    off = 12 + 4 + 12
    (length,) = struct.unpack_from("<i", raw, off)
    if length < 0 or off + 4 + length > len(raw):
        raise HardExtractError("mode objects: stage record has a bad m_Name length")
    return (off + 4 + length + 3) & ~3


def decode_stage_record(raw: bytes) -> dict:
    """Game.Modes.Data.ArenaSpawnBoundCheckPointData (layout read from level376 of Arena 0.6.1.0.48206, all 12 records):
    ``int team`` (1 FIGHTER, 0 DEFENCE), ``int initial``, ``int flag`` (1 everywhere, meaning unknown), the linked
    bounds (``int count`` + MonoBehaviour PPtrs), one null PPtr, the markers (count + GameObject PPtrs) and the
    capture points served (count + CapturePointEntity PPtrs). PPtr = int32 file id + int64 path id. The end offset
    must equal the object size; every PPtr must be local (file id 0) and the null one must be (0, 0)."""
    try:
        game_object = struct.unpack_from("<iq", raw, 0)
        cursor = _Cursor(raw, _monobehaviour_body_offset(raw))
        team, initial, flag = cursor.i32(), cursor.i32(), cursor.i32()
        links = cursor.pptr_list("link")
        null = cursor.pptr()
        markers = cursor.pptr_list("marker")
        capture = cursor.pptr_list("capture point")
    except struct.error as exc:
        raise HardExtractError(f"mode objects: stage record is shorter than its layout ({exc})") from exc
    if cursor.off != len(raw):
        raise HardExtractError(f"mode objects: stage record layout mismatch: ended at {cursor.off} of {len(raw)} bytes")
    if null != (0, 0):
        raise HardExtractError(f"mode objects: stage record carries the PPtr {null} where the layout has a null one")
    if initial not in (0, 1) or team not in (0, 1):
        raise HardExtractError(f"mode objects: stage record has team {team} / initial {initial}; expected 0 or 1")
    for what, pptrs in (("game object", [game_object]), ("link", links), ("marker", markers), ("capture point", capture)):
        if any(file_id != 0 for file_id, _ in pptrs):
            raise HardExtractError(f"mode objects: stage record {what} PPtr points into another file")
    return {"game_object": game_object[1], "team": team, "initial": bool(initial), "flag": flag,
            "links": [p[1] for p in links], "markers": [p[1] for p in markers], "capture": [p[1] for p in capture]}


def read_stage_records(arena_data: str, scene_index: int) -> list:
    """Every ArenaSpawnBoundCheckPointData of ``level<scene_index>`` of the installed Arena (read-only), decoded, with
    each link / marker / capture PPtr resolved to the path id of the GameObject that owns the target."""
    arena_data = str(arena_data).replace("\\", "/").rstrip("/")
    level_path = f"{arena_data}/level{scene_index}"
    if not os.path.isfile(level_path):
        raise HardExtractError(f"mode objects: {level_path} does not exist")
    if ANALYSIS_SCRIPTS not in sys.path:
        sys.path.insert(0, ANALYSIS_SCRIPTS)
    try:
        import UnityPy
        import census
        from common import scene_files
    except ImportError as exc:
        raise HardExtractError(f"mode objects: reading the stage table needs UnityPy and analysis/scripts ({exc})") from exc
    paths = scene_files({scene_index: "scene"}, base=arena_data)
    env = UnityPy.load(*paths, f"{arena_data}/globalgamemanagers.assets")
    level = env.files.get(level_path) or next(
        (f for key, f in env.files.items() if key.replace("\\", "/").endswith(f"/level{scene_index}")), None)
    if level is None:
        raise HardExtractError(f"mode objects: UnityPy did not load level{scene_index}")

    def owner(path_id: int) -> int:
        obj = level.objects.get(path_id)
        if obj is None:
            raise HardExtractError(f"mode objects: stage record names object {path_id}, which level{scene_index} does not have")
        if obj.type.name == "GameObject":
            return path_id
        if obj.type.name == "MonoBehaviour":
            return obj.parse_monobehaviour_head().m_GameObject.path_id
        return obj.read_typetree()["m_GameObject"]["m_PathID"]

    records = []
    for obj in list(level.objects.values()):
        if obj.type.name != "MonoBehaviour":
            continue
        class_name, status = census.resolve_script_name(obj)
        if status != "ok" or not class_name or class_name.rsplit(".", 1)[-1] != STAGE_CLASS:
            continue
        record = decode_stage_record(obj.get_raw_data())
        for key in ("links", "markers", "capture"):
            record[key] = [owner(pid) for pid in record[key]]
        records.append(record)
    return records


def scene_index_of(plan_path: Path, scene_name: str) -> int:
    """BuildSettings index of a scene named in a source plan's ``scenes`` (the plan is what pins the mapping)."""
    from source_plan import PlanError, load_plan, registry_for
    try:
        plan = load_plan(Path(plan_path))
    except PlanError as exc:
        raise HardExtractError(f"mode objects: {exc}") from exc
    for record in registry_for(plan):
        if record["name"] == scene_name:
            return record["index"]
    raise HardExtractError(f"mode objects: the plan {plan_path} lists no scene {scene_name}")


def _name_key(name: str) -> tuple:
    match = INDEX_SUFFIX.match(name.strip())
    return (match.group("stem"), int(match.group("index"))) if match else (name.strip(), -1)


def build_stages(records: list, graph: SceneGraph, rule: TeamBoundRule, markers: list, bounds: list,
                 capture_points: list) -> list:
    """The stage list of the sidecar from decoded records (GameObject path ids) and the package scene. Every
    cross-check of the module docstring fails the run; nothing is guessed."""
    bound_by_path = {b["path"]: b for b in bounds}
    marker_by_path = {m["path"]: m for m in markers}
    capture_by_path = {c["path"]: c["label"] for c in capture_points}
    team_by_key = {t.key: t for t in rule.teams}

    def resolve(path_id: int, what: str) -> str:
        obj = graph.by_id.get(str(path_id))
        if obj is None:
            raise HardExtractError(
                f"mode objects: stage table {what} names GameObject {path_id}, which the package scene {graph.name} "
                f"does not have (the installed Arena is not the build the package came from)")
        return graph.path(obj)

    stages, listed = {}, {}
    for record in records:
        where = resolve(record["game_object"], "record")
        bound = bound_by_path.get(where)
        if bound is None:
            raise HardExtractError(f"mode objects: a stage record sits on {where}, which is not a spawn bound")
        name = bound["name"]
        if name in stages:
            raise HardExtractError(f"mode objects: two stage records for {name}")
        team = team_by_key[bound["side"]]
        if record["team"] != team.arena_team:
            raise HardExtractError(f"mode objects: {name} says team {record['team']} in Arena's record, the rule has "
                                   f"{team.key} = {team.arena_team}")
        if record["initial"] != bound["initial"]:
            raise HardExtractError(f"mode objects: {name} has initial={record['initial']} in Arena's record, but the "
                                   f"name says {bound['initial']}")
        links = []
        for path_id in record["links"]:
            linked = bound_by_path.get(resolve(path_id, f"{name} link"))
            if linked is None:
                raise HardExtractError(f"mode objects: {name} links to a GameObject that is not a spawn bound")
            links.append(linked["name"])
        names = []
        for path_id in record["markers"]:
            marker = marker_by_path.get(resolve(path_id, f"{name} marker"))
            if marker is None:
                raise HardExtractError(f"mode objects: {name} lists a GameObject that is not a spawn marker")
            if marker["side"] != bound["side"]:
                raise HardExtractError(f"mode objects: {name} ({bound['side']}) lists {marker['name']} of team {marker['side']}")
            if marker["name"] in listed:
                raise HardExtractError(f"mode objects: {marker['name']} is listed by {listed[marker['name']]} and by {name}")
            listed[marker["name"]] = name
            names.append(marker["name"])
        labels = []
        for path_id in record["capture"]:
            label = capture_by_path.get(resolve(path_id, f"{name} capture point"))
            if label is None:
                raise HardExtractError(f"mode objects: {name} serves a GameObject that is not a capture point light")
            labels.append(label)
        stages[name] = {"bound": name, "team": bound["side"], "initial": bound["initial"], "flag": record["flag"],
                        "links": sorted(links, key=_name_key), "capture_points": sorted(labels),
                        "markers": sorted(names, key=_name_key)}
    for bound in bounds:
        if bound["name"] not in stages:
            raise HardExtractError(f"mode objects: spawn bound {bound['name']} has no stage record")
    unlisted = sorted(m["name"] for m in markers if m["name"] not in listed)
    if unlisted:
        raise HardExtractError(f"mode objects: {len(unlisted)} spawn marker(s) belong to no stage record: {unlisted[:5]}")
    for team in rule.teams:
        pocket = sorted(m["name"] for m in markers if m["side"] == team.key and m["pocket"])
        initial = next(s for s in stages.values() if s["team"] == team.key and s["initial"])
        if sorted(initial["markers"]) != pocket:
            raise HardExtractError(
                f"mode objects: Arena's initial record {initial['bound']} lists {sorted(initial['markers'])} but the "
                f"markers inside the bound are {pocket}")
    order = {team.key: i for i, team in enumerate(rule.teams)}
    return sorted(stages.values(), key=lambda s: (order[s["team"]], not s["initial"], _name_key(s["bound"])))


def _first_component(obj: dict, kind: str):
    for component in obj.get("components", []):
        if component.get("type") == kind:
            return component
    return None


def spawn_bound(graph: SceneGraph, obj: dict) -> dict:
    box = _first_component(obj, "BoxCollider")
    where = f"{graph.name}/{graph.path(obj)}"
    if box is None:
        raise HardExtractError(f"mode objects: spawn bound {where} has no BoxCollider")
    matrix = graph.world(obj)
    scale = lossy_scale(matrix)
    size = [abs(s) * k for s, k in zip(_finite(box["size"], 3, where + " BoxCollider.size"), scale)]
    center = point(matrix, tuple(_finite(box["center"], 3, where + " BoxCollider.center")))
    return {"scene": graph.name, "path": graph.path(obj), "name": obj["name"].strip(),
            "center": _vec(center), "size": _vec(size), "rotation": rotation_quaternion(matrix),
            "yaw": yaw_degrees(matrix), "side": None, "_z": center[2],
            "active": graph.active_in_hierarchy(obj)}


def spawn_marker(graph: SceneGraph, obj: dict) -> dict:
    matrix = graph.world(obj)
    position = point(matrix)
    stem, index = obj["name"].strip(), None
    suffix = INDEX_SUFFIX.match(stem)
    if suffix:
        stem, index = suffix.group("stem"), int(suffix.group("index"))
    return {"scene": graph.name, "path": graph.path(obj), "name": obj["name"].strip(), "stem": stem,
            "index": index, "position": _vec(position), "yaw": yaw_degrees(matrix),
            "side": None, "_z": position[2], "active": graph.active_in_hierarchy(obj)}


def _at_origin(position) -> bool:
    return max(abs(v) for v in position) < 1e-3


def capture_zone(graph: SceneGraph, obj: dict) -> dict:
    """A Capture_Zone root, its PROJECTION child, and the position that actually locates it.

    ``anchor_position`` is the PROJECTION child's world position when that is not the origin,
    else the root's when that is not the origin, else null (Equator's and Bay5's roots are
    authored at the origin and say nothing about where the point is)."""
    position = graph.position(obj)
    projection = [c for c in graph.children.get(obj["id"], []) if norm_name(c["name"]) == "projection"]
    projection_position = graph.position(projection[0]) if projection else None
    anchor = next((p for p in (projection_position, position) if p is not None and not _at_origin(p)), None)
    return {"scene": graph.name, "path": graph.path(obj), "position": _vec(position),
            "at_origin": _at_origin(position),
            "projection_position": None if projection_position is None else _vec(projection_position),
            "anchor_position": None if anchor is None else _vec(anchor)}


def capture_lights(graph: SceneGraph) -> list:
    lights = []
    for container in graph.objects:
        if norm_name(container["name"]) != "capture_point":
            continue
        for child in graph.children.get(container["id"], []):
            if "capture_point_light" in norm_name(child["name"]):
                lights.append({"scene": graph.name, "path": graph.path(child),
                               "position": _vec(graph.position(child))})
    return lights


def resolve_capture_point(lights: list, zones: list):
    """(capture_point dict or None). See the module docstring for the rule and its evidence."""
    if lights:
        n = len(lights)
        mean = [sum(light["position"][axis] for light in lights) / n for axis in range(3)]
        return {"position": _vec(mean), "method": "capture_point_light_centroid", "lights": lights}
    anchored = [zone for zone in zones if zone["anchor_position"] is not None]
    if anchored:
        return {"position": list(anchored[0]["anchor_position"]), "method": "capture_zone_anchor", "lights": []}
    return None


def _sort_markers(markers: list) -> list:
    return sorted(markers, key=lambda m: (m["scene"], m["stem"], m["index"] if m["index"] is not None else -1, m["path"]))


def extract_scenes(graphs: list, rule: TeamBoundRule | None = None) -> dict:
    """Mode objects of the given SceneGraphs (scene order as given). ``rule`` (``TEAM_BOUND_RULES[map key]``)
    replaces the two-pocket side rule by the team rule and the capture point by the individual points."""
    lights, zones, bounds, markers = [], [], [], []
    for graph in graphs:
        lights.extend(capture_lights(graph))
        for obj in graph.objects:
            name = norm_name(obj["name"])
            if name == "capture_zone":
                zones.append(capture_zone(graph, obj))
            elif name.startswith("spawn_bound"):
                bounds.append(spawn_bound(graph, obj))
            elif name == "spawns":
                for child in graph.children.get(obj["id"], []):
                    if not norm_name(child["name"]).startswith("spawn_bound"):
                        markers.append(spawn_marker(graph, child))
    if rule is None:
        capture, capture_points = resolve_capture_point(lights, zones), None
    else:  # several capture points: no centroid, every point on its own
        capture, capture_points = None, capture_points_of(lights)
    for zone in zones:
        zone["distance_to_capture_point"] = (None if capture is None or zone["anchor_position"] is None
                                             else _clean(math.dist(capture["position"], zone["anchor_position"])))
    if rule is None and capture is None:
        _warn("no capture point marker (no CAPTURE_POINT light child, no Capture_Zone away from the origin)")
    if rule is not None and not capture_points:
        _warn("no CAPTURE_POINT light child: the team-bound map lists no capture points")
    if not bounds:
        _warn("no SPAWN_BOUND* object")
    if not markers:
        _warn("no SPAWNS container children")
    side_rule = assign_team_bounds(markers, bounds, rule) if rule is not None else assign_sides(markers, bounds)
    bounds.sort(key=lambda b: (b["scene"], b["side"], b["path"]))
    zones.sort(key=lambda z: (z["scene"], z["path"]))
    sides = tuple(team.key for team in rule.teams) if rule is not None else ("north", "south")
    found = {"capture_point": capture, "capture_zones": zones, "spawn_bounds": bounds,
             "spawn_markers": {side: _sort_markers([m for m in markers if m["side"] == side]) for side in sides}}
    if capture_points is not None:
        found["capture_points"] = capture_points
    if side_rule is not None:  # only when the original sign-of-z rule did not decide (Bowl) or a team rule ran (Iceberg)
        found["side_rule"] = side_rule
    return found


def map_key_of(manifest: dict, override: str | None = None) -> str:
    source = manifest.get("source", {})
    key = override or source.get("map_key") or LEGACY_MAP_KEYS.get(source.get("extractor_script"))
    if not isinstance(key, str) or not MAP_KEY.fullmatch(key):
        raise HardExtractError(f"mode objects: cannot tell the map key of this package (got {key!r}); pass --map-key")
    return key


def extract_package(package: Path, map_key: str | None = None, arena_data=None, plan_path=None) -> dict:
    """The sidecar of one package. ``arena_data`` (the installed Arena's ``EscapeFromTarkovArena_Data``) adds the
    ``stages`` block of a team-bound map (read-only; ``plan_path`` defaults to ``plans/<stem>.json`` and gives the
    BuildSettings index of the rule's scene); it is ignored for a map without a team rule."""
    package = Path(package)
    try:
        manifest = json.loads((package / "manifest.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise HardExtractError(f"mode objects: cannot read {package / 'manifest.json'}: {exc}") from exc
    key = map_key_of(manifest, map_key)
    names = manifest.get("scenes")
    if not isinstance(names, list) or not names:
        raise HardExtractError("mode objects: manifest lists no scenes")
    graphs = []
    for name in names:
        path = package / "scenes" / f"{name}.json"
        try:
            graphs.append(SceneGraph(name, json.loads(path.read_text(encoding="utf-8"))))
        except (OSError, json.JSONDecodeError) as exc:
            raise HardExtractError(f"mode objects: cannot read scene {path}: {exc}") from exc
    rule = TEAM_BOUND_RULES.get(key)
    found = extract_scenes(graphs, rule)
    source = manifest.get("source", {})
    markers = found["spawn_markers"]
    head = {
        "schema_version": SCHEMA_VERSION,
        "map_key": key,
        "source_package": {"name": package.resolve().name, "extracted_utc": source.get("extracted_utc"),
                           "extractor_script": source.get("extractor_script"), "scenes": list(names)},
    }
    if rule is None:
        return {
            "_comment": ("Arena TeamFight mode objects (positions only) composed from the extracted scenes by "
                         "tools/extract/extract_mode_objects.py; schema and method in docs/progress/airpit-release.md. "
                         "Data for ArenaMode, not read by the map mod itself."),
            **head,
            "counts": {"capture_zones": len(found["capture_zones"]), "spawn_bounds": len(found["spawn_bounds"]),
                       "spawn_markers_north": len(markers["north"]), "spawn_markers_south": len(markers["south"])},
            **found,
        }
    out = {
        "_comment": (f"Arena {rule.mode} mode objects (positions only) composed from the extracted scenes by "
                     "tools/extract/extract_mode_objects.py with the team-bound rule (sides are the teams, pockets are "
                     "the unsuffixed spawn bounds, every capture point on its own); schema and method in "
                     "docs/progress/iceberg-release.md. Data for ArenaMode, not read by the map mod itself."),
        **head,
        "counts": {"capture_points": len(found["capture_points"]), "capture_zones": len(found["capture_zones"]),
                   "spawn_bounds": len(found["spawn_bounds"]),
                   **{f"spawn_markers_{team.key}": len(markers[team.key]) for team in rule.teams},
                   **{f"pocket_markers_{team.key}": sum(1 for m in markers[team.key] if m["pocket"])
                      for team in rule.teams}},
        **found,
    }
    if arena_data is not None:
        graph = next((g for g in graphs if g.name == rule.scene), None)
        if graph is None:
            raise HardExtractError(f"mode objects: the package has no scene {rule.scene}")
        plan = Path(plan_path) if plan_path else DEFAULT_PLANS_DIR / f"{key.removeprefix('arena_')}.json"
        index = scene_index_of(plan, rule.scene)
        records = read_stage_records(arena_data, index)
        out["stages"] = build_stages(records, graph, rule, [m for team in rule.teams for m in markers[team.key]],
                                     found["spawn_bounds"], found["capture_points"])
        out["stages_source"] = {"scene": rule.scene, "scene_index": index, "records": len(records),
                                "class": "Game.Modes.Data." + STAGE_CLASS,
                                "note": "raw-decoded from the installed Arena level file by extract_mode_objects.py "
                                        "(--arena-data); not part of the extracted package"}
    return out


def default_output_path(map_key: str) -> Path:
    return DEFAULT_MAPS_DIR / f"{map_key.removeprefix('arena_')}.modes.json"


def dumps(data: dict) -> str:
    return json.dumps(data, indent=2, sort_keys=True, ensure_ascii=False, allow_nan=False) + "\n"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--package", required=True, type=Path, help="extracted package directory")
    parser.add_argument("--map-key", help="arena_<stem>; needed only when the manifest does not say")
    parser.add_argument("--output", type=Path, help="default tools/maps/<stem>.modes.json")
    parser.add_argument("--arena-data", help="the installed Arena's EscapeFromTarkovArena_Data (read-only); adds the "
                                             "stages block of a map with a team rule (Iceberg), ignored otherwise")
    parser.add_argument("--plan", type=Path, help="source plan for the stage scene's BuildSettings index "
                                                  "(default tools/extract/plans/<stem>.json)")
    args = parser.parse_args(argv)
    try:
        data = extract_package(args.package, args.map_key, args.arena_data, args.plan)
    except HardExtractError as exc:
        print(f"[extract_mode_objects] FAILED: {exc}", file=sys.stderr)
        return 2
    output = args.output or default_output_path(data["map_key"])
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(dumps(data), encoding="utf-8", newline="\n")
    counts = data["counts"]
    if "capture_points" in data:
        teams = ", ".join(f"{team} {counts['spawn_markers_' + team]} markers ({counts['pocket_markers_' + team]} in the pocket)"
                          for team in data["side_rule"]["teams"])
        print(f"[extract_mode_objects] {data['map_key']}: {counts['capture_points']} capture point(s) "
              f"{[p['label'] for p in data['capture_points']]}, {counts['spawn_bounds']} spawn bound(s), {teams} (team bounds)"
              f"{', ' + str(len(data['stages'])) + ' stage record(s)' if 'stages' in data else ', no stage table'} "
              f"-> {output}", file=sys.stderr)
        return 0
    capture = data["capture_point"]
    print(f"[extract_mode_objects] {data['map_key']}: capture point "
          f"{capture['position'] if capture else None} ({capture['method'] if capture else 'none'}), "
          f"{counts['spawn_bounds']} spawn bound(s), markers north {counts['spawn_markers_north']} / "
          f"south {counts['spawn_markers_south']}{' (pocket split)' if 'side_rule' in data else ''} "
          f"-> {output}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
