using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Harmony prefix target for <see cref="BotsController.Init"/>, resolved and applied defensively by
    /// <see cref="HarmonyPatches"/> (AccessTools + null check — see that file's header comment for why).
    /// Verified signature: D:/SPT41/Development/_decompiled/EFT/BotsController.cs:168 — <c>public void
    /// Init(IBotGame, IBotCreator, BotZone[], ISpawnSystem, BotLocationModifier, bool, bool, bool, bool,
    /// bool, IPlayersCollection, string, LocationSettings.Location.BotLocationEvents)</c>, instance method,
    /// synchronous void — no async state-machine caveat here, unlike §F.6's PerfectCulling target.
    /// </summary>
    internal static class NavMeshInstallerPrefix
    {
        static void Prefix()
        {
            NavMeshInstaller.OnBotsControllerInit();
        }
    }

    /// <summary>
    /// §F.2: direct, near-verbatim port of InterchangeRework.Client/NavMeshExtension.cs (MIT — attribution
    /// in THIRD-PARTY-NOTICES.md). Adds our fresh agent-0 NavMesh bake before BotsController.Init's
    /// original body runs. Fires on every raid regardless of bot count (BotsController.Init runs
    /// unconditionally per §F.2), which is exactly the reliable, raid-start-adjacent hook point the
    /// spike's "player can walk" criterion needs.
    ///
    /// Multimap generalization (docs/progress/multimap-generalize.md): every cache field is now keyed by
    /// <see cref="ArenaMapDescriptor.Key"/> instead of a single unkeyed instance — a session that plays
    /// Equator, returns to the main menu, then plays Bay5 (and possibly Equator again after that) loads
    /// and installs each map's own navmesh bundle independently, without a stale/wrong-map cache entry
    /// from a previous raid.
    /// </summary>
    internal static class NavMeshInstaller
    {
        static readonly Dictionary<string, NavMeshData> _data = new Dictionary<string, NavMeshData>();
        static readonly Dictionary<string, bool> _loadTried = new Dictionary<string, bool>();
        static readonly Dictionary<string, NavMeshDataInstance> _instances = new Dictionary<string, NavMeshDataInstance>();
        static bool _unloadHooked;

        /// <summary>Raised right after this raid's ported NavMeshData is added (and its triangulation logged); runtime
        /// NavMesh users that must not touch the mesh before it exists (SpawnGates' carvers) subscribe here.</summary>
        internal static event Action Installed;

        internal static void OnBotsControllerInit()
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null)
            {
                return; // every other map's raid — untouched
            }

            var data = LoadOnce(map);
            if (data == null)
            {
                return; // already logged inside LoadOnce()
            }

            try
            {
                if (!_unloadHooked)
                {
                    _unloadHooked = true;
                    SceneManager.sceneUnloaded += OnSceneUnloaded;
                }

                Drop(map.Key);
                // Order matters: remove BEFORE add, or ours is dropped along with the map's own
                // (InterchangeRework's own documented ordering note, NavMeshExtension.cs). Only OUR
                // instance for THIS map is tracked/removed here — RemoveAllNavMeshData() clears every
                // NavMeshData in the process (there is only ever one raid's worth loaded at a time, so
                // this is still correct for the multi-map case: whichever map's data was previously
                // added was already dropped by that raid's own OnSceneUnloaded before a new raid's
                // BotsController.Init can run).
                NavMesh.RemoveAllNavMeshData();
                _instances[map.Key] = NavMesh.AddNavMeshData(data);

                var triangulation = NavMesh.CalculateTriangulation();
                int vertices = triangulation.vertices?.Length ?? 0;
                int triangles = (triangulation.indices?.Length ?? 0) / 3;
                Plugin.Log.LogDebug($"[NavMesh] added navmesh for '{map.Key}': " +
                                    $"{vertices} vertices, {triangles} triangles");
                try { Installed?.Invoke(); }
                catch (Exception e) { Plugin.Log.LogError($"[NavMesh] Installed handler failed: {e}"); }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[NavMesh] {e.GetType().Name}: {e.Message}");
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            // Stateless resolution (not ArenaMapRaid.CurrentMap) — see ArenaMapRaid's own doc comment
            // for why an unload handler must never depend on a shared mutable "current map" field.
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map != null)
            {
                Drop(map.Key);
            }
        }

        static void Drop(string mapKey)
        {
            if (!_instances.TryGetValue(mapKey, out var instance) || !instance.valid)
            {
                return;
            }
            instance.Remove();
            _instances[mapKey] = default;
        }

        // Loaded once per process per map, kept alive via HideFlags.DontUnloadUnusedAsset (matches
        // NavMeshExtension.cs's own comment: "Unload(false) keeps the asset; DontUnloadUnusedAsset keeps
        // it through sweeps").
        static NavMeshData LoadOnce(ArenaMapDescriptor map)
        {
            if (_loadTried.TryGetValue(map.Key, out var tried) && tried)
            {
                return _data.TryGetValue(map.Key, out var cached) ? cached : null;
            }
            _loadTried[map.Key] = true;

            var pluginDir = Path.GetDirectoryName(typeof(NavMeshInstaller).Assembly.Location) ?? ".";
            var path = Path.Combine(Path.Combine(pluginDir, "bundles"), map.NavMeshBundleFileName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogError($"[NavMesh] expected bundle at '{path}' but it is missing");
                return null;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    Plugin.Log.LogError($"[NavMesh] AssetBundle.LoadFromFile returned null for '{path}' " +
                                         "(wrong BuildTarget, or a corrupt/incompatible file?)");
                    return null;
                }

                // By type, not name — the asset is called whatever WP2's editor named it at bake time
                // (§B.WP2.7's own note: "the exact asset name here is not load-bearing").
                var assets = bundle.LoadAllAssets(typeof(NavMeshData));
                if (assets == null || assets.Length == 0)
                {
                    Plugin.Log.LogError($"[NavMesh] '{path}' contains no NavMeshData");
                    return null;
                }

                var data = (NavMeshData)assets[0];
                data.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                _data[map.Key] = data;
                return data;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[NavMesh] {e.GetType().Name}: {e.Message}");
                return null;
            }
            finally
            {
                bundle?.Unload(false);
            }
        }
    }
}
