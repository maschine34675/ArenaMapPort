"""Offline walkability model of a set of collider triangles (numpy only).

Why. docs/PLAN-AIRPIT.md item 7 wants to know, without a 15 minute Unity bake, where an agent of the
port's size can stand on the imported colliders. This is not a re-implementation of Recast. It is a
voxel column test on a fine xz grid that answers one question for a list of query points (the points
come from Arena's own NavMesh, see `navmesh_diff.py`): "if an agent of radius R and height H stood
here, would a collider triangle intrude into its body, and is there ground to stand on?".

Model.
  * The xz plane is a grid of square cells (default 0.1 m). Every triangle is rasterised
    conservatively: a cell is hit when the projected triangle overlaps it (separating-axis test for
    small triangles, per-row extent for large ones); its y interval is the plane height over the
    cell, clamped to the triangle's own y range. Exactly vertical triangles (zero projected area)
    mark the cells along their projected segment with the full y range of the triangle.
  * Per cell the occupied y intervals are stored as bit sets (`y_step` metres per bit) in a
    `solid` array. Triangles facing up within the agent slope also fill a `floor` array.
  * The body of an agent at (x, y, z) is the vertical cylinder of radius R from y + climb (steps up to
    the climb height are walked over) to y + H. A point is blocked when `solid`, dilated by a disc of
    radius R in the xz plane, has a bit inside that y range; it is supported when `floor` has a bit
    within +-`support_tol` of y at the point's own cell.
  * `attribute_blockers` runs the rasterisation a second time keeping the owner of every fragment, to
    say WHICH colliders intrude into the blocked bodies.

Winding. A triangle faces up when its normal (b - a) x (c - a) has a positive y component, which is
the clockwise-from-above winding of Unity's left-handed convention; loaders must repair the winding of
mirrored transforms (port_colliders.py does).

Accuracy limits (measured against the spike bake in docs/progress/airpit-release.md): cell and bit
quantisation (0.1 m), conservative rasterisation (slightly over-blocks around slanted triangles), no
convex-hull treatment of convex MeshColliders, coarse sphere/capsule tessellation, and no Recast
region merging / simplification (so no minRegionArea island removal).
"""
from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

ALL_ONES = np.uint64(0xFFFFFFFFFFFFFFFF)


@dataclass(frozen=True)
class AgentParams:
    radius: float
    height: float
    climb: float
    slope_deg: float = 48.0


ARENA_AGENT = AgentParams(0.28, 1.6, 0.38, 48.0)    # Arena's own NavMeshData build settings (AirPit)
PORT_AGENT = AgentParams(0.30, 1.7, 0.38, 48.0)     # BakeNavMesh.cs constants (SPT agent 0)


@dataclass
class Grid:
    x0: float
    z0: float
    cs: float
    nx: int
    nz: int
    y0: float
    dy: float
    nbits: int

    @property
    def cells(self) -> int:
        return self.nx * self.nz

    @property
    def words(self) -> int:
        return (self.nbits + 63) // 64

    def cell_of(self, x, z):
        ix = np.floor((np.asarray(x) - self.x0) / self.cs).astype(np.int64)
        iz = np.floor((np.asarray(z) - self.z0) / self.cs).astype(np.int64)
        return ix, iz

    def inside(self, ix, iz):
        return (ix >= 0) & (ix < self.nx) & (iz >= 0) & (iz < self.nz)

    def bin_floor(self, y):
        return np.floor((np.asarray(y) - self.y0) / self.dy).astype(np.int64)

    def bin_ceil(self, y):
        return np.ceil((np.asarray(y) - self.y0) / self.dy).astype(np.int64)


def make_grid(xmin, xmax, zmin, zmax, ymin, ymax, cs=0.1, y_step=0.1) -> Grid:
    nx = int(math.ceil((xmax - xmin) / cs)) + 1
    nz = int(math.ceil((zmax - zmin) / cs)) + 1
    nbits = int(math.ceil((ymax - ymin) / y_step)) + 1
    return Grid(xmin, zmin, cs, nx, nz, ymin, y_step, nbits)


# ---------------------------------------------------------------------------------------------
# bit helpers
# ---------------------------------------------------------------------------------------------

def _ones(k):
    """(1 << k) - 1 for k in 0..64 as uint64 (k >= 64 -> all ones)."""
    k = np.asarray(k, dtype=np.int64)
    out = (np.uint64(1) << np.minimum(k, 63).astype(np.uint64)) - np.uint64(1)
    return np.where(k >= 64, ALL_ONES, out)


def range_masks(lo, hi, words):
    """(n, words) uint64 masks with bits lo..hi (inclusive) set; empty when hi < lo."""
    lo = np.asarray(lo, dtype=np.int64)[:, None]
    hi = np.asarray(hi, dtype=np.int64)[:, None]
    w = np.arange(words, dtype=np.int64)[None, :] * 64
    a = np.clip(lo - w, 0, 64)
    b = np.clip(hi - w + 1, 0, 64)
    return _ones(b) & ~_ones(a)


# ---------------------------------------------------------------------------------------------
# conservative rasterisation
# ---------------------------------------------------------------------------------------------

def _prepare(tris, grid: Grid):
    """Per-triangle bbox cell ranges; `keep` drops triangles wholly outside the grid."""
    p = tris[:, :, (0, 2)]
    ix0 = np.floor((p[:, :, 0].min(axis=1) - grid.x0) / grid.cs).astype(np.int64)
    ix1 = np.floor((p[:, :, 0].max(axis=1) - grid.x0) / grid.cs).astype(np.int64)
    iz0 = np.floor((p[:, :, 1].min(axis=1) - grid.z0) / grid.cs).astype(np.int64)
    iz1 = np.floor((p[:, :, 1].max(axis=1) - grid.z0) / grid.cs).astype(np.int64)
    keep = (ix1 >= 0) & (ix0 < grid.nx) & (iz1 >= 0) & (iz0 < grid.nz)
    ix0, ix1 = np.clip(ix0, 0, grid.nx - 1), np.clip(ix1, 0, grid.nx - 1)
    iz0, iz1 = np.clip(iz0, 0, grid.nz - 1), np.clip(iz1, 0, grid.nz - 1)
    return keep, ix0, ix1, iz0, iz1


def triangle_normals(tris):
    return np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])


SMALL_BBOX_CELLS = 256          # triangles up to this many bbox cells use the padded separating-axis path
CHUNK_CANDIDATES = 1_500_000    # padded candidate cells per vectorised chunk


def iter_fragments(tris, owner_idx, grid: Grid, up_cos: float, solid_ok=None, floor_ok=None):
    """Yield (cell, lo_bin, hi_bin, owner, to_solid, to_floor) arrays.

    cell = iz * nx + ix; lo/hi are inclusive y bit indices. `solid_ok` / `floor_ok` are optional
    per-triangle booleans: a triangle contributes to `solid` when solid_ok, and to `floor` when it is
    floor_ok and faces up within the agent slope (`up_cos`). Triangles with neither flag are skipped.
    """
    n = len(tris)
    if n == 0:
        return
    nrm = triangle_normals(tris)
    nlen = np.linalg.norm(nrm, axis=1)
    facing_up = (nrm[:, 1] >= up_cos * nlen) & (nlen > 1e-12)
    to_solid = np.ones(n, dtype=bool) if solid_ok is None else np.asarray(solid_ok, dtype=bool)
    to_floor = facing_up & (np.ones(n, dtype=bool) if floor_ok is None else np.asarray(floor_ok, dtype=bool))
    keep, ix0, ix1, iz0, iz1 = _prepare(tris, grid)
    keep &= (to_solid | to_floor) & np.isfinite(tris).all(axis=(1, 2)) & (nlen > 1e-12)
    cells = (ix1 - ix0 + 1) * (iz1 - iz0 + 1)
    small = np.nonzero(keep & (cells <= SMALL_BBOX_CELLS))[0]
    large = np.nonzero(keep & (cells > SMALL_BBOX_CELLS))[0]
    if len(small):
        dims = np.maximum(ix1[small] - ix0[small] + 1, iz1[small] - iz0[small] + 1)
        small = small[np.argsort(dims, kind="stable")]
        dims = np.maximum(ix1[small] - ix0[small] + 1, iz1[small] - iz0[small] + 1)
        pos = 0
        while pos < len(small):
            end = pos + 1
            while end < len(small) and (end + 1 - pos) * int(dims[end]) ** 2 <= CHUNK_CANDIDATES:
                end += 1
            chunk = small[pos:end]
            mx = int((ix1[chunk] - ix0[chunk] + 1).max())
            mz = int((iz1[chunk] - iz0[chunk] + 1).max())
            frag = _small_fragments(tris, nrm, nlen, chunk, ix0, ix1, iz0, iz1, mx, mz, grid)
            if frag is not None:
                tri_idx, cell, lo, hi = frag
                yield cell, lo, hi, owner_idx[tri_idx], to_solid[tri_idx], to_floor[tri_idx]
            pos = end
    for t in large:
        frag = _scanline_fragments(tris[t], nrm[t], nlen[t], grid)
        if frag is None:
            continue
        cell, lo, hi = frag
        k = len(cell)
        yield (cell, lo, hi, np.full(k, owner_idx[t], dtype=owner_idx.dtype),
               np.full(k, to_solid[t]), np.full(k, to_floor[t]))


def _plane_range(t, nrm, nlen, xlo, xhi, zlo, zhi):
    """y interval of triangle planes over rectangles (arrays broadcast to (B, ...)), clamped to the
    triangle's own y range. t (B,3,3), nrm (B,3), nlen (B,)."""
    ymin, ymax = t[:, :, 1].min(axis=1), t[:, :, 1].max(axis=1)
    ny = nrm[:, 1]
    flat = np.abs(ny) > 1e-9 * nlen
    safe = np.where(flat, ny, 1.0)
    shape = np.broadcast(xlo, zlo).shape
    pad = (slice(None),) + (None,) * (len(shape) - 1)
    ylo = np.full(shape, np.inf)
    yhi = np.full(shape, -np.inf)
    for cx in (xlo, xhi):
        for cz in (zlo, zhi):
            yy = t[:, 0, 1][pad] - (nrm[:, 0][pad] * (cx - t[:, 0, 0][pad]) + nrm[:, 2][pad] * (cz - t[:, 0, 2][pad])) / safe[pad]
            ylo, yhi = np.minimum(ylo, yy), np.maximum(yhi, yy)
    ylo = np.where(flat[pad], np.maximum(ylo, ymin[pad]), ymin[pad])
    yhi = np.where(flat[pad], np.minimum(yhi, ymax[pad]), ymax[pad])
    return ylo, np.maximum(yhi, ylo)


def _small_fragments(tris, nrm, nlen, chunk, ix0, ix1, iz0, iz1, mx, mz, grid):
    t = tris[chunk]                                       # (B,3,3)
    ii = ix0[chunk][:, None] + np.arange(mx)[None, :]     # (B, mx)
    jj = iz0[chunk][:, None] + np.arange(mz)[None, :]
    overlap = (ii <= ix1[chunk][:, None])[:, :, None] & (jj <= iz1[chunk][:, None])[:, None, :]
    xlo3 = (grid.x0 + ii * grid.cs)[:, :, None]
    zlo3 = (grid.z0 + jj * grid.cs)[:, None, :]
    xhi3, zhi3 = xlo3 + grid.cs, zlo3 + grid.cs
    a2 = nrm[chunk, 1]
    degenerate = np.abs(a2) < 1e-10
    # a2 is the y component of the 3D normal = -(2D cross of the projected edges); the interior of an
    # edge loop with positive 2D cross lies on the left (>= 0) of every edge
    s = np.where(a2 <= 0, 1.0, -1.0)
    px, pz = t[:, :, 0], t[:, :, 2]
    for e in range(3):
        ax, az = px[:, e], pz[:, e]
        ex, ez = px[:, (e + 1) % 3] - ax, pz[:, (e + 1) % 3] - az
        gx, gz = -s * ez, s * ex
        cx = np.where(gx[:, None, None] > 0, xhi3, xlo3)
        cz = np.where(gz[:, None, None] > 0, zhi3, zlo3)
        dmax = s[:, None, None] * (ex[:, None, None] * (cz - az[:, None, None]) - ez[:, None, None] * (cx - ax[:, None, None]))
        overlap &= (dmax >= -1e-9) | degenerate[:, None, None]
    if degenerate.any():
        di = np.nonzero(degenerate)[0]
        overlap[di] &= _segment_cells(t[di], xlo3[di], zlo3[di], xhi3[di], zhi3[di])
    ylo, yhi = _plane_range(t, nrm[chunk], nlen[chunk], xlo3, xhi3, zlo3, zhi3)
    bi, bj, bk = np.nonzero(overlap)
    if not len(bi):
        return None
    lo_raw = grid.bin_floor(ylo[bi, bj, bk])
    hi_raw = grid.bin_floor(yhi[bi, bj, bk])
    ok = (hi_raw >= 0) & (lo_raw < grid.nbits)
    cell = (jj[bi, bk] * grid.nx + ii[bi, bj]).astype(np.int64)
    return chunk[bi][ok], cell[ok], np.clip(lo_raw[ok], 0, grid.nbits - 1), np.clip(hi_raw[ok], 0, grid.nbits - 1)


def _segment_cells(t, xlo, zlo, xhi, zhi):
    """Triangles whose projection is a segment: does the segment touch each candidate rectangle?"""
    px, pz = t[:, :, 0], t[:, :, 2]
    use_x = (px.max(axis=1) - px.min(axis=1)) >= (pz.max(axis=1) - pz.min(axis=1))
    i_lo = np.where(use_x, px.argmin(axis=1), pz.argmin(axis=1))
    i_hi = np.where(use_x, px.argmax(axis=1), pz.argmax(axis=1))
    r = np.arange(len(t))
    ax, az, bx, bz = px[r, i_lo], pz[r, i_lo], px[r, i_hi], pz[r, i_hi]
    ex, ez = (bx - ax)[:, None, None], (bz - az)[:, None, None]
    ax3, az3 = ax[:, None, None], az[:, None, None]
    d = [ex * (cz - az3) - ez * (cx - ax3) for cx, cz in ((xlo, zlo), (xhi, zlo), (xlo, zhi), (xhi, zhi))]
    straddle = (np.minimum.reduce(d) <= 1e-9) & (np.maximum.reduce(d) >= -1e-9)
    sx0, sx1 = np.minimum(ax, bx)[:, None, None], np.maximum(ax, bx)[:, None, None]
    sz0, sz1 = np.minimum(az, bz)[:, None, None], np.maximum(az, bz)[:, None, None]
    return straddle & (xhi >= sx0) & (xlo <= sx1) & (zhi >= sz0) & (zlo <= sz1)


def _scanline_fragments(tri, nrm, nlen, grid: Grid):
    """Cells of one (large) triangle. Per z row the triangle-within-row polygon is convex, so every cell
    column between its x extent is hit (exact cell/triangle overlap); cost follows the covered cells."""
    px, pz = tri[:, 0], tri[:, 2]
    iz0 = max(int(math.floor((pz.min() - grid.z0) / grid.cs)), 0)
    iz1 = min(int(math.floor((pz.max() - grid.z0) / grid.cs)), grid.nz - 1)
    if iz1 < iz0:
        return None
    rows = np.arange(iz0, iz1 + 1)
    zlo = grid.z0 + rows * grid.cs
    zhi = zlo + grid.cs
    xs_min = np.full(len(rows), np.inf)
    xs_max = np.full(len(rows), -np.inf)
    for e in range(3):
        ax, az, bx, bz = px[e], pz[e], px[(e + 1) % 3], pz[(e + 1) % 3]
        if abs(bz - az) >= 1e-12:
            for zline in (zlo, zhi):
                u = (zline - az) / (bz - az)
                ok = (u >= 0) & (u <= 1)
                x = ax + u * (bx - ax)
                xs_min = np.where(ok, np.minimum(xs_min, x), xs_min)
                xs_max = np.where(ok, np.maximum(xs_max, x), xs_max)
        else:                                            # horizontal edge: contributes to the row it lies in
            inside = (az >= zlo) & (az <= zhi)
            xs_min = np.where(inside, np.minimum(xs_min, min(ax, bx)), xs_min)
            xs_max = np.where(inside, np.maximum(xs_max, max(ax, bx)), xs_max)
        inside = (az >= zlo) & (az <= zhi)               # vertex a inside the row slab
        xs_min = np.where(inside, np.minimum(xs_min, ax), xs_min)
        xs_max = np.where(inside, np.maximum(xs_max, ax), xs_max)
    valid = np.isfinite(xs_min)
    if not valid.any():
        return None
    rows, xs_min, xs_max = rows[valid], xs_min[valid], xs_max[valid]
    raw0 = np.floor((xs_min - grid.x0) / grid.cs).astype(np.int64)
    raw1 = np.floor((xs_max - grid.x0) / grid.cs).astype(np.int64)
    inside = (raw1 >= 0) & (raw0 <= grid.nx - 1)
    rows, c0, c1 = rows[inside], np.clip(raw0[inside], 0, grid.nx - 1), np.clip(raw1[inside], 0, grid.nx - 1)
    counts = c1 - c0 + 1
    total = int(counts.sum())
    if total <= 0:
        return None
    row_of = np.repeat(rows, counts)
    col = np.repeat(c0, counts) + (np.arange(total) - np.repeat(np.cumsum(counts) - counts, counts))
    xl = grid.x0 + col * grid.cs
    zl = grid.z0 + row_of * grid.cs
    ylo, yhi = _plane_range(tri[None], nrm[None], np.array([nlen]), xl[None], xl[None] + grid.cs, zl[None], zl[None] + grid.cs)
    lo_raw, hi_raw = grid.bin_floor(ylo[0]), grid.bin_floor(yhi[0])
    ok = (hi_raw >= 0) & (lo_raw < grid.nbits)
    return (row_of * grid.nx + col)[ok], np.clip(lo_raw[ok], 0, grid.nbits - 1), np.clip(hi_raw[ok], 0, grid.nbits - 1)


# ---------------------------------------------------------------------------------------------
# occupancy
# ---------------------------------------------------------------------------------------------

def build_occupancy(tris, owner_idx, grid: Grid, agent: AgentParams, solid_ok=None, floor_ok=None):
    """(solid, floor, fragment_count): bit arrays of shape (cells, words)."""
    solid = np.zeros((grid.cells, grid.words), dtype=np.uint64)
    floor = np.zeros_like(solid)
    up_cos = math.cos(math.radians(agent.slope_deg))
    count = 0
    for cell, lo, hi, _owner, to_solid, to_floor in iter_fragments(tris, owner_idx, grid, up_cos, solid_ok, floor_ok):
        masks = range_masks(lo, hi, grid.words)
        for w in range(grid.words):
            if to_solid.any():
                np.bitwise_or.at(solid[:, w], cell[to_solid], masks[to_solid, w])
            if to_floor.any():
                np.bitwise_or.at(floor[:, w], cell[to_floor], masks[to_floor, w])
        count += len(cell)
    return solid, floor, count


def disc_offsets(radius_cells: float):
    r = int(math.ceil(radius_cells))
    return [(dx, dz) for dz in range(-r, r + 1) for dx in range(-r, r + 1)
            if dx * dx + dz * dz <= radius_cells * radius_cells + 1e-9]


def dilate_disc(bits: np.ndarray, grid: Grid, radius: float) -> np.ndarray:
    """OR of `bits` over all cells within `radius` (metres) in the xz plane."""
    shaped = bits.reshape(grid.nz, grid.nx, grid.words)
    out = np.zeros_like(shaped)
    nz, nx = grid.nz, grid.nx
    for dx, dz in disc_offsets(radius / grid.cs):
        # out[z, x] |= shaped[z + dz, x + dx] wherever both indices exist
        out[max(0, -dz):nz - max(0, dz), max(0, -dx):nx - max(0, dx)] |= \
            shaped[max(0, dz):nz - max(0, -dz), max(0, dx):nx - max(0, -dx)]
    return out.reshape(grid.cells, grid.words)


def column_masks(y, grid: Grid, agent: AgentParams, floor_margin: float = 0.05):
    """Inclusive bit ranges of the agent body above ground points at heights `y`."""
    lo = grid.bin_ceil(np.asarray(y) + agent.climb + floor_margin)
    hi = grid.bin_floor(np.asarray(y) + agent.height) - 1
    return range_masks(lo, hi, grid.words)


def support_masks(y, grid: Grid, tol: float):
    lo = grid.bin_floor(np.asarray(y) - tol)
    hi = grid.bin_floor(np.asarray(y) + tol)
    return range_masks(lo, hi, grid.words)


@dataclass
class SampleVerdict:
    blocked: np.ndarray        # (N,) bool
    unsupported: np.ndarray    # (N,) bool
    outside: np.ndarray        # (N,) bool: sample outside the grid (not evaluated)


def evaluate_samples(points, solid, floor, grid: Grid, agent: AgentParams, floor_margin=0.05,
                     support_tol=0.3, shrink=0.0, dilated=None):
    """Blocked / unsupported verdict for each (x, y, z) in `points` (xz in the grid)."""
    pts = np.asarray(points, dtype=np.float64)
    ix, iz = grid.cell_of(pts[:, 0], pts[:, 2])
    inside = grid.inside(ix, iz)
    cell = np.where(inside, iz * grid.nx + ix, 0)
    if dilated is None:
        dilated = dilate_disc(solid, grid, max(agent.radius - shrink, 0.0))
    body = column_masks(pts[:, 1], grid, agent, floor_margin)
    blocked = ((dilated[cell] & body) != 0).any(axis=1) & inside
    sup = support_masks(pts[:, 1], grid, support_tol)
    supported = ((floor[cell] & sup) != 0).any(axis=1)
    return SampleVerdict(blocked, ~supported & inside, ~inside)


def attribute_blockers(tris, owner_idx, grid: Grid, agent: AgentParams, points, blocked, floor_margin=0.05,
                       shrink=0.0, solid_ok=None):
    """Which owners intrude into the bodies of the `blocked` sample points.

    Returns {owner: sorted array of the sample indices it intrudes into}, by running the
    rasterisation again and testing every fragment against the body slabs of the blocked samples
    (all disc offsets). Several samples sharing one cell (stacked levels) are all credited when the
    fragment overlaps the cell's combined slab.
    """
    pts = np.asarray(points, dtype=np.float64)
    ix, iz = grid.cell_of(pts[:, 0], pts[:, 2])
    idx = np.nonzero(grid.inside(ix, iz) & np.asarray(blocked, dtype=bool))[0]
    cells = (iz[idx] * grid.nx + ix[idx]).astype(np.int64)
    slabs = np.zeros((grid.cells, grid.words), dtype=np.uint64)
    body = column_masks(pts[idx, 1], grid, agent, floor_margin)
    for w in range(grid.words):
        np.bitwise_or.at(slabs[:, w], cells, body[:, w])
    sample_of_cell: dict = {}
    for k, c in zip(idx, cells):
        sample_of_cell.setdefault(int(c), []).append(int(k))
    offsets = disc_offsets(max(agent.radius - shrink, 0.0) / grid.cs)
    up_cos = math.cos(math.radians(agent.slope_deg))
    pairs = []
    for cell, lo, hi, owner, to_solid, _to_floor in iter_fragments(tris, owner_idx, grid, up_cos, solid_ok, None):
        if not to_solid.any():
            continue
        cell, lo, hi, owner = cell[to_solid], lo[to_solid], hi[to_solid], owner[to_solid]
        masks = range_masks(lo, hi, grid.words)
        fz, fx = cell // grid.nx, cell % grid.nx
        for dx, dz in offsets:
            tx, tz = fx + dx, fz + dz
            ok = (tx >= 0) & (tx < grid.nx) & (tz >= 0) & (tz < grid.nz)
            tc = np.where(ok, tz * grid.nx + tx, 0)
            hit = ok & ((slabs[tc] & masks) != 0).any(axis=1)
            if hit.any():
                pairs.append(np.unique(np.stack([owner[hit].astype(np.int64), tc[hit]], axis=1), axis=0))
    out: dict = {}
    if not pairs:
        return {}
    for o, c in np.unique(np.concatenate(pairs), axis=0):
        out.setdefault(int(o), set()).update(sample_of_cell.get(int(c), ()))
    return {o: np.array(sorted(v), dtype=np.int64) for o, v in out.items()}
