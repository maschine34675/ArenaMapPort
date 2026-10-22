#nullable disable
using System;
using System.Collections.Generic;
using ArenaMapPort.Crypto;

namespace ArenaMapPort.Client
{
    /// <summary>What kind of problem a <see cref="MapLoadFailure"/> records. Blocking is decided by the attempt window and this kind, never by a flag at the call site.</summary>
    public enum MapLoadFailureKind
    {
        /// <summary>A bundle request was refused because the Arena installation check did not verify an installation (not finished in time, failed, not verified).</summary>
        GateRefused,
        /// <summary>The content key of the map could not be recovered (stage 2); <see cref="MapLoadFailure.KeyCause"/> says why.</summary>
        Key,
        /// <summary>The bundle file does not exist.</summary>
        BundleMissing,
        /// <summary>The file is not a valid map bundle (container damaged, wrong map).</summary>
        BundleInvalid,
        /// <summary>The file cannot be read (locked, access denied, I/O error).</summary>
        BundleUnreadable,
        /// <summary>Unity or EFT failed to load the bundle although the file opened.</summary>
        BundleLoadFailed,
        /// <summary>A chunk of a live bundle stream failed its integrity check. Blocking inside a failed attempt, a late notice outside one.</summary>
        Integrity,
        /// <summary>The NavMesh bundle could not be used. Never blocking: the raid still starts, bots just do not move (<see cref="NavMeshCause"/> feeds the in-raid toast).</summary>
        NavMesh,
    }

    /// <summary>Why a raid started without NavMesh, in the words of the in-raid toast.</summary>
    public enum NavMeshCause
    {
        /// <summary>Arena was not verified (the gate refused the bundle).</summary>
        Unverified,
        /// <summary>The navmesh bundle file is missing.</summary>
        FileMissing,
        /// <summary>The file is damaged or holds no NavMeshData.</summary>
        Damaged,
        /// <summary>The map's content key could not be recovered.</summary>
        NoKey,
    }

    /// <summary>One recorded problem. Immutable once <see cref="MapLoadFailures.Note"/> stored it (the registry hands out copies).</summary>
    internal sealed class MapLoadFailure
    {
        /// <summary>Position in the registry's global order (1, 2, ...); 0 on a record that was not stored.</summary>
        public long Sequence { get; internal set; }
        public string MapKey { get; set; }
        public MapLoadFailureKind Kind { get; set; }
        /// <summary>For <see cref="MapLoadFailureKind.Key"/>: the resolver's cause. <see cref="KeyFailure.None"/> otherwise.</summary>
        public KeyFailure KeyCause { get; set; }
        /// <summary>For <see cref="MapLoadFailureKind.NavMesh"/>: why the NavMesh is missing.</summary>
        public NavMeshCause NavMeshCause { get; set; }
        /// <summary>A short one-line reason for the log (never a hash or key material).</summary>
        public string Detail { get; set; }
        /// <summary>The file name the notice may show: the bundle, or the key file for a key failure.</summary>
        public string File { get; set; }
        /// <summary>Key failures: the Arena version the key file was built for, and the one installed.</summary>
        public string BuiltFor { get; set; }
        public string InstalledVersion { get; set; }
        /// <summary>Key failures: the Arena file the failure names.</summary>
        public string FailedPath { get; set; }

        internal MapLoadFailure Copy() => (MapLoadFailure)MemberwiseClone();
    }

    /// <summary>
    /// Per-map record of what went wrong while a map was being loaded, kept for the in-game map notices (MapNotices, docs/progress/map-notices.md).
    ///
    /// What vanilla does. Nothing: a bundle that cannot be served or loaded is an Error line in the BepInEx log and, in EFT, a generic "matching
    /// aborted" popup; the player never learns the cause. <see cref="BundleKeyResolver"/> even forgets the cause of a failed key derivation after
    /// it logged it once (it keeps only "ok or not").
    ///
    /// What the port lacks. A machine-readable record of the failure that the main menu can turn into a sentence with the fix.
    ///
    /// What this class does. A thread-safe, bounded list of <see cref="MapLoadFailure"/>s with a global sequence number, written from wherever the
    /// load failed (the gate refusal and the bundle branches of <c>BundleHost</c>, the refusals of <see cref="BundleKeyResolver.Prepare"/>, the stream
    /// registry's integrity report, the NavMesh installer) and read by <see cref="MapNoticeScheduler"/>. An attempt (EFT asked for the map's raid) is
    /// the window <c>(startSeq, endSeq]</c> of sequence numbers, so what belongs to a failed load is decided by the window, the map and the kind:
    /// <see cref="TakeBlocking"/> returns and consumes the records of that window and map that are not NavMesh; NavMesh records are never blocking
    /// (the raid starts without bots' NavMesh) and <see cref="LastNavMeshCause"/> keeps the newest per map without consuming it;
    /// <see cref="TakeIntegrity"/> returns the integrity records no failed attempt claimed (a damaged chunk found in a raid that ran).
    ///
    /// Constraints. Never throws and logs nothing itself (the caller owns the log lines, which stay unchanged; the plugin hooks
    /// <see cref="Noted"/> to write the one Debug line "load failure noted", and an exception from that callback is swallowed). The unconsumed list is capped
    /// (<see cref="MaxRecords"/>, oldest first) so a long session of failing raids cannot grow it. No Unity, BepInEx or EFT reference.
    /// Host evidence: docs/progress/map-notices.md.
    /// </summary>
    internal sealed class MapLoadFailures
    {
        /// <summary>The most records kept at once; the oldest go first.</summary>
        internal const int MaxRecords = 256;

        readonly object _sync = new object();
        readonly List<MapLoadFailure> _records = new List<MapLoadFailure>();
        readonly Dictionary<string, MapLoadFailure> _lastNavMesh = new Dictionary<string, MapLoadFailure>(StringComparer.Ordinal);
        long _sequence;

        /// <summary>
        /// Called with a copy of every stored record, on the thread that noted it (the game thread), after the registry's lock was released. The
        /// plugin uses it to write the Debug line of the record; whatever it throws is swallowed.
        /// </summary>
        internal Action<MapLoadFailure> Noted { get; set; }

        /// <summary>The sequence number of the newest record (0 before any). An attempt opens at this value and closes at its value later.</summary>
        internal long CurrentSequence
        {
            get { lock (_sync) return _sequence; }
        }

        /// <summary>How many records are waiting to be consumed (tests, diagnostics).</summary>
        internal int Count
        {
            get { lock (_sync) return _records.Count; }
        }

        /// <summary>
        /// Stores a copy of <paramref name="failure"/> and returns its sequence number; 0 when nothing was stored (null, no map key, or an internal
        /// error). A NavMesh record also becomes the map's <see cref="LastNavMeshCause"/>. Never throws.
        /// </summary>
        internal long Note(MapLoadFailure failure)
        {
            MapLoadFailure stored;
            try
            {
                if (failure == null || string.IsNullOrEmpty(failure.MapKey))
                    return 0;
                lock (_sync)
                {
                    MapLoadFailure copy = failure.Copy();
                    copy.Sequence = ++_sequence;
                    if (copy.Kind == MapLoadFailureKind.NavMesh)
                        _lastNavMesh[copy.MapKey] = copy;
                    _records.Add(copy);
                    if (_records.Count > MaxRecords)
                        _records.RemoveRange(0, _records.Count - MaxRecords);
                    stored = copy.Copy();
                }
            }
            catch
            {
                return 0;
            }

            try
            {
                Noted?.Invoke(stored);
            }
            catch
            {
                // the callback is a log line; it never fails a load
            }
            return stored.Sequence;
        }

        /// <summary>A bundle request of <paramref name="mapKey"/> was refused because Arena is not verified (or the check did not finish).</summary>
        internal long NoteGateRefused(string mapKey, string detail, string file = null) =>
            Note(new MapLoadFailure { MapKey = mapKey, Kind = MapLoadFailureKind.GateRefused, Detail = detail, File = file });

        /// <summary>The content key of <paramref name="mapKey"/> could not be recovered.</summary>
        internal long NoteKey(string mapKey, KeyFailure cause, string detail, string builtFor, string installedVersion, string failedPath, string keyFile) =>
            Note(new MapLoadFailure
            {
                MapKey = mapKey, Kind = MapLoadFailureKind.Key, KeyCause = cause, Detail = detail, BuiltFor = builtFor,
                InstalledVersion = installedVersion, FailedPath = failedPath, File = keyFile,
            });

        /// <summary>A bundle of <paramref name="mapKey"/> is missing, invalid, unreadable or failed to load (<paramref name="kind"/> says which of the four).</summary>
        internal long NoteBundle(string mapKey, MapLoadFailureKind kind, string file, string detail) =>
            Note(new MapLoadFailure { MapKey = mapKey, Kind = kind, File = file, Detail = detail });

        /// <summary>A chunk of a live bundle stream of <paramref name="mapKey"/> failed its integrity check.</summary>
        internal long NoteIntegrity(string mapKey, string file, string detail) =>
            Note(new MapLoadFailure { MapKey = mapKey, Kind = MapLoadFailureKind.Integrity, File = file, Detail = detail });

        /// <summary>The NavMesh of <paramref name="mapKey"/> could not be loaded.</summary>
        internal long NoteNavMesh(string mapKey, NavMeshCause cause, string detail, string file = null) =>
            Note(new MapLoadFailure { MapKey = mapKey, Kind = MapLoadFailureKind.NavMesh, NavMeshCause = cause, Detail = detail, File = file });

        /// <summary>
        /// Returns and consumes the records that belong to the failed attempt of <paramref name="mapKey"/>: same map, sequence in
        /// <c>(startSequence, endSequence]</c>, kind not NavMesh, oldest first. Records of other maps, outside the window or of kind NavMesh stay.
        /// Never throws.
        /// </summary>
        internal IReadOnlyList<MapLoadFailure> TakeBlocking(string mapKey, long startSequence, long endSequence)
        {
            var taken = new List<MapLoadFailure>();
            try
            {
                lock (_sync)
                    for (int i = 0; i < _records.Count;)
                    {
                        MapLoadFailure record = _records[i];
                        if (string.Equals(record.MapKey, mapKey, StringComparison.Ordinal)
                            && record.Kind != MapLoadFailureKind.NavMesh
                            && record.Sequence > startSequence && record.Sequence <= endSequence)
                        {
                            taken.Add(record.Copy());
                            _records.RemoveAt(i);
                        }
                        else
                        {
                            i++;
                        }
                    }
            }
            catch
            {
                // a registry that cannot be read says "nothing recorded"
            }
            return taken;
        }

        /// <summary>True when an unconsumed <see cref="MapLoadFailureKind.Integrity"/> record exists.</summary>
        internal bool HasIntegrity
        {
            get
            {
                try
                {
                    lock (_sync)
                        foreach (MapLoadFailure record in _records)
                            if (record.Kind == MapLoadFailureKind.Integrity)
                                return true;
                }
                catch
                {
                    // see TakeBlocking
                }
                return false;
            }
        }

        /// <summary>Returns and consumes every <see cref="MapLoadFailureKind.Integrity"/> record, oldest first: the damage no failed attempt claimed. Never throws.</summary>
        internal IReadOnlyList<MapLoadFailure> TakeIntegrity()
        {
            var taken = new List<MapLoadFailure>();
            try
            {
                lock (_sync)
                    for (int i = 0; i < _records.Count;)
                    {
                        if (_records[i].Kind == MapLoadFailureKind.Integrity)
                        {
                            taken.Add(_records[i].Copy());
                            _records.RemoveAt(i);
                        }
                        else
                        {
                            i++;
                        }
                    }
            }
            catch
            {
                // see TakeBlocking
            }
            return taken;
        }

        /// <summary>The newest NavMesh record of <paramref name="mapKey"/>, or null. Not consumed: every later raid on that map can use it. Never throws.</summary>
        internal MapLoadFailure LastNavMeshCause(string mapKey)
        {
            try
            {
                if (string.IsNullOrEmpty(mapKey))
                    return null;
                lock (_sync)
                    return _lastNavMesh.TryGetValue(mapKey, out MapLoadFailure record) ? record.Copy() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Forget everything, sequence included (tests only).</summary>
        internal void ResetForTests()
        {
            lock (_sync)
            {
                _records.Clear();
                _lastNavMesh.Clear();
                _sequence = 0;
            }
        }
    }
}
