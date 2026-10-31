using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The per-map emission cap's Unity-free core (EmissionCapModel.cs, docs/progress/iceberg-release.md "Per-map emission cap"): which shader families are
/// capped through which property, the scaling math, and the ledger that makes the write idempotent and exactly reversible. The Unity glue
/// (EmissionCapRepairs.cs) is read as text by EmissionCapSourcePinTests; the numbers below are Iceberg's real materials (package iceberg-native-v2).
/// </summary>
public sealed class EmissionCapModelTests
{
    // Emission_Yellow (367e4a61931e682b): Standard, keyword _EMISSION, _EmissionColor (67.79, 17.39, 0.0): the two lobby crystal lamps.
    private const float CrystalR = 67.79352569580078f, CrystalG = 17.392053604125977f, CrystalB = 0f;

    private sealed class FakeMaterial
    {
        public string Name = "";
        public EmissionValues Values;
        public bool Destroyed;
        public int Writes;
    }

    private sealed class FakeSurface : IEmissionSurface<FakeMaterial>
    {
        public bool TryRead(FakeMaterial material, EmissionFamily family, out EmissionValues current)
        {
            current = material.Values;
            return !material.Destroyed;
        }

        public void Write(FakeMaterial material, EmissionFamily family, EmissionValues values)
        {
            material.Values = values;
            material.Writes++;
        }
    }

    private static readonly FakeSurface Surface = new();

    private static FakeMaterial Crystal() => new() { Name = "367e4a61931e682b", Values = new EmissionValues(CrystalR, CrystalG, CrystalB, 1f, 0f) };

    private static EmissionEntry CrystalEntry() => EmissionEntry.ForColor("367e4a61931e682b", CrystalR, CrystalG, CrystalB, 1f);

    // Sanatorium_Emissive_Orange (d52be16e9d5d8091): p0/Reflective/Bumped Emissive Specular SMap, _EmissionPower 10, _EmissionVisibility 1.0, _EmissiveColor (0.68, 0.58, 0.10).
    private static FakeMaterial Chandelier() => new() { Name = "d52be16e9d5d8091", Values = new EmissionValues(0f, 0f, 0f, 0f, 10f) };

    private static EmissionEntry ChandelierEntry() => EmissionEntry.ForPower("d52be16e9d5d8091", 10f, 1f, 0.68f);

    private static float Peak(EmissionValues v) => Math.Max(v.R, Math.Max(v.G, v.B));

    [Theory]
    // Standard family: only with the _EMISSION keyword and a declared _EmissionColor
    [InlineData("Standard", true, true, false, "Color")]
    [InlineData("Standard", false, true, false, "None")]
    [InlineData("Standard", true, false, false, "None")]
    [InlineData("Standard", true, true, true, "Color")]            // a leftover _EmissionPower on a Standard material does not change the family
    [InlineData("Standard (Specular setup)", true, true, false, "Color")]
    [InlineData("Standard (Roughness setup)", true, true, false, "Color")]
    // p0 emissive shaders: the power family when the shader declares _EmissionPower, the colour family when it declares only _EmissionColor
    [InlineData("p0/Reflective/Bumped Emissive Specular SMap", false, false, true, "Power")]
    [InlineData("p0/Reflective/Bumped Emissive Specular SMap", false, true, true, "Power")]   // the leftover _EmissionColor of the material is not read
    [InlineData("p0/Reflective/Bumped Emissive Specular", false, false, true, "Power")]
    [InlineData("p0/Reflective/Bumped Animated Emissive Specular SMap", false, false, true, "Power")]
    [InlineData("p0/Cutout/Bumped Diffuse Emissive", false, true, false, "Color")]
    [InlineData("p0/Cutout/Bumped Diffuse Emissive", false, false, false, "None")]
    // not emissive shaders, whatever leftovers their materials carry
    [InlineData("p0/Reflective/Bumped Specular SMap", false, true, true, "None")]
    [InlineData("p0/Cutout/Bumped Diffuse", false, true, false, "None")]
    [InlineData("Custom/Vert Paint Shader Solid", true, true, false, "None")]
    [InlineData("Particles/Standard Unlit", true, true, false, "None")]
    [InlineData("Prism/Particles/Emissive", true, true, true, "None")]                        // not a p0 shader
    [InlineData("Hidden/ArenaMapPort/p0/Reflective/Bumped Emissive Specular SMap", false, false, true, "None")]   // a stand-in that was never rebound
    [InlineData("Hidden/ArenaMapPort/Standard", true, true, false, "None")]
    [InlineData("", true, true, true, "None")]
    [InlineData(null, true, true, true, "None")]
    public void ClassifyFollowsTheRealShaderNameAndWhatItDeclares(string? shader, bool keyword, bool hasColor, bool hasPower, string expected) =>
        Assert.Equal(expected, EmissionCapModel.Classify(shader, keyword, hasColor, hasPower).ToString());

    [Theory]
    [InlineData(6.8f, 4f, 0.588235f)]
    [InlineData(67.79352f, 4f, 0.0590035f)]
    [InlineData(3.93f, 4f, 1f)]            // the strongest p0 lamp of the released maps is left alone
    [InlineData(4f, 4f, 1f)]               // at the cap: nothing to do
    [InlineData(4.000001f, 4f, 1f)]        // within float noise of the cap: nothing to do (a re-captured capped value never moves again)
    [InlineData(6.8f, 0f, 1f)]             // 0 = off
    [InlineData(6.8f, -1f, 1f)]
    [InlineData(6.8f, float.NaN, 1f)]
    [InlineData(6.8f, float.PositiveInfinity, 1f)]
    [InlineData(0f, 4f, 1f)]               // a lamp that is off
    [InlineData(float.NaN, 4f, 1f)]
    [InlineData(float.PositiveInfinity, 4f, 1f)]
    public void DesiredScaleBringsALevelOntoTheCapAndLeavesEverythingElseAlone(float level, float cap, float expected) =>
        Assert.Equal(expected, EmissionCapModel.DesiredScale(level, cap), 5);

    [Fact]
    public void TheLevelsFollowTheDocumentedFormulas()
    {
        Assert.Equal(67.79352569580078f, EmissionCapModel.ColorLevel(CrystalR, CrystalG, CrystalB));
        Assert.Equal(0f, EmissionCapModel.ColorLevel(float.NaN, 1f, 1f));
        Assert.Equal(0f, EmissionCapModel.ColorLevel(-1f, -2f, -3f));
        Assert.Equal(6.8f, EmissionCapModel.PowerLevel(10f, 1f, 0.68f), 5);
        Assert.Equal(8.28f * 0.469f, EmissionCapModel.PowerLevel(8.28f, 0.469f, 1f), 5);          // Material #37 of the released maps: 3.88
        Assert.Equal(0f, EmissionCapModel.PowerLevel(0f, 1f, 1f));                                  // a lamp that is authored off
        Assert.Equal(0f, EmissionCapModel.PowerLevel(10f, 0f, 1f));
        Assert.Equal(0f, EmissionCapModel.PowerLevel(10f, 1f, 0f));
        Assert.Equal(0f, EmissionCapModel.PowerLevel(float.NaN, 1f, 1f));
        Assert.Equal(0f, EmissionCapModel.PowerLevel(10f, float.PositiveInfinity, 1f));
    }

    [Fact]
    public void ACrystalLampIsScaledOntoTheCapWithItsHueAndAlphaKept()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var lamp = Crystal();
        Assert.True(ledger.TryAdd(lamp, CrystalEntry()));
        var result = ledger.Apply(Surface, 4f);
        Assert.Equal((1, 1, 1), (result.Materials, result.Capped, result.Written));
        Assert.Equal(67.79352f, result.LargestOriginal, 4);
        Assert.Equal("367e4a61931e682b", result.LargestName);
        Assert.Equal(4f, Peak(lamp.Values), 4);
        Assert.Equal(CrystalG / CrystalR, lamp.Values.G / lamp.Values.R, 5);      // one scale for all three channels
        Assert.Equal(0f, lamp.Values.B);
        Assert.Equal(1f, lamp.Values.A);
        Assert.Equal(0f, lamp.Values.Power);                                      // the colour family never touches the power slot
    }

    [Fact]
    public void AChandelierIsScaledThroughItsPowerAlone()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var lamp = Chandelier();
        ledger.TryAdd(lamp, ChandelierEntry());
        var result = ledger.Apply(Surface, 4f);
        Assert.Equal(1, result.Capped);
        Assert.Equal(10f * 4f / 6.8f, lamp.Values.Power, 4);                      // 5.88: power x visibility x colour peak is now 4.0
        Assert.Equal(4f, EmissionCapModel.PowerLevel(lamp.Values.Power, 1f, 0.68f), 4);
        Assert.Equal((0f, 0f, 0f, 0f), (lamp.Values.R, lamp.Values.G, lamp.Values.B, lamp.Values.A));   // the colour slots stay out of it
    }

    [Fact]
    public void AMaterialAtOrBelowTheCapIsNeverWritten()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var projector = new FakeMaterial { Name = "165546dbf4ea570b", Values = new EmissionValues(0f, 0f, 0f, 0f, 3.932f) };   // the released maps' strongest p0 lamp
        ledger.TryAdd(projector, EmissionEntry.ForPower("165546dbf4ea570b", 3.932f, 1f, 1f));
        var result = ledger.Apply(Surface, 4f);
        Assert.Equal((1, 0, 0), (result.Materials, result.Capped, result.Written));
        Assert.Equal(0, projector.Writes);
        Assert.Equal(0f, result.LargestOriginal);
        Assert.Null(result.LargestName);
    }

    [Fact]
    public void ApplyingTheSameCapAgainWritesNothing()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var lamp = Crystal();
        var light = Chandelier();
        ledger.TryAdd(lamp, CrystalEntry());
        ledger.TryAdd(light, ChandelierEntry());
        Assert.Equal(2, ledger.Apply(Surface, 4f).Written);
        var second = ledger.Apply(Surface, 4f);
        Assert.Equal((2, 2, 0), (second.Materials, second.Capped, second.Written));
        Assert.Equal((1, 1), (lamp.Writes, light.Writes));
    }

    [Fact]
    public void ALiveChangeOfTheCapRetargetsFromTheOriginalNotFromTheLastResult()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var lamp = Crystal();
        ledger.TryAdd(lamp, CrystalEntry());
        ledger.Apply(Surface, 4f);
        ledger.Apply(Surface, 8f);
        Assert.Equal(8f, Peak(lamp.Values), 4);                                   // not 4 x (8/4) of a compounding step, and not capped twice
        ledger.Apply(Surface, 2f);
        Assert.Equal(2f, Peak(lamp.Values), 4);
        ledger.Apply(Surface, 200f);                                              // above the original: the original again, bit for bit
        Assert.Equal((CrystalR, CrystalG, CrystalB, 1f), (lamp.Values.R, lamp.Values.G, lamp.Values.B, lamp.Values.A));
    }

    [Fact]
    public void CapZeroWritesTheOriginalsBackBitForBitAndKeepsTheEntries()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var lamp = Crystal();
        var light = Chandelier();
        ledger.TryAdd(lamp, CrystalEntry());
        ledger.TryAdd(light, ChandelierEntry());
        ledger.Apply(Surface, 4f);
        var restored = ledger.Apply(Surface, 0f);
        Assert.Equal((2, 0, 2), (restored.Materials, restored.Capped, restored.Written));
        Assert.Equal((CrystalR, CrystalG, CrystalB, 1f), (lamp.Values.R, lamp.Values.G, lamp.Values.B, lamp.Values.A));
        Assert.Equal(10f, light.Values.Power);
        Assert.Equal(2, ledger.Count);
        // switching the repair back on needs no new scan
        Assert.Equal(2, ledger.Apply(Surface, 4f).Capped);
        Assert.Equal(4f, Peak(lamp.Values), 4);
        // Restore is the same operation as cap 0
        Assert.Equal(2, ledger.Restore(Surface).Written);
        Assert.Equal(10f, light.Values.Power);
    }

    [Fact]
    public void AMaterialTheLedgerAlreadyHoldsIsNeverCapturedAgain()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var lamp = Crystal();
        Assert.True(ledger.TryAdd(lamp, CrystalEntry()));
        ledger.Apply(Surface, 4f);
        // a rescan meets the same instance holding the capped value: it must not become the "original"
        var cappedAsSeenByARescan = EmissionEntry.ForColor("367e4a61931e682b", lamp.Values.R, lamp.Values.G, lamp.Values.B, lamp.Values.A);
        Assert.False(ledger.TryAdd(lamp, cappedAsSeenByARescan));
        ledger.Restore(Surface);
        Assert.Equal(CrystalR, lamp.Values.R);
        Assert.False(ledger.TryAdd(null!, CrystalEntry()));
        Assert.False(ledger.TryAdd(Crystal(), null!));
    }

    [Fact]
    public void ARaidThatMeetsTheCachedBundleMaterialAgainStartsFromTheRestoredOriginalAndEndsWhereTheFirstDid()
    {
        var lamp = Crystal();                                                     // the bundle's Material outlives the scene
        var first = new EmissionLedger<FakeMaterial>();
        first.TryAdd(lamp, CrystalEntry());
        first.Apply(Surface, 4f);
        var afterFirst = lamp.Values;
        first.Restore(Surface);                                                   // the scene unload
        first.Clear();
        Assert.Equal(0, first.Count);
        Assert.Equal(CrystalR, lamp.Values.R);

        var second = new EmissionLedger<FakeMaterial>();                          // the next raid scans what the material holds now
        second.TryAdd(lamp, EmissionEntry.ForColor(lamp.Name, lamp.Values.R, lamp.Values.G, lamp.Values.B, lamp.Values.A));
        second.Apply(Surface, 4f);
        Assert.Equal((afterFirst.R, afterFirst.G, afterFirst.B, afterFirst.A), (lamp.Values.R, lamp.Values.G, lamp.Values.B, lamp.Values.A));
    }

    [Fact]
    public void EvenARestoreThatNeverRanDoesNotCompoundBecauseTheCapIsACeiling()
    {
        var lamp = Crystal();
        var light = Chandelier();
        for (var raid = 0; raid < 6; raid++)                                      // six raids, no restore in between (a crash, an unload that never fired)
        {
            var ledger = new EmissionLedger<FakeMaterial>();
            ledger.TryAdd(lamp, EmissionEntry.ForColor(lamp.Name, lamp.Values.R, lamp.Values.G, lamp.Values.B, lamp.Values.A));
            ledger.TryAdd(light, EmissionEntry.ForPower(light.Name, light.Values.Power, 1f, 0.68f));
            ledger.Apply(Surface, 4f);
        }
        Assert.Equal(4f, Peak(lamp.Values), 4);
        Assert.Equal(4f, EmissionCapModel.PowerLevel(light.Values.Power, 1f, 0.68f), 4);
        Assert.Equal(1, lamp.Writes);                                             // the later raids found a value at the cap and wrote nothing
        Assert.Equal(1, light.Writes);
    }

    [Fact]
    public void APropertyAnotherControllerMovedAfterTheCapIsLeftAloneAndNotRestoredOverIt()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var light = Chandelier();
        ledger.TryAdd(light, ChandelierEntry());
        ledger.Apply(Surface, 4f);
        light.Values = new EmissionValues(0f, 0f, 0f, 0f, 2.5f);                  // e.g. a GarlandSwitcher tween
        var foreign = ledger.Apply(Surface, 3f);
        Assert.Equal((0, 0, 1), (foreign.Written, foreign.Capped, foreign.Foreign));
        Assert.Equal(2.5f, light.Values.Power);
        var restore = ledger.Restore(Surface);
        Assert.Equal((0, 1), (restore.Written, restore.Foreign));
        Assert.Equal(2.5f, light.Values.Power);
    }

    [Fact]
    public void ADestroyedMaterialIsDroppedWithoutAFailure()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var lamp = Crystal();
        var light = Chandelier();
        ledger.TryAdd(lamp, CrystalEntry());
        ledger.TryAdd(light, ChandelierEntry());
        lamp.Destroyed = true;
        var result = ledger.Apply(Surface, 4f);
        Assert.Equal((1, 1, 1), (result.Gone, result.Materials, result.Capped));
        Assert.Equal(1, ledger.Count);
        Assert.Equal(0, lamp.Writes);
    }

    [Fact]
    public void UnusableValuesAreCapturedButNeverTouched()
    {
        var ledger = new EmissionLedger<FakeMaterial>();
        var nan = new FakeMaterial { Values = new EmissionValues(float.NaN, 1f, 1f, 1f, 0f) };
        var off = new FakeMaterial { Values = new EmissionValues(0f, 0f, 0f, 0f, 0f) };
        var negative = new FakeMaterial { Values = new EmissionValues(-5f, -5f, -5f, 1f, 0f) };
        ledger.TryAdd(nan, EmissionEntry.ForColor("nan", float.NaN, 1f, 1f, 1f));
        ledger.TryAdd(off, EmissionEntry.ForPower("off", 0f, 1f, 1f));
        ledger.TryAdd(negative, EmissionEntry.ForColor("negative", -5f, -5f, -5f, 1f));
        var result = ledger.Apply(Surface, 4f);
        Assert.Equal((3, 0, 0), (result.Materials, result.Capped, result.Written));
        Assert.Equal((0, 0, 0), (nan.Writes, off.Writes, negative.Writes));
    }

    // Iceberg's emissive materials above 1.0 and the released maps' strongest ones (levels from the package census of 2026-10-31):
    // at cap 4.0 exactly the six Iceberg materials are written and nothing else.
    [Fact]
    public void AtCapFourExactlyIcebergsSixOverBrightMaterialsAreScaledAndTheReleasedMapsStrongestAreNot()
    {
        var table = new (string Name, bool Standard, float Level)[]
        {
            ("Emission_Yellow", true, 67.794f), ("Prison_Emission_red", false, 10.0f), ("Prison_Emission_passive", false, 7.736f),
            ("Sanatorium_Emissive_Orange", false, 6.792f), ("Prison_Emission_blue", false, 6.226f), ("Plafond_lamp_02_source_E_red", false, 4.277f),
            // Iceberg's remaining lamps stay as authored
            ("Searchlight_03_source_E", false, 2.925f), ("Sanatorium_Emissive_white", false, 2.83f), ("shopping_mall_ceiling_light_emission", false, 2.333f),
            ("Preset_refill_container_emission_green", true, 2.119f), ("AreaLightSource", true, 1.919f), ("projector_emission", true, 1.32f),
            // the strongest emissive materials of the four released maps
            ("Projector_portable_Albedo_emissive", false, 3.932f), ("Material #37", false, 3.883f), ("Airpit_emissive_02", false, 3.62f),
            ("Airport_Lamp_emissive_neon", true, 3.277f),
        };
        var ledger = new EmissionLedger<FakeMaterial>();
        var materials = new List<(string Name, FakeMaterial Material)>();
        foreach (var (name, standard, level) in table)
        {
            var material = new FakeMaterial { Name = name, Values = standard ? new EmissionValues(level, level * 0.3f, 0f, 1f, 0f) : new EmissionValues(0f, 0f, 0f, 0f, level) };
            ledger.TryAdd(material, standard ? EmissionEntry.ForColor(name, level, level * 0.3f, 0f, 1f) : EmissionEntry.ForPower(name, level, 1f, 1f));
            materials.Add((name, material));
        }
        var result = ledger.Apply(Surface, 4f);
        Assert.Equal(table.Length, result.Materials);
        Assert.Equal(6, result.Capped);
        Assert.Equal(6, result.Written);
        Assert.Equal("Emission_Yellow", result.LargestName);
        var written = materials.Where(pair => pair.Material.Writes > 0).Select(pair => pair.Name).ToArray();
        Assert.Equal(new[] { "Emission_Yellow", "Prison_Emission_red", "Prison_Emission_passive", "Sanatorium_Emissive_Orange", "Prison_Emission_blue", "Plafond_lamp_02_source_E_red" }, written);
    }

    [Fact]
    public void TheShadersDeclaredFactsAreWhatTheDocumentationClaims()
    {
        // The rule table is read from EFT's own shaders bundle (UnityPy, 2026-10-31); the property names are the ones the glue asks the material for.
        Assert.Equal("_EmissionColor", EmissionCapModel.EmissionColorProperty);
        Assert.Equal("_EmissionPower", EmissionCapModel.EmissionPowerProperty);
        Assert.Equal("_EmissionVisibility", EmissionCapModel.EmissionVisibilityProperty);
        Assert.Equal("_EmissiveColor", EmissionCapModel.EmissiveColorProperty);
        Assert.Equal("_EMISSION", EmissionCapModel.EmissionKeyword);
        Assert.Equal(100f, EmissionCapModel.MaxCap);
    }
}
