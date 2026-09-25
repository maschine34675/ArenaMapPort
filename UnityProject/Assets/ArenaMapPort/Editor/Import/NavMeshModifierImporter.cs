// PLAN-TWO-MAPS §1.4 -- Arena's own NavMesh area-override/ignore-from-build/volume authoring
// (Unity.AI.Navigation.NavMeshModifier / NavMeshModifierVolume, com.unity.ai.navigation@1.1.5),
// dropped by every prior stage of this pipeline: extract_equator.py never recorded these
// MonoBehaviours (COMPONENT_BUILDERS never dispatches on script identity), the importer never
// attached them, and BakeNavMesh.Bake() always passed an empty NavMeshBuildMarkup list. Ground
// truth (2026-09-25 navmesh-modifier spike): Bay5's bruno_helix pump-station family carries 113
// NavMeshModifier area overrides (area=1, "Not Walkable") and all 134 NavMeshModifierVolume
// instances, which bake as walkable today only because the marking BoxCollider is itself a
// trigger; Equator carries zero override instances anywhere.
//
// This class only attaches the two components and copies the extracted public properties onto
// them -- Unity's own [ExecuteAlways] OnEnable() then registers each live component into
// NavMeshModifier.activeModifiers / NavMeshModifierVolume.activeModifiers exactly like it would
// in any authored scene, which is the list BakeNavMesh.Bake() reads (see that file's own
// "PLAN-TWO-MAPS item 4" comments). Not yet host-verified; see
// docs/progress/two-maps-item-4-navmesh-modifiers.md for what the lead still needs to run.
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Unity.AI.Navigation;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    public static class NavMeshModifierImporter
    {
        public static NavMeshModifier AttachModifier(GameObject owner, JObject component)
        {
            string context = "NavMeshModifier on '" + owner.name + "': ";
            if (owner == null || component == null)
                throw new InvalidDataException(context + "requires an owner and component.");
            if (Text(component, "type", context) != "NavMeshModifier" || Int(component, "schema_version", context) != 1)
                throw new InvalidDataException(context + "unsupported component schema.");

            var modifier = owner.AddComponent<NavMeshModifier>();
            modifier.enabled = Bool(component, "enabled", context);
            modifier.overrideArea = Bool(component, "override_area", context);
            modifier.area = Int(component, "area", context);
            modifier.ignoreFromBuild = Bool(component, "ignore_from_build", context);
            modifier.applyToChildren = Bool(component, "apply_to_children", context);
            SetAffectedAgents(modifier.gameObject, component, context);
            return modifier;
        }

        public static NavMeshModifierVolume AttachVolume(GameObject owner, JObject component)
        {
            string context = "NavMeshModifierVolume on '" + owner.name + "': ";
            if (owner == null || component == null)
                throw new InvalidDataException(context + "requires an owner and component.");
            if (Text(component, "type", context) != "NavMeshModifierVolume" || Int(component, "schema_version", context) != 1)
                throw new InvalidDataException(context + "unsupported component schema.");

            var volume = owner.AddComponent<NavMeshModifierVolume>();
            volume.enabled = Bool(component, "enabled", context);
            volume.area = Int(component, "area", context);
            volume.size = ReadVector3(component["size"], context, "size");
            volume.center = ReadVector3(component["center"], context, "center");
            SetAffectedAgents(volume.gameObject, component, context);
            return volume;
        }

        // Both components serialize m_AffectedAgents identically (List<int>, [-1] == "All agent
        // types") but expose no public setter for the whole list -- only per-index membership via
        // AffectsAgentType(id). SPT's own agent-0 covers every bot; recording anything other than
        // the source's "All" default would need a public-property path this package version does
        // not expose, so a non-"All" source value is a hard error rather than a silent downgrade.
        static void SetAffectedAgents(GameObject owner, JObject component, string context)
        {
            var agents = component["affected_agents"] as JArray;
            if (agents == null || agents.Count != 1 || (int)agents[0] != -1)
                throw new InvalidDataException(context + "expected the default affected_agents==[-1] (\"All\"); " +
                    "a non-default source value has no public setter in com.unity.ai.navigation@1.1.5.");
        }

        static Vector3 ReadVector3(JToken token, string context, string field)
        {
            var array = token as JArray;
            if (array == null || array.Count != 3)
                throw new InvalidDataException(context + "invalid " + field + ".");
            return new Vector3((float)array[0], (float)array[1], (float)array[2]);
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
