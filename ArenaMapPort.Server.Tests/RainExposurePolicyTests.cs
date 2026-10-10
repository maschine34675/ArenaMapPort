using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public class RainExposurePolicyTests
{
    [Theory]
    [InlineData(0, 9, true, false)]
    [InlineData(3, 9, true, false)]
    [InlineData(5, 9, true, false)]
    [InlineData(5, 9, false, false)]
    [InlineData(6, 9, true, true)]
    [InlineData(7, 9, false, false)]
    [InlineData(8, 9, false, true)]
    [InlineData(9, 9, false, true)]
    [InlineData(4, 9, null, false)]
    [InlineData(6, 9, null, false)]
    [InlineData(7, 9, null, true)]
    [InlineData(0, 8, false, null)]
    [InlineData(9, 8, true, null)]
    public void FootprintHoldsAtEdgesAndFallsBackForMissingSamples(int count, int valid, bool? previous, bool? expected)
        => Assert.Equal(expected, RainExposurePolicy.Covered(count, valid, previous));

    [Theory]
    [InlineData(true, 20f, 1.7f, false)]
    [InlineData(false, .32f, 1.7f, false)]
    [InlineData(false, 1.8f, 1.7f, false)]
    [InlineData(false, 2.1f, 1.7f, true)]
    [InlineData(false, 20.63f, 1.7f, true)]
    [InlineData(false, float.NaN, 1.7f, false)]
    [InlineData(false, float.PositiveInfinity, 1.7f, false)]
    public void OnlyRealGeometryAboveListenerCounts(bool nothing, float height, float listener, bool expected)
        => Assert.Equal(expected, RainExposurePolicy.IsOverhead(nothing, height, listener));
}
