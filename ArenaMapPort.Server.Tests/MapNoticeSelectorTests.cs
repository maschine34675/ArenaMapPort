#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// MapNoticeSelector: the decision table of the design, rows 1 to 23, in priority order: the client's own check, a skipped hook, the server's reason
/// (none offered, some missing), a missing client file; the answers to a failed raid attempt (gate refusal, the six key texts, bundle problems, no cause);
/// the toasts; ShowMapNotices=false; "nothing to say" (no notice, no request).
/// </summary>
public sealed class MapNoticeSelectorTests
{
    static MapNoticePlan Menu(MapAvailabilityInput input) => MapNoticeSelector.SelectMenu(input);

    static FileScan Missing(params string[] files) => new(4, files, new[] { "Equator" });

    // ---- nothing to say -----------------------------------------------------------------------------------------------

    [Fact]
    public void AHealthyClientHasNothingToSayAndAsksNoOne()
    {
        MapAvailabilityInput input = NoticeTestData.Healthy();
        Assert.Null(Menu(input));
        Assert.False(MapNoticeInputs.NeedsProbe(input));
    }

    [Fact]
    public void AVerifiedClientWithAnUnknownLocationListAndCompleteFilesHasNothingToSay()
    {
        Assert.Null(Menu(NoticeTestData.Input(NoticeTestData.Verified(), null, null, FileScan.Clean(4))));
    }

    [Fact]
    public void AStillRunningCheckSaysNothingYet()
    {
        Assert.Null(Menu(NoticeTestData.Input(NoticeTestData.State(GateState.Running), new List<OfferedLocation>(), null, null, new[] { "BundleHost" })));
    }

    [Fact]
    public void ShowMapNoticesFalseSilencesEveryMenuNotice()
    {
        var gates = new[]
        {
            NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered), NoticeTestData.State(GateState.TimedOut), NoticeTestData.State(GateState.NotStarted),
        };
        foreach (GateSnapshot gate in gates)
            Assert.Null(Menu(NoticeTestData.Input(gate, null, enabled: false)));
        Assert.Null(Menu(NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.AllOffered(), null, Missing("x"), enabled: false)));
        Assert.Null(Menu(NoticeTestData.Input(NoticeTestData.Verified(), new List<OfferedLocation>(), NoticeTestData.Ok(NoticeTestData.Status("not_verified", "not_registered")), enabled: false)));
        Assert.Null(MapNoticeSelector.SelectMenu(null));
    }

    // ---- rows 1 to 6 and 8: the client's own check -----------------------------------------------------------------------

    [Fact]
    public void Row1NoArenaOnThisPc()
    {
        MapNoticePlan plan = Menu(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered, "none"), null));
        Assert.Equal(NoticeId.ClientNoArena, plan.Id);
        Assert.Equal(NoticeLead.Menu, plan.Lead);
        Assert.Equal(ReminderGroup.ArenaNotFound, plan.Reminder);
        Assert.Equal("client=not_verified:not_registered server=not_needed offered=?/4", plan.LogSummary);
        Assert.Equal("0.3.0", plan.Get("ver"));
    }

    [Fact]
    public void Row2RegistryUnreadable()
    {
        MapNoticePlan plan = Menu(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.RegistryUnavailable, "none"), null));
        Assert.Equal(NoticeId.ClientRegistryUnreadable, plan.Id);
        Assert.Equal(ReminderGroup.ArenaNotFound, plan.Reminder);
    }

    [Fact]
    public void Row3TheSettingIsBadAndRow4TheLaunchersFolderIsBad()
    {
        MapNoticePlan overridePlan = Menu(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.FolderMissing, "override", @"D:\Nope"), null));
        Assert.Equal(NoticeId.ClientOverrideBad, overridePlan.Id);
        Assert.Equal(NoticeWhy.FolderMissing, overridePlan.Why);

        MapNoticePlan registryPlan = Menu(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.NotArenaFolder, "registry", @"D:\Moved"), null));
        Assert.Equal(NoticeId.ClientFolderBad, registryPlan.Id);
        Assert.Equal(NoticeWhy.ExeMissing, registryPlan.Why);
        Assert.Equal(@"D:\Moved", registryPlan.Get("path"));
        Assert.Equal(ReminderGroup.ClientUnusable, registryPlan.Reminder);
    }

    [Fact]
    public void Row5FilesMismatchCarriesThePathAndTheCount()
    {
        foreach (string source in new[] { "override", "registry" })
        {
            MapNoticePlan plan = Menu(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.FilesMismatch, source, @"D:\Arena", 4321), null));
            Assert.Equal(NoticeId.ClientIncomplete, plan.Id);
            Assert.Equal(@"D:\Arena", plan.Get("path"));
            Assert.Equal("4321", plan.Get("n"));
        }
    }

    [Fact]
    public void Row6AnInternalErrorOrAnUnstartedCheckCarriesADetail()
    {
        var thrown = new GateSnapshot(GateState.CheckFailed, ArenaInstallCause.None, null, null, null, 0, null, "InvalidOperationException: boom");
        MapNoticePlan plan = Menu(NoticeTestData.Input(thrown, null));
        Assert.Equal(NoticeId.ClientCheckFailed, plan.Id);
        Assert.Equal("InvalidOperationException: boom", plan.Get("detail"));

        MapNoticePlan notStarted = Menu(NoticeTestData.Input(NoticeTestData.State(GateState.NotStarted), null));
        Assert.Equal(NoticeId.ClientCheckFailed, notStarted.Id);
        Assert.Equal("the check did not run", notStarted.Get("detail"));

        MapNoticePlan detected = Menu(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.CheckFailed, "none", null, 0, "Verifying failed unexpectedly: x"), null));
        Assert.Equal("Verifying failed unexpectedly: x", detected.Get("detail"));
    }

    [Fact]
    public void Row8ATimedOutCheckSaysItIsSlow()
    {
        MapNoticePlan plan = Menu(NoticeTestData.Input(NoticeTestData.State(GateState.TimedOut), null));
        Assert.Equal(NoticeId.ClientCheckSlow, plan.Id);
        Assert.Equal(ReminderGroup.ClientCheck, plan.Reminder); // repairing Arena does not help a slow check
    }

    [Fact]
    public void AnUnverifiedClientShowsItsOwnNoticeEvenWhenTheServerRegisteredTheMaps()
    {
        // every registry map offered, yet this client cannot serve them: the selection would show maps that fail 14 s into a load
        MapNoticePlan plan = Menu(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered), NoticeTestData.AllOffered()));
        Assert.Equal(NoticeId.ClientNoArena, plan.Id);
    }

    // ---- row 9: a skipped hook ------------------------------------------------------------------------------------------------

    [Fact]
    public void Row9ASkippedMapCriticalHookNamesThePart()
    {
        MapNoticePlan plan = Menu(NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.AllOffered(), null, null, new[] { "BundlesManager.LoadBundleAsync", "BotsController.Init" }));
        Assert.Equal(NoticeId.ClientHookMissing, plan.Id);
        Assert.Equal("BundlesManager.LoadBundleAsync", plan.Get("detail"));
        Assert.Equal(ReminderGroup.ClientBuild, plan.Reminder); // the remedy is another ArenaMapPort build, not a repaired Arena
    }

    // ---- rows 10 to 13: nothing offered -----------------------------------------------------------------------------------------

    static MapNoticePlan NoneOffered(ProbeView probe) =>
        Menu(NoticeTestData.Input(NoticeTestData.Verified(), new List<OfferedLocation>(), probe));

    [Fact]
    public void Row10TheServerCouldNotVerifyArena()
    {
        foreach (string state in new[] { "not_verified", "error" })
        {
            MapNoticePlan plan = NoneOffered(NoticeTestData.Ok(NoticeTestData.Status(state, "not_registered")));
            Assert.Equal(NoticeId.ServerNoArena, plan.Id);
            // the gate state "error" is an unexpected failure whatever the cause: a restart, not an arena-install.json
            Assert.Equal(state == "error" ? ReminderGroup.ServerCheck : ReminderGroup.ServerNoVerify, plan.Reminder);
            Assert.Equal("0.3.0", plan.ServerVersion);
            Assert.Equal("{\"installPath\": \"D:\\\\Battlestate Games\\\\Escape from Tarkov Arena\"}", plan.Get("json"));
        }
    }

    [Theory]
    [InlineData("not_registered", "absent", "none", NoticeWhy.ServerNotRegistered, NoticeAction.CreateOverride)]
    [InlineData("registry_unreadable", "absent", "none", NoticeWhy.ServerRegistryUnreadable, NoticeAction.CreateOverride)]
    [InlineData("not_registered", "ignored", "none", NoticeWhy.ServerOverrideIgnored, NoticeAction.FixIgnored)]
    [InlineData("registry_unreadable", "ignored", "none", NoticeWhy.ServerOverrideIgnored, NoticeAction.FixIgnored)]
    [InlineData("folder_missing", "used", "override", NoticeWhy.ServerOverrideFolderMissing, NoticeAction.FixOverridePath)]
    [InlineData("not_arena_folder", "used", "override", NoticeWhy.ServerOverrideNotArena, NoticeAction.FixOverridePath)]
    [InlineData("manifest_missing", "used", "override", NoticeWhy.ServerOverrideManifest, NoticeAction.FixOverridePath)]
    [InlineData("manifest_invalid", "used", "override", NoticeWhy.ServerOverrideManifest, NoticeAction.FixOverridePath)]
    [InlineData("folder_missing", "absent", "registry", NoticeWhy.ServerFolderBad, NoticeAction.FixServerFolder)]
    [InlineData("not_arena_folder", "ignored", "registry", NoticeWhy.ServerFolderBad, NoticeAction.FixServerFolder)]
    [InlineData("files_mismatch", "absent", "registry", NoticeWhy.ServerFilesMismatch, NoticeAction.FilesMismatch)]
    [InlineData("files_mismatch", "used", "override", NoticeWhy.ServerFilesMismatch, NoticeAction.FilesMismatch)]
    [InlineData("check_failed", "absent", "none", NoticeWhy.ServerCheckFailed, NoticeAction.CheckFailed)]
    [InlineData("none", "absent", "none", NoticeWhy.ServerCheckFailed, NoticeAction.CheckFailed)]
    [InlineData("a_cause_from_the_future", "absent", "none", NoticeWhy.ServerCheckFailed, NoticeAction.CheckFailed)]
    public void TheServerNoArenaReasonAndActionFollowTheServersCauseAndOverrideFile(string cause, string overrideFile, string source, NoticeWhy why, NoticeAction action)
    {
        var status = NoticeTestData.Status("not_verified", cause, source, overrideFile);
        MapNoticePlan plan = NoneOffered(NoticeTestData.Ok(status));
        Assert.Equal(NoticeId.ServerNoArena, plan.Id);
        Assert.Equal(why, plan.Why);
        Assert.Equal(action, plan.Action);
        // the selection reminder carries the remedy of this very cause
        ReminderGroup expected = action == NoticeAction.FilesMismatch || action == NoticeAction.FixServerFolder ? ReminderGroup.ServerArena
            : action == NoticeAction.CheckFailed ? ReminderGroup.ServerCheck : ReminderGroup.ServerNoVerify;
        Assert.Equal(expected, plan.Reminder);
    }

    [Fact]
    public void AServerGateErrorIsAnUnexpectedFailureWhateverTheCause()
    {
        var status = NoticeTestData.Status("error", "not_registered");
        MapNoticePlan plan = NoneOffered(NoticeTestData.Ok(status));
        Assert.Equal(NoticeWhy.ServerCheckFailed, plan.Why);
        Assert.Equal(NoticeAction.CheckFailed, plan.Action);
    }

    [Fact]
    public void Row11TheServerVerifiedButEveryMapFailed()
    {
        var dataBad = NoticeTestData.Status("verified", "none", maps: new[]
        {
            ("arena_equator", "failed", "base_json_parse"), ("arena_bay5", "failed", "base_json_missing"),
            ("arena_airpit", "failed", "base_json_parse"), ("arena_bowl", "failed", "base_json_missing"),
        });
        MapNoticePlan plan = NoneOffered(NoticeTestData.Ok(dataBad));
        Assert.Equal(NoticeId.ServerAllFailed, plan.Id);
        Assert.Equal(NoticeWhy.MapDataBad, plan.Why);
        Assert.Equal(ReminderGroup.ServerNoOffer, plan.Reminder);

        var registration = NoticeTestData.Status("verified", "none", maps: NoticeTestData.MapsAll("failed", "registration"));
        Assert.Equal(NoticeWhy.RegistrationFailed, NoneOffered(NoticeTestData.Ok(registration)).Why);

        var mixed = NoticeTestData.Status("verified", "none", maps: new[]
        {
            ("arena_equator", "failed", "base_json_parse"), ("arena_bay5", "failed", "registration"),
            ("arena_airpit", "failed", "base_json_parse"), ("arena_bowl", "failed", "base_json_parse"),
        });
        Assert.Equal(NoticeWhy.RegistrationFailed, NoneOffered(NoticeTestData.Ok(mixed)).Why);
    }

    [Fact]
    public void Row12AServerThatCannotSayWhyIsUnknown()
    {
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(new ProbeView(ProbeState.NotFound)).Id);
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(new ProbeView(ProbeState.Unavailable)).Id);
        // an answer this client cannot read (another schema) is a server that did not say why, not one that did not answer
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(new ProbeView(ProbeState.Unreadable)).Id);
        Assert.Equal(ReminderGroup.ServerNoOffer, NoneOffered(new ProbeView(ProbeState.Unreadable)).Reminder);
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(NoticeTestData.Ok(NoticeTestData.Status("not_run", "none"))).Id);
        Assert.Equal(ReminderGroup.ServerNoOffer, NoneOffered(new ProbeView(ProbeState.NotFound)).Reminder);

        // probe ok but inconsistent: verified, yet nothing failed (and nothing was offered)
        var nothingFailed = NoticeTestData.Status("verified", "none", maps: NoticeTestData.MapsAll("registered", "none"));
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(NoticeTestData.Ok(nothingFailed)).Id);
        var notRegistered = NoticeTestData.Status("verified", "none", maps: NoticeTestData.MapsAll("not_registered", "none"));
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(NoticeTestData.Ok(notRegistered)).Id);
        // ... or the list does not mention a registry map, or is empty
        var partialList = NoticeTestData.Status("verified", "none", maps: new[] { ("arena_equator", "failed", "registration") });
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(NoticeTestData.Ok(partialList)).Id);
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(NoticeTestData.Ok(NoticeTestData.Status("verified", "none"))).Id);
        // ... or a gate state this client does not know
        Assert.Equal(NoticeId.ServerUnknown, NoneOffered(NoticeTestData.Ok(NoticeTestData.Status("something_new", "none"))).Id);
    }

    [Fact]
    public void Row13ATimedOutOrFailedProbeIsNoAnswer()
    {
        foreach (ProbeState state in new[] { ProbeState.Failed, ProbeState.Pending, ProbeState.NotAsked })
        {
            MapNoticePlan plan = NoneOffered(new ProbeView(state));
            Assert.Equal(NoticeId.ServerNoAnswer, plan.Id);
            Assert.Equal(ReminderGroup.ServerNoAnswer, plan.Reminder); // a restart, not a reinstall
            Assert.Null(plan.ServerVersion);
        }
    }

    // ---- rows 14 and 15: some offered -----------------------------------------------------------------------------------------

    static MapNoticePlan SomeOffered(ProbeView probe, params string[] offered) =>
        Menu(NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.Offered(offered), probe));

    [Fact]
    public void Row14SomeMapsFailedToRegister()
    {
        var status = NoticeTestData.Status("verified", "none", maps: new[]
        {
            ("arena_equator", "registered", "none"), ("arena_bay5", "failed", "base_json_parse"),
            ("arena_airpit", "failed", "base_json_missing"), ("arena_bowl", "registered", "none"),
        });
        MapNoticePlan plan = SomeOffered(NoticeTestData.Ok(status), "arena_equator", "arena_bowl");
        Assert.Equal(NoticeId.ServerSomeFailed, plan.Id);
        Assert.Equal("Bay 5, Air Pit", plan.Get("maps"));
        Assert.Equal(NoticeWhy.MapDataBad, plan.Why);
        Assert.Equal("0.3.0", plan.ServerVersion);
        Assert.Equal(ReminderGroup.None, plan.Reminder); // rows 14 to 16 have no selection reminder

        var registration = NoticeTestData.Status("verified", "none", maps: new[]
        {
            ("arena_equator", "registered", "none"), ("arena_bay5", "registered", "none"),
            ("arena_airpit", "registered", "none"), ("arena_bowl", "failed", "registration"),
        });
        MapNoticePlan one = SomeOffered(NoticeTestData.Ok(registration), "arena_equator", "arena_bay5", "arena_airpit");
        Assert.Equal("Bowl", one.Get("maps"));
        Assert.Equal(NoticeWhy.RegistrationFailed, one.Why);
    }

    [Fact]
    public void Row15VersionSkewAndEveryOtherCauseNameTheMissingMaps()
    {
        // an older server: no route (404), a timeout, an unreadable answer, or an answer that lists the missing map as not registered
        foreach (ProbeView probe in new[]
                 {
                     new ProbeView(ProbeState.NotFound), new ProbeView(ProbeState.Failed), new ProbeView(ProbeState.Unavailable), new ProbeView(ProbeState.NotAsked),
                     NoticeTestData.Ok(NoticeTestData.Status("verified", "none", maps: new[] { ("arena_equator", "registered", "none"), ("arena_bay5", "registered", "none"), ("arena_airpit", "registered", "none") })),
                     NoticeTestData.Ok(NoticeTestData.Status("verified", "none", maps: new[] { ("arena_bowl", "not_registered", "none") })),
                 })
        {
            MapNoticePlan plan = SomeOffered(probe, "arena_equator", "arena_bay5", "arena_airpit");
            Assert.Equal(NoticeId.ServerSomeMissing, plan.Id);
            Assert.Equal("Bowl", plan.Get("maps"));
            Assert.Equal(ReminderGroup.None, plan.Reminder);
        }

        MapNoticePlan two = SomeOffered(new ProbeView(ProbeState.NotFound), "arena_equator", "arena_bay5");
        Assert.Equal("Air Pit, Bowl", two.Get("maps"));
        Assert.Equal("0.3.0", two.Get("ver"));
    }

    [Fact]
    public void AFailedMapThatIsOfferedNeverCountsAsMissing()
    {
        // the server says Bowl failed, but the backend lists it (an old record): the missing map is Air Pit and the server never failed it
        var status = NoticeTestData.Status("verified", "none", maps: new[] { ("arena_bowl", "failed", "registration") });
        MapNoticePlan plan = SomeOffered(NoticeTestData.Ok(status), "arena_equator", "arena_bay5", "arena_bowl");
        Assert.Equal(NoticeId.ServerSomeMissing, plan.Id);
        Assert.Equal("Air Pit", plan.Get("maps"));
    }

    // ---- row 16: files ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Row16ClientFilesAreMissing()
    {
        var scan = new FileScan(4, new[] { "arena_bay5_scenes.bundle", "arena_bowl.json" }, new[] { "Bay 5", "Bowl" });
        MapNoticePlan plan = Menu(NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.AllOffered(), null, scan));
        Assert.Equal(NoticeId.InstallIncomplete, plan.Id);
        Assert.Equal("Bay 5, Bowl", plan.Get("maps"));
        Assert.Equal("2", plan.Get("n"));
        Assert.Equal("arena_bay5_scenes.bundle", plan.Get("file"));
        Assert.Equal(ReminderGroup.None, plan.Reminder);
    }

    [Fact]
    public void Row16AlsoAppliesWhenTheOfferedListIsUnknown()
    {
        MapNoticePlan plan = Menu(NoticeTestData.Input(NoticeTestData.Verified(), null, null, Missing("arena_equator_navmesh.bundle")));
        Assert.Equal(NoticeId.InstallIncomplete, plan.Id);
    }

    // ---- priorities -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFirstMatchingRowWinsInTheOrderClientHookServerFiles()
    {
        // everything is wrong: hook skipped, nothing offered, server says no, files missing
        var hooks = new[] { "BundlesManager.LoadBundleAsync" };
        ProbeView probe = NoticeTestData.Ok(NoticeTestData.Status("not_verified", "not_registered"));
        MapAvailabilityInput all = NoticeTestData.Input(NoticeTestData.Verified(), new List<OfferedLocation>(), probe, Missing("x"), hooks);
        Assert.Equal(NoticeId.ClientHookMissing, Menu(all).Id);

        MapAvailabilityInput noHook = NoticeTestData.Input(NoticeTestData.Verified(), new List<OfferedLocation>(), probe, Missing("x"));
        Assert.Equal(NoticeId.ServerNoArena, Menu(noHook).Id);

        MapAvailabilityInput filesOnly = NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.AllOffered(), null, Missing("x"));
        Assert.Equal(NoticeId.InstallIncomplete, Menu(filesOnly).Id);

        // the client's own check beats all of it
        MapAvailabilityInput clientWins = NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.FilesMismatch), new List<OfferedLocation>(), probe, Missing("x"), hooks);
        Assert.Equal(NoticeId.ClientIncomplete, Menu(clientWins).Id);
    }

    [Fact]
    public void ThePlanOfEveryMenuNoticeCarriesTheVersionTheLeadAndTheLogSummary()
    {
        var inputs = new[]
        {
            NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered), null),
            NoticeTestData.Input(NoticeTestData.Verified(), new List<OfferedLocation>(), new ProbeView(ProbeState.NotFound)),
            NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.AllOffered(), null, Missing("x")),
        };
        foreach (MapAvailabilityInput input in inputs)
        {
            MapNoticePlan plan = Menu(input);
            Assert.Equal(NoticeLead.Menu, plan.Lead);
            Assert.Equal("0.3.0", plan.Get("ver"));
            Assert.StartsWith("client=", plan.LogSummary);
            Assert.Equal(NoticeKind.Modal, plan.Kind);
            // every plan composes in both languages without a leftover token
            foreach (string language in new[] { "en", "de" })
                Assert.DoesNotMatch(@"\{[A-Za-z][A-Za-z0-9]*\}", MapNoticeText.Compose(plan, language).Text);
        }
    }

    // ---- the reminder groups ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(NoticeId.ClientNoArena, ReminderGroup.ArenaNotFound)]
    [InlineData(NoticeId.ClientRegistryUnreadable, ReminderGroup.ArenaNotFound)]
    [InlineData(NoticeId.ClientOverrideBad, ReminderGroup.ClientUnusable)]
    [InlineData(NoticeId.ClientFolderBad, ReminderGroup.ClientUnusable)]
    [InlineData(NoticeId.ClientIncomplete, ReminderGroup.ClientUnusable)]
    [InlineData(NoticeId.ClientCheckFailed, ReminderGroup.ClientCheck)]
    [InlineData(NoticeId.ClientCheckSlow, ReminderGroup.ClientCheck)]
    [InlineData(NoticeId.ClientHookMissing, ReminderGroup.ClientBuild)]
    [InlineData(NoticeId.ServerNoArena, ReminderGroup.ServerNoVerify)]
    [InlineData(NoticeId.ServerAllFailed, ReminderGroup.ServerNoOffer)]
    [InlineData(NoticeId.ServerUnknown, ReminderGroup.ServerNoOffer)]
    [InlineData(NoticeId.ServerNoAnswer, ReminderGroup.ServerNoAnswer)]
    [InlineData(NoticeId.ServerSomeFailed, ReminderGroup.None)]
    [InlineData(NoticeId.ServerSomeMissing, ReminderGroup.None)]
    [InlineData(NoticeId.InstallIncomplete, ReminderGroup.None)]
    [InlineData(NoticeId.KeyUpdated, ReminderGroup.None)]
    [InlineData(NoticeId.BundleBad, ReminderGroup.None)]
    public void TheReminderGroupFollowsTheDesignsCauseTable(NoticeId id, ReminderGroup group)
    {
        Assert.Equal(group, MapNoticeSelector.ReminderOf(id));
    }

    [Theory]
    [InlineData(NoticeAction.CreateOverride, ReminderGroup.ServerNoVerify)]
    [InlineData(NoticeAction.FixIgnored, ReminderGroup.ServerNoVerify)]
    [InlineData(NoticeAction.FixOverridePath, ReminderGroup.ServerNoVerify)]
    [InlineData(NoticeAction.FixServerFolder, ReminderGroup.ServerArena)]
    [InlineData(NoticeAction.FilesMismatch, ReminderGroup.ServerArena)]
    [InlineData(NoticeAction.CheckFailed, ReminderGroup.ServerCheck)]
    public void TheServerNoArenaReminderFollowsItsAction(NoticeAction action, ReminderGroup group)
    {
        Assert.Equal(group, MapNoticeSelector.ReminderOf(NoticeId.ServerNoArena, action));
    }

    [Fact]
    public void EveryReminderGroupHasADistinctRemedyInBothLanguages()
    {
        // one fix per group: no two groups may share their fix sentence, or a cause would be sent to another cause's remedy
        foreach (string language in new[] { "en", "de" })
        {
            var fixes = new HashSet<string>();
            foreach (ReminderGroup group in Enum.GetValues<ReminderGroup>().Where(g => g != ReminderGroup.None))
                Assert.True(fixes.Add(MapNoticeText.ComposeReminder(group, language).Text), group + " repeats another group's text in " + language);
        }
    }

    // ---- rows 17 to 20: a failed attempt -----------------------------------------------------------------------------------------------

    static AttemptFailureInput Attempt(IEnumerable<MapLoadFailure> records, GateSnapshot gate = null, bool canceled = false, string key = "arena_bowl", string name = "Bowl") =>
        new(key, name, records.ToList(), gate ?? NoticeTestData.Verified(), canceled, false, "0.3.0");

    static MapLoadFailure Record(long sequence, MapLoadFailureKind kind, string file = "arena_bowl_scenes.bundle") =>
        new() { Sequence = sequence, MapKey = "arena_bowl", Kind = kind, File = file };

    [Fact]
    public void Row17AGateRefusalBecomesTheMatchingClientTextWithTheMapLead()
    {
        var cases = new (GateSnapshot Gate, NoticeId Expected)[]
        {
            (NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered, "none"), NoticeId.ClientNoArena),
            (NoticeTestData.NotVerified(ArenaInstallCause.RegistryUnavailable, "none"), NoticeId.ClientRegistryUnreadable),
            (NoticeTestData.NotVerified(ArenaInstallCause.FolderMissing, "override", @"D:\x"), NoticeId.ClientOverrideBad),
            (NoticeTestData.NotVerified(ArenaInstallCause.NotArenaFolder, "registry", @"D:\x"), NoticeId.ClientFolderBad),
            (NoticeTestData.NotVerified(ArenaInstallCause.FilesMismatch, "registry", @"D:\x", 7), NoticeId.ClientIncomplete),
            (NoticeTestData.State(GateState.CheckFailed, "boom"), NoticeId.ClientCheckFailed),
            (NoticeTestData.State(GateState.NotStarted), NoticeId.ClientCheckFailed),
            (NoticeTestData.State(GateState.TimedOut), NoticeId.ClientCheckSlow),
            (NoticeTestData.State(GateState.Running), NoticeId.ClientCheckSlow),
            (NoticeTestData.Verified(), NoticeId.ClientCheckSlow), // the check finished after the refusal
        };
        foreach (var (gate, expected) in cases)
        {
            MapNoticePlan plan = MapNoticeSelector.SelectAttempt(Attempt(new[] { Record(5, MapLoadFailureKind.GateRefused) }, gate));
            Assert.Equal(expected, plan.Id);
            Assert.Equal(NoticeLead.Attempt, plan.Lead);
            Assert.Equal("Bowl", plan.Get("Map"));
            Assert.Equal("0.3.0", plan.Get("ver"));
            Assert.StartsWith("Bowl could not be loaded: ", MapNoticeText.Compose(plan, "en").Text);
        }
    }

    [Theory]
    [InlineData(KeyFailure.SizeDiffers, "0.6.1.0.48206", "0.6.0.0.47752", NoticeId.KeyUpdated)]
    [InlineData(KeyFailure.ContentDiffers, "0.6.1.0.48206", "0.6.0.0.47752", NoticeId.KeyUpdated)]
    [InlineData(KeyFailure.SizeDiffers, "0.6.0.0.47752", "0.6.1.0.48206", NoticeId.KeyArenaOlder)]
    [InlineData(KeyFailure.ContentDiffers, "0.5.4.0.1", "0.6.0.0.47752", NoticeId.KeyArenaOlder)]
    [InlineData(KeyFailure.SizeDiffers, "0.6.1.0.48206", "0.6.1.0.48206", NoticeId.KeyDiffers)]
    [InlineData(KeyFailure.ContentDiffers, "0.6.1.0.48206", "0.6.1.0.48206", NoticeId.KeyDiffers)]
    [InlineData(KeyFailure.ContentDiffers, null, "0.6.1.0.48206", NoticeId.KeyDiffers)]
    [InlineData(KeyFailure.ContentDiffers, "0.6.1.0.48206", null, NoticeId.KeyDiffers)]
    [InlineData(KeyFailure.ContentDiffers, "garbage", "0.6.1.0.48206", NoticeId.KeyDiffers)]
    [InlineData(KeyFailure.ArenaFileUnreadable, "0.6.1.0.48206", "0.6.0.0.47752", NoticeId.KeyUnreadable)]
    [InlineData(KeyFailure.KeyFileMissing, "0.6.1.0.48206", "0.6.0.0.47752", NoticeId.KeyFileBad)]
    [InlineData(KeyFailure.KeyFileInvalid, "0.6.1.0.48206", "0.6.0.0.47752", NoticeId.KeyFileBad)]
    [InlineData(KeyFailure.Internal, "0.6.1.0.48206", "0.6.0.0.47752", NoticeId.KeyInternal)]
    [InlineData(KeyFailure.None, "0.6.1.0.48206", "0.6.0.0.47752", NoticeId.KeyInternal)]
    public void Row18AKeyFailureChoosesItsTextByCauseAndArenaVersions(KeyFailure cause, string installed, string built, NoticeId expected)
    {
        MapLoadFailure record = NoticeTestData.KeyFailure("arena_bowl", cause, installed, built);
        record.Sequence = 3;
        MapNoticePlan plan = MapNoticeSelector.SelectAttempt(Attempt(new[] { record }));
        Assert.Equal(expected, plan.Id);
        Assert.Equal("Bowl", plan.Get("Map"));
        Assert.Equal("arena_bowl.json", plan.Get("file"));
        Assert.Equal(built, plan.Get("built"));
        foreach (string language in new[] { "en", "de" })
            Assert.DoesNotMatch(@"\{[A-Za-z][A-Za-z0-9]*\}", MapNoticeText.Compose(plan, language).Text);
    }

    [Fact]
    public void TheInstalledVersionFallsBackToTheGatesManifestVersion()
    {
        MapLoadFailure record = NoticeTestData.KeyFailure("arena_bowl", KeyFailure.ContentDiffers, installed: null, built: "0.6.0.0.47752");
        record.Sequence = 2;
        MapNoticePlan plan = MapNoticeSelector.SelectAttempt(Attempt(new[] { record }, NoticeTestData.Verified(version: "0.6.1.0.48206")));
        Assert.Equal(NoticeId.KeyUpdated, plan.Id);
        Assert.Equal("0.6.1.0.48206", plan.Get("inst"));
    }

    [Theory]
    [InlineData(MapLoadFailureKind.BundleMissing)]
    [InlineData(MapLoadFailureKind.BundleInvalid)]
    [InlineData(MapLoadFailureKind.BundleUnreadable)]
    [InlineData(MapLoadFailureKind.BundleLoadFailed)]
    [InlineData(MapLoadFailureKind.Integrity)]
    public void Row19EveryBundleProblemInAFailedAttemptIsBundleBadWithTheFile(MapLoadFailureKind kind)
    {
        MapNoticePlan plan = MapNoticeSelector.SelectAttempt(Attempt(new[] { Record(4, kind, "arena_bowl_preset.bundle") }));
        Assert.Equal(NoticeId.BundleBad, plan.Id);
        Assert.Equal("arena_bowl_preset.bundle", plan.Get("file"));
        Assert.Equal(NoticeLead.Attempt, plan.Lead);
    }

    [Fact]
    public void Row20NoRecordMeansNoCauseUnlessThePlayerCancelled()
    {
        MapNoticePlan plan = MapNoticeSelector.SelectAttempt(Attempt(new MapLoadFailure[0]));
        Assert.Equal(NoticeId.LoadNoCause, plan.Id);
        Assert.Equal("Bowl", plan.Get("Map"));

        Assert.Null(MapNoticeSelector.SelectAttempt(Attempt(new MapLoadFailure[0], canceled: true)));
        var started = new AttemptFailureInput("arena_bowl", "Bowl", new MapLoadFailure[0], NoticeTestData.Verified(), false, true, "0.3.0");
        Assert.Null(MapNoticeSelector.SelectAttempt(started));
        Assert.Null(MapNoticeSelector.SelectAttempt(null));

        // a cancelled attempt that nevertheless recorded a real cause still says it
        MapNoticePlan withCause = MapNoticeSelector.SelectAttempt(Attempt(new[] { Record(2, MapLoadFailureKind.BundleMissing) }, canceled: true));
        Assert.Equal(NoticeId.BundleBad, withCause.Id);
    }

    [Fact]
    public void TheNewestBlockingRecordDecidesAndNavMeshRecordsNeverDo()
    {
        MapLoadFailure key = NoticeTestData.KeyFailure("arena_bowl", KeyFailure.ContentDiffers);
        key.Sequence = 3;
        MapLoadFailure bundle = Record(7, MapLoadFailureKind.BundleMissing);
        MapLoadFailure nav = new() { Sequence = 9, MapKey = "arena_bowl", Kind = MapLoadFailureKind.NavMesh, NavMeshCause = NavMeshCause.NoKey };

        Assert.Equal(NoticeId.BundleBad, MapNoticeSelector.SelectAttempt(Attempt(new[] { key, bundle })).Id);
        // the NavMesh record is the newest of all (9), yet the newest blocking record (7, a bundle) decides
        Assert.Equal(NoticeId.BundleBad, MapNoticeSelector.SelectAttempt(Attempt(new[] { bundle, key, nav, Record(2, MapLoadFailureKind.BundleMissing) })).Id);
        MapLoadFailure olderBundle = Record(1, MapLoadFailureKind.BundleMissing);
        Assert.Equal(NoticeId.KeyUpdated, MapNoticeSelector.SelectAttempt(Attempt(new[] { olderBundle, key })).Id);
        // duplicates for one map collapse: three "no key" records read as one notice
        MapLoadFailure k1 = NoticeTestData.KeyFailure("arena_bowl", KeyFailure.ContentDiffers); k1.Sequence = 1;
        MapLoadFailure k2 = NoticeTestData.KeyFailure("arena_bowl", KeyFailure.ContentDiffers); k2.Sequence = 2;
        MapLoadFailure k3 = NoticeTestData.KeyFailure("arena_bowl", KeyFailure.ContentDiffers); k3.Sequence = 3;
        Assert.Equal(NoticeId.KeyUpdated, MapNoticeSelector.SelectAttempt(Attempt(new[] { k1, k2, k3 })).Id);
        // a NavMesh record alone is no blocking cause
        Assert.Equal(NoticeId.LoadNoCause, MapNoticeSelector.SelectAttempt(Attempt(new[] { nav })).Id);
    }

    // ---- versions ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.6.1.0.48206", "0.6.0.0.47752", 1)]
    [InlineData("0.6.0.0.47752", "0.6.1.0.48206", -1)]
    [InlineData("0.6.1.0.48206", "0.6.1.0.48206", 0)]
    [InlineData("0.6.1", "0.6.1.0.0", 0)]
    [InlineData("0.6.1.0.1", "0.6.1", 1)]
    [InlineData("1", "0.9.9.9.9", 1)]
    [InlineData("0.6.10", "0.6.9", 1)] // numeric, not text, order
    [InlineData(" 0.6.1 ", "0.6.0", 1)]
    public void VersionsCompareComponentByComponentWithAnyNumberOfParts(string a, string b, int expected)
    {
        Assert.True(MapNoticeSelector.TryCompareVersions(a, b, out int order));
        Assert.Equal(expected, order);
    }

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("1.0", null)]
    [InlineData("", "1.0")]
    [InlineData("1.x", "1.0")]
    [InlineData("1..0", "1.0")]
    [InlineData("-1.0", "1.0")]
    [InlineData("1.0.0.0.0.0.0.0.0", "1.0")]
    [InlineData("1234567890", "1.0")]
    [InlineData("v1.2", "1.0")]
    public void AVersionThatIsNotADottedNumberDoesNotCompare(string a, string b)
    {
        Assert.False(MapNoticeSelector.TryCompareVersions(a, b, out int order));
        Assert.Equal(0, order);
    }

    [Fact]
    public void ArenasFiveNumberVersionsAreTheReasonSystemVersionIsNotUsed()
    {
        Assert.False(Version.TryParse("0.6.1.0.48206", out _)); // what the design's "System.Version" rule would have met on every real Arena build
        Assert.True(MapNoticeSelector.TryCompareVersions("0.6.1.0.48206", "0.6.0.0.47752", out int order));
        Assert.Equal(1, order);
    }

    // ---- toasts ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheToastPlansCarryTheirValues()
    {
        MapNoticePlan pending = MapNoticeSelector.SelectPending("0.3.0");
        Assert.Equal(NoticeId.CheckPending, pending.Id);

        foreach (var (cause, why) in new[]
                 {
                     (NavMeshCause.Unverified, NoticeWhy.NavUnverified), (NavMeshCause.FileMissing, NoticeWhy.NavFileMissing),
                     (NavMeshCause.Damaged, NoticeWhy.NavDamaged), (NavMeshCause.NoKey, NoticeWhy.NavNoKey),
                 })
        {
            MapNoticePlan nav = MapNoticeSelector.SelectNavMesh("Bowl", cause, "0.3.0");
            Assert.Equal(NoticeId.NavMeshMissing, nav.Id);
            Assert.Equal(why, nav.Why);
            Assert.Equal("Bowl", nav.Get("Map"));
        }

        MapNoticePlan damaged = MapNoticeSelector.SelectFileDamaged("Bay 5", "arena_bay5_scenes.bundle", "0.3.0");
        Assert.Equal(NoticeId.MapFileDamaged, damaged.Id);
        Assert.Equal("arena_bay5_scenes.bundle", damaged.Get("file"));
        Assert.Equal("?", MapNoticeSelector.SelectFileDamaged("Bay 5", null, "0.3.0").Get("file"));
    }

    // ---- every id is reachable and composes -------------------------------------------------------------------------------------------

    [Fact]
    public void EveryNoticeIdIsProducedBySomeSelectorPath()
    {
        var produced = new HashSet<NoticeId>();
        void Add(MapNoticePlan plan) { if (plan != null) produced.Add(plan.Id); }

        foreach (ArenaInstallCause cause in Enum.GetValues<ArenaInstallCause>())
            foreach (string source in new[] { "override", "registry", "none" })
                Add(Menu(NoticeTestData.Input(NoticeTestData.NotVerified(cause, source), null)));
        foreach (GateState state in Enum.GetValues<GateState>())
            Add(Menu(NoticeTestData.Input(NoticeTestData.State(state), null)));
        Add(Menu(NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.AllOffered(), null, null, new[] { "x" })));
        Add(NoneOffered(NoticeTestData.Ok(NoticeTestData.Status("not_verified", "not_registered"))));
        Add(NoneOffered(NoticeTestData.Ok(NoticeTestData.Status("verified", "none", maps: NoticeTestData.MapsAll("failed", "registration")))));
        Add(NoneOffered(new ProbeView(ProbeState.NotFound)));
        Add(NoneOffered(new ProbeView(ProbeState.Failed)));
        Add(SomeOffered(new ProbeView(ProbeState.NotFound), "arena_equator"));
        Add(SomeOffered(NoticeTestData.Ok(NoticeTestData.Status("verified", "none", maps: new[] { ("arena_bay5", "failed", "registration") })), "arena_equator"));
        Add(Menu(NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.AllOffered(), null, Missing("x"))));
        Add(MapNoticeSelector.SelectPending("0.3.0"));
        Add(MapNoticeSelector.SelectNavMesh("Bowl", NavMeshCause.NoKey, "0.3.0"));
        Add(MapNoticeSelector.SelectFileDamaged("Bowl", "f", "0.3.0"));
        Add(new MapNoticePlan(NoticeId.SelectionReminder)); // composed by ComposeReminder, not the selector
        foreach (KeyFailure cause in Enum.GetValues<KeyFailure>())
            foreach (var (installed, built) in new[] { ("0.6.1", "0.6.0"), ("0.6.0", "0.6.1"), ("0.6.1", "0.6.1") })
            {
                MapLoadFailure record = NoticeTestData.KeyFailure("arena_bowl", cause, installed, built);
                record.Sequence = 1;
                Add(MapNoticeSelector.SelectAttempt(Attempt(new[] { record })));
            }
        Add(MapNoticeSelector.SelectAttempt(Attempt(new[] { Record(1, MapLoadFailureKind.BundleMissing) })));
        Add(MapNoticeSelector.SelectAttempt(Attempt(new MapLoadFailure[0])));

        Assert.Equal(MapNoticeText.AllIds.OrderBy(id => id), produced.OrderBy(id => id));
    }
}
