#nullable disable
using System.Text.Json;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Autoservice's native acoustic graph as the client will see it (PLAN-AUTOSERVICE item 10, docs/progress/autoservice-release.md): seven rooms
/// from Arena's Sound scene (level112). Unlike Iceberg's single IndoorGeneric type the rooms carry detailed EAudioRoomTypeMask bits (2048,
/// 512, 16384, 8192 indoors, OutdoorGeneric 1 outdoors). The table is the full AABB size of each room in metres (twice the bounds extents),
/// computed offline from the exported graph. RoomShotAcousticsModel and RoomReverb are generic by type and size, so the map needs no row
/// there; these tests pin what they make of the seven rooms, so an Arena update that changes a type or a size is seen. The map is not in the
/// released set yet: nothing here touches a per-map client table.
/// </summary>
public sealed class AutoServiceAcousticsTests
{
    const int OutdoorGeneric = 0x1, IndoorMetalThin = 0x200;

    // (room_id, path below SpatialAudioSystem/, type, outdoor flag, W, H, D, class)
    static readonly (int RoomId, string Path, int Type, bool Outdoor, float W, float H, float D, RoomShotClass Class)[] Rooms =
    {
        (1, "IndoorResp2", 2048, false, 11.029f, 5.758f, 39.078f, RoomShotClass.Shop),   // 39.08 m: 0.92 m under the Hall edge
        (2, "AudioRoom_4", IndoorMetalThin, false, 6.629f, 3.341f, 6.234f, RoomShotClass.Metal),
        (3, "AudioRoom_2", 16384, false, 8.756f, 2.813f, 7.206f, RoomShotClass.Shop),
        (4, "AudioRoom_1", 8192, false, 9.087f, 3.096f, 6.150f, RoomShotClass.Shop),
        (5, "MainRoom", 2048, false, 57.460f, 22.744f, 46.672f, RoomShotClass.Hall),
        (6, "OutdoorResp1", OutdoorGeneric, true, 99.371f, 14.734f, 51.754f, RoomShotClass.Outdoor),
        (7, "AudioRoom_3", IndoorMetalThin, false, 22.703f, 2.903f, 2.926f, RoomShotClass.Metal),
    };

    static RoomShotClass Resolve(int type, bool outdoor, float w, float h, float d, out RoomShotPreset preset)
        => RoomShotAcousticsModel.Resolve(type, outdoor, false, w, h, d, null, out preset);

    [Fact]
    public void AutoServiceRoomsClassifyAsDocumented()
    {
        Assert.Equal(Enumerable.Range(1, 7), Rooms.Select(r => r.RoomId).Order());
        var defaults = RoomShotTuning.Default;
        foreach (var r in Rooms)
        {
            var cls = Resolve(r.Type, r.Outdoor, r.W, r.H, r.D, out var preset);
            Assert.True(r.Class == cls, $"room {r.RoomId} {r.Path}: expected {r.Class}, got {cls}");
            if (r.Class == RoomShotClass.Outdoor) { Assert.Null(preset); continue; }
            var t = defaults.For(cls);
            Assert.Equal(new[] { t.Walls, t.Walls, t.Ceiling, t.Floor, t.Walls, t.Walls }, preset.Materials);
            // The shoebox is the real AABB except where the 50 m cap clamps an edge (the main room's 57.5 m).
            Assert.Equal(Math.Min(r.W, RoomShotAcousticsModel.DefaultMaxDimension), preset.Width, 3);
            Assert.Equal(r.H, preset.Height, 3);
            Assert.Equal(Math.Min(r.D, RoomShotAcousticsModel.DefaultMaxDimension), preset.Depth, 3);
        }
        Assert.Equal(3, Rooms.Count(r => r.Class == RoomShotClass.Shop));
        Assert.Equal(2, Rooms.Count(r => r.Class == RoomShotClass.Metal));
        Assert.Equal(1, Rooms.Count(r => r.Class == RoomShotClass.Hall));
        Assert.Equal(1, Rooms.Count(r => r.Class == RoomShotClass.Outdoor));
    }

    [Fact]
    public void TheNearMissHallStaysAShopAndTheMainRoomIsTheHall()
    {
        var hall = Assert.Single(Rooms, r => r.Class == RoomShotClass.Hall);
        Assert.Equal(5, hall.RoomId);
        Assert.True(Math.Max(hall.W, hall.D) >= RoomShotAcousticsModel.HallMinFloorEdge);
        var near = Rooms.Single(r => r.RoomId == 1);
        Assert.True(Math.Max(near.W, near.D) < RoomShotAcousticsModel.HallMinFloorEdge);
        Assert.Equal(RoomShotClass.Shop, near.Class);
        // The two thin-metal rooms are Metal by their type bit whatever their size; the thin one is 2.9 m deep, above the 2 m floor.
        foreach (var m in Rooms.Where(r => r.Class == RoomShotClass.Metal))
        {
            Assert.NotEqual(0, m.Type & RoomShotAcousticsModel.IndoorMetalThin);
            Assert.True(Math.Min(m.W, Math.Min(m.H, m.D)) >= RoomShotAcousticsModel.MinDimension);
        }
    }

    [Fact]
    public void TheOutdoorRoomIsOutdoorByTheFlagAndByTheTypeBit()
    {
        var outdoor = Assert.Single(Rooms, r => r.Outdoor);
        Assert.Equal(6, outdoor.RoomId);
        foreach (var flag in new[] { true, false })
            Assert.Equal(RoomShotClass.Outdoor, Resolve(OutdoorGeneric, flag, outdoor.W, outdoor.H, outdoor.D, out _));
        Assert.Equal(RoomShotClass.Outdoor, Resolve(2048, true, outdoor.W, outdoor.H, outdoor.D, out _));
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
        Assert.Equal(4, categories[2048]);
        Assert.Equal(4, categories[IndoorMetalThin]);
        Assert.Equal(4, categories[16384]);
        Assert.Equal(4, categories[8192]);
        Assert.Equal(67, categories[OutdoorGeneric]);
        Assert.DoesNotContain(0, categories.Keys); // RoomReverb refuses a type-0 room
    }

    // ---- the exported graph itself (ignored work/extract/spatial-audio; no-op where the package is not reachable) -------------------------------

    static JsonDocument ExportedGraph()
    {
        var env = Environment.GetEnvironmentVariable("ARENAMAPPORT_SPATIAL_AUDIO_DIR");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(env)) candidates.Add(Path.Combine(env, "arena_autoservice", "spatial-audio.json"));
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "work", "extract", "spatial-audio", "arena_autoservice", "spatial-audio.json"));
        var path = candidates.FirstOrDefault(File.Exists);
        return path == null ? null : JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void TheRoomTableIsTheExportedGraphWhenThePackageIsPresent()
    {
        using var graph = ExportedGraph();
        if (graph == null) return;   // package not reachable from this checkout: the offline check of 2026-11-11 stands
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
            // 16 portal nodes are flat (z scale 0, a zero-depth plane the validator allows); none is negative.
            Assert.True(x >= 0 && y >= 0 && z >= 0, "a negative scale needs a reviewed rule for this map");
            Assert.Null(AcousticVolumeRules.ScaleProblem(x, y, z, mirrored.Contains(node.GetProperty("id").GetInt64())));
        }
        Assert.Equal(34, root.GetProperty("nodes").GetArrayLength());
    }
}
