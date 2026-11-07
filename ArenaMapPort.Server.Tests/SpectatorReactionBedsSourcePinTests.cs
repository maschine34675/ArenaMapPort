using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins for the roar layer (docs/progress/spectator-audio.md "Roar beds", user decision 2026-11-07). SpectatorReactionBeds.cs is Unity glue and cannot run here, so the rules it
/// must keep are read from the text: the model file knows no Unity type, the reaction calls the layer (and no longer lifts the murmur), the two keys are developer keys on Settings and
/// none is public, the layer is built after the murmur bed from the same positions in its own try, the worker loads the six clips without Unity and without a log, the tick is two field
/// reads when idle and allocates nothing, everything stops outside Started and on an audio reset, the teardown releases every source and clip, every NoiseProbe tap sits behind
/// NoiseProbe.Armed, and the log tag and levels are the spec's.
/// </summary>
public sealed class SpectatorReactionBedsSourcePinTests
{
    private const string Glue = "ArenaMapPort.Client/SpectatorReactionBeds.cs";
    private const string Model = "ArenaMapPort.Client/SpectatorReactionBedModel.cs";
    private const string Crowd = "ArenaMapPort.Client/ArenaSpectators.cs";
    private const string Audio = "ArenaMapPort.Client/SpectatorAudio.cs";

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

    private static int IndexOf(string text, string needle)
    {
        int at = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, "missing: " + needle);
        return at;
    }

    [Fact]
    public void TheModelFileKnowsNoUnityOrBepInExTypeAndNoSwell()
    {
        var code = Code(Model);
        Assert.DoesNotContain("UnityEngine", code);
        Assert.DoesNotContain("BepInEx", code);
        Assert.DoesNotContain("Plugin.Log", code);
        Assert.DoesNotContain("Swell", code);
    }

    [Fact]
    public void AReactionCallsTheRoarLayerAndNothingInTheClientKnowsTheSwellAnyMore()
    {
        var start = Between(Code(Audio), "void StartReaction(", "AudioClip ReactionAudioClip(");
        Assert.Contains("roars.Trigger(plan.Kind, plan.Strength, plan.DurationOf(reserved))", start);
        Assert.Contains("roar=", Between(Code(Audio), "void LogReaction(", "void LogReactionDrops()"));
        foreach (var file in new[] { Audio, "ArenaMapPort.Client/SpectatorCrowdBed.cs", "ArenaMapPort.Client/SpectatorCrowdBedModel.cs", "ArenaMapPort.Client/SpectatorCrowd.cs", Glue, Model })
        {
            var code = Code(file);
            Assert.DoesNotContain("RequestSwell", code);
            Assert.DoesNotContain("TakeSwell", code);
            Assert.DoesNotContain("AddSwell", code);
            Assert.DoesNotContain("ReactionSwell", code);
        }
    }

    [Fact]
    public void TheTwoKeysAreDeveloperKeysOnSettingsHonouredOnlyInDeveloperModeAndNoneIsPublic()
    {
        var code = Code(Glue);
        var install = Between(code, "internal static void Install()", "internal static void Create(");
        Assert.Contains("var s = Plugin.Instance.Settings;", install);
        Assert.DoesNotContain("Plugin.Instance.Config", code);
        Assert.Contains("s.Bind(\"Spectators\", \"ReactionBeds\", SpectatorReactionBedModel.ReactionBedsDefault,", install);
        Assert.Contains("s.Bind(\"Spectators\", \"ReactionBedVolume\", SpectatorReactionBedModel.ReactionBedVolumeDefault, new ConfigDescription(", install);
        Assert.Contains("s.Bind(\"Spectators\", \"ReactionBedDefaultsRevision\", 0, new ConfigDescription(", code);
        Assert.Contains("System.ComponentModel.BrowsableAttribute.No", code);
        Assert.Equal(3, Regex.Matches(code, @"\bs\.Bind\(").Count);
        Assert.Contains("new AcceptableValueRange<float>(0f, SpectatorCrowdBedModel.MaxVolume)", install);
        Assert.Contains("static bool BedsOn => !DeveloperOptions.Enabled || _enabled == null || _enabled.Value;", code);
        Assert.Contains("DeveloperOptions.Enabled && _volume != null ? SpectatorCrowdBedModel.ClampVolume(_volume.Value) : SpectatorReactionBedModel.ReactionBedVolumeDefault;", code);
        Assert.Contains("if (_installed) return;", install);
        foreach (var file in new[] { "ArenaMapPort.Client/CompactConfiguration.cs", "ArenaMapPort.Client/MapConfigVisibility.cs" })
            Assert.DoesNotContain("ReactionBed", Text(file));
        // not in the murmur bed's file, whose Bind count is pinned at three
        Assert.Equal(3, Regex.Matches(Code("ArenaMapPort.Client/SpectatorCrowdBed.cs"), @"\bs\.Bind\(").Count);
        Assert.DoesNotMatch(@"_(enabled|volume)\.SettingChanged", code);
        // wired once, before the crowd that creates the layer
        var plugin = Code("ArenaMapPort.Client/Plugin.cs");
        Assert.Equal(1, Regex.Matches(plugin, @"SpectatorReactionBeds\.Install\(\);").Count);
        Assert.True(IndexOf(plugin, "SpectatorReactionBeds.Install();") < IndexOf(plugin, "ArenaSpectators.Install();"));
    }

    [Fact]
    public void TheCrowdBuildsTheLayerAfterTheMurmurBedFromTheSameStandPlanInItsOwnTryAndRemovesItWithTheCrowdRoot()
    {
        var crowd = Code(Crowd);
        int bed = IndexOf(crowd, "SpectatorCrowdBed.Create(crowd, voicePositions);");
        int roar = IndexOf(crowd, "SpectatorReactionBeds.Create(crowd, voicePositions, loaded.Roars);");
        Assert.True(bed < roar);
        var code = Code(Glue);
        var create = Between(code, "internal static void Create(", "internal bool Trigger(");
        Assert.Contains("try", create);
        Assert.Contains("catch (Exception e)", create);
        Assert.Contains("SpectatorCrowdBedModel.Plan(points, SpectatorCrowdBed.ReachNow)", create);          // the same stands as the murmur
        Assert.Contains("root.transform.SetParent(crowd.transform, false);", create);
        Assert.Contains("if (root != null) Destroy(root);", create);
        Assert.DoesNotContain("SpectatorCrowdBed.Current", code);                                                // independent of the murmur bed being on
        Assert.DoesNotContain("SpectatorCrowd.State", code);
        // the murmur's placement helpers are shared, not copied
        var bedCode = Code("ArenaMapPort.Client/SpectatorCrowdBed.cs");
        Assert.Contains("internal static void Place(AudioSource source, BedStand stand)", bedCode);
        Assert.Contains("internal static AudioMixerGroup EnvironmentGroup()", bedCode);
        Assert.Contains("internal static float ReachNow", bedCode);
        Assert.Contains("SpectatorCrowdBed.Place(source, plan);", code);
        Assert.DoesNotContain("SetCustomCurve", code);
        // a damaged set is one Warning and a manifest without roar beds is one Debug line
        Assert.Contains("package.Absent", create);
        Assert.Contains("if (package.Error != null)", create);
        Assert.Contains("the calls, the reactions and the murmur remain", create);
    }

    [Fact]
    public void TheWorkerReadsHashesAndDecodesTheSixBedsWithoutUnityOrALogAndTheirFailureNeverTakesTheCrowdWithIt()
    {
        var code = Code(Crowd);
        var loader = Between(code, "static RoarPackage LoadRoarPackage(", "static string[] TextureOrder(");
        foreach (var token in new[] { "UnityEngine", "GameObject", "Mathf", "Time.", "Object.", "Plugin.Log", "Debug.", "Singleton<", "AudioClip", "Vector3" })
            Assert.DoesNotContain(token, loader);
        Assert.Contains("CryptoProviders.Select(null)", loader);
        Assert.Contains("manifest.ValidateReactionBeds();", loader);
        Assert.Contains("if (manifest.reaction_beds == null) return new RoarPackage { Absent = true };", loader);
        Assert.Contains("return new RoarPackage { Error = e };", loader);
        Assert.DoesNotContain("throw;", loader);
        Assert.Contains("ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;", loader);
        // the worker runs it after the reaction clips, outside the lock, and the main thread only when the worker did not
        var work = Between(code, "Loaded Work()", "public Loaded Join(");
        Assert.Contains("loaded.Roars = LoadRoarPackage(PluginDirectory(), out loaded.RoarTicks);", work);
        Assert.True(IndexOf(work, "LoadReactionPackage(") < IndexOf(work, "LoadRoarPackage("));
        Assert.Contains("if (loaded.Roars == null) loaded.Roars = LoadRoarPackage(pluginDir, out loaded.RoarTicks);", code);
        foreach (var token in new[] { "UnityEngine", "GameObject", "Plugin.Log" })
            Assert.DoesNotContain(token, Between(code, "internal sealed class RoarPackage", "sealed class LoadTiming"));
    }

    [Fact]
    public void TheTickIsTwoFieldReadsWhenIdleAllocatesNothingAndWritesAVolumeOnlyOnChange()
    {
        var code = Code(Glue);
        var frame = Between(code, "void Update()", "void Fail(");
        Assert.DoesNotMatch(@"\bnew\b|\$""|Linq|\.ToArray\(|\.ToList\(|string\.Format|ToString\(", frame);
        Assert.DoesNotMatch(@"FindObjectsOfType|FindObjectOfType|FindObjectsByType|Resources\.FindObjects|GameObject\.Find|GetComponentsInChildren|GetComponents?<|Raycast", code);
        // idle: the reset flag and the sounding count are the only reads before the started check
        Assert.True(IndexOf(frame, "if (_sounding == 0) return;") < IndexOf(frame, "Singleton<AbstractGame>.Instantiated"));
        Assert.True(IndexOf(frame, "if (!_ready) return;") < IndexOf(frame, "if (_reset)"));
        // the volume is written when the envelope's gain changed or the volume key moved, on unscaled time
        Assert.Contains("Time.unscaledTimeAsDouble", frame);
        Assert.Contains("if ((changed || keyChanged) && lane.Source != null) lane.Source.volume = SpectatorReactionBedModel.SourceVolume(key, gain);", frame);
        Assert.Contains("if (!lane.Envelope.Active) { StopLane(stand, i, lane); continue; }", frame);
        // outside Started, or with the key off, everything stops at once (no release)
        Assert.Contains("bool started = Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started;", frame);
        Assert.Contains("if (!started || !BedsOn || !SpectatorAudio.ReactionsOn)", frame);
        Assert.Contains("StopAll();", Between(frame, "if (!started || !BedsOn || !SpectatorAudio.ReactionsOn)", "double now"));
    }

    [Fact]
    public void ARoarUsesOwnedLoopingSourcesCreatedAtTheFirstTriggerAndTheSameClipOnEveryStand()
    {
        var code = Code(Glue);
        Assert.DoesNotContain("BetterAudio", code.Replace("SpectatorCrowdBed.EnvironmentGroup()", ""));     // no pooled source: the layer owns its AudioSources
        var trigger = Between(code, "internal bool Trigger(", "bool AnyActive(");
        Assert.Contains("if (!_ready || _failed || !BedsOn) return false;", trigger);
        Assert.Contains("SpectatorReactionBedKind.For(reactionKind)", trigger);
        Assert.Contains("SpectatorReactionBedModel.Amplitude(strength)", trigger);
        Assert.Contains("SpectatorReactionBedModel.HoldSeconds(reactionDurationSeconds)", trigger);
        // one clip per trigger, picked before the stands loop (a retrigger of a sounding roar keeps its clip)
        Assert.True(IndexOf(trigger, "_picker.Pick(bed)") < IndexOf(trigger, "for (int i = 0; i < _stands.Length; i++)"));
        Assert.Contains("AnyActive(bed) && _current[bed] >= 0 ? _current[bed] : _picker.Pick(bed)", trigger);
        // the other kind is cut, then this kind is (re)triggered
        Assert.True(IndexOf(trigger, "cut.Envelope.Cut(now);") < IndexOf(trigger, "lane.Envelope.Trigger(amplitude, hold, now);"));
        Assert.Contains("lane.Source.volume = SpectatorReactionBedModel.SourceVolume(key, lane.Envelope.Gain);", trigger);
        var start = Between(code, "void StartLane(", "void StopLane(");
        Assert.Contains("source.loop = true;", start);
        Assert.Contains("source.spatialBlend = 1f;", start);
        Assert.Contains("source.dopplerLevel = 0f;", start);
        Assert.Contains("source.timeSamples =", start);
        Assert.Contains("_picker.StartOffset(frames)", start);                                                    // a random start frame per stand
        Assert.Contains("source.Play();", start);
        Assert.Contains("if (stand.Go == null)", start);                                                          // created lazily
        Assert.Contains("if (source == null)", start);
        Assert.Contains("SpectatorCrowdBedModel.ToClipFloats(_pcm[bed][index])", code);                          // the bed's baked gain, so the volume law of the model holds
        Assert.DoesNotContain("occlusion", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProcessSourceOcclusion", code);
    }

    [Fact]
    public void AnAudioResetStopsTheRoarsAndDropsTheClipsAndTheTeardownReleasesEverySourceAndClip()
    {
        var code = Code(Glue);
        Assert.Contains("AudioSettings.OnAudioConfigurationChanged += component.AudioChanged;", code);
        var release = Between(code, "void Release(bool destroy)", "void OnDestroy()");
        Assert.Contains("StopAll();", release);
        Assert.Contains("Destroy(stand.Go);", release);
        Assert.Contains("Destroy(_clips[k][j]);", release);
        var reset = Between(code, "void Update()", "if (_sounding == 0) return;");
        Assert.Contains("Release(true);", reset);
        var destroy = code.Substring(IndexOf(code, "void OnDestroy()"));
        Assert.Contains("AudioSettings.OnAudioConfigurationChanged -= AudioChanged;", destroy);
        Assert.Contains("Release(true);", destroy);
        Assert.Contains("_pcm = null;", destroy);
        Assert.Contains("if (Current == this) Current = null;", destroy);
        // StopAll leaves nothing sounding and forgets the clip of each kind
        var stopAll = Between(code, "void StopAll()", "void AudioChanged(");
        Assert.Contains("_sounding = 0;", stopAll);
        Assert.Contains("_current[k] = -1;", stopAll);
    }

    [Fact]
    public void ARoarStopsWithTheReactionsKeyAHandledResetPrecedesATriggerAndTheStartTapLogsTheSourceVolume()
    {
        var code = Code(Glue);
        // F1: Spectators/Reactions off stops a sounding roar like the key of the roar layer itself
        var tick = Between(code, "void Update()", "double now = Time.unscaledTimeAsDouble;");
        Assert.Contains("!BedsOn || !SpectatorAudio.ReactionsOn", tick);
        Assert.Contains("StopAll();", tick);
        Assert.Contains("internal static bool ReactionsOn", Code("ArenaMapPort.Client/SpectatorAudio.cs"));
        // F2: a pending audio reset is consumed in Trigger before any lane starts
        var trigger = Between(code, "internal bool Trigger(", "void StartLane(");
        int reset = trigger.IndexOf("if (_reset)", StringComparison.Ordinal);
        Assert.True(reset >= 0, "Trigger must handle a pending reset");
        Assert.Contains("Release(true);", trigger.Substring(reset));
        Assert.True(reset < trigger.IndexOf("StartLane(", StringComparison.Ordinal));
        // F2 (compat): the roar+ tap reports the source volume, not the volume key
        var start = Between(code, "void StartLane(", "void StopLane(");
        Assert.Contains("lane.Label, 1, index, source.volume,", start);
        Assert.DoesNotContain("index, key,", start);
    }

    [Fact]
    public void EveryNoiseProbeTapSitsBehindArmedAndTheCensusCountsTheRoarSources()
    {
        var code = Code(Glue);
        var notes = code.Split('\n').Where(l => Regex.IsMatch(l, @"NoiseProbe\.Note\w*\(")).ToArray();
        Assert.Equal(2, notes.Length);                                                                                   // the start and the stop of a lane
        foreach (var line in notes) Assert.Contains("NoiseProbe.Armed", line);
        Assert.Contains("NoiseEv.Roar", string.Join("\n", notes));
        var census = Code("ArenaMapPort.Client/NoiseProbe.cs");
        Assert.Contains("\" ownedRoar=\"", census);
        Assert.Contains("roars.PlayingSources", census);
        Assert.Contains("roars.ActiveSources", census);
    }

    [Fact]
    public void TheLogTagAndLevelsAreTheSpecs()
    {
        var code = Code(Glue);
        Assert.Contains("const string Tag = \"[ReactionBeds]\";", code);
        Assert.DoesNotContain("LogInfo", code);
        Assert.DoesNotContain("LogError", code);
        Assert.Equal(Regex.Matches(code, @"LogDebug\(").Count, Regex.Matches(code, @"LogDebug\(\$""\{Tag\}").Count);       // every line carries the tag
        Assert.Equal(Regex.Matches(code, @"LogWarning\(").Count, Regex.Matches(code, @"LogWarning\(\$""\{Tag\}").Count);
        // a Warning at build (a damaged set, or any other failure), one at a failure while running, and only the first of those
        Assert.Equal(3, Regex.Matches(code, "LogWarning").Count);
        Assert.Contains("if (first) Plugin.Log.LogWarning", code);
        // the per-trigger line is a developer-mode Debug line
        var trigger = Between(code, "internal bool Trigger(", "bool AnyActive(");
        Assert.Contains("if (DeveloperOptions.Enabled)", trigger);
        Assert.Contains("roar {SpectatorReactionBedKind.Name(bed)} amp=", trigger);
        Assert.Contains("stands=", trigger);
    }
}
