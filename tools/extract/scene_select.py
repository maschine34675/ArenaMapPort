"""Partial scenes: import only the reviewed part of an Arena level as its own content scene.

What. A plan ``scenes`` record may carry ``select`` (schema below). The record then names a
*partial scene*: the extractor reads the whole level file, keeps the selected hierarchy and
writes it as an ordinary ``scenes/<name>.json``. AirPit uses this for the furnished shop
interiors of ``Arena_Airpit_Main_hall`` (level38) and the matching lights of
``Arena_Airpit_Light`` (level37): Arena's later TeamFight build ships those levels with the
shops fully furnished, while the ShootOut levels the port imports (level324/325) carry only
parts of them. Importing the two levels whole would double every object they share with
324/325, so the selection skips the *twin* of anything the reference scene already has.

Not a slice. ``extract_equator.py --slice-roots`` makes a slice root a scene root, loses the
parent chain, and is refused by the generic validators on purpose. A partial scene keeps the
real parent chain (as component-free *carriers*), so world positions, hierarchy paths and the
ballistic owner/ancestor activity checks stay valid, and the manifest records it in
``partial_scenes`` while ``slice`` stays ``{}``.

Schema. ``select`` = ``{"schema": 1, "reason": str, "skip_twins_of": <index>, "twin_descendants": "skip" |
"import_missing", "roots": [path], "region_roots": [path | {"path": path, "regions": [name]}],
"regions": [{"name", "min", "max"}], "exclude": [{"path", "at", "reason"}]}``.
Paths are hierarchy paths from the level root (``Root/Child/...``); names are exact, a trailing
space included, and a path that several siblings share names all of them (Arena has two
``BUILDING`` siblings). ``roots`` take every unit below the path; ``region_roots`` take only the units whose root position lies inside one
of the regions (a bare path uses every declared region, the object form a named subset).
``skip_twins_of`` is the BuildSettings index of an earlier, full scene of the same plan.

Units. The selection granularity is a *unit*: a LODGroup subtree, or a lone leaf that carries a
renderer, collider, Light, Rigidbody or ParticleSystem, or one of the renderer-less native scripts
of ``UNIT_SCRIPTS`` (a StaticDeferredDecal needs no MeshRenderer); children of a lone leaf belong to it.
Everything above a unit is a *container*. A selected unit always comes with its whole subtree;
a ``roots`` path that points inside a unit is refused because half a LODGroup cannot be
imported. The ancestors of every selected unit are emitted as carriers: the GameObject with
its own active flag, layer and local TRS and no components (``structure_problems`` refuses a
scene whose carrier gained one, so a future Arena update cannot slip a MeshFilter onto a container).

Rules. Each ``roots`` / ``region_roots`` entry is one rule, tried in the order roots first, then
region roots, in plan order; the first rule that matches a unit claims it. The manifest reports
``rules[<kind>:<path>] = {selected, twin_skipped, excluded, descendants_added}``. Every counter counts units
and each one belongs to a step: ``selected`` is the claimed units that were not twin-skipped (counted before the
two later steps), ``twin_skipped`` the claimed units skipped as twins, ``descendants_added`` the twin descendants
that ``twin_descendants`` imported below twin-skipped units (attributed to the rule that claimed the unit they sit
in) and ``excluded`` the units ``exclude`` dropped again, imported descendants included (attributed likewise).
So per rule ``selected + descendants_added - excluded`` units are left and
``units = sum(selected) + sum(descendants_added) - sum(excluded)``; the totals ``twin_descendants_added`` and
``excluded`` repeat the sums. tools/extract/native_contract_loader.py spells the same definition out for the pins.

Twins. A claimed unit is *twin-skipped* when the reference scene has a still unused unit root
with the same name once a trailing `` (N)`` is stripped, the same multiset of
non-MonoBehaviour component types, the same effective (in-hierarchy) activity, and a world
position within ``TWIN_TOLERANCE`` on every axis. Candidates are matched greedily in
(path, transform id) order, each reference unit once, nearest first, so the result does not
depend on the order the level file lists its objects. The signature leaves every MonoBehaviour
out; script classes only matter for ``UNIT_SCRIPTS`` and the dropped-owner count below.

Twin descendants. ``"twin_descendants": "import_missing"`` (needs ``skip_twins_of``; the default
``"skip"`` is the behaviour above) recovers what a twin lacks. For every claimed unit U that was
twin-skipped against the reference unit R, each node D below U that carries content of its own (the unit
rules above applied as if U and everything between were containers: LODGroup, renderer, collider, Light,
ParticleSystem, Rigidbody or a ``UNIT_SCRIPTS`` script) and has no counterpart below R becomes a unit of its
own with its whole subtree. A counterpart is a node below R with the same base name (`` (N)`` stripped), the
same component multiset, the same effective activity and a world position within ``TWIN_TOLERANCE`` on every
axis; candidates go in (path, transform id) order and take the nearest unused counterpart, so the result does
not depend on the table order. A content node inside an emitted D belongs to it. The chain from D up to and
including U is emitted as *twin carriers*: GameObjects with the source name, active flag, layer and local TRS
(D keeps its exact world pose and hierarchy path) but no components, although the source nodes have some
(U's LODGroup, renderers). They are the only carriers whose source carries components: ``extract_map``
emits them bare, their MonoBehaviours stay outside the augmenter scope (``Selection.scope_go_ids``), and
``structure_problems`` still refuses a scene in which any carrier, twin carriers included, has a component.

Exclusions. ``exclude`` removes reviewed duplicates after claiming, twin matching and twin descendants. An
entry names a node by its exact hierarchy path (a unit root, or a container above several units such as a
prefab instance; an added twin descendant also qualifies) and drops every selected unit, added descendants
included, whose root is that node or lies below it; carriers are recomputed from the units that remain.
``at`` (a world position, matched within ``EXCLUDE_AT_TOLERANCE``) picks one of several nodes that share the
path and is required when the path is not unique in the level. An entry that resolves to no node, is
ambiguous, points inside a unit, resolves to a node another entry already names or drops no unit raises
``HardExtractError``: the list is the fingerprint of one reviewed Arena build, so an update that moves or
removes a node fails in the extractor instead of silently importing the duplicate again.

Dropped owners. A container that lies in a claimed rule scope (under the rule path, and inside a
region for a region root), owns a MonoBehaviour and is neither a unit nor a carrier loses that script:
carriers are emitted without components. ``select_nodes`` counts these containers as
``dropped_script_owners`` (reported in the manifest ``partial_scenes`` block and pinned by the map
contract, 0 for both AirPit scenes), so a script that ``UNIT_SCRIPTS`` does not cover cannot vanish
unnoticed when a later Arena update adds one.

Native augmenters. The five per-scene augmenters (ballistics, NavMesh modifiers, decals,
SmallPhysicsObject, light culling) scan every MonoBehaviour of the level file and hard-fail
when an owner is not in the scene JSON. ``ScopedFile`` is the view of the level file they get:
scans see only the MonoBehaviours whose owner was extracted, lookups by id still reach the
whole file. A MonoBehaviour head that cannot be read, or an owner outside the level file, is an
extraction failure and never silently out of scope. A BallisticCollider on an owner without any physical collider (four
``video_player_LOD0`` meshes in level38) can never satisfy the augmenter's owner-physics check;
``ScopedFile`` hides it and records ``(game object id, component id)`` so the manifest keeps
the loss visible.

Failures. ``validate_select`` raises ``PlanError`` (a malformed record); everything that needs
the level raises ``HardExtractError`` (an unresolved root, a root inside a unit, an empty
selection, a missing reference scene, a stale exclusion), so a drifted Arena update fails in the
extractor and not in Unity.
"""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import math
import re

import extract_ballistics
from common_ids import HardExtractError
from source_plan import PlanError, finite_vector

SELECT_SCHEMA = 1
SELECT_KEYS = frozenset(("schema", "reason", "skip_twins_of", "twin_descendants", "roots", "region_roots",
                         "regions", "exclude"))
TWIN_DESCENDANTS_SKIP = "skip"
TWIN_DESCENDANTS_IMPORT = "import_missing"
TWIN_DESCENDANT_MODES = (TWIN_DESCENDANTS_SKIP, TWIN_DESCENDANTS_IMPORT)
# Per-axis distance under which two same-signature unit roots are the same object in two levels.
TWIN_TOLERANCE = 0.02
# Distance (metres, euclidean) within which an exclude entry's ``at`` selects a node.
EXCLUDE_AT_TOLERANCE = 0.05
_FLOAT_SLACK = 1e-9  # keeps a pair that sits exactly on the tolerance in, whatever the float32 round trip did
_CELL = 0.1          # twin lookup grid; any cell size >= TWIN_TOLERANCE makes the 27 neighbours exhaustive
# Components that make a lone GameObject a unit of its own (LODGroup roots are units by themselves).
CONTENT_TYPES = frozenset(("MeshRenderer", "SkinnedMeshRenderer", "MeshCollider", "BoxCollider",
                           "SphereCollider", "CapsuleCollider", "ParticleSystem", "Rigidbody", "Light"))
LOD_TYPE = "LODGroup"
# Native scripts that make a GameObject without any renderer or collider a unit of its own: the augmenters
# restore them on the owner, and extract_decals documents that a decal needs no MeshRenderer (AirPit's level38
# has two such spray quads). Spelled out here so the module stays free of UnityPy; test_scene_select pins the
# name to extract_decals.SOURCE_TYPE.
UNIT_SCRIPTS = frozenset(("EFT.Visual.StaticDecal.StaticDeferredDecal",))
_COPY_SUFFIX = re.compile(r"\s*\(\d+\)\s*$")
_IGNORED_SIGNATURE_TYPES = frozenset(("Transform", "MonoBehaviour"))


# --------------------------------------------------------------------------
# Plan record
# --------------------------------------------------------------------------

@dataclass(frozen=True)
class Region:
    name: str
    lo: tuple
    hi: tuple

    def contains(self, position) -> bool:
        """Inclusive on every face."""
        return all(l <= p <= h for l, p, h in zip(self.lo, position, self.hi))


@dataclass(frozen=True)
class Rule:
    kind: str                 # "roots" | "region_roots"
    path: str
    regions: tuple = ()       # Region objects; empty for "roots"

    @property
    def key(self) -> str:
        return f"{self.kind}:{self.path}"


@dataclass(frozen=True)
class Exclusion:
    path: str
    at: tuple | None          # world position that picks one of several nodes sharing the path
    reason: str


@dataclass(frozen=True)
class SelectSpec:
    reason: str
    skip_twins_of: int | None
    rules: tuple              # Rule objects in claim order: roots first, then region roots
    regions: tuple            # every declared Region, in declaration order
    twin_descendants: str = TWIN_DESCENDANTS_SKIP
    exclude: tuple = ()       # Exclusion objects in plan order


def _check_path(value, where: str) -> str:
    if (not isinstance(value, str) or not value.strip() or value.startswith("/") or value.endswith("/")
            or "//" in value or "\x00" in value):
        raise PlanError(f"{where}: invalid hierarchy path {value!r} (non-empty, no leading/trailing/double '/')")
    return value


def _check_keys(value, allowed: set, required: set, where: str) -> None:
    if not isinstance(value, dict):
        raise PlanError(f"{where} must be an object")
    unknown = set(value) - allowed
    if unknown:
        raise PlanError(f"{where} has unknown key(s) {sorted(unknown)}")
    missing = required - set(value)
    if missing:
        raise PlanError(f"{where} is missing {sorted(missing)}")


def _check_exclude(raw, where: str) -> tuple:
    """The ``exclude`` list as Exclusion objects; every entry needs a path and a reason, ``at`` is optional."""
    if not isinstance(raw, list):
        raise PlanError(f"{where} must be an array")
    entries, seen = [], set()
    for i, item in enumerate(raw):
        label = f"{where}[{i}]"
        _check_keys(item, {"path", "at", "reason"}, {"path", "reason"}, label)
        path = _check_path(item["path"], label)
        reason = item["reason"]
        if not isinstance(reason, str) or not reason.strip():
            raise PlanError(f"{label}: a reason is required")
        at = tuple(finite_vector(item["at"], f"{label} at")) if "at" in item else None
        if (path, at) in seen:
            raise PlanError(f"{where}: {path!r}{'' if at is None else ' at ' + str(list(at))} is listed twice")
        seen.add((path, at))
        entries.append(Exclusion(path, at, reason))
    return tuple(entries)


def _check_region(raw, where: str) -> Region:
    _check_keys(raw, {"name", "min", "max"}, {"name", "min", "max"}, where)
    name = raw["name"]
    if not isinstance(name, str) or not name.strip():
        raise PlanError(f"{where}: region name must be a non-empty string")
    lo = finite_vector(raw["min"], f"{where} {name!r} min")
    hi = finite_vector(raw["max"], f"{where} {name!r} max")
    if any(l >= h for l, h in zip(lo, hi)):
        raise PlanError(f"{where}: region {name!r} needs min < max on every axis")
    return Region(name, tuple(lo), tuple(hi))


def validate_select(select, where: str = "select") -> SelectSpec:
    """Check one ``select`` object and return it in the form ``select_nodes`` uses.

    Everything checkable without the level happens here (``source_plan.validate_plan`` also
    requires ``skip_twins_of`` to name an earlier full scene). Raises ``PlanError``.
    """
    _check_keys(select, set(SELECT_KEYS), {"schema", "reason"}, where)
    if type(select["schema"]) is not int or select["schema"] != SELECT_SCHEMA:
        raise PlanError(f"{where}: schema must be {SELECT_SCHEMA}")
    reason = select["reason"]
    if not isinstance(reason, str) or not reason.strip():
        raise PlanError(f"{where}: a reason is required")
    twins = select.get("skip_twins_of")
    if twins is not None and (type(twins) is not int or twins < 0):
        raise PlanError(f"{where}: skip_twins_of must be a BuildSettings index")
    mode = select.get("twin_descendants", TWIN_DESCENDANTS_SKIP)
    if not isinstance(mode, str) or mode not in TWIN_DESCENDANT_MODES:
        raise PlanError(f"{where}: twin_descendants must be one of {list(TWIN_DESCENDANT_MODES)}, got {mode!r}")
    if mode == TWIN_DESCENDANTS_IMPORT and twins is None:
        raise PlanError(f"{where}: twin_descendants {mode!r} needs skip_twins_of (there is no twin to compare with)")
    exclude = _check_exclude(select.get("exclude", []), f"{where}.exclude")

    declared = select.get("regions", [])
    if not isinstance(declared, list):
        raise PlanError(f"{where}: regions must be an array")
    regions = tuple(_check_region(raw, f"{where}.regions[{i}]") for i, raw in enumerate(declared))
    by_name = {}
    for region in regions:
        if region.name in by_name:
            raise PlanError(f"{where}: duplicate region name {region.name!r}")
        by_name[region.name] = region

    roots = select.get("roots", [])
    region_roots = select.get("region_roots", [])
    if not isinstance(roots, list) or not isinstance(region_roots, list):
        raise PlanError(f"{where}: roots and region_roots must be arrays")
    if not roots and not region_roots:
        raise PlanError(f"{where}: selects nothing (no roots and no region_roots)")
    rules, seen_paths, used = [], set(), set()
    for i, path in enumerate(roots):
        _check_path(path, f"{where}.roots[{i}]")
        if path in seen_paths:
            raise PlanError(f"{where}: path {path!r} is listed twice")
        seen_paths.add(path)
        rules.append(Rule("roots", path))
    for i, item in enumerate(region_roots):
        label = f"{where}.region_roots[{i}]"
        if isinstance(item, str):
            path, names = _check_path(item, label), [r.name for r in regions]
        else:
            _check_keys(item, {"path", "regions"}, {"path", "regions"}, label)
            path = _check_path(item["path"], label)
            names = item["regions"]
            if (not isinstance(names, list) or not names or any(not isinstance(n, str) for n in names)
                    or len(set(names)) != len(names)):
                raise PlanError(f"{label}: regions must be a non-empty list of distinct region names")
            unknown = [n for n in names if n not in by_name]
            if unknown:
                raise PlanError(f"{label}: unknown region name(s) {unknown}")
        if not names:
            raise PlanError(f"{label}: a region root needs at least one declared region")
        if path in seen_paths:
            raise PlanError(f"{where}: path {path!r} is listed twice")
        seen_paths.add(path)
        used.update(names)
        rules.append(Rule("region_roots", path, tuple(by_name[n] for n in names)))
    unused = [r.name for r in regions if r.name not in used]
    if unused:
        raise PlanError(f"{where}: region(s) {unused} are not used by any region root")
    return SelectSpec(reason, twins, tuple(rules), regions, mode, exclude)


# --------------------------------------------------------------------------
# Level graph
# --------------------------------------------------------------------------

class Node:
    """One GameObject/Transform pair of a level, with everything the selection needs."""
    __slots__ = ("tf_id", "go_id", "name", "path", "layer", "self_active", "active", "pos", "types", "scripts",
                 "components", "parent", "children", "unit")

    def __init__(self, tf_id, go_id, name, layer, self_active):
        self.tf_id, self.go_id, self.name, self.layer, self.self_active = tf_id, go_id, name, layer, self_active
        self.path = name
        self.active = self_active
        self.pos = (0.0, 0.0, 0.0)
        self.types = ()           # sorted multiset of component type names (Transform/MonoBehaviour left out)
        self.scripts = ()         # sorted script class of every MonoBehaviour ("" when unresolved)
        self.components = ()      # component path ids of the GameObject, in file order
        self.parent = None        # parent tf id, None for a level root
        self.children = []        # child tf ids, ascending
        self.unit = None          # tf id of the unit root this node belongs to; None for a container

    @property
    def is_unit_root(self) -> bool:
        return self.unit == self.tf_id

    @property
    def has_content(self) -> bool:
        """True for a node that makes a unit of its own wherever no ancestor already did."""
        return bool(LOD_TYPE in self.types or CONTENT_TYPES.intersection(self.types)
                    or UNIT_SCRIPTS.intersection(self.scripts))


def _rotation(q):
    x, y, z, w = q
    n = x * x + y * y + z * z + w * w
    if n == 0:
        return (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)
    s = 2.0 / n
    xx, yy, zz, xy, xz, yz = x * x * s, y * y * s, z * z * s, x * y * s, x * z * s, y * z * s
    wx, wy, wz = w * x * s, w * y * s, w * z * s
    return (1 - (yy + zz), xy - wz, xz + wy,
            xy + wz, 1 - (xx + zz), yz - wx,
            xz - wy, yz + wx, 1 - (xx + yy))


def _local_linear(rot, scale):
    r, (sx, sy, sz) = _rotation(rot), scale
    return (r[0] * sx, r[1] * sy, r[2] * sz, r[3] * sx, r[4] * sy, r[5] * sz, r[6] * sx, r[7] * sy, r[8] * sz)


def _mul(a, b):
    return tuple(a[3 * i] * b[j] + a[3 * i + 1] * b[3 + j] + a[3 * i + 2] * b[6 + j]
                 for i in range(3) for j in range(3))


def _apply(a, v):
    return tuple(a[3 * i] * v[0] + a[3 * i + 1] * v[1] + a[3 * i + 2] * v[2] for i in range(3))


class Graph:
    """The hierarchy of one level: paths, world positions, effective activity and unit assignment.

    ``from_tables`` takes the tables ``extract_equator.build_scene_graph`` returns (so tests need
    no Unity file); ``build_graph`` fills them from a UnityPy level file.
    """

    def __init__(self, nodes: dict):
        self.nodes = nodes
        self.by_path: dict[str, list[int]] = {}
        for tf_id in sorted(nodes):
            self.by_path.setdefault(nodes[tf_id].path, []).append(tf_id)
        self.unit_roots = sorted((n for n in nodes.values() if n.is_unit_root), key=lambda n: (n.path, n.tf_id))

    @classmethod
    def from_tables(cls, go_map: dict, tf_map: dict, type_of, script_of=None) -> "Graph":
        """``go_map``/``tf_map`` as ``build_scene_graph`` returns them; ``type_of(component id)``
        gives the component's Unity type name (None for a component missing from the file) and
        ``script_of(component id)`` the script class of a MonoBehaviour (None when unresolved or
        without a resolver, in which case no script can make a unit)."""
        nodes = {}
        for tf_id in sorted(tf_map):
            tf = tf_map[tf_id]
            go = go_map.get(tf["go_id"])
            if go is None:
                continue  # extract_scene skips a transform without a GameObject too
            nodes[tf_id] = Node(tf_id, tf["go_id"], go["name"], go["layer"], go["active"])
            types, scripts = [], []
            for cid in go["components"]:
                kind = type_of(cid)
                if kind == "MonoBehaviour":
                    scripts.append((script_of(cid) if script_of is not None else None) or "")
                elif kind and kind not in _IGNORED_SIGNATURE_TYPES:
                    types.append(kind)
            nodes[tf_id].types = tuple(sorted(types))
            nodes[tf_id].scripts = tuple(sorted(scripts))
            nodes[tf_id].components = tuple(go["components"])
        roots = []
        for tf_id, node in nodes.items():
            father = tf_map[tf_id]["father_id"]
            if father in nodes and father != tf_id:
                node.parent = father
                nodes[father].children.append(tf_id)
            else:
                roots.append(tf_id)
        for node in nodes.values():
            node.children.sort()
        reached = 0
        identity = (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)
        # Iterative top-down walk: path, effective activity, world position and unit assignment.
        stack = [(tf_id, identity, (0.0, 0.0, 0.0), None) for tf_id in sorted(roots, reverse=True)]
        while stack:
            tf_id, parent_linear, parent_pos, parent_unit = stack.pop()
            node, tf = nodes[tf_id], tf_map[tf_id]
            reached += 1
            if node.parent is not None:
                parent = nodes[node.parent]
                node.path = parent.path + "/" + node.name
                node.active = parent.active and node.self_active
            offset = _apply(parent_linear, tf["pos"])
            node.pos = (parent_pos[0] + offset[0], parent_pos[1] + offset[1], parent_pos[2] + offset[2])
            linear = _mul(parent_linear, _local_linear(tf["rot"], tf["scale"]))
            unit = parent_unit
            if unit is None and node.has_content:
                unit = tf_id
            node.unit = unit
            for child in reversed(node.children):
                stack.append((child, linear, node.pos, unit))
        if reached != len(nodes):
            raise HardExtractError(f"scene_select: {len(nodes) - reached} transform(s) form a hierarchy cycle")
        return cls(nodes)

    def subtree(self, tf_id: int) -> list[int]:
        out, stack = [], [tf_id]
        while stack:
            cur = stack.pop()
            out.append(cur)
            stack.extend(self.nodes[cur].children)
        return out

    def ancestors(self, tf_id: int):
        cur = self.nodes[tf_id].parent
        while cur is not None:
            yield cur
            cur = self.nodes[cur].parent


def build_graph(unity_file) -> Graph:
    """Build the Graph of a UnityPy level file (the same tables extract_scene reads)."""
    import extract_equator as EQ
    go_map, tf_map, _go_of_tf = EQ.build_scene_graph(unity_file)
    objects = unity_file.objects

    def type_of(component_id):
        reader = objects.get(component_id)
        return reader.type.name if reader is not None else None

    def script_of(component_id):
        name, status = EQ.arena_census.resolve_script_name(objects[component_id])
        return name if status == "ok" else None
    return Graph.from_tables(go_map, tf_map, type_of, script_of)


# --------------------------------------------------------------------------
# Twins
# --------------------------------------------------------------------------

def strip_copy_suffix(name: str) -> str:
    """``"Chair (12)"`` -> ``"Chair"`` (Unity's duplicate counter); nothing else is touched."""
    return _COPY_SUFFIX.sub("", name).strip()


def _twin_key(node: Node):
    return strip_copy_suffix(node.name), node.types, node.active


def _cell(position):
    return tuple(math.floor(v / _CELL) for v in position)


def _twin_index(nodes) -> dict:
    index: dict[tuple, list[Node]] = {}
    for node in nodes:
        index.setdefault((_twin_key(node), _cell(node.pos)), []).append(node)
    return index


def _nearest_unused(index: dict, used: set, node: Node) -> Node | None:
    """The still unused node of ``index`` (see ``_twin_index``) that is a twin of ``node``, nearest first."""
    key, (cx, cy, cz) = _twin_key(node), _cell(node.pos)
    best, best_rank = None, None
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for ref in index.get((key, (cx + dx, cy + dy, cz + dz)), ()):
                    if ref.tf_id in used:
                        continue
                    distance = max(abs(a - b) for a, b in zip(node.pos, ref.pos))
                    if distance > TWIN_TOLERANCE + _FLOAT_SLACK:
                        continue
                    rank = (distance, ref.path, ref.tf_id)
                    if best_rank is None or rank < best_rank:
                        best, best_rank = ref, rank
    return best


def match_twins(candidates, reference: Graph) -> dict[int, int]:
    """{candidate tf id: reference tf id} for every candidate that has a twin among the reference unit roots.

    Candidates are processed in (path, tf id) order and each reference unit root is used once.
    """
    index = _twin_index(reference.unit_roots)
    used: set[int] = set()
    twins: dict[int, int] = {}
    for node in sorted(candidates, key=lambda n: (n.path, n.tf_id)):
        best = _nearest_unused(index, used, node)
        if best is not None:
            used.add(best.tf_id)
            twins[node.tf_id] = best.tf_id
    return twins


def missing_descendants(graph: Graph, unit_tf_id: int, reference: Graph, reference_tf_id: int) -> list[int]:
    """Transform ids of the content nodes below a twin-skipped unit that its reference twin does not carry.

    Every node strictly below ``unit_tf_id`` with ``has_content`` is a candidate; the reference side is every node
    strictly below ``reference_tf_id``. Candidates go in (path, tf id) order and take the nearest unused
    counterpart (``_nearest_unused``). One without a counterpart is returned and its whole subtree is covered:
    content inside it is neither matched nor returned again. Ascending path order, so an ancestor always comes
    before the nodes it covers.
    """
    index = _twin_index(reference.nodes[t] for t in reference.subtree(reference_tf_id) if t != reference_tf_id)
    candidates = sorted((graph.nodes[t] for t in graph.subtree(unit_tf_id)
                         if t != unit_tf_id and graph.nodes[t].has_content), key=lambda n: (n.path, n.tf_id))
    used: set[int] = set()
    covered: set[int] = set()
    missing: list[int] = []
    for node in candidates:
        if node.tf_id in covered:
            continue
        counterpart = _nearest_unused(index, used, node)
        if counterpart is not None:
            used.add(counterpart.tf_id)
            continue
        missing.append(node.tf_id)
        covered.update(graph.subtree(node.tf_id))
    return missing


# --------------------------------------------------------------------------
# Selection
# --------------------------------------------------------------------------

@dataclass(frozen=True)
class Selection:
    node_tf_ids: tuple        # ascending: the members of every selected unit plus the carriers
    go_ids: frozenset         # GameObject path ids of node_tf_ids
    carrier_go_ids: frozenset
    unit_tf_ids: tuple        # the selected unit roots (added twin descendants included), in (path, tf id) order
    twins: dict               # twin-skipped unit tf id -> reference tf id
    report: dict              # objects, units, carriers, rules, dropped_script_owners, go_ids_sha256, excluded,
                              # exclude_entries, twin_descendants_added, twin_descendant_paths, twin_carriers,
                              # twin_carrier_ids (decimal GameObject ids, ascending)
    twin_carrier_tf_ids: tuple = ()          # the carriers whose source nodes have components (subset of the carriers)
    twin_carrier_go_ids: frozenset = frozenset()
    descendant_tf_ids: tuple = ()            # roots of the twin descendants twin_descendants imported (before exclusion)
    excluded_tf_ids: tuple = ()              # claimed unit roots and imported twin descendants the exclude entries dropped

    @property
    def scope_go_ids(self) -> frozenset:
        """Owners the native augmenters may see: everything extracted except the bare twin carriers, whose
        MonoBehaviours belong to units that were skipped."""
        return self.go_ids - self.twin_carrier_go_ids


def go_ids_digest(go_ids) -> str:
    return hashlib.sha256("\n".join(str(i) for i in sorted(int(g) for g in go_ids)).encode("utf-8")).hexdigest()


def _under(path: str, root: str) -> bool:
    return path == root or path.startswith(root + "/")


def _resolve_exclusion(entry: Exclusion, graph: Graph, label: str) -> int:
    """The transform an exclude entry names; HardExtractError when the path does not pick exactly one node."""
    found = graph.by_path.get(entry.path)
    if not found:
        raise HardExtractError(f"scene_select: {label} exclude {entry.path!r} resolves to no object")
    if entry.at is None:
        if len(found) > 1:
            raise HardExtractError(f"scene_select: {label} exclude {entry.path!r} is ambiguous ({len(found)} objects "
                                   "share the path); add 'at'")
        return found[0]
    near = [t for t in found if math.dist(graph.nodes[t].pos, entry.at) <= EXCLUDE_AT_TOLERANCE + _FLOAT_SLACK]
    if not near:
        raise HardExtractError(f"scene_select: {label} exclude {entry.path!r} has no object within "
                               f"{EXCLUDE_AT_TOLERANCE} m of {list(entry.at)}")
    if len(near) > 1:
        raise HardExtractError(f"scene_select: {label} exclude {entry.path!r} at {list(entry.at)} is ambiguous "
                               f"({len(near)} objects within {EXCLUDE_AT_TOLERANCE} m)")
    return near[0]


def select_nodes(record: dict, graph: Graph, reference_graph: Graph | None = None) -> Selection:
    """Apply the record's ``select`` to ``graph``; ``reference_graph`` is the graph of ``skip_twins_of``.

    Order: claim the units by rule, match twins, import the missing descendants of twin-skipped units
    (``twin_descendants``), drop the excluded units (``exclude``), then derive the carriers from the units that
    remain. ``tf`` ids are the keys everywhere; the report is the manifest ``partial_scenes`` block minus ``index``
    and ``ballistic_orphans``.
    """
    label = record.get("name", "?")
    spec = validate_select(record.get("select"), f"{label} select")
    if spec.skip_twins_of is not None and reference_graph is None:
        raise HardExtractError(f"scene_select: {label} skips twins of level{spec.skip_twins_of} but no "
                               "reference graph was given")
    for rule in spec.rules:
        found = graph.by_path.get(rule.path)
        if not found:
            raise HardExtractError(f"scene_select: {label} {rule.key!r} resolves to no object")
        inside = [t for t in found if graph.nodes[t].unit not in (None, t)]
        if inside:
            raise HardExtractError(f"scene_select: {label} {rule.key!r} points inside the unit "
                                   f"{graph.nodes[graph.nodes[inside[0]].unit].path!r}; select the unit root")
    claimed: dict[str, list[Node]] = {rule.key: [] for rule in spec.rules}
    for unit in graph.unit_roots:
        for rule in spec.rules:
            if _under(unit.path, rule.path) and (not rule.regions or any(r.contains(unit.pos) for r in rule.regions)):
                claimed[rule.key].append(unit)
                break
    twins = (match_twins([u for units in claimed.values() for u in units], reference_graph)
             if spec.skip_twins_of is not None else {})
    rules_report, selected, rule_of = {}, [], {}
    for rule in spec.rules:
        units = claimed[rule.key]
        kept = [u for u in units if u.tf_id not in twins]
        rules_report[rule.key] = {"selected": len(kept), "twin_skipped": len(units) - len(kept),
                                  "excluded": 0, "descendants_added": 0}
        selected.extend(kept)
        rule_of.update((u.tf_id, rule.key) for u in units)
    selected.sort(key=lambda n: (n.path, n.tf_id))
    descendants: dict[int, int] = {}      # twin descendant tf id -> the twin-skipped unit it sits in
    if spec.twin_descendants == TWIN_DESCENDANTS_IMPORT:
        for unit_tf_id in sorted(twins, key=lambda t: (graph.nodes[t].path, t)):
            for tf_id in missing_descendants(graph, unit_tf_id, reference_graph, twins[unit_tf_id]):
                descendants[tf_id] = unit_tf_id
                rules_report[rule_of[unit_tf_id]]["descendants_added"] += 1
    dropped_units: set[int] = set()
    if spec.exclude:
        candidates = {u.tf_id for u in selected} | set(descendants)
        named: dict[int, Exclusion] = {}
        for entry in spec.exclude:
            node = _resolve_exclusion(entry, graph, label)
            if node in named:
                raise HardExtractError(f"scene_select: {label} exclude {entry.path!r} names the same object as "
                                       f"{named[node].path!r}")
            named[node] = entry
            owner = graph.nodes[node].unit
            if owner not in (None, node) and node not in descendants:
                raise HardExtractError(f"scene_select: {label} exclude {entry.path!r} points inside the unit "
                                       f"{graph.nodes[owner].path!r}; exclude the unit root")
            drops = candidates.intersection(graph.subtree(node))
            if not drops:
                raise HardExtractError(f"scene_select: {label} exclude {entry.path!r} drops no unit "
                                       "(stale entry, or everything below it is a twin or not selected)")
            dropped_units.update(drops)
    final = sorted((n for n in ([*selected, *(graph.nodes[t] for t in descendants)]) if n.tf_id not in dropped_units),
                   key=lambda n: (n.path, n.tf_id))
    if not final:
        why = ("every selected unit is excluded" if selected or descendants
               else "every claimed unit is a twin or nothing matched")
        raise HardExtractError(f"scene_select: {label} selects no unit ({why})")
    for tf_id in dropped_units:
        rules_report[rule_of[descendants.get(tf_id, tf_id)]]["excluded"] += 1
    members: set[int] = set()
    for unit in final:
        members.update(graph.subtree(unit.tf_id))
    carriers: set[int] = set()
    twin_carriers: set[int] = set()       # carriers inside a twin-skipped unit (their source has components)
    for unit in final:
        for ancestor in graph.ancestors(unit.tf_id):
            if ancestor in carriers:
                break
            carriers.add(ancestor)
            if graph.nodes[ancestor].unit is not None:
                twin_carriers.add(ancestor)
    tf_ids = sorted(members | carriers)
    go_ids = frozenset(graph.nodes[t].go_id for t in tf_ids)
    dropped = sum(1 for node in graph.nodes.values()
                  if node.unit is None and node.scripts and node.tf_id not in carriers
                  and any(_under(node.path, rule.path)
                          and (not rule.regions or any(r.contains(node.pos) for r in rule.regions))
                          for rule in spec.rules))
    report = {"objects": len(tf_ids), "units": len(final), "carriers": len(carriers),
              "rules": rules_report, "dropped_script_owners": dropped, "go_ids_sha256": go_ids_digest(go_ids),
              "excluded": len(dropped_units), "exclude_entries": len(spec.exclude),
              "twin_descendants_added": len(descendants),
              "twin_descendant_paths": sorted(graph.nodes[t].path for t in descendants),
              "twin_carriers": len(twin_carriers),
              "twin_carrier_ids": sorted((str(graph.nodes[t].go_id) for t in twin_carriers), key=int)}
    return Selection(tuple(tf_ids), go_ids, frozenset(graph.nodes[t].go_id for t in carriers),
                     tuple(u.tf_id for u in final), {t: twins[t] for t in sorted(twins)}, report,
                     tuple(sorted(twin_carriers)), frozenset(graph.nodes[t].go_id for t in twin_carriers),
                     tuple(sorted(descendants, key=lambda t: (graph.nodes[t].path, t))),
                     tuple(sorted(dropped_units, key=lambda t: (graph.nodes[t].path, t))))


# --------------------------------------------------------------------------
# Extracted partial scene
# --------------------------------------------------------------------------

def structure_problems(scene: dict, selection: Selection) -> list[str]:
    """Sanity checks of an extracted partial scene against its selection; empty when sound."""
    objects = scene["objects"]
    ids = {o["id"] for o in objects}
    problems = []
    if len(ids) != len(objects):
        problems.append("duplicate object ids")
    expected = {str(g) for g in selection.go_ids}
    if ids != expected:
        problems.append(f"emitted objects differ from the selection: {len(ids - expected)} extra, "
                        f"{len(expected - ids)} missing")
    carriers = {str(g) for g in selection.carrier_go_ids}
    twin_carriers = {str(g) for g in selection.twin_carrier_go_ids}
    if not twin_carriers <= carriers:
        problems.append(f"twin carriers {sorted(twin_carriers - carriers)[:3]} are not carriers")
    for obj in objects:
        if obj["parent_id"] is not None and obj["parent_id"] not in ids:
            problems.append(f"{obj['id']} ({obj['name']!r}): parent {obj['parent_id']} is not in the scene")
        if obj["id"] in carriers and obj["components"]:
            # Twin carriers have components in the source (the skipped unit's LODGroup and renderers) and are
            # emitted bare by extract_map; a component that survived is as much a defect as on any container.
            kinds = sorted({c["type"] for c in obj["components"]})
            what = "twin carrier" if obj["id"] in twin_carriers else "carrier"
            problems.append(f"{obj['id']} ({obj['name']!r}): {what} has components {kinds}")
        for comp in obj["components"]:
            if comp["type"] != "LODGroup":
                continue
            missing = {r for lod in comp["lods"] for r in lod["renderer_object_ids"]} - ids
            if missing:
                problems.append(f"{obj['id']} ({obj['name']!r}): LODGroup renderers {sorted(missing)[:3]} are "
                                "outside the selection")
    return problems


# --------------------------------------------------------------------------
# Scoped view of the level file for the native augmenters
# --------------------------------------------------------------------------

class _ScopedObjects:
    """``objects`` of a ScopedFile: scans see the scope, lookups by id see the whole file."""

    def __init__(self, everything, visible: dict):
        self._all, self._visible = everything, visible

    def __getitem__(self, key):
        return self._all[key]

    def get(self, key, default=None):
        return self._all.get(key, default)

    def __contains__(self, key):
        return key in self._all

    def __iter__(self):
        return iter(self._visible)

    def __len__(self):
        return len(self._visible)

    def keys(self):
        return self._visible.keys()

    def values(self):
        return self._visible.values()

    def items(self):
        return self._visible.items()


class ScopedFile:
    """UnityPy SerializedFile view whose MonoBehaviour scan covers only the extracted owners.

    ``owner_ids`` are GameObject path ids. Scans of ``objects`` (``.values()`` and friends) skip
    every MonoBehaviour whose owner is a local GameObject outside ``owner_ids`` and every
    BallisticCollider whose owner carries no MeshCollider/BoxCollider/SphereCollider/CapsuleCollider
    (``orphans``, a sorted list of ``(game object id, component id)``); non-MonoBehaviour objects
    are always visible. Direct lookups by id are never filtered, and every other attribute is
    forwarded to the real file. With every owner in scope the augmenters see exactly what they see
    on the plain file (proved on the full AirPit levels 28, 29 and 40).

    Strict like the plain augmenters: a MonoBehaviour whose head cannot be read, or whose owner
    lives in another file, raises ``HardExtractError`` naming the component. A MonoBehaviour
    without any owner (path id 0) is never hidden; it stays visible, so an augmenter that needs
    an owner refuses it exactly as it does on the plain file.
    """

    def __init__(self, unity_file, owner_ids):
        owners = {int(o) for o in owner_ids}
        self._file = unity_file
        self.orphans: list[tuple[int, int]] = []
        everything = unity_file.objects
        visible = {}
        for path_id, reader in everything.items():
            if reader.type.name == "MonoBehaviour":
                try:
                    owner = reader.parse_monobehaviour_head().m_GameObject
                except Exception as exc:
                    raise HardExtractError(f"scene_select: MonoBehaviour {path_id}: unreadable head ({exc})") from exc
                if owner is not None and owner.path_id != 0:
                    if owner.file_id != 0:
                        raise HardExtractError(f"scene_select: MonoBehaviour {path_id}: owner {owner.path_id} lives "
                                               f"in file {owner.file_id}, not in the level")
                    if owner.path_id not in owners:
                        continue
                    if self._is_orphan_ballistic(reader, owner.path_id, everything):
                        self.orphans.append((owner.path_id, path_id))
                        continue
            visible[path_id] = reader
        self.orphans.sort()
        self.objects = _ScopedObjects(everything, visible)

    @staticmethod
    def _is_orphan_ballistic(reader, owner_id: int, everything) -> bool:
        if extract_ballistics.script_name(reader) != extract_ballistics.SCRIPT_NAME:
            return False
        kinds = set()
        for entry in everything[owner_id].read_typetree()["m_Component"]:
            pointer = entry.get("component", entry)
            component = everything.get(pointer.get("m_PathID", 0)) if pointer.get("m_FileID", 0) == 0 else None
            if component is not None:
                kinds.add(component.type.name)
        return not (kinds & extract_ballistics.PHYSICS_TYPES)

    def __getattr__(self, name):
        if name == "_file":  # not yet set (copy/pickle probing); never recurse
            raise AttributeError(name)
        return getattr(self._file, name)
