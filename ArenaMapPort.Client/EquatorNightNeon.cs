using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Optional Equator midnight installation: owned emissive strips plus real shadowed lights.
    /// Created after original scene-light capture, so day tuning and lamp baselines remain intact.
    /// Saved lighting/PostFX settings, physics and navigation are unchanged; optional camera PostFX
    /// are restored when this installation stops.
    /// Host evidence and authored mount references: docs/progress/equator-night-lights.md.
    /// </summary>
    internal static class EquatorNightNeon
    {
        const string Tag = "[ArenaMapPort][NightNeon]";
        static ConfigEntry<bool> _enabled, _rain;
        static ConfigEntry<float> _lightIntensity, _glowIntensity;
        static ConfigEntry<Color> _cyan, _magenta;
        static Runner _runner;
        static int _pendingRefresh = 1;

        internal static bool IsActive => !EquatorProfiles.NaturalNight && ArenaNeonPolicy.IsActive(_enabled?.Value == true,
            ArenaMapRaid.CurrentMap?.Key, TimeOfDayRepairs.ActiveFixedHour);
        internal static bool RainForCurrentRaid => RainForSelection(
            ArenaMapRaid.CurrentMap?.Key, TimeOfDayRepairs.ActiveFixedHour);
        internal static bool RainForSelection(string mapKey, int? hour) => ArenaNeonPolicy.RequestsRain(
            _enabled?.Value == true, _rain?.Value == true, mapKey, hour);

        internal static void Install(Harmony harmony)
        {
            const string section = "Night neon (arena_equator)";
            var config = Plugin.Instance.Settings;
            _enabled = config.Bind(section, "Enabled", EquatorProfiles.NightDefault("Enabled", true),
                "Experimental cyan/magenta light installation for Equator's 00:00 choice. " +
                "12:00 retains the existing lighting. Live; does not change saved image or PostFX settings.");
            _lightIntensity = config.Bind(section, "LightIntensity", EquatorProfiles.NightDefault("LightIntensity", 1.5f),
                new ConfigDescription("Strength of the added lights on floors, walls and characters. " +
                    "Independent of the original map lamps. Live.", new AcceptableValueRange<float>(0f, 3f)));
            _glowIntensity = config.Bind(section, "StripBrightness", EquatorProfiles.NightDefault("StripBrightness", .6f),
                new ConfigDescription("Brightness of the visible neon strips. Separate from their light on nearby surfaces. Live.",
                    new AcceptableValueRange<float>(0f, 3f)));
            _cyan = config.Bind(section, "CyanColor", EquatorProfiles.NightDefault("CyanColor", new Color(0.02f, .7f, 1f, 1f)),
                "Color of the column and side-lane lights/strips. RGB only; alpha is ignored. Live.");
            _magenta = config.Bind(section, "MagentaColor", EquatorProfiles.NightDefault("MagentaColor", new Color(1f, .025f, .3f, 1f)),
                "Color of the Equator sign and TECHLIGHT lights/strips. RGB only; alpha is ignored. Live.");
            _rain = config.Bind(section, "Rain", EquatorProfiles.NightDefault("Rain", true),
                "Use rain for the enabled Equator 00:00 neon variant, overriding Weather (test) only there. " +
                "Equator day always uses Clear; Bay5 always uses dynamic backend weather. Next raid.");
            _runner = Plugin.Instance.gameObject.AddComponent<Runner>();
            _enabled.SettingChanged += MarkDirty;
            _lightIntensity.SettingChanged += MarkDirty;
            _glowIntensity.SettingChanged += MarkDirty;
            _cyan.SettingChanged += MarkDirty;
            _magenta.SettingChanged += MarkDirty;
            NeonNightAtmosphere.BindConfig(MarkDirty);
            NeonLightFlicker.BindConfig(MarkDirty);
            NeonNightPostFx.BindConfig(MarkDirty);
            NeonDistanceHaze.Install(harmony, MarkDirty);
            NeonAccentLights.BindConfig(MarkDirty);
            NeonWetFloor.BindConfig(MarkDirty);
            SceneManager.sceneUnloaded += scene =>
            {
                if (_runner != null && Array.IndexOf(ArenaMapRegistry.Equator.AllSceneNames, scene.name) >= 0)
                    _runner.Stop("scene unload");
            };
        }

        internal static void Probe()
        {
            if (!DeveloperOptions.Enabled) return;
            if (_runner == null) return;
            _runner.Probe();
            _runner.SampleFlicker();
        }
        static void MarkDirty(object sender, EventArgs args) => Interlocked.Exchange(ref _pendingRefresh, 1);

        sealed class Runner : MonoBehaviour
        {
            internal bool Dirty = true;
            GameObject _root;
            Mesh _mesh;
            Material _cyanMaterial, _magentaMaterial;
            NeonNightAtmosphere _atmosphere;
            NeonLightFlicker _flicker;
            NeonNightPostFx _postFx;
            NeonDistanceHaze _distanceHaze;
            NeonAccentLights _accents;
            readonly List<Light> _lights = new List<Light>();
            readonly List<MeshRenderer> _strips = new List<MeshRenderer>();
            float[] _baseIntensities;
            Coroutine _flickerSample;
            bool _failed;
            float _nextUpdate;
            string _lastState;

            void LateUpdate()
            {
                // File-watcher callbacks may be off-thread: only consume their signal here.
                if (Interlocked.Exchange(ref _pendingRefresh, 0) != 0) Dirty = true;
                bool refresh = Dirty || Time.unscaledTime >= _nextUpdate;
                try
                {
                    // Only the short fault envelope and native render gate need every frame.
                    // Scene checks, distance baselines and atmosphere upkeep remain at 10 Hz.
                    if (!refresh)
                    {
                        _flicker?.Update();
                        _atmosphere?.SyncLightVisibility();
                        return;
                    }
                    _nextUpdate = Time.unscaledTime + .1f;
                    if (!IsActive)
                    {
                        Stop("inactive");
                        _failed = false;
                        if (ArenaMapRaid.CurrentMap == ArenaMapRegistry.Equator)
                            LogState($"inactive enabled={_enabled.Value} hour={TimeOfDayRepairs.ActiveFixedHour?.ToString() ?? "unknown"}");
                        Dirty = false;
                        return;
                    }
                    if (_failed) { NeonWetFloor.Update(false); return; }
                    if (!Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.MainPlayer == null) return;
                    var camera = CameraManager.Exist ? CameraManager.Instance.Camera : null;
                    if (camera == null) return;
                    if (_root == null)
                    {
                        // All source-light capture and shader rebinding callbacks have completed.
                        if (!ArenaMapRegistry.Equator.AllSceneNames.All(n => SceneManager.GetSceneByName(n).isLoaded)) return;
                        Build();
                        Dirty = true;
                    }
                    if (Dirty)
                    {
                        SetEmission(_cyanMaterial, _cyan.Value);
                        SetEmission(_magentaMaterial, _magenta.Value);
                    }
                    var fixtures = ArenaMapRegistry.Equator.NeonFixtures;
                    for (int i = 0; i < _lights.Count; i++)
                    {
                        var light = _lights[i];
                        float fade = ArenaNeonPolicy.DistanceFade(Vector3.Distance(camera.transform.position, light.transform.position));
                        light.color = Tint(fixtures[i].Magenta ? _magenta.Value : _cyan.Value);
                        _baseIntensities[i] = fixtures[i].Intensity * _lightIntensity.Value * fade;
                        light.intensity = _baseIntensities[i];
                        light.enabled = light.intensity > 0f;
                    }
                    _flicker.Update();
                    _atmosphere.Update(camera);
                    _postFx.Update(camera);
                    _distanceHaze.Update(camera);
                    _accents.Update(camera);
                    NeonWetFloor.Update(true);
                    _atmosphere.SyncLightVisibility();
                    if (Dirty && DeveloperOptions.Enabled) Probe();
                    Dirty = false;
                }
                catch (Exception e)
                {
                    Stop("error");
                    _failed = true;
                    Dirty = false;
                    Plugin.Log.LogError($"{Tag} disabled after error (toggle off/on to retry): {e}");
                }
            }

            void Build()
            {
                var shader = Shader.Find("Standard");
                if (shader == null || !shader.isSupported)
                    throw new InvalidOperationException("Equator's native Standard emissive shader is unavailable");
                _mesh = BoxMesh();
                _cyanMaterial = NewMaterial(shader, "Cyan");
                _magentaMaterial = NewMaterial(shader, "Magenta");
                _root = new GameObject("ArenaMapPort_NightNeon");
                SceneManager.MoveGameObjectToScene(_root, SceneManager.GetSceneByName(ArenaMapRegistry.Equator.ScriptsSceneName));
                foreach (var fixture in ArenaMapRegistry.Equator.NeonFixtures)
                {
                    var position = new Vector3(fixture.X, fixture.Bottom + fixture.Height * .5f, fixture.Z);
                    var normal = new Vector3(fixture.NormalX, 0, fixture.NormalZ);
                    var strip = new GameObject(fixture.Name + "_strip");
                    strip.transform.SetParent(_root.transform, false);
                    strip.transform.position = position;
                    strip.transform.localScale = new Vector3(.055f, fixture.Height, .055f);
                    strip.AddComponent<MeshFilter>().sharedMesh = _mesh;
                    var renderer = strip.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = fixture.Magenta ? _magentaMaterial : _cyanMaterial;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    _strips.Add(renderer);

                    var source = new GameObject(fixture.Name + "_light");
                    source.transform.SetParent(_root.transform, false);
                    source.transform.position = position + normal * .3f;
                    source.transform.rotation = Quaternion.LookRotation(normal + Vector3.down * .6f);
                    var light = source.AddComponent<Light>();
                    light.type = LightType.Spot;
                    light.bakingOutput = new LightBakingOutput { lightmapBakeType = LightmapBakeType.Realtime, isBaked = false };
                    light.renderMode = LightRenderMode.ForcePixel;
                    light.spotAngle = 130f;
                    light.range = fixture.Range;
                    light.bounceIntensity = 0f;
                    light.shadows = LightShadows.Soft;
                    light.shadowCustomResolution = 512;
                    light.shadowNearPlane = .15f;
                    light.shadowBias = .04f;
                    light.shadowNormalBias = .1f;
                    light.intensity = 0f;
                    _lights.Add(light);
                }
                _baseIntensities = new float[_lights.Count];
                _flicker = new NeonLightFlicker(_lights, _strips, _baseIntensities);
                _atmosphere = new NeonNightAtmosphere(_root, _lights);
                _postFx = new NeonNightPostFx();
                _distanceHaze = new NeonDistanceHaze();
                _accents = new NeonAccentLights(_root);
                LogState($"created strips={_lights.Count} lights={_lights.Count} shadow=Spot/512 " +
                    "hour=0; saved lighting/PostFX settings retained");
            }

            static Material NewMaterial(Shader shader, string suffix)
            {
                var material = new Material(shader) { name = "ArenaMapPort_Neon_" + suffix };
                material.SetColor("_Color", new Color(.015f, .015f, .015f, 1f));
                material.SetFloat("_Glossiness", .25f);
                material.SetFloat("_Metallic", 0f);
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                return material;
            }

            static Color Tint(Color value) => new Color(Mathf.Clamp01(value.r), Mathf.Clamp01(value.g), Mathf.Clamp01(value.b), 1f);
            static void SetEmission(Material material, Color tint)
            {
                var color = Tint(tint) * (2.5f * _glowIntensity.Value);
                color.a = 1f;
                material.SetColor("_EmissionColor", color);
            }

            static Mesh BoxMesh()
            {
                var mesh = new Mesh { name = "ArenaMapPort_NeonStrip" };
                mesh.vertices = new[] {
                    new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f),
                    new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                    new Vector3(-.5f,-.5f,.5f), new Vector3(.5f,-.5f,.5f),
                    new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f) };
                mesh.triangles = new[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,4,7,0,7,3,
                    1,2,6,1,6,5,0,1,5,0,5,4,3,7,6,3,6,2 };
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }

            internal void Probe()
            {
                Plugin.Log.LogDebug($"{Tag} enabled={_enabled.Value} active={IsActive} " +
                    $"map={ArenaMapRaid.CurrentMap?.Key ?? "none"} hour={TimeOfDayRepairs.ActiveFixedHour?.ToString() ?? "unknown"} " +
                    $"roots={(_root != null ? 1 : 0)} strips={_lights.Count} lights={_lights.Count} " +
                    $"lit={_lights.Count(l => l != null && l.enabled)} scale={_lightIntensity.Value:F2} glow={_glowIntensity.Value:F2} " +
                    $"cyan={ColorUtility.ToHtmlStringRGB(Tint(_cyan.Value))} magenta={ColorUtility.ToHtmlStringRGB(Tint(_magenta.Value))}");
                _atmosphere?.Probe();
                _flicker?.Probe();
                _postFx?.Probe();
                _distanceHaze?.Probe();
                _accents?.Probe();
                NeonWetFloor.Probe();
            }

            internal void SampleFlicker()
            {
                if (_flicker != null && !_flicker.Sampling) _flickerSample = StartCoroutine(_flicker.Sample());
            }

            void LogState(string state)
            {
                if (state == _lastState) return;
                _lastState = state;
                Plugin.Log.LogDebug($"{Tag} {state}");
            }

            internal void Stop(string reason)
            {
                bool hadRoot = _root != null || _mesh != null;
                NeonWetFloor.Update(false);
                _accents?.Dispose();
                _accents = null;
                if (_flickerSample != null) StopCoroutine(_flickerSample);
                _flickerSample = null;
                _postFx?.Dispose();
                _postFx = null;
                _distanceHaze?.Dispose();
                _distanceHaze = null;
                _flicker?.Dispose();
                _flicker = null;
                _atmosphere?.Dispose();
                _atmosphere = null;
                if (_root != null) { _root.SetActive(false); Object.Destroy(_root); }
                if (_mesh != null) Object.Destroy(_mesh);
                if (_cyanMaterial != null) Object.Destroy(_cyanMaterial);
                if (_magentaMaterial != null) Object.Destroy(_magentaMaterial);
                _root = null; _mesh = null; _cyanMaterial = _magentaMaterial = null;
                _lights.Clear();
                _strips.Clear();
                _baseIntensities = null;
                if (hadRoot) LogState($"removed ({reason}); owned lights/materials/mesh released");
            }

            void OnDestroy() => Stop("plugin destroyed");
        }
    }
}
