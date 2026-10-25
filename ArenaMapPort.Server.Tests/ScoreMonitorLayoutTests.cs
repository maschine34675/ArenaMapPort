using System.Text.Json;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The layout of the three monitor faces (ScoreMonitorLayout, docs/progress/score-monitors.md). The numbers are the ones decoded from Arena's templates
/// (MonitorTimer, MonitorTeamsCommonRoundScore, MonitorAlivePlayers); the tests carry their own copy of the decoded values, so a changed layout constant is a failing
/// test and a deliberate one a reviewed edit of both.
/// </summary>
public sealed class ScoreMonitorLayoutTests
{
    private static ScoreMonitorElement Element(ScoreMonitorFace face, ScoreMonitorRole role)
    {
        Assert.True(ScoreMonitorLayout.TryFind(face, role, out var e), $"{face} has no {role}");
        return e;
    }

    private static void AssertBox(ScoreMonitorElement e, float minX, float minY, float maxX, float maxY)
    {
        Assert.Equal(minX, e.MinX, 5);
        Assert.Equal(minY, e.MinY, 5);
        Assert.Equal(maxX, e.MaxX, 5);
        Assert.Equal(maxY, e.MaxY, 5);
    }

    // ---- which face a type word gets ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "timer")]
    [InlineData(1, "score")]
    [InlineData(2, "alive")]
    [InlineData(6, "score")]
    [InlineData(7, "none")]
    [InlineData(10, "none")]
    [InlineData(3, "none")]
    [InlineData(-1, "none")]
    [InlineData(99, "none")]
    public void ATypeWordGetsItsFace(int word, string face)
    {
        Assert.Equal(face, ScoreMonitorLayout.FaceName(ScoreMonitorLayout.FaceOf(word)));
    }

    [Fact]
    public void FaceNamesAreTheSidecarWords()
    {
        Assert.Equal(new[] { "none", "timer", "score", "alive" },
            new[] { ScoreMonitorFace.None, ScoreMonitorFace.Timer, ScoreMonitorFace.Score, ScoreMonitorFace.Alive }.Select(ScoreMonitorLayout.FaceName).ToArray());
    }

    [Fact]
    public void AFaceOfNoneHasNoElements()
    {
        Assert.Empty(ScoreMonitorLayout.ElementsOf(ScoreMonitorFace.None));
        Assert.Empty(ScoreMonitorLayout.SpritesOf(ScoreMonitorFace.None));
    }

    // ---- the decoded numbers ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheScoreFaceIsArenasTeamsCommonRoundScore()
    {
        var f = ScoreMonitorFace.Score;
        var ground = Element(f, ScoreMonitorRole.Ground);
        Assert.Equal(ScoreMonitorElementKind.Fill, ground.Kind);
        Assert.Equal((0f, 0f, 0f, 1f), (ground.Tint.R, ground.Tint.G, ground.Tint.B, ground.Tint.A));
        AssertBox(Element(f, ScoreMonitorRole.TeamBlockA), 0f, 0f, 0.492f, 1f);
        AssertBox(Element(f, ScoreMonitorRole.TeamBlockB), 0.508f, 0f, 1f, 1f);
        var a = Element(f, ScoreMonitorRole.TeamBlockA);
        var b = Element(f, ScoreMonitorRole.TeamBlockB);
        Assert.Equal(ScoreMonitorTeam.A, a.Team);
        Assert.Equal(ScoreMonitorTeam.B, b.Team);
        Assert.Equal(new[] { 0.93725f, 0.00784f, 0.22745f, 0.4902f }, new[] { a.Tint.R, a.Tint.G, a.Tint.B, a.Tint.A });
        Assert.Equal(new[] { 0.2549f, 0.30196f, 0.89804f, 0.4902f }, new[] { b.Tint.R, b.Tint.G, b.Tint.B, b.Tint.A });

        var left = Element(f, ScoreMonitorRole.ScoreA);
        var right = Element(f, ScoreMonitorRole.ScoreB);
        AssertBox(left, 0.04f, 0.2f, 0.45f, 0.8f);
        AssertBox(right, 0.55f, 0.2f, 0.96f, 0.8f);
        foreach (var t in new[] { left, right })
        {
            Assert.Equal(ScoreMonitorElementKind.Text, t.Kind);
            Assert.Equal(ScoreMonitorAlign.Center, t.Align);
            Assert.True(t.Ellipsis);
            Assert.Equal(0f, t.LineSpacing);
            Assert.Equal((1f, 1f, 1f, 1f), (t.Tint.R, t.Tint.G, t.Tint.B, t.Tint.A));
        }
        Assert.Equal(20f, left.CharacterSpacing);     // Arena's left score has a character spacing of 20, the right one 0
        Assert.Equal(0f, right.CharacterSpacing);

        var glow = Element(f, ScoreMonitorRole.IconGlow);
        AssertBox(glow, 0f, 0.2f, 1f, 0.8f);
        Assert.Equal(ScoreMonitorLayout.SpriteIconGlow, glow.Sprite);
        Assert.True(glow.PreserveAspect);
        Assert.Equal(0.29804f, glow.Tint.A, 5);
        var back = Element(f, ScoreMonitorRole.IconBack);
        AssertBox(back, 0f, 0.331f, 1f, 0.664f);
        Assert.Equal(new[] { 0.09804f, 0.09804f, 0.09804f, 1f }, new[] { back.Tint.R, back.Tint.G, back.Tint.B, back.Tint.A });
        var icon = Element(f, ScoreMonitorRole.Icon);
        AssertBox(icon, 0f, 0.15f, 1f, 0.87f);
        Assert.Equal(ScoreMonitorRole.IconBack, icon.Parent);
        Assert.Equal(ScoreMonitorLayout.SpriteScoreIcon, icon.Sprite);
    }

    [Fact]
    public void TheAliveFaceIsArenasAlivePlayers()
    {
        var f = ScoreMonitorFace.Alive;
        AssertBox(Element(f, ScoreMonitorRole.TeamBlockA), 0f, 0f, 0.492f, 1f);
        AssertBox(Element(f, ScoreMonitorRole.TeamBlockB), 0.508f, 0f, 1f, 1f);
        var left = Element(f, ScoreMonitorRole.TogglesA);
        var right = Element(f, ScoreMonitorRole.TogglesB);
        Assert.Equal(ScoreMonitorElementKind.Toggles, left.Kind);
        AssertBox(left, 0.17f, 0.02f, 0.325f, 0.98f);
        AssertBox(right, 0.675f, 0.02f, 0.83f, 0.98f);
        AssertBox(Element(f, ScoreMonitorRole.IconGlow), 0f, 0.2f, 1f, 0.8f);
        AssertBox(Element(f, ScoreMonitorRole.IconBack), 0f, 0.331f, 1f, 0.664f);
        Assert.Equal(ScoreMonitorLayout.SpriteAliveIcon, Element(f, ScoreMonitorRole.Icon).Sprite);
        Assert.False(ScoreMonitorLayout.TryFind(f, ScoreMonitorRole.ScoreA, out _));   // no score numbers on the alive face
    }

    [Fact]
    public void TheTimerFaceIsArenasTimer()
    {
        var f = ScoreMonitorFace.Timer;
        var ground = Element(f, ScoreMonitorRole.Ground);
        Assert.Equal(ScoreMonitorLayout.SpriteBackground, ground.Sprite);
        Assert.False(ground.PreserveAspect);
        var glow = Element(f, ScoreMonitorRole.TimerGlow);
        Assert.Equal(ScoreMonitorLayout.SpriteTimerGlow, glow.Sprite);
        Assert.True(glow.PreserveAspect);
        Assert.Equal(new[] { 0.47059f, 0.90196f, 0.58824f, 0.19608f }, new[] { glow.Tint.R, glow.Tint.G, glow.Tint.B, glow.Tint.A });
        AssertBox(Element(f, ScoreMonitorRole.TimerRow), 0.08f, 0.2f, 0.92f, 0.8f);
        var minutes = Element(f, ScoreMonitorRole.Minutes);
        var colon = Element(f, ScoreMonitorRole.Colon);
        var seconds = Element(f, ScoreMonitorRole.Seconds);
        Assert.Equal((ScoreMonitorAlign.Right, ScoreMonitorAlign.Center, ScoreMonitorAlign.Left), (minutes.Align, colon.Align, seconds.Align));
        foreach (var t in new[] { minutes, colon, seconds })
        {
            Assert.Equal(ScoreMonitorRole.TimerRow, t.Parent);
            Assert.Equal(2.7f, t.CharacterSpacing);
            Assert.Equal(-10f, t.LineSpacing);
            Assert.False(t.Ellipsis);
            Assert.Equal((120f / 255f, 230f / 255f, 150f / 255f, 1f), (t.Tint.R, t.Tint.G, t.Tint.B, t.Tint.A));
        }
        // the three texts tile the row, left to right
        Assert.Equal(0f, minutes.MinX);
        Assert.Equal(minutes.MaxX, colon.MinX, 6);
        Assert.Equal(colon.MaxX, seconds.MinX, 6);
        Assert.Equal(1f, seconds.MaxX);
    }

    [Fact]
    public void EveryFaceEndsWithTheSlicedVignetteAtAlpha06()
    {
        foreach (var face in new[] { ScoreMonitorFace.Timer, ScoreMonitorFace.Score, ScoreMonitorFace.Alive })
        {
            var last = ScoreMonitorLayout.ElementsOf(face).Last();
            Assert.Equal(ScoreMonitorRole.Vignette, last.Role);
            Assert.True(last.Sliced);
            Assert.Equal(0.6f, last.Tint.A);
            Assert.Equal(ScoreMonitorLayout.SpriteVignette, last.Sprite);
            AssertBox(last, 0f, 0f, 1f, 1f);
            Assert.True(ScoreMonitorLayout.TryGetSprite(last.Sprite, out var sprite));
            Assert.Equal(80, sprite.Border);
        }
    }

    [Fact]
    public void RolesAreUniqueInAFaceAndParentsComeBeforeTheirChildren()
    {
        foreach (var face in new[] { ScoreMonitorFace.Timer, ScoreMonitorFace.Score, ScoreMonitorFace.Alive })
        {
            var seen = new List<ScoreMonitorRole>();
            foreach (var e in ScoreMonitorLayout.ElementsOf(face))
            {
                Assert.DoesNotContain(e.Role, seen);
                if (e.Parent != ScoreMonitorRole.None) Assert.Contains(e.Parent, seen);
                Assert.NotEqual(ScoreMonitorRole.None, e.Role);
                seen.Add(e.Role);
                Assert.InRange(e.MinX, 0f, e.MaxX);
                Assert.InRange(e.MinY, 0f, e.MaxY);
                Assert.InRange(e.MaxX, 0f, 1f);
                Assert.InRange(e.MaxY, 0f, 1f);
                if (e.Kind == ScoreMonitorElementKind.Sprite) Assert.NotNull(e.Sprite);
                else Assert.Null(e.Sprite);
                if (e.Team != ScoreMonitorTeam.None) Assert.Equal(ScoreMonitorElementKind.Fill, e.Kind);
            }
        }
    }

    // ---- sprites -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EverySpriteAnElementNamesIsInTheSpriteTable()
    {
        var all = new HashSet<string>();
        foreach (var face in new[] { ScoreMonitorFace.Timer, ScoreMonitorFace.Score, ScoreMonitorFace.Alive })
            foreach (var name in ScoreMonitorLayout.SpritesOf(face))
            {
                Assert.True(ScoreMonitorLayout.TryGetSprite(name, out _), name);
                all.Add(name);
            }
        // the nine sprites of the package, no more (a sprite nobody draws would be staged for nothing)
        Assert.Equal(ScoreMonitorLayout.Sprites.Select(s => s.Name).OrderBy(n => n).ToArray(), all.OrderBy(n => n).ToArray());
        Assert.Equal(9, ScoreMonitorLayout.Sprites.Length);
        Assert.Equal(ScoreMonitorLayout.Sprites.Length, ScoreMonitorLayout.Sprites.Select(s => s.Name).Distinct().Count());
        Assert.False(ScoreMonitorLayout.TryGetSprite("Arena_Scoreboard_AllBoards_Noise_1", out _));   // the noise overlays are left out on purpose
    }

    [Fact]
    public void EachFaceDrawsExactlyItsSprites()
    {
        // the same lists as test_monitor_templates.py FACE_SPRITES (the extractor's used_by column): a sprite the faces do not draw is not exported, and
        // a face cannot name a sprite the package lacks
        Assert.Equal(new[] { "Arena_Scoreboard_AllBoards_Background", "Arena_Scoreboard_AllBoards_Vignette", "Arena_Scoreboard_Timer_Glow_White" },
            ScoreMonitorLayout.SpritesOf(ScoreMonitorFace.Timer).OrderBy(n => n).ToArray());
        Assert.Equal(new[] { "Arena_Scoreboard_AllBoards_Icon_Background", "Arena_Scoreboard_AllBoards_Icon_Glow", "Arena_Scoreboard_AllBoards_Vignette", "Arena_Scoreboard_Score_Icon_Scull" },
            ScoreMonitorLayout.SpritesOf(ScoreMonitorFace.Score).OrderBy(n => n).ToArray());
        Assert.Equal(new[] { "Arena_Scoreboard_AllBoards_Icon_Background", "Arena_Scoreboard_AllBoards_Icon_Glow", "Arena_Scoreboard_AllBoards_Vignette",
                "Arena_Scoreboard_Status_Icon_Scull", "Arena_Scoreboard_Status_Toggle_Off", "Arena_Scoreboard_Status_Toggle_On" },
            ScoreMonitorLayout.SpritesOf(ScoreMonitorFace.Alive).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void AliveFaceSpritesIncludeBothToggles()
    {
        var names = ScoreMonitorLayout.SpritesOf(ScoreMonitorFace.Alive);
        Assert.Contains(ScoreMonitorLayout.SpriteToggleOn, names);
        Assert.Contains(ScoreMonitorLayout.SpriteToggleOff, names);
        Assert.DoesNotContain(ScoreMonitorLayout.SpriteTimerGlow, names);
        Assert.Contains(ScoreMonitorLayout.SpriteScoreIcon, ScoreMonitorLayout.SpritesOf(ScoreMonitorFace.Score));
        Assert.DoesNotContain(ScoreMonitorLayout.SpriteToggleOn, ScoreMonitorLayout.SpritesOf(ScoreMonitorFace.Score));
    }

    private static string RepoText(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }

    private static string? RepoTextOrNull(string relative)
    {
        try { return RepoText(relative); } catch (FileNotFoundException) { return null; }
    }

    [Fact]
    public void TheSpriteTableAgreesWithTheStagedManifestWhenThePackageExists()
    {
        // work/extract/monitors is ignored by git: without the extracted package (a clean clone, CI) the sizes are pinned by test_monitor_templates.py instead
        var text = RepoTextOrNull("work/extract/monitors/monitors.json");
        if (text == null) return;
        using var doc = JsonDocument.Parse(text);
        var sprites = doc.RootElement.GetProperty("sprites").EnumerateArray().ToArray();
        Assert.Equal(ScoreMonitorLayout.Sprites.Select(s => s.Name).ToArray(), sprites.Select(s => s.GetProperty("name").GetString()).ToArray());
        for (int i = 0; i < sprites.Length; i++)
        {
            var table = ScoreMonitorLayout.Sprites[i];
            Assert.Equal(table.Width, sprites[i].GetProperty("width").GetInt32());
            Assert.Equal(table.Height, sprites[i].GetProperty("height").GetInt32());
            var border = sprites[i].GetProperty("border").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Assert.Equal(new[] { table.Border, table.Border, table.Border, table.Border }, border);
        }
    }

    // ---- geometry ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ResolvedRectsAreCanvasPixelsAndChildrenNestInsideTheirParents()
    {
        Assert.True(ScoreMonitorLayout.TryResolve(ScoreMonitorFace.Score, ScoreMonitorRole.TeamBlockB, out var b));
        Assert.Equal(0.508f * 1930f, b.X0, 2);
        Assert.Equal(1930f, b.X1, 2);
        Assert.Equal(0f, b.Y0);
        Assert.Equal(1163f, b.Y1);
        // the trophy sits in the disc: 0.331..0.664 of the height, the icon 0.15..0.87 of that
        Assert.True(ScoreMonitorLayout.TryResolve(ScoreMonitorFace.Score, ScoreMonitorRole.IconBack, out var disc));
        Assert.True(ScoreMonitorLayout.TryResolve(ScoreMonitorFace.Score, ScoreMonitorRole.Icon, out var icon));
        Assert.Equal(0.331f * 1163f, disc.Y0, 2);
        Assert.Equal(0.664f * 1163f, disc.Y1, 2);
        Assert.Equal(disc.Y0 + 0.15f * disc.Height, icon.Y0, 2);
        Assert.Equal(disc.Y0 + 0.87f * disc.Height, icon.Y1, 2);
        Assert.Equal(disc.X0, icon.X0, 2);
        Assert.False(ScoreMonitorLayout.TryResolve(ScoreMonitorFace.Alive, ScoreMonitorRole.ScoreA, out _));
        Assert.False(ScoreMonitorLayout.TryResolve(ScoreMonitorFace.None, ScoreMonitorRole.Ground, out _));
    }

    [Fact]
    public void PreserveAspectFitsTheWholeSpriteRectInsideItsBox()
    {
        // the glow (1250 x 500) in the 1930 x 697.8 box of the score face: height-limited, 1744 x 698
        Assert.True(ScoreMonitorLayout.TryResolve(ScoreMonitorFace.Score, ScoreMonitorRole.IconGlow, out var box));
        ScoreMonitorLayout.FitInside(box.Width, box.Height, 1250f, 500f, out var w, out var h);
        Assert.Equal(box.Height, h, 2);
        Assert.Equal(1250f / 500f * h, w, 2);
        Assert.True(w <= box.Width);
        // the timer glow (1920 x 1080 rect) in the full canvas: width-limited, 1930 x 1085.6 (the rect, not the tight 804-pixel mesh)
        ScoreMonitorLayout.FitInside(1930f, 1163f, 1920f, 1080f, out var gw, out var gh);
        Assert.Equal(1930f, gw, 2);
        Assert.Equal(1085.625f, gh, 2);
        // the disc (356 x 356) in a 1930 x 387.3 box is 387 x 387; the trophy (250 x 250) in 278.9 high is 279 x 279
        Assert.True(ScoreMonitorLayout.TryResolve(ScoreMonitorFace.Score, ScoreMonitorRole.IconBack, out var disc));
        ScoreMonitorLayout.FitInside(disc.Width, disc.Height, 356f, 356f, out var dw, out var dh);
        Assert.Equal(disc.Height, dh, 2);
        Assert.Equal(disc.Height, dw, 2);
    }

    [Fact]
    public void ADegenerateFitIsZero()
    {
        ScoreMonitorLayout.FitInside(0f, 10f, 5f, 5f, out var w, out var h);
        Assert.Equal((0f, 0f), (w, h));
        ScoreMonitorLayout.FitInside(10f, 10f, 0f, 5f, out w, out h);
        Assert.Equal((0f, 0f), (w, h));
    }

    // ---- the timer's three columns ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTimerColumnsReproduceArenasSavedWidthsInItsReferenceRow()
    {
        // Arena's template stores 728.95 / 154.90 / 728.95 for a row of 0.84 x 1920 = 1612.8
        ScoreMonitorLayout.TimerColumns(1612.8f, out var m, out var c, out var s);
        Assert.Equal(728.952f, m, 1);
        Assert.Equal(154.895f, c, 1);
        Assert.Equal(m, s);
        Assert.Equal(1612.8f, m + c + s, 2);
    }

    [Fact]
    public void TheTimerColumnsOfTheHoldersCanvasAndANarrowRow()
    {
        float row = 1930f * 0.84f;
        ScoreMonitorLayout.TimerColumns(row, out var m, out var c, out var s);
        Assert.Equal(row, m + c + s, 2);
        Assert.InRange(m, 732f, 734f);
        Assert.InRange(c, 155f, 156f);
        // the elements' anchors are those fractions of the row
        Assert.True(ScoreMonitorLayout.TryFind(ScoreMonitorFace.Timer, ScoreMonitorRole.Colon, out var colon));
        Assert.Equal(m / row, colon.MinX, 5);
        Assert.Equal((m + c) / row, colon.MaxX, 5);
        // a row below the preferred sizes keeps their proportions
        ScoreMonitorLayout.TimerColumns(755f, out var m2, out var c2, out var s2);
        Assert.Equal(755f, m2 + c2 + s2, 3);
        Assert.Equal(680f / 150f, m2 / c2, 3);
    }

    // ---- the toggles -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 200f)]
    [InlineData(1, 200f)]
    [InlineData(5, 200f)]      // five toggles of 200 fill 1000 of the column's 1116.5
    [InlineData(6, 186.08f)]   // 1116.5 / 6
    [InlineData(16, 69.78f)]   // the contract's largest team
    public void ATogglesCellShrinksToFitTheColumn(int count, float expected)
    {
        float width = 0.155f * 1930f, height = 0.96f * 1163f;
        Assert.Equal(expected, ScoreMonitorLayout.ToggleSize(count, width, height), 1);
    }

    [Fact]
    public void ATogglesCellNeverExceedsTheColumnWidth()
    {
        Assert.Equal(50f, ScoreMonitorLayout.ToggleSize(1, 50f, 1000f));
        Assert.Equal(40f, ScoreMonitorLayout.ToggleSize(0, 90f, 40f));
    }

    [Fact]
    public void TheToggleStackIsCentredAndTopFirst()
    {
        Assert.Equal(0f, ScoreMonitorLayout.ToggleCenterOffset(0, 1, 200f));
        Assert.Equal(100f, ScoreMonitorLayout.ToggleCenterOffset(0, 2, 200f));
        Assert.Equal(-100f, ScoreMonitorLayout.ToggleCenterOffset(1, 2, 200f));
        Assert.Equal(400f, ScoreMonitorLayout.ToggleCenterOffset(0, 5, 200f));
        Assert.Equal(0f, ScoreMonitorLayout.ToggleCenterOffset(2, 5, 200f));
        Assert.Equal(-400f, ScoreMonitorLayout.ToggleCenterOffset(4, 5, 200f));
    }

    // ---- colours ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-1, false)]
    [InlineData(0x1000000, true)]
    [InlineData(int.MinValue, false)]
    public void AnInvalidColourIsArenasRedOrBlue(int rgb, bool sideA)
    {
        var c = ScoreMonitorLayout.TeamColor(rgb, sideA);
        var expected = sideA ? ScoreMonitorLayout.ArenaRed : ScoreMonitorLayout.ArenaBlue;
        Assert.Equal((expected.R, expected.G, expected.B, expected.A), (c.R, c.G, c.B, c.A));
    }

    [Fact]
    public void ARealColourKeepsItsRgbAndTheBlockAlpha()
    {
        var c = ScoreMonitorLayout.TeamColor(0x7FDBFF, true);
        Assert.Equal(0x7F / 255f, c.R, 5);
        Assert.Equal(0xDB / 255f, c.G, 5);
        Assert.Equal(1f, c.B, 5);
        Assert.Equal(0.4902f, c.A, 5);
        var black = ScoreMonitorLayout.TeamColor(0, false);
        Assert.Equal((0f, 0f, 0f, 0.4902f), (black.R, black.G, black.B, black.A));
        var white = ScoreMonitorLayout.TeamColor(0xFFFFFF, true);
        Assert.Equal((1f, 1f, 1f), (white.R, white.G, white.B));
    }

    // ---- source pins ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheLayoutFileHasNoUnityOrPluginReference()
    {
        var text = RepoText("ArenaMapPort.Client/ScoreMonitorLayout.cs");
        Assert.DoesNotContain("UnityEngine", text);
        Assert.DoesNotContain("Plugin.", text);
        Assert.DoesNotContain("BepInEx", text);
        Assert.DoesNotContain("FindObjectsOfType", text);
        Assert.DoesNotContain("EnsureVerified", text);
    }
}
