#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using ArenaMapPort.Crypto;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What <see cref="BundleKeyResolver"/> needs from its surroundings, injectable so the whole key resolution runs in tests without Unity, BepInEx
    /// or a real Arena: the stage-1 gate (<see cref="ArenaInstallationGate"/>), the folder with the shipped key files, access to the Arena level
    /// files, the crypto provider and the log. Every method is called from the game thread.
    /// </summary>
    internal interface IBundleKeyEnvironment
    {
        /// <summary>Stage 1: waits (bounded) for the Arena installation check and says whether an installation was verified. Never throws.</summary>
        bool EnsureVerified();
        /// <summary>The verified installation folder, or null when Arena is not verified.</summary>
        string InstallPath { get; }
        /// <summary>The launcher manifest version of the verified installation (informational), or null.</summary>
        string InstalledVersion { get; }
        /// <summary>The folder with the shipped <c>&lt;map_key&gt;.json</c> key files (<c>&lt;plugin dir&gt;/keys</c>).</summary>
        string KeysDirectory { get; }
        /// <summary>Access to the files below the installation folder.</summary>
        IArenaFiles OpenArenaFiles(string installPath);
        /// <summary>The crypto provider; <paramref name="warning"/> receives the one-line "CNG unavailable" text the first time it applies.</summary>
        ICryptoProvider CryptoProvider(Action<string> warning);
        void LogDebug(string message);
        void LogWarning(string message);
        void LogError(string message);
    }

    /// <summary>The real environment: the stage-1 gate, the plugin's own folder, the files of the installation, Windows CNG and the BepInEx log.</summary>
    internal sealed class PluginBundleKeyEnvironment : IBundleKeyEnvironment
    {
        public bool EnsureVerified() => ArenaInstallationGate.EnsureVerified();

        public string InstallPath
        {
            get
            {
                var result = ArenaInstallationGate.Result;
                return result != null && result.Verified ? result.InstallPath : null;
            }
        }

        public string InstalledVersion => ArenaInstallationGate.Result?.ManifestVersion;

        public string KeysDirectory =>
            Path.Combine(Path.GetDirectoryName(typeof(PluginBundleKeyEnvironment).Assembly.Location) ?? ".", BundleKeyResolver.KeysFolder);

        public IArenaFiles OpenArenaFiles(string installPath) => new DirectoryArenaFiles(installPath);

        public ICryptoProvider CryptoProvider(Action<string> warning) => CryptoProviders.Select(warning);

        public void LogDebug(string message) => Plugin.Log.LogDebug(message);

        public void LogWarning(string message) => Plugin.Log.LogWarning(message);

        public void LogError(string message) => Plugin.Log.LogError(message);
    }

    /// <summary>What <see cref="BundleKeyResolver.Prepare"/> found at a bundle path.</summary>
    internal enum PreparedKind
    {
        /// <summary>The bundle cannot be used; the reason is already logged.</summary>
        Refused,
        /// <summary>No AMPB magic: a plaintext bundle (development builds), loaded from the file as before.</summary>
        Plaintext,
        /// <summary>An AMPB container opened with its key: <see cref="PreparedBundle.Stream"/> is the decrypting stream to hand to Unity.</summary>
        Encrypted,
    }

    /// <summary>
    /// One bundle file ready to load: either a plaintext file (load it from the path) or the decrypting stream of an AMPB container. Owns the
    /// stream; <see cref="Dispose"/> closes it and may be called more than once.
    /// </summary>
    internal sealed class PreparedBundle : IDisposable
    {
        internal PreparedBundle(PreparedKind kind, string filePath, string mapKey, string servedKey, bool navMesh, AmpbReadStream stream)
        {
            Kind = kind;
            FilePath = filePath;
            MapKey = mapKey;
            ServedKey = servedKey;
            NavMesh = navMesh;
            Stream = stream;
        }

        internal PreparedKind Kind { get; }
        internal string FilePath { get; }
        internal string FileName => Path.GetFileName(FilePath ?? "");
        internal string MapKey { get; }
        /// <summary>The key EFT asked for (the Bundles prefix path); null for the NavMesh bundle, which the installer loads itself.</summary>
        internal string ServedKey { get; }
        internal bool NavMesh { get; }
        /// <summary>The decrypting stream; null unless <see cref="Kind"/> is Encrypted.</summary>
        internal AmpbReadStream Stream { get; }
        /// <summary>Set once the stream's integrity error was logged (an error is reported once per stream).</summary>
        internal bool ErrorLogged { get; set; }

        public void Dispose()
        {
            Stream?.Dispose();
        }
    }

    /// <summary>
    /// The streams of the encrypted bundles that Unity keeps open. <c>AssetBundle.LoadFromStream</c> reads its stream for the whole lifetime of the
    /// bundle and never closes it, so someone has to: <see cref="Maintain"/> disposes the stream of every tracked bundle that is no longer alive
    /// (EFT unloaded it, see <see cref="Track"/>) and reports a damaged chunk once, from the game thread. A bundle that is still loaded keeps its
    /// stream across raids, including after its map's scene unloaded: EFT keeps a loaded map bundle cached and the next raid reuses it, so a
    /// stream must never be closed on a scene unload. Counters feed the Debug line of <see cref="StatsLine"/>. Nothing here logs from a thread
    /// other than the caller's.
    /// </summary>
    internal sealed class BundleStreamRegistry
    {
        sealed class Entry
        {
            internal PreparedBundle Bundle;
            internal Func<bool> IsAlive;
        }

        readonly object _sync = new object();
        readonly List<Entry> _entries = new List<Entry>();
        readonly IBundleKeyEnvironment _env;
        readonly MapLoadFailures _failures;

        /// <summary>
        /// <paramref name="failures"/> receives the damaged chunks <see cref="ReportError"/> finds (the in-game map notices read them); null keeps no record.
        /// </summary>
        internal BundleStreamRegistry(IBundleKeyEnvironment env, MapLoadFailures failures = null)
        {
            _env = env;
            _failures = failures;
        }

        /// <summary>How many streams are tracked.</summary>
        internal int Count
        {
            get { lock (_sync) return _entries.Count; }
        }

        /// <summary>
        /// Starts tracking an encrypted bundle whose AssetBundle was loaded from <paramref name="bundle"/>'s stream. <paramref name="isAlive"/> says
        /// whether the AssetBundle is still loaded (BundleHost: EFT's own bundle table still holds it); once it is false the stream is disposed
        /// by the next <see cref="Maintain"/>. A plaintext or refused bundle has no stream and is ignored.
        /// </summary>
        internal void Track(PreparedBundle bundle, Func<bool> isAlive)
        {
            if (bundle == null || bundle.Stream == null)
                return;
            lock (_sync)
                _entries.Add(new Entry { Bundle = bundle, IsAlive = isAlive });
        }

        /// <summary>
        /// Disposes the streams of bundles that are gone and reports the integrity errors of the others (once per stream). Called from the game
        /// thread at the places where it is at a loading boundary anyway: a bundle request, raid start, scene unload.
        /// </summary>
        internal void Maintain()
        {
            Entry[] snapshot;
            lock (_sync)
            {
                if (_entries.Count == 0)
                    return;
                snapshot = _entries.ToArray();
            }

            List<Entry> gone = null;
            foreach (var entry in snapshot)
            {
                bool alive;
                try
                {
                    alive = entry.IsAlive == null || entry.IsAlive();
                }
                catch
                {
                    alive = false; // a probe that throws must not keep a handle open forever
                }

                if (!alive)
                {
                    if (gone == null)
                        gone = new List<Entry>();
                    gone.Add(entry);
                    continue;
                }
                ReportError(entry.Bundle);
            }

            if (gone == null)
                return;
            lock (_sync)
            {
                foreach (var entry in gone)
                    _entries.Remove(entry);
            }
            foreach (var entry in gone)
            {
                ReportError(entry.Bundle);
                try
                {
                    _env.LogDebug(StatsLine(entry.Bundle, "bundle unloaded"));
                }
                catch
                {
                    // statistics are diagnostics; never let them keep the handle open
                }
                entry.Bundle.Dispose();
            }
        }

        /// <summary>Logs the damaged chunk of <paramref name="bundle"/>'s stream, once. A no-op for a plaintext bundle or a stream that read cleanly.</summary>
        internal void ReportError(PreparedBundle bundle)
        {
            if (bundle == null || bundle.Stream == null || bundle.ErrorLogged)
                return;
            AmpbException error;
            try
            {
                error = bundle.Stream.FirstError;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (error == null)
                return;
            bundle.ErrorLogged = true;
            NoteIntegrity(bundle, error);
            _env.LogError(IntegrityErrorLine(bundle, error));
        }

        // A damaged chunk of a live scenes or preset stream is an Integrity record (blocking inside a failed attempt, a late toast outside one); the
        // NavMesh bundle's own damage is a NavMesh record, which never blocks a raid. Never throws.
        void NoteIntegrity(PreparedBundle bundle, AmpbException error)
        {
            try
            {
                if (_failures == null)
                    return;
                string detail = "chunk " + error.Chunk.ToString(CultureInfo.InvariantCulture) + " failed its integrity check";
                if (bundle.NavMesh)
                    _failures.NoteNavMesh(bundle.MapKey, NavMeshCause.Damaged, detail, bundle.FileName);
                else
                    _failures.NoteIntegrity(bundle.MapKey, bundle.FileName, detail);
            }
            catch
            {
                // a record that cannot be written changes nothing about the load
            }
        }

        /// <summary>Maintains the registry, then logs the counters of every tracked stream of <paramref name="mapKey"/> at Debug.</summary>
        internal void LogStats(string mapKey, string reason)
        {
            Maintain();
            Entry[] snapshot;
            lock (_sync)
                snapshot = _entries.ToArray();
            foreach (var entry in snapshot)
            {
                if (!string.Equals(entry.Bundle.MapKey, mapKey, StringComparison.Ordinal))
                    continue;
                try
                {
                    _env.LogDebug(StatsLine(entry.Bundle, reason));
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>
        /// D3: <c>[Bundles] stream '&lt;file&gt;' (&lt;reason&gt;): &lt;x&gt; MiB decrypted in &lt;c&gt; chunks, cache hits &lt;h&gt; / misses &lt;m&gt;, crypto &lt;ms&gt; ms</c>.
        /// "Decrypted" counts a chunk again every time it is decrypted after eviction, which is the read amplification the load budget watches.
        /// </summary>
        internal static string StatsLine(PreparedBundle bundle, string reason)
        {
            AmpbStats stats = bundle.Stream.Stats;
            return BundleKeyResolver.BundlesTag + " stream '" + bundle.FileName + "' (" + reason + "): " +
                   Number(stats.BytesDecrypted / 1048576.0) + " MiB decrypted in " +
                   stats.ChunksDecrypted.ToString(CultureInfo.InvariantCulture) + " chunks, cache hits " +
                   stats.CacheHits.ToString(CultureInfo.InvariantCulture) + " / misses " +
                   stats.CacheMisses.ToString(CultureInfo.InvariantCulture) + ", crypto " +
                   ((long)Math.Round(stats.CryptoMilliseconds)).ToString(CultureInfo.InvariantCulture) + " ms";
        }

        /// <summary>
        /// E8: <c>'&lt;file&gt;' failed its integrity check at chunk &lt;i&gt; (offset &lt;o&gt;); the file is damaged, reinstall ArenaMapPort</c>, tagged
        /// [NavMesh] for the NavMesh bundle and [Bundles] for the others. The offset is the chunk's position in the decrypted bundle.
        /// </summary>
        internal static string IntegrityErrorLine(PreparedBundle bundle, AmpbException error)
        {
            long offset = error.Chunk >= 0 ? (long)error.Chunk * bundle.Stream.Header.ChunkSize : 0;
            return (bundle.NavMesh ? BundleKeyResolver.NavMeshTag : BundleKeyResolver.BundlesTag) + " '" + bundle.FileName +
                   "' failed its integrity check at chunk " + error.Chunk.ToString(CultureInfo.InvariantCulture) + " (offset " +
                   offset.ToString(CultureInfo.InvariantCulture) + "); the file is damaged, reinstall ArenaMapPort";
        }

        static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Per-map content key resolution and the opening of encrypted map bundles (stage 2 of the Arena gate, docs/PLAN-ENCRYPTION.md).
    ///
    /// What vanilla does. Nothing: EFT never meets our bundles except through <see cref="BundleHost.TryServe"/> and
    /// <see cref="NavMeshInstaller"/>, which read them from the plugin folder.
    ///
    /// What the shipped bundles lack. Since stage 2 each <c>*.bundle</c> is an AMPB container (AES-256-CTR in 1 MiB chunks) that Unity cannot read.
    /// Its content key K_map is not in the release: the release carries <c>keys/&lt;map_key&gt;.json</c> instead, which lists Arena files of that map
    /// (paths and sizes: since 2026-10-20 the raw resource streams <c>sharedassets&lt;N&gt;.assets.resS</c> / <c>.resource</c> of its scenes, which Arena's
    /// 0.6.0 to 0.6.1 hotfix left byte-identical while it rewrote every level file) and the key wrapped under a key derived from a fixed sample of
    /// those files' bytes. This is not copy protection: anyone with the Arena files, or with a derived key, can read the bundles.
    ///
    /// What this class does. <see cref="TryGetKey"/> resolves the key of one map once per session (requires the verified stage-1 gate and its
    /// installation folder): sizes of the listed files are checked first, the sample of each file is hashed (a few MB per map, not the hundreds of MB
    /// the files hold), the key-wrapping key is derived and the check value verified before the key is decrypted (all of it is
    /// <see cref="KeyResolver"/>, shared with the build tool, docs/PLAN-ENCRYPTION.md section 6). Success and failure are cached
    /// per map under a per-map lock, so eight concurrent callers cause one derivation; a failure logs its one Error line (E1-E6) once, later
    /// callers get E9 only. Arena not verified is not cached and logs nothing here (stage 1 owns that message). <see cref="Prepare"/> opens a bundle
    /// path: an AMPB container becomes a seekable decrypting stream for <c>AssetBundle.LoadFromStream</c>, anything else is a plaintext bundle
    /// (development builds, no switch) loaded from the file as before. Nothing in here calls Unity, so the whole logic is unit tested
    /// (BundleKeyResolverTests); the loaders are <see cref="BundleHost.LoadAssetBundle"/> and <see cref="NavMeshInstaller"/>.
    ///
    /// Log tags: key resolution [BundleKeys], served bundles [Bundles], the NavMesh bundle [NavMesh]. Every line comes from the game thread.
    /// Host tests: GPU-2 and GPU-1 on 2026-10-20 (docs/progress/bundle-encryption.md E5 to E7, and "Key source v2" for the sampled resource streams).
    /// </summary>
    internal sealed class BundleKeyResolver
    {
        internal const string KeyTag = "[BundleKeys]";
        internal const string BundlesTag = "[Bundles]";
        internal const string NavMeshTag = "[NavMesh]";
        internal const string KeysFolder = "keys";

        /// <summary>Decrypted-chunk cache slots of one stream (1 MiB each, allocated lazily).</summary>
        internal const int CacheSlots = 16;

        /// <summary>Unity's managed read buffer for <c>AssetBundle.LoadFromStream</c>: one chunk (D13 of the plan).</summary>
        internal const uint ReadBufferSize = 1u << 20;

        // The cached outcome of one map's key derivation. A failure keeps what KeyResolver said (cause, one-line detail, the Arena build the key file was built
        // for and the installed one, the Arena file it names) and the key file's name, so every later refusal of that map can be recorded for the in-game map
        // notices without deriving again. It holds no key material but the key itself.
        sealed class KeyEntry
        {
            internal bool Ok;
            internal byte[] Key;
            internal KeyFailure Cause;
            internal string Detail;
            internal string BuiltFor;
            internal string InstalledVersion;
            internal string FailedPath;
            internal string KeyFile;
        }

        static BundleKeyResolver _default;

        readonly IBundleKeyEnvironment _env;
        readonly object _sync = new object();
        readonly Dictionary<string, object> _mapLocks = new Dictionary<string, object>(StringComparer.Ordinal);
        readonly Dictionary<string, KeyEntry> _cache = new Dictionary<string, KeyEntry>(StringComparer.Ordinal);
        int _derivations;

        internal BundleKeyResolver(IBundleKeyEnvironment env)
        {
            _env = env ?? throw new ArgumentNullException(nameof(env));
            Failures = new MapLoadFailures();
            Streams = new BundleStreamRegistry(env, Failures);
        }

        /// <summary>The process-wide resolver (the real environment). Creating it touches no file and no crypto.</summary>
        internal static BundleKeyResolver Default
        {
            get
            {
                var resolver = Volatile.Read(ref _default);
                if (resolver != null)
                    return resolver;
                Interlocked.CompareExchange(ref _default, new BundleKeyResolver(new PluginBundleKeyEnvironment()), null);
                return _default;
            }
        }

        /// <summary>The streams Unity keeps open for the encrypted bundles that were loaded.</summary>
        internal BundleStreamRegistry Streams { get; }

        /// <summary>
        /// What went wrong while a map's bundles were being served, per map and in order, for the in-game map notices (MapNotices reads it after a failed
        /// raid attempt). Written only through <see cref="NoteSafe"/>.
        /// </summary>
        internal MapLoadFailures Failures { get; }

        /// <summary>
        /// Writes one record into <see cref="Failures"/> and never throws: the loaders run inside EFT's bundle request and raid start, and a note must not
        /// be able to break them. A source pin keeps BundleHost and NavMeshInstaller from calling the registry any other way.
        /// </summary>
        internal void NoteSafe(Action<MapLoadFailures> note)
        {
            try
            {
                note(Failures);
            }
            catch
            {
                // a record that cannot be written changes nothing about the load
            }
        }

        /// <summary>How many key derivations ran (a derivation = one <see cref="KeyResolver.ResolveFile"/> call); for tests.</summary>
        internal int Derivations => Volatile.Read(ref _derivations);

        // ---- key resolution -----------------------------------------------------------------------------------

        /// <summary>
        /// The content key of <paramref name="mapKey"/>: derived from the player's Arena files on the first call of the session and cached, for success
        /// and failure alike. False when the stage-1 gate is not verified (nothing is read, logged or cached: stage 1 already said why, and a later
        /// check may still verify) or when the key cannot be recovered (the Error line was logged once, by the first caller). The returned array is
        /// shared: never modify or clear it.
        /// </summary>
        internal bool TryGetKey(string mapKey, out byte[] key)
        {
            key = null;
            if (string.IsNullOrEmpty(mapKey))
                return false;

            object mapLock;
            lock (_sync)
            {
                if (!_mapLocks.TryGetValue(mapKey, out mapLock))
                {
                    mapLock = new object();
                    _mapLocks[mapKey] = mapLock;
                }
            }

            lock (mapLock)
            {
                KeyEntry cached;
                lock (_sync)
                    _cache.TryGetValue(mapKey, out cached);
                if (cached != null)
                {
                    key = cached.Key;
                    return cached.Ok;
                }

                bool verified;
                try
                {
                    verified = _env.EnsureVerified();
                }
                catch
                {
                    verified = false;
                }
                if (!verified)
                    return false;

                KeyEntry entry = Derive(mapKey);
                lock (_sync)
                    _cache[mapKey] = entry;
                key = entry.Key;
                return entry.Ok;
            }
        }

        /// <summary>One derivation: reads the key file, hashes the sample of each listed Arena file, logs D1 or the E-line. Never throws.</summary>
        KeyEntry Derive(string mapKey)
        {
            string keyFile = null;
            string install = null;
            try
            {
                if (!Ampb.IsValidMapKey(mapKey))
                    return Fail(mapKey, KeyFailure.Internal, "ArgumentException: '" + mapKey + "' is not a valid map key", null, null);
                install = _env.InstallPath;
                if (string.IsNullOrWhiteSpace(install))
                    return Fail(mapKey, KeyFailure.Internal, "InvalidOperationException: the verified Arena installation has no folder", null, null);

                keyFile = Path.Combine(_env.KeysDirectory ?? ".", mapKey + ".json");
                ICryptoProvider provider = Provider();
                IArenaFiles files = _env.OpenArenaFiles(install);
                Interlocked.Increment(ref _derivations);
                KeyResolution resolution = KeyResolver.ResolveFile(keyFile, install, _env.InstalledVersion, files, provider, mapKey);
                if (resolution.Ok)
                {
                    _env.LogDebug(DerivedLine(mapKey, resolution));
                    return new KeyEntry { Ok = true, Key = resolution.MapKey };
                }

                _env.LogError(FailureLine(mapKey, resolution, keyFile, install));
                return new KeyEntry
                {
                    Ok = false, Cause = resolution.Cause, Detail = resolution.Detail, BuiltFor = resolution.BuiltFor,
                    InstalledVersion = resolution.InstalledVersion ?? _env.InstalledVersion, FailedPath = resolution.FailedPath,
                    KeyFile = Path.GetFileName(keyFile),
                };
            }
            catch (Exception e)
            {
                return Fail(mapKey, KeyFailure.Internal, e.GetType().Name + ": " + e.Message, keyFile, install);
            }
        }

        /// <summary>A derivation that failed before or outside <see cref="KeyResolver"/> (E6): logs the line, caches the failure.</summary>
        KeyEntry Fail(string mapKey, KeyFailure cause, string detail, string keyFile, string install)
        {
            try
            {
                _env.LogError(FailureLine(mapKey, detail, cause, keyFile, install, null));
            }
            catch
            {
                // the entry below is what matters: the failure is cached either way
            }
            return new KeyEntry
            {
                Ok = false, Cause = cause, Detail = detail, InstalledVersion = SafeInstalledVersion(),
                KeyFile = string.IsNullOrEmpty(keyFile) ? mapKey + ".json" : Path.GetFileName(keyFile),
            };
        }

        string SafeInstalledVersion()
        {
            try
            {
                return _env.InstalledVersion;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The crypto provider; the one Warning W1 (CNG unavailable) is raised through the environment's log, once per process.</summary>
        ICryptoProvider Provider()
        {
            return _env.CryptoProvider(text => _env.LogWarning(BundlesTag + " " + text));
        }

        // ---- opening a bundle ---------------------------------------------------------------------------------

        /// <summary>
        /// Looks at the file at <paramref name="filePath"/>: a file that starts with the AMPB magic is opened with the map's key (resolved on first use)
        /// into a decrypting stream; any other file is a plaintext bundle. A bundle that cannot be used is logged here (E9 no key, E7 not a valid map
        /// bundle, or unreadable) and comes back Refused; this never throws. <paramref name="servedKey"/> is the key EFT asked for (null for the NavMesh
        /// bundle, <paramref name="navMesh"/> true), which only selects the wording of the lines.
        /// </summary>
        internal PreparedBundle Prepare(string filePath, string mapKey, string servedKey, bool navMesh)
        {
            FileStream file = null;
            try
            {
                file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                if (!Ampb.IsAmpb(file))
                {
                    file.Dispose();
                    return new PreparedBundle(PreparedKind.Plaintext, filePath, mapKey, servedKey, navMesh, null);
                }
            }
            catch (Exception e)
            {
                file?.Dispose();
                string reason = e is IOException || e is UnauthorizedAccessException ? e.Message : e.GetType().Name + ": " + e.Message;
                NoteBundleProblem(mapKey, navMesh, e is FileNotFoundException || e is DirectoryNotFoundException
                    ? MapLoadFailureKind.BundleMissing : MapLoadFailureKind.BundleUnreadable, filePath, reason);
                _env.LogError(RefusedLine(filePath, mapKey, servedKey, navMesh, "cannot be read", reason));
                return Refused(filePath, mapKey, servedKey, navMesh);
            }

            byte[] key;
            if (!TryGetKey(mapKey, out key))
            {
                file.Dispose();
                NoteNoKey(mapKey, navMesh, filePath);
                _env.LogError(NoKeyLine(mapKey, servedKey, navMesh));
                return Refused(filePath, mapKey, servedKey, navMesh);
            }

            try
            {
                ICryptoProvider provider = Provider();
                var stream = new AmpbReadStream(file, mapKey, key, provider, CacheSlots, false);
                return new PreparedBundle(PreparedKind.Encrypted, filePath, mapKey, servedKey, navMesh, stream);
            }
            catch (AmpbException e)
            {
                // the stream's constructor closed the file
                NoteBundleProblem(mapKey, navMesh, MapLoadFailureKind.BundleInvalid, filePath, e.Message);
                _env.LogError(RefusedLine(filePath, mapKey, servedKey, navMesh, "is not a valid map bundle", e.Message));
            }
            catch (Exception e)
            {
                file.Dispose();
                NoteBundleProblem(mapKey, navMesh, MapLoadFailureKind.BundleUnreadable, filePath, e.GetType().Name + ": " + e.Message);
                _env.LogError(RefusedLine(filePath, mapKey, servedKey, navMesh, "cannot be read", e.GetType().Name + ": " + e.Message));
            }
            return Refused(filePath, mapKey, servedKey, navMesh);
        }

        // ---- the failure records of Prepare ------------------------------------------------------------------

        // A bundle that is missing, not a valid container or unreadable. The NavMesh bundle is never a blocking record (the raid starts without bots'
        // NavMesh), so it is noted as a NavMesh cause instead of a bundle kind.
        void NoteBundleProblem(string mapKey, bool navMesh, MapLoadFailureKind kind, string filePath, string detail)
        {
            string file = FileNameOf(filePath);
            if (navMesh)
            {
                NavMeshCause cause = kind == MapLoadFailureKind.BundleMissing ? NavMeshCause.FileMissing : NavMeshCause.Damaged;
                NoteSafe(failures => failures.NoteNavMesh(mapKey, cause, detail, file));
            }
            else
            {
                NoteSafe(failures => failures.NoteBundle(mapKey, kind, file, detail));
            }
        }

        // The bundle was refused for want of a key: the cached derivation failure of the map (recorded again for every attempt, like the E9 line), or, when
        // the key was never derived because the stage-1 gate did not verify Arena, a gate refusal.
        void NoteNoKey(string mapKey, bool navMesh, string filePath)
        {
            KeyEntry entry;
            lock (_sync)
                _cache.TryGetValue(mapKey, out entry);
            string file = FileNameOf(filePath);
            if (entry == null)
            {
                string detail = "the Arena installation was not verified";
                if (navMesh)
                    NoteSafe(failures => failures.NoteNavMesh(mapKey, NavMeshCause.Unverified, detail, file));
                else
                    NoteSafe(failures => failures.NoteGateRefused(mapKey, detail, file));
                return;
            }
            if (navMesh)
                NoteSafe(failures => failures.NoteNavMesh(mapKey, NavMeshCause.NoKey, entry.Detail, entry.KeyFile));
            else
                NoteSafe(failures => failures.NoteKey(mapKey, entry.Cause, entry.Detail, entry.BuiltFor, entry.InstalledVersion, entry.FailedPath, entry.KeyFile));
        }

        static string FileNameOf(string filePath)
        {
            try
            {
                return Path.GetFileName(filePath ?? "");
            }
            catch
            {
                return null;
            }
        }

        static PreparedBundle Refused(string filePath, string mapKey, string servedKey, bool navMesh)
        {
            return new PreparedBundle(PreparedKind.Refused, filePath, mapKey, servedKey, navMesh, null);
        }

        // ---- the log lines ------------------------------------------------------------------------------------

        /// <summary>
        /// D1: <c>[BundleKeys] key for '&lt;map&gt;' derived in &lt;ms&gt; ms (&lt;n&gt; files, &lt;x&gt; MB sampled of &lt;y&gt; MB, built for Arena &lt;v&gt;, crypto: CNG)</c>:
        /// x is what was read and hashed, y the size of the files the key depends on.
        /// </summary>
        internal static string DerivedLine(string mapKey, KeyResolution resolution)
        {
            return KeyTag + " key for '" + mapKey + "' derived in " + resolution.Ms.ToString(CultureInfo.InvariantCulture) + " ms (" +
                   resolution.FilesHashed.ToString(CultureInfo.InvariantCulture) + " files, " +
                   (resolution.BytesHashed / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB sampled of " +
                   (resolution.BytesListed / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB, built for Arena " +
                   resolution.BuiltFor + ", crypto: " + resolution.Provider + ")";
        }

        /// <summary>The Error line E1-E6 for a failed resolution (never contains a hash or key material).</summary>
        internal static string FailureLine(string mapKey, KeyResolution resolution, string keyFilePath, string installPath)
        {
            return FailureLine(mapKey, resolution.Detail, resolution.Cause, keyFilePath, installPath, resolution);
        }

        static string FailureLine(string mapKey, string detail, KeyFailure cause, string keyFilePath, string installPath, KeyResolution resolution = null)
        {
            string head = KeyTag + " key for '" + mapKey + "' unavailable: ";
            switch (cause)
            {
                case KeyFailure.KeyFileMissing:
                    return head + "key file '" + keyFilePath + "' is missing; reinstall ArenaMapPort (the keys folder belongs to the release)";
                case KeyFailure.KeyFileInvalid:
                    return head + "key file '" + keyFilePath + "' is not valid (" + detail + "); reinstall ArenaMapPort";
                case KeyFailure.ArenaFileUnreadable:
                    return head + "Arena file '" + resolution?.FailedPath + "' cannot be read in " + installPath + " (" + detail + ")";
                case KeyFailure.SizeDiffers:
                    return head + SizeDiffersText(resolution);
                case KeyFailure.ContentDiffers:
                    return head + "the Arena files have the listed sizes but do not unlock the key (another build with equal sizes, or modified files)";
                default:
                    return head + "key derivation failed (" + detail + ")";
            }
        }

        static string SizeDiffersText(KeyResolution r)
        {
            string actual = r.ActualSize.ToString(CultureInfo.InvariantCulture);
            string expected = r.ExpectedSize.ToString(CultureInfo.InvariantCulture);
            bool sameVersion = !string.IsNullOrEmpty(r.InstalledVersion) && string.Equals(r.InstalledVersion, r.BuiltFor, StringComparison.Ordinal);
            if (sameVersion)
                return "Arena file '" + r.FailedPath + "' differs in size from the key file (" + actual + " against " + expected +
                       " bytes, Arena " + r.InstalledVersion + ")";
            string installed = string.IsNullOrEmpty(r.InstalledVersion) ? "another build" : r.InstalledVersion;
            return "the maps were built for Arena " + r.BuiltFor + " but " + installed + " is installed ('" + r.FailedPath + "' is " + actual +
                   " bytes there, " + expected + " expected); a keys update for this Arena build is needed";
        }

        /// <summary>E9: the bundle is refused because the map has no key (the line that says why comes first, from the first request of that map).</summary>
        internal static string NoKeyLine(string mapKey, string servedKey, bool navMesh)
        {
            if (navMesh)
                return NavMeshTag + " navmesh bundle for '" + mapKey + "' not loaded: no key (see the line above)";
            return BundlesTag + " '" + servedKey + "' not served: no key for '" + mapKey + "' (see the line above)";
        }

        /// <summary>E7 and its unreadable twin: <c>'&lt;file&gt;' &lt;problem&gt; (&lt;reason&gt;)</c> in the wording of the served bundle or the NavMesh bundle.</summary>
        internal static string RefusedLine(string filePath, string mapKey, string servedKey, bool navMesh, string problem, string reason)
        {
            string file = Path.GetFileName(filePath ?? "");
            if (navMesh)
                return NavMeshTag + " navmesh bundle for '" + mapKey + "' not loaded: '" + file + "' " + problem + " (" + reason + ")";
            return BundlesTag + " '" + servedKey + "' not served: '" + file + "' " + problem + " (" + reason + ")";
        }

        /// <summary>
        /// D2: <c>loaded '&lt;served key&gt;' from '&lt;path&gt;' (&lt;bytes&gt; bytes plaintext, encrypted AMPB v1, &lt;n&gt; chunks)</c>; a plaintext bundle (development builds) reads
        /// <c>(&lt;bytes&gt; bytes, plaintext bundle, not encrypted)</c> with the file's own size. The NavMesh bundle says "navmesh bundle for '&lt;map&gt;'" instead of the key.
        /// </summary>
        internal static string LoadedLine(PreparedBundle bundle, long fileBytes)
        {
            string subject = bundle.NavMesh
                ? NavMeshTag + " loaded navmesh bundle for '" + bundle.MapKey + "'"
                : BundlesTag + " loaded '" + bundle.ServedKey + "'";
            string detail = bundle.Kind == PreparedKind.Encrypted
                ? bundle.Stream.Length.ToString(CultureInfo.InvariantCulture) + " bytes plaintext, encrypted AMPB v" + bundle.Stream.Header.Version.ToString(CultureInfo.InvariantCulture) +
                  ", " + bundle.Stream.Header.ChunkCount.ToString(CultureInfo.InvariantCulture) + " chunks"
                : fileBytes.ToString(CultureInfo.InvariantCulture) + " bytes, plaintext bundle, not encrypted";
            return subject + " from '" + bundle.FilePath + "' (" + detail + ")";
        }
    }
}
