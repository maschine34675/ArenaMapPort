using System;
using System.Collections.Generic;
using System.Reflection;
using System.ComponentModel;
using BepInEx.Configuration;
using EFT;
using JsonType;
using HarmonyLib;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// daytime-and-visuals, F1 — 2026-09-17 playtest symptom A: the raid time of day cannot be
    /// selected in the menu for our maps, so <c>arena_*</c> raids spawn at whatever hour
    /// <c>BaseLocalGame&lt;TPlayerOwner&gt;.InitWeather</c> (EFT/BaseLocalGame.cs:489-517) happens to
    /// pick — often night, since local raids can have <c>IsRandomTime</c> on by default and every
    /// arena map falls through to that branch (only <c>factory4_day</c>/<c>factory4_night</c> are
    /// pre-seeded in <c>_specialTimeResolver</c>, BaseLocalGame.cs:327-336).
    ///
    /// PLAN AMENDMENT (critic review, 2026-09-17 — see docs/progress/daytime-and-visuals.md): the
    /// original draft proposed a <c>return false</c> prefix that writes <c>_localDateTime</c> and
    /// skips <c>InitWeather</c>'s body entirely. That is wrong and would make this fix ineffective —
    /// or worse — on the one map this pass targets:
    ///
    ///  - <c>InitWeather</c> does far more than set <c>_localDateTime</c>: behind the gate
    ///    <c>if (WeatherController.Instance != null || MonoBehaviourSingleton&lt;TODSkySimple&gt;
    ///    .Instance != null)</c> (line 502) it also sets <c>GameWorld.GameDateTime</c> (line 501),
    ///    <c>TODSkyProvider.Instance.CurrentTime.GameDateTime</c> (line 504), and calls
    ///    <c>WeatherController.Instance.SetWeatherNodes(...)</c> (line 514) — a full-body
    ///    <c>return false</c> skips all of it.
    ///  - For <c>arena_bay5</c>, <see cref="WeatherRig"/> activates its harvested rig (setting
    ///    <c>WeatherController.Instance</c>) BEFORE <c>InitWeather</c> runs, so that gate IS entered
    ///    every raid today. Skipping the body would newly skip it every time, and per
    ///    <c>TOD_Time.FixedUpdate</c> (TOD_Sky.cs — <c>TOD_Time.GameDateTime</c> null means the sky
    ///    free-runs its own internal day/night cycle from the donor's baked <c>Cycle.DateTime</c>,
    ///    decoupled from the server date entirely) — trading "sometimes spawns at night" for "the sky
    ///    free-runs on its own clock", a worse and harder-to-diagnose bug on the one map this pass
    ///    targets. <c>LightingDiagnosticProbe.ProbeWeatherRig</c>'s own
    ///    "TODSkyProvider.CurrentTime.GameDateTime-set" line is the tripwire this project already
    ///    built to detect exactly this failure mode.
    ///
    /// Fix instead: let <c>InitWeather</c>'s own branch logic pick OUR value, then run the body to
    /// completion so every downstream side effect above still happens, just seeded with our date —
    /// the same mechanism BSG's own code already uses for factory4_day/factory4_night
    /// (<c>_specialTimeResolver</c>, a per-instance, non-static, freshly-constructed-per-raid
    /// <c>Dictionary&lt;string, DateTime&gt;</c> — BaseLocalGame.cs:327, no JIT-order/static-lifetime
    /// concern). <see cref="Prefix"/> sets <c>timeAndWeather.IsRandomTime = false</c> (via a
    /// <c>ref</c> parameter — <c>TimeAndWeatherSettings</c> is a struct, EFT/TimeAndWeatherSettings.cs
    /// :8, so <c>ref</c> is required for the mutation to reach <c>InitWeather</c>'s own execution
    /// rather than a prefix-local copy) and seeds <c>_specialTimeResolver[Location.Id]</c> with
    /// today's backend date at <see cref="FixedHour"/>.
    ///
    /// Patch target: <c>EFT.LocalGame</c> (the concrete class SPT raids actually construct via
    /// <c>BaseLocalGame&lt;TPlayerOwner&gt;.Create&lt;T&gt;</c>), not the open generic
    /// <c>BaseLocalGame&lt;TPlayerOwner&gt;</c> — <c>InitWeather</c> is inherited, never overridden
    /// (EFT/LocalGame.cs:29), and <c>AccessTools.Method(typeof(BaseLocalGame&lt;&gt;), "InitWeather")</c>
    /// would not resolve to a runtime-patchable method on an open generic. See
    /// <see cref="HarmonyPatches.InstallAll"/> for the actual patch-target resolution.
    /// </summary>
    internal static class TimeOfDayRepairs
    {
        // Public instance field, per-raid dictionary — see class doc comment for why reflecting into
        // it (rather than a parallel resolver of our own) is the minimal-risk, BSG-pattern-matching fix.
        internal static readonly FieldInfo ResolverField =
            AccessTools.Field(typeof(BaseLocalGame<EftGamePlayerOwner>), "_specialTimeResolver");
        internal static readonly FieldInfo BackendDateTimeField =
            AccessTools.Field(typeof(BaseLocalGame<EftGamePlayerOwner>), "_backendDateTime");
        static readonly FieldInfo SelectedDateTimeField =
            AccessTools.Field(typeof(BaseLocalGame<EftGamePlayerOwner>), "_selectedDateTime");
        internal static int? ActiveFixedHour { get; private set; }

        static ConfigEntry<bool> _enabled;
        static ConfigEntry<int> _fixedHour;
        static bool _installed;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _enabled = Plugin.Instance.Config.Bind("TimeOfDay", "Enabled", true,
                new ConfigDescription("Legacy fixed-hour override for unreleased Arena maps without their own time selection. " +
                    "Equator uses 12:00/00:00; Bay5 uses the two native server times. Has no effect on either released map. " +
                    "Saved legacy values are retained. Next raid.", null, BrowsableAttribute.No));

            _fixedHour = Plugin.Instance.Config.Bind("TimeOfDay", "ArenaFixedHour", 12,
                new ConfigDescription(
                    "Legacy start hour for Arena maps without fixed menu choices, when Enabled=true. " +
                    "Equator uses 12:00/00:00; Bay5 uses the two native server times. Has no effect on either released map. " +
                    "Saved legacy values are retained. Next raid.",
                    new AcceptableValueRange<int>(0, 23), BrowsableAttribute.No));

            var revision = Plugin.Instance.Config.Bind("TimeOfDay", "FixedTimeSelectionRevision", 0,
                new ConfigDescription("Internal migration marker for fixed menu choices.", null, BrowsableAttribute.No));
            if (revision.Value < 1)
            {
                Plugin.Log.LogInfo("[ArenaMapPort][TimeOfDay] Fixed selection migration 1: Equator now starts at " +
                    "the selected 12:00/00:00. Legacy Enabled/ArenaFixedHour remain saved for other maps; " +
                    "no lighting values changed.");
                revision.Value = 1;
            }
            if (revision.Value < 2)
            {
                Plugin.Log.LogInfo("[ArenaMapPort][TimeOfDay] Time selection policy: Equator uses 12:00/00:00; " +
                    "Bay5 follows its selected server time. Legacy Enabled/ArenaFixedHour remain saved for other maps; " +
                    "no lighting values changed.");
                revision.Value = 2;
            }
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += scene =>
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name) != null) ActiveFixedHour = null;
            };
            if (SelectedDateTimeField == null)
                Plugin.Log.LogError("[ArenaMapPort][TimeOfDay] _selectedDateTime missing; fixed menu selection cannot be applied.");

            // Review fix-up (daytime-and-visuals, 2026-09-17): ResolverField/BackendDateTimeField are
            // resolved by THIS class's own AccessTools.Field calls (above), independent of
            // HarmonyPatches.Verify (which only ever checks the InitWeather MethodInfo target — see
            // HarmonyPatches.InstallTimeOfDayFix). Without this check, a future rename of either field
            // makes Prefix() silently no-op every raid with zero log output anywhere. Log once, here,
            // matching HarmonyPatches.Verify's own message style.
            if (ResolverField == null || BackendDateTimeField == null)
            {
                var missing = ResolverField == null && BackendDateTimeField == null
                    ? "_specialTimeResolver and _backendDateTime"
                    : ResolverField == null ? "_specialTimeResolver" : "_backendDateTime";
                Plugin.Log.LogError(
                    $"[ArenaMapPort][Harmony] target not found: EFT.BaseLocalGame<EFT.EftGamePlayerOwner>." +
                    $"{missing} — TimeOfDayRepairs (daytime-and-visuals F1) is DISABLED for this session: " +
                    "arena_* raids fall back to InitWeather's own random/night-prone time selection. The " +
                    "decompiled evidence this field lookup relies on no longer matches the running game " +
                    "build (renamed field, or a type that moved assembly). Every OTHER ArenaMapPort hook " +
                    "still installs and runs normally.");
            }
        }

        // Exposed for DaytimeVisualsProbe's consolidated F1-F6 summary line.
        internal static bool Enabled => _enabled != null && _enabled.Value;
        internal static int FixedHour => _fixedHour != null ? _fixedHour.Value : 12;

        // Target: LocalGame.InitWeather(TimeAndWeatherSettings) — see HarmonyPatches.InstallTimeOfDayFix
        // for the AccessTools.Method resolution and this class's own doc comment for the full amendment
        // writeup. __instance is typed LocalGame (the patched, closed type), not the open generic base.
        internal static void Prefix(LocalGame __instance, ref TimeAndWeatherSettings timeAndWeather)
        {
            ActiveFixedHour = null;
            var location = __instance.Location;
            if (location?.Id == null || !location.Id.StartsWith("arena_", StringComparison.Ordinal))
            {
                return; // every other map's raid (and the hideout) — untouched
            }
            var selectedMap = FixedRaidTimeSelection.FindSelectable(location.Id);
            if (selectedMap?.UsesServerTime == true)
            {
                // Keep the native CURR/PAST branch, progressing GameDateTime and backend weather.
                // Offline randomization would otherwise discard the explicitly selected server time.
                // These are per-raid arguments only; never rewrite saved user configuration.
                timeAndWeather.IsRandomTime = false;
                timeAndWeather.IsRandomWeather = false;
                Plugin.Log.LogInfo($"[ArenaMapPort][TimeOfDay] {location.Id}: native server time and weather; " +
                    "legacy fixed-hour override bypassed, native weather initialization retained.");
                return;
            }
            var fixedMap = FixedRaidTimeSelection.Find(location.Id);
            if (fixedMap == null && !Enabled) return;
            if (fixedMap != null && SelectedDateTimeField == null) return;

            if (ResolverField == null || BackendDateTimeField == null)
            {
                return; // reflection target moved — Install() already logged this once at plugin startup; don't half-apply
            }

            var resolver = (Dictionary<string, DateTime>)ResolverField.GetValue(__instance);
            var backendDateTime = (GameDateTime)BackendDateTimeField.GetValue(__instance);
            if (resolver == null || backendDateTime == null)
            {
                return; // defensive — should never happen given the null checks above
            }

            // ref param — Harmony writes this back into the local InitWeather's own body reads, so the
            // IsRandomTime branch (BaseLocalGame.cs:491-495) is skipped in favor of the
            // _specialTimeResolver branch (:496-499) we seed on the next line.
            timeAndWeather.IsRandomTime = false;
            var selected = fixedMap == null ? EDateTime.CURR : (EDateTime)SelectedDateTimeField.GetValue(__instance);
            int hour = fixedMap == null ? FixedHour :
                (selected == EDateTime.PAST ? fixedMap.FixedNightHour.Value : fixedMap.FixedDayHour.Value);
            resolver[location.Id] = backendDateTime.Calculate().Date.AddHours(hour);
            ActiveFixedHour = hour;
            Plugin.Log.LogInfo($"[ArenaMapPort][TimeOfDay] {location.Id}: selected={selected} " +
                $"start={hour:00}:00 fixedMenu={fixedMap != null}; native weather initialization retained.");
        }
    }
}
