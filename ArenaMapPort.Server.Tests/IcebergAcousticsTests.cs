#nullable disable
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Iceberg's native acoustic graph as the client sees it (PLAN-ICEBERG item 11, docs/progress/iceberg-release.md "Spatial acoustics"):
/// 46 rooms from Arena's Sanatorium sound scene (level259), 45 IndoorGeneric and one OutdoorCommon. The table is the full AABB size of
/// each room in metres, the union of its trigger areas' <c>Collider.bounds</c> (twice the bounds extents), computed offline from the
/// exported graph and equal, room by room, to the bounds Arena itself cached in the scene; that includes corridor_2_1 and coffe_hall,
/// whose areas are mirrored (negative local scale), so the sign of a scale never reached the sizes. The classes are what
/// RoomShotAcousticsModel makes of them; the mixer categories are what RoomReverb makes of the type and flags. Both are generic by type
/// and size, so Iceberg needs no per-map row there (docs/progress/shot-acoustics.md, room-reverb.md); these tests pin that claim.
/// </summary>
public sealed class IcebergAcousticsTests
{
    // EAudioRoomTypeMask values as they appear in the graph.
    const int IndoorGeneric = 4, OutdoorCommon = 64;

    // (room_id, path below SpatialAudioSystem/, type, outdoor flag, W, H, D, class)
    static readonly (int RoomId, string Path, int Type, bool Outdoor, float W, float H, float D, RoomShotClass Class)[] IcebergRooms =
    {
        (1, "MAIN_ENTRY_ZONE/Coridor_1", IndoorGeneric, false, 7.350f, 3.877f, 6.318f, RoomShotClass.Shop),
        (2, "MAIN_ENTRY_ZONE/pre_pool", IndoorGeneric, false, 6.954f, 3.431f, 6.772f, RoomShotClass.Shop),
        (3, "MAIN_ENTRY_ZONE/Arch_spawn (1)", IndoorGeneric, false, 4.333f, 4.352f, 4.865f, RoomShotClass.Shop),
        (4, "MAIN_ENTRY_ZONE/stage", IndoorGeneric, false, 6.654f, 6.207f, 19.441f, RoomShotClass.Shop),
        (5, "MAIN_ENTRY_ZONE/corridor_2_1", IndoorGeneric, false, 10.645f, 5.239f, 19.901f, RoomShotClass.Shop),  // mirrored volume(s)
        (6, "MAIN_ENTRY_ZONE/lounge", IndoorGeneric, false, 9.279f, 5.372f, 19.839f, RoomShotClass.Shop),
        (7, "MAIN_ENTRY_ZONE/Corridor_back (2)", IndoorGeneric, false, 13.785f, 4.337f, 3.595f, RoomShotClass.Shop),
        (8, "MAIN_ENTRY_ZONE/lounge (1)", IndoorGeneric, false, 6.553f, 5.372f, 16.921f, RoomShotClass.Shop),
        (9, "MAIN_ENTRY_ZONE/Coridor_1 (1)", IndoorGeneric, false, 13.763f, 3.958f, 7.345f, RoomShotClass.Shop),
        (10, "MAIN_ENTRY_ZONE/pre-theatre_up", IndoorGeneric, false, 17.849f, 4.586f, 30.137f, RoomShotClass.Shop),
        (11, "MAIN_ENTRY_ZONE/boiler", IndoorGeneric, false, 3.277f, 3.436f, 3.845f, RoomShotClass.Shop),
        (12, "MAIN_ENTRY_ZONE/Arch_spawn", IndoorGeneric, false, 4.333f, 4.352f, 5.112f, RoomShotClass.Shop),
        (13, "MAIN_ENTRY_ZONE/pre-theatre", IndoorGeneric, false, 17.849f, 4.951f, 30.137f, RoomShotClass.Shop),
        (14, "MAIN_ENTRY_ZONE/p_split", IndoorGeneric, false, 15.234f, 3.328f, 6.310f, RoomShotClass.Shop),
        (15, "MAIN_ENTRY_ZONE/office", IndoorGeneric, false, 6.872f, 5.372f, 10.037f, RoomShotClass.Shop),
        (16, "MAIN_ENTRY_ZONE/Corridor_back (3)", IndoorGeneric, false, 2.760f, 3.328f, 3.522f, RoomShotClass.Shop),
        (17, "MAIN_ENTRY_ZONE/under_pool", IndoorGeneric, false, 12.770f, 3.149f, 9.110f, RoomShotClass.Shop),
        (18, "MAIN_ENTRY_ZONE/showers", IndoorGeneric, false, 6.771f, 3.431f, 5.166f, RoomShotClass.Shop),
        (19, "MAIN_ENTRY_ZONE/elevators (1)", IndoorGeneric, false, 2.917f, 3.436f, 2.388f, RoomShotClass.Shop),
        (20, "MAIN_ENTRY_ZONE/coridor_from_pool_2", IndoorGeneric, false, 14.161f, 5.096f, 6.691f, RoomShotClass.Shop),
        (21, "MAIN_ENTRY_ZONE/right", IndoorGeneric, false, 6.055f, 4.107f, 5.594f, RoomShotClass.Shop),
        (22, "MAIN_ENTRY_ZONE/coffe_2", IndoorGeneric, false, 42.805f, 4.580f, 20.439f, RoomShotClass.Hall),
        (23, "MAIN_ENTRY_ZONE/Corridor_back", IndoorGeneric, false, 3.122f, 3.328f, 19.102f, RoomShotClass.Shop),
        (24, "MAIN_ENTRY_ZONE/boiler (2)", IndoorGeneric, false, 3.196f, 3.436f, 3.993f, RoomShotClass.Shop),
        (25, "MAIN_ENTRY_ZONE/elevators (2)", IndoorGeneric, false, 4.993f, 3.436f, 2.410f, RoomShotClass.Shop),
        (26, "MAIN_ENTRY_ZONE/Column_hall", IndoorGeneric, false, 15.105f, 5.441f, 14.795f, RoomShotClass.Shop),
        (27, "MAIN_ENTRY_ZONE/elevators", IndoorGeneric, false, 13.449f, 3.738f, 11.055f, RoomShotClass.Shop),
        (28, "MAIN_ENTRY_ZONE/stair_room", IndoorGeneric, false, 7.932f, 4.583f, 6.682f, RoomShotClass.Shop),
        (29, "MAIN_ENTRY_ZONE/theatre", IndoorGeneric, false, 18.883f, 8.036f, 30.737f, RoomShotClass.Shop),
        (30, "MAIN_ENTRY_ZONE/boiler (3)", IndoorGeneric, false, 2.861f, 2.604f, 5.588f, RoomShotClass.Shop),
        (31, "outdoor", OutdoorCommon, true, 338.633f, 187.269f, 305.472f, RoomShotClass.Outdoor),
        (32, "MAIN_ENTRY_ZONE/Reseption", IndoorGeneric, false, 12.472f, 6.539f, 29.487f, RoomShotClass.Shop),
        (33, "MAIN_ENTRY_ZONE/showers (2)", IndoorGeneric, false, 9.201f, 3.581f, 7.496f, RoomShotClass.Shop),
        (34, "MAIN_ENTRY_ZONE/pool_low_room", IndoorGeneric, false, 5.407f, 4.090f, 13.895f, RoomShotClass.Shop),
        (35, "MAIN_ENTRY_ZONE/coffe_hall", IndoorGeneric, false, 33.386f, 4.323f, 23.383f, RoomShotClass.Shop),  // mirrored volume(s)
        (36, "MAIN_ENTRY_ZONE/left", IndoorGeneric, false, 6.352f, 4.107f, 5.536f, RoomShotClass.Shop),
        (37, "MAIN_ENTRY_ZONE/Corridor_back (1)", IndoorGeneric, false, 4.977f, 3.328f, 12.901f, RoomShotClass.Shop),
        (38, "MAIN_ENTRY_ZONE/Pool", IndoorGeneric, false, 36.682f, 9.366f, 38.696f, RoomShotClass.Shop),
        (39, "MAIN_ENTRY_ZONE/to_lounge", IndoorGeneric, false, 3.623f, 5.372f, 3.753f, RoomShotClass.Shop),
        (40, "MAIN_ENTRY_ZONE/big_glass_room", IndoorGeneric, false, 23.771f, 5.619f, 31.169f, RoomShotClass.Shop),
        (41, "MAIN_ENTRY_ZONE/showers (1)", IndoorGeneric, false, 6.771f, 3.431f, 4.899f, RoomShotClass.Shop),
        (42, "MAIN_ENTRY_ZONE/corridor_2_2", IndoorGeneric, false, 14.970f, 5.239f, 8.341f, RoomShotClass.Shop),
        (43, "MAIN_ENTRY_ZONE/coridor_from_pool_1", IndoorGeneric, false, 12.180f, 5.096f, 7.386f, RoomShotClass.Shop),
        (44, "MAIN_ENTRY_ZONE/corridor_2_3", IndoorGeneric, false, 10.752f, 4.233f, 12.323f, RoomShotClass.Shop),
        (45, "MAIN_ENTRY_ZONE/pipes", IndoorGeneric, false, 11.317f, 4.212f, 4.585f, RoomShotClass.Shop),
        (46, "MAIN_ENTRY_ZONE/boiler (1)", IndoorGeneric, false, 6.407f, 3.436f, 9.312f, RoomShotClass.Shop),
    };

    static RoomShotClass Resolve(int type, bool outdoor, float w, float h, float d, out RoomShotPreset preset)
        => RoomShotAcousticsModel.Resolve(type, outdoor, false, w, h, d, null, out preset);

    [Fact]
    public void IcebergRoomsClassifyAsDocumented()
    {
        Assert.Equal(46, IcebergRooms.Length);
        Assert.Equal(46, IcebergRooms.Select(r => r.RoomId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 46), IcebergRooms.Select(r => r.RoomId).Order());
        var defaults = RoomShotTuning.Default;
        foreach (var r in IcebergRooms)
        {
            var cls = Resolve(r.Type, r.Outdoor, r.W, r.H, r.D, out var preset);
            Assert.True(r.Class == cls, $"room {r.RoomId} {r.Path}: expected {r.Class}, got {cls}");
            if (r.Class == RoomShotClass.Outdoor) { Assert.Null(preset); continue; }
            // No indoor room reaches the 50 m cap or falls below the 2 m floor in any edge: the shoebox is the real AABB.
            Assert.Equal(r.W, preset.Width, 3);
            Assert.Equal(r.H, preset.Height, 3);
            Assert.Equal(r.D, preset.Depth, 3);
            var t = defaults.For(cls);
            Assert.Equal(new[] { t.Walls, t.Walls, t.Ceiling, t.Floor, t.Walls, t.Walls }, preset.Materials);
            Assert.False(preset.UsedClassDefaults);
        }
        Assert.Equal(44, IcebergRooms.Count(r => r.Class == RoomShotClass.Shop));
        Assert.Equal(1, IcebergRooms.Count(r => r.Class == RoomShotClass.Hall));
        Assert.Equal(1, IcebergRooms.Count(r => r.Class == RoomShotClass.Outdoor));
        Assert.Equal(0, IcebergRooms.Count(r => r.Class == RoomShotClass.Metal)); // no steel body on this map
    }

    [Fact]
    public void IcebergHasOneHallAndTheNearMissesStayShops()
    {
        // coffe_2 (room 22) is the only room whose larger floor edge reaches the 40 m Hall rule (42.8 m).
        var hall = Assert.Single(IcebergRooms, r => r.Class == RoomShotClass.Hall);
        Assert.Equal(22, hall.RoomId);
        Assert.True(Math.Max(hall.W, hall.D) >= RoomShotAcousticsModel.HallMinFloorEdge);
        // The Pool (36.7 x 38.7 m) and coffe_hall (33.4 x 23.4 m, mirrored areas) stay below it.
        foreach (var id in new[] { 38, 35 })
        {
            var room = IcebergRooms.Single(r => r.RoomId == id);
            Assert.True(Math.Max(room.W, room.D) < RoomShotAcousticsModel.HallMinFloorEdge);
            Assert.Equal(RoomShotClass.Shop, room.Class);
        }
        Assert.True(IcebergRooms.Where(r => r.Class != RoomShotClass.Outdoor).Max(r => Math.Max(r.W, Math.Max(r.H, r.D))) < RoomShotAcousticsModel.DefaultMaxDimension);
        Assert.True(IcebergRooms.Where(r => r.Class != RoomShotClass.Outdoor).Min(r => Math.Min(r.W, Math.Min(r.H, r.D))) >= RoomShotAcousticsModel.MinDimension);
    }

    [Fact]
    public void TheOutdoorRoomIsOutdoorByTheFlagAndByTheTypeBit()
    {
        // Room 31 ("outdoor", 338.6 x 187.3 x 305.5 m): OutdoorCommon with the outdoor flag; either one alone is enough and the huge
        // floor never turns it into a Hall.
        var outdoor = Assert.Single(IcebergRooms, r => r.Outdoor);
        Assert.Equal(31, outdoor.RoomId);
        Assert.Equal(OutdoorCommon, outdoor.Type);
        foreach (var flag in new[] { true, false })
            Assert.Equal(RoomShotClass.Outdoor, Resolve(OutdoorCommon, flag, outdoor.W, outdoor.H, outdoor.D, out var preset));
        Assert.Equal(RoomShotClass.Outdoor, Resolve(IndoorGeneric, true, outdoor.W, outdoor.H, outdoor.D, out _));
    }

    // RoomReverb's TableHandler gives every room of the active group a mixer category by the isolated and outdoor FLAGS only
    // (isolated 8, outdoor 67, else 4) and refuses the graph ("conflicting mixer room category") when one room type would get two.
    static int MixerCategory(bool outdoor, bool isolated) => isolated ? 8 : outdoor ? 67 : 4;

    [Fact]
    public void IcebergRoomTypesMapToOneMixerCategoryEachAndTheShotRuleAgreesWithIt()
    {
        var categories = new Dictionary<int, int>();
        foreach (var r in IcebergRooms)
        {
            int category = MixerCategory(r.Outdoor, false);
            Assert.True(!categories.TryGetValue(r.Type, out int previous) || previous == category,
                $"room {r.RoomId} {r.Path}: type {r.Type} would get categories {previous} and {category}");
            categories[r.Type] = category;
            Assert.Equal(category == 67, RoomShotAcousticsModel.IsOutdoor(r.Type, r.Outdoor, false));
        }
        Assert.Equal(2, categories.Count);
        Assert.Equal(4, categories[IndoorGeneric]);
        Assert.Equal(67, categories[OutdoorCommon]);
        Assert.DoesNotContain(0, categories.Keys); // RoomReverb refuses a type-0 room
        // The source tables list exactly these three categories (4, 8, 67): the isolated one is simply unused here.
    }
}
