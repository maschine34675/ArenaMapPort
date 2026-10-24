#nullable disable
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;
using ClientGate = ArenaMapPort.Client.ArenaInstallationGate;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Where the in-game map notices are wired into the rest of the client (docs/progress/map-notices.md): the plugin's startup order and per-frame tick, the
/// attempt boundaries of LoadingScreenArt, the failure records of BundleHost and NavMeshInstaller, the skipped-hook registry of HarmonyPatches, the two
/// configuration allowlists, and the rules the Unity glue keeps. The glue itself (MapNotices.cs) calls EFT and cannot run here, so what it must do is pinned in
/// its source; the Unity-free parts it drives are tested with their own classes.
/// </summary>
public sealed class MapNoticeWiringTests
{
    static string ClientDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "ArenaMapPort.Client");
            if (File.Exists(Path.Combine(path, "Plugin.cs")))
                return path;
        }
        throw new DirectoryNotFoundException("ArenaMapPort.Client");
    }

    // Line endings normalised: the working tree is CRLF, the pins below are written with \n.
    static string Source(string file) => File.ReadAllText(Path.Combine(ClientDirectory(), file)).Replace("\r\n", "\n");

    // Between two markers of a source text (the second must follow the first).
    static string Between(string source, string from, string to)
    {
        int start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + from);
        int end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, "not found after '" + from + "': " + to);
        return source.Substring(start, end - start);
    }

    // ---- Plugin ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTickFollowsThePollOfTheGateInsideLateUpdateWithoutAnAddedBlock()
    {
        string plugin = Source("Plugin.cs");
        // the existing pin regex of the gate tests still matches: no brace between PollOutcome and the end of the method
        Assert.Matches(new Regex(@"void LateUpdate\(\)\s*\{[^}]*ArenaInstallationGate\.PollOutcome\(\);[^}]*\}"), plugin);
        Assert.Matches(new Regex(@"void LateUpdate\(\)\s*\{[^}]*ArenaInstallationGate\.PollOutcome\(\);\s*(//[^\n]*\r?\n\s*)*MapNotices\.Tick\(\);\s*\}"), plugin);
    }

    [Fact]
    public void TheNoticesAreBoundAndInstalledEachInItsOwnTryBlockBetweenTheGateAndTheBanner()
    {
        string plugin = Source("Plugin.cs");
        int gate = plugin.IndexOf("ArenaInstallationGate.Initialize(Config);", StringComparison.Ordinal);
        int harmony = plugin.IndexOf("_harmony = new Harmony(PluginGuid);", StringComparison.Ordinal);
        int bind = plugin.IndexOf("MapNotices.BindConfig(Config);", StringComparison.Ordinal);
        int install = plugin.IndexOf("MapNotices.Install(_harmony);", StringComparison.Ordinal);
        int banner = plugin.IndexOf("            LogStartupBanner();", StringComparison.Ordinal);
        int patches = plugin.IndexOf("HarmonyPatches.InstallAll(_harmony);", StringComparison.Ordinal);
        Assert.True(gate > 0 && harmony > gate && bind > harmony && install > bind && banner > install && patches > banner,
            "gate, Harmony instance, bind, install, banner, the other hooks");
        Assert.Single(Regex.Matches(plugin, @"_harmony = new Harmony\(PluginGuid\);")); // moved up, not duplicated
        Assert.Matches(new Regex(@"try\s*\{\s*MapNotices\.BindConfig\(Config\);\s*\}\s*catch \(Exception e\)\s*\{[^}]*\[Notice\][^}]*\}"), plugin);
        Assert.Matches(new Regex(@"try\s*\{\s*MapNotices\.Install\(_harmony\);\s*\}\s*catch \(Exception e\)\s*\{[^}]*\[Notice\][^}]*\}"), plugin);
    }

    // ---- the glue's own rules -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheOnePublicSwitchIsBoundOnceWithTheAgreedKeyDefaultAndText()
    {
        string glue = Source("MapNotices.cs");
        Assert.Single(Regex.Matches(glue, @"\.Bind\("));
        Assert.Contains(@"config.Bind(""General"", ""ShowMapNotices"", true, MapNoticeText.ShowMapNoticesDescription)", glue);
        Assert.Equal("General", MapNoticeText.ShowMapNoticesSection);
        Assert.Equal("ShowMapNotices", MapNoticeText.ShowMapNoticesKey);
        Assert.Equal(
            "Shows an in-game message at the main menu when the Arena maps are not available, with the reason and what to do. " +
            "Turning it off only hides these messages: the Arena check, the server and the log lines stay as they are, and the message that answers a failed raid start still appears. " +
            "Takes effect when the next message would be shown.", MapNoticeText.ShowMapNoticesDescription);
        // the switch silences messages only: nothing in the gate, the server registration or the bundle code reads it
        foreach (string file in new[] { "ArenaInstallationGate.cs", "BundleHost.cs", "BundleKeyResolver.cs", "NavMeshInstaller.cs" })
            Assert.DoesNotContain("ShowMapNotices", Source(file));
    }

    [Fact]
    public void TheSwitchIsInBothAllowlistsSoItIsKeptInThePublicFileAndShownInPlayerMode()
    {
        // CompactConfiguration and MapConfigVisibility need the real ConfigFile and cannot be compiled into this project (see the gate tests)
        string compact = Source("CompactConfiguration.cs");
        Assert.Matches(new Regex(@"IsPublic\(ConfigDefinition key\) =>[^;]*key\.Section == ""General"" && key\.Key == ""ShowMapNotices""[^;]*;"), compact);
        string visibility = Source("MapConfigVisibility.cs");
        Assert.Matches(new Regex(@"IsPlayerOption\(ConfigDefinition definition\)[^;]*definition\.Section == ""General"" && definition\.Key == ""ShowMapNotices""[^;]*;"), visibility);
        // the three older public entries are still there
        foreach (string source in new[] { compact, visibility })
        {
            Assert.Contains("\"NaturalEquatorNight\"", source);
            Assert.Contains("\"DeveloperMode\"", source);
            Assert.Contains("\"ArenaInstallPath\"", source);
        }
    }

    [Fact]
    public void TheGlueShowsTheModalForcedLeftAlignedAndTheToastAsALongSingletonWarning()
    {
        string glue = Source("MapNotices.cs");
        Assert.Contains("ui.ShowMessageWindow(text, accepted, null, caption, 0f, true, OurAlignment)", glue); // forceShow, left aligned, OK only
        Assert.Contains("const TextAlignmentOptions OurAlignment = TextAlignmentOptions.TopLeft;", glue);
        Assert.Contains("ui.ShowScrolledMessageWindow(text, accepted, null, caption, 0f, true)", glue);
        Assert.Contains("NotificationManager.DisplaySingletonWarningNotification(text, ENotificationDurationType.Long)", glue);
        Assert.DoesNotContain("ENotificationDurationType.Infinite", glue); // an un-clicked toast stalls every later one
        Assert.DoesNotContain(".ShowErrorScreen(", glue); // observing the native error is allowed; replacing or creating it is not
        Assert.DoesNotContain("ShowCriticalErrorScreen", glue);
        Assert.DoesNotContain("_messageOnLoadMenu", glue);
        Assert.Contains("Singleton<NotificationManager>.Instantiated", glue); // EFT drops a toast silently without its manager
    }

    [Fact]
    public void NativeErrorsAreObservedAfterTheOriginalQueuedThemWithoutTextMatchingOrChangingTheCallback()
    {
        string glue = Source("MapNotices.cs");
        Assert.Contains("AccessTools.Method(preloader, \"ShowErrorScreen\", new[] { typeof(string), typeof(string), typeof(Action) })", glue);
        Assert.Contains("HarmonyPatches.Verify(show, preloader, \"ShowErrorScreen(string, string, Action)\")", glue);
        Assert.Contains("harmony.Patch(show, postfix: new HarmonyMethod(typeof(MapNotices), nameof(NativeErrorReportedPostfix)))", glue);
        string observer = Between(glue, "static void NativeErrorReportedPostfix(", "static void EnsureCreated()");
        Assert.Contains("__runOriginal && AsyncWorker.CheckIsMainThread() && !string.IsNullOrWhiteSpace(__1)", observer);
        Assert.Contains("_scheduler?.NoteNativeErrorReported();", observer);
        Assert.DoesNotContain("ArenaMode", observer);
        Assert.DoesNotContain("PitFireTeam", observer);
        Assert.DoesNotContain("ShowErrorScreen", observer);
        Assert.DoesNotContain("ref ", observer);
    }

    [Fact]
    public void AnIntegrityRecordWakesTheSchedulerSoTheDamageToastDoesNotDependOnAMenuShow()
    {
        string glue = Source("MapNotices.cs");
        string noted = Between(glue, "static void OnFailureNoted(", "// ---- the hooks");
        Assert.Contains("failure.Kind == MapLoadFailureKind.Integrity", noted);
        Assert.Contains("_scheduler?.NoteIntegrityRecorded();", noted);
    }

    [Fact]
    public void ModalPossibleChecksEveryWindowAndErrorScreenAndTheRunningGameButNotTheStaleRaidFlag()
    {
        string glue = Source("MapNotices.cs");
        string find = Between(glue, "static string FindModalBlocker()", "static bool CriticalErrorScreenActive(");
        foreach (string check in new[]
                 {
                     "ItemUiContext.Instance", "ui.MessageWindowActive", "ui.ScrolledMessageWindowActive", "MonoBehaviourSingleton<PreloaderUI>.Instantiated",
                     "preloader.UnknownErrorScreen", "preloader.ErrorScreen", "CriticalErrorScreenActive(preloader)", "Singleton<AbstractGame>.Instantiated",
                 })
            Assert.Contains(check, find);
        // InGameStatus.InRaid is true for the whole session after a start that failed after GamePrepare: refusing on it would make the forced modal unreachable
        Assert.DoesNotContain("InGameStatus", find);
        Assert.DoesNotContain("RaidFlagSet", glue);
        // every sub-check names itself, and a check that throws is "exception" (a total predicate), never an exception into the scheduler
        Assert.Contains("return \"exception\";", find);
        foreach (string name in new[] { "\"ItemUiContext\"", "\"MessageWindow\"", "\"ScrolledMessageWindow\"", "\"PreloaderUI\"", "\"UnknownErrorScreen\"", "\"ErrorScreen\"", "\"CriticalErrorScreen\"", "\"AbstractGame\"" })
            Assert.Contains(name, find);

        // a critical error screen is an ACTIVE ErrorScreen under the preloader's container, not any active child of it
        string critical = Between(glue, "static bool CriticalErrorScreenActive(", "public bool ToastPossible");
        Assert.Contains("preloader._criticalErrorScreenContainer", critical);
        Assert.Contains("GetComponent<ErrorScreen>() != null", critical);
        Assert.Contains("activeSelf", critical);
    }

    [Fact]
    public void TheWindowWatchdogSeesBothMessageWindowsAndTheScrolledWindowIsUsedForLongTexts()
    {
        string glue = Source("MapNotices.cs");
        string open = Between(glue, "public bool MessageWindowOpen", "// The name of the first sub-check");
        Assert.Contains("ui.MessageWindowActive || ui.ScrolledMessageWindowActive", open);
        Assert.Contains("return true; // unknown reads as \"still open\"", open);

        // the scrolled branch of ShowModal: the shared window is made left aligned first (ShowScrolledMessageWindow has no alignment argument)
        string show = Between(glue, "public ModalTicket ShowModal(", "public bool ShowToast(");
        Assert.Contains("scrolled ? ui._scrolledMessageWindow : ui._messageWindow", show);
        Assert.Contains("SetAlignment(window, OurAlignment);", show);
        Assert.Contains("ui.ShowScrolledMessageWindow(text, accepted, null, caption, 0f, true)", show);
    }

    [Fact]
    public void TheSharedWindowsAlignmentGoesBackOnlyWhileItIsStillTheOneTheNoticeSet()
    {
        // ItemUiContext.method_1 declines our window AFTER the displacing caller set its own alignment: restoring blindly would overwrite that one
        string glue = Source("MapNotices.cs");
        string restore = Between(glue, "static void RestoreAlignment(", "public void Debug(");
        Assert.Contains("current.Value == OurAlignment", restore);
        Assert.Contains("ReadAlignment(window)", restore);
    }

    [Fact]
    public void TheGlueReadsTheScreenManagerWithoutCreatingOneAndHasNoSceneWideSearch()
    {
        string glue = Source("MapNotices.cs");
        Assert.Contains("EftScreenManager._instance", glue);
        Assert.DoesNotContain("EftScreenManager.Instance", glue); // the getter creates a manager when none exists: that is EFT's to do
        foreach (string file in new[] { "MapNotices.cs", "ServerStatusClient.cs" })
        {
            string source = Source(file);
            foreach (string search in new[] { "FindObjectsOfType", "FindObjectOfType", "FindObjectsByType", "GetComponentsInChildren", "FindObjectsOfTypeAll", "GameObject.Find" })
                Assert.DoesNotContain(search, source);
        }
    }

    [Fact]
    public void AllHooksAreResolvedByNameVerifiedAndGuardedAndTheMenuHookIgnoresACallWithoutAProfile()
    {
        string glue = Source("MapNotices.cs");
        Assert.Contains("AccessTools.TypeByName(\"EFT.UI.MenuScreen\")", glue);
        Assert.Contains("new[] { typeof(Profile), typeof(MatchmakerPlayersController), typeof(ESessionMode) }", glue);
        Assert.Contains("HarmonyPatches.Verify(show, menuScreen, \"Show(Profile, MatchmakerPlayersController, ESessionMode)\")", glue);
        Assert.Contains("AccessTools.TypeByName(\"EFT.UI.Matchmaker.MatchMakerSelectionLocationScreen\")", glue);
        Assert.Contains("HarmonyPatches.Verify(show, selection, \"ShowInternal(RaidSettings)\")", glue);
        Assert.Equal(3, Regex.Matches(glue, @"harmony\.Patch\(").Count);
        Assert.Equal(2, Regex.Matches(glue, @"Guard\(""hooking the ").Count);
        Assert.Contains("Guard(\"observing native load errors\", () =>", glue); // the observer also has its own guard
        Assert.Matches(new Regex(@"static void MenuShownPostfix\(Profile profile\)\s*\{\s*OnMenuShown\(profile != null\);"), glue);
        Assert.Contains("_scheduler.MenuPostfixInstalled = menuHooked;", glue); // the scheduler polls when the postfix is missing
    }

    [Fact]
    public void EveryEntryPointOfTheGlueIsTotal()
    {
        string glue = Source("MapNotices.cs");
        foreach (string entry in new[] { "OnMenuShown", "OnSelectionShown", "NoteAttemptStarted", "NoteComeback", "NoteRaidWithoutNavMesh", "NoteRaidEnded", "Tick" })
        {
            string body = Between(glue, "internal static void " + entry + "(", "\n        }\n");
            Assert.Contains("catch (Exception e)", body);
            Assert.Contains("if (scheduler == null)", body);
        }
    }

    [Fact]
    public void TheProbeIsStartedOnlyThroughTheEnvironmentAndTheModalSurfacesItsTicketOutcomes()
    {
        string glue = Source("MapNotices.cs");
        Assert.Contains("_probe = new ServerStatusClient();", glue);
        Assert.Contains("ModalTicket.SuppressedAt(now)", glue);
        Assert.Contains("ModalTicket.FailedAt(", glue);
        Assert.Contains("context.OnDecline += closed;", glue);
        Assert.Contains("context.OnCloseSilent += closed;", glue);
        Assert.Contains("context.OnClose += closed;", glue);
        Assert.Contains("ticket?.Accept(Now)", glue);
    }

    // ---- the attempt boundaries ----------------------------------------------------------------------------------------------------

    [Fact]
    public void LoadingScreenArtOpensAnAttemptBeforeItShowsTheArtAndFailsOneAtTheComeback()
    {
        string art = Source("LoadingScreenArt.cs");
        string show = Between(art, "static void ShowPostfix(", "// Subscribes to the deploy screen controller's OnClose");
        int started = show.IndexOf("MapNotices.NoteAttemptStarted(map.Key);", StringComparison.Ordinal);
        int shown = show.IndexOf("Show(map, (__instance as Component)?.transform);", StringComparison.Ordinal);
        int tryStart = show.IndexOf("try", StringComparison.Ordinal);
        Assert.True(tryStart >= 0 && started > tryStart && shown > started, "inside the existing try block, before the art is shown");

        string comeback = Between(art, "static void ComebackToMainMenuPrefix()", "static void OnSceneLoaded(");
        int note = comeback.IndexOf("MapNotices.NoteComeback();", StringComparison.Ordinal);
        int hide = comeback.IndexOf("Hide(\"ComebackToMainMenu\");", StringComparison.Ordinal);
        Assert.True(comeback.IndexOf("try", StringComparison.Ordinal) >= 0 && note > 0 && hide > note);
    }

    [Fact]
    public void NavMeshInstallerReportsARaidWithoutNavMeshAndItsEnd()
    {
        string nav = Source("NavMeshInstaller.cs");
        string init = Between(nav, "internal static void OnBotsControllerInit()", "static void OnSceneUnloaded");
        Assert.Matches(new Regex(@"if \(data == null\)\s*\{[^}]*MapNotices\.NoteRaidWithoutNavMesh\(map\.Key\);[^}]*return;"), init);
        string unload = Between(nav, "static void OnSceneUnloaded", "static void NoteNavMesh");
        Assert.Contains("MapNotices.NoteRaidEnded();", unload);
        Assert.DoesNotContain("Dispose", unload); // the existing pin: a scene unload never closes a stream
    }

    // ---- the failure records ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void BundleHostAndNavMeshInstallerWriteTheirRecordsOnlyThroughNoteSafeAndBeforeTheErrorLine()
    {
        string host = Source("BundleHost.cs");
        string nav = Source("NavMeshInstaller.cs");
        foreach (string source in new[] { host, nav })
        {
            Assert.DoesNotMatch(new Regex(@"\bFailures\.Note"), source); // no bare call on the registry
            Assert.Contains("NoteSafe(", source);
        }

        string serve = Between(host, "internal static void TryServe", "static bool IsStillServed");
        Assert.Matches(new Regex(@"NoteFailure\(bundleName, MapLoadFailureKind\.GateRefused, ArenaInstallationGate\.Reason\);[^\n]*\r?\n\s*Plugin\.Log\.LogError\(ArenaInstallationGate\.NotServedMessage\(bundleName\)\);"), serve);
        Assert.Matches(new Regex(@"NoteFailure\(bundleName, MapLoadFailureKind\.BundleMissing, ""the file is missing""\);\s*Plugin\.Log\.LogError\("), serve);

        string load = Between(host, "internal static AssetBundle LoadAssetBundle", "internal static void LogStreamStats");
        Assert.Matches(new Regex(@"NoteLoadFailure\(resolver, mapKey, navMesh, filePath, e\.GetType\(\)\.Name \+ "": "" \+ e\.Message\);\s*Plugin\.Log\.LogError\("), load);
        Assert.Matches(new Regex(@"NoteLoadFailure\(resolver, mapKey, navMesh, filePath, api \+ "" returned null""\);\s*Plugin\.Log\.LogError\("), load);

        string once = Between(nav, "static NavMeshData LoadOnce", "static void CloseStream");
        Assert.Equal(4, Regex.Matches(once, @"NoteNavMesh\(map\.Key, NavMeshCause\.\w+,[^;]*;\s*Plugin\.Log\.LogError\(").Count); // gate, file, no data, catch
        foreach (string cause in new[] { "Unverified", "FileMissing", "Damaged" })
            Assert.Contains("NavMeshCause." + cause, once);
    }

    [Fact]
    public void TheResolverKeepsTheKeyFailureAndCreatesTheRegistryWithItsStreams()
    {
        string resolver = Source("BundleKeyResolver.cs");
        Assert.Contains("Failures = new MapLoadFailures();", resolver);
        Assert.Contains("Streams = new BundleStreamRegistry(env, Failures);", resolver);
        Assert.Contains("internal MapLoadFailures Failures { get; }", resolver);
        Assert.Contains("internal void NoteSafe(Action<MapLoadFailures> note)", resolver);
        string notes = Between(resolver, "internal PreparedBundle Prepare(", "static PreparedBundle Refused");
        Assert.Equal(4, Regex.Matches(notes, @"(?<!void )Note(BundleProblem|NoKey)\(").Count); // the four refusals of Prepare
        // every note is followed by the unchanged Error line of that branch
        Assert.Matches(new Regex(@"NoteNoKey\(mapKey, navMesh, filePath\);\s*_env\.LogError\(NoKeyLine\(mapKey, servedKey, navMesh\)\);"), resolver);
    }

    // ---- the skipped hooks ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void VerifyRecordsASkippedTargetBeforeItsErrorLineAndTheTwoCriticalOnesMatchTheInstallers()
    {
        string patches = Source("HarmonyPatches.cs");
        string verify = Between(patches, "internal static bool Verify(", "\n        }\n");
        int record = verify.IndexOf("SkippedHooks.Record(declaringType?.FullName, methodName);", StringComparison.Ordinal);
        int error = verify.IndexOf("Plugin.Log.LogError(", StringComparison.Ordinal);
        Assert.True(record > 0 && error > record, "recorded before the Error line");

        // the two map-critical targets the registry names are the ones InstallBundleHost and InstallNavMesh verify
        Assert.Contains("Verify(target, typeof(BundlesManager), nameof(BundlesManager.LoadBundleAsync))", patches);
        Assert.Contains("Verify(target, typeof(BotsController), nameof(BotsController.Init))", patches);
        Assert.Equal("EFT.AssetsManager.BundlesManager", SkippedHooks.BundleServingType);
        Assert.Equal("LoadBundleAsync", SkippedHooks.BundleServingMethod);
        Assert.Equal("EFT.BotsController", SkippedHooks.NavMeshType);
        Assert.Equal("Init", SkippedHooks.NavMeshMethod);
    }

    // ---- the registry diagnostic of the gate ---------------------------------------------------------------------------------------

    sealed class ListedRegistry : IRegistryProbe
    {
        readonly string[] _names;

        internal ListedRegistry(params string[] names) => _names = names;

        public bool IsAvailable => true;

        public string GetValue(string keyName, string valueName) =>
            valueName == "InstallLocation" && keyName.EndsWith("\\EscapeFromTarkov", StringComparison.OrdinalIgnoreCase) ? @"D:\Nowhere\Tarkov" : null;

        public string[] GetSubKeyNames(string keyName) => keyName.EndsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ? _names : new string[0];
    }

    [Fact]
    public void ANotRegisteredResultGetsTheOneDiagnosticInfoLineAndNoOtherResultDoes()
    {
        var notRegistered = ArenaInstallation.Detect(null, new ListedRegistry("EscapeFromTarkov", "Unrelated"), ArenaInstallationFloors.Default);
        Assert.Equal(ArenaInstallCause.NotRegistered, notRegistered.Cause);

        string line = ClientGate.RegistryDiagnosticLine(notRegistered);

        Assert.StartsWith("[Arena] no registry entry; Uninstall subkeys matching Arena or Tarkov: ", line);
        Assert.Contains("EscapeFromTarkov=D:\\Nowhere\\Tarkov, exe=no", line);
        Assert.DoesNotContain("Unrelated", line);
        Assert.DoesNotContain("\n", line);

        var none = ArenaInstallation.Detect(null, new ListedRegistry(), ArenaInstallationFloors.Default);
        Assert.EndsWith("Arena or Tarkov: none", ClientGate.RegistryDiagnosticLine(none));

        var badFolder = ArenaInstallation.Verify(Path.Combine(Path.GetTempPath(), "arena-no-such-" + Guid.NewGuid().ToString("N")), "override", null, ArenaInstallationFloors.Default);
        Assert.False(badFolder.Verified);
        Assert.Null(ClientGate.RegistryDiagnosticLine(badFolder));
        Assert.Null(ClientGate.RegistryDiagnosticLine(null));
        Assert.Null(ClientGate.RegistryDiagnosticLine(ArenaInstallation.Detect(null, new UnavailableRegistry(), ArenaInstallationFloors.Default)));
    }

    // an unavailable registry is another cause (RegistryUnavailable): no candidates, no line
    sealed class UnavailableRegistry : IRegistryProbe
    {
        public bool IsAvailable => false;
        public string GetValue(string keyName, string valueName) => null;
        public string[] GetSubKeyNames(string keyName) => new string[0];
    }

    [Fact]
    public void TheGateLogsTheDiagnosticAfterItsHintAndOnlyForANotRegisteredResult()
    {
        string gate = Source("ArenaInstallationGate.cs");
        string log = Between(gate, "internal static void LogOutcome()", "/// <summary>Forget everything");
        int hint = log.IndexOf("Plugin.Log.LogInfo(HintLine());", StringComparison.Ordinal);
        int diagnostic = log.IndexOf("Plugin.Log.LogInfo(diagnostic);", StringComparison.Ordinal);
        int mismatches = log.IndexOf("mismatch: ", StringComparison.Ordinal);
        Assert.True(hint > 0 && diagnostic > hint && mismatches > diagnostic);
        Assert.Contains("string diagnostic = RegistryDiagnosticLine(result);", log);
    }
}

/// <summary>The skipped-hook registry behind the HOOK_MISSING notice.</summary>
public sealed class SkippedHooksTests : IDisposable
{
    public SkippedHooksTests() => SkippedHooks.ResetForTests();

    public void Dispose() => SkippedHooks.ResetForTests();

    [Fact]
    public void NothingIsSkippedUntilVerifyFindsAMissingTarget()
    {
        Assert.Empty(SkippedHooks.Targets());
        Assert.Empty(SkippedHooks.MapCritical());
    }

    [Fact]
    public void OnlyTheBundleServingAndTheNavMeshTargetsAreMapCritical()
    {
        SkippedHooks.Record("EFT.AssetsManager.BundlesManager", "LoadBundleAsync");
        SkippedHooks.Record("EFT.BotsController", "Init");
        SkippedHooks.Record("EFT.AmbientLight", "method_3");
        SkippedHooks.Record("EFT.UI.Matchmaker.MatchmakerTimeHasCome", "Show(IEftSession, RaidSettings, MatchmakerPlayersController)");

        Assert.Equal(new[] { "BundlesManager.LoadBundleAsync", "BotsController.Init" }, SkippedHooks.MapCritical());
        Assert.Equal(4, SkippedHooks.Targets().Count);
        Assert.Equal("AmbientLight.method_3", SkippedHooks.Targets()[2]);
    }

    [Fact]
    public void ATargetVerifiedTwiceIsOneEntry()
    {
        // BotsController.Init is verified by the NavMesh installer and by the stationary-controller repair
        SkippedHooks.Record("EFT.BotsController", "Init");
        SkippedHooks.Record("EFT.BotsController", "Init");

        Assert.Single(SkippedHooks.Targets());
        Assert.Single(SkippedHooks.MapCritical());
    }

    [Fact]
    public void ASimilarNameInAnotherTypeIsNotMapCritical()
    {
        SkippedHooks.Record("Other.BotsController", "Init");
        SkippedHooks.Record("EFT.BotsController", "Initialize");
        SkippedHooks.Record("EFT.AssetsManager.BundlesManager", "UnloadBundle");

        Assert.Empty(SkippedHooks.MapCritical());
        Assert.False(SkippedHooks.IsMapCritical("EFT.BotsController", "Initialize"));
        Assert.True(SkippedHooks.IsMapCritical("EFT.BotsController", "Init"));
    }

    [Fact]
    public void RecordingNothingNeverThrowsAndTheListsAreCopies()
    {
        SkippedHooks.Record(null, null);
        SkippedHooks.Record("", "");
        Assert.Equal(new[] { "?.?", "." }, SkippedHooks.Targets());

        IReadOnlyList<string> copy = SkippedHooks.Targets();
        SkippedHooks.Record("EFT.BotsController", "Init");
        Assert.Equal(2, copy.Count); // a returned list does not grow
    }

    [Fact]
    public void ARecordingFromManyThreadsKeepsEveryTargetOnce()
    {
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            for (int n = 0; n < 50; n++)
                SkippedHooks.Record("EFT.Type" + (n % 10), "Method");
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(20))));

        Assert.Equal(10, SkippedHooks.Targets().Count);
    }
}
