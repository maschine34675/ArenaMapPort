#nullable disable
namespace ArenaMapPort.Client
{
    /// <summary>
    /// The Unity-free decision of Iceberg's one-time bloom migration (BloomTuning, <c>BloomDefaultsRevision</c> 2).
    ///
    /// History of Iceberg's pair (docs/progress/iceberg-release.md): the keys were born -1/-1 (the camera's own value, the donor rig's 2.10/0.35);
    /// revision 1 (2026-10-30, <c>15a13d8</c>) moved a saved -1/-1 pair to 4.0/0.2 because the respawn-hall chandeliers bloomed into discs at the camera's
    /// values; revision 2 (2026-10-31) returns the map to the normal donor pair, because the per-map emission cap (EmissionCapRepairs) now takes the
    /// over-bright emissive materials down instead of the bloom. Only the pair revision 1 wrote moves: a pair a tester set by hand, a single changed
    /// value or a -1/-1 pair (which already means the camera's own 2.10/0.35) stays as it is.
    /// </summary>
    internal static class BloomDefaultsModel
    {
        /// <summary>The revision that returns Iceberg to the donor pair.</summary>
        internal const int IcebergRevision = 2;

        /// <summary>The pair revision 1 wrote into a saved configuration.</summary>
        internal const float IcebergRevision1Threshold = 4.0f;
        internal const float IcebergRevision1Intensity = 0.2f;

        /// <summary>true when a configuration saved at <paramref name="savedRevision"/> holds exactly revision 1's pair and must move to the donor pair once.</summary>
        internal static bool IcebergPairMoves(int savedRevision, float threshold, float intensity) =>
            savedRevision < IcebergRevision && threshold == IcebergRevision1Threshold && intensity == IcebergRevision1Intensity;
    }
}
