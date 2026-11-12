#!/usr/bin/env python3
"""Offline spawn-row coverage, extra Bot rows and exit placement for one ported map.

What it answers (all of it from the port's own BUILT NavMesh bundle and the tracked sidecars, no Unity, no host):
  1. Distance coverage. ArenaMode picks a Bot spawn row for a person/bot only among rows that are farther than a floor
     (default 25 m) from the others. For every walkable cell of the play component this tool counts the Bot rows that are
     farther than the floor, and reports min / median / share of cells with >= need (5), >= 2 and none, plus the worst
     region, for the existing rows and again after the selection.
  2. More rows. Greedy pick of additional rows from candidates: Arena AI points not yet used (<stem>.ai.json), the
     --sources file, and fill points sampled from the mesh. Existing rows are fixed.
  3. Exit options. For each team pocket (the initial spawn bound), the 4 x 4 m exit boxes that lie inside the pocket, with
     the three named options 'far', 'centre' and 'gate', and the metrics of the current exits.

The rules (every value is a CLI parameter; the defaults are the ones below)
  Model      The play component is the NavMesh component that holds most existing Bot rows. Border clearance is the horizontal
             distance to the nearest boundary edge OF THAT COMPONENT (an unmatched tile-portal edge counts as boundary) whose height
             is within 1.2 m. A row's y is the mesh surface under it.
  Pockets    The spawn bounds of <stem>.modes.json that are marked "initial": true (all active bounds when none is marked): rotated
             boxes. Their markers are the 'spawn_markers' entries.
  Gates      opened_barriers / start_gates / held_gates of the descriptor. With --package their collider footprints (per collider the
             XZ convex hull) in the closed pose and, where <stem>.doors.json has rows, the swept footprint of the open pose
             (translation rows: hull of closed + translated vertices; rotation rows: a disc around the node's pivot). Without
             --package gates are only known by the 'at' of door rows (3 m discs) and the gate margin is mostly skipped.
  Plain raid The area only reachable through held_gates counts as closed: reachability is a flood fill over the 1 m cells from the
             existing Bot rows with the cells inside a held gate's closed footprint (+0.3 m) removed.
  Coverage   Cells: 1 m lattice of the play component, outside both pocket footprints (also reported with the pockets). A Bot row
             is 'distant' when its distance to the cell is strictly greater than --floor (XZ and 3D are both reported,
             --metric picks the one the selection optimises, default 3d).
  Candidate  accepted when: on the play component (snap <= 0.5 m XZ, <= 1.0 m vertical); plain-raid reachable; inside bake_bounds;
             clearance >= --clearance (1.35), after a nudge of at most --nudge (2.0 m) toward more clearance (the move is recorded);
             outside both pocket footprints by >= --box-margin (1.5 m); >= --gate-margin (2.5 m) from every gate footprint (closed AND
             open pose); >= --player-margin (5 m) from every Player row; >= --spacing (7 m, 3D) from every other row, existing or
             selected; and, with --package, standing room per spawn_placement_probe.py (an UPPER bound on the host's IsValid). The
             rejection reason of every rejected candidate is recorded.
  Selection  Greedy, existing rows fixed. Each step adds the accepted candidate that most increases the number of cells with
             >= need distant rows; ties: more cells with >= 2, then (an addition of this tool) the larger total deficit reduction,
             then the larger minimum distance to all rows. A native candidate (AI / sources) wins over a fill point when its gain is
             within 5 % of the best. Stops at --max-rows total rows, or at --min-rows once no candidate improves the >= need count.
  Exit boxes 4 x 4 m, axis aligned (rotation 0), floor point = mesh surface at the centre; the box spans -0.5..+2.5 m around the
             floor (ExfilRepairs.cs). Valid: all four corners inside the pocket footprint, centre on the play component, all four
             corners on the mesh (within 0.5 m), plain-raid reachable, box edge >= --exit-gate-margin (0.5 m) from the pocket's gate
             footprints, centre clearance >= --exit-clearance (0), plain-raid reachability only with --exit-require-reach (else it is reported per pocket), and with --package no solid in the box above 0.3 m.
             Options per pocket: 'far' (max of min distance to the nearest marker / Player row), 'centre' (closest to the centroid of
             the pocket's markers), 'gate' (nearest to the pocket's gate on the inside).

Deterministic: stable sort keys, rounded numbers, no randomness, no network; the only writes are --out and --png.
Real data example:  python tools/validate/spawn_coverage.py --stem autoservice --package work/extract/autoservice-native-v2 --out x.json
"""
from __future__ import annotations

import argparse
import json
import math
import struct
import sys
import zlib
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(HERE))

import navmesh_oracle as oracle  # noqa: E402

DEFAULTS = {
    "floor": 25.0, "need": 5, "clearance": 1.35, "nudge": 2.0, "box_margin": 1.5, "gate_margin": 2.5,
    "player_margin": 5.0, "spacing": 7.0, "min_rows": 16, "max_rows": 24, "cell": 1.0, "fill_step": 2.0,
    "fill_clearance": 1.5, "max_candidates": 4000, "metric": "3d", "gate_link": 6.0, "exit_step": 1.0,
    "min_corners": 4, "exit_gate_margin": 0.5, "exit_require_reach": False, "exit_clearance": 0.0, "ai_used_radius": 2.5, "include_pockets": False, "probe": True,
}
EXIT_BOX = 4.0                  # box edge (x and z), like the existing exits
EXIT_FLOOR_BELOW = 0.5          # ExfilRepairs: the box spans 0.5 m below the floor point ...
EXIT_TOP = 2.5                  # ... to 2.5 m above it (SizeY 3)
EXIT_SOLID_FROM = 0.3
CLEARANCE_DY = 1.2
NATIVE_SOURCES = ("lasthero", "shootout", "ai_core", "ai_born", "stage", "other")
SNAP_XZ, SNAP_Y = 0.5, 1.0
NATIVE_GAIN_SLACK = 0.05


def r3(v):
    return round(float(v), 3)


def r2(v):
    return round(float(v), 2)


# ---------------------------------------------------------------------------------------------------------------------
# small geometry
# ---------------------------------------------------------------------------------------------------------------------
def quat_matrix(q) -> np.ndarray:
    """Unity quaternion (x, y, z, w) -> 3x3 rotation matrix (world = M @ local)."""
    x, y, z, w = (float(v) for v in q)
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def yaw_matrix(deg: float) -> np.ndarray:
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0.0, s], [0.0, 1.0, 0.0], [-s, 0.0, c]])


@dataclass
class Pocket:
    """A rotated spawn-bound box (Unity box: centre, size, rotation)."""
    name: str
    side: str
    center: np.ndarray
    size: np.ndarray
    rot: np.ndarray
    markers: np.ndarray = field(default_factory=lambda: np.zeros((0, 3)))

    @classmethod
    def from_bound(cls, b: dict) -> "Pocket":
        rot = quat_matrix(b["rotation"]) if b.get("rotation") is not None else yaw_matrix(float(b.get("yaw", 0.0)))
        return cls(b.get("name", "?"), str(b.get("side", "")), np.array(b["center"], float), np.array(b["size"], float), rot)

    def local(self, pts) -> np.ndarray:
        return (np.atleast_2d(np.asarray(pts, float)) - self.center) @ self.rot

    def contains(self, pts, margin: float = 0.0, with_y: bool = True) -> np.ndarray:
        loc = self.local(pts)
        h = self.size / 2.0 - margin
        ok = (np.abs(loc[:, 0]) <= h[0]) & (np.abs(loc[:, 2]) <= h[2])
        if with_y:
            ok &= np.abs(loc[:, 1]) <= self.size[1] / 2.0 + 1e-9
        return ok

    def xz_outside_distance(self, pts) -> np.ndarray:
        """0 inside the footprint, else the XZ distance to the rectangle (vertical extent ignored)."""
        loc = self.local(pts)
        h = self.size / 2.0
        return np.hypot(np.maximum(np.abs(loc[:, 0]) - h[0], 0.0), np.maximum(np.abs(loc[:, 2]) - h[2], 0.0))

    def y_range(self):
        return float(self.center[1] - self.size[1] / 2), float(self.center[1] + self.size[1] / 2)


def hull2d(points) -> np.ndarray:
    """Convex hull (monotone chain, counter-clockwise) of (N, 2) points; fewer than 3 distinct points are returned as they are."""
    pts = sorted({(round(float(x), 3), round(float(z), 3)) for x, z in np.asarray(points, float).reshape(-1, 2)})
    if len(pts) < 3:
        return np.array(pts, float).reshape(-1, 2)

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return np.array(lower[:-1] + upper[:-1], float)


def dist_to_hull(pts_xz, hull) -> np.ndarray:
    """XZ distance of points (N, 2) to a convex polygon (0 inside); a 1- or 2-point hull is a point / a segment."""
    p = np.asarray(pts_xz, float).reshape(-1, 2)
    h = np.asarray(hull, float).reshape(-1, 2)
    if not len(h):
        return np.full(len(p), np.inf)
    if len(h) == 1:
        return np.hypot(p[:, 0] - h[0, 0], p[:, 1] - h[0, 1])
    a = h
    b = np.roll(h, -1, axis=0) if len(h) > 2 else h[::-1]
    d = b - a
    l2 = np.maximum((d * d).sum(axis=1), 1e-12)
    t = np.clip(((p[:, None, 0] - a[None, :, 0]) * d[None, :, 0] + (p[:, None, 1] - a[None, :, 1]) * d[None, :, 1]) / l2[None, :], 0, 1)
    cx = a[None, :, 0] + t * d[None, :, 0]
    cz = a[None, :, 1] + t * d[None, :, 1]
    edge = np.hypot(p[:, None, 0] - cx, p[:, None, 1] - cz).min(axis=1)
    if len(h) > 2:
        cr = d[None, :, 0] * (p[:, None, 1] - a[None, :, 1]) - d[None, :, 1] * (p[:, None, 0] - a[None, :, 0])
        inside = (cr >= -1e-9).all(axis=1)
        edge = np.where(inside, 0.0, edge)
    return edge


def _segments_cross(p1, p2, p3, p4) -> bool:
    def orient(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    d1, d2, d3, d4 = orient(p3, p4, p1), orient(p3, p4, p2), orient(p1, p2, p3), orient(p1, p2, p4)
    return (d1 * d2 < 0) and (d3 * d4 < 0)


def hull_box_distance(hull, cx, cz, half) -> float:
    """XZ distance between a convex polygon (or a 1- or 2-point hull) and the axis-aligned square of half-edge `half` around (cx, cz); 0 on overlap."""
    h = np.asarray(hull, float).reshape(-1, 2)
    if not len(h):
        return math.inf
    box = [(cx - half, cz - half), (cx + half, cz - half), (cx + half, cz + half), (cx - half, cz + half)]
    qx = np.maximum(np.abs(h[:, 0] - cx) - half, 0.0)
    qz = np.maximum(np.abs(h[:, 1] - cz) - half, 0.0)
    best = float(np.hypot(qx, qz).min())
    if best == 0.0:
        return 0.0
    best = min(best, float(dist_to_hull(np.array(box), h).min()))
    if best == 0.0:
        return 0.0
    edges = list(zip(h, np.roll(h, -1, axis=0))) if len(h) > 2 else ([(h[0], h[-1])] if len(h) == 2 else [])
    for a, b in edges:
        for k in range(4):
            if _segments_cross(a, b, box[k], box[(k + 1) % 4]):
                return 0.0
    return best


def disc_hull(cx, cz, radius, n=24) -> np.ndarray:
    ang = np.linspace(0, 2 * math.pi, n, endpoint=False)
    return np.stack([cx + radius * np.cos(ang), cz + radius * np.sin(ang)], axis=1)


@dataclass
class Gate:
    path: str
    kinds: list
    closed: list                 # list of (K, 2) hulls in the closed pose
    swept: list                  # list of (K, 2) hulls: closed + open pose (what the margin is measured against)
    y_lo: float = -1e9
    y_hi: float = 1e9
    source: str = "colliders"    # colliders | door-at disc | none
    colliders: int = 0

    def distance(self, pts, swept: bool = True) -> np.ndarray:
        """XZ distance to the footprint, inf for points more than 2.5 m above/below its height range."""
        p = np.atleast_2d(np.asarray(pts, float))
        out = np.full(len(p), np.inf)
        ok = (p[:, 1] >= self.y_lo - 2.5) & (p[:, 1] <= self.y_hi + 2.5)
        if not ok.any():
            return out
        for h in (self.swept if swept else self.closed):
            out[ok] = np.minimum(out[ok], dist_to_hull(p[ok][:, [0, 2]], h))
        return out


def min_gate_box_distance(gates, cx, cy, cz, half) -> float:
    """Smallest XZ distance from the square box around (cx, cz) to the swept footprint of any gate (gates in another height band are skipped)."""
    best = math.inf
    for g in gates:
        if not (g.y_lo - 2.5 <= cy <= g.y_hi + 2.5):
            continue
        for h in g.swept:
            best = min(best, hull_box_distance(h, cx, cz, half))
    return best


def min_gate_distance(gates, pts, swept=True) -> np.ndarray:
    p = np.atleast_2d(np.asarray(pts, float))
    out = np.full(len(p), np.inf)
    for g in gates:
        out = np.minimum(out, g.distance(p, swept))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# lattice helpers
# ---------------------------------------------------------------------------------------------------------------------
class DSU:
    def __init__(self, n):
        self.p = list(range(n))

    def find(self, a):
        p = self.p
        while p[a] != a:
            p[a] = p[p[a]]
            a = p[a]
        return a

    def union(self, a, b):
        a, b = self.find(a), self.find(b)
        if a != b:
            self.p[max(a, b)] = min(a, b)


def lattice_index(pts, step):
    p = np.asarray(pts, float)
    return np.round(p[:, 0] / step).astype(np.int64), np.round(p[:, 2] / step).astype(np.int64)


def lattice_neighbours(pts, step, max_dy=1.0):
    """Yield (a, b) index pairs of cells that are 8-neighbours on the lattice and within max_dy vertically."""
    ii, jj = lattice_index(pts, step)
    table: dict = {}
    for k, (i, j) in enumerate(zip(ii.tolist(), jj.tolist())):
        table.setdefault((i, j), []).append(k)
    for (i, j), ks in table.items():
        for di, dj in ((0, 0), (1, 0), (0, 1), (1, 1), (1, -1)):
            others = table.get((i + di, j + dj))
            if not others:
                continue
            for a in ks:
                for b in others:
                    if b > a or (di, dj) != (0, 0):
                        if a != b and abs(pts[a][1] - pts[b][1]) <= max_dy:
                            yield a, b


def clusters_of(pts, mask, step, max_dy=1.0):
    """Connected clusters (lists of indices into pts) of the cells where mask is true, largest first."""
    idx = np.nonzero(mask)[0]
    if not len(idx):
        return []
    sub = np.asarray(pts, float)[idx]
    dsu = DSU(len(idx))
    for a, b in lattice_neighbours(sub, step, max_dy):
        dsu.union(a, b)
    groups: dict = {}
    for k in range(len(idx)):
        groups.setdefault(dsu.find(k), []).append(int(idx[k]))
    return sorted(groups.values(), key=lambda g: (-len(g), g[0]))


# ---------------------------------------------------------------------------------------------------------------------
# the play component
# ---------------------------------------------------------------------------------------------------------------------
class PlayMesh:
    """The NavMesh component the Bot rows live on, its boundary segments and the point queries on it."""

    def __init__(self, mesh, bot_rows):
        self.mesh = mesh
        rows = np.asarray(bot_rows, float).reshape(-1, 3)
        self.warnings: list = []
        votes: Counter = Counter()
        if len(rows):
            poly, _ = mesh.locate(rows, max_dy=2.0)
            for p in poly:
                if p >= 0:
                    votes[int(mesh.labels[p])] += 1
        if votes:
            best = max(votes.values())
            self.label = min(l for l, v in votes.items() if v == best)
            self.rows_on_component = int(votes[self.label])
        else:
            self.label = 0
            self.rows_on_component = 0
            self.warnings.append("no existing Bot row lies on the mesh; the largest component is the play component")
        self.rows_total = len(rows)
        self.segments = self._border_segments()

    def _border_segments(self) -> np.ndarray:
        m = self.mesh
        play = np.nonzero(m.labels == self.label)[0]
        segs, portals = [], []
        for k in range(oracle.MAX_POLY_VERTS):
            sel = play[(k < m.poly_nverts[play])]
            for kind, code in (("b", -1), ("p", -2)):
                s = sel[m.poly_nei[sel, k] == code]
                if not len(s):
                    continue
                a = m.verts[m.poly_verts[s, k]]
                b = m.verts[m.poly_verts[s, (k + 1) % m.poly_nverts[s]]]
                seg = np.stack([a, b], axis=1)
                (segs if kind == "b" else portals).append((seg, s))
        out = [seg for seg, _ in segs]
        if portals:
            # a portal edge is matched when another polygon has a portal edge through its midpoint; an unmatched one is boundary
            allp = np.concatenate([seg for seg, _ in portals])
            owner = np.concatenate([s for _, s in portals])
            mid = allp.mean(axis=1)
            d = (allp[:, 1] - allp[:, 0])[:, [0, 2]]
            l2 = np.maximum((d * d).sum(axis=1), 1e-12)
            keep = []
            for i in range(len(allp)):
                t = np.clip(((mid[i, 0] - allp[:, 0, 0]) * d[:, 0] + (mid[i, 2] - allp[:, 0, 2]) * d[:, 1]) / l2, 0, 1)
                dist = np.hypot(mid[i, 0] - (allp[:, 0, 0] + t * d[:, 0]), mid[i, 2] - (allp[:, 0, 2] + t * d[:, 1]))
                near = (dist < 0.05) & (owner != owner[i]) & (np.abs(allp[:, :, 1].mean(axis=1) - mid[i, 1]) < 0.6)
                if not near.any():
                    keep.append(i)
            if keep:
                out.append(allp[keep])
        return np.concatenate(out) if out else np.empty((0, 2, 3))

    def clearance(self, pts, dy: float = CLEARANCE_DY) -> np.ndarray:
        """Horizontal distance to the nearest boundary edge of the component whose height is within dy of the point."""
        p = np.atleast_2d(np.asarray(pts, float))
        seg = self.segments
        out = np.full(len(p), np.inf)
        if not len(seg):
            return out
        a, b = seg[:, 0], seg[:, 1]
        d = (b - a)[:, [0, 2]]
        l2 = np.maximum((d * d).sum(axis=1), 1e-12)
        ymid = 0.5 * (a[:, 1] + b[:, 1])
        slack = dy + np.abs(b[:, 1] - a[:, 1])
        for lo in range(0, len(p), 256):
            q = p[lo:lo + 256]
            t = np.clip(((q[:, None, 0] - a[None, :, 0]) * d[None, :, 0] + (q[:, None, 2] - a[None, :, 2]) * d[None, :, 1]) / l2[None, :], 0, 1)
            cx = a[None, :, 0] + t * d[None, :, 0]
            cz = a[None, :, 2] + t * d[None, :, 1]
            dist = np.hypot(q[:, None, 0] - cx, q[:, None, 2] - cz)
            dist = np.where(np.abs(ymid[None, :] - q[:, None, 1]) <= slack[None, :], dist, np.inf)
            out[lo:lo + 256] = dist.min(axis=1)
        return out

    def locate(self, pts, max_dy: float):
        """(play polygon or -1, surface y) per point; a polygon of another component counts as off the play component."""
        poly, surf = self.mesh.locate(pts, max_dy=max_dy)
        ok = poly >= 0
        ok[ok] = self.mesh.labels[poly[ok]] == self.label
        return np.where(ok, poly, -1), np.where(ok, surf, np.nan)

    def cells(self, step: float):
        pts, poly = oracle.sample_polygons(self.mesh, step)
        sel = self.mesh.labels[poly] == self.label
        return pts[sel], poly[sel]

    def snap(self, x, y, z, max_xz=SNAP_XZ, max_dy=SNAP_Y):
        """Nearest on-mesh spot within max_xz of (x, z): (x, surface_y, z, offset) or (None, reason)."""
        offs = [(0.0, 0.0)] + [(r * math.cos(k * math.pi / 4), r * math.sin(k * math.pi / 4)) for r in (0.25, max_xz) for k in range(8)]
        cand = np.array([[x + dx, y, z + dz] for dx, dz in offs])
        poly, surf = self.locate(cand, max_dy)
        for k in range(len(offs)):
            if poly[k] >= 0:
                return (float(cand[k, 0]), float(surf[k]), float(cand[k, 2]), math.hypot(*offs[k])), None
        anyp, _ = self.mesh.locate(cand, max_dy=max_dy)
        return None, ("off_play_component" if (anyp >= 0).any() else "off_mesh")


# ---------------------------------------------------------------------------------------------------------------------
# plain-raid reachability
# ---------------------------------------------------------------------------------------------------------------------
class Reach:
    """Flood fill over the lattice cells with the held gates' closed footprints removed (all cells when no gate is held)."""

    def __init__(self, play: PlayMesh, cells, cell_poly, step, held_gates, seed_points, margin=0.3):
        self.cells, self.step = np.asarray(cells, float), step
        n = len(self.cells)
        self.blocked = np.zeros(n, bool)
        self.active = bool(held_gates)
        self.reach = np.ones(n, bool)
        if not self.active or not n:
            return
        for g in held_gates:
            self.blocked |= g.distance(self.cells, swept=False) <= margin
        mesh = play.mesh
        near: dict = {}
        for a, b in mesh.edges.tolist():
            near.setdefault(a, set()).add(b)
            near.setdefault(b, set()).add(a)
        ring = {p: (near.get(p, set()) | {q for r in near.get(p, ()) for q in near.get(r, ())} | {p}) for p in set(cell_poly.tolist())}
        dsu = DSU(n)
        for a, b in lattice_neighbours(self.cells, step, 1.0):
            if self.blocked[a] or self.blocked[b]:
                continue
            if int(cell_poly[b]) in ring[int(cell_poly[a])]:
                dsu.union(a, b)
        seeds = set()
        for s in np.asarray(seed_points, float).reshape(-1, 3):
            k = self._nearest(s, 2.0)
            if k is not None and not self.blocked[k]:
                seeds.add(dsu.find(k))
        roots = np.array([dsu.find(k) for k in range(n)])
        self.reach = np.isin(roots, list(seeds)) & ~self.blocked
        self.seeds = len(seeds)

    def _nearest(self, p, max_xz):
        d = np.hypot(self.cells[:, 0] - p[0], self.cells[:, 2] - p[2])
        d = np.where(np.abs(self.cells[:, 1] - p[1]) <= 1.0, d, np.inf)
        k = int(np.argmin(d))
        return k if d[k] <= max_xz else None

    def query(self, point) -> bool:
        if not self.active:
            return True
        k = self._nearest(point, 1.5)
        return bool(k is not None and self.reach[k])


# ---------------------------------------------------------------------------------------------------------------------
# gates from the extracted package (optional)
# ---------------------------------------------------------------------------------------------------------------------
def build_gates(descriptor: dict, doors: dict | None, cset) -> tuple[list, list]:
    """Gate footprints from the package colliders (port_colliders). Returns (gates, notes)."""
    import port_colliders as pcol
    notes = []
    paths = []
    for key in ("opened_barriers", "start_gates", "held_gates"):
        for p in descriptor.get(key) or []:
            if p not in paths:
                paths.append(p)
    rows = [r for r in (doors or {}).get("poses", []) if not isinstance(r.get("switch"), dict)]
    gates = []
    for path in paths:
        kinds = [k for k, name in (("opened", "opened_barriers"), ("start", "start_gates"), ("held", "held_gates")) if path in (descriptor.get(name) or [])]
        instances = pcol.find_all_instances(cset.scenes, path)
        if not instances:
            notes.append(f"gate '{path}' resolves to no object in the package")
            gates.append(Gate(path, kinds, [], [], source="none"))
            continue
        keys = {(s, k) for s, k in instances}
        owners = [o for o in cset.owners if any((o.scene, kk) in keys for kk in cset.scenes[o.scene].path(o.obj_id))]
        closed, swept, ys = [], [], []
        for o in owners:
            tr = cset.tris[o.tri_start:o.tri_start + o.tri_count]
            if not len(tr):
                continue
            h = hull2d(tr.reshape(-1, 3)[:, [0, 2]])
            closed.append(h)
            swept.append(h)
            ys += [float(tr[..., 1].min()), float(tr[..., 1].max())]
        if not closed:
            scene, key = instances[0]
            pos = cset.scenes[scene].world(key)[:3, 3]
            notes.append(f"gate '{path}' has no collider; a 3 m disc around its root is used")
            closed = swept = [disc_hull(pos[0], pos[2], 3.0)]
            ys = [float(pos[1]) - 1.0, float(pos[1]) + 3.0]
            src = "disc"
        else:
            src = "colliders"
        extra_y = 0.0
        for row in [r for r in rows if r.get("barrier") == path]:
            ex = _open_pose_hulls(cset, instances, row, owners)
            if ex is None:
                notes.append(f"gate '{path}': door row part '{row.get('part')}' has no instance in the package")
                continue
            swept += ex[0]
            extra_y = max(extra_y, ex[1])
        gates.append(Gate(path, kinds, closed, swept, min(ys) - extra_y, max(ys) + extra_y, src, len(owners)))
    return gates, notes


def _open_pose_hulls(cset, instances, row, owners):
    """Footprints of one door row's moved node in the OPEN pose: translation -> hull(closed + translated vertices),
    rotation (> 1 degree about any axis) -> a disc around the node's pivot. Returns (hulls, dy) or None."""
    import port_colliders as pcol
    hulls, dy_max, found = [], 0.0, False
    prow = {"part": row.get("part", ""), "node": row.get("node", ""), "at": row.get("at"),
            "closed_position": (row.get("closed") or {}).get("position")}
    for scene_name, inst in instances:
        scene = cset.scenes[scene_name]
        if not pcol._door_row_applies(prow, scene.world(inst)[:3, 3]):
            continue
        parts = [inst] if not prow["part"] else pcol._parts_of_row(scene, inst, prow)
        for part in parts:
            node = part if not prow["node"] else scene.find(part, prow["node"])
            if node is None:
                continue
            found = True
            cl, op = row.get("closed") or {}, row.get("open") or {}
            parent = scene.objects[node].get("parent_id")
            pw = scene.world(parent)[:3, :3] if parent is not None else np.eye(3)
            delta = pw @ (np.array(op.get("position", cl.get("position", [0, 0, 0])), float) - np.array(cl.get("position", [0, 0, 0]), float))
            rot = np.abs(np.array(op.get("euler", [0, 0, 0]), float) - np.array(cl.get("euler", [0, 0, 0]), float)).max() > 1.0
            under = [o for o in owners if o.scene == scene_name and node in scene.path(o.obj_id)]
            pivot = scene.world(node)[:3, 3]
            for o in under:
                tr = cset.tris[o.tri_start:o.tri_start + o.tri_count]
                if not len(tr):
                    continue
                xz = tr.reshape(-1, 3)[:, [0, 2]]
                if rot:
                    rad = float(np.hypot(xz[:, 0] - pivot[0], xz[:, 1] - pivot[2]).max())
                    hulls.append(disc_hull(pivot[0], pivot[2], rad))
                else:
                    hulls.append(hull2d(np.concatenate([xz, xz + delta[[0, 2]]])))
            dy_max = max(dy_max, abs(float(delta[1])))
    return (hulls, dy_max) if found else None


def gates_from_door_rows(descriptor: dict, doors: dict | None) -> list:
    """Fallback without a package: a 3 m disc at every door row that carries an 'at' position."""
    out = []
    seen = set()
    for row in (doors or {}).get("poses", []):
        at = row.get("at")
        if at is None:
            continue
        key = (row.get("barrier"), tuple(round(v, 1) for v in at))
        if key in seen:
            continue
        seen.add(key)
        h = disc_hull(at[0], at[2], 3.0)
        kinds = [k for k, name in (("opened", "opened_barriers"), ("start", "start_gates"), ("held", "held_gates")) if row.get("barrier") in (descriptor.get(name) or [])]
        out.append(Gate(row.get("barrier", "?"), kinds, [h], [h], at[1] - 1.0, at[1] + 3.0, "door-at disc"))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# coverage
# ---------------------------------------------------------------------------------------------------------------------
def distant_counts(cells, rows, floor, metric):
    """Per cell: the number of rows strictly farther than floor, by 'xz' and '3d' (a dict of two int arrays)."""
    cells = np.asarray(cells, float).reshape(-1, 3)
    rows = np.asarray(rows, float).reshape(-1, 3)
    out = {}
    for name in ("xz", "3d"):
        cnt = np.zeros(len(cells), np.int64)
        for k in range(len(rows)):
            d = cells - rows[k]
            dist = np.hypot(d[:, 0], d[:, 2]) if name == "xz" else np.sqrt((d * d).sum(axis=1))
            cnt += (dist > floor)
        out[name] = cnt
    return out


def cover_stats(cells, cnt, mask, need, step) -> dict:
    n = int(mask.sum())
    if not n:
        return {"cells": 0}
    c = cnt[mask]
    st = {"cells": n, "area_m2": r2(n * step * step), "min": int(c.min()), "median": r2(np.median(c)), "mean": r2(c.mean()),
          "share_ge_need": r3((c >= need).mean()), "share_ge_2": r3((c >= 2).mean()), "share_zero": r3((c == 0).mean())}
    pts = np.asarray(cells, float)
    worst = mask & (cnt == c.min())
    w = pts[worst]
    st["worst_region"] = {"count_value": int(c.min()), "cells": int(worst.sum()), "bbox_xz": [r2(w[:, 0].min()), r2(w[:, 2].min()), r2(w[:, 0].max()), r2(w[:, 2].max())],
                          "centroid": [r2(w[:, 0].mean()), r2(w[:, 1].mean()), r2(w[:, 2].mean())]}
    groups = clusters_of(pts, mask & (cnt < need), step)[:5]
    st["deficit_clusters"] = [{"cells": len(g), "bbox_xz": [r2(pts[g][:, 0].min()), r2(pts[g][:, 2].min()), r2(pts[g][:, 0].max()), r2(pts[g][:, 2].max())],
                               "mean_count": r2(cnt[g].mean())} for g in groups]
    return st


def coverage_report(cells, rows, inside_pocket, params) -> dict:
    cnts = distant_counts(cells, rows, params["floor"], params["metric"])
    rep = {}
    for name, cnt in cnts.items():
        rep[name] = {"outside_pockets": cover_stats(cells, cnt, ~inside_pocket, params["need"], params["cell"]),
                     "with_pockets": cover_stats(cells, cnt, np.ones(len(cells), bool), params["need"], params["cell"])}
    return rep


# ---------------------------------------------------------------------------------------------------------------------
# candidates
# ---------------------------------------------------------------------------------------------------------------------
@dataclass
class Context:
    play: PlayMesh
    reach: Reach
    pockets: list
    gates: list
    player_rows: np.ndarray
    bot_rows: np.ndarray
    bounds: list | None
    params: dict
    probe: object = None
    probe_checks: int = 0
    probe_failures: int = 0


def dist3(a, b) -> np.ndarray:
    d = np.asarray(a, float).reshape(-1, 3) - np.asarray(b, float).reshape(1, 3)
    return np.sqrt((d * d).sum(axis=1))


def evaluate_candidate(ctx: Context, c: dict, on_mesh: bool = False):
    """Run the acceptance rules. Returns (record or None, reason). The record is the candidate with its final position and metrics."""
    P = ctx.params
    x, y, z = c["position"]
    snap_off = 0.0
    if on_mesh:
        pos = (x, y, z)
    else:
        s, why = ctx.play.snap(x, y, z)
        if s is None:
            return None, why
        pos, snap_off = (s[0], s[1], s[2]), s[3]
    if ctx.bounds:
        x0, x1, y0, y1, z0, z1 = ctx.bounds
        if not (x0 <= pos[0] <= x1 and z0 <= pos[2] <= z1 and y0 <= pos[1] <= y1):
            return None, "outside_bake_bounds"
    if not ctx.reach.query(pos):
        return None, "not_plain_raid_reachable"
    clr = float(ctx.play.clearance([pos])[0])
    moved = 0.0
    if clr < P["clearance"]:
        best = None
        radii = [r for r in (0.5, 1.0, 1.5, 2.0, 2.5, 3.0) if r <= P["nudge"] + 1e-9] or ([P["nudge"]] if P["nudge"] > 0 else [])
        if radii:
            offs = [(r * math.cos(k * math.pi / 8), r * math.sin(k * math.pi / 8), r) for r in radii for k in range(16)]
            pts = np.array([[pos[0] + dx, pos[1], pos[2] + dz] for dx, dz, _ in offs])
            poly, surf = ctx.play.locate(pts, 0.6)
            ok = np.nonzero(poly >= 0)[0]
            if len(ok):
                pts2 = pts[ok].copy()
                pts2[:, 1] = surf[ok]
                cl2 = ctx.play.clearance(pts2)
                for k, i in enumerate(ok):
                    if cl2[k] >= P["clearance"] and (best is None or offs[i][2] < best[0] - 1e-9):
                        best = (offs[i][2], pts2[k], float(cl2[k]))
                        break
                if best is None and len(ok):
                    clr_best = float(cl2.max())
                    return None, f"clearance_below_{P['clearance']:g}(best {clr_best:.2f} within {P['nudge']:g} m, own {clr:.2f})"
        if best is None:
            return None, f"clearance_below_{P['clearance']:g}(own {clr:.2f}, no nudge)"
        moved, pos, clr = best[0], (float(best[1][0]), float(best[1][1]), float(best[1][2])), best[2]
        if not ctx.reach.query(pos):
            return None, "not_plain_raid_reachable"
    p3 = np.array(pos)
    for pk in ctx.pockets:
        if pk.xz_outside_distance([p3])[0] < P["box_margin"] and pk.y_range()[0] - 0.5 <= pos[1] <= pk.y_range()[1] + 0.5:
            return None, f"inside_pocket_margin({pk.name})"
    gd = float(min_gate_distance(ctx.gates, [p3])[0]) if ctx.gates else math.inf
    if gd < P["gate_margin"]:
        return None, "gate_margin"
    if len(ctx.player_rows) and float(dist3(ctx.player_rows, p3).min()) < P["player_margin"]:
        return None, "player_margin"
    if len(ctx.bot_rows) and float(dist3(ctx.bot_rows, p3).min()) < P["spacing"]:
        return None, "spacing_existing"
    rec = dict(c)
    rec["position"] = [r2(pos[0]), r2(pos[1]), r2(pos[2])]
    rec["metrics"] = {"snap_offset": r2(snap_off), "nudged_m": r2(moved), "clearance": r2(clr), "gate_distance": None if math.isinf(gd) else r2(gd),
                      "nearest_player_row": r2(dist3(ctx.player_rows, p3).min()) if len(ctx.player_rows) else None,
                      "nearest_bot_row": r2(dist3(ctx.bot_rows, p3).min()) if len(ctx.bot_rows) else None}
    return rec, None


def standing_room(ctx: Context, rec: dict):
    """The spawn_placement_probe verdict for an accepted record (None when no probe): (ok, reason)."""
    if ctx.probe is None:
        return True, None
    v = ctx.probe.check(tuple(rec["position"]))
    ctx.probe_checks += 1
    if v.valid:
        return True, None
    ctx.probe_failures += 1
    if not v.ground_ok:
        return False, "no_standing_room(no ground within 1 m)"
    if v.headroom is not None and v.headroom < 1.8:
        return False, f"no_standing_room(headroom {v.headroom:.2f} m)"
    return False, f"no_standing_room(capsule blocked by {len(v.blocked)} ray(s), e.g. {v.blocked[0][2]})"


def gather_candidates(ai: dict | None, sources: dict | None, bot_rows, params) -> tuple[list, list]:
    """Native candidates in a fixed order; AI points already used by an existing row are returned as pre-rejected."""
    cands, pre = [], []
    used = np.asarray(bot_rows, float).reshape(-1, 3)
    for src, key in (("ai_core", "core_points"), ("ai_born", "born_points")):
        for i, p in enumerate((ai or {}).get(key) or []):
            c = {"source": src, "scene": "ai", "path": key, "name": p.get("name", str(p.get("id", i))), "position": [float(v) for v in p["position"]], "yaw": None, "native": True}
            c["source_position"] = list(c["position"])
            if len(used) and float(np.hypot(used[:, 0] - c["position"][0], used[:, 2] - c["position"][2]).min()) <= params["ai_used_radius"]:
                pre.append((c, "already_used_by_an_existing_row"))
            else:
                cands.append(c)
    for i, p in enumerate((sources or {}).get("points") or []):
        src = p.get("source", "other")
        if src not in NATIVE_SOURCES:
            raise SystemExit(f"--sources: point {i} has unknown source {src!r}; expected one of {', '.join(NATIVE_SOURCES)}")
        cands.append({"source": src, "scene": p.get("scene", ""), "path": p.get("path", ""), "name": p.get("name", ""), "position": [float(v) for v in p["position"]],
                      "yaw": p.get("yaw"), "native": True, "source_position": [float(v) for v in p["position"]]})
    cands.sort(key=lambda c: (NATIVE_SOURCES.index(c["source"]), c["scene"], c["path"], c["name"], c["position"][0], c["position"][2]))
    return cands, pre


def fill_candidates(ctx: Context, cells, cell_clear, params):
    """Fill points: every k-th lattice cell (k = fill_step / cell) with clearance >= fill_clearance."""
    k = max(1, int(round(params["fill_step"] / params["cell"])))
    ii, jj = lattice_index(cells, params["cell"])
    sel = np.nonzero((ii % k == 0) & (jj % k == 0) & (cell_clear >= params["fill_clearance"]))[0]
    out = [{"source": "fill", "scene": "mesh", "path": "", "name": f"cell({int(ii[s])},{int(jj[s])})", "position": [float(v) for v in cells[s]], "yaw": None, "native": False,
            "source_position": [float(v) for v in cells[s]]} for s in sel]
    out.sort(key=lambda c: (c["position"][0], c["position"][2], c["position"][1]))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# greedy selection
# ---------------------------------------------------------------------------------------------------------------------
def pick_best(idx, g1, g2, gd, mind, native):
    """The tie rules. Order: most cells reaching >= need (g1), then most cells reaching >= 2 (g2), then the larger deficit reduction (gd,
    an addition of this tool), then the larger minimum distance to all rows (mind), then the lower index. A native candidate wins over a
    fill point when its g1 is within 5 % of the best. Returns (index, note, g1 of the best)."""
    idx = np.asarray(idx)
    order = idx[np.lexsort((idx, -mind[idx], -gd[idx], -g2[idx], -g1[idx]))]
    best = int(order[0])
    top = int(g1[best])
    if not native[best]:
        nat = [int(i) for i in order if native[i] and g1[i] >= (1.0 - NATIVE_GAIN_SLACK) * g1[best]]
        if nat:
            return nat[0], "native within 5 % of the best gain", top
    return best, "best", top


def greedy_select(target_cells, base_rows, cands, ctx: Context, params, accepted_pos=None):
    """Select rows. cands: list of accepted records (with 'position' and 'native'). Returns (chosen, rejected (rec, reason), steps)."""
    P = params
    need = P["need"]
    cells = np.asarray(target_cells, float).reshape(-1, 3)
    base = np.asarray(base_rows, float).reshape(-1, 3)
    cnt = distant_counts(cells, base, P["floor"], P["metric"])[P["metric"]] if len(cells) else np.zeros(0, np.int64)
    C = len(cands)
    if not C or not len(cells):
        return [], [], []
    cpos = np.array([c["position"] for c in cands], float)
    F = np.zeros((C, len(cells)), bool)
    for i in range(C):
        d = cells - cpos[i]
        dist = np.hypot(d[:, 0], d[:, 2]) if P["metric"] == "xz" else np.sqrt((d * d).sum(axis=1))
        F[i] = dist > P["floor"]
    mind = np.full(C, np.inf)
    for r in base:
        mind = np.minimum(mind, np.sqrt(((cpos - r) ** 2).sum(axis=1)))
    native = np.array([bool(c.get("native")) for c in cands])
    alive = np.ones(C, bool)
    total = len(base)
    chosen, rejected, steps = [], [], []
    while alive.any():
        if total >= P["max_rows"]:
            break
        short = (cnt == need - 1)
        g1 = (F & short[None, :]).sum(axis=1)
        g2 = (F & (cnt == 1)[None, :]).sum(axis=1)
        gd = (F & (cnt < need)[None, :]).sum(axis=1)
        best, note, g1_best = pick_best(np.nonzero(alive)[0], g1, g2, gd, mind, native)
        if total >= P["min_rows"] and g1_best == 0:
            break
        ok, why = standing_room(ctx, cands[best])
        if not ok:
            alive[best] = False
            rejected.append((cands[best], why))
            continue
        chosen.append(cands[best])
        steps.append({"index": len(chosen), "gain_ge_need": int(g1[best]), "gain_ge_2": int(g2[best]), "deficit_reduction": int(gd[best]),
                      "min_distance_to_rows": r2(mind[best]), "pick": note})
        cnt = cnt + F[best]
        total += 1
        alive[best] = False
        d = np.sqrt(((cpos - cpos[best]) ** 2).sum(axis=1))
        mind = np.minimum(mind, d)
        kill = alive & (d < P["spacing"])
        for i in np.nonzero(kill)[0]:
            rejected.append((cands[int(i)], f"spacing_selected(<{P['spacing']:g} m from {chosen[-1].get('id_hint', chosen[-1].get('name'))})"))
        alive &= ~kill
    return chosen, rejected, steps


# ---------------------------------------------------------------------------------------------------------------------
# exits
# ---------------------------------------------------------------------------------------------------------------------
def tri_box_overlap(tris, center, half) -> np.ndarray:
    """Separating-axis test (Akenine-Moeller) of triangles (N, 3, 3) against an axis-aligned box; boolean per triangle."""
    t = np.asarray(tris, float) - np.asarray(center, float)
    h = np.asarray(half, float)
    n = len(t)
    if not n:
        return np.zeros(0, bool)
    hit = np.ones(n, bool)
    for ax in range(3):                                   # box face normals
        hit &= (t[:, :, ax].min(axis=1) <= h[ax]) & (t[:, :, ax].max(axis=1) >= -h[ax])
    e = np.stack([t[:, 1] - t[:, 0], t[:, 2] - t[:, 1], t[:, 0] - t[:, 2]], axis=1)
    nrm = np.cross(e[:, 0], e[:, 1])
    off = (nrm * t[:, 0]).sum(axis=1)
    rad = (np.abs(nrm) * h).sum(axis=1)
    hit &= np.abs(off) <= rad
    for i in range(3):
        for ax in range(3):
            unit = np.zeros(3)
            unit[ax] = 1.0
            axis = np.cross(e[:, i], unit)
            p = np.einsum("nvk,nk->nv", t, axis)
            r = (np.abs(axis) * h).sum(axis=1)
            hit &= (p.min(axis=1) <= r) & (p.max(axis=1) >= -r)
    return hit


class SolidIndex:
    """Runtime-solid triangles of the package (spawn-bound barriers, spawn markers and the moved parts of opened barriers excluded)."""

    def __init__(self, tris, tri_owner, owner_paths):
        self.tris, self.tri_owner, self.owner_paths = tris, tri_owner, owner_paths
        self.mn = tris.min(axis=1) if len(tris) else np.empty((0, 3))
        self.mx = tris.max(axis=1) if len(tris) else np.empty((0, 3))

    def solids_in_box(self, centre, size) -> list:
        c, h = np.asarray(centre, float), np.asarray(size, float) / 2.0
        sel = np.nonzero(np.all((self.mn <= c + h) & (self.mx >= c - h), axis=1))[0]
        if not len(sel):
            return []
        ok = sel[tri_box_overlap(self.tris[sel], c, h)]
        return sorted({self.owner_paths[int(self.tri_owner[i])] for i in ok})


def box_corners(x, z, edge=EXIT_BOX):
    h = edge / 2.0
    return [(x - h, z - h), (x + h, z - h), (x + h, z + h), (x - h, z + h)]


def exit_metrics(ctx: Context, pocket: Pocket, pos, markers, solids: SolidIndex | None, floor_y=None, pocket_gates=()):
    """Metrics of one 4 x 4 m exit box centred at pos = (x, y, z) (y = floor)."""
    x, y, z = pos
    p3 = np.array([x, y, z])
    corners = box_corners(x, z)
    corner_pts = np.array([[cx, y, cz] for cx, cz in corners])
    inside = bool(pocket.contains(corner_pts, with_y=False).all() and pocket.contains([p3])[0])
    poly_c, _ = ctx.play.locate([p3], 1.2)
    poly_k, _ = ctx.play.locate(corner_pts, 1.2)
    on_mesh = 0
    for k in range(4):
        if poly_k[k] >= 0 or float(ctx.play.clearance([corner_pts[k]])[0]) <= 0.5:
            on_mesh += 1
    m = {}
    m["position"] = [r2(x), r2(y), r2(z)]
    m["inside_pocket"] = inside
    m["centre_on_mesh"] = bool(poly_c[0] >= 0)
    m["corners_on_mesh"] = on_mesh
    mk = np.asarray(markers, float).reshape(-1, 3)
    m["nearest_marker"] = r2(np.hypot(mk[:, 0] - x, mk[:, 2] - z).min()) if len(mk) else None
    cen = mk[:, [0, 2]].mean(axis=0) if len(mk) else pocket.center[[0, 2]]
    m["pocket_marker_centroid_dist"] = r2(math.hypot(x - cen[0], z - cen[1]))
    m["pocket_centroid_dist"] = r2(math.hypot(x - pocket.center[0], z - pocket.center[2]))
    pr = ctx.player_rows
    if len(pr):
        d = np.hypot(pr[:, 0] - x, pr[:, 2] - z)
        m["nearest_player_row"] = r2(d.min())
        ex = np.maximum(np.abs(pr[:, 0] - x) - EXIT_BOX / 2, 0.0)
        ez = np.maximum(np.abs(pr[:, 2] - z) - EXIT_BOX / 2, 0.0)
        m["nearest_player_row_box_edge"] = r2(np.hypot(ex, ez).min())
    br = ctx.bot_rows
    m["nearest_bot_row"] = r2(np.hypot(br[:, 0] - x, br[:, 2] - z).min()) if len(br) else None
    if pocket_gates:
        gb = min_gate_box_distance(pocket_gates, x, y, z, EXIT_BOX / 2)
        m["gate_distance_box_edge"] = None if math.isinf(gb) else r2(gb)
        gc = min_gate_distance(pocket_gates, [p3])[0]
        m["gate_distance_centre"] = None if np.isinf(gc) else r2(gc)
    else:
        m["gate_distance_box_edge"] = m["gate_distance_centre"] = None
    m["border_clearance"] = r2(ctx.play.clearance([p3])[0])
    m["plain_raid_reachable"] = bool(ctx.reach.query(p3))
    if solids is not None:
        found = solids.solids_in_box([x, y + (EXIT_TOP - EXIT_FLOOR_BELOW) / 2 + EXIT_SOLID_FROM / 2, z],
                                     [EXIT_BOX, EXIT_TOP - EXIT_SOLID_FROM, EXIT_BOX])
        m["solids_in_box"] = found[:4]
        m["solid_free"] = not found
    else:
        m["solid_free"] = None
    gate_ok = m["gate_distance_box_edge"] is None or m["gate_distance_box_edge"] >= ctx.params["exit_gate_margin"]
    m["valid"] = bool(inside and m["centre_on_mesh"] and on_mesh >= ctx.params["min_corners"] and (m["plain_raid_reachable"] or not ctx.params["exit_require_reach"]) and m["solid_free"] is not False
                      and gate_ok and m["border_clearance"] >= ctx.params["exit_clearance"])
    return m


def pocket_gates_of(pocket: Pocket, gates, link) -> list:
    out = []
    for g in gates:
        pts = [pt for h in g.closed for pt in np.asarray(h).reshape(-1, 2)]
        if not pts:
            continue
        arr = np.array([[px, pocket.center[1], pz] for px, pz in pts])
        if float(pocket.xz_outside_distance(arr).min()) <= link:
            out.append(g)
    return out


def exit_options(ctx: Context, pocket: Pocket, cells, solids, params):
    markers = pocket.markers
    mk_cen = markers[:, [0, 2]].mean(axis=0) if len(markers) else pocket.center[[0, 2]]
    pgates = pocket_gates_of(pocket, ctx.gates, params["gate_link"])
    step = params["exit_step"]
    ii, jj = lattice_index(cells, params["cell"])
    k = max(1, int(round(step / params["cell"])))
    cand = [i for i in range(len(cells)) if ii[i] % k == 0 and jj[i] % k == 0]
    rows = []
    for i in cand:
        c = cells[i]
        corner_pts = np.array([[cx, c[1], cz] for cx, cz in box_corners(c[0], c[2])])
        if not (pocket.contains(corner_pts, with_y=False).all() and pocket.contains([c])[0]):
            continue
        m = exit_metrics(ctx, pocket, tuple(c), markers, solids, pocket_gates=pgates)
        rows.append(m)
    rows.sort(key=lambda m: (m["position"][0], m["position"][2], m["position"][1]))
    valid = [m for m in rows if m["valid"]]
    out = {"pocket": pocket.name, "side": pocket.side, "markers": int(len(markers)), "marker_centroid": [r2(mk_cen[0]), r2(mk_cen[1])],
           "gates": [g.path for g in pgates], "box_cells_inside_pocket": len(rows), "valid_cells": len(valid),
           "plain_raid_reachable": bool(valid) and all(m["plain_raid_reachable"] for m in valid),
           "valid_cells_plain_raid_reachable": sum(1 for m in valid if m["plain_raid_reachable"]), "options": {}}

    def pick(name, key):
        if not valid:
            out["options"][name] = None
            return
        best = min(valid, key=key)
        out["options"][name] = best
    pick("far", lambda m: (-min(m["nearest_marker"] if m["nearest_marker"] is not None else 1e9, m.get("nearest_player_row", 1e9)),
                           -m["border_clearance"], m["position"][0], m["position"][2]))
    pick("centre", lambda m: (m["pocket_marker_centroid_dist"], -m["border_clearance"], m["position"][0], m["position"][2]))
    if pgates:
        pick("gate", lambda m: (m["gate_distance_centre"] if m["gate_distance_centre"] is not None else 1e9, -m["border_clearance"], m["position"][0], m["position"][2]))
    else:
        out["options"]["gate"] = None
        out["gate_note"] = "no gate footprint within --gate-link of this pocket; the 'gate' option is unavailable"
    return out


# ---------------------------------------------------------------------------------------------------------------------
# analysis driver
# ---------------------------------------------------------------------------------------------------------------------
def select_pockets(modes: dict) -> list:
    bounds = modes.get("spawn_bounds") or []
    chosen = [b for b in bounds if b.get("initial") is True] or [b for b in bounds if b.get("active", True)]
    pockets = [Pocket.from_bound(b) for b in chosen]
    markers = modes.get("spawn_markers") or {}
    allm = [(side, m) for side, lst in markers.items() for m in lst]
    for pk in pockets:
        inside = [m["position"] for _, m in allm if pk.contains([m["position"]], margin=-0.5)[0]]
        if not inside:
            inside = [m["position"] for side, m in allm if side == pk.side]
        pk.markers = np.array(inside, float).reshape(-1, 3)
    return pockets


def analyse(inputs: dict, mesh, cset=None, params: dict | None = None, gates: list | None = None, stem: str = "?") -> dict:
    """The whole analysis on loaded sidecars (`inputs`: descriptor, bots, exits, modes, ai, doors, sources) and a NavMesh."""
    P = dict(DEFAULTS)
    P.update(params or {})
    desc, bots, modes = inputs.get("descriptor") or {}, inputs["bots"], inputs["modes"]
    warnings: list = []
    bot_rows = np.array([r["position"] for r in bots.get("bot_spawns", []) if {"Bot", "BotPmc"} & set(r.get("categories", ["Bot"]))], float).reshape(-1, 3)
    player_rows = np.array([r["position"] for r in bots.get("player_spawns", [])], float).reshape(-1, 3)
    play = PlayMesh(mesh, bot_rows)
    warnings += play.warnings
    cells, cpoly = play.cells(P["cell"])
    if not len(cells):
        raise SystemExit("the play component has no cells on the 1 m lattice; check --navmesh")
    cell_clear = play.clearance(cells)
    pockets = select_pockets(modes)
    if len(pockets) != 2:
        warnings.append(f"{len(pockets)} initial pocket(s) found (2 expected)")
    inside_pocket = np.zeros(len(cells), bool)
    for pk in pockets:
        inside_pocket |= pk.contains(cells)
    gate_notes: list = []
    if gates is None:
        if cset is not None:
            gates, gate_notes = build_gates(desc, inputs.get("doors"), cset)
        else:
            gates = gates_from_door_rows(desc, inputs.get("doors"))
            warnings.append("no --package: gate footprints are only known for door rows that carry an 'at' position (3 m discs); the gate margin and the held-gate "
                            "reachability are mostly skipped" if not gates else "no --package: gate footprints are 3 m discs at the door rows' 'at' positions")
    held = [g for g in gates if "held" in g.kinds]
    if (desc.get("held_gates") or []) and not held:
        warnings.append("held_gates are listed but no footprint could be built; plain-raid reachability assumes everything is open")
    reach = Reach(play, cells, cpoly, P["cell"], held, bot_rows)
    solids = probe = None
    if cset is not None:
        import spawn_placement_probe as spp
        keep = np.array([not (spp.runtime_nonsolid(o.names) or o.barrier_blocked) for o in cset.owners], bool)
        mask = keep[cset.tri_owner]
        solids = SolidIndex(cset.tris[mask], cset.tri_owner[mask], [o.path for o in cset.owners])
        if P["probe"]:
            probe = spp.PlacementProbe(cset.tris[mask], cset.tri_owner[mask], [o.path for o in cset.owners])
    ctx = Context(play, reach, pockets, gates, player_rows, bot_rows, desc.get("bake_bounds"), P, probe)
    if cset is None:
        warnings.append("no --package: no standing-room check and no solid check for the exit boxes")

    before = coverage_report(cells, bot_rows, inside_pocket, P)
    # --- candidates -------------------------------------------------------------------------------------------------
    native, pre = gather_candidates(inputs.get("ai"), inputs.get("sources"), bot_rows, P)
    rejected, accepted = [(c, why) for c, why in pre], []
    for c in native:
        rec, why = evaluate_candidate(ctx, c)
        if rec is None:
            rejected.append((c, why))
        else:
            accepted.append(rec)
    fills = fill_candidates(ctx, cells, cell_clear, P)
    thin = 1
    while len(fills) > P["max_candidates"]:
        thin += 1
        fills = fills[::2]
    if thin > 1:
        warnings.append(f"fill candidates thinned by {2 ** (thin - 1)} to stay under --max-candidates {P['max_candidates']}")
    fill_reject: Counter = Counter()
    fill_rows = []
    for c in fills:
        rec, why = evaluate_candidate(ctx, c, on_mesh=True)
        if rec is None:
            key = why.split("(")[0]
            fill_reject[key] += 1
            fill_rows.append([r2(c["position"][0]), r2(c["position"][1]), r2(c["position"][2]), why])
        else:
            accepted.append(rec)
    for i, rec in enumerate(accepted):
        rec["id_hint"] = f"{rec['source']}:{rec['name']}"
    target_mask = np.ones(len(cells), bool) if P["include_pockets"] else ~inside_pocket
    chosen, rej2, steps = greedy_select(cells[target_mask], bot_rows, accepted, ctx, P)
    rejected += rej2
    # --- ids --------------------------------------------------------------------------------------------------------
    key = desc.get("key") or f"arena_{stem}"
    taken: dict = {}
    for r in bots.get("bot_spawns", []):
        rid = r.get("id", "")
        for tag in ("core", "born", "fill", "lasthero", "shootout", "stage", "other"):
            pre_ = f"{key}-bot-{tag}-"
            if rid.startswith(pre_) and rid[len(pre_):].isdigit():
                taken[tag] = max(taken.get(tag, 0), int(rid[len(pre_):]))
    rows_out = []
    for rec, step in zip(chosen, steps):
        tag = {"ai_core": "core", "ai_born": "born"}.get(rec["source"], rec["source"])
        taken[tag] = taken.get(tag, 0) + 1
        rows_out.append({"row": {"id": f"{key}-bot-{tag}-{taken[tag]:02d}", "position": rec["position"], "radius": 25, "categories": ["Bot", "BotPmc"]},
                         "provenance": {"source": rec["source"], "scene": rec["scene"], "path": rec["path"], "name": rec["name"], "yaw": rec.get("yaw"),
                                        "source_position": [r2(v) for v in rec["source_position"]]},
                         "metrics": rec["metrics"], "selection": step})
    new_rows = np.array([r["row"]["position"] for r in rows_out], float).reshape(-1, 3)
    after_rows = np.concatenate([bot_rows, new_rows]) if len(new_rows) else bot_rows
    after = coverage_report(cells, after_rows, inside_pocket, P)
    # --- exits ------------------------------------------------------------------------------------------------------
    exits = {"pockets": [exit_options(ctx, pk, cells, solids, P) for pk in pockets], "current": []}
    for e in inputs.get("exits", {}).get("exits", []):
        pos = tuple(e["position"])
        if not pockets:
            break
        pk = min(pockets, key=lambda q: math.hypot(pos[0] - q.center[0], pos[2] - q.center[2]))
        m = exit_metrics(ctx, pk, pos, pk.markers, solids, pocket_gates=pocket_gates_of(pk, gates, P["gate_link"]))
        m["name"] = e.get("name")
        m["nearest_pocket"] = pk.name
        m["pocket_edge_distance_xz"] = r2(pk.xz_outside_distance([pos])[0])
        exits["current"].append(m)
    names = [e for e in inputs.get("exits", {}).get("exits", [])]
    for blk in exits["pockets"]:
        pk = next(q for q in pockets if q.name == blk["pocket"])
        if names:
            near = min(names, key=lambda e: math.hypot(e["position"][0] - pk.center[0], e["position"][2] - pk.center[2]))
            blk["template_exit"] = {k: near[k] for k in ("name", "box_size", "rotation", "exfiltration_time", "chance", "type") if k in near}
    rej_out = [{"source": c["source"], "name": c["name"], "scene": c["scene"], "path": c["path"], "position": [r2(v) for v in c["position"]], "reason": why}
               for c, why in rejected if c.get("source") != "fill"]
    rej_out.sort(key=lambda r: (NATIVE_SOURCES.index(r["source"]), r["scene"], r["path"], r["name"], r["position"][0], r["position"][2]))
    fill_spacing = [[r2(c["position"][0]), r2(c["position"][1]), r2(c["position"][2]), why] for c, why in rejected if c.get("source") == "fill"]
    fill_rows += fill_spacing
    for _c, why in rejected:
        if _c.get("source") == "fill":
            fill_reject[why.split("(")[0]] += 1
    fill_rows.sort(key=lambda r: (r[0], r[2], r[1], r[3]))
    report = {
        "tool": "spawn_coverage", "stem": stem, "key": key, "parameters": {k: P[k] for k in sorted(P)}, "warnings": warnings,
        "mesh": {"polygons": int(mesh.poly_count), "play_component": int(play.label), "bot_rows_on_component": f"{play.rows_on_component}/{play.rows_total}",
                 "boundary_segments": int(len(play.segments)), "cells": int(len(cells)), "cells_in_pockets": int(inside_pocket.sum())},
        "pockets": [{"name": p.name, "side": p.side, "center": [r2(v) for v in p.center], "size": [r2(v) for v in p.size], "markers": int(len(p.markers))} for p in pockets],
        "gates": [{"path": g.path, "kinds": g.kinds, "source": g.source, "colliders": g.colliders, "closed_hulls": len(g.closed), "swept_hulls": len(g.swept)} for g in gates],
        "gate_notes": gate_notes,
        "plain_raid": {"held_gates": [g.path for g in held], "unreachable_cells": int((~reach.reach).sum()) if reach.active else 0,
                       "reachable_cells": int(reach.reach.sum()), "cells": int(len(cells))},
        "existing_rows": int(len(bot_rows)), "player_rows": int(len(player_rows)),
        "coverage_before": before, "coverage_after": after,
        "selected_rows": rows_out, "total_rows_after": int(len(after_rows)),
        "candidates": {"native": len(native) + len(pre), "native_accepted": sum(1 for r in accepted if r["native"]), "fill": len(fills),
                       "fill_accepted": sum(1 for r in accepted if not r["native"]), "rejected_fill_by_reason": dict(sorted(fill_reject.items())),
                       "standing_room_checks": ctx.probe_checks, "standing_room_failures": ctx.probe_failures},
        "rejected": rej_out, "rejected_fill": fill_rows,
        "exits": exits,
        "approximations": ["NavMesh = the port's built bundle; border clearance counts only boundary edges of the play component",
                           "gate footprints are per-collider XZ convex hulls (over-approximate concave parts); the open pose is a translated hull or a pivot disc",
                           "standing room is an upper bound (spawn_placement_probe's ray model)", "reachability uses a 1 m cell flood fill, only when held gates exist",
                           "'ge_2' gain counts cells that move from 1 to 2 distant rows; the deficit tie key is an addition of this tool"],
    }
    report["_internal"] = {"cells": cells, "inside_pocket": inside_pocket, "after_rows": after_rows, "bot_rows": bot_rows, "chosen": new_rows, "pockets": pockets,
                           "player_rows": player_rows, "cnt": distant_counts(cells, after_rows, P["floor"], P["metric"])[P["metric"]]}
    return report


# ---------------------------------------------------------------------------------------------------------------------
# I/O
# ---------------------------------------------------------------------------------------------------------------------
def default_navmesh(stem: str) -> Path:
    sub = "full" if stem == "equator" else stem
    return REPO / "work" / "build" / sub / f"arena_{stem}_navmesh.bundle"


def load_inputs(stem: str, maps_dir: Path, sources: Path | None) -> dict:
    def rd(name, required):
        p = maps_dir / name
        if not p.is_file():
            if required:
                raise SystemExit(f"missing sidecar {p}")
            return None
        return json.loads(p.read_text(encoding="utf-8-sig"))
    out = {"descriptor": rd(f"{stem}.json", True), "bots": rd(f"{stem}.bots.json", True), "exits": rd(f"{stem}.exits.json", False) or {"exits": []},
           "modes": rd(f"{stem}.modes.json", True), "ai": rd(f"{stem}.ai.json", False), "doors": rd(f"{stem}.doors.json", False)}
    out["sources"] = json.loads(Path(sources).read_text(encoding="utf-8-sig")) if sources else None
    return out


def _json_default(o):
    if isinstance(o, (np.integer,)):
        return int(o)
    if isinstance(o, (np.floating,)):
        return float(o)
    if isinstance(o, np.bool_):
        return bool(o)
    if isinstance(o, np.ndarray):
        return o.tolist()
    raise TypeError(type(o))


def write_png(path, report, scale=None):
    """A top-down picture (pure numpy + zlib): cells coloured by distant-row count after the selection (red 0, orange < 2, yellow < need,
    green >= need), pockets in blue, rows white (existing) / cyan (selected), exit options as magenta squares, Player rows blue dots."""
    it = report["_internal"]
    cells = it["cells"]
    need = report["parameters"]["need"]
    pad = 6.0
    x0, x1 = cells[:, 0].min() - pad, cells[:, 0].max() + pad
    z0, z1 = cells[:, 2].min() - pad, cells[:, 2].max() + pad
    scale = scale or max(1, min(8, int(2400 / max(x1 - x0, z1 - z0))))
    w, h = int((x1 - x0) * scale) + 1, int((z1 - z0) * scale) + 1
    img = np.full((h, w, 3), 24, np.uint8)

    def px(x, z):
        return int((x - x0) * scale), int((z1 - z) * scale)

    def dot(x, z, rad, col):
        cx, cy = px(x, z)
        img[max(cy - rad, 0):cy + rad + 1, max(cx - rad, 0):cx + rad + 1] = col
    cnt = it["cnt"]
    half = max(1, int(scale * report["parameters"]["cell"] / 2))
    for p, c in zip(cells, cnt):
        col = (200, 40, 40) if c == 0 else (230, 140, 30) if c < 2 else (220, 210, 60) if c < need else (50, 150, 70)
        dot(p[0], p[2], half, col)
    for pk in it["pockets"]:
        corners = [(-pk.size[0] / 2, -pk.size[2] / 2), (pk.size[0] / 2, -pk.size[2] / 2), (pk.size[0] / 2, pk.size[2] / 2), (-pk.size[0] / 2, pk.size[2] / 2)]
        wc = [pk.center + pk.rot @ np.array([cx, 0, cz]) for cx, cz in corners]
        for a, b in zip(wc, wc[1:] + wc[:1]):
            for t in np.linspace(0, 1, int(max(abs(b[0] - a[0]), abs(b[2] - a[2])) * scale) + 2):
                dot(a[0] + t * (b[0] - a[0]), a[2] + t * (b[2] - a[2]), 0, (60, 120, 255))
    for r in it["bot_rows"]:
        dot(r[0], r[2], 2, (255, 255, 255))
    for r in it["chosen"]:
        dot(r[0], r[2], 3, (0, 255, 255))
    for r in it["player_rows"]:
        dot(r[0], r[2], 2, (90, 140, 255))
    for blk in report["exits"]["pockets"]:
        for opt in blk["options"].values():
            if opt:
                cx, cy = px(opt["position"][0], opt["position"][2])
                e = int(EXIT_BOX / 2 * scale)
                img[max(cy - e, 0):cy - e + 2, max(cx - e, 0):cx + e] = (255, 0, 255)
                img[cy + e - 1:cy + e + 1, max(cx - e, 0):cx + e] = (255, 0, 255)
                img[max(cy - e, 0):cy + e, max(cx - e, 0):max(cx - e, 0) + 2] = (255, 0, 255)
                img[max(cy - e, 0):cy + e, cx + e - 1:cx + e + 1] = (255, 0, 255)
    raw = b"".join(b"\x00" + img[y].tobytes() for y in range(h))

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
    Path(path).write_bytes(png)


def format_text(rep: dict) -> str:
    L = [f"spawn_coverage {rep['stem']} ({rep['key']})  metric={rep['parameters']['metric']} floor={rep['parameters']['floor']} need={rep['parameters']['need']}"]
    m = rep["mesh"]
    L.append(f"mesh: {m['polygons']} polygons, play component {m['play_component']} ({m['bot_rows_on_component']} Bot rows on it), {m['cells']} cells ({m['cells_in_pockets']} in pockets)")
    L.append(f"plain raid: held gates {rep['plain_raid']['held_gates']}, unreachable cells {rep['plain_raid']['unreachable_cells']}")
    for w in rep["warnings"]:
        L.append(f"WARNING {w}")
    for label, key in (("before", "coverage_before"), ("after ", "coverage_after")):
        for metric in ("xz", "3d"):
            for scope in ("outside_pockets", "with_pockets"):
                s = rep[key][metric][scope]
                if s.get("cells"):
                    L.append(f"{label} {metric:>2} {scope:<15} cells {s['cells']:>5} min {s['min']} median {s['median']} >=need {s['share_ge_need']:.1%} >=2 {s['share_ge_2']:.1%} zero {s['share_zero']:.1%}"
                             f"  worst {s['worst_region']['cells']} cells bbox {s['worst_region']['bbox_xz']}")
    L.append(f"rows: {rep['existing_rows']} existing + {len(rep['selected_rows'])} selected = {rep['total_rows_after']}")
    for r in rep["selected_rows"]:
        L.append(f"  + {r['row']['id']} {r['row']['position']}  {r['provenance']['source']}:{r['provenance']['name']}  gain>=need {r['selection']['gain_ge_need']}  clr {r['metrics']['clearance']}")
    c = rep["candidates"]
    L.append(f"candidates: native {c['native_accepted']}/{c['native']} accepted, fill {c['fill_accepted']}/{c['fill']}; fill rejections {c['rejected_fill_by_reason']}")
    for blk in rep["exits"]["pockets"]:
        L.append(f"exit pocket {blk['pocket']} ({blk['side']}): {blk['valid_cells']}/{blk['box_cells_inside_pocket']} valid box cells, gates {blk['gates']}")
        for name, o in blk["options"].items():
            if o:
                L.append(f"  {name:<6} {o['position']} marker {o['nearest_marker']} player {o.get('nearest_player_row')} (edge {o.get('nearest_player_row_box_edge')}) gate {o['gate_distance_box_edge']} clr {o['border_clearance']}")
            else:
                L.append(f"  {name:<6} none")
    for o in rep["exits"]["current"]:
        L.append(f"current exit {o['name']} {o['position']}: inside pocket {o['inside_pocket']}, {o['nearest_pocket']} edge {o['pocket_edge_distance_xz']} m, marker {o['nearest_marker']}, player {o.get('nearest_player_row')}, clr {o['border_clearance']}")
    return "\n".join(L)


def build_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--stem", required=True, help="map stem: equator, bay5, airpit, bowl, iceberg, autoservice (tools/maps/<stem>*.json)")
    ap.add_argument("--maps-dir", default=str(REPO / "tools" / "maps"), help="folder of the sidecars")
    ap.add_argument("--navmesh", help="built NavMesh bundle (default work/build/<stem>/arena_<stem>_navmesh.bundle; equator: work/build/full)")
    ap.add_argument("--package", help="extracted package folder (colliders: gate footprints, standing room, exit solid check)")
    ap.add_argument("--sources", help='extra candidates: {"points":[{"source":"lasthero|shootout|ai_core|ai_born|stage|other","scene","path","name","position":[x,y,z],"yaw"}]}')
    ap.add_argument("--out", help="write the full JSON report here")
    ap.add_argument("--png", help="write a top-down picture here")
    ap.add_argument("--quiet", action="store_true", help="no text summary")
    for k, typ, help_ in (("floor", float, "distance floor in m (strictly farther counts)"), ("need", int, "distant rows wanted per cell"),
                          ("clearance", float, "min border clearance of a row"), ("nudge", float, "max move toward clearance"),
                          ("box_margin", float, "min distance outside both pockets"), ("gate_margin", float, "min distance to gate footprints"),
                          ("player_margin", float, "min distance to Player rows"), ("spacing", float, "min distance to every other row (3D)"),
                          ("min_rows", int, "keep selecting up to this many total rows while candidates exist"),
                          ("max_rows", int, "stop at this many total rows"), ("cell", float, "lattice step of the coverage cells"),
                          ("fill_step", float, "spacing of fill points"), ("fill_clearance", float, "min clearance of fill points"),
                          ("max_candidates", int, "cap on fill points (thinned by 2 until under)"), ("gate_link", float, "a gate belongs to a pocket within this XZ distance"),
                          ("exit_step", float, "lattice step of exit box centres"), ("min_corners", int, "exit box corners that must lie on the mesh"),
                          ("exit_gate_margin", float, "min distance of an exit box edge to the pocket's gate footprints (closed and open pose)"),
                          ("exit_clearance", float, "min border clearance at an exit box centre"),
                          ("ai_used_radius", float, "an AI point this close to an existing row counts as used")):
        ap.add_argument("--" + k.replace("_", "-"), type=typ, default=DEFAULTS[k], help=f"{help_} (default {DEFAULTS[k]})")
    ap.add_argument("--metric", choices=("3d", "xz"), default=DEFAULTS["metric"], help="distance the selection optimises (both are reported)")
    ap.add_argument("--include-pockets", action="store_true", help="optimise the cells inside the pockets too")
    ap.add_argument("--exit-require-reach", action="store_true", help="an exit box must also be plain-raid reachable (default: only reported; a team pocket behind a held gate is valid for ArenaMode)")
    ap.add_argument("--no-probe", action="store_true", help="skip the standing-room check")
    return ap


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    maps_dir = Path(args.maps_dir)
    inputs = load_inputs(args.stem, maps_dir, args.sources)
    nav = Path(args.navmesh) if args.navmesh else default_navmesh(args.stem)
    if not nav.is_file():
        print(f"error: NavMesh bundle {nav} does not exist (build the map or pass --navmesh)", file=sys.stderr)
        return 2
    import navmesh_diff
    mesh = navmesh_diff.load_bundle_mesh(nav)
    cset = None
    if args.package:
        import port_colliders as pcol
        pkg = Path(args.package)
        if not (pkg / "scenes").is_dir():
            print(f"error: {pkg} has no scenes folder", file=sys.stderr)
            return 2
        cset = pcol.load_package_colliders(pkg)
        pcol.mark_barriers(cset, list((inputs["descriptor"].get("opened_barriers") or [])), pcol.load_door_rows(maps_dir / f"{args.stem}.doors.json") if (maps_dir / f"{args.stem}.doors.json").is_file() else None)
    params = {k: getattr(args, k) for k in DEFAULTS if hasattr(args, k)}
    params["probe"] = not args.no_probe
    rep = analyse(inputs, mesh, cset, params, stem=args.stem)
    if args.png:
        write_png(args.png, rep)
    internal = rep.pop("_internal")
    if not args.quiet:
        print(format_text(rep))
    if args.out:
        Path(args.out).write_text(json.dumps(rep, indent=1, sort_keys=True, default=_json_default) + "\n", encoding="utf-8")
    rep["_internal"] = internal
    return 0


if __name__ == "__main__":
    sys.exit(main())
