using Comfort.Common;
using EFT;
using EFT.EnvironmentEffect;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// impl-raidstart-repairs item (2). Our scenes carry no <c>EnvironmentManager</c> (indoor/outdoor
    /// switcher) or <c>ObservedCullingManager</c> (observed-body visibility culling) — both are scene
    /// singletons that only self-register via their own <c>Awake()</c> when a scene GameObject carries
    /// them, and we author neither for the spike.
    ///
    /// <c>EnvironmentManager.Instance</c> is dereferenced UNGUARDED at <c>EFT/Player.cs:28251-28252</c>
    /// (inside <c>Player.Init</c>, for every player — local and AI): once the SpatialAudioSystem fix
    /// lands, this is the very next abort (confirmed via decompile, docs/progress/audit-load-chain.md
    /// item 2). <c>ObservedCullingManager</c> by contrast is safely guarded everywhere it is used
    /// (<c>ObservedCullingObject.cs:118,126,166</c>, all behind <c>Singleton&lt;ObservedCullingManager&gt;
    /// .Instantiated</c>) — creating it is not required to prevent a crash, but the task brief asks for
    /// it explicitly and it is a small, self-contained, independently-verified-safe MonoBehaviour
    /// (<c>ObservedCullingManager.cs</c>: <c>Awake()</c> only allocates its own internal list and hooks
    /// <c>Camera.onPreCull</c>; <c>Update()</c> loops 0 registered objects when nothing has registered)
    /// — creating a bare instance restores normal observed-body culling behaviour instead of silently
    /// losing it.
    ///
    /// Anchor: a single prefix on <see cref="Player.Init"/> (the unguarded call site itself), not
    /// <c>LocalPlayer.Create</c> — matches the audit's recommendation (this is a non-Fika spike, so
    /// ManimalIcebreaker's own reason for a second anchor point, "FikaPlayer.Create bypasses
    /// LocalPlayer.Create", does not apply; one prefix on the call site every player Init funnels
    /// through is the simplest correct fix per docs/progress/audit-reference-repairs.md).
    ///
    /// Adapted from ManimalIcebreaker's <c>Patch_EnsureEnvironmentManager</c>/
    /// <c>Patch_EnsureEnvBeforeAnyPlayerInit</c> (<c>RaidFixPatches.cs:227-280</c>, MIT —
    /// `danauraborealis`; attribution in THIRD-PARTY-NOTICES.md), reduced to the bare-manager fallback
    /// only — their weather rebuild (<c>IcebreakerWeather.TryBuild</c>) and full indoor-trigger rehydration
    /// (<c>IcebreakerAcoustics.TryBuildEnvironmentTriggers</c>) both need sidecar data we do not have and
    /// are out of scope (SPEC: weather/TOD defaults are acceptable for the spike).
    ///
    /// Gated on <see cref="ArenaEquatorRaid.IsLoaded()"/> — valid here: <c>Player.Init</c> only ever
    /// runs after <c>TarkovApplication.LoadMapAndData</c> has finished loading our scenes (raid-start
    /// order), so the Scripts scene is already present.
    /// </summary>
    internal static class EnsureEnvironmentPrefix
    {
        internal static void Prefix()
        {
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return; // every other map's raid — untouched
            }

            if (EnvironmentManagerBase.Instance == null)
            {
                new GameObject("EnvironmentManager(ArenaFallback)").AddComponent<EnvironmentManager>();
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] created missing EnvironmentManager singleton " +
                                    "(bare — no indoor triggers authored for this spike, always-Outdoor)");
            }

            if (!Singleton<ObservedCullingManager>.Instantiated)
            {
                new GameObject("ObservedCullingManager(ArenaFallback)").AddComponent<ObservedCullingManager>();
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] created missing ObservedCullingManager singleton " +
                                    "(observed-body visibility culling)");
            }
        }
    }
}
