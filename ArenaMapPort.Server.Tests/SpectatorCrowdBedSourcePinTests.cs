using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins for the crowd murmur bed (docs/progress/spectator-audio.md "Crowd bed"). SpectatorCrowdBed.cs is Unity glue and cannot run here, so the rules it must keep are
/// read from the text: only owned AudioSources (no BetterAudio pool source), no scene search and no raycast, nothing allocated per frame, the three keys bound through Settings
/// as developer keys and none of them public, the sources created only once the raid has started, rebuilt by the audio reset and removed with the crowd, every NoiseProbe tap
/// behind NoiseProbe.Armed, one log tag at Debug with the Warning only for a damaged bed, and the Install and Create wiring.
/// </summary>
public sealed class SpectatorCrowdBedSourcePinTests
{
    private const string Glue = "ArenaMapPort.Client/SpectatorCrowdBed.cs";

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
    public void TheSourcesAreOwnedAndNothingSearchesTheSceneOrCastsARay()
    {
        var code = Code(Glue);
        Assert.DoesNotMatch(@"FindObjectsOfType|FindObjectOfType|FindObjectsByType|FindObjectByType|Resources\.FindObjects|GameObject\.Find|FindWithTag|GetComponentsInChildren|GetComponentInParent", code);
        Assert.DoesNotMatch(@"Raycast|Physics\.|SpatialAudioSystem|ProcessSourceOcclusion|EOcclusionTest", code);
        // no BetterAudio pool source: the bed owns its AudioSources and only reads the Environment mixer group of the singleton
        Assert.DoesNotMatch(@"\.GetSource\(|BetterSource|\.Release\(\)", code);
        Assert.Single(Regex.Matches(code, @"AddComponent<AudioSource>\(\)"));
        Assert.Contains("BetterAudio.AudioSourceGroupType.Environment", code);
        Assert.Contains("groups[index].MixerGroup", code);
        Assert.Contains("source.outputAudioMixerGroup = group;", code);
        // the stand sources
        foreach (var line in new[] { "source.loop = true;", "source.spatialBlend = 1f;", "source.spread = 70f;", "source.dopplerLevel = 0f;", "source.priority = 200;",
                     "source.playOnAwake = false;", "source.rolloffMode = AudioRolloffMode.Custom;", "source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);" })
            Assert.Contains(line, code);
        Assert.DoesNotContain("AudioRolloffMode.Logarithmic", code);   // it never reaches silence
        // a child of the crowd root: removed with the crowd
        Assert.Contains("root.transform.SetParent(crowd.transform, false);", code);
    }

    [Fact]
    public void TheFramePathAllocatesNothingAndTheWatchdogIsBounded()
    {
        var code = Code(Glue);
        var frame = Between(code, "void Update()", "catch (Exception e)");
        Assert.DoesNotMatch(@"\bnew\b|\$""|Linq|\.ToArray\(|\.ToList\(|string\.Format|ToString\(", frame);
        var watch = Between(code, "void Watch()", "void TearDown(");
        Assert.DoesNotMatch(@"\bnew\b|\$""(?!\{Tag\})", watch.Replace("$\"{Tag}", ""));
        Assert.Contains("const int MaxRestarts = 8;", code);
        Assert.Contains("if (_restarts >= MaxRestarts)", watch);
        Assert.Contains("const float WatchSeconds = 2f;", code);
        // the smoothing is the model's, on unscaled time, and the volume is only written when it moved
        Assert.Contains("_excitement.Step(Time.unscaledDeltaTime);", frame);
        Assert.Contains("if (Mathf.Abs(volume - _appliedVolume) > 0.0005f) ApplyVolume(volume);", frame);
    }

    [Fact]
    public void TheThreeKeysAreDeveloperKeysOnSettingsAndNoneIsPublic()
    {
        var code = Code(Glue);
        var install = Between(code, "internal static void Install()", "internal static void Create(");
        Assert.Contains("var s = Plugin.Instance.Settings;", install);
        Assert.DoesNotContain("Plugin.Instance.Config", code);
        Assert.Contains("s.Bind(\"Spectators\", \"CrowdBed\", true,", install);
        Assert.Contains("s.Bind(\"Spectators\", \"CrowdBedVolume\", SpectatorCrowdBedModel.DefaultVolume, new ConfigDescription(", install);
        Assert.Contains("s.Bind(\"Spectators\", \"CrowdBedReach\", SpectatorCrowdBedModel.DefaultReach, new ConfigDescription(", install);
        Assert.Equal(3, Regex.Matches(code, @"\bs\.Bind\(").Count);
        Assert.Contains("new AcceptableValueRange<float>(0f, SpectatorCrowdBedModel.MaxVolume)", install);
        Assert.Contains("new AcceptableValueRange<float>(SpectatorCrowdBedModel.MinReach, SpectatorCrowdBedModel.MaxReach)", install);
        // honoured only with DeveloperMode, like SpectatorCalls: the defaults apply to a player
        Assert.Contains("static bool BedOn => !DeveloperOptions.Enabled || _enabled == null || _enabled.Value;", code);
        Assert.Contains("DeveloperOptions.Enabled && _volume != null ? SpectatorCrowdBedModel.ClampVolume(_volume.Value) : SpectatorCrowdBedModel.DefaultVolume;", code);
        Assert.Contains("DeveloperOptions.Enabled && _reach != null ? SpectatorCrowdBedModel.ClampReach(_reach.Value) : SpectatorCrowdBedModel.DefaultReach;", code);
        // a public key would have to be listed in CompactConfiguration.IsPublic and MapConfigVisibility.IsPlayerOption
        Assert.DoesNotContain("CrowdBed", Text("ArenaMapPort.Client/CompactConfiguration.cs"));
        Assert.DoesNotContain("CrowdBed", Text("ArenaMapPort.Client/MapConfigVisibility.cs"));
        // the values are polled in the tick: no setting handler runs on whichever thread sets a value
        Assert.DoesNotContain("SettingChanged", code);
    }

    [Fact]
    public void TheSourcesExistOnlyOnceTheRaidHasStartedAndAreRebuiltByTheAudioReset()
    {
        var code = Code(Glue);
        var frame = Between(code, "void Update()", "catch (Exception e)");
        Assert.Contains("Singleton<AbstractGame>.Instance.Status == GameStatus.Started", frame);
        Assert.Contains("if (!on || !started)", frame);
        Assert.Contains("if (_stands == null && !Build()) return;", frame);
        Assert.Contains("TearDown(!on);", frame);                    // switched off frees the clip; merely not started keeps it for the start
        Assert.Contains("AudioSettings.OnAudioConfigurationChanged += component.AudioChanged;", code);
        Assert.Contains("AudioSettings.OnAudioConfigurationChanged -= AudioChanged;", code.Substring(code.IndexOf("void OnDestroy()", StringComparison.Ordinal)));
        var reset = Between(code, "if (_reset)", "bool started");
        Assert.Contains("TearDown(true);", reset);                   // the very Update then builds the clip and the sources again if the raid is running
        Assert.DoesNotContain("NoiseEv.Reset", reset);               // SpectatorAudio's tap already counts the one audio reset (the minute line would say resets=2)
        // the sources start at i/k of the loop
        Assert.Contains("source.timeSamples = Mathf.Clamp((int)(stand.StartFraction * _frames), 0, _frames - 1);", code);
        Assert.Contains("source.pitch = stand.Pitch;", code);
        // removal: the sources, the clip, the state
        var destroy = code.Substring(code.IndexOf("void OnDestroy()", StringComparison.Ordinal));
        Assert.Contains("TearDown(true);", destroy);
        Assert.Contains("SpectatorCrowd.State.Reset();", destroy);
        Assert.Contains("Current = null;", destroy);
    }

    [Fact]
    public void EveryTapSitsBehindArmedAndTheEventIsTheBedEvent()
    {
        var lines = Text(Glue).Split('\n').Select(l => l.Trim()).Where(l => Regex.IsMatch(l, @"NoiseProbe\.Note\w*\(") && !l.StartsWith("//")).ToArray();
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Contains("NoiseProbe.Armed", l));
        var code = Code(Glue);
        Assert.Equal(3, Regex.Matches(code, @"NoiseEv\.Bed").Count);                       // the start in Build and in Watch, the stop in TearDown
        Assert.DoesNotContain("NoiseEv.Reset", code);                                       // the bed-/bed+ events show its rebuild; the audio reset is counted once, by SpectatorAudio
        Assert.DoesNotMatch(@"&&\s*NoiseProbe\.Armed", code);                              // Armed first: the cost of Off is the one static bool read
        Assert.Contains("NoiseProbe.NoteAt(NoiseEv.Bed, go.transform.position, _stands[i].Label, 1, i, volume);", code);
        Assert.Contains("NoiseProbe.NoteAt(NoiseEv.Bed, stand.Go.transform.position, stand.Label, 0, i,", code);
    }

    [Fact]
    public void TheNoiseProbeCensusSeesTheBedBecauseItsSourcesAreOwnedLoopingPlayersOutsideThePool()
    {
        // The pooled BetterAudio walk of the census cannot see the stand sources (continuous looping players the bed owns): without ownedBed= a plateau from the bed would be the
        // blind spot the Equator investigation had once (docs/progress/equator-noise-bundle-block.md).
        var census = Between(Code("ArenaMapPort.Client/NoiseProbe.cs"), "static string Census(double now)", "static List<string> BuildDump(");
        Assert.Contains("var bed = SpectatorCrowdBed.Current;", census);
        Assert.Contains("\" ownedBed=\"", census);
        Assert.Contains("bed.PlayingStands", census);
        Assert.Contains("bed.StandCount", census);
        // the property reads at most six sources, allocates nothing and searches no scene
        var code = Code(Glue);
        var playing = Between(code, "internal int PlayingStands", "internal int StandCount");
        Assert.DoesNotMatch(@"\bnew\b|Find|GetComponents|Linq", playing);
        Assert.Contains("Source.isPlaying", playing);
    }

    [Fact]
    public void TheCrowdsRemovalResetsTheContractStateEvenWithoutABed()
    {
        // SpectatorCrowd.cs promises that the excitement is reset when the raid's crowd is removed; a package without a bed (or a bed that failed) has no bed component to do it.
        var crowd = Code("ArenaMapPort.Client/ArenaSpectators.cs");
        var destroy = Between(crowd, "void OnDestroy()", "void DisposeOwnedAssets()");
        Assert.Contains("if (SpectatorCrowdBed.Current == null) SpectatorCrowd.State.Reset();", destroy);   // a newer raid's live bed is left alone
    }

    [Fact]
    public void ThePlateauOfTheDistanceCurveKeepsFlatTangents()
    {
        // SmoothTangents on the two plateau keys overshoots gain 1 by 7 to 14 percent two thirds of the way to the minimum distance.
        var code = Code(Glue);
        Assert.Contains("for (int i = 2; i < keys.Length; i++) curve.SmoothTangents(i, 0f);", code);
        Assert.DoesNotMatch(@"for \(int i = 0; i < keys\.Length; i\+\+\) curve\.SmoothTangents", code);
    }

    [Fact]
    public void TheBedLogsAtDebugWithOneTagAndTheOnlyWarningsAreTheDamagedBedAndAStoppedBed()
    {
        var code = Code(Glue);
        Assert.Contains("const string Tag = \"[CrowdBed]\";", code);
        Assert.DoesNotContain("LogInfo", code);
        Assert.DoesNotContain("LogError", code);
        Assert.Equal(Regex.Matches(code, @"LogDebug\(").Count, Regex.Matches(code, @"LogDebug\(\$""\{Tag\}").Count);
        Assert.Equal(2, Regex.Matches(code, "LogWarning").Count);
        Assert.Contains("the crowd murmur is unavailable", code);
        Assert.Contains("the crowd murmur stopped", code);
        // a package without a bed is one Debug line, never a Warning
        var create = Between(code, "internal static void Create(", "void AudioChanged(");
        var missing = Between(create, "manifest.bed == null", "return;");
        Assert.Contains("LogDebug", missing);
        Assert.DoesNotContain("LogWarning", missing);
        // the hash check goes through the CNG provider and stays fail-closed, like the calls
        Assert.Contains("var crypto = CryptoProviders.Select(null);", create);
        Assert.Contains("throw new InvalidDataException(\"damaged crowd bed: \" + bed.file);", create);
        Assert.Contains("manifest.ValidateBed();", create);
        Assert.DoesNotContain("manifest.Validate();", create);                              // the calls' own check: a damaged call never takes the bed with it
        // one line per raid when it exists and one when it goes
        Assert.Contains("SpectatorCrowdBedModel.Describe(plan)", create);
        Assert.Contains("stand sources, clip and excitement removed", code);
    }

    [Fact]
    public void PluginInstallsTheKeysOnceBeforeTheCrowdAndTheCrowdCreatesTheBedAfterItsVoices()
    {
        var plugin = Code("ArenaMapPort.Client/Plugin.cs");
        Assert.Single(Regex.Matches(plugin, @"SpectatorCrowdBed\.Install\(\);"));
        Assert.Matches(@"NoiseProbe\.Install\(\);\s*SpectatorCrowdBed\.Install\(\);\s*ArenaSpectators\.Install\(\);", plugin);
        var crowd = Code("ArenaMapPort.Client/ArenaSpectators.cs");
        Assert.Single(Regex.Matches(crowd, @"SpectatorCrowdBed\.Create\(crowd, voicePositions\);"));
        var create = Between(crowd, "internal static void Create()", "static Loaded LoadPackage(");
        int voices = create.IndexOf("SpectatorAudio.Create(crowd, voicePositions);", StringComparison.Ordinal);
        int bed = create.IndexOf("SpectatorCrowdBed.Create(crowd, voicePositions);", StringComparison.Ordinal);
        int ticks = create.IndexOf("timing.AudioTicks", StringComparison.Ordinal);
        Assert.True(voices > 0 && voices < bed && bed < ticks);
        int tryAt = create.IndexOf("try", StringComparison.Ordinal);
        Assert.True(bed > tryAt, "the bed is created inside Create's try block");
    }

    [Fact]
    public void TheModelAndTheContractAreUnityFreeAndTheTestsLinkThemButNotTheGlue()
    {
        foreach (var file in new[] { "ArenaMapPort.Client/SpectatorCrowdBedModel.cs", "ArenaMapPort.Client/SpectatorCrowd.cs" })
        {
            var text = Text(file);
            Assert.StartsWith("#nullable disable", text);
            Assert.DoesNotContain("UnityEngine", text);
            Assert.DoesNotContain("BepInEx", text);
            Assert.DoesNotContain("Plugin.", text);
            Assert.DoesNotMatch(@"System\.Random|new Random\(|Guid\.NewGuid|DateTime\.|Environment\.TickCount", Code(file));
        }
        var glue = Text(Glue);
        Assert.Contains("using UnityEngine;", glue);
        var tests = Text("ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj");
        Assert.Contains("ArenaMapPort.Client\\SpectatorCrowdBedModel.cs", tests);
        Assert.Contains("ArenaMapPort.Client\\SpectatorCrowd.cs", tests);
        Assert.DoesNotContain("ArenaMapPort.Client\\SpectatorCrowdBed.cs", tests);
    }

    [Fact]
    public void TheCadenceConstantsAreTheLongerPausesOfTheUserDecision()
    {
        var model = Code("ArenaMapPort.Client/SpectatorAudioModel.cs");
        Assert.Contains("public const double InitialDelayMinSeconds = 3, InitialDelaySpanSeconds = 5;", model);
        Assert.Contains("public const double GapMinSeconds = 4.0, GapSpanSeconds = 4.0;", model);
        Assert.Contains("public const int MaxConcurrent = 2;", model);
        Assert.Contains("NextAt = now + InitialDelayMinSeconds + _random.NextDouble() * InitialDelaySpanSeconds;", model);
        Assert.Contains("NextAt = now + GapMinSeconds + _random.NextDouble() * GapSpanSeconds;", model);
        Assert.DoesNotContain("1.6", model);
    }
}
