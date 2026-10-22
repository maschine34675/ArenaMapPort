#nullable disable
using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins of the in-game map notices (docs/progress/map-notices.md): the Unity-free core must stay Unity-free, must never call the Arena gate's
/// blocking entry point, must not bind configuration, and the plugin's guarded wiring stays where the existing order pins expect it. Pins on files that
/// do not exist yet (the Unity glue) apply to them as soon as they appear.
/// </summary>
public sealed class MapNoticeSourcePinTests
{
    static readonly string[] UnityFreeFiles =
    {
        "MapNoticeText.cs", "MapNoticeInputs.cs", "MapNoticeSelector.cs", "MapNoticeScheduler.cs", "MapLoadFailures.cs", "ServerStatusParser.cs",
        "SkippedHooks.cs",
    };

    static string ClientDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "ArenaMapPort.Client");
            if (File.Exists(Path.Combine(path, "Plugin.cs")))
                return path;
        }
        throw new DirectoryNotFoundException("ArenaMapPort.Client");
    }

    static string Source(string file) => File.ReadAllText(Path.Combine(ClientDirectory(), file));

    /// <summary>Every notice file of the client directory: the Unity-free core and the glue (MapNotices.cs, ServerStatusClient.cs).</summary>
    static IEnumerable<string> NoticeFiles()
    {
        string dir = ClientDirectory();
        return Directory.GetFiles(dir, "MapNotice*.cs").Concat(Directory.GetFiles(dir, "ServerStatus*.cs"))
            .Concat(Directory.GetFiles(dir, "MapLoadFailures.cs")).Concat(Directory.GetFiles(dir, "SkippedHooks.cs"))
            .Select(Path.GetFileName).Distinct().OrderBy(name => name).ToList();
    }

    [Fact]
    public void TheCoreFilesExistAndAreLinkedIntoTheTestProject()
    {
        string project = File.ReadAllText(Path.Combine(ClientDirectory(), "..", "ArenaMapPort.Server.Tests", "ArenaMapPort.Server.Tests.csproj"));
        foreach (string file in UnityFreeFiles)
        {
            Assert.True(File.Exists(Path.Combine(ClientDirectory(), file)), file);
            Assert.Contains("<Compile Include=\"..\\ArenaMapPort.Client\\" + file + "\" Link=\"Client\\" + file + "\" />", project);
        }
    }

    [Fact]
    public void TheGlueAndTheProbeExistAndAreNoticeFiles()
    {
        // the pins below apply to them: no blocking gate call, no UI type outside MapNotices.cs, no configuration binding but the one switch
        Assert.Contains("MapNotices.cs", NoticeFiles());
        Assert.Contains("ServerStatusClient.cs", NoticeFiles());
        Assert.Contains("SkippedHooks.cs", NoticeFiles());
    }

    [Fact]
    public void NoNoticeFileEverCallsTheBlockingGateEntryPoint()
    {
        // EnsureVerified can block a game thread for 30 s; notice code reads the gate with State, Result and WaitForResult(TimeSpan.Zero) only
        foreach (string file in NoticeFiles())
            Assert.DoesNotContain("EnsureVerified(", Source(file));
    }

    [Fact]
    public void NoNoticeFileResolvesAKeyOrPreparesABundle()
    {
        // keys resolve at TryServe time (3 to 7.4 MB of Arena files per map): a stage 2 key failure is explained AFTER the failed attempt from the failure
        // registry, never by pre-resolving keys at the main menu
        foreach (string file in NoticeFiles())
        {
            string source = Source(file);
            foreach (string call in new[] { "TryGetKey(", ".Prepare(", "ResolveFile(", "Derive(" })
                Assert.False(source.Contains(call), file + " must not call " + call);
        }
    }

    [Fact]
    public void TheUnityFreeCoreReferencesNoUnityBepInExOrEftApiAndLogsThroughTheEnvironment()
    {
        var forbidden = new[]
        {
            "using UnityEngine", "using BepInEx", "using EFT", "using HarmonyLib", "using Comfort", "using TMPro", "using SPT.",
            "ItemUiContext", "NotificationManager", "EftScreenManager", "SettingsManager", "PreloaderUI", "Plugin.Log.", "Plugin.Instance",
        };
        foreach (string file in UnityFreeFiles)
        {
            string source = Source(file);
            foreach (string token in forbidden)
                Assert.False(source.Contains(token), file + " must not mention " + token);
        }
    }

    [Fact]
    public void OnlyTheGlueMayCallTheUiAndSettingsTypes()
    {
        // MapNotices.cs (the glue) is the one place that talks to ItemUiContext, NotificationManager, EftScreenManager and SettingsManager
        string[] uiTypes = { "ItemUiContext", "NotificationManager", "EftScreenManager", "SettingsManager" };
        foreach (string file in NoticeFiles().Where(name => name != "MapNotices.cs"))
            foreach (string type in uiTypes)
                Assert.False(Source(file).Contains(type), file + " must not mention " + type);
    }

    [Fact]
    public void NoticeCodeBindsNoConfigurationExceptTheOneShowMapNoticesEntryInTheGlue()
    {
        foreach (string file in NoticeFiles().Where(name => name != "MapNotices.cs"))
        {
            string source = Source(file);
            Assert.DoesNotContain(".Bind(", source);
            Assert.DoesNotContain("Config.Bind", source);
        }
        string glue = Path.Combine(ClientDirectory(), "MapNotices.cs");
        if (File.Exists(glue))
        {
            string source = File.ReadAllText(glue);
            Assert.Single(Regex.Matches(source, @"\.Bind\("));
            Assert.Contains("\"ShowMapNotices\"", source);
        }
    }

    [Fact]
    public void NoticeCodeNeverReadsTheEnvironmentOrDeveloperOptions()
    {
        foreach (string file in NoticeFiles())
        {
            string source = Source(file);
            Assert.DoesNotContain("GetEnvironmentVariable", source);
            Assert.DoesNotContain("DeveloperOptions", source);
        }
    }

    [Fact]
    public void EveryNoticeLogLineUsesTheNoticeTagAndNoOtherPrefix()
    {
        Assert.Contains("internal const string Tag = \"[Notice]\";", Source("MapNoticeScheduler.cs"));
        // the scheduler is the one source of notice log lines: every message goes through Debug, Info or Warning, which add the tag
        string scheduler = Source("MapNoticeScheduler.cs");
        Assert.Empty(Regex.Matches(scheduler, @"_env\.(Debug|Info|Warning)\((?!Tag \+)"));
    }

    [Fact]
    public void TheNoticeCoreNeverCallsTheGatesWriters()
    {
        // the gate's state is read-only to the notices: no Start, Initialize, ResetForTests, LogOutcome or PollOutcome call from notice code
        foreach (string file in UnityFreeFiles)
        {
            string source = Source(file);
            foreach (string call in new[] { "ArenaInstallationGate.Start(", "ArenaInstallationGate.Initialize(", "ArenaInstallationGate.ResetForTests(", "ArenaInstallationGate.LogOutcome(", "ArenaInstallationGate.PollOutcome(" })
                Assert.DoesNotContain(call, source);
        }
    }

    [Fact]
    public void TheGateKeepsOneBindAndTheReadOnlyStateMembersAreInPlace()
    {
        string gate = Source("ArenaInstallationGate.cs");
        Assert.Single(Regex.Matches(gate, @"\.Bind\("));
        Assert.Contains("public enum GateState", gate);
        Assert.Contains("internal static GateState State", gate);
        Assert.Contains("internal static string FailureDetail", gate);
        // PollOutcome stays pinned as the one call Plugin.LateUpdate makes before the notice tick
        Assert.Matches(new Regex(@"void LateUpdate\(\)\s*\{[^}]*ArenaInstallationGate\.PollOutcome\(\);[^}]*\}"), Source("Plugin.cs"));
    }
}
