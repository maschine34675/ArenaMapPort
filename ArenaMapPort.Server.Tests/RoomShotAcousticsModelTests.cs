#nullable disable
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The invented room table behind per-room gunshot acoustics (RoomShotAcousticsModel). Every number under test is
/// invented design data (docs/progress/shot-acoustics.md); these tests pin the starting table so a later tuning edit is a
/// deliberate change, pin the rule that routes AirPit's real rooms to the intended classes, and pin the lab-tuning rules
/// (per-class options, fallback for an unknown material name, the re-apply rule). Room sizes are the full AABB sizes
/// (twice the bounds extents) of the AirPit graph; the Bowl section pins the 11 active rooms of the Bowl graph.
/// </summary>
public sealed class RoomShotAcousticsModelTests
{
    // EAudioRoomTypeMask values as they appear in the spatial-audio graphs.
    const int OutdoorGeneric = 1, IndoorGeneric = 4, IndoorIsolated = 8, OutdoorCommon = 64,
        IndoorMetalThin = 512, IndoorWoodThick = 1024, IndoorBrick = 2048, IndoorConcrete = 4096, IndoorFabric = 32768,
        IndoorMetalThick = 65536;

    // Literal copy of MetaXRAudioRoomAcousticProperties.MaterialPreset (Meta.XR.Audio.dll). Kept as a literal on purpose:
    // a typo in the model must fail here, not at raid time inside Enum.Parse.
    static readonly string[] MetaMaterialPresets =
    {
        "AcousticTile", "Brick", "BrickPainted", "Cardboard", "Carpet", "CarpetHeavy", "CarpetHeavyPadded", "CeramicTile",
        "Concrete", "ConcreteRough", "ConcreteBlock", "ConcreteBlockPainted", "Curtain", "Foliage", "Glass", "GlassHeavy",
        "Grass", "Gravel", "GypsumBoard", "Marble", "Mud", "PlasterOnBrick", "PlasterOnConcreteBlock", "Rubber", "Soil",
        "SoundProof", "Snow", "Steel", "Stone", "Vent", "Water", "WoodThin", "WoodThick", "WoodFloor", "WoodOnConcrete",
        "MetaDefault"
    };

    // One real AirPit room per indoor class (full sizes in metres).
    static readonly (RoomShotClass Class, int Type, float W, float H, float D)[] IndoorRooms =
    {
        (RoomShotClass.Hall, IndoorConcrete, 111.4f, 43.2f, 96.4f),
        (RoomShotClass.Shop, IndoorBrick, 14.0f, 5.3f, 9.5f),
        (RoomShotClass.Metal, IndoorMetalThin, 5.95f, 2.1f, 39.4f),
    };

    static RoomShotTuning Default => RoomShotTuning.Default;

    static RoomShotClass Resolve(int type, bool outdoor, bool isolated, float w, float h, float d, out RoomShotPreset preset,
        RoomShotTuning tuning = null)
        => RoomShotAcousticsModel.Resolve(type, outdoor, isolated, w, h, d, tuning, out preset);

    static RoomShotTuning WithMax(float max) => new RoomShotTuning(Default.Hall, Default.Shop, Default.Metal, max);

    // The default tuning with one class replaced.
    static RoomShotTuning Tune(RoomShotClass roomClass, RoomShotClassTuning tuning) => new RoomShotTuning(
        roomClass == RoomShotClass.Hall ? tuning : Default.Hall,
        roomClass == RoomShotClass.Shop ? tuning : Default.Shop,
        roomClass == RoomShotClass.Metal ? tuning : Default.Metal,
        Default.HallMaxDimension);

    static void AssertPreset(RoomShotPreset preset, float w, float h, float d, string right, string left, string ceiling,
        string floor, string front, string back, float clutter)
    {
        Assert.NotNull(preset);
        Assert.Equal(w, preset.Width, 3);
        Assert.Equal(h, preset.Height, 3);
        Assert.Equal(d, preset.Depth, 3);
        Assert.Equal(new[] { right, left, ceiling, floor, front, back }, preset.Materials);
        Assert.Equal(clutter, preset.ClutterFactor, 3);
    }

    // ---- the starting table ---------------------------------------------------------------------------------------------

    [Fact]
    public void AirPitMainHallIsAHallClampedToTheCap()
    {
        // AirPit "Main": 111.4 x 43.2 x 96.4 m, IndoorConcrete.
        var cls = Resolve(IndoorConcrete, false, false, 111.4f, 43.2f, 96.4f, out var preset);
        Assert.Equal(RoomShotClass.Hall, cls);
        AssertPreset(preset, 50f, 43.2f, 50f, "Concrete", "Concrete", "Concrete", "CeramicTile", "Concrete", "Concrete", 0.4f);
        Assert.False(preset.UsedClassDefaults);
    }

    [Fact]
    public void AirPitPassportHallIsAHallEvenThoughItsTypeIsBrick()
    {
        // AirPit "Start1": 111 x 35 x 89 m, IndoorBrick. Size wins over the brick type for the Hall class.
        var cls = Resolve(IndoorBrick, false, false, 111f, 35f, 89f, out var preset);
        Assert.Equal(RoomShotClass.Hall, cls);
        AssertPreset(preset, 50f, 35f, 50f, "Concrete", "Concrete", "Concrete", "CeramicTile", "Concrete", "Concrete", 0.4f);
    }

    [Fact]
    public void AirPitBurgerSpotIsAShopAtItsRealSize()
    {
        // AirPit "AudioRoom_Burger_Spot_3": 14.0 x 5.3 x 9.5 m, IndoorBrick.
        var cls = Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var preset);
        Assert.Equal(RoomShotClass.Shop, cls);
        AssertPreset(preset, 14.0f, 5.3f, 9.5f, "GypsumBoard", "GypsumBoard", "AcousticTile", "CeramicTile", "GypsumBoard", "GypsumBoard", 0.7f);
        Assert.False(preset.UsedClassDefaults);
    }

    [Fact]
    public void ShopMaterialsDoNotDependOnTheRoomType()
    {
        // The first table chose PlasterOnBrick or ConcreteBlockPainted walls by IndoorBrick; the walls are one lab value now.
        // AirPit AudioRoom_Loui_Peeton_2 (room_id 3, graph node 1197): 13.4 x 6.5 x 13.3 m, IndoorConcrete.
        Resolve(IndoorConcrete, false, false, 13.4f, 6.5f, 13.3f, out var concrete);
        Resolve(IndoorBrick, false, false, 13.4f, 6.5f, 13.3f, out var brick);
        AssertPreset(concrete, 13.4f, 6.5f, 13.3f, "GypsumBoard", "GypsumBoard", "AcousticTile", "CeramicTile", "GypsumBoard", "GypsumBoard", 0.7f);
        Assert.Equal(brick.Materials, concrete.Materials);
        Assert.Equal(brick.ClutterFactor, concrete.ClutterFactor);
    }

    [Theory]
    [InlineData(IndoorGeneric)]
    [InlineData(IndoorWoodThick)]
    [InlineData(IndoorMetalThick)]
    [InlineData(0)]
    public void EveryOtherIndoorTypeIsAShop(int type)
    {
        // IndoorMetalThick (Bay5 PlantBuldingA_back_indoor: room_id 27, graph node 1250; 9.0 x 2.7 x 10.0 m) is a Shop on
        // purpose at this size: IndoorMetalThin is Metal, IndoorMetalThick only from MetalThickBodyMinFloorEdge on (Bowl's
        // Mi-26, see the Bowl section). The runtime (and the ShotAcoustics log line) reports room_id, not the node id.
        var cls = Resolve(type, false, false, 9.0f, 2.7f, 10.0f, out var preset);
        Assert.Equal(RoomShotClass.Shop, cls);
        Assert.Equal("GypsumBoard", preset.Right);
    }

    [Fact]
    public void AirPitAirlinerRoomIsMetalAtItsRealSize()
    {
        // AirPit airliner rooms: 5.95 x 2.1 x 39.4 m, IndoorMetalThin.
        var cls = Resolve(IndoorMetalThin, false, false, 5.95f, 2.1f, 39.4f, out var preset);
        Assert.Equal(RoomShotClass.Metal, cls);
        AssertPreset(preset, 5.95f, 2.1f, 39.4f, "Steel", "Steel", "Steel", "Carpet", "Steel", "Steel", 0.7f);
    }

    [Fact]
    public void MetalWinsOverHallWhenAMetalRoomIsHuge()
    {
        // Rule order is Outdoor, Metal, Hall, Shop.
        var cls = Resolve(IndoorMetalThin, false, false, 45f, 10f, 45f, out var preset);
        Assert.Equal(RoomShotClass.Metal, cls);
        Assert.Equal(45f, preset.Width, 3);
    }

    [Fact]
    public void TheDefaultTuningIsTheDocumentedStartingTable()
    {
        // The choice of the second host measurement round (docs/progress/shot-acoustics.md, "Tuning round"); the numbers
        // stay INVENTED. Changing one is a decision.
        Assert.Equal(("Concrete", "Concrete", "CeramicTile", 0.4f), (Default.Hall.Walls, Default.Hall.Ceiling, Default.Hall.Floor, Default.Hall.Clutter));
        Assert.Equal(("GypsumBoard", "AcousticTile", "CeramicTile", 0.7f), (Default.Shop.Walls, Default.Shop.Ceiling, Default.Shop.Floor, Default.Shop.Clutter));
        Assert.Equal(("Steel", "Steel", "Carpet", 0.7f), (Default.Metal.Walls, Default.Metal.Ceiling, Default.Metal.Floor, Default.Metal.Clutter));
        Assert.Equal(50f, Default.HallMaxDimension);
        Assert.Equal(RoomShotAcousticsModel.DefaultMaxDimension, Default.HallMaxDimension);
    }

    // ---- classification and size ----------------------------------------------------------------------------------------

    [Fact]
    public void AirPitOutdoorRoomHasNoPreset()
    {
        // AirPit "Start2": 193.2 x 43.2 x 375.3 m, OutdoorGeneric with the outdoor flag.
        var cls = Resolve(OutdoorGeneric, true, false, 193.2f, 43.2f, 375.3f, out var preset);
        Assert.Equal(RoomShotClass.Outdoor, cls);
        Assert.Null(preset);
        Assert.Null(Default.For(RoomShotClass.Outdoor));
    }

    [Theory]
    [InlineData(OutdoorGeneric, false)] // outdoor type without the flag
    [InlineData(OutdoorCommon, false)]
    [InlineData(IndoorBrick, true)] // flag without an outdoor type
    public void OutdoorFlagOrOutdoorTypeBitMakesOutdoor(int type, bool flag)
    {
        var cls = Resolve(type, flag, false, 20f, 5f, 20f, out var preset);
        Assert.Equal(RoomShotClass.Outdoor, cls);
        Assert.Null(preset);
    }

    [Theory]
    [InlineData(true, OutdoorGeneric, 20f, "Shop")]
    [InlineData(true, IndoorBrick, 20f, "Shop")]
    [InlineData(true, OutdoorCommon, 50f, "Hall")]
    [InlineData(false, IndoorIsolated, 20f, "Shop")]
    public void IsolatedRoomsAreNeverOutdoor(bool outdoorFlag, int type, float edge, string expected)
    {
        // Same precedence as RoomReverb's mixer categories: isolated beats outdoor.
        var cls = Resolve(type, outdoorFlag, true, edge, 4f, edge, out var preset);
        Assert.Equal(expected, cls.ToString());
        Assert.NotNull(preset);
    }

    [Theory]
    [InlineData(39.9f, 20f, "Shop")]
    [InlineData(40f, 20f, "Hall")]
    [InlineData(20f, 40f, "Hall")]
    [InlineData(39.9f, 39.9f, "Shop")]
    public void HallStartsAtFortyMetresOnEitherFloorEdge(float width, float depth, string expected)
    {
        Assert.Equal(expected, Resolve(IndoorConcrete, false, false, width, 5f, depth, out _).ToString());
    }

    [Fact]
    public void HeightAloneNeverMakesAHall()
    {
        // 45 m is above the 40 m hall threshold and below the 50 m cap, so the height passes through unchanged.
        Assert.Equal(RoomShotClass.Shop, Resolve(IndoorConcrete, false, false, 10f, 45f, 10f, out var preset));
        Assert.Equal(45f, preset.Height, 3);
    }

    [Fact]
    public void EveryEdgeIsRaisedToTwoMetres()
    {
        Resolve(IndoorBrick, false, false, 1.0f, 0.5f, 1.5f, out var preset);
        Assert.Equal(new[] { 2f, 2f, 2f }, new[] { preset.Width, preset.Height, preset.Depth });
        // Zero-size bounds (a room without colliders), negative and NaN input all land on the minimum.
        Resolve(IndoorBrick, false, false, 0f, -3f, float.NaN, out preset);
        Assert.Equal(new[] { 2f, 2f, 2f }, new[] { preset.Width, preset.Height, preset.Depth });
    }

    [Fact]
    public void EveryEdgeIsCappedAtTheMaximumDimension()
    {
        Resolve(IndoorConcrete, false, false, 111.4f, 120f, 96.4f, out var preset);
        Assert.Equal(new[] { 50f, 50f, 50f }, new[] { preset.Width, preset.Height, preset.Depth });
        Resolve(IndoorConcrete, false, false, 111.4f, 43.2f, 96.4f, out preset, WithMax(100f));
        Assert.Equal(new[] { 100f, 43.2f, 96.4f }, new[] { preset.Width, preset.Height, preset.Depth });
        // The cap also applies to the non-Hall classes (the key is named after the only class that reaches it).
        Resolve(IndoorMetalThin, false, false, 5f, 3f, 150f, out preset, WithMax(30f));
        Assert.Equal(30f, preset.Depth, 3);
        // An invalid cap below the minimum never inverts the clamp range.
        Resolve(IndoorMetalThin, false, false, 5f, 3f, 150f, out preset, WithMax(0.5f));
        Assert.Equal(new[] { 2f, 2f, 2f }, new[] { preset.Width, preset.Height, preset.Depth });
    }

    [Fact]
    public void ANaNCapFallsBackToTheDefaultCap()
    {
        Resolve(IndoorConcrete, false, false, 111.4f, 43.2f, 96.4f, out var preset, WithMax(float.NaN));
        Assert.Equal(new[] { 50f, 43.2f, 50f }, new[] { preset.Width, preset.Height, preset.Depth });
    }

    [Fact]
    public void ClampingDoesNotChangeTheClass()
    {
        // The Hall test reads the real room size, not the clamped preset size.
        Assert.Equal(RoomShotClass.Hall, Resolve(IndoorConcrete, false, false, 45f, 5f, 10f, out var preset, WithMax(10f)));
        Assert.Equal(10f, preset.Width, 3);
    }

    // ---- material names -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheModelOffersExactlyTheMetaMaterialPresets()
    {
        Assert.Equal(36, MetaMaterialPresets.Length);
        Assert.Equal(MetaMaterialPresets, RoomShotAcousticsModel.MaterialNames);
        Assert.Equal(MetaMaterialPresets.Length, MetaMaterialPresets.Distinct().Count());
        foreach (var name in MetaMaterialPresets) Assert.True(RoomShotAcousticsModel.IsMaterialName(name), name);
        // Exact match only: the plugin parses the name case-sensitively.
        foreach (var bad in new[] { null, "", " ", "concrete", "CONCRETE", "Concrete ", "Bogus" })
            Assert.False(RoomShotAcousticsModel.IsMaterialName(bad), bad ?? "(null)");
    }

    [Fact]
    public void TheDefaultTuningUsesOnlyMetaMaterialNames()
    {
        foreach (var tuning in new[] { Default.Hall, Default.Shop, Default.Metal })
        {
            foreach (var name in new[] { tuning.Walls, tuning.Ceiling, tuning.Floor }) Assert.Contains(name, MetaMaterialPresets);
            Assert.InRange(tuning.Clutter, 0f, 1f); // the plugin's [Range(0, 1)]
        }
        // The named constants the table is built from are checked as well, so an unused typo cannot hide.
        foreach (var name in new[]
        {
            RoomShotAcousticsModel.Steel, RoomShotAcousticsModel.Carpet, RoomShotAcousticsModel.Concrete,
            RoomShotAcousticsModel.CeramicTile, RoomShotAcousticsModel.GypsumBoard, RoomShotAcousticsModel.AcousticTile
        }) Assert.Contains(name, MetaMaterialPresets);
    }

    [Fact]
    public void EveryResolvedPresetUsesOnlyMetaMaterialNames()
    {
        foreach (var room in IndoorRooms)
        {
            Assert.Equal(room.Class, Resolve(room.Type, false, false, room.W, room.H, room.D, out var preset));
            Assert.Equal(6, preset.Materials.Length);
            foreach (var name in preset.Materials) Assert.Contains(name, MetaMaterialPresets);
            Assert.InRange(preset.ClutterFactor, 0f, 1f);
        }
    }

    [Fact]
    public void MaterialChoicesLeadWithTheKeyDefaultAndKeepTheEnumOrder()
    {
        foreach (var keyDefault in MetaMaterialPresets)
        {
            var choices = RoomShotAcousticsModel.MaterialChoices(keyDefault);
            Assert.Equal(MetaMaterialPresets.Length, choices.Length);
            Assert.Equal(keyDefault, choices[0]);
            Assert.Equal(choices.Length, choices.Distinct().Count());
            // Every name is still offered; the others keep the enum order.
            Assert.Equal(MetaMaterialPresets.OrderBy(n => n), choices.OrderBy(n => n));
            Assert.Equal(MetaMaterialPresets.Where(n => n != keyDefault), choices.Skip(1));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bogus")]
    [InlineData("concrete")]
    public void MaterialChoicesForAnUnknownDefaultAreTheEnumListInANewArray(string notAName)
    {
        var choices = RoomShotAcousticsModel.MaterialChoices(notAName);
        Assert.Equal(MetaMaterialPresets, choices);
        // A caller can never alter the model's own list through the result.
        choices[0] = "Changed";
        Assert.Equal(MetaMaterialPresets, RoomShotAcousticsModel.MaterialNames);
        Assert.NotSame(RoomShotAcousticsModel.MaterialNames, RoomShotAcousticsModel.MaterialChoices(notAName));
        Assert.NotSame(RoomShotAcousticsModel.MaterialChoices("Steel"), RoomShotAcousticsModel.MaterialChoices("Steel"));
    }

    // BepInEx's AcceptableValueList.Clamp (Clamp is applied when the .cfg is loaded and on every set): a value that is not in
    // the list becomes the list's FIRST entry. Checked against the installed BepInEx.dll on 2026-10-15; copied here because
    // this project has no BepInEx reference.
    static string ClampLikeBepInEx(string[] list, string value) => value != null && list.Contains(value) ? value : list[0];

    [Theory]
    [InlineData("Concerte")] // a hand typo
    [InlineData("")] // an emptied value
    [InlineData("concrete")] // case matters
    [InlineData("NotInTheList")]
    [InlineData(null)]
    public void ABadConfigValueIsClampedToTheKeyDefaultAndNeedsNoModelCorrection(string bad)
    {
        // The nine material keys: each key's list starts with that key's own default, so the clamp lands there and the
        // resolved preset is exactly the default table, with nothing left for the model to correct.
        foreach (var room in IndoorRooms)
        {
            var d = Default.For(room.Class);
            var tuning = Tune(room.Class, new RoomShotClassTuning(
                ClampLikeBepInEx(RoomShotAcousticsModel.MaterialChoices(d.Walls), bad),
                ClampLikeBepInEx(RoomShotAcousticsModel.MaterialChoices(d.Ceiling), bad),
                ClampLikeBepInEx(RoomShotAcousticsModel.MaterialChoices(d.Floor), bad), d.Clutter));
            Assert.Equal(d.Walls, tuning.For(room.Class).Walls);
            Assert.Equal(d.Ceiling, tuning.For(room.Class).Ceiling);
            Assert.Equal(d.Floor, tuning.For(room.Class).Floor);
            Resolve(room.Type, false, false, room.W, room.H, room.D, out var preset, tuning);
            Assert.Equal(new[] { d.Walls, d.Walls, d.Ceiling, d.Floor, d.Walls, d.Walls }, preset.Materials);
            Assert.False(preset.UsedClassDefaults);
        }
    }

    [Fact]
    public void AValidConfigValueSurvivesTheClampWhateverTheListOrder()
    {
        foreach (var name in MetaMaterialPresets)
            foreach (var keyDefault in new[] { "Concrete", "CeramicTile", "GypsumBoard", "AcousticTile", "Steel", "Carpet" })
                Assert.Equal(name, ClampLikeBepInEx(RoomShotAcousticsModel.MaterialChoices(keyDefault), name));
    }

    // ---- tuning options -------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryMetaMaterialIsAcceptedAsATuningValue()
    {
        foreach (var name in MetaMaterialPresets)
        {
            foreach (var room in IndoorRooms)
            {
                var tuning = Tune(room.Class, new RoomShotClassTuning(name, name, name, 0.5f));
                Resolve(room.Type, false, false, room.W, room.H, room.D, out var preset, tuning);
                Assert.Equal(new[] { name, name, name, name, name, name }, preset.Materials);
                Assert.False(preset.UsedClassDefaults);
            }
        }
    }

    [Fact]
    public void EachClassReadsItsOwnTuningAndMapsItToTheSixWalls()
    {
        var tuning = new RoomShotTuning(
            new RoomShotClassTuning("Marble", "Glass", "Water", 0.11f),
            new RoomShotClassTuning("Brick", "Curtain", "Rubber", 0.22f),
            new RoomShotClassTuning("Vent", "Snow", "Mud", 0.33f),
            100f);
        Resolve(IndoorConcrete, false, false, 111.4f, 43.2f, 96.4f, out var hall, tuning);
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var shop, tuning);
        Resolve(IndoorMetalThin, false, false, 5.95f, 2.1f, 39.4f, out var metal, tuning);
        // Plugin wall order: right, left, ceiling, floor, front, back; "Walls" is the four side walls.
        AssertPreset(hall, 100f, 43.2f, 96.4f, "Marble", "Marble", "Glass", "Water", "Marble", "Marble", 0.11f);
        AssertPreset(shop, 14.0f, 5.3f, 9.5f, "Brick", "Brick", "Curtain", "Rubber", "Brick", "Brick", 0.22f);
        AssertPreset(metal, 5.95f, 2.1f, 39.4f, "Vent", "Vent", "Snow", "Mud", "Vent", "Vent", 0.33f);
    }

    [Fact]
    public void ATuningEditOfOneClassLeavesTheOthersAlone()
    {
        var tuning = Tune(RoomShotClass.Shop, new RoomShotClassTuning("Glass", "Glass", "Glass", 0.9f));
        Resolve(IndoorConcrete, false, false, 111.4f, 43.2f, 96.4f, out var hall, tuning);
        Resolve(IndoorMetalThin, false, false, 5.95f, 2.1f, 39.4f, out var metal, tuning);
        AssertPreset(hall, 50f, 43.2f, 50f, "Concrete", "Concrete", "Concrete", "CeramicTile", "Concrete", "Concrete", 0.4f);
        AssertPreset(metal, 5.95f, 2.1f, 39.4f, "Steel", "Steel", "Steel", "Carpet", "Steel", "Steel", 0.7f);
    }

    [Fact]
    public void ANullTuningMeansTheDefaultTuning()
    {
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var viaNull, null);
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var viaDefault, Default);
        Assert.Equal(viaDefault.Materials, viaNull.Materials);
        Assert.Equal(viaDefault.ClutterFactor, viaNull.ClutterFactor);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("concrete")] // case matters: the plugin parses names case-sensitively
    [InlineData(" Steel")]
    public void AnUnknownMaterialNameFallsBackToTheClassDefaultAndIsReported(string bad)
    {
        foreach (var room in IndoorRooms)
        {
            var classDefault = Default.For(room.Class);
            foreach (var field in new[] { "Walls", "Ceiling", "Floor" })
            {
                // Valid placeholders for the two fields that are not under test, so exactly one value is wrong.
                string walls = field == "Walls" ? bad : "Brick", ceiling = field == "Ceiling" ? bad : "Glass",
                    floor = field == "Floor" ? bad : "Rubber";
                var cls = Resolve(room.Type, false, false, room.W, room.H, room.D, out var preset,
                    Tune(room.Class, new RoomShotClassTuning(walls, ceiling, floor, 0.25f)));

                Assert.Equal(room.Class, cls);
                string expectedWalls = field == "Walls" ? classDefault.Walls : "Brick";
                string expectedCeiling = field == "Ceiling" ? classDefault.Ceiling : "Glass";
                string expectedFloor = field == "Floor" ? classDefault.Floor : "Rubber";
                Assert.Equal(new[] { expectedWalls, expectedWalls, expectedCeiling, expectedFloor, expectedWalls, expectedWalls },
                    preset.Materials);
                Assert.Equal(0.25f, preset.ClutterFactor, 3); // the valid clutter is untouched
                Assert.True(preset.UsedClassDefaults);
                string expectedDefault = field == "Walls" ? classDefault.Walls : field == "Ceiling" ? classDefault.Ceiling : classDefault.Floor;
                var correction = Assert.Single(preset.Corrections);
                Assert.Equal($"{room.Class}{field}='{bad ?? "(null)"}'->{expectedDefault}", correction);
                Assert.Equal(correction, preset.CorrectionsText);
            }
        }
    }

    [Fact]
    public void SeveralBadValuesAreAllReportedInFieldOrder()
    {
        var tuning = Tune(RoomShotClass.Hall, new RoomShotClassTuning("Bogus", "Nope", "Void", float.NaN));
        var cls = Resolve(IndoorConcrete, false, false, 111.4f, 43.2f, 96.4f, out var preset, tuning);
        Assert.Equal(RoomShotClass.Hall, cls);
        // The whole class falls back to its own defaults, never to another class's.
        AssertPreset(preset, 50f, 43.2f, 50f, "Concrete", "Concrete", "Concrete", "CeramicTile", "Concrete", "Concrete", 0.4f);
        Assert.Equal(new[]
        {
            "HallWalls='Bogus'->Concrete", "HallCeiling='Nope'->Concrete", "HallFloor='Void'->CeramicTile", "HallClutter=NaN->0.4"
        }, preset.Corrections);
        Assert.Equal("HallWalls='Bogus'->Concrete; HallCeiling='Nope'->Concrete; HallFloor='Void'->CeramicTile; HallClutter=NaN->0.4",
            preset.CorrectionsText);
    }

    [Fact]
    public void ABadNameNeverThrows()
    {
        var bad = new RoomShotClassTuning(null, "", "???", float.PositiveInfinity);
        var tuning = new RoomShotTuning(bad, bad, bad, float.NaN);
        foreach (var room in IndoorRooms)
        {
            var ex = Record.Exception(() => Resolve(room.Type, false, false, room.W, room.H, room.D, out _, tuning));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void AClutterFactorOutsideZeroToOneIsClampedAndReported()
    {
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var high, Tune(RoomShotClass.Shop, new RoomShotClassTuning("Brick", "Brick", "Brick", 1.5f)));
        Assert.Equal(1f, high.ClutterFactor, 3);
        Assert.Equal("ShopClutter=1.5->1", Assert.Single(high.Corrections));
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var low, Tune(RoomShotClass.Shop, new RoomShotClassTuning("Brick", "Brick", "Brick", -0.2f)));
        Assert.Equal(0f, low.ClutterFactor, 3);
        Assert.Equal("ShopClutter=-0.2->0", Assert.Single(low.Corrections));
        // The ends of the range are fine as they are.
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var edge, Tune(RoomShotClass.Shop, new RoomShotClassTuning("Brick", "Brick", "Brick", 0f)));
        Assert.Equal(0f, edge.ClutterFactor, 3);
        Assert.False(edge.UsedClassDefaults);
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out edge, Tune(RoomShotClass.Shop, new RoomShotClassTuning("Brick", "Brick", "Brick", 1f)));
        Assert.Equal(1f, edge.ClutterFactor, 3);
        Assert.False(edge.UsedClassDefaults);
        // Infinity is clamped, NaN takes the class default.
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var inf, Tune(RoomShotClass.Shop, new RoomShotClassTuning("Brick", "Brick", "Brick", float.PositiveInfinity)));
        Assert.Equal(1f, inf.ClutterFactor, 3);
        Resolve(IndoorBrick, false, false, 14.0f, 5.3f, 9.5f, out var nan, Tune(RoomShotClass.Shop, new RoomShotClassTuning("Brick", "Brick", "Brick", float.NaN)));
        Assert.Equal(0.7f, nan.ClutterFactor, 3);
    }

    [Fact]
    public void AnOutdoorRoomIgnoresTheTuningEntirely()
    {
        var bad = new RoomShotClassTuning("Bogus", "Bogus", "Bogus", float.NaN);
        var cls = Resolve(OutdoorGeneric, true, false, 100f, 10f, 100f, out var preset, new RoomShotTuning(bad, bad, bad, Default.HallMaxDimension));
        Assert.Equal(RoomShotClass.Outdoor, cls);
        Assert.Null(preset);
    }

    // ---- the re-apply rule ----------------------------------------------------------------------------------------------

    [Fact]
    public void ARoomIsAppliedOnTheFirstTickAndOnEveryRealChange()
    {
        const int none = RoomShotAcousticsModel.NoRoom;
        // Nothing applied yet: always apply, whatever the other inputs say.
        Assert.True(RoomShotAcousticsModel.NeedsApply(none, 3, 0, 0, false));
        Assert.True(RoomShotAcousticsModel.NeedsApply(none, 3, 5, 5, true));
        // The same room with the same tuning is left alone: nothing happens per frame.
        Assert.False(RoomShotAcousticsModel.NeedsApply(3, 3, 5, 5, false));
        // A different room re-applies.
        Assert.True(RoomShotAcousticsModel.NeedsApply(3, 27, 5, 5, false));
    }

    [Fact]
    public void AChangedTuningReappliesTheSameRoom()
    {
        // A lab key was edited (SettingChanged bumped the tuning version): the unchanged room must be applied again.
        Assert.True(RoomShotAcousticsModel.NeedsApply(3, 3, 5, 6, false));
        Assert.True(RoomShotAcousticsModel.NeedsApply(3, 3, 6, 5, false)); // any difference, not only "newer"
        // The counter may wrap around; equality is what counts.
        Assert.True(RoomShotAcousticsModel.NeedsApply(3, 3, int.MaxValue, int.MinValue, false));
        Assert.False(RoomShotAcousticsModel.NeedsApply(3, 3, int.MinValue, int.MinValue, false));
        Assert.False(RoomShotAcousticsModel.NeedsApply(3, 3, int.MaxValue, int.MaxValue, false));
    }

    [Fact]
    public void EachTuningChangeCausesExactlyOneApplicationNoMatterHowManyKeysChanged()
    {
        // The runtime's loop: remember the room and the tuning version that were applied.
        int appliedRoom = RoomShotAcousticsModel.NoRoom, appliedVersion = 0, version = 0, applications = 0;
        void Frame(int room)
        {
            if (!RoomShotAcousticsModel.NeedsApply(appliedRoom, room, appliedVersion, version, false)) return;
            appliedRoom = room; appliedVersion = version; applications++;
        }
        Frame(3); Frame(3); Frame(3);
        Assert.Equal(1, applications);
        version++; // one edit
        Frame(3); Frame(3);
        Assert.Equal(2, applications);
        version += 3; // three edits between two frames (a profile paste): still one application
        Frame(3); Frame(3);
        Assert.Equal(3, applications);
        Frame(27); Frame(27); // a new room
        Assert.Equal(4, applications);
    }

    [Fact]
    public void AnAudioConfigurationChangeReappliesTheSameRoom()
    {
        // The plugin's setter skips an unchanged managed value, so after an audio reset or device switch only an explicit
        // re-application can put the model back on the native side: the unchanged room must not short-circuit it.
        Assert.True(RoomShotAcousticsModel.NeedsApply(3, 3, 5, 5, true));
        Assert.True(RoomShotAcousticsModel.NeedsApply(int.MaxValue, int.MaxValue, 10, 10, true));
    }

    [Theory]
    [InlineData(false, false, true)] // a normal game client
    [InlineData(true, false, false)] // batch mode (a dedicated or headless host)
    [InlineData(false, true, false)] // null graphics device (-nographics)
    [InlineData(true, true, false)]
    public void OnlyAClientThatCanBeHeardTouchesThePluginsNativeAcoustics(bool batchMode, bool nullGraphics, bool expected)
    {
        // EFT creates the AudioListener object on every client, headless ones included, so only these two process-level
        // facts separate a client that can be heard from one that cannot.
        Assert.Equal(expected, RoomShotAcousticsModel.IsAudioClient(batchMode, nullGraphics));
    }

    [Fact]
    public void LogTextsAreInvariantAndOrdered()
    {
        Resolve(IndoorBrick, false, false, 14.04f, 5.3f, 9.5f, out var preset);
        Assert.Equal("14.0x5.3x9.5", preset.DimensionsText);
        Assert.Equal("GypsumBoard,GypsumBoard,AcousticTile,CeramicTile,GypsumBoard,GypsumBoard", preset.MaterialsText);
        Assert.Empty(preset.Corrections);
        Assert.Equal("", preset.CorrectionsText);
        // A preset built without corrections reports none.
        Assert.False(new RoomShotPreset(2f, 2f, 2f, "Steel", "Steel", "Steel", "Carpet", "Steel", "Steel", 0.5f).UsedClassDefaults);
    }

    // ---- Bowl (PLAN-BOWL item 17) ---------------------------------------------------------------------------------------

    // Bowl's spatial-audio graph (work/extract/spatial-audio/arena_bowl: 14 rooms, 11 of them in the active group). Sizes are
    // the full AABB sizes in metres (twice the bounds extents). RoomId is the runtime ISpatialAudioRoom.ID (the graph's
    // room_id), which the "[ShotAcoustics] arena_bowl: room=<id>/<type>" line prints; GraphId is the graph's own
    // room id (what the group's room_ids, RoomReverb and the census use). Path is below SpatialAudioSystem/.
    static readonly (int RoomId, int GraphId, string Path, int Type, bool Outdoor, float W, float H, float D, RoomShotClass Class)[] BowlActiveRooms =
    {
        (1, 531, "Containers_R/Container_1", IndoorMetalThin, false, 8.152f, 2.680f, 7.038f, RoomShotClass.Metal),
        (2, 554, "Military_R/Hospital_enterance", IndoorFabric, false, 2.306f, 2.438f, 3.592f, RoomShotClass.Shop),
        (3, 558, "Offices_L/Offices_AudioRoom_3", IndoorWoodThick, false, 14.370f, 2.503f, 2.262f, RoomShotClass.Shop),
        (4, 563, "Helicopter/Helicopter", IndoorMetalThick, false, 20.776f, 4.488f, 13.788f, RoomShotClass.Metal),
        (5, 568, "FanZone/FanZone", IndoorGeneric, false, 49.478f, 2.911f, 10.124f, RoomShotClass.Hall),
        (6, 587, "Containers_L/Container_2", IndoorMetalThin, false, 6.536f, 2.637f, 4.893f, RoomShotClass.Metal),
        (7, 594, "Offices_L/Offices_AudioRoom_2", IndoorWoodThick, false, 5.998f, 2.503f, 2.262f, RoomShotClass.Shop),
        (8, 636, "Outdoor/Outdoor", OutdoorCommon, true, 239.871f, 42.439f, 235.251f, RoomShotClass.Outdoor),
        (9, 656, "Military_R/Hospital", IndoorMetalThin, false, 8.837f, 2.830f, 14.208f, RoomShotClass.Metal),
        (10, 665, "Containers_L/Container_1", IndoorMetalThin, false, 3.387f, 2.680f, 6.390f, RoomShotClass.Metal),
        (11, 672, "Offices_L/Offices_AudioRoom_1", IndoorWoodThick, false, 2.262f, 2.413f, 12.107f, RoomShotClass.Shop),
    };

    static void AssertClassDefaultPreset(RoomShotPreset preset, RoomShotClass roomClass, float w, float h, float d)
    {
        var t = Default.For(roomClass);
        AssertPreset(preset, w, h, d, t.Walls, t.Walls, t.Ceiling, t.Floor, t.Walls, t.Walls, t.Clutter);
        Assert.False(preset.UsedClassDefaults);
    }

    [Fact]
    public void BowlActiveRoomsClassifyAsDocumented()
    {
        Assert.Equal(11, BowlActiveRooms.Length);
        Assert.Equal(11, BowlActiveRooms.Select(r => r.RoomId).Distinct().Count());
        Assert.Equal(11, BowlActiveRooms.Select(r => r.GraphId).Distinct().Count());
        foreach (var r in BowlActiveRooms)
        {
            var cls = Resolve(r.Type, r.Outdoor, false, r.W, r.H, r.D, out var preset);
            Assert.True(r.Class == cls, $"room {r.RoomId} (graph {r.GraphId}) {r.Path}: expected {r.Class}, got {cls}");
            if (r.Class == RoomShotClass.Outdoor) { Assert.Null(preset); continue; }
            // No indoor room of Bowl reaches the 50 m cap or falls below the 2 m floor in any edge, so the shoebox is the real AABB.
            AssertClassDefaultPreset(preset, r.Class, r.W, r.H, r.D);
        }
        Assert.Equal(5, BowlActiveRooms.Count(r => r.Class == RoomShotClass.Metal)); // 4 IndoorMetalThin + the Mi-26
        Assert.Equal(1, BowlActiveRooms.Count(r => r.Class == RoomShotClass.Hall));
        Assert.Equal(4, BowlActiveRooms.Count(r => r.Class == RoomShotClass.Shop));
        Assert.Equal(1, BowlActiveRooms.Count(r => r.Class == RoomShotClass.Outdoor));
    }

    [Fact]
    public void BowlStadiumIsOutdoorByTheFlagAndByTheTypeBit()
    {
        // Room 8 (graph 636, 239.9 x 42.4 x 235.3 m): OutdoorCommon with the outdoor flag, not isolated. Either one alone is
        // enough, and the huge floor never turns it into a Hall; no preset, so the shipped acoustic model stays.
        foreach (var flag in new[] { true, false })
        {
            Assert.Equal(RoomShotClass.Outdoor, Resolve(OutdoorCommon, flag, false, 239.871f, 42.439f, 235.251f, out var preset));
            Assert.Null(preset);
        }
        Assert.Equal(RoomShotClass.Outdoor, Resolve(IndoorGeneric, true, false, 239.871f, 42.439f, 235.251f, out _));
    }

    [Fact]
    public void BowlCrashedMi26IsAMetalBody()
    {
        // Room 4 (graph 563, Helicopter/Helicopter): IndoorMetalThick, AABB 20.8 x 4.5 x 13.8 m around a hull that lies rotated
        // by about 30 degrees. A steel hull: Steel walls and ceiling, not the shop table (GypsumBoard / AcousticTile).
        var cls = Resolve(IndoorMetalThick, false, false, 20.776f, 4.488f, 13.788f, out var preset);
        Assert.Equal(RoomShotClass.Metal, cls);
        AssertPreset(preset, 20.776f, 4.488f, 13.788f, "Steel", "Steel", "Steel", "Carpet", "Steel", "Steel", 0.7f);
        Assert.False(preset.UsedClassDefaults);
    }

    [Theory]
    [InlineData(14.9f, 5f, "Shop")]
    [InlineData(15f, 5f, "Metal")]
    [InlineData(5f, 15f, "Metal")]
    [InlineData(9.0f, 10.0f, "Shop")] // Bay5 PlantBuldingA_back_indoor
    [InlineData(20.776f, 13.788f, "Metal")] // Bowl Mi-26
    [InlineData(45f, 10f, "Metal")] // like IndoorMetalThin: a steel body wins over the 40 m Hall rule
    public void AnIndoorMetalThickRoomIsASteelBodyFromFifteenMetresOnEitherFloorEdge(float width, float depth, string expected)
    {
        Assert.Equal(15f, RoomShotAcousticsModel.MetalThickBodyMinFloorEdge);
        Assert.Equal(expected, Resolve(IndoorMetalThick, false, false, width, 3f, depth, out var preset).ToString());
        Assert.NotNull(preset);
    }

    [Fact]
    public void AMetalThickRoomIsStillOutdoorWhenFlaggedAndOtherTypesIgnoreTheSizeEdge()
    {
        Assert.Equal(RoomShotClass.Outdoor, Resolve(IndoorMetalThick, true, false, 20.8f, 4.5f, 13.8f, out var preset));
        Assert.Null(preset);
        // The size edge belongs to IndoorMetalThick only: other indoor types of the same size are unchanged.
        foreach (var type in new[] { IndoorGeneric, IndoorWoodThick, IndoorBrick, IndoorConcrete, IndoorFabric, 0 })
            Assert.Equal(RoomShotClass.Shop, Resolve(type, false, false, 20.8f, 4.5f, 13.8f, out _));
    }

    [Fact]
    public void Bay5BackRoomKeepsItsShopResult()
    {
        // Bay5 PlantBuldingA_back_indoor (room_id 27): IndoorMetalThick 9.0 x 2.7 x 10.0 m, released as a Shop; the Bowl Mi-26
        // rule must not move it.
        var cls = Resolve(IndoorMetalThick, false, false, 9.0f, 2.7f, 10.0f, out var preset);
        Assert.Equal(RoomShotClass.Shop, cls);
        AssertPreset(preset, 9.0f, 2.7f, 10.0f, "GypsumBoard", "GypsumBoard", "AcousticTile", "CeramicTile", "GypsumBoard", "GypsumBoard", 0.7f);
    }

    [Fact]
    public void BowlFanZoneIsAHallByTheFortyMetreRule()
    {
        // Room 5 (graph 568, FanZone/FanZone): IndoorGeneric 49.5 x 2.9 x 10.1 m, a long low concourse above the north stand
        // (like Equator's UpRoof corridor, a Hall by the 40 m rule). 49.5 m is under the 50 m cap, so the real size is used.
        var cls = Resolve(IndoorGeneric, false, false, 49.478f, 2.911f, 10.124f, out var preset);
        Assert.Equal(RoomShotClass.Hall, cls);
        AssertPreset(preset, 49.478f, 2.911f, 10.124f, "Concrete", "Concrete", "Concrete", "CeramicTile", "Concrete", "Concrete", 0.4f);
    }

    [Fact]
    public void BowlInactiveSourceRoomsAreHarmlessEvenIfOneWereApplied()
    {
        // Graph rooms 572 (Military_L/Base) and 659 (Military_L/Tower): IndoorGeneric with 0 x 0 x 0 bounds; 550
        // (Military_R/Base): 9.4 x 1.9 x 5.3 m. All three are inactive in the hierarchy and outside the active group (11 of
        // the 14 rooms), so ListenerCurrentRoom is never one of them; the model still gives a sane answer for them.
        var cls = Resolve(IndoorGeneric, false, false, 0f, 0f, 0f, out var zero);
        Assert.Equal(RoomShotClass.Shop, cls);
        AssertClassDefaultPreset(zero, RoomShotClass.Shop, 2f, 2f, 2f);
        cls = Resolve(IndoorGeneric, false, false, 9.404f, 1.938f, 5.287f, out var low);
        Assert.Equal(RoomShotClass.Shop, cls);
        AssertClassDefaultPreset(low, RoomShotClass.Shop, 9.404f, 2f, 5.287f); // only the height is raised to the 2 m floor
    }

    // RoomReverb's TableHandler gives every room of the active group a mixer category by the isolated and outdoor FLAGS only
    // (isolated 8, outdoor 67, else 4) and refuses the graph ("conflicting mixer room category") when one room type would get two.
    static int MixerCategory(bool outdoor, bool isolated) => isolated ? 8 : outdoor ? 67 : 4;

    [Fact]
    public void BowlRoomTypesMapToOneMixerCategoryEachAndTheShotRuleAgreesWithIt()
    {
        var categories = new Dictionary<int, int>();
        foreach (var r in BowlActiveRooms)
        {
            int category = MixerCategory(r.Outdoor, false);
            Assert.True(!categories.TryGetValue(r.Type, out int previous) || previous == category,
                $"room {r.RoomId} {r.Path}: type {r.Type} would get categories {previous} and {category}");
            categories[r.Type] = category;
            // Same set of rooms: the shot-acoustics Outdoor decision (flag or type bit) equals RoomReverb's outdoor category.
            Assert.Equal(category == 67, RoomShotAcousticsModel.IsOutdoor(r.Type, r.Outdoor, false));
        }
        Assert.Equal(6, categories.Count); // 4, 512, 1024, 32768, 65536 and 64
        Assert.Equal(67, categories[OutdoorCommon]);
        Assert.All(categories.Where(c => c.Key != OutdoorCommon), c => Assert.Equal(4, c.Value));
        Assert.DoesNotContain(0, categories.Keys); // RoomReverb refuses a type-0 room
    }
}

/// <summary>
/// RoomShotAcoustics sits on Unity, BepInEx and Meta types and cannot be compiled into this project, so its wiring is
/// read as text, like AirPitWiringTests does for the other per-map audio files.
/// </summary>
public sealed class RoomShotAcousticsWiringTests
{
    static string Text(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void ConfigIsBoundForEveryReleasedMapAndDefaultsOn()
    {
        var code = Text("ArenaMapPort.Client/RoomShotAcoustics.cs");
        // Loops the registry instead of a literal list, so a newly released map cannot be missed.
        Assert.Contains("foreach (var map in ArenaMapRegistry.All)", code);
        Assert.Matches(@"""Audio \("" \+ map\.Key \+ ""\)"", ""RoomShotAcoustics"", true,", code);
        Assert.DoesNotMatch(@"(?<![A-Za-z])Config\.Bind\(", code); // tuning binds go through Settings
        Assert.True(ArenaMapRegistry.All.Count >= 3);
    }

    [Fact]
    public void TheLabKeysLiveInTheShotAcousticsLabSectionAndTheOldCapKeyIsGone()
    {
        var code = Text("ArenaMapPort.Client/RoomShotAcoustics.cs");
        Assert.Contains(@"const string LabSection = ""ShotAcousticsLab"";", code);
        // One helper binds the four keys of a class: <Class>Walls / Ceiling / Floor / Clutter.
        foreach (var roomClass in new[] { "Hall", "Shop", "Metal" })
            Assert.Contains($@"BindClass(""{roomClass}"", defaults.{roomClass},", code);
        foreach (var key in new[] { "Walls", "Ceiling", "Floor", "Clutter" })
            Assert.Matches($@"Settings\.Bind\(LabSection, name \+ ""{key}"", defaults\.{key},", code);
        Assert.Matches(@"Settings\.Bind\(LabSection, ""HallMaxDimension"", defaults\.HallMaxDimension,", code);
        // Defaults come from the model's table; the material keys offer the model's list of Meta preset names, each list led by
        // ITS key's default: BepInEx clamps a value outside the list to the first entry, so a typo lands on the key's default.
        Assert.Contains("var defaults = RoomShotTuning.Default;", code);
        Assert.Contains("new AcceptableValueList<string>(RoomShotAcousticsModel.MaterialChoices(keyDefault))", code);
        foreach (var field in new[] { "Walls", "Ceiling", "Floor" })
            Assert.Contains($"MaterialList(defaults.{field})", code);
        Assert.DoesNotContain("MaterialNames.Clone()", code); // no key may share the plain enum-ordered list
        // The key was bound for the first time on 2026-10-15 and never shipped: renamed, the old bind removed entirely.
        Assert.DoesNotContain("RoomShotAcousticsMaxDimension", code);
        Assert.DoesNotContain("Audio diagnostics", code);
        Assert.DoesNotContain("_maxDimension", code);
    }

    [Fact]
    public void EveryLabKeyMarksTheTuningChanged()
    {
        var code = Text("ArenaMapPort.Client/RoomShotAcoustics.cs");
        // Four per class (walls, ceiling, floor, clutter) plus HallMaxDimension; each handler bumps the tuning version.
        Assert.Equal(5, Regex.Matches(code, @"\.SettingChanged \+= MarkTuningChanged;").Count);
        Assert.Matches(@"static void MarkTuningChanged\(object sender, EventArgs args\) => _tuningVersion\+\+;", code);
        Assert.Contains("ReadTuning()", code); // the values are read on each application, never cached
    }

    [Fact]
    public void NativeSpatialAudioTicksAndCleansUpTheRepair()
    {
        var code = Text("ArenaMapPort.Client/NativeSpatialAudio.cs");
        Assert.Matches(@"RoomReverb\.Tick\(state\);\s*RoomShotAcoustics\.Tick\(state\);", code);
        Assert.Contains("CleanupStep(RoomShotAcoustics.Stop);", code);
    }

    [Fact]
    public void RoomReverbStillDerivesItsMixerCategoryFromTheIsolatedAndOutdoorFlagsOnly()
    {
        // RoomShotAcousticsModelTests copies this rule for the Bowl category check; a change here must change that copy.
        var code = Text("ArenaMapPort.Client/RoomReverb.cs");
        Assert.Contains(@"category = (bool)room[""isolated""] ? 8 : (bool)room[""outdoor""] ? 67 : 4;", code);
        Assert.Contains("conflicting mixer room category", code);
    }

    [Fact]
    public void PluginInstallsTheRepairRightAfterRoomReverb()
    {
        Assert.Matches(@"RoomReverb\.Install\(\);\s*(//[^\n]*\n\s*)?RoomShotAcoustics\.Install\(\);", Text("ArenaMapPort.Client/Plugin.cs"));
    }

    [Fact]
    public void ClientReferencesTheMetaAudioAssemblyLikeTheOtherGameAssemblies()
    {
        var csproj = Text("ArenaMapPort.Client/ArenaMapPort.Client.csproj");
        Assert.Matches(@"<Reference Include=""Meta\.XR\.Audio"">\s*<HintPath>\$\(EftManagedDir\)\\Meta\.XR\.Audio\.dll</HintPath>\s*<Private>false</Private>", csproj);
    }

    static string WithoutComments(string code) => Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    [Fact]
    public void NoSceneSearchRunsInsideTheRaid()
    {
        // FindObjectsOfType over a ported scene costs about 30 ms on the main thread (AGENTS.md traps); the first indoor tick
        // used to pay it for a foreign-room check the repair does not need (it is the only creator of the component).
        var code = WithoutComments(Text("ArenaMapPort.Client/RoomShotAcoustics.cs"));
        Assert.DoesNotMatch(@"FindObjectsOfType|FindObjectOfType|FindObjectsByType|FindObjectByType|Resources\.FindObjects|GetComponentsInChildren", code);
        Assert.DoesNotContain("ForeignRoom", code);
    }

    [Fact]
    public void AnAudioConfigurationChangeIsObservedAndReappliesThroughTheModelRule()
    {
        var code = Text("ArenaMapPort.Client/RoomShotAcoustics.cs");
        Assert.Matches(@"AudioSettings\.OnAudioConfigurationChanged \+= \w+ => _audioReset = true;", code);
        // The tuning version replaces the old size-cap comparison: a lab edit and a device switch share one rule.
        Assert.Contains("RoomShotAcousticsModel.NeedsApply(_appliedRoomId, room.ID, _appliedTuningVersion, version, reassert)", code);
        Assert.Contains("MetaPlugin.ResendModel()", code);
    }

    [Fact]
    public void TheFallbackMarkerIsPartOfTheExistingDebugLine()
    {
        // One Debug line per application (no extra warning line); it carries the class-default marker when a name was replaced.
        var code = Text("ArenaMapPort.Client/RoomShotAcoustics.cs");
        Assert.Contains("class-default-fallback=[{preset.CorrectionsText}]", code);
        Assert.Single(Regex.Matches(code, @"Plugin\.Log\.LogDebug\(\$""\{Tag\} \{state\.MapKey\}: room="));
        Assert.DoesNotMatch(@"LogWarning", WithoutComments(code));
    }

    [Fact]
    public void TheHeadlessGateReadsTheProcessNotJustTheListenerObject()
    {
        // The AudioListener object exists on every client (ClientApplication.StartTask), so it cannot be the gate.
        var code = WithoutComments(Text("ArenaMapPort.Client/RoomShotAcoustics.cs"));
        Assert.Contains("RoomShotAcousticsModel.IsAudioClient(Application.isBatchMode,", code);
        Assert.Contains("GraphicsDeviceType.Null", code);
        Assert.DoesNotContain("GetComponent<AudioListener>", code);
    }

    [Fact]
    public void TheProbeCoversThePlayAtPointRouteAndBothWeaponQueueRoutes()
    {
        var code = Text("ArenaMapPort.Client/ReverbAudioProbe.cs");
        // The gunshot buses are appended after the existing ones: saved ReverbPulseBus values keep their meaning.
        Assert.Matches(@"enum ProbeBus \{ Ambient, Gameplay, MapAmbience, IndoorRain, LegacyIndoorRain, Gunshots, GunshotsFirstPerson, GunshotsThirdPerson \}", code);
        Assert.Contains("audio.PlayAtPoint(position, _clip, BetterAudio.AudioSourceGroupType.Gunshots", code);
        Assert.Contains("autoReleaseSource: false", code); // released by the probe, never twice
        Assert.Contains("_queue.EnableStereo(_busUsed == ProbeBus.GunshotsFirstPerson);", code);
        Assert.Contains("pulse route", code);
        Assert.Contains("MetaXRAcousticSettings.Instance.AcousticModel", code);
        Assert.Contains("GetRoomDimensions", code);
    }

    [Fact]
    public void TheMetaApiIsOnlyTouchedFromTheIsolatedHelper()
    {
        // A vanilla map must never JIT a method that mentions a Meta type: only the nested MetaPlugin class may name them.
        var code = Text("ArenaMapPort.Client/RoomShotAcoustics.cs");
        int start = code.IndexOf("static class MetaPlugin", StringComparison.Ordinal);
        Assert.True(start > 0, "MetaPlugin helper missing");
        var outside = code.Substring(0, start);
        Assert.DoesNotMatch(@"MetaXR\w+|AcousticModel\b|MaterialPreset", Regex.Replace(outside, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline));
    }
}
