using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using EFT.Game.Spawning;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// exfils-stage2 follow-up (docs/progress/exfils-stage2.md, "Respawn booths") — open Arena's respawn booths.
    ///
    /// Players spawn inside Arena's respawn booths (scene roots RESPAWN_1/RESPAWN_2, Respawn_1/Respawn_2 under
    /// SBG_Arena_Equator/SOO). Each booth exit is a sliding door assembly "Door_Sliding_Respawn (n)" (LODGroup
    /// root with the frame meshes and colliders) whose two leaves "Door_Sliding_Respawn_L" / "_R" carry their own
    /// LOD meshes, a "_COLLIDER" (LowPolyCollider) and a "_BALLISTIC_metalthin" MeshCollider
    /// (work/extract/equator/scenes/Arena_Equator.json). In Arena a script slides the leaves apart when the round
    /// starts; that script is not ported (MonoBehaviour data is unreadable), so the doors stay shut and the user
    /// cannot leave the booth (2026-09-20). SpawnPlacementRepairs already disables the SPAWN_BOUND barriers the
    /// same way; this deactivates the two leaves of every sliding respawn door in our scenes right after the
    /// spawn points are created (same postfix), i.e. before the player is placed, and logs a horizontal raycast
    /// from every player spawn towards the nearest door so the log says whether the doorway is clear.
    /// The NavMesh was baked with the leaves in place (booth interiors are islands for bots); that is a bake
    /// change, not a runtime one — see the doc.
    /// </summary>
    internal static class BoothDoorRepairs
    {
        const string Tag = "[ArenaMapPort][RaidFix]";
        const string DoorPrefix = "Door_Sliding_Respawn";
        static ConfigEntry<bool> _enabled;

        internal static void BindConfig()
        {
            _enabled = Plugin.Instance.Config.Bind("Spawns", "OpenRespawnDoors", true,
                "Deactivate the leaves of Arena's sliding respawn-booth doors at raid start so players can leave the " +
                "spawn booth (Arena opens them per round by a script that is not ported). Takes effect on the next raid.");
        }

        internal static void OpenAll(SpawnPointsCollection spawnPoints)
        {
            if (_enabled == null || !_enabled.Value || !ArenaMapRaid.IsLoaded())
            {
                return;
            }

            var doorRoots = new List<Transform>();
            int leaves = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        string name = t.name;
                        if (!name.StartsWith(DoorPrefix))
                        {
                            continue;
                        }
                        if (name == DoorPrefix + "_L" || name == DoorPrefix + "_R")
                        {
                            if (t.gameObject.activeSelf)
                            {
                                t.gameObject.SetActive(false);
                                leaves++;
                            }
                        }
                        else if (t.GetComponent<LODGroup>() != null)
                        {
                            doorRoots.Add(t);
                        }
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append($"{Tag} respawn doors: deactivated {leaves} sliding-door leaf(s) on {doorRoots.Count} door assembly(s)");
            foreach (var door in doorRoots)
            {
                sb.Append($"\n    door '{door.name}' at {door.position:F1}");
            }
            // is the way out clear? one horizontal raycast per player spawn towards the nearest door
            if (spawnPoints != null && doorRoots.Count > 0)
            {
                int mask = LayerMask.GetMask("LowPolyCollider", "HighPolyCollider", "Default");
                foreach (var sp in spawnPoints)
                {
                    if (sp == null || (sp.Categories & ESpawnCategoryMask.Player) == 0) continue;
                    Transform nearest = null;
                    float best = float.MaxValue;
                    foreach (var door in doorRoots)
                    {
                        float d = (door.position - sp.Position).sqrMagnitude;
                        if (d < best) { best = d; nearest = door; }
                    }
                    if (nearest == null) continue;
                    var from = sp.Position + Vector3.up * 1.2f;
                    var dir = nearest.position - sp.Position;
                    dir.y = 0f;
                    float dist = dir.magnitude;
                    if (dist < 0.1f) continue;
                    string verdict = Physics.Raycast(from, dir / dist, out var hit, dist + 2f, mask, QueryTriggerInteraction.Ignore)
                        ? (hit.collider.name.StartsWith(DoorPrefix)
                            ? $"BLOCKED by '{hit.collider.name}' at {hit.distance:F1} m (a leaf or frame collider still active)"
                            : $"hits '{hit.collider.name}' at {hit.distance:F1} m of {dist:F1} m")
                        : "clear";
                    sb.Append($"\n    spawn '{sp.Name}' -> door '{nearest.name}' {dist:F1} m: {verdict}");
                }
            }
            if (leaves > 0 || doorRoots.Count > 0)
            {
                Plugin.Log.LogInfo(sb.ToString());
            }
        }
    }
}
