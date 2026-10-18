"""Read Arena's own source NavMesh (Detour tile blobs) and answer connectivity questions on it.

Why. Arena ships a baked NavMeshData for every map (AirPit: `sharedassets33.assets`, path 2, the
`Arena_Airpit_TeamFight_AI` scene). The port re-bakes a fresh NavMesh from the colliders it imports
(decision #6, `BakeNavMesh.cs`), so Arena's mesh is the only independent statement of where its own
agents walk. docs/PLAN-AIRPIT.md item 7 uses it as an oracle: which part of the map is one connected
walkable component, which spawn / exit / capture / AI-core points lie in it, and (with
`navmesh_diff.py`) which solids of the port stand inside that walkable space.

Format (measured on AirPit 0.6.0.47752, Unity 6000.3.6f1; every number below was checked against all 168
tiles, 68 of them non-empty, 3,853 polygons). `NavMeshData.m_NavMeshTiles[i].m_MeshData` is one Detour
tile:

    +0   int32 magic 0x444E4156 (the C literal 'DNAV')   +4  int32 version (16)
    +8   int32 x, +12 int32 y (tile grid, 17.92 m = 256 * 0.07 m cells)   +16 int32 agentTypeID
    +20  int32 polyCount, +24 vertCount, +28 detailMeshCount, +32 detailVertCount, +36 detailTriCount,
    +40  int32 bvNodeCount
    +44  float3 bmin, +56 float3 bmax, +68 float bvQuantFactor           (header = 72 bytes)
    +72  vertCount * float3 vertices (world space)
    then polyCount * 32-byte polygons:
         uint16 verts[6], uint16 neis[6], uint32 flags, uint8 vertCount, uint8 area, 2 pad bytes
         neis[k]: 0 = border (no neighbour); 0x8000 | side = portal to the neighbouring tile;
                  else 1-based index of the neighbouring polygon of the same tile (edge k joins
                  verts[k] and verts[(k+1) % vertCount]).
    detail meshes, detail vertices, detail triangles and the BV tree follow and are not needed here.

An "empty" tile is exactly the 72-byte header with polyCount 0. Cross-tile connectivity is rebuilt
geometrically (portal edges that lie on the shared tile border and overlap along it) instead of from
the side codes, so a different side numbering in another Unity version cannot silently break it.

Placement (PLAN-BOWL item 6, measured 2026-10-18 on AirPit and Bowl, Arena 0.6.0.47752). The tile vertices
are NOT world coordinates: they live in the frame of the NavMeshSurface that baked them, and
`NavMeshData.m_Position` / `m_Rotation` (the surface GameObject's world transform, the values Unity passes
to `NavMesh.AddNavMeshData(data, position, rotation)`) place that frame in the world. AirPit's is the
identity (level33, Transform 985 at the origin), which is why the first oracle could ignore it. Bowl's
(level128, the root `NavMesh` Transform 340, equal to its NavMeshData) is a rotation of 90 degrees about Y
(quaternion y 0.7071058 / w 0.7071078): the raw tiles span x -8.8..79.5 and z -63.8..59.6, the world mesh
x -63.8..59.6 and z -79.5..8.8, i.e. world (x, z) = (local z, -local x). Reading the tiles without the
placement puts Bowl's mesh 90 degrees off its spawns, core points and colliders and every oracle answer
(reachability, clearance, the diff against the port's colliders) is wrong. `Tile` therefore carries the
`Placement` of the NavMeshData it came from; `build_navmesh` builds the connectivity in the tile frame (the
tile grid is axis aligned only there, `_match_portals` needs that) and then maps vertices, tile boxes and
the lazy fan triangles into the world, so every `NavMesh` query is in world coordinates. Only upright
placements (a yaw about Y plus any translation) are supported: `locate`, `clearance` and the lattice
sampler treat y as up. A tilted frame raises `NavMeshFormatError` instead of answering wrongly.

Everything is numpy plus a tiny union-find (scipy is not installed in this repository's toolchain).
The module has no dependency on UnityPy except in `load_navmesh_tiles`, which imports it lazily.
"""
from __future__ import annotations

import dataclasses
import math
import struct
from dataclasses import dataclass, field

import numpy as np

MAGIC = 0x444E4156
SUPPORTED_VERSIONS = (16,)
HEADER_SIZE = 72
POLY_SIZE = 32
MAX_POLY_VERTS = 6
EXTERNAL_LINK = 0x8000


class NavMeshFormatError(ValueError):
    """The blob is not a Detour tile this reader understands (never guess a layout)."""


@dataclass(frozen=True)
class Placement:
    """Where a NavMeshData's tile frame sits in the world: `m_Position` and `m_Rotation` (x, y, z, w).

    world = R(rotation) * local + position, Unity's convention (a +90 degree yaw sends local +x to world -z
    and local +z to world +x). The default is the identity, which is exact for AirPit and for every bundle
    the port bakes (`BuildNavMeshData(..., Vector3.zero, Quaternion.identity)`).
    """
    position: tuple = (0.0, 0.0, 0.0)
    rotation: tuple = (0.0, 0.0, 0.0, 1.0)

    def __post_init__(self):
        """Plain float tuples of the right length, all finite: a NaN position is neither the identity nor refused by
        `matrix()`, so `apply` would silently move the whole mesh to NaN."""
        try:
            position = tuple(float(c) for c in self.position)
            rotation = tuple(float(c) for c in self.rotation)
        except (TypeError, ValueError) as exc:
            raise NavMeshFormatError(f"NavMeshData placement is not numeric ({exc!r})") from exc
        if len(position) != 3 or len(rotation) != 4 or not all(math.isfinite(c) for c in position + rotation):
            raise NavMeshFormatError(f"NavMeshData placement needs a finite position (3) and rotation (4), got {position} / {rotation}")
        object.__setattr__(self, "position", position)
        object.__setattr__(self, "rotation", rotation)

    @property
    def is_identity(self) -> bool:
        """Exactly the identity (a quaternion of -0 / +0 xyz and w of either sign): `apply` is then a no-op,
        which keeps the identity case (AirPit, every port bundle) bit-identical to the pre-placement oracle."""
        x, y, z, w = self.rotation
        return x == 0.0 and y == 0.0 and z == 0.0 and abs(w) == 1.0 and all(c == 0.0 for c in self.position)

    def matrix(self) -> np.ndarray:
        """3x3 rotation matrix of the (re-normalised) quaternion; a clearly non-unit quaternion is refused."""
        x, y, z, w = (float(c) for c in self.rotation)
        norm = math.sqrt(x * x + y * y + z * z + w * w)
        if not (abs(norm - 1.0) <= 1e-3):
            raise NavMeshFormatError(f"NavMeshData rotation {self.rotation} is not a unit quaternion (norm {norm})")
        x, y, z, w = x / norm, y / norm, z / norm, w / norm
        return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                         [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])

    def require_upright(self) -> None:
        """The oracle's queries treat y as up; refuse a frame that tilts it instead of answering wrongly."""
        if self.is_identity:
            return
        up = self.matrix()[:, 1]
        if np.abs(up - np.array([0.0, 1.0, 0.0])).max() > 1e-4:
            raise NavMeshFormatError(f"NavMeshData rotation {self.rotation} tilts the up axis (local up maps to "
                                     f"{[round(float(c), 4) for c in up]}); only yaw placements are supported")

    def apply(self, points) -> np.ndarray:
        """(N, 3) local points to world points (float64; the identity returns the input values unchanged)."""
        pts = np.asarray(points, dtype=np.float64).reshape(-1, 3)
        if self.is_identity:
            return pts
        return pts @ self.matrix().T + np.asarray(self.position, dtype=np.float64)

    def yaw_degrees(self) -> float:
        """Rotation about Y in degrees as Unity's `eulerAngles.y` reports it (0..360; meaningful for upright frames)."""
        m = self.matrix()
        return float(math.degrees(math.atan2(m[0, 2], m[0, 0])) % 360.0)


@dataclass
class Tile:
    x: int
    y: int
    agent_type: int
    bmin: tuple
    bmax: tuple
    verts: np.ndarray            # (V, 3) float64
    poly_verts: np.ndarray       # (P, 6) int32, tile-local vertex indices, -1 padded
    poly_neis: np.ndarray        # (P, 6) uint16, raw neighbour codes
    poly_nverts: np.ndarray      # (P,) uint8
    poly_area: np.ndarray        # (P,) uint8
    poly_flags: np.ndarray       # (P,) uint32
    placement: Placement = Placement()   # of the NavMeshData the tile came from; verts/bmin/bmax stay in the tile frame


def parse_tile(blob: bytes) -> Tile | None:
    """Parse one `m_MeshData` blob; returns None for an empty (header-only) tile."""
    if len(blob) < HEADER_SIZE:
        raise NavMeshFormatError(f"tile blob of {len(blob)} bytes is shorter than the {HEADER_SIZE}-byte header")
    magic, version, x, y, agent = struct.unpack_from("<5i", blob, 0)
    if magic != MAGIC:
        raise NavMeshFormatError(f"bad tile magic 0x{magic & 0xffffffff:08x}")
    if version not in SUPPORTED_VERSIONS:
        raise NavMeshFormatError(f"unsupported tile version {version} (measured on {SUPPORTED_VERSIONS})")
    poly_count, vert_count = struct.unpack_from("<2i", blob, 20)
    bmin = struct.unpack_from("<3f", blob, 44)
    bmax = struct.unpack_from("<3f", blob, 56)
    if poly_count < 0 or vert_count < 0:
        raise NavMeshFormatError("negative polygon/vertex count")
    if poly_count == 0:
        return None
    need = HEADER_SIZE + vert_count * 12 + poly_count * POLY_SIZE
    if len(blob) < need:
        raise NavMeshFormatError(f"tile ({x},{y}) is {len(blob)} bytes, needs at least {need}")
    verts = np.frombuffer(blob, "<f4", vert_count * 3, HEADER_SIZE).reshape(-1, 3).astype(np.float64)
    if not np.isfinite(verts).all():
        raise NavMeshFormatError(f"tile ({x},{y}) has non-finite vertices")
    raw = np.frombuffer(blob, np.uint8, poly_count * POLY_SIZE, HEADER_SIZE + vert_count * 12).reshape(poly_count, POLY_SIZE)
    pv = raw[:, 0:12].copy().view("<u2").reshape(poly_count, 6).astype(np.int32)
    neis = raw[:, 12:24].copy().view("<u2").reshape(poly_count, 6)
    flags = raw[:, 24:28].copy().view("<u4").reshape(poly_count)
    nverts = raw[:, 28].copy()
    area = raw[:, 29].copy()
    if (nverts < 3).any() or (nverts > MAX_POLY_VERTS).any():
        raise NavMeshFormatError(f"tile ({x},{y}) has a polygon with {int(nverts.min())}..{int(nverts.max())} vertices")
    slots = np.arange(MAX_POLY_VERTS)[None, :] < nverts[:, None]
    if (pv[slots] >= vert_count).any():
        raise NavMeshFormatError(f"tile ({x},{y}) references a vertex outside its vertex array")
    pv[~slots] = -1
    local = (neis & EXTERNAL_LINK) == 0
    internal = neis[slots & local & (neis != 0)]
    if internal.size and int(internal.max()) > poly_count:
        raise NavMeshFormatError(f"tile ({x},{y}) neighbour index beyond its polygon count")
    return Tile(x, y, agent, bmin, bmax, verts, pv, neis, nverts, area, flags)


@dataclass
class NavMesh:
    """All tiles merged into global arrays plus the polygon adjacency graph."""
    verts: np.ndarray                      # (V, 3) world coordinates (NavMeshData placement applied)
    poly_verts: np.ndarray                 # (P, 6) global vertex indices, -1 padded
    poly_nverts: np.ndarray                # (P,)
    poly_area: np.ndarray                  # (P,)
    poly_tile: np.ndarray                  # (P,) index into `tiles`
    poly_nei: np.ndarray                   # (P, 6) global polygon index, -1 border, -2 portal (see `portal_edges`)
    tiles: list = field(default_factory=list)   # the tiles in world space (identity placement); bmin / bmax: box of the corners
    edges: np.ndarray = None               # (E, 2) polygon adjacency pairs (internal + portal matches)
    portal_pairs: int = 0
    unmatched_portal_edges: int = 0
    labels: np.ndarray = None              # (P,) component label, 0..C-1, largest area first
    placement: Placement = Placement()     # applied to verts / tiles by build_navmesh (identity: nothing moved)
    # derived lazily
    _tris: tuple | None = None

    @property
    def poly_count(self) -> int:
        return len(self.poly_verts)

    # ------------------------------------------------------------------ geometry helpers
    def fan_triangles(self):
        """(tri_verts (T,3,3), tri_poly (T,)) fan triangulation of every convex polygon."""
        if self._tris is None:
            tv, tp = [], []
            for k in range(1, MAX_POLY_VERTS - 1):
                sel = self.poly_nverts > k + 1
                idx = np.nonzero(sel)[0]
                if not len(idx):
                    continue
                a = self.verts[self.poly_verts[idx, 0]]
                b = self.verts[self.poly_verts[idx, k]]
                c = self.verts[self.poly_verts[idx, k + 1]]
                tv.append(np.stack([a, b, c], axis=1))
                tp.append(idx)
            self._tris = (np.concatenate(tv), np.concatenate(tp))
        return self._tris

    def poly_areas(self):
        """(area_xz, area_3d) per polygon in square metres."""
        tv, tp = self.fan_triangles()
        e1, e2 = tv[:, 1] - tv[:, 0], tv[:, 2] - tv[:, 0]
        a3 = 0.5 * np.linalg.norm(np.cross(e1, e2), axis=1)
        axz = 0.5 * np.abs(e1[:, 0] * e2[:, 2] - e1[:, 2] * e2[:, 0])
        out_xz = np.zeros(self.poly_count)
        out_3d = np.zeros(self.poly_count)
        np.add.at(out_xz, tp, axz)
        np.add.at(out_3d, tp, a3)
        return out_xz, out_3d

    def border_edges(self):
        """(E, 2, 3) world segments of polygon edges without any neighbour (the walkable boundary)."""
        segs = []
        for k in range(MAX_POLY_VERTS):
            sel = np.nonzero((k < self.poly_nverts) & (self.poly_nei[:, k] == -1))[0]
            if not len(sel):
                continue
            a = self.verts[self.poly_verts[sel, k]]
            nxt = (k + 1) % self.poly_nverts[sel]
            b = self.verts[self.poly_verts[sel, nxt]]
            segs.append(np.stack([a, b], axis=1))
        return np.concatenate(segs) if segs else np.empty((0, 2, 3))

    # ------------------------------------------------------------------ point queries
    def locate(self, points, max_dy: float = 1.5):
        """For each (x, y, z): the polygon whose xz footprint contains it and whose surface height at
        that spot is closest to y (within `max_dy`). Returns (poly (N,), surface_y (N,)); poly is -1
        when the point is outside every polygon. Fan triangles are tested with barycentric coordinates."""
        pts = np.atleast_2d(np.asarray(points, dtype=np.float64))
        tv, tp = self.fan_triangles()
        poly = np.full(len(pts), -1, dtype=np.int64)
        surf = np.full(len(pts), np.nan)
        best = np.full(len(pts), np.inf)
        ax, az = tv[:, 0, 0], tv[:, 0, 2]
        v0x, v0z = tv[:, 1, 0] - ax, tv[:, 1, 2] - az
        v1x, v1z = tv[:, 2, 0] - ax, tv[:, 2, 2] - az
        den = v0x * v1z - v1x * v0z
        ok = np.abs(den) > 1e-12
        for i, (px, py, pz) in enumerate(pts):
            near = ok & (np.minimum(np.minimum(tv[:, 0, 0], tv[:, 1, 0]), tv[:, 2, 0]) - 1e-6 <= px) \
                & (np.maximum(np.maximum(tv[:, 0, 0], tv[:, 1, 0]), tv[:, 2, 0]) + 1e-6 >= px) \
                & (np.minimum(np.minimum(tv[:, 0, 2], tv[:, 1, 2]), tv[:, 2, 2]) - 1e-6 <= pz) \
                & (np.maximum(np.maximum(tv[:, 0, 2], tv[:, 1, 2]), tv[:, 2, 2]) + 1e-6 >= pz)
            idx = np.nonzero(near)[0]
            if not len(idx):
                continue
            dx, dz = px - ax[idx], pz - az[idx]
            u = (dx * v1z[idx] - v1x[idx] * dz) / den[idx]
            w = (v0x[idx] * dz - dx * v0z[idx]) / den[idx]
            inside = (u >= -1e-6) & (w >= -1e-6) & (u + w <= 1 + 1e-6)
            hit = idx[inside]
            if not len(hit):
                continue
            uu, ww = u[inside], w[inside]
            sy = tv[hit, 0, 1] + uu * (tv[hit, 1, 1] - tv[hit, 0, 1]) + ww * (tv[hit, 2, 1] - tv[hit, 0, 1])
            dy = np.abs(sy - py)
            k = int(np.argmin(dy))
            if dy[k] <= max_dy:
                poly[i], surf[i], best[i] = tp[hit[k]], sy[k], dy[k]
        return poly, surf

    def clearance(self, points, poly_ids=None, dy: float = 1.2):
        """Horizontal distance from each point to the nearest border edge whose height is within `dy`
        of the point. A point 0.30 m from the border passes an agent of radius 0.28 with 2 cm to spare."""
        pts = np.atleast_2d(np.asarray(points, dtype=np.float64))
        seg = self.border_edges()
        out = np.full(len(pts), np.inf)
        if not len(seg):
            return out
        a, b = seg[:, 0], seg[:, 1]
        d = (b - a)[:, (0, 2)]
        l2 = np.maximum((d * d).sum(axis=1), 1e-12)
        ymid = 0.5 * (a[:, 1] + b[:, 1])
        for i, (px, py, pz) in enumerate(pts):
            t = np.clip(((px - a[:, 0]) * d[:, 0] + (pz - a[:, 2]) * d[:, 1]) / l2, 0.0, 1.0)
            cx, cz = a[:, 0] + t * d[:, 0], a[:, 2] + t * d[:, 1]
            dist = np.hypot(px - cx, pz - cz)
            dist = np.where(np.abs(ymid - py) <= dy + np.abs(b[:, 1] - a[:, 1]), dist, np.inf)
            out[i] = dist.min()
        return out


# ---------------------------------------------------------------------------------------------
# Building the merged mesh
# ---------------------------------------------------------------------------------------------

def _union_find(n: int, pairs: np.ndarray) -> np.ndarray:
    parent = np.arange(n)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for a, b in pairs:
        ra, rb = find(int(a)), find(int(b))
        if ra != rb:
            parent[rb] = ra
    return np.array([find(i) for i in range(n)])


def _match_portals(tiles, offsets_poly, poly_verts, verts, tol=0.02, max_dy=0.6):
    """Match portal edges (neis & 0x8000) of neighbouring tiles geometrically.

    A portal edge lies on a tile border: both end points share x (or z) and that value equals the tile's
    bmin/bmax on the same axis. Two edges connect when they lie on the same border line, belong to
    different tiles and overlap along the border by more than `tol`, with surface heights that differ by
    less than `max_dy` in the middle of the overlap (Detour links within the walkable climb likewise).
    Returns (sorted polygon pairs, number of portal edges that found no partner).
    """
    portal = []                      # (poly, tile, axis, coord, lo, hi, y_lo, y_hi)
    stray = 0
    for ti, tile in enumerate(tiles):
        base = offsets_poly[ti]
        for p in range(len(tile.poly_nverts)):
            n = int(tile.poly_nverts[p])
            for k in range(n):
                if not tile.poly_neis[p, k] & EXTERNAL_LINK:
                    continue
                gp = base + p
                va, vb = verts[poly_verts[gp, k]], verts[poly_verts[gp, (k + 1) % n]]
                axis = None
                for ax, other in ((0, 2), (2, 0)):
                    if abs(va[ax] - vb[ax]) <= tol:
                        coord = 0.5 * (va[ax] + vb[ax])
                        if min(abs(coord - tile.bmin[ax]), abs(coord - tile.bmax[ax])) <= tol:
                            axis = ax
                            break
                if axis is None:
                    stray += 1
                    continue
                o = 2 if axis == 0 else 0
                lo_v, hi_v = (va, vb) if va[o] <= vb[o] else (vb, va)
                portal.append((gp, ti, axis, coord, lo_v[o], hi_v[o], lo_v[1], hi_v[1]))
    buckets: dict = {}
    for e in portal:
        buckets.setdefault((e[2], int(round(e[3] / 0.05))), []).append(e)
    pairs, matched = set(), set()
    for (axis, key), items in buckets.items():
        partners = items + buckets.get((axis, key + 1), [])
        for a in items:
            for b in partners:
                if a is b or a[1] == b[1] or abs(a[3] - b[3]) > tol:
                    continue
                lo, hi = max(a[4], b[4]), min(a[5], b[5])
                if hi - lo <= tol:
                    continue
                mid = 0.5 * (lo + hi)

                def height(e):
                    span = e[5] - e[4]
                    t = 0.5 if span < 1e-9 else (mid - e[4]) / span
                    return e[6] + t * (e[7] - e[6])

                if abs(height(a) - height(b)) > max_dy:
                    continue
                pairs.add((min(a[0], b[0]), max(a[0], b[0])))
                matched.update((id(a), id(b)))
    unmatched = sum(1 for e in portal if id(e) not in matched)
    return sorted(pairs), unmatched + stray


def _common_placement(tiles: list[Tile]) -> Placement:
    first = tiles[0].placement
    for t in tiles[1:]:
        if t.placement != first:
            raise NavMeshFormatError(f"tiles of one NavMesh carry different placements ({first} and {t.placement})")
    return first


def _place_tile(tile: Tile, placement: Placement) -> Tile:
    """World-space copy of a tile: vertices mapped, bmin / bmax the box of the eight mapped corners. The copy
    carries the identity placement (its arrays are world now), so rebuilding from it cannot apply the move twice."""
    corners = np.array([[x, y, z] for x in (tile.bmin[0], tile.bmax[0]) for y in (tile.bmin[1], tile.bmax[1])
                        for z in (tile.bmin[2], tile.bmax[2])], dtype=np.float64)
    world = placement.apply(corners)
    return dataclasses.replace(tile, verts=placement.apply(tile.verts), bmin=tuple(float(c) for c in world.min(axis=0)),
                               bmax=tuple(float(c) for c in world.max(axis=0)), placement=Placement())


def build_navmesh(tiles: list[Tile], placement: Placement | None = None) -> NavMesh:
    """Merge parsed (non-empty) tiles into one NavMesh with adjacency and component labels, in world coordinates.

    `placement` defaults to the one the tiles carry (`tiles_from_navmesh_data` stamps every tile with its
    NavMeshData's m_Position / m_Rotation); pass `Placement()` to read the raw tile frame. Connectivity is
    built in the tile frame, where the tile grid is axis aligned, and the result is mapped afterwards.
    """
    if not tiles:
        raise NavMeshFormatError("no non-empty tiles")
    placement = placement if placement is not None else _common_placement(tiles)
    placement.require_upright()
    vert_off, poly_off = [0], [0]
    for t in tiles:
        vert_off.append(vert_off[-1] + len(t.verts))
        poly_off.append(poly_off[-1] + len(t.poly_verts))
    verts = np.concatenate([t.verts for t in tiles])
    pv = np.concatenate([np.where(t.poly_verts >= 0, t.poly_verts + vert_off[i], -1) for i, t in enumerate(tiles)])
    nv = np.concatenate([t.poly_nverts for t in tiles])
    area = np.concatenate([t.poly_area for t in tiles])
    ptile = np.concatenate([np.full(len(t.poly_verts), i, dtype=np.int32) for i, t in enumerate(tiles)])
    nei = np.full(pv.shape, -1, dtype=np.int64)
    inner = []
    for i, t in enumerate(tiles):
        raw = t.poly_neis.astype(np.int64)
        slots = np.arange(MAX_POLY_VERTS)[None, :] < t.poly_nverts[:, None]
        is_local = slots & ((raw & EXTERNAL_LINK) == 0) & (raw != 0)
        glob = np.where(is_local, raw - 1 + poly_off[i], -1)
        glob = np.where(slots & ((raw & EXTERNAL_LINK) != 0), -2, glob)
        nei[poly_off[i]:poly_off[i + 1]] = glob
        rows, cols = np.nonzero(is_local)
        for r, c in zip(rows, cols):
            a, b = poly_off[i] + r, int(glob[r, c])
            inner.append((min(a, b), max(a, b)))
    portal_pairs, unmatched = _match_portals(tiles, poly_off, pv, verts)
    pairs = np.array(sorted(set(inner) | set(portal_pairs)), dtype=np.int64).reshape(-1, 2)
    mesh = NavMesh(verts, pv, nv, area, ptile, nei, tiles=tiles, edges=pairs,
                   portal_pairs=len(portal_pairs), unmatched_portal_edges=unmatched)
    root = _union_find(mesh.poly_count, pairs)
    axz, _a3 = mesh.poly_areas()
    uniq, inv = np.unique(root, return_inverse=True)
    comp_area = np.zeros(len(uniq))
    np.add.at(comp_area, inv, axz)
    order = np.argsort(-comp_area, kind="stable")
    rank = np.empty(len(uniq), dtype=np.int64)
    rank[order] = np.arange(len(uniq))
    mesh.labels = rank[inv]
    if placement.is_identity:
        mesh.tiles = [t if t.placement == placement else dataclasses.replace(t, placement=placement) for t in tiles]
    else:
        # An isometry about Y: areas, adjacency and the component ranking are unchanged. The fan triangles
        # computed for the areas above are still in the tile frame, so drop that cache with the move.
        mesh.verts = placement.apply(mesh.verts)
        mesh.tiles = [_place_tile(t, placement) for t in tiles]
        mesh._tris = None
    mesh.placement = placement             # what was applied; `tiles` are already world and carry the identity
    return mesh


def check_symmetry(mesh: NavMesh) -> int:
    """Number of in-tile neighbour links that are not reciprocal (a format sanity check; 0 expected)."""
    bad = 0
    pairs = {}
    for k in range(MAX_POLY_VERTS):
        sel = np.nonzero((k < mesh.poly_nverts) & (mesh.poly_nei[:, k] >= 0))[0]
        for p in sel:
            pairs.setdefault(int(p), set()).add(int(mesh.poly_nei[p, k]))
    for p, ns in pairs.items():
        for q in ns:
            if p not in pairs.get(q, ()):
                bad += 1
    return bad


def component_summary(mesh: NavMesh):
    """List of dicts (largest first): polygons, area_xz, area_3d, bbox, y range."""
    axz, a3 = mesh.poly_areas()
    out = []
    for label in range(int(mesh.labels.max()) + 1):
        sel = mesh.labels == label
        vids = np.unique(mesh.poly_verts[sel][mesh.poly_verts[sel] >= 0])
        v = mesh.verts[vids]
        out.append({
            "label": label,
            "polygons": int(sel.sum()),
            "area_xz": float(axz[sel].sum()),
            "area_3d": float(a3[sel].sum()),
            "min": [float(c) for c in v.min(axis=0)],
            "max": [float(c) for c in v.max(axis=0)],
        })
    return out


# ---------------------------------------------------------------------------------------------
# Sampling: a regular grid of points over the walkable polygons (used by navmesh_diff)
# ---------------------------------------------------------------------------------------------

def sample_polygons(mesh: NavMesh, step: float, origin=(0.0, 0.0)):
    """Points of the lattice origin + step * Z^2 that fall inside a polygon (xz), with the polygon's
    surface height. Returns (xyz (N,3), poly (N,)). Points on a shared edge belong to one polygon only
    (first fan triangle wins), so a point is never counted twice."""
    tv, tp = mesh.fan_triangles()
    ox, oz = origin
    pts, polys, seen = [], [], set()
    for tri, p in zip(tv, tp):
        x0, x1 = tri[:, 0].min(), tri[:, 0].max()
        z0, z1 = tri[:, 2].min(), tri[:, 2].max()
        i0, i1 = math.ceil((x0 - ox) / step - 1e-9), math.floor((x1 - ox) / step + 1e-9)
        j0, j1 = math.ceil((z0 - oz) / step - 1e-9), math.floor((z1 - oz) / step + 1e-9)
        if i1 < i0 or j1 < j0:
            continue
        gi, gj = np.meshgrid(np.arange(i0, i1 + 1), np.arange(j0, j1 + 1), indexing="ij")
        px, pz = ox + gi.ravel() * step, oz + gj.ravel() * step
        v0 = tri[1] - tri[0]
        v1 = tri[2] - tri[0]
        den = v0[0] * v1[2] - v1[0] * v0[2]
        if abs(den) < 1e-12:
            continue
        dx, dz = px - tri[0, 0], pz - tri[0, 2]
        u = (dx * v1[2] - v1[0] * dz) / den
        w = (v0[0] * dz - dx * v0[2]) / den
        inside = (u >= -1e-9) & (w >= -1e-9) & (u + w <= 1 + 1e-9)
        if not inside.any():
            continue
        py = tri[0, 1] + u[inside] * v0[1] + w[inside] * v1[1]
        for x, z, y, ii, jj in zip(px[inside], pz[inside], py, gi.ravel()[inside], gj.ravel()[inside]):
            key = (int(ii), int(jj), round(float(y), 1))
            if key in seen:
                continue
            seen.add(key)
            pts.append((x, y, z))
            polys.append(p)
    return np.array(pts, dtype=np.float64).reshape(-1, 3), np.array(polys, dtype=np.int64)


# ---------------------------------------------------------------------------------------------
# Loading from the live install
# ---------------------------------------------------------------------------------------------

def placement_from_navmesh_data(tree: dict) -> Placement:
    """The `m_Position` / `m_Rotation` of a read_typetree() dict of a `NavMeshData` (both are required: a
    missing key means another serialisation, and guessing the identity would hide a rotated mesh)."""
    try:
        pos, rot = tree["m_Position"], tree["m_Rotation"]
        return Placement(tuple(float(pos[k]) for k in "xyz"), tuple(float(rot[k]) for k in "xyzw"))
    except (KeyError, TypeError, ValueError) as exc:
        raise NavMeshFormatError(f"NavMeshData has no readable m_Position / m_Rotation ({exc!r})") from exc


def tiles_from_navmesh_data(tree: dict) -> list[Tile]:
    """Parse a read_typetree() dict of a `NavMeshData` object. Every tile is stamped with the object's
    placement, so `build_navmesh(tiles)` returns world coordinates; the tile arrays themselves stay raw."""
    placement = placement_from_navmesh_data(tree)
    out = []
    for entry in tree["m_NavMeshTiles"]:
        tile = parse_tile(bytes(entry["m_MeshData"]))
        if tile is not None:
            out.append(dataclasses.replace(tile, placement=placement))
    return out


def load_navmesh_tiles(assets_file: str, path_id: int) -> tuple[list[Tile], dict]:
    """Read one NavMeshData from a game assets file (read-only; UnityPy imported lazily). The tiles are in the
    NavMeshData's own frame and carry its placement; pass them to `build_navmesh` for world coordinates.
    `meta` lists the placement as plain `position` / `rotation` lists."""
    import UnityPy
    env = UnityPy.load(assets_file)
    for f in env.files.values():
        objects = getattr(f, "objects", None)
        if objects and path_id in objects and objects[path_id].type.name == "NavMeshData":
            tree = objects[path_id].read_typetree()
            placement = placement_from_navmesh_data(tree)
            meta = {"agent_type_id": tree.get("m_AgentTypeID"), "settings": tree.get("m_NavMeshBuildSettings"),
                    "offmesh_links": len(tree.get("m_OffMeshLinks") or []), "tiles_total": len(tree["m_NavMeshTiles"]),
                    "position": list(placement.position), "rotation": list(placement.rotation)}
            return tiles_from_navmesh_data(tree), meta
    raise NavMeshFormatError(f"no NavMeshData with path_id {path_id} in {assets_file}")


def mesh_from_triangulation(vertices, indices, areas=None) -> NavMesh:
    """Wrap a native triangulation (`ExportMapTriangulation` receipt: vertices, indices, areas) as a
    NavMesh whose polygons are the triangles; adjacency by shared vertex pairs after welding."""
    v = np.asarray(vertices, dtype=np.float64).reshape(-1, 3)
    idx = np.asarray(indices, dtype=np.int64).reshape(-1, 3)
    key = np.round(v / 1e-3).astype(np.int64)
    _u, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inv = inv.reshape(-1)
    verts = v[first]
    tri = inv[idx]
    keep = (tri[:, 0] != tri[:, 1]) & (tri[:, 1] != tri[:, 2]) & (tri[:, 0] != tri[:, 2])
    tri = tri[keep]
    area = np.zeros(len(tri), dtype=np.uint8) if areas is None else np.asarray(areas, dtype=np.uint8)[keep]
    p = len(tri)
    pv = np.full((p, MAX_POLY_VERTS), -1, dtype=np.int32)
    pv[:, :3] = tri
    nei = np.full((p, MAX_POLY_VERTS), -1, dtype=np.int64)
    edge_owner: dict = {}
    pairs = set()
    for i in range(p):
        for k in range(3):
            a, b = int(tri[i, k]), int(tri[i, (k + 1) % 3])
            other = edge_owner.get((b, a))
            if other is not None:
                pairs.add((other[0], i))
                nei[i, k] = other[0]
                nei[other[0], other[1]] = i
            edge_owner[(a, b)] = (i, k)
    mesh = NavMesh(verts, pv, np.full(p, 3, dtype=np.uint8), area, np.zeros(p, dtype=np.int32), nei,
                   edges=np.array(sorted(pairs), dtype=np.int64).reshape(-1, 2))
    root = _union_find(p, mesh.edges)
    axz, _a3 = mesh.poly_areas()
    uniq, inv2 = np.unique(root, return_inverse=True)
    comp = np.zeros(len(uniq))
    np.add.at(comp, inv2, axz)
    order = np.argsort(-comp, kind="stable")
    rank = np.empty(len(uniq), dtype=np.int64)
    rank[order] = np.arange(len(uniq))
    mesh.labels = rank[inv2]
    return mesh
