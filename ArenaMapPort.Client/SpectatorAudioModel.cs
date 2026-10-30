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
        /// <summary>The crowd murmur bed (SpectatorCrowdBedModel.cs); null in a package without one, which is still a valid schema-1 manifest.</summary>
        public SpectatorAudioBed bed;

        /// <summary>Checks the bed of a package that has one; independent of <see cref="Validate"/>, so a damaged bed never takes the calls with it.</summary>
        public void ValidateBed()
        {
            if (schema != 1 || trigger != 1123 || bank != "Participating" || bed == null)
                throw new InvalidDataException("unsupported spectator audio package");
            bed.Validate();
        }

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
        public const int MaxConcurrent = 2;
        // 2026-10-30 (user: "die Pausen ... nochmal verlaengern"): the single calls are sparser now that the crowd bed carries the continuous murmur.
        // The first call comes 3 to 8 s after the raid starts (before: 2 to 5 s), the starts that follow are 4.0 to 8.0 s apart (before: 1.6 to 3.2 s).
        // These are constants, not config keys, so there is no stored value to re-seed. The longest shipped call is 3.29 s: with a 4.0 s minimum gap two calls no
        // longer overlap in practice, and the second voice slot is only the cap for a longer clip.
        public const double InitialDelayMinSeconds = 3, InitialDelaySpanSeconds = 5;
        public const double GapMinSeconds = 4.0, GapSpanSeconds = 4.0;
        readonly Random _random;
        bool _running;
        int _lastSpeaker = -1, _lastClip = -1;
        public double NextAt { get; private set; } = double.PositiveInfinity;
        public SpectatorAudioSchedule(int seed) { _random = new Random(seed); }
        public void Start(double now)
        {
            if (_running) return;
            _running = true;
            NextAt = now + InitialDelayMinSeconds + _random.NextDouble() * InitialDelaySpanSeconds;
        }
        public void Stop() { _running = false; NextAt = double.PositiveInfinity; }
        public bool Due(double now, int activeVoices = 0) => _running && activeVoices < MaxConcurrent && now >= NextAt;
        public void Retry(double now) { NextAt = now + 3; }
        public void Played(double now, int speaker, int clip)
        {
            _lastSpeaker = speaker; _lastClip = clip;
            NextAt = now + GapMinSeconds + _random.NextDouble() * GapSpanSeconds;
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
