"""Offline approximation of the physics half of SpawnPlacementRepairs.IsValid, on the package's own colliders.

Why. The client repair (ArenaMapPort.Client/SpawnPlacementRepairs.cs) moves a spawn point whose capsule overlaps a solid
or that has no ground within one metre below it, with Physics queries at raid start. Whether Arena's authored positions
(the 70 SpawnPointMarkers of Iceberg, the player and bot rows derived from them) pass that test is knowable offline from
the same colliders the port imports: this module casts rays against their triangles (``port_colliders``), so a spawn
that would be pushed 6 m away on the host, or that stands in a pit, shows up before a Unity build.

What it models (all in world metres, y up):
  * ground ray: from feet + 0.3 straight down 1.0 m (IsValid's ``Physics.Raycast(feet + 0.3 up, down, 1)``); its hit is
    the floor under the feet (``floor_y``, ``ground_ok``);
  * floor without ground: when that ray finds nothing, the first solid below feet + 1.6 within 4 m (TryFindFree's probe)
    is reported as ``floor_y`` with ``ground_ok`` False;
  * ceiling: the first solid above the floor within 3 m (``ceiling`` = its height above the floor);
  * capsule: Unity's CheckCapsule is replaced by 16 horizontal rays of the capsule radius (0.35 m) at 0.5 m and 1.2 m
    above the floor; a hit means the capsule would overlap a solid.
It does not model the player collision matrix (every enabled, non-trigger, active collider counts, except those
``RUNTIME_NONSOLID`` names), mesh convexity, or moving parts (gate leaves are parked open at raid start and stand away
from the spawn pockets). A verdict is therefore a strong hint, not proof; the host's ``spawn placement:`` log line is the
authority.

Runtime non-solids: the SPAWN_BOUND* barriers are disabled by SpawnPlacementRepairs.ScanOwnedScenes and every collider of
a spawn marker (children of SPAWNS, layer 13) is a trigger after SpawnColliderRepairs.EnsureAllTriggers.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field

import numpy as np

CAPSULE_RADIUS = 0.35
CAPSULE_HEIGHT = 1.8
GROUND_RAY_START = 0.3        # IsValid: feet + 0.3 up
GROUND_RAY_LENGTH = 1.0
FLOOR_RAY_START = 1.6         # TryFindFree: candidate + 1.6 up
FLOOR_RAY_LENGTH = 4.0
CEILING_RAY_LENGTH = 3.0
SIDE_RAYS = 16
SIDE_HEIGHTS = (0.5, 1.2)
NEAR_RADIUS = 1.5             # triangles within this horizontal distance of the point are kept for its rays


def runtime_nonsolid(names) -> bool:
    """True for a collider that is not solid at raid time: ``names`` is the owner's name chain, root first."""
    return any(n.startswith("SPAWN_BOUND") for n in names) or (len(names) > 1 and names[0] == "SPAWNS")


@dataclass
class Verdict:
    point: tuple
    floor_y: float | None          # the solid under the feet (ground ray), else the first below point + 1.6 (None: nothing within 4 m)
    floor_owner: str | None
    ground_ok: bool                # IsValid's one-metre ground ray
    ceiling: float | None          # metres from the floor to the first solid above, None when clear for 3 m
    blocked: list = field(default_factory=list)   # (angle degrees, height, owner leaf name, distance) of capsule-ring hits

    @property
    def valid(self) -> bool:
        return self.ground_ok and not self.blocked and (self.ceiling is None or self.ceiling >= CAPSULE_HEIGHT)


class PlacementProbe:
    def __init__(self, tris: np.ndarray, tri_owner: np.ndarray, owner_paths: list):
        self.tris = tris
        self.tri_owner = tri_owner
        self.owner_paths = owner_paths
        self.mn = tris.min(axis=1) if len(tris) else np.empty((0, 3))
        self.mx = tris.max(axis=1) if len(tris) else np.empty((0, 3))

    @classmethod
    def from_collider_set(cls, cset, nonsolid=runtime_nonsolid) -> "PlacementProbe":
        keep = np.array([not nonsolid(o.names) for o in cset.owners], dtype=bool)
        mask = keep[cset.tri_owner]
        return cls(cset.tris[mask], cset.tri_owner[mask], [o.path for o in cset.owners])

    @classmethod
    def from_package(cls, package, nonsolid=runtime_nonsolid) -> "PlacementProbe":
        import port_colliders
        return cls.from_collider_set(port_colliders.load_package_colliders(package), nonsolid)

    # ------------------------------------------------------------------ rays
    def _near(self, point) -> np.ndarray:
        x, _y, z = point
        r = NEAR_RADIUS
        sel = ((self.mn[:, 0] <= x + r) & (self.mx[:, 0] >= x - r) & (self.mn[:, 2] <= z + r) & (self.mx[:, 2] >= z - r))
        return np.nonzero(sel)[0]

    def _cast(self, candidates, origin, direction, max_t):
        """First hit of the ray against the candidate triangles: (distance, triangle index) or None (Moller-Trumbore,
        two-sided: the model does not know which side of a MeshCollider triangle Unity ignores)."""
        if not len(candidates):
            return None
        o = np.asarray(origin, dtype=float)
        d = np.asarray(direction, dtype=float)
        end = o + d * max_t
        lo, hi = np.minimum(o, end) - 1e-4, np.maximum(o, end) + 1e-4
        sel = candidates[np.all((self.mn[candidates] <= hi) & (self.mx[candidates] >= lo), axis=1)]
        if not len(sel):
            return None
        t = self.tris[sel]
        v0, e1, e2 = t[:, 0], t[:, 1] - t[:, 0], t[:, 2] - t[:, 0]
        p = np.cross(d, e2)
        det = (e1 * p).sum(axis=1)
        ok = np.abs(det) > 1e-12
        inv = np.where(ok, 1.0 / np.where(ok, det, 1.0), 0.0)
        s = o - v0
        u = (s * p).sum(axis=1) * inv
        q = np.cross(s, e1)
        v = (q * d).sum(axis=1) * inv
        dist = (e2 * q).sum(axis=1) * inv
        hit = ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (dist >= 0) & (dist <= max_t)
        if not hit.any():
            return None
        k = int(np.argmin(np.where(hit, dist, np.inf)))
        return float(dist[k]), int(sel[k])

    def check(self, point) -> Verdict:
        """The verdict for a feet position (x, y, z)."""
        x, y, z = (float(v) for v in point)
        near = self._near((x, y, z))
        ground = self._cast(near, (x, y + GROUND_RAY_START, z), (0.0, -1.0, 0.0), GROUND_RAY_LENGTH)
        if ground is not None:      # the floor under the feet: IsValid's own ray
            floor_y, hit = y + GROUND_RAY_START - ground[0], ground
        else:                       # no ground within a metre: where TryFindFree's longer probe would find one
            hit = self._cast(near, (x, y + FLOOR_RAY_START, z), (0.0, -1.0, 0.0), FLOOR_RAY_LENGTH)
            floor_y = None if hit is None else y + FLOOR_RAY_START - hit[0]
        owner = None if hit is None else self.owner_paths[int(self.tri_owner[hit[1]])]
        ceiling, blocked = None, []
        base = y if floor_y is None else floor_y
        if floor_y is not None:
            up = self._cast(near, (x, floor_y + 0.05, z), (0.0, 1.0, 0.0), CEILING_RAY_LENGTH)
            ceiling = None if up is None else up[0] + 0.05
        for k in range(SIDE_RAYS):
            angle = k * math.tau / SIDE_RAYS
            for height in SIDE_HEIGHTS:
                side = self._cast(near, (x, base + height, z), (math.cos(angle), 0.0, math.sin(angle)), CAPSULE_RADIUS)
                if side is not None:
                    leaf = self.owner_paths[int(self.tri_owner[side[1]])].rsplit("/", 1)[-1]
                    blocked.append((round(math.degrees(angle)), height, leaf, round(side[0], 3)))
        return Verdict((x, y, z), floor_y, owner, ground is not None, ceiling, blocked)
