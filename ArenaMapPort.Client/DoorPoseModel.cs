#nullable disable
using System;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The pure rules behind the parked gate parts of <see cref="SpawnGates"/> (no Unity types, linked into the server
    /// tests; docs/progress/door-poses.md, "Runtime"): how far along its clip a driving part is, how a node's local
    /// position and euler angles are interpolated between Arena's closed pose and the END pose of its open clip, which
    /// colliders of a parked part are the ones Arena MOVED (and so get a park carver), when a parked collider stands in
    /// the way at all, and whether a NavMesh probe segment runs through a parked body.
    ///
    /// Interpolation is component-wise on the euler triple, as Unity animates euler clips: the BTR's wheels turn 1.5
    /// revolutions on the way (-553 degrees), a quaternion slerp would take the short way round and turn them 0.5. The
    /// ease is a plain smoothstep over the clip's length; Arena's own curves differ per part (the BTR is steeper in the
    /// first half, the ambulance stops at 70 % of its clip), so the motion is a stand-in, the END pose is Arena's data.
    /// </summary>
    internal static class DoorPoseModel
    {
        /// <summary>A parked collider whose top is not higher than this above the part's floor line (the lowest point of
        /// its closed movement colliders) is a curb, or lies below the ground: it gets no carver, so a body that Arena
        /// moved out of the way can never be carved shut.</summary>
        internal const float ParkedBodyMinHeight = 0.3f;

        /// <summary>The scene's local pose of a node may differ from the data's closed pose by this much (metres) before
        /// the data is treated as not belonging to this scene.</summary>
        internal const float PoseMatchPosition = 0.05f;

        /// <summary>... and by this much (degrees).</summary>
        internal const float PoseMatchAngle = 2f;

        /// <summary>Fraction of a clip that has elapsed, 0..1. A clip of zero or negative length is over at once.</summary>
        internal static float Progress(float elapsedSeconds, float clipSeconds)
        {
            if (!(clipSeconds > 0f)) return 1f;
            float k = elapsedSeconds / clipSeconds;
            if (!(k > 0f)) return 0f;
            return k > 1f ? 1f : k;
        }

        /// <summary>True once a drive of <paramref name="clipSeconds"/> has run its length (a clip of zero or negative
        /// length is over at once).</summary>
        internal static bool Finished(float elapsedSeconds, float clipSeconds) =>
            !(clipSeconds > 0f) || elapsedSeconds >= clipSeconds;

        /// <summary>Smoothstep of a clamped fraction: slow start, slow stop, exactly 0 at 0 and exactly 1 at 1.</summary>
        internal static float Ease(float k)
        {
            if (!(k > 0f)) return 0f;
            if (k >= 1f) return 1f;
            return k * k * (3f - 2f * k);
        }

        /// <summary>a + (b - a) * k with exact endpoints: k at or below 0 gives <paramref name="a"/>, at or above 1 gives
        /// <paramref name="b"/> (so a snap to the open pose is bit-for-bit the data, however often it is applied).</summary>
        internal static float Lerp(float a, float b, float k)
        {
            if (!(k > 0f)) return a;
            if (k >= 1f) return b;
            return a + (b - a) * k;
        }

        /// <summary>Component-wise <see cref="Lerp"/> of two 3-element arrays (positions or euler angles).</summary>
        internal static void Lerp3(float[] closed, float[] open, float k, out float x, out float y, out float z)
        {
            if (closed == null || open == null || closed.Length != 3 || open.Length != 3)
                throw new ArgumentException("a pose is three floats");
            x = Lerp(closed[0], open[0], k);
            y = Lerp(closed[1], open[1], k);
            z = Lerp(closed[2], open[2], k);
        }

        /// <summary>Does a collider belong to something Arena's clip moves? <paramref name="colliderPath"/> is the path of
        /// the collider's GameObject below the gate part ("" = the part itself), <paramref name="nodePaths"/> the paths of
        /// the animated nodes ("" = the part itself, which moves everything below it). A collider that no pose node
        /// contains (the frame of the army gate, the rail of the respawn gate, the Telfer pillars) stays where it
        /// always stood: its bounds can span the very doorway the moved leaves opened, so it is never carved when parked.</summary>
        internal static bool IsMoved(string colliderPath, string[] nodePaths)
        {
            if (colliderPath == null || nodePaths == null) return false;
            foreach (var node in nodePaths)
            {
                if (string.IsNullOrEmpty(node)) return true;
                if (colliderPath == node) return true;
                if (colliderPath.Length > node.Length && colliderPath[node.Length] == '/' &&
                    colliderPath.StartsWith(node, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Does a parked collider stand in the way? Its top must rise above the part's floor line by more than
        /// <see cref="ParkedBodyMinHeight"/>.</summary>
        internal static bool ParkedBodyStands(float colliderMaxY, float floorY) =>
            colliderMaxY > floorY + ParkedBodyMinHeight;

        /// <summary>Does the horizontal probe segment (a to b, x/z) touch a park carver box (x/z extents)? Liang-Barsky
        /// clip of the segment against the rectangle; a touching edge counts. The probe after a release is not judged for
        /// a doorway whose segment runs through a parked body.</summary>
        internal static bool SegmentTouchesBox(float ax, float az, float bx, float bz,
            float minX, float minZ, float maxX, float maxZ)
        {
            if (maxX < minX || maxZ < minZ) throw new ArgumentException("inverted box");
            float t0 = 0f, t1 = 1f;
            float dx = bx - ax, dz = bz - az;
            return Clip(-dx, ax - minX, ref t0, ref t1) && Clip(dx, maxX - ax, ref t0, ref t1) &&
                   Clip(-dz, az - minZ, ref t0, ref t1) && Clip(dz, maxZ - az, ref t0, ref t1);
        }

        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (p == 0f) return q >= 0f;
            float r = q / p;
            if (p < 0f)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }

        /// <summary>Is the scene's own pose of a node close enough to the data's closed pose for the data to apply? A node
        /// that is far off (another hierarchy, another build) makes the whole part fall back to the switch-off.</summary>
        internal static bool PoseMatchesScene(float positionErrorMetres, float angleErrorDegrees) =>
            positionErrorMetres <= PoseMatchPosition && angleErrorDegrees <= PoseMatchAngle;
    }
}
