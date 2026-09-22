using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.AmbientSplineEmitter;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>Starts Bay5's imported stock ambient players once real raid audio services exist.
    /// Arena's ambient singleton/room system is deliberately not recreated. Stock players retain
    /// their original clip selection, gain, pitch and timing; the stock shoreline calculator ticks
    /// from this owned driver. Host acceptance is documented in bay5-native-features progress.</summary>
    internal static class Bay5AmbientAudio
    {
        internal const string RootName = "ArenaMapPort_Bay5NativeAudio";
        static Driver _driver;
        static ConfigEntry<bool> _diagnostics;

        internal static void Ensure()
        {
            if (_driver != null || ArenaMapRaid.CurrentMap?.Key != "arena_bay5") return;
            if (_diagnostics == null) _diagnostics = Plugin.Instance.Config.Bind("Audio (arena_bay5)", "PlaybackDiagnostics", false,
                "Log native ambient clip/sample and shoreline movement evidence while in Bay5. Takes effect immediately; F10 also logs a snapshot.");
            var scene = SceneManager.GetSceneByName(ArenaMapRaid.CurrentMap.ScriptsSceneName);
            var roots = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                .Where(t => t.name == RootName).ToArray();
            if (roots.Length == 0) return; // Old packages do not contain this optional feature.
            if (roots.Length != 1) { Plugin.Log.LogError("[ArenaMapPort][NativeAudio] duplicate owned audio roots; refusing playback"); return; }
            _driver = Plugin.Instance.gameObject.AddComponent<Driver>();
            _driver.Initialize(roots[0].gameObject);
        }

        internal static void Shutdown()
        {
            var driver = _driver;
            _driver = null;
            if (driver == null) return;
            driver.Dispose();
            UnityEngine.Object.Destroy(driver);
        }

        /// <summary>Explicit diagnostic hook; automatic summaries also prove playback sample advancement.</summary>
        internal static void Probe() { if (_driver != null) _driver.Report(true); }

        public sealed class Driver : MonoBehaviour
        {
            GameObject _root;
            BaseAmbientSoundPlayer[] _players = Array.Empty<BaseAmbientSoundPlayer>();
            AmbientSplineEmitterController[] _controllers = Array.Empty<AmbientSplineEmitterController>();
            AmbientPlayerSplineMappedEmitter _water;
            readonly Dictionary<AudioSource, Tuple<AudioClip, int>> _samples = new Dictionary<AudioSource, Tuple<AudioClip, int>>();
            readonly List<AudioClip> _emptyClips = new List<AudioClip>();
            readonly float[] _outputSamples = new float[256];
            AudioMixer _ambientMixer;
            string _ambientVolumeParameter;
            float _previousAmbientVolume;
            bool _ownsAmbientVolume;
            float _servicesSince = -1f, _nextReport, _nextReadyCheck;
            int _reports;
            bool _ready, _disposed;
            ESeasonStatus _season;
            Vector3 _previousEmitter;
            bool _hasEmitterPosition;

            internal void Initialize(GameObject root)
            {
                _root = root;
                _players = root.GetComponentsInChildren<BaseAmbientSoundPlayer>(true);
                _controllers = root.GetComponentsInChildren<AmbientSplineEmitterController>(true);
                _water = root.GetComponentInChildren<AmbientPlayerSplineMappedEmitter>(true);
                if (root.activeSelf || _players.Length != 24 || _controllers.Length != 1 || _water == null)
                {
                    Plugin.Log.LogError("[ArenaMapPort][NativeAudio] invalid imported graph; refusing playback");
                    Dispose();
                    return;
                }
                Plugin.Log.LogInfo("[ArenaMapPort][NativeAudio] waiting for raid listener, season and native mixers; players=24");
            }

            void Update()
            {
                if (_disposed) return;
                if (_root == null || ArenaMapRaid.CurrentMap?.Key != "arena_bay5") { Shutdown(); return; }
                try
                {
                    if (!_ready)
                    {
                        if (Time.realtimeSinceStartup < _nextReadyCheck) return;
                        _nextReadyCheck = Time.realtimeSinceStartup + .5f;
                        TryStart();
                        if (!_ready && _servicesSince >= 0f && Time.realtimeSinceStartup - _servicesSince > 180f)
                        {
                            Plugin.Log.LogError("[ArenaMapPort][NativeAudio] raid audio services unavailable after 180s; playback remains off");
                            Dispose();
                        }
                        return;
                    }
                    var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                    var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
                    if (world == null || game == null || game.Status == GameStatus.Stopping || game.Status == GameStatus.Stopped || game.Status == GameStatus.SoftStopping)
                    { Shutdown(); return; }
                    if (Seasons.Controller != null && Seasons.Controller.Status != _season)
                        ApplySeason(Seasons.Controller.Status);
                    foreach (var controller in _controllers) controller.Tick();
                    if ((_diagnostics?.Value ?? false) && Time.realtimeSinceStartup >= _nextReport)
                    {
                        Report(false);
                        _reports++;
                        _nextReport = Time.realtimeSinceStartup + (_reports < 4 ? 10f : 60f);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[ArenaMapPort][NativeAudio] playback initialization/update failed; stopping owned sources: " + ex);
                    Dispose();
                }
            }

            void LateUpdate()
            {
                if (!_ready || _disposed) return;
                try { foreach (var controller in _controllers) controller.LateTick(); }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[ArenaMapPort][NativeAudio] shoreline update failed; stopping owned sources: " + ex);
                    Dispose();
                }
            }

            void TryStart()
            {
                var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                var listener = Singleton<AudioListenerConsistencyManager>.Instantiated ? Singleton<AudioListenerConsistencyManager>.Instance : null;
                if (world == null || world.MainPlayer == null || !Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started) return;
                if (_servicesSince < 0f) _servicesSince = Time.realtimeSinceStartup;
                if (listener == null || listener.transform.position.y < -500f || Seasons.Controller == null || !BetterAudio.Instantiated)
                    return;
                var audio = BetterAudio.Instance;
                if (audio.Master == null || audio.AudioMixerData == null) return;
                var effects = Mixer(audio.Master, "AmbientOut/CommonAmbOutEffects", "CommonAmbOutEffects");
                var bypass = Mixer(audio.Master, "CommonAmbOutBypass", "CommonAmbOutBypass");
                if (effects == null || bypass == null) return;
                var groups = _root.GetComponentsInChildren<AmbientSoundPlayerGroup>(true);
                if (groups.Length != 2) throw new InvalidOperationException("expected both source ambient groups");
                foreach (var group in groups)
                {
                    if (group.name == "AmbientPlayersGroup") group._outputMixerGroup = effects;
                    else if (group.name == "AmbientSplineEmitterWaterGroup") group._outputMixerGroup = bypass;
                    else throw new InvalidOperationException("unrecognized ambient group " + group.name);
                    group.SetMixerGroup();
                }
                AcquireAmbientVolume(audio);
                // All serialized fields and links exist before any stock Awake executes.
                _root.SetActive(true);
                foreach (var player in _players)
                    if (player.EmptyClip != null) _emptyClips.Add(player.EmptyClip);
                foreach (var controller in _controllers) controller.Init(listener.transform);
                PrepareShoreline();
                _season = Seasons.Controller.Status;
                int started = 0, seasonalSilence = 0;
                foreach (var player in _players)
                {
                    if (player is SeasonAmbientSoundPlayer seasonal)
                    {
                        if (!seasonal._seasonAmbientBanks.TryGetValue(_season, out var bank) || bank == null) { seasonalSilence++; continue; }
                        seasonal.SetSeasonStatus(_season);
                    }
                    player.Play();
                    started++;
                }
                _ready = true;
                _nextReport = Time.realtimeSinceStartup + 5f;
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] ready players={_players.Length} started={started} seasonalSilent={seasonalSilence} season={_season} mixers={effects.name},{bypass.name} shoreline=stock listener={listener.transform.position}");
            }

            static AudioMixerGroup Mixer(AudioMixer mixer, string path, string exactName)
            {
                var matches = mixer.FindMatchingGroups(path).Where(m => m != null && m.name == exactName).ToArray();
                return matches.Length == 1 ? matches[0] : null;
            }

            void PrepareShoreline()
            {
                // SPT measures its broad-range radius around the spline, but Init stores
                // the emitter's transform as the centre. Arena's two source origins differ
                // by over 200 m, incorrectly excluding playable parts of Bay5. Keep the
                // original spline, radius, jobs and attenuation; align this owned cache only.
                var calculators = _root.GetComponentsInChildren<SoundAmbientZoneCalculator>(true);
                var centre = typeof(BaseSplineEmitterCalculator).GetField("_splineCachedTransform",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (calculators.Length != 1 || centre == null || centre.FieldType != typeof(Transform) ||
                    calculators[0]._spline == null || calculators[0]._splineEmitter == null)
                    throw new InvalidOperationException("native shoreline range-origin contract unavailable");
                var calculator = calculators[0];
                var previous = centre.GetValue(calculator) as Transform;
                if (previous == null || (previous != calculator._splineEmitter.transform &&
                    previous != calculator._spline.transform))
                    throw new InvalidOperationException("unexpected native shoreline range origin");
                centre.SetValue(calculator, calculator._spline.transform);
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] shoreline range origin aligned from={previous.position} to={calculator._spline.transform.position} radius={calculator._calculationRadius:F2}");

                // The first stock translation adds its direction to _lastPos, initially
                // world zero. Schedule at the real listener first, then seed that origin
                // before consuming the job. _isFirstCalculation still requests the initial
                // translation. No per-frame field access or custom attenuation is needed.
                var lastPosition = typeof(SoundAmbientZoneCalculator).GetField("_lastPos",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var firstCalculation = typeof(SoundAmbientZoneCalculator).GetField("_isFirstCalculation",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (lastPosition == null || lastPosition.FieldType != typeof(Vector3) ||
                    firstCalculation == null || firstCalculation.FieldType != typeof(bool) ||
                    !(bool)firstCalculation.GetValue(calculator))
                    throw new InvalidOperationException("native shoreline initial-position contract unavailable");
                var target = calculator.TargetPos;
                if (!calculator.IsInRange() || target == (Vector3)lastPosition.GetValue(calculator))
                {
                    Plugin.Log.LogInfo("[ArenaMapPort][NativeAudio] shoreline initial calculation deferred to native listener movement");
                    return;
                }
                foreach (var controller in _controllers) controller.Tick();
                lastPosition.SetValue(calculator, target);
                foreach (var controller in _controllers) controller.LateTick();
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] shoreline initial calculation listener={calculator.TargetPos} emitter={_water.EmitterPosition}");
            }

            void AcquireAmbientVolume(BetterAudio audio)
            {
                // ResetMasterValues leaves this parent bus at -80 dB. Original Bay5's
                // AmbientOutMixerBlendParameters has exterior endpoint 1 (= 0 dB).
                // We own only that neutral bus gain while this graph lives; native earpro,
                // spatial filters and Main/InGame volumes retain their normal ownership.
                _ambientMixer = audio.Master;
                _ambientVolumeParameter = audio.AudioMixerData.AmbientOutMixerVolume;
                if (!_ambientMixer.GetFloat(_ambientVolumeParameter, out _previousAmbientVolume) ||
                    !_ambientMixer.SetFloat(_ambientVolumeParameter, 0f))
                    throw new InvalidOperationException("native exterior ambient volume parameter unavailable");
                _ownsAmbientVolume = true;
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] exterior bus acquired previousDb={_previousAmbientVolume:F2} ownedDb=0.00");
            }

            void ReleaseAmbientVolume()
            {
                if (!_ownsAmbientVolume) return;
                _ownsAmbientVolume = false;
                if (_ambientMixer == null) return;
                if (!_ambientMixer.GetFloat(_ambientVolumeParameter, out float current))
                    throw new InvalidOperationException("native exterior ambient volume could not be read during cleanup");
                if (Mathf.Approximately(current, 0f))
                {
                    if (!_ambientMixer.SetFloat(_ambientVolumeParameter, _previousAmbientVolume))
                        throw new InvalidOperationException("native exterior ambient volume could not be restored");
                    Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] exterior bus restored previousDb={_previousAmbientVolume:F2}");
                }
                else Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] exterior bus changed externally to {current:F2} dB; retaining its current value");
                _ambientMixer = null;
            }

            void ApplySeason(ESeasonStatus season)
            {
                int audible = 0, silent = 0;
                foreach (var player in _players.OfType<SeasonAmbientSoundPlayer>())
                {
                    player.Stop(true);
                    if (player._seasonAmbientBanks.TryGetValue(season, out var bank) && bank != null)
                    {
                        player.SetSeasonStatus(season);
                        player.Play(); audible++;
                    }
                    else { player.CurrentBank = null; silent++; }
                }
                _season = season;
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] season={season} seasonalStarted={audible} seasonalSilent={silent}");
            }

            internal void Report(bool detailed)
            {
                if (!_ready || _disposed) return;
                int playing = 0, advanced = 0;
                var listener = Singleton<AudioListenerConsistencyManager>.Instantiated ? Singleton<AudioListenerConsistencyManager>.Instance : null;
                if (detailed || _reports < 4)
                {
                    var audio = BetterAudio.Instantiated ? BetterAudio.Instance : null;
                    var data = audio?.AudioMixerData;
                    if (audio?.Master != null && data != null)
                        Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] mix listenerVolume={AudioListener.volume:F3} paused={AudioListener.pause} mainDb={MixerValue(audio.Master, data.MainMixerVolume)} inGameDb={MixerValue(audio.Master, data.InGameVolumeMixer)} ambientOutDb={MixerValue(audio.Master, data.AmbientOutMixerVolume)} worldDb={MixerValue(audio.Master, data.WorldMixerVolume)}");
                }
                foreach (var player in _players)
                {
                    if (player == null) continue;
                    var source = player.Source;
                    if (source.isPlaying)
                    {
                        playing++;
                        if (_samples.TryGetValue(source, out var previous) && previous.Item1 == source.clip && previous.Item2 != source.timeSamples) advanced++;
                    }
                    _samples[source] = Tuple.Create(source.clip, source.timeSamples);
                    if (detailed || (source.isPlaying && _reports < 4))
                    {
                        float distance = listener != null ? Vector3.Distance(source.transform.position, listener.transform.position) : float.NaN;
                        float energy = 0f;
                        if (source.isPlaying)
                        {
                            source.GetOutputData(_outputSamples, 0);
                            foreach (float sample in _outputSamples) energy += sample * sample;
                        }
                        float rms = Mathf.Sqrt(energy / _outputSamples.Length);
                        Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] source='{player.name}' clip='{source.clip?.name}' playing={source.isPlaying} virtual={source.isVirtual} sample={source.timeSamples} rms={rms:F6} volume={source.volume:F3} pitch={source.pitch:F3} muted={source.mute} distance={distance:F2} mixer={source.outputAudioMixerGroup?.name} pos={source.transform.position}");
                    }
                }
                Vector3 emitter = _water != null ? _water.EmitterPosition : Vector3.zero;
                float moved = _hasEmitterPosition ? Vector3.Distance(emitter, _previousEmitter) : 0f;
                _previousEmitter = emitter; _hasEmitterPosition = true;
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] playback sources={_players.Length} playing={playing} advancing={advanced} shorelinePosition={emitter} moved={moved:F2} season={_season}");
            }

            static string MixerValue(AudioMixer mixer, string parameter) =>
                mixer.GetFloat(parameter, out float value) ? value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";

            internal void Dispose()
            {
                if (_disposed) return;
                _disposed = true; _ready = false;
                int stopped = 0, emptyClips = 0, failures = 0;
                try
                {
                    foreach (var player in _players)
                    {
                        if (player == null) continue;
                        try
                        {
                            // Retain before Stop: a failing native callback cannot lose the allocation.
                            if (player.EmptyClip != null && !_emptyClips.Contains(player.EmptyClip)) _emptyClips.Add(player.EmptyClip);
                            // Inactive/uninitialized players have no Fader and must not execute stock Stop.
                            if (player.Fader != null) { player.Stop(true); stopped++; }
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            Plugin.Log.LogWarning("[ArenaMapPort][NativeAudio] source stop failed; continuing owned cleanup: " + ex.Message);
                            // Do not leave the source audible if a stock culling/event callback failed.
                            try { var source = player.GetComponent<AudioSource>(); if (source != null) source.Stop(); }
                            catch (Exception) { /* Root destruction below still removes this source. */ }
                        }
                    }
                }
                finally
                {
                    try { ReleaseAmbientVolume(); }
                    catch (Exception ex) { failures++; Plugin.Log.LogWarning("[ArenaMapPort][NativeAudio] mixer cleanup failed: " + ex.Message); }
                    foreach (var clip in _emptyClips)
                    {
                        try { if (clip != null) { UnityEngine.Object.Destroy(clip); emptyClips++; } }
                        catch (Exception ex) { failures++; Plugin.Log.LogWarning("[ArenaMapPort][NativeAudio] empty clip cleanup failed: " + ex.Message); }
                    }
                    _emptyClips.Clear();
                    _samples.Clear();
                    if (_root != null)
                    {
                        var ownedRoot = _root;
                        _root = null;
                        try { ownedRoot.SetActive(false); }
                        catch (Exception ex) { failures++; Plugin.Log.LogWarning("[ArenaMapPort][NativeAudio] root deactivation failed: " + ex.Message); }
                        finally
                        {
                            // Stock calculator OnDestroy completes its job and disposes both NativeArrays.
                            // Bank/clip assets are bundle-owned and are never destroyed by this driver.
                            try { UnityEngine.Object.Destroy(ownedRoot); }
                            catch (Exception ex) { failures++; Plugin.Log.LogWarning("[ArenaMapPort][NativeAudio] root destruction failed: " + ex.Message); }
                        }
                    }
                }
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] stopped ownedPlayers={stopped} emptyClipsReleased={emptyClips} cleanupFailures={failures}; shoreline disposal scheduled");
            }

            void OnDestroy() { Dispose(); if (_driver == this) _driver = null; }
        }
    }
}
