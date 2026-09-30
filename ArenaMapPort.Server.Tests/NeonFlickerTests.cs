using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public class NeonFlickerTests
{
    static readonly int[] Seeds = { 11, 29, 47 };

    [Fact]
    public void ExactlyThreeReviewedFixturesHaveIndependentSeeds()
    {
        var selected = ArenaNeonLayout.Equator.Where(f => f.FlickerSeed != 0).ToArray();
        Assert.Equal(3, selected.Length);
        Assert.Equal(11, Assert.Single(selected, f => f.Name == "north-column-west").FlickerSeed);
        Assert.Equal(29, Assert.Single(selected, f => f.Name == "techlight-right").FlickerSeed);
        Assert.Equal(47, Assert.Single(selected, f => f.Name == "south-sign-left").FlickerSeed);
        Assert.Equal(Seeds, selected.Select(f => f.FlickerSeed).OrderBy(s => s));
    }

    [Theory]
    [InlineData(0, .8f, 1f)]
    [InlineData(-1, .8f, 1f)]
    [InlineData(11, 0f, 1f)]
    [InlineData(11, .8f, 0f)]
    [InlineData(11, -1f, 1f)]
    [InlineData(11, .8f, -1f)]
    [InlineData(11, float.NaN, 1f)]
    [InlineData(11, .8f, float.PositiveInfinity)]
    public void DisabledOrInvalidControlsAlwaysRestoreSteadyLight(int seed, float strength, float frequency)
    {
        for (int sample = 0; sample < 10000; sample++)
            Assert.Equal(1f, ArenaNeonFlicker.Evaluate(sample / 50d, seed, strength, frequency));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void InvalidClockRestoresSteadyLight(double time)
        => Assert.Equal(1f, ArenaNeonFlicker.Evaluate(time, 11, 1, 3));

    [Fact]
    public void StrengthBoundsBrightnessAndFrequencyHasAnUpperLimit()
    {
        foreach (int seed in Seeds.Concat(new[] { 1, int.MaxValue }))
        foreach (float strength in new[] { .2f, .8f, 1f, 2f })
        foreach (float frequency in new[] { .1f, 1f, 3f })
        {
            float minimum = 1f - Math.Min(strength, 1f);
            for (int sample = 0; sample < 6000; sample++)
                Assert.InRange(ArenaNeonFlicker.Evaluate(sample / 50d, seed, strength, frequency), minimum, 1f);
        }
        for (int sample = 0; sample < 6000; sample++)
        {
            double time = sample / 50d;
            Assert.Equal(ArenaNeonFlicker.Evaluate(time, 11, .8f, 3),
                ArenaNeonFlicker.Evaluate(time, 11, .8f, 100));
        }
        Assert.InRange(ArenaNeonFlicker.Evaluate(double.MaxValue, int.MaxValue, 1, 3), 0f, 1f);
    }

    [Fact]
    public void EveryFixtureStartsSteadyAndDefaultSpendsMostTimeSteady()
    {
        foreach (int seed in Seeds)
        {
            for (int sample = 0; sample <= 400; sample++)
            {
                Assert.Equal(1f, ArenaNeonFlicker.Evaluate(sample / 100d, seed, .8f, 1));
                Assert.Equal(1f, ArenaNeonFlicker.Evaluate(sample / 100d, seed, .8f, 3));
            }
            int steady = 0;
            const int samples = 20000;
            for (int sample = 0; sample < samples; sample++)
                if (ArenaNeonFlicker.Evaluate(sample / 100d, seed, .8f, 1) == 1f) steady++;
            Assert.InRange(steady / (double)samples, .95d, .995d);
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(47)]
    public void FullStrengthHasSustainedBlackoutsAndDefaultRetainsTwentyPercent(int seed)
    {
        int zeroRun = 0, longestZeroRun = 0;
        float lowestDefault = 1f;
        for (int sample = 0; sample < 30000; sample++)
        {
            double time = sample / 1000d;
            zeroRun = ArenaNeonFlicker.Evaluate(time, seed, 1f, 1f) == 0f ? zeroRun + 1 : 0;
            longestZeroRun = Math.Max(longestZeroRun, zeroRun);
            lowestDefault = Math.Min(lowestDefault, ArenaNeonFlicker.Evaluate(time, seed, .8f, 1f));
        }
        Assert.True(longestZeroRun >= 40, "A blackout must hold for multiple frames, not hit zero for one sample.");
        Assert.InRange(lowestDefault, .19999f, .20001f);
    }

    [Fact]
    public void SelectedFixturesDoNotShareOneTimingPattern()
    {
        var starts = Seeds.Select(seed => FirstBurst(seed, 1)[0].Start).ToArray();
        Assert.Equal(3, starts.Distinct().Count());
        for (int a = 0; a < Seeds.Length; a++)
        for (int b = a + 1; b < Seeds.Length; b++)
            Assert.Contains(Enumerable.Range(0, 4000), sample =>
                ArenaNeonFlicker.Evaluate(sample / 20d, Seeds[a], .8f, 1) !=
                ArenaNeonFlicker.Evaluate(sample / 20d, Seeds[b], .8f, 1));
    }

    [Fact]
    public void OutputDoesNotDependOnFrameRateSkippedFramesOrCallOrder()
    {
        foreach (int seed in Seeds)
        {
            var at60 = new float[1441];
            var at144 = new float[1441];
            for (int frame = 0; frame <= 7200; frame++)
            {
                float value = ArenaNeonFlicker.Evaluate(frame / 60d, seed, .8f, 1);
                if (frame % 5 == 0) at60[frame / 5] = value;
            }
            for (int frame = 0; frame <= 17280; frame++)
            {
                float value = ArenaNeonFlicker.Evaluate(frame / 144d, seed, .8f, 1);
                if (frame % 12 == 0) at144[frame / 12] = value;
            }
            Assert.Equal(at60, at144);
            for (int sample = at60.Length - 1; sample >= 0; sample--)
            {
                ArenaNeonFlicker.Evaluate(sample * 17.3d, seed + 100, 1, 3);
                Assert.Equal(at60[sample], ArenaNeonFlicker.Evaluate(sample / 12d, seed, .8f, 1));
            }
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(47)]
    public void FrequencyChangesBurstCadenceWithoutShorteningPulses(int seed)
    {
        var normal = FirstBurst(seed, 1);
        var faster = FirstBurst(seed, 3);
        Assert.True(faster[0].Start < normal[0].Start);
        Assert.Equal(normal.Count, faster.Count);
        for (int i = 0; i < normal.Count; i++)
        {
            Assert.InRange(Math.Abs((normal[i].End - normal[i].Start) - (faster[i].End - faster[i].Start)), 0d, .003d);
            Assert.InRange(Math.Abs((normal[i].Start - normal[0].Start) - (faster[i].Start - faster[0].Start)), 0d, .003d);
        }
        Assert.Equal(10, CountBursts(seed, 1, 204d));
        Assert.Equal(30, CountBursts(seed, 3, 204d));
    }

    // Observe the public signal, grouping nearby dips into one burst. No production timing
    // constants or hash implementation are reproduced by these test helpers.
    static List<(double Start, double End)> FirstBurst(int seed, float frequency)
    {
        var dips = new List<(double Start, double End)>();
        double? start = null;
        for (int sample = 0; sample < 30000; sample++)
        {
            double time = sample / 1000d;
            bool dim = ArenaNeonFlicker.Evaluate(time, seed, 1, frequency) < .999f;
            if (dim && start == null) start = time;
            if (!dim && start != null) { dips.Add((start.Value, time)); start = null; }
            if (start == null && dips.Count > 0 && time - dips[dips.Count - 1].End > 1d) return dips;
        }
        Assert.Fail("No flicker burst observed in the first thirty seconds.");
        return dips;
    }

    static int CountBursts(int seed, float frequency, double seconds)
    {
        int count = 0;
        double previousDimTime = double.NegativeInfinity;
        for (int sample = 0; sample < seconds * 1000d; sample++)
        {
            double time = sample / 1000d;
            if (ArenaNeonFlicker.Evaluate(time, seed, 1, frequency) >= .999f) continue;
            if (time - previousDimTime > 1d) count++;
            previousDimTime = time;
        }
        return count;
    }
}
