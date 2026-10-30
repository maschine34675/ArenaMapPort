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
    /// <summary>Bounded overlapping crowd calls. Owned sources, native mixer and room propagation;
    /// no player/AI phrase events, no match announcements, no changes to shared audio presets.
    /// Cadence (SpectatorAudioSchedule, since 2026-10-30): the first call 3 to 8 s after the raid starts, then one every 4.0 to 8.0 s, at most two voices at a time. The
    /// continuous murmur between the calls is not made here but by SpectatorCrowdBed.cs, which the crowd creates right after this class from the same positions.
    /// The NoiseProbe developer diagnostic (NoiseProbe.cs) records starts, stops with their reason and an onset trace of every call through
    /// one-line taps guarded by NoiseProbe.Armed; its A/B key NoiseProbeSpectatorFade adds a 150 ms fade-in at the start and an 80 ms
    /// fade-out when a call is stopped early (a source that is fading out is released afterwards). Both are off by default.</summary>
    internal sealed class SpectatorAudio : MonoBehaviour
    {
        const string Tag = "[SpectatorAudio]";
        const float Gain = .5f;
        const float FadeInSeconds = .15f, FadeOutSeconds = .08f;
        static ConfigEntry<bool> _callsEnabled;
        Vector3[] _positions;
        float[] _distances;
        SpectatorAudioManifest _manifest;
        short[][] _pcm;
        AudioClip[] _clips;
        SpectatorAudioSchedule _schedule;
        Voice[] _voices;
        string[] _families;
        readonly HashSet<int> _busySpeakers = new HashSet<int>();
        readonly HashSet<int> _busyClips = new HashSet<int>();
        Voice _lastVoice;
        bool _ready, _reset, _warned;
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
        }

        sealed class Fading
        {
            public BetterSource Source;
            public float ReleaseAt;
        }

        internal static void Create(ArenaSpectators crowd, Vector3[] positions)
        {
            GameObject root = null;
            try
            {
                var folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "spectator-audio");
                var manifest = JsonConvert.DeserializeObject<SpectatorAudioManifest>(File.ReadAllText(Path.Combine(folder, "spectator-audio.json")));
                if (manifest == null) throw new InvalidDataException("missing spectator audio manifest");
                manifest.Validate();
                var pcm = new short[manifest.clips.Length][];
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
                root = new GameObject("ArenaMapPort_SpectatorVoice_" + crowd.MapKey);
                root.transform.SetParent(crowd.transform, false);
                var voice = root.AddComponent<SpectatorAudio>();
                voice.MapKey = crowd.MapKey;
                voice._positions = positions;
                voice._distances = new float[positions.Length];
                voice._manifest = manifest; voice._pcm = pcm; voice._clips = new AudioClip[pcm.Length];
                voice._families = manifest.clips.Select(c => c.name.Substring(0, c.name.IndexOf("_participating", StringComparison.Ordinal))).ToArray();
                voice._voices = new Voice[SpectatorAudioSchedule.MaxConcurrent];
                for (int i = 0; i < voice._voices.Length; i++)
                {
                    var emitter = new GameObject("SpectatorVoiceEmitter_" + i);
                    emitter.transform.SetParent(root.transform, false);
                    voice._voices[i] = new Voice { Emitter = emitter };
                }
                voice.Rolloff = crowd.MapKey == "arena_bowl" ? 150 : 90;
                voice._schedule = new SpectatorAudioSchedule(unchecked(Environment.TickCount ^ crowd.GetInstanceID()));
                AudioSettings.OnAudioConfigurationChanged += voice.AudioChanged;
                voice._ready = true;
                Current = voice;
                Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: {pcm.Length} participating clips, {positions.Length} positions, up to {voice._voices.Length} simultaneous voices");
            }
            catch (Exception e)
            {
                if (root != null) Destroy(root);
                Plugin.Log.LogWarning($"{Tag} spectator calls are unavailable ({e.GetType().Name}: {e.Message}); the crowd remains visible");
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
                    StopAllVoices(NoiseStopReason.Reset); ClearClips(); _schedule.Stop();
                }
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
                _schedule.Retry(Time.realtimeSinceStartup);
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

        // Frames 1, 2, 3, 5, 8 and 13 after Play: what source.volume and the occlusion factor are while the real occlusion result is still arriving.
        void ProbeOnset(Voice voice, AudioSource source)
        {
            voice.Frame++;
            if (voice.Frame > 13) { voice.Frame = -1; return; }
            if (!NoiseText.IsOnsetFrame(voice.Frame)) return;
            NoiseProbe.Note(NoiseEv.SpecOnset, NoiseText.ShortClip(_manifest.clips[voice.Clip].name), voice.Number, voice.Frame,
                source != null ? source.volume : 0f, voice.Source.OcclusionVolumeFactor);
        }

        /// <summary>"fan9_02#74@11.0m occ0.64" per active call (census and mark dump).</summary>
        internal string ProbeVoices()
        {
            if (_voices == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var v in _voices)
            {
                if (v == null || v.Source == null) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(NoiseText.ShortClip(_manifest.clips[v.Clip].name)).Append('#').Append(v.Number).Append('@')
                  .Append(v.Distance.ToString("0.0", NoiseText.Inv)).Append("m occ").Append(v.Source.OcclusionVolumeFactor.ToString("0.00", NoiseText.Inv));
            }
            return sb.ToString();
        }

        /// <summary>A call is playing with a source volume that is not negligible (the orphan test of a burst).</summary>
        internal bool AnyAudible()
        {
            if (_voices == null) return false;
            foreach (var v in _voices)
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
        void OnDisable() { StopAllVoices(NoiseStopReason.Disable); ReleaseFading(true); _schedule?.Stop(); }
        void OnDestroy()
        {
            _ready = false;
            if (Current == this) Current = null;
            AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
            StopAllVoices(NoiseStopReason.Disable); ReleaseFading(true); ClearClips(); _pcm = null;
            Plugin.Log.LogDebug($"{Tag} {MapKey}: all voices, propagation and clips removed");
        }
    }
}
