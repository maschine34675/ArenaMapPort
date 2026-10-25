using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class Bay5MapTests
{
    [Fact]
    public void FloorDetailInheritanceMatchesSidecarAndRemainsEquatorOnly()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("equator.json")));
        Assert.True(doc.RootElement.GetProperty("inherit_indoor_floor_details").GetBoolean());
        Assert.True(ArenaMapRegistry.Equator.InheritIndoorFloorDetails);
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => m != ArenaMapRegistry.Equator))
            Assert.False(map.InheritIndoorFloorDetails);
    }

    private static string Sidecar(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "maps", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void OpenedBarriersMatchTheBakeDescriptor()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("bay5.json")));
        Assert.Equal(doc.RootElement.GetProperty("opened_barriers").EnumerateArray().Select(e => e.GetString()),
                     ArenaMapRegistry.Bay5.OpenedBarriers);
    }

    [Fact]
    public void ShadowCasterRepairIsScopedToTheReportedBay5Assemblies()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("bay5.json")));
        var paths = doc.RootElement.GetProperty("restored_shadow_caster_roots").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] {
            "SBG_Arena_Bay5_Main/SOO/PROPS/Military_sandbag_barrier_03",
            "SBG_Arena_Bay5_Main/SOO/PROPS/Military_sandbag_barrier_03 (1)"
        }, paths);
        Assert.Equal(paths, ArenaMapRegistry.Bay5.RestoredShadowCasterRoots);
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => m != ArenaMapRegistry.Bay5))
            Assert.Empty(map.RestoredShadowCasterRoots);
    }

    [Fact]
    public void DistantCeilingCastersMatchSidecarAndRemainEquatorOnly()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("equator.json")));
        var paths = doc.RootElement.GetProperty("distant_shadow_caster_paths").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(3, paths.Length);
        Assert.Equal(paths, ArenaMapRegistry.Equator.DistantShadowCasterPaths);
        Assert.All(paths, path => Assert.EndsWith("_SHADOW_LOD0", path));
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => m != ArenaMapRegistry.Equator))
            Assert.Empty(map.DistantShadowCasterPaths);
    }

    // Puddle footsteps (PuddleFootstepRepairs; Bay5 since 2026-09-28, Equator, Air Pit and Bowl since 2026-10-25, see
    // docs/progress/puddle-footsteps-all-maps.md): the exact water material names of each released map and the ground surfaces besides
    // Concrete/Asphalt that may turn into the Puddle sound. 70f045bb011600e7 = "puddle indoor static" (fixed water level),
    // 0877f2335044042e = "puddle" (rain driven), 1b8546c701e1d1dc = "Airpit_puddle_white" (static). Oil decals share the shader and stay
    // out: Bay5's "puddle benz" 25519639aa567c85 and Air Pit's two inactive runway decals 823c859e2c5d11bc.
    [Theory]
    [InlineData("equator", "70f045bb011600e7,0877f2335044042e", "")]
    [InlineData("bay5", "70f045bb011600e7", "")]
    [InlineData("airpit", "1b8546c701e1d1dc,0877f2335044042e", "")]
    [InlineData("bowl", "0877f2335044042e", "Soil")] // the whole field is one Bowl_floor_BALLISTIC_soil collider
    public void PuddleDataMatchesTheSidecarAndTheExpectedLists(string stem, string materials, string surfaces)
    {
        var expectedMaterials = materials.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var expectedSurfaces = surfaces.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var map = ArenaMapRegistry.All.Single(m => m.Key == "arena_" + stem);
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(stem + ".json")));
        var names = doc.RootElement.GetProperty("puddle_water_material_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(expectedMaterials, names);
        Assert.Equal(names, map.PuddleWaterMaterialNames);
        var extra = doc.RootElement.TryGetProperty("puddle_extra_surfaces", out var surfaceList)
            ? surfaceList.EnumerateArray().Select(e => e.GetString()).ToArray() : new string?[0];
        Assert.Equal(expectedSurfaces, extra);
        Assert.Equal(extra, map.PuddleExtraSurfaces);
    }

    [Fact]
    public void ParkedMapsListNoPuddleDataAndTheSurfacesAreMembersOfTheNativeEnum()
    {
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => !ArenaMapRegistry.All.Contains(m)))
        {
            Assert.Empty(map.PuddleWaterMaterialNames);
            Assert.Empty(map.PuddleExtraSurfaces);
        }
        // The extra surfaces are BaseBallistic.ESurfaceSound names (EFT 0.16.9.5); Concrete and Asphalt are built in, Puddle is the result.
        var native = new[] { "Concrete", "Metal", "Wood", "Soil", "Grass", "Asphalt", "Glass", "Gravel", "MetalThin", "Puddle", "Slate",
            "Tile", "WoodThick", "Swamp", "Garbage", "GarbageMetal", "Plastic", "WholeGlass", "WoodThin", "Snow" };
        foreach (var map in ArenaMapRegistry.All)
        {
            Assert.All(map.PuddleExtraSurfaces, s => Assert.Contains(s, native));
            Assert.All(map.PuddleExtraSurfaces, s => Assert.DoesNotContain(s, new[] { "Concrete", "Asphalt", "Puddle" }));
            Assert.Equal(map.PuddleExtraSurfaces.Length, map.PuddleExtraSurfaces.Distinct().Count());
            Assert.Equal(map.PuddleWaterMaterialNames.Length, map.PuddleWaterMaterialNames.Distinct().Count());
            if (map.PuddleExtraSurfaces.Length > 0) Assert.NotEmpty(map.PuddleWaterMaterialNames);
        }
    }

    [Fact]
    public void BackgroundIsOwnedButExcludedFromGameplayWeatherClassification()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("bay5.json")));
        var names = doc.RootElement.GetProperty("background_scene_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(names, ArenaMapRegistry.Bay5.BackgroundSceneNames);
        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            Assert.Contains(name, ArenaMapRegistry.Bay5.AllSceneNames);
            Assert.True(ArenaMapPort.Client.ArenaMapRaid.IsOwnedScene(name!));
            Assert.False(ArenaMapPort.Client.ArenaMapRaid.IsGameplayScene(name!));
        }
        foreach (var map in ArenaMapRegistry.All)
            foreach (var name in map.AllSceneNames.Except(map.BackgroundSceneNames))
                Assert.True(ArenaMapPort.Client.ArenaMapRaid.IsGameplayScene(name));
        Assert.False(ArenaMapPort.Client.ArenaMapRaid.IsGameplayScene("Unknown_Background"));
        Assert.False(ArenaMapPort.Client.ArenaMapRaid.IsOwnedScene("Unknown_Background"));
    }

    [Fact]
    public void ExitTransformsMatchTheSidecarAndServerEligibility()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("bay5.exits.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "arena_bay5", "base.json")));
        var map = ArenaMapRegistry.Bay5;
        var authored = sidecar.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        var shipped = server.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        Assert.NotEmpty(map.Exits);
        Assert.Equal(map.Exits.Length, authored.Length);
        Assert.Equal(map.Exits.Length, shipped.Length);
        foreach (var expected in map.Exits)
        {
            var point = authored.Single(e => e.GetProperty("name").GetString() == expected.Name);
            Assert.Equal(new[] { expected.X, expected.Y, expected.Z }, point.GetProperty("position").EnumerateArray().Select(v => v.GetSingle()));
            Assert.Equal(new[] { expected.SizeX, expected.SizeY, expected.SizeZ }, point.GetProperty("box_size").EnumerateArray().Select(v => v.GetSingle()));
            Assert.Equal(expected.RotationY, point.GetProperty("rotation").GetSingle());
            var exit = shipped.Single(e => e.GetProperty("Name").GetString() == expected.Name);
            Assert.Equal(map.InfiltrationName, exit.GetProperty("EntryPoints").GetString());
            Assert.Equal("Individual", exit.GetProperty("ExfiltrationType").GetString());
            Assert.Equal("None", exit.GetProperty("PassageRequirement").GetString());
            Assert.Equal(100, exit.GetProperty("Chance").GetDouble());
            Assert.Equal(point.GetProperty("exfiltration_time").GetDouble(), exit.GetProperty("ExfiltrationTime").GetDouble());
        }
    }
}
