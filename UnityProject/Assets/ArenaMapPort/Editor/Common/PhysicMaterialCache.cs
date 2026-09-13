// ArenaMapPort WP2 — §B.WP2.5 "dedup by value, not by source pointer" PhysicMaterial cache.
// WP1's package emits physic_material inline (by value) on every MeshCollider/BoxCollider/
// SphereCollider/CapsuleCollider record (§C.6) rather than as a separate id table, since Arena's
// own distinct PhysicMaterial assets (~13, per ref-interchange-verify-F7.md §3) are shared across
// hundreds of colliders. This cache re-creates that sharing on import: an identical 5-tuple of
// values gets exactly one PhysicMaterial asset, reused by reference across every collider that
// declares the same values, scene-independent (one cache instance shared across the whole build).
using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Editor.Common;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ArenaMapPort.Editor.Common
{
    public class PhysicMaterialCache
    {
        private readonly Dictionary<string, PhysicMaterial> _cache = new Dictionary<string, PhysicMaterial>();
        private int _counter;

        /// <summary>Returns null for a null physicMaterialJson (§C.6: null is a valid "no override,
        /// use the collider's implicit default" state, not an error) — otherwise the shared
        /// PhysicMaterial asset for this exact 5-tuple of values, creating+saving it on first use.</summary>
        public PhysicMaterial GetOrCreate(JObject physicMaterialJson)
        {
            if (physicMaterialJson == null) return null;

            float df = (float)physicMaterialJson["dynamic_friction"];
            float sf = (float)physicMaterialJson["static_friction"];
            float b = (float)physicMaterialJson["bounciness"];
            string fc = (string)physicMaterialJson["friction_combine"];
            string bc = (string)physicMaterialJson["bounce_combine"];
            string key = df.ToString("R") + "|" + sf.ToString("R") + "|" + b.ToString("R") + "|" + fc + "|" + bc;

            PhysicMaterial existing;
            if (_cache.TryGetValue(key, out existing) && existing != null)
                return existing;

            var mat = new PhysicMaterial("ArenaPhysicMaterial_" + (_counter++))
            {
                dynamicFriction = df,
                staticFriction = sf,
                bounciness = b,
                frictionCombine = (PhysicMaterialCombine)Enum.Parse(typeof(PhysicMaterialCombine), fc),
                bounceCombine = (PhysicMaterialCombine)Enum.Parse(typeof(PhysicMaterialCombine), bc),
            };

            Directory.CreateDirectory(ArenaPaths.PhysicMaterialsDir);
            string assetPath = ArenaPaths.PhysicMaterialsDir + "/" + mat.name + ".asset";
            AssetDatabase.CreateAsset(mat, assetPath);
            _cache[key] = mat;
            return mat;
        }

        public int DistinctCount => _cache.Count;
    }
}
