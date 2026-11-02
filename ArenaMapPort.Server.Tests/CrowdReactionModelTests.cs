using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ArenaMapPort.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>Builds synthetic reaction sets in the shape the extractor writes (names and tags only, never a transcript): clips with a pool tag, a voice family and a sentence group, and
/// the banks that list them. The numbers of <see cref="PhaseA"/> are the pool sizes of the phase-A export (docs/PLAN-CROWD-REACTIONS.md 2.4).</summary>
internal sealed class ReactionSetBuilder
{
    private readonly List<SpectatorReactionClip> _clips = new();
    private readonly Dictionary<string, int> _serial = new();
    private readonly Dictionary<string, int> _lastGroup = new();

    internal static string Prefix(string tag) => SpectatorReactionTags.GroupPrefix(tag)!;

    internal ReactionSetBuilder Add(string tag, int family, int group, int frames = 88200)
    {
        string prefix = Prefix(tag);
        string category = prefix switch { "approve" => "kill_cheer", "disapprove" => "kill_angry", "start" => "match_start", "finish" => "match_end", _ => tag == "panic" ? "shoot_hide" : "aim_hide" };
        string stem = $"arena_fan{family}_{category}";
        int n = _serial.GetValueOrDefault(stem) + 1;
        _serial[stem] = n;
        string name = $"{stem}_{n:00}";
        _clips.Add(new SpectatorReactionClip
        {
            name = name, file = name + ".wav", channels = 1, sample_rate = 44100, frames = frames, bytes = 44 + frames * 2,
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(name))).ToLowerInvariant(), pool = tag, group = $"{prefix}-{group:00}",
        });
        return this;
    }

    /// <summary>Adds <c>count</c> clips per family to a pool and spreads them over <paramref name="groups"/> NEW sentence groups of the kind (round robin).</summary>
    internal ReactionSetBuilder Pool(string tag, (int Family, int Count)[] families, int groups)
    {
        string prefix = Prefix(tag);
        int first = _lastGroup.GetValueOrDefault(prefix) + 1;
        _lastGroup[prefix] = first + groups - 1;
        int i = 0;
        foreach (var (family, count) in families)
            for (int c = 0; c < count; c++)
                Add(tag, family, first + i++ % groups);
        return this;
    }

    internal SpectatorReactionSet Build()
    {
        var banks = new List<SpectatorReactionBank>();
        foreach (string bank in SpectatorReactionTags.BankNames)
        {
            var names = _clips.Where(c => SpectatorReactionTags.BankAllows(bank, c.pool)).Select(c => c.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            // Owned is the same event as Cheer: the builder lists every approve clip under Cheer only.
            if (bank == "Owned" || names.Length == 0) continue;
            banks.Add(new SpectatorReactionBank { bank = bank, trigger = SpectatorReactionTags.TriggerOf(bank), embedded = names.Length + 1, clips = names });
        }
        return new SpectatorReactionSet
        {
            schema = 1, max_frames = 308700, source_bundle = "StreamingAssets/Windows/assets/content/audio/phrases/fan_speaker_voice.bundle",
            banks = banks.ToArray(), clips = _clips.OrderBy(c => c.name, StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>The phase-A pools: Approve = praise 6 + gloat 4 (normal) and shout 2 (strong only), Disapprove = lament 10 + heckle 4, Start 6 (four families, five sentences), Finish 9,
    /// TakeCover = panic 12 (six sentences) + directed 17.</summary>
    internal static ReactionSetBuilder PhaseABuilder() => new ReactionSetBuilder()
        .Pool("praise", new[] { (8, 2), (11, 1), (12, 1), (13, 2) }, 5)
        .Pool("gloat", new[] { (7, 1), (12, 2), (15, 1) }, 4)
        .Pool("shout", new[] { (12, 1), (13, 1) }, 2)
        .Pool("lament", new[] { (7, 1), (11, 2), (12, 4), (13, 3) }, 9)
        .Pool("heckle", new[] { (12, 2), (13, 1), (15, 1) }, 3)
        .Pool("start", new[] { (7, 1), (11, 1), (12, 3), (13, 1) }, 5)
        .Pool("finish", new[] { (7, 1), (8, 1), (9, 1), (12, 4), (13, 2) }, 9)
        .Pool("panic", new[] { (7, 1), (8, 1), (9, 1), (11, 1), (12, 3), (13, 3), (15, 2) }, 6)
        .Pool("directed", new[] { (9, 3), (10, 1), (11, 1), (12, 7), (13, 5) }, 12);

    internal static SpectatorReactionSet PhaseA() => PhaseABuilder().Build();
}

/// <summary>
/// The Unity-free reaction model (docs/PLAN-CROWD-REACTIONS.md 4.3 to 4.5): the kind table, the claim and the request ring behind the four reaction members of
/// <c>SpectatorCrowd</c>, the clip pool of the manifest's reaction set, the planner (coalescing, priority, cooldowns, the global rate, voices from the strength, the clip choice and its
/// relaxation order) and the speaker helper. The synthetic sets are the tests' own; a real manifest is read when the extractor has written one.
/// </summary>
public sealed class CrowdReactionModelTests
{
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static CrowdReactionRequest Req(int kind, float strength, double at, bool directed = false) =>
        new CrowdReactionRequest { Kind = kind, Strength = strength, Directed = directed, Stamp = Ticks(at) };

    private static void Offer(CrowdReactionPlanner p, double now, params CrowdReactionRequest[] requests) => p.Offer(requests, requests.Length, Ticks(now), now);

    private static void Offer(CrowdReactionPlanner p, double now, int kind, float strength, bool directed = false) => Offer(p, now, Req(kind, strength, now, directed));

    /// <summary>Plans the next reaction, which must be due, and confirms it as started.</summary>
    private static CrowdReactionPlan Start(CrowdReactionPlanner p, double now, int free = 3, ICollection<int>? busy = null)
    {
        Assert.True(p.TryPlan(now, free, busy, out var plan), "a reaction was due");
        p.Started(plan, now);
        return plan;
    }

    private static List<CrowdReactionDrop> Drops(CrowdReactionPlanner p)
    {
        var list = new List<CrowdReactionDrop>();
        while (p.TryTakeDrop(out var d)) list.Add(d);
        return list;
    }

    private static CrowdReactionPlanner Planner(int seed = 1, float gapScale = 1f, SpectatorReactionSet? set = null) =>
        new CrowdReactionPlanner(set ?? ReactionSetBuilder.PhaseA(), seed) { GapScale = gapScale };

    private static CrowdReactionQueue ReadyQueue(int kinds = 62) { var q = new CrowdReactionQueue(); q.SetKinds(kinds); return q; }

    // ---- the kind table ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheKindNumbersTheirNamesAndTheMaskArePinned()
    {
        Assert.Equal((1, 2, 3, 4, 5, 6, 7, 31), (CrowdReactionKind.Approve, CrowdReactionKind.Disapprove, CrowdReactionKind.TakeCover, CrowdReactionKind.Start,
            CrowdReactionKind.Finish, CrowdReactionKind.Lull, CrowdReactionKind.PurifiedTeam, CrowdReactionKind.MaxKind));
        Assert.Equal(62, CrowdReactionKind.ImplementedMask);                     // bits 1 to 5: Lull and PurifiedTeam are reserved
        Assert.Equal(new[] { "Approve", "Disapprove", "TakeCover", "Start", "Finish", "Lull", "PurifiedTeam" }, Enumerable.Range(1, 7).Select(CrowdReactionKind.Name));
        Assert.Equal("kind0", CrowdReactionKind.Name(0));
        Assert.Equal("kind-1", CrowdReactionKind.Name(-1));
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 2)]
    [InlineData(5, true, 32)]
    [InlineData(30, true, 1 << 30)]
    [InlineData(31, true, int.MinValue)]
    [InlineData(32, false, 0)]
    [InlineData(33, false, 0)]
    [InlineData(-1, false, 0)]
    [InlineData(int.MinValue, false, 0)]
    [InlineData(int.MaxValue, false, 0)]
    public void ARangeCheckComesBeforeTheShiftSoNoKindOutsideOneToThirtyOneEverAliases(int kind, bool inRange, int bit)
    {
        Assert.Equal(inRange, CrowdReactionKind.InRange(kind));
        Assert.Equal(bit, CrowdReactionKind.Bit(kind));      // C# masks a shift count to 5 bits: 1 << 32 would be 1, 1 << 33 would be 2
    }

    [Fact]
    public void TheKindToBankTableIsPinned()
    {
        Assert.Equal(new[] { "Cheer", "Owned" }, CrowdReactionKind.Banks(1));
        Assert.Equal(new[] { "Angry" }, CrowdReactionKind.Banks(2));
        Assert.Equal(new[] { "Hide" }, CrowdReactionKind.Banks(3));
        Assert.Equal(new[] { "ChangedRound" }, CrowdReactionKind.Banks(4));
        Assert.Equal(new[] { "EndRound" }, CrowdReactionKind.Banks(5));
        foreach (int kind in new[] { 0, 6, 7, 8, 31, 32, -1 }) Assert.Empty(CrowdReactionKind.Banks(kind));
        // the same table seen from the manifest: every phase-A bank belongs to exactly one kind, through the sentence-group prefix
        foreach (int kind in Enumerable.Range(1, 5))
        {
            Assert.Equal(kind, CrowdReactionKind.OfGroupPrefix(CrowdReactionKind.GroupPrefix(kind)));
            foreach (string bank in CrowdReactionKind.Banks(kind))
                Assert.True(SpectatorReactionTags.BankAllows(bank, TagOf(kind)), bank);
        }
        Assert.Equal(0, CrowdReactionKind.OfGroupPrefix("lull"));
        Assert.Null(CrowdReactionKind.GroupPrefix(6));
    }

    private static string TagOf(int kind) => kind switch { 1 => "praise", 2 => "lament", 3 => "panic", 4 => "start", _ => "finish" };

    // ---- claim and handle ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AClaimNeedsAnOwnerNameAndACrowdThatCanReact()
    {
        var q = new CrowdReactionQueue();
        Assert.Equal(0, q.Kinds);
        Assert.Equal(0, q.Claim("plugin"));                  // no crowd, no reaction set, or switched off: nothing to claim
        q.SetKinds(62);
        foreach (string? blank in new[] { null, "", "   ", "\t\n" }) Assert.Equal(0, q.Claim(blank!));
        int handle = q.Claim("plugin");
        Assert.True(handle > 0);
        Assert.Equal(handle, q.Claim("plugin"));             // the same owner gets the same handle back
        Assert.Equal(handle, q.Claim("  plugin  "));         // the name is cleaned before it is compared
        Assert.Equal("plugin", q.HolderOwner);
        Assert.True(q.ExternalClaimed);
        Assert.Equal(0, q.Claim("another plugin"));          // any other holder refuses a different owner
        Assert.True(q.Release(handle));
        Assert.False(q.Release(handle));
        Assert.Null(q.HolderOwner);
        int next = q.Claim("another plugin");
        Assert.True(next > 0);
        Assert.NotEqual(handle, next);
    }

    [Fact]
    public void TheOwnerNameIsCleanedLikeTheScoreMonitorsAndCut()
    {
        Assert.Null(CrowdReactionQueue.CleanOwner(null!));
        Assert.Equal("ArenaMode", CrowdReactionQueue.CleanOwner("  ArenaMode "));
        Assert.Equal("a?b", CrowdReactionQueue.CleanOwner("a" + (char)0xE4 + "b"));
        Assert.Equal(64, CrowdReactionQueue.CleanOwner(new string('x', 500))!.Length);
    }

    [Fact]
    public void HandlesAreUniqueForTheProcessPositiveAndNeverReused()
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < 50; i++)
        {
            var q = ReadyQueue();
            int h = q.Claim("owner");
            Assert.True(h > 0);
            Assert.True(seen.Add(h), "a handle of another queue or an earlier claim came back");
            Assert.True(q.Release(h));
            int again = q.Claim("owner");
            Assert.True(again > 0);
            Assert.True(seen.Add(again));
        }
    }

    [Fact]
    public void TheDeveloperDemoYieldsToAnyOtherOwnerAndNeverTakesTheReactionsFromOne()
    {
        var q = ReadyQueue();
        int demo = q.Claim(CrowdReactionQueue.DemoOwner);
        Assert.True(demo > 0);
        Assert.Equal(demo, q.Claim(CrowdReactionQueue.DemoOwner));
        Assert.False(q.ExternalClaimed);                     // the plugin's own sensors do not stand down for the demo
        Assert.True(q.React(demo, 1, 0.5f));
        int plugin = q.Claim("ArenaMode");                   // yields: the plugin takes over
        Assert.True(plugin > 0);
        Assert.NotEqual(demo, plugin);
        Assert.True(q.ExternalClaimed);
        Assert.False(q.React(demo, 1, 0.5f));                // the demo's handle is stale now
        Assert.False(q.Release(demo));
        Assert.True(q.React(plugin, 1, 0.5f));
        Assert.Equal(0, q.Claim(CrowdReactionQueue.DemoOwner));   // a demo claim while a plugin holds
        Assert.True(q.Release(plugin));
        int demoAgain = q.Claim(CrowdReactionQueue.DemoOwner);    // free again
        Assert.True(demoAgain > 0);
        Assert.NotEqual(demo, demoAgain);
    }

    [Fact]
    public void AStaleOrForeignHandleIsRefused()
    {
        var q = ReadyQueue();
        var other = ReadyQueue();
        int mine = q.Claim("plugin");
        int theirs = other.Claim("plugin");
        Assert.False(q.React(theirs, 1, 0.5f));              // issued by another queue
        Assert.False(q.React(mine + 1000, 1, 0.5f));
        Assert.False(q.React(0, 1, 0.5f));
        Assert.False(q.React(-1, 1, 0.5f));
        Assert.False(q.Release(0));
        Assert.False(q.Release(-5));
        Assert.False(q.Release(theirs));
        Assert.True(q.React(mine, 1, 0.5f));
        Assert.True(q.Release(mine));
        Assert.False(q.React(mine, 1, 0.5f));                // released
    }

    [Fact]
    public void ResetVoidsEveryHandleEmptiesTheRingAndClearsTheKinds()
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        Assert.True(q.React(handle, 1, 0.5f));
        int generation = q.Generation;
        q.Reset();                                           // the crowd was removed
        Assert.Equal(generation + 1, q.Generation);
        Assert.Equal(0, q.Kinds);
        Assert.Equal(0, q.Count);
        Assert.Null(q.HolderOwner);
        Assert.False(q.React(handle, 1, 0.5f));
        Assert.False(q.Release(handle));
        Assert.Equal(0, q.Claim("plugin"));                  // no crowd any more
        q.SetKinds(62);                                      // the next raid's crowd
        int next = q.Claim("plugin");
        Assert.True(next > 0);
        Assert.NotEqual(handle, next);
        Assert.False(q.React(handle, 1, 0.5f));              // the old handle stays void even for the same owner
        Assert.True(q.React(next, 1, 0.5f));
    }

    [Fact]
    public void SwitchingTheKindsOffEmptiesTheRingAndBitZeroIsNotAKind()
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        Assert.True(q.React(handle, 2, 0.5f));
        q.SetKinds(0);
        Assert.Equal(0, q.Count);
        Assert.False(q.React(handle, 2, 0.5f));
        q.SetKinds(-1);                                      // every bit
        Assert.Equal(-2, q.Kinds);                           // all but bit 0
        Assert.True(q.React(handle, 31, 0.5f));
        Assert.False(q.React(handle, 0, 0.5f));
    }

    [Fact]
    public void NoClaimMeansNoKindsEvenWithAFullMask()
    {
        var q = ReadyQueue();
        Assert.False(q.React(1, 1, 0.5f));                   // nobody holds the reactions
        Assert.Equal(0, q.Count);
    }

    // ---- React ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(6)]                                          // reserved: not in the mask
    [InlineData(7)]
    public void AKindOutsideTheRangeOrTheMaskIsRefusedWithoutShifting(int kind)
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        Assert.False(q.React(handle, kind, 0.5f));
        Assert.Equal(0, q.Count);
        Assert.Equal(1, q.Refused);
    }

    [Fact]
    public void ABitThatIsNotInTheMaskIsRefusedWhileThoseInItAreAccepted()
    {
        var q = ReadyQueue(CrowdReactionKind.Bit(1) | CrowdReactionKind.Bit(4));
        int handle = q.Claim("plugin");
        foreach (int kind in Enumerable.Range(0, 34)) Assert.Equal(kind == 1 || kind == 4, q.React(handle, kind, 0.5f));
        Assert.Equal(2, q.Accepted);
    }

    [Fact]
    public void NaNAndAStrengthBelowFiveHundredthsAreRefusedAndAHigherOneIsClampedToOne()
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        foreach (float weak in new[] { float.NaN, float.NegativeInfinity, -1f, 0f, 0.049999f })
            Assert.False(q.React(handle, 1, weak));
        Assert.Equal(0, q.Count);
        Assert.True(q.React(handle, 1, 0.05f));
        Assert.True(q.React(handle, 1, 7f));
        Assert.True(q.React(handle, 1, float.PositiveInfinity));
        var buffer = new CrowdReactionRequest[16];
        Assert.Equal(3, q.Drain(buffer, Stopwatch.GetTimestamp()));
        Assert.Equal(new[] { 0.05f, 1f, 1f }, buffer.Take(3).Select(r => r.Strength));
        Assert.Equal(5, q.Refused);
    }

    [Fact]
    public void ARequestKeepsItsKindStrengthDirectionAndStampInArrivalOrder()
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        Assert.True(q.React(handle, 3, 0.4f, true, 111));
        Assert.True(q.React(handle, 1, 0.9f, false, 222));
        var buffer = new CrowdReactionRequest[16];
        Assert.Equal(2, q.Drain(buffer, 300));
        Assert.Equal((3, 0.4f, true, 111L), (buffer[0].Kind, buffer[0].Strength, buffer[0].Directed, buffer[0].Stamp));
        Assert.Equal((1, 0.9f, false, 222L), (buffer[1].Kind, buffer[1].Strength, buffer[1].Directed, buffer[1].Stamp));
        Assert.Equal(0, q.Drain(buffer, 300));
    }

    [Fact]
    public void ASeventeenthRequestEvictsTheOldestAndCountsIt()
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        for (int i = 1; i <= 16; i++) Assert.True(q.React(handle, 1, 0.5f, false, i));
        Assert.Equal(16, q.Count);
        Assert.Equal(0, q.Overflowed);
        Assert.True(q.React(handle, 2, 0.6f, false, 17));
        Assert.Equal(16, q.Count);
        Assert.Equal(1, q.Overflowed);
        Assert.Equal(17, q.Accepted);
        var buffer = new CrowdReactionRequest[32];
        Assert.Equal(16, q.Drain(buffer, 100));
        Assert.Equal(Enumerable.Range(2, 16).Select(i => (long)i), buffer.Take(16).Select(r => r.Stamp));   // stamp 1 is gone, the order is kept
        Assert.Equal(2, buffer[15].Kind);
        for (int i = 0; i < 40; i++) q.React(handle, 1, 0.5f, false, 1000 + i);                            // the ring wraps around many times
        Assert.Equal(16, q.Count);
        Assert.Equal(1 + 24, q.Overflowed);
        Assert.Equal(16, q.Drain(buffer, 2000));
        Assert.Equal(1024L, buffer[0].Stamp);
        Assert.Equal(1039L, buffer[15].Stamp);
    }

    [Fact]
    public void AnEntryOlderThanTwoSecondsIsDroppedAtTheDrain()
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        long t0 = Ticks(10);
        Assert.True(q.React(handle, 1, 0.5f, false, t0));
        Assert.True(q.React(handle, 2, 0.5f, false, t0 + Ticks(1.0)));
        Assert.True(q.React(handle, 4, 0.5f, false, t0 + Ticks(2.4)));
        var buffer = new CrowdReactionRequest[16];
        int n = q.Drain(buffer, t0 + Ticks(2.5));
        Assert.Equal(2, n);                                  // 2.5 s old: stale; 1.5 s and 0.1 s old: kept
        Assert.Equal(new[] { 2, 4 }, buffer.Take(n).Select(r => r.Kind));
        Assert.Equal(1, q.Stale);
        Assert.True(q.React(handle, 1, 0.5f, false, t0));
        Assert.Equal(1, q.Drain(buffer, t0 + Ticks(1.99)));  // just inside
        Assert.Equal(1, q.Stale);
        Assert.True(q.React(handle, 1, 0.5f, false, t0 + Ticks(5)));
        Assert.Equal(1, q.Drain(buffer, t0));                // a stamp from the future is not stale
    }

    [Fact]
    public void ADrainIntoASmallBufferLeavesTheRestQueued()
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        for (int i = 0; i < 5; i++) q.React(handle, 1, 0.5f, false, i);
        var buffer = new CrowdReactionRequest[2];
        Assert.Equal(2, q.Drain(buffer, 10));
        Assert.Equal(3, q.Count);
        Assert.Equal(2, q.Drain(buffer, 10));
        Assert.Equal(1, q.Drain(buffer, 10));
        Assert.Equal(0, q.Drain(null!, 10));
        Assert.Equal(0, q.Drain(new CrowdReactionRequest[0], 10));
    }

    [Fact]
    public async Task ManyThreadsReactWhileTheMainThreadDrainsWithoutAnExceptionAndWithConsistentCounters()
    {
        var q = ReadyQueue();
        int handle = q.Claim("stress");
        const int threads = 8, perThread = 25000;
        long refusedByCaller = 0;
        long drained = 0;
        bool stop = false;
        var buffer = new CrowdReactionRequest[CrowdReactionQueue.Capacity];
        var drainer = Task.Run(() => { while (!Volatile.Read(ref stop)) drained += q.Drain(buffer, Stopwatch.GetTimestamp()); });
        var reactors = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            long refused = 0;
            for (int i = 0; i < perThread; i++)
            {
                int kind = (i + t) % 9;                          // 0, 6, 7 and 8 are refused (not in the mask)
                float strength = (i % 7) / 6f;                   // 0 is refused (below 0.05)
                bool accepted = q.React(handle, kind, strength);
                if (!accepted) refused++;
                if (i % 5000 == 0) { q.Release(handle + 123456); _ = q.Kinds; _ = q.Claim("stress"); }   // foreign calls in between: no effect on the holder
            }
            Interlocked.Add(ref refusedByCaller, refused);
        })).ToArray();
        await Task.WhenAll(reactors);
        Volatile.Write(ref stop, true);
        await drainer;
        int n;
        while ((n = q.Drain(buffer, Stopwatch.GetTimestamp())) > 0) drained += n;
        Assert.Equal((long)threads * perThread, q.Accepted + q.Refused);
        Assert.Equal(refusedByCaller, q.Refused);
        Assert.Equal(q.Accepted, drained + q.Stale + q.Overflowed);     // every accepted request was drained, dropped as stale or evicted
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void ReactAndDrainAllocateNothing()
    {
        var q = ReadyQueue();
        int handle = q.Claim("plugin");
        var buffer = new CrowdReactionRequest[16];
        ReactAndDrain(q, handle, buffer, 2000);                  // warm: the JIT and the first calls may allocate once
        long before = GC.GetAllocatedBytesForCurrentThread();
        ReactAndDrain(q, handle, buffer, 20000);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, AllocationNoise);     // one allocation per call would be hundreds of kilobytes
    }

    // Allocation tests measure inside a fully optimised helper (the pattern of NoiseProbeModelTests.AddProps): a hot loop in a tier-0 test method can be replaced on the stack (OSR)
    // mid-measurement, and that JIT work is charged to this thread's counter, which is not the code under test allocating.
    private const long AllocationNoise = 512;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ReactAndDrain(CrowdReactionQueue q, int handle, CrowdReactionRequest[] buffer, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            q.React(handle, 1 + i % 5, 0.5f);
            q.React(handle, 99, 0.5f);                           // a refusal
            q.React(handle + 1, 1, 0.5f);
            if (i % 8 == 0) q.Drain(buffer, Stopwatch.GetTimestamp());
        }
    }

    // ---- the pool ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePoolSortsTheClipsOfAPhaseASetIntoTheirKindsFamiliesAndSentences()
    {
        var set = ReactionSetBuilder.PhaseA();
        set.Validate();
        var pool = new CrowdReactionPool(set);
        Assert.Equal(62, pool.KindsMask);
        Assert.Equal(set.clips.Length, pool.Slots);
        Assert.Equal(new Dictionary<string, int>
        {
            ["praise"] = 6, ["gloat"] = 4, ["shout"] = 2, ["urge"] = 0, ["lament"] = 10, ["heckle"] = 4, ["start"] = 6, ["finish"] = 9, ["panic"] = 12, ["directed"] = 17,
        }, new[] { "praise", "gloat", "shout", "urge", "lament", "heckle", "start", "finish", "panic", "directed" }.ToDictionary(t => t, pool.CountOf));
        Assert.Equal(new[] { 7, 11, 12, 13 }, pool.FamiliesOf(CrowdReactionKind.Start));
        Assert.Equal((11, 12, 18, 5, 9), (pool.GroupCountOf(1), pool.GroupCountOf(2), pool.GroupCountOf(3), pool.GroupCountOf(4), pool.GroupCountOf(5)));
        var clip = pool.At(0);
        Assert.NotNull(clip);
        Assert.Equal(CrowdReactionPool.FamilyOf(clip!.Name), clip.Family);
        Assert.Equal(2.0, clip.Seconds, 6);                  // 88200 frames at 44.1 kHz
        Assert.Null(pool.At(-1));
        Assert.Null(pool.At(set.clips.Length));
    }

    [Theory]
    [InlineData("arena_fan12_kill_cheer_03", 12)]
    [InlineData("arena_fan7_participating_01", 7)]
    [InlineData("arena_fan15_aim_hide_02", 15)]
    [InlineData("arena_fan_kill_cheer_03", -1)]
    [InlineData("arena_fan12", -1)]
    [InlineData("arena_fans_ambient_neutral", -1)]
    [InlineData("fan12_kill_cheer_03", -1)]
    [InlineData("", -1)]
    public void TheVoiceFamilyIsTheFanNumberOfTheClipName(string name, int family) => Assert.Equal(family, CrowdReactionPool.FamilyOf(name));

    [Fact]
    public void AKindIsImplementedOnlyWhenItsWeakestBandHasAClip()
    {
        int Mask(ReactionSetBuilder b) => new CrowdReactionPool(b.Build()).KindsMask;
        Assert.Equal(0, Mask(new ReactionSetBuilder()));
        Assert.Equal(CrowdReactionKind.Bit(1), Mask(new ReactionSetBuilder().Add("praise", 8, 1)));
        Assert.Equal(CrowdReactionKind.Bit(1), Mask(new ReactionSetBuilder().Add("gloat", 8, 1)));
        Assert.Equal(0, Mask(new ReactionSetBuilder().Add("shout", 8, 1).Add("urge", 9, 2)));        // strong-only clips cannot carry Approve
        Assert.Equal(0, Mask(new ReactionSetBuilder().Add("heckle", 8, 1)));                          // heckles come on top of laments
        Assert.Equal(CrowdReactionKind.Bit(2), Mask(new ReactionSetBuilder().Add("lament", 8, 1)));
        Assert.Equal(0, Mask(new ReactionSetBuilder().Add("directed", 8, 1)));
        Assert.Equal(CrowdReactionKind.Bit(3), Mask(new ReactionSetBuilder().Add("panic", 8, 1).Add("directed", 9, 2)));
        Assert.Equal(CrowdReactionKind.Bit(4) | CrowdReactionKind.Bit(5), Mask(new ReactionSetBuilder().Add("start", 8, 1).Add("finish", 9, 1)));
    }

    [Fact]
    public void AClipWithAnUnknownTagNoFamilyOrNoSentenceIsSkipped()
    {
        var set = ReactionSetBuilder.PhaseA();
        set.clips[0].pool = "sided";
        set.clips[1].name = "arena_participating";
        set.clips[2].group = null!;
        set.clips[3] = null!;
        var pool = new CrowdReactionPool(set);
        Assert.Null(pool.At(0));
        Assert.Null(pool.At(1));
        Assert.Null(pool.At(2));
        Assert.Null(pool.At(3));
        Assert.NotNull(pool.At(4));
        Assert.Throws<ArgumentNullException>(() => new CrowdReactionPool(null!));
        Assert.Throws<ArgumentNullException>(() => new CrowdReactionPlanner((CrowdReactionPool)null!, 1));
    }

    // ---- coalescing ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithinOneDrainTheLargestStrengthPerKindIsKept()
    {
        var p = Planner();
        Offer(p, 0, Req(1, 0.3f, 0), Req(1, 0.9f, 0), Req(1, 0.5f, 0), Req(3, 0.4f, 0), Req(3, 0.2f, 0));
        Assert.Equal(2, p.Pending);
        var first = Start(p, 0);
        Assert.Equal((1, 0.9f), (first.Kind, first.Strength));
        var second = Start(p, 1);
        Assert.Equal((3, 0.4f), (second.Kind, second.Strength));
        Assert.Equal(0, p.Pending);
    }

    [Fact]
    public void ARequestForAKindThatAlreadyWaitsIsMergedIntoItAndKeepsTheStrongerStrengthAndTheDirection()
    {
        var p = Planner();
        Offer(p, 0, 1, 0.3f);
        Offer(p, 0.1, 1, 0.9f);
        Offer(p, 0.2, 1, 0.5f);
        Assert.Equal(1, p.Pending);
        Assert.Equal(0.9f, Start(p, 0.3).Strength);
        Offer(p, 20, 3, 0.4f);
        Offer(p, 20.1, 3, 0.6f, directed: true);
        Offer(p, 20.2, 3, 0.2f);
        var plan = Start(p, 20.3);
        Assert.Equal((0.6f, true), (plan.Strength, plan.Directed));
    }

    [Fact]
    public void AStartOrAFinishRemovesTheApprovesAndDisapprovesOfItsBatchAndOfThePending()
    {
        var p = Planner();
        Offer(p, 0, Req(1, 0.5f, 0), Req(2, 0.5f, 0), Req(5, 1f, 0));
        Assert.Equal(1, p.Pending);
        Assert.True(p.IsPending(5));
        Assert.Equal(2, p.DroppedCount(CrowdReactionDropReason.Coalesced));
        Assert.Equal(new[] { 1, 2 }, Drops(p).Select(d => d.Kind));
        Start(p, 0);

        Offer(p, 10, 1, 0.5f);                               // waits
        Offer(p, 10, 2, 0.5f);
        Assert.Equal(2, p.Pending);
        Offer(p, 10.1, 4, 1f);                               // a later drain: the Start takes the waiting ones with it
        Assert.Equal(1, p.Pending);
        Assert.True(p.IsPending(4));
        Assert.Equal(4, p.DroppedCount(CrowdReactionDropReason.Coalesced));
        Assert.Equal(CrowdReactionKind.Start, Start(p, 10.1).Kind);

        Offer(p, 20, 5, 1f);                                 // the order matters: an Approve that arrives AFTER a Finish stays
        Offer(p, 20.1, 1, 0.5f);
        Assert.Equal(2, p.Pending);
    }

    [Fact]
    public void AFinishThatTheCooldownDropsDoesNotRemoveTheApprovesOfItsBatch()
    {
        var p = Planner();
        Offer(p, 0, 5, 1f);
        Start(p, 0);
        Drops(p);
        Offer(p, 1, Req(1, 0.5f, 1), Req(5, 1f, 1));         // the Finish is on cooldown (5 s): it is dropped and the cheer for the deciding kill stays
        Assert.Equal(new[] { (5, CrowdReactionDropReason.Cooldown) }, Drops(p).Select(d => (d.Kind, d.Reason)));
        Assert.Equal(1, p.Pending);
        Assert.True(p.IsPending(1));
    }

    // ---- priority and the pace -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, 4)]                                       // Finish before Start
    [InlineData(4, 1)]                                       // Start before Approve
    [InlineData(4, 2)]
    [InlineData(4, 3)]                                       // ... and TakeCover
    [InlineData(5, 3)]
    [InlineData(1, 3)]                                       // Approve before TakeCover
    [InlineData(2, 3)]                                       // Disapprove before TakeCover
    public void TheHigherPriorityGoesFirstWhateverTheArrivalOrder(int first, int second)
    {
        foreach (var order in new[] { new[] { first, second }, new[] { second, first } })
        {
            var p = Planner();
            // Start and Finish remove cheers and boos that are queued with them, so the request that is to survive them arrives in a later drain
            Offer(p, 0, order[0], 0.5f);
            Offer(p, 0.1, order[1], 0.5f);
            if (p.Pending != 2) { Assert.True(first is 4 or 5 && second is 1 or 2, "only a cheer or a boo may be taken by a Start or a Finish"); continue; }
            Assert.Equal(first, Start(p, 0.2).Kind);
            Assert.Equal(second, Start(p, 1.0).Kind);
        }
    }

    [Fact]
    public void FinishGoesBeforeStartAndTheyKeepEachOthersCheersGone()
    {
        var p = Planner();
        Offer(p, 0, Req(4, 1f, 0), Req(5, 1f, 0), Req(3, 0.5f, 0));
        Assert.Equal(3, p.Pending);
        Assert.Equal(new[] { 5, 4, 3 }, new[] { Start(p, 0).Kind, Start(p, 0.8).Kind, Start(p, 1.6).Kind });
    }

    [Fact]
    public void ApproveAndDisapproveShareARankAndTheOlderRequestGoesFirst()
    {
        foreach (var order in new[] { new[] { 1, 2 }, new[] { 2, 1 } })
        {
            var p = Planner();
            Offer(p, 0, Req(order[0], 0.5f, 0), Req(order[1], 0.5f, 0));
            Assert.Equal(order, new[] { Start(p, 0).Kind, Start(p, 1).Kind });
        }
        var q = Planner();
        Offer(q, 0, 2, 0.5f);                                // waits for the first slot... but nothing blocks, so a later request of the same rank queues behind
        Offer(q, 0.01, 1, 0.9f);
        Assert.Equal(2, Start(q, 0.02).Kind);
        Assert.Equal(1, Start(q, 1.0).Kind);
    }

    [Fact]
    public void ReservedAndUnknownKindsAreDroppedBecauseTheCrowdDoesNotPlayThem()
    {
        var p = Planner();
        Offer(p, 0, Req(6, 1f, 0), Req(7, 1f, 0), Req(0, 1f, 0), Req(32, 1f, 0), Req(-1, 1f, 0), Req(int.MinValue, 1f, 0), Req(int.MaxValue, 1f, 0), Req(9, 1f, 0));
        Assert.Equal(0, p.Pending);
        Assert.Equal(8, p.DroppedCount(CrowdReactionDropReason.NoPool));
        Assert.Equal(8, Drops(p).Count);
        Assert.False(p.IsPending(0));
        Assert.False(p.IsPending(32));
        Assert.False(p.IsPending(-1));
        Assert.False(p.TryPlan(0, 3, null, out _));
    }

    [Fact]
    public void AKindWithoutAPoolInThisSetIsDroppedAndTheOthersPlay()
    {
        var p = Planner(set: new ReactionSetBuilder().Pool("start", new[] { (7, 2), (8, 2) }, 3).Build());
        Assert.Equal(CrowdReactionKind.Bit(4), p.KindsMask);
        Offer(p, 0, Req(3, 1f, 0), Req(4, 1f, 0));
        Assert.Equal(1, p.Pending);
        Assert.True(p.IsPending(4));
        Assert.Equal(1, p.DroppedCount(CrowdReactionDropReason.NoPool));
    }

    [Fact]
    public void AWeakOrNaNStrengthThatReachesThePlannerIsDropped()
    {
        var p = Planner();
        Offer(p, 0, Req(1, 0.01f, 0), Req(2, float.NaN, 0), Req(3, -1f, 0));
        Assert.Equal(0, p.Pending);
        Assert.Equal(3, p.DroppedCount(CrowdReactionDropReason.Weak));
    }

    [Fact]
    public void AtMostOneReactionStartsPerThreeQuartersOfASecond()
    {
        var p = Planner();
        Offer(p, 0, Req(1, 0.5f, 0), Req(3, 0.5f, 0));
        Start(p, 0);
        Assert.False(p.TryPlan(0.5, 3, null, out _));
        Assert.False(p.TryPlan(0.7499, 3, null, out _));
        Assert.Equal(1, p.Pending);                          // the request waits, it is not dropped
        Assert.True(p.TryPlan(0.75, 3, null, out var plan));
        Assert.Equal(3, plan.Kind);
    }

    [Fact]
    public void ARequestThatWaitedTwoSecondsSinceItsReactCallIsDroppedAsStaleWithWhatItWaitedFor()
    {
        var p = Planner();
        Offer(p, 0, 1, 0.5f);
        Assert.False(p.TryPlan(1.99, 0, null, out _));       // no free slot
        Assert.Equal(1, p.Pending);
        Assert.False(p.TryPlan(2.01, 0, null, out _));
        Assert.Equal(0, p.Pending);
        var drop = Assert.Single(Drops(p));
        Assert.Equal((1, CrowdReactionDropReason.Stale, CrowdReactionDropReason.NoSlot), (drop.Kind, drop.Reason, drop.Blocker));

        // the clock starts at the React call, not at the drain: a request that was already 1.5 s old when it was drained has half a second left
        Offer(p, 10, Req(2, 0.5f, 8.5));
        Assert.False(p.TryPlan(10.4, 0, null, out _));
        Assert.Equal(1, p.Pending);
        Assert.False(p.TryPlan(10.6, 3, null, out _));
        Assert.Equal(0, p.Pending);

        // waiting for the global rate
        Offer(p, 20, Req(4, 1f, 20));
        Start(p, 20);
        Offer(p, 20.1, Req(3, 0.5f, 19));                    // one second old when drained: dies at 20.1 + 1.0
        Assert.False(p.TryPlan(20.5, 3, null, out _));
        Assert.False(p.TryPlan(21.2, 3, null, out _));
        Assert.Equal(CrowdReactionDropReason.Rate, Drops(p).Last(d => d.Reason == CrowdReactionDropReason.Stale).Blocker);
        Assert.Equal(0, p.Pending);
    }

    [Fact]
    public void ARequestThatStartsJustInsideTheTwoSecondsStillPlays()
    {
        var p = Planner();
        Offer(p, 0, 1, 0.5f);
        Assert.True(p.TryPlan(2.0, 3, null, out var plan));
        Assert.Equal(1, plan.Kind);
    }

    [Fact]
    public void AWeakLatecomerMergedIntoAnOldStrongRequestDoesNotStretchItsDeadline()
    {
        var p = Planner();
        Offer(p, 0, 1, 1f);                                  // strong, waits for a slot: it is due until 2.0
        Assert.False(p.TryPlan(0.1, 0, null, out _));
        Offer(p, 1.9, 1, 0.2f);                              // merged into it, but the strength (and the deadline) stay the old one's
        Assert.Equal(1, p.Pending);
        Assert.False(p.TryPlan(2.1, 3, null, out _));        // a slot is free at last: too late for the event that earned the strength
        Assert.Equal(0, p.Pending);
        var drop = Assert.Single(Drops(p));
        Assert.Equal((1, CrowdReactionDropReason.Stale, CrowdReactionDropReason.NoSlot), (drop.Kind, drop.Reason, drop.Blocker));
        Assert.Equal(1f, drop.Strength);
        Offer(p, 3, 1, 0.5f);                                // nothing of the old request is left to merge with
        Assert.Equal(0.5f, Start(p, 3).Strength);
    }

    [Fact]
    public void AFreshStrongRequestMergedIntoAnOldWeakOneBringsItsOwnDeadlineAndItsStrength()
    {
        var p = Planner();
        Offer(p, 0, 1, 0.2f);                                // weak, due until 2.0
        Offer(p, 1.9, 1, 1f);                                // strong and fresh: the merged request lives until 3.9
        Assert.Equal(1, p.Pending);
        Assert.True(p.TryPlan(2.1, 3, null, out var plan));
        Assert.Equal(1f, plan.Strength);
        Assert.Empty(Drops(p));

        // the same inside one drain: the strongest request of the batch decides, an older weaker one does not shorten it
        var q = Planner();
        Offer(q, 1.9, Req(2, 0.2f, 0), Req(2, 0.9f, 1.8));   // 1.9 s and 0.1 s old when drained
        Assert.False(q.TryPlan(2.5, 0, null, out _));
        Assert.Equal(1, q.Pending);                          // the weak one alone was due until 2.0; the strong one lives to 3.8
        Assert.True(q.TryPlan(3.7, 3, null, out var kept));
        Assert.Equal(0.9f, kept.Strength);
        // ... and an older strong one in the same batch keeps its own earlier deadline against a later weak one
        var r = Planner();
        Offer(r, 1.9, Req(2, 0.9f, 0), Req(2, 0.2f, 1.8));
        Assert.False(r.TryPlan(1.95, 0, null, out _));
        Assert.Equal(1, r.Pending);
        Assert.False(r.TryPlan(2.05, 3, null, out _));       // the strong request is 2.05 s old
        Assert.Equal(0, r.Pending);
        Assert.Equal(CrowdReactionDropReason.Stale, Assert.Single(Drops(r)).Reason);
    }

    [Fact]
    public void WithoutAFreeReactionSlotARequestWaitsAndThenTakesWhatIsFree()
    {
        var p = Planner();
        Offer(p, 0, 5, 1f);                                  // strength 1: three voices wanted
        Assert.False(p.TryPlan(0.1, 0, null, out _));
        Assert.False(p.TryPlan(0.2, -1, null, out _));
        Assert.Equal(1, p.Pending);
        Assert.True(p.TryPlan(0.3, 1, null, out var plan));
        Assert.Equal((1, 3), (plan.Voices, plan.Requested));
    }

    [Theory]
    [InlineData(0.05f, 1)]
    [InlineData(0.2f, 1)]
    [InlineData(1f / 3f, 1)]
    [InlineData(0.34f, 2)]
    [InlineData(0.5f, 2)]
    [InlineData(2f / 3f, 2)]
    [InlineData(0.67f, 3)]
    [InlineData(0.8f, 3)]
    [InlineData(1f, 3)]
    public void TheVoiceCountComesFromTheStrength(float strength, int voices)
    {
        Assert.Equal(voices, CrowdReactionPlanner.VoicesFor(strength));
        var p = Planner(set: ReactionSetBuilder.PhaseABuilder().Pool("finish", new[] { (7, 1), (8, 1), (9, 1), (10, 1) }, 4).Build());   // plenty of sentences
        Offer(p, 0, 5, strength);
        var plan = Start(p, 0);
        Assert.Equal((voices, voices), (plan.Voices, plan.Requested));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 2)]
    [InlineData(1, 1)]
    [InlineData(9, 3)]
    public void TheFreeSlotsCapTheVoices(int free, int voices)
    {
        var p = Planner();
        Offer(p, 0, 5, 1f);
        var plan = Start(p, 0, free);
        Assert.Equal((voices, 3), (plan.Voices, plan.Requested));
    }

    // ---- cooldowns -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 3.0)]
    [InlineData(2, 3.0)]
    [InlineData(3, 15.0)]
    [InlineData(4, 5.0)]
    [InlineData(5, 5.0)]
    public void ARequestOfAKindThatStartedLessThanItsGapAgoIsDroppedAsCooldown(int kind, double gap)
    {
        var p = Planner();
        Assert.Equal(gap, CrowdReactionPlanner.BaseGap(kind));
        Offer(p, 100, kind, 0.5f);
        Start(p, 100);
        Offer(p, 100 + gap - 0.01, kind, 0.5f);
        Assert.Equal(1, p.DroppedCount(CrowdReactionDropReason.Cooldown));
        Assert.Equal(0, p.Pending);
        Offer(p, 100 + gap, kind, 0.5f);
        Assert.Equal(1, p.DroppedCount(CrowdReactionDropReason.Cooldown));
        Assert.Equal(1, p.Pending);
    }

    [Theory]
    [InlineData(20.0, 6)]
    [InlineData(20.0, 7)]
    public void LullAndPurifiedTeamWouldWaitTwentySeconds(double gap, int kind) => Assert.Equal(gap, CrowdReactionPlanner.BaseGap(kind));

    [Fact]
    public void AStrengthOfZeroPointEightHalvesTheGapOfThatRequest()
    {
        var p = Planner();
        Assert.Equal(3.0, p.Gap(1, 0.79f));
        Assert.Equal(1.5, p.Gap(1, 0.8f));
        Assert.Equal(1.5, p.Gap(2, 1f));
        Assert.Equal(7.5, p.Gap(3, 0.9f));
        Assert.Equal(2.5, p.Gap(5, 0.8f));
        Offer(p, 0, 1, 0.5f);
        Start(p, 0);
        Offer(p, 1.4, 1, 0.79f);                             // 3 s apply
        Assert.Equal(1, p.DroppedCount(CrowdReactionDropReason.Cooldown));
        Offer(p, 1.4, 1, 0.8f);                              // 1.5 s apply: still inside
        Assert.Equal(2, p.DroppedCount(CrowdReactionDropReason.Cooldown));
        Offer(p, 1.5, 1, 0.8f);
        Assert.Equal(2, p.DroppedCount(CrowdReactionDropReason.Cooldown));
        Assert.Equal(1, p.Pending);
    }

    [Fact]
    public void TheGapScaleMultipliesTheGapsAndZeroSwitchesTheCooldownsOff()
    {
        var p = Planner(gapScale: 2f);
        Assert.Equal(6.0, p.Gap(1, 0.5f));
        Assert.Equal(30.0, p.Gap(3, 0.5f));
        p.GapScale = float.NaN; Assert.Equal(1f, p.GapScale);
        p.GapScale = 9f; Assert.Equal(CrowdReactionPlanner.MaxGapScale, p.GapScale);
        p.GapScale = -3f; Assert.Equal(0f, p.GapScale);
        p.GapScale = float.PositiveInfinity; Assert.Equal(CrowdReactionPlanner.MaxGapScale, p.GapScale);
        p.GapScale = 0f;
        Assert.Equal(0.0, p.Gap(3, 0.5f));
        for (int i = 0; i < 20; i++)                         // gap scale 0: no cooldown at all, only the pace of 0.75 s
        {
            Offer(p, i * 0.8, 3, 0.5f);
            Start(p, i * 0.8);
        }
        Assert.Equal(0, p.DroppedCount(CrowdReactionDropReason.Cooldown));
        var q = Planner(gapScale: 2f);
        Offer(q, 0, 1, 0.5f);
        Start(q, 0);
        Offer(q, 5.9, 1, 0.5f);
        Assert.Equal(1, q.DroppedCount(CrowdReactionDropReason.Cooldown));
        Offer(q, 6.0, 1, 0.5f);
        Assert.Equal(1, q.Pending);
    }

    [Fact]
    public void ADiscardedPlanStartsNoCooldownAndAStartedOneDoes()
    {
        var p = Planner();
        Offer(p, 0, 1, 0.5f);
        Assert.True(p.TryPlan(0, 3, null, out var plan));
        p.Discard(plan, CrowdReactionDropReason.NoSource);
        Assert.Equal(0, p.Pending);
        Assert.Equal(1, p.DroppedCount(CrowdReactionDropReason.NoSource));
        Offer(p, 0.1, 1, 0.5f);                              // not on cooldown: the voices never sounded
        Assert.Equal(1, p.Pending);
        Start(p, 0.1);
        Offer(p, 0.2, 1, 0.5f);
        Assert.Equal(1, p.DroppedCount(CrowdReactionDropReason.Cooldown));
        p.Discard(null!, CrowdReactionDropReason.Off);       // nothing to discard
        p.Started(null!, 1);
    }

    [Fact]
    public void OnlyTheVoicesThatReallyStartedEnterTheHistoryAndTheVoiceCount()
    {
        // Four sentences, one clip each. A three-voice Start plans three of them; only one speaker could be found, so only the first voice sounded.
        var set = new ReactionSetBuilder().Add("start", 7, 1).Add("start", 8, 2).Add("start", 9, 3).Add("start", 10, 4).Build();
        var heard = Planner(7, set: set);
        var all = Planner(7, set: set);
        Offer(heard, 0, 4, 1f); Offer(all, 0, 4, 1f);
        Assert.True(heard.TryPlan(0, 3, null, out var plan));
        Assert.True(all.TryPlan(0, 3, null, out var same));
        Assert.Equal(plan.Clips, same.Clips);                // the same seed plans the same reaction
        Assert.Equal(3, plan.Voices);
        heard.Started(plan, 0, 1);
        all.Started(same, 0);
        Assert.Equal((1, 1), (heard.StartedCount, heard.StartedVoices));
        Assert.Equal((1, 3), (all.StartedCount, all.StartedVoices));

        Offer(heard, 10, 4, 1f); Offer(all, 10, 4, 1f);
        var next = Start(heard, 10);
        var crowded = Start(all, 10);
        // the two sentences nobody heard are free again: all three voices find their place with every rule in force; had all three counted, the window would have to shrink
        Assert.Equal((3, 0), (next.Voices, next.Level));
        Assert.DoesNotContain(plan.Clips[0], next.Clips);    // the one that did sound is still out of the way
        Assert.Equal(3, crowded.Voices);
        Assert.True(crowded.Level > 0 && crowded.GroupsRelaxed);
        Assert.Equal(4, heard.StartedVoices);

        // the number is clamped to the plan: more than it has is all of it, nothing or less is the first voice
        var p = Planner(7, set: set);
        Offer(p, 0, 4, 1f);
        Assert.True(p.TryPlan(0, 3, null, out var big));
        p.Started(big, 0, 99);
        Assert.Equal(3, p.StartedVoices);
        Offer(p, 10, 4, 0.2f);
        Assert.True(p.TryPlan(10, 3, null, out var small));
        p.Started(small, 10, 0);
        Assert.Equal(4, p.StartedVoices);
        Offer(p, 20, 4, 0.2f);
        Assert.True(p.TryPlan(20, 3, null, out var negative));
        p.Started(negative, 20, -5);
        Assert.Equal(5, p.StartedVoices);
        p.Started(null!, 30, 2);                             // nothing to start
        Assert.Equal(3, p.StartedCount);
    }

    [Fact]
    public void APlanThatIsNeitherStartedNorDiscardedStaysPendingAndIsPlannedAgain()
    {
        var p = Planner();
        Offer(p, 0, 1, 0.5f);
        Assert.True(p.TryPlan(0.1, 3, null, out var first));
        Assert.True(p.TryPlan(0.2, 3, null, out var second));
        Assert.Equal((first.Kind, first.Strength), (second.Kind, second.Strength));
        Assert.Equal(1, p.Pending);
        Assert.False(p.TryPlan(2.1, 3, null, out _));        // it expires on its own
        Assert.Equal(0, p.Pending);
    }

    [Fact]
    public void ResetForgetsPendingRequestsCooldownsHistoryAndCountersButKeepsTheGapScale()
    {
        var p = Planner(gapScale: 2f);
        Offer(p, 0, 1, 0.5f);
        Start(p, 0);
        Offer(p, 1, 3, 0.5f);
        Offer(p, 1, 1, 0.5f);                                // cooldown drop
        p.Reset();
        Assert.Equal((0, 0, 0, 0), (p.Pending, p.StartedCount, p.StartedVoices, p.DroppedCount(CrowdReactionDropReason.Cooldown)));
        Assert.False(p.TryTakeDrop(out _));
        Assert.Equal(2f, p.GapScale);
        Offer(p, 1.1, 1, 0.5f);                              // neither on cooldown nor held back by the old rate
        Assert.True(p.TryPlan(1.1, 3, null, out _));
    }

    [Fact]
    public void TheDropRingKeepsTheNewestSixteenAndTheDescriptionNamesTheCounters()
    {
        var p = Planner();
        for (int i = 0; i < 20; i++) Offer(p, i, Req(0, 1f, i));
        Assert.Equal(20, p.DroppedCount(CrowdReactionDropReason.NoPool));
        Assert.Equal(CrowdReactionPlanner.DropCapacity, Drops(p).Count);
        Offer(p, 100, 1, 0.2f);
        Start(p, 100);
        Offer(p, 100.5, 1, 0.5f);
        Assert.Equal("started=1 (1 voices), pending=0, dropped cooldown=1 nopool=20", p.Describe());
        Assert.Equal("none", CrowdReactionDropReason.Name(0));
        Assert.Equal(new[] { "cooldown", "rate", "stale", "noslot", "nosource", "nolistener", "off", "notstarted", "nopool", "coalesced", "weak" },
            Enumerable.Range(1, 11).Select(CrowdReactionDropReason.Name));
    }

    [Fact]
    public void WhileNothingWaitsThePlannerAllocatesNothing()
    {
        var p = Planner();
        var buffer = new CrowdReactionRequest[16];
        IdleFrames(p, buffer, 2000);                         // warm: the JIT and the first calls may allocate once
        long before = GC.GetAllocatedBytesForCurrentThread();
        IdleFrames(p, buffer, 20000);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, AllocationNoise);     // one allocation per call would be hundreds of kilobytes
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void IdleFrames(CrowdReactionPlanner p, CrowdReactionRequest[] buffer, int frames)
    {
        for (int i = 0; i < frames; i++) { p.Offer(buffer, 0, 0, 0); p.TryPlan(i, 3, null, out _); p.TryTakeDrop(out _); }
    }

    // ---- the clip choice -----------------------------------------------------------------------------------------------------------------

    /// <summary>Plays reactions one after the other (a second apart, no cooldown) and returns the plans.</summary>
    private static List<CrowdReactionPlan> Run(CrowdReactionPlanner p, int count, int kind, float strength, bool directed = false, int free = 3, ICollection<int>? busy = null)
    {
        p.GapScale = 0f;
        var plans = new List<CrowdReactionPlan>();
        for (int i = 0; i < count; i++)
        {
            double now = 1000 + i * 1.0;
            Offer(p, now, kind, strength, directed);
            plans.Add(Start(p, now, free, busy));
        }
        return plans;
    }

    private static IEnumerable<ReactionClip> Voices(CrowdReactionPlanner p, CrowdReactionPlan plan) => plan.Clips.Select(i => p.Pool.At(i)!);

    [Fact]
    public void ApproveBelowZeroPointEightDrawsPraiseAndGloatForEveryVoiceAndNeverAShout()
    {
        var p = Planner(5);
        var plans = Run(p, 400, 1, 0.79f);
        var tags = plans.SelectMany(plan => Voices(p, plan)).Select(c => c.Tag).Distinct().OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "gloat", "praise" }, tags);
        Assert.True(plans.Count(plan => plan.Level == 0) > 300, "the rules hold for most reactions");
    }

    [Fact]
    public void ApproveFromZeroPointEightDrawsTheFirstVoiceFromTheShoutsAndTheOthersFromTheNormalClips()
    {
        var p = Planner(6);
        var plans = Run(p, 400, 1, 0.8f);
        Assert.All(plans, plan =>
        {
            var voices = Voices(p, plan).ToArray();
            Assert.Equal(3, voices.Length);
            Assert.Equal("shout", voices[0].Tag);
            Assert.All(voices.Skip(1), v => Assert.Contains(v.Tag, new[] { "praise", "gloat" }));
            Assert.False(plan.FellBack);                     // never the fallback on the normal clips: the other shout is always free
        });
        Assert.Equal(2, plans.Select(plan => p.Pool.At(plan.Clips[0])!.Name).Distinct().Count());   // both shouts take turns: one sentence is not repeated at once
    }

    [Fact]
    public void AnUrgeClipJoinsTheStrongFirstVoiceButIsNeverDrawnBelowZeroPointEight()
    {
        var set = ReactionSetBuilder.PhaseABuilder().Pool("urge", new[] { (9, 1), (10, 1) }, 2).Build();
        var p = Planner(7, set: set);
        var tags = Run(p, 600, 1, 1f).Select(plan => p.Pool.At(plan.Clips[0])!.Tag).Distinct().OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "shout", "urge" }, tags);
        var weak = Planner(8, set: set);
        Assert.DoesNotContain(Run(weak, 600, 1, 0.79f).SelectMany(plan => Voices(weak, plan)), c => c.Tag is "shout" or "urge");
    }

    [Fact]
    public void AStrongApprovesFirstVoiceGoesThroughTheSameStepsBeforeItEverTakesTheNormalClips()
    {
        // One shout only. After the first reaction its clip and its sentence are in the history, so the next one needs the relaxation steps; they run on the shout (the last-two-clips
        // rule, then the sentence window down to none) and the shout comes back before any normal clip is considered: the normal clips are the step after the last one.
        var set = new ReactionSetBuilder().Add("shout", 12, 1).Add("praise", 8, 2).Add("praise", 13, 3).Add("praise", 11, 4).Add("gloat", 12, 5).Build();
        var p = Planner(set: set);
        var plans = Run(p, 3, 1, 0.9f, free: 1);
        Assert.All(plans, plan =>
        {
            Assert.Equal("shout", p.Pool.At(plan.Clips[0])!.Tag);
            Assert.Equal(1, plan.Voices);
            Assert.False(plan.FellBack);
        });
        Assert.Equal((0, false, 3, 3), (plans[0].Level, plans[0].ClipsRelaxed, plans[0].GroupWindow, plans[0].FullGroupWindow));
        Assert.Equal("", plans[0].RelaxedText);
        // 5 sentences, so the full window is 3: step 1 drops the clips rule, steps 2 and 3 shrink the window to 2 and 1 (the shout's sentence is the latest, so it is still out), step 4 is window 0
        Assert.Equal((4, true, 0, 3, false), (plans[1].Level, plans[1].ClipsRelaxed, plans[1].GroupWindow, plans[1].FullGroupWindow, plans[1].FamiliesRelaxed));
        Assert.True(plans[1].GroupsRelaxed);
        Assert.Equal("last two clips, sentence window 0 of 3", plans[1].RelaxedText);
        Assert.Equal(plans[1].Level, plans[2].Level);
    }

    [Fact]
    public void DisapproveBelowZeroPointFourDrawsLamentsOnlyAndFromThereAlsoHeckles()
    {
        var p = Planner(9);
        var weak = Run(p, 500, 2, 0.39f);
        Assert.Equal(new[] { "lament" }, weak.SelectMany(plan => Voices(p, plan)).Select(c => c.Tag).Distinct());
        var q = Planner(9);
        var strong = Run(q, 500, 2, 0.4f).SelectMany(plan => Voices(q, plan)).Select(c => c.Tag).Distinct().OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "heckle", "lament" }, strong);
    }

    [Fact]
    public void TakeCoverDrawsPanicClipsAndTheDirectedPleasOnlyWhenTheRequestIsDirected()
    {
        var p = Planner(10);
        var plain = Run(p, 400, 3, 0.9f);
        Assert.Equal(new[] { "panic" }, plain.SelectMany(plan => Voices(p, plan)).Select(c => c.Tag).Distinct());
        Assert.All(plain, plan => Assert.False(plan.Directed));
        var q = Planner(10);
        var directed = Run(q, 400, 3, 0.9f, directed: true);
        Assert.All(directed, plan => Assert.True(plan.Directed));
        Assert.Equal(new[] { "directed", "panic" }, directed.SelectMany(plan => Voices(q, plan)).Select(c => c.Tag).Distinct().OrderBy(t => t));
    }

    [Fact]
    public void StartAndFinishDrawFromTheirOwnPoolOnly()
    {
        foreach (var (kind, tag) in new[] { (4, "start"), (5, "finish") })
        {
            var p = Planner(11);
            Assert.Equal(new[] { tag }, Run(p, 100, kind, 1f).SelectMany(plan => Voices(p, plan)).Select(c => c.Tag).Distinct());
        }
    }

    [Fact]
    public void TheFamilyIsDrawnWithTheSquareRootOfItsClipCountAndNotWithItsClipCount()
    {
        var builder = new ReactionSetBuilder();
        builder.Pool("finish", new[] { (7, 1), (8, 4), (9, 9) }, 14);      // weights 1 : 2 : 3, clip counts 1 : 4 : 9
        var p = new CrowdReactionPlanner(builder.Build(), 4242);
        var all = Enumerable.Range(0, p.Pool.Slots).Select(i => p.Pool.At(i)!).ToList();
        var counts = new Dictionary<int, int> { [7] = 0, [8] = 0, [9] = 0 };
        var cands = new List<ReactionClip>();
        const int draws = 60000;
        for (int i = 0; i < draws; i++)
        {
            cands.Clear(); cands.AddRange(all);
            counts[p.Draw(cands).Family]++;
            Assert.Equal(13, cands.Count);                  // the drawn clip is taken out
        }
        Assert.InRange(counts[7] / (double)draws, 1.0 / 6 - 0.01, 1.0 / 6 + 0.01);   // 16.7 %, a plain clip draw would give 7.1 %
        Assert.InRange(counts[8] / (double)draws, 2.0 / 6 - 0.01, 2.0 / 6 + 0.01);   // 33.3 %, a plain clip draw would give 28.6 %
        Assert.InRange(counts[9] / (double)draws, 3.0 / 6 - 0.01, 3.0 / 6 + 0.01);   // 50.0 %, a plain clip draw would give 64.3 %
    }

    [Fact]
    public void TheFamilyWeightingHoldsEndToEndThroughThePlanner()
    {
        var builder = new ReactionSetBuilder();
        builder.Pool("finish", new[] { (7, 4), (8, 16), (9, 36) }, 56);
        var p = new CrowdReactionPlanner(builder.Build(), 77);
        var plans = Run(p, 6000, 5, 0.1f);                  // one voice each
        var share = plans.GroupBy(plan => p.Pool.At(plan.Clips[0])!.Family).ToDictionary(g => g.Key, g => g.Count() / 6000.0);
        Assert.InRange(share[7], 1.0 / 6 - 0.03, 1.0 / 6 + 0.03);
        Assert.InRange(share[8], 1.0 / 3 - 0.03, 1.0 / 3 + 0.03);
        Assert.InRange(share[9], 1.0 / 2 - 0.03, 1.0 / 2 + 0.03);
    }

    [Theory]
    [InlineData(1, 1f)]
    [InlineData(4, 1f)]
    [InlineData(5, 1f)]
    [InlineData(2, 0.9f)]
    [InlineData(3, 1f)]
    public void WithinOneReactionNoSentenceIsSaidTwiceAndWhenEveryRuleHoldsNoVoiceFamilyEither(int kind, float strength)
    {
        var p = Planner(21 + kind);
        var plans = Run(p, 500, kind, strength);
        foreach (var plan in plans)
        {
            var voices = Voices(p, plan).ToArray();
            Assert.Equal(plan.Voices, voices.Select(v => v.Group).Distinct().Count());     // never relaxed
            if (!plan.FamiliesRelaxed) Assert.Equal(plan.Voices, voices.Select(v => v.Family).Distinct().Count());
        }
        if (kind == CrowdReactionKind.Start)
        {
            // five sentences, three voices, a window of three: after the first reaction only two sentences are outside the window, so no later one can keep every rule (the
            // pigeonhole that the shrinking window answers, ConsecutiveThreeVoiceStartsKeepAllThreeVoices...)
            Assert.Equal(1, plans.Count(plan => plan.Level == 0));
        }
        else
            Assert.True(plans.Count(plan => plan.Level == 0) > 100, "the rules hold for a good part of the reactions of a phase-A pool");
    }

    [Theory]
    [InlineData(1, 0.5f)]
    [InlineData(1, 1f)]
    [InlineData(2, 0.9f)]
    [InlineData(3, 1f)]
    [InlineData(4, 1f)]
    [InlineData(5, 1f)]
    [InlineData(5, 0.2f)]
    public void AReactionAvoidsTheLastTwoClipsAndTheRecentSentencesExactlyAsFarAsItSaysItRelaxedThem(int kind, float strength)
    {
        var p = Planner(31 + kind);
        var clips = new List<int>();
        var groups = new List<int>();
        foreach (var plan in Run(p, 500, kind, strength))
        {
            var voices = Voices(p, plan).ToArray();
            // what the plan says it relaxed is all it relaxed: the last two clips of the kind, and as many of the kind's latest sentences as its window says
            if (!plan.ClipsRelaxed) Assert.All(voices, v => Assert.DoesNotContain(v.Index, clips.TakeLast(2)));
            if (plan.GroupWindow > 0) Assert.All(voices, v => Assert.DoesNotContain(v.Group, groups.TakeLast(plan.GroupWindow)));
            // the order of the steps: no sentence window shrinks before the clips rule fell, no family rule falls before the window is gone, no fallback before the families
            Assert.Equal(plan.Level == 0, !plan.ClipsRelaxed && !plan.GroupsRelaxed && !plan.FamiliesRelaxed && !plan.FellBack);     // Level 0 is exactly the plan that relaxed nothing
            Assert.True(!plan.GroupsRelaxed || plan.ClipsRelaxed);
            Assert.True(!plan.FamiliesRelaxed || (plan.ClipsRelaxed && plan.GroupWindow == 0));
            Assert.True(!plan.FellBack || plan.FamiliesRelaxed);
            clips.AddRange(voices.Select(v => v.Index));
            groups.AddRange(voices.Select(v => v.Group));
        }
    }

    [Fact]
    public void TheHistoryIsPerKind()
    {
        var p = Planner(41);
        p.GapScale = 0f;
        Offer(p, 0, 5, 0.1f);
        var finishes = new List<int> { Start(p, 0).Clips[0] };
        for (int i = 1; i < 60; i++)
        {
            // A three-voice Disapprove between two Finishes: three clips and three sentences enter ITS history. One history shared by the kinds (two clips, three sentences) would be
            // flushed by it and the Finish before could come back at once; a history per kind is not touched.
            Offer(p, i * 2.0, 2, 1f);
            Assert.Equal(3, Start(p, i * 2.0).Voices);
            Offer(p, i * 2.0 + 1, 5, 0.1f);
            var plan = Start(p, i * 2.0 + 1);
            int clip = plan.Clips[0];
            Assert.Equal(0, plan.Level);                     // nine sentences for one voice: nothing needs relaxing
            Assert.DoesNotContain(clip, finishes.TakeLast(2));
            Assert.DoesNotContain(p.Pool.At(clip)!.Group, finishes.TakeLast(3).Select(c => p.Pool.At(c)!.Group));
            finishes.Add(clip);
        }
        Assert.True(finishes.Distinct().Count() > 5);
    }

    // The relaxation order: the last two clips, then the window of recent sentences one sentence at a time, then the voice families; a sentence inside one reaction never.
    // A sentence group is one sentence in the voices of several fans; a family is one fan voice.

    [Fact]
    public void WhenTheRulesCannotAllHoldTheLastTwoClipsAreRelaxedFirstThenTheVoiceFamily()
    {
        // one sentence recorded by three voices (a pool of one sentence has no window): after two reactions both clips of the last two are taken, and fan9 is busy (a call sounds)
        var set = new ReactionSetBuilder().Add("start", 7, 1).Add("start", 8, 1).Add("start", 9, 1).Build();
        var p = Planner(set: set);
        var busy = new HashSet<int> { 9 };
        var plans = new List<CrowdReactionPlan>();
        for (int i = 0; i < 3; i++)
        {
            Offer(p, i * 10.0, 4, 0.2f);
            plans.Add(Start(p, i * 10.0, 3, busy));
        }
        Assert.Equal(new[] { 0, 0, 1 }, plans.Select(plan => plan.Level));                       // the third reaction relaxed the clips only
        Assert.Equal(new[] { false, false, true }, plans.Select(plan => plan.ClipsRelaxed));
        Assert.All(plans, plan => Assert.False(plan.FamiliesRelaxed));
        Assert.All(plans, plan => Assert.NotEqual(9, p.Pool.At(plan.Clips[0])!.Family));         // the family rule still held
        busy.UnionWith(new[] { 7, 8 });
        Offer(p, 30, 4, 0.2f);
        var fourth = Start(p, 30, 3, busy);
        Assert.Equal(2, fourth.Level);                                                           // now the families went too
        Assert.True(fourth.ClipsRelaxed && fourth.FamiliesRelaxed);
        Assert.Equal(1, fourth.Voices);
        Assert.Equal("last two clips, voice families", fourth.RelaxedText);
    }

    [Fact]
    public void TheLastTwoClipsAreRelaxedBeforeTheSentenceWindow()
    {
        // two sentences with one clip each: the window is one sentence, so the sentences alternate, but the clip of two reactions ago is still among the last two
        var set = new ReactionSetBuilder().Add("start", 7, 1).Add("start", 8, 2).Build();
        var p = Planner(set: set);
        var plans = Run(p, 3, 4, 0.2f);
        Assert.Equal(0, plans[0].Level);
        Assert.Equal(0, plans[1].Level);                                                         // the other sentence, whose clip is new
        var third = plans[2];
        Assert.Equal(plans[0].Clips[0], third.Clips[0]);                                         // the first sentence's only clip is back, as it must be
        Assert.Equal((1, true, 1, 1, false), (third.Level, third.ClipsRelaxed, third.GroupWindow, third.FullGroupWindow, third.FamiliesRelaxed));
        Assert.False(third.GroupsRelaxed);                                                       // the sentence window is untouched: the clip went first
        Assert.Equal("last two clips", third.RelaxedText);
    }

    [Fact]
    public void TheSentenceWindowShrinksOneSentenceAtATimeAndStopsAsSoonAsTheReactionHasItsVoices()
    {
        // Four sentences, one clip (and one voice family) each, three voices: the first reaction takes three of the four. The window is three. Steps: 1 the clips rule, 2 window 2
        // (two free sentences: not enough for three voices), 3 window 1 (three free: enough). Window 1 is the largest that works, so it is the one used; the families still hold.
        var set = new ReactionSetBuilder().Add("start", 7, 1).Add("start", 8, 2).Add("start", 9, 3).Add("start", 10, 4).Build();
        var p = Planner(set: set);
        var plans = Run(p, 2, 4, 1f);
        Assert.Equal((0, 3, 3, 3), (plans[0].Level, plans[0].Voices, plans[0].GroupWindow, plans[0].FullGroupWindow));
        var second = plans[1];
        Assert.Equal(3, second.Voices);
        Assert.Equal((3, true, 1, 3, false, false), (second.Level, second.ClipsRelaxed, second.GroupWindow, second.FullGroupWindow, second.FamiliesRelaxed, second.FellBack));
        Assert.True(second.GroupsRelaxed);
        Assert.Equal(3, Voices(p, second).Select(v => v.Group).Distinct().Count());              // never a sentence twice inside the reaction
        Assert.Equal(3, Voices(p, second).Select(v => v.Family).Distinct().Count());             // and the families held
        Assert.Equal("last two clips, sentence window 1 of 3", second.RelaxedText);
    }

    [Fact]
    public void TheSentenceWindowGoesAllTheWayDownBeforeTheVoiceFamiliesAreGivenUp()
    {
        // Three sentences, one clip each, three voices: every reaction needs all three sentences, so the window must be gone (step 3 of a window of two). The families are distinct
        // by construction and were never touched.
        var set = new ReactionSetBuilder().Add("start", 7, 1).Add("start", 8, 2).Add("start", 9, 3).Build();
        var p = Planner(set: set);
        var plans = Run(p, 4, 4, 1f);
        Assert.Equal((0, 3), (plans[0].Level, plans[0].Voices));
        Assert.All(plans.Skip(1), plan =>
        {
            Assert.Equal(3, plan.Voices);
            Assert.Equal((3, true, 0, 2, false), (plan.Level, plan.ClipsRelaxed, plan.GroupWindow, plan.FullGroupWindow, plan.FamiliesRelaxed));
            Assert.Equal("last two clips, sentence window 0 of 2", plan.RelaxedText);
        });
    }

    [Fact]
    public void TheVoiceFamilyIsRelaxedLastAndBeforeAVoiceIsLost()
    {
        // three sentences in one fan's voice: the family rule cannot hold for two voices, the sentences can. Every step before it (the clips rule, the window of two sentences down to
        // none) changes nothing for a first reaction, so the plan is found at the families step, which comes after the window: step 4 of a window of two.
        var set = new ReactionSetBuilder().Add("start", 7, 1).Add("start", 7, 2).Add("start", 7, 3).Build();
        var p = Planner(set: set);
        var plan = Run(p, 1, 4, 1f)[0];
        Assert.Equal(3, plan.Voices);
        Assert.Equal((4, true, 0, 2, true), (plan.Level, plan.ClipsRelaxed, plan.GroupWindow, plan.FullGroupWindow, plan.FamiliesRelaxed));
        Assert.Equal(3, Voices(p, plan).Select(v => v.Group).Distinct().Count());
        Assert.Single(Voices(p, plan).Select(v => v.Family).Distinct());
        Assert.Equal("last two clips, sentence window 0 of 2, voice families", plan.RelaxedText);
    }

    [Fact]
    public void ConsecutiveThreeVoiceStartsKeepAllThreeVoicesWithDistinctSentencesInsideEachReaction()
    {
        // Start has five sentences and the window is three. After a three-voice Start only two sentences are outside it, so a rigid window left the next Start at two voices for the rest
        // of the raid. The window shrinks instead: a recent sentence comes back, the reaction keeps its voices, and no sentence is said twice inside it.
        var p = Planner(81);
        var plans = Run(p, 60, 4, 1f);
        Assert.Equal(0, plans[0].Level);
        for (int i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            Assert.Equal((3, 3), (plan.Requested, plan.Voices));
            var groups = Voices(p, plan).Select(v => v.Group).ToArray();
            Assert.Equal(3, groups.Distinct().Count());
            if (i == 0) continue;
            Assert.True(plan.GroupsRelaxed, "five sentences cannot keep three out of the way of three");
            Assert.True(groups.Intersect(Voices(p, plans[i - 1]).Select(v => v.Group)).Any(), "so a sentence of the reaction before comes back (the pigeonhole of 3 + 3 voices in 5 sentences)");
        }
    }

    [Fact]
    public void ASentenceIsNeverRelaxedSoAReactionThatNeedsMoreVoicesThanItHasSentencesGetsFewer()
    {
        // two sentences, two voices each: three voices are asked for, two sentences can be said
        var set = new ReactionSetBuilder().Add("start", 7, 1).Add("start", 8, 1).Add("start", 9, 2).Add("start", 10, 2).Build();
        var p = Planner(set: set);
        var plans = Run(p, 60, 4, 1f);
        Assert.All(plans, plan =>
        {
            Assert.Equal(3, plan.Requested);
            Assert.Equal(2, plan.Voices);                    // the window and the clips rule are relaxed as far as they go, so every reaction says both sentences
            Assert.Equal(plan.Voices, Voices(p, plan).Select(v => v.Group).Distinct().Count());
        });
        Assert.Equal(0, plans[0].Level);                     // the first reaction says both sentences with every rule in force
        // one sentence only: one voice, however strong
        var one = new ReactionSetBuilder().Add("start", 7, 1).Add("start", 8, 1).Add("start", 9, 1).Build();
        Assert.All(Run(Planner(set: one), 20, 4, 1f), plan => Assert.Equal(1, plan.Voices));
    }

    [Fact]
    public void TheSentenceWindowKeepsASmallPoolAlive()
    {
        // two sentences: the window is one sentence, so the pool alternates and is never empty
        var set = new ReactionSetBuilder().Add("finish", 7, 1).Add("finish", 8, 1).Add("finish", 9, 2).Add("finish", 10, 2).Build();
        var p = Planner(set: set);
        var plans = Run(p, 40, 5, 0.2f);
        var groups = plans.Select(plan => p.Pool.At(plan.Clips[0])!.Group).ToList();
        for (int i = 1; i < groups.Count; i++) Assert.NotEqual(groups[i - 1], groups[i]);
    }

    // ---- delays, the hold and the plan ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EachFurtherVoiceStartsBetweenOneHundredTwentyAndFourHundredFiftyMillisecondsAfterThePreviousOne()
    {
        var p = Planner(51);
        var plans = Run(p, 300, 5, 1f);
        Assert.All(plans, plan =>
        {
            Assert.Equal(0.0, plan.Delays[0]);
            for (int i = 1; i < plan.Voices; i++) Assert.InRange(plan.Delays[i] - plan.Delays[i - 1], 0.12, 0.45);
            Assert.Equal(plan.Voices, plan.Lengths.Length);
        });
        Assert.Contains(plans, plan => plan.Voices == 3 && plan.Delays[2] > 0.4);
    }

    [Fact]
    public void ThePlanSaysHowLongTheCallsStayQuietTheGapTableIsOneForTheGlueAndTheTests()
    {
        Assert.Equal(new[] { 4.0, 1.0, 1.0, 4.0, 4.0 }, Enumerable.Range(1, 5).Select(CrowdReactionPlanner.HoldGap));
        Assert.Equal(1.0, CrowdReactionPlanner.HoldGap(6));
        Assert.Equal(1.0, CrowdReactionPlanner.HoldGap(99));
        foreach (int kind in Enumerable.Range(1, 5))
        {
            var plan = Run(Planner(52 + kind), 1, kind, 0.9f)[0];
            Assert.Equal(CrowdReactionPlanner.HoldGap(kind), plan.HoldGapSeconds);
            double end = 0;
            for (int i = 0; i < plan.Voices; i++) end = Math.Max(end, plan.Delays[i] + plan.Lengths[i]);
            Assert.Equal(end, plan.DurationSeconds, 9);
            Assert.Equal(end + plan.HoldGapSeconds, plan.HoldSeconds, 9);
            Assert.True(plan.DurationOf(1) <= plan.DurationSeconds);
            Assert.Equal(plan.Lengths[0], plan.DurationOf(1), 9);
        }
    }

    [Fact]
    public void AReactionHoldsTheRandomCallsForItsDurationPlusTheGap()
    {
        var clock = new SpectatorAudioSchedule(3);
        clock.Start(0);
        var p = Planner(53);
        Offer(p, 1000, 5, 1f);
        var plan = Start(p, 1000);
        clock.Hold(1000, plan.HoldSeconds);
        Assert.Equal(1000 + plan.HoldSeconds, clock.NextAt);
        Assert.True(clock.NextAt >= 1000 + plan.DurationSeconds + 4.0);
        Assert.False(clock.Due(1000 + plan.HoldSeconds - 0.01));
        Assert.True(clock.Due(1000 + plan.HoldSeconds));
    }

    [Fact]
    public void ThePlanCountsAndTheSeededPlannerIsDeterministic()
    {
        var a = Planner(60); var b = Planner(60); var c = Planner(61);
        var pa = Run(a, 50, 5, 1f).Select(plan => string.Join(",", plan.Clips)).ToList();
        var pb = Run(b, 50, 5, 1f).Select(plan => string.Join(",", plan.Clips)).ToList();
        var pc = Run(c, 50, 5, 1f).Select(plan => string.Join(",", plan.Clips)).ToList();
        Assert.Equal(pa, pb);
        Assert.NotEqual(pa, pc);
        Assert.Equal(50, a.StartedCount);
        Assert.Equal(a.StartedVoices, Run(Planner(60), 50, 5, 1f).Sum(plan => plan.Voices));
    }

    // ---- the speakers --------------------------------------------------------------------------------------------------------------------

    private static float[] Grid(int side, float spacing)
    {
        var positions = new float[side * side * 3];
        for (int i = 0; i < side * side; i++) { positions[3 * i] = i % side * spacing; positions[3 * i + 1] = 10f; positions[3 * i + 2] = i / side * spacing; }
        return positions;
    }

    private static float Dist(float[] p, int a, int b)
    {
        float dx = p[3 * a] - p[3 * b], dy = p[3 * a + 1] - p[3 * b + 1], dz = p[3 * a + 2] - p[3 * b + 2];
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    [Fact]
    public void ThreeVoicesComeFromThreeDistinctSpeakersAtLeastFourMetresApart()
    {
        var speakers = new CrowdReactionSpeakers(70);
        var positions = Grid(10, 2f);                        // 100 spectators two metres apart
        var result = new int[3];
        for (int round = 0; round < 500; round++)
        {
            Assert.Equal(3, speakers.Choose(positions, 9f, 0f, 9f, 60f, 3, null, result));
            Assert.Equal(3, result.Distinct().Count());
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++) Assert.True(Dist(positions, result[i], result[j]) >= CrowdReactionSpeakers.MinSeparation, $"{result[i]} and {result[j]}");
        }
    }

    [Fact]
    public void TheSpeakersFollowTheLawOfTheCallsTheNearestSectionOfTheStandsWithinReach()
    {
        var speakers = new CrowdReactionSpeakers(71);
        var positions = new List<float>();
        for (int i = 0; i < 6; i++) positions.AddRange(new[] { 10f + i * 5f, 0f, 0f });          // 10, 15, ... 35 m from the listener
        positions.AddRange(new[] { 500f, 0f, 0f });                                              // far beyond the reach
        var result = new int[3];
        for (int round = 0; round < 200; round++)
        {
            int n = speakers.Choose(positions.ToArray(), 0f, 0f, 0f, 60f, 3, null, result);
            Assert.InRange(n, 1, 3);
            Assert.All(result.Take(n), s => Assert.InRange(positions[3 * s], 10f, 25f));        // nearest + 15 m, as the calls choose
        }
    }

    [Fact]
    public void TheSpeakerThatSpokeLastDoesNotOpenTheNextReactionWhenAnotherOneIsInReach()
    {
        var speakers = new CrowdReactionSpeakers(73);
        var two = new float[] { 10, 0, 0, 14, 0, 0 };        // two stands within the nearest-plus-15 m band
        var result = new int[1];
        int previous = -1;
        for (int round = 0; round < 100; round++)
        {
            Assert.Equal(1, speakers.Choose(two, 0f, 0f, 0f, 60f, 1, null, result));
            Assert.NotEqual(previous, result[0]);            // strict alternation: the speaker of the last reaction is not drawn again
            previous = result[0];
        }
        // the last voice of a reaction is the one that counts as the last speaker
        var law = new CrowdReactionSpeakers(74);
        var wide = new float[] { 10, 0, 0, 15, 0, 0, 20, 0, 0 };       // three stands in the band, five metres apart
        var three = new int[3];
        for (int round = 0; round < 100; round++)
        {
            Assert.Equal(3, law.Choose(wide, 0f, 0f, 0f, 60f, 3, null, three));
            Assert.Equal(3, three.Distinct().Count());
            int last = three[2];
            Assert.Equal(1, law.Choose(wide, 0f, 0f, 0f, 60f, 1, null, result));
            Assert.NotEqual(last, result[0]);
        }
        // a single stand in reach has no other to turn to: it speaks every time
        var one = new float[] { 10, 0, 0 };
        for (int round = 0; round < 20; round++)
        {
            Assert.Equal(1, speakers.Choose(one, 0f, 0f, 0f, 60f, 1, null, result));
            Assert.Equal(0, result[0]);
        }
    }

    [Fact]
    public void TooFewSeparatedSpeakersGiveFewerVoicesAndBusyOrUnreachableOnesAreNeverPicked()
    {
        var speakers = new CrowdReactionSpeakers(72);
        var cluster = Grid(3, 1.5f);                         // nine spectators within a 3 m square: only one speaker fits
        var result = new int[3];
        Assert.Equal(1, speakers.Choose(cluster, 0f, 0f, 0f, 60f, 3, null, result));
        var line = new float[] { 10, 0, 0, 14, 0, 0, 18, 0, 0, 22, 0, 0 };                        // four metres apart in a row
        for (int round = 0; round < 100; round++)
        {
            int n = speakers.Choose(line, 0f, 0f, 0f, 60f, 3, new HashSet<int> { 1 }, result);
            Assert.InRange(n, 1, 3);
            Assert.DoesNotContain(1, result.Take(n));
        }
        Assert.Equal(0, speakers.Choose(line, 0f, 0f, 0f, 5f, 3, null, result));                // nobody within reach
        Assert.Equal(0, speakers.Choose(line, 0f, 0f, 0f, 60f, 3, new HashSet<int> { 0, 1, 2, 3 }, result));
        Assert.Equal(0, speakers.Choose(null!, 0f, 0f, 0f, 60f, 3, null, result));
        Assert.Equal(0, speakers.Choose(new float[2], 0f, 0f, 0f, 60f, 3, null, result));      // a partial triple is no speaker
        Assert.Equal(0, speakers.Choose(line, 0f, 0f, 0f, 60f, 0, null, result));
        Assert.Equal(0, speakers.Choose(line, 0f, 0f, 0f, 60f, 3, null, null!));
        var withNaN = new float[] { float.NaN, 0, 0, float.PositiveInfinity, 0, 0, 12, 0, 0 };
        for (int round = 0; round < 20; round++)
        {
            Assert.Equal(1, speakers.Choose(withNaN, 0f, 0f, 0f, 60f, 3, null, result));
            Assert.Equal(2, result[0]);
        }
        Assert.Equal(2, speakers.Choose(Grid(5, 8f), 0f, 0f, 0f, 200f, 3, null, new int[2]));   // the result array caps the voices
    }

    // ---- the real manifest ---------------------------------------------------------------------------------------------------------------

    [RealReactionSetFact]
    public void TheRealReactionSetValidatesAndHasThePhaseAPoolSizes()
    {
        var manifest = RealReactionSet.Manifest()!;
        manifest.Validate();
        manifest.ValidateReactions();
        var pool = new CrowdReactionPool(manifest.reactions);
        Assert.Equal(62, pool.KindsMask);
        Assert.Equal(70, manifest.reactions.clips.Length);
        Assert.Equal(new[] { 6, 4, 2, 0, 10, 4, 6, 9, 12, 17 },
            new[] { "praise", "gloat", "shout", "urge", "lament", "heckle", "start", "finish", "panic", "directed" }.Select(pool.CountOf));
        Assert.Equal(new[] { 7, 11, 12, 13 }, pool.FamiliesOf(CrowdReactionKind.Start));
        Assert.Equal(5, pool.GroupCountOf(CrowdReactionKind.Start));
        Assert.Equal(11, pool.GroupCountOf(CrowdReactionKind.Approve));
        Assert.Equal(12, pool.GroupCountOf(CrowdReactionKind.Disapprove));
        Assert.Equal(9, pool.GroupCountOf(CrowdReactionKind.Finish));
        Assert.Equal(18, pool.GroupCountOf(CrowdReactionKind.TakeCover));
        Assert.Equal(6, manifest.reactions.clips.Where(c => c.pool == "panic").Select(c => c.group).Distinct().Count());
        // the shortest and the longest exported reaction clip
        Assert.InRange(manifest.reactions.clips.Min(c => c.frames) / 44100.0, 0.5, 0.8);
        Assert.InRange(manifest.reactions.clips.Max(c => c.frames) / 44100.0, 6.5, 7.0);
        // every kind plays from the real pool: a few hundred reactions of every kind and strength, and the rules hold
        foreach (int kind in Enumerable.Range(1, 5))
            foreach (float strength in new[] { 0.2f, 0.6f, 1f })
            {
                var p = new CrowdReactionPlanner(pool, 99 + kind);
                var plans = Run(p, 100, kind, strength, directed: kind == 3);
                Assert.All(plans, plan => Assert.Equal(plan.Voices, Voices(p, plan).Select(v => v.Group).Distinct().Count()));
                Assert.All(plans, plan => Assert.Equal(plan.Requested, plan.Voices));        // every kind has at least three sentences: the rules relax, no voice is lost
            }
    }

    [RealReactionSetFact]
    public void TheRealReactionSetAgreesWithThePoolSidecar()
    {
        string? sidecar = RealReactionSet.Sidecar();
        if (sidecar == null) return;                         // a checkout without tools/extract/reaction_pools.json: nothing to compare
        var manifest = RealReactionSet.Manifest()!;
        var side = JObject.Parse(File.ReadAllText(sidecar));
        var clips = (JObject)side["clips"]!;
        var exportedBanks = side["exported_banks"]!.Select(t => (string)t!).ToHashSet();
        var keep = clips.Properties()
            .Where(p => (string?)p.Value["status"] == "keep" && p.Value["banks"]!.Any(b => exportedBanks.Contains((string)b!)))
            .ToDictionary(p => p.Name, p => (Pool: (string)p.Value["pool"]!, Group: (string)p.Value["group"]!));
        Assert.Equal(keep.Count, manifest.reactions.clips.Length);
        foreach (var clip in manifest.reactions.clips)
        {
            Assert.True(keep.TryGetValue(clip.name, out var expected), clip.name + " is exported but not 'keep' in the sidecar");
            Assert.Equal(expected.Pool, clip.pool);
            Assert.Equal(expected.Group, clip.group);
        }
    }
}

/// <summary>Runs only when the ignored manifest of the extractor (work/extract/spectator-audio/spectator-audio.json, BSG-derived) has a <c>reactions</c> object: the checkout's work folder, or
/// the one named by ARENAMAPPORT_WORK_DIR.</summary>
internal sealed class RealReactionSetFactAttribute : FactAttribute
{
    public RealReactionSetFactAttribute()
    {
        if (RealReactionSet.Manifest() == null) Skip = "work/extract/spectator-audio/spectator-audio.json has no 'reactions' object (BSG-derived, ignored by git; run tools/extract/extract_spectator_audio.py extract)";
    }
}

internal static class RealReactionSet
{
    private static string? Find(string relative)
    {
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string path = Path.Combine(env, relative.StartsWith("work" + Path.DirectorySeparatorChar) ? relative.Substring(5) : relative);
            if (File.Exists(path)) return path;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static SpectatorAudioManifest? Manifest()
    {
        string? path = Find(Path.Combine("work", "extract", "spectator-audio", "spectator-audio.json"));
        if (path == null) return null;
        try
        {
            string text = File.ReadAllText(path);
            if (JObject.Parse(text)["reactions"] is not JObject) return null;
            return JsonConvert.DeserializeObject<SpectatorAudioManifest>(text);
        }
        catch (Exception) { return null; }
    }

    internal static string? Sidecar() => Find(Path.Combine("tools", "extract", "reaction_pools.json"));
}
