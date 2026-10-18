using System;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;
using UnityStandardAssets.ImageEffects;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The early Cam2 fallback rendered a thin horizontal flare using BloomAndFlares, not
    /// UltimateBloom. Reserve's active anamorphic pass disables lensflares and HDR. Restore
    /// Cam2's flare color/stretch/prefilter on that existing pass for Equator; preserve its
    /// broad-bloom gain and the rest of the donor camera. No added image effect or material.
    /// Source: resources.assets component 40780 vs sharedassets15.assets component 1185.
    /// Native comparisons and lifecycle evidence: docs/progress/equator-anamorphic-hdr.md.
    /// </summary>
    internal static class AnamorphicFlareTuning
    {
        const float DefaultLength = 3.5f;
        const float DefaultThreshold = 0.155f;
        const float MaximumLength = 10f;
        // The native anamorphic pass divides by (1 - lensflareThreshold), including in HDR.
        const float MaximumThreshold = 0.99f;
        static readonly Color DefaultTint = new Color(0.670588f, 0.862745f, 1f, 0.75f);
        static readonly Color PersistedDefaultTint = (Color32)DefaultTint;

        sealed class Original
        {
            internal BloomAndFlares Effect;
            internal bool Flares;
            internal HDRBloomMode Hdr;
            internal float Spread, Threshold, Intensity, FlareThreshold, Width;
            internal int BloomIterations, FlareIterations;
            internal Color Color;
            internal UltimateBloom Ultimate;
            internal bool UltimateFlare;
            internal PrismEffects Prism;
            internal bool Relocate, RenderLogged;
            internal int RenderedFrame = -1;

            internal Original(BloomAndFlares effect, UltimateBloom ultimate)
            {
                Effect = effect; Flares = effect.lensflares; Hdr = effect.hdr;
                Spread = effect.sepBlurSpread; Threshold = effect.bloomThreshold;
                BloomIterations = effect.bloomBlurIterations;
                Intensity = effect.lensflareIntensity; FlareThreshold = effect.lensflareThreshold;
                Width = effect.hollyStretchWidth; FlareIterations = effect.hollywoodFlareBlurIterations;
                Color = effect.flareColorA; Ultimate = ultimate;
                UltimateFlare = ultimate != null && ultimate.m_UseAnamorphicFlare;
                Prism = effect.GetComponent<PrismEffects>();
                // The old fallback already orders this pass before Prism. Only relocate the
                // donor's later pass, retaining native initialization and resource ownership.
                bool passedPrism = false;
                foreach (var component in effect.GetComponents<MonoBehaviour>())
                {
                    if (component == Prism) passedPrism = true;
                    if (component == effect) { Relocate = passedPrism; break; }
                }
            }

            internal void Restore()
            {
                if (Effect != null)
                {
                    Effect.lensflares = Flares; Effect.hdr = Hdr;
                    Effect.sepBlurSpread = Spread; Effect.bloomThreshold = Threshold;
                    Effect.bloomBlurIterations = BloomIterations;
                    Effect.lensflareIntensity = Intensity; Effect.lensflareThreshold = FlareThreshold;
                    Effect.hollyStretchWidth = Width; Effect.hollywoodFlareBlurIterations = FlareIterations;
                    Effect.flareColorA = Color;
                }
                if (Ultimate != null) Ultimate.m_UseAnamorphicFlare = UltimateFlare;
            }
        }

        static ConfigEntry<bool> _enabled, _hdr;
        static ConfigEntry<float> _intensity, _length, _threshold;
        static ConfigEntry<Color> _tint;
        static readonly Dictionary<BloomAndFlares, Original> Originals = new Dictionary<BloomAndFlares, Original>();
        static readonly Dictionary<PrismEffects, Original> BeforeTonemap = new Dictionary<PrismEffects, Original>();
        static Action<BloomAndFlares, RenderTexture, RenderTexture> _render;
        static AccessTools.FieldRef<UnityStandardAssets.ImageEffects.PostEffectsBase, SSAAPropagator> Propagator;
        static bool _rendering;

        internal static bool HasRestoredCamera => Originals.Count > 0 && ArenaMapRaid.CurrentMap == ArenaMapRegistry.Equator;
        internal static bool IsRestored(BloomAndFlares effect) => effect != null && Originals.ContainsKey(effect);

        internal static void InstallRenderHooks(Harmony harmony)
        {
            var signature = new[] { typeof(RenderTexture), typeof(RenderTexture) };
            var flare = AccessTools.Method(typeof(BloomAndFlares), "OnRenderImage", signature);
            var prism = AccessTools.Method(typeof(PrismEffects), nameof(PrismEffects.OnRenderImage), signature);
            if (!HarmonyPatches.Verify(flare, typeof(BloomAndFlares), "OnRenderImage") ||
                !HarmonyPatches.Verify(prism, typeof(PrismEffects), "OnRenderImage")) return;
            var field = AccessTools.Field(typeof(UnityStandardAssets.ImageEffects.PostEffectsBase), "_ssaaPropagator");
            if (field == null || field.FieldType != typeof(SSAAPropagator))
            {
                Plugin.Log.LogError("[Flare] native SSAA field missing; flare restoration disabled");
                return;
            }
            Propagator = AccessTools.FieldRefAccess<UnityStandardAssets.ImageEffects.PostEffectsBase, SSAAPropagator>(field);
            harmony.Patch(flare, prefix: new HarmonyMethod(typeof(AnamorphicFlareTuning), nameof(BeforeNativeFlare)));
            harmony.Patch(prism, prefix: new HarmonyMethod(typeof(AnamorphicFlareTuning), nameof(BeforePrism)));
            _render = (Action<BloomAndFlares, RenderTexture, RenderTexture>)Delegate.CreateDelegate(
                typeof(Action<BloomAndFlares, RenderTexture, RenderTexture>), flare);
            MuzzleFlareMask.Install(harmony, flare);
        }

        static void BeforePrism(PrismEffects __instance, RenderTexture source, RenderTexture destination)
        {
            if (_rendering || _render == null || !BeforeTonemap.TryGetValue(__instance, out var original) ||
                original.Effect == null || !original.Effect.enabled || Propagator(original.Effect) == null) return;
            try
            {
                // BSG's native pass reads/writes and swaps SSAA's current HDR buffers itself.
                // Prism has not called GetSourceDestinationHDRToLDR yet. No temporary texture,
                // copied shader, per-frame reflection or additional effect component is needed.
                _rendering = true;
                _render(original.Effect, source, destination);
                original.RenderedFrame = Time.frameCount;
                if (!original.RenderLogged)
                {
                    original.RenderLogged = true;
                    Plugin.Log.LogDebug("[Flare] rendered native anamorphic pass before Prism HDR-to-LDR; later pass bypassed");
                }
            }
            catch (Exception e)
            {
                original.Restore();
                BeforeTonemap.Remove(__instance);
                Plugin.Log.LogError($"[Flare] pre-tonemap render failed; restored camera defaults: {e}");
            }
            finally { _rendering = false; }
        }

        static bool BeforeNativeFlare(BloomAndFlares __instance, RenderTexture source, RenderTexture destination)
        {
            if (_rendering || !Originals.TryGetValue(__instance, out var original) ||
                !original.Relocate || original.RenderedFrame != Time.frameCount) return true;
            // Unity still expects its ordinary image-effect destination to be written. The real
            // SSAA buffers already contain this pass and must not be swapped a second time.
            Graphics.Blit(source, destination);
            return false;
        }

        internal static void Install()
        {
            var section = $"Lighting ({ArenaMapRegistry.Equator.Key})";
            _enabled = Plugin.Instance.Settings.Bind(section, "AnamorphicFlare", true,
                "Restore the early Equator camera's thin horizontal light streaks on the donor camera. " +
                "Uses its existing BloomAndFlares pass; replaces UltimateBloom's broader anamorphic streak. " +
                "Off restores the original camera settings. Live.");
            _hdr = Plugin.Instance.Settings.Bind(section, "AnamorphicFlareHDR", true,
                "Use HDR intermediate buffers and additive blending for the restored flare, like the early camera. " +
                "Off restores the camera's own HDR mode. Only applies while AnamorphicFlare is on. " +
                "Camera HDR is already enabled; this is not HDR monitor output. Live.");
            _intensity = Plugin.Instance.Settings.Bind(section, "AnamorphicFlareIntensity", 0.4f,
                new ConfigDescription("Horizontal flare strength. 0.4 is the accepted Equator Filmic tuning; " +
                    "0.1 is the early camera's subtler value. " +
                    "0 removes the streak while retaining the HDR/prefilter settings. Live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            _length = Plugin.Instance.Settings.Bind(section, "AnamorphicFlareLength", DefaultLength,
                new ConfigDescription("Horizontal stretch of the restored light streak. " +
                    "3.5 preserves the existing length; lower values shorten it and higher values lengthen it. " +
                    "Only applies while AnamorphicFlare is on. Live.",
                    new AcceptableValueRange<float>(0f, MaximumLength)));
            _threshold = Plugin.Instance.Settings.Bind(section, "AnamorphicFlareThreshold", DefaultThreshold,
                new ConfigDescription("Light threshold for the restored horizontal streak. " +
                    "0.155 preserves the existing selection; lower values admit dimmer highlights, " +
                    "higher values restrict the streak to brighter highlights. " +
                    "Only applies while AnamorphicFlare is on. Live.",
                    new AcceptableValueRange<float>(0f, MaximumThreshold)));
            _tint = Plugin.Instance.Settings.Bind(section, "AnamorphicFlareTint", DefaultTint,
                "Color of the restored horizontal streak. The default preserves its blue-white tint. " +
                "Alpha also multiplies AnamorphicFlareIntensity: 0 hides the streak, 0.75 preserves its " +
                "existing strength, and 1 uses the full configured strength. RGBA channels are limited to 0-1. " +
                "Only applies while AnamorphicFlare is on. Live.");
            var revision = Plugin.Instance.Settings.Bind(section, "AnamorphicDefaultsRevision", 0,
                new ConfigDescription("Internal — once-only Equator flare default migration. Leave it alone.",
                    null, System.ComponentModel.BrowsableAttribute.No));
            if (revision.Value < 1)
            {
                bool moved = _intensity.Value == 0.25f;
                if (moved) _intensity.Value = 0.4f;
                revision.Value = 1;
                Plugin.Log.LogDebug($"[AnamorphicFlare] config re-seeded to defaults revision 1 " +
                    $"(intensity moved={moved}; intensity={_intensity.Value:0.00})");
            }
            EquatorProfiles.Subscribe(_enabled, (_, __) => ApplyToCurrentCamera("setting changed"));
            EquatorProfiles.Subscribe(_hdr, (_, __) => ApplyToCurrentCamera("setting changed"));
            EquatorProfiles.Subscribe(_intensity, (_, __) => ApplyToCurrentCamera("setting changed"));
            EquatorProfiles.Subscribe(_length, (_, __) => ApplyToCurrentCamera("setting changed"));
            EquatorProfiles.Subscribe(_threshold, (_, __) => ApplyToCurrentCamera("setting changed"));
            EquatorProfiles.Subscribe(_tint, (_, __) => ApplyToCurrentCamera("setting changed"));
        }

        static float ClampFinite(float value, float fallback, float maximum)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, 0f, maximum);

        static Color ClampTint(Color tint)
        {
            // BepInEx saves Color as RRGGBBAA; its default alpha reloads as 191/255 instead
            // of 0.75. Restore the exact historical default when that saved color is read.
            if (tint == PersistedDefaultTint) return DefaultTint;
            return new Color(ClampFinite(tint.r, DefaultTint.r, 1f), ClampFinite(tint.g, DefaultTint.g, 1f),
                ClampFinite(tint.b, DefaultTint.b, 1f), ClampFinite(tint.a, DefaultTint.a, 1f));
        }

        internal static void ApplyToCurrentCamera(string reason)
        {
            try
            {
                if (ArenaMapRaid.CurrentMap != ArenaMapRegistry.Equator || _enabled == null || _render == null) return;
                if (!EquatorProfiles.Value(_enabled)) { Release(); return; }
                var camera = CameraManager.Exist ? CameraManager.Instance.Camera : null;
                if (camera == null) return;
                BloomAndFlares effect = null;
                // Reserve also contains a disabled ghosting pass: never activate or tune it.
                foreach (var candidate in camera.GetComponents<BloomAndFlares>())
                    if (candidate.enabled && candidate.lensflareMode == LensflareStyle34.Anamorphic)
                    { effect = candidate; break; }
                if (effect == null)
                {
                    Plugin.Log.LogWarning($"[Flare] '{camera.name}' has no enabled anamorphic BloomAndFlares pass");
                    return;
                }
                if (!Originals.TryGetValue(effect, out var original))
                    Originals.Add(effect, original = new Original(effect, camera.GetComponent<UltimateBloom>()));
                if (original.Relocate && original.Prism != null && _render != null)
                    BeforeTonemap[original.Prism] = original;
                effect.lensflares = true;
                effect.hdr = EquatorProfiles.Value(_hdr) ? HDRBloomMode.On : original.Hdr;
                // These form the source and vertical thickness of the stripe, not just its glow.
                effect.bloomThreshold = 0.77f;
                effect.sepBlurSpread = 1.89f;
                effect.bloomBlurIterations = 2;
                effect.lensflareThreshold = ClampFinite(EquatorProfiles.Value(_threshold), DefaultThreshold, MaximumThreshold);
                effect.hollyStretchWidth = ClampFinite(EquatorProfiles.Value(_length), DefaultLength, MaximumLength);
                effect.hollywoodFlareBlurIterations = 2;
                effect.lensflareIntensity = EquatorProfiles.Value(_intensity);
                effect.flareColorA = ClampTint(EquatorProfiles.Value(_tint));
                if (original.Ultimate != null) original.Ultimate.m_UseAnamorphicFlare = false;
                Plugin.Log.LogDebug($"[Flare] arena_equator '{camera.name}' ({reason}): " +
                    $"component={effect.GetInstanceID()} enabled={effect.enabled} lensflares={effect.lensflares} " +
                    $"mode={effect.lensflareMode} HDR={effect.hdr} cameraHDR={camera.allowHDR} " +
                    $"intensity={effect.lensflareIntensity:0.000} threshold={effect.lensflareThreshold:0.000} " +
                    $"spread={effect.sepBlurSpread:0.00} width={effect.hollyStretchWidth:0.00} " +
                    $"tint={effect.flareColorA.ToString("F3")} " +
                    $"bloom={effect.bloomThreshold:0.00}/{effect.bloomIntensity:0.00}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Flare] apply failed ({reason}): {e}");
            }
        }

        internal static void Release()
        {
            MuzzleFlareMask.Release();
            if (Originals.Count == 0) return;
            foreach (var original in Originals.Values) original.Restore();
            Plugin.Log.LogDebug($"[Flare] released {Originals.Count} camera state(s)");
            Originals.Clear();
            BeforeTonemap.Clear();
        }
    }
}
