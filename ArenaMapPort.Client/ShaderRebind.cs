using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// §F.3 + LEAD OVERRIDE O1. Our stand-in shaders are named "Hidden/ArenaMapPort/" + &lt;original BSG
    /// shader name&gt; (WP2, §B.WP2.3) — deliberately NOT the real name, which is what InterchangeRework's
    /// and Icebreaker's own rip-based mods use (ref-interchange.md §3, ref-manimal.md §3). O1's reasoning:
    /// identical names would let <c>Shader.Find</c>/the loaded-shader table return OUR OWN stand-in instead
    /// of the game's real compiled shader (Icebreaker playbook "Shader stand-in forensics" item 7) — the
    /// prefix makes that structurally impossible, since a target found by exact original-name lookup can
    /// never itself start with our prefix.
    ///
    /// Algorithm (O1, exact): for every material whose current shader name starts with the prefix, strip
    /// it to get the original name, look that name up first among <c>Resources.FindObjectsOfTypeAll&lt;Shader&gt;()</c>
    /// (exact match), then fall back to <c>Shader.Find</c>; never rebind to a prefixed shader; restore the
    /// per-material renderQueue/keywords WP2 recorded in the material-shaders.json sidecar (a reassigned
    /// shader instance can differ in defaults from the stand-in the material was imported against); log
    /// "[RebindShaders] Nx &lt;name&gt;" per shader and list unresolved ones.
    ///
    /// Two passes per raid — immediate (on each owned scene's sceneLoaded) and again ~3s later — to catch
    /// any late-instantiated material (§F.3: "for the spike, two passes (immediate + 3s) is sufficient").
    /// Structure (per-raid shader index, sweep-after-delay, sidecar JSON keyed by material name) follows
    /// the convergent Icebreaker/InterchangeRework pattern (InterchangeRework.Client/MaterialShaderRebind.cs,
    /// MIT — attribution in THIRD-PARTY-NOTICES.md), adapted for O1's prefixed-name scheme instead of their
    /// error-shader/Substitutes-table scheme (not needed here — see O1 and §F.3: "no substitution table is
    /// needed for the spike's material set"). A later fallback table for names SPT does not ship is owned by
    /// <see cref="ShaderStandinPolicy"/> (per-map allow-list: Bay5 as shipped, AirPit with its own switch) and
    /// the AirPit texture/float work by <see cref="ShaderStandins"/>.
    /// </summary>
    internal static class ShaderRebind
    {
        internal const string StandinPrefix = "Hidden/ArenaMapPort/";

        // Exposed for LightingDiagnosticProbe/DaytimeVisualsProbe's wetness-mitigation summary line.
        internal static bool GroundMaterialWetnessFixEnabled =>
            _groundMaterialWetnessFixEnabled != null && _groundMaterialWetnessFixEnabled.Value;

        // Equator's sidecar keeps its pre-generalization filename exactly — it is already
        // deployed at dist/BepInEx/plugins/maschine-ArenaMapPort/material-shaders.json and must keep
        // loading from there without a redeploy (multimap-generalize.md). Every OTHER map gets
        // its own file instead of overwriting/merging into Equator's already-verified one.
        const string EquatorSidecarFileName = "material-shaders.json";
        const float SweepDelaySeconds = 3f;

        // donor-settings-camera-weather §C.1. Materials are pipeline-generated and named by their
        // extraction hash, NOT their human-readable source name (MaterialImporter.cs:60, `mat.name =
        // matId`) — confirmed against work/extract/bay5/materials/be005d9cd5d9384e.json, whose
        // "source_name" field is "Bay5_Vertex_Paint_Ground". Matching on the hash is what actually
        // identifies the material at runtime. This is pipeline-derived, fragile content — a
        // re-extraction of Bay5 can change the hash — so ApplyKnownValueFixes logs a loud one-time
        // warning if a material with this hash is ever found but its pre-fix _BlendStrength doesn't
        // match what was measured when this hash was captured.
        //
        // Restoration history: this fix originally shipped in commit aae5a50 (2026-09-17) under key
        // names "FixBay5GroundBlendStrength"/"FixBay5GroundSmoothness"/"Bay5GroundBlendStrengthValue"/
        // "Bay5GroundSmoothnessScale", then was REMOVED in 0792f53 the same day because the 2026-09-17
        // playtest that tested it was confounded by the (since-fixed) sun/dome-recenter bug — the fix was
        // never cleanly re-tested, not refuted. Re-added here (donor-settings-camera-weather §C.1) as a
        // Tier-2 candidate, default ON (restoring previously-reviewed code, not a new experiment) but
        // under NEW key names ("GroundMaterialWetnessFixEnabled"/"GroundSmoothnessMultiplier") per the
        // project's hard rule — BepInEx persists a saved config value under its old key even after the
        // code that reads it is removed and re-added, so reusing the old names risks silently resurrecting
        // whatever a tester last left them at under the old, since-superseded semantics.
        const string Bay5GroundMaterialHash = "be005d9cd5d9384e";
        const float Bay5GroundExpectedBlendStrengthBeforeFix = 5.0f;
        const float Bay5GroundBlendStrengthTarget = 2.0f; // matches aae5a50's own "Bay5GroundBlendStrengthValue" default

        static ConfigEntry<bool> _groundMaterialWetnessFixEnabled;
        static ConfigEntry<float> _groundSmoothnessMultiplier;

        // Arena shader names with no compiled SPT equivalent at all (shader-compat-audit F2) — real
        // bug, tentative/unverified substitutes. The table (unchanged since it shipped for Bay5) and the
        // per-map rules now live in ShaderStandinPolicy: Bay5 behind EnableShaderFallbacks (default OFF),
        // AirPit behind its own AirPitShaderStandins (default ON), every other map never. Nature/SpeedTree8
        // stays out of the table: its SPT analog (Nature/SpeedTreeEFT) has a materially different property
        // set — an obviously-wrong magenta stand-in is a more honest failure state than a plausible-looking
        // wrong tree.
        static ConfigEntry<bool> _enableShaderFallbacks;

        sealed class SidecarEntry
        {
            internal string Shader;
            internal int RenderQueue = -1; // -1 = shader default, matches §C.4's material.json convention
            internal string[] Keywords = Array.Empty<string>();
        }

        static bool _installed;
        // Loaded lazily, once per map key — only one map's scenes are ever loaded in a given raid,
        // so this never needs merging, just per-map isolation (multimap-generalize.md).
        static readonly Dictionary<string, Dictionary<string, SidecarEntry>> _sidecarByMap =
            new Dictionary<string, Dictionary<string, SidecarEntry>>();
        static Dictionary<string, List<Shader>> _byName;  // loaded-shader exact name -> candidates
        static readonly Dictionary<string, int> _reboundCounts = new Dictionary<string, int>();
        static readonly HashSet<string> _unresolved = new HashSet<string>();      // shader names, reported by ReportSummary
        static readonly HashSet<string> _warnedOnce = new HashSet<string>();      // arbitrary one-time-warning dedup keys
        static GameObject _sweepHost;

        static string PluginDir => Path.GetDirectoryName(typeof(ShaderRebind).Assembly.Location) ?? ".";

        // internal, not private: Plugin.cs's startup banner also logs this file's expected path per map.
        internal static string SidecarFileName(ArenaMapDescriptor map) =>
            map.Key == ArenaMapRegistry.Equator.Key ? EquatorSidecarFileName : $"material-shaders.{map.Key}.json";

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _enableShaderFallbacks = Plugin.Instance.Settings.Bind("MaterialFixes", "EnableShaderFallbacks", false,
                "Substitutes a real SPT shader for Arena shader names with no compiled SPT equivalent at " +
                "all (162 materials / 6,942 renderers port-wide, currently stuck on the magenta stand-in " +
                "fragment). Mappings are inferred/unverified (shader-compat-audit F2). OFF by default, and " +
                "when ON this pass, applies ONLY to arena_bay5 — several of the same shader names " +
                "(Cloth/ClothShader, Custom/Billboard_FogSheet_Simple) are also used on arena_equator, " +
                "which was never evaluated with these substitutes; widen the scope only after a dedicated " +
                "per-map review.");

            // Default OFF: Arena renders this exact material correctly, so the values are right for a
            // correct lighting environment. The switch exists to compare, not to paper over an
            // environment problem (camera, reflections, scattering).
            _groundMaterialWetnessFixEnabled = Plugin.Instance.Settings.Bind("MaterialFixes", "GroundMaterialWetnessFixEnabled", false,
                $"Comparison switch for {Bay5GroundMaterialHash} (Bay5_Vertex_Paint_Ground): softens the " +
                "vertex-paint layer blend (_BlendStrength 5.0 -> 2.0) and scales its near-mirror smoothness " +
                "(_Smoothness0-2/_FresnelSpecular2, measured 0.85-0.98) by GroundSmoothnessMultiplier. " +
                "Applies only to arena_bay5, on the next raid load.");
            _groundSmoothnessMultiplier = Plugin.Instance.Settings.Bind("MaterialFixes", "GroundSmoothnessMultiplier", 0.6f,
                "Multiplier applied to _Smoothness0-2/_FresnelSpecular2 when GroundMaterialWetnessFixEnabled=true. " +
                "Requires a fresh raid load to change — this hook runs once per material, on scene load.");

            ShaderStandins.BindConfig(); // AirPit's own keys (the legacy switch above stays Bay5-only)

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaMapRaid.IsOwnedScene(scene.name))
            {
                return;
            }

            // Scene is already registered with SceneManager by the time sceneLoaded fires (see
            // ArenaMapRaid's own doc comment), so CurrentMap resolves even on the Scripts scene's
            // own load event.
            var map = ArenaMapRaid.CurrentMap;
            if (map == null)
            {
                return;
            }

            LoadSidecar(map);
            BuildShaderIndex();
            RunPass(scene, map);
            ScheduleSweep(map);
        }

        static void OnSceneUnloaded(Scene scene)
        {
            // Reset once per raid, on the raid's own scene teardown — the loaded-shader set can differ
            // between raids/maps, so a stale index must not carry over (matches InterchangeRework's own
            // per-raid reset, MaterialShaderRebind.cs's sceneUnloaded handler). Stateless resolution
            // (not ArenaMapRaid.CurrentMap) — see ArenaMapRaid's own doc comment for why.
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null)
            {
                return;
            }

            _byName = null;
            _reboundCounts.Clear();
            _unresolved.Clear();
            _warnedOnce.Clear();
            ShaderStandins.ResetRaid();
            if (_sweepHost != null)
            {
                UnityEngine.Object.Destroy(_sweepHost);
            }
            _sweepHost = null;
        }

        static void ScheduleSweep(ArenaMapDescriptor map)
        {
            if (_sweepHost != null)
            {
                return; // already scheduled for this raid
            }
            _sweepHost = new GameObject("ArenaMapPort_ShaderSweep") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_sweepHost);
            _sweepHost.AddComponent<Sweeper>().StartCoroutine(Sweeper.RunAfter(SweepDelaySeconds, map));
        }

        sealed class Sweeper : MonoBehaviour
        {
            internal static IEnumerator RunAfter(float seconds, ArenaMapDescriptor map)
            {
                yield return new WaitForSeconds(seconds);
                RunPassOverAllOwnedScenes(map);
                ReportSummary(map);
            }
        }

        static void RunPassOverAllOwnedScenes(ArenaMapDescriptor map)
        {
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    RunPass(scene, map);
                }
            }
        }

        // impl-raidstart-repairs item (4) / audit-reference-repairs.md "Recommendation": log an
        // immediate, synchronous per-scene summary at the end of THIS pass, not just the 3s-delayed
        // ReportSummary() sweep — a crash in the first few seconds of a raid (like the SpatialAudioSystem
        // NRE this task fixes) previously left no evidence in the log of whether the rebind pass ran at
        // all. Matches InterchangeRework's own MaterialShaderRebind.cs pattern (OnIrSceneLoaded logs
        // immediately; attribution already covers this file, THIRD-PARTY-NOTICES.md). The 3s
        // ReportSummary() sweep still runs unchanged, for stragglers only (materials whose target shader
        // was not yet loaded on this pass).
        static void RunPass(Scene scene, ArenaMapDescriptor map)
        {
            int considered = 0;
            int rebound = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                // Native projector decals store their material on the component, not a Renderer.
                // Resolve before StaticDeferredDecalRenderer clones it into its draw groups.
                foreach (var decal in root.GetComponentsInChildren<StaticDeferredDecal>(true))
                {
                    var material = decal._material;
                    if (material != null && material.shader != null &&
                        material.shader.name.StartsWith(StandinPrefix, StringComparison.Ordinal))
                    {
                        considered++;
                        if (Rebind(material, map)) rebound++;
                    }
                }
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    // AirPit A/B mode: fog-sheet quads are disabled instead of rebound (a no-op on every other map).
                    if (ShaderStandins.TryHideFogSheet(renderers[r], map))
                    {
                        continue;
                    }
                    var mats = renderers[r].sharedMaterials;
                    for (int m = 0; m < mats.Length; m++)
                    {
                        var material = mats[m];
                        if (material == null || material.shader == null)
                        {
                            continue;
                        }
                        if (!material.shader.name.StartsWith(StandinPrefix, StringComparison.Ordinal))
                        {
                            // Cached bundle materials retain their real shader on a later visit.
                            VertexGroundDiffuseRepairs.CaptureAndApply(material, material.shader.name, map);
                            continue;
                        }
                        considered++;
                        if (Rebind(material, map))
                        {
                            rebound++;
                        }
                    }
                }
            }

            Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] {scene.name}: pass complete — " +
                                $"{rebound}/{considered} stand-in material(s) rebound this pass");
        }

        // Naturally idempotent: a successful rebind changes the material's shader away from the prefix,
        // so a later pass (the 3s sweep, or a re-load of the same scene) skips it without needing a
        // separate "already seen" set — and, unlike a "seen" set would, this still RETRIES a material
        // whose target shader simply wasn't loaded yet on the first pass. Returns true iff this call
        // performed the rebind (used by RunPass for its immediate per-scene summary line above).
        static bool Rebind(Material material, ArenaMapDescriptor map)
        {
            if (material == null || material.shader == null)
            {
                return false;
            }
            var currentName = material.shader.name;
            // This shader implements the ported source terrain arrays and is the final
            // runtime material, with no matching EFT shader to rebind onto.
            if (currentName == "ArenaMapPort/Terrain")
                return false;
            if (!currentName.StartsWith(StandinPrefix, StringComparison.Ordinal))
            {
                return false; // not one of our stand-ins — already good, or not ours
            }

            var originalName = currentName.Substring(StandinPrefix.Length);
            var target = FindLoadedExact(originalName) ?? Shader.Find(originalName);

            // 2026-10-12 host finding: EFT 0.16.9.5 carries the AirPit fallback-table names itself (Customs'
            // scene shared assets, loaded in-process by raid start), so the direct lookup wins and the
            // stand-in path below never runs. Say so once per name and raid, so a future raid where the
            // substitutes DO run is visible in the log.
            if (target != null && ShaderStandinPolicy.TryGetFallback(map.Key, originalName, out _) &&
                _warnedOnce.Add("native-shader:" + originalName))
            {
                Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] '{originalName}' resolved to EFT's own loaded " +
                                    $"shader; the stand-in substitute is not used (map={map.Key})");
            }

            // §a2 — real bug (14 Arena shader names have no compiled SPT shader at all), tentative
            // fallback mapping. Per-map allow-list (ArenaMapDescriptor.ShaderFallbackNames) and rows in
            // ShaderStandinPolicy: Bay5 behind the legacy
            // EnableShaderFallbacks switch (default OFF, plain swap, as before), AirPit behind
            // AirPitShaderStandins (default ON, only its four names, plus ShaderStandins' texture/float work),
            // every other map never — several of these names (Cloth/ClothShader,
            // Custom/Billboard_FogSheet_Simple) are also used on arena_equator, which was never evaluated
            // with these unverified substitutes (adversarial review). Widening it needs its own review.
            ShaderStandins.Captured captured = null;
            if (target == null &&
                ShaderStandinPolicy.FallbacksActive(map.Key,
                    _enableShaderFallbacks != null && _enableShaderFallbacks.Value, ShaderStandins.AirPitStandinsEnabled) &&
                ShaderStandinPolicy.TryGetFallback(map.Key, originalName, out var fallbackName))
            {
                target = FindLoadedExact(fallbackName) ?? Shader.Find(fallbackName);
                if (target != null)
                {
                    if (ShaderStandinPolicy.UsesExtendedStandins(map.Key))
                    {
                        // Must be read while the stand-in shader (which declares every imported property) is still assigned.
                        captured = ShaderStandins.Capture(material, originalName, map);
                    }
                    Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] '{originalName}' has no SPT shader — " +
                                        $"using fallback '{fallbackName}' (tentative, unverified visual " +
                                        $"correctness, map={map.Key})");
                }
            }

            // O1: never rebind to a prefixed shader — by construction FindLoadedExact/Shader.Find on the
            // stripped name cannot return one of our own stand-ins, but guard explicitly anyway since this
            // is the one invariant O1 calls out by name.
            if (target == null || target.name.StartsWith(StandinPrefix, StringComparison.Ordinal))
            {
                _unresolved.Add(originalName);
                return false;
            }

            material.shader = target;
            // A name whose first pass failed (target shader not loaded yet) and that the 3 s sweep resolved must not
            // be reported as unresolved. A later material that still fails re-adds it above.
            _unresolved.Remove(originalName);

            var sidecar = _sidecarByMap.TryGetValue(map.Key, out var s) ? s : null;
            if (sidecar != null && sidecar.TryGetValue(MaterialKey(material.name), out var entry))
            {
                // Defensive cross-check: the sidecar's recorded shader for this material should agree
                // with the name we just derived from the stand-in shader's own name — a mismatch means
                // WP2's material import and its sidecar emission disagree about which material this is.
                if (!string.IsNullOrEmpty(entry.Shader) && entry.Shader != originalName &&
                    _warnedOnce.Add("sidecar-mismatch:" + material.name))
                {
                    Plugin.Log.LogWarning($"[ArenaMapPort][RebindShaders] '{material.name}': " +
                                           $"{SidecarFileName(map)} says shader '{entry.Shader}', but the stand-in " +
                                           $"shader on the material names '{originalName}' — using '{originalName}' " +
                                           "(derived from the material itself); renderQueue/keywords still " +
                                           "restored from the sidecar.");
                }

                if (entry.RenderQueue >= 0)
                {
                    // 2026-10-12: the sidecar carries the stand-in's queue (2000) for every source material that
                    // used the shader default; forcing it onto a transparent shader drew AirPit's hall glass in
                    // the opaque phase (near-black, opaque panes). ShaderStandinPolicy restores the shader default
                    // for that case on AirPit; -1 means "the shader's own queue".
                    int queue = ShaderStandinPolicy.EffectiveRenderQueue(map.Key, entry.RenderQueue, target.renderQueue);
                    material.renderQueue = queue;
                    if (queue != entry.RenderQueue && _warnedOnce.Add("queue-default:" + originalName))
                    {
                        Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] '{originalName}': sidecar queue " +
                                            $"{entry.RenderQueue} is the stand-in default, shader default is " +
                                            $"{target.renderQueue}; using the shader default (map={map.Key})");
                    }
                }
                material.shaderKeywords = entry.Keywords;
            }

            if (captured != null)
            {
                ShaderStandins.ApplyAfterSwap(material, captured, originalName, map);
            }

            ApplyKnownValueFixes(material, originalName, map);

            _reboundCounts[originalName] = _reboundCounts.TryGetValue(originalName, out var n) ? n + 1 : 1;
            return true;
        }

        // donor-settings-camera-weather §C.1. Runs immediately after the shader swap above, using the
        // REAL post-swap shader name — property values themselves survive the reassignment untouched
        // (Unity keeps a material's named property values regardless of which shader is currently
        // assigned; independently evidenced by the sidecar restore just above already relying on the same
        // fact for renderQueue/keywords). Gated on the registry key (ArenaMapRegistry.Bay5.Key), not a
        // hand-typed literal.
        static void ApplyKnownValueFixes(Material material, string realShaderName, ArenaMapDescriptor map)
        {
            VertexGroundDiffuseRepairs.CaptureAndApply(material, realShaderName, map);
            if (_groundMaterialWetnessFixEnabled == null || !_groundMaterialWetnessFixEnabled.Value)
            {
                return;
            }
            if (map.Key != ArenaMapRegistry.Bay5.Key || realShaderName != "Custom/Vert Paint Shader Solid")
            {
                return; // scoped strictly to the evidenced case
            }

            var key = MaterialKey(material.name);
            if (key != Bay5GroundMaterialHash)
            {
                return; // NOT Bay5_road_asphalt_01 (hash c0cee7841b421fb1) — its own _BlendStrength=2.0
                        // sits inside vanilla's observed range; no evidence it needs correction
            }

            // Drift guard: material.name is a pipeline-generated extraction hash (MaterialImporter.cs:60),
            // not a stable identifier — a re-extraction of Bay5 can reassign it. If a material with this
            // exact hash is ever found but doesn't look like the material this hash was captured against,
            // log a loud one-time warning instead of silently mis-applying the fix a second time.
            float preFixBlendStrength = material.GetFloat("_BlendStrength");
            if (Math.Abs(preFixBlendStrength - Bay5GroundExpectedBlendStrengthBeforeFix) > 0.01f &&
                _warnedOnce.Add("bay5-ground-blendstrength-drift"))
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][MaterialFixes] material '{Bay5GroundMaterialHash}' " +
                                       $"found (map={map.Key}, shader={realShaderName}) but its pre-fix " +
                                       $"_BlendStrength={preFixBlendStrength:0.0}, not the expected " +
                                       $"{Bay5GroundExpectedBlendStrengthBeforeFix:0.0} captured when this " +
                                       "hash was identified as Bay5_Vertex_Paint_Ground — a re-extraction " +
                                       "may have reassigned the hash to a different material. The fix below " +
                                       "still applies to whatever this material actually is; verify with the " +
                                       "F10 inspector (SceneInspector.cs) before trusting it.");
            }

            material.SetFloat("_BlendStrength", Bay5GroundBlendStrengthTarget);

            float scale = _groundSmoothnessMultiplier != null ? _groundSmoothnessMultiplier.Value : 0.6f;
            foreach (var prop in new[] { "_Smoothness0", "_Smoothness1", "_Smoothness2" })
            {
                material.SetFloat(prop, material.GetFloat(prop) * scale);
            }
            material.SetFloat("_FresnelSpecular2", material.GetFloat("_FresnelSpecular2") * scale);
            // _ReflectionStrength (1.0) intentionally left untouched — it already matches vanilla's 1.0,
            // nothing about it is an outlier for this material.

            Plugin.Log.LogDebug($"[ArenaMapPort][MaterialFixes] '{material.name}' (Bay5_Vertex_Paint_Ground): " +
                                $"_BlendStrength {preFixBlendStrength:0.0} -> {Bay5GroundBlendStrengthTarget:0.0}, " +
                                $"_Smoothness0-2/_FresnelSpecular2 scaled by {scale:0.00}");
        }

        static Shader FindLoadedExact(string name)
        {
            if (_byName != null && _byName.TryGetValue(name, out var candidates))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    if (candidate != null && !candidate.name.StartsWith(StandinPrefix, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }
            return null;
        }

        static void BuildShaderIndex()
        {
            if (_byName != null)
            {
                return; // once per raid — FindObjectsOfTypeAll is too expensive per material
            }

            _byName = new Dictionary<string, List<Shader>>();
            foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (shader == null || string.IsNullOrEmpty(shader.name))
                {
                    continue;
                }
                if (!_byName.TryGetValue(shader.name, out var list))
                {
                    _byName[shader.name] = list = new List<Shader>();
                }
                list.Add(shader);
            }
        }

        // "<name> (Instance)" -> "<name>", matching Unity's own runtime-instance naming convention for a
        // material accessed via .material rather than .sharedMaterial.
        static string MaterialKey(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }
            var marker = name.IndexOf(" (Instance)", StringComparison.Ordinal);
            return marker >= 0 ? name.Substring(0, marker) : name;
        }

        static void LoadSidecar(ArenaMapDescriptor map)
        {
            if (_sidecarByMap.ContainsKey(map.Key))
            {
                return; // sidecar content is static for the process lifetime, per map
            }
            var sidecar = new Dictionary<string, SidecarEntry>();
            _sidecarByMap[map.Key] = sidecar;

            var fileName = SidecarFileName(map);
            var path = Path.Combine(PluginDir, fileName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][RebindShaders] {fileName} not found at '{path}'; " +
                                       "shader identity rebind still runs, but per-material renderQueue/keywords " +
                                       "cannot be restored.");
                return;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                foreach (var pair in root)
                {
                    if (!(pair.Value is JObject entryObj))
                    {
                        continue;
                    }

                    var entry = new SidecarEntry
                    {
                        Shader = entryObj.Value<string>("shader"),
                        RenderQueue = entryObj.Value<int?>("renderQueue") ?? -1,
                    };

                    if (entryObj["keywords"] is JArray keywords)
                    {
                        var list = new string[keywords.Count];
                        for (int i = 0; i < keywords.Count; i++)
                        {
                            list[i] = (string)keywords[i];
                        }
                        entry.Keywords = list;
                    }

                    sidecar[pair.Key] = entry;
                }

                Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] {fileName}: {sidecar.Count} material entries");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][RebindShaders] {fileName} could not be read: " +
                                     $"{e.GetType().Name}: {e.Message}");
            }
        }

        static void ReportSummary(ArenaMapDescriptor map)
        {
            // §F.5 / ref-manimal.md §3 point 7: "the log line is the only ground truth" — one line per
            // rebound shader name, plus every name that never resolved.
            foreach (var pair in _reboundCounts)
            {
                Plugin.Log.LogDebug($"[RebindShaders] {pair.Value}x {pair.Key}");
            }

            // AirPit's four allowed names staying magenta because the developer switched AirPitShaderStandins off are the
            // deliberate A/B baseline: ONE Info line per raid carries the names, the per-name detail is Debug. Every
            // other unresolved name is real degradation and keeps its Error line, as before this item: Equator and
            // Bay5 (their names stay magenta unless Bay5's legacy switch is on), AirPit with the switch on (a
            // substitute that could not be resolved), and any name outside the map's allow-list.
            var knownGaps = new List<string>();
            foreach (var name in _unresolved)
            {
                if (ShaderStandinPolicy.IsKnownGap(map.Key, name, ShaderStandins.AirPitStandinsEnabled))
                {
                    knownGaps.Add(name);
                    Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] unresolved: '{name}' — no loaded shader of that " +
                                        "exact name and Shader.Find found none; material(s) using it stay on the " +
                                        $"'{StandinPrefix}{name}' stand-in (magenta, AirPitShaderStandins=false).");
                }
                else
                {
                    Plugin.Log.LogError($"[ArenaMapPort][RebindShaders] unresolved: '{name}' — no loaded shader of that " +
                                         "exact name and Shader.Find found none; material(s) using it stay on the " +
                                         $"'{StandinPrefix}{name}' stand-in (visible as a flat/incorrect surface, not " +
                                         "necessarily magenta, since the stand-in itself is a real, compiling shader).");
                }
            }
            if (knownGaps.Count > 0)
            {
                Plugin.Log.LogInfo("[ArenaMapPort][RebindShaders] " + ShaderStandinPolicy.FormatUnresolvedSummary(map.Key, knownGaps));
            }
            if (ShaderStandins.HiddenFogSheetRenderers > 0)
            {
                // Counts every fog-sheet renderer whose own enabled flag was on, including those under groups the
                // client deactivated at raid start (PASSPORT_CONTROL_DOORS), so up to 127 on the 0.6.0 package.
                Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] {map.Key}: AirPitFogSheets=Hidden, " +
                                    $"{ShaderStandins.HiddenFogSheetRenderers} fog-sheet renderer(s) disabled this raid " +
                                    "(inactive hierarchies included)");
            }
        }
    }
}
