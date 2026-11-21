#nullable disable
using System;
using System.Globalization;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The pure rules behind <see cref="LevelBorderBoxRepairs"/> (no Unity types, linked into the server tests): the names of the raised objects,
    /// the layer, which descriptor rows can be raised as a solid wall, the normalised rotation, the world bounds of an oriented box (which the tests
    /// compare with the AABB of the tracked border sidecar) and what a second pass over an existing set must do.
    ///
    /// No row gets a NavMesh carver: part 1 of PLAN-YARD item 10 measured with a region model of the port bake that the 104 Yard rows take 4.08 m2
    /// of the yard at its edges and cut off 0.00 m2 beyond the line for every row (docs/progress/yard-release.md), so bots never path into an
    /// invisible wall and there is nothing to carve. A row that the host shows a bot stuck at gets its carver as a later, separate change.
    /// </summary>
    internal static class LevelBorderBoxModel
    {
        /// <summary>The one parent object of every raised wall, a root of an owned scene. Never starts with <c>LevelBorder</c>: MapBorderRepairs'
        /// name fallback must not match it.</summary>
        internal const string ParentName = "ArenaMapPort_LevelBorders";

        /// <summary>Name prefix of the raised walls (<c>ArenaMapPort_LevelBorder_000</c> ...). The number is the row's index in the descriptor,
        /// which is the row index of the border sidecar.</summary>
        internal const string ChildPrefix = "ArenaMapPort_LevelBorder_";

        /// <summary>EFT's own name of layer 29. A kept Arena cube sits on it and, by EFT's physics matrix (row 29 = layers 3, 6, 7 and 8, the
        /// player), blocks the player only.</summary>
        internal const string LayerName = "LevelBorder";

        /// <summary>The walls' layer: the kept cubes' own layer.</summary>
        internal const int LayerFallback = 29;

        /// <summary>How far the length of a row's quaternion may be from 1 (the sidecar holds six digits, so 1e-6 is typical).</summary>
        internal const float UnitTolerance = 1e-3f;

        /// <summary>The name of the wall of row <paramref name="index"/>: three digits at least, never shorter.</summary>
        internal static string ChildName(int index) => ChildPrefix + index.ToString("000", CultureInfo.InvariantCulture);

        /// <summary>
        /// The layer to raise the walls on: always 29. A kept Arena cube sits on layer 29 in its bundle, and the walls equal a kept cube only on that
        /// number (EFT's physics matrix row 29 = layers 3, 6, 7, 8, checked 2026-11-21). The name that <c>LayerMask.NameToLayer</c> resolves is used for
        /// the log line only: a TagManager that named another layer "LevelBorder" must not move the walls to a layer with other collision rules.
        /// </summary>
        internal static int ResolveLayer(int namedLayer) => LayerFallback;

        /// <summary>Can this row stand as a solid wall: finite centre, finite positive size, a rotation of about unit length.</summary>
        internal static bool IsUsable(ArenaBorderBox box)
        {
            if (box == null)
            {
                return false;
            }
            return IsFinite(box.CenterX) && IsFinite(box.CenterY) && IsFinite(box.CenterZ)
                && IsFinite(box.SizeX) && IsFinite(box.SizeY) && IsFinite(box.SizeZ)
                && box.SizeX > 0f && box.SizeY > 0f && box.SizeZ > 0f
                && IsFinite(box.RotationX) && IsFinite(box.RotationY) && IsFinite(box.RotationZ) && IsFinite(box.RotationW)
                && Math.Abs(Length(box) - 1f) <= UnitTolerance;
        }

        /// <summary>How many rows can be raised.</summary>
        internal static int CountUsable(ArenaBorderBox[] boxes)
        {
            int n = 0;
            if (boxes != null)
            {
                for (int i = 0; i < boxes.Length; i++)
                {
                    if (IsUsable(boxes[i]))
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        /// <summary>The row's rotation divided by its length (x, y, z, w), so a quaternion rounded to six digits reaches Unity as an exact unit one.</summary>
        internal static void NormalizedRotation(ArenaBorderBox box, out float x, out float y, out float z, out float w)
        {
            double length = Length(box);
            x = (float)(box.RotationX / length);
            y = (float)(box.RotationY / length);
            z = (float)(box.RotationZ / length);
            w = (float)(box.RotationW / length);
        }

        /// <summary>
        /// The world AABB of the oriented box (centre, full size, rotation): the half extent along axis i is the sum over j of |R[i][j]| times the
        /// size along j, halved. <paramref name="min"/> and <paramref name="max"/> receive x, y, z. The same arithmetic as
        /// <c>validate_map_descriptors.border_box_aabb</c>; the tests compare it with the AABB the extractor wrote from the cube's world matrix.
        /// </summary>
        internal static void WorldBounds(ArenaBorderBox box, float[] min, float[] max)
        {
            double[,] r = RotationMatrix(box);
            double[] size = { box.SizeX, box.SizeY, box.SizeZ };
            double[] center = { box.CenterX, box.CenterY, box.CenterZ };
            for (int i = 0; i < 3; i++)
            {
                double half = (Math.Abs(r[i, 0]) * size[0] + Math.Abs(r[i, 1]) * size[1] + Math.Abs(r[i, 2]) * size[2]) / 2.0;
                min[i] = (float)(center[i] - half);
                max[i] = (float)(center[i] + half);
            }
        }

        /// <summary>The 3x3 matrix of the row's unit quaternion (x, y, z, w), Unity's convention: world = R * local.</summary>
        static double[,] RotationMatrix(ArenaBorderBox box)
        {
            NormalizedRotation(box, out float qx, out float qy, out float qz, out float qw);
            double x = qx, y = qy, z = qz, w = qw;
            return new[,]
            {
                { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w) },
                { 2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w) },
                { 2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y) },
            };
        }

        /// <summary>
        /// A point given in the box's local frame (the unit cube scaled by the size, centred on the origin) in world space: centre + R * local.
        /// Unlike <see cref="WorldBounds"/>, which uses |R| and cannot tell a rotation from its inverse, this is sign-sensitive; the tests pin it
        /// against hand-computed corners.
        /// </summary>
        internal static void LocalToWorld(ArenaBorderBox box, double lx, double ly, double lz, out double wx, out double wy, out double wz)
        {
            double[,] r = RotationMatrix(box);
            wx = box.CenterX + r[0, 0] * lx + r[0, 1] * ly + r[0, 2] * lz;
            wy = box.CenterY + r[1, 0] * lx + r[1, 1] * ly + r[1, 2] * lz;
            wz = box.CenterZ + r[2, 0] * lx + r[2, 1] * ly + r[2, 2] * lz;
        }

        /// <summary>What <see cref="LevelBorderBoxRepairs.Apply"/> does with the set that may already exist in the raid.</summary>
        internal enum Pass
        {
            /// <summary>No parent yet: raise every usable row.</summary>
            Build,

            /// <summary>The parent holds exactly one child per usable row: leave it (the spawn postfix runs twice per raid).</summary>
            Keep,

            /// <summary>A parent with another number of children (a pass cut short, a changed descriptor): remove it and raise the whole set again.</summary>
            Rebuild,

            /// <summary>Nothing to raise (no usable row).</summary>
            Nothing,
        }

        /// <summary>The decision of one pass, from the state it finds.</summary>
        internal static Pass PlanPass(bool parentExists, int existingChildren, int usableRows)
        {
            if (usableRows <= 0)
            {
                return parentExists ? Pass.Rebuild : Pass.Nothing;
            }
            if (!parentExists)
            {
                return Pass.Build;
            }
            return existingChildren == usableRows ? Pass.Keep : Pass.Rebuild;
        }

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        static double Length(ArenaBorderBox box) =>
            Math.Sqrt((double)box.RotationX * box.RotationX + (double)box.RotationY * box.RotationY + (double)box.RotationZ * box.RotationZ + (double)box.RotationW * box.RotationW);
    }
}
