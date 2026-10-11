"""World-space collider triangles of an extracted package, plus the NavMesh source rules of the port bake.

Why. `UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs` bakes the port's NavMesh from the
physics colliders of the imported content scenes. Everything the oracle diff (`navmesh_diff.py`) says
about "solids of the port" needs those same colliders as plain triangles, with the same inclusion
rules, without starting Unity (docs/PLAN-AIRPIT.md item 7; the Equator booth / FCK-hall investigation
in docs/progress/stage1-combat.md used the same offline pattern by hand).

What it reads. `<package>/scenes/*.json` (hierarchy with local TRS, layer, `active`, components) and
`<package>/meshes/<id>.mesh.bin` (docs/SPEC.md C.2 layout). Nothing from the game install.

Source rules mirrored from BakeNavMesh.cs (kept in `bake_exclusion_reason`):
  * collider component enabled, not a trigger, every object up the chain active;
  * Water layer (4) is masked out of the collect call; owners on layers 13 (Triggers) and 29
    (LevelBorder) are skipped (`NonPhysicalLayerReason`);
  * any ancestor-or-self named `SPAWN_BOUND*` / `LevelBorder*` / `Door_Sliding_Respawn_L|_R` is skipped
    (`RoundOpenedReason`);
  * every non-frame descendant of an `opened_barriers` path is skipped. A path is a root object name
    followed by `/`-separated child names resolved with Transform.Find semantics (first child of that
    name at each level); the barrier object itself stays, as in `RoundOpenedReason`.

Not mirrored: Unity's own treatment of MeshCollider.convex (the hull is not computed here), and the
exact tessellation of spheres and capsules (small fixed UV tessellations; boxes are exact).
"""
from __future__ import annotations

import json
import math
import struct
from dataclasses import dataclass
from pathlib import Path

import numpy as np

WATER_LAYER = 4
TRIGGERS_LAYER = 13
LEVEL_BORDER_LAYER = 29
EXCLUDED_NAME_PREFIXES = ("SPAWN_BOUND", "LevelBorder")
EXCLUDED_LEAF_NAMES = ("Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R")

LAYER_NAMES = {
    0: "Default", 1: "TransparentFX", 2: "Ignore Raycast", 4: "Water", 5: "UI", 8: "Player",
    9: "DoorLowPolyCollider", 10: "PlayerCollisionTest", 11: "Terrain", 12: "HighPolyCollider", 13: "Triggers",
    14: "DisablerCullingObject", 15: "Loot", 16: "HitCollider", 17: "PlayerRenderers", 18: "LowPolyCollider",
    19: "Weapon Preview", 20: "Shells", 21: "CullingMask", 22: "Interactive", 23: "Deadbody", 24: "RainDrops",
    25: "Menu Environment", 26: "Foliage", 27: "PlayerSpiritAura", 28: "Sky", 29: "LevelBorder",
    30: "TransparentCollider", 31: "Grass",
}


class ColliderModelError(ValueError):
    pass


# ---------------------------------------------------------------------------------------------
# mesh.bin
# ---------------------------------------------------------------------------------------------

def read_mesh_bin(path) -> tuple[np.ndarray, np.ndarray]:
    """(vertices (V,3) float64, triangles (T,3) int64) of a `meshes/<id>.mesh.bin` (SPEC C.2)."""
    raw = Path(path).read_bytes()
    if len(raw) < 16:
        raise ColliderModelError(f"truncated mesh: {path}")
    magic, version, count, mask, index_format, sub_count = struct.unpack_from("<4sIIHBB", raw)
    if magic != b"AMPM" or version != 1 or index_format not in (0, 1) or not mask & 1:
        raise ColliderModelError(f"unsupported mesh: {path}")
    floats = 3 + (3 if mask & 2 else 0) + (4 if mask & 4 else 0) + (4 if mask & 8 else 0)
    floats += sum(2 for bit in range(4, 12) if mask & (1 << bit))
    sub_at = 16 + count * floats * 4
    idx_at = sub_at + sub_count * 9
    stride = 2 if index_format == 0 else 4
    if idx_at > len(raw) or (len(raw) - idx_at) % stride:
        raise ColliderModelError(f"truncated mesh channels: {path}")
    verts = np.frombuffer(raw, "<f4", count * 3, 16).reshape(-1, 3).astype(np.float64)
    indices = np.frombuffer(raw, "<u2" if stride == 2 else "<u4", offset=idx_at)
    tris = []
    for i in range(sub_count):
        start, length, topology = struct.unpack_from("<IIB", raw, sub_at + i * 9)
        if topology != 0:
            raise ColliderModelError(f"non-triangle collider topology in {path}")
        if start + length > len(indices) or length % 3:
            raise ColliderModelError(f"invalid submesh range in {path}")
        if length:
            sub = indices[start:start + length].astype(np.int64)
            if sub.max() >= count:
                raise ColliderModelError(f"index out of range in {path}")
            tris.append(sub.reshape(-1, 3))
    return verts, (np.concatenate(tris) if tris else np.empty((0, 3), dtype=np.int64))


# ---------------------------------------------------------------------------------------------
# primitives
# ---------------------------------------------------------------------------------------------

_BOX_TRIS = np.array([[0, 1, 3], [0, 3, 2], [4, 6, 7], [4, 7, 5], [0, 4, 5], [0, 5, 1],
                      [2, 3, 7], [2, 7, 6], [0, 2, 6], [0, 6, 4], [1, 5, 7], [1, 7, 3]])


def box_mesh(center, size):
    corners = np.array([[x, y, z] for x in (-.5, .5) for y in (-.5, .5) for z in (-.5, .5)])
    return corners * np.asarray(size, dtype=float) + np.asarray(center, dtype=float), _BOX_TRIS


def _uv_sphere(radius, lat=6, lon=10):
    verts = [(0.0, radius, 0.0)]
    for i in range(1, lat):
        theta = math.pi * i / lat
        for j in range(lon):
            phi = 2 * math.pi * j / lon
            verts.append((radius * math.sin(theta) * math.cos(phi), radius * math.cos(theta),
                          radius * math.sin(theta) * math.sin(phi)))
    verts.append((0.0, -radius, 0.0))
    bottom = len(verts) - 1
    tris = []
    for j in range(lon):
        tris.append((0, 1 + j, 1 + (j + 1) % lon))
        tris.append((bottom, 1 + (lat - 2) * lon + (j + 1) % lon, 1 + (lat - 2) * lon + j))
    for i in range(lat - 2):
        for j in range(lon):
            a, b = 1 + i * lon + j, 1 + i * lon + (j + 1) % lon
            c, d = a + lon, b + lon
            tris.append((a, c, b))
            tris.append((b, c, d))
    return np.array(verts), np.array(tris)


def sphere_mesh(center, radius):
    verts, tris = _uv_sphere(radius)
    return verts + np.asarray(center, dtype=float), tris


def capsule_mesh(center, radius, height, direction):
    """Two hemispheres joined by the straight part, axis `direction` (0 x, 1 y, 2 z)."""
    verts, tris = _uv_sphere(radius)
    half = max(height * 0.5 - radius, 0.0)
    upper, lower = verts.copy(), verts.copy()
    upper[:, 1] += half
    lower[:, 1] -= half
    out_v = np.concatenate([upper, lower])
    out_t = np.concatenate([tris, tris + len(verts)])
    # side band joining the two equators
    lat, lon = 6, 10
    eq = 1 + (lat // 2 - 1) * lon
    band = []
    for j in range(lon):
        a, b = eq + j, eq + (j + 1) % lon
        band.append((a, a + len(verts), b))
        band.append((b, a + len(verts), b + len(verts)))
    out_t = np.concatenate([out_t, np.array(band)])
    if direction == 0:
        out_v = out_v[:, (1, 0, 2)] * (1, -1, 1)
    elif direction == 2:
        out_v = out_v[:, (0, 2, 1)] * (1, 1, -1)
    return out_v + np.asarray(center, dtype=float), out_t


# ---------------------------------------------------------------------------------------------
# scenes
# ---------------------------------------------------------------------------------------------

def _local_matrix(obj):
    x, y, z, w = obj["local_rotation"]
    m = np.eye(4)
    m[:3, :3] = np.array([
        [1 - 2 * y * y - 2 * z * z, 2 * x * y - 2 * z * w, 2 * x * z + 2 * y * w],
        [2 * x * y + 2 * z * w, 1 - 2 * x * x - 2 * z * z, 2 * y * z - 2 * x * w],
        [2 * x * z - 2 * y * w, 2 * y * z + 2 * x * w, 1 - 2 * x * x - 2 * y * y],
    ]) @ np.diag(obj["local_scale"])
    m[:3, 3] = obj["local_position"]
    return m


class Scene:
    """One extracted scene with lazily computed world matrices, paths and activity."""

    def __init__(self, data: dict):
        self.name = data["scene_name"]
        self.objects = {o["id"]: o for o in data["objects"]}
        if len(self.objects) != len(data["objects"]):
            raise ColliderModelError(f"duplicate object ids in {self.name}")
        self.children: dict = {}
        for o in data["objects"]:
            self.children.setdefault(o.get("parent_id"), []).append(o["id"])
        self._world: dict = {}
        self._path: dict = {}
        self._active: dict = {}
        self._sizes = None

    def roots(self):
        return list(self.children.get(None, []))

    def world(self, key):
        cached = self._world.get(key)
        if cached is not None:
            return cached
        matrix = None
        for k in self.path(key):                      # root .. key
            w = self._world.get(k)
            if w is None:
                local = _local_matrix(self.objects[k])
                w = local if matrix is None else matrix @ local
                self._world[k] = w
            matrix = w
        return matrix

    def path(self, key):
        if key not in self._path:
            chain, cur, seen = [], key, set()
            while cur is not None:
                if cur in seen:
                    raise ColliderModelError(f"cyclic hierarchy in {self.name}")
                seen.add(cur)
                chain.append(cur)
                cur = self.objects[cur].get("parent_id")
            self._path[key] = tuple(reversed(chain))
        return self._path[key]

    def path_names(self, key):
        return [self.objects[k]["name"] for k in self.path(key)]

    def subtree_size(self, key):
        """Number of objects in the subtree of `key` (itself included)."""
        if self._sizes is None:
            sizes = {k: 1 for k in self.objects}
            for k in sorted(self.objects, key=lambda x: -len(self.path(x))):
                parent = self.objects[k].get("parent_id")
                if parent is not None:
                    sizes[parent] += sizes[k]
            self._sizes = sizes
        return self._sizes[key]

    def chain_active(self, key):
        if key not in self._active:
            self._active[key] = all(self.objects[k].get("active", True) for k in self.path(key))
        return self._active[key]

    def find(self, root_id, rest):
        """Transform.Find(rest) from a root: first child with the name at every level."""
        cur = root_id
        for part in rest.split("/"):
            nxt = None
            for cid in self.children.get(cur, []):
                if self.objects[cid]["name"] == part:
                    nxt = cid
                    break
            if nxt is None:
                return None
            cur = nxt
        return cur


@dataclass
class Owner:
    """One collider component of the port, with everything the bake rules need."""
    index: int
    scene: str
    obj_id: str
    path: str
    names: tuple
    layer: int
    kind: str
    enabled: bool
    trigger: bool
    active: bool
    barrier_blocked: bool = False
    tri_start: int = 0
    tri_count: int = 0


@dataclass
class ColliderSet:
    tris: np.ndarray            # (N, 3, 3) float64 world-space triangles (outward winding repaired for mirrored transforms)
    tri_owner: np.ndarray       # (N,) int32 index into owners
    owners: list
    scenes: dict                # scene name -> Scene
    unresolved_barriers: list

    def owner_mask(self, predicate) -> np.ndarray:
        return np.array([bool(predicate(o)) for o in self.owners], dtype=bool)


def _collider_mesh(comp, mesh_cache, meshes_dir):
    kind = comp["type"]
    if kind == "MeshCollider":
        mesh_id = comp.get("mesh_id")
        if not mesh_id:
            return None
        if mesh_id not in mesh_cache:
            mesh_cache[mesh_id] = read_mesh_bin(Path(meshes_dir) / f"{mesh_id}.mesh.bin")
        return mesh_cache[mesh_id]
    if kind == "BoxCollider":
        return box_mesh(comp.get("center", [0, 0, 0]), comp["size"])
    if kind == "SphereCollider":
        return sphere_mesh(comp.get("center", [0, 0, 0]), comp["radius"])
    if kind == "CapsuleCollider":
        return capsule_mesh(comp.get("center", [0, 0, 0]), comp["radius"], comp["height"], comp.get("direction", 1))
    return None


def load_package_colliders(package, scene_files=None, include_inactive=False, include_disabled=False,
                           include_trigger=False) -> ColliderSet:
    """All collider triangles of the package scenes in world space.

    By default only colliders that the bake could see as sources are kept (enabled, non-trigger, whole
    chain active); `include_*` keep the others too so a report can say what was filtered.
    """
    package = Path(package)
    files = sorted((package / "scenes").glob("*.json")) if scene_files is None else [package / "scenes" / f for f in scene_files]
    if not files:
        raise ColliderModelError(f"no scenes in {package / 'scenes'}")
    scenes, owners, tri_chunks, owner_chunks = {}, [], [], []
    mesh_cache: dict = {}
    total = 0
    for f in files:
        scene = Scene(json.loads(f.read_text(encoding="utf-8")))
        scenes[scene.name] = scene
        for key, obj in scene.objects.items():
            comps = [c for c in obj.get("components", []) if c["type"] in
                     ("MeshCollider", "BoxCollider", "SphereCollider", "CapsuleCollider")]
            if not comps:
                continue
            active = scene.chain_active(key)
            for comp in comps:
                enabled = bool(comp.get("enabled", True))
                trigger = bool(comp.get("is_trigger", False))
                if (not active and not include_inactive) or (not enabled and not include_disabled) \
                        or (trigger and not include_trigger):
                    continue
                mesh = _collider_mesh(comp, mesh_cache, package / "meshes")
                if mesh is None:
                    continue
                verts, tri = mesh
                if not len(tri):
                    continue
                world = scene.world(key)
                wv = verts @ world[:3, :3].T + world[:3, 3]
                t = wv[tri]
                if np.linalg.det(world[:3, :3]) < 0:        # mirrored: restore winding
                    t = t[:, (0, 2, 1), :]
                idx = len(owners)
                names = tuple(scene.path_names(key))
                owners.append(Owner(idx, scene.name, key, "/".join(names), names, int(obj["layer"]), comp["type"],
                                    enabled, trigger, active, tri_start=total, tri_count=len(t)))
                tri_chunks.append(t)
                owner_chunks.append(np.full(len(t), idx, dtype=np.int32))
                total += len(t)
    tris = np.concatenate(tri_chunks) if tri_chunks else np.empty((0, 3, 3))
    tri_owner = np.concatenate(owner_chunks) if owner_chunks else np.empty(0, dtype=np.int32)
    return ColliderSet(tris, tri_owner, owners, scenes, [])


# ---------------------------------------------------------------------------------------------
# bake rules
# ---------------------------------------------------------------------------------------------

def resolve_barriers(scenes: dict, paths) -> tuple[list, list]:
    """BakeNavMesh.ResolveOpenedBarriers: [(scene, obj_id)] plus the paths that matched nothing."""
    found, unresolved = [], []
    for path in paths or []:
        if not path:
            continue
        slash = path.find("/")
        root_name = path if slash < 0 else path[:slash]
        rest = None if slash < 0 else path[slash + 1:]
        any_hit = False
        for scene in scenes.values():
            for rid in scene.roots():
                if scene.objects[rid]["name"] != root_name:
                    continue
                target = rid if rest is None else scene.find(rid, rest)
                if target is not None:
                    found.append((scene.name, target))
                    any_hit = True
        if not any_hit:
            unresolved.append(path)
    return found, unresolved


def mark_barriers(cset: ColliderSet, barrier_paths) -> None:
    """Flag every owner that RoundOpenedReason would drop because of `opened_barriers`."""
    barriers, cset.unresolved_barriers = resolve_barriers(cset.scenes, barrier_paths)
    by_scene: dict = {}
    for s, k in barriers:
        by_scene.setdefault(s, set()).add(k)
    for o in cset.owners:
        o.barrier_blocked = False
        wanted = by_scene.get(o.scene)
        if not wanted:
            continue
        chain = cset.scenes[o.scene].path(o.obj_id)
        for i, k in enumerate(chain):
            if k in wanted and i < len(chain) - 1:               # strict descendant of the barrier
                below = chain[i + 1:]
                if not any("frame" in cset.scenes[o.scene].objects[b]["name"].lower() for b in below):
                    o.barrier_blocked = True
                    break


def bake_exclusion_reason(o: Owner) -> str | None:
    """Why the port bake drops this collider, or None when it is a NavMesh source (BakeNavMesh.cs)."""
    if o.layer == WATER_LAYER:
        return "Water layer"
    if o.layer == TRIGGERS_LAYER:
        return "Triggers layer 13"
    if o.layer == LEVEL_BORDER_LAYER:
        return "LevelBorder layer 29"
    for name in o.names:
        for prefix in EXCLUDED_NAME_PREFIXES:
            if name.startswith(prefix):
                return prefix + "*"
        if name in EXCLUDED_LEAF_NAMES:
            return name
    if o.barrier_blocked:
        return "opened_barriers"
    return None


def source_owner_mask(cset: ColliderSet, layer_mask: int | None = None, extra_exclude=None) -> np.ndarray:
    """Boolean per owner: is it a NavMesh source of the port bake (optionally restricted to a Unity
    layer mask, which is how Arena's own NavMeshSurface collects)?"""
    out = np.zeros(len(cset.owners), dtype=bool)
    for i, o in enumerate(cset.owners):
        if bake_exclusion_reason(o) is not None:
            continue
        if layer_mask is not None and not (layer_mask >> o.layer) & 1:
            continue
        if extra_exclude is not None and extra_exclude(o):
            continue
        out[i] = True
    return out
