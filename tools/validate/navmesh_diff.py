"""NavMesh diff: Arena's own NavMesh against the port's colliders (docs/PLAN-AIRPIT.md item 7).

Why. The port bakes a fresh NavMesh from every imported physics collider (BakeNavMesh.cs); Arena's
shipped NavMesh (`navmesh_oracle.py`) is the only independent record of where its agents walk. On
AirPit the two disagree: Arena's single 8,168 m2 component walks through roughly 190 prop instances of
the base scene. Instead of guessing barrier names for `opened_barriers`, this tool measures it.

What it does (offline, read-only; no Unity, no host):
  1. Reads Arena's NavMeshData (default: AirPit, sharedassets33.assets path 2; `--map bowl` selects Bowl's
     sharedassets128.assets path 2, see ARENA_SOURCES) in WORLD coordinates (the NavMeshData's m_Position /
     m_Rotation are applied by navmesh_oracle; Bowl's is a 90 degree yaw, AirPit's the identity) and samples
     its polygons on a lattice (default 0.2 m), keeping the main component (or all low components).
  2. Loads the port's collider triangles from an extracted package with the bake rules of
     BakeNavMesh.cs (`port_colliders.py`), optionally with extra `opened_barriers` paths, bake-only
     `navmesh_bake_exclusions` (PLAN-AIRPIT item 8: dropped from the sources, still active at runtime) and a
     `navmesh_bake_layers` mask; `--descriptor tools/maps/<key>.json` applies all three.
  3. Runs the column test of `walk_model.py` for every sample: blocked by a port solid? supported by a
     port floor? Twice: with Arena's agent (0.28 / 1.6, with a slack for Detour simplification), which
     lists solids Arena provably walks through, and with the port agent (0.30 / 1.7), which predicts the
     port bake's connectivity inside Arena's footprint.
  4. Attributes the blocked samples to colliders and ranks hierarchy clusters (prop roots, families,
     containers); simulates exclusion sets without re-rasterising (a sample becomes passable when all
     of its blockers are excluded) and proposes a greedy list that joins the probe points.
  5. Reports reachability of probe points (spawns, exits, capture point, AI core points): which
     Arena component, clearance to Arena's border, and which port-model component.
  6. Alternative port side: `--port-triangulation` takes the `ExportMapTriangulation` receipt of a
     built NavMesh bundle and reports how much of Arena's footprint it covers and its components.

Not a bake: see walk_model.py for the model and its limits. The port-model connectivity is a lower
bound restricted to Arena's footprint (detours through places Arena considers unwalkable are not
counted). Calibration against the real spike bake is in docs/progress/airpit-release.md.
"""
from __future__ import annotations

import argparse
import dataclasses
import json
import math
import re
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import navmesh_oracle as oracle          # noqa: E402
import port_colliders as pcol            # noqa: E402
import walk_model as wm                  # noqa: E402

DEFAULT_ARENA_DATA = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
AIRPIT_NAVMESH = ("sharedassets33.assets", 2)
# Arena NavMeshSurface.m_LayerMask of AirPit's TeamFight_AI scene (decoded by probe_mode_toggler.py):
# DoorLowPolyCollider, Terrain, Triggers, LowPolyCollider, TransparentCollider.
AIRPIT_ARENA_LAYER_MASK = 0x40042A00
DEFAULT_PROP_MAX_OBJECTS = 120


@dataclass(frozen=True)
class ArenaSource:
    """Where one map's Arena-side oracle inputs live in the installed game (all read-only).

    `assets` / `path_id`: the NavMeshData of the map's TeamFight AI scene; `layer_mask`: that scene's
    NavMeshSurface.m_LayerMask (decoded by probe_mode_toggler); `ai_scene_index` / `ai_scene_name`: the `ai`
    scene role of tools/extract/plans/<stem>.json (a test keeps both in step); `package`: the default extracted
    package for the port side. AirPit's row is what the module hard-wired before PLAN-BOWL item 6.
    """
    key: str
    assets: str
    path_id: int
    layer_mask: int
    ai_scene_index: int
    ai_scene_name: str
    package: str


ARENA_SOURCES = {
    "airpit": ArenaSource("arena_airpit", AIRPIT_NAVMESH[0], AIRPIT_NAVMESH[1], AIRPIT_ARENA_LAYER_MASK,
                          33, "Arena_Airpit_TeamFight_AI", "work/extract/airpit-native-v8"),
    # Bowl (measured 2026-10-18, Arena 0.6.0.47752): level128, NavMeshSurface layers 9 / 11 / 18 / 30 (no 13), 80 tiles,
    # agent radius 0.3 / height 1.7, NavMeshData rotated 90 degrees about Y (navmesh_oracle applies it).
    "bowl": ArenaSource("arena_bowl", "sharedassets128.assets", 2, 0x40040A00,
                        128, "Arena_Bowl_TeamFight_AI", "work/extract/bowl-native-v2"),
}


@dataclass
class Config:
    cell: float = 0.1
    y_step: float = 0.1
    sample_step: float = 0.2
    # Calibrated against the spike bake of the port (docs/progress/airpit-release.md): with these
    # values the model agrees with the real bake on 98.7 percent of Arena's samples.
    arena_slack: float = 0.1             # voxel / Detour-simplification allowance subtracted from the Arena agent radius
    port_slack: float = 0.1              # the same for the port agent
    floor_margin: float = 0.15           # extra height above the climb before a solid counts as an obstacle
    support_tol: float = 0.6             # a floor within this height of the sample supports it (stairs treads)
    max_y: float = 12.0
    components: str = "main"             # "main" | "low" (every component whose lowest point is <= max_y)
    region_margin: float = 4.0
    arena_layer_mask: int | None = AIRPIT_ARENA_LAYER_MASK
    prop_max_objects: int = DEFAULT_PROP_MAX_OBJECTS
    probe_radius: float = 0.8
    # Hole test of the presence survey (`ring_survey`): a prop counts as honoured by Arena's mesh only when
    # Arena-walkable floor surrounds it on most sides while its own footprint is not walkable.
    hole_ring_points: int = 16           # points on the circle around the prop's bounding box
    hole_ring_margin: float = 0.6        # circle radius = half the bounding-box diagonal + this margin
    hole_ring_min: int = 12              # walkable ring points needed (of hole_ring_points)


# ---------------------------------------------------------------------------------------------
# clusters
# ---------------------------------------------------------------------------------------------

_FAMILY_STRIP = [re.compile(r"\s*\(\d+\)\s*$"), re.compile(r"_?\(ARENA\)", re.I), re.compile(r"\s+Variant\s*$"),
                 re.compile(r"\s+Group\s*$"), re.compile(r"\s*\(\d+\)\s*$")]


def family_name(name: str) -> str:
    out = name
    for _ in range(2):
        for rx in _FAMILY_STRIP:
            out = rx.sub("", out)
    return out.strip() or name


@dataclass(frozen=True)
class ClusterKey:
    scene: str
    root_id: str


def cluster_of(cset, owner, max_prop_objects: int = DEFAULT_PROP_MAX_OBJECTS) -> dict:
    """Prop-root cluster of a collider owner.

    The prop root is the highest ancestor-or-self whose whole subtree has at most `max_prop_objects`
    objects: one placed prop instance (a sandbag, a fence, a door assembly) with all its colliders, as
    opposed to the containers around it (`SOO`, `BUILDING`, `FRONT`, `VARIANT`) that hold hundreds of
    props. Scene roots are never prop roots unless the collider sits on one.
    """
    scene = cset.scenes[owner.scene]
    chain = scene.path(owner.obj_id)
    root_index = len(chain) - 1
    for i in range(len(chain) - 1, 0, -1):
        if scene.subtree_size(chain[i]) > max_prop_objects:
            break
        root_index = i
    root_id = chain[root_index]
    names = [scene.objects[k]["name"] for k in chain]
    parent = chain[root_index - 1] if root_index > 0 else None
    siblings = [c for c in scene.children.get(parent, []) if scene.objects[c]["name"] == names[root_index]] if parent else []
    # Transform.Find resolves every duplicate sibling name to the first one
    first_match = siblings[0] if siblings else None
    return {
        "key": ClusterKey(owner.scene, root_id),
        "scene": owner.scene,
        "path": "/".join(names[:root_index + 1]),
        "container": "/".join(names[:root_index]),
        "family": family_name(names[root_index]),
        "ambiguous": bool(parent) and len(siblings) > 1,
        "find_resolves_here": (first_match == root_id) if parent else True,
        "collider_on_root": owner.obj_id == root_id,
    }


# ---------------------------------------------------------------------------------------------
# probe points
# ---------------------------------------------------------------------------------------------

def load_points(path) -> list[dict]:
    """Probe points from a flexible JSON: {"points": [...]} (name, kind, position) or a bots/exits sidecar."""
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    out = []
    for p in data.get("points", []):
        out.append({"name": p["name"], "kind": p.get("kind", "point"), "position": [float(v) for v in p["position"]]})
    for p in data.get("player_spawns", []):
        out.append({"name": p.get("name") or p.get("id"), "kind": "player_spawn", "position": [float(v) for v in p["position"]]})
    for p in data.get("bot_spawns", []):
        out.append({"name": p.get("name") or p.get("id"), "kind": "bot_spawn", "position": [float(v) for v in p["position"]]})
    for p in data.get("exits", []):
        out.append({"name": p["name"], "kind": "exit", "position": [float(v) for v in p["position"]]})
    for p in data.get("core_points", []):
        out.append({"name": f"core{p['id']}", "kind": "core_point", "position": [float(v) for v in p["position"]]})
    for w in data.get("patrol_ways", []):
        for i, hub in enumerate(w.get("hubs", [])):
            out.append({"name": f"hub{i}", "kind": "patrol_hub", "position": [float(v) for v in hub["position"]]})
            for j, sub in enumerate(hub.get("sub_points", [])):
                out.append({"name": f"hub{i}.{j}", "kind": "patrol_point", "position": [float(v) for v in sub]})
    return out


def live_ai_points(arena_data=DEFAULT_ARENA_DATA, scene_index=33, scene_name="Arena_Airpit_TeamFight_AI",
                   map_key="arena_airpit") -> list[dict]:
    """AI core points and patrol hubs/points of a map's TeamFight_AI scene (default AirPit's; Bowl: 128,
    "Arena_Bowl_TeamFight_AI", "arena_bowl"; read-only, via the existing extractor's decoders; nothing is
    written). A scene without patrol ways yields core points only (Bowl has one way with 7 hubs / 79 sub points,
    AirPit one with 3 / 35)."""
    extract_dir = Path(__file__).resolve().parents[1] / "extract"
    sys.path.insert(0, str(extract_dir))
    import extract_ai_anchors as anchors
    data = anchors.extract_map(map_key, scene_index, scene_name, arena_data)
    tmp = {"core_points": data["core_points"], "patrol_ways": data["patrol_ways"]}
    out = []
    for p in tmp["core_points"]:
        out.append({"name": f"core{p['id']}", "kind": "core_point", "position": [float(v) for v in p["position"]]})
    for i, hub in enumerate(tmp["patrol_ways"][0]["hubs"] if tmp["patrol_ways"] else []):
        out.append({"name": f"hub{i}", "kind": "patrol_hub", "position": [float(v) for v in hub["position"]]})
        for j, sub in enumerate(hub["sub_points"]):
            out.append({"name": f"hub{i}.{j}", "kind": "patrol_point", "position": [float(v) for v in sub]})
    return out


def arena_source_from_plan(plan_path, base: ArenaSource | None = None) -> ArenaSource:
    """The AI scene (index, name) and map key of a source plan's `ai` role, for a map without an ARENA_SOURCES
    row (`--plan`). Everything else (NavMeshData, mask, package) comes from `base` (default AirPit's row), so a
    map that needs those too gets a table row; extract_ai_anchors resolves the role and fails closed on a
    plan without one."""
    extract_dir = Path(__file__).resolve().parents[1] / "extract"
    sys.path.insert(0, str(extract_dir))
    import extract_ai_anchors as anchors
    from source_plan import load_plan
    plan = load_plan(Path(plan_path))
    index, name = anchors.ai_scene_for_plan(plan)
    return dataclasses.replace(base or ARENA_SOURCES["airpit"], key=plan["key"], ai_scene_index=index, ai_scene_name=name)


# ---------------------------------------------------------------------------------------------
# Arena side
# ---------------------------------------------------------------------------------------------

def load_arena_mesh(arena_data=DEFAULT_ARENA_DATA, assets=AIRPIT_NAVMESH[0], path_id=AIRPIT_NAVMESH[1]):
    """Arena's NavMesh of one map in WORLD coordinates (the tiles carry the NavMeshData placement and
    `build_navmesh` applies it; identity on AirPit, a 90 degree yaw on Bowl)."""
    tiles, meta = oracle.load_navmesh_tiles(f"{arena_data}/{assets}", path_id)
    return oracle.build_navmesh(tiles), meta


def arena_samples(mesh, cfg: Config):
    """Lattice samples of the selected Arena components: (xyz, poly, label)."""
    origin = (0.5 * cfg.cell, 0.5 * cfg.cell)
    pts, polys = oracle.sample_polygons(mesh, cfg.sample_step, origin=origin)
    labels = mesh.labels[polys]
    if cfg.components == "main":
        keep = labels == 0
    else:                                    # every component, below the play-space ceiling
        keep = pts[:, 1] <= cfg.max_y
    return pts[keep], polys[keep], labels[keep]


# ---------------------------------------------------------------------------------------------
# sample graph
# ---------------------------------------------------------------------------------------------

class SampleGraph:
    """4-neighbour adjacency of lattice samples that belong to the same Arena component and differ in
    height by at most `max_dy` (so stacked levels never connect through the lattice)."""

    def __init__(self, pts, labels, step, origin, max_dy=0.45):
        self.n = len(pts)
        self.ix = np.round((pts[:, 0] - origin[0]) / step).astype(np.int64)
        self.iz = np.round((pts[:, 2] - origin[1]) / step).astype(np.int64)
        cols: dict = defaultdict(list)
        for k in range(self.n):
            cols[(int(self.ix[k]), int(self.iz[k]))].append(k)
        pairs = []
        for (i, j), members in cols.items():
            for di, dj in ((1, 0), (0, 1)):
                other = cols.get((i + di, j + dj))
                if not other:
                    continue
                for a in members:
                    for b in other:
                        if labels[a] == labels[b] and abs(pts[a, 1] - pts[b, 1]) <= max_dy:
                            pairs.append((a, b))
        self.pairs = np.array(pairs, dtype=np.int64).reshape(-1, 2)
        nbrs: list = [[] for _ in range(self.n)]
        for a, b in self.pairs:
            nbrs[a].append(b)
            nbrs[b].append(a)
        self.neighbors = nbrs
        self.columns = cols

    def components(self, passable: np.ndarray):
        """Component id per sample (-1 where not passable), sizes list."""
        parent = np.arange(self.n)

        def find(a):
            while parent[a] != a:
                parent[a] = parent[parent[a]]
                a = parent[a]
            return a

        ok = passable[self.pairs[:, 0]] & passable[self.pairs[:, 1]] if len(self.pairs) else np.zeros(0, dtype=bool)
        for a, b in self.pairs[ok]:
            ra, rb = find(int(a)), find(int(b))
            if ra != rb:
                parent[rb] = ra
        roots = np.array([find(i) for i in range(self.n)])
        comp = np.full(self.n, -1, dtype=np.int64)
        idx = np.nonzero(passable)[0]
        uniq, inv = np.unique(roots[idx], return_inverse=True)
        sizes = np.bincount(inv)
        order = np.argsort(-sizes, kind="stable")
        rank = np.empty(len(uniq), dtype=np.int64)
        rank[order] = np.arange(len(uniq))
        comp[idx] = rank[inv]
        return comp, sizes[order]


def nearest_sample(pts, graph: SampleGraph, step, origin, point, radius, max_dy=1.0):
    """Index of the closest lattice sample to `point` within `radius` (xz) and `max_dy`, or -1."""
    reach = int(math.ceil(radius / step))
    ci = int(round((point[0] - origin[0]) / step))
    cj = int(round((point[2] - origin[1]) / step))
    best, best_d = -1, float("inf")
    for i in range(ci - reach, ci + reach + 1):
        for j in range(cj - reach, cj + reach + 1):
            for k in graph.columns.get((i, j), ()):
                dxz = math.hypot(pts[k, 0] - point[0], pts[k, 2] - point[2])
                dy = abs(pts[k, 1] - point[1])
                if dxz <= radius and dy <= max_dy:
                    d = dxz * dxz + dy * dy
                    if d < best_d:
                        best, best_d = k, d
    return best


# ---------------------------------------------------------------------------------------------
# analysis
# ---------------------------------------------------------------------------------------------

def _source_arrays(cset, mask):
    tri_mask = mask[cset.tri_owner]
    return cset.tris[tri_mask], cset.tri_owner[tri_mask]


def _relevance(tris, ylo_all, yhi_all, agent):
    ymin = tris[:, :, 1].min(axis=1)
    ymax = tris[:, :, 1].max(axis=1)
    solid_ok = (ymax >= ylo_all + agent.climb - 0.1) & (ymin <= yhi_all + agent.height + 0.1)
    floor_ok = (ymax >= ylo_all - 0.4) & (ymin <= yhi_all + 0.4)
    return solid_ok, floor_ok


def rank_clusters(attr: dict, cset, cfg: Config, pts, owner_in_arena_mask=None):
    """Aggregate owner -> samples into prop-root clusters."""
    clusters: dict = {}
    for o, samples in attr.items():
        owner = cset.owners[o]
        info = cluster_of(cset, owner, cfg.prop_max_objects)
        c = clusters.setdefault(info["key"], {**info, "owners": [], "samples": set(), "layers": defaultdict(int)})
        c["collider_on_root"] = c["collider_on_root"] or info["collider_on_root"]
        c["owners"].append(o)
        c["samples"].update(int(s) for s in samples)
        c["layers"][owner.layer] += 1
    out = []
    for key, c in clusters.items():
        s = np.array(sorted(c["samples"]), dtype=np.int64)
        xyz = pts[s]
        arena_visible = False
        if owner_in_arena_mask is not None:
            arena_visible = bool(any(owner_in_arena_mask[o] for o in c["owners"]))
        out.append({
            "key": key, "scene": c["scene"], "path": c["path"], "container": c["container"], "family": c["family"],
            "ambiguous": c["ambiguous"], "find_resolves_here": c["find_resolves_here"],
            "collider_on_root": c["collider_on_root"],
            "samples": s, "sample_count": len(s), "area_m2": len(s) * 0.0,  # filled by caller with the sample area
            "owners": sorted(c["owners"]), "layers": dict(sorted(c["layers"].items())),
            "arena_mask_visible": arena_visible,
            "centroid": [float(xyz[:, 0].mean()), float(xyz[:, 1].mean()), float(xyz[:, 2].mean())],
            "bbox": [[float(v) for v in xyz.min(axis=0)], [float(v) for v in xyz.max(axis=0)]],
        })
    out.sort(key=lambda c: -c["sample_count"])
    return out


def aggregate(clusters, field_name):
    groups: dict = defaultdict(lambda: {"samples": set(), "instances": 0})
    for c in clusters:
        g = groups[c[field_name]]
        g["samples"].update(int(s) for s in c["samples"])
        g["instances"] += 1
    rows = [{"name": k, "instances": v["instances"], "sample_count": len(v["samples"])} for k, v in groups.items()]
    rows.sort(key=lambda r: -r["sample_count"])
    return rows


def blockers_by_sample(attr_clusters, n):
    """sample -> set of cluster indices blocking it."""
    blockers: list = [set() for _ in range(n)]
    for ci, c in enumerate(attr_clusters):
        for s in c["samples"]:
            blockers[int(s)].add(ci)
    return blockers


def _local_merge(graph: SampleGraph, passable, comp, freed_set, anchor, probes):
    """Probes joined to the anchor if `freed_set` became passable (local union-find, no full relabel)."""
    parent: dict = {}

    def find(a):
        parent.setdefault(a, a)
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[rb] = ra

    for f in freed_set:
        find(("f", f))
        for nb in graph.neighbors[f]:
            if nb in freed_set:
                union(("f", f), ("f", nb))
            elif passable[nb]:
                union(("f", f), ("c", int(comp[nb])))

    def key(s):
        if comp[s] >= 0:
            return ("c", int(comp[s]))
        return ("f", s) if s in freed_set else None

    ka = key(anchor) if anchor >= 0 else None
    if ka is None:
        return 0
    ga = find(ka)
    return sum(1 for p in probes if p >= 0 and (key(p) is not None) and find(key(p)) == ga)


def greedy_exclusions(graph: SampleGraph, passable_base, supported, blockers, probes, anchor, max_steps=12):
    """Pick clusters whose exclusion joins the probe samples to the anchor's component.

    A sample is passable when it is supported and all clusters blocking it are excluded. Each step
    evaluates, for every cluster that alone unblocks some samples (given the exclusions so far), how
    many probes would be connected to the anchor, and commits the best. Returns the steps and the
    final passable array.
    """
    passable = passable_base.copy()
    excluded: set = set()
    steps = []
    total = sum(1 for p in probes if p >= 0)
    for _ in range(max_steps):
        comp, _sizes = graph.components(passable)
        now = sum(1 for p in probes if p >= 0 and anchor >= 0 and comp[anchor] >= 0 and comp[p] == comp[anchor])
        if now >= total:
            break
        candidates: dict = defaultdict(list)
        for s, bl in enumerate(blockers):
            if bl and supported[s] and not passable[s]:
                missing = bl - excluded
                if len(missing) == 1:
                    candidates[next(iter(missing))].append(s)
        best = None
        for ci, freed in candidates.items():
            joined = _local_merge(graph, passable, comp, set(freed), anchor, probes)
            gain = joined - now
            if gain > 0 and (best is None or (gain, -len(freed)) > (best[0], -len(best[2]))):
                best = (gain, ci, freed, joined)
        if best is None:
            break
        _gain, ci, freed, joined = best
        excluded.add(ci)
        for s, bl in enumerate(blockers):
            if bl and supported[s] and not passable[s] and bl <= excluded:
                passable[s] = True
        steps.append({"cluster": ci, "freed": len(freed), "joined": joined})
    return steps, passable


# ---------------------------------------------------------------------------------------------
# coverage of Arena's footprint by another NavMesh (spike bake, ExportMapTriangulation receipt)
# ---------------------------------------------------------------------------------------------

def coverage_of(mesh, pts, grid: wm.Grid, tol: float = 0.35):
    """Per sample: does a polygon of `mesh` lie within `tol` in height at (or next to) the sample?"""
    tv, _tp = mesh.fan_triangles()
    owner = np.zeros(len(tv), dtype=np.int32)
    cover = np.zeros((grid.cells, grid.words), dtype=np.uint64)
    for cell, lo, hi, _o, _s, _f in wm.iter_fragments(tv, owner, grid, 2.0, None, None):
        masks = wm.range_masks(lo, hi, grid.words)
        for w in range(grid.words):
            np.bitwise_or.at(cover[:, w], cell, masks[:, w])
    cover = wm.dilate_disc(cover, grid, grid.cs)         # one cell of edge tolerance
    ix, iz = grid.cell_of(pts[:, 0], pts[:, 2])
    inside = grid.inside(ix, iz)
    cell = np.where(inside, iz * grid.nx + ix, 0)
    return ((cover[cell] & wm.support_masks(pts[:, 1], grid, tol)) != 0).any(axis=1) & inside


def load_triangulation_mesh(path):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    verts = np.array([[v["x"], v["y"], v["z"]] for v in data["vertices"]], dtype=np.float64)
    return oracle.mesh_from_triangulation(verts, data["indices"], data.get("areas"))


def load_bundle_mesh(path):
    """NavMesh of a built `*_navmesh.bundle` (UnityPy, read-only), in world coordinates through the bundle's own
    NavMeshData placement (identity in every bundle the port has baked: Vector3.zero / Quaternion.identity)."""
    import UnityPy
    env = UnityPy.load(str(path))
    for obj in env.objects:
        if obj.type.name == "NavMeshData":
            return oracle.build_navmesh(oracle.tiles_from_navmesh_data(obj.read_typetree()))
    raise oracle.NavMeshFormatError(f"no NavMeshData in {path}")


PRESENCE_STATES = ("walked-through", "respected", "near-edge", "unknown")


def _walkable_at(pts, graph: "SampleGraph", step, origin, x, z, y_lo, y_hi, tol):
    """Is there an Arena lattice sample within `tol` (xz) of (x, z) and between y_lo and y_hi?"""
    ci = int(round((x - origin[0]) / step))
    cj = int(round((z - origin[1]) / step))
    for i in (ci - 1, ci, ci + 1):
        for j in (cj - 1, cj, cj + 1):
            for k in graph.columns.get((i, j), ()):
                if y_lo <= pts[k, 1] <= y_hi and math.hypot(pts[k, 0] - x, pts[k, 2] - z) <= tol:
                    return True
    return False


def ring_survey(cset, source_mask, pts, graph: "SampleGraph", step, origin, cfg: Config) -> dict:
    """Hole test per prop instance: is Arena-walkable floor around it while its own footprint is not?

    The ring is `hole_ring_points` points on a circle around the prop's bounding box (radius = half the
    box diagonal in xz + `hole_ring_margin`); a ring point counts when an Arena lattice sample lies within
    0.75 lattice steps of it and within [lowest collider y - 1.0, lowest collider y + 0.6]. The footprint
    test is the same query at the bounding-box centre (window: lowest y - 0.5 .. highest y). Returns
    cluster key -> {"ring": walkable ring points, "centre_walkable": bool}.
    """
    boxes: dict = {}
    for o, owner in enumerate(cset.owners):
        if not source_mask[o]:
            continue
        t = cset.tris[owner.tri_start:owner.tri_start + owner.tri_count]
        if not len(t):
            continue
        key = cluster_of(cset, owner, cfg.prop_max_objects)["key"]
        lo, hi = t.reshape(-1, 3).min(axis=0), t.reshape(-1, 3).max(axis=0)
        b = boxes.setdefault(key, [lo, hi])
        b[0], b[1] = np.minimum(b[0], lo), np.maximum(b[1], hi)
    tol = 0.75 * step
    angles = [2.0 * math.pi * i / cfg.hole_ring_points for i in range(cfg.hole_ring_points)]
    out = {}
    for key, (lo, hi) in boxes.items():
        cx, cz = 0.5 * (lo[0] + hi[0]), 0.5 * (lo[2] + hi[2])
        radius = 0.5 * math.hypot(hi[0] - lo[0], hi[2] - lo[2]) + cfg.hole_ring_margin
        ring = sum(1 for a in angles if _walkable_at(pts, graph, step, origin, cx + radius * math.cos(a),
                                                    cz + radius * math.sin(a), lo[1] - 1.0, lo[1] + 0.6, tol))
        centre = _walkable_at(pts, graph, step, origin, cx, cz, lo[1] - 0.5, hi[1], tol)
        out[key] = {"ring": int(ring), "centre_walkable": bool(centre)}
    return out


def classify_presence(cset, source_mask, attr_walked: dict, attr_near: dict, cfg: Config, ring: dict | None = None):
    """Per prop instance: what does Arena's mesh say about the port's props?

    walked-through : some Arena sample lies inside the body range of one of its colliders (Arena's bake
                     did not see it);
    respected      : not walked through, near Arena-walkable space, and a hole in open floor: at least
                     `hole_ring_min` of the `hole_ring_points` ring points around it are Arena-walkable
                     and the footprint centre is not (`ring_survey`), so Arena's bake did include it;
    near-edge      : not walked through and within 1.2 m of Arena-walkable samples, but without that open
                     floor around it (it stands against a wall or an edge of Arena's footprint): proximity
                     only, no evidence either way;
    unknown        : no Arena sample nearby (outside the footprint, upstairs, inside a closed room).
    `ring` is the result of `ring_survey`; without it no prop can be classed as respected.
    """
    ring = ring or {}
    clusters: dict = {}
    for o, owner in enumerate(cset.owners):
        if not source_mask[o]:
            continue
        info = cluster_of(cset, owner, cfg.prop_max_objects)
        c = clusters.setdefault(info["key"], {**info, "walked": 0, "near": 0, "owners": 0})
        c["owners"] += 1
        c["walked"] += int(o in attr_walked)
        c["near"] += int(o in attr_near)
    rows = []
    for key, c in clusters.items():
        r = ring.get(key)
        hole = r is not None and not r["centre_walkable"] and r["ring"] >= cfg.hole_ring_min
        if c["walked"]:
            state = "walked-through"
        elif c["near"]:
            state = "respected" if hole else "near-edge"
        else:
            state = "unknown"
        rows.append({"scene": c["scene"], "path": c["path"], "container": c["container"], "family": c["family"],
                     "state": state, "ring": None if r is None else r["ring"],
                     "centre_walkable": None if r is None else r["centre_walkable"]})
    return rows


def summarise_presence(rows, field_name, by="walked-through"):
    table: dict = defaultdict(lambda: {s: 0 for s in PRESENCE_STATES})
    for r in rows:
        table[r[field_name]][r["state"]] += 1
    out = [{"name": k, **v} for k, v in table.items()]
    if by == "walked-through":
        out.sort(key=lambda r: (-r["walked-through"], -r["respected"]))
    else:
        out.sort(key=lambda r: -(r["walked-through"] + r["respected"] + r["near-edge"]))
    return out


# ---------------------------------------------------------------------------------------------
# the analysis
# ---------------------------------------------------------------------------------------------

def _status(verdict_blocked, verdict_unsupported, k):
    if k < 0:
        return "no-sample"
    if verdict_blocked[k] and verdict_unsupported[k]:
        return "blocked+unsupported"
    if verdict_blocked[k]:
        return "blocked"
    if verdict_unsupported[k]:
        return "unsupported"
    return "passable"


def probe_reachability(probes, anchor, comp_port) -> dict:
    """How many probe points the port model puts into the anchor's component (the headline of a connectivity check).

    `probes` are the analyse() probe rows; `anchor` the sample index of the anchor (a capture point if there is one);
    `comp_port` the port-model component of every sample. A probe without a lattice sample within the probe radius
    is not counted (`located` excludes it); a blocked or unsupported one is located but has no component."""
    located = [p for p in probes if p["sample"] >= 0]
    anchor_comp = int(comp_port[anchor]) if anchor >= 0 and comp_port[anchor] >= 0 else None
    per_component: dict = defaultdict(int)
    outside = []
    for p in located:
        comp = p["model_component"]
        per_component[comp] += 1
        if anchor_comp is None or comp != anchor_comp:
            outside.append(f"{p['kind']}:{p['name']}")
    return {"probes": len(probes), "located": len(located), "anchor_sample": int(anchor), "anchor_component": anchor_comp,
            "in_anchor_component": len(located) - len(outside),
            "per_component": {str(k): v for k, v in sorted(per_component.items(), key=lambda kv: (kv[0] is None, kv[0]))},
            "outside": outside}


def descriptor_bake_inputs(descriptor: dict) -> dict:
    """The bake inputs of a tools/maps/<key>.json: opened_barriers, navmesh_bake_exclusions (PLAN-AIRPIT item 8,
    dropped from the NavMesh sources but not deactivated at runtime) and navmesh_bake_layers as a layer mask
    (None = every layer but Water). bake_bounds is not modelled: the model covers Arena's footprint only."""
    return {"barriers": list(descriptor.get("opened_barriers") or []),
            "exclusions": list(descriptor.get("navmesh_bake_exclusions") or []),
            "layer_mask": pcol.layers_to_mask(descriptor.get("navmesh_bake_layers"))}


def analyse(package, cfg: Config | None = None, opened_barriers=(), points=(), arena_data=DEFAULT_ARENA_DATA,
            mesh=None, cset=None, bake_rules="port", other_mesh=None, greedy_steps=12, log=print,
            bake_exclusions=(), port_layer_mask=None, assets=AIRPIT_NAVMESH[0], path_id=AIRPIT_NAVMESH[1]):
    """`bake_exclusions` and `port_layer_mask` are the descriptor's navmesh_bake_exclusions / navmesh_bake_layers
    (as a mask): what the port bake would collect. `cfg.arena_layer_mask` stays what Arena's own surface sees.
    `assets` / `path_id` pick the NavMeshData to load when no `mesh` is given (default AirPit's)."""
    cfg = cfg or Config()
    if mesh is None:
        mesh, meta = load_arena_mesh(arena_data, assets, path_id)
    else:
        meta = {}
    arena_components = oracle.component_summary(mesh)
    pts, polys, labels = arena_samples(mesh, cfg)
    step = cfg.sample_step
    origin = (0.5 * cfg.cell, 0.5 * cfg.cell)
    sample_area = step * step
    log(f"[diff] Arena: {mesh.poly_count} polygons, {len(arena_components)} components, main "
        f"{arena_components[0]['area_xz']:.1f} m2; {len(pts)} samples at {step} m")
    if cset is None:
        cset = pcol.load_package_colliders(package)
    pcol.mark_barriers(cset, list(opened_barriers))
    pcol.mark_bake_exclusions(cset, list(bake_exclusions))
    if bake_rules == "none":
        mask_all = np.ones(len(cset.owners), dtype=bool)
    else:
        mask_all = pcol.source_owner_mask(cset, layer_mask=port_layer_mask)
    mask_arena = (pcol.source_owner_mask(cset, layer_mask=cfg.arena_layer_mask)
                  if (cfg.arena_layer_mask is not None and bake_rules != "none") else mask_all)
    reasons: dict = defaultdict(int)
    for o in cset.owners:
        reason = pcol.bake_exclusion_reason(o)
        if reason is None and bake_rules != "none" and port_layer_mask is not None and not (port_layer_mask >> o.layer) & 1:
            reason = "not in navmesh_bake_layers"
        reasons[reason or "source"] += 1
    log(f"[diff] port colliders: {len(cset.owners)} ({int(mask_all.sum())} bake sources); unresolved barriers {cset.unresolved_barriers}, "
        f"unresolved bake exclusions {cset.unresolved_exclusions}")

    agent_a, agent_p = wm.ARENA_AGENT, wm.PORT_AGENT
    m = cfg.region_margin
    grid = wm.make_grid(pts[:, 0].min() - m, pts[:, 0].max() + m, pts[:, 2].min() - m, pts[:, 2].max() + m,
                        pts[:, 1].min() - 1.5, pts[:, 1].max() + agent_p.height + 1.0, cfg.cell, cfg.y_step)
    ylo, yhi = float(pts[:, 1].min()), float(pts[:, 1].max())
    tris, owner = _source_arrays(cset, mask_all)
    solid_ok, floor_ok = _relevance(tris, ylo, yhi, agent_p)
    solid, floor, nfrag = wm.build_occupancy(tris, owner, grid, agent_p, solid_ok, floor_ok)
    log(f"[diff] grid {grid.nx}x{grid.nz} cells, {grid.nbits} y bits, {nfrag} fragments")
    kw = dict(floor_margin=cfg.floor_margin, support_tol=cfg.support_tol)
    v_arena = wm.evaluate_samples(pts, solid, floor, grid, agent_a, shrink=cfg.arena_slack, **kw)
    v_arena_strict = wm.evaluate_samples(pts, solid, floor, grid, agent_a, shrink=0.0, **kw)
    v_port = wm.evaluate_samples(pts, solid, floor, grid, agent_p, shrink=cfg.port_slack, **kw)
    # the same with only the layers Arena's own surface collects
    if mask_arena is mask_all or np.array_equal(mask_arena, mask_all):
        v_mask = v_arena
    else:
        tris_m, owner_m = _source_arrays(cset, mask_arena)
        s_ok, f_ok = _relevance(tris_m, ylo, yhi, agent_p)
        solid_m, floor_m, _n = wm.build_occupancy(tris_m, owner_m, grid, agent_p, s_ok, f_ok)
        v_mask = wm.evaluate_samples(pts, solid_m, floor_m, grid, agent_a, shrink=cfg.arena_slack, **kw)
    # --- attribution
    attr_a = wm.attribute_blockers(tris, owner, grid, agent_a, pts, v_arena.blocked, floor_margin=cfg.floor_margin,
                                   shrink=cfg.arena_slack, solid_ok=solid_ok)
    attr_p = wm.attribute_blockers(tris, owner, grid, agent_p, pts, v_port.blocked, floor_margin=cfg.floor_margin,
                                   shrink=cfg.port_slack, solid_ok=solid_ok)
    clusters_a = rank_clusters(attr_a, cset, cfg, pts, mask_arena)
    clusters_p = rank_clusters(attr_p, cset, cfg, pts, mask_arena)
    for c in clusters_a + clusters_p:
        c["area_m2"] = c["sample_count"] * sample_area
    # --- presence survey on a coarse grid: which props does Arena's mesh respect?
    coarse = wm.make_grid(grid.x0, grid.x0 + grid.nx * grid.cs, grid.z0, grid.z0 + grid.nz * grid.cs,
                          grid.y0, grid.y0 + grid.nbits * grid.dy, 0.3, 0.2)
    near_agent = wm.AgentParams(1.2, wm.ARENA_AGENT.height, wm.ARENA_AGENT.climb)
    attr_near = wm.attribute_blockers(tris, owner, coarse, near_agent, pts, np.ones(len(pts), dtype=bool),
                                      floor_margin=cfg.floor_margin, shrink=0.0, solid_ok=solid_ok)
    graph = SampleGraph(pts, labels, step, origin)
    ring = ring_survey(cset, mask_all, pts, graph, step, origin, cfg)
    presence_rows = classify_presence(cset, mask_all, attr_a, attr_near, cfg, ring)
    # --- components
    passable = ~v_port.blocked & ~v_port.unsupported
    comp_port, sizes_port = graph.components(passable)
    # --- probes
    probes = []
    poly, surf = mesh.locate(np.array([p["position"] for p in points])) if len(points) else (np.empty(0, int), np.empty(0))
    clear = mesh.clearance(np.array([p["position"] for p in points])) if len(points) else np.empty(0)
    for i, p in enumerate(points):
        k = nearest_sample(pts, graph, step, origin, p["position"], cfg.probe_radius)
        snap = nearest_sample(pts, graph, step, origin, p["position"], 3.0, max_dy=1.5)
        probes.append({
            **p,
            "arena_poly": int(poly[i]),
            "arena_contains": bool(poly[i] >= 0),
            "arena_snap": None if snap < 0 else round(float(math.hypot(pts[snap, 0] - p["position"][0], pts[snap, 2] - p["position"][2])), 2),
            "arena_component": int(mesh.labels[poly[i]]) if poly[i] >= 0 else None,
            "arena_clearance": None if not np.isfinite(clear[i]) else round(float(clear[i]), 2),
            "sample": int(k),
            "model_status": _status(v_port.blocked, v_port.unsupported, k),
            "model_status_arena_agent": _status(v_arena.blocked, v_arena.unsupported, k),
            "model_component": int(comp_port[k]) if k >= 0 and comp_port[k] >= 0 else None,
        })
    probe_samples = [p["sample"] for p in probes]
    anchor_candidates = [p["sample"] for p in probes if p["kind"] in ("capture_point",) and p["sample"] >= 0]
    anchor = anchor_candidates[0] if anchor_candidates else next((s for s in probe_samples if s >= 0 and comp_port[s] >= 0), -1)
    reachability = probe_reachability(probes, anchor, comp_port)
    greedy = {"steps": [], "anchor": int(anchor)}
    if anchor >= 0 and probe_samples:
        blockers = blockers_by_sample(clusters_p, len(pts))
        steps, final_pass = greedy_exclusions(graph, passable, ~v_port.unsupported, blockers, probe_samples, anchor, greedy_steps)
        comp_final, _s = graph.components(final_pass)
        greedy["steps"] = [{**s, "path": clusters_p[s["cluster"]]["path"], "scene": clusters_p[s["cluster"]]["scene"],
                            "ambiguous": clusters_p[s["cluster"]]["ambiguous"]} for s in steps]
        greedy["probes_joined"] = int(sum(1 for s in probe_samples if s >= 0 and comp_final[anchor] >= 0 and comp_final[s] == comp_final[anchor]))
    # --- unsupported groups
    unsup = v_port.unsupported
    un_comp, un_sizes = graph.components(unsup)
    unsupported_groups = []
    for g in range(min(len(un_sizes), 12)):
        sel = np.nonzero(un_comp == g)[0]
        unsupported_groups.append({"samples": int(len(sel)), "area_m2": round(len(sel) * sample_area, 1),
                                   "bbox": [[round(float(v), 2) for v in pts[sel].min(axis=0)], [round(float(v), 2) for v in pts[sel].max(axis=0)]]})
    report = {
        "arena": {"polygons": mesh.poly_count, "components": arena_components[:6], "meta": {k: v for k, v in meta.items() if k != "settings"},
                  "portal_pairs": mesh.portal_pairs, "unmatched_portal_edges": mesh.unmatched_portal_edges},
        "config": {"cell": cfg.cell, "y_step": cfg.y_step, "sample_step": cfg.sample_step, "arena_slack": cfg.arena_slack, "port_slack": cfg.port_slack, "floor_margin": cfg.floor_margin, "support_tol": cfg.support_tol,
                   "arena_layer_mask": cfg.arena_layer_mask, "opened_barriers": list(opened_barriers), "bake_rules": bake_rules,
                   "bake_exclusions": list(bake_exclusions), "port_layer_mask": port_layer_mask},
        "port": {"colliders": len(cset.owners), "sources": int(mask_all.sum()), "arena_mask_sources": int(mask_arena.sum()),
                 "exclusion_reasons": dict(sorted(reasons.items())), "triangles": int(len(cset.tris)),
                 "unresolved_barriers": cset.unresolved_barriers, "unresolved_exclusions": cset.unresolved_exclusions},
        "reachability": reachability,
        "samples": {"count": int(len(pts)), "area_m2": round(len(pts) * sample_area, 1),
                    "blocked_arena_agent": int(v_arena.blocked.sum()), "blocked_arena_agent_no_slack": int(v_arena_strict.blocked.sum()),
                    "blocked_arena_agent_arena_layers": int(v_mask.blocked.sum()),
                    "blocked_port_agent": int(v_port.blocked.sum()), "unsupported": int(v_port.unsupported.sum()),
                    "passable_port": int(passable.sum())},
        "port_components": [{"id": i, "samples": int(n), "area_m2": round(float(n) * sample_area, 1)} for i, n in enumerate(sizes_port[:10])],
        "port_component_count": int(len(sizes_port)),
        "clusters_arena_agent": clusters_a, "clusters_port_agent": clusters_p,
        "families": aggregate(clusters_a, "family"), "containers": aggregate(clusters_a, "container"),
        "presence": {"instances": len(presence_rows),
                     "totals": {k: sum(1 for r in presence_rows if r["state"] == k) for k in PRESENCE_STATES},
                     "hole_test": {"ring_points": cfg.hole_ring_points, "ring_margin": cfg.hole_ring_margin,
                                   "ring_min": cfg.hole_ring_min},
                     "by_container": summarise_presence(presence_rows, "container", by="total")[:25],
                     "by_family": summarise_presence(presence_rows, "family")[:60],
                     "rows": presence_rows},
        "probes": probes, "greedy": greedy, "unsupported_groups": unsupported_groups,
        "_pts": pts, "_graph": graph, "_grid": grid, "_verdicts": {"arena": v_arena, "port": v_port},
    }
    if other_mesh is not None:
        cov = coverage_of(other_mesh, pts, grid)
        mp = passable
        report["other_mesh"] = {
            "polygons": other_mesh.poly_count, "covered_samples": int(cov.sum()),
            "model_passable_and_covered": int((mp & cov).sum()), "model_passable_not_covered": int((mp & ~cov).sum()),
            "model_blocked_but_covered": int((~mp & cov).sum()), "model_blocked_not_covered": int((~mp & ~cov).sum()),
            "components": oracle.component_summary(other_mesh)[:4],
        }
        report["_other_cov"] = cov
        if len(points):
            o_poly, _o_surf = other_mesh.locate(np.array([p["position"] for p in points]))
            for q, op in zip(report["probes"], o_poly):
                q["other_contains"] = bool(op >= 0)
                q["other_component"] = int(other_mesh.labels[op]) if op >= 0 else None
            found = [q["other_component"] for q in report["probes"] if q["other_contains"]]
            report["other_mesh"]["probes_inside"] = len(found)
            report["other_mesh"]["probe_components"] = {str(k): found.count(k) for k in sorted(set(found))}
    return report


# ---------------------------------------------------------------------------------------------
# text report / CLI
# ---------------------------------------------------------------------------------------------

def _plain(report):
    """JSON-safe copy (drops the private numpy handles, converts keys and arrays)."""
    def conv(x):
        if isinstance(x, dict):
            return {str(k): conv(v) for k, v in x.items() if not str(k).startswith("_")}
        if isinstance(x, (list, tuple)):
            return [conv(v) for v in x]
        if isinstance(x, np.ndarray):
            return x.tolist() if x.size < 50 else {"count": int(x.size)}
        if isinstance(x, (np.integer,)):
            return int(x)
        if isinstance(x, (np.floating,)):
            return float(x)
        if isinstance(x, ClusterKey):
            return f"{x.scene}:{x.root_id}"
        return x
    return conv(report)


def format_report(report, top=25) -> str:
    out = []
    a, s, p = report["arena"], report["samples"], report["port"]
    out.append("== Arena NavMesh")
    meta = a.get("meta") or {}
    if "rotation" in meta and "position" in meta:
        placed = oracle.Placement(tuple(meta["position"]), tuple(meta["rotation"]))
        if not placed.is_identity:
            out.append(f"NavMeshData placement applied: yaw {placed.yaw_degrees():.2f} deg, position "
                       f"({placed.position[0]:.2f}, {placed.position[1]:.2f}, {placed.position[2]:.2f})")
    out.append(f"polygons {a['polygons']}, components {len(a['components'])}+ (largest first):")
    for c in a["components"][:4]:
        out.append(f"  #{c['label']}: {c['polygons']} polys, {c['area_xz']:.1f} m2, x {c['min'][0]:.1f}..{c['max'][0]:.1f}, "
                   f"y {c['min'][1]:.2f}..{c['max'][1]:.2f}, z {c['min'][2]:.1f}..{c['max'][2]:.1f}")
    out.append("== Port model on Arena's footprint")
    out.append(f"{p['colliders']} colliders ({p['sources']} bake sources, {p['arena_mask_sources']} on Arena's layers); exclusions {p['exclusion_reasons']}")
    out.append(f"samples {s['count']} ({s['area_m2']} m2): blocked by a solid under Arena's agent {s['blocked_arena_agent']} "
               f"(no slack {s['blocked_arena_agent_no_slack']}, only Arena's layers {s['blocked_arena_agent_arena_layers']}), "
               f"under the port agent {s['blocked_port_agent']}, without floor {s['unsupported']}, passable {s['passable_port']}")
    out.append(f"port-model components {report['port_component_count']}: " + ", ".join(
        f"#{c['id']} {c['area_m2']} m2" for c in report["port_components"][:6]))
    cfg_r, reach = report["config"], report["reachability"]
    mask = cfg_r["port_layer_mask"]
    out.append(f"bake inputs: {len(cfg_r['opened_barriers'])} opened_barriers, {len(cfg_r['bake_exclusions'])} navmesh_bake_exclusions, "
               f"layer mask {'every layer but Water' if mask is None else hex(mask)}")
    if reach["located"]:
        out.append(f"probe reachability: {reach['in_anchor_component']} of {reach['located']} located probes ({reach['probes']} given) "
                   f"in the anchor's port-model component {reach['anchor_component']}; per component {reach['per_component']}"
                   + (f"; outside: {reach['outside'][:10]}" if reach["outside"] else ""))
    ca = report["clusters_arena_agent"]
    out.append(f"== Solids Arena walks through: {len(ca)} prop instances, {sum(len(c['owners']) for c in ca)} collider objects, "
               f"{sum(c['area_m2'] for c in ca):.1f} m2 of samples counted per instance (top {top})")
    for c in report["clusters_arena_agent"][:top]:
        flag = ("" if not c["ambiguous"] else " [duplicate sibling name]") + (" [collider on the root object itself]" if c["collider_on_root"] else "")
        out.append(f"  {c['area_m2']:6.2f} m2  layers {c['layers']}  {'Arena-visible' if c['arena_mask_visible'] else 'hidden-to-Arena-layers'}  "
                   f"{c['scene']}::{c['path']}{flag}")
    out.append("== by family (top 20)")
    for r in report["families"][:20]:
        out.append(f"  {r['sample_count'] * report['config']['sample_step'] ** 2:7.2f} m2  {r['instances']:3d} instances  {r['name']}")
    pr = report["presence"]
    ht = pr["hole_test"]
    out.append(f"== What does Arena's mesh say about the port's props? {pr['instances']} prop instances: {pr['totals']} "
               f"(respected = hole in open floor: >= {ht['ring_min']} of {ht['ring_points']} ring points walkable, "
               f"centre not; near-edge = near walkable space without that evidence)")
    for r in pr["by_container"][:14]:
        out.append(f"  walked-through {r['walked-through']:4d}  respected {r['respected']:4d}  near-edge {r['near-edge']:4d}  "
                   f"unknown {r['unknown']:4d}  {r['name']}")
    out.append("== presence by family (walked-through first)")
    for r in pr["by_family"][:30]:
        out.append(f"  walked-through {r['walked-through']:4d}  respected {r['respected']:4d}  near-edge {r['near-edge']:4d}  "
                   f"unknown {r['unknown']:4d}  {r['name']}")
    out.append("== by container")
    for r in report["containers"][:12]:
        out.append(f"  {r['sample_count'] * report['config']['sample_step'] ** 2:7.2f} m2  {r['instances']:3d} instances  {r['name']}")
    out.append("== Probe points")
    for q in report["probes"]:
        where = "in Arena mesh" if q["arena_contains"] else f"outside Arena mesh (nearest walkable {q['arena_snap']} m)"
        out.append(f"  {q['kind']:13s} {q['name']:22s} {[round(v, 2) for v in q['position']]}  {where}, comp {q['arena_component']}, "
                   f"border clearance {q['arena_clearance']}  | port model: {q['model_status']} (Arena-agent: {q['model_status_arena_agent']}), comp {q['model_component']}")
    if report["greedy"]["steps"]:
        out.append("== Greedy exclusions joining the probes (port agent)")
        for st in report["greedy"]["steps"]:
            out.append(f"  +{st['freed']} samples, probes joined {st['joined']}: {st['scene']}::{st['path']}" + (" [ambiguous]" if st["ambiguous"] else ""))
        out.append(f"  probes joined to the anchor after the list: {report['greedy'].get('probes_joined')} of {sum(1 for q in report['probes'] if q['sample'] >= 0)}")
    if report["unsupported_groups"]:
        out.append("== Arena-walkable samples without port floor (groups)")
        for g in report["unsupported_groups"][:8]:
            out.append(f"  {g['area_m2']} m2 bbox {g['bbox']}")
    if "other_mesh" in report:
        o = report["other_mesh"]
        out.append(f"== Other NavMesh (built bundle / triangulation): {o['polygons']} polygons; covers {o['covered_samples']} of "
                   f"{s['count']} Arena samples; model agreement: passable&covered {o['model_passable_and_covered']}, "
                   f"passable but not covered {o['model_passable_not_covered']}, blocked but covered {o['model_blocked_but_covered']}, "
                   f"blocked and not covered {o['model_blocked_not_covered']}")
        if "probe_components" in o:
            out.append(f"   probes inside it: {o['probes_inside']} of {len(report['probes'])}, per component {o['probe_components']}")
    return "\n".join(out)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--map", choices=sorted(ARENA_SOURCES), default="airpit",
                    help="per-map Arena inputs (ARENA_SOURCES): NavMeshData, NavMeshSurface layer mask, AI scene and the default "
                         "package; --package / --assets / --path-id / --arena-layer-mask override single values")
    ap.add_argument("--plan", help="source plan JSON (tools/extract/plans/<stem>.json): its `ai` scene role replaces the AI scene "
                                   "(--live-core-points) of the selected --map")
    ap.add_argument("--package", default=None, help="extracted package with scenes/*.json and meshes/ (default: the --map's row)")
    ap.add_argument("--arena-data", default=DEFAULT_ARENA_DATA)
    ap.add_argument("--assets", default=None, help="Arena assets file holding the NavMeshData (default: the --map's row)")
    ap.add_argument("--path-id", type=int, default=None, help="path id of the NavMeshData in --assets (default: the --map's row)")
    ap.add_argument("--opened-barrier", action="append", default=[], help="extra opened_barriers path (repeatable)")
    ap.add_argument("--descriptor", help="map sidecar whose opened_barriers, navmesh_bake_exclusions and navmesh_bake_layers "
                                         "are applied (tools/maps/airpit.json)")
    ap.add_argument("--bake-exclusion", action="append", default=[],
                    help="extra navmesh_bake_exclusions path: subtree dropped from the bake sources, still active at runtime (repeatable)")
    ap.add_argument("--port-layer-mask", type=lambda v: int(v, 0),
                    help="layer mask the port bake collects from (overrides the descriptor's navmesh_bake_layers; default: every layer but Water)")
    ap.add_argument("--ignore-descriptor-layers", action="store_true",
                    help="apply the descriptor's barriers and exclusions but not its navmesh_bake_layers (A/B against the layer mask)")
    ap.add_argument("--points", action="append", default=[], help="probe-point JSON / bots / exits sidecar (repeatable)")
    ap.add_argument("--bake-rules", choices=("port", "none"), default="port",
                    help="'none' keeps every enabled non-trigger collider (reproduces a bake that predates the exclusions)")
    ap.add_argument("--arena-layer-mask", type=lambda v: int(v, 0), default=None,
                    help="Arena's NavMeshSurface layer mask (default: the --map's row)")
    ap.add_argument("--components", choices=("main", "low"), default="main")
    ap.add_argument("--cell", type=float, default=0.1)
    ap.add_argument("--sample-step", type=float, default=0.2)
    ap.add_argument("--slack", type=float, default=0.1, help="radius allowance for both agent tests")
    ap.add_argument("--live-core-points", action="store_true",
                    help="add Arena's AI core points / patrol points, read from the live install (the --map's TeamFight_AI scene, "
                         "or the --plan's `ai` role)")
    ap.add_argument("--compare-bundle", help="a built *_navmesh.bundle to compare against Arena's footprint")
    ap.add_argument("--compare-triangulation", help="ExportMapTriangulation receipt to compare against Arena's footprint")
    ap.add_argument("--top", type=int, default=25)
    ap.add_argument("--json-out")
    args = ap.parse_args(argv)
    source = ARENA_SOURCES[args.map]
    if args.plan:
        planned = arena_source_from_plan(args.plan, source)
        owner = next((stem for stem, row in ARENA_SOURCES.items() if row.key == planned.key), None)
        if owner is not None and planned.key != source.key:
            # AI points of one map on the NavMeshData of another would report most of them as off the mesh, silently
            ap.error(f"--plan {args.plan} belongs to {planned.key}, which has its own ARENA_SOURCES row: use --map {owner}")
        source = planned
    package = args.package or source.package
    assets = args.assets or source.assets
    path_id = args.path_id if args.path_id is not None else source.path_id
    arena_layer_mask = args.arena_layer_mask if args.arena_layer_mask is not None else source.layer_mask
    cfg = Config(cell=args.cell, sample_step=args.sample_step, arena_slack=args.slack, port_slack=args.slack,
                 components=args.components, arena_layer_mask=arena_layer_mask)
    barriers = list(args.opened_barrier)
    exclusions = list(args.bake_exclusion)
    layer_mask = args.port_layer_mask
    if args.descriptor:
        inputs = descriptor_bake_inputs(json.loads(Path(args.descriptor).read_text(encoding="utf-8")))
        barriers += inputs["barriers"]
        exclusions += inputs["exclusions"]
        if layer_mask is None and not args.ignore_descriptor_layers:
            layer_mask = inputs["layer_mask"]
    points = []
    for f in args.points:
        points += load_points(f)
    if args.live_core_points:
        points += live_ai_points(args.arena_data, source.ai_scene_index, source.ai_scene_name, source.key)
    other = None
    if args.compare_bundle:
        other = load_bundle_mesh(args.compare_bundle)
    elif args.compare_triangulation:
        other = load_triangulation_mesh(args.compare_triangulation)
    report = analyse(package, cfg, barriers, points, args.arena_data, bake_rules=args.bake_rules, other_mesh=other,
                     bake_exclusions=exclusions, port_layer_mask=layer_mask, assets=assets, path_id=path_id)
    print(format_report(report, args.top))
    if args.json_out:
        Path(args.json_out).parent.mkdir(parents=True, exist_ok=True)
        Path(args.json_out).write_text(json.dumps(_plain(report), indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
