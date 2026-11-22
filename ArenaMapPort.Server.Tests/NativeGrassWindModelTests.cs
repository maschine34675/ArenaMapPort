#nullable disable
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>The grass wind's arithmetic against EFT's WindCalculator.SetWind / SetupFoliage, by hand-computed numbers (NativeGrassWindModel).</summary>
public sealed class NativeGrassWindModelTests
{
    const float Tolerance = 1e-6f;

    [Fact]
    public void TheIdleDriftAtNoWindIsNonZeroAndNegativeOnBothAxes()
    {
        NativeGrassWindModel.Factor(0f, 0f, out float fx, out float fy);
        // scale = -0.02 * (0 + 6 * 0.3) = -0.036; factor = 0.3 * -0.036 = -0.0108
        Assert.InRange(fx, -0.0108f - Tolerance, -0.0108f + Tolerance);
        Assert.InRange(fy, -0.0108f - Tolerance, -0.0108f + Tolerance);
        Assert.True(fx < 0f && fy < 0f);
    }

    [Fact]
    public void AStormWindOfMinusPointNineGivesTheHandComputedFactor()
    {
        NativeGrassWindModel.Factor(0f, -0.9f, out float fx, out float fy);
        // magnitude 0.9; scale = -0.02 * (0.9 + 1.8) = -0.054; x = 0.3 * -0.054 = -0.0162; y = (-0.9 + 0.3) * -0.054 = 0.0324
        Assert.InRange(fx, -0.0162f - Tolerance, -0.0162f + Tolerance);
        Assert.InRange(fy, 0.0324f - Tolerance, 0.0324f + Tolerance);
    }

    [Fact]
    public void EftsDefaultWindGivesTheHandComputedFactor()
    {
        NativeGrassWindModel.Factor(0.05f, 0.1f, out float fx, out float fy);
        // magnitude = sqrt(0.0125) = 0.1118034; scale = -0.02 * (0.1118034 + 1.8) = -0.03823607; x = 0.35 * scale = -0.01338262; y = 0.4 * scale = -0.01529443
        Assert.InRange(fx, -0.01338262f - 1e-7f, -0.01338262f + 1e-7f);
        Assert.InRange(fy, -0.01529443f - 1e-7f, -0.01529443f + 1e-7f);
    }

    [Fact]
    public void PreviousEqualsTheLastPhaseAndThePhaseAddsDeltaTimesFactor()
    {
        var phase = new NativeGrassWindPhase();
        phase.Advance(0.5f, 2f, -4f);
        Assert.Equal(0f, phase.PreviousX);
        Assert.Equal(1f, phase.PhaseX);
        Assert.Equal(-2f, phase.PhaseY);
        phase.Advance(0.25f, 2f, -4f);
        Assert.Equal(1f, phase.PreviousX);
        Assert.Equal(-2f, phase.PreviousY);
        Assert.Equal(1.5f, phase.PhaseX);
        Assert.Equal(-3f, phase.PhaseY);
    }

    [Fact]
    public void ThePhaseIsLinearInTheTimeStep()
    {
        var one = new NativeGrassWindPhase();
        one.Advance(0.5f, 0.125f, -0.25f);
        var two = new NativeGrassWindPhase();
        two.Advance(0.25f, 0.125f, -0.25f);
        two.Advance(0.25f, 0.125f, -0.25f);
        Assert.Equal(one.PhaseX, two.PhaseX);
        Assert.Equal(one.PhaseY, two.PhaseY);
    }

    [Fact]
    public void ResetClearsThePhaseAndThePrevious()
    {
        var phase = new NativeGrassWindPhase();
        phase.Advance(1f, 3f, 4f);
        phase.Advance(1f, 3f, 4f);
        phase.Reset();
        Assert.Equal(0f, phase.PhaseX);
        Assert.Equal(0f, phase.PhaseY);
        Assert.Equal(0f, phase.PreviousX);
        Assert.Equal(0f, phase.PreviousY);
    }
}
