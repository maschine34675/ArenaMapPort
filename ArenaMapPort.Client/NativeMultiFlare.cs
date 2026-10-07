using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using MultiFlare;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Flare = MultiFlare.Flare;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Restores the original map-light sprites with EFT's native MultiFlare jobs, shaders and depth
    /// occlusion. Own storage/batches/camera buffers leave the global weapon-light manager untouched.
    /// No FlareLight/FlareSceneSettings/Scheduler components are created: their lifecycle methods
    /// register with or reset the global singleton. See docs/progress/native-multiflare.md.
    /// </summary>
    internal static class NativeMultiFlare
    {
        const string Tag = "[ArenaMapPort][MultiFlare]";
        static readonly Dictionary<string, ConfigEntry<bool>> Enabled = new Dictionary<string, ConfigEntry<bool>>();
        static readonly Dictionary<string, ConfigEntry<float>> Intensity = new Dictionary<string, ConfigEntry<float>>();
        static Runner _runner;

        internal static void Install()
        {
            foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5 })
            {
                Enabled[map.Key] = Plugin.Instance.Config.Bind("Lighting (" + map.Key + ")", "NativeMultiFlare", true,
                    "Restore original per-lamp flare sprites and lens effects with native depth occlusion. " +
                    "Separate from camera bloom/anamorphic streaks. Live toggle.");
                Intensity[map.Key] = Plugin.Instance.Config.Bind("Lighting (" + map.Key + ")", "NativeMultiFlareIntensity", .5f,
                    new ConfigDescription("Brightness of original lamp flares. 0 = invisible, 0.5 = 50%, 1 = original Arena brightness. " +
                        "Changes immediately; leaves lamp illumination, camera bloom and anamorphic streaks unchanged.",
                        new AcceptableValueRange<float>(0f, 1f)));
            }
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                var map = ArenaMapRaid.MapForScriptsScene(scene.name);
                if (map == null || !Enabled.ContainsKey(map.Key) || _runner != null) return;
                var go = new GameObject("ArenaMapPort MultiFlare");
                SceneManager.MoveGameObjectToScene(go, scene);
                _runner = go.AddComponent<Runner>();
                _runner.Map = map;
            };
            SceneManager.sceneUnloaded += scene =>
            {
                if (_runner != null && Array.IndexOf(_runner.Map.AllSceneNames, scene.name) >= 0)
                    Object.DestroyImmediate(_runner.gameObject);
            };
        }

        internal static void Probe() { if (_runner != null) _runner.Probe(); }

        sealed class Lamp
        {
            internal string Name;
            internal Transform Position, Original;
            internal float CullDistance, OriginalAlpha;
        }

        sealed class Runner : MonoBehaviour
        {
            internal ArenaMapDescriptor Map;
            LightStorage _storage;
            FlareRenderer _renderer;
            FlareSettings _settings;
            GameObject _proxies;
            Player _player;
            FlareBlindnessController _blindness;
            readonly List<Object> _assets = new List<Object>();
            readonly List<Lamp> _lamps = new List<Lamp>();
            readonly List<Camera> _cameras = new List<Camera>();
            bool _failed;
            int _frames;
            string _batchSummary = "pending";

            void Update()
            {
                try
                {
                    if (Map == null) return;
                    if (!EquatorProfiles.Value(Enabled[Map.Key])) { Stop(); _failed = false; return; }
                    if (_failed) return;
                    var manager = CameraManager.Exist ? CameraManager.Instance : null;
                    if (_renderer == null)
                    {
                        if (manager?.Camera == null || !Map.AllSceneNames.All(n => SceneManager.GetSceneByName(n).isLoaded)) return;
                        StartFlares();
                    }
                    ReconcileCameras(manager);
                    UpdateProtection(manager);
                    // Arena adds a cull distance to FlareLight. Keep its original cutoff even though
                    // EFT's older FlareLightData lacks that field. No angular/physics approximation:
                    // the original shader still handles visibility using the camera depth texture.
                    for (int i = 0; i < _lamps.Count; i++)
                    {
                        var lamp = _lamps[i];
                        var data = _storage._lights[i];
                        // Always derive from the source value: repeated live edits must not compound.
                        // Native blindness protection remains a separate multiplier on each batch.
                        data.Alpha = lamp.OriginalAlpha * EquatorProfiles.Value(Intensity[Map.Key]);
                        data.Enabled = (lamp.Original == null || lamp.Original.gameObject.activeInHierarchy) &&
                            _cameras.Any(c => (c.transform.position - lamp.Position.position).sqrMagnitude <= lamp.CullDistance * lamp.CullDistance);
                        _storage._lights[i] = data;
                    }
                    _renderer.StartRendering();
                }
                catch (Exception ex) { Fail(ex); }
            }

            void LateUpdate()
            {
                if (_renderer == null || !_renderer.IsRendering) return;
                try
                {
                    _renderer.CompleteRendering();
                    _renderer.RenderInstantBatches();
                    _frames++;
                    // F7 runs in Update, potentially while these native jobs are writing. Read
                    // their output only here, after every job has completed, and expose a cache.
                    _batchSummary = string.Join(",", _renderer.GetBatches().Cast<FlareBatch>().Select(b =>
                        b.Material.shader.name + ":" + b.Length + "/" + b._gpuData.Length));
                }
                catch (Exception ex) { Fail(ex); }
            }

            void Fail(Exception ex)
            {
                _failed = true;
                Stop();
                Plugin.Log.LogError($"{Tag} {Map?.Key} disabled after error (toggle off/on to retry): {ex}");
            }

            void StartFlares()
            {
                var dir = Path.Combine(Path.GetDirectoryName(typeof(NativeMultiFlare).Assembly.Location), "flares", Map.Key);
                var data = JObject.Parse(File.ReadAllText(Path.Combine(dir, "flares.json")));
                Require((int)data["schema_version"] == 1 && (string)data["map_key"] == Map.Key, "package identity");
                var sourceSettings = data["settings"];
                Require((bool)sourceSettings["enabled"] && (bool)sourceSettings["active_in_hierarchy"] &&
                    (float)sourceSettings["source_extra_scalar"] == 1f, "unsupported source settings");
                var atlas = ScriptableObject.CreateInstance<ProFlareAtlas>();
                _assets.Add(atlas);
                atlas.name = "ArenaMapPort " + Map.Key + " Flare Atlas";
                atlas._containers = ((JArray)data["atlas"]["containers"]).Select(c => new ProFlareAtlas.Container {
                    _name = (string)c["name"], _rect = new Rect(F(c["rect"][0]), F(c["rect"][1]), F(c["rect"][2]), F(c["rect"][3]))
                }).ToArray();
                Require(atlas._containers.Length > 0, "empty atlas");
                var textures = ((JArray)data["textures"]).ToDictionary(t => (string)t["id"], t => ReadTexture(dir, t));
                var materials = ((JArray)data["materials"]).ToDictionary(m => (string)m["id"], m => ReadMaterial(m, textures));
                _settings = new FlareSettings {
                    _atlas = atlas,
                    _normalBatch = ReadBatch<BatchSettings>(sourceSettings["normal_batch"], materials),
                    _shitBatch = ReadBatch<BatchSettings>(sourceSettings["noise_batch"], materials),
                    _flareOverlapSettings = ReadBatch<FlareOverlapSettings>(sourceSettings["overlap_batch"], materials)
                };
                var overlap = sourceSettings["overlap_batch"];
                _settings._flareOverlapSettings._maxNeighborCount = (int)overlap["max_neighbor_count"];
                _settings._flareOverlapSettings._searchRange = F(overlap["search_range"]);
                _settings._flareOverlapSettings._maxScaleMultiplier = F(overlap["max_scale_multiplier"]);
                Require(_settings.IsPopulated, "incomplete native settings");
                _storage = new LightStorage();
                _renderer = new FlareRenderer(_storage);
                _renderer.RegisterBatch(FlareType.Normal, new FlareBatch(atlas, _settings.NormalBatch, _storage));
                _renderer.RegisterBatch(FlareType.Noise, new FlareBatch(atlas, _settings.ShitBatch, _storage));
                _renderer.RegisterBatch(FlareType.FlashLight, new FlashLightBatch(atlas, _settings.FlareOverlapSettings, _storage));
                _proxies = new GameObject("Original flare positions");
                _proxies.transform.SetParent(transform, false);
                _proxies.SetActive(false); // transforms only; no native singleton lifecycle callbacks
                int sourceCount = 0, flareCount = 0, bound = 0;
                foreach (var light in (JArray)data["lights"])
                {
                    sourceCount++;
                    if (!(bool)light["enabled"] || !(bool)light["active_in_hierarchy"]) continue;
                    string scene = (string)light["scene"], path = (string)light["hierarchy_path"];
                    Require(Array.IndexOf(Map.AllSceneNames, scene) >= 0, "light outside map");
                    Transform parent = _proxies.transform;
                    foreach (var node in (JArray)light["transform_chain"])
                    {
                        var go = new GameObject("Flare position");
                        go.transform.SetParent(parent, false);
                        go.transform.localPosition = V3(node["position"]);
                        go.transform.localRotation = new Quaternion(F(node["rotation"][0]), F(node["rotation"][1]), F(node["rotation"][2]), F(node["rotation"][3]));
                        go.transform.localScale = V3(node["scale"]);
                        parent = go.transform;
                    }
                    var original = Resolve(scene, path);
                    if (original != null)
                    {
                        Require(Vector3.Distance(original.position, parent.position) < .02f, "source position mismatch: " + path);
                        bound++;
                    }
                    int index = _lamps.Count;
                    var lamp = new Lamp { Name = path, Position = parent, Original = original, CullDistance = F(light["cull_distance"]),
                        OriginalAlpha = F(light["total_alpha"]) };
                    Require(lamp.CullDistance > 0, "invalid cull distance");
                    _lamps.Add(lamp);
                    _storage._lights.Add(new FlareLightData { Alpha = lamp.OriginalAlpha * EquatorProfiles.Value(Intensity[Map.Key]),
                        Scale = F(light["total_scale"]), Enabled = true, WorldPos = parent.position });
                    _storage._transforms.Add(parent);
                    foreach (var item in (JArray)light["flares"])
                    {
                        var flare = ReadFlare(item);
                        Require(flare._texId >= 0 && flare._texId < atlas._containers.Length &&
                            (flare._flareType == FlareType.Normal || flare._flareType == FlareType.Noise), "unsupported flare type/atlas index");
                        _renderer._batches[flare.FlareType].Add(index, flare);
                        flareCount++;
                    }
                }
                Require(_lamps.Count > 0 && flareCount > 0, "no active flares");
                Plugin.Log.LogInfo($"{Tag} {Map.Key} enabled: sourceLights={sourceCount} activeLights={_lamps.Count} " +
                    $"flares={flareCount} boundOriginals={bound} nativeBatches=3 originalTextures={textures.Count} isolated=true global={GlobalState()}");
            }

            static Transform Resolve(string scene, string path)
            {
                var parts = path.Split('/');
                var roots = SceneManager.GetSceneByName(scene).GetRootGameObjects().Where(g => g.name == parts[0]).ToArray();
                if (roots.Length != 1) return null;
                Transform tr = roots[0].transform;
                for (int i = 1; i < parts.Length && tr != null; i++) tr = tr.Find(parts[i]);
                return tr;
            }

            void ReconcileCameras(CameraManager manager)
            {
                var desired = new List<Camera>();
                if (manager?.Camera != null) desired.Add(manager.Camera);
                var optic = manager?.OpticCameraManager;
                if (optic != null && optic.IsAnyOpticCameraRendering && optic.Camera != null && optic.Camera.isActiveAndEnabled && !desired.Contains(optic.Camera))
                    desired.Add(optic.Camera);
                foreach (var old in _cameras.Where(c => c == null || !desired.Contains(c)).ToArray())
                {
                    _renderer.RemoveCamera(old);
                    _cameras.Remove(old);
                }
                foreach (var camera in desired.Where(c => !_cameras.Contains(c)))
                {
                    var wrapper = new GameCameraRenderer(camera);
                    wrapper.CommandBuffer.name = "ArenaMapPort MultiFlare " + Map.Key;
                    _renderer.AddCamera(wrapper);
                    _cameras.Add(camera);
                    Plugin.Log.LogInfo($"{Tag} camera={camera.name} id={camera.GetInstanceID()} hdr={camera.allowHDR} ssaa={wrapper._ssaa != null}");
                }
            }

            void UpdateProtection(CameraManager manager)
            {
                _settings.IsNvEnabled = manager?.NightVision != null && manager.NightVision.On;
                var player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
                if (_player == player) return;
                ReleasePlayer();
                _player = player;
                _settings.SetBlindnessProtectionFactor(_player == null ? 0 : _player.GetBlindnessProtection());
                if (_player == null) return;
                _blindness = new FlareBlindnessController(this, _settings, _player.GetBlindnessProtection());
                _player.OnBlindnessProtectionChanged += OnProtection;
            }

            void OnProtection(float value)
            {
                if (Mathf.Approximately(value, 0)) _blindness?.Disable(); else _blindness?.Enable(value);
            }

            void ReleasePlayer()
            {
                if (!ReferenceEquals(_player, null)) _player.OnBlindnessProtectionChanged -= OnProtection;
                _blindness?.TryStopRoutine();
                _blindness = null;
                _player = null;
            }

            static T ReadBatch<T>(JToken data, Dictionary<string, Material> materials) where T : BatchSettings, new()
            {
                int capacity = (int)data["capacity"];
                Require(capacity > 0 && capacity <= 8192, "invalid batch capacity");
                return new T { _capacity = capacity, _material = materials[(string)data["material_id"]],
                    _blindProtectionAlphaFactor = F(data["blind_protection_alpha_factor"]),
                    _blindProtectionSizeFactor = F(data["blind_protection_size_factor"]) };
            }

            static Flare ReadFlare(JToken f)
            {
                var result = new Flare { _scale = V2(f["scale"]), _alpha = F(f["alpha"]), _color = C(f["color"]),
                    _flareType = (FlareType)(int)f["flare_type"], _minDist = F(f["min_dist"]), _maxDist = F(f["max_dist"]),
                    _minScale = F(f["min_scale"]), _maxScale = F(f["max_scale"]), _minAlpha = F(f["min_alpha"]),
                    _maxAlpha = F(f["max_alpha"]), _texId = (int)f["tex_id"] };
                Require(result._maxDist > result._minDist && result._minScale > 0 && result._minAlpha > 0, "invalid native flare division");
                return result;
            }

            Texture2D ReadTexture(string dir, JToken t)
            {
                string file = (string)t["file"];
                Require(file != null && file.StartsWith("textures/", StringComparison.Ordinal) && Path.GetFileName(file) == file.Substring(9), "texture filename");
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, file));
                using (var sha = SHA256.Create())
                    Require(string.Equals(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""), (string)t["sha256"], StringComparison.OrdinalIgnoreCase), "texture hash");
                int width = (int)t["width"], height = (int)t["height"], mips = (int)t["mip_count"];
                Require(width > 0 && width <= 8192 && height > 0 && height <= 8192 && mips > 0 && mips <= 14 &&
                    (int)t["format"] == 25 && (int)t["byte_count"] == bytes.Length, "unsupported texture layout");
                var texture = new Texture2D(width, height, TextureFormat.BC7, mips, (bool)t["linear"]);
                _assets.Add(texture);
                texture.name = "ArenaMapPort Flare " + (string)t["name"];
                texture.LoadRawTextureData(bytes);
                texture.Apply(false, true);
                texture.filterMode = (FilterMode)(int)t["filter_mode"];
                texture.wrapModeU = (TextureWrapMode)(int)t["wrap_u"];
                texture.wrapModeV = (TextureWrapMode)(int)t["wrap_v"];
                texture.wrapModeW = (TextureWrapMode)(int)t["wrap_w"];
                texture.anisoLevel = (int)t["aniso_level"];
                texture.mipMapBias = F(t["mip_map_bias"]);
                return texture;
            }

            Material ReadMaterial(JToken source, Dictionary<string, Texture2D> textures)
            {
                string name = (string)source["shader"];
                Require(name == "Custom/multiFlare" || name == "Custom/ShitOnScreen", "unsupported shader");
                var shader = Shader.Find(name);
                Require(shader != null && shader.isSupported, "native shader unavailable: " + name);
                var material = new Material(shader) { name = "ArenaMapPort " + (string)source["name"], renderQueue = (int)source["render_queue"] };
                _assets.Add(material);
                material.shaderKeywords = source["keywords"].Values<string>().ToArray();
                // Some native uniforms (notably _FadeDepthOffset) are deliberately absent from
                // ShaderLab Properties. HasProperty would discard their original saved values.
                foreach (var prop in ((JObject)source["floats"]).Properties()) material.SetFloat(prop.Name, F(prop.Value));
                foreach (var prop in ((JObject)source["colors"]).Properties()) material.SetColor(prop.Name, C(prop.Value));
                foreach (var tex in (JArray)source["textures"])
                {
                    string property = (string)tex["property"];
                    Require(material.HasProperty(property), "missing texture property " + property);
                    material.SetTexture(property, textures[(string)tex["texture_id"]]);
                    material.SetTextureScale(property, V2(tex["scale"]));
                    material.SetTextureOffset(property, V2(tex["offset"]));
                }
                return material;
            }

            internal void Probe()
            {
                if (_renderer == null) { Plugin.Log.LogInfo($"{Tag} {Map.Key} enabled=false failed={_failed} global={GlobalState()}"); return; }
                var camera = _cameras.FirstOrDefault();
                Plugin.Log.LogInfo($"{Tag} {Map.Key} lights={_lamps.Count} cameras={_cameras.Count} frames={_frames} " +
                    $"intensity={EquatorProfiles.Value(Intensity[Map.Key]):F3} batches={_batchSummary} " +
                    $"nv={_settings.NormalBatch.IsNvEnabled} global={GlobalState()}");
                foreach (var l in _lamps.OrderBy(l => camera == null ? 0 : (l.Position.position - camera.transform.position).sqrMagnitude).Take(6))
                    Plugin.Log.LogInfo($"{Tag} lamp={l.Name} pos={l.Position.position.ToString("F3")} distance={(camera == null ? -1 : Vector3.Distance(l.Position.position, camera.transform.position)):F2}");
            }

            static string GlobalState()
            {
                var m = FlareManager._instance;
                return m == null ? "absent" : $"initialized:{m.IsInitialized}/lights:{(m.IsInitialized ? m.LightStorage.Count : 0)}/renderer:{m._flareRenderer?.GetHashCode()}";
            }

            void Stop()
            {
                var renderer = _renderer;
                var storage = _storage;
                _renderer = null;
                _storage = null;
                var failures = new List<string>();
                void Release(Action action) { try { action(); } catch (Exception ex) { failures.Add(ex.Message); } }
                Release(ReleasePlayer);
                if (renderer != null)
                {
                    // Complete handles even when a partially scheduled frame did not reach
                    // IsRendering=true. Each resource releases independently if a native call fails.
                    foreach (var batch in renderer.GetBatches().Cast<FlareBatch>()) Release(() => batch._handle.Complete());
                    foreach (var camera in renderer._cameraRenderers.ToArray()) Release(camera.Dispose);
                    renderer._cameraRenderers.Clear();
                    foreach (var batch in renderer.GetBatches().Cast<FlareBatch>().ToArray()) Release(batch.Dispose);
                    renderer._batches.Clear();
                }
                if (storage != null) Release(storage.Dispose);
                _settings = null;
                _cameras.Clear();
                _lamps.Clear();
                if (_proxies != null) Release(() => Object.DestroyImmediate(_proxies));
                _proxies = null;
                for (int i = _assets.Count - 1; i >= 0; i--) if (_assets[i] != null) { var asset = _assets[i]; Release(() => Object.Destroy(asset)); }
                _assets.Clear();
                if (renderer != null) Plugin.Log.LogInfo($"{Tag} {Map.Key} released: ownedLights=0 ownedBuffers=0 cleanupFailures={failures.Count} global={GlobalState()} frames={_frames}");
                if (failures.Count > 0) Plugin.Log.LogWarning($"{Tag} cleanup: {string.Join("; ", failures)}");
                _frames = 0;
                _batchSummary = "pending";
            }

            void OnDestroy() { Stop(); if (_runner == this) _runner = null; }
        }

        static float F(JToken t) { float f = (float)t; Require(!float.IsNaN(f) && !float.IsInfinity(f), "nonfinite value"); return f; }
        static Vector2 V2(JToken t) => new Vector2(F(t[0]), F(t[1]));
        static Vector3 V3(JToken t) => new Vector3(F(t[0]), F(t[1]), F(t[2]));
        static Color C(JToken t) => new Color(F(t[0]), F(t[1]), F(t[2]), F(t[3]));
        static void Require(bool condition, string detail) { if (!condition) throw new InvalidDataException("MultiFlare: " + detail); }
    }
}
