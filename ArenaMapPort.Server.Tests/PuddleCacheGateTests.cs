using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The pacing rules of the puddle footstep cache (PuddleCacheGate, 2026-10-25): building the cache walks every renderer of the map
/// (30 to 60 ms), so it must never turn into a once-a-second scan while a listed water renderer waits for its native shader
/// (the 1 Hz freeze trap of AGENTS.md), and a scene that is not loaded is reported once instead of staying silent.
/// </summary>
public sealed class PuddleCacheGateTests
{
    [Fact]
    public void AttemptsAreSpacedOneSecondApart()
    {
        var gate = new PuddleCacheGate();
        Assert.True(gate.TryBegin(10f));
        Assert.False(gate.TryBegin(10f));
        Assert.False(gate.TryBegin(10.99f));
        Assert.True(gate.TryBegin(11f));
        Assert.False(gate.TryBegin(11.5f));
    }

    [Fact]
    public void ACompleteScanIsReadyAndNeedsNoRetry()
    {
        var gate = new PuddleCacheGate();
        Assert.Equal(PuddleScanResult.Ready, gate.AfterFullScan(false));
        Assert.Equal(1, gate.FullScans);
    }

    [Fact]
    public void AWaitingRebindRetriesAtMostFiveTimesAndThenGivesUp()
    {
        var gate = new PuddleCacheGate();
        Assert.Equal(5, PuddleCacheGate.MaxFullScans);
        for (int scan = 1; scan < PuddleCacheGate.MaxFullScans; scan++)
            Assert.Equal(PuddleScanResult.Retry, gate.AfterFullScan(true));
        Assert.Equal(PuddleScanResult.GaveUp, gate.AfterFullScan(true));
        Assert.Equal(PuddleCacheGate.MaxFullScans, gate.FullScans);
    }

    [Fact]
    public void TheRebindMayFinishOnALaterScan()
    {
        var gate = new PuddleCacheGate();
        Assert.Equal(PuddleScanResult.Retry, gate.AfterFullScan(true));
        Assert.Equal(PuddleScanResult.Retry, gate.AfterFullScan(true));
        Assert.Equal(PuddleScanResult.Ready, gate.AfterFullScan(false));
    }

    [Fact]
    public void ASceneChangeOrAToggleGrantsAFreshSetOfAttempts()
    {
        var gate = new PuddleCacheGate();
        Assert.True(gate.TryBegin(100f));
        for (int scan = 0; scan < PuddleCacheGate.MaxFullScans; scan++) gate.AfterFullScan(true);
        Assert.True(gate.ReportScenesMissing());
        gate.Reset();
        Assert.Equal(0, gate.FullScans);
        Assert.True(gate.TryBegin(100f)); // pacing forgotten as well
        Assert.True(gate.ReportScenesMissing());
        Assert.Equal(PuddleScanResult.Retry, gate.AfterFullScan(true));
    }

    [Fact]
    public void AMissingSceneIsReportedOnlyOncePerSceneState()
    {
        var gate = new PuddleCacheGate();
        Assert.True(gate.ReportScenesMissing());
        for (int i = 0; i < 20; i++) Assert.False(gate.ReportScenesMissing());
    }

    [Fact]
    public void AMapWithAStuckRendererScansAtMostFiveTimesInTotalNoMatterHowLongThePlayerWalks()
    {
        // The unbounded predecessor rescanned once a second for as long as the player walked on concrete. Model one minute of
        // footsteps (every frame at 60 Hz) with a renderer that never gets its shader.
        var gate = new PuddleCacheGate();
        int scans = 0;
        bool dirty = true;
        for (int frame = 0; frame < 60 * 60 && dirty; frame++)
        {
            if (!gate.TryBegin(frame / 60f)) continue;
            scans++;
            dirty = gate.AfterFullScan(true) == PuddleScanResult.Retry;
        }
        Assert.False(dirty);
        Assert.Equal(PuddleCacheGate.MaxFullScans, scans);
    }
}
