using System.Reflection;
using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Eft.Common;
using LocationsBase = SPTarkov.Server.Core.Models.Eft.Common.Tables.LocationsBase;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// ArenaMapRegistration.RegisterMapAsync: the per-map body that used to sit inside ArenaMapPortMod.OnLoadAsync's loop, now with
/// injectable inputs, and the outcome it records in the status holder (map notices, 2026-10-22): <c>base_json_parse</c>,
/// <c>base_json_missing</c>, <c>registration</c> and <c>registered</c>. The log lines and their order are the loop body's own;
/// ArenaMapPortModGateTests runs the whole OnLoadAsync up to the gate.
/// </summary>
[Collection(StatusHolderCollection.Name)]
public sealed class MapRegistrationOutcomeTests : IDisposable
{
    private readonly string _modDir = Path.Combine(Path.GetTempPath(), "MapRegistrationOutcome_" + Guid.NewGuid().ToString("N"));
    private static readonly ArenaMapDescriptor Equator = ArenaMapRegistry.Equator;
    private static readonly ArenaMapDescriptor Bay5 = ArenaMapRegistry.Bay5;

    public MapRegistrationOutcomeTests()
    {
        Directory.CreateDirectory(_modDir);
        ArenaServerStatus.ResetForTests();
        // The gate passed: a verified installation, nothing registered yet.
        ArenaServerStatus.RecordGate(new ArenaInstallationResult(true, "X", "registry", "1", null, 1, 1, null, 0, null), ArenaStatusContract.OverrideAbsent);
    }

    [Fact]
    public void TheModBuildsTheRegistrationInputsInsideATryAndRecordsEveryMapAsFailedWhenThatThrows()
    {
        // OnLoadAsync needs SPT's DI container and cannot run here: the guard around the inputs is pinned in the source. The inputs read SPT's config objects,
        // which used to be read inside each map's try block, so one that throws must still end as the old per-map "registration failed" Error and record.
        string? directory = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ArenaMapPort.Server", "ArenaMapRegistration.cs")))
                directory = Path.Combine(dir.FullName, "ArenaMapPort.Server");
        Assert.NotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory!, "ArenaMapRegistration.cs"));

        Assert.Matches(new System.Text.RegularExpressions.Regex(@"MapRegistrationInputs\? inputs = null;\s*try\s*\{\s*inputs = new MapRegistrationInputs"), source);
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"catch \(Exception e\) when \(e is not OperationCanceledException\)\s*\{\s*foreach \(var map in ArenaMapRegistry\.All\)\s*\{\s*" +
            @"logger\.Error\(\$""\[ArenaMapPort\] \{map\.Key\} registration failed: \{e\.Message\}""\);\s*" +
            @"ArenaServerStatus\.RecordMap\(map\.Key, ArenaStatusContract\.MapFailed, ArenaStatusContract\.MapCauseRegistration\);"), source);
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"if \(inputs is not null\)\s*\{\s*foreach \(var map in ArenaMapRegistry\.All\)\s*\{\s*await ArenaMapRegistration\.RegisterMapAsync\(map, inputs"), source);
    }

    public void Dispose()
    {
        ArenaServerStatus.ResetForTests();
        try { Directory.Delete(_modDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static LocationTable MakeLocationTable()
    {
        static Location Stub(string id) => new() { Base = new LocationBase { Id = id } };

        return new LocationTable
        {
            Bigmap = Stub("bigmap"),
            Factory4Day = Stub("factory4_day"),
            Factory4Night = Stub("factory4_night"),
            Interchange = Stub("interchange"),
            Laboratory = Stub("laboratory"),
            Lighthouse = Stub("lighthouse"),
            RezervBase = Stub("rezervbase"),
            Shoreline = Stub("shoreline"),
            TarkovStreets = Stub("tarkovstreets"),
            Labyrinth = Stub("labyrinth"),
            Woods = Stub("woods"),
            Sandbox = Stub("sandbox"),
            SandboxHigh = Stub("sandbox_high"),
            Base = new LocationsBase(),
        };
    }

    private static ISptLogger<ArenaMapPortMod> NewLogger(out RecordingLogger recorder)
    {
        var logger = DispatchProxy.Create<ISptLogger<ArenaMapPortMod>, RecordingLogger>();
        recorder = (RecordingLogger)(object)logger;
        return logger;
    }

    private MapRegistrationInputs Inputs(
        LocationTable table,
        Func<string, CancellationToken, Task<LocationBase?>> load,
        List<(string Key, string File)>? routes = null) =>
        new()
        {
            ModDir = _modDir,
            LoadBase = load,
            LocationTable = table,
            StaticLootMultiplier = new Dictionary<string, double>(),
            LooseLootMultiplier = new Dictionary<string, double>(),
            ScavRaidTimeMaps = new Dictionary<string, ScavRaidTimeLocationSettings?>(),
            CloneScavRaidTime = settings => settings with { },
            MaxBotCap = new Dictionary<string, int>(),
            AssaultBrainType = new Dictionary<string, Dictionary<string, int>>(),
            PlayerScavBrainType = new Dictionary<string, Dictionary<string, int>>(),
            PmcType = new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(),
            AddImageRoute = (key, file) => routes?.Add((key, file)),
        };

    private static Task<LocationBase?> Loaded(string path, CancellationToken token) =>
        Task.FromResult<LocationBase?>(new LocationBase { Id = "placeholder" });

    private static ArenaMapStatus Status(ArenaMapDescriptor map) => ArenaServerStatus.Snapshot().Maps.Single(status => status.Key == map.Key);

    [Fact]
    public async Task AMapThatLoadsAndRegistersIsRecordedAsRegisteredAndLogsTheAvailableLine()
    {
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);

        await ArenaMapRegistration.RegisterMapAsync(Equator, Inputs(table, Loaded), logger, CancellationToken.None);

        Assert.Equal(new ArenaMapStatus(Equator.Key, "registered", "none"), Status(Equator));
        Assert.True(table.GetDictionary().ContainsKey(Equator.Key));
        var last = recorder.Calls[^1];
        Assert.Equal("Info", last.Method);
        Assert.Equal($"[ArenaMapPort] {Equator.LocaleName} is available in location selection.", last.Message);
        Assert.DoesNotContain(recorder.Calls, call => call.Method == "Error");
    }

    [Fact]
    public async Task ABase_JsonThatFailsToParseIsRecordedAsBaseJsonParseWithTheOriginalErrorLine()
    {
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);
        string? readFrom = null;

        await ArenaMapRegistration.RegisterMapAsync(Equator, Inputs(table, (path, _) =>
        {
            readFrom = path;
            throw new InvalidOperationException("bad json");
        }), logger, CancellationToken.None);

        string basePath = Path.Combine(_modDir, "db", Equator.DbBaseJsonPath);
        Assert.Equal(basePath, readFrom);
        Assert.Equal(new ArenaMapStatus(Equator.Key, "failed", "base_json_parse"), Status(Equator));
        Assert.False(table.GetDictionary().ContainsKey(Equator.Key));
        var call = Assert.Single(recorder.Calls);
        Assert.Equal("Error", call.Method);
        Assert.Equal($"[ArenaMapPort] {Equator.Key}: {basePath} failed to parse — location NOT enabled: bad json", call.Message);
    }

    [Fact]
    public async Task ABase_JsonThatLoadsAsNullIsRecordedAsBaseJsonMissingWithTheOriginalErrorLine()
    {
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);

        await ArenaMapRegistration.RegisterMapAsync(Equator, Inputs(table, (_, _) => Task.FromResult<LocationBase?>(null)), logger, CancellationToken.None);

        string basePath = Path.Combine(_modDir, "db", Equator.DbBaseJsonPath);
        Assert.Equal(new ArenaMapStatus(Equator.Key, "failed", "base_json_missing"), Status(Equator));
        var call = Assert.Single(recorder.Calls);
        Assert.Equal("Error", call.Method);
        Assert.Equal($"[ArenaMapPort] {Equator.Key}: could not load {basePath} — location NOT enabled", call.Message);
    }

    [Fact]
    public async Task AKeyThatIsAlreadyRegisteredIsRecordedAsRegistrationAndLogsTheOriginalErrorLine()
    {
        var table = MakeLocationTable();
        table.GetDictionary().Add(Equator.Key, new Location { Base = new LocationBase { Id = Equator.Key } });
        var logger = NewLogger(out var recorder);

        await ArenaMapRegistration.RegisterMapAsync(Equator, Inputs(table, Loaded), logger, CancellationToken.None);

        Assert.Equal(new ArenaMapStatus(Equator.Key, "failed", "registration"), Status(Equator));
        var call = Assert.Single(recorder.Calls, c => c.Method == "Error");
        Assert.Equal($"[ArenaMapPort] {Equator.Key} registration failed: ArenaMapPort location key '{Equator.Key}' is already registered.", call.Message);
        Assert.DoesNotContain(recorder.Calls, c => c.Method == "Info");
    }

    [Fact]
    public async Task AConfigStepThatThrowsIsRecordedAsRegistration()
    {
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);
        var inputs = Inputs(table, Loaded) with { CloneScavRaidTime = _ => throw new InvalidOperationException("clone failed") };
        inputs.ScavRaidTimeMaps[ArenaMapRegistration.TemplateKey] = new ScavRaidTimeLocationSettings();

        await ArenaMapRegistration.RegisterMapAsync(Equator, inputs, logger, CancellationToken.None);

        Assert.Equal(new ArenaMapStatus(Equator.Key, "failed", "registration"), Status(Equator));
        Assert.Contains(recorder.Calls, c => c.Method == "Error" && c.Message == $"[ArenaMapPort] {Equator.Key} registration failed: clone failed");
        Assert.False(table.GetDictionary().ContainsKey(Equator.Key));
    }

    [Fact]
    public async Task OneMapsFailureDoesNotBlockTheNextMap()
    {
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);
        var inputs = Inputs(table, (path, token) =>
            path.Contains(Equator.DbBaseJsonPath, StringComparison.Ordinal)
                ? throw new InvalidOperationException("equator is broken")
                : Loaded(path, token));

        foreach (var map in new[] { Equator, Bay5 })
            await ArenaMapRegistration.RegisterMapAsync(map, inputs, logger, CancellationToken.None);

        Assert.Equal(new ArenaMapStatus(Equator.Key, "failed", "base_json_parse"), Status(Equator));
        Assert.Equal(new ArenaMapStatus(Bay5.Key, "registered", "none"), Status(Bay5));
        Assert.False(table.GetDictionary().ContainsKey(Equator.Key));
        Assert.True(table.GetDictionary().ContainsKey(Bay5.Key));
    }

    [Fact]
    public async Task ACancellationPropagatesAndRecordsNothing()
    {
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArenaMapRegistration.RegisterMapAsync(
            Equator, Inputs(table, (_, _) => throw new OperationCanceledException()), logger, CancellationToken.None));

        // Not a failed map: the server is stopping. The holder keeps its default and nothing is logged.
        Assert.Equal(new ArenaMapStatus(Equator.Key, "not_registered", "none"), Status(Equator));
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task AnExitMismatchWarnsBeforeTheAvailableLineAndStillRegisters()
    {
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);

        // The stub base.json has no exits, so the descriptor's exits are "descriptor only": the original Warning, then Info.
        await ArenaMapRegistration.RegisterMapAsync(Equator, Inputs(table, Loaded), logger, CancellationToken.None);

        int warning = recorder.Calls.FindIndex(c => c.Method == "Warning" && c.Message!.StartsWith($"[ArenaMapPort] {Equator.Key}: exit names differ between base.json and the client table — "));
        int info = recorder.Calls.FindIndex(c => c.Method == "Info");
        Assert.True(warning >= 0, "the exit-name warning is missing");
        Assert.True(info > warning, "the Info line must come after the warning");
        Assert.Equal("registered", Status(Equator).State);
    }

    [Fact]
    public async Task ABannerFileOnDiskIsWiredAndTheRouteIsAddedThroughTheInput()
    {
        string banners = Path.Combine(_modDir, "banners");
        Directory.CreateDirectory(banners);
        File.WriteAllBytes(Path.Combine(banners, "arena_equator.jpg"), new byte[] { 1, 2, 3 });
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);
        var routes = new List<(string Key, string File)>();

        await ArenaMapRegistration.RegisterMapAsync(Equator, Inputs(table, Loaded, routes), logger, CancellationToken.None);

        var route = Assert.Single(routes);
        Assert.Equal(Path.Combine(banners, "arena_equator.jpg"), route.File);
        Assert.Contains(recorder.Calls, c => c.Method == "Debug" && c.Message!.StartsWith($"[ArenaMapPort] {Equator.Key}: location-select banner wired ("));
        Assert.Equal("registered", Status(Equator).State);
    }

    [Fact]
    public async Task ABannerRouteThatThrowsNeverBlocksTheRegistration()
    {
        string banners = Path.Combine(_modDir, "banners");
        Directory.CreateDirectory(banners);
        File.WriteAllBytes(Path.Combine(banners, "arena_equator.jpg"), new byte[] { 1 });
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);
        var inputs = Inputs(table, Loaded) with { AddImageRoute = (_, _) => throw new InvalidOperationException("no image router") };

        await ArenaMapRegistration.RegisterMapAsync(Equator, inputs, logger, CancellationToken.None);

        Assert.Equal("registered", Status(Equator).State);
        Assert.Contains(recorder.Calls, c => c.Method == "Warning" && c.Message == $"[ArenaMapPort] {Equator.Key}: banner wiring failed (location-select falls back to the default art): no image router");
    }

    [Fact]
    public async Task EveryRegistryMapRegistersInTurnLikeTheLoopOfOnLoadAsync()
    {
        var table = MakeLocationTable();
        var logger = NewLogger(out var recorder);
        var inputs = Inputs(table, Loaded);

        foreach (var map in ArenaMapRegistry.All)
            await ArenaMapRegistration.RegisterMapAsync(map, inputs, logger, CancellationToken.None);

        Assert.All(ArenaServerStatus.Snapshot().Maps, status => Assert.Equal(("registered", "none"), (status.State, status.Cause)));
        Assert.Equal(
            ArenaMapRegistry.All.Select(map => $"[ArenaMapPort] {map.LocaleName} is available in location selection.").ToArray(),
            recorder.Calls.Where(c => c.Method == "Info").Select(c => c.Message).ToArray());
        Assert.All(ArenaMapRegistry.All, map => Assert.True(table.GetDictionary().ContainsKey(map.Key), map.Key));
    }
}
