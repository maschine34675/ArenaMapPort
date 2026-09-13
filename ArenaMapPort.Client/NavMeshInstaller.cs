using System;
using System.IO;
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
    /// </summary>
    internal static class NavMeshInstaller
    {
        const string BundleFileName = "arena_equator_navmesh.bundle";

        static NavMeshData _data;
        static bool _loadTried;
        static NavMeshDataInstance _instance;
        static bool _unloadHooked;

        internal static void OnBotsControllerInit()
        {
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return; // every other map's raid — untouched
            }

            var data = LoadOnce();
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

                Drop();
                // Order matters: remove BEFORE add, or ours is dropped along with the map's own
                // (InterchangeRework's own documented ordering note, NavMeshExtension.cs).
                NavMesh.RemoveAllNavMeshData();
                _instance = NavMesh.AddNavMeshData(data);

                var triangulation = NavMesh.CalculateTriangulation();
                int vertices = triangulation.vertices?.Length ?? 0;
                int triangles = (triangulation.indices?.Length ?? 0) / 3;
                Plugin.Log.LogInfo($"[ArenaMapPort][NavMesh] added navmesh: {vertices} vertices, {triangles} triangles");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][NavMesh] {e.GetType().Name}: {e.Message}");
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (scene.name == ArenaMapPort.Shared.ArenaEquatorLocation.ScriptsSceneName)
            {
                Drop();
            }
        }

        static void Drop()
        {
            if (!_instance.valid)
            {
                return;
            }
            _instance.Remove();
            _instance = default;
        }

        // Loaded once per process, kept alive via HideFlags.DontUnloadUnusedAsset (matches
        // NavMeshExtension.cs's own comment: "Unload(false) keeps the asset; DontUnloadUnusedAsset keeps
        // it through sweeps").
        static NavMeshData LoadOnce()
        {
            if (_loadTried)
            {
                return _data;
            }
            _loadTried = true;

            var pluginDir = Path.GetDirectoryName(typeof(NavMeshInstaller).Assembly.Location) ?? ".";
            var path = Path.Combine(Path.Combine(pluginDir, "bundles"), BundleFileName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogError($"[ArenaMapPort][NavMesh] expected bundle at '{path}' but it is missing");
                return null;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    Plugin.Log.LogError($"[ArenaMapPort][NavMesh] AssetBundle.LoadFromFile returned null for '{path}' " +
                                         "(wrong BuildTarget, or a corrupt/incompatible file?)");
                    return null;
                }

                // By type, not name — the asset is called whatever WP2's editor named it at bake time
                // (§B.WP2.7's own note: "the exact asset name here is not load-bearing").
                var assets = bundle.LoadAllAssets(typeof(NavMeshData));
                if (assets == null || assets.Length == 0)
                {
                    Plugin.Log.LogError($"[ArenaMapPort][NavMesh] '{path}' contains no NavMeshData");
                    return null;
                }

                _data = (NavMeshData)assets[0];
                _data.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                return _data;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][NavMesh] {e.GetType().Name}: {e.Message}");
                return null;
            }
            finally
            {
                bundle?.Unload(false);
            }
        }
    }
}
