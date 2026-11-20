#nullable disable
using System.Text.Json;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

// PLAN-AIRPIT item 11. ShaderRebind and ShaderStandins sit on Unity and BepInEx types and cannot be compiled into
// this project, so every per-map decision they take comes from the pure ShaderStandinPolicy and is pinned here:
// Bay5 keeps the behaviour it shipped with (all six rows, legacy switch, plain swap), AirPit gets its own switch,
// its four names and the extended handling, and nothing else (Equator included) ever substitutes a shader.
// The allow-list itself is per-map data: ArenaMapDescriptor.ShaderFallbackNames, mirrored by shader_fallback_names
// in tools/maps/<key>.json (checked below and by validate_map_descriptors.py).
public sealed class ShaderStandinPolicyTests
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
    public void TheAllowListsMirrorTheSidecarsAndOnlyBay5AirPitBowlAndAutoServiceHaveOne()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(map.Key.Replace("arena_", "") + ".json")));
            var hasKey = doc.RootElement.TryGetProperty("shader_fallback_names", out var names);
            var expected = hasKey ? names.EnumerateArray().Select(e => e.GetString()).ToArray() : new string[0];
            Assert.Equal(expected, map.ShaderFallbackNames);
            if (map == ArenaMapRegistry.Yard) continue;   // pending until PLAN-YARD item 15: YardListIsPendingOrTheDecidedSix pins it
            Assert.Equal(map == ArenaMapRegistry.Bay5 || map == ArenaMapRegistry.AirPit || map == ArenaMapRegistry.Bowl || map == ArenaMapRegistry.AutoService,
                map.ShaderFallbackNames.Length > 0);
        }
        Assert.Equal(ShippedRows.Select(r => r.Original).Order(), ArenaMapRegistry.Bay5.ShaderFallbackNames.Order());
        Assert.Equal(AirPitNames.Order(), ArenaMapRegistry.AirPit.ShaderFallbackNames.Order());
        Assert.Equal(BowlNames, ArenaMapRegistry.Bowl.ShaderFallbackNames);
        Assert.Equal(AutoServiceNames, ArenaMapRegistry.AutoService.ShaderFallbackNames);
        // Every listed name has a substitute row for its map, and the policy reads the same list.
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            Assert.Equal(map.ShaderFallbackNames, ShaderStandinPolicy.AllowedNames(map.Key));
            foreach (var name in map.ShaderFallbackNames)
                Assert.True(ShaderStandinPolicy.TryGetFallback(map.Key, name, out _), $"{map.Key} {name}");
        }
        Assert.Empty(ShaderStandinPolicy.AllowedNames("unknown_map"));
        Assert.Empty(ShaderStandinPolicy.AllowedNames(""));
        Assert.Empty(ShaderStandinPolicy.AllowedNames(null));
    }

    // 2026-10-12 host finding: EFT 0.16.9.5 carries Custom/Billboard_FogSheet_Simple itself, so after the first
    // rebind of a process the cached fog-sheet materials carry the REAL name, not the stand-in prefix; Hidden
    // mode has to recognise both (and nothing else).
    [Theory]
    [InlineData("Custom/Billboard_FogSheet_Simple", true)]
    [InlineData("Hidden/ArenaMapPort/Custom/Billboard_FogSheet_Simple", true)]
    [InlineData("Unlit/Transparent Colored", false)]
    [InlineData("Hidden/ArenaMapPort/Cloth/ClothShader", false)]
    [InlineData("Custom/Billboard_FogSheet", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FogSheetMaterialsAreRecognisedByTheRealAndTheStandinShaderName(string shaderName, bool expected)
    {
        Assert.Equal(expected, ShaderStandinPolicy.IsFogSheetShaderName(shaderName, "Hidden/ArenaMapPort/"));
    }

    // 2026-10-12 user report (hall glass near-black and opaque): the sidecar records the stand-in's Geometry queue
    // for every source material that used the shader default, and the rebind forced it onto the transparent shader.
    // AirPit restores the shader default in that case; explicit queues and the other maps are untouched.
    [Theory]
    [InlineData("arena_airpit", 2000, 3000, -1)]   // transparent glass recorded at the stand-in default -> shader default
    [InlineData("arena_airpit", 2000, 2000, 2000)] // opaque shader: 2000 is also its default, keep
    [InlineData("arena_airpit", 3000, 3000, 3000)] // explicit transparent queue, keep
    [InlineData("arena_airpit", 2004, 2000, 2004)] // explicit decal offset, keep
    [InlineData("arena_equator", 2000, 3000, 2000)] // other maps keep the accepted behaviour
    [InlineData("arena_bay5", 2000, 3000, 2000)]
    public void AirPitRestoresTheShaderDefaultQueueWhereTheSidecarCarriesTheStandinDefault(string mapKey, int recorded, int shaderDefault, int expected)
    {
        Assert.Equal(expected, ShaderStandinPolicy.EffectiveRenderQueue(mapKey, recorded, shaderDefault));
    }

    // The six rows exactly as they shipped for Bay5 (docs/progress/shader-compat-and-inspector.md section 3).
    static readonly (string Original, string Fallback)[] ShippedRows =
    {
        ("Custom/WorldTriplanarFresnelPro", "p0/Reflective/Bumped Specular SMap"),
        ("ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)", "MK4/Rock"),
        ("Cloth/ClothShader", "p0/Cutout/Bumped Diffuse"),
        ("Cloth/ClothShader_backface", "p0/Cutout/Bumped Diffuse"),
        ("Custom/Billboard_FogSheet_Simple", "Unlit/Transparent Colored"),
        ("p0/Reflective/Bumped Specular SMap Baked", "p0/Reflective/Bumped Specular SMap"),
    };

    static readonly string[] AirPitNames =
    {
        "Custom/Billboard_FogSheet_Simple", "Cloth/ClothShader", "Cloth/ClothShader_backface",
        "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)",
    };

    // Bowl (PLAN-BOWL item 10): the tarp tunnel's two-sided reflective shader, substituted by the Cull Off cutout.
    static readonly string[] BowlNames = { "p0/Reflective/Bumped Specular_TwoSided" };

    // AutoService (PLAN-AUTOSERVICE item 12): the fog sheet, both cloth shaders and the Baked reflective shader, its own switch.
    static readonly string[] AutoServiceNames =
    {
        "Custom/Billboard_FogSheet_Simple", "Cloth/ClothShader", "Cloth/ClothShader_backface", "p0/Reflective/Bumped Specular SMap Baked",
    };

    // Yard (PLAN-YARD item 13, 2026-11-20): the four of AutoService plus the two names EFT carries nowhere (the SpeedTree 7 leaf shader and the Amplify
    // trunk shader). The descriptor list and the sidecar row are item 15's; the policy rows below are decided now and pinned here.
    static readonly string[] YardNames =
    {
        "Custom/Billboard_FogSheet_Simple", "Cloth/ClothShader", "Cloth/ClothShader_backface", "p0/Reflective/Bumped Specular SMap Baked",
        "Nature/SpeedTree", "Impostors/Examples/StandardCrossfade",
    };

    // The maps that have decided shader rows of their own or are Yard (Catalog-only, pending): every OTHER map is never active and never substitutes.
    static IEnumerable<string> OtherMapKeys() =>
        ArenaMapRegistry.Catalog.Select(m => m.Key)
            .Where(k => k != ArenaMapRegistry.Bay5.Key && k != ArenaMapRegistry.AirPit.Key && k != ArenaMapRegistry.Bowl.Key &&
                        k != ArenaMapRegistry.AutoService.Key && k != ArenaMapRegistry.Yard.Key);

    [Fact]
    public void YardListIsPendingOrTheDecidedSixAndItsRowsAreDecided()
    {
        // The descriptor list (item 15) is either still empty (Catalog-only) or exactly the decided six, in this order.
        var listed = ArenaMapRegistry.Yard.ShaderFallbackNames;
        Assert.True(listed.Length == 0 || listed.SequenceEqual(YardNames), string.Join(", ", listed));
        // The policy rows do not depend on the list being present: with the list empty TryGetFallback is false (allow-list first), so nothing is reached;
        // the rows are read through the allow-list only when item 15 has written it, so they are asserted via the targets of the names the list will hold.
        var yard = ArenaMapRegistry.Yard.Key;
        if (listed.Length == 0)
        {
            foreach (var name in YardNames) Assert.False(ShaderStandinPolicy.TryGetFallback(yard, name, out _), name);
            Assert.False(ShaderStandinPolicy.UsesFallbackTable(yard));
            return;
        }
        Assert.Equal("Unlit/Transparent Colored", Target(yard, "Custom/Billboard_FogSheet_Simple"));
        Assert.Equal("p0/Cutout/Bumped Diffuse", Target(yard, "Cloth/ClothShader"));
        Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, Target(yard, "Cloth/ClothShader_backface"));
        Assert.Equal("p0/Reflective/Bumped Specular SMap", Target(yard, "p0/Reflective/Bumped Specular SMap Baked"));
        Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, Target(yard, "Nature/SpeedTree"));
        Assert.Equal("Diffuse", Target(yard, "Impostors/Examples/StandardCrossfade"));
    }

    [Fact]
    public void YardTargetRowsArePinnedBeforeThePromotion()
    {
        var t = ShaderStandinPolicy.YardFallbackTable;
        Assert.Equal(3, t.Count);
        Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, t["Cloth/ClothShader_backface"]);
        Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, t["Nature/SpeedTree"]);
        Assert.Equal("Diffuse", t["Impostors/Examples/StandardCrossfade"]);
    }

    [Fact]
    public void YardFollowsOnlyItsOwnSwitchAndTakesTheClothFixButNotTheRockFix()
    {
        var yard = ArenaMapRegistry.Yard.Key;
        Assert.True(ShaderStandinPolicy.YardStandinsDefault);
        Assert.True(ShaderStandinPolicy.FallbacksActive(yard, false, false, false, false, yardSwitch: true));
        Assert.True(ShaderStandinPolicy.FallbacksActive(yard, false, false, false, autoServiceSwitch: false, yardSwitch: true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(yard, true, true, true, true, yardSwitch: false));
        Assert.False(ShaderStandinPolicy.FallbacksActive(yard, true, true, true, true));          // the sixth parameter defaults to off: a caller must pass it
        // Neither switch of another map turns Yard on, and Yard's switch turns no other map on (the NoOther... tests below cover the rest of the grid).
        Assert.True(ShaderStandinPolicy.UsesExtendedStandins(yard));
        foreach (var name in new[] { "Cloth/ClothShader", "Cloth/ClothShader_backface" })
        {
            var fix = ShaderStandinPolicy.FixFor(yard, name);
            Assert.Equal("_BumpMap", Assert.Single(fix.Textures).To);
            Assert.Equal("_Cutoff", Assert.Single(fix.Floats).Name);
            Assert.Equal(0f, fix.Floats[0].Value);
        }
        // The two new names have NO fix: the leaf cutoff stays the material's 0.5 (zeroing it would draw every leaf card as a full quad) and the
        // bark takes plain Diffuse; the fog sheet and the Baked reflective get nothing either (FixFor is name-based, so the rock fix would answer for
        // the rock name on any extended map; the allow-list keeps that name away from Yard, as it does for AutoService).
        foreach (var name in new[] { "Nature/SpeedTree", "Impostors/Examples/StandardCrossfade", "Custom/Billboard_FogSheet_Simple",
                     "p0/Reflective/Bumped Specular SMap Baked" })
        {
            var fix = ShaderStandinPolicy.FixFor(yard, name);
            Assert.Empty(fix.Textures);
            Assert.Empty(fix.Floats);
        }
        Assert.False(ShaderStandinPolicy.HidesFogSheets(yard, FogSheetMode.Hidden));
    }

    // PLAN-YARD item 13, the pin that no released map changed: a golden table of what the policy answers for each of the six released maps over every
    // name that matters (the six shipped table names, Bowl's tarp shader, Yard's two new names and the names that resolve in EFT without a row). The
    // expectations are spelled out here by the pre-Yard rules, independently of ShaderStandinPolicy's own tables, so a Yard row that leaks into a released
    // map (a shared dictionary, an IsYard test in a wrong place, Yard's extra names in another map's allow-list) fails here.
    static readonly string[] CensusNames =
    {
        "Custom/WorldTriplanarFresnelPro", "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)", "Cloth/ClothShader", "Cloth/ClothShader_backface",
        "Custom/Billboard_FogSheet_Simple", "p0/Reflective/Bumped Specular SMap Baked", "p0/Reflective/Bumped Specular_TwoSided",
        "Nature/SpeedTree", "Impostors/Examples/StandardCrossfade", "Diffuse", "Nature/SpeedTree8", "Nature/SpeedTreeEFT",
        "Hidden/Amplify Impostors/Octahedron Impostor", "Custom/WireNetting", "p0/Reflective/Bumped Specular SMap", "Standard", "",
    };

    static string ExpectedTarget(string mapKey, string name)
    {
        string shipped = name switch
        {
            "Custom/WorldTriplanarFresnelPro" => "p0/Reflective/Bumped Specular SMap",
            "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)" => "MK4/Rock",
            "Cloth/ClothShader" => "p0/Cutout/Bumped Diffuse",
            "Cloth/ClothShader_backface" => "p0/Cutout/Bumped Diffuse",
            "Custom/Billboard_FogSheet_Simple" => "Unlit/Transparent Colored",
            "p0/Reflective/Bumped Specular SMap Baked" => "p0/Reflective/Bumped Specular SMap",
            _ => null,
        };
        const string twoSided = "Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side";
        if (mapKey == ArenaMapRegistry.Bay5.Key) return shipped;                                              // all six rows, the single-sided backface
        if (mapKey == ArenaMapRegistry.AirPit.Key)
        {
            if (name == "Cloth/ClothShader_backface") return twoSided;
            return name is "Custom/Billboard_FogSheet_Simple" or "Cloth/ClothShader" or "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)" ? shipped : null;
        }
        if (mapKey == ArenaMapRegistry.Bowl.Key) return name == "p0/Reflective/Bumped Specular_TwoSided" ? twoSided : null;
        if (mapKey == ArenaMapRegistry.AutoService.Key)
        {
            if (name == "Cloth/ClothShader_backface") return twoSided;
            return name is "Custom/Billboard_FogSheet_Simple" or "Cloth/ClothShader" or "p0/Reflective/Bumped Specular SMap Baked" ? shipped : null;
        }
        return null;                                                                                          // Equator and Iceberg: no fallback table
    }

    static bool ExpectedExtended(string mapKey) => mapKey == ArenaMapRegistry.AirPit.Key || mapKey == ArenaMapRegistry.AutoService.Key;

    [Fact]
    public void NoReleasedMapsFallbackActiveTargetOrFixChangedWithYardsRows()
    {
        var released = ArenaMapRegistry.All.Select(m => m.Key).ToArray();
        Assert.Equal(6, released.Length);
        foreach (var key in released)
        {
            Assert.True(ExpectedExtended(key) == ShaderStandinPolicy.UsesExtendedStandins(key), key);
            foreach (var name in CensusNames)
            {
                var expected = ExpectedTarget(key, name);
                Assert.True((expected != null) == ShaderStandinPolicy.TryGetFallback(key, name, out var got), $"{key} {name}");
                Assert.Equal(expected ?? string.Empty, got);
                // FixFor: the cloth fix on the two cloth names of an extended map, the rock fix on the rock name of an extended map, nothing else, nowhere.
                var fix = ShaderStandinPolicy.FixFor(key, name);
                bool cloth = ExpectedExtended(key) && (name is "Cloth/ClothShader" or "Cloth/ClothShader_backface");
                bool rock = ExpectedExtended(key) && name == ShaderStandinPolicy.RockShader;   // FixFor is name-based: the allow-list, not FixFor, keeps the rock off AutoService
                Assert.True((cloth ? 1 : rock ? 2 : 0) == fix.Textures.Length, $"{key} {name} textures");
                Assert.True((cloth ? 1 : 0) == fix.Floats.Length, $"{key} {name} floats");
            }
            // FallbacksActive over the whole switch grid, Yard's switch included: Yard's flag never reaches a released map.
            foreach (var legacy in new[] { false, true })
                foreach (var airPit in new[] { false, true })
                    foreach (var bowl in new[] { false, true })
                        foreach (var autoService in new[] { false, true })
                            foreach (var yardSwitch in new[] { false, true })
                            {
                                bool expectedActive = key == ArenaMapRegistry.Bay5.Key ? legacy : key == ArenaMapRegistry.AirPit.Key ? airPit :
                                    key == ArenaMapRegistry.Bowl.Key ? bowl : key == ArenaMapRegistry.AutoService.Key && autoService;
                                Assert.True(expectedActive == ShaderStandinPolicy.FallbacksActive(key, legacy, airPit, bowl, autoService, yardSwitch),
                                    $"{key} {legacy} {airPit} {bowl} {autoService} {yardSwitch}");
                                Assert.True(expectedActive == ShaderStandinPolicy.FallbacksActive(key, legacy, airPit, bowl, autoService), $"{key} (five arguments)");
                            }
        }
        // The shared table and every released allow-list are byte-for-byte what they were: six rows, none for Yard's two extra names.
        Assert.Equal(6, ShaderStandinPolicy.FallbackTable.Count);
        Assert.False(ShaderStandinPolicy.FallbackTable.ContainsKey("Nature/SpeedTree"));
        Assert.False(ShaderStandinPolicy.FallbackTable.ContainsKey("Impostors/Examples/StandardCrossfade"));
        foreach (var map in ArenaMapRegistry.All)
        {
            Assert.DoesNotContain("Nature/SpeedTree", map.ShaderFallbackNames);
            Assert.DoesNotContain("Impostors/Examples/StandardCrossfade", map.ShaderFallbackNames);
        }
    }

    [Fact]
    public void OnlyBay5AndAirPitConsultTheFallbackTable()
    {
        Assert.True(ShaderStandinPolicy.UsesFallbackTable(ArenaMapRegistry.Bay5.Key));
        Assert.True(ShaderStandinPolicy.UsesFallbackTable(ArenaMapRegistry.AirPit.Key));
        Assert.True(ShaderStandinPolicy.UsesFallbackTable(ArenaMapRegistry.Bowl.Key));
        Assert.True(ShaderStandinPolicy.UsesFallbackTable(ArenaMapRegistry.AutoService.Key));
        Assert.False(ShaderStandinPolicy.UsesFallbackTable(ArenaMapRegistry.Equator.Key));
        foreach (var key in OtherMapKeys())
            Assert.False(ShaderStandinPolicy.UsesFallbackTable(key), key);
        Assert.False(ShaderStandinPolicy.UsesFallbackTable(""));
        Assert.False(ShaderStandinPolicy.UsesFallbackTable(null));
    }

    [Fact]
    public void Bay5FollowsOnlyTheLegacySwitch()
    {
        var bay5 = ArenaMapRegistry.Bay5.Key;
        Assert.True(ShaderStandinPolicy.FallbacksActive(bay5, bay5LegacySwitch: true, airPitSwitch: false, bowlSwitch: false));
        Assert.True(ShaderStandinPolicy.FallbacksActive(bay5, true, true, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(bay5, false, true, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(bay5, false, false, false));
    }

    [Fact]
    public void AirPitFollowsOnlyItsOwnSwitchAndNotTheLegacyOne()
    {
        var airPit = ArenaMapRegistry.AirPit.Key;
        Assert.True(ShaderStandinPolicy.FallbacksActive(airPit, bay5LegacySwitch: false, airPitSwitch: true, bowlSwitch: false));
        Assert.True(ShaderStandinPolicy.FallbacksActive(airPit, true, true, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(airPit, true, false, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(airPit, false, false, false));
    }

    [Fact]
    public void BowlFollowsOnlyItsOwnSwitchAndGetsTheTwoSidedCutoutRow()
    {
        var bowl = ArenaMapRegistry.Bowl.Key;
        Assert.True(ShaderStandinPolicy.BowlStandinsDefault);
        Assert.True(ShaderStandinPolicy.FallbacksActive(bowl, bay5LegacySwitch: false, airPitSwitch: false, bowlSwitch: true));
        Assert.True(ShaderStandinPolicy.FallbacksActive(bowl, true, true, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(bowl, true, true, false));
        Assert.False(ShaderStandinPolicy.FallbacksActive(bowl, false, false, false));
        Assert.True(ShaderStandinPolicy.TryGetFallback(bowl, ShaderStandinPolicy.TwoSidedReflectiveShader, out var got));
        Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, got);
        // The row is Bowl's alone: the shipped table stays the six Bay5 rows and the other allow-lists do not name it.
        Assert.False(ShaderStandinPolicy.FallbackTable.ContainsKey(ShaderStandinPolicy.TwoSidedReflectiveShader));
        Assert.False(ShaderStandinPolicy.TryGetFallback(ArenaMapRegistry.Bay5.Key, ShaderStandinPolicy.TwoSidedReflectiveShader, out _));
        Assert.False(ShaderStandinPolicy.TryGetFallback(ArenaMapRegistry.AirPit.Key, ShaderStandinPolicy.TwoSidedReflectiveShader, out _));
        // Bowl's list names nothing else, so AirPit's rows do not leak onto it.
        Assert.False(ShaderStandinPolicy.TryGetFallback(bowl, ShaderStandinPolicy.ClothShader, out _));
        Assert.False(ShaderStandinPolicy.UsesExtendedStandins(bowl));
    }

    [Fact]
    public void AutoServiceFollowsOnlyItsOwnSwitchAndItsFourNamesGetTheirRows()
    {
        var map = ArenaMapRegistry.AutoService.Key;
        Assert.True(ShaderStandinPolicy.AutoServiceStandinsDefault);
        Assert.True(ShaderStandinPolicy.FallbacksActive(map, bay5LegacySwitch: false, airPitSwitch: false, bowlSwitch: false, autoServiceSwitch: true));
        Assert.True(ShaderStandinPolicy.FallbacksActive(map, true, true, true, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(map, true, true, true, false));
        Assert.False(ShaderStandinPolicy.FallbacksActive(map, true, true, true));   // the switch defaults to off in the policy: a caller must pass it
        Assert.False(ShaderStandinPolicy.FallbacksActive(map, false, false, false, false));
        Assert.Equal(AutoServiceNames, ShaderStandinPolicy.AllowedNames(map));
        // Fog sheet, single-sided cloth and the Baked reflective keep the shipped rows.
        Assert.True(ShaderStandinPolicy.TryGetFallback(map, "Custom/Billboard_FogSheet_Simple", out var fog));
        Assert.Equal("Unlit/Transparent Colored", fog);
        Assert.True(ShaderStandinPolicy.TryGetFallback(map, "Cloth/ClothShader", out var cloth));
        Assert.Equal("p0/Cutout/Bumped Diffuse", cloth);
        Assert.True(ShaderStandinPolicy.TryGetFallback(map, "p0/Reflective/Bumped Specular SMap Baked", out var baked));
        Assert.Equal("p0/Reflective/Bumped Specular SMap", baked);
        // The backface cloth draws from both sides, as on AirPit; the shipped table row (Cull Back) stays Bay5's.
        Assert.True(ShaderStandinPolicy.TryGetFallback(map, "Cloth/ClothShader_backface", out var backface));
        Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, backface);
        Assert.Equal("p0/Cutout/Bumped Diffuse", ShaderStandinPolicy.FallbackTable["Cloth/ClothShader_backface"]);
        // Nothing outside its list: the rock shader and Bowl's tarp shader are not allowed on it.
        Assert.False(ShaderStandinPolicy.TryGetFallback(map, ShaderStandinPolicy.RockShader, out _));
        Assert.False(ShaderStandinPolicy.TryGetFallback(map, ShaderStandinPolicy.TwoSidedReflectiveShader, out _));
        // Its rows are its own: the other maps' backface result is unchanged.
        Assert.Equal("p0/Cutout/Bumped Diffuse", Target(ArenaMapRegistry.Bay5.Key, "Cloth/ClothShader_backface"));
        Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, Target(ArenaMapRegistry.AirPit.Key, "Cloth/ClothShader_backface"));
    }

    static string Target(string key, string name) => ShaderStandinPolicy.TryGetFallback(key, name, out var t) ? t : null;

    [Fact]
    public void AutoServiceTakesTheClothFixButNotTheRockFix()
    {
        var map = ArenaMapRegistry.AutoService.Key;
        Assert.True(ShaderStandinPolicy.UsesExtendedStandins(map));
        foreach (var name in new[] { "Cloth/ClothShader", "Cloth/ClothShader_backface" })
        {
            var fix = ShaderStandinPolicy.FixFor(map, name);
            Assert.Equal("_BumpMap", Assert.Single(fix.Textures).To);
            Assert.Equal("_Cutoff", Assert.Single(fix.Floats).Name);
            Assert.Equal(0f, fix.Floats[0].Value);
        }
        Assert.Empty(ShaderStandinPolicy.FixFor(map, "Custom/Billboard_FogSheet_Simple").Textures);
        Assert.Empty(ShaderStandinPolicy.FixFor(map, "p0/Reflective/Bumped Specular SMap Baked").Floats);
        // Fog sheets are never hidden outside AirPit.
        Assert.False(ShaderStandinPolicy.HidesFogSheets(map, FogSheetMode.Hidden));
        // Like Bowl's, its magenta result with the switch off is a real degradation (Error line), not AirPit's deliberate A/B gap.
        foreach (var name in AutoServiceNames)
            Assert.False(ShaderStandinPolicy.IsKnownGap(map, name, airPitSwitchOn: false), name);
    }

    [Fact]
    public void NoOtherMapIsEverActiveWhateverTheSwitches()
    {
        var keys = OtherMapKeys().Append(ArenaMapRegistry.Equator.Key).Append("").Append(null);
        foreach (var key in keys)
            foreach (var legacy in new[] { false, true })
                foreach (var airPit in new[] { false, true })
                    foreach (var bowl in new[] { false, true })
                        foreach (var autoService in new[] { false, true })
                            Assert.False(ShaderStandinPolicy.FallbacksActive(key, legacy, airPit, bowl, autoService), $"{key} {legacy} {airPit} {bowl} {autoService}");
    }

    [Fact]
    public void AirPitDefaultsToTheShaderSubstitutesWithTheFogSheetStandIn()
    {
        Assert.True(ShaderStandinPolicy.AirPitStandinsDefault);
        Assert.Equal(FogSheetMode.StandIn, ShaderStandinPolicy.FogSheetModeDefault);
        Assert.Equal(1f, ShaderStandinPolicy.FogSheetOpacityDefault);
        Assert.InRange(ShaderStandinPolicy.FogSheetOpacityDefault,
            ShaderStandinPolicy.FogSheetOpacityMin, ShaderStandinPolicy.FogSheetOpacityMax);
    }

    [Fact]
    public void Bay5KeepsTheSixShippedRowsExactly()
    {
        var bay5 = ArenaMapRegistry.Bay5.Key;
        Assert.Equal(ShippedRows.Length, ShaderStandinPolicy.FallbackTable.Count);
        foreach (var (original, fallback) in ShippedRows)
        {
            Assert.True(ShaderStandinPolicy.TryGetFallback(bay5, original, out var got), original);
            Assert.Equal(fallback, got);
            Assert.Equal(fallback, ShaderStandinPolicy.FallbackTable[original]);
        }
        // SpeedTree8 deliberately has no row (a wrong-looking tree is worse than a magenta one).
        Assert.False(ShaderStandinPolicy.TryGetFallback(bay5, "Nature/SpeedTree8", out _));
        Assert.False(ShaderStandinPolicy.TryGetFallback(bay5, "", out _));
        Assert.False(ShaderStandinPolicy.TryGetFallback(bay5, null, out _));
    }

    [Fact]
    public void AirPitMayOnlySubstituteItsFourNamesWithTheRowsBay5ShipsExceptTheBackfaceCloth()
    {
        var airPit = ArenaMapRegistry.AirPit.Key;
        foreach (var (original, fallback) in ShippedRows)
        {
            bool allowed = AirPitNames.Contains(original);
            Assert.Equal(allowed, ShaderStandinPolicy.TryGetFallback(airPit, original, out var got));
            if (!allowed) Assert.Equal(string.Empty, got);
            else if (original == "Cloth/ClothShader_backface") Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, got);
            else Assert.Equal(fallback, got);
        }
        // The three targets named by the plan.
        ShaderStandinPolicy.TryGetFallback(airPit, "Custom/Billboard_FogSheet_Simple", out var fog);
        ShaderStandinPolicy.TryGetFallback(airPit, "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)", out var rock);
        ShaderStandinPolicy.TryGetFallback(airPit, "Cloth/ClothShader", out var cloth);
        Assert.Equal("Unlit/Transparent Colored", fog);
        Assert.Equal("MK4/Rock", rock);
        Assert.Equal("p0/Cutout/Bumped Diffuse", cloth);
    }

    [Fact]
    public void OnlyAirPitsBackfaceClothUsesTheDoubleSidedSubstitute()
    {
        // p0/Cutout/Bumped Diffuse is Cull Back in all three passes with no _Cull property (read-only census of the
        // SPT shaders bundle, 2026-10-12), so the backface cloth would vanish from behind. The legacy "2 side" shader
        // is Cull Off in all three passes and declares _Color, _MainTex, _BumpMap and _Cutoff.
        Assert.Equal("Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side", ShaderStandinPolicy.TwoSidedClothShader);
        Assert.True(ShaderStandinPolicy.TryGetFallback(ArenaMapRegistry.AirPit.Key, "Cloth/ClothShader_backface", out var airPit));
        Assert.Equal(ShaderStandinPolicy.TwoSidedClothShader, airPit);
        Assert.True(ShaderStandinPolicy.TryGetFallback(ArenaMapRegistry.AirPit.Key, "Cloth/ClothShader", out var front));
        Assert.Equal("p0/Cutout/Bumped Diffuse", front);
        // Bay5 keeps the shipped single-sided row (and the shared table row itself is untouched).
        Assert.True(ShaderStandinPolicy.TryGetFallback(ArenaMapRegistry.Bay5.Key, "Cloth/ClothShader_backface", out var bay5));
        Assert.Equal("p0/Cutout/Bumped Diffuse", bay5);
        Assert.Equal("p0/Cutout/Bumped Diffuse", ShaderStandinPolicy.FallbackTable["Cloth/ClothShader_backface"]);
        // The slot copy and the cutoff override apply to the double-sided substitute too (it declares both slots).
        Assert.Equal("_Cutoff", Assert.Single(ShaderStandinPolicy.FixFor(ArenaMapRegistry.AirPit.Key, "Cloth/ClothShader_backface").Floats).Name);
        Assert.Equal("_BumpMap", Assert.Single(ShaderStandinPolicy.FixFor(ArenaMapRegistry.AirPit.Key, "Cloth/ClothShader_backface").Textures).To);
    }

    [Fact]
    public void EquatorAndTheParkedMapsGetNoSubstituteForAnyName()
    {
        var keys = OtherMapKeys().Append(ArenaMapRegistry.Equator.Key);
        foreach (var key in keys)
            foreach (var (original, _) in ShippedRows)
                Assert.False(ShaderStandinPolicy.TryGetFallback(key, original, out _), $"{key} {original}");
    }

    [Fact]
    public void TheExtendedHandlingIsAirPitAndAutoServiceOnly()
    {
        Assert.True(ShaderStandinPolicy.UsesExtendedStandins(ArenaMapRegistry.AirPit.Key));
        Assert.True(ShaderStandinPolicy.UsesExtendedStandins(ArenaMapRegistry.AutoService.Key));
        Assert.False(ShaderStandinPolicy.UsesExtendedStandins(ArenaMapRegistry.Bay5.Key));
        Assert.False(ShaderStandinPolicy.UsesExtendedStandins(ArenaMapRegistry.Equator.Key));
        foreach (var key in OtherMapKeys())
            Assert.False(ShaderStandinPolicy.UsesExtendedStandins(key), key);

        foreach (var key in OtherMapKeys().Append(ArenaMapRegistry.Bay5.Key).Append(ArenaMapRegistry.Equator.Key))
            foreach (var (original, _) in ShippedRows)
            {
                var fix = ShaderStandinPolicy.FixFor(key, original);
                Assert.Empty(fix.Textures);
                Assert.Empty(fix.Floats);
            }
    }

    [Fact]
    public void AirPitRockAndClothFixesCopyTheSlotsTheSubstituteReads()
    {
        var airPit = ArenaMapRegistry.AirPit.Key;
        var rock = ShaderStandinPolicy.FixFor(airPit, "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)");
        Assert.Equal(new[] { "_BaseAlbedoASmoothness>_Aldebo", "_BaseNormalMap>_Normalmap" },
            rock.Textures.Select(t => t.From + ">" + t.To));
        Assert.Empty(rock.Floats);

        foreach (var name in new[] { "Cloth/ClothShader", "Cloth/ClothShader_backface" })
        {
            var cloth = ShaderStandinPolicy.FixFor(airPit, name);
            var remap = Assert.Single(cloth.Textures);
            Assert.Equal(("_NormalMap1", "_BumpMap"), (remap.From, remap.To));
            // The cloth albedo alpha carries gloss; the material's own 0.5..0.67 cutoff would discard every texel.
            var cutoff = Assert.Single(cloth.Floats);
            Assert.Equal(("_Cutoff", 0f), (cutoff.Name, cutoff.Value));
        }

        // The fog sheet needs a generated texture, not a slot copy; unknown names get nothing.
        Assert.Empty(ShaderStandinPolicy.FixFor(airPit, "Custom/Billboard_FogSheet_Simple").Textures);
        Assert.Empty(ShaderStandinPolicy.FixFor(airPit, "Standard").Textures);
        Assert.Empty(ShaderStandinPolicy.FixFor(airPit, null).Floats);
    }

    [Fact]
    public void OnlyAirPitInHiddenModeHidesTheFogSheets()
    {
        Assert.True(ShaderStandinPolicy.HidesFogSheets(ArenaMapRegistry.AirPit.Key, FogSheetMode.Hidden));
        Assert.False(ShaderStandinPolicy.HidesFogSheets(ArenaMapRegistry.AirPit.Key, FogSheetMode.StandIn));
        foreach (var key in OtherMapKeys().Append(ArenaMapRegistry.Bay5.Key).Append(ArenaMapRegistry.Equator.Key))
            foreach (var mode in new[] { FogSheetMode.StandIn, FogSheetMode.Hidden })
                Assert.False(ShaderStandinPolicy.HidesFogSheets(key, mode), $"{key} {mode}");
    }

    [Fact]
    public void FogPeakAlphaScalesWithTheAuthoredOpacityAndTheUserKeyAndClamps()
    {
        // The authored AirPit sheets: _Color.a 0.949, _OpacityIntensity 0.1.
        Assert.Equal(0.949f * 0.1f * 2f, ShaderStandinPolicy.FogSheetPeakAlpha(0.949f, 0.1f, 1f), 5);
        Assert.Equal(0f, ShaderStandinPolicy.FogSheetPeakAlpha(0.949f, 0.1f, 0f));
        Assert.Equal(4 * ShaderStandinPolicy.FogSheetPeakAlpha(0.949f, 0.1f, 1f),
            ShaderStandinPolicy.FogSheetPeakAlpha(0.949f, 0.1f, 4f), 5);
        Assert.Equal(1f, ShaderStandinPolicy.FogSheetPeakAlpha(1f, 3f, 4f));
        Assert.Equal(0f, ShaderStandinPolicy.FogSheetPeakAlpha(float.NaN, 0.1f, 1f));
        Assert.Equal(0f, ShaderStandinPolicy.FogSheetPeakAlpha(-1f, 0.1f, 1f));
    }

    [Fact]
    public void FogPixelsAreAColouredSoftDisc()
    {
        const int size = ShaderStandinPolicy.FogTextureSize;
        var px = ShaderStandinPolicy.BuildFogSheetPixels(size, 0.652f, 0.909f, 1f, 0.2f);
        Assert.Equal(size * size * 4, px.Length);
        byte R(int x, int y) => px[(y * size + x) * 4];
        byte G(int x, int y) => px[(y * size + x) * 4 + 1];
        byte B(int x, int y) => px[(y * size + x) * 4 + 2];
        byte A(int x, int y) => px[(y * size + x) * 4 + 3];

        // Constant colour everywhere (straight alpha, so the transparent rim does not tint the blend).
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.Equal(166, px[i]); Assert.Equal(232, px[i + 1]); Assert.Equal(255, px[i + 2]);
        }
        Assert.Equal((166, 232, 255), (R(10, 3), G(10, 3), B(10, 3)));

        // The disc peaks at the centre (0.2 * 255 = 51), is gone at the corners and the rim midpoints.
        Assert.InRange(A(size / 2, size / 2), 49, 51);
        Assert.Equal(0, A(0, 0));
        Assert.Equal(0, A(size - 1, size - 1));
        Assert.Equal(0, A(0, size - 1));
        Assert.True(A(0, size / 2) <= 1);
        // Monotone from the rim to the centre along a row.
        for (int x = 1; x <= size / 2; x++)
            Assert.True(A(x, size / 2) >= A(x - 1, size / 2), $"x={x}");
        // Symmetric.
        Assert.Equal(A(10, 20), A(size - 1 - 10, 20));
        Assert.Equal(A(10, 20), A(10, size - 1 - 20));
    }

    [Fact]
    public void ZeroPeakGivesAnInvisibleTexture()
    {
        var px = ShaderStandinPolicy.BuildFogSheetPixels(8, 1f, 1f, 1f, 0f);
        for (int i = 3; i < px.Length; i += 4) Assert.Equal(0, px[i]);
    }

    [Fact]
    public void FogFalloffIsOneAtTheCentreAndZeroOnAndOutsideTheCircle()
    {
        Assert.Equal(1f, ShaderStandinPolicy.FogFalloff(0f, 0f));
        Assert.Equal(0f, ShaderStandinPolicy.FogFalloff(1f, 0f));
        Assert.Equal(0f, ShaderStandinPolicy.FogFalloff(0.8f, 0.8f));
        Assert.Equal(0f, ShaderStandinPolicy.FogFalloff(-2f, 0.1f));
        Assert.True(ShaderStandinPolicy.FogFalloff(0.3f, 0f) > ShaderStandinPolicy.FogFalloff(0.6f, 0f));
    }

    [Fact]
    public void OnlyAirPitsFourNamesWithTheSwitchOffAreADeliberateGap()
    {
        var airPit = ArenaMapRegistry.AirPit.Key;
        // The A/B baseline: the developer switched the substitutes off, so the four names staying magenta is intended.
        foreach (var name in AirPitNames)
        {
            Assert.True(ShaderStandinPolicy.IsKnownGap(airPit, name, airPitSwitchOn: false), name);
            // With the switch on, an unresolved substitute is a real failure and keeps its Error line.
            Assert.False(ShaderStandinPolicy.IsKnownGap(airPit, name, airPitSwitchOn: true), name);
        }
        // AirPit names outside its allow-list are never a deliberate gap, even with the switch off.
        foreach (var name in new[] { "Custom/WorldTriplanarFresnelPro", "p0/Reflective/Bumped Specular SMap Baked",
                     "Nature/SpeedTree8", "Nature/SpeedTreeEFT", "p0/Reflective/Bumped Specular SMap", "" , null })
            Assert.False(ShaderStandinPolicy.IsKnownGap(airPit, name, airPitSwitchOn: false), name);
    }

    [Fact]
    public void EquatorBay5AndTheParkedMapsKeepTheirErrorLinesWhateverTheAirPitSwitch()
    {
        // Their unresolved names were Error lines before this item and stay so: the switch belongs to AirPit only.
        var keys = OtherMapKeys().Append(ArenaMapRegistry.Equator.Key).Append(ArenaMapRegistry.Bay5.Key).Append("").Append(null);
        foreach (var key in keys)
            foreach (var switchOn in new[] { false, true })
                foreach (var (original, _) in ShippedRows)
                    Assert.False(ShaderStandinPolicy.IsKnownGap(key, original, switchOn), $"{key} {original} {switchOn}");
    }

    [Fact]
    public void UnresolvedSummaryIsOneSortedLineWithoutDuplicates()
    {
        var line = ShaderStandinPolicy.FormatUnresolvedSummary("arena_airpit",
            new[] { "Cloth/ClothShader", "Custom/Billboard_FogSheet_Simple", "Cloth/ClothShader", "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)" });
        Assert.DoesNotContain('\n', line);
        Assert.Equal("arena_airpit: 3 shader name(s) without an SPT shader keep the flat stand-in: " +
                     "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy), Cloth/ClothShader, Custom/Billboard_FogSheet_Simple", line);
    }
}
