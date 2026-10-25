#nullable disable
using System;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>What a score monitor shows (docs/progress/score-monitors.md): the three faces of Arena's own templates the port draws. A prop whose
    /// type word has no face (Equator's player boards, Bowl's unknown word 10) stays dark.</summary>
    internal enum ScoreMonitorFace { None = 0, Timer = 1, Score = 2, Alive = 3 }

    /// <summary>What a layout element is. Fill = a solid rectangle, Sprite = a picture of the sprite table, Text = one TextMeshPro text, Toggles = a column
    /// of the alive toggles (<see cref="ScoreMonitorLayout.ToggleCell"/>), Group = a box that only holds the elements below it (the timer's text row).</summary>
    internal enum ScoreMonitorElementKind { Fill, Sprite, Text, Toggles, Group }

    /// <summary>The job of an element: the frame (what the monitor shows) feeds the texts, the team blocks and the toggle columns by role.</summary>
    internal enum ScoreMonitorRole
    {
        None = 0,
        Ground, TimerGlow, TeamBlockA, TeamBlockB, IconGlow, IconBack, Icon, Vignette,   // pictures
        TimerRow,                                                                         // the group that holds the three timer texts
        Minutes, Colon, Seconds, ScoreA, ScoreB,                                          // texts
        TogglesA, TogglesB,                                                               // toggle columns
    }

    /// <summary>Which team's colour an element takes (side A = the left block, side B = the right one).</summary>
    internal enum ScoreMonitorTeam { None = 0, A, B }

    internal enum ScoreMonitorAlign { Left, Center, Right }

    /// <summary>A colour as four floats in 0..1 (no Unity type: this file is linked into the tests).</summary>
    internal readonly struct ScoreMonitorColor
    {
        internal readonly float R, G, B, A;
        internal ScoreMonitorColor(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
    }

    /// <summary>One sprite of the artwork package (work/extract/monitors, staged to the plugin's monitors folder): its manifest name, the size of its
    /// PNG (= the sprite rect, which Unity's preserve-aspect fit uses) and the 9-slice border (the vignette only).</summary>
    internal readonly struct ScoreMonitorSprite
    {
        internal readonly string Name;
        internal readonly int Width, Height, Border;
        internal ScoreMonitorSprite(string name, int width, int height, int border) { Name = name; Width = width; Height = height; Border = border; }
    }

    /// <summary>A rectangle in canvas pixels, origin bottom-left like Unity's rect transforms.</summary>
    internal readonly struct ScoreMonitorRect
    {
        internal readonly float X0, Y0, X1, Y1;
        internal ScoreMonitorRect(float x0, float y0, float x1, float y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        internal float Width => X1 - X0;
        internal float Height => Y1 - Y0;
    }

    /// <summary>One element of a face: its place in the PARENT box as anchors (0..1, origin bottom-left, a stretched rect transform as in Arena's templates),
    /// what it draws and how. Elements of a face are listed back to front.</summary>
    internal readonly struct ScoreMonitorElement
    {
        internal readonly ScoreMonitorRole Role, Parent;           // Parent None = the canvas
        internal readonly ScoreMonitorElementKind Kind;
        internal readonly float MinX, MinY, MaxX, MaxY;
        internal readonly string Sprite;                           // Sprite elements: the manifest name; otherwise null
        internal readonly bool PreserveAspect;                     // fit the sprite inside the box keeping its rect's aspect (Unity's Image.preserveAspect)
        internal readonly bool Sliced;                             // 9-slice with the sprite's border (Unity's Image.Type.Sliced)
        internal readonly ScoreMonitorColor Tint;                  // fill colour / sprite tint / text colour
        internal readonly ScoreMonitorTeam Team;                   // not None: the RGB comes from the side's colour, the alpha stays Tint.A
        internal readonly ScoreMonitorAlign Align;                 // texts: horizontal alignment (always vertically centred)
        internal readonly float CharacterSpacing, LineSpacing;     // texts: TextMeshPro values
        internal readonly bool Ellipsis;                           // texts: overflow mode Ellipsis (else Overflow)

        internal ScoreMonitorElement(ScoreMonitorRole role, ScoreMonitorRole parent, ScoreMonitorElementKind kind, float minX, float minY, float maxX, float maxY,
            string sprite, bool preserveAspect, bool sliced, ScoreMonitorColor tint, ScoreMonitorTeam team, ScoreMonitorAlign align, float characterSpacing,
            float lineSpacing, bool ellipsis)
        {
            Role = role; Parent = parent; Kind = kind; MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; Sprite = sprite; PreserveAspect = preserveAspect;
            Sliced = sliced; Tint = tint; Team = team; Align = align; CharacterSpacing = characterSpacing; LineSpacing = lineSpacing; Ellipsis = ellipsis;
        }
    }

    /// <summary>
    /// The layout of the three monitor faces, Unity-free (linked into the server tests; docs/progress/score-monitors.md). Every number was decoded from Arena's own
    /// templates (MonitorTimer, MonitorTeamsCommonRoundScore, MonitorAlivePlayers of sharedassets1.assets; tools/extract/extract_monitor_templates.py has the
    /// sprites): anchors, colours, alphas, text alignments, spacings and the auto-size range. What Arena does: its mode scripts instantiate a template as a
    /// world-space canvas of 1930 x 1163 canvas pixels on the TV prop's holder and set its texts. What the port lacks: the canvas (the extractor skips rect
    /// transforms) and the scripts. What this does: lists the elements of the three faces so that the client can build the same picture itself (the ScoreMonitors
    /// view), without Arena's animated overlays (three 1920 x 1080 noise sprites and the scrolling interference texture are left out on purpose).
    ///
    /// Faces. <see cref="ScoreMonitorFace.Timer"/> (type word 0): the dark background sprite, a green glow, the time as three texts MM : SS in colour
    /// (120, 230, 150), the vignette. <see cref="ScoreMonitorFace.Score"/> (words 1 and 6, the final-score template is identical): black, a red block on the
    /// left (side A) and a blue one on the right (side B) at alpha 0.4902, the two scores in white, a glow, a dark disc with a trophy in the middle, the
    /// vignette. <see cref="ScoreMonitorFace.Alive"/> (word 2): the same blocks and disc with a skull, and in each block a column of toggles, one per team
    /// member, lit (On sprite) while the member is alive and dark (Off sprite) otherwise.
    ///
    /// Geometry. Anchors are fractions of the PARENT element's box (the canvas when Parent is None). Sprites with <see cref="ScoreMonitorElement.PreserveAspect"/>
    /// are fitted inside their box by <see cref="FitInside"/> (the size of the sprite's rect, centred). The timer's three texts sit in one row whose widths Unity's
    /// HorizontalLayoutGroup would assign (<see cref="TimerColumns"/>).
    /// </summary>
    internal static class ScoreMonitorLayout
    {
        // ---- the canvas and the faces ----------------------------------------------------------------------------------------------------

        /// <summary>The canvas of Arena's holder in canvas pixels (<see cref="ArenaScoreMonitorMount"/>).</summary>
        internal const float CanvasWidth = ArenaScoreMonitorMount.Width, CanvasHeight = ArenaScoreMonitorMount.Height;

        /// <summary>The face a holder's type word gets: 0 timer, 1 score, 6 score (TeamsFinalRoundScore, inferred and identical to word 1), 2 alive; every other
        /// word (7 player board, 10, anything new) none. The sidecars' <c>face</c> field says the same (a test pins them against each other).</summary>
        internal static ScoreMonitorFace FaceOf(int arenaType)
        {
            switch (arenaType)
            {
                case 0: return ScoreMonitorFace.Timer;
                case 1:
                case 6: return ScoreMonitorFace.Score;
                case 2: return ScoreMonitorFace.Alive;
                default: return ScoreMonitorFace.None;
            }
        }

        /// <summary>The sidecar's word for a face: timer, score, alive, none.</summary>
        internal static string FaceName(ScoreMonitorFace face)
        {
            switch (face)
            {
                case ScoreMonitorFace.Timer: return "timer";
                case ScoreMonitorFace.Score: return "score";
                case ScoreMonitorFace.Alive: return "alive";
                default: return "none";
            }
        }

        // ---- sprites (the manifest names of work/extract/monitors; tools/extract/extract_monitor_templates.py SPRITES, pinned by a test) ----

        internal const string SpriteBackground = "Arena_Scoreboard_AllBoards_Background";
        internal const string SpriteTimerGlow = "Arena_Scoreboard_Timer_Glow_White";
        internal const string SpriteVignette = "Arena_Scoreboard_AllBoards_Vignette";
        internal const string SpriteIconGlow = "Arena_Scoreboard_AllBoards_Icon_Glow";
        internal const string SpriteIconBack = "Arena_Scoreboard_AllBoards_Icon_Background";
        internal const string SpriteScoreIcon = "Arena_Scoreboard_Score_Icon_Scull";
        internal const string SpriteAliveIcon = "Arena_Scoreboard_Status_Icon_Scull";
        internal const string SpriteToggleOn = "Arena_Scoreboard_Status_Toggle_On";
        internal const string SpriteToggleOff = "Arena_Scoreboard_Status_Toggle_Off";

        /// <summary>Every sprite the three faces use, in the manifest's order, with its size (the PNG and the sprite rect) and 9-slice border.</summary>
        internal static readonly ScoreMonitorSprite[] Sprites =
        {
            new ScoreMonitorSprite(SpriteBackground, 1920, 1080, 0),
            new ScoreMonitorSprite(SpriteTimerGlow, 1920, 1080, 0),
            new ScoreMonitorSprite(SpriteVignette, 1920, 1080, 80),
            new ScoreMonitorSprite(SpriteIconGlow, 1250, 500, 0),
            new ScoreMonitorSprite(SpriteIconBack, 356, 356, 0),
            new ScoreMonitorSprite(SpriteScoreIcon, 250, 250, 0),
            new ScoreMonitorSprite(SpriteAliveIcon, 250, 250, 0),
            new ScoreMonitorSprite(SpriteToggleOn, 178, 178, 0),
            new ScoreMonitorSprite(SpriteToggleOff, 178, 178, 0),
        };

        /// <summary>The sprite table entry of a manifest name; false for a name this layout does not know.</summary>
        internal static bool TryGetSprite(string name, out ScoreMonitorSprite sprite)
        {
            foreach (ScoreMonitorSprite s in Sprites)
            {
                if (s.Name == name) { sprite = s; return true; }
            }
            sprite = default(ScoreMonitorSprite);
            return false;
        }

        // ---- colours ----------------------------------------------------------------------------------------------------------------------

        /// <summary>The alpha of both team blocks (0.4902 in Arena's templates).</summary>
        internal const float BlockAlpha = 0.4902f;

        /// <summary>Arena's red (side A, the left block) and blue (side B, the right block) without the alpha.</summary>
        internal static readonly ScoreMonitorColor ArenaRed = new ScoreMonitorColor(0.93725f, 0.00784f, 0.22745f, BlockAlpha);
        internal static readonly ScoreMonitorColor ArenaBlue = new ScoreMonitorColor(0.2549f, 0.30196f, 0.89804f, BlockAlpha);

        /// <summary>The block colour of a side for a contract colour argument: 0xRRGGBB, or anything else (-1, a value above 0xFFFFFF) for Arena's red (side A)
        /// or blue (side B). The alpha is always <see cref="BlockAlpha"/>.</summary>
        internal static ScoreMonitorColor TeamColor(int rgb, bool sideA)
        {
            if (rgb < 0 || rgb > 0xFFFFFF) return sideA ? ArenaRed : ArenaBlue;
            return new ScoreMonitorColor(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, BlockAlpha);
        }

        private static readonly ScoreMonitorColor White = new ScoreMonitorColor(1f, 1f, 1f, 1f);
        private static readonly ScoreMonitorColor Black = new ScoreMonitorColor(0f, 0f, 0f, 1f);
        /// <summary>The timer's text colour, the glow's RGB: Color32 (120, 230, 150).</summary>
        internal static readonly ScoreMonitorColor TimerGreen = new ScoreMonitorColor(120f / 255f, 230f / 255f, 150f / 255f, 1f);

        // ---- text style (Arena's TextMeshPro settings: auto-size between 0.2 and 580 inside the rect, wrapping on) ---------------------------

        internal const float TextMinSize = 0.2f, TextMaxSize = 580f;

        // ---- the timer row ----------------------------------------------------------------------------------------------------------------

        /// <summary>The timer's text row in the canvas: anchors (0.08, 0.2) to (0.92, 0.8).</summary>
        internal const float TimerRowMinX = 0.08f, TimerRowMinY = 0.2f, TimerRowMaxX = 0.92f, TimerRowMaxY = 0.8f;

        /// <summary>The row's HorizontalLayoutGroup (child control and force-expand on both axes, spacing 0) sizes the three texts: minutes and seconds ask for
        /// 680 with flexible 10, the colon asks for its own text width and has no flexible width, which force-expand raises to 1 (Unity takes max(flexible, 1)).
        /// Arena's saved sizes (728.95, 154.90, 728.95 in a 1612.8 wide row of its 1920 reference) give the colon a preferred width of 150.</summary>
        internal const float TimerDigitsPreferred = 680f, TimerColonPreferred = 150f, TimerDigitsFlexible = 10f, TimerColonFlexible = 1f;

        /// <summary>The widths of the minutes, colon and seconds texts in a row of <paramref name="rowWidth"/> pixels: the preferred sizes plus the free space
        /// shared by the flexible weights (a row narrower than the preferred sizes keeps them proportionally).</summary>
        internal static void TimerColumns(float rowWidth, out float minutes, out float colon, out float seconds)
        {
            float preferred = TimerDigitsPreferred + TimerColonPreferred + TimerDigitsPreferred;
            float flexible = TimerDigitsFlexible + TimerColonFlexible + TimerDigitsFlexible;
            float extra = rowWidth - preferred;
            if (extra < 0f)
            {
                float k = rowWidth / preferred;
                minutes = TimerDigitsPreferred * k; colon = TimerColonPreferred * k; seconds = TimerDigitsPreferred * k;
                return;
            }
            minutes = TimerDigitsPreferred + extra * TimerDigitsFlexible / flexible;
            colon = TimerColonPreferred + extra * TimerColonFlexible / flexible;
            seconds = minutes;
        }

        // ---- the alive toggles ------------------------------------------------------------------------------------------------------------

        /// <summary>The edge of one toggle cell in Arena's template (200 x 200 canvas pixels, the sprites are fitted inside with their aspect).</summary>
        internal const float ToggleCell = 200f;

        /// <summary>The edge of a toggle cell for <paramref name="count"/> members in a column box of <paramref name="width"/> x <paramref name="height"/> pixels:
        /// Arena's 200, shrunk so that the column fits (Arena's own teams are of five; the contract allows 16 per side).</summary>
        internal static float ToggleSize(int count, float width, float height)
        {
            if (count <= 0) return Math.Min(ToggleCell, Math.Min(width, height));
            return Math.Min(ToggleCell, Math.Min(width, height / count));
        }

        /// <summary>The vertical offset (canvas pixels, up positive) from the centre of the column box to the centre of toggle <paramref name="index"/> (0 = the top
        /// one) of <paramref name="count"/> toggles of edge <paramref name="size"/>: Arena's VerticalLayoutGroup centres the stack (child alignment MiddleCenter).</summary>
        internal static float ToggleCenterOffset(int index, int count, float size)
        {
            return ((count - 1) * 0.5f - index) * size;
        }

        // ---- the elements -------------------------------------------------------------------------------------------------------------------

        private static ScoreMonitorElement Fill(ScoreMonitorRole role, float minX, float minY, float maxX, float maxY, ScoreMonitorColor tint, ScoreMonitorTeam team = ScoreMonitorTeam.None) =>
            new ScoreMonitorElement(role, ScoreMonitorRole.None, ScoreMonitorElementKind.Fill, minX, minY, maxX, maxY, null, false, false, tint, team, ScoreMonitorAlign.Center, 0f, 0f, false);

        private static ScoreMonitorElement Picture(ScoreMonitorRole role, ScoreMonitorRole parent, float minX, float minY, float maxX, float maxY, string sprite,
            ScoreMonitorColor tint, bool preserveAspect = false, bool sliced = false) =>
            new ScoreMonitorElement(role, parent, ScoreMonitorElementKind.Sprite, minX, minY, maxX, maxY, sprite, preserveAspect, sliced, tint, ScoreMonitorTeam.None, ScoreMonitorAlign.Center, 0f, 0f, false);

        private static ScoreMonitorElement Text(ScoreMonitorRole role, ScoreMonitorRole parent, float minX, float minY, float maxX, float maxY, ScoreMonitorColor tint,
            ScoreMonitorAlign align, float characterSpacing, float lineSpacing, bool ellipsis) =>
            new ScoreMonitorElement(role, parent, ScoreMonitorElementKind.Text, minX, minY, maxX, maxY, null, false, false, tint, ScoreMonitorTeam.None, align, characterSpacing, lineSpacing, ellipsis);

        private static ScoreMonitorElement Toggles(ScoreMonitorRole role, float minX, float minY, float maxX, float maxY) =>
            new ScoreMonitorElement(role, ScoreMonitorRole.None, ScoreMonitorElementKind.Toggles, minX, minY, maxX, maxY, null, false, false, White, ScoreMonitorTeam.None, ScoreMonitorAlign.Center, 0f, 0f, false);

        private static ScoreMonitorElement Group(ScoreMonitorRole role, float minX, float minY, float maxX, float maxY) =>
            new ScoreMonitorElement(role, ScoreMonitorRole.None, ScoreMonitorElementKind.Group, minX, minY, maxX, maxY, null, false, false, White, ScoreMonitorTeam.None, ScoreMonitorAlign.Center, 0f, 0f, false);

        private static readonly ScoreMonitorElement VignetteElement =
            Picture(ScoreMonitorRole.Vignette, ScoreMonitorRole.None, 0f, 0f, 1f, 1f, SpriteVignette, new ScoreMonitorColor(1f, 1f, 1f, 0.6f), sliced: true);

        private static readonly ScoreMonitorElement[] TimerElements = BuildTimer();
        private static readonly ScoreMonitorElement[] ScoreElements = BuildScore();
        private static readonly ScoreMonitorElement[] AliveElements = BuildAlive();
        private static readonly ScoreMonitorElement[] NoElements = new ScoreMonitorElement[0];

        private static ScoreMonitorElement[] BuildTimer()
        {
            TimerColumns(CanvasWidth * (TimerRowMaxX - TimerRowMinX), out float minutes, out float colon, out float seconds);
            float row = minutes + colon + seconds;
            float colonStart = minutes / row, colonEnd = (minutes + colon) / row;
            // Arena: MinutesText right-aligned, ColonText centred (its own font, Bender Underlay Green Shine), SecondsText left-aligned; all 580 auto-size, spacing 2.7, line spacing -10, wrapping on, Overflow.
            return new[]
            {
                Picture(ScoreMonitorRole.Ground, ScoreMonitorRole.None, 0f, 0f, 1f, 1f, SpriteBackground, White),
                Picture(ScoreMonitorRole.TimerGlow, ScoreMonitorRole.None, 0f, 0f, 1f, 1f, SpriteTimerGlow, new ScoreMonitorColor(0.47059f, 0.90196f, 0.58824f, 0.19608f), preserveAspect: true),
                Group(ScoreMonitorRole.TimerRow, TimerRowMinX, TimerRowMinY, TimerRowMaxX, TimerRowMaxY),
                Text(ScoreMonitorRole.Minutes, ScoreMonitorRole.TimerRow, 0f, 0f, colonStart, 1f, TimerGreen, ScoreMonitorAlign.Right, 2.7f, -10f, false),
                Text(ScoreMonitorRole.Colon, ScoreMonitorRole.TimerRow, colonStart, 0f, colonEnd, 1f, TimerGreen, ScoreMonitorAlign.Center, 2.7f, -10f, false),
                Text(ScoreMonitorRole.Seconds, ScoreMonitorRole.TimerRow, colonEnd, 0f, 1f, 1f, TimerGreen, ScoreMonitorAlign.Left, 2.7f, -10f, false),
                VignetteElement,
            };
        }

        private static ScoreMonitorElement[] BuildScore()
        {
            // Arena: ScoreLeftText / ScoreRightText: white, centred, 580 auto-size, character spacing 20 / 0, line spacing 0, wrapping on, overflow Ellipsis.
            return new[]
            {
                Fill(ScoreMonitorRole.Ground, 0f, 0f, 1f, 1f, Black),
                Fill(ScoreMonitorRole.TeamBlockA, 0f, 0f, 0.492f, 1f, ArenaRed, ScoreMonitorTeam.A),
                Fill(ScoreMonitorRole.TeamBlockB, 0.508f, 0f, 1f, 1f, ArenaBlue, ScoreMonitorTeam.B),
                Text(ScoreMonitorRole.ScoreA, ScoreMonitorRole.None, 0.04f, 0.2f, 0.45f, 0.8f, White, ScoreMonitorAlign.Center, 20f, 0f, true),
                Text(ScoreMonitorRole.ScoreB, ScoreMonitorRole.None, 0.55f, 0.2f, 0.96f, 0.8f, White, ScoreMonitorAlign.Center, 0f, 0f, true),
                Picture(ScoreMonitorRole.IconGlow, ScoreMonitorRole.None, 0f, 0.2f, 1f, 0.8f, SpriteIconGlow, new ScoreMonitorColor(1f, 1f, 1f, 0.29804f), preserveAspect: true),
                Picture(ScoreMonitorRole.IconBack, ScoreMonitorRole.None, 0f, 0.331f, 1f, 0.664f, SpriteIconBack, new ScoreMonitorColor(0.09804f, 0.09804f, 0.09804f, 1f), preserveAspect: true),
                Picture(ScoreMonitorRole.Icon, ScoreMonitorRole.IconBack, 0f, 0.15f, 1f, 0.87f, SpriteScoreIcon, White, preserveAspect: true),
                VignetteElement,
            };
        }

        private static ScoreMonitorElement[] BuildAlive()
        {
            return new[]
            {
                Fill(ScoreMonitorRole.Ground, 0f, 0f, 1f, 1f, Black),
                Fill(ScoreMonitorRole.TeamBlockA, 0f, 0f, 0.492f, 1f, ArenaRed, ScoreMonitorTeam.A),
                Fill(ScoreMonitorRole.TeamBlockB, 0.508f, 0f, 1f, 1f, ArenaBlue, ScoreMonitorTeam.B),
                Toggles(ScoreMonitorRole.TogglesA, 0.17f, 0.02f, 0.325f, 0.98f),
                Toggles(ScoreMonitorRole.TogglesB, 0.675f, 0.02f, 0.83f, 0.98f),
                Picture(ScoreMonitorRole.IconGlow, ScoreMonitorRole.None, 0f, 0.2f, 1f, 0.8f, SpriteIconGlow, new ScoreMonitorColor(1f, 1f, 1f, 0.29804f), preserveAspect: true),
                Picture(ScoreMonitorRole.IconBack, ScoreMonitorRole.None, 0f, 0.331f, 1f, 0.664f, SpriteIconBack, new ScoreMonitorColor(0.09804f, 0.09804f, 0.09804f, 1f), preserveAspect: true),
                Picture(ScoreMonitorRole.Icon, ScoreMonitorRole.IconBack, 0f, 0.15f, 1f, 0.87f, SpriteAliveIcon, White, preserveAspect: true),
                VignetteElement,
            };
        }

        /// <summary>The elements of a face, back to front (the order Arena's templates draw them; the animated overlays are not part of it). The caller must not
        /// change the array. A face of None has no element.</summary>
        internal static ScoreMonitorElement[] ElementsOf(ScoreMonitorFace face)
        {
            switch (face)
            {
                case ScoreMonitorFace.Timer: return TimerElements;
                case ScoreMonitorFace.Score: return ScoreElements;
                case ScoreMonitorFace.Alive: return AliveElements;
                default: return NoElements;
            }
        }

        /// <summary>The sprite names a face draws (distinct, in drawing order), toggles included.</summary>
        internal static string[] SpritesOf(ScoreMonitorFace face)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (ScoreMonitorElement e in ElementsOf(face))
            {
                if (e.Sprite != null && !names.Contains(e.Sprite)) names.Add(e.Sprite);
                if (e.Kind == ScoreMonitorElementKind.Toggles)
                {
                    if (!names.Contains(SpriteToggleOn)) names.Add(SpriteToggleOn);
                    if (!names.Contains(SpriteToggleOff)) names.Add(SpriteToggleOff);
                }
            }
            return names.ToArray();
        }

        // ---- geometry -----------------------------------------------------------------------------------------------------------------------

        /// <summary>The element of a role in a face, or false.</summary>
        internal static bool TryFind(ScoreMonitorFace face, ScoreMonitorRole role, out ScoreMonitorElement element)
        {
            foreach (ScoreMonitorElement e in ElementsOf(face))
            {
                if (e.Role == role) { element = e; return true; }
            }
            element = default(ScoreMonitorElement);
            return false;
        }

        /// <summary>The rectangle of a role of a face in canvas pixels (origin bottom-left): the anchors of the element inside its parent's rectangle. The sprite
        /// fit of a preserve-aspect element is NOT applied (see <see cref="FitInside"/>). False when the face has no such role.</summary>
        internal static bool TryResolve(ScoreMonitorFace face, ScoreMonitorRole role, out ScoreMonitorRect rect)
        {
            rect = new ScoreMonitorRect(0f, 0f, CanvasWidth, CanvasHeight);
            if (!TryFind(face, role, out ScoreMonitorElement element)) return false;
            ScoreMonitorRect parent = rect;
            if (element.Parent != ScoreMonitorRole.None && !TryResolve(face, element.Parent, out parent)) return false;
            float w = parent.Width, h = parent.Height;
            rect = new ScoreMonitorRect(parent.X0 + element.MinX * w, parent.Y0 + element.MinY * h, parent.X0 + element.MaxX * w, parent.Y0 + element.MaxY * h);
            return true;
        }

        /// <summary>The size of a sprite of <paramref name="spriteWidth"/> x <paramref name="spriteHeight"/> fitted inside a box of <paramref name="boxWidth"/> x
        /// <paramref name="boxHeight"/> keeping its aspect (Unity's Image.preserveAspect: the whole sprite rect, never cropped), centred by the caller.</summary>
        internal static void FitInside(float boxWidth, float boxHeight, float spriteWidth, float spriteHeight, out float width, out float height)
        {
            if (boxWidth <= 0f || boxHeight <= 0f || spriteWidth <= 0f || spriteHeight <= 0f) { width = 0f; height = 0f; return; }
            float scale = Math.Min(boxWidth / spriteWidth, boxHeight / spriteHeight);
            width = spriteWidth * scale;
            height = spriteHeight * scale;
        }
    }
}
