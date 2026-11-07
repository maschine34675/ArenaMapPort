using System;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Audio;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What Arena does: the in-match crowd beds of ambient_voice.bundle come in three banks (Cheer, Angry, Neutral) and its <c>FansAmbient</c> emitters play them on the bank's trigger as the match
    /// goes; the emitters are inactive in the imported scenes and the triggers never fire, so no stand ever roars or boos (the port's murmur is the Neutral bed, SpectatorCrowdBed.cs).
    /// What the port lacks: the emitters and the triggers. What this does (the port's own addition, user decision 2026-11-07, docs/PLAN-CROWD-REACTIONS.md and docs/progress/spectator-audio.md
    /// "Roar beds"): when the crowd answers a match event (<see cref="SpectatorAudio"/>.StartReaction) it also plays one of Arena's Cheer beds (Approve, Start, Finish) or Angry beds (Disapprove)
    /// from every stand of the murmur bed (the same <see cref="SpectatorCrowdBedModel.Plan"/>: 4/4/6/6 stands on Equator/Bay5/Air Pit/Bowl, placed with the bed's own helpers), as a looping owned
    /// AudioSource per stand and kind, created at the first trigger of the kind. All stands play the same clip, each from its own random frame; the strength sets the level (0.35 to 1) and the
    /// reaction's length the hold (1.5 to 5 s), a 0.3 s attack and a 2 s release shape it (<see cref="RoarEnvelope"/>), and the other kind is cut in 0.5 s. A reaction no longer lifts the
    /// murmur. The layer does not depend on the murmur bed being on. The six clips are read, hashed and decoded on the crowd worker (ArenaSpectators.LoadRoarPackage) and become AudioClips lazily.
    ///
    /// The tick does nothing but read two fields while no roar sounds; while one does it steps the envelopes and writes a source's volume only when its gain changed. Outside <c>Started</c> every
    /// roar stops at once, the audio reset of EFT stops them and drops the clips (they are rebuilt at the next trigger), and the crowd's removal removes everything (this is a child of the crowd
    /// root). A package without roar beds is not an error (one Debug line); a damaged set is one Warning and the calls, the reactions and the murmur go on. Developer keys like
    /// <c>Spectators/CrowdBed</c> (honoured only with DeveloperMode, the defaults apply otherwise): <c>Spectators/ReactionBeds</c> and <c>ReactionBedVolume</c>, both live, none public.
    /// Host verification: not yet (unit-tested only; the level, -32 dBFS RMS per bed, is a start value for the author's listening).
    /// </summary>
    internal sealed class SpectatorReactionBeds : MonoBehaviour
    {
        const string Tag = "[ReactionBeds]";
        static ConfigEntry<bool> _enabled;
        static ConfigEntry<float> _volume;
        static bool _installed;

        /// <summary>The roar layer of the running raid; null outside an owned raid, without a crowd, or when the package has no roar beds.</summary>
        internal static SpectatorReactionBeds Current { get; private set; }

        sealed class Lane
        {
            public AudioSource Source;
            public readonly RoarEnvelope Envelope = new RoarEnvelope();
            public bool Sounding;
            public int Clip = -1;
            public string Label;
        }

        sealed class Stand
        {
            public GameObject Go;
            public readonly Lane[] Lanes = new Lane[SpectatorReactionBedKind.Count + 1];   // by bed kind; [0] unused
        }

        public string MapKey;

        BedStand[] _plan;
        Stand[] _stands;
        RoarClipTable _table;
        RoarClipPicker _picker;
        short[][][] _pcm;                                    // [bed kind][clip]: the decoded samples, kept for the lifetime of the layer
        AudioClip[][] _clips;                                // [bed kind][clip]: built at the first trigger of a clip, dropped on an audio reset
        readonly int[] _current = { -1, -1, -1 };            // the clip a kind plays now (a retrigger keeps it)
        int _sounding;
        float _appliedKey = -1f;
        bool _ready, _reset, _failed;

        static bool BedsOn => !DeveloperOptions.Enabled || _enabled == null || _enabled.Value;
        static float VolumeNow => DeveloperOptions.Enabled && _volume != null ? SpectatorCrowdBedModel.ClampVolume(_volume.Value) : SpectatorReactionBedModel.ReactionBedVolumeDefault;

        /// <summary>Lanes that sound (a source started and its envelope has not ended); read by the NoiseProbe census.</summary>
        internal int ActiveSources => _sounding;

        /// <summary>Lanes whose AudioSource reports playing; read only by the NoiseProbe census (at a burst or a mark), at most twelve reads.</summary>
        internal int PlayingSources
        {
            get
            {
                var stands = _stands;
                int n = 0;
                if (stands != null)
                    for (int i = 0; i < stands.Length; i++)
                        if (stands[i] != null)
                            for (int k = 1; k < stands[i].Lanes.Length; k++)
                                if (stands[i].Lanes[k] != null && stands[i].Lanes[k].Source != null && stands[i].Lanes[k].Source.isPlaying) n++;
                return n;
            }
        }

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            var s = Plugin.Instance.Settings;
            _enabled = s.Bind("Spectators", "ReactionBeds", SpectatorReactionBedModel.ReactionBedsDefault,
                "Developer hearing comparison: a reaction of the spectators plays one of Arena's cheer or boo beds from the stands. Live; off stops the roar at once. " +
                "Ignored outside DeveloperMode, where the roar beds are on.");
            _volume = s.Bind("Spectators", "ReactionBedVolume", SpectatorReactionBedModel.ReactionBedVolumeDefault, new ConfigDescription(
                "Developer hearing comparison: level of the cheer and boo beds, 1 = the exported level (-32 dBFS RMS per bed, a start value). Live. Ignored outside DeveloperMode.",
                new AcceptableValueRange<float>(0f, SpectatorCrowdBedModel.MaxVolume)));
        }

        /// <summary>Called by the crowd right after the murmur bed from the same positions; never throws.</summary>
        internal static void Create(ArenaSpectators crowd, Vector3[] positions, ArenaSpectators.RoarPackage package)
        {
            GameObject root = null;
            try
            {
                if (package == null || package.Absent)
                {
                    Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: the spectator audio package has no roar beds; none will play");
                    return;
                }
                if (package.Error != null)
                {
                    Plugin.Log.LogWarning($"{Tag} the cheer and boo beds are unavailable ({package.Error.GetType().Name}: {package.Error.Message}); the calls, the reactions and the murmur remain");
                    return;
                }
                var points = new BedPoint[positions.Length];
                for (int i = 0; i < points.Length; i++) points[i] = new BedPoint(positions[i].x, positions[i].y, positions[i].z);
                var plan = SpectatorCrowdBedModel.Plan(points, SpectatorCrowdBed.ReachNow);
                if (plan.Length == 0)
                {
                    Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: the crowd has no usable position; no roar beds");
                    return;
                }
                var table = new RoarClipTable(package.Manifest.reaction_beds);
                root = new GameObject("ArenaMapPort_SpectatorRoar_" + crowd.MapKey);
                root.transform.SetParent(crowd.transform, false);
                var component = root.AddComponent<SpectatorReactionBeds>();
                component.MapKey = crowd.MapKey;
                component._plan = plan;
                component._table = table;
                component._pcm = package.Pcm;
                component._picker = new RoarClipPicker(table, unchecked(Environment.TickCount ^ crowd.GetInstanceID() ^ 0x3C6EF372));
                component._stands = new Stand[plan.Length];
                component._clips = new AudioClip[SpectatorReactionBedKind.Count + 1][];
                for (int k = 1; k <= SpectatorReactionBedKind.Count; k++) component._clips[k] = new AudioClip[table.Count(k)];
                AudioSettings.OnAudioConfigurationChanged += component.AudioChanged;
                component._ready = true;
                Current = component;
                Plugin.Log.LogDebug($"{Tag} {crowd.MapKey}: {table.Count(SpectatorReactionBedKind.Cheer) + table.Count(SpectatorReactionBedKind.Angry)} roar beds " +
                    $"({table.Count(SpectatorReactionBedKind.Cheer)} cheer, {table.Count(SpectatorReactionBedKind.Angry)} angry), {plan.Length} stands, volume {VolumeNow:F2}");
            }
            catch (Exception e)
            {
                if (root != null) Destroy(root);
                Plugin.Log.LogWarning($"{Tag} the cheer and boo beds are unavailable ({e.GetType().Name}: {e.Message}); the calls, the reactions and the murmur remain");
            }
        }

        /// <summary>
        /// Starts or retriggers the roar of a reaction kind from every stand (main thread, called by SpectatorAudio when a reaction starts). Returns true when a roar started or was
        /// retriggered; false for a kind without a bed (TakeCover, the reserved kinds), a layer that is switched off, failed or not ready, or an audio group that does not exist yet.
        /// </summary>
        internal bool Trigger(int reactionKind, float strength, double reactionDurationSeconds)
        {
            if (!_ready || _failed || !BedsOn) return false;
            if (_reset)
            {
                // a pending audio reset: Update has not consumed it yet, and lanes started now would be destroyed by it
                _reset = false;
                try { Release(true); }
                catch (Exception e) { Fail(e); return false; }
            }
            int bed = SpectatorReactionBedKind.For(reactionKind);
            if (bed == SpectatorReactionBedKind.None || !_table.Has(bed)) return false;
            try
            {
                var group = SpectatorCrowdBed.EnvironmentGroup();
                if (group == null) return false;
                int other = bed == SpectatorReactionBedKind.Cheer ? SpectatorReactionBedKind.Angry : SpectatorReactionBedKind.Cheer;
                double now = Time.unscaledTimeAsDouble;
                float amplitude = SpectatorReactionBedModel.Amplitude(strength);
                float hold = SpectatorReactionBedModel.HoldSeconds(reactionDurationSeconds);
                // A roar of this kind that still sounds keeps its clip (a retrigger never swaps the audio under the listener); otherwise one clip is chosen for all stands.
                int clipIndex = AnyActive(bed) && _current[bed] >= 0 ? _current[bed] : _picker.Pick(bed);
                if (clipIndex < 0) return false;
                _current[bed] = clipIndex;
                var clip = ClipFor(bed, clipIndex);
                float key = VolumeNow;
                int frames = _table.Clips(bed)[clipIndex].frames;
                for (int i = 0; i < _stands.Length; i++)
                {
                    var stand = _stands[i] ?? (_stands[i] = new Stand());
                    var cut = stand.Lanes[other];
                    if (cut != null && cut.Envelope.Active) cut.Envelope.Cut(now);
                    var lane = stand.Lanes[bed] ?? (stand.Lanes[bed] = new Lane { Label = SpectatorReactionBedKind.Name(bed) + "/stand" + i });
                    if (!lane.Sounding) StartLane(stand, i, bed, lane, clip, clipIndex, frames, group);
                    lane.Envelope.Trigger(amplitude, hold, now);
                    lane.Source.volume = SpectatorReactionBedModel.SourceVolume(key, lane.Envelope.Gain);
                }
                _appliedKey = key;
                if (DeveloperOptions.Enabled)
                    Plugin.Log.LogDebug($"{Tag} {MapKey}: roar {SpectatorReactionBedKind.Name(bed)} amp={amplitude.ToString("0.00", NoiseText.Inv)} hold={hold.ToString("0.0", NoiseText.Inv)} s " +
                        $"clip={_table.Clips(bed)[clipIndex].name} stands={_stands.Length}");
                return true;
            }
            catch (Exception e)
            {
                Fail(e);
                return false;
            }
        }

        bool AnyActive(int bed)
        {
            for (int i = 0; i < _stands.Length; i++)
            {
                var lane = _stands[i] != null ? _stands[i].Lanes[bed] : null;
                if (lane != null && lane.Sounding) return true;
            }
            return false;
        }

        // The clip as the floats the bed's volume law expects (ClipGain baked in); built at the first trigger of the clip and shared by the stands.
        AudioClip ClipFor(int bed, int index)
        {
            var clips = _clips[bed];
            if (clips[index] != null) return clips[index];
            var c = _table.Clips(bed)[index];
            var clip = AudioClip.Create("ArenaMapPort roar bed " + c.name, c.frames, 1, c.sample_rate, false);
            if (!clip.SetData(SpectatorCrowdBedModel.ToClipFloats(_pcm[bed][index]), 0))
            {
                Destroy(clip);
                throw new System.IO.InvalidDataException("roar bed sample upload failed");
            }
            return clips[index] = clip;
        }

        // The stand's object and the lane's source are created at the first trigger that needs them; the source is placed like the murmur's (position, spread, the stand's Custom curve, the group).
        void StartLane(Stand stand, int index, int bed, Lane lane, AudioClip clip, int clipIndex, int frames, AudioMixerGroup group)
        {
            var plan = _plan[index];
            if (stand.Go == null)
            {
                stand.Go = new GameObject("SpectatorRoarStand_" + index);
                stand.Go.transform.SetParent(transform, false);
                stand.Go.transform.position = new Vector3(plan.X, plan.Y, plan.Z);
            }
            var source = lane.Source;
            if (source == null)
            {
                source = lane.Source = stand.Go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = true;
                source.pitch = 1f;
                source.spatialBlend = 1f;
                source.spread = 70f;
                source.dopplerLevel = 0f;
                source.priority = 150;                 // above the murmur's 200: the roar wins if the voices run short
                source.outputAudioMixerGroup = group;
                SpectatorCrowdBed.Place(source, plan);
            }
            source.clip = clip;
            source.volume = 0f;
            source.timeSamples = Mathf.Clamp(_picker.StartOffset(frames), 0, Mathf.Max(0, frames - 1));
            source.Play();
            lane.Clip = clipIndex;
            lane.Sounding = true;
            _sounding++;
            if (NoiseProbe.Armed) NoiseProbe.NoteAt(NoiseEv.Roar, stand.Go.transform.position, lane.Label, 1, index, source.volume, _table.Clips(bed)[clipIndex].name);
        }

        void StopLane(Stand stand, int index, Lane lane)
        {
            if (!lane.Sounding) return;
            lane.Sounding = false;
            _sounding--;
            lane.Envelope.Stop();
            if (NoiseProbe.Armed && stand.Go != null) NoiseProbe.NoteAt(NoiseEv.Roar, stand.Go.transform.position, lane.Label, 0, index, lane.Source != null ? lane.Source.volume : 0f);
            if (lane.Source != null) lane.Source.Stop();
        }

        void StopAll()
        {
            if (_stands == null) return;
            for (int i = 0; i < _stands.Length; i++)
            {
                var stand = _stands[i];
                if (stand == null) continue;
                for (int k = 1; k < stand.Lanes.Length; k++)
                    if (stand.Lanes[k] != null) StopLane(stand, i, stand.Lanes[k]);
            }
            _sounding = 0;
            for (int k = 0; k < _current.Length; k++) _current[k] = -1;
        }

        void AudioChanged(bool deviceChanged)
        {
            _reset = true;
            if (DeveloperOptions.Enabled) Plugin.Log.LogDebug($"{Tag} {MapKey}: audio configuration changed (device changed={deviceChanged}); the roars stop and the clips are rebuilt on demand");
        }

        void Update()
        {
            if (!_ready) return;
            if (_reset)
            {
                _reset = false;
                try { Release(true); }
                catch (Exception e) { Fail(e); return; }
            }
            if (_sounding == 0) return;           // idle: two field reads
            try
            {
                bool started = Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started;
                if (!started || !BedsOn || !SpectatorAudio.ReactionsOn)
                {
                    StopAll();                    // no release outside Started, with the roar key off or with Spectators/Reactions off
                    return;
                }
                double now = Time.unscaledTimeAsDouble;
                float key = VolumeNow;
                bool keyChanged = key != _appliedKey;
                _appliedKey = key;
                for (int i = 0; i < _stands.Length; i++)
                {
                    var stand = _stands[i];
                    if (stand == null) continue;
                    for (int k = 1; k < stand.Lanes.Length; k++)
                    {
                        var lane = stand.Lanes[k];
                        if (lane == null || !lane.Sounding) continue;
                        float gain = lane.Envelope.Step(now, out bool changed);
                        if (!lane.Envelope.Active) { StopLane(stand, i, lane); continue; }
                        if ((changed || keyChanged) && lane.Source != null) lane.Source.volume = SpectatorReactionBedModel.SourceVolume(key, gain);
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        void Fail(Exception e)
        {
            bool first = !_failed;
            _failed = true;
            try { Release(true); } catch (Exception) { /* the Warning below is the report */ }
            if (first) Plugin.Log.LogWarning($"{Tag} the cheer and boo beds stopped ({e.GetType().Name}: {e.Message}); the calls, the reactions and the murmur remain");
        }

        // Stops every roar; with destroy also the stand objects (their sources) and the clips, which are rebuilt at the next trigger. The decoded samples stay.
        void Release(bool destroy)
        {
            StopAll();
            if (!destroy) return;
            if (_stands != null)
                for (int i = 0; i < _stands.Length; i++)
                {
                    var stand = _stands[i];
                    if (stand == null) continue;
                    if (stand.Go != null) Destroy(stand.Go);
                    _stands[i] = null;
                }
            if (_clips != null)
                for (int k = 0; k < _clips.Length; k++)
                    if (_clips[k] != null)
                        for (int j = 0; j < _clips[k].Length; j++)
                            if (_clips[k][j] != null) { Destroy(_clips[k][j]); _clips[k][j] = null; }
            _appliedKey = -1f;
        }

        void OnDestroy()
        {
            _ready = false;
            AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
            try { Release(true); }
            catch (Exception e) { Plugin.Log.LogDebug($"{Tag} {MapKey}: clean-up: {e.GetType().Name}: {e.Message}"); }
            _pcm = null;
            if (Current == this) Current = null;
            Plugin.Log.LogDebug($"{Tag} {MapKey}: roar sources and clips removed");
        }
    }
}
