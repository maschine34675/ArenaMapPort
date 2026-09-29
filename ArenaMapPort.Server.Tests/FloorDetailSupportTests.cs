using ArenaMapPort.Client;
using Xunit;
using Point = ArenaMapPort.Client.IndoorSurfaceGeometry.Point;

namespace ArenaMapPort.Server.Tests;

public sealed class FloorDetailSupportTests
{
    static FloorDetailSupport.Face Face(Point a, Point b, Point c)
    {
        Assert.True(FloorDetailSupport.TryFace(a, b, c, out var face));
        return face;
    }

    static FloorDetailSupport Support(bool reversed = false)
    {
        var support = new FloorDetailSupport();
        support.Add(reversed ? Face(new(0,0,0), new(4,0,0), new(0,0,4))
            : Face(new(0,0,0), new(0,0,4), new(4,0,0)));
        return support;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UsesActualTriangleAndHeightRatherThanItsBoundingBox(bool reversed)
    {
        var support = Support(reversed);
        Assert.True(support.Contains(new(1,.03,1)));
        Assert.True(support.Contains(new(2,0,2))); // seam
        Assert.False(support.Contains(new(3,.03,3))); // inside AABB, outside triangle
        Assert.False(support.Contains(new(1,.201,1)));
        Assert.False(support.Contains(new(1,-.051,1)));
        Assert.False(support.Contains(new(double.NaN,0,1)));
    }

    [Fact]
    public void DetailNeedsAreaSupportAndCannotBridgeARealGap()
    {
        var supported = Face(new(.2,.03,.2), new(.2,.03,1.2), new(1.2,.03,.2));
        var gap = Face(new(3,.03,3), new(3,.03,4), new(4,.03,3));
        Assert.True(Support().Supports(new[] { supported }));
        Assert.False(Support().Supports(new[] { supported, gap }));
        Assert.False(Support().Supports(Array.Empty<FloorDetailSupport.Face>()));
    }

    [Fact]
    public void SpatialBucketsPreserveNegativeCoordinatesSeamsAndLargeFaceFallback()
    {
        var support = new FloorDetailSupport();
        support.Add(Face(new(-8,0,-8), new(-8,0,0), new(0,0,-8)));
        Assert.True(support.Contains(new(-4,.03,-4)));
        Assert.False(support.Contains(new(-1,.03,-1)));
        support.Add(Face(new(-1000,0,-1000), new(-1000,0,1000), new(1000,0,-1000)));
        Assert.True(support.Contains(new(-500,.03,-500)));
        Assert.False(support.Contains(new(500,.03,500)));
    }

    [Fact]
    public void FloorSlopeUsesInterpolatedHeightAndWallsCannotProvideSupport()
    {
        var support = new FloorDetailSupport();
        support.Add(Face(new(0,0,0), new(0,0,4), new(4,1,0)));
        Assert.True(support.Contains(new(2,.53,1)));
        Assert.False(support.Contains(new(2,.03,1)));
        Assert.False(FloorDetailSupport.TryFace(new(0,0,0), new(0,4,0), new(0,0,4), out _));
        Assert.False(FloorDetailSupport.TryFace(new(0,0,0), new(0,0,0), new(4,0,0), out _));
        Assert.False(FloorDetailSupport.TryFace(new(double.NaN,0,0), new(0,0,4), new(4,0,0), out _));
    }
}
