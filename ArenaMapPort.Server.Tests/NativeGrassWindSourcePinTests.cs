#nullable disable
using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins of the grass wind (NativeGrassWind.cs, NativeGrassWindModel.cs): the per-camera callback is a hot path with no search, log, allocation or try; EFT's own calculator is never written beside;
/// one cached delegate is subscribed and removed; the glue is armed and released by the grass repair; the key is a non-public default-on key; the files touch none of what the design excludes.
/// The GPU glue itself is host-checked only.
/// </summary>
public sealed class NativeGrassWindSourcePinTests
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

    static string Code(string source)
    {
        string noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        string noLine = Regex.Replace(noBlock, @"//[^\n]*", string.Empty);
        return Regex.Replace(noLine, "\"(\\\\.|[^\"\\\\])*\"", "\"\"");
    }

    /// <summary>The text of one method: from its signature to the next member at the same indent.</summary>
    static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        string rest = source.Substring(start + signature.Length);
        Match m = Regex.Match(rest, @"\n        (static|internal|sealed|///|//)");
        return source.Substring(start, signature.Length + (m.Success ? m.Index : rest.Length));
    }

    [Fact]
    public void TheModelIsLinkedIntoTheTestsAndIsUnityFree()
    {
        string csproj = File.ReadAllText(Path.Combine(ClientDir(), "..", "ArenaMapPort.Server.Tests", "ArenaMapPort.Server.Tests.csproj"));
        Assert.Contains("..\\ArenaMapPort.Client\\NativeGrassWindModel.cs", csproj);
        string code = Code(Source("NativeGrassWindModel.cs"));
        foreach (string banned in new[] { "UnityEngine", "BepInEx", "Plugin.", "LogDebug", "Shader", "Time.", "DateTime", "Random", "Stopwatch" })
            Assert.DoesNotContain(banned, code);
    }

    [Fact]
    public void TheCallbackStartsWithTheOneStaticRead()
    {
        string body = Method(Source("NativeGrassWind.cs"), "static void OnPreCull(Camera camera)");
        Assert.Matches(@"\{\s*if \(!_armed \|\| !_enabled\) return;", body);
    }

    [Fact]
    public void TheCallbackSearchesLogsAllocatesAndCatchesNothing()
    {
        string body = Code(Method(Source("NativeGrassWind.cs"), "static void OnPreCull(Camera camera)"));
        foreach (string banned in new[] { "Find", "GetComponent", "Log", "new ", "try", "catch", "Linq", "Select(", "Where(", "ToList", "ToArray", "foreach", "string", "$\"", "Reflection", "GetField", "Invoke", "Camera.main", "Resources.", "ToString", "=>", "Physics.", "GameObject" })
            Assert.DoesNotContain(banned, body);
    }

    [Fact]
    public void TheCalculatorGuardComesBeforeTheFirstWrite()
    {
        string body = Method(Source("NativeGrassWind.cs"), "static void OnPreCull(Camera camera)");
        int guard = body.IndexOf("Shader.GetGlobalFloat(WindCalculatedId) >= 0.5f) return;", StringComparison.Ordinal);
        int write = body.IndexOf("Shader.SetGlobalVector", StringComparison.Ordinal);
        Assert.True(guard > 0 && write > guard);
    }

    [Fact]
    public void OneCachedDelegateIsSubscribedAndRemoved()
    {
        string code = Code(Source("NativeGrassWind.cs"));
        Assert.Single(Regex.Matches(code, @"Camera\.onPreCull \+= Handler;"));
        Assert.Single(Regex.Matches(code, @"Camera\.onPreCull -= Handler;"));
        Assert.Equal(2, Regex.Matches(code, @"Camera\.onPreCull").Count);
        Assert.Contains("static readonly Camera.CameraCallback Handler = OnPreCull;", code);
    }

    [Fact]
    public void TheGrassRepairArmsAfterTheBuildAndReleasesInTheTeardown()
    {
        string repairs = Source("NativeGrassRepairs.cs");
        Assert.Single(Regex.Matches(repairs, @"NativeGrassWind\.Arm\("));
        Assert.Single(Regex.Matches(repairs, @"NativeGrassWind\.Release\("));
        string build = Method(repairs, "static void DoBuild()");
        Assert.True(build.IndexOf("_state.BuildSucceeded();", StringComparison.Ordinal) < build.IndexOf("NativeGrassWind.Arm(", StringComparison.Ordinal));
        string teardown = Method(repairs, "static void TearDownNow(GrassTeardownReason reason)");
        int release = teardown.IndexOf("NativeGrassWind.Release();", StringComparison.Ordinal);
        Assert.True(release > 0 && release < teardown.IndexOf("if (b == null)", StringComparison.Ordinal));
        Assert.Contains("NativeGrassWind.Install();", Source("Plugin.cs"));
    }

    [Fact]
    public void TheKeyIsANonPublicDefaultOnBoolBoundThroughSettings()
    {
        string code = Code(Source("NativeGrassWind.cs"));
        Assert.Contains("internal const string KeyName = \"\";", code);
        Assert.Contains("GrassWind", Source("NativeGrassWind.cs"));
        Assert.Contains("internal const string KeyName = \"GrassWind\";", Source("NativeGrassWind.cs"));
        Assert.Contains("internal const bool KeyDefault = true;", code);
        Assert.Contains("Plugin.Instance.Settings.Bind(NativeGrassKeys.SectionOf(key), KeyName, KeyDefault", code);
        Assert.DoesNotContain("Instance.Config", code);
        foreach (string file in new[] { "CompactConfiguration.cs", "MapConfigVisibility.cs", "PlayerOptions.cs" })
            Assert.DoesNotContain("GrassWind", Source(file));
    }

    [Fact]
    public void TheNewFilesTouchNoneOfWhatTheDesignExcludes()
    {
        foreach (string file in new[] { "NativeGrassWind.cs", "NativeGrassWindModel.cs" })
        {
            string code = Code(Source(file));
            foreach (string banned in new[] { "_impostorsWind", "TreeWind", "WindCalculator.Create", "ArenaInstallation", "LogInfo(", "LogWarning(", "LogError(", "DontDestroyOnLoad", "ComputeShader" })
                Assert.DoesNotContain(banned, code);
            // _WindCalculated is only ever read, never written (the property id is named once, and no SetGlobal call takes it)
            Assert.DoesNotMatch(@"SetGlobal\w+\(\s*WindCalculatedId", code);
            Assert.DoesNotContain("_WindVector\"", Regex.Replace(Source(file), "PropertyToID\\(\"_WindVector\"\\)", string.Empty));
        }
        string glue = Code(Source("NativeGrassWind.cs"));
        Assert.DoesNotMatch(@"SetGlobal\w+\(\s*WindVectorId", glue);
    }
}
