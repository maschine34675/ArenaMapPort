using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Iceberg's one-time bloom migration (BloomDefaultsModel, BloomTuning's BloomDefaultsRevision 2, docs/progress/iceberg-release.md "Per-map emission cap"):
/// exactly the 4.0/0.2 pair revision 1 wrote moves back to the donor pair once; every other pair stays.
/// </summary>
public sealed class BloomDefaultsModelTests
{
    [Theory]
    [InlineData(1, 4.0f, 0.2f, true)]      // the host configuration the lead reset to the defaults of revision 1
    [InlineData(0, 4.0f, 0.2f, true)]      // the same pair with the revision key missing
    [InlineData(2, 4.0f, 0.2f, false)]     // already migrated: a tester who sets 4.0/0.2 again keeps it
    [InlineData(3, 4.0f, 0.2f, false)]
    [InlineData(1, 4.0f, 0.25f, false)]    // one value tuned by hand protects the pair
    [InlineData(1, 3.9f, 0.2f, false)]
    [InlineData(1, 4.0f, 0.35f, false)]
    [InlineData(1, 2.1f, 0.35f, false)]    // already the donor pair
    [InlineData(1, 1.2f, 0.25f, false)]    // AirPit's pair on Iceberg's section: a hand edit
    [InlineData(0, -1f, -1f, false)]       // -1/-1 already means the camera's own 2.10/0.35
    [InlineData(1, -1f, -1f, false)]
    [InlineData(1, 8f, 0f, false)]
    public void OnlyTheExactPairOfRevisionOneMovesAndOnlyBeforeRevisionTwo(int revision, float threshold, float intensity, bool moves) =>
        Assert.Equal(moves, BloomDefaultsModel.IcebergPairMoves(revision, threshold, intensity));

    [Fact]
    public void TheConstantsAreTheValuesRevisionOneWrote()
    {
        Assert.Equal(2, BloomDefaultsModel.IcebergRevision);
        Assert.Equal(4.0f, BloomDefaultsModel.IcebergRevision1Threshold);
        Assert.Equal(0.2f, BloomDefaultsModel.IcebergRevision1Intensity);
    }

    [Fact]
    public void ASecondRunAfterTheMoveFindsTheDonorPairAndLeavesItAlone()
    {
        // BloomTuning writes revision 2 after the check, so the run after a move sees revision 2 (stops) and, were the revision lost, the donor pair (no match).
        Assert.False(BloomDefaultsModel.IcebergPairMoves(0, 2.1f, 0.35f));
        Assert.False(BloomDefaultsModel.IcebergPairMoves(BloomDefaultsModel.IcebergRevision, 4.0f, 0.2f));
    }
}
