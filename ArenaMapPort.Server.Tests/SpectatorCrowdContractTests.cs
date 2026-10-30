using System.Reflection;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The public contract <c>ArenaMapPort.Client.SpectatorCrowd</c> (docs/progress/spectator-audio.md "Crowd bed"): the user's ArenaMode binds it by reflection with exact
/// signature checks, like ScoreMonitors, so the public surface is APPEND-ONLY. These tests pin the member list (a new member is a deliberate edit here, and a removal or a
/// change of meaning raises ContractVersion), bind every member the way a binder does and drive the state through the bound delegates. They share the static state, so they
/// run in one collection and restore it.
/// </summary>
[Collection("SpectatorCrowd")]
public sealed class SpectatorCrowdContractTests : IDisposable
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private readonly CrowdBedState _saved = SpectatorCrowd.State;

    public SpectatorCrowdContractTests() { SpectatorCrowd.State = new CrowdBedState(); }

    public void Dispose() => SpectatorCrowd.State = _saved;

    private static string Signature(MethodInfo method) =>
        $"{method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})";

    [Fact]
    public void ThePublicSurfaceIsExactlyTheFourMembersOfContractVersion1()
    {
        Assert.Equal(1, SpectatorCrowd.ContractVersion);
        var type = typeof(SpectatorCrowd);
        Assert.True(type.IsPublic && type.IsAbstract && type.IsSealed, "a public static class");
        Assert.Equal("ArenaMapPort.Client.SpectatorCrowd", type.FullName);

        var members = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var method in type.GetMethods(Declared).Where(m => !m.IsSpecialName))
            members.Add(Signature(method));
        foreach (var property in type.GetProperties(Declared))
            members.Add($"{property.PropertyType.Name} {property.Name} {{ get; }}" + (property.GetSetMethod() != null ? " WITH SETTER" : ""));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));   // no field, no constant
        Assert.Empty(type.GetEvents(Declared));
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetNestedTypes(BindingFlags.Public));

        Assert.Equal(new SortedSet<string>(StringComparer.Ordinal)
        {
            "Boolean IsAvailable { get; }",
            "Int32 ContractVersion { get; }",
            "Single Excitement { get; }",
            "Void SetExcitement(Single level01)",
        }, members);
        Assert.Equal(4, members.Count);
    }

    [Fact]
    public void NoGenericMembersAndNoOptionalParametersSoAnExactBinderAlwaysFindsThem()
    {
        foreach (var method in typeof(SpectatorCrowd).GetMethods(Declared))
        {
            Assert.False(method.ContainsGenericParameters, method.Name);
            Assert.All(method.GetParameters(), p => Assert.False(p.IsOptional, method.Name + "." + p.Name));
        }
    }

    private static Func<T> Getter<T>(string name)
    {
        var getter = typeof(SpectatorCrowd).GetProperty(name, Declared)?.GetGetMethod();
        Assert.NotNull(getter);
        Assert.Equal(typeof(T), getter!.ReturnType);
        Assert.Empty(getter.GetParameters());
        return (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), getter);
    }

    [Fact]
    public void AMatchRunsThroughTheDelegatesABinderCreates()
    {
        var version = Getter<int>("ContractVersion");
        var available = Getter<bool>("IsAvailable");
        var excitement = Getter<float>("Excitement");
        var method = typeof(SpectatorCrowd).GetMethod("SetExcitement", Declared, null, new[] { typeof(float) }, null);
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
        var set = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), method);

        Assert.Equal(1, version());
        Assert.False(available());                 // no bed yet: a menu, a vanilla map or a package without one
        Assert.Equal(0f, excitement());
        set(0.75f);
        Assert.Equal(0.75f, excitement());         // the level last set, whether or not a bed sounds
        Assert.Equal(0.75f, SpectatorCrowd.State.Excitement);
        SpectatorCrowd.State.Available = true;     // the Unity side switches this
        Assert.True(available());
        set(1f); set(1f); set(1f);                 // idempotent, last write wins
        Assert.Equal(1f, excitement());
        set(0f);
        Assert.Equal(0f, excitement());
        SpectatorCrowd.State.Reset();              // the crowd is removed with the raid
        Assert.False(available());
        set(0.4f);
        SpectatorCrowd.State.Reset();
        Assert.Equal(0f, excitement());
    }

    [Theory]
    [InlineData(2f, 1f)]
    [InlineData(-0.5f, 0f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(0.3f, 0.3f)]
    public void ALevelOutsideZeroToOneIsClampedAndNaNCountsAsZero(float given, float stored)
    {
        SpectatorCrowd.SetExcitement(given);
        Assert.Equal(stored, SpectatorCrowd.Excitement);
    }

    [Fact]
    public async Task EveryMemberMayBeCalledFromAnyThreadAndNoneThrows()
    {
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 20000; i++)
            {
                SpectatorCrowd.SetExcitement((i % 101) / 100f);
                float level = SpectatorCrowd.Excitement;
                Assert.InRange(level, 0f, 1f);
                _ = SpectatorCrowd.IsAvailable;
                _ = SpectatorCrowd.ContractVersion;
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        // a broken state never escapes through the contract
        SpectatorCrowd.State = null!;
        Assert.False(SpectatorCrowd.IsAvailable);
        Assert.Equal(0f, SpectatorCrowd.Excitement);
        SpectatorCrowd.SetExcitement(0.5f);
    }
}
