#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArenaMapPort.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The pure rules behind the gate and door sounds (GateSoundModel and PcmWav, docs/progress/gate-sounds.md): the manifest, which rows
/// sound for the gate parts a release drives, the gain table, the seam a drive scale needs and the cut. The numbers of the picks are the
/// reviewed ones (2026-10-25): with the clean-seam tables the end event of a clip lands 0.01 to 0.2 s after the ideal at scale 0.5.
/// </summary>
public sealed class GateSoundModelTests
{
    static string RepoText(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }

    static string RepoTextOrNull(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    static readonly string[] Maps = { "equator", "bay5", "airpit", "bowl" };

    // ---- manifest built from the tracked row sidecar (the real rows, synthetic whole clips) --------------------------------

    static JObject Sidecar(string stem) => JObject.Parse(RepoText($"tools/maps/{stem}.gatesounds.json"));

    static GateSoundModel.Manifest FromSidecar(string stem)
    {
        var sidecar = Sidecar(stem);
        var clips = new JArray(sidecar["clips"].Select(c => new JObject
        {
            ["id"] = c["id"], ["file"] = (string)c["id"] + ".wav", ["channels"] = 2, ["sampleRate"] = 44100, ["frames"] = 44100, ["bytes"] = 44 + 44100 * 4,
            ["pitch_baked"] = false, ["fit"] = new JObject { ["mode"] = "as_is" },
        }));
        var root = new JObject { ["schema_version"] = 1, ["map_key"] = (string)sidecar["key"], ["rows"] = sidecar["rows"], ["clips"] = clips };
        return GateSoundModel.Parse(root.ToString(), (string)sidecar["key"]);
    }

    static GateSoundModel.Row RowOf(string barrier = "B/Gate", string part = null, float[] at = null, float[] closed = null) => new()
    {
        Barrier = barrier, Part = part, At = at, ClosedPosition = closed, Clip = "c", Origin = "test", Emitter = new[] { 0f, 1f, 0f },
        SourceVolume = 1f, PlayerVolume = 1f, MinDistance = 0.4f, MaxDistance = 30f, MixerDb = -15f,
    };

    static GateSoundModel.PartKey KeyOf(string barrier, string part, float[] at, float[] closed) =>
        GateSoundModel.KeyOf(barrier, part, at, closed[0], closed[1], closed[2]);

    // ---- matching ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARowWithNoPartServesEveryPartOfItsBarrierInstanceAndOnlyThatInstance()
    {
        var row = RowOf("SPAWN_DOORS/Door", at: new[] { 4.535f, 0.048f, 40.02f });
        var zero = new[] { 0f, 0f, 0f };
        Assert.True(GateSoundModel.RowApplies(row, KeyOf("SPAWN_DOORS/Door", "Door_L", new[] { 4.535f, 0.048f, 40.02f }, zero)));
        Assert.True(GateSoundModel.RowApplies(row, KeyOf("SPAWN_DOORS/Door", "Door_R", new[] { 4.58f, 0.048f, 40.02f }, zero)));     // 4.5 cm: inside
        Assert.False(GateSoundModel.RowApplies(row, KeyOf("SPAWN_DOORS/Door", "Door_R", new[] { 4.60f, 0.048f, 40.02f }, zero)));    // 6.5 cm: another instance
        Assert.False(GateSoundModel.RowApplies(row, KeyOf("SPAWN_DOORS/Door", "Door_R", new[] { -4.328f, 0.048f, 40.02f }, zero)));
        Assert.False(GateSoundModel.RowApplies(row, KeyOf("SPAWN_DOORS/Door", "Door_R", null, zero)));                                 // a part whose row has no instance
        Assert.False(GateSoundModel.RowApplies(row, KeyOf("SPAWN_DOORS/Other", "Door_R", new[] { 4.535f, 0.048f, 40.02f }, zero)));
    }

    [Fact]
    public void ANamedPartAndTheBarrierRootAreTold()
    {
        var zero = new[] { 0f, 0f, 0f };
        var named = RowOf("DOORS_SPAWN_A", "BTR_82_closed");
        Assert.True(GateSoundModel.RowApplies(named, KeyOf("DOORS_SPAWN_A", "BTR_82_closed", null, zero)));
        Assert.False(GateSoundModel.RowApplies(named, KeyOf("DOORS_SPAWN_A", "Bowl_Gates_Respawn_01", null, zero)));
        var root = RowOf("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)", "");           // Bay5's rollers: the barrier root itself moves
        Assert.True(GateSoundModel.RowApplies(root, KeyOf("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)", "", null, zero)));
        Assert.True(GateSoundModel.RowApplies(root, GateSoundModel.KeyOf("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)", null, null, 0, 0, 0)));   // null part reads as ""
        Assert.False(GateSoundModel.RowApplies(root, KeyOf("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)", "Leaf", null, zero)));
    }

    [Fact]
    public void AirPitsRepeatedLeafNamesAreToldApartByTheClosedLocalPosition()
    {
        var row = RowOf("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", closed: new[] { -5.089f, -3.23f, 5.938f });
        Assert.True(GateSoundModel.RowApplies(row, KeyOf("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", null, new[] { -5.089f, -3.23f, 5.938f })));
        Assert.True(GateSoundModel.RowApplies(row, KeyOf("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", null, new[] { -5.05f, -3.23f, 5.938f })));    // 3.9 cm
        Assert.False(GateSoundModel.RowApplies(row, KeyOf("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", null, new[] { 9.802f, -3.23f, 5.938f })));    // the other leaf (4)
        Assert.False(GateSoundModel.RowApplies(row, KeyOf("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", null, new[] { -5.089f, -3.23f, 3.099f })));    // the silent row of leaves
        Assert.False(GateSoundModel.RowApplies(row, KeyOf("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (2)", null, new[] { -5.089f, -3.23f, 5.938f })));
    }

    [Fact]
    public void BothLeavesOfADoorPlayOneSoundAndAGateWithoutARowAddsNothing()
    {
        var rows = new[]
        {
            RowOf("SPAWN_DOORS/Door", at: new[] { 1f, 0f, 0f }),
            RowOf("SPAWN_DOORS/Door", at: new[] { 5f, 0f, 0f }),
            RowOf("BUS_DOORS", "Apron_bus_MAZ_doors"),
        };
        var zero = new[] { 0f, 0f, 0f };
        var keys = new List<GateSoundModel.PartKey>
        {
            KeyOf("SPAWN_DOORS/Door", "Door_R", new[] { 5f, 0f, 0f }, zero),
            KeyOf("SPAWN_DOORS/Door", "Door_L", new[] { 5f, 0f, 0f }, zero),
            KeyOf("BUS_DOORS", "Apron_bus_MAZ_doors (3)", null, zero),            // the static back row: no row, no sound
            KeyOf("BUS_DOORS", "Apron_bus_MAZ_doors", null, zero),
            KeyOf("SPAWN_DOORS/Door", "Door_L", new[] { 1f, 0f, 0f }, zero),
            KeyOf("SPAWN_DOORS/Door", "Door_R", new[] { 1f, 0f, 0f }, zero),
        };
        Assert.Equal(new[] { 0, 1, 2 }, GateSoundModel.RowsFor(rows, keys));       // ascending, each once
        Assert.Empty(GateSoundModel.RowsFor(rows, new List<GateSoundModel.PartKey> { KeyOf("NOWHERE", "x", null, zero) }));
        Assert.Empty(GateSoundModel.RowsFor(rows, new List<GateSoundModel.PartKey>()));
    }

    [Fact]
    public void TheRealRowsGiveEveryGatePartAtMostOneSoundAndTheSoundingPartsAreThePlanSet()
    {
        var expected = new Dictionary<string, int> { ["equator"] = 5, ["bay5"] = 5, ["airpit"] = 11, ["bowl"] = 4 };
        foreach (var stem in Maps)
        {
            var manifest = FromSidecar(stem);
            Assert.Equal(expected[stem], manifest.Rows.Length);
            var doors = JObject.Parse(RepoText($"tools/maps/{stem}.doors.json"));
            // the keys SpawnGates would build: one per (barrier, part, at) and per closed local position of the part itself (node "")
            var keys = new List<GateSoundModel.PartKey>();
            foreach (var group in doors["poses"].Where(p => p["switch"] == null)
                         .GroupBy(p => ((string)p["barrier"], (string)p["part"], p["at"].Type == JTokenType.Null ? "" : p["at"].ToString(Newtonsoft.Json.Formatting.None))))
            {
                var first = group.First();
                float[] at = first["at"].Type == JTokenType.Null ? null : first["at"].Select(v => (float)v).ToArray();
                var closes = group.Where(p => (string)p["node"] == "").Select(p => p["closed"]["position"].Select(v => (float)v).ToArray()).ToList();
                if (closes.Count == 0) closes.Add(new[] { 0f, 0f, 0f });
                foreach (var closed in closes) keys.Add(KeyOf(group.Key.Item1, group.Key.Item2, at, closed));
            }
            int sounding = 0;
            foreach (var key in keys)
            {
                int hits = manifest.Rows.Count(r => GateSoundModel.RowApplies(r, key));
                Assert.True(hits <= 1, $"{stem}: {key.Barrier}/{key.Part} has {hits} sounds");
                sounding += hits;
            }
            Assert.Equal(new Dictionary<string, int> { ["equator"] = 10, ["bay5"] = 7, ["airpit"] = 11, ["bowl"] = 4 }[stem], sounding);
        }
    }

    // ---- gain, reach ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheGainTableOfTheShippedRowsIsTheReviewedOne()
    {
        double Db(float g) => 20.0 * Math.Log10(g);
        foreach (var stem in Maps)
        {
            foreach (var row in FromSidecar(stem).Rows)
            {
                double expected = row.Clip switch
                {
                    "arena_bay5_roller_door_full" => -15.9,
                    "arena_bay5_big_gates" => -17.5,
                    "arena_airpit_bus_doors" => -19.2,
                    "arena_airpit_metal_gates" => -14.9,
                    _ => -15.0,
                };
                Assert.Equal(expected, Db(GateSoundModel.Gain(row, 1f)), 1);
            }
        }
        Assert.Equal(0.1778f, GateSoundModel.DbToLinear(-15f), 4);
    }

    [Fact]
    public void TheUserVolumeScalesTheGainWithinZeroAndEight()
    {
        var row = RowOf();
        float one = GateSoundModel.Gain(row, 1f);
        Assert.Equal(8f, GateSoundModel.MaxUserVolume);
        Assert.Equal(0f, GateSoundModel.Gain(row, 0f));
        Assert.Equal(one * 2f, GateSoundModel.Gain(row, 2f), 5);
        Assert.Equal(one * 8f, GateSoundModel.Gain(row, 8f), 5);
        Assert.Equal(one * 8f, GateSoundModel.Gain(row, 12f), 5);
        Assert.Equal(0f, GateSoundModel.Gain(row, -1f));
        Assert.Equal(one, GateSoundModel.Gain(row, float.NaN), 5);
    }

    [Fact]
    public void TheRolloffIsTheMaxDistanceTimesTheUsersScaleRoundedAndAtLeastOne()
    {
        var row = RowOf();
        Assert.Equal(30, GateSoundModel.Rolloff(row, 1f));
        Assert.Equal(45, GateSoundModel.Rolloff(row, 1.5f));
        Assert.Equal(15, GateSoundModel.Rolloff(row, 0.5f));
        Assert.Equal(30, GateSoundModel.Rolloff(row, float.NaN));
        Assert.Equal(30, GateSoundModel.Rolloff(row, 0f));
        row.MaxDistance = 0.4f;
        Assert.Equal(1, GateSoundModel.Rolloff(row, 1f));
    }

    // ---- seam pick -------------------------------------------------------------------------------------------------------------

    static GateSoundModel.Fit SyntheticFit(double eventS = 4.0, params (double remove, bool ok)[] seams)
    {
        const int rate = 1000;
        return new GateSoundModel.Fit
        {
            Mode = GateSoundModel.FitBodyCut, EventS = eventS, BodyFrom = 100, BodyTo = 9000, XfadeFrames = 60,
            Seams = seams.Select(s => new GateSoundModel.Seam { RemoveS = s.remove, C0 = 1000, C1 = 1000 + (int)Math.Round(s.remove * rate), Ok = s.ok }).ToArray(),
        };
    }

    [Fact]
    public void ThePickIsTheLargestCleanRemovalNotAboveOneMinusTheScaleTimesTheEvent()
    {
        var fit = SyntheticFit(4.0, (0.5, true), (1.0, true), (1.5, false), (2.0, true), (2.5, false), (3.0, true));
        Assert.Equal(3, GateSoundModel.PickSeam(fit, 0.5f));       // limit 2.0: the entry of exactly 2.0 counts
        Assert.Equal(1, GateSoundModel.PickSeam(fit, 0.75f));      // limit 1.0
        Assert.Equal(0, GateSoundModel.PickSeam(fit, 0.85f));      // limit 0.6
        Assert.Equal(-1, GateSoundModel.PickSeam(fit, 0.9f));      // limit 0.4: nothing short enough
        Assert.Equal(5, GateSoundModel.PickSeam(fit, 0.25f));      // limit 3.0
        Assert.Equal(5, GateSoundModel.PickSeam(fit, 0.1f));
        Assert.Equal(5, GateSoundModel.PickSeam(fit, 0.01f));      // a scale below the drive's minimum is clamped to 0.1, the doors do the same
        Assert.Equal(3, GateSoundModel.PickSeam(fit, float.NaN));  // NaN is the default 0.5
    }

    [Fact]
    public void AClipPlaysWholeAtAScaleOfNinetyFivePercentOrMoreAndForEveryOtherReasonToNotCut()
    {
        var fit = SyntheticFit(4.0, (0.5, true), (1.0, true));
        Assert.Equal(-1, GateSoundModel.PickSeam(fit, 0.9f));                       // 0.9: limit 0.4, no entry that short
        Assert.Equal(-1, GateSoundModel.PickSeam(fit, 0.95f));
        Assert.Equal(-1, GateSoundModel.PickSeam(fit, 1f));
        Assert.Equal(-1, GateSoundModel.PickSeam(fit, 7f));
        Assert.Equal(-1, GateSoundModel.PickSeam(new GateSoundModel.Fit(), 0.5f));  // as_is
        Assert.Equal(-1, GateSoundModel.PickSeam(null, 0.5f));
        Assert.Equal(-1, GateSoundModel.PickSeam(SyntheticFit(4.0, (0.5, false), (1.0, false)), 0.5f));
        Assert.Equal(-1, GateSoundModel.PickSeam(SyntheticFit(4.0), 0.5f));
        Assert.Equal(4.0, GateSoundModel.EventLandsAt(fit, -1));
        Assert.Equal(3.0, GateSoundModel.EventLandsAt(fit, 1));
    }

    /// <summary>The picks at the default drive scale for the real tables (work/extract/gate-sounds, written by
    /// extract_gate_sounds.py clips): where the end event lands. It is NOT within 0.01 s of the ideal 0.5 x event for every clip (the clean seams
    /// cannot remove exactly what is wanted): only the Bay5 roller is, the Equator's lands 0.197 s late, the rest 0.04 to 0.08 s.</summary>
    [Fact]
    public void TheRealTablePicksAtScaleHalfLandTheEndEventWhereTheReviewMeasuredIt()
    {
        var expected = new (string map, string clip, double removal, double lands)[]
        {
            ("arena_equator", "arena_equator_old_slide_doors", 1.303, 1.697), ("arena_bay5", "arena_bay5_roller_door_full", 1.999, 2.021),
            ("arena_bay5", "arena_bay5_big_gates", 1.205, 1.345), ("arena_bowl", "arena_bowl_btr_gates", 2.900, 3.050),
            ("arena_bowl", "arena_bowl_emergency_car_gates_full", 3.001, 3.159), ("arena_bowl", "arena_bowl_double_metal_gates", 2.403, 2.477),
            ("arena_bowl", "arena_bowl_roller_gates", 2.301, 2.449),
        };
        int checkedClips = 0;
        foreach (var map in expected.Select(e => e.map).Distinct())
        {
            string text = RepoTextOrNull($"work/extract/gate-sounds/{map}/gate-sounds.json");
            if (text == null) continue;                               // the ignored package is not extracted on this machine (Python pins the same numbers)
            var manifest = GateSoundModel.Parse(text, map);
            foreach (var (_, clip, removal, lands) in expected.Where(e => e.map == map))
            {
                var info = manifest.ClipOf(clip);
                int pick = GateSoundModel.PickSeam(info.Fit, 0.5f);
                Assert.True(pick >= 0, clip);
                Assert.Equal(removal, info.Fit.Seams[pick].RemoveS, 3);
                Assert.Equal(lands, GateSoundModel.EventLandsAt(info.Fit, pick), 3);
                Assert.True(info.Fit.Seams[pick].RemoveS <= 0.5 * info.Fit.EventS + 1e-6);
                checkedClips++;
            }
        }
        Assert.True(checkedClips == 0 || checkedClips == expected.Length, "a package of some maps only");
    }

    // ---- the cut ----------------------------------------------------------------------------------------------------------------

    static GateSoundModel.Fit CutFit(int bodyFrom, int fade) => new() { Mode = GateSoundModel.FitBodyCut, EventS = 1.0, BodyFrom = bodyFrom, BodyTo = 38, XfadeFrames = fade };

    [Fact]
    public void TheCutMatchesTheNumpyReferenceOfTheExtractorsApplyCut()
    {
        // x[i] = sin(0.4 i) + 0.05 i, 40 frames; c0 = 22, c1 = 31, crossfade 6, body start 8, ramp 3 dB (numpy: x[8:22] *= 10 ** (linspace(0, 3, 14) / 20))
        var x = Enumerable.Range(0, 40).Select(i => (float)(Math.Sin(0.4 * i) + 0.05 * i)).ToArray();
        var seam = new GateSoundModel.Seam { C0 = 22, C1 = 31, RampDb = 3f, Ok = true, RemoveS = 0.009 };
        var y = GateSoundModel.ApplyCut(x, 1, CutFit(8, 6), seam);
        float[] reference =
        {
            0.000000f, 0.439418f, 0.817356f, 1.082039f, 1.199574f, 1.159297f, 0.975463f, 0.684988f, 0.341626f, 0.007681f, -0.270817f, -0.434922f, -0.440585f,
            -0.266621f, 0.080612f, 0.566770f, 1.133610f, 1.771227f, 2.097658f, 2.114079f, 1.911331f, 1.626904f, 1.384396f, 1.831510f, 2.242074f, 2.559162f,
            2.740607f, 2.765658f, 2.638252f, 2.386399f, 2.057754f,
        };
        Assert.Equal(reference.Length, y.Length);
        for (int i = 0; i < reference.Length; i++) Assert.Equal(reference[i], y[i], 4);
    }

    [Fact]
    public void TheCutRemovesExactlyTheFramesAndKeepsHeadAndTail()
    {
        var ramp = Enumerable.Range(0, 200).Select(i => (float)i).ToArray();
        var y = GateSoundModel.ApplyCut(ramp, 1, CutFit(20, 10), new GateSoundModel.Seam { C0 = 100, C1 = 140, RampDb = 0f });
        Assert.Equal(160, y.Length);
        for (int i = 0; i < 90; i++) Assert.Equal(ramp[i], y[i]);                       // head, up to the fade
        for (int i = 100; i < 160; i++) Assert.Equal(ramp[i + 40], y[i]);              // tail, from c1 on
        Assert.Equal(90f, y[90]);                                                       // the fade starts on the old material (cos 0 = 1)
        // the fade is cos / sin: at its middle both sides weigh 0.7071
        Assert.Equal(0.70710678f * (95f + 135f), y[95], 3);
    }

    [Fact]
    public void ACutOverOneWholePeriodOfASineLeavesNoClickAtEitherEndOfTheFade()
    {
        var sine = Enumerable.Range(0, 1200).Select(i => (float)Math.Sin(2 * Math.PI * i / 400.0)).ToArray();
        var y = GateSoundModel.ApplyCut(sine, 1, CutFit(100, 120), new GateSoundModel.Seam { C0 = 500, C1 = 900, RampDb = 0f });   // one period (400 frames) out
        Assert.Equal(800, y.Length);
        for (int i = 380; i < 780; i++)
            Assert.True(Math.Abs(y[i] - y[i - 1]) < 0.05f, $"a click at {i}: {y[i] - y[i - 1]}");        // the sine itself steps 0.0157 per frame, the equal-power bump adds less
        Assert.Equal(sine[0], y[0]);
        Assert.Equal(sine[1199], y[799]);
        Assert.Equal(sine[380], y[380]);                                                  // before the fade: the original
        Assert.Equal(sine[900], y[500]);                                                  // after it: the original from c1
    }

    [Fact]
    public void TheCutKeepsStereoChannelsApartAndPowerEqualAcrossTheFade()
    {
        const int frames = 300, fade = 40;
        var x = new float[frames * 2];
        for (int i = 0; i < frames; i++) { x[2 * i] = 0.5f; x[2 * i + 1] = i < 150 ? 0.25f : -0.25f; }
        var fit = CutFit(10, fade);
        var y = GateSoundModel.ApplyCut(x, 2, fit, new GateSoundModel.Seam { C0 = 120, C1 = 200, RampDb = 0f });
        Assert.Equal((frames - 80) * 2, y.Length);
        Assert.Equal(0.25f, y[2 * 50 + 1]);
        Assert.Equal(-0.25f, y[2 * 150 + 1]);                                           // tail after the seam keeps its own right channel
        float mid = y[2 * (120 - fade / 2)];
        Assert.InRange(mid, 0.5f * 0.99f, 0.5f * 1.42f);                                // cos + sin of one level: between 1 and sqrt 2
    }

    [Fact]
    public void TheRampMeetsTheLevelAfterTheSeamAndLeavesTheHeadAlone()
    {
        var x = Enumerable.Repeat(1f, 400).ToArray();
        var seam = new GateSoundModel.Seam { C0 = 200, C1 = 300, RampDb = 6f };
        var y = GateSoundModel.ApplyCut(x, 1, CutFit(100, 20), seam);
        Assert.Equal(1f, y[99]);                                                         // before the body start: untouched
        Assert.Equal(1f, y[100], 5);                                                     // 0 dB at the body start
        Assert.Equal((float)Math.Pow(10, 6.0 * 79 / 99 / 20.0), y[179], 3);              // linear in dB from the body start: frame 179 is step 79 of 99
        Assert.Equal(1f, y[^1]);                                                         // the tail after c1 is not ramped
    }

    [Fact]
    public void ASeamThatDoesNotFitTheClipIsRefused()
    {
        var x = new float[100];
        var fit = CutFit(10, 20);
        Assert.Throws<ArgumentException>(() => GateSoundModel.ApplyCut(x, 1, fit, new GateSoundModel.Seam { C0 = 10, C1 = 50 }));     // c0 - fade < 0
        Assert.Throws<ArgumentException>(() => GateSoundModel.ApplyCut(x, 1, fit, new GateSoundModel.Seam { C0 = 60, C1 = 60 }));     // empty
        Assert.Throws<ArgumentException>(() => GateSoundModel.ApplyCut(x, 1, fit, new GateSoundModel.Seam { C0 = 60, C1 = 101 }));    // beyond the clip
        Assert.Throws<ArgumentException>(() => GateSoundModel.ApplyCut(x, 1, CutFit(60, 20), new GateSoundModel.Seam { C0 = 60, C1 = 80 }));   // body start not before c0
        Assert.Throws<ArgumentException>(() => GateSoundModel.ApplyCut(new float[101], 2, fit, new GateSoundModel.Seam { C0 = 40, C1 = 45 }));   // no whole frames
    }

    // ---- manifest ----------------------------------------------------------------------------------------------------------------

    static JObject Minimal()
    {
        return new JObject
        {
            ["schema_version"] = 1, ["map_key"] = "arena_test",
            ["rows"] = new JArray(new JObject
            {
                ["barrier"] = "B/Gate", ["part"] = null, ["at"] = new JArray(1.0, 2.0, 3.0), ["closed_position"] = null, ["clip"] = "door", ["emitter"] = new JArray(1.0, 3.0, 3.0),
                ["source_volume"] = 0.9, ["player_volume"] = 1.0, ["pitch"] = 1.0, ["min_distance"] = 0.75, ["max_distance"] = 30.0, ["mixer_db"] = -15.0, ["origin"] = "level1 X",
            }),
            ["clips"] = new JArray(new JObject
            {
                ["id"] = "door", ["file"] = "door.wav", ["channels"] = 2, ["sampleRate"] = 44100, ["frames"] = 1000, ["bytes"] = 44 + 4000, ["pitch_baked"] = false,
                ["fit"] = new JObject
                {
                    ["mode"] = "body_cut", ["event_s"] = 0.02, ["body_f"] = new JArray(100, 900), ["xfade_f"] = 10,
                    ["seams"] = new JArray(new JObject { ["remove_s"] = 0.004, ["c0f"] = 300, ["c1f"] = 476, ["ramp_db"] = 1.5, ["ok"] = true }),
                },
            }),
        };
    }

    [Fact]
    public void AValidManifestParsesIntoRowsClipsAndSeams()
    {
        var manifest = GateSoundModel.Parse(Minimal().ToString(), "arena_test");
        var row = Assert.Single(manifest.Rows);
        Assert.Null(row.Part);
        Assert.Equal(new[] { 1f, 2f, 3f }, row.At);
        Assert.Equal(0.9f, row.SourceVolume);
        var clip = manifest.ClipOf("door");
        Assert.Equal(("door.wav", 2, 44100, 1000), (clip.File, clip.Channels, clip.SampleRate, clip.Frames));
        Assert.Equal((100, 900, 10), (clip.Fit.BodyFrom, clip.Fit.BodyTo, clip.Fit.XfadeFrames));
        var seam = Assert.Single(clip.Fit.Seams);
        Assert.Equal((300, 476, 1.5f, true), (seam.C0, seam.C1, seam.RampDb, seam.Ok));
        Assert.Null(manifest.ClipOf("nope"));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("key")]
    [InlineData("unknown clip")]
    [InlineData("pitch")]
    [InlineData("distances")]
    [InlineData("volume")]
    [InlineData("emitter")]
    [InlineData("file name")]
    [InlineData("file name 2")]
    [InlineData("bytes")]
    [InlineData("fit mode")]
    [InlineData("seam outside")]
    [InlineData("seam seconds")]
    [InlineData("seam before fade")]
    [InlineData("duplicate clip")]
    [InlineData("no rows")]
    [InlineData("not json")]
    public void AManifestThePlayerCouldTripOverIsRefusedWithAReason(string defect)
    {
        var json = Minimal();
        var row = (JObject)json["rows"][0];
        var clip = (JObject)json["clips"][0];
        var seam = (JObject)clip["fit"]["seams"][0];
        switch (defect)
        {
            case "schema": json["schema_version"] = 2; break;
            case "key": json["map_key"] = "arena_other"; break;
            case "unknown clip": row["clip"] = "other"; break;
            case "pitch": row["pitch"] = 1.25; break;
            case "distances": row["max_distance"] = 0.5; break;
            case "volume": row["source_volume"] = 3.0; break;
            case "emitter": row["emitter"] = new JArray(1, 2); break;
            case "file name": clip["file"] = "../door.wav"; break;
            case "file name 2": clip["file"] = "sub\\door.wav"; break;
            case "bytes": clip["bytes"] = 100; break;
            case "fit mode": clip["fit"]["mode"] = "stretch"; break;
            case "seam outside": seam["c1f"] = 5000; break;
            case "seam seconds": seam["remove_s"] = 0.5; break;
            case "seam before fade": seam["c0f"] = 5; seam["c1f"] = 181; break;
            case "duplicate clip": ((JArray)json["clips"]).Add(clip.DeepClone()); break;
            case "no rows": json["rows"] = new JArray(); break;
            case "not json": break;
        }
        string text = defect == "not json" ? "{ nope" : json.ToString();
        Assert.Throws<InvalidDataException>(() => GateSoundModel.Parse(text, "arena_test"));
    }

    [Fact]
    public void AnAsIsClipNeedsNoTableAndALeafNameIsNeverAPath()
    {
        var json = Minimal();
        ((JObject)json["clips"][0])["fit"] = new JObject { ["mode"] = "as_is" };
        var manifest = GateSoundModel.Parse(json.ToString(), "arena_test");
        Assert.Equal(GateSoundModel.FitAsIs, manifest.ClipOf("door").Fit.Mode);
        Assert.Empty(manifest.ClipOf("door").Fit.Seams);
        Assert.True(GateSoundModel.IsLeafName("arena_bay5_big_gates.wav"));
        foreach (var bad in new[] { "", null, "..", "a/b.wav", "a\\b.wav", "C:x.wav", "a..b.wav", "/x.wav" })
            Assert.False(GateSoundModel.IsLeafName(bad), bad);
    }

    [Fact]
    public void TheRealRowSidecarsParseWithTheirClipsAndEveryRowPlaysAtPitchOne()
    {
        foreach (var stem in Maps)
        {
            var manifest = FromSidecar(stem);
            Assert.All(manifest.Rows, r => Assert.NotNull(manifest.ClipOf(r.Clip)));
            Assert.All(manifest.Rows, r => Assert.True(r.MaxDistance >= 30f && r.MinDistance < 1.5f));
        }
        Assert.Equal(9, Maps.SelectMany(s => FromSidecar(s).Clips).Select(c => c.Id).Distinct().Count());
    }

    // ---- PCM ------------------------------------------------------------------------------------------------------------------------

    static byte[] Wav(int channels, int rate, short[] samples, int bits = 16, int format = 1, bool pad = false)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + dataBytes + (pad ? 2 : 0)); w.Write("WAVE".ToCharArray());
        if (pad)
        {
            w.Write("LIST".ToCharArray()); w.Write(1); w.Write((byte)7); w.Write((byte)0);       // an odd chunk with its pad byte
        }
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)format); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)bits);
        w.Write("data".ToCharArray()); w.Write(dataBytes);
        foreach (short s in samples) w.Write(s);
        w.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void AStereoPcm16WavReadsIntoFramesAndFloats()
    {
        var samples = new short[] { 0, 16384, -16384, 32767, -32768, 1 };
        var wav = PcmWav.Read(Wav(2, 44100, samples));
        Assert.Equal((2, 44100, 3), (wav.Channels, wav.SampleRate, wav.Frames));
        Assert.Equal(samples, wav.Samples);
        var floats = PcmWav.ToFloats(wav.Samples);
        Assert.Equal(new[] { 0f, 0.5f, -0.5f, 32767f / 32768f, -1f, 1f / 32768f }, floats);
        Assert.Equal(samples, PcmWav.Read(Wav(2, 44100, samples, pad: true)).Samples);          // an extra chunk with an odd size and its pad byte is walked over
    }

    [Fact]
    public void AWavThatIsNotPcm16OrIsDamagedIsRefused()
    {
        var good = Wav(2, 44100, new short[] { 1, 2, 3, 4 });
        Assert.Throws<InvalidDataException>(() => PcmWav.Read(null));
        Assert.Throws<InvalidDataException>(() => PcmWav.Read(new byte[5]));
        Assert.Throws<InvalidDataException>(() => PcmWav.Read(Wav(2, 44100, new short[] { 1, 2 }, bits: 8)));
        Assert.Throws<InvalidDataException>(() => PcmWav.Read(Wav(2, 44100, new short[] { 1, 2 }, format: 3)));
        Assert.Throws<InvalidDataException>(() => PcmWav.Read(Wav(3, 44100, new short[] { 1, 2, 3 })));
        Assert.Throws<InvalidDataException>(() => PcmWav.Read(Wav(2, 44100, new short[] { 1, 2, 3 })));       // 3 samples are no whole stereo frames
        Assert.Throws<InvalidDataException>(() => PcmWav.Read(good.Take(good.Length - 2).ToArray()));          // the data chunk runs past the file
        var wrongMagic = (byte[])good.Clone();
        wrongMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => PcmWav.Read(wrongMagic));
    }

    [Fact]
    public void TheStagedWavsMatchTheirManifestWhenThePackageIsExtracted()
    {
        int checkedFiles = 0;
        foreach (var map in new[] { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" })
        {
            string text = RepoTextOrNull($"work/extract/gate-sounds/{map}/gate-sounds.json");
            if (text == null) continue;
            var manifest = GateSoundModel.Parse(text, map);
            foreach (var clip in manifest.Clips)
            {
                string path = null;
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && path == null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "work", "extract", "gate-sounds", map, clip.File);
                    if (File.Exists(candidate)) path = candidate;
                }
                Assert.NotNull(path);
                var bytes = File.ReadAllBytes(path);
                Assert.Equal(clip.Bytes, bytes.Length);
                var wav = PcmWav.Read(bytes);
                Assert.Equal((clip.Channels, clip.SampleRate, clip.Frames), (wav.Channels, wav.SampleRate, wav.Frames));
                // every clean seam of the table cuts the real clip without leaving it
                foreach (var seam in clip.Fit.Seams.Where(s => s.Ok))
                {
                    var cut = GateSoundModel.ApplyCut(PcmWav.ToFloats(wav.Samples), clip.Channels, clip.Fit, seam);
                    Assert.Equal(wav.Samples.Length - (seam.C1 - seam.C0) * clip.Channels, cut.Length);
                    Assert.All(cut, v => Assert.True(!float.IsNaN(v) && Math.Abs(v) < 8f));
                }
                checkedFiles++;
            }
        }
        Assert.True(checkedFiles == 0 || checkedFiles == 9, "a package of some maps only");
    }
}
