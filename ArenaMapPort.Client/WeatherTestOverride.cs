using System;
using BepInEx.Configuration;
using EFT;
using EFT.Weather;
using UnityEngine;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// donor-settings-camera-weather §D. A deterministic, in-menu-selectable weather override for
    /// reproducing a given weather state on demand (the 2026-09-17 playtest's own worst-case ground
    /// symptom needs rain; the "is the ground still wrong once dry" control needs Clear) — a Harmony
    /// postfix on <c>EFT.Weather.WeatherController.SetWeatherNodes(WeatherNode[])</c> itself, not just
    /// <c>BaseLocalGame.InitWeather</c> (<see cref="TimeOfDayRepairs"/>'s own target): decompiled evidence
    /// shows <c>SetWeatherNodes</c> is called from TWO sites — <c>BaseLocalGame&lt;TPlayerOwner&gt;.
    /// InitWeather</c> (BaseLocalGame.cs:514) AND <c>LocalGame.vmethod_5</c> (LocalGame.cs:223, gated on
    /// <c>_raidSettings.mode == ELocalMode.PVE_OFFLINE</c>) — an SPT single-player raid's own later
    /// server-weather re-sync silently overwrites whatever <c>InitWeather</c> picked, on essentially every
    /// real SPT SP raid. Patching the sink both call sites share is the only way an override actually
    /// sticks.
    ///
    /// CRITICAL (critic amendment, 2026-09-17 — promoted from "defense-in-depth" to the load-bearing,
    /// mandatory FIRST check in this postfix): a Harmony postfix on an INSTANCE method fires for ANY
    /// instance the method runs on, not just ours. Without gating on <see cref="ArenaMapRaid.IsLoaded"/>
    /// as literally the first line, this postfix would silently rewrite weather on a real vanilla Reserve/
    /// Laboratory/Customs raid played in the same session too — the plan's original text framed this gate
    /// as "self-scoping for free... no extra gate needed", which the critic confirmed is false for any
    /// OTHER loaded WeatherController instance. <see cref="ArenaMapRaid.IsLoaded"/> (backed by
    /// <c>SceneManager.GetSceneByName</c> over <c>ArenaMapRegistry.All</c>) correctly and unambiguously
    /// distinguishes our own maps' Scripts scenes from any vanilla one, so the check itself is sufficient
    /// — it just must be unconditional, not skippable by any earlier return.
    ///
    /// Reentrancy: this postfix calls <c>instance.SetWeatherNodes(overrideNodes)</c> again — on the SAME
    /// method it is patched onto — which would stack-overflow without a guard. <see cref="_applying"/> is
    /// set inside a try/finally around that call (critic amendment: concrete mechanics, not just "needs a
    /// flag") so a thrown exception can never leave it stuck and silently disable the override for the
    /// rest of the session.
    ///
    /// Per report finding F10 (weather-override), each preset's field values are chosen to land
    /// unambiguously in the intended <c>EWeatherType</c> bucket per <c>WeatherNode.GetWeatherTypeByNode()</c>
    /// (decompiled, re-verified against this build) — two <c>WeatherNode</c>s, 600s apart, IDENTICAL field
    /// values (a flat curve, insensitive to <c>WeatherCurve</c>'s own PingPong wrap mode). One correction
    /// vs. the original report: <c>WeatherCurve.Rain</c>'s own property getter is
    /// <c>Mathf.Clamp01(rainCurve.Evaluate(...))</c> (WeatherCurve.cs, re-verified this pass) — it can
    /// never read back above 1.0 regardless of the authored <c>WeatherNode.Rain</c> value, so the "Rain"
    /// preset's <c>Rain=5f</c> (deliberately above BSG's own 0-4 authoring range) does not hit some higher
    /// shader-side ceiling the way the original report described; it reliably pins the CLAMPED
    /// <c>WeatherCurve.Rain</c> at exactly 1.0 — still maximum, deterministic rain, still at/above the
    /// 0.83 peak observed in the 2026-09-17 playtest, just via simple clamping rather than an inverse-lerp
    /// ceiling.
    /// </summary>
    internal static class WeatherTestOverride
    {
        internal enum TestWeather { Server, Clear, Overcast, Rain, Fog }

        const int SecondsHalfSpan = 300; // two nodes, 600s apart total, bracketing "now"

        static ConfigEntry<TestWeather> _mode;
        static bool _applying;
        static bool _installed;

        // Exposed for DaytimeVisualsProbe's consolidated summary line.
        internal static TestWeather Mode => _mode != null ? _mode.Value : TestWeather.Server;

        // One policy for both native menu preview and every weather initialization/re-sync.
        // FixedDayHour is the selected variant, not the later advancing in-raid sky hour.
        internal static TestWeather ModeForSelection(ArenaMapDescriptor map, int? selectedHour)
        {
            if (map == null || map.UsesServerTime) return TestWeather.Server;
            if (map.Key == ArenaMapRegistry.Equator.Key && selectedHour == map.FixedDayHour)
                return TestWeather.Clear;
            return EquatorNightNeon.RainForSelection(map.Key, selectedHour) ? TestWeather.Rain : Mode;
        }

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _mode = Plugin.Instance.Config.Bind("Weather (test)", "Weather", TestWeather.Server,
                "Equator day always uses Clear and Bay5 always uses dynamic SPT/backend weather; this setting " +
                "does not override either. Applies to Equator night when Night neon/Rain does not request rain, " +
                "and other experimental Arena maps. Server = backend weather. Any other value forces a " +
                "deterministic weather via a Harmony postfix on WeatherController.SetWeatherNodes, " +
                "reapplied on EVERY call (including SPT's own PVE_OFFLINE server re-sync) so it actually " +
                "sticks for the whole raid. Only ever applies to OUR maps' raids — see this file's own doc " +
                "comment. Applies on the next raid load, not live.");
        }

        // Target: EFT.Weather.WeatherController.SetWeatherNodes(WeatherNode[]) — see
        // HarmonyPatches.InstallWeatherTestOverride for the AccessTools resolution.
        internal static void Postfix(WeatherController __instance, WeatherNode[] nodes)
        {
            // MANDATORY first check — see this class's own doc comment. Nothing below this line may run
            // before it.
            if (!ArenaMapRaid.IsLoaded())
            {
                return;
            }
            if (_applying)
            {
                return; // reentrancy guard — see doc comment
            }
            var mode = ModeForSelection(ArenaMapRaid.CurrentMap, TimeOfDayRepairs.ActiveFixedHour);
            if (mode == TestWeather.Server)
            {
                return;
            }
            if (__instance == null)
            {
                return;
            }

            var overrideNodes = BuildNodes(mode);
            try
            {
                _applying = true;
                __instance.SetWeatherNodes(overrideNodes); // recurses into THIS SAME patched method — guarded above
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][WeatherTestOverride] applying '{mode}' threw: " +
                    $"{e.GetType().Name}: {e.Message}");
                return;
            }
            finally
            {
                _applying = false; // must clear even on a thrown exception, or the override silently
                                    // disables itself for the rest of the session — see doc comment
            }

            var map = ArenaMapRaid.CurrentMap;
            Plugin.Log.LogInfo($"[ArenaMapPort][WeatherTestOverride] applied '{mode}' " +
                $"(map={(map != null ? map.Key : "?")}; nightNeonRain={EquatorNightNeon.RainForCurrentRaid})");
        }

        static WeatherNode[] BuildNodes(TestWeather mode)
        {
            long now = NowTicks();
            long halfSpanTicks = TimeSpan.FromSeconds(SecondsHalfSpan).Ticks;
            var n0 = TemplateFor(mode);
            n0.Time = now - halfSpanTicks;
            var n1 = TemplateFor(mode);
            n1.Time = now + halfSpanTicks;
            return new[] { n0, n1 };
        }

        // Field values chosen so WeatherNode.GetWeatherTypeByNode() lands unambiguously in the intended
        // EWeatherType bucket (report finding F10, re-verified against this build's decompile) —
        // documented per-preset below rather than re-deriving the whole threshold table here.
        static WeatherNode TemplateFor(TestWeather mode)
        {
            switch (mode)
            {
                case TestWeather.Clear:
                    // ClearDay: Cloudness<-0.4 && Wind<=1 && Rain<2 && ScaterringFogDensity<=0.004.
                    return new WeatherNode
                    {
                        Cloudness = -0.5f,
                        Wind = 0.5f,
                        WindDirection = 0,
                        Rain = 0f,
                        RainRandomness = 0f,
                        ScaterringFogDensity = 0.001f,
                        Temperature = 20f,
                        AtmospherePressure = 760f,
                        LyingWater = 0f,
                    };
                case TestWeather.Overcast:
                    // FullCloud: Cloudness in [0.7,1] && Rain<=1.
                    return new WeatherNode
                    {
                        Cloudness = 0.8f,
                        Wind = 1f,
                        WindDirection = 0,
                        Rain = 0.3f,
                        RainRandomness = 0.3f,
                        ScaterringFogDensity = 0.01f,
                        Temperature = 15f,
                        AtmospherePressure = 750f,
                        LyingWater = 0.3f,
                    };
                case TestWeather.Rain:
                    // Rain bucket: Wind<=1 && Rain>3. Rain=5 deliberately above BSG's own 0-4 authoring
                    // range — WeatherCurve.Rain's getter clamps to [0,1] regardless (see doc comment), so
                    // this reliably pins the live value at exactly 1.0, at/above the 0.83 peak the
                    // 2026-09-17 playtest observed.
                    return new WeatherNode
                    {
                        Cloudness = 0.3f,
                        Wind = 0.5f,
                        WindDirection = 0,
                        Rain = 5f,
                        RainRandomness = 0.5f,
                        ScaterringFogDensity = 0.02f,
                        Temperature = 12f,
                        AtmospherePressure = 745f,
                        LyingWater = 1f,
                    };
                case TestWeather.Fog:
                    // Fog bucket: ScaterringFogDensity>=0.1 (checked after the LightRain/Rain/ClearDay/
                    // ClearWind/PartlyCloudDay/ClearFogDay buckets all fail first — Rain=0 and Cloudness=0
                    // keep this out of every one of those).
                    return new WeatherNode
                    {
                        Cloudness = 0f,
                        Wind = 0.5f,
                        WindDirection = 0,
                        Rain = 0f,
                        RainRandomness = 0f,
                        ScaterringFogDensity = 0.15f,
                        Temperature = 10f,
                        AtmospherePressure = 755f,
                        LyingWater = 0f,
                    };
                default:
                    return WeatherNode.CreateDefault();
            }
        }

        // Same "current game time, falling back to real time" resolution WeatherCurve.method_1 itself
        // uses (decompiled) — matches so the two bracketing nodes actually bracket "now" from the curve's
        // own point of view, not just wall-clock time.
        static long NowTicks()
        {
            try
            {
                if (Comfort.Common.Singleton<GameWorld>.Instantiated)
                {
                    var gdt = Comfort.Common.Singleton<GameWorld>.Instance.GameDateTime;
                    if (gdt != null)
                    {
                        return gdt.Calculate().Ticks;
                    }
                }
            }
            catch
            {
                // fall through to real time
            }
            return DateTime.UtcNow.Ticks;
        }
    }
}
