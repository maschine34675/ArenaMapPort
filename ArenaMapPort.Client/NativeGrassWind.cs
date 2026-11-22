using System;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The wind of the native grass (user, 2026-11-22: the grass does not move in the wind, it is only pressed flat when walked over).
    ///
    /// What vanilla does: EFT's GPUInstancer/Foliage_Simple has vertex wind code in every draw pass. It reads no _Time; its only time-varying input is the GLOBAL vector _WindTimeScaledVector (cbuffer
    /// EachFrameUpdate), which scrolls the lookup into _WindWaveNormalTexture (direction and strength come from the global _WindVector, which the weather rig already feeds every frame). In EFT 0.16.9.5 only
    /// EFT.Weather.WindCalculator.SetupFoliage writes _WindTimeScaledVector and _PrevWindTimeScaledVector: from Camera.onPreCull, for every camera that culls, phase += deltaTime * factor, with the factor
    /// of WindCalculator.SetWind. A calculator exists only after WindController.SetWind(TreeWind.Settings[]) with a non-empty array (LevelSettings.ApplyTreeWindSettings), and it sets the global float
    /// _WindCalculated to 1 while it is attached to a camera.
    /// What the ported scene lacks: no level settings, so no calculator (host: WindController._windCalculator is null on Block). The phase never advances and the grass stands still.
    /// What this does: while the native grass is built (NativeGrassRepairs.Arm after a successful build, Release in every teardown) it subscribes ONE cached delegate to Camera.onPreCull and reproduces
    /// SetupFoliage and nothing else (NativeGrassWindModel: factor from the global _WindVector, phase advance, the two global vectors), for every camera like EFT's own handler. When _WindCalculated says that
    /// EFT's own calculator is alive it writes nothing. It sets no _WindCalculated, no _impostorsWind and no TreeWind, creates no calculator and no compute shader. Release writes zero to both globals only
    /// when this class wrote them. A vanilla map never has the subscription.
    /// Key: Map (&lt;key&gt;)/GrassWind (bool, default true, live, not public): off freezes the grass where it stands, on continues.
    /// Status: the model is unit-tested and the glue source-pinned; host check pending. Logs: Debug, tag [GrassWind].
    /// </summary>
    internal static class NativeGrassWind
    {
        const string Tag = "[GrassWind]";
        internal const string KeyName = "GrassWind";
        internal const bool KeyDefault = true;

        static readonly int WindVectorId = Shader.PropertyToID("_WindVector");
        static readonly int WindCalculatedId = Shader.PropertyToID("_WindCalculated");
        static readonly int WindTimeScaledVectorId = Shader.PropertyToID("_WindTimeScaledVector");
        static readonly int PrevWindTimeScaledVectorId = Shader.PropertyToID("_PrevWindTimeScaledVector");

        // the ONE delegate, so that the subscription and the removal are the same object
        static readonly Camera.CameraCallback Handler = OnPreCull;

        static readonly Dictionary<string, ConfigEntry<bool>> _entries = new Dictionary<string, ConfigEntry<bool>>(StringComparer.Ordinal);
        static readonly NativeGrassWindPhase _phase = new NativeGrassWindPhase();

        static bool _armed;
        static bool _enabled = KeyDefault;
        static bool _wrote;
        static string _armedKey;
        static int _calls;
        static int _armFrame;
        static float _factorX;
        static float _factorY;
        static Vector4 _current;  // z and w stay 0 as in EFT
        static Vector4 _previous;

        /// <summary>Binds Map (&lt;key&gt;)/GrassWind for every map with native grass through Settings. Never throws.</summary>
        internal static void Install()
        {
            try
            {
                foreach (var map in ArenaMapRegistry.All)
                {
                    if (!map.HasNativeGrass) continue;
                    string key = map.Key;
                    ConfigEntry<bool> entry = Plugin.Instance.Settings.Bind(NativeGrassKeys.SectionOf(key), KeyName, KeyDefault,
                        "Wind movement of the native grass of this map: the grass sways with the weather's wind while it is built (EFT's foliage shader scrolls its wave lookup by a time vector that the port advances like EFT's own wind calculator). " +
                        "Off freezes the grass where it stands, on continues. Live.");
                    entry.SettingChanged += (_, __) => { if (_armedKey == key) _enabled = entry.Value; };
                    _entries[key] = entry;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{Tag} the key could not be bound ({e.GetType().Name}: {e.Message}); the grass wind stays on its default");
            }
        }

        /// <summary>Called after a successful grass build: subscribes the handler, resets the phase, logs one Debug line. Never throws.</summary>
        internal static void Arm(string mapKey)
        {
            try
            {
                if (_armed) Release();
                ConfigEntry<bool> entry;
                _enabled = !_entries.TryGetValue(mapKey ?? string.Empty, out entry) || entry.Value;
                _armedKey = mapKey;
                _phase.Reset();
                _wrote = false;
                _calls = 0;
                _armFrame = Time.frameCount;
                Vector4 wind = Shader.GetGlobalVector(WindVectorId);
                NativeGrassWindModel.Factor(wind.x, wind.y, out float fx, out float fy);
                string line = $"{Tag} map={mapKey} armed: wind=({wind.x:0.###}, {wind.y:0.###}) factor=({fx:0.#####}, {fy:0.#####}) calculated={(Shader.GetGlobalFloat(WindCalculatedId) >= 0.5f ? 1 : 0)} enabled={_enabled}";
                Camera.onPreCull += Handler;
                _armed = true;
                Plugin.Log.LogDebug(line);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{Tag} arm failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>Called by every grass teardown; safe when not armed. Unsubscribes, zeroes the two globals only if this class wrote them. Never throws.</summary>
        internal static void Release()
        {
            if (!_armed) return;
            try
            {
                Camera.onPreCull -= Handler;
                _armed = false;
                if (_wrote)
                {
                    Shader.SetGlobalVector(WindTimeScaledVectorId, Vector4.zero);
                    Shader.SetGlobalVector(PrevWindTimeScaledVectorId, Vector4.zero);
                }
                Plugin.Log.LogDebug($"{Tag} released: phase=({_phase.PhaseX:0.####}, {_phase.PhaseY:0.####}) preCullCalls={_calls} frames={Time.frameCount - _armFrame}");
                _wrote = false;
                _armedKey = null;
            }
            catch (Exception e)
            {
                _armed = false;
                Plugin.Log.LogDebug($"{Tag} release failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Camera.onPreCull, for EVERY camera that culls, as EFT's WindCalculator.OnPreCull (parity). One static bool read when disarmed or switched off; otherwise a handful of engine reads and writes that cannot
        /// throw: no search, no log, no allocation. Nothing is written while EFT's own calculator is alive. Like EFT's handler it advances the phase once per culling camera, not once per frame, so the sway
        /// speed depends on how many cameras cull (game, optic, depth photo): the same as vanilla, not a defect. A grass rebuild during a raid (camera replaced) restarts the phase at 0, which EFT's
        /// calculator does not do: a small visible snap, accepted.
        /// </summary>
        static void OnPreCull(Camera camera)
        {
            if (!_armed || !_enabled) return;
            if (Shader.GetGlobalFloat(WindCalculatedId) >= 0.5f) return;
            Vector4 wind = Shader.GetGlobalVector(WindVectorId);
            NativeGrassWindModel.Factor(wind.x, wind.y, out _factorX, out _factorY);
            _phase.Advance(Time.deltaTime, _factorX, _factorY);
            _current.x = _phase.PhaseX;
            _current.y = _phase.PhaseY;
            _previous.x = _phase.PreviousX;
            _previous.y = _phase.PreviousY;
            Shader.SetGlobalVector(WindTimeScaledVectorId, _current);
            Shader.SetGlobalVector(PrevWindTimeScaledVectorId, _previous);
            _wrote = true;
            _calls++;
        }
    }
}
