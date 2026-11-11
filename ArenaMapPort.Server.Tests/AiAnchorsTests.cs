using System.Text.Json;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-TWO-MAPS item 8 (docs/progress/two-maps-item-8-ai-anchors.md) — checks the AI-anchors
/// sidecars tools/extract/extract_ai_anchors.py writes (tools/maps/equator.ai.json,
/// tools/maps/bay5.ai.json and, since PLAN-AIRPIT item 20, tools/maps/airpit.ai.json; since PLAN-ICEBERG item 7 also
/// tools/maps/iceberg.ai.json, which has core points only) directly, the same
/// way Bay5MapTests.cs checks bay5.json/bay5.exits.json:
/// parsed as plain JSON here (no reference to ArenaMapPort.Client/AiAnchors.cs, which needs the real
/// UnityEngine/Newtonsoft assemblies this net10.0 test project does not have) against the counts the
/// 2026-09-27 research pass verified (14 core points / 2 hubs+21 sub points on Equator, 11 core
/// points / 4 hubs+43 sub points on Bay5) and the 2026-10-11 AirPit extraction (19 core points /
/// 3 hubs+35 sub points), plus the invariants both ArenaMapPort.Client/AiAnchors.cs'
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
    [InlineData("airpit.ai.json", 19, 3, 35)] // PLAN-AIRPIT item 20: 19 core points, hubs 12/12/11 (id 7 sits in the closed north pocket, an island like Equator's 13/14)
    [InlineData("iceberg.ai.json", 11, 0, 0)] // PLAN-ICEBERG item 7: 11 core points; the scene's 4 hubs / 40 sub points lie off the map and are not shipped
    [InlineData("autoservice.ai.json", 7, 5, 54)] // PLAN-AUTOSERVICE item 6: 7 core points, one 5-hub PatrolWay with 54 sub points, all on the map
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
    [InlineData("airpit.ai.json")]
    [InlineData("iceberg.ai.json")]
    [InlineData("autoservice.ai.json")]
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
    [InlineData("airpit.ai.json")]
    [InlineData("iceberg.ai.json")]
    [InlineData("autoservice.ai.json")]
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
    [InlineData("airpit.ai.json")]
    [InlineData("iceberg.ai.json")]
    [InlineData("autoservice.ai.json")]
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

    [Fact]
    public void AirPitAnchorsSitInsideTheDescriptorBakeBounds()
    {
        // PLAN-AIRPIT item 20. AirPit's bake volume is descriptor data (x -80..56, y -2..8, z -96..92: ground floor
        // plus wing mezzanine, user decision 2026-10-11), tighter than the -8..12 the theories above check for every
        // map. An anchor outside it could never be on the NavMesh the bots walk (the client snaps core points to it).
        var bounds = ArenaMapRegistry.AirPit.BakeBounds;
        Assert.Equal(6, bounds.Length);
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("airpit.ai.json")));
        // Core point 7 (-8.35, 0.02, 76.81) is Arena's anchor in the north spawn pocket. Since 2026-10-17 the pocket is
        // meshed again (the original north team box, bake box z 83) but sealed by the held passport gate's carvers in
        // every raid without a mode plugin's release, so it stays an island for bots like Equator's 13/14; the sidecar
        // keeps it because it is the extractor's reproducible output (from 2026-10-12 to 10-18 the box ended at z 70).
        // Core points 16, 17 and 18 sat inside the closed shop fronts of the U4 bake (2026-10-12, host raid 8 dry runs) and
        // were islands then; since the furnished shops of level38 came in and the six shells became opened_barriers (U6,
        // 2026-10-13) they are on the main component again and the bot spawns are back on Arena's anchors
        // (tools/maps/airpit.bots.json, tools/tests/test_airpit_spawns.py). The core points were never edited.
        var corePoints = doc.RootElement.GetProperty("core_points").EnumerateArray().ToList();
        var island = Assert.Single(corePoints, cp => cp.GetProperty("id").GetInt32() == 7);
        Assert.InRange(island.GetProperty("position")[2].GetSingle(), 75f, 80f);
        var positions = corePoints.Where(cp => cp.GetProperty("id").GetInt32() != 7).Select(cp => cp.GetProperty("position")).ToList();
        foreach (var hub in doc.RootElement.GetProperty("patrol_ways").EnumerateArray()
                     .SelectMany(way => way.GetProperty("hubs").EnumerateArray()))
        {
            positions.Add(hub.GetProperty("position"));
            positions.AddRange(hub.GetProperty("sub_points").EnumerateArray());
        }
        Assert.Equal(18 + 3 + 35, positions.Count);
        foreach (var position in positions)
        {
            var p = position.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            Assert.InRange(p[0], bounds[0], bounds[1]);
            Assert.InRange(p[1], bounds[2], bounds[3]);
            Assert.InRange(p[2], bounds[4], bounds[5]);
        }
    }

    [Fact]
    public void IcebergShipsNoPatrolWayAndRecordsTheOffMapHubsItDropped()
    {
        // PLAN-ICEBERG item 7 (extract_ai_anchors.py, PLAY_AREA): Iceberg's AI scene carries a 4-hub PatrolWay (40 sub points,
        // z 5..24) and ten Savage markers 40 to 60 m north of the border cubes and off Arena's NavMesh. patrol_ways is [], so
        // PatrolWayFallback.EnsurePatrolWays builds NavMesh-derived ways from the Bot spawn markers (AiAnchors.PatrolWays.Count == 0).
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("iceberg.ai.json")));
        Assert.Equal(0, doc.RootElement.GetProperty("patrol_ways").GetArrayLength());
        var dropped = doc.RootElement.GetProperty("dropped");
        Assert.Equal(4, dropped.GetProperty("hubs").GetInt32());
        Assert.Equal(40, dropped.GetProperty("sub_points").GetInt32());
        Assert.Equal(1, dropped.GetProperty("ways_removed").GetInt32());
        // every kept core point lies inside the play area box of the drop rule, and the released sidecars carry no such record
        var area = dropped.GetProperty("play_area");
        var x = area.GetProperty("x").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var z = area.GetProperty("z").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        foreach (var corePoint in doc.RootElement.GetProperty("core_points").EnumerateArray())
        {
            var p = corePoint.GetProperty("position").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            Assert.InRange(p[0], x[0], x[1]);
            Assert.InRange(p[2], z[0], z[1]);
        }
        foreach (var released in new[] { "equator.ai.json", "bay5.ai.json", "airpit.ai.json", "bowl.ai.json" })
        {
            using var other = JsonDocument.Parse(File.ReadAllText(Sidecar(released)));
            Assert.False(other.RootElement.TryGetProperty("dropped", out _), released);
            Assert.True(other.RootElement.GetProperty("patrol_ways").GetArrayLength() > 0, released);
        }
    }

    [Fact]
    public void IcebergsPlayAreaOfTheDropRuleIsInsideTheDescriptorBakeBoundsAndAllItsAnchorsSitInThem()
    {
        // Review of 2026-10-30: extract_ai_anchors.PLAY_AREA is a hand-written stand-in for the bake box of item 9; the descriptor's BakeBounds
        // (x -62..121, y -3..8, z -131..-33) now exists, so tie them: the play area (x -61..121, z -130..-33) lies inside the bake box, and every
        // anchor the sidecar keeps lies inside the bake box in all three axes.
        var bounds = ArenaMapRegistry.Iceberg.BakeBounds;
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("iceberg.ai.json")));
        var area = doc.RootElement.GetProperty("dropped").GetProperty("play_area");
        var x = area.GetProperty("x").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var z = area.GetProperty("z").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Assert.True(bounds[0] <= x[0] && x[1] <= bounds[1], "play area x outside the bake box");
        Assert.True(bounds[4] <= z[0] && z[1] <= bounds[5], "play area z outside the bake box");
        var corePoints = doc.RootElement.GetProperty("core_points").EnumerateArray().ToList();
        Assert.Equal(11, corePoints.Count);
        foreach (var corePoint in corePoints)
        {
            var p = corePoint.GetProperty("position").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            Assert.InRange(p[0], bounds[0], bounds[1]);
            Assert.InRange(p[1], bounds[2], bounds[3]);
            Assert.InRange(p[2], bounds[4], bounds[5]);
        }
    }

    [Fact]
    public void AutoserviceShipsArenasSevenCoreFiveHubsAndTheAiSceneDataWithinItsPlayArea()
    {
        // PLAN-AUTOSERVICE item 6 (extract_ai_anchors.py, PLAY_AREA / AI_SCENE_DATA): everything Arena authored for bots in
        // Arena_AutoService_AI lies on the map (nothing is dropped, so there is no "dropped" record), and the sidecar adds the
        // 16 bot born points (SpawnPointMarker "p1"), the 23 AIMinePoints (AI data, not minefields) and a census of the
        // far-field leftovers (beyond 400 m, never exported). Autoservice is Catalog-only: this pins the sidecar, not the release.
        using var doc = JsonDocument.Parse(File.ReadAllText(Sidecar("autoservice.ai.json")));
        Assert.False(doc.RootElement.TryGetProperty("dropped", out _));
        Assert.Equal(16, doc.RootElement.GetProperty("born_points").GetArrayLength());
        Assert.Equal(23, doc.RootElement.GetProperty("mine_points").GetArrayLength());
        // Arena's NavMeshSurface volume rounded outward (the extractor's PLAY_AREA row): x -421.2..-319.4, z 445.9..527.7
        var anchors = new List<JsonElement>();
        anchors.AddRange(doc.RootElement.GetProperty("core_points").EnumerateArray().Select(cp => cp.GetProperty("position")));
        anchors.AddRange(doc.RootElement.GetProperty("born_points").EnumerateArray().Select(p => p.GetProperty("position")));
        anchors.AddRange(doc.RootElement.GetProperty("mine_points").EnumerateArray().Select(p => p.GetProperty("position")));
        foreach (var hub in doc.RootElement.GetProperty("patrol_ways").EnumerateArray().SelectMany(way => way.GetProperty("hubs").EnumerateArray()))
        {
            anchors.Add(hub.GetProperty("position"));
            anchors.AddRange(hub.GetProperty("sub_points").EnumerateArray());
        }
        Assert.Equal(7 + 16 + 23 + 5 + 54, anchors.Count);
        foreach (var position in anchors)
        {
            var p = position.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            Assert.InRange(p[0], -421.2f, -319.4f);
            Assert.InRange(p[1], -8f, 12f);
            Assert.InRange(p[2], 445.9f, 527.7f);
        }
        var census = doc.RootElement.GetProperty("scene_census");
        Assert.Equal(400f, census.GetProperty("far_field_radius").GetSingle());
        Assert.Equal(3021, census.GetProperty("totals").GetProperty("transforms").GetInt32());
        foreach (var released in new[] { "equator.ai.json", "bay5.ai.json", "airpit.ai.json", "bowl.ai.json", "iceberg.ai.json" })
        {
            using var other = JsonDocument.Parse(File.ReadAllText(Sidecar(released)));
            Assert.False(other.RootElement.TryGetProperty("born_points", out _), released);
            Assert.False(other.RootElement.TryGetProperty("scene_census", out _), released);
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
