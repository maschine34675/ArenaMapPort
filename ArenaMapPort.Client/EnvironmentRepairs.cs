using BepInEx.Configuration;
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
    /// PLAN-TWO-MAPS item 6: Equator/Bay5 packages now DO author a real <c>EnvironmentManager</c>
    /// (RainCoverImporter.cs, from raincover.json — <c>TriggerGroup</c>/<c>IndoorTrigger</c>, and on Bay5
    /// <c>WeatherObstacle</c>/<c>DryPlane</c>). Its own <c>Awake()</c> runs during
    /// <c>TarkovApplication.LoadMapAndData</c>, strictly before <c>Player.Init</c> ever fires (this
    /// class's own gating argument below), and sets <c>EnvironmentManagerBase.Instance</c> itself — so the
    /// <c>Instance == null</c> check just below already skips creating the bare fallback for those two
    /// maps without any further change here (the same holds for any package that carries a
    /// <c>raincover</c> entry, such as the Arena 0.6.0 AirPit package). The fallback stays load-bearing
    /// for every map whose package still authors no <c>EnvironmentManager</c> at all: the parked maps.
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
    /// Gated on <see cref="ArenaMapRaid.IsLoaded()"/> — valid here: <c>Player.Init</c> only ever
    /// runs after <c>TarkovApplication.LoadMapAndData</c> has finished loading our scenes (raid-start
    /// order), so the Scripts scene is already present.
    ///
    /// PLAN-TWO-MAPS light-culling item — a third scene singleton in the same family:
    /// <c>CullingManager</c> (per-light visibility/intensity culling; decompile
    /// D:/SPT41/Development/_decompiled/CullingManager.cs). Equator/Bay5 each author many
    /// <c>CullingLightObject</c> components directly on their Light GameObjects (Equator 472, Bay5 186
    /// per the extractor's manifest counts) but — like <c>CullingManager</c> itself — never a
    /// <c>CullingManager</c> scene instance: that lives only in Arena's own non-imported "Scripts" scene,
    /// which our import never carries over (its one field, <c>_debugCamera</c>, is a serialized
    /// <c>Camera</c> PPtr that is null on both maps' own donor instance, so a bare fallback loses nothing
    /// map-specific). Without it, <c>CullingLightObject.Awake</c> (CullingLightObject.cs:112 —
    /// <c>if (!CullingManager.Instance) { CullingManager.OnInstanceCreated += method_1; }</c>) still runs
    /// fine, but the actual registration path is <c>CullingObject.Start()</c>
    /// (CullingObject.cs: <c>if (CullingManager.Instance == null) { CullingManager.AddEarlyObject(this); }
    /// else { Register(); }</c>) — with no instance ever created, every light queues into the static
    /// <c>CullingManager._earlyObjects</c> list and is never registered, so none of them ever get their
    /// per-camera-distance fade (<c>CullingLightObject.CustomUpdate/UpdateIntensity</c>) applied; the
    /// lights just sit at whatever intensity Awake last computed and are never distance-culled or
    /// visibility-culled at all (harmless — just an inert feature — but this is also the same field
    /// (<c>float_1</c>) <see cref="ArenaLightingRepairs"/>'s <c>SyncCullingLightIntensity</c> now needs
    /// live, so getting the manager running is the prerequisite for that repair actually mattering under
    /// camera-distance fade, not just at Awake time).
    ///
    /// Creation order does not matter: <c>CullingManager.Awake()</c> sets <c>Instance = this</c> (which
    /// fires <c>OnInstanceCreated</c> — for <c>CullingLightObject</c> that handler only unsubscribes
    /// itself, it does no registration) and THEN immediately calls
    /// <c>AddEarlyObjectsToCullingObjects()</c>, which walks the static early-objects list built by every
    /// light's own <c>Start()</c> and registers each one — so creating the fallback here, well after the
    /// Scripts scene (and therefore every light's own <c>Start()</c>) has already run, still picks up
    /// every queued light. <c>CullingManager.Awake()</c>/<c>Init()</c> touch no camera or player state at
    /// all (only <c>Camera.onPreCull</c>, its own internal arrays, and the job list), so there is no
    /// install-order hazard the way <c>VolumetricLightRenderer</c> (CameraEffectsRepairs.cs) has with the
    /// live camera. Not yet host-verified (no in-raid pass since this was written — see the client
    /// implementer's own report for what remains to check).
    /// </summary>
    internal static class EnsureEnvironmentPrefix
    {
        static ConfigEntry<bool> _enableCullingManagerFallback;

        internal static void BindConfig()
        {
            _enableCullingManagerFallback = Plugin.Instance.Settings.Bind("Environment", "EnableCullingManagerFallback", true,
                "Creates a bare CullingManager singleton (per-light visibility/intensity culling for " +
                "CullingLightObject-tagged lights) when none exists, the same way this class already " +
                "creates EnvironmentManager/ObservedCullingManager. Without it every CullingLightObject on " +
                "this map queues into CullingManager's static early-objects list and never registers, so " +
                "its distance/frustum fade never runs. Takes effect at the next raid start.");
        }

        internal static void Prefix()
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return; // every other map's raid — untouched
            }

            if (EnvironmentManagerBase.Instance == null)
            {
                new GameObject("EnvironmentManager(ArenaFallback)").AddComponent<EnvironmentManager>();
                Plugin.Log.LogDebug("[RaidFix] created missing EnvironmentManager singleton " +
                                    "(bare — no indoor triggers authored for this map, always-Outdoor; " +
                                    "Equator/Bay5 author their own real one instead, see RainCoverImporter.cs)");
            }

            if (!Singleton<ObservedCullingManager>.Instantiated)
            {
                new GameObject("ObservedCullingManager(ArenaFallback)").AddComponent<ObservedCullingManager>();
                Plugin.Log.LogDebug("[RaidFix] created missing ObservedCullingManager singleton " +
                                    "(observed-body visibility culling)");
            }

            if ((_enableCullingManagerFallback == null || _enableCullingManagerFallback.Value) &&
                CullingManager.Instance == null)
            {
                new GameObject("CullingManager (ArenaMapPort fallback)").AddComponent<CullingManager>();
                Plugin.Log.LogDebug("[Environment] created missing CullingManager singleton " +
                                    "(per-light visibility/intensity culling — every CullingLightObject on this " +
                                    "map queued itself into CullingManager's static early-objects list at its own " +
                                    "Start(); CullingManager.Awake() drains that list right after setting Instance, " +
                                    "so creation order relative to scene load does not matter)");
            }
        }
    }
}
