#nullable disable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ArenaMapPort.Client
{
    /// <summary>Every in-game map notice, one id per text (docs/progress/map-notices.md, table of failure states).</summary>
    public enum NoticeId
    {
        // client gate (modals)
        ClientNoArena,
        ClientRegistryUnreadable,
        ClientOverrideBad,
        ClientFolderBad,
        ClientIncomplete,
        ClientCheckFailed,
        ClientCheckSlow,
        ClientHookMissing,
        // server and files (modals)
        ServerNoArena,
        ServerUnknown,
        ServerNoAnswer,
        ServerAllFailed,
        ServerSomeMissing,
        ServerSomeFailed,
        InstallIncomplete,
        // toasts
        CheckPending,
        SelectionReminder,
        NavMeshMissing,
        MapFileDamaged,
        // stage 2 and failed loads (modals, shown after the failed attempt)
        KeyUpdated,
        KeyArenaOlder,
        KeyDiffers,
        KeyUnreadable,
        KeyFileBad,
        KeyInternal,
        BundleBad,
        LoadNoCause,
    }

    /// <summary>How a notice is shown: the modal OK window, or a short toast.</summary>
    public enum NoticeKind
    {
        Modal,
        Toast,
    }

    /// <summary>The first words of a client text: the menu notice (T1) or the answer to a failed raid attempt (T3).</summary>
    public enum NoticeLead
    {
        /// <summary>"The Arena maps are not available".</summary>
        Menu,
        /// <summary>"{Map} could not be loaded".</summary>
        Attempt,
    }

    /// <summary>The reason sentence a text fills into <c>{why}</c>; the selector picks it, the language table words it.</summary>
    public enum NoticeWhy
    {
        None,
        // the client's own Arena folder (ClientOverrideBad, ClientFolderBad)
        FolderMissing,
        ExeMissing,
        ManifestBad,
        // the server's gate (ServerNoArena)
        ServerNotRegistered,
        ServerRegistryUnreadable,
        ServerOverrideIgnored,
        ServerOverrideFolderMissing,
        ServerOverrideNotArena,
        ServerOverrideManifest,
        ServerFolderBad,
        ServerFilesMismatch,
        ServerCheckFailed,
        // the server's registration (ServerAllFailed, ServerSomeFailed)
        MapDataBad,
        RegistrationFailed,
        // the NavMesh toast
        NavUnverified,
        NavFileMissing,
        NavDamaged,
        NavNoKey,
    }

    /// <summary>The action lines of <see cref="NoticeId.ServerNoArena"/>, chosen by the server's cause and the state of its override file.</summary>
    public enum NoticeAction
    {
        None,
        /// <summary>Create the server's arena-install.json with the example content.</summary>
        CreateOverride,
        /// <summary>The file exists but could not be used (not valid JSON, or no "installPath" text value): make it like the example, backslashes doubled.</summary>
        FixIgnored,
        /// <summary>The path in the file is wrong: fix it or delete the file.</summary>
        FixOverridePath,
        /// <summary>The launcher's entry on the server PC points at a folder that cannot be used.</summary>
        FixServerFolder,
        /// <summary>The server's Arena files are incomplete or changed.</summary>
        FilesMismatch,
        /// <summary>The server's check failed unexpectedly.</summary>
        CheckFailed,
    }

    /// <summary>
    /// Which short cause and fix the <see cref="NoticeId.SelectionReminder"/> toast carries (the cause table of the design). One group per fix, so the toast
    /// never sends a player to the wrong remedy: a slow check is not repaired by repairing Arena, a build that does not fit is not repaired by a restart.
    /// </summary>
    public enum ReminderGroup
    {
        None,
        /// <summary>Rows 1-2: Arena was not found on this PC.</summary>
        ArenaNotFound,
        /// <summary>Rows 3-5: the Arena folder or installation cannot be used (the folder set in ArenaInstallPath, the launcher's folder, incomplete files).</summary>
        ClientUnusable,
        /// <summary>Rows 11 and 13: the SPT server does not offer the maps and its ArenaMapPort has to be reinstalled.</summary>
        ServerNoOffer,
        /// <summary>Row 10 with an override-file cause: the server could not verify Arena and its arena-install.json has to be set or fixed.</summary>
        ServerNoVerify,
        /// <summary>Rows 6-7: the client's own Arena check did not finish or failed (restart, then the log).</summary>
        ClientCheck,
        /// <summary>Row 9: this ArenaMapPort build does not fit the game version.</summary>
        ClientBuild,
        /// <summary>Row 12: the SPT server did not answer the question why.</summary>
        ServerNoAnswer,
        /// <summary>Row 10 with a folder or files cause: Arena on the server PC has to be repaired or updated.</summary>
        ServerArena,
        /// <summary>Row 10 with an unexpected failure: the server's check failed and the server restarts.</summary>
        ServerCheck,
    }

    /// <summary>What one notice shows.</summary>
    internal sealed class MapNoticePlan
    {
        internal MapNoticePlan(NoticeId id)
        {
            Id = id;
        }

        internal NoticeId Id { get; }
        internal NoticeLead Lead { get; set; }
        internal NoticeWhy Why { get; set; }
        internal NoticeAction Action { get; set; }
        /// <summary>Set on the notices of rows 1-13: what the selection screen's reminder toast says after this one.</summary>
        internal ReminderGroup Reminder { get; set; }
        /// <summary>The server mod's version when the status probe answered; shown in the footer.</summary>
        internal string ServerVersion { get; set; }
        /// <summary>The Info line fragment <c>client=... server=... offered=...</c> of the "showing" log line.</summary>
        internal string LogSummary { get; set; }

        /// <summary>The raw (unsanitised) values of the dynamic placeholders: Map, maps, path, file, detail, inst, built, n, ver, json, ...</summary>
        internal Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        internal NoticeKind Kind => MapNoticeText.KindOf(Id);

        internal MapNoticePlan Set(string name, string value)
        {
            Values[name] = value;
            return this;
        }

        internal string Get(string name) => Values.TryGetValue(name, out string value) ? value : null;
    }

    /// <summary>A composed notice, ready for the UI.</summary>
    internal sealed class MapNoticeMessage
    {
        internal MapNoticeMessage(NoticeId id, NoticeKind kind, string reference, string caption, string text, bool scrolled)
        {
            Id = id;
            Kind = kind;
            Ref = reference;
            Caption = caption;
            Text = text;
            Scrolled = scrolled;
        }

        internal NoticeId Id { get; }
        internal NoticeKind Kind { get; }
        /// <summary>The ref code printed in the footer (a modal) and logged (both).</summary>
        internal string Ref { get; }
        /// <summary>The window caption (<c>ArenaMapPort</c>); null for a toast.</summary>
        internal string Caption { get; }
        internal string Text { get; }
        /// <summary>True when the text is longer than <see cref="MapNoticeText.ModalLimit"/> and belongs in the scrolled message window.</summary>
        internal bool Scrolled { get; }
    }

    /// <summary>
    /// The words of the in-game map notices (docs/progress/map-notices.md): ids, ref codes, the English master table and its German twin,
    /// and the composition of a <see cref="MapNoticePlan"/> into a message.
    ///
    /// What vanilla does. Nothing: with the Arena gate (release 0.2.3) a PC without Arena, a server without its status, a half-copied ZIP or a stale
    /// key file all end as "the maps are missing" or a generic matching-aborted popup, and the cause sits in a log file. The server's own map
    /// locales cannot carry the texts either: it delivers them only when the gate passed, which is exactly the failing state.
    ///
    /// What this class does. A client-side table in two languages, chosen from EFT's UI language (<see cref="LanguageFor"/>: German, informal "du",
    /// for EFT's German locale code, English for everything else and as the fallback), with the same ids and the same placeholders in both (a
    /// parity test pins it). <see cref="Compose"/> expands the plan's template (lead, reason, action) and substitutes the dynamic values in one
    /// pass, so a value is never read as a placeholder: <c>&lt;</c> and <c>&gt;</c> in a value become parentheses (the window renders TextMeshPro
    /// rich text unescaped), a line break becomes a space, a detail is cut to <see cref="MaxDetailLength"/> characters and a path to
    /// <see cref="MaxPathLength"/> characters in the middle. A modal gets the caption <c>ArenaMapPort</c> and a footer line with the client version and
    /// the ref code (and the server's version when the probe answered), so a screenshot is enough for support; a toast starts with
    /// <c>ArenaMapPort: </c> and has none. A text longer than <see cref="ModalLimit"/> characters is flagged for the scrolled message window.
    /// A value the plan did not supply reads <c>?</c>, never a raw <c>{name}</c> token.
    ///
    /// Not here: any Unity or EFT call, any logging, and the decision which notice applies (<see cref="MapNoticeSelector"/>). No text says
    /// "protect" or "copy protection": the gate is a good-faith installation check, nothing more.
    ///
    /// Host test: docs/progress/map-notices.md (V1 to V8).
    /// </summary>
    internal static class MapNoticeText
    {
        /// <summary>The longest modal text (footer included) that fits the ordinary message window; longer texts go to the scrolled window.</summary>
        internal const int ModalLimit = 480;
        internal const int MaxDetailLength = 80;
        internal const int MaxPathLength = 60;

        internal const string Caption = "ArenaMapPort";
        internal const string ToastPrefix = "ArenaMapPort: ";
        internal const string LanguageEnglish = "en";
        internal const string LanguageGerman = "de";

        /// <summary>The public config key that silences the main-menu messages (decision (b) of the design, 2026-10-22): section, key and the F12 and file comment text.</summary>
        internal const string ShowMapNoticesSection = "General";
        internal const string ShowMapNoticesKey = "ShowMapNotices";
        internal const string ShowMapNoticesDescription =
            "Shows an in-game message at the main menu when the Arena maps are not available, with the reason and what to do. " +
            "Turning it off only hides these messages: the Arena check, the server and the log lines stay as they are, and the message that answers a failed raid start still appears. " +
            "Takes effect when the next message would be shown.";

        /// <summary>The project's issue tracker (decision (d) of the design, 2026-10-22).</summary>
        internal const string SupportLink = "https://github.com/maschine34675/ArenaMapPort/issues";

        /// <summary>Where a player finds the plugin's public settings (the F12 menu of BepInEx's ConfigurationManager).</summary>
        internal const string F12Path = "F12 > maschine-ArenaMapPort > General";

        /// <summary>The server's override file, always as this fixed relative path (never the server's absolute path).</summary>
        internal const string OverridePath = @"SPT_Runtime\user\mods\ArenaMapPort\config\arena-install.json";

        /// <summary>The placeholder token: <c>{name}</c>, a letter first, then letters and digits (<c>{f12}</c>), so the JSON example's braces never match.</summary>
        internal static readonly Regex PlaceholderPattern = new Regex(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant);

        // A body line; T1Only lines (the "to hide this message" hint) are dropped from the answers to a failed raid attempt,
        // because General/ShowMapNotices never silences those.
        struct Line
        {
            internal readonly string Text;
            internal readonly bool MenuOnly;

            internal Line(string text, bool menuOnly)
            {
                Text = text;
                MenuOnly = menuOnly;
            }
        }

        sealed class Table
        {
            internal readonly Dictionary<NoticeId, Line[]> Bodies = new Dictionary<NoticeId, Line[]>();
            internal readonly Dictionary<NoticeLead, string> Leads = new Dictionary<NoticeLead, string>();
            internal readonly Dictionary<NoticeWhy, string> Whys = new Dictionary<NoticeWhy, string>();
            internal readonly Dictionary<NoticeAction, string> Actions = new Dictionary<NoticeAction, string>();
            internal readonly Dictionary<ReminderGroup, string[]> Reminders = new Dictionary<ReminderGroup, string[]>();
        }

        static readonly Table English = BuildEnglish();
        static readonly Table German = BuildGerman();

        // ---- ids ---------------------------------------------------------------------------------------------------

        /// <summary>Every id, in declaration order.</summary>
        internal static IReadOnlyList<NoticeId> AllIds { get; } = (NoticeId[])Enum.GetValues(typeof(NoticeId));

        /// <summary>Modal or toast.</summary>
        internal static NoticeKind KindOf(NoticeId id)
        {
            switch (id)
            {
                case NoticeId.CheckPending:
                case NoticeId.SelectionReminder:
                case NoticeId.NavMeshMissing:
                case NoticeId.MapFileDamaged:
                    return NoticeKind.Toast;
                default:
                    return NoticeKind.Modal;
            }
        }

        /// <summary>The ref code of the footer and of the log line (one per id, unique).</summary>
        internal static string RefOf(NoticeId id)
        {
            switch (id)
            {
                case NoticeId.ClientNoArena: return "NO_ARENA";
                case NoticeId.ClientRegistryUnreadable: return "REGISTRY";
                case NoticeId.ClientOverrideBad: return "OVERRIDE_BAD";
                case NoticeId.ClientFolderBad: return "FOLDER_BAD";
                case NoticeId.ClientIncomplete: return "ARENA_INCOMPLETE";
                case NoticeId.ClientCheckFailed: return "CHECK_FAILED";
                case NoticeId.ClientCheckSlow: return "CHECK_SLOW";
                case NoticeId.ClientHookMissing: return "HOOK_MISSING";
                case NoticeId.ServerNoArena: return "SERVER_NO_ARENA";
                case NoticeId.ServerUnknown: return "SERVER_UNKNOWN";
                case NoticeId.ServerNoAnswer: return "SERVER_NO_ANSWER";
                case NoticeId.ServerAllFailed: return "SERVER_ALL_FAILED";
                case NoticeId.ServerSomeMissing: return "SERVER_PARTIAL";
                case NoticeId.ServerSomeFailed: return "SERVER_PARTIAL_FAILED";
                case NoticeId.InstallIncomplete: return "FILES_MISSING";
                case NoticeId.CheckPending: return "PENDING";
                case NoticeId.SelectionReminder: return "SELECTION";
                case NoticeId.NavMeshMissing: return "NAVMESH";
                case NoticeId.MapFileDamaged: return "FILE_DAMAGED";
                case NoticeId.KeyUpdated: return "KEY_UPDATED";
                case NoticeId.KeyArenaOlder: return "KEY_ARENA_OLDER";
                case NoticeId.KeyDiffers: return "KEY_DIFFERS";
                case NoticeId.KeyUnreadable: return "KEY_UNREADABLE";
                case NoticeId.KeyFileBad: return "KEY_FILE_BAD";
                case NoticeId.KeyInternal: return "KEY_INTERNAL";
                case NoticeId.BundleBad: return "BUNDLE_BAD";
                case NoticeId.LoadNoCause: return "NO_CAUSE";
                default: return id.ToString().ToUpperInvariant();
            }
        }

        // ---- language ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The table language for EFT's UI language code (<c>Settings.Language.Value</c>): German for EFT's German locale ("ge", <c>LocalizationManager.GERMAN_LOCALIZATION</c>
        /// in the 0.16.9.5 decompile; "de", "deu", "ger", "german" and regional forms like "de-DE" are accepted too), English for everything else, including null and empty.
        /// </summary>
        internal static string LanguageFor(string eftLocaleCode)
        {
            if (string.IsNullOrWhiteSpace(eftLocaleCode))
                return LanguageEnglish;
            string code = eftLocaleCode.Trim().ToLowerInvariant();
            if (code == "ge" || code == "de" || code == "deu" || code == "ger" || code == "german" || code == "deutsch")
                return LanguageGerman;
            if (code.Length > 2 && (code[2] == '-' || code[2] == '_') && (code.StartsWith("de", StringComparison.Ordinal) || code.StartsWith("ge", StringComparison.Ordinal)))
                return LanguageGerman;
            return LanguageEnglish;
        }

        static Table TableFor(string language) =>
            string.Equals(language, LanguageGerman, StringComparison.Ordinal) ? German : English;

        // ---- composition -------------------------------------------------------------------------------------------

        /// <summary>
        /// Turns <paramref name="plan"/> into the message of <paramref name="language"/> (null, empty or unknown = English). Never throws for a plan
        /// built by the selector; a value the plan lacks reads <c>?</c>.
        /// </summary>
        internal static MapNoticeMessage Compose(MapNoticePlan plan, string language)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            Table table = TableFor(language);
            NoticeKind kind = KindOf(plan.Id);
            string reference = RefOf(plan.Id);

            if (!table.Bodies.TryGetValue(plan.Id, out Line[] lines))
                lines = English.Bodies[plan.Id];

            var text = new StringBuilder();
            foreach (Line line in lines)
            {
                if (line.MenuOnly && plan.Lead != NoticeLead.Menu)
                    continue;
                if (text.Length > 0)
                    text.Append('\n');
                text.Append(Expand(line.Text, plan, table, 0));
            }

            string body = text.ToString();
            if (kind == NoticeKind.Toast)
                return new MapNoticeMessage(plan.Id, kind, reference, null, ToastPrefix + body, false);

            body += "\n" + Footer(plan, reference);
            return new MapNoticeMessage(plan.Id, kind, reference, Caption, body, body.Length > ModalLimit);
        }

        /// <summary>The selection screen's reminder toast for <paramref name="group"/>: the short cause and the short fix.</summary>
        internal static MapNoticeMessage ComposeReminder(ReminderGroup group, string language)
        {
            var plan = new MapNoticePlan(NoticeId.SelectionReminder) { Reminder = group == ReminderGroup.None ? ReminderGroup.ClientUnusable : group };
            return Compose(plan, language);
        }

        /// <summary>
        /// The toast that stands in for a modal that could not be shown (three displaced attempts, or a window that stayed busy): the reminder of
        /// the plan's group when it has one, otherwise "ArenaMapPort: " and the problem sentence (the first line) of the modal text, cut to
        /// 200 characters.
        /// </summary>
        internal static MapNoticeMessage ComposeFallbackToast(MapNoticePlan plan, string language)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            if (plan.Kind == NoticeKind.Toast)
                return Compose(plan, language);
            if (plan.Reminder != ReminderGroup.None)
                return ComposeReminder(plan.Reminder, language);

            MapNoticeMessage full = Compose(plan, language);
            string first = full.Text;
            int end = first.IndexOf('\n');
            if (end >= 0)
                first = first.Substring(0, end);
            if (first.Length > 200)
                first = first.Substring(0, 197) + "...";
            return new MapNoticeMessage(plan.Id, NoticeKind.Toast, full.Ref, null, ToastPrefix + first, false);
        }

        /// <summary>The footer line of a modal: <c>ArenaMapPort {ver} - ref {REF}</c>, plus <c>, server {sv}</c> when the probe returned a server version.</summary>
        internal static string Footer(MapNoticePlan plan, string reference)
        {
            string version = Sanitize(plan.Get("ver")) ?? "?";
            string footer = "ArenaMapPort " + version + " - ref " + reference;
            if (!string.IsNullOrWhiteSpace(plan.ServerVersion))
                footer += ", server " + Sanitize(plan.ServerVersion);
            return footer;
        }

        static string Expand(string template, MapNoticePlan plan, Table table, int depth)
        {
            if (depth > 3)
                return template;
            return PlaceholderPattern.Replace(template, match => Resolve(match.Groups[1].Value, plan, table, depth));
        }

        static string Resolve(string name, MapNoticePlan plan, Table table, int depth)
        {
            switch (name)
            {
                case "lead":
                    return Expand(Lookup(table.Leads, English.Leads, plan.Lead), plan, table, depth + 1);
                case "why":
                    return plan.Why == NoticeWhy.None ? "?" : Expand(Lookup(table.Whys, English.Whys, plan.Why), plan, table, depth + 1);
                case "action":
                    return plan.Action == NoticeAction.None ? "?" : Expand(Lookup(table.Actions, English.Actions, plan.Action), plan, table, depth + 1);
                case "short":
                    return ReminderPart(plan, table, 0);
                case "fix":
                    return ReminderPart(plan, table, 1);
                case "f12":
                    return F12Path;
                case "ovr":
                    return OverridePath;
                case "support":
                    return SupportLink;
                case "detail":
                    return Truncate(Sanitize(plan.Get(name)), MaxDetailLength) ?? "?";
                case "path":
                    return ShortenMiddle(Sanitize(plan.Get(name)), MaxPathLength) ?? "?";
                default:
                    return Sanitize(plan.Get(name)) ?? "?";
            }
        }

        static string ReminderPart(MapNoticePlan plan, Table table, int index)
        {
            ReminderGroup group = plan.Reminder == ReminderGroup.None ? ReminderGroup.ClientUnusable : plan.Reminder;
            if (!table.Reminders.TryGetValue(group, out string[] parts))
                parts = English.Reminders[group];
            return parts[index];
        }

        static string Lookup<TKey>(Dictionary<TKey, string> primary, Dictionary<TKey, string> fallback, TKey key)
        {
            if (primary.TryGetValue(key, out string value))
                return value;
            return fallback.TryGetValue(key, out value) ? value : "?";
        }

        /// <summary>
        /// A dynamic value made safe for the window: <c>&lt;</c> becomes <c>(</c> and <c>&gt;</c> becomes <c>)</c> (the window renders rich text
        /// unescaped), line breaks and tabs become spaces, the ends are trimmed. Null stays null.
        /// </summary>
        internal static string Sanitize(string value)
        {
            if (value == null)
                return null;
            var text = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '<': text.Append('('); break;
                    case '>': text.Append(')'); break;
                    case '\r':
                    case '\n':
                    case '\t':
                        text.Append(' ');
                        break;
                    default:
                        text.Append(c);
                        break;
                }
            }
            return text.ToString().Trim();
        }

        /// <summary>At most <paramref name="max"/> characters; a longer text is cut and ends in "...".</summary>
        internal static string Truncate(string value, int max)
        {
            if (value == null || value.Length <= max)
                return value;
            return value.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        /// <summary>At most <paramref name="max"/> characters; a longer text keeps its start and its end and has "..." in the middle.</summary>
        internal static string ShortenMiddle(string value, int max)
        {
            if (value == null || value.Length <= max)
                return value;
            int keep = Math.Max(0, max - 3);
            int head = keep / 2;
            int tail = keep - head;
            return value.Substring(0, head) + "..." + value.Substring(value.Length - tail);
        }

        /// <summary>
        /// The pre-built example of the server's override file: <c>{"installPath": "&lt;the client's Arena path, backslashes doubled&gt;"}</c>. It is one
        /// value (never expanded as placeholders). A null or empty path gives a neutral example path.
        /// </summary>
        internal static string BuildJsonExample(string clientInstallPath)
        {
            string path = string.IsNullOrWhiteSpace(clientInstallPath) ? @"D:\Battlestate Games\Escape from Tarkov Arena" : clientInstallPath.Trim();
            string escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "{\"installPath\": \"" + escaped + "\"}";
        }

        // ---- introspection for the parity and lint tests -------------------------------------------------------------

        /// <summary>The raw template lines of <paramref name="id"/> in <paramref name="language"/> (placeholders unexpanded).</summary>
        internal static IReadOnlyList<string> TemplateLines(NoticeId id, string language)
        {
            Table table = TableFor(language);
            if (!table.Bodies.TryGetValue(id, out Line[] lines))
                return new string[0];
            var result = new List<string>();
            foreach (Line line in lines)
                result.Add(line.Text);
            return result;
        }

        /// <summary>Whether line <paramref name="index"/> of <paramref name="id"/> is dropped from the answers to a failed raid attempt.</summary>
        internal static bool IsMenuOnlyLine(NoticeId id, string language, int index)
        {
            Table table = TableFor(language);
            return table.Bodies.TryGetValue(id, out Line[] lines) && index >= 0 && index < lines.Length && lines[index].MenuOnly;
        }

        /// <summary>Every sub-template text (lead, reason, action, reminder parts) of <paramref name="language"/>, keyed by a readable name.</summary>
        internal static IReadOnlyDictionary<string, string> SubTemplates(string language)
        {
            Table table = TableFor(language);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in table.Leads)
                result["lead:" + pair.Key] = pair.Value;
            foreach (var pair in table.Whys)
                result["why:" + pair.Key] = pair.Value;
            foreach (var pair in table.Actions)
                result["action:" + pair.Key] = pair.Value;
            foreach (var pair in table.Reminders)
            {
                result["short:" + pair.Key] = pair.Value[0];
                result["fix:" + pair.Key] = pair.Value[1];
            }
            return result;
        }

        /// <summary>The placeholder names that occur in <paramref name="text"/>.</summary>
        internal static ISet<string> PlaceholdersIn(string text)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            if (text != null)
                foreach (Match match in PlaceholderPattern.Matches(text))
                    names.Add(match.Groups[1].Value);
            return names;
        }

        // ---- the tables ----------------------------------------------------------------------------------------------

        static Line L(string text) => new Line(text, false);

        static Line MenuOnly(string text) => new Line(text, true);

        static Table BuildEnglish()
        {
            var t = new Table();

            t.Leads[NoticeLead.Menu] = "The Arena maps are not available";
            t.Leads[NoticeLead.Attempt] = "{Map} could not be loaded";

            // ---- client gate (modals)
            t.Bodies[NoticeId.ClientNoArena] = new[]
            {
                L("{lead}: ArenaMapPort did not find Escape from Tarkov: Arena on this PC."),
                L("Install Arena with the Battlestate Games launcher (the maps are built from its files), then restart the game and the SPT server."),
                L("Arena installed but not found? Set its folder in {f12} > ArenaInstallPath and in the server file arena-install.json (see the README)."),
                MenuOnly("To hide this message: {f12} > ShowMapNotices."),
            };
            t.Bodies[NoticeId.ClientRegistryUnreadable] = new[]
            {
                L("{lead}: ArenaMapPort could not read the Windows registry to look for Escape from Tarkov: Arena."),
                L(@"Restart the game once. If this repeats, set the Arena folder (the one with EscapeFromTarkovArena.exe) in {f12} > ArenaInstallPath and send the lines marked [Arena] from BepInEx\LogOutput.log to {support}."),
            };
            t.Bodies[NoticeId.ClientOverrideBad] = new[]
            {
                L("{lead}: the folder set in {f12} > ArenaInstallPath cannot be used ({why})."),
                L("Set it to the Arena folder that contains EscapeFromTarkovArena.exe (not the launcher folder), or clear it to look Arena up automatically. Restart the game afterwards."),
                MenuOnly("To hide this message: {f12} > ShowMapNotices."),
            };
            t.Bodies[NoticeId.ClientFolderBad] = new[]
            {
                L("{lead}: the launcher lists Arena at {path}, but {why}."),
                L("Reinstall or update Arena in the Battlestate Games launcher (or fix the folder if you moved it), then restart the game and the SPT server."),
            };
            t.Bodies[NoticeId.ClientIncomplete] = new[]
            {
                L("{lead}: your Arena installation at {path} looks incomplete or is still updating (files missing or changed: {n})."),
                L("Let the launcher finish updating, or check the installation there, then restart the game and the SPT server. Arena has to be complete and up to date."),
            };
            t.Bodies[NoticeId.ClientCheckFailed] = new[]
            {
                L("{lead}: ArenaMapPort could not check your Arena installation because of an internal error ({detail})."),
                L(@"Restart the game. If this repeats, send the lines marked [Arena] from BepInEx\LogOutput.log to {support}."),
            };
            t.Bodies[NoticeId.ClientCheckSlow] = new[]
            {
                L("{lead}: ArenaMapPort's check of your Arena installation did not finish in time (slow disk or unreachable folder)."),
                L("Start the raid again. If this repeats, check that the Arena folder, or the folder set in {f12} > ArenaInstallPath, is on a local, connected drive."),
            };
            t.Bodies[NoticeId.ClientHookMissing] = new[]
            {
                L("{lead}: this ArenaMapPort build does not fit your game version (part {detail} could not be attached)."),
                L("Install the ArenaMapPort release that matches your SPT version and restart the game. If that does not help, report it at {support}."),
            };

            // ---- server and files (modals)
            t.Bodies[NoticeId.ServerNoArena] = new[]
            {
                L("The Arena maps are not offered: the SPT server could not verify Escape from Tarkov: Arena ({why}), although this game found it."),
                L("{action}"),
            };
            t.Bodies[NoticeId.ServerUnknown] = new[]
            {
                L("The SPT server does not offer the Arena maps and did not say why: its ArenaMapPort mod is missing, older than this game's ({ver}), or could not find Arena."),
                L("Close the server, reinstall both parts of ArenaMapPort from the same ZIP, then restart the server and this game. The server console shows the reason in lines starting with [ArenaMapPort]. Server on another PC? Ask whoever runs it."),
            };
            t.Bodies[NoticeId.ServerNoAnswer] = new[]
            {
                L("The Arena maps are not offered, and the SPT server did not answer ArenaMapPort's question why (timeout or error)."),
                L("Restart the SPT server, then this game. If this repeats, the server console shows the reason in lines starting with [ArenaMapPort]."),
            };
            t.Bodies[NoticeId.ServerAllFailed] = new[]
            {
                L("The SPT server could not register any Arena map ({why})."),
                L(@"Close the server, reinstall the ArenaMapPort server files (SPT_Runtime\user\mods\ArenaMapPort) from the ZIP and restart it. The console shows the reason in lines starting with [ArenaMapPort]."),
            };
            t.Bodies[NoticeId.ServerSomeMissing] = new[]
            {
                L("The SPT server does not offer {maps}, which this game knows. The server's ArenaMapPort is probably older than this game's ({ver})."),
                L(@"Close the SPT server (a running server keeps its DLL locked), copy the SPT_Runtime folder of the same ZIP over the installation (overwrite its files), then restart it. Console lines starting with [ArenaMapPort] show details."),
            };
            t.Bodies[NoticeId.ServerSomeFailed] = new[]
            {
                L("The SPT server could not register {maps} ({why})."),
                L(@"Close the server, reinstall the ArenaMapPort server files (SPT_Runtime\user\mods\ArenaMapPort) from the ZIP, and restart it. Console lines starting with [ArenaMapPort] show the reason."),
            };
            t.Bodies[NoticeId.InstallIncomplete] = new[]
            {
                L("ArenaMapPort's files for {maps} are incomplete (missing: {n}, for example {file})."),
                L(@"Extract the complete ZIP again (client folder BepInEx\plugins\maschine-ArenaMapPort, with its bundles and keys), then restart the game."),
            };

            // ---- the NoArena why and action tables of the server text
            t.Whys[NoticeWhy.FolderMissing] = "the folder does not exist";
            t.Whys[NoticeWhy.ExeMissing] = "EscapeFromTarkovArena.exe is not in it";
            t.Whys[NoticeWhy.ManifestBad] = "its launcher file list is missing or does not look like Arena's";
            t.Whys[NoticeWhy.ServerNotRegistered] = "no launcher entry for Arena on the server PC";
            t.Whys[NoticeWhy.ServerRegistryUnreadable] = "the server cannot read the Windows registry";
            t.Whys[NoticeWhy.ServerOverrideIgnored] = "its arena-install.json was ignored: it is not valid JSON or has no \"installPath\" text value";
            t.Whys[NoticeWhy.ServerOverrideFolderMissing] = "the folder set in arena-install.json does not exist";
            t.Whys[NoticeWhy.ServerOverrideNotArena] = "EscapeFromTarkovArena.exe is not in that folder";
            t.Whys[NoticeWhy.ServerOverrideManifest] = "the launcher file list in that folder is missing or invalid";
            t.Whys[NoticeWhy.ServerFolderBad] = "the Arena folder the launcher lists on the server PC cannot be used";
            t.Whys[NoticeWhy.ServerFilesMismatch] = "its Arena files are incomplete or changed";
            t.Whys[NoticeWhy.ServerCheckFailed] = "its check failed unexpectedly";
            t.Whys[NoticeWhy.MapDataBad] = "its map data is missing or damaged";
            t.Whys[NoticeWhy.RegistrationFailed] = "registration failed";
            t.Whys[NoticeWhy.NavUnverified] = "Arena was not verified";
            t.Whys[NoticeWhy.NavFileMissing] = "the file is missing";
            t.Whys[NoticeWhy.NavDamaged] = "the file is damaged";
            t.Whys[NoticeWhy.NavNoKey] = "its unlock file could not be used";

            t.Actions[NoticeAction.CreateOverride] =
                "If the server runs on this PC, create {ovr} (create the folder config if needed; the name must end in .json, not .json.txt) with this content:\n" +
                "{json}\n" +
                "Then restart the server. If it runs on another PC, ask whoever runs it.";
            t.Actions[NoticeAction.FixIgnored] =
                @"The file {ovr} exists but could not be used, so it was ignored. Make it exactly like this, with every backslash in the path doubled (\\) or written as /:" + "\n" +
                "{json}\n" +
                "Then restart the server.";
            t.Actions[NoticeAction.FixOverridePath] =
                "Fix the path in {ovr}, or delete that file to use the launcher's entry, then restart the server.";
            t.Actions[NoticeAction.FixServerFolder] =
                "Reinstall or update Arena with the Battlestate Games launcher on the server PC, then restart the server.";
            t.Actions[NoticeAction.FilesMismatch] =
                "Let the Battlestate Games launcher finish updating Arena on the server PC, or check the installation there, then restart the server.";
            t.Actions[NoticeAction.CheckFailed] =
                "Restart the SPT server. If this repeats, send its console lines starting with [ArenaMapPort] to {support}.";

            // ---- toasts
            t.Bodies[NoticeId.CheckPending] = new[]
            {
                L("still checking your Arena installation (slow disk or unreachable path). The Arena maps load once it passes."),
            };
            t.Bodies[NoticeId.SelectionReminder] = new[]
            {
                L("the Arena maps are not available ({short}). {fix}"),
            };
            t.Bodies[NoticeId.NavMeshMissing] = new[]
            {
                L(@"{Map}: the navigation data could not be loaded ({why}); bots will not move. Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [NavMesh]."),
            };
            t.Bodies[NoticeId.MapFileDamaged] = new[]
            {
                L(@"a map file for {Map} reported damage during the raid ({file}). Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [Bundles]."),
            };
            t.Reminders[ReminderGroup.ArenaNotFound] = new[]
            {
                "Arena was not found on this PC",
                "Install Arena with the launcher, or set F12 > ArenaInstallPath, then restart.",
            };
            t.Reminders[ReminderGroup.ClientUnusable] = new[]
            {
                "the Arena installation cannot be used",
                "Repair Arena or fix F12 > ArenaInstallPath, then restart.",
            };
            t.Reminders[ReminderGroup.ServerNoOffer] = new[]
            {
                "the SPT server does not offer them",
                "Reinstall the server's ArenaMapPort and restart the server.",
            };
            t.Reminders[ReminderGroup.ServerNoVerify] = new[]
            {
                "the server could not verify Arena",
                "Set or fix the server's arena-install.json (see the README), then restart it.",
            };
            t.Reminders[ReminderGroup.ClientCheck] = new[]
            {
                "the check of your Arena installation did not finish",
                @"Restart the game; the [Arena] lines of BepInEx\LogOutput.log say more.",
            };
            t.Reminders[ReminderGroup.ClientBuild] = new[]
            {
                "this ArenaMapPort build does not fit your game version",
                "Install the ArenaMapPort release for your SPT version, then restart.",
            };
            t.Reminders[ReminderGroup.ServerNoAnswer] = new[]
            {
                "the SPT server did not answer",
                "Restart the SPT server, then this game.",
            };
            t.Reminders[ReminderGroup.ServerArena] = new[]
            {
                "the server's Arena installation cannot be used",
                "Repair or update Arena on the server PC, then restart the server.",
            };
            t.Reminders[ReminderGroup.ServerCheck] = new[]
            {
                "the server's Arena check failed",
                "Restart the SPT server. If this repeats, see its console.",
            };

            // ---- stage 2 and failed loads (modals)
            t.Bodies[NoticeId.KeyUpdated] = new[]
            {
                L("{Map} cannot be opened: Arena {inst} is installed, but this ArenaMapPort was built for Arena {built}, and files it needs have changed."),
                L("Install the newest ArenaMapPort release, which includes the matching unlock files, then restart the game."),
            };
            t.Bodies[NoticeId.KeyArenaOlder] = new[]
            {
                L("{Map} cannot be opened: this ArenaMapPort was built for Arena {built}, but Arena {inst} is installed."),
                L("Update Arena in the Battlestate Games launcher, then restart the game."),
            };
            t.Bodies[NoticeId.KeyDiffers] = new[]
            {
                L("{Map} cannot be opened: the Arena files it is built from do not match ArenaMapPort's unlock file (Arena {inst})."),
                L("Repair Arena in the Battlestate Games launcher (damaged or modified files), then restart the game. If it stays this way, your Arena version needs an ArenaMapPort update."),
            };
            t.Bodies[NoticeId.KeyUnreadable] = new[]
            {
                L("{Map} cannot be opened: ArenaMapPort cannot read an Arena file it needs (it is locked or access is denied)."),
                L("Close the launcher and any scan of the Arena folder, check the folder's permissions, then restart the game."),
            };
            t.Bodies[NoticeId.KeyFileBad] = new[]
            {
                L("{Map} cannot be opened: ArenaMapPort's unlock file for it ({file}) is missing or damaged."),
                L(@"Reinstall ArenaMapPort and copy the complete client folder (BepInEx\plugins\maschine-ArenaMapPort, including its keys folder), then restart the game."),
            };
            t.Bodies[NoticeId.KeyInternal] = new[]
            {
                L("{Map} cannot be opened because of an internal error while reading your Arena files ({detail})."),
                L(@"Restart the game. If this repeats, send the lines marked [BundleKeys] from BepInEx\LogOutput.log to {support}."),
            };
            t.Bodies[NoticeId.BundleBad] = new[]
            {
                L("{Map} could not be loaded: one of ArenaMapPort's map files is missing or damaged ({file})."),
                L(@"Reinstall ArenaMapPort and copy the complete client folder (bundles and keys included), then restart the game. Details: BepInEx\LogOutput.log, lines with [Bundles]."),
            };
            t.Bodies[NoticeId.LoadNoCause] = new[]
            {
                L("{Map} did not start, and ArenaMapPort recorded no cause for it."),
                L(@"Check BepInEx\LogOutput.log (lines marked [Arena], [Bundles], [BundleKeys], [NavMesh]) and the game's Logs folder (the errors.log of the newest session), and send them to {support}."),
            };

            return t;
        }

        static Table BuildGerman()
        {
            var t = new Table();

            t.Leads[NoticeLead.Menu] = "Die Arena-Karten sind nicht verfügbar";
            t.Leads[NoticeLead.Attempt] = "{Map} konnte nicht geladen werden";

            // ---- client gate (modals)
            t.Bodies[NoticeId.ClientNoArena] = new[]
            {
                L("{lead}: ArenaMapPort hat Escape from Tarkov: Arena auf diesem PC nicht gefunden."),
                L("Installiere Arena mit dem Battlestate-Games-Launcher (die Karten werden aus dessen Dateien gebaut) und starte dann das Spiel und den SPT-Server neu."),
                L("Arena ist installiert, wird aber nicht gefunden? Trage den Ordner in {f12} > ArenaInstallPath und in der Server-Datei arena-install.json ein (siehe README)."),
                MenuOnly("Um diese Meldung auszublenden: {f12} > ShowMapNotices."),
            };
            t.Bodies[NoticeId.ClientRegistryUnreadable] = new[]
            {
                L("{lead}: ArenaMapPort konnte die Windows-Registry nicht lesen, um Escape from Tarkov: Arena zu finden."),
                L(@"Starte das Spiel einmal neu. Wenn das wieder passiert, trage den Arena-Ordner (den mit EscapeFromTarkovArena.exe) in {f12} > ArenaInstallPath ein und schicke die mit [Arena] markierten Zeilen aus BepInEx\LogOutput.log an {support}."),
            };
            t.Bodies[NoticeId.ClientOverrideBad] = new[]
            {
                L("{lead}: Der in {f12} > ArenaInstallPath eingetragene Ordner kann nicht verwendet werden ({why})."),
                L("Trage den Arena-Ordner ein, der EscapeFromTarkovArena.exe enthält (nicht den Launcher-Ordner), oder leere das Feld, damit Arena automatisch gesucht wird. Starte danach das Spiel neu."),
                MenuOnly("Um diese Meldung auszublenden: {f12} > ShowMapNotices."),
            };
            t.Bodies[NoticeId.ClientFolderBad] = new[]
            {
                L("{lead}: Der Launcher meldet Arena unter {path}, aber {why}."),
                L("Installiere oder aktualisiere Arena im Battlestate-Games-Launcher neu (oder korrigiere den Ordner, falls du ihn verschoben hast) und starte dann das Spiel und den SPT-Server neu."),
            };
            t.Bodies[NoticeId.ClientIncomplete] = new[]
            {
                L("{lead}: Deine Arena-Installation unter {path} wirkt unvollständig oder wird noch aktualisiert (fehlende oder geänderte Dateien: {n})."),
                L("Lass den Launcher das Update beenden oder prüfe dort die Installation und starte dann das Spiel und den SPT-Server neu. Arena muss vollständig und aktuell sein."),
            };
            t.Bodies[NoticeId.ClientCheckFailed] = new[]
            {
                L("{lead}: ArenaMapPort konnte deine Arena-Installation wegen eines internen Fehlers nicht prüfen ({detail})."),
                L(@"Starte das Spiel neu. Wenn das wieder passiert, schicke die mit [Arena] markierten Zeilen aus BepInEx\LogOutput.log an {support}."),
            };
            t.Bodies[NoticeId.ClientCheckSlow] = new[]
            {
                L("{lead}: Die Prüfung deiner Arena-Installation durch ArenaMapPort wurde nicht rechtzeitig fertig (langsame Platte oder nicht erreichbarer Ordner)."),
                L("Starte den Raid erneut. Wenn das wieder passiert, prüfe, ob der Arena-Ordner oder der in {f12} > ArenaInstallPath eingetragene Ordner auf einem lokalen, verbundenen Laufwerk liegt."),
            };
            t.Bodies[NoticeId.ClientHookMissing] = new[]
            {
                L("{lead}: Dieser ArenaMapPort-Build passt nicht zu deiner Spielversion (Teil {detail} konnte nicht angebunden werden)."),
                L("Installiere die ArenaMapPort-Version, die zu deiner SPT-Version passt, und starte das Spiel neu. Hilft das nicht, melde es unter {support}."),
            };

            // ---- server and files (modals)
            t.Bodies[NoticeId.ServerNoArena] = new[]
            {
                L("Die Arena-Karten werden nicht angeboten: Der SPT-Server konnte Escape from Tarkov: Arena nicht verifizieren ({why}), obwohl dieses Spiel es gefunden hat."),
                L("{action}"),
            };
            t.Bodies[NoticeId.ServerUnknown] = new[]
            {
                L("Der SPT-Server bietet die Arena-Karten nicht an und hat nicht gesagt, warum: Sein ArenaMapPort fehlt, ist älter als das ArenaMapPort dieses Spiels ({ver}) oder konnte Arena nicht finden."),
                L("Schließe den Server, installiere beide Teile von ArenaMapPort aus demselben ZIP neu und starte dann den Server und dieses Spiel neu. Die Server-Konsole nennt den Grund in Zeilen, die mit [ArenaMapPort] beginnen. Läuft der Server auf einem anderen PC? Frage die Person, die ihn betreibt."),
            };
            t.Bodies[NoticeId.ServerNoAnswer] = new[]
            {
                L("Die Arena-Karten werden nicht angeboten, und der SPT-Server hat ArenaMapPorts Frage nach dem Grund nicht beantwortet (Zeitüberschreitung oder Fehler)."),
                L("Starte den SPT-Server und danach dieses Spiel neu. Wenn das wieder passiert, nennt die Server-Konsole den Grund in Zeilen, die mit [ArenaMapPort] beginnen."),
            };
            t.Bodies[NoticeId.ServerAllFailed] = new[]
            {
                L("Der SPT-Server konnte keine Arena-Karte registrieren ({why})."),
                L(@"Schließe den Server, installiere die ArenaMapPort-Serverdateien (SPT_Runtime\user\mods\ArenaMapPort) aus dem ZIP neu und starte ihn wieder. Die Konsole nennt den Grund in Zeilen, die mit [ArenaMapPort] beginnen."),
            };
            t.Bodies[NoticeId.ServerSomeMissing] = new[]
            {
                L("Der SPT-Server bietet {maps} nicht an, dieses Spiel kennt sie aber. Das ArenaMapPort des Servers ist wahrscheinlich älter als das dieses Spiels ({ver})."),
                L(@"Schließe den SPT-Server (ein laufender Server sperrt seine DLL), kopiere den Ordner SPT_Runtime aus demselben ZIP über die Installation (vorhandene Dateien überschreiben) und starte ihn neu. Konsolenzeilen, die mit [ArenaMapPort] beginnen, zeigen Details."),
            };
            t.Bodies[NoticeId.ServerSomeFailed] = new[]
            {
                L("Der SPT-Server konnte {maps} nicht registrieren ({why})."),
                L(@"Schließe den Server, installiere die ArenaMapPort-Serverdateien (SPT_Runtime\user\mods\ArenaMapPort) aus dem ZIP neu und starte ihn neu. Konsolenzeilen, die mit [ArenaMapPort] beginnen, zeigen den Grund."),
            };
            t.Bodies[NoticeId.InstallIncomplete] = new[]
            {
                L("Die Dateien von ArenaMapPort für {maps} sind unvollständig (fehlend: {n}, zum Beispiel {file})."),
                L(@"Entpacke das komplette ZIP erneut (Client-Ordner BepInEx\plugins\maschine-ArenaMapPort mit seinen Bundles und Keys) und starte dann das Spiel neu."),
            };

            t.Whys[NoticeWhy.FolderMissing] = "der Ordner existiert nicht";
            t.Whys[NoticeWhy.ExeMissing] = "EscapeFromTarkovArena.exe liegt nicht darin";
            t.Whys[NoticeWhy.ManifestBad] = "seine Launcher-Dateiliste fehlt oder sieht nicht nach Arena aus";
            t.Whys[NoticeWhy.ServerNotRegistered] = "auf dem Server-PC gibt es keinen Launcher-Eintrag für Arena";
            t.Whys[NoticeWhy.ServerRegistryUnreadable] = "der Server kann die Windows-Registry nicht lesen";
            t.Whys[NoticeWhy.ServerOverrideIgnored] = "seine arena-install.json wurde ignoriert: kein gültiges JSON oder kein Textwert \"installPath\"";
            t.Whys[NoticeWhy.ServerOverrideFolderMissing] = "der in arena-install.json eingetragene Ordner existiert nicht";
            t.Whys[NoticeWhy.ServerOverrideNotArena] = "EscapeFromTarkovArena.exe liegt nicht in diesem Ordner";
            t.Whys[NoticeWhy.ServerOverrideManifest] = "die Launcher-Dateiliste in diesem Ordner fehlt oder ist ungültig";
            t.Whys[NoticeWhy.ServerFolderBad] = "der Arena-Ordner, den der Launcher auf dem Server-PC nennt, kann nicht verwendet werden";
            t.Whys[NoticeWhy.ServerFilesMismatch] = "seine Arena-Dateien sind unvollständig oder verändert";
            t.Whys[NoticeWhy.ServerCheckFailed] = "seine Prüfung ist unerwartet fehlgeschlagen";
            t.Whys[NoticeWhy.MapDataBad] = "seine Kartendaten fehlen oder sind beschädigt";
            t.Whys[NoticeWhy.RegistrationFailed] = "die Registrierung ist fehlgeschlagen";
            t.Whys[NoticeWhy.NavUnverified] = "Arena wurde nicht verifiziert";
            t.Whys[NoticeWhy.NavFileMissing] = "die Datei fehlt";
            t.Whys[NoticeWhy.NavDamaged] = "die Datei ist beschädigt";
            t.Whys[NoticeWhy.NavNoKey] = "seine Entsperr-Datei konnte nicht verwendet werden";

            t.Actions[NoticeAction.CreateOverride] =
                "Wenn der Server auf diesem PC läuft, lege {ovr} an (erstelle bei Bedarf den Ordner config; der Name muss auf .json enden, nicht auf .json.txt) mit diesem Inhalt:\n" +
                "{json}\n" +
                "Starte danach den Server neu. Läuft er auf einem anderen PC, frage die Person, die ihn betreibt.";
            t.Actions[NoticeAction.FixIgnored] =
                @"Die Datei {ovr} existiert, konnte aber nicht verwendet werden und wurde deshalb ignoriert. Schreibe sie genau so, mit jedem Backslash im Pfad verdoppelt (\\) oder als /:" + "\n" +
                "{json}\n" +
                "Starte danach den Server neu.";
            t.Actions[NoticeAction.FixOverridePath] =
                "Korrigiere den Pfad in {ovr} oder lösche diese Datei, damit der Eintrag des Launchers genutzt wird, und starte dann den Server neu.";
            t.Actions[NoticeAction.FixServerFolder] =
                "Installiere oder aktualisiere Arena mit dem Battlestate-Games-Launcher auf dem Server-PC neu und starte dann den Server neu.";
            t.Actions[NoticeAction.FilesMismatch] =
                "Lass den Battlestate-Games-Launcher das Update von Arena auf dem Server-PC beenden oder prüfe dort die Installation und starte dann den Server neu.";
            t.Actions[NoticeAction.CheckFailed] =
                "Starte den SPT-Server neu. Wenn das wieder passiert, schicke seine Konsolenzeilen, die mit [ArenaMapPort] beginnen, an {support}.";

            // ---- toasts
            t.Bodies[NoticeId.CheckPending] = new[]
            {
                L("Deine Arena-Installation wird noch geprüft (langsame Platte oder nicht erreichbarer Pfad). Die Arena-Karten laden, sobald die Prüfung besteht."),
            };
            t.Bodies[NoticeId.SelectionReminder] = new[]
            {
                L("Die Arena-Karten sind nicht verfügbar ({short}). {fix}"),
            };
            t.Bodies[NoticeId.NavMeshMissing] = new[]
            {
                L(@"{Map}: Die Navigationsdaten konnten nicht geladen werden ({why}); Bots bewegen sich nicht. Installiere den kompletten Client-Ordner neu. Details: BepInEx\LogOutput.log, Zeilen [NavMesh]."),
            };
            t.Bodies[NoticeId.MapFileDamaged] = new[]
            {
                L(@"Eine Kartendatei für {Map} hat während des Raids eine Beschädigung gemeldet ({file}). Installiere den kompletten Client-Ordner neu. Details: BepInEx\LogOutput.log, Zeilen [Bundles]."),
            };
            t.Reminders[ReminderGroup.ArenaNotFound] = new[]
            {
                "Arena wurde auf diesem PC nicht gefunden",
                "Installiere Arena mit dem Launcher oder setze F12 > ArenaInstallPath und starte neu.",
            };
            t.Reminders[ReminderGroup.ClientUnusable] = new[]
            {
                "die Arena-Installation kann nicht verwendet werden",
                "Repariere Arena oder korrigiere F12 > ArenaInstallPath und starte neu.",
            };
            t.Reminders[ReminderGroup.ServerNoOffer] = new[]
            {
                "der SPT-Server bietet sie nicht an",
                "Installiere das ArenaMapPort des Servers neu und starte den Server neu.",
            };
            t.Reminders[ReminderGroup.ServerNoVerify] = new[]
            {
                "der Server konnte Arena nicht verifizieren",
                "Lege die arena-install.json des Servers an oder korrigiere sie (siehe README) und starte ihn neu.",
            };
            t.Reminders[ReminderGroup.ClientCheck] = new[]
            {
                "die Prüfung deiner Arena-Installation wurde nicht fertig",
                @"Starte das Spiel neu; die mit [Arena] markierten Zeilen in BepInEx\LogOutput.log sagen mehr.",
            };
            t.Reminders[ReminderGroup.ClientBuild] = new[]
            {
                "dieser ArenaMapPort-Build passt nicht zu deiner Spielversion",
                "Installiere die ArenaMapPort-Version für deine SPT-Version und starte neu.",
            };
            t.Reminders[ReminderGroup.ServerNoAnswer] = new[]
            {
                "der SPT-Server hat nicht geantwortet",
                "Starte den SPT-Server und danach dieses Spiel neu.",
            };
            t.Reminders[ReminderGroup.ServerArena] = new[]
            {
                "die Arena-Installation des Servers kann nicht verwendet werden",
                "Repariere oder aktualisiere Arena auf dem Server-PC und starte den Server neu.",
            };
            t.Reminders[ReminderGroup.ServerCheck] = new[]
            {
                "die Arena-Prüfung des Servers ist fehlgeschlagen",
                "Starte den SPT-Server neu. Wenn das wieder passiert, sieh in seiner Konsole nach.",
            };

            // ---- stage 2 and failed loads (modals)
            t.Bodies[NoticeId.KeyUpdated] = new[]
            {
                L("{Map} lässt sich nicht öffnen: Arena {inst} ist installiert, aber dieses ArenaMapPort wurde für Arena {built} gebaut, und Dateien, die es braucht, haben sich geändert."),
                L("Installiere die neueste ArenaMapPort-Version, die die passenden Entsperr-Dateien enthält, und starte dann das Spiel neu."),
            };
            t.Bodies[NoticeId.KeyArenaOlder] = new[]
            {
                L("{Map} lässt sich nicht öffnen: Dieses ArenaMapPort wurde für Arena {built} gebaut, aber Arena {inst} ist installiert."),
                L("Aktualisiere Arena im Battlestate-Games-Launcher und starte dann das Spiel neu."),
            };
            t.Bodies[NoticeId.KeyDiffers] = new[]
            {
                L("{Map} lässt sich nicht öffnen: Die Arena-Dateien, aus denen sie gebaut ist, passen nicht zur Entsperr-Datei von ArenaMapPort (Arena {inst})."),
                L("Repariere Arena im Battlestate-Games-Launcher (beschädigte oder veränderte Dateien) und starte dann das Spiel neu. Wenn es dabei bleibt, braucht deine Arena-Version ein ArenaMapPort-Update."),
            };
            t.Bodies[NoticeId.KeyUnreadable] = new[]
            {
                L("{Map} lässt sich nicht öffnen: ArenaMapPort kann eine benötigte Arena-Datei nicht lesen (sie ist gesperrt oder der Zugriff wurde verweigert)."),
                L("Schließe den Launcher und jeden Scan des Arena-Ordners, prüfe die Berechtigungen des Ordners und starte dann das Spiel neu."),
            };
            t.Bodies[NoticeId.KeyFileBad] = new[]
            {
                L("{Map} lässt sich nicht öffnen: Die Entsperr-Datei von ArenaMapPort dafür ({file}) fehlt oder ist beschädigt."),
                L(@"Installiere ArenaMapPort neu und kopiere den kompletten Client-Ordner (BepInEx\plugins\maschine-ArenaMapPort samt Ordner keys), dann starte das Spiel neu."),
            };
            t.Bodies[NoticeId.KeyInternal] = new[]
            {
                L("{Map} lässt sich nicht öffnen: Beim Lesen deiner Arena-Dateien ist ein interner Fehler aufgetreten ({detail})."),
                L(@"Starte das Spiel neu. Wenn das wieder passiert, schicke die mit [BundleKeys] markierten Zeilen aus BepInEx\LogOutput.log an {support}."),
            };
            t.Bodies[NoticeId.BundleBad] = new[]
            {
                L("{Map} konnte nicht geladen werden: Eine der Kartendateien von ArenaMapPort fehlt oder ist beschädigt ({file})."),
                L(@"Installiere ArenaMapPort neu und kopiere den kompletten Client-Ordner (Bundles und Keys inklusive), dann starte das Spiel neu. Details: BepInEx\LogOutput.log, Zeilen mit [Bundles]."),
            };
            t.Bodies[NoticeId.LoadNoCause] = new[]
            {
                L("{Map} wurde nicht gestartet, und ArenaMapPort hat dafür keine Ursache aufgezeichnet."),
                L(@"Sieh in BepInEx\LogOutput.log (Zeilen mit [Arena], [Bundles], [BundleKeys], [NavMesh]) und im Logs-Ordner des Spiels (die errors.log der neuesten Sitzung) nach und schicke sie an {support}."),
            };

            return t;
        }
    }
}
