using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArenaMapPort.Crypto;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>Bounded overlapping crowd calls and the crowd's reactions. Owned sources, native mixer and room propagation;
    /// no player/AI phrase events, no match announcements, no changes to shared audio presets.
    /// Cadence (SpectatorAudioSchedule, since 2026-10-30): the first call 3 to 8 s after the raid starts, then one every 4.0 to 8.0 s, at most two voices at a time. The
    /// continuous murmur between the calls is not made here but by SpectatorCrowdBed.cs, which the crowd creates right after this class from the same positions.
    /// The NoiseProbe developer diagnostic (NoiseProbe.cs) records starts, stops with their reason and an onset trace of every call through
    /// one-line taps guarded by NoiseProbe.Armed; its A/B key NoiseProbeSpectatorFade adds a 150 ms fade-in at the start and an 80 ms
    /// fade-out when a call is stopped early (a source that is fading out is released afterwards). Both are off by default.
    ///
    /// Reactions (2026-11-02, docs/PLAN-CROWD-REACTIONS.md): what Arena does is play a bank of the fans' clips on a match event (a kill, a death, a round start or end, a shot at the
    /// stands); what the port lacks is the events and the clips, so a mode plugin asks through SpectatorCrowd.React and this class plays the answer. The same component holds two
    /// independently validated sets, the 13 calls and the reaction set, each failing with its own one Warning and leaving the other (and the crowd) working; Current exists when either
    /// works. A reaction is 1 to 3 voices from three reaction slots that are separate from the calls' two: the planner (CrowdReactionPlanner) decides, this class plays through the
    /// calls' own path (an Environment source of BetterAudio, the mixer group, the rolloff, spatial blend 1, Combined occlusion) with gain Spectators/ReactionVolume, starts each further voice
    /// 0.12 to 0.45 s after the previous one, asks the bed for a swell, and holds the random calls for the reaction's length plus a gap. The reaction section of Update runs BEFORE the
    /// calls' living-player guard and has its own gate (the key on, the raid Started, the spatial audio services up) and never asks whether the local player is alive: in an ArenaMode
    /// raid the raid stays Started across the body handover, so a Disapprove for the local death and a Finish during the respawn wait still sound. The calls' guard,
    /// StopAllVoices and the schedule's Stop leave the reaction voices alone. The listener is the main camera, else the local player's body, else the reaction is dropped.
    /// The reaction keys (Spectators/Reactions, ReactionVolume, ReactionSwell, ReactionGapScale; SpectatorReactionDemo.cs binds the demo's two) are developer keys like SpectatorCalls:
    /// honoured only with DeveloperMode, live; the defaults apply otherwise.
    /// Host run (GPU-2, 2026-11-02, the developer demo on the four crowd maps; docs/progress/spectator-audio.md "Host run on GPU-2"): every kind played, 0 warnings, and a plain raid
    /// stayed Started after the local death, so the reactions went on there while the calls were silent. ArenaMode's body handover itself is not host-checked; nobody has listened yet.</summary>
    internal sealed class SpectatorAudio : MonoBehaviour
    {
        const string Tag = "[SpectatorAudio]";
        const float Gain = .5f;
        const float FadeInSeconds = .15f, FadeOutSeconds = .08f;
        const float ReactionVolumeDefault = .5f, ReactionSwellDefault = 1f;
        static ConfigEntry<bool> _callsEnabled, _reactionsEnabled;
        static ConfigEntry<float> _reactionVolume, _reactionSwell, _reactionGapScale;
        Vector3[] _positions;
        float[] _distances;
        SpectatorAudioManifest _manifest;
        short[][] _pcm;
        AudioClip[] _clips;
        SpectatorAudioSchedule _schedule;
        Voice[] _voices;
        int[] _families;
        readonly HashSet<int> _busySpeakers = new HashSet<int>();
        readonly HashSet<int> _busyClips = new HashSet<int>();
        Voice _lastVoice;
        bool _ready, _callsReady, _reset, _warned;
        readonly float[] _outputSamples = new float[256];
        readonly List<Fading> _fading = new List<Fading>();
        bool _probeAlive = true;
        public string MapKey, LastClip;
        public int PlaybackCount, ConfirmedPlaybackCount, LastSpeaker = -1, AudioResets;
        public float LastDistance, Rolloff, LastStartedAt, NextCallAt;
        public Vector3 LastPosition;
        public bool Playing, NativeOcclusion;
        public float OcclusionGain, SourceVolume, LastOutputPeak;
        public int ActiveVoices, PlayingVoices, PeakConcurrentVoices;
        public string ActiveSpeakerIds, ActiveClipNames;
        public bool CallsEnabled => !DeveloperOptions.Enabled || _callsEnabled == null || _callsEnabled.Value;
        /// <summary>The live crowd of the running raid (NoiseProbe census and orphan test); null outside an owned raid.</summary>
        internal static SpectatorAudio Current { get; private set; }
        static bool SpectatorFade => DeveloperOptions.Enabled && NoiseProbe.SpectatorFade;

        // ---- the reactions: the set, the planner and their own voice slots (outside StopAllVoices and the calls' schedule)
        bool _reactionsReady, _kindsPublished, _reactionWarned;
        SpectatorReactionSet _rset;
        short[][] _rpcm;
        AudioClip[] _rclips;
        int[] _rfamilies;
        float[] _packed;
        CrowdReactionPlanner _planner;
        CrowdReactionSpeakers _speakers;
        Voice[] _rvoices;
        int _reactionActive;
        float _appliedGapScale = float.NaN;
        readonly CrowdReactionRequest[] _requests = new CrowdReactionRequest[CrowdReactionQueue.Capacity];
        readonly HashSet<int> _rBusySpeakers = new HashSet<int>();
        readonly HashSet<int> _rBusyFamilies = new HashSet<int>();
        readonly int[] _chosen = new int[CrowdReactionPlanner.MaxVoices];
        readonly Voice[] _planSlots = new Voice[CrowdReactionPlanner.MaxVoices];
        public string LastReaction;
        public int ReactionCount, ReactionVoiceCount, ReactionSourceFailures, PlayingReactionVoices;
        /// <summary>Reaction voices that hold a slot: waiting for their staggered start or playing.</summary>
        public int ActiveReactionVoices => _reactionActive;
        static bool ReactionsOn => !DeveloperOptions.Enabled || _reactionsEnabled == null || _reactionsEnabled.Value;
        static float ReactionVolumeNow => DeveloperOptions.Enabled && _reactionVolume != null ? Unit(_reactionVolume.Value, ReactionVolumeDefault) : ReactionVolumeDefault;
        static float ReactionSwellNow => DeveloperOptions.Enabled && _reactionSwell != null ? Unit(_reactionSwell.Value, ReactionSwellDefault) : ReactionSwellDefault;
        static float ReactionGapScaleNow => DeveloperOptions.Enabled && _reactionGapScale != null ? _reactionGapScale.Value : 1f;
        static float Unit(float value, float fallback) => float.IsNaN(value) ? fallback : Mathf.Clamp01(value);

        internal static void Install()
        {
            _callsEnabled = Plugin.Instance.Settings.Bind("Audio diagnostics", "SpectatorCalls", true,
                "Developer hearing comparison: enable original spectator calls. Live; off stops only spectator " +
                "voices and keeps the visual crowd, map ambience and other sounds. On restarts the normal initial delay. " +
                "Ignored outside DeveloperMode.");
            _callsEnabled.SettingChanged += (_, __) =>
            {
                if (DeveloperOptions.Enabled)
                    Plugin.Log.LogDebug($"{Tag} developer hearing comparison: spectator calls enabled={_callsEnabled.Value}");
            };
            var s = Plugin.Instance.Settings;
            _reactionsEnabled = s.Bind("Spectators", "Reactions", true,
                "Developer hearing comparison: the spectators' reaction voices (a kill, a death, a round start or end, a shot at the stands) and the bed swell that goes with them. " +
                "Live; off stops the reaction voices, drops what waits and reports no reaction kinds to a mode plugin, on offers them again. The random calls and the murmur bed are not affected. " +
                "Ignored outside DeveloperMode, where the reactions are on.");
            _reactionVolume = s.Bind("Spectators", "ReactionVolume", ReactionVolumeDefault, new ConfigDescription(
                "Developer hearing comparison: gain of the reaction voices (the calls play at 0.5). Takes effect with the next reaction. Ignored outside DeveloperMode.",
                new AcceptableValueRange<float>(0f, 1f)));
            _reactionSwell = s.Bind("Spectators", "ReactionSwell", ReactionSwellDefault, new ConfigDescription(
                "Developer hearing comparison: how far a reaction lifts the crowd murmur bed for a moment, as a factor of the reaction's strength (1 = the strength itself, 0 = no swell). " +
                "Takes effect with the next reaction. Ignored outside DeveloperMode.", new AcceptableValueRange<float>(0f, 1f)));
            _reactionGapScale = s.Bind("Spectators", "ReactionGapScale", 1f, new ConfigDescription(
                "Developer hearing comparison: multiplies the cooldown between two reactions of one kind (3 s for Approve and Disapprove, 15 s for TakeCover, 5 s for Start and Finish; halved " +
                "from strength 0.8). 0 removes the cooldowns, the pace of one start per 0.75 s stays. Live. Ignored outside DeveloperMode.",
                new AcceptableValueRange<float>(0f, CrowdReactionPlanner.MaxGapScale)));
            SpectatorReactionDemo.Install();
        }

        sealed class Voice
        {
            public GameObject Emitter;
            public BetterSource Source;
            public SpatialAudioSystem System;
            public int Container = -1, Speaker, Clip;
            public double EndsAt;
            public float NextOutputProbe, Distance;
            public bool Confirmed;
            public double StartedAt;
            public int Number, Frame = -1;
            // reaction slots only: waiting for the staggered start, when it is due, and the kind that asked
            public bool Pending;
            public float StartAt;
            public int Kind;
        }

        sealed class Fading
        {
            public BetterSource Source;
            public float ReleaseAt;
        }

        internal static void Create(ArenaSpectators crowd, Vector3[] positions)
        {
            var folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "spectator-audio");
            // The two sets are independent: a damaged one is one Warning of its own and never takes the other (or the crowd) with it.
            SpectatorAudioManifest manifest = null;
            short[][] pcm = null;
            try
            {
                manifest = JsonConvert.DeserializeObject<SpectatorAudioManifest>(File.ReadAllText(Path.Combine(folder, "spectator-audio.json")));
                if (manifest == null) throw new InvalidDataException("missing spectator audio manifest");
                manifest.Validate();
                pcm = new short[manifest.clips.Length][];
                // Windows CNG when it works (managed SHA-256 runs at tens of MB/s in EFT's Mono); the check itself stays fail-closed.
                var crypto = CryptoProviders.Select(null);
                for (int i = 0; i < pcm.Length; i++)
                {
                    var c = manifest.clips[i];
                    var bytes = File.ReadAllBytes(Path.Combine(folder, c.file));
                    if (bytes.Length != c.bytes || BitConverter.ToString(crypto.Sha256(bytes, 0, bytes.Length)).Replace("-", "").ToLowerInvariant() != c.sha256)
                        throw new InvalidDataException("damaged spectator clip: " + c.file);
                    var wav = PcmWav.Read(bytes);
                    if (wav.Channels != c.channels || wav.SampleRate != c.sample_rate || wav.Frames != c.frames)
                        throw new InvalidDataException("unexpected spectator WAV format: " + c.file);
                    pcm[i] = wav.Samples;
                }
            }
            catch (Exception e)
            {
                manifest = null; pcm = null;
                Plugin.Log.LogWarning($"{Tag} spectator calls are unavailable ({e.GetType().Name}: {e.Message}); the crowd remains visible");
            }
            // The reaction set was read, hashed and decoded off the main thread together with the crowd package (ArenaSpectators.LoadReactionPackage).
            var prepared = crowd.ReactionData;
            crowd.ReactionData = null;
            SpectatorReactionSet rset = null;
            short[][] rpcm = null;
            CrowdReactionPlanner planner = null;
            if (prepared != null)
            {
                if (prepared.Error != null)
                    Plugin.Log.LogWarning($"{Tag} spectator reactions are unavailable ({prepared.Error.GetType().Name}: {prepared.Error.Message}); the calls and the crowd remain");
                else if (prepared.Absent)
                    Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: the spectator audio package has no reaction set; none will play");
                else
                {
                    try
                    {
                        planner = new CrowdReactionPlanner(prepared.Manifest.reactions, unchecked(Environment.TickCount ^ crowd.GetInstanceID() ^ 0x2545F491));
                        rset = prepared.Manifest.reactions;
                        rpcm = prepared.Pcm;
                        if (planner.KindsMask == 0) throw new InvalidDataException("the reaction set has no usable clips");
                    }
                    catch (Exception e)
                    {
                        planner = null; rset = null; rpcm = null;
                        Plugin.Log.LogWarning($"{Tag} spectator reactions are unavailable ({e.GetType().Name}: {e.Message}); the calls and the crowd remain");
                    }
                }
            }
            if (manifest == null && planner == null) return;
            GameObject root = null;
            try
            {
                root = new GameObject("ArenaMapPort_SpectatorVoice_" + crowd.MapKey);
                root.transform.SetParent(crowd.transform, false);
                var voice = root.AddComponent<SpectatorAudio>();
                voice.MapKey = crowd.MapKey;
                voice._positions = positions;
                voice.Rolloff = crowd.MapKey == "arena_bowl" ? 150 : 90;
                if (manifest != null)
                {
                    voice._distances = new float[positions.Length];
                    voice._manifest = manifest; voice._pcm = pcm; voice._clips = new AudioClip[pcm.Length];
                    voice._families = manifest.clips.Select(c => CrowdReactionPool.FamilyOf(c.name)).ToArray();
                    voice._voices = new Voice[SpectatorAudioSchedule.MaxConcurrent];
                    for (int i = 0; i < voice._voices.Length; i++)
                    {
                        var emitter = new GameObject("SpectatorVoiceEmitter_" + i);
                        emitter.transform.SetParent(root.transform, false);
                        voice._voices[i] = new Voice { Emitter = emitter };
                    }
                    voice._schedule = new SpectatorAudioSchedule(unchecked(Environment.TickCount ^ crowd.GetInstanceID()));
                    voice._callsReady = true;
                }
                if (planner != null)
                {
                    voice._rset = rset; voice._rpcm = rpcm; voice._rclips = new AudioClip[rpcm.Length];
                    voice._rfamilies = rset.clips.Select(c => CrowdReactionPool.FamilyOf(c.name)).ToArray();
                    voice._packed = new float[positions.Length * 3];
                    for (int i = 0; i < positions.Length; i++)
                    { voice._packed[3 * i] = positions[i].x; voice._packed[3 * i + 1] = positions[i].y; voice._packed[3 * i + 2] = positions[i].z; }
                    voice._planner = planner;
                    voice._speakers = new CrowdReactionSpeakers(unchecked(Environment.TickCount ^ crowd.GetInstanceID() ^ 0x5bd1e995));
                    voice._rvoices = new Voice[CrowdReactionPlanner.MaxVoices];
                    for (int i = 0; i < voice._rvoices.Length; i++)
                    {
                        var emitter = new GameObject("SpectatorReactionEmitter_" + i);
                        emitter.transform.SetParent(root.transform, false);
                        voice._rvoices[i] = new Voice { Emitter = emitter };
                    }
                    voice._reactionsReady = true;
                }
                AudioSettings.OnAudioConfigurationChanged += voice.AudioChanged;
                voice._ready = true;
                Current = voice;
                if (voice._callsReady)
                    Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: {pcm.Length} participating clips, {positions.Length} positions, up to {voice._voices.Length} simultaneous voices");
                if (voice._reactionsReady)
                {
                    voice.PublishKinds(ReactionsOn);
                    Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: {rpcm.Length} reaction clips, kinds mask {planner.KindsMask}, up to {voice._rvoices.Length} simultaneous reaction voices");
                }
            }
            catch (Exception e)
            {
                if (root != null) Destroy(root);
                Plugin.Log.LogWarning($"{Tag} spectator voices are unavailable ({e.GetType().Name}: {e.Message}); the crowd remains visible");
            }
        }

        void AudioChanged(bool deviceChanged)
        {
            _reset = true;
            if (DeveloperOptions.Enabled) Plugin.Log.LogDebug($"{Tag} {MapKey}: audio configuration changed (device changed={deviceChanged}); voices stop and clips are rebuilt");
        }

        void Update()
        {
            if (!_ready) return;
            try
            {
                if (_fading.Count > 0) ReleaseFading(false);
                if (_reset)
                {
                    _reset = false; AudioResets++;
                    if (NoiseProbe.Armed) NoiseProbe.Note(NoiseEv.Reset, "spectator");
                    StopAllVoices(NoiseStopReason.Reset); ClearClips(); _schedule?.Stop();
                    // The reaction voices and clips go too; the request queue and the planner's pending requests stay (an entry older than 2 s is dropped on its own).
                    if (_reactionsReady) { StopReactionVoices(NoiseStopReason.Reset); ClearReactionClips(); }
                }
                // The reactions run before the calls' living-player guard below: they have their own gate and never ask whether the local player is alive.
                if (_reactionsReady) UpdateReactions();
                if (!_callsReady) return;
                var player = GamePlayerOwner.MyPlayer;
                if (NoiseProbe.Armed) ProbeLife(player);
                if (!CallsEnabled || !Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started ||
                    player == null || player.HealthController == null || !player.HealthController.IsAlive)
                {
                    StopAllVoices(NoiseProbe.Armed ? ProbeReason() : NoiseStopReason.StopAll); _schedule.Stop(); NextCallAt = -1; return;
                }
                float now = Time.realtimeSinceStartup;
                _schedule.Start(now);
                if (!SpatialAudioSystem.Initialized || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated)
                { StopAllVoices(NoiseStopReason.NoServices); return; }
                PlayingVoices = 0;
                foreach (var voice in _voices)
                {
                    if (voice.Source == null) continue;
                    if (voice.System == null || AudioSettings.dspTime >= voice.EndsAt) { StopVoice(voice, voice.System == null ? NoiseStopReason.Slot : NoiseStopReason.Ended); continue; }
                    var source = voice.Source.source1;
                    bool on = source != null && source.isPlaying;
                    if (on) PlayingVoices++;
                    if (on && !voice.Confirmed) { ConfirmedPlaybackCount++; voice.Confirmed = true; }
                    if (voice.Frame >= 0 && NoiseProbe.Armed) ProbeOnset(voice, source);
                    if (voice != _lastVoice) continue;
                    OcclusionGain = voice.Source.OcclusionVolumeFactor;
                    SourceVolume = source != null ? source.volume : 0;
                    // Output readback is a developer probe; playback never depends on its peak.
                    if (DeveloperOptions.Enabled && on && now >= voice.NextOutputProbe)
                    {
                        voice.NextOutputProbe = now + .2f;
                        source.GetOutputData(_outputSamples, 0);
                        foreach (float sample in _outputSamples) LastOutputPeak = Mathf.Max(LastOutputPeak, Mathf.Abs(sample));
                    }
                }
                Playing = PlayingVoices > 0;
                PeakConcurrentVoices = Math.Max(PeakConcurrentVoices, PlayingVoices);
                NextCallAt = (float)_schedule.NextAt;
                if (!_schedule.Due(now, ActiveVoices)) return;
                _schedule.Retry(now);
                if (!MonoBehaviourSingleton<BetterAudio>.Exist(out var audio)) return;
                _busySpeakers.Clear(); _busyClips.Clear();
                foreach (var voice in _voices)
                {
                    if (voice.Source == null) continue;
                    _busySpeakers.Add(voice.Speaker);
                    // Different takes of the same recorded voice must not sound from two places together.
                    for (int i = 0; i < _families.Length; i++)
                        if (_families[i] == _families[voice.Clip]) _busyClips.Add(i);
                }
                for (int i = 0; i < _positions.Length; i++) _distances[i] = Vector3.Distance(player.Transform.position, _positions[i]);
                int speaker = _schedule.ChooseSpeaker(_distances, Rolloff * .8f, _busySpeakers);
                int pick = _schedule.ChooseClip(_clips.Length, _busyClips);
                if (speaker < 0 || pick < 0) return;
                var clip = Clip(pick);
                var slot = _voices.First(v => v.Source == null);
                try
                {
                    // Own each handle before setup; distinct emitter IDs keep propagation containers independent.
                    slot.Source = audio.GetSource(BetterAudio.AudioSourceGroupType.Environment);
                    if (slot.Source == null) return;
                    slot.Emitter.transform.position = _positions[speaker];
                    slot.Source.Position = slot.Emitter.transform.position;
                    slot.Source.SetMixerGroup(audio.SourceGroups[(int)BetterAudio.AudioSourceGroupType.Environment].MixerGroup);
                    slot.Source.SetPitch(1); slot.Source.Loop = false;
                    slot.Source.SetRolloff(Rolloff); slot.Source.SetSpatialBlend(1);
                    slot.Source.IncludeInOcclusionProcess(true); slot.Source.EnabledEQ(true);
                    slot.Source.Play(clip, null, 1, Gain, false, false);
                    if (SpectatorFade) slot.Source.VolumeFadeIn(FadeInSeconds);
                    slot.EndsAt = AudioSettings.dspTime + clip.length;
                    slot.System = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
                    slot.Container = slot.System.ProcessSourceOcclusion(slot.Emitter, slot.Source, EOcclusionTest.Combined);
                }
                catch (Exception e) { StopVoice(slot, NoiseStopReason.Slot); Warn(e); return; }
                slot.Confirmed = false; slot.Speaker = speaker; slot.Clip = pick;
                slot.NextOutputProbe = now + .15f;
                slot.StartedAt = now; slot.Distance = _distances[speaker]; slot.Frame = NoiseProbe.Armed ? 0 : -1;
                _lastVoice = slot; NativeOcclusion = true; LastOutputPeak = 0;
                LastClip = _manifest.clips[pick].name; LastSpeaker = speaker;
                LastPosition = slot.Emitter.transform.position; LastDistance = _distances[speaker]; LastStartedAt = now;
                PlaybackCount++;
                slot.Number = PlaybackCount;
                RefreshVoices();
                _schedule.Played(now, speaker, pick);
                NextCallAt = (float)_schedule.NextAt;
                if (NoiseProbe.Armed) NoiseProbe.Note(NoiseEv.SpecStart, NoiseText.ShortClip(_manifest.clips[pick].name), PlaybackCount, ActiveVoices, _distances[speaker], slot.Source.OcclusionVolumeFactor);
                // Individual calls are frequent now; counters remain available without filling normal debug logs.
                if (DeveloperOptions.Enabled)
                    Plugin.Log.LogDebug($"{Tag} {MapKey}: {LastClip}, fan {speaker} at {LastDistance:F1} m; voices={ActiveVoices}, next in {NextCallAt - now:F1} s");
            }
            catch (Exception e)
            {
                _schedule?.Retry(Time.realtimeSinceStartup);
                Warn(e);
            }
        }

        AudioClip Clip(int index)
        {
            if (_clips[index] != null) return _clips[index];
            var c = _manifest.clips[index];
            var clip = AudioClip.Create("ArenaMapPort spectator " + c.name, c.frames, 1, c.sample_rate, false);
            if (!clip.SetData(PcmWav.ToFloats(_pcm[index]), 0))
            { Destroy(clip); throw new InvalidDataException("spectator sample upload failed"); }
            return _clips[index] = clip;
        }

        void StopVoice(Voice voice, NoiseStopReason reason = NoiseStopReason.StopAll)
        {
            var source = voice.Source; var system = voice.System; int container = voice.Container;
            if (source != null && NoiseProbe.Armed) ProbeStop(voice, reason);
            voice.Source = null; voice.System = null; voice.Container = -1; voice.Frame = -1;
            try
            {
                if (system != null && SpatialAudioSystem.Initialized && container >= 0) system.UnregisterContainer(container);
            }
            catch (Exception e) { Warn(e); }
            finally
            {
                if (source != null) try { if (!BeginFade(source, reason)) source.Release(); } catch (Exception e) { Warn(e); }
            }
            RefreshVoices();
        }

        // NoiseProbeSpectatorFade: a call that is stopped before its clip ends fades out instead of being cut. Stop(fadeSec) is VolumeFadeOut(fadeSec, OnStop);
        // calling it directly says whether a fade really started (a call that is still fading in is cut). The source is released once the fade is over.
        bool BeginFade(BetterSource source, NoiseStopReason reason)
        {
            if (!SpectatorFade || reason == NoiseStopReason.Ended || reason == NoiseStopReason.Reset || reason == NoiseStopReason.Slot || reason == NoiseStopReason.Disable)
                return false;
            try
            {
                if (!source.VolumeFadeOut(FadeOutSeconds, source.OnStop)) return false;
                _fading.Add(new Fading { Source = source, ReleaseAt = Time.realtimeSinceStartup + FadeOutSeconds + .05f });
                return true;
            }
            catch (Exception e) { Warn(e); return false; }
        }

        void ReleaseFading(bool force)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = _fading.Count - 1; i >= 0; i--)
            {
                var item = _fading[i];
                if (!force && now < item.ReleaseAt) continue;
                _fading.RemoveAt(i);
                try { if (item.Source != null) item.Source.Release(); }
                catch (Exception e) { Warn(e); }
            }
        }

        // ---- NoiseProbe taps (only called while NoiseProbe.Armed; each reads state this class already owns)
        void ProbeLife(Player player)
        {
            bool alive = player != null && player.HealthController != null && player.HealthController.IsAlive;
            if (alive == _probeAlive) return;
            _probeAlive = alive;
            NoiseProbe.Note(NoiseEv.Life, alive ? "alive" : "dead", alive ? 1 : 0);
        }

        NoiseStopReason ProbeReason()
        {
            if (!CallsEnabled) return NoiseStopReason.Toggle;
            if (!Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started) return NoiseStopReason.NotStarted;
            return NoiseStopReason.Dead;
        }

        void ProbeStop(Voice voice, NoiseStopReason reason)
        {
            float played = (Time.realtimeSinceStartup - (float)voice.StartedAt) * 1000f;
            float left = (float)Math.Max(0.0, voice.EndsAt - AudioSettings.dspTime);
            NoiseProbe.Note(NoiseEv.SpecStop, NoiseText.ShortClip(_manifest.clips[voice.Clip].name), voice.Number, (int)reason, played, left);
        }

        // The same for a reaction voice (react-): its clip names come from the reaction set's table.
        void ProbeReactionStop(Voice voice, NoiseStopReason reason)
        {
            float played = (Time.realtimeSinceStartup - (float)voice.StartedAt) * 1000f;
            float left = (float)Math.Max(0.0, voice.EndsAt - AudioSettings.dspTime);
            NoiseProbe.Note(NoiseEv.ReactStop, NoiseText.ShortClip(_rset.clips[voice.Clip].name), voice.Number, (int)reason, played, left);
        }

        // Frames 1, 2, 3, 5, 8 and 13 after Play: what source.volume and the occlusion factor are while the real occlusion result is still arriving.
        void ProbeOnset(Voice voice, AudioSource source)
        {
            voice.Frame++;
            if (voice.Frame > 13) { voice.Frame = -1; return; }
            if (!NoiseText.IsOnsetFrame(voice.Frame)) return;
            NoiseProbe.Note(NoiseEv.SpecOnset, NoiseText.ShortClip(_manifest.clips[voice.Clip].name), voice.Number, voice.Frame,
                source != null ? source.volume : 0f, voice.Source.OcclusionVolumeFactor);
        }

        /// <summary>"fan9_02#74@11.0m occ0.64" per active call and "react:fan11_kill_cheer_01#3@12.5m occ0.31" per playing reaction voice (census and mark dump).</summary>
        internal string ProbeVoices()
        {
            if (_voices == null && _rvoices == null) return "";
            var sb = new System.Text.StringBuilder();
            if (_voices != null)
                foreach (var v in _voices)
                {
                    if (v == null || v.Source == null) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(NoiseText.ShortClip(_manifest.clips[v.Clip].name)).Append('#').Append(v.Number).Append('@')
                      .Append(v.Distance.ToString("0.0", NoiseText.Inv)).Append("m occ").Append(v.Source.OcclusionVolumeFactor.ToString("0.00", NoiseText.Inv));
                }
            if (_rvoices != null)
                foreach (var v in _rvoices)
                {
                    if (v == null || v.Source == null) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append("react:").Append(NoiseText.ShortClip(_rset.clips[v.Clip].name)).Append('#').Append(v.Number).Append('@')
                      .Append(v.Distance.ToString("0.0", NoiseText.Inv)).Append("m occ").Append(v.Source.OcclusionVolumeFactor.ToString("0.00", NoiseText.Inv));
                }
            return sb.ToString();
        }

        /// <summary>A call or a reaction voice is playing with a source volume that is not negligible (the orphan test of a burst).</summary>
        internal bool AnyAudible()
        {
            if (_voices != null)
                foreach (var v in _voices)
                {
                    if (v == null || v.Source == null) continue;
                    var source = v.Source.source1;
                    if (source != null && source.isPlaying && source.volume >= .01f) return true;
                }
            if (_rvoices != null)
                foreach (var v in _rvoices)
                {
                    if (v == null || v.Source == null) continue;
                    var source = v.Source.source1;
                    if (source != null && source.isPlaying && source.volume >= .01f) return true;
                }
            return false;
        }

        void RefreshVoices()
        {
            if (_voices == null) return;
            var active = _voices.Where(v => v != null && v.Source != null).ToArray();
            ActiveVoices = active.Length;
            ActiveSpeakerIds = string.Join(",", active.Select(v => v.Speaker));
            ActiveClipNames = string.Join(",", active.Select(v => _manifest.clips[v.Clip].name));
        }

        void StopAllVoices(NoiseStopReason reason = NoiseStopReason.StopAll)
        {
            if (_voices != null) foreach (var voice in _voices)
                if (voice != null && (voice.Source != null || voice.Container >= 0)) StopVoice(voice, reason);
            Playing = false; PlayingVoices = 0;
        }

        void Warn(Exception e)
        {
            if (_warned) return;
            _warned = true;
            Plugin.Log.LogWarning($"{Tag} a spectator call could not play ({e.GetType().Name}: {e.Message})");
        }
        void ClearClips()
        {
            if (_clips == null) return;
            for (int i = 0; i < _clips.Length; i++) { if (_clips[i] != null) Destroy(_clips[i]); _clips[i] = null; }
        }

        // ---- the reactions ------------------------------------------------------------------------------------------------------------

        // Publishes (or withdraws) the kinds this raid's crowd can play. A withdrawal stops the reaction voices and forgets what waits: the key went off, the component is disabled.
        void PublishKinds(bool on)
        {
            if (!_reactionsReady || on == _kindsPublished) return;
            _kindsPublished = on;
            SpectatorCrowd.Reactions.SetKinds(on ? _planner.KindsMask : 0);
            if (!on) { StopReactionVoices(NoiseStopReason.Toggle); _planner.Reset(); }
            if (DeveloperOptions.Enabled) Plugin.Log.LogDebug($"{Tag} {MapKey}: spectator reactions enabled={on}");
        }

        // The reaction section of Update: idle frames end at the first lines (a handful of field reads), nothing here allocates unless a reaction starts.
        void UpdateReactions()
        {
            try
            {
                var queue = SpectatorCrowd.Reactions;
                PublishKinds(ReactionsOn);
                if (!_kindsPublished) return;
                if (!Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started)
                {
                    // Loading, or the raid is ending: nothing plays, and what was asked for is not a reaction any more.
                    if (_reactionActive > 0 || _planner.Pending > 0 || queue.HasWaiting) DropReactions(queue, CrowdReactionDropReason.NotStarted, NoiseStopReason.NotStarted);
                    return;
                }
                bool busy = _reactionActive > 0 || _planner.Pending > 0 || queue.HasWaiting;
                if (!busy && !DeveloperOptions.Enabled) return;
                float now = Time.realtimeSinceStartup;
                if (busy)
                {
                    if (!SpatialAudioSystem.Initialized || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated)
                    {
                        // The services are not up (yet): voices stop, requests wait and are dropped by their own age.
                        StopReactionVoices(NoiseStopReason.NoServices);
                        return;
                    }
                    if (_reactionActive > 0) TickReactionVoices(now);
                    if (queue.HasWaiting)
                    {
                        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                        int n = queue.Drain(_requests, stamp);
                        if (n > 0) _planner.Offer(_requests, n, stamp, now);
                    }
                    if (_planner.Pending > 0) PlanReactions(now);
                }
                if (DeveloperOptions.Enabled)
                {
                    float scale = ReactionGapScaleNow;
                    if (scale != _appliedGapScale) { _appliedGapScale = scale; _planner.GapScale = scale; }
                    SpectatorReactionDemo.Tick(now);
                    LogReactionDrops();
                }
            }
            catch (Exception e)
            {
                WarnReaction(e);
                try { StopReactionVoices(NoiseStopReason.Slot); } catch (Exception) { /* the Warning above is the report */ }
            }
        }

        // Raid not Started: the voices stop, the queue and the planner's waiting requests are dropped (one Debug line in developer mode).
        void DropReactions(CrowdReactionQueue queue, int reason, NoiseStopReason stop)
        {
            StopReactionVoices(stop);
            int dropped = queue.HasWaiting ? queue.Drain(_requests, System.Diagnostics.Stopwatch.GetTimestamp()) : 0;
            if (_planner.Pending > 0) { dropped += _planner.Pending; _planner.Reset(); }
            if (dropped > 0 && DeveloperOptions.Enabled)
                Plugin.Log.LogDebug($"{Tag} {MapKey}: {dropped} reaction request(s) dropped ({CrowdReactionDropReason.Name(reason)})");
        }

        // End of life of the reaction voices and the staggered starts. Called only while a reaction voice holds a slot.
        void TickReactionVoices(float now)
        {
            PlayingReactionVoices = 0;
            BetterAudio audio = null;
            bool haveAudio = false;
            double dsp = AudioSettings.dspTime;
            foreach (var voice in _rvoices)
            {
                if (voice.Source != null)
                {
                    if (voice.System == null || dsp >= voice.EndsAt) { StopReactionVoice(voice, voice.System == null ? NoiseStopReason.Slot : NoiseStopReason.Ended); continue; }
                    var source = voice.Source.source1;
                    if (source != null && source.isPlaying) PlayingReactionVoices++;
                }
                else if (voice.Pending && now >= voice.StartAt)
                {
                    if (!haveAudio) haveAudio = MonoBehaviourSingleton<BetterAudio>.Exist(out audio);
                    if (haveAudio && StartReactionVoice(voice, audio, now)) continue;
                    // A later voice of a reaction that is already playing: skipped and counted, the others go on.
                    ReactionSourceFailures++;
                    FreeReactionSlot(voice);
                    if (DeveloperOptions.Enabled) Plugin.Log.LogDebug($"{Tag} {MapKey}: a reaction voice was skipped ({CrowdReactionDropReason.Name(CrowdReactionDropReason.NoSource)})");
                }
            }
        }

        void PlanReactions(float now)
        {
            // Each pass starts or discards the one reaction that was due; the global rate (one start per 0.75 s) ends the loop after a start.
            for (int guard = 0; guard < 4; guard++)
            {
                int free = 0;
                foreach (var v in _rvoices) if (!v.Pending && v.Source == null) free++;
                CollectBusy();
                if (!_planner.TryPlan(now, free, _rBusyFamilies, out var plan)) return;
                StartReaction(plan, now);
            }
        }

        // The speakers and voice families that sound right now, calls and reactions together (a call and a reaction of one recorded voice must not come from two places).
        void CollectBusy()
        {
            _rBusySpeakers.Clear(); _rBusyFamilies.Clear();
            if (_voices != null)
                foreach (var v in _voices)
                {
                    if (v == null || v.Source == null) continue;
                    _rBusySpeakers.Add(v.Speaker);
                    _rBusyFamilies.Add(_families[v.Clip]);
                }
            foreach (var v in _rvoices)
            {
                if (!v.Pending && v.Source == null) continue;
                _rBusySpeakers.Add(v.Speaker);
                _rBusyFamilies.Add(_rfamilies[v.Clip]);
            }
        }

        // The listener of a reaction: the main camera (the player's eye, a spectator or replay camera), else the local player's body; none = no reaction.
        static bool TryListener(out Vector3 position)
        {
            var camera = Camera.main;
            if (camera != null) { position = camera.transform.position; return true; }
            var player = GamePlayerOwner.MyPlayer;
            if (player != null && player.Transform != null) { position = player.Transform.position; return true; }
            position = Vector3.zero;
            return false;
        }

        // Plays a planned reaction: stands from the calls' law, slots for every voice, the first voice at once and the others after their delays.
        void StartReaction(CrowdReactionPlan plan, float now)
        {
            if (!TryListener(out var listener)) { _planner.Discard(plan, CrowdReactionDropReason.NoListener); return; }
            if (!MonoBehaviourSingleton<BetterAudio>.Exist(out var audio)) { _planner.Discard(plan, CrowdReactionDropReason.NoSource); return; }
            CollectBusy();
            int found = _speakers.Choose(_packed, listener.x, listener.y, listener.z, Rolloff * .8f, plan.Voices, _rBusySpeakers, _chosen);
            if (found < 1) { _planner.Discard(plan, CrowdReactionDropReason.NoSource); return; }
            int reserved = 0;
            for (int i = 0; i < found; i++)
            {
                Voice slot = null;
                foreach (var v in _rvoices) if (!v.Pending && v.Source == null) { slot = v; break; }
                if (slot == null) break;      // not reachable: the plan was made for the free slots
                slot.Pending = true; _reactionActive++;
                slot.Speaker = _chosen[i]; slot.Clip = plan.Clips[i]; slot.Kind = plan.Kind;
                slot.StartAt = now + (float)plan.Delays[i];
                slot.Distance = Vector3.Distance(listener, _positions[slot.Speaker]);
                _planSlots[reserved++] = slot;
            }
            if (reserved == 0) { _planner.Discard(plan, CrowdReactionDropReason.NoSlot); return; }
            // The first voice decides: without a source the reaction is given up and no cooldown starts.
            if (!StartReactionVoice(_planSlots[0], audio, now))
            {
                for (int i = 1; i < reserved; i++) FreeReactionSlot(_planSlots[i]);
                Array.Clear(_planSlots, 0, _planSlots.Length);
                ReactionSourceFailures++;
                _planner.Discard(plan, CrowdReactionDropReason.NoSource);
                return;
            }
            _planner.Started(plan, now, reserved);      // only the voices that sound enter the no-repeat history and the voice count
            ReactionCount++;
            LastReaction = CrowdReactionKind.Name(plan.Kind);
            // The bed swells with the reaction and the random calls stay quiet for its length plus the model's gap.
            float amplitude = Mathf.Min(1f, plan.Strength * ReactionSwellNow);
            if (amplitude > 0f && SpectatorCrowdBed.Current != null) SpectatorCrowd.State.RequestSwell(amplitude);
            double hold = plan.DurationOf(reserved) + plan.HoldGapSeconds;
            if (_callsReady) _schedule.Hold(now, hold);
            if (DeveloperOptions.Enabled) LogReaction(plan, reserved, hold);
            Array.Clear(_planSlots, 0, _planSlots.Length);
        }

        AudioClip ReactionAudioClip(int index)
        {
            if (_rclips[index] != null) return _rclips[index];
            var c = _rset.clips[index];
            var clip = AudioClip.Create("ArenaMapPort spectator reaction " + c.name, c.frames, 1, c.sample_rate, false);
            if (!clip.SetData(PcmWav.ToFloats(_rpcm[index]), 0))
            { Destroy(clip); throw new InvalidDataException("spectator reaction sample upload failed"); }
            return _rclips[index] = clip;
        }

        // The calls' playback path for one reaction voice. A false return has already freed the slot.
        bool StartReactionVoice(Voice slot, BetterAudio audio, float now)
        {
            try
            {
                var clip = ReactionAudioClip(slot.Clip);
                slot.Source = audio.GetSource(BetterAudio.AudioSourceGroupType.Environment);
                if (slot.Source == null) { FreeReactionSlot(slot); return false; }
                slot.Emitter.transform.position = _positions[slot.Speaker];
                slot.Source.Position = slot.Emitter.transform.position;
                slot.Source.SetMixerGroup(audio.SourceGroups[(int)BetterAudio.AudioSourceGroupType.Environment].MixerGroup);
                slot.Source.SetPitch(1); slot.Source.Loop = false;
                slot.Source.SetRolloff(Rolloff); slot.Source.SetSpatialBlend(1);
                slot.Source.IncludeInOcclusionProcess(true); slot.Source.EnabledEQ(true);
                slot.Source.Play(clip, null, 1, ReactionVolumeNow, false, false);
                slot.EndsAt = AudioSettings.dspTime + clip.length;
                slot.System = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
                slot.Container = slot.System.ProcessSourceOcclusion(slot.Emitter, slot.Source, EOcclusionTest.Combined);
            }
            catch (Exception e) { StopReactionVoice(slot, NoiseStopReason.Slot); WarnReaction(e); return false; }
            slot.Pending = false;
            slot.StartedAt = now;
            slot.Number = ++ReactionVoiceCount;
            if (NoiseProbe.Armed) NoiseProbe.Note(NoiseEv.ReactStart, NoiseText.ShortClip(_rset.clips[slot.Clip].name), slot.Number, slot.Kind, slot.Distance, slot.Source.OcclusionVolumeFactor);
            return true;
        }

        // A slot goes back to the free ones (waiting or playing, either way it held the count).
        void FreeReactionSlot(Voice voice)
        {
            bool held = voice.Pending || voice.Source != null;
            voice.Pending = false; voice.Source = null; voice.System = null; voice.Container = -1;
            if (held && _reactionActive > 0) _reactionActive--;
        }

        void StopReactionVoice(Voice voice, NoiseStopReason reason)
        {
            var source = voice.Source; var system = voice.System; int container = voice.Container;
            if (source != null && NoiseProbe.Armed) ProbeReactionStop(voice, reason);
            FreeReactionSlot(voice);
            try
            {
                if (system != null && SpatialAudioSystem.Initialized && container >= 0) system.UnregisterContainer(container);
            }
            catch (Exception e) { WarnReaction(e); }
            finally
            {
                if (source != null) try { source.Release(); } catch (Exception e) { WarnReaction(e); }
            }
        }

        void StopReactionVoices(NoiseStopReason reason)
        {
            if (_rvoices == null || _reactionActive == 0) { PlayingReactionVoices = 0; return; }
            foreach (var voice in _rvoices)
                if (voice != null && (voice.Pending || voice.Source != null || voice.Container >= 0)) StopReactionVoice(voice, reason);
            PlayingReactionVoices = 0;
        }

        void ClearReactionClips()
        {
            if (_rclips == null) return;
            for (int i = 0; i < _rclips.Length; i++) { if (_rclips[i] != null) Destroy(_rclips[i]); _rclips[i] = null; }
        }

        void WarnReaction(Exception e)
        {
            if (_reactionWarned) return;
            _reactionWarned = true;
            Plugin.Log.LogWarning($"{Tag} a spectator reaction could not play ({e.GetType().Name}: {e.Message})");
        }

        // Developer mode only: one line per played reaction ("reaction Approve strength=0.60, 2 voice(s) [...]") and one per dropped request.
        void LogReaction(CrowdReactionPlan plan, int voices, double hold)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Tag).Append(' ').Append(MapKey).Append(": reaction ").Append(CrowdReactionKind.Name(plan.Kind)).Append(plan.Directed ? " directed" : "")
              .Append(" strength=").Append(plan.Strength.ToString("0.00", NoiseText.Inv)).Append(", ").Append(voices).Append(" voice(s)");
            if (voices < plan.Requested) sb.Append(" of ").Append(plan.Requested);
            if (plan.Level > 0) sb.Append(", rules relaxed to level ").Append(plan.Level).Append(" (").Append(plan.RelaxedText).Append(')');
            sb.Append(" [");
            for (int i = 0; i < voices; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(_rset.clips[plan.Clips[i]].name).Append(" fan ").Append(_planSlots[i] != null ? _planSlots[i].Speaker : -1).Append(" at ")
                  .Append((_planSlots[i] != null ? _planSlots[i].Distance : 0f).ToString("0.0", NoiseText.Inv)).Append(" m +").Append(plan.Delays[i].ToString("0.00", NoiseText.Inv)).Append(" s");
            }
            sb.Append("], calls held ").Append(hold.ToString("0.0", NoiseText.Inv)).Append(" s");
            Plugin.Log.LogDebug(sb.ToString());
        }

        void LogReactionDrops()
        {
            while (_planner.TryTakeDrop(out var drop))
                Plugin.Log.LogDebug($"{Tag} {MapKey}: reaction {CrowdReactionKind.Name(drop.Kind)} strength={drop.Strength.ToString("0.00", NoiseText.Inv)} dropped ({CrowdReactionDropReason.Name(drop.Reason)}" +
                    (drop.Blocker != 0 ? $", waited for {CrowdReactionDropReason.Name(drop.Blocker)}" : "") + ")");
        }

        void OnDisable()
        {
            StopAllVoices(NoiseStopReason.Disable); ReleaseFading(true); _schedule?.Stop();
            if (_reactionsReady)
            {
                StopReactionVoices(NoiseStopReason.Disable);
                // A disabled component cannot play: the kinds are withdrawn until it runs again (Update publishes them anew).
                if (_kindsPublished && Current == this) { _kindsPublished = false; SpectatorCrowd.Reactions.SetKinds(0); }
            }
        }

        void OnDestroy()
        {
            _ready = false;
            bool mine = Current == this;
            if (mine) Current = null;
            AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
            StopAllVoices(NoiseStopReason.Disable); ReleaseFading(true); ClearClips(); _pcm = null;
            if (_reactionsReady)
            {
                _reactionsReady = false;
                StopReactionVoices(NoiseStopReason.Disable); ClearReactionClips(); _rpcm = null;
                if (mine)
                {
                    SpectatorReactionDemo.Stop("the crowd is removed");
                    SpectatorCrowd.Reactions.Reset();
                }
            }
            Plugin.Log.LogDebug($"{Tag} {MapKey}: all voices, propagation and clips removed");
        }
    }
}
