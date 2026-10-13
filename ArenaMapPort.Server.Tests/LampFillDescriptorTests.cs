using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// LampFillRepairs reads its cells from <see cref="ArenaLampFillDescriptor"/> (docs/progress/airpit-release.md, "Passport-hall
/// ceiling lamps"): a flat x0, z0, x1, z1, ... array read as pairs, plus the name of each copy. These tests pin the helpers
/// the client depends on and the shape of AirPit's literal; the sidecar mirror is AirPitMapTests.LampFillMatchesTheSidecar.
/// </summary>
public sealed class LampFillDescriptorTests
{
    [Fact]
    public void CellsAreReadAsXZPairs()
    {
        var fill = new ArenaLampFillDescriptor("Root/Group/Lamp", 6.5f, new[] { 1f, 2f, -3f, 4f, 5.5f, -6.5f });
        Assert.Equal("Root/Group/Lamp", fill.TemplatePath);
        Assert.Equal(6.5f, fill.Y);
        Assert.Equal(3, fill.Count);
        Assert.Equal((1f, 2f), (fill.CellX(0), fill.CellZ(0)));
        Assert.Equal((-3f, 4f), (fill.CellX(1), fill.CellZ(1)));
        Assert.Equal((5.5f, -6.5f), (fill.CellX(2), fill.CellZ(2)));
    }

    [Fact]
    public void NoCellsAndATrailingUnpairedValueCountCorrectly()
    {
        Assert.Equal(0, new ArenaLampFillDescriptor("A/B", 0f, new float[0]).Count);
        // An odd array is a data error the consistency tests refuse; the helper must not read past the end because of it.
        var odd = new ArenaLampFillDescriptor("A/B", 0f, new[] { 1f, 2f, 3f });
        Assert.Equal(1, odd.Count);
        Assert.Equal((1f, 2f), (odd.CellX(0), odd.CellZ(0)));
    }

    [Fact]
    public void CloneNamesCarryTheTemplateNameAndTheCellIndex()
    {
        Assert.Equal("Airport_lamp_02_disabled (fill 0)", ArenaLampFillDescriptor.CloneName("Airport_lamp_02_disabled", 0));
        Assert.Equal("Airport_lamp_02_disabled (fill 80)", ArenaLampFillDescriptor.CloneName("Airport_lamp_02_disabled", 80));
    }

    // The literal in ArenaMapRegistry.AirPit: 81 distinct cells on the passport-hall lattice, the template a path below a
    // scene root, nothing so close to another cell that two lamp discs (2.56 m) could overlap.
    [Fact]
    public void AirPitsLiteralIsWellFormed()
    {
        var fill = ArenaMapRegistry.AirPit.LampFill;
        Assert.NotNull(fill);
        Assert.Equal(0, fill!.Cells.Length % 2);
        Assert.Equal(81, fill.Count);
        Assert.True(float.IsFinite(fill.Y) && fill.Y > 0f);
        var segments = fill.TemplatePath.Split('/');
        Assert.True(segments.Length >= 2 && segments.All(s => s.Length > 0 && s == s.Trim()), fill.TemplatePath);
        Assert.Equal("SBG_Airpit_Light", segments[0]); // a scene root of Arena_Airpit_Light, not a scene name
        Assert.Equal("Airport_lamp_02_disabled", segments[^1]);
        for (var i = 0; i < fill.Count; i++)
        {
            Assert.True(float.IsFinite(fill.CellX(i)) && float.IsFinite(fill.CellZ(i)), $"cell {i}");
            Assert.InRange(fill.CellX(i), -38f, 15f);
            Assert.InRange(fill.CellZ(i), 55f, 84f);
            for (var j = i + 1; j < fill.Count; j++)
            {
                var dx = fill.CellX(i) - fill.CellX(j);
                var dz = fill.CellZ(i) - fill.CellZ(j);
                Assert.True(MathF.Sqrt(dx * dx + dz * dz) >= 3f, $"cells {i} and {j} are closer than 3 m");
            }
        }
    }

    // Arena_AirPit_ShootOut/.../OO_Building/Airport_column_02 stands twice in the lattice area (axes from the v8 package,
    // tools/tests/test_lamp_fill.py measures the same from it). At the lamp discs' height (y 5.35-5.48) its LOD0 mesh is
    // 0.913 m in radius, a copy's flat disc 1.28 m: a cell nearer than 2.19 m to an axis would sink the rim into the
    // column (review 2026-10-13: (-30.37, 73.13) did, by 0.12 m, and was dropped).
    [Theory]
    [InlineData(-32.44f, 73.18f)]
    [InlineData(10.05f, 73.34f)]
    public void NoLampDiscSinksIntoARoundColumn(float columnX, float columnZ)
    {
        const float DiscRadius = 1.28f, ColumnRadiusAtTheDiscs = 0.913f;
        var fill = ArenaMapRegistry.AirPit.LampFill!;
        var nearest = float.MaxValue;
        for (var i = 0; i < fill.Count; i++)
        {
            var dx = fill.CellX(i) - columnX;
            var dz = fill.CellZ(i) - columnZ;
            nearest = MathF.Min(nearest, MathF.Sqrt(dx * dx + dz * dz));
        }
        Assert.True(nearest >= DiscRadius + ColumnRadiusAtTheDiscs, $"nearest cell is {nearest:F2} m from the column axis");
    }
}
