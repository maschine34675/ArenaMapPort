using System;
using ArenaMapPort.Shared;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>Equator's rain-only exposure, independent of room/reverb classification.</summary>
    internal sealed class PrecipitationCover : IDisposable
    {
        RainCoverRepairs.DepthSnapshot _depth;
        object _revision;
        float _nextSample;
        internal bool? Covered { get; private set; }
        internal int CoveredSamples { get; private set; }
        internal int Captures { get; private set; }

        internal bool? Sample(ArenaMapDescriptor map, Vector3 listener, float now)
        {
            if (map != ArenaMapRegistry.Equator) return null;
            var revision = RainCoverRepairs.CompletedDepthRevision(map);
            if (!ReferenceEquals(revision, _revision))
            {
                _depth?.Dispose(); _depth = null;
                _revision = revision; Covered = null; _nextSample = 0f;
                if (revision != null)
                {
                    try { _depth = RainCoverRepairs.CaptureDepthSnapshot(map); if (_depth != null) Captures++; }
                    catch (Exception e)
                    {
                        // A failed readback must not silence rain. Retry on the next photo,
                        // and keep the native room fallback for this revision.
                        Plugin.Log.LogWarning($"[ArenaMapPort][RainAudio] roof snapshot unavailable; using room: {e.Message}");
                    }
                }
            }
            if (_depth == null) { Covered = null; return null; }
            if (now < _nextSample) return Covered;
            _nextSample = now + .2f;
            int covered = 0, valid = 0;
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                    if (_depth.TrySampleInside(listener.x + x * .6f, listener.z + z * .6f, out bool nothing, out float height))
                    {
                        valid++;
                        if (RainExposurePolicy.IsOverhead(nothing, height, listener.y)) covered++;
                    }
            CoveredSamples = covered;
            Covered = RainExposurePolicy.Covered(covered, valid, Covered);
            return Covered;
        }

        public void Dispose()
        {
            _depth?.Dispose(); _depth = null; _revision = null;
            Covered = null; CoveredSamples = 0; Captures = 0; _nextSample = 0f;
        }
    }
}
