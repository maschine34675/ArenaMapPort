using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Settings;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Owned atmosphere for EquatorNightNeon's existing midnight-only root. Native volumetric
    /// spotlights provide colored haze; independent shallow particle banks use a cloned EFT
    /// smoke material. No global fog, grenade behavior, original materials or graphics settings
    /// are changed. Local API/lifecycle evidence: docs/progress/equator-night-atmosphere.md.
    /// </summary>
    internal sealed class NeonNightAtmosphere : IDisposable
    {
        const string Tag = "[ArenaMapPort][NightAtmosphere]";
        static ConfigEntry<float> _haze, _smoke;
        readonly GameObject _root;
        readonly IList<Light> _lights;
        readonly List<OwnedVolume> _volumes = new List<OwnedVolume>();
        readonly List<ParticleSystem> _particles = new List<ParticleSystem>();
        Material _smokeMaterial;
        Task _load;
        bool _disposed, _volumeFailed, _smokeFailed;
        float _lastHaze = -1, _lastSmoke = -1;

        internal static void BindConfig(EventHandler changed)
        {
            const string section = "Night neon (arena_equator)";
            _haze = Plugin.Instance.Settings.Bind(section, "HazeStrength", EquatorProfiles.NightDefault("HazeStrength", .16f),
                new ConfigDescription("Visible colored haze in the added neon light cones. 0 = off. " +
                    "Requires the game's Volumetric Light graphics option; does not change that option. Live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            _smoke = Plugin.Instance.Settings.Bind(section, "SmokeDensity", EquatorProfiles.NightDefault("SmokeDensity", .65f),
                new ConfigDescription("Opacity of slow, shallow smoke banks by the neon columns and TECHLIGHT. " +
                    "0 = off. Decorative particles, without grenade or AI smoke behavior. Live.",
                    new AcceptableValueRange<float>(0f, 2f)));
            _haze.SettingChanged += changed;
            _smoke.SettingChanged += changed;
        }

        internal NeonNightAtmosphere(GameObject root, IList<Light> lights)
        {
            _root = root;
            _lights = lights;
        }

        internal void Update(Camera camera)
        {
            if (_disposed || _root == null) return;
            UpdateVolumes(camera);
            UpdateSmoke();
        }

        // Native CheckIntensity caches NeedToPrerender. Refresh after each flicker update so a
        // zero-to-positive transition cannot leave haze behind until the next 10 Hz upkeep tick.
        internal void SyncLightVisibility()
        {
            if (_disposed) return;
            try
            {
                for (int i = 0; i < _volumes.Count; i++)
                    if (ArenaMapRegistry.Equator.NeonFixtures[i].FlickerSeed > 0)
                        _volumes[i].SyncLightVisibility();
            }
            catch (Exception e)
            {
                ReleaseVolumes();
                _volumeFailed = true;
                Plugin.Log.LogWarning($"{Tag} haze synchronization disabled: {e}");
            }
        }

        void UpdateVolumes(Camera camera)
        {
            try
            {
                bool allowed = Singleton<SettingsManager>.Instance?.Graphics.Settings.VolumetricLight.Value == true;
                if (_haze.Value <= 0f || !allowed)
                {
                    ReleaseVolumes();
                    _volumeFailed = false;
                    return;
                }
                if (_volumeFailed || camera.GetComponent<VolumetricLightRenderer>() == null) return;
                if (_volumes.Count == 0)
                {
                    // Store the owner before initialization so partial Awake failures are cleaned up too.
                    foreach (var light in _lights)
                    {
                        var owner = new OwnedVolume(light);
                        _volumes.Add(owner);
                        owner.Create();
                    }
                    _lastHaze = -1;
                    Plugin.Log.LogDebug($"{Tag} created {_volumes.Count} native volumes; 16 samples, source shadows retained");
                }
                bool changed = _lastHaze != _haze.Value;
                foreach (var owner in _volumes)
                {
                    // Native spot haze becomes unshadowed past shadowDistance. End ours before that
                    // cutoff, independently of the light's existing distance fade, to avoid wall leaks.
                    float distance = Vector3.Distance(camera.transform.position, owner.Light.transform.position);
                    float shadowFade = Mathf.Clamp01((QualitySettings.shadowDistance - 2f - distance) / 5f);
                    owner.Apply(_haze.Value * shadowFade, changed);
                }
                if (changed) Plugin.Log.LogDebug($"{Tag} haze strength={_haze.Value:F2}");
                _lastHaze = _haze.Value;
            }
            catch (Exception e)
            {
                ReleaseVolumes();
                _volumeFailed = true;
                Plugin.Log.LogWarning($"{Tag} haze disabled; set HazeStrength to 0 then back to retry: {e}");
            }
        }

        void UpdateSmoke()
        {
            try
            {
                if (_smoke.Value <= 0f)
                {
                    foreach (var system in _particles)
                        if (system != null && !system.isStopped)
                            system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    _smokeFailed = false;
                    _lastSmoke = 0;
                    return;
                }
                if (_smokeFailed) return;
                if (_particles.Count == 0)
                {
                    // Poll a shared asset load from the main thread. No asynchronous continuation
                    // creates scene objects, so a departed raid cannot receive a late smoke bank.
                    if (_load == null) _load = ExfilSignals.EnsureHarvested();
                    if (!_load.IsCompleted) return;
                    _load.GetAwaiter().GetResult();
                    BuildSmoke();
                    _lastSmoke = -1;
                }
                if (_lastSmoke != _smoke.Value)
                {
                    _smokeMaterial.SetColor("_TintColor", new Color(.85f, .9f, 1f, .3f * _smoke.Value));
                    foreach (var system in _particles)
                    {
                        if (system == null || system.isPlaying) continue;
                        system.Simulate(4f, false, true, false);
                        system.Play(false);
                    }
                    Plugin.Log.LogDebug($"{Tag} smoke density={_smoke.Value:F2} banks={_particles.Count}");
                    _lastSmoke = _smoke.Value;
                }
            }
            catch (Exception e)
            {
                ReleaseSmoke();
                _smokeFailed = true;
                _load = null;
                Plugin.Log.LogWarning($"{Tag} smoke disabled; set SmokeDensity to 0 then back to retry: {e}");
            }
        }

        void BuildSmoke()
        {
            if (ExfilSignals.Material == null || ExfilSignals.Template == null)
                throw new InvalidOperationException("Native Flare_Smoke material/template unavailable");
            _smokeMaterial = new Material(ExfilSignals.Material) { name = "ArenaMapPort_NeonSmoke" };
            // Remove the signal flare's red ambient floor. Keep a subtle cool minimum in darkness.
            _smokeMaterial.SetColor("_LocalMinimalAmbientLight", new Color(.025f, .028f, .033f, 1f));
            var originalSheet = ExfilSignals.Template.textureSheetAnimation;
            int seed = 0;
            foreach (var fixture in ArenaMapRegistry.Equator.NeonFixtures)
            {
                // Smoke uses the low fixture mounts; elevated sign lights get haze only.
                if (fixture.Bottom > 1f) continue;
                var normal = new Vector3(fixture.NormalX, 0f, fixture.NormalZ);
                var bank = new GameObject(fixture.Name + "_mist");
                bank.transform.SetParent(_root.transform, false);
                bank.transform.position = new Vector3(fixture.X, fixture.Bottom + .25f, fixture.Z) + normal;
                var system = bank.AddComponent<ParticleSystem>();
                _particles.Add(system);
                system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.useAutoRandomSeed = false;
                system.randomSeed = (uint)(1701 + seed++);
                var main = system.main;
                main.playOnAwake = false;
                main.loop = true;
                main.duration = 10f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(8f, 11f);
                main.startSpeed = 0f;
                main.startSize3D = true;
                main.startSizeX = new ParticleSystem.MinMaxCurve(2.5f, 4f);
                main.startSizeY = new ParticleSystem.MinMaxCurve(.7f, 1.2f);
                main.startSizeZ = 1f;
                main.startColor = Color.white;
                main.maxParticles = 32;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                var emission = system.emission;
                emission.rateOverTime = 2f;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(1f, .15f, 1f);
                var velocity = system.velocityOverLifetime;
                velocity.enabled = true;
                velocity.space = ParticleSystemSimulationSpace.World;
                velocity.x = new ParticleSystem.MinMaxCurve(normal.x * .12f - .035f, normal.x * .12f + .035f);
                velocity.y = new ParticleSystem.MinMaxCurve(.015f, .025f);
                velocity.z = new ParticleSystem.MinMaxCurve(normal.z * .12f - .035f, normal.z * .12f + .035f);
                var color = system.colorOverLifetime;
                color.enabled = true;
                color.color = new Gradient
                {
                    colorKeys = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    alphaKeys = new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(.8f, .2f),
                        new GradientAlphaKey(.55f, .65f), new GradientAlphaKey(0f, 1f) }
                };
                var size = system.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, .65f, 1, 1.2f));
                var sheet = system.textureSheetAnimation;
                sheet.enabled = originalSheet.enabled;
                sheet.mode = originalSheet.mode;
                sheet.numTilesX = originalSheet.numTilesX;
                sheet.numTilesY = originalSheet.numTilesY;
                sheet.animation = originalSheet.animation;
                sheet.frameOverTime = originalSheet.frameOverTime;
                sheet.startFrame = originalSheet.startFrame;
                sheet.cycleCount = originalSheet.cycleCount;
                var renderer = bank.GetComponent<ParticleSystemRenderer>();
                renderer.sharedMaterial = _smokeMaterial;
                renderer.maxParticleSize = 1f;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            Plugin.Log.LogDebug($"{Tag} created {_particles.Count} shallow smoke banks; shader={_smokeMaterial.shader.name}, " +
                $"atlas={originalSheet.numTilesX}x{originalSheet.numTilesY}, maxParticles={_particles.Count * 32}");
        }

        internal void Probe() => Plugin.Log.LogDebug($"{Tag} volumes={_volumes.Count} " +
            $"haze={_haze.Value:F2} smokeBanks={_particles.Count} smoke={_smoke.Value:F2} " +
            $"particles={_particles.Where(p => p != null).Sum(p => p.particleCount)} " +
            $"sourceLightBuffers={SourceLightBuffers()}");

        int SourceLightBuffers() => _lights.Where(l => l != null)
            .Sum(l => l.GetCommandBuffers(LightEvent.AfterShadowMap).Length);

        void ReleaseVolumes()
        {
            foreach (var volume in _volumes) volume.Dispose();
            if (DeveloperOptions.Enabled && _volumes.Count > 0) Plugin.Log.LogDebug($"{Tag} released {_volumes.Count} volumes and their command buffers; " +
                $"remainingSourceLightBuffers={SourceLightBuffers()}");
            _volumes.Clear();
        }

        void ReleaseSmoke()
        {
            foreach (var system in _particles)
                if (system != null) { system.gameObject.SetActive(false); Object.Destroy(system.gameObject); }
            _particles.Clear();
            if (_smokeMaterial != null) Object.Destroy(_smokeMaterial);
            _smokeMaterial = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseVolumes();
            ReleaseSmoke();
            _load = null;
            Plugin.Log.LogDebug($"{Tag} disposed; smoke/materials/volumes released");
        }

        sealed class OwnedVolume : IDisposable
        {
            static readonly FieldInfo Command = AccessTools.Field(typeof(VolumetricLight), "_commandBuffer");
            static readonly FieldInfo Cascade = AccessTools.Field(typeof(VolumetricLight), "_cascadeShadowCommandBuffer");
            internal readonly Light Light;
            VolumetricLight _volume;
            CommandBuffer _command, _cascade;

            internal OwnedVolume(Light light) { Light = light; }

            internal void Create()
            {
                if (Command == null || Cascade == null) throw new MissingFieldException("VolumetricLight buffers changed");
                try { _volume = Light.gameObject.AddComponent<VolumetricLight>(); }
                finally
                {
                    // AddComponent may have attached the component before an Awake exception.
                    // Capture even that partial initialization for the caller's cleanup path.
                    if (_volume == null) _volume = Light.GetComponent<VolumetricLight>();
                    if (_volume != null)
                    {
                        _command = Command.GetValue(_volume) as CommandBuffer;
                        _cascade = Cascade.GetValue(_volume) as CommandBuffer;
                    }
                }
                if (_volume == null) throw new InvalidOperationException("Native volume was not created");
                if (_volume.VolumetricMaterial == null) throw new InvalidOperationException("Native volume has no material");
                _volume.SampleCount = 16;
                _volume.ExtinctionCoef = .01f;
                _volume.MieG = .15f;
                _volume.Noise = true;
                _volume.NoiseScale = .1f;
                _volume.NoiseIntensity = 1f;
                _volume.NoiseIntensityOffset = .3f;
                _volume.NoiseVelocity = new Vector2(.15f, .1f);
                _volume.SetDefaultLightValues();
                _volume.SetDynamicLightValues();
            }

            internal void Apply(float strength, bool changed)
            {
                if (_volume == null) return;
                if (changed || !Mathf.Approximately(_volume.ScatteringCoef, strength))
                {
                    _volume.ScatteringCoef = strength;
                    _volume.SetDefaultValues();
                }
                SyncLightVisibility();
            }

            internal void SyncLightVisibility()
            {
                if (_volume == null || Light == null) return;
                bool visible = _volume.ScatteringCoef > 0f && Light.enabled && Light.intensity >= .001f;
                if (!visible) _volume.NeedToPrerender = false;
                if (_volume.enabled != visible) _volume.enabled = visible;
                if (visible)
                {
                    _volume.CheckIntensity();
                    // Camera recreation clears native static registration; AddLight is idempotent.
                    VolumetricLightRenderer.AddLight(_volume);
                }
            }

            public void Dispose()
            {
                if (_volume != null)
                {
                    _volume.enabled = false;
                    if (Light != null) _volume.ClearCommandBuffer();
                    // Native OnDisable/OnDestroy do not dispose either allocation. Prevent later
                    // callbacks from clearing disposed buffers; native OnDestroy owns the material.
                    Command.SetValue(_volume, null);
                    Cascade.SetValue(_volume, null);
                    Object.Destroy(_volume);
                }
                _command?.Dispose(); _cascade?.Dispose();
                _command = _cascade = null;
                _volume = null;
            }
        }
    }
}
