#nullable disable
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The pure rules behind LevelBorderBoxRepairs (PLAN-YARD item 10 part 2, 2026-11-21): names, the layer, which rows can be raised, the normalised rotation, the world
/// bounds of an oriented box and the decision of a second pass over an existing set. The Unity glue is pinned as text (<see cref="LevelBorderBoxSourcePinTests"/>); the
/// descriptor rows against the sidecar are in YardMapTests. Host test pending: Yard is Catalog only until PLAN-YARD item 15.
/// </summary>
public sealed class LevelBorderBoxModelTests
{
    static ArenaBorderBox Box(float cx, float cy, float cz, float sx, float sy, float sz, float qx = 0f, float qy = 0f, float qz = 0f, float qw = 1f) =>
        new ArenaBorderBox(cx, cy, cz, sx, sy, sz, qx, qy, qz, qw);

    [Theory]
    [InlineData(0, "ArenaMapPort_LevelBorder_000")]
    [InlineData(7, "ArenaMapPort_LevelBorder_007")]
    [InlineData(103, "ArenaMapPort_LevelBorder_103")]
    [InlineData(1000, "ArenaMapPort_LevelBorder_1000")]
    public void ChildNamesAreTheRowIndexWithThreeDigitsAtLeast(int index, string expected)
    {
        Assert.Equal(expected, LevelBorderBoxModel.ChildName(index));
    }

    [Fact]
    public void NoOwnedNameStartsWithLevelBorderSoMapBorderRepairsNeverMatchesIt()
    {
        // MapBorderRepairs.IsLevelBorderObject matches the layer and the name prefix "LevelBorder" (Ordinal); the walls take neither: another prefix, and they are created after its scan
        Assert.DoesNotMatch("^LevelBorder", LevelBorderBoxModel.ParentName);
        Assert.DoesNotMatch("^LevelBorder", LevelBorderBoxModel.ChildPrefix);
        Assert.DoesNotMatch("^LevelBorder", LevelBorderBoxModel.ChildName(5));
        Assert.StartsWith("ArenaMapPort_", LevelBorderBoxModel.ParentName);
        Assert.NotEqual(AuthoredBorderPrefix, LevelBorderBoxModel.ChildPrefix);               // AirPit's walls keep their own prefix (BallisticTwinRepairs skips it by name)
        Assert.False(LevelBorderBoxModel.ChildPrefix.StartsWith(AuthoredBorderPrefix, StringComparison.Ordinal));
        var names = Enumerable.Range(0, 2000).Select(LevelBorderBoxModel.ChildName).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.DoesNotContain(LevelBorderBoxModel.ParentName, names);
    }

    // AuthoredBorderRepairs.ObjectPrefix (the Unity file is not linked into the tests): pinned as text in the source-pin class, copied here for the comparison above
    const string AuthoredBorderPrefix = "ArenaMapPort_Border_";

    [Theory]
    [InlineData(-1, 29)]          // NameToLayer of an unknown name
    [InlineData(29, 29)]
    [InlineData(30, 29)]          // a name that resolves elsewhere does not move the walls
    [InlineData(0, 29)]
    [InlineData(32, 29)]
    [InlineData(-5, 29)]
    public void TheLayerIsAlwaysTwentyNine(int named, int expected)
    {
        Assert.Equal(expected, LevelBorderBoxModel.ResolveLayer(named));
        Assert.Equal("LevelBorder", LevelBorderBoxModel.LayerName);
        Assert.Equal(29, LevelBorderBoxModel.LayerFallback);
    }

    [Fact]
    public void ARotationIsNotConfusedWithItsInverse()
    {
        // +90 degrees about y (Unity: x axis -> -z, z axis -> +x). WorldBounds uses |R| and would pass for -90 as well; this is sign-sensitive.
        float s = (float)Math.Sqrt(0.5);
        var box = Box(10f, 2f, 20f, 4f, 1f, 2f, qy: s, qw: s);
        LevelBorderBoxModel.LocalToWorld(box, 2, 0, 0, out double x, out double y, out double z);   // the +x face centre
        Assert.Equal(10.0, x, 4);
        Assert.Equal(2.0, y, 4);
        Assert.Equal(18.0, z, 4);
        LevelBorderBoxModel.LocalToWorld(box, 0, 0, 1, out x, out y, out z);                         // the +z face centre
        Assert.Equal(11.0, x, 4);
        Assert.Equal(20.0, z, 4);
        // the inverse rotation lands on the opposite sides
        var inverse = Box(10f, 2f, 20f, 4f, 1f, 2f, qy: -s, qw: s);
        LevelBorderBoxModel.LocalToWorld(inverse, 2, 0, 0, out x, out y, out z);
        Assert.Equal(22.0, z, 4);
    }

    [Fact]
    public void OnlyAFiniteSolidWithAUnitRotationIsUsable()
    {
        Assert.True(LevelBorderBoxModel.IsUsable(Box(1, 2, 3, 4, 5, 6)));
        Assert.True(LevelBorderBoxModel.IsUsable(Box(1, 2, 3, 0.1515f, 4.5352f, 32.9876f, 0f, 0f, 0.025267f, 0.999681f)));      // six digits: the length is 1 to within 1e-6
        Assert.False(LevelBorderBoxModel.IsUsable(null));
        Assert.False(LevelBorderBoxModel.IsUsable(Box(float.NaN, 0, 0, 1, 1, 1)));
        Assert.False(LevelBorderBoxModel.IsUsable(Box(0, float.PositiveInfinity, 0, 1, 1, 1)));
        Assert.False(LevelBorderBoxModel.IsUsable(Box(0, 0, 0, 0f, 1, 1)));
        Assert.False(LevelBorderBoxModel.IsUsable(Box(0, 0, 0, 1, -1f, 1)));
        Assert.False(LevelBorderBoxModel.IsUsable(Box(0, 0, 0, 1, 1, float.NaN)));
        Assert.False(LevelBorderBoxModel.IsUsable(Box(0, 0, 0, 1, 1, 1, 0f, 0f, 0f, 0f)));          // the zero quaternion
        Assert.False(LevelBorderBoxModel.IsUsable(Box(0, 0, 0, 1, 1, 1, 0f, 0f, 0.5f, 0.5f)));       // length 0.707
        Assert.False(LevelBorderBoxModel.IsUsable(Box(0, 0, 0, 1, 1, 1, 0f, 0f, 0f, float.NaN)));
        Assert.Equal(2, LevelBorderBoxModel.CountUsable(new[] { Box(0, 0, 0, 1, 1, 1), null, Box(1, 1, 1, 0f, 1, 1), Box(2, 2, 2, 1, 1, 1) }));
        Assert.Equal(0, LevelBorderBoxModel.CountUsable(null));
        Assert.Equal(0, LevelBorderBoxModel.CountUsable(new ArenaBorderBox[0]));
    }

    [Fact]
    public void ARotationRoundedToSixDigitsIsNormalisedToUnitLength()
    {
        var box = Box(0, 0, 0, 1, 1, 1, 0f, 0.893436f, 0f, 0.449191f);                               // Yard's "Cube (100)": about 1.7e-7 away from unit length
        LevelBorderBoxModel.NormalizedRotation(box, out float x, out float y, out float z, out float w);
        Assert.Equal(1.0, Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)w * w), 6);
        Assert.Equal(0f, x);
        Assert.Equal(0f, z);
        Assert.Equal(0.893436f, y, 5);
        Assert.Equal(0.449191f, w, 5);
        LevelBorderBoxModel.NormalizedRotation(Box(0, 0, 0, 1, 1, 1), out x, out y, out z, out w);
        Assert.Equal((0f, 0f, 0f, 1f), (x, y, z, w));
    }

    [Fact]
    public void WorldBoundsOfAnAxisAlignedBoxAreCentreMinusAndPlusHalfTheSize()
    {
        var min = new float[3];
        var max = new float[3];
        LevelBorderBoxModel.WorldBounds(Box(10f, 20f, 30f, 2f, 4f, 6f), min, max);
        Assert.Equal(new[] { 9f, 18f, 27f }, min);
        Assert.Equal(new[] { 11f, 22f, 33f }, max);
    }

    [Fact]
    public void WorldBoundsOfAYawedBoxFollowTheRotation()
    {
        var min = new float[3];
        var max = new float[3];
        // 90 degrees about y (the quaternion holds the half angle) swaps the x and z extents
        float s = MathF.Sin(MathF.PI / 4f), c = MathF.Cos(MathF.PI / 4f);
        LevelBorderBoxModel.WorldBounds(Box(0f, 0f, 0f, 2f, 4f, 6f, 0f, s, 0f, c), min, max);
        Assert.Equal(new[] { -3f, -2f, -1f }, min.Select(v => MathF.Round(v, 4)).ToArray());
        Assert.Equal(new[] { 3f, 2f, 1f }, max.Select(v => MathF.Round(v, 4)).ToArray());
        // 45 degrees: both extents become (2 + 6) / sqrt(2) / 2
        float s2 = MathF.Sin(MathF.PI / 8f), c2 = MathF.Cos(MathF.PI / 8f);
        LevelBorderBoxModel.WorldBounds(Box(10f, 5f, -3f, 2f, 4f, 6f, 0f, s2, 0f, c2), min, max);
        double half = (2.0 + 6.0) / Math.Sqrt(2.0) / 2.0;
        Assert.Equal(10.0 - half, min[0], 4);
        Assert.Equal(-3.0 + half, max[2], 4);
        Assert.Equal(3f, min[1], 4);
        Assert.Equal(7f, max[1], 4);
        // a roll about z by 90 degrees swaps x and y
        LevelBorderBoxModel.WorldBounds(Box(0f, 0f, 0f, 2f, 4f, 6f, 0f, 0f, s, c), min, max);
        Assert.Equal(new[] { -2f, -1f, -3f }, min.Select(v => MathF.Round(v, 4)).ToArray());
    }

    [Theory]
    [InlineData(false, 0, 104, "Build")]       // the first pass of a raid
    [InlineData(true, 104, 104, "Keep")]       // the second pass (CreateFromScene runs twice): idempotent
    [InlineData(true, 40, 104, "Rebuild")]     // a pass cut short or another descriptor: the set is replaced, never doubled
    [InlineData(true, 0, 104, "Rebuild")]
    [InlineData(true, 200, 104, "Rebuild")]
    [InlineData(false, 0, 0, "Nothing")]       // a map without rows (every released map): nothing is made
    [InlineData(true, 3, 0, "Rebuild")]
    public void ASecondPassNeverDoublesTheSet(bool parentExists, int children, int usable, string expected)
    {
        Assert.Equal(expected, LevelBorderBoxModel.PlanPass(parentExists, children, usable).ToString());
    }

    [Fact]
    public void AMapWithoutRowsPlansNothingAndHasNoUsableRow()
    {
        // every registered map except Yard (which joins All at item 15 and is the one map with rows): no usable row, so the pass plans nothing and Apply creates nothing
        foreach (var map in ArenaMapRegistry.All.Where(m => !ReferenceEquals(m, ArenaMapRegistry.Yard)))
        {
            Assert.Equal(0, LevelBorderBoxModel.CountUsable(map.LevelBorderBoxes));
            Assert.Equal(LevelBorderBoxModel.Pass.Nothing, LevelBorderBoxModel.PlanPass(false, 0, LevelBorderBoxModel.CountUsable(map.LevelBorderBoxes)));
        }
    }
}

/// <summary>
/// The glue file LevelBorderBoxRepairs.cs and its wiring, pinned as text (the Unity types keep it out of the test project): it never runs for a map without rows, never
/// searches the scene per row, names nothing LevelBorder*, raises plain BoxColliders on the resolved layer into an owned scene and is installed after AuthoredBorderRepairs
/// behind its own guard. Host test pending (Catalog only until PLAN-YARD item 15).
/// </summary>
public sealed class LevelBorderBoxSourcePinTests
{
    static string ClientDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "ArenaMapPort.Client");
            if (File.Exists(Path.Combine(path, "Plugin.cs")))
                return path;
        }
        throw new DirectoryNotFoundException("ArenaMapPort.Client");
    }

    static string Source(string file) => File.ReadAllText(Path.Combine(ClientDirectory(), file)).Replace("\r\n", "\n");

    static string Code(string file) => Regex.Replace(Regex.Replace(Source(file), @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    [Fact]
    public void TheOwnedPrefixOfAirPitsWallsIsTheOneBallisticTwinRepairsSkips()
    {
        Assert.Contains("ObjectPrefix = \"ArenaMapPort_Border_\"", Source("AuthoredBorderRepairs.cs"));
    }

    [Fact]
    public void ApplyReturnsBeforeAnyUnityCallWhenTheMapHasNoRows()
    {
        string code = Code("LevelBorderBoxRepairs.cs");
        int apply = code.IndexOf("internal static void Apply()", StringComparison.Ordinal);
        Assert.True(apply > 0);
        string body = code[apply..];
        int guard = body.IndexOf("map.LevelBorderBoxes.Length == 0", StringComparison.Ordinal);
        Assert.True(guard > 0, "the no-rows guard");
        foreach (string unity in new[] { "LayerMask.", "SceneManager.", "new GameObject", "GetRootGameObjects", "DestroyImmediate" })
            Assert.True(body.IndexOf(unity, StringComparison.Ordinal) > guard, $"{unity} comes before the no-rows guard");
        Assert.True(body.IndexOf("ArenaMapRaid.IsLoaded()", StringComparison.Ordinal) < guard);
    }

    [Fact]
    public void NoSceneWideSearchAndNoNameThatMapBorderRepairsWouldMatch()
    {
        string code = Code("LevelBorderBoxRepairs.cs");
        Assert.DoesNotContain("GameObject.Find", code);
        Assert.DoesNotContain("FindObjectsOfType", code);
        Assert.DoesNotContain("FindObjectOfType", code);
        Assert.DoesNotContain("GetComponentsInChildren", code);
        Assert.DoesNotContain("Resources.", code);
        Assert.DoesNotContain("Update(", code);                                  // not a per-frame or coroutine repair
        Assert.Matches(@"GetRootGameObjects\(\)", code);                         // the parent is looked up among the scene's roots, once per pass
        Assert.Equal(1, Regex.Matches(code, @"FindParent\(owned\)").Count);
    }

    [Fact]
    public void TheWallsAreInvisiblePlainBoxCollidersOnTheResolvedLayerInAnOwnedScene()
    {
        string code = Code("LevelBorderBoxRepairs.cs");
        Assert.Contains("AddComponent<BoxCollider>()", code);
        Assert.DoesNotContain("AddComponent<MeshRenderer>", code);
        Assert.DoesNotContain("AddComponent<MeshFilter>", code);
        Assert.DoesNotContain("isTrigger = true", code);
        Assert.DoesNotContain("AddComponent<Rigidbody>", code);
        Assert.DoesNotContain("NavMeshObstacle", code);                           // no carver: part 1 measured that no row cuts walkable area
        Assert.Contains("LevelBorderBoxModel.ResolveLayer(LayerMask.NameToLayer(LevelBorderBoxModel.LayerName))", code);
        Assert.Contains("SceneManager.MoveGameObjectToScene(root, owned)", code);
        // the root joins the owned scene before the loop, so a throw in the loop cannot leave a half-built set outside it
        Assert.True(code.IndexOf("MoveGameObjectToScene(root, owned)", StringComparison.Ordinal) < code.IndexOf("for (int i = 0; i < boxes.Length", StringComparison.Ordinal));
        Assert.Contains("SetPositionAndRotation(new Vector3(box.CenterX, box.CenterY, box.CenterZ), new Quaternion(qx, qy, qz, qw))", code);
        Assert.Contains("collider.size = new Vector3(box.SizeX, box.SizeY, box.SizeZ)", code);
        Assert.Contains("LevelBorderBoxModel.PlanPass(", code);
        Assert.Contains("LevelBorderBoxModel.NormalizedRotation(", code);
    }

    [Fact]
    public void TheLoggingIsDebugWithTheRaidFixTagAndOnlyTheSiteWarns()
    {
        string code = Code("LevelBorderBoxRepairs.cs");
        Assert.DoesNotContain("LogInfo", code);
        Assert.DoesNotContain("LogWarning", code);
        Assert.DoesNotContain("LogError", code);
        Assert.Contains("const string Tag = \"[RaidFix]\"", code);
        foreach (Match log in Regex.Matches(code, @"Plugin\.Log\.(\w+)\("))
            Assert.Equal("LogDebug", log.Groups[1].Value);
        string site = Code("SpawnInfiltrationRepairs.cs");
        int at = site.IndexOf("LevelBorderBoxRepairs.Apply()", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Contains("Plugin.Log.LogWarning($\"[RaidFix] raising the level border walls failed", site[at..]);
        Assert.Contains("catch (System.Exception", site[at..]);
    }

    [Fact]
    public void TheSettingsAreDeveloperOnlyAndBoundThroughTheSettingsObject()
    {
        string code = Code("LevelBorderBoxRepairs.cs");
        Assert.Equal(1, Regex.Matches(code, @"\.Bind\(").Count);
        Assert.Contains("Plugin.Instance.Settings.Bind(", code);
        Assert.DoesNotContain("Plugin.Instance.Config.Bind", code);
        Assert.Contains("foreach (var map in ArenaMapRegistry.All)", code);
        Assert.Contains("map.LevelBorderBoxes.Length == 0", code);
        // not a public key: CompactConfiguration.IsPublic and MapConfigVisibility.IsPlayerOption name the four public ones, and this is not among them
        Assert.DoesNotContain("LevelBorderWalls", Source("CompactConfiguration.cs"));
        Assert.DoesNotContain("LevelBorderWalls", Source("MapConfigVisibility.cs"));
    }

    [Fact]
    public void ItRunsRightAfterAuthoredBordersAndItsKeyIsBoundBesideTheirs()
    {
        string site = Source("SpawnInfiltrationRepairs.cs");
        int authored = site.IndexOf("AuthoredBorderRepairs.Apply();", StringComparison.Ordinal);
        int level = site.IndexOf("LevelBorderBoxRepairs.Apply();", StringComparison.Ordinal);
        int twins = site.IndexOf("BallisticTwinRepairs.Apply();", StringComparison.Ordinal);
        Assert.True(0 < authored && authored < level && level < twins, "AuthoredBorders, then the level border walls, then the ballistic twins");
        Assert.Equal(1, Regex.Matches(site, @"LevelBorderBoxRepairs\.Apply\(\)").Count);
        string patches = Source("HarmonyPatches.cs");
        int bindAuthored = patches.IndexOf("AuthoredBorderRepairs.BindConfig();", StringComparison.Ordinal);
        int bindLevel = patches.IndexOf("LevelBorderBoxRepairs.BindConfig();", StringComparison.Ordinal);
        Assert.True(0 < bindAuthored && bindAuthored < bindLevel);
        Assert.Equal(1, Regex.Matches(patches, @"LevelBorderBoxRepairs\.BindConfig\(\)").Count);
    }

    [Fact]
    public void YardIsNamedByNoClientListAndNoCsprojRow()
    {
        // Catalog only: a Release or DllOnly client build must not require any Yard file and no release list names Yard (item 15 adds the rows)
        string csproj = File.ReadAllText(Path.Combine(ClientDirectory(), "ArenaMapPort.Client.csproj"));
        Assert.DoesNotContain("yard", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LevelBorder", csproj);                             // the descriptor literal is the data: no staged file, no csproj row
        string code = Code("LevelBorderBoxRepairs.cs");
        Assert.DoesNotContain("Yard", code);
        Assert.DoesNotContain("arena_yard", code);
    }
}
