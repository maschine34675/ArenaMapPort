using System.Text.Json;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The score monitors of the four released maps (docs/progress/score-monitors.md): tools/maps/&lt;stem&gt;.monitors.json is written by
/// tools/extract/extract_score_monitors.py from the ArenaMonitorHolder objects of Arena's design scenes, ArenaMapRegistry.&lt;Map&gt;.ScoreMonitors is the typed
/// copy the client reads. The two must agree number for number (validate_map_descriptors.py checks the same on the Python side), the shared mount pose
/// must be the one ArenaScoreMonitorMount spells, and the face the client draws for a type word (ScoreMonitorLayout.FaceOf) must be the face the sidecar says.
/// </summary>
public sealed class ScoreMonitorSidecarTests
{
    private static string Sidecar(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "maps", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    private static float[] Floats(JsonElement array) =>
        array.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();

    private static ArenaMapDescriptor MapOf(string stem) => stem switch
    {
        "equator" => ArenaMapRegistry.Equator,
        "bay5" => ArenaMapRegistry.Bay5,
        "airpit" => ArenaMapRegistry.AirPit,
        "bowl" => ArenaMapRegistry.Bowl,
        _ => throw new ArgumentOutOfRangeException(nameof(stem)),
    };

    public static IEnumerable<object[]> Maps => new[]
    {
        new object[] { "equator" }, new object[] { "bay5" }, new object[] { "airpit" }, new object[] { "bowl" },
    };

    private static JsonDocument Load(string stem) => JsonDocument.Parse(File.ReadAllText(Sidecar(stem + ".monitors.json")));

    [Theory]
    [MemberData(nameof(Maps))]
    public void TheRegistryMatchesTheSidecarNumberForNumber(string stem)
    {
        var map = MapOf(stem);
        using var doc = Load(stem);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(map.Key, root.GetProperty("key").GetString());

        var rows = root.GetProperty("monitors").EnumerateArray().ToArray();
        Assert.Equal(rows.Length, map.ScoreMonitors.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            var s = rows[i];
            var m = map.ScoreMonitors[i];
            var label = $"{stem} monitor {i} ({m.Scene} {m.Path})";
            Assert.True(s.GetProperty("scene").GetString() == m.Scene, label + " scene");
            Assert.True(s.GetProperty("path").GetString() == m.Path, label + " path");
            Assert.Equal(Floats(s.GetProperty("at")), m.At);
            Assert.Equal(s.GetProperty("arena_type").GetInt32(), m.ArenaType);
            Assert.Equal(s.GetProperty("active_in_scene").GetBoolean(), m.ActiveInScene);
            Assert.Contains(m.Scene, map.AllSceneNames);
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void TheSidecarsMountIsTheOneEveryHolderHas(string stem)
    {
        using var doc = Load(stem);
        var mount = doc.RootElement.GetProperty("mount");
        Assert.Equal(ArenaScoreMonitorMount.Position, Floats(mount.GetProperty("position")));
        Assert.Equal(ArenaScoreMonitorMount.Rotation, Floats(mount.GetProperty("rotation")));
        Assert.Equal(ArenaScoreMonitorMount.Scale, (float)mount.GetProperty("scale").GetDouble());
        Assert.Equal(new[] { ArenaScoreMonitorMount.Width, ArenaScoreMonitorMount.Height }, Floats(mount.GetProperty("size")));
        Assert.Equal(new[] { ArenaScoreMonitorMount.PivotX, ArenaScoreMonitorMount.PivotY }, Floats(mount.GetProperty("pivot")));
        // the layout's canvas is the mount's rect
        Assert.Equal(ArenaScoreMonitorMount.Width, ScoreMonitorLayout.CanvasWidth);
        Assert.Equal(ArenaScoreMonitorMount.Height, ScoreMonitorLayout.CanvasHeight);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void TheFaceTheClientDrawsIsTheFaceTheSidecarSays(string stem)
    {
        using var doc = Load(stem);
        foreach (var row in doc.RootElement.GetProperty("monitors").EnumerateArray())
        {
            var word = row.GetProperty("arena_type").GetInt32();
            var label = $"{stem} {row.GetProperty("path").GetString()} type {word}";
            Assert.True(ScoreMonitorLayout.FaceName(ScoreMonitorLayout.FaceOf(word)) == row.GetProperty("face").GetString(), label);
            // a word without a template, and the inferred ones, are never confirmed
            var template = row.GetProperty("template");
            Assert.Equal(word is 0 or 1 or 2, row.GetProperty("confirmed").GetBoolean());
            Assert.Equal(word != 10, template.ValueKind == JsonValueKind.String);
        }
    }

    [Fact]
    public void TheFourMapsHold43HoldersAnd31OfThemAreActive()
    {
        // 2026-10-25, Arena 0.6.1.0.48206: Equator 11 (all active), Bay5 8 (all), AirPit 8 (6: the two team-box screens are inactive), Bowl 16 (6: the
        // ten of the base set are inactive, the TeamFight set stands in their place)
        var counts = new[] { "equator", "bay5", "airpit", "bowl" }.ToDictionary(s => s, s => (MapOf(s).ScoreMonitors.Length, MapOf(s).ScoreMonitors.Count(m => m.ActiveInScene)));
        Assert.Equal((11, 11), counts["equator"]);
        Assert.Equal((8, 8), counts["bay5"]);
        Assert.Equal((8, 6), counts["airpit"]);
        Assert.Equal((16, 6), counts["bowl"]);
    }

    [Fact]
    public void TheFacesOfTheActiveScreensPerMap()
    {
        // what a claim draws: Equator 2 timers, 5 scores (three type 1, two type 6), 2 alive counts, the two player boards (word 7) stay dark
        string Faces(string stem) => string.Join(",", MapOf(stem).ScoreMonitors.Where(m => m.ActiveInScene)
            .GroupBy(m => ScoreMonitorLayout.FaceName(ScoreMonitorLayout.FaceOf(m.ArenaType))).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));
        Assert.Equal("alive=2,none=2,score=5,timer=2", Faces("equator"));
        Assert.Equal("alive=3,score=2,timer=3", Faces("bay5"));
        Assert.Equal("alive=2,score=2,timer=2", Faces("airpit"));
        Assert.Equal("alive=2,score=2,timer=2", Faces("bowl"));
    }

    [Fact]
    public void TheInactiveRowsAreAirPitsTwoTeamBoxScreensAndBowlsBaseSet()
    {
        Assert.Equal(new[] { "TV/Arena_Home_theater (9)", "TV/Arena_Home_theater (11)" },
            ArenaMapRegistry.AirPit.ScoreMonitors.Where(m => !m.ActiveInScene).Select(m => m.Path).ToArray());
        var bowl = ArenaMapRegistry.Bowl.ScoreMonitors;
        Assert.All(bowl.Where(m => !m.ActiveInScene), m => Assert.Equal("Arena_Bowl_DesignStuff", m.Scene));
        Assert.All(bowl.Where(m => m.ActiveInScene), m => Assert.Equal("Arena_Bowl_TeamFight_DesignStuff", m.Scene));
        Assert.Equal(10, bowl.Count(m => !m.ActiveInScene));
        Assert.All(ArenaMapRegistry.Equator.ScoreMonitors.Concat(ArenaMapRegistry.Bay5.ScoreMonitors), m => Assert.True(m.ActiveInScene));
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void RowsAreUniqueAndAPropFindsItsRowByItsMountPoint(string stem)
    {
        var rows = MapOf(stem).ScoreMonitors;
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.True(rows[i].AppliesTo(rows[i].At[0], rows[i].At[1], rows[i].At[2]));
            Assert.False(rows[i].AppliesTo(rows[i].At[0] + 0.06f, rows[i].At[1], rows[i].At[2]));
            Assert.True(rows[i].AppliesTo(rows[i].At[0], rows[i].At[1] - 0.04f, rows[i].At[2]));
            for (int j = i + 1; j < rows.Length; j++)
            {
                var same = rows[i].Scene == rows[j].Scene && rows[i].Path == rows[j].Path;
                Assert.False(same && rows[i].AppliesTo(rows[j].At[0], rows[j].At[1], rows[j].At[2]),
                    $"{stem}: {rows[i].Path} appears twice at the same place");
            }
        }
    }

    [Fact]
    public void ARepeatedPathIsToldApartByItsPositionAndTheTwoDesignScenesOfEquatorRepeatPaths()
    {
        var bowl = ArenaMapRegistry.Bowl.ScoreMonitors.Where(m => m.ActiveInScene).GroupBy(m => m.Path).ToArray();
        Assert.Equal(3, bowl.Length);                       // ALIVE, SCORE and TIMER of the TeamFight set, two boards each
        Assert.All(bowl, g => Assert.Equal(2, g.Count()));
        var equator = ArenaMapRegistry.Equator.ScoreMonitors;
        var shared = equator.GroupBy(m => m.Path).Where(g => g.Select(m => m.Scene).Distinct().Count() > 1).Select(g => g.Key).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { "TV/Arena_Home_theater (5)", "TV/Arena_Home_theater (6)" }, shared);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void TheCanvasSizeFollowsThePropScale(string stem)
    {
        using var doc = Load(stem);
        foreach (var row in doc.RootElement.GetProperty("monitors").EnumerateArray())
        {
            var scale = row.GetProperty("prop_scale").GetDouble();
            var size = row.GetProperty("world_size_m").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            Assert.InRange(size[0], ArenaScoreMonitorMount.Width * ArenaScoreMonitorMount.Scale * scale - 0.002, ArenaScoreMonitorMount.Width * ArenaScoreMonitorMount.Scale * scale + 0.002);
            Assert.InRange(size[1], ArenaScoreMonitorMount.Height * ArenaScoreMonitorMount.Scale * scale - 0.002, ArenaScoreMonitorMount.Height * ArenaScoreMonitorMount.Scale * scale + 0.002);
            var yaw = row.GetProperty("viewer_yaw").GetDouble();
            Assert.InRange(yaw, 0.0, 359.99);
        }
    }

    [Fact]
    public void OnlyTheReleasedMapsCarryScoreMonitors()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            var released = ArenaMapRegistry.All.Contains(map);
            Assert.Equal(released, map.ScoreMonitors.Length > 0);
        }
        Assert.Equal(new[] { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" }, ArenaMapRegistry.All.Select(m => m.Key).ToArray());
    }

    [Fact]
    public void AMapWithoutRowsHasAnEmptyArrayNotNull()
    {
        Assert.Empty(ArenaMapRegistry.Prison.ScoreMonitors);
        var bare = new ArenaMapDescriptor("arena_x", "000000000000000000000000", "maps/arena_x_preset.bundle", "arena_x.scenespreset.asset",
            "arena_x/arena_x_scenes.bundle", "arena_x/arena_x_navmesh.bundle", "Arena_X_Scripts", new[] { "Arena_X_Scripts" }, "ArenaX", "Arena: X", "x", 1, 2, "arena_x/base.json");
        Assert.NotNull(bare.ScoreMonitors);
        Assert.Empty(bare.ScoreMonitors);
    }
}
