#nullable disable
namespace ArenaMapPort.Client
{
    /// <summary>
    /// The Unity-free decision of the once-only emission default migration (EmissionCapRepairs, <c>EmissionDefaultsRevision</c> 1).
    ///
    /// 0.5.0 shipped Iceberg's pair as EmissionCap 4.8 / EmissionFloor 1.9; on 2026-11-07 evening the user moved the defaults to 4.6 / 1.8. A saved
    /// configuration that holds exactly the old pair (the keys were seeded with the old defaults) moves to the descriptor's new pair once; a pair the
    /// tester tuned (any other value, 0/0 = off included) stays as it is.
    /// </summary>
    internal static class EmissionDefaultsModel
    {
        /// <summary>The revision that moves 4.8 / 1.9 to the descriptor's new defaults.</summary>
        internal const int Revision = 1;

        /// <summary>The pair 0.5.0 seeded into a saved configuration.</summary>
        internal const float Revision0Cap = 4.8f;
        internal const float Revision0Floor = 1.9f;

        /// <summary>true when a configuration saved at <paramref name="savedRevision"/> holds exactly the 0.5.0 pair and must move once.</summary>
        internal static bool PairMoves(int savedRevision, float cap, float floor) =>
            savedRevision < Revision && cap == Revision0Cap && floor == Revision0Floor;
    }
}
