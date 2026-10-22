#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArenaMapPort.Crypto;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>One map of the registry as the notices see it: its key, its display name and the files the client must have for it.</summary>
    internal sealed class MapInfo
    {
        internal MapInfo(string key, string name, string presetFile, string scenesFile, string navMeshFile, string sidecarFile)
        {
            Key = key;
            Name = name;
            PresetFile = presetFile;
            ScenesFile = scenesFile;
            NavMeshFile = navMeshFile;
            SidecarFile = sidecarFile;
        }

        /// <summary>The location key (<c>arena_equator</c>), also the <c>Id</c> of the location EFT's backend lists.</summary>
        internal string Key { get; }
        /// <summary>The name a player knows: the locale name without "Arena: " ("Equator", "Bay 5").</summary>
        internal string Name { get; }
        internal string PresetFile { get; }
        internal string ScenesFile { get; }
        internal string NavMeshFile { get; }
        /// <summary>The shader sidecar next to the plugin DLL (<c>material-shaders.&lt;key&gt;.json</c>), the fourth file <c>Plugin.LogExpectedFile</c> checks.</summary>
        internal string SidecarFile { get; }

        /// <summary>The sidecar's file name; the same spelling as <c>ShaderRebind.SidecarFileName</c> (a source pin keeps them together).</summary>
        internal static string SidecarFileName(string key) => "material-shaders." + key + ".json";

        /// <summary>"Arena: Bay 5" becomes "Bay 5".</summary>
        internal static string DisplayName(string localeName)
        {
            string name = (localeName ?? "").Trim();
            const string prefix = "Arena:";
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name.Substring(prefix.Length).Trim();
            return name;
        }

        internal static MapInfo From(ArenaMapDescriptor map) =>
            new MapInfo(map.Key, DisplayName(map.LocaleName), map.PresetBundleFileName, map.ScenesBundleFileName, map.NavMeshBundleFileName, SidecarFileName(map.Key));

        /// <summary>Every map of <see cref="ArenaMapRegistry.All"/>, in registry order.</summary>
        internal static IReadOnlyList<MapInfo> FromRegistry()
        {
            var maps = new List<MapInfo>();
            foreach (ArenaMapDescriptor map in ArenaMapRegistry.All)
                maps.Add(From(map));
            return maps;
        }
    }

    /// <summary>One entry of EFT's <c>LocationSettings.locations</c>, reduced to what the offered check needs.</summary>
    internal sealed class OfferedLocation
    {
        internal OfferedLocation(string id, bool enabled)
        {
            Id = id;
            Enabled = enabled;
        }

        /// <summary>The location's <c>Id</c> (the registry key of an Arena map, <c>arena_equator</c>).</summary>
        internal string Id { get; }
        internal bool Enabled { get; }
    }

    /// <summary>
    /// The client's Arena check as the notices read it, one immutable value. <see cref="From"/> normalises the three separately read members of
    /// <see cref="ArenaInstallationGate"/> (a check that finishes between the reads cannot produce a state that contradicts its result).
    /// </summary>
    internal sealed class GateSnapshot
    {
        internal GateSnapshot(GateState state, ArenaInstallCause cause, string source, string installPath, string manifestVersion,
            int mismatchCount, string reason, string failureDetail)
        {
            State = state;
            Cause = cause;
            Source = source ?? ArenaInstallation.SourceNone;
            InstallPath = installPath;
            ManifestVersion = manifestVersion;
            MismatchCount = mismatchCount;
            Reason = reason;
            FailureDetail = failureDetail;
        }

        internal GateState State { get; }
        /// <summary>For <see cref="GateState.NotVerified"/>: the cause of the failed check; <see cref="ArenaInstallCause.None"/> otherwise.</summary>
        internal ArenaInstallCause Cause { get; }
        /// <summary>Where the folder came from: <see cref="ArenaInstallation.SourceOverride"/> (the player's setting), the registry, or none.</summary>
        internal string Source { get; }
        /// <summary>The folder the check looked at (the verified installation, or the one that failed); null when there was none.</summary>
        internal string InstallPath { get; }
        internal string ManifestVersion { get; }
        internal int MismatchCount { get; }
        /// <summary>The check's own one-sentence reason (for a detail of a notice, cut later); null when verified or unfinished.</summary>
        internal string Reason { get; }
        /// <summary>What the gate recorded when the check itself threw; null otherwise.</summary>
        internal string FailureDetail { get; }

        internal bool Verified => State == GateState.Verified;

        internal bool OverrideSource => string.Equals(Source, ArenaInstallation.SourceOverride, StringComparison.Ordinal);

        /// <summary>The snapshot of a state, normalised against <paramref name="result"/> (non-null wins over <paramref name="state"/>).</summary>
        internal static GateSnapshot From(GateState state, ArenaInstallationResult result, string failureDetail)
        {
            if (result != null)
            {
                if (result.Verified)
                    return new GateSnapshot(GateState.Verified, ArenaInstallCause.None, result.Source, result.InstallPath, result.ManifestVersion,
                        0, null, null);
                ArenaInstallCause cause = result.Cause == ArenaInstallCause.None ? ArenaInstallCause.CheckFailed : result.Cause;
                return new GateSnapshot(GateState.NotVerified, cause, result.Source, result.InstallPath, result.ManifestVersion,
                    result.MismatchCount, result.Reason, null);
            }
            if (state == GateState.Verified || state == GateState.NotVerified)
                state = GateState.CheckFailed; // a finished check without a result is a failed one
            return new GateSnapshot(state, ArenaInstallCause.None, null, null, null, 0, null, failureDetail);
        }

        /// <summary>Reads the real gate without blocking. Never throws; an unreadable gate is <see cref="GateState.CheckFailed"/>.</summary>
        internal static GateSnapshot Read()
        {
            try
            {
                ArenaInstallationResult result = ArenaInstallationGate.Result;
                string failure = ArenaInstallationGate.FailureDetail;
                GateState state = ArenaInstallationGate.State;
                return From(state, result, failure);
            }
            catch (Exception e)
            {
                return From(GateState.CheckFailed, null, e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The cause code of the "showing" line: <c>verified:none</c>, <c>not_verified:not_registered</c>, <c>checking:none</c>, <c>timed_out:none</c>, <c>check_failed:check_failed</c>.</summary>
        internal string LogCode
        {
            get
            {
                switch (State)
                {
                    case GateState.Verified: return "verified:none";
                    case GateState.NotVerified: return "not_verified:" + ArenaStatusContract.CauseCode(Cause);
                    case GateState.Running: return "checking:none";
                    case GateState.TimedOut: return "timed_out:none";
                    case GateState.CheckFailed: return "check_failed:check_failed";
                    default: return "not_started:check_failed";
                }
            }
        }

        /// <summary>The <c>arena=</c> value of the availability line: verified, not_verified:cause, pending or failed.</summary>
        internal string AvailabilityCode
        {
            get
            {
                switch (State)
                {
                    case GateState.Verified: return "verified";
                    case GateState.NotVerified: return "not_verified:" + ArenaStatusContract.CauseCode(Cause);
                    case GateState.Running:
                    case GateState.TimedOut:
                        return "pending";
                    default: return "failed";
                }
            }
        }
    }

    /// <summary>Where the status probe stands.</summary>
    public enum ProbeState
    {
        NotAsked,
        Pending,
        /// <summary>The server answered and the document was read (<see cref="ProbeView.Status"/>).</summary>
        Ok,
        /// <summary>HTTP 404: a server without the route (an older ArenaMapPort).</summary>
        NotFound,
        /// <summary>Timeout, another HTTP status, a transport error.</summary>
        Failed,
        /// <summary>The client has no SPT HTTP types (or they differ from what was expected): the probe cannot run.</summary>
        Unavailable,
        /// <summary>
        /// HTTP 200, but the document is one this client cannot read (a schema it does not know, no schema or gate, not JSON): a server of another version.
        /// Design section 4.3: "a missing or higher schema reads as unavailable", so the notice is the one for a server that did not say why.
        /// </summary>
        Unreadable,
    }

    /// <summary>The probe's result: its state, and the document when it is <see cref="ProbeState.Ok"/>.</summary>
    internal sealed class ProbeView
    {
        internal ProbeView(ProbeState state, ServerStatusView status = null)
        {
            State = state;
            Status = status;
        }

        internal ProbeState State { get; }
        internal ServerStatusView Status { get; }

        internal static ProbeView NotAsked { get; } = new ProbeView(ProbeState.NotAsked);

        /// <summary>The <c>server=</c> value of the log lines: not_needed, pending, ok:gate.state/gate.cause, 404, failed, no_http or unreadable.</summary>
        internal string LogCode
        {
            get
            {
                switch (State)
                {
                    case ProbeState.NotAsked: return "not_needed";
                    case ProbeState.Pending: return "pending";
                    case ProbeState.Ok:
                        return Status == null ? "ok" : "ok:" + Status.GateState + "/" + Status.GateCauseCode;
                    case ProbeState.NotFound: return "404";
                    case ProbeState.Unavailable: return "no_http";
                    case ProbeState.Unreadable: return "unreadable";
                    default: return "failed";
                }
            }
        }
    }

    /// <summary>The file-system seam of the client's file check: tests supply their own.</summary>
    internal interface IFileProbe
    {
        bool Exists(string path);

        /// <summary>True when the file exists and starts with the AMPB magic (four bytes read).</summary>
        bool IsAmpb(string path);
    }

    /// <summary>The real disk. Never throws.</summary>
    internal sealed class DiskFileProbe : IFileProbe
    {
        public bool Exists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public bool IsAmpb(string path)
        {
            try
            {
                return Ampb.IsAmpb(path);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>What the client file check found missing.</summary>
    internal sealed class FileScan
    {
        internal FileScan(int mapsChecked, IReadOnlyList<string> missingFiles, IReadOnlyList<string> mapNames)
        {
            MapsChecked = mapsChecked;
            MissingFiles = missingFiles ?? new string[0];
            MapNames = mapNames ?? new string[0];
        }

        internal int MapsChecked { get; }
        /// <summary>The names of the missing files in the fixed order (registry order; per map preset, scenes, navmesh, sidecar, key file).</summary>
        internal IReadOnlyList<string> MissingFiles { get; }
        /// <summary>The display names of the maps that have a missing file, registry order.</summary>
        internal IReadOnlyList<string> MapNames { get; }

        internal int MissingCount => MissingFiles.Count;
        /// <summary>The first missing file in the fixed order, or null.</summary>
        internal string FirstMissing => MissingFiles.Count == 0 ? null : MissingFiles[0];

        internal static FileScan Clean(int mapsChecked = 0) => new FileScan(mapsChecked, null, null);
    }

    /// <summary>Everything the selector decides on, built by <see cref="MapNoticeInputs"/> from plain values.</summary>
    internal sealed class MapAvailabilityInput
    {
        internal MapAvailabilityInput(GateSnapshot gate, IReadOnlyList<MapInfo> maps, IReadOnlyList<string> offered, ProbeView probe,
            FileScan files, IReadOnlyList<string> skippedHooks, string clientVersion, bool noticesEnabled)
        {
            Gate = gate;
            Maps = maps ?? new MapInfo[0];
            Offered = offered;
            Probe = probe ?? ProbeView.NotAsked;
            Files = files;
            SkippedHooks = skippedHooks ?? new string[0];
            ClientVersion = clientVersion;
            NoticesEnabled = noticesEnabled;
        }

        internal GateSnapshot Gate { get; }
        /// <summary>The registry maps, in registry order.</summary>
        internal IReadOnlyList<MapInfo> Maps { get; }
        /// <summary>The registry keys EFT's backend offers (enabled locations); null when the backend's location list could not be read.</summary>
        internal IReadOnlyList<string> Offered { get; }
        internal ProbeView Probe { get; }
        /// <summary>The file check, or null when it was not run.</summary>
        internal FileScan Files { get; }
        /// <summary>Names of the map-critical Harmony targets that <c>HarmonyPatches.Verify</c> skipped.</summary>
        internal IReadOnlyList<string> SkippedHooks { get; }
        internal string ClientVersion { get; }
        /// <summary>General/ShowMapNotices.</summary>
        internal bool NoticesEnabled { get; }

        internal bool OfferedKnown => Offered != null;

        /// <summary>The registry maps the backend does not offer; empty when the offered list is unknown.</summary>
        internal IReadOnlyList<MapInfo> MissingMaps
        {
            get
            {
                var missing = new List<MapInfo>();
                if (Offered == null)
                    return missing;
                foreach (MapInfo map in Maps)
                    if (!Offered.Contains(map.Key))
                        missing.Add(map);
                return missing;
            }
        }

        /// <summary>The maps the file check covers: the offered ones, or every registry map when the offered list is unknown.</summary>
        internal IReadOnlyList<MapInfo> ScopeMaps
        {
            get
            {
                if (Offered == null)
                    return Maps;
                var scope = new List<MapInfo>();
                foreach (MapInfo map in Maps)
                    if (Offered.Contains(map.Key))
                        scope.Add(map);
                return scope;
            }
        }

        internal MapAvailabilityInput WithProbe(ProbeView probe) =>
            new MapAvailabilityInput(Gate, Maps, Offered, probe, Files, SkippedHooks, ClientVersion, NoticesEnabled);

        internal MapAvailabilityInput WithFiles(FileScan files) =>
            new MapAvailabilityInput(Gate, Maps, Offered, Probe, files, SkippedHooks, ClientVersion, NoticesEnabled);
    }

    /// <summary>
    /// The pure builder behind the main-menu notice (MapNotices, docs/progress/map-notices.md): plain values in, a <see cref="MapAvailabilityInput"/> out.
    ///
    /// What vanilla does. Nothing: EFT lists whatever locations the server registered and says nothing about the ones that are absent.
    ///
    /// What this class does. The offered check (<see cref="OfferedKeys"/>: a registry map is offered when the backend lists an enabled location
    /// with its key), the cause-to-row mapping of the client's own Arena check (<see cref="ClassifyClient"/>: rows 1 to 6 and 8 of the design), the rule when
    /// the server must be asked (<see cref="NeedsProbe"/>: a verified client with a registry map the backend does not offer) and the client file check
    /// (<see cref="ScanFiles"/>: the three bundles and the shader sidecar of every map in scope exist, and the key file does when the preset bundle is an
    /// AMPB container, so a development build with plaintext bundles gets no false notice; <c>File.Exists</c> and four bytes only, no key is resolved).
    /// Nothing here touches Unity, EFT or the log, and every file access goes through <see cref="IFileProbe"/>.
    ///
    /// Host test: docs/progress/map-notices.md (V1 to V3).
    /// </summary>
    internal static class MapNoticeInputs
    {
        internal const string BundlesFolder = "bundles";

        /// <summary>The registry keys the backend offers (enabled), in registry order; null when <paramref name="locations"/> is null (the list was not readable).</summary>
        internal static IReadOnlyList<string> OfferedKeys(IReadOnlyList<MapInfo> maps, IEnumerable<OfferedLocation> locations)
        {
            if (locations == null)
                return null;
            var enabled = new HashSet<string>(StringComparer.Ordinal);
            foreach (OfferedLocation location in locations)
                if (location != null && location.Enabled && !string.IsNullOrEmpty(location.Id))
                    enabled.Add(location.Id);
            var offered = new List<string>();
            if (maps != null)
                foreach (MapInfo map in maps)
                    if (enabled.Contains(map.Key))
                        offered.Add(map.Key);
            return offered;
        }

        /// <summary>
        /// Builds the input. <paramref name="locations"/> null means the backend's location list is unknown; <paramref name="files"/> null means the file
        /// check was not run; <paramref name="probe"/> null means the server was not asked.
        /// </summary>
        internal static MapAvailabilityInput Build(GateSnapshot gate, IReadOnlyList<MapInfo> maps, IEnumerable<OfferedLocation> locations,
            FileScan files, ProbeView probe, IReadOnlyList<string> skippedHooks, string clientVersion, bool noticesEnabled)
        {
            if (gate == null)
                gate = GateSnapshot.From(GateState.NotStarted, null, null);
            if (maps == null)
                maps = MapInfo.FromRegistry();
            return new MapAvailabilityInput(gate, maps, OfferedKeys(maps, locations), probe, files, skippedHooks, clientVersion, noticesEnabled);
        }

        /// <summary>True when the server should be asked why: Arena is verified here and a registry map is not offered (offered list known).</summary>
        internal static bool NeedsProbe(MapAvailabilityInput input) =>
            input != null && input.Gate != null && input.Gate.Verified && input.OfferedKnown && input.MissingMaps.Count > 0;

        /// <summary>
        /// Rows 1 to 6 and 8 of the design: the client notice that the gate's state and cause call for, or false when the gate has nothing to
        /// say (verified, or the check is still running).
        /// </summary>
        internal static bool ClassifyClient(GateSnapshot gate, out NoticeId id, out NoticeWhy why)
        {
            id = NoticeId.ClientCheckFailed;
            why = NoticeWhy.None;
            if (gate == null)
                return true;
            switch (gate.State)
            {
                case GateState.Verified:
                case GateState.Running:
                    return false;
                case GateState.TimedOut:
                    id = NoticeId.ClientCheckSlow;
                    return true;
                case GateState.NotStarted:
                case GateState.CheckFailed:
                    id = NoticeId.ClientCheckFailed;
                    return true;
            }

            switch (gate.Cause)
            {
                case ArenaInstallCause.NotRegistered:
                    id = NoticeId.ClientNoArena;
                    return true;
                case ArenaInstallCause.RegistryUnavailable:
                    id = NoticeId.ClientRegistryUnreadable;
                    return true;
                case ArenaInstallCause.FolderMissing:
                case ArenaInstallCause.NotArenaFolder:
                case ArenaInstallCause.ManifestMissing:
                case ArenaInstallCause.ManifestInvalid:
                    id = gate.OverrideSource ? NoticeId.ClientOverrideBad : NoticeId.ClientFolderBad;
                    why = gate.Cause == ArenaInstallCause.FolderMissing ? NoticeWhy.FolderMissing
                        : gate.Cause == ArenaInstallCause.NotArenaFolder ? NoticeWhy.ExeMissing
                        : NoticeWhy.ManifestBad;
                    return true;
                case ArenaInstallCause.FilesMismatch:
                    id = NoticeId.ClientIncomplete;
                    return true;
                default:
                    id = NoticeId.ClientCheckFailed; // CheckFailed, or a failed check that named no cause
                    return true;
            }
        }

        /// <summary>
        /// The client file check for <paramref name="scope"/>: per map the three bundles and the shader sidecar in <paramref name="pluginDirectory"/>
        /// (bundles below <c>bundles</c>), and <c>keys\&lt;map key&gt;.json</c> when the preset bundle starts with the AMPB magic. Never throws.
        /// </summary>
        internal static FileScan ScanFiles(string pluginDirectory, IReadOnlyList<MapInfo> scope, IFileProbe files)
        {
            var missing = new List<string>();
            var names = new List<string>();
            int checkedMaps = 0;
            try
            {
                if (files == null)
                    files = new DiskFileProbe();
                string dir = pluginDirectory ?? ".";
                string bundles = Path.Combine(dir, BundlesFolder);
                string keys = Path.Combine(dir, BundleKeyResolver.KeysFolder);
                if (scope != null)
                    foreach (MapInfo map in scope)
                    {
                        checkedMaps++;
                        int before = missing.Count;
                        string preset = Path.Combine(bundles, map.PresetFile);
                        bool presetExists = files.Exists(preset);
                        if (!presetExists)
                            missing.Add(map.PresetFile);
                        if (!files.Exists(Path.Combine(bundles, map.ScenesFile)))
                            missing.Add(map.ScenesFile);
                        if (!files.Exists(Path.Combine(bundles, map.NavMeshFile)))
                            missing.Add(map.NavMeshFile);
                        if (!files.Exists(Path.Combine(dir, map.SidecarFile)))
                            missing.Add(map.SidecarFile);
                        if (presetExists && files.IsAmpb(preset))
                        {
                            string keyFile = map.Key + ".json";
                            if (!files.Exists(Path.Combine(keys, keyFile)))
                                missing.Add(keyFile);
                        }
                        if (missing.Count > before)
                            names.Add(map.Name);
                    }
            }
            catch
            {
                // a scan that cannot run says what it found so far
            }
            return new FileScan(checkedMaps, missing, names);
        }

        /// <summary>The comma list of map names a text puts into <c>{maps}</c>.</summary>
        internal static string JoinNames(IEnumerable<string> names) => string.Join(", ", names ?? new string[0]);

        /// <summary>The <c>offered=n/m [keys]</c> part of the availability line (<c>?/m</c> when unknown).</summary>
        internal static string DescribeOffered(MapAvailabilityInput input)
        {
            if (input.Offered == null)
                return "offered=?/" + input.Maps.Count;
            return "offered=" + input.Offered.Count + "/" + input.Maps.Count + " [" + string.Join(",", input.Offered) + "]";
        }

        /// <summary>The <c>files=</c> value: ok, "n missing" or skipped (not run).</summary>
        internal static string DescribeFiles(MapAvailabilityInput input)
        {
            if (input.Files == null)
                return "skipped";
            return input.Files.MissingCount == 0 ? "ok" : input.Files.MissingCount + " missing";
        }

        /// <summary>
        /// The Debug availability line without its tag: <c>availability: arena=&lt;verified|not_verified:cause|pending|failed&gt;, offered=n/m [keys],
        /// files=&lt;ok|n missing|skipped&gt;, server=&lt;...&gt;</c>.
        /// </summary>
        internal static string DescribeAvailability(MapAvailabilityInput input)
        {
            var line = new StringBuilder("availability: arena=");
            line.Append(input.Gate.AvailabilityCode)
                .Append(", ").Append(DescribeOffered(input))
                .Append(", files=").Append(DescribeFiles(input))
                .Append(", server=").Append(input.Probe.LogCode);
            if (input.SkippedHooks.Count > 0)
                line.Append(", skipped hooks=").Append(string.Join(",", input.SkippedHooks));
            return line.ToString();
        }

        /// <summary>The <c>client=... server=... offered=...</c> fragment of the "showing" Info line.</summary>
        internal static string DescribeSummary(MapAvailabilityInput input)
        {
            string offered = input.Offered == null ? "?" : input.Offered.Count.ToString();
            return "client=" + input.Gate.LogCode + " server=" + input.Probe.LogCode + " offered=" + offered + "/" + input.Maps.Count;
        }
    }
}
