using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Developer-only audio noise probe (docs/progress/spectator-audio.md, "NoiseProbe diagnostic (2026-10-29)"). The user hears a short
    /// noise on Equator that no log, clip or code review explains; this turns "somewhere in the last minutes" into attributable evidence.
    ///
    /// Vanilla EFT and the rest of the plugin: nothing reads the mixed output during a raid, and nothing records when ambient clips, prop
    /// impacts, spectator voices or room changes start, so a heard burst has no timestamp to join against.
    /// The probe: (a) Detect reads the listener output (AudioListener.GetOutputData, every channel, one 1024-sample window at most every 12 ms,
    /// post-mixer, main thread only, no managed code on the audio thread) and classifies PEAK / CLICK / DROPOUT / GAP / CLIP bursts with
    /// the thresholds of <see cref="NoiseThresholds"/>; (b) a 1024-entry ring records what the owned audio code did (spectator start / stop with
    /// reason and an onset trace, ambient isPlaying edges and in-place restarts, prop impacts, room and mixer applies, gate sounds, puddle
    /// steps, rain beds, audio resets, deaths, hitches, GC) so every burst can be flagged ORPHAN (nothing of ours began in the previous
    /// 400 ms); (c) the mark key (Input.GetKeyDown, not KeyboardShortcut, which is dead while WASD is held) dumps the last ten seconds
    /// on demand; (d) Capture adds an OnAudioFilterRead tap on the AudioListener object that keeps the last 15 s as PCM16 and exports a
    /// 12 s WAV (10 s before + 2 s after a mark, or an orphan burst) from a worker thread. The self-test plays a 10 ms noise burst at -18 /
    /// -24 / -30 / -36 dBFS through an owned source and reports what the detector saw, which bounds what a negative result excludes.
    ///
    /// Off (the default, and always outside DeveloperMode) costs one static bool read per tap, no GetOutputData, no tap component and no
    /// per-frame work (the driver component is disabled). Armed only inside a raid; everything stops at raid end. No public key: the keys
    /// are honoured only with DeveloperMode, like SpectatorCalls. Nothing here changes what is played, except the explicit A/B switches
    /// NoiseProbeMuteAmbient, NoiseProbeMuteProps and NoiseProbeSpectatorFade, which are reversible and live. Never throws into the game:
    /// a failure logs one Warning, stops the probe for the session and leaves the audio as it is. Host proof: none yet (offline-tested model).
    /// </summary>
    internal static class NoiseProbe
    {
        internal const string Tag = "[NoiseProbe]";
        const string Section = "Audio diagnostics";
        const double ReadInterval = 0.012, CaptureSeconds = 12.0, CaptureAfterMark = 2.0, RingSeconds = 15.0, DumpSeconds = 10.0;

        // Read by the tap call sites: one static bool per call when the probe is off.
        internal static bool Armed;
        internal static bool PropsMuted;
        internal static bool SpectatorFade;
        /// <summary>Bumped whenever the ambient mute list changes; the ambient driver re-applies it when its own copy differs.</summary>
        internal static int AmbientMuteVersion;
        /// <summary>Owned ambient players that were playing at the last poll (census).</summary>
        internal static int AmbientPlaying;

        static ConfigEntry<NoiseProbeMode> _mode;
        static ConfigEntry<KeyCode> _markKey;
        static ConfigEntry<bool> _selfTestRequest, _muteProps, _fade;
        static ConfigEntry<string> _muteAmbient;
        static HashSet<string> _muted;
        static MarkLabel _label;

        static bool _inRaid, _failedRaid, _failedSession, _warned, _audioReset, _deviceChanged, _selfPending, _modeOn;
        static volatile bool _dirty;
        static NoiseProbeMode _active;
        static double _raidStart, _lastRead, _lastCensus, _labelUntil;
        static int _channels = 2, _readChannels = 2, _rate = 48000, _markNumber, _calibMinutes, _lastRoom = int.MinValue, _gc0, _gc1, _gc2;
        static string _lastCensusText = "";
        static string _lastShotSignature, _mapKey = "vanilla";
        static int _lastReverbCategory = int.MinValue;
        static Transform _listener;

        static NoiseDetector _detector;
        static NoiseEventRing _ring;
        static NoiseLabels _labels;
        static NoiseBlock _block;
        static NoiseGapWatch _gap;
        static NoiseCapturePolicy _policy;
        static float[] _buf;
        static List<AudioSource> _sources;
        static Dictionary<string, int> _groups;
        static List<string> _groupNames;
        static List<NoiseEvent> _window;

        // Self-test
        static NoiseSelfTest _test;
        static GameObject _testObject;
        static AudioSource _testSource;
        static List<AudioClip> _testClips;
        static long _testStartFrame;

        // Capture
        static NoisePcmRing _pcm;
        static NoiseTap _tap;
        static PendingCapture _pending;
        static int _exportBusy;
        static bool _captureDisabled;
        static ConcurrentQueue<string> _results;

        sealed class PendingCapture
        {
            public double DueAt;
            public int Number;
            public bool Automatic;
            public string Name;
            public StringBuilder Text;
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Configuration

        internal static void Install()
        {
            var s = Plugin.Instance.Settings;
            _mode = s.Bind(Section, "NoiseProbe", NoiseProbeMode.Off,
                "Developer hearing diagnostic for the unexplained short noise. Detect reads the mixed listener output in a raid (every channel, at most every 12 ms) and " +
                "logs PEAK / CLICK / DROPOUT / GAP / CLIP bursts at Debug with what the mod started just before ([NoiseProbe] BURST lines), plus a summary line per minute; " +
                "Capture also keeps the last 15 s of the mix as PCM and exports a 12 s WAV on a mark or an unexplained burst (BepInEx/logs/ArenaMapPort-noise). " +
                "Live; armed only inside a raid. Ignored outside DeveloperMode.");
            _markKey = s.Bind(Section, "NoiseProbeMarkKey", KeyCode.Pause,
                "Key that marks 'I just heard it': dumps the last ten seconds of audio events to the log (and the WAV in Capture). Polled directly, so it works while WASD is held. " +
                "Mouse3 / Mouse4 (side buttons) work too. Only with NoiseProbe Detect or Capture. Ignored outside DeveloperMode.");
            _selfTestRequest = s.Bind(Section, "NoiseProbeSelfTest", false,
                "Request: play four 10 ms noise bursts at -18, -24, -30 and -36 dBFS through an owned source and log what the detector saw. Starts a few seconds into a raid with " +
                "NoiseProbe Detect or Capture, then resets to false. Ignored outside DeveloperMode.");
            _muteAmbient = s.Bind(Section, "NoiseProbeMuteAmbient", "",
                "A/B switch: comma-separated exact names of the imported ambient players to mute (AudioSource.mute, reversible), for example electricity,cracles,cracles (1),cracles (2). " +
                "Live; works without NoiseProbe. Ignored outside DeveloperMode.");
            _muteProps = s.Bind(Section, "NoiseProbeMuteProps", false,
                "A/B switch: skip every physics-prop impact sound (the props still move and collide). Live. Ignored outside DeveloperMode.");
            _fade = s.Bind(Section, "NoiseProbeSpectatorFade", false,
                "A/B switch: fade each spectator call in over 150 ms and fade it out over 80 ms when it is stopped early (death, calls switched off) instead of cutting it. Live, for new calls. " +
                "Ignored outside DeveloperMode.");
            if (!DeveloperOptions.Enabled) return;

            // SettingChanged can arrive from whichever thread set the value: the handlers only set flags, the driver applies them on the game thread.
            _mode.SettingChanged += (_, __) => { _modeOn = _mode.Value != NoiseProbeMode.Off; _dirty = true; };
            _selfTestRequest.SettingChanged += (_, __) => _dirty = true;
            _muteAmbient.SettingChanged += (_, __) => ParseMuteList(true);
            _muteProps.SettingChanged += (_, __) => { PropsMuted = _muteProps.Value; LogSwitch("props muted=" + PropsMuted); };
            _fade.SettingChanged += (_, __) => { SpectatorFade = _fade.Value; LogSwitch("spectator fade=" + SpectatorFade); };
            PropsMuted = _muteProps.Value;
            SpectatorFade = _fade.Value;
            ParseMuteList(false);
            AudioSettings.OnAudioConfigurationChanged += changed => { _audioReset = true; _deviceChanged = changed; };
            _modeOn = _mode.Value != NoiseProbeMode.Off;
            _dirty = true;
            Plugin.Instance.gameObject.AddComponent<Driver>();
        }

        static void LogSwitch(string text)
        {
            Plugin.Log.LogDebug($"{Tag} switch {text}");
        }

        static void ParseMuteList(bool log)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in (_muteAmbient.Value ?? "").Split(','))
            {
                var name = part.Trim();
                if (name.Length > 0) set.Add(name);
            }
            _muted = set;
            AmbientMuteVersion++;
            if (log) LogSwitch("ambient muted=[" + string.Join(",", set) + "]");
        }

        internal static bool IsAmbientMuted(string name) => _muted != null && name != null && _muted.Contains(name);

        // Game thread: a mode or self-test key changed since the last frame.
        static void ApplyPending()
        {
            bool on = _mode.Value != NoiseProbeMode.Off;
            _modeOn = on;
            if (!on && Armed) Disarm("mode off");
            if (_selfTestRequest.Value)
            {
                if (on) _selfPending = true;
                else CancelSelfTestRequest("it needs NoiseProbe Detect or Capture");
            }
        }

        static void CancelSelfTestRequest(string why)
        {
            _selfPending = false;
            Plugin.Log.LogDebug($"{Tag} self-test request dropped: {why}");
            if (_selfTestRequest.Value) _selfTestRequest.Value = false;
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // The taps (game thread only). Call sites guard with "if (NoiseProbe.Armed)" so that their arguments cost nothing when off.

        /// <summary>One ring event. Never throws.</summary>
        internal static void Note(NoiseEv kind, string label = null, int a = 0, int b = 0, float v1 = 0f, float v2 = 0f, string label2 = null)
        {
            if (!Armed) return;
            try
            {
                _ring.Add(Time.realtimeSinceStartupAsDouble, AudioSettings.dspTime, kind, a, b, v1, v2, _labels.Intern(label), _labels.Intern(label2));
                _detector.Minute.OnEvent(kind, a, b);
            }
            catch (Exception) { /* a diagnostic never disturbs the game */ }
        }

        /// <summary>An event at a world position: V1 is the distance to the listener.</summary>
        internal static void NoteAt(NoiseEv kind, Vector3 position, string label = null, int a = 0, int b = 0, float v2 = 0f, string label2 = null)
        {
            if (!Armed) return;
            float distance = _listener != null ? Vector3.Distance(_listener.position, position) : -1f;
            Note(kind, label, a, b, distance, v2, label2);
        }

        /// <summary>SmallPhysicsObject.OnCollisionHandler: outcome 1 = the bank plays, 0 = skipped (no bank), 2 = muted by NoiseProbeMuteProps.</summary>
        internal static void NoteProp(object prop, int outcome)
        {
            if (!Armed) return;
            try
            {
                var p = prop as SmallPhysicsObject;
                if (p == null) return;
                NoteAt(NoiseEv.Prop, p.transform.position, p.name, outcome, 0, 0f, p.SoundType.ToString());
            }
            catch (Exception) { }
        }

        /// <summary>RoomReverb.Apply: the mixer category written; "changed" is whether it differs from the previous apply (all Equator rooms share category 4).</summary>
        internal static void NoteRoomReverb(int category)
        {
            if (!Armed) return;
            int changed = category != _lastReverbCategory ? 1 : 0;
            _lastReverbCategory = category;
            Note(NoiseEv.RoomReverb, null, category, changed);
        }

        /// <summary>RoomShotAcoustics.Apply: "changed" is whether the preset differs from the previous apply.</summary>
        internal static void NoteRoomShot(int roomId, string roomClass, string dimensions, string materials)
        {
            if (!Armed) return;
            string signature = roomClass + "|" + dimensions + "|" + materials;
            int changed = signature != _lastShotSignature ? 1 : 0;
            _lastShotSignature = signature;
            Note(NoiseEv.RoomShot, roomClass, roomId, changed);
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Per-frame driver

        /// <summary>Added only in DeveloperMode. With the probe off its per-frame cost is two static reads.</summary>
        public sealed class Driver : MonoBehaviour
        {
            void LateUpdate()
            {
                if (_dirty)
                {
                    _dirty = false;
                    try { ApplyPending(); } catch (Exception e) { Fail("config", e); }
                }
                if (_modeOn || Armed) Tick();
            }
        }

        static bool RaidActive() =>
            Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started &&
            Singleton<AudioListenerConsistencyManager>.Instantiated;

        internal static void Tick()
        {
            if (_failedSession) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                bool raid = RaidActive();
                if (raid != _inRaid)
                {
                    _inRaid = raid; _failedRaid = false;
                    if (raid) _raidStart = now;
                }
                if (!raid)
                {
                    if (Armed) Disarm("raid ended");
                    return;
                }
                if (!Armed)
                {
                    if (_failedRaid || now - _raidStart < 1.0) return;
                    Arm(now);
                    if (!Armed) return;
                }
                Frame(now);
                _detector.Minute.AddProbeCost((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            catch (Exception e) { Fail("frame", e); }
        }

        static void EnsureBuffers()
        {
            if (_ring != null) return;
            _ring = new NoiseEventRing(1024);
            _labels = new NoiseLabels();
            _block = new NoiseBlock();
            _detector = new NoiseDetector();
            _gap = new NoiseGapWatch();
            _policy = new NoiseCapturePolicy();
            _buf = new float[1024];
            _sources = new List<AudioSource>(256);
            _groups = new Dictionary<string, int>();
            _groupNames = new List<string>();
            _window = new List<NoiseEvent>(256);
            _testClips = new List<AudioClip>();
            _results = new ConcurrentQueue<string>();
        }

        static void Arm(double now)
        {
            try
            {
                EnsureBuffers();
                ReadAudioFormat();
                _audioReset = false; // a reset that happened before the probe armed (EFT resets the audio once at raid start) is not an event of this run
                _listener = Singleton<AudioListenerConsistencyManager>.Instance.transform;
                // ReadAudioFormat proved the readback on every channel it will use; a driver that refuses even channel 0 cannot be probed.
                if (_readChannels < 1) throw new InvalidOperationException("AudioListener.GetOutputData refuses every channel (driver " + AudioSettings.driverCapabilities + ")");
                _mapKey = ArenaMapRaid.CurrentMap?.Key ?? "vanilla";
                _detector.Reset(now);
                _detector.NextNumber = 1;
                _detector.BlockSeconds = _buf.Length / (double)_rate;
                _gap.Reset();
                _ring.Clear();
                _markNumber = 0; _calibMinutes = 0; _lastRead = 0; _lastRoom = int.MinValue; _lastReverbCategory = int.MinValue; _lastShotSignature = null;
                _pending = null; _captureDisabled = false; _lastCensus = -1;
                _policy.BeginRaid();
                _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
                _active = _mode.Value;
                Armed = true;
                if (_active == NoiseProbeMode.Capture) AttachTap();
                LogArmed();
            }
            catch (Exception e)
            {
                Armed = false; _failedRaid = true;
                DetachTap();
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning($"{Tag} the probe cannot arm ({e.GetType().Name}: {e.Message}); audio is unaffected");
                }
            }
        }

        static void ReadAudioFormat()
        {
            var config = AudioSettings.GetConfiguration();
            _channels = ChannelsOf(config.speakerMode);
            _rate = config.sampleRate > 0 ? config.sampleRate : AudioSettings.outputSampleRate;
            _readChannels = ReadableChannels(_channels);
        }

        // AudioListener.GetOutputData reads the DEVICE output, not the mix: under a 5.1 speaker mode on a stereo driver (GPU-2, 2026-10-29:
        // "driver=Stereo", SurroundAudio forcing Mode5point1) it throws ArgumentException "invalid channel" from channel 2 on, while the
        // OnAudioFilterRead tap still receives all six mix channels. Detect therefore reads the channels the driver has (the downmix a
        // player hears) and Capture records the mix; the armed line says "ch=2/6" when the two differ.
        static int ReadableChannels(int mixChannels)
        {
            int count = 0;
            for (int c = 0; c < mixChannels; c++)
            {
                try { AudioListener.GetOutputData(_buf, c); }
                catch (ArgumentException) { break; }
                count++;
            }
            return count;
        }

        static int ChannelsOf(AudioSpeakerMode mode)
        {
            switch (mode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Quad: return 4;
                case AudioSpeakerMode.Surround: return 5;
                case AudioSpeakerMode.Mode5point1: return 6;
                case AudioSpeakerMode.Mode7point1: return 8;
                default: return 2;
            }
        }

        static void LogArmed()
        {
            var config = AudioSettings.GetConfiguration();
            var spectators = SpectatorAudio.Current;
            Plugin.Log.LogDebug($"{Tag} " + NoiseReport.Armed(_active, _mapKey, config.speakerMode.ToString(), AudioSettings.driverCapabilities.ToString(), _readChannels, _channels,
                _rate, config.dspBufferSize, config.numRealVoices, config.numVirtualVoices,
                spectators == null ? "n/a" : spectators.CallsEnabled ? "on" : "off", Bay5AmbientAudio.MixSummary(),
                RoomShotAcoustics.IsOn(_mapKey) ? "on" : "off", RoomReverb.IsActive(_mapKey) ? "on" : "off", RingSeconds));
        }

        static void Disarm(string reason)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            try
            {
                if (_detector != null && Armed)
                {
                    HandleHits(_detector.Flush(now), now);
                    Plugin.Log.LogDebug($"{Tag} " + NoiseReport.MinuteLine(_detector.Minute, _mapKey, now) + " (final)");
                }
                EndSelfTest();
                DetachTap();
                _pending = null;
                if (_label != null) _label.enabled = false;
            }
            catch (Exception e) { Plugin.Log.LogDebug($"{Tag} disarm cleanup: {e.GetType().Name}: {e.Message}"); }
            finally
            {
                bool was = Armed;
                Armed = false;
                if (was) Plugin.Log.LogDebug($"{Tag} disarmed ({reason})");
            }
        }

        static void Fail(string what, Exception e)
        {
            _failedSession = true;
            try { Disarm("failed"); } catch (Exception) { }
            Armed = false;
            Plugin.Log.LogWarning($"{Tag} the probe stopped for this session ({what}: {e.GetType().Name}: {e.Message}); audio is unaffected");
        }

        static void Frame(double now)
        {
            // Mode switch while armed (Detect <-> Capture).
            var wanted = _mode.Value;
            if (wanted != _active)
            {
                if (wanted == NoiseProbeMode.Off) { Disarm("mode off"); return; }
                _active = wanted;
                if (_active == NoiseProbeMode.Capture) AttachTap(); else DetachTap();
                LogArmed();
            }
            if (_audioReset)
            {
                _audioReset = false;
                Note(NoiseEv.Config, null, _deviceChanged ? 1 : 0);
                _detector.Settle(now);
                _gap.Reset();
                int channels = _channels, rate = _rate;
                ReadAudioFormat();
                if (_active == NoiseProbeMode.Capture && (channels != _channels || rate != _rate)) AttachTap();
            }
            DrainResults();

            float dt = Time.unscaledDeltaTime;
            if (dt >= 0.05f && now - _raidStart > 3.0) Note(NoiseEv.Hitch, null, 0, 0, dt * 1000f);
            GcTick();
            RoomTick();

            if (_markKey.Value != KeyCode.None && Input.GetKeyDown(_markKey.Value)) OnMark(now);
            if (_labelUntil > 0 && now >= _labelUntil) { _labelUntil = 0; if (_label != null) _label.enabled = false; }
            SelfTestTick(now);

            if (now - _lastRead >= ReadInterval) Read(now);
            if (_gap.Update(now, AudioSettings.dspTime, out double lag)) EmitGap(_detector.ObserveGap(now, lag, "src=dsp"), now);
            TapGapTick(now);

            PendingTick(now);
            if (_detector.MinuteDue(now))
            {
                Plugin.Log.LogDebug($"{Tag} " + NoiseReport.MinuteLine(_detector.Minute, _mapKey, now));
                if (++_calibMinutes <= 5) Plugin.Log.LogDebug($"{Tag} " + NoiseReport.CalibrationLine(_detector.Minute, _calibMinutes));
                _detector.RollMinute(now);
            }
        }

        static void GcTick()
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            if (g0 != _gc0) { Note(NoiseEv.Gc, null, 0, g0 - _gc0); _gc0 = g0; }
            if (g1 != _gc1) { Note(NoiseEv.Gc, null, 1, g1 - _gc1); _gc1 = g1; }
            if (g2 != _gc2) { Note(NoiseEv.Gc, null, 2, g2 - _gc2); _gc2 = g2; }
        }

        static void RoomTick()
        {
            try
            {
                var state = NativeSpatialAudio.Current;
                if (state == null || !state.Finished || state.Released || state.System == null) return;
                var room = state.System.ListenerCurrentRoom;
                if (room == null || !room.IsValid) return;
                int id = (int)room.ID;
                if (id == _lastRoom) return;
                _lastRoom = id;
                Note(NoiseEv.Room, room.Type.ToString(), id, room.IsOutdoor ? 1 : 0);
            }
            catch (Exception) { /* the room graph is not ours to break */ }
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Detect

        static void Read(double now)
        {
            _lastRead = now;
            _block.Reset();
            for (int c = 0; c < _readChannels; c++)
            {
                AudioListener.GetOutputData(_buf, c);
                _block.AddChannel(c, _buf, 0, 1, _buf.Length);
            }
            _block.Finish();
            if (_test != null && _test.Running) _test.OnBlock(now, _block.Peak, _detector.LastBgPk);
            HandleHits(_detector.Process(_block, now), now);
        }

        static void HandleHits(int count, double now)
        {
            for (int i = 0; i < count; i++) OnHit(_detector.Hits[i], now);
        }

        static void EmitGap(NoiseHit hit, double now)
        {
            if (hit != null) OnHit(hit, now);
        }

        static void OnHit(NoiseHit hit, double now)
        {
            if (_test != null && (hit.Kind == NoiseKind.Peak || hit.Kind == NoiseKind.Click) && _test.InWindow(hit.Time)) { _test.OnHit(hit); return; }
            bool eventExplained = _ring.HasCause(hit.Time, 0.4, 0.05);
            var spectators = SpectatorAudio.Current;
            bool voice = spectators != null && spectators.AnyAudible();
            bool orphan = !eventExplained && !voice;
            if (orphan) _detector.Minute.Orphan++;
            if (!eventExplained) _detector.Minute.EvOrphan++;
            Note(NoiseEv.Burst, NoiseReport.KindText(hit.Kind), hit.Number, 0, hit.OverRmsDb);
            string wall = NoiseText.Clock(DateTime.Now.AddSeconds(hit.Time - now));
            string context = NoiseReport.Context(_ring, _labels, hit.Time);
            string voices = spectators != null ? spectators.ProbeVoices() : "";
            string text = NoiseReport.BurstLine(hit, wall, Where(spectators) + " orphan=" + (orphan ? 1 : 0) + " evOrphan=" + (eventExplained ? 0 : 1), "spec=[" + voices + "] " + context + " census=[" + Census(now) + "]");
            Plugin.Log.LogDebug($"{Tag} {text}");
            if (_active == NoiseProbeMode.Capture && _pcm != null && orphan && (hit.Kind == NoiseKind.Click || (hit.Kind == NoiseKind.Peak && hit.Level == NoiseLevel.Mid)))
                RequestCapture(now, true, text + "\n" + string.Join("\n", BuildDump(now, spectators).ToArray()), null);
        }

        static string Where(SpectatorAudio spectators)
        {
            var player = GamePlayerOwner.MyPlayer;
            Vector3 pos = player != null ? player.Position : (_listener != null ? _listener.position : Vector3.zero);
            float dt = Time.unscaledDeltaTime;
            var c = NoiseText.Inv;
            return "pos=(" + pos.x.ToString("0.0", c) + "," + pos.y.ToString("0.0", c) + "," + pos.z.ToString("0.0", c) + ") room=" + RoomText() +
                " fps=" + (dt > 0f ? (int)(1f / dt) : 0).ToString(c) + " voices=" + (spectators != null ? spectators.PlayingVoices : 0).ToString(c);
        }

        static string RoomText()
        {
            try
            {
                var state = NativeSpatialAudio.Current;
                if (state == null || !state.Finished || state.Released || state.System == null) return "n/a";
                var room = state.System.ListenerCurrentRoom;
                return room != null && room.IsValid ? ((int)room.ID).ToString(NoiseText.Inv) + "/" + room.Type : "n/a";
            }
            catch (Exception) { return "n/a"; }
        }

        /// <summary>Playing sources of the pooled BetterAudio hierarchy by mixer group. Only called at a logged burst or a mark (at most one per 250 ms): the
        /// hierarchy walk is the one scene search of this file and must never run per frame.</summary>
        static string Census(double now)
        {
            if (now - _lastCensus < 0.25 && _lastCensus >= 0) return _lastCensusText + " (reused)";
            _lastCensus = now;
            var sb = new StringBuilder(160);
            try
            {
                if (MonoBehaviourSingleton<BetterAudio>.Exist(out var audio))
                {
                    _sources.Clear(); _groups.Clear(); _groupNames.Clear();
                    audio.transform.GetComponentsInChildren<AudioSource>(false, _sources);
                    int playing = 0;
                    for (int i = 0; i < _sources.Count; i++)
                    {
                        var source = _sources[i];
                        if (source == null || !source.isPlaying) continue;
                        playing++;
                        var group = source.outputAudioMixerGroup;
                        string name = group != null ? group.name : "none";
                        int n;
                        _groups.TryGetValue(name, out n);
                        _groups[name] = n + 1;
                    }
                    foreach (var key in _groups.Keys) _groupNames.Add(key);
                    _groupNames.Sort(StringComparer.Ordinal);
                    sb.Append("pool=").Append(_sources.Count).Append(" playing=").Append(playing);
                    for (int i = 0; i < _groupNames.Count; i++) sb.Append(' ').Append(_groupNames[i]).Append(':').Append(_groups[_groupNames[i]]);
                }
                sb.Append(" ownedAmbient=").Append(AmbientPlaying);
                var spectators = SpectatorAudio.Current;
                if (spectators != null) sb.Append(" ownedSpectator=").Append(spectators.PlayingVoices);
            }
            catch (Exception e) { sb.Append("census failed: ").Append(e.GetType().Name); }
            return _lastCensusText = sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Mark

        // The last ten seconds of the event ring (at most 200 lines), the bursts among them, the playing voices and the census.
        static List<string> BuildDump(double now, SpectatorAudio spectators)
        {
            var lines = new List<string>(64);
            _window.Clear();
            _ring.CopyWindow(now - DumpSeconds, now, _window);
            int first = Math.Max(0, _window.Count - 200);
            if (first > 0) lines.Add("  (" + first.ToString(NoiseText.Inv) + " older events not shown)");
            for (int i = first; i < _window.Count; i++) lines.Add(NoiseReport.EventLine(_window[i], _labels, now));
            var hits = new StringBuilder();
            for (int i = 0; i < _window.Count; i++)
            {
                var e = _window[i];
                if (e.Kind != NoiseEv.Burst) continue;
                if (hits.Length > 0) hits.Append(", ");
                hits.Append("BURST #").Append(e.A.ToString(NoiseText.Inv)).Append(" (").Append((e.Time - now).ToString("0.00", NoiseText.Inv)).Append("s, ")
                    .Append(NoiseText.Signed(e.V1)).Append(" dB)");
            }
            lines.Add("  hits in window: " + (hits.Length > 0 ? hits.ToString() : "none"));
            lines.Add("  voices: [" + (spectators != null ? spectators.ProbeVoices() : "") + "] census=[" + Census(now) + "]");
            return lines;
        }

        static void OnMark(double now)
        {
            int number = ++_markNumber;
            string wall = NoiseText.Clock(DateTime.Now);
            var spectators = SpectatorAudio.Current;
            var lines = new List<string>(64);
            lines.Add(NoiseReport.MarkHeader(number, wall, Time.frameCount, Where(spectators)));
            lines.AddRange(BuildDump(now, spectators));
            Note(NoiseEv.Mark, null, number);

            string captureText = null;
            if (_active == NoiseProbeMode.Capture && _pcm != null)
                captureText = RequestCapture(now, false, string.Join("\n", lines.ToArray()), "mark" + number.ToString(NoiseText.Inv));
            foreach (var line in lines) Plugin.Log.LogDebug($"{Tag} {line}");
            if (captureText != null) Plugin.Log.LogDebug($"{Tag} MARK #{number} {captureText}");
            ShowLabel("NOISE MARK #" + number, now);
        }

        static void ShowLabel(string text, double now)
        {
            if (_label == null) _label = Plugin.Instance.gameObject.AddComponent<MarkLabel>();
            MarkLabel.Text = text;
            _label.enabled = true;
            _labelUntil = now + 1.5;
        }

        /// <summary>A silent IMGUI label (no toast: a UI sound would confound the listening test). Enabled for 1.5 s per mark, so nothing runs otherwise.</summary>
        public sealed class MarkLabel : MonoBehaviour
        {
            internal static string Text = "";
            static GUIStyle _style;

            void OnGUI()
            {
                if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
                var old = GUI.color;
                GUI.color = new Color(1f, .85f, .1f, 1f);
                GUI.Label(new Rect(24f, 24f, 480f, 32f), Text, _style);
                GUI.color = old;
            }
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Self-test

        static void SelfTestTick(double now)
        {
            if (_selfPending && _test == null && now - _raidStart > 4.0)
            {
                _selfPending = false;
                if (_selfTestRequest.Value) _selfTestRequest.Value = false;
                _test = new NoiseSelfTest(null);
                _test.Begin(now);
                _testStartFrame = _pcm != null ? _pcm.WrittenFrames : 0;
                Plugin.Log.LogDebug($"{Tag} selftest started: four 10 ms noise bursts at {string.Join(", ", Array.ConvertAll(NoiseSelfTest.DefaultLevelsDb, v => v.ToString("0", NoiseText.Inv)))} dBFS through an owned 2D source");
            }
            if (_test == null || !_test.Running) return;
            try
            {
                if (_test.ShouldPlay(now, out float level))
                {
                    _testStartFrame = _pcm != null ? _pcm.WrittenFrames : 0;
                    PlayBurst(level);
                    Note(NoiseEv.SelfTest, "burst " + level.ToString("0", NoiseText.Inv));
                }
                while (_test.ResultDue(now))
                {
                    string line = _test.TakeResult(now, _pcm != null ? NoiseText.Db(_pcm.PeakSince(_testStartFrame)) : float.NaN);
                    if (line != null) Plugin.Log.LogDebug($"{Tag} {line}");
                }
                if (!_test.Running) EndSelfTest();
            }
            catch (Exception e)
            {
                EndSelfTest();
                Plugin.Log.LogWarning($"{Tag} the self-test stopped ({e.GetType().Name}: {e.Message})");
            }
        }

        static void PlayBurst(float levelDb)
        {
            if (!MonoBehaviourSingleton<BetterAudio>.Exist(out var audio)) throw new InvalidOperationException("no BetterAudio");
            if (_testObject == null)
            {
                _testObject = new GameObject("ArenaMapPort NoiseProbe self-test");
                _testObject.transform.SetParent(Plugin.Instance.transform, false);
                _testSource = _testObject.AddComponent<AudioSource>();
                _testSource.playOnAwake = false; _testSource.spatialBlend = 0f; _testSource.spatialize = false; _testSource.volume = 1f; _testSource.loop = false;
                _testSource.outputAudioMixerGroup = audio.SourceGroups[(int)BetterAudio.AudioSourceGroupType.Environment].MixerGroup;
            }
            var data = NoiseSelfTest.MakeBurst(_rate, levelDb, 7 + (int)levelDb);
            var clip = AudioClip.Create("ArenaMapPort noise self-test " + levelDb.ToString("0", NoiseText.Inv), data.Length, 1, _rate, false);
            if (!clip.SetData(data, 0)) { UnityEngine.Object.Destroy(clip); throw new InvalidOperationException("self-test samples rejected"); }
            _testClips.Add(clip);
            _testSource.Stop();
            _testSource.clip = clip;
            _testSource.Play();
        }

        static void EndSelfTest()
        {
            _test = null;
            if (_testSource != null) _testSource.Stop();
            if (_testObject != null) UnityEngine.Object.Destroy(_testObject);
            _testObject = null; _testSource = null;
            if (_testClips != null)
            {
                foreach (var clip in _testClips) if (clip != null) UnityEngine.Object.Destroy(clip);
                _testClips.Clear();
            }
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Capture (the audio filter tap, the PCM ring and the WAV export)

        /// <summary>The audio filter on the listener's object. Runs on the audio thread: no allocation, no lock, no Unity API, and a catch-all that switches it off.</summary>
        public sealed class NoiseTap : MonoBehaviour
        {
            internal static NoisePcmRing Ring;
            internal static volatile bool Dead;
            internal static volatile int ChannelMismatch;
            internal static long LastTicks, MaxGapTicks;

            void OnAudioFilterRead(float[] data, int channels)
            {
                try
                {
                    var ring = Ring;
                    if (ring == null || Dead) return;
                    if (channels != ring.Channels) { ChannelMismatch = channels; return; }
                    long now = Stopwatch.GetTimestamp();
                    long last = LastTicks;
                    if (last != 0)
                    {
                        long gap = now - last;
                        if (gap > Volatile.Read(ref MaxGapTicks)) Volatile.Write(ref MaxGapTicks, gap);
                    }
                    LastTicks = now;
                    ring.Write(data, data.Length);
                }
                catch (Exception) { Dead = true; }
            }
        }

        static void AttachTap()
        {
            DetachTap();
            try
            {
                var manager = Singleton<AudioListenerConsistencyManager>.Instance;
                if (manager.GetComponent<AudioListener>() == null)
                {
                    Plugin.Log.LogWarning($"{Tag} the listener object has no AudioListener component; Capture stays off (Detect still runs)");
                    return;
                }
                _pcm = new NoisePcmRing(_channels, _rate, RingSeconds);
                NoiseTap.Dead = false; NoiseTap.ChannelMismatch = 0; NoiseTap.LastTicks = 0; Volatile.Write(ref NoiseTap.MaxGapTicks, 0);
                NoiseTap.Ring = _pcm;
                _tap = manager.gameObject.AddComponent<NoiseTap>();
            }
            catch (Exception e)
            {
                _pcm = null; NoiseTap.Ring = null;
                Plugin.Log.LogWarning($"{Tag} the capture tap could not be attached ({e.GetType().Name}: {e.Message}); Detect still runs");
            }
        }

        static void DetachTap()
        {
            NoiseTap.Ring = null;
            if (_tap != null) UnityEngine.Object.Destroy(_tap);
            _tap = null; _pcm = null;
        }

        static void TapGapTick(double now)
        {
            if (_tap == null) return;
            if (NoiseTap.Dead)
            {
                Plugin.Log.LogWarning($"{Tag} the audio filter tap stopped itself after an exception on the audio thread; Capture is off for this raid");
                DetachTap();
                return;
            }
            int mismatch = NoiseTap.ChannelMismatch;
            if (mismatch != 0)
            {
                NoiseTap.ChannelMismatch = 0;
                ReadAudioFormat();
                if (_channels == mismatch) AttachTap();
                return;
            }
            long gapTicks = Interlocked.Exchange(ref NoiseTap.MaxGapTicks, 0L);
            double ms = gapTicks * 1000.0 / Stopwatch.Frequency;
            if (ms > 3.0 * _buf.Length * 1000.0 / _rate && Application.isFocused) EmitGap(_detector.ObserveGap(now, ms, "src=tap"), now);
        }

        /// <summary>Returns the text for the MARK line ("wav=... pending" or the refusal), or null when nothing was decided.</summary>
        static string RequestCapture(double now, bool automatic, string text, string suffix)
        {
            if (_captureDisabled || _pcm == null) return null;
            string directory = ExportDirectory();
            string reason;
            var decision = _policy.Request(now, automatic, _pending != null, FreeBytes(directory), out reason);
            if (decision == NoiseCaptureDecision.Extend)
            {
                _pending.DueAt = now + CaptureAfterMark;
                _pending.Text.Append("\n--- extended by a mark ").Append(NoiseText.Clock(DateTime.Now)).Append('\n').Append(text);
                return "wav shared with mark #" + _pending.Number.ToString(NoiseText.Inv);
            }
            if (decision == NoiseCaptureDecision.Refuse)
            {
                if (reason == "disk" && !_captureDisabled)
                {
                    _captureDisabled = true;
                    Plugin.Log.LogWarning($"{Tag} less than 500 MB free for the noise captures; Capture is off for this raid (Detect still runs)");
                }
                return "wav refused (" + reason + ")";
            }
            _policy.Started(now, automatic);
            int number = _markNumber;
            string name = "noise-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", NoiseText.Inv) + "-" + (automatic ? "auto" + _policy.AutomaticThisRaid.ToString(NoiseText.Inv) : suffix);
            _pending = new PendingCapture { DueAt = now + CaptureAfterMark, Number = number, Automatic = automatic, Name = name, Text = new StringBuilder(text) };
            return "wav pending " + name + ".wav (written in " + CaptureAfterMark.ToString("0", NoiseText.Inv) + " s)";
        }

        static void PendingTick(double now)
        {
            if (_pending == null || now < _pending.DueAt || _pcm == null) return;
            if (Interlocked.CompareExchange(ref _exportBusy, 1, 0) != 0) return;
            var job = new ExportJob
            {
                Ring = _pcm, EndFrame = _pcm.WrittenFrames, Frames = (int)(CaptureSeconds * _pcm.SampleRate), Name = _pending.Name,
                Text = _pending.Text.ToString(), Directory = ExportDirectory(), Fallback = FallbackDirectory(), Results = _results
            };
            _pending = null;
            ThreadPool.QueueUserWorkItem(RunExport, job);
        }

        sealed class ExportJob
        {
            public NoisePcmRing Ring;
            public long EndFrame;
            public int Frames;
            public string Name, Text, Directory, Fallback;
            public ConcurrentQueue<string> Results;
        }

        static string ExportDirectory() => Path.Combine(BepInEx.Paths.BepInExRootPath, "logs", "ArenaMapPort-noise");

        static string FallbackDirectory() =>
            Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "diagnostics");

        static long FreeBytes(string directory)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))).AvailableFreeSpace; }
            catch (Exception) { return -1; }
        }

        // Worker thread: files and the sample copy only; the result goes back through the queue and is logged on the game thread.
        static void RunExport(object state)
        {
            var job = (ExportJob)state;
            var results = job.Results;
            try
            {
                string directory = job.Directory;
                try { Directory.CreateDirectory(directory); }
                catch (Exception) { directory = job.Fallback; Directory.CreateDirectory(directory); }
                int channels = job.Ring.Channels, rate = job.Ring.SampleRate;
                TrimOld(directory, (long)job.Frames * channels * 2 + NoiseWav.HeaderBytes);
                var data = new short[job.Frames * channels];
                if (!job.Ring.TryCopy(job.EndFrame, job.Frames, data, out long start, out int copied))
                { results.Enqueue("!the capture ring was overwritten during the copy; nothing written"); return; }
                string wav = Path.Combine(directory, job.Name + ".wav");
                using (var stream = new FileStream(wav, FileMode.Create, FileAccess.Write, FileShare.Read))
                    NoiseWav.Write(stream, data, copied, channels, rate);
                File.WriteAllText(Path.Combine(directory, job.Name + ".txt"), job.Text.Replace("\n", "\r\n"), new UTF8Encoding(false));
                results.Enqueue("wav=" + wav + " " + (copied / (double)rate).ToString("0.0", NoiseText.Inv) + "s " + channels.ToString(NoiseText.Inv) + "ch " +
                    ((copied * (long)channels * 2 + NoiseWav.HeaderBytes) / 1048576.0).ToString("0.0", NoiseText.Inv) + "MB");
            }
            catch (Exception e) { results.Enqueue("!the capture could not be written (" + e.GetType().Name + ": " + e.Message + ")"); }
            finally { Interlocked.Exchange(ref _exportBusy, 0); }
        }

        static void TrimOld(string directory, long incoming)
        {
            var files = new DirectoryInfo(directory).GetFiles("noise-*.wav");
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            var sizes = new long[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                string txt = Path.ChangeExtension(files[i].FullName, ".txt");
                sizes[i] = files[i].Length + (File.Exists(txt) ? new FileInfo(txt).Length : 0);
            }
            int remove = NoiseCapturePolicy.PlanDeletions(sizes, incoming);
            for (int i = 0; i < remove; i++)
            {
                try
                {
                    files[i].Delete();
                    string txt = Path.ChangeExtension(files[i].FullName, ".txt");
                    if (File.Exists(txt)) File.Delete(txt);
                }
                catch (Exception) { /* the cap is best effort */ }
            }
        }

        static void DrainResults()
        {
            while (_results.TryDequeue(out string message))
            {
                if (message.StartsWith("!", StringComparison.Ordinal)) Plugin.Log.LogWarning($"{Tag} {message.Substring(1)}");
                else Plugin.Log.LogDebug($"{Tag} capture {message}");
            }
        }
    }
}
