#nullable disable
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The Unity-free core of the rain-drops check (RainUnderRoofRepairs, docs/progress/two-maps-item-6-raincover.md "2026-11-21"): the depth-to-height
/// conversion, the texel mapping, the readback window, the orientation probe and the state that turns a window result into the flag.
/// </summary>
public sealed class RainUnderRoofModelTests
{
    // A 4096 px photo over 409.6 m starting at (-200, -200): 0.1 m per texel; the depth camera looks down from y 48 over 60 m.
    static RainPhoto Photo(bool reversedZ = true, int dim = 4096, float metres = 409.6f) =>
        new RainPhoto(-200f, -200f, 1f / metres, 1f / metres, dim, 48f, 60f, reversedZ);

    static float RawAt(float height, bool reversedZ, float top = 48f, float far = 60f) =>
        reversedZ ? 1f - (top - height) / far : (top - height) / far;

    // ---- conversion ----

    [Fact]
    public void ReversedDepthConvertsToTheWorldHeight()
    {
        Assert.Equal(48f, RainUnderRoofModel.Height(1f, 48f, 40f, true), 3);
        Assert.Equal(28f, RainUnderRoofModel.Height(0.5f, 48f, 40f, true), 3);
        Assert.Equal(8f, RainUnderRoofModel.Height(0f, 48f, 40f, true), 3);
    }

    [Fact]
    public void ForwardDepthConvertsToTheWorldHeight()
    {
        Assert.Equal(48f, RainUnderRoofModel.Height(0f, 48f, 40f, false), 3);
        Assert.Equal(38f, RainUnderRoofModel.Height(0.25f, 48f, 40f, false), 3);
    }

    [Fact]
    public void TheClearedDepthMeansNothingPhotographedInBothConventions()
    {
        Assert.True(RainUnderRoofModel.IsNothing(0f, true));
        Assert.False(RainUnderRoofModel.IsNothing(0.3f, true));
        Assert.True(RainUnderRoofModel.IsNothing(1f, false));
        Assert.False(RainUnderRoofModel.IsNothing(0.9f, false));
    }

    [Fact]
    public void TheF8ReadbackHelpersAreTheModelsFormulas()
    {
        // RainCoverRepairs forwards to the model: one tested formula for the logged numbers and the check
        string source = File.ReadAllText(Path.Combine(ClientDir(), "RainCoverRepairs.cs"));
        Assert.Contains("RainUnderRoofModel.Height(raw, camTopY, farClip, reversedZ)", source);
        Assert.Contains("RainUnderRoofModel.IsNothing(raw, reversedZ)", source);
        Assert.Contains("RainUnderRoofModel.TexelClamped(", source);
    }

    // ---- texel mapping ----

    [Fact]
    public void ATexelIsFoundFromTheWorldPoint()
    {
        var photo = Photo();
        Assert.True(RainUnderRoofModel.TryTexel(photo, -200f, -200f, out int px, out int py));
        Assert.Equal((0, 0), (px, py));
        Assert.True(RainUnderRoofModel.TryTexel(photo, 0f, 0f, out px, out py));
        Assert.Equal((2000, 2000), (px, py));
        Assert.True(RainUnderRoofModel.TryTexel(photo, 209.5f, -199.95f, out px, out py));
        Assert.Equal((4095, 0), (px, py));
    }

    [Fact]
    public void APointOutsideThePhotoHasNoTexelAndTheEdgesAreExact()
    {
        var photo = Photo();
        Assert.False(RainUnderRoofModel.TryTexel(photo, -200.01f, 0f, out _, out _));
        Assert.False(RainUnderRoofModel.TryTexel(photo, 0f, -200.01f, out _, out _));
        Assert.False(RainUnderRoofModel.TryTexel(photo, 209.6f, 0f, out _, out _)); // u == 1 is outside
        Assert.False(RainUnderRoofModel.TryTexel(photo, 0f, 209.6f, out _, out _));
        Assert.False(RainUnderRoofModel.TryTexel(photo, float.NaN, 0f, out _, out _));
        Assert.True(RainUnderRoofModel.TryTexel(photo, 209.59f, 209.59f, out int px, out int py));
        Assert.Equal((4095, 4095), (px, py));
    }

    [Fact]
    public void TheClampedMappingIsTheF8ReadbacksRule()
    {
        RainUnderRoofModel.TexelClamped(-200f, -200f, 1f / 409.6f, 1f / 409.6f, 4096, -500f, 900f, out int px, out int py);
        Assert.Equal((0, 4095), (px, py));
        RainUnderRoofModel.TexelClamped(-200f, -200f, 1f / 409.6f, 1f / 409.6f, 4096, 0f, 0f, out px, out py);
        Assert.Equal((2000, 2000), (px, py));
    }

    [Fact]
    public void ThePhotoIsUsableOnlyWithSaneParameters()
    {
        Assert.True(Photo().IsUsable);
        Assert.False(new RainPhoto(0, 0, 0f, 1f, 4096, 48f, 40f, true).IsUsable);
        Assert.False(new RainPhoto(0, 0, 1f, 1f, 4096, 48f, 0f, true).IsUsable);
        Assert.False(new RainPhoto(0, 0, 1f, 1f, 0, 48f, 40f, true).IsUsable);
        Assert.False(new RainPhoto(0, 0, 1f, 1f, 100000, 48f, 40f, true).IsUsable);
        Assert.False(new RainPhoto(0, 0, 1f, 1f, 4096, float.NaN, 40f, true).IsUsable);
        Assert.False(default(RainPhoto).IsUsable);
    }

    // ---- window ----

    [Theory]
    [InlineData(409.6f, 3)]   // 0.1 m per texel: ceil(2.5) = 3
    [InlineData(2048f, 1)]    // 0.5 m per texel: one texel is already wider than the radius
    [InlineData(81.92f, 3)]   // 0.02 m per texel: capped
    [InlineData(1024f, 1)]    // 0.25 m per texel: exactly one
    public void TheWindowRadiusFollowsTheTexelSizeWithinItsBounds(float metres, int expected)
    {
        Assert.Equal(expected, RainUnderRoofModel.RadiusTexels(Photo(metres: metres)));
    }

    [Fact]
    public void TheWindowIsCentredOnTheCamerasTexel()
    {
        Assert.True(RainUnderRoofModel.TryPlanWindow(Photo(), RainProbeOrientation.AsWritten, 0f, 0f, 7, 1, out var w));
        Assert.Equal((1997, 1997, 7, 7), (w.X, w.Y, w.Width, w.Height));
        Assert.Equal((2000, 2000), (w.CenterPx, w.CenterPy));
        Assert.Equal(49, w.Count);
        Assert.Equal(7, w.Generation);
    }

    [Fact]
    public void TheWindowIsClippedAtThePhotoEdges()
    {
        Assert.True(RainUnderRoofModel.TryPlanWindow(Photo(), RainProbeOrientation.AsWritten, -199.95f, 209.55f, 1, 1, out var w));
        Assert.Equal(0, w.X);
        Assert.Equal(4 , w.Width);          // texels 0..3
        Assert.Equal(4092, w.Y);
        Assert.Equal(4, w.Height);          // texels 4092..4095
        Assert.True(w.X >= 0 && w.X + w.Width <= 4096 && w.Y >= 0 && w.Y + w.Height <= 4096);
    }

    [Fact]
    public void AFlippedReadbackAsksForTheSameRowsFromTheOtherEnd()
    {
        Assert.True(RainUnderRoofModel.TryPlanWindow(Photo(), RainProbeOrientation.FlippedY, 0f, 0f, 1, 1, out var w));
        // logical rows 1997..2003 sit at physical rows 4095-2003 .. 4095-1997
        Assert.Equal(4095 - 2003, w.Y);
        Assert.Equal(7, w.Height);
        Assert.True(RainUnderRoofModel.TryPlanWindow(Photo(), RainProbeOrientation.FlippedY, -199.95f, -199.95f, 1, 1, out w));
        Assert.Equal(4095 - 3, w.Y);
        Assert.Equal(4, w.Height);
    }

    [Fact]
    public void NoWindowWithoutAUsablePhotoAnOrientationOrAPointInsideThePhoto()
    {
        Assert.False(RainUnderRoofModel.TryPlanWindow(Photo(), RainProbeOrientation.Unusable, 0f, 0f, 1, 1, out _));
        Assert.False(RainUnderRoofModel.TryPlanWindow(default, RainProbeOrientation.AsWritten, 0f, 0f, 1, 1, out _));
        Assert.False(RainUnderRoofModel.TryPlanWindow(Photo(), RainProbeOrientation.AsWritten, 500f, 0f, 1, 1, out _));
    }

    // ---- window result ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTopSurfaceIsTheHighestPhotographedTexel(bool reversedZ)
    {
        var photo = Photo(reversedZ);
        float nothing = reversedZ ? 0f : 1f;
        var values = new[] { nothing, RawAt(6.2f, reversedZ), RawAt(30.5f, reversedZ), nothing, RawAt(12f, reversedZ) };
        Assert.Equal(30.5f, RainUnderRoofModel.TopHeight(photo, values, values.Length), 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWindowOfNothingIsOpenSky(bool reversedZ)
    {
        var values = new float[49];
        System.Array.Fill(values, reversedZ ? 0f : 1f);
        Assert.True(float.IsNaN(RainUnderRoofModel.TopHeight(Photo(reversedZ), values, 49)));
    }

    [Fact]
    public void OnlyTheCountedSamplesAndFiniteValuesTakePart()
    {
        var photo = Photo();
        var values = new[] { RawAt(10f, true), RawAt(40f, true), float.NaN, float.PositiveInfinity };
        Assert.Equal(10f, RainUnderRoofModel.TopHeight(photo, values, 1), 2);
        Assert.Equal(40f, RainUnderRoofModel.TopHeight(photo, values, 4), 2);
        Assert.True(float.IsNaN(RainUnderRoofModel.TopHeight(photo, null, 3)));
    }

    [Fact]
    public void CoverAtOrJustBelowHeadHeightCountsAsARoof()
    {
        float m = RainUnderRoofModel.CoverMargin;
        Assert.Equal(-0.1f, m);
        Assert.False(RainUnderRoofModel.IsCovered(float.NaN, 1.7f, m));
        Assert.False(RainUnderRoofModel.IsCovered(0f, 1.7f, m));        // the ground below
        Assert.False(RainUnderRoofModel.IsCovered(1.0f, 1.7f, m));      // a low table or crate
        Assert.False(RainUnderRoofModel.IsCovered(1.6f, 1.7f, m));      // exactly at the margin: not a roof
        Assert.True(RainUnderRoofModel.IsCovered(1.61f, 1.7f, m));      // head height or just below: leans to roof
        Assert.True(RainUnderRoofModel.IsCovered(1.9f, 1.7f, m));       // a thin overhang just above the head
        Assert.True(RainUnderRoofModel.IsCovered(6.2f, 1.7f, m));       // a hall ceiling
        Assert.False(RainUnderRoofModel.IsCovered(5f, float.NaN, m));
    }

    // ---- orientation probe ----

    static float[] RegionData(int logicalRow, int skew = 0)
    {
        var data = new float[RainUnderRoofModel.ProbeRegionWidth];
        for (int i = 0; i < data.Length; i++)
            data[i] = RainUnderRoofModel.ProbeValue(RainUnderRoofModel.ProbeRegionX + i + skew, logicalRow);
        return data;
    }

    [Fact]
    public void TheProbeTexelsAreAllDifferent()
    {
        var seen = new HashSet<float>();
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.True(seen.Add(RainUnderRoofModel.ProbeValue(x, y)));
    }

    [Fact]
    public void TheRegionProbeTellsWhatTheRegionsYMeans()
    {
        // region row 0 came back as logical row 0: the region's y is the texture's row
        Assert.Equal(RainProbeOrientation.AsWritten, RainUnderRoofModel.ClassifyRegionProbe(RegionData(0)));
        // region row 0 came back as the last logical row: the region's y counts flipped native rows
        Assert.Equal(RainProbeOrientation.FlippedY, RainUnderRoofModel.ClassifyRegionProbe(RegionData(RainUnderRoofModel.ProbeSize - 1)));
    }

    [Fact]
    public void TheRegionProbeRefusesTheRest()
    {
        Assert.Equal(RainProbeOrientation.Unusable, RainUnderRoofModel.ClassifyRegionProbe(RegionData(1)));
        Assert.Equal(RainProbeOrientation.Unusable, RainUnderRoofModel.ClassifyRegionProbe(RegionData(2)));
        Assert.Equal(RainProbeOrientation.Unusable, RainUnderRoofModel.ClassifyRegionProbe(RegionData(0, skew: 1)));   // a shifted column
        var mirroredX = RegionData(0);
        System.Array.Reverse(mirroredX);
        Assert.Equal(RainProbeOrientation.Unusable, RainUnderRoofModel.ClassifyRegionProbe(mirroredX));
        Assert.Equal(RainProbeOrientation.Unusable, RainUnderRoofModel.ClassifyRegionProbe(new float[2]));
        Assert.Equal(RainProbeOrientation.Unusable, RainUnderRoofModel.ClassifyRegionProbe(new float[1]));
        Assert.Equal(RainProbeOrientation.Unusable, RainUnderRoofModel.ClassifyRegionProbe(null));
    }

    [Fact]
    public void TheFlippedPlanAsksForNativeRowsThatHoldTheLogicalWindow()
    {
        // the probe's two outcomes and the plan agree: FlippedY asks for native rows Dim-1-y1.. so the logical rows y0..y1 come back
        var photo = Photo();
        Assert.True(RainUnderRoofModel.TryPlanWindow(photo, RainProbeOrientation.AsWritten, 0f, 0f, 1, 1, out var asWritten));
        Assert.True(RainUnderRoofModel.TryPlanWindow(photo, RainProbeOrientation.FlippedY, 0f, 0f, 1, 1, out var flipped));
        Assert.Equal(asWritten.Height, flipped.Height);
        Assert.Equal(photo.Dim - 1 - (asWritten.Y + asWritten.Height - 1), flipped.Y);
    }

    // ---- engagement gate ----

    [Fact]
    public void ThePhotoCarriesCoverOnlyInTheRoofDepthModeWithAFinishedScanOrACoverAllQuad()
    {
        Assert.True(RainUnderRoofModel.CarriesCover(true, false, true, 12, out string why));
        Assert.Null(why);
        Assert.False(RainUnderRoofModel.CarriesCover(false, false, true, 12, out why));      // Off or the legacy RoofDepth
        Assert.NotNull(why);
        Assert.False(RainUnderRoofModel.CarriesCover(false, true, true, 12, out _));         // a cover-all quad does not rescue another mode
        Assert.False(RainUnderRoofModel.CarriesCover(true, false, false, 12, out why));      // scan unfinished
        Assert.Contains("not finished", why);
        Assert.False(RainUnderRoofModel.CarriesCover(true, false, true, 0, out why));        // an empty photo would read as open sky indoors
        Assert.Contains("0 cover meshes", why);
        Assert.True(RainUnderRoofModel.CarriesCover(true, true, false, 0, out _));           // FullyIndoorCover / the lab quad: covered everywhere
    }

    // ---- state ----

    static float[] Window(float height, bool reversedZ, int count = 49)
    {
        var values = new float[count];
        System.Array.Fill(values, float.IsNaN(height) ? (reversedZ ? 0f : 1f) : RawAt(height, reversedZ));
        return values;
    }

    static RainUnderRoofState Ready(bool reversedZ = true)
    {
        var state = new RainUnderRoofState { Orientation = RainProbeOrientation.AsWritten };
        state.BeginPhoto(Photo(reversedZ));
        return state;
    }

    [Fact]
    public void NothingIsDecidedWithoutAPhotoAnOrientationOrAResult()
    {
        var empty = new RainUnderRoofState { Orientation = RainProbeOrientation.AsWritten };
        Assert.False(empty.TryDecide(0, 1.7f, 0, out _, out _, out var why));
        Assert.Equal(RainNotReady.NoPhoto, why);

        var noOrientation = new RainUnderRoofState();
        noOrientation.BeginPhoto(Photo());
        Assert.False(noOrientation.TryDecide(0, 1.7f, 0, out _, out _, out why));
        Assert.Equal(RainNotReady.NoOrientation, why);
        Assert.False(noOrientation.TryBeginRequest(0, 0, out _));

        var state = Ready();
        Assert.False(state.TryDecide(0, 1.7f, 0, out _, out _, out why));
        Assert.Equal(RainNotReady.NoResult, why);
        Assert.False(state.TryDecide(900f, 1.7f, 0, out _, out _, out why));
        Assert.Equal(RainNotReady.OutsidePhoto, why);
    }

    [Fact]
    public void AHallCeilingAboveTheCameraMeansUnderRoof()
    {
        var state = Ready();
        Assert.True(state.TryBeginRequest(0f, 0f, out var w));
        Assert.True(state.Submit(w, Window(6.2f, true), w.Count));
        Assert.True(state.TryDecide(0f, 1.7f, 0f, out bool underRain, out float top, out _));
        Assert.False(underRain);
        Assert.Equal(6.2f, top, 2);
    }

    [Fact]
    public void OpenSkyAndTheGroundBelowMeanUnderRain()
    {
        var sky = Ready();
        Assert.True(sky.TryBeginRequest(0f, 0f, out var w));
        Assert.True(sky.Submit(w, Window(float.NaN, true), w.Count));
        Assert.True(sky.TryDecide(0f, 1.7f, 0f, out bool underRain, out float top, out _));
        Assert.True(underRain);
        Assert.True(float.IsNaN(top));

        var ground = Ready();
        Assert.True(ground.TryBeginRequest(0f, 0f, out w));
        Assert.True(ground.Submit(w, Window(0.03f, true), w.Count));
        Assert.True(ground.TryDecide(0f, 1.7f, 0f, out underRain, out _, out _));
        Assert.True(underRain);
        // lying prone on that ground, camera 0.4 m up: still the ground, still rain
        Assert.True(ground.TryDecide(0f, 0.43f, 0f, out underRain, out _, out _));
        Assert.True(underRain);
    }

    [Fact]
    public void StandingOnARoofIsUnderRainAndTheSameRoofAboveIsNot()
    {
        var state = Ready(false);
        Assert.True(state.TryBeginRequest(0f, 0f, out var w));
        Assert.True(state.Submit(w, Window(12f, false), w.Count));
        Assert.True(state.TryDecide(0f, 13.7f, 0f, out bool underRain, out _, out _)); // on the roof
        Assert.True(underRain);
        Assert.True(state.TryDecide(0f, 8.7f, 0f, out underRain, out _, out _));       // under it
        Assert.False(underRain);
    }

    [Fact]
    public void ASecondRequestForTheSameTexelIsNotMade()
    {
        var state = Ready();
        Assert.True(state.TryBeginRequest(0f, 0f, out var w1));
        Assert.False(state.TryBeginRequest(0.02f, 0.03f, out _)); // same 0.1 m texel
        Assert.True(state.TryBeginRequest(0.5f, 0f, out var w2)); // another texel
        Assert.True(w2.Sequence > w1.Sequence);
    }

    [Fact]
    public void AFailedRequestIsAskedForAgainButOnlyInItsOwnGeneration()
    {
        var state = Ready();
        Assert.True(state.TryBeginRequest(0f, 0f, out var w));
        Assert.False(state.TryBeginRequest(0f, 0f, out _));
        state.ForgetRequest(w.Generation - 1);                 // a request of an older photo changes nothing
        Assert.False(state.TryBeginRequest(0f, 0f, out _));
        state.ForgetRequest(w.Generation);
        Assert.True(state.TryBeginRequest(0f, 0f, out var again));
        Assert.True(again.Sequence > w.Sequence);
    }

    [Fact]
    public void AReRenderReplacesThePhotoAndDropsEverythingOfTheOldOne()
    {
        var state = Ready();
        Assert.True(state.TryBeginRequest(0f, 0f, out var old));
        Assert.True(state.Submit(old, Window(6.2f, true), old.Count));
        int before = state.Generation;

        state.BeginPhoto(Photo());
        Assert.Equal(before + 1, state.Generation);
        // the result of the old photo is gone: back to the harmless "not ready"
        Assert.False(state.TryDecide(0f, 1.7f, 0f, out _, out _, out var why));
        Assert.Equal(RainNotReady.NoResult, why);
        // a readback still in flight from the old photo is refused when it arrives
        Assert.False(state.Submit(old, Window(30f, true), old.Count));
        Assert.False(state.TryDecide(0f, 1.7f, 0f, out _, out _, out _));
        // the same texel is asked for again in the new generation
        Assert.True(state.TryBeginRequest(0f, 0f, out var fresh));
        Assert.Equal(state.Generation, fresh.Generation);
        Assert.True(state.Submit(fresh, Window(float.NaN, true), fresh.Count));
        Assert.True(state.TryDecide(0f, 1.7f, 0f, out bool underRain, out _, out _));
        Assert.True(underRain);
    }

    [Fact]
    public void AReRenderWithDifferentParametersConvertsWithTheNewOnes()
    {
        var state = Ready();
        var shorter = new RainPhoto(-200f, -200f, 1f / 409.6f, 1f / 409.6f, 4096, 30f, 40f, true); // the depth camera now starts at 30
        state.BeginPhoto(shorter);
        Assert.True(state.TryBeginRequest(0f, 0f, out var w));
        var values = new float[49];
        System.Array.Fill(values, 0.5f); // raw 0.5 reversed: 30 - 0.5 * 40 = 10
        Assert.True(state.Submit(w, values, 49));
        Assert.True(state.TryDecide(0f, 1.7f, 0f, out bool underRain, out float top, out _));
        Assert.Equal(10f, top, 2);
        Assert.False(underRain);
    }

    [Fact]
    public void AnOlderResultNeverReplacesANewerOne()
    {
        var state = Ready();
        Assert.True(state.TryBeginRequest(0f, 0f, out var first));
        Assert.True(state.TryBeginRequest(0.5f, 0f, out var second));
        Assert.True(state.Submit(second, Window(float.NaN, true), second.Count));
        Assert.False(state.Submit(first, Window(6.2f, true), first.Count));
        Assert.True(state.TryDecide(0.5f, 1.7f, 0f, out bool underRain, out _, out _));
        Assert.True(underRain);
    }

    [Fact]
    public void AResultFromFarAwayIsStale()
    {
        var state = Ready();
        Assert.True(state.TryBeginRequest(0f, 0f, out var w));
        Assert.True(state.Submit(w, Window(float.NaN, true), w.Count));
        Assert.True(state.TryDecide(1.9f, 1.7f, 0f, out _, out _, out _));
        Assert.False(state.TryDecide(2.1f, 1.7f, 0f, out _, out _, out var why));
        Assert.Equal(RainNotReady.StaleResult, why);
        Assert.False(state.TryDecide(0f, 1.7f, 50f, out _, out _, out why)); // a respawn on the same map
        Assert.Equal(RainNotReady.StaleResult, why);
    }

    [Fact]
    public void AResultWithTooFewValuesIsRefused()
    {
        var state = Ready();
        Assert.True(state.TryBeginRequest(0f, 0f, out var w));
        Assert.False(state.Submit(w, new float[10], 10));
        Assert.False(state.Submit(w, null, 49));
        Assert.False(state.TryDecide(0f, 1.7f, 0f, out _, out _, out _));
    }

    [Fact]
    public void ResetDropsThePhotoAndTheResultAndIgnoresLateReadbacks()
    {
        var state = Ready();
        Assert.True(state.TryBeginRequest(0f, 0f, out var w));
        state.Reset();
        Assert.False(state.HasPhoto);
        Assert.False(state.Submit(w, Window(6.2f, true), w.Count));
        Assert.False(state.TryBeginRequest(0f, 0f, out _));
        Assert.False(state.TryDecide(0f, 1.7f, 0f, out _, out _, out var why));
        Assert.Equal(RainNotReady.NoPhoto, why);
    }

    [Fact]
    public void AnUnusablePhotoIsNeverArmed()
    {
        var state = new RainUnderRoofState { Orientation = RainProbeOrientation.AsWritten };
        state.BeginPhoto(default);
        Assert.False(state.HasPhoto);
        Assert.False(state.TryBeginRequest(0f, 0f, out _));
    }

    // ---- sources ----

    static string ClientDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "ArenaMapPort.Client");
            if (File.Exists(Path.Combine(path, "Plugin.cs"))) return path;
        }
        throw new DirectoryNotFoundException("ArenaMapPort.Client");
    }
}
