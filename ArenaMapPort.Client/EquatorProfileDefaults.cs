using System.Collections.Generic;
using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>Accepted GPU-2 night recipe captured 2026-10-07. Only absent profile keys use it.</summary>
    internal static class EquatorProfileDefaults
    {
        internal static readonly IReadOnlyDictionary<string, string> Appearance = new Dictionary<string, string>
        {
            ["Lighting (arena_equator)/KeepFillLight"] = "false",
            ["Lighting (arena_equator)/GroundSmoothness"] = "1",
            ["Lighting (arena_equator)/ReflectionIntensity"] = "1",
            ["Lighting (arena_equator)/AmbientIntensity"] = "4",
            ["Lighting (arena_equator)/IndoorFloorSmoothness"] = "0.5",
            ["Lighting (arena_equator)/IndoorReflectionStrength"] = "1",
            ["Lighting (arena_equator)/IndoorSurfaceSmoothness"] = "0.5",
            ["Lighting (arena_equator)/IndoorSurfaceReflectionStrength"] = "0.2",
            ["Lighting (arena_equator)/ReflectiveSpecularCap"] = "1",
            ["Lighting (arena_equator)/AmbientColorMultiplier"] = "0.5",
            ["Lighting (arena_equator)/GlobalLightIntensityMultiplier"] = "0.5",
            ["Lighting (arena_equator)/SunIntensityMultiplier"] = "0.2",
            ["Lighting (arena_equator)/SunColorWhiteBlend"] = "0.1",
            ["Lighting (arena_equator)/FogDensity"] = "0",
            ["Lighting (arena_equator)/FillLightIntensity"] = "0.2",
            ["Lighting (arena_equator)/NightSunMultiplier"] = "0",
            ["Lighting (arena_equator)/BloomThreshold"] = "2",
            ["Lighting (arena_equator)/BloomIntensity"] = "0.35",
            ["Lighting (arena_equator)/AnamorphicFlare"] = "true",
            ["Lighting (arena_equator)/AnamorphicFlareHDR"] = "true",
            ["Lighting (arena_equator)/AnamorphicFlareIntensity"] = "0.3",
            ["Lighting (arena_equator)/AnamorphicFlareLength"] = "4.2",
            ["Lighting (arena_equator)/AnamorphicFlareThreshold"] = "0.17",
            ["Lighting (arena_equator)/AnamorphicFlareTint"] = "FF268FCC",
            ["Lighting (arena_equator)/ExposureCompensationEV"] = "0.3",
            ["Lighting (arena_equator)/ExposureRangeMultiplier"] = "1",
            ["Lighting (arena_equator)/ExposureSpeedMultiplier"] = "1",
            ["Lighting (arena_equator)/TonemapProfile"] = "Filmic",
            ["Lighting (arena_equator)/TonemapCustomCurve"] = "true",
            ["Lighting (arena_equator)/TonemapCurve"] = "{\"x\":7.0,\"y\":0.3499999940395355,\"z\":0.699999988079071}",
            ["Lighting (arena_equator)/TonemapSecondaryCurve"] = "{\"x\":-0.019999999552965165,\"y\":0.18000000715255738,\"z\":0.0}",
            ["Lighting (arena_equator)/NativeMultiFlare"] = "true",
            ["Lighting (arena_equator)/NativeMultiFlareIntensity"] = "0.6",
            ["Lighting (arena_equator)/HandsAmbientHighlightMultiplier"] = "1",
            ["LightingLab (arena_equator)/AmbientOverride"] = "Custom",
            ["LightingLab (arena_equator)/AmbientCustomColor"] = "02131AFF",
            ["LightingLab (arena_equator)/GlobalReflection"] = "Rig",
            ["LightingLab (arena_equator)/SSRFactor"] = "1",
            ["LightingLab (arena_equator)/WaterLevelOverrideEnabled"] = "false",
            ["LightingLab (arena_equator)/WaterLevelOverrideValue"] = "0.6",
            ["LightingLab (arena_equator)/RainOff"] = "false",
            ["LightingLab (arena_equator)/GroundSmoothnessMultiplier"] = "1",
            ["LightingLab (arena_equator)/SunIntensityMultiplier"] = "1",
        };

        static readonly IReadOnlyDictionary<string, string> NightEffects = new Dictionary<string, string>
        {
            ["Enabled"] = "true",
            ["LightIntensity"] = "1.5",
            ["StripBrightness"] = "0.6",
            ["CyanColor"] = "05B2FFFF",
            ["MagentaColor"] = "FF064CFF",
            ["Rain"] = "true",
            ["HazeStrength"] = "0.2398122",
            ["SmokeDensity"] = "1",
            ["FlickerStrength"] = "0.8",
            ["FlickerFrequency"] = "3",
            ["PostFx"] = "true",
            ["DistanceHaze"] = "0.01",
            ["AccentLightIntensity"] = "1",
            ["WetFloorStrength"] = "0.8",
        };

        internal static T NightEffect<T>(string key, T fallback) =>
            NightEffects.TryGetValue(key, out var value) ? TomlTypeConverter.ConvertToValue<T>(value) : fallback;
    }
}
