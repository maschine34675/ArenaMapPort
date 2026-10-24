#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// MapNoticeScheduler: the state machine of the in-game map notices over a scripted environment and a fake clock. The menu clock and its thresholds, T1 (gate
/// states, the location list, the file check, the server probe, silencing), delivery (waiting for a free window, seen or displaced, the retry, the toast
/// fallback, re-arming), T2 (the reminder toast), T3 (attempts and their answers), N1, row 22, the fault policy, the idle state and the degraded menu poll.
/// </summary>
public sealed class MapNoticeSchedulerTests
{
    sealed class Harness
    {
        internal readonly FakeNoticeEnvironment Env = new();
        internal readonly MapLoadFailures Failures = new();
        internal readonly MapNoticeScheduler Sched;

        internal Harness()
        {
            Sched = new MapNoticeScheduler(Env, Failures);
        }

        /// <summary>Advances the clock by <paramref name="seconds"/> in steps and ticks after each. The first tick after a wake-up adds no menu time.</summary>
        internal void Run(double seconds, double step = 0.25)
        {
            int ticks = (int)Math.Round(seconds / step);
            for (int i = 0; i < ticks; i++)
            {
                Env.Now += step;
                Sched.Tick();
            }
        }

        /// <summary>The first main menu, then enough time for the settle time to pass (about 2.5 menu-seconds).</summary>
        internal void FirstMenuAndSettle()
        {
            Sched.OnMenuShown(true);
            Run(3.0);
        }

        internal string Text(int index = 0) => Env.Modals[index].Text;

        internal bool Logged(IEnumerable<string> lines, string fragment) => lines.Any(line => line.Contains(fragment));
    }

    static Harness Failing(ArenaInstallCause cause = ArenaInstallCause.NotRegistered)
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.NotVerified(cause, "none");
        return h;
    }

    // ---- nothing to say ---------------------------------------------------------------------------------------------

    [Fact]
    public void AHealthyClientShowsNothingAsksNoOneAndGoesIdle()
    {
        var h = new Harness();
        h.FirstMenuAndSettle();

        Assert.Empty(h.Env.Modals);
        Assert.Empty(h.Env.Toasts);
        Assert.Equal(0, h.Env.ProbeStarts);
        Assert.Equal(1, h.Env.ScanCalls);
        Assert.Equal(4, h.Env.LastScanScope.Count);
        Assert.True(h.Sched.T1Done);
        Assert.Contains("[Notice] availability: arena=verified, offered=4/4 [arena_equator,arena_bay5,arena_airpit,arena_bowl], files=ok, server=not_needed", h.Env.Debugs);
        Assert.Empty(h.Env.Infos);
        Assert.Empty(h.Env.Warnings);
        Assert.False(h.Sched.Work); // idle: one flag read per frame from here on
    }

    [Fact]
    public void WhenIdleATickReadsNoStateAtAll()
    {
        var h = new Harness();
        h.FirstMenuAndSettle();
        Assert.False(h.Sched.Work);

        int now = h.Env.NowReads;
        int menu = h.Env.MenuReads;
        for (int i = 0; i < 1000; i++)
            h.Sched.Tick();
        Assert.Equal(now, h.Env.NowReads);
        Assert.Equal(menu, h.Env.MenuReads);
    }

    [Fact]
    public void ANewSchedulerIsIdleUntilTheFirstMenu()
    {
        var h = new Harness();
        Assert.False(h.Sched.Work);
        h.Run(5);
        Assert.Equal(0, h.Env.NowReads);
        h.Sched.OnMenuShown(true);
        Assert.True(h.Sched.Work);
        Assert.Equal(1, h.Sched.MenuShowCount);
    }

    [Fact]
    public void TheInRaidEscMenuHasNoProfileAndIsIgnored()
    {
        var h = new Harness();
        h.Sched.OnMenuShown(false);
        Assert.Equal(0, h.Sched.MenuShowCount);
        Assert.False(h.Sched.Work);
        h.Run(5);
        Assert.Equal(0, h.Env.NowReads);

        h.Sched.OnMenuShown(true);
        h.Sched.OnMenuShown(false);
        Assert.Equal(1, h.Sched.MenuShowCount);
    }

    // ---- T1: the client's own check -----------------------------------------------------------------------------------

    [Fact]
    public void T1ShowsItsModalOnlyAfterTheSettleTime()
    {
        Harness h = Failing();
        h.Sched.OnMenuShown(true);
        h.Run(1.75); // 1.5 menu-seconds
        Assert.Empty(h.Env.Modals);
        Assert.Contains("[Notice] waiting: settle time", h.Env.Debugs);

        h.Run(1.0);
        ModalShownCheck(h);
    }

    static void ModalShownCheck(Harness h)
    {
        var modal = Assert.Single(h.Env.Modals);
        Assert.Equal("ArenaMapPort", modal.Caption);
        Assert.Equal(modal.Text.Length > MapNoticeText.ModalLimit, modal.Scrolled); // the 480-character rule decides the window
        Assert.StartsWith("The Arena maps are not available: ArenaMapPort did not find Escape from Tarkov: Arena on this PC.\n", modal.Text);
        Assert.EndsWith("\nArenaMapPort 0.3.0 - ref NO_ARENA", modal.Text);
        Assert.Equal("[Notice] showing 'ClientNoArena' (modal) ref=NO_ARENA client=not_verified:not_registered server=not_needed offered=?/4",
            Assert.Single(h.Env.Infos));
        Assert.Contains(h.Env.Debugs, line => line.StartsWith("[Notice] text: The Arena maps are not available: ArenaMapPort did not find") && line.Contains(" | "));
    }

    [Fact]
    public void AnAcceptedModalIsSeenAndT1IsDoneForTheProcess()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        Assert.False(h.Sched.T1Done);
        Assert.True(h.Sched.ModalOpen);

        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1.0);
        Assert.True(h.Sched.T1Done);
        Assert.False(h.Sched.ModalOpen);
        Assert.Equal(0, h.Sched.JobCount);
        Assert.Contains("[Notice] 'ClientNoArena' seen (accepted)", h.Env.Debugs);

        // a later menu show never shows it again, whatever happens to the gate
        h.Env.Gate = NoticeTestData.NotVerified(ArenaInstallCause.FilesMismatch);
        h.Sched.OnMenuShown(true);
        h.Run(5);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void AModalThatStaysOpenIsNotShownAgain()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        h.Run(30);
        Assert.Single(h.Env.Modals);
        Assert.True(h.Sched.ModalOpen);
    }

    [Theory]
    [InlineData(ArenaInstallCause.NotRegistered, "NO_ARENA")]
    [InlineData(ArenaInstallCause.RegistryUnavailable, "REGISTRY")]
    [InlineData(ArenaInstallCause.FolderMissing, "FOLDER_BAD")]
    [InlineData(ArenaInstallCause.FilesMismatch, "ARENA_INCOMPLETE")]
    [InlineData(ArenaInstallCause.CheckFailed, "CHECK_FAILED")]
    public void EachClientFailureShowsItsOwnRef(ArenaInstallCause cause, string reference)
    {
        Harness h = Failing(cause);
        h.Env.Gate = NoticeTestData.NotVerified(cause, "registry", @"D:\Arena", 5, "reason");
        h.FirstMenuAndSettle();
        Assert.EndsWith("- ref " + reference, h.Text());
    }

    [Fact]
    public void AStillRunningCheckWaitsAndShowsThePendingToastAfterTenMenuSeconds()
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.State(GateState.Running);
        h.Sched.OnMenuShown(true);
        h.Run(9.0); // 8.75 menu-seconds
        Assert.Empty(h.Env.Toasts);
        Assert.Contains("[Notice] waiting: the Arena check is still running", h.Env.Debugs);

        h.Run(2.0);
        Assert.Equal(
            new[] { "ArenaMapPort: still checking your Arena installation (slow disk or unreachable path). The Arena maps load once it passes." },
            h.Env.Toasts);
        Assert.Equal("[Notice] showing 'CheckPending' (toast) ref=PENDING client=checking:none server=not_needed offered=?/4", Assert.Single(h.Env.Infos));

        h.Run(20); // only once
        Assert.Single(h.Env.Toasts);
        Assert.Empty(h.Env.Modals);

        // the check ends not verified: T1 goes on and shows its modal
        h.Env.Gate = NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered, "none");
        h.Run(1.0);
        Assert.Single(h.Env.Modals);
        Assert.Contains("ref NO_ARENA", h.Text().Replace("- ref", "ref"));
    }

    [Fact]
    public void ThePendingToastWaitsForTheNotificationManagerAndHonoursTheSwitch()
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.State(GateState.Running);
        h.Env.ToastPossible = false;
        h.Sched.OnMenuShown(true);
        h.Run(15);
        Assert.Empty(h.Env.Toasts);
        h.Env.ToastPossible = true;
        h.Run(0.5);
        Assert.Single(h.Env.Toasts);

        var off = new Harness();
        off.Env.Gate = NoticeTestData.State(GateState.Running);
        off.Env.NoticesEnabled = false;
        off.Sched.OnMenuShown(true);
        off.Run(15);
        Assert.Empty(off.Env.Toasts);
        Assert.Contains("[Notice] 'CheckPending' not shown: General/ShowMapNotices is off", off.Env.Debugs);
    }

    [Fact]
    public void ATimedOutCheckShowsTheSlowTextAtT1()
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.State(GateState.TimedOut);
        h.FirstMenuAndSettle();
        Assert.EndsWith("- ref CHECK_SLOW", h.Text());
    }

    [Fact]
    public void AnUnstartedGateIsTheInternalErrorText()
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.State(GateState.NotStarted);
        h.FirstMenuAndSettle();
        Assert.EndsWith("- ref CHECK_FAILED", h.Text());
    }

    // ---- the menu clock ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TimeAwayFromTheMainMenuDoesNotCountTowardsAnyThreshold()
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.State(GateState.Running);
        h.Sched.OnMenuShown(true);
        h.Run(5.0); // 4.75 menu-seconds
        double before = h.Sched.MenuSeconds;
        Assert.InRange(before, 4.5, 5.0);

        h.Env.MenuIsCurrent = false; // the hideout, a trader, a raid
        h.Run(60);
        Assert.Equal(before, h.Sched.MenuSeconds);
        Assert.Empty(h.Env.Toasts);

        h.Env.MenuIsCurrent = true;
        h.Run(3.0); // back at the menu: the first tick adds nothing, then 2.5 more
        Assert.InRange(h.Sched.MenuSeconds, before + 2.0, before + 3.0);
        Assert.Empty(h.Env.Toasts);
        h.Run(4.0);
        Assert.Single(h.Env.Toasts); // past ten menu-seconds
    }

    [Fact]
    public void AHitchIsNotMenuTime()
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.State(GateState.Running);
        h.Sched.OnMenuShown(true);
        h.Run(1.0);
        double before = h.Sched.MenuSeconds;
        h.Env.Now += 600; // a ten minute stall
        h.Sched.Tick();
        Assert.True(h.Sched.MenuSeconds - before <= MapNoticeScheduler.MaxClockStep + 0.001);
    }

    [Fact]
    public void T1WaitsAtMostThirtyMenuSecondsForTheLocationListThenProceedsWithOfferedUnknown()
    {
        var h = new Harness();
        h.Env.OfferedAvailable = false;
        h.Env.Scan = new FileScan(4, new[] { "arena_bay5_scenes.bundle" }, new[] { "Bay 5" });
        h.Sched.OnMenuShown(true);
        h.Run(25);
        Assert.Empty(h.Env.Modals);
        Assert.Contains("[Notice] waiting: the location list", h.Env.Debugs);
        Assert.Equal(0, h.Env.ScanCalls);

        h.Run(10); // past 30 menu-seconds
        Assert.Contains("[Notice] the location list was not available after 30 s; offered is unknown", h.Env.Debugs);
        Assert.Contains(h.Env.Debugs, line => line.StartsWith("[Notice] availability: arena=verified, offered=?/4, files=1 missing, server=not_needed"));
        Assert.Equal(0, h.Env.ProbeStarts); // an unknown list asks no one
        Assert.Equal(4, h.Env.LastScanScope.Count); // unknown: every registry map is checked
        Assert.EndsWith("- ref FILES_MISSING", h.Text());
        Assert.Contains("files for Bay 5 are incomplete (missing: 1, for example arena_bay5_scenes.bundle)", h.Text());
    }

    [Fact]
    public void TheLocationListThatArrivesInTimeIsUsed()
    {
        var h = new Harness();
        h.Env.OfferedAvailable = false;
        h.Sched.OnMenuShown(true);
        h.Run(10);
        h.Env.OfferedAvailable = true;
        h.Run(1.0);
        Assert.Empty(h.Env.Modals);
        Assert.True(h.Sched.T1Done);
        Assert.Contains("[Notice] availability: arena=verified, offered=4/4 [arena_equator,arena_bay5,arena_airpit,arena_bowl], files=ok, server=not_needed", h.Env.Debugs);
    }

    // ---- T1: the server ------------------------------------------------------------------------------------------------------

    static Harness NothingOffered()
    {
        var h = new Harness();
        h.Env.Locations = new List<OfferedLocation>();
        h.Env.WhenProbeStarts = () => h.Env.Probe = new ProbeView(ProbeState.Pending);
        return h;
    }

    [Fact]
    public void WhenNoMapIsOfferedTheServerIsAskedOnceAndItsReasonIsShown()
    {
        Harness h = NothingOffered();
        h.FirstMenuAndSettle();
        Assert.Equal(1, h.Env.ProbeStarts);
        Assert.Empty(h.Env.Modals);
        Assert.Contains("[Notice] waiting: the server status", h.Env.Debugs);

        h.Run(2.0);
        Assert.Equal(1, h.Env.ProbeStarts); // asked once
        Assert.Empty(h.Env.Modals);

        h.Env.Probe = NoticeTestData.Ok(NoticeTestData.Status("not_verified", "not_registered"));
        h.Run(0.5);
        var modal = Assert.Single(h.Env.Modals);
        Assert.Contains("the SPT server could not verify Escape from Tarkov: Arena (no launcher entry for Arena on the server PC)", modal.Text);
        Assert.Contains("create SPT_Runtime\\user\\mods\\ArenaMapPort\\config\\arena-install.json", modal.Text);
        Assert.EndsWith("- ref SERVER_NO_ARENA, server 0.3.0", modal.Text);
        Assert.Contains("[Notice] showing 'ServerNoArena' (modal) ref=SERVER_NO_ARENA client=verified:none server=ok:not_verified/not_registered offered=0/4",
            h.Env.Infos);
        Assert.Contains("[Notice] server status: ok:not_verified/not_registered", h.Env.Debugs);
        Assert.Equal(ReminderGroup.ServerNoVerify, h.Sched.ReminderGroupFound);
    }

    [Fact]
    public void AServerWithoutTheRouteIsUnknown()
    {
        Harness h = NothingOffered();
        h.Env.WhenProbeStarts = () => h.Env.Probe = new ProbeView(ProbeState.NotFound);
        h.FirstMenuAndSettle();
        h.Run(0.5);
        Assert.EndsWith("- ref SERVER_UNKNOWN", h.Text());
        Assert.Contains("server=404 offered=0/4", Assert.Single(h.Env.Infos));
        Assert.Equal(ReminderGroup.ServerNoOffer, h.Sched.ReminderGroupFound);
    }

    [Fact]
    public void AProbeThatNeverAnswersIsGivenFiveSecondsThenReadsAsNoAnswer()
    {
        Harness h = NothingOffered();
        h.FirstMenuAndSettle();
        Assert.Equal(1, h.Env.ProbeStarts);
        h.Run(4.0);
        Assert.Empty(h.Env.Modals);
        h.Run(2.0);
        Assert.EndsWith("- ref SERVER_NO_ANSWER", h.Text());
        Assert.Contains("server=failed offered=0/4", Assert.Single(h.Env.Infos));
    }

    [Fact]
    public void AProbeThatCannotStartIsUnavailable()
    {
        Harness h = NothingOffered();
        h.Env.WhenProbeStarts = () => throw new InvalidOperationException("no http");
        h.FirstMenuAndSettle();
        Assert.Contains("server=no_http offered=0/4", Assert.Single(h.Env.Infos));
        Assert.EndsWith("- ref SERVER_UNKNOWN", h.Text());
        Assert.Contains(h.Env.Warnings, line => line.Contains("could not show 'probe' (InvalidOperationException: no http)"));
    }

    [Fact]
    public void SomeMapsMissingShowsTheVersionSkewTextWhenTheServerHasNoRoute()
    {
        var h = new Harness();
        h.Env.Locations = NoticeTestData.Offered("arena_equator", "arena_bay5", "arena_airpit");
        h.Env.WhenProbeStarts = () => h.Env.Probe = new ProbeView(ProbeState.NotFound);
        h.FirstMenuAndSettle();
        h.Run(0.5);
        Assert.Contains("The SPT server does not offer Bowl, which this game knows.", h.Text());
        Assert.EndsWith("- ref SERVER_PARTIAL", h.Text());
        Assert.Equal(ReminderGroup.None, h.Sched.ReminderGroupFound); // row 15 has no selection reminder
    }

    [Fact]
    public void ShowMapNoticesFalseSilencesT1BeforeAnyRequest()
    {
        Harness h = NothingOffered();
        h.Env.NoticesEnabled = false;
        h.FirstMenuAndSettle();
        Assert.Equal(0, h.Env.ProbeStarts);
        Assert.Empty(h.Env.Modals);
        Assert.Contains("[Notice] 'T1' not shown: General/ShowMapNotices is off", h.Env.Debugs);
        Assert.True(h.Sched.T1Done);
        Assert.Equal(ReminderGroup.None, h.Sched.ReminderGroupFound);

        Harness client = Failing();
        client.Env.NoticesEnabled = false;
        client.FirstMenuAndSettle();
        Assert.Empty(client.Env.Modals);
        Assert.Contains("[Notice] 'ClientNoArena' not shown: General/ShowMapNotices is off", client.Env.Debugs);
    }

    [Fact]
    public void TheSwitchTurnedOffWhileAModalIsQueuedDropsIt()
    {
        Harness h = Failing();
        h.Env.ModalPossible = false;
        h.FirstMenuAndSettle();
        Assert.Empty(h.Env.Modals);
        h.Env.NoticesEnabled = false;
        h.Env.ModalPossible = true;
        h.Run(1.0);
        Assert.Empty(h.Env.Modals);
        Assert.True(h.Sched.T1Done);
        Assert.Equal(0, h.Sched.JobCount);
    }

    [Fact]
    public void AMissingFileIsReportedWithoutAnyRequest()
    {
        var h = new Harness();
        h.Env.Scan = new FileScan(4, new[] { "arena_bowl.json", "arena_bay5_scenes.bundle" }, new[] { "Bay 5", "Bowl" });
        h.FirstMenuAndSettle();
        Assert.Equal(0, h.Env.ProbeStarts);
        Assert.EndsWith("- ref FILES_MISSING", h.Text());
        Assert.Contains("files for Bay 5, Bowl are incomplete (missing: 2, for example arena_bowl.json)", h.Text());
    }

    [Fact]
    public void ASkippedHookIsReportedAheadOfFiles()
    {
        var h = new Harness();
        h.Env.SkippedHooks = new[] { "BundlesManager.LoadBundleAsync" };
        h.Env.Scan = new FileScan(4, new[] { "x" }, new[] { "Bowl" });
        h.FirstMenuAndSettle();
        Assert.EndsWith("- ref HOOK_MISSING", h.Text());
        Assert.Contains("(part BundlesManager.LoadBundleAsync could not be attached)", h.Text());
    }

    [Fact]
    public void AnUnverifiedClientDoesNotScanFilesOrAskTheServer()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        Assert.Equal(0, h.Env.ScanCalls);
        Assert.Equal(0, h.Env.ProbeStarts);
        Assert.Contains(h.Env.Debugs, line => line.Contains("files=skipped"));
    }

    [Fact]
    public void AVeryLongPathInTheJsonExampleGoesToTheScrolledWindow()
    {
        Harness h = NothingOffered();
        h.Env.Gate = NoticeTestData.Verified(@"D:\" + new string('x', 200));
        h.Env.WhenProbeStarts = () => h.Env.Probe = NoticeTestData.Ok(NoticeTestData.Status("not_verified", "not_registered"));
        h.FirstMenuAndSettle();
        h.Run(0.5);
        Assert.True(h.Env.Modals[0].Scrolled);
        Assert.True(h.Env.Modals[0].Text.Length > MapNoticeText.ModalLimit);
    }

    [Fact]
    public void TheGermanTableIsUsedForEftsGermanLocale()
    {
        Harness h = Failing();
        h.Env.Language = "ge";
        h.FirstMenuAndSettle();
        Assert.StartsWith("Die Arena-Karten sind nicht verfügbar: ArenaMapPort hat Escape from Tarkov: Arena auf diesem PC nicht gefunden.", h.Text());
        Assert.EndsWith("- ref NO_ARENA", h.Text());

        Harness french = Failing();
        french.Env.Language = "fr";
        french.FirstMenuAndSettle();
        Assert.StartsWith("The Arena maps are not available", french.Text());

        Harness unknown = Failing();
        unknown.Env.Language = "";
        unknown.FirstMenuAndSettle();
        Assert.StartsWith("The Arena maps are not available", unknown.Text());
    }

    // ---- delivery -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AModalWaitsForAFreeWindowAndSaysSoOnce()
    {
        Harness h = Failing();
        h.Env.ModalPossible = false; // another message window, an EFT error screen, a critical error screen, the preloader, a raid ...
        h.FirstMenuAndSettle();
        h.Run(20);
        Assert.Empty(h.Env.Modals);
        Assert.Equal(1, h.Env.Debugs.Count(line => line == "[Notice] waiting: a free message window"));

        h.Env.ModalPossible = true;
        h.Run(0.5);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void AModalWaitsForTheMainMenuToBeTheCurrentScreen()
    {
        Harness h = Failing();
        h.Sched.OnMenuShown(true);
        h.Env.MenuIsCurrent = false; // the screen changed to the hideout before the settle time passed
        h.Run(10);
        Assert.Empty(h.Env.Modals);
        h.Env.MenuIsCurrent = true;
        h.Run(3);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void AModalClosedWithinTwoSecondsWasDisplacedAndComesBackAfterTheRetryDelay()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        h.Env.LastTicket.Close(h.Env.LastTicket.ShownAt + 0.5); // EFT showed its own window over ours

        h.Run(0.5);
        Assert.Single(h.Env.Modals);
        Assert.Contains("[Notice] 'ClientNoArena' displaced (1 of 3)", h.Env.Debugs);
        Assert.False(h.Sched.T1Done);

        h.Run(1.5); // the retry delay of one menu-second
        Assert.Equal(2, h.Env.Modals.Count);
        Assert.Single(h.Env.Infos); // the "showing" line is one per notice, not per attempt
        Assert.Equal(h.Env.Modals[0].Text, h.Env.Modals[1].Text);

        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1.0);
        Assert.True(h.Sched.T1Done);
    }

    [Fact]
    public void AModalClosedAfterTwoSecondsWasSeenEvenWithoutOk()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        h.Run(2.5);
        h.Env.LastTicket.Close(h.Env.Now); // the player pressed Escape after reading it
        h.Run(1.0);
        Assert.True(h.Sched.T1Done);
        Assert.Contains(h.Env.Debugs, line => line.StartsWith("[Notice] 'ClientNoArena' seen (closed after "));
        h.Run(10);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void ASuppressedModalIsDisplacedAndThreeDisplacementsGiveTheReminderToast()
    {
        Harness h = Failing();
        h.Env.ModalOverride = _ => ModalTicket.SuppressedAt(h.Env.Now);
        h.FirstMenuAndSettle();
        h.Run(6.0);

        Assert.Equal(3, h.Env.Modals.Count);
        Assert.Contains("[Notice] shown as a toast after 3 displaced modal attempts", h.Env.Warnings);
        Assert.Equal(
            new[] { "ArenaMapPort: the Arena maps are not available (Arena was not found on this PC). Install Arena with the launcher, or set F12 > ArenaInstallPath, then restart." },
            h.Env.Toasts);
        Assert.False(h.Sched.T1Done); // still armed, parked until the next menu show
        Assert.Empty(h.Env.Infos); // nothing reached the player as a modal, so nothing was "shown"

        h.Run(20); // parked: no further attempt, no second toast
        Assert.Equal(3, h.Env.Modals.Count);
        Assert.Single(h.Env.Toasts);
    }

    [Fact]
    public void ADisplacedNoticeIsReArmedWithFreshCountersAtTheNextMenuShow()
    {
        Harness h = Failing();
        h.Env.ModalOverride = _ => ModalTicket.SuppressedAt(h.Env.Now);
        h.FirstMenuAndSettle();
        h.Run(6.0);
        Assert.Equal(3, h.Env.Modals.Count);

        h.Env.ModalOverride = null;
        h.Sched.OnMenuShown(true); // the player went to the hideout and came back
        h.Run(3.0);
        Assert.Equal(4, h.Env.Modals.Count);
        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1.0);
        Assert.True(h.Sched.T1Done);
    }

    [Fact]
    public void AFreshArmingStartsFromZeroDisplacements()
    {
        Harness h = Failing();
        h.Env.ModalOverride = _ => ModalTicket.SuppressedAt(h.Env.Now);
        h.FirstMenuAndSettle();
        h.Run(6.0);
        h.Sched.OnMenuShown(true);
        h.Run(3.0);
        Assert.Equal(4, h.Env.Modals.Count);
        h.Run(6.0);
        Assert.Equal(6, h.Env.Modals.Count); // three more attempts, then parked again
        Assert.Equal(2, h.Env.Warnings.Count(line => line.Contains("displaced modal attempts")));
        Assert.Equal(2, h.Env.Toasts.Count);
    }

    [Fact]
    public void TwoMinutesOfAWindowThatStaysBusyGiveTheToastAndParkTheNotice()
    {
        Harness h = Failing();
        h.Env.ModalPossible = false;
        h.FirstMenuAndSettle();
        h.Run(110);
        Assert.Empty(h.Env.Toasts);
        h.Run(20);
        Assert.Single(h.Env.Toasts);
        // the busy case is worded in menu-seconds (no modal was ever displaced), and the Warning is written because the toast really was shown
        Assert.Contains(h.Env.Warnings, line => line.StartsWith("[Notice] shown as a toast after 12") && line.EndsWith(" menu-seconds with the message window busy"));

        h.Env.ModalPossible = true;
        h.Run(5);
        Assert.Empty(h.Env.Modals); // parked until the next menu show
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void TheBusyBudgetCountsOnlyMenuSecondsWithTheWindowBusy()
    {
        Harness h = Failing();
        h.Env.ModalPossible = false;
        h.FirstMenuAndSettle();
        h.Run(60);
        h.Env.MenuIsCurrent = false;
        h.Run(300); // away: not counted
        h.Env.MenuIsCurrent = true;
        h.Run(30);
        Assert.Empty(h.Env.Toasts); // about 90 busy menu-seconds so far
    }

    [Fact]
    public void ANoticeThatCannotComposeIsAFaultNotACrash()
    {
        Harness h = Failing();
        h.Env.ModalOverride = _ => throw new InvalidOperationException("override failure");
        h.FirstMenuAndSettle();
        h.Run(1.0);
        Assert.Contains(h.Env.Warnings, line => line.Contains("could not show 'ClientNoArena'"));
    }

    // ---- T2 --------------------------------------------------------------------------------------------------------------------------

    static Harness AfterNoArenaT1()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1.0);
        Assert.True(h.Sched.T1Done);
        return h;
    }

    [Fact]
    public void TheSelectionScreenGetsTheReminderToastOnce()
    {
        Harness h = AfterNoArenaT1();
        h.Env.SelectionIsCurrent = true;
        h.Sched.OnSelectionShown();
        h.Run(1.0);
        Assert.Equal(
            new[] { "ArenaMapPort: the Arena maps are not available (Arena was not found on this PC). Install Arena with the launcher, or set F12 > ArenaInstallPath, then restart." },
            h.Env.Toasts);
        Assert.Contains("[Notice] showing 'SelectionReminder' (toast) ref=SELECTION client=not_verified:not_registered server=not_needed offered=?/4", h.Env.Infos);

        h.Sched.OnSelectionShown(); // the player opens the screen again
        h.Run(1.0);
        Assert.Single(h.Env.Toasts);
    }

    [Fact]
    public void TheReminderCarriesTheCauseSpecificFix()
    {
        var h = new Harness();
        h.Env.Locations = new List<OfferedLocation>();
        h.Env.WhenProbeStarts = () => h.Env.Probe = NoticeTestData.Ok(NoticeTestData.Status("not_verified", "not_registered"));
        h.FirstMenuAndSettle();
        h.Run(0.5);
        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1.0);
        h.Env.SelectionIsCurrent = true;
        h.Sched.OnSelectionShown();
        h.Run(1.0);
        Assert.Equal(
            "ArenaMapPort: the Arena maps are not available (the server could not verify Arena). Set or fix the server's arena-install.json (see the README), then restart it.",
            Assert.Single(h.Env.Toasts));
    }

    [Fact]
    public void TheReminderIsInTheLanguageOfTheClient()
    {
        Harness h = Failing();
        h.Env.Language = "ge";
        h.FirstMenuAndSettle();
        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1.0);
        h.Env.SelectionIsCurrent = true;
        h.Sched.OnSelectionShown();
        h.Run(1.0);
        Assert.StartsWith("ArenaMapPort: Die Arena-Karten sind nicht verfügbar (Arena wurde auf diesem PC nicht gefunden).", Assert.Single(h.Env.Toasts));
    }

    [Fact]
    public void ThereIsNoReminderWhenT1FoundNothingWrong()
    {
        var h = new Harness();
        h.FirstMenuAndSettle();
        h.Env.SelectionIsCurrent = true;
        h.Sched.OnSelectionShown();
        h.Run(2.0);
        Assert.Empty(h.Env.Toasts);
    }

    [Fact]
    public void ThereIsNoReminderForRowsFourteenToSixteen()
    {
        var h = new Harness();
        h.Env.Scan = new FileScan(4, new[] { "x" }, new[] { "Bowl" });
        h.FirstMenuAndSettle();
        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1.0);
        h.Env.SelectionIsCurrent = true;
        h.Sched.OnSelectionShown();
        h.Run(2.0);
        Assert.Empty(h.Env.Toasts);
    }

    [Fact]
    public void ThereIsNoReminderWhenTheNoticesAreSwitchedOff()
    {
        Harness h = Failing();
        h.Env.NoticesEnabled = false;
        h.FirstMenuAndSettle();
        h.Env.SelectionIsCurrent = true;
        h.Sched.OnSelectionShown();
        h.Run(2.0);
        Assert.Empty(h.Env.Toasts);
    }

    [Fact]
    public void TheReminderNeverAppearsAfterTheSelectionScreenClosed()
    {
        Harness h = AfterNoArenaT1();
        h.Env.ToastPossible = false; // no notification manager yet
        h.Env.SelectionIsCurrent = true;
        h.Sched.OnSelectionShown();
        h.Run(1.0);
        h.Env.SelectionIsCurrent = false; // the screen closed before the toast could be shown
        h.Run(1.0);

        h.Env.ToastPossible = true;
        h.Env.SelectionIsCurrent = true; // current again, but nobody opened it: no late toast
        h.Run(2.0);
        Assert.Empty(h.Env.Toasts);
    }

    [Fact]
    public void TheReminderWaitsWhileOurOwnModalIsStillOpen()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        Assert.True(h.Sched.ModalOpen);
        h.Env.SelectionIsCurrent = true;
        h.Sched.OnSelectionShown();
        h.Run(1.0);
        Assert.Empty(h.Env.Toasts);

        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1.0);
        Assert.Single(h.Env.Toasts);
    }

    [Fact]
    public void AReminderForAScreenThatNeverBecomesCurrentExpires()
    {
        Harness h = AfterNoArenaT1();
        h.Sched.OnSelectionShown();
        h.Run(8.0);
        Assert.False(h.Sched.Work);
        h.Env.SelectionIsCurrent = true;
        h.Run(2.0);
        Assert.Empty(h.Env.Toasts);
    }

    // ---- T3: attempts ------------------------------------------------------------------------------------------------------------------

    static Harness HealthyAfterT1()
    {
        var h = new Harness();
        h.FirstMenuAndSettle();
        Assert.True(h.Sched.T1Done);
        return h;
    }

    // An attempt for Bowl whose raid never starts: loading screen, comeback, then the next main menu.
    static void FailedAttempt(Harness h, Action recordFailures)
    {
        h.Sched.NoteAttemptStarted("arena_bowl");
        recordFailures?.Invoke();
        h.Env.MenuIsCurrent = false;
        h.Run(5);
        h.Sched.NoteComeback();
        h.Run(1);
        h.Env.MenuIsCurrent = true;
        h.Sched.OnMenuShown(true);
        h.Run(3);
    }

    [Fact]
    public void AFailedAttemptShowsItsCauseAtTheNextMenuAndDuplicatesCollapse()
    {
        Harness h = HealthyAfterT1();
        FailedAttempt(h, () =>
        {
            for (int i = 0; i < 3; i++)
                h.Failures.NoteKey("arena_bowl", KeyFailure.ContentDiffers, "d", "0.6.0.0.47752", "0.6.1.0.48206", null, "arena_bowl.json");
        });

        var modal = Assert.Single(h.Env.Modals);
        Assert.StartsWith("Bowl cannot be opened: Arena 0.6.1.0.48206 is installed, but this ArenaMapPort was built for Arena 0.6.0.0.47752", modal.Text);
        Assert.EndsWith("- ref KEY_UPDATED", modal.Text);
        Assert.Contains(h.Env.Debugs, line => line == "[Notice] attempt: map=arena_bowl started");
        Assert.Contains(h.Env.Debugs, line => line == "[Notice] attempt: map=arena_bowl failed records=3");
        Assert.Contains(h.Env.Infos, line => line.StartsWith("[Notice] showing 'KeyUpdated' (modal) ref=KEY_UPDATED"));
        Assert.Equal(0, h.Failures.Count); // the blocking records were consumed
    }

    [Fact]
    public void T3WaitsForAMenuShowNewerThanTheComeback()
    {
        Harness h = HealthyAfterT1();
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "arena_bowl_scenes.bundle", "missing");
        h.Run(3);
        h.Sched.NoteComeback();
        h.Run(10); // EFT's generic popup, the menu not yet shown
        Assert.Empty(h.Env.Modals);
        Assert.True(h.Sched.Work);

        h.Sched.OnMenuShown(true);
        h.Run(1.5);
        Assert.Empty(h.Env.Modals); // settle time
        h.Run(1.5);
        var modal = Assert.Single(h.Env.Modals);
        Assert.StartsWith("Bowl could not be loaded: one of ArenaMapPort's map files is missing or damaged (arena_bowl_scenes.bundle).", modal.Text);
        Assert.EndsWith("- ref BUNDLE_BAD", modal.Text);
    }

    [Fact]
    public void AGateRefusalIsAnsweredWithTheClientTextAndTheMapLead()
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.NotVerified(ArenaInstallCause.FilesMismatch, "registry", @"D:\Arena", 12);
        h.Env.NoticesEnabled = false; // T1 silent; T3 is an answer to what the player just did
        h.FirstMenuAndSettle();
        Assert.Empty(h.Env.Modals);

        FailedAttempt(h, () => h.Failures.NoteGateRefused("arena_bowl", "not verified"));
        var modal = Assert.Single(h.Env.Modals);
        Assert.StartsWith("Bowl could not be loaded: your Arena installation at D:\\Arena looks incomplete or is still updating (files missing or changed: 12).", modal.Text);
        Assert.EndsWith("- ref ARENA_INCOMPLETE", modal.Text);
    }

    [Fact]
    public void ThereIsNoCauseModalWithoutRecordsUnlessThePlayerCancelled()
    {
        Harness h = HealthyAfterT1();
        FailedAttempt(h, null);
        var modal = Assert.Single(h.Env.Modals);
        Assert.StartsWith("Bowl did not start, and ArenaMapPort recorded no cause for it.", modal.Text);
        Assert.EndsWith("- ref NO_CAUSE", modal.Text);

        Harness canceled = HealthyAfterT1();
        canceled.Env.MatchingCanceledByPlayer = true;
        FailedAttempt(canceled, null);
        Assert.Empty(canceled.Env.Modals);
        Assert.Contains(canceled.Env.Debugs, line => line.Contains("no notice (cancelled by the player)"));
    }

    [Fact]
    public void AnAttemptThatStartedTheRaidIsNotAFailure()
    {
        Harness h = HealthyAfterT1();
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Run(2);
        h.Env.InStartedRaid = true;
        h.Run(1);
        Assert.Contains("[Notice] attempt: map=arena_bowl succeeded", h.Env.Debugs);

        h.Env.InStartedRaid = false;
        h.Env.MenuIsCurrent = false;
        h.Sched.NoteComeback(); // the raid ended
        h.Env.MenuIsCurrent = true;
        h.Sched.OnMenuShown(true);
        h.Run(4);
        Assert.Empty(h.Env.Modals);
        Assert.False(h.Sched.Work);
    }

    [Fact]
    public void NativeErrorDuringLoadingSuppressesOnlyTheRedundantNoCauseAnswer()
    {
        Harness h = HealthyAfterT1();
        FailedAttempt(h, () => h.Sched.NoteNativeErrorReported());
        Assert.Empty(h.Env.Modals);
        Assert.Contains(h.Env.Debugs, line => line.Contains("error already reported by EFT; no map failure recorded"));

        // A fresh attempt, even on the same map, does not inherit that error.
        FailedAttempt(h, null);
        Assert.EndsWith("- ref NO_CAUSE", Assert.Single(h.Env.Modals).Text);
    }

    [Theory]
    [InlineData(MapLoadFailureKind.BundleMissing)]
    [InlineData(MapLoadFailureKind.Integrity)]
    [InlineData(MapLoadFailureKind.GateRefused)]
    [InlineData(MapLoadFailureKind.Key)]
    public void NativeErrorDoesNotHideAnOwnedFailureEvenWithMenuNoticesDisabled(MapLoadFailureKind kind)
    {
        Harness h = HealthyAfterT1();
        h.Env.NoticesEnabled = false;
        FailedAttempt(h, () =>
        {
            h.Sched.NoteNativeErrorReported();
            if (kind == MapLoadFailureKind.GateRefused) h.Failures.NoteGateRefused("arena_bowl", "gate");
            else if (kind == MapLoadFailureKind.Key) h.Failures.NoteKey("arena_bowl", KeyFailure.KeyFileMissing, "missing", null, null, null, "bowl.json");
            else h.Failures.NoteBundle("arena_bowl", kind, "bowl.bundle", "missing or damaged");
        });
        Assert.DoesNotContain("NO_CAUSE", Assert.Single(h.Env.Modals).Text);
    }

    [Fact]
    public void NativeErrorsOutsideTheLoadingAttemptDoNotSilenceItsFallback()
    {
        Harness h = HealthyAfterT1();
        h.Sched.NoteNativeErrorReported(); // menu error before loading
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Env.InStartedRaid = true; // runtime error before the scheduler has polled Started
        h.Sched.NoteNativeErrorReported();
        h.Env.InStartedRaid = false;
        h.Env.MenuIsCurrent = false;
        h.Sched.NoteComeback();
        h.Sched.NoteNativeErrorReported(); // cannot withdraw an already queued answer
        h.Env.MenuIsCurrent = true;
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.EndsWith("- ref NO_CAUSE", Assert.Single(h.Env.Modals).Text);
    }

    [Fact]
    public void NativeErrorDoesNotConsumeNonblockingOrOtherMapRecords()
    {
        Harness h = HealthyAfterT1();
        FailedAttempt(h, () =>
        {
            h.Sched.NoteNativeErrorReported();
            h.Failures.NoteNavMesh("arena_bowl", NavMeshCause.NoKey, "missing");
            h.Failures.NoteBundle("arena_bay5", MapLoadFailureKind.BundleMissing, "bay5.bundle", "other map");
        });
        Assert.Empty(h.Env.Modals);
        Assert.Equal(2, h.Failures.Count);
    }

    [Fact]
    public void OnlyRecordsOfTheAttemptsWindowAndMapAreAnsweredAndTheOthersWait()
    {
        Harness h = HealthyAfterT1();
        h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleInvalid, "stale.bundle", "from an earlier time"); // before the attempt
        FailedAttempt(h, () =>
        {
            h.Failures.NoteBundle("arena_bay5", MapLoadFailureKind.BundleMissing, "other_map.bundle", "other map");
            h.Failures.NoteNavMesh("arena_bowl", NavMeshCause.NoKey, "no key");
        });
        // no blocking record of Bowl in the window: NoCause (the old record is outside, the NavMesh one never blocks, the other map is not ours)
        Assert.EndsWith("- ref NO_CAUSE", Assert.Single(h.Env.Modals).Text);
        Assert.Equal(3, h.Failures.Count);
    }

    [Fact]
    public void ANewAttemptSupersedesAnOlderOne()
    {
        Harness h = HealthyAfterT1();
        h.Sched.NoteAttemptStarted("arena_bay5");
        h.Failures.NoteBundle("arena_bay5", MapLoadFailureKind.BundleMissing, "a.bundle", "x");
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "b.bundle", "x");
        h.Env.MenuIsCurrent = false;
        h.Run(2);
        h.Sched.NoteComeback();
        h.Env.MenuIsCurrent = true;
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Contains("(b.bundle)", Assert.Single(h.Env.Modals).Text);
    }

    [Fact]
    public void T3IgnoresShowMapNoticesFalseAndGoesToTheScrolledWindowWhenLong()
    {
        Harness h = HealthyAfterT1();
        h.Env.NoticesEnabled = false;
        FailedAttempt(h, () => h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, new string('f', 300), "x"));
        var modal = Assert.Single(h.Env.Modals);
        Assert.True(modal.Scrolled);
    }

    [Fact]
    public void EveryFailedAttemptShowsAgain()
    {
        Harness h = HealthyAfterT1();
        for (int round = 1; round <= 3; round++)
        {
            FailedAttempt(h, () => h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "b.bundle", "x"));
            Assert.Equal(round, h.Env.Modals.Count);
            h.Env.LastTicket.Accept(h.Env.Now);
            h.Run(1.0);
        }
    }

    [Fact]
    public void AnAnswerThatCannotBeShownFallsBackToAToastAndStaysArmed()
    {
        Harness h = HealthyAfterT1();
        h.Env.ModalOverride = _ => ModalTicket.SuppressedAt(h.Env.Now);
        FailedAttempt(h, () => h.Failures.NoteKey("arena_bowl", KeyFailure.ContentDiffers, "d", "0.6.0.0.47752", "0.6.1.0.48206", null, "arena_bowl.json"));
        h.Run(6);
        Assert.Equal(3, h.Env.Modals.Count);
        string toast = Assert.Single(h.Env.Toasts);
        Assert.StartsWith("ArenaMapPort: Bowl cannot be opened: Arena 0.6.1.0.48206 is installed", toast);

        h.Env.ModalOverride = null;
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Equal(4, h.Env.Modals.Count);
    }

    [Fact]
    public void TwoFailedAttemptsAreAnsweredOneAfterTheOther()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(1);
        Assert.True(h.Sched.T1Done);
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "first.bundle", "x");
        h.Sched.NoteComeback();
        h.Sched.NoteAttemptStarted("arena_bay5");
        h.Failures.NoteBundle("arena_bay5", MapLoadFailureKind.BundleMissing, "second.bundle", "x");
        h.Sched.NoteComeback();
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Contains("(first.bundle)", Assert.Single(h.Env.Modals.Skip(1)).Text);
        h.Env.LastTicket.Accept(h.Env.Now);
        h.Run(2);
        Assert.Contains("(second.bundle)", h.Env.Modals[2].Text);
    }

    // ---- N1 ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARaidWithoutNavMeshGetsItsToastOnceTheRaidHasStarted()
    {
        Harness h = HealthyAfterT1();
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Failures.NoteNavMesh("arena_bowl", NavMeshCause.NoKey, "no key");
        h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
        h.Env.MenuIsCurrent = false;
        h.Run(3); // still loading
        Assert.Empty(h.Env.Toasts);

        h.Env.InStartedRaid = true;
        h.Env.CurrentRaidMapKey = "arena_bowl";
        h.Run(1);
        Assert.Equal(
            new[] { @"ArenaMapPort: Bowl: the navigation data could not be loaded (its unlock file could not be used); bots will not move. Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [NavMesh]." },
            h.Env.Toasts);
        Assert.Contains(h.Env.Infos, line => line.StartsWith("[Notice] showing 'NavMeshMissing' (toast) ref=NAVMESH"));
        h.Run(5);
        Assert.Single(h.Env.Toasts);
        Assert.Empty(h.Env.Modals);
    }

    [Fact]
    public void EveryLaterRaidOnTheMapShowsTheToastAgainWithTheSameReason()
    {
        Harness h = HealthyAfterT1();
        h.Failures.NoteNavMesh("arena_bowl", NavMeshCause.FileMissing, "missing");
        for (int raid = 1; raid <= 3; raid++)
        {
            h.Env.InStartedRaid = false;
            h.Env.CurrentRaidMapKey = null;
            h.Sched.NoteAttemptStarted("arena_bowl");
            h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
            h.Run(1);
            h.Env.InStartedRaid = true;
            h.Env.CurrentRaidMapKey = "arena_bowl";
            h.Run(1);
            Assert.Equal(raid, h.Env.Toasts.Count);
            Assert.Contains("(the file is missing)", h.Env.Toasts[^1]);
            h.Env.InStartedRaid = false;
            h.Run(1);
            h.Sched.NoteRaidEnded();
        }
    }

    [Fact]
    public void AStaleRaidRecordNeverFiresForAnotherMapOrAfterTheRaidEnded()
    {
        Harness h = HealthyAfterT1();
        h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
        h.Env.InStartedRaid = true;
        h.Env.CurrentRaidMapKey = "arena_bay5"; // another map's raid
        h.Run(2);
        Assert.Empty(h.Env.Toasts);
        h.Env.CurrentRaidMapKey = "arena_bowl"; // the record was cleared by the other map
        h.Run(2);
        Assert.Empty(h.Env.Toasts);

        h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
        h.Sched.NoteRaidEnded(); // scene unload or raid end
        h.Run(2);
        Assert.Empty(h.Env.Toasts);

        h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
        h.Sched.NoteAttemptStarted("arena_bowl"); // a new raid begins: the old record cannot belong to it
        h.Run(2);
        Assert.Empty(h.Env.Toasts);
    }

    [Fact]
    public void ARaidThatEndedWithoutTheToastEverBeingPossibleClearsTheRecord()
    {
        Harness h = HealthyAfterT1();
        h.Env.ToastPossible = false;
        h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
        h.Env.InStartedRaid = true;
        h.Env.CurrentRaidMapKey = "arena_bowl";
        h.Run(2);
        h.Env.InStartedRaid = false;
        h.Run(2);
        h.Env.ToastPossible = true;
        h.Env.InStartedRaid = true;
        h.Run(2);
        Assert.Empty(h.Env.Toasts);
    }

    [Fact]
    public void TheNavMeshToastIgnoresShowMapNoticesFalseAndWaitsForTheNotificationManager()
    {
        Harness h = HealthyAfterT1();
        h.Env.NoticesEnabled = false;
        h.Env.ToastPossible = false;
        h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
        h.Env.InStartedRaid = true;
        h.Env.CurrentRaidMapKey = "arena_bowl";
        h.Run(2);
        Assert.Empty(h.Env.Toasts);
        h.Env.ToastPossible = true;
        h.Run(1);
        Assert.Single(h.Env.Toasts);
        Assert.Contains("(the file is damaged)", h.Env.Toasts[0]); // no record: the neutral reason
    }

    // ---- row 22 --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void DamageFoundInARaidThatRanIsReportedAtTheNextMenuOncePerMap()
    {
        Harness h = HealthyAfterT1();
        h.Failures.NoteIntegrity("arena_bay5", "arena_bay5_scenes.bundle", "chunk 3");
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Equal(
            new[] { @"ArenaMapPort: a map file for Bay 5 reported damage during the raid (arena_bay5_scenes.bundle). Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [Bundles]." },
            h.Env.Toasts);

        h.Failures.NoteIntegrity("arena_bay5", "arena_bay5_scenes.bundle", "chunk 9"); // the same map again
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Single(h.Env.Toasts);

        h.Failures.NoteIntegrity("arena_equator", "arena_equator_preset.bundle", "chunk 0"); // another map
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Equal(2, h.Env.Toasts.Count);
        Assert.Contains("Equator", h.Env.Toasts[1]);
        Assert.Empty(h.Env.Modals);
    }

    [Fact]
    public void DamageInsideAFailedAttemptIsTheAttemptsAnswerNotALateToast()
    {
        Harness h = HealthyAfterT1();
        FailedAttempt(h, () => h.Failures.NoteIntegrity("arena_bowl", "arena_bowl_scenes.bundle", "chunk 3"));
        Assert.EndsWith("- ref BUNDLE_BAD", Assert.Single(h.Env.Modals).Text);
        Assert.Empty(h.Env.Toasts);
    }

    [Fact]
    public void DamageInsideASucceededAttemptIsReportedAtTheNextMenu()
    {
        Harness h = HealthyAfterT1();
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Failures.NoteIntegrity("arena_bowl", "arena_bowl_scenes.bundle", "chunk 3");
        h.Env.InStartedRaid = true;
        h.Run(2);
        h.Env.InStartedRaid = false;
        h.Env.MenuIsCurrent = false;
        h.Sched.NoteComeback();
        h.Run(2);
        h.Env.MenuIsCurrent = true;
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Single(h.Env.Toasts);
        Assert.Contains("Bowl", h.Env.Toasts[0]);
        Assert.Empty(h.Env.Modals);
    }

    [Fact]
    public void TheDamageToastWaitsWhileAnAttemptIsOpenAndForTheNotificationManager()
    {
        Harness h = HealthyAfterT1();
        h.Failures.NoteIntegrity("arena_bay5", "f.bundle", "x");
        h.Sched.NoteAttemptStarted("arena_bowl"); // a raid is loading
        h.Sched.OnMenuShown(true);
        h.Run(4);
        Assert.Empty(h.Env.Toasts);
        h.Env.ToastPossible = false; // the raid starts (the attempt closes) while no toast can be shown
        h.Env.InStartedRaid = true;
        h.Run(1);
        h.Env.InStartedRaid = false;
        h.Run(4);
        Assert.Empty(h.Env.Toasts);
        h.Env.ToastPossible = true;
        h.Run(1);
        Assert.Single(h.Env.Toasts);
    }

    // ---- the fault policy ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void APredicateThatThrowsIsNotAFault()
    {
        Harness h = Failing();
        h.Env.MenuPredicateThrows = true;
        h.Env.ModalPredicateThrows = true;
        h.Sched.OnMenuShown(true);
        h.Run(10);
        Assert.Empty(h.Env.Warnings);
        Assert.Empty(h.Env.Modals);
        Assert.False(h.Sched.Disabled);

        h.Env.MenuPredicateThrows = false;
        h.Run(3);
        Assert.Empty(h.Env.Warnings);
        h.Env.ModalPredicateThrows = false;
        h.Run(1);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void ThreeShowFaultsInARowSwitchT1OffWithOneWarningEachUntilTheNextMenuShow()
    {
        Harness h = Failing();
        h.Env.ShowModalThrows = new InvalidOperationException("boom");
        h.FirstMenuAndSettle();
        h.Run(6);

        Assert.True(h.Sched.Disabled);
        Assert.Equal(2, h.Env.Warnings.Count);
        Assert.Equal("[Notice] could not show 'ClientNoArena' (InvalidOperationException: boom); the reason is in the [Arena] lines of this log", h.Env.Warnings[0]);
        Assert.Equal("[Notice] switched off for this session after 3 errors (InvalidOperationException: boom); the maps themselves are not affected", h.Env.Warnings[1]);
        Assert.Empty(h.Env.Modals);
        Assert.Empty(h.Env.Toasts); // switched off: no toast either ...
        Assert.DoesNotContain(h.Env.Warnings, line => line.Contains("shown as a toast")); // ... so the log must not claim one
        Assert.Contains("[Notice] 'ClientNoArena' parked after 3 displaced modal attempts; no toast: the notices are switched off for this session", h.Env.Debugs);

        // the next menu show lifts the latch and re-arms the notice
        h.Env.ShowModalThrows = null;
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.False(h.Sched.Disabled);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void ASuccessfulActionResetsTheFaultCounter()
    {
        Harness h = Failing();
        int calls = 0;
        h.Env.ModalOverride = ticket => ++calls < 3 ? throw new InvalidOperationException("boom") : ticket;
        h.FirstMenuAndSettle();
        h.Run(6);
        Assert.False(h.Sched.Disabled);
        Assert.Single(h.Env.Modals);
        Assert.Equal(1, h.Env.Warnings.Count(line => line.Contains("could not show 'ClientNoArena'"))); // one Warning per notice id
        Assert.DoesNotContain(h.Env.Warnings, line => line.Contains("switched off"));
    }

    [Fact]
    public void ToastFaultsAreCountedToo()
    {
        Harness h = HealthyAfterT1();
        h.Env.ShowToastThrows = new InvalidOperationException("no toast");
        for (int i = 0; i < 3; i++)
        {
            h.Failures.NoteIntegrity("arena_bay5", "f.bundle", "x");
            h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
            h.Env.InStartedRaid = true;
            h.Env.CurrentRaidMapKey = "arena_bowl";
            h.Run(1);
            h.Env.InStartedRaid = false;
            h.Run(0.5);
        }
        Assert.True(h.Sched.Disabled);
        Assert.Contains(h.Env.Warnings, line => line.Contains("switched off for this session after 3 errors"));
    }

    [Fact]
    public void TheLatchNeverStopsAnAnswerToAFailedAttempt()
    {
        Harness h = HealthyAfterT1();
        h.Env.ModalPossible = false; // the answer is due but the window is busy
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "b.bundle", "x");
        h.Sched.NoteComeback();
        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Empty(h.Env.Modals);

        // three toast faults set the latch while the answer waits
        h.Env.ShowToastThrows = new InvalidOperationException("no toast");
        for (int i = 0; i < 3; i++)
        {
            h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
            h.Env.InStartedRaid = true;
            h.Env.CurrentRaidMapKey = "arena_bowl";
            h.Run(0.5);
            h.Env.InStartedRaid = false;
            h.Run(0.5);
        }
        Assert.True(h.Sched.Disabled);

        h.Env.ModalPossible = true;
        h.Run(1);
        Assert.Contains("(b.bundle)", Assert.Single(h.Env.Modals).Text);
    }

    [Fact]
    public void ALoggerThatThrowsCannotBreakTheTick()
    {
        var env = new ThrowingLogEnvironment();
        var sched = new MapNoticeScheduler(env, new MapLoadFailures());
        env.Gate = NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered, "none");
        sched.OnMenuShown(true);
        for (int i = 0; i < 20; i++)
        {
            env.Now += 0.25;
            sched.Tick();
        }
        Assert.Single(env.Modals);
    }

    sealed class ThrowingLogEnvironment : INoticeEnvironment
    {
        internal readonly List<string> Modals = new();
        internal GateSnapshot Gate = NoticeTestData.Verified();
        public double Now { get; set; } = 100;
        public bool NoticesEnabled => true;
        public string Language => "en";
        public string ClientVersion => "0.3.0";
        public IReadOnlyList<MapInfo> Maps => NoticeTestData.Maps;
        public bool MenuIsCurrent => true;
        public bool SelectionIsCurrent => false;
        public bool ModalPossible => true;
        public string ModalBlocker => "";
        public bool MessageWindowOpen => true;
        public bool ToastPossible => true;
        public bool InStartedRaid => false;
        public string CurrentRaidMapKey => null;
        public bool MatchingCanceledByPlayer => false;
        public IReadOnlyList<string> SkippedHooks => new string[0];
        public GateSnapshot ReadGate() => Gate;
        public bool TryReadOffered(out IReadOnlyList<OfferedLocation> locations) { locations = null; return false; }
        public FileScan ScanFiles(IReadOnlyList<MapInfo> scope) => FileScan.Clean();
        public ProbeView Probe => ProbeView.NotAsked;
        public void StartProbe() { }
        public ModalTicket ShowModal(string caption, string text, bool scrolled) { Modals.Add(text); return new ModalTicket(Now); }
        public bool ShowToast(string text) => true;
        public void Debug(string message) => throw new InvalidOperationException("log");
        public void Info(string message) => throw new InvalidOperationException("log");
        public void Warning(string message) => throw new InvalidOperationException("log");
    }

    // ---- the fallbacks and the safety nets of the review fixes ----------------------------------------------------------------------------

    [Fact]
    public void AnAnswerIsReleasedByTheMenuScreenBecomingCurrentWhenNoMenuShowFollowsTheComeback()
    {
        // the fallback of the menu postfix (design section 3.3): MenuScreen.Show may not fire after a failed load; the screen becoming current again does
        Harness h = HealthyAfterT1();
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "arena_bowl_scenes.bundle", "missing");
        h.Env.MenuIsCurrent = false; // the loading screen, then EFT's own popup
        h.Run(3);
        h.Sched.NoteComeback();
        h.Run(3);
        Assert.Empty(h.Env.Modals);
        Assert.True(h.Sched.Work);

        h.Env.MenuIsCurrent = true; // the main menu is back, and no OnMenuShown arrives
        h.Run(1.0);
        Assert.Empty(h.Env.Modals); // the settle time still applies
        h.Run(2.0);
        var modal = Assert.Single(h.Env.Modals);
        Assert.StartsWith("Bowl could not be loaded: one of ArenaMapPort's map files is missing or damaged (arena_bowl_scenes.bundle).", modal.Text);
        Assert.Contains("[Notice] 'BundleBad' released: the main menu is the current screen again and no menu show was seen", h.Env.Debugs);
        Assert.Equal(1, h.Sched.MenuShowCount); // no menu show was invented
    }

    [Fact]
    public void AMenuThatWasCurrentAllAlongIsNotAnEdgeAndAMenuShowStillReleasesTheAnswerOnce()
    {
        Harness h = HealthyAfterT1();
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "b.bundle", "x");
        h.Sched.NoteComeback();
        h.Run(10); // the menu screen is current throughout and no show was seen: nothing to release it
        Assert.Empty(h.Env.Modals);

        h.Sched.OnMenuShown(true);
        h.Run(3);
        Assert.Single(h.Env.Modals);
        Assert.DoesNotContain(h.Env.Debugs, line => line.Contains("released: the main menu is the current screen again"));
    }

    [Fact]
    public void ABlockedModalNamesWhatBlocksIt()
    {
        Harness h = Failing();
        h.Env.ModalPossible = false;
        h.Env.ModalBlocker = "ErrorScreen";
        h.FirstMenuAndSettle();
        h.Run(2);
        Assert.Contains("[Notice] waiting: a free message window (ErrorScreen)", h.Env.Debugs);
        Assert.DoesNotContain("[Notice] waiting: a free message window", h.Env.Debugs); // the bare text is only for an unnamed blocker

        h.Env.ModalBlocker = "MessageWindow";
        h.Run(2);
        Assert.Contains("[Notice] waiting: a free message window (MessageWindow)", h.Env.Debugs);

        h.Run(130);
        Assert.Contains(h.Env.Warnings, line => line.StartsWith("[Notice] shown as a toast after 12") && line.EndsWith(" menu-seconds with the message window busy (MessageWindow)"));
    }

    [Fact]
    public void TheFallbackToastIsLoggedOnlyWhenItWasReallyShown()
    {
        // no notification manager: no toast, no Warning that claims one
        Harness h = Failing();
        h.Env.ModalOverride = _ => ModalTicket.SuppressedAt(h.Env.Now);
        h.Env.ToastPossible = false;
        h.FirstMenuAndSettle();
        h.Run(6.0);
        Assert.Equal(3, h.Env.Modals.Count);
        Assert.Empty(h.Env.Toasts);
        Assert.DoesNotContain(h.Env.Warnings, line => line.Contains("shown as a toast"));
        Assert.Contains("[Notice] 'ClientNoArena' parked after 3 displaced modal attempts; no toast: the notification manager is not available", h.Env.Debugs);

        // EFT dropped the toast (it returns false): the same
        Harness dropped = Failing();
        dropped.Env.ModalOverride = _ => ModalTicket.SuppressedAt(dropped.Env.Now);
        dropped.Env.ToastResult = false;
        dropped.FirstMenuAndSettle();
        dropped.Run(6.0);
        Assert.Single(dropped.Env.Toasts); // it was offered to EFT ...
        Assert.DoesNotContain(dropped.Env.Warnings, line => line.Contains("shown as a toast")); // ... and not claimed as shown
        Assert.Contains("[Notice] 'ClientNoArena' parked after 3 displaced modal attempts; the toast was dropped by EFT", dropped.Env.Debugs);

        // a toast that works is the Warning of the original design
        Harness ok = Failing();
        ok.Env.ModalOverride = _ => ModalTicket.SuppressedAt(ok.Env.Now);
        ok.FirstMenuAndSettle();
        ok.Run(6.0);
        Assert.Contains("[Notice] shown as a toast after 3 displaced modal attempts", ok.Env.Warnings);
    }

    [Fact]
    public void AWindowThatVanishedWithoutAnyCallbackIsTreatedAsClosedAfterAOneSecondGrace()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        h.Run(2.5); // the window has been up for more than two seconds
        Assert.True(h.Sched.ModalOpen);

        h.Env.MessageWindowOpen = false; // no EFT message window is on screen, and none of our callbacks fired
        h.Run(0.5);
        Assert.True(h.Sched.ModalOpen); // the grace period
        h.Run(1.5);
        Assert.False(h.Sched.ModalOpen);
        Assert.True(h.Sched.T1Done); // on screen long enough to count as seen
        Assert.Contains("[Notice] 'ClientNoArena' window is gone without an answer; treated as closed", h.Env.Debugs);
    }

    [Fact]
    public void AWindowThatVanishedRightAfterItWasShownIsDisplacedNotSeenAndTheNextNoticeIsNotBlocked()
    {
        Harness h = Failing();
        h.Sched.OnMenuShown(true);
        h.Run(2.5); // the modal is shown at the ninth tick
        Assert.Single(h.Env.Modals);
        h.Env.MessageWindowOpen = false;
        h.Run(2.0);
        Assert.Contains("[Notice] 'ClientNoArena' displaced (1 of 3)", h.Env.Debugs);
        Assert.False(h.Sched.T1Done);
        Assert.False(h.Sched.ModalOpen && h.Env.Modals.Count == 1); // it is not stuck: the retry follows
        h.Run(2.0);
        Assert.True(h.Env.Modals.Count >= 2);
    }

    [Fact]
    public void AWindowThatIsStillOnScreenIsNeverClosedByTheWatchdog()
    {
        Harness h = Failing();
        h.FirstMenuAndSettle();
        h.Run(60); // MessageWindowOpen stays true: a player reading the notice
        Assert.True(h.Sched.ModalOpen);
        Assert.Single(h.Env.Modals);
        Assert.DoesNotContain(h.Env.Debugs, line => line.Contains("window is gone"));
    }

    [Fact]
    public void AClockThatKeepsThrowingStopsTheTickInsteadOfThrowingEveryFrameAndTheNextMenuShowTriesAgain()
    {
        Harness h = Failing();
        h.Sched.OnMenuShown(true);
        h.Env.NowThrows = true;
        h.Run(1.0);

        Assert.False(h.Sched.Work); // stopped: the tick is the one flag read again
        Assert.Equal(1, h.Env.Warnings.Count(line => line.Contains("the 'clock' step failed")));
        Assert.Contains(h.Env.Warnings, line => line == "[Notice] the 'clock' step is stopped after 3 errors in a row, until the next main menu");
        int reads = h.Env.NowReads;
        h.Run(10.0);
        Assert.Equal(reads, h.Env.NowReads); // not one more call into the failing clock

        h.Env.NowThrows = false;
        h.Sched.OnMenuShown(true); // a menu show gives the step another chance
        h.Run(3.0);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void TheDamageCheckDoesNotKeepTheTickAliveThroughARaidThatBeganBeforeItRanButTheNextMenuShowRunsIt()
    {
        Harness h = HealthyAfterT1();
        Assert.False(h.Sched.Work);

        h.Sched.OnMenuShown(true); // back from the hideout ...
        h.Env.MenuIsCurrent = false; // ... and straight into a raid, before the settle time passed
        h.Run(3.0);
        Assert.False(h.Sched.Work); // the raid does not cost a tick per frame for a check that cannot run

        h.Failures.NoteIntegrity("arena_bowl", "arena_bowl_scenes.bundle", "a chunk failed"); // found during the raid
        h.Env.MenuIsCurrent = true;
        h.Sched.OnMenuShown(true);
        h.Run(3.0);
        Assert.Equal(new[] { @"ArenaMapPort: a map file for Bowl reported damage during the raid (arena_bowl_scenes.bundle). Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [Bundles]." },
            h.Env.Toasts);
    }

    [Fact]
    public void ADamageRecordedWhileIdleDuringARaidIsShownAtTheNextMenuEvenWithoutACountedMenuShow()
    {
        Harness h = HealthyAfterT1();
        Assert.False(h.Sched.Work);

        h.Env.MenuIsCurrent = false; // a raid
        h.Failures.NoteIntegrity("arena_bowl", "arena_bowl_scenes.bundle", "a chunk failed");
        h.Sched.NoteIntegrityRecorded(); // what the glue's Noted callback calls for an Integrity record
        Assert.True(h.Sched.Work);
        h.Run(5.0);
        Assert.True(h.Sched.Work);

        h.Env.MenuIsCurrent = true; // back at the menu and no OnMenuShown was counted (the postfix may not fire)
        h.Run(3.0);
        Assert.Single(h.Env.Toasts);
        Assert.False(h.Sched.Work);
    }

    [Fact]
    public void ADamageRecordKeepsTheTickAliveUntilTheMenuIsBack()
    {
        Harness h = HealthyAfterT1();
        h.Sched.OnMenuShown(true);
        h.Failures.NoteIntegrity("arena_bowl", "arena_bowl_scenes.bundle", "a chunk failed");
        h.Env.MenuIsCurrent = false;
        h.Run(3.0);
        Assert.True(h.Sched.Work);
        h.Env.MenuIsCurrent = true;
        h.Run(3.0);
        Assert.Single(h.Env.Toasts);
        Assert.False(h.Sched.Work);
    }

    [Fact]
    public void ARecordOfARaidWithoutNavMeshDoesNotOutliveTheComebackOfARaidThatNeverStarted()
    {
        // the scene-unload hook that normally clears it is registered only once a NavMesh was installed, so the comeback clears it too
        Harness h = HealthyAfterT1();
        h.Env.MatchingCanceledByPlayer = true; // no answer modal: this test is about the record
        h.Sched.NoteAttemptStarted("arena_bowl");
        h.Sched.NoteRaidWithoutNavMesh("arena_bowl");
        h.Env.MenuIsCurrent = false;
        h.Run(2.0);
        Assert.True(h.Sched.Work);

        h.Sched.NoteComeback(); // the raid aborted before it started
        h.Env.MenuIsCurrent = true;
        h.Sched.OnMenuShown(true);
        h.Run(4.0);
        Assert.False(h.Sched.Work);

        h.Env.InStartedRaid = true; // a later raid of the same map must not get the old record's toast
        h.Env.CurrentRaidMapKey = "arena_bowl";
        h.Run(2.0);
        Assert.Empty(h.Env.Toasts);
    }

    // ---- the degraded menu poll ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutTheMenuPostfixTheIdleTickPollsTheMenuScreenOncePerSecond()
    {
        var h = new Harness();
        h.Env.Gate = NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered, "none");
        h.Sched.MenuPostfixInstalled = false;
        h.Env.MenuIsCurrent = false; // loading screens, the login screen

        h.Run(5.0, 0.1);
        Assert.InRange(h.Env.MenuReads, 4, 6); // about one per second, not one per frame (50 ticks)
        Assert.Equal(0, h.Sched.MenuShowCount);

        h.Env.MenuIsCurrent = true; // the rising edge counts as a menu show
        h.Run(1.5, 0.1);
        Assert.Equal(1, h.Sched.MenuShowCount);
        h.Run(4.0, 0.1);
        Assert.Single(h.Env.Modals);
    }

    [Fact]
    public void WithoutTheMenuPostfixEachReturnToTheMenuIsAMenuShow()
    {
        var h = new Harness();
        h.Sched.MenuPostfixInstalled = false;
        h.Env.MenuIsCurrent = true;
        h.Run(2.0);
        Assert.Equal(1, h.Sched.MenuShowCount);
        h.Env.MenuIsCurrent = false;
        h.Run(2.0);
        h.Env.MenuIsCurrent = true;
        h.Run(2.0);
        Assert.Equal(2, h.Sched.MenuShowCount);
    }

    [Fact]
    public void WithTheMenuPostfixInstalledTheIdleTickNeverPolls()
    {
        var h = new Harness();
        h.Env.MenuIsCurrent = true;
        h.Run(5.0, 0.1);
        Assert.Equal(0, h.Env.MenuReads);
        Assert.Equal(0, h.Sched.MenuShowCount);
    }

    // ---- the ticket ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFirstAnswerOfATicketWins()
    {
        var accepted = new ModalTicket(10);
        Assert.Equal(ModalOutcome.Pending, accepted.Outcome);
        accepted.Accept(12);
        accepted.Close(13);
        Assert.Equal(ModalOutcome.Accepted, accepted.Outcome);
        Assert.Equal(12, accepted.ClosedAt);
        Assert.Equal(10, accepted.ShownAt);

        var closed = new ModalTicket(10);
        closed.Close(11);
        closed.Accept(12);
        Assert.Equal(ModalOutcome.Closed, closed.Outcome);
        Assert.Equal(11, closed.ClosedAt);
    }

    [Fact]
    public void SuppressedAndFailedTicketsAreAlreadyAnswered()
    {
        ModalTicket suppressed = ModalTicket.SuppressedAt(5);
        Assert.Equal(ModalOutcome.Suppressed, suppressed.Outcome);
        suppressed.Accept(6);
        Assert.Equal(ModalOutcome.Suppressed, suppressed.Outcome);

        ModalTicket failed = ModalTicket.FailedAt(5, "boom");
        Assert.Equal(ModalOutcome.Failed, failed.Outcome);
        Assert.Equal("boom", failed.Error);
    }

    [Fact]
    public void AllConstantsMatchTheDesign()
    {
        Assert.Equal(2.0, MapNoticeScheduler.SettleSeconds);
        Assert.Equal(30.0, MapNoticeScheduler.LocationsWait);
        Assert.Equal(10.0, MapNoticeScheduler.PendingToastAfter);
        Assert.Equal(5.0, MapNoticeScheduler.ProbeTimeout);
        Assert.Equal(2.0, MapNoticeScheduler.SeenAfter);
        Assert.Equal(3, MapNoticeScheduler.MaxModalAttempts);
        Assert.Equal(120.0, MapNoticeScheduler.BlockedBudget);
        Assert.Equal(3, MapNoticeScheduler.MaxConsecutiveFaults);
        Assert.Equal("[Notice]", MapNoticeScheduler.Tag);
    }
}
