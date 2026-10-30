using System.IO.Compression;
using ArenaMapPort.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ArenaMapPort.Server.Tests;

/// <summary>Runs only when the four spectator graphs of a staged dist/ are readable: dist/ of the checkout the tests run in, or the folder named by ARENAMAPPORT_DIST.</summary>
internal sealed class DistCrowdFactAttribute : FactAttribute
{
    public DistCrowdFactAttribute()
    {
        if (!DistCrowd.AllPresent) Skip = "no staged spectator graphs (dist/BepInEx/plugins/maschine-ArenaMapPort/spectators/<map>/spectator.json.gz); set ARENAMAPPORT_DIST to a dist folder";
    }
}

internal static class DistCrowd
{
    internal static readonly string[] Maps = { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" };

    internal static string? Graph(string key)
    {
        string tail = Path.Combine("BepInEx", "plugins", "maschine-ArenaMapPort", "spectators", key, "spectator.json.gz");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "dist", tail);
            if (File.Exists(path)) return path;
        }
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_DIST");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string path = Path.Combine(env, tail);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static bool AllPresent => Maps.All(m => Graph(m) != null);

    private static readonly Dictionary<string, BedPoint[]> Cache = new();

    internal static BedPoint[] Points(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var points)) Cache[key] = points = Read(key);
            return points;
        }
    }

    /// <summary>The positions the runtime hands the bed: the actor roots in world space (the container offset is the only transform; rotation is the identity and scale 1 on all four maps) plus 1.6 m.</summary>
    private static BedPoint[] Read(string key)
    {
        using var gz = new GZipStream(File.OpenRead(Graph(key)!), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var doc = JObject.Parse(reader.ReadToEnd());
        var origin = doc["container"]!["position"]!.Select(t => (float)t).ToArray();
        return doc["actors"]!.Select(actor =>
        {
            var p = actor["nodes"]![0]!["position"]!.Select(t => (float)t).ToArray();
            return new BedPoint(p[0] + origin[0], p[1] + origin[1] + 1.6f, p[2] + origin[2]);
        }).ToArray();
    }
}

/// <summary>
/// The Unity-free rules of the crowd murmur bed (docs/progress/spectator-audio.md "Crowd bed"): the stands, the parameters of a stand's source, the distance and volume
/// laws, the excitement smoothing and the manifest's optional bed. The synthetic crowds are the tests' own; the real positions are read from a staged dist/ when there is one.
/// </summary>
public sealed class SpectatorCrowdBedModelTests
{
    private readonly ITestOutputHelper _out;
    public SpectatorCrowdBedModelTests(ITestOutputHelper output) { _out = output; }

    // ---- synthetic crowds ----------------------------------------------------------------------------------------------------------------

    /// <summary>A repeatable scatter: points of a square of +-spread around each centre, from a fixed linear congruential generator.</summary>
    private static List<BedPoint> Crowd(IEnumerable<(float X, float Y, float Z)> centres, int perCentre, float spread, uint seed = 12345)
    {
        uint state = seed;
        float Next() { state = unchecked(state * 1664525u + 1013904223u); return ((state >> 8) / (float)(1 << 24)) * 2f - 1f; }
        var points = new List<BedPoint>();
        foreach (var c in centres)
            for (int i = 0; i < perCentre; i++)
                points.Add(new BedPoint(c.X + Next() * spread, c.Y + Next() * 2f, c.Z + Next() * spread));
        return points;
    }

    private static readonly (float X, float Y, float Z)[] FourStands = { (-60, 10, -60), (60, 12, -60), (60, 8, 60), (-60, 9, 60) };

    [Fact]
    public void NoPositionGivesNoStandAndOnePositionGivesOne()
    {
        Assert.Empty(SpectatorCrowdBedModel.Plan(null!, 60));
        Assert.Empty(SpectatorCrowdBedModel.Plan(new List<BedPoint>(), 60));
        Assert.Empty(SpectatorCrowdBedModel.Plan(new[] { new BedPoint(float.NaN, 0, 0), new BedPoint(0, float.PositiveInfinity, 0) }, 60));
        var one = Assert.Single(SpectatorCrowdBedModel.Plan(new[] { new BedPoint(3, 7, -4) }, 60));
        Assert.Equal((3f, 7f, -4f), (one.X, one.Y, one.Z));
        Assert.Equal(1, one.Members);
        Assert.Equal(0f, one.Radius);
        Assert.Equal(8f, one.MinDistance);          // the floor
        Assert.Equal(68f, one.MaxDistance);
        Assert.Equal(0f, one.StartFraction);
        Assert.Equal(1f, one.Pitch);                // one stand: no spread of pitches
    }

    [Fact]
    public void TwoIdenticalPositionsCollapseToOneStand()
    {
        var stands = SpectatorCrowdBedModel.Plan(new[] { new BedPoint(5, 5, 5), new BedPoint(5, 5, 5) }, 60);
        var stand = Assert.Single(stands);
        Assert.Equal(2, stand.Members);
    }

    [Fact]
    public void NonFinitePositionsAreIgnored()
    {
        var points = Crowd(FourStands, 10, 8);
        var clean = SpectatorCrowdBedModel.Plan(points, 60);
        points.Insert(3, new BedPoint(float.NaN, 0, 0));
        points.Add(new BedPoint(0, 0, float.NegativeInfinity));
        var dirty = SpectatorCrowdBedModel.Plan(points, 60);
        Assert.Equal(clean.Length, dirty.Length);
        Assert.Equal(40, dirty.Sum(s => s.Members));
    }

    [Fact]
    public void FourSeparatedStandsAreFoundAtTheirCentres()
    {
        var stands = SpectatorCrowdBedModel.Plan(Crowd(FourStands, 24, 10), 60);
        Assert.Equal(4, stands.Length);
        // west to east (by the means, so two stands at nearly the same x may come either way round); the index is the position in that order
        Assert.Equal(stands.OrderBy(s => s.X).Select(s => s.Index), stands.Select(s => s.Index));
        for (int i = 0; i < 4; i++) Assert.Equal(i, stands[i].Index);
        Assert.Equal(stands.Select(s => s.X).OrderBy(x => x), stands.Select(s => s.X));
        foreach (var (x, y, z) in FourStands)
        {
            var near = Assert.Single(stands, s => Math.Abs(s.X - x) < 4 && Math.Abs(s.Z - z) < 4);
            Assert.Equal(24, near.Members);
            Assert.InRange(near.Radius, 6, 18);       // a +-10 m square: corners lie 14 m out
            // the centre's height is the mean height of its members
            Assert.InRange(near.Y, y - 2, y + 2);
        }
        Assert.Equal(96, stands.Sum(s => s.Members));
    }

    [Fact]
    public void PlanIsDeterministicAndDoesNotDependOnTheOrderOfAClearCrowd()
    {
        var points = Crowd(FourStands, 24, 10);
        var a = SpectatorCrowdBedModel.Plan(points, 60);
        var b = SpectatorCrowdBedModel.Plan(points, 60);
        Assert.Equal(Describe(a), Describe(b));
        for (int i = 0; i < a.Length; i++)
            Assert.Equal((a[i].X, a[i].Y, a[i].Z, a[i].Radius, a[i].Members), (b[i].X, b[i].Y, b[i].Z, b[i].Radius, b[i].Members));
        var reversed = new List<BedPoint>(points); reversed.Reverse();
        var shuffled = points.OrderBy(p => (p.X * 31 + p.Z * 17) % 7).ToList();
        foreach (var other in new[] { SpectatorCrowdBedModel.Plan(reversed, 60), SpectatorCrowdBedModel.Plan(shuffled, 60) })
        {
            Assert.Equal(a.Length, other.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.InRange(Math.Abs(a[i].X - other[i].X), 0, 1e-3);
                Assert.InRange(Math.Abs(a[i].Z - other[i].Z), 0, 1e-3);
                Assert.Equal(a[i].Members, other[i].Members);
            }
        }
    }

    private static string Describe(BedStand[] stands) => SpectatorCrowdBedModel.Describe(stands);

    [Fact]
    public void StandsAreAddedUntilEveryRadiusFitsAndNeverBeyondSix()
    {
        // a row of 50 spectators 100 m long: two stands of 24 m radius fit (limit 25 m)
        var row = Enumerable.Range(0, 50).Select(i => new BedPoint(i * 2f, 10, 0)).ToList();
        var two = SpectatorCrowdBedModel.Plan(row, 60);
        Assert.Equal(2, two.Length);
        Assert.All(two, s => Assert.InRange(s.Radius, 0, SpectatorCrowdBedModel.MaxStandRadius));
        // 200 m: two stands have a radius of 50 m, three of 33 m, four of 24 m: four fit
        var longRow = Enumerable.Range(0, 100).Select(i => new BedPoint(i * 2f, 10, 0)).ToList();
        var four = SpectatorCrowdBedModel.Plan(longRow, 60);
        Assert.Equal(4, four.Length);
        Assert.All(four, s => Assert.InRange(s.Radius, 0, SpectatorCrowdBedModel.MaxStandRadius));
        // a ring 240 m across cannot fit in six: the cap stops it
        var ring = Enumerable.Range(0, 72).Select(i => new BedPoint(120f * (float)Math.Cos(i * Math.PI / 36), 10, 120f * (float)Math.Sin(i * Math.PI / 36))).ToList();
        var capped = SpectatorCrowdBedModel.Plan(ring, 60);
        Assert.Equal(SpectatorCrowdBedModel.MaxStands, capped.Length);
        Assert.Contains(capped, s => s.Radius > SpectatorCrowdBedModel.MaxStandRadius);
        Assert.Equal(72, capped.Sum(s => s.Members));
    }

    [Fact]
    public void EachStandCarriesTheParametersOfItsSource()
    {
        var points = Crowd(FourStands, 24, 10);
        var stands = SpectatorCrowdBedModel.Plan(points, 60);
        Assert.Equal(4, stands.Length);
        for (int i = 0; i < stands.Length; i++)
        {
            var s = stands[i];
            Assert.Equal(Math.Max(8f, Math.Min(60f, s.Radius)), s.MinDistance);
            Assert.Equal(s.MinDistance + 60f, s.MaxDistance);
            Assert.Equal(i / 4f, s.StartFraction);
            Assert.Equal(0.98f + 0.04f * i / 3f, s.Pitch, 5);
        }
        Assert.Equal(0.98f, stands[0].Pitch, 5);
        Assert.Equal(1.02f, stands[3].Pitch, 5);
        // the reach moves only the maximum distance, and is held to 10..150 m
        Assert.Equal(stands[1].MinDistance + 100f, SpectatorCrowdBedModel.Plan(points, 100)[1].MaxDistance);
        Assert.Equal(stands[1].MinDistance + 10f, SpectatorCrowdBedModel.Plan(points, 1f)[1].MaxDistance);
        Assert.Equal(stands[1].MinDistance + 150f, SpectatorCrowdBedModel.Plan(points, 1000f)[1].MaxDistance);
        Assert.Equal(stands[1].MinDistance + 60f, SpectatorCrowdBedModel.Plan(points, float.NaN)[1].MaxDistance);
    }

    [Fact]
    public void TheMinimumDistanceIsTheRadiusHeldTo8To60Metres()
    {
        var tight = SpectatorCrowdBedModel.Plan(Crowd(new[] { (0f, 0f, 0f) }, 5, 1f), 60);
        Assert.All(tight, s => Assert.Equal(8f, s.MinDistance));
        var ring = Enumerable.Range(0, 72).Select(i => new BedPoint(150f * (float)Math.Cos(i * Math.PI / 36), 10, 150f * (float)Math.Sin(i * Math.PI / 36))).ToList();
        var wide = SpectatorCrowdBedModel.Plan(ring, 60);
        Assert.Contains(wide, s => s.Radius > 60f);
        Assert.All(wide, s => Assert.InRange(s.MinDistance, 8f, 60f));
        Assert.Contains(wide, s => s.MinDistance == 60f);
    }

    [Fact]
    public void TheLogLineNamesTheStandsTheirRadiiAndCentres()
    {
        var stands = SpectatorCrowdBedModel.Plan(Crowd(FourStands, 24, 10), 60);
        string line = SpectatorCrowdBedModel.Describe(stands);
        Assert.StartsWith("4 stands (radii ", line);
        Assert.Contains(" m, centres (", line);
        Assert.EndsWith(")", line);
        Assert.Equal(4, line.Split("radii ")[1].Split(" m,")[0].Split('/').Length);        // four radii
        Assert.Equal(4, line.Split("centres ")[1].Split(' ').Length);                       // four centres
        Assert.Equal("1 stand (radius 0.0 m, centre (3,7,-4))", SpectatorCrowdBedModel.Describe(SpectatorCrowdBedModel.Plan(new[] { new BedPoint(3, 7, -4) }, 60)));
    }

    // ---- the distance law ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDistanceLawIsFullInsideThenOneOverDThenSilentAtTheMaximum()
    {
        const float min = 20, max = 80;
        Assert.Equal(1f, SpectatorCrowdBedModel.RolloffGain(0, min, max));
        Assert.Equal(1f, SpectatorCrowdBedModel.RolloffGain(20, min, max));
        Assert.Equal(1f, SpectatorCrowdBedModel.RolloffGain(float.NaN, min, max));
        // 1/d (6 dB per doubling) until the taper starts at 60 % of the way (20 + 0.6 * 60 = 56 m)
        Assert.Equal(0.5f, SpectatorCrowdBedModel.RolloffGain(40, min, max), 5);
        Assert.Equal(20f / 56f, SpectatorCrowdBedModel.RolloffGain(56, min, max), 5);
        // beyond it the gain falls below the 1/d law and reaches nothing at the maximum distance (Unity's Logarithmic mode would stay at min / max for ever)
        Assert.True(SpectatorCrowdBedModel.RolloffGain(68, min, max) < 20f / 68f * 0.6f);
        Assert.Equal(0f, SpectatorCrowdBedModel.RolloffGain(80, min, max));
        Assert.Equal(0f, SpectatorCrowdBedModel.RolloffGain(500, min, max));
        float previous = 1f;
        for (float d = 0; d <= 90; d += 0.25f)
        {
            float g = SpectatorCrowdBedModel.RolloffGain(d, min, max);
            Assert.InRange(g, 0f, previous + 1e-6f);        // never rises
            previous = g;
        }
    }

    [Theory]
    [InlineData(8f, 68f)]
    [InlineData(17f, 77f)]
    [InlineData(60f, 120f)]
    [InlineData(60f, 70f)]
    public void TheCurveKeysAreStrictlyIncreasingFromFullToSilent(float min, float max)
    {
        SpectatorCrowdBedModel.RolloffKeys(min, max, out var times, out var gains);
        Assert.Equal(SpectatorCrowdBedModel.RolloffSamples + 1, times.Length);
        Assert.Equal(times.Length, gains.Length);
        Assert.Equal((0f, 1f), (times[0], gains[0]));
        Assert.Equal(min / max, times[1], 5);
        Assert.Equal(1f, gains[1]);
        Assert.Equal((1f, 0f), (times[^1], gains[^1]));
        for (int i = 1; i < times.Length; i++)
        {
            Assert.True(times[i] > times[i - 1], "time " + i);
            Assert.InRange(gains[i], 0f, gains[i - 1]);
        }
        // the keys are the law at their distances
        for (int i = 1; i < times.Length; i++)
            Assert.Equal(SpectatorCrowdBedModel.RolloffGain(times[i] * max, min, max), gains[i], 4);
    }

    // ---- the volume law ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSourceVolumeNeverExceedsOneAndTheClipGainRestoresTheLevel()
    {
        Assert.Equal(3.2f, SpectatorCrowdBedModel.ClipGain, 5);
        Assert.Equal(1.0f / 3.2f, SpectatorCrowdBedModel.SourceVolume(1f, 1f), 5);
        Assert.Equal(1f, SpectatorCrowdBedModel.SourceVolume(2f, 1.6f), 5);
        Assert.Equal(0f, SpectatorCrowdBedModel.SourceVolume(0f, 1.6f));
        foreach (float volume in new[] { 0f, 0.25f, 1f, 1.5f, 2f })
            foreach (float factor in new[] { 1f, 1.2f, 1.6f })
            {
                float source = SpectatorCrowdBedModel.SourceVolume(volume, factor);
                Assert.InRange(source, 0f, 1f);
                // the level that reaches the listener is the clip's own level x volume x factor over the whole range
                Assert.Equal(volume * factor, source * SpectatorCrowdBedModel.ClipGain, 4);
            }
        // out-of-range input is held to the ranges; NaN falls back
        Assert.Equal(1f, SpectatorCrowdBedModel.SourceVolume(9f, 9f), 5);
        Assert.Equal(SpectatorCrowdBedModel.SourceVolume(1f, 1f), SpectatorCrowdBedModel.SourceVolume(float.NaN, float.NaN), 5);
        Assert.Equal(SpectatorCrowdBedModel.SourceVolume(1f, 1f), SpectatorCrowdBedModel.SourceVolume(1f, 0.2f), 5);
        Assert.Equal(0f, SpectatorCrowdBedModel.SourceVolume(-1f, 1f));
        Assert.Equal(1f, SpectatorCrowdBedModel.ClampVolume(float.NaN));
        Assert.Equal(60f, SpectatorCrowdBedModel.ClampReach(float.NaN));
    }

    [Fact]
    public void TheClipCarriesTheFixedGain()
    {
        var floats = SpectatorCrowdBedModel.ToClipFloats(new short[] { 0, 16384, -16384, 32767, -32768 });
        Assert.Equal(0f, floats[0]);
        Assert.Equal(0.5f * 3.2f, floats[1], 5);
        Assert.Equal(-0.5f * 3.2f, floats[2], 5);
        Assert.Equal(32767 / 32768f * 3.2f, floats[3], 5);
        Assert.Equal(-3.2f, floats[4], 5);
        // the bed's measured peak (-22.7 dBFS) stays far below full scale after the gain
        Assert.True(0.0735f * 3.2f < 0.25f);
    }

    // ---- the excitement ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheExcitementFollowsItsTargetWithATwoSecondTimeConstant()
    {
        var e = new CrowdExcitement();
        Assert.Equal((0f, 0f, 1f), (e.Target, e.Current, e.Factor));
        e.SetTarget(1f);
        Assert.Equal(0f, e.Current);                          // setting does not move the level
        for (int i = 0; i < 100; i++) e.Step(0.02f);          // two seconds in 20 ms frames
        Assert.Equal(1f - Math.Exp(-1), e.Current, 3);        // 63.2 % of the way after one time constant
        Assert.Equal(1f + 0.6f * e.Current, e.Factor, 5);
        float before = e.Current;
        Assert.True(e.Step(0.5f));
        Assert.True(e.Current > before);
        for (int i = 0; i < 2000; i++) e.Step(0.02f);
        Assert.Equal(1f, e.Current);                          // it arrives, it does not creep for ever
        Assert.Equal(1.6f, e.Factor, 5);
        Assert.False(e.Step(0.02f));                          // nothing to do any more
        e.SetTarget(0f);
        Assert.True(e.Step(2f));
        Assert.InRange(e.Current, 0.35f, 0.38f);              // down again: 1 - 0.632
    }

    [Fact]
    public void TheExcitementIsIndependentOfTheFrameRate()
    {
        var fine = new CrowdExcitement(); var coarse = new CrowdExcitement();
        fine.SetTarget(0.8f); coarse.SetTarget(0.8f);
        for (int i = 0; i < 300; i++) fine.Step(0.01f);       // 3 s
        for (int i = 0; i < 3; i++) coarse.Step(1f);          // 3 s
        Assert.Equal(fine.Current, coarse.Current, 3);
    }

    [Fact]
    public void BadInputToTheExcitementIsHeldToZeroToOne()
    {
        var e = new CrowdExcitement();
        e.SetTarget(7f); Assert.Equal(1f, e.Target);
        e.SetTarget(-3f); Assert.Equal(0f, e.Target);
        e.SetTarget(float.NaN); Assert.Equal(0f, e.Target);
        e.SetTarget(float.PositiveInfinity); Assert.Equal(1f, e.Target);
        e.SetTarget(1f);
        Assert.False(e.Step(0f)); Assert.False(e.Step(-1f)); Assert.False(e.Step(float.NaN));
        Assert.Equal(0f, e.Current);
        Assert.True(e.Step(float.PositiveInfinity));          // an endless frame arrives at the target, it does not produce NaN
        Assert.Equal(1f, e.Current);
        e.SetTarget(0.25f); e.Snap();
        Assert.Equal(0.25f, e.Current);
        e.Reset();
        Assert.Equal((0f, 0f), (e.Target, e.Current));
    }

    // ---- the manifest's bed --------------------------------------------------------------------------------------------------------------

    private static object Clip(int i) => new
    {
        name = $"arena_fan7_participating_{i:00}", file = $"arena_fan7_participating_{i:00}.wav", channels = 1, sample_rate = 44100,
        frames = 44100, bytes = 88244, sha256 = new string('a', 64)
    };

    private static JObject Manifest(object? bed)
    {
        var root = JObject.FromObject(new { schema = 1, bank = "Participating", trigger = 1123, source_bundle = "x.bundle", external_entries_omitted = 9, clips = Enumerable.Range(0, 13).Select(Clip).ToArray() });
        if (bed != null) root["bed"] = JObject.FromObject(bed);
        return root;
    }

    private static object GoodBed(Action<JObject>? change = null)
    {
        // The shape tools/extract/extract_spectator_audio.py writes (the extraction of 2026-10-30, 29.813 s): "source" is the container name as a string, "source_id" the
        // clip's path id and "processing" holds the clip's own statistics before the downmix. The client reads only name, file, channels, sample_rate, frames, bytes, sha256
        // and seam_crossfade_ms and ignores the rest.
        var bed = JObject.FromObject(new
        {
            name = "arena_fans_ambient_postmatch", file = "arena_fans_ambient_postmatch.wav", channels = 1, sample_rate = 44100,
            frames = 1314766, bytes = 2629576, sha256 = new string('b', 64), peak = 0.067779541015625, rms = 0.009969709001597833, seam_crossfade_ms = 500,
            source = "resources.assets", source_id = 5167,
            processing = new
            {
                original_channels = 2, original_rate = 44100, original_frames = 1336816, original_duration = 30.31328798185941,
                original_peak = 0.075225830078125, original_rms = 0.010541602213171818, downmix = "mean", gain_db = 0.0, crossfade_frames = 22050
            }
        });
        change?.Invoke(bed);
        return bed;
    }

    private static SpectatorAudioManifest Parse(JObject json) => JsonConvert.DeserializeObject<SpectatorAudioManifest>(json.ToString())!;

    [Fact]
    public void AManifestWithABedParsesAndValidatesAndKeepsTheClipRulesApart()
    {
        var manifest = Parse(Manifest(GoodBed()));
        manifest.Validate();
        manifest.ValidateBed();
        var bed = manifest.bed;
        Assert.NotNull(bed);
        Assert.Equal(("arena_fans_ambient_postmatch", "arena_fans_ambient_postmatch.wav", 1, 44100, 1314766, 500), (bed.name, bed.file, bed.channels, bed.sample_rate, bed.frames, bed.seam_crossfade_ms));
        Assert.Equal(44 + 1314766 * 2, bed.bytes);
        Assert.Equal(new string('b', 64), bed.sha256);
        Assert.Equal(13, manifest.clips.Length);
    }

    [Fact]
    public void AManifestWithoutABedIsStillValidAndHasNoBed()
    {
        var manifest = Parse(Manifest(null));
        manifest.Validate();                                      // the 13 calls play as before
        Assert.Null(manifest.bed);
        Assert.Throws<InvalidDataException>(manifest.ValidateBed);
    }

    [Theory]
    [InlineData("name", "arena_fans_ambient_other")]
    [InlineData("file", "../arena_fans_ambient_postmatch.wav")]
    [InlineData("file", "arena_fans_ambient_postmatch.ogg")]
    [InlineData("channels", 2)]
    [InlineData("sample_rate", 48000)]
    [InlineData("frames", 1000)]
    [InlineData("frames", 6000000)]
    [InlineData("bytes", 12345)]
    [InlineData("sha256", "ABCDEF")]
    [InlineData("seam_crossfade_ms", 0)]
    [InlineData("seam_crossfade_ms", 60000)]
    public void ADamagedBedIsRefusedWithoutTouchingTheClips(string field, object value)
    {
        var manifest = Parse(Manifest(GoodBed(bed => bed[field] = JToken.FromObject(value))));
        manifest.Validate();
        Assert.Throws<InvalidDataException>(manifest.ValidateBed);
    }

    [Fact]
    public void ABedWithAWrongHashCaseOrAMissingHashIsRefused()
    {
        Assert.Throws<InvalidDataException>(Parse(Manifest(GoodBed(bed => bed.Remove("sha256")))).ValidateBed);
        Assert.Throws<InvalidDataException>(Parse(Manifest(GoodBed(bed => bed["sha256"] = new string('B', 64)))).ValidateBed);
        var otherSchema = Manifest(GoodBed()); otherSchema["schema"] = 2;
        Assert.Throws<InvalidDataException>(Parse(otherSchema).ValidateBed);
    }

    // ---- the real crowds -----------------------------------------------------------------------------------------------------------------

    [DistCrowdFact]
    public void TheFourRealCrowdsFormTwoToSixStandsEachMeetingTheRadiusRuleOrTheCap()
    {
        // The rule (k from 2 until every stand is at most 25 m wide, at most six) gives, with the positions of the dist/ graphs of 2026-10-30: Equator 4 (the natural
        // four stands, 13 to 17 m), Bay5 4, Air Pit 6 and Bowl 6 (the cap; wider than 25 m). At the first draft's 30 m Equator was 2 stands (radii 29 and 27 m) and Air Pit 5.
        var expected = new Dictionary<string, int> { ["arena_equator"] = 4, ["arena_bay5"] = 4, ["arena_airpit"] = 6, ["arena_bowl"] = 6 };
        foreach (string key in DistCrowd.Maps)
        {
            var points = DistCrowd.Points(key);
            var stands = SpectatorCrowdBedModel.Plan(points, 60);
            _out.WriteLine($"{key}: {points.Length} fans, {SpectatorCrowdBedModel.Describe(stands)}");
            Assert.InRange(stands.Length, 2, SpectatorCrowdBedModel.MaxStands);
            Assert.Equal(points.Length, stands.Sum(s => s.Members));
            Assert.True(stands.Length == SpectatorCrowdBedModel.MaxStands || SpectatorCrowdBedModel.LargestRadius(stands) <= SpectatorCrowdBedModel.MaxStandRadius, key);
            Assert.Equal(expected[key], stands.Length);
            // every centre lies inside the crowd's own bounding box
            Assert.All(stands, s =>
            {
                Assert.InRange(s.X, points.Min(p => p.X), points.Max(p => p.X));
                Assert.InRange(s.Y, points.Min(p => p.Y), points.Max(p => p.Y));
                Assert.InRange(s.Z, points.Min(p => p.Z), points.Max(p => p.Z));
            });
            Assert.Equal(Describe(stands), Describe(SpectatorCrowdBedModel.Plan(points, 60)));       // and the same again
        }
    }

    [DistCrowdFact]
    public void TheFourRealCrowdsAreTheSameWhateverTheReach()
    {
        foreach (string key in DistCrowd.Maps)
        {
            var points = DistCrowd.Points(key);
            var a = SpectatorCrowdBedModel.Plan(points, 20);
            var b = SpectatorCrowdBedModel.Plan(points, 140);
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal((a[i].X, a[i].Y, a[i].Z, a[i].MinDistance), (b[i].X, b[i].Y, b[i].Z, b[i].MinDistance));
                Assert.Equal(a[i].MaxDistance + 120f, b[i].MaxDistance, 3);
            }
        }
    }
}
