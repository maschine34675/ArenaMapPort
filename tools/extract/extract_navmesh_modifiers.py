"""NavMesh modifier/volume components, source-backed (PLAN-TWO-MAPS §1.4).

Verified facts (2026-09-25 navmesh-modifier spike, see docs/progress/two-maps-item-4-navmesh-modifiers.md):
Arena's own Equator/Bay5 content carries real Unity.AI.Navigation.NavMeshModifier /
NavMeshModifierVolume authoring -- 113 NavMeshModifier area overrides (area=1,
"Not Walkable") and all 134 NavMeshModifierVolume instances sit on Bay5's
bruno_helix pump-station prop family, which today bakes as walkable because its
marking BoxCollider is itself a trigger. Equator carries zero override instances
anywhere -- this extractor changes nothing observable for Equator's own bake.

Follows extract_ballistics.py's "augment the already-built scene JSON after the
fact" pattern, not COMPONENT_BUILDERS: every script instance's raw Unity type is
"MonoBehaviour" (extract_equator.COMPONENT_BUILDERS is keyed by native engine
type names such as "MeshFilter"/"BoxCollider" and never dispatches on script
identity), so this module resolves script identity itself via
analysis/scripts/census.py's resolve_script_name() -- the same MonoScript-pool
PPtr-dereference machinery extract_equator.py already reuses for spawn anchors --
and appends components onto the already-built scene JSON's owner GameObjects by
id, exactly the way extract_ballistics.augment_scene() does.

No cross-version preset/material contract is needed here (unlike ballistics):
the "area" integer is just Unity's plain 0..31 NavMesh area index, resolved to a
name via the companion read_navmesh_areas() (NavMeshProjectSettings, a native
engine ScriptableObject, ordinary typetree, no hand-built nodes) and recorded
once in manifest.json under native_features.navmesh_areas -- BakeNavMesh.cs
itself never needs the name, only the numeric index the modifier/volume already
carries, matching NavMeshBuildMarkup.area / NavMeshBuildSource.area.
"""
from __future__ import annotations

import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ANALYSIS_SCRIPTS = os.path.normpath(os.path.join(_HERE, "..", "..", "analysis", "scripts"))
for _path in (_HERE, _ANALYSIS_SCRIPTS):  # standalone-importable regardless of caller sys.path setup
    if _path not in sys.path:
        sys.path.insert(0, _path)

import UnityPy  # noqa: E402
from common_ids import HardExtractError  # noqa: E402
from navmesh_typetree import build_modifier_nodes, build_volume_nodes  # noqa: E402
import census  # analysis/scripts/census.py -- resolve_script_name()  # noqa: E402

SCRIPT_MODIFIER = "Unity.AI.Navigation.NavMeshModifier"
SCRIPT_VOLUME = "Unity.AI.Navigation.NavMeshModifierVolume"


def _require(condition, message):
    if not condition:
        raise HardExtractError("NavMeshModifiers: " + message)


def _affected_agents(tree):
    agents = tree.get("m_AffectedAgents")
    _require(isinstance(agents, list) and all(type(a) is int for a in agents), "invalid m_AffectedAgents")
    return list(agents)


def _vec3(tree, key):
    """[x, y, z] array -- this project's established Vector3 JSON convention
    (extract_equator.build_boxcollider/build_scene_graph and SceneAssembler.cs's
    ReadVector3 both use arrays, never an {x,y,z} dict)."""
    v = tree.get(key)
    _require(isinstance(v, dict) and {"x", "y", "z"} <= set(v), "invalid " + key)
    return [v["x"], v["y"], v["z"]]


def _modifier_component(tree, enabled):
    return {
        "type": "NavMeshModifier", "schema_version": 1, "enabled": enabled,
        "override_area": bool(tree["m_OverrideArea"]), "area": int(tree["m_Area"]),
        "ignore_from_build": bool(tree["m_IgnoreFromBuild"]),
        "apply_to_children": bool(tree["m_ApplyToChildren"]),
        "affected_agents": _affected_agents(tree),
    }


def _volume_component(tree, enabled):
    return {
        "type": "NavMeshModifierVolume", "schema_version": 1, "enabled": enabled,
        "area": int(tree["m_Area"]), "size": _vec3(tree, "m_Size"), "center": _vec3(tree, "m_Center"),
        "affected_agents": _affected_agents(tree),
    }


def augment_scene(scene_json, source_file):
    """Append NavMeshModifier/NavMeshModifierVolume components onto their owner
    GameObjects in the already-built scene JSON extract_scene() produced. Additive
    and idempotent, same contract as extract_ballistics.augment_scene(): a second
    call over the same (scene_json, source_file) adds nothing new, and a
    pre-existing non-matching component of the same type is a hard error rather
    than a silent overwrite. Returns (modifier_count, volume_count)."""
    objects = scene_json["objects"]
    by_id = {o["id"]: o for o in objects}
    _require(len(by_id) == len(objects), "duplicate scene object IDs")
    modifier_count = volume_count = 0
    for reader in source_file.objects.values():
        if reader.type.name != "MonoBehaviour":
            continue
        name, status = census.resolve_script_name(reader)
        if status != "ok" or name not in (SCRIPT_MODIFIER, SCRIPT_VOLUME):
            continue
        mb = reader.parse_monobehaviour_head()
        owner_ptr = mb.m_GameObject
        _require(owner_ptr is not None and owner_ptr.path_id != 0, "modifier with no owner GameObject")
        owner_id = str(owner_ptr.path_id)
        _require(owner_id in by_id, "modifier owner " + owner_id + " is not in this scene's extracted object set")
        owner = by_id[owner_id]
        enabled = bool(mb.m_Enabled)
        if name == SCRIPT_MODIFIER:
            component_type = "NavMeshModifier"
            tree = reader.read_typetree(nodes=build_modifier_nodes(reader.version), check_read=True)
            component = _modifier_component(tree, enabled)
        else:
            component_type = "NavMeshModifierVolume"
            tree = reader.read_typetree(nodes=build_volume_nodes(reader.version), check_read=True)
            component = _volume_component(tree, enabled)
        existing = [c for c in owner["components"] if c["type"] == component_type]
        _require(not existing or existing == [component], "conflicting existing " + component_type + " component")
        if existing:
            continue
        owner["components"].append(component)
        if component_type == "NavMeshModifier":
            modifier_count += 1
        else:
            volume_count += 1
    return modifier_count, volume_count


def _scan_for_areas(env):
    for f in env.files.values():
        if not hasattr(f, "objects"):
            continue
        for obj in f.objects.values():
            if obj.type.name != "NavMeshProjectSettings":
                continue
            tree = obj.read_typetree()
            areas = {}
            for index, entry in enumerate(tree.get("areas", []) or []):
                entry_name = entry.get("name")
                if entry_name:
                    areas[str(index)] = {"name": entry_name, "cost": entry.get("cost")}
            return areas
    return None


def read_navmesh_areas(env, source_root=None):
    """NavMeshProjectSettings.areas: index -> {name, cost}. A native engine
    ScriptableObject (ordinary typetree, no hand-built nodes) whose numeric costs
    already matched BakeNavMesh.cs's hardcoded agent-0 constants in the 2026-09-25
    spike (an independent cross-check, no bake-side change needed for that).

    It lives in Arena's bare "globalgamemanagers" serialized file (the project's
    global-manager singletons: TagManager/InputManager/BuildSettings/...), NOT in
    "globalgamemanagers.assets" (shared runtime-referenced assets) -- confirmed by
    this function returning nothing against extract_equator.load_environment()'s /
    extract_bay5.load_environment_bay5()'s own loaded env (both load only the
    ".assets" file) until the bare file is loaded too. source_root, when given,
    is used for exactly that one small extra load (mirrors extract_bay5.py's own
    resolve_scene_indices(), which already loads this same bare file to read
    BuildSettings.scenes) -- never re-loads the whole multi-file scene environment."""
    areas = _scan_for_areas(env)
    if areas is not None:
        return areas
    if not source_root:
        return {}
    gm_path = os.path.join(source_root, "globalgamemanagers")
    if not os.path.isfile(gm_path):
        return {}
    return _scan_for_areas(UnityPy.load(gm_path)) or {}
