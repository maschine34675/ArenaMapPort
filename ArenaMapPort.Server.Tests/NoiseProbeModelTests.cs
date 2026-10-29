#nullable disable
using System.Globalization;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    /// <summary>
    /// The Unity-free core of the developer noise probe (NoiseProbeModel.cs) on synthetic signals: a white noise bed, noise bursts of known
    /// peak level, a single-sample click, tones, a speech-like signal and silence. The signals are deterministic, so a changed threshold shows up
    /// as a changed count and not as a flaky test. Nothing here was heard or measured on the host; the numbers pin the model, not the game.
    /// </summary>
    public sealed class NoiseProbeModelTests
    {
        const int Rate = 48000, Window = 1024, Hop = 512;

        sealed class Seen
        {
            public NoiseKind Kind;
            public NoiseLevel Level;
            public double Time, DurMs;
            public float Pk, PkDb, Hf, OverRms;
            public int PkChannel, Number;
            public string Note;
        }

        static Seen Copy(NoiseHit h) => new Seen
        {
            Kind = h.Kind, Level = h.Level, Time = h.Time, DurMs = h.DurMs, Pk = h.Pk, PkDb = h.PkDb, Hf = h.Hf, OverRms = h.OverRmsDb,
            PkChannel = h.PkChannel, Number = h.Number, Note = h.Note
        };

        static uint Step(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return state;
        }

        /// <summary>White uniform noise with the given rms (dBFS).</summary>
        static float[] Bed(int samples, double rmsDb, uint seed)
        {
            var x = new float[samples];
            uint s = seed * 2654435761u + 1u;
            float rms = (float)Math.Pow(10.0, rmsDb / 20.0);
            for (int i = 0; i < samples; i++) x[i] = (float)((Step(ref s) >> 8) / 8388608.0 - 1.0) * 1.7320508f * rms;
            return x;
        }

        static float[] Sine(int samples, double hz, double amplitude, double phase = 0.0)
        {
            var x = new float[samples];
            for (int i = 0; i < samples; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate + phase));
            return x;
        }

        /// <summary>Low-passed noise under a slow syllable envelope: a stand-in for speech without sibilants (the model cannot tell those from noise).</summary>
        static float[] Speech(int samples, double rmsDb, uint seed)
        {
            var noise = Bed(samples, 0, seed);
            var x = new float[samples];
            float a = 0f, b = 0f;
            for (int i = 0; i < samples; i++)
            {
                a += 0.05f * (noise[i] - a);
                b += 0.05f * (a - b);
                double t = i / (double)Rate;
                x[i] = b * (float)(0.55 + 0.45 * Math.Sin(2 * Math.PI * 3.2 * t));
            }
            double sum = 0;
            foreach (float v in x) sum += (double)v * v;
            float scale = (float)(Math.Pow(10.0, rmsDb / 20.0) / Math.Sqrt(sum / samples));
            for (int i = 0; i < samples; i++) x[i] *= scale;
            return x;
        }

        static void Add(float[] target, float[] part, int at)
        {
            for (int i = 0; i < part.Length; i++) target[at + i] += part[i];
        }

        static List<Seen> Run(NoiseDetector detector, float[] mono, double t0 = 0.0, int hop = Hop)
        {
            var seen = new List<Seen>();
            var block = new NoiseBlock();
            for (int end = Window; end <= mono.Length; end += hop)
            {
                block.Reset();
                block.AddChannel(0, mono, end - Window, 1, Window);
                block.Finish();
                int n = detector.Process(block, t0 + end / (double)Rate);
                for (int i = 0; i < n; i++) seen.Add(Copy(detector.Hits[i]));
            }
            int tail = detector.Flush(t0 + mono.Length / (double)Rate);
            for (int i = 0; i < tail; i++) seen.Add(Copy(detector.Hits[i]));
            return seen;
        }

        static List<Seen> RunChannels(NoiseDetector detector, float[][] channels, int hop = Hop)
        {
            var seen = new List<Seen>();
            var block = new NoiseBlock();
            int length = channels[0].Length;
            for (int end = Window; end <= length; end += hop)
            {
                block.Reset();
                for (int c = 0; c < channels.Length; c++) block.AddChannel(c, channels[c], end - Window, 1, Window);
                block.Finish();
                int n = detector.Process(block, end / (double)Rate);
                for (int i = 0; i < n; i++) seen.Add(Copy(detector.Hits[i]));
            }
            int tail = detector.Flush(length / (double)Rate);
            for (int i = 0; i < tail; i++) seen.Add(Copy(detector.Hits[i]));
            return seen;
        }

        static NoiseDetector Fresh()
        {
            var detector = new NoiseDetector();
            detector.Reset(0);
            return detector;
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Block statistics

        [Fact]
        public void BlockStatisticsFollowTheTheoryForNoiseAndTones()
        {
            var block = new NoiseBlock();
            var white = Bed(8192, -20, 1);
            block.AddChannel(0, white, 0, 1, white.Length); block.Finish();
            Assert.InRange(block.Hf, 0.95f, 1.05f);                    // white noise: rms(diff) = sqrt 2 * rms
            Assert.InRange(block.Rms, 0.095f, 0.105f);

            var tone = Sine(8192, 1000, 0.5);
            block.Reset(); block.AddChannel(0, tone, 0, 1, tone.Length); block.Finish();
            Assert.InRange(block.Hf, 0.088f, 0.097f);                  // 1 kHz at 48 kHz: 0.0925
            Assert.InRange(block.D1, 0.064f, 0.067f);                  // 2 * 0.5 * sin(pi * 1000 / 48000)
            Assert.Equal(0.5f, block.Peak, 3);

            var six = Sine(8192, 6000, 0.5);
            block.Reset(); block.AddChannel(0, six, 0, 1, six.Length); block.Finish();
            Assert.InRange(block.Hf, 0.53f, 0.55f);                    // 6 kHz: 0.541
        }

        [Fact]
        public void InterleavedAndPerChannelInputGiveTheSameStatistics()
        {
            var left = Bed(2048, -30, 3);
            var right = Bed(2048, -40, 4);
            var interleaved = new float[4096];
            for (int i = 0; i < 2048; i++) { interleaved[2 * i] = left[i]; interleaved[2 * i + 1] = right[i]; }
            var a = new NoiseBlock(); a.AddChannel(0, left, 0, 1, 2048); a.AddChannel(1, right, 0, 1, 2048); a.Finish();
            var b = new NoiseBlock(); b.AddChannel(0, interleaved, 0, 2, 2048); b.AddChannel(1, interleaved, 1, 2, 2048); b.Finish();
            Assert.Equal(a.Peak, b.Peak); Assert.Equal(a.PeakChannel, b.PeakChannel); Assert.Equal(0, b.PeakChannel);
            Assert.Equal(a.Rms, b.Rms); Assert.Equal(a.D1, b.D1); Assert.Equal(a.Hf, b.Hf);
            Assert.Equal(2, b.Channels);
            Assert.True(b.ChannelPeak[0] > b.ChannelPeak[1]);
        }

        [Fact]
        public void NonFiniteAndFullScaleSamplesAreCountedAndDoNotPoisonTheSums()
        {
            var x = Bed(1024, -40, 5);
            x[10] = float.NaN; x[20] = float.PositiveInfinity; x[30] = 1.0f; x[40] = -0.9995f;
            var block = new NoiseBlock();
            block.AddChannel(0, x, 0, 1, 1024); block.Finish();
            Assert.Equal(2, block.NonFinite);
            Assert.Equal(2, block.Clipped);
            Assert.False(float.IsNaN(block.Rms) || float.IsNaN(block.Hf) || float.IsNaN(block.D1));
            Assert.Equal(1.0f, block.Peak);
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Detector

        [Theory]
        [InlineData(-18f)]
        [InlineData(-24f)]
        [InlineData(-30f)]
        public void TenMillisecondNoiseBurstsOverAQuietBedAreFoundOnceEveryTime(float peakDb)
        {
            for (int trial = 0; trial < 40; trial++)
            {
                var detector = Fresh();
                var signal = Bed(Rate * 8, -60, (uint)trial + 1);
                int at = Rate * 5 + trial * 37;                          // a different alignment against the 512-sample hop each time
                Add(signal, NoiseSelfTest.MakeBurst(Rate, peakDb, trial), at);
                var seen = Run(detector, signal);
                var peak = Assert.Single(seen, h => h.Kind == NoiseKind.Peak);
                Assert.Equal(NoiseLevel.Mid, peak.Level);
                Assert.InRange(peak.Time, at / (double)Rate, at / (double)Rate + 0.06);
                Assert.InRange(peak.PkDb, peakDb - 0.7f, peakDb + 0.7f);
                Assert.InRange(peak.Hf, 0.7f, 1.3f);
                Assert.True(seen.Count(h => h.Kind == NoiseKind.Click) <= 1, "an edge window may add at most one CLICK");
                Assert.DoesNotContain(seen, h => h.Kind != NoiseKind.Peak && h.Kind != NoiseKind.Click);
            }
        }

        [Fact]
        public void ABurstBelowThePeakFloorIsNotReportedWhateverItsShape()
        {
            for (int trial = 0; trial < 20; trial++)
            {
                var detector = Fresh();
                var signal = Bed(Rate * 8, -60, (uint)trial + 100);
                Add(signal, NoiseSelfTest.MakeBurst(Rate, -36f, trial), Rate * 5 + trial * 41);
                Assert.Empty(Run(detector, signal).Where(h => h.Kind == NoiseKind.Peak));
            }
        }

        [Fact]
        public void AQuietBedAloneIsSilentForAMinute()
        {
            var detector = Fresh();
            Assert.Empty(Run(detector, Bed(Rate * 60, -60, 9)));
            Assert.InRange(detector.Minute.Blocks, 5000, 5800);
            Assert.Equal(0, detector.Minute.Suppressed);
        }

        [Fact]
        public void TonesAreNeverAClickAndAnAbruptToneIsNeitherPeakNorClick()
        {
            var detector = Fresh();
            Assert.Empty(Run(detector, Sine(Rate * 20, 1000, 0.1)));
            detector = Fresh();
            Assert.Empty(Run(detector, Sine(Rate * 20, 1000, 0.5)));        // loud, stationary: D1 is large but never against its own median
            // A tone that starts smoothly (sine phase 0, 0.2 amplitude) is neither: D1 is 0.026, below the 0.04 floor, and HF is 0.09.
            detector = Fresh();
            var smooth = Bed(Rate * 10, -70, 1);
            Add(smooth, Sine(Rate * 5, 1000, 0.2), Rate * 5);
            Assert.Empty(Run(detector, smooth).Where(h => h.Kind == NoiseKind.Peak || h.Kind == NoiseKind.Click));

            // The same tone cut in at its peak is a real discontinuity of 0.2: one CLICK, still no PEAK (the tone has no wideband energy).
            detector = Fresh();
            var abrupt = Bed(Rate * 10, -70, 1);
            Add(abrupt, Sine(Rate * 5, 1000, 0.2, Math.PI / 2), Rate * 5);
            var seen = Run(detector, abrupt);
            Assert.Empty(seen.Where(h => h.Kind == NoiseKind.Peak));
            Assert.Single(seen, h => h.Kind == NoiseKind.Click);
        }

        [Fact]
        public void ASingleSampleClickIsAClickWhenTheLevelDoesNotLiftTheBlockAndAPeakWhenItDoes()
        {
            var detector = Fresh();
            var quiet = Bed(Rate * 8, -60, 11);
            quiet[Rate * 5] += 0.08f;                                        // block rms +8.6 dB: below the 10 dB PEAK condition
            var click = Assert.Single(Run(detector, quiet));
            Assert.Equal(NoiseKind.Click, click.Kind);
            Assert.InRange(click.PkDb, -22.4f, -21.4f);

            detector = Fresh();
            var loud = Bed(Rate * 8, -60, 11);
            loud[Rate * 5] += 0.15f;                                         // +13.6 dB: the broadband PEAK wins, it is not also a CLICK
            var peak = Assert.Single(Run(detector, loud));
            Assert.Equal(NoiseKind.Peak, peak.Kind);
        }

        [Fact]
        public void SpeechLikeSignalsRaiseNoPeakOrClickInAMinute()
        {
            var detector = Fresh();
            Assert.Empty(Run(detector, Speech(Rate * 60, -40, 21)));
            detector = Fresh();
            Assert.Empty(Run(detector, Speech(Rate * 60, -55, 22)));
        }

        [Fact]
        public void TwoOrMoreSilentBlocksOverAnAudibleBedAreADropout()
        {
            var detector = Fresh();
            var signal = Bed(Rate * 8, -60, 31);
            Array.Clear(signal, Rate * 5, Rate / 10);                        // 100 ms of exact zeros
            var drop = Assert.Single(Run(detector, signal));
            Assert.Equal(NoiseKind.Dropout, drop.Kind);
            Assert.InRange(drop.DurMs, 60, 160);

            detector = Fresh();
            signal = Bed(Rate * 8, -60, 31);
            Array.Clear(signal, Rate * 5, 960);                              // shorter than one block: never two silent blocks
            Assert.Empty(Run(detector, signal));

            detector = Fresh();
            signal = Bed(Rate * 8, -90, 31);                                 // silence is silence: the bed itself is below the 3e-4 floor
            Array.Clear(signal, Rate * 5, Rate / 10);
            Assert.Empty(Run(detector, signal));
        }

        [Fact]
        public void FullScaleAndNonFiniteSamplesAreClipsAndAreNotIgnoredAtRaidStart()
        {
            var detector = Fresh();
            var signal = Bed(Rate * 4, -60, 41);
            signal[Rate * 1] = 1.0f;                                         // 1 s: inside the 3 s ignore window for PEAK and CLICK
            var clip = Assert.Single(Run(detector, signal));
            Assert.Equal(NoiseKind.Clip, clip.Kind);
            Assert.Contains("clipped=1", clip.Note);

            detector = Fresh();
            signal = Bed(Rate * 8, -60, 42);
            signal[Rate * 5] = float.NaN;
            var nan = Assert.Single(Run(detector, signal), h => h.Kind == NoiseKind.Clip);
            Assert.Contains("nonfinite=1", nan.Note);
        }

        [Fact]
        public void PeaksAndClicksAreIgnoredForThreeSecondsAfterAResetAndNotAfter()
        {
            var detector = Fresh();
            var signal = Bed(Rate * 8, -60, 51);
            Add(signal, NoiseSelfTest.MakeBurst(Rate, -18f, 1), (int)(Rate * 1.5));
            Add(signal, NoiseSelfTest.MakeBurst(Rate, -18f, 2), Rate * 5);
            var peaks = Run(detector, signal).Where(h => h.Kind == NoiseKind.Peak).ToList();
            var only = Assert.Single(peaks);
            Assert.InRange(only.Time, 5.0, 5.06);
            Assert.True(detector.Ignoring(2.9));
            Assert.False(detector.Ignoring(3.0));
        }

        [Fact]
        public void ARefractoryPeriodMergesBurstsLessThanHalfASecondApartPerKindNotAcrossKinds()
        {
            var detector = Fresh();
            var signal = Bed(Rate * 8, -60, 61);
            Add(signal, NoiseSelfTest.MakeBurst(Rate, -24f, 1), Rate * 5);
            Add(signal, NoiseSelfTest.MakeBurst(Rate, -24f, 2), Rate * 5 + 4800);   // 100 ms later: inside the 150 ms refractory period
            Add(signal, NoiseSelfTest.MakeBurst(Rate, -24f, 3), Rate * 6 + 2400);   // 1.45 s later
            var peaks = Run(detector, signal).Where(h => h.Kind == NoiseKind.Peak).ToList();
            Assert.Equal(2, peaks.Count);
            Assert.True(detector.Minute.Refractory >= 1);
            Assert.Equal(2, detector.Minute.Bursts[0]);
        }

        [Fact]
        public void TheLineLimitCountsTheRestAsSuppressedAndTheNextMinuteStartsFresh()
        {
            var detector = Fresh();
            var signal = Bed(Rate * 30, -60, 71);
            for (int i = 0; i < 40; i++) Add(signal, NoiseSelfTest.MakeBurst(Rate, -24f, i), Rate * 4 + i * (Rate / 2));
            var peaks = Run(detector, signal).Where(h => h.Kind == NoiseKind.Peak).ToList();
            Assert.Equal(12, peaks.Count);
            Assert.Equal(40, detector.Minute.Bursts[0]);
            Assert.True(detector.Minute.Suppressed >= 28);
            Assert.False(detector.MinuteDue(59.9));
            Assert.True(detector.MinuteDue(60.0));

            detector.RollMinute(60.0);
            Assert.Equal(0, detector.Minute.Bursts[0]);
            Assert.Equal(0, detector.Minute.Suppressed);
            var next = Bed(Rate * 4, -60, 72);
            Add(next, NoiseSelfTest.MakeBurst(Rate, -24f, 1), Rate * 2);
            Assert.Single(Run(detector, next, 62.0), h => h.Kind == NoiseKind.Peak);   // the baseline is warm and the line budget is back
        }

        [Fact]
        public void LoudBurstsAreCountedAndLoggedAtMostFourTimesAMinute()
        {
            var detector = Fresh();
            var signal = Bed(Rate * 20, -60, 81);
            for (int i = 0; i < 12; i++) Add(signal, NoiseSelfTest.MakeBurst(Rate, -6f, i), Rate * 4 + i * (Rate * 2 / 5));
            var seen = Run(detector, signal);
            Assert.Equal(4, seen.Count(h => h.Level == NoiseLevel.Loud));
            Assert.Equal(12, detector.Minute.Loud);
            Assert.True(detector.Minute.Suppressed >= 8);
        }

        [Fact]
        public void TheChannelOfAPeakIsReportedAndTheOthersStayQuiet()
        {
            var detector = Fresh();
            var channels = new float[6][];
            for (int c = 0; c < 6; c++) channels[c] = Bed(Rate * 8, -60, (uint)(90 + c));
            Add(channels[2], NoiseSelfTest.MakeBurst(Rate, -24f, 1), Rate * 5);
            var peak = Assert.Single(RunChannels(detector, channels), h => h.Kind == NoiseKind.Peak);
            Assert.Equal(2, peak.PkChannel);
            var hit = new NoiseHit { Kind = NoiseKind.Peak, Number = 1, Channels = 6, PkChannel = 2, Pk = peak.Pk };
            Assert.Contains("@ch3 ", NoiseReport.BurstLine(hit, "12:00:00.000", "", ""));
        }

        [Fact]
        public void TheDefaultThresholdsAreTheDocumentedOnes()
        {
            var t = new NoiseThresholds();
            Assert.Equal(0.03f, t.PeakMin); Assert.Equal(15f, t.PeakOverPkDb); Assert.Equal(10f, t.PeakOverRmsDb); Assert.Equal(0.30f, t.PeakHf); Assert.Equal(0.25f, t.LoudPk);
            Assert.Equal(0.04f, t.ClickMin); Assert.Equal(8f, t.ClickRatio); Assert.Equal(0.5f, t.ClickOfPeak);
            Assert.Equal(3e-4f, t.DropoutBgRms); Assert.Equal(2, t.DropoutBlocks); Assert.Equal(60.0, t.GapMs);
            Assert.Equal(0.15, t.RefractorySeconds); Assert.Equal(3.0, t.IgnoreSeconds);
            Assert.Equal(12, t.LinesPerKindPerMinute); Assert.Equal(4, t.LoudLinesPerMinute); Assert.Equal(64, t.History);
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Gap watch

        /// <summary>Real time and the mixer's own clock, which advances in whole 1024-sample buffers and stops while the audio thread is stalled.</summary>
        sealed class Clocks
        {
            public double Real = 100, Mix = 100, Lag;
            public double Dsp => Math.Floor(Mix / (1024.0 / Rate)) * (1024.0 / Rate);

            public int Frames(NoiseGapWatch watch, int frames, double frameSeconds, bool stalled = false)
            {
                int gaps = 0;
                for (int i = 0; i < frames; i++)
                {
                    Real += frameSeconds;
                    if (!stalled) Mix += frameSeconds;
                    if (watch.Update(Real, Dsp, out double lag)) { gaps++; Lag = lag; }
                }
                return gaps;
            }
        }

        [Fact]
        public void TheGapWatchSeesAnUnderrunButNotAMainThreadHitchOrNormalBufferJitter()
        {
            var watch = new NoiseGapWatch();
            var clocks = new Clocks();
            Assert.Equal(0, clocks.Frames(watch, 600, 0.01));                              // 100 fps with DSP time in 21.3 ms steps
            Assert.Equal(0, clocks.Frames(watch, 60, 0.0167));                             // 60 fps
            Assert.Equal(0, clocks.Frames(watch, 20, 0.05));                               // 20 fps

            clocks.Real += 0.4; clocks.Mix += 0.4;                                         // a 400 ms main thread hitch: both clocks move
            Assert.False(watch.Update(clocks.Real, clocks.Dsp, out _));
            Assert.Equal(0, clocks.Frames(watch, 100, 0.01));

            Assert.Equal(1, clocks.Frames(watch, 9, 0.01, stalled: true));                 // the audio thread produced nothing for 90 ms
            Assert.InRange(clocks.Lag, 60.0, 90.0);
            Assert.Equal(0, clocks.Frames(watch, 300, 0.01));                              // the lost time is never made up: the new lag is the baseline, not a second gap

            Assert.Equal(1, clocks.Frames(watch, 8, 0.01, stalled: true));                 // another stall is reported again
            Assert.Equal(0, clocks.Frames(watch, 300, 0.01));
        }

        [Fact]
        public void TheDetectorReportsAGapOncePerRefractoryPeriodAndNumbersItLikeABurst()
        {
            var detector = Fresh();
            var first = detector.ObserveGap(10.0, 87.0, "src=dsp");
            Assert.NotNull(first);
            Assert.Equal(NoiseKind.Gap, first.Kind); Assert.Equal(87.0, first.DurMs); Assert.Equal(1, first.Number); Assert.Equal("src=dsp", first.Note);
            Assert.Null(detector.ObserveGap(10.1, 90.0, "src=dsp"));                      // 100 ms later: refractory
            var second = detector.ObserveGap(10.4, 70.0, "src=tap");
            Assert.Equal(2, second.Number);
            Assert.Equal(2, detector.Minute.Bursts[3]);
            Assert.Equal(1, detector.Minute.Refractory);
            Assert.Contains("kind=gap lvl=MID lag=70ms src=tap", NoiseReport.BurstLine(second, "00:00:00.000", "", ""));
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Event ring, labels, report text

        [Fact]
        public void TheEventRingKeepsTheLastThousandEntriesInOrderAndNeverAllocates()
        {
            var ring = new NoiseEventRing(1024);
            for (int i = 0; i < 300; i++) ring.Add(i, i, NoiseEv.SpecStart, i, 0, 0, 0, 0, 0);
            Assert.Equal(300, ring.Count);
            for (int i = 300; i < 2000; i++) ring.Add(i, i, NoiseEv.AmbStart, i, 0, 0, 0, 0, 0);
            Assert.Equal(1024, ring.Count);
            Assert.Equal(2000 - 1024, ring[0].A);
            Assert.Equal(1999, ring[1023].A);
            Assert.Throws<ArgumentOutOfRangeException>(() => ring[1024]);
            for (int i = 0; i < 20000; i++) ring.Add(i, i, NoiseEv.Prop, i, 0, 1f, 2f, 3, 4);       // warm: the JIT tiers up and may allocate a few bytes once
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++) ring.Add(i, i, NoiseEv.Prop, i, 0, 1f, 2f, 3, 4);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 512L);              // one allocation per Add would be megabytes
            ring.Clear();
            Assert.Equal(0, ring.Count);
        }

        [Fact]
        public void OnlyEventsThatCanMakeANoiseExplainABurst()
        {
            var ring = new NoiseEventRing(64);
            ring.Add(10.0, 0, NoiseEv.SpecOnset, 0, 0, 0, 0, 0, 0);
            ring.Add(10.1, 0, NoiseEv.Mark, 0, 0, 0, 0, 0, 0);
            ring.Add(10.2, 0, NoiseEv.Burst, 0, 0, 0, 0, 0, 0);
            ring.Add(10.3, 0, NoiseEv.AmbStop, 0, 0, 0, 0, 0, 0);
            ring.Add(10.4, 0, NoiseEv.Gc, 0, 0, 0, 0, 0, 0);
            Assert.False(ring.HasCause(10.5, 0.4, 0.05));                   // derived, bookkeeping and stop-only events explain nothing
            ring.Add(10.46, 0, NoiseEv.AmbStart, 0, 0, 0, 0, 0, 0);
            Assert.True(ring.HasCause(10.5, 0.4, 0.05));                    // 40 ms before
            Assert.False(ring.HasCause(11.0, 0.4, 0.05));                   // 540 ms before: outside the 400 ms window
            ring.Add(10.6, 0, NoiseEv.Room, 0, 0, 0, 0, 0, 0);
            Assert.True(ring.HasCause(10.58, 0.4, 0.05));                   // 20 ms after the burst start: the poll lags the audio
            Assert.False(ring.HasCause(10.4, 0.01, 0.05));                  // 200 ms after is too late
            foreach (var kind in new[] { NoiseEv.SpecStart, NoiseEv.SpecStop, NoiseEv.AmbStart, NoiseEv.AmbRestart, NoiseEv.Prop, NoiseEv.Room, NoiseEv.RoomReverb,
                NoiseEv.RoomShot, NoiseEv.Gate, NoiseEv.Puddle, NoiseEv.Rain, NoiseEv.RainPlay, NoiseEv.Config, NoiseEv.Reset, NoiseEv.Life, NoiseEv.Hitch })
                Assert.True(NoiseText.IsCause(kind), kind.ToString());
        }

        [Fact]
        public void CopyWindowReturnsTheEventsOfTheWindowOldestFirst()
        {
            var ring = new NoiseEventRing(16);
            for (int i = 0; i < 40; i++) ring.Add(i, 0, NoiseEv.Prop, i, 0, 0, 0, 0, 0);
            var list = new List<NoiseEvent>();
            Assert.Equal(6, ring.CopyWindow(30, 35, list));
            Assert.Equal(new[] { 30, 31, 32, 33, 34, 35 }, list.Select(e => e.A).ToArray());
        }

        [Fact]
        public void LabelsAreInternedAndBounded()
        {
            var labels = new NoiseLabels();
            int a = labels.Intern("cracles (1)");
            Assert.Equal(a, labels.Intern("cracles (1)"));
            Assert.Equal("cracles (1)", labels.Get(a));
            Assert.Equal(0, labels.Intern(null)); Assert.Equal("", labels.Get(0));
            for (int i = 0; i < NoiseLabels.Max + 50; i++) labels.Intern("name" + i);
            Assert.Equal(NoiseLabels.Max, labels.Count);
            Assert.Equal("?", labels.Get(labels.Intern("one more")));
        }

        [Fact]
        public void TheReportTextCarriesTheDocumentedFields()
        {
            var labels = new NoiseLabels();
            var ring = new NoiseEventRing(64);
            ring.Add(100.00, 0, NoiseEv.SpecStart, 5, 2, 12.5f, 0.31f, labels.Intern("fan10_01"), 0);
            ring.Add(100.40, 0, NoiseEv.SpecStop, 5, (int)NoiseStopReason.Dead, 1234f, 0.45f, labels.Intern("fan10_01"), 0);
            ring.Add(100.50, 0, NoiseEv.AmbStart, 1031, 0, 14.2f, 0.41f, labels.Intern("cracles (1)"), labels.Intern("cracles_02"));
            ring.Add(100.55, 0, NoiseEv.Prop, 1, 0, 6.2f, 0f, labels.Intern("Bottle (4)"), labels.Intern("GlassBottle"));
            ring.Add(100.60, 0, NoiseEv.Room, 19, 0, 0, 0, labels.Intern("IndoorConcrete"), 0);
            ring.Add(100.62, 0, NoiseEv.RoomReverb, 4, 0, 0, 0, 0, 0);
            ring.Add(100.63, 0, NoiseEv.Hitch, 0, 0, 62f, 0, 0, 0);
            ring.Add(100.64, 0, NoiseEv.Gc, 1, 3, 0, 0, 0, 0);

            Assert.Equal("spec+ fan10_01#5 12.5m occ0.31 voices=2", NoiseReport.EventText(ring[0], labels));
            Assert.Equal("spec- fan10_01#5 reason=dead played=1234ms left=0.45s", NoiseReport.EventText(ring[1], labels));
            Assert.Equal("amb+ cracles (1) clip=cracles_02 14.2m vol=0.41 pitch=1.03", NoiseReport.EventText(ring[2], labels));
            Assert.Equal("prop Bottle (4) GlassBottle 6.2m played", NoiseReport.EventText(ring[3], labels));
            Assert.Equal("room 19/IndoorConcrete", NoiseReport.EventText(ring[4], labels));
            Assert.Equal("roomReverb cat=4 changed=0", NoiseReport.EventText(ring[5], labels));
            Assert.Equal("hitch 62ms", NoiseReport.EventText(ring[6], labels));
            Assert.Equal("gc gen1 +3", NoiseReport.EventText(ring[7], labels));
            Assert.Equal("  -0.600s spec+ fan10_01#5 12.5m occ0.31 voices=2", NoiseReport.EventLine(ring[0], labels, 100.6));

            Assert.Equal("amb=[cracles (1)@14.2m age120ms] prop=[GlassBottle@6.2m age70ms] ev=[hitch@+0.01s,roomReverb@+0.00s,room@-0.02s,prop@-0.07s,amb+@-0.12s,spec-@-0.22s,spec+@-0.62s]",
                NoiseReport.Context(ring, labels, 100.62));
        }

        [Fact]
        public void TheArmedMinuteMarkAndCalibrationLinesHaveTheDocumentedShape()
        {
            Assert.Equal("armed mode=Detect map=arena_equator speakers=Mode5point1 driver=Stereo ch=6 rate=48000 dsp=1024 voices=196/256 calls=on mix=g2.0/o0.40/r2.50 roomShot=on roomReverb=on tap=readback ring=none",
                NoiseReport.Armed(NoiseProbeMode.Detect, "arena_equator", "Mode5point1", "Stereo", 6, 48000, 1024, 196, 256, "on", "g2.0/o0.40/r2.50", "on", "on", 15));
            Assert.EndsWith("tap=readback+filter ring=15s", NoiseReport.Armed(NoiseProbeMode.Capture, "arena_bowl", "Stereo", "Stereo", 2, 44100, 1024, 196, 256, "off", "n/a", "off", "off", 15));

            var detector = Fresh();
            var signal = Bed(Rate * 12, -60, 5);
            Add(signal, NoiseSelfTest.MakeBurst(Rate, -24f, 1), Rate * 5);
            Run(detector, signal);
            detector.Minute.OnEvent(NoiseEv.SpecStart, 0, 0); detector.Minute.OnEvent(NoiseEv.SpecStart, 0, 0); detector.Minute.OnEvent(NoiseEv.SpecStop, 0, 0);
            detector.Minute.OnEvent(NoiseEv.AmbStart, 0, 0); detector.Minute.OnEvent(NoiseEv.AmbRestart, 0, 0); detector.Minute.OnEvent(NoiseEv.Prop, 0, 0);
            detector.Minute.OnEvent(NoiseEv.Room, 0, 0); detector.Minute.OnEvent(NoiseEv.Config, 0, 0); detector.Minute.OnEvent(NoiseEv.Hitch, 0, 0);
            detector.Minute.OnEvent(NoiseEv.Gc, 1, 2);
            detector.Minute.AddProbeCost(0.2); detector.Minute.AddProbeCost(0.4);
            string line = NoiseReport.MinuteLine(detector.Minute, "arena_equator", 12.0);
            Assert.StartsWith("minute map=arena_equator blocks=", line);
            Assert.Matches(@"% bursts peak=1 click=[01] dropout=0 gap=0 clip=0 loud=0 suppressed=0 refractory=\d+ orphan=0 evOrphan=0 bgMedian=-(59|60)\.\d hitch50=1 gc=0/2/0 ", line);
            Assert.Contains(" cover=", line);
            Assert.EndsWith(" spec=+2/-1 amb=+1 restarts=1 props=1 rooms=1 resets=1 probeMs=0.300/0.400", line);
            string calibration = NoiseReport.CalibrationLine(detector.Minute, 1);
            Assert.StartsWith("calib minute=1 pk50=-", calibration);
            Assert.Matches(@"pk99=-?\d+\.\d pk999=-?\d+\.\d over50=[+-]\d+\.\d over99=[+-]\d+\.\d over999=[+-]\d+\.\d$", calibration);
            Assert.Equal("MARK #3 15:04:13.002 frame=48213 pos=(1.0,2.0,3.0) room=19/Indoor fps=118 (the reaction lag is 0.3-1.5 s)",
                NoiseReport.MarkHeader(3, "15:04:13.002", 48213, "pos=(1.0,2.0,3.0) room=19/Indoor fps=118"));
        }

        [Fact]
        public void ABurstLineNamesTheKindTheLevelsTheOrphanFlagsAndTheContext()
        {
            var hit = new NoiseHit
            {
                Kind = NoiseKind.Peak, Level = NoiseLevel.Mid, Number = 12, Pk = 0.0618f, Rms = 0.0112f, BgPk = 0.002f, BgRms = 0.00123f, OverRmsDb = 19.1f, OverPkDb = 22.0f,
                D1 = 0.021f, Hf = 0.62f, DurMs = 21.3, PkChannel = 2, Channels = 3
            };
            hit.ChannelPk[0] = 0.03f; hit.ChannelPk[1] = 0.035f; hit.ChannelPk[2] = 0.0618f;
            string line = NoiseReport.BurstLine(hit, "15:04:12.481", "pos=(1.0,2.0,3.0) orphan=1", "amb=[] prop=[] ev=[] census=[pool=3]");
            Assert.Equal("BURST #12 15:04:12.481 kind=peak lvl=MID pk=-24.2@ch3 rms=-39.0 bg=-58.2 over=+19.1 overPk=+22.0 d1=0.021 hf=0.62 dur=21ms ch=[-30,-29,-24] " +
                "pos=(1.0,2.0,3.0) orphan=1 | amb=[] prop=[] ev=[] census=[pool=3]", line);
            hit.Kind = NoiseKind.Clip; hit.Note = "clipped=3 nonfinite=0"; hit.Level = NoiseLevel.Loud;
            Assert.Contains("kind=clip lvl=LOUD", NoiseReport.BurstLine(hit, "x", "", ""));
            Assert.EndsWith("clipped=3 nonfinite=0", NoiseReport.BurstLine(hit, "x", "", ""));
        }

        [Fact]
        public void ShortClipNamesAndTheOnsetFramesAreStable()
        {
            Assert.Equal("fan9_02", NoiseText.ShortClip("arena_fan9_participating_02"));
            Assert.Equal("fan13_03", NoiseText.ShortClip("arena_fan13_participating_03"));
            Assert.Equal("something else", NoiseText.ShortClip("something else"));
            Assert.Equal("", NoiseText.ShortClip(null));
            Assert.Equal(new[] { 1, 2, 3, 5, 8, 13 }, NoiseText.OnsetFrames);
            Assert.True(NoiseText.IsOnsetFrame(8)); Assert.False(NoiseText.IsOnsetFrame(4)); Assert.False(NoiseText.IsOnsetFrame(14));
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Self-test

        [Fact]
        public void TheSelfTestBurstHasExactlyTheRequestedPeakAndIsDeterministic()
        {
            foreach (float db in NoiseSelfTest.DefaultLevelsDb)
            {
                var burst = NoiseSelfTest.MakeBurst(Rate, db, 3);
                Assert.Equal(480, burst.Length);
                float peak = burst.Max(v => Math.Abs(v));
                Assert.Equal(Math.Pow(10, db / 20.0), peak, 6);
                Assert.Equal(burst, NoiseSelfTest.MakeBurst(Rate, db, 3));
                Assert.Equal(0f, burst[0], 3); Assert.Equal(0f, burst[^1], 3);
            }
        }

        /// <summary>Drives the self-test the way the probe's frame loop does: 12 ms steps, a 85 ms mixer latency between Play and the first sample.</summary>
        [Fact]
        public void TheSelfTestFindsTheThreeLoudBurstsReportsTheQuietOneAsMissedAndEndsWithASummary()
        {
            var detector = Fresh();
            var mix = Bed(Rate * 12, -60, 7);
            var test = new NoiseSelfTest(null);
            var lines = new List<string>();
            var block = new NoiseBlock();
            test.Begin(4.0);
            for (double t = 4.0; t < 11.0; t += 0.012)
            {
                int end = (int)(t * Rate);
                block.Reset(); block.AddChannel(0, mix, end - Window, 1, Window); block.Finish();
                test.OnBlock(t, block.Peak, detector.LastBgPk);
                int n = detector.Process(block, t);
                for (int i = 0; i < n; i++) test.OnHit(detector.Hits[i]);
                if (test.ShouldPlay(t, out float level))
                    Add(mix, NoiseSelfTest.MakeBurst(Rate, level, (int)level), (int)((t + 0.085) * Rate));
                while (test.ResultDue(t)) lines.Add(test.TakeResult(t, float.NaN));
            }
            Assert.False(test.Running);
            Assert.Equal(5, lines.Count);
            for (int i = 0; i < 3; i++) Assert.Matches(@"^selftest -(18|24|30)dBFS PASS latency (\d+) ms over \+\d+\.\d dB$", lines[i]);
            Assert.StartsWith("selftest -36dBFS MISS block peak -", lines[3]);
            Assert.Equal("selftest done 3/4 detected; weakest detected -30dBFS", lines[4]);
            foreach (int i in new[] { 0, 1, 2 })
            {
                int latency = int.Parse(lines[i].Split(new[] { "latency " }, StringSplitOptions.None)[1].Split(' ')[0], CultureInfo.InvariantCulture);
                Assert.InRange(latency, 85, 200);                                         // the 85 ms mixer latency plus at most two polling steps
            }
        }

        [Fact]
        public void TheSelfTestPlaysOneCaseAtATimeAndAddsTheTapPeakWhenThereIsOne()
        {
            var test = new NoiseSelfTest(new[] { -20f });
            test.Begin(10.0);
            Assert.False(test.ShouldPlay(10.9, out _));
            Assert.True(test.ShouldPlay(11.0, out float level)); Assert.Equal(-20f, level);
            Assert.False(test.ShouldPlay(11.1, out _));                                    // waiting for the answer
            Assert.False(test.ResultDue(11.4));
            Assert.True(test.ResultDue(11.5));
            Assert.Equal("selftest -20dBFS MISS block peak -120.0 dBFS, background peak -120.0 dBFS tap=-19.8dBFS", test.TakeResult(11.5, -19.8f));
            Assert.True(test.ResultDue(11.6));
            Assert.Equal("selftest done 0/1 detected", test.TakeResult(11.6, float.NaN));
            Assert.False(test.Running);
            Assert.False(test.ResultDue(12));
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Capture: PCM ring, WAV, policy

        [Fact]
        public void ThePcmRingKeepsTheLastSecondsAndCopiesAcrossTheWrap()
        {
            var ring = new NoisePcmRing(2, 1000, 1.0);                                      // 1.0 s at 1000 Hz is raised to the 1024-frame minimum
            Assert.Equal(1024, ring.CapacityFrames);
            var chunk = new float[400];                                                      // 200 frames per write
            long frame = 0;
            for (int w = 0; w < 7; w++)                                                       // 1400 frames: the ring has wrapped
            {
                for (int i = 0; i < 200; i++) { chunk[2 * i] = (frame + i) / 4000f; chunk[2 * i + 1] = -(frame + i) / 4000f; }
                ring.Write(chunk, chunk.Length);
                frame += 200;
            }
            Assert.Equal(1400, ring.WrittenFrames);
            var copy = new short[600];
            Assert.True(ring.TryCopy(1400, 300, copy, out long start, out int copied));
            Assert.Equal(1100, start); Assert.Equal(300, copied);
            for (int i = 0; i < 300; i++)
            {
                int expected = (int)Math.Round((1100 + i) / 4000f * 32767f);
                Assert.InRange((int)copy[2 * i], expected - 1, expected + 1);
                Assert.InRange((int)copy[2 * i + 1], -expected - 1, -expected + 1);
            }
            Assert.False(ring.TryCopy(1400, 1100, new short[2200], out _, out copied));       // more than the ring holds: refused, nothing copied
            Assert.Equal(0, copied);
            Assert.False(ring.TryCopy(300, 100, copy, out _, out copied));                    // frames 200..300 are long overwritten
            Assert.Equal(0, copied);
            Assert.True(ring.TryCopy(5000, 100, copy, out start, out copied));                // beyond what was written: clamped to the end
            Assert.Equal(1300, start); Assert.Equal(100, copied);
        }

        [Fact]
        public void ThePcmRingClampsNaNAndOverloadsAndMeasuresThePeak()
        {
            var ring = new NoisePcmRing(1, 1000, 1.0);
            ring.Write(new[] { 0.5f, -0.5f, float.NaN, 2f, -3f, 0.25f }, 6);
            var copy = new short[6];
            Assert.True(ring.TryCopy(6, 6, copy, out _, out int copied));
            Assert.Equal(6, copied);
            Assert.Equal(new short[] { 16384, -16384, 0, 32767, -32768, 8192 }, copy);
            Assert.Equal(1.0f, ring.PeakSince(0), 3);
            Assert.Equal(0.25f, ring.PeakSince(5), 3);
            Assert.Equal(0f, ring.PeakSince(6));
            var odd = new NoisePcmRing(2, 1000, 1.0);
            odd.Write(new float[7], 7);                                                       // three whole frames, the last sample is dropped
            Assert.Equal(3, odd.WrittenFrames);
        }

        [Fact]
        public void ThePcmRingWritesWithoutAllocating()
        {
            var ring = new NoisePcmRing(6, 48000, 15.0);
            var data = new float[6144];
            ring.Write(data, data.Length);
            for (int i = 0; i < 100; i++) ring.Write(data, data.Length);                            // warm
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++) ring.Write(data, data.Length);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 512L);              // one array per call would be 12 MB
            Assert.Equal(15 * 48000 * 6 * 2L, ring.ByteSize);                                 // 8.6 MB
        }

        [Fact]
        public void TheWavWriterRoundTripsThroughPcmWavForMonoAndStereo()
        {
            var mono = new short[] { 0, 1, -1, 32767, -32768, 1234, -4321 };
            var file = NoiseWav.Build(mono, mono.Length, 1, 44100);
            Assert.Equal(44 + 14, file.Length);
            var read = PcmWav.Read(file);
            Assert.Equal(1, read.Channels); Assert.Equal(44100, read.SampleRate); Assert.Equal(7, read.Frames); Assert.Equal(mono, read.Samples);

            var stereo = new short[] { 1, 2, 3, 4, -5, -6 };
            read = PcmWav.Read(NoiseWav.Build(stereo, 3, 2, 48000));
            Assert.Equal(2, read.Channels); Assert.Equal(48000, read.SampleRate); Assert.Equal(3, read.Frames); Assert.Equal(stereo, read.Samples);
        }

        [Fact]
        public void ASixChannelWavHasACanonicalHeaderAndTheRightSizeForTwelveSeconds()
        {
            var samples = new short[48000 * 12 * 6];
            for (int i = 0; i < samples.Length; i += 997) samples[i] = (short)(i % 30000 - 15000);
            var file = NoiseWav.Build(samples, 48000 * 12, 6, 48000);
            Assert.Equal(44 + 48000 * 12 * 6 * 2, file.Length);                                // 6.9 MB
            Assert.True(NoiseWav.TryReadHeader(file, out int channels, out int rate, out int frames));
            Assert.Equal(6, channels); Assert.Equal(48000, rate); Assert.Equal(576000, frames);
            Assert.Equal(file.Length - 8, BitConverter.ToInt32(file, 4));
            Assert.Equal(48000 * 6 * 2, BitConverter.ToInt32(file, 28));                       // byte rate
            Assert.Equal(12, BitConverter.ToInt16(file, 32));                                  // block align
            Assert.Equal(16, BitConverter.ToInt16(file, 34));
            var back = new short[samples.Length];
            Buffer.BlockCopy(file, 44, back, 0, file.Length - 44);
            Assert.Equal(samples, back);
            Assert.False(NoiseWav.TryReadHeader(new byte[10], out _, out _, out _));
            Assert.Throws<InvalidDataException>(() => PcmWav.Read(file));                      // PcmWav reads mono and stereo only: this header is for Audacity, not for the mod
        }

        [Fact]
        public void TheCapturePolicyAllowsSixPerRaidFiveSecondsApartAndThreeAutomaticOnes()
        {
            var policy = new NoiseCapturePolicy();
            string reason;
            Assert.Equal(NoiseCaptureDecision.Start, policy.Request(100, false, false, 10L << 30, out reason));
            policy.Started(100, false);
            Assert.Equal(NoiseCaptureDecision.Extend, policy.Request(103, false, true, 10L << 30, out reason));      // a mark within 5 s shares the capture
            Assert.Equal(NoiseCaptureDecision.Refuse, policy.Request(106, false, true, 10L << 30, out reason)); Assert.Equal("busy", reason);
            Assert.Equal(NoiseCaptureDecision.Refuse, policy.Request(104, false, false, 10L << 30, out reason)); Assert.Equal("spacing", reason);
            Assert.Equal(NoiseCaptureDecision.Refuse, policy.Request(103, true, true, 10L << 30, out reason));        // an automatic one never extends

            double t = 100;
            for (int i = 0; i < 2; i++) { t += 6; Assert.Equal(NoiseCaptureDecision.Start, policy.Request(t, true, false, -1, out reason)); policy.Started(t, true); }
            t += 6;
            Assert.Equal(NoiseCaptureDecision.Start, policy.Request(t, true, false, -1, out reason)); policy.Started(t, true);     // third automatic
            t += 6;
            Assert.Equal(NoiseCaptureDecision.Refuse, policy.Request(t, true, false, -1, out reason)); Assert.Equal("autocap", reason);
            Assert.Equal(NoiseCaptureDecision.Start, policy.Request(t, false, false, -1, out reason)); policy.Started(t, false);    // the fifth
            t += 6;
            Assert.Equal(NoiseCaptureDecision.Start, policy.Request(t, false, false, -1, out reason)); policy.Started(t, false);    // the sixth
            t += 6;
            Assert.Equal(6, policy.StartedThisRaid);
            Assert.Equal(NoiseCaptureDecision.Refuse, policy.Request(t, false, false, -1, out reason)); Assert.Equal("raidcap", reason);
            policy.BeginRaid();
            Assert.Equal(NoiseCaptureDecision.Start, policy.Request(t, false, false, -1, out reason));
        }

        [Fact]
        public void TheCapturePolicyNeedsFiveHundredMegabytesFreeAndKeepsSixtyFourOnDisk()
        {
            var policy = new NoiseCapturePolicy();
            string reason;
            Assert.Equal(NoiseCaptureDecision.Refuse, policy.Request(1, false, false, 499L << 20, out reason)); Assert.Equal("disk", reason);
            Assert.Equal(NoiseCaptureDecision.Start, policy.Request(1, false, false, 500L << 20, out reason));
            Assert.Equal(NoiseCaptureDecision.Start, policy.Request(1, false, false, -1, out reason));          // unknown free space does not block

            long file = 6_912_044 + 3000;
            var sizes = Enumerable.Repeat(file, 9).ToArray();                                                    // 9 x 6.9 MB = 62.3 MB
            Assert.Equal(1, NoiseCapturePolicy.PlanDeletions(sizes, 6_912_044));                                 // the tenth needs one deletion
            Assert.Equal(0, NoiseCapturePolicy.PlanDeletions(sizes.Take(8).ToArray(), 6_912_044));
            Assert.Equal(2, NoiseCapturePolicy.PlanDeletions(new long[] { 10, 10, 10 }, 100, 115));
            Assert.Equal(3, NoiseCapturePolicy.PlanDeletions(new long[] { 10, 10, 10 }, 100, 50));                // never more than there is
        }

        [Fact]
        public void TheModelFileUsesNoUnityBepInExOrLoggerType()
        {
            var path = FindRepoFile("ArenaMapPort.Client/NoiseProbeModel.cs");
            var code = File.ReadAllText(path);
            Assert.DoesNotContain("UnityEngine", code);
            Assert.DoesNotContain("BepInEx", code);
            Assert.DoesNotContain("Plugin.", code);
            Assert.DoesNotContain("DateTime.Now", code);
        }

        static string FindRepoFile(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, relative);
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException(relative);
        }
    }
}
