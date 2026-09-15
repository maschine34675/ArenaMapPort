using System;
using System.Threading;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using Audio.SpatialSystem.Utils;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// impl-raidstart-repairs item (1). Fixes the observed, confirmed crash: <c>SpatialAudioSystem
    /// .Initialize</c> aborts <c>TarkovApplication.LoadMapAndData</c> with an NRE because our scenes
    /// carry no <c>SpatialAudioSystem</c> component, forcing BSG's own fallback path
    /// (<c>CreateSpatialAudioSystem</c>) whose <c>OcclusionSettings</c> field is never assigned.
    ///
    /// DECISION (task brief asked for one, with decompiled evidence): SKIP <c>Initialize</c>'s body
    /// entirely on our map, rather than assigning <c>OcclusionSettings</c> and letting the "real" init
    /// run against BSG's own <c>SpatialAudioLocationInfo/empty_info</c> fallback asset. Evidence for
    /// skip over real-init, all from <c>D:/SPT41/Development/_decompiled</c>:
    ///   - Fixing only <c>OcclusionSettings</c> (the observed crash, <c>SpatialAudioSystem.cs:184-195</c>
    ///     <c>method_0</c>) does not make <c>Initialize</c> safe to run to completion: the very next
    ///     statement after the fixed call constructs a <c>SpatialAudioDataLoader</c>
    ///     (<c>SpatialAudioSystem.cs:152-153</c>) whose <c>LoadDataAsync</c>
    ///     (<c>SpatialAudioDataLoader.cs:83-107</c>) reads a bake file from
    ///     <c>Path.Combine(Application.streamingAssetsPath, _locationInfo.relativeBakeDataPath)</c> via a
    ///     coroutine-driven <c>AudioBakeDataSerializer.ReadAndWriteToDataContainerAsync</c> call whose
    ///     behaviour for the "empty_info" bake path we do not ship and have never inspected is untested.
    ///     That whole sequence sits on the exact same unguarded, no-try/catch await chain
    ///     (<c>EFT/TarkovApplication.cs:2779-2790</c>) that produced the crash we are fixing — any
    ///     surprise there (a missing file, a disposed container, a path-resolution quirk under our
    ///     plugin's own bundle-serving setup) reproduces the identical "raid aborts" symptom, just moved
    ///     one step later and harder to diagnose.
    ///   - The skip path is exactly what ManimalIcebreaker (MIT, `danauraborealis`, same 0.16.9.5 build)
    ///     ships and has tested in production for a from-scratch scene set with no acoustics sidecar
    ///     data (`RaidFixPatches.cs`'s `Patch_SpatialAudioInit`, "staging unavailable" branch) — which is
    ///     our exact situation (audio explicitly out of scope, no bake data authored).
    ///   - Every consumer of <c>SpatialAudioSystem</c> that matters for "raid runs cleanly" already
    ///     gates on the static <c>Initialized</c> flag BSG's own code maintains
    ///     (<c>CanUpdate</c> at <c>SpatialAudioSystem.cs:107-117</c>, used by both <c>Update()</c> and
    ///     <c>LateUpdate()</c>) — so leaving <c>Initialized=false</c> for the whole raid makes those two
    ///     MonoBehaviour callbacks safe no-ops automatically, with no patch needed. TWO public entry
    ///     points do NOT check <c>Initialized</c> themselves (corrected — a fix-raidstart review found
    ///     the second one; this comment previously claimed there was only one):
    ///     <c>ProcessSourceOcclusion</c> (all 3 overloads, routed through on every gunshot/impact via
    ///     <c>BetterAudio.PlayAtPoint</c>; see <see cref="SpatialAudioOcclusionGuard"/>) and
    ///     <c>ProcessInteractiveObjectOcclusion</c> (<c>SpatialAudioSystem.cs:554-557</c>, called from
    ///     <c>WorldInteractiveObject.ProcessOcclusion</c> — doors/switches/levers — and
    ///     <c>WindowBreaker</c> — breakable glass; dereferences <c>_interactiveObjectOccluder</c>, which
    ///     is assigned only inside <c>Initialize</c>'s real body and so stays null forever on our map;
    ///     see <see cref="SpatialAudioInteractiveOcclusionGuard"/>). Dormant on this milestone — the
    ///     extraction pipeline authors no <c>WorldInteractiveObject</c>/<c>WindowBreaker</c> instances
    ///     yet — but guarded now so the next milestone that adds doors/interactives doesn't NRE.
    ///
    /// We still perform the defensive null-safety this task's item (1) literally asks for
    /// (<see cref="SpatialAudioCreateDefaultsPostfix"/>) as cheap insurance — it is a no-op in practice
    /// once the skip prefix below is in place (Initialize's body, where those fields would be read,
    /// never runs on our map), but keeps the object internally consistent if anything ever calls
    /// Initialize a second time or reads the fields directly.
    ///
    /// All gated on <see cref="ArenaMapRaid.IsLoaded()"/> — valid at both hook points: scenes are
    /// loaded (per <c>TarkovApplication.LoadMapAndData</c>'s own sequence, confirmed by the audit —
    /// <c>LoadScenesFromPresetOperation</c> completes before <c>SpatialAudioSystem.Initialize</c> is
    /// even reached) whenever either patch below can fire.
    /// </summary>
    internal static class SpatialAudioCreateDefaultsPostfix
    {
        // Target: SpatialAudioSystem.CreateSpatialAudioSystem(Transform), static factory —
        // Audio.SpatialSystem/SpatialAudioSystem.cs:286-296.
        internal static void Postfix(SpatialAudioSystem __result)
        {
            if (__result == null || !ArenaMapRaid.IsLoaded())
            {
                return;
            }

            if (__result.OcclusionSettings == null)
            {
                __result.OcclusionSettings = ScriptableObject.CreateInstance<AudioOcclusionSettings>();
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] SpatialAudioSystem.OcclusionSettings was null " +
                                    "(scene carries no SpatialAudioSystem) — assigned a blank AudioOcclusionSettings instance");
            }

            if (__result.poolsConfig == null)
            {
                __result.poolsConfig = new SpatialAudioPoolsConfig();
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] SpatialAudioSystem.poolsConfig was null — assigned class defaults");
            }
        }
    }

    internal static class SpatialAudioInitSkipPrefix
    {
        // Target: SpatialAudioSystem.Initialize(CancellationToken, IProgress<float>), instance, async
        // Task — Audio.SpatialSystem/SpatialAudioSystem.cs:139-182. ASYNC STATE-MACHINE CAVEAT (same as
        // PerfectCullingSkipPrefix): a bare `return false` would hand the caller (TarkovApplication
        // .LoadMapAndData:2789, `await ....Initialize(cancellationToken)`) a null Task -> NRE at the
        // await, turning this fix into the very crash it prevents. `ref Task __result` +
        // `Task.CompletedTask` supplies an already-completed Task instead.
        internal static bool Prefix(ref Task __result)
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return true; // vanilla behaviour for every other map's raid
            }

            __result = Task.CompletedTask;
            SpatialAudioSystem.Initialized = false; // explicit: this is the invariant every other guard relies on
            Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] skipped SpatialAudioSystem.Initialize " +
                                "(no acoustics bake/sidecar data for this spike — SPEC: audio out of scope)");
            return false; // skip original body entirely
        }
    }

    /// <summary>
    /// Follow-up to the Initialize skip above: with <c>Initialized</c> permanently false on our map,
    /// every <c>ProcessSourceOcclusion</c> overload would otherwise NRE on its first use of
    /// <c>_containerData</c>/<c>ListenerTransform</c> (only ever assigned inside the now-skipped
    /// <c>Initialize</c> body) — and this method runs on every gunshot/impact sound via
    /// <c>BetterAudio.PlayAtPoint</c>, so that would spam an NRE per shot. Degrade to <c>-1</c>, the
    /// same "no occlusion" result BSG's own <c>EOcclusionTest.None</c> early-out already returns
    /// (<c>SpatialAudioSystem.cs:348-363</c>) — the sound itself still plays, it is just never occluded.
    ///
    /// GATING NOTE (task brief asks every hook's gate to be documented): the <see cref="Prefix"/> below
    /// is intentionally NOT map-gated with <see cref="ArenaMapRaid.IsLoaded()"/> — it only branches
    /// on the static <c>Initialized</c> flag, matching ManimalIcebreaker's own upstream choice. This is
    /// safe and correct on every map, not just ours: <c>Initialized</c> is false on a normal map only in
    /// the narrow window before ITS OWN <c>Initialize</c> completes, and degrading an early call to -1
    /// (unoccluded) instead of letting it NRE against not-yet-assigned fields is a strict improvement
    /// there too, never a behaviour change once that map's own init finishes (<c>Initialized</c> then
    /// stays true and the prefix's `return true` passes every later call through untouched). The
    /// <see cref="Finalizer"/> IS explicitly map-gated, since swallowing an exception is the riskier
    /// half — it must never mask a genuinely new bug on a normal map's fully-initialized system.
    ///
    /// Ported from ManimalIcebreaker's <c>Patch_OcclusionWhenUninitialized</c> (prefix, gate on
    /// <c>!Initialized</c>) + <c>Patch_OcclusionNeverThrows</c> (finalizer belt-and-braces, in case some
    /// other precondition we have not enumerated also fails) — <c>RaidFixPatches.cs:60-76,133-153</c>
    /// (MIT, `danauraborealis`; attribution in THIRD-PARTY-NOTICES.md). All 3 overloads share the same
    /// <c>int</c> return type, so one prefix/finalizer pair covers every overload
    /// <see cref="HarmonyPatches"/> resolves by name via reflection (no single fixed signature to hard-code).
    /// </summary>
    internal static class SpatialAudioOcclusionGuard
    {
        static int _throwLogged;

        internal static bool Prefix(ref int __result)
        {
            if (SpatialAudioSystem.Initialized)
            {
                return true; // real maps / already-initialized system: untouched
            }
            __result = -1;
            return false;
        }

        internal static Exception Finalizer(Exception __exception, ref int __result)
        {
            if (__exception == null)
            {
                return null;
            }
            if (!ArenaMapRaid.IsLoaded())
            {
                return __exception; // vanilla behaviour for every other map's raid
            }

            __result = -1;
            if (_throwLogged++ < 5) // per-shot spam guard, matches ShaderRebind's dedup philosophy
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][RaidFix] SpatialAudioSystem.ProcessSourceOcclusion threw " +
                                       $"(sound degraded to unoccluded): {__exception.GetType().Name}: {__exception.Message}");
            }
            return null; // swallow — never let this escape into the caller's sound-playback flow
        }
    }

    /// <summary>
    /// fix-raidstart — second, previously-unguarded <c>Initialized</c>-independent NRE entry point
    /// found by re-review: <c>ProcessInteractiveObjectOcclusion</c>
    /// (<c>SpatialAudioSystem.cs:554-557</c>) unconditionally dereferences
    /// <c>_interactiveObjectOccluder</c>, which is only ever assigned inside <c>Initialize</c>'s real
    /// body (skipped permanently on our map by <see cref="SpatialAudioInitSkipPrefix"/>) — so it stays
    /// null for the whole raid. Callers: <c>EFT.Interactive.WorldInteractiveObject.ProcessOcclusion</c>
    /// (doors/switches/levers) and <c>EFT.Interactive.WindowBreaker</c> (breakable glass).
    /// <see cref="SpatialAudioOcclusionGuard"/> does NOT cover this — it is found by reflecting on the
    /// literal name <c>ProcessSourceOcclusion</c>, a different method — hence this separate guard.
    ///
    /// Currently dormant: the extraction pipeline (<c>tools/extract/extract_equator.py</c>) authors
    /// only <c>LevelSettings</c>/<c>LocationScene</c>/<c>BotZone</c>/<c>LocationExportInfo</c>/
    /// <c>SpawnPointMarker</c>, never a <c>WorldInteractiveObject</c> or <c>WindowBreaker</c>, so no
    /// instance of either type exists in the shipped scenes yet — but left unguarded this would NRE the
    /// instant a later milestone adds a door or breakable window.
    ///
    /// Unlike <see cref="SpatialAudioOcclusionGuard"/>, the method is <c>void</c> — no <c>__result</c>
    /// plumbing needed for the prefix (a bare <c>bool</c> return already controls whether the original
    /// body runs) or the finalizer (nothing to force back to a sentinel value on the way out).
    ///
    /// GATING: matches <see cref="SpatialAudioOcclusionGuard"/>'s own reasoning — the <see cref="Prefix"/>
    /// is intentionally NOT map-gated (branches only on the static <c>Initialized</c> flag, safe on every
    /// map); the <see cref="Finalizer"/> IS map-gated, since swallowing an exception is the riskier half.
    /// </summary>
    internal static class SpatialAudioInteractiveOcclusionGuard
    {
        static int _throwLogged;

        internal static bool Prefix()
        {
            return SpatialAudioSystem.Initialized; // false => skip original body (no occluder to call)
        }

        internal static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }
            if (!ArenaMapRaid.IsLoaded())
            {
                return __exception; // vanilla behaviour for every other map's raid
            }

            if (_throwLogged++ < 5) // per-call spam guard, matches SpatialAudioOcclusionGuard
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][RaidFix] SpatialAudioSystem.ProcessInteractiveObjectOcclusion " +
                                       $"threw (interactive-object occlusion sound skipped): " +
                                       $"{__exception.GetType().Name}: {__exception.Message}");
            }
            return null; // swallow — never let this escape into the caller's interactive-object flow
        }
    }
}
