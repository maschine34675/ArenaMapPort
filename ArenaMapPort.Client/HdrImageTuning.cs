using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// only the current gameplay camera's own materials of a released map (Equator, Bay5, AirPit, Bowl)
    /// receive adjusted values.
    /// Native evidence: PrismEffects.cs 1063-1380, 1611-1784.
    /// </summary>
    internal static class HdrImageTuning
    {
        internal enum ToneProfile { Original, Filmic, RomB, ACES, Off }

        // The installed ConfigurationManager requires this exact type name, then copies public
        // fields by reflection. No assembly dependency or additional saved scalar keys are needed.
        sealed class ConfigurationManagerAttributes
        {
            public bool? IsAdvanced = true;
            public Action<ConfigEntryBase> CustomDrawer;
        }

        sealed class CurveDrawer
        {
            readonly string _map, _section, _controlPrefix;
            readonly bool _secondary;
            readonly Dictionary<string, CurveDrawer> _sectionDrawers = new Dictionary<string, CurveDrawer>();
            readonly string[] _text = new string[3];
            Vector3 _observed;
            bool _initialized;

            internal CurveDrawer(string map, bool secondary, string section = null)
            {
                _map = map; _secondary = secondary; _section = section;
                _controlPrefix = "ArenaMapPort.Tone." + (section ?? map) + (secondary ? ".Secondary." : ".Primary.");
            }

            internal void Draw(ConfigEntryBase entry)
            {
                if (!(entry is ConfigEntry<Vector3> curve) || !Configs.TryGetValue(_map, out var settings)) return;
                // ConfigurationManager copies this delegate when profile entries are bound. Keep
                // edit buffers and IMGUI focus names separate for each displayed profile section.
                // Editing Night while a Day raid is active must still change only the Night tuple.
                if (_section == null)
                {
                    string section = entry.Definition.Section;
                    if (!_sectionDrawers.TryGetValue(section, out var drawer))
                        _sectionDrawers[section] = drawer = new CurveDrawer(_map, _secondary, section);
                    drawer.Draw(entry);
                    return;
                }
                var tone = EquatorProfiles.ForSection(settings.Tone, _section);
                var customCurve = EquatorProfiles.ForSection(settings.CustomCurve, _section);
                var primaryCurve = EquatorProfiles.ForSection(settings.Curve, _section);
                var secondaryCurve = EquatorProfiles.ForSection(settings.SecondaryCurve, _section);
                var value = curve.Value;
                for (int axis = 0; axis < 3; axis++)
                    if (!_initialized || !value[axis].Equals(_observed[axis])) _text[axis] = Number(value[axis]);
                _initialized = true;
                GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
                try
                {
                    for (int axis = 0; axis < 3; axis++) value[axis] = DrawAxis(axis, value[axis]);
                    // Rendering a field never normalizes or rewrites an existing saved value.
                    if (!value.Equals(curve.Value)) curve.Value = value;
                    _observed = value;

                    bool explicitProfile = IsExplicitProfile(tone.Value);
                    if (!_secondary)
                    {
                        bool wasEnabled = GUI.enabled;
                        GUI.enabled = wasEnabled && explicitProfile;
                        try
                        {
                            string profileName = explicitProfile ? tone.Value.ToString() : "selected profile";
                            if (GUILayout.Button(new GUIContent("Use " + profileName + " preset",
                                "Replace BOTH saved vectors with this profile's native preset and enable TonemapCustomCurve. The standard Reset button resets only this vector to this map's config default.")))
                            {
                                var preset = new Values();
                                SetTonePreset(ref preset, tone.Value);
                                primaryCurve.Value = preset.Curve;
                                secondaryCurve.Value = preset.SecondaryCurve;
                                customCurve.Value = true;
                                GUI.FocusControl(null);
                            }
                        }
                        finally { GUI.enabled = wasEnabled; }
                    }
                    // Keep one status row in every state so a live toggle or preset click does
                    // not change the layout's control count halfway through an IMGUI event.
                    GUILayout.Label(!explicitProfile ? "Inactive: select Filmic, RomB or ACES." :
                        customCurve.Value ? "Custom curve active." : "Inactive: enable TonemapCustomCurve.");
                }
                finally { GUILayout.EndVertical(); }
            }

            float DrawAxis(int axis, float value)
            {
                GUILayout.BeginHorizontal();
                try
                {
                    GUILayout.Label(new GUIContent(((char)('X' + axis)).ToString(),
                        "Raw native coefficient, -16 to 16. Slider movement is finer near zero; the number box accepts exact values. Meanings depend on the selected profile."), GUILayout.Width(18f));
                    // A signed cubic scale gives useful travel to small native constants such as
                    // Filmic's 0.004, while still covering the existing full [-16,16] domain.
                    float safe = Finite(value) ? Mathf.Clamp(value, -16f, 16f) : 0f;
                    float position = (float)(Math.Sign(safe) * Math.Pow(Math.Abs(safe) / 16d, 1d / 3d));
                    bool wasChanged = GUI.changed;
                    GUI.changed = false;
                    float moved = GUILayout.HorizontalSlider(position, -1f, 1f, GUILayout.ExpandWidth(true));
                    bool sliderChanged = GUI.changed;
                    GUI.changed |= wasChanged;
                    if (sliderChanged && !moved.Equals(position))
                    {
                        value = Mathf.Clamp(16f * moved * moved * moved, -16f, 16f);
                        _text[axis] = Number(value);
                    }

                    string control = _controlPrefix + axis;
                    Color oldColor = GUI.color;
                    bool valid = TryCoefficient(_text[axis], out _);
                    if (!valid) GUI.color = new Color(1f, 0.55f, 0.55f, oldColor.a);
                    GUI.SetNextControlName(control);
                    string edited;
                    try { edited = GUILayout.TextField(_text[axis], GUILayout.Width(85f)); }
                    finally { GUI.color = oldColor; }
                    if (edited != _text[axis])
                    {
                        _text[axis] = edited;
                        if (TryCoefficient(edited, out float parsed)) value = parsed;
                    }
                    // Keep intermediate '-' / exponent / empty input while typing. Invalid input
                    // never reaches the config, and abandoning it shows the last accepted value.
                    if (GUI.GetNameOfFocusedControl() != control && !TryCoefficient(_text[axis], out _))
                        _text[axis] = Number(value);
                    return value;
                }
                finally { GUILayout.EndHorizontal(); }
            }

            static string Number(float value) => value.ToString("G7", CultureInfo.InvariantCulture);

            static bool TryCoefficient(string text, out float value)
            {
                bool parsed = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                    float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
                return parsed && Finite(value) && value >= -16f && value <= 16f;
            }
        }

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
            // Every released map (Equator, Bay5, AirPit, Bowl) gets the sections; only Equator carries non-neutral
            // defaults. The neutral rows of AirPit and Bowl (Original tonemap, 0 EV, range/speed 1) change nothing until tuned.
            foreach (var map in ArenaMapRegistry.All) Bind(map);
            var render = AccessTools.Method(typeof(PrismEffects), nameof(PrismEffects.OnRenderImageInternal),
                new[] { typeof(RenderTexture), typeof(RenderTexture) });
            var upload = AccessTools.Method(typeof(PrismEffects), nameof(PrismEffects.UpdateShaderValues), Type.EmptyTypes);
            if (!HarmonyPatches.Verify(render, typeof(PrismEffects), nameof(PrismEffects.OnRenderImageInternal)) ||
                !HarmonyPatches.Verify(upload, typeof(PrismEffects), nameof(PrismEffects.UpdateShaderValues))) return;
            if (render.ReturnType != typeof(void) || upload.ReturnType != typeof(void) || render.IsStatic || upload.IsStatic)
            {
                Plugin.Log.LogError("[HDR] unexpected native render/upload signature; image tuning disabled");
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
            Plugin.Log.LogDebug("[HDR] per-map exposure/tone controls installed after native environment overwrite; Equator defaults to custom Filmic, Bay5, AirPit and Bowl preserve native tone");
        }

        static void Bind(ArenaMapDescriptor map)
        {
            var section = $"Lighting ({map.Key})";
            var config = Plugin.Instance.Settings;
            bool equator = map.Key == ArenaMapRegistry.Equator.Key;
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
                Tone = config.Bind(section, "TonemapProfile", equator ? ToneProfile.Filmic : ToneProfile.Original,
                    "Original preserves native tonemapping and its current parameters. Filmic, RomB and ACES enable that native profile, using its native preset unless TonemapCustomCurve is on. Equator defaults to the accepted custom Filmic curve; Bay5, AirPit and Bowl keep Original. Off disables tonemapping. Live."),
                CustomCurve = config.Bind(section, "TonemapCustomCurve", equator,
                    new ConfigDescription("Use the two advanced raw native parameter vectors. Requires an explicit Filmic, RomB or ACES profile; Original and Off ignore them. Invalid or out-of-range vectors fall back to that profile. Live.", null, new ConfigurationManagerAttributes())),
                Curve = config.Bind(section, "TonemapCurve", equator ? new Vector3(7f, 0.35f, 0.7f) : new Vector3(-1f, 2.72f, 0.15f),
                    new ConfigDescription("Raw Prism toneValues (x, y, z); meanings depend on the selected native shader profile. Sliders cover [-16,16] with finer movement near zero; number boxes allow exact values. Used only with TonemapCustomCurve and an explicit profile. Use the preset button to copy both native vectors and enable custom tuning. Live.", null,
                        new ConfigurationManagerAttributes { CustomDrawer = new CurveDrawer(map.Key, false).Draw })),
                SecondaryCurve = config.Bind(section, "TonemapSecondaryCurve", equator ? new Vector3(-0.02f, 0.18f, 0f) : Vector3.zero,
                    new ConfigDescription("Raw Prism secondaryToneValues (x, y, z); meanings depend on the selected native shader profile. Sliders cover [-16,16] with finer movement near zero; number boxes allow exact values. Used only with TonemapCustomCurve and an explicit profile. Live.", null,
                        new ConfigurationManagerAttributes { CustomDrawer = new CurveDrawer(map.Key, true).Draw }))
            };
            var settings = Configs[map.Key];
            if (equator)
            {
                var revision = config.Bind(section, "HdrDefaultsRevision", 0,
                    new ConfigDescription("Internal — once-only Equator tonemap default migration. Leave it alone.",
                        null, System.ComponentModel.BrowsableAttribute.No));
                if (revision.Value < 1)
                {
                    // The profile and both vectors are a single preset: only migrate the complete
                    // old neutral tuple. Even a partially customized tuple must remain untouched.
                    bool moved = settings.Tone.Value == ToneProfile.Original && !settings.CustomCurve.Value &&
                        settings.Curve.Value.Equals(new Vector3(-1f, 2.72f, 0.15f)) &&
                        settings.SecondaryCurve.Value.Equals(Vector3.zero);
                    if (moved)
                    {
                        settings.Tone.Value = (ToneProfile)settings.Tone.DefaultValue;
                        settings.CustomCurve.Value = (bool)settings.CustomCurve.DefaultValue;
                        settings.Curve.Value = (Vector3)settings.Curve.DefaultValue;
                        settings.SecondaryCurve.Value = (Vector3)settings.SecondaryCurve.DefaultValue;
                    }
                    revision.Value = 1;
                    Plugin.Log.LogDebug($"[HDR] {map.Key}: config re-seeded to HDR defaults revision 1 " +
                        $"(complete neutral tone tuple moved={moved}; profile={settings.Tone.Value}, custom={settings.CustomCurve.Value})");
                }
            }
            EquatorProfiles.Subscribe(settings.Compensation, RequestSample);
            EquatorProfiles.Subscribe(settings.Range, RequestSample);
            EquatorProfiles.Subscribe(settings.Speed, RequestSample);
            EquatorProfiles.Subscribe(settings.Tone, RequestSample);
            EquatorProfiles.Subscribe(settings.CustomCurve, RequestSample);
            EquatorProfiles.Subscribe(settings.Curve, RequestSample);
            EquatorProfiles.Subscribe(settings.SecondaryCurve, RequestSample);
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
            // Finish every open scope, innermost first. FinishRender only advances _active when the scope
            // it finishes IS _active and returns at once for a scope that already finished; a scope that
            // finished out of order (outer before inner) would leave _active on a finished scope and a
            // bare `while (_active != null) FinishRender(_active)` would spin on the main thread. Not
            // constructible from current EFT call paths (PrismEffects never re-enters its render, so the
            // chain is one deep and finalizes LIFO; review 2026-10-14) - a safety net, identical in the
            // normal case. Step past such a scope ourselves and bound the walk; scopes dropped at the bound
            // are not Finished, so their own finalizers still restore their fields.
            for (int i = 0; _active != null && i < 64; i++)
            {
                var scope = _active;
                FinishRender(scope);
                if (ReferenceEquals(_active, scope))
                {
                    Plugin.Log.LogDebug("[HDR] release stepped past an already-finished render scope");
                    _active = scope.Parent;
                }
            }
            if (_active != null)
            {
                Plugin.Log.LogWarning("[HDR] render scope chain did not drain within 64 steps " +
                                      "(cycle or overlong chain); dropping the rest");
                _active = null;
            }
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
            float ev = Setting(EquatorProfiles.Value(config.Compensation), -4f, 4f, 0f, ref invalid);
            float range = Setting(EquatorProfiles.Value(config.Range), 0.25f, 2f, 1f, ref invalid);
            float speed = Setting(EquatorProfiles.Value(config.Speed), 0.1f, 4f, 1f, ref invalid);
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
            var tone = EquatorProfiles.Value(config.Tone);
            if ((int)tone < (int)ToneProfile.Original || (int)tone > (int)ToneProfile.Off)
            { invalid = true; tone = ToneProfile.Original; }
            if (tone == ToneProfile.Original) return value;
            if (tone == ToneProfile.Off) { value.Tonemap = false; return value; }
            value.Tonemap = true;
            SetTonePreset(ref value, tone);
            if (EquatorProfiles.Value(config.CustomCurve))
            {
                if (ValidVector(EquatorProfiles.Value(config.Curve)) && ValidVector(EquatorProfiles.Value(config.SecondaryCurve)))
                { value.Curve = EquatorProfiles.Value(config.Curve); value.SecondaryCurve = EquatorProfiles.Value(config.SecondaryCurve); }
                else invalid = true;
            }
            return value;
        }

        static bool IsExplicitProfile(ToneProfile tone) =>
            tone == ToneProfile.Filmic || tone == ToneProfile.RomB || tone == ToneProfile.ACES;

        static void SetTonePreset(ref Values value, ToneProfile tone)
        {
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
                if (!DeveloperOptions.Enabled || (!_sampleRequested && Time.unscaledTime < _nextSampleTime)) return;
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
            if (!DeveloperOptions.Enabled) return;
            _sampleRequested = true;
            try
            {
                var current = CameraManager.Exist ? CameraManager.Instance.Camera : null;
                var prism = current != null ? current.GetComponent<PrismEffects>() : null;
                Plugin.Log.LogDebug($"[HDR] map={ArenaMapRaid.CurrentMap?.Key ?? "outside-port"} installed={_installed} camera={(current != null ? current.name : "none")} owned={(_effect != null)} activeTransaction={(_active != null)}");
                if (prism != null)
                {
                    var values = Values.Read(prism);
                    Plugin.Log.LogDebug($"[HDR] current component fields: exposure {values.ExposureText}; tone {values.ToneText}; child={prism.isChildPrism} exposureMaster={(prism.m_MasterEffectExposure != null)}");
                }
                Plugin.Log.LogDebug($"[HDR] observed tone keyword masks (Filmic=1 RomB=2 ACES=4): globalNow={ReadToneMask()} materialNow={(prism != null && prism.m_Material != null ? ReadToneMask(prism.m_Material) : -1)}");
                var sample = _last;
                if (!_hasLast || !ReferenceEquals(sample.Effect, prism))
                { Plugin.Log.LogDebug("[HDR] no owned completed shader upload sample"); return; }
                Plugin.Log.LogDebug($"[HDR] sample map={sample.Map} frame={sample.Frame} age={Time.frameCount - sample.Frame} guardedInput={sample.InvalidInput}: native exposure {sample.Native.ExposureText}; applied exposure {sample.Adjusted.ExposureText}");
                Plugin.Log.LogDebug($"[HDR] native tone {sample.Native.ToneText}; applied tone {sample.Adjusted.ToneText}");
                Plugin.Log.LogDebug($"[HDR] uploaded material: exposureUsed={sample.Adjusted.Exposure} grey={sample.UploadedGrey:0.######} range=[{sample.UploadedLower:0.####},{sample.UploadedUpper:0.####}] speed={sample.UploadedSpeed:0.####}; toneUsed={sample.Adjusted.Tonemap} curve={VectorText(sample.UploadedCurve)} secondary={VectorText(sample.UploadedSecondary)}; toneMaskExpected={ExpectedToneMask(sample.Adjusted)} observed={sample.UploadedToneMask}; afterRenderGlobalExpected={sample.ExpectedGlobalToneMask} observed={sample.ObservedGlobalToneMask}; fieldTransactionActive={sample.FieldsActive} renderTransactionClosed={sample.Finished}");
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
            Plugin.Log.LogError($"[HDR] image tuning hook failed (further hook error logging suppressed): {error}");
        }
    }
}
