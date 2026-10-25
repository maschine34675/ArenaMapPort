#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins for the score monitors (docs/progress/score-monitors.md). ScoreMonitorView (UGUI, TextMeshPro) cannot run here, so the facts that the host test, AGENTS.md and the
/// release rules depend on are read from the source text, like GateSoundWiringTests and MapNoticeSourcePinTests do for their glue: the Unity-free core stays Unity-free, the view has
/// no scene-wide search, no extra camera, no collider, no Info line and no Harmony patch, the keys are developer keys on Settings, nothing in the periodic path costs more than a
/// flag read, and Plugin.cs / HarmonyPatches.cs call the two entry points exactly once.
/// </summary>
public sealed class ScoreMonitorSourcePinTests
{
    static string Text(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n");   // the sources are CRLF on the build machine
        }
        throw new FileNotFoundException(relative);
    }

    static string Between(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, "missing: " + from);
        int b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        Assert.True(b > a, "missing: " + to);
        return text.Substring(a, b - a);
    }

    /// <summary>Code without comments and string literals, so a pin does not trip over the class comment that explains what is NOT done.</summary>
    static string Code(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"^\s*//.*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\s//[^\n""]*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\$?""(?:[^""\\\n]|\\.)*""", "\"\"");
        return text;
    }

    static readonly string[] CoreFiles =
    {
        "ArenaMapPort.Client/ScoreMonitorLayout.cs", "ArenaMapPort.Client/ScoreMonitorModel.cs", "ArenaMapPort.Client/ScoreMonitorHub.cs",
        "ArenaMapPort.Client/ScoreMonitorDemo.cs", "ArenaMapPort.Client/ScoreMonitors.cs",
    };

    [Fact]
    public void TheCoreIsFreeOfUnityBepInExAndTheLogger()
    {
        foreach (var file in CoreFiles)
        {
            var code = Code(Text(file));
            foreach (var forbidden in new[] { "UnityEngine", "TMPro", "BepInEx", "Plugin.", "Harmony", "using EFT", "Comfort", "ArenaInstallationGate" })
                Assert.False(code.Contains(forbidden), $"{file} must not use {forbidden}");
        }
    }

    [Fact]
    public void TheViewHasNoSceneWideSearchNoCameraNoColliderNoInfoLineAndNoPatch()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        var code = Code(view);
        foreach (var forbidden in new[]
                 {
                     "FindObjectsOfType", "FindObjectOfType", "FindObjectsByType", "FindObjectsOfTypeAll", "GetComponentsInChildren", "GetComponentInChildren", "GameObject.Find",
                     "FindWithTag", "Resources.FindObjects",                                           // no scene-wide search (the 1 Hz freeze trap of AGENTS.md)
                     "Camera", "RenderTexture", "OnPreCull",                                            // no extra camera, no render-to-texture
                     "Collider", "Rigidbody",                                                           // the canvases are not physical
                     "LogInfo(", "LogError(", "LogFatal(",                                              // Debug and one Warning, nothing at Info
                     "EnsureVerified", "ArenaInstallationGate",                                         // a notice-like feature never blocks the game thread on the Arena check
                     "Harmony", "HarmonyPatch", "[HarmonyPrefix", "[HarmonyPostfix",                    // no patch: the generic-class trap does not apply
                     "DestroyImmediate", "Config.Bind", "Config.Save",                                  // Settings only, never an explicit save
                 })
            Assert.False(code.Contains(forbidden), "ScoreMonitorView must not use " + forbidden);
        Assert.Contains("LogDebug(", code);
        Assert.Contains("LogWarning(", code);
    }

    [Fact]
    public void TheViewIsTheOnlyFileOfTheFeatureThatTouchesUnityAndItNeedsNoNewReference()
    {
        Assert.Contains("using TMPro;", Text("ArenaMapPort.Client/ScoreMonitorView.cs"));
        Assert.Contains("using UnityEngine.UI;", Text("ArenaMapPort.Client/ScoreMonitorView.cs"));
        var csproj = Text("ArenaMapPort.Client/ArenaMapPort.Client.csproj");
        Assert.Contains("Include=\"Unity.TextMeshPro\"", csproj);
        Assert.Contains("Include=\"UnityEngine.UI\"", csproj);
        Assert.Contains("Include=\"UnityEngine.ImageConversionModule\"", csproj);
    }

    [Fact]
    public void TheThreeKeysAreDeveloperKeysOnSettingsAndNoneIsPublic()
    {
        var view = Code(Text("ArenaMapPort.Client/ScoreMonitorView.cs"));
        Assert.Equal(3, Regex.Matches(view, @"\bs\.Bind\(").Count);
        Assert.Equal(3, Regex.Matches(Text("ArenaMapPort.Client/ScoreMonitorView.cs"), @"s\.Bind\(""Monitors"", ""(Enabled|ScreenLift|Demo)""").Count);
        Assert.Contains("var s = Plugin.Instance.Settings;", view);
        foreach (var file in new[] { "ArenaMapPort.Client/CompactConfiguration.cs", "ArenaMapPort.Client/MapConfigVisibility.cs" })
            Assert.DoesNotContain("Monitors", Text(file));
    }

    [Fact]
    public void TheLiftIsInPropLocalUnitsAddedToTheMountAndLive()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        var mount = Between(view, "static Vector3 MountPosition(float lift)", "static Color ToColor");
        Assert.Contains("ArenaScoreMonitorMount.Position[0] + lift", mount);
        // the canvas is placed in the PROP's local space (a child at the mount pose), never by a metre offset along its normal
        Assert.DoesNotContain("rt.position", Code(view));
        Assert.DoesNotContain("forward *", Code(view));
        Assert.Contains("rt.localPosition = MountPosition(lift);", view);
        var adjust = Between(view, "static void AdjustLift()", "static void Present()");
        Assert.Contains("_lift.Value", adjust);
        Assert.Contains("m.Root.transform.localPosition = MountPosition(lift);", adjust);
        // the default and range of the key
        Assert.Contains("const float LiftDefault = 0.0100f, LiftMin = 0f, LiftMax = 0.03f;", view);
    }

    [Fact]
    public void TheCanvasIsWorldSpaceOnLayerZeroUnderTheProp()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        var build = Between(view, "static Monitor Build(ArenaScoreMonitor row, Transform prop)", "static RectTransform CreateElement");
        Assert.Contains("root.layer = 0;", build);
        Assert.Contains("rt.SetParent(prop, false);", build);
        Assert.Contains("canvas.renderMode = RenderMode.WorldSpace;", build);
        Assert.Contains("ArenaScoreMonitorMount.Rotation[0]", build);
        Assert.Contains("ArenaScoreMonitorMount.Scale", build);
        Assert.Contains("ArenaScoreMonitorMount.Width", build);
        Assert.DoesNotContain("AddComponent<GraphicRaycaster>", Code(view));
        Assert.DoesNotContain("CanvasScaler", Code(view));
    }

    [Fact]
    public void TheFontIsBenderNormalNeverTheStyleSourceOrAnOverlayAndANegativeResultIsCached()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        var font = Between(view, "static void ResolveFont()", "static void DisposeAssets()");
        Assert.Contains("DON'T USE", font);
        Assert.Contains("\"Overlay\"", font);
        Assert.Contains("\"Bender Normal\"", font);
        Assert.Contains("_fontTried = true;", font);
        Assert.Single(Regex.Matches(view, @"Resources\.LoadAll<TMP_FontAsset>\(""UI/Fonts""\)"));
        Assert.Contains("if (!_fontTried) ResolveFont();", view);
        Assert.Single(Regex.Matches(font, @"LogWarning\("));
    }

    [Fact]
    public void TheTickCostsOneFlagReadWhileNothingRunsAndNeverThrows()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        var tick = Between(view, "internal static void Tick()", "static void Advance()");
        int first = tick.IndexOf("if (!_installed || (!_raidLoaded && _claim == 0 && _demoClaim == 0)) return;", StringComparison.Ordinal);
        int body = tick.IndexOf("try", StringComparison.Ordinal);
        Assert.True(first > 0 && first < body, "the early return comes before anything else");
        Assert.Contains("catch (Exception e)", tick);
        Assert.Contains("int version = hub.Version;", tick);
        Assert.DoesNotContain("Find", Code(tick));
        // the clock is read only when a timer face exists and only formatted on a change of the whole second
        var advance = Between(view, "static void Advance()", "// ---- claim / teardown");
        Assert.Contains("_timerCount > 0 ? ScoreMonitorModel.ClockSecond", advance);
        Assert.Contains("second != _clockSecond", advance);
        // building is bounded
        Assert.Contains("const int BuildPerFrame = 3, SpritesPerFrame = 1;", view);
        Assert.Contains("BuildBudgetMs = 6f", view);
    }

    [Fact]
    public void EverySceneHandlerIsGuardedAndTheRaidEndDestroysWhatItBuilt()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        var loaded = Between(view, "static void OnSceneLoaded(Scene scene, LoadSceneMode mode)", "static void OnSceneUnloaded(Scene scene)");
        Assert.Contains("try", loaded);
        Assert.Contains("catch (Exception e)", loaded);
        Assert.Contains("ArenaMapRaid.MapForScriptsScene(scene.name)", loaded);
        var unloaded = Between(view, "static void OnSceneUnloaded(Scene scene)", "// ---- the tick");
        Assert.Contains("catch (Exception e)", unloaded);
        Assert.Contains("ArenaMapRaid.MapForScriptsScene(scene.name) == null", unloaded);
        int flag = unloaded.IndexOf("_raidLoaded = false;", StringComparison.Ordinal);
        int teardown = unloaded.IndexOf("Teardown(\"raid ended\", destroy: true);", StringComparison.Ordinal);
        int assets = unloaded.IndexOf("DisposeAssets();", StringComparison.Ordinal);
        int reset = unloaded.IndexOf("ScoreMonitors.Hub.ResetForNewRaid();", StringComparison.Ordinal);
        Assert.True(flag > 0 && flag < teardown && teardown < assets && assets < reset, "refuse claims, remove the canvases, then the sprites, then void the claim");
        // sprites and textures made here are destroyed with the raid
        var dispose = Between(view, "static void DisposeAssets()", "// ---- building");
        Assert.Contains("UnityEngine.Object.Destroy(sprite)", dispose);
        Assert.Contains("UnityEngine.Object.Destroy(texture)", dispose);
    }

    [Fact]
    public void EveryUnityObjectOfABuiltMonitorIsNullCheckedBeforeUse()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        var present = Between(view, "static void Present()", "static void Apply(Monitor m, in ScoreMonitorFrame f)");
        Assert.Contains("if (m.Root == null)", present);
        var setText = Between(view, "static void SetText(", "static void SetBlocks(");
        Assert.Contains("text == null", setText);
        Assert.Contains("col == null || col.Box == null", Between(view, "static void SetToggles(", "static void LayoutToggles("));
        Assert.Contains("if (m.BlockA != null", Between(view, "static void SetBlocks(", "static void SetToggles("));
        var teardown = Between(view, "static void Teardown(string why, bool destroy)", "// ---- assets");
        Assert.Contains("m.Root != null", teardown);
    }

    [Fact]
    public void TheDemoClaimsAsDeveloperDemoAndBacksOffWhenRefused()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        Assert.Contains("const string DemoOwner = \"developer-demo\";", view);
        var demo = Between(view, "static void TickDemo(ScoreMonitorHub hub)", "\n    }\n}");
        Assert.Contains("hub.Claim(DemoOwner, true)", demo);
        Assert.Contains("DemoRefusedSeconds", demo);
        Assert.Contains("if (hub.IsClaimed) return;", demo);
        Assert.Contains("hub.SetSides(_demoClaim", demo);
        Assert.Contains("hub.SetScore(_demoClaim", demo);
        Assert.Contains("hub.SetClock(_demoClaim", demo);
        Assert.Contains("_demo.Value == DemoMode.Off", demo);
    }

    [Fact]
    public void ThePropIsFoundByPathAndMountPointAndTheSpritesCannotBreakABuild()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        var find = Between(view, "static bool TryFindProp(", "static Vector3 MountPosition");
        Assert.Contains("BoothDoorRepairs.FindAllByPath(row.Path)", find);
        Assert.Contains("t.gameObject.scene.name != row.Scene", find);
        Assert.Contains("row.AppliesTo(world.x, world.y, world.z)", find);
        // an inactive prop is never switched on
        Assert.DoesNotContain("SetActive(true)", Between(view, "static void BuildSome()", "static bool TryFindProp("));
        // the 20 s give-up clock of missing props also waits for the content scenes: the Scripts scene loads first and a claim (the demo's) can precede them by tens of seconds
        Assert.Contains("real - _claimedAt > PendingTimeoutSeconds && real - _sceneActivityAt > PendingTimeoutSeconds", Between(view, "static void BuildSome()", "static bool TryFindProp("));
        Assert.Contains("if (ArenaMapRaid.IsOwnedScene(scene.name)) _sceneActivityAt = Time.realtimeSinceStartup;",
            Between(view, "static void OnSceneLoaded(Scene scene, LoadSceneMode mode)", "static void OnSceneUnloaded(Scene scene)"));
        // a sprite that fails is recorded, never rethrown: the faces degrade
        var load = Between(view, "static void LoadSprite(string name)", "// EFT keeps its TMP fonts");
        Assert.Contains("_missing.Add(name);", load);
        Assert.DoesNotContain("throw;", load);
        Assert.Contains("texture.width != info.Width || texture.height != info.Height", load);
        Assert.Contains("ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true)", load);
        // one Warning for all missing sprites, none from the load itself
        Assert.DoesNotContain("LogWarning(", load);
        Assert.Single(Regex.Matches(Between(view, "static void PrepareAssets()", "static string MonitorsFolder()"), @"LogWarning\("));
    }

    [Fact]
    public void PluginAndHarmonyPatchesCallTheTwoEntryPointsExactlyOnce()
    {
        var plugin = Text("ArenaMapPort.Client/Plugin.cs");
        Assert.Single(Regex.Matches(plugin, @"ScoreMonitorView\.Tick\(\);"));
        var late = Between(plugin, "void LateUpdate()", "void Awake()");
        Assert.Contains("ScoreMonitorView.Tick();", late);
        // before the gate poll and the notices: MapNoticeWiringTests pins MapNotices.Tick() as the last statement of LateUpdate
        Assert.True(late.IndexOf("ScoreMonitorView.Tick();", StringComparison.Ordinal) < late.IndexOf("ArenaInstallationGate.PollOutcome();", StringComparison.Ordinal));
        Assert.True(late.IndexOf("ArenaLightingRepairs.UpdateArtificialSun();", StringComparison.Ordinal) < late.IndexOf("ScoreMonitorView.Tick();", StringComparison.Ordinal));
        var patches = Text("ArenaMapPort.Client/HarmonyPatches.cs");
        Assert.Single(Regex.Matches(patches, @"ScoreMonitorView\.Install\(\);"));
        Assert.True(patches.IndexOf("SpawnGates.Install();", StringComparison.Ordinal) < patches.IndexOf("ScoreMonitorView.Install();", StringComparison.Ordinal));
        // the install runs inside InstallAll, after Settings exists (Plugin.Awake binds it first)
        Assert.True(patches.IndexOf("internal static void InstallAll(Harmony harmony)", StringComparison.Ordinal) < patches.IndexOf("ScoreMonitorView.Install();", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHubReachesTheViewOnlyThroughTheInstallDelegatesAndNeverReadsConfigFromAnotherThread()
    {
        var view = Text("ArenaMapPort.Client/ScoreMonitorView.cs");
        Assert.Contains("ScoreMonitors.Hub = new ScoreMonitorHub(() => _raidLoaded, () => _usable, () => _enabledFlag);", view);
        Assert.Contains("static volatile bool _enabledFlag;", view);
        Assert.Contains("_enabled.SettingChanged += (sender, args) => _enabledFlag = _enabled.Value;", view);
        // the hub's delegates read plain volatile fields, not a ConfigEntry, because any thread may call the public members
        Assert.DoesNotContain("() => _enabled.Value", view);
    }
}
