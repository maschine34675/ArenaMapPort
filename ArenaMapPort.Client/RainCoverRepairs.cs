using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
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
            ConfigEntry<Vector3> rainBoundsCenter, ConfigEntry<Vector3> rainBoundsSize, ConfigEntry<float> coverMinHeight)
        {
            _states[map.Key] = new CoverState
            {
                Mode = mode,
                RainBoundsCenter = rainBoundsCenter,
                RainBoundsSize = rainBoundsSize,
                CoverMinHeight = coverMinHeight,
            };
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
                    $"excluded(noMesh)={scan.excludedNoMesh} CoverMinHeight={(state.CoverMinHeight != null ? state.CoverMinHeight.Value : 2.2f):0.0}");
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

            internal ScanResult(Bounds bounds, int layerMask, int rendererCount, int excludedOversized,
                List<ElevatedMesh> elevatedMeshes, int consideredForCover, int excludedMaterial,
                int excludedNotElevated, int excludedNoMesh)
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
                    null, 0, 0, 0, 0);
            }

            // Pad 10m each side horizontally; vertical range stays min(bounds) to max(bounds)+5m (task's
            // own spec numbers — a little headroom above the highest surviving renderer, e.g. a roof lip,
            // without ballooning the ortho footprint the way including the oversized sea/sky volumes would).
            float rawMinY = bounds.min.y; // captured BEFORE padding — Y is never padded downward, so this
                                           // doubles as "the scanned map's own min.y" for §13's elevation fallback below.
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
            int consideredForCover = 0, excludedMaterial = 0, excludedNotElevated = 0, excludedNoMesh = 0;

            if (computeElevated)
            {
                elevatedMeshes = new List<ElevatedMesh>();
                int colliderMask = BuildGroundColliderMask();

                for (int c = 0; c < candidates.Count; c++)
                {
                    var renderer = candidates[c];
                    if (IsExcludedByMaterial(renderer))
                    {
                        excludedMaterial++;
                        continue;
                    }
                    consideredForCover++;

                    var rb = renderer.bounds;
                    Vector3 origin = new Vector3(rb.center.x, rb.min.y - 0.05f, rb.center.z);
                    bool elevated;
                    if (Physics.Raycast(origin, Vector3.down, out var hit, ColliderRaycastMaxDistance, colliderMask, QueryTriggerInteraction.Ignore))
                    {
                        elevated = (rb.min.y - hit.point.y) >= coverMinHeight;
                    }
                    else
                    {
                        // No collider hit below at all — only elevated if it clearly sits above the whole
                        // scanned map's own floor (task's own spec fallback rule).
                        elevated = rb.min.y > (rawMinY + coverMinHeight);
                    }

                    if (!elevated)
                    {
                        excludedNotElevated++;
                        continue;
                    }

                    var filter = renderer.GetComponent<MeshFilter>();
                    var mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null)
                    {
                        excludedNoMesh++;
                        continue;
                    }

                    elevatedMeshes.Add(new ElevatedMesh(mesh, renderer.transform.localToWorldMatrix));
                }
            }

            return new ScanResult(padded, layerMaskAccum, rendererCount, excludedOversized,
                elevatedMeshes, consideredForCover, excludedMaterial, excludedNotElevated, excludedNoMesh);
        }

        // §13 goal 2 — "exclude renderers with any material whose shader name contains 'Transparent',
        // 'Glass', 'Fence' or whose renderQueue >= 2450 (fences, glass, decals, foliage cards)" (task's own
        // spec wording). sharedMaterials, never .materials, to avoid instancing every material we merely
        // inspect. Any ONE matching material on a multi-material renderer excludes the whole renderer.
        static bool IsExcludedByMaterial(Renderer renderer)
        {
            var mats = renderer.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null)
                {
                    continue;
                }
                if (mat.renderQueue >= 2450)
                {
                    return true;
                }
                var shader = mat.shader;
                if (shader == null)
                {
                    continue;
                }
                string name = shader.name;
                if (name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Fence", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
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

            var meshes = state.ElevatedMeshes;
            if (meshes == null || meshes.Count == 0)
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

            // Task's own §4 investigation note: the LAYER argument to Graphics.DrawMesh is matched against
            // the CAMERA's cullingMask, not the mesh's own GameObject layer — must be Terrain to be seen by
            // this depth camera (SetupDepthCamera sets cullingMask = 1 << Terrain, DepthPhotograper.cs:126),
            // exactly like vanilla's own WeatherObstacle draw call (DepthPhotograper.cs:155).
            int terrainLayer = LayerMask.NameToLayer("Terrain");
            int submeshDraws = 0;
            for (int i = 0; i < meshes.Count; i++)
            {
                var em = meshes[i];
                if (em.mesh == null)
                {
                    continue;
                }
                int subCount = Mathf.Max(1, em.mesh.subMeshCount);
                for (int s = 0; s < subCount; s++)
                {
                    Graphics.DrawMesh(em.mesh, em.localToWorld, material, terrainLayer, cam, s);
                    submeshDraws++;
                }
            }

            if (!state.DrawLogSent)
            {
                state.DrawLogSent = true;
                Plugin.Log.LogInfo($"[ArenaMapPort][RainCover] map={map.Key} RoofDepthBlack: drew {meshes.Count} " +
                    $"elevated mesh(es) ({submeshDraws} submesh draw call(s)) onto the depth camera (Terrain layer, Hidden/Black material).");
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

        // §13 goal 1 — depth-map readback diagnostic. Called from LightingLab's F9 snapshot at the live
        // camera position (docs/progress/donor-settings-camera-weather.md §13 test steps: "F9 at both
        // spots"). DepthPhotograper.Instance/_depthRT/_yBias/_depthTextureDimension are all public
        // (DepthPhotograper.cs:10-27,45) and the component itself is never destroyed mid-raid — only its
        // transient _depthCamera GameObject/_cameraRT are, once per camera change (DepthPhotograper.cs:
        // 79-80) — so these four are safe to read live, no caching needed. _MapStart/_MapScale/
        // _WeatherDepthMap are process-wide Shader.SetGlobal* globals, set from SetShaderValues() which
        // runs AFTER RenderObstacles() inside the same Render() call (DepthPhotograper.cs:174-182) — also
        // safe to read live, long after the depth camera itself is destroyed. Only the transient camera's
        // OWN top-Y/farClipPlane need the SetupDepthCameraPostfix cache (see CoverState's own doc comment).
        internal static string BuildDepthReadback(Vector3 worldPos)
        {
            var photo = DepthPhotograper.Instance;
            if (photo == null)
            {
                return "RainCover depth readback: DepthPhotograper.Instance is null (no rig active for this " +
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

            int px = Mathf.Clamp(Mathf.FloorToInt((worldPos.x - mapStart.x) * mapScale.x * dim), 0, dim - 1);
            int py = Mathf.Clamp(Mathf.FloorToInt((worldPos.z - mapStart.z) * mapScale.z * dim), 0, dim - 1);

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
        static void AppendTexel(StringBuilder sb, RenderTexture rt, Texture2D scratch, int px, int py,
            float camTopY, float farClip, bool reversedZ, string label)
        {
            var prevActive = RenderTexture.active;
            float raw;
            try
            {
                RenderTexture.active = rt;
                scratch.ReadPixels(new Rect(px, py, 1, 1), 0, 0);
                scratch.Apply();
                raw = scratch.GetPixel(0, 0).r;
            }
            finally
            {
                // Review fix-up: ReadPixels/Apply/GetPixel throwing used to leave RenderTexture.active
                // pointed at DepthPhotograper's own _depthRT (an RFloat RT) — the caller's own try/finally
                // (BuildDepthReadback) only destroys the scratch Texture2D, it never restored this global.
                RenderTexture.active = prevActive;
            }

            // "Nothing photographed" check (task's own §3): CameraClearFlags.Color clears color AND depth
            // every render, and the cleared depth value in shader-space is always the platform's own FAR
            // sentinel — 0 under reversed-Z (this project's D3D11 desktop build), 1 otherwise.
            bool nothingPhotographed = reversedZ ? raw <= 1e-4f : raw >= 1f - 1e-4f;

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
