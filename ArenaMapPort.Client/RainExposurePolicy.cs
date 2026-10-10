namespace ArenaMapPort.Client
{
    // Rain only: an acoustic room can enclose an open atrium. A small footprint
    // avoids switching beds for each narrow roof beam; the dead band holds the
    // previous result near an edge. Unknown samples retain the native fallback.
    internal static class RainExposurePolicy
    {
        internal static bool IsOverhead(bool nothing, float height, float listenerY) =>
            !nothing && !float.IsNaN(height) && !float.IsInfinity(height) && height > listenerY + .2f;

        internal static bool? Covered(int covered, int valid, bool? previous)
        {
            if (valid != 9 || covered < 0 || covered > valid) return null;
            // Crossing lattice beams can occupy five of nine nearby rays without
            // forming a roof. Require nearly continuous cover for the muffled bed.
            if (covered <= 5) return false;
            if (covered >= 8) return true;
            return previous ?? covered >= 7;
        }
    }
}
