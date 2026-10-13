using System.Text.Json;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// docs/PLAN-AIRPIT.md item 6 (docs/progress/airpit-release.md): tools/maps/airpit.bots.json and
/// airpit.exits.json are the single source of AirPit's spawn points, waves, PMC squads and exits.
/// The compiled descriptor (what the client authors) and the shipped db/arena_airpit/base.json
/// (what make_base_json.py generated) must say exactly what the sidecars say.
/// </summary>
public sealed class AirPitMapTests
{
    private static readonly JsonSerializerOptions Options = CreateServerOptions();

    private static JsonSerializerOptions CreateServerOptions()
    {
        // Same converter set the live server's JsonUtil registers, so base.json parses as at server load.
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters())
            options.Converters.Add(converter);
        return options;
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

    private static string BaseJsonPath => Path.Combine(AppContext.BaseDirectory, "db", "arena_airpit", "base.json");

    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(v => v.GetSingle()).ToArray();

    [Fact]
    public void ExitTransformsMatchTheSidecarAndServerEligibility()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.exits.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.AirPit;
        var authored = sidecar.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        var shipped = server.RootElement.GetProperty("exits").EnumerateArray().ToArray();
        Assert.Equal(2, map.Exits.Length);
        Assert.Equal(map.Exits.Length, authored.Length);
        Assert.Equal(map.Exits.Length, shipped.Length);
        foreach (var expected in map.Exits)
        {
            var point = authored.Single(e => e.GetProperty("name").GetString() == expected.Name);
            Assert.Equal(new[] { expected.X, expected.Y, expected.Z }, Floats(point.GetProperty("position")));
            Assert.Equal(new[] { expected.SizeX, expected.SizeY, expected.SizeZ }, Floats(point.GetProperty("box_size")));
            Assert.Equal(expected.RotationY, point.GetProperty("rotation").GetSingle());
            var exit = shipped.Single(e => e.GetProperty("Name").GetString() == expected.Name);
            Assert.Equal(map.InfiltrationName, exit.GetProperty("EntryPoints").GetString());
            Assert.Equal("Individual", exit.GetProperty("ExfiltrationType").GetString());
            Assert.Equal("None", exit.GetProperty("PassageRequirement").GetString());
            Assert.Equal(100, exit.GetProperty("Chance").GetDouble());
            Assert.Equal(point.GetProperty("exfiltration_time").GetDouble(), exit.GetProperty("ExfiltrationTime").GetDouble());
        }
        Assert.Empty(ArenaMapRegistration.ExitNameMismatches(
            shipped.Select(e => e.GetProperty("Name").GetString()), map));
    }

    [Fact]
    public void SpawnPointsWavesAndSquadsMatchTheBotsSidecar()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.bots.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.AirPit;
        var bots = sidecar.RootElement;
        var zone = bots.GetProperty("zone").GetString();
        var shipped = server.RootElement.GetProperty("SpawnPointParams").EnumerateArray().ToArray();
        var players = bots.GetProperty("player_spawns").EnumerateArray().ToArray();
        var botSpawns = bots.GetProperty("bot_spawns").EnumerateArray().ToArray();

        Assert.Equal(4, players.Length);
        Assert.Equal(14, botSpawns.Length);
        Assert.Equal(players.Length + botSpawns.Length, shipped.Length);
        Assert.Equal(shipped.Length, shipped.Select(p => p.GetProperty("Id").GetString()).Distinct().Count());

        foreach (var expected in players)
        {
            var point = shipped.Single(p => p.GetProperty("Id").GetString() == expected.GetProperty("id").GetString());
            Assert.Equal(Floats(expected.GetProperty("position")), PositionOf(point));
            Assert.Equal(expected.GetProperty("rotation").GetDouble(), point.GetProperty("Rotation").GetDouble());
            Assert.Equal(expected.GetProperty("sides").EnumerateArray().Select(s => s.GetString()),
                         point.GetProperty("Sides").EnumerateArray().Select(s => s.GetString()));
            Assert.Equal(new[] { "Player" }, point.GetProperty("Categories").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal(map.InfiltrationName, point.GetProperty("Infiltration").GetString());
        }
        foreach (var expected in botSpawns)
        {
            var point = shipped.Single(p => p.GetProperty("Id").GetString() == expected.GetProperty("id").GetString());
            Assert.Equal(Floats(expected.GetProperty("position")), PositionOf(point));
            Assert.Equal(zone, point.GetProperty("BotZoneName").GetString());
            Assert.Equal(expected.GetProperty("categories").EnumerateArray().Select(c => c.GetString()),
                         point.GetProperty("Categories").EnumerateArray().Select(c => c.GetString()));
            Assert.Contains("BotPmc", point.GetProperty("Categories").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("", point.GetProperty("Infiltration").GetString());
        }

        var root = server.RootElement;
        Assert.Equal(bots.GetProperty("max_bots").GetInt32(), root.GetProperty("BotMax").GetInt32());
        Assert.Equal(zone, root.GetProperty("OpenZones").GetString());
        Assert.True(root.GetProperty("DisabledForScav").GetBoolean());
        Assert.Empty(root.GetProperty("Loot").EnumerateArray());
        Assert.Equal(bots.GetProperty("waves").GetArrayLength(), root.GetProperty("waves").GetArrayLength());
        Assert.All(root.GetProperty("waves").EnumerateArray(), w => Assert.Equal(zone, w.GetProperty("SpawnPoints").GetString()));

        var squads = root.GetProperty("BossLocationSpawn").EnumerateArray().ToArray();
        Assert.Equal(bots.GetProperty("boss_waves").GetArrayLength(), squads.Length);
        Assert.All(squads, b => Assert.Contains(b.GetProperty("BossName").GetString(), new[] { "pmcUSEC", "pmcBEAR" }));
        Assert.All(squads, b => Assert.Equal(zone, b.GetProperty("BossZone").GetString()));
        Assert.Equal(2, squads.Count(b => b.GetProperty("BossName").GetString() == "pmcUSEC"));
        Assert.Equal(2, squads.Count(b => b.GetProperty("BossName").GetString() == "pmcBEAR"));
    }

    // PLAN-AIRPIT item 8: tools/maps/airpit.json is what BakeNavMesh and the Python model read, the compiled descriptor is
    // what BoothDoorRepairs opens at raid start. One list each, and Equator's and Bay5's sidecars stay free of the new keys
    // (their NavMesh bundles are not rebuilt, so their bake inputs must not change).
    [Fact]
    public void OpenedBarriersAndBakeInputsMatchTheSidecar()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.json")));
        var root = sidecar.RootElement;
        var map = ArenaMapRegistry.AirPit;

        Assert.Equal(root.GetProperty("opened_barriers").EnumerateArray().Select(e => e.GetString()), map.OpenedBarriers);
        Assert.Equal(root.GetProperty("navmesh_bake_exclusions").EnumerateArray().Select(e => e.GetString()), map.NavMeshBakeExclusions);
        Assert.Equal(Floats(root.GetProperty("bake_bounds")), map.BakeBounds);
        if (root.TryGetProperty("navmesh_bake_layers", out var layers))
            Assert.Equal(layers.EnumerateArray().Select(e => e.GetInt32()), map.NavMeshBakeLayers);
        else
            Assert.Empty(map.NavMeshBakeLayers);

        foreach (var other in new[] { "equator.json", "bay5.json" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(other)));
            foreach (var key in new[] { "navmesh_bake_exclusions", "bake_bounds", "navmesh_bake_layers" })
                Assert.False(doc.RootElement.TryGetProperty(key, out _), $"{other} must not carry '{key}'");
        }
    }

    // 2026-10-12 (host raid 4): the invisible walls along Arena's TeamFight outline, one sidecar entry per segment
    // ({name, from [x, z], to [x, z], y [min, max]}) mirrored by the compiled descriptor AuthoredBorderRepairs reads.
    [Fact]
    public void AuthoredBordersMatchTheSidecar()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.json")));
        var entries = sidecar.RootElement.GetProperty("authored_borders").EnumerateArray().ToArray();
        var map = ArenaMapRegistry.AirPit;
        Assert.Equal(entries.Length, map.AuthoredBorders.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            var b = map.AuthoredBorders[i];
            Assert.Equal(e.GetProperty("name").GetString(), b.Name);
            Assert.Equal(Floats(e.GetProperty("from")), new[] { b.X0, b.Z0 });
            Assert.Equal(Floats(e.GetProperty("to")), new[] { b.X1, b.Z1 });
            Assert.Equal(Floats(e.GetProperty("y")), new[] { b.MinY, b.MaxY });
        }
        foreach (var other in new[] { "equator.json", "bay5.json" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(other)));
            Assert.False(doc.RootElement.TryGetProperty("authored_borders", out _), $"{other} must not carry 'authored_borders'");
        }
    }

    // 2026-10-13 (user decision): the empty cells of the passport-hall ring-lamp lattice get dark copies of the authored
    // off-lamp (LampFillRepairs). tools/maps/airpit.json's "lamp_fill" {template, y, cells [[x, z], ...]} is the Python
    // side's copy of the compiled descriptor; no other map carries it.
    [Fact]
    public void LampFillMatchesTheSidecar()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.json")));
        var entry = sidecar.RootElement.GetProperty("lamp_fill");
        var fill = ArenaMapRegistry.AirPit.LampFill;
        Assert.NotNull(fill);
        Assert.Equal("SBG_Airpit_Light/OO/Airport_lamp_02/Airport_lamp_02_disabled", fill!.TemplatePath);
        Assert.Equal(entry.GetProperty("template").GetString(), fill.TemplatePath);
        Assert.Equal(entry.GetProperty("y").GetSingle(), fill.Y);
        Assert.Equal(6.83f, fill.Y);
        var cells = entry.GetProperty("cells").EnumerateArray().ToArray();
        Assert.Equal(0, fill.Cells.Length % 2);
        Assert.Equal(81, cells.Length);
        Assert.Equal(cells.Length, fill.Count);
        for (var i = 0; i < cells.Length; i++)
            Assert.Equal(Floats(cells[i]), new[] { fill.CellX(i), fill.CellZ(i) });

        var mapsDir = Path.GetDirectoryName(Sidecar("airpit.json"))!;
        foreach (var path in Directory.EnumerateFiles(mapsDir, "*.json").Where(p => !Path.GetFileNameWithoutExtension(p).Contains('.')))
        {
            if (Path.GetFileName(path) == "airpit.json") continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(doc.RootElement.TryGetProperty("lamp_fill", out _), $"{Path.GetFileName(path)} must not carry 'lamp_fill'");
        }
    }

    [Fact]
    public void ShippedBaseJsonParsesAsTheServerLocationModel()
    {
        var location = JsonSerializer.Deserialize<LocationBase>(File.ReadAllText(BaseJsonPath), Options);
        Assert.NotNull(location);
        var map = ArenaMapRegistry.AirPit;
        Assert.Equal(map.Key, location!.Id);
        Assert.Equal(map.IdField, location.IdField.ToString());
        Assert.Equal(18, location.SpawnPointParams!.Count());
        Assert.Equal(4, location.SpawnPointParams!.Count(p => p.Categories!.Contains("Player")));
        Assert.Equal(14, location.SpawnPointParams!.Count(p => p.Categories!.Contains("Bot")));
        Assert.Equal(2, location.Exits.Count());
        Assert.Equal(5, location.Waves!.Count);
        Assert.Equal(4, location.BossLocationSpawn!.Count);
        Assert.Equal(8, location.BotMax);
        Assert.True(location.DisabledForScav);
        Assert.Empty(location.Banners!);
    }

    // PLAN-AIRPIT items 9 + 23: AirPit is a released map, so its identity is the shipped one. The sidecar, the
    // compiled descriptor and the generated base.json must agree on name, description and menu slot, and no
    // released map may still carry the spike wording.
    [Fact]
    public void ReleasedIdentityMatchesTheSidecarAndBaseJsonAndKeepsItsOwnMenuSlot()
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.json")));
        using var server = JsonDocument.Parse(File.ReadAllText(BaseJsonPath));
        var map = ArenaMapRegistry.AirPit;

        Assert.Equal(new[] { "arena_equator", "arena_bay5", "arena_airpit" }, ArenaMapRegistry.All.Select(m => m.Key));
        Assert.Equal("Arena: Air Pit", map.LocaleName);
        Assert.Equal(sidecar.RootElement.GetProperty("locale_name").GetString(), map.LocaleName);
        Assert.Equal(sidecar.RootElement.GetProperty("locale_description").GetString(), map.LocaleDescription);
        Assert.Equal(sidecar.RootElement.GetProperty("icon_x").GetInt32(), map.IconX);
        Assert.Equal(sidecar.RootElement.GetProperty("icon_y").GetInt32(), map.IconY);
        Assert.Equal(map.LocaleName, server.RootElement.GetProperty("Name").GetString());
        Assert.Equal(map.IconX, server.RootElement.GetProperty("IconX").GetInt32());
        Assert.Equal(map.IconY, server.RootElement.GetProperty("IconY").GetInt32());

        foreach (var released in ArenaMapRegistry.All)
        {
            Assert.DoesNotContain("spike", released.LocaleName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Internal test build", released.LocaleDescription, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(released.LocaleDescription));
        }
        // Released pins remain selectable and inside the map. Their geographic positions now come
        // from Arena screenshots, not the old arbitrary box between Equator and Bay5.
        foreach (var a in ArenaMapRegistry.All)
            foreach (var b in ArenaMapRegistry.All.Where(b => b.Key != a.Key))
                Assert.True(Math.Sqrt(Math.Pow(a.IconX - b.IconX, 2) + Math.Pow(a.IconY - b.IconY, 2)) >= 120,
                    $"{a.Key} and {b.Key} menu icons are closer than 120 units");
        Assert.InRange(map.IconX, 0, 1000);
        Assert.InRange(map.IconY, 0, 1000);
        // The banner row ships with the map; base.json itself keeps Banners empty (ArenaMapBanners adds it at load).
        Assert.Equal("arena_airpit_banner", ArenaMapBanners.For(map)!.Id);
    }

    private static float[] PositionOf(JsonElement spawnPoint)
    {
        var p = spawnPoint.GetProperty("Position");
        return new[] { p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(), p.GetProperty("z").GetSingle() };
    }
}
