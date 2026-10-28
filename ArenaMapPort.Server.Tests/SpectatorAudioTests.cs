using ArenaMapPort.Client;
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
        Assert.InRange(clock.NextAt, 102, 105);
        var first = clock.NextAt;
        clock.Start(110); // Per-frame calls must not postpone playback forever.
        Assert.Equal(first, clock.NextAt);
        for (int i = 0; i < 1000; i++)
        {
            double now = clock.NextAt;
            Assert.True(clock.Due(now));
            clock.Played(now, i, i%13);
            Assert.InRange(clock.NextAt - now, .8, 1.6);
            Assert.False(clock.Due(now + .799));
        }
        clock.Played(10000, 1, 1); // A long stall never creates a catch-up burst.
        Assert.False(clock.Due(10000));
        Assert.InRange(clock.NextAt, 10000.8, 10001.6);
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
        Assert.InRange(clock.NextAt, 10002, 10005);
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
    public void RepeatedCallsOverlapButNeverExceedThreeConcurrentVoices()
    {
        var clock = new SpectatorAudioSchedule(27);
        var active = new List<(double End, int Speaker, int Clip)>();
        clock.Start(0);
        int peak = 0, starts = 0;
        for (int tick = 0; tick < 6000; tick++)
        {
            double now = tick / 10.0;
            active.RemoveAll(v => v.End <= now);
            Assert.False(clock.Due(now, 3));
            if (clock.Due(now, active.Count))
            {
                int speaker = clock.ChooseSpeaker(Enumerable.Repeat(20f, 99).ToArray(), 72,
                    active.Select(v => v.Speaker).ToHashSet());
                int clip = clock.ChooseClip(13, active.Select(v => v.Clip).ToHashSet());
                Assert.DoesNotContain(active, v => v.Speaker == speaker || v.Clip == clip);
                active.Add((now + 3.292, speaker, clip));
                clock.Played(now, speaker, clip);
                starts++;
            }
            peak = Math.Max(peak, active.Count);
            Assert.InRange(active.Count, 0, 3);
        }
        Assert.Equal(3, peak);
        Assert.InRange(starts, 400, 600);
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
