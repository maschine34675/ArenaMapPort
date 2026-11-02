#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ArenaMapPort.Client
{
    #pragma warning disable CS0649 // Package fields are populated by Newtonsoft.Json.
    /// <summary>The optional <c>bed</c> object of spectator-audio.json: Arena's neutral in-match crowd bed (arena_fans_ambient_neutral, the Neutral bank of
    /// ambient_voice.bundle; since 2026-11-02, before that the results-screen bed arena_fans_ambient_postmatch), downmixed to mono, brought to the approved
    /// level and with its loop seam fixed offline (tools/extract/extract_spectator_audio.py). A manifest without it is still a valid schema-1 manifest and
    /// simply has no bed. Fields the client does not use (the source path id, the clip's statistics before the downmix, peak, rms) are ignored.</summary>
    internal sealed class SpectatorAudioBed
    {
        public const string ClipName = "arena_fans_ambient_neutral";
        public string name, file, sha256;
        public int channels, sample_rate, frames, bytes, seam_crossfade_ms;

        public void Validate()
        {
            if (name != ClipName || file != ClipName + ".wav" || channels != 1 || sample_rate != 44100 ||
                frames < 10 * 44100 || frames > 120 * 44100 || bytes != 44 + frames * 2 ||
                seam_crossfade_ms < 1 || seam_crossfade_ms > 5000 ||
                sha256 == null || !Regex.IsMatch(sha256, @"^[0-9a-f]{64}$"))
                throw new InvalidDataException("invalid spectator crowd bed");
        }
    }
    #pragma warning restore CS0649

    /// <summary>One spectator position (world space, as the audio emitters use it). A struct of its own keeps the model free of Unity types.</summary>
    internal struct BedPoint
    {
        public float X, Y, Z;
        public BedPoint(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    /// <summary>One stand of the crowd bed: a cluster of spectators with the parameters of its looping source.</summary>
    internal sealed class BedStand
    {
        public int Index, Members;
        /// <summary>Mean x, mean y and mean z of the members.</summary>
        public float X, Y, Z;
        /// <summary>The largest x/z distance of a member from the centre.</summary>
        public float Radius;
        public float MinDistance, MaxDistance;
        /// <summary>Where in the loop the source starts (i / stands), so the stands do not murmur in step.</summary>
        public float StartFraction;
        public float Pitch;
    }

    /// <summary>
    /// The Unity-free rules of the crowd murmur bed (docs/progress/spectator-audio.md "Crowd bed"): which stands the spectators form, what each stand's looping source
    /// is told, the distance law, and the volume law. SpectatorCrowdBed.cs is the only Unity file.
    ///
    /// Stands: deterministic k-means over x/z (maximin seeding from twelve evenly spaced first points, Lloyd iterations, the lowest squared error wins; no random
    /// numbers), k from 2 upward until every stand's radius is at most <see cref="MaxStandRadius"/> or k reaches <see cref="MaxStands"/>. The result is sorted by
    /// (x, z), so the start offsets and the pitches do not depend on the cluster numbering.
    ///
    /// Distance: Unity's Logarithmic rolloff stops attenuating at the maximum distance and keeps that level for ever (a murmur that never ends), so the source uses a
    /// Custom curve: full volume inside the stand, then 1/d like the logarithmic law, tapering to silence at the maximum distance.
    ///
    /// Volume: <c>AudioSource.volume</c> cannot exceed 1, but CrowdBedVolume goes to 2 and the excitement factor to 1.6. The decoded clip therefore carries a fixed
    /// gain of <see cref="ClipGain"/> (2 x 1.6) and the source volume is <c>volume x factor / ClipGain</c>: the level that reaches the listener is the clip's own
    /// level x volume x factor over the whole range.
    /// </summary>
    internal static class SpectatorCrowdBedModel
    {
        internal const int MaxStands = 6, Restarts = 12, MaxIterations = 100;
        /// <summary>A stand may not be wider than this before another stand is added (x/z radius, metres). 25 m, not the 30 m of the first draft (review 2026-10-30):
        /// at 30 m Equator's crowd already fits two stands of 27 and 29 m, which puts both sources between the real stands, while at 25 m it splits into its four natural stands of
        /// 13 to 17 m; Bay5 gets 4, Air Pit and Bowl reach the cap of six.</summary>
        internal const float MaxStandRadius = 25f;
        internal const float MinDistanceFloor = 8f, MinDistanceCeiling = 60f;
        internal const float DefaultReach = 60f, MinReach = 10f, MaxReach = 150f;
        internal const float DefaultVolume = 1f, MaxVolume = 2f;
        /// <summary>Fixed gain of the decoded clip: the largest volume times the largest excitement factor.</summary>
        internal const float ClipGain = MaxVolume * CrowdExcitement.MaxFactor;
        /// <summary>Where the distance law starts to taper to silence, as a fraction of the way from the minimum to the maximum distance.</summary>
        internal const float TaperStart = 0.6f;
        internal const int RolloffSamples = 24;
        internal const float PitchLow = 0.98f, PitchSpan = 0.04f;

        internal static float ClampReach(float reach) => float.IsNaN(reach) ? DefaultReach : Math.Max(MinReach, Math.Min(MaxReach, reach));

        internal static float ClampVolume(float volume) => float.IsNaN(volume) ? DefaultVolume : Math.Max(0f, Math.Min(MaxVolume, volume));

        // ---- stands ------------------------------------------------------------------------------------------------------------------------

        /// <summary>The stands of a crowd; empty for no usable position. Non-finite positions are ignored. Deterministic.</summary>
        internal static BedStand[] Plan(IReadOnlyList<BedPoint> points, float reach)
        {
            var xs = new List<double>(); var ys = new List<double>(); var zs = new List<double>();
            if (points != null)
                foreach (var p in points)
                {
                    if (float.IsNaN(p.X) || float.IsInfinity(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.Y) || float.IsNaN(p.Z) || float.IsInfinity(p.Z)) continue;
                    xs.Add(p.X); ys.Add(p.Y); zs.Add(p.Z);
                }
            int n = xs.Count;
            if (n == 0) return new BedStand[0];
            reach = ClampReach(reach);
            double[] x = xs.ToArray(), y = ys.ToArray(), z = zs.ToArray();
            BedStand[] stands;
            int k = Math.Min(2, n);
            while (true)
            {
                stands = Build(x, y, z, Cluster(x, z, k));
                if (k >= MaxStands || k >= n || LargestRadius(stands) <= MaxStandRadius) break;
                k++;
            }
            for (int i = 0; i < stands.Length; i++)
            {
                var s = stands[i];
                s.Index = i;
                s.MinDistance = Math.Max(MinDistanceFloor, Math.Min(MinDistanceCeiling, s.Radius));
                s.MaxDistance = s.MinDistance + reach;
                s.StartFraction = (float)i / stands.Length;
                s.Pitch = stands.Length <= 1 ? 1f : PitchLow + PitchSpan * i / (stands.Length - 1);
            }
            return stands;
        }

        internal static float LargestRadius(BedStand[] stands)
        {
            float largest = 0;
            foreach (var s in stands) largest = Math.Max(largest, s.Radius);
            return largest;
        }

        /// <summary>The best of <see cref="Restarts"/> deterministic k-means runs; label per point (0..k-1, a label may be unused).</summary>
        static int[] Cluster(double[] x, double[] z, int k)
        {
            int n = x.Length, runs = Math.Min(Restarts, n);
            int[] best = null; double bestError = double.MaxValue;
            for (int run = 0; run < runs; run++)
            {
                var labels = Lloyd(x, z, k, (int)((long)run * n / runs), out double error);
                if (best == null || error < bestError - 1e-9) { best = labels; bestError = error; }
            }
            return best;
        }

        static int[] Lloyd(double[] x, double[] z, int k, int first, out double error)
        {
            int n = x.Length;
            var cx = new double[k]; var cz = new double[k];
            var taken = new bool[n];
            cx[0] = x[first]; cz[0] = z[first]; taken[first] = true;
            for (int j = 1; j < k; j++)
            {
                int pick = -1; double far = -1;
                for (int i = 0; i < n; i++)
                {
                    if (taken[i]) continue;
                    double nearest = double.MaxValue;
                    for (int c = 0; c < j; c++) nearest = Math.Min(nearest, Sq(x[i] - cx[c]) + Sq(z[i] - cz[c]));
                    if (nearest > far) { far = nearest; pick = i; }
                }
                if (pick < 0) { cx[j] = cx[0]; cz[j] = cz[0]; continue; }
                cx[j] = x[pick]; cz[j] = z[pick]; taken[pick] = true;
            }
            var label = new int[n];
            for (int i = 0; i < n; i++) label[i] = -1;
            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    int bestLabel = 0; double bestDistance = double.MaxValue;
                    for (int j = 0; j < k; j++)
                    {
                        double d = Sq(x[i] - cx[j]) + Sq(z[i] - cz[j]);
                        if (d < bestDistance) { bestDistance = d; bestLabel = j; }
                    }
                    if (label[i] != bestLabel) { label[i] = bestLabel; changed = true; }
                }
                var sx = new double[k]; var sz = new double[k]; var count = new int[k];
                for (int i = 0; i < n; i++) { sx[label[i]] += x[i]; sz[label[i]] += z[i]; count[label[i]]++; }
                for (int j = 0; j < k; j++)
                    if (count[j] > 0) { cx[j] = sx[j] / count[j]; cz[j] = sz[j] / count[j]; }
                if (!changed) break;
            }
            error = 0;
            for (int i = 0; i < n; i++) error += Sq(x[i] - cx[label[i]]) + Sq(z[i] - cz[label[i]]);
            return label;
        }

        static double Sq(double v) => v * v;

        static BedStand[] Build(double[] x, double[] y, double[] z, int[] label)
        {
            int k = 0;
            foreach (int l in label) k = Math.Max(k, l + 1);
            var sx = new double[k]; var sy = new double[k]; var sz = new double[k]; var count = new int[k];
            for (int i = 0; i < label.Length; i++) { sx[label[i]] += x[i]; sy[label[i]] += y[i]; sz[label[i]] += z[i]; count[label[i]]++; }
            var list = new List<BedStand>();
            for (int j = 0; j < k; j++)
            {
                if (count[j] == 0) continue;
                double cx = sx[j] / count[j], cz = sz[j] / count[j], radius = 0;
                for (int i = 0; i < label.Length; i++)
                    if (label[i] == j) radius = Math.Max(radius, Math.Sqrt(Sq(x[i] - cx) + Sq(z[i] - cz)));
                list.Add(new BedStand { Members = count[j], X = (float)cx, Y = (float)(sy[j] / count[j]), Z = (float)cz, Radius = (float)radius });
            }
            // A stable order: the numbering of k-means says nothing; west to east, then south to north.
            var order = new List<BedStand>(list);
            order.Sort((a, b) =>
            {
                int c = a.X.CompareTo(b.X);
                if (c == 0) c = a.Z.CompareTo(b.Z);
                if (c == 0) c = a.Y.CompareTo(b.Y);
                return c != 0 ? c : list.IndexOf(a).CompareTo(list.IndexOf(b));
            });
            return order.ToArray();
        }

        /// <summary>"4 stands (radii 13.3/14.0/16.2/17.1 m, centres (-19,10,-10) (17,10,-11) ...)": the one log line a raid gets.</summary>
        internal static string Describe(BedStand[] stands)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(stands.Length.ToString(inv)).Append(stands.Length == 1 ? " stand (radius " : " stands (radii ");
            for (int i = 0; i < stands.Length; i++)
                sb.Append(i > 0 ? "/" : "").Append(stands[i].Radius.ToString("0.0", inv));
            sb.Append(" m, ").Append(stands.Length == 1 ? "centre " : "centres ");
            for (int i = 0; i < stands.Length; i++)
                sb.Append(i > 0 ? " " : "").Append('(').Append(stands[i].X.ToString("0", inv)).Append(',').Append(stands[i].Y.ToString("0", inv))
                  .Append(',').Append(stands[i].Z.ToString("0", inv)).Append(')');
            return sb.Append(')').ToString();
        }

        // ---- distance law ------------------------------------------------------------------------------------------------------------------

        /// <summary>Gain at <paramref name="distance"/> metres: 1 inside <paramref name="min"/>, min / distance beyond it (6 dB per doubling), tapering to 0 at
        /// <paramref name="max"/> from <see cref="TaperStart"/> of the way there. Continuous, non-increasing.</summary>
        internal static float RolloffGain(float distance, float min, float max)
        {
            if (!(distance > min)) return 1f;
            if (distance >= max) return 0f;
            float s = (distance - min) / (max - min);
            float taper = 1f;
            if (s > TaperStart)
            {
                float u = (s - TaperStart) / (1f - TaperStart);
                taper = 1f - u * u * (3f - 2f * u);
            }
            return min / distance * taper;
        }

        /// <summary>The keys of the Custom rolloff curve: <paramref name="times"/> are distance / maximum distance (strictly increasing, 0..1), <paramref name="gains"/>
        /// the law above. Sampled more densely near the minimum distance, where the 1/d law bends most.</summary>
        internal static void RolloffKeys(float min, float max, out float[] times, out float[] gains)
        {
            times = new float[RolloffSamples + 1]; gains = new float[RolloffSamples + 1];
            times[0] = 0f; gains[0] = 1f;
            for (int j = 0; j < RolloffSamples; j++)
            {
                float fraction = (float)Math.Pow(j / (double)(RolloffSamples - 1), 1.6);
                float distance = j == RolloffSamples - 1 ? max : min + fraction * (max - min);
                times[j + 1] = distance / max;
                gains[j + 1] = RolloffGain(distance, min, max);
            }
        }

        // ---- volume law --------------------------------------------------------------------------------------------------------------------

        /// <summary>AudioSource.volume for the configured volume (0..2) and the excitement factor (1..1.6); never above 1.</summary>
        internal static float SourceVolume(float volume, float factor)
        {
            volume = ClampVolume(volume);
            if (float.IsNaN(factor)) factor = 1f;
            factor = Math.Max(1f, Math.Min(CrowdExcitement.MaxFactor, factor));
            return Math.Min(1f, volume * factor / ClipGain);
        }

        /// <summary>The PCM16 samples of the bed as the floats the clip carries: value / 32768 x <see cref="ClipGain"/>.</summary>
        internal static float[] ToClipFloats(short[] samples)
        {
            var result = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++) result[i] = samples[i] / 32768f * ClipGain;
            return result;
        }
    }

    /// <summary>
    /// The smoothed excitement of the crowd: a target level (0 calm to 1 peak, set by a mode plugin) that the stands follow with a time constant of two seconds, a transient
    /// swell that every reaction voice adds on top (docs/PLAN-CROWD-REACTIONS.md 4.4), and the volume factor the larger of the two maps to (1.0 to 1.6). The swell rises
    /// linearly to its amplitude in <see cref="SwellAttack"/> seconds and then decays exponentially with a time constant of <see cref="SwellTimeConstant"/> seconds; the level the
    /// factor uses is <c>max(smoothed external level, swell)</c> and never exceeds 1, so the factor never exceeds <see cref="MaxFactor"/> and the fixed
    /// <see cref="SpectatorCrowdBedModel.ClipGain"/> stays valid. No allocation; no Unity type.
    /// </summary>
    internal sealed class CrowdExcitement
    {
        internal const float TimeConstant = 2f, MaxFactor = 1.6f;
        internal const float SwellAttack = 0.3f, SwellTimeConstant = 3f;
        float _target, _current;
        float _swell, _swellPeak, _attackLeft;

        internal float Target => _target;
        /// <summary>The smoothed external level (what <see cref="SetTarget"/> asked for), without the swell.</summary>
        internal float Current => _current;
        /// <summary>The level of the transient swell (0 when none).</summary>
        internal float Swell => _swell;
        /// <summary>What the volume factor follows: the larger of the smoothed external level and the swell; 0..1.</summary>
        internal float Level => Math.Max(_current, _swell);
        internal float Factor => 1f + (MaxFactor - 1f) * Level;

        /// <summary>0..1; NaN counts as 0.</summary>
        internal static float Sanitize(float level) => float.IsNaN(level) ? 0f : Math.Max(0f, Math.Min(1f, level));

        internal void SetTarget(float level) { _target = Sanitize(level); }

        /// <summary>The current level jumps to the target (a source that starts now starts at the right level).</summary>
        internal void Snap() { _current = _target; }

        /// <summary>Starts a swell towards <paramref name="amplitude"/> (0..1, NaN counts as 0). An amplitude that does not exceed the swell that is already there (its level, or its
        /// peak while it still rises) is ignored, so a quieter reaction never cuts a louder one short.</summary>
        internal void AddSwell(float amplitude)
        {
            float a = Sanitize(amplitude);
            if (!(a > _swell) || (_attackLeft > 0f && !(a > _swellPeak))) return;
            _swellPeak = a;
            _attackLeft = SwellAttack;
        }

        internal void Reset() { _target = 0f; _current = 0f; _swell = 0f; _swellPeak = 0f; _attackLeft = 0f; }

        /// <summary>Moves the current level towards the target and the swell along its envelope by <paramref name="seconds"/>; true when either moved.</summary>
        internal bool Step(float seconds)
        {
            if (!(seconds > 0f)) return false;
            if (float.IsInfinity(seconds)) seconds = 3600f;
            bool moved = StepExternal(seconds);
            if (StepSwell(seconds)) moved = true;
            return moved;
        }

        bool StepExternal(float seconds)
        {
            if (_current == _target) return false;
            float before = _current;
            _current += (_target - _current) * (1f - (float)Math.Exp(-seconds / TimeConstant));
            if (Math.Abs(_target - _current) < 1e-4f) _current = _target;
            return _current != before;
        }

        bool StepSwell(float seconds)
        {
            if (_swell == 0f && _attackLeft <= 0f) return false;
            float before = _swell;
            if (_attackLeft > 0f)
            {
                float t = Math.Min(seconds, _attackLeft);
                _swell = Math.Min(_swellPeak, _swell + (_swellPeak - _swell) * (t / _attackLeft));
                _attackLeft -= t;
                seconds -= t;
                if (_attackLeft <= 0f) { _attackLeft = 0f; _swell = _swellPeak; }
            }
            if (_attackLeft <= 0f && seconds > 0f && _swell > 0f)
            {
                _swell *= (float)Math.Exp(-seconds / SwellTimeConstant);
                if (_swell < 1e-4f) _swell = 0f;
            }
            return _swell != before;
        }
    }

    /// <summary>The process-global state behind <see cref="SpectatorCrowd"/>: what a mode plugin asked for and whether a bed can sound. Thread-safe by single volatile
    /// fields; the Unity side reads the target once per frame.</summary>
    internal sealed class CrowdBedState
    {
        volatile float _excitement;
        volatile bool _available;
        float _swellRequest;     // only touched through Interlocked/Volatile: the largest amplitude asked for since the bed's tick last took it

        /// <summary>The level most recently set (0..1).</summary>
        internal float Excitement => _excitement;

        /// <summary>An owned raid's crowd has a bed that is switched on. Written by the Unity side.</summary>
        internal bool Available { get { return _available; } set { _available = value; } }

        internal void SetExcitement(float level01) { _excitement = CrowdExcitement.Sanitize(level01); }

        /// <summary>Asks the bed for a swell (<see cref="CrowdExcitement.AddSwell"/>) of at most <paramref name="amplitude"/> (0..1, NaN counts as 0): written by a reaction voice that
        /// starts, any thread. Requests that arrive before the bed's tick took the last one are merged into the largest.</summary>
        internal void RequestSwell(float amplitude)
        {
            float a = CrowdExcitement.Sanitize(amplitude);
            if (!(a > 0f)) return;
            float seen = Volatile.Read(ref _swellRequest);
            while (a > seen)
            {
                float was = Interlocked.CompareExchange(ref _swellRequest, a, seen);
                if (was == seen) return;
                seen = was;
            }
        }

        /// <summary>Returns the pending swell request (0 when none) and clears it; read by the bed's main-thread tick.</summary>
        internal float TakeSwell() => Interlocked.Exchange(ref _swellRequest, 0f);

        internal void Reset() { _excitement = 0f; _available = false; Interlocked.Exchange(ref _swellRequest, 0f); }
    }
}
