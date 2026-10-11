using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using EFT.Game.Spawning;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// exfils-stage2 follow-up (docs/progress/exfils-stage2.md, "Respawn booths" / "Spawn barriers") — open Arena's
    /// respawn booths and the static barriers that close a spawn area for the round.
    ///
    /// Players spawn inside Arena's respawn booths (scene roots RESPAWN_1/RESPAWN_2, Respawn_1/Respawn_2 under
    /// SBG_Arena_Equator/SOO). Each booth exit is a sliding door assembly "Door_Sliding_Respawn (n)" (LODGroup
    /// root with the frame meshes and colliders) whose two leaves "Door_Sliding_Respawn_L" / "_R" carry their own
    /// LOD meshes, a "_COLLIDER" (LowPolyCollider) and a "_BALLISTIC_metalthin" MeshCollider
    /// (work/extract/equator/scenes/Arena_Equator.json). In Arena a script slides the leaves apart when the round
    /// starts; that script is not ported (MonoBehaviour data is unreadable), so the doors stay shut and the user
    /// cannot leave the booth (2026-09-20). SpawnPlacementRepairs already disables the SPAWN_BOUND barriers the
    /// same way; this deactivates the two leaves of every sliding respawn door in our scenes right after the
    /// spawn points are created (same postfix), i.e. before the player is placed.
    ///
    /// Spawn barriers (2026-09-20 evening, user: "noch ein weiteres Rolltor beim Spieler-Spawn"): the Usec spawn
    /// sits in the FCK hall, whose corner exit is a roller shutter — plain static geometry under
    /// SBG_Arena_Equator/SOO_LOD0 (LODGroup root; children roller_shutters_02_LOD0..2, _SHADOW_LOD0, _COLLIDER on
    /// LowPolyCollider, _BALLISTIC_metalthin on HighPolyCollider, plus the same set for roller_shutters_frame_02),
    /// no door component, no script. Equator has 20 such shutters; 19 line the outer walls (decoration in front of
    /// unmodelled shop interiors), only this one closes a spawn area, so the map descriptor names the ones to open
    /// by hierarchy path (<see cref="ArenaMapPort.Shared.ArenaMapDescriptor.OpenedBarriers"/>) and this deactivates
    /// every child that is not part of the frame. Object names are not unique in Arena scenes
    /// ("equator_roller_shutters_02 (4)" exists twice), hence full paths.
    ///
    /// Both steps log a horizontal raycast from every player spawn to each opening within 20 m so the log says
    /// whether the way out is clear. The NavMesh was baked with the leaves and shutters in place (booth and hall
    /// interiors are islands for bots); that is a bake change, not a runtime one — see the doc.
    /// </summary>
    internal static class BoothDoorRepairs
    {
        const string Tag = "[ArenaMapPort][RaidFix]";
        const string DoorPrefix = "Door_Sliding_Respawn";
        const string FramePart = "frame";
        const float RayReach = 20f; // openings within this distance of a player spawn get a raycast verdict
        static ConfigEntry<bool> _doors;
        static ConfigEntry<bool> _barriers;

        internal static void BindConfig()
        {
            _doors = Plugin.Instance.Settings.Bind("Spawns", "OpenRespawnDoors", true,
                "Deactivate the leaves of Arena's sliding respawn-booth doors at raid start so players can leave the " +
                "spawn booth (Arena opens them per round by a script that is not ported). Takes effect on the next raid.");
            _barriers = Plugin.Instance.Settings.Bind("Spawns", "OpenSpawnBarriers", true,
                "Deactivate the moving parts (everything but the frame) of the static scene objects the map lists as " +
                "spawn barriers — on Equator the roller shutter that closes the FCK hall at the Usec spawn (Arena opens " +
                "it per round by a script that is not ported). Takes effect on the next raid.");
        }

        internal static void OpenAll(SpawnPointsCollection spawnPoints)
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return;
            }
            bool doors = _doors != null && _doors.Value;
            bool barriers = _barriers != null && _barriers.Value;
            if (!doors && !barriers)
            {
                return;
            }

            var openings = new List<Transform>();
            var sb = new StringBuilder();
            if (doors)
            {
                OpenDoors(openings, sb);
            }
            if (barriers)
            {
                OpenBarriers(openings, sb);
            }
            ReportRays(spawnPoints, openings, sb);
            if (sb.Length > 0)
            {
                Plugin.Log.LogDebug($"{Tag} spawn openings:{sb}");
            }
        }

        // Arena's sliding booth doors: deactivate both leaves of every assembly in our scenes.
        static void OpenDoors(List<Transform> openings, StringBuilder sb)
        {
            var doorRoots = new List<Transform>();
            int leaves = 0;
            foreach (var root in OwnedSceneRoots())
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
            if (leaves == 0 && doorRoots.Count == 0)
            {
                return;
            }
            sb.Append($"\n  respawn doors: deactivated {leaves} sliding-door leaf(s) on {doorRoots.Count} door assembly(s)");
            foreach (var door in doorRoots)
            {
                sb.Append($"\n    door '{door.name}' at {door.position:F1}");
            }
            openings.AddRange(doorRoots);
        }

        // Static barriers the descriptor names by hierarchy path: deactivate every child that is not frame.
        static void OpenBarriers(List<Transform> openings, StringBuilder sb)
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || map.OpenedBarriers.Length == 0)
            {
                return;
            }
            foreach (var path in map.OpenedBarriers)
            {
                var found = FindByPath(path);
                if (found.Count == 0)
                {
                    sb.Append($"\n  spawn barrier '{path}': NOT FOUND in the loaded scenes (scene changed? path typo?)");
                    Plugin.Log.LogWarning($"{Tag} spawn barrier '{path}' not found in the loaded scenes — the spawn area stays closed");
                    continue;
                }
                if (found.Count > 1)
                {
                    sb.Append($"\n  spawn barrier '{path}': {found.Count} objects share this path — opening all of them");
                }
                foreach (var barrier in found)
                {
                    int opened = 0, kept = 0, alreadyOff = 0;
                    for (int i = 0; i < barrier.childCount; i++)
                    {
                        var part = barrier.GetChild(i);
                        if (part.name.IndexOf(FramePart, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            kept++;
                            continue;
                        }
                        if (part.gameObject.activeSelf)
                        {
                            part.gameObject.SetActive(false);
                            opened++;
                        }
                        else
                        {
                            alreadyOff++;
                        }
                    }
                    sb.Append($"\n  spawn barrier '{barrier.name}' at {barrier.position:F1}: deactivated {opened} part(s), " +
                              $"{alreadyOff} already off, kept {kept} frame part(s)");
                    openings.Add(barrier);
                }
            }
        }

        // Is the way out clear? One horizontal raycast per player spawn towards every opening within RayReach.
        static void ReportRays(SpawnPointsCollection spawnPoints, List<Transform> openings, StringBuilder sb)
        {
            if (spawnPoints == null || openings.Count == 0)
            {
                return;
            }
            int mask = LayerMask.GetMask("LowPolyCollider", "HighPolyCollider", "Default");
            foreach (var sp in spawnPoints)
            {
                if (sp == null || (sp.Categories & ESpawnCategoryMask.Player) == 0) continue;
                foreach (var opening in openings)
                {
                    var from = sp.Position + Vector3.up * 1.2f;
                    var dir = opening.position - sp.Position;
                    dir.y = 0f;
                    float dist = dir.magnitude;
                    if (dist < 0.1f || dist > RayReach) continue;
                    string verdict;
                    if (Physics.Raycast(from, dir / dist, out var hit, dist + 2f, mask, QueryTriggerInteraction.Ignore))
                    {
                        verdict = hit.collider.name.StartsWith(DoorPrefix) || hit.transform.IsChildOf(opening)
                            ? $"BLOCKED by '{hit.collider.name}' at {hit.distance:F1} m (a part of the opening is still active)"
                            : $"hits '{hit.collider.name}' at {hit.distance:F1} m of {dist:F1} m";
                    }
                    else
                    {
                        verdict = "clear";
                    }
                    sb.Append($"\n    spawn '{sp.Name}' -> '{opening.name}' {dist:F1} m: {verdict}");
                }
            }
        }

        static IEnumerable<GameObject> OwnedSceneRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    yield return root;
                }
            }
        }

        // "Root/Child/Grandchild (n)" — the first segment is a scene root object in one of our scenes, the rest is
        // resolved with Transform.Find (which takes '/'-separated paths and returns the first match per level).
        static List<Transform> FindByPath(string path)
        {
            var found = new List<Transform>();
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            string rest = slash < 0 ? null : path.Substring(slash + 1);
            foreach (var root in OwnedSceneRoots())
            {
                if (root.name != rootName)
                {
                    continue;
                }
                if (rest == null)
                {
                    found.Add(root.transform);
                    continue;
                }
                var t = root.transform.Find(rest);
                if (t != null)
                {
                    found.Add(t);
                }
            }
            return found;
        }
    }
}
