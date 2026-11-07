#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ArenaMapPort.Client
{
    #pragma warning disable CS0649 // Package fields are populated by Newtonsoft.Json.
    /// <summary>
    /// The optional <c>reaction_beds</c> object of spectator-audio.json (tools/extract/extract_spectator_audio.py, phase A2 of docs/PLAN-CROWD-REACTIONS.md): the six crowd beds of
    /// the Cheer and Angry banks of Arena's ambient_voice.bundle (three clips each), downmixed to mono, brought to one common level and made loop-safe offline like the neutral
    /// murmur bed. A reaction plays one of them from every stand for its length (the roar layer, SpectatorReactionBeds.cs). Fields the client does not use (the source path, the clip
    /// statistics before the downmix, peak, rms, the processing record) are ignored. A manifest without it is a valid schema-1 manifest and simply has no roar layer.
    /// </summary>
    internal sealed class SpectatorReactionBedSet
    {
        public const int MaxBanks = 2, MaxClipsPerBank = 8;
        public int schema;
        public float target_rms_db;
        public SpectatorReactionBedBank[] banks;

        public void Validate()
        {
            if (schema != 1 || !(target_rms_db >= -60f && target_rms_db <= -10f) || banks == null || banks.Length != MaxBanks)
                throw new InvalidDataException("invalid spectator reaction bed set");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < banks.Length; i++)
            {
                var b = banks[i];
                // Cheer first, Angry second: the order of the extractor's table, so a swapped pair is a damaged file
                if (b == null || b.bank != SpectatorReactionBedKind.BankName(i + 1) || b.trigger != SpectatorReactionBedKind.TriggerOf(i + 1) ||
                    b.kind != SpectatorReactionBedKind.Name(i + 1) || b.clips == null || b.clips.Length < 1 || b.clips.Length > MaxClipsPerBank)
                    throw new InvalidDataException("invalid spectator reaction bed bank");
                string pattern = @"^arena_fans_ambient_" + b.kind + @"[23]?$";
                foreach (var c in b.clips)
                    if (c == null || c.name == null || !Regex.IsMatch(c.name, pattern) || !names.Add(c.name) || c.file != c.name + ".wav" || c.channels != 1 ||
                        c.sample_rate != 44100 || c.frames < 3 * 44100 || c.frames > 12 * 44100 || c.bytes != 44 + c.frames * 2 ||
                        c.seam_crossfade_ms < 1 || c.seam_crossfade_ms > 5000 || c.sha256 == null || !Regex.IsMatch(c.sha256, @"^[0-9a-f]{64}$"))
                        throw new InvalidDataException("invalid spectator reaction bed clip");
            }
        }
    }

    internal sealed class SpectatorReactionBedBank
    {
        public string bank, kind;
        public int trigger;
        public SpectatorReactionBedClip[] clips;
    }

    internal sealed class SpectatorReactionBedClip
    {
        public string name, file, sha256;
        public int channels, sample_rate, frames, bytes, seam_crossfade_ms;
    }
    #pragma warning restore CS0649

    /// <summary>
    /// Which crowd bed answers which reaction kind (user decision 2026-11-07): the approving kinds (Approve, Start, Finish) the Cheer bed, Disapprove the Angry bed, TakeCover and
    /// the reserved kinds (Lull, PurifiedTeam) none. The bed kinds are 1 Cheer and 2 Angry, 0 for none; the reaction kinds are the documented numbers of
    /// <see cref="CrowdReactionKind"/>.
    /// </summary>
    internal static class SpectatorReactionBedKind
    {
        internal const int None = 0, Cheer = 1, Angry = 2, Count = 2;

        /// <summary>The bed a reaction of <paramref name="reactionKind"/> plays: <see cref="Cheer"/>, <see cref="Angry"/> or <see cref="None"/> (also for a kind out of range).</summary>
        internal static int For(int reactionKind)
        {
            switch (reactionKind)
            {
                case CrowdReactionKind.Approve: case CrowdReactionKind.Start: case CrowdReactionKind.Finish: return Cheer;
                case CrowdReactionKind.Disapprove: return Angry;
                default: return None;
            }
        }

        /// <summary>"cheer", "angry"; "none" for anything else (the word the manifest's <c>kind</c> and the log lines use).</summary>
        internal static string Name(int bedKind) => bedKind == Cheer ? "cheer" : bedKind == Angry ? "angry" : "none";

        /// <summary>The Arena bank: "Cheer" or "Angry"; null for none.</summary>
        internal static string BankName(int bedKind) => bedKind == Cheer ? "Cheer" : bedKind == Angry ? "Angry" : null;

        /// <summary>Arena's trigger id of the bank: 1119 (Cheer), 1121 (Angry); 0 for none.</summary>
        internal static int TriggerOf(int bedKind) => bedKind == Cheer ? 1119 : bedKind == Angry ? 1121 : 0;

        /// <summary>The bed kind of the manifest's word; <see cref="None"/> for anything else.</summary>
        internal static int Parse(string name) => name == "cheer" ? Cheer : name == "angry" ? Angry : None;
    }

    /// <summary>
    /// The Unity-free rules of the roar layer (docs/progress/spectator-audio.md "Roar beds"; SpectatorReactionBeds.cs is the only Unity file). Arena plays these beds on its
    /// FansAmbient emitters on the Cheer and Angry triggers; the emitters are inactive in the imported scenes, so the port plays the bed of a reaction from each stand of the murmur bed
    /// instead (SpectatorCrowdBedModel.Plan) for the length of the reaction.
    ///
    /// Strength and length: the amplitude is <c>0.35 + 0.65 x strength</c> and the hold <c>clamp(duration, 1.5, 5.0)</c> seconds. The volume law is the murmur bed's: the clip floats carry
    /// <see cref="SpectatorCrowdBedModel.ClipGain"/> (<see cref="SpectatorCrowdBedModel.ToClipFloats"/>) and a source's volume is <c>key x gain / ClipGain</c>, so at key 1 and gain 1 the roar
    /// plays at its exported level.
    /// </summary>
    internal static class SpectatorReactionBedModel
    {
        internal const bool ReactionBedsDefault = true;
        internal const float ReactionBedVolumeDefault = 2f; // the top of the key range 0..MaxVolume (2); 0.5.0 shipped 1f
        internal const float AmplitudeFloor = 0.35f, AmplitudeSpan = 0.65f;
        internal const float HoldMinSeconds = 1.5f, HoldMaxSeconds = 5f;

        /// <summary>0.35 + 0.65 x strength, the strength held to 0..1 (NaN counts as 0, so the result is the floor).</summary>
        internal static float Amplitude(float strength01) => AmplitudeFloor + AmplitudeSpan * CrowdExcitement.Sanitize(strength01);

        /// <summary>How long a roar holds its amplitude: the reaction's length held to 1.5 to 5.0 s (NaN counts as the shortest).</summary>
        internal static float HoldSeconds(double reactionDurationSeconds)
        {
            if (double.IsNaN(reactionDurationSeconds)) return HoldMinSeconds;
            return (float)Math.Max(HoldMinSeconds, Math.Min(HoldMaxSeconds, reactionDurationSeconds));
        }

        /// <summary>AudioSource.volume of a roar: the configured volume (0..2) times the envelope gain (0..1) over the clip's fixed gain; never above 1.</summary>
        internal static float SourceVolume(float volumeKey, float gain)
        {
            gain = float.IsNaN(gain) ? 0f : Math.Max(0f, Math.Min(1f, gain));
            return Math.Min(1f, SpectatorCrowdBedModel.ClampVolume(volumeKey) * gain / SpectatorCrowdBedModel.ClipGain);
        }
    }

    /// <summary>The clip table of a validated <see cref="SpectatorReactionBedSet"/>, one list per bed kind (manifest order, which is name order).</summary>
    internal sealed class RoarClipTable
    {
        readonly SpectatorReactionBedClip[][] _clips = new SpectatorReactionBedClip[SpectatorReactionBedKind.Count + 1][];

        internal RoarClipTable(SpectatorReactionBedSet set)
        {
            for (int k = 0; k < _clips.Length; k++) _clips[k] = new SpectatorReactionBedClip[0];
            if (set == null || set.banks == null) return;
            foreach (var bank in set.banks)
            {
                int kind = SpectatorReactionBedKind.Parse(bank?.kind);
                if (kind != SpectatorReactionBedKind.None && bank.clips != null) _clips[kind] = (SpectatorReactionBedClip[])bank.clips.Clone();
            }
        }

        /// <summary>The clips of a bed kind; empty for none or a kind the set does not have.</summary>
        internal SpectatorReactionBedClip[] Clips(int bedKind) => bedKind >= 1 && bedKind < _clips.Length ? _clips[bedKind] : _clips[0];

        internal int Count(int bedKind) => Clips(bedKind).Length;

        /// <summary>True when the set has at least one clip for a bed kind.</summary>
        internal bool Has(int bedKind) => Count(bedKind) > 0;
    }

    /// <summary>
    /// The clip and the per-stand start offsets of a roar. One clip per trigger, never the one played last for that kind (a kind with a single clip repeats it), the same clip on every
    /// stand of that reaction; each stand starts at its own random frame of the clip, so the stands are out of phase. Seeded <see cref="Random"/>: the same seed gives the same sequence.
    /// </summary>
    internal sealed class RoarClipPicker
    {
        readonly RoarClipTable _table;
        readonly Random _random;
        readonly int[] _last = new int[SpectatorReactionBedKind.Count + 1];

        internal RoarClipPicker(RoarClipTable table, int seed)
        {
            _table = table;
            _random = new Random(seed);
            for (int k = 0; k < _last.Length; k++) _last[k] = -1;
        }

        /// <summary>The index into <see cref="RoarClipTable.Clips"/> to play for <paramref name="bedKind"/>; -1 when the kind has no clip.</summary>
        internal int Pick(int bedKind)
        {
            int n = _table.Count(bedKind);
            if (n == 0) return -1;
            int last = bedKind >= 1 && bedKind < _last.Length ? _last[bedKind] : -1;
            int pick = n == 1 || last < 0 || last >= n ? _random.Next(n) : _random.Next(n - 1);
            if (n > 1 && last >= 0 && last < n && pick >= last) pick++;
            if (bedKind >= 1 && bedKind < _last.Length) _last[bedKind] = pick;
            return pick;
        }

        /// <summary>A random start frame, 0 to <paramref name="frames"/> - 1 (0 for a clip without frames).</summary>
        internal int StartOffset(int frames) => frames <= 1 ? 0 : _random.Next(frames);
    }

    /// <summary>The phases of a <see cref="RoarEnvelope"/>.</summary>
    internal enum RoarPhase { Idle, Attack, Hold, Release }

    /// <summary>
    /// The gain of one stand's roar of one kind: Idle, then Attack (linear, <see cref="AttackSeconds"/>, from the current gain to the amplitude), Hold (to the end of the reaction's hold),
    /// Release (linear to 0 in <see cref="ReleaseSeconds"/>), Idle. A retrigger while it sounds keeps the larger amplitude of an attack or hold under way, moves the hold end to the later of the
    /// two and restarts the attack from the current gain (no jump); after the release began the new amplitude counts as it is. <see cref="Cut"/> is the quick release (<see cref="CutSeconds"/>)
    /// for the other kind taking over. The gain is a function of the clock, not of the step size, so the shape is the same at any frame rate. No allocation; no Unity type; one thread.
    /// </summary>
    internal sealed class RoarEnvelope
    {
        internal const float AttackSeconds = 0.3f, ReleaseSeconds = 2f, CutSeconds = 0.5f, MaxHoldSeconds = 60f;
        RoarPhase _phase;
        float _gain, _reported, _amplitude, _fromGain, _releaseFrom, _releaseSeconds;
        double _segmentStart, _attackEnd, _holdEnd, _releaseEnd, _clock;

        internal RoarPhase Phase => _phase;
        /// <summary>The gain at the last <see cref="Step"/>, <see cref="Trigger"/> or <see cref="Cut"/>; 0..1.</summary>
        internal float Gain => _gain;
        /// <summary>The amplitude the roar rises or holds at (0 while idle).</summary>
        internal float Amplitude => _amplitude;
        /// <summary>True from a trigger until the release has ended.</summary>
        internal bool Active => _phase != RoarPhase.Idle;
        /// <summary>The clock time at which the hold ends (the release starts); meaningful while Attack or Hold.</summary>
        internal double HoldEnd => _holdEnd;

        /// <summary>Starts or retriggers the roar at clock time <paramref name="now"/> (seconds, never moving back). A non-positive amplitude does nothing.</summary>
        internal void Trigger(float amplitude, float holdSeconds, double now)
        {
            Advance(now);
            amplitude = CrowdExcitement.Sanitize(amplitude);
            if (!(amplitude > 0f)) return;
            holdSeconds = float.IsNaN(holdSeconds) ? 0f : Math.Max(0f, Math.Min(MaxHoldSeconds, holdSeconds));
            bool playing = _phase == RoarPhase.Attack || _phase == RoarPhase.Hold;
            double holdEnd = _clock + AttackSeconds + holdSeconds;
            if (playing)
            {
                amplitude = Math.Max(amplitude, _amplitude);
                holdEnd = Math.Max(holdEnd, _holdEnd);
            }
            _amplitude = amplitude;
            _holdEnd = holdEnd;
            _fromGain = _gain;
            _segmentStart = _clock;
            _attackEnd = _clock + AttackSeconds;
            _phase = RoarPhase.Attack;
        }

        /// <summary>Releases quickly (<see cref="CutSeconds"/>) from the current gain; a release that is already shorter than that stays as it is. Nothing when idle.</summary>
        internal void Cut(double now)
        {
            Advance(now);
            if (_phase == RoarPhase.Idle) return;
            if (_phase == RoarPhase.Release && _releaseEnd - _clock <= CutSeconds) return;
            BeginRelease(_clock, _gain, CutSeconds);
        }

        /// <summary>Returns to idle at once, without a release (the raid left <c>Started</c>, an audio reset).</summary>
        internal void Stop()
        {
            _phase = RoarPhase.Idle;
            _gain = 0f; _reported = 0f; _amplitude = 0f;
        }

        /// <summary>The gain at clock time <paramref name="now"/>; <paramref name="changed"/> is true when it differs from the value the previous Step returned.</summary>
        internal float Step(double now, out bool changed)
        {
            Advance(now);
            changed = _gain != _reported;
            _reported = _gain;
            return _gain;
        }

        void BeginRelease(double from, float gain, float seconds)
        {
            _phase = RoarPhase.Release;
            _releaseFrom = gain;
            _releaseSeconds = seconds;
            _segmentStart = from;
            _releaseEnd = from + seconds;
        }

        void Advance(double now)
        {
            if (double.IsNaN(now) || now < _clock) now = _clock;
            _clock = now;
            while (true)
            {
                switch (_phase)
                {
                    case RoarPhase.Attack:
                        if (now >= _attackEnd) { _gain = _amplitude; _phase = RoarPhase.Hold; continue; }
                        _gain = _fromGain + (_amplitude - _fromGain) * (float)((now - _segmentStart) / (_attackEnd - _segmentStart));
                        return;
                    case RoarPhase.Hold:
                        if (now >= _holdEnd) { _gain = _amplitude; BeginRelease(Math.Max(_holdEnd, _attackEnd), _amplitude, ReleaseSeconds); continue; }
                        _gain = _amplitude;
                        return;
                    case RoarPhase.Release:
                        if (now >= _releaseEnd) { _phase = RoarPhase.Idle; _gain = 0f; _amplitude = 0f; return; }
                        _gain = _releaseFrom * (float)(1.0 - (now - _segmentStart) / _releaseSeconds);
                        return;
                    default:
                        _gain = 0f;
                        return;
                }
            }
        }
    }
}

namespace ArenaMapPort.Client
{
    /// <summary>The once-only re-seed of the reaction bed defaults (<c>Spectators/ReactionBedDefaultsRevision</c>). Revision 1 (2026-11-07): the volume default 1 (0.5.0) became 2; exactly a saved 1 moves, a value tuned away from it stays.</summary>
    internal static class ReactionBedDefaults
    {
        internal const int Revision = 1;
        internal const float OldVolumeDefault = 1f;

        internal static bool VolumeMoves(int savedRevision, float value) => savedRevision < Revision && value == OldVolumeDefault;
    }
}
