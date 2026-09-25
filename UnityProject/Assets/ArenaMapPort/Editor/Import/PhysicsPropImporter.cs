// PLAN-TWO-MAPS item 5 -- attach the engine Rigidbody and EFT's native SmallPhysicsObject class to
// Equator's 391 / Bay5's 18 movable props (cups, bottles, boxes, garbage) so they react to shots and
// touches instead of being static dead geometry. See tools/extract/extract_physics_props.py for the
// extractor half and docs/progress/two-maps-item-5-physics-props.md for host-test evidence.
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    public static class PhysicsPropImporter
    {
        /// <summary>
        /// SceneAssembler Pass-1c: the engine Rigidbody component, order-independent like
        /// BoxCollider/Light (every field here has a sane compiled default, no cross-object
        /// reference to resolve). Field names verified against real Arena data this spike
        /// (tools/extract/extract_physics_props.py's own header note): the shipped typetree keys
        /// are m_LinearDamping/m_AngularDamping, not m_Drag/m_AngularDrag -- Unity 2022.3's public
        /// Rigidbody API still exposes them as .drag/.angularDrag (the old names), which is what
        /// this importer sets. Rigidbody has no m_Enabled field at all, so unlike every other
        /// per-component attacher in this project this one has no ReadEnabled/enabled to apply.
        /// </summary>
        public static void AttachRigidbody(GameObject go, JObject comp)
        {
            if (go == null || comp == null)
                throw new InvalidDataException("Rigidbody import requires an owner and component.");
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = (float)comp["mass"];
            rb.drag = (float)comp["linear_damping"];
            rb.angularDrag = (float)comp["angular_damping"];
            rb.useGravity = (bool)comp["use_gravity"];
            rb.isKinematic = (bool)comp["is_kinematic"];
            rb.interpolation = (RigidbodyInterpolation)(int)comp["interpolate"];
            rb.constraints = (RigidbodyConstraints)(int)comp["constraints"];
            rb.collisionDetectionMode = (CollisionDetectionMode)(int)comp["collision_detection"];
        }

        /// <summary>
        /// SceneAssembler Pass 2: call only after every Collider/Rigidbody/BallisticCollider under
        /// <paramref name="owner"/>'s subtree already exists -- <c>SmallPhysicsObject.Awake()</c>
        /// (D:/SPT41/Development/_decompiled/SmallPhysicsObject.cs:124-158) walks
        /// <c>GetComponentsInChildren(includeInactive: true, ...)</c> for both the moment this
        /// component is added (AddComponent runs Awake synchronously in the Editor when the
        /// GameObject is active in the loaded scene). Presence, enabled state and (schema 2, since
        /// 2026-09-25) SoundType are recoverable -- SoundType is decoded from the raw bytes by
        /// extract_physics_props.decode_sound_type because the class default (Books) played nothing
        /// on the first host raid (null WeaponSounds bank -> NullReferenceException on every touch).
        /// Every other field (force multipliers, TimeOfLife, ...) is left at the class's compiled
        /// default (docs/PLAN-TWO-MAPS.md §3: "cosmetic only... not in this plan").
        /// </summary>
        public static global::SmallPhysicsObject AttachSmallPhysicsObject(GameObject owner, JObject comp)
        {
            if (owner == null || comp == null)
                throw new InvalidDataException("SmallPhysicsObject import requires an owner and component.");
            string context = "SmallPhysicsObject on '" + owner.name + "': ";
            int schema = Int(comp, "schema_version", context);
            if (Text(comp, "type", context) != "SmallPhysicsObject" || (schema != 1 && schema != 2))
                throw new InvalidDataException(context + "unsupported component schema.");
            bool enabled = Bool(comp, "enabled", context);
            // schema 2: Arena's authored SoundType (EPhysicsObjectType, 0..12); schema 1 packages keep the class default.
            int soundType = schema >= 2 ? Int(comp, "sound_type", context) : (int)global::SmallPhysicsObject.EPhysicsObjectType.Books;
            if (!Enum.IsDefined(typeof(global::SmallPhysicsObject.EPhysicsObjectType), soundType))
                throw new InvalidDataException(context + "sound_type " + soundType + " is not an EPhysicsObjectType value.");

            // includeInactive: true -- SceneAssembler's own Pass 1a already applied every object's
            // authored active state before this Pass-2 call runs, and Equator's PHYSIC container
            // (this subtree's ancestor) ships hierarchy-disabled in Arena's own data, so the whole
            // subtree is inactive-in-hierarchy at import time even though each prop's own
            // activeSelf is true (verified this spike, physics-spike/find_inactive_ancestors.py).
            if (owner.GetComponentInChildren<Rigidbody>(true) == null)
                throw new InvalidDataException(context + "no Rigidbody anywhere in the subtree; import order is wrong.");
            if (owner.GetComponent<global::SmallPhysicsObject>() != null)
                throw new InvalidDataException(context + "owner already carries a SmallPhysicsObject.");

            global::SmallPhysicsObject spo = null;
            try
            {
                spo = owner.AddComponent<global::SmallPhysicsObject>();
                if (spo == null)
                    throw new InvalidDataException(context + "native EFT component is not ready; retry the authoring phase on an Editor update tick.");
                spo.enabled = enabled;
                spo.SoundType = (global::SmallPhysicsObject.EPhysicsObjectType)soundType;
                return spo;
            }
            catch
            {
                if (spo != null) UnityEngine.Object.DestroyImmediate(spo);
                throw;
            }
        }

        static string Text(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.String || String.IsNullOrEmpty((string)token))
                throw new InvalidDataException(context + "missing string '" + key + "'.");
            return (string)token;
        }

        static int Int(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.Integer)
                throw new InvalidDataException(context + "missing integer '" + key + "'.");
            long value = token.Value<long>();
            if (value < Int32.MinValue || value > Int32.MaxValue)
                throw new InvalidDataException(context + "integer out of range: " + key + ".");
            return (int)value;
        }

        static bool Bool(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.Boolean)
                throw new InvalidDataException(context + "missing boolean '" + key + "'.");
            return (bool)token;
        }
    }
}
