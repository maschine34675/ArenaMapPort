using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using JsonType;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    internal enum EquatorProfile { None, Day, Night }

    /// <summary>
    /// Factory-style independent appearance recipes behind Equator's existing time choices.
    /// Capture before LoadMapAndData loads scenes: InitWeather alone is too late for scene repairs.
    /// A raid retains its selected recipe; clocks, neon toggles and saved legacy tuning cannot swap it.
    /// Native rendering/SAIN acceptance is documented in docs/progress/equator-profiles.md.
    /// </summary>
    internal static class EquatorProfiles
    {
        static readonly Dictionary<ConfigEntryBase, object> Pairs = new Dictionary<ConfigEntryBase, object>();
        static readonly MethodInfo RegisterMethod = typeof(EquatorProfiles).GetMethod(nameof(Register),
            BindingFlags.Static | BindingFlags.NonPublic);
        static bool _installed;
        static long _generation;
        static int? _scriptsSceneHandle;
        static bool _confirmed;
        internal static EquatorProfile Current { get; private set; }

        sealed class Pair<T>
        {
            internal ConfigEntry<T> Day, Night;
        }

        internal static void Install(Harmony harmony)
        {
            if (_installed) return;
            _installed = true;
            var load = AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.LoadMapAndData));
            if (load == null)
                Plugin.Log.LogError("[ArenaMapPort][Profiles] LoadMapAndData unavailable; early profile capture cannot run.");
            else
                harmony.Patch(load, prefix: new HarmonyMethod(typeof(EquatorProfiles), nameof(LoadPrefix)),
                    postfix: new HarmonyMethod(typeof(EquatorProfiles), nameof(LoadPostfix)));
            SceneManager.sceneLoaded += (scene, _) =>
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name)?.Key == ArenaMapRegistry.Equator.Key)
                    _scriptsSceneHandle = scene.handle;
            };
            SceneManager.sceneUnloaded += scene =>
            {
                // An old raid can unload after the next load has already captured its selection.
                // Track this generation's loaded scene, not CurrentMap or merely the map name.
                if (_scriptsSceneHandle == scene.handle) Reset("scripts scene unloaded");
            };
        }

        static void LoadPrefix(TarkovApplication __instance, out long __state)
        {
            var settings = __instance.CurrentRaidSettings;
            __state = BeginLoad(settings?.SelectedLocation?.Id ?? settings?.LocationId,
                settings?.SelectedDateTime ?? EDateTime.CURR);
        }

        static void LoadPostfix(ref Task<ClientGameWorld> __result, long __state) =>
            __result = ObserveLoad(__result, __state);

        internal static long BeginLoad(string mapKey, EDateTime selected)
        {
            Reset("new load");
            Current = Select(mapKey, selected);
            if (Current != EquatorProfile.None)
                Plugin.Log.LogInfo($"[ArenaMapPort][Profiles] {mapKey}: selected {Current} before scene loading (generation {_generation}).");
            return _generation;
        }

        internal static async Task<T> ObserveLoad<T>(Task<T> task, long generation) where T : class
        {
            try
            {
                var result = await task;
                if (result == null && generation == _generation) Reset("load returned no world");
                return result;
            }
            catch
            {
                if (generation == _generation) Reset("load canceled or failed");
                throw;
            }
        }

        internal static void ConfirmSelection(string mapKey, EDateTime selected)
        {
            var requested = Select(mapKey, selected);
            if (requested == EquatorProfile.None)
            {
                Reset("non-Equator weather initialization");
                return;
            }
            // Fallback supports callers that bypass the normal application loader; normal raids
            // already selected this exact profile before any scene callbacks consumed settings.
            if (Current == EquatorProfile.None) Current = requested;
            else if (Current != requested)
            {
                Plugin.Log.LogError($"[ArenaMapPort][Profiles] Selection mismatch: early={Current}, InitWeather={requested}; " +
                    "retaining the early profile to prevent mixed scene settings.");
                return;
            }
            if (!_confirmed)
                Plugin.Log.LogInfo($"[ArenaMapPort][Profiles] {mapKey}: confirmed {Current} at InitWeather; recipe locked for this raid.");
            _confirmed = true;
        }

        static EquatorProfile Select(string mapKey, EDateTime selected) => mapKey != ArenaMapRegistry.Equator.Key
            ? EquatorProfile.None : selected == EDateTime.PAST ? EquatorProfile.Night : EquatorProfile.Day;

        static void Reset(string reason)
        {
            if (Current != EquatorProfile.None)
                Plugin.Log.LogInfo($"[ArenaMapPort][Profiles] Cleared {Current}: {reason}.");
            Current = EquatorProfile.None;
            _confirmed = false;
            _scriptsSceneHandle = null;
            _generation++;
        }

        internal static void BindAll()
        {
            // Snapshot because binding profile copies expands the ConfigFile collection.
            var entries = new List<ConfigEntryBase>();
            foreach (var item in Plugin.Instance.Config)
                if (IsAppearance(item.Key)) entries.Add(item.Value);
            foreach (var entry in entries)
                RegisterMethod.MakeGenericMethod(entry.SettingType).Invoke(null, new object[] { entry });
            Plugin.Log.LogInfo($"[ArenaMapPort][Profiles] Bound {entries.Count} independent appearance settings per Equator profile; " +
                "legacy saved settings retained, Day uses accepted defaults, Night uses the 2026-10-07 recipe.");
        }

        static bool IsAppearance(ConfigDefinition definition) => definition != null &&
            EquatorProfileDefaults.Appearance.ContainsKey(definition.Section + "/" + definition.Key);

        internal static bool IsLegacyDefinition(ConfigDefinition definition) => IsAppearance(definition) ||
            (definition?.Section == "Bots (arena_equator)" && definition.Key == "SainLocation");

        static Pair<T> Register<T>(ConfigEntry<T> entry)
        {
            if (entry == null) return null;
            if (Pairs.TryGetValue(entry, out var existing)) return (Pair<T>)existing;
            if (!IsAppearance(entry.Definition))
            {
                Pairs.Add(entry, null); // Bay5/common per-frame reads also use the allocation-free fast path.
                return null;
            }
            var old = entry.Description;
            var stem = entry.Definition.Section.Substring(0, entry.Definition.Section.Length - 1);
            var serialized = EquatorProfileDefaults.Appearance[entry.Definition.Section + "/" + entry.Definition.Key];
            ConfigDescription Description(string profile) => new ConfigDescription(
                $"Equator {profile} profile. " + old.Description, old.AcceptableValues,
                old.Tags == null ? Array.Empty<object>() : (object[])old.Tags.Clone());
            var pair = new Pair<T>
            {
                Day = entry.ConfigFile.Bind(stem + " Day)", entry.Definition.Key, (T)entry.DefaultValue, Description("Day")),
                Night = entry.ConfigFile.Bind(stem + " Night)", entry.Definition.Key,
                    TomlTypeConverter.ConvertToValue<T>(serialized), Description("Night"))
            };
            Pairs.Add(entry, pair);
            Pairs.Add(pair.Day, pair);
            Pairs.Add(pair.Night, pair);
            return pair;
        }

        internal static T Value<T>(ConfigEntry<T> entry) => Entry(entry).Value;

        internal static ConfigEntry<T> Entry<T>(ConfigEntry<T> entry)
        {
            var pair = Register(entry);
            return pair == null ? entry : Current == EquatorProfile.Night ? pair.Night : pair.Day;
        }

        internal static ConfigEntry<T> ForSection<T>(ConfigEntry<T> entry, string section)
        {
            var pair = Register(entry);
            if (pair == null) return entry;
            if (section == pair.Day.Definition.Section) return pair.Day;
            if (section == pair.Night.Definition.Section) return pair.Night;
            return entry;
        }

        internal static void Subscribe<T>(ConfigEntry<T> entry, EventHandler handler)
        {
            var pair = Register(entry);
            if (pair == null) { if (entry != null) entry.SettingChanged += handler; }
            else { pair.Day.SettingChanged += handler; pair.Night.SettingChanged += handler; }
        }

        internal static T NightDefault<T>(string key, T fallback) => EquatorProfileDefaults.NightEffect(key, fallback);
    }
}
