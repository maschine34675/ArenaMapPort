using System;
using System.IO;
using ArenaMapPort.Crypto;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Audio;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What Arena does: its only crowd ambience clip, <c>arena_fans_ambient_postmatch</c>, is the results-screen bed; none of the four levels carries a crowd-ambience
    /// emitter, so the stands are silent between the fans' calls (the Participating clips of <see cref="SpectatorAudio"/>). What the port lacks: a continuous crowd.
    /// What this does (the port's own addition, user decision 2026-10-30): once the original spectator crowd exists, it groups the fans into stands
    /// (<see cref="SpectatorCrowdBedModel.Plan"/>, deterministic k-means over x/z, two to six stands) and plays the bed as a looping murmur from the centre of every
    /// stand: one owned GameObject and AudioSource per stand, the clip decoded from the package's mono WAV (loop seam crossfaded at extraction), a start offset of
    /// i/k of the loop and a pitch of 0.98 to 1.02 so the stands do not murmur in step, spatial blend 1, spread 70, no Doppler, priority 200, the Environment mixer
    /// group the calls use, and a Custom distance curve that falls to silence at the stand's maximum distance (Unity's Logarithmic mode never does). No occlusion, no
    /// scene search, no per-frame work beyond the excitement smoothing and a few field reads.
    ///
    /// The sources exist while the raid has started and the bed is on; the audio reset of EFT (<c>OnAudioConfigurationChanged</c>) rebuilds clip and sources, the
    /// crowd's removal removes the bed (it is a child of the crowd root). A mode plugin lifts the volume with <see cref="SpectatorCrowd.SetExcitement"/>. The keys are
    /// developer keys like <c>Audio diagnostics/SpectatorCalls</c> (honoured only with DeveloperMode; the defaults apply otherwise): <c>Spectators/CrowdBed</c>,
    /// <c>CrowdBedVolume</c>, <c>CrowdBedReach</c>, all live. A package without a bed is not an error (one Debug line); a damaged bed is one Warning and the crowd
    /// stays. The NoiseProbe taps (<see cref="NoiseEv.Bed"/>) sit behind <c>NoiseProbe.Armed</c>. Host verification: docs/progress/spectator-audio.md "Crowd bed".
    /// </summary>
    internal sealed class SpectatorCrowdBed : MonoBehaviour
    {
        const string Tag = "[CrowdBed]";
        const float WatchSeconds = 2f;
        const int MaxRestarts = 8;
        static ConfigEntry<bool> _enabled;
        static ConfigEntry<float> _volume, _reach;
        static bool _installed;

        /// <summary>The bed of the running raid; null outside an owned raid or when the package has none.</summary>
        internal static SpectatorCrowdBed Current { get; private set; }

        sealed class Stand
        {
            public GameObject Go;
            public AudioSource Source;
            public string Label;
        }

        public string MapKey;

        /// <summary>Stand sources that are playing and how many stands there are; read only by the NoiseProbe census (at a burst or a mark), at most six reads.</summary>
        internal int PlayingStands
        {
            get
            {
                var stands = _stands;
                int n = 0;
                if (stands != null)
                    for (int i = 0; i < stands.Length; i++)
                        if (stands[i] != null && stands[i].Source != null && stands[i].Source.isPlaying) n++;
                return n;
            }
        }

        internal int StandCount => _plan != null ? _plan.Length : 0;

        BedPoint[] _points;
        BedStand[] _plan;
        short[] _pcm;
        int _frames, _rate;
        AudioClip _clip;
        Stand[] _stands;
        readonly CrowdExcitement _excitement = new CrowdExcitement();
        float _appliedVolume = -1f, _appliedReach, _nextWatch;
        int _restarts;
        bool _ready, _reset, _failed, _waitLogged, _gaveUp;

        static bool BedOn => !DeveloperOptions.Enabled || _enabled == null || _enabled.Value;
        static float VolumeNow => DeveloperOptions.Enabled && _volume != null ? SpectatorCrowdBedModel.ClampVolume(_volume.Value) : SpectatorCrowdBedModel.DefaultVolume;
        static float ReachNow => DeveloperOptions.Enabled && _reach != null ? SpectatorCrowdBedModel.ClampReach(_reach.Value) : SpectatorCrowdBedModel.DefaultReach;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            var s = Plugin.Instance.Settings;
            _enabled = s.Bind("Spectators", "CrowdBed", true,
                "Developer hearing comparison: play Arena's crowd murmur bed from every stand of the original spectator crowd. Live; off removes the stand sources, on " +
                "creates them again. Ignored outside DeveloperMode, where the bed is on.");
            _volume = s.Bind("Spectators", "CrowdBedVolume", SpectatorCrowdBedModel.DefaultVolume, new ConfigDescription(
                "Developer hearing comparison: level of the crowd murmur bed, 1 = the clip's own level. The excitement a mode plugin sets raises it by up to 60 percent on " +
                "top. Live. Ignored outside DeveloperMode.", new AcceptableValueRange<float>(0f, SpectatorCrowdBedModel.MaxVolume)));
            _reach = s.Bind("Spectators", "CrowdBedReach", SpectatorCrowdBedModel.DefaultReach, new ConfigDescription(
                "Developer hearing comparison: how far in metres beyond a stand's own radius its murmur carries before it is silent (the full volume reaches the radius, then it " +
                "falls off like 1/distance and tapers to nothing). Live. Ignored outside DeveloperMode.",
                new AcceptableValueRange<float>(SpectatorCrowdBedModel.MinReach, SpectatorCrowdBedModel.MaxReach)));
        }

        /// <summary>Called by the crowd right after its audio exists; never throws.</summary>
        internal static void Create(ArenaSpectators crowd, Vector3[] positions)
        {
            GameObject root = null;
            try
            {
                var folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "spectator-audio");
                var manifest = JsonConvert.DeserializeObject<SpectatorAudioManifest>(File.ReadAllText(Path.Combine(folder, "spectator-audio.json")));
                if (manifest == null || manifest.bed == null)
                {
                    Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: the spectator audio package has no crowd bed; none will play");
                    return;
                }
                manifest.ValidateBed();
                var bed = manifest.bed;
                var crypto = CryptoProviders.Select(null);
                var bytes = File.ReadAllBytes(Path.Combine(folder, bed.file));
                if (bytes.Length != bed.bytes || BitConverter.ToString(crypto.Sha256(bytes, 0, bytes.Length)).Replace("-", "").ToLowerInvariant() != bed.sha256)
                    throw new InvalidDataException("damaged crowd bed: " + bed.file);
                var wav = PcmWav.Read(bytes);
                if (wav.Channels != bed.channels || wav.SampleRate != bed.sample_rate || wav.Frames != bed.frames)
                    throw new InvalidDataException("unexpected crowd bed WAV format: " + bed.file);
                var points = new BedPoint[positions.Length];
                for (int i = 0; i < points.Length; i++) points[i] = new BedPoint(positions[i].x, positions[i].y, positions[i].z);
                float reach = ReachNow;
                var plan = SpectatorCrowdBedModel.Plan(points, reach);
                if (plan.Length == 0)
                {
                    Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: the crowd has no usable position; no bed");
                    return;
                }
                root = new GameObject("ArenaMapPort_SpectatorBed_" + crowd.MapKey);
                root.transform.SetParent(crowd.transform, false);
                var component = root.AddComponent<SpectatorCrowdBed>();
                component.MapKey = crowd.MapKey;
                component._points = points; component._plan = plan; component._appliedReach = reach;
                component._pcm = wav.Samples; component._frames = wav.Frames; component._rate = wav.SampleRate;
                AudioSettings.OnAudioConfigurationChanged += component.AudioChanged;
                component._ready = true;
                Current = component;
                // The clip is uploaded now, while the raid loads, so that the first frames of the raid only create the sources.
                if (BedOn) component.EnsureClip();
                SpectatorCrowd.State.Available = BedOn;
                Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: {SpectatorCrowdBedModel.Describe(plan)}, clip {wav.Frames / (float)wav.SampleRate:F1} s mono, " +
                    $"volume {VolumeNow:F2}, reach {reach:F0} m");
            }
            catch (Exception e)
            {
                if (root != null) Destroy(root);
                Plugin.Log.LogWarning($"{Tag} the crowd murmur is unavailable ({e.GetType().Name}: {e.Message}); the crowd remains");
            }
        }

        void AudioChanged(bool deviceChanged)
        {
            _reset = true;
            if (DeveloperOptions.Enabled) Plugin.Log.LogDebug($"{Tag} {MapKey}: audio configuration changed (device changed={deviceChanged}); the stand sources and the clip are rebuilt");
        }

        void Update()
        {
            if (!_ready) return;
            try
            {
                bool on = BedOn && !_failed;
                SpectatorCrowd.State.Available = on;
                if (_reset)
                {
                    _reset = false;
                    TearDown(true); // the bed-/bed+ events of the teardown and the rebuild show it; the one audio reset is counted by SpectatorAudio's own tap
                }
                bool started = Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started;
                if (!on || !started)
                {
                    // Switched off: the clip goes too. Not started (loading, or the raid is ending): the clip stays for the start.
                    if (_stands != null || (!on && _clip != null)) TearDown(!on);
                    return;
                }
                if (_stands == null && !Build()) return;
                float reach = ReachNow;
                if (reach != _appliedReach) ApplyReach(reach);
                _excitement.SetTarget(SpectatorCrowd.State.Excitement);
                _excitement.Step(Time.unscaledDeltaTime);
                float volume = SpectatorCrowdBedModel.SourceVolume(VolumeNow, _excitement.Factor);
                if (Mathf.Abs(volume - _appliedVolume) > 0.0005f) ApplyVolume(volume);
                if (Time.unscaledTime >= _nextWatch) Watch();
            }
            catch (Exception e)
            {
                _failed = true;
                try { TearDown(true); } catch (Exception) { /* the Warning below is the report */ }
                SpectatorCrowd.State.Available = false;
                Plugin.Log.LogWarning($"{Tag} the crowd murmur stopped ({e.GetType().Name}: {e.Message}); the crowd remains");
            }
        }

        // The Environment group the fans' calls play in (BetterAudio's own source group, as SpectatorAudio reads it). Null until BetterAudio exists.
        static AudioMixerGroup EnvironmentGroup()
        {
            if (!MonoBehaviourSingleton<BetterAudio>.Exist(out var audio)) return null;
            var groups = audio.SourceGroups;
            int index = (int)BetterAudio.AudioSourceGroupType.Environment;
            return groups != null && index < groups.Length && groups[index] != null ? groups[index].MixerGroup : null;
        }

        bool Build()
        {
            var group = EnvironmentGroup();
            if (group == null)
            {
                if (!_waitLogged) { _waitLogged = true; Plugin.Log.LogDebug($"{Tag} {MapKey}: waiting for BetterAudio and its Environment mixer group"); }
                return false;
            }
            EnsureClip();
            var clip = _clip;
            _excitement.SetTarget(SpectatorCrowd.State.Excitement);
            _excitement.Snap();
            float volume = SpectatorCrowdBedModel.SourceVolume(VolumeNow, _excitement.Factor);
            _stands = new Stand[_plan.Length];
            for (int i = 0; i < _stands.Length; i++)
            {
                var stand = _plan[i];
                var go = new GameObject("SpectatorBedStand_" + i);
                go.transform.SetParent(transform, false);
                go.transform.position = new Vector3(stand.X, stand.Y, stand.Z);
                var source = go.AddComponent<AudioSource>();
                _stands[i] = new Stand { Go = go, Source = source, Label = "stand" + i };
                source.playOnAwake = false;
                source.clip = clip;
                source.loop = true;
                source.volume = volume;
                source.pitch = stand.Pitch;
                source.spatialBlend = 1f;
                source.spread = 70f;
                source.dopplerLevel = 0f;
                source.priority = 200;
                source.outputAudioMixerGroup = group;
                Place(source, stand);
                source.timeSamples = Mathf.Clamp((int)(stand.StartFraction * _frames), 0, _frames - 1);
                source.Play();
                if (NoiseProbe.Armed) NoiseProbe.NoteAt(NoiseEv.Bed, go.transform.position, _stands[i].Label, 1, i, volume);
            }
            _appliedVolume = volume;
            _nextWatch = Time.unscaledTime + WatchSeconds;
            _waitLogged = false;
            return true;
        }

        // The decoded samples as an AudioClip of the fixed gain (see SpectatorCrowdBedModel.ClipGain); built once and kept until the bed is switched off, reset or removed.
        void EnsureClip()
        {
            if (_clip != null) return;
            var clip = AudioClip.Create("ArenaMapPort crowd bed", _frames, 1, _rate, false);
            if (!clip.SetData(SpectatorCrowdBedModel.ToClipFloats(_pcm), 0))
            {
                Destroy(clip);
                throw new InvalidDataException("crowd bed sample upload failed");
            }
            _clip = clip;
        }

        // Full volume to the stand's minimum distance, 1/d beyond it, silent at the maximum distance (see SpectatorCrowdBedModel.RolloffGain).
        static void Place(AudioSource source, BedStand stand)
        {
            SpectatorCrowdBedModel.RolloffKeys(stand.MinDistance, stand.MaxDistance, out var times, out var gains);
            var keys = new Keyframe[times.Length];
            for (int i = 0; i < keys.Length; i++) keys[i] = new Keyframe(times[i], gains[i]);
            var curve = new AnimationCurve(keys);
            // Keys 0 and 1 are the plateau at full gain and keep the flat tangents of a new Keyframe: smoothed tangents there overshoot gain 1 by 7 to 14 percent.
            for (int i = 2; i < keys.Length; i++) curve.SmoothTangents(i, 0f);
            source.rolloffMode = AudioRolloffMode.Custom;
            source.minDistance = stand.MinDistance;
            source.maxDistance = stand.MaxDistance;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
        }

        void ApplyReach(float reach)
        {
            _appliedReach = reach;
            var plan = SpectatorCrowdBedModel.Plan(_points, reach);
            if (plan.Length != _plan.Length) return;
            _plan = plan;
            if (_stands == null) return;
            for (int i = 0; i < _stands.Length; i++)
                if (_stands[i].Source != null) Place(_stands[i].Source, _plan[i]);
            if (DeveloperOptions.Enabled) Plugin.Log.LogDebug($"{Tag} {MapKey}: reach {reach:F0} m");
        }

        void ApplyVolume(float volume)
        {
            _appliedVolume = volume;
            for (int i = 0; i < _stands.Length; i++)
                if (_stands[i].Source != null) _stands[i].Source.volume = volume;
        }

        // A looping source that stopped for a reason we do not know (a deactivated object, a mixer rebuild) is started again, a bounded number of times.
        void Watch()
        {
            _nextWatch = Time.unscaledTime + WatchSeconds;
            for (int i = 0; i < _stands.Length; i++)
            {
                var source = _stands[i].Source;
                if (source == null || source.isPlaying) continue;
                if (_restarts >= MaxRestarts)
                {
                    if (!_gaveUp) { _gaveUp = true; Plugin.Log.LogDebug($"{Tag} {MapKey}: a stand source keeps stopping; not restarted again"); }
                    return;
                }
                _restarts++;
                source.Play();
                if (NoiseProbe.Armed) NoiseProbe.NoteAt(NoiseEv.Bed, _stands[i].Go.transform.position, _stands[i].Label, 1, i, source.volume);
                if (DeveloperOptions.Enabled) Plugin.Log.LogDebug($"{Tag} {MapKey}: stand {i} had stopped; restarted ({_restarts}/{MaxRestarts})");
            }
        }

        // The sources go, and the clip with them when asked; the decoded samples stay for the next start.
        void TearDown(bool freeClip)
        {
            if (_stands != null)
            {
                for (int i = 0; i < _stands.Length; i++)
                {
                    var stand = _stands[i];
                    if (stand == null) continue;
                    if (NoiseProbe.Armed && stand.Go != null) NoiseProbe.NoteAt(NoiseEv.Bed, stand.Go.transform.position, stand.Label, 0, i, stand.Source != null ? stand.Source.volume : 0f);
                    if (stand.Source != null) stand.Source.Stop();
                    if (stand.Go != null) Destroy(stand.Go);
                }
                _stands = null;
            }
            if (freeClip && _clip != null) { Destroy(_clip); _clip = null; }
            _appliedVolume = -1f;
        }

        void OnDestroy()
        {
            _ready = false;
            AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
            try { TearDown(true); }
            catch (Exception e) { Plugin.Log.LogDebug($"{Tag} {MapKey}: clean-up: {e.GetType().Name}: {e.Message}"); }
            _pcm = null;
            if (Current == this)
            {
                Current = null;
                SpectatorCrowd.State.Reset();
            }
            Plugin.Log.LogDebug($"{Tag} {MapKey}: stand sources, clip and excitement removed");
        }
    }
}
