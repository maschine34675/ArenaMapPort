using System.Reflection;
using System.Text.RegularExpressions;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-AIRPIT items 15, 20, 21 (Bowl joined the released set with PLAN-BOWL item 8, 2026-10-18; every list below is
/// compared with <see cref="ArenaMapRegistry.All"/>, so the same tests pin Bowl). The client files that bind a per-map table (native acoustics, room reverb, rain
/// audio, projection decals) and the csproj rows that stage a map's AI sidecar and acoustic package are sources of
/// silent failure: a missing entry does not throw, the feature just never starts on that map (RoomReverb would even
/// throw a KeyNotFoundException against a map NativeSpatialAudio bound). They sit on Unity and BepInEx types and
/// cannot be compiled into this project, so the lists are read as text and compared with the released set.
/// </summary>
public sealed class AirPitWiringTests
{
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

    static string Stem(ArenaMapDescriptor map) => map.Key.Substring("arena_".Length);

    static string[] ReleasedKeys => ArenaMapRegistry.All.Select(m => m.Key).Order().ToArray();

    static string KeyOf(string registryMember)
    {
        var field = typeof(ArenaMapRegistry).GetField(registryMember, BindingFlags.Public | BindingFlags.Static);
        Assert.True(field != null, "ArenaMapRegistry has no member " + registryMember);
        return ((ArenaMapDescriptor)field!.GetValue(null)!).Key;
    }

    [Theory]
    [InlineData("ArenaMapPort.Client/NativeSpatialAudio.cs")]
    [InlineData("ArenaMapPort.Client/RoomReverb.cs")]
    [InlineData("ArenaMapPort.Client/PrecipitationAudio.cs")]
    [InlineData("ArenaMapPort.Client/NativeDecalRepairs.cs")]
    public void PerMapClientTablesNameEveryReleasedMap(string file)
    {
        // Each of these files binds its per-map config with one `foreach (var map in new[] { ArenaMapRegistry.X, ... })`.
        var matches = Regex.Matches(Text(file), @"foreach \(var map in new\[\] \{([^}]*)\}\)");
        var match = Assert.Single(matches);
        var keys = Regex.Matches(match.Groups[1].Value, @"ArenaMapRegistry\.(\w+)")
            .Select(m => KeyOf(m.Groups[1].Value)).Order().ToArray();
        Assert.Equal(ReleasedKeys, keys);
    }

    [Fact]
    public void TheThreeAudioTablesAndTheAcousticsStagingAgree()
    {
        // The csproj stages one acoustics package per released map; the C# tables above read it by map key.
        var csproj = Text("ArenaMapPort.Client/ArenaMapPort.Client.csproj");
        var staged = Regex.Matches(csproj, @"<ArenaMapPortAcousticFile Include=[^>]*><MapKey>(arena_\w+)</MapKey>")
            .Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(ReleasedKeys, staged);
        // The Error condition that guards a missing input names the same maps.
        var guarded = Regex.Matches(csproj, @"\\(arena_\w+)\\spatial-audio\.json'\)")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(ReleasedKeys, guarded);
    }

    [Fact]
    public void EveryReleasedMapWithAnAiSidecarStagesIt()
    {
        // AiAnchors.Load reads <plugin>/ai/<key without "arena_">.ai.json. A tracked sidecar that is not staged is
        // silently ignored (the repairs fall back to NavMesh-derived synthesis); a staged row without a file breaks
        // the Release build.
        var csproj = Text("ArenaMapPort.Client/ArenaMapPort.Client.csproj");
        var rows = Regex.Matches(csproj, @"ArenaMapPortAiSidecar Include=""[^""]*\\tools\\maps\\(\w+)\.ai\.json""")
            .Select(m => m.Groups[1].Value).Order().ToArray();
        var withSidecar = ArenaMapRegistry.All.Where(m => File.Exists(RepoFileOrNull($"tools/maps/{Stem(m)}.ai.json")))
            .Select(Stem).Order().ToArray();
        Assert.Equal(withSidecar, rows);
        Assert.Contains("airpit", rows);
        Assert.Contains("bowl", rows); // PLAN-BOWL item 8/15
        foreach (var row in rows) Assert.NotNull(RepoFileOrNull($"tools/maps/{row}.ai.json"));
    }

    [Fact]
    public void EveryReleasedMapsShaderSidecarIsStagedUnderItsKey()
    {
        // 2026-10-17: one naming rule for every map. ShaderRebind.SidecarFileName reads <plugin>/material-shaders.<key>.json;
        // the csproj renames each map's build output (work/build/<dir>/material-shaders.json) to that name while staging.
        // Equator's pre-generalization "material-shaders.json" is retired; a literal under that name must not come back.
        var csproj = Text("ArenaMapPort.Client/ArenaMapPort.Client.csproj");
        var staged = Regex.Matches(csproj, @"DestinationFiles=""\$\(ArenaMapPortDistDir\)\\material-shaders\.(arena_\w+)\.json""")
            .Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(ReleasedKeys, staged);
        Assert.DoesNotMatch(@"SourceFiles=""@\(ArenaMapPortShaderSidecar\)""\s+DestinationFolder=", csproj); // the old unrenamed Equator Copy
        var rebind = Text("ArenaMapPort.Client/ShaderRebind.cs");
        Assert.Contains("internal static string SidecarFileName(ArenaMapDescriptor map) => $\"material-shaders.{map.Key}.json\";", rebind);
        // No code path may hand out the legacy name again (a comment may still mention it).
        Assert.DoesNotMatch(@"(=|\?|:|return)\s*""material-shaders\.json""", rebind);
    }

    static string? RepoFileOrNull(string relative)
    {
        try { return RepoFile(relative); } catch (FileNotFoundException) { return null; }
    }

    [Fact]
    public void BarbedWireAndBallisticsRepairsAreNotMapGated()
    {
        // PLAN-AIRPIT item 15/13: both attach by name/layer on every owned scene, so AirPit needs no entry. A map
        // literal creeping into either file would silently leave AirPit (or Equator) out; keep them map-agnostic.
        foreach (var file in new[] { "ArenaMapPort.Client/BarbedWireRepairs.cs", "ArenaMapPort.Client/BallisticTwinRepairs.cs" })
        {
            var code = string.Join("\n", Text(file).Split('\n').Where(l => !l.TrimStart().StartsWith("///") && !l.TrimStart().StartsWith("//")));
            Assert.DoesNotMatch(@"ArenaMapRegistry\.(Equator|Bay5|AirPit|Bowl)\b|""arena_(equator|bay5|airpit|bowl)""", code);
        }
    }
}
