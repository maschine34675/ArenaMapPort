#nullable disable
using System;
using System.Collections.Generic;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The pure rules behind the parked gate parts of <see cref="SpawnGates"/> (no Unity types, linked into the server
    /// tests; docs/progress/door-poses.md, "Runtime"): how far along its clip a driving part is, the ease of that
    /// progress (smoothstep for Bowl's clips, linear / DOTween InOutQuint / a stored Hermite curve for the DotAnimator
    /// poses of Equator and Bay5), how a node's local position and euler angles are interpolated between Arena's closed
    /// pose and the END pose of its open animation, which colliders of a parked part are the ones Arena MOVED (and so
    /// get a park carver), when a parked collider occupies the walking band at all, whether a NavMesh probe segment runs
    /// through a parked body, how long a drive takes at the player's timing scale, and how a hierarchy path resolves to
    /// every instance that carries it.
    ///
    /// Interpolation of a Mecanim clip's node (source "clip", Bowl) is component-wise on the euler triple, as Unity
    /// animates euler clips: the BTR's wheels turn 1.5 revolutions on the way (-553 degrees), a quaternion slerp would
    /// take the short way round and turn them 0.5. A DotAnimator node (source "dotanimator" / "borrowed") is turned by a
    /// quaternion slerp between its two euler triples, the way a 90 or 160 degree swing about one axis should go; the
    /// slerp itself is Unity's, in <see cref="SpawnGates"/>, this file only says WHICH nodes use it. Bowl's ease is a plain
    /// smoothstep over the clip's length; Arena's own curves differ per part (the BTR is steeper in the first half, the
    /// ambulance stops at 70 % of its clip), so the motion is a stand-in, the END pose is Arena's data.
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

        /// <summary>Height above a part's floor line that a player or bot can occupy (metres): a parked collider whose
        /// BOTTOM is above floor + this (a roller slab lifted 3.09 m into the lintel, the BTR's turret) cannot be walked
        /// into and gets no park carver, however high it rises.</summary>
        internal const float WalkingBandHeight = 1.8f;

        /// <summary>Default of <c>Spawns/SpawnGateDriveScale</c>: half of Arena's own durations (user decision 2026-10-23).</summary>
        internal const float DefaultDriveScale = 0.5f;

        /// <summary>The range of <c>Spawns/SpawnGateDriveScale</c>: a tenth of Arena's timing up to Arena's own (1).</summary>
        internal const float MinDriveScale = 0.1f, MaxDriveScale = 1.0f;

        /// <summary>A sidecar's <c>seconds</c> (Arena's own duration) turned into the seconds a drive takes: Arena's duration
        /// times the timing scale, which is clamped to <see cref="MinDriveScale"/>..<see cref="MaxDriveScale"/> (a NaN is the
        /// default). A duration of zero or less stays zero (the part snaps).</summary>
        internal static float ScaledSeconds(float authoredSeconds, float scale)
        {
            if (!(authoredSeconds > 0f)) return 0f;
            if (float.IsNaN(scale)) scale = DefaultDriveScale;
            if (scale < MinDriveScale) scale = MinDriveScale;
            else if (scale > MaxDriveScale) scale = MaxDriveScale;
            return authoredSeconds * scale;
        }

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

        /// <summary>DOTween's Ease.InOutQuint of a clamped fraction: 0.5 * t^5 for the first half (t = 2k), then
        /// 0.5 * ((t - 2)^5 + 2). Exactly 0 at 0, 0.5 at 0.5 and 1 at 1; Arena's booth doors use it.</summary>
        internal static float InOutQuint(float k)
        {
            if (!(k > 0f)) return 0f;
            if (k >= 1f) return 1f;
            float t = k * 2f;
            if (t < 1f) return 0.5f * t * t * t * t * t;
            t -= 2f;
            return 0.5f * (t * t * t * t * t + 2f);
        }

        /// <summary>Unity's AnimationCurve.Evaluate on a flat key array (time, value, inSlope, outSlope per key, times
        /// ascending, 0..1 here): the first value before the first key, the last value from the last key on, and between two
        /// keys the cubic Hermite spline with the left key's OUT slope and the right key's IN slope. Bay5's depot gates use
        /// it: three keys, 80 % of the swing in the first 40 % of the time.</summary>
        internal static float Hermite(float[] keys, float k)
        {
            if (keys == null || keys.Length < 8 || keys.Length % 4 != 0) throw new ArgumentException("a curve is at least two keys of four floats");
            int n = keys.Length / 4;
            if (!(k > keys[0])) return keys[1];
            if (k >= keys[(n - 1) * 4]) return keys[(n - 1) * 4 + 1];
            int i = 0;
            while (i < n - 2 && k >= keys[(i + 1) * 4]) i++;
            float ta = keys[i * 4], va = keys[i * 4 + 1], outSlope = keys[i * 4 + 3];
            float tb = keys[(i + 1) * 4], vb = keys[(i + 1) * 4 + 1], inSlope = keys[(i + 1) * 4 + 2];
            float dt = tb - ta;
            if (!(dt > 0f)) return vb;
            float s = (k - ta) / dt;
            float s2 = s * s, s3 = s2 * s;
            return (2f * s3 - 3f * s2 + 1f) * va + (s3 - 2f * s2 + s) * dt * outSlope +
                   (-2f * s3 + 3f * s2) * vb + (s3 - s2) * dt * inSlope;
        }

        /// <summary>Can a pose's ease be evaluated? One of the four names of <see cref="ArenaDoorPose"/>, and for "curve" a flat
        /// key array of at least two keys with finite numbers and strictly ascending times. <paramref name="why"/> says what is
        /// wrong.</summary>
        internal static bool EaseIsUsable(string ease, float[] curve, out string why)
        {
            why = null;
            switch (ease)
            {
                case ArenaDoorPose.EaseSmoothstep:
                case ArenaDoorPose.EaseLinear:
                case ArenaDoorPose.EaseInOutQuint:
                    return true;
                case ArenaDoorPose.EaseCurve:
                    if (curve == null || curve.Length < 8 || curve.Length % 4 != 0) { why = "a curve ease needs at least two keys of four floats"; return false; }
                    for (int i = 0; i < curve.Length; i++)
                    {
                        if (float.IsNaN(curve[i]) || float.IsInfinity(curve[i])) { why = "the curve holds a number that is not finite"; return false; }
                    }
                    for (int key = 1; key < curve.Length / 4; key++)
                    {
                        if (!(curve[key * 4] > curve[(key - 1) * 4])) { why = "the curve's key times do not ascend"; return false; }
                    }
                    return true;
                default:
                    why = $"unknown ease '{ease}'";
                    return false;
            }
        }

        /// <summary>The fraction of the path (0..1) that a pose has covered when its drive is <paramref name="k"/> (0..1) of the
        /// way through its duration: the pose's own ease, with exact ends (a snap to the open pose or a re-close is the
        /// data bit for bit whatever the ease). Throws for an ease that <see cref="EaseIsUsable"/> refuses.</summary>
        internal static float EaseOf(string ease, float[] curve, float k)
        {
            if (!(k > 0f)) return 0f;
            if (k >= 1f) return 1f;
            switch (ease)
            {
                case ArenaDoorPose.EaseSmoothstep: return Ease(k);
                case ArenaDoorPose.EaseLinear: return k;
                case ArenaDoorPose.EaseInOutQuint: return InOutQuint(k);
                case ArenaDoorPose.EaseCurve: return Hermite(curve, k);
                default: throw new ArgumentException($"unknown ease '{ease}'");
            }
        }

        /// <summary>Is a node's rotation animated by this pose (do the closed and open euler triples differ)? A node whose
        /// rotation stays put is left untouched by a DotAnimator pose: Bay5's rollers sit at a gimbal lock (scene euler 270, 270, 0)
        /// and the pose only lifts them.</summary>
        internal static bool RotationMoves(float[] closedEuler, float[] openEuler)
        {
            if (closedEuler == null || openEuler == null || closedEuler.Length != 3 || openEuler.Length != 3)
                throw new ArgumentException("a pose is three floats");
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(closedEuler[i] - openEuler[i]) > 1e-4f) return true;
            }
            return false;
        }

        /// <summary>Does the source turn its node by a quaternion slerp (Arena's DotAnimator and the borrowed poses) rather than by
        /// a component-wise euler lerp (a Mecanim clip's raw angles, Bowl's wheel spins)?</summary>
        internal static bool UsesSlerp(string source) => source != ArenaDoorPose.SourceClip;

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

        /// <summary>Does a parked collider occupy the band a player or bot walks in? Its top must rise above the part's floor
        /// line by more than <see cref="ParkedBodyMinHeight"/> (<see cref="ParkedBodyStands"/>) AND its bottom must lie below
        /// floor + <see cref="WalkingBandHeight"/>. A roller slab lifted 3.09 m into the lintel (bottom 3.09 m above the floor,
        /// top 6.56 m) and the BTR's turret (bottom 2.09 m) stand above everyone's head: they cannot be walked into and a
        /// carving box over them would only cut what lies below; a swung depot leaf, a slid booth leaf and the BTR's body do
        /// occupy the band.</summary>
        internal static bool OccupiesWalkingBand(float colliderMinY, float colliderMaxY, float floorY) =>
            ParkedBodyStands(colliderMaxY, floorY) && colliderMinY < floorY + WalkingBandHeight;

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

        /// <summary>Every node a '/'-separated hierarchy path leads to: the roots named like the first segment, then at every
        /// further segment ALL children of ALL current matches with that name, in tree order. Transform.Find returns the first
        /// match of a level only, and Arena's scenes repeat names (Bay5's two OLD_WAREHOUSE_DOORS/Gates_Depot_Frame, the two
        /// unnumbered SPAWN_DOORS/Door_Sliding_Respawn): a barrier that exists twice has to open twice. No match gives an
        /// empty list.</summary>
        internal static List<T> ResolveAll<T>(IEnumerable<T> roots, string path, Func<T, string> nameOf, Func<T, IEnumerable<T>> childrenOf)
        {
            if (roots == null || string.IsNullOrEmpty(path)) return new List<T>();
            var segments = path.Split('/');
            var level = new List<T>();
            foreach (var root in roots)
            {
                if (nameOf(root) == segments[0]) level.Add(root);
            }
            for (int i = 1; i < segments.Length && level.Count > 0; i++)
            {
                var next = new List<T>();
                foreach (var parent in level)
                {
                    foreach (var child in childrenOf(parent))
                    {
                        if (nameOf(child) == segments[i]) next.Add(child);
                    }
                }
                level = next;
            }
            return level;
        }
    }
}
