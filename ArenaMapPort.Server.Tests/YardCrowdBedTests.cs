using System.IO.Compression;
using ArenaMapPort.Client;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ArenaMapPort.Server.Tests;

/// <summary>Runs only when the ignored source export of Yard's crowd (work/extract/spectator-yard, or ARENAMAPPORT_WORK_DIR/extract/spectator-yard) exists.</summary>
internal sealed class YardCrowdFactAttribute : FactAttribute
{
    public YardCrowdFactAttribute()
    {
        if (YardCrowd.Graph() == null) Skip = "no work/extract/spectator-yard/spectator.json.gz (python tools/extract/extract_spectator.py extract --map yard); ARENAMAPPORT_WORK_DIR names a work folder";
    }
}

internal static class YardCrowd
{
    internal static string? Graph()
    {
        string tail = Path.Combine("extract", "spectator-yard", "spectator.json.gz");
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string path = Path.Combine(env, tail);
            if (File.Exists(path)) return path;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "work", tail);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The positions the runtime hands the bed (the actor roots in world space, plus 1.6 m), read like <c>DistCrowd.Read</c> from the schema-3 source export (the staged schema-4 graph has the same container and actors).</summary>
    internal static BedPoint[] Points()
    {
        using var gz = new GZipStream(File.OpenRead(Graph()!), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var doc = JObject.Parse(reader.ReadToEnd());
        Assert.Equal("arena_yard", (string)doc["map"]!);
        var origin = doc["container"]!["position"]!.Select(t => (float)t).ToArray();
        Assert.Equal(new[] { 0f, 0f, 0f }, origin);                                                   // the world-space shortcut below needs the identity container
        Assert.Equal(new[] { 1f, 1f, 1f }, doc["container"]!["scale"]!.Select(t => (float)t).ToArray());
        return doc["actors"]!.Select(actor =>
        {
            var p = actor["nodes"]![0]!["position"]!.Select(t => (float)t).ToArray();
            return new BedPoint(p[0] + origin[0], p[1] + origin[1] + 1.6f, p[2] + origin[2]);
        }).ToArray();
    }
}

/// <summary>PLAN-YARD item 14: the murmur bed's stand plan for Yard's 27 spectators, computed by the real <see cref="SpectatorCrowdBedModel"/> (k-means, radius limit 25 m, cap 6).</summary>
public sealed class YardCrowdBedTests
{
    private readonly ITestOutputHelper _out;
    public YardCrowdBedTests(ITestOutputHelper output) { _out = output; }

    [YardCrowdFact]
    public void YardsTwentySevenFansFormTheStandsTheRadiusRuleGives()
    {
        var points = YardCrowd.Points();
        Assert.Equal(27, points.Length);
        var stands = SpectatorCrowdBedModel.Plan(points, 60);
        _out.WriteLine($"arena_yard: {points.Length} fans, {SpectatorCrowdBedModel.Describe(stands)}");
        Assert.InRange(stands.Length, 2, SpectatorCrowdBedModel.MaxStands);
        Assert.Equal(points.Length, stands.Sum(s => s.Members));
        Assert.True(stands.Length == SpectatorCrowdBedModel.MaxStands || SpectatorCrowdBedModel.LargestRadius(stands) <= SpectatorCrowdBedModel.MaxStandRadius);
        Assert.Equal(ExpectedStands, stands.Length);
        Assert.All(stands, s =>
        {
            Assert.InRange(s.X, points.Min(p => p.X), points.Max(p => p.X));
            Assert.InRange(s.Y, points.Min(p => p.Y), points.Max(p => p.Y));
            Assert.InRange(s.Z, points.Min(p => p.Z), points.Max(p => p.Z));
        });
        Assert.Equal(SpectatorCrowdBedModel.Describe(stands), SpectatorCrowdBedModel.Describe(SpectatorCrowdBedModel.Plan(points, 60)));
    }

    [YardCrowdFact]
    public void YardsPlanDoesNotDependOnTheReach()
    {
        var points = YardCrowd.Points();
        var a = SpectatorCrowdBedModel.Plan(points, 20);
        var b = SpectatorCrowdBedModel.Plan(points, 140);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.Equal((a[i].X, a[i].Y, a[i].Z, a[i].MinDistance), (b[i].X, b[i].Y, b[i].Z, b[i].MinDistance));
    }

    // Measured 2026-11-20 with the real model on the extracted crowd: 4 stands, radii 21.8 / 23.5 / 18.4 / 21.6 m (the 25 m rule is met without the cap), centres
    // (-64,18,71) (-31,15,123) (-26,22,17) (1,21,72). The stand centres lie at y 15 to 22.
    private const int ExpectedStands = 4;
}
