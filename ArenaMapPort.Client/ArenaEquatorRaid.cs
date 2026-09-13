using ArenaMapPort.Shared;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The gate every hook in this plugin uses: "is Arena_Equator content currently loaded?" — the same
    /// cheap, per-frame-safe pattern InterchangeRework's own IrRaid.IsLoaded() uses (ref-interchange.md
    /// §5). Every Harmony patch in this plugin checks this (or <see cref="IsOwnedScene"/>) first and is a
    /// complete no-op for every other map's raids.
    /// </summary>
    internal static class ArenaEquatorRaid
    {
        /// <summary>True once the Scripts scene (loaded first, LoadSceneMode.Single, §D) is present.</summary>
        internal static bool IsLoaded()
        {
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name == ArenaEquatorLocation.ScriptsSceneName)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True if <paramref name="sceneName"/> is one of our 6 scenes (§E.1).</summary>
        internal static bool IsOwnedScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return false;
            }

            var names = ArenaEquatorLocation.AllSceneNames;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == sceneName)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
