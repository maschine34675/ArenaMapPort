using System.Reflection;
using System.Text;
using System.Text.Json;
using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using SPTarkov.Common.Models.Logging;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The server side of the Arena installation gate: the optional config\arena-install.json, the one log line per
/// outcome and the fail-closed rule. ArenaInstallationTests covers the detection and verification itself.
/// </summary>
public sealed class ArenaInstallationGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ArenaInstallationGate_" + Guid.NewGuid().ToString("N"));
    private int _next;

    public ArenaInstallationGateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string NewFolder(string name)
    {
        string folder = Path.Combine(_root, name + "_" + _next++);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string WriteOverride(string modDir, string content, Encoding? encoding = null)
    {
        string file = ArenaInstallationGate.OverridePath(modDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content, encoding ?? new UTF8Encoding(false));
        return file;
    }

    private static ArenaInstallationResult Verified(string path = @"D:\Battlestate Games\Escape from Tarkov Arena") =>
        new(true, path, "registry", "0.6.1.0.48206", "0.6.0.0.47752", 15351, 76_118_000_000L, null, 0, null);

    private static ArenaInstallationResult NotVerified(string reason) =>
        new(false, null, "none", null, null, 0, 0, reason, 0, null);

    private sealed class Log
    {
        public readonly List<string> Info = new();
        public readonly List<string> Warning = new();
        public readonly List<string> Error = new();
        public readonly List<string> Order = new();

        public bool Run(string modDir, Func<string?, ArenaInstallationResult>? detect = null) =>
            ArenaInstallationGate.Evaluate(
                modDir,
                m => { Info.Add(m); Order.Add("info"); },
                m => { Warning.Add(m); Order.Add("warning"); },
                m => { Error.Add(m); Order.Add("error"); },
                detect);
    }

    // ---- ReadOverride -------------------------------------------------------------------------------------------

    [Fact]
    public void OverridePath_IsConfigArenaInstallJson_BesideTheServerDll()
    {
        Assert.Equal("arena-install.json", ArenaInstallationGate.OverrideFileName);
        Assert.Equal("config", ArenaInstallationGate.OverrideFolderName);
        Assert.Equal("installPath", ArenaInstallationGate.OverrideProperty);
        Assert.Equal(Path.Combine("mods", "ArenaMapPort", "config", "arena-install.json"),
            ArenaInstallationGate.OverridePath(Path.Combine("mods", "ArenaMapPort")));
    }

    [Fact]
    public void ReadOverride_AbsentFile_GivesNothingAndNoWarning()
    {
        string modDir = NewFolder("mod");
        Assert.Equal(default, ArenaInstallationGate.ReadOverride(modDir));
    }

    [Fact]
    public void ReadOverride_ConfigFolderWithoutTheFile_GivesNothingAndNoWarning()
    {
        string modDir = NewFolder("mod");
        Directory.CreateDirectory(Path.Combine(modDir, "config"));
        File.WriteAllText(Path.Combine(modDir, "config", "something-else.json"), """{"installPath": "D:\\Arena"}""");
        Assert.Equal(default, ArenaInstallationGate.ReadOverride(modDir));
    }

    [Fact]
    public void ReadOverride_MissingModFolder_GivesNothingAndNoWarning()
    {
        Assert.Equal(default, ArenaInstallationGate.ReadOverride(Path.Combine(_root, "does-not-exist")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadOverride_NoModFolderGiven_GivesNothing(string? modDir)
    {
        Assert.Equal(default, ArenaInstallationGate.ReadOverride(modDir!));
    }

    [Theory]
    [InlineData("""{"installPath": "D:\\Games\\Escape from Tarkov Arena"}""", @"D:\Games\Escape from Tarkov Arena")]
    [InlineData("""{"installPath": "D:/Games/Escape from Tarkov Arena"}""", "D:/Games/Escape from Tarkov Arena")]
    [InlineData("""{"installPath": "  D:\\Games\\Arena  "}""", @"D:\Games\Arena")]
    [InlineData("""{"installPath": "\\\\nas\\games\\Arena"}""", @"\\nas\games\Arena")]
    [InlineData("""{"INSTALLPATH": "D:\\Arena"}""", @"D:\Arena")]
    [InlineData("""{"installpath": "D:\\Arena"}""", @"D:\Arena")]
    [InlineData("""{"other": 1, "installPath": "D:\\Arena", "more": [1, 2]}""", @"D:\Arena")]
    [InlineData("{ // where Arena lives\n  \"installPath\": \"D:\\\\Arena\", /* note */\n}", @"D:\Arena")]
    [InlineData("""{"installPath": "D:\u005cArena"}""", @"D:\Arena")]
    public void ReadOverride_ValidFile_GivesThePathWithoutAWarning(string json, string expectedPath)
    {
        string modDir = NewFolder("mod");
        WriteOverride(modDir, json);
        var read = ArenaInstallationGate.ReadOverride(modDir);
        Assert.Equal(expectedPath, read.InstallPath);
        Assert.Null(read.Warning);
    }

    [Fact]
    public void ReadOverride_FileWithAByteOrderMarkAndNonAsciiPath_IsRead()
    {
        string modDir = NewFolder("mod");
        WriteOverride(modDir, "{\"installPath\": \"D:\\\\Spiele\\\\Escape from Tarkov Arena \u00dc\"}", new UTF8Encoding(true));
        var read = ArenaInstallationGate.ReadOverride(modDir);
        Assert.Equal("D:\\Spiele\\Escape from Tarkov Arena \u00dc", read.InstallPath);
        Assert.Null(read.Warning);
    }

    [Theory]
    [InlineData("""{"installPath": ""}""")]
    [InlineData("""{"installPath": "   "}""")]
    [InlineData("""{"installPath": null}""")]
    public void ReadOverride_EmptyOrNullInstallPath_IsAnExplicitNoOverrideWithoutAWarning(string json)
    {
        string modDir = NewFolder("mod");
        WriteOverride(modDir, json);
        Assert.Equal(default, ArenaInstallationGate.ReadOverride(modDir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("""{"installPath": "D:\Games\Arena"}""")]
    [InlineData("""{"installPath": "D:\\Games\\Arena"} trailing""")]
    [InlineData("[]")]
    [InlineData("""["D:\\Arena"]""")]
    [InlineData("\"D:\\\\Arena\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"other": "D:\\Arena"}""")]
    [InlineData("""{"installPath": 5}""")]
    [InlineData("""{"installPath": true}""")]
    [InlineData("""{"installPath": ["D:\\Arena"]}""")]
    [InlineData("""{"installPath": {"path": "D:\\Arena"}}""")]
    public void ReadOverride_UnusableFile_WarnsAndGivesNoPath(string json)
    {
        string modDir = NewFolder("mod");
        string file = WriteOverride(modDir, json);

        var read = ArenaInstallationGate.ReadOverride(modDir);

        Assert.Null(read.InstallPath);
        Assert.NotNull(read.Warning);
        Assert.StartsWith("[ArenaMapPort] Ignoring " + file + ": ", read.Warning);
        Assert.Contains("installPath", read.Warning);
        Assert.EndsWith("The Arena installation is looked up through the BSG launcher's registry entry instead.", read.Warning);
        Assert.DoesNotContain("\n", read.Warning);
    }

    [Fact]
    public void ReadOverride_AnUndoubledBackslashPath_TellsTheUserAboutTheBackslashRule()
    {
        string modDir = NewFolder("mod");
        WriteOverride(modDir, """{"installPath": "D:\Games\Arena"}""");

        var read = ArenaInstallationGate.ReadOverride(modDir);

        Assert.Null(read.InstallPath);
        Assert.Contains("not valid JSON", read.Warning);
        Assert.Contains("Backslashes in the path must be doubled or written as forward slashes", read.Warning);
        Assert.DoesNotContain(".).", read.Warning);
    }

    [Fact]
    public void ReadOverride_AnOversizedFile_WarnsAndGivesNoPath()
    {
        string modDir = NewFolder("mod");
        string padding = new string('x', 70 * 1024);
        string file = WriteOverride(modDir, "{\"installPath\": \"D:\\\\Arena\", \"note\": \"" + padding + "\"}");

        var read = ArenaInstallationGate.ReadOverride(modDir);

        Assert.Null(read.InstallPath);
        Assert.StartsWith("[ArenaMapPort] Ignoring " + file + ": it is larger than 64 KB", read.Warning);
    }

    [Fact]
    public void ReadOverride_AFileThatCannotBeOpened_WarnsAndGivesNoPath()
    {
        string modDir = NewFolder("mod");
        string file = WriteOverride(modDir, """{"installPath": "D:\\Arena"}""");

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var read = ArenaInstallationGate.ReadOverride(modDir);

            Assert.Null(read.InstallPath);
            Assert.StartsWith("[ArenaMapPort] Ignoring " + file + ": it could not be read (IOException: ", read.Warning);
        }

        // Released again: the same file reads fine.
        Assert.Equal(@"D:\Arena", ArenaInstallationGate.ReadOverride(modDir).InstallPath);
    }

    // ---- Decide -------------------------------------------------------------------------------------------------

    [Fact]
    public void Decide_VerifiedInstallation_RegistersAndLogsTheDescribeLine()
    {
        var result = Verified();
        var decision = ArenaInstallationGate.Decide(result, Path.Combine(_root, "mod"));

        Assert.True(decision.Register);
        Assert.Equal(
            @"[ArenaMapPort] Escape from Tarkov: Arena 0.6.1.0.48206 at D:\Battlestate Games\Escape from Tarkov Arena (registry; 15,351 files, 76.1 GB verified)",
            decision.Message);
        Assert.Equal("[ArenaMapPort] " + result.Describe(), decision.Message);
    }

    [Fact]
    public void Decide_NotVerified_DoesNotRegisterAndSaysWhatToDo()
    {
        string modDir = Path.Combine(_root, "mod");
        var decision = ArenaInstallationGate.Decide(NotVerified("No installation is registered."), modDir);

        Assert.False(decision.Register);
        Assert.Equal(
            "[ArenaMapPort] Escape from Tarkov: Arena not found: No installation is registered. "
            + "The " + ArenaMapRegistry.All.Count + " Arena maps stay unregistered. "
            + "Install Escape from Tarkov: Arena through the BSG launcher, or set \"installPath\" in "
            + Path.Combine(modDir, "config", "arena-install.json")
            + " for an installation without the launcher's registry entry.",
            decision.Message);
    }

    [Theory]
    [InlineData("Nothing here.")]
    [InlineData("Nothing here")]
    [InlineData("Nothing here. ")]
    [InlineData("Nothing here..")]
    public void Decide_NotVerified_JoinsTheReasonAndTheNextSentenceWithOneFullStop(string reason)
    {
        var decision = ArenaInstallationGate.Decide(NotVerified(reason), Path.Combine(_root, "mod"));

        Assert.DoesNotContain("..", decision.Message);
        Assert.Contains("Escape from Tarkov: Arena not found: Nothing here. The " + ArenaMapRegistry.All.Count + " Arena maps stay unregistered. ",
            decision.Message);
    }

    [Fact]
    public void Decide_NullResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ArenaInstallationGate.Decide(null!, Path.Combine(_root, "mod")));
    }

    [Fact]
    public void Decide_AnEmptyFolderThroughDetect_IsNotVerified()
    {
        string empty = NewFolder("empty");

        var result = ArenaInstallation.Detect(empty);
        var decision = ArenaInstallationGate.Decide(result, Path.Combine(_root, "mod"));

        Assert.False(result.Verified);
        Assert.Equal(ArenaInstallation.SourceOverride, result.Source);
        Assert.False(decision.Register);
        Assert.StartsWith("[ArenaMapPort] Escape from Tarkov: Arena not found: ", decision.Message);
        Assert.Contains(empty + " does not contain EscapeFromTarkovArena.exe", decision.Message);
        Assert.Contains("stay unregistered", decision.Message);
    }

    [Fact]
    public void Decide_AFolderThatDoesNotExistThroughDetect_IsNotVerified()
    {
        string missing = Path.Combine(_root, "no-such-folder");

        var result = ArenaInstallation.Detect(missing);
        var decision = ArenaInstallationGate.Decide(result, Path.Combine(_root, "mod"));

        Assert.False(decision.Register);
        Assert.Contains("The installation folder does not exist: " + missing, decision.Message);
    }

    // ---- Evaluate: the whole gate --------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_OverrideFileAtAnEmptyFolder_IsNotVerifiedThroughTheRealDetect()
    {
        string modDir = NewFolder("mod");
        string empty = NewFolder("empty");
        WriteOverride(modDir, JsonSerializer.Serialize(new { installPath = empty }));
        var log = new Log();

        bool register = log.Run(modDir);

        Assert.False(register);
        string line = Assert.Single(log.Info);
        Assert.Contains(empty + " does not contain EscapeFromTarkovArena.exe", line);
        Assert.Contains("stay unregistered", line);
        Assert.Empty(log.Warning);
        Assert.Empty(log.Error);
    }

    [Fact]
    public void Evaluate_AWrongOverride_DoesNotFallBackToTheRegistry()
    {
        // The user asked for that folder, so a registry entry elsewhere must not rescue it.
        string modDir = NewFolder("mod");
        string empty = NewFolder("empty");
        WriteOverride(modDir, JsonSerializer.Serialize(new { installPath = empty }));
        var log = new Log();
        string?[] detectedWith = new string?[1];

        bool register = log.Run(modDir, path =>
        {
            detectedWith[0] = path;
            return ArenaInstallation.Detect(path);
        });

        Assert.False(register);
        Assert.Equal(empty, detectedWith[0]);
    }

    [Fact]
    public void Evaluate_VerifiedResult_RegistersAndHandsTheOverridePathToDetect()
    {
        string modDir = NewFolder("mod");
        WriteOverride(modDir, """{"installPath": "E:\\Games\\Arena"}""");
        var log = new Log();
        string? detectedWith = null;
        int calls = 0;

        bool register = log.Run(modDir, path =>
        {
            detectedWith = path;
            calls++;
            return Verified(path!);
        });

        Assert.True(register);
        Assert.Equal(1, calls);
        Assert.Equal(@"E:\Games\Arena", detectedWith);
        Assert.Equal(new[] { "[ArenaMapPort] " + Verified(@"E:\Games\Arena").Describe() }, log.Info);
        Assert.Empty(log.Warning);
        Assert.Empty(log.Error);
    }

    [Fact]
    public void Evaluate_WithoutAnOverrideFile_AsksDetectForTheRegistryEntry()
    {
        var log = new Log();
        bool called = false;
        string? detectedWith = "unset";

        bool register = log.Run(NewFolder("mod"), path =>
        {
            called = true;
            detectedWith = path;
            return Verified();
        });

        Assert.True(register);
        Assert.True(called);
        Assert.Null(detectedWith);
        Assert.Empty(log.Warning);
    }

    [Fact]
    public void Evaluate_MalformedOverride_WarnsFirstThenFallsBackToTheRegistryLookup()
    {
        string modDir = NewFolder("mod");
        WriteOverride(modDir, """{"installPath": "D:\Games\Arena"}""");
        var log = new Log();
        string? detectedWith = "unset";

        bool register = log.Run(modDir, path =>
        {
            detectedWith = path;
            return NotVerified("No installation is registered.");
        });

        Assert.False(register);
        Assert.Null(detectedWith);
        Assert.Equal(new[] { "warning", "info" }, log.Order);
        Assert.Contains("not valid JSON", log.Warning[0]);
        Assert.Contains("stay unregistered", log.Info[0]);
        Assert.Empty(log.Error);
    }

    [Fact]
    public void Evaluate_AnUnexpectedException_FailsClosedAndNamesItInAnErrorLine()
    {
        var log = new Log();

        bool register = log.Run(NewFolder("mod"), _ => throw new InvalidOperationException("registry went away"));

        Assert.False(register);
        Assert.Empty(log.Info);
        Assert.Empty(log.Warning);
        Assert.Equal(
            "[ArenaMapPort] Looking for the Escape from Tarkov: Arena installation failed unexpectedly "
            + "(InvalidOperationException: registry went away). The " + ArenaMapRegistry.All.Count + " Arena maps stay unregistered.",
            Assert.Single(log.Error));
    }

    [Fact]
    public void Evaluate_ANullResultFromDetect_FailsClosed()
    {
        var log = new Log();

        bool register = log.Run(NewFolder("mod"), _ => null!);

        Assert.False(register);
        Assert.Empty(log.Info);
        Assert.Contains("ArgumentNullException", Assert.Single(log.Error));
    }

    [RealInstallFact]
    public void Evaluate_TheRealInstallationThroughTheOverrideFile_IsVerified()
    {
        string modDir = NewFolder("mod");
        WriteOverride(modDir, JsonSerializer.Serialize(new { installPath = RealArena.Folder }));
        var log = new Log();

        bool register = log.Run(modDir);

        Assert.True(register, string.Join(" | ", log.Info.Concat(log.Warning).Concat(log.Error)));
        string line = Assert.Single(log.Info);
        Assert.StartsWith("[ArenaMapPort] Escape from Tarkov: Arena ", line);
        Assert.Contains(" at " + RealArena.Folder + " (override; ", line);
        Assert.Empty(log.Warning);
        Assert.Empty(log.Error);
    }
}

/// <summary>Records every call so a test can see exactly what the server mod logged. Public: DispatchProxy needs it.</summary>
public class RecordingLogger : DispatchProxy
{
    public readonly List<(string Method, string? Message)> Calls = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        Calls.Add((targetMethod.Name, args is { Length: > 0 } ? args[0] as string : null));
        Type returnType = targetMethod.ReturnType;
        return returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
    }
}

/// <summary>
/// Runs ArenaMapPortMod.OnLoadAsync itself against an Arena folder that does not verify. Every SPT dependency is null:
/// the mod must return from the gate before it touches any of them, so a NullReferenceException here means a
/// registration step ran ahead of the gate. The override file goes next to the test copy of the server DLL, where
/// the mod looks for it; this class is the only writer there and its tests run one after the other.
/// </summary>
public sealed class ArenaMapPortModGateTests : IDisposable
{
    private static readonly string ModDir = Path.GetDirectoryName(typeof(ArenaMapPortMod).Assembly.Location)!;
    private static readonly string OverrideFile = ArenaInstallationGate.OverridePath(ModDir);

    private readonly string _empty = Path.Combine(Path.GetTempPath(), "ArenaMapPortModGate_" + Guid.NewGuid().ToString("N"));
    private readonly byte[]? _previousFile;
    private readonly bool _createdConfigFolder;

    public ArenaMapPortModGateTests()
    {
        Directory.CreateDirectory(_empty);
        string configFolder = Path.GetDirectoryName(OverrideFile)!;
        _createdConfigFolder = !Directory.Exists(configFolder);
        Directory.CreateDirectory(configFolder);
        _previousFile = File.Exists(OverrideFile) ? File.ReadAllBytes(OverrideFile) : null;
    }

    public void Dispose()
    {
        if (_previousFile is not null)
            File.WriteAllBytes(OverrideFile, _previousFile);
        else if (File.Exists(OverrideFile))
            File.Delete(OverrideFile);
        if (_createdConfigFolder && Directory.GetFileSystemEntries(Path.GetDirectoryName(OverrideFile)!).Length == 0)
            Directory.Delete(Path.GetDirectoryName(OverrideFile)!);
        Directory.Delete(_empty, true);
    }

    private static ArenaMapPortMod NewMod(out RecordingLogger recorder)
    {
        var logger = DispatchProxy.Create<ISptLogger<ArenaMapPortMod>, RecordingLogger>();
        recorder = (RecordingLogger)(object)logger;
        return new ArenaMapPortMod(null!, null!, null!, null!, null!, null!, null!, null!, logger);
    }

    [Fact]
    public async Task OnLoad_WithoutAVerifiedInstallation_RegistersNothingAndTouchesNoDependency()
    {
        File.WriteAllText(OverrideFile, JsonSerializer.Serialize(new { installPath = _empty }));
        var mod = NewMod(out var recorder);

        await mod.OnLoadAsync(CancellationToken.None);

        var call = Assert.Single(recorder.Calls);
        Assert.Equal("Info", call.Method);
        Assert.StartsWith("[ArenaMapPort] Escape from Tarkov: Arena not found: ", call.Message);
        Assert.Contains(_empty + " does not contain EscapeFromTarkovArena.exe", call.Message);
        Assert.Contains("The " + ArenaMapRegistry.All.Count + " Arena maps stay unregistered.", call.Message);
        Assert.Contains(OverrideFile, call.Message);
    }
}
