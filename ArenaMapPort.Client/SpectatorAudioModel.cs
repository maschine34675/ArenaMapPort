#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ArenaMapPort.Client
{
    #pragma warning disable CS0649 // Package fields are populated by Newtonsoft.Json.
    // Pure package and timing rules, shared with offline tests. No global Unity random state.
    internal sealed class SpectatorAudioManifest
    {
        public int schema, trigger;
        public string bank;
        public SpectatorAudioClip[] clips;

        public void Validate()
        {
            if (schema != 1 || trigger != 1123 || bank != "Participating" || clips == null || clips.Length != 13)
                throw new InvalidDataException("unsupported spectator audio package");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in clips)
                if (c == null || c.name == null || !Regex.IsMatch(c.name, @"^arena_fan(?:[7-9]|1[0-3])_participating_\d{2}$") ||
                    !names.Add(c.name) || c.file != c.name + ".wav" || c.channels != 1 || c.sample_rate != 44100 ||
                    c.frames < 22050 || c.frames > 220500 || c.bytes != 44 + c.frames * 2 ||
                    c.sha256 == null || !Regex.IsMatch(c.sha256, @"^[0-9a-f]{64}$"))
                    throw new InvalidDataException("invalid spectator audio clip");
        }
    }

    internal sealed class SpectatorAudioClip
    {
        public string name, file, sha256;
        public int channels, sample_rate, frames, bytes;
    }

    internal sealed class SpectatorAudioSchedule
    {
        public const int MaxConcurrent = 4;
        readonly Random _random;
        bool _running;
        int _lastSpeaker = -1, _lastClip = -1;
        public double NextAt { get; private set; } = double.PositiveInfinity;
        public SpectatorAudioSchedule(int seed) { _random = new Random(seed); }
        public void Start(double now)
        {
            if (_running) return;
            _running = true;
            NextAt = now + 2 + _random.NextDouble() * 3;
        }
        public void Stop() { _running = false; NextAt = double.PositiveInfinity; }
        public bool Due(double now, int activeVoices = 0) => _running && activeVoices < MaxConcurrent && now >= NextAt;
        public void Retry(double now) { NextAt = now + 3; }
        public void Played(double now, int speaker, int clip)
        {
            _lastSpeaker = speaker; _lastClip = clip;
            NextAt = now + .8 + _random.NextDouble() * .8;
        }
        public int ChooseClip(int count, ISet<int> busy = null) =>
            Pick(Enumerable.Range(0, count).Where(i => busy == null || !busy.Contains(i)).ToList(), _lastClip);

        public int ChooseSpeaker(float[] distances, float maxDistance, ISet<int> busy = null)
        {
            // Prefer the nearest section of the stands, independently of the view direction.
            float nearest = float.PositiveInfinity;
            foreach (float d in distances) if (d >= 0 && d <= maxDistance && d < nearest) nearest = d;
            var eligible = new List<int>();
            for (int i = 0; i < distances.Length; i++)
                if (distances[i] >= 0 && distances[i] <= Math.Min(maxDistance, nearest + 15) &&
                    (busy == null || !busy.Contains(i))) eligible.Add(i);
            return Pick(eligible, _lastSpeaker);
        }

        int Pick(List<int> choices, int previous)
        {
            if (choices.Count > 1) choices.Remove(previous);
            return choices.Count == 0 ? -1 : choices[_random.Next(choices.Count)];
        }
    }
    #pragma warning restore CS0649
}
