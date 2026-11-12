using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Distant sun shadow enrolment (RoofShadowRepairs, docs/progress/lighting-followup.md "2026-11-12"): EFT's DistantShadow draws only renderers that
/// a LODGroup references, so a shadow-only proxy outside any group has to be enrolled. The descriptor rows are the one list (Equator: three ceiling
/// proxies, two-sided material; Chop Shop: the two hall hulls, Arena's own material). The real-data test reads the ignored extracted package and
/// skips when it is absent; the source pins keep the repair descriptor-driven, developer-only and free of scene-wide searches in periodic code.
/// </summary>
public sealed class DistantShadowCasterTests
{
    private const string AutoServicePackageScene = "work/extract/autoservice-native-v2/scenes/Arena_AutoService.json";

    private static readonly string[] HallPaths =
    {
        "SBG_Arena_AutoService/SOO_LOD0/Autoservice_Main_Building/Arena_AutoService_OUTDOOR/AutoService_Building_OUTDOOR_SHADOW_LOD0",
        "SBG_Arena_AutoService/SOO_LOD0/Autoservice_Main_Building/Arena_AutoService_INDOOR/AutoService_Building_INDOOR_SHADOW_LOD0",
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
    public void OnlyEquatorAndAutoServiceEnrolProxies()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            if (map == ArenaMapRegistry.Equator || map == ArenaMapRegistry.AutoService) continue;
            Assert.Empty(map.DistantShadowCasterPaths);
            Assert.False(map.DistantShadowTwoSided);
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

    private sealed record Node(string Id, string? Parent, string Name, bool Active, JsonElement Components);

    [RealAutoServicePackageFact]
    public void EachHallPathExistsOnceAsAnActiveShadowsOnlyRendererNoLodGroupReferences()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindWork(AutoServicePackageScene)!));
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

        foreach (var path in ArenaMapRegistry.AutoService.DistantShadowCasterPaths)
        {
            var hits = nodes.Where(n => PathOf(n) == path).ToList();
            Assert.Single(hits);
            var node = hits[0];
            for (Node? cur = node; cur != null; cur = Up(cur))
            {
                Assert.True(cur.Active, path + " is inactive at " + cur.Name);
                Assert.DoesNotContain(cur.Id, lodGroupOwners); // GetComponentInParent<LODGroup> would skip the path
            }
            Assert.DoesNotContain(node.Id, referenced);
            var renderer = node.Components.EnumerateArray().Single(c => c.GetProperty("type").GetString() == "MeshRenderer");
            Assert.Equal("ShadowsOnly", renderer.GetProperty("cast_shadows").GetString());
            Assert.True(renderer.GetProperty("enabled").GetBoolean());
            Assert.Contains(node.Components.EnumerateArray(), c => c.GetProperty("type").GetString() == "MeshFilter");
        }
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
