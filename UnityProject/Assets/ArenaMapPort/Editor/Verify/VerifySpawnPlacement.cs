// Source-informed placement for generic full-map builds. This is an offline
// authoring gate; runtime SpawnPlacementRepairs remains responsible for raids.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using ArenaMapPort.Editor.Common;
using EFT.Game.Spawning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Editor.Verify
{
    public static class VerifySpawnPlacement
    {
        // Match Client/SpawnPlacementRepairs.cs. The collision mask comes from
        // a hash-bound target SPT snapshot, because this Editor lacks its layer names.
        private const float Radius = 0.35f, Height = 1.8f, MaxSearch = 40f;
        private const float RingStep = 1.5f, BoothMargin = 0.75f, MinSpacing = 1.2f;
        private const float NavSampleDistance = 0.5f, GroundTolerance = 0.15f;
        private const int Directions = 16;
        private static readonly string[] MarkerNames =
            { "SpawnPoint_Player_All", "SpawnPoint_Player_Usec", "SpawnPoint_Player_Bear" };

        public static bool Required(ArenaPackage pkg)
        {
            string mapKey = (string)pkg.Manifest["source"]?["map_key"];
            // Equator's optional native features now declare their map identity.
            // Its accepted legacy starts must retain the existing authoring path;
            // only the generic map pipeline relocates starts from source anchors.
            return !string.IsNullOrEmpty(mapKey) && mapKey != "arena_equator";
        }

        public static void Place(ArenaPackage pkg, NavMeshData data, GameObject[] contentRoots, string outputDir)
        {
            if (!Required(pkg)) return;
            string mapKey = (string)pkg.Manifest["source"]["map_key"];
            if (mapKey != ArenaPaths.Map.key) throw new IOException("Spawn placement map key/descriptor mismatch");
            Directory.CreateDirectory(outputDir);
            string reportPath = Path.Combine(outputDir, "spawn-placement.json");
            var report = new JObject
            {
                ["schema_version"] = 1, ["map_key"] = mapKey, ["status"] = "failed",
                ["limitation"] = "Offline NavMesh and physics authoring proof; not a live SPT raid test.",
                ["parameters"] = new JObject
                {
                    ["capsule_radius"] = Radius, ["capsule_height"] = Height,
                    ["max_search_radius"] = MaxSearch, ["ring_step"] = RingStep,
                    ["directions"] = Directions, ["booth_margin"] = BoothMargin,
                    ["min_spacing"] = MinSpacing, ["nav_sample_distance"] = NavSampleDistance,
                    ["ground_tolerance"] = GroundTolerance, ["ground_lift"] = 0.02f,
                    ["capsule_bottom_extra_lift"] = 0.1f, ["ignore_triggers"] = true
                },
                ["starts"] = new JArray()
            };
            var disabled = new List<Collider>();
            var booths = new List<Bounds>();
            NavMeshDataInstance installed = default(NavMeshDataInstance);
            bool installedHere = false;
            try
            {
                JObject target = ReadTargetPhysics(outputDir);
                int mask = (int)target["player_collision_mask"];
                report["target_physics"] = target;
                if (data == null) throw new IOException("Fresh NavMeshData is null");
                var previous = UnityEngine.AI.NavMesh.CalculateTriangulation();
                if (previous.vertices.Length != 0 || previous.indices.Length != 0)
                    throw new IOException("Unexpected pre-existing NavMesh; cannot prove placement uses this map only");
                installed = UnityEngine.AI.NavMesh.AddNavMeshData(data);
                installedHere = true;
                if (!installed.valid) throw new IOException("Fresh NavMesh could not be installed for placement");
                var triangulation = UnityEngine.AI.NavMesh.CalculateTriangulation();
                report["navmesh_vertices"] = triangulation.vertices.Length;
                report["navmesh_triangles"] = triangulation.indices.Length / 3;
                if (triangulation.vertices.Length < 3 || triangulation.indices.Length < 3)
                    throw new IOException("Fresh map NavMesh is empty");
                foreach (Vector3 v in triangulation.vertices)
                    if (!Finite(v)) throw new IOException("Fresh NavMesh contains non-finite vertices");

                List<Vector3> anchors = ReadAnchors(pkg);
                report["source_anchor_count"] = anchors.Count;
                report["source_plan_sha256"] = pkg.Manifest["source"]?["source_plan_sha256"];
                Scene scripts = SceneManager.GetSceneByName(ArenaPaths.Map.scripts_scene_name);
                if (!scripts.IsValid() || !scripts.isLoaded)
                    throw new IOException("Authored Scripts scene is missing");
                SpawnPointMarker[] markers = ReadMarkers(scripts);
                Physics.SyncTransforms();
                ScanContent(contentRoots, disabled, booths);
                Physics.SyncTransforms();
                report["temporarily_disabled_barriers"] = disabled.Count;
                report["respawn_booth_bounds"] = booths.Count;
                var accepted = new List<Vector3>();
                var entries = (JArray)report["starts"];
                for (int i = 0; i < markers.Length; i++)
                {
                    Vector3 source = anchors[i % anchors.Count];
                    Vector3 original = markers[i].transform.position;
                    if (!Finite(original)) throw new IOException("Authored marker position is non-finite");
                    var entry = new JObject
                    {
                        ["name"] = markers[i].name, ["source_anchor_index"] = i % anchors.Count,
                        ["source_position"] = Vector(source), ["original_position"] = Vector(original),
                        ["accepted"] = false, ["rejections"] = new JObject()
                    };
                    entries.Add(entry);
                    Vector3 position;
                    if (!FindPosition(source, original, anchors, mask, booths, accepted, contentRoots, entry, out position))
                        throw new IOException("No grounded, clear NavMesh start within 40m for " + markers[i].name);
                    accepted.Add(position);
                    entry["displacement_from_source"] = Vector3.Distance(source, position);
                    entry["displacement_from_original"] = Vector3.Distance(original, position);
                }
                // All three pass before anything is changed; content colliders are
                // restored by finally before Scripts is saved or bundles are built.
                for (int i = 0; i < markers.Length; i++)
                {
                    markers[i].transform.position = accepted[i];
                    markers[i]._spawnPoint.Position = accepted[i];
                    EditorUtility.SetDirty(markers[i]);
                    EditorUtility.SetDirty(markers[i].transform);
                }
                Physics.SyncTransforms();
                EditorSceneManager.MarkSceneDirty(scripts);
                // Restore source barriers before saving, including failure paths.
                RestoreBarriers(disabled);
                report["barriers_restored"] = true;
                if (!EditorSceneManager.SaveScene(scripts, scripts.path))
                    throw new IOException("Failed to save accepted positions into Scripts scene");
                report["scripts_scene_path"] = scripts.path;
                report["scripts_scene_sha256"] = FileHash(scripts.path);
                report["status"] = "passed";
                Debug.Log("[ArenaMapPort][SpawnPlacement] " + mapKey + ": accepted and saved three grounded, clear starts");
            }
            catch (Exception ex)
            {
                report["error"] = ex.Message;
                throw;
            }
            finally
            {
                RestoreBarriers(disabled);
                report["barriers_restored"] = true;
                if (installedHere) installed.Remove();
                File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            }
        }

        private static JObject ReadTargetPhysics(string outputDir)
        {
            string path = Path.Combine(outputDir, "target-physics.json");
            if (!File.Exists(path)) throw new IOException("Missing hash-bound target-physics.json");
            var target = JObject.Parse(File.ReadAllText(path));
            if ((int?)target["schema_version"] != 1) throw new IOException("Unsupported target physics schema");
            var layers = target["layers"] as JArray;
            var matrix = target["layer_collision_matrix"] as JArray;
            int player = (int?)target["player_layer"] ?? -1;
            if (layers == null || layers.Count != 32 || matrix == null || matrix.Count != 32 ||
                player < 0 || player >= 32 || (string)layers[player] != "Player")
                throw new IOException("Target physics snapshot has no valid Player layer/matrix");
            uint row = (uint)matrix[player];
            long derived = row & ~(1u << player);
            long supplied = (long?)target["player_collision_mask"] ?? 0;
            if (supplied <= 0 || supplied > int.MaxValue || supplied != derived)
                throw new IOException("Target player collision mask does not match its matrix row");
            string sourcePath = (string)target["source_path"];
            string sourceHash = (string)target["source_sha256"];
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath) ||
                !string.Equals(FileHash(sourcePath), sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Target physics source hash is stale or unavailable");
            return target;
        }

        private static List<Vector3> ReadAnchors(ArenaPackage pkg)
        {
            var samples = pkg.Manifest["anchors_reference"]?["EFT.Game.Spawning.SpawnPointMarker"]?["sample_positions"] as JArray;
            if (samples == null || samples.Count < 3)
                throw new IOException("Generic map requires at least three real source spawn anchors");
            var result = new List<Vector3>();
            foreach (JToken sample in samples)
            {
                var a = sample as JArray;
                if (a == null || a.Count != 3) throw new IOException("Malformed source spawn anchor");
                var v = new Vector3((float)a[0], (float)a[1], (float)a[2]);
                if (!Finite(v)) throw new IOException("Non-finite source spawn anchor");
                result.Add(v);
            }
            return result;
        }

        private static SpawnPointMarker[] ReadMarkers(Scene scripts)
        {
            var found = new Dictionary<string, SpawnPointMarker>();
            foreach (GameObject root in scripts.GetRootGameObjects())
                foreach (SpawnPointMarker marker in root.GetComponentsInChildren<SpawnPointMarker>(true))
                {
                    if (found.ContainsKey(marker.name)) throw new IOException("Duplicate authored spawn marker name");
                    found.Add(marker.name, marker);
                }
            if (found.Count != 3) throw new IOException("Scripts scene must contain exactly three authored starts");
            var result = new SpawnPointMarker[3];
            for (int i = 0; i < result.Length; i++)
            {
                SpawnPointMarker marker;
                if (!found.TryGetValue(MarkerNames[i], out marker) || marker._spawnPoint == null ||
                    !marker.gameObject.activeInHierarchy || marker._collider == null || !marker._collider.enabled || !marker._collider.isTrigger)
                    throw new IOException("Authored player start is missing, inactive, unwired or has a solid marker collider");
                result[i] = marker;
            }
            return result;
        }

        private static void ScanContent(GameObject[] roots, List<Collider> disabled, List<Bounds> booths)
        {
            var seen = new HashSet<Collider>();
            foreach (GameObject root in roots)
                if (root != null)
                    foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
                    {
                        if (!seen.Add(c)) continue;
                        string name = c.gameObject.name;
                        if (name.StartsWith("SPAWN_BOUND", StringComparison.Ordinal))
                        {
                            if (c.enabled) { disabled.Add(c); c.enabled = false; }
                        }
                        else if (name.Contains("_Respawn_") && c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy)
                        {
                            Bounds b = c.bounds; b.Expand(BoothMargin * 2f); booths.Add(b);
                        }
                    }
        }

        private static void RestoreBarriers(List<Collider> disabled)
        {
            foreach (Collider c in disabled)
                if (c != null) c.enabled = true;
            Physics.SyncTransforms();
        }

        private static bool FindPosition(Vector3 source, Vector3 original, List<Vector3> anchors, int mask,
            List<Bounds> booths, List<Vector3> placed, GameObject[] roots, JObject entry, out Vector3 position)
        {
            if (TryCandidate(source, original, mask, booths, placed, roots, entry, out position)) return true;
            foreach (Vector3 anchor in anchors)
                if (Vector3.Distance(source, anchor) <= MaxSearch &&
                    TryCandidate(source, anchor, mask, booths, placed, roots, entry, out position)) return true;
            // Match runtime search radii/directions, with an additional exact 40m ring.
            for (float radius = RingStep; radius < MaxSearch + RingStep; radius += RingStep)
            {
                float bounded = Mathf.Min(radius, MaxSearch);
                for (int d = 0; d < Directions; d++)
                {
                    float angle = d * (360f / Directions) * Mathf.Deg2Rad;
                    Vector3 candidate = source + new Vector3(Mathf.Cos(angle) * bounded, 0f, Mathf.Sin(angle) * bounded);
                    if (TryCandidate(source, candidate, mask, booths, placed, roots, entry, out position)) return true;
                }
            }
            position = default(Vector3);
            return false;
        }

        private static bool TryCandidate(Vector3 source, Vector3 candidate, int mask, List<Bounds> booths,
            List<Vector3> placed, GameObject[] roots, JObject entry, out Vector3 position)
        {
            position = default(Vector3);
            entry["candidates_tested"] = ((int?)entry["candidates_tested"] ?? 0) + 1;
            RaycastHit first;
            if (!Physics.Raycast(candidate + Vector3.up * 1.6f, Vector3.down, out first, 4f, mask, QueryTriggerInteraction.Ignore))
                return Reject(entry, "no_nearby_ground");
            NavMeshHit nav;
            if (!UnityEngine.AI.NavMesh.SamplePosition(first.point, out nav, NavSampleDistance, UnityEngine.AI.NavMesh.AllAreas))
                return Reject(entry, "not_on_fresh_navmesh");
            RaycastHit ground;
            if (!Physics.Raycast(nav.position + Vector3.up * 0.3f, Vector3.down, out ground, 1f, mask, QueryTriggerInteraction.Ignore) ||
                !Owned(ground.collider, roots)) return Reject(entry, "no_owned_ground");
            Vector3 feet = ground.point + Vector3.up * 0.02f;
            if (!Finite(feet) || Vector3.Distance(source, feet) > MaxSearch + 0.001f)
                return Reject(entry, "outside_source_search_radius");
            if (Mathf.Abs(ground.point.y - nav.position.y) > GroundTolerance ||
                ground.normal.y < Mathf.Cos(ArenaMapPort.Editor.NavMesh.BakeNavMesh.AgentSlope * Mathf.Deg2Rad))
                return Reject(entry, "ground_navmesh_height_or_slope_mismatch");
            foreach (Bounds booth in booths)
                if (booth.Contains(feet + Vector3.up * 0.9f)) return Reject(entry, "respawn_booth");
            foreach (Vector3 other in placed)
                if ((other - feet).sqrMagnitude < MinSpacing * MinSpacing) return Reject(entry, "start_spacing");
            Vector3 bottom = feet + Vector3.up * (Radius + 0.1f);
            Vector3 top = feet + Vector3.up * (Height - Radius);
            if (Physics.CheckCapsule(bottom, top, Radius, mask, QueryTriggerInteraction.Ignore))
                return Reject(entry, "player_capsule_blocked");
            position = feet;
            entry["proposed_position"] = Vector(candidate);
            entry["accepted_position"] = Vector(feet);
            entry["accepted"] = true;
            entry["proof"] = new JObject
            {
                ["navmesh_position"] = Vector(nav.position), ["navmesh_distance"] = Vector3.Distance(feet, nav.position),
                ["ground_position"] = Vector(ground.point), ["ground_normal"] = Vector(ground.normal),
                ["ground_collider"] = ColliderPath(ground.collider), ["ground_layer"] = ground.collider.gameObject.layer,
                ["ground_enabled"] = ground.collider.enabled, ["ground_active"] = ground.collider.gameObject.activeInHierarchy,
                ["ground_trigger"] = ground.collider.isTrigger, ["capsule_bottom"] = Vector(bottom),
                ["capsule_top"] = Vector(top), ["capsule_clear"] = true, ["outside_respawn_booths"] = true
            };
            return true;
        }

        private static bool Reject(JObject entry, string reason)
        {
            var counts = (JObject)entry["rejections"];
            counts[reason] = ((int?)counts[reason] ?? 0) + 1;
            return false;
        }

        private static bool Owned(Collider c, GameObject[] roots)
        {
            if (c == null) return false;
            foreach (GameObject root in roots)
                if (root != null && c.transform.IsChildOf(root.transform)) return true;
            return false;
        }

        private static string ColliderPath(Collider collider)
        {
            string path = collider.name;
            for (Transform t = collider.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return collider.gameObject.scene.name + ":" + path;
        }

        private static JArray Vector(Vector3 v) { return new JArray(v.x, v.y, v.z); }
        private static bool Finite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) &&
                   !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }
        private static string FileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
