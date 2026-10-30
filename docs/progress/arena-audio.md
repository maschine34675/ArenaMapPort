# Arena audio extraction for ArenaMode — 2026-09-29

## Purpose

[`docs/ARENA-CONTENT-FOR-ARENAMODE.md`](../ARENA-CONTENT-FOR-ARENAMODE.md) (written in parallel this
session) surveys everything in the Arena 0.5.4.0 install worth adopting for the user's ArenaMode mod,
beyond the maps themselves. Its Tier A "English commentator voice pack" item and Tier A/B stinger/
ambience/SFX items need a read-only extraction step before ArenaMode can use any of it. This document is
that step's own write-up: `tools/extract/extract_arena_audio.py`, its source table, its category catalog
decisions, the manifest it produces (a CONTRACT for ArenaMode to read), and this session's real run
evidence. It does not wire anything into ArenaMode itself — see "Ownership" below.

## Sources

Every path and clip name below was verified against a live UnityPy read of the user's own installs this
session (never assume a path_id is stable; this pipeline selects by `m_Name`, path_ids are drift-detection
hints only, logged if they no longer match).

| Group | Source | Clips | Notes |
|---|---|---|---|
| `commentator` | Arena `StreamingAssets/Windows/assets/content/audio/phrases/arena/arenaspeakertagbankmanagereng.bundle` (598,344,048 bytes) | 1640 total, 1206 extracted | All mono, 44.1 kHz. 69 categories grouped by 30 `ArenaTagBank` tags; see "Catalog decisions" below. |
| `stingers` | **EFT's own** `D:/SPT41/EscapeFromTarkov_Data/resources.assets` — no Arena dependency | 3 (`arena_start`, `arena_win`, `arena_lose`) | Already ship inside the target client itself; zero extra versioning risk. |
| `crowd` | Arena `resources.assets` | 1 (`arena_fans_ambient_postmatch`, 30.31 s stereo) | Post-match results-screen ambience loop (see "Suggested ArenaMode cue mapping" below for why, not the live spectator camera). |
| `spectator_glitch` | Arena `resources.assets` | 6 (`arena_cam_digital_glitch_01`–`05`, `arena_cam_glitch_loop_02`) | All found directly in `resources.assets`; the separate `arenasoundeffectswrapper.bundle` the task brief also allowed was not needed. |
| `capture` | Arena `StreamingAssets/Windows/assets/content/audio/arena/soundeffects/arenasoundeffectstagbankmanager.bundle` (1,134,864 bytes) | 2 (`arena_point_captured_ally`/`_enemy`) | Bundle path_ids are large signed 64-bit values (e.g. `-6444912900452477379`) — expected for a `.bundle`, unlike the small ids in `.assets` files. |
| `round_signal` | Arena `StreamingAssets/Windows/assets/content/audio/phrases/arena_effects.bundle` (299,056 bytes) | 1 (`asrena_start_signal_dry`) | The misspelling ("asrena") is the source object's own name, kept verbatim. |
| `result_music` | Arena `resources.assets` | 5 (`result_music_start`/`_loop`/`_end_winner`/`_end_looser`, `matchend_music`) | Genuinely odd native sample rates (23,550–28,045 Hz) — confirmed, not a bug; resampled to 44100 Hz like every other group. |

## Catalog decisions

`tools/extract/arena_audio_catalog.json` (git-tracked, category **names** only, never audio) classifies
every one of the commentator bank's 69 categories as included (with a reason) or excluded (with a
reason). Earlier passes of this catalog inferred that classification from clip-NAME prefixes alone (a
category starting `bg_`/`eng_bg_` is Blast-Gang-only, everything else is generic) — a heuristic, not
evidence, and it got two categories wrong (see "Tag evidence" below). **This pass replaces that heuristic
with the bank's own ground truth**: the commentator bank holds 30 `ArenaTagBank` MonoBehaviours, one per
in-game EVENT tag (`StartGame`, `TeamLead`, `KillAllWin`, `OvertimeStart`, …), each listing exactly which
AudioClips it fires for in its own `Clips` field. `read_tag_bank()` resolves every tag's `Clips` PPtrs
back to categories; the catalog records each category's tag(s) as `arena_tag`, and `validate_tag_bank()`
re-derives this mapping from a live bank read on every extraction run and raises `HardExtractError` if
the catalog and the bank ever disagree, if the bank has a tag the catalog's `known_tags` does not know, or
if a tag references a category the catalog's `categories` list does not know — the three failure modes
`tools/tests/test_arena_audio.py`'s `ValidateTagBankTests` exercise directly with fake data. **54
categories / 1207 clips are included; 15 categories / 433 clips are excluded** — 14 of those are
referenced only by a Blast Gang-mode tag (bomb-plant mode; AGENTS.md: ArenaMode targets TDM/FFA, Blast
Gang is not planned), plus one Arena-specific exclusion unrelated to Blast Gang (see below).
`classify_categories()` still raises if the live bank ever contains a category the catalog does not know,
or is ever missing an included one, on top of the tag cross-check — the catalog and the bank must agree
exactly, not "close enough."

### Tag evidence: every tag → its categories → included/excluded → suggested cue

`mode` is this catalog's own `known_tags` classification: `generic` (usable in TDM/FFA), `blast_gang`
(Blast Gang only, excluded), or `arena_specific` (excluded, but not Blast-Gang-gated — one tag,
`PurifiedTeamAppearance`, below).

| Tag | mode | Categories (clip count) | Included? | Suggested ArenaMode cue |
|---|---|---|---|---|
| `StartGame` | generic | `start`(30) | yes | `prepare_to_fight` (alt) |
| `StartRound` | generic | `start_round`(30) | yes | `round_start` |
| `CountDown` | generic | `count_down`(1) | yes | `prepare_to_fight` |
| `FirstBlood` | generic | `first_blood`(30) | yes | `first_blood` |
| `KillingSpree` | generic | `killing_streak`(21) | yes | *unmapped* — ArenaMode's own UT-style per-tier lines stay primary (see below) |
| `PlayerDead` | generic | `player_death`(29), `tm_player_death_blue`(21), `tm_player_death_red`(21) | yes | *unmapped* — no ArenaMode cue for a generic/team on-death bark yet |
| `LowDeathIntensity` | generic | `low_death_intensity`(25) | yes | *unmapped* |
| `HighDeathIntensity` | generic | `high_death_intensity`(25) | yes | *unmapped* |
| `TeamLead` | generic | `{red,blue}_team_lead_match_{start,middle,end}`(23 each, 6 cats) | yes | `taken_the_lead` / `lost_the_lead` (`tied_for_the_lead` deliberately unmapped: every TeamLead line names a leading team) |
| `KillAllWin` | generic | `bg_kill_all_win_blue`(27), `eng_kill_all_win_red`(33) | yes | *unmapped* — kept for a possible future team-wipe announcement; **not** `shutdown` (see below) |
| `MatchWon` | generic | `eng_shootout_battle_end_victory`(17), `eng_clash_draw`(12), `eng_round_draw`(14), `eng_tm_round_end_blue`(19), `eng_tm_round_end_red`(20) | yes | `round_won`/`round_lost` (`eng_tm_round_end_*`), `draw` (`eng_round_draw`/`eng_clash_draw`); `eng_shootout_battle_end_victory` unmapped (FFA, future) |
| `MatchEnd` | generic | `shootout_battle_end_victory_{blue,green,pink,red,white,yellow}`(18 each), `eng_clash_draw`(12), `eng_match_end_blue`(27), `eng_match_end_red`(26) | yes | `match_won`/`match_lost` (`eng_match_end_*` + the 6 ShootOut colours, future FFA); `draw` (`eng_clash_draw`) |
| `NextRound` | generic | `shootout_next_round`(26), `shootout_next_battle`(26), `eng_final_battle`(32) | yes | `final_round` (`eng_final_battle`); ShootOut pair unmapped (FFA, future) |
| `TimeExpire` | generic | `end_of_time`(24) | yes | *unmapped* |
| `CheckPointTimeExpired` | generic | `1_min_left`(24), `3_min_left`(24) | yes | `one_minute_remaining` (`1_min_left`); `3_min_left` unmapped (no matching ArenaMode cue key) |
| `UnfairGame` | generic | `unfair_play`(5) | yes | *unmapped* |
| `OvertimeStart` | generic | `bg_overtime_start`(26) | **yes** (was wrongly excluded — see below) | *unmapped* — no ArenaMode overtime cue exists yet, but a future one can reuse this line (see `docs/ARENA-CONTENT-FOR-ARENAMODE.md`'s Tier A "TDM overtime" item) |
| `CapturePointAppearance` | generic | `capture_point_live`(24) | yes | *unmapped* — no Control Point mode built |
| `CapturedPoint` | generic | 12 point-captured categories(23 each) | yes | *unmapped* |
| `AllPointsCaptured` | generic | `all_points_{blue,red}_team_captured`(23 each) | yes | *unmapped* |
| `PurifiedTeamAppearance` | **arena_specific** | `tm_sweep_team`(21) | **no** (was wrongly included — see below) | n/a |
| `TeamSwapped` | blast_gang | `bg_roles_changed`(29) | no | n/a |
| `SpecialItemPlanted` | blast_gang | `bg_special_item_planted`(28) | no | n/a |
| `SpecialItemDefusedTeamWin` | blast_gang | `eng_bg_item_defused_team_win_{blue,red}`(31/30) | no | n/a |
| `AttackTeamWin` | blast_gang | `eng_bg_attack_team_win_{blue,red}`(38/33) | no | n/a |
| `DefendTeamWin` | blast_gang | `eng_bg_defend_team_win_{blue,red}`(27/31) | no | n/a |
| `TimeExpiredAttackTeamWin` | blast_gang | `bg_{blue,red}_defend_team_win_time`(27 each) | no | n/a |
| `TeamDiedButWin` | blast_gang | `bg_team_died_but_win_{blue,red}`(27 each) | no | n/a |
| `ToxinStart` | blast_gang | `bg_toxin_start`(21) | no | n/a |
| `BlastGangMatchendDraw` | blast_gang | `bg_match_end_draw`(36) | no | n/a |

`blast_gang`-mode tags sum to 412 clips / 32.26 min (14 categories); the one `arena_specific` exclusion
(`tm_sweep_team`) is 21 clips / 1.82 min — both figures independently re-derived this session by summing
`m_Length` across the live bank's own `AudioClip` objects for exactly those categories (never the bank-wide
mean scaled by clip count, which would be off — see `docs/ARENA-CONTENT-FOR-ARENAMODE.md`'s own note on
this same pitfall).

**Two corrections this pass, both found by cross-checking the bank's own tags instead of trusting clip-name
prefixes:**

1. **`eng_commentator_bg_overtime_start` was wrongly excluded.** Its clip name carries a `bg_` prefix, but
   its real tag is `OvertimeStart` — a dedicated MonoBehaviour with no Blast-Gang-specific members at all
   (unlike `AttackTeamWin`/`ToxinStart`/etc., which really are Blast-Gang-only tags). The `bg_` prefix here
   is naming only, not a mode gate. **Now included** (`family: "overtime"`). No ArenaMode cue consumes it
   yet, but `docs/ARENA-CONTENT-FOR-ARENAMODE.md`'s Tier A "TDM overtime" item can now reuse this line
   instead of needing a brand-new ElevenLabs recording, as that doc previously assumed.
2. **`eng_commentator_tm_sweep_team` was wrongly included** (previously catalogued `family: "team"`, reason
   "one team sweeps the other"). Its real tag is `PurifiedTeamAppearance` — an Arena-specific "Purified"
   team spawn event, unrelated to a team sweep/wipe despite the category's own name. **Now excluded**
   (`family: "arena_specific_excluded"`, distinct from `blast_gang_excluded` since this has nothing to do
   with Blast Gang). ArenaMode has no Purified-team mechanic to attach this line to.

Both corrections net out to the same totals as the catalog's previous (already-corrected-once) state — 54
included / 15 excluded — because one category moved into the included set as the other moved out; the
*clip* counts shifted underneath that stable category count (1202 → 1207 included clips), and
`eng_commentator_bg_match_end_draw` (`BlastGangMatchendDraw` tag, genuinely Blast Gang) stays excluded as
before. Every other category's classification from the prior pass is confirmed correct by this tag
evidence, including the `eng_kill_all_win_red`/`bg_kill_all_win_blue` pair (`KillAllWin` tag, generic) and
`eng_final_battle` (`NextRound` tag, generic) — both previously included on the (correct, in hindsight)
strength of a same-family colour-pair/thematic argument, now confirmed directly rather than inferred.

The eight families in the catalog (`family` field, informational; `overtime` and `arena_specific_excluded`
are new this pass):

| Family | Categories | Clips | What |
|---|---|---|---|
| `general_time` | 13 | 300 | Match-flow/kill-pace lines usable in any mode: start/round-start/countdown, first blood, kill streak, on-death, death-intensity color commentary, 1/3-min-left, end-of-time, final battle, unfair-play. |
| `team` | 14 | 332 | Team-colored TDM lines (BEAR=red, USEC=blue — see cue mapping below): team-leading-the-match (start/middle/end × 2 colors), match end, round end, player death, team wipe. |
| `draw_overtime` | 2 | 26 | Round/clash draw lines (`eng_round_draw`, `eng_clash_draw`). |
| `overtime` | 1 | 26 | Overtime begins (`bg_overtime_start`, `OvertimeStart` tag — generic despite the `bg_` prefix). |
| `ffa_shootout` | 9 | 177 | Per-color (6) and generic ShootOut battle-victory lines, next-battle/next-round. |
| `capture` | 15 | 346 | Capture-point contest/capture lines (not wired to any ArenaMode mode today — TDM only — kept for a future Capture Point mode per `CONTROL-POINT-REVIEW.md`). |
| `blast_gang_excluded` | 14 | 412 | Attack/defend-team-win, item-defused, special-item-planted, roles-changed, toxin-start, team-died-but-win, defend-team-win-time, match-draw — every category referenced *only* by a `blast_gang`-mode tag. |
| `arena_specific_excluded` | 1 | 21 | `tm_sweep_team` (`PurifiedTeamAppearance` tag) — Arena-specific, not Blast-Gang-gated. |

## Output layout and manifest schema — CONTRACT for ArenaMode

Deployed to `BepInEx/plugins/ArenaMapPort/arena-audio/manifest.json` by
`ArenaMapPortStageArenaAudio` in `ArenaMapPort.Client.csproj` (runs on every Release build, DllOnly
included; prints a high-importance build message and skips silently, no error, if
`work/extract/arena-audio/manifest.json` does not exist yet — this data is **optional**, ArenaMapPort
itself never reads or plays any of it). ArenaMode may read this folder as an **optional** input: absence
must never break ArenaMode's own announcer.

```
arena-audio/
  manifest.json
  stingers/arena_start.ogg, arena_win.ogg, arena_lose.ogg
  crowd/arena_fans_ambient_postmatch.ogg
  spectator_glitch/arena_cam_digital_glitch_01.ogg … _05.ogg, arena_cam_glitch_loop_02.ogg
  capture/arena_point_captured_ally.ogg, arena_point_captured_enemy.ogg
  round_signal/asrena_start_signal_dry.ogg
  result_music/result_music_start.ogg, _loop.ogg, _end_winner.ogg, _end_looser.ogg, matchend_music.ogg
  commentator/<category>/<clip_name>.ogg   (54 category folders, e.g. commentator/eng_commentator_start/eng_commentator_start_01.ogg)
```

Every file is OGG Vorbis, `-q:a 5`, 44100 Hz, source channel count preserved (mono commentator lines,
stereo everything else) — the same format ArenaMode's own `AnnouncerService.cs` already loads via
`UnityWebRequestMultimedia.GetAudioClip(AudioType.OGGVORBIS)`.

`manifest.json` (`schema_version: 1`):

```jsonc
{
  "schema_version": 1,
  "generator": "extract_arena_audio.py",
  "generated_at": "<ISO 8601 UTC>",
  "sources": [                    // one entry per physical source file actually read
    {"root": "arena"|"eft", "file": "<path relative to that install's *_Data dir>",
     "size": <bytes>, "mtime": "<ISO 8601 UTC>", "unity_version": "<e.g. 6000.3.6f1>"}
  ],
  "groups": {
    "stingers": {"clips": [ClipEntry, ...]},
    "crowd": {"clips": [...]}, "spectator_glitch": {"clips": [...]}, "capture": {"clips": [...]},
    "round_signal": {"clips": [...]}, "result_music": {"clips": [...]},
    "commentator": {
      "categories": {
        "<category>": {"family": "<general_time|team|draw_overtime|overtime|ffa_shootout|capture>",
                       "reason": "<why included>", "arena_tag": ["<ArenaTagBank tag name>", ...],
                       "clips": [ClipEntry, ...]}
      },
      "excluded_categories": {"<category>": {"reason": "<why excluded>", "arena_tag": [...]}},
      "skipped_clips": [{"name": "<clip>", "category": "<category>", "reason": "<why skipped>"}]
    }
  },
  "suggested_arenamode_cues": { "<ArenaMode cue key>": [Pick, ...] }   // see below
}
```

`ClipEntry`: `{name, file (relative path from this manifest), duration_s, channels, sample_rate,
source_duration_s, source_rms}` (+ `path_id_hint`/`path_id_matched` for the six small named-clip groups
only). `Pick`: `{group, category|clip, team?, note?}`. `arena_tag`: the in-game `ArenaTagBank` event
tag(s) this category belongs to (see "Tag evidence" above) — a list because a category can belong to more
than one tag (`eng_clash_draw`: `["MatchEnd", "MatchWon"]`); every category's `arena_tag` is cross-checked
against a live bank read on every extraction run (`validate_tag_bank()`), new this pass.

`skipped_clips` exists because one real source clip failed validation this run (see "Measured results"):
a category's `clips` list can therefore be shorter than its full authored variant count, but every
**included** category is guaranteed non-empty (`extract_commentator` raises `HardExtractError` — the
whole run fails — if a category would otherwise end up with zero surviving clips; a single bad variant
among many is tolerated, an unplayable category is not). The six small named-clip groups have no such
tolerance: every listed clip is individually required, and any one failing still aborts the whole run.

## Suggested ArenaMode cue mapping

Read from `ArenaMode.Client/Audio/AnnouncerCueCatalog.cs` (`ArenaAnnouncerCue`/`Classify()`/
`OptionalReserveKeys`) and `ArenaMode.Server/Services/TeamDeathmatchArmbandPolicy.cs` (`UsecBlue`,
`BearRed` — **TDM sides: BEAR = red, USEC = blue**). This table is generated data
(`SUGGESTED_ARENAMODE_CUES` in `extract_arena_audio.py`, embedded verbatim in every manifest's
`suggested_arenamode_cues`) — keep this table and that dict in sync by hand if either changes.
`Team`-marked picks need the caller to resolve red/blue at runtime; the commentator always speaks about
a team **by colour**, never "you" — ArenaMode integration must translate "my team"/"the enemy" into
BEAR/USEC → red/blue itself, this extractor cannot know that.

Cue keys below are the real ones `ArenaMode.Client.Modes.ArenaAnnouncerCue` /
`AnnouncerCueCatalog.Classify()` / `AnnouncerService.CalloutPriority` actually key sound lookups by
(D:/SPT41/Development/ArenaMode, read-only, re-verified this pass). **A prior version of this table
included `double_kill`/`triple_kill`/`monster_kill`/`multi_kill`/`godlike`/`unstoppable`/`dominating`/
`rampage`/`killing_spree` entries — none of those exist as `ArenaAnnouncerCue` values, and while they
*are* real HUD kill-label keys `AnnouncerService` plays audio for, ArenaMode already owns its own
per-tier ElevenLabs recordings for them; Arena's bank has only the one generic `killing_streak` line
(`docs/ARENA-CONTENT-FOR-ARENAMODE.md`: "a UT-style kill ladder … Arena has [no] equivalent ladder"), so
pointing all nine at it would flatten ArenaMode's own ladder. Removed; `killing_streak` stays catalogued
but unmapped (see below).**

| ArenaMode cue | Picks |
|---|---|
| `round_start` | `commentator/eng_commentator_start_round` (`StartRound`); + `stingers/arena_start` and `round_signal/asrena_start_signal_dry` as optional pre-roll/underlay layers |
| `prepare_to_fight` | `commentator/eng_commentator_count_down` (`CountDown`, closer semantic match) or `commentator/eng_commentator_start` (`StartGame`, alternate) |
| `first_blood` | `commentator/eng_commentator_first_blood` (`FirstBlood`) |
| `round_won` / `round_lost` | `commentator/eng_commentator_eng_tm_round_end_{red,blue}` (`MatchWon` — Arena's own tag name for round-end audio, not a mismap) — resolve to the **round-winning** team's color |
| `match_won` / `match_lost` | `commentator/eng_commentator_eng_match_end_{red,blue}` (`MatchEnd`, TDM; resolve to the **match-winning** team's color) + `commentator/eng_commentator_shootout_battle_end_victory_{blue,green,pink,red,white,yellow}` (`MatchEnd`, FFA colour identity — **future**, not used by today's TDM-only ArenaMode) + `stingers/arena_win` (winner) or `arena_lose` (loser) as an SFX layer |
| `draw` | `commentator/eng_round_draw` (`MatchWon`, round-level naming) or `commentator/eng_clash_draw` (`MatchWon`/`MatchEnd`, clash/tiebreak-level naming) — Arena's own naming is round/clash-level, not whole-match, but these are the closest available lines for ArenaMode's match-level `Draw` cue (now mapped — see "Two corrections" above: neither category is Blast-Gang-scoped) |
| `taken_the_lead` / `lost_the_lead` | `commentator/eng_commentator_{red,blue}_team_lead_match_middle` (`TeamLead`) — resolve to the team **now** in the lead (both cues point at the same pair; only which color you resolve differs) |
| `one_minute_remaining` | `commentator/eng_commentator_1_min_left` (`CheckPointTimeExpired`) |
| `final_round` | `commentator/eng_commentator_eng_final_battle` (`NextRound`) |

Deliberately **not** mapped (no suitable Arena source, or ArenaMode's own recordings should stay
primary): `tied_for_the_lead` (the bank has no "tied" line and every `TeamLead` line names a leading team, so it would announce a false lead — lead review 2026-09-29), `last_man_standing`, `embarrassing`, `no_contest`, `clutch`, `payback`, `airborne`, `no_scope`,
`final_weapon`, `ladder_complete`, `match_point`, `five_kills_remaining`, `one_kill_remaining`,
`thirty_seconds_remaining`, `ten_seconds_remaining`, `three`, `two`, `one` (Arena's countdown "get ready"
line exists as `count_down`, above, but there is no per-digit three/two/one voice in this bank at all),
plus the nine kill-tier ladder keys removed above (`double_kill` … `killing_spree` — `killing_streak`
stays catalogued, available, just not wired to any of them), plus:

- `shutdown` — ArenaMode's `Shutdown` cue is a per-kill Award (`ArenaScoreboard.IsShutdown`: a single
  local kill that ends one opposing player's `AwardLifeStreak >= 5`, the match otherwise continuing;
  `AnnouncerCueCatalog` classifies it `AnnouncerCueKind.Award` next to `LastManStanding`/`Embarrassing`,
  and `AnnouncerService.CalloutPriority` ranks it alongside `monster_kill`/`multi_kill`/`first_blood` as
  an in-round personal callout). `eng_commentator_eng_kill_all_win_red`/`_bg_kill_all_win_blue`
  (`KillAllWin` tag) are match/round-outcome lines about eliminating the **entire** enemy team (family
  `team`, grouped with `eng_tm_round_end_*`/`eng_match_end_*`) — playing one after a single kill that
  merely ends one player's streak, while the rest of that team is still alive, would announce an event
  that has not happened. Both commentator categories stay **included** (still useful for a genuinely
  different, still-unmapped team-wipe concept if ArenaMode ever adds one), just not wired to this cue.

Not tied to a specific `AnnouncerCueCatalog` key, phase-based instead:

- `result_music/*` **and** `crowd`/`arena_fans_ambient_postmatch` — both belong to the **post-match
  results screen**, not the live in-round spectator camera (review finding, 2026-09-29: the clip's own
  name says `_postmatch`, and it sits thematically with `result_music`, not with the live death-cam
  below). `result_music/_start` opens the results screen, `_loop` sustains it, then
  `_end_winner`/`_end_looser` plays per the local outcome (or `matchend_music` as a generic alternative);
  `crowd`/`arena_fans_ambient_postmatch` is a looped, ducked spectator-fans ambience bed meant to run
  underneath that same screen, not during a live round.
- `spectator_glitch/*` — camera-switch transition SFX for `ArenaSpectatorService`'s **live, in-round**
  follow-camera (`StartFollowing`/`UpdateCurrentTargetFromInput` after a local death) — a different
  moment from the post-match phase above; do not use `crowd`/`arena_fans_ambient_postmatch` here.
- `capture/*` — Control-Point capture SFX for a future Capture Point mode; unused by today's TDM-only
  ArenaMode.
- 4 commentator families (`ffa_shootout`, `capture`, the FFA-color subset of `team`, and `overtime`) are
  extracted and catalogued now but likewise have no current ArenaMode consumer — kept for FFA/ShootOut,
  Capture Point and TDM-overtime modes if/when built, per `docs/ARENA-CONTENT-FOR-ARENAMODE.md`.

## Usage notes

- **Random variant per category, no immediate repeats.** Every included category has multiple clips
  (2 for the smallest sensible groupings up to 38) **except `eng_commentator_count_down`, which has
  exactly one** (the bank's only bare-name category, no numeric suffix at all — see "Catalog decisions"
  above); pick uniformly at random each time a cue fires, and remember the last 1–2 picks per category to
  avoid an audible immediate repeat — for `count_down` specifically, that anti-repeat logic simply has
  nothing to avoid repeating against, and integrators should not assume every category has ≥2 variants.
  This extractor does not pick for ArenaMode, it only ships every variant so ArenaMode can.
- **Duck `crowd`/`result_music` under combat/spoken lines**, the same way ArenaMode already treats its own
  music/ambience beds relative to spoken announcer lines.
- **Play `stingers`/`round_signal` as short layers alongside a spoken commentator line, never as a
  replacement for it** — they are all under 2 seconds (except the loop) and read as stings/signals, not
  full announcements.

## Measured results (real run, 2026-09-29, this session)

```
python tools/extract/extract_arena_audio.py
[capture] 2/2 clips ok
[crowd] 1/1 clips ok
[result_music] 5/5 clips ok
[round_signal] 1/1 clips ok
[spectator_glitch] 6/6 clips ok
[stingers] 3/3 clips ok
[commentator] scanning bank: 1640 clips
[commentator] 69 categories: 56 included, 13 excluded
  [warn] commentator/eng_commentator_eng_match_end_red/eng_commentator_eng_match_end_red_07: skipped
    (ArenaAudio: eng_commentator_eng_match_end_red_07: decoded PCM looks silent (rms=3.5, threshold=40.0))
[commentator] 1263/1264 clips ok, 1 skipped
OK: 1281 clip(s), 59.27 MiB, 80.1s wall -> D:\ArenaMapPort\work\extract\arena-audio
```

`eng_commentator_eng_match_end_red_07` decodes cleanly (no decode error) but to real, near-silent PCM
(peak amplitude 11/32768, a genuine flaw in Arena's own recording, not this pipeline) — correctly caught
by the RMS silence guard and skipped; `eng_commentator_eng_match_end_red` still has 25 other surviving
variants, so this did not trip the "category left with zero clips" hard-failure path. Every other
skip/failure path (missing clip, duration/format drift after encode, unknown/missing catalog category)
was exercised only by the unit tests, never triggered for real.

### Re-run after review fixes (2026-09-29, same session)

The review that followed this initial run found `eng_commentator_bg_match_end_draw`/`_bg_overtime_start`
were catalogued as generically included without the same "despite the `bg_` prefix" evidence the sibling
`bg_kill_all_win_blue` entry has (see "A reclassified ambiguity" above); both are now excluded, which
changes the commentator group's included/excluded split and total clip count. Re-running the full
extraction after that catalog fix:

```
python tools/extract/extract_arena_audio.py
[capture] 2/2 clips ok
[crowd] 1/1 clips ok
[result_music] 5/5 clips ok
[round_signal] 1/1 clips ok
[spectator_glitch] 6/6 clips ok
[stingers] 3/3 clips ok
[commentator] scanning bank: 1640 clips
[commentator] 69 categories: 54 included, 15 excluded
  [warn] commentator/eng_commentator_eng_match_end_red/eng_commentator_eng_match_end_red_07: skipped
    (ArenaAudio: eng_commentator_eng_match_end_red_07: decoded PCM looks silent (rms=3.5, threshold=40.0))
[commentator] 1201/1202 clips ok, 1 skipped
OK: 1219 clip(s), 56.47 MiB, 61.6s wall -> D:\ArenaMapPort\work\extract\arena-audio
```

Same single skip as the first run (`eng_commentator_eng_match_end_red_07`, unrelated to this fix); 62
fewer commentator clips extracted (36 `bg_match_end_draw` + 26 `bg_overtime_start`, both now excluded).
The now-orphaned `commentator/eng_commentator_bg_match_end_draw/` and `_bg_overtime_start/` folders from
the first run's output were deleted from `work/extract/arena-audio/` (gitignored; not referenced by the
new manifest).

`python -m unittest discover -s tools/tests -p "test_*.py"`: 527 tests, all pass (28 skipped, unrelated
to this work) — includes `test_arena_audio.py`'s 48 tests (the original 45 plus 3 added while fixing the
review findings: `test_missing_family_raises`/`test_empty_family_raises` for `load_catalog()`'s new
`family` check, and `test_shutdown_and_draw_stay_unmapped` guarding the `shutdown`/`draw` cue removals).

`dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true`
staged the corrected output: `dist/BepInEx/plugins/ArenaMapPort/arena-audio/manifest.json` present, 1220
files (1219 clips + the manifest) under that folder, 56.94 MiB on disk (the stale 1282-file `dist/`
folder from the first run was removed first, since the csproj `Copy` target does not delete files that
disappeared from its source — a `dist/`-only, gitignored, harmless staleness, not a correctness issue in
the source `work/` output or the manifest it copies).

`python tools/extract/extract_arena_audio.py --check`: `OK`, exit 0, against the corrected real output
above.

### Re-run with tag evidence (2026-09-29, later same session)

The lead's `announcer_tags.py`/`announcer_tags.txt` read of the bank's 30 `ArenaTagBank` MonoBehaviours
(see "Catalog decisions" above) found the prefix heuristic behind both prior runs had gotten two
categories wrong: `eng_commentator_bg_overtime_start`'s real tag (`OvertimeStart`) is generic, not
Blast-Gang-gated, and `eng_commentator_tm_sweep_team`'s real tag (`PurifiedTeamAppearance`) is an
Arena-specific event unrelated to a team sweep, not the generic `team`-family line it was catalogued as.
`arena_audio_catalog.json` gained a top-level `known_tags` dict and a per-category `arena_tag` field;
`extract_arena_audio.py` gained `read_tag_bank()`/`validate_tag_bank()`, which cross-check every
category's `arena_tag` against a live bank read on every run. Re-running the full extraction after both
the catalog fix and the extractor change:

```
python tools/extract/extract_arena_audio.py
[capture] 2/2 clips ok
[crowd] 1/1 clips ok
[result_music] 5/5 clips ok
[round_signal] 1/1 clips ok
[spectator_glitch] 6/6 clips ok
[stingers] 3/3 clips ok
[commentator] scanning bank: 1640 clips
[commentator] 69 categories: 54 included, 15 excluded
  [warn] commentator/eng_commentator_eng_match_end_red/eng_commentator_eng_match_end_red_07: skipped (ArenaAudio: eng_commentator_eng_match_end_red_07: decoded PCM looks silent (rms=3.5, threshold=40.0))
[commentator] 1206/1207 clips ok, 1 skipped
OK: 1224 clip(s), 56.61 MiB, 59.8s wall -> D:\ArenaMapPort\work\extract\arena-audio
```

The included/excluded *category* counts stay 54/15 (one category moved each way), but the included
*clip* count moved from 1202 to 1207 (net +5: `bg_overtime_start`'s 26 clips in, `tm_sweep_team`'s 21
clips out) — 1206 after the same pre-existing silent-clip skip as every prior run. `validate_tag_bank()`
ran clean against the live bank (no `HardExtractError`) on this run, confirming every one of the other 67
categories' `arena_tag` already matched the bank exactly. `python tools/extract/extract_arena_audio.py
--check`: `OK`, exit 0.

`python -m unittest discover -s tools/tests -p "test_*.py"`: `tools/tests/test_arena_audio.py` now runs
78 tests (up from 48), all pass — the added `ValidateTagBankTests`/`WalkPptrCategoriesTests`/
`InvertTagCategoryMapTests` classes exercise the three new failure modes (disagreeing tag, unknown tag,
unknown category in a tag) entirely with fake data, never a live bank read; `LoadCatalogValidationTests`
gained fixtures for the new `known_tags`/`arena_tag` fields and the Blast-Gang/generic/arena_specific
mode-vs-`included` biconditional; `CatalogIntegrityTests` gained direct checks that every category has a
non-empty `arena_tag` and that Blast-Gang-mode tags correspond exactly to `blast_gang_excluded`
categories; `SuggestedCuesTests` gained checks that the nine bogus kill-tier cue keys stay absent and
that `draw` is now present (`tied_for_the_lead` was mapped by that pass too and removed again by the lead review, see "Deliberately not mapped").

`dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true`
staged the corrected output after first removing the stale `dist/BepInEx/plugins/ArenaMapPort/arena-audio`
folder from the prior run (gitignored build output, not deleted by the csproj's own `Copy` target):
`dist/BepInEx/plugins/ArenaMapPort/arena-audio/manifest.json` present, 1225 files (1224 clips + the
manifest) under that folder, ~60 MiB on disk.

No host raid test applies to this task — this is a pure Python/ffmpeg extraction step with no client
runtime component of its own; nothing in `ArenaMapPort.Client` reads or plays any of this data.

## Ownership

Extraction stays in **this repo** (ArenaMapPort): the tooling, the category catalog, and this manifest
contract. Wiring any of it into ArenaMode's `AnnouncerCueCatalog`/`AnnouncerService` — reading this
manifest, resolving `team` picks to BEAR/USEC, adding a voice-pack selector — is a **separate task in the
ArenaMode project**, per `docs/ARENA-CONTENT-FOR-ARENAMODE.md`'s ownership split. Nothing in
`ArenaMapPort.Client`/`ArenaMapPort.Server` reads this data; it exists solely to be staged for ArenaMode
to optionally pick up.

## Distribution

BSG-derived (Arena's own audio, and — for the three `stingers` clips only — EFT's own resources.assets):
extracted **locally and read-only** from the user's own installs, never redistributed in source form and
never committed to git. Only `work/extract/arena-audio/` (this extractor's default `--output`, gitignored)
and `dist/` (gitignored, what the client csproj stages) ever hold the actual `.ogg` bytes. The only
git-tracked file is `tools/extract/arena_audio_catalog.json`, which names commentator **categories** and
the in-game `ArenaTagBank` event **tag names** they belong to, never audio — matching this project's
existing policy for banners, deploy art, native spatial audio, decals and MultiFlare (AGENTS.md).

## `other` group — unassigned audio (2026-09-29, user request)

User request: *"export the unassigned clips into an 'other'/'Sonstiges' category too, maybe they're
useful for something else."* `manifest.groups.other` is a catch-all: every clip in it is explicitly
**unassigned** — `other.note` says so, and none of them appear anywhere in `suggested_arenamode_cues`.
Two independently-selected parts, both under `other/<part>/...`:

### Part 1 — `other/commentator/<category>/<clip>.ogg`: the 15 excluded commentator categories

Every clip in the 15 commentator categories `arena_audio_catalog.json` excludes (14 Blast-Gang-tagged, 1
arena-specific — see "Catalog decisions"/"Tag evidence" above) — nothing about that classification
changed: `classify_categories()`, `read_tag_bank()`/`validate_tag_bank()` and the catalog itself are
untouched, and `scan_commentator_bank()` now shares one bank scan between the main `commentator` group
and this part instead of opening the 598 MB bundle twice in a default run. "Excluded" always meant "not
in the suggested ArenaMode set", not "not exported" — each category keeps its `family`, `reason` and
`arena_tag` in the manifest, same shape as the main group's categories.

| Metric | Value |
|---|---|
| Categories | 15 (all of them — every excluded category has ≥1 surviving clip) |
| Clips | 433 |
| Size | 21.65 MiB |
| Skipped (silent/undecodable) | 0 |

### Part 2 — `other/arena/<source_key>/<clip>.ogg`: unassigned Arena-only clips

Every `AudioClip` in `OTHER_ARENA_SOURCES` (Arena's own `resources.assets`, plus the `blastgang`/
`soundeffects`/`arenaaudio`/`arena_effects` bundles) whose name is not already claimed by another group
(including the commentator bank itself, included or excluded — `arena_claimed_names()`) and does not
exist as an `AudioClip` name in EFT 0.16.9.5's own `resources.assets` (`eft_audio_clip_names()`, 508
names) — so shared EFT audio (weapon/footstep/ambience names Arena kept verbatim from its EFT fork) is
never duplicated. A name repeated across more than one source file is kept once, from the first source
file it appears in (`plan_other_arena_selection()`, pure/tested with fake data); every later repeat is
recorded in `other.arena_duplicate_clips`, not re-extracted. Each surviving clip gets a coarse, always-a-
guess `kind` (`ui`/`music`/`sfx`/`voice`, `kind_guess: true` always — this group is unclassified by
construction) from `guess_kind()`, a whole-token name heuristic (e.g. `arena_button_click` → `ui` via the
`button` token, `usec_03_ending_03` → `voice` via `ending`, everything unmatched → `sfx`).

**Enumeration before extraction** (task requirement — stop part 2 if projected OGG size would exceed 400
MiB): a throwaway scratch probe (`probe_other.py`, not part of the shipped pipeline) ran the same
selection logic ahead of time and found 875 `AudioClip` objects scanned across the 5 source files, 335
selected after the EFT-shared/already-claimed/duplicate filters, ~227.6 minutes total duration, a rough
q5-bitrate projection of ~168 MiB — comfortably under the 400 MiB cap, so extraction went ahead. The real
run came in well under that projection (see below), since the rough projection assumed a flat per-second
rate rather than actual libvorbis output size.

| Source (`OTHER_ARENA_SOURCES` key) | File | Clips exported | Size |
|---|---|---|---|
| `resources` | Arena `resources.assets` | 302 | 71.18 MiB |
| `blastgang` | `.../content/audio/arena/blastgang/blastgangsoundsso.bundle` | 14 | 0.77 MiB |
| `soundeffects` | `.../content/audio/arena/soundeffects/arenasoundeffectstagbankmanager.bundle` (beyond the 2 clips `capture` already exports from it) | 2 | 0.09 MiB |
| `arenaaudio` | `.../arena/resources/arenaaudio/arenasoundeffectswrapper.bundle` | 0 (every clip in it duplicates `resources.assets` or is already exported by `crowd`/`spectator_glitch`/`result_music`) | — |
| `arena_effects` | `.../content/audio/phrases/arena_effects.bundle` (beyond the 1 clip `round_signal` already exports from it) | 0 (no other clips in this file) | — |
| **Total** | | **318** | **72.04 MiB** |

`kind` distribution (all `kind_guess: true`): `sfx` 218, `voice` 76, `ui` 20, `music` 4. The `voice`
bucket is mostly long (~189 s) per-character "ending_0N" lines and `encircled`/`terminal_cutscene_*`
tracks — core-EFT-style character voice-over Arena ships under its own names, not part of the commentator
bank, kept here since nothing suggests an ArenaMode use for them yet. The `ui` bucket matches the task
brief's named examples exactly: `arena_button_click`/`_over`, `arena_tab_click`, `arena_menu_checkbox`/
`_dropdown`/`_dropdown_select`/`_escape`, `arena_preset_click`/`_over`, `arena_presets_screen_show`,
`arena_inspect_click`, `arena_magnifier_click`, `arena_popup_generic`/`_negative`/`_positive`/
`_attention`, `arena_match_ready_popup`, `arena_wtask_complete`. `music` (all 4): `arena_track_02`,
`arena_track_03`, `arena_wait_match_background_music`, `bp_tactical_map_music_background_01`.
`arena_mvp_widget_02` and `arena_mvp_postmatch` both guess `sfx` (neither name contains a `track`/`music`
token), as does `arena_railway_station_notification`. Also present, all `sfx`: the Blast Gang SFX kit
(`blastgangsoundsso.bundle`'s 14 clips — `blast_gang_light_on`, `bomb_signal_defuse`/`_plant`,
`blastgang_defuse_beep`/`_plant_beep`/`_bomb_beep_defuse`/`_bomb_beep_plant`/`_flare_explosion`/
`_backpack_defuse`/`_backpack_plant`/`_code_running_FP`/`_fireworks_loop`, `blast_gang_notebook_diffuse_loop`),
`arena_bomb_exploded_signal_01`/`_02`, `arena_railway_station_notification`.

17 clips skipped as silent (same `RMS_SILENCE_THRESHOLD=40.0` guard the rest of this pipeline uses, never
loosened for this group): 13 `bells_0N`/`bells_1N` chime variants, `amb_rain_indoor_medium`/`_weak`,
`random_scav_06`, `usec_04_ending_02`, `arena_preset_over`, `BP_1_map_loading`, `booz_2`/`_3` — plausibly
genuinely quiet source material (distant bells, light rain, a hover sound), not a pipeline bug; recorded
in `other.arena_skipped_clips`, never a hard failure (task brief: part 2 never fails the whole run for a
per-clip problem).

11 duplicate names recorded in `other.arena_duplicate_clips` (all `arenaaudio` repeats of a `resources`
clip, `resources` coming first in `OTHER_ARENA_SOURCES`): `spectator_start`, `arena_mvp_postmatch`,
`draw_shield`, `shine`, `small_score_2`, `win_shield`, `roll_anim`, `final_screen`, `loose_shield`,
`big_score`, `rollback_anim`.

### Voice banks deliberately NOT exported

The task brief explicitly excludes Arena's other three commentator voice banks under
`StreamingAssets/Windows/assets/content/audio/phrases/arena/` (same folder as the English bank this
pipeline's main `commentator`/`other.commentator` groups already extract from) — available on request,
not swept by any `--only` flag today:

| Bundle | Voice pack | Size | `AudioClip` objects |
|---|---|---|---|
| `arenaspeakertagbankmanager.bundle` | RU (default) | 524.3 MiB | 1640 |
| `arenageneburnspeakertagbankmanager.bundle` | Geneburn | 235.3 MiB | 785 |
| `arenageneburnspeakertagbankmanagerpreview.bundle` | Geneburn (preview) | 3.8 MiB | 13 |
| `arenaspeakertagbankmanagertagillaeng.bundle` | Tagilla (EN) | 404.5 MiB | 1496 |
| `arenaspeakertagbankmanagertagillaru.bundle` | Tagilla (RU) | 467.8 MiB | 1530 |
| **Total** | | **~1.6 GiB** | **5464** |

### Combined `other` totals and pipeline changes

751 clips (433 + 318), 93.69 MiB — well under the 400 MiB budget the task set for part 2 alone.
`extract_commentator()` was split into `scan_commentator_bank()` (one bank scan + catalog/tag-bank
validation, shared) + `extract_commentator()` (included categories, unchanged behavior/output) +
`extract_commentator_other()` (excluded categories, new); `extract_all()` now scans the bank once
whenever either `commentator` or `other` is requested. `--only other` builds both parts of `other` in one
flag (needs no prior `--only commentator` run); `--check` covers `other`'s different manifest shape
(`_iter_manifest_clip_lists()` extended for `groups.other.commentator.*.clips` /
`groups.other.arena.*.clips`, alongside the existing `clips`/`categories` shapes). Every one of the 7
pre-existing groups' output was confirmed byte-identical before/after this change (same manifest content
for `capture`/`crowd`/`result_music`/`round_signal`/`spectator_glitch`/`stingers`/`commentator`).

### Real run evidence (2026-09-29, this session)

```
python tools/extract/extract_arena_audio.py --only other
[commentator] scanning bank: 1640 clips
[commentator] 69 categories: 54 included, 15 excluded
[other/commentator] 433/433 clips ok
[other/arena] 335 clip(s) selected (875 scanned, 11 duplicate name(s) across sources)
  [warn] other/arena/resources/amb_rain_indoor_medium: skipped (... rms=23.3, threshold=40.0)
  ... (17 total, see "Voice banks"/skip list above)
[other/arena] 318/335 clips ok, 17 skipped
OK: 1975 clip(s), 150.30 MiB, 202.7s wall -> D:\ArenaMapPort\work\extract\arena-audio

python tools/extract/extract_arena_audio.py --check
OK: manifest and referenced files present under D:\ArenaMapPort\work\extract\arena-audio
```

Also re-ran the full default extraction (no `--only`, all 8 groups including `other` in one process) to
exercise the shared-bank-scan path where both `commentator` and `other/commentator` are requested
together: same 1975 clips / 150.30 MiB total, same single pre-existing `eng_commentator_eng_match_end_red_07`
skip in the main commentator group, same 17 `other/arena` skips — 316.3 s wall (bank scanned once, not
twice). `--check` passed again afterward.

`python -m unittest discover -s tools/tests -p "test_*.py"`: 638 tests, all pass (28 skipped, unrelated to
this work) — `test_arena_audio.py` now runs 100 tests (up from 80): `GuessKindTests`,
`ArenaClaimedNamesTests`, `PlanOtherArenaSelectionTests`, `ListAudioclipMetasTests` (all fake-data, no
UnityPy/install read) cover the selection/dedupe/EFT-name-filter/kind-guess logic directly; `OutputPathTests`
gained `other_commentator_output_path`/`other_arena_output_path` cases; `ManifestSchemaTests` gained
shape checks for `groups.other`; `CheckModeTests` gained a case proving `--check` reports/clears missing
files under `other.commentator.*.clips`/`other.arena.*.clips`; `GroupSourcesTests`/`RealSourceExtractionTests`
updated for `ALL_GROUPS` now including `other`. `pwsh tools/tests/test_unity_log_scan.ps1`: 8 cases, all
pass, unaffected (no client-log changes in this work).

`dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true`
staged the new folder with no csproj change needed on the audio side: `ArenaMapPortStageArenaAudio`'s
existing `Include="$(ArenaMapPortAudioDir)\**\*.*"` glob is already recursive, so `other/` was picked up
automatically. Confirmed `dist/BepInEx/plugins/ArenaMapPort/arena-audio/other/{commentator,arena}/` present
with exactly 751 `.ogg` files (433 + 318) after first removing the stale pre-`other` `dist/` folder.

**Note, 2026-10-30:** the post-match bed `crowd`/`arena_fans_ambient_postmatch` described above is now also used live by the port: on the user's decision (he said it sounds good) the
spectator code mixes it to mono, smooths its loop seam and plays it as a quiet murmur from every stand of the original crowd on all four maps, which is an addition of the port (Arena's maps carry
no crowd ambience). The catalogue entry above stays correct for ArenaMode's own results screen. See [spectator-audio.md](spectator-audio.md) "Crowd murmur bed and longer call pauses".
