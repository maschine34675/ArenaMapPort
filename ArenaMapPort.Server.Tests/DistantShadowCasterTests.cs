using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Distant sun shadow enrolment (RoofShadowRepairs, docs/progress/lighting-followup.md "2026-11-12"): EFT's DistantShadow draws only renderers that
/// a LODGroup references, so a shadow-only proxy outside any group has to be enrolled. The descriptor rows are the one list (Equator: three ceiling
/// proxies, two-sided material; Chop Shop: the two hall hulls, Arena's own material; Iceberg: the cafe's three storey shells, Arena's own
/// material, which stop low morning sun behind the east glazing). The real-data tests read the ignored extracted packages and
/// skip when they are absent; the source pins keep the repair descriptor-driven, developer-only and free of scene-wide searches in periodic code.
/// </summary>
public sealed class DistantShadowCasterTests
{
    private const string AutoServicePackageScene = "work/extract/autoservice-native-v2/scenes/Arena_AutoService.json";
    private const string IcebergPackageScene = "work/extract/iceberg-native-v2/scenes/Arena_Sanatorium.json";
    private const string YardPackageScenes = "work/extract/yard-native-v2/scenes";

    private static readonly string[] CafePaths =
    {
        "SBG_Arena_Sanatorium_Main/SOO_LOD0/CAFE/Sanatorium_Cafe_second_floor/Sanatorium_Cafe_second_floor_SHADOW_LOD0",
        "SBG_Arena_Sanatorium_Main/SOO_LOD0/CAFE/Sanatorium_Cafe_ground_floor/Sanatorium_Cafe_ground_floor_SHADOW_LOD0",
        "SBG_Arena_Sanatorium_Main/OO/CAFE/PROPS/Sanatorium_Cafe_constructions/Sanatorium_Cafe_constructions_SHADOW_LOD0",
    };

    private static readonly string[] HallPaths =
    {
        "SBG_Arena_AutoService/SOO_LOD0/Autoservice_Main_Building/Arena_AutoService_OUTDOOR/AutoService_Building_OUTDOOR_SHADOW_LOD0",
        "SBG_Arena_AutoService/SOO_LOD0/Autoservice_Main_Building/Arena_AutoService_INDOOR/AutoService_Building_INDOOR_SHADOW_LOD0",
    };

    // Yard (PLAN-YARD item 21, offline half, 2026-11-21): 15 hulls chosen with the census on work/extract/yard-native-v2, not host-verified.
    private static readonly string[] YardPaths =
    {
        "SBG_Arena_Yard/SOO_LOD0/Yard_Building_03/Benya_House_01_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Mansion_05/Mansion_05_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Primorskiy_43 (2) Group/Building_Panel_Devastated_01/Building_Panel_Devastated_01_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Respawn_Outdoor/Respawn_Outdoor_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Building_factory_brick_03/Building_factory_brick_03_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Mansion_02/Mansion_02_SHADOW_LOD0",
        "STENCILS/Yard_STENCIL",
        "SBG_Arena_Yard/SOO_LOD0/Building_factory_brick_02/Building_factory_brick_02_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Primorskiy_43 (2) Group/Garage_array_02_outdoor/Garage_array_02_outdoor_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Mansion_01/Mansion_01_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Mansion_04/Mansion_04_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Primorskiy_43 (2) Group/Garage_array_04_outdoor/Garage_array_04_outdoor_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Garage_array_03/Garage_array_03_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Garage_array_05/Garage_array_05_SHADOW_LOD0",
        "SBG_Arena_Yard/SOO_LOD0/Boiler_room_brick/Boiler_room_brick_SHADOW_LOD0",
    };

    private static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? FindWork(string relative)
    {
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var path = Path.Combine(env, relative.StartsWith("work/") ? relative.Substring(5) : relative);
            if (File.Exists(path)) return path;
        }
        return FindUp(relative);
    }

    private static string Source(string relative) =>
        File.ReadAllText(FindUp(relative) ?? throw new FileNotFoundException(relative));

    [Fact]
    public void OnlyEquatorIcebergAutoServiceAndYardEnrolProxies()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            if (map != ArenaMapRegistry.Equator) Assert.False(map.DistantShadowTwoSided);
            if (map == ArenaMapRegistry.Equator || map == ArenaMapRegistry.AutoService || map == ArenaMapRegistry.Iceberg
                || map == ArenaMapRegistry.Yard) continue;
            Assert.Empty(map.DistantShadowCasterPaths);
        }
    }

    [Fact]
    public void EquatorKeepsItsThreeCeilingProxiesAndTheTwoSidedMaterial()
    {
        var map = ArenaMapRegistry.Equator;
        Assert.Equal(3, map.DistantShadowCasterPaths.Length);
        Assert.True(map.DistantShadowTwoSided);
        Assert.All(map.DistantShadowCasterPaths, p => Assert.StartsWith("SBG_Arena_Equator/SOO_LOD0/STRUCTURES/Floors/", p));
    }

    [Fact]
    public void AutoServiceListsTheTwoHallHullsWithArenasOwnMaterial()
    {
        var map = ArenaMapRegistry.AutoService;
        Assert.Equal(HallPaths, map.DistantShadowCasterPaths);
        // Geometry (census 2026-11-12): the OUTDOOR hull is a one-sided outward shell whose roof faces up, so the original back-face-culling
        // shadow shader already blocks the sun; the distant map draws what the cascades draw. A change needs a new census and a host A/B.
        Assert.False(map.DistantShadowTwoSided);
        Assert.Contains(ArenaMapRegistry.AutoService, ArenaMapRegistry.All);
        Assert.All(map.DistantShadowCasterPaths, p => Assert.EndsWith("_SHADOW_LOD0", p));
        Assert.All(map.DistantShadowCasterPaths, p => Assert.StartsWith("SBG_Arena_AutoService/", p));
        Assert.Equal(map.DistantShadowCasterPaths.Length, map.DistantShadowCasterPaths.Distinct().Count());
    }

    [Fact]
    public void AutoServiceSidecarMirrorsTheDescriptor()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindUp("tools/maps/autoservice.json")!));
        var paths = doc.RootElement.GetProperty("distant_shadow_caster_paths").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(ArenaMapRegistry.AutoService.DistantShadowCasterPaths, paths);
    }

    [Fact]
    public void IcebergListsTheThreeCafeShellsWithArenasOwnMaterial()
    {
        var map = ArenaMapRegistry.Iceberg;
        Assert.Equal(CafePaths, map.DistantShadowCasterPaths);
        // Census and host A/B of 2026-11-12 (shadow quality Low): the roof and the facades are in EFT's list, but low morning sun through the
        // east glazing meets these slabs first. The stopping faces are slab tops, so Arena's one-sided material is enough. The hallway and
        // library shells lie under listed casters and stay out; a change needs tools/validate/distant_shadow_census.py and a host A/B.
        Assert.False(map.DistantShadowTwoSided);
        Assert.Contains(ArenaMapRegistry.Iceberg, ArenaMapRegistry.All);
        Assert.All(map.DistantShadowCasterPaths, p => Assert.EndsWith("_SHADOW_LOD0", p));
        Assert.All(map.DistantShadowCasterPaths, p => Assert.StartsWith("SBG_Arena_Sanatorium_Main/", p));
        Assert.All(map.DistantShadowCasterPaths, p => Assert.Contains("/CAFE/", p));
        Assert.Equal(map.DistantShadowCasterPaths.Length, map.DistantShadowCasterPaths.Distinct().Count());
    }

    [Fact]
    public void IcebergSidecarMirrorsTheDescriptor()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindUp("tools/maps/iceberg.json")!));
        var paths = doc.RootElement.GetProperty("distant_shadow_caster_paths").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(ArenaMapRegistry.Iceberg.DistantShadowCasterPaths, paths);
    }

    [Fact]
    public void YardListsTheFifteenBuildingHullsWithArenasOwnMaterial()
    {
        var map = ArenaMapRegistry.Yard;
        Assert.Equal(YardPaths, map.DistantShadowCasterPaths);
        Assert.Equal(15, map.DistantShadowCasterPaths.Length);
        Assert.Equal(map.DistantShadowCasterPaths.Length, map.DistantShadowCasterPaths.Distinct().Count());
        // Census model on the package (2026-11-21): the proxies are closed outward shells, so Arena's one-sided material is enough and the
        // distant map draws what the cascades draw. A change needs tools/validate/distant_shadow_census.py and a host A/B.
        Assert.False(map.DistantShadowTwoSided);
        Assert.Contains(ArenaMapRegistry.Yard, ArenaMapRegistry.All);
        Assert.All(map.DistantShadowCasterPaths, p => Assert.True(p.EndsWith("_SHADOW_LOD0", StringComparison.Ordinal) || p == "STENCILS/Yard_STENCIL", p));
    }

    [Fact]
    public void YardNeverEnrolsAMovingPartTheFloorPlaneOrAPropUnderThreeMetres()
    {
        var map = ArenaMapRegistry.Yard;
        // DistantShadow freezes a draw transform at list build: nothing SpawnGates moves (the DOORS barriers) may be listed.
        foreach (var path in map.DistantShadowCasterPaths)
        {
            Assert.DoesNotContain(map.OpenedBarriers, b => path == b || path.StartsWith(b + "/", StringComparison.Ordinal));
            Assert.DoesNotContain("Cradle", path);
            Assert.DoesNotContain("YARD_Floor", path);   // the 307 x 278 m courtyard plane stays out (acne risk, nothing to shade)
            Assert.DoesNotContain("Imposter", path);     // simplified copies of a listed hull
            Assert.DoesNotContain("inposter", path);
            Assert.DoesNotContain("Cloth", path);        // no native pass-0 precedent for the cloth shader
            Assert.False(path.StartsWith("VARIANT/", StringComparison.Ordinal), path); // props under 3 m
        }
    }

    [Fact]
    public void YardSidecarMirrorsTheDescriptor()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindUp("tools/maps/yard.json")!));
        var paths = doc.RootElement.GetProperty("distant_shadow_caster_paths").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(ArenaMapRegistry.Yard.DistantShadowCasterPaths, paths);
    }

    private sealed record Node(string Id, string? Parent, string Name, bool Active, JsonElement Components);

    [RealAutoServicePackageFact]
    public void EachHallPathExistsOnceAsAnActiveShadowsOnlyRendererNoLodGroupReferences() =>
        AssertEnrollable(AutoServicePackageScene, ArenaMapRegistry.AutoService.DistantShadowCasterPaths);

    [RealYardPackageFact]
    public void EachYardPathExistsOnceInOneSceneAsAnActiveShadowsOnlyRendererNoLodGroupReferences()
    {
        // The client resolves a path in every owned scene, so it must exist in exactly one of the scenes of the package.
        var dir = Path.GetDirectoryName(FindWork(YardPackageScenes + "/Arena_Yard.json")!)!;
        var scenes = Directory.GetFiles(dir, "*.json").Select(f => (Name: Path.GetFileNameWithoutExtension(f), File: f)).ToList();
        Assert.Contains(scenes, s => s.Name == "Arena_Yard_Light");
        foreach (var path in ArenaMapRegistry.Yard.DistantShadowCasterPaths)
        {
            var found = scenes.Where(s => AssertEnrollable(s.File, new[] { path }, mustExist: false)).Select(s => s.Name).ToList();
            Assert.True(found.Count == 1, path + " resolves in " + found.Count + " scenes: " + string.Join(", ", found));
        }
        // Yard_STENCIL is the one row outside Arena_Yard; the Light scene is among the descriptor's owned scenes.
        Assert.Contains("Arena_Yard_Light", ArenaMapRegistry.Yard.AllSceneNames);
    }

    [RealIcebergPackageFact]
    public void EachCafePathExistsOnceAsAnActiveShadowsOnlyRendererNoLodGroupReferences() =>
        AssertEnrollable(IcebergPackageScene, ArenaMapRegistry.Iceberg.DistantShadowCasterPaths);

    // What RoofShadowRepairs.Apply demands of a path: one object of that hierarchy path, active up the chain, an enabled ShadowsOnly
    // MeshRenderer with a MeshFilter, no LODGroup on it or above it, and no LODGroup of the scene referencing it. Its Find takes the FIRST
    // root of the first name and then Transform.Find, so every step of the path must be the only sibling of its name (order-independent:
    // a repeated name is refused here even where the first sibling would happen to be the right one).
    private static void AssertEnrollable(string packageScene, string[] paths) =>
        AssertEnrollable(FindWork(packageScene)!, paths, mustExist: true);

    // Returns true when every path was found in this scene (mustExist false: a path that is absent here is not a failure, a path that is
    // present must still satisfy every rule). Several hits of one path in a scene always fail.
    private static bool AssertEnrollable(string sceneFile, string[] paths, bool mustExist)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(sceneFile));
        var nodes = doc.RootElement.GetProperty("objects").EnumerateArray().Select(o => new Node(
            o.GetProperty("id").GetString()!,
            o.TryGetProperty("parent_id", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
            o.GetProperty("name").GetString()!, o.GetProperty("active").GetBoolean(), o.GetProperty("components"))).ToList();
        var byId = nodes.ToDictionary(n => n.Id);
        var referenced = new HashSet<string>();
        var lodGroupOwners = new HashSet<string>();
        foreach (var n in nodes)
            foreach (var c in n.Components.EnumerateArray())
                if (c.GetProperty("type").GetString() == "LODGroup")
                {
                    lodGroupOwners.Add(n.Id);
                    foreach (var lod in c.GetProperty("lods").EnumerateArray())
                        foreach (var id in lod.GetProperty("renderer_object_ids").EnumerateArray()) referenced.Add(id.GetString()!);
                }

        Node? Up(Node n) => n.Parent != null && byId.TryGetValue(n.Parent, out var up) ? up : null;
        string PathOf(Node n)
        {
            var parts = new List<string>();
            for (Node? cur = n; cur != null; cur = Up(cur)) parts.Add(cur.Name);
            parts.Reverse();
            return string.Join('/', parts);
        }

        Assert.NotEmpty(paths);
        bool all = true;
        foreach (var path in paths)
        {
            var hits = nodes.Where(n => PathOf(n) == path).ToList();
            if (hits.Count == 0 && !mustExist) { all = false; continue; }
            Assert.Single(hits);
            var node = hits[0];
            for (Node? cur = node; cur != null; cur = Up(cur))
            {
                Assert.True(cur.Active, path + " is inactive at " + cur.Name);
                Assert.DoesNotContain(cur.Id, lodGroupOwners); // GetComponentInParent<LODGroup> would skip the path
                int sameName = nodes.Count(n => n.Parent == cur.Parent && n.Name == cur.Name);
                Assert.True(sameName == 1, path + ": " + sameName + " siblings are named '" + cur.Name + "'");
            }
            Assert.DoesNotContain(node.Id, referenced);
            var renderer = node.Components.EnumerateArray().Single(c => c.GetProperty("type").GetString() == "MeshRenderer");
            Assert.Equal("ShadowsOnly", renderer.GetProperty("cast_shadows").GetString());
            Assert.True(renderer.GetProperty("enabled").GetBoolean());
            Assert.Contains(node.Components.EnumerateArray(), c => c.GetProperty("type").GetString() == "MeshFilter");
        }
        return all;
    }

    [Fact]
    public void TheRepairIsDescriptorDrivenAndDeveloperOnly()
    {
        var text = Source("ArenaMapPort.Client/RoofShadowRepairs.cs");
        Assert.Null(FindUp("ArenaMapPort.Client/EquatorRoofShadowRepairs.cs")); // renamed, not duplicated
        Assert.DoesNotContain("ArenaMapRegistry.Equator", text);
        Assert.Contains("map.DistantShadowCasterPaths", text);
        Assert.Contains("map.DistantShadowTwoSided", text);
        Assert.Contains("const string Tag = \"[RoofShadow]\";", text);
        // one bind, the existing key name and default, in the per-map lighting section
        Assert.Equal(1, Count(text, ".Bind("));
        Assert.Contains("\"Lighting (\" + map.Key + \")\"", text);
        Assert.Contains("\"RoofDistantShadows\", true", text);
        // Yard's text is its own (an outdoor map; the hall wording would be wrong), the other maps' texts are untouched
        Assert.Contains("map == ArenaMapRegistry.Yard", text);
        Assert.Contains("so the buildings keep casting sun shadows beyond the cascade distance.", text);
        Assert.Contains("not light the interior through the roof beyond the cascade distance. Keeps Arena's own shadow material. ", text);
        // Equator's config text is unchanged since the first release of the key
        Assert.Contains("Make Equator's original solid ceiling shadows two-sided and include them in the native sun-shadow cache. ", text);
        Assert.Contains("Retains the original atrium openings. Live toggle; the native cache takes a few seconds to refresh.", text);
        // the install is one call in Plugin and the probe one call in LightCullingProbe
        Assert.Contains("RoofShadowRepairs.Install();", Source("ArenaMapPort.Client/Plugin.cs"));
        Assert.Contains("RoofShadowRepairs.Probe();", Source("ArenaMapPort.Client/LightCullingProbe.cs"));
        // never a public key
        Assert.DoesNotContain("RoofDistantShadows", Source("ArenaMapPort.Client/CompactConfiguration.cs"));
        Assert.DoesNotContain("RoofDistantShadows", Source("ArenaMapPort.Client/MapConfigVisibility.cs"));
    }

    [Fact]
    public void SceneWideSearchesStayInTheDeveloperProbe()
    {
        var text = Source("ArenaMapPort.Client/RoofShadowRepairs.cs");
        int probe = text.IndexOf("internal static void Probe()", StringComparison.Ordinal);
        Assert.True(probe > 0);
        foreach (var search in new[] { "FindObjectsOfType", "FindObjectOfType", "GetComponentsInChildren", "FindObjectsOfTypeAll" })
        {
            int at = text.IndexOf(search, StringComparison.Ordinal);
            if (at >= 0) Assert.True(at > probe, search + " outside Probe");
        }
        Assert.DoesNotContain("void Update(", text);
        Assert.DoesNotContain("IEnumerator", text);
        Assert.Contains("if (!DeveloperOptions.Enabled) return;", text.Substring(probe));
    }

    private static int Count(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

/// <summary>Runs only when the ignored extracted Chop Shop package is present (checkout work folder or ARENAMAPPORT_WORK_DIR).</summary>
internal sealed class RealAutoServicePackageFactAttribute : FactAttribute
{
    public RealAutoServicePackageFactAttribute()
    {
        const string relative = "work/extract/autoservice-native-v2/scenes/Arena_AutoService.json";
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, relative.Substring(5)))) return;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return;
        Skip = relative + " is absent (BSG-derived, ignored by git; run the Autoservice extraction)";
    }
}

/// <summary>Runs only when the ignored extracted Iceberg package is present (checkout work folder or ARENAMAPPORT_WORK_DIR).</summary>
internal sealed class RealIcebergPackageFactAttribute : FactAttribute
{
    public RealIcebergPackageFactAttribute()
    {
        const string relative = "work/extract/iceberg-native-v2/scenes/Arena_Sanatorium.json";
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, relative.Substring(5)))) return;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return;
        Skip = relative + " is absent (BSG-derived, ignored by git; run the Iceberg extraction)";
    }
}

/// <summary>Runs only when the ignored extracted Yard package is present (checkout work folder or ARENAMAPPORT_WORK_DIR).</summary>
internal sealed class RealYardPackageFactAttribute : FactAttribute
{
    public RealYardPackageFactAttribute()
    {
        const string relative = "work/extract/yard-native-v2/scenes/Arena_Yard.json";
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, relative.Substring(5)))) return;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return;
        Skip = relative + " is absent (BSG-derived, ignored by git; run the Yard extraction)";
    }
}
