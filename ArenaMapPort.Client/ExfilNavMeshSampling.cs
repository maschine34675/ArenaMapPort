namespace ArenaMapPort.Client
{
    /// <summary>
    /// Prefer the authored floor before widening an exit's NavMesh search.
    /// Bay5's north exit returned an upper floor with a single 6 m query even
    /// though a small query found the reachable ground floor at the same X/Z.
    /// Keep the former wide search as a fallback for approximate map sidecars.
    /// </summary>
    internal static class ExfilNavMeshSampling
    {
        internal const float NearRadius = 1f;
        internal const float FallbackRadius = 6f;

        internal delegate bool Sampler<T>(float radius, out T hit);

        internal static bool TrySample<T>(Sampler<T> sample, out T hit, out float sampleRadius)
        {
            sampleRadius = NearRadius;
            if (sample(sampleRadius, out hit)) return true;

            sampleRadius = FallbackRadius;
            return sample(sampleRadius, out hit);
        }
    }
}
