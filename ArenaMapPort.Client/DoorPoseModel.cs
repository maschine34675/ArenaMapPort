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
    /// every instance that carries it. Since 2026-10-24 (AirPit, "Runtime 3") also: which of several rows that share a part
    /// name belong to the node at hand (<see cref="SelectRowsForNode"/>, by the closed position), the active state a switch
    /// row sets at the start and the end of a drive (<see cref="SwitchStateAt"/>), how long a part with only switches takes
    /// (<see cref="SwitchPartDriveSeconds"/>) and, since 2026-10-25, which barriers of a map are LAMP GROUPS, Arena's exit lamps
    /// (<see cref="LampBarriers"/>).
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

        /// <summary>Does the navmesh probe judge a gate part? The probe that follows a hold or the NavMesh install
        /// (<c>expectSealed</c>) asks whether a CLOSED gate seals its doorway: a part that stands at its open pose (parked, or
        /// still driving there) is no closed gate and is not judged. AirPit parks its bus assemblies in a plain raid while the
        /// passport leaves are held closed, so that raid runs the sealed probe with open bus doorways in the part table; counting
        /// them gave a false "a gate is NOT sealed" Warning. The probe after a release (<c>!expectSealed</c>) judges every part
        /// (a parked body on the segment is handled there).</summary>
        internal static bool ProbeJudgesPart(bool expectSealed, bool parkedOrDriving) =>
            !(expectSealed && parkedOrDriving);

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

        /// <summary>Which of the rows of ONE node (the same part and the same node path) belong to the node that stands at the
        /// local position (x, y, z)? Arena's scenes repeat names: AirPit's 16 passport leaves reuse six, so the descriptor holds
        /// several rows for part "Passport_control_01_Door (4)" with node "" and tells them apart only by <see cref="ArenaDoorPose.ClosedPosition"/>
        /// (docs/progress/door-poses.md, "Repeated names"). A single row is taken as it is, whatever its position (the
        /// ordinary case on every other map; <see cref="PoseMatchesScene"/> then reports a node that stands elsewhere). With
        /// several rows only those whose closed position lies within <see cref="ArenaDoorPose.ClosedPositionTolerance"/> of
        /// the node's own are returned, in the given order; none of them is an empty array (the caller falls back).</summary>
        internal static ArenaDoorPose[] SelectRowsForNode(ArenaDoorPose[] rows, float x, float y, float z)
        {
            if (rows == null || rows.Length == 0) return new ArenaDoorPose[0];
            if (rows.Length == 1) return rows;
            float tolerance2 = ArenaDoorPose.ClosedPositionTolerance * ArenaDoorPose.ClosedPositionTolerance;
            var matching = new List<ArenaDoorPose>();
            foreach (var row in rows)
            {
                var p = row.ClosedPosition;
                if (p == null || p.Length != 3) continue;
                float dx = p[0] - x, dy = p[1] - y, dz = p[2] - z;
                if (dx * dx + dy * dy + dz * dz <= tolerance2) matching.Add(row);
            }
            return matching.ToArray();
        }

        /// <summary>The rows that belong to ONE gate part out of every row of its NAME: the rows are grouped by node path
        /// ("" = the part itself) and <see cref="SelectRowsForNode"/> picks within every group that has several rows, with the
        /// local position <paramref name="localPositionOfNode"/> returns for that node path (null = the node does not exist
        /// below the part: the group is kept whole so the caller reports the missing node). The result keeps the sidecar's
        /// order. A name that does not repeat (one row per node) comes back unchanged.</summary>
        internal static ArenaDoorPose[] SelectRowsForPart(ArenaDoorPose[] rows, Func<string, float[]> localPositionOfNode)
        {
            if (rows == null || rows.Length == 0) return new ArenaDoorPose[0];
            if (rows.Length == 1 || localPositionOfNode == null) return rows;
            var groups = new Dictionary<string, List<ArenaDoorPose>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var row in rows)
            {
                string node = row.Node ?? "";
                if (!groups.TryGetValue(node, out var group))
                {
                    groups[node] = group = new List<ArenaDoorPose>();
                    order.Add(node);
                }
                group.Add(row);
            }
            var keep = new HashSet<ArenaDoorPose>();
            foreach (var node in order)
            {
                var group = groups[node];
                float[] at = group.Count > 1 ? localPositionOfNode(node) : null;
                if (at == null || at.Length != 3)
                {
                    foreach (var row in group) keep.Add(row);
                    continue;
                }
                foreach (var row in SelectRowsForNode(group.ToArray(), at[0], at[1], at[2])) keep.Add(row);
            }
            var result = new List<ArenaDoorPose>();
            foreach (var row in rows)
            {
                if (keep.Contains(row)) result.Add(row);
            }
            return result.ToArray();
        }

        /// <summary>The active state a SWITCH row (<see cref="ArenaDoorPose.IsSwitch"/>, a SetActive node of Arena's DotAnimator:
        /// AirPit's passport lamps) puts its node in when its part's drive is <paramref name="k"/> (0..1) of the way through:
        /// <paramref name="closedActive"/> at the start (and for anything below 0, NaN included), <paramref name="openActive"/> at
        /// the end (and above), and null in between, where the node keeps whatever state it has. A switch flips at the END of its
        /// part's drive, never on a fraction of it: the lamp changes when the leaves stand open, not while they swing.</summary>
        internal static bool? SwitchStateAt(float k, bool closedActive, bool openActive)
        {
            if (!(k > 0f)) return closedActive;
            if (k >= 1f) return openActive;
            return null;
        }

        /// <summary>How long a part that has only SWITCH rows (AirPit's RED and GREEN: no collider, no motion) takes on a
        /// release: as long as the longest drive among the other parts of its barrier, so the lamp flips when the last leaf is
        /// open. <paramref name="otherDriveSeconds"/> are the (already scaled) drive seconds of the parts that move; nothing
        /// moving, or only values that are not positive numbers, gives zero (the switch flips in the first frame).</summary>
        internal static float SwitchPartDriveSeconds(IEnumerable<float> otherDriveSeconds)
        {
            float longest = 0f;
            if (otherDriveSeconds == null) return longest;
            foreach (var seconds in otherDriveSeconds)
            {
                if (seconds > longest) longest = seconds;
            }
            return longest;
        }

        /// <summary>The barriers that are LAMP GROUPS (Equator's EXIT_LIGHT_EMISSIVE, AirPit's BUS_LIGHT, 2026-10-25: the green
        /// emergency-exit lamps Arena lights when the gates open): every row of the barrier is a SWITCH row and the barrier is no
        /// entry of <paramref name="openedBarriers"/>, so the bake and the keep-structure rule never see it. Distinct barrier paths
        /// in row order. A barrier with any transform row (AirPit's PASSPORT_CONTROL_DOORS: leaves turn, RED / GREEN switch) or listed
        /// as opened (it has its own handling) is not one; no rows, or no barrier name, give an empty list.
        /// tools/validate/validate_map_descriptors.py (door_lamp_groups) states the same rule for the sidecar.</summary>
        internal static List<string> LampBarriers(IEnumerable<ArenaDoorPose> rows, IEnumerable<string> openedBarriers)
        {
            var opened = new HashSet<string>(openedBarriers ?? new string[0], StringComparer.Ordinal);
            var order = new List<string>();
            var allSwitch = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (rows == null) return order;
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Barrier)) continue;
                if (!allSwitch.TryGetValue(row.Barrier, out bool all))
                {
                    order.Add(row.Barrier);
                    all = true;
                }
                allSwitch[row.Barrier] = all && row.IsSwitch;
            }
            order.RemoveAll(barrier => !allSwitch[barrier] || opened.Contains(barrier));
            return order;
        }

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
