using System;
using System.IO;
using System.Security.Cryptography;
using Audio.SpatialSystem;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>One occasional voice for the whole crowd. Owned source, native mixer and room propagation;
    /// no player/AI phrase events, no match announcements, no changes to shared audio presets.</summary>
    internal sealed class SpectatorAudio : MonoBehaviour
    {
        const string Tag = "[SpectatorAudio]";
        const float Gain = .5f;
        Vector3[] _positions;
        float[] _distances;
        SpectatorAudioManifest _manifest;
        short[][] _pcm;
        AudioClip[] _clips;
        SpectatorAudioSchedule _schedule;
        BetterSource _source;
        SpatialAudioSystem _system;
        int _container = -1;
        float _endsAt;
        bool _ready, _reset, _warned;
        readonly float[] _outputSamples = new float[256];
        float _nextOutputProbe;
        public string MapKey, LastClip;
        public int PlaybackCount, ConfirmedPlaybackCount, LastSpeaker = -1, AudioResets;
        public float LastDistance, Rolloff, LastStartedAt, NextCallAt;
        public Vector3 LastPosition;
        public bool Playing, NativeOcclusion;
        public float OcclusionGain, SourceVolume, LastOutputPeak;

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
                voice.Rolloff = crowd.MapKey == "arena_bowl" ? 150 : 90;
                voice._schedule = new SpectatorAudioSchedule(unchecked(Environment.TickCount ^ crowd.GetInstanceID()));
                AudioSettings.OnAudioConfigurationChanged += voice.AudioChanged;
                voice._ready = true;
                Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: {pcm.Length} participating clips, {positions.Length} emitters, one voice at a time");
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
                    StopVoice(); ClearClips(); _schedule.Stop();
                }
                var player = GamePlayerOwner.MyPlayer;
                if (!Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started ||
                    player == null || player.HealthController == null || !player.HealthController.IsAlive)
                {
                    StopVoice(); _schedule.Stop(); NextCallAt = -1; return;
                }
                float now = Time.realtimeSinceStartup;
                _schedule.Start(now);
                if (_source != null)
                {
                    if (!SpatialAudioSystem.Initialized || _system == null) { StopVoice(); return; }
                    Playing = _source.source1 != null && _source.source1.isPlaying;
                    OcclusionGain = _source.OcclusionVolumeFactor;
                    SourceVolume = _source.source1 != null ? _source.source1.volume : 0;
                    if (Playing && !_confirmed) { ConfirmedPlaybackCount++; _confirmed = true; }
                    if (Playing && now >= _nextOutputProbe)
                    {
                        _nextOutputProbe = now + .2f;
                        _source.source1.GetOutputData(_outputSamples, 0);
                        foreach (float sample in _outputSamples) LastOutputPeak = Mathf.Max(LastOutputPeak, Mathf.Abs(sample));
                    }
                    if (now >= _endsAt) StopVoice();
                }
                NextCallAt = (float)_schedule.NextAt;
                if (_source != null || !_schedule.Due(now)) return;
                _schedule.Retry(now);
                if (!MonoBehaviourSingleton<BetterAudio>.Exist(out var audio)) return;
                // Wait for the owned room graph rather than start a voice through ceilings/walls.
                if (!SpatialAudioSystem.Initialized || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return;
                for (int i = 0; i < _positions.Length; i++) _distances[i] = Vector3.Distance(player.Transform.position, _positions[i]);
                int speaker = _schedule.ChooseSpeaker(_distances, Rolloff * .8f);
                if (speaker < 0) return;
                int pick = _schedule.ChooseClip(_clips.Length);
                var clip = Clip(pick);
                // Keep the handle before any setup that can throw. No native automatic-release queue.
                _source = audio.GetSource(BetterAudio.AudioSourceGroupType.Environment);
                if (_source == null) return;
                transform.position = _positions[speaker];
                _source.Position = transform.position;
                _source.SetMixerGroup(audio.SourceGroups[(int)BetterAudio.AudioSourceGroupType.Environment].MixerGroup);
                _source.SetPitch(1); _source.Loop = false;
                _source.SetRolloff(Rolloff); _source.SetSpatialBlend(1);
                _source.IncludeInOcclusionProcess(true); _source.EnabledEQ(true);
                _source.Play(clip, null, 1, Gain, false, false);
                NativeOcclusion = SpatialAudioSystem.Initialized && MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated;
                if (NativeOcclusion)
                {
                    _system = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
                    _container = _system.ProcessSourceOcclusion(gameObject, _source, EOcclusionTest.Combined);
                }
                _endsAt = now + clip.length;
                LastOutputPeak = 0; _nextOutputProbe = now + .15f;
                _confirmed = false;
                LastClip = _manifest.clips[pick].name; LastSpeaker = speaker;
                LastPosition = transform.position; LastDistance = _distances[speaker]; LastStartedAt = now;
                PlaybackCount++;
                _schedule.Played(now, clip.length, speaker, pick);
                NextCallAt = (float)_schedule.NextAt;
                Plugin.Log.LogDebug($"{Tag} {MapKey}: {LastClip}, fan {speaker} at {LastDistance:F1} m; native occlusion={NativeOcclusion}, next in {NextCallAt - now:F1} s");
            }
            catch (Exception e)
            {
                StopVoice();
                _schedule.Played(Time.realtimeSinceStartup, 0, LastSpeaker, -1);
                Warn(e);
            }
        }
        bool _confirmed;

        AudioClip Clip(int index)
        {
            if (_clips[index] != null) return _clips[index];
            var c = _manifest.clips[index];
            var clip = AudioClip.Create("ArenaMapPort spectator " + c.name, c.frames, 1, c.sample_rate, false);
            if (!clip.SetData(PcmWav.ToFloats(_pcm[index]), 0))
            { Destroy(clip); throw new InvalidDataException("spectator sample upload failed"); }
            return _clips[index] = clip;
        }

        void StopVoice()
        {
            var source = _source; var system = _system; int container = _container;
            _source = null; _system = null; _container = -1; Playing = false;
            try
            {
                if (system != null && SpatialAudioSystem.Initialized && container >= 0) system.UnregisterContainer(container);
            }
            catch (Exception e) { Warn(e); }
            finally
            {
                if (source != null) try { source.Release(); } catch (Exception e) { Warn(e); }
            }
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
        void OnDisable() { StopVoice(); _schedule?.Stop(); }
        void OnDestroy()
        {
            _ready = false;
            AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
            StopVoice(); ClearClips(); _pcm = null;
            Plugin.Log.LogDebug($"{Tag} {MapKey}: source, propagation and clips removed");
        }
    }
}
