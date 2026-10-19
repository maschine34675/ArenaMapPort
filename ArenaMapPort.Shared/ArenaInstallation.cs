#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace ArenaMapPort.Shared
{
    /// <summary>
    /// Outcome of looking for, and verifying, the user's own Escape from Tarkov: Arena installation.
    /// Compiled into both the client plugin and the server mod; neither needs any extra package for it.
    /// </summary>
    public sealed class ArenaInstallationResult
    {
        internal ArenaInstallationResult(bool verified, string installPath, string source, string manifestVersion,
            string registryVersion, int entriesChecked, long totalBytes, string reason, int mismatchCount,
            IReadOnlyList<string> mismatches, int longPathsSkipped = 0)
        {
            LongPathsSkipped = longPathsSkipped;
            Verified = verified;
            InstallPath = installPath;
            Source = source ?? ArenaInstallation.SourceNone;
            ManifestVersion = manifestVersion;
            RegistryVersion = registryVersion;
            EntriesChecked = entriesChecked;
            TotalBytes = totalBytes;
            Reason = reason;
            MismatchCount = mismatchCount;
            Mismatches = mismatches ?? new string[0];
        }

        /// <summary>True when the installation folder exists, its launcher manifest is plausible and every listed file is present with the listed size.</summary>
        public bool Verified { get; }

        /// <summary>The installation folder that was examined (absolute), or null when none was found.</summary>
        public string InstallPath { get; }

        /// <summary>"override" (an explicit path was given), "registry" (the BSG launcher's uninstall entry) or "none".</summary>
        public string Source { get; }

        /// <summary>Version string from the launcher manifest ConsistencyInfo (authoritative), or null when it was not read.</summary>
        public string ManifestVersion { get; }

        /// <summary>DisplayVersion from the registry entry (may lag behind the manifest), or null.</summary>
        public string RegistryVersion { get; }

        /// <summary>Number of manifest entries compared with the disk (the manifest's own entry is not one of them).</summary>
        public int EntriesChecked { get; }

        /// <summary>Sum of the sizes of the files that matched, in bytes.</summary>
        public long TotalBytes { get; }

        /// <summary>One sentence naming why the installation was not verified; null when it was.</summary>
        public string Reason { get; }

        /// <summary>How many listed files were missing or had a different size.</summary>
        public int MismatchCount { get; }

        /// <summary>
        /// At most 8 of the mismatches, each "relative\path: missing" or "relative\path: size ACTUAL != LISTED".
        /// </summary>
        public IReadOnlyList<string> Mismatches { get; }

        /// <summary>
        /// Listed files that were not compared because their full path reaches Windows' classic 260-character limit
        /// (<see cref="ArenaInstallation.MaxPathLength"/>): the installation sits in a deep folder. Counted, never a mismatch.
        /// </summary>
        public int LongPathsSkipped { get; }

        /// <summary>One line for an Info log.</summary>
        public string Describe()
        {
            if (!Verified)
                return "Escape from Tarkov: Arena not found: " + Reason;
            return string.Format(CultureInfo.InvariantCulture, "Escape from Tarkov: Arena{0} at {1} ({2}; {3:N0} files, {4} GB verified{5})",
                string.IsNullOrEmpty(ManifestVersion) ? "" : " " + ManifestVersion, InstallPath, Source, EntriesChecked,
                ArenaInstallation.FormatGigabytes(TotalBytes),
                LongPathsSkipped == 0 ? "" : string.Format(CultureInfo.InvariantCulture, ", {0:N0} long path{1} skipped",
                    LongPathsSkipped, LongPathsSkipped == 1 ? "" : "s"));
        }

        public override string ToString() => Describe();
    }

    /// <summary>
    /// Finds the user's Escape from Tarkov: Arena installation and checks it against the launcher's own manifest.
    ///
    /// Arena is a separate purchase from Escape from Tarkov, and this mod ships maps derived from Arena's files, so the
    /// maps are offered only on a machine that has Arena installed. This is a good-faith installation gate at SPT's own
    /// level (SPT requires an installed EFT), not copy protection: there is no switch that turns it off. An explicit
    /// install path exists for installations without the launcher's registry entry and runs the same verification.
    ///
    /// The BSG launcher registers the game under HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\
    /// EscapeFromTarkovArena_live (InstallLocation, DisplayVersion) and writes ConsistencyInfo, a UTF-8 JSON manifest
    /// {"Version":..,"Entries":[{"Path":..,"Size":..,"Checksum":..},..]} (about 15,000 entries, 76 GB). Verification reads
    /// no game file: it compares each listed file's existence and size with the disk (about half a second warm,
    /// longer on a cold disk cache, so callers run it once and keep the result). The Checksum is BSG's own algorithm and
    /// is ignored; the manifest's own entry cannot match its own size and is skipped.
    ///
    /// Limits, stated plainly. The comparison covers names and sizes only, and the plausibility floors count distinct
    /// files, so a forged installation needs thousands of distinct files of the listed sizes (sparse files or hard links
    /// make that cheap). It raises the effort, nothing more; do not describe it as stronger than a good-faith gate.
    /// A missing file fails the gate; a different size fails it too, except for the files BattlEye rewrites in place
    /// (BattlEye\*) and the user-editable Logging.config, which only have to exist (<see cref="IsSizeTolerant"/>).
    /// Entries whose full path reaches Windows' classic 260-character limit are skipped and counted
    /// (<see cref="MaxPathLength"/>): EFT's Mono runtime cannot stat them when the installation sits in a deep
    /// folder, and both sides must reach the same verdict. The call is synchronous and does file-system I/O on the given path: an unreachable network path (an
    /// override or InstallLocation on an unrouted UNC address) blocked for 21 s when measured, so a caller on a game
    /// thread runs it on a worker with a bounded wait and treats a timeout as not verified.
    /// </summary>
    public static class ArenaInstallation
    {
        public const string SourceOverride = "override";
        public const string SourceRegistry = "registry";
        public const string SourceNone = "none";

        /// <summary>The registry key the BSG launcher writes; named in the "not found" reason.</summary>
        public const string RegistryKeyDisplay =
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkovArena_live";

        internal const string ExeName = "EscapeFromTarkovArena.exe";
        internal const string ManifestName = "ConsistencyInfo";
        internal const string GameAssemblyName = "GameAssembly.dll";
        internal const string LevelPrefix = @"EscapeFromTarkovArena_Data\level";
        internal const int MaxReportedMismatches = 8;
        /// <summary>Windows' classic MAX_PATH; a listed file whose full path is this long or longer is skipped, not compared.</summary>
        internal const int MaxPathLength = 260;
        const long MaxManifestBytes = 64L * 1000 * 1000;

        const string UninstallWow = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        const string UninstallPlain = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string KeyPrefix = "EscapeFromTarkovArena";
        const string PreferredKey = "EscapeFromTarkovArena_live";

        const string OverrideHint =
            "point the mod's Arena install path override at the folder that contains " + ExeName;

        /// <summary>
        /// Locate and verify Arena. A non-empty <paramref name="overridePath"/> is verified as given (an explicit but
        /// wrong override is NOT verified and does not fall back to the registry: the user asked for that path); else the
        /// BSG launcher's registry entry is used. Never throws.
        /// </summary>
        public static ArenaInstallationResult Detect(string overridePath) =>
            Detect(overridePath, new ReflectionRegistryProbe(), ArenaInstallationFloors.Default);

        /// <summary>Verify an explicit installation folder (the override path). Never throws.</summary>
        public static ArenaInstallationResult Verify(string installDir) =>
            Verify(installDir, SourceOverride, null, ArenaInstallationFloors.Default);

        internal static ArenaInstallationResult Detect(string overridePath, IRegistryProbe registry, ArenaInstallationFloors floors)
        {
            try
            {
                string explicitPath = CleanPath(overridePath);
                if (explicitPath.Length > 0)
                    return Verify(explicitPath, SourceOverride, null, floors);

                if (registry == null || !registry.IsAvailable)
                    return NotFound(SourceNone, null, null,
                        "The Windows registry cannot be read on this system, so the installation cannot be located automatically; " + OverrideHint + ".");

                List<ArenaRegistryEntry> entries = FindRegistryEntries(registry);
                if (entries.Count == 0)
                    return NotFound(SourceNone, null, null,
                        "No Escape from Tarkov: Arena installation is registered in the Windows registry (" + RegistryKeyDisplay
                        + "); install Arena with the Battlestate Games launcher, or " + OverrideHint + ".");

                ArenaInstallationResult first = null;
                foreach (ArenaRegistryEntry entry in entries)
                {
                    ArenaInstallationResult result = Verify(entry.InstallLocation, SourceRegistry, entry.DisplayVersion, floors);
                    if (result.Verified)
                        return result;
                    if (first == null)
                        first = result;
                }
                return first;
            }
            catch (Exception ex)
            {
                return NotFound(SourceNone, null, null, "Looking for the Arena installation failed unexpectedly: " + ex.Message);
            }
        }

        internal static ArenaInstallationResult Verify(string installDir, string source, string registryVersion, ArenaInstallationFloors floors)
        {
            string dir = null;
            try
            {
                floors = floors ?? ArenaInstallationFloors.Default;
                dir = NormalizeDirectory(installDir);
                if (dir == null)
                    return NotFound(source, null, registryVersion, "No installation folder was given.");
                return VerifyCore(dir, source, registryVersion, floors);
            }
            catch (ArgumentException ex)
            {
                return NotFound(source, dir, registryVersion, "The installation folder path is not valid: " + ex.Message);
            }
            catch (Exception ex)
            {
                return NotFound(source, dir, registryVersion, "Verifying the installation failed unexpectedly: " + ex.Message);
            }
        }

        static ArenaInstallationResult VerifyCore(string dir, string source, string registryVersion, ArenaInstallationFloors floors)
        {
            if (!Directory.Exists(dir))
                return NotFound(source, dir, registryVersion, File.Exists(dir)
                    ? "The installation path is a file, not the installation folder: " + dir + "."
                    : "The installation folder does not exist: " + dir + ".");
            if (!File.Exists(Path.Combine(dir, ExeName)))
                return NotFound(source, dir, registryVersion, dir + " does not contain " + ExeName + ".");

            string manifestPath = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifestPath))
                return NotFound(source, dir, registryVersion, dir + " does not contain the launcher manifest " + ManifestName + ".");

            ArenaManifest manifest;
            try
            {
                long manifestBytes = new FileInfo(manifestPath).Length;
                if (manifestBytes > MaxManifestBytes)
                    return NotFound(source, dir, registryVersion, "The launcher manifest " + ManifestName + " is implausibly large.");
                manifest = ArenaManifest.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            }
            catch (FormatException ex)
            {
                return NotFound(source, dir, registryVersion, "The launcher manifest " + ManifestName + " is not valid: " + ex.Message + ".");
            }
            catch (Exception ex)
            {
                return NotFound(source, dir, registryVersion, "The launcher manifest " + ManifestName + " cannot be read: " + ex.Message);
            }

            string version = manifest.Version == null ? null : manifest.Version.Trim();
            if (string.IsNullOrEmpty(version))
                return NotFound(source, dir, registryVersion, "The launcher manifest " + ManifestName + " has no version.");
            if (manifest.Entries.Count == 0)
                return NotFound(source, dir, registryVersion, "The launcher manifest " + ManifestName + " lists no files.", version);

            string conflict;
            List<ArenaPreparedEntry> files = PrepareEntries(dir, manifest, out conflict);
            if (conflict != null)
                return NotFound(source, dir, registryVersion, conflict, version);

            string floorReason = CheckFloors(files, floors);
            if (floorReason != null)
                return NotFound(source, dir, registryVersion, floorReason, version);

            int checkedCount = 0;
            int mismatchCount = 0;
            int longPathsSkipped = 0;
            long verifiedBytes = 0;
            var mismatches = new List<string>();
            foreach (ArenaPreparedEntry entry in files)
            {
                if (string.Equals(entry.Logical, ManifestName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.FullPath != null && entry.FullPath.Length >= MaxPathLength)
                {
                    longPathsSkipped++;
                    continue;
                }
                checkedCount++;
                string problem = CheckFile(entry, IsSizeTolerant(entry.Logical));
                if (problem == null)
                {
                    verifiedBytes += entry.Size;
                    continue;
                }
                mismatchCount++;
                if (mismatches.Count < MaxReportedMismatches)
                    mismatches.Add(entry.Logical + ": " + problem);
            }

            if (mismatchCount > 0)
            {
                string reason = string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} of {1:N0} files listed in {2} are missing or have a different size (first: {3}); the installation is incomplete, still updating or damaged.",
                    mismatchCount, checkedCount, ManifestName, mismatches[0]);
                return new ArenaInstallationResult(false, dir, source, version, registryVersion, checkedCount, verifiedBytes, reason, mismatchCount, mismatches, longPathsSkipped);
            }
            return new ArenaInstallationResult(true, dir, source, version, registryVersion, checkedCount, verifiedBytes, null, 0, null, longPathsSkipped);
        }

        /// <summary>
        /// Turns the manifest into the list of DISTINCT files to check. A file listed more than once (any spelling the runtime
        /// resolves to the same absolute path: '/' or '\', letter case, a doubled separator) is one file, so one real file
        /// cannot be listed again and again to reach the floors. A repeated listing with the same size is collapsed; with a
        /// different size the manifest contradicts itself and <paramref name="conflict"/> names it. The real launcher manifest
        /// has no repeated path (0 in 15,352 entries). A path that is not a plain relative path inside the installation keeps
        /// FullPath null and is reported as "invalid path" by <see cref="CheckFile"/>.
        /// </summary>
        static List<ArenaPreparedEntry> PrepareEntries(string dir, ArenaManifest manifest, out string conflict)
        {
            conflict = null;
            char last = dir[dir.Length - 1];
            string dirPrefix = last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar
                ? dir
                : dir + Path.DirectorySeparatorChar;

            var files = new List<ArenaPreparedEntry>(manifest.Entries.Count);
            var firstSizeByFile = new Dictionary<string, long>(manifest.Entries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (ArenaManifestEntry entry in manifest.Entries)
            {
                string logical = Logical(entry.Path);
                string full = ResolveInside(dir, dirPrefix, logical);
                string identity = full ?? "?" + logical; // a resolved path never starts with '?'
                long firstSize;
                if (firstSizeByFile.TryGetValue(identity, out firstSize))
                {
                    if (firstSize == entry.Size)
                        continue;
                    conflict = string.Format(CultureInfo.InvariantCulture,
                        "The launcher manifest {0} lists {1} more than once with different sizes ({2} and {3} bytes).",
                        ManifestName, logical, firstSize, entry.Size);
                    return null;
                }
                firstSizeByFile.Add(identity, entry.Size);
                files.Add(new ArenaPreparedEntry { Logical = logical, Size = entry.Size, FullPath = full });
            }
            return files;
        }

        /// <summary>
        /// The plausibility floors, applied to the distinct files of <see cref="PrepareEntries"/>. A hand-made manifest has to
        /// list at least this much distinct data, which raises the effort of a forged installation; it does not prove the
        /// game is there (names and sizes only: a speed bump for a good-faith gate, not copy protection). Null when all hold.
        /// </summary>
        static string CheckFloors(List<ArenaPreparedEntry> files, ArenaInstallationFloors floors)
        {
            long total = 0;
            int levels = 0;
            long gameAssemblyBytes = -1;
            bool listsExe = false;
            foreach (ArenaPreparedEntry entry in files)
            {
                total = entry.Size > long.MaxValue - total ? long.MaxValue : total + entry.Size;
                if (entry.Logical.StartsWith(LevelPrefix, StringComparison.OrdinalIgnoreCase))
                    levels++;
                if (string.Equals(entry.Logical, GameAssemblyName, StringComparison.OrdinalIgnoreCase) && entry.Size > gameAssemblyBytes)
                    gameAssemblyBytes = entry.Size;
                if (string.Equals(entry.Logical, ExeName, StringComparison.OrdinalIgnoreCase))
                    listsExe = true;
            }

            string lead = "The launcher manifest " + ManifestName;
            if (files.Count < floors.MinEntries)
                return string.Format(CultureInfo.InvariantCulture, "{0} lists only {1:N0} files; an Arena installation has at least {2:N0}.",
                    lead, files.Count, floors.MinEntries);
            if (total < floors.MinTotalBytes)
                return string.Format(CultureInfo.InvariantCulture, "{0} lists only {1} GB of files; an Arena installation has at least {2} GB.",
                    lead, FormatGigabytes(total), FormatGigabytes(floors.MinTotalBytes));
            if (levels < floors.MinLevelEntries)
                return string.Format(CultureInfo.InvariantCulture, "{0} lists only {1:N0} level files; an Arena installation has at least {2:N0}.",
                    lead, levels, floors.MinLevelEntries);
            if (gameAssemblyBytes < floors.MinGameAssemblyBytes)
                return string.Format(CultureInfo.InvariantCulture, "{0} does not list {1} with at least {2} MB.",
                    lead, GameAssemblyName, (floors.MinGameAssemblyBytes / 1000000d).ToString("0.#", CultureInfo.InvariantCulture));
            if (!listsExe)
                return lead + " does not list " + ExeName + ".";
            return null;
        }

        /// <summary>Null when the listed file exists with exactly the listed size, else "missing" / "size A != L" / "invalid path".</summary>
        /// <summary>
        /// Files that may legitimately differ in size from the manifest: BattlEye updates its own files in place when the
        /// game starts, and Logging.config is user-editable. They must exist; their size is not compared.
        /// </summary>
        internal static bool IsSizeTolerant(string logical) =>
            logical != null && (logical.StartsWith(@"BattlEye\", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(logical, "Logging.config", StringComparison.OrdinalIgnoreCase));

        static string CheckFile(ArenaPreparedEntry entry, bool sizeTolerant)
        {
            if (entry.FullPath == null)
                return "invalid path";
            try
            {
                var info = new FileInfo(entry.FullPath);
                if (!info.Exists)
                    return "missing";
                if (sizeTolerant)
                    return null;
                long actual = info.Length;
                return actual == entry.Size
                    ? null
                    : "size " + actual.ToString(CultureInfo.InvariantCulture) + " != " + entry.Size.ToString(CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                return "invalid path";
            }
            catch (Exception ex)
            {
                return "cannot be inspected (" + ex.GetType().Name + ")";
            }
        }

        /// <summary>
        /// The absolute path a manifest path resolves to, or null unless it is a plain relative path that stays inside the
        /// installation. Two independent guards: <see cref="IsPlainRelativePath"/> is a rule on the text (identical on every
        /// runtime), and the resolved path must still start with the installation folder, whatever this runtime's path
        /// normalisation made of the text.
        /// </summary>
        internal static string ResolveInside(string dir, string dirPrefix, string logical)
        {
            if (!IsPlainRelativePath(logical))
                return null;
            try
            {
                string joined = Path.Combine(dir, logical.Replace('\\', Path.DirectorySeparatorChar));
                // Legacy runtimes (EFT's Mono) throw PathTooLongException in GetFullPath at MAX_PATH. The text rule above
                // already refused every escaping spelling, and VerifyCore skips such an entry instead of comparing it.
                if (joined.Length >= MaxPathLength)
                    return joined;
                string full = Path.GetFullPath(joined);
                return full.Length > dirPrefix.Length && full.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True for a relative path of named segments joined by '\'. Refused: an empty path, a rooted or UNC path, a colon
        /// (drive letter, alternate data stream), and every segment that is empty or made only of dots and spaces: ".",
        /// "..", "...", ".. ", a doubled or trailing separator. Legacy path normalisation (.NET Framework, Mono in EFT)
        /// trims trailing dots and spaces from each segment, so ".. " resolves like ".." there and would leave the
        /// installation although the text does not equal "..".
        /// </summary>
        internal static bool IsPlainRelativePath(string logical)
        {
            if (string.IsNullOrEmpty(logical) || logical[0] == '\\' || logical.IndexOf(':') >= 0)
                return false;
            foreach (string segment in logical.Split('\\'))
                if (segment.TrimEnd(' ', '.').Length == 0)
                    return false;
            return true;
        }

        /// <summary>The manifest uses '\' as the directory separator; accept '/' too and compare in one spelling.</summary>
        static string Logical(string manifestPath) => manifestPath.Replace('/', '\\');

        static string CleanPath(string path)
        {
            if (path == null)
                return "";
            return path.Trim().Trim('"').Trim();
        }

        static string NormalizeDirectory(string installDir)
        {
            string cleaned = CleanPath(installDir);
            if (cleaned.Length == 0)
                return null;
            string full = Path.GetFullPath(cleaned);
            string root = Path.GetPathRoot(full);
            while (full.Length > (root == null ? 0 : root.Length)
                   && (full[full.Length - 1] == Path.DirectorySeparatorChar || full[full.Length - 1] == Path.AltDirectorySeparatorChar))
                full = full.Substring(0, full.Length - 1);
            return full;
        }

        static ArenaInstallationResult NotFound(string source, string installPath, string registryVersion, string reason, string manifestVersion = null) =>
            new ArenaInstallationResult(false, installPath, source, manifestVersion, registryVersion, 0, 0, reason, 0, null);

        internal static string FormatGigabytes(long bytes) =>
            (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture);

        // ---- registry -----------------------------------------------------------------------------------------

        /// <summary>
        /// The launcher's uninstall entries for Arena, preferred key first: WOW6432Node before the plain path, the
        /// "_live" key before any other key whose name starts with EscapeFromTarkovArena. Entries without an
        /// InstallLocation, and repeated locations, are dropped.
        /// </summary>
        internal static List<ArenaRegistryEntry> FindRegistryEntries(IRegistryProbe registry)
        {
            var result = new List<ArenaRegistryEntry>();
            var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string baseKey in new[] { UninstallWow, UninstallPlain })
            {
                var names = new List<string> { PreferredKey };
                var scanned = new List<string>();
                string[] subKeys = registry.GetSubKeyNames(baseKey);
                if (subKeys != null)
                {
                    foreach (string name in subKeys)
                        if (name != null && name.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(name, PreferredKey, StringComparison.OrdinalIgnoreCase))
                            scanned.Add(name);
                }
                scanned.Sort(StringComparer.Ordinal);
                names.AddRange(scanned);

                foreach (string name in names)
                {
                    string keyName = baseKey + "\\" + name;
                    string location = CleanPath(registry.GetValue(keyName, "InstallLocation"));
                    if (location.Length == 0 || !seenLocations.Add(location.TrimEnd('\\', '/')))
                        continue;
                    result.Add(new ArenaRegistryEntry(keyName, location, registry.GetValue(keyName, "DisplayVersion")));
                }
            }
            return result;
        }

        internal sealed class ArenaRegistryEntry
        {
            public ArenaRegistryEntry(string keyName, string installLocation, string displayVersion)
            {
                KeyName = keyName;
                InstallLocation = installLocation;
                DisplayVersion = displayVersion;
            }

            public string KeyName { get; }
            public string InstallLocation { get; }
            public string DisplayVersion { get; }
        }
    }

    /// <summary>The plausibility floors of the manifest; the production values are <see cref="Default"/>, tests scale them.</summary>
    internal sealed class ArenaInstallationFloors
    {
        public ArenaInstallationFloors(int minEntries, long minTotalBytes, int minLevelEntries, long minGameAssemblyBytes)
        {
            MinEntries = minEntries;
            MinTotalBytes = minTotalBytes;
            MinLevelEntries = minLevelEntries;
            MinGameAssemblyBytes = minGameAssemblyBytes;
        }

        /// <summary>At least 5,000 entries, 40 GB listed, 100 level files and GameAssembly.dll of 50 MB (decimal units); the real game has 15,352 entries, 76.1 GB, 388 level files and 155 MB.</summary>
        public static readonly ArenaInstallationFloors Default =
            new ArenaInstallationFloors(5000, 40000000000L, 100, 50000000L);

        public int MinEntries { get; }
        public long MinTotalBytes { get; }
        public int MinLevelEntries { get; }
        public long MinGameAssemblyBytes { get; }
    }

    /// <summary>Reads HKLM values; implemented by reflection for the game and by a fake in tests. Members never throw.</summary>
    internal interface IRegistryProbe
    {
        /// <summary>False when no registry can be read (not Windows, type missing).</summary>
        bool IsAvailable { get; }

        /// <summary>A value of a key given as "HKEY_LOCAL_MACHINE\..."; null when the key or value is absent or unreadable.</summary>
        string GetValue(string keyName, string valueName);

        /// <summary>Names of the subkeys of a key given as "HKEY_LOCAL_MACHINE\..."; empty when absent or unreadable.</summary>
        string[] GetSubKeyNames(string keyName);
    }

    /// <summary>
    /// Microsoft.Win32.Registry through reflection, so the client (Mono inside EFT) and the server (net10) need no
    /// compile-time reference. The type is resolved from the Microsoft.Win32.Registry assembly first (net5+), then
    /// from mscorlib (Mono, .NET Framework). Every platform or reflection failure means "no registry", never an exception.
    /// </summary>
    internal sealed class ReflectionRegistryProbe : IRegistryProbe
    {
        const string HklmPrefix = @"HKEY_LOCAL_MACHINE\";
        readonly MethodInfo _getValue;
        readonly object _localMachine;
        readonly MethodInfo _openSubKey;
        readonly MethodInfo _getSubKeyNames;

        public ReflectionRegistryProbe()
        {
            try
            {
                Type registry = ResolveRegistryType();
                if (registry == null)
                    return;
                MethodInfo getValue = registry.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(object) }, null);
                if (getValue == null)
                    return;
                // Probe once: a platform without a registry throws here (PlatformNotSupportedException inside the invoke).
                getValue.Invoke(null, new object[] { @"HKEY_LOCAL_MACHINE\SOFTWARE", "ArenaMapPortRegistryProbe", null });
                _getValue = getValue;

                object localMachine = ReadStatic(registry, "LocalMachine");
                if (localMachine != null)
                {
                    Type keyType = localMachine.GetType();
                    _openSubKey = keyType.GetMethod("OpenSubKey", new[] { typeof(string) });
                    _getSubKeyNames = keyType.GetMethod("GetSubKeyNames", Type.EmptyTypes);
                    _localMachine = localMachine;
                }
            }
            catch (Exception)
            {
                _getValue = null;
                _localMachine = null;
            }
        }

        public bool IsAvailable => _getValue != null;

        public string GetValue(string keyName, string valueName)
        {
            if (_getValue == null)
                return null;
            try
            {
                object value = _getValue.Invoke(null, new object[] { keyName, valueName, null });
                return value as string ?? (value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string[] GetSubKeyNames(string keyName)
        {
            if (_localMachine == null || _openSubKey == null || _getSubKeyNames == null
                || keyName == null || !keyName.StartsWith(HklmPrefix, StringComparison.OrdinalIgnoreCase))
                return new string[0];
            object key = null;
            try
            {
                key = _openSubKey.Invoke(_localMachine, new object[] { keyName.Substring(HklmPrefix.Length) });
                if (key == null)
                    return new string[0];
                return _getSubKeyNames.Invoke(key, null) as string[] ?? new string[0];
            }
            catch (Exception)
            {
                return new string[0];
            }
            finally
            {
                try { (key as IDisposable)?.Dispose(); }
                catch (Exception) { }
            }
        }

        static Type ResolveRegistryType()
        {
            foreach (string name in new[] { "Microsoft.Win32.Registry, Microsoft.Win32.Registry", "Microsoft.Win32.Registry, mscorlib", "Microsoft.Win32.Registry" })
            {
                try
                {
                    Type type = Type.GetType(name, false);
                    if (type != null)
                        return type;
                }
                catch (Exception)
                {
                    // try the next assembly
                }
            }
            return null;
        }

        static object ReadStatic(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (field != null)
                return field.GetValue(null);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return property == null ? null : property.GetValue(null, null);
        }
    }

    /// <summary>One entry of the launcher manifest: the relative path as written ('\' separated) and the byte size.</summary>
    internal struct ArenaManifestEntry
    {
        public string Path;
        public long Size;
    }

    /// <summary>
    /// A manifest entry after path analysis: its '\' spelling, its size, and the absolute path this runtime resolves it to
    /// (null when it is not a plain relative path inside the installation).
    /// </summary>
    internal struct ArenaPreparedEntry
    {
        public string Logical;
        public long Size;
        public string FullPath;
    }

    /// <summary>
    /// The launcher manifest ConsistencyInfo, read by a deliberately small reader for exactly this shape:
    /// {"Version":"..","Entries":[{"Path":"..","Size":123,"Checksum":-45,..},..]}. Keys may come in any order and
    /// unknown keys (the real file also has IsCritical) are skipped; anything malformed throws FormatException.
    /// Not a general JSON parser.
    /// </summary>
    internal sealed class ArenaManifest
    {
        const int MaxSkipDepth = 32;

        public string Version;
        public readonly List<ArenaManifestEntry> Entries = new List<ArenaManifestEntry>();

        public static ArenaManifest Parse(string json)
        {
            if (json == null)
                throw new FormatException("the manifest is empty");
            return new Reader(json).ReadManifest();
        }

        sealed class Reader
        {
            readonly string _s;
            int _i;

            public Reader(string text)
            {
                _s = text;
                _i = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
            }

            public ArenaManifest ReadManifest()
            {
                var manifest = new ArenaManifest();
                bool hasVersion = false, hasEntries = false;
                SkipWhitespace();
                Expect('{');
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _i++;
                }
                else
                {
                    while (true)
                    {
                        SkipWhitespace();
                        string key = ReadString();
                        SkipWhitespace();
                        Expect(':');
                        SkipWhitespace();
                        if (key == "Version")
                        {
                            manifest.Version = ReadString();
                            hasVersion = true;
                        }
                        else if (key == "Entries")
                        {
                            manifest.Entries.Clear();
                            ReadEntries(manifest.Entries);
                            hasEntries = true;
                        }
                        else
                        {
                            SkipValue(0);
                        }
                        SkipWhitespace();
                        char next = Next();
                        if (next == '}')
                            break;
                        if (next != ',')
                            throw Unexpected(next, _i - 1);
                    }
                }
                SkipWhitespace();
                if (_i != _s.Length)
                    throw new FormatException("unexpected data after the manifest at offset " + _i);
                if (!hasVersion)
                    throw new FormatException("the key \"Version\" is missing");
                if (!hasEntries)
                    throw new FormatException("the key \"Entries\" is missing");
                return manifest;
            }

            void ReadEntries(List<ArenaManifestEntry> entries)
            {
                Expect('[');
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _i++;
                    return;
                }
                while (true)
                {
                    SkipWhitespace();
                    entries.Add(ReadEntry(entries.Count));
                    SkipWhitespace();
                    char next = Next();
                    if (next == ']')
                        return;
                    if (next != ',')
                        throw Unexpected(next, _i - 1);
                }
            }

            ArenaManifestEntry ReadEntry(int index)
            {
                string path = null;
                long size = 0;
                bool hasSize = false;
                Expect('{');
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _i++;
                }
                else
                {
                    while (true)
                    {
                        SkipWhitespace();
                        string key = ReadString();
                        SkipWhitespace();
                        Expect(':');
                        SkipWhitespace();
                        if (key == "Path")
                        {
                            path = ReadString();
                        }
                        else if (key == "Size")
                        {
                            size = ReadSize();
                            hasSize = true;
                        }
                        else
                        {
                            SkipValue(0);
                        }
                        SkipWhitespace();
                        char next = Next();
                        if (next == '}')
                            break;
                        if (next != ',')
                            throw Unexpected(next, _i - 1);
                    }
                }
                if (path == null || !hasSize)
                    throw new FormatException("entry " + index + " lacks Path or Size");
                if (path.Length == 0)
                    throw new FormatException("entry " + index + " has an empty Path");
                return new ArenaManifestEntry { Path = path, Size = size };
            }

            string ReadString()
            {
                Expect('"');
                int start = _i;
                while (true)
                {
                    if (_i >= _s.Length)
                        throw End();
                    char c = _s[_i];
                    if (c == '"')
                    {
                        string plain = _s.Substring(start, _i - start);
                        _i++;
                        return plain;
                    }
                    if (c == '\\')
                        break;
                    if (c < ' ')
                        throw new FormatException("control character inside a string at offset " + _i);
                    _i++;
                }

                var sb = new StringBuilder();
                sb.Append(_s, start, _i - start);
                while (true)
                {
                    char c = Next();
                    if (c == '"')
                        return sb.ToString();
                    if (c < ' ')
                        throw new FormatException("control character inside a string at offset " + (_i - 1));
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    char escape = Next();
                    switch (escape)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length)
                                throw End();
                            int code;
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                throw new FormatException("invalid \\u escape at offset " + (_i - 2));
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw new FormatException("invalid escape \\" + escape + " at offset " + (_i - 2));
                    }
                }
            }

            long ReadSize()
            {
                int start = _i;
                if (Peek() == '-')
                    _i++;
                int digits = _i;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
                    _i++;
                if (_i == digits)
                    throw new FormatException("the size at offset " + start + " is not a number");
                if (_i < _s.Length && (_s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E'))
                    throw new FormatException("the size at offset " + start + " is not an integer");
                long value;
                if (!long.TryParse(_s.Substring(start, _i - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                    throw new FormatException("the size at offset " + start + " is out of range");
                if (value < 0)
                    throw new FormatException("the size at offset " + start + " is negative");
                return value;
            }

            /// <summary>Skip one JSON value of an unknown key, validating its structure.</summary>
            void SkipValue(int depth)
            {
                if (depth > MaxSkipDepth)
                    throw new FormatException("nesting is too deep at offset " + _i);
                char c = Peek();
                if (c == '"')
                {
                    ReadString();
                }
                else if (c == '{' || c == '[')
                {
                    bool isObject = c == '{';
                    char close = isObject ? '}' : ']';
                    _i++;
                    SkipWhitespace();
                    if (Peek() == close)
                    {
                        _i++;
                        return;
                    }
                    while (true)
                    {
                        SkipWhitespace();
                        if (isObject)
                        {
                            ReadString();
                            SkipWhitespace();
                            Expect(':');
                            SkipWhitespace();
                        }
                        SkipValue(depth + 1);
                        SkipWhitespace();
                        char next = Next();
                        if (next == close)
                            return;
                        if (next != ',')
                            throw Unexpected(next, _i - 1);
                    }
                }
                else if (MatchWord("true") || MatchWord("false") || MatchWord("null"))
                {
                    // consumed by MatchWord
                }
                else
                {
                    SkipNumber();
                }
            }

            bool MatchWord(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                    return false;
                _i += word.Length;
                return true;
            }

            void SkipNumber()
            {
                int start = _i;
                if (Peek() == '-')
                    _i++;
                if (SkipDigits() == 0)
                    throw new FormatException("expected a value at offset " + start);
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    if (SkipDigits() == 0)
                        throw new FormatException("invalid number at offset " + start);
                }
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-'))
                        _i++;
                    if (SkipDigits() == 0)
                        throw new FormatException("invalid number at offset " + start);
                }
            }

            int SkipDigits()
            {
                int start = _i;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
                    _i++;
                return _i - start;
            }

            void SkipWhitespace()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r'))
                    _i++;
            }

            char Peek()
            {
                if (_i >= _s.Length)
                    throw End();
                return _s[_i];
            }

            char Next()
            {
                if (_i >= _s.Length)
                    throw End();
                return _s[_i++];
            }

            void Expect(char expected)
            {
                char found = Next();
                if (found != expected)
                    throw new FormatException("expected '" + expected + "' but found '" + found + "' at offset " + (_i - 1));
            }

            static FormatException End() => new FormatException("the manifest ends unexpectedly");

            static FormatException Unexpected(char c, int offset) =>
                new FormatException("unexpected character '" + c + "' at offset " + offset);
        }
    }
}
