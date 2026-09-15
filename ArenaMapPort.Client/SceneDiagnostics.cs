using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// §F.5: "scene load confirmation per scene name". Logged only for our own 6 scenes (§E.1) — zero
    /// cost and silent for every other map's raids.
    /// </summary>
    internal static class SceneDiagnostics
    {
        static bool _installed;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaMapRaid.IsOwnedScene(scene.name))
            {
                return;
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][Scene] loaded '{scene.name}' (mode={mode})");
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (!ArenaMapRaid.IsOwnedScene(scene.name))
            {
                return;
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][Scene] unloaded '{scene.name}'");
        }
    }
}
