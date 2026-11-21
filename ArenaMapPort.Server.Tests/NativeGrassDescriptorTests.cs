using System.Text.Json;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The descriptor flag of the native grass (stage 2, 2026-11-21): which maps have Arena grass is <see cref="ArenaMapDescriptor.HasNativeGrass"/>, mirrored by <c>has_native_grass</c> in tools/maps/&lt;stem&gt;.json and held
/// equal to the explicit NATIVE_GRASS_MAP_KEYS of validate_map_descriptors.py by the Python validator (the water flag's precedent, see <see cref="NativeWaterTests"/>). The Grass scene of such a map is the one
/// scene of its list that ends in "_Grass", and its staged bundle is bundles/&lt;key&gt;_grass.bundle; neither is part of what makes a map available.
/// </summary>
public sealed class NativeGrassDescriptorTests
{
    static string Text(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n");   // the sources are CRLF on the build machine
        }
        throw new FileNotFoundException(relative);
    }

    static readonly string[] GrassMaps = { "arena_bowl", "arena_iceberg", "arena_yard" };

    [Fact]
    public void OnlyBowlIcebergAndYardHaveNativeGrass_AndTheFlagMirrorsTheSidecars()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            using var sidecar = JsonDocument.Parse(Text("tools/maps/" + map.Key["arena_".Length..] + ".json"));
            bool flag = sidecar.RootElement.TryGetProperty("has_native_grass", out var value) && value.GetBoolean();
            Assert.Equal(flag, map.HasNativeGrass);
            Assert.Equal(GrassMaps.Contains(map.Key), map.HasNativeGrass);
        }
        Assert.True(ArenaMapRegistry.Bowl.HasNativeGrass);
        Assert.True(ArenaMapRegistry.Iceberg.HasNativeGrass);
        Assert.True(ArenaMapRegistry.Yard.HasNativeGrass);
        Assert.False(ArenaMapRegistry.Equator.HasNativeGrass);
        Assert.False(ArenaMapRegistry.Bay5.HasNativeGrass);
        Assert.False(ArenaMapRegistry.AirPit.HasNativeGrass);
        Assert.False(ArenaMapRegistry.AutoService.HasNativeGrass);
    }

    [Fact]
    public void TheRegistryOrderIsTheOrderOfTheValidatorsList()
    {
        // NATIVE_GRASS_MAP_KEYS = ("arena_bowl", "arena_iceberg", "arena_yard") in the registry's order (Bowl, Iceberg, Yard)
        var inRegistry = ArenaMapRegistry.All.Where(m => m.HasNativeGrass).Select(m => m.Key).ToArray();
        Assert.Equal(GrassMaps, inRegistry);
        Assert.Contains("NATIVE_GRASS_MAP_KEYS = (\"arena_bowl\", \"arena_iceberg\", \"arena_yard\")", Text("tools/validate/validate_map_descriptors.py"));
    }

    [Fact]
    public void EveryGrassMapNamesItsGrassSceneAmongItsOwnedScenes()
    {
        Assert.Equal("Arena_Bowl_Grass", ArenaMapRegistry.Bowl.GrassSceneName);
        Assert.Equal("Arena_Sanatorium_Grass", ArenaMapRegistry.Iceberg.GrassSceneName);
        Assert.Equal("Arena_Yard_Grass", ArenaMapRegistry.Yard.GrassSceneName);
        foreach (var map in ArenaMapRegistry.All.Where(m => m.HasNativeGrass))
        {
            Assert.Contains(map.GrassSceneName, map.AllSceneNames);
            Assert.Single(map.AllSceneNames, n => n.EndsWith("_Grass", StringComparison.Ordinal));      // the rule the Python validator holds: exactly one
            Assert.NotEqual(map.ScriptsSceneName, map.GrassSceneName);
        }
    }

    [Fact]
    public void AMapWithoutNativeGrassHasNoGrassSceneEvenWhenItListsOne()
    {
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => !m.HasNativeGrass))
            Assert.Null(map.GrassSceneName);
        // Prison and Train (Catalog only) list a Grass scene and have no grass work: the flag, not the scene list, decides
        Assert.Contains("Arena_Prison_Grass", ArenaMapRegistry.Catalog.SelectMany(m => m.AllSceneNames));
        Assert.Contains("Arena_Train_Grass", ArenaMapRegistry.Catalog.SelectMany(m => m.AllSceneNames));
    }

    [Fact]
    public void TheStagedBundleIsTheMapKeyPlusGrassBundleAndIsNoServedBundle()
    {
        Assert.Equal("arena_bowl_grass.bundle", ArenaMapRegistry.Bowl.GrassBundleFileName);
        Assert.Equal("arena_iceberg_grass.bundle", ArenaMapRegistry.Iceberg.GrassBundleFileName);
        Assert.Equal("arena_yard_grass.bundle", ArenaMapRegistry.Yard.GrassBundleFileName);
        foreach (var map in ArenaMapRegistry.All)
        {
            Assert.Equal(map.Key + "_grass.bundle", map.GrassBundleFileName);
            Assert.Equal(NativeGrassPack.BundleFileName(map.Key), map.GrassBundleFileName);
            // never one of the three bundles EFT or the client's NavMesh installer asks for: the grass bundle is not a served bundle, so no map is made unavailable by it
            Assert.NotEqual(map.ScenesBundleFileName, map.GrassBundleFileName);
            Assert.NotEqual(map.NavMeshBundleFileName, map.GrassBundleFileName);
            Assert.NotEqual(map.PresetBundleFileName, map.GrassBundleFileName);
            Assert.DoesNotContain("grass", map.ScenesBundleKey, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grass", map.PresetBundleKey, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grass", map.NavMeshBundleKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NothingThatDecidesAMapsAvailabilityKnowsTheGrassBundle()
    {
        // the bundle host serves the three bundles of each map, the notices count the same three plus the shader sidecar, the NavMesh installer loads its own, the server registers the map from its db
        foreach (string file in new[] { "BundleHost.cs", "MapNoticeInputs.cs", "NavMeshInstaller.cs", "MapNotices.cs", "MapLoadFailures.cs", "ServerStatusClient.cs", "ServerStatusParser.cs" })
            Assert.DoesNotContain("Grass", Text("ArenaMapPort.Client/" + file));
        foreach (string file in new[] { "ArenaMapPort.Server/ArenaServerStatus.cs", "ArenaMapPort.Shared/ArenaStatusContract.cs" })
            Assert.DoesNotContain("Grass", Text(file));
    }
}
