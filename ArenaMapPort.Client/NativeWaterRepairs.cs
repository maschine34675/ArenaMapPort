using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using WaterSSR;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Arena's newer WaterContainer is absent in EFT 0.16.9. Its serialized WaterSettings contract
    /// still matches native WaterForSSRv3. Restore the extracted settings and raw texture mip chains
    /// on the original mesh, replacing the unlit SimpleWater4 placeholder. The native deferred pass
    /// receives the world's lighting, reflections and weather. See docs/progress/bay5-water.md.
    /// </summary>
    internal static class NativeWaterRepairs
    {
        const string Tag = "[ArenaMapPort][Water]";
        static ConfigEntry<bool> _enabled;
        static Runner _runner;
        static readonly FieldInfo BuffersField = typeof(WaterRendererv3).GetField("commandBufferManager", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo SnowDisabledField = typeof(WaterRendererv3).GetField("bool_0", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo TargetsField = typeof(WaterRendererv3).GetField("renderTargetIdentifier_0", BindingFlags.Static | BindingFlags.NonPublic);

        internal static void Install()
        {
            _enabled = Plugin.Instance.Settings.Bind("Water (arena_bay5)", "NativeHarborWater", true,
                "Use original harbor waves, foam and depth settings with EFT's native water renderer. " +
                "Responds to world lighting at day and night. Live toggle; off restores the old placeholder.");
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name != ArenaMapRegistry.Bay5.ScriptsSceneName || _runner != null) return;
                var go = new GameObject("ArenaMapPort Native Water");
                SceneManager.MoveGameObjectToScene(go, scene);
                _runner = go.AddComponent<Runner>();
                _runner.StartCoroutine(_runner.Run());
            };
            SceneManager.sceneUnloaded += scene =>
            {
                if (_runner != null && Array.IndexOf(ArenaMapRegistry.Bay5.AllSceneNames, scene.name) >= 0)
                    UnityEngine.Object.DestroyImmediate(_runner.gameObject);
            };
        }

        internal static void Probe()
        {
            if (_runner != null) _runner.Probe();
        }

        sealed class Runner : MonoBehaviour
        {
            WaterRendererv3 _renderer;
            WaterForSSRv3 _water;
            GameObject _owner;
            MeshRenderer _target;
            Material[] _oldMaterials;
            bool _oldForceOff;
            bool _failed;
            bool _oldSnowDisabled;
            bool _ownsSnowMode;
            readonly List<Texture2D> _textures = new List<Texture2D>();
            readonly HashSet<CommandBuffer> _buffers = new HashSet<CommandBuffer>();

            internal IEnumerator Run()
            {
                yield return new WaitForSecondsRealtime(4f); // after placeholder and shader rebinding
                while (true)
                {
                    if (ArenaMapRegistry.Bay5.AllSceneNames.All(name => SceneManager.GetSceneByName(name).isLoaded))
                    {
                        if (!_enabled.Value && _owner != null) Stop();
                        if (_enabled.Value && _owner == null && !_failed)
                        {
                            try { StartWater(); }
                            catch (Exception ex)
                            {
                                _failed = true;
                                Stop();
                                Plugin.Log.LogError($"{Tag} initialization failed; preserving placeholder: {ex}");
                            }
                        }
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }

            void StartWater()
            {
                if (BuffersField == null || SnowDisabledField == null || TargetsField == null)
                    throw new MissingFieldException("Native water renderer contract changed");
                if (UnityEngine.Object.FindObjectOfType<WaterRendererv3>() != null)
                    throw new InvalidOperationException("Another native water renderer exists; refusing a second manager");
                var dir = Path.Combine(Path.GetDirectoryName(typeof(NativeWaterRepairs).Assembly.Location), "water", "arena_bay5");
                var data = JObject.Parse(File.ReadAllText(Path.Combine(dir, "water.json")));
                if ((int)data["schema_version"] != 1 || (string)data["map_key"] != ArenaMapRegistry.Bay5.Key)
                    throw new InvalidDataException("Unexpected water package identity");
                var target = data["target"];
                var sceneName = (string)target["scene"];
                if (Array.IndexOf(ArenaMapRegistry.Bay5.AllSceneNames, sceneName) < 0)
                    throw new InvalidDataException("Water target outside Bay5");
                var path = (string)target["hierarchy_path"];
                var scene = SceneManager.GetSceneByName(sceneName);
                var parts = path.Split('/');
                var root = scene.GetRootGameObjects().SingleOrDefault(go => go.name == parts[0]);
                var tr = root != null ? root.transform : null;
                for (int i = 1; i < parts.Length && tr != null; i++) tr = tr.Find(parts[i]);
                if (tr == null) throw new InvalidDataException("Water target missing: " + sceneName + "/" + path);
                var meshRenderer = tr.GetComponent<MeshRenderer>();
                var filter = tr.GetComponent<MeshFilter>();
                if (meshRenderer == null || filter == null || filter.sharedMesh == null || tr.gameObject.layer != LayerMask.NameToLayer("Water"))
                    throw new InvalidDataException("Invalid original water mesh target");
                var shader = Shader.Find("Hidden/WaterForSSR");
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("Native water shader unavailable");
                var settings = ReadSettings((JObject)data["settings"]);
                foreach (var prop in ((JObject)data["textures"]).Properties())
                {
                    var field = typeof(WaterSettings).GetField(prop.Name);
                    if (field == null || !typeof(Texture).IsAssignableFrom(field.FieldType))
                        throw new InvalidDataException("Unknown water texture field: " + prop.Name);
                    field.SetValue(settings, ReadTexture(dir, (JObject)prop.Value));
                }
                if (settings.Normals == null || settings.NormalsDetails == null || settings.Foam == null || settings.RippleTexture == null)
                    throw new InvalidDataException("Incomplete water textures");
                settings.Validate();

                _target = meshRenderer;
                _oldMaterials = _target.sharedMaterials;
                _oldForceOff = _target.forceRenderingOff;
                // Keep Renderer.enabled: WaterObject.Enabled checks it. Suppress only its automatic
                // draw; native water draws the Mesh explicitly into the deferred buffers.
                _target.forceRenderingOff = true;
                _owner = new GameObject("ArenaMapPort Water Renderer");
                _owner.SetActive(false);
                _owner.transform.SetParent(transform, false);
                _water = _owner.AddComponent<WaterForSSRv3>();
                _water._settings = settings;
                _water._targets = new[] { new WaterObject(filter, _target, tr.gameObject) };
                _renderer = _owner.AddComponent<WaterRendererv3>();
                _renderer._shader = shader;
                // This installed shader has four G-buffer outputs (no snow mask output). Bay5's
                // original summer water uses that path; restore the prior native state on teardown.
                _oldSnowDisabled = (bool)SnowDisabledField.GetValue(null);
                _ownsSnowMode = true;
                WaterRendererv3.DisableSnowMask(true);
                _owner.SetActive(true);
                Plugin.Log.LogInfo($"{Tag} enabled: target={sceneName}/{path} textures={_textures.Count} " +
                    $"shader={shader.name} renderer={_renderer.GetInstanceID()} camera={Camera.main?.actualRenderingPath}");
            }

            Texture2D ReadTexture(string dir, JObject info)
            {
                string file = (string)info["file"];
                if (string.IsNullOrEmpty(file) || !file.StartsWith("textures/", StringComparison.Ordinal) ||
                    Path.GetFileName(file) != file.Substring("textures/".Length))
                    throw new InvalidDataException("Invalid water texture filename");
                var bytes = File.ReadAllBytes(Path.Combine(dir, file));
                using (var hash = SHA256.Create())
                {
                    var actual = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
                    if (!string.Equals(actual, (string)info["sha256"], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Water texture hash mismatch: " + file);
                }
                int width = (int)info["width"], height = (int)info["height"], mips = (int)info["mip_count"];
                if (width < 1 || height < 1 || width > 8192 || height > 8192 || mips < 1 || mips > 14)
                    throw new InvalidDataException("Invalid water texture dimensions");
                var tex = new Texture2D(width, height, (TextureFormat)(int)info["format"], mips, (bool)info["linear"]);
                _textures.Add(tex); // retain for cleanup even if loading fails
                tex.name = "ArenaMapPort Water " + file;
                tex.LoadRawTextureData(bytes);
                tex.Apply(false, true); // preserve original normal channels and every authored mip
                tex.filterMode = (FilterMode)(int)info["filter_mode"];
                tex.wrapModeU = (TextureWrapMode)(int)info["wrap_u"];
                tex.wrapModeV = (TextureWrapMode)(int)info["wrap_v"];
                tex.wrapModeW = (TextureWrapMode)(int)info["wrap_w"];
                tex.anisoLevel = (int)info["aniso_level"];
                tex.mipMapBias = (float)info["mip_map_bias"];
                return tex;
            }

            static WaterSettings ReadSettings(JObject data)
            {
                var settings = new WaterSettings();
                foreach (var prop in data.Properties())
                {
                    var field = typeof(WaterSettings).GetField(prop.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (field == null) throw new InvalidDataException("Unknown water setting: " + prop.Name);
                    object value;
                    if (field.FieldType == typeof(float)) value = (float)prop.Value;
                    else if (field.FieldType == typeof(Color))
                    {
                        var a = (JArray)prop.Value;
                        value = new Color((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                    }
                    else if (field.FieldType == typeof(WaterSettings.TextureBlendSetting))
                    {
                        var a = prop.Value;
                        value = new WaterSettings.TextureBlendSetting {
                            Scale = (float)a["Scale"], Blend = (float)a["Blend"],
                            MovementDirection = new Vector2((float)a["MovementDirection"][0], (float)a["MovementDirection"][1])
                        };
                    }
                    else if (field.FieldType == typeof(AnimationCurve))
                    {
                        var keys = ((JArray)prop.Value["keys"]).Select(a => new Keyframe((float)a["time"], (float)a["value"],
                            (float)a["in_tangent"], (float)a["out_tangent"], (float)a["in_weight"], (float)a["out_weight"])
                            { weightedMode = (WeightedMode)(int)a["weighted_mode"] }).ToArray();
                        // Serialized curve infinity enum 2 is ClampForever, unlike runtime WrapMode 2 (Loop).
                        value = new AnimationCurve(keys) { preWrapMode = CurveWrap((int)prop.Value["pre_infinity"]),
                            postWrapMode = CurveWrap((int)prop.Value["post_infinity"]) };
                    }
                    else throw new InvalidDataException("Unexpected water setting type: " + field.FieldType);
                    field.SetValue(settings, value);
                }
                return settings;
            }

            static WrapMode CurveWrap(int value) => value == 2 ? WrapMode.ClampForever :
                value == 1 ? WrapMode.Loop : throw new InvalidDataException("Unsupported source curve infinity: " + value);

            void LateUpdate() { CaptureBuffers(); }
            void CaptureBuffers()
            {
                if (_renderer != null && BuffersField.GetValue(_renderer) is CommandBufferManager manager)
                    foreach (var buffer in manager.Cameras.Values) _buffers.Add(buffer);
            }

            internal void Probe()
            {
                CaptureBuffers();
                Plugin.Log.LogInfo($"{Tag} probe enabled={_renderer != null} buffers={_buffers.Count} textures={_textures.Count} " +
                    $"target={_target?.name} forceOff={_target?.forceRenderingOff} targets={_water?.Targets.Length} " +
                    $"drawReady={(_renderer != null && _renderer.method_4())} camera={Camera.main?.actualRenderingPath}");
            }

            void Stop()
            {
                CaptureBuffers();
                if (_renderer != null)
                {
                    _renderer.enabled = false; // detach native callbacks and command buffers first
                    SnowRenderer._instance?.TryUnregister(_renderer);
                }
                if (_owner != null) UnityEngine.Object.DestroyImmediate(_owner);
                _owner = null; _water = null; _renderer = null;
                if (_ownsSnowMode)
                {
                    WaterRendererv3.DisableSnowMask(_oldSnowDisabled);
                    TargetsField.SetValue(null, null); // no references to the previous raid's targets
                    _ownsSnowMode = false;
                }
                int buffers = _buffers.Count;
                foreach (var buffer in _buffers) buffer.Dispose();
                _buffers.Clear();
                if (_target != null)
                {
                    _target.sharedMaterials = _oldMaterials;
                    _target.forceRenderingOff = _oldForceOff;
                }
                _target = null; _oldMaterials = null;
                foreach (var texture in _textures) if (texture != null) UnityEngine.Object.Destroy(texture);
                _textures.Clear();
                Plugin.Log.LogInfo($"{Tag} stopped; released buffers={buffers}; restored original renderer");
            }

            void OnDisable() { Stop(); }
            void OnDestroy() { if (_runner == this) _runner = null; }
        }
    }
}
