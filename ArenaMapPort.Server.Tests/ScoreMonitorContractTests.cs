using System.Reflection;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The public contract <c>ArenaMapPort.Client.ScoreMonitors</c> (docs/progress/score-monitors.md): the user's ArenaMode binds it by reflection with exact signature checks,
/// like its ArenaMapPortSpawnGates binds SpawnGates, so the public surface is APPEND-ONLY. These tests pin the member list (a new member is a deliberate edit here), bind every
/// member the way the binder does (declared, public, static, exact types) and drive a whole match through the bound delegates. They share the static hub, so they run in one
/// collection and restore it.
/// </summary>
[Collection("ScoreMonitors")]
public sealed class ScoreMonitorContractTests : IDisposable
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private readonly ScoreMonitorHub _saved = ScoreMonitors.Hub;
    private bool _raid = true, _enabled = true;
    private int _usable = 9;

    public ScoreMonitorContractTests()
    {
        ScoreMonitors.Hub = new ScoreMonitorHub(() => _raid, () => _usable, () => _enabled);
    }

    public void Dispose() => ScoreMonitors.Hub = _saved;

    // ---- the surface ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePublicSurfaceIsExactlyTheNineMembersOfContractVersion1()
    {
        Assert.Equal(1, ScoreMonitors.ContractVersion);
        var type = typeof(ScoreMonitors);
        Assert.True(type.IsPublic && type.IsAbstract && type.IsSealed, "a public static class");
        Assert.Equal("ArenaMapPort.Client.ScoreMonitors", type.FullName);

        var members = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var method in type.GetMethods(Declared).Where(m => !m.IsSpecialName))
            members.Add(Signature(method));
        foreach (var property in type.GetProperties(Declared))
            members.Add($"{property.PropertyType.Name} {property.Name} {{ get; }}" + (property.GetSetMethod() != null ? " WITH SETTER" : ""));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Empty(type.GetEvents(Declared));
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetNestedTypes(BindingFlags.Public));

        Assert.Equal(new SortedSet<string>(StringComparer.Ordinal)
        {
            "Boolean IsAvailable { get; }",
            "Boolean IsClaimed { get; }",
            "Boolean Release(Int32 claim)",
            "Boolean SetClock(Int32 claim, Int32 mode, Single seconds)",
            "Boolean SetScore(Int32 claim, Int32 scoreA, Int32 scoreB, Int32 aliveA, Int32 aliveB)",
            "Boolean SetSides(Int32 claim, Int32 colorA, Int32 colorB, Int32 teamSize)",
            "Int32 Claim(String owner)",
            "Int32 ContractVersion { get; }",
            "Int32 Count { get; }",
        }, members);
    }

    private static string Signature(MethodInfo method) =>
        $"{method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})";

    [Fact]
    public void NoGenericMembersAndNoOptionalParametersSoAnExactBinderAlwaysFindsThem()
    {
        foreach (var method in typeof(ScoreMonitors).GetMethods(Declared))
        {
            Assert.False(method.ContainsGenericParameters, method.Name);
            Assert.All(method.GetParameters(), p => Assert.False(p.IsOptional, method.Name + "." + p.Name));
        }
    }

    // ---- bound the way ArenaMode's binder binds it ---------------------------------------------------------------------------------------------

    private static Func<T> Getter<T>(string name)
    {
        var getter = typeof(ScoreMonitors).GetProperty(name, Declared)?.GetGetMethod();
        Assert.NotNull(getter);
        Assert.Equal(typeof(T), getter!.ReturnType);
        Assert.Empty(getter.GetParameters());
        return (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), getter);
    }

    private static TDelegate Bind<TDelegate>(string name, Type returns, params Type[] args) where TDelegate : Delegate
    {
        var method = typeof(ScoreMonitors).GetMethod(name, Declared, null, args, null);
        Assert.NotNull(method);
        Assert.Equal(returns, method!.ReturnType);
        return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method);
    }

    [Fact]
    public void AWholeMatchRunsThroughTheDelegatesABinderCreates()
    {
        var version = Getter<int>("ContractVersion");
        var available = Getter<bool>("IsAvailable");
        var claimed = Getter<bool>("IsClaimed");
        var count = Getter<int>("Count");
        var claim = Bind<Func<string, int>>("Claim", typeof(int), typeof(string));
        var release = Bind<Func<int, bool>>("Release", typeof(bool), typeof(int));
        var setSides = Bind<Func<int, int, int, int, bool>>("SetSides", typeof(bool), typeof(int), typeof(int), typeof(int), typeof(int));
        var setScore = Bind<Func<int, int, int, int, int, bool>>("SetScore", typeof(bool), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int));
        var setClock = Bind<Func<int, int, float, bool>>("SetClock", typeof(bool), typeof(int), typeof(int), typeof(float));

        Assert.Equal(1, version());
        Assert.True(available());
        Assert.False(claimed());
        int handle = claim("ArenaMode Team Deathmatch");
        Assert.True(handle > 0);
        Assert.True(claimed());
        Assert.Equal(handle, claim("ArenaMode Team Deathmatch"));
        Assert.True(setSides(handle, -1, -1, 5));
        Assert.True(setScore(handle, 12, 9, 4, 5));
        Assert.True(setClock(handle, 2, 1234.5f));
        var s = ScoreMonitors.Hub.Snapshot();
        Assert.Equal((12, 9, 4, 5, 5), (s.ScoreA, s.ScoreB, s.AliveA, s.AliveB, s.TeamSize));
        Assert.Equal((2, 1234.5f), (s.ClockMode, s.ClockSeconds));
        ScoreMonitors.Hub.BuiltCount = 6;
        Assert.Equal(6, count());
        Assert.True(release(handle));
        Assert.False(claimed());
        Assert.Equal(0, count());
        Assert.False(setScore(handle, 1, 1, 1, 1));   // a stale handle: the mode notices and stops pushing
        Assert.False(release(handle));
    }

    // ---- the binder block of the progress log ----------------------------------------------------------------------------------------------

    private static string Alias(Type t) => t == typeof(int) ? "int" : t == typeof(bool) ? "bool" : t == typeof(float) ? "float" : t == typeof(string) ? "string" : t.Name;

    private static string Doc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "progress", "score-monitors.md");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("docs/progress/score-monitors.md");
    }

    [Fact]
    public void TheBinderBlockOfTheProgressLogBindsExactlyTheContractMembersWithTheirExactTypes()
    {
        string doc = Doc().Replace("\r\n", "\n");
        int from = doc.IndexOf("### The ArenaMode binder", StringComparison.Ordinal);
        int to = doc.IndexOf("### The Team Deathmatch mapping", StringComparison.Ordinal);
        Assert.True(from > 0 && to > from, "the two headings");
        string block = System.Text.RegularExpressions.Regex.Replace(doc.Substring(from, to - from), @"\s+", " ");
        Assert.Contains("\"com.maschine.ArenaMapPort\"", block);
        Assert.Contains("\"ArenaMapPort.Client.ScoreMonitors\"", block);
        Assert.Contains("BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly", block);
        Assert.Contains("if (version() != 1 || !available()) return null;", block);   // another contract version means a changed member
        foreach (var method in typeof(ScoreMonitors).GetMethods(Declared).Where(m => !m.IsSpecialName))
        {
            var parameters = method.GetParameters().Select(p => Alias(p.ParameterType)).ToList();
            string expected = $"Bind<Func<{string.Join(", ", parameters.Append(Alias(method.ReturnType)))}>>(type, \"{method.Name}\", typeof({Alias(method.ReturnType)})"
                              + string.Concat(parameters.Select(p => $", typeof({p})")) + ");";
            Assert.Contains(expected, block);
        }
        foreach (var property in typeof(ScoreMonitors).GetProperties(Declared).Where(p => p.Name is "ContractVersion" or "IsAvailable"))
            Assert.Contains($"Getter<{Alias(property.PropertyType)}>(type, \"{property.Name}\")", block);
    }

    // ---- nothing throws, nothing is available outside an owned raid ------------------------------------------------------------------------------

    [Fact]
    public void OutsideAnOwnedRaidNothingIsAvailableAndEveryCallIsHarmless()
    {
        _raid = false;
        Assert.False(ScoreMonitors.IsAvailable);
        Assert.False(ScoreMonitors.IsClaimed);
        Assert.Equal(0, ScoreMonitors.Count);
        Assert.Equal(0, ScoreMonitors.Claim("mode"));
        Assert.False(ScoreMonitors.Release(1));
        Assert.False(ScoreMonitors.SetSides(1, 0, 0, 5));
        Assert.False(ScoreMonitors.SetScore(1, 0, 0, 0, 0));
        Assert.False(ScoreMonitors.SetClock(1, 1, 0f));
    }

    [Fact]
    public void TheDefaultHubBeforeInstallRefusesEverything()
    {
        ScoreMonitors.Hub = new ScoreMonitorHub(() => false, () => 0, () => false);
        Assert.False(ScoreMonitors.IsAvailable);
        Assert.Equal(0, ScoreMonitors.Claim("mode"));
    }

    [Fact]
    public void ABrokenHubNeverMakesAPublicMemberThrow()
    {
        ScoreMonitors.Hub = new ScoreMonitorHub(() => throw new InvalidOperationException("raid"), () => throw new InvalidOperationException("map"),
            () => throw new InvalidOperationException("config"));
        Assert.False(ScoreMonitors.IsAvailable);
        Assert.Equal(0, ScoreMonitors.Claim("mode"));
        Assert.False(ScoreMonitors.IsClaimed);
        Assert.False(ScoreMonitors.Release(1));
        ScoreMonitors.Hub = null!;   // even a missing hub: every member catches
        Assert.False(ScoreMonitors.IsAvailable);
        Assert.False(ScoreMonitors.IsClaimed);
        Assert.Equal(0, ScoreMonitors.Count);
        Assert.Equal(0, ScoreMonitors.Claim("mode"));
        Assert.False(ScoreMonitors.Release(1));
        Assert.False(ScoreMonitors.SetSides(1, 0, 0, 5));
        Assert.False(ScoreMonitors.SetScore(1, 0, 0, 0, 0));
        Assert.False(ScoreMonitors.SetClock(1, 1, 0f));
    }

    [Fact]
    public void ADisabledFeatureRefusesTheClaimAndAMapWithoutUsableMonitorsToo()
    {
        _enabled = false;
        Assert.False(ScoreMonitors.IsAvailable);
        Assert.Equal(0, ScoreMonitors.Claim("mode"));
        _enabled = true;
        _usable = 0;
        Assert.False(ScoreMonitors.IsAvailable);
        Assert.Equal(0, ScoreMonitors.Claim("mode"));
    }

    [Fact]
    public void ADemoClaimNeverBlocksAPluginThroughThePublicApi()
    {
        int demo = ScoreMonitors.Hub.Claim("developer-demo", true);
        Assert.True(ScoreMonitors.IsClaimed);
        int plugin = ScoreMonitors.Claim("ArenaMode");
        Assert.True(plugin > 0 && plugin != demo);
        Assert.False(ScoreMonitors.SetScore(demo, 1, 1, 1, 1));
        Assert.True(ScoreMonitors.SetScore(plugin, 1, 1, 1, 1));
    }
}
