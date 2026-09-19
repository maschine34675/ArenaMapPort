using System;
using System.Collections;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// docs/progress/donor-settings-camera-weather.md §19 — Bay5's hall floors (e.g.
    /// <c>SBG_Arena_Bay5_DEPOT/SOO/Depot_indoor/Depot_indoor_LOD0</c>) mirror the bright sky as a white
    /// grazing-angle veil now that <see cref="WeatherRig"/>'s <c>ReflectionIntensity</c> is back at 1.0
    /// (§16.2/§17 — 0.5 fixed the hall but also darkened every shadowed OUTDOOR surface, which the user
    /// does not want). Those hall renderers are not matched by <see cref="LightingLab"/> candidate E's own
    /// renderer-NAME filter (<c>floor</c>/<c>ground</c>/<c>asphalt</c> — "Depot_indoor_LOD0" contains
    /// none of those), and one of their materials (<c>p0/Reflective/Bumped Specular SMap</c>) is SHARED
    /// with the yard floor (<c>Bay5_floor_LOD0</c>), which needs its own sky-mirrored puddles left alone —
    /// so a renderer-name fix to candidate E, or any change to the shared material itself, cannot isolate
    /// one from the other.
    ///
    /// This file classifies floor-like renderers (by SHADER family, not name — see
    /// <see cref="HasFloorLikeShader"/>) as INDOOR using the <see cref="RainCoverRepairs"/> RoofDepthBlack
    /// depth photo (does this renderer's own footprint read "something photographed a roof/ceiling height
    /// above it"?), then scales their materials' smoothness/gloss properties on per-renderer CLONES —
    /// never the shared originals, so an indoor renderer sharing a material with an outdoor one never
    /// affects that outdoor renderer.
    ///
    /// Runs once per raid, gated on <see cref="ArenaMapRaid.IsLoaded"/> +
    /// <see cref="WeatherRig.IsActiveForCurrentMap"/> + <c>RainCoverMethod=RoofDepthBlack</c> (same
    /// "mandatory first check" convention every other Harmony entry point in this project documents),
    /// triggered by <see cref="RenderPostfix"/> — a Harmony postfix on <c>DepthPhotograper.Render()</c>
    /// itself, installed independently of <see cref="RainCoverRepairs"/>'s own prefix/postfix on that same
    /// method (see <c>HarmonyPatches.InstallIndoorFloorSmoothness</c>'s own doc comment for why patching
    /// the OUTER method avoids any Harmony patch-ordering question). The actual scan is deferred
    /// <see cref="ClassifyDelaySeconds"/> after this raid's Scripts-scene load — the same margin
    /// <see cref="LightingLab"/>'s own candidate E scan uses (<c>GroundScanDelaySeconds</c>) to let
    /// <see cref="ShaderRebind"/>'s 3-second stand-in-shader sweep finish first: our own shader-name filter
    /// (<see cref="HasFloorLikeShader"/>) checks <c>shader.name.StartsWith("p0/Reflective", ...)</c>, which
    /// FAILS on a not-yet-rebound <c>Hidden/ArenaMapPort/p0/Reflective/...</c> stand-in name.
    /// </summary>
    internal static class IndoorFloorSmoothnessRepairs
    {
        // Same margin LightingLab.cs's own GroundScanDelaySeconds uses, chosen for the identical reason
        // (ShaderRebind's fixed 3s-after-load sweep must have already completed — see class doc comment).
        const float ClassifyDelaySeconds = 5f;

        // Case-insensitive Contains, matching every float/range shader property whose name mentions any of
        // these — task's own spec: "Smoothness or Glossness or Gloss". Deliberately a DIFFERENT hint set
        // from LightingLab.SmoothnessPropertyHints ("smooth"/"gloss"/"shin") — that file's own
        // DiscoverSmoothnessProps takes the hint list as a parameter precisely so this file can pass its
        // own without disturbing candidate E's existing behavior.
        static readonly string[] IndoorSmoothnessHints = { "Smoothness", "Glossness", "Gloss" };

        sealed class IndoorFloorEntry
        {
            internal Renderer Renderer;
            internal Material[] OriginalMaterials; // cached BEFORE cloning — restore target
            internal Material[] ClonedMaterials;   // renderer.materials after cloning
            internal List<(int matIndex, int propId, float original)> Props;
        }

        sealed class IndoorFloorState
        {
            internal float SceneLoadTime;  // Time.time at this raid's Scripts-scene load
            internal bool Scheduled;       // RenderPostfix already scheduled the classify coroutine
            internal bool Done;            // Classify() completed
            internal readonly List<IndoorFloorEntry> Entries = new List<IndoorFloorEntry>();
        }

        static readonly Dictionary<string, IndoorFloorState> _states = new Dictionary<string, IndoorFloorState>();
        static GameObject _scanHost;
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

            // Live re-apply wiring lives HERE (not inside WeatherRig.BindConfig) — same "WeatherRig owns
            // the config, the consuming file owns its own re-apply wiring" convention
            // GroundSmoothnessBaseEntry's own SettingChanged hookup (LightingLab.BindConfig) already
            // establishes; WeatherRig.cs never references this class.
            foreach (var map in ArenaMapRegistry.All)
            {
                var entry = WeatherRig.IndoorFloorSmoothnessEntry(map);
                if (entry != null)
                {
                    entry.SettingChanged += (_, __) => ReapplyIfLoaded(map);
                }
            }
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null)
            {
                return;
            }

            // WeatherRig.Install() runs its own OnSceneLoaded on this SAME dispatch, earlier (must be —
            // Plugin.cs installs WeatherRig.Install() before this file's Install(), see Plugin.cs's own
            // ordering comment), so IsActiveForCurrentMap()/RainCoverForCurrentMap() already reflect THIS
            // raid here — same ordering argument Plugin.cs already documents for LightingLab.Install().
            if (!WeatherRig.IsActiveForCurrentMap() || WeatherRig.RainCoverForCurrentMap() != WeatherRig.RainCoverMode.RoofDepthBlack)
            {
                _states.Remove(map.Key); // no stale state from an earlier raid with different settings
                return;
            }
            _states[map.Key] = new IndoorFloorState { SceneLoadTime = Time.time };
        }

        static void OnSceneUnloaded(Scene scene)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null || !_states.TryGetValue(map.Key, out var state))
            {
                return;
            }

            int restoredRenderers = 0, destroyedClones = 0;
            foreach (var entry in state.Entries)
            {
                if (entry.Renderer != null)
                {
                    entry.Renderer.materials = entry.OriginalMaterials;
                    restoredRenderers++;
                }
                foreach (var clone in entry.ClonedMaterials)
                {
                    if (clone != null)
                    {
                        UnityEngine.Object.Destroy(clone);
                        destroyedClones++;
                    }
                }
            }
            state.Entries.Clear();
            state.Done = false;
            state.Scheduled = false;
            if (_scanHost != null)
            {
                UnityEngine.Object.Destroy(_scanHost);
                _scanHost = null;
            }

            if (restoredRenderers > 0 || destroyedClones > 0)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] {map.Key}: raid ended — restored " +
                    $"{restoredRenderers} renderer(s), destroyed {destroyedClones} clone(s).");
            }
        }

        // Harmony postfix target — HarmonyPatches.InstallIndoorFloorSmoothness. Fires once per camera
        // change (DepthPhotograper.Render(), not per frame), strictly after that call's own Blit into
        // _depthRT and the transient camera/cameraRT DestroyImmediate calls have already run — see that
        // installer's own doc comment for why the OUTER method is patched instead of RenderObstacles().
        internal static void RenderPostfix(DepthPhotograper __instance)
        {
            // MANDATORY first check — Render() runs on ANY loaded DepthPhotograper, same convention every
            // entry point in RainCoverRepairs.cs already documents.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap()
                || WeatherRig.RainCoverForCurrentMap() != WeatherRig.RainCoverMode.RoofDepthBlack)
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !_states.TryGetValue(map.Key, out var state) || state.Scheduled)
            {
                return;
            }
            if (__instance._depthRT == null)
            {
                return; // guard: RT missing — retried on the next camera change
            }

            state.Scheduled = true;
            float delay = Mathf.Max(0f, ClassifyDelaySeconds - (Time.time - state.SceneLoadTime));
            EnsureScanHost();
            _scanHost.GetComponent<ClassifyRunner>().StartCoroutine(ClassifyRunner.RunAfter(delay, map));
        }

        static void EnsureScanHost()
        {
            if (_scanHost != null)
            {
                return;
            }
            _scanHost = new GameObject("ArenaMapPort_IndoorFloorSmoothnessScan") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_scanHost);
            _scanHost.AddComponent<ClassifyRunner>();
        }

        sealed class ClassifyRunner : MonoBehaviour
        {
            internal static IEnumerator RunAfter(float seconds, ArenaMapDescriptor map)
            {
                yield return new WaitForSeconds(seconds);
                try
                {
                    Classify(map);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} classify threw: {e}");
                }
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Classification.
        // ---------------------------------------------------------------------------------------------
        static void Classify(ArenaMapDescriptor map)
        {
            // Re-verified — several seconds passed since scheduling; RainCoverRepairs.RerenderNow/
            // RerenderIfActive do the same re-check for the same reason.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap() || ArenaMapRaid.CurrentMap != map
                || WeatherRig.RainCoverForCurrentMap() != WeatherRig.RainCoverMode.RoofDepthBlack)
            {
                return;
            }
            if (DepthPhotograper.Instance?._depthRT == null)
            {
                return; // guard — not retried, RenderPostfix already fired once
            }
            if (!_states.TryGetValue(map.Key, out var state))
            {
                return;
            }

            int floorLike = 0, indoor = 0, clones = 0;
            var indoorNames = new List<string>();
            Texture2D scratch = null;
            try
            {
                scratch = new Texture2D(1, 1, TextureFormat.RFloat, false, true);
                int waterLayer = LayerMask.NameToLayer("Water");
                int skyLayer = LayerMask.NameToLayer("Sky");
                int threshold = WeatherRig.IndoorCoverSampleThresholdForCurrentMap();
                float multiplier = WeatherRig.IndoorFloorSmoothnessForCurrentMap();

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
                        var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                        for (int r = 0; r < renderers.Length; r++)
                        {
                            var renderer = renderers[r];
                            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                            {
                                continue;
                            }

                            int layer = renderer.gameObject.layer;
                            if (layer == waterLayer || layer == skyLayer)
                            {
                                continue;
                            }
                            // Hierarchy check, not layer — same reasoning RainCoverRepairs.ScanStaticGeometry
                            // already documents for its own identical check.
                            if (renderer.GetComponentInParent<EFT.Player>() != null)
                            {
                                continue;
                            }

                            var bounds = renderer.bounds;
                            if (bounds.size.y > 1.5f)
                            {
                                continue;
                            }
                            if (Mathf.Max(bounds.size.x, bounds.size.z) < 4f)
                            {
                                continue;
                            }

                            // sharedMaterials, never .materials, for inspection — same convention
                            // RainCoverRepairs.IsExcludedByMaterial/LightingLab.ScanGroundMaterials already
                            // document (avoids needlessly instancing a material we merely inspect).
                            var shared = renderer.sharedMaterials;
                            if (!HasFloorLikeShader(shared) || HasDecalShader(shared))
                            {
                                continue;
                            }

                            floorLike++;
                            if (CountCoveredSamples(map, scratch, bounds) < threshold)
                            {
                                continue;
                            }

                            indoor++;
                            if (indoorNames.Count < 20)
                            {
                                indoorNames.Add(renderer.gameObject.name);
                            }
                            var entry = CloneAndScale(renderer, shared, multiplier);
                            state.Entries.Add(entry);
                            clones += entry.ClonedMaterials.Length;
                        }
                    }
                }
            }
            finally
            {
                if (scratch != null)
                {
                    UnityEngine.Object.Destroy(scratch);
                }
            }

            state.Done = true;
            Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} classified: floorLike={floorLike} " +
                $"indoor={indoor} materialClones={clones} multiplier={WeatherRig.IndoorFloorSmoothnessForCurrentMap():0.00} " +
                $"threshold={WeatherRig.IndoorCoverSampleThresholdForCurrentMap()}/9");
            if (indoor > 0)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} indoor renderer(s) " +
                    $"(showing {indoorNames.Count} of {indoor}): {string.Join(", ", indoorNames)}");
            }
        }

        // Any ONE matching material on a multi-material renderer qualifies the whole renderer — mirrors
        // RainCoverRepairs.IsExcludedByMaterial's own shape. Shader FAMILY, not renderer name (unlike
        // LightingLab candidate E) — this is precisely what lets this file find the hall floor.
        static bool HasFloorLikeShader(Material[] mats)
        {
            for (int i = 0; i < mats.Length; i++)
            {
                var shader = mats[i]?.shader;
                if (shader == null)
                {
                    continue;
                }
                if (shader.name.IndexOf("Vert Paint", StringComparison.OrdinalIgnoreCase) >= 0
                    || shader.name.StartsWith("p0/Reflective", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // NOT redundant with HasFloorLikeShader's own p0/Reflective check — DecalRepairs.cs documents a
        // real shader variant "p0/Reflective/Bumped Specular SMap_Decal" (a mesh decal, not a projector
        // decal, so DecalRepairs' own narrower exact-name check does not touch it) that STARTS WITH
        // "p0/Reflective" too; without this separate exclusion a mesh decal would misclassify as a floor
        // candidate.
        static bool HasDecalShader(Material[] mats)
        {
            for (int i = 0; i < mats.Length; i++)
            {
                var shader = mats[i]?.shader;
                if (shader != null && shader.name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // 3x3 grid over the renderer's own bounds footprint, inset 10% each side so the sample points stay
        // well inside the renderer instead of grazing its edge (a neighboring outdoor renderer's own
        // footprint, or plain empty air past the renderer's own silhouette). A sample counts as "covered"
        // when the depth photo shows something 1.5-60m above this renderer's own top — 1.5m so the
        // renderer's own thickness/nearby low clutter never self-counts as its own roof, 60m so a
        // realistic ceiling/roof height still counts (this project's own maps run tens of meters tall).
        static int CountCoveredSamples(ArenaMapDescriptor map, Texture2D scratch, Bounds bounds)
        {
            float insetX = (bounds.max.x - bounds.min.x) * 0.1f;
            float insetZ = (bounds.max.z - bounds.min.z) * 0.1f;
            float loX = bounds.min.x + insetX, hiX = bounds.max.x - insetX;
            float loZ = bounds.min.z + insetZ, hiZ = bounds.max.z - insetZ;
            float coverLoY = bounds.max.y + 1.5f, coverHiY = bounds.max.y + 60f;

            int covered = 0;
            for (int gx = 0; gx < 3; gx++)
            {
                float wx = Mathf.Lerp(loX, hiX, gx / 2f);
                for (int gz = 0; gz < 3; gz++)
                {
                    float wz = Mathf.Lerp(loZ, hiZ, gz / 2f);
                    if (RainCoverRepairs.TrySampleDepthAtWorldPoint(map, scratch, wx, wz, out bool nothing, out float height)
                        && !nothing && height >= coverLoY && height <= coverHiY)
                    {
                        covered++;
                    }
                }
            }
            return covered;
        }

        // ---------------------------------------------------------------------------------------------
        // Material cloning and restore.
        // ---------------------------------------------------------------------------------------------
        static IndoorFloorEntry CloneAndScale(Renderer renderer, Material[] original, float multiplier)
        {
            var clones = new Material[original.Length];
            var props = new List<(int matIndex, int propId, float original)>();
            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] == null)
                {
                    clones[i] = null;
                    continue;
                }
                var clone = new Material(original[i]) { name = original[i].name + " (ArenaMapPort IndoorFloorSmoothness clone)" };
                clones[i] = clone;
                // Reuses LightingLab's own discovery technique (promoted internal, hint list parameterized)
                // with this file's own hint set — task's own "same discovery as LightingLab E".
                var discovered = LightingLab.DiscoverSmoothnessProps(clone, IndoorSmoothnessHints);
                for (int p = 0; p < discovered.Count; p++)
                {
                    props.Add((i, discovered[p].propId, discovered[p].original));
                    clone.SetFloat(discovered[p].propId, discovered[p].original * multiplier);
                }
            }
            // Per-renderer instances from here on; renderer.sharedMaterials for THIS renderer also returns
            // `clones` from now on (same underlying slot Unity's material/sharedMaterial accessors both
            // read/write) — safe here because LightingLab candidate E discovers ground materials by
            // RENDERER NAME ("floor"/"ground"/"asphalt"), never by walking this renderer, and the yard
            // floor's own shared original (found via the YARD renderer) is untouched by this clone — see
            // this file's own class doc comment.
            renderer.materials = clones;
            return new IndoorFloorEntry { Renderer = renderer, OriginalMaterials = original, ClonedMaterials = clones, Props = props };
        }

        // Live re-apply (SettingChanged, wired from Install() above).
        internal static void ReapplyIfLoaded(ArenaMapDescriptor map)
        {
            if (ArenaMapRaid.CurrentMap != map || !_states.TryGetValue(map.Key, out var state) || state.Entries.Count == 0)
            {
                return;
            }
            float multiplier = WeatherRig.IndoorFloorSmoothnessForCurrentMap();
            int scaled = 0;
            foreach (var entry in state.Entries)
            {
                foreach (var (matIndex, propId, original) in entry.Props)
                {
                    var mat = entry.ClonedMaterials[matIndex];
                    if (mat == null)
                    {
                        continue;
                    }
                    mat.SetFloat(propId, original * multiplier);
                    scaled++;
                }
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} multiplier={multiplier:0.00} " +
                $"reapplied live to {scaled} cloned property value(s) across {state.Entries.Count} indoor renderer(s).");
        }

        // F9 snapshot line (LightingLab.RunSnapshot).
        internal static string SnapshotLine()
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !_states.TryGetValue(map.Key, out var state))
            {
                return "§19 IndoorFloorSmoothness: n/a (no state for this map — no rig, or RainCoverMethod != RoofDepthBlack)";
            }
            int clones = 0;
            foreach (var e in state.Entries)
            {
                clones += e.ClonedMaterials.Length;
            }
            return $"§19 IndoorFloorSmoothness: multiplier={WeatherRig.IndoorFloorSmoothnessForCurrentMap():0.00} " +
                $"threshold={WeatherRig.IndoorCoverSampleThresholdForCurrentMap()}/9 done={state.Done} " +
                $"indoor={state.Entries.Count} materialClones={clones}";
        }
    }
}
