using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>Bounded overlapping crowd calls. Owned sources, native mixer and room propagation;
    /// no player/AI phrase events, no match announcements, no changes to shared audio presets.</summary>
    internal sealed class SpectatorAudio : MonoBehaviour
    {
        const string Tag = "[SpectatorAudio]";
        const float Gain = .5f;
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
        public string MapKey, LastClip;
        public int PlaybackCount, ConfirmedPlaybackCount, LastSpeaker = -1, AudioResets;
        public float LastDistance, Rolloff, LastStartedAt, NextCallAt;
        public Vector3 LastPosition;
        public bool Playing, NativeOcclusion;
        public float OcclusionGain, SourceVolume, LastOutputPeak;
        public int ActiveVoices, PlayingVoices, PeakConcurrentVoices;
        public string ActiveSpeakerIds, ActiveClipNames;
        public bool CallsEnabled => !DeveloperOptions.Enabled || _callsEnabled == null || _callsEnabled.Value;

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
            public float NextOutputProbe;
            public bool Confirmed;
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
                for (int i = 0; i < pcm.Length; i++)
                {
                    var c = manifest.clips[i];
                    var bytes = File.ReadAllBytes(Path.Combine(folder, c.file));
                    using (var sha = SHA256.Create())
                        if (bytes.Length != c.bytes || BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != c.sha256)
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
                Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: {pcm.Length} participating clips, {positions.Length} positions, up to {voice._voices.Length} simultaneous voices");
            }
            catch (Exception e)
            {
                if (root != null) Destroy(root);
                Plugin.Log.LogWarning($"{Tag} spectator calls are unavailable ({e.GetType().Name}: {e.Message}); the crowd remains visible");
            }
        }

        void AudioChanged(bool deviceChanged) { _reset = true; }

        void Update()
        {
            if (!_ready) return;
            try
            {
                if (_reset)
                {
                    _reset = false; AudioResets++;
                    StopAllVoices(); ClearClips(); _schedule.Stop();
                }
                var player = GamePlayerOwner.MyPlayer;
                if (!CallsEnabled || !Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started ||
                    player == null || player.HealthController == null || !player.HealthController.IsAlive)
                {
                    StopAllVoices(); _schedule.Stop(); NextCallAt = -1; return;
                }
                float now = Time.realtimeSinceStartup;
                _schedule.Start(now);
                if (!SpatialAudioSystem.Initialized || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated)
                { StopAllVoices(); return; }
                PlayingVoices = 0;
                foreach (var voice in _voices)
                {
                    if (voice.Source == null) continue;
                    if (voice.System == null || AudioSettings.dspTime >= voice.EndsAt) { StopVoice(voice); continue; }
                    var source = voice.Source.source1;
                    bool on = source != null && source.isPlaying;
                    if (on) PlayingVoices++;
                    if (on && !voice.Confirmed) { ConfirmedPlaybackCount++; voice.Confirmed = true; }
                    if (voice != _lastVoice) continue;
                    OcclusionGain = voice.Source.OcclusionVolumeFactor;
                    SourceVolume = source != null ? source.volume : 0;
                    if (on && now >= voice.NextOutputProbe)
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
                    slot.EndsAt = AudioSettings.dspTime + clip.length;
                    slot.System = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
                    slot.Container = slot.System.ProcessSourceOcclusion(slot.Emitter, slot.Source, EOcclusionTest.Combined);
                }
                catch (Exception e) { StopVoice(slot); Warn(e); return; }
                slot.Confirmed = false; slot.Speaker = speaker; slot.Clip = pick;
                slot.NextOutputProbe = now + .15f;
                _lastVoice = slot; NativeOcclusion = true; LastOutputPeak = 0;
                LastClip = _manifest.clips[pick].name; LastSpeaker = speaker;
                LastPosition = slot.Emitter.transform.position; LastDistance = _distances[speaker]; LastStartedAt = now;
                PlaybackCount++;
                RefreshVoices();
                _schedule.Played(now, speaker, pick);
                NextCallAt = (float)_schedule.NextAt;
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

        void StopVoice(Voice voice)
        {
            var source = voice.Source; var system = voice.System; int container = voice.Container;
            voice.Source = null; voice.System = null; voice.Container = -1;
            try
            {
                if (system != null && SpatialAudioSystem.Initialized && container >= 0) system.UnregisterContainer(container);
            }
            catch (Exception e) { Warn(e); }
            finally
            {
                if (source != null) try { source.Release(); } catch (Exception e) { Warn(e); }
            }
            RefreshVoices();
        }

        void RefreshVoices()
        {
            if (_voices == null) return;
            var active = _voices.Where(v => v != null && v.Source != null).ToArray();
            ActiveVoices = active.Length;
            ActiveSpeakerIds = string.Join(",", active.Select(v => v.Speaker));
            ActiveClipNames = string.Join(",", active.Select(v => _manifest.clips[v.Clip].name));
        }

        void StopAllVoices()
        {
            if (_voices != null) foreach (var voice in _voices)
                if (voice != null && (voice.Source != null || voice.Container >= 0)) StopVoice(voice);
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
        void OnDisable() { StopAllVoices(); _schedule?.Stop(); }
        void OnDestroy()
        {
            _ready = false;
            AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
            StopAllVoices(); ClearClips(); _pcm = null;
            Plugin.Log.LogDebug($"{Tag} {MapKey}: all voices, propagation and clips removed");
        }
    }
}
