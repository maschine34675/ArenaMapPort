using System.Diagnostics;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The Unity-free half of the reactions' developer demo (SpectatorReactionDemoPlan.cs) and the queue's idle check the reaction section of SpectatorAudio.Update stands on. The demo
/// is the only thing that fires reactions without a mode plugin, so its table is proved against the REAL planner: every step of every pass (all kinds, and each kind alone) is
/// accepted, planned and started at gap scale 1, with no cooldown, rate, stale or slot drop, over three repeating passes and a dozen seeds; the strength bands it is meant to
/// demonstrate really are the ones the clips are drawn from. The synthetic phase-A set is the tests' own (<see cref="ReactionSetBuilder"/>).
/// </summary>
public sealed class SpectatorReactionDemoTests
{
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static int[] AllReasons => Enumerable.Range(1, CrowdReactionDropReason.Count - 1).ToArray();

    /// <summary>The demo and the host loop of SpectatorAudio.UpdateReactions in one place: the runner fires steps through the queue, the planner plans, three reaction slots are
    /// occupied until each voice's start delay plus its clip has passed.</summary>
    private sealed class Sim
    {
        public readonly CrowdReactionQueue Queue = new();
        public readonly CrowdReactionPlanner Planner;
        public readonly SpectatorReactionDemoPlan Demo = new();
        public readonly List<(ReactionDemoStep Step, CrowdReactionPlan? Plan)> Log = new();
        public readonly int Handle;
        private readonly List<double> _busyUntil = new();
        private readonly CrowdReactionRequest[] _buffer = new CrowdReactionRequest[CrowdReactionQueue.Capacity];
        private ReactionDemoStep? _lastFired;
        public double Now;
        public int Fired, Started;

        public Sim(int seed)
        {
            Planner = new CrowdReactionPlanner(ReactionSetBuilder.PhaseA(), seed);
            Queue.SetKinds(Planner.KindsMask);
            Handle = Queue.Claim(CrowdReactionQueue.DemoOwner);
            Assert.True(Handle > 0);
        }

        /// <summary>Runs 20 frames a second until <paramref name="steps"/> demo steps have fired and started (or the time limit).</summary>
        public void RunSteps(ReactionDemoMode mode, int filter, int steps, double limitSeconds)
        {
            double end = Now + limitSeconds;
            int settle = 0;
            while (Now < end && (Fired < steps || Started < Fired || settle < 20))
            {
                Frame(mode, filter, Fired < steps);
                if (Fired >= steps && Started >= Fired) settle++;
                Now += 0.05;
            }
        }

        private void Frame(ReactionDemoMode mode, int filter, bool fire)
        {
            _busyUntil.RemoveAll(t => t <= Now);
            if (fire && Demo.TryNext(Now, mode, filter, out var step))
            {
                Fired++;
                Assert.True(Queue.React(Handle, step.Kind, step.Strength, step.Directed, Ticks(Now)), "the queue accepts every step of the demo");
                _lastFired = step;
            }
            long stamp = Ticks(Now);
            if (Queue.HasWaiting)
            {
                int n = Queue.Drain(_buffer, stamp);
                if (n > 0) Planner.Offer(_buffer, n, stamp, Now);
            }
            while (Planner.Pending > 0 && Planner.TryPlan(Now, 3 - _busyUntil.Count, null, out var plan))
            {
                Planner.Started(plan, Now);
                for (int i = 0; i < plan.Voices; i++) _busyUntil.Add(Now + plan.Delays[i] + plan.Lengths[i]);
                Started++;
                Log.Add((_lastFired ?? default, plan));
            }
        }
    }

    // ---- the table -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void OnePassWalksEveryImplementedKindAtSteppedStrengthsAndTakeCoverTwice()
    {
        var pass = SpectatorReactionDemoPlan.Pass(0);
        Assert.Equal(new[] { 1, 1, 1, 2, 2, 2, 3, 3, 4, 5 }, pass.Select(s => s.Kind));
        Assert.Equal(new[] { 0.30f, 0.60f, 0.90f, 0.30f, 0.60f, 0.90f, 0.50f, 0.50f, 1.0f, 1.0f }, pass.Select(s => s.Strength));
        Assert.Equal(new[] { false, false, false, false, false, false, false, true, false, false }, pass.Select(s => s.Directed));
        Assert.Equal(0.0, pass[0].At);
        Assert.Equal(CrowdReactionKind.ImplementedMask, pass.Aggregate(0, (mask, s) => mask | CrowdReactionKind.Bit(s.Kind)));
        for (int i = 1; i < pass.Length; i++)
            Assert.True(pass[i].At - pass[i - 1].At >= SpectatorReactionDemoPlan.SpacingSeconds, $"step {i} follows its predecessor by at least the spacing");
    }

    [Fact]
    public void TheStepsAreSpacedByTheSlotsAndTheKindsCooldownAndTheSecondTakeCoverWaitsItsFifteenSeconds()
    {
        Assert.True(SpectatorReactionDemoPlan.SpacingSeconds > 6.713 + 2 * (CrowdReactionPlanner.DelayMinSeconds + CrowdReactionPlanner.DelaySpanSeconds),
            "longer than the longest clip plus the staggering of three voices: the reaction slots are free again");
        var pass = SpectatorReactionDemoPlan.Pass(0);
        double panic = pass[6].At, directed = pass[7].At;
        Assert.True(directed - panic >= CrowdReactionPlanner.BaseGap(CrowdReactionKind.TakeCover) + SpectatorReactionDemoPlan.MarginSeconds);
        Assert.Equal(15.0, SpectatorReactionDemoPlan.Gap(CrowdReactionKind.TakeCover, 0.5f));
        Assert.Equal(7.5, SpectatorReactionDemoPlan.Gap(CrowdReactionKind.TakeCover, 0.9f));       // from 0.8 the gap is halved, as the planner has it
        Assert.Equal(1.5, SpectatorReactionDemoPlan.Gap(CrowdReactionKind.Approve, 0.9f));
        Assert.Equal(5.0, SpectatorReactionDemoPlan.Gap(CrowdReactionKind.Start, 0.5f));
    }

    [Theory]
    [InlineData(1, new[] { 1, 1, 1 })]
    [InlineData(2, new[] { 2, 2, 2 })]
    [InlineData(3, new[] { 3, 3 })]
    [InlineData(4, new[] { 4 })]
    [InlineData(5, new[] { 5 })]
    public void AKindFilterKeepsOnlyThatKindsStepsAndAnyOtherNumberHasNone(int filter, int[] kinds)
    {
        Assert.Equal(kinds, SpectatorReactionDemoPlan.Pass(filter).Select(s => s.Kind));
        Assert.Empty(SpectatorReactionDemoPlan.Pass(6));
        Assert.Empty(SpectatorReactionDemoPlan.Pass(-1));
        Assert.Empty(SpectatorReactionDemoPlan.Pass(32));
    }

    [Fact]
    public void ARepeatingPassIsLongEnoughThatItsFirstStepOfEveryKindClearsTheCooldownOfTheLastOne()
    {
        for (int filter = 0; filter <= 5; filter++)
        {
            var pass = SpectatorReactionDemoPlan.Pass(filter);
            double cycle = SpectatorReactionDemoPlan.CycleSeconds(pass);
            Assert.True(cycle >= pass[^1].At + SpectatorReactionDemoPlan.SpacingSeconds, $"filter {filter}: one spacing after the last step");
            foreach (var kind in pass.Select(s => s.Kind).Distinct())
            {
                var first = pass.First(s => s.Kind == kind);
                var last = pass.Last(s => s.Kind == kind);
                Assert.True(cycle + first.At - last.At >= SpectatorReactionDemoPlan.Gap(kind, first.Strength) + SpectatorReactionDemoPlan.MarginSeconds,
                    $"filter {filter}, {CrowdReactionKind.Name(kind)}: the next pass waits out the cooldown");
            }
        }
        // TakeCover alone is the case that needs more than one spacing: the pair is 16 s apart, the cooldown is 15 s.
        Assert.True(SpectatorReactionDemoPlan.CycleSeconds(SpectatorReactionDemoPlan.Pass(3)) >= 16.0 + 15.0 + 1.0);
        Assert.Equal(SpectatorReactionDemoPlan.SpacingSeconds, SpectatorReactionDemoPlan.CycleSeconds(Array.Empty<ReactionDemoStep>()));
    }

    // ---- proved against the real planner ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryStepOfEveryCyclePassesTheCooldownsTheRateAndTheSlotsAtGapScaleOne(int filter)
    {
        int perPass = SpectatorReactionDemoPlan.Pass(filter).Length;
        for (int seed = 1; seed <= 12; seed++)
        {
            var sim = new Sim(seed);
            sim.RunSteps(ReactionDemoMode.Cycle, filter, 3 * perPass, 3 * 400.0);
            Assert.Equal(3 * perPass, sim.Fired);
            Assert.Equal(sim.Fired, sim.Started);                                             // every request became a reaction
            foreach (int reason in AllReasons)
                Assert.True(sim.Planner.DroppedCount(reason) == 0, $"filter {filter}, seed {seed}: dropped {CrowdReactionDropReason.Name(reason)} {sim.Planner.DroppedCount(reason)} time(s)");
            Assert.False(sim.Planner.TryTakeDrop(out _));
            Assert.Equal(0, sim.Planner.Pending);
            Assert.Equal(0, sim.Queue.Stale);
            Assert.Equal(0, sim.Queue.Overflowed);
        }
    }

    [Fact]
    public void OneOncePassIsStartedCompleteAndTheDemoStopsAfterIt()
    {
        var sim = new Sim(5);
        int steps = SpectatorReactionDemoPlan.Pass(0).Length;
        sim.RunSteps(ReactionDemoMode.Once, 0, steps + 5, 600.0);
        Assert.Equal(steps, sim.Fired);                      // asked for more: Once stopped after the pass
        Assert.Equal(steps, sim.Started);
        Assert.False(sim.Demo.Running);
        foreach (int reason in AllReasons) Assert.Equal(0, sim.Planner.DroppedCount(reason));
    }

    [Fact]
    public void TheFirstPassPlaysTheVoicesItsStrengthsAskForAndDrawsFromTheBandsItIsMeantToShow()
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            var sim = new Sim(seed);
            var pass = SpectatorReactionDemoPlan.Pass(0);
            sim.RunSteps(ReactionDemoMode.Once, 0, pass.Length, 600.0);
            Assert.Equal(pass.Length, sim.Log.Count);
            for (int i = 0; i < pass.Length; i++)
            {
                var step = pass[i];
                var plan = sim.Log[i].Plan!;
                string where = $"seed {seed}, step {i} ({CrowdReactionKind.Name(step.Kind)} {step.Strength})";
                Assert.Equal(step.Kind, plan.Kind);
                Assert.Equal(CrowdReactionPlanner.VoicesFor(step.Strength), plan.Voices);       // no history yet: every rule holds, no voice is lost
                var tags = plan.Clips.Select(c => sim.Planner.Pool.At(c)!.Tag).ToArray();
                switch (i)
                {
                    case 0: case 1: // Approve below 0.8: the normal clips for every voice
                        Assert.True(tags.All(t => t is "praise" or "gloat"), where);
                        break;
                    case 2:         // Approve from 0.8: the first voice is a shout
                        Assert.Equal("shout", tags[0]);
                        Assert.True(tags.Skip(1).All(t => t is "praise" or "gloat"), where);
                        break;
                    case 3:         // Disapprove below 0.4: laments only
                        Assert.True(tags.All(t => t == "lament"), where);
                        break;
                    case 4: case 5: // from 0.4: laments and heckles
                        Assert.True(tags.All(t => t is "lament" or "heckle"), where);
                        break;
                    case 6:         // TakeCover without the line of fire: panic only
                        Assert.True(tags.All(t => t == "panic"), where);
                        break;
                    case 7:         // TakeCover directed: panic and the directed pleas
                        Assert.True(plan.Directed, where);
                        Assert.True(tags.All(t => t is "panic" or "directed"), where);
                        break;
                    case 8:
                        Assert.True(tags.All(t => t == "start"), where);
                        break;
                    case 9:
                        Assert.True(tags.All(t => t == "finish"), where);
                        break;
                }
            }
        }
    }

    [Fact]
    public void ALaterPassOfTheCycleStillPlaysEveryStartWithAllThreeVoicesAlthoughTheStartPoolHasOnlyFiveSentences()
    {
        // Start has five sentences and the last three may not repeat: after a three-voice Start only two others remain. The window shrinks instead of the next Start losing a voice
        // (before 2026-11-02 it played two voices for the rest of the raid), and no sentence is said twice inside one Start.
        for (int seed = 1; seed <= 12; seed++)
        {
            var sim = new Sim(seed);
            int perPass = SpectatorReactionDemoPlan.Pass(0).Length;
            sim.RunSteps(ReactionDemoMode.Cycle, 0, 3 * perPass, 3 * 400.0);
            var starts = sim.Log.Select(l => l.Plan!).Where(p => p.Kind == CrowdReactionKind.Start).ToArray();
            Assert.Equal(3, starts.Length);
            Assert.All(starts, p =>
            {
                Assert.Equal(3, p.Voices);
                Assert.Equal(3, p.Clips.Select(c => sim.Planner.Pool.At(c)!.Group).Distinct().Count());
            });
            Assert.Equal(0, starts[0].Level);
            Assert.All(starts.Skip(1), p => Assert.True(p.GroupsRelaxed));
            Assert.Equal(0, sim.Planner.DroppedCount(CrowdReactionDropReason.NoPool));
        }
    }

    // ---- the runner ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRunnerHandsOutOneDueStepAtATimeAfterTheFirstDelay()
    {
        var demo = new SpectatorReactionDemoPlan();
        var pass = SpectatorReactionDemoPlan.Pass(0);
        double t = 100.0;
        Assert.False(demo.TryNext(t, ReactionDemoMode.Cycle, 0, out _));                                         // begins; the first step is a delay away
        Assert.True(demo.Running);
        Assert.False(demo.TryNext(t + SpectatorReactionDemoPlan.FirstDelaySeconds - 0.01, ReactionDemoMode.Cycle, 0, out _));
        Assert.True(demo.TryNext(t + SpectatorReactionDemoPlan.FirstDelaySeconds, ReactionDemoMode.Cycle, 0, out var first));
        Assert.Equal((CrowdReactionKind.Approve, 0.30f), (first.Kind, first.Strength));
        Assert.False(demo.TryNext(t + SpectatorReactionDemoPlan.FirstDelaySeconds, ReactionDemoMode.Cycle, 0, out _));   // one per call, and the next is a spacing away
        Assert.True(demo.TryNext(t + SpectatorReactionDemoPlan.FirstDelaySeconds + pass[1].At, ReactionDemoMode.Cycle, 0, out var second));
        Assert.Equal(0.60f, second.Strength);
    }

    [Fact]
    public void CycleWrapsToTheNextPassAndOnceStopsAfterTheLastStep()
    {
        var cycle = new SpectatorReactionDemoPlan();
        var pass = SpectatorReactionDemoPlan.Pass(4);                      // Start alone: one step per pass
        Assert.False(cycle.TryNext(0, ReactionDemoMode.Cycle, 4, out _));
        double start = SpectatorReactionDemoPlan.FirstDelaySeconds;
        double length = SpectatorReactionDemoPlan.CycleSeconds(pass);
        Assert.True(cycle.TryNext(start, ReactionDemoMode.Cycle, 4, out var a));
        Assert.Equal(CrowdReactionKind.Start, a.Kind);
        Assert.False(cycle.TryNext(start + length - 0.01, ReactionDemoMode.Cycle, 4, out _));
        Assert.True(cycle.TryNext(start + length, ReactionDemoMode.Cycle, 4, out var b));
        Assert.Equal(CrowdReactionKind.Start, b.Kind);
        Assert.True(cycle.Running);

        var once = new SpectatorReactionDemoPlan();
        Assert.False(once.TryNext(0, ReactionDemoMode.Once, 4, out _));
        Assert.True(once.TryNext(start, ReactionDemoMode.Once, 4, out _));
        Assert.False(once.Running);
        Assert.False(once.TryNext(start + 10 * length, ReactionDemoMode.Once, 4, out _));
        // Off, then Once again: a fresh pass
        Assert.False(once.TryNext(start + 11 * length, ReactionDemoMode.Off, 4, out _));
        Assert.False(once.TryNext(start + 12 * length, ReactionDemoMode.Once, 4, out _));
        Assert.True(once.TryNext(start + 12 * length + SpectatorReactionDemoPlan.FirstDelaySeconds, ReactionDemoMode.Once, 4, out _));
    }

    [Fact]
    public void AChangedModeOrKindBeginsANewPassAndAnOutOfRangeKindMeansAll()
    {
        var demo = new SpectatorReactionDemoPlan();
        Assert.False(demo.TryNext(0, ReactionDemoMode.Cycle, 2, out _));
        Assert.True(demo.TryNext(1, ReactionDemoMode.Cycle, 2, out var a));
        Assert.Equal(CrowdReactionKind.Disapprove, a.Kind);
        Assert.False(demo.TryNext(2, ReactionDemoMode.Cycle, 5, out _));                         // another kind: new pass, new first delay
        Assert.True(demo.TryNext(3, ReactionDemoMode.Cycle, 5, out var b));
        Assert.Equal(CrowdReactionKind.Finish, b.Kind);
        Assert.False(demo.TryNext(10, ReactionDemoMode.Once, 99, out _));                        // 99 is not a kind: all of them
        Assert.Equal(SpectatorReactionDemoPlan.Pass(0).Length, demo.Steps.Length);
        Assert.False(demo.TryNext(11, ReactionDemoMode.Off, 5, out _));
        Assert.False(demo.Running);
    }

    [Fact]
    public void AStepThatIsFarTooLateIsNotFiredInABurstWithItsSuccessors()
    {
        var demo = new SpectatorReactionDemoPlan();
        Assert.False(demo.TryNext(0, ReactionDemoMode.Cycle, 0, out _));
        // the game stalled for a minute: step 0 is due, step 1 must still be a spacing later than it, not due at once
        Assert.True(demo.TryNext(60, ReactionDemoMode.Cycle, 0, out var first));
        Assert.Equal(0.30f, first.Strength);
        Assert.False(demo.TryNext(60.5, ReactionDemoMode.Cycle, 0, out _));
        Assert.True(demo.TryNext(60 + SpectatorReactionDemoPlan.SpacingSeconds, ReactionDemoMode.Cycle, 0, out var second));
        Assert.Equal(0.60f, second.Strength);
    }

    // ---- the idle check of the reaction section ----------------------------------------------------------------------------------------

    [Fact]
    public void HasWaitingIsAVolatileMirrorOfTheRingThatTheFrameReadsWithoutTheLock()
    {
        var queue = new CrowdReactionQueue();
        queue.SetKinds(62);
        Assert.False(queue.HasWaiting);
        int handle = queue.Claim("plugin");
        Assert.True(queue.React(handle, CrowdReactionKind.Approve, 0.5f));
        Assert.True(queue.HasWaiting);
        var buffer = new CrowdReactionRequest[CrowdReactionQueue.Capacity];
        Assert.Equal(1, queue.Drain(buffer, Stopwatch.GetTimestamp()));
        Assert.False(queue.HasWaiting);
        Assert.False(queue.React(handle, CrowdReactionKind.Lull, 0.5f));                           // refused: nothing waits
        Assert.False(queue.HasWaiting);
    }

    [Fact]
    public void HasWaitingFollowsAStaleDropAnOverflowAKindsWithdrawalAndAReset()
    {
        var queue = new CrowdReactionQueue();
        queue.SetKinds(62);
        int handle = queue.Claim("plugin");
        long old = Stopwatch.GetTimestamp() - Ticks(10.0);
        Assert.True(queue.React(handle, CrowdReactionKind.Start, 1f, false, old));
        Assert.True(queue.HasWaiting);
        var buffer = new CrowdReactionRequest[CrowdReactionQueue.Capacity];
        Assert.Equal(0, queue.Drain(buffer, Stopwatch.GetTimestamp()));                           // older than 2 s: dropped, and the mirror says empty
        Assert.False(queue.HasWaiting);

        for (int i = 0; i < CrowdReactionQueue.Capacity + 4; i++) Assert.True(queue.React(handle, CrowdReactionKind.Approve, 0.5f));
        Assert.True(queue.HasWaiting);
        Assert.Equal(4, queue.Overflowed);
        Assert.Equal(CrowdReactionQueue.Capacity, queue.Drain(buffer, Stopwatch.GetTimestamp()));
        Assert.False(queue.HasWaiting);

        Assert.True(queue.React(handle, CrowdReactionKind.Approve, 0.5f));
        queue.SetKinds(0);                                                                         // the key went off: the ring is emptied
        Assert.False(queue.HasWaiting);
        queue.SetKinds(62);
        Assert.True(queue.React(handle, CrowdReactionKind.Approve, 0.5f));
        queue.Reset();                                                                             // the crowd was removed
        Assert.False(queue.HasWaiting);
        Assert.Equal(0, queue.Count);
    }
}
