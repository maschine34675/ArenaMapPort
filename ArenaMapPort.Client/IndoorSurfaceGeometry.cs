#nullable disable
using System;
using System.Collections.Generic;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Render-only floor partition. A bounded spatial probe grid finds roof edges even inside large
    /// authored triangles; mixed cells are refined before assigning a material. Derived vertices keep
    /// barycentric provenance so the Unity adapter can interpolate every original vertex channel.
    /// </summary>
    internal static class IndoorSurfaceGeometry
    {
        internal readonly struct Point
        {
            internal readonly double X, Y, Z;
            internal Point(double x, double y, double z) { X = x; Y = y; Z = z; }
            internal bool Finite => IsFinite(X) && IsFinite(Y) && IsFinite(Z);
        }

        internal readonly struct AddedVertex : IEquatable<AddedVertex>
        {
            internal readonly int A, B, C;
            internal readonly double WA, WB, WC;

            internal AddedVertex(int a, int b, int c, double wa, double wb, double wc)
            {
                // Canonical ordering also shares subdivisions along an original edge. Zero-weight
                // third corners must not distinguish the same edge in two adjacent triangles.
                if (a > b) { Swap(ref a, ref b); Swap(ref wa, ref wb); }
                if (b > c) { Swap(ref b, ref c); Swap(ref wb, ref wc); }
                if (a > b) { Swap(ref a, ref b); Swap(ref wa, ref wb); }
                A = wa == 0 ? -1 : a; B = wb == 0 ? -1 : b; C = wc == 0 ? -1 : c;
                WA = wa; WB = wb; WC = wc;
                // Compact nonzero contributions to the front, for equivalent edge provenance.
                if (WA == 0) { A = B; B = C; C = -1; WA = WB; WB = WC; WC = 0; }
                if (WB == 0) { B = C; C = -1; WB = WC; WC = 0; }
            }

            // Absent contributions use A as a harmless valid fallback for channel array reads.
            internal int IndexB => B < 0 ? A : B;
            internal int IndexC => C < 0 ? A : C;
            internal double Interpolate(double a, double b, double c) => a * WA + b * WB + c * WC;
            internal Point Position(Point[] vertices) => new Point(
                Interpolate(vertices[A].X, vertices[IndexB].X, vertices[IndexC].X),
                Interpolate(vertices[A].Y, vertices[IndexB].Y, vertices[IndexC].Y),
                Interpolate(vertices[A].Z, vertices[IndexB].Z, vertices[IndexC].Z));

            public bool Equals(AddedVertex other) => A == other.A && B == other.B && C == other.C
                && WA == other.WA && WB == other.WB && WC == other.WC;
            public override bool Equals(object obj) => obj is AddedVertex other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return (((((A * 397 ^ B) * 397 ^ C) * 397 ^ WA.GetHashCode())
                    * 397 ^ WB.GetHashCode()) * 397) ^ WC.GetHashCode(); }
            }
            static void Swap<T>(ref T a, ref T b) { var old = a; a = b; b = old; }
        }

        internal sealed class Partition
        {
            internal int[][] Retained;
            internal int[][] Covered;
            internal AddedVertex[] AddedVertices;
            internal int Triangles;
        }

        // The coarse grid limits undetected roof details to sub-metre cells; the fine cells bound
        // visible roof-edge material steps to 18.75 cm. This is not exact polygon/roof clipping.
        internal const double ProbeEdge = .75;
        internal const double BoundaryEdge = .1875;
        internal const int MaximumFragments = 65536;
        internal const int MaximumNodes = 131072;
        internal const int MaximumSamples = 524288;
        const int MaximumDepth = 28;
        const double FloorBand = 1.5;
        const double MinimumFloorArea = 16;
        const double MinimumSelectedSlotArea = 4;

        readonly struct Weights
        {
            internal readonly double A, B;
            internal Weights(double a, double b) { A = a; B = b; }
            internal double C => 1 - A - B;
            internal static Weights Mid(Weights a, Weights b) => new Weights((a.A + b.A) * .5, (a.B + b.B) * .5);
        }

        sealed class Region
        {
            internal Weights A, B, C;
            internal bool Covered;
            internal Region Left, Right;
            internal bool Leaf => Left == null;
            internal double CoveredFraction;
        }

        sealed class Budget
        {
            internal int Nodes, Samples, Fragments;
            internal bool Exceeded;
        }

        internal static Partition Split(Point[] vertices, int[][] indices, bool[] eligible,
            double bottom, bool mirrored, Func<Point, bool> covered)
        {
            if (vertices == null || indices == null || eligible == null || covered == null
                || indices.Length != eligible.Length || !IsFinite(bottom)) return null;
            foreach (var vertex in vertices) if (!vertex.Finite) return null;
            var candidates = new List<(int slot, int offset, double area)>();
            double floorArea = 0;
            int inputTriangles = 0;
            for (int slot = 0; slot < indices.Length; slot++)
            {
                var triangles = indices[slot];
                if (triangles == null || triangles.Length % 3 != 0) return null;
                inputTriangles += triangles.Length / 3;
                if (inputTriangles > MaximumFragments) return null;
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) return null;
                    if (!eligible[slot]) continue;
                    double area = FloorArea(vertices[a], vertices[b], vertices[c], bottom, mirrored);
                    if (area <= 0) continue;
                    candidates.Add((slot, t, area));
                    floorArea += area;
                }
            }
            if (!IsFinite(floorArea) || floorArea < MinimumFloorArea) return null;

            var regions = new Dictionary<int, Region>[indices.Length];
            var selectedArea = new double[indices.Length];
            var budget = new Budget();
            foreach (var triangle in candidates)
            {
                var src = indices[triangle.slot];
                var a = vertices[src[triangle.offset]];
                var b = vertices[src[triangle.offset + 1]];
                var c = vertices[src[triangle.offset + 2]];
                var region = Classify(a, b, c, new Weights(1, 0), new Weights(0, 1), new Weights(0, 0),
                    0, covered, budget);
                if (budget.Exceeded) return null; // Never publish a partly inspected/partitioned mesh.
                if (regions[triangle.slot] == null) regions[triangle.slot] = new Dictionary<int, Region>();
                regions[triangle.slot].Add(triangle.offset, region);
                selectedArea[triangle.slot] += triangle.area * region.CoveredFraction;
            }

            var result = new Partition { Retained = new int[indices.Length][], Covered = new int[indices.Length][] };
            var added = new List<AddedVertex>();
            var vertexIndices = new Dictionary<AddedVertex, int>();
            for (int slot = 0; slot < indices.Length; slot++)
            {
                var keep = new List<int>();
                var indoors = new List<int>();
                var src = indices[slot];
                for (int t = 0; t < src.Length; t += 3)
                {
                    if (selectedArea[slot] >= MinimumSelectedSlotArea && regions[slot] != null
                        && regions[slot].TryGetValue(t, out var region))
                        Emit(region, src[t], src[t + 1], src[t + 2], vertices.Length,
                            keep, indoors, added, vertexIndices, budget);
                    else
                    {
                        keep.Add(src[t]); keep.Add(src[t + 1]); keep.Add(src[t + 2]);
                        if (++budget.Fragments > MaximumFragments) budget.Exceeded = true;
                    }
                    if (budget.Exceeded) return null;
                }
                result.Retained[slot] = keep.ToArray();
                result.Covered[slot] = indoors.ToArray();
                result.Triangles += indoors.Count / 3;
            }
            result.AddedVertices = added.ToArray();
            return result;
        }

        static Region Classify(Point sourceA, Point sourceB, Point sourceC, Weights a, Weights b, Weights c,
            int depth, Func<Point, bool> covered, Budget budget)
        {
            if (++budget.Nodes > MaximumNodes || depth > MaximumDepth) { budget.Exceeded = true; return null; }
            var pa = Blend(sourceA, sourceB, sourceC, a);
            var pb = Blend(sourceA, sourceB, sourceC, b);
            var pc = Blend(sourceA, sourceB, sourceC, c);
            double ab = DistanceSquared(pa, pb), bc = DistanceSquared(pb, pc), ca = DistanceSquared(pc, pa);
            double edge = Math.Max(ab, Math.Max(bc, ca));
            if (edge <= ProbeEdge * ProbeEdge)
            {
                // Include edges and vertices, not only interior samples: doorway boundaries often
                // cross a large triangle while all its old four interior samples agree.
                bool center = Sample(covered, Blend(sourceA, sourceB, sourceC,
                    new Weights((a.A + b.A + c.A) / 3, (a.B + b.B + c.B) / 3)), budget);
                int count = center ? 1 : 0;
                if (Sample(covered, pa, budget)) count++;
                if (Sample(covered, pb, budget)) count++;
                if (Sample(covered, pc, budget)) count++;
                if (Sample(covered, Blend(sourceA, sourceB, sourceC, Weights.Mid(a, b)), budget)) count++;
                if (Sample(covered, Blend(sourceA, sourceB, sourceC, Weights.Mid(b, c)), budget)) count++;
                if (Sample(covered, Blend(sourceA, sourceB, sourceC, Weights.Mid(c, a)), budget)) count++;
                if (budget.Exceeded) return null;
                if (count == 0 || count == 7 || edge <= BoundaryEdge * BoundaryEdge)
                    return Leaf(a, b, c, center);
            }

            Region left, right;
            if (ab >= bc && ab >= ca)
            {
                var m = Weights.Mid(a, b);
                left = Classify(sourceA, sourceB, sourceC, a, m, c, depth + 1, covered, budget);
                if (budget.Exceeded) return null;
                right = Classify(sourceA, sourceB, sourceC, m, b, c, depth + 1, covered, budget);
            }
            else if (bc >= ca)
            {
                var m = Weights.Mid(b, c);
                left = Classify(sourceA, sourceB, sourceC, a, b, m, depth + 1, covered, budget);
                if (budget.Exceeded) return null;
                right = Classify(sourceA, sourceB, sourceC, a, m, c, depth + 1, covered, budget);
            }
            else
            {
                var m = Weights.Mid(c, a);
                left = Classify(sourceA, sourceB, sourceC, a, b, m, depth + 1, covered, budget);
                if (budget.Exceeded) return null;
                right = Classify(sourceA, sourceB, sourceC, m, b, c, depth + 1, covered, budget);
            }
            if (budget.Exceeded) return null;
            // No extra vertices or triangles survive for spatially uniform regions.
            if (left.Leaf && right.Leaf && left.Covered == right.Covered) return Leaf(a, b, c, left.Covered);
            return new Region { A = a, B = b, C = c, Left = left, Right = right,
                CoveredFraction = left.CoveredFraction + right.CoveredFraction };
        }

        static Region Leaf(Weights a, Weights b, Weights c, bool covered) => new Region
        {
            A = a, B = b, C = c, Covered = covered,
            CoveredFraction = covered ? Math.Abs((b.A - a.A) * (c.B - a.B) - (b.B - a.B) * (c.A - a.A)) : 0
        };

        static bool Sample(Func<Point, bool> covered, Point p, Budget budget)
        {
            if (++budget.Samples > MaximumSamples) { budget.Exceeded = true; return false; }
            return covered(p);
        }

        static void Emit(Region region, int a, int b, int c, int originalCount, List<int> keep, List<int> indoors,
            List<AddedVertex> added, Dictionary<AddedVertex, int> vertexIndices, Budget budget)
        {
            if (budget.Exceeded) return;
            if (!region.Leaf)
            {
                Emit(region.Left, a, b, c, originalCount, keep, indoors, added, vertexIndices, budget);
                Emit(region.Right, a, b, c, originalCount, keep, indoors, added, vertexIndices, budget);
                return;
            }
            if (++budget.Fragments > MaximumFragments) { budget.Exceeded = true; return; }
            var output = region.Covered ? indoors : keep;
            output.Add(Index(a, b, c, region.A, originalCount, added, vertexIndices));
            output.Add(Index(a, b, c, region.B, originalCount, added, vertexIndices));
            output.Add(Index(a, b, c, region.C, originalCount, added, vertexIndices));
        }

        static int Index(int a, int b, int c, Weights weights, int originalCount,
            List<AddedVertex> added, Dictionary<AddedVertex, int> indices)
        {
            if (weights.A == 1) return a;
            if (weights.B == 1) return b;
            if (weights.C == 1) return c;
            var vertex = new AddedVertex(a, b, c, weights.A, weights.B, weights.C);
            if (!indices.TryGetValue(vertex, out int result))
            {
                result = originalCount + added.Count;
                indices.Add(vertex, result); added.Add(vertex);
            }
            return result;
        }

        static double FloorArea(Point a, Point b, Point c, double bottom, bool mirrored)
        {
            if (Math.Max(a.Y, Math.Max(b.Y, c.Y)) > bottom + FloorBand
                || Math.Min(a.Y, Math.Min(b.Y, c.Y)) < bottom - .01) return 0;
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!IsFinite(length) || length < 1e-8 || (mirrored ? -ny : ny) < .8 * length) return 0;
            return length * .5;
        }

        static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static double DistanceSquared(Point a, Point b) => (a.X - b.X) * (a.X - b.X)
            + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
        static Point Blend(Point a, Point b, Point c, Weights w) => new Point(
            a.X * w.A + b.X * w.B + c.X * w.C, a.Y * w.A + b.Y * w.B + c.Y * w.C,
            a.Z * w.A + b.Z * w.B + c.Z * w.C);
    }
}
