using System.Collections;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// daytime-and-visuals, F6 — 2026-09-17 playtest symptom B: the sea around the harbor rendered as a
    /// flat, uniform grey plane stretching to the horizon (no water rendering at all). Root cause
    /// confirmed directly in work/extract/bay5/scenes/Arena_Bay5_Main.json: the "Sea" > "Plane" object
    /// (id 19568) has <c>"material_ids": []</c> and <c>"layer": 4</c> (Unity's built-in "Water" layer) —
    /// an authored water surface whose real water material never made it through the port, so it renders
    /// with Unity's own default fallback material (flat grey).
    ///
    /// Not Bay5-specific: the detection predicate (an empty <c>sharedMaterials</c> array on a renderer
    /// tagged the "Water" layer) generalizes to any owned scene, any map — matches the plan's own
    /// framing ("generic detection, not Bay5-specific").
    ///
    /// Cosmetic stopgap only, and explicitly out of scope for anything more: <c>Shader.Find</c>ing a
    /// real water shader (<c>FX/SimpleWater4</c>, proven loadable via arena_factory's own binding) is
    /// tried first; if that ever fails (missing on some future build), the renderer is disabled instead
    /// of left drawing the default-material grey plane — either result is strictly better than today's
    /// symptom, never worse. Capturing the map's REAL authored water material/shader
    /// (<c>WaterContainer</c>/<c>WaterRenderer</c>) is tools/-side work that invalidates receipts — see
    /// docs/progress/daytime-and-visuals.md's Priority-2 section; deliberately not attempted here.
    /// </summary>
    internal static class SeaPlaceholderRepairs
    {
        const string PlaceholderShaderName = "FX/SimpleWater4";
        const float PassDelaySeconds = 2f; // margin for every owned scene of this raid to finish loading

        static ConfigEntry<bool> _enabled;
        static bool _installed;
        static GameObject _host;
        static Material _placeholderMaterial; // built once per process; shared across every matching renderer

        // Exposed for DaytimeVisualsProbe's consolidated F1-F6 summary line. null = pass hasn't run yet
        // this raid (still pending, or Enabled=false); otherwise "applied Nx" / "disabled Nx (shader not found)".
        internal static string LastResult { get; private set; }

        internal static bool Enabled => _enabled != null && _enabled.Value;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _enabled = Plugin.Instance.Config.Bind("Sea", "EnableWaterPlaceholder", true,
                "Assigns a placeholder water material (FX/SimpleWater4) to any renderer with an empty " +
                "sharedMaterials array on the built-in 'Water' layer (e.g. Bay5's Sea/Plane) — stands in " +
                "for a real authored water surface that never made it through the port. Falls back to " +
                "disabling the renderer if the placeholder shader can't be found. Cosmetic stopgap only, " +
                "see this file's own doc comment.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaMapRaid.IsOwnedScene(scene.name))
            {
                return;
            }
            if (_host != null)
            {
                return; // already scheduled for this raid
            }

            LastResult = null;
            _host = new GameObject("ArenaMapPort_SeaPlaceholderSweep") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<Runner>().StartCoroutine(Runner.RunAfter(PassDelaySeconds));
        }

        static void OnSceneUnloaded(Scene scene)
        {
            // Stateless resolution — see ArenaMapRaid's own doc comment for why an unload handler must
            // never depend on a shared mutable "current map" field.
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null)
            {
                return;
            }
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }
            _host = null;
            LastResult = null;
        }

        sealed class Runner : MonoBehaviour
        {
            internal static IEnumerator RunAfter(float seconds)
            {
                yield return new WaitForSeconds(seconds);
                RunPass();
            }
        }

        static void RunPass()
        {
            if (_enabled == null || !_enabled.Value)
            {
                LastResult = "disabled (config)";
                Plugin.Log.LogInfo("[ArenaMapPort][Sea] EnableWaterPlaceholder=false — skipped");
                return;
            }

            int waterLayer = LayerMask.NameToLayer("Water");
            if (waterLayer < 0)
            {
                LastResult = "skipped (no 'Water' layer)";
                Plugin.Log.LogWarning("[ArenaMapPort][Sea] LayerMask.NameToLayer(\"Water\") returned -1 — " +
                                       "this build has no 'Water' layer, skipping (should not happen, it's " +
                                       "one of Unity's built-in layers 0-7)");
                return;
            }

            int applied = 0;
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
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
                        if (renderer.gameObject.layer != waterLayer || renderer.sharedMaterials.Length != 0)
                        {
                            continue;
                        }

                        if (TryGetPlaceholderMaterial(out var placeholder))
                        {
                            renderer.sharedMaterials = new[] { placeholder };
                        }
                        else
                        {
                            renderer.enabled = false;
                        }
                        applied++;
                        Plugin.Log.LogInfo($"[ArenaMapPort][Sea] '{renderer.gameObject.name}' (scene='{scene.name}') — " +
                                           $"{(placeholder != null ? "assigned placeholder water material" : "no placeholder shader found, disabled renderer")}");
                    }
                }
            }

            LastResult = applied > 0
                ? $"{(_placeholderMaterial != null ? "applied" : "disabled")} {applied}x"
                : "none found";
            Plugin.Log.LogInfo($"[ArenaMapPort][Sea] pass complete — {applied} empty-material 'Water'-layer " +
                                "renderer(s) handled — daytime-and-visuals F6");
        }

        static bool TryGetPlaceholderMaterial(out Material material)
        {
            if (_placeholderMaterial != null)
            {
                material = _placeholderMaterial;
                return true;
            }

            var shader = Shader.Find(PlaceholderShaderName);
            if (shader == null)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][Sea] Shader.Find(\"{PlaceholderShaderName}\") returned " +
                                       "null — falling back to disabling the renderer instead");
                material = null;
                return false;
            }

            _placeholderMaterial = new Material(shader) { name = "ArenaMapPort_SeaPlaceholder" };
            material = _placeholderMaterial;
            return true;
        }
    }
}
