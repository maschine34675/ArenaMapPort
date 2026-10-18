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
    /// <summary>Starts the imported stock ambient players of Equator, Bay5, AirPit and Bowl once real raid audio services exist.
    /// Arena's ambient singleton/room system is deliberately not recreated. Stock players retain
    /// their original clip selection, gain, pitch and timing; Bay5's stock shoreline calculator
    /// ticks from this owned driver. What differs per map (root name, graph census, group to mixer/bus
    /// routing, season and shoreline handling, the reverb reroute) is the <see cref="AmbientAudioProfile"/> table in
    /// AmbientAudioProfiles.cs; per-map host acceptance lives in native-features progress.
    /// AirPit (2026-10-12) has an exterior group on AmbientOut and an interior group on AmbientIn and holds both
    /// parent buses open for the whole raid; blending them by acoustic room is not implemented.</summary>
    internal static class Bay5AmbientAudio
    {
        static Driver _driver;
        // Tuning entries per map, bound once per session from the profile table (they outlive every Driver).
        sealed class MapSettings
        {
            internal ConfigEntry<bool> Diagnostics;
            // PLAN-TWO-MAPS item 1 (2026-09-25): the user hears the ported ambience as too quiet. BaseAmbientSoundPlayer
            // draws GetBaseVolume() = _volume + Random.Range(-_deltaVolume, 0) per raid; 13 of Equator's 25 authored
            // players have _deltaVolume > _volume, so a draw can clamp them to silence. The accepted release mix uses
            // a dB gain that scales _volume and _deltaVolume together, and a
            // linear offset added to _volume (raises the floor). Whenever either is non-neutral, _deltaVolume is also
            // capped at _volume so no draw can fall below zero. Applied once per raid to the scene's own player
            // instances right before they start (the scene is unloaded with the raid, nothing accumulates).
            internal ConfigEntry<float> GainDb, Offset;
            // 2026-09-25 user report: even at AmbientGainDb=+12 / AmbientVolumeOffset=1 the imported ambience is only
            // audible right next to a source. Cause (decompile BaseAmbientSoundPlayer.Awake, lines 250-261): the
            // authored custom rolloff curve (1.0 at 0.2 %, 0.27 at 19 %, 0 at 100 % of _maxDistance; Equator players
            // mostly 10-20 m) is baked into the AudioSource once at Awake, and gain/offset only scale the pre-attenuation
            // volume. This multiplier stretches _maxDistance BEFORE the graph is activated (before that Awake), so the
            // same curve shape reaches further. 1 = original Arena range.
            internal ConfigEntry<float> Range;
        }
        static readonly Dictionary<string, MapSettings> Bound = new Dictionary<string, MapSettings>();

        // Bind every map before the initial F12 visibility pass, even when no raid has
        // loaded yet. This also applies the accepted audio migration before map cleanup.
        internal static void BindConfig()
        {
            foreach (var profile in AmbientAudioProfiles.All)
            {
                string key = profile.MapKey, section = "Audio (" + key + ")", name = profile.DisplayName;
                if (Bound.ContainsKey(key)) continue;
                AmbientAudioDefaults.Values(key, out float gain, out float offset, out float range);
                var settings = new MapSettings();
                settings.Diagnostics = Plugin.Instance.Settings.Bind(section, "PlaybackDiagnostics", false, DiagnosticsText(profile));
                settings.GainDb = Plugin.Instance.Settings.Bind(section, "AmbientGainDb", gain,
                    new ConfigDescription($"Gain in dB applied to the imported ambient sources' authored volume (and their random dip) on {name}. 0 dB applies no gain to authored sources; the volume offset and range still apply. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(-12f, 12f)));
                settings.Offset = Plugin.Instance.Settings.Bind(section, "AmbientVolumeOffset", offset,
                    new ConfigDescription($"Linear volume added to every imported ambient source on {name} (raises the floor so quiet sources cannot dip to silence). 0 = no additive volume floor. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(0f, 1f)));
                SubscribeLive(settings.GainDb, settings.Offset, key);
                settings.Range = Plugin.Instance.Settings.Bind(section, "AmbientRangeMultiplier", range,
                    new ConfigDescription($"Multiplies every imported ambient source's Max Distance on {name} (the authored rolloff curve keeps its shape, so sources stay audible further away{RangeNote(profile)}). 1 = original Arena range. Applied live in a running raid and at every raid start.", new AcceptableValueRange<float>(1f, 8f)));
                settings.Range.SettingChanged += (_, __) => ApplyLive(key);
                AmbientAudioDefaults.Migrate(Plugin.Instance.Settings, key, settings.GainDb, settings.Offset, settings.Range);
                Bound.Add(key, settings);
            }
        }

        static string DiagnosticsText(AmbientAudioProfile profile)
        {
            const string tail = " Takes effect immediately; F10 also logs a snapshot.";
            switch (profile.MapKey)
            {
                case "arena_equator": return "Log native indoor ambient clip/sample evidence while in Equator." + tail;
                case "arena_bay5": return "Log native ambient clip/sample and shoreline movement evidence while in Bay5." + tail;
                default: return $"Log native ambient clip/sample evidence while in {profile.DisplayName}." + tail;
            }
        }

        static string RangeNote(AmbientAudioProfile profile)
        {
            switch (profile.MapKey)
            {
                case "arena_equator": return "; most Equator sources are authored at 10-20 m";
                case "arena_airpit": return "; half of the AirPit sources are authored at 10 m or less";
                default: return "";
            }
        }

        internal static void Ensure()
        {
            string mapKey = ArenaMapRaid.CurrentMap?.Key;
            if (_driver != null || !AmbientAudioProfiles.TryGet(mapKey, out var profile)) return;
            BindConfig();
            var scene = SceneManager.GetSceneByName(ArenaMapRaid.CurrentMap.ScriptsSceneName);
            var roots = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                .Where(t => t.name == profile.RootName).ToArray();
            if (roots.Length == 0) return; // Old packages do not contain this optional feature.
            if (roots.Length != 1) { Plugin.Log.LogError("[ArenaMapPort][NativeAudio] duplicate owned audio roots; refusing playback"); return; }
            var settings = Bound[mapKey];
            _driver = Plugin.Instance.gameObject.AddComponent<Driver>();
            _driver.Initialize(roots[0].gameObject, profile, settings.Diagnostics, settings.GainDb, settings.Offset, settings.Range);
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
        internal static void Probe() {
            if (!DeveloperOptions.Enabled) return; if (_driver != null) _driver.Report(true); }

        internal static AudioMixerGroup CurrentEffectsMixer(string mapKey) =>
            _driver != null && _driver.MapKey == mapKey ? _driver.EffectsMixer : null;

        internal static void RestoreReverbRouting() { if (_driver != null) _driver.RefreshReverbRouting(false); }

        public sealed class Driver : MonoBehaviour
        {
            GameObject _root;
            string _mapKey;
            AmbientAudioProfile _profile;
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
            // One lease per parent bus the profile's groups feed (Equator In, Bay5 Out, AirPit Out and In). The leases share
            // their entry with the room-tone and precipitation owners, so a bus opens once and closes with its last owner.
            readonly Dictionary<AmbientBus, AmbientBusLease> _leases = new Dictionary<AmbientBus, AmbientBusLease>();
            AmbientSoundPlayerGroup _effectsGroup;
            AmbientGroupRoute _effectsRoute;
            AudioMixerGroup _originalEffects, _reverbEffects;
            bool _reverbRouted, _reverbRouteYielded;
            internal AudioMixerGroup EffectsMixer => _effectsGroup != null ? _effectsGroup._outputMixerGroup : null;
            float _servicesSince = -1f, _nextReport, _nextReadyCheck;
            int _reports;
            bool _ready, _disposed;
            ESeasonStatus _season;
            Vector3 _previousEmitter;
            bool _hasEmitterPosition;

            internal void Initialize(GameObject root, AmbientAudioProfile profile, ConfigEntry<bool> diagnostics,
                ConfigEntry<float> gainDb, ConfigEntry<float> offset, ConfigEntry<float> range)
            {
                _root = root;
                _profile = profile; _mapKey = profile.MapKey; _diagnostics = diagnostics;
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
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] waiting for raid listener and native mixers; map={_mapKey} players={_players.Length} active={_activePlayers.Length}");
            }

            void ValidateGraph()
            {
                var profile = _profile;
                if (_root.activeSelf || _players.Length != profile.Players ||
                    _groups.Length != profile.Groups || _controllers.Length != profile.Controllers ||
                    (profile.HasShoreline ? _water == null : _water != null) ||
                    _root.GetComponentsInChildren<AudioSource>(true).Length != _players.Length)
                    throw new InvalidOperationException("native graph census mismatch");
                if (_groups.Any(g => !g.enabled || !ActiveUnderOwnedRoot(g.transform) || g._soundPlayers == null))
                    throw new InvalidOperationException("inactive or incomplete native player group");
                // Every group must be one the profile routes, each exactly once (found by the carrying GameObject's name).
                if (_groups.Any(g => profile.IndexOfGroup(g.name) < 0) || _groups.Select(g => g.name).Distinct().Count() != _groups.Length)
                    throw new InvalidOperationException("unrecognized or duplicate ambient group");
                if (_groups.Any(g => g._soundPlayers.Count != profile.Routes[profile.IndexOfGroup(g.name)].Members))
                    throw new InvalidOperationException("ambient group member count differs from the source graph");
                var members = _groups.SelectMany(g => g._soundPlayers).ToArray();
                var active = _players.Where(p => p.enabled && ActiveUnderOwnedRoot(p.transform) &&
                    p.GetComponent<AudioSource>() != null && p.GetComponent<AudioSource>().enabled).ToArray();
                // Equator, AirPit: the groups list exactly the players that start. Bowl (PLAN-BOWL item 9): its source groups
                // list every player, four of them under an inactive ancestor; those stay members (the group's own Play and
                // SetMixerGroup tolerate them, as in Arena) but are never started here, so the started set is the active
                // members and the member list must still be exactly the graph's players.
                _activePlayers = profile.GroupsListInactivePlayers
                    ? members.Where(p => p != null && p.enabled && ActiveUnderOwnedRoot(p.transform)).ToArray()
                    : members;
                if (members.Length != profile.GroupedPlayers || members.Any(p => p == null) ||
                    members.Distinct().Count() != members.Length ||
                    _activePlayers.Length != profile.ActivePlayers ||
                    !new HashSet<BaseAmbientSoundPlayer>(active).SetEquals(_activePlayers) ||
                    (profile.GroupsListInactivePlayers && !new HashSet<BaseAmbientSoundPlayer>(_players).SetEquals(members)))
                    throw new InvalidOperationException("native group membership differs from enabled active players");
                // Retain Bay5's accepted hierarchy iteration order; the original-activation maps (Equator, AirPit)
                // follow their original active member lists and never start ungrouped descendants.
                if (!profile.OriginalActivation) { _activePlayers = _players; return; }
                var inactive = _players.Where(p => !ActiveUnderOwnedRoot(p.transform)).ToArray();
                if (inactive.Length != profile.InactivePlayers.Count ||
                    !new HashSet<string>(inactive.Select(p => p.name)).SetEquals(profile.InactivePlayers) ||
                    _players.Any(p => !(p is AmbientSoundPlayer) || !p.enabled || !p.GetComponent<AudioSource>().enabled) ||
                    profile.LoopPlayers.Any(name => _players.Count(p => p.name == name) != 1) ||
                    _players.Any(p => p.GetComponent<AudioSource>().loop != profile.LoopPlayers.Contains(p.name)))
                    throw new InvalidOperationException(profile.DisplayName + " original activation/loop contract differs");
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
                    if (_profile.UsesSeasons && Seasons.Controller != null && Seasons.Controller.Status != _season)
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
                if (listener == null || listener.transform.position.y < -500f || (_profile.UsesSeasons && Seasons.Controller == null) || !BetterAudio.Instantiated)
                    return;
                var audio = BetterAudio.Instance;
                if (audio.Master == null || audio.AudioMixerData == null) return;
                // Resolve every route's native mixer group first; a missing one means the audio services are not up yet.
                var routes = _profile.Routes;
                var mixers = new AudioMixerGroup[routes.Count];
                for (int i = 0; i < routes.Count; i++)
                {
                    mixers[i] = Mixer(audio.Master, routes[i].MixerPath, routes[i].MixerName);
                    if (mixers[i] == null) return;
                }
                foreach (var group in _groups)
                {
                    int index = _profile.IndexOfGroup(group.name);
                    if (index < 0) throw new InvalidOperationException("unrecognized ambient group " + group.name);
                    group._outputMixerGroup = mixers[index];
                    if (routes[index].Effects) { _effectsGroup = group; _effectsRoute = routes[index]; _originalEffects = mixers[index]; }
                    group.SetMixerGroup();
                }
                AcquireAmbientVolume(audio);
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
                if (_profile.HasShoreline) PrepareShoreline();
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
                string mixerNames = string.Join(",", mixers.Select(m => m.name));
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] ready players={_players.Length} started={started} seasonalSilent={seasonalSilence} season={_season} mixers={mixerNames} shoreline={(_profile.HasShoreline ? "stock" : "none")} listener={listener.transform.position} map={_mapKey} sourceInactive={_players.Length - _activePlayers.Length}");
            }

            internal void RefreshReverbRouting(bool requested)
            {
                var route = _effectsRoute;
                if (route == null || !route.ReverbReroute || _effectsGroup == null || _originalEffects == null || _reverbEffects == null || _reverbRouteYielded) return;
                // EFT permanently mutes the original Returns/Reverb group. This original alternative
                // ambient bus reaches TarkovReverb. Its dry path is equivalent while the owned parent
                // gain is neutral (no skipped filters/effects); preserve external non-neutral gains.
                // Equator, AirPit and Bowl reroute their interior group (AmbientIn parent); Bay5's exterior bus needs no reroute.
                float volume = float.NaN;
                bool neutralParent = _leases.TryGetValue(route.Bus, out var lease) && lease.IsNeutral && _ambientMixer != null &&
                    _ambientMixer.GetFloat(AmbientAudioProfiles.Parameter(route.Bus), out volume) && Mathf.Approximately(volume, 0f);
                bool useReverb = requested && neutralParent;
                if (useReverb == _reverbRouted) return;
                var expected = _reverbRouted ? _reverbEffects : _originalEffects;
                if (_effectsGroup._outputMixerGroup != expected || _effectsGroup._soundPlayers.Any(p =>
                    p != null && (p._mixerGroup != expected || p.GetComponent<AudioSource>()?.outputAudioMixerGroup != expected)))
                {
                    _reverbRouteYielded = true;
                    Plugin.Log.LogDebug(RoomReverb.Tag + " " + _profile.DisplayName + " ambient routing changed externally; retaining it");
                    return;
                }
                _effectsGroup._outputMixerGroup = useReverb ? _reverbEffects : _originalEffects;
                _effectsGroup.SetMixerGroup(); // Also updates live sources; no restart/random delay reset.
                _reverbRouted = useReverb;
                Plugin.Log.LogDebug($"{RoomReverb.Tag} {_profile.DisplayName} owned ambient route={_effectsGroup._outputMixerGroup.name}, " +
                    $"players={_effectsGroup._soundPlayers.Count}, original indoor bus={volume:F2}dB");
            }

            // See the field comment on MapSettings.Range. Runs on the still-inactive graph; 1.0 leaves every value untouched.
            void ApplyUserRange()
            {
                float m = _range?.Value ?? 1f;
                if (Mathf.Approximately(m, 1f) || m <= 0f)
                {
                    foreach (var player in _players)
                        if (player != null && _baseline.TryGetValue(player, out var b0)) player._maxDistance = b0.MaxDistance;
                    Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] user range neutral (original Arena max distances) map={_mapKey}");
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
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] user range x{m:F2}: max distance scaled on {adjusted} player(s); e.g.{examples} map={_mapKey}");
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
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] live settings applied: gainDb={_gainDb?.Value ?? 0f:F1} offset={_offset?.Value ?? 0f:F3} range=x{_range?.Value ?? 1f:F2}; re-targeted {pushed} playing source(s), max distance pushed on {_activePlayers.Length} map={_mapKey}");
            }

            // PLAN-TWO-MAPS item 1 — see the field comment on MapSettings.GainDb. Neutral settings leave every value untouched.
            void ApplyUserMix()
            {
                float gainDb = _gainDb?.Value ?? 0f;
                float offset = _offset?.Value ?? 0f;
                if (Mathf.Approximately(gainDb, 0f) && Mathf.Approximately(offset, 0f))
                {
                    foreach (var player in _activePlayers)
                        if (player != null && _baseline.TryGetValue(player, out var b0)) { player._volume = b0.Volume; player._deltaVolume = b0.Delta; }
                    Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] user mix neutral (original Arena volumes) map={_mapKey}");
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
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] user mix gainDb={gainDb:F1} offset={offset:F3}: adjusted {adjusted} player(s), dip capped on {capped}; e.g.{examples} map={_mapKey}");
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
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] shoreline range origin aligned from={previous.position} to={calculator._spline.transform.position} radius={calculator._calculationRadius:F2}");

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
                    Plugin.Log.LogDebug("[ArenaMapPort][NativeAudio] shoreline initial calculation deferred to native listener movement");
                    return;
                }
                foreach (var controller in _controllers) controller.Tick();
                lastPosition.SetValue(calculator, target);
                foreach (var controller in _controllers) controller.LateTick();
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] shoreline initial calculation listener={calculator.TargetPos} emitter={_water.EmitterPosition}");
            }

            void AcquireAmbientVolume(BetterAudio audio)
            {
                // ResetMasterValues leaves these parent buses at -80 dB. The original
                // exterior/interior mixer blend endpoint is 1 (= 0 dB).
                // We own only that neutral bus gain while this graph lives; native earpro,
                // spatial filters and Main/InGame volumes retain their normal ownership.
                // AirPit holds both buses open at 0 dB (no room crossfade); a lease is shared with the rain and
                // room-tone owners, so the value is written once and restored by the last owner.
                _ambientMixer = audio.Master;
                foreach (var bus in _profile.Buses)
                {
                    string parameter = bus == AmbientBus.In ? audio.AudioMixerData.AmbientInMixerVolume : audio.AudioMixerData.AmbientOutMixerVolume;
                    if (parameter != AmbientAudioProfiles.Parameter(bus))
                        throw new InvalidOperationException("native " + (bus == AmbientBus.In ? "interior" : "exterior") + " ambient volume contract changed");
                    var lease = AmbientBusLease.Acquire(_ambientMixer, parameter);
                    _leases.Add(bus, lease);
                    Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] {(bus == AmbientBus.In ? "interior" : "exterior")} bus leased neutral={lease.IsNeutral} map={_mapKey}");
                }
            }

            void ReleaseAmbientVolume()
            {
                // Release every lease even if one fails; report the first failure to the caller's cleanup handler.
                Exception first = null;
                foreach (var lease in _leases.Values)
                {
                    try { lease.Dispose(); }
                    catch (Exception ex) { if (first == null) first = ex; }
                }
                _leases.Clear();
                _ambientMixer = null;
                if (first != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
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
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] season={season} seasonalStarted={audible} seasonalSilent={silent}");
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
                        Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] mix listenerVolume={AudioListener.volume:F3} paused={AudioListener.pause} mainDb={MixerValue(audio.Master, data.MainMixerVolume)} inGameDb={MixerValue(audio.Master, data.InGameVolumeMixer)} ambientOutDb={MixerValue(audio.Master, data.AmbientOutMixerVolume)} ambientInDb={MixerValue(audio.Master, data.AmbientInMixerVolume)} worldDb={MixerValue(audio.Master, data.WorldMixerVolume)} map={_mapKey}");
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
                        Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] source='{player.name}' clip='{source.clip?.name}' playing={source.isPlaying} virtual={source.isVirtual} sample={source.timeSamples} rms={rms:F6} volume={source.volume:F3} pitch={source.pitch:F3} muted={source.mute} distance={distance:F2} mixer={source.outputAudioMixerGroup?.name} pos={source.transform.position} active={player.gameObject.activeInHierarchy} initialized={initialized} loop={source.loop} map={_mapKey}");
                    }
                }
                Vector3 emitter = _water != null ? _water.EmitterPosition : Vector3.zero;
                float moved = _hasEmitterPosition ? Vector3.Distance(emitter, _previousEmitter) : 0f;
                _previousEmitter = emitter; _hasEmitterPosition = true;
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] playback sources={_players.Length} playing={playing} advancing={advanced} shorelinePosition={emitter} moved={moved:F2} season={_season} map={_mapKey} initialized={_players.Count(p => p != null && p.Fader != null && p.EmptyClip != null)} sourceInactive={_players.Length - _activePlayers.Length}");
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
                Plugin.Log.LogDebug($"[ArenaMapPort][NativeAudio] stopped ownedPlayers={stopped} emptyClipsReleased={emptyClips} cleanupFailures={failures}; owned graph disposal scheduled map={_mapKey}");
            }

            void OnDestroy() { Dispose(); if (_driver == this) _driver = null; }
        }
    }
}
