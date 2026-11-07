using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins for the Unity glue of the crowd's reactions (docs/PLAN-CROWD-REACTIONS.md 4.4 and 4.6; the stage-1 model is tested on its own). SpectatorAudio.cs,
/// SpectatorReactionDemo.cs and ArenaSpectators.cs need Unity and EFT types and cannot run here, so the rules they must keep are read from the text: the reaction drain and gate sit
/// BEFORE the calls' living-player guard and never ask whether the player is alive, the calls' guard and StopAllVoices leave the reaction slots alone, the two sets fail
/// independently, the kinds are published only while the set is valid and the key is on, an idle frame costs a few field reads and allocates nothing, the playback path is the
/// calls', the listener is null-safe, the keys are developer keys on Settings and none is public, there is no ReactionAuto key in phase A, the reaction clips are read, hashed and decoded
/// by the crowd's worker, React and the queue use no Unity API and log nothing, and the crowd's removal resets the contract state whether or not the audio component exists.
/// </summary>
public sealed class SpectatorReactionSourcePinTests
{
    private const string Audio = "ArenaMapPort.Client/SpectatorAudio.cs";
    private const string Demo = "ArenaMapPort.Client/SpectatorReactionDemo.cs";
    private const string Crowd = "ArenaMapPort.Client/ArenaSpectators.cs";
    private const string Bed = "ArenaMapPort.Client/SpectatorCrowdBed.cs";

    private static string Text(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n");
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

    private const string NoSceneSearch = @"FindObjectsOfType|FindObjectOfType|FindObjectsByType|FindObjectByType|Resources\.FindObjects|GameObject\.Find|FindWithTag|GetComponentsInChildren|GetComponentInParent|GetComponents?<";
    private const string NoLinq = @"\.Select\(|\.Where\(|\.ToArray\(|\.ToList\(|\.First\(|\.FirstOrDefault\(|\.Any\(|\.OrderBy\(|string\.Join\(|string\.Format\(";

    // ---- placement: before the living-player guard ---------------------------------------------------------------------------------

    [Fact]
    public void TheReactionSectionRunsBeforeTheCallsLivingPlayerGuardAndNeverAsksWhetherThePlayerIsAlive()
    {
        var code = Code(Audio);
        var update = Between(code, "void Update()", "AudioClip Clip(int index)");
        int reactions = IndexOf(update, "if (_reactionsReady) UpdateReactions();");
        int player = IndexOf(update, "var player = GamePlayerOwner.MyPlayer;");
        int guard = IndexOf(update, "if (!CallsEnabled ||");
        int alive = IndexOf(update, "IsAlive");
        Assert.True(reactions < player && player < guard && guard < alive, "the drain and the gate come before the guard");
        Assert.True(IndexOf(update, "if (!_callsReady) return;") > reactions, "a damaged call set returns only after the reactions ran");
        // the reaction section itself: its own gate, never the player's life
        var section = Between(code, "void UpdateReactions()", "void TickReactionVoices(");
        Assert.DoesNotContain("IsAlive", section);
        Assert.DoesNotContain("HealthController", section);
        Assert.DoesNotContain("MyPlayer", section);
        Assert.DoesNotContain("CallsEnabled", section);
        Assert.Contains("PublishKinds(ReactionsOn);", section);                                   // the Reactions key
        Assert.Contains("Singleton<AbstractGame>.Instance.Status != GameStatus.Started", section);  // the raid
        Assert.Contains("!SpatialAudioSystem.Initialized || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated", section);   // the spatial audio services
        // the calls keep their guard unchanged
        Assert.Contains("player == null || player.HealthController == null || !player.HealthController.IsAlive", update);
        Assert.Contains("StopAllVoices(NoiseProbe.Armed ? ProbeReason() : NoiseStopReason.StopAll); _schedule.Stop(); NextCallAt = -1; return;", update);
    }

    [Fact]
    public void TheCallsGuardTheCallsStopAndTheScheduleLeaveTheReactionSlotsAlone()
    {
        var code = Code(Audio);
        var stopAll = Between(code, "void StopAllVoices(", "void Warn(");
        Assert.DoesNotContain("_rvoices", stopAll);
        Assert.DoesNotContain("Reaction", stopAll);
        var update = Between(code, "void Update()", "AudioClip Clip(int index)");
        var guard = Between(update, "if (!CallsEnabled ||", "float now = Time.realtimeSinceStartup;");
        Assert.DoesNotContain("Reaction", guard);
        Assert.DoesNotContain("_rvoices", guard);
        // _schedule.Stop(): the guard, the audio reset (null-safe) and OnDisable; none of them names a reaction slot on its line
        foreach (var line in code.Split('\n').Where(l => l.Contains("_schedule.Stop()") || l.Contains("_schedule?.Stop()")))
        {
            Assert.DoesNotContain("_rvoices", line);
            Assert.DoesNotContain("Reaction", line);
        }
        // the random calls hear the reactions only through the schedule's Hold, which postpones and never stops
        Assert.Single(Regex.Matches(code, @"_schedule\.Hold\("));
        // the slots are their own pool of three
        Assert.Contains("voice._rvoices = new Voice[CrowdReactionPlanner.MaxVoices];", code);
        Assert.Equal(3, CrowdReactionPlanner.MaxVoices);
    }

    [Fact]
    public void OutsideStartedTheReactionVoicesStopAndWhatWaitsIsDroppedAndAnAudioResetKeepsTheQueue()
    {
        var code = Code(Audio);
        var section = Between(code, "void UpdateReactions()", "void TickReactionVoices(");
        Assert.Contains("DropReactions(queue, CrowdReactionDropReason.NotStarted, NoiseStopReason.NotStarted);", section);
        var drop = Between(code, "void DropReactions(", "void TickReactionVoices(");
        Assert.Contains("StopReactionVoices(stop);", drop);
        Assert.Contains("queue.Drain(_requests", drop);
        Assert.Contains("_planner.Reset();", drop);
        // the spatial audio services going down stops the voices but keeps the requests (they age out on their own)
        Assert.Contains("StopReactionVoices(NoiseStopReason.NoServices);", section);
        // the audio reset: reaction voices and clips go, the queue, the planner and the kinds stay
        var update = Between(code, "void Update()", "AudioClip Clip(int index)");
        var reset = Between(update, "if (_reset)", "if (_reactionsReady) UpdateReactions();");
        Assert.Contains("StopReactionVoices(NoiseStopReason.Reset); ClearReactionClips();", reset);
        Assert.DoesNotContain("SetKinds", reset);
        Assert.DoesNotContain("Reactions.Reset", reset);
        Assert.DoesNotContain("_planner.Reset", reset);
        Assert.DoesNotContain("Drain(", reset);
    }

    // ---- two sets, the kinds, the keys --------------------------------------------------------------------------------------------

    [Fact]
    public void TheCallsAndTheReactionsAreValidatedIndependentlyAndEachFailsWithOneWarningOfItsOwn()
    {
        var code = Code(Audio);
        var create = Between(code, "internal static void Create(", "void AudioChanged(");
        Assert.Contains("manifest.Validate();", create);
        Assert.DoesNotContain("ValidateReactions", create);                                       // the reaction set is validated by the worker's package
        Assert.Contains("spectator calls are unavailable", create);
        Assert.Contains("spectator reactions are unavailable", create);
        Assert.Contains("if (manifest == null && planner == null) return;", create);              // Current exists when either works
        Assert.Contains("voice._callsReady = true;", create);
        Assert.Contains("voice._reactionsReady = true;", create);
        Assert.Contains("Current = voice;", create);
        Assert.True(IndexOf(create, "Current = voice;") > IndexOf(create, "voice._reactionsReady = true;"));
        // a missing reaction set is a Debug line, never a Warning
        var absent = Between(create, "else if (prepared.Absent)", "else\n");
        Assert.Contains("LogDebug", absent);
        Assert.DoesNotContain("LogWarning", absent);
        // the worker's package validates the reaction set and never the calls
        var loader = Between(Code(Crowd), "static ReactionPackage LoadReactionPackage(", "static string[] TextureOrder(");
        Assert.Contains("manifest.ValidateReactions();", loader);
        Assert.DoesNotContain("manifest.Validate();", loader);
        Assert.DoesNotContain("ValidateBed", loader);
        Assert.Contains("if (manifest.reactions == null) return new ReactionPackage { Absent = true };", loader);
        Assert.Contains("catch (Exception e)", loader);
        Assert.Contains("return new ReactionPackage { Error = e };", loader);
        // the calls' Update and helpers work without the call set (null-safe), the reactions without it too
        Assert.Contains("_schedule?.Stop()", code);
        Assert.Contains("_schedule?.Retry(", code);
    }

    [Fact]
    public void TheKindsArePublishedOnlyWhileTheSetIsValidAndTheKeyIsOnAndWithdrawnOnSwitchOffDisableAndDestroy()
    {
        var code = Code(Audio);
        var publish = Between(code, "void PublishKinds(bool on)", "void UpdateReactions()");
        Assert.Contains("if (!_reactionsReady || on == _kindsPublished) return;", publish);
        Assert.Contains("SpectatorCrowd.Reactions.SetKinds(on ? _planner.KindsMask : 0);", publish);
        Assert.Contains("StopReactionVoices(NoiseStopReason.Toggle); _planner.Reset();", publish);
        // published at creation (key on), live afterwards
        var create = Between(code, "internal static void Create(", "void AudioChanged(");
        Assert.Contains("voice.PublishKinds(ReactionsOn);", create);
        Assert.True(IndexOf(create, "voice.PublishKinds(ReactionsOn);") > IndexOf(create, "voice._reactionsReady = true;"));
        // withdrawn when the component is disabled or destroyed (the destroy resets the claim and the queue as well)
        var disable = Between(code, "void OnDisable()", "void OnDestroy()");
        Assert.Contains("SpectatorCrowd.Reactions.SetKinds(0);", disable);
        var destroy = code.Substring(IndexOf(code, "void OnDestroy()"));
        Assert.Contains("SpectatorCrowd.Reactions.Reset();", destroy);
        Assert.Contains("SpectatorReactionDemo.Stop(", destroy);
        // the kind mask is the planner's (the pool's implemented kinds), never a literal
        Assert.DoesNotMatch(@"SetKinds\(\s*\d", code.Replace("SetKinds(0)", "").Replace("SetKinds(on ? _planner.KindsMask : 0)", ""));
    }

    [Fact]
    public void TheCrowdsRemovalResetsTheReactionsWhetherOrNotTheAudioComponentExists()
    {
        var crowd = Code(Crowd);
        var destroy = Between(crowd, "void OnDestroy()", "void DisposeOwnedAssets()");
        Assert.Contains("SpectatorCrowd.Reactions.Reset();", destroy);
        Assert.Contains("SpectatorReactionDemo.Stop(", destroy);
        // not behind the audio component's existence, and not behind the bed's
        var line = destroy.Split('\n').Single(l => l.Contains("SpectatorCrowd.Reactions.Reset();"));
        Assert.DoesNotContain("if (", line);
        Assert.DoesNotContain("SpectatorAudio.Current", destroy);
        // the contract still reads: "a newer raid's live bed is left alone"
        Assert.Contains("if (SpectatorCrowdBed.Current == null) SpectatorCrowd.State.Reset();", destroy);
    }

    [Fact]
    public void TheFourReactionKeysAreDeveloperKeysOnSettingsHonouredOnlyInDeveloperModeAndNoneIsPublic()
    {
        var code = Code(Audio);
        var install = Between(code, "internal static void Install()", "sealed class Voice");
        Assert.Contains("var s = Plugin.Instance.Settings;", install);
        Assert.DoesNotContain("Plugin.Instance.Config", code);
        Assert.Contains("s.Bind(\"Spectators\", \"Reactions\", true,", install);
        Assert.Contains("s.Bind(\"Spectators\", \"ReactionVolume\", ReactionVolumeDefault, new ConfigDescription(", install);
        Assert.DoesNotContain("ReactionSwell", code);                                              // removed 2026-11-07 (user decision: the roar beds answer a reaction instead)
        Assert.Contains("s.Bind(\"Spectators\", \"ReactionGapScale\", 1f, new ConfigDescription(", install);
        Assert.Equal(3, Regex.Matches(install, @"\bs\.Bind\(").Count);
        Assert.Contains("const float ReactionVolumeDefault = .5f;", code);
        Assert.Contains("new AcceptableValueRange<float>(0f, CrowdReactionPlanner.MaxGapScale)", install);
        Assert.Equal(1, Regex.Matches(install, @"new AcceptableValueRange<float>\(0f, 1f\)").Count);     // the volume
        Assert.Contains("SpectatorReactionDemo.Install();", install);
        // honoured only with DeveloperMode, like Audio diagnostics/SpectatorCalls: the defaults apply to a player
        Assert.Contains("static bool ReactionsOn => !DeveloperOptions.Enabled || _reactionsEnabled == null || _reactionsEnabled.Value;", code);
        Assert.Contains("DeveloperOptions.Enabled && _reactionVolume != null ? Unit(_reactionVolume.Value, ReactionVolumeDefault) : ReactionVolumeDefault;", code);
        Assert.Contains("DeveloperOptions.Enabled && _reactionGapScale != null ? _reactionGapScale.Value : 1f;", code);
        // none of them is public, and none lives in the bed's file (whose Bind count is pinned at three)
        foreach (var file in new[] { "ArenaMapPort.Client/CompactConfiguration.cs", "ArenaMapPort.Client/MapConfigVisibility.cs" })
        {
            var text = Text(file);
            foreach (var key in new[] { "\"Reactions\"", "ReactionVolume", "ReactionSwell", "ReactionGapScale", "ReactionDemo", "ReactionBed" })
                Assert.DoesNotContain(key, text);
        }
        var bed = Code(Bed);
        Assert.DoesNotContain("Reaction", bed);
        Assert.Equal(3, Regex.Matches(bed, @"\bs\.Bind\(").Count);
        // the values are polled in the frame, no setting handler runs on whichever thread sets a value
        Assert.DoesNotMatch(@"_reaction\w*\.SettingChanged", code);
    }

    [Fact]
    public void ThereIsNoReactionAutoKeyInPhaseAAndNoSensorFile()
    {
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(PathOf(Audio))!, "*.cs"))
        {
            var code = Regex.Replace(File.ReadAllText(path), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
            Assert.False(code.Contains("ReactionAuto", StringComparison.Ordinal), Path.GetFileName(path) + " must not bind ReactionAuto (plain-raid sensors are phase C)");
        }
        Assert.False(File.Exists(PathOf("ArenaMapPort.Client/SpectatorReactionSensors.cs")));
    }

    private static string PathOf(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
            if (Directory.Exists(Path.Combine(dir.FullName, "ArenaMapPort.Client"))) return Path.Combine(dir.FullName, relative);
        }
        throw new FileNotFoundException(relative);
    }

    // ---- the demo ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDemoBindsItsTwoKeysThroughSettingsClaimsThroughThePublicContractAndYieldsToAPlugin()
    {
        var code = Code(Demo);
        Assert.Contains("var s = Plugin.Instance.Settings;", code);
        Assert.DoesNotContain("Plugin.Instance.Config", code);
        Assert.Contains("s.Bind(\"Spectators\", \"ReactionDemo\", ReactionDemoMode.Off,", code);
        Assert.Contains("s.Bind(\"Spectators\", \"ReactionDemoKind\", 0, new ConfigDescription(", code);
        Assert.Equal(2, Regex.Matches(code, @"\bs\.Bind\(").Count);
        Assert.Contains("new AcceptableValueRange<int>(0, CrowdReactionKind.Finish)", code);
        // developer-only: Off outside DeveloperMode
        Assert.Contains("DeveloperOptions.Enabled && _mode != null ? _mode.Value : ReactionDemoMode.Off;", code);
        // the claim and the release go through the public members; only the Directed TakeCover needs the internal overload
        Assert.Contains("SpectatorCrowd.ClaimReactions(CrowdReactionQueue.DemoOwner)", code);
        Assert.Contains("SpectatorCrowd.ReleaseReactions(_claim)", code);
        Assert.Contains("SpectatorCrowd.React(_claim, step.Kind, step.Strength)", code);
        Assert.Contains("queue.React(_claim, step.Kind, step.Strength, true, Stopwatch.GetTimestamp())", code);
        Assert.Contains("if (queue.ExternalClaimed) return;", code);                                // waits for a plugin
        Assert.Contains("if (_claim != 0 || Plan.Running) Stop(\"off\");", code);                   // releases on Off
        Assert.Matches(@"DemoOwner\s*=\s*""developer-demo""", Code("ArenaMapPort.Client/CrowdReactionModel.cs"));
        // ticked by the reaction section only (developer mode, reactions running); no Plugin.LateUpdate hook
        var audio = Code(Audio);
        Assert.Contains("SpectatorReactionDemo.Tick(now);", audio);
        Assert.Contains("SpectatorReactionDemo.Install();", audio);
        Assert.DoesNotContain("SpectatorReactionDemo", Text("ArenaMapPort.Client/Plugin.cs"));
        // the Unity-free half has no Unity, BepInEx or plugin reference
        var plan = Text("ArenaMapPort.Client/SpectatorReactionDemoPlan.cs");
        Assert.StartsWith("#nullable disable", plan);
        foreach (var token in new[] { "UnityEngine", "BepInEx", "Plugin.", "ConfigEntry" }) Assert.DoesNotContain(token, plan);
        Assert.Contains("using UnityEngine;", Text(Demo));
        var tests = Text("ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj");
        Assert.Contains("ArenaMapPort.Client\\SpectatorReactionDemoPlan.cs", tests);
        Assert.DoesNotContain("ArenaMapPort.Client\\SpectatorReactionDemo.cs", tests);
    }

    // ---- React and the queue: no Unity, no log --------------------------------------------------------------------------------------

    [Fact]
    public void ReactTheQueueTheContractAndTheModelUseNoUnityApiAndLogNothing()
    {
        foreach (var file in new[] { "ArenaMapPort.Client/CrowdReactionModel.cs", "ArenaMapPort.Client/SpectatorCrowd.cs", "ArenaMapPort.Client/SpectatorAudioModel.cs",
                     "ArenaMapPort.Client/SpectatorCrowdBedModel.cs" })
        {
            var text = Text(file);
            Assert.StartsWith("#nullable disable", text);
            foreach (var token in new[] { "UnityEngine", "BepInEx", "Plugin.", "Singleton<", "MonoBehaviour" }) Assert.DoesNotContain(token, text);
            Assert.DoesNotMatch(@"LogDebug|LogWarning|LogInfo|LogError|Console\.|Debug\.", Code(file));
        }
        var model = Code("ArenaMapPort.Client/CrowdReactionModel.cs");
        var react = Between(model, "internal bool React(int handle, int kind, float strength01, bool directed, long stamp)", "internal int Drain(");
        Assert.DoesNotMatch(@"\bnew\s+(?!CrowdReactionRequest\b)[A-Za-z_]|\$""|Linq|\.ToString\(|string\.", react);   // the ring slot is a struct: nothing on the heap
        Assert.Contains("catch", react);                                                          // never throws
        // the public members are one try/catch each around the internal queue
        var contract = Code("ArenaMapPort.Client/SpectatorCrowd.cs");
        foreach (var call in new[] { "try { return Reactions.Kinds; } catch { return 0; }", "try { return Reactions.Claim(owner); } catch { return 0; }",
                     "try { return Reactions.React(claim, kind, strength01); } catch { return false; }", "try { return Reactions.Release(claim); } catch { return false; }" })
            Assert.Contains(call, contract);
        // the worker's reaction loader and the queue are not the places that read the clock of Unity
        Assert.DoesNotContain("Time.", model);
    }

    // ---- the frame: a few field reads, no allocation, no scene search -----------------------------------------------------------------

    [Fact]
    public void AnIdleFrameOfTheReactionSectionIsAFewFieldReadsAndNothingSearchesTheSceneOrAllocatesPerFrame()
    {
        var code = Code(Audio);
        var section = Between(code, "void UpdateReactions()", "void DropReactions(");
        // the idle exit comes before the clock is read, the queue is locked or anything else is touched
        int idle = IndexOf(section, "if (!busy && !DeveloperOptions.Enabled) return;");
        Assert.True(idle < IndexOf(section, "Time.realtimeSinceStartup"));
        Assert.True(idle < IndexOf(section, "queue.Drain("));
        Assert.True(idle < IndexOf(section, "Stopwatch.GetTimestamp()"));
        Assert.Contains("bool busy = _reactionActive > 0 || _planner.Pending > 0 || queue.HasWaiting;", section);
        // what the idle path reads: statics, fields and one volatile read
        var idlePart = section.Substring(0, idle);
        Assert.DoesNotMatch(@"\bnew\b|\$""|Time\.|Camera|Find|GetComponent|" + NoLinq, idlePart);
        // the queue is drained only when it says something waits
        Assert.Contains("if (queue.HasWaiting)", section);
        // the whole reaction path of a frame: no scene search and no LINQ; allocation only when a reaction starts
        var frame = Between(code, "void UpdateReactions()", "static bool TryListener(");       // the section, the drop, the voices' tick, the planning loop, the busy sets
        Assert.DoesNotMatch(NoSceneSearch, frame);
        Assert.DoesNotMatch(NoLinq, frame);
        Assert.DoesNotContain("Camera.main", frame);                                              // the listener is resolved per planned reaction, in TryListener
        var tick = Between(code, "void TickReactionVoices(", "void PlanReactions(");
        Assert.DoesNotMatch(@"\bnew\b|\$""", tick.Replace("Plugin.Log.LogDebug($\"{Tag}", ""));
        var plan = Between(code, "void PlanReactions(", "void CollectBusy()");
        Assert.DoesNotMatch(@"\bnew\b", plan);
        var busy = Between(code, "void CollectBusy()", "static bool TryListener(");
        Assert.DoesNotMatch(@"\bnew\b|" + NoLinq, busy);
        // no scene search anywhere in the reaction code
        var all = Between(code, "void PublishKinds(bool on)", "void OnDisable()");
        Assert.DoesNotMatch(NoSceneSearch, all);
    }

    [Fact]
    public void TheListenerIsTheMainCameraThenTheLocalPlayersBodyAndNoneDropsTheReaction()
    {
        var code = Code(Audio);
        var listener = Between(code, "static bool TryListener(", "void StartReaction(");
        int camera = IndexOf(listener, "Camera.main");
        int body = IndexOf(listener, "GamePlayerOwner.MyPlayer");
        Assert.True(camera < body);
        Assert.Contains("player != null && player.Transform != null", listener);
        Assert.Contains("position = Vector3.zero;", listener);
        Assert.Contains("return false;", listener);
        var start = Between(code, "void StartReaction(", "AudioClip ReactionAudioClip(");
        Assert.Contains("if (!TryListener(out var listener)) { _planner.Discard(plan, CrowdReactionDropReason.NoListener); return; }", start);
        // the speaker distances of a reaction are measured from that listener, not from the local player
        Assert.Contains("_speakers.Choose(_packed, listener.x, listener.y, listener.z, Rolloff * .8f, plan.Voices, _rBusySpeakers, _chosen);", start);
        Assert.Contains("slot.Distance = Vector3.Distance(listener, _positions[slot.Speaker]);", start);
    }

    [Fact]
    public void AReactionVoicePlaysThroughTheCallsPathAndAFailedSourceIsSkippedAndCounted()
    {
        var code = Code(Audio);
        var play = Between(code, "bool StartReactionVoice(", "void FreeReactionSlot(");
        foreach (var call in new[]
                 {
                     "audio.GetSource(BetterAudio.AudioSourceGroupType.Environment)",
                     "slot.Source.SetMixerGroup(audio.SourceGroups[(int)BetterAudio.AudioSourceGroupType.Environment].MixerGroup);",
                     "slot.Source.SetPitch(1); slot.Source.Loop = false;",
                     "slot.Source.SetRolloff(Rolloff); slot.Source.SetSpatialBlend(1);",
                     "slot.Source.IncludeInOcclusionProcess(true); slot.Source.EnabledEQ(true);",
                     "slot.Source.Play(clip, null, 1, ReactionVolumeNow, false, false);",
                     "slot.EndsAt = AudioSettings.dspTime + clip.length;",
                     "slot.System = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;",
                     "slot.System.ProcessSourceOcclusion(slot.Emitter, slot.Source, EOcclusionTest.Combined);",
                 })
            Assert.Contains(call, play);
        Assert.Contains("if (slot.Source == null) { FreeReactionSlot(slot); return false; }", play);
        Assert.Contains("catch (Exception e) { StopReactionVoice(slot, NoiseStopReason.Slot); WarnReaction(e); return false; }", play);
        var tick = Between(code, "void TickReactionVoices(", "void PlanReactions(");
        Assert.Contains("ReactionSourceFailures++;", tick);
        // the end of a voice releases its propagation container and gives the source back to the pool
        var stop = Between(code, "void StopReactionVoice(", "void StopReactionVoices(");
        Assert.Contains("system.UnregisterContainer(container)", stop);
        Assert.Contains("source.Release();", stop);
        Assert.Contains("if (source != null && NoiseProbe.Armed) ProbeReactionStop(voice, reason);", stop);
        Assert.DoesNotContain("BeginFade", stop);                                                  // the calls' A/B fade is not the reactions'
        // each further voice starts after its own delay
        Assert.Contains("slot.StartAt = now + (float)plan.Delays[i];", Between(code, "void StartReaction(", "AudioClip ReactionAudioClip("));
        Assert.Contains("else if (voice.Pending && now >= voice.StartAt)", tick);
    }

    [Fact]
    public void AStartedReactionAsksTheRoarLayerAndHoldsTheRandomCallsThroughTheModelsGap()
    {
        var start = Between(Code(Audio), "void StartReaction(", "AudioClip ReactionAudioClip(");
        int started = IndexOf(start, "_planner.Started(plan, now, reserved);");       // the history and the voice count take the voices that were reserved, not the plan's
        int roar = IndexOf(start, "roars.Trigger(plan.Kind, plan.Strength, plan.DurationOf(reserved))");
        int hold = IndexOf(start, "_schedule.Hold(now, hold);");
        Assert.True(started < roar && started < hold, "the planner's cooldown starts when the first voice has started");
        Assert.Contains("var roars = SpectatorReactionBeds.Current;", start);                       // the roar layer is independent of the murmur bed (no SpectatorCrowdBed.Current condition)
        Assert.DoesNotContain("SpectatorCrowdBed", start);
        Assert.DoesNotContain("RequestSwell", start);
        Assert.Contains("double hold = plan.DurationOf(reserved) + plan.HoldGapSeconds;", start);       // the voices that really started, plus the model's gap table
        Assert.Contains("if (_callsReady) _schedule.Hold(now, hold);", start);
        // the first voice decides: without a source the reaction is given up and no cooldown starts
        Assert.Contains("if (!StartReactionVoice(_planSlots[0], audio, now))", start);
        Assert.Contains("_planner.Discard(plan, CrowdReactionDropReason.NoSource);", start);
    }

    [Fact]
    public void ReactionClipsAreCreatedLazilyAndFreedWithTheAudioResetAndTheComponent()
    {
        var code = Code(Audio);
        var create = Between(code, "internal static void Create(", "void AudioChanged(");
        Assert.DoesNotContain("AudioClip.Create(", create);
        Assert.Contains("voice._rclips = new AudioClip[rpcm.Length];", create);
        var lazy = Between(code, "AudioClip ReactionAudioClip(", "bool StartReactionVoice(");
        Assert.Contains("if (_rclips[index] != null) return _rclips[index];", lazy);
        Assert.Contains("AudioClip.Create(\"ArenaMapPort spectator reaction \" + c.name, c.frames, 1, c.sample_rate, false)", lazy);
        Assert.Contains("PcmWav.ToFloats(_rpcm[index])", lazy);
        Assert.Contains("void ClearReactionClips()", code);
        Assert.Single(Regex.Matches(code, @"AudioClip\.Create\(\""ArenaMapPort spectator reaction"));
    }

    // ---- the worker -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheReactionSetIsReadHashedAndDecodedByTheCrowdsWorkerAndATimeoutOrFailureLeavesTheCrowd()
    {
        var crowd = Code(Crowd);
        var loader = Between(crowd, "static ReactionPackage LoadReactionPackage(", "static string[] TextureOrder(");
        Assert.Contains("var crypto = CryptoProviders.Select(null);", loader);                  // CNG: managed SHA-256 is 70 MiB/s in EFT's Mono
        Assert.Contains("crypto.Sha256(bytes, 0, bytes.Length)", loader);
        Assert.DoesNotContain("SHA256", loader);
        Assert.Contains("throw new InvalidDataException(\"damaged spectator reaction clip: \" + c.file);", loader);
        Assert.Contains("bytes.Length != c.bytes ||", loader);
        Assert.Contains("!= c.sha256", loader);
        Assert.Contains("PcmWav.Read(bytes)", loader);
        Assert.Contains("using ArenaMapPort.Crypto;", crowd);
        // the worker runs it after the crowd package and the texture prefetch, outside the lock, and its failure is the reactions' own
        var work = Between(crowd, "Loaded Work()", "public Loaded Join(");
        int textures = IndexOf(work, "_textures.Start();");
        int reactions = IndexOf(work, "loaded.Reactions = LoadReactionPackage(PluginDirectory(), out loaded.ReactionTicks);");
        Assert.True(textures < reactions);
        // the main-thread path (the worker did not run) loads them synchronously, inside Create's try, and hands them to the audio component
        var create = Between(crowd, "internal static void Create()", "static Loaded LoadPackage(");
        int tryAt = IndexOf(create, "try");
        Assert.True(IndexOf(create, "if (loaded.Reactions == null) loaded.Reactions = LoadReactionPackage(pluginDir") > tryAt);
        int hand = IndexOf(create, "crowd.ReactionData = loaded.Reactions;");
        int audioCreate = IndexOf(create, "SpectatorAudio.Create(crowd, voicePositions);");
        Assert.True(hand < audioCreate && audioCreate < IndexOf(create, "crowd.ReactionData = null;"));
        // the audio component takes the package and clears the crowd's reference (the decoded PCM is held once)
        var code2 = Code(Audio);
        Assert.Contains("var prepared = crowd.ReactionData;\n            crowd.ReactionData = null;", code2);
    }

    // ---- the noise probe -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheProbeSeesReactionVoicesInItsEventsItsCensusItsVoiceListAndItsAudibleTest()
    {
        var audio = Code(Audio);
        Assert.Contains("if (NoiseProbe.Armed) NoiseProbe.Note(NoiseEv.ReactStart, NoiseText.ShortClip(_rset.clips[slot.Clip].name), slot.Number, slot.Kind, slot.Distance, slot.Source.OcclusionVolumeFactor);", audio);
        Assert.Contains("NoiseProbe.Note(NoiseEv.ReactStop, NoiseText.ShortClip(_rset.clips[voice.Clip].name), voice.Number, (int)reason, played, left);", audio);
        var voices = Between(audio, "internal string ProbeVoices()", "void RefreshVoices()");
        Assert.Contains("foreach (var v in _rvoices)", voices);
        Assert.Contains("\"react:\"", voices);
        var audible = Between(audio, "internal bool AnyAudible()", "void RefreshVoices()");
        Assert.Contains("foreach (var v in _rvoices)", audible);
        var probe = Code("ArenaMapPort.Client/NoiseProbe.cs");
        var census = Between(probe, "static string Census(double now)", "static List<string> BuildDump(");
        Assert.Contains("\" ownedReaction=\"", census);
        Assert.Contains("spectators.PlayingReactionVoices", census);
        Assert.Contains("spectators.ActiveReactionVoices", census);
        Assert.Contains("spectators.PlayingReactionVoices", Between(probe, "static string Where(", "static string RoomText()"));
    }

    [Fact]
    public void TheStagingCopiesTheFlatReactionWavsKeepsTheMissingManifestErrorAndPrunesWhatLeftTheExport()
    {
        var project = Text("ArenaMapPort.Client/ArenaMapPort.Client.csproj");
        var target = Between(project, "<Target Name=\"ArenaMapPortStageSpectatorAudio\"", "</Target>");
        // one flat glob: the 70 reaction WAVs ride with the calls and the bed, no per-clip list to keep in step with the extractor
        Assert.Contains("Include=\"$(ArenaMapPortSpectatorAudioDir)\\spectator-audio.json;$(ArenaMapPortSpectatorAudioDir)\\*.wav\"", target);
        Assert.Contains("DestinationFolder=\"$(ArenaMapPortDistDir)\\spectator-audio\"", target);
        Assert.Contains("<Error Condition=\"!Exists('$(ArenaMapPortSpectatorAudioDir)\\spectator-audio.json')\" Text=\"Missing spectator audio; run python tools/extract/extract_spectator_audio.py extract\" />", target);
        // a WAV of the staged folder that the source folder no longer has is deleted (a demoted reaction clip); nothing else is
        Assert.Contains("<ArenaMapPortSpectatorAudioStale Include=\"$(ArenaMapPortDistDir)\\spectator-audio\\*.wav\" />", target);
        Assert.Contains("<ArenaMapPortSpectatorAudioStale Remove=\"@(ArenaMapPortSpectatorAudioFile->'$(ArenaMapPortDistDir)\\spectator-audio\\%(Filename)%(Extension)')\" />", target);
        Assert.Contains("<Delete Files=\"@(ArenaMapPortSpectatorAudioStale)\" />", target);
        // the stale list is built after the copy, and the retired bed's own delete stays
        Assert.True(target.IndexOf("<Copy ", StringComparison.Ordinal) < target.IndexOf("ArenaMapPortSpectatorAudioStale", StringComparison.Ordinal));
        Assert.Contains("arena_fans_ambient_postmatch.wav", target);
    }

    [Fact]
    public void TheBedHasNoSwellAndKeepsItsPinnedRules()
    {
        var bed = Code(Bed);
        var frame = Between(bed, "void Update()", "catch (Exception e)");
        Assert.Contains("_excitement.Step(Time.unscaledDeltaTime);", frame);
        Assert.DoesNotContain("Swell", bed);
        Assert.DoesNotContain("RequestSwell", Text("ArenaMapPort.Client/SpectatorAudio.cs"));
        Assert.DoesNotMatch(@"\bnew\b|\$""|Linq|\.ToArray\(|\.ToList\(|string\.Format|ToString\(", frame);
        Assert.Equal(3, Regex.Matches(bed, @"\bs\.Bind\(").Count);
    }
}
