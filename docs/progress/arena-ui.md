# Arena UI sprite/texture extraction for ArenaMode — 2026-09-29

## Purpose

[`docs/ARENA-CONTENT-FOR-ARENAMODE.md`](../ARENA-CONTENT-FOR-ARENAMODE.md) (written in parallel this
session, audio half) surveys everything in the Arena 0.5.4.0 install worth adopting for the user's
ArenaMode mod beyond the maps themselves. Several of its findings are 2D UI sprites/textures — killfeed
icons, a K/D/A glyph triplet, mode-select wordmark logos, kit-class medallion badges, capture-point
progress/marker art, scoreboard row backgrounds and a team-color palette, and an animated post-match MVP
medal. This document is the UI half's own write-up: `tools/extract/extract_arena_ui.py`, its catalog, the
manifest it produces (a CONTRACT for ArenaMode to read), this session's real run evidence, and
ArenaMode-side integration notes. It does not wire anything into ArenaMode itself — extraction tooling
lives here, consumption belongs to that separate repo (read-only from this one).

The user asked to see what the killfeed icons look like (2026-09-29); this extraction, its previews and
the correction appended to `docs/ARENA-CONTENT-FOR-ARENAMODE.md` below are the answer.

## Method

Every name/path_id below was read live with UnityPy 1.25.2 against the user's own Arena 0.5.4.0 install
this session, independently of the original 23-agent survey that first flagged these candidates (its own
numbers were spot-checked and, in a few places, corrected — see "Corrections to the original survey"
below). Selection is always by the object's own `m_Name`; a `path_id` in the catalog is a drift-detection
hint, logged (not fatal) when it no longer matches — same rule `extract_arena_audio.py` already uses.
Every catalog item is a Unity `Sprite`, except the one `Killmetod_icon` strip (`kind: "strip_slot"`),
sliced from its own raw `Texture2D` — see "The Killmetod_icon strip" below.

One deliberate deviation from name-only selection: several `mode_logos` sprites legitimately share one
`m_Name` (Arena ships each of the 9 mode logos as 2–4 differently-sized variants, all under the same
name — `teamfight` appears 4 times, for example). For those, `path_id` is not just a hint but the only
disambiguator: `_resolve()` raises `HardExtractError` if a name is ambiguous and no candidate's `path_id`
matches the catalog's recorded one, rather than silently picking the first match.

## Groups

| Group | Items | On-disk size | Purpose |
|---|---|---|---|
| `killfeed` | 40 | 25.5 KiB | Weapon-class (11), kill-method (5) and death-cause (5) icons, two grenade/knife size variants each (4), and the 15-cell `Killmetod_icon` strip (larger duplicates of the weapon + death-cause set). |
| `kda` | 3 | 1.2 KiB | Kill / Death / Assist glyph triplet, 20×20 each. |
| `mode_logos` | 26 | 517 KiB | 9 mode wordmark logos (TeamFight/CheckPoint/Duel/OneManArmy/BlastGang/ShootOut/LastHero/LastHero-NewYear/FinalRun), 2–4 size tiers each. |
| `kit_badges` | 31 | 1.47 MiB | 26 `Popup_Item_Icon_TacMap_*` medallions (weapon classes, 3 mode initials, 7 color-identity, 1 blank), declared rect 300×280, exported (alpha-trimmed) 226×218, plus 5 `PresetsALL_Classes_151123` compact 32×32 glyphs. |
| `objective` | 5 | 3.4 KiB | Capture-progress bar fill (1×19) + background (10×8) tiles, the BlastGang carried-bomb spectator identity icon, and 2 map-pin markers (`Point_Icon_0`/`_1`). |
| `scoreboard` | 18 | 210 KiB | 4 post-round results-row background panels + a matched 7-color team/player identity palette (2 texture families, `Teams_Backgrounds` + `Spectator_Team Color_Background`), average RGBA measured per sprite. |
| `mvp` | 36 | 33.5 MiB | Every frame of the animated post-match MVP medal flipbook (source texture 6480×6480, each frame ~750–1080px square natively). |
| **Total** | **159** | **35.7 MiB** | |

`Icon_Pointer` (a small directional chevron) was deliberately excluded from `objective`: re-verified
against EFT's own `D:/SPT41/EscapeFromTarkov_Data/resources.assets`, which already ships an identically
named, identically sized (35×14) `Icon_Pointer` texture — the same asset, already available to ArenaMode
through the vanilla client, not Arena-exclusive art.

## The Killmetod_icon strip

`sharedassets1.assets`' `Killmetod_icon` is a 480×30 `Texture2D` holding larger duplicates of the same
weapon-class and death-cause icons, at higher fidelity than the small `Notification_BattleUI_*` sprite
families. The task brief that started this extraction flagged it as "NOT a uniform 30 px grid" — true in
spirit, but the *specific* grid it actually is turned out different from the size that initial guess
implied. Live-verified this session: the strip's own 5 named `Sprite` objects (indices 10–14, one each for
fall/barbed-wire/fire/charge/other) are all exactly 32×30, at `x = index * 32`, and 480 / 32 == 15 exactly
— a clean, uniform **32px** grid, not 30px (a naive reading of the texture's own 30px *height* as its cell
*width* — an easy mistake this repo's own first pass over this strip made, corrected here). `derive_strip_grid()`
never hardcodes this: it re-derives cell width/height from the strip's own named sprites and validates the
full texture divides evenly into the catalog's `expected_cell_count`, raising `HardExtractError` on any
disagreement (a future Arena update changing this layout stops extraction instead of silently slicing the
wrong boxes). Indices 0–9 (no named `Sprite` of their own) are cropped from the raw decoded texture at
that same derived grid; this crop was cross-checked pixel-for-pixel against a named sprite at the same
cell this session (index 10) and matched exactly, confirming the raw-texture crop coordinate system needs
no extra flip/offset beyond what `slice_uniform_grid()` already does.

## Manifest contract

`work/extract/arena-ui/manifest.json` (schema_version 1): `{schema_version, generator, generated_at,
source: {root, dir}, groups: {<group>: {purpose, items: [...]}}}`. Each item:

```json
{
  "group": "killfeed", "key": "weapon_icon_0", "kind": "sprite",
  "source": {"root": "arena", "file": "resources.assets"},
  "name": "Notification_BattleUI_Killpg_Icons_Small_0", "path_id": 5831,
  "pixel_size": [43, 11], "sprite_rect": {"x": 0.0, "y": 4.0, "width": 43.0, "height": 11.0},
  "sprite_pivot": {"x": 0.5, "y": 0.5},
  "label": "sniper / bolt-action rifle silhouette", "confidence": "high",
  "arenamode_use": "...", "file": "killfeed/weapon_icon_0.png"
}
```

A `strip_slot` item additionally carries `strip_key`/`strip_index`, and `name`/`path_id`/`sprite_pivot`
are `null` for a cell with no named `Sprite` of its own (indices 0–9, which have no pivot to report).
Every `scoreboard` item carries an extra
`avg_color_rgba` (PIL's own 1×1 box-filter downscale of the sprite — a genuine per-pixel mean, not a
single sampled pixel; cross-checked this session against the original 23-agent survey's own independently
measured colors and matched exactly, e.g. `spectator_teamcolor_0` → `[159, 1, 38, 192]`). Every item
carries `label` (a semantic description), `confidence` (`high`/`medium`/`low` — nothing is dropped for
being uncertain, several kill-method glyphs in particular stay catalogued at `low` confidence with the
ambiguity stated plainly) and `arenamode_use` (a suggested ArenaMode integration point, copied from the
catalog).

`tools/extract/arena_ui_catalog.json` is the git-tracked input this all comes from: group purposes, every
item's Unity name/path_id/label/confidence/use, and the one strip's grid definition — names and metadata
only, never pixels. `--check` verifies `manifest.json` and every file it references exist and are
non-empty, touching no source install and importing no UnityPy, matching every other extractor's `--check`
in this pipeline.

## Previews

`work/extract/arena-ui/previews/<group>.png` — one dark-background, per-item-labeled contact sheet per
group, 3×-upscaled with nearest-neighbour for anything ≤64px so small icons stay legible; never staged
(the client csproj target excludes `previews/` explicitly) and never read by this script, for a human to
eyeball. Every sheet was read back and checked against the catalog's own labels this session; one label
was corrected as a result — `objective/point_icon_1` was first catalogued as "near-identical" to
`point_icon_0` (both green map pins). The preview showed it is actually **yellow/olive**, not green — the
two are a same-silhouette, different-color pair (plausibly an ownership-state indicator, e.g.
friendly/owned vs. neutral/contested), not two redundant copies of one marker. Fixed in the catalog before
this run (`gen_catalog.py`'s own generation script, not hand-edited into the JSON, to keep the 159-item
catalog reproducible from one source of truth).

The `mvp` preview also revealed the flipbook's real animation arc, not obvious from the task brief's "36
frames" alone: frames 0–1 are near-empty (fading in from scattered particles), 2–9 build up the wreath and
"MVP" text, 10–14 show the complete medal with no sword element, 15–29 add a vertical sword blade rising
above the medal, and 30–35 show that blade with an added orange/fiery glow at its base — a 5-stage reveal,
not a uniform loop. Individual frame labels stay generic (`frame N of a 36-frame ... flipbook`); this
sequencing note lives here rather than in 36 near-duplicate catalog entries.

## Corrections to the original survey

The original 23-agent survey (cited in `docs/ARENA-CONTENT-FOR-ARENAMODE.md`) got the broad strokes right
but several specifics needed re-verification against a live UnityPy read before this catalog could trust
them:

- **`lastHeroNewYear`** (pid 5911) is not "146×45, a plain recolor of `lasthero`" as the original survey's
  own evidence table stated — its real crop is 293×143 (this session's own independent verify pass had
  already caught the size), and the preview confirms it is a genuine reskin (the same wordmark with a
  frost/ice decorative overlay), not a different pictogram or a same-scale recolor.
- **Kill-method / death-cause icon sizes are not uniform** within their own families — e.g.
  `Notification_BattleUI_Killpg_Method_Icons_Small`'s 5 sprites are 20/9/17/21/20 px wide, not a flat
  "9×20"; recorded per-item in the manifest, not summarized as one representative size.
- **`kit_badges` is 26 `Popup_Item_Icon_TacMap_*` medallions, not 25** — the original survey's own count
  undercounted by one (its own item text already listed 14 weapon-class suffixes despite labeling the
  total "13"). Each is its own individual 300×280 `Texture2D`, not a shared atlas.
- **`BlastGang_spectator_bomb-identity_icon` is a backpack/satchel silhouette with a carrying handle**, not
  a "bomb/canister-with-fuse" as the original survey described from a lower-detail read — matches
  BlastGang's own carried-objective mechanic (a backpack, not a planted charge) documented elsewhere in
  `docs/ARENA-CONTENT-FOR-ARENAMODE.md`. Re-verified this session with an 8× zoom.
- **The killfeed icon item's Tier C "contested" rating** — see the dated correction appended to
  `docs/ARENA-CONTENT-FOR-ARENAMODE.md` below.

## ArenaMode integration notes (read-only reference, not implemented here)

Every note below is a read of `D:/SPT41/Development/ArenaMode` (the user's other mod, read-only from this
repo) as it stands today, not a description of anything this repo changes.

- **Killfeed** — `ArenaMode.Client/Hud/KillfeedView.cs` (179 lines) + `KillfeedText.cs` (39 lines) render a
  pooled, fading uGUI text line: `"Killer -> Victim"` in player-relative team colors, then a second line
  `"role -> role . distance . WeaponName . HS"`. `KillfeedView.cs`'s own doc comment says this is
  deliberate ("player-relative team colors instead of faction icons", ported from MIT-licensed
  `DragonDen.Killfeed`) — a considered design choice, not an oversight. The `killfeed` group's icons are
  weapon-class/kill-method glyphs, not faction icons, so they would *complement* that team-color line
  rather than reverse it: the Arena weapon-class icon could replace the `->` arrow or the trailing
  `WeaponName` text, and the headshot-skull method icon could replace the trailing `HS`, either via a
  runtime `TMP_SpriteAsset` (`<sprite>` tags inline in the existing text) or a small `Image` placed next to
  `KillfeedText.cs`'s line. Weapon class would need to come from the EFT item template's parent class
  (server-side data ArenaMode already has for scoring), mapped onto this catalog's 11-way weapon-class
  split.
- **KDA** — the real F9 scoreboard is `ArenaPanel.cs` (553-line IMGUI `GUILayout` table, plain-text
  `NAME/ROLE/STATE/K/D/A/HS/SCORE` headers at lines 448–455, drawn with `GUI.DrawTexture`) — **not**
  `ScorePanel.cs`, a separate compact top-center HUD strip. The `kda` triplet is a drop-in for
  `ArenaPanel.cs`'s K/D/A column headers or per-row glyphs there; because it is IMGUI, wiring it in means
  `GUI.DrawTexture` calls with a loaded `Texture2D`, not a uGUI `Image` swap like every other group here.
- **mode_logos** — `MODE-SIDEBAR-REVIEW.md`'s mode-select rows are text-only today (zero icon/sprite
  references). Only `teamfight` (→ ArenaMode's own TDM) and `checkpoint` (→ the still-unbuilt single
  control-point mode) name anything ArenaMode has or plans; the other six logos (Duel/OneManArmy/
  BlastGang/ShootOut/LastHero/FinalRun) match no current or planned mode.
- **kit_badges** — `ArenaMode.Client/Kits/KitCatalog.cs` is a pure server-JSON kit catalog with no
  icon/category code today; these medallions are prep art for a kit-selection screen, not a drop-in.
- **objective** — `CONTROL-POINT-REVIEW.md` ("proposed 2026-09-16, nothing here is implemented") needs
  exactly a capture-percentage bar and a world/map marker; `ArenaFriendlyMarkers.cs`'s existing
  `WorldToViewportPoint` projection (already production-proven for teammate diamonds) already does the
  world-to-screen work needed for a static point marker instead of a moving one.
- **scoreboard** — `Effects/ArenaTracerColor.cs` already ships its own 8-color palette wired into
  friendly-marker and killfeed coloring; the measured 7-color palette here is redundant with it, and
  ArenaMode has no post-round results screen for the row-background art to skin. Reference only.
- **mvp** — "MVP" occurs nowhere in ArenaMode's ~140 docs; `ANNOUNCER-AWARDS-REVIEW.md`'s "awards" are
  in-raid kill-streak barks, not a post-match best-player concept. This flipbook is prep art for a
  not-yet-built feature, not a drop-in today.

## Build/test evidence (2026-09-29)

```
python tools/extract/extract_arena_ui.py
  -> OK: 159 item(s), 35.69 MiB, 12.5s wall -> work/extract/arena-ui
python tools/extract/extract_arena_ui.py --check
  -> OK: manifest and referenced files present under work/extract/arena-ui
python -m unittest tools.tests.test_arena_ui -v
  -> Ran 59 tests in 17.1s, OK
python -m unittest discover -s tools/tests -p "test_*.py"
  -> Ran 638 tests, OK (skipped=28) -- full pipeline suite, unaffected by this addition
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
  -> [ArenaMapPort] staged Arena UI sprites (optional, for ArenaMode) from .../work/extract/arena-ui
     into dist/.../arena-ui/ (0 Fehler)
```

`dist/BepInEx/plugins/ArenaMapPort/arena-ui/` after that build: `manifest.json` + the 7 group folders
(159 PNGs, 35 MiB), `previews/` correctly absent. No host raid test applies here — this is inert,
undeployed-to-`D:\SPT41` staging data with nothing in `ArenaMapPort.Client` reading it yet, exactly like
`arena-audio`'s own staging target.

## Ownership

Same split as `arena-audio.md`: extraction tooling and the read-only catalog live in **ArenaMapPort**
(`tools/extract/extract_arena_ui.py`, `arena_ui_catalog.json`); any actual HUD/UI consumption of the
staged PNGs belongs in **ArenaMode**, which this repo only reads. Nothing here is committed as pixels —
`arena_ui_catalog.json` holds names, labels and path_id hints only, matching `AGENTS.md`'s policy for
every other BSG-derived extractor in this pipeline.
