using ArenaMapPort.Client;
using BepInEx.Configuration;

internal static class CompactChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ArenaMapPort-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "plugin.cfg");
        try
        {
            var main = new ConfigFile(path, false);
            var tuning = CompactConfiguration.Initialize(main);
            Check(!ReferenceEquals(main, tuning), "Normal tuning must be separate");
            Check(!tuning.SaveOnConfigSet, "Normal tuning must never autosave");
            Check(!File.Exists(path), "Bindings wrote public config before finalization");
            var volume = tuning.Bind("Audio (arena_equator)", "AmbientGainDb", 5f);
            var revision = tuning.Bind("Internal", "Revision", 0);
            revision.Value = 1;
            CompactConfiguration.Complete(main);
            Check(Assignments(path) == 2, "Fresh public config must have exactly two assignments");
            Check(!File.Exists(CompactConfiguration.DeveloperPath(main)), "Fresh player mode created developer config");
            int events = 0; volume.SettingChanged += (_, _) => events++;
            volume.Value = 7;
            main.Bind("Appearance", "NaturalEquatorNight", false).Value = true;
            Check(events == 1 && Assignments(path) == 2, "Runtime tuning or player change expanded config");
            Check(!File.Exists(CompactConfiguration.DeveloperPath(main)), "Runtime tuning created developer config");

            // Positive control: create a representative old full config including orphaned/parked keys.
            File.AppendAllText(path, "\n[Audio (arena_equator)]\nAmbientGainDb = 6.25\n[Lighting (arena_bowl)]\nReflectionIntensity = 0.43\n[Future]\nKeepMe = unknown\n[Logging]\nVerbose = true\n");
            Check(Assignments(path) == 6, "Legacy fixture is not actually a full config");
            main = new ConfigFile(path, false);
            tuning = CompactConfiguration.Initialize(main);
            string archive = CompactConfiguration.DeveloperPath(main);
            Check(File.Exists(archive), "Legacy tuning not archived before compaction");
            Check(PlayerOptions.NaturalEquatorNight, "Public choice lost during migration");
            Check(tuning.Bind("Audio (arena_equator)", "AmbientGainDb", 5f).Value == 6.25f, "Custom mix lost in player mode");
            string originalArchive = File.ReadAllText(archive);
            RetiredConfiguration.RemoveSavedEntries(tuning, persist: false);
            CompactConfiguration.Complete(main);
            Check(Assignments(path) == 2, "Legacy primary remained large");
            Check(File.ReadAllText(archive) == originalArchive, "Normal startup changed developer archive");

            // Turn developer mode on via the documented key, then simulate the next process.
            main.Bind("General", "DeveloperMode", false).Value = true;
            Check(!DeveloperOptions.Enabled, "Developer mode was not startup-latched");
            main = new ConfigFile(path, false);
            tuning = CompactConfiguration.Initialize(main);
            Check(ReferenceEquals(main, tuning) && DeveloperOptions.Enabled, "Developer settings cannot appear in F12");
            volume = tuning.Bind("Audio (arena_equator)", "AmbientGainDb", 5f);
            Check(volume.Value == 6.25f, "Developer recipe was not restored");
            Check(tuning.Bind("Lighting (arena_bowl)", "ReflectionIntensity", 0f).Value == .43f, "Parked value lost");
            Check(tuning.Bind("Future", "KeepMe", "missing").Value == "unknown", "Unknown value lost");
            volume.Value = 8f;
            CompactConfiguration.Complete(main);
            Check(Assignments(path) > 2, "Positive developer control failed to save full tuning");
            main.Bind("General", "DeveloperMode", false).Value = false;
            main.Bind("Appearance", "NaturalEquatorNight", false).Value = false;
            main = new ConfigFile(path, false);
            tuning = CompactConfiguration.Initialize(main);
            Check(!PlayerOptions.NaturalEquatorNight, "Archived public option overrode current choice");
            Check(tuning.Bind("Audio (arena_equator)", "AmbientGainDb", 5f).Value == 8f, "Latest developer change lost");
            CompactConfiguration.Complete(main);
            Check(Assignments(path) == 2, "Returning to player mode did not compact config");
            for (int i = 0; i < 3; i++)
            {
                string before = File.ReadAllText(archive);
                main = new ConfigFile(path, false);
                tuning = CompactConfiguration.Initialize(main);
                Check(tuning.Bind("Audio (arena_equator)", "AmbientGainDb", 5f).Value == 8f, "Restart lost tuning");
                CompactConfiguration.Complete(main);
                Check(File.ReadAllText(archive) == before && Assignments(path) == 2, "Repeat startup rewrites or expands config");
            }
            // A future manually added primary key must merge without erasing archived tuning.
            File.AppendAllText(path, "\n[Future]\nNewKey = retain-too\n");
            main = new ConfigFile(path, false);
            tuning = CompactConfiguration.Initialize(main);
            Check(tuning.Bind("Future", "NewKey", "missing").Value == "retain-too", "New orphan lost");
            Check(tuning.Bind("Future", "KeepMe", "missing").Value == "unknown", "Merge discarded older archive");
            CompactConfiguration.Complete(main);
            Check(Assignments(path) == 2, "Orphan merge expanded primary");
            File.AppendAllText(path, "\n[Future]\nAfterLock = still-safe\n");
            string primaryBeforeFailure = File.ReadAllText(path);
            string archiveBeforeFailure = File.ReadAllText(archive);
            using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { CompactConfiguration.Initialize(new ConfigFile(path, false)); }
                catch (IOException) { failed = true; }
                Check(failed, "Locked archive did not exercise the failure path");
            }
            Check(File.ReadAllText(path) == primaryBeforeFailure && File.ReadAllText(archive) == archiveBeforeFailure,
                "Archive failure changed either original file");
            Console.WriteLine("COMPACT_CONFIG_PERSISTENCE_OK: fresh, migration, archived tuning, developer roundtrip, restart and orphan merge");
        }
        finally
        {
            // Only our known temporary directory; never a live install or recursive deletion.
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    static int Assignments(string path) => File.ReadLines(path).Count(line =>
        !line.StartsWith('#') && !line.StartsWith('[') && line.Contains('='));
    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
