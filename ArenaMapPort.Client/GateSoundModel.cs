#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using Newtonsoft.Json.Linq;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The pure rules behind <see cref="GateSounds"/> (no Unity type, linked into the server tests; docs/progress/gate-sounds.md): the
    /// manifest the extractor stages (<c>gate-sounds/&lt;map_key&gt;/gate-sounds.json</c>: rows, clips, seam tables), which rows belong
    /// to the gate parts a release drives, the gain, which seam of a clip the drive scale needs, and the cut itself.
    ///
    /// What Arena does: a clip plays on every gate it animates, as long as Arena's own motion (3 to 13 s). What the port does: it opens the
    /// same gates in <c>SpawnGateDriveScale</c> (default 0.5) of that time, so a clip played as it is ends 1.3 to 2.6 s after the door
    /// stopped. What this does: the extractor measured, per clip, where the motion ends (<c>event_s</c>, the thunk or bang that closes the
    /// movement) and where ONE stationary stretch can be removed with the least audible seam (a table of removals on a 0.1 s grid, each with
    /// its cut frames c0 / c1, the level ramp that evens the two sides, and whether the seam is clean: <c>ok</c>). The runtime takes the
    /// largest clean removal that does not exceed (1 - scale) * event_s and cuts: head, the end event and the tail stay, a 60 ms
    /// equal-power crossfade joins the seam. No FFT, no search at run time. The table's removals are not exact (the +-5 ms alignment moves
    /// c0 / c1 off the grid, and a clean seam may be shorter than wanted): the end event lands up to 0.2 s after the ideal at scale 0.5
    /// (Equator), see the tests for every clip.
    ///
    /// Row matching. A row belongs to a gate part when the barrier is equal, the part is null (every part of the barrier instance: both
    /// leaves of a booth door share one sound) or equal ("" = the barrier root itself moves), <c>at</c> is null or within
    /// <see cref="ArenaDoorPose.AtTolerance"/> of the part's barrier instance and <c>closed_position</c> is null or within
    /// <see cref="ArenaDoorPose.ClosedPositionTolerance"/> of the part's closed local position (AirPit's passport leaves reuse six names for
    /// sixteen leaves). <see cref="PartKey"/> is what SpawnGates hands over for a part, so the matcher needs no Unity type and no private class.
    /// </summary>
    internal static class GateSoundModel
    {
        internal const int SchemaVersion = 1;
        internal const string FitBodyCut = "body_cut", FitAsIs = "as_is";

        /// <summary>At a drive scale of at least this the clip plays as it is (the door takes as long as Arena's, nothing to remove).</summary>
        internal const float FitOffFromScale = 0.95f;

        /// <summary>The upper limit of <c>Spawns/GateSoundVolume</c>: 8 (+18 dB) so the baked -15 dB of Arena's ArenaGates group can be raised to
        /// EFT's own gate level (BufferGates plays at 1.1) during the listening pass; 1 stays the default.</summary>
        internal const float MaxUserVolume = 8f;

        /// <summary>A removal may exceed (1 - scale) * event_s by this much (seconds, rounding of the table).</summary>
        internal const double RemovalSlack = 1e-6;

        // ---- the key of a gate part ---------------------------------------------------------------------------------------------

        /// <summary>What identifies a gate part to the rows: the barrier path and part name of its first pose row, the world position of its
        /// barrier instance as that row names it (null when the row has none), and the part's closed local position (x, y, z).</summary>
        internal readonly struct PartKey
        {
            internal readonly string Barrier, Part;
            internal readonly float[] At, ClosedLocal;

            internal PartKey(string barrier, string part, float[] at, float[] closedLocal)
            {
                Barrier = barrier;
                Part = part ?? "";
                At = at;
                ClosedLocal = closedLocal;
            }
        }

        internal static PartKey KeyOf(string barrier, string part, float[] at, float x, float y, float z) =>
            new PartKey(barrier, part, at, new[] { x, y, z });

        // ---- manifest -------------------------------------------------------------------------------------------------------------

        internal sealed class Row
        {
            internal string Barrier, Part, Clip, Origin;      // Part null = every part
            internal float[] At, ClosedPosition, Emitter;
            internal float SourceVolume, PlayerVolume, MinDistance, MaxDistance, MixerDb;
        }

        internal sealed class Seam
        {
            internal double RemoveS;
            internal int C0, C1;
            internal float RampDb;
            internal bool Ok;
        }

        internal sealed class Fit
        {
            internal string Mode = FitAsIs;
            internal double EventS;
            internal int BodyFrom, BodyTo, XfadeFrames;
            internal Seam[] Seams = new Seam[0];
        }

        internal sealed class ClipInfo
        {
            internal string Id, File;
            internal int Channels, SampleRate, Frames, Bytes;
            internal bool PitchBaked;
            internal Fit Fit = new Fit();
        }

        internal sealed class Manifest
        {
            internal string MapKey;
            internal Row[] Rows;
            internal ClipInfo[] Clips;

            internal ClipInfo ClipOf(string id)
            {
                foreach (var clip in Clips)
                    if (clip.Id == id) return clip;
                return null;
            }
        }

        /// <summary>Parses and validates a staged manifest. Anything the player code would trip over (a row naming a clip that is not there, a
        /// pitch other than 1, a seam outside its clip, a file name that leaves the folder) is an <see cref="InvalidDataException"/> with the
        /// reason; the client then plays nothing for the raid and says why once.</summary>
        internal static Manifest Parse(string json, string expectedKey)
        {
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception e) { throw new InvalidDataException("gate sound manifest is not valid JSON: " + e.Message); }
            if ((int?)root["schema_version"] != SchemaVersion) throw new InvalidDataException("gate sound manifest schema is not " + SchemaVersion);
            if ((string)root["map_key"] != expectedKey) throw new InvalidDataException("gate sound manifest is for map " + (string)root["map_key"] + ", not " + expectedKey);
            var clips = new List<ClipInfo>();
            var ids = new HashSet<string>();
            foreach (var token in root["clips"] as JArray ?? new JArray())
            {
                var clip = ParseClip(token);
                if (!ids.Add(clip.Id)) throw new InvalidDataException("gate sound manifest lists the clip " + clip.Id + " twice");
                clips.Add(clip);
            }
            var rows = new List<Row>();
            foreach (var token in root["rows"] as JArray ?? new JArray()) rows.Add(ParseRow(token, ids));
            if (rows.Count == 0 || clips.Count == 0) throw new InvalidDataException("gate sound manifest has no rows or no clips");
            return new Manifest { MapKey = expectedKey, Rows = rows.ToArray(), Clips = clips.ToArray() };
        }

        static Row ParseRow(JToken t, HashSet<string> clipIds)
        {
            var row = new Row
            {
                Barrier = (string)t["barrier"], Part = t["part"] == null || t["part"].Type == JTokenType.Null ? null : (string)t["part"],
                Clip = (string)t["clip"], Origin = (string)t["origin"] ?? "",
                At = Vec3(t["at"], true, "at"), ClosedPosition = Vec3(t["closed_position"], true, "closed_position"), Emitter = Vec3(t["emitter"], false, "emitter"),
                SourceVolume = Number(t["source_volume"], "source_volume"), PlayerVolume = Number(t["player_volume"], "player_volume"),
                MinDistance = Number(t["min_distance"], "min_distance"), MaxDistance = Number(t["max_distance"], "max_distance"),
                MixerDb = Number(t["mixer_db"], "mixer_db"),
            };
            if (string.IsNullOrEmpty(row.Barrier)) throw new InvalidDataException("a gate sound row has no barrier");
            if (!clipIds.Contains(row.Clip ?? "")) throw new InvalidDataException("gate sound row " + row.Origin + " names the unknown clip " + row.Clip);
            if (Number(t["pitch"], "pitch") != 1f) throw new InvalidDataException("gate sound row " + row.Origin + " asks for a pitch other than 1: the clip carries its pitch");
            if (row.SourceVolume < 0f || row.SourceVolume > 2f || row.PlayerVolume < 0f || row.PlayerVolume > 2f)
                throw new InvalidDataException("gate sound row " + row.Origin + " has a volume outside 0..2");
            if (!(row.MinDistance > 0f && row.MaxDistance > row.MinDistance)) throw new InvalidDataException("gate sound row " + row.Origin + " has no usable distances");
            if (row.MixerDb < -80f || row.MixerDb > 20f) throw new InvalidDataException("gate sound row " + row.Origin + " has an implausible mixer level");
            return row;
        }

        static ClipInfo ParseClip(JToken t)
        {
            var clip = new ClipInfo
            {
                Id = (string)t["id"], File = (string)t["file"], Channels = (int?)t["channels"] ?? 0, SampleRate = (int?)t["sampleRate"] ?? 0,
                Frames = (int?)t["frames"] ?? 0, Bytes = (int?)t["bytes"] ?? 0, PitchBaked = (bool?)t["pitch_baked"] ?? false,
            };
            if (string.IsNullOrEmpty(clip.Id) || !IsLeafName(clip.File)) throw new InvalidDataException("a gate sound clip has no id or a file name that is not a plain name");
            if (clip.Channels < 1 || clip.Channels > 2 || clip.SampleRate < 8000 || clip.SampleRate > 192000 || clip.Frames < 1)
                throw new InvalidDataException("gate sound clip " + clip.Id + " has no usable format");
            if (clip.Bytes != 44 + clip.Frames * clip.Channels * 2) throw new InvalidDataException("gate sound clip " + clip.Id + ": the recorded size is no 44-byte header plus its samples");
            var fit = t["fit"];
            if (fit == null) throw new InvalidDataException("gate sound clip " + clip.Id + " has no fit block");
            string mode = (string)fit["mode"];
            if (mode == FitAsIs) return clip;
            if (mode != FitBodyCut) throw new InvalidDataException("gate sound clip " + clip.Id + " has the unknown fit mode " + mode);
            var body = fit["body_f"] as JArray;
            var seams = new List<Seam>();
            clip.Fit = new Fit
            {
                Mode = FitBodyCut, EventS = (double?)fit["event_s"] ?? 0.0, XfadeFrames = (int?)fit["xfade_f"] ?? 0,
                BodyFrom = body != null && body.Count == 2 ? (int)body[0] : -1, BodyTo = body != null && body.Count == 2 ? (int)body[1] : -1,
            };
            if (!(clip.Fit.EventS > 0.0) || clip.Fit.XfadeFrames < 1 || clip.Fit.BodyFrom < 0 || clip.Fit.BodyTo <= clip.Fit.BodyFrom || clip.Fit.BodyTo > clip.Frames)
                throw new InvalidDataException("gate sound clip " + clip.Id + " has an unusable fit header");
            foreach (var s in fit["seams"] as JArray ?? new JArray())
            {
                var seam = new Seam { RemoveS = (double?)s["remove_s"] ?? 0.0, C0 = (int?)s["c0f"] ?? -1, C1 = (int?)s["c1f"] ?? -1, RampDb = (float?)s["ramp_db"] ?? 0f, Ok = (bool?)s["ok"] ?? false };
                if (seam.C0 - clip.Fit.XfadeFrames < 0 || seam.C0 <= clip.Fit.BodyFrom || seam.C1 <= seam.C0 || seam.C1 > clip.Frames)
                    throw new InvalidDataException("gate sound clip " + clip.Id + " has a seam outside the clip");
                if (Math.Abs((seam.C1 - seam.C0) / (double)clip.SampleRate - seam.RemoveS) > 0.002)
                    throw new InvalidDataException("gate sound clip " + clip.Id + " has a seam whose frames and seconds disagree");
                seams.Add(seam);
            }
            clip.Fit.Seams = seams.ToArray();
            return clip;
        }

        static float[] Vec3(JToken t, bool nullable, string what)
        {
            if (t == null || t.Type == JTokenType.Null)
            {
                if (nullable) return null;
                throw new InvalidDataException("gate sound row lacks " + what);
            }
            var array = t as JArray;
            if (array == null || array.Count != 3) throw new InvalidDataException("gate sound row " + what + " is not three numbers");
            var v = new[] { (float)array[0], (float)array[1], (float)array[2] };
            foreach (float x in v)
                if (float.IsNaN(x) || float.IsInfinity(x)) throw new InvalidDataException("gate sound row " + what + " is not finite");
            return v;
        }

        static float Number(JToken t, string what)
        {
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) throw new InvalidDataException("gate sound row lacks the number " + what);
            float v = (float)t;
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidDataException("gate sound row " + what + " is not finite");
            return v;
        }

        /// <summary>A plain file name: not empty, no directory part, no ".." and not rooted, so a manifest cannot point the reader out of its folder.</summary>
        internal static bool IsLeafName(string name) =>
            !string.IsNullOrEmpty(name) && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && name != "." && name != ".." && !name.Contains("..");

        // ---- matching, gain, fit ----------------------------------------------------------------------------------------------

        static bool Within(float[] a, float[] b, float tolerance)
        {
            if (a == null || b == null) return false;
            float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return dx * dx + dy * dy + dz * dz <= tolerance * tolerance;
        }

        /// <summary>Does the row belong to the gate part with this key? (The rule of the class summary; the extractor, the validators and
        /// the tests state the same one.)</summary>
        internal static bool RowApplies(Row row, PartKey key)
        {
            if (!string.Equals(row.Barrier, key.Barrier, StringComparison.Ordinal)) return false;
            if (row.Part != null && !string.Equals(row.Part, key.Part, StringComparison.Ordinal)) return false;
            if (row.At != null && !Within(row.At, key.At, ArenaDoorPose.AtTolerance)) return false;
            if (row.ClosedPosition != null && !Within(row.ClosedPosition, key.ClosedLocal, ArenaDoorPose.ClosedPositionTolerance)) return false;
            return true;
        }

        /// <summary>The rows (indices into <paramref name="rows"/>, ascending, each once) that sound for the gate parts of a release: the
        /// first row that applies to a key, so both leaves of a booth door (one row for the assembly) and a part that two keys describe play
        /// ONE sound. A key no row applies to (a gate Arena has no sound for) adds nothing.</summary>
        internal static List<int> RowsFor(Row[] rows, IList<PartKey> keys)
        {
            var picked = new SortedSet<int>();
            foreach (var key in keys)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    if (!RowApplies(rows[i], key)) continue;
                    picked.Add(i);
                    break;
                }
            }
            return new List<int>(picked);
        }

        internal static float DbToLinear(float db) => (float)Math.Pow(10.0, db / 20.0);

        /// <summary>The volume handed to BetterAudio: the AudioSource's volume times the player's volume times the mixer group's fader (the
        /// ArenaGates group is -15 dB, 0.1778) times the user's <c>Spawns/GateSoundVolume</c>. A gain table of the shipped maps: -15.0 dB on
        /// Equator and Bowl, -15.9 dB Bay5's rollers, -17.5 dB its depot gates, -19.2 dB AirPit's bus doors, -14.9 dB its passport leaves.</summary>
        internal static float Gain(Row row, float userVolume)
        {
            if (float.IsNaN(userVolume)) userVolume = 1f;
            userVolume = Math.Max(0f, Math.Min(MaxUserVolume, userVolume));
            return row.SourceVolume * row.PlayerVolume * DbToLinear(row.MixerDb) * userVolume;
        }

        /// <summary>The distance (metres) at which BetterAudio stops playing the sound: the row's max distance times the user's scale, rounded.</summary>
        internal static int Rolloff(Row row, float scale)
        {
            if (float.IsNaN(scale) || scale <= 0f) scale = 1f;
            return Math.Max(1, (int)Math.Round(row.MaxDistance * scale));
        }

        /// <summary>The drive scale the doors actually use (<see cref="DoorPoseModel.ScaledSeconds"/> clamps the same way).</summary>
        internal static float ClampScale(float scale)
        {
            if (float.IsNaN(scale)) return DoorPoseModel.DefaultDriveScale;
            return Math.Max(DoorPoseModel.MinDriveScale, Math.Min(DoorPoseModel.MaxDriveScale, scale));
        }

        /// <summary>The seam (index into <c>fit.Seams</c>) a drive of <paramref name="scale"/> needs, -1 for none: the clean (<c>ok</c>) entry with
        /// the largest removal that does not exceed (1 - scale) * event_s. None for a clip that is never cut, at a scale of 0.95 or more, or
        /// when no clean entry is short enough.</summary>
        internal static int PickSeam(Fit fit, float scale)
        {
            if (fit == null || fit.Mode != FitBodyCut || fit.Seams.Length == 0) return -1;
            scale = ClampScale(scale);
            if (scale >= FitOffFromScale) return -1;
            double limit = (1.0 - scale) * fit.EventS + RemovalSlack;
            int best = -1;
            for (int i = 0; i < fit.Seams.Length; i++)
            {
                var seam = fit.Seams[i];
                if (!seam.Ok || seam.RemoveS > limit) continue;
                if (best < 0 || seam.RemoveS > fit.Seams[best].RemoveS) best = i;
            }
            return best;
        }

        /// <summary>Where the end event of the clip lands (seconds from its start) once the seam is cut; -1 for "no cut" gives the original time.</summary>
        internal static double EventLandsAt(Fit fit, int seam) => fit.EventS - (seam < 0 ? 0.0 : fit.Seams[seam].RemoveS);

        // ---- the cut ----------------------------------------------------------------------------------------------------------------

        /// <summary>The clip with the frames [c0, c1) removed. Before the cut the material from the body start to c0 is ramped by
        /// <c>RampDb</c> (linear in dB, 0 at the body start) so its level meets the level after the seam; the <c>XfadeFrames</c> frames before
        /// c0 are then crossfaded (cos / sin, equal power) with the frames before c1; head and tail are copied. The same operation as
        /// extract_gate_sounds.apply_cut (tools/tests/test_gate_sounds.py pins the numbers), over interleaved samples.</summary>
        internal static float[] ApplyCut(float[] source, int channels, Fit fit, Seam seam)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (channels < 1 || source.Length % channels != 0) throw new ArgumentException("samples are no whole number of frames");
            int frames = source.Length / channels;
            int fade = fit.XfadeFrames, c0 = seam.C0, c1 = seam.C1, lo = fit.BodyFrom;
            if (fade < 1 || c0 - fade < 0 || c0 >= c1 || c1 > frames || lo < 0 || lo >= c0) throw new ArgumentException("the seam does not fit the clip");
            float[] work = source;
            if (seam.RampDb != 0f)
            {
                work = (float[])source.Clone();
                int n = c0 - lo;
                for (int i = 0; i < n; i++)
                {
                    double db = n > 1 ? seam.RampDb * (double)i / (n - 1) : 0.0;
                    float gain = (float)Math.Pow(10.0, db / 20.0);
                    for (int c = 0; c < channels; c++) work[(lo + i) * channels + c] *= gain;
                }
            }
            int outFrames = frames - (c1 - c0);
            var y = new float[outFrames * channels];
            Array.Copy(work, 0, y, 0, (c0 - fade) * channels);
            for (int j = 0; j < fade; j++)
            {
                double theta = Math.PI / 2 * j / fade;
                float fadeOut = (float)Math.Cos(theta), fadeIn = (float)Math.Sin(theta);
                for (int c = 0; c < channels; c++)
                    y[(c0 - fade + j) * channels + c] = work[(c0 - fade + j) * channels + c] * fadeOut + work[(c1 - fade + j) * channels + c] * fadeIn;
            }
            Array.Copy(work, c1 * channels, y, c0 * channels, (frames - c1) * channels);
            return y;
        }
    }
}
