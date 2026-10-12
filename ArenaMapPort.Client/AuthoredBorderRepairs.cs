using System.Collections.Generic;
using System.Text;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Invisible play-area walls the map descriptor authors (<see cref="ArenaMapDescriptor.AuthoredBorders"/>).
    ///
    /// What vanilla does: an EFT location is bounded by its own geometry; where a map needs an invisible limit it ships
    /// colliders for it. What Arena does on AirPit: the TeamFight area is a data polygon (`ExcludeLevelBorders` in the
    /// culling scene, read by its mode scripts, which are not ported), not a collider. The passport-hall facade has a
    /// 32 m opening (x -28..4 at z 82.5) into the unlit entrance hall and the bus apron opens onto the runway; the only
    /// solid across the opening is the `SPAWN_BOUND_FIGHTER` box that SpawnPlacementRepairs must disable (the players
    /// spawn inside it), and the 67 `LevelBorder` cubes bound only the ShootOut area (MapBorderRepairs removes them).
    /// Host raid 4 (2026-10-12, user report): from the north spawn the player walks north into the dark hall.
    ///
    /// What this does: right after the spawn points exist (SpawnInfiltrationRepairs' postfix, after BoothDoorRepairs),
    /// it raises one thin BoxCollider per authored segment on the movement layer (`LowPolyCollider`; bullets use the
    /// ballistic layer and pass), parented into an owned scene so it unloads with the map. Idempotent: the postfix runs
    /// twice per raid and an existing wall is kept. The NavMesh is baked with the descriptor's `bake_bounds`, which for
    /// AirPit is the walls' bounding box, so bots have no mesh beyond them.
    ///
    /// Verified: host raid 5 (2026-10-12), see docs/progress/airpit-release.md.
    /// </summary>
    internal static class AuthoredBorderRepairs
    {
        const string Tag = "[ArenaMapPort][RaidFix]";
        internal const string ObjectPrefix = "ArenaMapPort_Border_";
        const string LayerName = "LowPolyCollider";
        const int LayerFallback = 18;
        const float Thickness = 0.4f;
        static readonly Dictionary<string, ConfigEntry<bool>> _enabled = new Dictionary<string, ConfigEntry<bool>>();

        internal static void BindConfig()
        {
            foreach (var map in ArenaMapRegistry.All)
            {
                if (map.AuthoredBorders.Length == 0)
                {
                    continue;
                }
                _enabled[map.Key] = Plugin.Instance.Settings.Bind($"Map ({map.Key})", "AuthoredBorders", true,
                    $"Raise the {map.AuthoredBorders.Length} invisible walls the map descriptor authors along Arena's " +
                    "TeamFight outline (movement layer only, bullets pass). Off = the outline is open, as Arena's data " +
                    "ships it. Takes effect on the next raid.");
            }
        }

        internal static void Apply()
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || map.AuthoredBorders.Length == 0)
            {
                return;
            }
            if (_enabled.TryGetValue(map.Key, out var entry) && !entry.Value)
            {
                Plugin.Log.LogDebug($"{Tag} authored borders: {map.AuthoredBorders.Length} wall(s) skipped (Map ({map.Key})/AuthoredBorders = false)");
                return;
            }

            int layer = LayerMask.NameToLayer(LayerName);
            if (layer < 0)
            {
                layer = LayerFallback;
            }
            Scene owned = default;
            bool haveScene = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    owned = scene;
                    haveScene = true;
                    break;
                }
            }

            int created = 0, existing = 0, degenerate = 0;
            var sb = new StringBuilder();
            foreach (var border in map.AuthoredBorders)
            {
                string name = ObjectPrefix + border.Name;
                if (GameObject.Find(name) != null)
                {
                    existing++;
                    continue;
                }
                float dx = border.X1 - border.X0, dz = border.Z1 - border.Z0;
                float length = Mathf.Sqrt(dx * dx + dz * dz);
                float height = border.MaxY - border.MinY;
                if (length < 0.01f || height <= 0f)
                {
                    degenerate++;
                    continue;
                }
                var go = new GameObject(name) { layer = layer };
                go.transform.position = new Vector3((border.X0 + border.X1) * 0.5f, (border.MinY + border.MaxY) * 0.5f, (border.Z0 + border.Z1) * 0.5f);
                go.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 0f); // local +z runs along the segment
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(Thickness, height, length);
                if (haveScene)
                {
                    SceneManager.MoveGameObjectToScene(go, owned);
                }
                created++;
                sb.Append($"\n    '{border.Name}' ({border.X0:F1}, {border.Z0:F1}) -> ({border.X1:F1}, {border.Z1:F1}) {length:F1} m, y {border.MinY:F1}..{border.MaxY:F1}");
            }
            Plugin.Log.LogDebug($"{Tag} authored borders: created {created} wall(s) on layer {layer} ('{LayerMask.LayerToName(layer)}'), " +
                                $"{existing} already present, {degenerate} degenerate, scene={(haveScene ? owned.name : "<active>")}{sb}");
        }
    }
}
