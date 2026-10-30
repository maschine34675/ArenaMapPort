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
  * `keep_level_borders` (Bowl 2026-10-18, Iceberg 2026-10-30; `keep_borders=True` here) keeps the layer-29 cubes as
    sources: the layer-29 rule of `NonPhysicalLayerReason` and, since Iceberg, the `LevelBorder*` name prefix of
    `RoundOpenedReason` do not apply (Iceberg's 365 cubes are NAMED `LevelBorder (N)` under a `LevelBorders` group; Bowl's
    are called `Cube`, which is why the prefix never mattered before). `SPAWN_BOUND*` and the leaf names still apply;
  * every non-frame descendant of an `opened_barriers` path is skipped. A path is a root object name
    followed by `/`-separated child names resolved with Transform.Find semantics (first child of that
    name at each level); the barrier object itself stays, as in `RoundOpenedReason`. A path that has rows in
    the map's doors sidecar (`mark_barriers(..., door_rows=load_door_rows(...))`, `resolve_opened_barriers`) drops
    only the MOVED subtrees of its rows instead (Iceberg 2026-10-30: the unposed parts of a gate unit stay
    bake sources, as the client leaves them standing);
  * the whole subtree of a `navmesh_bake_exclusions` path is skipped, the listed object itself included
    (`BakeOnlyExclusionReason`; same path syntax and Find semantics; PLAN-AIRPIT item 8). These objects stay
    active at runtime, so unlike a barrier there is no frame exception;
  * an optional `navmesh_bake_layers` set restricts the collected layers (`layers_to_mask`,
    `source_owner_mask(layer_mask=...)`; `BakeNavMesh.SourceLayerMask`). The owner-layer rule above (13, 29)
    still applies on top of it.

Not mirrored: Unity's own treatment of MeshCollider.convex (the hull is not computed here), and the
exact tessellation of spheres and capsules (small fixed UV tessellations; boxes are exact).
"""
from __future__ import annotations

import json
import math
import struct
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

WATER_LAYER = 4
TRIGGERS_LAYER = 13
LEVEL_BORDER_LAYER = 29
LEVEL_BORDER_PREFIX = "LevelBorder"
EXCLUDED_NAME_PREFIXES = ("SPAWN_BOUND", LEVEL_BORDER_PREFIX)
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
    bake_excluded: bool = False
    tri_start: int = 0
    tri_count: int = 0


@dataclass
class ColliderSet:
    tris: np.ndarray            # (N, 3, 3) float64 world-space triangles (outward winding repaired for mirrored transforms)
    tri_owner: np.ndarray       # (N,) int32 index into owners
    owners: list
    scenes: dict                # scene name -> Scene
    unresolved_barriers: list
    unresolved_exclusions: list = field(default_factory=list)

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
    """BakeNavMesh.ResolveHierarchyPaths: [(scene, obj_id)] plus the paths that matched nothing."""
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


DOOR_AT_TOLERANCE = 0.05                 # ArenaDoorPose.AtTolerance == BakeNavMesh.DoorAtTolerance
DOOR_CLOSED_POSITION_TOLERANCE = 0.05    # ArenaDoorPose.ClosedPositionTolerance == BakeNavMesh.DoorClosedPositionTolerance


def door_sidecar_path(descriptor_path) -> Path | None:
    """ArenaPaths.DoorSidecarPath: tools/maps/bay5.json -> tools/maps/bay5.doors.json; None for a path that is not a .json file or
    already is a .doors.json file."""
    path = Path(descriptor_path)
    lower = path.name.lower()
    if not lower.endswith(".json") or lower.endswith(".doors.json"):
        return None
    return path.with_name(path.name[:-len(".json")] + ".doors.json")


def load_door_rows(path, expected_key: str | None = None) -> list[dict]:
    """The rows the bake reads (ArenaPaths.ParseDoorRows) from a doors sidecar (schema 2): barrier, part, node, at (world position of the
    barrier root instance or None), closed_position (the node's local closed position or None) and switch (a SetActive row)."""
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    if doc.get("schema_version") != 2:
        raise ColliderModelError(f"{path}: schema_version must be 2")
    if expected_key is not None and doc.get("key") != expected_key:
        raise ColliderModelError(f"{path}: key {doc.get('key')!r} is not {expected_key!r}")
    rows = []
    for i, pose in enumerate(doc.get("poses") or []):
        if not isinstance(pose.get("barrier"), str) or not pose["barrier"] or not isinstance(pose.get("part"), str) \
                or not isinstance(pose.get("node"), str):
            raise ColliderModelError(f"{path}: poses[{i}] needs a non-empty barrier and string part and node")
        closed = pose.get("closed")
        position = closed.get("position") if isinstance(closed, dict) else None
        rows.append({"barrier": pose["barrier"], "part": pose["part"], "node": pose["node"],
                     "at": None if pose.get("at") is None else [float(v) for v in pose["at"]],
                     "closed_position": None if position is None else [float(v) for v in position],
                     "switch": isinstance(pose.get("switch"), dict)})
    return rows


def find_all_instances(scenes: dict, path: str) -> list:
    """BakeNavMesh.FindAllInstances: [(scene, obj_id)] of EVERY object a hierarchy path names (all roots of the first name, all children
    of the next name at each level), not just the first one Transform.Find would take."""
    segments = path.split("/")
    found = []
    for scene in scenes.values():
        level = [r for r in scene.roots() if scene.objects[r]["name"] == segments[0]]
        for segment in segments[1:]:
            level = [c for k in level for c in scene.children.get(k, []) if scene.objects[c]["name"] == segment]
        found += [(scene.name, k) for k in level]
    return found


def _door_row_applies(row: dict, position) -> bool:
    """ArenaDoorPose.AppliesTo / BakeNavMesh.DoorRowAppliesTo: no 'at' = every instance, else Euclidean within 0.05 m."""
    if row["at"] is None:
        return True
    return sum((float(position[i]) - row["at"][i]) ** 2 for i in range(3)) <= DOOR_AT_TOLERANCE ** 2


def _children_named(scene, key, name) -> list:
    """BakeNavMesh.ChildrenNamed: every direct child called `name`, in sibling order."""
    return [c for c in scene.children.get(key, []) if scene.objects[c]["name"] == name]


def _closed_position_matches(scene, child, row: dict) -> bool:
    """BakeNavMesh.ClosedPositionMatches: the child's (node "": itself, else the node below it) LOCAL position is within 0.05 m of the row's."""
    probe = child if not row["node"] else scene.find(child, row["node"])
    if probe is None:
        return False
    local = scene.objects[probe]["local_position"]
    return sum((float(local[i]) - row["closed_position"][i]) ** 2 for i in range(3)) <= DOOR_CLOSED_POSITION_TOLERANCE ** 2


def _parts_of_row(scene, instance, row: dict) -> list:
    """BakeNavMesh.PartsOfRow: a name that occurs once (or a row without a closed position) takes the first child, as Transform.Find did;
    a repeated name takes every child standing at the row's closed position."""
    named = _children_named(scene, instance, row["part"])
    if len(named) <= 1 or row.get("closed_position") is None:
        return named[:1]
    return [c for c in named if _closed_position_matches(scene, c, row)]


def resolve_opened_barriers(scenes: dict, paths, rows) -> tuple[list, list, list, int]:
    """BakeNavMesh.ResolveOpenedBarriers -> (legacy paths, moved [(scene, obj_id)], unresolved [str], posed barrier count).

    A path WITHOUT rows in the doors sidecar is returned in `legacy`: it keeps the old rule (every non-frame descendant is dropped, the
    barrier object stays). A path WITH rows adds the MOVED subtrees to `moved`: per barrier root instance (all instances of the path when a
    row carries 'at', otherwise the Transform.Find instances) and per applicable row the row's part, or the instance itself for part "".
    Nothing else of a posed barrier is dropped, frame-named or not: the client leaves the unposed parts standing, so they are bake
    sources. A SetActive (switch) row moves nothing and drops nothing."""
    legacy, moved, unresolved, posed = [], [], [], 0
    for path in paths or []:
        if not path:
            continue
        path_rows = [r for r in rows or () if r["barrier"] == path]
        if not path_rows:
            legacy.append(path)
            continue
        posed += 1
        if any(r["at"] is not None for r in path_rows):
            instances = find_all_instances(scenes, path)
        else:
            instances, _missing = resolve_barriers(scenes, [path])
        if not instances:
            unresolved.append(path)
            continue
        used = [False] * len(path_rows)
        for scene_name, key in instances:
            scene = scenes[scene_name]
            position = scene.world(key)[:3, 3]
            for i, row in enumerate(path_rows):
                if not _door_row_applies(row, position):
                    continue
                used[i] = True
                if row["switch"]:
                    continue
                if not row["part"]:
                    if (scene_name, key) not in moved:
                        moved.append((scene_name, key))
                    continue
                parts = _parts_of_row(scene, key, row)
                if not parts:
                    unresolved.append(f"{path}/{row['part']} (door pose part; barrier root at {tuple(round(float(v), 3) for v in position)})")
                    continue
                for part in parts:
                    if (scene_name, part) not in moved:
                        moved.append((scene_name, part))
        unresolved += [f"{path} (door pose row {i} matches no instance)" for i, hit in enumerate(used) if not hit and not path_rows[i]["switch"]]
    return legacy, moved, unresolved, posed


def mark_barriers(cset: ColliderSet, barrier_paths, door_rows=None) -> None:
    """Flag every owner that RoundOpenedReason would drop because of `opened_barriers`.

    Without `door_rows` (the default, and every path of a map without a doors sidecar) the legacy rule applies to every path: all non-frame
    descendants go. With the rows of tools/maps/<stem>.doors.json a path that has rows drops only its moved subtrees
    (`resolve_opened_barriers`), exactly as BakeNavMesh does."""
    legacy_paths, moved, unresolved, _posed = resolve_opened_barriers(cset.scenes, barrier_paths, door_rows)
    barriers, legacy_unresolved = resolve_barriers(cset.scenes, legacy_paths)
    cset.unresolved_barriers = legacy_unresolved + unresolved
    by_scene: dict = {}
    for s, k in barriers:
        by_scene.setdefault(s, set()).add(k)
    moved_by_scene: dict = {}
    for s, k in moved:
        moved_by_scene.setdefault(s, set()).add(k)
    for o in cset.owners:
        o.barrier_blocked = False
        chain = cset.scenes[o.scene].path(o.obj_id)
        wanted_moved = moved_by_scene.get(o.scene)
        if wanted_moved and any(k in wanted_moved for k in chain):     # the moved part itself and everything below it
            o.barrier_blocked = True
            continue
        wanted = by_scene.get(o.scene)
        if not wanted:
            continue
        for i, k in enumerate(chain):
            if k in wanted and i < len(chain) - 1:               # strict descendant of the barrier
                below = chain[i + 1:]
                if not any("frame" in cset.scenes[o.scene].objects[b]["name"].lower() for b in below):
                    o.barrier_blocked = True
                    break


def mark_bake_exclusions(cset: ColliderSet, paths) -> None:
    """Flag every owner at or below a `navmesh_bake_exclusions` path (BakeNavMesh.BakeOnlyExclusionReason).

    Same path syntax and Transform.Find resolution as `opened_barriers`; unlike a barrier the listed object
    itself is dropped too (it stays active at runtime, so there are no frame parts to keep).
    """
    excluded, cset.unresolved_exclusions = resolve_barriers(cset.scenes, paths)
    by_scene: dict = {}
    for s, k in excluded:
        by_scene.setdefault(s, set()).add(k)
    for o in cset.owners:
        wanted = by_scene.get(o.scene)
        o.bake_excluded = bool(wanted) and any(k in wanted for k in cset.scenes[o.scene].path(o.obj_id))


def layers_to_mask(layers) -> int | None:
    """`navmesh_bake_layers` -> Unity layer mask (BakeNavMesh.SourceLayerMask without the Water rule, which
    `bake_exclusion_reason` applies). Empty or None: no restriction, as in a descriptor without the field."""
    if not layers:
        return None
    mask = 0
    for layer in layers:
        if type(layer) is not int or not 0 <= layer <= 31:
            raise ColliderModelError(f"navmesh_bake_layers entry is not a layer index (0..31): {layer!r}")
        mask |= 1 << layer
    return mask


def bake_exclusion_reason(o: Owner, keep_borders: bool = False) -> str | None:
    """Why the port bake drops this collider, or None when it is a NavMesh source (BakeNavMesh.cs).
    `keep_borders` is the descriptor's keep_level_borders: the layer-29 cubes stay sources and the `LevelBorder*` name
    prefix does not drop them (BakeNavMesh.NonPhysicalLayerReason / RoundOpenedReason)."""
    if o.layer == WATER_LAYER:
        return "Water layer"
    if o.layer == TRIGGERS_LAYER:
        return "Triggers layer 13"
    if o.layer == LEVEL_BORDER_LAYER and not keep_borders:
        return "LevelBorder layer 29"
    for name in o.names:
        for prefix in EXCLUDED_NAME_PREFIXES:
            if keep_borders and prefix == LEVEL_BORDER_PREFIX:
                continue
            if name.startswith(prefix):
                return prefix + "*"
        if name in EXCLUDED_LEAF_NAMES:
            return name
    if o.barrier_blocked:
        return "opened_barriers"
    if o.bake_excluded:
        return "navmesh_bake_exclusions"
    return None


def source_owner_mask(cset: ColliderSet, layer_mask: int | None = None, extra_exclude=None,
                      keep_borders: bool = False) -> np.ndarray:
    """Boolean per owner: is it a NavMesh source of the port bake (optionally restricted to a Unity
    layer mask, which is how Arena's own NavMeshSurface collects)? `keep_borders`: see bake_exclusion_reason."""
    out = np.zeros(len(cset.owners), dtype=bool)
    for i, o in enumerate(cset.owners):
        if bake_exclusion_reason(o, keep_borders) is not None:
            continue
        if layer_mask is not None and not (layer_mask >> o.layer) & 1:
            continue
        if extra_exclude is not None and extra_exclude(o):
            continue
        out[i] = True
    return out
