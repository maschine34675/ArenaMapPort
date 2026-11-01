using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins for the per-map emission cap and Iceberg's bloom (docs/progress/iceberg-release.md "Per-map emission cap"). EmissionCapRepairs.cs is Unity glue
/// (renderer scan, Material reads and writes) and BloomTuning.cs binds BepInEx entries on EFT's camera; neither can run here, so the rules they must keep are read
/// from the text: one scan, never per frame, scheduled behind the loading screen from the owned scenes' load, the materials changed in place and written back at the
/// end of the raid, the specular-cap clones suspended around the pass, the developer key on Settings with the descriptor's default, Debug-level logging, and the
/// bloom defaults of Iceberg equal to Equator's and Bay5's with the revision 2 migration decided by the model.
/// </summary>
public sealed class EmissionCapSourcePinTests
{
    private const string Glue = "ArenaMapPort.Client/EmissionCapRepairs.cs";
    private const string Model = "ArenaMapPort.Client/EmissionCapModel.cs";
    private const string Bloom = "ArenaMapPort.Client/BloomTuning.cs";

    private static string Text(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }

    private static string Code(string relative) => Regex.Replace(Text(relative), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, "missing: " + start);
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, "missing end marker: " + end);
        return text.Substring(a, b - a);
    }

    [Fact]
    public void TheModelsAreFreeOfUnityAndBepInEx()
    {
        foreach (var file in new[] { Model, "ArenaMapPort.Client/BloomDefaultsModel.cs" })
        {
            var code = Code(file);
            Assert.DoesNotMatch(@"\bUnityEngine\b|\bBepInEx\b|Plugin\.Log|DeveloperOptions", code);
        }
    }

    [Fact]
    public void NothingRunsPerFrameAndTheSceneIsScannedExactlyOnceInOneMethod()
    {
        var code = Code(Glue);
        Assert.DoesNotMatch(@"\bvoid\s+(Update|LateUpdate|FixedUpdate|OnGUI)\s*\(", code);
        Assert.DoesNotMatch(@"FindObjectsOfType|FindObjectOfType|FindObjectsByType|Resources\.FindObjects|GameObject\.Find|FindWithTag", code);
        Assert.Single(Regex.Matches(code, @"GetComponentsInChildren<"));
        Assert.Contains("GetComponentsInChildren<Renderer>(true)", Between(code, "static int Scan(State state)", "static EmissionEntry Capture("));
        // the scan is called from the one pass, and only when the cap or the floor is on and a rescan is asked for
        Assert.Contains("bool on = cap > 0f || floor > 0f;", code);
        Assert.Contains("if (on && rescan)", code);
        Assert.Single(Regex.Matches(code, @"\bScan\(state\)"));
        // a shared list, not an array per renderer
        Assert.Contains("renderer.GetSharedMaterials(materials);", code);
        Assert.DoesNotMatch(@"\.sharedMaterials\b|\.materials\b|\.material\b|new Material\(", code);   // never instantiates a material: the shared instance is the one that is capped
    }

    [Fact]
    public void ThePassIsScheduledBehindTheLoadingScreenFromTheOwnedScenesAndOnlyForAMapWithACap()
    {
        var code = Code(Glue);
        var loaded = Between(code, "static void OnSceneLoaded(", "static State StateFor(");
        Assert.Contains("ArenaMapRaid.IsOwnedScene(scene.name)", loaded);
        Assert.Contains("!Active(map)", loaded);                            // a map without a cap or a floor schedules nothing
        Assert.Contains("static bool Active(ArenaMapDescriptor map) => CapFor(map) > 0f || FloorFor(map) > 0f;", code);
        Assert.Contains("StartCoroutine(RunAfter(state, ++state.Generation))", loaded);
        Assert.Contains("const float PassDelaySeconds = 4f;", code);
        var run = Between(code, "static IEnumerator RunAfter(", "static void OnCapChanged(");
        Assert.Contains("yield return new WaitForSecondsRealtime(PassDelaySeconds);", run);
        Assert.Contains("state.Generation != generation", run);              // a later scene load restarts the timer
        Assert.Contains("SceneManager.sceneLoaded += OnSceneLoaded;", code);
        Assert.Contains("SceneManager.sceneUnloaded += OnSceneUnloaded;", code);
    }

    [Fact]
    public void TheOriginalsGoBackAtTheEndOfTheRaidAndTheStateIsDropped()
    {
        var code = Code(Glue);
        var unload = Between(code, "static void OnSceneUnloaded(", "static void Pass(");
        Assert.Contains("ArenaMapRaid.MapForScriptsScene(scene.name) == null", unload);
        Assert.Contains("Release(\"raid ended\")", unload);
        var release = Between(code, "static void Release(string reason)", "static string F(");
        Assert.Contains("state.Ledger.Restore(Surface)", release);
        Assert.Contains("state.Ledger.Clear();", release);
        Assert.Contains("UnityEngine.Object.Destroy(state.Host)", release);
        // the unload handler resolves the map from the scene name, not from a shared "current map"
        Assert.DoesNotContain("CurrentMap", unload);
    }

    [Fact]
    public void TheSpecularCapClonesAreSuspendedAroundTheReadAndTheWrite()
    {
        var code = Code(Glue);
        var pass = Between(code, "static void Pass(", "static int Scan(");
        var suspend = Between(pass, "using (ReflectiveSpecularRepairs.Suspend(state.Map))", "catch (Exception e)");
        Assert.Contains("Scan(state)", suspend);
        Assert.Contains("state.Ledger.Apply(Surface, floor, cap)", suspend);
        Assert.Contains("state.Ledger.Restore(Surface)", suspend);
    }

    [Fact]
    public void TheDeveloperKeyIsOnSettingsBoundPerMapWithTheDescriptorsValueAndIsNotPublic()
    {
        var code = Code(Glue);
        var install = Between(code, "internal static void Install()", "static float CapFor(");
        Assert.Contains("foreach (var map in ArenaMapRegistry.All)", install);
        Assert.Contains("Plugin.Instance.Settings.Bind($\"Lighting ({map.Key})\", \"EmissionCap\", map.EmissionCap,", install);
        Assert.Contains("new AcceptableValueRange<float>(0f, EmissionCapModel.MaxCap)", install);
        Assert.Contains("entry.SettingChanged += (_, __) => OnCapChanged(captured);", install);
        // the floor: the same section, the descriptor's value, the same range, the same live pass
        Assert.Contains("Plugin.Instance.Settings.Bind($\"Lighting ({map.Key})\", \"EmissionFloor\", map.EmissionFloor,", install);
        Assert.Equal(2, Regex.Matches(install, @"new AcceptableValueRange<float>\(0f, EmissionCapModel\.MaxCap\)").Count);
        Assert.Contains("floor.SettingChanged += (_, __) => OnCapChanged(captured);", install);
        Assert.DoesNotContain("Plugin.Instance.Config", code);
        // none of the four public keys: the visibility policy hides them from players
        var visibility = Code("ArenaMapPort.Client/MapConfigVisibility.cs");
        Assert.DoesNotContain("EmissionCap", visibility);
        Assert.DoesNotContain("EmissionFloor", visibility);
        Assert.DoesNotContain("EmissionCap", Code("ArenaMapPort.Client/CompactConfiguration.cs"));
        Assert.DoesNotContain("EmissionFloor", Code("ArenaMapPort.Client/CompactConfiguration.cs"));
    }

    [Fact]
    public void LoggingIsDebugExceptForRealFailures()
    {
        var code = Code(Glue);
        Assert.DoesNotMatch(@"Plugin\.Log\.LogInfo", code);
        var levels = Regex.Matches(code, @"Plugin\.Log\.Log(Debug|Warning|Error)\(").Select(m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(levels);
        Assert.Equal(1, levels.Count(l => l == "Warning"));                  // the pass could not be scheduled
        Assert.True(levels.Count(l => l == "Error") >= 2);
        // the tag and the per-material list behind the developer switch
        Assert.Contains("const string Tag = \"[EmissionCap]\";", code);
        Assert.Contains("if (DeveloperOptions.Enabled)", Between(code, "static void Pass(", "static int Scan("));
    }

    [Fact]
    public void ThePluginInstallsTheRepairRightAfterTheBloomKeys()
    {
        var plugin = Text("ArenaMapPort.Client/Plugin.cs");
        int bloom = plugin.IndexOf("BloomTuning.Install();", StringComparison.Ordinal);
        int emission = plugin.IndexOf("EmissionCapRepairs.Install();", StringComparison.Ordinal);
        Assert.True(bloom > 0 && emission > bloom, "EmissionCapRepairs.Install() follows BloomTuning.Install()");
        Assert.Single(Regex.Matches(plugin, @"EmissionCapRepairs\.Install\(\)"));
    }

    [Fact]
    public void IcebergsBloomDefaultsAreTheDonorPairWithTheRevisionTwoMigration()
    {
        var code = Code(Bloom);
        // 4.0/0.2 is gone as a default; Iceberg joins the donor pair
        Assert.DoesNotMatch(@"IcebergThreshold|IcebergIntensity", code);
        Assert.Contains("float defaultThreshold = donorDefaults || iceberg ? DefaultThreshold : airPit ? AirPitThreshold : -1f;", code);
        Assert.Contains("float defaultIntensity = donorDefaults || iceberg ? DefaultIntensity : airPit ? AirPitIntensity : -1f;", code);
        Assert.Contains("const float DefaultThreshold = 2.1f;", code);
        Assert.Contains("const float DefaultIntensity = 0.35f;", code);
        // the migration is the model's, revision 2, and AirPit keeps its revision 1 pair migration alone
        Assert.Contains("if (iceberg) MigrateIcebergDefaults(map.Key, section, threshold, intensity);", code);
        Assert.Contains("if (airPit) MigratePairDefaults(map.Key, section, threshold, intensity, AirPitThreshold, AirPitIntensity);", code);
        var migration = Between(code, "static void MigrateIcebergDefaults(", "internal static void ApplyToCurrentCamera(");
        Assert.Contains("BloomDefaultsModel.IcebergPairMoves(revision.Value, threshold.Value, intensity.Value)", migration);
        Assert.Contains("threshold.Value = DefaultThreshold; intensity.Value = DefaultIntensity;", migration);
        Assert.Contains("revision.Value = BloomDefaultsModel.IcebergRevision;", migration);
        Assert.Contains("\"BloomDefaultsRevision\"", migration);
        // the key names and the AirPit and Equator defaults did not move
        Assert.Contains("const float AirPitThreshold = 1.2f;", code);
        Assert.Contains("const float AirPitIntensity = 0.25f;", code);
        Assert.Contains("\"BloomThreshold\", defaultThreshold", code);
        Assert.Contains("\"BloomIntensity\", defaultIntensity", code);
    }

    [Fact]
    public void TheBloomTextsNoLongerSendIcebergToFourOrBlameTheChandeliersOnTheCamera()
    {
        // string literals joined across lines: "a " + "b" reads "a b"
        var text = Regex.Replace(Text(Bloom), "\"\\s*\\+\\s*\"", "");
        Assert.DoesNotContain("Iceberg to 4.0", text);
        Assert.DoesNotContain("Iceberg to 0.2", text);
        Assert.DoesNotContain("candle chandeliers glare at the camera's own value", text);
        Assert.Contains("Equator, Bay5 and Iceberg default to the donor rig's normal threshold, 2.10", text);
        Assert.Contains("Equator, Bay5 and Iceberg default to this normal intensity, 0.35", text);
    }
}
