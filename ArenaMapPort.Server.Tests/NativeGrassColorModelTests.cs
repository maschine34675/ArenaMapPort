#nullable disable
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The colour lists, the season rule and the constants of the native grass probe (PLAN-YARD item 23). Every number is pinned against the exact float32 values that
/// work/tmp/yard-grass/core/exact_values.py printed from Arena's own serialized objects on 2026-11-21 (core/exact_values.txt); the sources are named in NativeGrassColorModel.cs.
/// Unit-tested; nothing here is host-run.
/// </summary>
public sealed class NativeGrassColorModelTests
{
    // ---- the colour lists and the season rule -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSummerListsHaveFiveEntriesOfFourFloatsAndOneEntryPerSlice()
    {
        GrassColorLists lists = NativeGrassColorModel.SummerLists;
        Assert.Equal(NativeGrassColorModel.ListLength, lists.Count);
        Assert.Equal(5, lists.Count);
        Assert.Equal(20, lists.Dry.Length);
        Assert.Equal(20, lists.Healthy.Length);
        Assert.Equal(GrassTestPack.RealLayout().SliceCount, lists.Count);       // index i of the lists is the texture slice i: five slices need five entries
    }

    [Fact]
    public void TheSummerListsAreArenasDryAndHealthyColoursOfYard()
    {
        GrassColorLists l = NativeGrassColorModel.SummerLists;
        float[] dry =
        {
            1f, 0.94117653f, 0.078431375f, 0f,
            0.47450984f, 0.56078434f, 0.35686275f, 0f,
            0.6509434f, 0.63684934f, 0.43600926f, 0f,
            0.71330523f, 0.7735849f, 0.4743681f, 0f,
            1f, 0.6683485f, 0f, 0f,
        };
        float[] healthy =
        {
            1f, 0.6156863f, 0.20000002f, 0f,
            1f, 1f, 1f, 0f,
            0.990566f, 0.91738427f, 0.6541474f, 0f,
            0.6509434f, 0.6153164f, 0.53426486f, 0f,
            0.4811321f, 0.45832464f, 0.39489144f, 0f,
        };
        Assert.Equal(dry, l.Dry);
        Assert.Equal(healthy, l.Healthy);
        // the rounded values G1 and G2 print (three digits): slice 2 carries 8,103 of Yard's 10,132 clumps
        Assert.Equal(0.65f, l.Dry[2 * 4 + 0], 2);
        Assert.Equal(0.64f, l.Dry[2 * 4 + 1], 2);
        Assert.Equal(0.44f, l.Dry[2 * 4 + 2], 2);
        Assert.Equal(0.99f, l.Healthy[2 * 4 + 0], 2);
        Assert.Equal(0.92f, l.Healthy[2 * 4 + 1], 2);
        Assert.Equal(0.65f, l.Healthy[2 * 4 + 2], 2);
    }

    [Fact]
    public void EveryChannelIsAUnitFloatAndTheAlphaIsZero()
    {
        foreach (float[] list in new[] { NativeGrassColorModel.SummerLists.Dry, NativeGrassColorModel.SummerLists.Healthy })
        {
            for (int i = 0; i < list.Length; i++)
            {
                Assert.InRange(list[i], 0f, 1f);
                if (i % 4 == 3) Assert.Equal(0f, list[i]);
            }
        }
    }

    [Fact]
    public void NoSeasonValueOfTheGameEverChangesTheLists()
    {
        GrassColorLists summer = NativeGrassColorModel.SummerLists;
        // ESeason 0..5, ESeasonStatus 0..6 (Storm is 6 and would throw in EFT's own switch), and numbers no enum has
        foreach (int season in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 100, 255, -1, int.MinValue, int.MaxValue })
        {
            Assert.Same(summer, NativeGrassColorModel.ForSeason(season));
        }
        foreach (string name in new[] { "Summer", "Autumn", "Winter", "Spring", "AutumnLate", "SpringEarly", "Storm", "winter", "", " ", null, "nonsense" })
        {
            Assert.Same(summer, NativeGrassColorModel.ForSeasonName(name));
        }
    }

    [Fact]
    public void AWinterOrAutumnDateDoesNotSelectTheWinterOrSeasonsList()
    {
        // EFT's Winter list is five whites and its Seasons list the dry list without the healthy blend: neither may be what the grass takes
        GrassColorLists winterDate = NativeGrassColorModel.ForSeasonName("Winter");
        Assert.NotEqual(Enumerable.Repeat(1f, 20).Select((v, i) => i % 4 == 3 ? 0f : 1f).ToArray(), winterDate.Dry);
        Assert.Equal(NativeGrassColorModel.SummerLists.Healthy, NativeGrassColorModel.ForSeasonName("AutumnLate").Healthy);
        Assert.NotEqual(NativeGrassColorModel.SummerLists.Dry, NativeGrassColorModel.SummerLists.Healthy);
    }

    [Fact]
    public void TheColourModelNeverReadsASeasonFromTheGame()
    {
        // the rule is in the code, not in a lookup: the model file has no season type of the game in it and no list other than the summer pair
        string source = File.ReadAllText(Path.Combine(ClientDir(), "NativeGrassColorModel.cs"));
        Assert.DoesNotContain("static readonly float[] Winter", source);
        Assert.DoesNotContain("static readonly float[] Seasons", source);
        Assert.DoesNotContain("using ", source.Replace("using System;", string.Empty));
    }

    static string ClientDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "ArenaMapPort.Client");
            if (File.Exists(Path.Combine(path, "Plugin.cs"))) return path;
        }
        throw new DirectoryNotFoundException("ArenaMapPort.Client");
    }

    // ---- the prototype -----------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePrototypeIsArenasCrossGrassPrototype()
    {
        Assert.True(NativeGrassValues.ProtoIsShadowCasting);
        Assert.True(NativeGrassValues.ProtoUseCustomShadowDistance);
        Assert.Equal(25f, NativeGrassValues.ProtoShadowDistance);
        Assert.True(NativeGrassValues.ProtoUseOriginalShaderForShadow);
        Assert.True(NativeGrassValues.ProtoCullShadows);
        Assert.Equal(0f, NativeGrassValues.ProtoMinDistance);
        Assert.Equal(150f, NativeGrassValues.ProtoMaxDistance);
        Assert.Equal(0f, NativeGrassValues.ProtoMinDistanceOptic);
        Assert.Equal(500f, NativeGrassValues.ProtoMaxDistanceOptic);
        Assert.True(NativeGrassValues.ProtoIsFrustumCulling);
        Assert.True(NativeGrassValues.ProtoIsOcclusionCulling);
        Assert.Equal(0f, NativeGrassValues.ProtoMinCullingDistance);
        Assert.Equal(0f, NativeGrassValues.ProtoOcclusionOffset);
        Assert.Equal(1, NativeGrassValues.ProtoOcclusionAccuracy);
        Assert.False(NativeGrassValues.ProtoIsLodCrossFade);
        Assert.False(NativeGrassValues.ProtoIsLodCrossFadeAnimate);
        Assert.Equal(0.1f, NativeGrassValues.ProtoLodFadeTransitionWidth);
        Assert.Equal(1f, NativeGrassValues.ProtoLodBiasAdjustment);
        Assert.True(NativeGrassValues.ProtoCheckedForBillboardExtensions);
        Assert.False(NativeGrassValues.ProtoUseGeneratedBillboard);
        Assert.False(NativeGrassValues.ProtoIsBillboardDisabled);
        Assert.False(NativeGrassValues.ProtoAutoUpdateTransformData);
        Assert.Equal(0, NativeGrassValues.ProtoTreeType);
        Assert.False(NativeGrassValues.ProtoEnableRuntimeModifications);
        Assert.Equal(new[] { 0f, 4f, 0f, 0f, 9f, 5f, 0f, 0f, 2f, 6f, 0f, 0f, 3f, 7f, 0f, 0f }, NativeGrassValues.ProtoShadowLodMap);
    }

    [Fact]
    public void ACustomShadowDistanceNeedsTheOriginalShaderForShadows()
    {
        // G1 section 1.1: without it the manager would build Material(Shader.Find("Hidden/GPUInstancer/ShadowsOnly")), a shader EFT does not ship
        if (NativeGrassValues.ProtoIsShadowCasting) Assert.True(NativeGrassValues.ProtoUseOriginalShaderForShadow);
    }

    [Fact]
    public void TheManagersAreArenasTwoAndTheOpticOneComesSecond()
    {
        Assert.Equal(2, NativeGrassValues.ManagerCount);
        Assert.True(NativeGrassValues.ManagerFrustumCulling);
        Assert.True(NativeGrassValues.ManagerOcclusionCulling);
        Assert.Equal(3, NativeGrassValues.ManagerMaxThreads);
        Assert.Equal(-1, NativeGrassValues.ManagerLayerMask);
        Assert.Equal(0f, NativeGrassValues.ManagerMinCullingDistance);
    }

    [Fact]
    public void TheLodGroupHasTwoLevelsWithArenasHeights()
    {
        Assert.Equal(0.17512812f, NativeGrassValues.Lod0ScreenHeight);
        Assert.Equal(0.0020024395f, NativeGrassValues.Lod1ScreenHeight);
        Assert.True(NativeGrassValues.Lod0ScreenHeight > NativeGrassValues.Lod1ScreenHeight);
        Assert.Equal(0f, NativeGrassValues.LodFadeTransitionWidth);
        Assert.Equal(2.0000002f, NativeGrassValues.LodGroupSize);
        Assert.Equal(0, NativeGrassValues.LodFadeMode);
        Assert.True(NativeGrassValues.LodAnimateCrossFading);
    }

    // ---- the material ------------------------------------------------------------------------------------------------------------------------------------------------------

    static float Float(string name) => NativeGrassValues.MaterialFloats.Single(f => f.Name == name).Value;

    static GrassVector Vector(string name) => NativeGrassValues.MaterialVectors.Single(v => v.Name == name);

    [Fact]
    public void TheMaterialFloatsAreArenasSeventeenMinusTheOneThatDiffers()
    {
        // 17 floats on Arena's material, `_IsBillboard` is the only one that differs between the two materials and is a named pair
        Assert.Equal(16, NativeGrassValues.MaterialFloats.Length);
        Assert.Equal(16, NativeGrassValues.MaterialFloats.Select(f => f.Name).Distinct().Count());
        Assert.DoesNotContain(NativeGrassValues.MaterialFloats, f => f.Name == NativeGrassValues.IsBillboardName);
        Assert.Equal("_IsBillboard", NativeGrassValues.IsBillboardName);
        Assert.Equal(0f, NativeGrassValues.Lod0IsBillboard);
        Assert.Equal(1f, NativeGrassValues.Lod1IsBillboard);
        Assert.Equal(1f, Float("AditionClip"));
        Assert.Equal(0f, Float("IsBillboard"));
        Assert.Equal(0.045f, Float("_AmbientOcclusion"));
        Assert.Equal(0f, Float("_ArrayIndex"));
        Assert.Equal(0f, Float("_BillboardFaceCamPos"));
        Assert.Equal(0.33f, Float("_Cutoff"));
        Assert.Equal(1f, Float("_FlipUV"));
        Assert.Equal(19.5f, Float("_NoiseSpread"));
        Assert.Equal(-1.5f, Float("_TaaBias"));
        Assert.Equal(0.3f, Float("_WindIdleSway"));
        Assert.Equal(0.15f, Float("_WindWaveSway"));
        Assert.Equal(0.5f, Float("_WindWaveTint"));
        Assert.Equal(1f, Float("_WindWavesOn"));
        Assert.Equal(2f, Float("_ZTest"));
        Assert.Equal(1f, Float("_ZWrite"));
        Assert.Equal(1f, Float("__dirty"));
    }

    [Fact]
    public void TheMaterialColoursAreArenasFiveVectors()
    {
        Assert.Equal(5, NativeGrassValues.MaterialVectors.Length);
        GrassVector fade = Vector("_CutoffFade");
        Assert.Equal((40f, 45f, 45f, 50f), (fade.X, fade.Y, fade.Z, fade.W));
        GrassVector dry = Vector("_DryColor");
        Assert.Equal((0.47390676f, 0.50735295f, 0.32082614f, 1f), (dry.X, dry.Y, dry.Z, dry.W));
        GrassVector healthy = Vector("_HealthyColor");
        Assert.Equal((1f, 1f, 1f, 0f), (healthy.X, healthy.Y, healthy.Z, healthy.W));
        GrassVector wave = Vector("_WaveAndDistance");
        Assert.Equal((12f, 3.6f, 1f, 1f), (wave.X, wave.Y, wave.Z, wave.W));
        GrassVector tint = Vector("_WindWaveTintColor");
        Assert.Equal((0.499f, 0.48356444f, 0.27518383f, 0f), (tint.X, tint.Y, tint.Z, tint.W));
    }

    [Fact]
    public void TheNamesAndSlotsAreEftsShaderAndTheFiveTextureSlots()
    {
        Assert.Equal("GPUInstancer/Foliage_Simple", NativeGrassValues.ShaderName);
        Assert.Equal("Textures/Noise/Fractal_Simplex_grayscale", NativeGrassValues.NoiseResourcePath);
        Assert.Equal("_MainTexArray", NativeGrassValues.TexArraySlot);
        Assert.Equal("_NormalMapArray", NativeGrassValues.NormalArraySlot);
        Assert.Equal("_NormalMap", NativeGrassValues.NormalTextureSlot);
        Assert.Equal("_HealthyDryNoiseTexture", NativeGrassValues.NoiseSlot);
        Assert.Equal("_WindWaveNormalTexture", NativeGrassValues.WaveNormalSlot);
        Assert.Equal("_ArrayIndexBuffer", NativeGrassValues.IndexBufferName);
        Assert.Equal("AditionClip_ON", NativeGrassValues.EnabledKeyword);
        Assert.Equal(31, NativeGrassValues.Layer);
    }

    [Fact]
    public void TheShaderNameIsNotAStandInName()
    {
        // ShaderRebind's stand-ins are named Hidden/ArenaMapPort/<original>: a material on one of them would reach the manager as a Standard clone (G1 section 0 point 2)
        Assert.DoesNotContain("Hidden/ArenaMapPort", NativeGrassValues.ShaderName);
        Assert.DoesNotContain("Hidden/", NativeGrassValues.ShaderName);
    }

    [Fact]
    public void TheOwnedObjectNamesAvoidTheNamesThePortsSweepsMatch()
    {
        string[] banned = { "floor", "ground", "indoor", "puddle" };
        string[] names =
        {
            NativeGrassValues.HolderName, NativeGrassValues.PrefabRootName, NativeGrassValues.Lod0Name, NativeGrassValues.Lod1Name,
            NativeGrassValues.Lod0MaterialName, NativeGrassValues.Lod1MaterialName, NativeGrassValues.TexArrayObjectName,
            NativeGrassValues.NormalArrayObjectName, NativeGrassValues.NormalTextureObjectName,
        };
        foreach (string name in names)
        {
            foreach (string word in banned) Assert.DoesNotContain(word, name, StringComparison.OrdinalIgnoreCase);
            if (!name.StartsWith("lod", StringComparison.Ordinal)) Assert.StartsWith("ArenaMapPort_", name);
        }
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void TheCommandBufferNamesAreTheFourTheManagersAdd()
    {
        Assert.Equal(new[] { "MotionVectorsPASS", "MotionVectorsAfterPASS", "MotionVectorsOpticPASS", "MotionVectorsAfterOpticPASS" }, NativeGrassValues.CommandBufferNames);
        Assert.Equal("GPUInstancerHiZOcclusionGenerator", NativeGrassValues.HiZComponentTypeName);
    }

    // ---- the meshes --------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Lod0IsTwoCrossedQuadsOfEightVerticesAndTwelveIndices()
    {
        GrassMeshData m = NativeGrassValues.BuildMesh(true);
        Assert.Equal(8, m.VertexCount);
        Assert.Equal(8 * 3, m.Normals.Length);
        Assert.Equal(8 * 4, m.Tangents.Length);
        Assert.Equal(8 * 2, m.Uvs.Length);
        Assert.Equal(new[] { 0, 2, 3, 0, 3, 1, 4, 6, 7, 4, 7, 5 }, m.Indices);
        // quad A runs along x at z = 0, quad B along z at x = 0, both y 0..1, both inside the unit box
        for (int v = 0; v < 8; v++)
        {
            float x = m.Positions[v * 3], y = m.Positions[v * 3 + 1], z = m.Positions[v * 3 + 2];
            Assert.InRange(x, -0.5f, 0.5f);
            Assert.InRange(y, 0f, 1f);
            Assert.InRange(z, -0.5f, 0.5f);
            if (v < 4) Assert.Equal(0f, z); else Assert.Equal(0f, x);
            Assert.Equal(0.5f - (v < 4 ? x : z), m.Uvs[v * 2], 6);
            Assert.Equal(y, m.Uvs[v * 2 + 1]);
            Assert.Equal(1f, m.Normals[v * 3] * m.Normals[v * 3] + m.Normals[v * 3 + 1] * m.Normals[v * 3 + 1] + m.Normals[v * 3 + 2] * m.Normals[v * 3 + 2], 6);
            Assert.Equal(-1f, m.Tangents[v * 4 + 3]);
        }
        // the first vertex of each quad is the one Arena's lod0 starts with: (0.5, 1, 0) with uv (0, 1), and (0, 1, 0.5) with uv (0, 1)
        Assert.Equal((0.5f, 1f, 0f), (m.Positions[0], m.Positions[1], m.Positions[2]));
        Assert.Equal((0f, 1f, 0.5f), (m.Positions[12], m.Positions[13], m.Positions[14]));
        Assert.Equal((0f, 1f), (m.Uvs[0], m.Uvs[1]));
        Assert.Equal((0f, 1f), (m.Uvs[8], m.Uvs[9]));
        Assert.Equal((0f, 0f, 1f), (m.Normals[0], m.Normals[1], m.Normals[2]));
        Assert.Equal((-1f, 0f, 0f), (m.Normals[12], m.Normals[13], m.Normals[14]));
        Assert.Equal((-1f, 0f, 0f, -1f), (m.Tangents[0], m.Tangents[1], m.Tangents[2], m.Tangents[3]));
        Assert.Equal((0f, 0f, -1f, -1f), (m.Tangents[16], m.Tangents[17], m.Tangents[18], m.Tangents[19]));
    }

    [Fact]
    public void Lod1IsTheFirstQuadAlone()
    {
        GrassMeshData one = NativeGrassValues.BuildMesh(false);
        GrassMeshData two = NativeGrassValues.BuildMesh(true);
        Assert.Equal(4, one.VertexCount);
        Assert.Equal(new[] { 0, 2, 3, 0, 3, 1 }, one.Indices);
        Assert.Equal(two.Positions.Take(12).ToArray(), one.Positions);
        Assert.Equal(two.Normals.Take(12).ToArray(), one.Normals);
        Assert.Equal(two.Tangents.Take(16).ToArray(), one.Tangents);
        Assert.Equal(two.Uvs.Take(8).ToArray(), one.Uvs);
    }
}
