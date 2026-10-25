using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What Arena does: every gate it animates plays a clip while it moves (an EntitySoundPlayer on an emitter next to the gate, mixer group
    /// ArenaGates at -15 dB). What the port lacked: any sound, a booth door slid open in silence. What this does: when
    /// <see cref="SpawnGates"/> RELEASES held gates and drives them (a mode plugin such as ArenaMode held them, or the developer test hold
    /// <c>Spawns/SpawnGateHold</c>), it hands over the key of every part it drives (<see cref="OnRelease"/>); the rows of the package
    /// <c>gate-sounds/&lt;map_key&gt;/gate-sounds.json</c> (extracted from Arena by tools/extract/extract_gate_sounds.py, staged by the
    /// client build) say which of those parts sound, from where, with which clip and at which gain. Each sound plays once, from the emitter
    /// Arena put it at, through <c>BetterAudio.PlayAtPoint</c> in EFT's InteractiveObjects group, in the first frame of the drive.
    /// Silent by design: a plain raid (the gates are parked open at raid start, nothing drives), a hold, a re-close and the snap
    /// (<c>Spawns/SpawnGateDrive</c> false); a Hold that arrives while a release plays stops its sounds.
    ///
    /// The clips are Arena's own, 44.1 kHz stereo PCM16, loaded from the package (length checked against the manifest; the SHA-256 is
    /// checked by tools/validate/validate_distribution.py, a managed SHA over 6 MB would cost the raid-start frame hundreds of ms). They are
    /// prepared in the background of the raid start, one per frame, as <c>AudioClip.Create</c> + <c>SetData</c>; a release that comes first
    /// loads what it needs on the spot (one Debug line, one hitch). The clip is fitted to the drive scale of the release
    /// (<see cref="GateSoundModel.PickSeam"/>: one stationary stretch is cut out so the end event of the clip lands near the end of the
    /// shortened motion; <c>Spawns/GateSoundFit</c> false plays the clip as it is). Unity discards the data of script-created clips on an
    /// audio reset, so a clip built before one is built again from the kept PCM (the same rule NativeSpatialAudio applies to its room tones).
    ///
    /// Not here, on purpose: loudness and reach are first estimates (the baked mixer level follows the PLAYER's group; some AudioSources name
    /// Environment, 0 dB, instead, and which wins in Arena is unknown), so <c>Spawns/GateSoundVolume</c> (live, 0 to 8) and
    /// <c>Spawns/GateSoundRolloffScale</c> are the levers; they are developer keys like every Spawns key, never public. Logging: Debug for
    /// every row and one summary per release, one Warning per clip that fails, nothing at Info. The occlusion mode is decided once per
    /// release (OneShotPropagation when the room graph is initialised, else none) and a failing PlayAtPoint is NOT retried: BetterAudio plays
    /// the clip before it processes the occlusion, so a retry would play it twice and leak a source.
    ///
    /// Host checks: docs/progress/gate-sounds.md.
    /// </summary>
    internal static class GateSounds
    {
        const string Tag = "[GateSounds]";
        const string ScaleSection = "Spawns", ScaleKey = "SpawnGateDriveScale";

        // One clip of the package, kept as PCM so it can be fitted again (another drive scale) or rebuilt (after an audio reset).
        sealed class LoadedClip
        {
            internal GateSoundModel.ClipInfo Info;
            internal short[] Pcm;
            internal AudioClip Clip;
            internal int Pick = -2;          // the seam the Clip was built with (-1 = whole), -2 = not built
            internal int BuiltAtReset = -1;
        }

        sealed class ActiveSound
        {
            internal BetterSource Source;
            internal float EndsAt;           // realtime when BetterAudio's queue gives the source back
            internal string Label;
        }

        sealed class State
        {
            internal string MapKey, Folder;
            internal GateSoundModel.Manifest Manifest;
            internal readonly Dictionary<string, LoadedClip> Clips = new Dictionary<string, LoadedClip>();
            internal readonly List<ActiveSound> Active = new List<ActiveSound>();
            internal readonly HashSet<string> Warned = new HashSet<string>();
            internal Coroutine Preparing, Waiting;
            internal bool Failed, Disposed;
        }

        static ConfigEntry<bool> _enabled, _fit;
        static ConfigEntry<float> _volume, _rolloff;
        static ConfigEntry<float> _driveScale;      // SpawnGates' own entry (bound before this one), read here for the prepare-time guess only
        static State _state;
        static int _audioResets;
        static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            var s = Plugin.Instance.Settings;
            _enabled = s.Bind("Spawns", "GateSounds", true,
                "Plays Arena's own gate and door sounds (booth doors, warehouse rollers and depot gates, passport leaves and bus doors, the Bowl " +
                "gates) when a mode plugin releases the held spawn gates (SpawnGates.Release, or the developer test hold). A plain raid has no " +
                "release and stays silent. Next release.");
            _volume = s.Bind("Spawns", "GateSoundVolume", 1.0f, new ConfigDescription(
                "Loudness of the gate sounds: 1 = Arena's own level through its ArenaGates mixer group (-15 dB; an estimate for the gates whose " +
                "AudioSource names the Environment group instead). Live (the next sound).", new AcceptableValueRange<float>(0f, GateSoundModel.MaxUserVolume)));
            _fit = s.Bind("Spawns", "GateSoundFit", true,
                "Cut one stationary stretch out of a gate clip so that its end event (the thunk or bang that closes the motion) lands near the " +
                "end of the shortened door motion (Spawns/SpawnGateDriveScale below 0.95); false plays every clip as Arena recorded it, longer " +
                "than the door moves. Next release.");
            _rolloff = s.Bind("Spawns", "GateSoundRolloffScale", 1.0f, new ConfigDescription(
                "Multiplies the distance (Arena's max distance of the sound, 30 to 60 m) beyond which a gate sound is not played at all. EFT's " +
                "InteractiveObjects falloff is steeper than Arena's, so a larger value carries the sound further. Next release.",
                new AcceptableValueRange<float>(0.5f, 4f)));
            s.TryGetEntry(new ConfigDefinition(ScaleSection, ScaleKey), out _driveScale);
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            AudioSettings.OnAudioConfigurationChanged += changed => _audioResets++;
        }

        // ---- hooks called by SpawnGates (each is guarded: a sound must never reach EFT's load chain) --------------------------------

        /// <summary>SpawnGates noted the first gate part with Arena's poses in this raid: start preparing the package in the background.</summary>
        internal static void Prepare()
        {
            try
            {
                if (!_installed || (_state != null && !_state.Disposed) || !_enabled.Value) return;   // once per raid: NotePoses calls this for every part
                string key = ArenaMapRaid.CurrentMap?.Key;
                if (key == null) return;
                _state = new State { MapKey = key, Folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "gate-sounds", key) };
                _state.Preparing = Plugin.Instance.StartCoroutine(PrepareAll(_state));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} could not start preparing the gate sounds ({e.GetType().Name}: {e.Message}); the gates stay silent");
            }
        }

        /// <summary>SpawnGates.Open drives these parts now (a release that follows a hold, scale = the SpawnGateDriveScale it uses): play their sounds.</summary>
        internal static void OnRelease(GateSoundModel.PartKey[] keys, float scale)
        {
            try
            {
                if (!_installed || !_enabled.Value || keys == null || keys.Length == 0) return;
                var state = _state;
                if (state == null || state.Disposed)
                {
                    Prepare();
                    state = _state;
                    if (state == null) return;
                }
                if (state.Failed) return;
                if (!EnsureManifest(state)) return;
                var rows = GateSoundModel.RowsFor(state.Manifest.Rows, keys);
                if (rows.Count == 0)
                {
                    Plugin.Log.LogDebug($"{Tag} release: {keys.Length} part(s) driven, none of them has a sound row");
                    return;
                }
                if (!GameStarted())
                {
                    // EFT's raid-start audio reset stops sources started before GameStatus.Started: wait for it
                    if (state.Waiting != null) Plugin.Instance.StopCoroutine(state.Waiting);
                    state.Waiting = Plugin.Instance.StartCoroutine(PlayWhenStarted(state, rows, scale, keys.Length));
                    Plugin.Log.LogDebug($"{Tag} release before the raid started: {rows.Count} sound(s) wait for GameStatus.Started");
                    return;
                }
                Play(state, rows, scale, keys.Length);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} release failed ({e.GetType().Name}: {e.Message}); the gates open without sound");
            }
        }

        /// <summary>SpawnGates.Hold: the gates are closed again, the sounds of the release before must not play on.</summary>
        internal static void OnHold()
        {
            try
            {
                var state = _state;
                if (state == null || state.Disposed) return;
                if (state.Waiting != null)
                {
                    Plugin.Instance.StopCoroutine(state.Waiting);
                    state.Waiting = null;
                }
                int stopped = StopActive(state);
                if (stopped > 0) Plugin.Log.LogDebug($"{Tag} hold: {stopped} gate sound(s) of the last release stopped");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} hold: stopping the sounds failed ({e.GetType().Name}: {e.Message})");
            }
        }

        // ---- scene end --------------------------------------------------------------------------------------------------------------

        static void OnSceneUnloaded(Scene scene)
        {
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null) return;
            try { Dispose(_state); }
            catch (Exception e) { Plugin.Log.LogWarning($"{Tag} release of the gate sounds failed ({e.GetType().Name}: {e.Message})"); }
            _state = null;
        }

        static void Dispose(State state)
        {
            if (state == null || state.Disposed) return;
            state.Disposed = true;
            if (state.Preparing != null) Plugin.Instance.StopCoroutine(state.Preparing);
            if (state.Waiting != null) Plugin.Instance.StopCoroutine(state.Waiting);
            StopActive(state);
            foreach (var loaded in state.Clips.Values)
                if (loaded.Clip != null) UnityEngine.Object.Destroy(loaded.Clip);
            state.Clips.Clear();
            Plugin.Log.LogDebug($"{Tag} released the gate sounds of {state.MapKey}");
        }

        // ---- loading ------------------------------------------------------------------------------------------------------------------

        // Reads and validates the manifest once per raid; a failure disables the raid's sounds with one Warning.
        static bool EnsureManifest(State state)
        {
            if (state.Manifest != null) return true;
            if (state.Failed) return false;
            try
            {
                string json = File.ReadAllText(Path.Combine(state.Folder, "gate-sounds.json"), Encoding.UTF8);
                state.Manifest = GateSoundModel.Parse(json, state.MapKey);
                return true;
            }
            catch (Exception e)
            {
                Fail(state, "package", $"the gate sound package of {state.MapKey} is unusable ({e.GetType().Name}: {e.Message})");
                return false;
            }
        }

        static void Fail(State state, string what, string message)
        {
            if (what == "package") state.Failed = true;
            if (state.Warned.Add(what)) Plugin.Log.LogWarning($"{Tag} {message}; the gates of this raid stay silent");
        }

        // Background preparation: the manifest on the first frame after the raid-start repairs, then one clip per frame for the drive scale
        // that is set now. Everything a release needs before it is done is loaded by the release itself.
        static IEnumerator PrepareAll(State state)
        {
            yield return null;
            if (state.Disposed || !EnsureManifest(state)) yield break;
            float scale = CurrentScale();
            int built = 0;
            foreach (var info in state.Manifest.Clips)
            {
                if (state.Disposed) yield break;
                if (EnsureClip(state, info.Id, scale) != null) built++;
                yield return null;
            }
            state.Preparing = null;
            Plugin.Log.LogDebug($"{Tag} prepared {built}/{state.Manifest.Clips.Length} clip(s) of {state.MapKey} for drive scale {scale:F2}");
        }

        static float CurrentScale() => GateSoundModel.ClampScale(_driveScale != null ? _driveScale.Value : DoorPoseModel.DefaultDriveScale);

        // The AudioClip of a package clip for this drive scale: read from disk once, then built (or built again for another seam or after an
        // audio reset). Returns null (one Warning per clip) when it cannot be had.
        static AudioClip EnsureClip(State state, string id, float scale)
        {
            if (state.Disposed || state.Failed) return null;
            try
            {
                var info = state.Manifest.ClipOf(id);
                if (info == null) throw new InvalidDataException("the manifest has no clip " + id);
                if (!state.Clips.TryGetValue(id, out var loaded))
                {
                    var path = Path.Combine(state.Folder, info.File);
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length != info.Bytes) throw new InvalidDataException($"{info.File} is {bytes.Length} bytes, the manifest says {info.Bytes}");
                    var wav = PcmWav.Read(bytes);
                    if (wav.Channels != info.Channels || wav.SampleRate != info.SampleRate || wav.Frames != info.Frames)
                        throw new InvalidDataException($"{info.File} is {wav.Channels} ch / {wav.SampleRate} Hz / {wav.Frames} frames, the manifest says {info.Channels} / {info.SampleRate} / {info.Frames}");
                    loaded = new LoadedClip { Info = info, Pcm = wav.Samples };
                    state.Clips[id] = loaded;
                }
                int pick = _fit.Value ? GateSoundModel.PickSeam(info.Fit, scale) : -1;
                if (loaded.Clip != null && loaded.Pick == pick && loaded.BuiltAtReset == _audioResets) return loaded.Clip;
                if (loaded.Clip != null) UnityEngine.Object.Destroy(loaded.Clip);
                loaded.Clip = null;
                var samples = PcmWav.ToFloats(loaded.Pcm);
                if (pick >= 0) samples = GateSoundModel.ApplyCut(samples, info.Channels, info.Fit, info.Fit.Seams[pick]);
                var clip = AudioClip.Create($"ArenaMapPort gate {id}{(pick >= 0 ? " -" + info.Fit.Seams[pick].RemoveS.ToString("F3") + " s" : "")}",
                    samples.Length / info.Channels, info.Channels, info.SampleRate, false);
                if (!clip.SetData(samples, 0))
                {
                    UnityEngine.Object.Destroy(clip);
                    throw new InvalidDataException("the sample upload of " + id + " failed");
                }
                loaded.Clip = clip;
                loaded.Pick = pick;
                loaded.BuiltAtReset = _audioResets;
                Plugin.Log.LogDebug($"{Tag} clip {id}: {(pick >= 0 ? $"seam -{info.Fit.Seams[pick].RemoveS:F3} s (end event at {GateSoundModel.EventLandsAt(info.Fit, pick):F3} s of {info.Fit.EventS:F2} s) for scale {scale:F2}" : "whole")}, " +
                                    $"{clip.length:F2} s");
                return clip;
            }
            catch (Exception e)
            {
                if (state.Warned.Add("clip " + id)) Plugin.Log.LogWarning($"{Tag} clip {id} is unusable ({e.GetType().Name}: {e.Message}); that gate stays silent");
                return null;
            }
        }

        // ---- playing ------------------------------------------------------------------------------------------------------------------

        static bool GameStarted() =>
            Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started;

        static IEnumerator PlayWhenStarted(State state, List<int> rows, float scale, int parts)
        {
            float giveUp = Time.realtimeSinceStartup + 120f;
            while (!state.Disposed && !GameStarted() && Time.realtimeSinceStartup < giveUp) yield return null;
            state.Waiting = null;
            if (state.Disposed) yield break;
            if (!GameStarted())
            {
                Plugin.Log.LogDebug($"{Tag} the raid did not start within 120 s: {rows.Count} waiting sound(s) dropped");
                yield break;
            }
            try { Play(state, rows, scale, parts); }
            catch (Exception e) { Plugin.Log.LogWarning($"{Tag} delayed release failed ({e.GetType().Name}: {e.Message})"); }
        }

        static void Play(State state, List<int> rows, float scale, int parts)
        {
            if (!MonoBehaviourSingleton<BetterAudio>.Exist(out var audio))
            {
                Plugin.Log.LogDebug($"{Tag} no BetterAudio instance: {rows.Count} sound(s) not played");
                return;
            }
            StopActive(state);
            scale = GateSoundModel.ClampScale(scale);
            // decided once for the whole release: the room-graph propagation when the native graph runs, else none. A failure inside
            // PlayAtPoint is never answered with a second call (the first already plays the clip).
            var occlusion = SpatialAudioSystem.Initialized && MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated
                ? EOcclusionTest.OneShotPropagation : EOcclusionTest.None;
            int started = 0, silent = 0, failed = 0;
            foreach (int index in rows)
            {
                var row = state.Manifest.Rows[index];
                var clip = EnsureClip(state, row.Clip, scale);
                if (clip == null) { failed++; continue; }
                var position = new Vector3(row.Emitter[0], row.Emitter[1], row.Emitter[2]);
                int rolloff = GateSoundModel.Rolloff(row, _rolloff.Value);
                float volume = GateSoundModel.Gain(row, _volume.Value);
                BetterSource source = null;
                try
                {
                    source = audio.PlayAtPoint(position, clip, BetterAudio.AudioSourceGroupType.InteractiveObjects, rolloff, volume, occlusion);
                }
                catch (Exception e)
                {
                    failed++;
                    if (state.Warned.Add("play " + row.Clip)) Plugin.Log.LogWarning($"{Tag} PlayAtPoint of {row.Clip} failed ({e.GetType().Name}: {e.Message}); not retried");
                    continue;
                }
                string label = $"{row.Origin} / {row.Clip}";
                if (source == null)
                {
                    silent++;
                    Plugin.Log.LogDebug($"{Tag} {label}: not played (the listener is beyond {rolloff} m, or the InteractiveObjects pool has no free source)");
                    continue;
                }
                started++;
                state.Active.Add(new ActiveSound { Source = source, EndsAt = Time.realtimeSinceStartup + Mathf.Max(clip.length, 1f) - 0.1f, Label = label });
                Plugin.Log.LogDebug($"{Tag} {label}: volume {volume:F3} ({20f * Mathf.Log10(Mathf.Max(volume, 1e-6f)):F1} dB), reaches {rolloff} m, " +
                                    $"from ({position.x:F1}, {position.y:F1}, {position.z:F1}), occlusion {occlusion}");
            }
            Plugin.Log.LogDebug($"{Tag} released {parts} part(s): {rows.Count} sound row(s), {started} playing, {silent} out of reach or without a free source, " +
                                $"{failed} failed (drive scale {scale:F2}, fit {(_fit.Value ? "on" : "off")}, volume {_volume.Value:F2})");
            if (started > 0) Plugin.Instance.StartCoroutine(ReportPlaying(state, state.Active.ToArray()));
        }

        // One frame later: which of the sounds really play (host evidence; a source that EFT refused shows isPlaying False).
        static IEnumerator ReportPlaying(State state, ActiveSound[] sounds)
        {
            yield return null;
            if (state.Disposed) yield break;
            var sb = new StringBuilder();
            int playing = 0;
            foreach (var sound in sounds)
            {
                bool on = sound.Source != null && sound.Source.source1 != null && sound.Source.source1.isPlaying;
                if (on) playing++;
                else sb.Append("\n  ").Append(sound.Label).Append(": isPlaying False");
            }
            Plugin.Log.LogDebug($"{Tag} one frame later: {playing}/{sounds.Length} playing{sb}");
        }

        // Stops what the last release started and is still playing (a Hold, the next release, the end of the raid). BetterAudio's own queue
        // gives each source back at the end of its clip; stopping early leaves it borrowed until then, which is how EFT's own one-shots end.
        static int StopActive(State state)
        {
            int stopped = 0;
            float now = Time.realtimeSinceStartup;
            foreach (var sound in state.Active)
            {
                if (now >= sound.EndsAt || sound.Source == null || sound.Source.source1 == null || !sound.Source.source1.isPlaying) continue;
                sound.Source.Stop();
                stopped++;
            }
            state.Active.Clear();
            return stopped;
        }
    }
}
