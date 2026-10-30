#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ArenaMapPort.Client
{
    // Pure rules of the developer-only audio noise probe (docs/progress/spectator-audio.md "NoiseProbe diagnostic (2026-10-29)").
    // No engine, plugin-framework or game type in this file: the tests link it and run it on synthetic signals. NoiseProbe.cs is the Unity glue
    // (config, taps, hotkey, census, export). Nothing here allocates per block or per event once its buffers exist, and nothing here
    // logs: every line is built on the game thread by NoiseReport and handed to the caller.

    /// <summary>Audio diagnostics/NoiseProbe. Off costs one static bool read per tap; Detect reads the listener output; Capture adds the PCM ring.</summary>
    internal enum NoiseProbeMode { Off, Detect, Capture }

    internal enum NoiseKind { Peak = 0, Click = 1, Dropout = 2, Gap = 3, Clip = 4 }

    internal enum NoiseLevel { Mid, Loud }

    /// <summary>What the event ring can hold. Appended only: the ring is a runtime structure, but the log tokens are read by analysis scripts.</summary>
    internal enum NoiseEv : byte
    {
        None, SpecStart, SpecStop, SpecOnset, AmbStart, AmbRestart, AmbStop, Prop, Room, RoomReverb, RoomShot, Gate, Puddle,
        Rain, RainPlay, Config, Reset, Life, Hitch, Gc, Mark, Burst, SelfTest,
        /// <summary>A crowd-bed stand source starts (A = 1) or stops (A = 0): B the stand index, V1 the distance to the listener, V2 the source volume.</summary>
        Bed
    }

    /// <summary>Why a spectator voice stopped (the spec- event).</summary>
    internal enum NoiseStopReason { Ended, Dead, Reset, Slot, StopAll, Toggle, NotStarted, NoServices, Disable }

    internal static class NoiseText
    {
        internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The frames after Play at which the spectator onset trace samples source.volume and the occlusion factor.</summary>
        internal static readonly int[] OnsetFrames = { 1, 2, 3, 5, 8, 13 };

        internal static bool IsOnsetFrame(int frame)
        {
            for (int i = 0; i < OnsetFrames.Length; i++) if (OnsetFrames[i] == frame) return true;
            return false;
        }

        internal static bool IsCause(NoiseEv kind)
        {
            switch (kind)
            {
                case NoiseEv.SpecStart: case NoiseEv.SpecStop: case NoiseEv.AmbStart: case NoiseEv.AmbRestart: case NoiseEv.Prop:
                case NoiseEv.Room: case NoiseEv.RoomReverb: case NoiseEv.RoomShot: case NoiseEv.Gate: case NoiseEv.Puddle:
                case NoiseEv.Rain: case NoiseEv.RainPlay: case NoiseEv.Config: case NoiseEv.Reset: case NoiseEv.Life:
                case NoiseEv.Hitch: case NoiseEv.SelfTest: case NoiseEv.Bed:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>"arena_fan9_participating_02" becomes "fan9_02"; any other name is returned unchanged.</summary>
        internal static string ShortClip(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            const string marker = "_participating";
            int at = name.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return name;
            string head = name.StartsWith("arena_", StringComparison.Ordinal) ? name.Substring(6, at - 6) : name.Substring(0, at);
            return head + name.Substring(at + marker.Length);
        }

        internal static float Db(float linear) => linear <= 1e-6f ? -120f : (float)(20.0 * Math.Log10(linear));

        internal static string Signed(float value) => value.ToString("+0.0;-0.0;+0.0", Inv);

        internal static string Clock(DateTime local) => local.ToString("HH:mm:ss.fff", Inv);

        internal static string ReasonText(NoiseStopReason reason)
        {
            switch (reason)
            {
                case NoiseStopReason.Ended: return "ended";
                case NoiseStopReason.Dead: return "dead";
                case NoiseStopReason.Reset: return "reset";
                case NoiseStopReason.Slot: return "slot";
                case NoiseStopReason.Toggle: return "toggle";
                case NoiseStopReason.NotStarted: return "notstarted";
                case NoiseStopReason.NoServices: return "noservices";
                case NoiseStopReason.Disable: return "disable";
                default: return "stopall";
            }
        }

        internal static string Token(NoiseEv kind)
        {
            switch (kind)
            {
                case NoiseEv.SpecStart: return "spec+";
                case NoiseEv.SpecStop: return "spec-";
                case NoiseEv.SpecOnset: return "spec.on";
                case NoiseEv.AmbStart: return "amb+";
                case NoiseEv.AmbRestart: return "amb~";
                case NoiseEv.AmbStop: return "amb-";
                case NoiseEv.Prop: return "prop";
                case NoiseEv.Room: return "room";
                case NoiseEv.RoomReverb: return "roomReverb";
                case NoiseEv.RoomShot: return "roomShot";
                case NoiseEv.Gate: return "gate";
                case NoiseEv.Puddle: return "puddle";
                case NoiseEv.Rain: return "rain";
                case NoiseEv.RainPlay: return "rain.play";
                case NoiseEv.Config: return "audioConfig";
                case NoiseEv.Reset: return "reset";
                case NoiseEv.Life: return "life";
                case NoiseEv.Hitch: return "hitch";
                case NoiseEv.Gc: return "gc";
                case NoiseEv.Mark: return "MARK";
                case NoiseEv.Burst: return "burst";
                case NoiseEv.SelfTest: return "selftest";
                case NoiseEv.Bed: return "bed";
                default: return "none";
            }
        }

        /// <summary>The token of one event: <see cref="Token(NoiseEv)"/>, except that the crowd bed says whether the stand source started or stopped ("bed+", "bed-").</summary>
        internal static string Token(NoiseEvent e) => e.Kind == NoiseEv.Bed ? (e.A == 1 ? "bed+" : "bed-") : Token(e.Kind);
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Block statistics

    /// <summary>
    /// Statistics of one window of the mixed output (all channels). Filled channel by channel (a listener readback gives one buffer per
    /// channel, an audio filter gives interleaved data: <c>stride</c> covers both). Reused: <see cref="Reset"/> clears it, no allocation.
    /// HF is rms(first difference) / (rms * sqrt 2): 1.0 for white noise, 0.09 for a 1 kHz sine at 48 kHz, 0.54 at 6 kHz.
    /// </summary>
    internal sealed class NoiseBlock
    {
        public const int MaxChannels = 8;
        public readonly float[] ChannelPeak = new float[MaxChannels];
        public int Channels, SamplesPerChannel, PeakChannel, Clipped, NonFinite;
        public float Peak, D1, Rms, Hf;
        public double SumSq, DiffSumSq;
        public long Count, DiffCount;

        public void Reset()
        {
            for (int i = 0; i < MaxChannels; i++) ChannelPeak[i] = 0f;
            Channels = 0; SamplesPerChannel = 0; PeakChannel = 0; Clipped = 0; NonFinite = 0;
            Peak = 0f; D1 = 0f; Rms = 0f; Hf = 0f; SumSq = 0; DiffSumSq = 0; Count = 0; DiffCount = 0;
        }

        public void AddChannel(int channel, float[] buffer, int offset, int stride, int count)
        {
            if (channel < 0 || channel >= MaxChannels || buffer == null || count <= 0 || stride < 1) return;
            float peak = 0f, d1 = 0f, prev = 0f;
            bool havePrev = false;
            double sumSq = 0, diffSq = 0;
            long diffCount = 0, finite = 0;
            int clipped = 0, bad = 0;
            for (int i = 0, k = offset; i < count; i++, k += stride)
            {
                float x = buffer[k];
                if (float.IsNaN(x) || float.IsInfinity(x)) { bad++; havePrev = false; continue; }
                float a = x < 0f ? -x : x;
                if (a > peak) peak = a;
                if (a >= 0.999f) clipped++;
                sumSq += (double)x * x;
                finite++;
                if (havePrev)
                {
                    float d = x - prev;
                    float ad = d < 0f ? -d : d;
                    if (ad > d1) d1 = ad;
                    diffSq += (double)d * d;
                    diffCount++;
                }
                prev = x; havePrev = true;
            }
            ChannelPeak[channel] = peak;
            if (channel + 1 > Channels) Channels = channel + 1;
            if (count > SamplesPerChannel) SamplesPerChannel = count;
            if (peak > Peak) { Peak = peak; PeakChannel = channel; }
            if (d1 > D1) D1 = d1;
            Clipped += clipped; NonFinite += bad;
            SumSq += sumSq; DiffSumSq += diffSq; Count += finite; DiffCount += diffCount;
        }

        public void Finish()
        {
            Rms = Count > 0 ? (float)Math.Sqrt(SumSq / Count) : 0f;
            Hf = Rms > 1e-9f && DiffCount > 0 ? (float)(Math.Sqrt(DiffSumSq / DiffCount) / (Rms * 1.41421356237)) : 0f;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Detector

    /// <summary>Starting thresholds, simulation-tuned (docs/progress/spectator-audio.md), to be calibrated on the first host minutes.</summary>
    internal sealed class NoiseThresholds
    {
        public float PeakMin = 0.03f, PeakOverPkDb = 15f, PeakOverRmsDb = 10f, PeakHf = 0.30f, LoudPk = 0.25f;
        public float ClickMin = 0.04f, ClickRatio = 8f, ClickOfPeak = 0.5f;
        public float DropoutBgRms = 3e-4f;
        public int DropoutBlocks = 2;
        public float PkFloor = 1e-3f, RmsFloor = 1e-4f, D1Floor = 1e-4f;
        public double GapMs = 60.0;
        public double RefractorySeconds = 0.15, IgnoreSeconds = 3.0, MaxOpenSeconds = 0.30;
        public int LinesPerKindPerMinute = 12, LoudLinesPerMinute = 4;
        public int History = 64, MinHistory = 24;
    }

    /// <summary>One logged detection. Instances are owned and reused by <see cref="NoiseDetector"/>: consume them before the next call.</summary>
    internal sealed class NoiseHit
    {
        public NoiseKind Kind;
        public NoiseLevel Level;
        public int Number, PkChannel, Channels, Blocks;
        public double Time, DurMs;
        public float Pk, Rms, BgPk, BgRms, D1, Hf, OverPkDb, OverRmsDb;
        public readonly float[] ChannelPk = new float[NoiseBlock.MaxChannels];
        public string Note;

        public float PkDb => NoiseText.Db(Pk);
        public float RmsDb => NoiseText.Db(Rms);
        public float BgPkDb => NoiseText.Db(BgPk);
        public float BgRmsDb => NoiseText.Db(BgRms);

        public void Clear()
        {
            Kind = NoiseKind.Peak; Level = NoiseLevel.Mid; Number = 0; PkChannel = 0; Channels = 0; Blocks = 0;
            Time = 0; DurMs = 0; Pk = 0; Rms = 0; BgPk = 0; BgRms = 0; D1 = 0; Hf = 0; OverPkDb = 0; OverRmsDb = 0; Note = null;
            for (int i = 0; i < ChannelPk.Length; i++) ChannelPk[i] = 0f;
        }

        internal void Fill(NoiseBlock b, float bgPk, float bgRms, float overPk, float overRms)
        {
            Pk = b.Peak; Rms = b.Rms; D1 = b.D1; Hf = b.Hf; BgPk = bgPk; BgRms = bgRms; OverPkDb = overPk; OverRmsDb = overRms;
            PkChannel = b.PeakChannel; Channels = b.Channels;
            for (int i = 0; i < ChannelPk.Length; i++) ChannelPk[i] = b.ChannelPeak[i];
        }
    }

    /// <summary>Counters and calibration samples of one reporting minute.</summary>
    internal sealed class NoiseMinute
    {
        public double Start, Covered;
        public int Blocks, Loud, Suppressed, Refractory, Orphan, EvOrphan, Hitch50, SpecStart, SpecStop, AmbStart, Restarts, Props, Rooms, Resets, ProbeCalls;
        public readonly int[] Bursts = new int[5];
        public readonly int[] Gc = new int[3];
        public double ProbeMsSum, ProbeMsMax;
        readonly float[] _bgRmsDb = new float[1024], _pkDb = new float[8192], _overDb = new float[8192];
        int _bgN, _pkN, _sampleSkip;

        public void Reset(double start)
        {
            Start = start; Covered = 0; Blocks = 0; Loud = 0; Suppressed = 0; Refractory = 0; Orphan = 0; EvOrphan = 0; Hitch50 = 0;
            SpecStart = 0; SpecStop = 0; AmbStart = 0; Restarts = 0; Props = 0; Rooms = 0; Resets = 0; ProbeCalls = 0;
            for (int i = 0; i < Bursts.Length; i++) Bursts[i] = 0;
            for (int i = 0; i < Gc.Length; i++) Gc[i] = 0;
            ProbeMsSum = 0; ProbeMsMax = 0; _bgN = 0; _pkN = 0; _sampleSkip = 0;
        }

        internal void AddBlock(float pkDb, float overPkDb, float bgRmsDb)
        {
            if (_pkN < _pkDb.Length) { _pkDb[_pkN] = pkDb; _overDb[_pkN] = overPkDb; _pkN++; }
            if ((_sampleSkip++ & 7) == 0 && _bgN < _bgRmsDb.Length) _bgRmsDb[_bgN++] = bgRmsDb;
        }

        public void AddProbeCost(double ms)
        {
            ProbeCalls++; ProbeMsSum += ms;
            if (ms > ProbeMsMax) ProbeMsMax = ms;
        }

        public void OnEvent(NoiseEv kind, int a, int b)
        {
            switch (kind)
            {
                case NoiseEv.SpecStart: SpecStart++; break;
                case NoiseEv.SpecStop: SpecStop++; break;
                case NoiseEv.AmbStart: AmbStart++; break;
                case NoiseEv.AmbRestart: Restarts++; break;
                case NoiseEv.Prop: Props++; break;
                case NoiseEv.Room: Rooms++; break;
                case NoiseEv.Config: case NoiseEv.Reset: Resets++; break;
                case NoiseEv.Hitch: Hitch50++; break;
                case NoiseEv.Gc: if (a >= 0 && a < Gc.Length) Gc[a] += b; break;
            }
        }

        public float BgMedianDb() => _bgN == 0 ? -120f : Percentile(_bgRmsDb, _bgN, 0.5);
        public float PkPercentile(double p) => _pkN == 0 ? -120f : Percentile(_pkDb, _pkN, p);
        public float OverPercentile(double p) => _pkN == 0 ? 0f : Percentile(_overDb, _pkN, p);

        /// <summary>Nearest-rank percentile (0..1). Sorts the sample array in place: the minute is over when this is called.</summary>
        static float Percentile(float[] samples, int n, double p)
        {
            Array.Sort(samples, 0, n);
            int index = (int)Math.Ceiling(p * n) - 1;
            if (index < 0) index = 0;
            if (index >= n) index = n - 1;
            return samples[index];
        }
    }

    /// <summary>
    /// Burst detector on a stream of <see cref="NoiseBlock"/>s. PEAK: a wideband level jump over the median of the last 64 blocks. CLICK: a
    /// sharp step that the level jump alone does not explain (a block that qualifies as PEAK is not also a CLICK). DROPOUT: two or more
    /// all-zero blocks over an audible background. GAP: the DSP clock fell behind real time. CLIP: a full-scale or non-finite sample.
    /// Consecutive triggering blocks are one event (logged when it ends), a refractory period and a per-minute line limit bound the output.
    /// </summary>
    internal sealed class NoiseDetector
    {
        public readonly NoiseThresholds T;
        public readonly NoiseMinute Minute = new NoiseMinute();
        /// <summary>Filled by <see cref="Process"/> / <see cref="Flush"/>.</summary>
        public readonly NoiseHit[] Hits = new NoiseHit[8];
        public int NextNumber = 1;
        public double BlockSeconds = 1024.0 / 48000.0;
        /// <summary>Median peak of the history at the last <see cref="Process"/> call (floored); the self-test reports it on a miss.</summary>
        public float LastBgPk { get; private set; }

        sealed class OpenEvent { public bool Active; public double Start, Last; public int Blocks; public readonly NoiseHit Hit = new NoiseHit(); }

        readonly float[] _pk, _rms, _d1, _scratch;
        readonly OpenEvent[] _open = new OpenEvent[5];
        readonly double[] _lastEmit = new double[5];
        readonly int[] _lines = new int[6];
        readonly NoiseHit _gapHit = new NoiseHit();
        int _count, _head, _zeroRun, _hitCount;
        double _ignoreUntil, _lastBlockAt = double.NaN;

        public NoiseDetector() : this(new NoiseThresholds()) { }

        public NoiseDetector(NoiseThresholds thresholds)
        {
            T = thresholds;
            _pk = new float[T.History]; _rms = new float[T.History]; _d1 = new float[T.History]; _scratch = new float[T.History];
            for (int i = 0; i < _open.Length; i++) { _open[i] = new OpenEvent(); _lastEmit[i] = double.NegativeInfinity; }
            Reset(0);
        }

        /// <summary>Raid start: forget the baseline, ignore PEAK/CLICK for the next seconds (the mixer is settling) and start a fresh minute.</summary>
        public void Reset(double now)
        {
            Settle(now);
            Minute.Reset(now);
            for (int i = 0; i < _lines.Length; i++) _lines[i] = 0;
        }

        /// <summary>An audio reset in the middle of a minute: forget the baseline and ignore PEAK/CLICK for the next seconds; the minute counters go on.</summary>
        public void Settle(double now)
        {
            _count = 0; _head = 0; _zeroRun = 0; _lastBlockAt = double.NaN;
            _ignoreUntil = now + T.IgnoreSeconds;
            for (int i = 0; i < _open.Length; i++) _open[i].Active = false;
        }

        public bool MinuteDue(double now) => now - Minute.Start >= 60.0;

        public void RollMinute(double now)
        {
            Minute.Reset(now);
            for (int i = 0; i < _lines.Length; i++) _lines[i] = 0;
        }

        public bool Ignoring(double now) => now < _ignoreUntil;

        /// <summary>Feeds one block taken at <paramref name="now"/> (seconds). Returns how many entries of <see cref="Hits"/> are new.</summary>
        public int Process(NoiseBlock b, double now)
        {
            _hitCount = 0;
            Minute.Blocks++;
            Minute.Covered += double.IsNaN(_lastBlockAt) ? BlockSeconds : Math.Min(BlockSeconds, Math.Max(0.0, now - _lastBlockAt));
            _lastBlockAt = now;

            bool warm = _count >= T.MinHistory;
            float bgPk = Math.Max(Median(_pk), T.PkFloor);
            float rawBgRms = Median(_rms);
            float bgRms = Math.Max(rawBgRms, T.RmsFloor);
            float bgD1 = Math.Max(Median(_d1), T.D1Floor);
            LastBgPk = bgPk;
            float pk = b.Peak, rms = b.Rms, d1 = b.D1;
            float overPk = pk > 0f ? NoiseText.Db(pk / bgPk) : -120f;
            float overRms = rms > 0f ? NoiseText.Db(rms / bgRms) : -120f;
            bool ignore = now < _ignoreUntil;
            Minute.AddBlock(NoiseText.Db(pk), overPk, NoiseText.Db(bgRms));

            bool peak = warm && !ignore && pk >= T.PeakMin && overPk >= T.PeakOverPkDb && overRms >= T.PeakOverRmsDb && b.Hf >= T.PeakHf;
            // A CLICK next to a PEAK (the edge windows of one noise burst) is the same event: it is not reported twice.
            bool nearPeak = _open[(int)NoiseKind.Peak].Active || now - _lastEmit[(int)NoiseKind.Peak] < T.RefractorySeconds;
            bool click = warm && !ignore && !peak && !nearPeak && d1 >= T.ClickMin && d1 >= T.ClickRatio * bgD1 && d1 >= T.ClickOfPeak * pk && pk < T.LoudPk;
            bool zero = b.Count > 0 && b.Peak == 0f && b.NonFinite == 0 && rawBgRms >= T.DropoutBgRms;
            _zeroRun = zero ? _zeroRun + 1 : 0;
            bool dropout = zero && _zeroRun >= T.DropoutBlocks;
            bool clip = b.Clipped > 0 || b.NonFinite > 0;

            Feed(NoiseKind.Peak, peak, b, bgPk, bgRms, overPk, overRms, now);
            Feed(NoiseKind.Click, click, b, bgPk, bgRms, overPk, overRms, now);
            Feed(NoiseKind.Dropout, dropout, b, bgPk, bgRms, overPk, overRms, now);
            Feed(NoiseKind.Clip, clip, b, bgPk, bgRms, overPk, overRms, now);

            Push(pk, rms, d1);
            return _hitCount;
        }

        /// <summary>Closes every open event (end of the raid or a mode change).</summary>
        public int Flush(double now)
        {
            _hitCount = 0;
            for (int k = 0; k < _open.Length; k++) if (_open[k].Active) Close(k, now);
            return _hitCount;
        }

        /// <summary>
        /// A DSP clock that fell behind real time (<paramref name="lagMs"/>), or an audio-filter callback gap. Returns the hit, or null when
        /// the refractory period or the line limit swallowed it. The hit is owned by the detector.
        /// </summary>
        public NoiseHit ObserveGap(double now, double lagMs, string note)
        {
            int k = (int)NoiseKind.Gap;
            if (now - _lastEmit[k] < T.RefractorySeconds) { Minute.Refractory++; return null; }
            _lastEmit[k] = now;
            Minute.Bursts[k]++;
            if (!TakeLine(k)) { Minute.Suppressed++; return null; }
            var h = _gapHit;
            h.Clear();
            h.Kind = NoiseKind.Gap; h.Level = NoiseLevel.Mid; h.Time = now; h.DurMs = lagMs; h.Blocks = 1; h.Number = NextNumber++; h.Note = note;
            return h;
        }

        void Feed(NoiseKind kind, bool trigger, NoiseBlock b, float bgPk, float bgRms, float overPk, float overRms, double now)
        {
            int k = (int)kind;
            var open = _open[k];
            if (trigger)
            {
                if (open.Active)
                {
                    open.Blocks++; open.Last = now;
                    if (b.Peak > open.Hit.Pk) open.Hit.Fill(b, bgPk, bgRms, overPk, overRms);
                    if (now - open.Start >= T.MaxOpenSeconds) Close(k, now);
                }
                else if (now - _lastEmit[k] < T.RefractorySeconds) Minute.Refractory++;
                else
                {
                    open.Active = true; open.Start = now; open.Last = now; open.Blocks = 1;
                    open.Hit.Clear();
                    open.Hit.Kind = kind; open.Hit.Time = now;
                    open.Hit.Fill(b, bgPk, bgRms, overPk, overRms);
                    if (kind == NoiseKind.Clip) open.Hit.Note = "clipped=" + b.Clipped.ToString(NoiseText.Inv) + " nonfinite=" + b.NonFinite.ToString(NoiseText.Inv);
                }
            }
            else if (open.Active) Close(k, now);
        }

        void Close(int k, double now)
        {
            var open = _open[k];
            open.Active = false;
            var h = open.Hit;
            h.Blocks = open.Blocks;
            h.DurMs = ((open.Last - open.Start) + BlockSeconds) * 1000.0;
            h.Level = h.Kind == NoiseKind.Peak && h.Pk >= T.LoudPk ? NoiseLevel.Loud : NoiseLevel.Mid;
            _lastEmit[k] = now;
            bool loud = h.Level == NoiseLevel.Loud;
            if (loud) Minute.Loud++; else Minute.Bursts[k]++;
            if (!TakeLine(loud ? 5 : k)) { Minute.Suppressed++; return; }
            h.Number = NextNumber++;
            if (_hitCount < Hits.Length) Hits[_hitCount++] = h;
        }

        bool TakeLine(int lane)
        {
            int limit = lane == 5 ? T.LoudLinesPerMinute : T.LinesPerKindPerMinute;
            if (_lines[lane] >= limit) return false;
            _lines[lane]++;
            return true;
        }

        void Push(float pk, float rms, float d1)
        {
            _pk[_head] = pk; _rms[_head] = rms; _d1[_head] = d1;
            _head = (_head + 1) % _pk.Length;
            if (_count < _pk.Length) _count++;
        }

        float Median(float[] source)
        {
            int n = _count;
            if (n == 0) return 0f;
            Array.Copy(source, 0, _scratch, 0, n);
            Array.Sort(_scratch, 0, n);
            return (n & 1) == 1 ? _scratch[n / 2] : 0.5f * (_scratch[n / 2 - 1] + _scratch[n / 2]);
        }
    }

    /// <summary>
    /// Main-thread stall detector for the audio clock. The lag is how far the DSP clock has fallen behind real time since the first sample
    /// (real-time advance minus DSP-time advance); a GAP is a step of that lag over the minimum of the last second. A hitch on the main thread
    /// moves both clocks and leaves the lag alone; an audio thread that stopped producing buffers (an underrun) moves only real time, and the
    /// missing time is never made up, so the lag steps up and stays. DSP time advances in whole buffers (21.3 ms at 48 kHz), so the normal
    /// jitter of the step is about one buffer, well below the 60 ms threshold (three buffers). After a report the history starts again at the
    /// new level. Reset on every audio reconfiguration, which may restart the DSP clock.
    /// </summary>
    internal sealed class NoiseGapWatch
    {
        public double ThresholdMs = 60.0, WindowSeconds = 1.0, SampleSpacing = 0.01;
        readonly double[] _time = new double[256], _lag = new double[256];
        int _head, _count;
        double _rt0, _dsp0, _lastSample = double.NegativeInfinity;
        bool _initialized;

        public void Reset()
        {
            _initialized = false; _count = 0; _head = 0; _lastSample = double.NegativeInfinity;
        }

        /// <summary>True when the lag stepped up by more than the threshold; <paramref name="lagMs"/> is the step.</summary>
        public bool Update(double realtime, double dsp, out double lagMs)
        {
            lagMs = 0.0;
            if (!_initialized) { _initialized = true; _rt0 = realtime; _dsp0 = dsp; }
            double lag = (realtime - _rt0) - (dsp - _dsp0);
            double baseline = lag;
            for (int i = 0; i < _count; i++)
            {
                int at = (_head - 1 - i + _time.Length) % _time.Length;
                if (realtime - _time[at] > WindowSeconds) break;
                if (_lag[at] < baseline) baseline = _lag[at];
            }
            double step = (lag - baseline) * 1000.0;
            if (step > ThresholdMs)
            {
                lagMs = step;
                _count = 0; _head = 0; _lastSample = double.NegativeInfinity;
                Push(realtime, lag);
                return true;
            }
            if (realtime - _lastSample >= SampleSpacing) Push(realtime, lag);
            return false;
        }

        void Push(double realtime, double lag)
        {
            _time[_head] = realtime; _lag[_head] = lag; _lastSample = realtime;
            _head = (_head + 1) % _time.Length;
            if (_count < _time.Length) _count++;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Event ring

    internal struct NoiseEvent
    {
        public double Time, Dsp;
        public NoiseEv Kind;
        public int A, B, L1, L2;
        public float V1, V2;
    }

    /// <summary>Interned strings for the event ring (clip, player, prop names), bounded so a name storm cannot grow it without limit.</summary>
    internal sealed class NoiseLabels
    {
        public const int Max = 2048;
        readonly Dictionary<string, int> _ids = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly List<string> _names = new List<string> { "", "?" };

        public int Intern(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int id;
            if (_ids.TryGetValue(text, out id)) return id;
            if (_names.Count >= Max) return 1;
            id = _names.Count;
            _names.Add(text);
            _ids.Add(text, id);
            return id;
        }

        public string Get(int id) => id >= 0 && id < _names.Count ? _names[id] : "?";
        public int Count => _names.Count;
    }

    /// <summary>The last 1024 audio-relevant events, written by the game thread only. Add never allocates.</summary>
    internal sealed class NoiseEventRing
    {
        readonly NoiseEvent[] _e;
        int _head, _count;

        public NoiseEventRing(int capacity = 1024) { _e = new NoiseEvent[capacity]; }

        public int Count => _count;
        public int Capacity => _e.Length;

        public void Clear() { _head = 0; _count = 0; }

        public void Add(double time, double dsp, NoiseEv kind, int a, int b, float v1, float v2, int l1, int l2)
        {
            ref NoiseEvent e = ref _e[_head];
            e.Time = time; e.Dsp = dsp; e.Kind = kind; e.A = a; e.B = b; e.V1 = v1; e.V2 = v2; e.L1 = l1; e.L2 = l2;
            _head = (_head + 1) % _e.Length;
            if (_count < _e.Length) _count++;
        }

        /// <summary>0 = the oldest event still held.</summary>
        public NoiseEvent this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
                return _e[(_head - _count + index + _e.Length * 2) % _e.Length];
            }
        }

        /// <summary>Appends the events with <c>since &lt;= Time &lt;= until</c>, oldest first. Returns how many were appended.</summary>
        public int CopyWindow(double since, double until, List<NoiseEvent> into)
        {
            int added = 0;
            for (int i = 0; i < _count; i++)
            {
                var e = this[i];
                if (e.Time < since || e.Time > until) continue;
                into.Add(e); added++;
            }
            return added;
        }

        /// <summary>True when an event that could cause a burst (<see cref="NoiseText.IsCause"/>) lies in [t - before, t + after].</summary>
        public bool HasCause(double t, double before, double after)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                var e = this[i];
                if (e.Time < t - before) break;
                if (e.Time <= t + after && NoiseText.IsCause(e.Kind)) return true;
            }
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Report formatting (text only; the caller prefixes the [NoiseProbe] tag and logs on the game thread)

    internal static class NoiseReport
    {
        static string F(float v, string format) => v.ToString(format, NoiseText.Inv);
        static string F(double v, string format) => v.ToString(format, NoiseText.Inv);

        internal static string KindText(NoiseKind kind)
        {
            switch (kind)
            {
                case NoiseKind.Peak: return "peak";
                case NoiseKind.Click: return "click";
                case NoiseKind.Dropout: return "dropout";
                case NoiseKind.Gap: return "gap";
                default: return "clip";
            }
        }

        /// <param name="readChannels">Channels AudioListener.GetOutputData reads (the device output); <paramref name="mixChannels"/> the speaker mode's.
        /// They differ on a stereo driver under a 5.1 speaker mode, printed as "ch=2/6".</param>
        internal static string Armed(NoiseProbeMode mode, string map, string speakers, string driver, int readChannels, int mixChannels, int rate, int dspLength,
            int realVoices, int virtualVoices, string calls, string mix, string roomShot, string roomReverb, double ringSeconds)
        {
            string channels = readChannels.ToString(NoiseText.Inv) + (readChannels != mixChannels ? "/" + mixChannels.ToString(NoiseText.Inv) : "");
            return "armed mode=" + mode + " map=" + map + " speakers=" + speakers + " driver=" + driver + " ch=" + channels +
                " rate=" + rate.ToString(NoiseText.Inv) + " dsp=" + dspLength.ToString(NoiseText.Inv) +
                " voices=" + realVoices.ToString(NoiseText.Inv) + "/" + virtualVoices.ToString(NoiseText.Inv) + " calls=" + calls + " mix=" + mix +
                " roomShot=" + roomShot + " roomReverb=" + roomReverb + " tap=" + (mode == NoiseProbeMode.Capture ? "readback+filter" : "readback") +
                " ring=" + (mode == NoiseProbeMode.Capture ? F(ringSeconds, "0") + "s" : "none");
        }

        /// <summary>One detection. <paramref name="where"/> and <paramref name="context"/> are built by the caller (position, room, fps, orphan flags; census).</summary>
        internal static string BurstLine(NoiseHit h, string wall, string where, string context)
        {
            var sb = new StringBuilder(256);
            sb.Append("BURST #").Append(h.Number.ToString(NoiseText.Inv)).Append(' ').Append(wall)
              .Append(" kind=").Append(KindText(h.Kind)).Append(" lvl=").Append(h.Level == NoiseLevel.Loud ? "LOUD" : "MID");
            if (h.Kind == NoiseKind.Gap)
            {
                sb.Append(" lag=").Append(F(h.DurMs, "0")).Append("ms");
                if (!string.IsNullOrEmpty(h.Note)) sb.Append(' ').Append(h.Note);
            }
            else
            {
                sb.Append(" pk=").Append(F(h.PkDb, "0.0")).Append("@ch").Append((h.PkChannel + 1).ToString(NoiseText.Inv))
                  .Append(" rms=").Append(F(h.RmsDb, "0.0")).Append(" bg=").Append(F(h.BgRmsDb, "0.0"))
                  .Append(" over=").Append(NoiseText.Signed(h.OverRmsDb)).Append(" overPk=").Append(NoiseText.Signed(h.OverPkDb))
                  .Append(" d1=").Append(F(h.D1, "0.000")).Append(" hf=").Append(F(h.Hf, "0.00"))
                  .Append(" dur=").Append(F(h.DurMs, "0")).Append("ms ch=[");
                for (int c = 0; c < h.Channels; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(F(NoiseText.Db(h.ChannelPk[c]), "0"));
                }
                sb.Append(']');
                if (!string.IsNullOrEmpty(h.Note)) sb.Append(' ').Append(h.Note);
            }
            if (!string.IsNullOrEmpty(where)) sb.Append(' ').Append(where);
            if (!string.IsNullOrEmpty(context)) sb.Append(" | ").Append(context);
            return sb.ToString();
        }

        /// <summary>"amb=[...] prop=[...] ev=[...]": what the ring knows about the second before <paramref name="t"/>.</summary>
        internal static string Context(NoiseEventRing ring, NoiseLabels labels, double t)
        {
            var amb = new StringBuilder();
            var prop = new StringBuilder();
            var ev = new StringBuilder();
            int evCount = 0;
            for (int i = ring.Count - 1; i >= 0; i--)
            {
                var e = ring[i];
                if (e.Time < t - 1.5) break;
                if (e.Time > t + 0.05) continue;
                double age = t - e.Time;
                if ((e.Kind == NoiseEv.AmbStart || e.Kind == NoiseEv.AmbRestart) && amb.Length < 200)
                {
                    if (amb.Length > 0) amb.Append(',');
                    amb.Append(labels.Get(e.L1)).Append('@').Append(F(e.V1, "0.0")).Append("m age").Append(F(Math.Max(0.0, age) * 1000.0, "0")).Append("ms");
                }
                else if (e.Kind == NoiseEv.Prop && age <= 1.0 && prop.Length < 200)
                {
                    if (prop.Length > 0) prop.Append(',');
                    prop.Append(labels.Get(e.L2)).Append('@').Append(F(e.V1, "0.0")).Append("m age").Append(F(Math.Max(0.0, age) * 1000.0, "0")).Append("ms");
                }
                if (age <= 1.0 && evCount < 8 && NoiseText.IsCause(e.Kind) && e.Kind != NoiseEv.SelfTest)
                {
                    if (evCount > 0) ev.Append(',');
                    ev.Append(NoiseText.Token(e)).Append('@').Append(F((float)(e.Time - t), "+0.00;-0.00;+0.00")).Append('s');
                    evCount++;
                }
            }
            return "amb=[" + amb + "] prop=[" + prop + "] ev=[" + ev + "]";
        }

        /// <summary>One ring event for the mark dump, relative to <paramref name="now"/> ("-7.412s spec+ fan10_01#5 12.5m occ0.31 voices=2").</summary>
        internal static string EventLine(NoiseEvent e, NoiseLabels labels, double now) =>
            "  " + F((float)(e.Time - now), "+0.000;-0.000;+0.000") + "s " + EventText(e, labels);

        internal static string EventText(NoiseEvent e, NoiseLabels labels)
        {
            string l1 = labels.Get(e.L1), l2 = labels.Get(e.L2);
            string token = NoiseText.Token(e);
            switch (e.Kind)
            {
                case NoiseEv.SpecStart:
                    return token + " " + l1 + "#" + e.A.ToString(NoiseText.Inv) + " " + F(e.V1, "0.0") + "m occ" + F(e.V2, "0.00") + " voices=" + e.B.ToString(NoiseText.Inv);
                case NoiseEv.SpecStop:
                    return token + " " + l1 + "#" + e.A.ToString(NoiseText.Inv) + " reason=" + NoiseText.ReasonText((NoiseStopReason)e.B) +
                        " played=" + F(e.V1, "0") + "ms left=" + F(e.V2, "0.00") + "s";
                case NoiseEv.SpecOnset:
                    return token + " " + l1 + "#" + e.A.ToString(NoiseText.Inv) + " f" + e.B.ToString(NoiseText.Inv) + " vol=" + F(e.V1, "0.000") + " occ=" + F(e.V2, "0.00");
                case NoiseEv.AmbStart:
                    return token + " " + l1 + " clip=" + l2 + " " + F(e.V1, "0.0") + "m vol=" + F(e.V2, "0.00") + " pitch=" + F(e.A / 1000f, "0.00");
                case NoiseEv.AmbRestart:
                    return token + " " + l1 + " clip=" + l2 + " " + F(e.V1, "0.0") + "m vol=" + F(e.V2, "0.00");
                case NoiseEv.AmbStop:
                    return token + " " + l1;
                case NoiseEv.Prop:
                    return token + " " + l1 + " " + l2 + " " + F(e.V1, "0.0") + "m " + (e.A == 1 ? "played" : e.A == 2 ? "muted" : "skipped");
                case NoiseEv.Room:
                    return token + " " + e.A.ToString(NoiseText.Inv) + "/" + l1;
                case NoiseEv.RoomReverb:
                    return token + " cat=" + e.A.ToString(NoiseText.Inv) + " changed=" + e.B.ToString(NoiseText.Inv);
                case NoiseEv.RoomShot:
                    return token + " room=" + e.A.ToString(NoiseText.Inv) + " class=" + l1 + " changed=" + e.B.ToString(NoiseText.Inv);
                case NoiseEv.Gate:
                    return token + " " + l1 + " " + F(e.V1, "0.0") + "m vol=" + F(e.V2, "0.00");
                case NoiseEv.Puddle:
                    return token + " " + l1 + " " + F(e.V1, "0.0") + "m";
                case NoiseEv.Bed:
                    return token + " " + l1 + " " + F(e.V1, "0.0") + "m vol=" + F(e.V2, "0.000");
                case NoiseEv.Rain: case NoiseEv.RainPlay: case NoiseEv.Reset: case NoiseEv.Life: case NoiseEv.SelfTest:
                    return token + " " + l1;
                case NoiseEv.Config:
                    return token + " deviceChanged=" + e.A.ToString(NoiseText.Inv);
                case NoiseEv.Hitch:
                    return token + " " + F(e.V1, "0") + "ms";
                case NoiseEv.Gc:
                    return token + " gen" + e.A.ToString(NoiseText.Inv) + " +" + e.B.ToString(NoiseText.Inv);
                case NoiseEv.Mark:
                    return token + " #" + e.A.ToString(NoiseText.Inv);
                case NoiseEv.Burst:
                    return token + " #" + e.A.ToString(NoiseText.Inv) + " " + l1 + " over=" + F(e.V1, "+0.0;-0.0;+0.0") + "dB";
                default:
                    return token;
            }
        }

        /// <summary>The per-minute summary line.</summary>
        internal static string MinuteLine(NoiseMinute m, string map, double now)
        {
            double elapsed = Math.Max(0.001, now - m.Start);
            var sb = new StringBuilder(256);
            sb.Append("minute map=").Append(map).Append(" blocks=").Append(m.Blocks.ToString(NoiseText.Inv))
              .Append(" cover=").Append(F(Math.Min(100.0, m.Covered / elapsed * 100.0), "0")).Append('%')
              .Append(" bursts peak=").Append(m.Bursts[0].ToString(NoiseText.Inv)).Append(" click=").Append(m.Bursts[1].ToString(NoiseText.Inv))
              .Append(" dropout=").Append(m.Bursts[2].ToString(NoiseText.Inv)).Append(" gap=").Append(m.Bursts[3].ToString(NoiseText.Inv))
              .Append(" clip=").Append(m.Bursts[4].ToString(NoiseText.Inv)).Append(" loud=").Append(m.Loud.ToString(NoiseText.Inv))
              .Append(" suppressed=").Append(m.Suppressed.ToString(NoiseText.Inv)).Append(" refractory=").Append(m.Refractory.ToString(NoiseText.Inv))
              .Append(" orphan=").Append(m.Orphan.ToString(NoiseText.Inv)).Append(" evOrphan=").Append(m.EvOrphan.ToString(NoiseText.Inv))
              .Append(" bgMedian=").Append(F(m.BgMedianDb(), "0.0"))
              .Append(" hitch50=").Append(m.Hitch50.ToString(NoiseText.Inv))
              .Append(" gc=").Append(m.Gc[0].ToString(NoiseText.Inv)).Append('/').Append(m.Gc[1].ToString(NoiseText.Inv)).Append('/').Append(m.Gc[2].ToString(NoiseText.Inv))
              .Append(" spec=+").Append(m.SpecStart.ToString(NoiseText.Inv)).Append("/-").Append(m.SpecStop.ToString(NoiseText.Inv))
              .Append(" amb=+").Append(m.AmbStart.ToString(NoiseText.Inv)).Append(" restarts=").Append(m.Restarts.ToString(NoiseText.Inv))
              .Append(" props=").Append(m.Props.ToString(NoiseText.Inv)).Append(" rooms=").Append(m.Rooms.ToString(NoiseText.Inv))
              .Append(" resets=").Append(m.Resets.ToString(NoiseText.Inv))
              .Append(" probeMs=").Append(F(m.ProbeCalls > 0 ? m.ProbeMsSum / m.ProbeCalls : 0.0, "0.000")).Append('/').Append(F(m.ProbeMsMax, "0.000"));
            return sb.ToString();
        }

        /// <summary>Calibration percentiles of a minute (logged for the first minutes of a raid). Call after <see cref="MinuteLine"/>: it sorts the samples.</summary>
        internal static string CalibrationLine(NoiseMinute m, int minuteIndex) =>
            "calib minute=" + minuteIndex.ToString(NoiseText.Inv) + " pk50=" + F(m.PkPercentile(0.5), "0.0") + " pk99=" + F(m.PkPercentile(0.99), "0.0") +
            " pk999=" + F(m.PkPercentile(0.999), "0.0") + " over50=" + F(m.OverPercentile(0.5), "+0.0;-0.0;+0.0") +
            " over99=" + F(m.OverPercentile(0.99), "+0.0;-0.0;+0.0") + " over999=" + F(m.OverPercentile(0.999), "+0.0;-0.0;+0.0");

        internal static string MarkHeader(int number, string wall, long frame, string where) =>
            "MARK #" + number.ToString(NoiseText.Inv) + " " + wall + " frame=" + frame.ToString(NoiseText.Inv) +
            (string.IsNullOrEmpty(where) ? "" : " " + where) + " (the reaction lag is 0.3-1.5 s)";
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Self-test

    /// <summary>
    /// The sensitivity check: four short noise bursts of falling level, 1.2 s apart, played through an owned source. The detector must answer
    /// each within 0.5 s, or the line says what the block peak was. This bounds what a negative host result can exclude.
    /// </summary>
    internal sealed class NoiseSelfTest
    {
        public const double Spacing = 1.2, Window = 0.5, Lead = 1.0;
        public static readonly float[] DefaultLevelsDb = { -18f, -24f, -30f, -36f };

        readonly float[] _levels;
        int _index;
        double _nextAt, _playedAt;
        bool _awaiting, _hit;
        float _maxPk, _bgPk, _over;
        double _latencyMs;
        int _passed;
        float _weakest = float.NaN;

        public NoiseSelfTest(float[] levelsDb) { _levels = levelsDb ?? DefaultLevelsDb; }

        public bool Running { get; private set; }
        public int Total => _levels.Length;
        public int Passed => _passed;
        public bool Awaiting => _awaiting;

        public void Begin(double now)
        {
            Running = true; _index = 0; _awaiting = false; _passed = 0; _weakest = float.NaN; _nextAt = now + Lead;
        }

        /// <summary>True once per case when its burst must be played now.</summary>
        public bool ShouldPlay(double now, out float levelDb)
        {
            levelDb = 0f;
            if (!Running || _awaiting || _index >= _levels.Length || now < _nextAt) return false;
            levelDb = _levels[_index];
            _awaiting = true; _playedAt = now; _hit = false; _maxPk = 0f; _bgPk = 0f; _over = 0f; _latencyMs = 0.0;
            return true;
        }

        public bool InWindow(double t) => _awaiting && t >= _playedAt - 0.02 && t <= _playedAt + Window;

        /// <summary>Every block while a case is awaiting its answer.</summary>
        public void OnBlock(double now, float pk, float bgPk)
        {
            if (!_awaiting || now < _playedAt || now > _playedAt + Window) return;
            if (pk > _maxPk) _maxPk = pk;
            if (bgPk > _bgPk) _bgPk = bgPk;
        }

        /// <summary>A PEAK or CLICK detection inside the window answers the case.</summary>
        public void OnHit(NoiseHit h)
        {
            if (!_awaiting || _hit || h == null || (h.Kind != NoiseKind.Peak && h.Kind != NoiseKind.Click) || !InWindow(h.Time)) return;
            _hit = true; _latencyMs = Math.Max(0.0, (h.Time - _playedAt) * 1000.0); _over = h.OverRmsDb;
        }

        /// <summary>True when <see cref="TakeResult"/> has a line to give (a finished case, or the closing summary).</summary>
        public bool ResultDue(double now) =>
            Running && ((_awaiting && now >= _playedAt + Window) || (!_awaiting && _index >= _levels.Length));

        /// <summary>The result line of a finished case (null while the window is open), then the closing summary after the last one.</summary>
        public string TakeResult(double now, float tapPeakDb)
        {
            if (!Running) return null;
            if (_awaiting && now >= _playedAt + Window)
            {
                float level = _levels[_index];
                string tap = float.IsNaN(tapPeakDb) ? "" : " tap=" + tapPeakDb.ToString("0.0", NoiseText.Inv) + "dBFS";
                string line;
                if (_hit)
                {
                    _passed++;
                    if (float.IsNaN(_weakest) || level < _weakest) _weakest = level;
                    line = "selftest " + level.ToString("0", NoiseText.Inv) + "dBFS PASS latency " + _latencyMs.ToString("0", NoiseText.Inv) +
                        " ms over " + NoiseText.Signed(_over) + " dB" + tap;
                }
                else
                {
                    line = "selftest " + level.ToString("0", NoiseText.Inv) + "dBFS MISS block peak " + NoiseText.Db(_maxPk).ToString("0.0", NoiseText.Inv) +
                        " dBFS, background peak " + NoiseText.Db(_bgPk).ToString("0.0", NoiseText.Inv) + " dBFS" + tap;
                }
                _awaiting = false; _index++; _nextAt = _playedAt + Spacing;
                return line;
            }
            if (!_awaiting && _index >= _levels.Length)
            {
                Running = false;
                return "selftest done " + _passed.ToString(NoiseText.Inv) + "/" + _levels.Length.ToString(NoiseText.Inv) + " detected" +
                    (float.IsNaN(_weakest) ? "" : "; weakest detected " + _weakest.ToString("0", NoiseText.Inv) + "dBFS");
            }
            return null;
        }

        /// <summary>A 10 ms Hann-windowed white-noise burst whose largest sample is exactly <paramref name="peakDb"/> dBFS (deterministic).</summary>
        public static float[] MakeBurst(int sampleRate, float peakDb, int seed)
        {
            int n = Math.Max(16, sampleRate / 100);
            var data = new float[n];
            uint state = (uint)seed * 2654435761u + 12345u;
            float max = 0f;
            for (int i = 0; i < n; i++)
            {
                state = state * 1664525u + 1013904223u;
                float white = (float)((state >> 8) / 8388608.0 - 1.0);
                float window = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * (i + 0.5) / n);
                data[i] = white * window;
                float a = Math.Abs(data[i]);
                if (a > max) max = a;
            }
            float scale = (float)Math.Pow(10.0, peakDb / 20.0) / (max > 0f ? max : 1f);
            for (int i = 0; i < n; i++) data[i] *= scale;
            return data;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Capture: PCM ring, WAV writer, limits

    /// <summary>
    /// PCM16 ring written by the audio thread (<see cref="Write"/>: no allocation, no lock, no Unity call) and copied by the game thread or a
    /// worker (<see cref="TryCopy"/>). The writer publishes the number of frames written with a volatile write; a copy re-checks it afterwards
    /// and refuses a range the writer reached while it was copying.
    /// </summary>
    internal sealed class NoisePcmRing
    {
        public readonly int Channels, SampleRate, CapacityFrames;
        readonly short[] _data;
        long _written;

        public NoisePcmRing(int channels, int sampleRate, double seconds)
        {
            if (channels < 1 || channels > NoiseBlock.MaxChannels) throw new ArgumentOutOfRangeException(nameof(channels));
            Channels = channels; SampleRate = sampleRate;
            CapacityFrames = Math.Max(1024, (int)(sampleRate * seconds));
            _data = new short[CapacityFrames * channels];
        }

        public long WrittenFrames => Volatile.Read(ref _written);
        public long ByteSize => (long)_data.Length * 2;

        /// <summary>Audio thread. <paramref name="sampleCount"/> interleaved samples; a count that is no multiple of the channel count is truncated.</summary>
        public void Write(float[] interleaved, int sampleCount)
        {
            int frames = sampleCount / Channels;
            if (frames <= 0) return;
            long w = _written;
            int pos = (int)(w % CapacityFrames) * Channels;
            int k = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < Channels; c++)
                {
                    float x = interleaved[k++];
                    int v;
                    if (float.IsNaN(x)) v = 0;
                    else if (x >= 1f) v = 32767;
                    else if (x <= -1f) v = -32768;
                    else v = (int)(x * 32767f + (x >= 0f ? 0.5f : -0.5f));
                    _data[pos++] = (short)v;
                }
                if (pos >= _data.Length) pos = 0;
            }
            Volatile.Write(ref _written, w + frames);
        }

        /// <summary>
        /// Copies the <paramref name="frames"/> frames that end at <paramref name="endFrame"/> (clamped to what was written) into
        /// <paramref name="destination"/> (interleaved). <paramref name="startFrame"/> is the first frame copied and <paramref name="copied"/> how many
        /// (less than requested near the start of the stream). False when the writer overtook the range during the copy.
        /// </summary>
        public bool TryCopy(long endFrame, int frames, short[] destination, out long startFrame, out int copied)
        {
            long written = WrittenFrames;
            long end = Math.Min(endFrame, written);
            long start = Math.Max(0L, end - frames);
            copied = (int)(end - start);
            startFrame = start;
            if (copied <= 0) { copied = 0; return false; }
            if (written - start > CapacityFrames) { copied = 0; return false; }
            if ((long)copied * Channels > destination.Length) throw new ArgumentException("destination is too small", nameof(destination));
            int pos = (int)(start % CapacityFrames) * Channels;
            int remaining = copied * Channels, outAt = 0;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, _data.Length - pos);
                Array.Copy(_data, pos, destination, outAt, chunk);
                outAt += chunk; remaining -= chunk; pos = 0;
            }
            if (WrittenFrames - start > CapacityFrames) { copied = 0; return false; }
            return true;
        }

        /// <summary>Largest absolute sample (0..1) among the frames written since <paramref name="startFrame"/>, for the self-test's tap check.</summary>
        public float PeakSince(long startFrame)
        {
            long written = WrittenFrames;
            long start = Math.Max(Math.Max(0L, startFrame), written - CapacityFrames + 1);
            if (start >= written) return 0f;
            int pos = (int)(start % CapacityFrames) * Channels;
            long samples = (written - start) * Channels;
            int peak = 0;
            for (long i = 0; i < samples; i++)
            {
                int v = _data[pos++];
                if (v < 0) v = -v;
                if (v > peak) peak = v;
                if (pos >= _data.Length) pos = 0;
            }
            return peak / 32768f;
        }
    }

    internal static class NoiseWav
    {
        public const int HeaderBytes = 44;

        /// <summary>Canonical 44-byte PCM16 header for <paramref name="frames"/> frames.</summary>
        public static byte[] Header(int channels, int sampleRate, int frames)
        {
            int dataBytes = checked(frames * channels * 2);
            var h = new byte[HeaderBytes];
            Put(h, 0, "RIFF"); PutInt(h, 4, 36 + dataBytes); Put(h, 8, "WAVE"); Put(h, 12, "fmt ");
            PutInt(h, 16, 16); PutShort(h, 20, 1); PutShort(h, 22, channels); PutInt(h, 24, sampleRate);
            PutInt(h, 28, sampleRate * channels * 2); PutShort(h, 32, channels * 2); PutShort(h, 34, 16);
            Put(h, 36, "data"); PutInt(h, 40, dataBytes);
            return h;
        }

        /// <summary>Header plus <paramref name="frames"/> frames from <paramref name="samples"/> (interleaved), streamed in 64 KiB chunks.</summary>
        public static void Write(Stream output, short[] samples, int frames, int channels, int sampleRate)
        {
            var header = Header(channels, sampleRate, frames);
            output.Write(header, 0, header.Length);
            var chunk = new byte[65536];
            long remaining = (long)frames * channels, at = 0;
            while (remaining > 0)
            {
                int n = (int)Math.Min(remaining, chunk.Length / 2);
                Buffer.BlockCopy(samples, (int)at * 2, chunk, 0, n * 2);
                if (!BitConverter.IsLittleEndian)
                    for (int i = 0; i < n * 2; i += 2) { byte t = chunk[i]; chunk[i] = chunk[i + 1]; chunk[i + 1] = t; }
                output.Write(chunk, 0, n * 2);
                at += n; remaining -= n;
            }
        }

        public static byte[] Build(short[] samples, int frames, int channels, int sampleRate)
        {
            using (var ms = new MemoryStream(HeaderBytes + frames * channels * 2))
            {
                Write(ms, samples, frames, channels, sampleRate);
                return ms.ToArray();
            }
        }

        /// <summary>Reads back what <see cref="Write"/> wrote (any channel count; PcmWav.Read takes mono and stereo only).</summary>
        public static bool TryReadHeader(byte[] file, out int channels, out int sampleRate, out int frames)
        {
            channels = 0; sampleRate = 0; frames = 0;
            if (file == null || file.Length < HeaderBytes) return false;
            if (file[0] != 'R' || file[1] != 'I' || file[2] != 'F' || file[3] != 'F' || file[8] != 'W' || file[9] != 'A' || file[10] != 'V' || file[11] != 'E') return false;
            if (file[36] != 'd' || file[37] != 'a' || file[38] != 't' || file[39] != 'a') return false;
            channels = BitConverter.ToUInt16(file, 22);
            sampleRate = BitConverter.ToInt32(file, 24);
            int dataBytes = BitConverter.ToInt32(file, 40);
            if (channels < 1 || dataBytes < 0 || dataBytes > file.Length - HeaderBytes || dataBytes % (channels * 2) != 0) return false;
            frames = dataBytes / (channels * 2);
            return true;
        }

        static void Put(byte[] b, int at, string text) { for (int i = 0; i < text.Length; i++) b[at + i] = (byte)text[i]; }
        static void PutInt(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
        static void PutShort(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
    }

    internal enum NoiseCaptureDecision { Start, Extend, Refuse }

    /// <summary>When a WAV capture may start: six per raid (three of them automatic), five seconds apart, 500 MB free, 64 MB kept on disk.</summary>
    internal sealed class NoiseCapturePolicy
    {
        public const int MaxPerRaid = 6, MaxAutoPerRaid = 3;
        public const double MinSpacingSeconds = 5.0;
        public const long TotalCapBytes = 64L * 1024 * 1024, MinFreeBytes = 500L * 1024 * 1024;

        int _raid, _auto;
        double _lastStart = double.NegativeInfinity;

        public int StartedThisRaid => _raid;
        public int AutomaticThisRaid => _auto;

        public void BeginRaid() { _raid = 0; _auto = 0; _lastStart = double.NegativeInfinity; }

        /// <param name="pending">a capture is waiting for its two seconds after the mark</param>
        /// <param name="freeBytes">free space of the target drive, or a negative number when unknown</param>
        public NoiseCaptureDecision Request(double now, bool automatic, bool pending, long freeBytes, out string reason)
        {
            reason = null;
            if (freeBytes >= 0 && freeBytes < MinFreeBytes) { reason = "disk"; return NoiseCaptureDecision.Refuse; }
            if (pending)
            {
                if (!automatic && now - _lastStart < MinSpacingSeconds) return NoiseCaptureDecision.Extend;
                reason = "busy"; return NoiseCaptureDecision.Refuse;
            }
            if (now - _lastStart < MinSpacingSeconds) { reason = "spacing"; return NoiseCaptureDecision.Refuse; }
            if (_raid >= MaxPerRaid) { reason = "raidcap"; return NoiseCaptureDecision.Refuse; }
            if (automatic && _auto >= MaxAutoPerRaid) { reason = "autocap"; return NoiseCaptureDecision.Refuse; }
            return NoiseCaptureDecision.Start;
        }

        public void Started(double now, bool automatic)
        {
            _raid++; if (automatic) _auto++;
            _lastStart = now;
        }

        /// <summary>How many of the oldest files must go so that the kept files plus the incoming one stay within the cap.</summary>
        public static int PlanDeletions(long[] sizesOldestFirst, long incomingBytes, long capBytes = TotalCapBytes)
        {
            long total = incomingBytes;
            foreach (long s in sizesOldestFirst) total += s;
            int remove = 0;
            while (total > capBytes && remove < sizesOldestFirst.Length) total -= sizesOldestFirst[remove++];
            return remove;
        }
    }
}
