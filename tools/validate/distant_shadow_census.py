"""Offline census of sun-shadow casters that EFT's DistantShadow never draws, and of the sun rays only they block.

Why. Unity's sun cascades end at QualitySettings.shadowDistance; beyond it only EFT's `DistantShadow` keeps the
sun out, and it builds its draw list from LODGroups only (docs/progress/lighting-followup.md, 2026-11-12). A
shadow proxy outside every LODGroup is drawn by the cascades and missing from the distant map, so a building
whose roof is such a proxy shows a sun-lit patch that follows the view (Chop Shop). `RoofShadowRepairs` enrols
the descriptor's `DistantShadowCasterPaths`; this tool says which paths a map needs, before the first host raid.

What it reads. `<package>/scenes/*.json`, `<package>/meshes/<id>.mesh.bin` and `<package>/manifest.json`
(shader names, submesh counts). Nothing from the game install.

Two steps.
  1. Census. `DistantShadow.InvalidateRenderers` (0.16.9.5) is mirrored: per LODGroup the walk starts at the
     second-to-last LOD (index max(0, n - 2)) and goes down to the first LOD holding a renderer whose
     shadowCastingMode is not Off; of that LOD it draws the renderers that cast, have a material, are active in
     the hierarchy (the renderer's own `enabled` is not read), have a mesh and a world bounds edge of at least
     1 m, one record per submesh. Everything else that casts and is drawn by the cascades is 'unlisted': either
     a member of another LOD of some group, or referenced by no LODGroup at all (the candidates).
     Check: `draw_records` equals the host's `DistantShadow._renderList.Count` (Iceberg 1,571 on 2026-11-12).
  2. Coverage. Not every candidate matters: Iceberg's storey shells sit under a roof and facades that ARE in the
     list. For a sun direction everything is projected across the sun into depth buffers; a candidate texel is
     'uncovered' when it is the first blocker of its sun ray and the next listed caster lies at least MIN_GAP
     behind it (or nowhere). That is sun the distant map lets into the space behind the candidate. Faces count
     as the shaders cull them: `shadow` and ordinary materials by their front faces, `shadow_backside` by its
     back faces, an enrolled two-sided proxy (Equator) by both.
     Check: Chop Shop's outdoor hull is 96 % uncovered without its sidecar rows and listed with them.

Sun and cascade model (measured on GPU-2, 2026-11-12, TOD_Sky of the SPT client: latitude 46, date 1 August):
`sun_direction(hour)` reproduces three readings of `TOD_Sky.SunDirection` (07:00, 08:00, 15:30) within 0.004;
+x is east, +z south. `cascade_distance` is EnvironmentManager's rule: the shadow-quality distance (30, 40, 60 or
80 m) times the long-shadow factor, which falls to 1/3 for an outdoor player between 05-10 h and 17-23 h.
The cascades hand over to the distant map inside that nominal distance (Chop Shop: about 48 m at 33 degrees off
the view axis with a nominal 80 m).

Usage:
  python tools/validate/distant_shadow_census.py <package> [--map-descriptor tools/maps/<stem>.json] [--two-sided]
      [--hours 8,9,15.5] [--sun x,y,z ...] [--min-edge 3] [--texel 0.25] [--clusters] [--fail-above M2]
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from port_colliders import ColliderModelError, read_mesh_bin  # noqa: E402

OBJECT_SIZE_FILTER = 1.0     # DistantShadow.ObjectSizeFilter
MIN_GAP = 0.5                # metres behind a candidate before a listed caster counts as "the same surface"
BIG = 24                     # sub-samples per edge above which a triangle is rasterized over its clipped box
DEFAULT_HOURS = (7.5, 8.0, 9.0, 10.0, 12.0, 13.8, 15.5, 17.0, 18.0, 19.0, 20.0)

# TOD_Sky of the SPT client, fitted to GPU-2 readings (see the module text).
SUN_LATITUDE = math.radians(46.0)
SUN_DECLINATION = math.radians(18.0)
SOLAR_NOON = 13.81
SHADOW_INTERVALS = ((5.0, 10.0), (17.0, 23.0))   # EnvironmentManager.ShadowInterval1/2
SHADOW_DECREASE_FACTOR = 3.0
QUALITY_DISTANCES = (30.0, 40.0, 60.0, 80.0)     # GraphicsSettingsGroup.GetMaxShadowVisibility(0..3)


def sun_direction(hour: float) -> np.ndarray:
    """Unit vector toward the sun at a raid clock hour (+x east, +y up, +z south)."""
    angle = math.radians(15.0 * (hour - SOLAR_NOON))
    cd, sd, cl, sl = math.cos(SUN_DECLINATION), math.sin(SUN_DECLINATION), math.cos(SUN_LATITUDE), math.sin(SUN_LATITUDE)
    return np.array([-cd * math.sin(angle), sl * sd + cl * cd * math.cos(angle), sl * cd * math.cos(angle) - cl * sd])


def long_shadow_factor(hour: float) -> float:
    """EnvironmentManager.GetLongShadowCorrectionFactor for an outdoor player."""
    (a0, a1), (b0, b1) = SHADOW_INTERVALS
    low = 1.0 / SHADOW_DECREASE_FACTOR
    if a0 < hour < a1:
        return low + (1.0 - low) * (hour - a0) / (a1 - a0)
    if b0 < hour < b1:
        return low + (1.0 - low) * (1.0 - (hour - b0) / (b1 - b0))
    return 1.0


def cascade_distance(hour: float, quality_distance: float = 80.0, outdoor: bool = True) -> float:
    """Nominal QualitySettings.shadowDistance: an indoor player always gets the full quality distance."""
    return quality_distance * (long_shadow_factor(hour) if outdoor else 1.0)


# ---------------------------------------------------------------------------------------------
# hierarchy
# ---------------------------------------------------------------------------------------------

def _quat_matrix(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def component(obj, kind):
    for comp in obj["components"]:
        if comp["type"] == kind:
            return comp
    return None


class Scene:
    def __init__(self, path: Path):
        data = json.loads(Path(path).read_text(encoding="utf-8"))
        self.name = data["scene_name"]
        self.objects = {o["id"]: o for o in data["objects"]}
        self.children = {}
        self.roots = []
        for o in data["objects"]:
            parent = o.get("parent_id")
            if parent and parent in self.objects:
                self.children.setdefault(parent, []).append(o["id"])
            else:
                self.roots.append(o["id"])
        self._world, self._active, self._path = {}, {}, {}

    def _parent(self, oid):
        parent = self.objects[oid].get("parent_id")
        return parent if parent and parent in self.objects else None

    def world(self, oid):
        m = self._world.get(oid)
        if m is None:
            o = self.objects[oid]
            m = np.eye(4)
            m[:3, :3] = _quat_matrix(o["local_rotation"]) * np.asarray(o["local_scale"], dtype=float)
            m[:3, 3] = o["local_position"]
            parent = self._parent(oid)
            if parent:
                m = self.world(parent) @ m
            self._world[oid] = m
        return m

    def active(self, oid):
        a = self._active.get(oid)
        if a is None:
            parent = self._parent(oid)
            a = bool(self.objects[oid]["active"]) and (self.active(parent) if parent else True)
            self._active[oid] = a
        return a

    def path(self, oid):
        p = self._path.get(oid)
        if p is None:
            parent = self._parent(oid)
            p = (self.path(parent) + "/" if parent else "") + self.objects[oid]["name"]
            self._path[oid] = p
        return p

    def has_lod_ancestor(self, oid):
        cur = oid
        while cur:
            if component(self.objects[cur], "LODGroup"):
                return True
            cur = self._parent(cur)
        return False

    def find(self, path):
        """Transform.Find semantics of RoofShadowRepairs.Find: the first root of that name, then the first child per level."""
        names = path.split("/")
        cur = next((r for r in self.roots if self.objects[r]["name"] == names[0]), None)
        for name in names[1:]:
            if cur is None:
                return None
            cur = next((c for c in self.children.get(cur, []) if self.objects[c]["name"] == name), None)
        return cur


# ---------------------------------------------------------------------------------------------
# census
# ---------------------------------------------------------------------------------------------

@dataclass
class Census:
    package: Path
    scenes: dict
    native: list = field(default_factory=list)          # drawn by DistantShadow (or enrolled by the sidecar)
    unreferenced: list = field(default_factory=list)    # cascades only, referenced by no LODGroup: the candidates
    other_lod: list = field(default_factory=list)       # cascades only, member of a LOD the walk does not pick
    skipped_paths: list = field(default_factory=list)   # (enrolled path, reason) the client would not enrol
    draw_records: int = 0                               # compare with DistantShadow._renderList.Count
    lod_groups: int = 0


class _Meshes:
    def __init__(self, package: Path):
        self.package = package
        self.cache = {}
        manifest = json.loads((package / "manifest.json").read_text(encoding="utf-8"))
        self.materials = manifest.get("materials", {})
        self.meshes = manifest.get("meshes", {})

    def get(self, mesh_id):
        m = self.cache.get(mesh_id)
        if m is None:
            try:
                m = read_mesh_bin(self.package / "meshes" / f"{mesh_id}.mesh.bin")
            except (FileNotFoundError, ColliderModelError):
                m = (np.empty((0, 3)), np.empty((0, 3), dtype=np.int64))
            self.cache[mesh_id] = m
        return m

    def submeshes(self, mesh_id):
        return int(self.meshes.get(mesh_id, {}).get("submesh_count", 1))

    def shaders(self, material_ids):
        return {self.materials.get(m, {}).get("shader") for m in material_ids}


def _picked_lod(scene: Scene, lods):
    """Index of the LOD DistantShadow draws, or None: from the second-to-last down to the first one with a caster."""
    for index in range(max(0, len(lods) - 2), -1, -1):
        for rid in lods[index].get("renderer_object_ids", []):
            obj = scene.objects.get(rid)
            renderer = component(obj, "MeshRenderer") if obj else None
            if renderer is not None and renderer.get("cast_shadows") != "Off":
                return index
    return None


def _record(scene: Scene, meshes: _Meshes, oid):
    obj = scene.objects[oid]
    renderer, mesh_filter = component(obj, "MeshRenderer"), component(obj, "MeshFilter")
    if renderer is None or mesh_filter is None or not mesh_filter.get("mesh_id"):
        return None
    verts, tris = meshes.get(mesh_filter["mesh_id"])
    if len(verts) == 0 or len(tris) == 0:
        return None
    world = scene.world(oid)
    lo, hi = verts.min(axis=0), verts.max(axis=0)
    # Renderer.bounds is the world box of the transformed LOCAL box, not of the vertices.
    corners = np.array([[x, y, z] for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])])
    corners = corners @ world[:3, :3].T + world[:3, 3]
    materials = [m for m in renderer.get("material_ids", []) if m]
    return {
        "scene": scene.name, "id": oid, "name": obj["name"], "path": scene.path(oid),
        "cast": renderer.get("cast_shadows"), "enabled": bool(renderer.get("enabled", True)),
        "mesh_id": mesh_filter["mesh_id"], "materials": materials,
        "lo": corners.min(axis=0), "hi": corners.max(axis=0), "size": corners.max(axis=0) - corners.min(axis=0),
        "tris": int(len(tris)), "sides": "back" if meshes.shaders(materials) == {"shadow_backside"} else "front",
    }


def census(package, enrolled_paths=(), two_sided=False) -> Census:
    """Mirror DistantShadow's draw list for a package; `enrolled_paths` are a sidecar's distant_shadow_caster_paths."""
    package = Path(package)
    meshes = _Meshes(package)
    result = Census(package, {s.name: s for s in (Scene(p) for p in sorted((package / "scenes").glob("*.json")))})
    result._meshes = meshes
    enrolled, resolved = {}, set()
    for path in enrolled_paths:
        for scene in result.scenes.values():
            oid = scene.find(path)
            if oid is None:
                continue
            resolved.add(path)
            obj = scene.objects[oid]
            renderer, mesh_filter = component(obj, "MeshRenderer"), component(obj, "MeshFilter")
            # RoofShadowRepairs.Apply: active, enabled ShadowsOnly renderer with a mesh, under no LODGroup.
            if not scene.active(oid) or renderer is None or not renderer.get("enabled", True) \
                    or renderer.get("cast_shadows") != "ShadowsOnly" or mesh_filter is None or not mesh_filter.get("mesh_id"):
                result.skipped_paths.append((path, "not an active, enabled ShadowsOnly mesh renderer"))
            elif scene.has_lod_ancestor(oid):
                result.skipped_paths.append((path, "already under a LODGroup"))
            else:
                enrolled[(scene.name, oid)] = path
            break
    result.skipped_paths.extend((p, "not found in any scene") for p in enrolled_paths if p not in resolved)

    for scene in result.scenes.values():
        picked, referenced = set(), {}
        for oid, obj in scene.objects.items():
            group = component(obj, "LODGroup")
            if group is None:
                continue
            result.lod_groups += 1
            lods = group.get("lods", [])
            pick = _picked_lod(scene, lods)
            for index, lod in enumerate(lods):
                for rid in lod.get("renderer_object_ids", []):
                    referenced.setdefault(rid, []).append({"group": scene.path(oid), "lod": index, "lods": len(lods), "picked": pick})
                    if index == pick:
                        picked.add(rid)
        for oid, obj in scene.objects.items():
            renderer = component(obj, "MeshRenderer")
            if renderer is None or renderer.get("cast_shadows") == "Off" or not scene.active(oid):
                continue
            rec = _record(scene, meshes, oid)
            if rec is None or float(rec["size"].max()) < OBJECT_SIZE_FILTER:
                continue
            rec["lod_refs"] = referenced.get(oid, [])
            if oid in picked:
                if rec["materials"]:                      # DistantShadow needs a sharedMaterial
                    result.native.append(rec)
                    result.draw_records += meshes.submeshes(rec["mesh_id"])
            elif (scene.name, oid) in enrolled:
                rec["enrolled"] = True
                if two_sided:
                    rec["sides"] = "both"
                result.native.append(rec)
                result.draw_records += meshes.submeshes(rec["mesh_id"])
            elif not rec["enabled"]:
                continue                                  # the cascades do not draw it either
            elif rec["lod_refs"]:
                result.other_lod.append(rec)
            else:
                result.unreferenced.append(rec)
    result.unreferenced.sort(key=lambda r: -float(r["size"].max()))
    return result


# ---------------------------------------------------------------------------------------------
# coverage
# ---------------------------------------------------------------------------------------------

def _world_triangles(result: Census, rec):
    scene = result.scenes[rec["scene"]]
    verts, tris = result._meshes.get(rec["mesh_id"])
    m = scene.world(rec["id"])
    lin = m[:3, :3]
    world = verts @ lin.T + m[:3, 3]
    a, b, c = verts[tris[:, 0]], verts[tris[:, 1]], verts[tris[:, 2]]
    # Unity's front side is cross(b - a, c - a); the normal matrix keeps it under mirrored scales.
    normals = np.cross(b - a, c - a) @ np.linalg.inv(lin)
    return world[tris], normals


def _basis(sun):
    sun = np.asarray(sun, dtype=float)
    sun = sun / np.linalg.norm(sun)
    up = np.array([0.0, 1.0, 0.0]) if abs(sun[1]) < 0.95 else np.array([1.0, 0.0, 0.0])
    u = np.cross(up, sun)
    u /= np.linalg.norm(u)
    return u, np.cross(sun, u), sun


def _raster_big(tris, lo, shape, texel, buf):
    """Texel-centre rasterization of large triangles, clipped to the buffer and inflated by half a texel."""
    for a, b, c in tris:
        mn, mx = np.minimum(np.minimum(a, b), c)[:2], np.maximum(np.maximum(a, b), c)[:2]
        x0 = max(0, int(math.floor((mn[0] - lo[0]) / texel)) - 1)
        y0 = max(0, int(math.floor((mn[1] - lo[1]) / texel)) - 1)
        x1 = min(shape[0], int(math.ceil((mx[0] - lo[0]) / texel)) + 1)
        y1 = min(shape[1], int(math.ceil((mx[1] - lo[1]) / texel)) + 1)
        if x1 <= x0 or y1 <= y0:
            continue
        e0, e1 = (b - a)[:2], (c - a)[:2]
        det = e0[0] * e1[1] - e0[1] * e1[0]
        if abs(det) < 1e-12:
            continue
        gx, gy = np.meshgrid(lo[0] + (np.arange(x0, x1) + 0.5) * texel, lo[1] + (np.arange(y0, y1) + 0.5) * texel, indexing="ij")
        dx, dy = gx - a[0], gy - a[1]
        s = (dx * e1[1] - dy * e1[0]) / det
        t = (dy * e0[0] - dx * e0[1]) / det
        tol = 0.5 * texel / abs(det)
        inside = (s >= -tol * np.linalg.norm(e1)) & (t >= -tol * np.linalg.norm(e0)) & (s + t <= 1 + tol * np.linalg.norm((c - b)[:2]))
        if inside.any():
            view = buf[x0:x1, y0:y1]
            np.maximum(view, np.where(inside, a[2] + s * (b[2] - a[2]) + t * (c[2] - a[2]), -np.inf), out=view)


def _splat(tris, lo, shape, texel, buf):
    """Max-depth buffer of (T, 3, 3) [u, v, depth] triangles: small ones as point splats, large ones rasterized."""
    if len(tris) == 0:
        return
    a, b, c = tris[:, 0], tris[:, 1], tris[:, 2]
    edge = np.maximum.reduce([np.linalg.norm((b - a)[:, :2], axis=1), np.linalg.norm((c - a)[:, :2], axis=1),
                              np.linalg.norm((c - b)[:, :2], axis=1)])
    n = np.ceil(edge / (texel / 3.0)).astype(int)
    big = n > BIG
    _raster_big(tris[big], lo, shape, texel, buf)
    tris, n = tris[~big], np.clip(n[~big], 1, BIG)
    if len(tris) == 0:
        return
    a, b, c = tris[:, 0], tris[:, 1], tris[:, 2]
    level = np.ceil(np.log2(n)).astype(int)
    flat_buf = buf.reshape(-1)
    for lv in np.unique(level):
        idx = np.nonzero(level == lv)[0]
        k = 1 << int(lv)
        ii, jj = np.meshgrid(np.arange(k + 1), np.arange(k + 1), indexing="ij")
        keep = ii + jj <= k
        s, t = (ii[keep] / k)[None, :, None], (jj[keep] / k)[None, :, None]
        chunk = max(1, 2_000_000 // s.shape[1])
        for at in range(0, len(idx), chunk):
            part = idx[at:at + chunk]
            p = (a[part, None, :] + s * (b[part] - a[part])[:, None, :] + t * (c[part] - a[part])[:, None, :]).reshape(-1, 3)
            x = np.floor((p[:, 0] - lo[0]) / texel).astype(np.int64)
            y = np.floor((p[:, 1] - lo[1]) / texel).astype(np.int64)
            ok = (x >= 0) & (x < shape[0]) & (y >= 0) & (y < shape[1])
            np.maximum.at(flat_buf, x[ok] * shape[1] + y[ok], p[ok, 2])


def _label(mask):
    """4-connected components of a small boolean grid."""
    lab = np.zeros(mask.shape, dtype=np.int32)
    count = 0
    for x, y in zip(*np.nonzero(mask)):
        if lab[x, y]:
            continue
        count += 1
        stack = [(x, y)]
        lab[x, y] = count
        while stack:
            i, j = stack.pop()
            for p, q in ((i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1)):
                if 0 <= p < mask.shape[0] and 0 <= q < mask.shape[1] and mask[p, q] and not lab[p, q]:
                    lab[p, q] = count
                    stack.append((p, q))
    return lab, count


def _facing(normals, sun, sides):
    dot = normals @ sun
    if sides == "both":
        return np.abs(dot) > 1e-9
    return dot < -1e-9 if sides == "back" else dot > 1e-9


class Coverage:
    """Depth buffers of the listed casters for a set of sun directions, queried per candidate."""

    def __init__(self, result: Census, min_edge=3.0, texel=0.25):
        self.result, self.texel = result, texel
        self.candidates = [r for r in result.unreferenced if float(r["size"].max()) >= min_edge]
        self._cand_geo = [_world_triangles(result, r) for r in self.candidates]
        if self.candidates:
            lo = np.min([r["lo"] for r in self.candidates], axis=0) - 1.0
            hi = np.max([r["hi"] for r in self.candidates], axis=0) + 1.0
            self._corners = np.array([[x, y, z] for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])])
        geo = [(_world_triangles(result, r), r["sides"]) for r in result.native]
        self._native = [(t, n, sides) for (t, n), sides in geo]

    def at(self, sun, clusters=False):
        """Rows {record, facing_m2, uncovered_m2[, clusters]} for one sun direction; areas are measured across the sun."""
        rows = []
        if not self.candidates:
            return rows
        u, v, sun = _basis(sun)
        proj = self._corners @ np.stack([u, v], axis=1)
        lo = proj.min(axis=0)
        shape = tuple(np.ceil((proj.max(axis=0) - lo) / self.texel).astype(int) + 1)
        hi = lo + np.array(shape) * self.texel
        axes = np.stack([u, v, sun], axis=1)
        native = np.full(shape, -np.inf)
        for tris, normals, sides in self._native:
            p = tris @ axes
            tmin, tmax = p[:, :, :2].min(axis=1), p[:, :, :2].max(axis=1)
            sel = (tmax[:, 0] >= lo[0]) & (tmin[:, 0] <= hi[0]) & (tmax[:, 1] >= lo[1]) & (tmin[:, 1] <= hi[1]) \
                & _facing(normals, sun, sides)
            _splat(p[sel], lo, shape, self.texel, native)
        cell = self.texel * self.texel
        for rec, (tris, normals) in zip(self.candidates, self._cand_geo):
            buf = np.full(shape, -np.inf)
            _splat((tris @ axes)[_facing(normals, sun, rec["sides"])], lo, shape, self.texel, buf)
            has = np.isfinite(buf)
            with np.errstate(invalid="ignore"):
                uncovered = has & (buf - native > MIN_GAP)
            row = {"record": rec, "facing_m2": float(has.sum() * cell), "uncovered_m2": float(uncovered.sum() * cell)}
            if clusters and uncovered.any():
                row["clusters"] = []
                lab, count = _label(uncovered)
                for i in range(1, count + 1):
                    m = lab == i
                    xs, ys = np.nonzero(m)
                    pu, pv = lo[0] + (xs + 0.5) * self.texel, lo[1] + (ys + 0.5) * self.texel
                    entry = pu[:, None] * u + pv[:, None] * v + buf[m][:, None] * sun
                    behind = native[m]
                    fin = np.isfinite(behind)
                    cluster = {"area_m2": float(m.sum() * cell), "entry_lo": entry.min(axis=0), "entry_hi": entry.max(axis=0),
                               "open_share": float((~fin).mean())}
                    if fin.any():
                        land = pu[fin, None] * u + pv[fin, None] * v + behind[fin, None] * sun
                        cluster.update(land_lo=land.min(axis=0), land_hi=land.max(axis=0),
                                       gap_median=float(np.median(buf[m][fin] - behind[fin])))
                    row["clusters"].append(cluster)
                row["clusters"].sort(key=lambda c: -c["area_m2"])
            rows.append(row)
        return rows


# ---------------------------------------------------------------------------------------------
# report
# ---------------------------------------------------------------------------------------------

def _fmt_size(rec):
    sx, sy, sz = rec["size"]
    cx, cy, cz = (rec["lo"] + rec["hi"]) / 2
    return f"{sx:6.1f} x {sy:5.1f} x {sz:6.1f} m at ({cx:7.1f}, {cy:6.1f}, {cz:7.1f})"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("package", type=Path)
    ap.add_argument("--map-descriptor", type=Path, help="tools/maps/<stem>.json; its distant_shadow_caster_paths count as listed")
    ap.add_argument("--two-sided", action="store_true", help="the enrolled paths block with both face sides (DistantShadowTwoSided)")
    ap.add_argument("--hours", default=",".join(str(h) for h in DEFAULT_HOURS), help="raid clock hours to test")
    ap.add_argument("--sun", action="append", default=[], help="x,y,z toward the sun; replaces --hours")
    ap.add_argument("--min-edge", type=float, default=3.0, help="smallest candidate bounds edge for the coverage test")
    ap.add_argument("--texel", type=float, default=0.25)
    ap.add_argument("--clusters", action="store_true", help="print where the uncovered rays enter and land")
    ap.add_argument("--fail-above", type=float, help="exit 1 when a candidate has more uncovered area than this at a tested sun")
    args = ap.parse_args(argv)

    enrolled = []
    if args.map_descriptor:
        enrolled = json.loads(args.map_descriptor.read_text(encoding="utf-8")).get("distant_shadow_caster_paths", [])
    result = census(args.package, enrolled, args.two_sided)
    print(f"package {args.package}: {len(result.scenes)} scenes, {result.lod_groups} LODGroups")
    print(f"DistantShadow list: {len(result.native)} renderers, {result.draw_records} draw records "
          f"({sum(1 for r in result.native if r.get('enrolled'))} enrolled by the sidecar)")
    for path, reason in result.skipped_paths:
        print(f"  sidecar path NOT enrolled ({reason}): {path}")
    print(f"cascades only: {len(result.other_lod)} in another LOD of a group, {len(result.unreferenced)} referenced by no LODGroup")
    print(f"\nreferenced by no LODGroup, bounds edge >= {args.min_edge} m:")
    for rec in result.unreferenced:
        if float(rec["size"].max()) >= args.min_edge:
            print(f"  {rec['cast']:11s} {_fmt_size(rec)} {rec['tris']:5d} tris  [{rec['scene']}] {rec['path']}")

    if args.sun:
        suns = [(f"sun {s}", np.array([float(x) for x in s.split(",")]), None) for s in args.sun]
    else:
        suns = [(f"{h:5.2f} h", sun_direction(h), h) for h in (float(x) for x in args.hours.split(",") if x)]
    cover = Coverage(result, args.min_edge, args.texel)
    worst = 0.0
    print(f"\nuncovered sun per candidate (m2 across the sun, candidates with at least 1 m2; gap >= {MIN_GAP} m):")
    for label, sun, hour in suns:
        if sun[1] <= 0:
            print(f"{label}: the sun is below the horizon")
            continue
        rows = [r for r in cover.at(sun, args.clusters) if r["uncovered_m2"] >= 1.0]
        head = f"{label}: sun ({sun[0]:.3f}, {sun[1]:.3f}, {sun[2]:.3f}), elevation {math.degrees(math.asin(sun[1] / np.linalg.norm(sun))):.1f} deg"
        if hour is not None:
            head += ", outdoor cascade " + " / ".join(f"{cascade_distance(hour, q):.0f}" for q in QUALITY_DISTANCES) + " m by shadow quality"
        print(head + ("" if rows else ": nothing uncovered"))
        for row in sorted(rows, key=lambda r: -r["uncovered_m2"]):
            worst = max(worst, row["uncovered_m2"])
            print(f"    {row['uncovered_m2']:7.1f} of {row['facing_m2']:7.1f} m2  {row['record']['name']}")
            for c in row.get("clusters", [])[:8]:
                if c["area_m2"] < 1.0:
                    continue
                text = (f"        {c['area_m2']:6.1f} m2 enters x {c['entry_lo'][0]:.1f}..{c['entry_hi'][0]:.1f} y {c['entry_lo'][1]:.1f}..{c['entry_hi'][1]:.1f} "
                        f"z {c['entry_lo'][2]:.1f}..{c['entry_hi'][2]:.1f}")
                if "land_lo" in c:
                    text += (f", meets the next listed caster at x {c['land_lo'][0]:.1f}..{c['land_hi'][0]:.1f} y {c['land_lo'][1]:.1f}..{c['land_hi'][1]:.1f} "
                             f"z {c['land_lo'][2]:.1f}..{c['land_hi'][2]:.1f} ({c['gap_median']:.1f} m on, median)")
                if c["open_share"] > 0:
                    text += f", {c['open_share'] * 100:.0f}% meet none"
                print(text)
    if args.fail_above is not None and worst > args.fail_above:
        print(f"\nFAIL: {worst:.1f} m2 uncovered exceeds --fail-above {args.fail_above}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
