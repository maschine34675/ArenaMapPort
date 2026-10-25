using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The Unity-free rules the monitor faces stand on (ScoreMonitorModel, ScoreMonitorFrames, ScoreMonitorDemo; docs/progress/score-monitors.md): the clock (rounding like
/// ArenaMode's HUD, the 99:59 clamp, exact seconds at an absolute Time.time in the hours), the text of a number, which toggles are lit, which rows of the four maps get a
/// surface, and the demo cycle that lets every face be looked at on the host.
/// </summary>
public sealed class ScoreMonitorModelTests
{
    // ---- the clock -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 12f, 100f, -1)]          // no clock
    [InlineData(1, 0f, 100f, 0)]            // frozen
    [InlineData(1, 59f, 100f, 59)]
    [InlineData(1, 541f, 3f, 541)]          // a frozen value ignores the time
    [InlineData(1, 0.2f, 0f, 1)]            // rounded up like a countdown
    [InlineData(1, -5f, 0f, 0)]
    [InlineData(1, 7200f, 0f, 5999)]        // 120:00 clamps to 99:59
    [InlineData(2, 110f, 100f, 10)]         // counting down to the absolute time 110
    [InlineData(2, 110f, 109.2f, 1)]        // 0.8 s left shows 1, like the HUD's Math.Ceiling
    [InlineData(2, 110f, 110f, 0)]
    [InlineData(2, 110f, 130f, 0)]          // past the deadline: clamped at 0
    [InlineData(2, 100000f, 0f, 5999)]
    [InlineData(3, 100f, 130f, 30)]         // counting up from the absolute time 100
    [InlineData(3, 100f, 130.9f, 30)]       // rounded down
    [InlineData(3, 100f, 90f, 0)]           // before the start: 0
    [InlineData(3, 0f, 100000f, 5999)]
    [InlineData(9, 100f, 130f, -1)]         // an unknown mode is no clock
    public void TheClockSecondIsTheWholeSecondTheTimerFacesShow(int mode, float seconds, float now, int expected)
    {
        Assert.Equal(expected, ScoreMonitorModel.ClockSecond(mode, seconds, now));
    }

    [Fact]
    public void ANonFiniteClockValueNeverThrowsAndCountsAsZero()
    {
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Equal(0, ScoreMonitorModel.ClockSecond(MonitorClockMode.Frozen, bad, 5f));
            Assert.Equal(0, ScoreMonitorModel.ClockSecond(MonitorClockMode.CountDown, bad, 5f));
            Assert.Equal(5, ScoreMonitorModel.ClockSecond(MonitorClockMode.CountUp, bad, 5f));
        }
    }

    [Fact]
    public void AnAbsoluteTimeInTheHoursStillGivesExactSeconds()
    {
        // 90000 s of simulation time (25 h) is above float's 2^24 whole-second precision limit for fractions, but a 30 s countdown must still read 30, 29, ...
        float end = 90030f;
        Assert.Equal(30, ScoreMonitorModel.ClockSecond(MonitorClockMode.CountDown, end, 90000f));
        Assert.Equal(29, ScoreMonitorModel.ClockSecond(MonitorClockMode.CountDown, end, 90001f));
        Assert.Equal(1, ScoreMonitorModel.ClockSecond(MonitorClockMode.CountDown, end, 90029f));
        Assert.Equal(0, ScoreMonitorModel.ClockSecond(MonitorClockMode.CountDown, end, 90030f));
    }

    [Fact]
    public void ACountdownNeverSkipsAndNeverRepeatsASecond()
    {
        var seen = new List<int>();
        for (float now = 1000f; now <= 1010.5f; now += 1f / 60f)
        {
            int second = ScoreMonitorModel.ClockSecond(MonitorClockMode.CountDown, 1010f, now);
            if (seen.Count == 0 || seen[^1] != second) seen.Add(second);
        }
        Assert.Equal(Enumerable.Range(0, 11).Reverse().ToList(), seen);
    }

    [Theory]
    [InlineData(-1, "00", "00")]
    [InlineData(0, "00", "00")]
    [InlineData(1, "00", "01")]
    [InlineData(59, "00", "59")]
    [InlineData(60, "01", "00")]
    [InlineData(541, "09", "01")]
    [InlineData(3599, "59", "59")]
    [InlineData(5999, "99", "59")]
    public void TheClockReadsMinutesAndSecondsInTwoDigits(int clockSecond, string minutes, string seconds)
    {
        Assert.Equal(minutes, ScoreMonitorModel.MinutesText(clockSecond));
        Assert.Equal(seconds, ScoreMonitorModel.SecondsText(clockSecond));
    }

    [Fact]
    public void TheClockCapIs9959()
    {
        Assert.Equal(5999, ScoreMonitorModel.MaxClockSeconds);
        Assert.Equal(9999, ScoreMonitorModel.MaxScore);
        Assert.Equal(16, ScoreMonitorModel.MaxTeamSize);
    }

    // ---- frames --------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, 5, 5)]
    [InlineData(3, 5, 3)]
    [InlineData(0, 5, 0)]
    [InlineData(-1, 5, 0)]     // unknown: every toggle off
    [InlineData(9, 5, 5)]      // never more than the team size
    [InlineData(16, 16, 16)]
    [InlineData(4, 0, 0)]      // a team size of 0 draws no toggle
    [InlineData(4, 99, 4)]
    public void TheLitTogglesAreTheAliveCountLimitedByTheTeamSize(int alive, int teamSize, int lit)
    {
        Assert.Equal(lit, ScoreMonitorFrames.Lit(alive, teamSize));
    }

    [Fact]
    public void AFrameCarriesTheFormattedValues()
    {
        var s = new ScoreMonitorSnapshot { Claim = 1, ColorA = 0x7FDBFF, ColorB = -1, TeamSize = 16, ScoreA = 12, ScoreB = 10000, AliveA = 11, AliveB = -1 };
        var f = ScoreMonitorFrames.Build(s, 541);
        Assert.Equal("09", f.Minutes);
        Assert.Equal(":", f.Colon);
        Assert.Equal("01", f.Seconds);
        Assert.Equal("12", f.ScoreA);
        Assert.Equal("9999", f.ScoreB);
        Assert.Equal(0x7FDBFF, f.ColorA);
        Assert.Equal(-1, f.ColorB);
        Assert.Equal(16, f.TeamSize);
        Assert.Equal(11, f.LitA);
        Assert.Equal(0, f.LitB);
        Assert.Equal("00", ScoreMonitorFrames.Build(s, -1).Minutes);
    }

    [Fact]
    public void ADigitIsNeverSignedOrPadded()
    {
        foreach (int v in new[] { 0, 9, 99, 999, 9999 })
        {
            var f = ScoreMonitorFrames.Build(new ScoreMonitorSnapshot { ScoreA = v, ScoreB = v }, 0);
            Assert.Equal(v.ToString(), f.ScoreA);
            Assert.Equal(v.ToString(), f.ScoreB);
        }
    }

    // ---- which rows get a surface --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("arena_equator", 11, 9)]
    [InlineData("arena_bay5", 8, 8)]
    [InlineData("arena_airpit", 8, 6)]
    [InlineData("arena_bowl", 16, 6)]
    public void OnlyTheActiveRowsWithAFaceAreUsable(string key, int rows, int usable)
    {
        var map = ArenaMapRegistry.All.Single(m => m.Key == key);
        Assert.Equal(rows, map.ScoreMonitors.Length);
        var rowsUsable = ScoreMonitorModel.UsableRows(map.ScoreMonitors);
        Assert.Equal(usable, rowsUsable.Length);
        Assert.All(rowsUsable, r =>
        {
            Assert.True(r.ActiveInScene);
            Assert.NotEqual(ScoreMonitorFace.None, ScoreMonitorLayout.FaceOf(r.ArenaType));
        });
    }

    [Fact]
    public void TheDarkRowsAreTheInactiveOnesAndTheWordsWithoutAFace()
    {
        var equator = ArenaMapRegistry.All.Single(m => m.Key == "arena_equator");
        var dark = equator.ScoreMonitors.Except(ScoreMonitorModel.UsableRows(equator.ScoreMonitors)).ToList();
        Assert.Equal(2, dark.Count);
        Assert.All(dark, r => Assert.Equal(7, r.ArenaType));   // the two small TeamFight boards (player boards)

        var airpit = ArenaMapRegistry.All.Single(m => m.Key == "arena_airpit");
        var inactive = airpit.ScoreMonitors.Where(r => !r.ActiveInScene).ToList();
        Assert.Equal(2, inactive.Count);   // the two pocket screens stay dark
        Assert.DoesNotContain(inactive, r => ScoreMonitorModel.UsableRows(airpit.ScoreMonitors).Contains(r));

        var bowl = ArenaMapRegistry.All.Single(m => m.Key == "arena_bowl");
        Assert.Equal(10, bowl.ScoreMonitors.Count(r => !r.ActiveInScene));   // the inactive base set
        Assert.All(ScoreMonitorModel.UsableRows(bowl.ScoreMonitors), r => Assert.Equal("Arena_Bowl_TeamFight_DesignStuff", r.Scene));
    }

    [Fact]
    public void EveryReleasedMapHasUsableMonitorsSoAClaimCanSucceed()
    {
        foreach (var map in ArenaMapRegistry.All)
            Assert.True(ScoreMonitorModel.UsableRows(map.ScoreMonitors).Length > 0, map.Key);
        Assert.Null(ScoreMonitorModel.UsableRows(null).FirstOrDefault());
    }

    // ---- the demo ------------------------------------------------------------------------------------------------------------------------

    private static ScoreMonitorDemo.Step Step(float elapsed, float start = 1000f) => ScoreMonitorDemo.Cycle(elapsed, start);

    [Fact]
    public void TheDemoCycleIs70SecondsInFourSections()
    {
        Assert.Equal(70f, ScoreMonitorDemo.CycleSeconds);
        Assert.Equal(10f, ScoreMonitorDemo.DigitsSeconds);
        Assert.Equal(25f, ScoreMonitorDemo.Match5Seconds);
        Assert.Equal(25f, ScoreMonitorDemo.Match16Seconds);
        Assert.Equal(10f, ScoreMonitorDemo.ResultSeconds);
    }

    [Fact]
    public void TheDigitsSectionStepsEveryScoreWidthAndTheClockClamp()
    {
        var scores = new List<int>();
        var clocks = new List<int>();
        for (float t = 0.5f; t < 10f; t += 2f)
        {
            var s = Step(t);
            Assert.Equal(5, s.TeamSize);
            Assert.Equal(-1, s.ColorA);
            Assert.Equal(MonitorClockMode.Frozen, s.ClockMode);
            scores.Add(s.ScoreA);
            clocks.Add(ScoreMonitorModel.ClockSecond(s.ClockMode, s.ClockSeconds, 0f));
        }
        Assert.Equal(new[] { 0, 9, 99, 999, 9999 }, scores);
        Assert.Equal(new[] { 0, 59, 600, 5999, 3599 }, clocks);   // 7200 clamps to 99:59
        Assert.Equal(-1, Step(9f).AliveA);                        // the last slot has unknown alive counts: every toggle off
    }

    [Fact]
    public void TheMatchSectionsCoverBothTeamSizesBothColourSetsAndBothCountingClocks()
    {
        var five = Step(15f);
        Assert.Equal(5, five.TeamSize);
        Assert.Equal(-1, five.ColorA);
        Assert.Equal(MonitorClockMode.CountDown, five.ClockMode);
        var sixteen = Step(40f);
        Assert.Equal(16, sixteen.TeamSize);
        Assert.Equal(ScoreMonitorDemo.HudCyan, sixteen.ColorA);
        Assert.Equal(ScoreMonitorDemo.HudRed, sixteen.ColorB);
        Assert.Equal(MonitorClockMode.CountUp, sixteen.ClockMode);
        var result = Step(65f);
        Assert.Equal(MonitorClockMode.Frozen, result.ClockMode);
        Assert.Equal(0f, result.ClockSeconds);
        Assert.Equal(16, result.AliveA);
    }

    [Fact]
    public void TheDemoClocksRunAgainstTheCycleStartOfTheirOwnCycle()
    {
        // the countdown of the first match section ends 35 s into the cycle, the count-up of the second one starts there
        Assert.Equal(1000f + 35f, Step(15f).ClockSeconds);
        Assert.Equal(1000f + 35f, Step(40f).ClockSeconds);
        Assert.Equal(20, ScoreMonitorModel.ClockSecond(Step(15f).ClockMode, Step(15f).ClockSeconds, 1015f));
        Assert.Equal(5, ScoreMonitorModel.ClockSecond(Step(40f).ClockMode, Step(40f).ClockSeconds, 1040f));
        // the second cycle uses its own start
        Assert.Equal(1000f + 70f + 35f, Step(70f + 15f).ClockSeconds);
    }

    [Fact]
    public void TheDemoIsDeterministicAndStaysInsideTheContractRanges()
    {
        for (float t = 0f; t < 3 * ScoreMonitorDemo.CycleSeconds; t += 0.37f)
        {
            var a = Step(t);
            var b = Step(t);
            Assert.Equal(a, b);
            Assert.InRange(a.ScoreA, 0, ScoreMonitorModel.MaxScore);
            Assert.InRange(a.ScoreB, 0, ScoreMonitorModel.MaxScore);
            Assert.InRange(a.TeamSize, 0, ScoreMonitorModel.MaxTeamSize);
            Assert.InRange(a.AliveA, -1, a.TeamSize);
            Assert.InRange(a.AliveB, -1, a.TeamSize);
            Assert.True(MonitorClockMode.IsValid(a.ClockMode));
            Assert.True(a.ClockMode != MonitorClockMode.None);
        }
    }

    [Fact]
    public void TheDemoRepeatsAndSurvivesBadInput()
    {
        var first = Step(3f);
        var second = Step(3f + ScoreMonitorDemo.CycleSeconds, 1000f);
        Assert.Equal(first.ScoreA, second.ScoreA);
        Assert.Equal(first.ClockMode, second.ClockMode);
        Assert.Equal(Step(0f), Step(-5f));
        Assert.Equal(Step(0f), Step(float.NaN));
        Assert.Equal(Step(0f), Step(float.PositiveInfinity));
    }
}
