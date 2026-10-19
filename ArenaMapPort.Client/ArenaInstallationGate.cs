#nullable disable
using System;
using System.Threading;
using ArenaMapPort.Shared;
using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Client side of the Arena installation gate (user decision 2026-10-19, release 0.2.3).
    ///
    /// What this is. Escape from Tarkov: Arena is a separate purchase from Escape from Tarkov, and this mod ships maps
    /// derived from Arena's files, so the maps load only on a machine that has Arena installed. The server registers
    /// the Arena locations only when <see cref="ArenaInstallation"/> verifies an installation, and the client serves
    /// its map bundles only when it verifies one too (<see cref="BundleHost.TryServe"/>). This is a good-faith
    /// installation gate at SPT's own level (SPT requires an installed EFT), not copy protection: the check covers
    /// file names and sizes, and there is deliberately no switch that turns it off. The one override is the install
    /// path <c>General/ArenaInstallPath</c> for an installation without the BSG launcher's registry entry; the path is
    /// verified against the launcher's file manifest exactly like the registry's.
    ///
    /// What vanilla does. Nothing here replaces vanilla behaviour. A vanilla raid never asks for one of our bundles,
    /// so <see cref="BundleHost.TryServe"/> returns before reading this class, and every repair stays gated on
    /// <c>ArenaMapRaid.IsLoaded()</c> as before.
    ///
    /// What the ported scenes lack. They lack nothing on a machine with Arena; on a machine without it the maps are
    /// unavailable by design. When the server did not register them (the normal case without Arena) EFT never requests
    /// our bundles. If a server that did register them meets a client that cannot verify Arena, the bundle request is
    /// refused here with an Error line and EFT reports the missing bundle: the failure is loud rather than silent.
    ///
    /// What this class does. <see cref="Initialize"/> binds the public config key and starts the one check on a
    /// worker thread (it never throws). The check does file-system I/O on a path that may be unreachable (an unrouted
    /// UNC address blocked it for 21 s when measured, a cold disk cache lengthens the usual 0.5 s), so no game thread
    /// waits for it without a bound: <see cref="Initialize"/> callers wait <see cref="StartupWait"/> for the startup
    /// log line, the first bundle request waits <see cref="BundleWait"/>, and a wait that times out fails closed (not
    /// verified, reason "did not finish"). After a bundle wait has timed out, later bundle requests do not block again
    /// (a check that finishes later is still honoured by them). The outcome is logged once, from whichever game-thread
    /// caller first sees it. A check that outlives <see cref="StartupWait"/> would otherwise be logged only when a bundle
    /// request comes (a session of vanilla raids never asks), so <see cref="Initialize"/> arms a one-time poll that
    /// <c>Plugin.LateUpdate</c> drives through <see cref="PollOutcome"/>: while armed it reads one flag per frame, once
    /// the check has finished it logs the outcome and disarms itself (no scan, no timer, nothing after that).
    ///
    /// Host test: GPU-2 once Arena is installed there (docs/progress/arena-install-check.md).
    /// </summary>
    internal static class ArenaInstallationGate
    {
        internal const string Section = "General";
        internal const string PathKey = "ArenaInstallPath";
        internal const string Tag = "[ArenaMapPort][Arena]";

        /// <summary>Text of the F12 entry and of the config file comment.</summary>
        internal const string PathDescription =
            "Path of the Escape from Tarkov: Arena installation when the BSG launcher's registry entry is missing. " +
            "Empty = automatic (registry). The path is verified against the launcher's file manifest; the maps load " +
            "only with a verified installation. Takes effect after a restart.";

        /// <summary>Second Info line when Arena was not verified.</summary>
        internal const string NotFoundHint =
            "the Arena maps need an installed Escape from Tarkov: Arena; the server does not register them without one. " +
            "Set General/ArenaInstallPath for an installation without the launcher's registry entry.";

        /// <summary>How long Plugin.Awake waits for the check before it carries on and lets the check finish in the background (settable for tests).</summary>
        internal static TimeSpan StartupWait = TimeSpan.FromSeconds(3);

        /// <summary>How long the first bundle request waits for a check that is still running (covers the measured 21 s of an unrouted path).</summary>
        internal static TimeSpan BundleWait = TimeSpan.FromSeconds(30);

        static readonly object Sync = new object();
        static ManualResetEventSlim _done = new ManualResetEventSlim(false);
        static int _generation;
        static bool _started;
        static bool _timedOut; // a bundle wait ran out of time: later bundle waits do not block again
        static bool _outcomeLogged;
        static volatile bool _outcomePending; // armed when the startup wait ran out: PollOutcome logs the outcome once
        static ArenaInstallationResult _result;
        static string _failure;

        /// <summary>
        /// The verification result, or null while the check still runs, was never started, or threw (then
        /// <see cref="Reason"/> says why). Does not block.
        /// </summary>
        internal static ArenaInstallationResult Result
        {
            get { lock (Sync) return _started && _done.IsSet ? _result : null; }
        }

        /// <summary>True only when the check has finished and verified a real Arena installation. Does not block.</summary>
        internal static bool Verified
        {
            get
            {
                lock (Sync) return _started && _done.IsSet && _result != null && _result.Verified;
            }
        }

        /// <summary>Why Arena is not verified (one sentence); null when it is. Does not block.</summary>
        internal static string Reason
        {
            get
            {
                lock (Sync)
                {
                    if (!_started)
                        return "the Arena installation check did not run";
                    if (!_done.IsSet)
                        return _timedOut
                            ? "the Arena installation check did not finish in time, slow disk or unreachable path"
                            : "the Arena installation check is still running";
                    if (_result != null)
                        return _result.Verified ? null : _result.Reason;
                    return "the Arena installation check failed (" + _failure + ")";
                }
            }
        }

        /// <summary>Bind the public config key. The result is the entry, whose value is the path ("" = automatic).</summary>
        internal static ConfigEntry<string> BindConfig(ConfigFile config) =>
            config.Bind(Section, PathKey, "", PathDescription);

        /// <summary>
        /// Plugin.Awake entry: bind the key on the public file, start the check and wait <see cref="StartupWait"/> for the
        /// startup lines. Never throws; any failure leaves Arena not verified.
        /// </summary>
        internal static void Initialize(ConfigFile config) => Initialize(config, null);

        /// <summary><see cref="Initialize(ConfigFile)"/> with an injectable detector (tests; null = the real check).</summary>
        internal static void Initialize(ConfigFile config, Func<string, ArenaInstallationResult> detect)
        {
            string path = "";
            try
            {
                path = BindConfig(config).Value ?? "";
            }
            catch (Exception e)
            {
                // An unreadable key must not read as "use the registry": the player asked for something we cannot see.
                Fail("the General/ArenaInstallPath setting could not be read (" + e.GetType().Name + ": " + e.Message + ")");
                LogOutcome();
                return;
            }
            Start(path, detect);
            if (WaitForResult(StartupWait))
            {
                LogOutcome();
                return;
            }
            _outcomePending = true; // armed before the line below: a check that finishes in between is caught by the next poll
            Plugin.Log.LogInfo(Tag + " still checking the Arena installation in the background (slow disk or unreachable path); " +
                               "the maps load once it is verified.");
        }

        /// <summary>
        /// Called every frame from <c>Plugin.LateUpdate</c>. Unarmed (the normal case, and after the first log) it is a
        /// single flag read. Armed by a startup wait that ran out, it logs the outcome once the check has finished, from
        /// the game thread, and disarms. Never blocks and never throws.
        /// </summary>
        internal static void PollOutcome()
        {
            if (!_outcomePending)
                return;
            try
            {
                if (!WaitForResult(TimeSpan.Zero))
                    return; // still running (or never started): try again next frame
                _outcomePending = false;
                LogOutcome(); // no-op when a bundle request logged it first
            }
            catch
            {
                _outcomePending = false; // a poll that cannot work must not run every frame for the rest of the session
            }
        }

        /// <summary>True while the late-outcome poll is armed (tests).</summary>
        internal static bool OutcomePending => _outcomePending;

        /// <summary>
        /// Start the one check. <paramref name="detect"/> is the injectable form of <see cref="ArenaInstallation.Detect(string)"/>
        /// (tests). Restarts cleanly when called again: a stale worker's result is ignored.
        /// </summary>
        internal static void Start(string overridePath, Func<string, ArenaInstallationResult> detect)
        {
            int generation;
            ManualResetEventSlim done;
            lock (Sync)
            {
                generation = ++_generation;
                _done = done = new ManualResetEventSlim(false);
                _started = true;
                _timedOut = false;
                _outcomeLogged = false;
                _outcomePending = false;
                _result = null;
                _failure = null;
            }

            Func<string, ArenaInstallationResult> run = detect ?? (path => ArenaInstallation.Detect(path));
            string argument = overridePath ?? "";
            try
            {
                var worker = new Thread(() => Run(generation, done, argument, run))
                {
                    IsBackground = true, // a check stuck on an unreachable path must never hold the game's exit
                    Name = "ArenaMapPort-ArenaCheck"
                };
                worker.Start();
            }
            catch (Exception e)
            {
                Complete(generation, done, null, "the check could not be started (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        static void Run(int generation, ManualResetEventSlim done, string overridePath, Func<string, ArenaInstallationResult> detect)
        {
            ArenaInstallationResult result = null;
            string failure = null;
            try
            {
                result = detect(overridePath);
                if (result == null)
                    failure = "the check returned no result";
            }
            catch (Exception e)
            {
                failure = e.GetType().Name + ": " + e.Message;
            }
            Complete(generation, done, result, failure);
        }

        static void Complete(int generation, ManualResetEventSlim done, ArenaInstallationResult result, string failure)
        {
            lock (Sync)
            {
                if (generation != _generation)
                    return; // a newer Start() owns the state
                _result = result;
                _failure = failure;
            }
            done.Set();
        }

        static void Fail(string failure)
        {
            lock (Sync)
            {
                _generation++;
                _done = new ManualResetEventSlim(true);
                _started = true;
                _timedOut = false;
                _outcomeLogged = false;
                _outcomePending = false;
                _result = null;
                _failure = failure;
            }
        }

        /// <summary>
        /// Wait up to <paramref name="timeout"/> for the check. True when it has finished (verified or not); false when it was
        /// never started or is still running. Latches nothing (see <see cref="EnsureVerified"/>).
        /// </summary>
        internal static bool WaitForResult(TimeSpan timeout)
        {
            ManualResetEventSlim done;
            lock (Sync)
            {
                if (!_started)
                    return false;
                done = _done;
            }
            return done.IsSet || done.Wait(timeout);
        }

        /// <summary>
        /// For a bundle request: wait (bounded by <see cref="BundleWait"/>) for the check, log the outcome once, and say
        /// whether Arena is verified. A wait that runs out of time fails closed and latches, so the next bundle request does
        /// not block again. Never throws and never returns true unless the check finished and verified Arena.
        /// </summary>
        internal static bool EnsureVerified()
        {
            ManualResetEventSlim done;
            bool alreadyTimedOut;
            lock (Sync)
            {
                done = _started ? _done : null;
                alreadyTimedOut = _timedOut;
            }
            if (done != null && !done.IsSet && !alreadyTimedOut && !done.Wait(BundleWait))
            {
                lock (Sync)
                    if (_done == done)
                        _timedOut = true;
            }
            LogOutcome(); // no-op unless the check has finished and was not logged yet
            return Verified;
        }

        /// <summary>The Info line for a finished check: "[ArenaMapPort][Arena] " + <see cref="ArenaInstallationResult.Describe"/>; null when the check itself threw.</summary>
        internal static string OutcomeLine(ArenaInstallationResult result) =>
            result == null ? null : Tag + " " + result.Describe();

        /// <summary>The Info line that tells a player what to do when Arena is not verified.</summary>
        internal static string HintLine() => Tag + " " + NotFoundHint;

        /// <summary>The Error line for a refused bundle request.</summary>
        internal static string NotServedMessage(string bundleName) =>
            "[ArenaMapPort][Bundles] '" + bundleName + "' not served: Escape from Tarkov: Arena not found (" + Reason + ")";

        /// <summary>"Arena: found 0.6.1.0.48206", "Arena: not found" or "Arena: still being checked" for the startup summary.</summary>
        internal static string StateSummary()
        {
            lock (Sync)
            {
                if (!_started)
                    return "Arena: not found";
                if (!_done.IsSet)
                    return "Arena: still being checked";
                if (_result != null && _result.Verified)
                    return string.IsNullOrEmpty(_result.ManifestVersion) ? "Arena: found" : "Arena: found " + _result.ManifestVersion;
                return "Arena: not found";
            }
        }

        /// <summary>
        /// The map part of the startup banner. With a verified Arena: "Maps: A, B. Arena: found 0.6.1.0.48206."; without one the
        /// maps are not offered (the server does not register them), so the line says so instead of listing them next to a
        /// "not found": "Maps: none available. Arena: not found."; while the check still runs: "Maps: available once Arena is
        /// verified (A, B). Arena: still being checked.".
        /// </summary>
        internal static string MapsSummary(System.Collections.Generic.IEnumerable<string> mapNames)
        {
            string names = string.Join(", ", mapNames ?? new string[0]);
            string state = StateSummary();
            if (state.StartsWith("Arena: found", StringComparison.Ordinal))
                return "Maps: " + names + ". " + state + ".";
            if (state == "Arena: still being checked")
                return "Maps: available once Arena is verified (" + names + "). " + state + ".";
            return "Maps: none available. " + state + ".";
        }

        /// <summary>
        /// Log the finished check once: the Info line (an Error line instead when the check itself threw), the hint when Arena
        /// is not verified, and the first mismatches at Debug. A no-op until the check has finished; safe to call repeatedly.
        /// </summary>
        internal static void LogOutcome()
        {
            ArenaInstallationResult result;
            string failure;
            lock (Sync)
            {
                if (!_started || !_done.IsSet || _outcomeLogged)
                    return;
                _outcomeLogged = true;
                result = _result;
                failure = _failure;
            }
            bool verified = result != null && result.Verified;
            try
            {
                if (result == null)
                    Plugin.Log.LogError(Tag + " checking the Arena installation failed unexpectedly (" + failure + "); " +
                                        "the Arena maps are not served this session.");
                else
                    Plugin.Log.LogInfo(OutcomeLine(result));
                if (!verified)
                    Plugin.Log.LogInfo(HintLine());
                if (result != null && !verified)
                    foreach (string mismatch in result.Mismatches)
                        Plugin.Log.LogDebug(Tag + " mismatch: " + mismatch);
            }
            catch
            {
                // Logging must never take the plugin or a raid load down.
            }
        }

        /// <summary>Forget everything (tests only).</summary>
        internal static void ResetForTests()
        {
            lock (Sync)
            {
                _generation++;
                _done = new ManualResetEventSlim(false);
                _started = false;
                _timedOut = false;
                _outcomeLogged = false;
                _outcomePending = false;
                _result = null;
                _failure = null;
            }
        }
    }
}
