using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Keep the public BepInEx file small. Normal-mode tuning is a non-saving ConfigFile;
    /// developer mode binds into the public file so ConfigurationManager can discover it.
    /// Existing tuning is archived before compaction, including unknown/parked-map values.
    /// </summary>
    internal static class CompactConfiguration
    {
        static readonly PropertyInfo OrphansProperty = typeof(ConfigFile).GetProperty(
            "OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool IsPublic(ConfigDefinition key) =>
            (key.Section == "General" && key.Key == "DeveloperMode") ||
            (key.Section == "Appearance" && key.Key == "NaturalEquatorNight");

        internal static string DeveloperPath(ConfigFile config) =>
            Path.ChangeExtension(config.ConfigFilePath, ".developer.cfg");

        internal static ConfigFile Initialize(ConfigFile config)
        {
            // Must precede every Bind: BepInEx otherwise saves after each new entry.
            config.SaveOnConfigSet = false;
            var current = Orphans(config);
            string archivePath = DeveloperPath(config);
            var archive = new ConfigFile(archivePath, false) { SaveOnConfigSet = false };
            var saved = Orphans(archive);
            bool hasTuning = false;
            foreach (var pair in current)
                if (!IsPublic(pair.Key)) { hasTuning = true; break; }
            if (hasTuning)
            {
                // Merge before replacing the archive: hand-added or future keys must not
                // discard an older developer recipe. Public switches belong only to config.
                foreach (var pair in current)
                    if (!IsPublic(pair.Key)) saved[pair.Key] = pair.Value;
                foreach (var key in new List<ConfigDefinition>(saved.Keys))
                    if (IsPublic(key)) saved.Remove(key);
                SaveArchive(archivePath, saved); // failure leaves the original public file intact
            }

            DeveloperOptions.Install(config);
            PlayerOptions.Install(config);
            if (DeveloperOptions.Enabled)
            {
                foreach (var pair in saved)
                    if (!IsPublic(pair.Key) && !current.ContainsKey(pair.Key)) current.Add(pair.Key, pair.Value);
                return config;
            }

            // These were never bound to the public ConfigFile. All functional code uses
            // Plugin.Settings, whose entries and change handlers stay intact independently.
            foreach (var key in new List<ConfigDefinition>(current.Keys))
                if (!IsPublic(key)) current.Remove(key);
            return archive;
        }

        internal static void Complete(ConfigFile config)
        {
            config.Save();
            config.SaveOnConfigSet = true;
        }

        static Dictionary<ConfigDefinition, string> Orphans(ConfigFile config) =>
            OrphansProperty?.GetValue(config) as Dictionary<ConfigDefinition, string> ??
            throw new InvalidOperationException("Unsupported BepInEx orphaned configuration layout");

        static void SaveArchive(string path, Dictionary<ConfigDefinition, string> values)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var snapshot = new ConfigFile(temporary, false) { SaveOnConfigSet = false };
                foreach (var pair in values) Orphans(snapshot).Add(pair.Key, pair.Value);
                snapshot.Save();
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
