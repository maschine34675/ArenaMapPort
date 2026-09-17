using System;
using System.Collections;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// daytime-and-visuals, F4 — 2026-09-17 playtest symptom B: the ground/asphalt looked almost white
    /// with large dark/black blotches across the whole yard. Root cause confirmed directly in
    /// work/extract/bay5/materials/*.json: e.g. <c>0cad361327fe76c5.json</c>
    /// (<c>source_name: "Bay5_Decal_dirt_dark"</c>) — <c>shader: "Decal/Ultra Deferred Decal Of God
    /// 3000"</c>, <c>_SrcBlend=1 _DstBlend=0 _ZWrite=1</c> (fully OPAQUE, not blended — the deferred
    /// decal projector pass these shaders expect never runs in our scenes) with
    /// <c>_Color≈(0.40,0.40,0.40,0.62)</c> — drawn as a plain, opaque <c>MeshRenderer</c> quad this
    /// becomes a near-white/grey overwrite box sitting on top of the real ground, squarely matching the
    /// symptom. Mesh decal shaders ending in <c>_Decal</c> are deliberately NOT matched: they render
    /// correctly as ordinary renderers.
    ///
    /// Proper fix (authoring these as real <c>StaticDeferredDecal</c> projectors) needs a tools/
    /// change and invalidates the 12 maps' full-map-validation receipts — deferred, see
    /// docs/progress/daytime-and-visuals.md's Priority-2 section. This is the client-only stopgap:
    /// disable the Renderer outright once its (real, post-rebind) shader name matches one of these
    /// generic patterns — no ground texture where the decal would have projected, but no white/black
    /// overwrite blotch either.
    ///
    /// Runs one pass per raid, timed to fire AFTER <see cref="ShaderRebind"/>'s own 3s sweep
    /// (ShaderRebind.cs) has already turned each material's stand-in shader
    /// (<c>Hidden/ArenaMapPort/&lt;name&gt;</c>) back into its real name — checking shader names before
    /// that sweep completes would see only stand-ins and never match. Not Bay5-specific: any owned
    /// scene, any map, is scanned the same way.
    /// </summary>
    internal static class DecalRepairs
    {
        // ShaderRebind's own sweep fires at its SweepDelaySeconds (3f) after scene load; this pass runs
        // after that has had time to complete, with a margin for its own per-scene work.
        const float PassDelaySeconds = 4f;

        static ConfigEntry<bool> _enabled;
        static bool _installed;
        static GameObject _host;

        // Exposed for DaytimeVisualsProbe's consolidated F1-F6 summary line. -1 = pass hasn't run yet
        // this raid (still pending, or Enabled=false).
        internal static int LastDisabledCount { get; private set; } = -1;

        internal static bool Enabled => _enabled != null && _enabled.Value;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _enabled = Plugin.Instance.Config.Bind("Decals", "DisableUnsupportedDecalRenderers", true,
                "Disables any Renderer whose (real, post-ShaderRebind) shader is 'Decal/Water Deferred " +
                "Decal' or 'Decal/Ultra Deferred Decal Of God 3000' — these were authored " +
                "as deferred-decal projectors, which our scenes don't run, and render as an opaque near-" +
                "white/grey overwrite box instead. Cosmetic stopgap only; see this file's own doc comment.");

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

            LastDisabledCount = -1;
            _host = new GameObject("ArenaMapPort_DecalRepairSweep") { hideFlags = HideFlags.HideAndDontSave };
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
            LastDisabledCount = -1;
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
                Plugin.Log.LogInfo("[ArenaMapPort][Decals] DisableUnsupportedDecalRenderers=false — skipped");
                return;
            }

            int scanned = 0;
            int disabled = 0;
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
                        if (!renderer.enabled)
                        {
                            continue; // already off (another repair, or this pass on a re-entrant scene) — don't double-count
                        }
                        scanned++;
                        if (UsesUnsupportedDecalShader(renderer))
                        {
                            renderer.enabled = false;
                            disabled++;
                        }
                    }
                }
            }

            LastDisabledCount = disabled;
            Plugin.Log.LogInfo($"[ArenaMapPort][Decals] pass complete — {disabled}/{scanned} renderer(s) disabled " +
                                "(unsupported deferred-decal shader) — daytime-and-visuals F4");
        }

        static bool UsesUnsupportedDecalShader(Renderer renderer)
        {
            var mats = renderer.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var material = mats[m];
                if (material == null || material.shader == null)
                {
                    continue;
                }
                if (IsUnsupportedDecalShaderName(material.shader.name))
                {
                    return true;
                }
            }
            return false;
        }

        static bool IsUnsupportedDecalShaderName(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName))
            {
                return false;
            }
            // Only the projector-box shaders that need BSG's StaticDeferredDecal command-buffer pass.
            // Mesh decal shaders such as "p0/Reflective/Bumped Specular SMap_Decal" render correctly as
            // ordinary renderers and must stay visible.
            return shaderName == "Decal/Water Deferred Decal" ||
                   shaderName == "Decal/Ultra Deferred Decal Of God 3000";
        }
    }
}
