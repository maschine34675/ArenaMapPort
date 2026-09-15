using ArenaMapPort.Shared;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The gate every hook in this plugin uses: "is one of OUR maps currently loaded, and which
    /// one?" — the same cheap, per-frame-safe pattern InterchangeRework's own IrRaid.IsLoaded()
    /// uses (ref-interchange.md §5), generalized over <see cref="ArenaMapRegistry.All"/> instead
    /// of one hardcoded map. Every Harmony patch in this plugin checks
    /// <see cref="IsLoaded"/>/<see cref="IsOwnedScene"/> (or reads <see cref="CurrentMap"/>) first
    /// and is a complete no-op for every other map's raids.
    ///
    /// Formerly ArenaEquatorRaid (multimap generalization, docs/progress/multimap-generalize.md).
    /// <see cref="CurrentMap"/> is deliberately a COMPUTED property, not a field cached by
    /// SceneManager.sceneLoaded/sceneUnloaded subscriptions: several other repair files
    /// (NavMeshInstaller, ShaderRebind, MapBorderRepairs, ArenaLightingRepairs,
    /// LightingDiagnosticProbe) also subscribe to sceneUnloaded to reset their own per-raid
    /// state, and C# multicast delegates invoke subscribers in subscription order — if this
    /// class instead cached CurrentMap and cleared it in its own sceneUnloaded handler, every
    /// other handler's own reset logic would silently depend on install order (whichever handler
    /// happened to subscribe after this one would see CurrentMap already cleared). Computing it
    /// fresh from live SceneManager state on every access removes that ordering hazard entirely:
    /// there is no shared mutable state to race. Where a reset handler specifically needs "which
    /// map's Scripts scene just unloaded" it uses the stateless <see cref="MapForScriptsScene"/>
    /// instead, which has the same property for the same reason.
    /// </summary>
    internal static class ArenaMapRaid
    {
        /// <summary>The map whose Scripts scene (loaded first, LoadSceneMode.Single) is
        /// currently present, or null if none of ours is loaded. Safe to call from a scene's own
        /// sceneLoaded callback — Unity registers a scene with SceneManager before firing
        /// sceneLoaded for it, so the just-loaded Scripts scene is already queryable here.</summary>
        internal static ArenaMapDescriptor CurrentMap
        {
            get
            {
                foreach (var map in ArenaMapRegistry.All)
                {
                    var scene = SceneManager.GetSceneByName(map.ScriptsSceneName);
                    if (scene.IsValid() && scene.isLoaded)
                    {
                        return map;
                    }
                }
                return null;
            }
        }

        internal static bool IsLoaded() => CurrentMap != null;

        /// <summary>True if <paramref name="sceneName"/> is one of ANY registered map's scenes.</summary>
        internal static bool IsOwnedScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return false;
            }

            foreach (var map in ArenaMapRegistry.All)
            {
                var names = map.AllSceneNames;
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i] == sceneName)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Resolves the registered map (if any) whose ScriptsSceneName equals
        /// <paramref name="sceneName"/> — stateless, derived purely from the scene name and the
        /// static registry, never from mutable "current map" bookkeeping. Every OnSceneUnloaded
        /// reset handler in this plugin uses this (not <see cref="CurrentMap"/>) to decide
        /// whether "the raid's own Scripts scene just unloaded, reset my per-raid state" — see
        /// this class's own doc comment for why.
        /// </summary>
        internal static ArenaMapDescriptor MapForScriptsScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return null;
            }
            foreach (var map in ArenaMapRegistry.All)
            {
                if (map.ScriptsSceneName == sceneName)
                {
                    return map;
                }
            }
            return null;
        }
    }
}
