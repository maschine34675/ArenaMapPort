#nullable disable
using System.Diagnostics;
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Xunit;
using ClientGate = ArenaMapPort.Client.ArenaInstallationGate;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The client side of the Arena installation gate (release 0.2.3): the public config key, the one check on a worker
/// thread with bounded waits, the exact log lines, and the places in the plugin that must call it. The verification itself
/// is covered by ArenaInstallationTests; here a seam stands in for it so every outcome can be produced on any machine.
/// This file stands on its own: the developer's real installation and registry entry are named below, not borrowed from
/// ArenaInstallationTests.cs (the skip attributes at the end of this file are its own).
/// </summary>
[Collection("MapPort client lifecycle")]
public sealed class ClientArenaGateTests : IDisposable
{
    const string ExpectedDescription =
        "Path of the Escape from Tarkov: Arena installation when the BSG launcher's registry entry is missing. " +
        "Empty = automatic (registry). The path is verified against the launcher's file manifest; the maps load only " +
        "with a verified installation. Takes effect after a restart.";

    const string ExpectedHint =
        "[Arena] the Arena maps need an installed Escape from Tarkov: Arena; the server does not register " +
        "them without one. Set General/ArenaInstallPath for an installation without the launcher's registry entry.";

    internal const string RealFolder = @"D:\Battlestate Games\Escape from Tarkov Arena";

    internal const string RealUninstallKey =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkovArena_live";

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    static readonly string[] Names = { "Arena: Equator", "Arena: Bay 5", "Arena: Air Pit" };
    readonly TimeSpan _startupWait = ClientGate.StartupWait;
    readonly TimeSpan _bundleWait = ClientGate.BundleWait;

    public ClientArenaGateTests() => ClientGate.ResetForTests();

    public void Dispose()
    {
        ClientGate.StartupWait = _startupWait;
        ClientGate.BundleWait = _bundleWait;
        ClientGate.ResetForTests();
    }

    static ArenaInstallationResult Found(string version = "0.6.1.0.48206") =>
        new(true, @"D:\Synthetic Arena", "registry", version, "0.6.0.0.47752", 15351, 76_100_000_000L, null, 0, null);

    static ArenaInstallationResult Missing(string reason = "No Escape from Tarkov: Arena installation is registered.") =>
        new(false, null, "none", null, null, 0, 0, reason, 0, null);

    static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(relative);
    }

    static string Text(string relative) => File.ReadAllText(RepoFile(relative));

    // ---- the config key ---------------------------------------------------------------------------------------

    [Fact]
    public void PathKeyIsGeneralArenaInstallPathWithAnEmptyDefaultAndTheAgreedText()
    {
        var cfg = new ConfigFile();
        var entry = ClientGate.BindConfig(cfg);
        Assert.Equal("General", entry.Definition.Section);
        Assert.Equal("ArenaInstallPath", entry.Definition.Key);
        Assert.Equal("", entry.Value);
        Assert.Equal("", entry.DefaultValue);
        Assert.Equal(ExpectedDescription, entry.Description.Description);
        Assert.Same(entry, cfg.Get<string>("General", "ArenaInstallPath"));
    }

    [Fact]
    public void CompactConfigurationKeepsTheKeyInThePublicFile()
    {
        // CompactConfiguration cannot be compiled into this project (it needs the real ConfigFile); a lost key would be
        // archived as "tuning" and removed from the public file in player mode, so the typed path would vanish at restart.
        var source = Text("ArenaMapPort.Client/CompactConfiguration.cs");
        Assert.Matches(new Regex(@"IsPublic\(ConfigDefinition key\) =>[^;]*key\.Section == ""General"" && key\.Key == ""ArenaInstallPath""[^;]*;"),
            source);
    }

    // ---- the check ----------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeAnyCheckArenaIsNotVerifiedAndNothingBlocks()
    {
        var clock = Stopwatch.StartNew();
        Assert.False(ClientGate.Verified);
        Assert.Null(ClientGate.Result);
        Assert.Equal("the Arena installation check did not run", ClientGate.Reason);
        Assert.False(ClientGate.WaitForResult(Patience));
        Assert.False(ClientGate.EnsureVerified());
        Assert.Equal("Arena: not found", ClientGate.StateSummary());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AVerifiedInstallationOpensTheGateAndNamesItsVersion()
    {
        string seen = null;
        var found = Found();
        ClientGate.Start(@"D:\Wherever", path => { seen = path; return found; });
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal(@"D:\Wherever", seen);
        Assert.True(ClientGate.Verified);
        Assert.Same(found, ClientGate.Result);
        Assert.Null(ClientGate.Reason);
        Assert.True(ClientGate.EnsureVerified());
        Assert.Equal("Arena: found 0.6.1.0.48206", ClientGate.StateSummary());
        Assert.Equal("[Arena] " + found.Describe(), ClientGate.OutcomeLine(found));
        Assert.StartsWith(@"[Arena] Escape from Tarkov: Arena 0.6.1.0.48206 at D:\Synthetic Arena (registry; 15,351 files, ",
            ClientGate.OutcomeLine(found));
    }

    [Fact]
    public void ANullOverrideReachesTheDetectorAsTheEmptyString()
    {
        string seen = "unset";
        ClientGate.Start(null, path => { seen = path; return Found(); });
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal("", seen);
    }

    [Fact]
    public void AnInstallationThatIsNotVerifiedKeepsTheGateClosedAndSaysWhy()
    {
        var missing = Missing("The installation folder does not exist: D:\\Nope.");
        ClientGate.Start("D:\\Nope", _ => missing);
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.False(ClientGate.Verified);
        Assert.False(ClientGate.EnsureVerified());
        Assert.Same(missing, ClientGate.Result);
        Assert.Equal("The installation folder does not exist: D:\\Nope.", ClientGate.Reason);
        Assert.Equal("Arena: not found", ClientGate.StateSummary());
        Assert.Equal("[Arena] Escape from Tarkov: Arena not found: The installation folder does not exist: D:\\Nope.",
            ClientGate.OutcomeLine(missing));
        Assert.Equal(ExpectedHint, ClientGate.HintLine());
        Assert.Equal("[Bundles] 'arena_equator_scenes.bundle' not served: Escape from Tarkov: Arena not found " +
                     "(The installation folder does not exist: D:\\Nope.)",
            ClientGate.NotServedMessage("arena_equator_scenes.bundle"));
    }

    [Fact]
    public void ADetectorThatThrowsIsNotVerifiedAndIsLoggedOnceAsAnError()
    {
        ClientGate.Start("", _ => throw new InvalidOperationException("boom"));
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.False(ClientGate.Verified);
        Assert.Null(ClientGate.Result);
        Assert.Equal("the Arena installation check failed (InvalidOperationException: boom)", ClientGate.Reason);
        Assert.Null(ClientGate.OutcomeLine(null));
        Assert.Equal("Arena: not found", ClientGate.StateSummary());

        int before = Plugin.Log.Errors.Count;
        ClientGate.LogOutcome();
        ClientGate.LogOutcome();
        Assert.False(ClientGate.EnsureVerified());
        var errors = Plugin.Log.Errors.Skip(before).ToArray();
        var error = Assert.Single(errors);
        Assert.Equal("[Arena] checking the Arena installation failed unexpectedly (InvalidOperationException: boom); " +
                     "the Arena maps are not served this session.", error);
    }

    [Fact]
    public void ADetectorThatReturnsNothingIsNotVerified()
    {
        ClientGate.Start("", _ => null);
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.False(ClientGate.Verified);
        Assert.Equal("the Arena installation check failed (the check returned no result)", ClientGate.Reason);
    }

    [Fact]
    public void ASlowCheckFailsClosedAfterTheBoundedWaitOnlyOnceAndIsHonouredWhenItFinishesLater()
    {
        ClientGate.BundleWait = TimeSpan.FromMilliseconds(400);
        using var release = new ManualResetEventSlim(false);
        ClientGate.Start("", _ => { release.Wait(Patience); return Found(); });

        Assert.False(ClientGate.WaitForResult(TimeSpan.FromMilliseconds(50)));
        Assert.Equal("the Arena installation check is still running", ClientGate.Reason);
        Assert.Equal("Arena: still being checked", ClientGate.StateSummary());

        var clock = Stopwatch.StartNew();
        Assert.False(ClientGate.EnsureVerified());
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(300), "the first bundle request waits for the check");
        Assert.Equal("the Arena installation check did not finish in time, slow disk or unreachable path", ClientGate.Reason);
        Assert.Contains("not served: Escape from Tarkov: Arena not found (the Arena installation check did not finish in time",
            ClientGate.NotServedMessage("arena_bay5_preset.bundle"));

        clock.Restart();
        Assert.False(ClientGate.EnsureVerified());
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(200), "after one timeout the next request does not block again");

        release.Set();
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.True(ClientGate.EnsureVerified());
        Assert.Null(ClientGate.Reason);
    }

    [Fact]
    public void AStaleWorkerNeverOverwritesALaterCheck()
    {
        using var release = new ManualResetEventSlim(false);
        ClientGate.Start("", _ => { release.Wait(Patience); return Found(); });
        ClientGate.Start("", _ => Missing("second check"));
        Assert.True(ClientGate.WaitForResult(Patience));
        release.Set();
        Thread.Sleep(250); // generous: a wrongly applied stale result would land within milliseconds
        Assert.False(ClientGate.Verified);
        Assert.Equal("second check", ClientGate.Reason);
    }

    [Fact]
    public void InitializeReadsTheConfiguredPathAndVerifiesItWithTheRealCheck()
    {
        string missing = Path.Combine(Path.GetTempPath(), "arena-gate-missing-" + Guid.NewGuid().ToString("N"));
        var cfg = new ConfigFile();
        ClientGate.BindConfig(cfg).Value = missing;
        ClientGate.Initialize(cfg);

        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.False(ClientGate.Verified);
        var result = ClientGate.Result;
        Assert.NotNull(result);
        Assert.Equal(ArenaInstallation.SourceOverride, result.Source); // an explicit path never falls back to the registry
        Assert.Equal(Path.GetFullPath(missing), result.InstallPath);
        Assert.Contains("does not exist", ClientGate.Reason);
    }

    [InstalledArenaFact]
    public void InitializeWithTheDevelopersRealInstallationOpensTheGate()
    {
        var cfg = new ConfigFile();
        ClientGate.BindConfig(cfg).Value = RealFolder;
        ClientGate.Initialize(cfg);
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.True(ClientGate.Verified, ClientGate.Reason);
        Assert.Equal(ArenaInstallation.SourceOverride, ClientGate.Result.Source);
        Assert.StartsWith("Arena: found ", ClientGate.StateSummary());
    }

    [RegisteredArenaFact]
    public void AnEmptyPathFindsTheLauncherRegistration()
    {
        var cfg = new ConfigFile();
        Assert.Equal("", ClientGate.BindConfig(cfg).Value);
        ClientGate.Initialize(cfg);
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.True(ClientGate.Verified, ClientGate.Reason);
        Assert.Equal(ArenaInstallation.SourceRegistry, ClientGate.Result.Source);
    }

    // ---- a check that outlives the startup wait ---------------------------------------------------------------------

    [Fact]
    public void ACheckThatFinishesInTimeNeedsNoLaterPoll()
    {
        ClientGate.StartupWait = TimeSpan.FromSeconds(5);
        ClientGate.Initialize(new ConfigFile(), _ => Found());
        Assert.True(ClientGate.Verified);
        Assert.False(ClientGate.OutcomePending, "logged during Initialize, nothing left to poll");
        ClientGate.PollOutcome(); // inert
        Assert.False(ClientGate.OutcomePending);
    }

    [Fact]
    public void ACheckThatOutlivesTheStartupWaitIsLoggedOnceByThePollWithoutAnyBundleRequest()
    {
        ClientGate.StartupWait = TimeSpan.FromMilliseconds(50);
        using var release = new ManualResetEventSlim(false);
        int before = Plugin.Log.Errors.Count;

        // A detector that throws after the wait is the observable outcome here: the test log captures Error lines.
        ClientGate.Initialize(new ConfigFile(), _ => { release.Wait(Patience); throw new InvalidOperationException("late"); });
        Assert.True(ClientGate.OutcomePending, "the startup wait ran out: the poll is armed");
        Assert.Equal("Arena: still being checked", ClientGate.StateSummary());

        ClientGate.PollOutcome();
        Assert.True(ClientGate.OutcomePending, "still running: nothing logged, still armed");
        Assert.Equal(before, Plugin.Log.Errors.Count);

        release.Set();
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal(before, Plugin.Log.Errors.Count); // the worker thread itself never logs
        ClientGate.PollOutcome();
        Assert.False(ClientGate.OutcomePending, "the poll disarms itself after the one log");
        var error = Assert.Single(Plugin.Log.Errors.Skip(before));
        Assert.Equal("[Arena] checking the Arena installation failed unexpectedly (InvalidOperationException: late); " +
                     "the Arena maps are not served this session.", error);

        ClientGate.PollOutcome();
        ClientGate.LogOutcome();
        Assert.False(ClientGate.EnsureVerified());
        Assert.Single(Plugin.Log.Errors.Skip(before)); // once, whoever asks afterwards
    }

    [Fact]
    public void ABundleRequestThatLogsFirstLeavesNothingForThePoll()
    {
        ClientGate.StartupWait = TimeSpan.FromMilliseconds(50);
        ClientGate.BundleWait = TimeSpan.FromSeconds(10);
        using var release = new ManualResetEventSlim(false);
        int before = Plugin.Log.Errors.Count;
        ClientGate.Initialize(new ConfigFile(), _ => { release.Wait(Patience); throw new InvalidOperationException("late"); });
        Assert.True(ClientGate.OutcomePending);

        release.Set();
        Assert.False(ClientGate.EnsureVerified()); // the first bundle request waits for the check and logs its outcome
        Assert.Single(Plugin.Log.Errors.Skip(before));
        ClientGate.PollOutcome();
        Assert.False(ClientGate.OutcomePending);
        Assert.Single(Plugin.Log.Errors.Skip(before));
    }

    [Fact]
    public void StartingTheCheckAgainDisarmsAStalePoll()
    {
        ClientGate.StartupWait = TimeSpan.FromMilliseconds(50);
        using var release = new ManualResetEventSlim(false);
        ClientGate.Initialize(new ConfigFile(), _ => { release.Wait(Patience); return Found(); });
        Assert.True(ClientGate.OutcomePending);
        ClientGate.Start("", _ => Found());
        Assert.False(ClientGate.OutcomePending);
        release.Set();
    }

    // ---- the startup banner -----------------------------------------------------------------------------------------

    [Fact]
    public void TheBannerListsTheMapsOnlyWhenArenaIsVerified()
    {
        Assert.Equal("Maps: none available. Arena: not found.", ClientGate.MapsSummary(Names)); // never started

        ClientGate.Start("", _ => Found());
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal("Maps: Arena: Equator, Arena: Bay 5, Arena: Air Pit. Arena: found 0.6.1.0.48206.", ClientGate.MapsSummary(Names));

        ClientGate.Start("", _ => Missing());
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal("Maps: none available. Arena: not found.", ClientGate.MapsSummary(Names));

        ClientGate.Start("", _ => throw new InvalidOperationException("boom"));
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal("Maps: none available. Arena: not found.", ClientGate.MapsSummary(Names));

        using var release = new ManualResetEventSlim(false);
        ClientGate.Start("", _ => { release.Wait(Patience); return Found(); });
        Assert.Equal("Maps: available once Arena is verified (Arena: Equator, Arena: Bay 5, Arena: Air Pit). Arena: still being checked.",
            ClientGate.MapsSummary(Names));
        release.Set();
        Assert.True(ClientGate.WaitForResult(Patience));
    }

    // ---- where the plugin calls the gate ------------------------------------------------------------------------

    [Fact]
    public void BundleHostRefusesOurBundlesWithoutArenaBeforeTouchingAnyFile()
    {
        var source = Text("ArenaMapPort.Client/BundleHost.cs");
        int serve = source.IndexOf("internal static void TryServe", StringComparison.Ordinal);
        Assert.True(serve > 0);
        int ours = source.IndexOf("OurBundles().TryGetValue(bundleName", serve, StringComparison.Ordinal);
        int gate = source.IndexOf("ArenaInstallationGate.EnsureVerified()", serve, StringComparison.Ordinal);
        int message = source.IndexOf("Plugin.Log.LogError(ArenaInstallationGate.NotServedMessage(bundleName))", serve, StringComparison.Ordinal);
        int exists = source.IndexOf("File.Exists(filePath)", serve, StringComparison.Ordinal);
        int load = source.IndexOf("AssetBundle.LoadFromFile(filePath)", serve, StringComparison.Ordinal);
        Assert.True(ours > serve && gate > ours, "the gate applies only to our own bundles");
        Assert.True(message > gate && exists > message, "the refusal is logged and returns before the File.Exists check");
        Assert.True(load > exists);
        // The refusal returns, it does not fall through to the load.
        Assert.Matches(new Regex(@"NotServedMessage\(bundleName\)\);\s*return;"), source);
    }

    [Fact]
    public void PluginAwakeStartsTheGateRightAfterTheCompactConfigurationAndBeforeAnyRepairIsInstalled()
    {
        var source = Text("ArenaMapPort.Client/Plugin.cs");
        int compact = source.IndexOf("Settings = CompactConfiguration.Fallback(Config);", StringComparison.Ordinal);
        int gate = source.IndexOf("ArenaInstallationGate.Initialize(Config);", StringComparison.Ordinal);
        int banner = source.IndexOf("            LogStartupBanner();", StringComparison.Ordinal);
        int patches = source.IndexOf("HarmonyPatches.InstallAll(_harmony);", StringComparison.Ordinal);
        int complete = source.IndexOf("CompactConfiguration.Complete(Config);", StringComparison.Ordinal);
        Assert.True(compact > 0 && gate > compact, "bound on Config right after CompactConfiguration.Initialize");
        Assert.True(banner > gate, "the startup banner reports the gate's state");
        Assert.True(patches > banner);
        Assert.True(complete > patches, "the public file is saved after the key is bound");
    }

    [Fact]
    public void PluginPollsTheGateEveryFrameAndTheBannerUsesTheGatedMapsSummary()
    {
        var source = Text("ArenaMapPort.Client/Plugin.cs");
        Assert.Matches(new Regex(@"void LateUpdate\(\)\s*\{[^}]*ArenaInstallationGate\.PollOutcome\(\);[^}]*\}"), source);
        Assert.Contains("ArenaInstallationGate.MapsSummary(ArenaMapRegistry.All.Select(map => map.LocaleName))", source);
        // The old banner listed the maps unconditionally and appended the state.
        Assert.DoesNotMatch(new Regex(@"loaded\. Maps: \{string\.Join"), source);
    }

    [Fact]
    public void TheGateHasNoSwitchAndNoSecondConfigKey()
    {
        var source = Text("ArenaMapPort.Client/ArenaInstallationGate.cs");
        Assert.Single(Regex.Matches(source, @"\.Bind\("));
        Assert.DoesNotContain("GetEnvironmentVariable", source);
        Assert.DoesNotContain("DeveloperOptions", source);
    }
}

/// <summary>Runs only when the developer's real Arena installation is present.</summary>
internal sealed class InstalledArenaFactAttribute : FactAttribute
{
    public InstalledArenaFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(ClientArenaGateTests.RealFolder, "ConsistencyInfo")))
            Skip = "no Arena installation at " + ClientArenaGateTests.RealFolder;
    }
}

/// <summary>Runs only when the BSG launcher's registry entry points at an existing installation.</summary>
internal sealed class RegisteredArenaFactAttribute : FactAttribute
{
    public RegisteredArenaFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
            return;
        }
        string location = new ReflectionRegistryProbe().GetValue(ClientArenaGateTests.RealUninstallKey, "InstallLocation");
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(Path.Combine(location, "ConsistencyInfo")))
            Skip = "Arena is not registered in the registry on this machine";
    }
}
