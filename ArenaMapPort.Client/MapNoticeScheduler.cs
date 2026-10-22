#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ArenaMapPort.Client
{
    /// <summary>How a modal window ended (the answer of <see cref="ModalTicket"/>).</summary>
    public enum ModalOutcome
    {
        /// <summary>Still on screen, or its callbacks have not fired.</summary>
        Pending,
        /// <summary>The player pressed OK.</summary>
        Accepted,
        /// <summary>The window went away without OK (declined, replaced by another window, closed silently).</summary>
        Closed,
        /// <summary>EFT showed nothing (it accepted the request on the spot, for example in a raid).</summary>
        Suppressed,
        /// <summary>Showing it threw.</summary>
        Failed,
    }

    /// <summary>
    /// The scheduler's handle on one modal window: the UI glue creates it when it shows the window and calls <see cref="Accept"/>, <see cref="Close"/>
    /// from the window's callbacks (game thread). The first terminal answer wins.
    /// </summary>
    internal sealed class ModalTicket
    {
        internal ModalTicket(double shownAt)
        {
            ShownAt = shownAt;
        }

        internal double ShownAt { get; }
        internal double ClosedAt { get; private set; }
        internal ModalOutcome Outcome { get; private set; }
        internal string Error { get; private set; }

        internal void Accept(double now) => Finish(ModalOutcome.Accepted, now, null);

        /// <summary>The window was declined or closed without OK (EFT's <c>OnDecline</c>, <c>OnClose</c>, <c>OnCloseSilent</c>).</summary>
        internal void Close(double now) => Finish(ModalOutcome.Closed, now, null);

        void Finish(ModalOutcome outcome, double now, string error)
        {
            if (Outcome != ModalOutcome.Pending)
                return;
            Outcome = outcome;
            ClosedAt = now;
            Error = error;
        }

        /// <summary>A ticket for a window EFT did not show.</summary>
        internal static ModalTicket SuppressedAt(double now)
        {
            var ticket = new ModalTicket(now);
            ticket.Finish(ModalOutcome.Suppressed, now, null);
            return ticket;
        }

        /// <summary>A ticket for a window that could not be shown.</summary>
        internal static ModalTicket FailedAt(double now, string error)
        {
            var ticket = new ModalTicket(now);
            ticket.Finish(ModalOutcome.Failed, now, error);
            return ticket;
        }
    }

    /// <summary>
    /// What <see cref="MapNoticeScheduler"/> needs from the game, so the whole state machine runs in tests without Unity or EFT. Every predicate is total:
    /// it returns false (or the neutral value) on any failure and logs nothing; an exception from one is never counted as a fault. The actions
    /// (<see cref="ShowModal"/>, <see cref="ShowToast"/>) never throw either, but the scheduler guards them anyway. Everything is called on the game thread.
    /// </summary>
    internal interface INoticeEnvironment
    {
        /// <summary>Unscaled seconds (Unity's <c>Time.unscaledTime</c>).</summary>
        double Now { get; }
        /// <summary>General/ShowMapNotices.</summary>
        bool NoticesEnabled { get; }
        /// <summary>EFT's UI language code, "" on any failure.</summary>
        string Language { get; }
        /// <summary>The plugin version, for the footer.</summary>
        string ClientVersion { get; }
        /// <summary>The registry maps (normally <see cref="MapInfo.FromRegistry"/>).</summary>
        IReadOnlyList<MapInfo> Maps { get; }
        /// <summary>The main menu is the current screen of EFT's screen manager.</summary>
        bool MenuIsCurrent { get; }
        /// <summary>The location selection screen is the current screen.</summary>
        bool SelectionIsCurrent { get; }
        /// <summary>A modal can be shown now: the UI context exists, no message window is open, no error screen is up, no raid is running.</summary>
        bool ModalPossible { get; }
        /// <summary>
        /// The name of the sub-check that made the latest <see cref="ModalPossible"/> read false ("ErrorScreen", "MessageWindow", ...); null or empty when it
        /// read true. It only makes the "waiting" log line say what the window is waiting for.
        /// </summary>
        string ModalBlocker { get; }
        /// <summary>An EFT message window (the ordinary or the scrolled one) is on screen. The watchdog of a modal whose window vanished without an answer.</summary>
        bool MessageWindowOpen { get; }
        /// <summary>The notification manager exists, so a toast would not be dropped.</summary>
        bool ToastPossible { get; }
        /// <summary>A raid of an Arena map has started (game status Started).</summary>
        bool InStartedRaid { get; }
        /// <summary>The key of the Arena map of the running raid; null when there is none.</summary>
        string CurrentRaidMapKey { get; }
        /// <summary>EFT's <c>MatchingOperation.MatchingCanceledByPlayer</c>.</summary>
        bool MatchingCanceledByPlayer { get; }
        /// <summary>The names of the map-critical hooks <c>HarmonyPatches.Verify</c> skipped.</summary>
        IReadOnlyList<string> SkippedHooks { get; }

        /// <summary>The client's Arena check right now. Never blocks.</summary>
        GateSnapshot ReadGate();
        /// <summary>The backend's location list; false while it is not available (no session yet).</summary>
        bool TryReadOffered(out IReadOnlyList<OfferedLocation> locations);
        /// <summary>The client file check for <paramref name="scope"/>.</summary>
        FileScan ScanFiles(IReadOnlyList<MapInfo> scope);

        /// <summary>The status probe's current result. Never blocks.</summary>
        ProbeView Probe { get; }
        /// <summary>Starts the probe once (a background request); the result shows in <see cref="Probe"/>.</summary>
        void StartProbe();

        /// <summary>Shows the modal window; the returned ticket receives the window's callbacks.</summary>
        ModalTicket ShowModal(string caption, string text, bool scrolled);
        /// <summary>Shows a toast; false when it was dropped.</summary>
        bool ShowToast(string text);

        void Debug(string message);
        void Info(string message);
        void Warning(string message);
    }

    /// <summary>
    /// The state machine of the in-game map notices (docs/progress/map-notices.md, "Tick state machine"): when a notice is shown, how it is delivered and
    /// when it counts as seen. The Unity side (MapNotices) feeds it hook events and calls <see cref="Tick"/> from <c>Plugin.LateUpdate</c>.
    ///
    /// What vanilla does. EFT shows its own message windows right after the main menu appears, throws a generic popup after a failed load, and has no way
    /// for a mod to say "the maps are missing because of X" at the moment the player looks for them.
    ///
    /// What the port lacks. A delivery that survives EFT's own windows, the hideout, a raid that ends in an error screen and a player who closes the window
    /// without reading it. A window that was replaced is not a window that was read.
    ///
    /// What this class does. One menu clock, <see cref="MenuSeconds"/>, advances only while the main menu is the current screen (two ticks in a row), so
    /// time in the hideout, a trader or a raid counts for nothing; every threshold is measured on it, anchored at the first accepted menu show
    /// (<see cref="OnMenuShown"/>; the in-raid Esc menu is a menu screen too and is ignored because it has no profile). T1 (the first main menu) waits for the
    /// settle time, reads the client's Arena check (a running check waits, with the pending toast after ten menu-seconds), reads the backend's location list
    /// (up to 30 menu-seconds), runs the client file check, builds the input, asks the server (once, only when this client is verified and a registry map is
    /// not offered, five seconds at most) and queues the one modal <see cref="MapNoticeSelector.SelectMenu"/> chooses. T2 shows the selection screen's reminder toast
    /// once. T3 answers a failed Arena raid attempt (<see cref="NoteAttemptStarted"/>, <see cref="NoteComeback"/>) with the modal for its recorded cause at the
    /// next main menu. N1 shows the toast of a raid that started without NavMesh, and row 22 the toast of a damaged chunk found in a raid that ran.
    ///
    /// Delivery of a modal: it is shown only when a window is possible, the menu is current and the settle time passed; it counts as seen when the player
    /// pressed OK or closed it after at least two seconds; a window that was replaced earlier, or that EFT suppressed, is displaced and is shown again; three
    /// displacements, or two minutes of menu time with the window busy, give a toast with the short text (the Warning says so only when the toast really was
    /// shown) and park the notice until the next menu show, which re-arms it with fresh counters. A window that vanished without any callback is closed by a
    /// watchdog after a second, so a ticket can never hold the delivery for the session. An answer to a failed attempt waits for a menu show newer than its
    /// comeback, or, when the menu postfix did not fire, for the menu screen becoming current again (the fallback the design names).
    ///
    /// Faults. Every entry point is guarded: the first fault per notice is one Warning, three faults in a row switch T1, the toasts and N1 off until the next
    /// menu show (T3 is never switched off, it follows the player's own retries). Each step of a tick (clock, attempt, raid toast, damage toast, T1, modals,
    /// reminder) also has its own fault counter: a step that threw three times in a row stops until the next menu show, and a stopped step's state no longer
    /// keeps the tick alive, so a bug in one of them is a bounded cost and never one exception per frame for the session. A text that cannot be shown
    /// costs nothing else.
    ///
    /// Not here: any Unity or EFT call (the environment does that), the Arena check itself (never <c>EnsureVerified</c>, which can block 30 s), any scene-wide
    /// search. Log lines carry the <c>[Notice]</c> tag. Host test: docs/progress/map-notices.md (V1 to V8).
    /// </summary>
    internal sealed class MapNoticeScheduler
    {
        internal const string Tag = "[Notice]";

        /// <summary>The menu must have been current for this long since the latest menu show, so EFT's own first-menu windows claim their window first.</summary>
        internal const double SettleSeconds = 2.0;
        /// <summary>How long T1 waits for the backend's location list, in menu-seconds.</summary>
        internal const double LocationsWait = 30.0;
        /// <summary>The Arena check still runs this many menu-seconds after the first menu: the pending toast.</summary>
        internal const double PendingToastAfter = 10.0;
        /// <summary>How long the scheduler waits for the status probe before it reads it as failed, in seconds.</summary>
        internal const double ProbeTimeout = 5.0;
        /// <summary>A window that closes without OK after at least this long on screen counts as seen.</summary>
        internal const double SeenAfter = 2.0;
        internal const int MaxModalAttempts = 3;
        /// <summary>Menu-seconds a modal may wait for a free window before the toast takes over.</summary>
        internal const double BlockedBudget = 120.0;
        /// <summary>Menu-seconds between two attempts to show a displaced modal.</summary>
        internal const double RetryDelay = 1.0;
        /// <summary>The most one tick may add to the menu clock (a hitch is not menu time).</summary>
        internal const double MaxClockStep = 5.0;
        /// <summary>The selection reminder is dropped this long after the screen opened if the screen never reported itself as current.</summary>
        internal const double SelectionGrace = 5.0;
        /// <summary>The menu-screen poll of the degraded mode (the menu postfix did not install).</summary>
        internal const double MenuPollInterval = 1.0;
        internal const int MaxConsecutiveFaults = 3;
        /// <summary>A modal whose window is gone (no EFT message window on screen) without any callback is treated as closed after this many seconds.</summary>
        internal const double WindowGoneGrace = 1.0;

        // The steps of one tick, each with its own fault counter (see RunStep): a step that keeps throwing stops running until the next menu show.
        const int StepClock = 0;
        const int StepAttempt = 1;
        const int StepRaid = 2;
        const int StepIntegrity = 3;
        const int StepT1 = 4;
        const int StepJobs = 5;
        const int StepSelection = 6;
        const int StepCount = 7;
        static readonly string[] StepNames = { "clock", "attempt", "raid toast", "damage toast", "T1", "modals", "selection reminder" };

        enum T1State { NotArmed, Waiting, Queued, Done }

        enum AttemptState { Open, Succeeded, Failed }

        sealed class Attempt
        {
            internal string Key;
            internal long StartSequence;
            internal AttemptState State;
        }

        sealed class ModalJob
        {
            internal bool IsAttempt;           // T3 (an answer to a failed raid attempt); otherwise T1
            internal MapNoticePlan Plan;
            internal int QueuedMenuCount;      // T3: needs a menu show newer than this
            internal int Displaced;
            internal double Blocked;           // menu-seconds spent with the window busy
            internal double NotBefore;         // menu clock, the retry delay
            internal bool EdgeReleased;        // T3: released by the menu screen becoming current again, no menu show seen (the fallback of the postfix)
            internal bool Parked;              // waiting for the next menu show
            internal bool Logged;              // the "showing" Info line was written
            internal ModalTicket Ticket;
        }

        readonly INoticeEnvironment _env;
        readonly MapLoadFailures _failures;

        // The per-frame delegates, built once: Tick runs every frame of a raid load and of a menu wait, and a lambda over `this` or a method group is a new
        // object per call.
        readonly Func<bool> _menuIsCurrent;
        readonly Func<bool> _selectionIsCurrent;
        readonly Func<bool> _modalPossible;
        readonly Func<bool> _toastPossible;
        readonly Func<bool> _inStartedRaid;
        readonly Func<bool> _noticesEnabled;
        readonly Func<bool> _messageWindowOpen;
        readonly Action _tickBody;
        readonly Action _idlePoll;
        readonly Action[] _steps;

        volatile bool _work;
        bool _menuPostfixInstalled = true;
        bool _disabled;
        int _consecutiveFaults;
        readonly int[] _stepFaults = new int[StepCount];
        readonly HashSet<string> _faultWarned = new HashSet<string>(StringComparer.Ordinal);
        string _lastWait;
        string _blockerSeen = "";
        string _blockedWait = BlockedWaitText;

        const string BlockedWaitText = "a free message window";

        // menu clock
        int _menuShowCount;
        bool _anchored;
        double _menuSeconds;
        double _settleBase;
        double _lastTickAt = double.NaN;
        bool _lastMenuCurrent;
        bool _tickMenu;
        double _tickNow;
        bool _menuRose;
        double _dtMenu;
        double _nextMenuPoll;
        bool _pollMenuLast;

        // T1
        T1State _t1 = T1State.NotArmed;
        bool _pendingShown;
        MapAvailabilityInput _t1Input;
        bool _probeStarted;
        double _probeStartedAt;

        // T2
        ReminderGroup _reminderGroup = ReminderGroup.None;
        bool _reminderShown;
        bool _selectionPending;
        bool _selectionSeenCurrent;
        double _selectionSince;

        // modal jobs
        readonly List<ModalJob> _jobs = new List<ModalJob>();
        ModalJob _open;
        double _windowGoneSince = double.NaN;

        // T3
        Attempt _attempt;

        // N1 and row 22
        string _n1Key;
        bool _n1SawRaid;
        bool _integrityCheck;
        readonly HashSet<string> _damagedShown = new HashSet<string>(StringComparer.Ordinal);

        internal MapNoticeScheduler(INoticeEnvironment env, MapLoadFailures failures)
        {
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _failures = failures ?? throw new ArgumentNullException(nameof(failures));
            _menuIsCurrent = () => _env.MenuIsCurrent;
            _selectionIsCurrent = () => _env.SelectionIsCurrent;
            _modalPossible = () => _env.ModalPossible;
            _toastPossible = () => _env.ToastPossible;
            _inStartedRaid = () => _env.InStartedRaid;
            _noticesEnabled = () => _env.NoticesEnabled;
            _messageWindowOpen = () => _env.MessageWindowOpen;
            _tickBody = TickBody;
            _idlePoll = IdleMenuPoll;
            _steps = new Action[StepCount];
            _steps[StepClock] = ClockStep;
            _steps[StepAttempt] = PollAttempt;
            _steps[StepRaid] = TickRaidToast;
            _steps[StepIntegrity] = TickIntegrity;
            _steps[StepT1] = TickT1;
            _steps[StepJobs] = TickJobs;
            _steps[StepSelection] = TickSelection;
        }

        // ---- state for the glue and the tests ----------------------------------------------------------------------------

        /// <summary>True while <see cref="Tick"/> has something to do; false is the one-flag-read idle state.</summary>
        internal bool Work => _work;

        /// <summary>The fault latch: T1, the toasts and N1 are off until the next menu show.</summary>
        internal bool Disabled => _disabled;

        /// <summary>Accepted menu shows (the in-raid Esc menu is not one).</summary>
        internal int MenuShowCount => _menuShowCount;

        /// <summary>Menu-seconds since the first accepted menu show.</summary>
        internal double MenuSeconds => _menuSeconds;

        /// <summary>T1 has finished: its modal was seen, or it had nothing to say, or it was silenced.</summary>
        internal bool T1Done => _t1 == T1State.Done;

        /// <summary>A modal is on screen (shown, not yet answered).</summary>
        internal bool ModalOpen => _open != null;

        /// <summary>Modal jobs that are queued, in flight or parked.</summary>
        internal int JobCount => _jobs.Count;

        /// <summary>The reminder group T1 found, or None.</summary>
        internal ReminderGroup ReminderGroupFound => _reminderGroup;

        /// <summary>
        /// False when the menu-screen postfix did not install: the idle tick then polls the menu screen once per second and treats a rising edge as a
        /// menu show. Defaults to true.
        /// </summary>
        internal bool MenuPostfixInstalled
        {
            get => _menuPostfixInstalled;
            set => _menuPostfixInstalled = value;
        }

        // ---- events ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>MenuScreen.Show</c> ran. <paramref name="hasProfile"/> is false for the in-raid Esc menu (a menu screen with no profile), which is ignored.
        /// Arms T1 on the first one, restarts the settle time, lifts the fault latch and re-arms parked modals with fresh counters.
        /// </summary>
        internal void OnMenuShown(bool hasProfile)
        {
            if (!hasProfile)
                return;
            Guard("menu", MenuShown);
        }

        void MenuShown()
        {
            _menuShowCount++;
            Wake();
            _settleBase = _menuSeconds;
            if (!_anchored)
            {
                _anchored = true;
                _t1 = T1State.Waiting;
            }
            _disabled = false;
            _consecutiveFaults = 0;
            Array.Clear(_stepFaults, 0, StepCount); // a step that was stopped for its errors gets another chance
            _integrityCheck = true;
            foreach (ModalJob job in _jobs)
                if (job.Parked)
                {
                    job.Parked = false;
                    job.Displaced = 0;
                    job.Blocked = 0;
                    job.NotBefore = 0;
                }
        }

        /// <summary>The location selection screen opened. The reminder toast (T2) is shown from <see cref="Tick"/> when T1 found "nothing available".</summary>
        internal void OnSelectionShown()
        {
            Guard("selection", () =>
            {
                Wake();
                _selectionPending = true;
                _selectionSeenCurrent = false;
                _selectionSince = SafeNow();
            });
        }

        /// <summary>EFT started to load the raid of <paramref name="mapKey"/> (an Arena map). Opens an attempt and supersedes an older one.</summary>
        internal void NoteAttemptStarted(string mapKey)
        {
            Guard("attempt", () =>
            {
                Wake();
                _n1Key = null; // a stale "raid without NavMesh" never belongs to the new raid
                _attempt = new Attempt { Key = mapKey, StartSequence = _failures.CurrentSequence, State = AttemptState.Open };
                Debug("attempt: map=" + mapKey + " started");
            });
        }

        /// <summary>
        /// <c>ComebackToMainMenu</c> ran. An open attempt (the raid never started) fails: its blocking records are taken now and the answer is queued for the
        /// next main menu. A finished attempt (the raid started and ended) is left alone.
        /// </summary>
        internal void NoteComeback()
        {
            Guard("comeback", () =>
            {
                // the raid is over or never started: a pending "raid without NavMesh" record can no longer fire (the scene-unload hook that normally clears
                // it is only registered once a NavMesh was installed)
                _n1Key = null;
                Attempt attempt = _attempt;
                if (attempt == null || attempt.State != AttemptState.Open)
                    return;
                attempt.State = AttemptState.Failed;
                _attempt = null;
                long end = _failures.CurrentSequence;
                IReadOnlyList<MapLoadFailure> records = _failures.TakeBlocking(attempt.Key, attempt.StartSequence, end);
                bool canceled = Pred(() => _env.MatchingCanceledByPlayer);
                Debug("attempt: map=" + attempt.Key + " failed records=" + records.Count.ToString(CultureInfo.InvariantCulture));

                var input = new AttemptFailureInput(attempt.Key, NameOf(attempt.Key), records, ReadGate(), canceled, false, SafeVersion());
                MapNoticePlan plan = MapNoticeSelector.SelectAttempt(input);
                if (plan == null)
                {
                    Debug("attempt: map=" + attempt.Key + " no notice (" + (canceled ? "cancelled by the player" : "nothing to say") + ")");
                    return;
                }
                Wake();
                _jobs.Add(new ModalJob { IsAttempt = true, Plan = plan, QueuedMenuCount = _menuShowCount });
            });
        }

        /// <summary>
        /// A damaged chunk was recorded (an Integrity record, row 22), possibly while the tick was idle during a raid: the damage check is armed again and
        /// keeps the tick alive until the main menu is back, so the toast does not depend on a menu show being counted.
        /// </summary>
        internal void NoteIntegrityRecorded()
        {
            Guard("integrity", () =>
            {
                _integrityCheck = true;
                Wake();
            });
        }

        /// <summary>A raid started on <paramref name="mapKey"/> although its NavMesh could not be loaded (N1): the toast follows once the raid has started.</summary>
        internal void NoteRaidWithoutNavMesh(string mapKey)
        {
            Guard("navmesh", () =>
            {
                if (string.IsNullOrEmpty(mapKey))
                    return;
                Wake();
                _n1Key = mapKey;
                _n1SawRaid = false;
            });
        }

        /// <summary>The raid ended or its scene unloaded: a pending N1 record must not fire for another raid.</summary>
        internal void NoteRaidEnded()
        {
            Guard("raid end", () => _n1Key = null);
        }

        // ---- the tick ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Called every frame from <c>Plugin.LateUpdate</c>. When nothing is pending it is one flag read (and, in the degraded mode, one poll per second).
        /// Never throws.
        /// </summary>
        internal void Tick()
        {
            if (!_work)
            {
                if (!_menuPostfixInstalled)
                    Guard("poll", _idlePoll);
                return;
            }
            Guard("tick", _tickBody);
        }

        void IdleMenuPoll()
        {
            double now = _env.Now;
            if (now < _nextMenuPoll)
                return;
            _nextMenuPoll = now + MenuPollInterval;
            bool current = Pred(_menuIsCurrent);
            if (current && !_pollMenuLast)
                MenuShown();
            _pollMenuLast = current;
        }

        // One tick = the clock, then every other step, each guarded on its own (RunStep). A step that threw MaxConsecutiveFaults times in a row stops
        // until the next menu show, so a bug in one of them costs one Warning and a few retries and then nothing, instead of one exception per frame
        // for the rest of the session; the others keep working (an open attempt must still be closed by PollAttempt).
        void TickBody()
        {
            if (!RunStep(StepClock))
            {
                // a clock that threw leaves this frame's readings stale: skip the frame; one that is stopped means nothing can progress until the next
                // menu show wakes the tick again and gives the clock another chance
                _work = !Stopped(StepClock);
                return;
            }
            for (int step = StepAttempt; step < StepCount; step++)
                RunStep(step);

            _work = ComputeWork();
        }

        // False when the step did not run to its end (stopped, or it threw).
        bool RunStep(int step)
        {
            if (Stopped(step))
                return false;
            try
            {
                _steps[step]();
                _stepFaults[step] = 0;
                return true;
            }
            catch (Exception e)
            {
                OnStepFault(step, e);
                return false;
            }
        }

        void OnStepFault(int step, Exception e)
        {
            try
            {
                _stepFaults[step]++;
                string name = StepNames[step];
                if (_faultWarned.Add("step:" + name))
                    Warning("the '" + name + "' step failed (" + e.GetType().Name + ": " + e.Message + "); the maps themselves are not affected");
                if (_stepFaults[step] >= MaxConsecutiveFaults)
                    Warning("the '" + name + "' step is stopped after " + MaxConsecutiveFaults.ToString(CultureInfo.InvariantCulture) +
                            " errors in a row, until the next main menu");
                CountFault(e);
            }
            catch
            {
                // the fault policy itself must not throw into EFT's frame
            }
        }

        bool Stopped(int step) => _stepFaults[step] >= MaxConsecutiveFaults;

        // True while some step still has something to do. A stopped step's state does not count: nothing would ever clear it.
        bool ComputeWork()
        {
            if (_t1 == T1State.Waiting && !Stopped(StepT1))
                return true;
            if ((_open != null || HasActiveJob()) && !Stopped(StepJobs))
                return true;
            if (_attempt != null && !Stopped(StepAttempt))
                return true;
            if (_selectionPending && !Stopped(StepSelection))
                return true;
            if (_n1Key != null && !Stopped(StepRaid))
                return true;
            // the damage check waits for the main menu: a raid that began before it ran does not keep the tick alive (the next menu show re-arms it),
            // unless a damage record is already waiting
            return _integrityCheck && !Stopped(StepIntegrity) && (_tickMenu || _failures.HasIntegrity);
        }

        void Wake()
        {
            if (!_work)
            {
                _lastTickAt = double.NaN; // the gap while idle is not menu time
                _lastMenuCurrent = false;
            }
            _work = true;
        }

        void ClockStep()
        {
            _tickNow = _env.Now;
            _tickMenu = Pred(_menuIsCurrent);
            AdvanceClocks(_tickNow, _tickMenu);

            if (!_menuPostfixInstalled)
            {
                if (_tickMenu && !_pollMenuLast)
                    MenuShown();
                _pollMenuLast = _tickMenu;
            }
            else if (_menuRose)
            {
                ReleaseAnswersWaitingForAShow();
            }
        }

        void AdvanceClocks(double now, bool menu)
        {
            double step = 0;
            if (!double.IsNaN(_lastTickAt) && _lastMenuCurrent && menu)
                step = Math.Min(Math.Max(now - _lastTickAt, 0), MaxClockStep);
            // a rising edge of "the main menu is current" that a previous tick saw as "not": the screen came back (the very first tick after a wake-up has no
            // previous observation and is no edge)
            _menuRose = menu && !_lastMenuCurrent && !double.IsNaN(_lastTickAt);
            _lastTickAt = now;
            _lastMenuCurrent = menu;
            _dtMenu = step;
            if (_anchored)
                _menuSeconds += step;
        }

        // The fallback for a MenuScreen.Show that did not fire after a failed load (design section 3.3: the screen manager's screen-changed signal): an answer that is
        // still waiting for a menu show newer than its comeback is released by the menu screen becoming current again. It still waits out the settle time
        // from here, so EFT's own windows claim theirs first.
        void ReleaseAnswersWaitingForAShow()
        {
            foreach (ModalJob job in _jobs)
            {
                if (!job.IsAttempt || job.Parked || job.EdgeReleased || _menuShowCount > job.QueuedMenuCount)
                    continue;
                job.EdgeReleased = true;
                job.NotBefore = Math.Max(job.NotBefore, _menuSeconds + SettleSeconds);
                Debug("'" + job.Plan.Id + "' released: the main menu is the current screen again and no menu show was seen");
            }
        }

        bool Settled => _menuSeconds - _settleBase >= SettleSeconds;

        bool HasActiveJob()
        {
            foreach (ModalJob job in _jobs)
                if (!job.Parked)
                    return true;
            return false;
        }

        // ---- attempts ------------------------------------------------------------------------------------------------------

        void PollAttempt()
        {
            Attempt attempt = _attempt;
            if (attempt == null || attempt.State != AttemptState.Open)
                return;
            if (!Pred(_inStartedRaid))
                return;
            attempt.State = AttemptState.Succeeded;
            _attempt = null;
            Debug("attempt: map=" + attempt.Key + " succeeded");
        }

        // ---- N1 --------------------------------------------------------------------------------------------------------------

        void TickRaidToast()
        {
            string key = _n1Key;
            if (key == null)
                return;
            if (!Pred(_inStartedRaid))
            {
                if (_n1SawRaid)
                    _n1Key = null; // the raid ended without the toast ever being possible
                return;
            }
            string current = null;
            try
            {
                current = _env.CurrentRaidMapKey;
            }
            catch
            {
                // a predicate that fails reads as "no map"
            }
            if (string.IsNullOrEmpty(current))
                return;
            if (!string.Equals(current, key, StringComparison.Ordinal))
            {
                _n1Key = null; // another map's raid: the record is stale
                return;
            }
            _n1SawRaid = true;
            if (_disabled || !Pred(_toastPossible))
                return;

            MapLoadFailure last = _failures.LastNavMeshCause(key);
            MapNoticePlan plan = MapNoticeSelector.SelectNavMesh(NameOf(key), last == null ? NavMeshCause.Damaged : last.NavMeshCause, SafeVersion());
            plan.LogSummary = DefaultSummary();
            _n1Key = null;
            ShowToast(plan);
        }

        // ---- row 22 ------------------------------------------------------------------------------------------------------------

        void TickIntegrity()
        {
            if (!_integrityCheck || !_tickMenu || !Settled || _attempt != null)
                return;
            if (!_failures.HasIntegrity)
            {
                _integrityCheck = false;
                return;
            }
            if (!Pred(_toastPossible))
                return;
            _integrityCheck = false;
            foreach (MapLoadFailure record in _failures.TakeIntegrity())
            {
                if (!_damagedShown.Add(record.MapKey))
                    continue; // once per map and session
                MapNoticePlan plan = MapNoticeSelector.SelectFileDamaged(NameOf(record.MapKey), record.File, SafeVersion());
                plan.LogSummary = DefaultSummary();
                ShowToast(plan);
            }
        }

        // ---- T1 ----------------------------------------------------------------------------------------------------------------

        void TickT1()
        {
            double now = _tickNow;
            if (_t1 != T1State.Waiting || _disabled || !_tickMenu)
                return;
            if (!Settled)
            {
                Wait("settle time");
                return;
            }

            if (_t1Input == null)
            {
                GateSnapshot gate = ReadGate();
                if (gate.State == GateState.Running)
                {
                    if (!_pendingShown && _menuSeconds >= PendingToastAfter)
                        ShowPendingToast();
                    Wait("the Arena check is still running");
                    return;
                }
                if (!BuildInput(gate))
                    return;
            }

            bool needsProbe = MapNoticeInputs.NeedsProbe(_t1Input);
            MapNoticePlan early = needsProbe ? null : MapNoticeSelector.SelectMenu(_t1Input);
            if (!needsProbe && early == null)
            {
                _t1 = T1State.Done; // nothing to say: the availability line is the only trace
                return;
            }
            if (!SafeEnabled())
            {
                Debug("'" + (early == null ? "T1" : early.Id.ToString()) + "' not shown: General/ShowMapNotices is off");
                _t1 = T1State.Done;
                return;
            }

            MapAvailabilityInput input = _t1Input;
            if (needsProbe)
            {
                ProbeView probe = SafeProbe();
                if (probe.State == ProbeState.NotAsked)
                {
                    if (!_probeStarted)
                    {
                        _probeStarted = true;
                        _probeStartedAt = now;
                        if (!Act("probe", () => _env.StartProbe()))
                            probe = new ProbeView(ProbeState.Unavailable);
                    }
                    if (probe.State == ProbeState.NotAsked)
                    {
                        Wait("the server status");
                        return;
                    }
                }
                if (probe.State == ProbeState.Pending)
                {
                    if (now - _probeStartedAt < ProbeTimeout)
                    {
                        Wait("the server status");
                        return;
                    }
                    probe = new ProbeView(ProbeState.Failed);
                }
                input = _t1Input.WithProbe(probe);
                Debug("server status: " + probe.LogCode);
            }

            MapNoticePlan plan = MapNoticeSelector.SelectMenu(input);
            if (plan == null)
            {
                _t1 = T1State.Done;
                return;
            }
            _reminderGroup = plan.Reminder;
            _jobs.Add(new ModalJob { IsAttempt = false, Plan = plan });
            _t1 = T1State.Queued;
            _lastWait = null;
        }

        // Reads the gate-dependent inputs once. False while the backend's location list is still being waited for.
        bool BuildInput(GateSnapshot gate)
        {
            IReadOnlyList<OfferedLocation> locations = null;
            bool known = false;
            if (gate.Verified)
            {
                try
                {
                    known = _env.TryReadOffered(out locations);
                }
                catch
                {
                    known = false;
                }
                if (!known)
                {
                    if (_menuSeconds < LocationsWait)
                    {
                        Wait("the location list");
                        return false;
                    }
                    Debug("the location list was not available after " + LocationsWait.ToString("0", CultureInfo.InvariantCulture) + " s; offered is unknown");
                    locations = null;
                }
            }

            IReadOnlyList<MapInfo> maps = SafeMaps();
            MapAvailabilityInput input = MapNoticeInputs.Build(gate, maps, known ? locations : null, null, ProbeView.NotAsked,
                SafeSkippedHooks(), SafeVersion(), true);
            if (gate.Verified)
            {
                FileScan scan = null;
                Act("file scan", () => scan = _env.ScanFiles(input.ScopeMaps));
                input = input.WithFiles(scan);
            }
            _t1Input = input;
            Debug(MapNoticeInputs.DescribeAvailability(input));
            return true;
        }

        void ShowPendingToast()
        {
            if (!SafeEnabled())
            {
                _pendingShown = true;
                Debug("'" + NoticeId.CheckPending + "' not shown: General/ShowMapNotices is off");
                return;
            }
            if (!Pred(_toastPossible))
                return; // try again on a later tick
            _pendingShown = true;
            MapNoticePlan plan = MapNoticeSelector.SelectPending(SafeVersion());
            plan.LogSummary = DefaultSummary();
            ShowToast(plan);
        }

        // ---- modal jobs ----------------------------------------------------------------------------------------------------------

        void TickJobs()
        {
            bool menu = _tickMenu;
            if (_open != null)
            {
                EvaluateOpen();
                if (_open != null)
                    return;
            }

            ModalJob job = NextJob();
            if (job == null)
                return;
            if (!job.IsAttempt)
            {
                if (_disabled)
                    return;
                if (!SafeEnabled())
                {
                    Debug("'" + job.Plan.Id + "' not shown: General/ShowMapNotices is off");
                    _jobs.Remove(job);
                    _t1 = T1State.Done;
                    return;
                }
            }
            if (!menu)
            {
                Wait("the main menu");
                return;
            }
            if (!Settled)
            {
                Wait("settle time");
                return;
            }
            if (_menuSeconds < job.NotBefore)
                return;
            if (!Pred(_modalPossible))
            {
                NoteBlocker();
                Wait(_blockedWait);
                job.Blocked += _dtMenu;
                if (job.Blocked >= BlockedBudget)
                    Fallback(job, "after " + job.Blocked.ToString("0", CultureInfo.InvariantCulture) + " menu-seconds with the message window busy" +
                                  (_blockerSeen.Length == 0 ? "" : " (" + _blockerSeen + ")"));
                return;
            }
            Show(job);
        }

        // The first answer to a failed attempt that has a newer menu show, else the T1 modal; parked jobs wait for the next menu show.
        ModalJob NextJob()
        {
            foreach (ModalJob job in _jobs)
                if (job.IsAttempt && !job.Parked && (_menuShowCount > job.QueuedMenuCount || job.EdgeReleased))
                    return job;
            foreach (ModalJob job in _jobs)
                if (!job.IsAttempt && !job.Parked)
                    return job;
            return null;
        }

        void Show(ModalJob job)
        {
            MapNoticeMessage message;
            try
            {
                message = MapNoticeText.Compose(job.Plan, SafeLanguage());
            }
            catch (Exception e)
            {
                OnFault(job.Plan.Id.ToString(), e);
                Displace(job);
                return;
            }

            double now = _env.Now;
            ModalTicket ticket;
            Exception shownError = null;
            try
            {
                ticket = _env.ShowModal(message.Caption, message.Text, message.Scrolled);
            }
            catch (Exception e)
            {
                shownError = e;
                ticket = ModalTicket.FailedAt(now, e.GetType().Name + ": " + e.Message);
            }
            if (ticket == null)
                ticket = ModalTicket.FailedAt(now, "no window was returned");

            if (ticket.Outcome == ModalOutcome.Failed)
            {
                OnFault(job.Plan.Id.ToString(), shownError ?? new InvalidOperationException(ticket.Error ?? "the window could not be shown"));
                Displace(job);
                return;
            }

            _consecutiveFaults = 0;
            if (ticket.Outcome == ModalOutcome.Suppressed)
            {
                Displace(job); // EFT showed nothing: nothing to log as shown
                return;
            }
            LogShowing(job, message);
            job.Ticket = ticket;
            _open = job;
            _windowGoneSince = double.NaN;
            _lastWait = null;
        }

        void EvaluateOpen()
        {
            ModalJob job = _open;
            ModalTicket ticket = job.Ticket;
            switch (ticket.Outcome)
            {
                case ModalOutcome.Pending:
                    CheckVanishedWindow(job);
                    return;
                case ModalOutcome.Accepted:
                    Seen(job, "accepted");
                    return;
                case ModalOutcome.Closed:
                    if (ticket.ClosedAt - ticket.ShownAt >= SeenAfter)
                        Seen(job, "closed after " + (ticket.ClosedAt - ticket.ShownAt).ToString("0.0", CultureInfo.InvariantCulture) + " s");
                    else
                        Displace(job);
                    return;
                default:
                    Displace(job);
                    return;
            }
        }

        void Seen(ModalJob job, string how)
        {
            _open = null;
            _jobs.Remove(job);
            if (!job.IsAttempt)
                _t1 = T1State.Done;
            Debug("'" + job.Plan.Id + "' seen (" + how + ")");
        }

        // The window did not reach the player: count it, retry later, and after three give the toast.
        void Displace(ModalJob job)
        {
            _open = null;
            job.Ticket = null;
            job.Displaced++;
            job.NotBefore = _menuSeconds + RetryDelay;
            Debug("'" + job.Plan.Id + "' displaced (" + job.Displaced.ToString(CultureInfo.InvariantCulture) + " of " + MaxModalAttempts.ToString(CultureInfo.InvariantCulture) + ")");
            if (job.Displaced >= MaxModalAttempts)
                Fallback(job, "after " + job.Displaced.ToString(CultureInfo.InvariantCulture) + " displaced modal attempts");
        }

        // The watchdog of a window whose callbacks never fired: every close path of EFT's message windows reaches our handlers, but a ticket that stayed
        // Pending would hold _open for the session and block every later notice, the answers to a failed raid included. When no EFT message window is
        // on screen any more for WindowGoneGrace seconds, the window is gone: treated as closed (seen only if it had been up for SeenAfter).
        void CheckVanishedWindow(ModalJob job)
        {
            if (Pred(_messageWindowOpen))
            {
                _windowGoneSince = double.NaN;
                return;
            }
            double now = SafeNow();
            if (double.IsNaN(_windowGoneSince))
            {
                _windowGoneSince = now;
                return;
            }
            if (now - _windowGoneSince < WindowGoneGrace)
                return;
            _windowGoneSince = double.NaN;
            Debug("'" + job.Plan.Id + "' window is gone without an answer; treated as closed");
            job.Ticket.Close(now);
        }

        // Remembers what kept the window busy (the environment's sub-check name) and rebuilds the wait text only when it changed: no allocation per frame.
        void NoteBlocker()
        {
            string blocker;
            try
            {
                blocker = _env.ModalBlocker ?? "";
            }
            catch
            {
                blocker = "";
            }
            if (string.Equals(blocker, _blockerSeen, StringComparison.Ordinal))
                return;
            _blockerSeen = blocker;
            _blockedWait = blocker.Length == 0 ? BlockedWaitText : BlockedWaitText + " (" + blocker + ")";
        }

        // The short toast stands in for the modal; the notice stays armed and re-arms at the next menu show. The Warning is written only when the toast
        // was really shown (a latched, impossible or failing toast is a Debug line): a log that says "shown as a toast" has to be true.
        void Fallback(ModalJob job, string how)
        {
            _open = null;
            job.Ticket = null;
            job.Parked = true;
            string id = job.Plan.Id.ToString();
            if (!job.IsAttempt && _disabled)
            {
                Debug("'" + id + "' parked " + how + "; no toast: the notices are switched off for this session");
                return;
            }
            if (!Pred(_toastPossible))
            {
                Debug("'" + id + "' parked " + how + "; no toast: the notification manager is not available");
                return;
            }
            MapNoticeMessage toast;
            try
            {
                toast = MapNoticeText.ComposeFallbackToast(job.Plan, SafeLanguage());
            }
            catch (Exception e)
            {
                OnFault(id, e);
                return;
            }
            bool shown = false;
            if (!Act(id, () => shown = _env.ShowToast(toast.Text)))
                return;
            if (shown)
                Warning("shown as a toast " + how);
            else
                Debug("'" + id + "' parked " + how + "; the toast was dropped by EFT");
        }

        // ---- T2 ------------------------------------------------------------------------------------------------------------------

        void TickSelection()
        {
            double now = _tickNow;
            if (!_selectionPending)
                return;
            bool current = Pred(_selectionIsCurrent);
            if (current)
                _selectionSeenCurrent = true;
            if (!current && (_selectionSeenCurrent || now - _selectionSince > SelectionGrace))
            {
                _selectionPending = false; // the screen closed: a late toast never appears
                _selectionSeenCurrent = false;
                return;
            }
            if (!current || _reminderShown || _reminderGroup == ReminderGroup.None || _disabled || _open != null)
                return;
            if (!SafeEnabled() || !Pred(_toastPossible))
                return;

            _reminderShown = true;
            _selectionPending = false;
            MapNoticeMessage message;
            try
            {
                message = MapNoticeText.ComposeReminder(_reminderGroup, SafeLanguage());
            }
            catch (Exception e)
            {
                OnFault(NoticeId.SelectionReminder.ToString(), e);
                return;
            }
            var plan = new MapNoticePlan(NoticeId.SelectionReminder) { LogSummary = DefaultSummary() };
            Info("showing '" + NoticeId.SelectionReminder + "' (toast) ref=" + message.Ref + " " + plan.LogSummary);
            Debug("text: " + OneLine(message.Text));
            Act(NoticeId.SelectionReminder.ToString(), () => _env.ShowToast(message.Text));
        }

        // ---- shared helpers --------------------------------------------------------------------------------------------------------

        void ShowToast(MapNoticePlan plan)
        {
            MapNoticeMessage message;
            try
            {
                message = MapNoticeText.Compose(plan, SafeLanguage());
            }
            catch (Exception e)
            {
                OnFault(plan.Id.ToString(), e);
                return;
            }
            Info("showing '" + plan.Id + "' (toast) ref=" + message.Ref + " " + (plan.LogSummary ?? DefaultSummary()));
            Debug("text: " + OneLine(message.Text));
            Act(plan.Id.ToString(), () => _env.ShowToast(message.Text));
        }

        void LogShowing(ModalJob job, MapNoticeMessage message)
        {
            if (!job.Logged)
            {
                job.Logged = true;
                Info("showing '" + job.Plan.Id + "' (modal) ref=" + message.Ref + " " + (job.Plan.LogSummary ?? DefaultSummary()));
            }
            Debug("text: " + OneLine(message.Text));
        }

        static string OneLine(string text) => (text ?? "").Replace("\r", "").Replace("\n", " | ");

        string DefaultSummary()
        {
            GateSnapshot gate = ReadGate();
            return "client=" + gate.LogCode + " server=not_needed offered=?/" + SafeMaps().Count.ToString(CultureInfo.InvariantCulture);
        }

        string NameOf(string mapKey)
        {
            foreach (MapInfo map in SafeMaps())
                if (string.Equals(map.Key, mapKey, StringComparison.Ordinal))
                    return map.Name;
            string name = mapKey ?? "?";
            return name.StartsWith("arena_", StringComparison.Ordinal) ? name.Substring("arena_".Length) : name;
        }

        void Wait(string what)
        {
            if (string.Equals(_lastWait, what, StringComparison.Ordinal))
                return;
            _lastWait = what;
            Debug("waiting: " + what);
        }

        // ---- the environment, made total ---------------------------------------------------------------------------------------------

        bool Pred(Func<bool> predicate)
        {
            try
            {
                return predicate();
            }
            catch
            {
                return false; // a predicate that fails says "no" and is not a fault
            }
        }

        bool SafeEnabled() => Pred(_noticesEnabled);

        double SafeNow()
        {
            try
            {
                return _env.Now;
            }
            catch
            {
                return 0;
            }
        }

        string SafeLanguage()
        {
            try
            {
                return MapNoticeText.LanguageFor(_env.Language);
            }
            catch
            {
                return MapNoticeText.LanguageEnglish;
            }
        }

        string SafeVersion()
        {
            try
            {
                return _env.ClientVersion;
            }
            catch
            {
                return null;
            }
        }

        IReadOnlyList<MapInfo> SafeMaps()
        {
            try
            {
                return _env.Maps ?? new MapInfo[0];
            }
            catch
            {
                return new MapInfo[0];
            }
        }

        IReadOnlyList<string> SafeSkippedHooks()
        {
            try
            {
                return _env.SkippedHooks;
            }
            catch
            {
                return null;
            }
        }

        GateSnapshot ReadGate()
        {
            try
            {
                return _env.ReadGate() ?? GateSnapshot.From(GateState.NotStarted, null, null);
            }
            catch
            {
                return GateSnapshot.From(GateState.CheckFailed, null, "the Arena check could not be read");
            }
        }

        ProbeView SafeProbe()
        {
            try
            {
                return _env.Probe ?? ProbeView.NotAsked;
            }
            catch
            {
                return new ProbeView(ProbeState.Failed);
            }
        }

        void Debug(string message)
        {
            try
            {
                _env.Debug(Tag + " " + message);
            }
            catch
            {
                // logging never faults the notices
            }
        }

        void Info(string message)
        {
            try
            {
                _env.Info(Tag + " " + message);
            }
            catch
            {
                // see Debug
            }
        }

        void Warning(string message)
        {
            try
            {
                _env.Warning(Tag + " " + message);
            }
            catch
            {
                // see Debug
            }
        }

        // ---- faults ----------------------------------------------------------------------------------------------------------------------

        /// <summary>Runs an action; false (and a counted fault) when it threw. A success resets the consecutive-fault counter.</summary>
        bool Act(string what, Action action)
        {
            try
            {
                action();
                _consecutiveFaults = 0;
                return true;
            }
            catch (Exception e)
            {
                OnFault(what, e);
                return false;
            }
        }

        /// <summary>Runs an entry point; an exception is a counted fault and never leaves.</summary>
        bool Guard(string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                OnFault(what, e);
                return false;
            }
        }

        void OnFault(string what, Exception e)
        {
            try
            {
                if (_faultWarned.Add(what))
                    Warning("could not show '" + what + "' (" + e.GetType().Name + ": " + e.Message + "); the reason is in the [Arena] lines of this log");
                CountFault(e);
            }
            catch
            {
                // the fault policy itself must not throw into EFT's frame
            }
        }

        // Three faults in a row latch T1, the toasts and N1 off until the next menu show (T3 is never latched).
        void CountFault(Exception e)
        {
            _consecutiveFaults++;
            if (_consecutiveFaults >= MaxConsecutiveFaults && !_disabled)
            {
                _disabled = true;
                Warning("switched off for this session after " + MaxConsecutiveFaults.ToString(CultureInfo.InvariantCulture) + " errors (" +
                        e.GetType().Name + ": " + e.Message + "); the maps themselves are not affected");
            }
        }
    }
}
