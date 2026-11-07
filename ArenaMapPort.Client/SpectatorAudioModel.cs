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
        /// <summary>The reaction voices (CrowdReactionModel.cs, docs/PLAN-CROWD-REACTIONS.md): the spectators' answers to a kill, a death, a round start or end and a shot at the stands.
        /// Null in a package without one, which is still a valid schema-1 manifest.</summary>
        public SpectatorReactionSet reactions;
        /// <summary>The roar beds (SpectatorReactionBedModel.cs): Arena's Cheer and Angry crowd beds that a reaction plays from the stands. Null in a package without them, which is
        /// still a valid schema-1 manifest.</summary>
        public SpectatorReactionBedSet reaction_beds;

        /// <summary>Checks the roar beds of a package that has them; independent of <see cref="Validate"/>, <see cref="ValidateBed"/> and <see cref="ValidateReactions"/> in every
        /// direction, so a damaged roar set takes none of the others with it and none of them takes the roar beds.</summary>
        public void ValidateReactionBeds()
        {
            if (schema != 1 || trigger != 1123 || bank != "Participating" || reaction_beds == null)
                throw new InvalidDataException("unsupported spectator audio package");
            reaction_beds.Validate();
        }

        /// <summary>Checks the reaction set of a package that has one; independent of <see cref="Validate"/> and <see cref="ValidateBed"/> in both directions, so a damaged
        /// reaction set never takes the calls or the bed with it and a damaged call set never takes the reactions with it.</summary>
        public void ValidateReactions()
        {
            if (schema != 1 || trigger != 1123 || bank != "Participating" || reactions == null)
                throw new InvalidDataException("unsupported spectator audio package");
            reactions.Validate();
        }

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

    /// <summary>
    /// The optional <c>reactions</c> object of spectator-audio.json (tools/extract/extract_spectator_audio.py, phase A of docs/PLAN-CROWD-REACTIONS.md): Arena's own
    /// reaction clips of the six fan banks Cheer, Owned, Angry, ChangedRound, EndRound and Hide, in the format of the calls, one table of clips (a name shared by Cheer and Owned
    /// appears once) with the pool tag and the sentence group of each, and per bank the exported names. Fields the client does not use (the left-out clips, the clip statistics, the
    /// processing record) are ignored. A manifest without it is a valid schema-1 manifest and simply has no reactions.
    /// </summary>
    internal sealed class SpectatorReactionSet
    {
        public const int ExpectedMaxFrames = 308700, MinClipFrames = 22050, MaxClips = 256;
        public int schema, max_frames;
        public string source_bundle;
        public SpectatorReactionBank[] banks;
        public SpectatorReactionClip[] clips;

        public void Validate()
        {
            if (schema != 1 || max_frames != ExpectedMaxFrames || banks == null || clips == null || clips.Length == 0 || clips.Length > MaxClips || banks.Length == 0 ||
                banks.Length > SpectatorReactionTags.BankNames.Length)
                throw new InvalidDataException("invalid spectator reaction set");
            var byName = new Dictionary<string, SpectatorReactionClip>(StringComparer.Ordinal);
            foreach (var c in clips)
            {
                if (c == null || c.name == null || !Regex.IsMatch(c.name, SpectatorReactionTags.NamePattern) || byName.ContainsKey(c.name) || c.file != c.name + ".wav" ||
                    c.channels != 1 || c.sample_rate != 44100 || c.frames < MinClipFrames || c.frames > max_frames || c.bytes != 44 + c.frames * 2 ||
                    c.sha256 == null || !Regex.IsMatch(c.sha256, @"^[0-9a-f]{64}$") || c.pool == null || c.group == null)
                    throw new InvalidDataException("invalid spectator reaction clip");
                string prefix = SpectatorReactionTags.GroupPrefix(c.pool);
                if (prefix == null || !Regex.IsMatch(c.group, "^" + prefix + @"-\d{2}$"))
                    throw new InvalidDataException("invalid spectator reaction clip");
                byName.Add(c.name, c);
            }
            var listed = new HashSet<string>(StringComparer.Ordinal);
            var seenBanks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in banks)
            {
                if (b == null || b.bank == null || !seenBanks.Add(b.bank) || SpectatorReactionTags.TriggerOf(b.bank) != b.trigger || b.clips == null ||
                    b.embedded < 0 || b.clips.Length > b.embedded)
                    throw new InvalidDataException("invalid spectator reaction bank");
                var inBank = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in b.clips)
                    if (name == null || !inBank.Add(name) || !byName.TryGetValue(name, out var clip) || !SpectatorReactionTags.BankAllows(b.bank, clip.pool))
                        throw new InvalidDataException("invalid spectator reaction bank");
                listed.UnionWith(inBank);
            }
            if (listed.Count != byName.Count)
                throw new InvalidDataException("spectator reaction clip without a bank");
        }
    }

    internal sealed class SpectatorReactionBank
    {
        public string bank;
        public int trigger, embedded;
        public string[] clips;
    }

    internal sealed class SpectatorReactionClip
    {
        public string name, file, sha256, pool, group;
        public int channels, sample_rate, frames, bytes;
    }

    /// <summary>
    /// The fixed vocabulary of the reaction set: the six banks of phase A with their Arena trigger ids and the pool tags a clip of that bank may carry, the pool tag to
    /// sentence-group prefix table (a group id is <c>&lt;prefix&gt;-NN</c>, one prefix per kind of reaction) and the clip name pattern of the phase-A categories. Names and tags
    /// only: no transcript of any clip is stored anywhere. Lull and PurifiedTeam are not exported in phase A; their banks join this table when they do.
    /// </summary>
    internal static class SpectatorReactionTags
    {
        internal const string NamePattern = @"^arena_fan\d{1,2}_(?:kill_cheer|kill_angry|match_start|match_end|aim_hide|shoot_hide)_\d{2}$";
        internal static readonly string[] BankNames = { "Cheer", "Owned", "Angry", "ChangedRound", "EndRound", "Hide" };

        internal static int TriggerOf(string bank)
        {
            switch (bank)
            {
                case "Cheer": return 1119;
                case "Owned": return 1120;
                case "Angry": return 1121;
                case "Hide": return 1124;
                case "ChangedRound": return 1125;
                case "EndRound": return 1126;
                default: return 0;
            }
        }

        /// <summary>The sentence-group prefix of a pool tag; null for a tag that does not exist.</summary>
        internal static string GroupPrefix(string tag)
        {
            switch (tag)
            {
                case "praise": case "gloat": case "shout": case "urge": return "approve";
                case "lament": case "heckle": return "disapprove";
                case "start": return "start";
                case "finish": return "finish";
                case "panic": case "directed": return "takecover";
                default: return null;
            }
        }

        /// <summary>A clip of Cheer or Owned is praise, gloat, shout or urge; of Angry lament or heckle; of ChangedRound start; of EndRound finish; of Hide panic or directed.</summary>
        internal static bool BankAllows(string bank, string tag)
        {
            string prefix = GroupPrefix(tag);
            if (prefix == null) return false;
            switch (bank)
            {
                case "Cheer": case "Owned": return prefix == "approve";
                case "Angry": return prefix == "disapprove";
                case "ChangedRound": return prefix == "start";
                case "EndRound": return prefix == "finish";
                case "Hide": return prefix == "takecover";
                default: return false;
            }
        }
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
        /// <summary>Keeps the next random call from starting before <paramref name="now"/> + <paramref name="seconds"/>: a reaction plays, and the passivity jibes of the calls
        /// would contradict a fresh cheer. Only ever postpones (a later NextAt stays), does nothing while stopped, and ignores a non-finite time. Calls that
        /// already play finish by themselves.</summary>
        public void Hold(double now, double seconds)
        {
            if (!_running || double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
            NextAt = Math.Max(NextAt, now + seconds);
        }
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
