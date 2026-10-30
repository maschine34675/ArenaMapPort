#nullable disable
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    /// <summary>
    /// Source pins for the developer noise probe (docs/progress/spectator-audio.md "NoiseProbe diagnostic (2026-10-29)"): the Unity glue
    /// (NoiseProbe.cs) cannot run here, so the rules it must keep are read from the text: the keys are developer keys on Settings and none is
    /// public, Off costs one static bool per tap, nothing searches a scene except the census at a logged burst or mark, the audio-thread
    /// callback touches no Unity API and allocates nothing, the worker thread never logs, and every tap in the owned audio code sits behind
    /// NoiseProbe.Armed. The prop prefix, which is linked production source, is exercised with a stand-in NoiseProbe.
    /// </summary>
    [Collection("MapPort client lifecycle")]
    public sealed class NoiseProbeSourcePinTests : IDisposable
    {
        public NoiseProbeSourcePinTests() { SceneManager.Scenes.Clear(); Time.time = 100; NoiseProbe.Armed = false; NoiseProbe.PropsMuted = false; NoiseProbe.Props.Clear(); }
        public void Dispose() { SceneManager.Scenes.Clear(); NoiseProbe.Armed = false; NoiseProbe.PropsMuted = false; NoiseProbe.Props.Clear(); }

        static string Text(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, relative);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            throw new FileNotFoundException(relative);
        }

        static string Code(string relative) => Regex.Replace(Text(relative), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

        static string Between(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a >= 0, "missing: " + start);
            int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            Assert.True(b > a, "missing end marker: " + end);
            return text.Substring(a, b - a);
        }

        const string Glue = "ArenaMapPort.Client/NoiseProbe.cs";

        // ------------------------------------------------------------------------------------------------------------------------
        // The prefix of the physics props, with a stand-in NoiseProbe

        static SmallPhysicsObject Prop(Scene scene) => new() { gameObject = new GameObject("Bottle (4)") { scene = scene } };

        static SmallPhysicsObject LiveProp()
        {
            var map = ArenaMapRegistry.All[0];
            SceneManager.Scenes.Add(new Scene(map.ScriptsSceneName));
            return Prop(new Scene(map.AllSceneNames.First()));
        }

        [Fact]
        public void ThePropPrefixNotesPlayedAndSkippedImpactsOnlyWhileTheProbeIsArmed()
        {
            var prop = LiveProp();
            SoundBank bank = null;
            float timer = -1;
            prop.AfterSetup = () => bank = new SoundBank();
            Assert.True(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, false, ref bank, ref timer));
            Assert.Empty(NoiseProbe.Props);                                                  // off: nothing is noted

            NoiseProbe.Armed = true;
            bank = new SoundBank();
            Assert.True(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, false, ref bank, ref timer));
            Assert.Equal(("Bottle (4)", 1), Assert.Single(NoiseProbe.Props));                // the bank plays

            NoiseProbe.Props.Clear();
            bank = null;
            prop.AfterSetup = null;
            Assert.False(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, false, ref bank, ref timer));
            Assert.Equal(("Bottle (4)", 0), Assert.Single(NoiseProbe.Props));                // the bank is missing: skipped, the 0.5 s timer is kept
            Assert.Equal(100.5f, timer);
        }

        [Fact]
        public void TheMutePropsSwitchSkipsEveryBankButKeepsTheCollisionTimerAndIsNotedWhenArmed()
        {
            var prop = LiveProp();
            NoiseProbe.PropsMuted = true;
            SoundBank bank = new SoundBank();
            float timer = -1;
            prop.AfterSetup = () => throw new InvalidOperationException("a muted prop must not even set up its bank");
            Assert.False(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, true, ref bank, ref timer));
            Assert.Equal(0, prop.SetupCalls);
            Assert.Equal(100.5f, timer);
            Assert.Empty(NoiseProbe.Props);                                                  // muted but not armed: silent

            NoiseProbe.Armed = true;
            timer = -1;
            Assert.False(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, false, ref bank, ref timer));
            Assert.Equal(("Bottle (4)", 2), Assert.Single(NoiseProbe.Props));
            Assert.Equal(100.5f, timer);

            NoiseProbe.PropsMuted = false;
            NoiseProbe.Props.Clear();
            prop.AfterSetup = null;
            Assert.True(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, false, ref bank, ref timer));   // switched off again: the vanilla path plays
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Wiring

        [Fact]
        public void PluginInstallsTheProbeOnceRightAfterSpectatorAudio()
        {
            var plugin = Text("ArenaMapPort.Client/Plugin.cs");
            Assert.Matches(@"SpectatorAudio\.Install\(\);[^\n]*\n\s*NoiseProbe\.Install\(\);", plugin);
            Assert.Single(Regex.Matches(plugin, @"NoiseProbe\.Install\("));
            // Plugin's LateUpdate is pinned by the notice and monitor tests: the probe has its own driver component.
            Assert.DoesNotContain("NoiseProbe.Tick", plugin);
            var tests = Text("ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj");
            Assert.Contains("ArenaMapPort.Client\\NoiseProbeModel.cs", tests);
            Assert.DoesNotContain("ArenaMapPort.Client\\NoiseProbe.cs", tests);
        }

        [Fact]
        public void TheSixKeysAreDeveloperKeysOnSettingsHonouredOnlyInDeveloperModeAndNoneIsPublic()
        {
            var code = Code(Glue);
            Assert.Contains("var s = Plugin.Instance.Settings;", code);
            Assert.DoesNotContain("Plugin.Instance.Config", code);
            Assert.Contains("const string Section = \"Audio diagnostics\";", code);
            Assert.Contains("s.Bind(Section, \"NoiseProbe\", NoiseProbeMode.Off,", code);
            Assert.Contains("s.Bind(Section, \"NoiseProbeMarkKey\", KeyCode.Pause,", code);
            Assert.Contains("s.Bind(Section, \"NoiseProbeSelfTest\", false,", code);
            Assert.Contains("s.Bind(Section, \"NoiseProbeMuteAmbient\", \"\",", code);
            Assert.Contains("s.Bind(Section, \"NoiseProbeMuteProps\", false,", code);
            Assert.Contains("s.Bind(Section, \"NoiseProbeSpectatorFade\", false,", code);
            Assert.Equal(6, Regex.Matches(code, @"\bs\.Bind\(").Count);
            // the binds exist in player mode too (so the file is stable), but nothing is wired and no component is added without DeveloperMode
            int guard = code.IndexOf("if (!DeveloperOptions.Enabled) return;", StringComparison.Ordinal);
            Assert.True(guard > code.IndexOf("_fade = s.Bind(", StringComparison.Ordinal));
            Assert.True(guard < code.IndexOf("SettingChanged +=", StringComparison.Ordinal));
            Assert.True(guard < code.IndexOf("AddComponent<Driver>()", StringComparison.Ordinal));
            Assert.True(guard < code.IndexOf("OnAudioConfigurationChanged +=", StringComparison.Ordinal));
            // none of them is public: a public key would have to be listed in CompactConfiguration.IsPublic and MapConfigVisibility.IsPlayerOption
            Assert.DoesNotContain("NoiseProbe", Text("ArenaMapPort.Client/CompactConfiguration.cs"));
            Assert.DoesNotContain("NoiseProbe", Text("ArenaMapPort.Client/MapConfigVisibility.cs"));
            // the setting handlers run on whichever thread sets the value: they only set flags, the driver applies them on the game thread
            var handlers = Between(code, "_mode.SettingChanged +=", "PropsMuted = _muteProps.Value;");
            Assert.DoesNotContain("Disarm", handlers);
            Assert.DoesNotContain("UnityEngine.Object", handlers);
        }

        [Fact]
        public void TheMarkKeyIsPolledDirectlyBecauseKeyboardShortcutIsDeadWhileWasdIsHeld()
        {
            var code = Code(Glue);
            Assert.Contains("Input.GetKeyDown(_markKey.Value)", code);
            Assert.DoesNotContain("KeyboardShortcut", code);
            Assert.DoesNotContain(".IsDown()", code);
            Assert.Contains("ConfigEntry<KeyCode> _markKey", code);
        }

        [Fact]
        public void OffCostsOneStaticBoolPerTapAndTwoPerFrameAndNothingRunsOutsideARaid()
        {
            var code = Code(Glue);
            var note = Between(code, "internal static void Note(NoiseEv kind", "internal static void NoteAt(");
            Assert.Contains("if (!Armed) return;", note);
            var late = Between(code, "void LateUpdate()", "static bool RaidActive()");
            Assert.Contains("if (_modeOn || Armed) Tick();", late);
            // armed only by Arm, which Tick calls inside a raid; disarmed at the raid's end, at mode Off and on failure
            Assert.Single(Regex.Matches(code, @"\bArmed = true;"));
            Assert.Contains("if (!raid)", code);
            Assert.Contains("if (Armed) Disarm(\"raid ended\");", code);
            Assert.Contains("Disarm(\"mode off\")", code);
            // the tap component and the listener readback exist only on the armed paths
            Assert.Single(Regex.Matches(code, @"AddComponent<NoiseTap>\(\)"));
            Assert.Equal(2, Regex.Matches(code, @"AudioListener\.GetOutputData\(").Count);   // the channel probe at arm time and the read
            var read = Between(code, "static void Read(double now)", "static void HandleHits(");
            Assert.Contains("AudioListener.GetOutputData(_buf, c)", read);
            // GetOutputData reads the DEVICE output: a stereo driver under a 5.1 speaker mode refuses channels 2-5 (GPU-2, 2026-10-29), so the
            // read loops over the channels the probe proved readable, the probe stops at the first ArgumentException, and arming fails only
            // when channel 0 is refused
            Assert.Contains("for (int c = 0; c < _readChannels; c++)", read);
            var probe = Between(code, "static int ReadableChannels(int mixChannels)", "static void LogArmed()");
            Assert.Contains("catch (ArgumentException) { break; }", probe);
            Assert.Contains("_readChannels = ReadableChannels(_channels);", Between(code, "static void ReadAudioFormat()", "static int ReadableChannels("));
            Assert.Contains("if (_readChannels < 1) throw", Between(code, "static void Arm(double now)", "static void ReadAudioFormat()"));
            // the filter tap delivers the device format too (2 channels on that driver): a channel mismatch re-creates the ring with the tap's
            // count instead of waiting for a speaker mode that never comes, and an empty ring is reported as empty, not as overwritten
            var gap = Between(code, "static void TapGapTick(double now)", "static string RequestCapture(");
            Assert.Contains("_tapChannels = mismatch;", gap);
            Assert.Contains("AttachTap();", gap);
            Assert.DoesNotContain("if (_channels == mismatch) AttachTap();", gap);
            Assert.Contains("new NoisePcmRing(_tapChannels > 0 ? _tapChannels : _channels, _rate, RingSeconds)", Between(code, "static void AttachTap()", "static void DetachTap()"));
            Assert.Contains("the capture ring is empty (the filter tap has written nothing)", code);
            Assert.Contains("if (now - _lastRead >= ReadInterval) Read(now);", code);
            Assert.Contains("const double ReadInterval = 0.012", code);
        }

        [Fact]
        public void NoSceneSearchRunsOutsideTheCensusAndTheCensusOnlyAtABurstOrAMark()
        {
            var code = Code(Glue);
            Assert.DoesNotMatch(@"FindObjectsOfType|FindObjectOfType|FindObjectsByType|FindObjectByType|Resources\.FindObjects", code);
            Assert.Single(Regex.Matches(code, "GetComponentsInChildren"));
            var census = Between(code, "static string Census(double now)", "static List<string> BuildDump(");
            Assert.Contains("GetComponentsInChildren<AudioSource>(false, _sources)", census);
            Assert.Equal(3, Regex.Matches(code, @"Census\(").Count);                          // the definition, the burst line and the dump of a mark or an automatic capture
            Assert.Contains("Census(now)", Between(code, "static void OnHit(NoiseHit hit, double now)", "static string Where("));
            Assert.Contains("Census(now)", Between(code, "static List<string> BuildDump(", "static void OnMark(double now)"));
            Assert.Contains("BuildDump(now, spectators)", Between(code, "static void OnMark(double now)", "static void ShowLabel("));
            // the census is limited to one per 250 ms
            Assert.Contains("now - _lastCensus < 0.25", census);
            // the listener of the armed probe is resolved once per raid, not per event
            Assert.Single(Regex.Matches(code, @"_listener = Singleton<AudioListenerConsistencyManager>\.Instance\.transform;"));
        }

        [Fact]
        public void TheAudioThreadCallbackTouchesNoUnityApiAllocatesNothingAndSwitchesItselfOffOnAnyException()
        {
            var code = Code(Glue);
            var tap = Between(code, "void OnAudioFilterRead(float[] data, int channels)", "static void AttachTap()");
            foreach (var forbidden in new[] { "Time.", "Plugin.", "Debug", "new ", "lock", "Destroy", "GameObject", "GetComponent", "Application.", "AudioSettings", "ToString", "string" })
                Assert.DoesNotContain(forbidden, tap);
            Assert.Contains("ring.Write(data, data.Length);", tap);
            Assert.Contains("catch (Exception) { Dead = true; }", tap);
            Assert.Contains("Stopwatch.GetTimestamp()", tap);
        }

        [Fact]
        public void TheExportWorkerNeverLogsOrTouchesUnityAndEveryResultIsLoggedOnTheGameThread()
        {
            var code = Code(Glue);
            var worker = Between(code, "static void RunExport(object state)", "static void TrimOld(");
            foreach (var forbidden in new[] { "Plugin.", "Time.", "UnityEngine", "GameObject", "Application.", "AudioSettings" })
                Assert.DoesNotContain(forbidden, worker);
            Assert.Contains("results.Enqueue(", worker);
            Assert.Contains("ThreadPool.QueueUserWorkItem(RunExport, job);", code);
            var drain = code.Substring(code.IndexOf("static void DrainResults()", StringComparison.Ordinal));
            Assert.Contains("Plugin.Log.LogDebug", drain);
            // the documented limits
            Assert.Contains("CaptureSeconds = 12.0", code);
            Assert.Contains("CaptureAfterMark = 2.0", code);
            Assert.Contains("RingSeconds = 15.0", code);
            Assert.Contains("\"ArenaMapPort-noise\"", code);
            Assert.Contains("\"diagnostics\"", code);
        }

        [Fact]
        public void TheProbeLogsAtDebugWithOneTagAndTheOnlyWarningsAreFailuresToArmOrRun()
        {
            var code = Code(Glue);
            Assert.Contains("internal const string Tag = \"[NoiseProbe]\";", code);
            Assert.DoesNotContain("LogInfo", code);
            Assert.DoesNotContain("LogError", code);
            Assert.Equal(Regex.Matches(code, @"LogDebug\(").Count, Regex.Matches(code, @"LogDebug\(\$""\{Tag\}").Count);   // every line carries the tag
            // worker and audio thread never log
            Assert.DoesNotContain("Plugin.Log", Between(code, "void OnAudioFilterRead(float[] data, int channels)", "static void AttachTap()"));
            Assert.DoesNotContain("Plugin.Log", Between(code, "static void RunExport(object state)", "static void TrimOld("));
            // the Warning lines are the ones the brief allows: cannot arm, stopped, tap problems, the low disk space, a stopped self-test
            Assert.True(Regex.Matches(code, "LogWarning").Count <= 9);
        }

        [Fact]
        public void EveryTapInTheOwnedAudioCodeSitsBehindArmedOrInsideAProbeHelper()
        {
            // Lines that call NoiseProbe.Note* without the guard on the same line. Each one is the body of a helper that is only called while armed.
            var expected = new Dictionary<string, string[]>
            {
                ["ArenaMapPort.Client/SpectatorAudio.cs"] = new[] { "NoiseProbe.Note(NoiseEv.Life,", "NoiseProbe.Note(NoiseEv.SpecStop,", "NoiseProbe.Note(NoiseEv.SpecOnset," },
                ["ArenaMapPort.Client/Bay5AmbientAudio.cs"] = new[] { "NoiseProbe.Note(NoiseEv.AmbStop,", "NoiseProbe.Note(kind," },
                ["ArenaMapPort.Client/PhysicsPropSoundRepairs.cs"] = new string[0],
                ["ArenaMapPort.Client/GateSounds.cs"] = new string[0],
                ["ArenaMapPort.Client/PuddleFootstepRepairs.cs"] = new string[0],
                ["ArenaMapPort.Client/PrecipitationAudio.cs"] = new string[0],
                ["ArenaMapPort.Client/RoomReverb.cs"] = new string[0],
                ["ArenaMapPort.Client/RoomShotAcoustics.cs"] = new string[0],
                ["ArenaMapPort.Client/SpectatorCrowdBed.cs"] = new string[0],
            };
            foreach (var pair in expected)
            {
                var unguarded = Text(pair.Key).Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => Regex.IsMatch(line, @"NoiseProbe\.Note\w*\(") && !line.StartsWith("//") && !line.Contains("NoiseProbe.Armed"))
                    .Select(line => line.Substring(line.IndexOf("NoiseProbe.Note", StringComparison.Ordinal)))
                    .Select(line => line.Length > 40 ? line.Substring(0, line.IndexOf(',') + 1) : line)
                    .ToArray();
                Assert.Equal(pair.Value, unguarded);
            }
            // the helpers themselves are called under the guard
            var spectators = Code("ArenaMapPort.Client/SpectatorAudio.cs");
            Assert.Contains("if (NoiseProbe.Armed) ProbeLife(player);", spectators);
            Assert.Contains("if (source != null && NoiseProbe.Armed) ProbeStop(voice, reason);", spectators);
            Assert.Contains("if (voice.Frame >= 0 && NoiseProbe.Armed) ProbeOnset(voice, source);", spectators);
            // every tap of the tapped files appears
            Assert.Contains("NoiseEv.SpecStart", spectators);
            Assert.Contains("NoiseEv.Reset", spectators);
            Assert.Contains("NoiseEv.Rain,", Code("ArenaMapPort.Client/PrecipitationAudio.cs"));
            Assert.Contains("NoiseEv.RainPlay,", Code("ArenaMapPort.Client/PrecipitationAudio.cs"));
            Assert.Contains("NoiseEv.Gate,", Code("ArenaMapPort.Client/GateSounds.cs"));
            Assert.Contains("NoiseEv.Puddle,", Code("ArenaMapPort.Client/PuddleFootstepRepairs.cs"));
            Assert.Contains("NoiseProbe.NoteRoomReverb(category)", Code("ArenaMapPort.Client/RoomReverb.cs"));
            Assert.Contains("NoiseProbe.NoteRoomShot(room.ID,", Code("ArenaMapPort.Client/RoomShotAcoustics.cs"));
            Assert.Contains("NoiseEv.Bed,", Code("ArenaMapPort.Client/SpectatorCrowdBed.cs"));
            var ambient = Code("ArenaMapPort.Client/Bay5AmbientAudio.cs");
            Assert.Contains("NoiseEv.AmbStart", ambient);
            Assert.Contains("NoiseEv.AmbRestart", ambient);
            Assert.Contains("if (NoiseProbe.Armed || _probeMuteVersion != NoiseProbe.AmbientMuteVersion) ProbePoll();", ambient);
        }

        [Fact]
        public void TheAmbientPollIsOneLoopOverTheOwnedPlayersAndCannotTakeTheDriverDown()
        {
            var code = Code("ArenaMapPort.Client/Bay5AmbientAudio.cs");
            var poll = Between(code, "void ProbePoll()", "static bool LoopWrapped(");
            Assert.Contains("catch (Exception ex)", poll);
            Assert.Contains("_probeBroken = true;", poll);
            Assert.DoesNotMatch(@"FindObject|GetComponentsInChildren|GetComponent<", poll);
            Assert.Contains("target.mute = wanted", poll);                                    // the mute list is applied and reverted through AudioSource.mute only
            Assert.Contains("NoiseProbe.AmbientPlaying = playing;", poll);
            Assert.Contains("if (!NoiseProbe.Armed) return;", poll);                          // without the probe only the mute list is applied
        }

        [Fact]
        public void TheSpectatorFadeIsADeveloperSwitchThatReleasesTheSourceOnlyAfterTheFade()
        {
            var code = Code("ArenaMapPort.Client/SpectatorAudio.cs");
            Assert.Contains("static bool SpectatorFade => DeveloperOptions.Enabled && NoiseProbe.SpectatorFade;", code);
            Assert.Single(Regex.Matches(code, @"VolumeFadeIn\("));
            Assert.Contains("if (SpectatorFade) slot.Source.VolumeFadeIn(FadeInSeconds);", code);
            Assert.Single(Regex.Matches(code, @"VolumeFadeOut\("));
            Assert.Contains("source.VolumeFadeOut(FadeOutSeconds, source.OnStop)", code);
            Assert.Contains("const float FadeInSeconds = .15f, FadeOutSeconds = .08f;", code);
            Assert.Contains("if (!BeginFade(source, reason)) source.Release();", code);
            // a call that ended by itself, a reset, a failed setup and a disable are never faded
            var fade = Between(code, "bool BeginFade(BetterSource source", "void ReleaseFading(bool force)");
            foreach (var reason in new[] { "Ended", "Reset", "Slot", "Disable" })
                Assert.Contains("NoiseStopReason." + reason, fade);
            // the sources that are still fading go back to the pool when the fade is over, and at once when the crowd is switched off or destroyed
            Assert.Contains("if (_fading.Count > 0) ReleaseFading(false);", code);
            Assert.Equal(2, Regex.Matches(code, @"ReleaseFading\(true\)").Count);
            Assert.DoesNotContain("Stop(FadeOutSeconds)", code);
        }

        [Fact]
        public void TheSpectatorClipsAreHashedThroughTheCngProviderAndTheCheckStaysFailClosed()
        {
            var code = Code("ArenaMapPort.Client/SpectatorAudio.cs");
            Assert.Contains("var crypto = CryptoProviders.Select(null);", code);
            Assert.Contains("crypto.Sha256(bytes, 0, bytes.Length)", code);
            Assert.DoesNotContain("SHA256", code);
            Assert.DoesNotContain("System.Security.Cryptography", code);
            Assert.Contains("throw new InvalidDataException(\"damaged spectator clip: \" + c.file);", code);
            Assert.Contains("bytes.Length != c.bytes ||", code);
            Assert.Contains("!= c.sha256", code);
            // the failure still ends in the existing Warning and the visible crowd
            Assert.Contains("spectator calls are unavailable", code);
        }

        [Fact]
        public void ThePropMuteSwitchSitsAfterTheLateCallbackGuardAndBeforeTheBankIsTouched()
        {
            var code = Code("ArenaMapPort.Client/PhysicsPropSoundRepairs.cs");
            int late = code.IndexOf("if (!ArenaMapRaid.IsLoaded())", StringComparison.Ordinal);
            int mute = code.IndexOf("if (NoiseProbe.PropsMuted)", StringComparison.Ordinal);
            int setup = code.IndexOf("__instance.SetupSoundBank();", StringComparison.Ordinal);
            Assert.True(late > 0 && late < mute && mute < setup);
            Assert.Single(Regex.Matches(code, @"NoiseProbe\.PropsMuted"));
        }

        [Fact]
        public void TheModelAndTheGlueKeepTheirSideOfTheUnityBoundary()
        {
            var model = Text("ArenaMapPort.Client/NoiseProbeModel.cs");
            Assert.DoesNotContain("UnityEngine", model);
            Assert.DoesNotContain("BepInEx", model);
            Assert.DoesNotContain("Plugin.", model);
            Assert.StartsWith("#nullable disable", model);
            var glue = Text(Glue);
            Assert.Contains("using UnityEngine;", glue);
            // the probe never writes a game or configuration file: only its own export folder
            Assert.DoesNotContain("File.Delete(\"", glue);
            Assert.DoesNotMatch(@"SetFloat|ClearFloat|SetModel|AcousticModel", Code(Glue));
        }
    }
}

namespace ArenaMapPort.Client
{
    // Native boundary only: the few NoiseProbe members that the linked PhysicsPropSoundRepairs.cs reads. The probe itself (NoiseProbe.cs) needs Unity and EFT
    // types and is read as text by NoiseProbeSourcePinTests.
    internal static class NoiseProbe
    {
        internal static bool Armed, PropsMuted;
        internal static readonly List<(string Name, int Outcome)> Props = new();
        internal static void NoteProp(object prop, int outcome) => Props.Add((((SmallPhysicsObject)prop).name, outcome));
    }
}
