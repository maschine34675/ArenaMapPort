using System.Collections.Generic;
using System.Text;
using EFT.Game.Spawning;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Moves our player spawn points out of Arena's own spawn enclosures before spawn selection.
    ///
    /// The 2026-09-14 19:33 probe put the player at (-5.73, 0.08, 48.59) overlapping
    /// 'SPAWN_BOUND_FIGHTER' (Default layer, solid) and 'Equator_Respawn_01_COLLIDER' (LowPolyCollider):
    /// AuthorScriptsScene.ResolveSpawnPositions takes Arena's own SpawnPointMarker sample positions, which
    /// sit inside Arena's respawn booths and pre-round barriers. Arena opens those per round; SPT never
    /// does, so the player spawned boxed in even after the marker colliders became triggers.
    ///
    /// Runs from SpawnInfiltrationFixPostfix (CreateFromScene, BaseLocalGame.cs:526), i.e. before
    /// SelectSpawnPoint reads SpawnPoint.Position (BaseLocalGame.cs:576). The NavMesh is not installed
    /// yet at that point (BotsController.Init comes later), so placement uses physics queries only.
    /// </summary>
    internal static class SpawnPlacementRepairs
    {
        const float CapsuleRadius = 0.35f;
        const float CapsuleHeight = 1.8f;
        const float MaxSearchRadius = 40f;
        const float RingStep = 1.5f;
        const int Directions = 16;
        const float BoothMargin = 0.75f;
        const float MinSpacing = 1.2f;

        internal static void Apply(SpawnPointsCollection spawnPoints)
        {
            int mask = PlayerCollisionMask();
            var boothBounds = new List<Bounds>();
            int barriersDisabled = ScanOwnedScenes(boothBounds);
            // EFT can switch Physics.autoSyncTransforms off (PhysicsExtensions.cs:332); make the disabled
            // barriers visible to the queries below either way.
            Physics.SyncTransforms();

            var placed = new List<Vector3>();
            var log = new StringBuilder();
            foreach (var point in spawnPoints)
            {
                if (!(point is SpawnPoint spawn))
                {
                    continue;
                }

                Vector3 origin = spawn.Position;
                if (IsValid(origin, mask, boothBounds, placed))
                {
                    placed.Add(origin);
                    continue;
                }

                if (TryFindFree(origin, mask, boothBounds, placed, out var position))
                {
                    spawn.Position = position;
                    // Move the marker too: its BoxCollider bounds back ISpawnPointCollider.Contains
                    // (SpawnPointMarker.cs:139-165), which bot and group spawn checks read
                    // (SpawnPointExtension.cs:88/141/207).
                    if (spawnPoints._dictionary != null &&
                        spawnPoints._dictionary.TryGetValue(point, out var marker) && marker != null)
                    {
                        marker.transform.position = position;
                    }
                    placed.Add(position);
                    log.Append($" '{spawn.Name}' {origin} -> {position} ({Vector3.Distance(origin, position):0.0} m);");
                }
                else
                {
                    log.Append($" '{spawn.Name}' {origin}: no free spot within {MaxSearchRadius} m, left unchanged;");
                }
            }

            Plugin.Log.LogDebug($"[ArenaMapPort][RaidFix] spawn placement: disabled {barriersDisabled} SPAWN_BOUND " +
                                $"barrier collider(s), {boothBounds.Count} respawn-booth collider(s) avoided;" +
                                (log.Length > 0 ? log.ToString() : " all spawn points already free"));
        }

        // Disables Arena's pre-round barriers and collects respawn-booth bounds from our own scenes.
        static int ScanOwnedScenes(List<Bounds> boothBounds)
        {
            int disabled = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                    {
                        string name = collider.gameObject.name;
                        if (name.StartsWith("SPAWN_BOUND"))
                        {
                            if (collider.enabled)
                            {
                                collider.enabled = false;
                                disabled++;
                            }
                        }
                        else if (name.Contains("_Respawn_") && collider.enabled && !collider.isTrigger &&
                                 collider.gameObject.activeInHierarchy)
                        {
                            var bounds = collider.bounds;
                            bounds.Expand(BoothMargin * 2f);
                            boothBounds.Add(bounds);
                        }
                    }
                }
            }
            return disabled;
        }

        static bool TryFindFree(Vector3 origin, int mask, List<Bounds> boothBounds, List<Vector3> placed,
            out Vector3 position)
        {
            for (float radius = RingStep; radius <= MaxSearchRadius; radius += RingStep)
            {
                for (int d = 0; d < Directions; d++)
                {
                    float angle = d * (360f / Directions) * Mathf.Deg2Rad;
                    var candidate = origin + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                    // Stay on the spawn's own floor level: look for ground from below typical ceiling height.
                    if (!Physics.Raycast(candidate + Vector3.up * 1.6f, Vector3.down, out var hit, 4f, mask,
                            QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    var grounded = hit.point + Vector3.up * 0.02f;
                    if (IsValid(grounded, mask, boothBounds, placed))
                    {
                        position = grounded;
                        return true;
                    }
                }
            }

            position = origin;
            return false;
        }

        static bool IsValid(Vector3 feet, int mask, List<Bounds> boothBounds, List<Vector3> placed)
        {
            for (int i = 0; i < boothBounds.Count; i++)
            {
                if (boothBounds[i].Contains(feet + Vector3.up * 0.9f))
                {
                    return false;
                }
            }
            for (int i = 0; i < placed.Count; i++)
            {
                if ((placed[i] - feet).sqrMagnitude < MinSpacing * MinSpacing)
                {
                    return false;
                }
            }

            var bottom = feet + Vector3.up * (CapsuleRadius + 0.1f);
            var top = feet + Vector3.up * (CapsuleHeight - CapsuleRadius);
            if (Physics.CheckCapsule(bottom, top, CapsuleRadius, mask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            // Needs ground right below, so the player neither falls nor stands on a ledge edge.
            return Physics.Raycast(feet + Vector3.up * 0.3f, Vector3.down, 1f, mask, QueryTriggerInteraction.Ignore);
        }

        // Layers the player's character collider collides with, from the game's collision matrix.
        static int PlayerCollisionMask()
        {
            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer < 0)
            {
                return Physics.DefaultRaycastLayers;
            }

            int mask = 0;
            for (int layer = 0; layer < 32; layer++)
            {
                if (layer != playerLayer && !Physics.GetIgnoreLayerCollision(playerLayer, layer))
                {
                    mask |= 1 << layer;
                }
            }
            return mask != 0 ? mask : Physics.DefaultRaycastLayers;
        }
    }
}
