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
    public void Equator_DbBaseJsonPath_IsMapScoped()
    {
        Assert.Equal("arena_equator/base.json", ArenaMapRegistry.Equator.DbBaseJsonPath);
    }

    // Spawn barriers are authored by hierarchy path only for the walked maps (Equator, Bay5, AirPit).
    // Every remaining map keeps the empty default until its own barriers have been checked.
    // A path is a root object name followed by optional "/"-separated children, so a single segment
    // (AirPit's BUS_DOORS and PASSPORT_CONTROL_DOORS gate groups) is a valid root path and AirPit's two
    // portal gates are ordinary nested paths; empty paths and empty segments ("a//b", leading or trailing
    // "/") are not.
    [Fact]
    public void OpenedBarriers_WalkedMapsNamePaths_OtherMapsNone()
    {
        Assert.Equal(new[] { "SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)" }, ArenaMapRegistry.Equator.OpenedBarriers);
        Assert.Equal(new[]
        {
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)",
            "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)",
            "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame",
        }, ArenaMapRegistry.Bay5.OpenedBarriers);
        Assert.Equal(new[]
        {
            "BUS_DOORS",
            // the passport control (16 lane turnstiles + the red lane sheets) is the north team box's HELD gate since
            // 2026-10-18: closed at raid start in every mode, opened only by SpawnGates.Release (HeldGates below)
            "PASSPORT_CONTROL_DOORS",
            "PASSPORT_CONTROL_EMISSIVE",
            // the two portal gates of the z 50.5 facade (host raid 1, 2026-10-12): non-root paths into the main scene
            "SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (2)",
            "SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (3)",
            // the six closed shop shells of the ShootOut build (furnished shops imported from Arena_Airpit_Main_hall,
            // 2026-10-12 late): the first BUILDING sibling under SOO_LOD0, no frame-named parts
            "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/Airport_Shop_BS_closed_closed",
            "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed",
            "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed (1)",
            "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_01_closed",
            "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_02_closed",
            "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_03_closed",
            // the three layer-0 blockout panels over the south-west shop's roll-door lines (host raid 2026-10-13)
            "SBG_Arena_AirPit_ShootOut/SOO/Cube (10) Group",
        }, ArenaMapRegistry.AirPit.OpenedBarriers);
        var walked = new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5, ArenaMapRegistry.AirPit };
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            Assert.NotNull(map.OpenedBarriers);
            Assert.All(map.OpenedBarriers, path => Assert.True(IsHierarchyPath(path), $"{map.Key}: malformed opened_barriers path '{path}'"));
            Assert.Equal(map.OpenedBarriers.Length, map.OpenedBarriers.Distinct().Count());
            if (!walked.Contains(map))
            {
                Assert.Empty(map.OpenedBarriers);
            }
        }
    }

    // A hierarchy path as BoothDoorRepairs.FindByPath and BakeNavMesh.ResolveHierarchyPaths read it.
    private static bool IsHierarchyPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Split('/').All(segment => segment.Length > 0 && segment == segment.Trim());

    // 2026-10-12 (host raid 4): AirPit's invisible walls follow Arena's TeamFight outline, a closed ring of twelve
    // segments whose bounding box is the bake volume. AuthoredBorderRepairs raises one box per segment at raid start.
    [Fact]
    public void AuthoredBorders_FormAirPitsClosedOutline_OtherMapsNone()
    {
        var borders = ArenaMapRegistry.AirPit.AuthoredBorders;
        Assert.Equal(12, borders.Length);
        Assert.Equal(borders.Length, borders.Select(b => b.Name).Distinct().Count());
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (var i = 0; i < borders.Length; i++)
        {
            var b = borders[i];
            var next = borders[(i + 1) % borders.Length];
            Assert.True(b.Name.Length > 0 && b.Name.Trim() == b.Name, b.Name);
            Assert.All(new[] { b.X0, b.Z0, b.X1, b.Z1, b.MinY, b.MaxY }, v => Assert.True(float.IsFinite(v), b.Name));
            Assert.True(b.MinY < b.MaxY, b.Name);
            Assert.True(MathF.Sqrt((b.X1 - b.X0) * (b.X1 - b.X0) + (b.Z1 - b.Z0) * (b.Z1 - b.Z0)) >= 1f, $"{b.Name}: shorter than 1 m");
            Assert.True(b.X1 == next.X0 && b.Z1 == next.Z0, $"{b.Name} does not meet {next.Name}"); // closed ring
            minX = MathF.Min(minX, MathF.Min(b.X0, b.X1)); maxX = MathF.Max(maxX, MathF.Max(b.X0, b.X1));
            minZ = MathF.Min(minZ, MathF.Min(b.Z0, b.Z1)); maxZ = MathF.Max(maxZ, MathF.Max(b.Z0, b.Z1));
        }
        // Bots get no mesh beyond the walls: the bake volume hugs the outline's bounding box to within 1 m on every
        // edge (the north again since 2026-10-18: the original north team box lies behind the held passport gate).
        var bounds = ArenaMapRegistry.AirPit.BakeBounds;
        Assert.InRange(bounds[0], minX - 1f, minX + 1f); Assert.InRange(bounds[1], maxX - 1f, maxX + 1f);
        Assert.InRange(bounds[4], minZ - 1f, minZ + 1f); Assert.InRange(bounds[5], maxZ - 1f, maxZ + 1f);
        // Every spawn and exit of the map lies inside the outline's box (the walls must not shut a spawn out).
        foreach (var exit in ArenaMapRegistry.AirPit.Exits)
        {
            Assert.InRange(exit.X, minX, maxX); Assert.InRange(exit.Z, minZ, maxZ);
        }
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => !ReferenceEquals(m, ArenaMapRegistry.AirPit)))
        {
            Assert.Empty(map.AuthoredBorders);
        }
    }

    // 2026-10-13: the passport-hall lamp fill (LampFillRepairs) is AirPit's alone; every other map keeps the default (none),
    // so nothing is cloned on Equator, Bay5 or a parked map.
    [Fact]
    public void LampFill_ExistsForAirPitOnly()
    {
        Assert.NotNull(ArenaMapRegistry.AirPit.LampFill);
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => !ReferenceEquals(m, ArenaMapRegistry.AirPit)))
        {
            Assert.Null(map.LampFill);
        }
    }

    // PLAN-AIRPIT item 8: the bake-only inputs exist for AirPit alone; Equator's and Bay5's NavMesh bake keeps its
    // renderer-union volume, its every-layer mask and no exclusion, which is what their shipped bundles were built with.
    [Fact]
    public void NavMeshBakeInputs_ExistForAirPitOnly()
    {
        var airPit = ArenaMapRegistry.AirPit;
        // 2026-10-12 host raid 1: the four bake-only exclusions (shop shutters, a fence) let the NavMesh run through
        // solids that stay in the scene; the walk test stopped at the DonB_Ramen shutter. AirPit now bakes every
        // solid and opens the two portal gates instead (OpenedBarriers), so the list is empty by design. The six
        // closed shop shells joined the list when the furnished shops came in from Arena_Airpit_Main_hall, and the
        // layer-0 blockout panels over the south-west roll doors after the first host raid on that build.
        Assert.Empty(airPit.NavMeshBakeExclusions);
        Assert.Equal(12, airPit.OpenedBarriers.Length);
        Assert.All(airPit.OpenedBarriers, path => Assert.True(IsHierarchyPath(path), path));
        Assert.Equal(airPit.OpenedBarriers.Length, airPit.OpenedBarriers.Distinct().Count());
        Assert.Contains("PASSPORT_CONTROL_DOORS", airPit.OpenedBarriers); // the held north gate: out of the bake, closed at raid start
        // The AABB of the authored borders (Arena's TeamFight outline) rounded outwards (z 83 vs the outline's 83.6).
        Assert.Equal(new[] { -57.0f, 37.0f, -2.0f, 8.0f, -83.0f, 83.0f }, airPit.BakeBounds);
        // min < max per axis: [min_x, max_x, min_y, max_y, min_z, max_z]
        for (var axis = 0; axis < 3; axis++)
            Assert.True(airPit.BakeBounds[2 * axis] < airPit.BakeBounds[2 * axis + 1]);
        // A bake exclusion that is also a barrier would be dropped twice and say two different things about the runtime.
        Assert.DoesNotContain(airPit.NavMeshBakeExclusions, p => airPit.OpenedBarriers.Any(b => p == b || p.StartsWith(b + "/")));

        foreach (var map in ArenaMapRegistry.Catalog.Where(m => !ReferenceEquals(m, airPit)))
        {
            Assert.Empty(map.NavMeshBakeExclusions);
            Assert.Empty(map.BakeBounds);
            Assert.Empty(map.NavMeshBakeLayers);
        }
        Assert.All(airPit.NavMeshBakeLayers, layer => Assert.InRange(layer, 0, 31));
        Assert.DoesNotContain(4, airPit.NavMeshBakeLayers); // Water is never collected
    }

    [Fact]
    public void Registry_HasNoDuplicateKeysOrIdFields()
    {
        var keys = new HashSet<string>();
        var idFields = new HashSet<string>();
        foreach (var map in ArenaMapRegistry.Catalog)
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

    // 2026-10-18 (the original AirPit north team box): which opened barriers a mode plugin may hold (StartGates) and
    // which stay closed until SpawnGates.Release (HeldGates). Equator and Bay5 declare neither, so every barrier of
    // theirs is a start gate, exactly as before.
    [Fact]
    public void SpawnGateRoles_AirPitHoldsOnlyTheBusDoors_AndKeepsThePassportControlClosed()
    {
        var airPit = ArenaMapRegistry.AirPit;
        Assert.Equal(new[] { "BUS_DOORS" }, airPit.StartGates);
        Assert.Equal(new[] { "PASSPORT_CONTROL_DOORS", "PASSPORT_CONTROL_EMISSIVE" }, airPit.HeldGates);
        Assert.Equal(ArenaGateRole.Start, airPit.GateRoleOf("BUS_DOORS"));
        Assert.Equal(ArenaGateRole.Held, airPit.GateRoleOf("PASSPORT_CONTROL_DOORS"));
        Assert.Equal(ArenaGateRole.Held, airPit.GateRoleOf("PASSPORT_CONTROL_EMISSIVE"));
        Assert.Equal(9, airPit.OpenedBarriers.Count(p => airPit.GateRoleOf(p) == ArenaGateRole.Plain)); // fences, shells, panels
        Assert.Equal(ArenaGateRole.Plain, airPit.GateRoleOf("not a barrier at all"));
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            Assert.All(map.StartGates, p => Assert.Contains(p, map.OpenedBarriers));
            Assert.All(map.HeldGates, p => Assert.Contains(p, map.OpenedBarriers));
            Assert.Empty(map.StartGates.Intersect(map.HeldGates));
            if (ReferenceEquals(map, airPit)) continue;
            Assert.Empty(map.StartGates);
            Assert.Empty(map.HeldGates);
            Assert.All(map.OpenedBarriers, p => Assert.Equal(ArenaGateRole.Start, map.GateRoleOf(p)));
        }
    }
}
