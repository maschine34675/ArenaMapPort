using System.Collections.Generic;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Multimap generalization (docs/progress/multimap-generalize.md), §1's own acceptance
/// criterion: <see cref="ArenaMapRegistry.Equator"/>'s fields are read directly off
/// <see cref="ArenaEquatorLocation"/>'s own consts/statics (not re-declared as independent
/// literals), so byte-for-byte drift between the two is structurally impossible — but this test
/// still exists as the project's own established cross-check pattern (the same "duplicated table,
/// diffed against the built artifact" idiom validate_bundle_roundtrip.py/validate_preset.py
/// already use on the Python side) and as a regression guard if a future edit ever changes
/// ArenaMapRegistry.Equator to a literal instead of a reference.
/// </summary>
public class MapDescriptorConsistencyTests
{
    [Fact]
    public void Equator_Descriptor_MatchesArenaEquatorLocationConsts()
    {
        var map = ArenaMapRegistry.Equator;

        Assert.Equal(ArenaEquatorLocation.Key, map.Key);
        Assert.Equal(ArenaEquatorLocation.IdField, map.IdField);
        Assert.Equal(ArenaEquatorLocation.PresetBundleKey, map.PresetBundleKey);
        Assert.Equal(ArenaEquatorLocation.PresetAssetName, map.PresetAssetName);
        Assert.Equal(ArenaEquatorLocation.ScenesBundleKey, map.ScenesBundleKey);
        Assert.Equal(ArenaEquatorLocation.NavMeshBundleKey, map.NavMeshBundleKey);
        Assert.Equal(ArenaEquatorLocation.ScriptsSceneName, map.ScriptsSceneName);
        Assert.Equal(ArenaEquatorLocation.AllSceneNames, map.AllSceneNames);
    }

    // The three bundle-filename helpers are what BundleHost.cs/NavMeshInstaller.cs/Plugin.cs/
    // BuildBundles.cs all key off — must resolve to exactly the pre-generalization literals
    // ("arena_equator_preset.bundle" etc.), not just "the served key's basename, whatever that
    // computes to".
    [Fact]
    public void Equator_BundleFileNames_MatchThePreGeneralizationLiterals()
    {
        var map = ArenaMapRegistry.Equator;

        Assert.Equal("arena_equator_preset.bundle", map.PresetBundleFileName);
        Assert.Equal("arena_equator_scenes.bundle", map.ScenesBundleFileName);
        Assert.Equal("arena_equator_navmesh.bundle", map.NavMeshBundleFileName);
    }

    [Fact]
    public void Equator_DbBaseJsonPath_IsTheLegacyUnnestedPath()
    {
        // Must stay "base.json" (not "arena_equator/base.json") — the already-deployed dist/
        // layout (dist/SPT_Runtime/user/mods/ArenaMapPort/db/base.json) must keep working
        // without a redeploy/migration.
        Assert.Equal("base.json", ArenaMapRegistry.Equator.DbBaseJsonPath);
    }

    // Spawn barriers (exfils-stage2 follow-up, BoothDoorRepairs.OpenBarriers): only Equator names one, by hierarchy
    // path (object names repeat in Arena scenes), and every other map keeps the empty default — nothing gets
    // deactivated on a map nobody has walked yet.
    [Fact]
    public void OpenedBarriers_EquatorNamesTheFckShutterByPath_OtherMapsNone()
    {
        Assert.Equal(new[] { "SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)" }, ArenaMapRegistry.Equator.OpenedBarriers);
        foreach (var map in ArenaMapRegistry.All)
        {
            Assert.NotNull(map.OpenedBarriers);
            Assert.All(map.OpenedBarriers, path => Assert.Contains("/", path));
            if (!ReferenceEquals(map, ArenaMapRegistry.Equator))
            {
                Assert.Empty(map.OpenedBarriers);
            }
        }
    }

    [Fact]
    public void Registry_HasNoDuplicateKeysOrIdFields()
    {
        var keys = new HashSet<string>();
        var idFields = new HashSet<string>();
        foreach (var map in ArenaMapRegistry.All)
        {
            Assert.True(keys.Add(map.Key), $"duplicate map key: {map.Key}");
            Assert.True(idFields.Add(map.IdField), $"duplicate map IdField: {map.IdField}");
        }
    }

    [Theory]
    [MemberData(nameof(LocationRegistrationTests.AllMaps), MemberType = typeof(LocationRegistrationTests))]
    public void IdField_Is24LowercaseHexChars(ArenaMapDescriptor map)
    {
        Assert.Matches("^[0-9a-f]{24}$", map.IdField);
    }

    [Theory]
    [MemberData(nameof(LocationRegistrationTests.AllMaps), MemberType = typeof(LocationRegistrationTests))]
    public void EveryMap_ScriptsSceneIsFirstInAllSceneNames(ArenaMapDescriptor map)
    {
        Assert.NotEmpty(map.AllSceneNames);
        Assert.Equal(map.ScriptsSceneName, map.AllSceneNames[0]);
    }
}
