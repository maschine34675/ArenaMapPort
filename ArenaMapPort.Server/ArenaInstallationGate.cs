using System.Text.Json;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Server;

/// <summary>
/// The server side of the Arena installation gate (user decision 2026-10-19). Escape from Tarkov: Arena is a separate
/// purchase from Escape from Tarkov and this mod ships maps derived from Arena's files, so the server registers the
/// Arena locations only when <see cref="ArenaInstallation"/> finds and verifies an installation (the BSG launcher's
/// registry entry, or an explicit path in <c>config\arena-install.json</c> next to the server DLL). This is a good-faith
/// installation gate at SPT's own level (SPT requires an installed Escape from Tarkov), not copy protection: there is
/// no switch that turns it off, and the explicit path runs the same verification as the registry entry.
///
/// Kept DI-free and static, like <see cref="ArenaMapRegistration"/>, so the tests can run the whole decision without
/// a live server. When the gate says no, <see cref="ArenaMapPortMod"/> returns before it touches anything: no
/// location, no locale transformer, no weather hook, no APBS patch. The one exception is the read-only status route
/// (<see cref="ArenaMapPortStatusRouter"/>), which reports the verdict recorded here to the game client as codes.
/// </summary>
public static class ArenaInstallationGate
{
    /// <summary>Folder next to the server DLL that holds the optional override file.</summary>
    public const string OverrideFolderName = "config";

    /// <summary>Optional file <c>{"installPath": "D:\\Games\\Escape from Tarkov Arena"}</c>.</summary>
    public const string OverrideFileName = "arena-install.json";

    /// <summary>JSON property that holds the installation folder (matched case-insensitively).</summary>
    public const string OverrideProperty = "installPath";

    /// <summary>Every server log line of this mod starts with it; the shared server logger has no source name.</summary>
    public const string LogPrefix = "[ArenaMapPort] ";

    // A hand-written file with one path in it; anything bigger is not what we are looking for.
    private const long MaxOverrideFileBytes = 64 * 1024;

    /// <summary>What <see cref="ReadOverride"/> found: a path to verify, a warning to log, both null for no file or no path.</summary>
    public readonly record struct OverrideRead(string? InstallPath, string? Warning);

    /// <summary>The gate's verdict: whether to register the maps and the one Info line that says why.</summary>
    public readonly record struct Decision(bool Register, string Message);

    /// <summary>Full path of the optional override file for a mod folder.</summary>
    public static string OverridePath(string modDir) => Path.Combine(modDir, OverrideFolderName, OverrideFileName);

    /// <summary>
    /// Reads the optional <c>config\arena-install.json</c> of <paramref name="modDir"/>. Never throws.
    /// Absent file, empty or null <c>installPath</c> (an explicit "no override"): <c>(null, null)</c>. An unreadable file,
    /// malformed JSON, a root that is not an object, or a missing or non-string <c>installPath</c>: a warning line to log
    /// and no path, so the registry entry is used instead. Comments and trailing commas are tolerated because the
    /// file is edited by hand. The path is returned as written; <see cref="ArenaInstallation.Detect"/> trims it and
    /// verifies it in full.
    /// </summary>
    public static OverrideRead ReadOverride(string modDir)
    {
        if (string.IsNullOrWhiteSpace(modDir))
            return default;

        string file = OverridePath(modDir);
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                return default;
            if (info.Length > MaxOverrideFileBytes)
                return Ignored(file, "it is larger than 64 KB, which is not an install path file");

            string text = File.ReadAllText(file);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Ignored(file, "its top level is not a JSON object like " + Example);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, OverrideProperty, StringComparison.OrdinalIgnoreCase))
                    continue;
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        string? value = property.Value.GetString();
                        return string.IsNullOrWhiteSpace(value) ? default : new OverrideRead(value.Trim(), null);
                    case JsonValueKind.Null:
                        return default;
                    default:
                        return Ignored(file, "\"" + OverrideProperty + "\" is not a string; expected " + Example);
                }
            }

            return Ignored(file, "it has no \"" + OverrideProperty + "\" value; expected " + Example);
        }
        catch (JsonException e)
        {
            return Ignored(file, "it is not valid JSON (" + e.Message.TrimEnd('.', ' ') + "). Backslashes in the path must be doubled "
                                 + "or written as forward slashes; expected " + Example);
        }
        catch (Exception e)
        {
            return Ignored(file, "it could not be read (" + e.GetType().Name + ": " + e.Message + ")");
        }
    }

    /// <summary>
    /// Turns the verification result into the one Info line for the log and the go/no-go for registration.
    /// <paramref name="modDir"/> is only used to tell the player which file to create. The not-found line carries the advice
    /// once: the shared reason states only what was found (2026-10-23, the user saw the launcher advice twice in one line).
    /// </summary>
    public static Decision Decide(ArenaInstallationResult result, string modDir)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Verified)
            return new Decision(true, LogPrefix + result.Describe());

        // Describe() ends in the reason, which is a sentence of its own; one full stop between the sentences.
        string reason = result.Describe().TrimEnd('.', ' ');
        return new Decision(false,
            LogPrefix + reason + ". The " + ArenaMapRegistry.All.Count + " Arena maps stay unregistered. "
            + "Install Escape from Tarkov: Arena through the BSG launcher, or set \"" + OverrideProperty + "\" in "
            + OverridePath(modDir) + " for an installation without the launcher's registry entry.");
    }

    /// <summary>
    /// Runs the whole gate: reads the override, detects and verifies the installation, logs the verdict and returns
    /// whether the maps may be registered. Fails closed: an unexpected exception is logged through <paramref name="error"/>
    /// and counts as not verified. <paramref name="detect"/> is the test seam; production passes none and gets
    /// <see cref="ArenaInstallation.Detect(string)"/>. The verdict (or the failure) is also recorded in
    /// <see cref="ArenaServerStatus"/>, a plain write that logs nothing, so the status route can tell the game client why no map
    /// is offered; the log lines are the same as before. The not-found verdict goes through <paramref name="notFound"/> when
    /// given (the mod logs it in red at Info level, user request 2026-10-23: the console line that explains missing maps must
    /// stand out), else through <paramref name="info"/> like the verified one.
    /// </summary>
    public static bool Evaluate(
        string modDir,
        Action<string> info,
        Action<string> warning,
        Action<string> error,
        Func<string?, ArenaInstallationResult>? detect = null,
        Action<string>? notFound = null)
    {
        var overrideFile = ArenaStatusContract.OverrideAbsent;
        try
        {
            var read = ReadOverride(modDir);
            overrideFile = read.Warning is not null
                ? ArenaStatusContract.OverrideIgnored
                : read.InstallPath is not null ? ArenaStatusContract.OverrideUsed : ArenaStatusContract.OverrideAbsent;
            if (read.Warning is not null)
                warning(read.Warning);

            var result = (detect ?? ArenaInstallation.Detect)(read.InstallPath);
            var decision = Decide(result, modDir);
            ArenaServerStatus.RecordGate(result, overrideFile);
            if (decision.Register || notFound is null)
                info(decision.Message);
            else
                notFound(decision.Message);
            return decision.Register;
        }
        catch (Exception e)
        {
            ArenaServerStatus.RecordGateError(overrideFile);
            error(LogPrefix + "Looking for the Escape from Tarkov: Arena installation failed unexpectedly ("
                  + e.GetType().Name + ": " + e.Message + "). The " + ArenaMapRegistry.All.Count
                  + " Arena maps stay unregistered.");
            return false;
        }
    }

    private const string Example = "{\"" + OverrideProperty + "\": \"D:\\\\Games\\\\Escape from Tarkov Arena\"}";

    private static OverrideRead Ignored(string file, string why) =>
        new(null, LogPrefix + "Ignoring " + file + ": " + why + ". The Arena installation is looked up through the "
                  + "BSG launcher's registry entry instead.");
}
