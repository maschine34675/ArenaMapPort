using System;
using System.Collections.Generic;
using EFT;
using EFT.Game.Spawning;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// bots-stage1 (docs/progress/bots-stage1.md, "Host test 9") — patrol ways for a zone that has none.
    ///
    /// With every holder, the voxel grid and the core point in place a scav activates cleanly, and then stands
    /// at its spawn point: the status dump shows <c>layer=PatrolAssault standBy=active peace=True moving=False</c>.
    /// SAIN only takes over in combat; in peace BSG's own PatrolAssault layer walks <c>BotZone.PatrolWays</c>,
    /// and our zones ship zero ways (the Arena scenes carry no patrol data; <c>PatrolPointChooserBasic</c>
    /// tolerates the empty list and simply never sets a target). DrakiaXYZ-Waypoints 1.9 does not add ways
    /// either — it only patches pathing.
    ///
    /// Fix: before <c>BotsController.Init</c> (which calls <c>BotZone.Init</c> → <c>Restore</c>/<c>InitPoints</c>
    /// on every way), build ways from the zone's Bot spawn markers: each marker sampled onto the NavMesh, grouped
    /// into NavMesh-connected components (pairwise complete paths — host test 10 showed the first marker can sit
    /// on an island, e.g. the upper-floor ring, and "reachable from the first" then drops everything else), every
    /// component chained nearest-neighbour and cut into ways of up to <see cref="MaxPointsPerWay"/> points (two
    /// or more ways let bots switch, <c>_canChangeWays</c>); an isolated point becomes a one-point way. Every point
    /// gets its sub points from BSG's own generator (<c>PatrolPoint.CreatSubs</c>, the same three radii
    /// <c>CreateSubPoints</c> uses in the editor). Vanilla maps and zones with authored ways are never touched.
    ///
    /// PLAN-TWO-MAPS item 8 (docs/progress/two-maps-item-8-ai-anchors.md): when the caller passes a
    /// non-null <see cref="AiAnchors"/> (Equator/Bay5 only, and only once <see cref="AICorePointFallback"/>
    /// has already authored the matching real <see cref="AICorePoint"/> net this raid — see that file),
    /// <see cref="BuildFromAnchors"/> authors Arena's own real hub/subpoint hierarchy instead of the
    /// NavMesh-derived synthesis below: one <see cref="PatrolWay"/> per sidecar entry, its <c>Points</c>
    /// the real hub <see cref="PatrolPoint"/>s (<see cref="PatrolPoint.SetCorePoint"/> to the matching real
    /// core point — a hub's own <c>_corePointId</c> defaults to 0, which would otherwise silently resolve
    /// to the id-0 alias instead of Arena's intended core point), each hub's <c>subPoints</c> the literal
    /// authored subpoint positions (plain C# defaults: <c>CanUseByBoss</c> true, <c>ShallSit</c> false,
    /// <c>PatrolPointType.checkPoint</c>, no <c>ActionData</c>/<c>PointWithLookSides</c> — exactly what
    /// <c>AddComponent&lt;PatrolPoint&gt;()</c> already gives, set explicitly here for clarity), bypassing
    /// <c>CreatSubs</c>/<c>RefreshData</c> entirely. Any exception (a hub referencing a core point id the
    /// holder does not have, for instance) falls through to the untouched NavMesh-derived synthesis below
    /// for that zone — the twelve deferred maps' own unchanged behavior.
    /// </summary>
    internal static class PatrolWayFallback
    {
        const string Tag = "[ArenaMapPort][RaidFix]";
        internal const int MaxPointsPerWay = 8; // soak 2 (20.09.): a 4+2 split left one bot shuttling between two points
        const float SampleRadius = 2f;
        const int IdBase = 910000;   // PatrolPoint.Id must be non-zero and unique within the zone
        const int HubIdBase = 920000; // AI-anchors hubs -- distinct range from IdBase above (the two paths
                                       // never run for the same zone, but distinct ranges keep log/diagnostic
                                       // ids unambiguous about which path authored a given PatrolPoint).

        internal static void EnsurePatrolWays(BotZone[] botZones, AiAnchors anchors, out int hubCount, out int subPointCount)
        {
            hubCount = 0;
            subPointCount = 0;
            if (botZones == null)
            {
                return;
            }
            var markers = UnityEngine.Object.FindObjectsOfType<SpawnPointMarker>();
            foreach (var zone in botZones)
            {
                if (zone == null || (zone.PatrolWays != null && zone.PatrolWays.Length > 0))
                {
                    continue; // authored ways — never touch
                }
                if (anchors != null && anchors.PatrolWays.Count > 0)
                {
                    try
                    {
                        BuildFromAnchors(zone, anchors, out int zoneHubs, out int zoneSubPoints);
                        hubCount += zoneHubs;
                        subPointCount += zoneSubPoints;
                        continue; // real topology authored — do not also run the NavMesh-derived synthesis
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"{Tag} zone '{zone.name}': authoring real patrol ways from " +
                                              $"ai/{anchors.MapKey}.ai.json failed ({e.GetType().Name}: {e.Message}) — " +
                                              "falling back to the NavMesh-derived synthesis");
                        // BuildFromAnchors destroyed its partial GameObjects and never reached its zone.PatrolWays
                        // assignment (see its own doc comment), so the zone is exactly as the "authored ways?" check
                        // above saw it (null or empty) — BuildForZone below overwrites it either way.
                    }
                }
                try
                {
                    BuildForZone(zone, markers);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"{Tag} patrol-way fallback failed for zone '{zone.name}': {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    if (zone.PatrolWays == null)
                    {
                        zone.PatrolWays = Array.Empty<PatrolWay>();
                    }
                }
            }
        }

        /// <summary>Authors Arena's own real hub/subpoint <see cref="PatrolWay"/>(s) for one zone from
        /// <paramref name="anchors"/> — see this class' own doc comment. Builds fully into a local list and
        /// only assigns <paramref name="zone"/>.PatrolWays on its own final line; a mid-way exception destroys
        /// the PatrolWay GameObjects created so far and rethrows, leaving the zone exactly as the caller found
        /// it — nothing for the caller to undo before it falls back to <see cref="BuildForZone"/>.</summary>
        static void BuildFromAnchors(BotZone zone, AiAnchors anchors, out int hubCount, out int subPointCount)
        {
            hubCount = 0;
            subPointCount = 0;
            var holder = UnityEngine.Object.FindObjectOfType<AICorePointHolder>();
            if (holder == null)
            {
                throw new InvalidOperationException("no AICorePointHolder in the scene (AICorePointFallback should have created one)");
            }

            var ways = new List<PatrolWay>();
            var created = new List<GameObject>();
            try
            {
                foreach (var wayAnchor in anchors.PatrolWays)
                {
                    var wayGo = new GameObject($"PatrolWay(Arena) {ways.Count}");
                    created.Add(wayGo);
                    wayGo.transform.SetParent(zone.transform, false);
                    var way = wayGo.AddComponent<PatrolWay>();
                    way.PatrolType = PatrolType.patrolling;
                    way._maxPersons = -1;
                    way.CoefSubPoints = 1f;
                    way.Points = new List<PatrolPoint>();

                    foreach (var hubAnchor in wayAnchor.Hubs)
                    {
                        var corePoint = holder.GetCorePoint(hubAnchor.CorePointId);
                        if (corePoint == null)
                        {
                            throw new InvalidOperationException(
                                $"hub at {hubAnchor.Position:F1} references core point id {hubAnchor.CorePointId}, " +
                                "not present in the holder");
                        }

                        var hubGo = new GameObject($"PatrolPoint(Arena) hub{hubCount}");
                        hubGo.transform.SetParent(wayGo.transform, false);
                        hubGo.transform.position = hubAnchor.Position;
                        var hubPoint = hubGo.AddComponent<PatrolPoint>(); // Awake -> subPoints = RefreshData() (no children yet)
                        hubPoint.Id = HubIdBase + hubCount;
                        hubPoint.CanUseByBoss = true;
                        hubPoint.ShallSit = false;
                        hubPoint.PatrolPointType = PatrolPointType.checkPoint;
                        hubPoint.SubManual = false;
                        hubPoint.SetCorePoint(corePoint); // _corePointId = corePoint.Id -- its own default (0) would
                                                           // otherwise resolve to the id-0 alias, not this real point
                        if (hubPoint.subPoints == null)
                        {
                            hubPoint.subPoints = new List<PatrolPoint>();
                        }

                        foreach (var subPos in hubAnchor.SubPoints)
                        {
                            var subGo = new GameObject($"SubPoint_{hubPoint.subPoints.Count + 1}");
                            subGo.transform.SetParent(hubGo.transform, false);
                            subGo.transform.position = subPos;
                            var subPoint = subGo.AddComponent<PatrolPoint>(); // C# defaults match Arena's own authored
                            subPoint.CanUseByBoss = true;                     // subpoints exactly (CanUseByBoss true,
                            subPoint.ShallSit = false;                        // ShallSit false, checkPoint, _corePointId
                            subPoint.PatrolPointType = PatrolPointType.checkPoint; // 0/Id 0, no ActionData/PointWithLookSides)
                            subPoint.SubManual = false;
                            hubPoint.subPoints.Add(subPoint);
                            subPointCount++;
                        }

                        way.Points.Add(hubPoint);
                        hubCount++;
                    }
                    ways.Add(way);
                }
            }
            catch
            {
                foreach (var go in created)
                {
                    UnityEngine.Object.Destroy(go);
                }
                throw;
            }

            zone.PatrolWays = ways.ToArray();
            Plugin.Log.LogDebug($"{Tag} zone '{zone.name}' authored {ways.Count} real patrol way(s) from " +
                               $"ai/{anchors.MapKey}.ai.json ({hubCount} hub(s), {subPointCount} sub point(s))");
        }

        static void BuildForZone(BotZone zone, SpawnPointMarker[] markers)
        {
            var candidates = new List<Vector3>();
            int offMesh = 0;
            foreach (var marker in markers)
            {
                var sp = marker?.SpawnPoint;
                if (sp == null || (sp.Categories & ESpawnCategoryMask.Bot) == 0)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(sp.BotZoneName) && sp.BotZoneName != zone.name)
                {
                    continue;
                }
                if (NavMesh.SamplePosition(marker.transform.position + Vector3.up * 0.5f, out var hit, SampleRadius, NavMesh.AllAreas))
                {
                    candidates.Add(hit.position);
                }
                else
                {
                    offMesh++;
                }
            }
            if (candidates.Count == 0)
            {
                Plugin.Log.LogWarning($"{Tag} zone '{zone.name}' has no patrol ways and no Bot spawn marker on the NavMesh " +
                                      $"({offMesh} off-mesh) — bots will stand at their spawn points");
                zone.PatrolWays = Array.Empty<PatrolWay>();
                return;
            }

            // NavMesh-connected components (union-find over pairwise complete paths)
            int n = candidates.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (Find(i) != Find(j) && PathComplete(candidates[i], candidates[j]))
                {
                    parent[Find(i)] = Find(j);
                }
            }
            var components = new Dictionary<int, List<Vector3>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!components.TryGetValue(root, out var list)) components[root] = list = new List<Vector3>();
                list.Add(candidates[i]);
            }

            var ways = new List<PatrolWay>();
            int subPoints = 0, points = 0, islands = 0;
            foreach (var component in components.Values)
            {
                var ordered = Chain(component);
                points += ordered.Count;
                if (ordered.Count == 1) islands++;
                for (int start = 0; start < ordered.Count; start += MaxPointsPerWay)
                {
                    int count = Math.Min(MaxPointsPerWay, ordered.Count - start);
                    if (count == 1 && start > 0)
                    {
                        // a lone tail point of this component: append to its previous way instead of a one-point way
                        var last = ways[ways.Count - 1];
                        last.Points.Add(BuildPoint(last, ordered[start], last.Points.Count, ref subPoints));
                        break;
                    }
                    ways.Add(BuildWay(zone, ordered.GetRange(start, count), ways.Count, ref subPoints));
                }
            }
            zone.PatrolWays = ways.ToArray();
            Plugin.Log.LogDebug($"{Tag} zone '{zone.name}' had no patrol ways — built {ways.Count} way(s) with {points} point(s) " +
                               $"and {subPoints} sub point(s) from {candidates.Count} Bot spawn marker(s) in {components.Count} " +
                               $"NavMesh-connected component(s) ({islands} isolated single point(s), {offMesh} marker(s) off the NavMesh); " +
                               "vanilla PatrolAssault can walk now");
        }

        static PatrolWay BuildWay(BotZone zone, List<Vector3> positions, int index, ref int subPoints)
        {
            var go = new GameObject($"PatrolWay(ArenaFallback) {index}");
            go.transform.SetParent(zone.transform, false);
            var way = go.AddComponent<PatrolWay>();
            way.PatrolType = PatrolType.patrolling;
            way._maxPersons = -1;
            way.CoefSubPoints = 1f;
            way.Points = new List<PatrolPoint>();
            for (int i = 0; i < positions.Count; i++)
            {
                way.Points.Add(BuildPoint(way, positions[i], i, ref subPoints));
            }
            return way;
        }

        static PatrolPoint BuildPoint(PatrolWay way, Vector3 position, int index, ref int subPoints)
        {
            var go = new GameObject($"PatrolPoint {index}");
            go.transform.SetParent(way.transform, false);
            go.transform.position = position;
            var point = go.AddComponent<PatrolPoint>(); // Awake: subPoints = RefreshData() (no children yet)
            point.Id = IdBase + way.GetInstanceID() % 1000 * 100 + index;
            point.CanUseByBoss = true;
            point.PatrolPointType = PatrolPointType.checkPoint;
            point.SubManual = false;
            if (point.subPoints == null)
            {
                point.subPoints = new List<PatrolPoint>();
            }
            // BSG's generator (PatrolPoint.CreateSubPoints, non-manual branch): three rings, two more when thin
            point.CreatSubs(1.7f * way.CoefSubPoints);
            point.CreatSubs(2.5f * way.CoefSubPoints);
            point.CreatSubs(3.4f * way.CoefSubPoints);
            if (point.subPoints.Count <= 6)
            {
                point.CreatSubs(1.7f * way.CoefSubPoints, 1f);
                point.CreatSubs(2.5f * way.CoefSubPoints, 1f);
            }
            subPoints += point.subPoints.Count;
            return point;
        }

        static bool PathComplete(Vector3 from, Vector3 to)
        {
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
        }

        static List<Vector3> Chain(List<Vector3> points)
        {
            var remaining = new List<Vector3>(points);
            var ordered = new List<Vector3>();
            if (remaining.Count == 0)
            {
                return ordered;
            }
            var current = remaining[0];
            remaining.RemoveAt(0);
            ordered.Add(current);
            while (remaining.Count > 0)
            {
                int best = 0;
                float bestD = float.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    float d = (remaining[i] - current).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                current = remaining[best];
                remaining.RemoveAt(best);
                ordered.Add(current);
            }
            return ordered;
        }
    }
}
