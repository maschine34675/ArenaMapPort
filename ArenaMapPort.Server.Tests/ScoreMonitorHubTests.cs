using System.Collections.Concurrent;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The claim semantics behind the public ScoreMonitors contract (ScoreMonitorHub, docs/progress/score-monitors.md): one owner at a time, the same owner gets the same handle,
/// the developer demo never blocks a plugin and a plugin takes over from it, a stale or foreign handle makes every setter fail and change nothing, the clamps, idempotent
/// setters (no version bump for an unchanged call), handles that are never reused, and a lock that keeps a snapshot consistent under parallel setters. Unity-free.
/// </summary>
public sealed class ScoreMonitorHubTests
{
    private sealed class Raid
    {
        internal bool Loaded = true, Enabled = true;
        internal int Usable = 9;
        internal ScoreMonitorHub Hub => _hub ??= new ScoreMonitorHub(() => Loaded, () => Usable, () => Enabled);
        private ScoreMonitorHub? _hub;
    }

    // ---- claiming ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AClaimGivesAPositiveHandleAndTheDefaultContent()
    {
        var raid = new Raid();
        int claim = raid.Hub.Claim("ArenaMode Team Deathmatch", false);
        Assert.True(claim > 0);
        Assert.True(raid.Hub.IsClaimed);
        var s = raid.Hub.Snapshot();
        Assert.Equal(claim, s.Claim);
        Assert.Equal("ArenaMode Team Deathmatch", s.Owner);
        Assert.False(s.Demo);
        Assert.Equal(-1, s.ColorA);
        Assert.Equal(-1, s.ColorB);
        Assert.Equal(ScoreMonitorModel.DefaultTeamSize, s.TeamSize);
        Assert.Equal(5, s.TeamSize);
        Assert.Equal(0, s.ScoreA);
        Assert.Equal(0, s.ScoreB);
        Assert.Equal(-1, s.AliveA);
        Assert.Equal(-1, s.AliveB);
        Assert.Equal(MonitorClockMode.None, s.ClockMode);
    }

    [Fact]
    public void TheSameOwnerClaimingAgainGetsTheSameHandleAndKeepsTheContent()
    {
        var raid = new Raid();
        int first = raid.Hub.Claim("mode", false);
        Assert.True(raid.Hub.SetScore(first, 12, 9, 4, 3));
        int version = raid.Hub.Version;
        Assert.Equal(first, raid.Hub.Claim("mode", false));
        Assert.Equal(first, raid.Hub.Claim("  mode  ", false));   // the owner name is trimmed
        Assert.Equal(version, raid.Hub.Version);                  // nothing changed
        var s = raid.Hub.Snapshot();
        Assert.Equal(12, s.ScoreA);
        Assert.Equal(3, s.AliveB);
    }

    [Fact]
    public void AnotherOwnerIsRefusedWhileTheMonitorsAreHeld()
    {
        var raid = new Raid();
        int first = raid.Hub.Claim("mode A", false);
        Assert.Equal(0, raid.Hub.Claim("mode B", false));
        Assert.Equal(first, raid.Hub.Snapshot().Claim);
        Assert.Equal("mode A", raid.Hub.Snapshot().Owner);
        Assert.True(raid.Hub.SetScore(first, 1, 2, 3, 4));   // the holder is unaffected
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankOwnerIsRefused(string? owner)
    {
        var raid = new Raid();
        Assert.Equal(0, raid.Hub.Claim(owner!, false));
        Assert.False(raid.Hub.IsClaimed);
    }

    [Fact]
    public void AClaimIsRefusedWhenTheFeatureIsOffNoOwnedRaidIsLoadedOrTheMapHasNothingUsable()
    {
        var raid = new Raid { Enabled = false };
        Assert.False(raid.Hub.IsAvailable);
        Assert.Equal(0, raid.Hub.Claim("mode", false));
        raid.Enabled = true;
        raid.Loaded = false;
        Assert.False(raid.Hub.IsAvailable);
        Assert.Equal(0, raid.Hub.Claim("mode", false));
        raid.Loaded = true;
        raid.Usable = 0;
        Assert.False(raid.Hub.IsAvailable);
        Assert.Equal(0, raid.Hub.Claim("mode", false));
        raid.Usable = 1;
        Assert.True(raid.Hub.IsAvailable);
        Assert.True(raid.Hub.Claim("mode", false) > 0);
    }

    [Fact]
    public void RefusalsAreQueuedForTheMainThreadNotLogged()
    {
        var raid = new Raid { Loaded = false };
        raid.Hub.Claim("mode", false);
        Assert.True(raid.Hub.HasLog);
        var lines = raid.Hub.DrainLog();
        Assert.NotNull(lines);
        Assert.Single(lines!);
        Assert.Contains("no owned raid", lines![0]);
        Assert.False(raid.Hub.HasLog);
        Assert.Null(raid.Hub.DrainLog());
    }

    [Fact]
    public void TheLogQueueIsBounded()
    {
        var raid = new Raid { Loaded = false };
        for (int i = 0; i < 100; i++) raid.Hub.Claim("mode", false);
        Assert.True(raid.Hub.DrainLog()!.Length <= 16);
    }

    // ---- the demo ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void APluginTakesTheMonitorsOverFromTheDemoWithAFreshContent()
    {
        var raid = new Raid();
        int demo = raid.Hub.Claim("developer-demo", true);
        Assert.True(demo > 0);
        Assert.True(raid.Hub.Snapshot().Demo);
        Assert.True(raid.Hub.SetScore(demo, 30, 20, 2, 2));
        int plugin = raid.Hub.Claim("ArenaMode", false);
        Assert.True(plugin > demo);
        var s = raid.Hub.Snapshot();
        Assert.Equal(plugin, s.Claim);
        Assert.False(s.Demo);
        Assert.Equal(0, s.ScoreA);   // the plugin starts from the defaults, not from the demo's numbers
        Assert.False(raid.Hub.SetScore(demo, 1, 1, 1, 1));   // the demo's handle is stale at once
        Assert.True(raid.Hub.SetScore(plugin, 1, 1, 1, 1));
    }

    [Fact]
    public void TheDemoNeverTakesTheMonitorsFromAPlugin()
    {
        var raid = new Raid();
        int plugin = raid.Hub.Claim("ArenaMode", false);
        Assert.Equal(0, raid.Hub.Claim("developer-demo", true));
        Assert.Equal(plugin, raid.Hub.Snapshot().Claim);
        Assert.False(raid.Hub.Snapshot().Demo);
    }

    [Fact]
    public void TheDemoClaimingAgainGetsTheSameHandle()
    {
        var raid = new Raid();
        int demo = raid.Hub.Claim("developer-demo", true);
        Assert.Equal(demo, raid.Hub.Claim("developer-demo", true));
    }

    [Fact]
    public void APluginWithTheDemosOwnNameStillTakesOverAsAPlugin()
    {
        var raid = new Raid();
        int demo = raid.Hub.Claim("developer-demo", true);
        int plugin = raid.Hub.Claim("developer-demo", false);
        Assert.NotEqual(demo, plugin);
        Assert.False(raid.Hub.Snapshot().Demo);
    }

    // ---- release and the end of the raid -------------------------------------------------------------------------------------------------

    [Fact]
    public void ReleaseTakesTheMonitorsBackOnceAndTheHandleIsStaleAfterwards()
    {
        var raid = new Raid();
        int claim = raid.Hub.Claim("mode", false);
        raid.Hub.BuiltCount = 7;
        int version = raid.Hub.Version;
        Assert.True(raid.Hub.Release(claim));
        Assert.True(raid.Hub.Version > version);
        Assert.False(raid.Hub.IsClaimed);
        Assert.Equal(0, raid.Hub.BuiltCount);
        Assert.Equal(0, raid.Hub.Snapshot().Claim);
        Assert.False(raid.Hub.Release(claim));
        Assert.False(raid.Hub.SetScore(claim, 1, 1, 1, 1));
        Assert.False(raid.Hub.SetSides(claim, 1, 2, 3));
        Assert.False(raid.Hub.SetClock(claim, 1, 5f));
    }

    [Fact]
    public void AStaleHandleCannotReleaseALaterClaim()
    {
        var raid = new Raid();
        int demo = raid.Hub.Claim("developer-demo", true);
        int plugin = raid.Hub.Claim("mode", false);
        Assert.False(raid.Hub.Release(demo));
        Assert.True(raid.Hub.IsClaimed);
        Assert.True(raid.Hub.Release(plugin));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public void ForeignHandlesFailEverySetterAndChangeNothing(int handle)
    {
        var raid = new Raid();
        raid.Hub.Claim("mode", false);
        int version = raid.Hub.Version;
        Assert.False(raid.Hub.Release(handle));
        Assert.False(raid.Hub.SetSides(handle, 1, 2, 3));
        Assert.False(raid.Hub.SetScore(handle, 1, 2, 3, 4));
        Assert.False(raid.Hub.SetClock(handle, 1, 9f));
        Assert.Equal(version, raid.Hub.Version);
    }

    [Fact]
    public void ANewRaidVoidsTheClaimAndHandlesAreNeverReused()
    {
        var raid = new Raid();
        int first = raid.Hub.Claim("mode", false);
        raid.Hub.SetScore(first, 5, 4, 3, 2);
        raid.Hub.BuiltCount = 4;
        raid.Hub.ResetForNewRaid();
        Assert.False(raid.Hub.IsClaimed);
        Assert.Equal(0, raid.Hub.BuiltCount);
        Assert.False(raid.Hub.SetScore(first, 1, 1, 1, 1));
        int second = raid.Hub.Claim("mode", false);
        Assert.True(second > first);
        Assert.False(raid.Hub.SetScore(first, 1, 1, 1, 1));
        Assert.Equal(0, raid.Hub.Snapshot().ScoreA);
    }

    // ---- the setters ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SettersAreIdempotentAndBumpTheVersionOnlyWhenSomethingChanges()
    {
        var raid = new Raid();
        int c = raid.Hub.Claim("mode", false);
        Assert.True(raid.Hub.SetSides(c, 0x112233, 0x445566, 8));
        int v = raid.Hub.Version;
        Assert.True(raid.Hub.SetSides(c, 0x112233, 0x445566, 8));
        Assert.Equal(v, raid.Hub.Version);
        Assert.True(raid.Hub.SetSides(c, 0x112233, 0x445566, 9));
        Assert.True(raid.Hub.Version > v);

        v = raid.Hub.Version;
        Assert.True(raid.Hub.SetScore(c, 3, 4, 5, 6));
        Assert.True(raid.Hub.Version > v);
        v = raid.Hub.Version;
        Assert.True(raid.Hub.SetScore(c, 3, 4, 5, 6));
        Assert.Equal(v, raid.Hub.Version);

        Assert.True(raid.Hub.SetClock(c, MonitorClockMode.CountDown, 1234.5f));
        v = raid.Hub.Version;
        Assert.True(raid.Hub.SetClock(c, MonitorClockMode.CountDown, 1234.5f));
        Assert.Equal(v, raid.Hub.Version);
        Assert.True(raid.Hub.SetClock(c, MonitorClockMode.CountDown, 1235f));
        Assert.True(raid.Hub.Version > v);
    }

    [Fact]
    public void ScoresAliveCountsTeamSizeAndColoursAreClamped()
    {
        var raid = new Raid();
        int c = raid.Hub.Claim("mode", false);
        raid.Hub.SetScore(c, -5, 10_000, -3, 99);
        var s = raid.Hub.Snapshot();
        Assert.Equal(0, s.ScoreA);
        Assert.Equal(9999, s.ScoreB);
        Assert.Equal(-1, s.AliveA);
        Assert.Equal(16, s.AliveB);

        raid.Hub.SetSides(c, -1, 0x1000000, 99);
        s = raid.Hub.Snapshot();
        Assert.Equal(-1, s.ColorA);
        Assert.Equal(-1, s.ColorB);
        Assert.Equal(16, s.TeamSize);

        raid.Hub.SetSides(c, 0xFF00FF, 0, -4);
        s = raid.Hub.Snapshot();
        Assert.Equal(0xFF00FF, s.ColorA);
        Assert.Equal(0, s.ColorB);   // black is a colour, only a negative or too large value means Arena's own
        Assert.Equal(0, s.TeamSize);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void AnUnknownClockModeIsRefusedAndChangesNothing(int mode)
    {
        var raid = new Raid();
        int c = raid.Hub.Claim("mode", false);
        raid.Hub.SetClock(c, MonitorClockMode.Frozen, 60f);
        int v = raid.Hub.Version;
        Assert.False(raid.Hub.SetClock(c, mode, 5f));
        Assert.Equal(v, raid.Hub.Version);
        Assert.Equal(MonitorClockMode.Frozen, raid.Hub.Snapshot().ClockMode);
        Assert.Equal(60f, raid.Hub.Snapshot().ClockSeconds);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ANonFiniteClockValueCountsAsZero(float seconds)
    {
        var raid = new Raid();
        int c = raid.Hub.Claim("mode", false);
        Assert.True(raid.Hub.SetClock(c, MonitorClockMode.CountDown, seconds));
        Assert.Equal(0f, raid.Hub.Snapshot().ClockSeconds);
        Assert.Equal(MonitorClockMode.CountDown, raid.Hub.Snapshot().ClockMode);
    }

    [Fact]
    public void TheOwnerNameIsTrimmedCutAndAsciiOnly()
    {
        Assert.Equal("a b", ScoreMonitorModel.CleanOwner("  a b  "));
        Assert.Equal(ScoreMonitorModel.OwnerMax, ScoreMonitorModel.CleanOwner(new string('x', 200))!.Length);
        Assert.Equal("caf?", ScoreMonitorModel.CleanOwner("café"));
        Assert.Equal("a?b", ScoreMonitorModel.CleanOwner("a\nb"));
        Assert.Null(ScoreMonitorModel.CleanOwner(null));
        Assert.Null(ScoreMonitorModel.CleanOwner(" \t "));
    }

    // ---- threads -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ParallelSettersNeverTearASnapshot()
    {
        var raid = new Raid();
        int c = raid.Hub.Claim("mode", false);
        var torn = new ConcurrentBag<string>();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 3000; i++)
            {
                int v = (i * 7 + w) % 10000;
                raid.Hub.SetScore(c, v, v, v % 17 - 1, v % 17 - 1);
                raid.Hub.SetSides(c, v % 0x1000000, v % 0x1000000, v % 17);
                raid.Hub.SetClock(c, v % 4, v);
            }
        })).ToArray();
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 6000; i++)
            {
                var s = raid.Hub.Snapshot();
                if (s.ScoreA != s.ScoreB || s.AliveA != s.AliveB || s.ColorA != s.ColorB) torn.Add($"{s.ScoreA}/{s.ScoreB} {s.AliveA}/{s.AliveB} {s.ColorA}/{s.ColorB}");
                if (s.Claim != c) torn.Add("claim " + s.Claim);
            }
        });
        await Task.WhenAll(writers.Append(reader));
        Assert.Empty(torn);
        Assert.True(raid.Hub.Release(c));
    }

    [Fact]
    public void ParallelClaimsLeaveExactlyOneHolder()
    {
        var raid = new Raid();
        var handles = new ConcurrentBag<int>();
        Parallel.For(0, 64, i => handles.Add(raid.Hub.Claim("owner " + i, false)));
        Assert.Single(handles, h => h > 0);
        Assert.Equal(handles.Single(h => h > 0), raid.Hub.Snapshot().Claim);
    }
}
