using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What vanilla does: every EFT location's Scripts scene carries a <c>TechnicalManagers</c> root whose
    /// <c>ScopeMaskRenderer</c> child holds the game's one <c>ScopeMaskRenderer</c> (decompile
    /// D:/SPT41/Development/_decompiled/ScopeMaskRenderer.cs; live on Factory 2026-10-16:
    /// <c>TechnicalManagers/ScopeMaskRenderer</c> next to OnRenderObjectManager, lightManager, WindowBreakerManager,
    /// WaterRenderer, ImpostorsRenderer and GrassTrampler). Every frame it draws the active optic's lens renderer
    /// (red) and the collimator meshes (blue, the active one green) into a camera-sized mask render texture in a
    /// BeforeGBuffer command buffer on the main camera and publishes it as the global shader texture
    /// <c>_ScopeMask</c>. The screen effects that must leave the sight picture alone, the aiming depth-of-field
    /// above all, read that mask.
    ///
    /// What the ported scene lacks: Arena's scenes ship no TechnicalManagers root and the extract holds no
    /// ScopeMaskRenderer, so <c>_ScopeMask</c> is never written on a ported map. The image inside every
    /// magnifying optic is then blurred like the periphery: ELCAN SpecterDR at 4x on AirPit, Laplacian variance of
    /// the scope interior 2.3 against 79.5 on Factory with the same scope and the same graphics settings (GPU-2,
    /// 2026-10-16, stills in docs/progress/scope-mask.md). Nothing else differed: the optic camera is EFT's own
    /// <c>BaseOpticCamera</c> prefab on both maps, its effect states and TAA settings were identical, the plugin
    /// touches no optic camera component, the scenes carry no post-process volumes, and the source textures are
    /// at most 2048 px, so the import limit cuts nothing.
    ///
    /// What the repair does: on the owned Scripts scene's sceneLoaded it creates "ArenaMapPort ScopeMaskRenderer"
    /// with EFT's own <c>ScopeMaskRenderer</c> component, unless one already exists. The component's Awake hooks
    /// <c>Camera.onPreCull</c> and resolves <c>Hidden/ScopeMask</c> and <c>CW FX/Collimator</c> through
    /// <c>ShadersFinder</c> (both are in its pre-registered list); its <c>CameraRender</c> only draws for
    /// <c>CameraManager.Instance.Camera</c>, so a cold CameraManager before the raid camera exists is harmless.
    /// The host object is destroyed on that Scripts scene's sceneUnloaded; the component's OnDestroy unhooks the
    /// callback and destroys the mask texture, exactly as vanilla's own teardown does. One scene scan per raid,
    /// never periodic.
    ///
    /// Host test (GPU-2, 2026-10-16 14:07-14:12, docs/progress/scope-mask.md): AirPit botless PMC raid, Mk 16 with the
    /// same ELCAN at 4x from the same spawn and view as the "before" still: sight picture sharp (rivets, panel edges,
    /// reticle ladder digits), periphery blurred as on vanilla, Laplacian variance of the scope interior 2.3 before /
    /// 709.0 after; log "added ScopeMaskRenderer for 'Arena_AirPit_Scripts'", one ScopeMaskRenderer instance in the raid,
    /// none after the disconnect ("removed ..."), no plugin warning, 120 fps with max frame 8.6 ms while aiming, hideout
    /// opened afterwards.
    /// </summary>
    internal static class ScopeMaskRepairs
    {
        const string Tag = "[ScopeMask]";

        static ConfigEntry<bool> _enabled;
        static GameObject _host;
        static bool _installed;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _enabled = Plugin.Instance.Settings.Bind("Camera", "EnableScopeMaskFix", true,
                "Adds EFT's own ScopeMaskRenderer (the TechnicalManagers object every vanilla map's Scripts scene " +
                "carries, missing from the ported scenes) so the aiming depth-of-field and the other screen " +
                "effects leave the image inside magnifying optics and collimators sharp. Next raid.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Stateless: the Scripts scene name alone decides, never mutable current-map bookkeeping
            // (ArenaMapRaid's own doc comment).
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null)
            {
                return;
            }
            if (_enabled == null || !_enabled.Value)
            {
                Plugin.Log.LogDebug($"{Tag} EnableScopeMaskFix is off; optics stay blurred under the aiming depth-of-field");
                return;
            }
            if (_host != null)
            {
                return; // already provided for this raid
            }

            try
            {
                // One scan at scene load (never periodic): a future package that authors the vanilla object itself,
                // or another mod providing it, must not end up with two mask writers fighting over _ScopeMask.
                if (UnityEngine.Object.FindObjectOfType<ScopeMaskRenderer>() != null)
                {
                    Plugin.Log.LogDebug($"{Tag} ScopeMaskRenderer already present in '{scene.name}'; no changes made");
                    return;
                }

                // ScopeMaskRenderer.Awake subscribes Camera.onPreCull before it builds its materials. An exception
                // inside Awake never reaches this method (Unity swallows it), but it would leave a subscribed callback
                // without materials, failing on every main-camera frame. So the two shaders it needs are resolved here
                // first; both are in ShadersFinder's pre-registered list and resolved on the host (2026-10-16).
                var maskShader = ShadersFinder.Find("Hidden/ScopeMask");
                var collimatorShader = ShadersFinder.Find("CW FX/Collimator");
                if (maskShader == null || collimatorShader == null)
                {
                    Plugin.Log.LogWarning($"{Tag} shader missing (Hidden/ScopeMask: {(maskShader != null ? "ok" : "none")}, " +
                                          $"CW FX/Collimator: {(collimatorShader != null ? "ok" : "none")}); ScopeMaskRenderer " +
                                          "not added, optics stay blurred under the aiming depth-of-field");
                    return;
                }

                _host = new GameObject("ArenaMapPort ScopeMaskRenderer");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<ScopeMaskRenderer>();
                Plugin.Log.LogDebug($"{Tag} added ScopeMaskRenderer for '{scene.name}' (vanilla: TechnicalManagers/" +
                                    "ScopeMaskRenderer); _ScopeMask is written again, so the sight picture stays sharp " +
                                    "under the aiming depth-of-field");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} could not add ScopeMaskRenderer for '{scene.name}': " +
                                      $"{e.GetType().Name}: {e.Message}; optics stay blurred under the aiming depth-of-field");
                if (_host != null)
                {
                    UnityEngine.Object.Destroy(_host);
                    _host = null;
                }
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null)
            {
                return;
            }
            if (_host != null)
            {
                // ScopeMaskRenderer.OnDestroy unhooks Camera.onPreCull and destroys its mask texture.
                UnityEngine.Object.Destroy(_host);
                _host = null;
                Plugin.Log.LogDebug($"{Tag} removed ScopeMaskRenderer with '{scene.name}'");
            }
        }
    }
}
