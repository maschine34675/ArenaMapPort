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

        // The public file holds exactly these entries. General/ArenaInstallPath (0.2.3, ArenaInstallationGate) is the
        // Arena installation override: it must stay public, otherwise compaction would archive a path the player
        // typed and the next start would read the default again. General/ShowMapNotices (MapNotices, 2026-10-22) is
        // the switch of the in-game "the maps are not available" messages and stays public for the same reason: a
        // player who turned the message off must not see it come back. Literals, not the constants of those classes:
        // the ConfigRetirementCheck tool compiles this file on its own.
        internal static bool IsPublic(ConfigDefinition key) =>
            (key.Section == "General" && key.Key == "DeveloperMode") ||
            (key.Section == "Appearance" && key.Key == "NaturalEquatorNight") ||
            (key.Section == "General" && key.Key == "ArenaInstallPath") ||
            (key.Section == "General" && key.Key == "ShowMapNotices");

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

        /// <summary>
        /// Last-resort configuration when <see cref="Initialize"/> throws (2026-10-12 host session: a sync tool held
        /// the developer archive, <c>File.Replace</c> threw an IOException out of Plugin.Awake and the whole plugin
        /// stayed dead for the session - no patches, no bundles, raid load aborted with a 404). The public file is
        /// used as the settings file for this session; nothing is archived or compacted.
        /// </summary>
        internal static ConfigFile Fallback(ConfigFile config)
        {
            config.SaveOnConfigSet = false;
            DeveloperOptions.Install(config);
            PlayerOptions.Install(config);
            return config;
        }

        // The archive is a persistence convenience: the merged values already live in the in-memory ConfigFile that
        // Initialize hands out, so a file that cannot be replaced right now (held by a sync/antivirus tool: Syncthing
        // on the test host, 2026-10-12) must not take the plugin down. Retry briefly, then fall back to an in-place
        // copy, then keep the old file and say so; the next start merges and retries.
        internal const int ReplaceAttempts = 5;

        /// <summary>Warning sink (Plugin.Awake wires the BepInEx logger; the ConfigRetirementCheck tool and tests leave it unset).</summary>
        internal static Action<string> Warning;

        static void SaveArchive(string path, Dictionary<ConfigDefinition, string> values)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var snapshot = new ConfigFile(temporary, false) { SaveOnConfigSet = false };
                foreach (var pair in values) Orphans(snapshot).Add(pair.Key, pair.Value);
                snapshot.Save();
                Exception last = null;
                for (int attempt = 0; attempt < ReplaceAttempts; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                        return;
                    }
                    catch (IOException e) { last = e; }
                    catch (UnauthorizedAccessException e) { last = e; }
                    System.Threading.Thread.Sleep(40 * (attempt + 1));
                }
                try { File.Copy(temporary, path, true); return; }
                catch (IOException e) { last = e; }
                catch (UnauthorizedAccessException e) { last = e; }
                Warning?.Invoke($"[Config] could not update the developer archive '{path}' " +
                                $"({last?.GetType().Name}: {last?.Message}); this session keeps the merged values in " +
                                "memory and the file is retried at the next start");
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
