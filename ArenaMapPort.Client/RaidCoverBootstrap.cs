#nullable disable
using System;
using System.Collections;
using ArenaMapPort.Shared;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The harvested donor can outlive several CameraManager instances, including hideout cameras.
    /// Start cover/classification from this raid's scene lifecycle after its real camera is ready;
    /// never require a camera event received while the Scripts scene was loading.
    /// </summary>
    internal static class RaidCoverBootstrap
    {
        sealed class Raid
        {
            internal ArenaMapDescriptor Map;
        }
        sealed class Runner : MonoBehaviour { }
        static Raid _raid;
        static GameObject _host;
        static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null) return;
            Clear();
            if (!WeatherRig.IsActiveForCurrentMap()
                || WeatherRig.RainCoverForCurrentMap() != WeatherRig.RainCoverMode.RoofDepthBlack) return;
            var raid = _raid = new Raid { Map = map };
            _host = new GameObject("ArenaMapPort_RaidCoverBootstrap") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<Runner>().StartCoroutine(Run(raid));
            Plugin.Log.LogInfo($"[ArenaMapPort][RaidCover] map={map.Key} scheduled from Scripts scene; waiting for raid and camera readiness.");
        }

        static bool IsCurrent(Raid raid) => _raid == raid && ArenaMapRaid.CurrentMap == raid.Map
            && WeatherRig.IsActiveForCurrentMap()
            && WeatherRig.RainCoverForCurrentMap() == WeatherRig.RainCoverMode.RoofDepthBlack;

        static bool CameraReady() => Singleton<AbstractGame>.Instantiated
            && Singleton<AbstractGame>.Instance.Status == GameStatus.Started
            && CameraManager.Exist && CameraManager.Instance.Camera != null;

        static IEnumerator Run(Raid raid)
        {
            // ShaderRebind's delayed sweep is 3 seconds after scene load. Full raid readiness also
            // ensures all content scenes are present; camera existence during Scripts load is irrelevant.
            yield return new WaitForSeconds(5f);
            bool ready = false;
            for (int second = 0; second < 240 && IsCurrent(raid); second++)
            {
                if (CameraReady()) { ready = true; break; }
                yield return new WaitForSeconds(1f);
            }
            if (!IsCurrent(raid)) yield break;
            if (!ready)
            {
                Plugin.Log.LogError($"[ArenaMapPort][RaidCover] map={raid.Map.Key} timed out waiting for raid/camera readiness; no classification performed.");
                yield break;
            }
            yield return new WaitForSeconds(3f);
            // A replacement camera or an unavailable donor photo is retried, never accepted as success.
            for (int attempt = 0; attempt < 10 && IsCurrent(raid); attempt++)
            {
                if (CameraReady())
                {
                    bool prepared = false;
                    try { prepared = IndoorFloorSmoothnessRepairs.InitializeForCurrentRaid(raid.Map); }
                    catch (Exception error)
                    {
                        Plugin.Log.LogError($"[ArenaMapPort][RaidCover] map={raid.Map.Key} initialization failed: {error}");
                    }
                    if (prepared)
                    {
                        Plugin.Log.LogInfo($"[ArenaMapPort][RaidCover] map={raid.Map.Key} automatic post-start cover/classification complete.");
                        yield break;
                    }
                }
                yield return new WaitForSeconds(1f);
            }
            if (IsCurrent(raid)) Plugin.Log.LogError($"[ArenaMapPort][RaidCover] map={raid.Map.Key} could not initialize cover after camera readiness; retries exhausted.");
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (_raid != null && ArenaMapRaid.MapForScriptsScene(scene.name) == _raid.Map) Clear();
        }

        internal static void Clear()
        {
            _raid = null; // invalidate pending coroutine before destroying its host (Destroy is deferred)
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = null;
        }
    }
}
