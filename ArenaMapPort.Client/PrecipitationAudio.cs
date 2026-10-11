using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArenaMapPort.Shared;
using Audio;
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.Data;
using Audio.Extensions;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The harvested Weather root renders rain, but native rain sound lives in a separate
    /// Sound scene. Restore only its precipitation beds on the two supported maps, using
    /// EFT's own season bank, room levels and rain buses. No ambient singleton or mixer
    /// effects controller is created. See docs/progress/equator-rain-accents.md for evidence.
    /// </summary>
    internal static class PrecipitationAudio
    {
        const string Tag = "[ArenaMapPort][RainAudio]";
        const string BundleKey = "assets/content/audio/data/soundcontainers/precipitationsoundstorageso.bundle";
        static readonly Dictionary<string, ConfigEntry<float>> Volumes = new Dictionary<string, ConfigEntry<float>>();
        static PrecipitationSoundStorageSO _cachedBank;
        static Task<PrecipitationSoundStorageSO> _bankLoad;
        static Driver _driver;

        internal static void Install()
        {
            if (_driver != null) return;
            foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5 })
                Volumes[map.Key] = Plugin.Instance.Settings.Bind("Audio (" + map.Key + ")", "RainVolume", 1f,
                    new ConfigDescription("Volume of native rain/snow ambience only. 1 = native full level, 0 = silent. " +
                        "Follows actual weather, season and acoustic room; live. Existing scenery ambience is unchanged.",
                        new AcceptableValueRange<float>(0f, 1f)));
            _driver = Plugin.Instance.gameObject.AddComponent<Driver>();
        }

        internal static void Probe()
        {
            if (!DeveloperOptions.Enabled) return;
            if (_driver != null) _driver.Report();
        }

        static Task<PrecipitationSoundStorageSO> Bank()
        {
            if (_cachedBank != null) return Task.FromResult(_cachedBank);
            if (_bankLoad?.Status == TaskStatus.RanToCompletion && _bankLoad.Result == null) _bankLoad = null;
            if (_bankLoad == null || _bankLoad.IsFaulted || _bankLoad.IsCanceled) _bankLoad = LoadBank();
            return _bankLoad;
        }

        static async Task<PrecipitationSoundStorageSO> LoadBank()
        {
            // Most raids already reference this shared native asset. A direct bank load is
            // a bounded fallback; loading Reserve_Sound would create a conflicting manager.
            var existing = Resources.FindObjectsOfTypeAll<PrecipitationSoundStorageSO>().Where(Usable).ToArray();
            if (existing.Length == 1) return _cachedBank = existing[0];
            var factory = Singleton<ObjectsFactory>.Instance;
            if (factory == null) throw new InvalidOperationException("Native asset loader is unavailable");
            var assets = factory.EasyAssets;
            var retained = assets.Retain(BundleKey, null, null);
            try
            {
                await retained.LoadingJob;
                var bank = assets.GetAsset<PrecipitationSoundStorageSO>(BundleKey);
                if (!Usable(bank)) throw new InvalidOperationException("Native precipitation bank has no complete summer rain set");
                // Process-wide borrowed assets only. A raid ending during this await cannot create
                // sources or touch a mixer; the driver checks its current owner before using it.
                return _cachedBank = bank;
            }
            catch { retained.Release(); throw; }
        }

        static bool Usable(PrecipitationSoundStorageSO bank)
        {
            if (bank == null) return false;
            foreach (var intensity in new[] { RainController.ERainIntensity.Low, RainController.ERainIntensity.Med, RainController.ERainIntensity.High })
                foreach (var environment in new[] { EnvironmentType.Outdoor, EnvironmentType.Indoor })
                    if (!bank.TryGetClip(ESeasonStatus.Summer, intensity, environment, out var clip) || clip == null) return false;
            return true;
        }

        public sealed class Driver : MonoBehaviour
        {
            string _mapKey;
            AbstractGame _game;
            int _sceneHandle;
            float _startedAt, _nextForeignCheck;
            bool _failed, _foreign, _refresh;
            Task<PrecipitationSoundStorageSO> _pendingBank;
            PrecipitationSoundStorageSO _bank;
            GameObject _root;
            Layer _outdoor, _indoor;
            AudioMixer _mixer;
            AmbientBusLease _outdoorBus, _indoorBus;
            RoomPrecipitationCalculator _calculator;
            ISpatialAudioRoom _room;
            ClientAudioSettings.SeasonAmbientSettings _seasonSettings;
            ESeasonStatus _season;
            bool _hasSeason;
            RainController.ERainIntensity _intensity;
            EnvironmentType _environment;
            EnvironmentType _roomEnvironment;
            readonly PrecipitationCover _cover = new PrecipitationCover();
            float _roomLevel = 1f;
            readonly float[] _samples = new float[512];

            void Awake()
            {
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                AudioSettings.OnAudioConfigurationChanged += OnAudioChanged;
            }

            void OnSceneUnloaded(Scene scene)
            {
                if (_mapKey != null && scene.handle == _sceneHandle) Reset();
            }

            void OnAudioChanged(bool _) { _refresh = true; }

            void Update()
            {
                var map = ArenaMapRaid.CurrentMap;
                var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
                var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                if (map == null || !Volumes.ContainsKey(map.Key) || game == null || game.Status != GameStatus.Started || world?.MainPlayer == null)
                {
                    if (_mapKey != null) Reset();
                    return;
                }
                var scene = SceneManager.GetSceneByName(map.ScriptsSceneName);
                if (_mapKey != map.Key || _sceneHandle != scene.handle || !ReferenceEquals(_game, game))
                {
                    Reset();
                    _mapKey = map.Key; _sceneHandle = scene.handle; _game = game;
                    _startedAt = Time.realtimeSinceStartup;
                }
                if (_failed) return;
                try
                {
                    if (_refresh)
                    {
                        _refresh = false;
                        ClearPlayback();
                        _startedAt = Time.realtimeSinceStartup;
                    }
                    if (Time.realtimeSinceStartup >= _nextForeignCheck)
                    {
                        _nextForeignCheck = Time.realtimeSinceStartup + 1f;
                        bool foreign = MonoBehaviourSingleton<AmbientAudioSystem>.Instantiated ||
                            Object.FindObjectsOfType<PrecipitationAmbientBlender>().Any(b => b.isActiveAndEnabled);
                        if (foreign && !_foreign)
                        {
                            ClearPlayback();
                            Plugin.Log.LogDebug($"{Tag} {_mapKey}: native ambient owner present; yielding precipitation playback");
                        }
                        if (!foreign && _foreign) _startedAt = Time.realtimeSinceStartup;
                        _foreign = foreign;
                    }
                    if (_foreign) return;
                    if (_root == null)
                    {
                        if (!TryStart())
                        {
                            if (Time.realtimeSinceStartup - _startedAt > 60f)
                                throw new InvalidOperationException("Precipitation services/bank unavailable after 60 seconds");
                            return;
                        }
                    }
                    Tick();
                }
                catch (Exception e)
                {
                    _failed = true;
                    ClearPlayback();
                    Plugin.Log.LogError($"{Tag} {_mapKey}: precipitation stopped for this raid: {e}");
                }
            }

            bool TryStart()
            {
                if (!MonoBehaviourSingleton<BetterAudio>.Instantiated ||
                    !Singleton<AudioListenerConsistencyManager>.Instantiated || Seasons.Controller == null) return false;
                var listener = Singleton<AudioListenerConsistencyManager>.Instance;
                if (listener == null || listener.transform.position.y < -500f) return false;
                _mixer = MonoBehaviourSingleton<BetterAudio>.Instance.Master;
                if (_mixer == null) return false;
                if (_pendingBank == null) _pendingBank = Bank();
                if (!_pendingBank.IsCompleted) return false;
                // GetResult observes faults once; Update disables only this raid on failure.
                _bank = _pendingBank.GetAwaiter().GetResult();
                if (_bank == null) throw new InvalidOperationException("Loaded precipitation bank was released");
                var outside = Group("AmbientOut/PrecipitationAmbientOut", "PrecipitationAmbientOut");
                // The effect-free native PrecipitationAmbientIn child is -80 dB in the
                // Outdoor gameplay snapshot. Imported acoustic rooms can still be indoor
                // there. Our source gain already owns that room blend; use the immediate
                // effect-free parent to bypass only this hidden gate. All downstream
                // ambient/headphone/master processing remains on the original path.
                var inside = MonoBehaviourSingleton<BetterAudio>.Instance.AmbientInMixer;
                if (outside == null || inside == null) throw new InvalidOperationException("Native precipitation mixer routes are unavailable");
                _outdoorBus = AmbientBusLease.Acquire(_mixer, "AmbientOutVolume");
                _indoorBus = AmbientBusLease.Acquire(_mixer, "AmbientInVolume");
                _root = new GameObject("ArenaMapPort_PrecipitationAudio");
                _root.transform.SetParent(transform, false);
                _outdoor = new Layer(_root.transform, "Outdoor", outside, 1f / .33f);
                _indoor = new Layer(_root.transform, "Indoor", inside, 1f);
                _calculator = new RoomPrecipitationCalculator();
                Plugin.Log.LogDebug($"{Tag} {_mapKey}: ready after raid audio reset; native bank='{_bank.name}', four owned 2D sources");
                return true;
            }

            AudioMixerGroup Group(string path, string name)
            {
                var groups = _mixer.FindMatchingGroups(path).Where(g => g != null && g.name == name).ToArray();
                return groups.Length == 1 ? groups[0] : null;
            }

            void Tick()
            {
                // Observe ownership each frame without reasserting over an external mix change.
                _ = _outdoorBus.IsNeutral;
                _ = _indoorBus.IsNeutral;
                var spatial = NativeSpatialAudio.Current;
                var room = spatial != null && spatial.MapKey == _mapKey && spatial.Finished && !spatial.Released &&
                    SpatialAudioSystem.Initialized ? spatial.System?.ListenerCurrentRoom : null;
                // EmptyAudioRoom intentionally has IsValid=false, ID=-1 and Outdoor type.
                // Its classification remains authoritative, but it carries no ambient data.
                if (room != null && room.Type == EAudioRoomTypeMask.None) room = null;
                if (!ReferenceEquals(_room, room))
                {
                    _room = room;
                    _calculator.UpdateRoom(room?.RoomAmbientData != null ? room : null);
                }
                _roomEnvironment = room != null ? (room.Type.IsOutdoor() ? EnvironmentType.Outdoor : EnvironmentType.Indoor) :
                    (GamePlayerOwner.MyPlayer?.Environment ?? EnvironmentType.Outdoor);
                _intensity = RainController.IntensityType;
                var listener = Singleton<AudioListenerConsistencyManager>.Instantiated ?
                    Singleton<AudioListenerConsistencyManager>.Instance : null;
                bool? covered = null;
                if (_intensity != RainController.ERainIntensity.None && listener != null && listener.transform.position.y >= -500f)
                    covered = _cover.Sample(ArenaMapRaid.CurrentMap, listener.transform.position, Time.realtimeSinceStartup);
                else _cover.Dispose(); // No CPU roof copy needed in dry weather or during camera reset.
                _environment = covered.HasValue ? (covered.Value ? EnvironmentType.Indoor : EnvironmentType.Outdoor) : _roomEnvironment;
                var season = Seasons.Controller.Status;
                if (!_hasSeason || season != _season)
                {
                    _season = season; _hasSeason = true;
                    _seasonSettings = Singleton<ClientSettingsConfig>.Instantiated ?
                        Singleton<ClientSettingsConfig>.Instance.AudioSettings.EnvironmentSettings.GetSeasonSettings(season) :
                        new ClientAudioSettings.SeasonAmbientSettings();
                }
                var ambient = room?.RoomAmbientData;
                float roomVolume = ambient == null ? 1f : ambient.UseVolumeRange ?
                    _calculator.CalculateNormalizedVolumeByRange() : ambient.PrecipitationVolume;
                if (float.IsNaN(roomVolume) || float.IsInfinity(roomVolume)) roomVolume = 0f;
                _roomLevel = Mathf.Clamp01(roomVolume);
                float gain = Volumes[_mapKey].Value;
                float outside = _environment == EnvironmentType.Outdoor ? gain * _seasonSettings.GetRainMult(EnvironmentType.Outdoor, _intensity) : 0f;
                float inside = _environment == EnvironmentType.Indoor ? gain * _roomLevel *
                    _seasonSettings.GetRainMult(EnvironmentType.Indoor, _intensity) : 0f;
                _outdoor.Tick(Clip(EnvironmentType.Outdoor), outside, Time.deltaTime, .5f);
                _indoor.Tick(Clip(EnvironmentType.Indoor), inside, Time.deltaTime, .1f);
            }

            AudioClip Clip(EnvironmentType environment)
            {
                if (_intensity == RainController.ERainIntensity.None) return null;
                // An indoor acoustic room may contain open sky. Its authored muffled
                // preset is valid only for the bed matching that room's environment.
                var preset = environment == _roomEnvironment ? _room?.RoomAmbientData?.SeasonSoundPreset : null;
                if (preset != null && preset._content != null && preset.TryGetContent(_season, out var content) &&
                    content.TryGetValue(_intensity, out var roomClip)) return roomClip;
                return _bank.TryGetClip(_season, _intensity, environment, out var clip) ? clip : null;
            }

            internal void Report()
            {
                if (_mapKey == null) return;
                float listenerEnergy = 0f;
                if (_root != null)
                {
                    AudioListener.GetOutputData(_samples, 0);
                    foreach (float sample in _samples) listenerEnergy += sample * sample;
                }
                Plugin.Log.LogDebug($"{Tag} map={_mapKey} ready={_root != null} foreign={_foreign} failed={_failed} " +
                    $"rain={RainController.Intensity:F3}/{RainController.IntensityType} season={_season} " +
                    $"room={_room?.ID}/{_room?.Type} environment={_environment} playerEnvironment={GamePlayerOwner.MyPlayer?.Environment} " +
                    $"rainCover={(_cover.Covered.HasValue ? (_cover.Covered.Value ? "covered" : "open") : "room-fallback")} coveredSamples={_cover.CoveredSamples}/9 depthCaptures={_cover.Captures} " +
                    $"roomLevel={_roomLevel:F3} ranged={_room?.RoomAmbientData?.UseVolumeRange} " +
                    $"setting={Volumes[_mapKey].Value:F3} ambientInDb={Bus("AmbientInVolume")} ambientOutDb={Bus("AmbientOutVolume")} " +
                    $"ambientDb={Bus("AmbientVolume")} headphonesDb={Bus("HeadphonesMixerVolume")} " +
                    $"listenerRms={Mathf.Sqrt(listenerEnergy / _samples.Length):F6} listenerVolume={AudioListener.volume:F3} paused={AudioListener.pause}");
                _outdoor?.Report(_samples);
                _indoor?.Report(_samples);
            }

            string Bus(string parameter) => _mixer != null && _mixer.GetFloat(parameter, out float value) ?
                value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "missing";

            void ClearPlayback()
            {
                _cover.Dispose();
                _outdoor?.Stop(); _outdoor = null;
                _indoor?.Stop(); _indoor = null;
                if (_root != null) { _root.SetActive(false); Object.Destroy(_root); _root = null; }
                _calculator?.Dispose(); _calculator = null; _room = null;
                _outdoorBus?.Dispose(); _outdoorBus = null;
                _indoorBus?.Dispose(); _indoorBus = null;
                _mixer = null; _bank = null; _hasSeason = false;
            }

            void Reset()
            {
                bool hadPlayback = _root != null;
                ClearPlayback();
                if (hadPlayback) Plugin.Log.LogDebug($"{Tag} {_mapKey}: released four sources and ambient bus leases");
                _mapKey = null; _game = null; _sceneHandle = 0;
                _pendingBank = null; _failed = false; _foreign = false; _nextForeignCheck = 0f;
            }

            void OnDestroy()
            {
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                AudioSettings.OnAudioConfigurationChanged -= OnAudioChanged;
                Reset();
                if (_driver == this) _driver = null;
            }
        }

        /// <summary>
        /// Native two-source linear clip fade with an independently smoothed room gain.
        /// Keeping both weights avoids a native coroutine fighting live volume updates.
        /// Only one environment bed reaches full level; both parents may be leased by scenery.
        /// </summary>
        sealed class Layer
        {
            readonly AudioSource[] _sources = new AudioSource[2];
            readonly float[] _weights = new float[2], _from = new float[2];
            readonly float _duration;
            AudioClip _requested;
            int _target = -1;
            float _elapsed, _gain;

            internal Layer(Transform parent, string name, AudioMixerGroup mixer, float duration)
            {
                _duration = duration;
                for (int i = 0; i < 2; i++)
                {
                    var go = new GameObject(name + " rain " + (i + 1));
                    go.transform.SetParent(parent, false);
                    var source = go.AddComponent<AudioSource>();
                    source.playOnAwake = false; source.loop = true; source.volume = 0f;
                    source.spatialBlend = 0f; source.spatialize = false; source.priority = 128;
                    source.outputAudioMixerGroup = mixer;
                    _sources[i] = source;
                }
            }

            internal void Tick(AudioClip clip, float targetGain, float dt, float environmentFade)
            {
                if (clip != _requested)
                {
                    _requested = clip;
                    _elapsed = 0f;
                    _target = -1;
                    if (clip != null)
                    {
                        _target = Array.FindIndex(_sources, s => s.clip == clip);
                        if (_target < 0)
                        {
                            _target = _weights[0] <= _weights[1] ? 0 : 1;
                            var source = _sources[_target];
                            source.Stop(); source.clip = clip; source.volume = 0f; _weights[_target] = 0f;
                            if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                        }
                    }
                    Array.Copy(_weights, _from, 2);
                }
                if (float.IsNaN(targetGain) || float.IsInfinity(targetGain)) targetGain = 0f;
                _gain = Mathf.MoveTowards(_gain, Mathf.Clamp01(targetGain), dt / environmentFade);
                _elapsed += dt;
                float t = Mathf.Clamp01(_elapsed / _duration);
                for (int i = 0; i < 2; i++)
                {
                    var source = _sources[i];
                    _weights[i] = Mathf.Lerp(_from[i], i == _target ? 1f : 0f, t);
                    source.volume = _weights[i] * _gain;
                    if ((i == _target || _weights[i] > 0f) && source.clip != null && !source.isPlaying && !AudioListener.pause &&
                        source.clip.loadState == AudioDataLoadState.Loaded)
                    {
                        source.time = UnityEngine.Random.Range(0f, source.clip.length);
                        source.Play();
                    }
                    else if (_weights[i] <= 0f && i != _target)
                    {
                        source.Stop(); source.clip = null;
                    }
                }
            }

            internal void Report(float[] samples)
            {
                foreach (var source in _sources)
                {
                    float energy = 0f;
                    if (source.isPlaying)
                    {
                        source.GetOutputData(samples, 0);
                        foreach (float sample in samples) energy += sample * sample;
                    }
                    Plugin.Log.LogDebug($"{Tag} source='{source.name}' id={source.GetInstanceID()} clip='{source.clip?.name}' " +
                        $"playing={source.isPlaying} virtual={source.isVirtual} samples={source.timeSamples} volume={source.volume:F4} " +
                        $"route={source.outputAudioMixerGroup?.name} rms={Mathf.Sqrt(energy / samples.Length):F6} " +
                        $"loaded={source.clip?.loadState} spatial={source.spatialBlend:F1} muted={source.mute}");
                }
            }

            internal void Stop()
            {
                foreach (var source in _sources)
                    if (source != null) { source.Stop(); source.clip = null; }
            }
        }
    }
}
