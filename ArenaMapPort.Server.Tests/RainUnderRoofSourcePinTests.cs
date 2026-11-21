#nullable disable
using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins of the rain-drops check (RainUnderRoofRepairs, docs/progress/two-maps-item-6-raincover.md "2026-11-21"): the per-frame hook costs one static
/// read when disarmed and does no search, no log and no string work; the core stays Unity-free; the repair is wired, released at raid end and not a
/// public setting. The GPU glue itself is host-checked only.
/// </summary>
public sealed class RainUnderRoofSourcePinTests
{
    static string ClientDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "ArenaMapPort.Client");
            if (File.Exists(Path.Combine(path, "Plugin.cs"))) return path;
        }
        throw new DirectoryNotFoundException("ArenaMapPort.Client");
    }

    static string Source(string file) => File.ReadAllText(Path.Combine(ClientDir(), file)).Replace("\r\n", "\n");

    /// <summary>The text of one method: from its signature line to the next member at the same indent.</summary>
    static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int next = Regex.Match(source.Substring(start + signature.Length), @"\n        (static|internal|sealed|///|//)").Index;
        return source.Substring(start, next + signature.Length);
    }

    [Fact]
    public void ThePerFrameHookStartsWithTheOneStaticRead()
    {
        string body = Method(Source("RainUnderRoofRepairs.cs"), "internal static void CheckPostfix(");
        Assert.Matches(@"\{\s*if \(!_armed\) return;", body);
    }

    [Theory]
    [InlineData("internal static void CheckPostfix(")]
    [InlineData("static void RequestWindow(")]
    [InlineData("static void OnReadback(")]
    public void TheFramePathDoesNotSearchAScene(string signature)
    {
        string body = Method(Source("RainUnderRoofRepairs.cs"), signature);
        foreach (string banned in new[] { "FindObjectsOfType", "FindObjectOfType", "GetComponentsInChildren", "FindObjectsByType", "Resources.", "GameObject.Find", "Camera.main" })
            Assert.DoesNotContain(banned, body);
    }

    [Theory]
    [InlineData("internal static void CheckPostfix(")]
    [InlineData("static void RequestWindow(")]
    public void TheFramePathHasNoStringWorkAndNoAllocation(string signature)
    {
        string body = Method(Source("RainUnderRoofRepairs.cs"), signature);
        Assert.DoesNotContain("$\"", body);
        Assert.DoesNotContain("new ", body.Replace("RainWindow window", string.Empty));
        Assert.DoesNotContain("ToString", body);
        Assert.DoesNotContain("Linq", body);
    }

    [Fact]
    public void TheFrameHookLogsOnlyThroughTheTransitionHelperAndTheFailurePath()
    {
        string source = Source("RainUnderRoofRepairs.cs");
        string body = Method(source, "internal static void CheckPostfix(");
        Assert.DoesNotContain("Plugin.Log", body);
        Assert.Contains("LogDecision(", body);
        // the helper is rate limited
        Assert.Contains("_nextLog", Method(source, "static void LogDecision("));
        // Warnings: the one failure path only
        Assert.Single(Regex.Matches(source, @"LogWarning\(Tag \+ ""the rain-drops check cannot work"));
    }

    [Fact]
    public void ARealWeatherObstacleAlwaysWinsAndNothingEscapesIntoEft()
    {
        string source = Source("RainUnderRoofRepairs.cs");
        string frame = Method(source, "internal static void CheckPostfix(");
        Assert.Contains("global::WeatherObstacle.Instance", frame);
        Assert.Contains("return; // a real obstacle always wins", frame);
        Assert.Contains("catch (Exception e)", frame);
        Assert.Contains("catch (Exception e)", Method(source, "internal static void RenderPostfix("));
        Assert.Contains("catch (Exception e)", Method(source, "static void OnReadback("));
        Assert.Contains("catch (Exception e)", Method(source, "static void OnProbe("));
    }

    [Fact]
    public void TheEngagementGateIsAskedAtEveryPhotoAndDisarmsWhenItRefuses()
    {
        string source = Source("RainUnderRoofRepairs.cs");
        string render = Method(source, "internal static void RenderPostfix(");
        int ask = render.IndexOf("RainCoverRepairs.PhotoCarriesCover(", StringComparison.Ordinal);
        int arm = render.IndexOf("_armed = true;", StringComparison.Ordinal);
        Assert.True(ask >= 0 && arm > ask, "the gate must be asked before the repair arms");
        Assert.Matches(@"if \(!RainCoverRepairs\.PhotoCarriesCover\([^
]*\)\)\s*\{\s*Disarm\(", render);
        // the gate itself is the tested model function
        Assert.Contains("RainUnderRoofModel.CarriesCover(", Source("RainCoverRepairs.cs"));
    }

    [Fact]
    public void ALateOrFailedReadbackOfAnEndedPhotoCountsNothing()
    {
        string source = Source("RainUnderRoofRepairs.cs");
        string body = Method(source, "static void OnReadback(");
        Assert.True(body.IndexOf("if (!_armed || slot.Window.Generation != _state.Generation) return;", StringComparison.Ordinal)
            < body.IndexOf("hasError", StringComparison.Ordinal));
        Assert.Contains("_errors = 0;", Method(source, "internal static void Release("));
    }

    [Fact]
    public void TheOrientationProbeUsesTheRegionOverloadAndRestoresTheActiveTarget()
    {
        string source = Source("RainUnderRoofRepairs.cs");
        string probe = Method(source, "static void StartProbe(");
        Assert.Contains("RainUnderRoofModel.ProbeRegionX", probe);
        Assert.Contains("RenderTexture.active = previous;", probe);
        Assert.Contains("ClassifyRegionProbe", Method(source, "static void OnProbe("));
    }

    [Fact]
    public void TheHarmonyPostfixParametersMatchTheEftSignature()
    {
        string source = Source("RainUnderRoofRepairs.cs");
        Assert.Contains("CheckPostfix(DepthPhotograper __instance, Transform cameraTransform, ref bool __result)", source);
        Assert.Contains("RenderPostfix(DepthPhotograper __instance)", source);
    }

    [Fact]
    public void TheCoreIsUnityFreeAndLinkedIntoTheTests()
    {
        string core = Source("RainUnderRoofModel.cs");
        Assert.DoesNotContain("using UnityEngine", core);
        Assert.DoesNotContain("BepInEx", core);
        Assert.DoesNotContain("Plugin.", core);
        string project = File.ReadAllText(Path.Combine(ClientDir(), "..", "ArenaMapPort.Server.Tests", "ArenaMapPort.Server.Tests.csproj"));
        Assert.Contains("<Compile Include=\"..\\ArenaMapPort.Client\\RainUnderRoofModel.cs\" Link=\"Client\\RainUnderRoofModel.cs\" />", project);
    }

    [Fact]
    public void TheRepairIsInstalledReleasedAndDescribed()
    {
        Assert.Contains("RainUnderRoofRepairs.InstallHooks(harmony);", Source("HarmonyPatches.cs"));
        Assert.Contains("RainUnderRoofRepairs.Install();", Source("Plugin.cs"));
        string rig = Source("WeatherRig.cs");
        Assert.Contains("RainUnderRoofRepairs.Release();", rig);
        Assert.True(rig.IndexOf("static void OnSceneUnloaded(", StringComparison.Ordinal) < rig.IndexOf("RainUnderRoofRepairs.Release();", StringComparison.Ordinal));
        Assert.Contains("RainUnderRoofRepairs.Describe(", Source("RainCoverRepairs.cs"));
        Assert.Contains("RainUnderRoofRepairs.Describe(", Source("FootstepSurfaceProbe.cs"));
    }

    [Fact]
    public void TheKeyIsBoundThroughSettingsAndIsNotAPublicOption()
    {
        string source = Source("RainUnderRoofRepairs.cs");
        Assert.Contains("Plugin.Instance.Settings.Bind(", source);
        Assert.DoesNotContain("Plugin.Instance.Config.Bind(", source);
        Assert.Single(Regex.Matches(source, @"\.Bind\("));
        foreach (string file in new[] { "CompactConfiguration.cs", "MapConfigVisibility.cs", "PlayerOptions.cs" })
            Assert.DoesNotContain("RainUnderRoofCheck", Source(file));
    }

    [Fact]
    public void TheDisarmedStateWritesBackOnlyWhatThisRepairWrote()
    {
        string source = Source("RainUnderRoofRepairs.cs");
        string disarm = Method(source, "static void Disarm(");
        Assert.Contains("if (_wroteTrue)", disarm);
        Assert.Equal(1, Regex.Matches(source, @"RainController\.IsCameraUnderRain = ").Count);
    }
}
