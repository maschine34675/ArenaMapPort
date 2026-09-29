# Arena content for ArenaMode — 2026-09-29 survey

Purpose: beyond the maps themselves (already ported — see below), does EFT Arena 0.5.4.0 contain
anything else worth adopting for the user's ArenaMode mod (arena deathmatch with respawns, played on
the ported maps)? This document is the write-up of a one-shot, read-only survey run this session
against the user's own installs. It records what was found, what is recommended, what was rejected and
why (so nobody re-investigates a dead end), and the extraction/ownership split between this repo and
ArenaMode. It does not implement anything itself.

## Method

23 agents in four phases, all read-only, no writes under `D:/Battlestate Games`, `D:/SPT41`,
`C:/SPT41*` or this repo:

1. **Survey** — 7 area passes (audio, ui, rules, mapobjects, vfx, chars, systems) against
   `D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data` (UnityPy against
   `resources.assets` and the `StreamingAssets/Windows` bundles), cross-checked name-by-name against
   `D:/SPT41/EscapeFromTarkov_Data/resources.assets` and its `Windows.json` bundle manifest to tell
   genuinely Arena-exclusive content from what the target client (EFT 0.16.9.5) already ships.
2. **Verify** — one skeptic pass per area, independently re-deriving every candidate's evidence
   (byte counts, path_ids, durations) before it could survive to synthesis; each area's own "missed"
   findings (cited inline below) fed back into the synthesis.
3. **Synthesize** — the Tier A/B/C/rejected split below.
4. **Critic** — a completeness pass that ran three additional gap surveys end-to-end: BSG's own
   Arena-mode presets (spawn boxes, capture points) on full-size Tarkov maps, Arena's shared
   `PlayerSpirit` ghost/observer prefab family, and an unused/WIP "Zombies" PvE archetype. All three
   came back empty or already-covered (see Rejected, below) — closed, do not re-investigate.

Arena's `global-metadata.dat` (IL2CPP type/field/string metadata) is encrypted in this build (header
magic `97b3af54`, not the standard `FAB11BAF`; zero hits for guaranteed strings like `UnityEngine` or
`MonoBehaviour`) — confirmed independently in the systems pass. There is no managed code or class dump
available anywhere in this survey, and round timers, capture radii and economy formulas are server-side
and unreachable regardless. Every finding below is therefore either a literal, reusable asset (bundle
path, `path_id`, duration, position) or explicitly marked **design/taxonomy reference only**, never a
decompiled rule.

Arena's Unity bundles are built with **Unity 6000.3.6f1** (confirmed this session), a full major-version
gap from this project's Unity 2022.3.43f1 and from EFT 0.16.9.5's own 2022.3.43f1 — relevant wherever a
candidate below is a skinned/rigged mesh rather than a static asset.

## Already taken from Arena (context, not new work)

For contrast, what ArenaMapPort already carries over from Arena, beyond raw geometry: per-map native
features on Equator and Bay5 (NavMesh baked from every physics collider incl. modifiers/volumes, rain
cover, light culling, static projection decals, native spatial acoustics + room-dependent ambient
reverb, per-light MultiFlare, Bay5's harbor water, puddle footsteps — see `docs/progress/INDEX.md` for
the dated entries); deploy/menu art (location-select banners, `tools/extract/extract_banners.py`; the
per-map loading-screen image, `tools/extract/extract_loadscreens.py`); and map-side bot AI anchors
(`AICorePointRepairs`/`PatrolWayRepairs` synthesizing the AI-holder voxel grid and patrol ways Arena's
ported scenes lack, per `AGENTS.md`). This survey is about everything **else** in the Arena install.

## Tier A — high value, low/moderate effort, no unbuilt prerequisite

| Item | What (evidence) | ArenaMode use | Owner | Effort |
|---|---|---|---|---|
| **English commentator voice pack** (lead instruction, not a synthesis promotion — see provenance note below) | `StreamingAssets/Windows/assets/content/audio/phrases/arena/arenaspeakertagbankmanagereng.bundle`, 598,344,048 bytes; 1640 AudioClips in 69 categories (`<category>_<nn>`) — byte/clip/category counts confirmed in the verifier journal. All mono 44.1 kHz (1601 PCM, 37 Vorbis, 2 ADPCM), 118.8 min total, mean 4.35 s/clip — these sub-statistics were given directly by the lead, not present in the synthesis or journal; independently re-derived this session by enumerating every `AudioClip` in the same bundle with UnityPy 1.25.2 (`m_CompressionFormat`/`m_Channels`/`m_Frequency`/`m_Length`), which reproduces all of them exactly. A third-person red/blue sports commentator, not a UT-style kill ladder (no double/multi/monster-kill tier). | Optional, selectable announcer voice pack, **additive** to ArenaMode's existing 41–46-line ElevenLabs pack, not a replacement — kill-streak lines stay on ElevenLabs since Arena has no equivalent ladder. | ArenaMode | M — systematic category→cue mapping (round-flow, team-lead, capture, FFA-color, Blast Gang groups below), OGG conversion for 3 codecs, a voice-pack indirection over `AnnouncerCueCatalog` |
| `arena_win`/`arena_lose`/`arena_start` round stingers | Ship inside **EFT 0.16.9.5's own client**, not just Arena's: `D:/SPT41/EscapeFromTarkov_Data/resources.assets` path_ids 2925 (`arena_win`, 6.94 s), 2930 (`arena_lose`, 9.18 s), 3044 (`arena_start`, 10.01 s); zero managed-code references (dormant). | Musical bed under RoundStart/RoundWon/RoundLost spoken cues in every mode. | ArenaMode | S — zero Arena-install dependency, playback pattern already proven for hitmarkers |
| Spectator crowd-ambience loop | `arena_fans_ambient_postmatch`, 30.31 s stereo 44.1 kHz, path_id 4985, Arena `resources.assets`; absent from EFT's 508-name AudioClip set. | Ducked loop under `ArenaSpectatorService.cs` (990 lines, confirmed zero audio references today). | ArenaMode | S |
| F9 scoreboard K/D/A icon triplet | `lasthero_observer_kill`/`death`/`assist` sprites, 20×20 each, cut from a 60×20 texture (path_id 893, `sharedassets1.assets`); absent from EFT. | `ArenaPanel.cs` (553-line IMGUI table, plain-text `NAME/ROLE/STATE/K/D/A/HS/SCORE` headers at lines 448–455 — **not** `ScorePanel.cs`, a different HUD strip) via `GUI.DrawTexture`. | ArenaMode | S |
| TDM overtime / sudden-death instead of a flat draw | ~~Design reference only (no BSG audio ported for the mechanic itself — the only clip literally named "overtime", `bg_overtime_start`, was thought Blast-Gang-scoped by prefix)~~ — **corrected 2026-09-29 (tag evidence, see below):** `bg_overtime_start`'s real tag is `OvertimeStart`, a dedicated non-Blast-Gang MonoBehaviour; the line is confirmed generic and is now extracted/included (`tools/extract/arena_audio_catalog.json`, family `overtime`). | Replaces TDM's terminal `DRAW` with a decider round; needs a new `AnnouncerCueCatalog` "overtime" key, which **can now reuse `commentator/eng_commentator_bg_overtime_start`** instead of requiring a brand-new ElevenLabs recording as first assumed here. | ArenaMode | M — must reuse `TDM-MATCH-FLOW-REVIEW.md`'s existing terminal-result-lifecycle rigor (Redeploy races, HoldRespawn interception) rather than open a second exit path |
| FFA per-player color identity via mostly-native armbands | EFT 0.16.9.5 already ships native red/blue/green/white/yellow armband items; `TeamDeathmatchArmbandPolicy.cs` already force-assigns two of them for TDM bots. Only an exact "pink" match (Arena's 6th ShootOut palette color) needs anything pulled from Arena at all. | Extend the existing armband-forcing pattern to Deathmatch, stable-per-life color, not team affiliation. | ArenaMode | S |

### English commentator bank — 69 categories by purpose

**Provenance, precisely:** `survey:audio` filed the *entire* Arena speaker-tag-bank family — including
this exact bundle — under `not_useful`: "huge generic per-character/per-locale combat-bark voice
databases (bot barks, not match-flow announcer content) … offers no round-flow/announcer/UI-stinger
content distinct from what's already covered" (journal, agent `a20468f4dbaba8f4c`). The two synthesis
items that do cite this bank — Tier A "TDM overtime" and Tier B "second announcer voice pack" — use it
only as category-taxonomy evidence and explicitly keep Arena's own VO unused ("Arena's own VO is not
reused — a second pack still needs the user's own recording session"). No candidate anywhere in the
23-agent pipeline's own output proposes shipping this bundle's audio as a selectable ArenaMode voice
pack. That proposal, and its Tier A placement, is the lead's own correction for this task, made with
byte-level measurements the lead took independently of the survey — it is not something the survey
pipeline itself concluded or "kept" at design-reference status.

Full per-category counts are also in the survey scratchpad (`announcer_categories.txt`); grouped here
by apparent purpose (category counts and clip counts both reconcile exactly to 69 / 1640):

| Group | Categories | Clips | Representative names (clip count) |
|---|---|---|---|
| General / round-flow / time | 12 | 268 | `start`(30), `start_round`(30), `count_down`(1), `3_min_left`(24), `1_min_left`(24), `end_of_time`(24), `first_blood`(30), `player_death`(29), `killing_streak`(21), `high_death_intensity`(25), `low_death_intensity`(25), `unfair_play`(5) |
| Team red/blue (TDM-style) | 13 | 293 | `{blue,red}_team_lead_match_{start,middle,end}`(23 each), `eng_match_end_{blue,red}`(27/26), `eng_tm_round_end_{blue,red}`(19/20), `tm_player_death_{blue,red}`(21 each), `tm_sweep_team`(21) |
| Draw (generic, cross-mode) | 2 | 26 | `eng_round_draw`(14), `eng_clash_draw`(12) |
| FFA colors / ShootOut | 9 | 177 | `shootout_battle_end_victory_{blue,green,pink,red,white,yellow}`(18 each), ungendered `eng_shootout_battle_end_victory`(17), `shootout_next_battle`(26), `shootout_next_round`(26) |
| Capture points | 15 | 346 | `all_points_{blue,red}_team_captured`(23 each), `{blue,red,neutral}_point_{a,b,c}_{...}_captured`(23 each × 12), `capture_point_live`(24) |
| Blast Gang (bomb mode) | 18 | 530 (≈42.0 min) | `bg_blue/red_defend_team_win_time`(27 each), `bg_kill_all_win_blue`(27) / `eng_kill_all_win_red`(33), `bg_match_end_draw`(36), `bg_overtime_start`(26), `bg_roles_changed`(29), `bg_special_item_planted`(28), `bg_team_died_but_win_{blue,red}`(27 each), `bg_toxin_start`(21), `eng_bg_attack_team_win_{blue,red}`(38/33), `eng_bg_defend_team_win_{blue,red}`(27/31), `eng_bg_item_defused_team_win_{blue,red}`(31/30), `eng_final_battle`(32) |

**Ambiguity flagged as requested:** `eng_final_battle` and `eng_kill_all_win_red` carry no literal `bg_`
token; they are grouped into Blast Gang here because they mirror a `bg_`-prefixed sibling by theme
(`kill_all_win`) or fit the Blast-Gang duration/count profile — not because IL2CPP metadata confirms
mode-gating (it is encrypted and unreachable). Likewise, the "overtime" and "match-draw" categories used
as evidence for the Tier A TDM-overtime item above are both `bg_`-prefixed, i.e. named for Blast Gang
specifically; there is no confirmed generic-mode overtime category in this bank. RU, Geneburn and
Tagilla banks also exist as separate bundles (Geneburn: 246.7 MB EN + 3.9 MB preview; Tagilla: 424.1 MB
EN / 490.5 MB RU, 5 `voice_unlock` clips each) — read as a Tier B "second voice pack" reference, below,
not part of this Tier A item.

**Correction, 2026-09-29 (later same session) — tag evidence, superseding the grouping above:** the table
and paragraph above classified categories by clip-NAME prefix alone (`bg_`/`eng_bg_` = Blast Gang), which
this session's own text already flagged as an inference, not a confirmed fact, for two names. The lead
then read the bank's 30 `ArenaTagBank` MonoBehaviours directly (`announcer_tags.py`, each tag's own
`Clips` field lists exactly which categories it fires for — ground truth, not an inference from naming)
and found the prefix heuristic had **four** groupings wrong, not two:

- **`eng_final_battle`(32) is NOT Blast Gang** — its tag is `NextRound` (generic: also covers
  `shootout_next_round`/`shootout_next_battle`). Belongs in **General / round-flow / time**.
- **`eng_kill_all_win_red`(33) and `bg_kill_all_win_blue`(27) are NOT Blast Gang, either one** — both
  share the one `KillAllWin` tag (generic), a same-family red/blue pair despite the `bg_` prefix on the
  blue side. Belong in **Team red/blue**.
- **`bg_overtime_start`(26) is NOT Blast Gang** — its tag is `OvertimeStart`, a dedicated MonoBehaviour
  with no Blast-Gang-only members at all; the `bg_` prefix is naming only. Belongs in a new **Overtime
  (generic)** row. This directly changes the Tier A "TDM overtime" item's own conclusion below: a future
  overtime cue **can** reuse this Arena line, it does not necessarily need a brand-new ElevenLabs
  recording as first written.
- **`tm_sweep_team`(21) is NOT a generic team-sweep line** — its real tag is `PurifiedTeamAppearance`, an
  Arena-specific "Purified" team spawn event, unrelated to a team sweep/wipe despite the category's own
  name. Moves **out of Team red/blue** into a new **Arena-specific (excluded)** row — it was never
  Blast-Gang-gated, so it does not belong in that row either.
- **`bg_match_end_draw`(36) really is Blast Gang** (tag `BlastGangMatchendDraw`, Blast-Gang-only) —
  unchanged from the table above.

Corrected grouping (replaces the table above; category/clip counts still reconcile to 69 / 1640):

| Group | Categories | Clips | Change from the table above |
|---|---|---|---|
| General / round-flow / time | 13 | 300 | +`eng_final_battle`(32) |
| Team red/blue (TDM-style) | 14 | 332 | +`eng_kill_all_win_red`(33), +`bg_kill_all_win_blue`(27); −`tm_sweep_team`(21) |
| Draw (generic, cross-mode) | 2 | 26 | unchanged |
| Overtime (generic) | 1 | 26 | new row: +`bg_overtime_start`(26) |
| FFA colors / ShootOut | 9 | 177 | unchanged |
| Capture points | 15 | 346 | unchanged |
| Blast Gang (bomb mode, excluded) | 14 | 412 (32.26 min) | −`eng_final_battle`, −`eng_kill_all_win_red`, −`bg_kill_all_win_blue`, −`bg_overtime_start` |
| Arena-specific (excluded, not Blast Gang) | 1 | 21 (1.82 min) | new row: `tm_sweep_team` (`PurifiedTeamAppearance` tag) |

The corrected Blast Gang total (412 clips / 32.26 min) is smaller than the 530 clips / ≈42.0 min above
because four categories (91 clips) move out of it, not because any clip count was mismeasured — both
figures are independently re-derived this session by summing `m_Length` across the live bank's own
`AudioClip` objects for exactly the categories in each corrected row (never the bank-wide 4.35 s mean
scaled by clip count, the same pitfall the original ≈42.0 min figure below was careful to avoid). Full
tag → categories → included/excluded → suggested-cue detail lives in
[`docs/progress/arena-audio.md`](progress/arena-audio.md)'s "Tag evidence" table, generated from and
cross-checked against the same `arena_audio_catalog.json` `extract_arena_audio.py` now validates on every
run (`validate_tag_bank()`) — that table is the current source of truth; treat the grouping above (before
this correction) as historical.

The Blast Gang group's ≈42.0 min figure (as originally computed, 530-clip grouping, superseded above) is
a per-clip sum, not the overall 4.35 s mean scaled by 530
clips (that arithmetic gives ≈38.4 min instead, since Blast Gang's own per-category means run 3.9–5.8 s,
higher than the bank-wide average). It is independently computed this session by summing `m_Length`
across the 530 `AudioClip` objects in these 18 categories (same UnityPy pass as above); no duration
figure for this specific group appears in the synthesis or journal.

## Tier B — worthwhile, gated on an unbuilt prerequisite or a design decision

| Item | What (evidence) | ArenaMode use | Owner | Effort | Prerequisite |
|---|---|---|---|---|---|
| Adaptive post-match result music (start/loop/win/lose stems) | `result_music_start`(4.94 s)/`_loop`(9.82 s)/`_end_winner`(9.82 s)/`_end_looser`(9.82 s)/`matchend_music`(24.95 s), path_ids 5410/4880/5204/5262/4780; genuinely absent from EFT/ArenaMode. | Underlay for the result moment — but `TDM-MATCH-FLOW-REVIEW.md` confirms that moment is a 15 s HUD toast/scoreboard over the still-running raid, not a dedicated quiet scene; the ~14.8 s start+loop runway barely fits. | ArenaMode | M | A design decision on ducking under live raid audio |
| Control-Point capture-stinger pair + round-start signal | `arena_point_captured_ally`(1.49 s)/`_enemy`(2.25 s) in a 5-clip, 1,134,864-byte bundle; `asrena_start_signal_dry` in `arena_effects.bundle`. | Round-start half ships now via the existing `HitmarkerAudioService` pattern; capture half needs `CONTROL-POINT-REVIEW.md`'s slice 0 (currently zero audio). | ArenaMode | S | Capture half: Control Point slice 0. Round-start half: none |
| Spectator camera-glitch SFX | 5 one-shots (0.35–0.48 s) + a 20 s loop, path_id 5094. | One-shot on `ArenaSpectatorService`'s target-switch event (currently silent). | ArenaMode | S | Design taste call (purpose inferred from naming/duration only) |
| Mode-select logo lockups (TeamFight/CheckPoint) | TeamFight 145×65, path_id 5662; CheckPoint 241×87, path_id 5785. `MODE-SIDEBAR-REVIEW.md` confirmed zero icon/sprite references today. | Image on the (not-yet-built) mode-select rows. | ArenaMode | S | CheckPoint half needs Control Point; TeamFight half usable now |
| Kit-selection weapon-class badges | 26 individual 300×280 uncompressed `Popup_Item_Icon_TacMap_<suffix>` medallions; `KitCatalog.cs` is pure JSON with no icon code. | Category/tier field in `kits.json` + matching image. | ArenaMode | S | Confirm `KitBuilder.cs`/`KitSummary.cs`'s actual render path first |
| Control-point progress bar + world marker art | `ObjectiveProgress`(1×19)/`ObjectiveProgressBack`(10×8)/`Point_Icon`(66×40 backing). | `ArenaFriendlyMarkers.cs`'s existing `WorldToViewportPoint` projection already does the world→screen work needed. | ArenaMode | S | Control Point slice 0 ("nothing here is implemented" per that review) |
| Control-point anchor position (map-side data) | The `CAPTURE_POINT` object itself sits at local (0,0,0) on both maps; the actual anchor is its light child (walk the parent chain per `AGENTS.md`'s object-addressing convention, do not collapse parent+child into one id). Equator: `CAPTURE_POINT` id 27 → child id 424 `EQUATOR_CAPTURE_POINT_LIGHT` at local (1.28, 0.25, 4.22). Bay5: `CAPTURE_POINT` id 126 → child id 38 `CAPTURE_POINT_LIGHT_02` at local (91.26, −4.37, 29.44). Both already inside scenes both release maps already build (`TeamFight(_DesignStuff)`). The matching floor decal is already live via the accepted v5 static-decal importer, so this is data-only. | New `ControlPoint` field in `ArenaMapDescriptor.cs` + `tools/maps/{equator,bay5}.json`, mirroring `Exits`/`OpenedBarriers`, for ArenaMode to read. | both | S | Control Point built; team starts certified on Equator/Bay5 (currently **rejected** per ArenaMode's own `EQUATOR-MAP-REVIEW.md`/`BAY5-MAP-REVIEW.md`) |
| Per-team spawn/play-area bounding boxes (`SPAWN_BOUND_FIGHTER`) | Real BoxColliders on both maps' TeamFight scenes, `is_trigger=false` throughout; each map's two boxes have distinct z-center/size, not one shared number. Equator: id 71 center (−0.334,−0.168,0.416) size (19.92,4.28,4.63) at world z≈45.91; id 98 center (−0.334,−0.168,0.001) size (19.92,4.28,3.80) at world z≈−39.95. Bay5: id 89 center (1.40,−0.168,0.081) size (20.35,4.28,4.53); id 313 center (1.40,−0.168,2.02) size (20.35,4.28,8.41). | New `BoundedArea[]` field mirroring `Exits`/`OpenedBarriers`; ArenaMode's Areas system carves a NavMeshObstacle enclosure from it — needs an `OverlapBox`-style check, not a literal `OnTriggerEnter` port. | both | S | Team starts not yet certified on either release map |
| REFILL_CASE resupply-crate prop | Open-suitcase mesh + lit ready/cooldown lamp triplet; confirmed only on AirPit2 (non-release map, LOD/2 MeshColliders + 9 sibling light objects). | Modeled replacement for `ArenaPickupView.cs`'s current procedural-primitives-only pickup rendering. | ArenaMode | M | None strictly, but needs a new standalone prop mesh/material/texture extractor — none exists yet |
| V40/M18 grenade-specific VFX | 8 Arena-only effect prefabs. Runtime hook fully traced: `EFT.Grenade.Explosion()` → `Singleton<Effects>.EmitGrenade()` → a private `EmissionEffect[]`-built table (decompile: `Grenade.cs:340-342`, `Effects.cs:402,444,458,593-598`). 6/7 root GameObjects carry only shared EFT-native MonoBehaviours (portable); 1 carries Arena-only `ParticleRotationHolder` (no EFT counterpart). | Harmony patch inserting V40/M18 entries into the private effect table. | ArenaMode | S | Confirm V40/M18 actually appear in an ArenaMode kit's grenade slot — **unconfirmed** in this survey |
| 3-point Domination (Control Point's own deferred follow-up) | Capture-category families above (12 neutral/red/blue `point_<a|b|c>_captured` × 23 clips = 276; `all_points_<red|blue>_team_captured` × 23; `capture_point_live` × 24) answer `CONTROL-POINT-REVIEW.md`'s own open "what comes after slice 0" question. Design reference only, no assets pulled. | Generalize the single-point policy to a fixed set of points with per-point ownership + an all-captured terminal condition. | ArenaMode | L | Control Point slice 0 **and** slice 1, neither built |
| Second announcer voice pack (Geneburn/Tagilla) | Full alternate personas beyond the Tier A English bank: Geneburn (246.7 MB EN + 3.9 MB preview) and unlockable Tagilla (424.1 MB EN / 490.5 MB RU, 5 `voice_unlock` clips each), plus continuous `low`/`high_death_intensity` reactions (25 clips each, already counted in the Tier A general/time group). | Purely additive voice-pack indirection over `AnnouncerCueCatalog`'s sounds directory + a Settings.cs key. | ArenaMode | S | None — Arena's own VO stays reference only; a second full pack still needs the user's own recording session for anything ElevenLabs-specific |

## Tier C — design reference, redundant, contested, or speculative

| Item | Why not near-term | Owner | Effort |
|---|---|---|---|
| Native menu-click / popup-alert SFX kits (11 `arena_*_click`/`menu` clips + 4 `popup_*`) | `CHARACTER-SELECTION-REVIEW.md`: hover/click on ArenaMode's own screens "remain owned by EFT's original toggle"; no HUD alert/toast infrastructure exists anywhere in the client (`Pickups/`, `Hud/` grepped, zero hits). Revisit only if ArenaMode ever owns real menu chrome. | ArenaMode | S |
| Lobby / in-match background music tracks (`arena_wait_match_background_music` 112.00 s/32000 Hz, `arena_track_02` 153.29 s, `arena_track_03` 124.16 s) | No separate lobby screen exists to gate lobby music to; in-match background music would be unprecedented in any Tarkov raid, vanilla or Arena. Highest-risk item in the whole survey. | ArenaMode | L |
| Killfeed weapon-class / kill-method icon sets | ~~Contradicts `KillfeedView.cs`'s own documented, **considered** design ("player-relative team colors instead of faction icons", ported from MIT `DragonDen.Killfeed`) — a design question to raise with the user, not a gap.~~ — **corrected 2026-09-29, see below:** weapon-class/kill-method icons are not faction icons; they complement the existing team-colored line rather than contradicting it (extraction now complete — 40 items, `docs/progress/arena-ui.md`). | ArenaMode | M |
| Animated MVP post-match medal (36-frame flipbook, 6480×6480 source) | "MVP" occurs 0 times across ArenaMode's ~140 docs; `ANNOUNCER-AWARDS-REVIEW.md`'s "awards" are in-raid kill-streak barks, not a postmatch best-player concept. Needs new scoring logic before this art has anywhere to attach. | ArenaMode | M |
| Scoreboard row-background art + 7-color team palette | Largely redundant: `Effects/ArenaTracerColor.cs` already ships an 8-color palette wired into friendly-marker and killfeed coloring. Only the decorative row art (not the colors) is non-redundant, and there is no results screen to skin it onto. | ArenaMode | S |
| Arena's 11-mode catalog + per-map matrix (design reference) | Real, non-obvious roster (TeamFight/LastHero/ShootOut Solo-Duo-Trio/Coop/OverRun/BlastGang/CheckPoint) confirmed via `ScenesPreset.GameModes` typetree on both release maps; zero numeric rules recoverable (server-side). Worth reading once against ArenaMode's own roadmap, nothing to build from it directly. | ArenaMode | S |
| Blast Gang bomb plant/defuse mode (complete design reference) | Fully voiced/SFX'd asymmetric design (carried backpack, code-entry minigame, attack/defend halftime swap, post-detonation "toxin" area denial); shipped on 7/14 sampled maps, **not** on Equator/Bay5. Strictly harder AI work than the still-unproven Control Point prerequisite — `CONTROL-POINT-REVIEW.md` already rejected CTF partly over the same carry-bound-item constraint a bomb mode hits even harder. | ArenaMode | XL |
| Bay5 2v2 competitive-format precedent | Bay5's ShootOutDuo mode and its "Tournament 2x2" preset are the **same asset** (identical path_id `-7827987340368857968` via cross-bundle PPtr) — corroborates, not replaces, ArenaMode's own already-built, GPU-tested 2v2 bounded-area mechanism. | both | S |
| Kit/preset class-grouping schema | `PresetTemplates` TextAsset (path_id 4212): 12 presets, 6 `presetTypes` (one literally "Rating-gated", unused), `metaScore` 0–572. ArenaMode's `KitPricer.cs` explicitly rejected an analogous derived-power-score idea as design philosophy, not oversight — only a UI-only class/tier label would respect that decision. | ArenaMode | M |
| Per-mode ranked rating field | `ArenaProfile` TextAsset (path_id 4311): `Info.Rank.points` seeded 750 for TeamFight/BlastGang. `FUTURE-WORK.md` explicitly excludes public ranked play from ArenaMode's scope. Reference only. | ArenaMode | XL |
| Coop/OverRun PvE-flavored modes | Mode names + a dedicated "Arena Fighter" bot-difficulty family exist, but OverRun's `ScenePreset` path_id is identical to the map's other PvP mode on both sampled maps — undercuts "distinct PvE mode". No ArenaMode doc identifies this gap. Lowest confidence in the survey. | ArenaMode | L |
| Arena-exclusive cosmetics (outfits, hand-skins, gladiator armor) | ~30 sets contradict `CHARACTER-PREVIEW-REVIEW.md`'s documented native-items-only policy. Skinned meshes are Unity 6000.3.6f1-authored (`top_arena_gladiator.bundle`, `head_civilian_1.bundle`), a full major-version gap from this project's 2022.3.43f1; ArenaMapPort's compat tooling has only ever been proven on static scene/prop content, never a skinned/rigged humanoid mesh. Largest, least-proven technical risk in the whole survey. | both | XL |

## Rejected / dead ends (do not re-investigate)

- **MVP reveal stinger** (`arena_mvp_postmatch`/`_widget_02`) — no MVP concept anywhere in ArenaMode;
  `ANNOUNCER-AWARDS-REVIEW.md`'s "awards" are in-raid kill-streak barks.
- **Spawn-protection shield visual** — the container name (`IceBergSpawnProtectionZones`) is inherited
  dev scaffolding from Iceberg/Sanatorium's CheckPoint prefab, only 2 instances, non-release AirPit2
  only; `ArenaProtection.cs` already implements the invulnerability mechanic with no visual.
- **AutoCamera waypoint flythrough files** — all 6 sampled map folders are byte-identical placeholder
  content, not tuned per-map camera art. (A later systems-area re-check found the plain-text
  `.cfg` format itself real and short — two waypoints per map, `AutoCamera/{Equator,Bay5}/1.cfg` — but
  it is an idle/attract-mode pan, not a killcam or spectator feature ArenaMode has any use for; its
  natural home, if ever wanted, is ArenaMapPort's own menu-art extraction, not ArenaMode.)
- **Native per-zone PostProcessProfile mechanism** — an unmodified native EFT mechanism Arena merely
  reuses; belongs to ArenaMapPort's map-atmosphere work (already covered), not ArenaMode.
- **Arena point-marker/rank-icon sprite containers** — misfiled under VFX; no ArenaMode map-marker or
  rank-icon HUD element exists to attach it to, and EFT already ships its own rank-icon series.
- **13 extra locomotion/civilian-panic AnimationClips** — solidly evidenced but zero concrete use case
  anywhere in ArenaMode's bot-movement docs.
- **Unused "civilian" NPC archetype** (Bay5-harbor-themed) — complete but entirely unused; zero
  civilian/hostage/bystander/crowd matches anywhere in `FUTURE-WORK.md`.
- **GAP: BSG's own Arena-mode presets on full-size Tarkov maps** — every `ScenesPreset` for
  Customs/Factory/Labs/Labyrinth/Lighthouse/Reserve/Shoreline/Woods/Interchange has `GameModes=[]`;
  BSG never authored a deathmatch/TDM/capture-point variant for any full-size map. ArenaMode's own
  `BOUNDED-AREA-REVIEW.md` already solves "bounded arena on a full Tarkov map" better, from the SPT
  location's own spawn-point database. Closed.
- **GAP: `PlayerSpirit` ghost/observer prefab family** — a live-player animator/character-controller
  offload optimization shared byte-for-byte with EFT 0.16.9.5's own client, force-disabled for
  local/offline games (the exact mode ArenaMode runs in). Nothing to do with death or spectating.
  `ArenaSpectatorService.cs` is a pure FPS-camera reposition trick with no body shown, so there is no
  visual gap for this to fill anyway. Closed.
- **GAP: unused/WIP "Zombies" PvE archetype** — a fairly complete unreleased Halloween-event archetype,
  but all 128 of its bundle paths already ship byte-identical in EFT 0.16.9.5 itself, backed by a fully
  wired native `BotHalloweenWithZombies` AI system on the SPT 4.1.5 server — nothing Arena-exclusive to
  extract. ArenaMode has zero PvE/zombie precedent or intent anywhere in its docs or code. If a PvE
  "Infected" mode is ever wanted, the actionable idea is SPT's own built-in Halloween system, not a
  port from Arena — and that belongs in ArenaMode's own backlog. Closed.

## Correction, 2026-09-29 (later same session) — killfeed icon item was not actually contested

Tier C rated the "Killfeed weapon-class / kill-method icon sets" item **contested** because
`KillfeedView.cs`'s own doc comment says it uses "player-relative team colors **instead of faction
icons**" — read at the time as this candidate proposing to replace that team-color line with icons, i.e.
reversing a considered design decision. On a closer look (prompted by the user asking to see what the
killfeed icons actually look like, 2026-09-29): **weapon-class and kill-method icons are not faction
icons.** `KillfeedView.cs`'s own design statement is specifically about not using per-faction (USEC/BEAR)
iconography in place of the team-color text — it says nothing about a weapon-class or kill-method glyph,
which identifies *what* killed the victim, not *which side* the killer is on. The two are orthogonal: a
weapon icon or a headshot-skull marker sitting next to the existing team-colored `"Killer -> Victim"` line
complements it (same role the trailing `WeaponName`/`HS` text already plays today) rather than replacing
the thing the design comment was actually defending. This item should not have been filed as "contradicts
a considered design decision" — moved out of Tier C's contested framing; see
[`docs/progress/arena-ui.md`](progress/arena-ui.md) for the actual extracted icon set (40 items: 11
weapon-class, 5 kill-method, 5 death-cause, 4 grenade/knife size variants, and the 15-cell `Killmetod_icon`
strip), its previews, and integration notes against the real `KillfeedView.cs`/`KillfeedText.cs`. Still a
design decision for the user to make (whether ArenaMode's killfeed should grow icons at all), just not one
this survey's own evidence actually argues against.

## Distribution and legal policy

Every item above that needs a real BSG asset (audio, texture, sprite, mesh) is pulled **locally and
read-only** from the user's own Arena 0.5.4.0 install, or — for the three round stingers and the native
armband colors — from the SPT41 client mirror itself, which needs no Arena dependency at all. Extraction
follows the same small, targeted, path_id/`m_Name`-guarded pattern this repo already uses
(`tools/extract/extract_banners.py`, `extract_loadscreens.py`): read with UnityPy, convert at build time
into plugin-local files (OGG/WAV, PNG, JSON), and ship inside the consuming mod's own dist-equivalent
output. **None of this is ever committed to git** — tracked files may name clips/categories, never carry
the audio/texture/mesh data itself, matching this project's existing policy for banners, deploy art,
native audio, decals and MultiFlare.

ArenaMapPort and ArenaMode are separate repos: ArenaMode does **not** automatically inherit
ArenaMapPort's extractors. Each ArenaMode-side item needs its own small extractor, cloned from the same
proven pattern, built inside ArenaMode's own tooling. ArenaMode currently tracks its own `.ogg` files in
git (its own convention, unlike this repo, which gitignores all BSG-derived output) — so any Arena clip
still needs a local-extraction step producing a gitignored intermediate before a human decides what, if
anything, enters ArenaMode's tracked sounds folder; the raw Arena bundle or an extracted WAV must never
be committed there either.

**Ownership split**, per the lead's correction: extraction tooling and any map-side data (the
control-point anchor position, the spawn-bound boxes) belong in **ArenaMapPort**
(`ArenaMapDescriptor.cs` + `tools/maps/<key>.json`, mirroring the existing `Exits`/`OpenedBarriers`
fields) — the two Tier B items marked "owner: both" above. Gameplay and HUD consumption of any of this
data or audio belongs in **ArenaMode**, which this repo only reads
(`D:/SPT41/Development/ArenaMode`, read-only, never edited from here). The audio-extraction side of this
survey is being implemented now, in this repo, as `tools/extract/extract_arena_audio.py` — see
[`docs/progress/arena-audio.md`](progress/arena-audio.md) (written in parallel this session) for its
own evidence and host-test status.

Legal risk is not uniform: `arena_win`/`_lose`/`_start` and the native armband colors carry the lowest
risk of everything surveyed, since they already ship inside the SPT 4.1.5/EFT 0.16.9.5 client this mod
targets — zero Arena dependency, zero extra versioning risk. Everything else pulled from Arena is
ordinary "extracted locally from the user's own paid install, converted, never redistributed in source
form" risk, the same posture already accepted for every native-feature port this project has shipped.

## Recommended order

1. Ship the Tier A items as one small ArenaMode polish pass: the English commentator voice pack as a
   new, additive, selectable announcer option (kill-streaks stay on ElevenLabs); `arena_win`/`_lose`/
   `_start` stingers (sourced from the SPT41 client itself); the Spectator crowd-ambience loop; the
   `ArenaPanel.cs` K/D/A icon triplet; and extending `TeamDeathmatchArmbandPolicy` to FFA color
   identity.
2. Build TDM overtime carefully, reusing (not routing around) `TDM-MATCH-FLOW-REVIEW.md`'s existing
   terminal-result-lifecycle rigor — its "overtime"-named asset (`bg_overtime_start`) is now confirmed
   generic by tag evidence (2026-09-29 correction above), so the spoken line can reuse that extracted
   Arena clip instead of needing a fresh ElevenLabs recording, pending a design decision on whether Arena's
   own delivery fits ArenaMode's announcer voice.
3. Before spending effort on the V40/M18 grenade VFX item, confirm those two grenades actually appear
   in a real ArenaMode kit's grenade slot — unconfirmed in this survey.
4. Treat the Control-Point-adjacent Tier B batch (capture SFX, progress/marker art, CheckPoint logo,
   anchor position, spawn-bound boxes) as one batch once `CONTROL-POINT-REVIEW.md`'s slice 0 is built
   and team starts are certified on Equator/Bay5 — don't extract piecemeal before that.
5. Build a standalone prop mesh/material/texture extractor (following `tools/extract/common.py`'s
   pattern) before pursuing the REFILL_CASE pickup-crate prop; none exists yet in either project.
6. Do not pursue Arena-exclusive skinned cosmetics until a first skinned-mesh compat proof-of-concept
   crosses the Unity 6000.3.6f1 → 2022.3.43f1 gap, and until the user explicitly wants to reverse
   `CHARACTER-PREVIEW-REVIEW.md`'s native-only-items policy.
7. Read, but do not build from, the pure design-reference Tier C items — the 11-mode catalog, the
   kit-preset class schema, the Bay5 2v2 precedent, the per-mode rating field, and the full Blast Gang
   design — to inform ArenaMode's own roadmap without importing their assets.
8. Audio extraction starts now, in this repo, as `tools/extract/extract_arena_audio.py`
   ([`docs/progress/arena-audio.md`](progress/arena-audio.md)); wiring any of it into ArenaMode's
   `AnnouncerCueCatalog` is a separate task in that repo.
