using System;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>Opt-in short quiet tone, with no baked tail, measured at the real listener output.
    /// A tone-frequency readback separates the diagnostic from most background ambience. This
    /// measures the complete mixer output, not subjective quality or isolated reverb alone.</summary>
    internal static class ReverbAudioProbe
    {
        public enum ProbeBus { Ambient, Gameplay, MapAmbience, IndoorRain, LegacyIndoorRain }
        static ConfigEntry<bool> _request;
        static ConfigEntry<ProbeBus> _bus;
        static AudioSource _source;
        static AudioClip _clip;
        static GameObject _object;
        static NativeSpatialAudio.State _owner;
        static readonly float[] Samples = new float[4096];
        static double _started, _playedAt, _baseline, _early, _late, _dryPeak;
        static bool _played, _audioReset;
        static int _baselineN, _earlyN, _lateN;
        const double Frequency = 1500;

        internal static void Install()
        {
            _request = Plugin.Instance.Settings.Bind("Audio diagnostics", "ReverbPulse", false,
                "Test only: play one quiet 0.12-second tone and measure listener output after it ends. Automatically resets to false. Only during a raid on a map with native acoustics (Equator, Bay5, AirPit); the map ambience bus needs the imported ambient driver (Equator, Bay5).");
            _bus = Plugin.Instance.Settings.Bind("Audio diagnostics", "ReverbPulseBus", ProbeBus.Ambient,
                "Route the test tone through ambient effects, gameplay, map ambience, the repaired indoor rain path or its original snapshot-gated child. Does not change their mixer settings.");
            if (!DeveloperOptions.Enabled) return;
            Plugin.Instance.gameObject.AddComponent<Runner>();
            AudioSettings.OnAudioConfigurationChanged += deviceChanged => _audioReset = true;
        }

        internal static void Stop()
        {
            if (_source != null) _source.Stop();
            if (_object != null) Object.Destroy(_object);
            if (_clip != null) Object.Destroy(_clip);
            _source = null; _object = null; _clip = null; _owner = null;
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
                    if (_source == null)
                    {
                        if (!_request.Value) return;
                        _request.Value = false;
                        _owner = state;
                        _object = new GameObject("ArenaMapPort Reverb Pulse");
                        _object.transform.SetParent(state.Root.transform, false);
                        _source = _object.AddComponent<AudioSource>();
                        _source.playOnAwake = false; _source.spatialBlend = 0; _source.spatialize = false;
                        _source.volume = 0.08f;
                        var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                        _source.outputAudioMixerGroup = _bus.Value == ProbeBus.Ambient ? audio.CommonAmbientEffectsMixer :
                            _bus.Value == ProbeBus.Gameplay ? audio.VeryStandartMixerGroup :
                            _bus.Value == ProbeBus.IndoorRain ? audio.AmbientInMixer :
                            _bus.Value == ProbeBus.LegacyIndoorRain ? audio.FindMixerGroup("AmbientIn/PrecipitationAmbientIn") :
                            Bay5AmbientAudio.CurrentEffectsMixer(state.MapKey);
                        if (_source.outputAudioMixerGroup == null) throw new InvalidOperationException(
                            $"Probe bus {_bus.Value} has no mixer group on {state.MapKey} (MapAmbience needs the imported ambient driver, which runs on Equator and Bay5 only)");
                        int rate = AudioSettings.outputSampleRate;
                        var pcm = new float[(int)(rate * 0.12)];
                        for (int i = 0; i < pcm.Length; i++)
                            pcm[i] = (float)(Math.Sin(2 * Math.PI * Frequency * i / rate) * Math.Sin(Math.PI * i / pcm.Length));
                        _clip = AudioClip.Create("ArenaMapPort dry diagnostic pulse", pcm.Length, 1, rate, false);
                        if (!_clip.SetData(pcm, 0)) throw new InvalidOperationException("Pulse PCM rejected");
                        _source.clip = _clip;
                        _started = AudioSettings.dspTime; _played = false;
                        _baseline = _early = _late = _dryPeak = 0; _baselineN = _earlyN = _lateN = 0;
                        AudioListener.GetOutputData(Samples, 0); // Warm up the listener readback.
                        RoomReverb.Log();
                        Plugin.Log.LogDebug($"{RoomReverb.Tag} pulse begin bus={_bus.Value} group={_source.outputAudioMixerGroup?.name} room={state.System.ListenerCurrentRoom?.ID}");
                    }
                    double time = AudioSettings.dspTime - _started;
                    AudioListener.GetOutputData(Samples, 0);
                    double real = 0, imaginary = 0, step = 2 * Math.PI * Frequency / AudioSettings.outputSampleRate;
                    for (int i = 0; i < Samples.Length; i++)
                    { real += Samples[i] * Math.Cos(step * i); imaginary += Samples[i] * Math.Sin(step * i); }
                    double energy = (real * real + imaginary * imaginary) / (Samples.Length * Samples.Length);
                    if (time < 0.6) { _baseline += energy; _baselineN++; }
                    else if (!_played) { _source.Play(); _played = true; _playedAt = AudioSettings.dspTime; }
                    if (!_played) return;
                    double afterPlay = AudioSettings.dspTime - _playedAt;
                    if (afterPlay < 0.2) _dryPeak = Math.Max(_dryPeak, energy);
                    // Skip dry playback and at least one complete output buffer (4096/48k=85ms).
                    if (afterPlay >= 0.35 && afterPlay < 0.7) { _early += energy; _earlyN++; }
                    if (afterPlay >= 0.7 && afterPlay < 1.5) { _late += energy; _lateN++; }
                    if (afterPlay < 1.6) return;
                    Plugin.Log.LogDebug($"{RoomReverb.Tag} pulse result bus={_source.outputAudioMixerGroup?.name} dryPlaying={_source.isPlaying} " +
                        $"baseline={Math.Sqrt(_baseline / Math.Max(1, _baselineN)):E4} dryPeak={Math.Sqrt(_dryPeak):E4} " +
                        $"tailEarly={Math.Sqrt(_early / Math.Max(1, _earlyN)):E4} tailLate={Math.Sqrt(_late / Math.Max(1, _lateN)):E4} " +
                        $"blocks={_baselineN}/{_earlyN}/{_lateN}; output includes background audio");
                    Stop();
                }
                catch (Exception e) { Stop(); _request.Value = false; Plugin.Log.LogError(RoomReverb.Tag + " pulse failed: " + e); }
            }
        }
    }
}
