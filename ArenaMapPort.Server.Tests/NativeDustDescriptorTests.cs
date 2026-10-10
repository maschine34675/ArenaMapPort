using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class NativeDustDescriptorTests
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

    [Theory]
    [InlineData("equator.json", "arena_equator")]
    [InlineData("bay5.json", "arena_bay5")]
    public void EveryOriginalDustParameterMatchesThePipelineDescriptor(string sidecar, string key)
    {
        var map = ArenaMapRegistry.All.Single(m => m.Key == key);
        var dust = Assert.IsType<ArenaDustDescriptor>(map.Dust);
        using var document = JsonDocument.Parse(File.ReadAllText(Sidecar(sidecar)));
        var json = document.RootElement.GetProperty("dust");
        Assert.Equal(dust.Scene, json.GetProperty("scene").GetString());
        Assert.Equal(dust.Path, json.GetProperty("path").GetString());
        Assert.Equal(dust.TimeSpeed, json.GetProperty("time_speed").GetSingle());
        Assert.Equal(dust.Size, json.GetProperty("size").GetSingle());
        Assert.Equal(dust.QuadTileSetSide, json.GetProperty("quad_tile_set_side").GetInt32());
        Assert.Equal(dust.UvTilePadding, json.GetProperty("uv_tile_padding").GetSingle());
        Assert.Equal(new[] { dust.CenterX, dust.CenterY, dust.CenterZ },
            json.GetProperty("bounds_center").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal(new[] { dust.ExtentX, dust.ExtentY, dust.ExtentZ },
            json.GetProperty("bounds_extents").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Contains(dust.Scene, map.AllSceneNames);
        Assert.True(dust.ExtentX > 0 && dust.ExtentY > 0 && dust.ExtentZ > 0);
    }

    [Fact]
    public void OnlyTheTwoAuditedOriginalParentsReceiveAnimation()
    {
        Assert.Equal("Arena_Equator_Lighting", ArenaMapRegistry.Equator.Dust!.Scene);
        Assert.Equal("EQUATOR_DUST", ArenaMapRegistry.Equator.Dust.Path);
        Assert.Equal(0.09f, ArenaMapRegistry.Equator.Dust.TimeSpeed);
        Assert.Equal(0.03f, ArenaMapRegistry.Equator.Dust.Size);
        Assert.Equal(1.0f, ArenaMapRegistry.Equator.Dust.UvTilePadding);
        Assert.Equal("Arena_Bay5_Light", ArenaMapRegistry.Bay5.Dust!.Scene);
        Assert.Equal("DUST", ArenaMapRegistry.Bay5.Dust.Path);
        Assert.Equal(0.07f, ArenaMapRegistry.Bay5.Dust.TimeSpeed);
        Assert.Equal(0.04f, ArenaMapRegistry.Bay5.Dust.Size);
        Assert.Equal(0.0f, ArenaMapRegistry.Bay5.Dust.UvTilePadding);
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => m != ArenaMapRegistry.Equator && m != ArenaMapRegistry.Bay5))
            Assert.Null(map.Dust);
    }
}
