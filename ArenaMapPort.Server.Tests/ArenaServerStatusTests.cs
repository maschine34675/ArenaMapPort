using System.Reflection;
using System.Text.Json;
using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The static status holder and the router share process-wide state, so every test class that writes it (the gate tests, the mod
/// gate test, the registration outcome tests and the classes below) joins this one xunit collection and resets the holder.
/// </summary>
public static class StatusHolderCollection
{
    public const string Name = "ServerStatusHolder";
}

/// <summary>
/// ArenaServerStatus: the holder behind <c>GET /arenamapport/status</c> (map notices, 2026-10-22). Golden JSON for a verified
/// server, an unverified one, a failed map and an empty holder, the wire codes, the privacy rule (codes, counts and the Arena
/// version only) and the "cannot throw" rule.
/// </summary>
[Collection(StatusHolderCollection.Name)]
public sealed class ArenaServerStatusTests : IDisposable
{
    private static readonly string[] RegistryKeys = { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" };

    public ArenaServerStatusTests() => ArenaServerStatus.ResetForTests();

    public void Dispose() => ArenaServerStatus.ResetForTests();

    private static string ServerVersion => new ModMetadata().Version.ToString();

    private static ArenaInstallationResult Verified() =>
        new(true, @"D:\Secret Folder\Escape from Tarkov Arena", "registry", "0.6.1.0.48206", "0.6.0.0.47752", 15351, 76_118_000_000L, null, 0, null);

    private static ArenaInstallationResult NotVerified(
        ArenaInstallCause cause, string source = "none", string? manifestVersion = null, int mismatchCount = 0) =>
        new(false, @"D:\Secret Folder\Escape from Tarkov Arena", source, manifestVersion, null, 0, 0,
            @"SECRET reason sentence naming D:\Secret Folder\Escape from Tarkov Arena", mismatchCount,
            new[] { @"secret\level0: missing" }, 0, cause);

    private static string MapsJson(params (string Key, string State, string Cause)[] maps) =>
        "[" + string.Join(",", maps.Select(map => $"{{\"key\":\"{map.Key}\",\"state\":\"{map.State}\",\"cause\":\"{map.Cause}\"}}")) + "]";

    private static string Document(string gate, string maps) =>
        "{\"schema\":1,\"serverVersion\":\"" + ServerVersion + "\",\"gate\":" + gate + ",\"maps\":" + maps + "}";

    private static string AllMaps(string state, string cause) =>
        MapsJson(RegistryKeys.Select(key => (key, state, cause)).ToArray());

    // ---- golden JSON ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheRegistryTheGoldenDocumentsListIsTheCurrentOne()
    {
        // The golden documents below spell the four maps out. A new or removed registry map must change them on purpose.
        Assert.Equal(RegistryKeys, ArenaMapRegistry.All.Select(map => map.Key).ToArray());
    }

    [Fact]
    public void AnEmptyHolderSaysTheGateHasNotRun()
    {
        string expected = Document(
            "{\"state\":\"not_run\",\"cause\":\"none\",\"source\":\"none\",\"manifestVersion\":null,\"mismatchCount\":0,\"overrideFile\":\"absent\"}",
            AllMaps("not_registered", "none"));

        Assert.Equal(expected, ArenaServerStatus.ToJson());
    }

    [Fact]
    public void AVerifiedServerWithEveryMapRegisteredIsGolden()
    {
        ArenaServerStatus.RecordGate(Verified(), ArenaStatusContract.OverrideAbsent);
        foreach (string key in RegistryKeys)
            ArenaServerStatus.RecordMap(key, ArenaStatusContract.MapRegistered, ArenaStatusContract.MapCauseNone);

        string expected = Document(
            "{\"state\":\"verified\",\"cause\":\"none\",\"source\":\"registry\",\"manifestVersion\":\"0.6.1.0.48206\",\"mismatchCount\":0,\"overrideFile\":\"absent\"}",
            AllMaps("registered", "none"));

        Assert.Equal(expected, ArenaServerStatus.ToJson());
    }

    [Fact]
    public void AnUnverifiedServerIsGoldenAndEveryMapIsNotRegisteredBecauseOfTheGate()
    {
        ArenaServerStatus.RecordGate(NotVerified(ArenaInstallCause.NotRegistered), ArenaStatusContract.OverrideAbsent);

        string expected = Document(
            "{\"state\":\"not_verified\",\"cause\":\"not_registered\",\"source\":\"none\",\"manifestVersion\":null,\"mismatchCount\":0,\"overrideFile\":\"absent\"}",
            AllMaps("not_registered", "gate"));

        Assert.Equal(expected, ArenaServerStatus.ToJson());
    }

    [Fact]
    public void AVerifiedServerWithOneFailedMapIsGolden()
    {
        ArenaServerStatus.RecordGate(Verified(), ArenaStatusContract.OverrideUsed);
        ArenaServerStatus.RecordMap("arena_equator", ArenaStatusContract.MapRegistered, ArenaStatusContract.MapCauseNone);
        ArenaServerStatus.RecordMap("arena_bay5", ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseBaseJsonParse);
        ArenaServerStatus.RecordMap("arena_airpit", ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseBaseJsonMissing);
        ArenaServerStatus.RecordMap("arena_bowl", ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseRegistration);

        string expected = Document(
            "{\"state\":\"verified\",\"cause\":\"none\",\"source\":\"registry\",\"manifestVersion\":\"0.6.1.0.48206\",\"mismatchCount\":0,\"overrideFile\":\"used\"}",
            MapsJson(
                ("arena_equator", "registered", "none"),
                ("arena_bay5", "failed", "base_json_parse"),
                ("arena_airpit", "failed", "base_json_missing"),
                ("arena_bowl", "failed", "registration")));

        Assert.Equal(expected, ArenaServerStatus.ToJson());
    }

    [Fact]
    public void AnOverrideThatFailedFilesMismatchCarriesTheCountAndTheVersion()
    {
        ArenaServerStatus.RecordGate(
            NotVerified(ArenaInstallCause.FilesMismatch, "override", "0.6.1.0.48206", mismatchCount: 17), ArenaStatusContract.OverrideUsed);

        string expected = Document(
            "{\"state\":\"not_verified\",\"cause\":\"files_mismatch\",\"source\":\"override\",\"manifestVersion\":\"0.6.1.0.48206\",\"mismatchCount\":17,\"overrideFile\":\"used\"}",
            AllMaps("not_registered", "gate"));

        Assert.Equal(expected, ArenaServerStatus.ToJson());
    }

    [Fact]
    public void AGateErrorIsGoldenAndKeepsTheOverrideFileState()
    {
        ArenaServerStatus.RecordGateError(ArenaStatusContract.OverrideIgnored);

        string expected = Document(
            "{\"state\":\"error\",\"cause\":\"check_failed\",\"source\":\"none\",\"manifestVersion\":null,\"mismatchCount\":0,\"overrideFile\":\"ignored\"}",
            AllMaps("not_registered", "gate"));

        Assert.Equal(expected, ArenaServerStatus.ToJson());
    }

    // ---- the wire codes follow the cause --------------------------------------------------------------------------

    [Fact]
    public void EveryCauseIsSentWithItsWireCode()
    {
        foreach (ArenaInstallCause cause in Enum.GetValues<ArenaInstallCause>().Where(c => c != ArenaInstallCause.None))
        {
            ArenaServerStatus.RecordGate(NotVerified(cause), ArenaStatusContract.OverrideAbsent);

            Assert.Equal(ArenaStatusContract.CauseCode(cause), ArenaServerStatus.Snapshot().Gate.Cause);
            Assert.Equal(ArenaStatusContract.GateNotVerified, ArenaServerStatus.Snapshot().Gate.State);
        }
    }

    [Fact]
    public void AFailedResultWithoutACauseIsReportedAsCheckFailedNeverAsNone()
    {
        ArenaServerStatus.RecordGate(NotVerified(ArenaInstallCause.None), ArenaStatusContract.OverrideAbsent);

        Assert.Equal("check_failed", ArenaServerStatus.Snapshot().Gate.Cause);
    }

    [Fact]
    public void ANullResultIsAGateErrorAndNeverThrows()
    {
        ArenaServerStatus.RecordGate(null!, ArenaStatusContract.OverrideUsed);

        var gate = ArenaServerStatus.Snapshot().Gate;
        Assert.Equal("error", gate.State);
        Assert.Equal("check_failed", gate.Cause);
        Assert.Equal("used", gate.OverrideFile);
    }

    [Fact]
    public void AVerifiedGateClearsTheMapRecordsOfAnEarlierRun()
    {
        ArenaServerStatus.RecordGate(NotVerified(ArenaInstallCause.NotRegistered), ArenaStatusContract.OverrideAbsent);
        Assert.All(ArenaServerStatus.Snapshot().Maps, map => Assert.Equal("gate", map.Cause));

        ArenaServerStatus.RecordGate(Verified(), ArenaStatusContract.OverrideAbsent);

        // Verified but nothing registered yet: not registered, with no cause (the registration writes the rest).
        Assert.All(ArenaServerStatus.Snapshot().Maps, map =>
        {
            Assert.Equal("not_registered", map.State);
            Assert.Equal("none", map.Cause);
        });
    }

    [Fact]
    public void AnUnknownOverrideFileStateOrSourceIsReportedAsTheNeutralValue()
    {
        var result = new ArenaInstallationResult(true, "X", "something-new", "1", null, 1, 1, null, 0, null);
        ArenaServerStatus.RecordGate(result, "weird");

        var gate = ArenaServerStatus.Snapshot().Gate;
        Assert.Equal("none", gate.Source);
        Assert.Equal("absent", gate.OverrideFile);
    }

    [Fact]
    public void ANegativeMismatchCountAndALongManifestVersionAreBounded()
    {
        var result = new ArenaInstallationResult(false, "X", "override", new string('9', 200), null, 0, 0, "r", -5, null, 0, ArenaInstallCause.FilesMismatch);
        ArenaServerStatus.RecordGate(result, ArenaStatusContract.OverrideUsed);

        var gate = ArenaServerStatus.Snapshot().Gate;
        Assert.Equal(0, gate.MismatchCount);
        Assert.Equal(64, gate.ManifestVersion!.Length);
    }

    // ---- RecordMap -------------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownMapStateOrCauseIsRecordedAsAFailureNeverAsAHealthyMap()
    {
        ArenaServerStatus.RecordGate(Verified(), ArenaStatusContract.OverrideAbsent);
        ArenaServerStatus.RecordMap("arena_equator", "fine", "none");
        ArenaServerStatus.RecordMap("arena_bay5", "registered", "mystery");

        var maps = ArenaServerStatus.Snapshot().Maps;
        Assert.Equal(new ArenaMapStatus("arena_equator", "failed", "registration"), maps.Single(map => map.Key == "arena_equator"));
        Assert.Equal(new ArenaMapStatus("arena_bay5", "failed", "registration"), maps.Single(map => map.Key == "arena_bay5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMapWithoutAKeyIsIgnored(string? key)
    {
        ArenaServerStatus.RecordMap(key!, ArenaStatusContract.MapRegistered, ArenaStatusContract.MapCauseNone);

        Assert.All(ArenaServerStatus.Snapshot().Maps, map => Assert.Equal("not_registered", map.State));
    }

    [Fact]
    public void AMapThatIsNotInTheRegistryDoesNotAppearInTheDocument()
    {
        ArenaServerStatus.RecordMap("arena_somewhere_else", ArenaStatusContract.MapRegistered, ArenaStatusContract.MapCauseNone);

        Assert.Equal(RegistryKeys, ArenaServerStatus.Snapshot().Maps.Select(map => map.Key).ToArray());
        Assert.DoesNotContain("arena_somewhere_else", ArenaServerStatus.ToJson());
    }

    [Fact]
    public void TheLastRecordOfAMapWins()
    {
        ArenaServerStatus.RecordMap("arena_bowl", ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseRegistration);
        ArenaServerStatus.RecordMap("arena_bowl", ArenaStatusContract.MapRegistered, ArenaStatusContract.MapCauseNone);

        Assert.Equal(new ArenaMapStatus("arena_bowl", "registered", "none"), ArenaServerStatus.Snapshot().Maps.Single(map => map.Key == "arena_bowl"));
    }

    // ---- the document's shape and privacy ---------------------------------------------------------------------------

    [Fact]
    public void TheDocumentIsOneLineOfCamelCaseJsonWithNullsWritten()
    {
        ArenaServerStatus.RecordGate(NotVerified(ArenaInstallCause.ManifestMissing, "registry"), ArenaStatusContract.OverrideAbsent);

        string json = ArenaServerStatus.ToJson();
        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain("  ", json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(new[] { "schema", "serverVersion", "gate", "maps" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(ServerVersion, root.GetProperty("serverVersion").GetString());
        JsonElement gate = root.GetProperty("gate");
        Assert.Equal(
            new[] { "state", "cause", "source", "manifestVersion", "mismatchCount", "overrideFile" },
            gate.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, gate.GetProperty("manifestVersion").ValueKind);
        Assert.Equal(JsonValueKind.Number, gate.GetProperty("mismatchCount").ValueKind);
        foreach (JsonElement map in root.GetProperty("maps").EnumerateArray())
            Assert.Equal(new[] { "key", "state", "cause" }, map.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void EveryValueOfEveryStateIsAWireCodeOfTheContract()
    {
        var scenarios = new Action[]
        {
            () => ArenaServerStatus.ResetForTests(),
            () => ArenaServerStatus.RecordGate(Verified(), ArenaStatusContract.OverrideUsed),
            () => ArenaServerStatus.RecordGate(NotVerified(ArenaInstallCause.FolderMissing, "override"), ArenaStatusContract.OverrideIgnored),
            () => ArenaServerStatus.RecordGateError(),
        };

        foreach (Action scenario in scenarios)
        {
            scenario();
            var snapshot = ArenaServerStatus.Snapshot();
            Assert.Equal(ArenaStatusContract.StatusSchema, snapshot.Schema);
            Assert.True(ArenaStatusContract.IsOneOf(snapshot.Gate.State, ArenaStatusContract.GateStates));
            Assert.True(ArenaStatusContract.TryParseCause(snapshot.Gate.Cause, out _));
            Assert.True(ArenaStatusContract.IsOneOf(snapshot.Gate.OverrideFile, ArenaStatusContract.OverrideFileStates));
            Assert.All(snapshot.Maps, map =>
            {
                Assert.True(ArenaStatusContract.IsOneOf(map.State, ArenaStatusContract.MapStates));
                Assert.True(ArenaStatusContract.IsOneOf(map.Cause, ArenaStatusContract.MapCauses));
            });
        }
    }

    [Fact]
    public void TheDocumentCarriesNoReasonTextNoPathAndNoFileName()
    {
        ArenaServerStatus.RecordGate(
            NotVerified(ArenaInstallCause.FilesMismatch, "override", "0.6.1.0.48206", mismatchCount: 3), ArenaStatusContract.OverrideUsed);
        ArenaServerStatus.RecordMap("arena_equator", ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseBaseJsonParse);

        string json = ArenaServerStatus.ToJson();

        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason sentence", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("level0", json);
        Assert.DoesNotContain("D:", json);
        Assert.DoesNotContain("\\\\", json);
        Assert.DoesNotContain("arena-install.json", json);
        Assert.DoesNotContain("config", json);
        Assert.DoesNotMatch("[0-9a-fA-F]{32,}", json);
    }

    [Fact]
    public void TheServerVersionIsTheModsMetadataVersion()
    {
        Assert.Equal(new ModMetadata().Version.ToString(), ArenaServerStatus.Snapshot().ServerVersion);
    }

    // ---- cannot throw -----------------------------------------------------------------------------------------------

    [Fact]
    public void ConcurrentWritersAndReadersNeverThrow()
    {
        var exceptions = new List<Exception>();
        Parallel.For(0, 400, i =>
        {
            try
            {
                switch (i % 5)
                {
                    case 0: ArenaServerStatus.RecordGate(i % 2 == 0 ? Verified() : NotVerified(ArenaInstallCause.CheckFailed), ArenaStatusContract.OverrideAbsent); break;
                    case 1: ArenaServerStatus.RecordMap(RegistryKeys[i % 4], ArenaStatusContract.MapRegistered, ArenaStatusContract.MapCauseNone); break;
                    case 2: ArenaServerStatus.RecordGateError(); break;
                    case 3: ArenaServerStatus.RecordMap(null!, null!, null!); break;
                    default: JsonDocument.Parse(ArenaServerStatus.ToJson()).Dispose(); break;
                }
            }
            catch (Exception e)
            {
                lock (exceptions)
                    exceptions.Add(e);
            }
        });

        Assert.Empty(exceptions);
    }
}

/// <summary>
/// ArenaMapPortStatusRouter: the one route of the mod's own. It answers with the holder's JSON, also when nothing was
/// recorded (the gate returned before registering anything is exactly the state worth reporting).
/// </summary>
[Collection(StatusHolderCollection.Name)]
public sealed class ArenaMapPortStatusRouterTests : IDisposable
{
    public ArenaMapPortStatusRouterTests() => ArenaServerStatus.ResetForTests();

    public void Dispose() => ArenaServerStatus.ResetForTests();

    private static async Task<string> Ask()
    {
        var router = new ArenaMapPortStatusRouter(null!);
        object answer = await router.HandleStaticAsync(ArenaStatusContract.StatusRoute, null, default, "");
        return Assert.IsType<string>(answer);
    }

    [Fact]
    public async Task TheRouteAnswersWithTheHoldersJson()
    {
        ArenaServerStatus.RecordGateError(ArenaStatusContract.OverrideIgnored);

        string answer = await Ask();

        Assert.Equal(ArenaServerStatus.ToJson(), answer);
        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("error", document.RootElement.GetProperty("gate").GetProperty("state").GetString());
    }

    [Fact]
    public async Task AnEmptyHolderAnswersNotRun()
    {
        string answer = await Ask();

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("not_run", document.RootElement.GetProperty("gate").GetProperty("state").GetString());
        Assert.Equal(ArenaMapRegistry.All.Count, document.RootElement.GetProperty("maps").GetArrayLength());
    }

    [Fact]
    public async Task TheRouteReadsTheCurrentStateEachTime()
    {
        string before = await Ask();
        ArenaServerStatus.RecordGate(
            new ArenaInstallationResult(true, "X", "registry", "1", null, 1, 1, null, 0, null), ArenaStatusContract.OverrideAbsent);
        string after = await Ask();

        Assert.Contains("\"state\":\"not_run\"", before);
        Assert.Contains("\"state\":\"verified\"", after);
    }

    [Fact]
    public void TheRouterIsRegisteredAfterTheCoreRoutersLikeTheArenaModeOne()
    {
        var injectable = typeof(ArenaMapPortStatusRouter).GetCustomAttribute<Injectable>();

        Assert.NotNull(injectable);
        Assert.Equal(OnLoadOrder.Routers + 1, injectable!.TypePriority);
        Assert.True(typeof(ArenaMapPortStatusRouter).IsSealed);
        Assert.True(typeof(StaticRouter).IsAssignableFrom(typeof(ArenaMapPortStatusRouter)));
    }

    [Fact]
    public void TheRouteIsOurOwnPrefixAndDoesNotCollideWithArenaMode()
    {
        Assert.StartsWith("/arenamapport/", ArenaStatusContract.StatusRoute);
        Assert.DoesNotContain("arenamode", ArenaStatusContract.StatusRoute);
    }
}

/// <summary>
/// The gate records its verdict in the holder while its log lines stay as they were (ArenaInstallationGateTests pins those).
/// </summary>
[Collection(StatusHolderCollection.Name)]
public sealed class ArenaInstallationGateStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ArenaGateStatus_" + Guid.NewGuid().ToString("N"));

    public ArenaInstallationGateStatusTests()
    {
        Directory.CreateDirectory(_root);
        ArenaServerStatus.ResetForTests();
    }

    public void Dispose()
    {
        ArenaServerStatus.ResetForTests();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string ModDir()
    {
        string folder = Path.Combine(_root, "mod");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void WriteOverride(string modDir, string content)
    {
        string file = ArenaInstallationGate.OverridePath(modDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    private static bool Run(string modDir, Func<string?, ArenaInstallationResult> detect)
    {
        return ArenaInstallationGate.Evaluate(modDir, _ => { }, _ => { }, _ => { }, detect);
    }

    private static ArenaInstallationResult Verified(string path = "X") =>
        new(true, path, "registry", "0.6.1.0.48206", null, 10, 10, null, 0, null);

    [Fact]
    public void AVerifiedInstallationIsRecordedAsVerifiedWithoutAnOverrideFile()
    {
        Assert.True(Run(ModDir(), _ => Verified()));

        var gate = ArenaServerStatus.Snapshot().Gate;
        Assert.Equal("verified", gate.State);
        Assert.Equal("none", gate.Cause);
        Assert.Equal("registry", gate.Source);
        Assert.Equal("0.6.1.0.48206", gate.ManifestVersion);
        Assert.Equal("absent", gate.OverrideFile);
    }

    [Fact]
    public void ARealOverridePathIsRecordedAsUsed()
    {
        string modDir = ModDir();
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        WriteOverride(modDir, JsonSerializer.Serialize(new { installPath = empty }));

        Assert.False(ArenaInstallationGate.Evaluate(modDir, _ => { }, _ => { }, _ => { }));

        var gate = ArenaServerStatus.Snapshot().Gate;
        Assert.Equal("not_verified", gate.State);
        Assert.Equal("not_arena_folder", gate.Cause);
        Assert.Equal("override", gate.Source);
        Assert.Equal("used", gate.OverrideFile);
        Assert.All(ArenaServerStatus.Snapshot().Maps, map => Assert.Equal(("not_registered", "gate"), (map.State, map.Cause)));
    }

    [Fact]
    public void AnUnusableOverrideFileIsRecordedAsIgnoredAndTheRegistryLookupSaysWhyNothingWasFound()
    {
        string modDir = ModDir();
        WriteOverride(modDir, """{"installPath": "D:\Games\Arena"}"""); // single backslashes: not valid JSON

        Assert.False(Run(modDir, path =>
        {
            Assert.Null(path); // ignored: the registry lookup runs without an override
            return new ArenaInstallationResult(false, null, "none", null, null, 0, 0, "No installation is registered.", 0, null, 0,
                ArenaInstallCause.NotRegistered);
        }));

        var gate = ArenaServerStatus.Snapshot().Gate;
        Assert.Equal("not_verified", gate.State);
        Assert.Equal("not_registered", gate.Cause);
        Assert.Equal("ignored", gate.OverrideFile);
    }

    [Theory]
    [InlineData("""{"installPath": ""}""")]
    [InlineData("""{"installPath": null}""")]
    public void AnExplicitNoOverrideIsRecordedAsAbsent(string json)
    {
        string modDir = ModDir();
        WriteOverride(modDir, json);

        Run(modDir, _ => Verified());

        Assert.Equal("absent", ArenaServerStatus.Snapshot().Gate.OverrideFile);
    }

    [Fact]
    public void AGateThatThrowsIsRecordedAsAnErrorAndKeepsTheOverrideFileState()
    {
        string modDir = ModDir();
        WriteOverride(modDir, "not json");

        Assert.False(Run(modDir, _ => throw new InvalidOperationException("registry went away")));

        var gate = ArenaServerStatus.Snapshot().Gate;
        Assert.Equal("error", gate.State);
        Assert.Equal("check_failed", gate.Cause);
        Assert.Equal("ignored", gate.OverrideFile);
        Assert.All(ArenaServerStatus.Snapshot().Maps, map => Assert.Equal(("not_registered", "gate"), (map.State, map.Cause)));
    }

    [Fact]
    public void ANullResultFromDetectIsRecordedAsAnError()
    {
        Assert.False(Run(ModDir(), _ => null!));

        Assert.Equal("error", ArenaServerStatus.Snapshot().Gate.State);
    }

    [Fact]
    public void TheHoldersWriteNeverChangesTheLogLinesOfAnUnverifiedGate()
    {
        var info = new List<string>();
        var warning = new List<string>();
        var error = new List<string>();

        bool register = ArenaInstallationGate.Evaluate(
            ModDir(), info.Add, warning.Add, error.Add,
            _ => new ArenaInstallationResult(false, null, "none", null, null, 0, 0, "Nothing here.", 0, null, 0, ArenaInstallCause.NotRegistered));

        Assert.False(register);
        Assert.Single(info);
        Assert.StartsWith("[ArenaMapPort] Escape from Tarkov: Arena not found: Nothing here. The ", info[0]);
        Assert.Empty(warning);
        Assert.Empty(error);
    }
}
