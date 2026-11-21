#nullable disable
using System.Text.Json;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Yard's native acoustic graph as the client sees it (PLAN-YARD item 12, docs/progress/yard-release.md; staged with the promotion of item 15): nineteen rooms
/// from Arena's Sound scene (level163), five outdoor (EAudioRoomTypeMask 64 for three of them and 1 for two) and fourteen indoor with the detailed type bits 1024,
/// 2048 and 4096 (none of them metal). The table is the full AABB size of each room in metres (twice the bounds extents), computed offline from the exported
/// graph. RoomShotAcousticsModel and RoomReverb are generic by type and size, so the map needs no row there; these tests pin what they make of the nineteen
/// rooms, so an Arena update that changes a type or a size is seen, and that the staged package (graph, original bake, both room tones) is the one that was
/// exported. No file digest of Arena content is kept here.
/// </summary>
public sealed class YardAcousticsTests
{
    const int OutdoorGeneric = 0x1, OutdoorCommon = 0x40;

    // (room_id, path below SpatialAudioSystem/, type, outdoor flag, W, H, D, class)
    static readonly (int RoomId, string Path, int Type, bool Outdoor, float W, float H, float D, RoomShotClass Class)[] Rooms =
    {
        (1, "Outdoor/Rooms/Outdoor_Resp_1", OutdoorCommon, true, 69.113f, 32.521f, 46.628f, RoomShotClass.Outdoor),
        (2, "Indoor/tonnel_1 (1)", 4096, false, 9.255f, 3.079f, 4.784f, RoomShotClass.Shop),
        (3, "Indoor/Resp_1/Room_3", 2048, false, 6.370f, 4.064f, 10.436f, RoomShotClass.Shop),
        (4, "Indoor/Side/Room_2", 4096, false, 10.205f, 6.247f, 8.933f, RoomShotClass.Shop),
        (5, "Indoor/Garage_2", 1024, false, 3.628f, 3.079f, 5.690f, RoomShotClass.Shop),
        (6, "Outdoor/Rooms/Outdoor_Mid", OutdoorCommon, true, 69.113f, 32.521f, 48.884f, RoomShotClass.Outdoor),
        (7, "Indoor/tonnel_1 (3)", 4096, false, 4.784f, 3.265f, 11.733f, RoomShotClass.Shop),
        (8, "Indoor/Kishka_to_Mid", 2048, false, 8.636f, 3.486f, 7.973f, RoomShotClass.Shop),
        (9, "Indoor/Resp_1/Room_1", 2048, false, 11.322f, 3.776f, 5.374f, RoomShotClass.Shop),
        (10, "Indoor/Side/Room_1", 4096, false, 9.621f, 6.247f, 9.047f, RoomShotClass.Shop),
        (11, "Indoor/tonnel_1", 4096, false, 10.031f, 3.079f, 10.031f, RoomShotClass.Shop),
        (12, "Indoor/Garage_1", 1024, false, 4.050f, 2.821f, 6.395f, RoomShotClass.Shop),
        (13, "Outdoor/Rooms/Outdoor_Kishka", OutdoorGeneric, true, 56.050f, 32.521f, 85.387f, RoomShotClass.Outdoor),
        (14, "Indoor/Side/Room_3", 4096, false, 3.126f, 6.247f, 2.731f, RoomShotClass.Shop),
        (15, "Outdoor/Rooms/Outdoor_Resp_2", OutdoorGeneric, true, 17.624f, 23.891f, 24.920f, RoomShotClass.Outdoor),
        (16, "Indoor/Resp_1/Room_2", 2048, false, 7.349f, 3.505f, 8.617f, RoomShotClass.Shop),
        (17, "Outdoor/Rooms/Outdoor_Resp_2_Out", OutdoorCommon, true, 69.113f, 33.007f, 32.158f, RoomShotClass.Outdoor),
        (18, "Indoor/tonnel_1 (4)", 4096, false, 4.784f, 4.426f, 14.460f, RoomShotClass.Shop),
        (19, "Indoor/tonnel_1 (2)", 4096, false, 9.781f, 3.079f, 9.781f, RoomShotClass.Shop),
    };

    static RoomShotClass Resolve(int type, bool outdoor, float w, float h, float d, out RoomShotPreset preset)
        => RoomShotAcousticsModel.Resolve(type, outdoor, false, w, h, d, null, out preset);

    [Fact]
    public void YardRoomsClassifyAsDocumented()
    {
        Assert.Equal(Enumerable.Range(1, 19), Rooms.Select(r => r.RoomId).Order());
        var defaults = RoomShotTuning.Default;
        foreach (var r in Rooms)
        {
            var cls = Resolve(r.Type, r.Outdoor, r.W, r.H, r.D, out var preset);
            Assert.True(r.Class == cls, $"room {r.RoomId} {r.Path}: expected {r.Class}, got {cls}");
            if (r.Class == RoomShotClass.Outdoor) { Assert.Null(preset); continue; }
            var t = defaults.For(cls);
            Assert.Equal(new[] { t.Walls, t.Walls, t.Ceiling, t.Floor, t.Walls, t.Walls }, preset.Materials);
            // Every indoor room is far below the 50 m cap: the shoebox is the real AABB.
            Assert.Equal(r.W, preset.Width, 3);
            Assert.Equal(r.H, preset.Height, 3);
            Assert.Equal(r.D, preset.Depth, 3);
        }
        Assert.Equal(14, Rooms.Count(r => r.Class == RoomShotClass.Shop));
        Assert.Equal(5, Rooms.Count(r => r.Class == RoomShotClass.Outdoor));
        Assert.Equal(0, Rooms.Count(r => r.Class == RoomShotClass.Hall));
        Assert.Equal(0, Rooms.Count(r => r.Class == RoomShotClass.Metal));
    }

    [Fact]
    public void NoIndoorRoomIsAHallOrMetalAndTheLargestFloorEdgeIsWellBelowTheHallEdge()
    {
        foreach (var r in Rooms.Where(r => !r.Outdoor))
        {
            Assert.Equal(0, r.Type & (RoomShotAcousticsModel.IndoorMetalThin | RoomShotAcousticsModel.IndoorMetalThick));
            Assert.True(Math.Max(r.W, r.D) < RoomShotAcousticsModel.HallMinFloorEdge, $"room {r.RoomId}");
            Assert.True(Math.Min(r.W, Math.Min(r.H, r.D)) >= RoomShotAcousticsModel.MinDimension, $"room {r.RoomId}");
        }
        Assert.InRange(Rooms.Where(r => !r.Outdoor).Max(r => Math.Max(r.W, r.D)), 14f, 15f);   // the longest tunnel (room 18), 14.46 m
    }

    [Fact]
    public void TheFiveOutdoorRoomsAreOutdoorByTheFlagAndByTheTypeBit()
    {
        var outdoor = Rooms.Where(r => r.Outdoor).ToArray();
        Assert.Equal(new[] { 1, 6, 13, 15, 17 }, outdoor.Select(r => r.RoomId).Order());
        foreach (var r in outdoor)
        {
            Assert.NotEqual(0, r.Type & RoomShotAcousticsModel.OutdoorBits);
            foreach (var flag in new[] { true, false })
                Assert.Equal(RoomShotClass.Outdoor, Resolve(r.Type, flag, r.W, r.H, r.D, out _));
        }
        // an indoor type with the outdoor flag set is outdoor too
        Assert.Equal(RoomShotClass.Outdoor, Resolve(2048, true, 10f, 3f, 10f, out _));
    }

    // RoomReverb's TableHandler gives every room a mixer category by the isolated and outdoor FLAGS only (isolated 8, outdoor 67, else 4) and
    // refuses the graph ("conflicting mixer room category") when one room type would get two.
    static int MixerCategory(bool outdoor, bool isolated) => isolated ? 8 : outdoor ? 67 : 4;

    [Fact]
    public void EveryRoomTypeMapsToOneMixerCategoryAndTheShotRuleAgreesWithIt()
    {
        var categories = new Dictionary<int, int>();
        foreach (var r in Rooms)
        {
            int category = MixerCategory(r.Outdoor, false);
            Assert.True(!categories.TryGetValue(r.Type, out int previous) || previous == category,
                $"room {r.RoomId} {r.Path}: type {r.Type} would get categories {previous} and {category}");
            categories[r.Type] = category;
            Assert.Equal(category == 67, RoomShotAcousticsModel.IsOutdoor(r.Type, r.Outdoor, false));
        }
        Assert.Equal(5, categories.Count);
        Assert.Equal(4, categories[1024]);
        Assert.Equal(4, categories[2048]);
        Assert.Equal(4, categories[4096]);
        Assert.Equal(67, categories[OutdoorGeneric]);
        Assert.Equal(67, categories[OutdoorCommon]);
        Assert.DoesNotContain(0, categories.Keys); // RoomReverb refuses a type-0 room
    }

    // ---- the exported graph itself (ignored work/extract/spatial-audio; no-op where the package is not reachable) -------------------------------

    static string PackageFolder()
    {
        var env = Environment.GetEnvironmentVariable("ARENAMAPPORT_SPATIAL_AUDIO_DIR");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(env)) candidates.Add(Path.Combine(env, "arena_yard"));
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "work", "extract", "spatial-audio", "arena_yard"));
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "spatial-audio.json")));
    }

    static JsonDocument ExportedGraph()
    {
        var folder = PackageFolder();
        return folder == null ? null : JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "spatial-audio.json")));
    }

    [Fact]
    public void TheRoomTableIsTheExportedGraphWhenThePackageIsPresent()
    {
        using var graph = ExportedGraph();
        if (graph == null) return;   // package not reachable from this checkout: the offline check of 2026-11-21 stands
        Assert.Equal("arena_yard", graph.RootElement.GetProperty("map_key").GetString());
        Assert.Equal("level163", graph.RootElement.GetProperty("source_scene").GetString());
        var rooms = graph.RootElement.GetProperty("rooms").EnumerateArray().Where(r => r.GetProperty("active_in_hierarchy").GetBoolean()).ToArray();
        Assert.Equal(Rooms.Length, rooms.Length);
        foreach (var row in Rooms)
        {
            var room = rooms.Single(r => r.GetProperty("room_id").GetInt32() == row.RoomId);
            Assert.Equal(row.Path, room.GetProperty("path").GetString().Replace("SpatialAudioSystem/", ""));
            Assert.Equal(row.Type, room.GetProperty("type").GetInt32());
            Assert.Equal(row.Outdoor, room.GetProperty("outdoor").GetBoolean());
            Assert.False(room.GetProperty("isolated").GetBoolean());
            var extents = room.GetProperty("bounds").GetProperty("extents").EnumerateArray().Select(e => e.GetSingle() * 2f).ToArray();
            Assert.Equal(row.W, extents[0], 3);
            Assert.Equal(row.H, extents[1], 3);
            Assert.Equal(row.D, extents[2], 3);
        }
        Assert.Equal(27, graph.RootElement.GetProperty("portals").GetArrayLength());
    }

    [Fact]
    public void NoNodeOfTheExportedGraphIsMirroredAndTheClientRuleAcceptsEveryScale()
    {
        using var graph = ExportedGraph();
        if (graph == null) return;
        var root = graph.RootElement;
        IEnumerable<long> Nodes(string key) => root.GetProperty(key).EnumerateArray().Select(i => i.GetProperty("node_id").GetInt64());
        var parents = root.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("parent_id").ValueKind == JsonValueKind.Number)
            .Select(n => n.GetProperty("parent_id").GetInt64());
        var mirrored = AcousticVolumeRules.MirroredVolumeNodes(Nodes("areas"), Nodes("rooms").Concat(Nodes("portals")).Concat(Nodes("groups")).Concat(Nodes("systems")), parents);
        foreach (var node in root.GetProperty("nodes").EnumerateArray())
        {
            var s = node.GetProperty("scale");
            float x = s.GetProperty("x").GetSingle(), y = s.GetProperty("y").GetSingle(), z = s.GetProperty("z").GetSingle();
            // no node is mirrored (extract_spatial_audio.REVIEWED_MIRRORED_VOLUMES lists none for Yard): a negative scale would need a reviewed rule for this map
            Assert.True(x >= 0 && y >= 0 && z >= 0, "a negative scale needs a reviewed rule for this map");
            Assert.Null(AcousticVolumeRules.ScaleProblem(x, y, z, mirrored.Contains(node.GetProperty("id").GetInt64())));
        }
        Assert.Equal(76, root.GetProperty("nodes").GetArrayLength());
    }

    // ---- the staged package (dist/BepInEx/plugins/maschine-ArenaMapPort/acoustics/arena_yard, written by every Release client build) ----------------------

    static string StagedFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "dist", "BepInEx", "plugins", "maschine-ArenaMapPort", "acoustics", "arena_yard");
            if (Directory.Exists(path)) return path;
        }
        return null;
    }

    [Fact]
    public void TheStagedPackageHoldsTheGraphTheOriginalBakeAndBothRoomTonesIdenticalToTheExport()
    {
        var staged = StagedFolder();
        var export = PackageFolder();
        if (staged == null || export == null) return;   // no dist/ or no extracted package on this machine (validate_distribution.py checks the same on the lead's side)
        var names = Directory.GetFiles(staged).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "sharedassets111.assets_40.wav", "sharedassets163.assets_17.wav", "source.audiobakedata", "spatial-audio.json" }, names);
        foreach (var name in names)
            Assert.True(File.ReadAllBytes(Path.Combine(export, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(staged, name))), name);
        // the graph names exactly the two WAVs and the one bake file that sit beside it
        using var graph = JsonDocument.Parse(File.ReadAllText(Path.Combine(staged, "spatial-audio.json")));
        var tones = graph.RootElement.GetProperty("clips").EnumerateArray().Select(c => c.GetProperty("decoded").GetProperty("file").GetString()).Order().ToArray();
        Assert.Equal(new[] { "sharedassets111.assets_40.wav", "sharedassets163.assets_17.wav" }, tones);
        Assert.Equal("source.audiobakedata", graph.RootElement.GetProperty("bake").GetProperty("file").GetString());
        Assert.Equal(new FileInfo(Path.Combine(staged, "source.audiobakedata")).Length, graph.RootElement.GetProperty("bake").GetProperty("bytes").GetInt64());
        foreach (var tone in graph.RootElement.GetProperty("clips").EnumerateArray())
            Assert.Equal(new FileInfo(Path.Combine(staged, tone.GetProperty("decoded").GetProperty("file").GetString())).Length, tone.GetProperty("decoded").GetProperty("bytes").GetInt64());
    }

    [Fact]
    public void TheClientCsprojStagesYardsAcousticsLikeTheOtherMaps()
    {
        string csproj = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && csproj == null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "ArenaMapPort.Client", "ArenaMapPort.Client.csproj");
            if (File.Exists(path)) csproj = File.ReadAllText(path);
        }
        Assert.NotNull(csproj);
        Assert.Contains("$(ArenaMapPortAcousticsDir)\\arena_yard\\**\\spatial-audio.json;$(ArenaMapPortAcousticsDir)\\arena_yard\\**\\source.audiobakedata;$(ArenaMapPortAcousticsDir)\\arena_yard\\**\\*.wav\"><MapKey>arena_yard</MapKey>", csproj);
        Assert.Contains("$(ArenaMapPortAcousticsDir)\\arena_yard\\spatial-audio.json", csproj);   // the Error guard names it
    }
}
