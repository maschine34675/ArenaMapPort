using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Imported StaticDeferredDecal components need the native global renderer after shader rebinding.
    /// Its camera callback ignores enabled, and ReInitialize duplicates decal subscriptions. Own one
    /// fresh manager per raid/comparison instead, destroying it to remove callbacks and GPU buffers.
    /// Existing native managers and other maps are never taken over. See docs/progress/native-decals.md.
    /// </summary>
    internal static class NativeDecalRepairs
    {
        internal const string ShaderName = "Decal/DeferredDecalDiffuseNormalsDynamicEditor";
        static readonly Dictionary<string, ConfigEntry<bool>> Settings = new Dictionary<string, ConfigEntry<bool>>();
        static Runner _runner;
        // Native OnDestroy does not dispose these material/command-buffer wrappers. Read only
        // this manager's owned collections before destroying it; never touch source materials.
        static readonly FieldInfo GroupsField = typeof(StaticDeferredDecalRenderer).GetField("_decals", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo CamerasField = typeof(StaticDeferredDecalRenderer).GetField("_cameras", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void Install()
        {
            // Every released map that carries StaticDeferredDecal components (AirPit: 481, PLAN-AIRPIT item 19;
            // Bowl: 766, PLAN-BOWL item 8, 2026-10-18).
            foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5, ArenaMapRegistry.AirPit, ArenaMapRegistry.Bowl })
                Settings[map.Key] = Plugin.Instance.Settings.Bind($"Decals ({map.Key})", "Enabled", true,
                    "Render original Arena projection decals (graffiti, dirt, markings). Live comparison toggle; preserves source inactive objects.");
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null || !Settings.ContainsKey(map.Key) || _runner != null) return;
            var go = new GameObject("ArenaMapPort Native Decals");
            SceneManager.MoveGameObjectToScene(go, scene);
            _runner = go.AddComponent<Runner>();
            _runner.Map = map;
            _runner.StartCoroutine(_runner.Run());
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (_runner != null && Array.IndexOf(_runner.Map.AllSceneNames, scene.name) >= 0)
                UnityEngine.Object.DestroyImmediate(_runner.gameObject);
        }

        sealed class Runner : MonoBehaviour
        {
            internal ArenaMapDescriptor Map;
            StaticDeferredDecalRenderer _owned;
            bool _failed;
            bool _ready;
            readonly HashSet<Material> _materials = new HashSet<Material>();
            readonly HashSet<CommandBuffer> _buffers = new HashSet<CommandBuffer>();

            internal IEnumerator Run()
            {
                yield return new WaitForSecondsRealtime(4f); // ShaderRebind's delayed sweep is at 3 seconds.
                while (true)
                {
                    if (!_ready)
                    {
                        _ready = true;
                        foreach (var name in Map.AllSceneNames)
                        {
                            var scene = SceneManager.GetSceneByName(name);
                            if (!scene.IsValid() || !scene.isLoaded) { _ready = false; break; }
                        }
                    }
                    if (_ready)
                    {
                        bool enabled = Settings[Map.Key].Value;
                        if (_owned != null) CaptureResources();
                        if (!enabled && _owned != null) StopRenderer();
                        if (enabled && _owned == null && !_failed)
                        {
                            try { StartRenderer(); }
                            catch (Exception ex)
                            {
                                _failed = true;
                                StopRenderer();
                                Plugin.Log.LogError($"[ArenaMapPort][NativeDecals] map={Map.Key} initialization failed: {ex}");
                            }
                        }
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }

            void StartRenderer()
            {
                if (GroupsField == null || CamerasField == null)
                    throw new MissingFieldException("Native decal cleanup fields changed");
                if (StaticDeferredDecalRenderer.Instance != null ||
                    UnityEngine.Object.FindObjectOfType<StaticDeferredDecalRenderer>() != null)
                    throw new InvalidOperationException("Another native decal renderer already exists; refusing a second manager");
                int count = 0, active = 0;
                var materials = new HashSet<Material>();
                foreach (var name in Map.AllSceneNames)
                {
                    var scene = SceneManager.GetSceneByName(name);
                    foreach (var root in scene.GetRootGameObjects())
                    foreach (var decal in root.GetComponentsInChildren<StaticDeferredDecal>(true))
                    {
                        count++;
                        if (!decal.enabled || !decal.gameObject.activeInHierarchy) continue;
                        var mat = decal._material;
                        if (mat == null || mat.shader == null || mat.shader.name != ShaderName ||
                            !(mat.GetTexture("_MainTex") is Texture2D))
                            throw new InvalidOperationException("Unresolved original material on " + name + "/" + decal.name);
                        active++;
                        materials.Add(mat);
                    }
                }
                if (count == 0)
                {
                    _failed = true;
                    Plugin.Log.LogWarning($"[ArenaMapPort][NativeDecals] {Map.LocaleName}: decal data is missing; reinstall the complete map package.");
                    return;
                }
                // Arena's normal-map importer keeps readable bytes for bundle serialization.
                // The native decal atlas uses regional Graphics.CopyTexture for its shorter mip
                // chain. Compressed CPU regions cannot be copied, even though the GPU copy works.
                // Discard only our decal sources' unused CPU copies before native Awake builds the
                // atlases; keep uploaded pixels/mips intact, including authored DXT5 normal maps.
                var prepared = new HashSet<Texture2D>();
                foreach (var material in materials)
                {
                    if (!material.HasProperty("_BumpMap") || !(material.GetTexture("_BumpMap") is Texture2D texture)
                        || !texture.isReadable || !GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat)
                        || !prepared.Add(texture)) continue;
                    texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                }
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeDecals] map={Map.Key} released {prepared.Count} compressed source CPU copies before atlas creation");
                var owner = new GameObject("ArenaMapPort StaticDeferredDecalRenderer");
                owner.SetActive(false);
                owner.transform.SetParent(transform, false);
                try
                {
                    _owned = owner.AddComponent<StaticDeferredDecalRenderer>();
                    // The bundle's imported textures do not use streaming mipmaps. Avoid the
                    // native texture-streaming QuadTree and its unmanaged, unowned node clones.
                    // Keep the native direct instanced draw path; set before Awake caches modes.
                    _owned.UseQuadTreeRenderInPlayMode = false;
                    _owned.UseTexturesQuadTreeInPlayMode = false;
                    owner.AddComponent<BeforeUnload>().Owner = this;
                    owner.SetActive(true);
                    _owned.UpdateInstancesBuffers();
                    CaptureResources();
                    var draws = _owned.GetDecalDrawInstancesArray();
                    int registered = 0;
                    foreach (var draw in draws) registered += draw.Count;
                    if (registered != active)
                        throw new InvalidOperationException($"Native decal census {registered} differs from owned active set {active}");
                    Plugin.Log.LogDebug($"[ArenaMapPort][NativeDecals] map={Map.Key} enabled: source={count} active={active} " +
                        $"registered={registered} materials={materials.Count} groups={draws.Length} " +
                        $"nativeEnabled={EFTHardSettings.Instance.STATIC_DEFERRED_DECALS_ENABLED} " +
                        $"camera={Camera.main?.actualRenderingPath} renderer={_owned.GetInstanceID()}");
                }
                catch
                {
                    // AddComponent may throw after attaching the component; retain it for full cleanup.
                    if (_owned == null) _owned = owner.GetComponent<StaticDeferredDecalRenderer>();
                    if (_owned == null) UnityEngine.Object.DestroyImmediate(owner);
                    throw;
                }
            }

            internal void CaptureResources()
            {
                if (_owned == null) return;
                var groups = (DictionaryListHydra<string, StaticDeferredDecalRenderer.DecalsData>)GroupsField.GetValue(_owned);
                for (int i = 0; i < groups.Count; i++) _materials.Add(groups.GetByIndex(i).OwnMaterial);
                foreach (var draw in _owned.GetDecalDrawInstancesArray())
                    if (draw.CommandBuffer != null) _buffers.Add(draw.CommandBuffer);
                var cameras = (Dictionary<Camera, StaticDeferredDecalRenderer.CameraDecalsCommandBuffers>)CamerasField.GetValue(_owned);
                foreach (var entry in cameras.Values)
                {
                    if (entry.BeginCB != null) _buffers.Add(entry.BeginCB);
                    if (entry.EndCB != null) _buffers.Add(entry.EndCB);
                }
            }

            void StopRenderer()
            {
                CaptureResources();
                if (_owned != null)
                {
                    if (StaticDeferredDecalRenderer.Instance == _owned) StaticDeferredDecalRenderer.Instance = null;
                    UnityEngine.Object.DestroyImmediate(_owned.gameObject);
                }
                _owned = null;
                foreach (var material in _materials)
                    if (material != null) UnityEngine.Object.DestroyImmediate(material);
                foreach (var buffer in _buffers) buffer.Dispose();
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeDecals] map={Map.Key} renderer removed; " +
                    $"released {_materials.Count} material(s), {_buffers.Count} command buffer(s) and native callbacks");
                _materials.Clear();
                _buffers.Clear();
            }

            void OnDestroy()
            {
                StopRenderer();
                if (_runner == this) _runner = null;
            }
        }

        sealed class BeforeUnload : MonoBehaviour
        {
            internal Runner Owner;
            void OnDisable()
            {
                // Unity calls OnDisable before native OnDestroy clears its collections.
                if (Owner != null) Owner.CaptureResources();
            }
        }
    }
}
