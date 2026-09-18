using System;
using System.Collections.Generic;
using System.Reflection;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// docs/progress/donor-settings-camera-weather.md §12, goal 3 — rain cover for ported maps.
    ///
    /// Root cause (2026-09-18 14:52 playtest, spiegelung-innen1/2.png): <c>DepthPhotograper.Render()</c>
    /// (decompile, called from <c>RainController.AbstractState.CameraChanged</c> on every camera change —
    /// <see cref="WeatherRig"/>'s own doc comment already establishes the timing) photographs a top-down
    /// orthographic depth of ONLY the "Terrain" layer plus <c>WeatherObstacle.Instance</c>'s combined
    /// <c>DryPlane</c> mesh, drawn in black onto that same layer (<c>DepthPhotograper.RenderObstacles</c>).
    /// No ArenaMapPort map authors a <c>WeatherObstacle</c>/<c>DryPlane</c>/Terrain-layer geometry the way
    /// a vanilla donor does — the depth map comes back empty and every point on the map reads
    /// "uncovered", so indoor floors get treated exactly like open sky (full wetness, bright sky-cubemap
    /// reflection) even under a roof.
    ///
    /// Two independent pieces. <see cref="WeatherRig.OnSceneLoaded"/> only ARMS them per raid
    /// (<see cref="ApplyForCurrentMap"/>): it fires on the Scripts scene, which is the first scene of the
    /// raid to load, so none of the map's geometry scenes exist yet (LightingLab's own ground-material
    /// scan hit the same hazard and defers 5 s). The actual work happens in <see cref="RenderPrefix"/>,
    /// a Harmony prefix on <c>DepthPhotograper.Render()</c>: the first camera change of the raid comes
    /// after every scene in the preset has loaded and the player exists, so the scan sees the whole map,
    /// and the bounds are written immediately before <c>Render()</c> reads them (DepthPhotograper.cs:59).
    ///  (a) <see cref="RenderPrefix"/> computes <c>LevelSettings.RainBounds</c> from the map's own
    ///      static geometry (every enabled <c>MeshRenderer</c> in an owned scene, excluding Water/Sky/
    ///      LevelBorder layers, the local player's own body, and oversized volumes like the sea plane/sky
    ///      dome) and writes it — safe to overwrite <see cref="WeatherRig"/>'s own default-value write
    ///      (spec §THE SPEC, 3(a)): <c>RainBounds</c> is never referenced by
    ///      <c>LevelSettings.ApplySettings()</c> (confirmed, LevelSettings.cs:194-218), it is read fresh,
    ///      later, directly off the live singleton by <c>DepthPhotograper.Render()</c>
    ///      (DepthPhotograper.cs:59). The existing <c>RainBoundsCenter</c>/<c>RainBoundsSize</c> config
    ///      stays a manual override when set away from the class defaults.
    ///  (b) <see cref="SetupDepthCameraPostfix"/> (Harmony postfix on
    ///      <c>DepthPhotograper.SetupDepthCamera</c>, installed by
    ///      <see cref="HarmonyPatches.InstallAll"/>) widens the depth camera's <c>cullingMask</c> to the
    ///      layer set the bounds scan actually found, so roofs/floors above ground get photographed as
    ///      cover instead of only the (always-empty, for us) Terrain layer. Timing: <c>SetupDepthCamera()</c>
    ///      runs inside <c>Render()</c> before <c>RenderObstacles()</c>/<c>_depthCamera.Render()</c> and
    ///      before the camera object is destroyed at the end of the same call — widening the mask in this
    ///      postfix is read by that very same render.
    ///
    /// Goal 3(c) (a synthetic <c>WeatherObstacle</c> for <c>DepthPhotograper.CheckIfCameraUnderRain</c> —
    /// the separate "rain drops on screen indoors" mechanism, a physics raycast against
    /// <c>WeatherObstacle.MeshCollider</c>, structurally independent of the depth-map coverage this file
    /// targets) is explicitly deferred — see docs/progress/donor-settings-camera-weather.md §12 for the
    /// reasoning. <c>WeatherObstacle.Instance</c> stays null on every ported map; the resulting
    /// once-per-camera-change <c>Debug.LogError("WeatherObstacle.Instance is null")</c> is left alone too
    /// (cosmetic log noise only, not a per-frame cost — same doc section).
    ///
    /// Both gated on <see cref="ArenaMapRaid.IsLoaded"/>/<see cref="WeatherRig.IsActiveForCurrentMap"/> —
    /// <see cref="SetupDepthCameraPostfix"/> is a Harmony postfix on a method that runs for ANY loaded
    /// <c>DepthPhotograper</c>, including a real vanilla raid played earlier in the same session, same
    /// "mandatory first check" convention <see cref="RainWetnessRepairs"/>/<see cref="WeatherTestOverride"/>
    /// already document.
    /// </summary>
    internal static class RainCoverRepairs
    {
        // The largest single renderer bounds we still trust as "real map geometry" — excludes the sea
        // placeholder plane / sky dome, which otherwise blow the computed RainBounds out to a size that
        // makes DepthPhotograper's ortho footprint (MaxBoundSize = max(size.x, size.z)) uselessly coarse.
        const float MaxRendererExtent = 600f;
        const float HorizontalPadding = 10f;
        const float TopPadding = 5f;

        sealed class CoverState
        {
            internal WeatherRig.RainCoverMode Mode;
            internal ConfigEntry<Vector3> RainBoundsCenter;
            internal ConfigEntry<Vector3> RainBoundsSize;
            internal int LayerMask;
            internal int LastAppliedCullingMask;
            internal bool Computed;
            internal int EmptyScans;
        }

        // A scan that finds no geometry is retried on the next camera change (spectate/death cam), but
        // not forever — after this many empty scans the raid keeps the class-default bounds.
        const int MaxEmptyScans = 3;

        static readonly Dictionary<string, CoverState> _states = new Dictionary<string, CoverState>();

        // Project convention: AccessTools.Field over Harmony's ___fieldName injection, matching
        // EffectsControllerRepairs.cs's own stated preference for explicit, verifiable reflection over
        // attribute magic. DepthPhotograper._depthCamera is private (DepthPhotograper.cs:22).
        static readonly FieldInfo DepthCameraField = AccessTools.Field(typeof(DepthPhotograper), "_depthCamera");

        // Called once per raid from WeatherRig.OnSceneLoaded (Scripts scene — geometry scenes are not loaded
        // yet, see the class doc comment). Only records what RenderPrefix needs; nothing is scanned here.
        internal static void ApplyForCurrentMap(ArenaMapDescriptor map, WeatherRig.RainCoverMode mode,
            ConfigEntry<Vector3> rainBoundsCenter, ConfigEntry<Vector3> rainBoundsSize)
        {
            _states[map.Key] = new CoverState { Mode = mode, RainBoundsCenter = rainBoundsCenter, RainBoundsSize = rainBoundsSize };
            if (mode == WeatherRig.RainCoverMode.Off)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RainCover=Off — no bounds/mask computed");
                return;
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RainCover=RoofDepth armed — bounds and " +
                "cover layers are computed on the raid's first DepthPhotograper.Render (first camera change)");
        }

        // Harmony prefix on DepthPhotograper.Render() — HarmonyPatches.InstallRainCoverDepthMask. Render()
        // reads Singleton<LevelSettings>.Instance.RainBounds as its first statement, so the bounds written
        // here are the ones this very render uses. Runs once per camera change, not per frame.
        internal static void RenderPrefix(DepthPhotograper __instance)
        {
            // MANDATORY first check — Render runs on ANY loaded DepthPhotograper, vanilla raids included.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !_states.TryGetValue(map.Key, out var state)
                || state.Mode != WeatherRig.RainCoverMode.RoofDepth || state.Computed)
            {
                return;
            }
            try
            {
                ComputeAndWrite(map, state);
            }
            catch (Exception e)
            {
                state.Computed = true; // do not retry a throwing scan on every camera change
                Plugin.Log.LogError($"[ArenaMapPort][RainCover] map={map.Key}: scan threw — {e.GetType().Name}: {e.Message}. " +
                    "Class-default RainBounds and the Terrain-only depth mask stay in effect for this raid.");
            }
        }

        static void ComputeAndWrite(ArenaMapDescriptor map, CoverState state)
        {
            var scan = ScanStaticGeometry(map);
            if (scan.rendererCount == 0)
            {
                state.EmptyScans++;
                if (state.EmptyScans >= MaxEmptyScans)
                {
                    state.Computed = true;
                    Plugin.Log.LogWarning($"[ArenaMapPort][RainCover] map={map.Key}: {state.EmptyScans} empty scans — giving up for this raid.");
                }
                return; // retried on the next camera change
            }

            state.LayerMask = scan.layerMask;
            state.Computed = true;

            var ls = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
            if (ls == null)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][RainCover] map={map.Key}: LevelSettings singleton not found at " +
                    "render time — RainBounds not written (DepthPhotograper.Render logs its own error for this case).");
                return;
            }

            bool manualOverride = state.RainBoundsCenter.Value != Vector3.zero
                || state.RainBoundsSize.Value != new Vector3(1000f, 220f, 1000f);
            if (manualOverride)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RainBounds manual override in use " +
                    $"(center={state.RainBoundsCenter.Value} size={state.RainBoundsSize.Value}) — computed would have been " +
                    $"center={scan.bounds.center} size={scan.bounds.size} from {scan.rendererCount} renderer(s)");
            }
            else
            {
                // Overwrites WeatherRig.ApplyDonorLevelSettingsOverrides' own default-value RainBounds
                // write — safe: RainBounds isn't part of LevelSettings.ApplySettings()'s pushes (confirmed,
                // LevelSettings.cs:194-218 never mentions it), it's read fresh, right after this prefix, by
                // DepthPhotograper.Render() (DepthPhotograper.cs:59).
                ls.RainBounds = scan.bounds;
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RainBounds computed from " +
                    $"{scan.rendererCount} renderer(s) (excluded {scan.excludedOversized} oversized): " +
                    $"center={scan.bounds.center} size={scan.bounds.size}");
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} depth-camera cover layers=0x{scan.layerMask:X}");
        }

        readonly struct ScanResult
        {
            internal readonly Bounds bounds;
            internal readonly int layerMask;
            internal readonly int rendererCount;
            internal readonly int excludedOversized;

            internal ScanResult(Bounds bounds, int layerMask, int rendererCount, int excludedOversized)
            {
                this.bounds = bounds;
                this.layerMask = layerMask;
                this.rendererCount = rendererCount;
                this.excludedOversized = excludedOversized;
            }
        }

        // Walks every loaded, ArenaMapRaid.IsOwnedScene-owned scene's enabled MeshRenderers (SceneInspector
        // .cs/LightingLab.cs's own established owned-scene-walk pattern), computing a padded world bounds
        // and the OR'd set of layers that survive the filter.
        static ScanResult ScanStaticGeometry(ArenaMapDescriptor map)
        {
            // Resolved by NAME, never hardcoded indices (SeaPlaceholderRepairs.cs/SceneInspector.cs/
            // SpawnPlacementRepairs.cs's own established convention) — tolerant of -1 (this project's own
            // TagManager.asset leaves "LevelBorder" unnamed/blank at layer 29, so that lookup legitimately
            // returns -1 here; MapBorderRepairs.cs's own LevelBorderLayer field already documents this).
            int waterLayer = LayerMask.NameToLayer("Water");
            int skyLayer = LayerMask.NameToLayer("Sky");
            int levelBorderLayer = LayerMask.NameToLayer("LevelBorder");

            Bounds bounds = default;
            bool hasBounds = false;
            int layerMaskAccum = 0;
            int rendererCount = 0;
            int excludedOversized = 0;

            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    var renderers = root.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        var renderer = renderers[r];
                        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                        {
                            continue;
                        }

                        int layer = renderer.gameObject.layer;
                        if (layer == waterLayer || layer == skyLayer || layer == levelBorderLayer)
                        {
                            continue;
                        }
                        // Our TagManager leaves layer 29 unnamed, so "LevelBorder" resolves to -1 above; fall
                        // back to Arena's own 'LevelBorder (N)' object names, as MapBorderRepairs does.
                        if (levelBorderLayer < 0 && renderer.gameObject.name.StartsWith("LevelBorder", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // Hierarchy check, not a layer check — level-dressing meshes that merely happen to
                        // share a "Player"-named layer are NOT excluded here, only the local player's own
                        // body (SceneInspector.cs:138's own established pattern).
                        if (renderer.GetComponentInParent<EFT.Player>() != null)
                        {
                            continue;
                        }

                        var rendererBounds = renderer.bounds;
                        var extents = rendererBounds.extents;
                        if (extents.x > MaxRendererExtent || extents.y > MaxRendererExtent || extents.z > MaxRendererExtent)
                        {
                            excludedOversized++; // the sea placeholder plane / sky dome and similar enormous volumes
                            continue;
                        }

                        if (!hasBounds)
                        {
                            bounds = rendererBounds;
                            hasBounds = true;
                        }
                        else
                        {
                            bounds.Encapsulate(rendererBounds);
                        }
                        rendererCount++;
                        layerMaskAccum |= 1 << layer;
                    }
                }
            }

            if (!hasBounds)
            {
                // Defensive — should not happen for a real map with any static geometry at all; falling
                // back to the class-default bounds keeps RainBounds non-degenerate (a zero-size Bounds
                // would divide by zero in DepthPhotograper's own ortho-size/_MapScale math) rather than
                // leaving the scan's caller with nothing usable.
                Plugin.Log.LogWarning($"[ArenaMapPort][RainCover] map={map.Key}: static-geometry scan found 0 " +
                    "eligible renderer(s) — class-default RainBounds, no cullingMask widening (retried on the next camera change).");
                return new ScanResult(new Bounds(Vector3.zero, new Vector3(1000f, 220f, 1000f)), 0, 0, excludedOversized);
            }

            // Pad 10m each side horizontally; vertical range stays min(bounds) to max(bounds)+5m (task's
            // own spec numbers — a little headroom above the highest surviving renderer, e.g. a roof lip,
            // without ballooning the ortho footprint the way including the oversized sea/sky volumes would).
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            min.x -= HorizontalPadding; max.x += HorizontalPadding;
            min.z -= HorizontalPadding; max.z += HorizontalPadding;
            max.y += TopPadding;
            var padded = new Bounds();
            padded.SetMinMax(min, max);

            return new ScanResult(padded, layerMaskAccum, rendererCount, excludedOversized);
        }

        // Harmony postfix target — HarmonyPatches.InstallRainCoverDepthMask. Runs inside
        // DepthPhotograper.Render(), right after SetupDepthCamera() builds the depth camera and before
        // RenderObstacles()/_depthCamera.Render() actually photographs it (DepthPhotograper.cs) — widening
        // the mask here is read by that same Render() call. Fires once per camera-change event (via
        // RainController.AbstractState.CameraChanged), not per frame.
        internal static void SetupDepthCameraPostfix(DepthPhotograper __instance)
        {
            // MANDATORY first check — SetupDepthCamera runs on ANY loaded DepthPhotograper, including a
            // real vanilla raid played earlier in the same session, not just ours.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || WeatherRig.RainCoverForCurrentMap() != WeatherRig.RainCoverMode.RoofDepth)
            {
                return;
            }
            if (!_states.TryGetValue(map.Key, out var state) || !state.Computed || state.LayerMask == 0)
            {
                return;
            }
            if (DepthCameraField == null)
            {
                return; // defensive — a future build renamed the field; HarmonyPatches.Verify already logs if the METHOD itself moved
            }

            var cam = DepthCameraField.GetValue(__instance) as Camera;
            if (cam == null)
            {
                return;
            }

            cam.cullingMask |= state.LayerMask; // OR, not replace — never narrows the original 1<<Terrain bit
            state.LastAppliedCullingMask = cam.cullingMask;
            Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} depth camera cullingMask=0x{cam.cullingMask:X} " +
                $"(added 0x{state.LayerMask:X})");
        }

        // Read by DaytimeVisualsProbe's own once-per-raid diagnostic line (§12 goal 4). CACHED at postfix
        // time deliberately — DepthPhotograper._depthCamera's GameObject is DestroyImmediate'd at the end
        // of every Render() call (DepthPhotograper.cs:79), long gone by the time any once-per-raid probe
        // runs seconds later; a live readback there would NRE/return a stale default.
        internal static int LastAppliedCullingMaskForCurrentMap() =>
            ArenaMapRaid.CurrentMap != null && _states.TryGetValue(ArenaMapRaid.CurrentMap.Key, out var s) ? s.LastAppliedCullingMask : 0;
    }
}
