#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using Diz.Utils;
using EFT;
using EFT.Communications;
using EFT.Settings;
using EFT.UI;
using EFT.UI.Matchmaker;
using EFT.UI.Screens;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The in-game notice that says why the Arena maps are missing or failed to load (docs/progress/map-notices.md, the design of 2026-10-22). This is the
    /// only file that talks to EFT's UI: <c>ItemUiContext</c> (the modal window), <c>NotificationManager</c> (the toast), <c>EftScreenManager</c> (which
    /// screen is current), <c>PreloaderUI</c> (error screens) and <c>SettingsManager</c> (EFT's UI language). Every decision lives in the Unity-free core it
    /// drives: <see cref="MapNoticeScheduler"/> (the state machine), <see cref="MapNoticeSelector"/> and <see cref="MapNoticeText"/>.
    ///
    /// What vanilla does. Nothing about our maps: when the server registers no Arena location the selection simply lacks them, and a failed load ends in
    /// EFT's generic "matching aborted" popup. The player cannot tell a missing Arena installation from a locked server DLL from a damaged download.
    ///
    /// What the port lacks. A message at the moment the player looks for the maps, with the reason and the fix, that does not depend on the server having
    /// registered anything (EFT's locale files cannot carry it: the server delivers map texts only when the Arena check passed, which is the failing case).
    ///
    /// What this class does. <see cref="BindConfig"/> binds the one public switch <c>General/ShowMapNotices</c> (it silences the main-menu messages and the
    /// selection reminder; it never touches the Arena check, the server or the log lines). <see cref="Install"/> hooks the main menu
    /// (<c>MenuScreen.Show</c> postfix, a call with no profile is the in-raid Esc menu and is ignored; with a polling fallback of the scheduler when the hook
    /// is missing) and the location selection (<c>MatchMakerSelectionLocationScreen.ShowInternal</c> postfix), each patch individually guarded and
    /// passed through <see cref="HarmonyPatches.Verify"/>. <see cref="Tick"/> (one flag read per frame while idle) drives the scheduler from
    /// <c>Plugin.LateUpdate</c>. The attempt boundaries come from <c>LoadingScreenArt</c> (<see cref="NoteAttemptStarted"/>,
    /// <see cref="NoteComeback"/>), the "raid without NavMesh" record from <c>NavMeshInstaller</c>. The environment implements what the scheduler asks of the
    /// game: the screen and window predicates (total, never counted as faults), the modal window with its callbacks (shown with <c>forceShow</c> because
    /// <c>InGameStatus.InRaid</c> stays true after a start that failed late; left aligned like EFT's own long messages; a text longer than the ordinary
    /// window goes to the scrolled one; the shared window's alignment goes back only when it is still the one this class set), the toast (singleton warning, <c>Long</c>, never <c>Infinite</c>: an un-clicked toast stalls every later one), the
    /// location list of EFT's backend, the file check and the server probe (<see cref="ServerStatusClient"/>).
    ///
    /// Constraints. Main thread only for every EFT call and every log line. No exception reaches EFT: each entry point is guarded and a notice that cannot be
    /// shown costs nothing else. No scene-wide search anywhere (the tick is a flag read, the window predicates are a handful of null checks and one transform's
    /// children). The Arena check is only read, never waited for.
    ///
    /// Host test: docs/progress/map-notices.md (V1 to V8), not yet run.
    /// </summary>
    internal static class MapNotices
    {
        static ConfigEntry<bool> _show;
        static MapNoticeScheduler _scheduler;
        static NoticeEnvironment _environment;
        static readonly HashSet<string> FaultWarned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The scheduler (null until <see cref="Install"/> created it, or when that failed): for the diagnostics and the tests of the glue.</summary>
        internal static MapNoticeScheduler Scheduler => _scheduler;

        // ---- configuration -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Binds <c>General/ShowMapNotices</c> (default true) on the public configuration file. A switch for the messages only: the Arena check, the
        /// server's registration and the bundle refusal never read it. Plugin.Awake calls this in its own try/catch.
        /// </summary>
        internal static void BindConfig(ConfigFile config)
        {
            _show = config.Bind("General", "ShowMapNotices", true, MapNoticeText.ShowMapNoticesDescription);
        }

        // ---- installation ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// Creates the scheduler and installs the Harmony hooks, each in its own guard; a hook that cannot be installed is one Error line from
        /// <see cref="HarmonyPatches.Verify"/> (or a Warning here) and never stops the plugin. Plugin.Awake calls this in its own try/catch, before
        /// the startup banner.
        /// </summary>
        internal static void Install(Harmony harmony)
        {
            Guard("creating the notices", EnsureCreated);
            if (_scheduler == null)
                return;

            bool menuHooked = false;
            bool selectionHooked = false;
            bool errorHooked = false;

            Guard("hooking the main menu", () =>
            {
                Type menuScreen = AccessTools.TypeByName("EFT.UI.MenuScreen");
                if (menuScreen == null)
                {
                    Plugin.Log.LogError("[Notice] type not found: EFT.UI.MenuScreen: the main-menu message falls back to polling the current screen.");
                    return;
                }
                var show = AccessTools.Method(menuScreen, "Show", new[] { typeof(Profile), typeof(MatchmakerPlayersController), typeof(ESessionMode) });
                if (HarmonyPatches.Verify(show, menuScreen, "Show(Profile, MatchmakerPlayersController, ESessionMode)"))
                {
                    var postfix = AccessTools.Method(typeof(MapNotices), nameof(MenuShownPostfix));
                    harmony.Patch(show, postfix: new HarmonyMethod(postfix));
                    menuHooked = true;
                }
            });

            Guard("hooking the location selection", () =>
            {
                Type selection = AccessTools.TypeByName("EFT.UI.Matchmaker.MatchMakerSelectionLocationScreen");
                if (selection == null)
                {
                    Plugin.Log.LogError("[Notice] type not found: EFT.UI.Matchmaker.MatchMakerSelectionLocationScreen: the selection reminder is disabled.");
                    return;
                }
                var show = AccessTools.Method(selection, "ShowInternal", new[] { typeof(RaidSettings) });
                if (HarmonyPatches.Verify(show, selection, "ShowInternal(RaidSettings)"))
                {
                    var postfix = AccessTools.Method(typeof(MapNotices), nameof(SelectionShownPostfix));
                    harmony.Patch(show, postfix: new HarmonyMethod(postfix));
                    selectionHooked = true;
                }
            });

            Guard("observing native load errors", () =>
            {
                Type preloader = AccessTools.TypeByName("EFT.UI.PreloaderUI");
                if (preloader == null)
                {
                    Plugin.Log.LogError("[Notice] type not found: EFT.UI.PreloaderUI: native load errors cannot suppress the no-cause fallback.");
                    return;
                }
                var show = AccessTools.Method(preloader, "ShowErrorScreen", new[] { typeof(string), typeof(string), typeof(Action) });
                if (HarmonyPatches.Verify(show, preloader, "ShowErrorScreen(string, string, Action)"))
                {
                    harmony.Patch(show, postfix: new HarmonyMethod(typeof(MapNotices), nameof(NativeErrorReportedPostfix)));
                    errorHooked = true;
                }
            });

            _scheduler.MenuPostfixInstalled = menuHooked;
            Plugin.Log.LogDebug("[Notice] installed (main menu hook=" + (menuHooked ? "yes" : "no, polling") + ", selection hook=" +
                                (selectionHooked ? "yes" : "no") + ", native error hook=" + (errorHooked ? "yes" : "no") +
                                ", ShowMapNotices=" + (_show == null || _show.Value ? "on" : "off") + ")");
        }

        // The exception overload forwards here. Observe the queued error without reading its
        // wording, replacing it or touching the callback that returns EFT to the menu.
        static void NativeErrorReportedPostfix(string __1, bool __runOriginal)
        {
            Guard("native load error", () =>
            {
                if (__runOriginal && AsyncWorker.CheckIsMainThread() && !string.IsNullOrWhiteSpace(__1))
                    _scheduler?.NoteNativeErrorReported();
            });
        }

        static void EnsureCreated()
        {
            if (_scheduler != null)
                return;
            BundleKeyResolver resolver = BundleKeyResolver.Default;
            resolver.Failures.Noted = OnFailureNoted;
            _environment = new NoticeEnvironment();
            _scheduler = new MapNoticeScheduler(_environment, resolver.Failures);
        }

        // The Debug line of every stored record: "[Notice] load failure noted: map=<key> kind=<kind> cause=<cause> seq=<n>".
        static void OnFailureNoted(MapLoadFailure failure)
        {
            string cause = failure.Kind == MapLoadFailureKind.Key ? failure.KeyCause.ToString()
                : failure.Kind == MapLoadFailureKind.NavMesh ? failure.NavMeshCause.ToString() : "none";
            Plugin.Log.LogDebug("[Notice] load failure noted: map=" + failure.MapKey + " kind=" + failure.Kind + " cause=" + cause + " seq=" + failure.Sequence);
            if (failure.Kind == MapLoadFailureKind.Integrity)
                _scheduler?.NoteIntegrityRecorded(); // row 22 must not depend on a menu show being counted after the raid
        }

        // ---- the hooks ---------------------------------------------------------------------------------------------------------

        // Harmony postfix of MenuScreen.Show(Profile, MatchmakerPlayersController, ESessionMode). The in-raid Esc menu is a MenuScreen too and calls it with a
        // null profile: that is not a main menu.
        static void MenuShownPostfix(Profile profile)
        {
            OnMenuShown(profile != null);
        }

        // Harmony postfix of MatchMakerSelectionLocationScreen.ShowInternal(RaidSettings).
        static void SelectionShownPostfix()
        {
            OnSelectionShown();
        }

        // ---- entry points (each total: nothing reaches EFT) --------------------------------------------------------------------

        /// <summary><c>MenuScreen.Show</c> ran. <paramref name="hasProfile"/> is false for the in-raid Esc menu.</summary>
        internal static void OnMenuShown(bool hasProfile)
        {
            MapNoticeScheduler scheduler = _scheduler;
            if (scheduler == null)
                return;
            try
            {
                scheduler.OnMenuShown(hasProfile);
            }
            catch (Exception e)
            {
                Fault("menu", e);
            }
        }

        /// <summary>The location selection screen opened.</summary>
        internal static void OnSelectionShown()
        {
            MapNoticeScheduler scheduler = _scheduler;
            if (scheduler == null)
                return;
            try
            {
                scheduler.OnSelectionShown();
            }
            catch (Exception e)
            {
                Fault("selection", e);
            }
        }

        /// <summary>EFT started to load the raid of <paramref name="mapKey"/> (LoadingScreenArt's show hook).</summary>
        internal static void NoteAttemptStarted(string mapKey)
        {
            MapNoticeScheduler scheduler = _scheduler;
            if (scheduler == null)
                return;
            try
            {
                scheduler.NoteAttemptStarted(mapKey);
            }
            catch (Exception e)
            {
                Fault("attempt", e);
            }
        }

        /// <summary><c>ComebackToMainMenu</c> ran (LoadingScreenArt's prefix): an attempt that never started has failed.</summary>
        internal static void NoteComeback()
        {
            MapNoticeScheduler scheduler = _scheduler;
            if (scheduler == null)
                return;
            try
            {
                scheduler.NoteComeback();
            }
            catch (Exception e)
            {
                Fault("comeback", e);
            }
        }

        /// <summary>A raid started on <paramref name="mapKey"/> although its NavMesh could not be loaded (NavMeshInstaller).</summary>
        internal static void NoteRaidWithoutNavMesh(string mapKey)
        {
            MapNoticeScheduler scheduler = _scheduler;
            if (scheduler == null)
                return;
            try
            {
                scheduler.NoteRaidWithoutNavMesh(mapKey);
            }
            catch (Exception e)
            {
                Fault("navmesh", e);
            }
        }

        /// <summary>The raid's scene unloaded (NavMeshInstaller): a pending "raid without NavMesh" record is stale.</summary>
        internal static void NoteRaidEnded()
        {
            MapNoticeScheduler scheduler = _scheduler;
            if (scheduler == null)
                return;
            try
            {
                scheduler.NoteRaidEnded();
            }
            catch (Exception e)
            {
                Fault("raid end", e);
            }
        }

        /// <summary>
        /// Called every frame after <c>ArenaInstallationGate.PollOutcome</c> in <c>Plugin.LateUpdate</c>. While nothing is pending it is one flag read inside the
        /// scheduler. Never throws.
        /// </summary>
        internal static void Tick()
        {
            MapNoticeScheduler scheduler = _scheduler;
            if (scheduler == null)
                return;
            try
            {
                scheduler.Tick();
            }
            catch (Exception e)
            {
                Fault("tick", e);
            }
        }

        // ---- faults ------------------------------------------------------------------------------------------------------------

        static bool Guard(string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                Fault(what, e);
                return false;
            }
        }

        // The glue's own faults: one Warning per place, never an exception. (The scheduler counts and latches the faults of showing a notice.)
        static void Fault(string what, Exception e)
        {
            try
            {
                if (FaultWarned.Add(what))
                    Plugin.Log.LogWarning("[Notice] " + what + " failed (" + e.GetType().Name + ": " + e.Message + "); the maps themselves are not affected");
            }
            catch
            {
                // the fault report itself must not throw into EFT's frame
            }
        }

        // ---- the environment ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// What <see cref="MapNoticeScheduler"/> asks of the game, made of EFT's own singletons. Every predicate is total (false on any exception, no log line);
        /// the actions never throw (a failure is a <see cref="ModalTicket"/> in the Failed state).
        /// </summary>
        sealed class NoticeEnvironment : INoticeEnvironment
        {
            // the notices' own text alignment, like EFT's long messages
            const TextAlignmentOptions OurAlignment = TextAlignmentOptions.TopLeft;

            IReadOnlyList<MapInfo> _maps;
            ServerStatusClient _probe;
            bool _probeLogged;
            string _pluginDirectory;

            public double Now => Time.unscaledTime;

            public bool NoticesEnabled => _show == null || _show.Value;

            public string Language
            {
                get
                {
                    try
                    {
                        if (!Singleton<SettingsManager>.Instantiated)
                            return "";
                        return Singleton<SettingsManager>.Instance?.Game?.Settings?.Language?.Value ?? "";
                    }
                    catch
                    {
                        return "";
                    }
                }
            }

            public string ClientVersion => Plugin.PluginVersion;

            public IReadOnlyList<MapInfo> Maps => _maps ?? (_maps = MapInfo.FromRegistry());

            public bool MenuIsCurrent
            {
                get
                {
                    try
                    {
                        // _instance, not Instance: the getter creates a screen manager when none exists, and that is EFT's to create
                        EftScreenManager screens = EftScreenManager._instance;
                        return screens != null && screens.CheckCurrentScreen(EEftScreenType.MainMenu);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            public bool SelectionIsCurrent
            {
                get
                {
                    try
                    {
                        EftScreenManager screens = EftScreenManager._instance;
                        return screens != null && screens.CheckCurrentScreen(EEftScreenType.SelectLocation);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            string _modalBlocker = "";

            public bool ModalPossible
            {
                get
                {
                    string blocker = FindModalBlocker();
                    _modalBlocker = blocker ?? "";
                    return blocker == null;
                }
            }

            public string ModalBlocker => _modalBlocker;

            public bool MessageWindowOpen
            {
                get
                {
                    try
                    {
                        ItemUiContext ui = ItemUiContext.Instance;
                        return ui != null && (ui.MessageWindowActive || ui.ScrolledMessageWindowActive);
                    }
                    catch
                    {
                        return true; // unknown reads as "still open": the watchdog must not close a window it cannot see
                    }
                }
            }

            // The name of the first sub-check that forbids a modal now, null when there is none; "exception" for a check that threw (a total predicate says no). The
            // name goes into the scheduler's "waiting" line, because a modal that never shows is otherwise undiagnosable. A raid that is running, or that failed
            // late, is covered by the AbstractGame check alone: ComebackToMainMenu releases that singleton, whereas InGameStatus.InRaid stays true after a start
            // that failed after GamePrepare and would refuse for the whole session (the modal itself is shown with forceShow for the same reason).
            static string FindModalBlocker()
            {
                try
                {
                    ItemUiContext ui = ItemUiContext.Instance;
                    if (ui == null)
                        return "ItemUiContext";
                    if (ui.MessageWindowActive)
                        return "MessageWindow";
                    if (ui.ScrolledMessageWindowActive)
                        return "ScrolledMessageWindow";
                    if (!MonoBehaviourSingleton<PreloaderUI>.Instantiated)
                        return "PreloaderUI";
                    PreloaderUI preloader = MonoBehaviourSingleton<PreloaderUI>.Instance;
                    if (preloader == null)
                        return "PreloaderUI";
                    if (preloader.UnknownErrorScreen != null && preloader.UnknownErrorScreen.isActiveAndEnabled)
                        return "UnknownErrorScreen";
                    if (preloader.ErrorScreen != null && preloader.ErrorScreen.gameObject.activeSelf)
                        return "ErrorScreen";
                    if (CriticalErrorScreenActive(preloader))
                        return "CriticalErrorScreen";
                    if (Singleton<AbstractGame>.Instantiated)
                        return "AbstractGame";
                    return null;
                }
                catch
                {
                    return "exception";
                }
            }

            // Critical error screens (SPT's beta disclaimer, EFT's connection errors) are ErrorScreen instances that the preloader instantiates under one
            // container: an active ErrorScreen child is one. Any other child of that container (a prefab's own helper object) is not.
            static bool CriticalErrorScreenActive(PreloaderUI preloader)
            {
                var container = preloader._criticalErrorScreenContainer;
                if (container == null)
                    return false;
                Transform root = container.transform;
                for (int i = 0; i < root.childCount; i++)
                {
                    Transform child = root.GetChild(i);
                    if (child.gameObject.activeSelf && child.GetComponent<ErrorScreen>() != null)
                        return true;
                }
                return false;
            }

            public bool ToastPossible
            {
                get
                {
                    try
                    {
                        return Singleton<NotificationManager>.Instantiated;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            public bool InStartedRaid
            {
                get
                {
                    try
                    {
                        return Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started && ArenaMapRaid.IsLoaded();
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            public string CurrentRaidMapKey
            {
                get
                {
                    try
                    {
                        return ArenaMapRaid.CurrentMap?.Key;
                    }
                    catch
                    {
                        return null;
                    }
                }
            }

            public bool MatchingCanceledByPlayer
            {
                get
                {
                    try
                    {
                        return MatchingOperation.MatchingCanceledByPlayer;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            // the class SkippedHooks and this property share a name: qualified on purpose
            public IReadOnlyList<string> SkippedHooks => ArenaMapPort.Client.SkippedHooks.MapCritical();

            public GateSnapshot ReadGate() => GateSnapshot.Read();

            public bool TryReadOffered(out IReadOnlyList<OfferedLocation> locations)
            {
                locations = null;
                try
                {
                    if (!Singleton<ClientApplication<IEftSession>>.Instantiated)
                        return false;
                    IEftSession session = Singleton<ClientApplication<IEftSession>>.Instance?.GetClientBackEndSession();
                    var table = session?.LocationSettings?.locations;
                    if (table == null)
                        return false;
                    var list = new List<OfferedLocation>(table.Count);
                    foreach (var pair in table)
                        if (pair.Value != null)
                            list.Add(new OfferedLocation(pair.Value.Id, pair.Value.Enabled));
                    locations = list;
                    return true;
                }
                catch
                {
                    locations = null;
                    return false;
                }
            }

            public FileScan ScanFiles(IReadOnlyList<MapInfo> scope)
            {
                if (_pluginDirectory == null)
                    _pluginDirectory = Path.GetDirectoryName(typeof(MapNotices).Assembly.Location) ?? ".";
                return MapNoticeInputs.ScanFiles(_pluginDirectory, scope, null);
            }

            public ProbeView Probe
            {
                get
                {
                    ServerStatusClient probe = _probe;
                    if (probe == null)
                        return ProbeView.NotAsked;
                    ProbeView view = probe.Result;
                    if (!_probeLogged && view.State != ProbeState.Pending && view.State != ProbeState.NotAsked)
                    {
                        _probeLogged = true;
                        Debug("[Notice] server status: " + probe.Detail); // the worker never logs: the game thread does
                    }
                    return view;
                }
            }

            public void StartProbe()
            {
                if (_probe != null)
                    return;
                _probe = new ServerStatusClient();
                _probe.Start();
            }

            public ModalTicket ShowModal(string caption, string text, bool scrolled)
            {
                double now = Now;
                try
                {
                    ItemUiContext ui = ItemUiContext.Instance;
                    if (ui == null)
                        return ModalTicket.FailedAt(now, "ItemUiContext is not available");

                    ModalTicket ticket = null;
                    bool returned = false;
                    bool answeredAtOnce = false;
                    // EFT accepts at once (and shows nothing) when a raid is running and forceShow is off; with forceShow it always shows. Either way an accept
                    // that arrives before ShowMessageWindow returned means no window was shown.
                    Action accepted = () =>
                    {
                        if (!returned)
                            answeredAtOnce = true;
                        else
                            ticket?.Accept(Now);
                    };

                    MessageWindow window = scrolled ? ui._scrolledMessageWindow : ui._messageWindow;
                    TextAlignmentOptions? before = ReadAlignment(window);
                    DialogWindowContext context;
                    if (scrolled)
                    {
                        SetAlignment(window, OurAlignment); // ShowScrolledMessageWindow has no alignment argument
                        context = ui.ShowScrolledMessageWindow(text, accepted, null, caption, 0f, true);
                    }
                    else
                    {
                        context = ui.ShowMessageWindow(text, accepted, null, caption, 0f, true, OurAlignment);
                    }
                    returned = true;

                    if (answeredAtOnce)
                    {
                        RestoreAlignment(window, before);
                        return ModalTicket.SuppressedAt(now);
                    }
                    if (context == null)
                    {
                        RestoreAlignment(window, before);
                        return ModalTicket.FailedAt(now, "EFT returned no window");
                    }

                    ticket = new ModalTicket(now);
                    // Declined (Esc, the close button, replaced by another window, a global close), closed silently or closed after OK: the window is gone. The ticket
                    // keeps the first answer, so OK stays "accepted" when OnClose follows it. EFT's other message windows share this window object: its alignment
                    // goes back to what it was, so a later confirmation is not left aligned, but only while it is still ours: a window that displaced ours has set
                    // its own alignment just before it declined ours (ItemUiContext.method_1), and that one must stay.
                    Action closed = () =>
                    {
                        ticket.Close(Now);
                        RestoreAlignment(window, before);
                    };
                    context.OnDecline += closed;
                    context.OnCloseSilent += closed;
                    context.OnClose += closed;
                    return ticket;
                }
                catch (Exception e)
                {
                    return ModalTicket.FailedAt(now, e.GetType().Name + ": " + e.Message);
                }
            }

            public bool ShowToast(string text)
            {
                if (!Singleton<NotificationManager>.Instantiated)
                    return false; // EFT drops a toast silently without its notification manager
                NotificationManager.DisplaySingletonWarningNotification(text, ENotificationDurationType.Long);
                return true;
            }

            static TextAlignmentOptions? ReadAlignment(MessageWindow window)
            {
                try
                {
                    return window?._message == null ? (TextAlignmentOptions?)null : window._message.alignment;
                }
                catch
                {
                    return null;
                }
            }

            static void SetAlignment(MessageWindow window, TextAlignmentOptions alignment)
            {
                try
                {
                    if (window != null)
                        window.MessageAlignment = alignment;
                }
                catch
                {
                    // an alignment that cannot be set leaves the text where EFT puts it; the notice is shown all the same
                }
            }

            // Puts the shared window's alignment back, unless something else set a different one in the meantime.
            static void RestoreAlignment(MessageWindow window, TextAlignmentOptions? before)
            {
                if (!before.HasValue)
                    return;
                TextAlignmentOptions? current = ReadAlignment(window);
                if (current.HasValue && current.Value == OurAlignment)
                    SetAlignment(window, before.Value);
            }

            public void Debug(string message) => Plugin.Log.LogDebug(message);

            public void Info(string message) => Plugin.Log.LogInfo(message);

            public void Warning(string message) => Plugin.Log.LogWarning(message);
        }
    }
}
