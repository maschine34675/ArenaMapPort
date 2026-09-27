using System.Text.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-TWO-MAPS item 8 (docs/progress/two-maps-item-8-ai-anchors.md) — checks the two AI-anchors
/// sidecars tools/extract/extract_ai_anchors.py writes (tools/maps/equator.ai.json,
/// tools/maps/bay5.ai.json) directly, the same way Bay5MapTests.cs checks bay5.json/bay5.exits.json:
/// parsed as plain JSON here (no reference to ArenaMapPort.Client/AiAnchors.cs, which needs the real
/// UnityEngine/Newtonsoft assemblies this net10.0 test project does not have) against the counts the
/// 2026-09-27 research pass verified (14 core points / 2 hubs+21 sub points on Equator, 11 core
/// points / 4 hubs+43 sub points on Bay5) and the invariants both ArenaMapPort.Client/AiAnchors.cs'
/// parser and the NavMesh bake (BakeNavMesh, y -8..12) rely on.
/// </summary>
public sealed class AiAnchorsTests
{
    const float NavMeshBakeMinY = -8f;
    const float NavMeshBakeMaxY = 12f;

    private static string Sidecar(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "maps", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    [Theory]
    [InlineData("equator.ai.json", 14, 2, 21)]
    [InlineData("bay5.ai.json", 11, 4, 43)]
    public void CountsMatchTheResearchPass(string file, int expectedCorePoints, int expectedHubs, int expectedSubPoints)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(file)));
        Assert.Equal(expectedCorePoints, doc.RootElement.GetProperty("core_points").GetArrayLength());

        var hubs = doc.RootElement.GetProperty("patrol_ways").EnumerateArray()
            .SelectMany(way => way.GetProperty("hubs").EnumerateArray()).ToArray();
        Assert.Equal(expectedHubs, hubs.Length);
        Assert.Equal(expectedSubPoints, hubs.Sum(hub => hub.GetProperty("sub_points").GetArrayLength()));
    }

    [Theory]
    [InlineData("equator.ai.json")]
    [InlineData("bay5.ai.json")]
    public void CorePointIdsAreUniqueWithFinitePositionsInTheBakeVolume(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(file)));
        var seenIds = new HashSet<int>();
        foreach (var corePoint in doc.RootElement.GetProperty("core_points").EnumerateArray())
        {
            var id = corePoint.GetProperty("id").GetInt32();
            // Id 0 is reserved for the client's synthetic alias (SpawnPointParams.CorePointId is always 0);
            // AICorePointFallback skips the alias when a real id 0 exists, so a re-extraction must never ship one.
            Assert.True(id >= 1, $"{file}: core point id {id} collides with the synthetic id-0 alias");
            Assert.True(seenIds.Add(id), $"{file}: duplicate core point id {id}");
            AssertFiniteAndWithinBakeVolume(corePoint.GetProperty("position"), file);
        }
    }

    [Theory]
    [InlineData("equator.ai.json")]
    [InlineData("bay5.ai.json")]
    public void EveryHubCorePointIdExistsAndEveryPositionIsValid(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(file)));
        var knownIds = doc.RootElement.GetProperty("core_points").EnumerateArray()
            .Select(cp => cp.GetProperty("id").GetInt32()).ToHashSet();
        Assert.NotEmpty(knownIds);

        foreach (var way in doc.RootElement.GetProperty("patrol_ways").EnumerateArray())
        {
            foreach (var hub in way.GetProperty("hubs").EnumerateArray())
            {
                var corePointId = hub.GetProperty("core_point_id").GetInt32();
                Assert.Contains(corePointId, knownIds);
                AssertFiniteAndWithinBakeVolume(hub.GetProperty("position"), file);
                foreach (var subPoint in hub.GetProperty("sub_points").EnumerateArray())
                {
                    AssertFiniteAndWithinBakeVolume(subPoint, file);
                }
            }
        }
    }

    [Theory]
    [InlineData("equator.ai.json")]
    [InlineData("bay5.ai.json")]
    public void NoConnectionOrEdgeFieldsAreShipped(string file)
    {
        // Deliberate -- extract_ai_anchors.py's own module docstring: AICorePoint.ConnectionsAtNet is
        // only ever populated by AICorePointHolder.DrawInner, itself only reachable from the Unity
        // Editor's OnDrawGizmos/OnDrawGizmosSelected, which never fire in a built client or a live raid.
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar(file)));
        foreach (var corePoint in doc.RootElement.GetProperty("core_points").EnumerateArray())
        {
            Assert.False(corePoint.TryGetProperty("connections_at_net", out _));
            Assert.False(corePoint.TryGetProperty("connection_ids", out _));
            Assert.False(corePoint.TryGetProperty("connection_group_ids", out _));
        }
    }

    private static void AssertFiniteAndWithinBakeVolume(JsonElement position, string file)
    {
        var values = position.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Assert.Equal(3, values.Length);
        foreach (var value in values)
        {
            Assert.True(float.IsFinite(value), $"{file}: non-finite position component {value}");
        }
        Assert.InRange(values[1], NavMeshBakeMinY, NavMeshBakeMaxY); // BakeNavMesh's own bake volume (AGENTS.md "Traps")
    }
}
