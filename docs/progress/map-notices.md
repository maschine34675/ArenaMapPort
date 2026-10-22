# In-game map notices - 2026-10-22

Design record for the in-game message that tells a player why the Arena maps are missing or failed to load. Written on `main`
(0.3.0 line), in four stages: server and shared, the Unity-free client core, the client glue, and these docs.
Status when written: all four are in the tree and unit-tested. **Host-verified on 2026-10-22** (21:07 to 21:27, GPU-2 and GPU-1, DLLs
`0.3.0+0dd0b2a` plus the uncommitted notice tree): V1, V2, V3 (as a server-side variant) and V5 passed, V4, V6, V7 and V8 were not run; see
"Host evidence". The "Host evidence" section at the
end is empty on purpose and is appended to, never rewritten. The sections below are the design as implemented (the spec of the same
day, three reviews and the choices the implementation stages made); [Not verified](#not-verified-runtime-facts-and-known-gaps) lists what
only a host raid can prove.

## Motivation

A player who had Arena installed wrote (in German) that "the maps do not show up". Since the Arena gate of 0.2.3
([arena-install-check.md](arena-install-check.md)) and the encrypted bundles of 0.3.0 ([bundle-encryption.md](bundle-encryption.md)),
"no maps" is a normal state of many installs, and until now every cause sat in a log file: the server registers nothing without a
verified Arena (one console line), the client serves no bundle (Error lines), EFT shows a generic "matching aborted" popup after a
failed load, and ArenaMode silently drops maps the server did not register. Nothing at the place where the player looks.

The most likely causes of "I have Arena, the maps do not show up":

- The F12 path (client) is visible and easy to set, but the SERVER decides what is registered. Its `config\arena-install.json` is a
  hidden, hand-made file.
- A server on another PC; Arena still updating or incomplete.
- A new client against a locked or stale server DLL (three maps show, Bowl is missing).
- A Steam-origin launcher entry under another name (see "Steam finding" below).

The feature names the cause and the fix at the main menu, in the player's own language, and keeps a support-grade trace (a ref code
in the window and one Info line in the log).

## Decisions

The spec's decision list (2026-10-22) and how the open points were settled (decisions of 2026-10-22, taken by the lead of this work):

1. **Client-side notice system.** The Unity glue is one class (`MapNotices`); the logic (input builder, selector, scheduler, texts,
   failure registry, parser) is Unity-free and linked into the xunit project. One state machine, driven from `Plugin.LateUpdate`.
2. **Primary display: a modal OK window** (`ItemUiContext.ShowMessageWindow`, `forceShow: true`, left aligned). A modal counts as
   delivered only when the player has seen it. Toasts carry only secondary or short states.
3. **Five triggers, one pipeline:** T1 the first main menu of the session (modal); Pending, the check still running 10 menu-seconds
   later (toast); T2 the first location selection while T1 found "nothing available" (toast with the short fix); T3 the next main menu after
   a failed Arena raid attempt (modal naming the cause); N1 a raid that started without NavMesh (toast in the raid).
4. **The reason comes from four sources:** the client's own Arena gate (machine-readable cause `ArenaInstallCause`); the server's reason via
   the new route `GET /arenamapport/status`, asked only when the client is verified and maps are missing from `LocationSettings`; a client
   file check at T1 (`File.Exists` only, no key resolution); a per-attempt load-failure record for stage 2 and bundle damage.
5. **Stage 2 explains after the failed load; it does not pre-resolve keys at the menu** (3 to 7.4 MB per map at every start for a rare
   failure; it would move the `[BundleKeys]` Error lines). What counts as a failed load is defined by an attempt latch, not by "any record".
6. **No change** to `EnsureVerified`, the gate texts or the pinned bundle-loading order. Notice code never blocks: it reads the gate
   without waiting.
7. **(a) No backport.** Implemented on `main` only; `release/0.2.3` is untouched. The reporter is on 0.2.3, the only downloadable
   version, so this reaches him with the next release from `main` (0.3.0). The spec's sketch of a 0.2.4 stage-1 backport is not adopted
   (see "Release vehicle").
8. **(b) One new public config key, `General/ShowMapNotices`** (bool, default true), in both allowlists, the README and the package texts.
9. **(c) English master plus German**, informal "du", chosen from EFT's UI language (German locale code `ge`, `LocalizationManager.GERMAN_LOCALIZATION` in the 0.16.9.5 decompile), English fallback,
   parity tests between the tables.
10. **(d) `{support}` is** `https://github.com/maschine34675/ArenaMapPort/issues`.
11. **(e) GPU-1 may be used for host checks** (V2, V3: no Arena there); the lead does all host work.
12. **(f) No ArenaMode or Bowl line** in README or release notes. The only statement kept is: no ArenaMapPort message means the
    ArenaMapPort side is fine. ArenaMode's own picker is not read and not changed (see "ArenaMode's own picker").

## Facts the design stands on

Decompile root `D:/SPT41/Development/_decompiled` (EFT 0.16.9.5). "Re-checked" means read while writing the spec, "report" means a
reviewer cited it. The glue stage re-checked the members it calls (list after the table).

| Fact | Where |
|---|---|
| Toast API. `DisplaySingletonWarningNotification(string, ENotificationDurationType)` dedupes equal text. `DisplayNotification` silently drops when `Singleton<NotificationManager>` is not instantiated. | `EFT.Communications/NotificationManager.cs:495-518`; `EFT.UI/NotifierView.cs:157-162` |
| Durations: `Default=0, Long=1, Infinite=2`. `Infinite` stays until clicked and stalls the FIFO queue. Absolute seconds are not in the decompile. | `ENotificationDurationType.cs`; `BaseNotificationView.cs:75-84` |
| The notification manager is created after login and recreated on every return to the menu. | `EFT/TarkovApplication.cs:1689-1698` |
| `ShowMessageWindow(description, acceptAction, cancelAction = null, caption = null, time = 0f, forceShow = false, alignment = Center)` returns a `DialogWindowContext`; it sets the window's `MessageAlignment` and calls `method_1`. | `EFT.UI/ItemUiContext.cs:1335-1338` |
| `method_1` `Decline()`s an open message window and an open scrolled window. With `InGameStatus.InRaid && !forceShow` it auto-accepts and shows nothing. Otherwise it calls `RequestGlobalClose()` and shows the window; a null cancel action gives one OK button. | `ItemUiContext.cs:1407-1438` |
| `DialogWindowContext` derives from `BaseWindowContext<bool>` with events `OnAccept`, `OnDecline`, `OnCloseSilent`; a displaced window gets `OnDecline`. | `BaseWindowContext.cs:42, 72, 102` |
| `InGameStatus.InRaid` is set at `TarkovApplication.cs:2802` and reset only at `:3053`, so it stays true after a start that fails past GamePrepare (ArenaMode resets it by hand). | report |
| `ErrorScreen.Show` and every `TryCloseScreen` call `ItemUiContext.CloseAllWindows()`; EFT shows its own message windows after `ShowScreenAsync` (PvE first-time, system messages). | report |
| `MenuScreen.Show(Profile, MatchmakerPlayersController, ESessionMode)` is on a non-generic class. The in-raid Esc menu is also a `MenuScreen` (screen type `MainMenu`) and calls `Show` with a null profile. | `EFT.UI/MenuScreen.cs:314`; report |
| `EEftScreenType.MainMenu` and `SelectLocation` exist; `EftScreenManager.Instance.CheckCurrentScreen(...)` is public. | `EEftScreenType.cs`; `ScreenManager.cs:697` |
| `PreloaderUI.ErrorScreen` and `UnknownErrorScreen` are public; `ShowErrorScreen` cuts at 500 characters; `PrepareGameJob` calls `CloseErrorScreen()` and then shows `_messageOnLoadMenu`. | `EFT.UI/PreloaderUI.cs`; `TarkovApplication.cs:1851-1856` |
| Failed load chain: `LocalGameMatching` swallows the failure and rethrows a cancel; `HandleError` shows the generic `Error/MatchingAborted` screen unless `MatchingOperation.MatchingCanceledByPlayer` is set, then runs `ComebackToMainMenu`. `InGameStatus.InRaid` is set only after `LoadMapAndData`, so the T3 modal can show. | `TarkovApplication.cs:2409-2602` |
| This mod already hooks the attempt boundaries: `LoadingScreenArt.ShowPostfix` (on `MatchmakerTimeHasCome.Show`) resolves `raidSettings.LocationId` against `ArenaMapRegistry.All`; `ComebackToMainMenuPrefix` is installed on `ComebackToMainMenu`. | `ArenaMapPort.Client/LoadingScreenArt.cs` |
| EFT's UI language: `Singleton<SettingsManager>.Instance.Game.Settings.Language.Value`. | `EFT.UI/WelcomeScreen.cs:162`; `GameSettingsTab.cs:78` |
| `LocationSettings.locations` is a `Dictionary<string, Location>` keyed by the 24-hex `_Id`; reach it via `Singleton<ClientApplication<IEftSession>>.Instance.GetClientBackEndSession().LocationSettings`. | `JsonType/LocationSettings.cs:243-257`; `EftClientBackendSession.cs` |
| SPT HTTP: `RequestHandler.GetJson` retries 3 times with an Error line per attempt. `RequestHandler.HttpClient` is a public static readonly field of type `Client`, with `CreateNewHttpRequest(HttpMethod, string)` and a public `HttpClient` property (cookie and the self-signed HTTPS bypass included). The server zlib-compresses responses. This is the SPT 4.1.6 client decompile (the build installed on this VM; the docs and the server decompile say 4.1.5). | scratchpad decompile of `spt-common`; report |
| SPT static-router pattern: ArenaMode's `[Injectable(TypePriority = OnLoadOrder.Routers + 1)] sealed class X : StaticRouter`; the route is unauthenticated. | `ArenaMode.Server/Routers/ArenaModeRouter.cs` (read only) |
| Server before this work: `ArenaInstallationGate.Evaluate` discarded the result and returned a bool; `OnLoadAsync` returned before touching anything when the gate failed; `ArenaMapPortModGateTests` runs it with every dependency null and asserts exactly one Info call. | `ArenaMapPort.Server/ArenaInstallationGate.cs`, `ArenaMapRegistration.cs` |
| Client before this work: `ArenaInstallationGate` had `Result`, `Verified`, `Reason`, `WaitForResult`, `EnsureVerified`, `PollOutcome` and no state enum; `BundleKeyResolver` kept only `{Ok, Key}` per map, so the `KeyResolution` cause was lost after logging. | `ArenaMapPort.Client` |
| F12 visibility is a second allowlist: `MapConfigVisibility.IsPlayerOption` and `CompactConfiguration.IsPublic` each held the same three keys. | `MapConfigVisibility.cs`, `CompactConfiguration.cs` |

EFT members the glue stage checked against the decompile (paths under the decompile root): `EFT.UI/ItemUiContext.cs` (`Instance` 962,
`MessageWindowActive` 1034, `ScrolledMessageWindowActive` 1036, `_messageWindow` 790, `_scrolledMessageWindow` 793, `ShowMessageWindow`
1335, `ShowScrolledMessageWindow` 1402, `method_1` 1407-1438); `BaseWindowContext.cs` (`OnAccept` 42, `OnDecline` 72, `OnCloseSilent`
102); `WindowContext.cs` (`OnClose` 45); `DialogWindow.cs` (`Close(ECloseState)` 158-183); `BaseMessageWindow.cs` (`_message` 19,
`MessageAlignment` 25); `PreloaderUI.cs` (`ErrorScreen` 137, `_criticalErrorScreenContainer` 143, `UnknownErrorScreen` 157);
`MonoBehaviourSingleton.cs` (`Instantiated` 8); `EFT.Communications/NotificationManager.cs` (`DisplaySingletonWarningNotification`
505); `EftScreenManager.cs` (`_instance` 76); `ScreenManager.cs` (`CheckCurrentScreen` 697); `EFT.Settings.Game/GameSettingsGroup.cs`
(`Language` 98); `EFT/InGameStatus.cs` (`InRaid` 8, obsolete, wrapped in a pragma); `MatchingOperation.cs`
(`MatchingCanceledByPlayer` 19); `ClientApplication.cs` (`GetClientBackEndSession` 82); `IEftSession.cs` (`LocationSettings` 36);
`JsonType/LocationSettings.cs` (`locations` 448, `Location.Id` 245, `Location.Enabled` 247); `EFT.UI/MenuScreen.cs` (`Show` 314);
`MatchMakerSelectionLocationScreen.cs` (`ShowInternal` 409). SPT: `RequestHandler.HttpClient` at line 14 of the 4.1.6 `RequestHandler.cs`.

## Failure states and notices

"Offered" means `LocationSettings.locations.Values.Any(l => l.Id == map.Key && l.Enabled)`. The registry is `ArenaMapRegistry.All`
(4 maps on `main`). Ref codes are printed in the footer of a modal and logged; a toast has no footer.

| # | State, and how the client detects it | Notice id (ref) | Kind | When |
|---|---|---|---|---|
| 1 | Gate `NotVerified`, cause `NotRegistered` | `ClientNoArena` (NO_ARENA) | modal | T1 |
| 2 | Gate `NotVerified`, cause `RegistryUnavailable` | `ClientRegistryUnreadable` (REGISTRY) | modal | T1 |
| 3 | `NotVerified`, source override, cause `FolderMissing`, `NotArenaFolder`, `ManifestMissing` or `ManifestInvalid` | `ClientOverrideBad` (OVERRIDE_BAD) | modal | T1 |
| 4 | Same causes, source registry | `ClientFolderBad` (FOLDER_BAD) | modal | T1 |
| 5 | `NotVerified`, cause `FilesMismatch` (either source) | `ClientIncomplete` (ARENA_INCOMPLETE) | modal | T1 |
| 6 | Check threw, `ArenaInstallPath` unreadable (`CheckFailed`), or state `NotStarted` at the first menu (`Initialize` failed in Awake, permanent) | `ClientCheckFailed` (CHECK_FAILED) | modal | T1 |
| 7 | Check still running at the first menu | `CheckPending` (PENDING), only after 10 menu-seconds | toast | Pending |
| 8 | Check timed out (a bundle request waited 30 s, `TimedOut`) | `ClientCheckSlow` (CHECK_SLOW) | modal | T3, or T1 if seen |
| 9 | A map-critical Harmony target was skipped by `HarmonyPatches.Verify` (`BundlesManager.LoadBundleAsync`, `BotsController.Init`) | `ClientHookMissing` (HOOK_MISSING) | modal | T1 |
| 10 | Verified, 0 of N offered, probe ok, server gate `not_verified` or `error` | `ServerNoArena` (SERVER_NO_ARENA), action chosen by the server's cause | modal | T1 |
| 11 | Verified, 0 offered, probe ok, gate `verified`, every map `failed` | `ServerAllFailed` (SERVER_ALL_FAILED) | modal | T1 |
| 12 | Verified, 0 offered, probe 404, probe `Unavailable` (no SPT HTTP types), probe `Unreadable` (a 200 whose schema or document this client cannot read: another server version), gate `not_run`, or probe ok but inconsistent | `ServerUnknown` (SERVER_UNKNOWN) | modal | T1 |
| 13 | Verified, 0 offered, probe timed out or failed | `ServerNoAnswer` (SERVER_NO_ANSWER) | modal | T1 |
| 14 | Verified, some offered, probe ok, some map `failed` | `ServerSomeFailed` (SERVER_PARTIAL_FAILED) | modal | T1 |
| 15 | Verified, some offered, any other cause (version skew, 404, unknown) | `ServerSomeMissing` (SERVER_PARTIAL) | modal | T1 |
| 16 | Verified, the maps to check (offered, or all registry maps when offered is unknown) lack a bundle, a sidecar or (for AMPB bundles) the key file | `InstallIncomplete` (FILES_MISSING) | modal | T1 |
| 17 | An Arena raid attempt for map M was refused by the gate (rows 1-6, 8) | the matching `Client*` text with the lead "{Map} could not be loaded" | modal | T3 |
| 18 | Key failure for M inside a failed attempt (stage 2) | `KeyUpdated`, `KeyArenaOlder`, `KeyDiffers`, `KeyUnreadable`, `KeyFileBad`, `KeyInternal` | modal | T3 |
| 19 | Bundle missing, invalid, unreadable or load failed inside a failed attempt | `BundleBad` (BUNDLE_BAD) | modal | T3 |
| 20 | An Arena attempt returned to the menu before `GameStatus.Started`, was not cancelled by the player, and has no record | `LoadNoCause` (NO_CAUSE) | modal | T3 |
| 21 | A raid started on M without NavMesh | `NavMeshMissing` (NAVMESH) | toast | N1 |
| 22 | A damaged chunk reported for a live scenes or preset stream, outside any failed attempt | `MapFileDamaged` (FILE_DAMAGED) | toast | next main menu, once per map and session |
| 23 | The location-selection screen opened while rows 1-13 hold | `SelectionReminder` (SELECTION) | toast | T2, once |

Rules:

- **Priority.** One T1 modal per pass, the first that matches: client gate (1-6), hook missing (9), server with none offered (10-13), server
  with some missing (14, 15), install incomplete (16). After the player fixes one problem the next shows at the next start; each text
  states only its own problem.
- **Nothing to say.** The client is verified, every registry key is offered and no file is missing: no notice, no HTTP request, one Debug line.
- **Unverified client.** A client that is not verified shows the client notice even when the server registered the maps (they then appear in
  the selection but would fail to load, so the player is told before a 14 s doomed load).
- **Silencing.** `ShowMapNotices=false` silences rows 1-16 (T1), 7 (Pending) and 23 (T2). Rows 17-22 are answers to something the player
  just did, or to a damaged install found in use, and always show.
- **Blocking and non-blocking records** (see "Attempts and T3"): rows 17-20 use only records that belong to a failed attempt; row 22 uses
  records outside any failed attempt, so a raid that ran fine never gets "could not be loaded".
- **Reminder groups (row 23).** The selection toast carries the remedy of the notice's own cause, so it never sends a player to a fix that
  does not apply (a review finding of 2026-10-22: one group for rows 3-6, 8, 9 told a slow check or a mismatching build to "repair Arena"). One
  group per remedy: `ArenaNotFound` (rows 1, 2), `ClientUnusable` (3, 4, 5), `ClientCheck` (6, 8: restart, then the log), `ClientBuild` (9: another
  ArenaMapPort build), `ServerNoVerify` (10 with an override-file cause: set or fix the server's arena-install.json), `ServerArena` (10 with a
  launcher-folder or files cause: repair or update Arena on the server PC), `ServerCheck` (10 with an unexpected failure: restart the server),
  `ServerNoOffer` (11, 12: reinstall the server's ArenaMapPort), `ServerNoAnswer` (13: restart the server, then the game). Rows 14-16 have none.

### ArenaMode's own picker

ArenaMode filters on the server's registration, so when the server registers nothing it silently drops the maps. T1 is independent of
ArenaMode and fires at the first main menu; T2 patches `MatchMakerSelectionLocationScreen.ShowInternal`, the same screen ArenaMode's
selection uses (a second postfix coexists with `SelectionMapArtwork`'s). ArenaMapPort never reads ArenaMode's own filtering: a map the
server offers but ArenaMode hides is not a failure on this side, so no notice. The README keeps one sentence: no ArenaMapPort message
means the ArenaMapPort side is fine. ArenaMode's in-screen "ARENA UNAVAILABLE" notice and its warning toasts are untouched.

## Mechanism

### Why a modal, and why a toast

**Modal** (`ItemUiContext.Instance.ShowMessageWindow(text, null, null, caption, 0f, forceShow: true, TextAlignmentOptions.TopLeft)`): no
side effects (no logout or quit callback, which is what ErrorScreen callbacks do in EFT); not closed by `PrepareGameJob`'s
`CloseErrorScreen`; EFT shows the same window itself right after the menu appears; no 500-character cap, no shared error queue.
`forceShow: true` bypasses the `InRaid` auto-accept that would swallow it after a failed start. The window is not guaranteed to survive
(it can be displaced). A text longer than 480 characters goes to `ShowScrolledMessageWindow`, which several common notices do (see the
list under "Texts as implemented": `ClientNoArena` and `ServerNoArena` in English, and most of the German table), so the scrolled window is not a
rare path and V2 and V3 verify it explicitly. The shared window's alignment is set to left before it is shown and put back when our window
closes, but only while it is still the one we set: a window that displaced ours sets its own alignment just before it declines ours.

**Rejected:** `PreloaderUI.ShowErrorScreen` and `ShowCriticalErrorScreen` (shared queue, 500-character cut, error sound, blank header via
`_messageOnLoadMenu`); `_messageOnLoadMenu` via a `HandleError` prefix (a private-field poke that only fits failed loads and ties us to EFT's
flow; it is the documented fallback if a host run shows the modal cannot appear after a failed load).

**Toast** (`NotificationManager.DisplaySingletonWarningNotification(text, ENotificationDurationType.Long)`): non-blocking, so only for
secondary states; the singleton variant drops an equal visible text; it silently drops when the manager is not instantiated, so
`Singleton<NotificationManager>.Instantiated` is waited on; never `Infinite` (an un-clicked toast stalls every later one, and SPT's own
plugin-error toast is `Infinite`). The user's NotificationFilter mod may rewrite durations.

### Components

| File | Unity-free (linked into the tests) | Role |
|---|---|---|
| `ArenaMapPort.Shared/ArenaStatusContract.cs` | yes (server, client, tests) | route `/arenamapport/status`, schema 1, `ArenaInstallCause` and its wire codes, the gate, override-file and map vocabularies |
| `ArenaMapPort.Server/ArenaServerStatus.cs` | server | static holder: `RecordGate`, `RecordGateError`, `RecordMap`, `Snapshot`, `ToJson` |
| `ArenaMapPort.Server/ArenaMapPortStatusRouter.cs` | server | the static router at `OnLoadOrder.Routers + 1`, in its own DI class |
| `ArenaMapPort.Client/MapNoticeText.cs` | yes | ids, ref codes, the `en` and `de` tables, composition, sanitising |
| `MapNoticeInputs.cs` | yes | pure builder: gate snapshot, offered keys or unknown, probe view, file-exists seam, skipped hooks, language to `MapAvailabilityInput`; the offered check, the cause-to-row mapping, `NeedsProbe`, the file scan |
| `MapNoticeSelector.cs` | yes | pure function from `MapAvailabilityInput` (or an attempt) to `MapNoticePlan` (table above, priority order) |
| `MapNoticeScheduler.cs` | yes | the state machine over an injectable `INoticeEnvironment`: menu clock, modal tickets, attempts |
| `MapLoadFailures.cs` | yes | thread-safe, bounded (256) per-map failure registry with sequence numbers, owned by `BundleKeyResolver` |
| `ServerStatusParser.cs` | yes (Newtonsoft) | JSON to `ServerStatusView`, zlib-aware body decode |
| `SkippedHooks.cs` | yes | registry of skipped Harmony targets; two are map-critical |
| `MapNotices.cs` | no | the only file that calls EFT's UI: `BindConfig`, `Install(Harmony)`, `Tick()`, the environment implementation |
| `ServerStatusClient.cs` | no | the quiet HTTP probe |

`INoticeEnvironment` (the seam between the scheduler and the game): `Now`, `NoticesEnabled`, `Language`, `ClientVersion`, `Maps`,
`MenuIsCurrent`, `SelectionIsCurrent`, `ModalPossible`, `ModalBlocker`, `MessageWindowOpen`, `ToastPossible`, `InStartedRaid`, `CurrentRaidMapKey`,
`MatchingCanceledByPlayer`, `SkippedHooks`, `ReadGate()`, `TryReadOffered(...)`, `ScanFiles(...)`, `Probe`, `StartProbe()`, `ShowModal(caption, text, scrolled)`,
`ShowToast(text)` and the three log methods. Every predicate is total: it returns false (or the neutral value) and logs nothing on any
exception, and an exception from one is never counted as a fault. `ModalPossible` is `ItemUiContext.Instance != null`, no message or
scrolled window open, `PreloaderUI` instantiated, no `UnknownErrorScreen`, `ErrorScreen` or critical error screen (an active `ErrorScreen`
child of the preloader's critical container, not any active child) and no `AbstractGame`. It does NOT read `InGameStatus.InRaid`: that flag stays
true after a start that failed after `GamePrepare` (`TarkovApplication.cs:2802` sets it, only `OnGameEnd` clears it), which would refuse a
modal for the whole session and make `forceShow` unreachable, while `ComebackToMainMenu` releases the `AbstractGame` singleton. `ModalBlocker`
names the sub-check that said no ("ErrorScreen", "MessageWindow", "AbstractGame", ...; "exception" for one that threw) and goes into the
"waiting" Debug line and the busy-budget Warning, so a modal that never shows is diagnosable. `MessageWindowOpen` is "an EFT message window is on
screen" (the watchdog below); an unreadable state reads as open.
`ModalTicket` records the show time and receives `OnAccept`, `OnDecline` and `OnClose` from the returned `DialogWindowContext`; a
synchronous accept inside `ShowModal` is reported as `Suppressed` (EFT showed nothing).

The client's gate gained read-only members and nothing else (no text or behaviour change, nothing bound, the gate test still pins one
`.Bind(`): `GateState { NotStarted, Running, TimedOut, CheckFailed, Verified, NotVerified }`, `State` (same lock and logic as `Reason`),
`FailureDetail`, and the registry diagnostic line below.

### Hooks (all non-generic, void postfixes or prefixes, no obfuscated names)

1. `EFT.UI.MenuScreen.Show(Profile, MatchmakerPlayersController, ESessionMode)`, resolved with `AccessTools` and passed through
   `HarmonyPatches.Verify` (it logs and skips instead of throwing). The postfix `MapNotices.OnMenuShown` ignores a call with no profile
   (the in-raid Esc menu is a `MenuScreen` too); otherwise it counts the show, wakes the scheduler and anchors the menu clock on the first one.
   The fallback of design section 3.3 for a show that does not fire after a failed load is a rising edge of "the main menu is the current
   screen" (see T3 below); `_messageOnLoadMenu` stays the documented fallback only if a host run shows the modal cannot appear at all.
2. `MatchMakerSelectionLocationScreen.ShowInternal(RaidSettings)`: the postfix sets a pending flag; the toast is shown by `Tick`.
3. Attempt boundaries: one guarded statement in each of `LoadingScreenArt.ShowPostfix` (`MapNotices.NoteAttemptStarted(map.Key)`, before
   `Show(map, ...)`) and `ComebackToMainMenuPrefix` (`MapNotices.NoteComeback()`). Nothing else in those methods changed.
4. `Plugin.LateUpdate` gets one plain statement after `PollOutcome();`: `MapNotices.Tick();` (no added `{ }` block: the order pin is a
   regex). Idle `Tick` is one volatile flag read; `_work` is true only after a menu show, an open attempt, a recorded failure, a pending
   selection reminder or a pending in-raid notice.
5. Installation isolation: `MapNotices.BindConfig(Config)` and `MapNotices.Install(harmony)` are each in their own try/catch in
   `Plugin.Awake` (one Error line, continue; Awake must never die in them, the 2026-10-12 incident), before `LogStartupBanner();`; the
   Harmony instance is therefore created earlier than before. Each `harmony.Patch` inside `Install` is individually guarded. If the menu
   postfix did not install, the scheduler polls `CheckCurrentScreen(MainMenu)` once per second and treats a rising edge as a menu show.

### Tick state machine (game thread only)

**Clocks.** The menu clock `MenuSeconds` advances only while `MenuIsCurrent` holds (two ticks in a row, at most 5 s per tick); time in the
hideout, a trader or a raid does not count. Every threshold is measured on it, anchored at the first accepted `OnMenuShown`.

| Constant | Value | Meaning |
|---|---|---|
| `SettleSeconds` | 2.0 | menu time since the latest menu show before a modal, so EFT's own first-menu windows claim their window first |
| `LocationsWait` | 30 menu-seconds | how long T1 waits for the backend's location list; then it proceeds with offered unknown (no server notice, a Debug line) |
| `PendingToastAfter` | 10 menu-seconds | the check is still running: `CheckPending` toast, once |
| `ProbeTimeout` | 5 s | the probe's own token |
| `SeenAfter` | 2 s | a window closed without OK after at least this long on screen counts as seen |
| `MaxModalAttempts` | 3 | displaced attempts per arming |
| `BlockedBudget` | 120 menu-seconds | waiting for a free window |
| `RetryDelay` | 1 menu-second | between two attempts to show a displaced modal |
| `SelectionGrace` | 5 s | the reminder is dropped this long after the screen opened if it never reported itself current |
| `MenuPollInterval` | 1 s | the polling fallback when the menu postfix did not install |
| `MaxConsecutiveFaults` | 3 | action faults in a row before the latch; also the errors in a row after which one step of a tick stops |
| `WindowGoneGrace` | 1 s | no EFT message window on screen for this long while our ticket is still pending: the window is gone, treated as closed |

**Modal delivery (all modals).** A modal is shown only when `ModalPossible`, `MenuIsCurrent` and the settle time hold. The ticket ends as
`Accepted` (seen), `Closed` after at least `SeenAfter` (seen, the player dismissed it), or `Closed` earlier / `Suppressed` (displaced: not
seen, the attempt counter increments). A displaced modal is re-queued. After `MaxModalAttempts` or `BlockedBudget` one Long toast with the
short text (the `SelectionReminder` form of the notice's own group, or the first sentence of the modal) is shown, and the Warning ("shown as a
toast after N displaced modal attempts", or "... after N menu-seconds with the message window busy (<blocker>)") is written only when the toast
really was shown: a latched or impossible toast, or one that EFT dropped, is a Debug line ("parked ... no toast"). The notice stays armed and
re-arms on every later `OnMenuShown` with fresh counters until it is seen. Every close path of EFT's message windows reaches our handlers
(`CloseWindows`, `Decline`, `AcceptAndClose`), but a ticket that stayed Pending would hold the delivery for the session and block every later
notice, the answers to a failed raid included, so a watchdog treats a window as closed when no EFT message window has been on screen for
`WindowGoneGrace` (it counts as seen only if it had been up for `SeenAfter`).

**T1, once per process (armed until seen):**

1. Wait for `MenuIsCurrent` and the settle time (the hideout is excluded because its current screen is not MainMenu).
2. Gate state: `NotStarted` is treated as `CheckFailed` (row 6); `Running` returns (at `PendingToastAfter` emit `CheckPending` once, if
   enabled, and keep waiting); otherwise build the input.
3. If the gate is verified, read `LocationSettings` (guard the singleton and a non-null session); still null after `LocationsWait`
   proceeds with offered unknown.
4. Build `MapAvailabilityInput` without the probe, including the file scan.
5. Nothing to say: finish with the Debug availability line.
6. `NoticesEnabled` false: Debug "not shown", finish, before any probe.
7. `NeedsProbe` (verified, some registry key not offered): start the probe once and wait up to `ProbeTimeout`.
8. `MapNoticeSelector.SelectMenu(input)` gives at most one modal; show it by the delivery rules. T1 is done only when the modal was seen.

**T2.** After T1 found "nothing available" (rows 1-13) a pending selection shows the `SelectionReminder` toast once. At display time
`SelectionIsCurrent` and `ToastPossible` must hold and no modal may be open; the flag is cleared when the selection screen is no longer
current, so a late toast never appears after the screen closed. The toast carries the cause-specific short fix.

**Attempts and T3.** `NoteAttemptStarted(key)` opens an attempt `{key, startSeq = Failures.CurrentSequence}` and supersedes an older one.
While one is open `Tick` polls once per frame, cheaply, for `InStartedRaid`; on success it closes as Succeeded. `NoteComeback()` (the
`ComebackToMainMenu` prefix) while an attempt is open and not Succeeded closes it as Failed with `endSeq`, and queues it for T3 with the
`MenuShowCount` at that moment.

- **Blocking records.** A record belongs to the failed attempt when its map matches, its sequence is in `(startSeq, endSeq]` and its kind is
  not NavMesh. `Failures.TakeBlocking(attempt)` returns those and consumes them; duplicates for one map collapse and the newest cause wins.
  If none exist, `MatchingOperation.MatchingCanceledByPlayer` is false and the attempt did not reach Started, the plan is `LoadNoCause`; if
  the flag is true there is no notice.
- **Non-blocking records.** NavMesh records are never blocking: they feed N1 and the per-map `LastNavMeshCause` (not consumed). Integrity
  records outside any failed attempt feed row 22.
- T3 shows one modal naming the map after a `MenuShown` newer than the queued count, then `MenuIsCurrent`, the settle time and
  `ModalPossible` must hold. When the menu postfix is installed but no show follows the comeback, the menu screen becoming current again (a
  rising edge, seen by a tick that saw it not current) releases the answer instead, after a fresh settle time (`[Notice] '<id>' released: the
  main menu is the current screen again and no menu show was seen`); a menu that was current all along is no edge. V4 shows which of the two
  releases happens. The generic EFT popup is closed by `PrepareGameJob` first, which the error-screen check covers. Every failed
  attempt shows again (bounded by the player retrying).

**N1.** `NavMeshInstaller.OnBotsControllerInit`, in the branch where `LoadOnce` returned null, records "raid without NavMesh" with the map
key (`NoteRaidWithoutNavMesh`). `Tick` shows the toast once the raid has `GameStatus.Started` and `ArenaMapRaid.CurrentMap.Key` equals the
recorded key; the record is cleared on a different map, at scene unload (`NoteRaidEnded`, whose hook `NavMeshInstaller` now registers before the
load, because a process whose NavMesh never loads never reached the old registration), at `ComebackToMainMenu` (a raid that aborted before it
started) or at raid end. `{why}` comes from the
non-consumed `LastNavMeshCause`, so each later raid on that map shows the toast with its reason.

**Row 22.** Records of kind Integrity outside any failed attempt produce the `MapFileDamaged` toast at the next main menu, once per map and
session.

### Fail-safe

Every entry point is wrapped (`Guard(what, action)`). `OnFault` counts only action faults (`ShowModal`, `ShowToast`, `Install`, a postfix
body); predicates are total and never count. The first fault per `what` gives one Warning; three consecutive action faults set `_disabled`
for T1, Pending, T2 and N1 for the session with one Warning; any successful action resets the counter, and the next `OnMenuShown` re-arms.
T3 is never disabled by this latch (it runs only after a failed attempt, which is bounded by the player). Each of the seven steps of a tick
(clock, attempt, raid toast, damage toast, T1, modals, selection reminder) also has its own fault counter, because the guard around the whole tick
would otherwise throw every frame for the session when one step had a bug the latch cannot stop (the latch gates only some steps): a step that
threw three times in a row logs one Warning ("the '<step>' step is stopped after 3 errors in a row, until the next main menu") and stops until the
next menu show; a stopped step's state no longer keeps the tick alive (`ComputeWork`), and a stopped clock idles the tick. The per-frame delegates
of the tick are built once, so a raid load or a menu wait allocates nothing per frame; the damage check keeps the tick alive only while the menu is
current or a damage record waits. `Failures.Note*` are called only
through the `NoteSafe` helper (a source pin forbids a bare `Failures.Note` in `BundleHost` and `NavMeshInstaller`; `LoadOnce` runs inside
EFT's `BotsController.Init` and `TryServe` inside `BundlesManager.LoadBundleAsync`).

Rules the code keeps: main thread only for every EFT API and every log call; no exception escapes into EFT's load chain; no generic-class
patch (the HideoutGame trap does not apply: all patched methods are on non-generic classes); never `EnsureVerified()` from notice code
(it can block 30 s); no scene-wide search anywhere in `Tick`.

## Server side

### Contract and recording

`ArenaMapPort.Shared/ArenaStatusContract.cs` is compiled into server, client and the BundleCrypt tool project (the tests see it through the
server reference). `ArenaInstallationResult` gained `Cause` through optional trailing constructor parameters (the 10-argument form stays
valid), and the internal `NotFound` factory takes a REQUIRED `cause`, so a forgotten failure site fails to compile. A test asserts that
`!Verified` implies `Cause != None` for every failure seam. `Reason` strings are unchanged.

| Site | Cause (wire code) |
|---|---|
| registry probe unavailable | `RegistryUnavailable` (`registry_unreadable`) |
| no registry entry | `NotRegistered` (`not_registered`) |
| folder missing, path is a file, no folder given, invalid path | `FolderMissing` (`folder_missing`) |
| no `EscapeFromTarkovArena.exe` | `NotArenaFolder` (`not_arena_folder`) |
| no `ConsistencyInfo` | `ManifestMissing` (`manifest_missing`) |
| too large, unparseable, unreadable, no version, no entries, conflict, floors | `ManifestInvalid` (`manifest_invalid`) |
| `mismatchCount > 0` | `FilesMismatch` (`files_mismatch`) |
| any exception catch | `CheckFailed` (`check_failed`) |
| verified | `None` (`none`) |

Gate state wire codes: `not_run`, `verified`, `not_verified`, `error`; override file: `absent`, `used`, `ignored`; map state:
`not_registered`, `registered`, `failed`; map cause: `none`, `gate`, `base_json_parse`, `base_json_missing`, `registration`.

`ArenaServerStatus` is a static, DI-free holder: `ArenaInstallationGate.Evaluate` writes the verdict (including the catch branch as
`error` and the override file's state), and the per-map body of `ArenaMapRegistration` (extracted as `RegisterMapAsync` over an internal
`MapRegistrationInputs` record, so the `RecordMap` sites are testable without the DI container; log texts and order unchanged) writes
`base_json_parse`, `base_json_missing`, `registration` and `registered`. When the gate fails every registry map is `not_registered` with
cause `gate`. Every writer is a plain write under a lock: no logging, no I/O, cannot throw. The server adds **no log line**.

`ArenaMapPortStatusRouter` is a static router in its own DI class at `OnLoadOrder.Routers + 1`, so it answers even when `OnLoadAsync`
returned at the gate. The comments and docs that said "the server is left exactly as SPT built it" gained one exception: this read-only
route.

### JSON, schema 1

```json
{"schema":1,"serverVersion":"0.3.0",
 "gate":{"state":"not_verified","cause":"not_registered","source":"none","manifestVersion":null,"mismatchCount":0,"overrideFile":"absent"},
 "maps":[{"key":"arena_equator","state":"not_registered","cause":"gate"}]}
```

`System.Text.Json`, camelCase, nulls written. The route needs no session, so anyone who can reach the port can read it: it carries codes,
counts and the three-valued override-file state only: no `Reason` text, no Arena install path, no server path of any kind and no hash. The
texts show the fixed relative path `SPT_Runtime\user\mods\ArenaMapPort\config\arena-install.json`, never the server's absolute path.
`serverVersion` is `ModMetadata.Version`. The client requires schema 1, ignores unknown fields and reads a missing or higher schema as
unavailable.

### Client probe (section 4.4 of the design: `ServerStatusClient`, quiet, off the main thread)

- **When:** only when the client is verified, `NoticesEnabled` is true and a registry key is missing from `LocationSettings`. The healthy path
  never sends a request.
- **How:** by reflection (the client has no `spt-common` reference): `AccessTools.TypeByName("SPT.Common.Http.RequestHandler")`, the public
  static field `HttpClient` (property fallback), `CreateNewHttpRequest(HttpMethod.Get, route)`, sent with the `Client`'s `HttpClient`
  property's `SendAsync` under its own 5 s `CancellationTokenSource`. Reusing SPT's `Client` gets the cookie and the self-signed HTTPS bypass.
  The shared `HttpClient.Timeout` is never changed, the request and response are disposed, and `GetJson` is never used (a 404 would cost 4
  attempts, 4 Error lines and two Info lines per call). A dedicated background thread `ArenaMapPort-StatusProbe`; the worker never logs
  and never touches Unity, it writes a volatile result that `Tick` reads and logs. A second constructor (type name and timeout) is a seam
  for the tests only; no setting reaches it.
- **Reading the response:** 200 is decoded (zlib header `0x78` with the first two bytes a multiple of 31: skip 2 bytes and
  `DeflateStream`; also gzip and plain), UTF-8, `ServerStatusParser.Parse` (Newtonsoft, EFT's own 13.0.1); 404 is `NotFound` (an older
  server without the route); a timeout or any other failure is `Failed`; missing types or a `TypeInitializationException` is `Unavailable`.

## Stage 2 records and the file check

**Record, then explain after the failed attempt.**

- `KeyEntry` in `BundleKeyResolver` keeps `Cause`, `Detail`, `BuiltFor`, `InstalledVersion`, `FailedPath` and the key file name from the
  `KeyResolution` (the Internal path keeps cause `Internal`); `Prepare` records each of its four refusals through `NoteSafe`, and the cached
  key failure is recorded again on every attempt (`NoKeyLine`, E9, repeats per attempt, so a record is made per attempt).
- `BundleHost.TryServe`: a private `NoteFailure(bundleName, kind, detail)` helper (never inline) is called as the first statement inside the
  `if (!ArenaInstallationGate.EnsureVerified())` block and before the `LogError` of the `File.Exists` branch, so the existing pins
  (`NotServedMessage(bundleName));\s*return;`, `message > gate && exists > message`, `out var prepared);\s*if (bundle == null) { return; }`) stay
  true. `LoadAssetBundle` records its exception and null-bundle branches. An unknown bundle name (a newer server's map) still returns silently.
- `BundleStreamRegistry.ReportError` records `Integrity`; a damaged chunk in the NavMesh bundle is recorded as a NavMesh cause, so it
  never blocks.
- `NavMeshInstaller.LoadOnce` records the gate refusal, the missing file, no NavMeshData and the catch branch with kind NavMesh before the
  existing `LogError` lines; the `LoadOnce` order pin and `_loadTried[map.Key] = true;` are untouched.
- `HarmonyPatches.Verify` records the names of skipped targets (`SkippedHooks.Record`); the two map-critical ones feed row 9.
- **No ahead-of-time key resolution at the menu.** If the early warning is ever wanted: `BundleKeyResolver.PreResolve(mapKey, presetPath)`,
  skipping maps whose preset has no AMPB magic, one map per frame after T1's settle time, summed derive time measured on GPU-2 first.

**Client file check at T1 (no key resolution).** For each map in scope (offered, or all registry maps when offered is unknown) `File.Exists`
checks its three bundles and its shader sidecar (the list `Plugin.LogExpectedFile` already uses). `keys\<map_key>.json` is required only
when the preset bundle starts with the AMPB magic (4 bytes read), so a development build with plaintext bundles gets no false notice.
`{n}` is the number of missing files and `{file}` the first one in a fixed order. A half-copied 3 GB ZIP is a common cause of "maps do not
show up"; `Plugin.LogExpectedFile` only logged a Warning at startup.

**Key-failure mapping used by the texts:**

| `KeyFailure` | Condition | Notice |
|---|---|---|
| `SizeDiffers` or `ContentDiffers` | installed Arena version newer than the key file's | `KeyUpdated` (ArenaMapPort needs an update) |
| `SizeDiffers` or `ContentDiffers` | installed older than the key file's | `KeyArenaOlder` (update Arena) |
| `SizeDiffers` or `ContentDiffers` | equal versions, or either unparseable | `KeyDiffers` (neutral: repair Arena, or update ArenaMapPort) |
| `ArenaFileUnreadable` | | `KeyUnreadable` |
| `KeyFileMissing` or `KeyFileInvalid` | | `KeyFileBad` |
| `Internal` | | `KeyInternal` |

Bundle problems (missing, not a valid container, unreadable, load failed) inside a failed attempt all map to `BundleBad`; an integrity record
outside a failed attempt maps to `MapFileDamaged` (row 22). Versions are compared component by component, because Arena's own versions have
five numbers (`0.6.1.0.48206`), which `System.Version` cannot parse; with it `KeyUpdated` and `KeyArenaOlder` would never have fired.

## Configuration

**New public entry `General/ShowMapNotices`** (bool, default true), bound on `Config` by `MapNotices.BindConfig(Config)` (its own try/catch).
Description (F12 and the file comment): "Shows an in-game message at the main menu when the Arena maps are not available, with the reason
and what to do. Turning it off only hides these messages: the Arena check, the server and the log lines stay as they are. Takes effect
when the next message would be shown."

- **Three places change together, or the key is archived or invisible:** `CompactConfiguration.IsPublic` (a literal; the
  `ConfigRetirementCheck` tool compiles that file on its own), `MapConfigVisibility.IsPlayerOption` (without it the key is `Browsable=No`
  in player mode, the trap the 0.2.3 integration hit with `ArenaInstallPath`), and the README settings text plus `REQUIREMENT_TEXT` and
  the package README settings text in `tools/release/make_release_zips.py`.
- **Why public and not `Plugin.Instance.Settings`:** in player mode `Settings` is the non-saving developer archive, so a player could not
  change it from F12 or the file they know; and a player who intentionally has the mod without Arena (an ArenaMode user, or someone who
  got it through a Forge dependency) needs a way to stop a modal at every start.
- **This is not a switch for the Arena check.** The check, the server registration and the bundle refusal do not read it. It silences T1,
  Pending and T2 only; T3, N1 and row 22 always show. Notice code never references `EnsureVerified`, and a source pin enforces that over every
  notice file. AGENTS.md's hard rules say so.
- **Cost:** the public file goes from three to four entries and F12's player controls from two normal ones to three. The tests and docs that
  said "three" or "two" were updated (`ReleaseConfigurationTests`, `tools/tests/ConfigRetirementCheck`, AGENTS.md).

## Logging

Client lines use the tag `[Notice]` only (BepInEx adds the source `maschine-ArenaMapPort`); server lines are unchanged and the server adds none.

| Level | Line |
|---|---|
| Info | `[Notice] showing '<id>' (modal\|toast) ref=<REF> client=<state>:<cause> server=<not_needed\|ok:<gate.state>/<gate.cause>\|404\|failed\|no_http> offered=<n>/<m>`. One per shown notice; the map-availability summary a support log needs; cause codes only; bounded by the number of notices. |
| Info | `[Arena] no registry entry; Uninstall subkeys matching Arena or Tarkov: <name>=<InstallLocation>, exe=<yes\|no>; ...` (client only, once, from `ArenaInstallationResult`, logged by the gate's `LogOutcome`; see "Steam finding") |
| Debug | `[Notice] availability: arena=<verified\|not_verified:<cause>\|pending\|failed>, offered=<n>/<m> [<keys>], files=<ok\|<n> missing>, server=...` |
| Debug | `[Notice] text: <text, one line, " \| " between lines>` |
| Debug | `[Notice] '<id>' not shown: General/ShowMapNotices is off` |
| Debug | `[Notice] waiting: <predicate>` (only when the reason changes) |
| Debug | `[Notice] server status: GET <route> -> <200 in N ms\|404\|timeout\|Type: msg>` |
| Debug | `[Notice] load failure noted: map=<key> kind=<kind> cause=<cause> seq=<n>` |
| Debug | `[Notice] attempt: map=<key> started\|succeeded\|failed records=<n>` |
| Warning | `[Notice] could not show '<id>' (Type: msg); the reason is in the [Arena] lines of this log` (once per id) |
| Warning | `[Notice] shown as a toast after <n> displaced modal attempts` |
| Warning | `[Notice] switched off for this session after 3 errors (Type: msg); the maps themselves are not affected` |

Existing lines are unchanged: the `[Arena]` Info lines, the `[BundleKeys]`, `[Bundles]` and `[NavMesh]` Errors, and the server lines. A key,
a hash or an Arena file content is never logged. Host logs need Debug for V1 and V2 to read the availability and waiting lines.

## Texts as implemented

Rules for every text: plain text, `\n` line breaks, no markup, at most 480 characters after substitution including the footer (longer
texts go to the scrolled window). Dynamic values are sanitised before substitution (`<` becomes `(`, `>` becomes `)`, because the window
renders TextMeshPro rich text unescaped). Every modal has the caption `ArenaMapPort` and the footer
`ArenaMapPort {ver} - ref {REF}` plus `, server {sv}` when the probe returned a server version, so a screenshot is enough for support. A toast
starts with `ArenaMapPort: ` and has no footer. No text says "copy protection" or "protect", contains a hash, or names Steam (the research
does not confirm a Steam-specific path). The language is German for EFT's German locale (`ge`, `LocalizationManager.GERMAN_LOCALIZATION` in
the 0.16.9.5 decompile; `de`, `deu`, `ger`, `german`, `deutsch` and regional forms are accepted too), English for everything else and as the fallback. The tests enforce the same ids and the same
placeholder set in both tables, and the lint rules (no leftover token, no `<`, no run of 32 or more hex characters, the key phrases where
required).

Texts over the 480 limit with TYPICAL values (the default Arena path of 46 characters, version 0.3.0, server 0.3.0; measured on the
composed text, footer included, on 2026-10-22 after the review fixes). They go to the scrolled window, so it is the ordinary path for the
first notice a player without Arena sees, not a rare one, and V2 and V3 verify it explicitly (layout, OK button, no clipping) and record which
window each case used; V2 also decides whether to raise the threshold. English: `ServerNoArena` with the "fix the ignored file" action (569) or
the "create the file" action (558), `ClientNoArena` as a main-menu message (518) and `ClientOverrideBad` with a bad launcher file list (487).
German: `ServerNoArena` "create the file" (643) and "fix the ignored file" (618), `ClientNoArena` as a main-menu message (584),
`ClientOverrideBad` with a bad file list (539), a missing exe (519) or a missing folder (502), `ServerUnknown` (527) and
`ClientRegistryUnreadable` (487, or 483 as the answer to a failed attempt); `ClientNoArena` as the answer to a failed attempt is 493. Every other text of both languages is
480 or less. Shortening was rejected: it would change the spec's texts and still leave German in the scrolled window.

<!-- GENERATED from ArenaMapPort.Client/MapNoticeText.cs by a script when this record was written; that file is the source of truth. -->

### Placeholders

`{lead}` is the lead sentence below; `{why}` and `{action}` are the sub-texts below; `{f12}` is `F12 > maschine-ArenaMapPort > General`; `{ovr}` is `SPT_Runtime\user\mods\ArenaMapPort\config\arena-install.json` (always this fixed relative path, never the server's absolute path); `{json}` is the pre-built example `{"installPath": "<the client's Arena path, backslashes doubled>"}`; `{support}` is `https://github.com/maschine34675/ArenaMapPort/issues`; `{Map}`, `{maps}`, `{path}`, `{file}`, `{n}`, `{detail}`, `{inst}`, `{built}`, `{ver}` are filled from the situation (`{detail}` is cut to 80 characters, `{path}` to 60 in the middle, `<` and `>` in any value become `(` and `)`). A value the plan did not supply reads `?`, never a raw token.

### Lead sentences (`{lead}`)

`Menu`

English:

```text
The Arena maps are not available
```

German:

```text
Die Arena-Karten sind nicht verfügbar
```

`Attempt`

English:

```text
{Map} could not be loaded
```

German:

```text
{Map} konnte nicht geladen werden
```

### Reason sentences (`{why}`)

`FolderMissing`

```text
en: the folder does not exist
de: der Ordner existiert nicht
```

`ExeMissing`

```text
en: EscapeFromTarkovArena.exe is not in it
de: EscapeFromTarkovArena.exe liegt nicht darin
```

`ManifestBad`

```text
en: its launcher file list is missing or does not look like Arena's
de: seine Launcher-Dateiliste fehlt oder sieht nicht nach Arena aus
```

`ServerNotRegistered`

```text
en: no launcher entry for Arena on the server PC
de: auf dem Server-PC gibt es keinen Launcher-Eintrag für Arena
```

`ServerRegistryUnreadable`

```text
en: the server cannot read the Windows registry
de: der Server kann die Windows-Registry nicht lesen
```

`ServerOverrideIgnored`

```text
en: its arena-install.json was ignored: it is not valid JSON or has no "installPath" text value
de: seine arena-install.json wurde ignoriert: kein gültiges JSON oder kein Textwert "installPath"
```

`ServerOverrideFolderMissing`

```text
en: the folder set in arena-install.json does not exist
de: der in arena-install.json eingetragene Ordner existiert nicht
```

`ServerOverrideNotArena`

```text
en: EscapeFromTarkovArena.exe is not in that folder
de: EscapeFromTarkovArena.exe liegt nicht in diesem Ordner
```

`ServerOverrideManifest`

```text
en: the launcher file list in that folder is missing or invalid
de: die Launcher-Dateiliste in diesem Ordner fehlt oder ist ungültig
```

`ServerFolderBad`

```text
en: the Arena folder the launcher lists on the server PC cannot be used
de: der Arena-Ordner, den der Launcher auf dem Server-PC nennt, kann nicht verwendet werden
```

`ServerFilesMismatch`

```text
en: its Arena files are incomplete or changed
de: seine Arena-Dateien sind unvollständig oder verändert
```

`ServerCheckFailed`

```text
en: its check failed unexpectedly
de: seine Prüfung ist unerwartet fehlgeschlagen
```

`MapDataBad`

```text
en: its map data is missing or damaged
de: seine Kartendaten fehlen oder sind beschädigt
```

`RegistrationFailed`

```text
en: registration failed
de: die Registrierung ist fehlgeschlagen
```

`NavUnverified`

```text
en: Arena was not verified
de: Arena wurde nicht verifiziert
```

`NavFileMissing`

```text
en: the file is missing
de: die Datei fehlt
```

`NavDamaged`

```text
en: the file is damaged
de: die Datei ist beschädigt
```

`NavNoKey`

```text
en: its unlock file could not be used
de: seine Entsperr-Datei konnte nicht verwendet werden
```

### Action lines of `ServerNoArena` (`{action}`)

`CreateOverride`

English:

```text
If the server runs on this PC, create {ovr} (create the folder config if needed; the name must end in .json, not .json.txt) with this content:
{json}
Then restart the server. If it runs on another PC, ask whoever runs it.
```

German:

```text
Wenn der Server auf diesem PC läuft, lege {ovr} an (erstelle bei Bedarf den Ordner config; der Name muss auf .json enden, nicht auf .json.txt) mit diesem Inhalt:
{json}
Starte danach den Server neu. Läuft er auf einem anderen PC, frage die Person, die ihn betreibt.
```

`FixIgnored`

English:

```text
The file {ovr} exists but could not be used, so it was ignored. Make it exactly like this, with every backslash in the path doubled (\\) or written as /:
{json}
Then restart the server.
```

German:

```text
Die Datei {ovr} existiert, konnte aber nicht verwendet werden und wurde deshalb ignoriert. Schreibe sie genau so, mit jedem Backslash im Pfad verdoppelt (\\) oder als /:
{json}
Starte danach den Server neu.
```

`FixOverridePath`

English:

```text
Fix the path in {ovr}, or delete that file to use the launcher's entry, then restart the server.
```

German:

```text
Korrigiere den Pfad in {ovr} oder lösche diese Datei, damit der Eintrag des Launchers genutzt wird, und starte dann den Server neu.
```

`FixServerFolder`

English:

```text
Reinstall or update Arena with the Battlestate Games launcher on the server PC, then restart the server.
```

German:

```text
Installiere oder aktualisiere Arena mit dem Battlestate-Games-Launcher auf dem Server-PC neu und starte dann den Server neu.
```

`FilesMismatch`

English:

```text
Let the Battlestate Games launcher finish updating Arena on the server PC, or check the installation there, then restart the server.
```

German:

```text
Lass den Battlestate-Games-Launcher das Update von Arena auf dem Server-PC beenden oder prüfe dort die Installation und starte dann den Server neu.
```

`CheckFailed`

English:

```text
Restart the SPT server. If this repeats, send its console lines starting with [ArenaMapPort] to {support}.
```

German:

```text
Starte den SPT-Server neu. Wenn das wieder passiert, schicke seine Konsolenzeilen, die mit [ArenaMapPort] beginnen, an {support}.
```

### Selection-reminder parts (`{short}` and `{fix}` of `SelectionReminder`)

`ArenaNotFound`

```text
en short: Arena was not found on this PC
en fix:   Install Arena with the launcher, or set F12 > ArenaInstallPath, then restart.
de short: Arena wurde auf diesem PC nicht gefunden
de fix:   Installiere Arena mit dem Launcher oder setze F12 > ArenaInstallPath und starte neu.
```

`ClientUnusable`

```text
en short: the Arena installation cannot be used
en fix:   Repair Arena or fix F12 > ArenaInstallPath, then restart.
de short: die Arena-Installation kann nicht verwendet werden
de fix:   Repariere Arena oder korrigiere F12 > ArenaInstallPath und starte neu.
```

`ServerNoOffer`

```text
en short: the SPT server does not offer them
en fix:   Reinstall the server's ArenaMapPort and restart the server.
de short: der SPT-Server bietet sie nicht an
de fix:   Installiere das ArenaMapPort des Servers neu und starte den Server neu.
```

`ServerNoVerify`

```text
en short: the server could not verify Arena
en fix:   Set or fix the server's arena-install.json (see the README), then restart it.
de short: der Server konnte Arena nicht verifizieren
de fix:   Lege die arena-install.json des Servers an oder korrigiere sie (siehe README) und starte ihn neu.
```

`ClientCheck`

```text
en short: the check of your Arena installation did not finish
en fix:   Restart the game; the [Arena] lines of BepInEx\LogOutput.log say more.
de short: die Prüfung deiner Arena-Installation wurde nicht fertig
de fix:   Starte das Spiel neu; die mit [Arena] markierten Zeilen in BepInEx\LogOutput.log sagen mehr.
```

`ClientBuild`

```text
en short: this ArenaMapPort build does not fit your game version
en fix:   Install the ArenaMapPort release for your SPT version, then restart.
de short: dieser ArenaMapPort-Build passt nicht zu deiner Spielversion
de fix:   Installiere die ArenaMapPort-Version für deine SPT-Version und starte neu.
```

`ServerNoAnswer`

```text
en short: the SPT server did not answer
en fix:   Restart the SPT server, then this game.
de short: der SPT-Server hat nicht geantwortet
de fix:   Starte den SPT-Server und danach dieses Spiel neu.
```

`ServerArena`

```text
en short: the server's Arena installation cannot be used
en fix:   Repair or update Arena on the server PC, then restart the server.
de short: die Arena-Installation des Servers kann nicht verwendet werden
de fix:   Repariere oder aktualisiere Arena auf dem Server-PC und starte den Server neu.
```

`ServerCheck`

```text
en short: the server's Arena check failed
en fix:   Restart the SPT server. If this repeats, see its console.
de short: die Arena-Prüfung des Servers ist fehlgeschlagen
de fix:   Starte den SPT-Server neu. Wenn das wieder passiert, sieh in seiner Konsole nach.
```

### Client Arena check (modals, main menu or failed attempt)

#### `ClientNoArena` (ref `NO_ARENA`, modal)

English:

```text
{lead}: ArenaMapPort did not find Escape from Tarkov: Arena on this PC.
Install Arena with the Battlestate Games launcher (the maps are built from its files), then restart the game and the SPT server.
Arena installed but not found? Set its folder in {f12} > ArenaInstallPath and in the server file arena-install.json (see the README).
To hide this message: {f12} > ShowMapNotices.   [main-menu messages only: dropped from the answer to a failed raid attempt]
```

German:

```text
{lead}: ArenaMapPort hat Escape from Tarkov: Arena auf diesem PC nicht gefunden.
Installiere Arena mit dem Battlestate-Games-Launcher (die Karten werden aus dessen Dateien gebaut) und starte dann das Spiel und den SPT-Server neu.
Arena ist installiert, wird aber nicht gefunden? Trage den Ordner in {f12} > ArenaInstallPath und in der Server-Datei arena-install.json ein (siehe README).
Um diese Meldung auszublenden: {f12} > ShowMapNotices.   [main-menu messages only: dropped from the answer to a failed raid attempt]
```

#### `ClientRegistryUnreadable` (ref `REGISTRY`, modal)

English:

```text
{lead}: ArenaMapPort could not read the Windows registry to look for Escape from Tarkov: Arena.
Restart the game once. If this repeats, set the Arena folder (the one with EscapeFromTarkovArena.exe) in {f12} > ArenaInstallPath and send the lines marked [Arena] from BepInEx\LogOutput.log to {support}.
```

German:

```text
{lead}: ArenaMapPort konnte die Windows-Registry nicht lesen, um Escape from Tarkov: Arena zu finden.
Starte das Spiel einmal neu. Wenn das wieder passiert, trage den Arena-Ordner (den mit EscapeFromTarkovArena.exe) in {f12} > ArenaInstallPath ein und schicke die mit [Arena] markierten Zeilen aus BepInEx\LogOutput.log an {support}.
```

#### `ClientOverrideBad` (ref `OVERRIDE_BAD`, modal)

English:

```text
{lead}: the folder set in {f12} > ArenaInstallPath cannot be used ({why}).
Set it to the Arena folder that contains EscapeFromTarkovArena.exe (not the launcher folder), or clear it to look Arena up automatically. Restart the game afterwards.
To hide this message: {f12} > ShowMapNotices.   [main-menu messages only: dropped from the answer to a failed raid attempt]
```

German:

```text
{lead}: Der in {f12} > ArenaInstallPath eingetragene Ordner kann nicht verwendet werden ({why}).
Trage den Arena-Ordner ein, der EscapeFromTarkovArena.exe enthält (nicht den Launcher-Ordner), oder leere das Feld, damit Arena automatisch gesucht wird. Starte danach das Spiel neu.
Um diese Meldung auszublenden: {f12} > ShowMapNotices.   [main-menu messages only: dropped from the answer to a failed raid attempt]
```

#### `ClientFolderBad` (ref `FOLDER_BAD`, modal)

English:

```text
{lead}: the launcher lists Arena at {path}, but {why}.
Reinstall or update Arena in the Battlestate Games launcher (or fix the folder if you moved it), then restart the game and the SPT server.
```

German:

```text
{lead}: Der Launcher meldet Arena unter {path}, aber {why}.
Installiere oder aktualisiere Arena im Battlestate-Games-Launcher neu (oder korrigiere den Ordner, falls du ihn verschoben hast) und starte dann das Spiel und den SPT-Server neu.
```

#### `ClientIncomplete` (ref `ARENA_INCOMPLETE`, modal)

English:

```text
{lead}: your Arena installation at {path} looks incomplete or is still updating (files missing or changed: {n}).
Let the launcher finish updating, or check the installation there, then restart the game and the SPT server. Arena has to be complete and up to date.
```

German:

```text
{lead}: Deine Arena-Installation unter {path} wirkt unvollständig oder wird noch aktualisiert (fehlende oder geänderte Dateien: {n}).
Lass den Launcher das Update beenden oder prüfe dort die Installation und starte dann das Spiel und den SPT-Server neu. Arena muss vollständig und aktuell sein.
```

#### `ClientCheckFailed` (ref `CHECK_FAILED`, modal)

English:

```text
{lead}: ArenaMapPort could not check your Arena installation because of an internal error ({detail}).
Restart the game. If this repeats, send the lines marked [Arena] from BepInEx\LogOutput.log to {support}.
```

German:

```text
{lead}: ArenaMapPort konnte deine Arena-Installation wegen eines internen Fehlers nicht prüfen ({detail}).
Starte das Spiel neu. Wenn das wieder passiert, schicke die mit [Arena] markierten Zeilen aus BepInEx\LogOutput.log an {support}.
```

#### `ClientCheckSlow` (ref `CHECK_SLOW`, modal)

English:

```text
{lead}: ArenaMapPort's check of your Arena installation did not finish in time (slow disk or unreachable folder).
Start the raid again. If this repeats, check that the Arena folder, or the folder set in {f12} > ArenaInstallPath, is on a local, connected drive.
```

German:

```text
{lead}: Die Prüfung deiner Arena-Installation durch ArenaMapPort wurde nicht rechtzeitig fertig (langsame Platte oder nicht erreichbarer Ordner).
Starte den Raid erneut. Wenn das wieder passiert, prüfe, ob der Arena-Ordner oder der in {f12} > ArenaInstallPath eingetragene Ordner auf einem lokalen, verbundenen Laufwerk liegt.
```

#### `ClientHookMissing` (ref `HOOK_MISSING`, modal)

English:

```text
{lead}: this ArenaMapPort build does not fit your game version (part {detail} could not be attached).
Install the ArenaMapPort release that matches your SPT version and restart the game. If that does not help, report it at {support}.
```

German:

```text
{lead}: Dieser ArenaMapPort-Build passt nicht zu deiner Spielversion (Teil {detail} konnte nicht angebunden werden).
Installiere die ArenaMapPort-Version, die zu deiner SPT-Version passt, und starte das Spiel neu. Hilft das nicht, melde es unter {support}.
```

### Server and installed files (modals, main menu)

#### `ServerNoArena` (ref `SERVER_NO_ARENA`, modal)

English:

```text
The Arena maps are not offered: the SPT server could not verify Escape from Tarkov: Arena ({why}), although this game found it.
{action}
```

German:

```text
Die Arena-Karten werden nicht angeboten: Der SPT-Server konnte Escape from Tarkov: Arena nicht verifizieren ({why}), obwohl dieses Spiel es gefunden hat.
{action}
```

#### `ServerUnknown` (ref `SERVER_UNKNOWN`, modal)

English:

```text
The SPT server does not offer the Arena maps and did not say why: its ArenaMapPort mod is missing, older than this game's ({ver}), or could not find Arena.
Close the server, reinstall both parts of ArenaMapPort from the same ZIP, then restart the server and this game. The server console shows the reason in lines starting with [ArenaMapPort]. Server on another PC? Ask whoever runs it.
```

German:

```text
Der SPT-Server bietet die Arena-Karten nicht an und hat nicht gesagt, warum: Sein ArenaMapPort fehlt, ist älter als das ArenaMapPort dieses Spiels ({ver}) oder konnte Arena nicht finden.
Schließe den Server, installiere beide Teile von ArenaMapPort aus demselben ZIP neu und starte dann den Server und dieses Spiel neu. Die Server-Konsole nennt den Grund in Zeilen, die mit [ArenaMapPort] beginnen. Läuft der Server auf einem anderen PC? Frage die Person, die ihn betreibt.
```

#### `ServerNoAnswer` (ref `SERVER_NO_ANSWER`, modal)

English:

```text
The Arena maps are not offered, and the SPT server did not answer ArenaMapPort's question why (timeout or error).
Restart the SPT server, then this game. If this repeats, the server console shows the reason in lines starting with [ArenaMapPort].
```

German:

```text
Die Arena-Karten werden nicht angeboten, und der SPT-Server hat ArenaMapPorts Frage nach dem Grund nicht beantwortet (Zeitüberschreitung oder Fehler).
Starte den SPT-Server und danach dieses Spiel neu. Wenn das wieder passiert, nennt die Server-Konsole den Grund in Zeilen, die mit [ArenaMapPort] beginnen.
```

#### `ServerAllFailed` (ref `SERVER_ALL_FAILED`, modal)

English:

```text
The SPT server could not register any Arena map ({why}).
Close the server, reinstall the ArenaMapPort server files (SPT_Runtime\user\mods\ArenaMapPort) from the ZIP and restart it. The console shows the reason in lines starting with [ArenaMapPort].
```

German:

```text
Der SPT-Server konnte keine Arena-Karte registrieren ({why}).
Schließe den Server, installiere die ArenaMapPort-Serverdateien (SPT_Runtime\user\mods\ArenaMapPort) aus dem ZIP neu und starte ihn wieder. Die Konsole nennt den Grund in Zeilen, die mit [ArenaMapPort] beginnen.
```

#### `ServerSomeMissing` (ref `SERVER_PARTIAL`, modal)

English:

```text
The SPT server does not offer {maps}, which this game knows. The server's ArenaMapPort is probably older than this game's ({ver}).
Close the SPT server (a running server keeps its DLL locked), copy the SPT_Runtime folder of the same ZIP over the installation (overwrite its files), then restart it. Console lines starting with [ArenaMapPort] show details.
```

German:

```text
Der SPT-Server bietet {maps} nicht an, dieses Spiel kennt sie aber. Das ArenaMapPort des Servers ist wahrscheinlich älter als das dieses Spiels ({ver}).
Schließe den SPT-Server (ein laufender Server sperrt seine DLL), kopiere den Ordner SPT_Runtime aus demselben ZIP über die Installation (vorhandene Dateien überschreiben) und starte ihn neu. Konsolenzeilen, die mit [ArenaMapPort] beginnen, zeigen Details.
```

#### `ServerSomeFailed` (ref `SERVER_PARTIAL_FAILED`, modal)

English:

```text
The SPT server could not register {maps} ({why}).
Close the server, reinstall the ArenaMapPort server files (SPT_Runtime\user\mods\ArenaMapPort) from the ZIP, and restart it. Console lines starting with [ArenaMapPort] show the reason.
```

German:

```text
Der SPT-Server konnte {maps} nicht registrieren ({why}).
Schließe den Server, installiere die ArenaMapPort-Serverdateien (SPT_Runtime\user\mods\ArenaMapPort) aus dem ZIP neu und starte ihn neu. Konsolenzeilen, die mit [ArenaMapPort] beginnen, zeigen den Grund.
```

#### `InstallIncomplete` (ref `FILES_MISSING`, modal)

English:

```text
ArenaMapPort's files for {maps} are incomplete (missing: {n}, for example {file}).
Extract the complete ZIP again (client folder BepInEx\plugins\maschine-ArenaMapPort, with its bundles and keys), then restart the game.
```

German:

```text
Die Dateien von ArenaMapPort für {maps} sind unvollständig (fehlend: {n}, zum Beispiel {file}).
Entpacke das komplette ZIP erneut (Client-Ordner BepInEx\plugins\maschine-ArenaMapPort mit seinen Bundles und Keys) und starte dann das Spiel neu.
```

### Toasts

#### `CheckPending` (ref `PENDING`, toast)

English:

```text
still checking your Arena installation (slow disk or unreachable path). The Arena maps load once it passes.
```

German:

```text
Deine Arena-Installation wird noch geprüft (langsame Platte oder nicht erreichbarer Pfad). Die Arena-Karten laden, sobald die Prüfung besteht.
```

#### `SelectionReminder` (ref `SELECTION`, toast)

English:

```text
the Arena maps are not available ({short}). {fix}
```

German:

```text
Die Arena-Karten sind nicht verfügbar ({short}). {fix}
```

#### `NavMeshMissing` (ref `NAVMESH`, toast)

English:

```text
{Map}: the navigation data could not be loaded ({why}); bots will not move. Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [NavMesh].
```

German:

```text
{Map}: Die Navigationsdaten konnten nicht geladen werden ({why}); Bots bewegen sich nicht. Installiere den kompletten Client-Ordner neu. Details: BepInEx\LogOutput.log, Zeilen [NavMesh].
```

#### `MapFileDamaged` (ref `FILE_DAMAGED`, toast)

English:

```text
a map file for {Map} reported damage during the raid ({file}). Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [Bundles].
```

German:

```text
Eine Kartendatei für {Map} hat während des Raids eine Beschädigung gemeldet ({file}). Installiere den kompletten Client-Ordner neu. Details: BepInEx\LogOutput.log, Zeilen [Bundles].
```

### Failed loads and stage 2 (modals, shown at the main menu after the failed attempt)

#### `KeyUpdated` (ref `KEY_UPDATED`, modal)

English:

```text
{Map} cannot be opened: Arena {inst} is installed, but this ArenaMapPort was built for Arena {built}, and files it needs have changed.
Install the newest ArenaMapPort release, which includes the matching unlock files, then restart the game.
```

German:

```text
{Map} lässt sich nicht öffnen: Arena {inst} ist installiert, aber dieses ArenaMapPort wurde für Arena {built} gebaut, und Dateien, die es braucht, haben sich geändert.
Installiere die neueste ArenaMapPort-Version, die die passenden Entsperr-Dateien enthält, und starte dann das Spiel neu.
```

#### `KeyArenaOlder` (ref `KEY_ARENA_OLDER`, modal)

English:

```text
{Map} cannot be opened: this ArenaMapPort was built for Arena {built}, but Arena {inst} is installed.
Update Arena in the Battlestate Games launcher, then restart the game.
```

German:

```text
{Map} lässt sich nicht öffnen: Dieses ArenaMapPort wurde für Arena {built} gebaut, aber Arena {inst} ist installiert.
Aktualisiere Arena im Battlestate-Games-Launcher und starte dann das Spiel neu.
```

#### `KeyDiffers` (ref `KEY_DIFFERS`, modal)

English:

```text
{Map} cannot be opened: the Arena files it is built from do not match ArenaMapPort's unlock file (Arena {inst}).
Repair Arena in the Battlestate Games launcher (damaged or modified files), then restart the game. If it stays this way, your Arena version needs an ArenaMapPort update.
```

German:

```text
{Map} lässt sich nicht öffnen: Die Arena-Dateien, aus denen sie gebaut ist, passen nicht zur Entsperr-Datei von ArenaMapPort (Arena {inst}).
Repariere Arena im Battlestate-Games-Launcher (beschädigte oder veränderte Dateien) und starte dann das Spiel neu. Wenn es dabei bleibt, braucht deine Arena-Version ein ArenaMapPort-Update.
```

#### `KeyUnreadable` (ref `KEY_UNREADABLE`, modal)

English:

```text
{Map} cannot be opened: ArenaMapPort cannot read an Arena file it needs (it is locked or access is denied).
Close the launcher and any scan of the Arena folder, check the folder's permissions, then restart the game.
```

German:

```text
{Map} lässt sich nicht öffnen: ArenaMapPort kann eine benötigte Arena-Datei nicht lesen (sie ist gesperrt oder der Zugriff wurde verweigert).
Schließe den Launcher und jeden Scan des Arena-Ordners, prüfe die Berechtigungen des Ordners und starte dann das Spiel neu.
```

#### `KeyFileBad` (ref `KEY_FILE_BAD`, modal)

English:

```text
{Map} cannot be opened: ArenaMapPort's unlock file for it ({file}) is missing or damaged.
Reinstall ArenaMapPort and copy the complete client folder (BepInEx\plugins\maschine-ArenaMapPort, including its keys folder), then restart the game.
```

German:

```text
{Map} lässt sich nicht öffnen: Die Entsperr-Datei von ArenaMapPort dafür ({file}) fehlt oder ist beschädigt.
Installiere ArenaMapPort neu und kopiere den kompletten Client-Ordner (BepInEx\plugins\maschine-ArenaMapPort samt Ordner keys), dann starte das Spiel neu.
```

#### `KeyInternal` (ref `KEY_INTERNAL`, modal)

English:

```text
{Map} cannot be opened because of an internal error while reading your Arena files ({detail}).
Restart the game. If this repeats, send the lines marked [BundleKeys] from BepInEx\LogOutput.log to {support}.
```

German:

```text
{Map} lässt sich nicht öffnen: Beim Lesen deiner Arena-Dateien ist ein interner Fehler aufgetreten ({detail}).
Starte das Spiel neu. Wenn das wieder passiert, schicke die mit [BundleKeys] markierten Zeilen aus BepInEx\LogOutput.log an {support}.
```

#### `BundleBad` (ref `BUNDLE_BAD`, modal)

English:

```text
{Map} could not be loaded: one of ArenaMapPort's map files is missing or damaged ({file}).
Reinstall ArenaMapPort and copy the complete client folder (bundles and keys included), then restart the game. Details: BepInEx\LogOutput.log, lines with [Bundles].
```

German:

```text
{Map} konnte nicht geladen werden: Eine der Kartendateien von ArenaMapPort fehlt oder ist beschädigt ({file}).
Installiere ArenaMapPort neu und kopiere den kompletten Client-Ordner (Bundles und Keys inklusive), dann starte das Spiel neu. Details: BepInEx\LogOutput.log, Zeilen mit [Bundles].
```

#### `LoadNoCause` (ref `NO_CAUSE`, modal)

English:

```text
{Map} did not start, and ArenaMapPort recorded no cause for it.
Check BepInEx\LogOutput.log (lines marked [Arena], [Bundles], [BundleKeys], [NavMesh]) and the game's Logs folder (the errors.log of the newest session), and send them to {support}.
```

German:

```text
{Map} wurde nicht gestartet, und ArenaMapPort hat dafür keine Ursache aufgezeichnet.
Sieh in BepInEx\LogOutput.log (Zeilen mit [Arena], [Bundles], [BundleKeys], [NavMesh]) und im Logs-Ordner des Spiels (die errors.log der neuesten Sitzung) nach und schicke sie an {support}.
```

## Implementation choices and deviations from the spec

The spec is the design of 2026-10-22; the stages made these choices where it was silent or where reading the code showed a problem.
Stage 1 is server and shared, stage 2 the Unity-free client core, stage 3 the glue.

**Stage 1 (server and shared):**

- A failed result without a cause (a test double, never a real check) is sent as `check_failed`, not `none`. An unknown map state or cause
  is recorded as `failed` with cause `registration`.
- `maps` in the JSON always lists every registry map: a map with no record shows `not_registered` with cause `none`; an empty holder says
  gate `not_run`.
- `RegisterMapAsync` builds its inputs once after the gate passes; the config properties were previously read inside each map's try block.
  Review fix of 2026-10-22: building the inputs is in its own try/catch in `OnLoadAsync`, which writes the same per-map "registration failed"
  Error and `registration` status record the old per-map catch did, so a config object that throws cannot abort `OnLoadAsync` (the locale, weather
  and APBS steps after the loop). The input property is named `CloneScavRaidTime` because a record cannot have a member called `Clone`.
- The "no registry entry" branch of `ArenaInstallation` also fills `RegistryCandidates` (matching Uninstall subkey names, their
  `InstallLocation`, whether the exe exists there, at most 8); `DescribeRegistryCandidates()` is the log fragment ("none" when nothing matched).
  Review fix of 2026-10-22: the exe is never probed on a UNC path or a mapped network drive (`ArenaInstallation.IsNetworkLocation`; the fragment says
  `exe=not checked`), because a stale entry pointing at an unreachable share blocks a file probe for 20 s or more and the server runs the same
  `Detect` on its startup thread (it discards the candidates). Local paths and unknown drive letters are probed as before.
- The contract is linked into the server, the client and the BundleCrypt tool project (and `bundle_crypt.py`'s source list with its
  `BuildWiringTests` assertion), but not into the test project: a second link would give a CS0436 warning.

**Stage 2 (client core):**

- `INoticeEnvironment` has more members than spec section 3.2 lists (see "Components"): the glue maps window callbacks to
  `ModalTicket.Accept` and `Close`, a synchronous accept to `SuppressedAt`, and exceptions to `FailedAt`.
- The placeholder pattern is `\{([A-Za-z][A-Za-z0-9]*)\}`: the spec's `\{[A-Za-z]+\}` cannot match `{f12}`, so a leftover token would have
  shown to players (a test caught it).
- The "To hide this message" line is a `MenuOnly` line: dropped from the answers to a failed raid attempt, because `ShowMapNotices` never
  silences those. The `>` lint allows `>` only as the " > " separator in the F12 path; `<` never survives.
- The fault latch lifts at the next menu show. Row 22 toasts are not gated by the latch (the spec lists only T1, Pending, T2 and N1).
- Invented server wording: the server's registry-source folder causes (`folder_missing`, `not_arena_folder`, `manifest_missing`,
  `manifest_invalid` with the override file absent) had no text in the spec; they use `NoticeWhy.ServerFolderBad` and
  `NoticeAction.FixServerFolder` ("Reinstall or update Arena with the Battlestate Games launcher on the server PC, then restart the server").
- The notice enums and `GateState` are `public` so xunit theories can take them.
- The parser uses Newtonsoft because the client already references EFT's own 13.0.1 and six client files use it at runtime; it decodes
  zlib, gzip and plain bodies, caps the size, and is tested with real `ZLibStream` output.
- `MapLoadFailures` is capped at 256 records, and notice code never calls `EnsureVerified`.

**Stage 3 (glue):**

- The registry diagnostic Info line is logged from the gate's `LogOutcome` (which `Initialize`, `PollOutcome` and the first bundle request
  all go through), not from `PollOutcome` as the spec said: `PollOutcome` only runs when the startup wait expired, so almost no log would
  have carried the line. It still logs once, client only.
- `ShowModal` restores the shared message window's previous alignment when its own window closes, because EFT's other windows share the object,
  but only while the alignment is still the one it set (review fix of 2026-10-22): `ItemUiContext.method_1` declines our window after the displacing
  caller set its own alignment, and a blind restore overwrote that one. The one remaining case, a displacing caller that also asks for left
  alignment, gets the previous one back; it is cosmetic.
- `MenuIsCurrent` follows the spec literally (no `AbstractGame` check; `ModalPossible` has it), so the fallback toast can still fire if a
  game object lingers in the menu. It reads `EftScreenManager._instance`, never `Instance` (the getter would create a screen manager).
- The fallback of spec 3.3 item 1 (a `MenuScreen.Show` that does not fire after a failed load) is implemented as a rising edge of
  `MenuIsCurrent` (review fix of 2026-10-22; see T3), not as a subscription to `EftScreenManager`'s screen-changed event: the scheduler already
  reads the current screen every tick while it has work, so the edge needs no second hook. A menu that was current all along gets no edge. The
  `_messageOnLoadMenu` fallback of spec 3.1 is deferred: it is conditional on a host run showing that the modal cannot appear after a failed load.
- `Plugin.Awake` creates the Harmony instance before the banner so `BindConfig` and `Install` can each sit in their own try/catch ahead of it.
- `MapNotices.cs` is the only file that calls EFT's UI; `ServerStatusClient` reflects into SPT's `RequestHandler` (4.1.6 surface, decompiled
  in the scratchpad), never `GetJson`, never logs from its worker thread.

## Tests

Build and test results at the end of stage 4 are in the "Verification at the end of stage 4" block below. All new Unity-free client files
are linked into `ArenaMapPort.Server.Tests` (csproj `Compile Include` rows with `Link="Client\..."`, `Newtonsoft.Json` 13.0.1 added for
the parser); the glue (`MapNotices.cs`, `ServerStatusClient.cs`) cannot run in xunit because it needs EFT, so it is pinned in source and
unit-tested through the core.

- **Cause mapping and the registry:** `ArenaInstallationTests` (one test per failure site of the cause table, registry-candidate tests),
  `ArenaStatusContractTests`.
- **Server:** `ArenaServerStatusTests` (golden JSON for a verified server, an unverified one and a failed map; no `Reason` text, install
  path or server path; stable wire codes; the router; the mod gate), `MapRegistrationOutcomeTests` (the extracted per-map body: `base_json_parse`,
  `base_json_missing`, `registration`, `registered`), `ArenaInstallationGateTests` and `ArenaMapPortModGateTests` (joined the
  `ServerStatusHolder` collection and reset the holder, because the holder is static; the Info call count stays 1).
- **Client core:** `MapNoticeTextTests` (parity, lint, composition, 480-character rule), `MapNoticeInputsTests`, `MapNoticeSelectorTests`
  (rows 1-23, priorities, no-notice states, `ShowMapNotices=false`), `MapNoticeSchedulerTests` (fake environment and clock: thresholds,
  waits, displacement, T2, T3, attempts, N1, fault policy), `MapLoadFailuresTests`, `ServerStatusParserTests` (including the round trip
  against `ArenaServerStatus.ToJson`, so camelCase or field drift fails), `MapNoticeGateStateTests`.
- **Glue and wiring:** `ServerStatusClientTests` (the real `HttpClient` against a loopback TCP stub: 200, zlib body, 404, 500, garbage, timeout,
  refused, the unavailable shapes; stand-ins in `SptHttpStandIns.cs`), `BundleFailureRecordTests`, `MapNoticeWiringTests` (with
  `SkippedHooksTests`), `MapNoticeEndToEndTests` (real resolver into real scheduler into the modal text), `MapNoticeSourcePinTests`,
  `MapNoticeTestSupport` (the shared fake environment).
- **Review fixes of 2026-10-22:** scheduler tests for the edge release, the named blocker, the truthful fallback Warning, the window watchdog,
  the stopped clock step, the damage check and the N1 record not keeping the tick alive; selector tests for the nine reminder groups and the
  `Unreadable` probe; `ArenaInstallationTests` for network locations; source pins for `ModalPossible` (every sub-check, no `InGameStatus`, an
  `ErrorScreen` component for the critical container), the scrolled branch of `ShowModal`, the alignment restore and "no key resolution in any
  notice file".
- **Source pins:** `MapNotices.Tick();` after `PollOutcome();` without an added `{ }`; the `BundleHost` and `NavMeshInstaller` notes come
  before the `LogError`, go through `NoteSafe`, and the existing pins are unchanged; no `.Bind(` in the gate, exactly one bind of
  `General/ShowMapNotices` in `MapNotices.cs`; no `EnsureVerified(` in any notice file; `MapNotices.cs` is the only file with `ItemUiContext`,
  `NotificationManager`, `EftScreenManager` or `SettingsManager`; every notice log line carries `[Notice]`.
- **Updated:** `ReleaseConfigurationTests` (four public entries, three normal controls), `tools/tests/ConfigRetirementCheck/CompactChecks.cs`
  (`ShowMapNotices` stays in the public file across restarts), `tools/tests/test_bundle_crypt_tool.py`.

## Verification at the end of stage 4 (2026-10-22)

- `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release`: 2090 of 2090 passed (0 failed, 0 skipped); the run
  rebuilt the server project and the test project. The stages before it ended with the server project, the client
  (`ArenaMapPortDllOnly=true`) and the BundleCrypt tool at 0 warnings and 0 errors; stage 4 changed documentation only.
- `python -m unittest discover -s tools/tests -p "test_*.py"` with `PYTHONUTF8=1`: 1950 tests, OK, 55 skipped. `test_release_zips.py` alone:
  177 tests OK; its `RealRepoTests` pin that README, FORGE and `docs/releases/v0.3.0.md` name the Arena build of the keys. Without
  `PYTHONUTF8=1` one test, `test_door_poses.SidecarTests.test_the_validator_run_passes`, fails on a cp1252 console: `validate_map_descriptors.py`
  prints an em dash that the test decodes as UTF-8 (the validator itself prints `OK`); unrelated to this work.
- Nothing was deployed, committed or run on a host.

## Review fixes (2026-10-22)

Three reviewers (runtime safety, spec and tests, texts and docs) went through the implemented feature; every high and medium finding that was real
is fixed, and the low ones that were cheap and right. What changed in behaviour:

- **Runtime safety.** `ModalPossible` counts only an active `ErrorScreen` child of the critical container and no longer reads the stale
  `InGameStatus.InRaid`; it names its blocker in the log. A watchdog closes a ticket whose window vanished without a callback. Each step of a tick has
  its own fault counter, so one that keeps throwing stops instead of throwing every frame. The per-frame delegates are cached. The shared window's
  alignment is restored only while still ours. `NoteLoadFailure` is guarded. The registry diagnostic never probes a network location. The server's
  registration inputs are built inside a try/catch again.
- **Delivery.** An answer to a failed attempt is released by the main menu becoming current again when no menu show follows the comeback. The fallback
  toast is logged only when it was shown. A 200 answer this client cannot read is `Unreadable` and reads as "the server did not say why". A pending
  "raid without NavMesh" record is cleared at the comeback and its unload hook is registered before the load; the damage check no longer keeps the
  tick alive through a raid.
- **Texts.** The selection reminder has one group per remedy (nine). "Not valid JSON" for an ignored override file says what the server really
  checks; "1 files" plurals, "knows them", "unmodified" and the `{support}` phrases are fixed in both languages; the replace-the-folder advice no
  longer deletes the override file; the German table's two stiff phrases are rewritten.
- **Docs.** The README names what the setting hides, qualifies "no message means fine", and the release notes and the zip text say the same.

Result after the fixes: server build and client build (`ArenaMapPortDllOnly=true`) 0 warnings, 0 errors; `dotnet test` 2123 of 2123 passed (33 more
than before); the test project's clean build has 70 warnings, none from the notice files (the two CA2022 of the new tests are gone); the full Python
suite (`PYTHONUTF8=1`) 1950 tests OK, 55 skipped. Nothing was deployed, committed or run on a host.

Findings that were rejected or deferred, with their reasons, are in the fix stage's report: the `_messageOnLoadMenu` fallback waits for host
evidence (V4); the ClientFolderBad "?" path cannot occur because the registry scan skips an entry without an InstallLocation; shortening the over-limit
texts was rejected (see the list under "Texts as implemented").

## Not verified: runtime facts and known gaps

Nothing in this list could be checked without a host run; each has a step in "Host verification plan".

- Seconds a Long toast stays on screen (prefab animator, not in the decompile).
- That `MenuScreen.Show` fires after a failed load, and what EFT shows during the failed load (the open generic-popup question).
- That a displaced window raises `OnDecline` or `OnClose` as the decompile suggests (`ItemUiContext.cs:1412-1419`, `BaseWindowContext.cs`), and
  what `PreloaderUI.CanShowErrorScreen` means exactly.
- The reviewers' claims about the in-raid Esc menu (`profile == null`, screen type `MainMenu`), the `InGameStatus.InRaid` reset points, and
  `ErrorScreen.Show` calling `CloseAllWindows`. The design is safe if they hold and the guards are cheap if they do not.
- That `ModalPossible` does not stay false forever. A stale `AbstractGame` would keep it false (since the review fixes of 2026-10-22 it no
  longer reads `InGameStatus.InRaid`, and only an active `ErrorScreen` child of the critical container counts); the scheduler then waits out
  the blocked budget (120 menu-seconds) and the toast fallback takes over, and the notice re-arms at every later menu show. The "waiting"
  Debug line now names the blocking sub-check, which settles V1's question from the log.
- That `Settings.Language.Value` holds EFT's German locale code `ge` (the constant is `LocalizationManager.GERMAN_LOCALIZATION` in the 0.16.9.5
  decompile) and that `SettingsManager` is instantiated at the first main menu; and that Bender renders the German table's umlauts and ß (V2).
- That the reflection surface of `spt-common` matches the 4.1.6 decompile at runtime. The VM runs SPT 4.1.6 while the docs and the server
  decompile say 4.1.5; the 404 and zlib claims come from the 4.1.5 server decompile. If the surface differs, the probe returns
  `Unavailable` and T1 shows `ServerUnknown`.
- Modal layout capacity for the longest text, and whether a loader overlay covers the modal (add `!PreloaderUI._loader.activeSelf` to
  `ModalPossible` only if a host run shows it does).
- The server version on GPU-1 (assumed 0.2.3) and whether the 0.6.0 share is reachable from it.
- Known gaps, not fixed here: a newer server registering a map an older client does not know has no attempt latch and gives no notice (the T1
  file check and the version-skew modals cover only maps in the client's registry); ArenaMode's own filtering of offered maps stays invisible.

## Host verification plan

Setup: client and server Release builds (`-p:ArenaMapPortDllOnly=true` for the client), then `tools/deploy-to-spt.ps1` with a dry run first.
Close EFT and the server before the deploy (the server DLL is replaced only when the process is closed); verify the host's server DLL hash
and the client MVID before restarting. Nothing in this change touches the bundles, so the sync is small. Nothing is written to `D:\SPT41`
by hand, and no step requires deleting a host file. A modal at the main menu blocks UiBridge and `gpu-run` menu clicks: record its OK
click point (from `uibridge_snapshot contains=OK`) in the evidence section. Host logs need Debug enabled for the availability lines.

Tamper steps (V5 to V7) follow the E6 precedent in [bundle-encryption.md](bundle-encryption.md): backups under `work/negative`, a
byte-identical restore, a dry run reporting `0 to overwrite` after the restore, `validate_distribution.py` before any packaging, never print a hash.

| Step | Host and action | Expected |
|---|---|---|
| V1 | GPU-2 (Arena 0.6.1, positive) | First main menu shows no modal and no toast. Debug availability line `offered=4/4 files=ok`. No `/arenamapport/status` request in the log. `uibridge_status` max frame time unchanged. Open the hideout once (no new errors in `Logs/<session>/*errors.log`, no second notice). Then start one raid (for example Bowl, NO BOTS) as a regression. Open the in-raid Esc menu once: no notice. |
| V2 | GPU-1 (no Arena; the retired host, decision (e)) | Client banner "Maps: none available. Arena: not found." First main menu: `ClientNoArena` modal with the exact text, ref and footer, OK closes it. At 518 characters (English) or 584 (German) this text goes through the SCROLLED window: check its layout, left alignment, scroll and OK button, and record which window was used; the German table must render ä, ö, ü and ß. Open the hideout and the location selection: one `SelectionReminder` toast with the fix, no second modal. Restart EFT: shown again (once per session). Set `General/ShowMapNotices=false`: neither modal nor toast. Confirm which MCP prefix is live before deploying. |
| V3 | GPU-1 server reasons, real states | Set the client's public `General/ArenaInstallPath` to a verifying Arena install that GPU-1 can reach (the 0.6.0 share named in AGENTS, if reachable). With the host's current 0.2.3 server: a real 404 gives `ServerUnknown`. After deploying the new server DLL: `ServerNoArena` with cause `not_registered`, the "create the file" action and the JSON example (558 characters, the scrolled window: record layout and OK button). No tampered file reaches `dist/`. Clear the override key afterwards (mind the `uibridge_config_set` lease trap: it blocks `gpu-run` for 90 s). |
| V4 | GPU-2 client gate refusal (rows 3 and 17) | Set the public `General/ArenaInstallPath` to a nonexistent folder, restart EFT. First menu: `ClientOverrideBad` modal. Start a raid: the load is refused, EFT shows its generic popup, and after about 14 s the main menu shows the T3 modal "{Map} could not be loaded" with the same cause. Clear the key, restart EFT. |
| V5 | GPU-2 stage 2 key failure (row 18) | Deploy a tampered `keys/arena_bowl.json` (one size changed, copy staged in scratch). Start Bowl: about 14 s later the generic EFT popup, then `KeyDiffers` (or `KeyUpdated` / `KeyArenaOlder` by version) once at the main menu. Equator still loads (control). Restore by redeploying the pristine key file, verify the host file matches `dist`, restart EFT (the failure is cached per process). |
| V6 | GPU-2 blocking bundle failure (row 19) | N3b-style header tamper of a preset bundle copy. Expect `BundleBad` at the next menu. Restore. |
| V7 | GPU-2 NavMesh (row 21, optional) | Tamper a copy of a map's navmesh bundle. The raid starts, no modal after it, the toast appears once the raid has started with the reason. A second raid on that map shows it again with the same reason. Restore. |
| V8 | Negative checks | Cancel a matchmaking attempt as the player: no `LoadNoCause` modal. Displacement: with another message window open at the first menu, the notice waits and shows after it closes. |

If a modal does not show at a main menu where it should, the `[Notice] waiting: a free message window (<blocker>)` Debug line names the sub-check
that refused (V1 settles whether `ModalPossible` is true on an ordinary first menu this way). A window that vanished without any callback is
logged as `window is gone without an answer; treated as closed`.

Measure on the host: how many seconds a Long toast stays visible; that the longest modal fits without clipping (if not, lower the
480-character threshold); that `MenuScreen.Show` fires after the failed load (V4/V5; if not, the `released: the main menu is the current screen again` Debug line shows the
rising-edge fallback doing its job); what EFT shows during
the failed load (resolves the open generic-popup question) and whether the T3 modal appears after the popup closes or collides with it; that a
window closed by an EFT `ShowMessageWindow` or `ErrorScreen.Show` reports `OnDecline` or `OnClose` as assumed (the displacement logic depends
on it); that the hideout opens once without a new error (a prefix on a generic class would show here).

Not host-provable without deleting host files or a second host, so unit-tested only: rows 2, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 16, 20, 22.
Pending and `ClientCheckSlow` also use injected clocks, because the measured 21 s UNC block cannot be forced across the 10 s menu threshold reliably.

## Steam finding and detection

- Arena is not a standalone Steam app. Steam owners get an Arena entitlement through the "Unheard Steam Edition" DLC; Arena itself is
  installed and launched by the BSG launcher. Whether a Steam-origin launcher writes `HKLM\...\Uninstall\EscapeFromTarkovArena_live`, or lays Arena
  out under `steamapps\common\...\build`, could not be verified (this VM has no Steam keys). No text mentions Steam.
- **Do not change detection speculatively.** First get evidence from the reporting player: the `[Arena]` lines of `BepInEx\LogOutput.log` and the
  output of `reg query HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall /s /f Arena`.
- **The diagnostic that rides with this work:** when no registry entry is found, the client logs ONE Info line (`[Arena] no registry entry;
  Uninstall subkeys matching Arena or Tarkov: ...`) listing the matching subkey names, their `InstallLocation` and whether
  `EscapeFromTarkovArena.exe` exists there. It is computed in the existing `NotRegistered` branch of `ArenaInstallation`, carried in the result,
  logged only by the client (never by the server, whose lines are pinned), and adds no behaviour and no change to the gate texts. It is Info,
  not Debug, because a normal player's log is the only evidence support gets.
- **If evidence shows another registration name or layout:** widen to any Uninstall subkey, plus HKCU, whose `InstallLocation` contains
  `EscapeFromTarkovArena.exe`. As safe as today, because the verification of the folder against `ConsistencyInfo` is the gate.
- ArenaMode's source catalog (read only) knows Equator, Bay5 and Air Pit, not Bowl; that is an ArenaMode-side matter this repository does not
  change, and by decision (f) neither the README nor the release notes carry a line about it. A future read-only reason hook from ArenaMapPort
  could feed ArenaMode's own "ARENA UNAVAILABLE" notice.

## Release vehicle

By decision (a) of 2026-10-22 this is implemented on `main` only; `release/0.2.3` and its published 0.2.3 are untouched. The reporter is on
0.2.3, the only downloadable version, so the notices reach him with the first release cut from `main` (0.3.0; the notes carry the bullet and the
instruction to replace both DLLs with the server closed). A 0.2.4 backport of the stage-1 subset (the cause field and enum, the server holder,
router and gate recording, `GateState`, the input builder, scheduler, selector and texts for rows 1-16, 21 and 23, the language tables, the
config key with both allowlists, the `Plugin` hooks, the attempt hooks in `LoadingScreenArt`; failure records in `BundleHost.TryServe`'s refusal
and missing-file branches with a standalone registry) is possible later: the server files and `ArenaInstallation.cs` are identical there apart
from the map count, stage 2 (key failures, integrity records, rows 18, 19 partly, 22) and Bowl do not apply, and the tag would be written into
the message text there. It is not scheduled. A new client against a 0.2.3 server (no status route) gives the generic `ServerUnknown` or
`ServerSomeMissing` text; the exact server reason needs the server DLL of the same version.

## Host evidence

Append-only. Each entry gets a date, time, build id (client MVID, server DLL hash comparison), the quoted `[Notice]` and `[Arena]` lines, the
measurements listed above and the modal's OK click point.

| Step | Status |
|---|---|
| V1 | **passed** 2026-10-22 21:08-21:10, GPU-2 |
| V2 | **passed** 2026-10-22 21:11-21:14, GPU-1 |
| V3 | **passed as a variant** 2026-10-22 21:17-21:19, GPU-2 (server override instead of a client override, see below) |
| V4 | not run (it needs a client config change, which the Syncthing mirror carries to every machine, the user's own PC included); unit tests only |
| V5 | **passed** 2026-10-22 21:20-21:26, GPU-2 |
| V6 | not run; unit tests only |
| V7 | not run; the 2026-10-20 N3 runs of [bundle-encryption.md](bundle-encryption.md) showed the raid starting without NavMesh, the toast itself is unit-tested only |
| V8 | not run; unit tests only |

### 2026-10-22, 21:07 to 21:27 (lead)

**Build and deploy.** Server and client Release builds (`-p:ArenaMapPortDllOnly=true`) of `main` at `0dd0b2a` plus the uncommitted notice tree, both
`0.3.0+0dd0b2ab0e7e91a72ecfc5d8d04512c2660a906f`. Deploy dry run `90 file(s) ... 0 new, 2 to overwrite, 88 already identical` (the two DLLs), then
`-Confirm`; `gpu-run deploy-wait --host host2 --server --force` (server stopped, `2 file(s) identical on the host after 62.2 s`, server restarted),
`gpu-run restart --host host2` (70 s, `16/16 identical`); for GPU-1 `deploy-wait --host host1 --server` (`server was not running`, started) and
`gpu-run start --host host1` (95 s, `16/16 identical`). Server log on both: `Mod: ArenaMapPort Version: 0.3.0 ... geladen`.

**V1, GPU-2 (Arena 0.6.1.0.48206, positive), EFT instance `fb73a4f7`.**
```
21:08:44.241 [Info] [Arena] Escape from Tarkov: Arena 0.6.1.0.48206 at C:\Battlestate Games\Escape from Tarkov Arena (registry; 15,351 files, 76.1 GB verified)
21:08:44.363 [Debug] [Notice] installed (main menu hook=yes, selection hook=yes, ShowMapNotices=on)
21:09:15.010 [Debug] [Notice] waiting: settle time
21:09:16.123 [Debug] [Notice] availability: arena=verified, offered=4/4 [arena_equator,arena_bay5,arena_airpit,arena_bowl], files=ok, server=not_needed
```
No window, no toast, no `/arenamapport/status` request in the server log. The hideout opened in 8 s and BACK returned to the menu (only the
hideout's own `LODGroup` warnings and one `Can not play a disabled audio source`, both vanilla). A Bowl raid (no bots) started in 37 s:
`[Notice] attempt: map=arena_bowl started` (21:09:55), `[BundleKeys] key for 'arena_bowl' derived in 30 ms (... crypto: CNG)`, `[NavMesh] added navmesh
for 'arena_bowl': 12704 vertices, 5890 triangles`, `[Notice] attempt: map=arena_bowl succeeded` (21:10:25); in the raid 120 fps, max 8.9 ms over 300
frames; 0 Warnings from the mod.

**V2, GPU-1 (no Arena), EFT instance started 21:12.**
```
21:12:00.879 [Info] [Arena] Escape from Tarkov: Arena not found: No Escape from Tarkov: Arena installation is registered in the Windows registry (...)
21:12:00.887 [Info] [Arena] no registry entry; Uninstall subkeys matching Arena or Tarkov: EscapeFromTarkov=C:\Battlestate Games\Escape from Tarkov, exe=no
21:12:04.210 [Info] Version 0.3.0 loaded. Maps: none available. Arena: not found. ...
21:13:01.075 [Debug] [Notice] availability: arena=not_verified:not_registered, offered=?/4, files=skipped, server=not_needed
21:13:01.108 [Info] [Notice] showing 'ClientNoArena' (modal) ref=NO_ARENA client=not_verified:not_registered server=not_needed offered=?/4
21:14:06.674 [Debug] [Notice] 'ClientNoArena' seen (accepted)
21:14:18.156 [Info] [Notice] showing 'SelectionReminder' (toast) ref=SELECTION client=not_verified:not_registered server=not_needed offered=?/4
```
The new registry diagnostic names the EFT entry and says that Arena's exe is not in it. The modal (518 characters, so the wide scrolled message
window) showed all five lines unclipped, caption `ArenaMapPort`, OK at (960, 612); still `work/screenshots/notices/notice-host1-no-arena.png`.
The selection reminder appeared bottom right on SELECT LOCATION ("ArenaMapPort: the Arena maps are not available (Arena was not found on this PC).
Install Arena with the launcher, or set F12 > ArenaInstallPath, then restart."), screenshot of the scenario run. No second modal.

**V3 variant, GPU-2: the server cannot verify Arena, the client can.** The plan's V3 sets the client's `General/ArenaInstallPath` on GPU-1; the
BepInEx config travels through the Syncthing mirror to every machine (both hosts and the user's own PC), so the server side was tampered instead:
`dist/SPT_Runtime/user/mods/ArenaMapPort/config/arena-install.json` = `{ "installPath": "C:/Battlestate Games/ArenaMapPort-notice-test-missing" }`,
deployed (`1 new`), server restarted by `deploy-wait --server --force`, EFT restarted.
```
srv 21:17:24.182 [ArenaMapPort] Escape from Tarkov: Arena not found: The installation folder does not exist: C:\Battlestate Games\ArenaMapPort-notice-test-missing. The 4 Arena maps stay unregistered. (...)
    21:18:38.645 [Debug] [Notice] availability: arena=verified, offered=0/4 [], files=ok, server=not_needed
    21:18:38.646 [Debug] [Notice] waiting: the server status
srv 21:18:38.777 [SptLoggerMiddleware] [Client-Anfrage] /arenamapport/status
    21:18:38.804 [Debug] [Notice] server status: GET /arenamapport/status -> 200 in 16 ms
    21:18:38.805 [Debug] [Notice] server status: ok:not_verified/folder_missing
    21:18:38.825 [Info] [Notice] showing 'ServerNoArena' (modal) ref=SERVER_NO_ARENA client=verified:none server=ok:not_verified/folder_missing offered=0/4
```
The window said: "The Arena maps are not offered: the SPT server could not verify Escape from Tarkov: Arena (the folder set in arena-install.json does
not exist), although this game found it. Fix the path in SPT_Runtime\user\mods\ArenaMapPort\config\arena-install.json, or delete that file to use
the launcher's entry, then restart the server." with the footer `ArenaMapPort 0.3.0 - ref SERVER_NO_ARENA, server 0.3.0` (the narrow window; the long
path wraps inside a word, readable); still `work/screenshots/notices/notice-host2-server-no-arena.png`. This proves the status route, the probe by
reflection over SPT's `RequestHandler.HttpClient` (SPT 4.1.6 client), the response decoding and the per-cause action line on a real host.
Restore: the same file with `{ "installPath": "" }` (an explicit "no override": no warning, the registry entry is used), deployed together with V5's
file; the next server start logged `(registry; 15,351 files, 76.1 GB verified)` and four `is available` lines. **That neutral file stays on every
install the mirror reaches** (the deploy script never deletes; `dist/` no longer carries it), harmless, and the user may delete
`SPT_Runtime\user\mods\ArenaMapPort\config\arena-install.json` there.

**V5, GPU-2: stage-2 key failure after a raid start.** Backup of the staged `keys/arena_bowl.json` under ignored `work/negative/notice/orig/`, a copy
with the first `size` + 1 (`sharedassets41.assets.resS` 578163152 -> 578163153) staged into `dist/`, deployed (`2 to overwrite` with the neutral
override), server and EFT restarted; first menu: `offered=4/4`, no notice. Bowl raid:
```
21:22:33.202 [Debug] [Notice] attempt: map=arena_bowl started
21:22:34.984 [Error] [BundleKeys] key for 'arena_bowl' unavailable: Arena file 'EscapeFromTarkovArena_Data/sharedassets41.assets.resS' differs in size from the key file (578163152 against 578163153 bytes, Arena 0.6.1.0.48206)
21:22:34.985 [Debug] [Notice] load failure noted: map=arena_bowl kind=Key cause=SizeDiffers seq=1
21:22:34.985 [Error] [Bundles] 'maps/arena_bowl_preset.bundle' not served: no key for 'arena_bowl' (see the line above)
21:22:35.001 [Debug] [Notice] attempt: map=arena_bowl failed records=1
21:22:51.894 [Info] [Notice] showing 'KeyDiffers' (modal) ref=KEY_DIFFERS client=verified:none server=not_needed offered=?/?
```
EFT returned to the main menu and the window followed 17 s after the refusal: "Bowl cannot be opened: the Arena files it is built from do not match
ArenaMapPort's unlock file (Arena 0.6.1.0.48206). Repair Arena in the Battlestate Games launcher (damaged or modified files), then restart the game. If
it stays this way, your Arena version needs an ArenaMapPort update." (equal versions, so `KeyDiffers`, not `KeyUpdated`/`KeyArenaOlder`). OK closed
it. Restore: the backup copied back (`cmp` equal), deployed, dry run `90 file(s) ... 0 new, 0 to overwrite, 90 already identical`, EFT restarted,
Bowl raid in 38 s with `derived in 8 ms`, `12704 vertices, 5890 triangles`, `attempt ... succeeded`, 0 Warnings from the mod.
`python tools/validate/validate_distribution.py` afterwards: `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted`.

**Measured and still open.** Both modal layouts render without clipping (the wide scrolled window for the 518-character `ClientNoArena`, the narrow
one for `ServerNoArena` and `KeyDiffers`). After the failed load the T3 window appeared at the main menu by itself (no fallback line was needed to
observe it). Not measured: how long a Long toast stays, whether EFT's generic "matching aborted" popup showed before the T3 window (no screenshot in
that 17 s), the displacement callbacks (V8) and the German texts on a host (both hosts run EFT in English).

## Cross-references

- [arena-install-check.md](arena-install-check.md): the check these notices explain; the status route reads its result, the gate texts are unchanged.
- [bundle-encryption.md](bundle-encryption.md): the key and bundle failures the T3 modals name.
- [../../README.md](../../README.md): the player-facing "If the maps do not show up" list (ref to fix).
