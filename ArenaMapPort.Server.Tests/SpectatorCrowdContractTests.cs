using System.Reflection;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The public contract <c>ArenaMapPort.Client.SpectatorCrowd</c> (docs/progress/spectator-audio.md "Crowd bed"): the user's ArenaMode binds it by reflection with exact
/// signature checks, like ScoreMonitors, so the public surface is APPEND-ONLY. These tests pin the member list (a new member is a deliberate edit here, and a removal or a
/// change of meaning raises ContractVersion), bind every member the way a binder does and drive the state through the bound delegates. They share the static state, so they
/// run in one collection and restore it. Since 2026-11-02 the contract has eight members: the four of the bed and the four reaction members (docs/PLAN-CROWD-REACTIONS.md 4.3),
/// which a binder treats as optional and binds apart from SetExcitement.
/// </summary>
[Collection("SpectatorCrowd")]
public sealed class SpectatorCrowdContractTests : IDisposable
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private readonly CrowdBedState _saved = SpectatorCrowd.State;
    private readonly CrowdReactionQueue _savedReactions = SpectatorCrowd.Reactions;

    public SpectatorCrowdContractTests()
    {
        SpectatorCrowd.State = new CrowdBedState();
        SpectatorCrowd.Reactions = new CrowdReactionQueue();
    }

    public void Dispose()
    {
        SpectatorCrowd.State = _saved;
        SpectatorCrowd.Reactions = _savedReactions;
    }

    private static string Signature(MethodInfo method) =>
        $"{method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})";

    [Fact]
    public void ThePublicSurfaceIsExactlyTheEightMembersOfContractVersion1()
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
            "Boolean React(Int32 claim, Int32 kind, Single strength01)",
            "Boolean ReleaseReactions(Int32 claim)",
            "Int32 ClaimReactions(String owner)",
            "Int32 ContractVersion { get; }",
            "Int32 ReactionKinds { get; }",
            "Single Excitement { get; }",
            "Void SetExcitement(Single level01)",
        }, members);
        Assert.Equal(8, members.Count);
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
    public void TheReactionMembersAreFoundWithExactSignaturesAndRunAMatchThroughTheDelegatesABinderCreates()
    {
        var kinds = Getter<int>("ReactionKinds");
        var claim = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), typeof(SpectatorCrowd).GetMethod("ClaimReactions", Declared, null, new[] { typeof(string) }, null)!);
        var react = (Func<int, int, float, bool>)Delegate.CreateDelegate(typeof(Func<int, int, float, bool>),
            typeof(SpectatorCrowd).GetMethod("React", Declared, null, new[] { typeof(int), typeof(int), typeof(float) }, null)!);
        var release = (Func<int, bool>)Delegate.CreateDelegate(typeof(Func<int, bool>), typeof(SpectatorCrowd).GetMethod("ReleaseReactions", Declared, null, new[] { typeof(int) }, null)!);

        Assert.Equal(0, kinds());                                // a menu, a vanilla map, Iceberg or a package without reaction clips: nothing to claim
        Assert.Equal(0, claim("ArenaMode"));
        Assert.False(react(1, 1, 0.5f));
        SpectatorCrowd.Reactions.SetKinds(62);                   // the Unity side switches this when the crowd and its reaction set exist
        Assert.Equal(62, kinds());
        Assert.Equal(0, claim(null!));
        Assert.Equal(0, claim(" "));
        int handle = claim("ArenaMode");
        Assert.True(handle > 0);
        Assert.Equal(handle, claim("ArenaMode"));
        Assert.Equal(0, claim("another mod"));
        Assert.True(react(handle, 1, 0.8f));
        Assert.False(react(handle, 6, 0.8f));                    // Lull is reserved and not in the mask
        Assert.False(react(handle, 32, 0.8f));
        Assert.False(react(handle, 1, float.NaN));
        Assert.False(react(handle, 1, 0.01f));
        Assert.False(react(handle + 1, 1, 0.8f));                // not the holder
        var drained = new CrowdReactionRequest[16];
        Assert.Equal(1, SpectatorCrowd.Reactions.Drain(drained, System.Diagnostics.Stopwatch.GetTimestamp()));
        Assert.Equal((1, 0.8f, false), (drained[0].Kind, drained[0].Strength, drained[0].Directed));   // the public contract never sets the line-of-fire flag
        Assert.True(release(handle));
        Assert.False(release(handle));
        Assert.False(react(handle, 1, 0.8f));
        Assert.Equal(62, kinds());                               // releasing does not end the crowd
        SpectatorCrowd.Reactions.Reset();                        // the crowd is removed with the raid
        Assert.Equal(0, kinds());
        Assert.Equal(0, claim("ArenaMode"));
    }

    [Fact]
    public void TheReactionsWorkWithTheBedSwitchedOffAndTheBedMembersAreUnchangedByThem()
    {
        SpectatorCrowd.Reactions.SetKinds(62);
        Assert.False(SpectatorCrowd.IsAvailable);                // no bed: IsAvailable keeps its meaning
        int handle = SpectatorCrowd.ClaimReactions("ArenaMode");
        Assert.True(SpectatorCrowd.React(handle, 4, 1f));
        SpectatorCrowd.SetExcitement(0.6f);
        Assert.Equal(0.6f, SpectatorCrowd.Excitement);
        Assert.False(SpectatorCrowd.IsAvailable);
        Assert.Equal(1, SpectatorCrowd.ContractVersion);         // new members do not raise it
    }

    [Fact]
    public void AReleasedOrVoidedHandleNeverMatchesALaterClaim()
    {
        SpectatorCrowd.Reactions.SetKinds(62);
        int first = SpectatorCrowd.ClaimReactions("ArenaMode");
        SpectatorCrowd.Reactions.Reset();
        SpectatorCrowd.Reactions.SetKinds(62);
        int second = SpectatorCrowd.ClaimReactions("ArenaMode");
        Assert.True(first > 0 && second > 0);
        Assert.NotEqual(first, second);
        Assert.False(SpectatorCrowd.React(first, 1, 1f));        // a handle of the raid before
        Assert.True(SpectatorCrowd.React(second, 1, 1f));
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
                _ = SpectatorCrowd.ReactionKinds;
                int handle = SpectatorCrowd.ClaimReactions("thread " + t);
                SpectatorCrowd.React(handle, 1 + i % 5, (i % 101) / 100f);
                SpectatorCrowd.React(i, i, float.NaN);
                if (i % 997 == 0) SpectatorCrowd.ReleaseReactions(handle);
            }
        })).ToArray();
        SpectatorCrowd.Reactions.SetKinds(62);
        await Task.WhenAll(tasks);
        // a broken state never escapes through the contract
        SpectatorCrowd.State = null!;
        Assert.False(SpectatorCrowd.IsAvailable);
        Assert.Equal(0f, SpectatorCrowd.Excitement);
        SpectatorCrowd.SetExcitement(0.5f);
        SpectatorCrowd.Reactions = null!;
        Assert.Equal(0, SpectatorCrowd.ReactionKinds);
        Assert.Equal(0, SpectatorCrowd.ClaimReactions("ArenaMode"));
        Assert.False(SpectatorCrowd.React(1, 1, 0.5f));
        Assert.False(SpectatorCrowd.ReleaseReactions(1));
    }
}
