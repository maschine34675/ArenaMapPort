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
        // 0.796 px per IconY unit (y grows downwards). The 'ARENA: CHOP SHOP' toggle measured at 870/428 spans [949..1224] x [441..473],
        // i.e. from 10 px left of its dot to 265 px right of it and from 17 px above to 15 px below it.
        // 2026-11-12 (user: another 100 px to the left, 743/428): the toggle rectangles are padding around the ink. Measured on the
        // GPU-2 screenshot of that day (pixels brighter than 150): the ink (dot and text) starts 5 px inside the left edge, ends 28 px
        // inside the right edge, 8 px below the top and 7 px above the bottom, the same for CUSTOMS, INTERCHANGE, FACTORY and RESERVE.
        // The rule is therefore on the ink: the rectangles of CUSTOMS and Chop Shop share a 10 x 7 px corner of empty padding.
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("tools", "maps", "selection", "markers.json")));
        var chopShop = document.RootElement.GetProperty("markers").EnumerateArray()
            .Single(m => m.GetProperty("catalog_key").ValueKind == JsonValueKind.String && m.GetProperty("catalog_key").GetString() == "arena_autoservice");
        var display = chopShop.GetProperty("display");
        Assert.Equal((display.GetProperty("icon_x").GetInt32(), display.GetProperty("icon_y").GetInt32()),
            (ArenaMapRegistry.AutoService.IconX, ArenaMapRegistry.AutoService.IconY));
        double dotX = 594 + (ArenaMapRegistry.AutoService.IconX - 407) * .790;
        double dotY = 424 - (ArenaMapRegistry.AutoService.IconY - 471) * .796;
        var box = (left: dotX - 10 + 5, top: dotY - 17 + 8, right: dotX + 265 - 28, bottom: dotY + 15 - 7);
        // The ink of the toggles the position sits beside: uibridge_snapshot's rectangles on GPU-2 (CUSTOMS [696..859] x [415..448],
        // INTERCHANGE [783..998] x [494..527], ARENA: EQUATOR [884..1132] x [367..400]) less the measured padding.
        var labels = new[]
        {
            ("CUSTOMS", (left: 702.0, top: 423.0, right: 830.0, bottom: 440.0)),
            ("INTERCHANGE", (left: 788.0, top: 502.0, right: 969.0, bottom: 519.0)),
            ("ARENA: EQUATOR", (left: 890.0, top: 375.0, right: 1102.0, bottom: 396.0)),
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
