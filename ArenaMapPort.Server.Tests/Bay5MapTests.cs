using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class Bay5MapTests
{
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
