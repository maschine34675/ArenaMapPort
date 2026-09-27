// ArenaMapPort WP2 — headless editor verification pass (task instruction, not a numbered SPEC.md
// subsection): re-opens every generated scene fresh from disk via EditorSceneManager (a genuine
// reload, not just inspecting the in-memory scenes the build just created — the same "open it and
// look" spirit as §G.S1's manual pass condition, done here as an automated, offline-checkable
// equivalent) and logs GameObject/component counts + key EFT-component field values to a JSON
// report.
using System;
using System.Collections.Generic;
using System.IO;
using EFT;
using EFT.Game.Spawning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Editor.Verify
{
    public static class VerifyBuild
    {
        public static void Run(List<string> scenePaths, string outputDir)
        {
            var scenesReport = new JArray();
            foreach (string path in scenePaths)
            {
                Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                scenesReport.Add(VerifyScene(scene, path));
            }

            var report = new JObject
            {
                ["generated_utc"] = DateTime.UtcNow.ToString("o"),
                ["scene_count"] = scenesReport.Count,
                ["scenes"] = scenesReport,
            };

            Directory.CreateDirectory(outputDir);
            string reportPath = Path.Combine(outputDir, "editor_verify.json").Replace('\\', '/');
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Debug.Log("[ArenaMapPort][VerifyBuild] wrote " + reportPath);
        }

        private static JObject VerifyScene(Scene scene, string path)
        {
            var allTransforms = new List<Transform>();
            foreach (GameObject root in scene.GetRootGameObjects())
                allTransforms.AddRange(root.GetComponentsInChildren<Transform>(true));

            int mfNonNull = 0, mfNull = 0;
            long totalVerts = 0;
            int rendererAllMaterialsNonNull = 0, rendererHasNullMaterialSlot = 0;
            var shaderNames = new SortedSet<string>(StringComparer.Ordinal);
            int meshColliders = 0, boxColliders = 0, sphereColliders = 0, capsuleColliders = 0;
            int lights = 0;
            int staticDecals = 0, activeStaticDecals = 0;
            int lodGroups = 0, lodGroupNullRendererEntries = 0;

            int levelSettingsCount = 0;
            string levelSettingsAmbientMode = null;

            int locationSceneCount = 0;
            int locationSceneBotZonesLen = -1, locationSceneSpawnMarkersLen = -1, locationSceneWorldInteractiveLen = -1;

            int botZoneCount = 0;
            int botZonePatrolWaysLen = -1, botZoneSpawnMarkersCount = -1, botZoneUnSpawnPointsLen = -1;

            int locationExportInfoCount = 0;
            long locationExportInfoUnixTime = 0;

            var spawnMarkers = new JArray();

            foreach (Transform t in allTransforms)
            {
                GameObject go = t.gameObject;

                var mf = go.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    if (mf.sharedMesh != null) { mfNonNull++; totalVerts += mf.sharedMesh.vertexCount; }
                    else mfNull++;
                }

                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    bool anyNull = false;
                    foreach (Material m in mr.sharedMaterials)
                    {
                        if (m == null) { anyNull = true; continue; }
                        shaderNames.Add(m.shader != null ? m.shader.name : "<material has null shader>");
                    }
                    if (anyNull) rendererHasNullMaterialSlot++; else rendererAllMaterialsNonNull++;
                }

                if (go.GetComponent<MeshCollider>() != null) meshColliders++;
                if (go.GetComponent<BoxCollider>() != null) boxColliders++;
                if (go.GetComponent<SphereCollider>() != null) sphereColliders++;
                if (go.GetComponent<CapsuleCollider>() != null) capsuleColliders++;
                if (go.GetComponent<Light>() != null) lights++;
                var decal = go.GetComponent<StaticDeferredDecal>();
                if (decal != null)
                {
                    staticDecals++;
                    if (decal.enabled && go.activeInHierarchy) activeStaticDecals++;
                    if (decal._material == null || !(decal._material.GetTexture("_MainTex") is Texture2D))
                        throw new System.IO.InvalidDataException("Unresolved static decal material in " + path + "/" + go.name);
                    shaderNames.Add(decal._material.shader.name);
                }

                var lg = go.GetComponent<LODGroup>();
                if (lg != null)
                {
                    lodGroups++;
                    foreach (LOD lod in lg.GetLODs())
                        foreach (Renderer r in lod.renderers)
                            if (r == null) lodGroupNullRendererEntries++;
                }

                var levelSettings = go.GetComponent<LevelSettings>();
                if (levelSettings != null)
                {
                    levelSettingsCount++;
                    levelSettingsAmbientMode = levelSettings.AmbientMode.ToString();
                }

                var locationScene = go.GetComponent<global::LocationScene>();
                if (locationScene != null)
                {
                    locationSceneCount++;
                    locationSceneBotZonesLen = locationScene.BotZones != null ? locationScene.BotZones.Length : -1;
                    locationSceneSpawnMarkersLen = locationScene.SpawnPointMarkers != null ? locationScene.SpawnPointMarkers.Length : -1;
                    locationSceneWorldInteractiveLen = locationScene.WorldInteractiveObjects != null ? locationScene.WorldInteractiveObjects.Length : -1;
                }

                var botZone = go.GetComponent<global::BotZone>();
                if (botZone != null)
                {
                    botZoneCount++;
                    botZonePatrolWaysLen = botZone.PatrolWays != null ? botZone.PatrolWays.Length : -1;
                    botZoneSpawnMarkersCount = botZone.SpawnPointMarkers != null ? botZone.SpawnPointMarkers.Count : -1;
                    botZoneUnSpawnPointsLen = botZone.UnSpawnPoints != null ? botZone.UnSpawnPoints.Length : -1;
                }

                var exportInfo = go.GetComponent<LocationExportInfo>();
                if (exportInfo != null)
                {
                    locationExportInfoCount++;
                    locationExportInfoUnixTime = exportInfo._unixDateTime;
                }

                var marker = go.GetComponent<SpawnPointMarker>();
                if (marker != null)
                {
                    spawnMarkers.Add(new JObject
                    {
                        ["name"] = go.name,
                        ["sides"] = marker.Sides.ToString(),
                        ["categories"] = marker._spawnPoint != null ? marker._spawnPoint.Categories.ToString() : null,
                        ["collider_attached"] = marker._collider != null,
                        ["collider_enabled"] = marker._collider != null && marker._collider.enabled,
                    });
                }
            }

            var eftComponents = new JObject
            {
                ["LevelSettings"] = new JObject { ["count"] = levelSettingsCount, ["ambient_mode"] = levelSettingsAmbientMode },
                ["LocationScene"] = new JObject
                {
                    ["count"] = locationSceneCount,
                    ["bot_zones_length"] = locationSceneBotZonesLen,
                    ["spawn_point_markers_length"] = locationSceneSpawnMarkersLen,
                    ["world_interactive_objects_length"] = locationSceneWorldInteractiveLen,
                },
                ["BotZone"] = new JObject
                {
                    ["count"] = botZoneCount,
                    ["patrol_ways_length"] = botZonePatrolWaysLen,
                    ["spawn_point_markers_count"] = botZoneSpawnMarkersCount,
                    ["un_spawn_points_length"] = botZoneUnSpawnPointsLen,
                },
                ["LocationExportInfo"] = new JObject { ["count"] = locationExportInfoCount, ["unix_date_time"] = locationExportInfoUnixTime },
                ["SpawnPointMarker"] = new JObject { ["count"] = spawnMarkers.Count, ["markers"] = spawnMarkers },
            };

            return new JObject
            {
                ["scene_name"] = scene.name,
                ["path"] = path,
                ["game_object_count"] = allTransforms.Count,
                ["mesh_filters"] = new JObject { ["non_null_mesh"] = mfNonNull, ["null_mesh"] = mfNull, ["total_vertex_count"] = totalVerts },
                ["renderers"] = new JObject
                {
                    ["all_materials_non_null"] = rendererAllMaterialsNonNull,
                    ["has_null_material_slot"] = rendererHasNullMaterialSlot,
                    ["distinct_shader_names"] = new JArray(shaderNames),
                },
                ["colliders"] = new JObject
                {
                    ["mesh"] = meshColliders,
                    ["box"] = boxColliders,
                    ["sphere"] = sphereColliders,
                    ["capsule"] = capsuleColliders,
                    ["total"] = meshColliders + boxColliders + sphereColliders + capsuleColliders,
                },
                ["lights"] = lights,
                ["static_decals"] = new JObject { ["count"] = staticDecals, ["active"] = activeStaticDecals },
                ["lod_groups"] = new JObject { ["count"] = lodGroups, ["null_renderer_entries"] = lodGroupNullRendererEntries },
                ["eft_components"] = eftComponents,
            };
        }
    }
}
