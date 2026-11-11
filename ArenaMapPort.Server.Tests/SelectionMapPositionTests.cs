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
            // A "display" block moves the shown anchor away from the measured one, with its reason; the measurement stays the record.
            var shown = marker.TryGetProperty("display", out var display) ? display : marker;
            int x = shown.GetProperty("icon_x").GetInt32(), y = shown.GetProperty("icon_y").GetInt32();
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
    public void OnlyIcebergAndChopShopAreDisplayedAwayFromTheirMeasurement()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", "selection", "markers.json")));
        var moved = document.RootElement.GetProperty("markers").EnumerateArray().Where(m => m.TryGetProperty("display", out _)).ToArray();
        Assert.Equal(new[] { "arena_autoservice", "arena_iceberg" },
            moved.Select(m => m.GetProperty("catalog_key").GetString()).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var marker in moved)
        {
            var display = marker.GetProperty("display");
            Assert.False(string.IsNullOrWhiteSpace(display.GetProperty("reason").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(display.GetProperty("decision").GetString()));
        }
    }

    [Fact]
    public void ChopShopIsDisplayedClearOfTheCustomsInterchangeAndEquatorLabels()
    {
        // GPU-2, PMC flow on EFT's own region map, 1920x1080, 2026-11-11: dots measured at Chop Shop (594, 424) for the anchor
        // 407/471, Iceberg (441, 315) for 215/608 and Equator (894, 384) for 788/521, so a dot moves 0.79 px per IconX unit and
        // 0.796 px per IconY unit (y grows downwards). A toggle's box runs from 10 px left of its dot to its label's end; the label
        // 'ARENA: CHOP SHOP' ends 236 px right of the dot and the box is 33 px high around the dot.
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", "selection", "markers.json")));
        var chopShop = document.RootElement.GetProperty("markers").EnumerateArray()
            .Single(m => m.GetProperty("catalog_key").ValueKind == JsonValueKind.String && m.GetProperty("catalog_key").GetString() == "arena_autoservice");
        var display = chopShop.GetProperty("display");
        Assert.Equal((display.GetProperty("icon_x").GetInt32(), display.GetProperty("icon_y").GetInt32()),
            (ArenaMapRegistry.AutoService.IconX, ArenaMapRegistry.AutoService.IconY));
        double dotX = 594 + (ArenaMapRegistry.AutoService.IconX - 407) * .790;
        double dotY = 424 - (ArenaMapRegistry.AutoService.IconY - 471) * .796;
        var box = (left: dotX - 10, top: dotY - 17, right: dotX + 246, bottom: dotY + 16);
        // The labels the measured position collided with or now sits beside, read from the same screenshot.
        var labels = new[]
        {
            ("CUSTOMS", (left: 696.0, top: 415.0, right: 842.0, bottom: 448.0)),
            ("INTERCHANGE", (left: 783.0, top: 494.0, right: 981.0, bottom: 527.0)),
            ("ARENA: EQUATOR", (left: 884.0, top: 367.0, right: 1115.0, bottom: 400.0)),
        };
        foreach (var (name, label) in labels)
        {
            double gapX = Math.Max(label.left - box.right, box.left - label.right);
            double gapY = Math.Max(label.top - box.bottom, box.top - label.bottom);
            Assert.True(Math.Max(gapX, gapY) >= 12, $"{name}: gap {Math.Max(gapX, gapY):F0} px");
        }
    }

    [Fact]
    public void IcebergIsDisplayedClearOfArenaModesPanel()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", "selection", "markers.json")));
        var iceberg = document.RootElement.GetProperty("markers").EnumerateArray()
            .Single(m => m.GetProperty("catalog_key").ValueKind == JsonValueKind.String && m.GetProperty("catalog_key").GetString() == "arena_iceberg");
        var display = iceberg.GetProperty("display");
        Assert.False(string.IsNullOrWhiteSpace(display.GetProperty("reason").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(display.GetProperty("decision").GetString()));
        Assert.Equal(iceberg.GetProperty("icon_y").GetInt32(), display.GetProperty("icon_y").GetInt32());
        // GPU-2 2026-11-01 at 1920x1080: the toggle's left edge moves 0.79 px per anchor unit from x 349 at the measured 110;
        // ArenaMode's mode panel ends at x 416 (56 + 360, ArenaModeSelectionView). Keep at least 12 px between them.
        int shift = display.GetProperty("icon_x").GetInt32() - iceberg.GetProperty("icon_x").GetInt32();
        Assert.True(349 + shift * .790 >= 416 + 12, $"left edge {349 + shift * .790:F0} px");
    }

    [Fact]
    public void FutureMeasurementsDoNotActivateUnreleasedMapsOrAssignAirPit2TheAirPitPin()
    {
        Assert.Equal(new[] { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl", "arena_iceberg", "arena_autoservice" }, ArenaMapRegistry.All.Select(m => m.Key));
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", "selection", "markers.json")));
        var unmapped = document.RootElement.GetProperty("markers").EnumerateArray()
            .Where(m => m.GetProperty("catalog_key").ValueKind == JsonValueKind.Null).ToArray();
        Assert.Equal("Arena_LabsReplica", Assert.Single(unmapped).GetProperty("source_key").GetString());
        Assert.Equal(295, ArenaMapRegistry.AirPit2.IconX);
        Assert.Equal(830, ArenaMapRegistry.AirPit2.IconY);
    }
}
