using System.Collections.Generic;
using System.Text;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Arena's own play-area walls for a map whose Scripts scene holds them (<see cref="ArenaMapDescriptor.LevelBorderBoxes"/>; Yard).
    ///
    /// What vanilla does: an EFT location is bounded by its own geometry, and where it needs an invisible limit it ships colliders. What Arena
    /// does on Yard: it bounds the TeamFight area with 118 static cubes on layer 29 (LevelBorder), each a BoxCollider with a disabled renderer, that
    /// stand over fences, roofs and wall tops so that nobody climbs or vaults out. 14 of them live in imported scenes (11 under
    /// TeamFight_LevelBorders, 3 closing walls under the DOORS parts) and stay scene objects because the descriptor says KeepLevelBorders; the other
    /// 104 sit in Arena_Yard_Scripts, the one scene no package imports (the port authors its own Scripts scene under that name), so the port has
    /// no object for them. tools/extract/extract_level_borders.py reads them from the Arena install into tools/maps/yard.borders.json.
    ///
    /// What this does: right after the spawn points exist (SpawnInfiltrationRepairs' postfix, after AuthoredBorderRepairs) it raises one invisible
    /// BoxCollider per usable descriptor row, at the row's world centre, rotation and size, on the LevelBorder layer (always 29): by EFT's physics matrix (read from globalgamemanagers, row 29 = layers 3, 6, 7 and 8) exactly what a kept cube does, it stops the
    /// player and nothing else. No renderer and no ballistic component, like the kept cubes. One parent object (ArenaMapPort_LevelBorders) holds
    /// the 104 children (ArenaMapPort_LevelBorder_NNN, never a name that starts with LevelBorder, which MapBorderRepairs would match); the parent is
    /// a root of the map's Scripts scene, so it unloads with the map. The pass is idempotent (the postfix runs twice per raid): an existing parent
    /// with one child per usable row is kept, anything else is removed and raised again. The parent is found among the scene's root objects, never
    /// with GameObject.Find and never one find per row. MapBorderRepairs scans a scene when it loads, so it never sees these objects.
    ///
    /// Bots: none needed. Part 1 of PLAN-YARD item 10 modelled the port bake with the 118 cubes as solids and found that they take 4.08 m2 (4.77 m2 with the kept cubes as sources, review 2026-11-21) of the
    /// yard at its edges and cut off 0.00 m2 for every row, so no row gets a carver and the bake takes no source from the sidecar
    /// (LevelBorderBoxModel). The rows are a descriptor literal, mirrored number for number by validate_map_descriptors.py and YardMapTests.
    ///
    /// Host test pending: Yard has been registered since PLAN-YARD item 15, but its bundles are not built yet. Walking into the walls at several edges, the log line below
    /// (created 104, layer 29 'LevelBorder'), the F10 report of one wall (layer LevelBorder, ballistic none) and the walls gone after the raid
    /// are the checks of the host stage.
    /// </summary>
    internal static class LevelBorderBoxRepairs
    {
        const string Tag = "[RaidFix]";
        static readonly Dictionary<string, ConfigEntry<bool>> _enabled = new Dictionary<string, ConfigEntry<bool>>();

        /// <summary>One developer key per map that has rows, bound like AuthoredBorders' (Yard is the one map with rows; the six older maps have none and bind nothing.)</summary>
        internal static void BindConfig()
        {
            foreach (var map in ArenaMapRegistry.All)
            {
                if (map.LevelBorderBoxes.Length == 0)
                {
                    continue;
                }
                _enabled[map.Key] = Plugin.Instance.Settings.Bind($"Map ({map.Key})", "LevelBorderWalls", true,
                    $"Raise the {map.LevelBorderBoxes.Length} invisible walls of Arena's own level-border cubes that the package does not import " +
                    "(LevelBorder layer: they stop the player only). Off = those edges are open, as the imported scenes alone leave them. " +
                    "Takes effect on the next raid.");
            }
        }

        internal static void Apply()
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || map.LevelBorderBoxes.Length == 0)
            {
                return;
            }
            if (_enabled.TryGetValue(map.Key, out var entry) && !entry.Value)
            {
                Plugin.Log.LogDebug($"{Tag} level borders: {map.LevelBorderBoxes.Length} wall(s) skipped (Map ({map.Key})/LevelBorderWalls = false)");
                return;
            }

            var boxes = map.LevelBorderBoxes;
            int usable = LevelBorderBoxModel.CountUsable(boxes);
            int layer = LevelBorderBoxModel.ResolveLayer(LayerMask.NameToLayer(LevelBorderBoxModel.LayerName));

            Scene owned = SceneManager.GetSceneByName(map.ScriptsSceneName);
            bool haveScene = owned.IsValid() && owned.isLoaded;
            if (!haveScene)
            {
                for (int i = 0; i < SceneManager.sceneCount && !haveScene; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && ArenaMapRaid.IsOwnedScene(scene.name))
                    {
                        owned = scene;
                        haveScene = true;
                    }
                }
            }

            if (!haveScene)
            {
                // CurrentMap is non-null only while the map's Scripts scene is loaded, so this cannot happen in a raid; without a scene to own the
                // walls the pass could not tell its own set from a second one, so it raises nothing.
                Plugin.Log.LogDebug($"{Tag} level borders: no loaded scene of '{map.Key}' to own the walls; none raised");
                return;
            }

            GameObject parent = FindParent(owned);
            var pass = LevelBorderBoxModel.PlanPass(parent != null, parent != null ? parent.transform.childCount : 0, usable);
            if (pass == LevelBorderBoxModel.Pass.Keep)
            {
                Plugin.Log.LogDebug($"{Tag} level borders: {usable} wall(s) already present, scene={owned.name}");
                return;
            }
            if (pass == LevelBorderBoxModel.Pass.Rebuild && parent != null)
            {
                Plugin.Log.LogDebug($"{Tag} level borders: {parent.transform.childCount} child(ren) for {usable} usable row(s); removing the set and raising it again");
                Object.DestroyImmediate(parent);
            }
            if (pass == LevelBorderBoxModel.Pass.Nothing || usable == 0)
            {
                return; // nothing to raise; a set left over for a map without a usable row was removed above and is not replaced by an empty parent
            }

            var root = new GameObject(LevelBorderBoxModel.ParentName);
            SceneManager.MoveGameObjectToScene(root, owned);   // first, so a throw below leaves the half-built set in the owned scene, where the next pass finds and rebuilds it
            int created = 0, skipped = 0;
            for (int i = 0; i < boxes.Length; i++)
            {
                var box = boxes[i];
                if (!LevelBorderBoxModel.IsUsable(box))
                {
                    skipped++;
                    continue;
                }
                LevelBorderBoxModel.NormalizedRotation(box, out float qx, out float qy, out float qz, out float qw);
                var go = new GameObject(LevelBorderBoxModel.ChildName(i)) { layer = layer };
                var t = go.transform;
                t.SetParent(root.transform, false); // the parent stays at the origin, so local = world
                t.SetPositionAndRotation(new Vector3(box.CenterX, box.CenterY, box.CenterZ), new Quaternion(qx, qy, qz, qw));
                var collider = go.AddComponent<BoxCollider>();
                collider.size = new Vector3(box.SizeX, box.SizeY, box.SizeZ);
                created++;
            }
            var sb = new StringBuilder();
            if (skipped > 0)
            {
                sb.Append($", {skipped} unusable row(s) skipped");
            }
            Plugin.Log.LogDebug($"{Tag} level borders: created {created} wall(s) on layer {layer} ('{LayerMask.LayerToName(layer)}'), " +
                                $"scene={owned.name}{sb}");
        }

        /// <summary>The parent among the scene's root objects (one pass over a handful of roots, no scene-wide search).</summary>
        static GameObject FindParent(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root != null && root.name == LevelBorderBoxModel.ParentName)
                {
                    return root;
                }
            }
            return null;
        }
    }
}
