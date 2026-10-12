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
Nothing is read from the Arena install and nothing is written outside ``--output``.

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
  the source), world ``rotation`` quaternion, ``yaw``, and ``side`` by the sign of the
  centre z.
* ``spawn_markers`` - the direct children of the object named ``SPAWNS`` other than the
  bounds: Equator and AirPit ``ScavPMCspawn (1..20)`` (a SphereCollider each), Bay5
  ``Bear house (n)`` / ``Usec house (n)`` (a BoxCollider each). World ``position``, ``yaw`` in
  degrees [0, 360) (Unity convention: 0 looks along +z, increasing towards +x), ``stem``
  and ``index`` from the ``name (n)`` suffix, ``active`` (active in hierarchy). They are
  grouped ``north`` (z > 0) and ``south`` (z < 0); a marker on z == 0 cannot be grouped and
  fails the run.

Transforms are composed as translate * rotate * scale with unit quaternions and no shear
handling (Unity's lossy-scale approximation); values are rounded to 1e-4 m / degree, and
-0.0 is written as 0.0, so the output is byte-reproducible.

Fail closed (``HardExtractError``): a parent id that names no object, a parent cycle, a
spawn bound without a BoxCollider, a marker on z == 0, a malformed vector. Missing
categories only warn (stderr) and appear as empty lists / null.

CLI:
    python tools/extract/extract_mode_objects.py --package work/extract/equator-native-v5
    python tools/extract/extract_mode_objects.py --package work/extract/bay5-native-v5
    python tools/extract/extract_mode_objects.py --package work/extract/airpit-native-v6
    (the map key comes from the manifest, or from the extractor script for the two legacy
     packages, or from --map-key; the output defaults to tools/maps/<stem>.modes.json)
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from common_ids import HardExtractError  # noqa: E402

SCHEMA_VERSION = 1
DEFAULT_MAPS_DIR = Path(_HERE).parent / "maps"
LEGACY_MAP_KEYS = {
    "tools/extract/extract_equator.py": "arena_equator",
    "tools/extract/extract_bay5.py": "arena_bay5",
}
MAP_KEY = re.compile(r"arena_[a-z0-9_]+")
INDEX_SUFFIX = re.compile(r"^(?P<stem>.*?)\s*\((?P<index>\d+)\)$")
ROUND = 4  # decimals: 1e-4 m / degree


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
    if z > 0.0:
        return "north"
    if z < 0.0:
        return "south"
    raise HardExtractError(f"mode objects: {label} lies on z == 0; it cannot be grouped north/south")


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
            "yaw": yaw_degrees(matrix), "side": side_of(center[2], where),
            "active": graph.active_in_hierarchy(obj)}


def spawn_marker(graph: SceneGraph, obj: dict) -> dict:
    where = f"{graph.name}/{graph.path(obj)}"
    matrix = graph.world(obj)
    position = point(matrix)
    stem, index = obj["name"].strip(), None
    suffix = INDEX_SUFFIX.match(stem)
    if suffix:
        stem, index = suffix.group("stem"), int(suffix.group("index"))
    return {"scene": graph.name, "path": graph.path(obj), "name": obj["name"].strip(), "stem": stem,
            "index": index, "position": _vec(position), "yaw": yaw_degrees(matrix),
            "side": side_of(position[2], where), "active": graph.active_in_hierarchy(obj)}


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


def extract_scenes(graphs: list) -> dict:
    """Mode objects of the given SceneGraphs (scene order as given)."""
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
    capture = resolve_capture_point(lights, zones)
    for zone in zones:
        zone["distance_to_capture_point"] = (None if capture is None or zone["anchor_position"] is None
                                             else _clean(math.dist(capture["position"], zone["anchor_position"])))
    if capture is None:
        _warn("no capture point marker (no CAPTURE_POINT light child, no Capture_Zone away from the origin)")
    if not bounds:
        _warn("no SPAWN_BOUND* object")
    if not markers:
        _warn("no SPAWNS container children")
    bounds.sort(key=lambda b: (b["scene"], b["side"], b["path"]))
    zones.sort(key=lambda z: (z["scene"], z["path"]))
    return {"capture_point": capture, "capture_zones": zones, "spawn_bounds": bounds,
            "spawn_markers": {side: _sort_markers([m for m in markers if m["side"] == side])
                              for side in ("north", "south")}}


def map_key_of(manifest: dict, override: str | None = None) -> str:
    source = manifest.get("source", {})
    key = override or source.get("map_key") or LEGACY_MAP_KEYS.get(source.get("extractor_script"))
    if not isinstance(key, str) or not MAP_KEY.fullmatch(key):
        raise HardExtractError(f"mode objects: cannot tell the map key of this package (got {key!r}); pass --map-key")
    return key


def extract_package(package: Path, map_key: str | None = None) -> dict:
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
    found = extract_scenes(graphs)
    source = manifest.get("source", {})
    markers = found["spawn_markers"]
    return {
        "_comment": ("Arena TeamFight mode objects (positions only) composed from the extracted scenes by "
                     "tools/extract/extract_mode_objects.py; schema and method in docs/progress/airpit-release.md. "
                     "Data for ArenaMode, not read by the map mod itself."),
        "schema_version": SCHEMA_VERSION,
        "map_key": key,
        "source_package": {"name": package.resolve().name, "extracted_utc": source.get("extracted_utc"),
                           "extractor_script": source.get("extractor_script"), "scenes": list(names)},
        "counts": {"capture_zones": len(found["capture_zones"]), "spawn_bounds": len(found["spawn_bounds"]),
                   "spawn_markers_north": len(markers["north"]), "spawn_markers_south": len(markers["south"])},
        **found,
    }


def default_output_path(map_key: str) -> Path:
    return DEFAULT_MAPS_DIR / f"{map_key.removeprefix('arena_')}.modes.json"


def dumps(data: dict) -> str:
    return json.dumps(data, indent=2, sort_keys=True, ensure_ascii=False, allow_nan=False) + "\n"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--package", required=True, type=Path, help="extracted package directory")
    parser.add_argument("--map-key", help="arena_<stem>; needed only when the manifest does not say")
    parser.add_argument("--output", type=Path, help="default tools/maps/<stem>.modes.json")
    args = parser.parse_args(argv)
    try:
        data = extract_package(args.package, args.map_key)
    except HardExtractError as exc:
        print(f"[extract_mode_objects] FAILED: {exc}", file=sys.stderr)
        return 2
    output = args.output or default_output_path(data["map_key"])
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(dumps(data), encoding="utf-8", newline="\n")
    capture = data["capture_point"]
    print(f"[extract_mode_objects] {data['map_key']}: capture point "
          f"{capture['position'] if capture else None} ({capture['method'] if capture else 'none'}), "
          f"{data['counts']['spawn_bounds']} spawn bound(s), markers north {data['counts']['spawn_markers_north']} / "
          f"south {data['counts']['spawn_markers_south']} -> {output}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
