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
        foreach (var map in ArenaMapRegistry.All.Where(m => m != ArenaMapRegistry.Equator))
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
        foreach (var map in ArenaMapRegistry.All.Where(m => m != ArenaMapRegistry.Bay5))
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
        foreach (var map in ArenaMapRegistry.All.Where(m => m != ArenaMapRegistry.Equator))
            Assert.Empty(map.DistantShadowCasterPaths);
    }

    [Fact]
    public void PuddleWaterMaterialsMatchTheSidecarAndRemainBay5Only()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("bay5.json")));
        var names = doc.RootElement.GetProperty("puddle_water_material_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "70f045bb011600e7" }, names); // Original "puddle indoor static", not oil decals.
        Assert.Equal(names, ArenaMapRegistry.Bay5.PuddleWaterMaterialNames);
        foreach (var map in ArenaMapRegistry.All.Where(m => m != ArenaMapRegistry.Bay5))
            Assert.Empty(map.PuddleWaterMaterialNames);
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
