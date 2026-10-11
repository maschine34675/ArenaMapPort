using System;
using System.Collections.Generic;
using System.Linq;
using ArenaMapPort.Shared;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Audio;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Arena's AmbientAudioSystem normally owns these source mixer tables. The port has its
    /// own ambient players, so restore only the missing native room-event/mixer controller.
    /// Detailed room types need explicit translation to the source's three table categories.
    /// Gameplay reverb remains controlled by native IndoorTriggers and mixer snapshots.
    /// Source, signal paths and GPU acceptance: docs/progress/room-reverb.md.
    /// </summary>
    internal static class RoomReverb
    {
        internal const string Tag = "[ArenaMapPort][RoomReverb]";
        static readonly Dictionary<string, ConfigEntry<bool>> Enabled = new Dictionary<string, ConfigEntry<bool>>();
        static readonly HashSet<string> Allowed = new HashSet<string>((
            "AmbientReverbVolume AmbientReverbDryLevel AmbientReverbMix AmbientReverbDelay " +
            "AmbientReverbReflectDelay AmbientReverbReflectMix AmbientReverbRoomSize AmbientReverbDecayTime " +
            "AmbientReverbDecayHFRatio AmbientReverbRoomLowFreqGain AmbientReverbLFReference " +
            "AmbientReverbRoomHighFreqGain AmbientReverbHFReference DelayVolume DelayTime DelayDecayTime " +
            "DelayWetmix DelayLowpassFilterFreq AmbientOutDelaySend AmbientOutReverbSend AmbCommonEffectsSend " +
            "AmbientOutDayEffectsSend AmbientOutNightEffectsSend CommonAmbOutEffectsSend CommonAmbInEffectsSend " +
            "DelayToReverbSendLevel").Split(' '));
        static NativeSpatialAudio.State _owner;
        static AudioMixerParamsController _controller;
        static TableHandler _handler;
        static bool _failed, _refresh;
        internal static bool IsActive(string mapKey) => _owner?.MapKey == mapKey &&
            _controller != null && _handler?.Failed == false && Enabled[mapKey].Value;

        internal static void Install()
        {
            // Same maps as NativeSpatialAudio: Tick indexes Enabled with the acoustic state's MapKey.
            foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5, ArenaMapRegistry.AirPit })
                Enabled[map.Key] = Plugin.Instance.Settings.Bind("Audio (" + map.Key + ")", "NativeRoomReverb", true,
                    "Restore original room-dependent ambient reverb, delay and effect sends. Requires NativeSpatialAudio. " +
                    "Live. Equator ambience uses a compatible effect bus because EFT mutes its original return. Gameplay reverb is unchanged.");
            AudioSettings.OnAudioConfigurationChanged += deviceChanged => _refresh = true;
        }

        internal static void Tick(NativeSpatialAudio.State state)
        {
            if (state != _owner) { Stop(); _owner = state; _failed = false; }
            if (state == null) return;
            if (!Enabled[state.MapKey].Value) { ReleaseController(); _failed = false; return; }
            if (_refresh) { ReleaseController(); _refresh = false; _failed = false; }
            if (_failed || _controller != null || !state.Finished || state.Released ||
                state.System == null || !SpatialAudioSystem.Initialized ||
                !Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started ||
                !MonoBehaviourSingleton<BetterAudio>.Instantiated || state.System.ListenerCurrentRoom == null) return;
            try
            {
                // A real native ambient manager already owns this exact parameter family.
                if (MonoBehaviourSingleton<AmbientAudioSystem>.Instantiated)
                    throw new InvalidOperationException("Native ambient manager already owns the mixer");
                var data = state.Data["mixer_effects"] as JObject;
                NativeSpatialAudio.Require(data != null && (int)data["schema_version"] == 1 &&
                    (string)data["map_key"] == state.MapKey, "missing/invalid mixer table identity");
                _handler = new TableHandler(state, data, MonoBehaviourSingleton<BetterAudio>.Instance.Master);
                _controller = new AudioMixerParamsController(_handler);
                if (_handler.Failed) throw new InvalidOperationException("Initial mixer application failed");
                Plugin.Log.LogDebug($"{Tag} {state.MapKey}: native room controller active; 26 original parameters, detailed room types mapped; gameplay snapshots retained");
            }
            catch (Exception e)
            {
                ReleaseController(); _failed = true;
                Plugin.Log.LogError($"{Tag} Startup rejected; existing audio retained: {e}");
            }
        }

        internal static void Stop()
        {
            ReleaseController(); _owner = null; _failed = false;
        }

        static void ReleaseController()
        {
            Bay5AmbientAudio.RestoreReverbRouting();
            _controller?.Dispose(); _controller = null;
            _handler?.Release(); _handler = null;
        }

        internal static void Log()
        {
            if (!DeveloperOptions.Enabled) return;
            if (!MonoBehaviourSingleton<BetterAudio>.Instantiated) return;
            var mixer = MonoBehaviourSingleton<BetterAudio>.Instance.Master;
            string Value(string key) => mixer.GetFloat(key, out float v) ? v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "missing";
            var room = _owner?.System?.ListenerCurrentRoom;
            Plugin.Log.LogDebug($"{Tag} room={room?.ID}/{room?.Type} active={_controller != null && _handler?.Failed == false} " +
                $"ambientReturn={Value("AmbientReverbVolume")} ambientMix={Value("AmbientReverbMix")} " +
                $"outSend={Value("AmbientOutReverbSend")} inSend={Value("CommonAmbInEffectsSend")} commonSend={Value("AmbCommonEffectsSend")} " +
                $"gameplayReturn=unexposed playerEnvironment={GamePlayerOwner.MyPlayer?.Environment}");
        }

        sealed class TableHandler : IEnvironmentMixerParamsHandler
        {
            readonly AudioMixer _mixer;
            readonly Dictionary<int, int> _categories = new Dictionary<int, int> { [67] = 67 };
            readonly Dictionary<string, Dictionary<int, float>> _tables = new Dictionary<string, Dictionary<int, float>>();
            readonly Dictionary<string, float> _applied = new Dictionary<string, float>();
            readonly HashSet<string> _yielded = new HashSet<string>();
            internal bool Failed;

            internal TableHandler(NativeSpatialAudio.State state, JObject data, AudioMixer mixer)
            {
                _mixer = mixer;
                var active = new HashSet<long>(state.Data["groups"][0]["room_ids"].Select(x => (long)x));
                foreach (var room in state.Data["rooms"].Where(r => active.Contains((long)r["id"])))
                {
                    int type = (int)room["type"], category = (bool)room["isolated"] ? 8 : (bool)room["outdoor"] ? 67 : 4;
                    NativeSpatialAudio.Require(type != 0 && (!_categories.TryGetValue(type, out int previous) || previous == category),
                        "conflicting mixer room category");
                    _categories[type] = category;
                }
                // Validate the complete payload and target mixer before writing even one value.
                foreach (var role in new[] { "effects_sends", "delay", "reverb", "effects_volumes" })
                foreach (var parameter in data["handlers"][role]["parameters"])
                {
                    string name = (string)parameter["param_name"];
                    NativeSpatialAudio.Require(Allowed.Contains(name) && !_tables.ContainsKey(name) && mixer.GetFloat(name, out _),
                        "unknown/duplicate/unavailable mixer parameter: " + name);
                    var values = new Dictionary<int, float>();
                    foreach (var v in parameter["values"])
                    {
                        int key = (int)v["room_type"]; float value = (float)v["value"];
                        NativeSpatialAudio.Require((key == 4 || key == 8 || key == 67) && !float.IsNaN(value) && !float.IsInfinity(value),
                            "invalid mixer category/value");
                        values.Add(key, value);
                    }
                    NativeSpatialAudio.Require(values.Count == 3, "incomplete mixer categories");
                    _tables.Add(name, values);
                }
                NativeSpatialAudio.Require(_tables.Count == Allowed.Count, "incomplete mixer parameters");
            }

            public void Apply(AudioMixer mixer, EAudioRoomTypeMask type)
            {
                if (Failed) return;
                try
                {
                    if ((int)type == 0) return; // Native storage clear during teardown.
                    NativeSpatialAudio.Require(mixer == _mixer && _categories.TryGetValue((int)type, out _), "unmapped listener room");
                    int category = _categories[(int)type];
                    foreach (var parameter in _tables)
                    {
                        string name = parameter.Key;
                        if (_yielded.Contains(name)) continue;
                        if (_applied.TryGetValue(name, out float previous) &&
                            (!mixer.GetFloat(name, out float current) || !Mathf.Approximately(previous, current)))
                        {
                            _yielded.Add(name); _applied.Remove(name);
                            Plugin.Log.LogDebug($"{Tag} Yielded {name} to an external mixer writer");
                            continue;
                        }
                        float value = parameter.Value[category];
                        NativeSpatialAudio.Require(mixer.SetFloat(name, value), "mixer rejected " + name);
                        _applied[name] = value;
                    }
                    Plugin.Log.LogDebug($"{Tag} applied room type={type}, source category={(EAudioRoomTypeMask)category}, owned={_applied.Count}");
                    Log();
                }
                catch (Exception e)
                {
                    Failed = true; Release();
                    Plugin.Log.LogError($"{Tag} Mixer application stopped: {e}");
                }
            }

            internal void Release()
            {
                if (_mixer == null || _applied.Count == 0) return;
                int cleared = 0, preserved = 0;
                foreach (var parameter in _applied)
                    if (_mixer.GetFloat(parameter.Key, out float current) && Mathf.Approximately(current, parameter.Value))
                    {
                        // SetFloat(old number) would leave a script override blocking future snapshots.
                        if (_mixer.ClearFloat(parameter.Key)) cleared++;
                        else Plugin.Log.LogWarning($"{Tag} Could not release mixer override {parameter.Key}");
                    }
                    else preserved++;
                _applied.Clear();
                Plugin.Log.LogDebug($"{Tag} released overrides={cleared}, preserved external={preserved}; snapshot control restored");
            }
        }
    }
}
