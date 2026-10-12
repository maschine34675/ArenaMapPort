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
    public void TheAllowListsMirrorTheSidecarsAndOnlyBay5AndAirPitHaveOne()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(map.Key.Replace("arena_", "") + ".json")));
            var hasKey = doc.RootElement.TryGetProperty("shader_fallback_names", out var names);
            var expected = hasKey ? names.EnumerateArray().Select(e => e.GetString()).ToArray() : new string[0];
            Assert.Equal(expected, map.ShaderFallbackNames);
            Assert.Equal(map == ArenaMapRegistry.Bay5 || map == ArenaMapRegistry.AirPit, map.ShaderFallbackNames.Length > 0);
        }
        Assert.Equal(ShippedRows.Select(r => r.Original).Order(), ArenaMapRegistry.Bay5.ShaderFallbackNames.Order());
        Assert.Equal(AirPitNames.Order(), ArenaMapRegistry.AirPit.ShaderFallbackNames.Order());
        // Every listed name has a substitute row, and the policy reads the same list.
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            Assert.Equal(map.ShaderFallbackNames, ShaderStandinPolicy.AllowedNames(map.Key));
            foreach (var name in map.ShaderFallbackNames)
                Assert.True(ShaderStandinPolicy.FallbackTable.ContainsKey(name), $"{map.Key} {name}");
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

    static IEnumerable<string> OtherMapKeys() =>
        ArenaMapRegistry.Catalog.Select(m => m.Key)
            .Where(k => k != ArenaMapRegistry.Bay5.Key && k != ArenaMapRegistry.AirPit.Key);

    [Fact]
    public void OnlyBay5AndAirPitConsultTheFallbackTable()
    {
        Assert.True(ShaderStandinPolicy.UsesFallbackTable(ArenaMapRegistry.Bay5.Key));
        Assert.True(ShaderStandinPolicy.UsesFallbackTable(ArenaMapRegistry.AirPit.Key));
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
        Assert.True(ShaderStandinPolicy.FallbacksActive(bay5, bay5LegacySwitch: true, airPitSwitch: false));
        Assert.True(ShaderStandinPolicy.FallbacksActive(bay5, true, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(bay5, false, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(bay5, false, false));
    }

    [Fact]
    public void AirPitFollowsOnlyItsOwnSwitchAndNotTheLegacyOne()
    {
        var airPit = ArenaMapRegistry.AirPit.Key;
        Assert.True(ShaderStandinPolicy.FallbacksActive(airPit, bay5LegacySwitch: false, airPitSwitch: true));
        Assert.True(ShaderStandinPolicy.FallbacksActive(airPit, true, true));
        Assert.False(ShaderStandinPolicy.FallbacksActive(airPit, true, false));
        Assert.False(ShaderStandinPolicy.FallbacksActive(airPit, false, false));
    }

    [Fact]
    public void NoOtherMapIsEverActiveWhateverTheSwitches()
    {
        var keys = OtherMapKeys().Append(ArenaMapRegistry.Equator.Key).Append("").Append(null);
        foreach (var key in keys)
            foreach (var legacy in new[] { false, true })
                foreach (var airPit in new[] { false, true })
                    Assert.False(ShaderStandinPolicy.FallbacksActive(key, legacy, airPit), $"{key} {legacy} {airPit}");
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
    public void TheExtendedHandlingIsAirPitOnly()
    {
        Assert.True(ShaderStandinPolicy.UsesExtendedStandins(ArenaMapRegistry.AirPit.Key));
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
