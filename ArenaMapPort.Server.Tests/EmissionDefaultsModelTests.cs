using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public class EmissionDefaultsModelTests
{
    [Fact]
    public void MovesExactlyTheShippedPairOnce()
    {
        Assert.Equal(1, EmissionDefaultsModel.Revision);
        Assert.True(EmissionDefaultsModel.PairMoves(0, 4.8f, 1.9f));
    }

    [Theory]
    [InlineData(0, 4.6f, 1.8f)]
    [InlineData(0, 4.8f, 3.0f)]
    [InlineData(0, 4.0f, 1.9f)]
    [InlineData(0, 0f, 0f)]
    [InlineData(1, 4.8f, 1.9f)]
    [InlineData(2, 4.8f, 1.9f)]
    public void KeepsEveryOtherState(int revision, float cap, float floor) =>
        Assert.False(EmissionDefaultsModel.PairMoves(revision, cap, floor));

    [Fact]
    public void TheOldPairDiffersFromTheDescriptorDefaults()
    {
        Assert.NotEqual(EmissionDefaultsModel.Revision0Cap, ArenaMapPort.Shared.ArenaMapRegistry.Iceberg.EmissionCap);
        Assert.NotEqual(EmissionDefaultsModel.Revision0Floor, ArenaMapPort.Shared.ArenaMapRegistry.Iceberg.EmissionFloor);
    }
}
