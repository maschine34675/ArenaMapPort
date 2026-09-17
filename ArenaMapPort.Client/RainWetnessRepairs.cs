using System;
using BepInEx.Configuration;
using EFT.Weather;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// donor-settings-camera-weather §C.3/§C.4 (Tier 2 — default OFF/no-op, playtest-gated). Two
    /// independent, config-tunable mitigations for the 2026-09-17 playtest's blown-out/mirror-white wet
    /// ground, neither of them re-tested cleanly since the sun/dome bug was fixed (the plan's own
    /// framing — candidate fixes to A/B during a playtest, not settled ones):
    ///
    ///  - §C.3 WetRenderer intensity damper: <c>RainController.AbstractStateSummer.SetWetting</c>
    ///    (RainController.cs, decompiled) ramps <c>Controller._wetRenderer.Intensity</c> from 0 toward
    ///    <c>Mathf.Clamp01(WeatherCurve.Rain)</c> over real time every frame it runs — a Harmony postfix
    ///    on that method, not a one-shot field write, is the only way to scale it down continuously as it
    ///    ramps. <c>WetRendererIntensityMultiplier</c> defaults to 1.0 (no change) — this is the least-
    ///    tested of the four §C mitigations, ships fully inert.
    ///  - §C.4 puddle-height ceiling clamp: <c>RainController.PuddleWaterLevel</c> (a public, plain
    ///    <c>Vector2</c> FIELD on the MonoBehaviour, default <c>(0, 0.95)</c>) is the upper bound
    ///    <c>AbstractState.vmethod_15</c> lerps <c>_WaterLevel</c> toward every frame
    ///    (<c>Mathf.LerpUnclamped(puddleWaterLevel.x, puddleWaterLevel.y, laingWater)</c>). Unlike §C.3,
    ///    this needs no Harmony patch at all — it is a plain field on the harvested rig's own
    ///    RainController, set once per raid activation (<see cref="ApplyPuddleClamp"/>, called from
    ///    <see cref="WeatherRig.OnSceneLoaded"/> right after the rig itself activates) rather than the
    ///    plan's originally-suggested Harmony postfix on <c>vmethod_15</c> — strictly simpler for an
    ///    identical effect (a real Harmony patch on a value that is read, not computed, every frame would
    ///    only add reentrancy/timing surface for no benefit). Ships disabled (no clamp) by default —
    ///    <c>MaxLyingWaterOverrideEnabled=false</c> — since the plan explicitly deferred authoring real
    ///    per-map cover data (<c>WeatherObstacle</c>, §E) and this is a blunt stopgap for that gap, not a
    ///    settled fix. Review fix-ups (docs/progress/donor-settings-camera-weather.md) #1:
    ///    <see cref="ApplyPuddleClamp"/> now ALWAYS writes <c>PuddleWaterLevel</c> — restoring the
    ///    harvest-time original when disabled, not just skipping the write — because the RainController it
    ///    mutates lives on a session-persistent, <c>SetActive</c>-only rig clone (never
    ///    <c>Destroy()</c>'d, reused for every raid on this donor for the rest of the session); a bare
    ///    early return on the disabled path left a PREVIOUS raid's clamp permanently stuck once the config
    ///    was toggled back off.
    ///
    /// Both gated on <see cref="WeatherRig.IsActiveForCurrentMap"/> (RainController only exists inside a
    /// harvested rig) — §C.3's Harmony patch additionally gates on <see cref="ArenaMapRaid.IsLoaded"/> as
    /// its own first check, same reasoning as <see cref="WeatherTestOverride"/>'s critic-amended gate:
    /// Harmony fires an instance postfix for ANY <c>RainController.AbstractStateSummer</c> instance the
    /// patched method runs on, including a real vanilla Reserve/Laboratory/Customs raid played in the
    /// same session, not just ours.
    /// </summary>
    internal static class RainWetnessRepairs
    {
        static ConfigEntry<float> _wetRendererIntensityMultiplier;
        static ConfigEntry<bool> _maxLyingWaterOverrideEnabled;
        static ConfigEntry<float> _maxLyingWaterOverrideValue;
        static bool _installed;
        static bool _damperLogged;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _wetRendererIntensityMultiplier = Plugin.Instance.Config.Bind("Wetness", "WetRendererIntensityMultiplier", 1.0f,
                new ConfigDescription(
                    "Multiplies RainController's WetRenderer.Intensity (the ground-wetness/reflection " +
                    "driver) every frame it ramps, while a rig is active for this map. 1.0 = off/no change " +
                    "(Tier 2, ships disabled — the least-tested of the four wetness mitigations, no prior " +
                    "playtest evidence either way). Lower values (e.g. 0.5) reduce ground wetness/mirror " +
                    "reflection intensity without changing the rain visuals themselves.",
                    new AcceptableValueRange<float>(0f, 1f)));

            _maxLyingWaterOverrideEnabled = Plugin.Instance.Config.Bind("Wetness", "MaxLyingWaterOverrideEnabled", false,
                "Clamps RainController.PuddleWaterLevel's upper bound (authored 0.95) down to " +
                "MaxLyingWaterOverride, while a rig is active for this map. false = unclamped (today's " +
                "behavior). Blunt stopgap for the fact no ArenaMapPort map authors a real WeatherObstacle " +
                "for RainController's own cover-exclusion system (docs/progress/" +
                "donor-settings-camera-weather.md §Deferred) — Tier 2, ships disabled.");

            _maxLyingWaterOverrideValue = Plugin.Instance.Config.Bind("Wetness", "MaxLyingWaterOverride", 0.6f,
                new ConfigDescription(
                    "Replacement PuddleWaterLevel.y applied when MaxLyingWaterOverrideEnabled=true (authored " +
                    "default 0.95). Applies on the next raid load, not live.",
                    new AcceptableValueRange<float>(0f, 0.95f)));
        }

        internal static float WetRendererIntensityMultiplier => _wetRendererIntensityMultiplier != null ? _wetRendererIntensityMultiplier.Value : 1f;
        internal static bool MaxLyingWaterOverrideEnabled => _maxLyingWaterOverrideEnabled != null && _maxLyingWaterOverrideEnabled.Value;
        internal static float MaxLyingWaterOverrideValue => _maxLyingWaterOverrideValue != null ? _maxLyingWaterOverrideValue.Value : 0.6f;

        // §C.4 — called once per raid from WeatherRig.OnSceneLoaded, right after the rig itself activates
        // (rig.Kind == RigKind.Full only — RainController doesn't exist on a Simplified rig).
        //
        // Review fix-ups (docs/progress/donor-settings-camera-weather.md) #1: `originalPuddleWaterLevelY`
        // (WeatherRig.HarvestedRig.OriginalPuddleWaterLevelY, captured once at harvest time) is ALWAYS
        // written back when the override is disabled — this method no longer early-returns on the
        // disabled path. The RainController this mutates lives on the harvested rig's clone, which
        // WeatherRig only ever SetActive(true)/SetActive(false)'s — never Destroy()'s — so it is the SAME
        // component instance reused for every raid played on this donor for the rest of the session.
        // A bare early return here left whatever the LAST raid's clamp wrote (if any) permanently in
        // place: toggling MaxLyingWaterOverrideEnabled back to false stopped applying a NEW clamp but did
        // nothing to undo a previous one already sitting on the field.
        internal static void ApplyPuddleClamp(ArenaMapPort.Shared.ArenaMapDescriptor map, WeatherController controller,
            float originalPuddleWaterLevelY)
        {
            var rain = controller != null ? controller.RainController : null;
            if (rain == null)
            {
                return; // defensive — HarvestOne's own validation already guarantees this is non-null for a Full rig
            }

            bool enabled = _maxLyingWaterOverrideEnabled != null && _maxLyingWaterOverrideEnabled.Value;
            float target = enabled ? _maxLyingWaterOverrideValue.Value : originalPuddleWaterLevelY;

            var before = rain.PuddleWaterLevel;
            if (before.y == target)
            {
                return; // already at the target value — nothing to log or write (covers the common
                        // disabled-by-default case where target already equals the harvest-time original)
            }

            rain.PuddleWaterLevel = new Vector2(before.x, target);
            if (enabled)
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][RainClamp] map={map.Key} MaxLyingWater clamped " +
                    $"{before.y:0.00} -> {target:0.00}");
            }
            else
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][RainClamp] map={map.Key} MaxLyingWater restored " +
                    $"{before.y:0.00} -> {target:0.00} (override disabled)");
            }
        }

        // §C.3 — Harmony postfix target: RainController.AbstractStateSummer.SetWetting(float, float,
        // Vector2, float). See HarmonyPatches.InstallWetRendererDamper for the target resolution.
        internal static void Postfix(RainController.AbstractStateSummer __instance)
        {
            // MANDATORY first check — see this class's own doc comment: without it this postfix would
            // dampen wetness on every real vanilla raid played in the same session too, not just ours.
            if (!ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap())
            {
                return;
            }
            float multiplier = WetRendererIntensityMultiplier;
            if (multiplier >= 1f)
            {
                return; // 1.0 = off — avoid a pointless write every frame
            }

            var controller = __instance?.Controller;
            var wet = controller != null ? controller._wetRenderer : null;
            if (wet == null)
            {
                return;
            }

            wet.Intensity = wet.Intensity * multiplier;
            if (!_damperLogged)
            {
                _damperLogged = true;
                Plugin.Log.LogInfo($"[ArenaMapPort][WetRendererDamper] intensity scaled by x{multiplier:0.00} " +
                    $"(now {wet.Intensity:0.00}) — logged once per session, applies every frame thereafter");
            }
        }
    }
}
