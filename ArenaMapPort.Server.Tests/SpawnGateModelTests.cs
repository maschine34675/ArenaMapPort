#nullable disable
using System;
using System.IO;
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The pure rules behind SpawnGates (SpawnGateModel): slide direction per Equator leaf, the carver box from a part's
/// world bounds, the timed release, the thin axis a probe crosses and the probe verdict. The slide distance and margins
/// are design data (docs/progress/spawn-gates.md); these tests pin them so a later change is deliberate.
/// </summary>
public sealed class SpawnGateModelTests
{
    [Theory]
    [InlineData("Door_Sliding_Respawn_L", 1)]
    [InlineData("Door_Sliding_Respawn_R", -1)]
    [InlineData("Door_Sliding_Respawn", 0)]
    [InlineData("Door_Sliding_Respawn (3)", 0)]
    [InlineData("Apron_bus_MAZ_doors (2)", 0)]
    [InlineData("Gates_Depot_Door_L", 0)]
    [InlineData("", 0)]
    public void OnlyEquatorsTwoLeavesSlideAndInOppositeDirections(string name, int expected)
    {
        Assert.Equal(expected, SpawnGateModel.SlideDirection(name));
    }

    [Fact]
    public void SlideDistanceIsTheLeafWidth()
    {
        Assert.Equal(1.38f, SpawnGateModel.SlideDistance, 3);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void GatesAreHeldOnlyWhenEnabledAndAsked(bool enabled, bool requested, bool testHold, bool expected)
    {
        Assert.Equal(expected, SpawnGateModel.ShouldHold(enabled, requested, testHold));
    }

    [Theory]
    [InlineData(0f, 30f, false)]
    [InlineData(29.9f, 30f, false)]
    [InlineData(30f, 30f, true)]
    [InlineData(500f, 0f, false)]
    [InlineData(500f, -1f, false)]
    public void TimedReleaseFiresAtTheHoldLengthAndNeverForZero(float elapsed, float hold, bool expected)
    {
        Assert.Equal(expected, SpawnGateModel.AutoReleaseDue(elapsed, hold));
    }

    [Fact]
    public void CarverBoxGrowsSidewaysAndUpButKeepsTheFloorLine()
    {
        // An Equator leaf: 1.38 m wide (x), 2.05 m high, 0.03 m thick (z), floor at y 0.05.
        SpawnGateModel.CarverBox(-4.33f, 0.05f, 39.985f, -2.95f, 2.10f, 40.015f,
            out float cx, out float cy, out float cz, out float sx, out float sy, out float sz);
        Assert.Equal(1.38f + 0.4f, sx, 3);
        Assert.Equal(0.03f + 0.4f, sz, 3);
        Assert.Equal(2.05f + 0.5f, sy, 3);
        Assert.Equal(-3.64f, cx, 3);
        Assert.Equal(40.0f, cz, 3);
        Assert.Equal(0.05f + (2.05f + 0.5f) * 0.5f, cy, 3); // bottom stays at the part's floor line
    }

    [Fact]
    public void CarverBoxRejectsInvertedBounds()
    {
        Assert.Throws<ArgumentException>(() => SpawnGateModel.CarverBox(1f, 0f, 0f, 0f, 1f, 1f,
            out _, out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(0.03f, 1.38f, 0)]  // a leaf thin in x: the probe crosses along x
    [InlineData(1.38f, 0.03f, 2)]  // a leaf thin in z: the probe crosses along z
    [InlineData(1f, 1f, 0)]
    public void ProbeCrossesTheThinAxis(float sizeX, float sizeZ, int axis)
    {
        Assert.Equal(axis, SpawnGateModel.ThinAxis(sizeX, sizeZ));
    }

    [Theory]
    [InlineData(0, false)] // PathComplete
    [InlineData(1, true)]  // PathPartial
    [InlineData(2, true)]  // PathInvalid
    public void OnlyACompletePathMeansTheGateIsOpenForBots(int status, bool sealedExpected)
    {
        Assert.Equal(sealedExpected, SpawnGateModel.Sealed(status));
    }
}

/// <summary>
/// SpawnGates itself sits on Unity and BepInEx types; its wiring is read as text like the other runtime files.
/// </summary>
public sealed class SpawnGatesWiringTests
{
    static string Text(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void InstalledRightAfterTheBoothDoorConfig()
    {
        Assert.Matches(@"BoothDoorRepairs\.BindConfig\(\);[^\n]*\n\s*SpawnGates\.Install\(\);", Text("ArenaMapPort.Client/HarmonyPatches.cs"));
    }

    [Fact]
    public void EveryGatePartIsOfferedBeforeItIsSwitchedOff()
    {
        var code = Text("ArenaMapPort.Client/BoothDoorRepairs.cs");
        Assert.Equal(2, Regex.Matches(code, @"SpawnGates\.Offer\(").Count); // door leaves and descriptor barriers
        // In both loops the part is offered first and only switched off when the offer says "not held".
        foreach (var variable in new[] { "t", "part" })
        {
            int offer = code.IndexOf($"SpawnGates.Offer({variable})", StringComparison.Ordinal);
            int off = code.IndexOf($"{variable}.gameObject.SetActive(false);", offer, StringComparison.Ordinal);
            Assert.True(offer > 0 && off > offer && off - offer < 400, $"offer before switch-off for '{variable}'");
        }
        Assert.Contains("const string DoorPrefix = SpawnGateModel.DoorPrefix;", code);
    }

    [Fact]
    public void CarversWaitForThePortedNavMesh()
    {
        Assert.Contains("Installed?.Invoke()", Text("ArenaMapPort.Client/NavMeshInstaller.cs"));
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        Assert.Contains("NavMeshInstaller.Installed += OnNavMeshInstalled", gates);
        Assert.Contains("carveOnlyStationary = true", gates);
        Assert.Contains("obstacle.carving = true", gates);
    }

    [Fact]
    public void PublicContractForAModePlugin()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        Assert.Contains("public static class SpawnGates", gates);
        Assert.Contains("public static bool Hold(string reason)", gates);
        Assert.Contains("public static void Release(string reason)", gates);
        Assert.Contains("public static bool IsHeld", gates);
    }

    [Fact]
    public void ConfigKeysAreBoundOnSettingsWithDefaults()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        Assert.Contains("Plugin.Instance.Settings", gates);
        Assert.DoesNotContain("Plugin.Instance.Config", gates);
        foreach (var key in new[] { "\"SpawnGates\", true", "\"SpawnGateCarve\", true", "\"SpawnGateHold\", false",
                                    "\"SpawnGateSlideSeconds\", 1.0f", "\"SpawnGateMaxHoldSeconds\", 300f", "\"SpawnGateHoldSeconds\", 30f" })
        {
            Assert.Contains(key, gates);
        }
    }

    [Fact]
    public void EveryRaidStateResetsOnTheScriptsSceneUnload()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        int start = gates.IndexOf("static void OnSceneUnloaded(Scene scene)", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = gates.IndexOf("\n        }", start, StringComparison.Ordinal);
        var body = gates.Substring(start, end - start);
        Assert.Contains("ArenaMapRaid.MapForScriptsScene(scene.name) == null) return;", body);
        foreach (var reset in new[] { "DestroyCarvers();", "UnityEngine.Object.Destroy(_host);", "_host = null;", "_runner = null;",
                                      "_openCoroutine = null;", "_parts.Clear();", "_closedPose.Clear();", "_bounds.Clear();", "_baseline.Clear();",
                                      "_requested = false;", "_released = false;", "_navMeshReady = false;", "_opening = false;",
                                      "_startedAt = -1f;", "_heldSince = -1f;" })
        {
            Assert.Contains(reset, body);
        }
    }

    [Fact]
    public void HoldRestoresTheClosedPoseAndCancelsAnOpeningInProgress()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        // The class doc quotes both signatures inside <c>...</c>; the method bodies follow a signature at a line end.
        int start = Regex.Match(gates, @"public static bool Hold\(string reason\)\s*\r?\n").Index;
        int end = Regex.Match(gates, @"public static void Release\(string reason\)\s*\r?\n").Index;
        Assert.True(start > 0 && end > start);
        var body = gates.Substring(start, end - start);
        Assert.Contains("_runner.StopCoroutine(_openCoroutine);", body);
        Assert.Contains("part.localPosition = pose.LocalPosition;", body);
        Assert.Contains("part.gameObject.SetActive(true);", body);
        Assert.Contains("_heldSince = Time.time;", body);
        // The safety limit counts from the hold when that came after the raid timer started.
        Assert.Contains("Mathf.Max(_startedAt, _heldSince)", gates);
    }
}
