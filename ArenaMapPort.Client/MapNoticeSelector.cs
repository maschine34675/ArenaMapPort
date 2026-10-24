#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using ArenaMapPort.Crypto;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>What the scheduler knows when an Arena raid attempt returned to the main menu without starting (T3).</summary>
    internal sealed class AttemptFailureInput
    {
        internal AttemptFailureInput(string mapKey, string mapName, IReadOnlyList<MapLoadFailure> records, GateSnapshot gate,
            bool canceledByPlayer, bool reachedStarted, string clientVersion, bool nativeErrorReported = false)
        {
            MapKey = mapKey;
            MapName = mapName;
            Records = records ?? new MapLoadFailure[0];
            Gate = gate;
            CanceledByPlayer = canceledByPlayer;
            ReachedStarted = reachedStarted;
            ClientVersion = clientVersion;
            NativeErrorReported = nativeErrorReported;
        }

        internal string MapKey { get; }
        /// <summary>The display name of the map ("Bowl").</summary>
        internal string MapName { get; }
        /// <summary>The blocking records of the attempt (<see cref="MapLoadFailures.TakeBlocking"/>).</summary>
        internal IReadOnlyList<MapLoadFailure> Records { get; }
        /// <summary>The client's Arena check as it was when the attempt ended.</summary>
        internal GateSnapshot Gate { get; }
        /// <summary>EFT's own flag that the player cancelled matchmaking.</summary>
        internal bool CanceledByPlayer { get; }
        internal bool ReachedStarted { get; }
        internal string ClientVersion { get; }
        /// <summary>EFT already queued an error for this exact loading attempt; only the no-cause fallback is redundant.</summary>
        internal bool NativeErrorReported { get; }
    }

    /// <summary>
    /// The decision table of the in-game map notices (docs/progress/map-notices.md, "Failure states and notices"): a pure function from what is known
    /// to the one notice that applies.
    ///
    /// What vanilla does. Nothing: absent maps and failed loads are silent or end in a generic popup.
    ///
    /// What this class does. <see cref="SelectMenu"/> is the main-menu notice (T1), one modal per pass, the first that matches in the design's order:
    /// the client's own Arena check (rows 1 to 6, 8), a skipped map-critical hook (row 9), the server's reason when no registry map is offered
    /// (rows 10 to 13), the server's reason when some are missing (rows 14, 15), a client file that is missing (row 16). A client that is not verified shows
    /// its own notice even when the server registered the maps; a verified client with every registry map offered and every file in place gets none (and
    /// no HTTP request). General/ShowMapNotices=false silences it. <see cref="SelectAttempt"/> answers a failed Arena raid attempt (rows 17 to 20): the
    /// newest blocking record decides, the gate refusal becomes the matching client text with the "{Map} could not be loaded" lead, a key failure
    /// becomes one of six key texts (an Arena version newer than the key file's means "update ArenaMapPort", an older one "update Arena"; versions are
    /// compared component by component because Arena's own versions have five numbers, which <c>System.Version</c> cannot hold), a bundle problem
    /// becomes <see cref="NoticeId.BundleBad"/>, and no record at all becomes <see cref="NoticeId.LoadNoCause"/> unless the player cancelled or the
    /// raid started or EFT already reported an error for that attempt. <see cref="SelectNavMesh"/> (row 21), <see cref="SelectFileDamaged"/> (row 22) and <see cref="SelectPending"/> (row 7) are the toasts.
    ///
    /// Not here: any Unity or EFT call, any logging, the timing (<see cref="MapNoticeScheduler"/>) and the words (<see cref="MapNoticeText"/>).
    /// Host test: docs/progress/map-notices.md.
    /// </summary>
    internal static class MapNoticeSelector
    {
        /// <summary>The main-menu notice for <paramref name="input"/>, or null when there is nothing to say (the check still runs, everything is fine, or the notices are switched off).</summary>
        internal static MapNoticePlan SelectMenu(MapAvailabilityInput input)
        {
            if (input == null || !input.NoticesEnabled)
                return null;
            MapNoticePlan plan = SelectMenuCore(input);
            if (plan == null)
                return null;
            plan.Lead = NoticeLead.Menu;
            plan.Set("ver", input.ClientVersion);
            plan.Reminder = ReminderOf(plan.Id, plan.Action);
            plan.LogSummary = MapNoticeInputs.DescribeSummary(input);
            return plan;
        }

        static MapNoticePlan SelectMenuCore(MapAvailabilityInput input)
        {
            GateSnapshot gate = input.Gate;

            if (gate.State == GateState.Running)
                return null; // nothing yet: the scheduler waits (and shows the pending toast after ten menu-seconds)

            if (MapNoticeInputs.ClassifyClient(gate, out NoticeId clientId, out NoticeWhy clientWhy))
                return ClientPlan(clientId, clientWhy, gate);

            if (input.SkippedHooks.Count > 0)
                return new MapNoticePlan(NoticeId.ClientHookMissing).Set("detail", input.SkippedHooks[0]);

            MapNoticePlan server = SelectServer(input);
            if (server != null)
                return server;

            if (input.Files != null && input.Files.MissingCount > 0)
                return new MapNoticePlan(NoticeId.InstallIncomplete)
                    .Set("maps", MapNoticeInputs.JoinNames(input.Files.MapNames))
                    .Set("n", input.Files.MissingCount.ToString(CultureInfo.InvariantCulture))
                    .Set("file", input.Files.FirstMissing);

            return null;
        }

        /// <summary>The plan of a client-gate notice (rows 1 to 6, 8) with the values its text needs.</summary>
        static MapNoticePlan ClientPlan(NoticeId id, NoticeWhy why, GateSnapshot gate)
        {
            var plan = new MapNoticePlan(id) { Why = why };
            switch (id)
            {
                case NoticeId.ClientFolderBad:
                    plan.Set("path", gate.InstallPath);
                    break;
                case NoticeId.ClientIncomplete:
                    plan.Set("path", gate.InstallPath).Set("n", gate.MismatchCount.ToString(CultureInfo.InvariantCulture));
                    break;
                case NoticeId.ClientCheckFailed:
                    plan.Set("detail", gate.FailureDetail ?? gate.Reason ?? "the check did not run");
                    break;
            }
            return plan;
        }

        // ---- the server's reason (rows 10 to 15) ------------------------------------------------------------------------

        static MapNoticePlan SelectServer(MapAvailabilityInput input)
        {
            if (!input.Gate.Verified || !input.OfferedKnown)
                return null;
            IReadOnlyList<MapInfo> missing = input.MissingMaps;
            if (missing.Count == 0)
                return null;

            ServerStatusView status = input.Probe.State == ProbeState.Ok ? input.Probe.Status : null;
            MapNoticePlan plan = input.Offered.Count == 0 ? NoneOffered(input, status) : SomeOffered(input, status, missing);
            if (plan != null && status != null)
                plan.ServerVersion = status.ServerVersion;
            return plan;
        }

        // Rows 10 to 13: the backend offers no registry map at all.
        static MapNoticePlan NoneOffered(MapAvailabilityInput input, ServerStatusView status)
        {
            switch (input.Probe.State)
            {
                case ProbeState.Ok:
                    if (status == null || status.GateNotRun)
                        return new MapNoticePlan(NoticeId.ServerUnknown);
                    if (status.GateVerified)
                        return AllRegisteredMapsFailed(input, status)
                            ? new MapNoticePlan(NoticeId.ServerAllFailed) { Why = MapFailureWhy(status, input.Maps) }
                            : new MapNoticePlan(NoticeId.ServerUnknown); // verified, nothing failed, yet nothing offered: inconsistent
                    if (string.Equals(status.GateState, ArenaStatusContract.GateNotVerified, StringComparison.Ordinal)
                        || string.Equals(status.GateState, ArenaStatusContract.GateError, StringComparison.Ordinal))
                        return ServerNoArena(input, status);
                    return new MapNoticePlan(NoticeId.ServerUnknown); // a gate state this client does not know
                case ProbeState.NotFound:
                case ProbeState.Unavailable:
                case ProbeState.Unreadable: // a server of another version answered: it did not say why
                    return new MapNoticePlan(NoticeId.ServerUnknown);
                default:
                    return new MapNoticePlan(NoticeId.ServerNoAnswer); // timed out, failed, or still pending when the scheduler gave up
            }
        }

        // Rows 14 and 15: some registry maps are offered, some are not.
        static MapNoticePlan SomeOffered(MapAvailabilityInput input, ServerStatusView status, IReadOnlyList<MapInfo> missing)
        {
            if (status != null)
            {
                var failed = new List<MapInfo>();
                foreach (MapInfo map in missing)
                {
                    ServerMapStatus entry = status.FindMap(map.Key);
                    if (entry != null && entry.Failed)
                        failed.Add(map);
                }
                if (failed.Count > 0)
                    return new MapNoticePlan(NoticeId.ServerSomeFailed) { Why = MapFailureWhy(status, failed) }
                        .Set("maps", NamesOf(failed));
            }
            return new MapNoticePlan(NoticeId.ServerSomeMissing).Set("maps", NamesOf(missing));
        }

        static bool AllRegisteredMapsFailed(MapAvailabilityInput input, ServerStatusView status)
        {
            if (input.Maps.Count == 0)
                return false;
            foreach (MapInfo map in input.Maps)
            {
                ServerMapStatus entry = status.FindMap(map.Key);
                if (entry == null || !entry.Failed)
                    return false;
            }
            return true;
        }

        /// <summary>"Its map data is missing or damaged" when every failed map's cause is a base.json one, otherwise "registration failed".</summary>
        static NoticeWhy MapFailureWhy(ServerStatusView status, IReadOnlyList<MapInfo> maps)
        {
            bool any = false;
            foreach (MapInfo map in maps)
            {
                ServerMapStatus entry = status.FindMap(map.Key);
                if (entry == null || !entry.Failed)
                    continue;
                any = true;
                if (!string.Equals(entry.Cause, ArenaStatusContract.MapCauseBaseJsonParse, StringComparison.Ordinal)
                    && !string.Equals(entry.Cause, ArenaStatusContract.MapCauseBaseJsonMissing, StringComparison.Ordinal))
                    return NoticeWhy.RegistrationFailed;
            }
            return any ? NoticeWhy.MapDataBad : NoticeWhy.RegistrationFailed;
        }

        static string NamesOf(IReadOnlyList<MapInfo> maps)
        {
            var names = new List<string>();
            foreach (MapInfo map in maps)
                names.Add(map.Name);
            return MapNoticeInputs.JoinNames(names);
        }

        /// <summary>Row 10: the server could not verify Arena although this client did. The reason and the action follow the server's cause and its override file.</summary>
        static MapNoticePlan ServerNoArena(MapAvailabilityInput input, ServerStatusView status)
        {
            ClassifyServerGate(status, out NoticeWhy why, out NoticeAction action);
            return new MapNoticePlan(NoticeId.ServerNoArena)
            {
                Why = why,
                Action = action,
            }.Set("json", MapNoticeText.BuildJsonExample(input.Gate.InstallPath));
        }

        /// <summary>
        /// The reason and the action for a server gate that did not verify: an unexpected failure or error restarts the server, changed files wait for the
        /// launcher, a missing launcher entry asks for the override file (or its repair when the file exists but was ignored), a bad override path asks to fix
        /// the path, a bad folder the launcher itself lists asks for a reinstall.
        /// </summary>
        internal static void ClassifyServerGate(ServerStatusView status, out NoticeWhy why, out NoticeAction action)
        {
            ArenaInstallCause cause = status.GateCauseKnown ? status.GateCause : ArenaInstallCause.CheckFailed;
            bool overrideIgnored = string.Equals(status.OverrideFile, ArenaStatusContract.OverrideIgnored, StringComparison.Ordinal);
            bool overrideUsed = string.Equals(status.OverrideFile, ArenaStatusContract.OverrideUsed, StringComparison.Ordinal)
                                || string.Equals(status.GateSource, ArenaInstallation.SourceOverride, StringComparison.Ordinal);

            if (string.Equals(status.GateState, ArenaStatusContract.GateError, StringComparison.Ordinal)
                || cause == ArenaInstallCause.CheckFailed || cause == ArenaInstallCause.None)
            {
                why = NoticeWhy.ServerCheckFailed;
                action = NoticeAction.CheckFailed;
                return;
            }

            switch (cause)
            {
                case ArenaInstallCause.FilesMismatch:
                    why = NoticeWhy.ServerFilesMismatch;
                    action = NoticeAction.FilesMismatch;
                    return;
                case ArenaInstallCause.NotRegistered:
                case ArenaInstallCause.RegistryUnavailable:
                    if (overrideIgnored)
                    {
                        why = NoticeWhy.ServerOverrideIgnored;
                        action = NoticeAction.FixIgnored;
                        return;
                    }
                    why = cause == ArenaInstallCause.NotRegistered ? NoticeWhy.ServerNotRegistered : NoticeWhy.ServerRegistryUnreadable;
                    action = NoticeAction.CreateOverride;
                    return;
                default:
                    // folder_missing, not_arena_folder, manifest_missing, manifest_invalid
                    if (overrideUsed)
                    {
                        why = cause == ArenaInstallCause.FolderMissing ? NoticeWhy.ServerOverrideFolderMissing
                            : cause == ArenaInstallCause.NotArenaFolder ? NoticeWhy.ServerOverrideNotArena
                            : NoticeWhy.ServerOverrideManifest;
                        action = NoticeAction.FixOverridePath;
                        return;
                    }
                    why = NoticeWhy.ServerFolderBad;
                    action = NoticeAction.FixServerFolder;
                    return;
            }
        }

        /// <summary>The reminder group of a notice (rows 1 to 13), or <see cref="ReminderGroup.None"/> for the others; <see cref="NoticeId.ServerNoArena"/> is the override-file group.</summary>
        internal static ReminderGroup ReminderOf(NoticeId id) => ReminderOf(id, NoticeAction.None);

        /// <summary>
        /// The reminder group of a notice with its action: the group is the notice's own remedy, so the toast never sends a player to a fix that does not
        /// apply (a slow check is not repaired by repairing Arena; a server whose launcher folder is bad is not helped by an arena-install.json).
        /// </summary>
        internal static ReminderGroup ReminderOf(NoticeId id, NoticeAction action)
        {
            switch (id)
            {
                case NoticeId.ClientNoArena:
                case NoticeId.ClientRegistryUnreadable:
                    return ReminderGroup.ArenaNotFound;
                case NoticeId.ClientOverrideBad:
                case NoticeId.ClientFolderBad:
                case NoticeId.ClientIncomplete:
                    return ReminderGroup.ClientUnusable;
                case NoticeId.ClientCheckFailed:
                case NoticeId.ClientCheckSlow:
                    return ReminderGroup.ClientCheck;
                case NoticeId.ClientHookMissing:
                    return ReminderGroup.ClientBuild;
                case NoticeId.ServerNoArena:
                    switch (action)
                    {
                        case NoticeAction.FilesMismatch:
                        case NoticeAction.FixServerFolder:
                            return ReminderGroup.ServerArena;
                        case NoticeAction.CheckFailed:
                            return ReminderGroup.ServerCheck;
                        default:
                            return ReminderGroup.ServerNoVerify; // CreateOverride, FixIgnored, FixOverridePath: the arena-install.json
                    }
                case NoticeId.ServerAllFailed:
                case NoticeId.ServerUnknown:
                    return ReminderGroup.ServerNoOffer;
                case NoticeId.ServerNoAnswer:
                    return ReminderGroup.ServerNoAnswer;
                default:
                    return ReminderGroup.None;
            }
        }

        // ---- a failed attempt (rows 17 to 20) ----------------------------------------------------------------------------

        /// <summary>
        /// The notice for a failed Arena raid attempt, or null when there is none to give (the player cancelled, or the raid started, and nothing was recorded).
        /// The newest blocking record decides; a NavMesh record is never blocking and is ignored here.
        /// </summary>
        internal static MapNoticePlan SelectAttempt(AttemptFailureInput input)
        {
            if (input == null)
                return null;

            MapLoadFailure newest = null;
            foreach (MapLoadFailure record in input.Records)
                if (record != null && record.Kind != MapLoadFailureKind.NavMesh && (newest == null || record.Sequence >= newest.Sequence))
                    newest = record;

            MapNoticePlan plan;
            if (newest == null)
            {
                if (input.CanceledByPlayer || input.ReachedStarted || input.NativeErrorReported)
                    return null;
                plan = new MapNoticePlan(NoticeId.LoadNoCause);
            }
            else if (newest.Kind == MapLoadFailureKind.GateRefused)
            {
                GateSnapshot gate = input.Gate ?? GateSnapshot.From(GateState.NotStarted, null, null);
                // a check that finished after the refusal, or still runs, is the "not finished yet" text
                if (!MapNoticeInputs.ClassifyClient(gate, out NoticeId id, out NoticeWhy why))
                    plan = new MapNoticePlan(NoticeId.ClientCheckSlow);
                else
                    plan = ClientPlan(id, why, gate);
                plan.Reminder = ReminderOf(plan.Id, plan.Action);
            }
            else if (newest.Kind == MapLoadFailureKind.Key)
            {
                plan = KeyPlan(newest, input);
            }
            else
            {
                // BundleMissing, BundleInvalid, BundleUnreadable, BundleLoadFailed, Integrity
                plan = new MapNoticePlan(NoticeId.BundleBad).Set("file", newest.File ?? "?");
            }

            plan.Lead = NoticeLead.Attempt;
            plan.Set("Map", input.MapName).Set("ver", input.ClientVersion);
            plan.LogSummary = "client=" + (input.Gate == null ? "unknown" : input.Gate.LogCode) + " server=not_needed offered=?/?";
            return plan;
        }

        static MapNoticePlan KeyPlan(MapLoadFailure record, AttemptFailureInput input)
        {
            string installed = !string.IsNullOrWhiteSpace(record.InstalledVersion) ? record.InstalledVersion : input.Gate?.ManifestVersion;
            string built = record.BuiltFor;
            NoticeId id;
            switch (record.KeyCause)
            {
                case KeyFailure.SizeDiffers:
                case KeyFailure.ContentDiffers:
                    if (TryCompareVersions(installed, built, out int order) && order != 0)
                        id = order > 0 ? NoticeId.KeyUpdated : NoticeId.KeyArenaOlder;
                    else
                        id = NoticeId.KeyDiffers;
                    break;
                case KeyFailure.ArenaFileUnreadable:
                    id = NoticeId.KeyUnreadable;
                    break;
                case KeyFailure.KeyFileMissing:
                case KeyFailure.KeyFileInvalid:
                    id = NoticeId.KeyFileBad;
                    break;
                default:
                    id = NoticeId.KeyInternal;
                    break;
            }
            return new MapNoticePlan(id)
                .Set("inst", installed)
                .Set("built", built)
                .Set("file", record.File ?? (input.MapKey + ".json"))
                .Set("detail", record.Detail);
        }

        /// <summary>
        /// Compares two dotted version numbers component by component (a missing component counts as 0). Arena's versions have five numbers
        /// ("0.6.1.0.48206"), which <c>System.Version</c> cannot hold. False when either text is not 1 to 8 dot-separated non-negative integers
        /// of at most nine digits; <paramref name="order"/> is then 0.
        /// </summary>
        internal static bool TryCompareVersions(string a, string b, out int order)
        {
            order = 0;
            if (!TryParseVersion(a, out long[] left) || !TryParseVersion(b, out long[] right))
                return false;
            int length = Math.Max(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                long x = i < left.Length ? left[i] : 0;
                long y = i < right.Length ? right[i] : 0;
                if (x != y)
                {
                    order = x > y ? 1 : -1;
                    return true;
                }
            }
            return true;
        }

        static bool TryParseVersion(string text, out long[] parts)
        {
            parts = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string[] pieces = text.Trim().Split('.');
            if (pieces.Length == 0 || pieces.Length > 8)
                return false;
            var values = new long[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                string piece = pieces[i];
                if (piece.Length == 0 || piece.Length > 9)
                    return false;
                long value = 0;
                foreach (char c in piece)
                {
                    if (c < '0' || c > '9')
                        return false;
                    value = value * 10 + (c - '0');
                }
                values[i] = value;
            }
            parts = values;
            return true;
        }

        // ---- the toasts ------------------------------------------------------------------------------------------------

        /// <summary>Row 7: the check is still running ten menu-seconds after the first menu.</summary>
        internal static MapNoticePlan SelectPending(string clientVersion) =>
            new MapNoticePlan(NoticeId.CheckPending).Set("ver", clientVersion);

        /// <summary>Row 21: a raid started on <paramref name="mapName"/> without NavMesh.</summary>
        internal static MapNoticePlan SelectNavMesh(string mapName, NavMeshCause cause, string clientVersion)
        {
            NoticeWhy why;
            switch (cause)
            {
                case NavMeshCause.Unverified: why = NoticeWhy.NavUnverified; break;
                case NavMeshCause.FileMissing: why = NoticeWhy.NavFileMissing; break;
                case NavMeshCause.NoKey: why = NoticeWhy.NavNoKey; break;
                default: why = NoticeWhy.NavDamaged; break;
            }
            return new MapNoticePlan(NoticeId.NavMeshMissing) { Why = why }.Set("Map", mapName).Set("ver", clientVersion);
        }

        /// <summary>Row 22: a chunk of a live bundle stream of <paramref name="mapName"/> reported damage outside any failed attempt.</summary>
        internal static MapNoticePlan SelectFileDamaged(string mapName, string file, string clientVersion) =>
            new MapNoticePlan(NoticeId.MapFileDamaged).Set("Map", mapName).Set("file", file ?? "?").Set("ver", clientVersion);
    }
}
