using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    /// needed for the spike's material set").
    /// </summary>
    internal static class ShaderRebind
    {
        internal const string StandinPrefix = "Hidden/ArenaMapPort/";
        const string SidecarFileName = "material-shaders.json";
        const float SweepDelaySeconds = 3f;

        sealed class SidecarEntry
        {
            internal string Shader;
            internal int RenderQueue = -1; // -1 = shader default, matches §C.4's material.json convention
            internal string[] Keywords = Array.Empty<string>();
        }

        static bool _installed;
        static Dictionary<string, SidecarEntry> _sidecar; // material asset name -> sidecar entry
        static Dictionary<string, List<Shader>> _byName;  // loaded-shader exact name -> candidates
        static readonly Dictionary<string, int> _reboundCounts = new Dictionary<string, int>();
        static readonly HashSet<string> _unresolved = new HashSet<string>();      // shader names, reported by ReportSummary
        static readonly HashSet<string> _warnedOnce = new HashSet<string>();      // arbitrary one-time-warning dedup keys
        static GameObject _sweepHost;

        static string PluginDir => Path.GetDirectoryName(typeof(ShaderRebind).Assembly.Location) ?? ".";

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaEquatorRaid.IsOwnedScene(scene.name))
            {
                return;
            }

            LoadSidecar();
            BuildShaderIndex();
            RunPass(scene);
            ScheduleSweep();
        }

        static void OnSceneUnloaded(Scene scene)
        {
            // Reset once per raid, on the raid's own scene teardown — the loaded-shader set can differ
            // between raids/maps, so a stale index must not carry over (matches InterchangeRework's own
            // per-raid reset, MaterialShaderRebind.cs's sceneUnloaded handler).
            if (scene.name != ArenaMapPort.Shared.ArenaEquatorLocation.ScriptsSceneName)
            {
                return;
            }

            _byName = null;
            _reboundCounts.Clear();
            _unresolved.Clear();
            _warnedOnce.Clear();
            if (_sweepHost != null)
            {
                UnityEngine.Object.Destroy(_sweepHost);
            }
            _sweepHost = null;
        }

        static void ScheduleSweep()
        {
            if (_sweepHost != null)
            {
                return; // already scheduled for this raid
            }
            _sweepHost = new GameObject("ArenaMapPort_ShaderSweep") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_sweepHost);
            _sweepHost.AddComponent<Sweeper>().StartCoroutine(Sweeper.RunAfter(SweepDelaySeconds));
        }

        sealed class Sweeper : MonoBehaviour
        {
            internal static IEnumerator RunAfter(float seconds)
            {
                yield return new WaitForSeconds(seconds);
                RunPassOverAllOwnedScenes();
                ReportSummary();
            }
        }

        static void RunPassOverAllOwnedScenes()
        {
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && ArenaEquatorRaid.IsOwnedScene(scene.name))
                {
                    RunPass(scene);
                }
            }
        }

        static void RunPass(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var mats = renderers[r].sharedMaterials;
                    for (int m = 0; m < mats.Length; m++)
                    {
                        Rebind(mats[m]);
                    }
                }
            }
        }

        // Naturally idempotent: a successful rebind changes the material's shader away from the prefix,
        // so a later pass (the 3s sweep, or a re-load of the same scene) skips it without needing a
        // separate "already seen" set — and, unlike a "seen" set would, this still RETRIES a material
        // whose target shader simply wasn't loaded yet on the first pass.
        static void Rebind(Material material)
        {
            if (material == null || material.shader == null)
            {
                return;
            }
            var currentName = material.shader.name;
            if (!currentName.StartsWith(StandinPrefix, StringComparison.Ordinal))
            {
                return; // not one of our stand-ins — already good, or not ours
            }

            var originalName = currentName.Substring(StandinPrefix.Length);
            var target = FindLoadedExact(originalName) ?? Shader.Find(originalName);

            // O1: never rebind to a prefixed shader — by construction FindLoadedExact/Shader.Find on the
            // stripped name cannot return one of our own stand-ins, but guard explicitly anyway since this
            // is the one invariant O1 calls out by name.
            if (target == null || target.name.StartsWith(StandinPrefix, StringComparison.Ordinal))
            {
                _unresolved.Add(originalName);
                return;
            }

            material.shader = target;

            if (_sidecar != null && _sidecar.TryGetValue(MaterialKey(material.name), out var entry))
            {
                // Defensive cross-check: the sidecar's recorded shader for this material should agree
                // with the name we just derived from the stand-in shader's own name — a mismatch means
                // WP2's material import and its sidecar emission disagree about which material this is.
                if (!string.IsNullOrEmpty(entry.Shader) && entry.Shader != originalName &&
                    _warnedOnce.Add("sidecar-mismatch:" + material.name))
                {
                    Plugin.Log.LogWarning($"[ArenaMapPort][RebindShaders] '{material.name}': material-shaders.json " +
                                           $"says shader '{entry.Shader}', but the stand-in shader on the material " +
                                           $"names '{originalName}' — using '{originalName}' (derived from the " +
                                           "material itself); renderQueue/keywords still restored from the sidecar.");
                }

                if (entry.RenderQueue >= 0)
                {
                    material.renderQueue = entry.RenderQueue;
                }
                material.shaderKeywords = entry.Keywords;
            }

            _reboundCounts[originalName] = _reboundCounts.TryGetValue(originalName, out var n) ? n + 1 : 1;
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

        static void LoadSidecar()
        {
            if (_sidecar != null)
            {
                return; // sidecar content is static for the process lifetime
            }
            _sidecar = new Dictionary<string, SidecarEntry>();

            var path = Path.Combine(PluginDir, SidecarFileName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][RebindShaders] {SidecarFileName} not found at '{path}'; " +
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

                    _sidecar[pair.Key] = entry;
                }

                Plugin.Log.LogInfo($"[ArenaMapPort][RebindShaders] {SidecarFileName}: {_sidecar.Count} material entries");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][RebindShaders] {SidecarFileName} could not be read: " +
                                     $"{e.GetType().Name}: {e.Message}");
            }
        }

        static void ReportSummary()
        {
            // §F.5 / ref-manimal.md §3 point 7: "the log line is the only ground truth" — one line per
            // rebound shader name, plus every name that never resolved.
            foreach (var pair in _reboundCounts)
            {
                Plugin.Log.LogInfo($"[RebindShaders] {pair.Value}x {pair.Key}");
            }
            foreach (var name in _unresolved)
            {
                Plugin.Log.LogError($"[ArenaMapPort][RebindShaders] unresolved: '{name}' — no loaded shader of that " +
                                     "exact name and Shader.Find found none; material(s) using it stay on the " +
                                     $"'{StandinPrefix}{name}' stand-in (visible as a flat/incorrect surface, not " +
                                     "necessarily magenta, since the stand-in itself is a real, compiling shader).");
            }
        }
    }
}
