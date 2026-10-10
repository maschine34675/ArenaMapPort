using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Occasional faults on selected, owned neon fixtures. Native LightFlicker writes only Light
    /// intensity; these strips also need their Standard shader emission and haze synchronized.
    /// One clock-derived envelope drives both, without modifying the original arcade flicker.
    /// Local native API and GPU evidence: docs/progress/equator-night-flicker.md.
    /// </summary>
    internal sealed class NeonLightFlicker : IDisposable
    {
        const string Tag = "[ArenaMapPort][NeonFlicker]";
        static readonly int Emission = Shader.PropertyToID("_EmissionColor");
        static ConfigEntry<float> _strength, _frequency;
        readonly List<Entry> _entries = new List<Entry>();
        readonly float[] _baseIntensities;
        readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        readonly double _started = Time.timeAsDouble;
        bool _disposed, _sampling;
        internal bool Sampling => _sampling;

        internal static void BindConfig(EventHandler changed)
        {
            const string section = "Night neon (arena_equator)";
            _strength = Plugin.Instance.Settings.Bind(section, "FlickerStrength", EquatorProfiles.NightDefault("FlickerStrength", .8f),
                new ConfigDescription("Depth of occasional faults on three selected neon lights. " +
                    "0 = steady; 1 = brief complete blackouts. Strip, light and haze react together. Live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            _frequency = Plugin.Instance.Settings.Bind(section, "FlickerFrequency", EquatorProfiles.NightDefault("FlickerFrequency", 1f),
                new ConfigDescription("Frequency of occasional neon faults: 1 = about one burst per 20 seconds " +
                    "per selected fixture, 3 = three times as often. Does not speed up each burst. 0 = steady. Live.",
                    new AcceptableValueRange<float>(0f, 3f)));
            _strength.SettingChanged += changed;
            _frequency.SettingChanged += changed;
        }

        internal NeonLightFlicker(IList<Light> lights, IList<MeshRenderer> strips, float[] baseIntensities)
        {
            _baseIntensities = baseIntensities;
            var fixtures = ArenaMapRegistry.Equator.NeonFixtures;
            for (int i = 0; i < fixtures.Length; i++)
                if (fixtures[i].FlickerSeed > 0)
                    _entries.Add(new Entry { Index = i, Fixture = fixtures[i], Light = lights[i], Strip = strips[i] });
            Plugin.Log.LogInfo($"{Tag} selected={_entries.Count}: {string.Join(", ", _entries.Select(e => e.Fixture.Name))}");
        }

        internal void Update()
        {
            if (_disposed) return;
            double elapsed = Time.timeAsDouble - _started;
            foreach (var entry in _entries)
            {
                float factor = ArenaNeonFlicker.Evaluate(elapsed, entry.Fixture.FlickerSeed, _strength.Value, _frequency.Value);
                entry.Factor = factor;
                entry.Light.intensity = _baseIntensities[entry.Index] * factor;
                entry.Light.enabled = entry.Light.intensity > 0;
                var emission = entry.Strip.sharedMaterial.GetColor(Emission) * factor;
                emission.a = 1;
                _block.SetColor(Emission, emission);
                entry.Strip.SetPropertyBlock(_block);
            }
        }

        internal void Probe() => Plugin.Log.LogInfo($"{Tag} strength={_strength.Value:F2} frequency={_frequency.Value:F2} " +
            $"selected={_entries.Count} elapsed={Time.timeAsDouble - _started:F2}s " +
            string.Join("; ", _entries.Select(e => $"{e.Fixture.Name}={e.Factor:F3}")));

        // Explicit F7 only: sample actual native Light values, renderer property blocks and haze
        // activation after LateUpdate. Stills alone cannot prove that a short fault is synchronized.
        internal IEnumerator Sample()
        {
            if (_sampling || _disposed) yield break;
            _sampling = true;
            float strength = _strength.Value, frequency = _frequency.Value;
            var stats = _entries.Select(_ => new SampleStats()).ToArray();
            var readback = new MaterialPropertyBlock();
            var wait = new WaitForEndOfFrame();
            double end = Time.realtimeSinceStartupAsDouble + 40;
            bool interrupted = false;
            Plugin.Log.LogInfo($"{Tag} 40-second synchronization sample started; strength={strength:F2} frequency={frequency:F2}");
            try
            {
                while (!_disposed && Time.realtimeSinceStartupAsDouble < end)
                {
                    yield return wait;
                    if (_disposed) yield break;
                    if (strength != _strength.Value || frequency != _frequency.Value)
                    { interrupted = true; break; }
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        var entry = _entries[i]; var sample = stats[i];
                        if (entry.Light == null || entry.Strip == null) { interrupted = true; break; }
                        sample.Frames++;
                        sample.Min = Mathf.Min(sample.Min, entry.Factor);
                        sample.Max = Mathf.Max(sample.Max, entry.Factor);
                        if (entry.Factor == 0) sample.BlackoutFrames++;
                        if (entry.Factor < .999f && !sample.InDip) sample.Dips++;
                        sample.InDip = entry.Factor < .999f;
                        sample.LightError = Mathf.Max(sample.LightError,
                            Mathf.Abs(entry.Light.intensity - _baseIntensities[entry.Index] * entry.Factor));
                        entry.Strip.GetPropertyBlock(readback);
                        var actual = readback.GetColor(Emission);
                        var expected = entry.Strip.sharedMaterial.GetColor(Emission) * entry.Factor;
                        sample.EmissionError = Mathf.Max(sample.EmissionError, Mathf.Max(
                            Mathf.Abs(actual.r - expected.r), Mathf.Max(Mathf.Abs(actual.g - expected.g), Mathf.Abs(actual.b - expected.b))));
                        var volume = entry.Light.GetComponent<VolumetricLight>();
                        if (volume != null)
                        {
                            sample.HazeFrames++;
                            bool visible = volume.ScatteringCoef > 0 && entry.Light.enabled && entry.Light.intensity >= .001f;
                            if ((volume.enabled && volume.NeedToPrerender) != visible) sample.HazeMismatches++;
                        }
                    }
                    if (interrupted) break;
                }
                if (!_disposed)
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        var s = stats[i];
                        Plugin.Log.LogInfo($"{Tag} sample { (interrupted ? "interrupted" : "complete") } '{_entries[i].Fixture.Name}' " +
                            $"frames={s.Frames} factor={s.Min:F3}..{s.Max:F3} dips={s.Dips} blackoutFrames={s.BlackoutFrames} " +
                            $"maxLightError={s.LightError:F6} maxEmissionError={s.EmissionError:F6} " +
                            $"hazeMismatches={s.HazeMismatches}/{s.HazeFrames}");
                    }
            }
            finally { _sampling = false; }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var entry in _entries)
                if (entry.Strip != null) entry.Strip.SetPropertyBlock(null);
            _entries.Clear();
        }

        sealed class Entry
        {
            internal int Index;
            internal ArenaNeonFixture Fixture;
            internal Light Light;
            internal MeshRenderer Strip;
            internal float Factor = 1;
        }

        sealed class SampleStats
        {
            internal int Frames, Dips, BlackoutFrames, HazeFrames, HazeMismatches;
            internal float Min = 1, Max, LightError, EmissionError;
            internal bool InDip;
        }
    }
}
