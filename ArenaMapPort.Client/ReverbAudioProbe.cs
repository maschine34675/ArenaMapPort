using System;
using System.Globalization;
using Audio.ReverbSubsystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>Opt-in short quiet tone, with no baked tail, measured at the real listener output.
    /// A tone-frequency readback separates the diagnostic from most background ambience. This
    /// measures the complete mixer output, not subjective quality or isolated reverb alone.
    /// The three Gunshots buses use no mixer group; they play the tone through EFT's own gunshot sources (ReverbSuperSource,
    /// whose reverb comes from the Meta XR Audio plugin). Gunshots: BetterAudio.PlayAtPoint with spatialization (the
    /// game's only other user of the Gunshots group is the artillery sound; it is the plain spatialized route).
    /// GunshotsFirstPerson and GunshotsThirdPerson: the weapon audio queue that WeaponSoundPlayer fills for EVERY shot,
    /// with EnableStereo(true) (the player's own weapon) or EnableStereo(false) (everyone else's: bots, other players).
    /// The "pulse begin" line records the plugin's acoustic model and native room, the "pulse route" line what the source
    /// really did (spatialized, acoustics, helper sources), so the log shows which path each number belongs to.</summary>
    internal static class ReverbAudioProbe
    {
        // The gunshot buses are appended last: saved ReverbPulseBus values keep their meaning.
        public enum ProbeBus { Ambient, Gameplay, MapAmbience, IndoorRain, LegacyIndoorRain, Gunshots, GunshotsFirstPerson, GunshotsThirdPerson }
        enum Route { Mixer, PlayAtPoint, WeaponQueue }
        static ConfigEntry<bool> _request;
        static ConfigEntry<ProbeBus> _bus;
        static ProbeBus _busUsed; // the bus of the running pulse (the config value may change meanwhile)
        static Route _route;
        static AudioSource _source;
        static SuperBetterAudioQueue _queue; // weapon-queue gunshot buses only
        static BetterSource _pulse; // bus Gunshots only
        static AudioClip _clip;
        static GameObject _object;
        static NativeSpatialAudio.State _owner;
        static readonly float[] Samples = new float[4096];
        static double _started, _playedAt, _baseline, _early, _late, _dryPeak;
        static bool _running, _played, _audioReset, _routeLogged;
        static int _baselineN, _earlyN, _lateN;
        const double Frequency = 1500;
        static ConfigEntry<float> _volume; // pulse level: the muted test host measures energy, so a loud pulse only raises the signal over the background
        // Pulse distance from the listener and the source's rolloff (metres) on the gun paths. Not a measured value.
        const float GunshotDistance = 2f, GunshotRolloff = 200f;

        internal static void Install()
        {
            _request = Plugin.Instance.Settings.Bind("Audio diagnostics", "ReverbPulse", false,
                "Test only: play one quiet 0.12-second tone and measure listener output after it ends. Automatically resets to false. Only during a raid on a map with native acoustics (Equator, Bay5, AirPit, Bowl); the map ambience bus needs the imported ambient driver (Equator, Bay5, AirPit, Bowl).");
            _bus = Plugin.Instance.Settings.Bind("Audio diagnostics", "ReverbPulseBus", ProbeBus.Ambient,
                "Route the test tone through ambient effects, gameplay, map ambience, the repaired indoor rain path or its original snapshot-gated child, or through EFT's own gunshot sources and their Meta XR Audio reverb: Gunshots (BetterAudio.PlayAtPoint, spatialized, from behind the listener), GunshotsFirstPerson (weapon queue as the player's own weapon plays it, stereo) or GunshotsThirdPerson (weapon queue as other shooters' weapons play it, spatialized, from behind the listener). Does not change their mixer settings.");
            _volume = Plugin.Instance.Settings.Bind("Audio diagnostics", "ReverbPulseVolume", 0.08f,
                new ConfigDescription("Test only: level of the ReverbPulse tone (0.08 = quiet). Raise it on a muted test host so the tail measurement stands clear of background audio; the numbers scale with it, compare runs at one level.",
                    new AcceptableValueRange<float>(0.01f, 1f)));
            if (!DeveloperOptions.Enabled) return;
            Plugin.Instance.gameObject.AddComponent<Runner>();
            AudioSettings.OnAudioConfigurationChanged += deviceChanged => _audioReset = true;
        }

        internal static void Stop()
        {
            if (_source != null) _source.Stop();
            ReleaseQueue();
            ReleasePulse();
            if (_object != null) Object.Destroy(_object);
            if (_clip != null) Object.Destroy(_clip);
            _source = null; _object = null; _clip = null; _owner = null;
            _running = false; _route = Route.Mixer;
        }

        // Returns the PlayAtPoint source to BetterAudio's pool. The pulse is played with autoReleaseSource=false, so nothing
        // else releases it (BetterAudio's time queue never learns of it): no double release is possible, and the source
        // stays alive for the whole measurement, exactly as long as the weapon-queue buses keep theirs.
        static void ReleasePulse()
        {
            var pulse = _pulse;
            _pulse = null;
            if (pulse != null && MonoBehaviourSingleton<BetterAudio>.Instantiated) pulse.Release();
        }

        // Returns the borrowed weapon queue (and with it both ReverbSuperSource sources) to BetterAudio's pools.
        static void ReleaseQueue()
        {
            var queue = _queue;
            _queue = null;
            if (queue == null) return;
            // A sample that was never dequeued (released before BetterAudio's next Update) would play for the next borrower.
            queue._samples.Clear();
            if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
                MonoBehaviourSingleton<BetterAudio>.Instance.ReleaseQueue(queue);
        }

        static string BusName() => _route == Route.PlayAtPoint ? "Gunshots(PlayAtPoint)"
            : _route == Route.WeaponQueue ? _busUsed + "(weapon queue)"
            : _source.outputAudioMixerGroup?.name;

        static bool DryPlaying() => _route == Route.PlayAtPoint ? _pulse != null && _pulse.source1 != null && _pulse.source1.isPlaying
            : _route == Route.WeaponQueue ? _queue != null && Array.Exists(_queue.AudioSources, s => s != null && s.source1 != null && s.source1.isPlaying)
            : _source.isPlaying;

        // What the gunshot source really did once it started: whether the Meta spatializer is on for it, whether its reverb
        // send is on, and whether the two reverb helper sources of a ReverbSuperSource are playing. Diagnostic only.
        static string RouteState()
        {
            try
            {
                var c = CultureInfo.InvariantCulture;
                BetterSource source = _route == Route.PlayAtPoint ? _pulse
                    : _queue == null ? null : Array.Find(_queue.AudioSources, s => s != null && s.source1 != null && s.source1.isPlaying);
                if (source == null) return $"bus={_busUsed} no playing gunshot source yet";
                var reverb = source as ReverbSuperSource;
                return $"bus={_busUsed} source={source.GetType().Name} spatialized={source.EnableSpatialization} " +
                    $"acoustics={source.EnableReverb} reverbSendDb={source.ReverbSendDB.ToString("0.0", c)} " +
                    $"helperSources={(reverb == null ? "n/a" : (reverb._reverbSourceA.isPlaying || reverb._reverbSourceB.isPlaying).ToString())} " +
                    $"position={source.Position.ToString("F2", c)}";
            }
            catch (Exception e)
            {
                return "route state unavailable: " + e.GetType().Name + ": " + e.Message;
            }
        }

        // The plugin's process-global acoustic model and the native shoebox room it currently holds. The oversized arrays
        // are deliberate: the native writer's extent is not documented and a short buffer could be overrun.
        static string MetaState()
        {
            try
            {
                var c = CultureInfo.InvariantCulture;
                var dimensions = new float[16];
                var coefficients = new float[64];
                int result = MetaXRAudioNativeInterface.Interface.GetRoomDimensions(dimensions, coefficients, out Vector3 position);
                var firstCoefficients = new string[6];
                for (int i = 0; i < firstCoefficients.Length; i++) firstCoefficients[i] = coefficients[i].ToString("0.000", c);
                return $"acousticModel={MetaXRAcousticSettings.Instance.AcousticModel} nativeRoom rc={result} " +
                    $"size={dimensions[0].ToString("0.00", c)}x{dimensions[1].ToString("0.00", c)}x{dimensions[2].ToString("0.00", c)} " +
                    $"position={position.ToString("F2", c)} coefs[0..5]={string.Join(",", firstCoefficients)}";
            }
            catch (Exception e)
            {
                return "metaState unavailable: " + e.GetType().Name + ": " + e.Message;
            }
        }

        public sealed class Runner : MonoBehaviour
        {
            void Update()
            {
                try
                {
                    if (_audioReset) { Stop(); _request.Value = false; _audioReset = false; }
                    var state = NativeSpatialAudio.Current;
                    bool ready = state != null && state.Finished && !state.Released &&
                        Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started &&
                        MonoBehaviourSingleton<BetterAudio>.Instantiated;
                    if (!ready || (_owner != null && _owner != state)) { Stop(); _request.Value = false; return; }
                    if (!_running)
                    {
                        if (!_request.Value) return;
                        _request.Value = false;
                        _owner = state;
                        _busUsed = _bus.Value;
                        _route = _busUsed == ProbeBus.Gunshots ? Route.PlayAtPoint
                            : _busUsed == ProbeBus.GunshotsFirstPerson || _busUsed == ProbeBus.GunshotsThirdPerson ? Route.WeaponQueue
                            : Route.Mixer;
                        var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                        if (_route != Route.Mixer)
                        {
                            if (!Singleton<AudioListenerConsistencyManager>.Instantiated)
                                throw new InvalidOperationException($"Probe bus {_busUsed} needs the audio listener");
                        }
                        else
                        {
                            _object = new GameObject("ArenaMapPort Reverb Pulse");
                            _object.transform.SetParent(state.Root.transform, false);
                            _source = _object.AddComponent<AudioSource>();
                            _source.playOnAwake = false; _source.spatialBlend = 0; _source.spatialize = false;
                            _source.volume = _volume.Value;
                            _source.outputAudioMixerGroup = _bus.Value == ProbeBus.Ambient ? audio.CommonAmbientEffectsMixer :
                                _bus.Value == ProbeBus.Gameplay ? audio.VeryStandartMixerGroup :
                                _bus.Value == ProbeBus.IndoorRain ? audio.AmbientInMixer :
                                _bus.Value == ProbeBus.LegacyIndoorRain ? audio.FindMixerGroup("AmbientIn/PrecipitationAmbientIn") :
                                Bay5AmbientAudio.CurrentEffectsMixer(state.MapKey);
                            if (_source.outputAudioMixerGroup == null) throw new InvalidOperationException(
                                $"Probe bus {_bus.Value} has no mixer group on {state.MapKey} (MapAmbience needs the imported ambient driver, which runs on Equator, Bay5, AirPit and Bowl only)");
                        }
                        int rate = AudioSettings.outputSampleRate;
                        var pcm = new float[(int)(rate * 0.12)];
                        for (int i = 0; i < pcm.Length; i++)
                            pcm[i] = (float)(Math.Sin(2 * Math.PI * Frequency * i / rate) * Math.Sin(Math.PI * i / pcm.Length));
                        _clip = AudioClip.Create("ArenaMapPort dry diagnostic pulse", pcm.Length, 1, rate, false);
                        if (!_clip.SetData(pcm, 0)) throw new InvalidOperationException("Pulse PCM rejected");
                        if (_route == Route.Mixer) _source.clip = _clip;
                        _started = AudioSettings.dspTime; _played = false; _running = true; _routeLogged = false;
                        _baseline = _early = _late = _dryPeak = 0; _baselineN = _earlyN = _lateN = 0;
                        AudioListener.GetOutputData(Samples, 0); // Warm up the listener readback.
                        RoomReverb.Log();
                        Plugin.Log.LogDebug($"{RoomReverb.Tag} pulse begin bus={_bus.Value} group={(_route != Route.Mixer ? audio.GunshotMixerGroup?.name : _source.outputAudioMixerGroup?.name)} " +
                            $"room={state.System.ListenerCurrentRoom?.ID} {MetaState()}");
                    }
                    double time = AudioSettings.dspTime - _started;
                    AudioListener.GetOutputData(Samples, 0);
                    double real = 0, imaginary = 0, step = 2 * Math.PI * Frequency / AudioSettings.outputSampleRate;
                    for (int i = 0; i < Samples.Length; i++)
                    { real += Samples[i] * Math.Cos(step * i); imaginary += Samples[i] * Math.Sin(step * i); }
                    double energy = (real * real + imaginary * imaginary) / (Samples.Length * Samples.Length);
                    if (time < 0.6) { _baseline += energy; _baselineN++; }
                    else if (!_played) { Play(); _played = true; _playedAt = AudioSettings.dspTime; }
                    if (!_played) return;
                    double afterPlay = AudioSettings.dspTime - _playedAt;
                    if (_route != Route.Mixer && !_routeLogged && afterPlay >= 0.05)
                    {
                        _routeLogged = true;
                        Plugin.Log.LogDebug($"{RoomReverb.Tag} pulse route {RouteState()}");
                    }
                    if (afterPlay < 0.2) _dryPeak = Math.Max(_dryPeak, energy);
                    // Skip dry playback and at least one complete output buffer (4096/48k=85ms).
                    if (afterPlay >= 0.35 && afterPlay < 0.7) { _early += energy; _earlyN++; }
                    if (afterPlay >= 0.7 && afterPlay < 1.5) { _late += energy; _lateN++; }
                    if (afterPlay < 1.6) return;
                    Plugin.Log.LogDebug($"{RoomReverb.Tag} pulse result bus={BusName()} dryPlaying={DryPlaying()} " +
                        $"baseline={Math.Sqrt(_baseline / Math.Max(1, _baselineN)):E4} dryPeak={Math.Sqrt(_dryPeak):E4} " +
                        $"tailEarly={Math.Sqrt(_early / Math.Max(1, _earlyN)):E4} tailLate={Math.Sqrt(_late / Math.Max(1, _lateN)):E4} " +
                        $"blocks={_baselineN}/{_earlyN}/{_lateN}; output includes background audio");
                    Stop();
                }
                catch (Exception e) { Stop(); _request.Value = false; Plugin.Log.LogError(RoomReverb.Tag + " pulse failed: " + e); }
            }

            // Mixer buses: one plain AudioSource. Gunshot buses: one of the two ways EFT plays a gunshot.
            // PlayAtPoint: BetterAudio.PlayAtPoint with spatialize; SuperSource.Play never reaches
            // ReverbSuperSource.PlayScheduled, so the two reverb helper sources stay off and the source's own Meta spatializer
            // (the preset's mono acoustic settings) is the only Meta path.
            // Weapon queue (WeaponSoundPlayer.FireBullet, every shot): borrow the queue, pose it, enqueue the clip;
            // BetterAudio.Update then plays it through ReverbSuperSource.PlayScheduled, which starts the helper sources only
            // when the source is not spatialized. EnableStereo(true) is the player's own weapon, EnableStereo(false) everyone else's.
            // Position: BetterSource.CheckBinauralAllowed turns the Meta spatializer on for a source behind the camera and leaves
            // it off for one straight ahead at this distance, so the spatialized buses play from behind the listener and only the
            // stereo first-person bus plays from ahead; "pulse route" logs what was really in effect.
            void Play()
            {
                if (_route == Route.Mixer) { _source.Play(); return; }
                var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                var listener = Singleton<AudioListenerConsistencyManager>.Instance.transform;
                bool ahead = _busUsed == ProbeBus.GunshotsFirstPerson;
                Vector3 position = listener.position + (ahead ? listener.forward : -listener.forward) * GunshotDistance;
                if (_route == Route.PlayAtPoint)
                {
                    // autoReleaseSource false: Stop() releases it, after the same window the other buses measure.
                    _pulse = audio.PlayAtPoint(position, _clip, BetterAudio.AudioSourceGroupType.Gunshots, (int)GunshotRolloff,
                        _volume.Value, spatialize: true, autoReleaseSource: false);
                    if (_pulse == null) throw new InvalidOperationException("BetterAudio.PlayAtPoint returned no Gunshots source (none free, or out of range)");
                    return;
                }
                _queue = audio.BorrowWeaponAudioQueue(BetterAudio.AudioSourceGroupType.Gunshots);
                if (_queue == null) throw new InvalidOperationException("No free gunshot audio queue");
                _queue.Pose(position);
                _queue.SetRolloff(GunshotRolloff);
                _queue.SetMixerGroup(audio.GunshotMixerGroup);
                _queue.EnableStereo(_busUsed == ProbeBus.GunshotsFirstPerson);
                _queue.Enqueue(_clip, null, 1f, AudioSettings.dspTime, _clip.length, _volume.Value);
            }
        }
    }
}
