using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// PLAN-TWO-MAPS item 8 (docs/progress/two-maps-item-8-ai-anchors.md) — loads Arena's own AI
    /// patrol hub/core-point topology, extracted from the excluded TeamFight_AI scene by
    /// tools/extract/extract_ai_anchors.py into <c>tools/maps/&lt;key&gt;.ai.json</c>, staged as a loose
    /// file at <c>&lt;plugin dir&gt;/ai/&lt;key&gt;.ai.json</c> (main csproj addition, see that progress
    /// doc). <see cref="AICorePointRepairs"/>/<see cref="PatrolWayRepairs"/> author the real
    /// topology from this instead of their own NavMesh-derived/single-point synthesis when it loads;
    /// a missing or invalid file (any map without a staged sidecar; Equator, Bay5, AirPit and Bowl have one) returns null and those repairs fall
    /// through to that synthesis unchanged.
    ///
    /// Loading follows <see cref="ShaderRebind"/>'s own convention exactly: a loose JSON sidecar next
    /// to the plugin DLL, read once per map key and cached, parsed with the already-referenced
    /// Newtonsoft.Json as a plain <see cref="JObject"/> (no strongly-typed DTO — this project has no
    /// other use for one, and the schema is small enough that JObject indexing stays readable). A
    /// missing file logs once at Info (most maps have none, by design); a present-but-unparseable file
    /// logs once at Warning (that IS a bug worth surfacing).
    ///
    /// Schema (tools/extract/extract_ai_anchors.py's own docstring has the full field-by-field
    /// provenance): <c>core_points: [{id, connection_group_id, position:[x,y,z], name}]</c>,
    /// <c>patrol_ways: [{hubs: [{core_point_id, position:[x,y,z], sub_points:[[x,y,z], ...]}]}]</c>.
    /// Deliberately no connection/edge data — see the extractor's own module docstring for why
    /// <c>AICorePoint.ConnectionsAtNet</c> is never authored from this file.
    /// </summary>
    internal sealed class AiAnchors
    {
        internal sealed class CorePointAnchor
        {
            internal int Id;
            internal int ConnectionGroupId;
            internal Vector3 Position;
            internal string Name;
        }

        internal sealed class HubAnchor
        {
            internal int CorePointId;
            internal Vector3 Position;
            internal List<Vector3> SubPoints;
        }

        internal sealed class PatrolWayAnchor
        {
            internal List<HubAnchor> Hubs;
        }

        internal string MapKey;
        internal List<CorePointAnchor> CorePoints;
        internal List<PatrolWayAnchor> PatrolWays;

        const string Tag = "[ArenaMapPort][AiAnchors]";

        static readonly Dictionary<string, AiAnchors> _cache = new Dictionary<string, AiAnchors>();
        static readonly HashSet<string> _loggedOnce = new HashSet<string>();

        static string PluginDir => Path.GetDirectoryName(typeof(AiAnchors).Assembly.Location) ?? ".";
        static string AiDir => Path.Combine(PluginDir, "ai");

        /// <summary>tools/maps/&lt;key&gt;.ai.json's own file stem — the map's registry Key
        /// ("arena_equator"/"arena_bay5") with the "arena_" prefix stripped, matching every other
        /// tools/maps/ sidecar for this map (equator.json, equator.bots.json, equator.exits.json).</summary>
        internal static string ShortKey(ArenaMapDescriptor map) =>
            map.Key.StartsWith("arena_", StringComparison.Ordinal) ? map.Key.Substring("arena_".Length) : map.Key;

        /// <summary>Loads (and caches) this map's AI anchors sidecar, or returns null if there is
        /// none / it fails to parse — the caller's own existing synthesis is the fallback in both
        /// cases, never an exception surfaced to a Harmony patch.</summary>
        internal static AiAnchors Load(ArenaMapDescriptor map)
        {
            if (map == null)
            {
                return null;
            }
            var key = ShortKey(map);
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var path = Path.Combine(AiDir, $"{key}.ai.json");
            AiAnchors result = null;
            try
            {
                if (File.Exists(path))
                {
                    var root = JObject.Parse(File.ReadAllText(path));
                    result = Parse(key, root);
                }
                else if (_loggedOnce.Add("missing:" + key))
                {
                    Plugin.Log.LogDebug($"{Tag} no sidecar at {path} — {map.Key} keeps the synthesized " +
                                       "core point / patrol ways (AICorePointFallback/PatrolWayFallback)");
                }
            }
            catch (Exception e)
            {
                result = null;
                if (_loggedOnce.Add("error:" + key))
                {
                    Plugin.Log.LogWarning($"{Tag} failed to load/parse {path}: {e.GetType().Name}: {e.Message} — " +
                                          $"{map.Key} keeps the synthesized core point / patrol ways");
                }
            }
            _cache[key] = result;
            return result;
        }

        static AiAnchors Parse(string key, JObject root)
        {
            var corePoints = new List<CorePointAnchor>();
            foreach (var token in root["core_points"] as JArray ?? new JArray())
            {
                var o = (JObject)token;
                corePoints.Add(new CorePointAnchor
                {
                    Id = o.Value<int>("id"),
                    ConnectionGroupId = o.Value<int>("connection_group_id"),
                    Position = ReadVector3((JArray)o["position"]),
                    Name = o.Value<string>("name"),
                });
            }

            var patrolWays = new List<PatrolWayAnchor>();
            foreach (var wayToken in root["patrol_ways"] as JArray ?? new JArray())
            {
                var wayObject = (JObject)wayToken;
                var hubs = new List<HubAnchor>();
                foreach (var hubToken in wayObject["hubs"] as JArray ?? new JArray())
                {
                    var hubObject = (JObject)hubToken;
                    var subPoints = new List<Vector3>();
                    foreach (var subToken in hubObject["sub_points"] as JArray ?? new JArray())
                    {
                        subPoints.Add(ReadVector3((JArray)subToken));
                    }
                    hubs.Add(new HubAnchor
                    {
                        CorePointId = hubObject.Value<int>("core_point_id"),
                        Position = ReadVector3((JArray)hubObject["position"]),
                        SubPoints = subPoints,
                    });
                }
                patrolWays.Add(new PatrolWayAnchor { Hubs = hubs });
            }

            return new AiAnchors { MapKey = key, CorePoints = corePoints, PatrolWays = patrolWays };
        }

        static Vector3 ReadVector3(JArray array)
        {
            return new Vector3((float)array[0], (float)array[1], (float)array[2]);
        }
    }
}
