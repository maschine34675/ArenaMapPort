#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins for the gate and door sounds (docs/progress/gate-sounds.md): the Unity glue (GateSounds.cs) and the four hook points in
/// SpawnGates.cs cannot run here, so the facts the host test and the other pins depend on are read from the source text, like
/// DoorPoseWiringTests does for the door poses: where SpawnGates calls in, that every entry point is guarded, which EFT calls the player
/// makes (and that it makes the playing call exactly once: BetterAudio plays before it processes the occlusion, a retry would double the sound),
/// that the keys are developer keys on Settings, and that the build stages the package in Release and DllOnly builds alike.
/// </summary>
public sealed class GateSoundWiringTests
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

    [Fact]
    public void SpawnGatesCallsGateSoundsFromExactlyFourPlacesAndTheDoorPoseGuardsStay()
    {
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        // the key of a part: a field of PosedPart, set where NotePoses builds it, from the first pose row and the part's closed local position
        Assert.Contains("internal GateSoundModel.PartKey Key;", Between(gates, "sealed class PosedPart", "static ConfigEntry<bool>"));
        var note = Between(gates, "internal static void NotePoses(Transform part, ArenaDoorPose[] poses)", "internal static bool Park(Transform part)");
        int created = note.IndexOf("var posed = new PosedPart", StringComparison.Ordinal);
        int key = note.IndexOf("Key = GateSoundModel.KeyOf(poses[0].Barrier, poses[0].Part, poses[0].At, part.localPosition.x, part.localPosition.y, part.localPosition.z),", StringComparison.Ordinal);
        int stored = note.IndexOf("_posed[part] = posed;", StringComparison.Ordinal);
        int prepare = note.IndexOf("GateSounds.Prepare();", StringComparison.Ordinal);
        Assert.True(created > 0 && created < key && key < stored && stored < prepare, "new PosedPart { Key = ... }, _posed[part] = posed, GateSounds.Prepare()");
        // the release: after the longest drive is known, before the loop moves anything, only when the parts drive (not when they snap)
        var open = Between(gates, "static IEnumerator Open(string reason)", "static void OnSceneUnloaded(Scene scene)");
        int longest = open.IndexOf("_openLongestSeconds = longest;", StringComparison.Ordinal);
        int call = open.IndexOf("if (drive) GateSounds.OnRelease(SoundKeys(drivers), scale);", StringComparison.Ordinal);
        int loop = open.IndexOf("while (true)", StringComparison.Ordinal);
        Assert.True(longest > 0 && longest < call && call < loop, "_openLongestSeconds, the OnRelease call, the drive loop");
        Assert.Single(Regex.Matches(gates, @"GateSounds\.OnRelease\("));
        // the pins of the door poses stay valid: one scale read, one switch-off in Open
        Assert.Single(Regex.Matches(gates, @"_driveScale\.Value"));
        Assert.Single(Regex.Matches(open, @"SetActive\(false\)"));
        Assert.Contains("float scale = _driveScale.Value;", open);
        // a lamp part has no clip: only the parts that move are handed over
        var keys = Between(gates, "static GateSoundModel.PartKey[] SoundKeys(List<PosedPart> drivers)", "static IEnumerator Open(string reason)");
        Assert.Contains("if (!driver.SwitchOnly) keys.Add(driver.Key);", keys);
        // a Hold stops what the release before started, after it cancelled an opening in progress
        var hold = Between(gates, "        public static bool Hold(string reason)\n        {", "        public static void Release(string reason)\n        {");
        int cancelled = hold.IndexOf("cancelledOpening = true;", StringComparison.Ordinal);
        int stop = hold.IndexOf("GateSounds.OnHold();", StringComparison.Ordinal);
        int reclose = hold.IndexOf("int reclosed = 0, unparked = 0;", StringComparison.Ordinal);
        Assert.True(cancelled > 0 && cancelled < stop && stop < reclose);
        Assert.Single(Regex.Matches(gates, @"GateSounds\.OnHold\("));
        Assert.Single(Regex.Matches(gates, @"GateSounds\.Prepare\("));
        // nothing else of the sound code lives in SpawnGates
        Assert.DoesNotContain("PlayAtPoint", gates);
        Assert.DoesNotContain("AudioClip", gates);
    }

    [Fact]
    public void EveryEntryPointOfGateSoundsIsGuardedAndNeverReachesEftsLoadChain()
    {
        var code = Text("ArenaMapPort.Client/GateSounds.cs");
        foreach (var signature in new[] { "internal static void Prepare()", "internal static void OnRelease(GateSoundModel.PartKey[] keys, float scale)", "internal static void OnHold()" })
        {
            int at = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, signature);
            var body = code.Substring(at, code.IndexOf("\n        }\n", at, StringComparison.Ordinal) - at);
            Assert.Contains("try", body);
            Assert.Contains("catch (Exception e)", body);
            Assert.Contains("LogWarning", body);
        }
        Assert.Contains("static void OnSceneUnloaded(Scene scene)", code);
        Assert.Contains("try { Dispose(_state); }", code);
    }

    [Fact]
    public void ThePlayerMakesOneBetterAudioCallPerSoundWithTheDocumentedEftNames()
    {
        var code = Text("ArenaMapPort.Client/GateSounds.cs");
        Assert.Single(Regex.Matches(code, @"\.PlayAtPoint\("));        // never retried: BetterAudio plays the clip BEFORE it processes the occlusion
        var play = Between(code, "static void Play(State state", "static IEnumerator ReportPlaying");
        Assert.Contains("MonoBehaviourSingleton<BetterAudio>.Exist(out var audio)", play);
        Assert.Contains("audio.PlayAtPoint(position, clip, BetterAudio.AudioSourceGroupType.InteractiveObjects, rolloff, volume, occlusion)", play);
        Assert.Contains("SpatialAudioSystem.Initialized && MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated", play);
        Assert.Contains("? EOcclusionTest.OneShotPropagation : EOcclusionTest.None", play);
        int occlusion = play.IndexOf("var occlusion =", StringComparison.Ordinal);
        int loop = play.IndexOf("foreach (int index in rows)", StringComparison.Ordinal);
        Assert.True(occlusion > 0 && occlusion < loop, "the occlusion mode is decided once, before the rows");
        var catchAt = play.IndexOf("catch (Exception e)", StringComparison.Ordinal);
        Assert.True(catchAt > 0 && play.IndexOf("continue;", catchAt, StringComparison.Ordinal) > catchAt && play.IndexOf(".PlayAtPoint(", catchAt, StringComparison.Ordinal) < 0,
            "an exception ends that sound: no second PlayAtPoint after it");
        // raid start: sources started before GameStatus.Started are stopped by EFT's audio reset
        Assert.Contains("Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started", code);
        Assert.Contains("PlayWhenStarted(state, rows, scale, keys.Length)", code);
        // the sound that failed is one Warning per clip (a HashSet of reasons), the rest is Debug
        Assert.Contains("state.Warned.Add(\"play \" + row.Clip)", code);
        Assert.Contains("state.Warned.Add(\"clip \" + id)", code);
        Assert.DoesNotContain("LogInfo", code);
        Assert.DoesNotContain("LogError", code);
    }

    [Fact]
    public void TheKeysAreDeveloperKeysOnSettingsAndNothingIsSearchedOrHashedOnTheMainThread()
    {
        var code = Text("ArenaMapPort.Client/GateSounds.cs");
        Assert.Contains("var s = Plugin.Instance.Settings;", code);
        Assert.DoesNotContain("Plugin.Instance.Config", code);
        Assert.Contains("s.Bind(\"Spawns\", \"GateSounds\", true,", code);
        Assert.Contains("s.Bind(\"Spawns\", \"GateSoundVolume\", 1.0f, new ConfigDescription(", code);
        Assert.Contains("new AcceptableValueRange<float>(0f, GateSoundModel.MaxUserVolume)", code);
        Assert.Contains("s.Bind(\"Spawns\", \"GateSoundFit\", true,", code);
        Assert.Contains("s.Bind(\"Spawns\", \"GateSoundRolloffScale\", 1.0f, new ConfigDescription(", code);
        // SpawnGates' own scale entry is read through the config file: a second _driveScale.Value in SpawnGates would break its pins
        Assert.Contains("s.TryGetEntry(new ConfigDefinition(ScaleSection, ScaleKey), out _driveScale);", code);
        Assert.Contains("const string ScaleSection = \"Spawns\", ScaleKey = \"SpawnGateDriveScale\";", code);
        // none of them is public: a public key would have to be listed in CompactConfiguration.IsPublic and MapConfigVisibility.IsPlayerOption
        Assert.DoesNotContain("GateSound", Text("ArenaMapPort.Client/CompactConfiguration.cs"));
        Assert.DoesNotContain("GateSound", Text("ArenaMapPort.Client/MapConfigVisibility.cs"));
        // no scene-wide search, no managed hash over megabytes, no per-frame work
        foreach (var forbidden in new[] { "FindObjectsOfType", "FindObjectOfType", "GetComponentsInChildren", "SHA256", "void Update(", "void LateUpdate(" })
            Assert.DoesNotContain(forbidden, code);
        // the package sits beside the plugin, one folder per map key, like the acoustics
        Assert.Contains("Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), \"gate-sounds\", key)", code);
        // owned scene only; script-created clips are rebuilt after an audio reset
        Assert.Contains("if (ArenaMapRaid.MapForScriptsScene(scene.name) == null) return;", code);
        Assert.Contains("AudioSettings.OnAudioConfigurationChanged += changed => _audioResets++;", code);
        Assert.Contains("loaded.BuiltAtReset == _audioResets", code);
    }

    [Fact]
    public void TheHarmonyInstallerStartsGateSoundsAfterSpawnGatesBecauseItReadsItsDriveScaleEntry()
    {
        var patches = Text("ArenaMapPort.Client/HarmonyPatches.cs");
        int gates = patches.IndexOf("SpawnGates.Install();", StringComparison.Ordinal);
        int sounds = patches.IndexOf("GateSounds.Install();", StringComparison.Ordinal);
        Assert.True(gates > 0 && gates < sounds);
        Assert.Single(Regex.Matches(patches, @"GateSounds\.Install\("));
    }

    [Fact]
    public void TheBuildStagesThePackageInReleaseAndDllOnlyBuildsAndNamesTheExtractorWhenItIsMissing()
    {
        var project = Text("ArenaMapPort.Client/ArenaMapPort.Client.csproj");
        var target = Between(project, "<Target Name=\"ArenaMapPortStageGateSounds\"", "</Target>");
        Assert.Contains("AfterTargets=\"ArenaMapPortDistCopy\"", target);
        Assert.Contains("Condition=\"'$(Configuration)'=='Release'\"", target);
        Assert.DoesNotContain("ArenaMapPortDllOnly", target);          // staged with the DLL in every Release build, like the acoustics
        Assert.Contains("run tools/extract/extract_gate_sounds.py clips", target);
        foreach (var key in new[] { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" })
        {
            Assert.Contains($"$(ArenaMapPortGateSoundsDir)\\{key}\\gate-sounds.json;$(ArenaMapPortGateSoundsDir)\\{key}\\*.wav\"><MapKey>{key}</MapKey>", target);
        }
        Assert.Contains("DestinationFiles=\"@(ArenaMapPortGateSoundFile->'$(ArenaMapPortDistDir)\\gate-sounds\\%(MapKey)\\%(Filename)%(Extension)')\"", target);
        var tests = Text("ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj");
        Assert.Contains("ArenaMapPort.Client\\GateSoundModel.cs", tests);
        Assert.Contains("ArenaMapPort.Client\\PcmWav.cs", tests);
    }

    [Fact]
    public void TheModelAndTheWavReaderUseNoUnityTypeSoTheTestsLinkThem()
    {
        foreach (var file in new[] { "GateSoundModel.cs", "PcmWav.cs" })
        {
            var code = Text("ArenaMapPort.Client/" + file);
            Assert.DoesNotContain("UnityEngine", code);
            Assert.DoesNotContain("Plugin.Log", code);
            Assert.DoesNotContain("BepInEx", code);
        }
    }

    [Fact]
    public void TheSoundsAreSilentByDesignWhereNothingDrivesAndTheHostChecksAreRecorded()
    {
        var code = Text("ArenaMapPort.Client/GateSounds.cs");
        Assert.Contains("Silent by design: a plain raid", code);
        Assert.Contains("keys.Length == 0", code);
        var gates = Text("ArenaMapPort.Client/SpawnGates.cs");
        // Release without a hold returns before Open; Park never calls the sound code
        var park = Between(gates, "internal static bool Park(Transform part)", "internal static void SealParked(string why)");
        Assert.DoesNotContain("GateSounds", park);
        var release = Between(gates, "        public static void Release(string reason)\n        {", "        internal static void Install()\n        {");
        Assert.DoesNotContain("GateSounds", release);
        Assert.Contains("Host checks", Text("docs/progress/gate-sounds.md"));
    }
}
