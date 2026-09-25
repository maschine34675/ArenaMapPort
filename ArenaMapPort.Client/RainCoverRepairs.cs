using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// docs/progress/donor-settings-camera-weather.md §12/§13 — rain cover for ported maps.
    ///
    /// Root cause (2026-09-18 14:52 playtest, spiegelung-innen1/2.png; re-confirmed 2026-09-18 20:20,
    /// außen.png/innen.png): <c>DepthPhotograper.Render()</c> (decompile, called from
    /// <c>RainController.AbstractState.CameraChanged</c> on every camera change — <see cref="WeatherRig"/>'s
    /// own doc comment already establishes the timing) photographs a top-down orthographic depth of ONLY
    /// the "Terrain" layer plus <c>WeatherObstacle.Instance</c>'s combined <c>DryPlane</c> mesh, drawn in
    /// black onto that same layer (<c>DepthPhotograper.RenderObstacles</c>). No ArenaMapPort map authors a
    /// <c>WeatherObstacle</c>/<c>DryPlane</c>/Terrain-layer geometry the way a vanilla donor does — the
    /// depth map comes back empty and every point on the map reads "uncovered", so indoor floors get
    /// treated exactly like open sky (full wetness, bright sky-cubemap reflection) even under a roof.
    ///
    /// Three independent pieces, all driven once per raid from <see cref="WeatherRig.OnSceneLoaded"/>
    /// (<see cref="ApplyForCurrentMap"/> only ARMS them — it fires on the Scripts scene, before any
    /// geometry scene exists; the actual scan runs in <see cref="RenderPrefix"/>, a Harmony prefix on
    /// <c>DepthPhotograper.Render()</c>, on the raid's first camera change, when every scene is loaded):
    ///
    ///  (a) <see cref="RenderPrefix"/> computes <c>LevelSettings.RainBounds</c> from the map's own static
    ///      geometry (every enabled <c>MeshRenderer</c> in an owned scene, excluding Water/Sky/LevelBorder
    ///      layers, the local player's own body, and oversized volumes like the sea plane/sky dome) and
    ///      writes it — safe to overwrite <see cref="WeatherRig"/>'s own default-value write: <c>RainBounds</c>
    ///      is never referenced by <c>LevelSettings.ApplySettings()</c> (confirmed, LevelSettings.cs:194-218),
    ///      it is read fresh, later, directly off the live singleton by <c>DepthPhotograper.Render()</c>
    ///      (DepthPhotograper.cs:59). The existing <c>RainBoundsCenter</c>/<c>RainBoundsSize</c> config
    ///      stays a manual override when set away from the class defaults. Both <c>RainCoverMode.RoofDepth</c>
    ///      and <c>RainCoverMode.RoofDepthBlack</c> run this same scan.
    ///  (b) <see cref="SetupDepthCameraPostfix"/> (Harmony postfix on <c>DepthPhotograper.SetupDepthCamera</c>)
    ///      — §12, mode <c>RoofDepth</c> only — widens the depth camera's <c>cullingMask</c> to the layer
    ///      set the bounds scan actually found. Kept for A/B comparison; the 2026-09-18 20:20 playtest
    ///      found this ineffective in practice (see §13 of the doc): <c>SetupDepthCamera</c> forces
    ///      <c>RenderingPath.Forward</c>, and this map's deferred-only shaders very plausibly draw nothing
    ///      into a forward-path camera's color/depth-writing pass. The SAME postfix also now caches, for
    ///      EVERY mode (goal 1 diagnostics, §13), the transient depth camera's own top-Y/far-clip and this
    ///      instance's <c>_yBias</c>/<c>_depthTextureDimension</c> — <see cref="BuildDepthReadback"/>'s own
    ///      doc comment explains why these need caching rather than a live re-derivation.
    ///  (c) <see cref="RenderObstaclesPrefix"/> (§13, new — Harmony prefix on
    ///      <c>DepthPhotograper.RenderObstacles</c>) — mode <c>RoofDepthBlack</c> only, the new default —
    ///      draws every cached ELEVATED static mesh (found by the very same scan as (a), gated on
    ///      <c>CoverMinHeight</c>) in "Hidden/Black" onto the depth camera via <c>Graphics.DrawMesh</c>,
    ///      the same mechanism a real donor's <c>WeatherObstacle.MeshCollider</c> (a combined mesh of
    ///      hand-placed <c>DryPlane</c> roofs/overhangs) uses. Does NOT widen the <c>cullingMask</c> —
    ///      stays Terrain-only, matching vanilla exactly. See docs/progress/donor-settings-camera-weather.md
    ///      §13 for the full investigation (Harmony target validity, field access, the RFloat readback
    ///      recipe, the <c>Graphics.DrawMesh</c> overload, and why draws submitted in a prefix on
    ///      <c>RenderObstacles</c> are still consumed by <c>_depthCamera.Render()</c> later in the same,
    ///      unmodified original method body).
    ///
    /// Goal 3(c) of §12 (a synthetic <c>WeatherObstacle</c> for <c>DepthPhotograper.CheckIfCameraUnderRain</c>
    /// — the separate "rain drops on screen indoors" mechanism, a physics raycast against
    /// <c>WeatherObstacle.MeshCollider</c>, structurally independent of the depth-map coverage this file
    /// targets) is still explicitly deferred — see docs/progress/donor-settings-camera-weather.md §12 for
    /// the reasoning. <c>WeatherObstacle.Instance</c> stays null on every ported map; the resulting
    /// once-per-camera-change <c>Debug.LogError("WeatherObstacle.Instance is null")</c> is left alone too
    /// (cosmetic log noise only, not a per-frame cost — same doc section).
    ///
    /// Every entry point is gated on <see cref="ArenaMapRaid.IsLoaded"/>/<see cref="WeatherRig.IsActiveForCurrentMap"/>
    /// — each Harmony hook here runs for ANY loaded <c>DepthPhotograper</c>, including a real vanilla raid
    /// played earlier in the same session, same "mandatory first check" convention
    /// <see cref="RainWetnessRepairs"/>/<see cref="WeatherTestOverride"/> already document.
    ///
    /// §14 (docs/progress/donor-settings-camera-weather.md) — the 2026-09-18 21:17 playtest confirmed
    /// <c>RoofDepthBlack</c> DOES photograph something both outdoors (the yard's own ground, a scan bug —
    /// see goal 5 below) and indoors (something ~8.9m above the warehouse floor), yet both spots still show
    /// full puddle wetness. Three live-switchable EXPERIMENTS, new per-map section
    /// <c>RainCoverLab (&lt;key&gt;)</c> (<see cref="InstallLab"/>/<see cref="BindLabConfig"/>), let a
    /// tester isolate which hypothesis (H1 orientation mismatch / H2 the wetting shader ignores the depth
    /// map / H3 a height-band compare) explains it, all re-rendering the depth photo immediately
    /// (<see cref="RerenderNow"/>, also bound to a new hotkey) so no new raid is needed between tries:
    ///  - <see cref="DrawTransformMode"/> (H1) — mirrors/swaps the elevated-mesh draws about the live
    ///    <c>RainBounds</c> center before they hit the depth camera (<see cref="BuildMirrorMatrix"/>).
    ///  - <c>CoverAll</c> (H2) — draws one extra black quad over the WHOLE <c>RainBounds</c> footprint
    ///    (<see cref="GetOrCreateCoverAllQuadMesh"/>), so "is anything drawn at this pixel at all" is
    ///    trivially true everywhere; if the floor is still wet with this on, the ground-wetness shader most
    ///    likely never reads <c>_WeatherDepthMap</c> for puddles in the first place.
    ///  - Goal 5's own elevated-mesh filter fix (in <see cref="ScanStaticGeometry"/>) — the root cause the
    ///    playtest's own outdoor readback exposed: the old downward raycast started BELOW the candidate
    ///    renderer's own bounds, so a FLOOR's own collider (at/above that origin) was never hit, and the
    ///    "no hit → elevated if above the map's own min.y" fallback then wrongly classified the yard's own
    ///    ground as an elevated roof. Fixed by starting the raycast just inside the renderer's own bottom
    ///    and treating "no hit" as "not elevated" — see that method's own comment for the full reasoning.
    /// See docs/progress/donor-settings-camera-weather.md §14 for the full test script and how each switch
    /// maps to an observable outcome.
    /// </summary>
    internal static class RainCoverRepairs
    {
        // The largest single renderer bounds we still trust as "real map geometry" — excludes the sea
        // placeholder plane / sky dome, which otherwise blow the computed RainBounds out to a size that
        // makes DepthPhotograper's ortho footprint (MaxBoundSize = max(size.x, size.z)) uselessly coarse.
        const float MaxRendererExtent = 600f;
        const float HorizontalPadding = 10f;
        const float TopPadding = 5f;

        // §13 goal 2 — a degenerate (near-zero) size.y would make DepthPhotograper's own _MapScale.y
        // (1/RainBounds.size.y, DepthPhotograper.cs:178) blow up; also guards against the ortho camera's
        // farClipPlane (= RainBounds.size.y) ending up too shallow to separate a roof from its own floor.
        const float MinBoundsHeight = 10f;

        // §13 goal 2 — elevated-mesh raycast (renderer-vs-ground) and goal 1's own ground-under-camera
        // diagnostic raycast both use this. Generous for these maps' vertical extents.
        const float ColliderRaycastMaxDistance = 200f;

        // §14 (H1) — RainCoverLab's DrawTransform experiment. Pre-multiplies each elevated mesh's
        // localToWorld by a world-space mirror/swap about the live RainBounds center before it is drawn
        // onto the depth camera (RenderObstaclesPrefix), to test whether the consuming wetting shader
        // samples _WeatherDepthMap with a different orientation than DepthPhotograper.SetShaderValues' own
        // math implies. See BuildMirrorMatrix for the exact per-mode math and BindLabConfig's own doc
        // comment for why mirroring (which reverses mesh winding) needs no material/Cull change here.
        internal enum DrawTransformMode { None, FlipX, FlipZ, FlipXZ, SwapXZ }

        sealed class CoverState
        {
            internal WeatherRig.RainCoverMode Mode;
            internal ConfigEntry<Vector3> RainBoundsCenter;
            internal ConfigEntry<Vector3> RainBoundsSize;
            internal ConfigEntry<float> CoverMinHeight; // §13 goal 2
            internal int LayerMask;
            internal int LastAppliedCullingMask;
            internal bool Computed;
            internal int EmptyScans;

            // §13 goal 2 — RoofDepthBlack's cached elevated-mesh candidates (built once alongside the
            // RainBounds scan, drawn every camera change by RenderObstaclesPrefix).
            internal List<ElevatedMesh> ElevatedMeshes;
            internal bool DrawLogSent;

            // §13 goal 1 diagnostics — cached at SetupDepthCameraPostfix time for EVERY mode (the transient
            // depth camera GameObject is DestroyImmediate'd at the end of every Render() call,
            // DepthPhotograper.cs:79, long before any on-demand F9 readback runs).
            internal float LastCamTopY;
            internal float LastFarClip;
            internal bool LastCameraCaptured;
        }

        // A scan that finds no geometry is retried on the next camera change (spectate/death cam), but
        // not forever — after this many empty scans the raid keeps the class-default bounds.
        const int MaxEmptyScans = 3;

        static readonly Dictionary<string, CoverState> _states = new Dictionary<string, CoverState>();

        // §14 — RainCoverLab (<key>) per-map experiment config. A separate dictionary/Install path from
        // CoverState above (which is armed once per raid from WeatherRig.OnSceneLoaded): these ConfigEntries
        // are bound once at plugin Awake (InstallLab, called from Plugin.cs) and read live, every
        // RenderObstaclesPrefix call, exactly like every other Lab-style knob in this codebase
        // (LightingLab.cs's own convention).
        sealed class LabConfigBundle
        {
            internal ConfigEntry<DrawTransformMode> DrawTransform;
            internal ConfigEntry<bool> CoverAll;
        }

        static readonly Dictionary<string, LabConfigBundle> _labConfigs = new Dictionary<string, LabConfigBundle>();
        static ConfigEntry<KeyboardShortcut> _rerenderHotkey;
        static bool _labInstalled;

        // §14 (H2) — CoverAll's own runtime-built quad mesh, created once and reused (same "persistent,
        // never destroyed" convention as _blackMaterial below).
        static Mesh _coverAllQuadMesh;

        // Project convention: AccessTools.Field over Harmony's ___fieldName injection, matching
        // EffectsControllerRepairs.cs's own stated preference for explicit, verifiable reflection over
        // attribute magic. DepthPhotograper._depthCamera is private (DepthPhotograper.cs:22).
        static readonly FieldInfo DepthCameraField = AccessTools.Field(typeof(DepthPhotograper), "_depthCamera");

        // §13 goal 1 — same string literals DepthPhotograper.cs:31-33 hashes into its own private
        // readonly ints; these globals are process-wide (Shader.SetGlobalVector), so re-hashing the same
        // name here reads back the identical value with no reflection needed.
        static readonly int MapStartId = Shader.PropertyToID("_MapStart");
        static readonly int MapScaleId = Shader.PropertyToID("_MapScale");

        // §13 goal 2 — persistent "Hidden/Black" material for the elevated-mesh draws, created once and
        // kept (NOT DepthPhotograper's own _blackMaterial field, which is created fresh and
        // DestroyImmediate'd every single Render() call, DepthPhotograper.cs:66,78 — reusing that one
        // would be a use-after-destroy hazard the very next raid's first camera change).
        static Material _blackMaterial;
        static bool _blackMaterialFailed;

        // Called once per raid from WeatherRig.OnSceneLoaded (Scripts scene — geometry scenes are not loaded
        // yet, see the class doc comment). Only records what RenderPrefix needs; nothing is scanned here.
        internal static void ApplyForCurrentMap(ArenaMapDescriptor map, WeatherRig.RainCoverMode mode,
            ConfigEntry<Vector3> rainBoundsCenter, ConfigEntry<Vector3> rainBoundsSize, ConfigEntry<float> coverMinHeight,
            bool authoredRainCover = false)
        {
            _states[map.Key] = new CoverState
            {
                Mode = mode,
                RainBoundsCenter = rainBoundsCenter,
                RainBoundsSize = rainBoundsSize,
                CoverMinHeight = coverMinHeight,
            };
            // PLAN-TWO-MAPS item 6 — when this map's scene now carries a REAL, authored
            // EnvironmentManager/WeatherObstacle/DryPlane (RainCoverImporter.cs), this heuristic must not
            // also draw its own elevated-mesh guesses into the same depth photo: vanilla's own
            // DepthPhotograper.RenderObstacles() already draws instance.MeshCollider.sharedMesh once
            // WeatherObstacle.Instance is non-null (decompile), so RenderObstaclesPrefix's EXTRA draws
            // would double up on top of it. Forcing Mode=Off here (rather than skipping this method) keeps
            // this map's CoverState entry in _states — SetupDepthCameraPostfix's §13 goal-1 camera-diagnostics
            // caching (a no-op for every mode, including Off) still runs unchanged.
            if (authoredRainCover)
            {
                _states[map.Key].Mode = WeatherRig.RainCoverMode.Off;
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} AuthoredRainCover=true — skipping the " +
                    "depth-photo heuristic (RainBounds scan, cullingMask widening, elevated-mesh draws); this map's " +
                    "own EnvironmentManager/WeatherObstacle geometry is used instead (RainCoverImporter.cs)");
                return;
            }
            if (mode == WeatherRig.RainCoverMode.Off)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RainCover=Off — no bounds/mask/obstacle draws computed");
                return;
            }
            string what = mode == WeatherRig.RainCoverMode.RoofDepthBlack
                ? "bounds and the elevated-mesh draw cache"
                : "bounds and cover layers";
            Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RainCover={mode} armed — {what} are " +
                "computed on the raid's first DepthPhotograper.Render (first camera change)");
            if (mode == WeatherRig.RainCoverMode.RoofDepth)
            {
                // Review fix-up: the code default moved to RoofDepthBlack this pass (§13), but
                // BepInEx.Configuration.ConfigEntry.Bind() reads an already-saved key from the .cfg file
                // instead of overwriting it with the new class default — a config saved during the §12
                // playtest (RainCover=RoofDepth) keeps running the legacy path forever unless a person
                // edits the .cfg or deletes the section so BepInEx regenerates it. Flagged loudly here
                // because this project's own hard rule forbids this client code from writing that .cfg
                // itself (it lives under D:\SPT41vanilla).
                Plugin.Log.LogWarning($"[ArenaMapPort][RainCover] map={map.Key} RainCover=RoofDepth is the LEGACY " +
                    "cullingMask-widening mode (kept for A/B comparison only) — if this was NOT chosen on purpose, " +
                    "it is most likely a value saved to com.maschine.ArenaMapPort.cfg before this pass, when " +
                    "RoofDepth was still the default. Set RainCover=RoofDepthBlack in that map's [Lighting (...)] " +
                    "section (or delete the section so BepInEx regenerates it) to pick up the new §13 default.");
            }
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
                || state.Mode == WeatherRig.RainCoverMode.Off || state.Computed)
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
            bool computeElevated = state.Mode == WeatherRig.RainCoverMode.RoofDepthBlack;
            var scan = ScanStaticGeometry(map, computeElevated, state.CoverMinHeight != null ? state.CoverMinHeight.Value : 2.2f);
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
            state.ElevatedMeshes = scan.elevatedMeshes ?? new List<ElevatedMesh>();

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

            if (computeElevated)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RoofDepthBlack scan: " +
                    $"keptForBounds={scan.rendererCount} consideredForCover={scan.consideredForCover} " +
                    $"elevated={state.ElevatedMeshes.Count} excluded(oversized)={scan.excludedOversized} " +
                    $"excluded(material)={scan.excludedMaterial} excluded(notElevated)={scan.excludedNotElevated} " +
                    $"excluded(noMesh)={scan.excludedNoMesh} included(glass)={scan.includedGlass} " +
                    $"CoverIncludesGlass={WeatherRig.CoverIncludesGlassForCurrentMap()} " +
                    $"CoverMinHeight={(state.CoverMinHeight != null ? state.CoverMinHeight.Value : 2.2f):0.0}");
            }
        }

        readonly struct ElevatedMesh
        {
            internal readonly Mesh mesh;
            internal readonly Matrix4x4 localToWorld;

            internal ElevatedMesh(Mesh mesh, Matrix4x4 localToWorld)
            {
                this.mesh = mesh;
                this.localToWorld = localToWorld;
            }
        }

        readonly struct ScanResult
        {
            internal readonly Bounds bounds;
            internal readonly int layerMask;
            internal readonly int rendererCount;      // "kept" — used for RainBounds, unchanged meaning from §12
            internal readonly int excludedOversized;
            internal readonly List<ElevatedMesh> elevatedMeshes; // only populated when computeElevated=true
            internal readonly int consideredForCover;  // §13 — candidates that passed the material filter
            internal readonly int excludedMaterial;    // §13 — Transparent/Glass/Fence shader or renderQueue>=2450
            internal readonly int excludedNotElevated; // §13 — too close to the ground/collider below
            internal readonly int excludedNoMesh;      // §13 — elevated but no MeshFilter.sharedMesh to draw
            internal readonly int includedGlass;       // §20.2 — glass-shader renderers kept as cover (CoverIncludesGlass)

            internal ScanResult(Bounds bounds, int layerMask, int rendererCount, int excludedOversized,
                List<ElevatedMesh> elevatedMeshes, int consideredForCover, int excludedMaterial,
                int excludedNotElevated, int excludedNoMesh, int includedGlass)
            {
                this.bounds = bounds;
                this.layerMask = layerMask;
                this.rendererCount = rendererCount;
                this.excludedOversized = excludedOversized;
                this.elevatedMeshes = elevatedMeshes;
                this.consideredForCover = consideredForCover;
                this.excludedMaterial = excludedMaterial;
                this.excludedNotElevated = excludedNotElevated;
                this.excludedNoMesh = excludedNoMesh;
                this.includedGlass = includedGlass;
            }
        }

        // Walks every loaded, ArenaMapRaid.IsOwnedScene-owned scene's enabled MeshRenderers (SceneInspector
        // .cs/LightingLab.cs's own established owned-scene-walk pattern), computing a padded world bounds
        // and the OR'd set of layers that survive the filter — exactly as §12 always has. When
        // computeElevated is true (RainCoverMode.RoofDepthBlack only), a SECOND pass over the same
        // candidates then classifies each one elevated/not (§13 goal 2's own spec: needs the FULL scanned
        // map's min.y, which is only known once the first pass has walked every renderer) and caches the
        // elevated ones' (mesh, localToWorld) for RenderObstaclesPrefix to draw every camera change.
        static ScanResult ScanStaticGeometry(ArenaMapDescriptor map, bool computeElevated, float coverMinHeight)
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

            // §13 goal 2 — every renderer that survived the §12 filter (Water/Sky/LevelBorder/player/
            // oversized), gathered here, elevation-classified in a second pass below once map min.y is known.
            var candidates = computeElevated ? new List<Renderer>() : null;

            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !ArenaMapRaid.IsGameplayScene(scene.name))
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

                        if (computeElevated)
                        {
                            candidates.Add(renderer);
                        }
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
                    "eligible renderer(s) — class-default RainBounds, no cullingMask widening/obstacle draws (retried on the next camera change).");
                return new ScanResult(new Bounds(Vector3.zero, new Vector3(1000f, 220f, 1000f)), 0, 0, excludedOversized,
                    null, 0, 0, 0, 0, 0);
            }

            // Pad 10m each side horizontally; vertical range stays min(bounds) to max(bounds)+5m (task's
            // own spec numbers — a little headroom above the highest surviving renderer, e.g. a roof lip,
            // without ballooning the ortho footprint the way including the oversized sea/sky volumes would).
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            min.x -= HorizontalPadding; max.x += HorizontalPadding;
            min.z -= HorizontalPadding; max.z += HorizontalPadding;
            max.y += TopPadding;
            // §13 goal 2 — keep size.y non-degenerate (DepthPhotograper._MapScale.y = 1/size.y,
            // DepthPhotograper.cs:178) and make sure the ortho camera's farClipPlane (= size.y) has enough
            // headroom above the highest roof to actually separate it from the ground.
            if (max.y - min.y < MinBoundsHeight)
            {
                max.y = min.y + MinBoundsHeight;
            }
            var padded = new Bounds();
            padded.SetMinMax(min, max);

            List<ElevatedMesh> elevatedMeshes = null;
            int consideredForCover = 0, excludedMaterial = 0, excludedNotElevated = 0, excludedNoMesh = 0, includedGlass = 0;

            if (computeElevated)
            {
                elevatedMeshes = new List<ElevatedMesh>();
                int colliderMask = BuildGroundColliderMask();
                bool includeGlass = WeatherRig.CoverIncludesGlassForCurrentMap(); // §20.2

                for (int c = 0; c < candidates.Count; c++)
                {
                    var renderer = candidates[c];
                    if (IsExcludedByMaterial(renderer, includeGlass, out bool glass))
                    {
                        excludedMaterial++;
                        continue;
                    }
                    if (glass)
                    {
                        includedGlass++;
                    }
                    consideredForCover++;

                    var rb = renderer.bounds;
                    // §14 goal 5 fix (2026-09-18 21:17 playtest, außen.png/F9: the yard's own ground
                    // photographed itself as "covered"). The OLD origin (bounds.min.y - 0.05, i.e. BELOW the
                    // renderer's own bottom) pointed a downward ray away from that same renderer's own
                    // collider surface — for a FLOOR renderer (whose collider sits at/near its own
                    // bounds.min.y) this always came back "no hit", and the old fallback below then read
                    // "no hit" as "elevated if above the whole map's own min.y" — true for nearly every
                    // floor on a map whose lowest point is a basement/underside tens of meters down. New
                    // origin sits 2cm INSIDE the renderer's own bottom: a downward ray from there still
                    // misses that SAME renderer's own upward-facing surface (the ray moves further away from
                    // it, exactly as before) but DOES reach a real ground/floor collider sitting further
                    // below an ACTUALLY elevated mesh (a roof/overhang has empty air, not its own floor,
                    // between bounds.min.y and the real ground). "No hit at all" now means "not elevated" —
                    // this IS the ground/floor itself — the mapMin.y fallback is removed entirely.
                    // Host test 2026-09-19: the elevated-vs-ground raycast excluded every building SHELL —
                    // Bay5's halls are one mesh each (walls + roof, e.g. the depot), so their bounds.min sits
                    // at ground level and the "clearance" reads 0 → no hall roof ever reached the photo
                    // (Depot_indoor cover 0/25). The depth camera keeps the HIGHEST surface per pixel, so
                    // drawing everything is safe: a floor photographed at its own height reads "uncovered"
                    // for the indoor classifier (roof >= floor + 2.5 m) and, as observed in the 21:17 test,
                    // for the WetRenderer too. The raycast gate is gone; CoverMinHeight stays an unused key.
                    _ = coverMinHeight;

                    var filter = renderer.GetComponent<MeshFilter>();
                    var mesh = IndoorSurfaceMesh.StableSource(filter != null ? filter.sharedMesh : null);
                    if (mesh == null)
                    {
                        excludedNoMesh++;
                        continue;
                    }

                    elevatedMeshes.Add(new ElevatedMesh(mesh, renderer.transform.localToWorldMatrix));
                }
            }

            return new ScanResult(padded, layerMaskAccum, rendererCount, excludedOversized,
                elevatedMeshes, consideredForCover, excludedMaterial, excludedNotElevated, excludedNoMesh, includedGlass);
        }

        // §13 goal 2 — "exclude renderers with any material whose shader name contains 'Transparent',
        // 'Glass', 'Fence' or whose renderQueue >= 2450 (fences, glass, decals, foliage cards)" (task's own
        // spec wording). sharedMaterials, never .materials, to avoid instancing every material we merely
        // inspect. Any ONE matching material on a multi-material renderer excludes the whole renderer.
        // §20.2 — with includeGlass, a GLASS material (IsGlassShaderName) is cover and neither its
        // transparent render queue nor the 'Transparent'/'Glass' words exclude it; anyGlass reports that
        // at least one such material was seen (log counter). A renderer mixing glass with a fence/foliage
        // material is still excluded by the latter.
        static bool IsExcludedByMaterial(Renderer renderer, bool includeGlass, out bool anyGlass)
        {
            anyGlass = false;
            var mats = renderer.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null)
                {
                    continue;
                }
                var shader = mat.shader;
                string name = shader != null ? shader.name : string.Empty;
                if (includeGlass && IsGlassShaderName(name))
                {
                    anyGlass = true;
                    continue;
                }
                if (mat.renderQueue >= 2450)
                {
                    return true;
                }
                if (shader == null)
                {
                    continue;
                }
                if (name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Fence", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // §20.2 — EFT's glass shaders: anything with 'Glass' in its name, or 'Transparent' together with
        // 'Reflective'/'Specular' ("Global Fog/Transparent Reflective Specular" is Equator's roof-pane shader,
        // 9 materials in analysis/equator_census.json). Cutout/cutoff/fence/billboard/sheet/decal/water/
        // particle words veto: foliage cards, fences, fog sheets and water decals are not cover.
        static bool IsGlassShaderName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string[] veto = { "Cutout", "Cutoff", "Fence", "Billboard", "Sheet", "Decal", "Water", "Particle" };
            for (int i = 0; i < veto.Length; i++)
            {
                if (name.IndexOf(veto[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            if (name.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0
                && (name.IndexOf("Reflective", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Specular", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // §13 goals 1+2 — "collider layers (HighPolyCollider, LowPolyCollider, Terrain, Default)" (task's
        // own spec wording, used both for the elevated-vs-ground raycast and the goal 1 diagnostic's own
        // ground-under-camera raycast). Resolved by name, tolerant of any one being absent from this
        // project's TagManager (contributes 0 bits rather than throwing/whole-mask-failing).
        static int BuildGroundColliderMask()
        {
            int mask = 0;
            string[] names = { "HighPolyCollider", "LowPolyCollider", "Terrain", "Default" };
            for (int i = 0; i < names.Length; i++)
            {
                int layer = LayerMask.NameToLayer(names[i]);
                if (layer >= 0)
                {
                    mask |= 1 << layer;
                }
            }
            return mask;
        }

        // Harmony postfix target — HarmonyPatches.InstallRainCoverDepthMask. Runs inside
        // DepthPhotograper.Render(), right after SetupDepthCamera() builds the depth camera and before
        // RenderObstacles()/_depthCamera.Render() actually photographs it (DepthPhotograper.cs) — widening
        // the mask here (RoofDepth only) is read by that same Render() call. Fires once per camera-change
        // event (via RainController.AbstractState.CameraChanged), not per frame.
        internal static void SetupDepthCameraPostfix(DepthPhotograper __instance)
        {
            // MANDATORY first check — SetupDepthCamera runs on ANY loaded DepthPhotograper, including a
            // real vanilla raid played earlier in the same session, not just ours.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !_states.TryGetValue(map.Key, out var state))
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

            // §13 goal 1 diagnostics — cache the transient depth camera's own top-Y/farClip for EVERY
            // mode (including Off), read back later by BuildDepthReadback. Cached here rather than
            // re-derived live from LevelSettings.RainBounds at readback time because the GameObject itself
            // is DestroyImmediate'd at the end of this very Render() call (DepthPhotograper.cs:79) — same
            // reasoning LastAppliedCullingMaskForCurrentMap's own doc comment already establishes for §12.
            state.LastCamTopY = cam.transform.position.y;
            state.LastFarClip = cam.farClipPlane;
            state.LastCameraCaptured = true;

            // Legacy §12 mask-widening — RoofDepth only. §13's RoofDepthBlack deliberately stays
            // Terrain-only, like vanilla (see RainCoverMode's own doc comment in WeatherRig.cs).
            if (state.Mode != WeatherRig.RainCoverMode.RoofDepth || !state.Computed || state.LayerMask == 0)
            {
                return;
            }

            cam.cullingMask |= state.LayerMask; // OR, not replace — never narrows the original 1<<Terrain bit
            state.LastAppliedCullingMask = cam.cullingMask;
            Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} depth camera cullingMask=0x{cam.cullingMask:X} " +
                $"(added 0x{state.LayerMask:X})");
        }

        // §13 goal 2 — Harmony prefix on DepthPhotograper.RenderObstacles(), installed by
        // HarmonyPatches.InstallRainCoverDepthMask. A VOID prefix (not bool) — the original body must
        // still run (it does the terrain-lower/restore dance and calls _depthCamera.Render() itself); this
        // prefix only QUEUES extra Graphics.DrawMesh(..., depthCamera) calls before that render happens,
        // exactly the way vanilla's own WeatherObstacle draw call in the very same method works
        // (RenderObstacles(): its Graphics.DrawMesh(...) is queued a few lines before _depthCamera.Render()
        // executes later in the same synchronous method body — docs/progress/
        // donor-settings-camera-weather.md §13 investigation §5 cites this directly). Runs once per camera
        // change (RainController.AbstractState.CameraChanged), never per frame.
        internal static void RenderObstaclesPrefix(DepthPhotograper __instance)
        {
            // MANDATORY first check — RenderObstacles runs on ANY loaded DepthPhotograper, vanilla raids included.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !_states.TryGetValue(map.Key, out var state)
                || state.Mode != WeatherRig.RainCoverMode.RoofDepthBlack)
            {
                return;
            }
            if (DepthCameraField == null)
            {
                return;
            }
            var cam = DepthCameraField.GetValue(__instance) as Camera;
            if (cam == null)
            {
                return;
            }

            // §14 — live RainCoverLab overrides for the currently loaded map. None/false = byte-identical
            // §13 behavior (both read fresh every call — cheap ConfigEntry.Value reads, no caching needed;
            // this prefix already only runs once per camera change, never per frame).
            DrawTransformMode drawTransform = DrawTransformMode.None;
            bool coverAll = false;
            if (_labConfigs.TryGetValue(map.Key, out var lab))
            {
                drawTransform = lab.DrawTransform.Value;
                coverAll = lab.CoverAll.Value;
            }

            var meshes = state.ElevatedMeshes;
            bool haveMeshes = meshes != null && meshes.Count > 0;
            if (!haveMeshes && !coverAll)
            {
                if (!state.DrawLogSent)
                {
                    state.DrawLogSent = true;
                    Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RoofDepthBlack: 0 elevated mesh(es) " +
                        "to draw this raid (see the earlier 'RoofDepthBlack scan' line for why) — depth camera stays Terrain-only, same as an empty photo.");
                }
                return;
            }

            var material = GetOrCreateBlackMaterial();
            if (material == null)
            {
                return; // ShadersFinder.Find("Hidden/Black") failed — logged once inside GetOrCreateBlackMaterial
            }

            var ls = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;

            // Task's own §4 investigation note: the LAYER argument to Graphics.DrawMesh is matched against
            // the CAMERA's cullingMask, not the mesh's own GameObject layer — must be Terrain to be seen by
            // this depth camera (SetupDepthCamera sets cullingMask = 1 << Terrain, DepthPhotograper.cs:126),
            // exactly like vanilla's own WeatherObstacle draw call (DepthPhotograper.cs:155).
            int terrainLayer = LayerMask.NameToLayer("Terrain");
            int submeshDraws = 0;
            int meshCount = 0;
            if (haveMeshes)
            {
                meshCount = meshes.Count;
                // §14 (H1) — computed once for the whole draw pass (not per submesh): the live RainBounds
                // center this raid, or Vector3.zero (a harmless no-op fallback — DrawTransform is only
                // ever meaningful with a live LevelSettings anyway) if the singleton isn't around.
                Matrix4x4 mirror = drawTransform == DrawTransformMode.None
                    ? Matrix4x4.identity
                    : BuildMirrorMatrix(drawTransform, ls != null ? ls.RainBounds.center : Vector3.zero);

                for (int i = 0; i < meshes.Count; i++)
                {
                    var em = meshes[i];
                    if (em.mesh == null)
                    {
                        continue;
                    }
                    var localToWorld = drawTransform == DrawTransformMode.None ? em.localToWorld : mirror * em.localToWorld;
                    int subCount = Mathf.Max(1, em.mesh.subMeshCount);
                    for (int s = 0; s < subCount; s++)
                    {
                        Graphics.DrawMesh(em.mesh, localToWorld, material, terrainLayer, cam, s);
                        submeshDraws++;
                    }
                }
            }

            // §14 (H2) — CoverAll: one extra black quad over the whole live RainBounds footprint, drawn
            // regardless of DrawTransform/whether any elevated meshes exist this raid (so CoverAll can be
            // tested in isolation even with 0 elevated meshes found — the earlier haveMeshes||coverAll gate
            // already let an all-CoverAll, no-meshes raid reach this point).
            if (coverAll && ls != null)
            {
                var quad = GetOrCreateCoverAllQuadMesh();
                if (quad != null)
                {
                    var rb = ls.RainBounds;
                    var quadTRS = Matrix4x4.TRS(
                        new Vector3(rb.center.x, rb.max.y - 1f, rb.center.z),
                        Quaternion.identity,
                        new Vector3(Mathf.Max(rb.size.x, 0.01f), 1f, Mathf.Max(rb.size.z, 0.01f)));
                    Graphics.DrawMesh(quad, quadTRS, material, terrainLayer, cam, 0);
                    submeshDraws++;
                }
            }
            else if (coverAll && ls == null)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][RainCoverLab] map={map.Key} CoverAll=true but " +
                    "LevelSettings singleton not found at draw time — CoverAll quad skipped this camera change.");
            }

            if (!state.DrawLogSent)
            {
                state.DrawLogSent = true;
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RoofDepthBlack: drew {meshCount} " +
                    $"elevated mesh(es) ({submeshDraws} submesh draw call(s), CoverAll={coverAll}" +
                    (drawTransform != DrawTransformMode.None ? $", DrawTransform={drawTransform}" : string.Empty) +
                    ") onto the depth camera (Terrain layer, Hidden/Black material).");
            }
        }

        static Material GetOrCreateBlackMaterial()
        {
            if (_blackMaterial != null)
            {
                return _blackMaterial;
            }
            if (_blackMaterialFailed)
            {
                return null;
            }
            var shader = ShadersFinder.Find("Hidden/Black");
            if (shader == null)
            {
                _blackMaterialFailed = true;
                Plugin.Log.LogError("[ArenaMapPort][RainCover] ShadersFinder.Find(\"Hidden/Black\") returned null — " +
                    "RoofDepthBlack obstacle draws disabled for the rest of this session.");
                return null;
            }
            // Persistent for the whole session (never destroyed) — unlike DepthPhotograper's own
            // _blackMaterial, which is deliberately created fresh and DestroyImmediate'd every single
            // Render() call (DepthPhotograper.cs:66,78); this one is ours and outlives any one render.
            _blackMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _blackMaterial;
        }

        // -----------------------------------------------------------------------------------------------
        // §14 — RainCoverLab experiments (docs/progress/donor-settings-camera-weather.md §14). Called from
        // Plugin.cs, AFTER WeatherRig.Install() (reads WeatherRig.IsActiveForCurrentMap(), same ordering
        // reasoning LightingLab.Install()'s own doc comment already establishes) — order relative to
        // LightingLab.Install() itself does not matter, the two files don't read each other's state.
        // -----------------------------------------------------------------------------------------------
        internal static void InstallLab()
        {
            if (_labInstalled)
            {
                return;
            }
            _labInstalled = true;

            foreach (var map in ArenaMapRegistry.All)
            {
                _labConfigs[map.Key] = BindLabConfig(map);
            }

            _rerenderHotkey = Plugin.Instance.Config.Bind("RainCoverLab", "RerenderHotkey",
                new KeyboardShortcut(KeyCode.F8),
                "§14 goal 4 — re-renders DepthPhotograper's depth photo immediately " +
                "(the active donor's DepthPhotograper.Render, the same photo a real camera change uses) and logs a " +
                "fresh depth readback (the same line F9/LightingLab's own snapshot appends) at the live " +
                "camera position, so a DrawTransform/CoverAll change below is visible without a new raid. " +
                "Same 'poll a KeyboardShortcut every Update' pattern as LightingLab.cs's own F9 hotkey. Only " +
                "acts while ArenaMapRaid.IsLoaded() && WeatherRig.IsActiveForCurrentMap().");

            // Plugin.Instance.gameObject is the same always-alive host LightingLab.cs/SceneInspector.cs
            // already add their own hotkey pollers to — no new persistent GameObject needed.
            Plugin.Instance.gameObject.AddComponent<LabHotkeyRunner>();

            // §14 investigation note (task's own "verify the Hidden/Black cull question" requirement) —
            // settled from the game data, not guessed: a UnityPy read of
            // D:\SPT41vanilla\EscapeFromTarkov_Data\globalgamemanagers.assets (pathid=245, m_Name=
            // "Hidden/Black") shows m_ParsedForm.m_PropInfo.m_Props is EMPTY — this shader exposes no
            // material properties at all, so there is no "_Cull" to SetInt() on. But its single compiled
            // SubShader pass's own m_State.culling.val already reads 0 (UnityEngine.Rendering.CullMode.Off)
            // — Cull Off is baked into the shader itself, not something a caller sets — so a mirrored
            // (winding-reversed) DrawTransform draw needs no material swap/double-draw workaround; the
            // existing persistent _blackMaterial (GetOrCreateBlackMaterial) is reused unchanged for every
            // DrawTransform mode and for the CoverAll quad below.
            Plugin.Log.LogInfo("[ArenaMapPort][RainCoverLab] installed — 'Hidden/Black' has no exposed shader " +
                "properties (m_PropInfo.m_Props empty, confirmed via UnityPy against globalgamemanagers.assets " +
                "pathid=245) but its own compiled pass already bakes Cull=Off, so DrawTransform's " +
                "winding-reversing mirrors need no material change. RerenderHotkey=F8.");
        }

        static LabConfigBundle BindLabConfig(ArenaMapDescriptor map)
        {
            // Own section, "RainCoverLab (<key>)" — never "Lighting (<key>)"/"LightingLab (<key>)" (the
            // other two files' own sections), same "structurally impossible key collision, F12-UI
            // discoverability as its own group" reasoning LightingLab.cs's own BindConfig already documents.
            string section = $"RainCoverLab ({map.Key})";
            var cfg = new LabConfigBundle();

            cfg.DrawTransform = Plugin.Instance.Config.Bind(section, "DrawTransform", DrawTransformMode.None,
                "§14 (H1) — pre-multiplies every RoofDepthBlack elevated-mesh draw's localToWorld matrix by " +
                "a world-space mirror/swap about the live LevelSettings.RainBounds center before it hits the " +
                "depth camera. None = today's RoofDepthBlack behavior, unchanged. FlipX: x'=2*cx-x. FlipZ: " +
                "z'=2*cz-z. FlipXZ: both. SwapXZ: (x'-cx)=(z-cz) and (z'-cz)=(x-cx) — a diagonal mirror that " +
                "swaps which world axis maps to which depth-texture axis. Tests H1: whether the ground- " +
                "wetness shader samples _WeatherDepthMap with a different orientation than " +
                "DepthPhotograper.SetShaderValues' own _MapStart/_MapScale math implies, so a roof's shadow " +
                "on the depth map lands in the wrong place. Every mode reverses mesh winding — verified safe " +
                "with no material change (see InstallLab's own log line: 'Hidden/Black' bakes Cull=Off). " +
                "Re-renders the depth photo immediately on change (goal 4) — no new raid needed. Only " +
                "applied while ArenaMapRaid.IsLoaded() && WeatherRig.IsActiveForCurrentMap().");

            cfg.CoverAll = Plugin.Instance.Config.Bind(section, "CoverAll", false,
                "§14 (H2) — additionally draws one black quad (two triangles, built at runtime) over the " +
                "WHOLE live RainBounds footprint at RainBounds.max.y - 1, so the depth map reads 'covered' " +
                "everywhere regardless of DrawTransform/the elevated-mesh scan. If the hall AND the yard " +
                "both dry out with this on, the depth map genuinely drives ground wetness end-to-end and the " +
                "bug is narrowed to geometry/orientation (H1/H3), not the consuming shader. If either stays " +
                "wet with the WHOLE map covered, that consuming shader most likely never reads " +
                "_WeatherDepthMap for ground puddles at all (H2). false = today's RoofDepthBlack behavior. " +
                "Re-renders immediately on change (goal 4). Only applied while ArenaMapRaid.IsLoaded() && " +
                "WeatherRig.IsActiveForCurrentMap().");

            // "Applies live, not next-raid" (goal 4) — same ReapplyIfLoaded/re-render-on-SettingChanged
            // pattern LightingLab.cs's own F12 knobs already use, scoped to THIS map only (a SettingChanged
            // fired for a map that isn't the one currently loaded must not re-render some OTHER map's
            // unrelated depth photo).
            cfg.DrawTransform.SettingChanged += (_, __) => RerenderIfActive(map, "DrawTransform changed");
            cfg.CoverAll.SettingChanged += (_, __) => RerenderIfActive(map, "CoverAll changed");

            return cfg;
        }

        // §14 goal 4 — same "poll a KeyboardShortcut every Update, do real work only inside the keypress
        // branch" pattern LightingLab.cs's own F9 HotkeyRunner already uses.
        sealed class LabHotkeyRunner : MonoBehaviour
        {
            void Update()
            {
                try
                {
                    if (_rerenderHotkey != null && _rerenderHotkey.Value.IsDown() && ArenaMapRaid.IsLoaded())
                    {
                        RerenderNow("F8 hotkey");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[ArenaMapPort][RainCoverLab] hotkey Update threw: {ex}");
                }
            }
        }

        static void RerenderIfActive(ArenaMapDescriptor map, string trigger)
        {
            if (ArenaMapRaid.CurrentMap != map)
            {
                return; // a different map's raid (or none) is loaded — same guard LightingLab.ReapplyIfLoaded
                        // already uses for its own live F12 knobs, so editing map A's Lab section while
                        // playing map B never re-renders B's unrelated depth photo.
            }
            RerenderNow(trigger);
        }

        // §14 goal 4 — the actual re-render. DepthPhotograper.Render() is the exact same call
        // RainController.AbstractState.CameraChanged makes on every real camera change
        // (RainController.cs:100, Controller._depthPhotograper.Render()) — safe to call again here: it
        // synchronously rebuilds the transient depth camera (SetupDepthCameraPostfix fires again, refreshing
        // the goal-1 diagnostic cache), re-runs RenderObstaclesPrefix (this file's own Harmony prefix, which
        // reads the CURRENT DrawTransform/CoverAll values live), then SetShaderValues() — same flow a real
        // camera change uses, just invoked on demand instead of waiting for one.
        // Full redo: forget this raid's geometry scan (bounds + elevated meshes) and photograph again. The
        // first camera change — the normal trigger — comes before the preset's remaining scenes
        // (DEPOT/HANGAR/WAREHOUSE on Bay5) are loaded, so their roofs are missing from the first photo.
        // Returns true when the depth photo was actually re-rendered (review 2026-09-19: a caller that
        // re-classifies against the photo must not do so after a failed/skipped re-render).
        internal static bool RescanAndRerender(string trigger, bool refreshCurrentCamera = false)
        {
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return false;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map != null && _states.TryGetValue(map.Key, out var state))
            {
                state.Computed = false;
                state.EmptyScans = 0;
                state.ElevatedMeshes = null;
            }
            return RerenderNow(trigger, refreshCurrentCamera);
        }

        static bool RerenderNow(string trigger, bool refreshCurrentCamera = false)
        {
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return false; // MANDATORY first check, same convention every entry point in this file already follows
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null)
            {
                return false;
            }

            var photo = WeatherRig.DepthPhotographerForCurrentMap();
            if (photo == null)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCoverLab] map={map.Key} {trigger}: " +
                    "active donor has no DepthPhotograper — nothing to re-render.");
                return false;
            }

            DrawTransformMode drawTransform = DrawTransformMode.None;
            bool coverAll = false;
            if (_labConfigs.TryGetValue(map.Key, out var lab))
            {
                drawTransform = lab.DrawTransform.Value;
                coverAll = lab.CoverAll.Value;
            }

            try
            {
                if (refreshCurrentCamera)
                {
                    if (!WeatherRig.RefreshCurrentCameraForCover()) return false;
                }
                else photo.Render();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][RainCoverLab] map={map.Key} {trigger}: " +
                    $"DepthPhotograper.Render() threw — {e.GetType().Name}: {e.Message}");
                return false;
            }

            Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} re-rendered depth photo " +
                $"(DrawTransform={drawTransform}, CoverAll={coverAll}) [{trigger}]");

            // Goal 4's own "run the F9 depth readback line right after" — the identical readback F9/
            // LightingLab's own snapshot hotkey appends, at the live camera position, so the effect is
            // visible without moving.
            try
            {
                var cam = ResolveActiveCamera();
                Plugin.Log.LogInfo(cam != null
                    ? "[ArenaMapPort][RainCoverLab] " + BuildDepthReadback(cam.transform.position)
                    : "[ArenaMapPort][RainCoverLab] re-render readback: n/a (no active camera)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][RainCoverLab] map={map.Key} post-rerender readback threw: " +
                    $"{e.GetType().Name}: {e.Message}");
            }
            return true;
        }

        // Same CameraManager-first/Camera.main-fallback resolution LightingLab.cs's own ResolveActiveCamera
        // already uses — duplicated here (not shared) to keep this file's own gating/dependencies self
        // contained, matching this file's existing "no cross-file coupling beyond WeatherRig/ArenaMapRaid"
        // convention.
        static Camera ResolveActiveCamera()
        {
            try
            {
                Camera c = CameraManager.Instance?.Camera;
                if (c != null)
                {
                    return c;
                }
            }
            catch
            {
                // CameraManager mid-teardown — fall through
            }
            return Camera.main;
        }

        // §14 (H1) — the exact world-space mirror/swap math BindLabConfig's own DrawTransform description
        // states, built as translate-to-origin / transform / translate-back so it works regardless of
        // where RainBounds' center actually sits (never assumed to be world origin).
        static Matrix4x4 BuildMirrorMatrix(DrawTransformMode mode, Vector3 center)
        {
            if (mode == DrawTransformMode.SwapXZ)
            {
                // Relative to center, (dx,dy,dz) -> (dz,dy,dx): swap the X/Z basis columns, leave Y/W alone.
                // This has determinant -1 (an improper transform, same as the negative-scale modes below) —
                // it also reverses winding, same reasoning applies.
                var swap = Matrix4x4.identity;
                swap.SetColumn(0, new Vector4(0f, 0f, 1f, 0f)); // new X basis = old Z
                swap.SetColumn(2, new Vector4(1f, 0f, 0f, 0f)); // new Z basis = old X
                return Matrix4x4.Translate(center) * swap * Matrix4x4.Translate(-center);
            }

            Vector3 scale;
            switch (mode)
            {
                case DrawTransformMode.FlipX: scale = new Vector3(-1f, 1f, 1f); break;
                case DrawTransformMode.FlipZ: scale = new Vector3(1f, 1f, -1f); break;
                case DrawTransformMode.FlipXZ: scale = new Vector3(-1f, 1f, -1f); break;
                default: return Matrix4x4.identity; // None — callers already skip building this at all
            }
            // Translating by the FULL center (including the untouched axis/axes, whose scale is 1) is
            // harmless — a translate-then-identity-scale-then-translate-back nets to zero on any axis the
            // Scale() call leaves at 1, so one shared expression covers FlipX/FlipZ/FlipXZ without a
            // per-mode axis list.
            return Matrix4x4.Translate(center) * Matrix4x4.Scale(scale) * Matrix4x4.Translate(-center);
        }

        // §14 (H2) — CoverAll's own quad: a unit square (two triangles) in the local XZ plane, built once
        // and reused every raid (never destroyed — same "persistent for the session" convention
        // GetOrCreateBlackMaterial's own _blackMaterial already uses), scaled/positioned per draw to the
        // live RainBounds footprint (RenderObstaclesPrefix). Winding doesn't matter here either — same
        // Cull=Off shader, same InstallLab log line.
        static Mesh GetOrCreateCoverAllQuadMesh()
        {
            if (_coverAllQuadMesh != null)
            {
                return _coverAllQuadMesh;
            }
            var mesh = new Mesh
            {
                name = "ArenaMapPort_RainCoverLab_CoverAllQuad",
                hideFlags = HideFlags.HideAndDontSave,
            };
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 0.5f),
                new Vector3(-0.5f, 0f, 0.5f),
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            _coverAllQuadMesh = mesh;
            return _coverAllQuadMesh;
        }

        // §13 goal 1 — depth-map readback diagnostic. Called from LightingLab's F9 snapshot at the live
        // camera position (docs/progress/donor-settings-camera-weather.md §13 test steps: "F9 at both
        // spots"). The donor photo's _depthRT/_yBias/_depthTextureDimension are all public
        // (DepthPhotograper.cs:10-27,45) and the component itself is never destroyed mid-raid — only its
        // transient _depthCamera GameObject/_cameraRT are, once per camera change (DepthPhotograper.cs:
        // 79-80) — so these four are safe to read live, no caching needed. _MapStart/_MapScale/
        // _WeatherDepthMap are process-wide Shader.SetGlobal* globals, set from SetShaderValues() which
        // runs AFTER RenderObstacles() inside the same Render() call (DepthPhotograper.cs:174-182) — also
        // safe to read live, long after the depth camera itself is destroyed. Only the transient camera's
        // OWN top-Y/farClipPlane need the SetupDepthCameraPostfix cache (see CoverState's own doc comment).
        internal static string BuildDepthReadback(Vector3 worldPos)
        {
            var photo = WeatherRig.DepthPhotographerForCurrentMap();
            if (photo == null)
            {
                return "RainCover depth readback: active donor DepthPhotograper is null (no full rig active for this " +
                    "map, or the first camera change hasn't happened yet this raid).";
            }

            Vector3 mapStart = Shader.GetGlobalVector(MapStartId);
            Vector3 mapScale = Shader.GetGlobalVector(MapScaleId);
            RenderTexture depthTex = photo._depthRT;
            int dim = (int)photo._depthTextureDimension;

            if (depthTex == null || mapScale.x == 0f || mapScale.z == 0f)
            {
                return $"RainCover depth readback: not available yet (depthRT-present={depthTex != null} " +
                    $"mapScale={mapScale}) — waiting for this raid's first camera change (DepthPhotograper.SetShaderValues).";
            }

            bool groundHit = Physics.Raycast(worldPos + Vector3.up * 0.1f, Vector3.down, out var hit,
                ColliderRaycastMaxDistance, BuildGroundColliderMask(), QueryTriggerInteraction.Ignore);
            bool reversedZ = SystemInfo.usesReversedZBuffer;

            var map = ArenaMapRaid.CurrentMap;
            CoverState state = map != null && _states.TryGetValue(map.Key, out var s) ? s : null;
            bool haveCamMeta = state != null && state.LastCameraCaptured;
            float camTopY = haveCamMeta ? state.LastCamTopY : float.NaN;
            float farClip = haveCamMeta ? state.LastFarClip : float.NaN;

            var sb = new StringBuilder();
            sb.Append($"RainCover depth readback @ ({worldPos.x:0.00}, {worldPos.y:0.00}, {worldPos.z:0.00}): " +
                $"cameraY={worldPos.y:0.00} groundY={(groundHit ? hit.point.y.ToString("0.00") : "n/a (no collider hit below)")} " +
                $"yBias={photo._yBias:0.000} depthTextureDimension={dim} mapStart={mapStart} mapScale={mapScale} " +
                $"usesReversedZBuffer={reversedZ} " +
                (haveCamMeta
                    ? $"depthCamTopY(cached)={camTopY:0.00} farClip(cached)={farClip:0.00}"
                    : "depthCamTopY/farClip=n/a (SetupDepthCamera postfix hasn't run yet this raid)"));

            // Review fix-up: was its own inline copy of this formula; now calls the extracted helper below
            // so BuildDepthReadback and TrySampleDepthAtWorldPoint can't silently drift apart.
            WorldXZToTexel(mapStart, mapScale, dim, worldPos.x, worldPos.z, out int px, out int py);

            Texture2D scratch = null;
            try
            {
                // Same "one reusable 1x1 scratch texture across several reads" pattern LightingLab.cs's own
                // AppendReflectionProbeFaces already uses.
                scratch = new Texture2D(1, 1, TextureFormat.RFloat, false, true);
                AppendTexel(sb, depthTex, scratch, px, py, camTopY, farClip, reversedZ, "center");
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }
                        int nx = Mathf.Clamp(px + dx, 0, dim - 1);
                        int ny = Mathf.Clamp(py + dy, 0, dim - 1);
                        AppendTexel(sb, depthTex, scratch, nx, ny, camTopY, farClip, reversedZ, $"({dx},{dy})");
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append($" 3x3 readback failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (scratch != null)
                {
                    UnityEngine.Object.Destroy(scratch);
                }
            }

            return sb.ToString();
        }

        // RFloat RT readback recipe — task's own §3 investigation, confirmed against this build's
        // UnityEngine.CoreModule.dll: _depthRT is a color-format RenderTexture (RFloat), not a native depth
        // buffer, so it reads back like any other RenderTexture (RenderTexture.active + ReadPixels).
        //
        // §19 — extracted out of AppendTexel's own try/finally (the §13.10 review fix-up below) so
        // IndoorFloorSmoothnessRepairs' classifier can reuse the identical dance for its own 3x3-per-
        // renderer sampling pass, instead of duplicating it.
        static float ReadTexelRaw(RenderTexture rt, Texture2D scratch, int px, int py)
        {
            var prevActive = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                scratch.ReadPixels(new Rect(px, py, 1, 1), 0, 0);
                scratch.Apply();
                return scratch.GetPixel(0, 0).r;
            }
            finally
            {
                // Review fix-up: ReadPixels/Apply/GetPixel throwing used to leave RenderTexture.active
                // pointed at DepthPhotograper's own _depthRT (an RFloat RT) — the caller's own try/finally
                // (BuildDepthReadback) only destroys the scratch Texture2D, it never restored this global.
                RenderTexture.active = prevActive;
            }
        }

        // §19 — extracted one-liner (was inline in AppendTexel below). "Nothing photographed" check (task's
        // own §3): CameraClearFlags.Color clears color AND depth every render, and the cleared depth value
        // in shader-space is always the platform's own FAR sentinel — 0 under reversed-Z (this project's
        // D3D11 desktop build), 1 otherwise.
        internal static bool IsNothingPhotographed(float raw, bool reversedZ) =>
            reversedZ ? raw <= 1e-4f : raw >= 1f - 1e-4f;

        // §19 — the ONE formula that's actually real on this machine (AppendTexel below logs BOTH for a
        // human to compare; the classifier only ever wants the real one for usesReversedZBuffer).
        internal static float ReconstructHeightReversedAware(float raw, float camTopY, float farClip, bool reversedZ) =>
            reversedZ ? camTopY - (1f - raw) * farClip : camTopY - raw * farClip;

        // §19 — new accessor, same shape as LastAppliedCullingMaskForCurrentMap() below.
        internal static bool TryGetCachedCameraMeta(ArenaMapDescriptor map, out float camTopY, out float farClip)
        {
            if (map != null && _states.TryGetValue(map.Key, out var s) && s.LastCameraCaptured)
            {
                camTopY = s.LastCamTopY;
                farClip = s.LastFarClip;
                return true;
            }
            camTopY = float.NaN;
            farClip = float.NaN;
            return false;
        }

        // §19 — extracted from BuildDepthReadback's own inline formula below.
        internal static void WorldXZToTexel(Vector3 mapStart, Vector3 mapScale, int dim, float worldX, float worldZ,
            out int px, out int py)
        {
            px = Mathf.Clamp(Mathf.FloorToInt((worldX - mapStart.x) * mapScale.x * dim), 0, dim - 1);
            py = Mathf.Clamp(Mathf.FloorToInt((worldZ - mapStart.z) * mapScale.z * dim), 0, dim - 1);
        }

        // A partition needs many samples along roof edges. One CPU-readable snapshot avoids a GPU
        // readback/upload stall for each sample. Ownership is limited to one classification pass.
        internal sealed class DepthSnapshot : IDisposable
        {
            readonly Texture2D _pixels;
            readonly Vector3 _start, _scale;
            readonly float _top, _far;
            readonly int _dimension;
            readonly bool _reversed;

            internal DepthSnapshot(Texture2D pixels, Vector3 start, Vector3 scale, float top, float far)
            {
                _pixels = pixels; _start = start; _scale = scale; _top = top; _far = far;
                _dimension = pixels.width; _reversed = SystemInfo.usesReversedZBuffer;
            }

            internal bool TrySample(float x, float z, out bool nothing, out float height)
            {
                WorldXZToTexel(_start, _scale, _dimension, x, z, out int px, out int py);
                float raw = _pixels.GetPixel(px, py).r;
                nothing = IsNothingPhotographed(raw, _reversed);
                height = ReconstructHeightReversedAware(raw, _top, _far, _reversed);
                return true;
            }

            public void Dispose() { if (_pixels != null) UnityEngine.Object.Destroy(_pixels); }
        }

        internal static DepthSnapshot CaptureDepthSnapshot(ArenaMapDescriptor map)
        {
            var photo = WeatherRig.DepthPhotographerForCurrentMap();
            if (photo == null || photo._depthRT == null || !TryGetCachedCameraMeta(map, out float top, out float far))
                return null;
            Vector3 start = Shader.GetGlobalVector(MapStartId), scale = Shader.GetGlobalVector(MapScaleId);
            int dim = (int)photo._depthTextureDimension;
            if (scale.x == 0 || scale.z == 0 || dim <= 0 || dim > 8192
                || photo._depthRT.width != dim || photo._depthRT.height != dim) return null;
            var previous = RenderTexture.active;
            Texture2D pixels = null;
            try
            {
                pixels = new Texture2D(dim, dim, TextureFormat.RFloat, false, true);
                RenderTexture.active = photo._depthRT;
                pixels.ReadPixels(new Rect(0, 0, dim, dim), 0, 0, false);
                // ReadPixels fills CPU pixels. This snapshot is never rendered, so no Apply/upload.
                return new DepthSnapshot(pixels, start, scale, top, far);
            }
            catch
            {
                if (pixels != null) UnityEngine.Object.Destroy(pixels);
                throw;
            }
            finally { RenderTexture.active = previous; }
        }

        // §19 — the shared entry point IndoorFloorSmoothnessRepairs' classifier calls. Caller owns
        // `scratch` (reused across a whole 3x3-per-renderer classification pass, unlike BuildDepthReadback's
        // own single-call scratch below) so a floor-like-renderer scan doesn't allocate/destroy a Texture2D
        // per sample.
        internal static bool TrySampleDepthAtWorldPoint(ArenaMapDescriptor map, Texture2D scratch,
            float worldX, float worldZ, out bool nothingPhotographed, out float height)
        {
            nothingPhotographed = true;
            height = float.NaN;
            var photo = WeatherRig.DepthPhotographerForCurrentMap();
            if (photo == null || photo._depthRT == null)
            {
                return false;
            }

            Vector3 mapStart = Shader.GetGlobalVector(MapStartId);
            Vector3 mapScale = Shader.GetGlobalVector(MapScaleId);
            if (mapScale.x == 0f || mapScale.z == 0f)
            {
                return false;
            }
            if (!TryGetCachedCameraMeta(map, out float camTopY, out float farClip))
            {
                return false;
            }

            int dim = (int)photo._depthTextureDimension;
            WorldXZToTexel(mapStart, mapScale, dim, worldX, worldZ, out int px, out int py);
            bool reversedZ = SystemInfo.usesReversedZBuffer;
            float raw = ReadTexelRaw(photo._depthRT, scratch, px, py);
            nothingPhotographed = IsNothingPhotographed(raw, reversedZ);
            height = ReconstructHeightReversedAware(raw, camTopY, farClip, reversedZ);
            return true;
        }

        static void AppendTexel(StringBuilder sb, RenderTexture rt, Texture2D scratch, int px, int py,
            float camTopY, float farClip, bool reversedZ, string label)
        {
            float raw = ReadTexelRaw(rt, scratch, px, py);
            bool nothingPhotographed = IsNothingPhotographed(raw, reversedZ);

            string heightLinear = "n/a";
            string heightReversed = "n/a";
            if (!float.IsNaN(camTopY) && !float.IsNaN(farClip))
            {
                heightLinear = (camTopY - raw * farClip).ToString("0.00");
                heightReversed = (camTopY - (1f - raw) * farClip).ToString("0.00");
            }
            sb.Append($" [{label} px={px} py={py} raw={raw:0.0000} nothingPhotographed={nothingPhotographed} " +
                $"height(linear)={heightLinear} height(reversedZ)={heightReversed}]");
        }

        // Read by DaytimeVisualsProbe's own once-per-raid diagnostic line (§12 goal 4). CACHED at postfix
        // time deliberately — DepthPhotograper._depthCamera's GameObject is DestroyImmediate'd at the end
        // of every Render() call (DepthPhotograper.cs:79), long gone by the time any once-per-raid probe
        // runs seconds later; a live readback there would NRE/return a stale default.
        internal static int LastAppliedCullingMaskForCurrentMap() =>
            ArenaMapRaid.CurrentMap != null && _states.TryGetValue(ArenaMapRaid.CurrentMap.Key, out var s) ? s.LastAppliedCullingMask : 0;
    }
}
