using System;
using System.Collections.Generic;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>Opt-in silent remote-source probe: exercises the real pooled source, native Combined
    /// occlusion and final filter setters. Never changes the listener's rooms or authored acoustics.
    /// Readbacks demonstrate processing, not subjective listening. Off/unload returns the source.</summary>
    internal static class SpatialAudioProbe
    {
        static ConfigEntry<bool> _enabled;
        static ConfigEntry<Vector3> _position;
        static BetterSource _source;
        static GameObject _object;
        static AudioClip _silence;
        static SpatialAudioSystem _system;
        static int _container = -1;
        static float _lowPass = 22000, _highPass, _propagation, _obstruction, _occlusion;
        static short _sourceRoom = -1;
        static float _nextLog;

        internal static void Install(Harmony harmony)
        {
            _enabled = Plugin.Instance.Settings.Bind("Audio diagnostics", "SpatialProbe", false,
                "Test only: run a silent remote sound through native wall/portal attenuation. Logs actual room, volume and filter results. Live; off releases it.");
            _position = Plugin.Instance.Settings.Bind("Audio diagnostics", "SpatialProbePosition", Vector3.zero,
                "World position of the silent remote acoustic test source. Live. Does not change the player's room or any map geometry.");
            if (!DeveloperOptions.Enabled) return;
            harmony.Patch(AccessTools.Method(typeof(BetterSource), nameof(BetterSource.SetLowPassFilterParameters)),
                postfix: new HarmonyMethod(typeof(SpatialAudioProbe), nameof(LowPassPostfix)));
            harmony.Patch(AccessTools.Method(typeof(BetterSource), nameof(BetterSource.SetHighPassFilterParameters)),
                postfix: new HarmonyMethod(typeof(SpatialAudioProbe), nameof(HighPassPostfix)));
            harmony.Patch(AccessTools.Method(typeof(SpatialAudioOccluder), nameof(SpatialAudioOccluder.ApplyOcclusionToSource)),
                postfix: new HarmonyMethod(typeof(SpatialAudioProbe), nameof(ApplyPostfix)));
            Plugin.Instance.gameObject.AddComponent<Runner>();
        }

        static void LowPassPostfix(BetterSource __instance, float cutoffFreq) { if (__instance == _source) _lowPass = cutoffFreq; }
        static void HighPassPostfix(BetterSource __instance, float cutoffFreq) { if (__instance == _source) _highPass = cutoffFreq; }
        static void ApplyPostfix(SpatialAudioOccluder __instance, BetterSource source)
        {
            if (source != _source) return;
            _sourceRoom = __instance.SourceContainer.CurrentAudioRoom.ID;
            _propagation = __instance.PropagationEffect;
            _obstruction = __instance.ObstructionEffect;
            _occlusion = __instance.OcclusionEffect;
        }

        internal static void Stop()
        {
            if (_source == null && _object == null) return;
            if (_system != null && SpatialAudioSystem.Initialized && _container >= 0) _system.UnregisterContainer(_container);
            _container = -1;
            if (_source != null) _source.Release();
            _source = null; _system = null;
            if (_object != null) Object.Destroy(_object);
            if (_silence != null) Object.Destroy(_silence);
            _object = null; _silence = null; _sourceRoom = -1;
            Plugin.Log.LogInfo(NativeSpatialAudio.Tag + " silent probe released");
        }

        internal static void Log()
        {
            if (_source == null || _system == null) return;
            var listener = _system.ListenerCurrentRoom;
            Plugin.Log.LogInfo($"{NativeSpatialAudio.Tag} probe listener={listener?.ID}/{listener?.Name}/{listener?.Type} " +
                $"source={_sourceRoom}@{_source.Position:F2} container={_container} playing={_source.PlayBackState} " +
                $"audioPlaying={_source.source1.isPlaying} samples={_source.source1.timeSamples} " +
                $"propagation={_propagation:F4} obstruction={_obstruction:F4} occlusion={_occlusion:F4} " +
                $"appliedVolume={_source.OcclusionVolumeFactor:F4} LPF={_lowPass:F1} HPF={_highPass:F1}");
        }

        public sealed class Runner : MonoBehaviour
        {
            void Update()
            {
                try
                {
                    var state = NativeSpatialAudio.Current;
                    if (!_enabled.Value || state == null || !state.Finished || !SpatialAudioSystem.Initialized)
                    { Stop(); return; }
                    if (!state.System.ListenerCurrentRoom.IsValid || GamePlayerOwner.MyPlayer == null ||
                        !Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started) return;
                    if (_source == null)
                    {
                        _system = state.System;
                        _object = new GameObject("ArenaMapPort Silent Acoustic Probe");
                        _object.transform.SetParent(state.Root.transform, false);
                        _object.transform.position = _position.Value;
                        _silence = AudioClip.Create("ArenaMapPort silent diagnostic", 22050, 1, 22050, false);
                        _source = MonoBehaviourSingleton<BetterAudio>.Instance.GetSource(BetterAudio.AudioSourceGroupType.Environment);
                        _source.Position = _position.Value;
                        _source.SetRolloff(120); _source.SetSpatialBlend(1);
                        _source.IncludeInOcclusionProcess(true); _source.EnabledEQ(true); _source.Loop = true;
                        _source.Play(_silence, null, 1, 1, false, false);
                        _container = _system.ProcessSourceOcclusion(_object, _source, EOcclusionTest.Combined);
                        _nextLog = Time.unscaledTime + 2;
                    }
                    _object.transform.position = _position.Value;
                    _source.Position = _position.Value;
                    if (Time.unscaledTime >= _nextLog) { _nextLog = Time.unscaledTime + 3; Log(); }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(NativeSpatialAudio.Tag + " diagnostic failed: " + e);
                    _enabled.Value = false;
                    Stop();
                }
            }
        }
    }
}
