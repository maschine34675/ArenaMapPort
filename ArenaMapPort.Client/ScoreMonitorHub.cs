#nullable disable
using System;
using System.Collections.Generic;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The state behind <see cref="ScoreMonitors"/>: who claimed the monitors of the current owned raid and what they are to show. Unity-free, thread-safe (one
    /// lock), last-write-wins and idempotent: a setter that changes nothing does not bump <see cref="Version"/>, so a mode plugin may call it every frame. The Unity
    /// side (ScoreMonitorView) only READS: it compares <see cref="Version"/> once per frame and copies the snapshot when it moved. Nothing here logs; claim changes
    /// and refusals are queued for the main thread (<see cref="DrainLog"/>).
    ///
    /// Handles are never reused within a process, so a handle that was released, taken over by the demo's successor or issued in an earlier raid can never match a later
    /// claim. A refused setter changes nothing.
    /// </summary>
    internal sealed class ScoreMonitorHub
    {
        readonly object _lock = new object();
        readonly Func<bool> _raidLoaded;      // an owned map's Scripts scene is loaded
        readonly Func<int> _usableOnMap;      // usable monitors the current map lists (0 = none / no owned raid)
        readonly Func<bool> _enabled;         // Monitors/Enabled
        readonly List<string> _log = new List<string>();
        ScoreMonitorSnapshot _s;
        int _nextClaim;
        int _builtCount;
        volatile int _version;
        volatile bool _hasLog;

        internal ScoreMonitorHub(Func<bool> raidLoaded, Func<int> usableOnMap, Func<bool> enabled)
        {
            _raidLoaded = raidLoaded;
            _usableOnMap = usableOnMap;
            _enabled = enabled;
        }

        /// <summary>Changes with every accepted change; one volatile read per frame is all an idle view costs.</summary>
        internal int Version => _version;

        /// <summary>True while <see cref="DrainLog"/> has lines; one volatile read per frame.</summary>
        internal bool HasLog => _hasLog;

        /// <summary>The feature is on, an owned raid is loaded and its map has monitors that can show something.</summary>
        internal bool IsAvailable => _enabled() && _raidLoaded() && _usableOnMap() > 0;

        internal bool IsClaimed { get { lock (_lock) return _s.Claim != 0; } }

        /// <summary>Monitors the view has built for the current claim (0 until its first frames after the claim).</summary>
        internal int BuiltCount { get { lock (_lock) return _builtCount; } set { lock (_lock) _builtCount = value; } }

        internal ScoreMonitorSnapshot Snapshot() { lock (_lock) return _s; }

        /// <summary>Claims the monitors; returns a positive handle, or 0 when refused (no owner name, feature off, no owned raid, a map without usable monitors, or another
        /// owner holds them). A claim by a plugin takes the monitors over from the developer demo (<paramref name="demo"/> false); a demo claim never takes them from a
        /// plugin. Claiming again with the same owner returns the SAME handle and keeps the content.</summary>
        internal int Claim(string owner, bool demo)
        {
            string name = ScoreMonitorModel.CleanOwner(owner);
            lock (_lock)
            {
                if (name == null) return Refuse("claim refused: no owner name");
                if (!_enabled()) return Refuse($"claim by '{name}' refused: Monitors/Enabled is off");
                if (!_raidLoaded()) return Refuse($"claim by '{name}' refused: no owned raid is loaded");
                if (_usableOnMap() <= 0) return Refuse($"claim by '{name}' refused: this map has no usable monitors");
                if (_s.Claim != 0)
                {
                    if (!demo && _s.Demo) { /* a plugin takes the monitors over from the demo, below */ }
                    else if (string.Equals(_s.Owner, name, StringComparison.Ordinal)) return _s.Claim;
                    else return Refuse($"claim by '{name}' refused: held by '{_s.Owner}'");
                }
                _s = Fresh(++_nextClaim, name, demo);
                _s.Version = ++_version;
                Note($"claimed by '{name}' (handle {_s.Claim}{(demo ? ", developer demo" : "")})");
                return _s.Claim;
            }
        }

        /// <summary>Gives the monitors back; they go dark. False (nothing changes) when the handle is not the current claim.</summary>
        internal bool Release(int claim)
        {
            lock (_lock)
            {
                if (!Current(claim)) return false;
                Note($"released by '{_s.Owner}' (handle {claim})");
                _s = default(ScoreMonitorSnapshot);
                _builtCount = 0;
                _s.Version = ++_version;
                return true;
            }
        }

        /// <summary>A new owned raid (the Scripts scene unloaded): the claim is void, the monitors die with their scenes. Handles stay unique.</summary>
        internal void ResetForNewRaid()
        {
            lock (_lock)
            {
                if (_s.Claim != 0) Note($"claim of '{_s.Owner}' ended with the raid (handle {_s.Claim})");
                _s = default(ScoreMonitorSnapshot);
                _builtCount = 0;
                _s.Version = ++_version;
            }
        }

        /// <summary>Colours (0xRRGGBB, or any negative / above-0xFFFFFF value for Arena's red / blue) and the toggles per side (clamped to 0..16).</summary>
        internal bool SetSides(int claim, int colorA, int colorB, int teamSize)
        {
            lock (_lock)
            {
                if (!Current(claim)) return false;
                ScoreMonitorSnapshot n = _s;
                n.ColorA = ScoreMonitorModel.NormalizeColor(colorA);
                n.ColorB = ScoreMonitorModel.NormalizeColor(colorB);
                n.TeamSize = ScoreMonitorModel.ClampTeamSize(teamSize);
                return Commit(n);
            }
        }

        /// <summary>Scores clamped to 0..9999, alive counts to -1 (unknown) or 0..16.</summary>
        internal bool SetScore(int claim, int scoreA, int scoreB, int aliveA, int aliveB)
        {
            lock (_lock)
            {
                if (!Current(claim)) return false;
                ScoreMonitorSnapshot n = _s;
                n.ScoreA = ScoreMonitorModel.ClampScore(scoreA);
                n.ScoreB = ScoreMonitorModel.ClampScore(scoreB);
                n.AliveA = ScoreMonitorModel.ClampAlive(aliveA);
                n.AliveB = ScoreMonitorModel.ClampAlive(aliveB);
                return Commit(n);
            }
        }

        /// <summary>False for an unknown mode (nothing changes); a non-finite <paramref name="seconds"/> counts as 0.</summary>
        internal bool SetClock(int claim, int mode, float seconds)
        {
            lock (_lock)
            {
                if (!Current(claim)) return false;
                if (!MonitorClockMode.IsValid(mode)) return false;
                ScoreMonitorSnapshot n = _s;
                n.ClockMode = mode;
                n.ClockSeconds = float.IsNaN(seconds) || float.IsInfinity(seconds) ? 0f : seconds;
                return Commit(n);
            }
        }

        /// <summary>The claim changes and refusals since the last call, for the main thread to log at Debug (the setters never log).</summary>
        internal string[] DrainLog()
        {
            lock (_lock)
            {
                if (_log.Count == 0) return null;
                string[] lines = _log.ToArray();
                _log.Clear();
                _hasLog = false;
                return lines;
            }
        }

        bool Current(int claim) => claim != 0 && claim == _s.Claim;

        void Note(string line)
        {
            if (_log.Count < 16) { _log.Add(line); _hasLog = true; }
        }

        int Refuse(string why)
        {
            Note(why);
            return 0;
        }

        static ScoreMonitorSnapshot Fresh(int claim, string owner, bool demo) => new ScoreMonitorSnapshot
        {
            Claim = claim, Owner = owner, Demo = demo, ColorA = -1, ColorB = -1, TeamSize = ScoreMonitorModel.DefaultTeamSize, AliveA = -1, AliveB = -1,
            ClockMode = MonitorClockMode.None,
        };

        // Bumps the version only when something differs, so an unchanged call from a per-frame caller costs a lock and a compare.
        bool Commit(ScoreMonitorSnapshot n)
        {
            n.Version = _s.Version;
            if (Same(_s, n)) return true;
            n.Version = ++_version;
            _s = n;
            return true;
        }

        static bool Same(in ScoreMonitorSnapshot a, in ScoreMonitorSnapshot b) =>
            a.ColorA == b.ColorA && a.ColorB == b.ColorB && a.TeamSize == b.TeamSize && a.ScoreA == b.ScoreA && a.ScoreB == b.ScoreB &&
            a.AliveA == b.AliveA && a.AliveB == b.AliveB && a.ClockMode == b.ClockMode && a.ClockSeconds.Equals(b.ClockSeconds);
    }
}
