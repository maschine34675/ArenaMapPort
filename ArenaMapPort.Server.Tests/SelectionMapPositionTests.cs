using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class SelectionMapPositionTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    [Fact]
    public void ScreenshotPositionsMatchCatalogSidecarsAndDatabase()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", "selection", "markers.json")));
        var markers = document.RootElement.GetProperty("markers").EnumerateArray().ToArray();
        Assert.Equal(14, markers.Length);
        var mapped = markers.Where(m => m.GetProperty("catalog_key").ValueKind == JsonValueKind.String).ToArray();
        Assert.Equal(ArenaMapRegistry.Catalog.Where(m => m.Key != "arena_airpit2").Select(m => m.Key).Order(),
            mapped.Select(m => m.GetProperty("catalog_key").GetString()).Order());
        foreach (var marker in mapped)
        {
            string key = marker.GetProperty("catalog_key").GetString()!;
            var map = ArenaMapRegistry.Catalog.Single(m => m.Key == key);
            int x = marker.GetProperty("icon_x").GetInt32(), y = marker.GetProperty("icon_y").GetInt32();
            Assert.Equal(x, map.IconX);
            Assert.Equal(y, map.IconY);
            using var sidecar = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", key[6..] + ".json")));
            Assert.Equal(x, sidecar.RootElement.GetProperty("icon_x").GetInt32());
            Assert.Equal(y, sidecar.RootElement.GetProperty("icon_y").GetInt32());
            using var database = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", map.DbBaseJsonPath)));
            Assert.Equal(x, database.RootElement.GetProperty("IconX").GetInt32());
            Assert.Equal(y, database.RootElement.GetProperty("IconY").GetInt32());
        }
    }

    [Fact]
    public void StoredAnchorsReconstructTheMeasuredScreenshotPinCenters()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", "selection", "markers.json")));
        var root = document.RootElement;
        var calibration = root.GetProperty("calibration");
        double scale = calibration.GetProperty("scale").GetDouble();
        double tx = calibration.GetProperty("offset_x").GetDouble(), ty = calibration.GetProperty("offset_y").GetDouble();
        Assert.InRange(calibration.GetProperty("max_landmark_residual_px").GetDouble(), 0, 1);
        Assert.Equal(800, calibration.GetProperty("geographic_size").GetInt32());
        Assert.Equal(5, calibration.GetProperty("spt_inset").GetInt32());
        foreach (var marker in root.GetProperty("markers").EnumerateArray())
        {
            int x = marker.GetProperty("icon_x").GetInt32(), y = marker.GetProperty("icon_y").GetInt32();
            Assert.InRange(x, 0, 1000);
            Assert.InRange(y, 0, 1000);
            // EFT anchors run bottom-up in a 790px container inside the 800px image.
            // Reproject to the original 1080px texture, then to the 2560x1440 screenshot.
            double screenX = (140 + 5 + x * .790) * scale + tx;
            double screenY = (795 - y * .790) * scale + ty;
            var measured = marker.GetProperty("screen_center");
            Assert.InRange(Math.Abs(screenX - measured[0].GetDouble()), 0, .6);
            Assert.InRange(Math.Abs(screenY - measured[1].GetDouble()), 0, .6);
            Assert.False(string.IsNullOrWhiteSpace(marker.GetProperty("screenshot").GetString()));
        }
    }

    [Fact]
    public void FutureMeasurementsDoNotActivateUnreleasedMapsOrAssignAirPit2TheAirPitPin()
    {
        Assert.Equal(new[] { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" }, ArenaMapRegistry.All.Select(m => m.Key));
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", "selection", "markers.json")));
        var unmapped = document.RootElement.GetProperty("markers").EnumerateArray()
            .Where(m => m.GetProperty("catalog_key").ValueKind == JsonValueKind.Null).ToArray();
        Assert.Equal("Arena_LabsReplica", Assert.Single(unmapped).GetProperty("source_key").GetString());
        Assert.Equal(295, ArenaMapRegistry.AirPit2.IconX);
        Assert.Equal(830, ArenaMapRegistry.AirPit2.IconY);
    }
}
