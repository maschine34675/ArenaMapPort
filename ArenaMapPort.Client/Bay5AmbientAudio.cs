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
    /// <summary>Starts Bay5/Equator imported stock ambient players once real raid audio services exist.
    /// Arena's ambient singleton/room system is deliberately not recreated. Stock players retain
    /// their original clip selection, gain, pitch and timing; Bay5's stock shoreline calculator
    /// ticks from this owned driver. Per-map host acceptance lives in native-features progress.</summary>
    internal static class Bay5AmbientAudio
    {
        internal const string RootName = "ArenaMapPort_Bay5NativeAudio";
        internal const string EquatorRootName = "ArenaMapPort_EquatorNativeAudio";
        static Driver _driver;
        static ConfigEntry<bool> _bay5Diagnostics, _equatorDiagnostics;
        // PLAN-TWO-MAPS item 1 (2026-09-25): the user hears the ported ambience as too quiet. BaseAmbientSoundPlayer
        // draws GetBaseVolume() = _volume + Random.Range(-_deltaVolume, 0) per raid; 13 of Equator's 25 authored
        // players have _deltaVolume > _volume, so a draw can clamp them to silence. The accepted release mix uses
        // a dB gain that scales _volume and _deltaVolume together, and a
        // linear offset added to _volume (raises the floor). Whenever either is non-neutral, _deltaVolume is also
        // capped at _volume so no draw can fall below zero. Applied once per raid to the scene's own player
        // instances right before they start (the scene is unloaded with the raid, nothing accumulates).
        static ConfigEntry<float> _bay5GainDb, _equatorGainDb, _bay5Offset, _equatorOffset;
        // 2026-09-25 user report: even at AmbientGainDb=+12 / AmbientVolumeOffset=1 the imported ambience is only
        // audible right next to a source. Cause (decompile BaseAmbientSoundPlayer.Awake, lines 250-261): the
        // authored custom rolloff curve (1.0 at 0.2 %, 0.27 at 19 %, 0 at 100 % of _maxDistance; Equator players
        // mostly 10-20 m) is baked into the AudioSource once at Awake, and gain/offset only scale the pre-attenuation
        // volume. This multiplier stretches _maxDistance BEFORE the graph is activated (before that Awake), so the
        // same curve shape reaches further. 1 = original Arena range.
        static ConfigEntry<float> _bay5Range, _equatorRange;

        // Bind both maps before the initial F12 visibility pass, even when no raid has
        // loaded yet. This also applies the accepted audio migration before map cleanup.
        internal static void BindConfig()
        {
            if (_equatorDiagnostics == null)
            {
                AmbientAudioDefaults.Values("arena_equator", out float gain, out float offset, out float range);
                _equatorDiagnostics = Plugin.Instance.Settings.Bind("Audio (arena_equator)", "PlaybackDiagnostics", false,
                    "Log native indoor ambient clip/sample evidence while in Equator. Takes effect immediately; F10 also logs a snapshot.");
                _equatorGainDb = Plugin.Instance.Settings.Bind("Audio (arena_equator)", "AmbientGainDb", gain,
                    new ConfigDescription("Gain in dB applied to the imported ambient sources' authored volume (and their random dip) on Equator. 0 dB applies no gain to authored sources; the volume offset and range still apply. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(-12f, 12f)));
                _equatorOffset = Plugin.Instance.Settings.Bind("Audio (arena_equator)", "AmbientVolumeOffset", offset,
                    new ConfigDescription("Linear volume added to every imported ambient source on Equator (raises the floor so quiet sources cannot dip to silence). 0 = no additive volume floor. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(0f, 1f)));
                SubscribeLive(_equatorGainDb, _equatorOffset, "arena_equator");
                _equatorRange = Plugin.Instance.Settings.Bind("Audio (arena_equator)", "AmbientRangeMultiplier", range,
                    new ConfigDescription("Multiplies every imported ambient source's Max Distance on Equator (the authored rolloff curve keeps its shape, so sources stay audible further away; most Equator sources are authored at 10-20 m). 1 = original Arena range. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(1f, 8f)));
                _equatorRange.SettingChanged += (_, __) => ApplyLive("arena_equator");
                AmbientAudioDefaults.Migrate(Plugin.Instance.Settings, "arena_equator", _equatorGainDb, _equatorOffset, _equatorRange);
            }
            if (_bay5Diagnostics == null)
            {
                AmbientAudioDefaults.Values("arena_bay5", out float gain, out float offset, out float range);
                _bay5Diagnostics = Plugin.Instance.Settings.Bind("Audio (arena_bay5)", "PlaybackDiagnostics", false,
                    "Log native ambient clip/sample and shoreline movement evidence while in Bay5. Takes effect immediately; F10 also logs a snapshot.");
                _bay5GainDb = Plugin.Instance.Settings.Bind("Audio (arena_bay5)", "AmbientGainDb", gain,
                    new ConfigDescription("Gain in dB applied to the imported ambient sources' authored volume (and their random dip) on Bay5. 0 dB applies no gain to authored sources; the volume offset and range still apply. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(-12f, 12f)));
                _bay5Offset = Plugin.Instance.Settings.Bind("Audio (arena_bay5)", "AmbientVolumeOffset", offset,
                    new ConfigDescription("Linear volume added to every imported ambient source on Bay5 (raises the floor so quiet sources cannot dip to silence). 0 = no additive volume floor. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(0f, 1f)));
                SubscribeLive(_bay5GainDb, _bay5Offset, "arena_bay5");
                _bay5Range = Plugin.Instance.Settings.Bind("Audio (arena_bay5)", "AmbientRangeMultiplier", range,
                    new ConfigDescription("Multiplies every imported ambient source's Max Distance on Bay5 (the authored rolloff curve keeps its shape, so sources stay audible further away). 1 = original Arena range. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(1f, 8f)));
                _bay5Range.SettingChanged += (_, __) => ApplyLive("arena_bay5");
                AmbientAudioDefaults.Migrate(Plugin.Instance.Settings, "arena_bay5", _bay5GainDb, _bay5Offset, _bay5Range);
            }
        }

        internal static void Ensure()
        {
            string mapKey = ArenaMapRaid.CurrentMap?.Key;
            if (_driver != null || (mapKey != "arena_bay5" && mapKey != "arena_equator")) return;
            BindConfig();
            bool equator = mapKey == "arena_equator";
            var scene = SceneManager.GetSceneByName(ArenaMapRaid.CurrentMap.ScriptsSceneName);
            var roots = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                .Where(t => t.name == (equator ? EquatorRootName : RootName)).ToArray();
            if (roots.Length == 0) return; // Old packages do not contain this optional feature.
            if (roots.Length != 1) { Plugin.Log.LogError("[ArenaMapPort][NativeAudio] duplicate owned audio roots; refusing playback"); return; }
            _driver = Plugin.Instance.gameObject.AddComponent<Driver>();
            _driver.Initialize(roots[0].gameObject, mapKey, equator ? _equatorDiagnostics : _bay5Diagnostics,
                equator ? _equatorGainDb : _bay5GainDb, equator ? _equatorOffset : _bay5Offset,
                equator ? _equatorRange : _bay5Range);
        }

        // 2026-09-25 user request: tuning needs the keys to act in the running raid. Bound once per session (the
        // entries outlive every Driver), so the handlers reach the CURRENT driver through the static field.
        static void SubscribeLive(ConfigEntry<float> gainDb, ConfigEntry<float> offset, string mapKey)
        {
            gainDb.SettingChanged += (_, __) => ApplyLive(mapKey);
            offset.SettingChanged += (_, __) => ApplyLive(mapKey);
        }

        static void ApplyLive(string mapKey)
        {
            var driver = _driver;
            if (driver == null || driver.MapKey != mapKey) return;
            try { driver.ApplyUserSettingsLive(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[ArenaMapPort][NativeAudio] live setting apply failed map={mapKey}: {ex.Message}"); }
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

        internal static AudioMixerGroup CurrentEffectsMixer(string mapKey) =>
            _driver != null && _driver.MapKey == mapKey ? _driver.EffectsMixer : null;

        internal static void RestoreReverbRouting() { if (_driver != null) _driver.RefreshReverbRouting(false); }

        public sealed class Driver : MonoBehaviour
        {
            GameObject _root;
            string _mapKey;
            bool _equator;
            ConfigEntry<bool> _diagnostics;
            ConfigEntry<float> _gainDb, _offset, _range;
            internal string MapKey => _mapKey;
            // Authored (pre-user-mix) values per player: every apply computes from these, never from the fields it
            // wrote last time, so raid-start and live re-applies are idempotent.
            struct Baseline { public float Volume, Delta, MaxDistance; }
            readonly Dictionary<BaseAmbientSoundPlayer, Baseline> _baseline = new Dictionary<BaseAmbientSoundPlayer, Baseline>();
            BaseAmbientSoundPlayer[] _players = Array.Empty<BaseAmbientSoundPlayer>();
            BaseAmbientSoundPlayer[] _activePlayers = Array.Empty<BaseAmbientSoundPlayer>();
            AmbientSoundPlayerGroup[] _groups = Array.Empty<AmbientSoundPlayerGroup>();
            AmbientSplineEmitterController[] _controllers = Array.Empty<AmbientSplineEmitterController>();
            AmbientPlayerSplineMappedEmitter _water;
            readonly Dictionary<AudioSource, Tuple<AudioClip, int>> _samples = new Dictionary<AudioSource, Tuple<AudioClip, int>>();
            readonly List<AudioClip> _emptyClips = new List<AudioClip>();
            readonly float[] _outputSamples = new float[256];
            AudioMixer _ambientMixer;
            string _ambientVolumeParameter;
            AmbientBusLease _ambientBus;
            AmbientSoundPlayerGroup _effectsGroup;
            AudioMixerGroup _originalEffects, _reverbEffects;
            bool _reverbRouted, _reverbRouteYielded;
            internal AudioMixerGroup EffectsMixer => _effectsGroup != null ? _effectsGroup._outputMixerGroup : null;
            float _servicesSince = -1f, _nextReport, _nextReadyCheck;
            int _reports;
            bool _ready, _disposed;
            ESeasonStatus _season;
            Vector3 _previousEmitter;
            bool _hasEmitterPosition;

            internal void Initialize(GameObject root, string mapKey, ConfigEntry<bool> diagnostics,
                ConfigEntry<float> gainDb, ConfigEntry<float> offset, ConfigEntry<float> range)
            {
                _root = root;
                _mapKey = mapKey; _equator = mapKey == "arena_equator"; _diagnostics = diagnostics;
                _gainDb = gainDb; _offset = offset; _range = range;
                _players = root.GetComponentsInChildren<BaseAmbientSoundPlayer>(true);
                foreach (var p in _players)
                    if (p != null) _baseline[p] = new Baseline { Volume = p._volume, Delta = p._deltaVolume, MaxDistance = p._maxDistance };
                _groups = root.GetComponentsInChildren<AmbientSoundPlayerGroup>(true);
                _controllers = root.GetComponentsInChildren<AmbientSplineEmitterController>(true);
                _water = root.GetComponentInChildren<AmbientPlayerSplineMappedEmitter>(true);
                try
                {
                    ValidateGraph();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[ArenaMapPort][NativeAudio] invalid imported graph map={_mapKey}; refusing playback: {ex.Message}");
                    Dispose();
                    return;
                }
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] waiting for raid listener and native mixers; map={_mapKey} players={_players.Length} active={_activePlayers.Length}");
            }

            void ValidateGraph()
            {
                if (_root.activeSelf || _players.Length != (_equator ? 25 : 24) ||
                    _groups.Length != (_equator ? 1 : 2) || _controllers.Length != (_equator ? 0 : 1) ||
                    (_equator ? _water != null : _water == null) ||
                    _root.GetComponentsInChildren<AudioSource>(true).Length != _players.Length)
                    throw new InvalidOperationException("native graph census mismatch");
                if (_groups.Any(g => !g.enabled || !ActiveUnderOwnedRoot(g.transform) || g._soundPlayers == null))
                    throw new InvalidOperationException("inactive or incomplete native player group");
                _activePlayers = _groups.SelectMany(g => g._soundPlayers).ToArray();
                var active = _players.Where(p => p.enabled && ActiveUnderOwnedRoot(p.transform) &&
                    p.GetComponent<AudioSource>() != null && p.GetComponent<AudioSource>().enabled).ToArray();
                if (_activePlayers.Length != (_equator ? 23 : 24) || _activePlayers.Any(p => p == null) ||
                    _activePlayers.Distinct().Count() != _activePlayers.Length ||
                    !new HashSet<BaseAmbientSoundPlayer>(active).SetEquals(_activePlayers))
                    throw new InvalidOperationException("native group membership differs from enabled active players");
                // Retain Bay5's accepted hierarchy iteration order; Equator follows its
                // original active member list and never starts ungrouped descendants.
                if (!_equator) { _activePlayers = _players; return; }
                var inactive = _players.Where(p => !ActiveUnderOwnedRoot(p.transform)).ToArray();
                if (_groups[0].name != "CommonAmbientInGroup" || inactive.Length != 2 ||
                    !new HashSet<string>(inactive.Select(p => p.name)).SetEquals(new[] { "far_dogs1", "far_dogs2" }) ||
                    _players.Any(p => !(p is AmbientSoundPlayer) || !p.enabled || !p.GetComponent<AudioSource>().enabled) ||
                    _players.Count(p => p.name == "electricity") != 1 ||
                    _players.Any(p => p.GetComponent<AudioSource>().loop != (p.name == "electricity")))
                    throw new InvalidOperationException("Equator original activation/loop contract differs");
            }

            bool ActiveUnderOwnedRoot(Transform transform)
            {
                for (var current = transform; current != null && current != _root.transform; current = current.parent)
                    if (!current.gameObject.activeSelf) return false;
                return transform.IsChildOf(_root.transform);
            }

            void Update()
            {
                if (_disposed) return;
                if (_root == null || ArenaMapRaid.CurrentMap?.Key != _mapKey) { Shutdown(); return; }
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
                    if (!_equator && Seasons.Controller != null && Seasons.Controller.Status != _season)
                        ApplySeason(Seasons.Controller.Status);
                    foreach (var controller in _controllers) controller.Tick();
                    if (DeveloperOptions.Enabled && (_diagnostics?.Value ?? false) && Time.realtimeSinceStartup >= _nextReport)
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
                try
                {
                    RefreshReverbRouting(RoomReverb.IsActive(_mapKey));
                    foreach (var controller in _controllers) controller.LateTick();
                }
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
                if (listener == null || listener.transform.position.y < -500f || (!_equator && Seasons.Controller == null) || !BetterAudio.Instantiated)
                    return;
                var audio = BetterAudio.Instance;
                if (audio.Master == null || audio.AudioMixerData == null) return;
                var effects = _equator ? Mixer(audio.Master, "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects") :
                    Mixer(audio.Master, "AmbientOut/CommonAmbOutEffects", "CommonAmbOutEffects");
                var bypass = _equator ? null : Mixer(audio.Master, "CommonAmbOutBypass", "CommonAmbOutBypass");
                if (effects == null || (!_equator && bypass == null)) return;
                foreach (var group in _groups)
                {
                    if (group.name == (_equator ? "CommonAmbientInGroup" : "AmbientPlayersGroup"))
                    { group._outputMixerGroup = effects; _effectsGroup = group; }
                    else if (!_equator && group.name == "AmbientSplineEmitterWaterGroup") group._outputMixerGroup = bypass;
                    else throw new InvalidOperationException("unrecognized ambient group " + group.name);
                    group.SetMixerGroup();
                }
                AcquireAmbientVolume(audio);
                _originalEffects = effects;
                _reverbEffects = audio.CommonAmbientEffectsMixer;
                ApplyUserRange(); // must precede SetActive: BaseAmbientSoundPlayer.Awake bakes _maxDistance into the AudioSource once
                // All serialized fields and links exist before any stock Awake executes.
                _root.SetActive(true);
                foreach (var player in _players)
                    if (player.EmptyClip != null) _emptyClips.Add(player.EmptyClip);
                if (_activePlayers.Any(p => !p.isActiveAndEnabled || p.Fader == null || p.EmptyClip == null) ||
                    _players.Except(_activePlayers).Any(p => p.Fader != null || p.EmptyClip != null))
                    throw new InvalidOperationException("native initialization differs from active source graph");
                foreach (var controller in _controllers) controller.Init(listener.transform);
                if (!_equator) PrepareShoreline();
                _season = Seasons.Controller != null ? Seasons.Controller.Status : default(ESeasonStatus);
                ApplyUserMix();
                int started = 0, seasonalSilence = 0;
                foreach (var player in _activePlayers)
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
                RefreshReverbRouting(RoomReverb.IsActive(_mapKey));
                _nextReport = Time.realtimeSinceStartup + 5f;
                string mixers = effects.name + (bypass != null ? "," + bypass.name : "");
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] ready players={_players.Length} started={started} seasonalSilent={seasonalSilence} season={_season} mixers={mixers} shoreline={(_equator ? "none" : "stock")} listener={listener.transform.position} map={_mapKey} sourceInactive={_players.Length - _activePlayers.Length}");
            }

            internal void RefreshReverbRouting(bool requested)
            {
                if (!_equator || _effectsGroup == null || _originalEffects == null || _reverbEffects == null || _reverbRouteYielded) return;
                // EFT permanently mutes the original Returns/Reverb group. This original alternative
                // ambient bus reaches TarkovReverb. Its dry path is equivalent while the owned parent
                // gain is neutral (no skipped filters/effects); preserve external non-neutral gains.
                float volume = float.NaN;
                bool neutralParent = _ambientBus?.IsNeutral == true && _ambientMixer != null &&
                    _ambientMixer.GetFloat(_ambientVolumeParameter, out volume) && Mathf.Approximately(volume, 0f);
                bool useReverb = requested && neutralParent;
                if (useReverb == _reverbRouted) return;
                var expected = _reverbRouted ? _reverbEffects : _originalEffects;
                if (_effectsGroup._outputMixerGroup != expected || _effectsGroup._soundPlayers.Any(p =>
                    p != null && (p._mixerGroup != expected || p.GetComponent<AudioSource>()?.outputAudioMixerGroup != expected)))
                {
                    _reverbRouteYielded = true;
                    Plugin.Log.LogWarning(RoomReverb.Tag + " Equator ambient routing changed externally; retaining it");
                    return;
                }
                _effectsGroup._outputMixerGroup = useReverb ? _reverbEffects : _originalEffects;
                _effectsGroup.SetMixerGroup(); // Also updates live sources; no restart/random delay reset.
                _reverbRouted = useReverb;
                Plugin.Log.LogInfo($"{RoomReverb.Tag} Equator owned ambient route={_effectsGroup._outputMixerGroup.name}, " +
                    $"players={_effectsGroup._soundPlayers.Count}, original indoor bus={volume:F2}dB");
            }

            // See the field comment on _bay5Range. Runs on the still-inactive graph; 1.0 leaves every value untouched.
            void ApplyUserRange()
            {
                float m = _range?.Value ?? 1f;
                if (Mathf.Approximately(m, 1f) || m <= 0f)
                {
                    foreach (var player in _players)
                        if (player != null && _baseline.TryGetValue(player, out var b0)) player._maxDistance = b0.MaxDistance;
                    Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] user range neutral (original Arena max distances) map={_mapKey}");
                    return;
                }
                int adjusted = 0;
                var examples = new System.Text.StringBuilder();
                foreach (var player in _players)
                {
                    if (player == null) continue;
                    float before = _baseline.TryGetValue(player, out var b) ? b.MaxDistance : player._maxDistance;
                    player._maxDistance = Mathf.Clamp(before * m, player._minDistance + 0.1f, 1000f);
                    adjusted++;
                    if (adjusted <= 3) examples.Append($" '{player.name}' {before:F0}->{player._maxDistance:F0} m;");
                }
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] user range x{m:F2}: max distance scaled on {adjusted} player(s); e.g.{examples} map={_mapKey}");
            }

            // Live re-apply from a SettingChanged: recompute the per-player fields from the baseline (ApplyUserRange /
            // ApplyUserMix), then push them onto the sources that are already playing — BaseAmbientSoundPlayer only reads
            // _maxDistance in Awake and _volume in FadeIn (decompile), so a running source needs Source.maxDistance and
            // a Fader re-target to hear the change now (PlayCoroutine copies Fader.Volume into Source.volume every frame).
            internal void ApplyUserSettingsLive()
            {
                if (!_ready || _disposed) return;
                ApplyUserRange();
                ApplyUserMix();
                int pushed = 0;
                foreach (var player in _activePlayers)
                {
                    if (player == null || !player.isActiveAndEnabled || player.Source == null) continue;
                    player.Source.maxDistance = player._maxDistance;
                    if (player.IsPlaying && player.Fader != null)
                    {
                        player.Fader.FadeTo(player.GetBaseVolume(), 0.5f);
                        pushed++;
                    }
                }
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] live settings applied: gainDb={_gainDb?.Value ?? 0f:F1} offset={_offset?.Value ?? 0f:F3} range=x{_range?.Value ?? 1f:F2}; re-targeted {pushed} playing source(s), max distance pushed on {_activePlayers.Length} map={_mapKey}");
            }

            // PLAN-TWO-MAPS item 1 — see the field comment on _bay5GainDb. Neutral settings leave every value untouched.
            void ApplyUserMix()
            {
                float gainDb = _gainDb?.Value ?? 0f;
                float offset = _offset?.Value ?? 0f;
                if (Mathf.Approximately(gainDb, 0f) && Mathf.Approximately(offset, 0f))
                {
                    foreach (var player in _activePlayers)
                        if (player != null && _baseline.TryGetValue(player, out var b0)) { player._volume = b0.Volume; player._deltaVolume = b0.Delta; }
                    Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] user mix neutral (original Arena volumes) map={_mapKey}");
                    return;
                }
                float gain = Mathf.Pow(10f, gainDb / 20f);
                int adjusted = 0, capped = 0;
                var examples = new System.Text.StringBuilder();
                foreach (var player in _activePlayers)
                {
                    if (player == null) continue;
                    float volume = player._volume, delta = player._deltaVolume;
                    if (_baseline.TryGetValue(player, out var b)) { volume = b.Volume; delta = b.Delta; }
                    float newVolume = Mathf.Clamp01(volume * gain + offset);
                    float newDelta = Mathf.Max(0f, delta * gain);
                    if (newDelta > newVolume) { newDelta = newVolume; capped++; }
                    player._volume = newVolume;
                    player._deltaVolume = newDelta;
                    adjusted++;
                    if (adjusted <= 3)
                        examples.Append($" '{player.name}' {volume:F3}-{delta:F3}->{newVolume:F3}-{newDelta:F3};");
                }
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] user mix gainDb={gainDb:F1} offset={offset:F3}: adjusted {adjusted} player(s), dip capped on {capped}; e.g.{examples} map={_mapKey}");
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
                // ResetMasterValues leaves these parent buses at -80 dB. The original
                // exterior/interior mixer blend endpoint is 1 (= 0 dB).
                // We own only that neutral bus gain while this graph lives; native earpro,
                // spatial filters and Main/InGame volumes retain their normal ownership.
                _ambientMixer = audio.Master;
                _ambientVolumeParameter = _equator ? audio.AudioMixerData.AmbientInMixerVolume : audio.AudioMixerData.AmbientOutMixerVolume;
                if (_equator && _ambientVolumeParameter != "AmbientInVolume")
                    throw new InvalidOperationException("native interior ambient volume contract changed");
                _ambientBus = AmbientBusLease.Acquire(_ambientMixer, _ambientVolumeParameter);
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] {(_equator ? "interior" : "exterior")} bus leased neutral={_ambientBus.IsNeutral} map={_mapKey}");
            }

            void ReleaseAmbientVolume()
            {
                _ambientBus?.Dispose();
                _ambientBus = null;
                _ambientMixer = null;
            }

            void ApplySeason(ESeasonStatus season)
            {
                int audible = 0, silent = 0;
                foreach (var player in _activePlayers.OfType<SeasonAmbientSoundPlayer>())
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
                        Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] mix listenerVolume={AudioListener.volume:F3} paused={AudioListener.pause} mainDb={MixerValue(audio.Master, data.MainMixerVolume)} inGameDb={MixerValue(audio.Master, data.InGameVolumeMixer)} ambientOutDb={MixerValue(audio.Master, data.AmbientOutMixerVolume)} ambientInDb={MixerValue(audio.Master, data.AmbientInMixerVolume)} worldDb={MixerValue(audio.Master, data.WorldMixerVolume)} map={_mapKey}");
                }
                foreach (var player in _players)
                {
                    if (player == null) continue;
                    // Inspect authored sources without asking an uninitialized inactive player
                    // to create/cache native state. The two Equator dogs never execute Awake.
                    var source = player.GetComponent<AudioSource>();
                    if (source == null) continue;
                    bool initialized = player.Fader != null && player.EmptyClip != null;
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
                        Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] source='{player.name}' clip='{source.clip?.name}' playing={source.isPlaying} virtual={source.isVirtual} sample={source.timeSamples} rms={rms:F6} volume={source.volume:F3} pitch={source.pitch:F3} muted={source.mute} distance={distance:F2} mixer={source.outputAudioMixerGroup?.name} pos={source.transform.position} active={player.gameObject.activeInHierarchy} initialized={initialized} loop={source.loop} map={_mapKey}");
                    }
                }
                Vector3 emitter = _water != null ? _water.EmitterPosition : Vector3.zero;
                float moved = _hasEmitterPosition ? Vector3.Distance(emitter, _previousEmitter) : 0f;
                _previousEmitter = emitter; _hasEmitterPosition = true;
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] playback sources={_players.Length} playing={playing} advancing={advanced} shorelinePosition={emitter} moved={moved:F2} season={_season} map={_mapKey} initialized={_players.Count(p => p != null && p.Fader != null && p.EmptyClip != null)} sourceInactive={_players.Length - _activePlayers.Length}");
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
                    RefreshReverbRouting(false);
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
                Plugin.Log.LogInfo($"[ArenaMapPort][NativeAudio] stopped ownedPlayers={stopped} emptyClipsReleased={emptyClips} cleanupFailures={failures}; owned graph disposal scheduled map={_mapKey}");
            }

            void OnDestroy() { Dispose(); if (_driver == this) _driver = null; }
        }
    }
}
