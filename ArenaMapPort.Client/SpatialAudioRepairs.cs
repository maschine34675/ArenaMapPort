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
    /// Supply native defaults for EFT's fallback SpatialAudioSystem factory. The owned Equator/Bay5/AirPit/Bowl
    /// graphs now permit real initialization through NativeSpatialAudio; other ported maps, disabled
    /// acoustics, and rejected packages retain the original uninitialized fallback. Keep source and
    /// interactive entry points guarded because vanilla calls them even while initialization is off.
    /// See docs/progress/spatial-audio.md for source compatibility and GPU acceptance.
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
                Plugin.Log.LogDebug("[ArenaMapPort][RaidFix] SpatialAudioSystem.OcclusionSettings was null " +
                                    "(scene carries no SpatialAudioSystem) — assigned a blank AudioOcclusionSettings instance");
            }

            if (__result.poolsConfig == null)
            {
                __result.poolsConfig = new SpatialAudioPoolsConfig();
                Plugin.Log.LogDebug("[ArenaMapPort][RaidFix] SpatialAudioSystem.poolsConfig was null — assigned class defaults");
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
        internal static bool Prefix(SpatialAudioSystem __instance, ref CancellationToken token,
            ref Task __result, out NativeSpatialAudio.State __state)
        {
            __state = null;
            if (!ArenaMapRaid.IsLoaded())
            {
                return true; // vanilla behaviour for every other map's raid
            }

            if (NativeSpatialAudio.Prepare(__instance, ref token, out __state)) return true;

            __result = Task.CompletedTask;
            SpatialAudioSystem.Initialized = false; // explicit: this is the invariant every other guard relies on
            Plugin.Log.LogDebug("[ArenaMapPort][RaidFix] skipped SpatialAudioSystem.Initialize " +
                                "(native acoustics disabled or no accepted source package for this map)");
            return false; // skip original body entirely
        }

        internal static void Postfix(ref Task __result, NativeSpatialAudio.State __state)
        {
            if (__state != null && __state.Ready)
                __result = NativeSpatialAudio.ObserveInitialization(__result, __state);
        }
    }

    /// <summary>
    /// Guard the Initialize fallback above: while <c>Initialized</c> is false on an owned map,
    /// every <c>ProcessSourceOcclusion</c> overload would otherwise NRE on its first use of
    /// <c>_containerData</c>/<c>ListenerTransform</c> (only ever assigned inside the now-skipped
    /// <c>Initialize</c> body) — and this method runs on every gunshot/impact sound via
    /// <c>BetterAudio.PlayAtPoint</c>, so that would spam an NRE per shot. Degrade to <c>-1</c>, the
    /// same "no occlusion" result BSG's own <c>EOcclusionTest.None</c> early-out already returns
    /// (<c>SpatialAudioSystem.cs:348-363</c>) — the sound itself still plays, it is just never occluded.
    ///
    /// GATING (changed 2026-10-14, a side finding of the hideout-regression review, not its cause): the
    /// <see cref="Prefix"/> is map-gated like every other hook — outside a ported raid
    /// (<see cref="ArenaMapRaid.IsLoaded()"/> false: vanilla maps, menu, hideout) the native body always
    /// runs, whatever <c>Initialized</c> says, so those stay behaviourally vanilla (the earlier version
    /// skipped the native body wherever the flag was false, ManimalIcebreaker's upstream choice, which is a
    /// behaviour change outside our maps even if a benign one). Inside a ported raid the <c>Initialized</c>
    /// branch is unchanged. The static flag is tested first: this is the hot sound path and
    /// <c>IsLoaded()</c> walks the registry and SceneManager. The <see cref="Finalizer"/> was map-gated from
    /// the start, since swallowing an exception is the riskier half — it must never mask a genuinely new
    /// bug on a normal map's fully-initialized system.
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
            // cheap static flag first (hot path), the registry/scene walk only on an uninitialized system
            if (SpatialAudioSystem.Initialized || !ArenaMapRaid.IsLoaded())
            {
                return true; // already-initialized system / every other map, menu, hideout: untouched
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
    /// GATING: matches <see cref="SpatialAudioOcclusionGuard"/> — since 2026-10-14 the <see cref="Prefix"/>
    /// is map-gated too (outside a ported raid the native body always runs); the <see cref="Finalizer"/>
    /// was map-gated from the start, since swallowing an exception is the riskier half.
    /// </summary>
    internal static class SpatialAudioInteractiveOcclusionGuard
    {
        static int _throwLogged;

        internal static bool Prefix()
        {
            // Initialized (cheap flag first) or outside a ported raid: vanilla. Otherwise skip the original
            // body (no occluder to call).
            return SpatialAudioSystem.Initialized || !ArenaMapRaid.IsLoaded();
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
