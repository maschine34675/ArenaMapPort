using System;
using System.Collections;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Optional diagnostic: hides renderers drawn purely with BSG's deferred-decal projector shaders
    /// ("Decal/Water Deferred Decal", "Decal/Ultra Deferred Decal Of God 3000"). These mesh-renderer
    /// decals remain separate from the StaticDeferredDecal components restored by NativeDecalRepairs.
    ///
    /// History: shipped enabled on 2026-09-17 as the suspected cause of Bay5's white ground with dark
    /// blotches. The playtest refuted that (blotches unchanged) and exposed a bug: any renderer with a
    /// decal material SLOT was hidden, which removed Bay5's cargo containers. Now off by default and
    /// limited to renderers whose every slot is a projector decal. Runs after ShaderRebind's sweep so
    /// real shader names are visible.
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

            // New key (was "DisableUnsupportedDecalRenderers", default true): the 2026-09-17 14:54 playtest
            // showed the blotches are NOT caused by these decals, and hiding renderers that merely carry a
            // decal material slot removed Bay5's cargo containers. Off by default; a renamed key so the
            // previously saved "true" does not carry over.
            _enabled = Plugin.Instance.Config.Bind("Decals", "HideProjectorDecals", false,
                "Hides renderers whose materials are ALL deferred-decal projector shaders ('Decal/Water " +
                "Deferred Decal', 'Decal/Ultra Deferred Decal Of God 3000'). Diagnostic option, off by default.");

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
                return; // off by default
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

        // True only when EVERY material slot is a projector-decal shader: multi-material meshes (e.g. the
        // cargo containers, which carry a decal slot next to their body material) must stay visible.
        static bool UsesUnsupportedDecalShader(Renderer renderer)
        {
            var mats = renderer.sharedMaterials;
            int decalSlots = 0;
            for (int m = 0; m < mats.Length; m++)
            {
                var material = mats[m];
                if (material == null || material.shader == null ||
                    !IsUnsupportedDecalShaderName(material.shader.name))
                {
                    return false;
                }
                decalSlots++;
            }
            return decalSlots > 0;
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
