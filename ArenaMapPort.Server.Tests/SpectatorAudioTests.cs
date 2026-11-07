using ArenaMapPort.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class SpectatorAudioTests
{
    [Fact]
    public void CrowdClockSchedulesOverlappingCallsWithoutCatchupBursts()
    {
        var clock = new SpectatorAudioSchedule(71);
        Assert.False(clock.Due(100));
        clock.Start(100);
        Assert.InRange(clock.NextAt, 103, 108);                      // the first call 3 to 8 s after the start
        var first = clock.NextAt;
        clock.Start(110); // Per-frame calls must not postpone playback forever.
        Assert.Equal(first, clock.NextAt);
        for (int i = 0; i < 1000; i++)
        {
            double now = clock.NextAt;
            Assert.True(clock.Due(now));
            clock.Played(now, i, i%13);
            Assert.InRange(clock.NextAt - now, 4.0, 8.0);            // the starts that follow 4 to 8 s apart (2026-10-30; before 1.6 to 3.2 s)
            Assert.False(clock.Due(now + 3.999));
        }
        clock.Played(10000, 1, 1); // A long stall never creates a catch-up burst.
        Assert.False(clock.Due(10000));
        Assert.InRange(clock.NextAt, 10004, 10008);
    }

    [Fact]
    public void StopCannotSpeakUntilANewStartDelay()
    {
        var clock = new SpectatorAudioSchedule(9);
        clock.Start(0);
        clock.Stop();
        Assert.False(clock.Due(10000));
        clock.Start(10000);
        Assert.False(clock.Due(10001));
        Assert.InRange(clock.NextAt, 10003, 10008);
    }

    [Fact]
    public void CandidateSelectionRespectsReachAndAvoidsImmediateRepeats()
    {
        var clock = new SpectatorAudioSchedule(7);
        var distances = new[] { 16f, 20f, 25f, 60f, 100f, float.NaN, -1f };
        int previousSpeaker = -1, previousClip = -1;
        for (int i = 0; i < 1000; i++)
        {
            int speaker = clock.ChooseSpeaker(distances, 72);
            int clip = clock.ChooseClip(13);
            Assert.InRange(speaker, 0, 2);
            Assert.NotEqual(previousSpeaker, speaker);
            Assert.NotEqual(previousClip, clip);
            clock.Played(i*30, speaker, clip);
            previousSpeaker = speaker; previousClip = clip;
        }
        Assert.Equal(-1, clock.ChooseSpeaker(new[] { 80f, float.NaN }, 72));
        Assert.Equal(0, clock.ChooseSpeaker(new[] { 65f }, 72));
        Assert.InRange(clock.ChooseSpeaker(new[] { 90f, 110f }, 120), 0, 0);
    }

    [Fact]
    public void BusySpeakersAndVoiceFamiliesAreExcludedEvenWhenAllAreBusy()
    {
        var clock = new SpectatorAudioSchedule(9);
        var busySpeakers = new HashSet<int> {0, 1};
        var familyClips = new HashSet<int> {0, 1, 2};
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(2, clock.ChooseSpeaker(new[] {10f,11f,12f}, 72, busySpeakers));
            Assert.InRange(clock.ChooseClip(13, familyClips), 3, 12);
        }
        busySpeakers.Add(2);
        Assert.Equal(-1, clock.ChooseSpeaker(new[] {10f,11f,12f}, 72, busySpeakers));
        Assert.Equal(-1, clock.ChooseClip(3, familyClips));
    }

    [Fact]
    public void ShippedCallsNoLongerOverlapAtTheLongerPauses()
    {
        // 2026-10-30: the starts are 4.0 to 8.0 s apart and the longest shipped call is 3.292 s (arena_fan8_participating_01), so two calls never sound together
        // any more; the second voice slot is only the cap for a longer clip (the next test).
        var clock = new SpectatorAudioSchedule(27);
        var active = new List<double>();
        clock.Start(0);
        int peak = 0, starts = 0;
        for (int tick = 0; tick < 6000; tick++)
        {
            double now = tick / 10.0;
            active.RemoveAll(end => end <= now);
            if (clock.Due(now, active.Count))
            {
                active.Add(now + 3.292);
                clock.Played(now, 0, 0);
                starts++;
            }
            peak = Math.Max(peak, active.Count);
        }
        Assert.Equal(1, peak);
        Assert.InRange(starts, 90, 150);       // 600 s at a mean of 6 s between starts after a 3 to 8 s first delay
    }

    [Fact]
    public void RepeatedLongCallsOverlapButNeverExceedTwoConcurrentVoices()
    {
        var clock = new SpectatorAudioSchedule(27);
        var active = new List<(double End, int Speaker, int Clip)>();
        clock.Start(0);
        int peak = 0, starts = 0;
        for (int tick = 0; tick < 6000; tick++)
        {
            double now = tick / 10.0;
            active.RemoveAll(v => v.End <= now);
            Assert.False(clock.Due(now, 2));
            Assert.False(clock.Due(now, 3));
            if (clock.Due(now, active.Count))
            {
                int speaker = clock.ChooseSpeaker(Enumerable.Repeat(20f, 99).ToArray(), 72,
                    active.Select(v => v.Speaker).ToHashSet());
                int clip = clock.ChooseClip(13, active.Select(v => v.Clip).ToHashSet());
                Assert.DoesNotContain(active, v => v.Speaker == speaker || v.Clip == clip);
                active.Add((now + 9.0, speaker, clip));        // a call longer than the pause, to exercise the cap
                clock.Played(now, speaker, clip);
                starts++;
            }
            peak = Math.Max(peak, active.Count);
            Assert.InRange(active.Count, 0, 2);
        }
        Assert.Equal(2, peak);
        Assert.InRange(starts, 60, 110);
    }

    [Fact]
    public void NoCandidateRetriesAreBounded()
    {
        var clock = new SpectatorAudioSchedule(1);
        clock.Start(0);
        clock.Retry(20);
        Assert.False(clock.Due(22.99));
        Assert.True(clock.Due(23));
    }
    // ---- Hold: a reaction keeps the random calls quiet ------------------------------------------------------------------------------------

    [Fact]
    public void HoldPostponesTheNextCallAndNeverBringsItForward()
    {
        var clock = new SpectatorAudioSchedule(5);
        clock.Start(100);
        Assert.InRange(clock.NextAt, 103, 108);
        clock.Hold(100, 20);
        Assert.Equal(120, clock.NextAt);                         // max(NextAt, now + seconds)
        Assert.False(clock.Due(119.99));
        Assert.True(clock.Due(120));
        clock.Hold(101, 5);                                      // a shorter hold changes nothing
        Assert.Equal(120, clock.NextAt);
        clock.Hold(110, 25);                                     // a longer one extends
        Assert.Equal(135, clock.NextAt);
        clock.Hold(0, 0);
        clock.Hold(130, -4);                                     // no hold at all
        Assert.Equal(135, clock.NextAt);
        clock.Played(135, 1, 1);                                 // the next call sets its own gap as before
        Assert.InRange(clock.NextAt - 135, 4.0, 8.0);
    }

    [Fact]
    public void HoldDoesNothingWhileStoppedOrForTimesThatAreNotNumbers()
    {
        var clock = new SpectatorAudioSchedule(6);
        clock.Hold(0, 50);                                       // never started
        Assert.Equal(double.PositiveInfinity, clock.NextAt);
        Assert.False(clock.Due(1000));
        clock.Start(0);
        clock.Stop();
        clock.Hold(0, 50);                                       // stopped
        Assert.Equal(double.PositiveInfinity, clock.NextAt);
        clock.Start(10);
        double next = clock.NextAt;
        clock.Hold(double.NaN, 5);
        clock.Hold(10, double.NaN);
        clock.Hold(10, double.PositiveInfinity);
        clock.Hold(double.PositiveInfinity, 1);
        Assert.Equal(next, clock.NextAt);                        // a non-finite hold would silence the calls for ever
        Assert.True(clock.Due(next));
    }

    [Fact]
    public void HoldDoesNotTouchTheSpeakerAndClipMemoryOfTheCalls()
    {
        var a = new SpectatorAudioSchedule(7);
        var b = new SpectatorAudioSchedule(7);
        a.Start(0); b.Start(0);
        for (int i = 0; i < 50; i++)
        {
            b.Hold(i, 3);
            Assert.Equal(a.ChooseSpeaker(new[] { 10f, 11f, 12f, 13f }, 72), b.ChooseSpeaker(new[] { 10f, 11f, 12f, 13f }, 72));
            Assert.Equal(a.ChooseClip(13), b.ChooseClip(13));
        }
    }

    // ---- the manifest's reaction set -------------------------------------------------------------------------------------------------------

    private static JObject CallClip(int i) => JObject.FromObject(new
    {
        name = $"arena_fan7_participating_{i:00}", file = $"arena_fan7_participating_{i:00}.wav", channels = 1, sample_rate = 44100,
        frames = 44100, bytes = 88244, sha256 = new string('a', 64),
    });

    private static JObject ManifestJson(bool calls = true, bool bed = true, bool reactions = true)
    {
        var root = JObject.FromObject(new { schema = 1, bank = "Participating", trigger = 1123, source_bundle = "x.bundle", external_entries_omitted = 9 });
        if (calls) root["clips"] = new JArray(Enumerable.Range(0, 13).Select(CallClip));
        if (bed)
            root["bed"] = JObject.FromObject(new
            {
                name = "arena_fans_ambient_neutral", file = "arena_fans_ambient_neutral.wav", channels = 1, sample_rate = 44100, frames = 3505950, bytes = 7011944,
                sha256 = new string('b', 64), seam_crossfade_ms = 500,
            });
        // the shape tools/extract/extract_spectator_audio.py writes: the real extractor adds statistics, processing records and left-out names that the client ignores
        if (reactions)
        {
            var set = JObject.FromObject(ReactionSetBuilder.PhaseA());
            foreach (var clip in set["clips"]!) { clip["peak"] = 0.5; clip["rms"] = 0.07; clip["source_id"] = 123456789012L; clip["processing"] = JObject.FromObject(new { gain_db = 1.5 }); }
            foreach (var bank in set["banks"]!) bank["left_out"] = JArray.FromObject(new[] { new { name = "arena_fan9_kill_cheer_99", status = "listen", reason = "FLAG" } });
            root["reactions"] = set;
        }
        return root;
    }

    private static SpectatorAudioManifest Parse(JObject json) => JsonConvert.DeserializeObject<SpectatorAudioManifest>(json.ToString())!;

    [Fact]
    public void AManifestWithAReactionSetParsesAndEverySetValidatesOnItsOwn()
    {
        var manifest = Parse(ManifestJson());
        manifest.Validate();
        manifest.ValidateBed();
        manifest.ValidateReactions();
        Assert.Equal(70, manifest.reactions.clips.Length);
        Assert.Equal(1, manifest.reactions.schema);
        Assert.Equal(308700, manifest.reactions.max_frames);
        Assert.Equal(5, manifest.reactions.banks.Length);        // the synthetic set has no Owned bank (Cheer carries the approve clips)
        Assert.Equal(13, manifest.clips.Length);
        Assert.NotNull(manifest.bed);
    }

    [Fact]
    public void AManifestWithoutAReactionSetIsStillValidAndHasNoReactions()
    {
        var manifest = Parse(ManifestJson(reactions: false));
        manifest.Validate();
        manifest.ValidateBed();
        Assert.Null(manifest.reactions);
        Assert.Throws<InvalidDataException>(manifest.ValidateReactions);
        Assert.Throws<InvalidDataException>(manifest.ValidateReactionBeds);       // the roar beds are a set of their own (SpectatorReactionBedModelTests)
    }

    [Fact]
    public void ADamagedCallSetLeavesTheReactionsAndTheBedValid()
    {
        var damaged = ManifestJson();
        damaged["clips"]![0]!["file"] = "../outside.wav";
        var manifest = Parse(damaged);
        Assert.Throws<InvalidDataException>(manifest.Validate);
        manifest.ValidateReactions();
        manifest.ValidateBed();

        var noCalls = Parse(ManifestJson(calls: false));
        Assert.Throws<InvalidDataException>(noCalls.Validate);
        noCalls.ValidateReactions();
        noCalls.ValidateBed();

        var damagedBed = ManifestJson();
        damagedBed["bed"]!["frames"] = 5;
        var withBadBed = Parse(damagedBed);
        Assert.Throws<InvalidDataException>(withBadBed.ValidateBed);
        withBadBed.Validate();
        withBadBed.ValidateReactions();
    }

    [Theory]
    [InlineData("schema", 2)]
    [InlineData("max_frames", 220500)]
    [InlineData("max_frames", 0)]
    [InlineData("banks", null)]
    [InlineData("clips", null)]
    public void ADamagedReactionSetLeavesTheCallsAndTheBedValid(string field, object? value)
    {
        var json = ManifestJson();
        json["reactions"]![field] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
        var manifest = Parse(json);
        Assert.Throws<InvalidDataException>(manifest.ValidateReactions);
        manifest.Validate();
        manifest.ValidateBed();
    }

    [Theory]
    [InlineData("name", "arena_fan7_participating_01")]       // a call is not a reaction
    [InlineData("name", "arena_fan7_kill_cheer_1")]
    [InlineData("name", "arena_fan123_kill_cheer_01")]
    [InlineData("name", "arena_fan7_low_death_intensity_01")]  // Lull is not exported in phase A
    [InlineData("name", "arena_fan7_sweep_team_01")]
    [InlineData("name", "../arena_fan7_kill_cheer_01")]
    [InlineData("file", "../outside.wav")]
    [InlineData("file", "other.wav")]
    [InlineData("channels", 2)]
    [InlineData("sample_rate", 48000)]
    [InlineData("frames", 22049)]
    [InlineData("frames", 308701)]
    [InlineData("frames", 1000)]
    [InlineData("bytes", 12345)]
    [InlineData("sha256", "ABCDEF")]
    [InlineData("sha256", null)]
    [InlineData("pool", "sided")]
    [InlineData("pool", "hype")]
    [InlineData("pool", null)]
    [InlineData("pool", "lament")]                            // not a tag of the clip's bank (Cheer)
    [InlineData("group", "disapprove-01")]                    // the prefix of another kind
    [InlineData("group", "approve-1")]
    [InlineData("group", "approve-001")]
    [InlineData("group", "Approve-01")]
    [InlineData("group", null)]
    public void ADamagedReactionClipIsRefusedWithoutTouchingTheCallsOrTheBed(string field, object? value)
    {
        var json = ManifestJson();
        // the first clip by name is an approve clip of the Cheer bank (praise or gloat): see ReactionSetBuilder
        var clip = (JObject)json["reactions"]!["clips"]!.First(c => (string?)c["pool"] == "praise")!;
        clip[field] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
        var manifest = Parse(json);
        Assert.Throws<InvalidDataException>(manifest.ValidateReactions);
        manifest.Validate();
        manifest.ValidateBed();
    }

    [Fact]
    public void ADamagedReactionSetOfAnotherKindIsRefused()
    {
        void Damaged(Action<JObject> damage)
        {
            var json = ManifestJson();
            damage((JObject)json["reactions"]!);
            Assert.Throws<InvalidDataException>(Parse(json).ValidateReactions);
        }
        Damaged(r => r["clips"] = new JArray());                                                    // nothing to play
        Damaged(r => r["banks"] = new JArray());
        Damaged(r => ((JArray)r["clips"]!)[0] = JValue.CreateNull());                              // a null entry
        Damaged(r => ((JArray)r["clips"]!)[1] = r["clips"]![0]!.DeepClone());                      // a duplicated name
        Damaged(r => ((JArray)r["banks"]!)[0] = JValue.CreateNull());
        Damaged(r => r["banks"]![0]!["trigger"] = 1120);                                            // another bank's trigger
        Damaged(r => r["banks"]![0]!["bank"] = "Neutral");                                          // not a reaction bank
        Damaged(r => r["banks"]![0]!["bank"] = r["banks"]![1]!["bank"]!.DeepClone());              // a bank twice
        Damaged(r => r["banks"]![0]!["clips"] = JValue.CreateNull());
        Damaged(r => r["banks"]![0]!["embedded"] = 1);                                              // fewer embedded clips than the bank lists
        Damaged(r => ((JArray)r["banks"]![0]!["clips"]!).Add("arena_fan7_kill_cheer_98"));         // a name that is not in the table
        Damaged(r => ((JArray)r["banks"]![0]!["clips"]!).Add(r["banks"]![0]!["clips"]![0]!.DeepClone()));   // a name twice in one bank
        Damaged(r => ((JArray)r["banks"]![0]!["clips"]!).RemoveAt(0));                              // a table clip that no bank lists
        Damaged(r => ((JArray)r["banks"]![1]!["clips"]!).Add(r["banks"]![0]!["clips"]![0]!.DeepClone()));   // a Cheer clip listed under Angry
        Damaged(r => r["banks"] = new JArray(Enumerable.Range(0, 7).Select(i => r["banks"]![0]!.DeepClone())));   // more banks than there are
    }

    [Fact]
    public void ARealisticallyShapedSetWithOwnedAndTheExtractorsExtraKeysValidates()
    {
        var json = ManifestJson();
        var reactions = (JObject)json["reactions"]!;
        var cheer = (JObject)reactions["banks"]!.First(b => (string?)b["bank"] == "Cheer")!;
        var owned = (JObject)cheer.DeepClone();
        owned["bank"] = "Owned";
        owned["trigger"] = 1120;
        ((JArray)reactions["banks"]!).Insert(1, owned);                                              // Cheer and Owned list the same names
        reactions["future_key"] = "ignored";
        var manifest = Parse(json);
        manifest.ValidateReactions();
        Assert.Equal(6, manifest.reactions.banks.Length);
    }

    [Fact]
    public void ReactionsNeedTheSamePackageIdentityAsTheCallsAndTheBed()
    {
        foreach (var (field, value) in new (string, object)[] { ("schema", 2), ("trigger", 1124), ("bank", "Neutral") })
        {
            var json = ManifestJson();
            json[field] = JToken.FromObject(value);
            Assert.Throws<InvalidDataException>(Parse(json).ValidateReactions);
        }
    }

    [Fact]
    public void ThePoolTagTablesAreOneVocabularyForTheManifestAndTheModel()
    {
        Assert.Equal(new[] { "Cheer", "Owned", "Angry", "ChangedRound", "EndRound", "Hide" }, SpectatorReactionTags.BankNames);
        Assert.Equal(new[] { 1119, 1120, 1121, 1125, 1126, 1124 }, SpectatorReactionTags.BankNames.Select(SpectatorReactionTags.TriggerOf));
        Assert.Equal(0, SpectatorReactionTags.TriggerOf("Participating"));
        foreach (var (tag, prefix) in new[] { ("praise", "approve"), ("gloat", "approve"), ("shout", "approve"), ("urge", "approve"), ("lament", "disapprove"), ("heckle", "disapprove"),
                     ("start", "start"), ("finish", "finish"), ("panic", "takecover"), ("directed", "takecover") })
            Assert.Equal(prefix, SpectatorReactionTags.GroupPrefix(tag));
        Assert.All(new[] { "lull", "hype", "sided", "", "Praise" }, t => Assert.Null(SpectatorReactionTags.GroupPrefix(t)));
        Assert.False(SpectatorReactionTags.BankAllows("Cheer", "lament"));
        Assert.False(SpectatorReactionTags.BankAllows("Participating", "praise"));
        Assert.True(SpectatorReactionTags.BankAllows("Owned", "urge"));
        Assert.True(SpectatorReactionTags.BankAllows("Hide", "directed"));
    }

    [Fact]
    public void RuntimePackageRejectsTraversalDuplicatesAndExcessData()
    {
        var manifest = new SpectatorAudioManifest { schema=1, bank="Participating", trigger=1123,
            clips=Enumerable.Range(0,13).Select(i => new SpectatorAudioClip {
                name=$"arena_fan7_participating_{i:00}", file=$"arena_fan7_participating_{i:00}.wav",
                channels=1, sample_rate=44100, frames=44100, bytes=88244, sha256=new string('a',64)
            }).ToArray() };
        manifest.Validate();
        manifest.clips[0].file = "../outside.wav";
        Assert.Throws<InvalidDataException>(manifest.Validate);
        manifest.clips[0] = manifest.clips[1];
        Assert.Throws<InvalidDataException>(manifest.Validate);
    }
}
