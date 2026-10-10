using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public class NeonNightTests
{
    [Theory]
    [InlineData(true, true, "arena_equator", 0, true)]
    [InlineData(false, true, "arena_equator", 0, false)]
    [InlineData(true, false, "arena_equator", 0, false)]
    [InlineData(true, true, "arena_equator", 12, false)]
    [InlineData(true, true, "arena_equator", null, false)]
    [InlineData(true, true, "arena_bay5", 0, false)]
    [InlineData(true, true, "factory4_night", 0, false)]
    public void MenuAndRaidRainShareTheSamePolicy(bool enabled, bool rain, string? key, int? hour, bool expected)
        => Assert.Equal(expected, ArenaNeonPolicy.RequestsRain(enabled, rain, key, hour));

    [Theory]
    [InlineData(true, "arena_equator", 0, true)]
    [InlineData(true, "arena_equator", 12, false)]
    [InlineData(true, "arena_equator", null, false)]
    [InlineData(false, "arena_equator", 0, false)]
    [InlineData(true, "arena_bay5", 0, false)]
    [InlineData(true, "factory4_night", 0, false)]
    [InlineData(true, null, 0, false)]
    public void OnlyConfirmedEquatorMidnightEnablesLights(bool enabled, string? key, int? hour, bool expected)
        => Assert.Equal(expected, ArenaNeonPolicy.IsActive(enabled, key, hour));

    [Fact]
    public void NightDayOtherNightSequenceDoesNotDependOnPreviousRaid()
    {
        var actual = new[] {
            ArenaNeonPolicy.IsActive(true, "arena_equator", 0),
            ArenaNeonPolicy.IsActive(true, "arena_equator", null),
            ArenaNeonPolicy.IsActive(true, "arena_equator", 12),
            ArenaNeonPolicy.IsActive(true, "arena_bay5", 0),
            ArenaNeonPolicy.IsActive(true, "arena_equator", 0) };
        Assert.Equal(new[] { true, false, false, false, true }, actual);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(35, 1)]
    [InlineData(42.5f, .5f)]
    [InlineData(50, 0)]
    [InlineData(1000, 0)]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    public void LightsFadeSmoothlyAndStopAtFiftyMetres(float distance, float expected)
        => Assert.Equal(expected, ArenaNeonPolicy.DistanceFade(distance));

    [Fact]
    public void OnlyEquatorOwnsAnInstallationAndItsCostIsBounded()
    {
        Assert.All(ArenaMapRegistry.Catalog.Where(m => m != ArenaMapRegistry.Equator), m => Assert.Empty(m.NeonFixtures));
        var fixtures = ArenaMapRegistry.Equator.NeonFixtures;
        Assert.InRange(fixtures.Length, 1, 12);
        Assert.Equal(fixtures.Length, fixtures.Select(f => f.Name).Distinct().Count());
        foreach (var fixture in fixtures)
        {
            Assert.InRange(fixture.Range, 1, 12);
            Assert.InRange(fixture.Height, .1f, 7);
            Assert.InRange(fixture.Intensity, .1f, 3);
            Assert.Equal(1f, fixture.NormalX * fixture.NormalX + fixture.NormalZ * fixture.NormalZ);
        }
    }

    [Fact]
    public void CompiledMountsMatchTheReviewableSidecar()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "tools/maps/equator.neon.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools/maps/equator.neon.json")));
        var nodes = data.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        var fixtures = ArenaMapRegistry.Equator.NeonFixtures;
        Assert.Equal(fixtures.Length, nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i]; var f = fixtures[i];
            Assert.Equal(f.Name, n.GetProperty("name").GetString());
            Assert.Equal(f.Magenta, n.GetProperty("magenta").GetBoolean());
            Assert.Equal(f.FlickerSeed, n.GetProperty("flickerSeed").GetInt32());
            Assert.Equal(new[] { f.X, f.Bottom, f.Z, f.Height, f.NormalX, f.NormalZ, f.Range, f.Intensity },
                new[] { "x", "bottom", "z", "height", "normalX", "normalZ", "range", "intensity" }.Select(k => n.GetProperty(k).GetSingle()));
        }
    }
}
