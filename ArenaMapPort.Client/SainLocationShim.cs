using System;
using System.Collections.Generic;
using System.Reflection;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using HarmonyLib;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// bots-stage1 (docs/progress/bots-stage1.md) — SAIN compatibility on a ported map, soft dependency.
    ///
    /// SAIN (me.sol.sain) resolves the raid's map once per tick in <c>SAIN.Components.LocationClass</c>:
    /// <c>ManualUpdate → findLocation → parseLocation</c> switches over the vanilla location ids and on
    /// anything else logs an error and returns <c>ELocation.None</c> (SAIN decompile, LocationClass.cs:92-155).
    /// Because <c>_foundLocation</c> only flips on a known id, the switch — and the error line — repeat on
    /// every tick for the whole raid (the "[SAIN.Components.LocationClass] parseLocation [arena_equator]"
    /// spam seen in every ported-map log). The only consumer of the parsed value is
    /// <c>LocationSettingsExtensions.Current</c> (per-location difficulty multipliers), whose dictionary has
    /// no <c>None</c> key, so SAIN silently skips its location tuning on our maps.
    ///
    /// Fix: a Harmony prefix on the private <c>findLocation()</c> that, while one of our maps is loaded,
    /// writes the configured stand-in location into <c>Location</c>, sets <c>_foundLocation</c>, and skips
    /// the original. Equator's selected day/night profile defaults to Factory/FactoryNight. Vanilla
    /// maps are untouched (prefix returns true). Every SAIN type/member is resolved by name via
    /// <see cref="AccessTools"/> at install time — no compile-time reference; when SAIN is absent (the
    /// user's vanilla testbed) nothing is patched and a single info line says so.
    /// </summary>
    internal static class SainLocationShim
    {
        const string SainLocationClass = "SAIN.Components.LocationClass";
        const string FindLocationMethod = "findLocation";
        const string LocationProperty = "Location";
        const string FoundField = "_foundLocation";
        const string FallbackLocation = "Factory";

        static readonly Dictionary<string, ConfigEntry<string>> _configs = new Dictionary<string, ConfigEntry<string>>();
        static ConfigEntry<string> _equatorDay, _equatorNight;
        static MethodInfo _locationSetter;
        static FieldInfo _foundLocation;
        static Type _eLocation;
        static string _loggedForMap;

        internal static void BindConfig()
        {
            // Review 2026-09-19: per-raid log gate, reset like every other per-raid state in this plugin.
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += scene =>
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name) != null)
                {
                    _loggedForMap = null;
                }
            };
            foreach (var map in ArenaMapRegistry.All)
            {
                string fallback = map.Key == ArenaMapRegistry.Equator.Key ? "Interchange"
                    : map.Key == ArenaMapRegistry.Bay5.Key ? "Customs"
                    : FallbackLocation;
                _configs[map.Key] = Plugin.Instance.Settings.Bind($"Bots ({map.Key})", "SainLocation", fallback,
                    "bots-stage1 — the vanilla location SAIN treats this map as, for its per-location difficulty " +
                    "settings (SAIN's ELocation names: Factory, FactoryNight, Customs, GroundZero, Reserve, Streets, " +
                    "Lighthouse, Shoreline, Labs, Woods, Interchange, Terminal, Town, Labyrinth). Only read while " +
                    "SAIN is installed. Takes effect on the next raid.");
            }
            // Preserve the legacy assignment for recovery; Equator's independent profiles own the
            // effective aliases now. Config visibility hides the superseded legacy entry.
            _equatorDay = Plugin.Instance.Settings.Bind("Bots (arena_equator Day)", "SainLocation", "Factory",
                "Vanilla SAIN location used for Equator's 12:00 Day profile. Takes effect on the next raid.");
            _equatorNight = Plugin.Instance.Settings.Bind("Bots (arena_equator Night)", "SainLocation", "FactoryNight",
                "Vanilla SAIN location used for Equator's 00:00 Night profile. FactoryNight is SAIN's native " +
                "enum name; Factory_Night is also accepted. Takes effect on the next raid.");
        }

        /// <summary>Returns true when the patch was installed (SAIN present and its shape as expected).</summary>
        internal static bool TryInstall(Harmony harmony)
        {
            var type = AccessTools.TypeByName(SainLocationClass);
            if (type == null)
            {
                Plugin.Log.LogInfo("[ArenaMapPort][SAIN] SAIN not loaded — location shim not installed (nothing to do).");
                return false;
            }
            var find = AccessTools.Method(type, FindLocationMethod);
            var prop = AccessTools.Property(type, LocationProperty);
            _locationSetter = prop?.GetSetMethod(true);
            _eLocation = prop?.PropertyType;
            _foundLocation = AccessTools.Field(type, FoundField);
            if (find == null || _locationSetter == null || _eLocation == null || !_eLocation.IsEnum || _foundLocation == null)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][SAIN] {SainLocationClass} does not look like the 2026-09 SAIN build " +
                    $"(findLocation={(find != null)} Location.set={(_locationSetter != null)} enum={(_eLocation?.Name ?? "null")} " +
                    $"_foundLocation={(_foundLocation != null)}) — shim not installed; SAIN will log its own location error each tick.");
                return false;
            }

            // Review 2026-09-19: the fallback name must exist in THIS SAIN build's enum, otherwise the prefix
            // could only ever hand control back to SAIN — validate once here instead of per tick.
            if (ParseOrNull(FallbackLocation) == null)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][SAIN] '{FallbackLocation}' is not a member of {_eLocation.FullName} — shim not installed.");
                return false;
            }
            foreach (var kv in _configs)
            {
                if (kv.Key != ArenaMapRegistry.Equator.Key) ValidateEntry(kv.Value);
            }
            ValidateEntry(_equatorDay);
            ValidateEntry(_equatorNight);

            harmony.Patch(find, prefix: new HarmonyMethod(AccessTools.Method(typeof(SainLocationShim), nameof(FindLocationPrefix))));
            Plugin.Log.LogInfo($"[ArenaMapPort][Harmony] patched {SainLocationClass}.{FindLocationMethod} (bots-stage1 SAIN location shim)");
            return true;
        }

        static object ParseOrNull(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || _eLocation == null)
            {
                return null;
            }
            try
            {
                name = name.Trim();
                if (string.Equals(name, "Factory_Night", StringComparison.OrdinalIgnoreCase)) name = "FactoryNight";
                var parsed = Enum.Parse(_eLocation, name, ignoreCase: true);
                return Enum.IsDefined(_eLocation, parsed) ? parsed : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        static void ValidateEntry(ConfigEntry<string> entry)
        {
            if (ParseOrNull(entry.Value) != null) return;
            Plugin.Log.LogWarning($"[ArenaMapPort][SAIN] [{entry.Definition.Section}] SainLocation='{entry.Value}' is not a SAIN ELocation " +
                $"name (valid: {string.Join(", ", Enum.GetNames(_eLocation))}) — {FallbackLocation} will be used for that profile.");
        }

        // Harmony prefix — SAIN's findLocation() is private, instance, no parameters, void. SAIN's own body
        // is `if (!_foundLocation) { Location = parseLocation(); ... }`; this mirrors that guard so the
        // reflected writes happen once per raid, not per tick (review 2026-09-19).
        static bool FindLocationPrefix(object __instance)
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || __instance == null)
            {
                return true; // vanilla map: SAIN's own switch runs untouched
            }
            if (_foundLocation.GetValue(__instance) is bool found && found)
            {
                return false; // already set (by us) — nothing to do this tick
            }

            ConfigEntry<string> entry;
            if (map.Key == ArenaMapRegistry.Equator.Key)
            {
                // Scene callbacks can precede the raid's authoritative selection. Suppress SAIN's
                // unknown-map spam, but leave its found flag false so the next tick can retry.
                if (EquatorProfiles.Current == EquatorProfile.None) return false;
                entry = EquatorProfiles.Current == EquatorProfile.Night ? _equatorNight : _equatorDay;
            }
            else _configs.TryGetValue(map.Key, out entry);
            string wanted = entry?.Value ?? FallbackLocation;
            object value = ParseOrNull(wanted);
            if (value == null)
            {
                value = ParseOrNull(FallbackLocation);
                if (value == null)
                {
                    return true; // cannot happen after TryInstall's validation; be safe and let SAIN run
                }
            }

            try
            {
                _locationSetter.Invoke(__instance, new[] { value });
                _foundLocation.SetValue(__instance, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][SAIN] could not set SAIN's location for {map.Key}: {e.GetType().Name}: {e.Message} — letting SAIN run its own lookup.");
                return true;
            }

            if (_loggedForMap != map.Key)
            {
                _loggedForMap = map.Key;
                Plugin.Log.LogInfo($"[ArenaMapPort][SAIN] {map.Key}: SAIN location set to {value} " +
                    $"(config [{entry?.Definition.Section ?? "fallback"}] SainLocation='{wanted}').");
            }
            return false; // skip SAIN's switch (and its per-tick error line)
        }
    }
}
