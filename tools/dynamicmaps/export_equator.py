"""Generate local-only Equator, Bay5 and AirPit DynamicMaps plans from extracted geometry.

No game data ships with this tool. Install requirements in a dedicated venv.
The drawing is a cartographic approximation, not a navigation or collision mesh.
"""
from __future__ import annotations

import argparse
from collections import Counter
from functools import lru_cache
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

import numpy as np
from shapely import GeometryCollection, LineString, Point, Polygon, box, make_valid, set_precision
from shapely import STRtree, points, shortest_line, get_point, get_coordinates
from shapely.ops import unary_union


LEGACY_NORTH_ARROW = '<path fill="#d6b56d" d="M5,2 L3.8,5 L5,4.3 L6.2,5 Z"/>'
# Paths keep the N visible in Unity.VectorGraphics without font/text support.
# The gold half points to world +Z (SVG up); this is not the live player marker.
NORTH_MARKER_SVG = '''<g id="arena-north-marker">
<path fill="#151c24" d="M2,0.5 H8 V10 H2 Z"/>
<path fill="#f1eee5" d="M3.7,3.7 V1 H4.3 L5.7,2.8 V1 H6.3 V3.7 H5.7 L4.3,1.9 V3.7 Z"/>
<path fill="#d6b56d" d="M5,4.4 L3.5,7.2 H6.5 Z"/>
<path fill="#697783" d="M5,9.3 L3.5,7.2 H6.5 Z"/>
</g>'''


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read_mesh(path):
    raw = Path(path).read_bytes()
    if len(raw) < 16:
        raise ValueError(f"Truncated mesh: {path}")
    magic, version, count, mask, index_format, sub_count = struct.unpack_from("<4sIIHBB", raw)
    if magic != b"AMPM" or version != 1 or index_format not in (0, 1) or not mask & 1 or mask >> 12:
        raise ValueError(f"Unsupported mesh: {path}")
    floats = 3 + (3 if mask & 2 else 0) + (4 if mask & 4 else 0) + (4 if mask & 8 else 0)
    floats += sum(2 for bit in range(4, 12) if mask & (1 << bit))
    offset = 16 + count * floats * 4
    end = offset + sub_count * 9
    stride = 2 if index_format == 0 else 4
    if end > len(raw) or (len(raw) - end) % stride:
        raise ValueError(f"Truncated mesh channels: {path}")
    vertices = np.frombuffer(raw, "<f4", count * 3, 16).reshape(-1, 3).astype(float)
    indices = np.frombuffer(raw, "<u2" if stride == 2 else "<u4", offset=end)
    if not np.isfinite(vertices).all():
        raise ValueError(f"Non-finite vertices: {path}")
    triangles = []
    for i in range(sub_count):
        start, length, topology = struct.unpack_from("<IIB", raw, offset + i * 9)
        if start + length > len(indices):
            raise ValueError(f"Truncated mesh indices: {path}")
        if topology != 0:
            raise ValueError(f"Non-triangle collider topology: {path}")
        if length % 3 or (length and indices[start:start + length].max() >= count):
            raise ValueError(f"Invalid mesh indices: {path}")
        triangles.append(indices[start:start + length].reshape(-1, 3))
    return vertices, np.concatenate(triangles) if triangles else np.empty((0, 3), dtype=int)


def local_matrix(owner):
    x, y, z, w = owner["local_rotation"]
    result = np.eye(4)
    result[:3, :3] = np.array([
        [1-2*y*y-2*z*z, 2*x*y-2*z*w, 2*x*z+2*y*w],
        [2*x*y+2*z*w, 1-2*x*x-2*z*z, 2*y*z-2*x*w],
        [2*x*z-2*y*w, 2*y*z+2*x*w, 1-2*x*x-2*y*y],
    ]) @ np.diag(owner["local_scale"])
    result[:3, 3] = owner["local_position"]
    if not np.isfinite(result).all():
        raise ValueError("Non-finite transform")
    return result


class Scene:
    def __init__(self, data):
        self.objects = {o["id"]: o for o in data["objects"]}
        if len(self.objects) != len(data["objects"]):
            raise ValueError("Duplicate object ID")

    @lru_cache(maxsize=None)
    def lineage(self, key):
        result, seen = [], set()
        while key is not None:
            if key in seen:
                raise ValueError("Cyclic hierarchy")
            seen.add(key)
            obj = self.objects[key]
            result.append(obj)
            key = obj.get("parent_id")
        return tuple(reversed(result))

    @lru_cache(maxsize=None)
    def world(self, key):
        result = np.eye(4)
        for obj in self.lineage(key):
            result = result @ local_matrix(obj)
        return result

    def included(self, key, opened):
        chain = self.lineage(key)
        if any(not o.get("active", True) for o in chain):
            return False
        if chain[-1]["layer"] in (4, 13, 29):
            return False
        names = [o["name"] for o in chain]
        if any(n.startswith(("SPAWN_BOUND", "LevelBorder")) or n in (
                "Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R") for n in names):
            return False
        path = "/".join(names)
        for barrier in opened:
            if path.startswith(barrier + "/") and "frame" not in path[len(barrier)+1:].lower():
                return False
        return True


BOX_TRIANGLES = np.array([
    [0, 1, 3], [0, 3, 2], [4, 6, 7], [4, 7, 5],
    [0, 4, 5], [0, 5, 1], [2, 3, 7], [2, 7, 6],
    [0, 2, 6], [0, 6, 4], [1, 5, 7], [1, 7, 3],
])


def primitive(component):
    """Box exact; round colliders deliberately use a conservative bounding box."""
    kind = component["type"]
    if kind == "BoxCollider":
        size = np.array(component["size"], dtype=float)
    elif kind in ("SphereCollider", "CapsuleCollider"):
        size = np.full(3, 2 * component["radius"], dtype=float)
        if kind == "CapsuleCollider":
            axis = component.get("direction", 1)
            size[axis] = max(size[axis], component["height"])
    else:
        raise ValueError(kind)
    vertices = np.array([[x, y, z] for x in (-.5, .5) for y in (-.5, .5) for z in (-.5, .5)])
    return vertices * size + component.get("center", [0, 0, 0]), BOX_TRIANGLES


def clip_height(points, height, above):
    """Sutherland-Hodgman clipping in 3D against one horizontal plane."""
    result = []
    for a, b in zip(points, np.roll(points, -1, axis=0)):
        a_in = a[1] >= height if above else a[1] <= height
        b_in = b[1] >= height if above else b[1] <= height
        if a_in:
            result.append(a)
        if a_in != b_in:
            result.append(a + (b-a) * ((height-a[1]) / (b[1]-a[1])))
    return np.array(result).reshape(-1, 3)


def project_slab(triangles, low, high):
    parts = []
    heights = triangles[:, :, 1]
    candidates = triangles[(heights.max(axis=1) >= low) & (heights.min(axis=1) <= high)]
    for triangle in candidates:
        points = clip_height(triangle, low, True)
        if len(points) < 2:
            continue
        points = clip_height(points, high, False)
        if len(points) < 2:
            continue
        xz = points[:, (0, 2)]
        poly = Polygon(xz) if len(xz) >= 3 else Polygon()
        if poly.area > 1e-7:
            parts.append(make_valid(poly))
        elif np.linalg.norm(xz.max(axis=0)-xz.min(axis=0)) > .03:
            parts.append(LineString(xz).buffer(.055, cap_style="flat", join_style="mitre"))
    return unary_union(parts) if parts else GeometryCollection()


def polygons(geom):
    if geom.is_empty:
        return
    if geom.geom_type == "Polygon":
        yield geom
    elif hasattr(geom, "geoms"):
        for child in geom.geoms:
            yield from polygons(child)


def clean(geom, min_area=.15, tolerance=.12, min_hole_area=.15):
    # Remove sub-footstep holes/noise; preserve larger gaps and courtyard holes.
    parts = []
    for p in polygons(make_valid(geom)):
        if p.area < min_area:
            continue
        p = Polygon(p.exterior, [ring for ring in p.interiors if Polygon(ring).area >= min_hole_area])
        parts.append(p.simplify(tolerance, preserve_topology=True))
    return unary_union(parts)


def svg_point(x, z, bounds):
    return x - bounds[0], bounds[3] - z


def svg_path(geom, bounds):
    paths, count = [], 0
    # Quantize topologically before formatting: independently rounding near-touching
    # vertices can otherwise introduce self-intersections in the native tessellator.
    geom = set_precision(make_valid(geom), .001, mode="valid_output")
    for p in sorted(polygons(geom), key=lambda p: (*p.bounds, p.area)):
        for ring in [p.exterior, *p.interiors]:
            pts = [svg_point(x, z, bounds) for x, z in list(ring.coords)[:-1]]
            count += len(pts)
            paths.append("M" + " L".join(f"{x:.3f},{y:.3f}" for x, y in pts) + " Z")
    return " ".join(paths), count


def render_preview(path, layers, bounds):
    from PIL import Image, ImageDraw
    scale = 10
    width, height = bounds[2]-bounds[0], bounds[3]-bounds[1]
    image = Image.new("RGB", (math.ceil(width*scale), math.ceil(height*scale)), "#151c24")
    for geom, color in layers:
        mask = Image.new("L", image.size)
        draw = ImageDraw.Draw(mask)
        for p in polygons(geom):
            for idx, ring in enumerate([p.exterior, *p.interiors]):
                draw.polygon([(u*scale, v*scale) for u, v in (svg_point(x, z, bounds) for x, z in ring.coords)],
                             fill=255 if idx == 0 else 0)
        image.paste(color, mask=mask)
    image.save(path)


def native_triangles(path, height_range=(-.5, 1.2)):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    verts = np.array([[v[axis] for axis in ("x", "y", "z")] for v in data["vertices"]], dtype=float)
    indices = np.array(data["indices"])
    if (not len(verts) or not np.isfinite(verts).all() or indices.ndim != 1 or indices.size % 3
            or not np.issubdtype(indices.dtype, np.integer) or indices.min() < 0 or indices.max() >= len(verts)):
        raise ValueError("Invalid native triangulation")
    triangles = verts[indices.reshape(-1, 3)]
    ground = triangles[(triangles[:, :, 1].min(axis=1) >= height_range[0])
                       & (triangles[:, :, 1].max(axis=1) <= height_range[1])]
    return ground


def read_triangulation(path, height_range=(-.5, 1.2)):
    ground = native_triangles(path, height_range)
    result = clean(unary_union([Polygon(t[:, (0, 2)]) for t in ground]), min_area=.25, tolerance=.045)
    if result.is_empty:
        raise ValueError("Native triangulation has no ground area")
    return result


class GroundSurface:
    """Sample the nearest native ground triangle in XZ, including its slope.

    Clamped closest points prevent extrapolating ramps far outside their triangles.
    This supplies cartographic reference heights, not runtime collision queries.
    """
    def __init__(self, triangles, region):
        kept, shapes = [], []
        for tri in triangles:
            shape = Polygon(tri[:, (0, 2)])
            if shape.area > 1e-7 and shape.intersects(region):
                kept.append(tri)
                shapes.append(shape)
        if not kept:
            raise ValueError("No native ground triangles in the drawing region")
        self.triangles = np.array(kept)
        self.shapes = np.array(shapes, dtype=object)
        self.tree = STRtree(self.shapes)

    def heights(self, xz):
        queries = points(np.asarray(xz))
        source, selected = self.tree.query_nearest(queries, all_matches=False)
        nearest = get_coordinates(get_point(shortest_line(queries[source], self.shapes[selected]), 1))
        tri = self.triangles[selected]
        a, b, c = tri[:, 0, :][:, (0, 2)], tri[:, 1, :][:, (0, 2)], tri[:, 2, :][:, (0, 2)]
        v0, v1, v2 = b-a, c-a, nearest-a
        cross = lambda u, v: u[:, 0]*v[:, 1]-u[:, 1]*v[:, 0]
        denominator = cross(v0, v1)
        wb, wc = cross(v2, v1)/denominator, cross(v0, v2)/denominator
        result = np.empty(len(xz))
        result[source] = (1-wb-wc)*tri[:, 0, 1] + wb*tri[:, 1, 1] + wc*tri[:, 2, 1]
        if not np.isfinite(result).all():
            raise ValueError("Non-finite native ground sample")
        return result


MAP_PROFILES = {
    "arena_equator": {"name": "Equator", "folder": "ArenaMapPort_Equator", "title": "EQUATOR",
                       "heights": (-.5, 1.2), "anchor": (0, 0), "relative": False},
    "arena_bay5": {"name": "Bay 5", "folder": "ArenaMapPort_Bay5", "title": "BAY 5",
                    "heights": (-4.5, -2.5), "anchor": (90, 30), "relative": True},
    "arena_airpit": {"name": "Air Pit", "folder": "ArenaMapPort_AirPit", "title": "AIR PIT",
                     "heights": (-.5, 1.2), "anchor": (0, 0), "relative": True,
                     "seam_grid": .01, "bounds": [-60, -86, 40, 73]},
}


def generate(package, descriptor, output, navmesh=None):
    package, descriptor, output = map(lambda p: Path(p).resolve(), (package, descriptor, output))
    if package == output or package in output.parents or output in package.parents:
        raise ValueError("Output must be separate from the source package")
    config = json.loads(descriptor.read_text(encoding="utf-8"))
    if config["key"] not in MAP_PROFILES:
        raise ValueError("Supported maps: arena_equator, arena_bay5, arena_airpit")
    profile = MAP_PROFILES[config["key"]]
    bounds_override = profile.get("bounds")
    if profile["relative"] and navmesh is None:
        raise ValueError(profile["folder"].removeprefix("ArenaMapPort_") + " requires native ground triangulation for height calibration")
    inputs = {str(descriptor): digest(descriptor)}
    opened = config.get("opened_barriers", [])
    floor_parts, low_parts, high_parts = [], [], []
    stats = Counter()
    walkable = read_triangulation(navmesh, profile["heights"]) if navmesh else None
    if walkable is not None and profile.get("seam_grid"):
        # Tile edges differ by a few float ULPs. Snap only AirPit before selecting
        # its connected footprint, otherwise the south apron is cut at z=-51.2.
        walkable = unary_union(set_precision(walkable, profile["seam_grid"]))
    connected, drawing_clip, surface = None, None, None
    if navmesh:
        inputs[str(Path(navmesh).resolve())] = digest(navmesh)
        components = [p for p in polygons(walkable) if p.covers(Point(*profile["anchor"]))]
        if len(components) != 1:
            raise ValueError("Expected one ground component at the map's reviewed anchor")
        connected = components[0]
        if profile["relative"]:
            xmin, zmin, xmax, zmax = connected.bounds
            drawing_clip = box(*(bounds_override or [math.floor(xmin)-3, math.floor(zmin)-3, math.ceil(xmax)+3, math.ceil(zmax)+3]))
            surface = GroundSurface(native_triangles(navmesh, profile["heights"]), drawing_clip)

    @lru_cache(maxsize=256)
    def mesh(mesh_id):
        if not mesh_id or any(c not in "0123456789abcdef" for c in mesh_id):
            raise ValueError("Invalid mesh ID")
        path = package / "meshes" / (mesh_id + ".mesh.bin")
        inputs[str(path)] = digest(path)
        return read_mesh(path)

    for scene_path in sorted((package / "scenes").glob("*.json")):
        if (scene_path.stem not in config["all_scene_names"]
                or scene_path.stem in config.get("background_scene_names", [])):
            continue
        inputs[str(scene_path)] = digest(scene_path)
        scene = Scene(json.loads(scene_path.read_text(encoding="utf-8")))
        for key, owner in scene.objects.items():
            if not scene.included(key, opened):
                continue
            for component in owner["components"]:
                kind = component["type"]
                if kind not in ("MeshCollider", "BoxCollider", "SphereCollider", "CapsuleCollider"):
                    continue
                if not component.get("enabled", True) or component.get("is_trigger", False):
                    continue
                verts, indices = mesh(component["mesh_id"]) if kind == "MeshCollider" else primitive(component)
                world = scene.world(key)
                verts = verts @ world[:3, :3].T + world[:3, 3]
                if not len(verts):
                    continue
                if surface is not None:
                    if (verts[:, 1].min() > profile["heights"][1]+1.8
                            or verts[:, 1].max() < profile["heights"][0]-.45):
                        continue
                    vmin, vmax = verts.min(axis=0), verts.max(axis=0)
                    if not box(vmin[0], vmin[2], vmax[0], vmax[2]).intersects(drawing_clip):
                        continue
                    verts = verts.copy()
                    verts[:, 1] -= surface.heights(verts[:, (0, 2)])
                if verts[:, 1].min() > 1.8 or verts[:, 1].max() < -.45:
                    continue
                stats[kind] += 1
                tris = verts[indices]
                # Near-horizontal ground faces only; walls cannot inflate the floor footprint.
                normal = np.cross(tris[:, 1]-tris[:, 0], tris[:, 2]-tris[:, 0])
                lengths = np.linalg.norm(normal, axis=1)
                ground = tris[(abs(normal[:, 1]) > lengths*.94) & (tris[:, :, 1].max(axis=1) <= .45)
                              & (tris[:, :, 1].min(axis=1) >= -.45)]
                if len(ground):
                    floor_parts.append(unary_union([Polygon(t[:, (0, 2)]) for t in ground]))
                low = project_slab(tris, .4, 1.05)
                high = project_slab(tris, 1.1, 1.8)
                if not low.is_empty:
                    low_parts.append(low)
                if not high.is_empty:
                    high_parts.append(high)
        print(f"Read {scene_path.name}: {sum(stats.values())} ground-height colliders", flush=True)
        scene.world.cache_clear()
        scene.lineage.cache_clear()

    floor = clean(unary_union(floor_parts), min_area=2)
    # Keep all substantial ground pieces, including spawn booths; remove tiny prop-top islands.
    if floor.is_empty:
        raise ValueError("No ground floor found")
    xmin, zmin, xmax, zmax = (connected if connected is not None else floor).bounds
    bounds = bounds_override or [math.floor(xmin)-3, math.floor(zmin)-3, math.ceil(xmax)+3, math.ceil(zmax)+3]
    if bounds[2]-bounds[0] > 180 or bounds[3]-bounds[1] > 180:
        raise ValueError(f"Unexpected {profile['name']} ground extent: {bounds}")
    drawing_clip = box(*bounds)
    detail_tolerance = .18 if profile["relative"] else .12
    low = unary_union(low_parts).intersection(drawing_clip).buffer(.07).buffer(-.07)
    high = unary_union(high_parts).intersection(drawing_clip).buffer(.07).buffer(-.07)
    if profile["relative"]:
        # A large flat face can span different nearest ground heights at its corners,
        # producing a false diagonal slab. This ground plan gives native floor area
        # priority over projected obstacles; genuine obstacle holes stay unfilled.
        # Overhangs above otherwise walkable ground are intentionally omitted.
        low, high = low.difference(walkable), high.difference(walkable)
    low = clean(low, tolerance=detail_tolerance)
    high = clean(high, tolerance=detail_tolerance)
    layers = [(floor.intersection(drawing_clip), "#46545e" if walkable is not None else "#a8b1b5")]
    if walkable is not None:
        # Avoid drawing the main polygon twice on new maps. Keep Equator's accepted
        # serialization unchanged, including its original overdraw ordering.
        other_ground = walkable.difference(connected) if profile["relative"] else walkable
        layers.append((other_ground.intersection(drawing_clip), "#66747d"))
        layers.append((connected, "#b8c1c4"))
    layers.extend([(low, "#8d8167"), (high, "#293641")])
    width, height = bounds[2]-bounds[0], bounds[3]-bounds[1]
    svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
           f'<rect x="0" y="0" width="{width}" height="{height}" fill="#151c24"/>']
    point_count = 0
    layer_points = []
    for geom, color in layers:
        path, count = svg_path(geom, bounds)
        point_count += count
        layer_points.append({"fill": color, "points": count})
        svg.append(f'<path fill="{color}" fill-rule="evenodd" d="{path}"/>')
    svg.append(NORTH_MARKER_SVG)
    svg.append("</svg>\n")
    if point_count > 16000:
        raise ValueError(f"Drawing too complex for the native tessellator: {point_count} points; {layer_points}")
    rect = {"Min": {"x": bounds[0], "y": bounds[1]}, "Max": {"x": bounds[2], "y": bounds[3]}}
    definition = {
        "DisplayName": f"Arena: {profile['name']} — ground plan", "Author": "ArenaMapPort",
        "MapInternalNames": [config["key"]], "CoordinateRotation": 0, "DefaultLevel": 0,
        "Bounds": rect,
        "Layers": {"Ground Floor": {"Level": 0, "ImagePath": f"Maps/{profile['folder']}/Layers/Ground.svg",
            "TesselationIndex": 1, "ImageBounds": rect,
            "GameBounds": [{"Min": {**rect["Min"], "z": -8}, "Max": {**rect["Max"], "z": 12}}]}},
        "Labels": [{"Text": f"{profile['title']} / GROUND", "Position": {"x": (bounds[0]+bounds[2])/2, "y": bounds[3]-2, "z": 0},
                    "FontSize": 16}], "StaticMarkers": [],
    }
    exits_path = descriptor.with_suffix(".exits.json")
    inputs[str(exits_path)] = digest(exits_path)
    for item in json.loads(exits_path.read_text(encoding="utf-8"))["exits"]:
        x, y, z = item["position"]
        definition["StaticMarkers"].append({"Text": item["name"], "ImagePath": "Markers/exit.png",
            "Position": {"x": x, "y": z, "z": y}, "Category": "Extract", "ShowInRaid": False,
            "Color": {"r": .3, "g": 1, "b": .5, "a": 1}})
    pack = output / "pack"
    (pack / "Layers").mkdir(parents=True, exist_ok=True)
    (pack / "Layers" / "Ground.svg").write_text("\n".join(svg), encoding="utf-8", newline="\n")
    (pack / (profile["folder"]+".jsonc")).write_text(json.dumps(definition, indent=2, ensure_ascii=False)+"\n", encoding="utf-8", newline="\n")
    render_preview(output / "preview.png", layers, bounds)
    if any(digest(path) != expected for path, expected in inputs.items()):
        raise ValueError("An input changed during export")
    receipt = {"schema": 1, "map": config["key"], "bounds_xz": bounds, "svg_points": point_count,
        "colliders": dict(stats), "areas_m2": {"floor": floor.area, "low": low.area, "high": high.area},
        "generator_sha256": digest(__file__), "inputs_sha256": dict(sorted(inputs.items())),
        "output_sha256": {p.relative_to(pack).as_posix(): digest(p) for p in sorted(pack.rglob("*")) if p.is_file()},
        "native_walkable_area_m2": walkable.area if walkable is not None else None,
        "atrium_component_area_m2": connected.area if connected is not None else None,
        "ground_profile": profile,
        "layer_points": layer_points,
        "obstacle_tolerance_m": detail_tolerance,
        "limitations": ["Ground floor only; higher surfaces and container tops omitted",
                        "Approximate slab projection at 0.4–1.8 m above the reference ground",
                        "Bay5 obstacle overlays omit overhangs above projected native ground",
                        "Round primitive colliders use conservative boxes", "Not a walkability or line-of-sight guarantee"]}
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2)+"\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in receipt.items() if k not in ("inputs_sha256",)}, indent=2))



if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--descriptor", type=Path, default=Path(__file__).resolve().parents[1]/"maps/equator.json")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--navmesh", type=Path, help="Native triangulation exported from the installed matching NavMesh bundle")
    args = parser.parse_args()
    generate(args.package, args.descriptor, args.output, args.navmesh)
