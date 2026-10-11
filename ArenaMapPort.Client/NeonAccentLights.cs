using System;
using System.Collections.Generic;
using System.Linq;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Decorative chemlights borrowed from Equator's already loaded lighting scene. Only mesh and
    /// texture references are reused; owned materials/lights contain no loot or gameplay behavior.
    /// </summary>
    internal sealed class NeonAccentLights : IDisposable
    {
        const string Tag = "[ArenaMapPort][NightAccents]";
        const float Scale = 1.886f, FloorY = .03087f, DefaultStrength = 1.6f;
        static ConfigEntry<float> _strength;
        static readonly Color[] Colors = {
            new Color(.04f, .65f, 1f), new Color(1f, .025f, .3f),
            new Color(1f, .38f, .035f), new Color(.3f, 1f, .06f)
        };
        readonly GameObject _parent;
        GameObject _root;
        readonly List<Material> _materials = new List<Material>();
        readonly List<Light> _lights = new List<Light>();
        bool _failed, _disposed;
        float _lastStrength = -1;

        internal static void BindConfig(EventHandler changed)
        {
            const string section = "Night neon (arena_equator)";
            _strength = Plugin.Instance.Settings.Bind(section, "AccentLightIntensity", DefaultStrength,
                new ConfigDescription("Strength of nine additional colored chemlights and their small light pools. " +
                    "0 removes these accents. Native models, decorative only; independent of the large neon strips. Live.",
                    new AcceptableValueRange<float>(0f, 2f)));
            var revision = Plugin.Instance.Settings.Bind(section, "AccentDefaultsRevision", 0,
                new ConfigDescription("Internal once-only migration of the original chemlight strength to the brighter wall layout. " +
                    "Custom values and disabled accents are retained.", null, System.ComponentModel.BrowsableAttribute.No));
            if (revision.Value < 1)
            {
                if (_strength.Value == 1f) _strength.Value = DefaultStrength;
                revision.Value = 1;
            }
            _strength.SettingChanged += changed;
        }

        internal NeonAccentLights(GameObject parent) { _parent = parent; }

        internal void Update(Camera camera)
        {
            if (_disposed) return;
            float strength = _strength.Value;
            if (!EquatorNightNeon.IsActive || strength <= 0f || float.IsNaN(strength) || float.IsInfinity(strength))
            {
                Release();
                _failed = false;
                return;
            }
            if (_failed || camera == null) return;
            try
            {
                if (_root == null) Build();
                if (_lastStrength != strength)
                {
                    for (int i = 0; i < _materials.Count; i++)
                    {
                        Color emission = Colors[i] * (1.5f * strength);
                        emission.a = 1f;
                        _materials[i].SetColor("_EmissionColor", emission);
                    }
                    _lastStrength = strength;
                }
                foreach (var light in _lights)
                {
                    float distance = Vector3.Distance(camera.transform.position, light.transform.position);
                    light.intensity = strength * ArenaNeonPolicy.AccentDistanceFade(distance);
                    light.enabled = light.intensity > 0f;
                }
            }
            catch (Exception e)
            {
                Release();
                _failed = true;
                Plugin.Log.LogWarning($"{Tag} disabled; set AccentLightIntensity to 0 then back to retry: {e}");
            }
        }

        void Build()
        {
            var scene = SceneManager.GetSceneByName("Arena_Equator_Lighting");
            MeshFilter source = null;
            if (scene.IsValid() && scene.isLoaded)
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name == "SBG_Arena_Equator_Light")
                    {
                        source = root.transform.Find("OO/LIGHT/glowstick_yellow/chemlight_LOD0")?.GetComponent<MeshFilter>();
                        break;
                    }
            var sourceMaterial = source != null ? source.GetComponent<MeshRenderer>()?.sharedMaterial : null;
            if (source == null || source.sharedMesh == null || sourceMaterial == null || !sourceMaterial.HasProperty("_EmissionColor"))
                throw new InvalidOperationException("Loaded Equator chemlight mesh/emission material not found");
            var mesh = source.sharedMesh;
            foreach (var color in Colors)
            {
                var material = new Material(sourceMaterial) { name = "ArenaMapPort_Accent_" + _materials.Count };
                _materials.Add(material);
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            _root = new GameObject("ArenaMapPort_NightAccents");
            _root.transform.SetParent(_parent.transform, false);
            foreach (var fixture in ArenaMapRegistry.Equator.NightAccents)
            {
                var body = new GameObject("ArenaMapPort_Chemlight_" + fixture.Name);
                body.transform.SetParent(_root.transform, false);
                body.transform.position = new Vector3(fixture.X, FloorY + .004f - mesh.bounds.min.y * Scale, fixture.Z);
                body.transform.rotation = Quaternion.Euler(0, fixture.Yaw, 0);
                body.transform.localScale = Vector3.one * Scale;
                body.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = body.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _materials[(int)fixture.Color];
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                var lamp = new GameObject("ArenaMapPort_AccentLight_" + fixture.Name);
                lamp.transform.SetParent(_root.transform, false);
                lamp.transform.position = new Vector3(fixture.X, FloorY + .10f, fixture.Z);
                var light = lamp.AddComponent<Light>();
                _lights.Add(light);
                light.type = LightType.Point;
                light.color = Colors[(int)fixture.Color];
                light.range = 3f;
                light.bounceIntensity = 0f;
                light.renderMode = LightRenderMode.ForcePixel;
                light.bakingOutput = new LightBakingOutput { lightmapBakeType = LightmapBakeType.Realtime, isBaked = false };
                light.shadows = LightShadows.Soft;
                light.shadowCustomResolution = 256;
                light.shadowNearPlane = .025f;
                light.shadowBias = .02f;
                light.shadowNormalBias = .05f;
                light.intensity = 0f;
            }
            _lastStrength = -1;
            Plugin.Log.LogDebug($"{Tag} created {_lights.Count} chemlights/point lights, {_materials.Count} colors; " +
                $"mesh={mesh.name} vertices={mesh.vertexCount} source={sourceMaterial.name}; range=3m, shadows=256, no gameplay components");
        }

        internal void Probe() => Plugin.Log.LogDebug($"{Tag} strength={_strength.Value:F2} " +
            $"roots={(_root != null ? 1 : 0)} chemlights={_lights.Count} " +
            $"lit={_lights.Count(l => l != null && l.enabled)} colors={_materials.Count}");

        void Release()
        {
            bool hadResources = _root != null || _materials.Count != 0;
            if (_root != null) { _root.SetActive(false); Object.Destroy(_root); }
            foreach (var material in _materials) if (material != null) Object.Destroy(material);
            _root = null;
            _materials.Clear(); _lights.Clear();
            _lastStrength = -1;
            if (hadResources) Plugin.Log.LogDebug($"{Tag} removed owned accents; borrowed mesh/textures retained");
        }

        public void Dispose() { if (_disposed) return; _disposed = true; Release(); }
    }
}
