"""Physics props: Rigidbody (engine component) + SmallPhysicsObject (native class) presence tag --
docs/PLAN-TWO-MAPS.md item 5, "Physics props -- Rigidbody + SmallPhysicsObject (both maps)".

Gives Equator's 391 / Bay5's 18 movable props (cups, bottles, boxes, garbage -- Arena's own "PHYSIC"
containers) a real Rigidbody and a marker for EFT's native `SmallPhysicsObject` class, so they can be
kicked and shot instead of being static dead geometry (user decision 2026-09-25: props on by default,
docs/PLAN-TWO-MAPS.md §4.1).

Rigidbody builder (`build_rigidbody`): registered into extract_equator.py's COMPONENT_BUILDERS like
any other engine component (BoxCollider, Light, ...) -- runs for EVERY Rigidbody in the extracted
node set, not only the ones inside a SmallPhysicsObject subtree. Bay5 ships 4 extra standalone
"sliver_*" glass-shard Rigidbodies with no SmallPhysicsObject parent at all; the verified total is
413 Rigidbody objects on Equator / 22 on Bay5 (physics-spike/reconcile_counts.py this pass), against
391/18 SmallPhysicsObject instances -- so gating Rigidbody extraction on SmallPhysicsObject presence
would silently drop those 4 Bay5 shards. Field names were verified against real Arena data this spike
(physics-spike/inspect_sliver.py, physics-spike/read_physics_props.py): the shipped typetree keys are
m_LinearDamping/m_AngularDamping, NOT m_Drag/m_AngularDrag as an earlier scratch attempt assumed
(silently produced `drag: null` for all 409 instances found then). Rigidbody has no m_Enabled field
at all (confirmed by its absence from the typetree, not an oversight -- so, unlike every other
COMPONENT_BUILDERS entry, this record carries no "enabled" key; see validate_extract.py's
ENABLED_COMPONENT_TYPES comment for the same "MeshFilter has none" precedent).

SmallPhysicsObject native-features tag (`augment_scene`): pattern of extract_ballistics.augment_scene,
opted into the same way (only under --native-features). SmallPhysicsObject's own custom fields are
unrecoverable -- a direct typetree read fails ("Expected 116 bytes, got 32" this spike) -- but its
class identity and owning GameObject resolve cleanly through the exact MonoScript-header recipe
analysis/scripts/census.py's resolve_script_name() already proves for MonoBehaviour class names
(reused here directly, not reimplemented). Only presence + enabled state is recorded; SoundType and
the cosmetic per-instance force multipliers stay unrecovered by design (docs/PLAN-TWO-MAPS.md §3:
"cosmetic only... not in this plan").
"""
from __future__ import annotations

import os
import sys

from common_ids import HardExtractError

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(_HERE, "..", "..", "analysis", "scripts")))

import census as arena_census  # analysis/scripts/census.py -- resolve_script_name()  # noqa: E402

SCRIPT_NAME = "SmallPhysicsObject"  # plain (non-namespaced) Assembly-CSharp class, verified this spike


def _require(condition, message):
    if not condition:
        raise HardExtractError("PhysicsProps: " + message)


def build_rigidbody(obj, ctx):
    """COMPONENT_BUILDERS["Rigidbody"] builder -- mirrors build_boxcollider's shape (no ctx use
    today; kept as a parameter for signature parity with every other builder in
    extract_equator.py's COMPONENT_BUILDERS dict, and so a future field needing dedup/warnings has
    somewhere to put it without changing every call site again)."""
    tt = obj.read_typetree()
    return {
        "type": "Rigidbody",
        "mass": tt.get("m_Mass", 1.0),
        "linear_damping": tt.get("m_LinearDamping", 0.0),
        "angular_damping": tt.get("m_AngularDamping", 0.05),
        "use_gravity": bool(tt.get("m_UseGravity", 1)),
        "is_kinematic": bool(tt.get("m_IsKinematic", 0)),
        "interpolate": tt.get("m_Interpolate", 0),
        "constraints": tt.get("m_Constraints", 0),
        "collision_detection": tt.get("m_CollisionDetection", 0),
    }


def augment_scene(scene_json, source_file):
    """Tag GameObjects carrying the SmallPhysicsObject MonoBehaviour with a
    {"type":"SmallPhysicsObject","schema_version":1,"enabled":bool} presence marker; identical
    reruns add zero (pattern of extract_ballistics.augment_scene). No contract/byte-decode needed:
    SmallPhysicsObject's own fields are unrecoverable, so only class identity + the generic
    MonoBehaviour header's m_Enabled are ever read."""
    objects = scene_json["objects"]
    by_id = {o["id"]: o for o in objects}
    _require(len(by_id) == len(objects), "duplicate scene object IDs")
    pending, source_owners = [], set()
    for reader in source_file.objects.values():
        if reader.type.name != "MonoBehaviour":
            continue
        cls, status = arena_census.resolve_script_name(reader)
        if status != "ok":
            _require(status in ("null_script", "external_not_loaded"),
                      f"cannot resolve MonoScript for {reader.path_id}: {status}")
            continue
        short_name = cls.rsplit(".", 1)[-1] if cls else None
        if short_name != SCRIPT_NAME:
            continue
        head = reader.parse_monobehaviour_head()
        gptr = head.m_GameObject
        _require(gptr is not None and gptr.path_id != 0 and gptr.file_id == 0,
                 "SmallPhysicsObject with no scene-local owner")
        owner_id = str(gptr.path_id)
        _require(owner_id in by_id and owner_id not in source_owners,
                 "missing or duplicate SmallPhysicsObject owner")
        source_owners.add(owner_id)
        owner = by_id[owner_id]
        component = {"type": "SmallPhysicsObject", "schema_version": 1, "enabled": bool(head.m_Enabled)}
        existing = [c for c in owner["components"] if c["type"] == "SmallPhysicsObject"]
        _require(not existing or existing == [component], "conflicting existing SmallPhysicsObject component")
        if not existing:
            pending.append((owner, component))
    for owner in objects:
        _require(owner["id"] in source_owners or
                 not any(c["type"] == "SmallPhysicsObject" for c in owner["components"]),
                 "SmallPhysicsObject component has no source counterpart")
    for owner, component in pending:
        owner["components"].append(component)
    return len(pending)
