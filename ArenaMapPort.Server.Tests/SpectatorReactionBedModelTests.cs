using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The Unity-free rules of the roar layer (SpectatorReactionBedModel.cs, docs/progress/spectator-audio.md "Roar beds"): the kind-to-bed table, the roar envelope at several step sizes,
/// the amplitude, hold and volume laws, the clip picker, the manifest's <c>reaction_beds</c> and its independence of the other three sets, and (where the extractor's ignored output has the
/// beds) the real files. SpectatorReactionBeds.cs is Unity glue; its source pins live with the other reaction pins.
/// </summary>
public sealed class SpectatorReactionBedModelTests
{
    // ---- the kind table --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheKindTableSendsTheApprovingKindsToTheCheerBedAndDisapproveToTheAngryBed()
    {
        Assert.Equal(SpectatorReactionBedKind.Cheer, SpectatorReactionBedKind.For(CrowdReactionKind.Approve));
        Assert.Equal(SpectatorReactionBedKind.Cheer, SpectatorReactionBedKind.For(CrowdReactionKind.Start));
        Assert.Equal(SpectatorReactionBedKind.Cheer, SpectatorReactionBedKind.For(CrowdReactionKind.Finish));
        Assert.Equal(SpectatorReactionBedKind.Angry, SpectatorReactionBedKind.For(CrowdReactionKind.Disapprove));
        Assert.Equal(SpectatorReactionBedKind.None, SpectatorReactionBedKind.For(CrowdReactionKind.TakeCover));
        Assert.Equal(SpectatorReactionBedKind.None, SpectatorReactionBedKind.For(CrowdReactionKind.Lull));
        Assert.Equal(SpectatorReactionBedKind.None, SpectatorReactionBedKind.For(CrowdReactionKind.PurifiedTeam));
        foreach (int kind in new[] { int.MinValue, -1, 0, 8, 31, 32, 33, int.MaxValue })
            Assert.Equal(SpectatorReactionBedKind.None, SpectatorReactionBedKind.For(kind));
        Assert.Equal((1, 2), (SpectatorReactionBedKind.Cheer, SpectatorReactionBedKind.Angry));
    }

    [Fact]
    public void TheBedKindsKnowTheirManifestWordBankAndTrigger()
    {
        Assert.Equal(("cheer", "angry", "none"), (SpectatorReactionBedKind.Name(1), SpectatorReactionBedKind.Name(2), SpectatorReactionBedKind.Name(0)));
        Assert.Equal(("Cheer", "Angry", (string?)null), (SpectatorReactionBedKind.BankName(1), SpectatorReactionBedKind.BankName(2), SpectatorReactionBedKind.BankName(0)));
        Assert.Equal((1119, 1121, 0), (SpectatorReactionBedKind.TriggerOf(1), SpectatorReactionBedKind.TriggerOf(2), SpectatorReactionBedKind.TriggerOf(0)));
        Assert.Equal((1, 2, 0, 0, 0), (SpectatorReactionBedKind.Parse("cheer"), SpectatorReactionBedKind.Parse("angry"), SpectatorReactionBedKind.Parse("Cheer"),
            SpectatorReactionBedKind.Parse(""), SpectatorReactionBedKind.Parse(null!)));
        // the triggers are the ones of the reaction set's banks
        Assert.Equal(SpectatorReactionTags.TriggerOf("Cheer"), SpectatorReactionBedKind.TriggerOf(1));
        Assert.Equal(SpectatorReactionTags.TriggerOf("Angry"), SpectatorReactionBedKind.TriggerOf(2));
    }

    // ---- amplitude, hold, volume -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheAmplitudeIsTheFloorPlusTheStrengthHeldToZeroToOne()
    {
        Assert.Equal(0.35f, SpectatorReactionBedModel.Amplitude(0f), 6);
        Assert.Equal(0.675f, SpectatorReactionBedModel.Amplitude(0.5f), 6);
        Assert.Equal(1f, SpectatorReactionBedModel.Amplitude(1f), 6);
        Assert.Equal(0.35f + 0.65f * 0.74f, SpectatorReactionBedModel.Amplitude(0.74f), 6);
        Assert.Equal(0.35f, SpectatorReactionBedModel.Amplitude(float.NaN), 6);
        Assert.Equal(0.35f, SpectatorReactionBedModel.Amplitude(-3f), 6);
        Assert.Equal(0.35f, SpectatorReactionBedModel.Amplitude(float.NegativeInfinity), 6);
        Assert.Equal(1f, SpectatorReactionBedModel.Amplitude(9f), 6);
        Assert.Equal(1f, SpectatorReactionBedModel.Amplitude(float.PositiveInfinity), 6);
    }

    [Fact]
    public void TheHoldIsTheReactionsLengthHeldToOneAndAHalfToFiveSeconds()
    {
        Assert.Equal(1.5f, SpectatorReactionBedModel.HoldSeconds(0), 6);
        Assert.Equal(1.5f, SpectatorReactionBedModel.HoldSeconds(1.49), 6);
        Assert.Equal(1.5f, SpectatorReactionBedModel.HoldSeconds(1.5), 6);
        Assert.Equal(3.2f, SpectatorReactionBedModel.HoldSeconds(3.2), 6);
        Assert.Equal(5f, SpectatorReactionBedModel.HoldSeconds(5), 6);
        Assert.Equal(5f, SpectatorReactionBedModel.HoldSeconds(50), 6);
        Assert.Equal(1.5f, SpectatorReactionBedModel.HoldSeconds(-4), 6);
        Assert.Equal(1.5f, SpectatorReactionBedModel.HoldSeconds(double.NaN), 6);
        Assert.Equal(1.5f, SpectatorReactionBedModel.HoldSeconds(double.NegativeInfinity), 6);
        Assert.Equal(5f, SpectatorReactionBedModel.HoldSeconds(double.PositiveInfinity), 6);
    }

    [Fact]
    public void TheVolumeLawPlaysTheRoarAtItsExportedLevelAtKeyOneAndFullGainAndNeverAboveOne()
    {
        Assert.Equal(1f / SpectatorCrowdBedModel.ClipGain, SpectatorReactionBedModel.SourceVolume(1f, 1f), 6);
        Assert.Equal(1f / 3.2f, SpectatorReactionBedModel.SourceVolume(1f, 1f), 6);      // the clip floats carry the 3.2: 3.2 x 0.3125 = 1
        Assert.Equal(0f, SpectatorReactionBedModel.SourceVolume(0f, 1f));
        Assert.Equal(0f, SpectatorReactionBedModel.SourceVolume(1f, 0f));
        Assert.Equal(0.5f / 3.2f, SpectatorReactionBedModel.SourceVolume(1f, 0.5f), 6);
        Assert.Equal(2f / 3.2f, SpectatorReactionBedModel.SourceVolume(2f, 1f), 6);
        Assert.Equal(2f / 3.2f, SpectatorReactionBedModel.SourceVolume(9f, 1f), 6);      // the key is held to 0..2
        Assert.Equal(1f / 3.2f, SpectatorReactionBedModel.SourceVolume(1f, 7f), 6);      // the gain to 0..1
        Assert.Equal(0f, SpectatorReactionBedModel.SourceVolume(1f, float.NaN));
        Assert.Equal(1f / 3.2f, SpectatorReactionBedModel.SourceVolume(float.NaN, 1f), 6);   // an unusable key counts as the default
        Assert.Equal(0f, SpectatorReactionBedModel.SourceVolume(-1f, 1f));
        for (float key = -1f; key <= 3f; key += 0.25f)
            for (float gain = -1f; gain <= 2f; gain += 0.25f)
                Assert.InRange(SpectatorReactionBedModel.SourceVolume(key, gain), 0f, 1f);
    }

    [Fact]
    public void TheDefaultsAreOnAndAtVolumeTwoTheTopOfTheRange()
    {
        Assert.True(SpectatorReactionBedModel.ReactionBedsDefault);
        Assert.Equal(2f, SpectatorReactionBedModel.ReactionBedVolumeDefault);
        Assert.Equal(SpectatorCrowdBedModel.MaxVolume, SpectatorReactionBedModel.ReactionBedVolumeDefault);
        Assert.Equal(0.625f, SpectatorReactionBedModel.SourceVolume(2f, 1f), 5);
    }

    [Fact]
    public void TheReSeedMovesExactlyTheOldDefaultOnce()
    {
        Assert.Equal(1, ReactionBedDefaults.Revision);
        Assert.True(ReactionBedDefaults.VolumeMoves(0, 1f));
        foreach (var v in new[] { 2f, 0.8f, 0f, 1.5f, float.NaN })
            Assert.False(ReactionBedDefaults.VolumeMoves(0, v));
        foreach (var v in new[] { 1f, 2f, 0.8f, 0f, float.NaN })
            Assert.False(ReactionBedDefaults.VolumeMoves(1, v));
    }

    // ---- the envelope ----------------------------------------------------------------------------------------------------------------

    /// <summary>The gain a trigger at 0 with amplitude 0.8 and hold 2 s should have at time t: 0.3 s attack, hold to 2.3 s, 2 s release to 4.3 s.</summary>
    private static double Expected(double t) => t < 0.3 ? 0.8 * t / 0.3 : t < 2.3 ? 0.8 : t < 4.3 ? 0.8 * (1 - (t - 2.3) / 2.0) : 0.0;

    [Theory]
    [InlineData(1 / 120.0)]
    [InlineData(1 / 60.0)]
    [InlineData(1 / 10.0)]
    [InlineData(0.37)]
    public void TheEnvelopeFollowsTheSameShapeAtAnyStepSize(double dt)
    {
        const double t0 = 100.0;
        var e = new RoarEnvelope();
        Assert.Equal(RoarPhase.Idle, e.Phase);
        e.Trigger(0.8f, 2f, t0);
        Assert.Equal(RoarPhase.Attack, e.Phase);
        double idleAt = -1;
        float previous = 0f;
        bool rose = false, fell = false;
        for (int i = 1; i * dt < 6.0; i++)
        {
            double t = i * dt;
            float gain = e.Step(t0 + t, out _);
            Assert.Equal(Expected(t), gain, 3);
            Assert.InRange(gain, 0f, 0.8f + 1e-6f);
            if (gain > previous) { Assert.False(fell, "the gain rose again after it began to fall"); rose = true; }
            if (gain < previous) fell = true;
            previous = gain;
            if (e.Phase == RoarPhase.Idle && idleAt < 0) idleAt = t;
        }
        Assert.True(rose && fell);
        Assert.InRange(idleAt, 4.3 - 1e-9, 4.3 + dt + 1e-9);          // idle at 4.3 s, at the first step that reaches it
        Assert.False(e.Active);
        Assert.Equal(0f, e.Gain);
    }

    [Fact]
    public void ThePhasesFollowEachOtherAttackHoldReleaseIdle()
    {
        var e = new RoarEnvelope();
        e.Trigger(1f, 2f, 0);
        var seen = new List<RoarPhase> { e.Phase };
        for (double t = 0; t < 5; t += 1 / 60.0)
        {
            e.Step(t, out _);
            if (e.Phase != seen[^1]) seen.Add(e.Phase);
        }
        Assert.Equal(new[] { RoarPhase.Attack, RoarPhase.Hold, RoarPhase.Release, RoarPhase.Idle }, seen);
        Assert.Equal(0f, e.Amplitude);                                 // the amplitude is cleared at idle
    }

    [Fact]
    public void AStepThatSpansTheWholeRoarArrivesAtIdleWithoutVisitingTheOtherPhases()
    {
        var e = new RoarEnvelope();
        e.Trigger(0.9f, 3f, 5);
        float gain = e.Step(500, out bool changed);
        Assert.Equal(0f, gain);
        Assert.Equal(RoarPhase.Idle, e.Phase);
        Assert.False(changed);                                         // 0 at the trigger's last report, 0 now
        var f = new RoarEnvelope();
        f.Trigger(0.9f, 3f, 5);
        f.Step(5.2, out _);
        f.Step(500, out changed);
        Assert.True(changed);                                          // it was above 0 at the previous step
        Assert.Equal(RoarPhase.Idle, f.Phase);
    }

    [Fact]
    public void StepReportsAChangeOnlyWhenTheGainMoved()
    {
        var e = new RoarEnvelope();
        Assert.Equal(0f, e.Step(1, out bool changed));
        Assert.False(changed);                                         // idle stays idle
        e.Trigger(0.6f, 2f, 1);
        e.Step(1.1, out changed); Assert.True(changed);                // rising
        e.Step(1.2, out changed); Assert.True(changed);
        e.Step(1.5, out changed); Assert.True(changed);                // reaches the amplitude
        e.Step(2.0, out changed); Assert.False(changed);               // holds
        e.Step(2.5, out changed); Assert.False(changed);
        float held = e.Step(3.2, out changed);
        Assert.False(changed);
        Assert.Equal(0.6f, held, 6);
        e.Step(3.5, out changed); Assert.True(changed);                // 1 + 0.3 + 2 = 3.3 s is the end of the hold: now falling
        e.Step(5.4, out changed); Assert.True(changed);
        e.Step(6.0, out changed); Assert.False(changed);
        Assert.Equal(RoarPhase.Idle, e.Phase);
    }

    [Fact]
    public void AnAttackStartsFromTheCurrentGainAndNeverJumps()
    {
        var e = new RoarEnvelope();
        e.Trigger(0.8f, 3f, 0);
        e.Step(0.15, out _);
        Assert.Equal(0.4f, e.Gain, 5);
        e.Trigger(0.8f, 3f, 0.15);                                     // a retrigger half way up
        Assert.Equal(0.4f, e.Gain, 5);                                 // no jump at the trigger
        Assert.Equal(0.4f, e.Step(0.15, out _), 5);
        Assert.Equal(0.4f + 0.4f * 0.5f, e.Step(0.30, out _), 5);      // a new linear attack of 0.3 s from 0.4: half way at 0.15 s later
        Assert.Equal(0.8f, e.Step(0.45, out _), 5);
    }

    [Fact]
    public void ARetriggerWhileItPlaysKeepsTheLargerAmplitudeAndTheLaterHoldEnd()
    {
        var e = new RoarEnvelope();
        e.Trigger(0.9f, 4f, 0);                                        // hold ends at 4.3
        e.Step(1, out _);
        Assert.Equal(0.9f, e.Gain, 6);
        e.Trigger(0.5f, 1.5f, 1);                                      // smaller and shorter (ends at 2.8): neither takes over
        Assert.Equal(0.9f, e.Amplitude, 6);
        Assert.Equal(4.3, e.HoldEnd, 6);
        Assert.Equal(0.9f, e.Step(1.5, out _), 6);                     // no dip towards the smaller amplitude
        Assert.Equal(0.9f, e.Step(4.0, out _), 6);
        e.Trigger(0.95f, 5f, 4.0);                                     // larger and longer: both take over
        Assert.Equal(0.95f, e.Amplitude, 6);
        Assert.Equal(9.3, e.HoldEnd, 6);
        Assert.Equal(0.9f, e.Gain, 6);                                 // continuous at the trigger
        Assert.Equal(0.95f, e.Step(4.31, out _), 5);
        Assert.Equal(RoarPhase.Hold, e.Phase);
        Assert.Equal(0.95f, e.Step(9.0, out _), 5);
        e.Step(9.3 + 2.0 + 0.01, out _);
        Assert.Equal(RoarPhase.Idle, e.Phase);
    }

    [Fact]
    public void ARetriggerDuringTheReleaseRisesFromWhereTheGainIsAndTakesTheNewAmplitudeAsItIs()
    {
        var e = new RoarEnvelope();
        e.Trigger(0.8f, 2f, 0);                                        // release from 2.3 to 4.3
        float falling = e.Step(3.3, out _);
        Assert.Equal(0.4f, falling, 5);
        e.Trigger(0.6f, 1.5f, 3.3);                                    // below the old amplitude, above the current gain
        Assert.Equal(RoarPhase.Attack, e.Phase);
        Assert.Equal(0.4f, e.Gain, 5);                                 // no jump
        Assert.Equal(0.5f, e.Step(3.45, out _), 5);                    // half way from 0.4 to 0.6
        Assert.Equal(0.6f, e.Step(3.6, out _), 5);
        Assert.Equal(0.6f, e.Amplitude, 6);                            // not the 0.8 of the roar that was dying
        Assert.Equal(3.3 + 0.3 + 1.5, e.HoldEnd, 6);                   // the old hold end (2.3) is past and does not count
    }

    [Fact]
    public void ACutReleasesFromTheCurrentGainInHalfASecondFromAnyPhase()
    {
        // from the hold
        var e = new RoarEnvelope();
        e.Trigger(0.8f, 5f, 0);
        e.Step(2, out _);
        e.Cut(2);
        Assert.Equal(RoarPhase.Release, e.Phase);
        Assert.Equal(0.8f, e.Gain, 6);                                 // no jump at the cut
        Assert.Equal(0.4f, e.Step(2.25, out _), 5);
        Assert.Equal(0f, e.Step(2.51, out _), 5);
        Assert.Equal(RoarPhase.Idle, e.Phase);
        // from the attack
        var f = new RoarEnvelope();
        f.Trigger(0.8f, 5f, 0);
        f.Step(0.15, out _);
        f.Cut(0.15);
        Assert.Equal(0.4f, f.Gain, 5);
        Assert.Equal(0.2f, f.Step(0.4, out _), 5);
        f.Step(0.66, out _);
        Assert.Equal(RoarPhase.Idle, f.Phase);
        // from a slow release that still has more than half a second to go: the cut is faster
        var g = new RoarEnvelope();
        g.Trigger(0.8f, 1.5f, 0);                                      // release from 1.8 to 3.8
        g.Step(2.3, out _);
        float before = g.Gain;
        g.Cut(2.3);
        Assert.Equal(before, g.Gain, 6);
        g.Step(2.81, out _);
        Assert.Equal(RoarPhase.Idle, g.Phase);
        // a release that has less than half a second to go is left alone
        var h = new RoarEnvelope();
        h.Trigger(0.8f, 1.5f, 0);
        h.Step(3.5, out _);                                            // 0.3 s from the end
        float rest = h.Gain;
        h.Cut(3.5);
        Assert.Equal(rest, h.Step(3.5, out _), 6);
        h.Step(3.81, out _);
        Assert.Equal(RoarPhase.Idle, h.Phase);
    }

    [Fact]
    public void ACutDoesNothingWhileIdleAndATriggerAfterACutRisesFromTheFallingGain()
    {
        var e = new RoarEnvelope();
        e.Cut(5);
        Assert.Equal((RoarPhase.Idle, 0f), (e.Phase, e.Gain));
        e.Trigger(0.8f, 3f, 5);
        e.Step(6, out _);
        e.Cut(6);
        e.Step(6.25, out _);
        Assert.Equal(0.4f, e.Gain, 5);
        e.Trigger(0.7f, 1.5f, 6.25);                                   // the other kind's cut source is not retriggered in practice, but the envelope copes
        Assert.Equal(0.4f, e.Gain, 5);
        Assert.Equal(0.7f, e.Step(6.56, out _), 5);
        Assert.Equal(RoarPhase.Hold, e.Phase);
    }

    [Fact]
    public void StopReturnsToIdleAtOnceAndBadInputDoesNothingHarmful()
    {
        var e = new RoarEnvelope();
        e.Trigger(0.8f, 3f, 0);
        e.Step(1, out _);
        e.Stop();
        Assert.Equal((RoarPhase.Idle, 0f, false), (e.Phase, e.Gain, e.Active));
        Assert.Equal(0f, e.Step(1.1, out bool changed));
        Assert.False(changed);                                         // Stop reports as the new baseline: the glue stops its source itself
        e.Trigger(0f, 3f, 2);
        e.Trigger(float.NaN, 3f, 2);
        e.Trigger(-1f, 3f, 2);
        Assert.Equal(RoarPhase.Idle, e.Phase);                         // a non-positive amplitude starts nothing
        e.Trigger(5f, float.NaN, 2);                                   // amplitude held to 1, a NaN hold counts as 0
        Assert.Equal(1f, e.Amplitude);
        Assert.Equal(2.3, e.HoldEnd, 6);
        e.Step(2.3, out _);
        Assert.Equal(1f, e.Gain, 6);
        e.Step(double.NaN, out _);                                     // a NaN clock is the previous time
        Assert.Equal(1f, e.Gain, 6);
        e.Step(1.0, out _);                                            // the clock never runs back
        Assert.Equal(1f, e.Gain, 6);
        var huge = new RoarEnvelope();
        huge.Trigger(0.5f, float.PositiveInfinity, 0);
        Assert.Equal(60.3, huge.HoldEnd, 4);
    }

    [Fact]
    public void TheEnvelopeAddsNothingToTheGcHeap()
    {
        var e = new RoarEnvelope();
        Drive(e, 2000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Drive(e, 50000);
        // the measured loop lives in an optimised helper, as in SpectatorCrowdBedModelTests: a hot tier-0 loop would charge OSR compilation to this thread
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 512L);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void Drive(RoarEnvelope e, int frames)
    {
        double now = 0;
        for (int i = 0; i < frames; i++)
        {
            now += 0.016;
            if (i % 97 == 0) e.Trigger((i % 10) / 10f + 0.1f, 1.5f + i % 4, now);
            if (i % 211 == 0) e.Cut(now);
            e.Step(now, out _);
        }
    }

    // ---- the clip table and the picker -----------------------------------------------------------------------------------------------

    private static JObject ClipJson(string name, int frames = 7 * 44100) => JObject.FromObject(new
    {
        name, file = name + ".wav", channels = 1, sample_rate = 44100, frames, bytes = 44 + frames * 2, sha256 = new string('c', 64), peak = 0.2, rms = 0.025,
        seam_crossfade_ms = 500, source = "StreamingAssets/Windows/assets/content/audio/phrases/ambient_voice.bundle", source_id = 4455667788L,
        processing = new { original_channels = 2, original_rate = 44100, original_frames = 308700, original_duration = 7.0, original_peak = 0.5, original_rms = 0.07, downmix = "mean", gain_db = -9.5, crossfade_frames = 22050 },
    });

    /// <summary>The shape tools/extract/extract_spectator_audio.py writes for <c>reaction_beds</c>: Cheer then Angry, three clips each, sorted by name.</summary>
    internal static JObject BedSetJson()
    {
        string[] cheer = { "arena_fans_ambient_cheer", "arena_fans_ambient_cheer2", "arena_fans_ambient_cheer3" };
        string[] angry = { "arena_fans_ambient_angry", "arena_fans_ambient_angry2", "arena_fans_ambient_angry3" };
        return JObject.FromObject(new
        {
            schema = 1, source = "StreamingAssets/Windows/assets/content/audio/phrases/ambient_voice.bundle", target_rms_db = -32.0,
            banks = new[]
            {
                new { bank = "Cheer", trigger = 1119, kind = "cheer", clips = cheer.Select(n => ClipJson(n)).ToArray() },
                new { bank = "Angry", trigger = 1121, kind = "angry", clips = angry.Select(n => ClipJson(n, 8 * 44100)).ToArray() },
            },
        });
    }

    private static SpectatorReactionBedSet Set(Action<JObject>? change = null)
    {
        var json = BedSetJson();
        change?.Invoke(json);
        return json.ToObject<SpectatorReactionBedSet>()!;
    }

    [Fact]
    public void TheTableHoldsThreeClipsPerKindInManifestOrderAndNothingForNone()
    {
        var set = Set();
        set.Validate();
        var table = new RoarClipTable(set);
        Assert.Equal(new[] { "arena_fans_ambient_cheer", "arena_fans_ambient_cheer2", "arena_fans_ambient_cheer3" }, table.Clips(SpectatorReactionBedKind.Cheer).Select(c => c.name));
        Assert.Equal(new[] { "arena_fans_ambient_angry", "arena_fans_ambient_angry2", "arena_fans_ambient_angry3" }, table.Clips(SpectatorReactionBedKind.Angry).Select(c => c.name));
        Assert.Empty(table.Clips(SpectatorReactionBedKind.None));
        Assert.Empty(table.Clips(7));
        Assert.Empty(table.Clips(-1));
        Assert.True(table.Has(1) && table.Has(2) && !table.Has(0) && !table.Has(3));
        Assert.Equal(3, table.Count(2));
        var empty = new RoarClipTable(null!);
        Assert.False(empty.Has(1));
    }

    [Fact]
    public void ThePickerNeverPlaysTheLastClipOfAKindAgainAndKeepsTheKindsApart()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var picker = new RoarClipPicker(new RoarClipTable(Set()), seed);
            int lastCheer = -1, lastAngry = -1;
            var seenCheer = new HashSet<int>();
            for (int i = 0; i < 300; i++)
            {
                int cheer = picker.Pick(SpectatorReactionBedKind.Cheer);
                Assert.InRange(cheer, 0, 2);
                Assert.NotEqual(lastCheer, cheer);
                lastCheer = cheer; seenCheer.Add(cheer);
                if (i % 3 == 0)                                                   // an Angry pick between two Cheer picks does not count as the last Cheer
                {
                    int angry = picker.Pick(SpectatorReactionBedKind.Angry);
                    Assert.InRange(angry, 0, 2);
                    Assert.NotEqual(lastAngry, angry);
                    lastAngry = angry;
                }
            }
            Assert.Equal(3, seenCheer.Count);                                     // all three clips come up over time
        }
    }

    [Fact]
    public void ThePickerIsDeterministicPerSeedAndHandlesOneClipAndNoClip()
    {
        var table = new RoarClipTable(Set());
        var a = new RoarClipPicker(table, 1234); var b = new RoarClipPicker(table, 1234); var c = new RoarClipPicker(table, 99);
        var sa = new List<int>(); var sb = new List<int>(); var sc = new List<int>();
        for (int i = 0; i < 40; i++)
        {
            sa.Add(a.Pick(1) * 1000 + a.StartOffset(7 * 44100) % 1000); sb.Add(b.Pick(1) * 1000 + b.StartOffset(7 * 44100) % 1000); sc.Add(c.Pick(1) * 1000 + c.StartOffset(7 * 44100) % 1000);
        }
        Assert.Equal(sa, sb);
        Assert.NotEqual(sa, sc);

        var one = new RoarClipPicker(new RoarClipTable(Set(j => ((JArray)j["banks"]![0]!["clips"]!).RemoveAt(2))), 5);
        var two = new RoarClipPicker(new RoarClipTable(Set(j => { var clips = (JArray)j["banks"]![0]!["clips"]!; clips.RemoveAt(2); clips.RemoveAt(1); })), 5);
        Assert.InRange(one.Pick(1), 0, 1);
        for (int i = 0; i < 20; i++) Assert.Equal(0, two.Pick(1));                // one clip: it repeats, the rule cannot hold
        Assert.Equal(-1, two.Pick(0));
        Assert.Equal(-1, two.Pick(3));
        Assert.Equal(-1, two.Pick(-1));
        var none = new RoarClipPicker(new RoarClipTable(null!), 1);
        Assert.Equal(-1, none.Pick(1));
    }

    [Fact]
    public void TheStartOffsetsLieInsideTheClipAndSpreadOverIt()
    {
        var picker = new RoarClipPicker(new RoarClipTable(Set()), 77);
        const int frames = 7 * 44100;
        var seen = new HashSet<int>();
        int min = int.MaxValue, max = int.MinValue;
        for (int i = 0; i < 1000; i++)
        {
            int offset = picker.StartOffset(frames);
            Assert.InRange(offset, 0, frames - 1);
            seen.Add(offset); min = Math.Min(min, offset); max = Math.Max(max, offset);
        }
        Assert.True(seen.Count > 900, "the stands would start in step");
        Assert.True(min < frames / 10 && max > frames * 9 / 10, "the offsets do not cover the clip");
        Assert.Equal(0, picker.StartOffset(0));
        Assert.Equal(0, picker.StartOffset(1));
        Assert.Equal(0, picker.StartOffset(-5));
    }

    // ---- the manifest's reaction_beds -------------------------------------------------------------------------------------------------

    private static JObject CallClip(int i) => JObject.FromObject(new
    {
        name = $"arena_fan7_participating_{i:00}", file = $"arena_fan7_participating_{i:00}.wav", channels = 1, sample_rate = 44100,
        frames = 44100, bytes = 88244, sha256 = new string('a', 64),
    });

    private static JObject Manifest(bool calls = true, bool bed = true, bool reactions = true, bool roar = true)
    {
        var root = JObject.FromObject(new { schema = 1, bank = "Participating", trigger = 1123, source_bundle = "x.bundle", external_entries_omitted = 9 });
        if (calls) root["clips"] = new JArray(Enumerable.Range(0, 13).Select(CallClip));
        if (bed)
            root["bed"] = JObject.FromObject(new
            {
                name = "arena_fans_ambient_neutral", file = "arena_fans_ambient_neutral.wav", channels = 1, sample_rate = 44100, frames = 3505950, bytes = 7011944,
                sha256 = new string('b', 64), seam_crossfade_ms = 500,
            });
        if (reactions) root["reactions"] = JObject.FromObject(ReactionSetBuilder.PhaseA());
        if (roar) root["reaction_beds"] = BedSetJson();
        return root;
    }

    private static SpectatorAudioManifest Parse(JObject json) => JsonConvert.DeserializeObject<SpectatorAudioManifest>(json.ToString())!;

    private static void AssertOnly(SpectatorAudioManifest m, bool calls, bool bed, bool reactions, bool roar)
    {
        void Check(bool expectedValid, Action validate)
        {
            if (expectedValid) validate(); else Assert.Throws<InvalidDataException>(validate);
        }
        Check(calls, m.Validate);
        Check(bed, m.ValidateBed);
        Check(reactions, m.ValidateReactions);
        Check(roar, m.ValidateReactionBeds);
    }

    [Fact]
    public void AManifestWithRoarBedsParsesAndEverySetValidatesOnItsOwn()
    {
        var m = Parse(Manifest());
        AssertOnly(m, true, true, true, true);
        Assert.Equal(-32f, m.reaction_beds.target_rms_db);
        Assert.Equal(2, m.reaction_beds.banks.Length);
        Assert.Equal(new[] { 3, 3 }, m.reaction_beds.banks.Select(b => b.clips.Length));
        Assert.Equal(7 * 44100, m.reaction_beds.banks[0].clips[0].frames);
    }

    [Fact]
    public void AManifestWithoutRoarBedsIsStillValidAndHasNone()
    {
        var m = Parse(Manifest(roar: false));
        Assert.Null(m.reaction_beds);
        AssertOnly(m, true, true, true, false);
        Assert.Throws<InvalidDataException>(m.ValidateReactionBeds);
    }

    [Fact]
    public void ADamagedRoarSetLeavesTheCallsTheBedAndTheReactionsValid()
    {
        var json = Manifest();
        json["reaction_beds"]!["banks"]![0]!["clips"]![0]!["sha256"] = "XYZ";
        AssertOnly(Parse(json), true, true, true, false);
        var noBanks = Manifest();
        noBanks["reaction_beds"]!["banks"] = JValue.CreateNull();
        AssertOnly(Parse(noBanks), true, true, true, false);
    }

    [Fact]
    public void ADamagedCallSetOrBedOrReactionSetLeavesTheRoarBedsValid()
    {
        var calls = Manifest();
        calls["clips"]![0]!["file"] = "../outside.wav";
        AssertOnly(Parse(calls), false, true, true, true);
        AssertOnly(Parse(Manifest(calls: false)), false, true, true, true);

        var bed = Manifest();
        bed["bed"]!["frames"] = 5;
        AssertOnly(Parse(bed), true, false, true, true);
        AssertOnly(Parse(Manifest(bed: false)), true, false, true, true);

        var reactions = Manifest();
        reactions["reactions"]!["schema"] = 2;
        AssertOnly(Parse(reactions), true, true, false, true);
        AssertOnly(Parse(Manifest(reactions: false)), true, true, false, true);

        var everything = Manifest(calls: false, bed: false, reactions: false);
        AssertOnly(Parse(everything), false, false, false, true);
    }

    [Theory]
    [InlineData("schema", 2)]
    [InlineData("target_rms_db", 0.0)]
    [InlineData("target_rms_db", -90.0)]
    public void ARoarSetOfAnotherSchemaOrAbsurdLevelIsRefused(string field, object value)
    {
        var set = Set(j => j[field] = JToken.FromObject(value));
        Assert.Throws<InvalidDataException>(set.Validate);
    }

    [Fact]
    public void ARoarSetNeedsTheSamePackageIdentityAsTheOtherSets()
    {
        foreach (var (field, value) in new (string, object)[] { ("schema", 2), ("trigger", 1122), ("bank", "Cheer") })
        {
            var json = Manifest();
            json[field] = JToken.FromObject(value);
            Assert.Throws<InvalidDataException>(Parse(json).ValidateReactionBeds);
        }
    }

    [Theory]
    [InlineData("name", "arena_fans_ambient_neutral")]
    [InlineData("name", "arena_fans_ambient_cheer4")]
    [InlineData("name", "arena_fans_ambient_angry2")]            // an Angry name in the Cheer bank
    [InlineData("name", "arena_fans_ambient_cheer22")]
    [InlineData("name", "../arena_fans_ambient_cheer")]
    [InlineData("name", "arena_fans_ambient_cheer2")]            // duplicates the second clip
    [InlineData("file", "../arena_fans_ambient_cheer.wav")]
    [InlineData("file", "arena_fans_ambient_cheer.ogg")]
    [InlineData("file", "other.wav")]
    [InlineData("channels", 2)]
    [InlineData("sample_rate", 48000)]
    [InlineData("frames", 2 * 44100)]
    [InlineData("frames", 13 * 44100)]
    [InlineData("bytes", 12345)]
    [InlineData("sha256", "ABCDEF")]
    [InlineData("sha256", null)]
    [InlineData("seam_crossfade_ms", 0)]
    [InlineData("seam_crossfade_ms", 60000)]
    public void ADamagedRoarClipIsRefused(string field, object? value)
    {
        var set = Set(j => ((JObject)j["banks"]![0]!["clips"]![0]!)[field] = value == null ? JValue.CreateNull() : JToken.FromObject(value));
        Assert.Throws<InvalidDataException>(set.Validate);
    }

    [Fact]
    public void ADamagedRoarBankTableIsRefused()
    {
        Assert.Throws<InvalidDataException>(Set(j => j["banks"]![0]!["trigger"] = 1121).Validate);
        Assert.Throws<InvalidDataException>(Set(j => j["banks"]![1]!["trigger"] = 1119).Validate);
        Assert.Throws<InvalidDataException>(Set(j => j["banks"]![0]!["kind"] = "angry").Validate);
        Assert.Throws<InvalidDataException>(Set(j => j["banks"]![0]!["bank"] = "Neutral").Validate);
        Assert.Throws<InvalidDataException>(Set(j => j["banks"]![0]!["clips"] = new JArray()).Validate);
        Assert.Throws<InvalidDataException>(Set(j => j["banks"]![0]!["clips"] = JValue.CreateNull()).Validate);
        Assert.Throws<InvalidDataException>(Set(j => ((JArray)j["banks"]!).RemoveAt(1)).Validate);           // one bank
        Assert.Throws<InvalidDataException>(Set(j => ((JArray)j["banks"]!).Add(j["banks"]![0]!.DeepClone())).Validate);   // three banks
        Assert.Throws<InvalidDataException>(Set(j => { var banks = (JArray)j["banks"]!; var first = banks[0]; banks.RemoveAt(0); banks.Add(first); }).Validate);   // Angry before Cheer
        Assert.Throws<InvalidDataException>(Set(j => ((JArray)j["banks"]![0]!["clips"]!).Add(JValue.CreateNull())).Validate);
    }

    // ---- the real beds ---------------------------------------------------------------------------------------------------------------

    [RealReactionBedsFact]
    public void TheExtractorsRealBedsPassTheClientsValidationAndTheirFilesMatchTheirRecords()
    {
        string folder = RealReactionBeds.Folder()!;
        var manifest = JsonConvert.DeserializeObject<SpectatorAudioManifest>(File.ReadAllText(Path.Combine(folder, "spectator-audio.json")))!;
        manifest.ValidateReactionBeds();
        var crypto = CryptoProviders.Select(null);
        var table = new RoarClipTable(manifest.reaction_beds);
        Assert.Equal((3, 3), (table.Count(SpectatorReactionBedKind.Cheer), table.Count(SpectatorReactionBedKind.Angry)));
        foreach (int kind in new[] { SpectatorReactionBedKind.Cheer, SpectatorReactionBedKind.Angry })
            foreach (var c in table.Clips(kind))
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(folder, c.file));
                Assert.Equal(c.bytes, bytes.Length);
                Assert.Equal(c.sha256, Convert.ToHexString(crypto.Sha256(bytes, 0, bytes.Length)).ToLowerInvariant());
                var wav = PcmWav.Read(bytes);
                Assert.Equal((c.channels, c.sample_rate, c.frames), (wav.Channels, wav.SampleRate, wav.Frames));
                var floats = PcmWav.ToFloats(wav.Samples);
                double sum = 0, peak = 0;
                foreach (float f in floats) { sum += f * f; peak = Math.Max(peak, Math.Abs(f)); }
                double rmsDb = 20 * Math.Log10(Math.Sqrt(sum / floats.Length)), peakDb = 20 * Math.Log10(peak);
                Assert.InRange(rmsDb, manifest.reaction_beds.target_rms_db - 0.3, manifest.reaction_beds.target_rms_db + 0.3);   // the common level
                Assert.True(peakDb <= -3.0, $"{c.name}: peak {peakDb:0.0} dBFS");
                // the decoded clip as the glue builds it: the fixed gain of the bed's volume law
                var clip = SpectatorCrowdBedModel.ToClipFloats(wav.Samples);
                Assert.Equal(floats.Length, clip.Length);
                Assert.True(clip.Max(x => Math.Abs(x)) <= SpectatorCrowdBedModel.ClipGain);
            }
        // the calls, the murmur and the reactions are still valid next to the roar beds
        manifest.Validate();
        manifest.ValidateBed();
        manifest.ValidateReactions();
    }

    // ---- the model file stays Unity-free ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheModelFileNamesNoUnityTypeAndNoLogOrConfig()
    {
        string text = "";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "ArenaMapPort.Client", "SpectatorReactionBedModel.cs");
            if (File.Exists(path)) { text = File.ReadAllText(path); break; }
        }
        Assert.NotEmpty(text);
        string code = Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (string forbidden in new[] { "UnityEngine", "BepInEx", "ConfigEntry", "Debug.", "Logger", "AudioSource", "Mathf.", "Time.", "Random.Range" })
            Assert.DoesNotContain(forbidden, code);
        Assert.DoesNotContain("Swell", code);
    }
}

/// <summary>Runs only when the ignored manifest of the extractor (work/extract/spectator-audio/spectator-audio.json, BSG-derived) has a <c>reaction_beds</c> object: the checkout's work
/// folder, or the one named by ARENAMAPPORT_WORK_DIR.</summary>
internal sealed class RealReactionBedsFactAttribute : FactAttribute
{
    public RealReactionBedsFactAttribute()
    {
        if (RealReactionBeds.Folder() == null) Skip = "work/extract/spectator-audio/spectator-audio.json has no 'reaction_beds' object (BSG-derived, ignored by git; run tools/extract/extract_spectator_audio.py extract)";
    }
}

internal static class RealReactionBeds
{
    internal static string? Folder()
    {
        string relative = Path.Combine("work", "extract", "spectator-audio", "spectator-audio.json");
        var candidates = new List<string>();
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(Path.Combine(env, "extract", "spectator-audio", "spectator-audio.json"));
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) candidates.Add(Path.Combine(dir.FullName, relative));
        foreach (string path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (JObject.Parse(File.ReadAllText(path))["reaction_beds"] is JObject) return Path.GetDirectoryName(path);
            }
            catch (Exception) { }
            return null;
        }
        return null;
    }
}
