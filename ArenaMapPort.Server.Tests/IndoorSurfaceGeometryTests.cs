#nullable disable
using System;
using System.Linq;
using ArenaMapPort.Client;
using Xunit;
using Point = ArenaMapPort.Client.IndoorSurfaceGeometry.Point;

namespace ArenaMapPort.Server.Tests;

public sealed class IndoorSurfaceGeometryTests
{
    // Two adjacent 32 m² floor triangles, plus a wall, a raised ledge and a decal slot.
    static readonly Point[] Vertices = {
        new(0, 1.2, 0), new(0, 1.2, 8), new(8, 1.2, 0),
        new(20, 1.2, 0), new(20, 1.2, 8), new(28, 1.2, 0),
        new(0, 0, 0), new(0, 8, 0), new(8, 0, 0),
        new(0, 3, 0), new(0, 3, 8), new(8, 3, 0),
    };
    static readonly int[][] Indices = { new[] { 0,1,2, 3,4,5, 6,7,8, 9,10,11 }, new[] { 0,1,2 } };

    [Fact]
    public void MixedShellMovesOnlyCoveredFloorAndPreservesEveryOriginalTriangle()
    {
        var before = Indices.Select(x => x.ToArray()).ToArray();
        var result = IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, 0, false, p => p.X < 10);
        Assert.Equal(1, result.Triangles);
        Assert.Equal(new[] { 0,1,2 }, result.Covered[0]);
        Assert.Equal(new[] { 3,4,5, 6,7,8, 9,10,11 }, result.Retained[0]);
        Assert.Empty(result.Covered[1]);
        Assert.Equal(Indices[1], result.Retained[1]);
        for (int s = 0; s < before.Length; s++)
        {
            Assert.Equal(before[s], Indices[s]);
            Assert.Equal(Triangles(before[s]), Triangles(result.Covered[s].Concat(result.Retained[s]).ToArray()));
        }
    }

    [Fact]
    public void PartialRoofTriangleSplitsCoveredFloorAndRetainsOutdoorRemainder()
    {
        var result = IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, 0, false,
            p => p.X < 4 && p.Z < 4);
        Assert.NotNull(result); // examined; do not fall back to whole-renderer classification
        Assert.True(result.Triangles > 1);
        Assert.NotEmpty(result.AddedVertices);
        var expanded = Expand(Vertices, result);
        Assert.Equal(16, Area(expanded, result.Covered[0]), 8);
        Assert.Equal(Area(Vertices, Indices[0]), Area(expanded, result.Covered[0]) + Area(expanded, result.Retained[0]), 8);
        Assert.Equal(Indices[1], result.Retained[1]);
        AssertUpward(expanded, result.Covered[0]);
        Assert.Empty(result.Covered[1]);
    }

    [Fact]
    public void LongTriangleFindsRoofStripMissedByOldFourInteriorSamples()
    {
        var result = IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, 0, false,
            p => p.X > 3 && p.X < 4);
        Assert.NotNull(result);
        Assert.NotEmpty(result.Covered[0]);
        var expanded = Expand(Vertices, result);
        Assert.Equal(4.5, Area(expanded, result.Covered[0]), 8);
        Assert.Equal(Area(Vertices, Indices[0]), Area(expanded, result.Covered[0]) + Area(expanded, result.Retained[0]), 8);
    }

    [Fact]
    public void InteriorRoofGapDoesNotDisappearInsideAnOtherwiseCoveredTriangle()
    {
        var result = IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, 0, false,
            p => p.X < 10 && !(p.X > 3 && p.X < 4));
        var expanded = Expand(Vertices, result);
        Assert.Equal(27.5, Area(expanded, result.Covered[0]), 8);
        Assert.Equal(Area(Vertices, Indices[0]), Area(expanded, result.Covered[0]) + Area(expanded, result.Retained[0]), 8);
    }

    [Fact]
    public void SlantedRoofBoundaryHasOnlySmallCellErrorAndPreservesWinding()
    {
        double Distance(Point p) => p.X + .37 * p.Z - 4.3;
        var result = IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, 0, false,
            p => Distance(p) < 0);
        var expanded = Expand(Vertices, result);
        AssertUpward(expanded, result.Covered[0]);
        Assert.All(result.Covered[0], i => Assert.True(Distance(expanded[i]) <= IndoorSurfaceGeometry.BoundaryEdge * 1.1));
        foreach (var triangle in Triples(result.Retained[0]))
        {
            if (triangle.All(i => Math.Abs(expanded[i].Y - 1.2) < 1e-8))
            {
                AssertUpward(expanded, triangle);
                Assert.All(triangle, i => Assert.True(Distance(expanded[i]) >= -IndoorSurfaceGeometry.BoundaryEdge * 1.1));
            }
        }
        Assert.Equal(Area(Vertices, Indices[0]), Area(expanded, result.Covered[0]) + Area(expanded, result.Retained[0]), 8);
    }

    [Fact]
    public void DerivedVerticesPreservePlaneAndEveryAffineVertexChannel()
    {
        var result = IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, 0, false,
            p => p.X + p.Z * .37 < 4.3);
        Assert.NotEmpty(result.AddedVertices);
        foreach (var v in result.AddedVertices)
        {
            Assert.InRange(v.WA, 0, 1); Assert.InRange(v.WB, 0, 1); Assert.InRange(v.WC, 0, 1);
            Assert.Equal(1, v.WA + v.WB + v.WC, 12);
            Assert.InRange(v.A, 0, Vertices.Length - 1);
            Assert.InRange(v.IndexB, 0, Vertices.Length - 1);
            Assert.InRange(v.IndexC, 0, Vertices.Length - 1);
            var p = v.Position(Vertices);
            Assert.Equal(1.2, p.Y, 12);
            Assert.InRange(p.X, 0, 8); Assert.InRange(p.Z, 0, 8);
            // UV, normal, tangent and color components all obey the same linear interpolation.
            foreach (var channel in new Func<Point, double>[] {
                q => q.X / 8, q => q.Z / 3, q => 2.1 * q.X - 3.2 * q.Y + 7 * q.Z + .6,
                _ => -1, _ => .2 })
                Assert.Equal(channel(p), v.Interpolate(channel(Vertices[v.A]),
                    channel(Vertices[v.IndexB]), channel(Vertices[v.IndexC])), 10);
        }
    }

    [Fact]
    public void SharedOriginalEdgeUsesOneCanonicalInterpolatedVertex()
    {
        var vertices = new[] { new Point(0,1.2,0), new Point(0,1.2,8), new Point(8,1.2,0), new Point(8,1.2,8) };
        var result = IndoorSurfaceGeometry.Split(vertices, new[] { new[] { 0,1,2, 2,1,3 } },
            new[] { true }, 0, false, p => p.X < 3.7);
        var points = result.AddedVertices.Select(v => v.Position(vertices)).ToArray();
        var edgePoints = points.Where(p => Math.Abs(p.X + p.Z - 8) < 1e-10).ToArray();
        Assert.NotEmpty(edgePoints);
        Assert.Equal(edgePoints.Length, edgePoints.Select(p => (p.X, p.Y, p.Z)).Distinct().Count());
        var expanded = Expand(vertices, result);
        Assert.Equal(64, Area(expanded, result.Covered[0]) + Area(expanded, result.Retained[0]), 8);
        AssertUpward(expanded, result.Covered[0]); AssertUpward(expanded, result.Retained[0]);
    }

    [Fact]
    public void EmptyRoofReturnsExaminedButUnchanged()
    {
        var result = IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, 0, false, _ => false);
        Assert.NotNull(result);
        Assert.Equal(0, result.Triangles);
        Assert.Empty(result.AddedVertices);
        Assert.Equal(Indices[0], result.Retained[0]);
    }

    [Fact]
    public void MirroredTransformKeepsAuthoredUpwardFaceEligible()
    {
        var mirrored = Vertices.Select(p => new Point(-p.X, p.Y, p.Z)).ToArray();
        var result = IndoorSurfaceGeometry.Split(mirrored, Indices, new[] { true, false }, 0, true, _ => true);
        Assert.Equal(2, result.Triangles);
        Assert.Null(IndoorSurfaceGeometry.Split(mirrored, Indices, new[] { true, false }, 0, false, _ => true));
    }

    [Fact]
    public void FoundationOffsetAdmitsFloorButNotRaisedLedge()
    {
        var result = IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, 0, false, _ => true);
        Assert.Equal(new[] { 0,1,2, 3,4,5 }, result.Covered[0]);
        Assert.Equal(new[] { 6,7,8, 9,10,11 }, result.Retained[0]);
    }

    [Fact]
    public void SmallClutterIsRejectedBeforeAnyGpuCoverSamples()
    {
        var small = Vertices.Select(p => new Point(p.X / 4, p.Y, p.Z / 4)).ToArray();
        Assert.Null(IndoorSurfaceGeometry.Split(small, Indices, new[] { true, false }, 0, false,
            _ => throw new InvalidOperationException("Clutter must not sample cover")));
    }

    [Fact]
    public void SmallCoveredSlotIsKeptEvenWhenOtherSlotMakesLargeBuilding()
    {
        var vertices = Vertices.Concat(new[] { new Point(40,1.2,0), new Point(40,1.2,2), new Point(42,1.2,0) }).ToArray();
        var result = IndoorSurfaceGeometry.Split(vertices, new[] { Indices[0], new[] { 12,13,14 } },
            new[] { true, true }, 0, false, _ => true);
        Assert.Equal(2, result.Triangles);
        Assert.Empty(result.Covered[1]);
        Assert.Equal(new[] { 12,13,14 }, result.Retained[1]);
    }

    [Theory]
    [InlineData(-1)] [InlineData(12)]
    public void InvalidIndicesFailClosed(int invalid)
    {
        Assert.Null(IndoorSurfaceGeometry.Split(Vertices, new[] { new[] { 0,1,invalid } },
            new[] { true }, 0, false, _ => true));
    }

    [Fact]
    public void MalformedAndNonfiniteDataFailClosed()
    {
        Assert.Null(IndoorSurfaceGeometry.Split(Vertices, new[] { new[] { 0,1 } }, new[] { true }, 0, false, _ => true));
        var broken = Vertices.ToArray(); broken[0] = new Point(double.NaN, 0, 0);
        Assert.Null(IndoorSurfaceGeometry.Split(broken, Indices, new[] { true, false }, 0, false, _ => true));
        Assert.Null(IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true }, 0, false, _ => true));
        Assert.Null(IndoorSurfaceGeometry.Split(Vertices, new int[][] { null }, new[] { true }, 0, false, _ => true));
        Assert.Null(IndoorSurfaceGeometry.Split(Vertices, Indices, new[] { true, false }, double.PositiveInfinity, false, _ => true));
    }

    [Fact]
    public void DegenerateAndDownwardFacesRemainExactlyOnceInOriginalSlot()
    {
        var indices = new[] { new[] { 0,1,2, 3,4,5, 0,0,1, 0,2,1 } };
        var result = IndoorSurfaceGeometry.Split(Vertices, indices, new[] { true }, 0, false, _ => true);
        Assert.Equal(new[] { 0,0,1, 0,2,1 }, result.Retained[0]);
        Assert.Equal(new[] { 0,1,2, 3,4,5 }, result.Covered[0]);
        Assert.Empty(result.AddedVertices);
    }

    [Fact]
    public void HugeFloorFailsEntirePartitionClosedWithinSampleBudget()
    {
        var vertices = new[] { new Point(0,1.2,0), new Point(0,1.2,1000), new Point(1000,1.2,0) };
        int samples = 0;
        Assert.Null(IndoorSurfaceGeometry.Split(vertices, new[] { new[] { 0,1,2 } }, new[] { true },
            0, false, _ => { samples++; return true; }));
        Assert.InRange(samples, 1, IndoorSurfaceGeometry.MaximumSamples);
    }

    [Fact]
    public void ExtremeEdgeLengthFailsDepthBudgetWithoutStackOverflow()
    {
        var vertices = new[] { new Point(0,1.2,0), new Point(0,1.2,1e9), new Point(1e9,1.2,0) };
        Assert.Null(IndoorSurfaceGeometry.Split(vertices, new[] { new[] { 0,1,2 } }, new[] { true },
            0, false, _ => throw new InvalidOperationException("Depth budget must stop before fine probes")));
    }

    [Fact]
    public void TooManyOriginalFragmentsFailBeforeCoverSampling()
    {
        var indices = Enumerable.Range(0, IndoorSurfaceGeometry.MaximumFragments + 1).SelectMany(_ => new[] { 0,1,2 }).ToArray();
        Assert.Null(IndoorSurfaceGeometry.Split(Vertices, new[] { indices }, new[] { true }, 0, false,
            _ => throw new InvalidOperationException("Fragment budget must stop before cover probes")));
    }

    [Fact]
    public void SubdivisionThatExceedsFinalFragmentBudgetDiscardsEntireResult()
    {
        var indices = new[] { 0,1,2 }.Concat(Enumerable.Range(0, IndoorSurfaceGeometry.MaximumFragments - 1)
            .SelectMany(_ => new[] { 0,0,0 })).ToArray();
        Assert.Null(IndoorSurfaceGeometry.Split(Vertices, new[] { indices }, new[] { true }, 0, false,
            p => p.X < 4 && p.Z < 4));
        Assert.Equal(IndoorSurfaceGeometry.MaximumFragments * 3, indices.Length);
        Assert.Equal(new[] { 0,1,2 }, indices.Take(3));
    }

    static Point[] Expand(Point[] source, IndoorSurfaceGeometry.Partition partition) =>
        source.Concat(partition.AddedVertices.Select(v => v.Position(source))).ToArray();

    static System.Collections.Generic.IEnumerable<int[]> Triples(int[] indices) =>
        Enumerable.Range(0, indices.Length / 3).Select(i => indices.Skip(i * 3).Take(3).ToArray());

    static double Area(Point[] vertices, int[] indices) => Triples(indices).Sum(t =>
    {
        var a = vertices[t[0]]; var b = vertices[t[1]]; var c = vertices[t[2]];
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
        return Math.Sqrt(x * x + y * y + z * z) / 2;
    });

    static void AssertUpward(Point[] vertices, int[] indices)
    {
        foreach (var t in Triples(indices))
        {
            var a = vertices[t[0]]; var b = vertices[t[1]]; var c = vertices[t[2]];
            Assert.True((b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z) > 0);
        }
    }

    static string[] Triangles(int[] indices) => Enumerable.Range(0, indices.Length / 3)
        .Select(i => string.Join(",", indices.Skip(i * 3).Take(3))).OrderBy(x => x).ToArray();
}
