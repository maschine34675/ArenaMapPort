using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Owned native distance fog for Equator midnight, independent of TOD's dark sky color.
    /// Only this component's synchronous render sees the temporary fog settings.
    /// </summary>
    internal sealed class NeonDistanceHaze : IDisposable
    {
        const string Tag = "[ArenaMapPort][NightHaze]";
        static readonly Type FogType = AccessTools.TypeByName("UnityStandardAssets.ImageEffects.GlobalFog");
        static readonly FieldInfo MaterialField = FogType == null ? null : AccessTools.Field(FogType, "fogMaterial");
        static readonly int FogColorId = Shader.PropertyToID("unity_FogColor");
        static readonly Color HazeColor = new Color(.15f, .20f, .23f, 1f);
        static ConfigEntry<float> _density;
        static NeonDistanceHaze _current;
        static bool _available;
        Behaviour _effect;
        Material _material;
        Camera _camera;
        bool _disposed, _failed;
        int _lastFrame = -1;
        float _lastDensity;

        struct RenderScope
        {
            internal bool Active;
            internal FogMode Mode;
            internal float Density;
            internal Color Color;
            internal Vector4 ShaderColor;
        }

        internal static void Install(Harmony harmony, EventHandler changed)
        {
            _density = Plugin.Instance.Config.Bind("Night neon (arena_equator)", "DistanceHaze", .002f,
                new ConfigDescription("Cool, subtle distance haze throughout Equator at 00:00. " +
                    "0 = off; 0.002 is subtle. Starts beyond 5 metres, independent of the night sky. " +
                    "Separate from local light cones and smoke banks. Live.",
                    new AcceptableValueRange<float>(0f, .01f)));
            _density.SettingChanged += changed;
            var method = FogType == null ? null : AccessTools.Method(FogType, "OnRenderImage",
                new[] { typeof(RenderTexture), typeof(RenderTexture) });
            var resources = FogType == null ? null : AccessTools.Method(FogType, "CheckResources");
            if (method == null || resources == null || MaterialField == null)
            {
                Plugin.Log.LogWarning($"{Tag} native GlobalFog API missing; distance haze unavailable");
                return;
            }
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(NeonDistanceHaze), nameof(BeforeRender)),
                postfix: new HarmonyMethod(typeof(NeonDistanceHaze), nameof(AfterRender)),
                finalizer: new HarmonyMethod(typeof(NeonDistanceHaze), nameof(FinalizeRender)));
            harmony.Patch(resources, postfix: new HarmonyMethod(typeof(NeonDistanceHaze), nameof(CaptureMaterial)));
            _available = true;
        }

        internal NeonDistanceHaze() { _current = this; }

        internal void Update(Camera camera)
        {
            if (_disposed) return;
            bool allowed = _available && EquatorNightNeon.IsActive && _density.Value > 0f &&
                !float.IsNaN(_density.Value) && !float.IsInfinity(_density.Value);
            if (!allowed || _camera != camera)
            {
                Release();
                _failed = false;
            }
            if (!allowed || _failed || camera == null || !camera.isActiveAndEnabled) return;
            try
            {
                if (_effect == null)
                {
                    var shader = Shader.Find("Hidden/GlobalFog");
                    if (shader == null || !shader.isSupported)
                        throw new InvalidOperationException("Native Hidden/GlobalFog shader unavailable");
                    _camera = camera;
                    _effect = (Behaviour)camera.gameObject.AddComponent(FogType);
                    // No native Awake/OnEnable; base Start initializes support next frame.
                    Set("fogShader", shader);
                    Set("distanceFog", true);
                    Set("heightFog", false);
                    Set("useRadialDistance", true);
                    Set("startDistance", 5f);
                    _effect.enabled = true;
                    Plugin.Log.LogInfo($"{Tag} created owned native distance pass; shader={shader.name} " +
                        $"color={HazeColor} start=5m camera={camera.GetInstanceID()}");
                }
            }
            catch (Exception e)
            {
                Release();
                _camera = camera; // Retry only after off/on or camera replacement.
                _failed = true;
                Plugin.Log.LogWarning($"{Tag} unavailable; set DistanceHaze to 0 then back to retry: {e}");
            }
        }

        void Set(string name, object value) => AccessTools.Field(FogType, name).SetValue(_effect, value);

        static void CaptureMaterial(Behaviour __instance)
        {
            var owner = _current;
            if (owner == null || owner._disposed || __instance != owner._effect) return;
            var material = MaterialField.GetValue(__instance) as Material;
            if (owner._material != null && owner._material != material) Object.Destroy(owner._material);
            owner._material = material;
        }

        static bool BeforeRender(Behaviour __instance, RenderTexture __0, RenderTexture __1,
            out RenderScope __state)
        {
            __state = default;
            var owner = _current;
            if (owner == null || __instance != owner._effect) return true;
            if (owner._disposed || !EquatorNightNeon.IsActive || _density.Value <= 0f ||
                float.IsNaN(_density.Value) || float.IsInfinity(_density.Value))
            {
                // The native effect otherwise renders ordinary fog before 10 Hz cleanup runs.
                Graphics.Blit(__0, __1);
                return false;
            }
            __state = new RenderScope
            {
                Active = true, Mode = RenderSettings.fogMode, Density = RenderSettings.fogDensity,
                Color = RenderSettings.fogColor, ShaderColor = Shader.GetGlobalVector(FogColorId)
            };
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = Mathf.Clamp(_density.Value, 0f, .01f);
            RenderSettings.fogColor = HazeColor;
            owner._lastDensity = RenderSettings.fogDensity;
            owner._lastFrame = Time.frameCount;
            return true;
        }

        static void AfterRender(ref RenderScope __state) => Restore(ref __state);
        static Exception FinalizeRender(Exception __exception, ref RenderScope __state)
        {
            Restore(ref __state);
            return __exception;
        }

        static void Restore(ref RenderScope state)
        {
            if (!state.Active) return;
            state.Active = false;
            RenderSettings.fogMode = state.Mode;
            RenderSettings.fogDensity = state.Density;
            RenderSettings.fogColor = state.Color;
            Shader.SetGlobalVector(FogColorId, state.ShaderColor);
        }

        internal void Probe() => Plugin.Log.LogInfo($"{Tag} density={_density.Value:F4} " +
            $"owned={(_effect != null)} enabled={(_effect != null && _effect.enabled)} " +
            $"lastDensity={_lastDensity:F4} ageFrames={(_lastFrame < 0 ? -1 : Time.frameCount - _lastFrame)} " +
            $"nativeMode={RenderSettings.fogMode} nativeDensity={RenderSettings.fogDensity:F6} " +
            $"nativeColor={RenderSettings.fogColor}; native weather restored after each pass");

        void Release()
        {
            if (_effect != null)
            {
                _effect.enabled = false;
                Object.Destroy(_effect);
                Plugin.Log.LogInfo($"{Tag} removed owned distance pass; native weather retained");
            }
            // Keep ownership even when a retired camera already destroyed its component.
            // Neither GlobalFog nor PostEffectsBase disposes this private material.
            if (_material != null) Object.Destroy(_material);
            _material = null;
            _effect = null;
            _camera = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release();
            if (ReferenceEquals(_current, this)) _current = null;
        }
    }
}
