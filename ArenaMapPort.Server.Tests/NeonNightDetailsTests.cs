using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public class NeonNightDetailsTests
{
    [Theory]
    [InlineData(true, "arena_equator", 0, 2.06f)]
    [InlineData(true, "arena_equator", 12, 1.5f)]
    [InlineData(true, "arena_equator", null, 1.5f)]
    [InlineData(false, "arena_equator", 0, 1.5f)]
    [InlineData(true, "arena_bay5", 0, 1.5f)]
    [InlineData(true, null, 0, 1.5f)]
    public void WetFloorOnlyChangesAnOwnedConfirmedNight(bool owned, string? map, int? hour, float expected)
        => Assert.Equal(expected, ArenaNeonPolicy.WetFloorSmoothness(owned, map, hour, 1.5f, .8f), 6);

    [Fact]
    public void LiveUserEditsAndDisableUseCurrentBaselineWithoutAccumulation()
    {
        float configured = 1.5f;
        for (int i = 0; i < 100; i++)
            Assert.Equal(2.06f, ArenaNeonPolicy.WetFloorSmoothness(true, "arena_equator", 0, configured, .8f), 6);
        configured = 1.8f; // Saved edit during the raid.
        Assert.Equal(2.12f, ArenaNeonPolicy.WetFloorSmoothness(true, "arena_equator", 0, configured, .8f), 6);
        Assert.Equal(configured, ArenaNeonPolicy.WetFloorSmoothness(false, "arena_equator", 0, configured, .8f));
        Assert.Equal(configured, ArenaNeonPolicy.WetFloorSmoothness(true, "arena_equator", 12, configured, .8f));
    }

    [Theory]
    [InlineData(0, .5f)]
    [InlineData(-1, .5f)]
    [InlineData(float.NaN, .5f)]
    [InlineData(float.PositiveInfinity, .5f)]
    [InlineData(.5f, 1.35f)]
    [InlineData(1, 2.2f)]
    [InlineData(2, 2.2f)]
    public void WetStrengthIsBoundedAndInvalidValuesAreInert(float strength, float expected)
        => Assert.Equal(expected, ArenaNeonPolicy.WetFloorSmoothness(true, "arena_equator", 0, .5f, strength), 6);

    [Fact]
    public void AlreadyHighAuthoredMultipliersAreNotReduced()
    {
        foreach (float strength in new[] { 0f, .3f, .8f, 1f, 5f })
            Assert.Equal(2.4f, ArenaNeonPolicy.WetFloorSmoothness(true, "arena_equator", 0, 2.4f, strength));
    }

    [Theory]
    [InlineData("equator_main_floor_LOD0", true)]
    [InlineData("equator_lounge_zone_LOD0", true)]
    [InlineData("Sandbag_fortification_04_LOD0", false)]
    [InlineData("van_backdoor_LOD0", false)]
    [InlineData(null, false)]
    public void WetFloorDoesNotSelectOrdinaryIndoorProps(string? name, bool expected)
        => Assert.Equal(expected, ArenaNeonPolicy.IsFloorRenderer(name!));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(20, 1)]
    [InlineData(25, .5f)]
    [InlineData(30, 0)]
    [InlineData(100, 0)]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    public void SmallPointLightsHaveABoundedFade(float distance, float expected)
        => Assert.Equal(expected, ArenaNeonPolicy.AccentDistanceFade(distance));

    [Fact]
    public void AccentLayoutHasOnlyEquatorOwnershipAndMatchesReviewedPlacementData()
    {
        Assert.All(ArenaMapRegistry.All.Where(m => m != ArenaMapRegistry.Equator), m => Assert.Empty(m.NightAccents));
        var accents = ArenaMapRegistry.Equator.NightAccents;
        Assert.InRange(accents.Length, 1, 12); // Bound realtime point-shadow cost.
        Assert.Equal(accents.Length, accents.Select(a => a.Name).Distinct().Count());
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "tools/maps/equator.accents.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools/maps/equator.accents.json")));
        var nodes = data.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        Assert.Equal(accents.Length, nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
        {
            Assert.Equal(accents[i].Name, nodes[i].GetProperty("name").GetString());
            Assert.Equal(accents[i].Color.ToString(), nodes[i].GetProperty("color").GetString());
            Assert.Equal(new[] { accents[i].X, accents[i].Z, accents[i].Yaw },
                new[] { "x", "z", "yaw" }.Select(k => nodes[i].GetProperty(k).GetSingle()));
        }
    }
}
