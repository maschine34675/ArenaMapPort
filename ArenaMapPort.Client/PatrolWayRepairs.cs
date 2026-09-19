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
    /// </summary>
    internal static class PatrolWayFallback
    {
        const string Tag = "[ArenaMapPort][RaidFix]";
        internal const int MaxPointsPerWay = 4;
        const float SampleRadius = 2f;
        const int IdBase = 910000; // PatrolPoint.Id must be non-zero and unique within the zone

        internal static void EnsurePatrolWays(BotZone[] botZones)
        {
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
            Plugin.Log.LogInfo($"{Tag} zone '{zone.name}' had no patrol ways — built {ways.Count} way(s) with {points} point(s) " +
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
