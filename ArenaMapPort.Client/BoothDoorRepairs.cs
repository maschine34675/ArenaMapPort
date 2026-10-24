using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using ArenaMapPort.Shared;
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
    /// ("equator_roller_shutters_02 (4)" exists twice), hence full paths. Since 2026-10-23 that shutter is not opened any
    /// more: the spawns stand on Arena's own markers inside the booths and Equator lists no barrier at all
    /// (docs/progress/equator-booth-spawns.md); the paragraph is the history of the entry.
    ///
    /// Both steps log a horizontal raycast from every player spawn to each opening within 20 m so the log says
    /// whether the way out is clear. The NavMesh bake leaves these parts out (BakeNavMesh.RoundOpenedReason), so
    /// the baked mesh runs through every opening and the booth interiors join the main NavMesh component.
    ///
    /// Since 2026-10-17 every part is offered to <see cref="SpawnGates"/> before it is switched off: the part is
    /// registered with its closed pose either way, and while a mode plugin holds the gates closed it stays active
    /// (sealed for bots by a NavMesh carver) and opens on the plugin's release instead; the raycast then reports
    /// BLOCKED on purpose. Since 2026-10-17 the descriptor says which barriers that applies to: <c>StartGates</c> are
    /// offered (AirPit: the bus doors only), <c>HeldGates</c> stay closed at raid start in every mode and open only on
    /// the plugin's release (AirPit's passport control, the original north team box's gate), every other opened
    /// barrier is switched off here without a way back (shop shells, portal fences, blockout panels).
    ///
    /// Since 2026-10-22 (user: "in Endstellung parken") a part that Arena ANIMATES is not switched off but parked: Bowl's
    /// BTR, ambulance, army gate leaves and respawn gate carry the end pose of their open clip in the descriptor
    /// (<see cref="ArenaMapPort.Shared.ArenaMapDescriptor.DoorPoses"/>, docs/progress/door-poses.md); the part stays active,
    /// its nodes snap to that pose (<see cref="SpawnGates.Park"/>) and a later hold and release close and drive it again.
    /// A part whose poses cannot be applied logs one Warning and is switched off as before.
    ///
    /// Since 2026-10-23 (user: Equator's spawns behind the booth doors, "alle Tore aufmachen" on Bay5) the same holds for the DotAnimator
    /// poses of Equator and Bay5 (docs/progress/door-poses.md, "DotAnimator"): the ten booth leaves are found by NAME and looked up by
    /// the parent assembly's hierarchy path plus world position (two assemblies share a path), Bay5's roller gates move their barrier ROOT
    /// (a pose row with part ""), and a barrier that has any pose row keeps the parts without a pose standing (the depot gates' Telfer
    /// posts and frame meshes are structure Arena leaves in place; only a barrier without poses hides every non-frame part). A path in
    /// <c>OpenedBarriers</c> opens EVERY instance that carries it (Bay5 has two depot gates under one path).
    ///
    /// Since 2026-10-24 (user: AirPit "the same way as Equator and Bay5") AirPit's bus doors and passport control carry rows too
    /// (docs/progress/door-poses.md, "Runtime 3"). BUS_DOORS: the three front assemblies are parked at the open pose (hinges and leaves
    /// swung out, borrowed from the static back row), the three back ones have no row and stand open as Arena built them (the
    /// keep-structure rule). PASSPORT_CONTROL_DOORS (a held gate): the 16 leaves reuse six names, so a child takes the rows of its
    /// name narrowed by its own closed position (<see cref="SpawnGates.RowsForPart"/>); a child whose name has rows but none for its
    /// position logs one Warning and is handled like a part without a pose (held closed, switched off on the release), never left
    /// standing as structure. RED and GREEN carry switch rows (the lamp flip). Without a hold the held gate stays closed as before.
    /// </summary>
    internal static class BoothDoorRepairs
    {
        const string Tag = "[RaidFix]";
        const string DoorPrefix = SpawnGateModel.DoorPrefix;
        const string FramePart = "frame";
        const float RayReach = 20f; // openings within this distance of a player spawn get a raycast verdict
        static ConfigEntry<bool> _doors;
        static ConfigEntry<bool> _barriers;

        internal static void BindConfig()
        {
            _doors = Plugin.Instance.Settings.Bind("Spawns", "OpenRespawnDoors", true,
                "Open Arena's sliding respawn-booth doors at raid start so players can leave the spawn booth: the leaves " +
                "stand at the open pose of Arena's own animation (Equator), or are deactivated when the map has no pose for " +
                "them (Arena opens them per round by a script that is not ported). Takes effect on the next raid.");
            _barriers = Plugin.Instance.Settings.Bind("Spawns", "OpenSpawnBarriers", true,
                "Open the static scene objects the map lists as spawn barriers (Arena opens them per round by a script " +
                "that is not ported): Bowl's vehicles and gates, Bay5's warehouse roller gates and depot gates, AirPit's front bus doors and " +
                "Arena's animated parts in general are parked at the open pose of Arena's animation, every other non-frame part of a " +
                "barrier without an animation is deactivated (AirPit's portal fences and shop shells). A barrier " +
                "Arena animates keeps its parts without an animation standing (AirPit's back bus-door row). A map's held gates (AirPit's passport control) are not " +
                "governed by this switch: they stay closed until a mode plugin releases them, and open at raid start only " +
                "when Spawns/SpawnGates or SpawnGateCarve is off. Takes effect on the next raid.");
        }

        internal static void OpenAll(SpawnPointsCollection spawnPoints)
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return;
            }
            bool doors = _doors != null && _doors.Value;
            bool barriers = _barriers != null && _barriers.Value;
            var map = ArenaMapRaid.CurrentMap;
            bool heldGates = map != null && map.HeldGates.Length > 0; // registered even with OpenSpawnBarriers off
            if (!doors && !barriers && !heldGates)
            {
                return;
            }

            var openings = new List<Transform>();
            var sb = new StringBuilder();
            if (doors)
            {
                OpenDoors(openings, sb);
            }
            if (barriers || heldGates)
            {
                OpenBarriers(openings, sb, barriers);
            }
            SpawnGates.SealParked("parked parts registered after the navmesh install"); // the booth leaves and the barrier parts, once
            if (heldGates)
            {
                SpawnGates.SealHeldGates("held gates registered after the navmesh install");
            }
            ReportRays(spawnPoints, openings, sb);
            if (sb.Length > 0)
            {
                Plugin.Log.LogDebug($"{Tag} spawn openings:{sb}");
            }
        }

        // Arena's sliding booth doors: both leaves of every assembly in our scenes. A leaf the descriptor has a pose for (matched
        // by its assembly's hierarchy path and world position) is parked at Arena's open pose, any other leaf is deactivated.
        static void OpenDoors(List<Transform> openings, StringBuilder sb)
        {
            var map = ArenaMapRaid.CurrentMap;
            var doorRoots = new List<Transform>();
            int leaves = 0, held = 0, parked = 0, unposed = 0;
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
                            var poses = PosesOfLeaf(map, t);
                            if (poses.Length == 0 && map != null && map.DoorPoses.Length > 0) unposed++;
                            SpawnGates.NotePoses(t, poses); // Arena's open pose of this leaf, resolved while it is still closed
                            if (SpawnGates.Offer(t)) { held++; continue; } // registered either way; a hold keeps the booth closed for now
                            if (SpawnGates.Park(t)) { parked++; continue; } // Arena slides it: stands at the open pose, active
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
            if (unposed > 0)
            {
                Plugin.Log.LogWarning($"{Tag} {unposed} sliding-door leaf(s) have no door pose in the descriptor (assembly path or position differs); they switch off like a plain booth door");
            }
            if (leaves == 0 && parked == 0 && doorRoots.Count == 0)
            {
                return;
            }
            sb.Append($"\n  respawn doors: deactivated {leaves} sliding-door leaf(s) on {doorRoots.Count} door assembly(s)" +
                      (parked > 0 ? $", parked {parked} leaf(s) at the open pose" : "") +
                      (held > 0 ? $", {held} leaf(s) {SpawnGates.Describe()}" : ""));
            foreach (var door in doorRoots)
            {
                sb.Append($"\n    door '{door.name}' at {door.position:F1}");
            }
            openings.AddRange(doorRoots);
        }

        // Static barriers the descriptor names by hierarchy path, EVERY instance of each path. A barrier without pose rows: deactivate
        // every child that is not frame. A barrier Arena animates (it has pose rows): the root itself moves when a row has part "" (Bay5's
        // rollers), a child with a row is parked, a child without one stays standing (structure: Telfer posts, frame meshes). A held
        // gate's children are registered with SpawnGates instead and stay closed; openPlain false (the OpenSpawnBarriers switch is
        // off) registers only those.
        static void OpenBarriers(List<Transform> openings, StringBuilder sb, bool openPlain)
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || map.OpenedBarriers.Length == 0)
            {
                return;
            }
            foreach (var path in map.OpenedBarriers)
            {
                var role = map.GateRoleOf(path);
                if (role != ArenaGateRole.Held && !openPlain)
                {
                    continue;
                }
                var found = FindAllByPath(path);
                if (found.Count == 0)
                {
                    sb.Append($"\n  spawn barrier '{path}': NOT FOUND in the loaded scenes (scene changed? path typo?)");
                    Plugin.Log.LogWarning($"{Tag} spawn barrier '{path}' not found in the loaded scenes — the spawn area stays closed");
                    continue;
                }
                if (found.Count > 1)
                {
                    sb.Append($"\n  spawn barrier '{path}': {found.Count} instance(s) share this path — opening all of them");
                }
                foreach (var barrier in found)
                {
                    int opened = 0, kept = 0, standing = 0, alreadyOff = 0, held = 0, parked = 0, switches = 0;
                    var at = barrier.position;
                    // Pose rows of THIS instance (a row with an `at` belongs to the instance standing there; rows without one to every instance).
                    var rootPoses = map.DoorPosesOf(path, "", at.x, at.y, at.z);
                    bool rootDone = false;
                    if (rootPoses.Length > 0 && barrier.gameObject.activeSelf)
                    {
                        SpawnGates.NotePoses(barrier, rootPoses); // the whole root moves (Bay5's rollers); resolved while it is still closed
                        if (SpawnGates.HasPoses(barrier))
                        {
                            if (role == ArenaGateRole.Held && SpawnGates.OfferHeld(barrier)) { held++; rootDone = true; } // closed until the mode plugin's release
                            else if (role == ArenaGateRole.Start && SpawnGates.Offer(barrier)) { held++; rootDone = true; } // a hold keeps it closed for now
                            else if (SpawnGates.Park(barrier)) { parked++; rootDone = true; } // stands at the open pose, active
                        }
                    }
                    // A barrier Arena animates keeps the parts without a pose of their own standing; a root whose pose failed to apply
                    // (one Warning) is hidden child by child like a barrier without poses. An instance that none of the path's rows
                    // names (its root stands elsewhere than every row's `at`) is hidden the old way too, with one Warning.
                    var instancePoses = map.DoorPosesOfBarrier(path, at.x, at.y, at.z);
                    if (instancePoses.Length == 0 && PathHasPoseRows(map, path))
                    {
                        Plugin.Log.LogWarning($"{Tag} spawn barrier '{path}': the instance at {at:F3} matches no door pose row (every row's 'at' " +
                                              "names another position), so its non-frame parts are switched off like a barrier without poses");
                    }
                    bool keepStructure = instancePoses.Length > 0 && !(rootPoses.Length > 0 && !rootDone);
                    for (int i = 0; i < barrier.childCount && !rootDone; i++)
                    {
                        var part = barrier.GetChild(i);
                        if (part.name.IndexOf(FramePart, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            kept++;
                            continue;
                        }
                        var named = map.DoorPosesOf(path, part.name, at.x, at.y, at.z); // every row of this NAME (AirPit's passport leaves repeat six)
                        var poses = SpawnGates.RowsForPart(part, named);                  // ... of THIS child: told apart by the closed position
                        bool unmatched = named.Length > 0 && poses.Length == 0;           // rows for the name, none for this position
                        if (poses.Length == 0 && keepStructure && !unmatched)
                        {
                            standing++;
                            continue;
                        }
                        if (part.gameObject.activeSelf)
                        {
                            // a name none of whose rows is this child's goes in whole: NotePoses logs its one Warning and the part counts as pose-less
                            SpawnGates.NotePoses(part, unmatched ? named : poses); // Arena's end pose, resolved while the part is still closed
                            if (role == ArenaGateRole.Held && SpawnGates.OfferHeld(part)) { held++; switches += SpawnGates.SwitchCountOf(part); continue; } // closed until the mode plugin's release
                            if (role == ArenaGateRole.Start && SpawnGates.Offer(part)) { held++; switches += SpawnGates.SwitchCountOf(part); continue; } // registered either way; a hold keeps the enclosure closed for now
                            if (SpawnGates.Park(part)) { parked++; switches += SpawnGates.SwitchCountOf(part); continue; } // Arena animates it: stands at the open pose, active
                            part.gameObject.SetActive(false);
                            opened++;
                        }
                        else
                        {
                            alreadyOff++;
                        }
                    }
                    sb.Append($"\n  spawn barrier '{barrier.name}' at {barrier.position:F1}: deactivated {opened} part(s), " +
                              $"{alreadyOff} already off, kept {kept} frame part(s)" +
                              (standing > 0 ? $", {standing} part(s) without a pose left standing" : "") +
                              (parked > 0 ? $", parked {parked} part(s) at the open pose" : "") +
                              (held > 0 ? $", {held} part(s) {(role == ArenaGateRole.Held ? SpawnGates.DescribeHeldGate() : SpawnGates.Describe())}" : "") +
                              (switches > 0 ? $", {switches} switch(es)" : ""));
                    if (role != ArenaGateRole.Held) openings.Add(barrier); // a held gate is BLOCKED on purpose
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

        // The poses of one booth leaf: the leaf's parent is the door assembly (a barrier root in the descriptor's sense), told apart
        // from its namesake by its hierarchy path plus world position (the two unnumbered SPAWN_DOORS/Door_Sliding_Respawn).
        static ArenaDoorPose[] PosesOfLeaf(ArenaMapDescriptor map, Transform leaf)
        {
            var assembly = leaf.parent;
            if (map == null || assembly == null || map.DoorPoses.Length == 0) return new ArenaDoorPose[0];
            var at = assembly.position;
            return map.DoorPosesOf(HierarchyPath(assembly), leaf.name, at.x, at.y, at.z);
        }

        // Does the descriptor carry any pose row for this barrier path, whatever instance it names?
        static bool PathHasPoseRows(ArenaMapDescriptor map, string path)
        {
            foreach (var pose in map.DoorPoses)
            {
                if (pose.Barrier == path) return true;
            }
            return false;
        }

        // "SceneRoot/Child/Grandchild (n)": the names from the scene root down, the form the descriptor's paths take.
        internal static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, '/').Insert(0, p.name);
            return sb.ToString();
        }

        static IEnumerable<Transform> ChildrenOf(Transform t)
        {
            for (int i = 0; i < t.childCount; i++) yield return t.GetChild(i);
        }

        // Every instance of "Root/Child/Grandchild (n)": the first segment names scene roots in our scenes, then ALL children of
        // ALL matches per level (DoorPoseModel.ResolveAll). Bay5's depot gate path exists twice; Transform.Find would give one.
        internal static List<Transform> FindAllByPath(string path)
        {
            var roots = new List<Transform>();
            foreach (var root in OwnedSceneRoots()) roots.Add(root.transform);
            return DoorPoseModel.ResolveAll(roots, path, t => t.name, ChildrenOf);
        }

        // "Root/Child/Grandchild (n)" — the first segment is a scene root object in one of our scenes, the rest is
        // resolved with Transform.Find (which takes '/'-separated paths and returns the first match per level).
        // The first instance only; the barrier opening reads FindAllByPath.
        internal static List<Transform> FindByPath(string path) // also LampFillRepairs' template lookup
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
