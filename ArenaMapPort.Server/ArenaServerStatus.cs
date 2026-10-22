using System.Text.Json;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Server;

/// <summary>The gate part of the status document (<c>gate</c> in schema 1). Codes and counts only; see <see cref="ArenaStatusContract"/>.</summary>
public sealed record ArenaGateStatus(
    string State,
    string Cause,
    string Source,
    string? ManifestVersion,
    int MismatchCount,
    string OverrideFile);

/// <summary>One entry of <c>maps</c> in the status document.</summary>
public sealed record ArenaMapStatus(string Key, string State, string Cause);

/// <summary>The whole status document, schema 1.</summary>
public sealed record ArenaStatusSnapshot(int Schema, string? ServerVersion, ArenaGateStatus Gate, IReadOnlyList<ArenaMapStatus> Maps);

/// <summary>
/// What the server mod knows about why the Arena maps are, or are not, offered, kept where the status route can read it.
///
/// Vanilla SPT: a mod that does not register a location says nothing to the game client, and this mod's Arena gate
/// (<see cref="ArenaInstallationGate"/>, user decision 2026-10-19) makes "the server registered no map" a normal state of an
/// install without Arena. Until now the only trace was one line in the server console; <see cref="ArenaInstallationGate.Evaluate"/>
/// discarded the verdict and <see cref="ArenaMapRegistration.RegisterMapAsync"/> logged each map's outcome and forgot it.
///
/// What this does: a static, DI-free holder. The gate writes its verdict (<see cref="RecordGate"/>, <see cref="RecordGateError"/>),
/// the per-map registration writes each map's outcome (<see cref="RecordMap"/>), and <see cref="ArenaMapPortStatusRouter"/> answers
/// <see cref="ArenaStatusContract.StatusRoute"/> with <see cref="ToJson"/>. It is static so the router, which is its own DI class
/// and answers even when <c>OnLoadAsync</c> returned at the gate, reaches it without the mod instance, and so a write needs no
/// container in the tests.
///
/// Constraints. Every writer is a plain write under a lock: no logging (the server's Info, Warning and Error lines are pinned by
/// tests), no DI, no I/O, and none can throw. The document carries codes, counts and the Arena version only (no reason text, no
/// install path, no server path, no hash), because the route needs no session. The client reads it only when maps are missing
/// from its location list. Host evidence: docs/progress/map-notices.md.
/// </summary>
public static class ArenaServerStatus
{
    private const int MaxManifestVersionLength = 64;

    // Static initializers run in textual order: the not-run gate must exist before the field that starts as a copy of it.
    private static readonly ArenaGateStatus NotRunGate = new(
        ArenaStatusContract.GateNotRun,
        ArenaStatusContract.CauseCode(ArenaInstallCause.None),
        ArenaInstallation.SourceNone,
        null,
        0,
        ArenaStatusContract.OverrideAbsent);

    private static readonly object Lock = new();
    private static ArenaGateStatus _gate = NotRunGate;
    private static readonly Dictionary<string, ArenaMapStatus> Maps = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly Lazy<string?> ServerVersion = new(ReadServerVersion);

    /// <summary>
    /// Records the verdict of the gate. A verified installation clears the per-map records (the registration that follows writes
    /// them); a failed one marks every registry map as not registered because of the gate. A failed result without a cause
    /// (a test double, never a real check: <see cref="ArenaInstallation"/> names a cause at every site) is reported as
    /// <see cref="ArenaInstallCause.CheckFailed"/>, so "not verified" never reaches the wire without one. Never throws.
    /// </summary>
    /// <param name="result">The verification result.</param>
    /// <param name="overrideFile">One of <see cref="ArenaStatusContract.OverrideFileStates"/>; anything else counts as absent.</param>
    public static void RecordGate(ArenaInstallationResult result, string overrideFile)
    {
        try
        {
            if (result is null)
            {
                RecordGateError(overrideFile);
                return;
            }

            bool verified = result.Verified;
            var cause = verified
                ? ArenaInstallCause.None
                : result.Cause == ArenaInstallCause.None ? ArenaInstallCause.CheckFailed : result.Cause;
            var gate = new ArenaGateStatus(
                verified ? ArenaStatusContract.GateVerified : ArenaStatusContract.GateNotVerified,
                ArenaStatusContract.CauseCode(cause),
                SourceCode(result.Source),
                Truncate(result.ManifestVersion, MaxManifestVersionLength),
                Math.Max(0, result.MismatchCount),
                OverrideCode(overrideFile));
            Write(gate, verified);
        }
        catch (Exception)
        {
            // a writer never throws into the gate or the registration
        }
    }

    /// <summary>
    /// Records that the gate threw (it fails closed: the maps stay unregistered). The cause is
    /// <see cref="ArenaInstallCause.CheckFailed"/>. Never throws.
    /// </summary>
    public static void RecordGateError(string overrideFile = ArenaStatusContract.OverrideAbsent)
    {
        try
        {
            Write(new ArenaGateStatus(
                ArenaStatusContract.GateError,
                ArenaStatusContract.CauseCode(ArenaInstallCause.CheckFailed),
                ArenaInstallation.SourceNone,
                null,
                0,
                OverrideCode(overrideFile)), false);
        }
        catch (Exception)
        {
            // see RecordGate
        }
    }

    /// <summary>
    /// Records one map's registration outcome. <paramref name="state"/> is one of <see cref="ArenaStatusContract.MapStates"/> and
    /// <paramref name="cause"/> one of <see cref="ArenaStatusContract.MapCauses"/>; an unknown value is recorded as a failed
    /// registration rather than as a healthy map. A null or empty key is ignored. Never throws.
    /// </summary>
    public static void RecordMap(string key, string state, string cause)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
                return;
            bool known = ArenaStatusContract.IsOneOf(state, ArenaStatusContract.MapStates)
                         && ArenaStatusContract.IsOneOf(cause, ArenaStatusContract.MapCauses);
            var record = known
                ? new ArenaMapStatus(key, state, cause)
                : new ArenaMapStatus(key, ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseRegistration);
            lock (Lock)
                Maps[key] = record;
        }
        catch (Exception)
        {
            // see RecordGate
        }
    }

    /// <summary>
    /// The document as it stands: every map of <see cref="ArenaMapRegistry.All"/> in registry order (not registered, cause none,
    /// until a writer says otherwise). Before the gate has run the state is <c>not_run</c>.
    /// </summary>
    public static ArenaStatusSnapshot Snapshot()
    {
        ArenaGateStatus gate;
        var recorded = new Dictionary<string, ArenaMapStatus>(StringComparer.Ordinal);
        lock (Lock)
        {
            gate = _gate;
            foreach (var pair in Maps)
                recorded[pair.Key] = pair.Value;
        }

        var maps = new List<ArenaMapStatus>(ArenaMapRegistry.All.Count);
        foreach (var map in ArenaMapRegistry.All)
        {
            maps.Add(recorded.TryGetValue(map.Key, out var status)
                ? status
                : new ArenaMapStatus(map.Key, ArenaStatusContract.MapNotRegistered, ArenaStatusContract.MapCauseNone));
        }

        return new ArenaStatusSnapshot(ArenaStatusContract.StatusSchema, ServerVersion.Value, gate, maps);
    }

    /// <summary>
    /// <see cref="Snapshot"/> as the JSON the route sends: schema 1, camelCase, nulls written, one line. Never throws; if the
    /// serializer itself fails the answer is a minimal document that says <c>error</c>, so the client sees a server that answered
    /// but could not say more.
    /// </summary>
    public static string ToJson()
    {
        try
        {
            return JsonSerializer.Serialize(Snapshot(), JsonOptions);
        }
        catch (Exception)
        {
            return "{\"schema\":" + ArenaStatusContract.StatusSchema + ",\"serverVersion\":null,\"gate\":{\"state\":\""
                   + ArenaStatusContract.GateError + "\",\"cause\":\"" + ArenaStatusContract.CauseCode(ArenaInstallCause.CheckFailed)
                   + "\",\"source\":\"" + ArenaInstallation.SourceNone + "\",\"manifestVersion\":null,\"mismatchCount\":0,\"overrideFile\":\""
                   + ArenaStatusContract.OverrideAbsent + "\"},\"maps\":[]}";
        }
    }

    /// <summary>Back to "gate not run, nothing recorded". For the tests, which share this static state.</summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            _gate = NotRunGate;
            Maps.Clear();
        }
    }

    private static void Write(ArenaGateStatus gate, bool verified)
    {
        lock (Lock)
        {
            _gate = gate;
            Maps.Clear();
            if (verified)
                return;
            foreach (var map in ArenaMapRegistry.All)
                Maps[map.Key] = new ArenaMapStatus(map.Key, ArenaStatusContract.MapNotRegistered, ArenaStatusContract.MapCauseGate);
        }
    }

    private static string SourceCode(string source) =>
        source == ArenaInstallation.SourceOverride || source == ArenaInstallation.SourceRegistry
            ? source
            : ArenaInstallation.SourceNone;

    private static string OverrideCode(string? overrideFile) =>
        ArenaStatusContract.IsOneOf(overrideFile!, ArenaStatusContract.OverrideFileStates) ? overrideFile! : ArenaStatusContract.OverrideAbsent;

    private static string? Truncate(string? text, int max) =>
        text is null ? null : text.Length <= max ? text : text.Substring(0, max);

    private static string? ReadServerVersion()
    {
        try
        {
            return new ModMetadata().Version.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
