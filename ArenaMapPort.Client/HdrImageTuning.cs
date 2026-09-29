using System;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using Prism.Utils;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Prism reads the live environment exposure immediately before UpdateShaderValues. Stage
    /// per-map image controls there, retaining indoor/outdoor adaptation and camera presets.
    /// Restore component fields after upload and changed global tone keywords after rendering;
    /// only the current Equator/Bay5 gameplay camera's own materials receive adjusted values.
    /// Native evidence: PrismEffects.cs 1063-1380, 1611-1784.
    /// </summary>
    internal static class HdrImageTuning
    {
        internal enum ToneProfile { Original, Filmic, RomB, ACES, Off }

        // ConfigurationManager reads this optional field by reflection; no dependency is needed.
        sealed class AdvancedSetting { public bool? IsAdvanced = true; }

        sealed class Settings
        {
            internal ConfigEntry<float> Compensation, Range, Speed;
            internal ConfigEntry<ToneProfile> Tone;
            internal ConfigEntry<bool> CustomCurve;
            internal ConfigEntry<Vector3> Curve, SecondaryCurve;
        }

        struct Values
        {
            internal float Grey, Lower, Upper, Speed;
            internal bool Exposure, Tonemap;
            internal TonemapType Type;
            internal Vector3 Curve, SecondaryCurve;

            internal static Values Read(PrismEffects effect) => new Values
            {
                Grey = effect.exposureMiddleGrey, Lower = effect.exposureLowerLimit,
                Upper = effect.exposureUpperLimit, Speed = effect.exposureSpeed,
                Exposure = effect.useExposure, Tonemap = effect.useTonemap, Type = effect.tonemapType,
                Curve = effect.toneValues, SecondaryCurve = effect.secondaryToneValues
            };

            internal string ExposureText => $"on={Exposure} grey={Grey:0.######} range=[{Lower:0.####},{Upper:0.####}] speed={Speed:0.####}";
            internal string ToneText => $"on={Tonemap} type={Type} curve={VectorText(Curve)} secondary={VectorText(SecondaryCurve)}";
        }

        sealed class RenderScope
        {
            internal PrismEffects Effect;
            internal RenderScope Parent;
            internal string Map;
            internal Values Native, Adjusted;
            internal bool Staged, FieldsActive, ToneChanged, Uploaded, Finished, InvalidInput, InPool;
            internal int Frame;
            internal int UploadedToneMask, ExpectedGlobalToneMask, ObservedGlobalToneMask;
            internal float UploadedGrey, UploadedLower, UploadedUpper, UploadedSpeed;
            internal Vector3 UploadedCurve, UploadedSecondary;
        }

        struct DiagnosticSample
        {
            internal PrismEffects Effect;
            internal string Map;
            internal Values Native, Adjusted;
            internal bool InvalidInput, FieldsActive, Finished;
            internal int Frame, UploadedToneMask, ExpectedGlobalToneMask, ObservedGlobalToneMask;
            internal float UploadedGrey, UploadedLower, UploadedUpper, UploadedSpeed;
            internal Vector3 UploadedCurve, UploadedSecondary;

            internal DiagnosticSample(RenderScope scope)
            {
                Effect = scope.Effect; Map = scope.Map; Native = scope.Native; Adjusted = scope.Adjusted;
                InvalidInput = scope.InvalidInput; FieldsActive = scope.FieldsActive; Finished = scope.Finished;
                Frame = scope.Frame; UploadedToneMask = scope.UploadedToneMask;
                ExpectedGlobalToneMask = scope.ExpectedGlobalToneMask; ObservedGlobalToneMask = scope.ObservedGlobalToneMask;
                UploadedGrey = scope.UploadedGrey; UploadedLower = scope.UploadedLower;
                UploadedUpper = scope.UploadedUpper; UploadedSpeed = scope.UploadedSpeed;
                UploadedCurve = scope.UploadedCurve; UploadedSecondary = scope.UploadedSecondary;
            }
        }

        static readonly Dictionary<string, Settings> Configs = new Dictionary<string, Settings>();
        static readonly string[] ToneKeywords = { "PRISM_FILMIC_TONEMAP", "PRISM_ROMB_TONEMAP", "PRISM_ACES_TONEMAP" };
        static readonly int GreyProperty = Shader.PropertyToID("_ExposureMiddleGrey");
        static readonly int LowerProperty = Shader.PropertyToID("_ExposureLowerLimit");
        static readonly int UpperProperty = Shader.PropertyToID("_ExposureUpperLimit");
        static readonly int SpeedProperty = Shader.PropertyToID("_ExposureSpeed");
        static readonly int CurveProperty = Shader.PropertyToID("_ToneParams");
        static readonly int SecondaryProperty = Shader.PropertyToID("_SecondaryToneParams");
        static PrismEffects _effect;
        static Camera _camera;
        static readonly Stack<RenderScope> ScopePool = new Stack<RenderScope>();
        static RenderScope _active;
        static DiagnosticSample _last;
        static bool _installed, _failureLogged, _hasLast, _sampleRequested = true;
        static float _nextSampleTime;

        internal static void Install(Harmony harmony)
        {
            if (_installed) return;
            Bind(ArenaMapRegistry.Equator);
            Bind(ArenaMapRegistry.Bay5);
            var render = AccessTools.Method(typeof(PrismEffects), nameof(PrismEffects.OnRenderImageInternal),
                new[] { typeof(RenderTexture), typeof(RenderTexture) });
            var upload = AccessTools.Method(typeof(PrismEffects), nameof(PrismEffects.UpdateShaderValues), Type.EmptyTypes);
            if (!HarmonyPatches.Verify(render, typeof(PrismEffects), nameof(PrismEffects.OnRenderImageInternal)) ||
                !HarmonyPatches.Verify(upload, typeof(PrismEffects), nameof(PrismEffects.UpdateShaderValues))) return;
            if (render.ReturnType != typeof(void) || upload.ReturnType != typeof(void) || render.IsStatic || upload.IsStatic)
            {
                Plugin.Log.LogError("[ArenaMapPort][HDR] unexpected native render/upload signature; image tuning disabled");
                return;
            }
            // Install cleanup first: even a failed later installation cannot leave staged fields behind.
            harmony.Patch(render,
                prefix: new HarmonyMethod(typeof(HdrImageTuning), nameof(BeforeRender)),
                postfix: new HarmonyMethod(typeof(HdrImageTuning), nameof(AfterRender)),
                finalizer: new HarmonyMethod(typeof(HdrImageTuning), nameof(FinalizeRender)));
            harmony.Patch(upload,
                prefix: new HarmonyMethod(typeof(HdrImageTuning), nameof(BeforeUpload)),
                postfix: new HarmonyMethod(typeof(HdrImageTuning), nameof(AfterUpload)),
                finalizer: new HarmonyMethod(typeof(HdrImageTuning), nameof(FinalizeUpload)));
            _installed = true;
            Plugin.Log.LogInfo("[ArenaMapPort][HDR] per-map exposure/tone controls installed after native environment overwrite; defaults preserve camera settings");
        }

        static void Bind(ArenaMapDescriptor map)
        {
            var section = $"Lighting ({map.Key})";
            var config = Plugin.Instance.Config;
            Configs[map.Key] = new Settings
            {
                Compensation = config.Bind(section, "ExposureCompensationEV", 0f,
                    new ConfigDescription("Target brightness relative to native automatic exposure, in stops. +1 doubles the live middle-grey target; -1 halves it. 0 preserves native indoor/outdoor adaptation. Does not enable exposure. Live.",
                        new AcceptableValueRange<float>(-4f, 4f))),
                Range = config.Bind(section, "ExposureRangeMultiplier", 1f,
                    new ConfigDescription("Scale both native exposure limits about zero. 1 preserves the camera range; lower values reduce the limits, higher values increase them. Native intervals spanning zero retain a positive upper limit. Does not enable exposure. Live.",
                        new AcceptableValueRange<float>(0.25f, 2f))),
                Speed = config.Bind(section, "ExposureSpeedMultiplier", 1f,
                    new ConfigDescription("Multiply the live native exposure adaptation speed. 1 preserves indoor/outdoor behavior; higher values adapt faster. Does not enable exposure. Live.",
                        new AcceptableValueRange<float>(0.1f, 4f))),
                Tone = config.Bind(section, "TonemapProfile", ToneProfile.Original,
                    "Original preserves native tonemapping and its current parameters. Filmic, RomB and ACES enable that native profile with its matching native reset parameters. Off disables tonemapping. Live."),
                CustomCurve = config.Bind(section, "TonemapCustomCurve", false,
                    new ConfigDescription("Use the two advanced raw native parameter vectors. Requires an explicit Filmic, RomB or ACES profile; Original and Off ignore them. Invalid or out-of-range vectors fall back to that profile. Live.", null, new AdvancedSetting())),
                Curve = config.Bind(section, "TonemapCurve", new Vector3(-1f, 2.72f, 0.15f),
                    new ConfigDescription("Raw Prism toneValues (x, y, z); meanings depend on the selected native shader profile. Each component must be finite and within [-16,16]. Used only with TonemapCustomCurve and an explicit profile. Live.", null, new AdvancedSetting())),
                SecondaryCurve = config.Bind(section, "TonemapSecondaryCurve", Vector3.zero,
                    new ConfigDescription("Raw Prism secondaryToneValues (x, y, z); meanings depend on the selected native shader profile. Each component must be finite and within [-16,16]. Used only with TonemapCustomCurve and an explicit profile. Live.", null, new AdvancedSetting()))
            };
            var settings = Configs[map.Key];
            settings.Compensation.SettingChanged += RequestSample;
            settings.Range.SettingChanged += RequestSample;
            settings.Speed.SettingChanged += RequestSample;
            settings.Tone.SettingChanged += RequestSample;
            settings.CustomCurve.SettingChanged += RequestSample;
            settings.Curve.SettingChanged += RequestSample;
            settings.SecondaryCurve.SettingChanged += RequestSample;
        }

        static void RequestSample(object sender, EventArgs args) => _sampleRequested = true;

        internal static void OnCameraChanged()
        {
            Release();
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !Configs.ContainsKey(map.Key) || !CameraManager.Exist) return;
            _camera = CameraManager.Instance.Camera;
            if (_camera != null) _effect = _camera.GetComponent<PrismEffects>();
        }

        internal static void Release()
        {
            while (_active != null) FinishRender(_active);
            _effect = null; _camera = null; _last = default; _hasLast = false; _sampleRequested = true;
        }

        static void BeforeRender(PrismEffects __instance, out RenderScope __state)
        {
            __state = null;
            try
            {
                if (!_installed || !CameraManager.Exist || !Singleton<GameWorld>.Instantiated ||
                    Singleton<GameWorld>.Instance.MainPlayer == null) return;
                var map = ArenaMapRaid.CurrentMap;
                var camera = CameraManager.Instance.Camera;
                if (map == null || !Configs.ContainsKey(map.Key) || camera == null ||
                    camera.cameraType != CameraType.Game || !camera.isActiveAndEnabled ||
                    __instance == null || !__instance.isActiveAndEnabled || __instance.isChildPrism ||
                    __instance.m_MasterEffectExposure != null || __instance.gameObject != camera.gameObject) return;
                if (_camera != camera || _effect != __instance) OnCameraChanged();
                if (!ReferenceEquals(_effect, __instance)) return;
                __state = ScopePool.Count > 0 ? ScopePool.Pop() : new RenderScope();
                __state.Effect = __instance; __state.Parent = _active; __state.Map = map.Key; __state.Frame = Time.frameCount;
                __state.Native = default; __state.Adjusted = default;
                __state.Staged = __state.FieldsActive = __state.ToneChanged = __state.Uploaded = false;
                __state.Finished = __state.InvalidInput = __state.InPool = false;
                _active = __state;
            }
            catch (Exception e) { ReportFailure(e); }
        }

        static void BeforeUpload(PrismEffects __instance, out RenderScope __state)
        {
            __state = null;
            var scope = _active;
            if (scope == null || scope.Finished || scope.Staged || !ReferenceEquals(scope.Effect, __instance)) return;
            try
            {
                // OnRenderImageInternal has just refreshed these from EnvironmentManager. Do not
                // capture at camera creation: the environment keeps blending them as the player moves.
                scope.Native = Values.Read(__instance);
                scope.Adjusted = Adjust(scope.Native, Configs[scope.Map], out scope.InvalidInput);
                scope.Staged = true;
                scope.ToneChanged = scope.Native.Tonemap != scope.Adjusted.Tonemap || scope.Native.Type != scope.Adjusted.Type;
                scope.FieldsActive = true;
                __state = scope;
                WriteChanged(__instance, scope.Native, scope.Adjusted);
            }
            catch (Exception e) { RestoreFields(scope); ReportFailure(e); }
        }

        static Values Adjust(Values value, Settings config, out bool invalid)
        {
            invalid = false;
            float ev = Setting(config.Compensation.Value, -4f, 4f, 0f, ref invalid);
            float range = Setting(config.Range.Value, 0.25f, 2f, 1f, ref invalid);
            float speed = Setting(config.Speed.Value, 0.1f, 4f, 1f, ref invalid);
            if (value.Exposure)
            {
                if (ev != 0f)
                {
                    if (Finite(value.Grey) && value.Grey >= 0f)
                        value.Grey = (float)Math.Min(64d, (double)value.Grey * Math.Pow(2d, ev));
                    else invalid = true;
                }
                if (speed != 1f)
                {
                    if (Finite(value.Speed) && value.Speed >= 0f)
                        value.Speed = (float)Math.Min(128d, (double)value.Speed * speed);
                    else invalid = true;
                }
                if (range != 1f)
                {
                    if (Finite(value.Lower) && Finite(value.Upper) && value.Lower < value.Upper)
                    {
                        // Native limits can be very asymmetric (for example -10 and 0.9).
                        // Contracting about their midpoint can make the upper limit negative
                        // and black out the image. Scaling about zero preserves their signs.
                        float lower = (float)Math.Max(-32d, Math.Min(32d, (double)value.Lower * range));
                        float upper = (float)Math.Max(-32d, Math.Min(32d, (double)value.Upper * range));
                        if (lower < upper) { value.Lower = lower; value.Upper = upper; }
                        else invalid = true;
                    }
                    else invalid = true;
                }
            }
            var tone = config.Tone.Value;
            if ((int)tone < (int)ToneProfile.Original || (int)tone > (int)ToneProfile.Off)
            { invalid = true; tone = ToneProfile.Original; }
            if (tone == ToneProfile.Original) return value;
            if (tone == ToneProfile.Off) { value.Tonemap = false; return value; }
            value.Tonemap = true;
            // Exact native ResetToneParams* constants (PrismEffects.cs 549-565).
            switch (tone)
            {
                case ToneProfile.Filmic:
                    value.Type = TonemapType.Filmic;
                    value.Curve = new Vector3(6.2f, 0.5f, 1.7f);
                    value.SecondaryCurve = new Vector3(0.004f, 0.06f, 0f);
                    break;
                case ToneProfile.ACES:
                    value.Type = TonemapType.ACES;
                    value.Curve = new Vector3(2.51f, 0.03f, 2.43f);
                    value.SecondaryCurve = new Vector3(0.59f, 0.14f, 0f);
                    break;
                default:
                    value.Type = TonemapType.RomB;
                    value.Curve = new Vector3(-1f, 2.72f, 0.15f);
                    value.SecondaryCurve = Vector3.zero;
                    break;
            }
            if (config.CustomCurve.Value)
            {
                if (ValidVector(config.Curve.Value) && ValidVector(config.SecondaryCurve.Value))
                { value.Curve = config.Curve.Value; value.SecondaryCurve = config.SecondaryCurve.Value; }
                else invalid = true;
            }
            return value;
        }

        static void WriteChanged(PrismEffects effect, Values from, Values to)
        {
            if (!from.Grey.Equals(to.Grey)) effect.exposureMiddleGrey = to.Grey;
            if (!from.Lower.Equals(to.Lower)) effect.exposureLowerLimit = to.Lower;
            if (!from.Upper.Equals(to.Upper)) effect.exposureUpperLimit = to.Upper;
            if (!from.Speed.Equals(to.Speed)) effect.exposureSpeed = to.Speed;
            if (from.Tonemap != to.Tonemap) effect.useTonemap = to.Tonemap;
            if (from.Type != to.Type) effect.tonemapType = to.Type;
            if (!from.Curve.Equals(to.Curve)) effect.toneValues = to.Curve;
            if (!from.SecondaryCurve.Equals(to.SecondaryCurve)) effect.secondaryToneValues = to.SecondaryCurve;
        }

        static void AfterUpload(RenderScope __state)
        {
            if (__state == null) return;
            try
            {
                // Diagnostics read back actual native uniforms at 2 Hz, plus the first completed
                // upload after any setting change. Normal frames perform no material readbacks.
                if (!_sampleRequested && Time.unscaledTime < _nextSampleTime) return;
                var effect = __state.Effect;
                if (effect != null && effect.m_Material != null && effect.m_Material2 != null)
                {
                    __state.UploadedGrey = effect.m_Material.GetFloat(GreyProperty);
                    __state.UploadedLower = effect.m_Material.GetFloat(LowerProperty);
                    __state.UploadedUpper = effect.m_Material.GetFloat(UpperProperty);
                    __state.UploadedSpeed = effect.m_Material2.GetFloat(SpeedProperty);
                    __state.UploadedCurve = effect.m_Material.GetVector(CurveProperty);
                    __state.UploadedSecondary = effect.m_Material.GetVector(SecondaryProperty);
                    __state.UploadedToneMask = ReadToneMask(effect.m_Material);
                    __state.Uploaded = true;
                    _sampleRequested = false;
                    _nextSampleTime = Time.unscaledTime + 0.5f;
                }
            }
            catch (Exception e) { ReportFailure(e); }
            finally { RestoreFields(__state); }
        }

        static Exception FinalizeUpload(Exception __exception, RenderScope __state)
        {
            RestoreFields(__state);
            return __exception;
        }

        static void RestoreFields(RenderScope scope)
        {
            if (scope == null || !scope.FieldsActive) return;
            scope.FieldsActive = false;
            var effect = scope.Effect;
            if (effect == null) return;
            var current = Values.Read(effect);
            var restore = current;
            // Restore only our still-present writes; preserve changes made by another patch.
            if (current.Grey.Equals(scope.Adjusted.Grey)) restore.Grey = scope.Native.Grey;
            if (current.Lower.Equals(scope.Adjusted.Lower)) restore.Lower = scope.Native.Lower;
            if (current.Upper.Equals(scope.Adjusted.Upper)) restore.Upper = scope.Native.Upper;
            if (current.Speed.Equals(scope.Adjusted.Speed)) restore.Speed = scope.Native.Speed;
            if (current.Tonemap == scope.Adjusted.Tonemap) restore.Tonemap = scope.Native.Tonemap;
            if (current.Type == scope.Adjusted.Type) restore.Type = scope.Native.Type;
            if (current.Curve.Equals(scope.Adjusted.Curve)) restore.Curve = scope.Native.Curve;
            if (current.SecondaryCurve.Equals(scope.Adjusted.SecondaryCurve)) restore.SecondaryCurve = scope.Native.SecondaryCurve;
            WriteChanged(effect, current, restore);
        }

        static void AfterRender(RenderScope __state) => FinishRender(__state);

        static Exception FinalizeRender(Exception __exception, RenderScope __state)
        {
            FinishRender(__state);
            // Return only after all postfixes have run, so reentrant rendering cannot reuse an
            // invocation's __state while Harmony still needs it. Normal rendering reuses one scope.
            if (__state != null && !__state.InPool)
            {
                __state.InPool = true;
                __state.Effect = null; __state.Parent = null; __state.Map = null;
                ScopePool.Push(__state);
            }
            return __exception;
        }

        static void FinishRender(RenderScope scope)
        {
            if (scope == null || scope.Finished) return;
            scope.Finished = true;
            try
            {
                RestoreFields(scope);
                var baseline = scope.Parent != null && scope.Parent.Staged && !scope.Parent.Finished
                    ? scope.Parent.Adjusted : scope.Native;
                if (scope.Staged && scope.ToneChanged)
                {
                    // Native material keywords were cleared at UpdateShaderValues entry. Keep its
                    // selected local variant for the blit; now restore only the three global tone
                    // keywords to what this native render would have left behind.
                    for (int i = 0; i < ToneKeywords.Length; i++)
                    {
                        if (baseline.Tonemap && (int)baseline.Type == i) Shader.EnableKeyword(ToneKeywords[i]);
                        else Shader.DisableKeyword(ToneKeywords[i]);
                    }
                }
                if (scope.Uploaded && ReferenceEquals(scope.Effect, _effect))
                {
                    scope.ExpectedGlobalToneMask = ExpectedToneMask(baseline);
                    scope.ObservedGlobalToneMask = ReadToneMask();
                    _last = new DiagnosticSample(scope);
                    _hasLast = true;
                }
            }
            catch (Exception e) { ReportFailure(e); }
            finally { if (ReferenceEquals(_active, scope)) _active = scope.Parent; }
        }

        internal static void LogDiagnostics()
        {
            _sampleRequested = true;
            try
            {
                var current = CameraManager.Exist ? CameraManager.Instance.Camera : null;
                var prism = current != null ? current.GetComponent<PrismEffects>() : null;
                Plugin.Log.LogInfo($"[ArenaMapPort][HDR] map={ArenaMapRaid.CurrentMap?.Key ?? "outside-port"} installed={_installed} camera={(current != null ? current.name : "none")} owned={(_effect != null)} activeTransaction={(_active != null)}");
                if (prism != null)
                {
                    var values = Values.Read(prism);
                    Plugin.Log.LogInfo($"[ArenaMapPort][HDR] current component fields: exposure {values.ExposureText}; tone {values.ToneText}; child={prism.isChildPrism} exposureMaster={(prism.m_MasterEffectExposure != null)}");
                }
                Plugin.Log.LogInfo($"[ArenaMapPort][HDR] observed tone keyword masks (Filmic=1 RomB=2 ACES=4): globalNow={ReadToneMask()} materialNow={(prism != null && prism.m_Material != null ? ReadToneMask(prism.m_Material) : -1)}");
                var sample = _last;
                if (!_hasLast || !ReferenceEquals(sample.Effect, prism))
                { Plugin.Log.LogInfo("[ArenaMapPort][HDR] no owned completed shader upload sample"); return; }
                Plugin.Log.LogInfo($"[ArenaMapPort][HDR] sample map={sample.Map} frame={sample.Frame} age={Time.frameCount - sample.Frame} guardedInput={sample.InvalidInput}: native exposure {sample.Native.ExposureText}; applied exposure {sample.Adjusted.ExposureText}");
                Plugin.Log.LogInfo($"[ArenaMapPort][HDR] native tone {sample.Native.ToneText}; applied tone {sample.Adjusted.ToneText}");
                Plugin.Log.LogInfo($"[ArenaMapPort][HDR] uploaded material: exposureUsed={sample.Adjusted.Exposure} grey={sample.UploadedGrey:0.######} range=[{sample.UploadedLower:0.####},{sample.UploadedUpper:0.####}] speed={sample.UploadedSpeed:0.####}; toneUsed={sample.Adjusted.Tonemap} curve={VectorText(sample.UploadedCurve)} secondary={VectorText(sample.UploadedSecondary)}; toneMaskExpected={ExpectedToneMask(sample.Adjusted)} observed={sample.UploadedToneMask}; afterRenderGlobalExpected={sample.ExpectedGlobalToneMask} observed={sample.ObservedGlobalToneMask}; fieldTransactionActive={sample.FieldsActive} renderTransactionClosed={sample.Finished}");
            }
            catch (Exception e) { ReportFailure(e); }
        }

        static float Setting(float value, float min, float max, float fallback, ref bool invalid)
        {
            if (!Finite(value) || value < min || value > max) { invalid = true; return fallback; }
            return value;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static int ExpectedToneMask(Values values) => values.Tonemap && (int)values.Type >= 0 && (int)values.Type < ToneKeywords.Length
            ? 1 << (int)values.Type : 0;
        static int ReadToneMask(Material material = null)
        {
            int mask = 0;
            for (int i = 0; i < ToneKeywords.Length; i++)
                if (material != null ? material.IsKeywordEnabled(ToneKeywords[i]) : Shader.IsKeywordEnabled(ToneKeywords[i]))
                    mask |= 1 << i;
            return mask;
        }
        static bool ValidVector(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) &&
            Mathf.Abs(value.x) <= 16f && Mathf.Abs(value.y) <= 16f && Mathf.Abs(value.z) <= 16f;
        static string VectorText(Vector3 value) => $"({value.x:0.######},{value.y:0.######},{value.z:0.######})";

        static void ReportFailure(Exception error)
        {
            if (_failureLogged) return;
            _failureLogged = true;
            Plugin.Log.LogError($"[ArenaMapPort][HDR] image tuning hook failed (further hook error logging suppressed): {error}");
        }
    }
}
