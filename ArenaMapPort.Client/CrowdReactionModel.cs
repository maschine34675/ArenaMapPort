#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The kinds of reaction a mode plugin can ask the crowd for (docs/PLAN-CROWD-REACTIONS.md 4.1) and the table that says which Arena bank answers each. The kinds are
    /// documented numbers, not public constants (the contract test forbids those on <see cref="SpectatorCrowd"/>); 1 to 31 is the whole range because a kind is also a bit of
    /// the mask <see cref="SpectatorCrowd.ReactionKinds"/>, and C# masks a shift count to five bits, so a kind is range-checked before it is ever shifted.
    /// </summary>
    internal static class CrowdReactionKind
    {
        internal const int Approve = 1, Disapprove = 2, TakeCover = 3, Start = 4, Finish = 5, Lull = 6, PurifiedTeam = 7, MaxKind = 31;
        /// <summary>The kinds phase A implements (Lull and PurifiedTeam are reserved): bits 1 to 5 = 62.</summary>
        internal const int ImplementedMask = (1 << Approve) | (1 << Disapprove) | (1 << TakeCover) | (1 << Start) | (1 << Finish);

        static readonly string[] CheerAndOwned = { "Cheer", "Owned" }, Angry = { "Angry" }, Hide = { "Hide" }, ChangedRound = { "ChangedRound" }, EndRound = { "EndRound" }, NoBank = new string[0];

        internal static string Name(int kind)
        {
            switch (kind)
            {
                case Approve: return "Approve";
                case Disapprove: return "Disapprove";
                case TakeCover: return "TakeCover";
                case Start: return "Start";
                case Finish: return "Finish";
                case Lull: return "Lull";
                case PurifiedTeam: return "PurifiedTeam";
                default: return "kind" + kind.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        internal static bool InRange(int kind) => kind >= 1 && kind <= MaxKind;

        /// <summary>1 &lt;&lt; kind inside 1 to 31, otherwise 0; the range is checked before the shift.</summary>
        internal static int Bit(int kind) => InRange(kind) ? 1 << kind : 0;

        /// <summary>The Arena banks whose clips make the pool of a kind (shared array, do not modify): Approve = Cheer and Owned as one pool, Disapprove = Angry, TakeCover = Hide,
        /// Start = ChangedRound, Finish = EndRound; none for a reserved or unknown kind. Pinned by a test.</summary>
        internal static string[] Banks(int kind)
        {
            switch (kind)
            {
                case Approve: return CheerAndOwned;
                case Disapprove: return Angry;
                case TakeCover: return Hide;
                case Start: return ChangedRound;
                case Finish: return EndRound;
                default: return NoBank;
            }
        }

        /// <summary>The sentence-group prefix of a kind's clips (<c>approve-03</c>); null for a kind without a pool.</summary>
        internal static string GroupPrefix(int kind)
        {
            switch (kind)
            {
                case Approve: return "approve";
                case Disapprove: return "disapprove";
                case TakeCover: return "takecover";
                case Start: return "start";
                case Finish: return "finish";
                default: return null;
            }
        }

        /// <summary>The kind whose clips carry a sentence-group prefix; 0 for an unknown prefix.</summary>
        internal static int OfGroupPrefix(string prefix)
        {
            switch (prefix)
            {
                case "approve": return Approve;
                case "disapprove": return Disapprove;
                case "takecover": return TakeCover;
                case "start": return Start;
                case "finish": return Finish;
                default: return 0;
            }
        }
    }

    /// <summary>One accepted request, as the queue hands it to the main thread. <see cref="Stamp"/> is a <see cref="Stopwatch"/> timestamp taken in <c>React</c>.</summary>
    internal struct CrowdReactionRequest
    {
        public int Kind;
        public float Strength;
        /// <summary>TakeCover only: the line of fire points at a stand, so the directed pleas may join the panic clips. The public contract never sets it; the plugin's own shot
        /// sensor and the developer demo do.</summary>
        public bool Directed;
        public long Stamp;
    }

    /// <summary>
    /// The state behind the four reaction members of <see cref="SpectatorCrowd"/> (docs/PLAN-CROWD-REACTIONS.md 4.3): who claimed the reactions of the current raid and the
    /// requests that wait for the main thread. Unity-free and thread-safe by one lock; <see cref="React"/> is that lock plus a fixed ring of <see cref="Capacity"/> slots, takes no
    /// Unity API, allocates nothing and never throws. The Unity side only DRAINS (<see cref="Drain"/>): it drops what is older than <see cref="MaxAgeSeconds"/> and hands the rest to
    /// <see cref="CrowdReactionPlanner"/>. Nothing here logs; the counters are what a Debug line reads.
    ///
    /// Claim: the owner name is cleaned like the score monitors' (null or blank = refused). A claim is refused while <see cref="Kinds"/> is 0 (no crowd, no reaction set, switched
    /// off). The same owner gets the same handle back; a holder named <see cref="DemoOwner"/> (the developer demo) yields to any other owner, whose claim voids the demo's handle;
    /// any other holder refuses a different owner, and a demo claim never takes the reactions from a plugin. Handles come from one process-wide counter, so a handle that was released,
    /// yielded, voided by <see cref="Reset"/> or issued by another queue can never match a later claim; they are never 0 or negative.
    /// </summary>
    internal sealed class CrowdReactionQueue
    {
        /// <summary>Slots of the ring; a full ring evicts the OLDEST request and counts it.</summary>
        internal const int Capacity = 16;
        /// <summary>A request older than this when it is drained is dropped (a reaction that plays late is wrong).</summary>
        internal const double MaxAgeSeconds = 2.0;
        /// <summary>Below this strength <see cref="React"/> returns false.</summary>
        internal const float MinStrength = 0.05f;
        internal const string DemoOwner = "developer-demo";
        const int OwnerMax = 64;

        static int s_handles;     // the process-wide handle counter

        readonly object _lock = new object();
        readonly CrowdReactionRequest[] _ring = new CrowdReactionRequest[Capacity];
        int _head, _count;
        int _holder;
        string _owner;
        int _generation;
        volatile int _kinds;
        volatile int _ringWaiting;     // _count mirrored for the main thread's idle check (HasWaiting): written under the lock, read without it
        long _accepted, _refused, _overflowed, _stale;

        /// <summary>The kinds this raid's crowd implements, as a mask of <c>1 &lt;&lt; kind</c>; 0 when there is no crowd, no reaction set or reactions are switched off.</summary>
        internal int Kinds => _kinds;

        /// <summary>Unity side: the implemented kinds of the crowd that was just built (0 = none). Bit 0 is not a kind and is cleared; 0 also empties the ring.</summary>
        internal void SetKinds(int mask)
        {
            lock (_lock)
            {
                _kinds = mask & ~1;
                if (_kinds == 0) { _head = 0; _count = 0; _ringWaiting = 0; }
            }
        }

        /// <summary>Requests wait in the ring. A volatile read without the lock: the main thread asks it every frame before it drains, so an idle frame costs a field read (it may lag a
        /// <see cref="React"/> on another thread by a frame, which the next frame's read catches).</summary>
        internal bool HasWaiting => _ringWaiting != 0;

        /// <summary>Counts how many times the crowd was removed (every <see cref="Reset"/>); the per-raid generation of the handles.</summary>
        internal int Generation { get { lock (_lock) return _generation; } }

        /// <summary>The owner of the current claim; null when nobody holds the reactions.</summary>
        internal string HolderOwner { get { lock (_lock) return _owner; } }

        /// <summary>A claim is held by someone other than the developer demo (the plugin's own sensors stand down then).</summary>
        internal bool ExternalClaimed { get { lock (_lock) return _holder != 0 && !string.Equals(_owner, DemoOwner, StringComparison.Ordinal); } }

        /// <summary>Requests waiting in the ring.</summary>
        internal int Count { get { lock (_lock) return _count; } }

        internal long Accepted { get { lock (_lock) return _accepted; } }
        /// <summary>Calls of <see cref="React"/> that returned false (stale or foreign handle, kind outside the mask, NaN, too weak).</summary>
        internal long Refused { get { lock (_lock) return _refused; } }
        /// <summary>Requests evicted from a full ring.</summary>
        internal long Overflowed { get { lock (_lock) return _overflowed; } }
        /// <summary>Requests dropped by <see cref="Drain"/> because they were older than <see cref="MaxAgeSeconds"/>.</summary>
        internal long Stale { get { lock (_lock) return _stale; } }

        internal static string CleanOwner(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner)) return null;
            var sb = new StringBuilder(Math.Min(owner.Length, OwnerMax));
            foreach (char c in owner.Trim())
            {
                if (sb.Length >= OwnerMax) break;
                sb.Append(c >= ' ' && c <= '~' ? c : '?');
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
        }

        /// <summary>Claims the reactions; a positive handle, or 0 when refused (see the class comment).</summary>
        internal int Claim(string owner)
        {
            string name = CleanOwner(owner);
            if (name == null) return 0;
            lock (_lock)
            {
                if (_kinds == 0) return 0;
                if (_holder != 0)
                {
                    if (string.Equals(_owner, name, StringComparison.Ordinal)) return _holder;
                    bool demoRequests = string.Equals(name, DemoOwner, StringComparison.Ordinal);
                    bool demoHolds = string.Equals(_owner, DemoOwner, StringComparison.Ordinal);
                    if (demoRequests || !demoHolds) return 0;      // a plugin takes over from the demo only
                }
                int handle = System.Threading.Interlocked.Increment(ref s_handles);
                if (handle <= 0) return 0;
                _holder = handle;
                _owner = name;
                return handle;
            }
        }

        /// <summary>Gives the reactions back; false (nothing changes) when the handle is not the current claim. Requests already accepted still play.</summary>
        internal bool Release(int handle)
        {
            lock (_lock)
            {
                if (handle <= 0 || handle != _holder) return false;
                _holder = 0;
                _owner = null;
                return true;
            }
        }

        /// <summary>Queues a reaction for the main thread; true = accepted into the ring (it may still be dropped by a cooldown later). False for a stale or foreign handle, a kind
        /// outside 1 to 31 or whose bit is not in <see cref="Kinds"/>, a NaN strength and a strength below <see cref="MinStrength"/>; a larger strength is clamped to 1.</summary>
        internal bool React(int handle, int kind, float strength01) => React(handle, kind, strength01, false, Stopwatch.GetTimestamp());

        /// <summary>The same with the line-of-fire flag and an explicit timestamp: for the developer demo and the tests.</summary>
        internal bool React(int handle, int kind, float strength01, bool directed, long stamp)
        {
            try
            {
                int bit = CrowdReactionKind.Bit(kind);
                lock (_lock)
                {
                    if (handle <= 0 || handle != _holder || bit == 0 || (_kinds & bit) == 0 || float.IsNaN(strength01) || strength01 < MinStrength)
                    {
                        _refused++;
                        return false;
                    }
                    if (strength01 > 1f) strength01 = 1f;
                    int slot;
                    if (_count < Capacity) slot = (_head + _count++) % Capacity;
                    else
                    {
                        slot = _head;
                        _head = (_head + 1) % Capacity;
                        _overflowed++;
                    }
                    _ring[slot] = new CrowdReactionRequest { Kind = kind, Strength = strength01, Directed = directed, Stamp = stamp };
                    _ringWaiting = _count;
                    _accepted++;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Main thread: moves the waiting requests, oldest first, into <paramref name="buffer"/> (what does not fit stays queued) and returns how many. A request older than
        /// <see cref="MaxAgeSeconds"/> at <paramref name="nowStamp"/> is dropped and counted as stale.</summary>
        internal int Drain(CrowdReactionRequest[] buffer, long nowStamp)
        {
            if (buffer == null || buffer.Length == 0) return 0;
            long maxAge = (long)(MaxAgeSeconds * Stopwatch.Frequency);
            lock (_lock)
            {
                int n = 0;
                while (_count > 0 && n < buffer.Length)
                {
                    CrowdReactionRequest r = _ring[_head];
                    _head = (_head + 1) % Capacity;
                    _count--;
                    if (nowStamp - r.Stamp > maxAge) { _stale++; continue; }
                    buffer[n++] = r;
                }
                _ringWaiting = _count;
                return n;
            }
        }

        /// <summary>Seconds between two <see cref="Stopwatch"/> stamps (0 when the second is not later).</summary>
        internal static double SecondsBetween(long from, long to) => to > from ? (to - from) / (double)Stopwatch.Frequency : 0.0;

        /// <summary>The crowd is removed (the raid ended): every handle is void, the ring is empty and <see cref="Kinds"/> is 0. The counters stay (they are cumulative).</summary>
        internal void Reset()
        {
            lock (_lock)
            {
                _generation++;
                _holder = 0;
                _owner = null;
                _head = 0;
                _count = 0;
                _ringWaiting = 0;
                _kinds = 0;
            }
        }
    }

    /// <summary>One clip of the reaction pool: where it sits in the manifest's clip table (the index the Unity side keeps its PCM under), what it is and how long it plays.</summary>
    internal sealed class ReactionClip
    {
        internal int Index, Kind, Family, Group;
        internal string Name, Tag;
        internal double Seconds;
    }

    /// <summary>
    /// The clips of a validated <see cref="SpectatorReactionSet"/> sorted into the pools the planner draws from (docs/PLAN-CROWD-REACTIONS.md 2.4 and 4.4): Approve = praise and gloat
    /// (the normal clips) and shout and urge (strong only), Disapprove = lament and heckle, TakeCover = panic and directed, Start, Finish. A clip with an unknown tag, no voice family or no
    /// sentence group is skipped (a validated set has none). The voice family is the <c>fanN</c> of the clip name: one recorded voice.
    /// </summary>
    internal sealed class CrowdReactionPool
    {
        static readonly ReactionClip[] NoClips = new ReactionClip[0];
        readonly ReactionClip[] _byIndex;
        readonly ReactionClip[] _approveNormal, _approveStrong, _lament, _lamentHeckle, _panic, _panicDirected, _start, _finish;
        readonly Dictionary<string, int> _tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly int _kindsMask;

        internal CrowdReactionPool(SpectatorReactionSet set)
        {
            if (set == null || set.clips == null) throw new ArgumentNullException(nameof(set));
            _byIndex = new ReactionClip[set.clips.Length];
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            var praise = new List<ReactionClip>(); var gloat = new List<ReactionClip>(); var shout = new List<ReactionClip>(); var urge = new List<ReactionClip>();
            var lament = new List<ReactionClip>(); var heckle = new List<ReactionClip>(); var panic = new List<ReactionClip>(); var directed = new List<ReactionClip>();
            var start = new List<ReactionClip>(); var finish = new List<ReactionClip>();
            for (int i = 0; i < set.clips.Length; i++)
            {
                var c = set.clips[i];
                if (c == null || c.name == null || c.group == null) continue;
                int family = FamilyOf(c.name);
                int kind = CrowdReactionKind.OfGroupPrefix(SpectatorReactionTags.GroupPrefix(c.pool));
                if (family < 0 || kind == 0) continue;
                if (!groups.TryGetValue(c.group, out int group)) groups[c.group] = group = groups.Count;
                var clip = new ReactionClip { Index = i, Kind = kind, Family = family, Group = group, Name = c.name, Tag = c.pool, Seconds = c.sample_rate > 0 ? c.frames / (double)c.sample_rate : 0.0 };
                _byIndex[i] = clip;
                _tagCounts[c.pool] = _tagCounts.TryGetValue(c.pool, out int n) ? n + 1 : 1;
                switch (c.pool)
                {
                    case "praise": praise.Add(clip); break;
                    case "gloat": gloat.Add(clip); break;
                    case "shout": shout.Add(clip); break;
                    case "urge": urge.Add(clip); break;
                    case "lament": lament.Add(clip); break;
                    case "heckle": heckle.Add(clip); break;
                    case "panic": panic.Add(clip); break;
                    case "directed": directed.Add(clip); break;
                    case "start": start.Add(clip); break;
                    case "finish": finish.Add(clip); break;
                }
            }
            _approveNormal = Join(praise, gloat); _approveStrong = Join(shout, urge);
            _lament = lament.ToArray(); _lamentHeckle = Join(lament, heckle);
            _panic = panic.ToArray(); _panicDirected = Join(panic, directed);
            _start = start.ToArray(); _finish = finish.ToArray();
            // A kind is implemented when the pool of its weakest band has a clip: strong-only, heckle-only and directed-only clips cannot carry a kind alone.
            if (_approveNormal.Length > 0) _kindsMask |= CrowdReactionKind.Bit(CrowdReactionKind.Approve);
            if (_lament.Length > 0) _kindsMask |= CrowdReactionKind.Bit(CrowdReactionKind.Disapprove);
            if (_panic.Length > 0) _kindsMask |= CrowdReactionKind.Bit(CrowdReactionKind.TakeCover);
            if (_start.Length > 0) _kindsMask |= CrowdReactionKind.Bit(CrowdReactionKind.Start);
            if (_finish.Length > 0) _kindsMask |= CrowdReactionKind.Bit(CrowdReactionKind.Finish);
        }

        static ReactionClip[] Join(List<ReactionClip> a, List<ReactionClip> b)
        {
            var all = new ReactionClip[a.Count + b.Count];
            a.CopyTo(all, 0); b.CopyTo(all, a.Count);
            return all;
        }

        /// <summary>The <c>fanN</c> number of a clip name (<c>arena_fan12_kill_cheer_03</c> = 12, the calls' <c>arena_fan7_participating_01</c> = 7); -1 when the name has none.</summary>
        internal static int FamilyOf(string name)
        {
            const string prefix = "arena_fan";
            if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal)) return -1;
            int i = prefix.Length, value = 0, digits = 0;
            while (i < name.Length && name[i] >= '0' && name[i] <= '9' && digits < 3) { value = value * 10 + (name[i] - '0'); i++; digits++; }
            return digits == 0 || i >= name.Length || name[i] != '_' ? -1 : value;
        }

        /// <summary>The kinds with a usable pool, as a mask of <c>1 &lt;&lt; kind</c>; 62 for the phase-A set, 0 for a set without usable clips. What the Unity side passes to
        /// <see cref="CrowdReactionQueue.SetKinds"/>.</summary>
        internal int KindsMask => _kindsMask;

        /// <summary>The slots of the manifest's clip table (skipped clips included).</summary>
        internal int Slots => _byIndex.Length;

        /// <summary>The clip at a slot of the manifest's clip table; null for a skipped slot or an index outside the table.</summary>
        internal ReactionClip At(int index) => index >= 0 && index < _byIndex.Length ? _byIndex[index] : null;

        /// <summary>How many usable clips carry a pool tag (0 for an unknown tag).</summary>
        internal int CountOf(string tag) => tag != null && _tagCounts.TryGetValue(tag, out int n) ? n : 0;

        /// <summary>The distinct voice families among the clips of a kind, ascending.</summary>
        internal int[] FamiliesOf(int kind)
        {
            var set = new SortedSet<int>();
            foreach (var c in _byIndex) if (c != null && c.Kind == kind) set.Add(c.Family);
            var all = new int[set.Count]; set.CopyTo(all);
            return all;
        }

        /// <summary>The number of distinct sentence groups among the clips of a kind.</summary>
        internal int GroupCountOf(int kind)
        {
            var set = new HashSet<int>();
            foreach (var c in _byIndex) if (c != null && c.Kind == kind) set.Add(c.Group);
            return set.Count;
        }

        /// <summary>The pools one voice of a reaction draws from (shared arrays, do not modify). <paramref name="primary"/> is the pool of the voice; <paramref name="fallback"/> (usually
        /// null) is what the voice falls back on when the primary pool cannot serve it at all. Approve below <see cref="CrowdReactionPlanner.StrongApprove"/> draws praise and gloat for
        /// every voice; from there the FIRST voice draws from shout and urge (falling back on the normal clips) and the others from the normal ones. Disapprove below
        /// <see cref="CrowdReactionPlanner.HeckleFrom"/> draws laments only, from there laments and heckles. TakeCover draws panic, plus directed when <paramref name="directed"/>.
        /// Start and Finish use their whole pool. Empty for a kind without a pool.</summary>
        internal void VoicePools(int kind, float strength, bool directed, int voice, out ReactionClip[] primary, out ReactionClip[] fallback)
        {
            fallback = null;
            switch (kind)
            {
                case CrowdReactionKind.Approve:
                    if (voice == 0 && strength >= CrowdReactionPlanner.StrongApprove && _approveStrong.Length > 0) { primary = _approveStrong; fallback = _approveNormal; }
                    else primary = _approveNormal;
                    return;
                case CrowdReactionKind.Disapprove: primary = strength >= CrowdReactionPlanner.HeckleFrom ? _lamentHeckle : _lament; return;
                case CrowdReactionKind.TakeCover: primary = directed ? _panicDirected : _panic; return;
                case CrowdReactionKind.Start: primary = _start; return;
                case CrowdReactionKind.Finish: primary = _finish; return;
                default: primary = NoClips; return;
            }
        }
    }

    /// <summary>One reaction the planner decided to play: which clips, in which order, how long after the start each voice begins and how long the random calls stay quiet.</summary>
    internal sealed class CrowdReactionPlan
    {
        internal int Kind;
        internal float Strength;
        internal bool Directed;
        /// <summary>Slots of the manifest's clip table (the Unity side's PCM index), one per voice, in start order.</summary>
        internal int[] Clips;
        /// <summary>Seconds after the reaction's start at which each voice begins: 0 for the first, then 0.12 to 0.45 s after the previous one.</summary>
        internal double[] Delays;
        /// <summary>Seconds each voice's clip plays.</summary>
        internal double[] Lengths;
        /// <summary>Seconds the random calls stay quiet after the reaction ends (<see cref="CrowdReactionPlanner.HoldGap"/>).</summary>
        internal double HoldGapSeconds;
        /// <summary>The voices the strength asked for (1 to 3) before the free slots, or too few different sentences in the pool for one reaction, cut them.</summary>
        internal int Requested;
        /// <summary>The relaxation step the clips were found at; 0 = every rule held. The steps, in this order: 1 drops the last-two-clips rule; then the window of recent sentences shrinks one
        /// sentence at a time down to none; then the voice-family rule drops; the last step lets a strong Approve's first voice take the normal clips. How many steps the window takes depends
        /// on the pool, so read <see cref="ClipsRelaxed"/>, <see cref="GroupWindow"/>, <see cref="FamiliesRelaxed"/> and <see cref="FellBack"/> instead of decoding the number. A sentence is
        /// never repeated inside one reaction at any step: the reaction gets fewer <see cref="Voices"/> instead.</summary>
        internal int Level;
        /// <summary>The kind's last two clips were allowed to come back.</summary>
        internal bool ClipsRelaxed;
        /// <summary>How many of the kind's most recent sentence groups this reaction avoids (<see cref="CrowdReactionPlanner.RecentGroups"/>, or fewer for a small pool, down to 0).</summary>
        internal int GroupWindow;
        /// <summary>The window shrank below <see cref="FullGroupWindow"/>: a recent sentence came back.</summary>
        internal bool GroupsRelaxed => GroupWindow < FullGroupWindow;
        /// <summary>The window the pool allows at full strictness (3, or one less than the pool's sentence groups when that is fewer).</summary>
        internal int FullGroupWindow;
        /// <summary>Two voices of one recorded voice (or a voice that sounds right now) were allowed.</summary>
        internal bool FamiliesRelaxed;
        /// <summary>A strong Approve took a normal clip for its first voice. This is the step after every other relaxation, and the shouts are never ruled out by then (only a sentence of
        /// the same reaction can still stand in the way), so it is a safety net: a shout is replaced only when no completion of the reaction fits next to any shout.</summary>
        internal bool FellBack;

        /// <summary>The relaxed rules in words for the Debug line ("last two clips, sentence window 1 of 3, voice families"); empty while every rule held.</summary>
        internal string RelaxedText
        {
            get
            {
                if (Level == 0) return "";
                var sb = new StringBuilder();
                if (ClipsRelaxed) sb.Append("last two clips");
                if (GroupsRelaxed) { if (sb.Length > 0) sb.Append(", "); sb.Append("sentence window ").Append(GroupWindow).Append(" of ").Append(FullGroupWindow); }
                if (FamiliesRelaxed) { if (sb.Length > 0) sb.Append(", "); sb.Append("voice families"); }
                if (FellBack) { if (sb.Length > 0) sb.Append(", "); sb.Append("normal clips"); }
                return sb.ToString();
            }
        }

        internal int Voices => Clips.Length;

        /// <summary>Seconds from the start until the first <paramref name="voices"/> voices have ended (the speaker helper may find fewer speakers than the plan has voices).</summary>
        internal double DurationOf(int voices)
        {
            double end = 0;
            for (int i = 0; i < voices && i < Clips.Length; i++) end = Math.Max(end, Delays[i] + Lengths[i]);
            return end;
        }

        internal double DurationSeconds => DurationOf(Clips.Length);

        /// <summary>The argument of <see cref="SpectatorAudioSchedule.Hold"/> for a reaction that starts now: its duration plus the gap.</summary>
        internal double HoldSeconds => DurationSeconds + HoldGapSeconds;
    }

    /// <summary>Why a request or a reaction did not play. The names are what the Debug line prints.</summary>
    internal static class CrowdReactionDropReason
    {
        internal const int Cooldown = 1, Rate = 2, Stale = 3, NoSlot = 4, NoSource = 5, NoListener = 6, Off = 7, NotStarted = 8, NoPool = 9, Coalesced = 10, Weak = 11, Count = 12;

        internal static string Name(int reason)
        {
            switch (reason)
            {
                case Cooldown: return "cooldown";
                case Rate: return "rate";
                case Stale: return "stale";
                case NoSlot: return "noslot";
                case NoSource: return "nosource";
                case NoListener: return "nolistener";
                case Off: return "off";
                case NotStarted: return "notstarted";
                case NoPool: return "nopool";
                case Coalesced: return "coalesced";
                case Weak: return "weak";
                default: return "none";
            }
        }
    }

    /// <summary>A dropped request for the Debug line. <see cref="Blocker"/> is what the request was waiting for when it went stale (<see cref="CrowdReactionDropReason.Rate"/> or
    /// <see cref="CrowdReactionDropReason.NoSlot"/>), 0 otherwise.</summary>
    internal struct CrowdReactionDrop
    {
        public int Kind;
        public float Strength;
        public int Reason, Blocker;
    }

    /// <summary>
    /// The main-thread rules of the reactions (docs/PLAN-CROWD-REACTIONS.md 4.4): what to do with the requests <see cref="CrowdReactionQueue.Drain"/> returned. Unity-free;
    /// deterministic for a seed; allocates only when a reaction is planned (the plan and its three small arrays) and nothing per frame while nothing waits.
    ///
    /// Use, once per frame on the main thread: <see cref="Offer"/> what the queue returned, then, while <see cref="TryPlan"/> gives a plan, start its voices and confirm with
    /// <see cref="Started"/> (or give it up with <see cref="Discard"/>); read the drops with <see cref="TryTakeDrop"/> for the Debug log. A plan the Unity side neither starts nor discards
    /// stays pending and is planned again on the next call (it expires on its own).
    ///
    /// Offer: within one batch the largest strength per kind is kept (and the line-of-fire flag of any), and a Start or Finish that is queued removes the Approve and Disapprove
    /// of the batch and the pending ones. A request whose kind was last started less than its gap ago is dropped (cooldown); a request for a kind that already waits is merged into
    /// it, and the merged request keeps the strength AND the deadline of the strongest of the two (on a tie the older one), so a weak latecomer neither stretches the life of an old strong
    /// request nor shortens that of a fresh strong one. Gaps: Approve and Disapprove 3 s, TakeCover 15 s, Start and Finish 5 s, Lull and PurifiedTeam 20 s; a strength of at least 0.8 halves the gap of THAT request (an important
    /// event is not swallowed by the cooldown of a small one); all multiplied by <see cref="GapScale"/> (0 = no cooldown). A kind outside 1 to 31 or without a pool in this raid's set
    /// is dropped as nopool, a strength that is NaN or below 0.05 as weak (the queue refuses both already; the planner does not trust its caller).
    ///
    /// TryPlan: requests that wait for a start older than 2 s since their React call are dropped as stale; at most one reaction starts per <see cref="RateSeconds"/> and only with a
    /// free reaction slot; the pending request of the highest priority goes first (Finish, Start, Approve = Disapprove in arrival order, TakeCover, Lull, PurifiedTeam). Its strength
    /// sets the voices (up to 1/3 = 1, up to 2/3 = 2, above = 3, cut to the free slots). The clips are drawn per voice from the pool of the kind and band
    /// (<see cref="CrowdReactionPool.VoicePools"/>): the voice family with a weight of the square root of its clip count, then a clip of that family at random. Rules within one
    /// reaction: no two voices of one family (voices that play right now, calls included, count as used) and no sentence twice (the 'group'); across reactions of a kind: none of its
    /// last two clips and none of its last three sentence groups (the window). When they cannot all hold for the voices the strength asks for, they are relaxed in this order, all the
    /// steps at the full voice count before a voice is given up: (1) the last-two-clips rule, (2) the sentence window, one sentence at a time (3, 2, 1, none), (3) the voice-family rule.
    /// A strong Approve's first voice goes through the same steps with the shouts alone and only after the last of them may take the normal clips. A sentence is NEVER repeated
    /// inside one reaction, whatever the step: only then does the reaction get fewer voices. At full strictness the window is at most one less than the pool's groups, so a small pool
    /// does not start out relaxed. The step a plan was found at is on the plan (<see cref="CrowdReactionPlan.Level"/> and the flags beside it).
    /// </summary>
    internal sealed class CrowdReactionPlanner
    {
        internal const float StrongApprove = 0.8f, HeckleFrom = 0.4f, StrongGap = 0.8f;
        internal const double RateSeconds = 0.75, MaxWaitSeconds = 2.0;
        internal const int MaxVoices = 3, RecentClips = 2, RecentGroups = 3, DropCapacity = 16;
        internal const double DelayMinSeconds = 0.12, DelaySpanSeconds = 0.33;
        internal const float MaxGapScale = 4f;

        // The rules in force at one relaxation step of a plan (CrowdReactionPlan.Level): Clips = the last two clips stay out, Window = how many of the kind's latest sentence groups stay
        // out, Families = no two voices of one family and none of a family that sounds right now, Fallback = a strong Approve's first voice may take the normal clips.
        struct Rules
        {
            public bool Clips, Families, Fallback;
            public int Window;
        }

        struct Waiting
        {
            public bool Active, Directed;
            public float Strength;
            public double Expires;
            public long Seq;
            public int Blocker;
        }

        readonly CrowdReactionPool _pool;
        readonly Random _random;
        readonly Waiting[] _waiting = new Waiting[CrowdReactionKind.MaxKind + 1];
        readonly double[] _lastStart = new double[CrowdReactionKind.MaxKind + 1];
        readonly int[][] _recentClips = new int[CrowdReactionKind.MaxKind + 1][], _recentGroups = new int[CrowdReactionKind.MaxKind + 1][];
        readonly int[] _nClips = new int[CrowdReactionKind.MaxKind + 1], _nGroups = new int[CrowdReactionKind.MaxKind + 1];
        // batch scratch of Offer
        readonly bool[] _bHas = new bool[CrowdReactionKind.MaxKind + 1], _bDirected = new bool[CrowdReactionKind.MaxKind + 1];
        readonly float[] _bStrength = new float[CrowdReactionKind.MaxKind + 1];
        readonly double[] _bExpires = new double[CrowdReactionKind.MaxKind + 1];
        readonly long[] _bSeq = new long[CrowdReactionKind.MaxKind + 1];
        // search scratch of TryPlan
        readonly ReactionClip[] _pick = new ReactionClip[MaxVoices];
        readonly List<ReactionClip>[] _cands = { new List<ReactionClip>(32), new List<ReactionClip>(32), new List<ReactionClip>(32) };
        readonly List<int> _famIds = new List<int>(16), _famCounts = new List<int>(16);
        readonly HashSet<int> _scratchGroups = new HashSet<int>();
        // drops
        readonly CrowdReactionDrop[] _drops = new CrowdReactionDrop[DropCapacity];
        readonly int[] _dropped = new int[CrowdReactionDropReason.Count];
        int _dropHead, _dropCount;
        double _lastAny = double.NegativeInfinity;
        int _waitingCount, _started, _startedVoices;
        long _nextSeq;
        float _gapScale = 1f;

        internal CrowdReactionPlanner(SpectatorReactionSet set, int seed) : this(new CrowdReactionPool(set), seed) { }

        internal CrowdReactionPlanner(CrowdReactionPool pool, int seed)
        {
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _random = new Random(seed);
            for (int k = 0; k < _lastStart.Length; k++)
            {
                _lastStart[k] = double.NegativeInfinity;
                _recentClips[k] = new int[RecentClips];
                _recentGroups[k] = new int[RecentGroups];
            }
        }

        internal CrowdReactionPool Pool => _pool;

        /// <summary>The kinds this raid's crowd implements (the pool's mask).</summary>
        internal int KindsMask => _pool.KindsMask;

        /// <summary>Multiplies every cooldown gap (developer key <c>ReactionGapScale</c>); 0 to <see cref="MaxGapScale"/>, NaN counts as 1. The global rate is not scaled.</summary>
        internal float GapScale
        {
            get { return _gapScale; }
            set { _gapScale = float.IsNaN(value) ? 1f : Math.Max(0f, Math.Min(MaxGapScale, value)); }
        }

        /// <summary>Requests that wait for a free moment.</summary>
        internal int Pending => _waitingCount;

        internal bool IsPending(int kind) => CrowdReactionKind.InRange(kind) && _waiting[kind].Active;

        /// <summary>Reactions confirmed with <see cref="Started"/> and the voices they had.</summary>
        internal int StartedCount => _started;
        internal int StartedVoices => _startedVoices;

        /// <summary>How many requests were dropped for a <see cref="CrowdReactionDropReason"/>.</summary>
        internal int DroppedCount(int reason) => reason > 0 && reason < _dropped.Length ? _dropped[reason] : 0;

        // ---- tables ----------------------------------------------------------------------------------------------------------------

        /// <summary>The base gap between two starts of a kind, in seconds.</summary>
        internal static double BaseGap(int kind)
        {
            switch (kind)
            {
                case CrowdReactionKind.Approve:
                case CrowdReactionKind.Disapprove: return 3.0;
                case CrowdReactionKind.TakeCover: return 15.0;
                case CrowdReactionKind.Start:
                case CrowdReactionKind.Finish: return 5.0;
                case CrowdReactionKind.Lull:
                case CrowdReactionKind.PurifiedTeam: return 20.0;
                default: return 5.0;
            }
        }

        /// <summary>The gap that applies to a request: the base gap, halved from a strength of 0.8, times <see cref="GapScale"/>.</summary>
        internal double Gap(int kind, float strength) => BaseGap(kind) * (strength >= StrongGap ? 0.5 : 1.0) * _gapScale;

        /// <summary>How long the random calls stay quiet after a reaction of a kind has ended: 4 s after Approve, Start and Finish (a passivity jibe would contradict the cheer), 1 s after
        /// the others. The one table the Unity side and the tests use.</summary>
        internal static double HoldGap(int kind)
        {
            switch (kind)
            {
                case CrowdReactionKind.Approve:
                case CrowdReactionKind.Start:
                case CrowdReactionKind.Finish: return 4.0;
                default: return 1.0;
            }
        }

        /// <summary>Voices from the strength: up to 1/3 one, up to 2/3 two, above that three (before the free slots cut them).</summary>
        internal static int VoicesFor(float strength) => strength <= 1f / 3f ? 1 : strength <= 2f / 3f ? 2 : 3;

        /// <summary>Lower goes first: Finish, Start, Approve = Disapprove, TakeCover, Lull, PurifiedTeam.</summary>
        internal static int Rank(int kind)
        {
            switch (kind)
            {
                case CrowdReactionKind.Finish: return 0;
                case CrowdReactionKind.Start: return 1;
                case CrowdReactionKind.Approve:
                case CrowdReactionKind.Disapprove: return 2;
                case CrowdReactionKind.TakeCover: return 3;
                case CrowdReactionKind.Lull: return 4;
                case CrowdReactionKind.PurifiedTeam: return 5;
                default: return 6;
            }
        }

        // ---- offer -----------------------------------------------------------------------------------------------------------------

        /// <summary>Takes what <see cref="CrowdReactionQueue.Drain"/> returned: coalesces the batch, applies the cooldowns and queues the survivors to wait for a start. <paramref name="now"/>
        /// is the Unity side's clock in seconds, <paramref name="nowStamp"/> the <see cref="Stopwatch"/> stamp that was passed to <c>Drain</c> (the age of a request is the difference of the
        /// stamps).</summary>
        internal void Offer(CrowdReactionRequest[] requests, int count, long nowStamp, double now)
        {
            if (requests == null || count <= 0) return;
            if (count > requests.Length) count = requests.Length;
            Array.Clear(_bHas, 0, _bHas.Length);
            for (int i = 0; i < count; i++)
            {
                CrowdReactionRequest r = requests[i];
                float strength = float.IsNaN(r.Strength) ? 0f : Math.Max(0f, Math.Min(1f, r.Strength));
                if (!CrowdReactionKind.InRange(r.Kind)) { Drop(r.Kind, strength, CrowdReactionDropReason.NoPool, 0); continue; }
                if (!(strength >= CrowdReactionQueue.MinStrength)) { Drop(r.Kind, strength, CrowdReactionDropReason.Weak, 0); continue; }
                double expires = now - CrowdReactionQueue.SecondsBetween(r.Stamp, nowStamp) + MaxWaitSeconds;
                int k = r.Kind;
                if (!_bHas[k])
                {
                    _bHas[k] = true; _bStrength[k] = strength; _bDirected[k] = r.Directed; _bExpires[k] = expires; _bSeq[k] = _nextSeq++;
                }
                else
                {
                    // The deadline belongs to the request whose strength is kept: a weak later request must not stretch an old strong one past its 2 s.
                    if (strength > _bStrength[k]) { _bStrength[k] = strength; _bExpires[k] = expires; }
                    _bDirected[k] |= r.Directed;
                }
            }
            // Start and Finish first: when one is queued the cheers and boos of the same moment are part of it and go.
            bool supersede = false;
            for (int k = CrowdReactionKind.Start; k <= CrowdReactionKind.Finish; k++)
                if (_bHas[k] && Admit(k, now)) supersede = true;
            if (supersede)
            {
                Supersede(CrowdReactionKind.Approve);
                Supersede(CrowdReactionKind.Disapprove);
            }
            for (int k = 1; k <= CrowdReactionKind.MaxKind; k++)
            {
                if (k == CrowdReactionKind.Start || k == CrowdReactionKind.Finish || !_bHas[k]) continue;
                Admit(k, now);
            }
        }

        // Moves one coalesced batch entry into the waiting list. True when it is waiting afterwards.
        bool Admit(int k, double now)
        {
            float strength = _bStrength[k];
            _bHas[k] = false;
            if ((_pool.KindsMask & CrowdReactionKind.Bit(k)) == 0) { Drop(k, strength, CrowdReactionDropReason.NoPool, 0); return false; }
            if (_waiting[k].Active)
            {
                // The kind already waits for its moment: one reaction answers both, with the strength of the stronger and the deadline of the request that brought it.
                if (strength > _waiting[k].Strength) { _waiting[k].Strength = strength; _waiting[k].Expires = _bExpires[k]; }
                _waiting[k].Directed |= _bDirected[k];
                return true;
            }
            double gap = Gap(k, strength);
            if (gap > 0 && now - _lastStart[k] < gap) { Drop(k, strength, CrowdReactionDropReason.Cooldown, 0); return false; }
            _waiting[k] = new Waiting { Active = true, Strength = strength, Directed = _bDirected[k], Expires = _bExpires[k], Seq = _bSeq[k] };
            _waitingCount++;
            return true;
        }

        void Supersede(int k)
        {
            if (_bHas[k]) { Drop(k, _bStrength[k], CrowdReactionDropReason.Coalesced, 0); _bHas[k] = false; }
            if (_waiting[k].Active) { Drop(k, _waiting[k].Strength, CrowdReactionDropReason.Coalesced, 0); Clear(k); }
        }

        void Clear(int k)
        {
            if (!_waiting[k].Active) return;
            _waiting[k].Active = false;
            _waitingCount--;
        }

        // ---- plan ------------------------------------------------------------------------------------------------------------------

        /// <summary>Asks for the next reaction to start. False while nothing waits, while the global rate holds, while no reaction slot is free (<paramref name="freeSlots"/>) and when a
        /// pending request could not be served. <paramref name="busyFamilies"/> are the voice families that sound right now (null = none); only called for when something waits.</summary>
        internal bool TryPlan(double now, int freeSlots, ICollection<int> busyFamilies, out CrowdReactionPlan plan)
        {
            plan = null;
            if (_waitingCount == 0) return false;
            for (int k = 1; k <= CrowdReactionKind.MaxKind; k++)
                if (_waiting[k].Active && now > _waiting[k].Expires)
                {
                    Drop(k, _waiting[k].Strength, CrowdReactionDropReason.Stale, _waiting[k].Blocker);
                    Clear(k);
                }
            if (_waitingCount == 0) return false;
            if (now - _lastAny < RateSeconds) { Block(CrowdReactionDropReason.Rate); return false; }
            if (freeSlots <= 0) { Block(CrowdReactionDropReason.NoSlot); return false; }
            int best = 0;
            for (int k = 1; k <= CrowdReactionKind.MaxKind; k++)
            {
                if (!_waiting[k].Active) continue;
                if (best == 0 || Rank(k) < Rank(best) || (Rank(k) == Rank(best) && _waiting[k].Seq < _waiting[best].Seq)) best = k;
            }
            plan = Assemble(best, _waiting[best].Strength, _waiting[best].Directed, Math.Min(MaxVoices, freeSlots), busyFamilies);
            if (plan == null)
            {
                Drop(best, _waiting[best].Strength, CrowdReactionDropReason.NoPool, 0);
                Clear(best);
                return false;
            }
            return true;
        }

        void Block(int reason)
        {
            for (int k = 1; k <= CrowdReactionKind.MaxKind; k++)
                if (_waiting[k].Active) _waiting[k].Blocker = reason;
        }

        /// <summary>The Unity side started all the voices of <paramref name="plan"/>: the kind's cooldown and the global rate run from <paramref name="now"/>, the clips and sentences enter the
        /// no-repeat history and the request stops waiting.</summary>
        internal void Started(CrowdReactionPlan plan, double now) => Started(plan, now, plan == null ? 0 : plan.Clips.Length);

        /// <summary>The same when only the first <paramref name="voices"/> voices of the plan could be started (the speaker helper found fewer separated speakers than the plan has voices):
        /// only the clips and sentences that really sound enter the no-repeat history and the voice count, so the sentences nobody heard stay available. Clamped to 1 up to the plan's voices.</summary>
        internal void Started(CrowdReactionPlan plan, double now, int voices)
        {
            if (plan == null) return;
            int k = plan.Kind;
            if (!CrowdReactionKind.InRange(k)) return;
            voices = Math.Max(Math.Min(1, plan.Clips.Length), Math.Min(voices, plan.Clips.Length));
            _lastStart[k] = now;
            _lastAny = now;
            Clear(k);
            for (int i = 0; i < voices; i++)
            {
                var clip = _pool.At(plan.Clips[i]);
                if (clip == null) continue;
                Push(_recentClips[k], ref _nClips[k], clip.Index);
                Push(_recentGroups[k], ref _nGroups[k], clip.Group);
            }
            _started++;
            _startedVoices += voices;
        }

        /// <summary>The Unity side could not start <paramref name="plan"/> (no source, no listener, the key went off): the request is dropped for <paramref name="reason"/> and no cooldown starts.</summary>
        internal void Discard(CrowdReactionPlan plan, int reason)
        {
            if (plan == null || !CrowdReactionKind.InRange(plan.Kind)) return;
            if (_waiting[plan.Kind].Active)
            {
                Drop(plan.Kind, plan.Strength, reason, 0);
                Clear(plan.Kind);
            }
        }

        /// <summary>Forgets everything but the gap scale: pending requests, cooldowns, the rate, the no-repeat history, the drops and the counters (the crowd was removed or the key went off).</summary>
        internal void Reset()
        {
            for (int k = 0; k < _waiting.Length; k++)
            {
                _waiting[k] = default(Waiting);
                _lastStart[k] = double.NegativeInfinity;
                _nClips[k] = 0; _nGroups[k] = 0;
            }
            _waitingCount = 0; _lastAny = double.NegativeInfinity; _nextSeq = 0;
            _dropHead = 0; _dropCount = 0; Array.Clear(_dropped, 0, _dropped.Length);
            _started = 0; _startedVoices = 0;
        }

        /// <summary>The oldest drop not read yet; false when there is none. Fixed ring of <see cref="DropCapacity"/> (an unread overflow loses the oldest).</summary>
        internal bool TryTakeDrop(out CrowdReactionDrop drop)
        {
            if (_dropCount == 0) { drop = default(CrowdReactionDrop); return false; }
            drop = _drops[_dropHead];
            _dropHead = (_dropHead + 1) % DropCapacity;
            _dropCount--;
            return true;
        }

        void Drop(int kind, float strength, int reason, int blocker)
        {
            if (reason > 0 && reason < _dropped.Length) _dropped[reason]++;
            int slot;
            if (_dropCount < DropCapacity) slot = (_dropHead + _dropCount++) % DropCapacity;
            else { slot = _dropHead; _dropHead = (_dropHead + 1) % DropCapacity; }
            _drops[slot] = new CrowdReactionDrop { Kind = kind, Strength = strength, Reason = reason, Blocker = blocker };
        }

        /// <summary>"started=3 (7 voices), dropped cooldown=1 stale=0 ..."</summary>
        internal string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("started=").Append(_started).Append(" (").Append(_startedVoices).Append(" voices), pending=").Append(_waitingCount).Append(", dropped");
            for (int r = 1; r < _dropped.Length; r++)
                if (_dropped[r] > 0) sb.Append(' ').Append(CrowdReactionDropReason.Name(r)).Append('=').Append(_dropped[r]);
            return sb.ToString();
        }

        // ---- clip choice -------------------------------------------------------------------------------------------------------------

        static void Push(int[] ring, ref int n, int value)
        {
            if (n < ring.Length) { ring[n++] = value; return; }
            for (int i = 1; i < ring.Length; i++) ring[i - 1] = ring[i];
            ring[ring.Length - 1] = value;
        }

        CrowdReactionPlan Assemble(int kind, float strength, bool directed, int slots, ICollection<int> busyFamilies)
        {
            int requested = VoicesFor(strength);
            int want = Math.Min(requested, slots);
            if (want < 1) return null;
            int window = GroupWindow(kind, strength, directed);
            _pool.VoicePools(kind, strength, directed, 0, out _, out var firstFallback);
            int levels = window + (firstFallback != null ? 4 : 3);
            // All the steps at the full voice count before a voice is given up: only the sentence groups of ONE reaction are never relaxed.
            for (int count = want; count >= 1; count--)
                for (int level = 0; level < levels; level++)
                {
                    var rules = RulesAt(level, window);
                    if (Search(kind, strength, directed, 0, count, rules, busyFamilies))
                        return Build(kind, strength, directed, count, requested, level, window, rules);
                }
            return null;
        }

        // The rules at relaxation step <level> for a full sentence window of <window>: 0 = every rule; 1 drops "none of the last two clips"; 2 to window + 1 shrink the window by one
        // sentence each, down to none; window + 2 also drops the voice-family rule; window + 3 lets a strong Approve's first voice take the normal clips. Sentences within one reaction are
        // not a rule of this table: they are never relaxed.
        static Rules RulesAt(int level, int window) => new Rules
        {
            Clips = level < 1,
            Window = level <= 1 ? window : Math.Max(0, window - (level - 1)),
            Families = level < window + 2,
            Fallback = level >= window + 3,
        };

        // The last three sentence groups may not repeat, but never more than one less than the groups the voices of this reaction can draw from, so a small pool does not start out relaxed.
        int GroupWindow(int kind, float strength, bool directed)
        {
            _scratchGroups.Clear();
            for (int voice = 0; voice < 2; voice++)
            {
                _pool.VoicePools(kind, strength, directed, voice, out var primary, out var fallback);
                foreach (var c in primary) _scratchGroups.Add(c.Group);
                if (fallback != null) foreach (var c in fallback) _scratchGroups.Add(c.Group);
            }
            return Math.Max(0, Math.Min(RecentGroups, _scratchGroups.Count - 1));
        }

        bool Search(int kind, float strength, bool directed, int depth, int count, Rules rules, ICollection<int> busyFamilies)
        {
            if (depth == count) return true;
            var cands = _cands[depth];
            cands.Clear();
            _pool.VoicePools(kind, strength, directed, depth, out var primary, out var fallback);
            Fill(cands, primary, kind, depth, rules, busyFamilies);
            bool fallbackLeft = fallback != null && rules.Fallback;
            while (true)
            {
                if (cands.Count == 0)
                {
                    if (!fallbackLeft) return false;
                    fallbackLeft = false;
                    Fill(cands, fallback, kind, depth, rules, busyFamilies);
                    if (cands.Count == 0) return false;
                }
                _pick[depth] = Draw(cands);
                if (Search(kind, strength, directed, depth + 1, count, rules, busyFamilies)) return true;
            }
        }

        void Fill(List<ReactionClip> into, ReactionClip[] from, int kind, int depth, Rules rules, ICollection<int> busyFamilies)
        {
            foreach (var c in from)
                if (Allowed(c, kind, depth, rules, busyFamilies)) into.Add(c);
        }

        // A sentence group is never repeated within the reaction, at any step; every other rule is the one the step in force says.
        bool Allowed(ReactionClip c, int kind, int depth, Rules rules, ICollection<int> busyFamilies)
        {
            for (int j = 0; j < depth; j++)
            {
                if (_pick[j].Group == c.Group) return false;
                if (rules.Families && _pick[j].Family == c.Family) return false;
            }
            if (rules.Window > 0)
            {
                int[] ring = _recentGroups[kind];
                for (int i = Math.Max(0, _nGroups[kind] - rules.Window); i < _nGroups[kind]; i++)
                    if (ring[i] == c.Group) return false;
            }
            if (rules.Clips)
            {
                int[] ring = _recentClips[kind];
                for (int i = 0; i < _nClips[kind]; i++)
                    if (ring[i] == c.Index) return false;
            }
            if (rules.Families && busyFamilies != null && busyFamilies.Contains(c.Family)) return false;
            return true;
        }

        /// <summary>Draws one clip out of <paramref name="cands"/> (and removes it): the voice family with a weight of the square root of its clip count in the candidates, then a clip of
        /// that family at random. A plain clip draw would give a family with one clip a share of 5 percent where this gives 20 to 25 and keep the family with half the clips from holding half the
        /// reactions.</summary>
        internal ReactionClip Draw(List<ReactionClip> cands)
        {
            _famIds.Clear(); _famCounts.Clear();
            for (int i = 0; i < cands.Count; i++)
            {
                int f = _famIds.IndexOf(cands[i].Family);
                if (f < 0) { _famIds.Add(cands[i].Family); _famCounts.Add(1); }
                else _famCounts[f]++;
            }
            double total = 0;
            for (int f = 0; f < _famCounts.Count; f++) total += Math.Sqrt(_famCounts[f]);
            double r = _random.NextDouble() * total;
            int pick = 0;
            while (pick < _famIds.Count - 1)
            {
                double w = Math.Sqrt(_famCounts[pick]);
                if (r < w) break;
                r -= w;
                pick++;
            }
            int family = _famIds[pick], nth = _random.Next(_famCounts[pick]);
            for (int i = 0; i < cands.Count; i++)
            {
                if (cands[i].Family != family || nth-- != 0) continue;
                var clip = cands[i];
                cands.RemoveAt(i);
                return clip;
            }
            var last = cands[cands.Count - 1];      // not reachable: the family has that many clips
            cands.RemoveAt(cands.Count - 1);
            return last;
        }

        CrowdReactionPlan Build(int kind, float strength, bool directed, int count, int requested, int level, int fullWindow, Rules rules)
        {
            var plan = new CrowdReactionPlan
            {
                Kind = kind, Strength = strength, Directed = directed, Requested = requested, HoldGapSeconds = HoldGap(kind),
                Level = level, ClipsRelaxed = !rules.Clips, GroupWindow = rules.Window, FullGroupWindow = fullWindow, FamiliesRelaxed = !rules.Families,
                // the last step is the one that allows the fallback; a plan found there used it (the step before it failed with the same rules)
                FellBack = rules.Fallback,
                Clips = new int[count], Delays = new double[count], Lengths = new double[count],
            };
            for (int i = 0; i < count; i++)
            {
                plan.Clips[i] = _pick[i].Index;
                plan.Lengths[i] = _pick[i].Seconds;
                plan.Delays[i] = i == 0 ? 0.0 : plan.Delays[i - 1] + DelayMinSeconds + _random.NextDouble() * DelaySpanSeconds;
            }
            return plan;
        }
    }

    /// <summary>
    /// Which stand a reaction voice comes from: the law of the random calls (<see cref="SpectatorAudioSchedule.ChooseSpeaker"/>, unchanged: the nearest section of the stands, never the
    /// speaker that spoke last) run on a SEPARATE schedule instance, so the calls' own no-repeat memory is not disturbed, and every further voice of a reaction must stand at least
    /// <see cref="MinSeparation"/> metres from the voices already picked. Positions are packed x, y, z triples so the model stays Unity-free.
    /// </summary>
    internal sealed class CrowdReactionSpeakers
    {
        internal const float MinSeparation = 4f;
        readonly SpectatorAudioSchedule _law;
        readonly HashSet<int> _excluded = new HashSet<int>();
        float[] _distances = new float[0];

        internal CrowdReactionSpeakers(int seed) { _law = new SpectatorAudioSchedule(seed); }

        /// <summary>Picks up to <paramref name="voices"/> speakers for one reaction into <paramref name="result"/> and returns how many it found (fewer than asked when too few stand within
        /// <paramref name="maxDistance"/> and apart). <paramref name="positions"/> holds the stands' emitters as x, y, z triples (a trailing partial triple is ignored); a speaker at a non-finite
        /// position is never picked; <paramref name="busy"/> (null = none) are the speakers that sound right now.</summary>
        internal int Choose(float[] positions, float listenerX, float listenerY, float listenerZ, float maxDistance, int voices, ISet<int> busy, int[] result)
        {
            if (positions == null || result == null || voices < 1) return 0;
            int n = positions.Length / 3;
            if (n == 0) return 0;
            if (_distances.Length != n) _distances = new float[n];      // exactly n: ChooseSpeaker walks the whole array
            for (int i = 0; i < n; i++)
            {
                float dx = positions[3 * i] - listenerX, dy = positions[3 * i + 1] - listenerY, dz = positions[3 * i + 2] - listenerZ;
                _distances[i] = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);       // NaN stays NaN: ChooseSpeaker takes it for out of reach
            }
            _excluded.Clear();
            if (busy != null) foreach (int b in busy) _excluded.Add(b);
            int found = 0;
            voices = Math.Min(voices, result.Length);
            while (found < voices)
            {
                int speaker = _law.ChooseSpeaker(_distances, maxDistance, _excluded);
                if (speaker < 0) break;
                result[found++] = speaker;
                _law.Played(0, speaker, -1);
                _excluded.Add(speaker);
                for (int i = 0; i < n; i++)
                {
                    if (_excluded.Contains(i)) continue;
                    float dx = positions[3 * i] - positions[3 * speaker], dy = positions[3 * i + 1] - positions[3 * speaker + 1], dz = positions[3 * i + 2] - positions[3 * speaker + 2];
                    if (dx * dx + dy * dy + dz * dz < MinSeparation * MinSeparation) _excluded.Add(i);
                }
            }
            return found;
        }
    }
}
