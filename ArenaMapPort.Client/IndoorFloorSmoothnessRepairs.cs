using System;
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
    /// Runs once per raid after RaidCoverBootstrap observes a Started raid and its live camera.
    /// The bootstrap rebinds the reused donor to that camera, renders fresh cover and invokes the
    /// classifier. It does not depend on receiving a camera event during Scripts-scene loading.
    /// </summary>
    internal static class IndoorFloorSmoothnessRepairs
    {
        // Case-insensitive Contains, matching every float/range shader property whose name mentions any of
        // these — task's own spec: "Smoothness or Glossness or Gloss". Deliberately a DIFFERENT hint set
        // from LightingLab.SmoothnessPropertyHints ("smooth"/"gloss"/"shin") — that file's own
        // DiscoverSmoothnessProps takes the hint list as a parameter precisely so this file can pass its
        // own without disturbing candidate E's existing behavior.
        static readonly string[] IndoorSmoothnessHints = { "Smoothness", "Glossness", "Gloss" };
        // §20.2 — the p0/Reflective/Bumped Specular family's cubemap reflection tint (Color); scaled by
        // IndoorReflectionStrength on the same clones. Discovered by exact name + property type Color.
        const string ReflectColorName = "_ReflectColor";
        static readonly int ReflectColorId = Shader.PropertyToID(ReflectColorName);
        // §20.3 — host A/B 2026-09-19 17:3x on Equator's lounge parquet: _ReflectColor 0 and _Glossness 0
        // both left the sky sheen untouched, LightingLab GlobalReflection=Black removed it. EFT renders
        // deferred: the environment reflection comes from the deferred reflection pass reading the
        // G-buffer's specular color, i.e. this family's _SpecColor × _Specularness (parquet: (1.0, 0.97,
        // 0.90) × 0.9 — a near-metal F0 authored against Arena's own dim interior cubemaps). So the
        // reflection-strength knob scales those two as well; _ReflectColor stays in for the cubemap term.
        const string SpecColorName = "_SpecColor";
        static readonly int SpecColorId = Shader.PropertyToID(SpecColorName);
        const string SpecularnessName = "_Specularness";
        static readonly int SpecularnessId = Shader.PropertyToID(SpecularnessName);

        sealed class IndoorFloorEntry
        {
            internal Renderer Renderer;
            internal Material[] OriginalMaterials; // cached BEFORE cloning — restore target
            internal Material[] ClonedMaterials;   // renderer.materials after cloning
            internal List<(int matIndex, int propId, float original)> Props;
            internal List<(int matIndex, int propId, Color original)> ColorProps; // §20.2/§20.3 — _ReflectColor, _SpecColor
            internal List<(int matIndex, int propId, float original)> ReflectionFloatProps; // §20.3 — _Specularness
        }

        sealed class IndoorFloorState
        {
            internal bool Done;            // Classify() completed
            internal readonly List<IndoorFloorEntry> Entries = new List<IndoorFloorEntry>();
        }

        static readonly Dictionary<string, IndoorFloorState> _states = new Dictionary<string, IndoorFloorState>();
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
                var percentEntry = WeatherRig.IndoorCoverPercentEntry(map);
                if (percentEntry != null)
                {
                    percentEntry.SettingChanged += (_, __) => ReclassifyIfLoaded(map);
                }
                if (entry != null)
                {
                    entry.SettingChanged += (_, __) => ReapplyIfLoaded(map);
                }
                // §20.2
                var reflectionEntry = WeatherRig.IndoorReflectionStrengthEntry(map);
                if (reflectionEntry != null)
                {
                    reflectionEntry.SettingChanged += (_, __) => ReapplyIfLoaded(map);
                }
                var glassEntry = WeatherRig.CoverIncludesGlassEntry(map);
                if (glassEntry != null)
                {
                    // New roof photo first (glass now drawn or not), then classify against it — the same
                    // order InitializeForCurrentRaid uses on raid start.
                    glassEntry.SettingChanged += (_, __) =>
                    {
                        // Review 2026-09-19: the same map guard every other live path here has — a toggle in
                        // ANOTHER map's F12 section must not re-photograph the raid actually running — and no
                        // re-classify against a photo that failed to re-render (its cover would silently
                        // drift from the setting).
                        if (ArenaMapRaid.CurrentMap != map)
                        {
                            return;
                        }
                        if (RainCoverRepairs.RescanAndRerender("CoverIncludesGlass changed"))
                        {
                            ReclassifyIfLoaded(map);
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} CoverIncludesGlass " +
                                "changed but the roof photo did not re-render — classification left as is (toggle again, " +
                                "or nudge IndoorCoverPercent, to retry).");
                        }
                    };
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
            _states[map.Key] = new IndoorFloorState();
        }

        // Live re-classify (IndoorCoverPercent changed in F12): put the originals back, classify again.
        internal static void ReclassifyIfLoaded(ArenaMapDescriptor map)
        {
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap() || ArenaMapRaid.CurrentMap != map
                || !_states.TryGetValue(map.Key, out var state) || !state.Done)
            {
                return;
            }
            using var specularScope = ReflectiveSpecularRepairs.Suspend(map);
            RestoreEntries(state, out int restored, out int destroyed);
            Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} re-classifying: restored {restored} " +
                $"renderer(s), destroyed {destroyed} clone(s)");
            state.Done = false;
            Classify(map);
        }

        static void RestoreEntries(IndoorFloorState state, out int restoredRenderers, out int destroyedClones)
        {
            restoredRenderers = 0;
            destroyedClones = 0;
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
        }

        static void OnSceneUnloaded(Scene scene)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null || !_states.TryGetValue(map.Key, out var state))
            {
                return;
            }

            ReflectiveSpecularRepairs.Clear(); // remove the outer layer before destroying its indoor inputs
            RestoreEntries(state, out int restoredRenderers, out int destroyedClones);
            state.Done = false;

            if (restoredRenderers > 0 || destroyedClones > 0)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] {map.Key}: raid ended — restored " +
                    $"{restoredRenderers} renderer(s), destroyed {destroyedClones} clone(s).");
            }
        }

        // Called once per raid by the explicit scene/readiness bootstrap, not a camera-event postfix.
        internal static bool InitializeForCurrentRaid(ArenaMapDescriptor map)
        {
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap() || ArenaMapRaid.CurrentMap != map
                || WeatherRig.RainCoverForCurrentMap() != WeatherRig.RainCoverMode.RoofDepthBlack
                || !_states.TryGetValue(map.Key, out var state)) return false;
            if (state.Done) return true;
            using var specularScope = ReflectiveSpecularRepairs.Suspend(map);
            RestoreEntries(state, out _, out _); // also rolls back a prior incomplete classification
            if (!RainCoverRepairs.RescanAndRerender("automatic raid bootstrap", refreshCurrentCamera: true)) return false;
            try { Classify(map); }
            catch
            {
                RestoreEntries(state, out _, out _);
                throw;
            }
            return state.Done;
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
            if (WeatherRig.DepthPhotographerForCurrentMap()?._depthRT == null)
            {
                return; // bootstrap retries an unavailable donor photo
            }
            if (!_states.TryGetValue(map.Key, out var state))
            {
                return;
            }

            int floorLike = 0, indoor = 0, clones = 0;
            using var specularScope = ReflectiveSpecularRepairs.Suspend(map);
            var perScene = new List<string>();
            var indoorNames = new List<string>();
            var rejects = new List<string>();
            Texture2D scratch = null;
            try
            {
                scratch = new Texture2D(1, 1, TextureFormat.RFloat, false, true);
                int waterLayer = LayerMask.NameToLayer("Water");
                int skyLayer = LayerMask.NameToLayer("Sky");
                int percent = WeatherRig.IndoorCoverPercentForCurrentMap();
                float multiplier = WeatherRig.IndoorFloorSmoothnessForCurrentMap();
                float reflection = WeatherRig.IndoorReflectionStrengthForCurrentMap(); // §20.2

                int sceneCount = SceneManager.sceneCount;
                for (int i = 0; i < sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded || !ArenaMapRaid.IsGameplayScene(scene.name))
                    {
                        continue;
                    }

                    int sceneRenderers = 0;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                        sceneRenderers += renderers.Length;
                        for (int r = 0; r < renderers.Length; r++)
                        {
                            var renderer = renderers[r];
                            if (renderer == null)
                            {
                                continue;
                            }
                            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                            {
                                if (renderer.gameObject.name.IndexOf("indoor", StringComparison.OrdinalIgnoreCase) >= 0
                                    && renderer.gameObject.name.IndexOf("puddle", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    Reject(true, rejects, renderer, renderer.bounds, renderer.enabled ? "inactive" : "disabled", -1);
                                }
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
                            // Large candidates get their rejection reason logged (max 40 lines) — the
                            // 2026-09-19 host test classified 41 boards/trusses but not the hall floor.
                            string goName = renderer.gameObject.name;
                            bool large = goName.IndexOf("puddle", StringComparison.OrdinalIgnoreCase) < 0
                                && (goName.IndexOf("indoor", StringComparison.OrdinalIgnoreCase) >= 0
                                || goName.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0
                                || goName.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0
                                || goName.IndexOf("asphalt", StringComparison.OrdinalIgnoreCase) >= 0
                                || goName.IndexOf("lounge", StringComparison.OrdinalIgnoreCase) >= 0   // §20.2 Equator parquet
                                || goName.IndexOf("parquet", StringComparison.OrdinalIgnoreCase) >= 0);
                            // Host test 2026-09-19: Bay5's hall floor is part of 'Depot_indoor_LOD0', the whole
                            // interior shell (floor + inner walls, 45x22 m, 8.6 m tall), so a thin-slab test
                            // rejects exactly the renderer this pass exists for. Only towers/cranes are cut.
                            if (bounds.size.y > 12f)
                            {
                                Reject(large, rejects, renderer, bounds, "size.y", -1);
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
                                Reject(large, rejects, renderer, bounds, HasDecalShader(shared) ? "decal" : "shader", -1);
                                continue;
                            }

                            floorLike++;
                            int covered = CountCoveredSamples(map, scratch, bounds);
                            if (covered * 100 < percent * CoverGridSamples)
                            {
                                Reject(large, rejects, renderer, bounds, "cover", covered);
                                continue;
                            }

                            indoor++;
                            if (indoorNames.Count < 20)
                            {
                                indoorNames.Add(renderer.gameObject.name);
                            }
                            if (large)
                            {
                                Reject(true, rejects, renderer, bounds, "ACCEPTED", covered);
                            }
                            var entry = CloneAndScale(renderer, shared, multiplier, reflection);
                            state.Entries.Add(entry);
                            clones += entry.ClonedMaterials.Length;
                        }
                    }
                    perScene.Add($"{scene.name}={sceneRenderers}");
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
            for (int i = 0; i < rejects.Count; i++)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} rejected large candidate: {rejects[i]}");
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} scanned scenes (MeshRenderers): {string.Join(", ", perScene)}");
            Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} classified: floorLike={floorLike} " +
                $"indoor={indoor} materialClones={clones} multiplier={WeatherRig.IndoorFloorSmoothnessForCurrentMap():0.00} " +
                $"reflection={WeatherRig.IndoorReflectionStrengthForCurrentMap():0.00} " +
                $"coverPercent={WeatherRig.IndoorCoverPercentForCurrentMap()} grid={CoverGrid}x{CoverGrid}");
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
        static void Reject(bool large, List<string> rejects, Renderer renderer, Bounds bounds, string reason, int covered)
        {
            if (!large || rejects.Count >= 40)
            {
                return;
            }
            var t = renderer.transform;
            string path = t.parent != null ? t.parent.name + "/" + t.name : t.name;
            rejects.Add($"'{path}' reason={reason}{(covered >= 0 ? $"({covered}/{CoverGridSamples})" : "")} " +
                $"center={bounds.center:F1} size={bounds.size:F1} scene={renderer.gameObject.scene.name}");
        }

        const int CoverGrid = 5;
        const int CoverGridSamples = CoverGrid * CoverGrid;

        static int CountCoveredSamples(ArenaMapDescriptor map, Texture2D scratch, Bounds bounds)
        {
            float insetX = (bounds.max.x - bounds.min.x) * 0.1f;
            float insetZ = (bounds.max.z - bounds.min.z) * 0.1f;
            float loX = bounds.min.x + insetX, hiX = bounds.max.x - insetX;
            float loZ = bounds.min.z + insetZ, hiZ = bounds.max.z - insetZ;
            // Cover is judged from the renderer's FLOOR level (bounds.min.y): an interior shell's own
            // ceiling structure sits close under the roof, so "roof >= bounds.max.y + 1.5" would fail it.
            float coverLoY = bounds.min.y + 2.5f, coverHiY = bounds.min.y + 60f;

            int covered = 0;
            for (int gx = 0; gx < CoverGrid; gx++)
            {
                float wx = Mathf.Lerp(loX, hiX, gx / (float)(CoverGrid - 1));
                for (int gz = 0; gz < CoverGrid; gz++)
                {
                    float wz = Mathf.Lerp(loZ, hiZ, gz / (float)(CoverGrid - 1));
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
        static IndoorFloorEntry CloneAndScale(Renderer renderer, Material[] original, float multiplier, float reflection)
        {
            var clones = new Material[original.Length];
            var props = new List<(int matIndex, int propId, float original)>();
            var colorProps = new List<(int matIndex, int propId, Color original)>();
            var reflectionFloats = new List<(int matIndex, int propId, float original)>();
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
                // §20.2 — reflection tint (Color), exact name, type-checked so a same-named texture/float
                // on some other shader can never be misread as a color.
                if (HasColorProperty(clone.shader, ReflectColorName))
                {
                    var c = clone.GetColor(ReflectColorId);
                    colorProps.Add((i, ReflectColorId, c));
                    clone.SetColor(ReflectColorId, ScaleRgb(c, reflection));
                }
                // §20.3 — the G-buffer specular (F0) this family writes: color × intensity.
                if (HasColorProperty(clone.shader, SpecColorName))
                {
                    var c = clone.GetColor(SpecColorId);
                    colorProps.Add((i, SpecColorId, c));
                    clone.SetColor(SpecColorId, ScaleRgb(c, reflection));
                }
                if (HasFloatProperty(clone.shader, SpecularnessName))
                {
                    float f = clone.GetFloat(SpecularnessId);
                    reflectionFloats.Add((i, SpecularnessId, f));
                    clone.SetFloat(SpecularnessId, f * reflection);
                }
            }
            // Per-renderer instances from here on; renderer.sharedMaterials for THIS renderer also returns
            // `clones` from now on (same underlying slot Unity's material/sharedMaterial accessors both
            // read/write) — safe here because LightingLab candidate E discovers ground materials by
            // RENDERER NAME ("floor"/"ground"/"asphalt"), never by walking this renderer, and the yard
            // floor's own shared original (found via the YARD renderer) is untouched by this clone — see
            // this file's own class doc comment.
            renderer.materials = clones;
            return new IndoorFloorEntry
            {
                Renderer = renderer, OriginalMaterials = original, ClonedMaterials = clones,
                Props = props, ColorProps = colorProps, ReflectionFloatProps = reflectionFloats,
            };
        }

        static bool HasFloatProperty(Shader shader, string name)
        {
            if (shader == null)
            {
                return false;
            }
            try
            {
                int idx = shader.FindPropertyIndex(name);
                if (idx < 0)
                {
                    return false;
                }
                var t = shader.GetPropertyType(idx);
                return t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range;
            }
            catch
            {
                return false;
            }
        }

        static bool HasColorProperty(Shader shader, string name)
        {
            if (shader == null)
            {
                return false;
            }
            try
            {
                int idx = shader.FindPropertyIndex(name);
                return idx >= 0 && shader.GetPropertyType(idx) == UnityEngine.Rendering.ShaderPropertyType.Color;
            }
            catch
            {
                return false;
            }
        }

        static Color ScaleRgb(Color c, float m) => new Color(c.r * m, c.g * m, c.b * m, c.a);

        // Live re-apply (SettingChanged, wired from Install() above).
        internal static void ReapplyIfLoaded(ArenaMapDescriptor map)
        {
            if (ArenaMapRaid.CurrentMap != map || !_states.TryGetValue(map.Key, out var state) || state.Entries.Count == 0)
            {
                return;
            }
            using var specularScope = ReflectiveSpecularRepairs.Suspend(map);
            float multiplier = WeatherRig.IndoorFloorSmoothnessForCurrentMap();
            float reflection = WeatherRig.IndoorReflectionStrengthForCurrentMap(); // §20.2
            int scaled = 0, recolored = 0;
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
                if (entry.ColorProps == null)
                {
                    continue;
                }
                foreach (var (matIndex, propId, original) in entry.ColorProps)
                {
                    var mat = entry.ClonedMaterials[matIndex];
                    if (mat == null)
                    {
                        continue;
                    }
                    mat.SetColor(propId, ScaleRgb(original, reflection));
                    recolored++;
                }
                if (entry.ReflectionFloatProps == null)
                {
                    continue;
                }
                foreach (var (matIndex, propId, original) in entry.ReflectionFloatProps)
                {
                    var mat = entry.ClonedMaterials[matIndex];
                    if (mat == null)
                    {
                        continue;
                    }
                    mat.SetFloat(propId, original * reflection);
                    recolored++;
                }
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][IndoorFloorSmoothness] map={map.Key} multiplier={multiplier:0.00} " +
                $"reflection={reflection:0.00} reapplied live to {scaled} cloned smoothness value(s) + {recolored} " +
                $"reflection value(s) (_ReflectColor/_SpecColor/_Specularness) across {state.Entries.Count} indoor renderer(s).");
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
                $"reflection={WeatherRig.IndoorReflectionStrengthForCurrentMap():0.00} " +
                $"coverIncludesGlass={WeatherRig.CoverIncludesGlassForCurrentMap()} " +
                $"coverPercent={WeatherRig.IndoorCoverPercentForCurrentMap()} done={state.Done} " +
                $"indoor={state.Entries.Count} materialClones={clones}";
        }
    }
}
