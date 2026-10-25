#nullable disable

namespace ArenaMapPort.Client
{
    /// <summary>What a full scene scan of <see cref="PuddleCacheGate"/> means for the water cache.</summary>
    internal enum PuddleScanResult
    {
        /// <summary>Every listed water renderer carried the native water shader: the cache is complete.</summary>
        Ready,
        /// <summary>A listed water renderer still wears another shader (ShaderRebind has not reached it): scan again later.</summary>
        Retry,
        /// <summary>The retry bound is spent: keep what was cached, stop scanning until the next scene change.</summary>
        GaveUp,
    }

    /// <summary>
    /// The pure pacing rules of <see cref="PuddleFootstepRepairs"/>' water cache (no Unity types, linked into the server
    /// tests). Building the cache walks every renderer of the map's scenes (about 30 ms per 55,000 objects, Bowl 80,000), so
    /// it must never become a periodic scan: <see cref="TryBegin"/> spaces attempts a second apart,
    /// <see cref="AfterFullScan"/> allows at most <see cref="MaxFullScans"/> full scans per scene state while a listed water
    /// renderer still waits for its native shader (until 2026-10-25 that wait repeated once a second for as long as the player
    /// walked on concrete, the 1 Hz freeze trap of AGENTS.md), and <see cref="ReportScenesMissing"/> lets the caller say once
    /// that a scene of the map is not loaded (a cache that never builds is otherwise silent). A scene load or unload, or a
    /// config toggle, calls <see cref="Reset"/> and so grants a fresh set of attempts.
    /// </summary>
    internal sealed class PuddleCacheGate
    {
        /// <summary>Full scans allowed while a listed water renderer still lacks the native shader.</summary>
        internal const int MaxFullScans = 5;

        /// <summary>Seconds (unscaled) between two build attempts.</summary>
        internal const float AttemptInterval = 1f;

        float _next;
        int _fullScans;
        bool _missingReported;

        /// <summary>Full scans made since the last <see cref="Reset"/>.</summary>
        internal int FullScans => _fullScans;

        /// <summary>True when a build attempt may start at <paramref name="now"/>; arms the next one a second later.</summary>
        internal bool TryBegin(float now)
        {
            if (now < _next) return false;
            _next = now + AttemptInterval;
            return true;
        }

        /// <summary>True the first time it is called after a <see cref="Reset"/>: the caller logs "scene not loaded" once.</summary>
        internal bool ReportScenesMissing()
        {
            if (_missingReported) return false;
            _missingReported = true;
            return true;
        }

        /// <summary>Counts one finished full scan and says whether to rescan, accept or stop.</summary>
        internal PuddleScanResult AfterFullScan(bool waitingForRebind)
        {
            _fullScans++;
            if (!waitingForRebind) return PuddleScanResult.Ready;
            return _fullScans >= MaxFullScans ? PuddleScanResult.GaveUp : PuddleScanResult.Retry;
        }

        /// <summary>A new scene state or a toggled setting: forget attempts, the pacing and the one-time report.</summary>
        internal void Reset()
        {
            _next = 0f;
            _fullScans = 0;
            _missingReported = false;
        }
    }
}
