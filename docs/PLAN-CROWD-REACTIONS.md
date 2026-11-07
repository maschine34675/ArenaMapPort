# PLAN-CROWD-REACTIONS: action-bound spectator calls

Status: design, nothing implemented, nothing listened to. 2026-11-02, branch `feature/iceberg`. Revised the same day with the user's automatic German subtitles of every crowd clip (section 2.4): a check of the clips by text, not by ear.

**Status update, 2026-11-02 (evening): phase A is implemented in ArenaMapPort and reviewed offline** (70 `keep` clips of the six banks, contract of eight members, planner, glue, demo, swell; decisions D1, D13 and D14 are marked in section 8). **The host run and the user's listening are pending; the ArenaMode part (phase B) and the plugin's own sensors (phase C) are not started.** The sentence above is the plan's state when it was written; the implementation record and the remaining host checklist are in [progress/spectator-audio.md](progress/spectator-audio.md), "2026-11-02 - Action-bound reactions, phase A (offline)". Where the code differs from this plan, the record says so (the clip-choice relaxation order of 4.4 is the lead's decision of that day). **Host run 2026-11-02 21:09-21:20 on GPU-2:** all five kinds on the four crowd maps, 0 warnings, Iceberg `ReactionKinds` 0, hideout OK (record: "Host run on GPU-2" in the same file); the listening is pending. One host correction to 4.4: a plain raid stays `Started` after the local death until it is ended, so reactions go on there while dead too (D12 allows it; the random calls stay silent).

**Status update, 2026-11-07: D3 is decided and implemented offline** (roar layer, swell removed, `Spectators/ReactionSwell` gone; reviewed, 3,184 .NET tests, dist 359 files). **Host-run on GPU-2 on 2026-11-07 (Equator and Bowl, the hideout, 0 warnings), not listened to.** The sections above stay as written; where they speak of a bed swell (sections 4 and 5, the phase-A status line), the record in [progress/spectator-audio.md](progress/spectator-audio.md) "2026-11-07 - Reaction roar layer" wins. Phase B (ArenaMode) and C are unchanged and not started.

Origin: the user asked to look again at action-bound spectator calls, the crowd reacting to what happens in the match as in Arena,
and where ArenaMode and ArenaMapPort need changes. The plan comes from a read-only analysis (workflow `wf_3515f8ba-a57`):
- **Four readers:** Arena's data, ArenaMapPort's crowd audio, ArenaMode's events, EFT's events.
- **One designer.**
- **Two adversarial checkers:** 82 claims on the ArenaMapPort/Arena side and 68 on the ArenaMode/EFT side were checked against code and data. Their 31 findings are folded in below.

Notation:
- "Fact" is read from files or data; "inference" is a deduction; "proposal" is a tunable starting value.
- ArenaMode paths are relative to `D:\SPT41\Development\ArenaMode` at HEAD `65007e74` (2026-11-01).
- `C/` stands for `ArenaMode.Client/`. EFT paths are under `D:/SPT41/Development/_decompiled/EFT/`.
- ArenaMapPort's agents never edit ArenaMode: section 6 is for the user or Codex to adopt.

## 1. Goal and scope

**In scope:**
- **Maps:** Equator, Bay5, Air Pit and Bowl, the four maps with a crowd. Iceberg has no spectators and no fan speakers, so it stays silent (`ReactionKinds == 0`, no warning).
- **ArenaMode raid:** this is the target. ArenaMode knows kills, scores, teams, rounds and results; ArenaMapPort does not.
- **Plain SPT raid:** only the test vehicle. A few events the plugin sees itself can drive a fallback. A plain local death ends the raid within about 5 s (`BaseLocalGame.cs:847`), so reactions to it are barely audible.
- **Unchanged:** the random "Participating" calls (user decisions: embedded fan clips only, 2 voices, gain 0.5, 4-8 s pauses) and the murmur bed (since 2026-11-02 Arena's neutral in-match bed, at the level approved for the postmatch bed). Reactions are an addition.

**Out of scope:**
- Animation reactions (D9).
- Arena's stereo in-match beds (D3, but see 2.1: they exist).
- Fika.
- The standing rules of AGENTS.md (no loot, no scav).

## 2. What Arena does

Arena's reaction logic is IL2CPP code in `GameAssembly.dll` and `global-metadata.dat` is encrypted. No event ids, cooldowns, probabilities or thresholds are serialized anywhere readable, and none were obtained. The readers stopped there and did not try to defeat the protection.

### 2.1 Three layers (fact)

1. **Individual fan voices.** `fan_speaker_voice.bundle` holds 10 banks, 136 distinct clips (all mono 16-bit) and 254 slots.
   - **Emitters:** `ArenaFanSpeakerEntity` points: Equator 20, Bay5 14, Air Pit 24, Bowl 13, Iceberg 0. Each carries only an id.
   - **Falloff:** 75 m on every slot. The port's calls use 90 m (Bowl 150 m).
2. **In-match crowd beds.** `ambient_voice.bundle` holds 7 stereo 44.1 kHz beds in 3 banks:
   - Cheer 1119: 3 clips of 7.0 s, about -22.5 dBFS RMS.
   - Angry 1121: 3 clips of 8.0 s, -26 to -30 dBFS.
   - Neutral 1122: 1 clip of 80.0 s, -31.1 dBFS.

   Every sound scene carries `FansAmbient` emitters with the trigger id as an int field, in equal counts of 1119, 1121 and 1122:

   | Map | Emitters | Sound scene |
   |---|---:|---|
   | Equator | 6 | level56 |
   | Bay5 | 6 | level188 |
   | Air Pit | 12 | level39 |
   | Bowl | 6 | level111 |
   | Iceberg | 12 | level259 |

   A `FansAmbientEntityGroup` lists two Cheer+Angry pairs and a Neutral list. The emitters are inactive in the imported scenes (`equator-native-features.md:61`). Whether Arena activates them in a match is not determined.

   The postmatch clip the port uses as its murmur measures -39.5 dBFS RMS, 8 to 17 dB below these beds. **User decision 2026-11-02, implemented in `deb7b85`:** the 80 s Neutral bed replaces the postmatch clip as the murmur loop, trimmed by -8.9 dB to the approved level (79.5 s after the seam; record in `spectator-audio.md` "Neutral bed").
3. **Animation.** The shared `ArenaFan` Animator Controller (`sharedassets40.assets`, path 338) has 98 parameters and two layers (`Base Layer`, `HideLayer`).
   - **95 triggers, one per animation:** `ArenaFan_Pleased_000..` (including `_004/005/007_BigWin`), `ArenaFan_Anger_000-005`, `ArenaFan_Owned_000/001`, `ArenaFan_Shock_000-008`, `ArenaFan_Hide_000-011`, a `Parapet_*` family and `QuickUnhide`.
   - **3 states:** `Parapet` (bool), `Hide` (bool) and `HideInModifier` (float). Hide is a sustained state with its own layer, not a one-shot trigger.
   - **What the port ships:** only `Idle_000_loop` and `Idle_Cheer_001` (`extract_spectator.py:29`).

**Correction of the port's own texts.** The statement that Arena's maps carry no crowd ambience, or that the postmatch clip is the only one in its files, is wrong today, independent of this work. What the data supports: the postmatch clip is the only crowd clip that is not bound to a match-event trigger; since 2026-11-02 the port's murmur is Arena's own Neutral in-match bed (trigger 1122) instead. Arena's in-match beds sit in `ambient_voice.bundle` on emitters that are inactive in the imported scenes. The sentence appears in:
- **Released texts, to be fixed with the next release:** `README.md:29-30`, `FORGE.md:27-28`, `CHANGELOG.md:26`, `docs/releases/v0.4.0.md:20-21`.
- **Records:** `docs/progress/spectator-audio.md:568-569`, `docs/progress/arena-audio.md:583`.
- **Code:** the `extract_spectator_audio.py` docstring (lines 3-4), `SpectatorCrowd.cs:6`, `SpectatorCrowdBed.cs:14`, `SpectatorCrowdBedModel.cs:12`.
- **HANDOFF:** `HANDOFF.md:156`.

### 2.2 The banks of `fan_speaker_voice.bundle` (fact, confirmed by both checkers; the Event column is inference, checked against the subtitles in 2.4)

| Bank (trigger) | Embedded clips (s) | External Scav slots | Event, as far as known |
|---|---|---|---|
| LowDeathIntensity (1116) | 15 (39.2) | 0 | Same id as the announcer's bank of that name (fact); "few kills lately" (inference; content-confirmed, 2.4) |
| Cheer (1119) | 14 (39.3) | 15 | A kill the crowd likes (inference; content-confirmed, 2.4) |
| Owned (1120) | 16 (46.2); 13 share names with Cheer, 17 distinct in the union | 3 | No meaning of its own: the Cheer clips plus three twins (2.4) |
| Angry (1121) | 18 (43.5) | 23, plus 2 null | A kill or loss against the crowd's fighter (inference; content-confirmed, 2.4) |
| Neutral (1122) | 2 (3.6) | 17, plus 8 null | Baseline chatter by its 17 external references (idle mutter, combat calls); its 2 embedded clips are a Cheer duplicate and a 0.86 s wordless scream (2.4); effectively an external bank |
| Participating (1123) | 13 (31.9) | 9 | Shipped today as the random calls; passivity jibes and urging, none reacts to a result (2.4) |
| Hide (1124) | 36 (79.2): `aim_hide` 11, `shoot_hide` 25 | 14 | A shot or aim toward a stand, not an explosion (inference from names and the Hide layer; narrowed by the subtitles, 2.4) |
| ChangedRound (1125) | 9 (28.6) | 5 | Match or round start (inference; content-confirmed, 2.4) |
| EndRound (1126) | 11 (29.1) | 8 | Round end; the id equals Arena's EndRound signal in `arena_effects.bundle` (fact); neutral commentary, no winner named (2.4) |
| PurifiedTeam (1127) | 16 (49.1) | 0 | Unresolved: the announcer tag of that id is "an Arena-specific Purified team spawn event, unrelated to a team sweep" (`arena-audio.md:105-108`); only the fan clip names say `sweep_team`; the subtitles say anticipation of a fight and talk of a sweeping squad (2.4) |

- **Shared parameters (all banks):** Importance 3, Blocker 0, IgnoreTags 0, no chain (`ChainEvent` 8 = `PhraseNone`, `EPhraseTrigger.cs:5`), slot Mask 0.
- **Clip lengths:** 0.70 to 6.71 s. Six non-Participating clips exceed 5 s; the longest is `kill_cheer` at 6.71 s.
- **Totals:** 150 embedded slot references resolve to 136 distinct clips. Without Participating that is 123 clips, 318.5 s, 28.09 MB as 44.1 kHz mono WAV.
- **External references:** they resolve to `voice/scav/scav1.bundle` and `scav2.bundle`. That is 94 slots over all banks and 78 distinct, of which 68 slots and 60 distinct are in the six phase-A banks.
- **No StartRound bank.** The fan bundle has no `StartRound` bank (`spectator-audio.md:13` is wrong); round start is `ChangedRound` (1125).

**One event-id space (fact; the bank-to-event meaning is inference).** The trigger ids of Arena's phrase bundles form one contiguous space.

| Bundle | Ids |
|---|---|
| `speaker_voice.bundle` (announcer) | 1107 FirstBlood, 1108 StartGame, 1109 StartRound, 1111 KillingSpree, 1112 PlayerDead, 1114 CountDown, 1115 HighDeathIntensity, 1116 LowDeathIntensity, 1118 TimeExpire, 1127 PurifiedTeamAppearance (0 clips), 1128 CapturePointAppearance, 1129 PointCapturing, 1130 NextRound, 1131 MatchWon, 1132 UnfairGame |
| `arena_effects.bundle` | 1109 StartRound, 1126 EndRound |

- The fan banks 1116, 1126 and 1127 reuse ids of this space.
- 1119-1125 sit between TimeExpire and the round events. That supports reading them as match-event reactions.
- Ids 1110, 1113 and 1117 are absent.

**Never determined:**
- What fires 1119-1125 in Arena, and how Arena picks a side.
- The three constant 5s in `ArenaFanEntity`, and `SpectacularityContainer`.
- Whether Arena silences the crowd for a dead viewer. The port's death guard is its own simplification.

### 2.3 Audio facts for an export

- **No overlap with key sources (fact).** All 136 fan clips and the 7 beds stream from `archive:/CAB-*.resource` inside their own bundles. Exactly 19 AudioClips of the game stream from stage-2 key-source files, and none of them is crowd audio. They are:
  - 16 `branches_NN` in `sharedassets41.resource`;
  - `arena_airpit_metal_gates` in `sharedassets28.resource`;
  - one clip each in `sharedassets48.resource` and `sharedassets84.resource`.
- **The normalisation works on all of them.** All 136 fan clips and all 7 beds decode with `_decode_and_validate_source`.
  - `normalize()` gives -10.09 to +2.72 dB, peaks 0.279-0.691 (validator ceiling 0.709) and RMS 0.0794.
  - 45 of the 123 need the ffmpeg resample (49 of all 136); their rates are 36.4-40.9 kHz.
- **The 5 s length cap blocks today.** It sits in the extractor (`extract_spectator_audio.py:161` file size, `:167` frames) and in the client (`SpectatorAudioModel.cs:36`).

### 2.4 What the clips say (user's subtitles, 2026-11-02)

**Source and limits.**
- The user exported every crowd clip (`work/extract/crowd-sounds-listening`: 252 files, 244 fan voices plus the 7 stereo beds and the postmatch clip; `index.csv` lists bank, source, length and level) and had German subtitles made (`Deutsche_Untertitel/Uebersicht.csv`).
- **The subtitles are automatic** (speech recognition with a machine translation), **not a manual check**: 48 of the 252 rows are flagged uncertain by the tool and 9 carry no recognised speech. Three readers classified the clips from subtitle text, clip name, length and level; the results were merged here. Nobody listened to a clip. What follows narrows the guesses of 2.2 and does not replace the listening check of section 7.
- This plan quotes no voice line. Clips are characterised, never quoted.
- **Delivery is lost in the port.** Every clip is normalised to -22 dBFS RMS, so a mumbled lament and a shout play equally loud. "Strong" below means the wording of a clip (a shout, a superlative, an urge), and loudness comes from the voice count.
- **Status of a clip.** *keep*: text clear and fits the bank's event. *listen*: exported by default and named in the listening check, because its subtitle is a single flagged or unintelligible one (judged by name and delivery; FLAG), it is a taste question (TASTE), it reads as generic outside its moment (CTX), it is an urge and not a reaction to a result (ROLE) or it takes a side (SIDED). *out*: left out by name, with a reason code: SLUR (the original contains a slur), CTX (only fits a moment the port does not produce), UNCL (no recognisable tone), WRONG (speaks as or to the sweep crew, not as a spectator). A flagged clip counts as keep when two or more voices carry the same sentence in the German translation (the transcripts may differ): four take-cover clips and one eye-opening line in Hide. Two Angry insult clips whose transcripts differ between voices are listen, because a slur cannot be ruled out from the text.
- **Scav references** (`scav__*`, the external Scav voices Arena lists in the same banks) are counted as "fit by content": they would work as a spectator remark in the bank. All stay excluded by D2.

**Per bank** (lengths and counts from `index.csv`, all equal to the table in 2.2):
- **Cheer 1119 (Approve).** Kill reactions in three tones: praise of the killer or the play (9 clips), gloating at the victim in the second person (4), one clip that urges the player on. Only the praise tone also works for an event without a victim. Meaning confirmed. Usable 14 of 14 (keep 10, listen 4). Scav: about 9 to 11 of 15 fit (kill confirmations and praise; `scav1_enemy_down_04_l` is a taunt of the victim like the embedded gloat clips, `scav1_enemy_down_11_bl_l` a reaction shout); 4 are unintelligible or combat urging. Out: none.
- **Owned 1120 (merged into Approve).** 13 of its 16 clips are the Cheer clips; the other 3 are twins of Cheer lines in other voices (one ordinary praise, one superlative shout, one urging clip). The content gives no separate event, so the plan's optional extra pool has no basis. Meaning corrected: the same event as Cheer. Usable 16 of 16 (keep 12, listen 4); the union with Cheer is 17 clips (keep 12, listen 5), 48.9 s. Scav: 2 of 3 fit (kill confirmations), 1 unintelligible. Out: none.
- **Angry 1121 (Disapprove).** Laments about a lost bet, shock and resignation (10 clips) and heckling of the dead player in the second person (8). The bank is about the viewer's fighter dying or losing, as inferred; the heckles fit an own death only. Meaning confirmed. Usable 18 of 18 (keep 14, listen 4). Scav: 6 of 23 fit (short disappointed groans), 3 more possible; the rest are combat, lookout, friendly-fire or time-pressure calls and one triumph line from the wrong side. Out: none.
- **ChangedRound 1125 (Start).** Surprise that it has already begun, impatience, pre-match banter, a call to go. None names a round number. Meaning confirmed (Start). Usable 9 of 9 (keep 6, listen 3). Scav: 4 of 5 fit in meaning (session-start greetings). Out: none. `fan12_match_start_05` (5.49 s) is listen (FLAG): its flagged subtitle may be a countdown complaint.
- **EndRound 1126 (Finish).** Neutral commentary on an ending: boasts (2 clips), lukewarm or surprised remarks (2), mild praise of the fighters (3), dark humour about clearing the finished fighters away (1), relish of blood (1), a holiday remark (1), one unintelligible clip. No clip names a winner or a loser. Meaning confirmed, unsided. Usable 11 of 11 (keep 9, listen 2). Scav: 0 of 8 fit as Finish (seven praise or acknowledgement lines and one unintelligible shout; all 8 are also Cheer references). Out: none.
- **Hide 1124 (TakeCover).** Spectators panic at, plead with or scold a fighter who aims or fires toward them (toward the stands is an inference from the scolds; no clip names a stand): panic, shock and fear shouts (12 clips, 10 `shoot_hide` and 2 `aim_hide`, 6 distinct sentences) and pleas or scolds addressed to the shooter (22 usable clips). No recognised speech, embedded or Scav, mentions an explosion or a grenade (9 rows of the export have no recognised speech). Meaning corrected: a shot or aim toward a stand, not gunfire or an explosion in general. The 36 clips carry about 22 distinct sentences. Usable 34 of 36 (keep 29, listen 5). Scav: 4 fit in meaning (the take-cover calls `scav1_incover_01` and `_02`, the plea `scav1_enemy_bear_02_l` and the scold `scav1_ffire_02_l`); the other 10 are insults, combat and hurt lines, three of them with a slur. Out: `fan8_aim_hide_01`, `fan12_shoot_hide_05` (SLUR).
- **LowDeathIntensity 1116 (Lull).** Complaints that the match is dull (5 clips, one of them ambiguous), impatient jibes at idle fighters (7 readable), 2 clips without recognised speech (flagged, listen) and one wordless yell (out). Nothing refers to a side, a faction, a place or a weapon. Meaning confirmed by content, the best-supported bank. Usable 14 of 15 (keep 11, listen 3); no Scav references. Out: `fan12_low_death_intensity_04` (UNCL: 5.16 s of wordless yelling, tone unclear).
- **PurifiedTeam 1127.** Anticipation of a fight: hype (5 clips), one boredom remark (Lull material), bets, drink and food, taunts at a hunted team, talk of a sweeping squad's job. It is not a result, not a reaction to a kill and not a team wipe. Meaning narrowed: "a sweeping squad is sent in, the fight is about to get going"; which ArenaMode event corresponds is open. Usable 12 of 16 (keep 5, listen 7: sided or flagged); no Scav references. Out: `fan7_sweep_team_01`, `fan12_sweep_team_11` (WRONG: they address the sweep crew about the equipment, not as spectators; the same sentence), `fan10_sweep_team_02` (WRONG: speaks as a future member of the sweep crew), `fan13_sweep_team_02` (WRONG: speaks as one of the sweep crew).
- **Participating 1123 (the shipped random calls).** Taunts at passive, hiding or slow fighters and urging; none reacts to a kill, death, score or round. The same flavour as LowDeathIntensity. The 9 Scav references are spotting and combat callouts, which agrees with the user's exclusion.
- **Neutral 1122.** Its 2 embedded clips are a duplicate of a Cheer clip and a 0.86 s wordless scream (`fan12_match_start_06`, flagged; a start only by its name); its 17 Scav references are idle mutter and combat calls, consistent with baseline chatter. Effectively an external bank, not exported; the scream is an optional addition to the Start pool.
- **Beds.** The Cheer (3 x 7 s) and Angry (3 x 8 s) beds in `ambient_voice` are steady roars and boos with no intelligible speech; the 80 s Neutral bed is a speechless murmur with the same short-term fluctuation as the postmatch clip, about 9 dB louder.

**Embedded clips by status** (a clip in two banks counts once in the union):

| Bank | Embedded | keep | listen | out |
|---|---:|---:|---:|---:|
| Cheer | 14 | 10 | 4 | 0 |
| Owned | 16 | 12 | 4 | 0 |
| Cheer + Owned (union) | 17 | 12 | 5 | 0 |
| Angry | 18 | 14 | 4 | 0 |
| ChangedRound | 9 | 6 | 3 | 0 |
| EndRound | 11 | 9 | 2 | 0 |
| Hide | 36 | 29 | 5 | 2 |
| **Phase A (91)** | 91 | 70 | 19 | 2 |
| LowDeathIntensity | 15 | 11 | 3 | 1 |
| PurifiedTeam | 16 | 5 | 7 | 4 |
| **Eight banks (122)** | 122 | 86 | 29 | 7 |

**The 19 phase-A "listen" clips** (for the listening check of section 7):
- FLAG: `fan9_kill_cheer_01`, `fan10_kill_cheer_01`, `fan12_kill_cheer_10`, `fan12_kill_angry_12`, `fan13_kill_angry_02`, `fan12_kill_angry_08`, `fan13_kill_angry_06`, `fan12_match_start_05`, `fan10_match_end_02`, `fan12_match_end_02`.
- ROLE: `fan13_kill_cheer_03`, `fan15_kill_cheer_02`.
- TASTE: `fan11_match_start_01`, `fan8_aim_hide_02`, `fan12_shoot_hide_06`, `fan13_shoot_hide_07`, `fan13_shoot_hide_08`. The last four are one crude family joke and three mental-health jibes.
- CTX: `fan12_match_start_04`, `fan13_aim_hide_01`.

Lull's three listen clips (`fan11_low_death_intensity_03`, `fan12_low_death_intensity_03`, `fan13_low_death_intensity_03`) are FLAG.

**Phase-A pools and the strength values of section 6** (the tags are a proposal; the intensity reading comes from wording only):

| Kind | Bank(s) | Clips | Sub-pools | Rule |
|---|---|---:|---|---|
| Approve (1) | Cheer + Owned | 17 | normal 13 = 9 praise + 4 gloat (a gloat clip needs a victim, so it is kill-bound); strong-only 2 superlative shouts (4.55 s, 6.71 s); the 2 urging clips (listen, ROLE) join the strong-only set only if the listening check keeps them; 14 distinct sentences | below 0.8 (kills 0.35 and 0.6, lead 0.5): the 13 normal clips; from 0.8 (awards 0.9): the first voice is drawn from the strong-only clips, the others from the normal clips (with 2 strong-only clips the first voice repeats often). An event without a victim (a Domination capture, later) needs the 11 clips of praise and shouts only: a flag or kind 8 then |
| Disapprove (2) | Angry | 18 | lament 10; heckle 8 (own death only); 15 distinct sentences | below 0.4 (lead lost 0.3): the 10 laments; from 0.4 (own death 0.5): all 18. No strong-only pool: the mapping stops at 0.5. The subtitles carry no delivery, so no clip is classed by how loudly it is spoken |
| TakeCover (3) | Hide | 34 | panic 12 (6 distinct sentences); directed 22 (14 `shoot_hide`, 8 `aim_hide`) | any close shot: panic; directed only when the line of fire points at a stand; strength sets the voice count only |
| Start (4) | ChangedRound | 9 | one pool: the 8 clips read clearly (7 distinct sentences) plus `fan12_match_start_05` (listen), 4 voice families; optional +1 (`fan12_match_start_06`) and optional +5 neutral hype clips from PurifiedTeam (`fan8_sweep_team_01`, `_02`, `fan12_sweep_team_05`, `_10`, `fan15_sweep_team_01`) | 1.0 means 3 voices from 4 families: repeats after two or three starts; consider Start 0.6 (2 voices) or the optional clips |
| Finish (5) | EndRound | 11 | one pool, 10 distinct sentences; 3 praise the fighters (`fan12_match_end_02`, `_03`, `_04`), 1 is positive in tone, 2 are lukewarm or disappointed | 1.0; whether a loss variant drops any clip is a listening question |
| Lull (6, reserved) | LowDeathIntensity | 14 | one pool, 12 distinct sentences: 5 complaints, 7 readable jibes, 2 without recognised speech | trigger: D14 |
| PurifiedTeam (7, reserved) | PurifiedTeam | 12 | 5 neutral hype; 1 boredom remark (`fan12_sweep_team_04`, Lull material); 6 sided or flagged | no ArenaMode event is named |

"Distinct sentences" matters because the same line is recorded by several voices (Hide: 11 groups of two to four voices). A reaction with two or three voices should not say one sentence in chorus (4.4).

**What this changes in the plan:**
- Approve and Disapprove need no Owned pool. The merged pools have 17 and 18 clips.
- TakeCover is a reaction to gunfire toward a stand (inference from the scolds). The explosion sensor is dropped from v1 (4.2).
- Finish stays unsided (D7). Start is the match-live moment, and the countdown cues stay silent (6).
- Lull and PurifiedTeam: the clips are ready, the triggers are open (D1, D14).
- The random calls and Lull have the same tone, so a jibe right after a cheer would contradict it (4.4, `Hold`).

## 3. Today's state

### ArenaMapPort

- **Contract.** `ArenaMapPort.Client.SpectatorCrowd` has exactly four members: `ContractVersion` (1), `IsAvailable`, `Excitement`, `SetExcitement(float)` (`SpectatorCrowd.cs:23-44`).
  - The append-only rule is at `:14-15`.
  - The pin test `ThePublicSurfaceIsExactlyTheFourMembersOfContractVersion1` (`SpectatorCrowdContractTests.cs:27`) also forbids fields, constants (`:39`), events (`:40`), constructors and nested types (`:42`), generics and optional parameters (`:55-60`).
  - Nothing calls `SetExcitement`.
- **Bed.** `SetExcitement` is a level with a 2 s time constant and a factor of `1 + 0.6*level` (`SpectatorCrowdBedModel.cs:291-320`).
  - `SourceVolume = min(1, volume*factor/3.2)`, where 3.2 is the gain baked into the clip.
  - The bed checks only `Status`, not `IsAlive` (`SpectatorCrowdBed.cs:169-176`).
- **Random calls** (`SpectatorAudio.cs`, `SpectatorAudioModel.cs`):
  - Gain 0.5, `MaxConcurrent` 2, pauses 4.0-8.0 s, first call after 3-8 s.
  - **Speaker law:** `ChooseSpeaker(_distances, Rolloff * .8f, busy)` (`SpectatorAudio.cs:206`, `SpectatorAudioModel.cs:79-89`). Eligible are positions within 15 m of the nearest one, capped at 0.8 × Rolloff (72 m, Bowl 120 m). Busy speakers are excluded, and so is the last speaker when another candidate remains.
  - **Speaker positions:** every fan root plus 1.6 m, 99/88/99/99 (`ArenaSpectators.cs:220`).
- **The guard returns early.** `SpectatorAudio.Update` returns at `:160-164` (after `StopAllVoices` and `_schedule.Stop()`) in any of these cases:
  - the local player is null or dead;
  - the game is not `Started`;
  - `SpectatorCalls` is off.

  It returns again at `:167-168` while the spatial audio system is not ready. The voice-end loop (`:170-189`) and the schedule (`:193`) sit behind both returns.
- **Fragile family parse.** `_families` throws on a clip without `_participating` (`:118`).
- **`SpectatorAudio.Current` only exists when the 13 calls validate** (`:96`, `:130`).
- **The export chain is pinned to the 13 calls:**
  - extractor bank selection (`:205-210`);
  - `validate()` requiring trigger 1123 and 13 clips (`:148-177`);
  - client `Validate()` (`SpectatorAudioModel.cs:28-39`);
  - packer regex (`make_release_zips.py:360`);
  - staged-byte comparison (`validate_distribution.py:525-535`);
  - deliberate rejections in `test_spectator_audio.py:40-46` and `test_release_zips.py:375-383`.

  `validate_distribution.py` has key-source checks for gate sounds (`:322-325`) and monitors (`:386-404`) but none for spectator audio.
- **The crowd is built once per raid.** `ArenaSpectators.Create` runs on both `CreateFromScene` passes, but `ShouldBuild` (`:155-165`) returns false once the root exists. It is rebuilt only after a failure or between raids.
- **ScoreMonitors is handle-based.**
  - `int Claim(string owner)` returns a handle; setters and `Release(int)` take it.
  - A foreign owner is refused; only a developer-demo claim yields to a plugin.
  - A stale handle changes nothing and returns false (`ScoreMonitorHub.cs:55-89`).

### ArenaMode

**Its links to ArenaMapPort.** There is no reference to `SpectatorCrowd` or `SetExcitement`, and no `BepInDependency` on ArenaMapPort (`C/Plugin.cs:16-24`). It finds the plugin by `Chainloader.PluginInfos["com.maschine.ArenaMapPort"]`. It binds two contracts by reflection with exact signature checks:
- `ScoreMonitors`: `C/Compat/ArenaMapPortScoreMonitors.cs`, used from `C/Modes/TeamDeathmatchMode.Monitors.cs:21-39`, with one claim per match and a unique owner `"AM ..." + Guid`.
- `SpawnGates`: `C/Compat/ArenaMapPortSpawnGates.cs`. Only true absence counts as an older API (`BindCompletion`, `:82-101`).

**Event bus `ArenaMatch`.**
- **`OnKill(ArenaKill)` (`ArenaMatch.cs:38`).** It fires for every kill that the capture patch admits and the mode accepts:
  - The patch is a postfix on `Player.OnBeenKilledByAggressor` (`C/Combat/CombatPatches.cs:117-229`). It drops kills with no aggressor, self-kills (`:137-138`) and administrative removals (`ArenaAdministrativeDeathGate`, `:144`).
  - `ReportKill` drops kills that the mode's `TryBeginScoredEvent()` refuses (`:100-121`), and non-team Zombie kills after completion (`:78-98`).
  - Bot-vs-bot kills pass.
  - `DamageKind` is `EDamageType.ToString()` (`CombatPatches.cs:221`), for example `Melee`, `Explosion`, `GrenadeFragment`, `Flame`.
- **`OnCallout(string)` (`:47`).** Award labels for the local player only (`C/Combat/ArenaCombatAwards.cs:56-69`): FIRST BLOOD, multi-kill and streak labels, SHUTDOWN, AIRBORNE, NO SCOPE, COLLATERAL, PAYBACK and HEADSHOT, the last one for any single-hit headshot. Callouts fire before `OnKill` for the same kill (`ArenaMatch.cs:141-147`).
- **`OnAnnouncerCue(ArenaAnnouncerCue)` (`:56`).** The cue is an internal enum (`ArenaAnnouncerCue.cs:7`) with no score, side or round data.
- **Presentation consumers.** Announcer, hitmarker audio and lighting attach at `ArenaMatchService.cs:145-147`; `:148` is `mode.OnMatchStart`.

**Second bus `ArenaEvents`** (`C/Events/ArenaEvents.cs:17-37`):
- `OnPlayerDied` is raised at `C/Respawn/ArenaRespawnDriver.cs:75`.
- `OnPlayerRespawned` is raised at `:349`, `:450` and `:542`.
- Its doc says other mods should not bind it; a service inside ArenaMode may.

**Modes.**
- **Domination is Team DM.** `ModeRegistry.cs:18-19` builds it as `new TeamDeathmatchMode(true)`. The `RoundStart` cue (`TeamDeathmatchMode.cs:193-197`, at `MatchPhase == Live`) and the result cues (`TeamDeathmatchMatchFlow.cs:76-96`) therefore cover both.
- **Domination ownership changes are not events.** There is only a joined log string at `DominationRuntime.cs:181-183`.
- **Deathmatch** (`RoundsMode`) has public `OnRoundStarted` and `OnRoundEnded` (`:60-61`) that nothing subscribes to.
  - `OnRoundEnded` is raised at `:264`. The same call first raises the `MatchWon`, `MatchLost` or `NoContest` cue (`:254-259`).
  - `OnRoundStarted` (`:325`) also fires for round 1.
  - There is no `RoundStart` cue and no `RoundWon` or `RoundLost` cue.
- **Tactical** has `RoundStart` cues (`TacticalMode.cs:739`, `:1116`).
- **`NoContest`** is a technical teardown outcome. It is raised from the failed-start and abort paths: `ArenaMatchService.cs:179`, `TeamDeathmatchMode.cs:552`, `TacticalMode.cs:2629`, `RoundsMode.cs:258`. The announcer treats it as an abort (`C/Audio/AnnouncerService.cs:393-401`).

**Gaps and conventions.**
- **Missing events:** there is no Gun Game tier-up or Zombies wave event, and no team wipe outside Tactical and Zombies.
- **Shots:** there is no public shot event. ArenaMode does patch `Player.OnMakingShot` for every actor, including offline bots (`C/Combat/TeamSpawnProtectionHooks.cs:30-37`).
- **No ducking anywhere.** The announcer is a 2D Master-group source.
- **Precedent for an interlock:** `AnnouncerService.IsPmcVoiceBlocked` (`:77`; `ArenaPmcVoicePolicy.CanSpeak` at `:24-25`).
- **Push gate:** `ArenaAnnouncementDirector.Tick` (`C/Audio/ArenaAnnouncementDirector.cs:37-45`) allows a push only when:
  - `ReferenceEquals(match, ArenaMatchService.Current)`;
  - `Status == Started`;
  - `ArenaSession.IsExactActiveRaid(game, world)`.
- **Ticks that return early.** `ArenaMatchService.Tick` returns early on `TeamStartAbortPending` (`ArenaMatchService.cs:40-45`) and on `ArenaRaidExit.NativeStopOwnsCurrentRaid` (`:52-58`). Only the presentation-stop hook releases the monitors claim there (`TeamDeathmatchMode.Monitors.cs:13-19`).
- **Cameras after death.** `ArenaSpectatorService` owns the view in Tactical and Zombie team arenas (`C/Spectator/ArenaSpectatorService.cs:60`). There is also an optional death replay (`C/Respawn/ArenaRespawnDriver.cs:71-72`, `C/Spectator/KillcamCompat.cs`).
- **Body handover.** `GamePlayerOwner.MyPlayer` can be a dead or null body between bodies (`C/Respawn/LiveKitRespawn.cs:481`, `:528`). The raid stays `Started` meanwhile: Redeploy's `Redeploy/Redeploy.Core/Engine/DeathInterception.cs:35-38`, `:131` and `:266-308`.

## 4. Design

### 4.1 Event taxonomy

A small, stable set. A kind says what happened for the viewer's side; ArenaMapPort owns the kind-to-bank table. Kinds are documented numbers 1..31, not constants: the contract test forbids constants. `ReactionKinds` is the bit mask `1 << kind`, and both sides range-check 1..31 before shifting, because C# masks shift counts to 5 bits.

| # | Name | Caller means | Bank | Phase |
|---|---|---|---|---|
| 1 | Approve | Good for the viewer's side: own kill, award, lead taken (a round or match result is Finish) | Cheer 1119 and Owned 1120 as one pool of 17 (2.4, D5) | A |
| 2 | Disapprove | Bad: own death, lead lost, point lost | Angry 1121 (18 clips: 10 laments, 8 heckles of the dead player) | A |
| 3 | TakeCover | A shot or aim toward a stand (inference from the scolds); no recognised speech mentions an explosion (2.4) | Hide 1124 (34 clips: 12 panic, 22 directed) | Clips and runtime in A, sensor in C |
| 4 | Start | A round or the match begins | ChangedRound 1125 (8 clips) | A |
| 5 | Finish | A round or the match ends with a result; neutral commentary, no winner named (2.4) | EndRound 1126 (11 clips) | A |
| 6 | Lull | No kills for a while (content-confirmed, 2.4) | LowDeathIntensity 1116 (14 clips) | Reserved; clips ready, trigger is D14 |
| 7 | PurifiedTeam | A sweeping squad is sent in, the fight is about to get going (2.4); not a result, not a team wipe; no ArenaMode event is named | PurifiedTeam 1127 (12 clips) | Reserved |

- **Strength** is a float 0..1. Below 0.05 it is dropped (proposal) and NaN is rejected. It sets the voice count (proposal: up to 1/3 gives 1 voice, up to 2/3 gives 2, above that 3) and the size of the bed swell (4.4), not the gain. Two thresholds also pick the sub-pool of a clip (proposal, 2.4 and 4.4): from 0.8 the first Approve voice is drawn from the strong-only clips, below 0.4 Disapprove draws laments only.
- **No home side in the contract.** "Viewer's side" is the caller's judgement. Sided stands are not in v1 (section 9).
- **Priority** (proposal): Finish > Start > Approve = Disapprove > TakeCover > Lull > random calls.
- **Neutral and Participating are not kinds.** Participating stays the random baseline. Neutral has 2 local clips of 27 (a Cheer duplicate and a 0.86 s wordless scream, 2.4).

### 4.2 Who detects what

| Kind | ArenaMode raid | Plain raid (fallback, phase C) |
|---|---|---|
| Approve, Disapprove | ArenaMode: `OnKill`, whitelisted callouts, lead cues; Disapprove for self or environment deaths from `ArenaEvents.OnPlayerDied` | `Player.OnPlayerDeadStatic` (`Player.cs:25083`, `:30110`, a persistent static event): local kill gives Approve, local victim gives Disapprove. Skip `LastDamageType == Undefined` (inference: ArenaMode ends bodies with `Kill(Undefined)`) |
| Start | ArenaMode: the `RoundStart` cue of Team DM, Domination and Tactical; Deathmatch's `OnRoundStarted` | `SpawnGates.GatesOpened` or `GameWorld.AfterGameStarted` (`GameWorld.cs:725`, once per raid) |
| Finish | ArenaMode only: round and match results, never `NoContest` | none (the plain raid leaves `Started` at once) |
| TakeCover | ArenaMapPort in both raid kinds | the same |
| Lull, PurifiedTeam | reserved | reserved |

ArenaMapPort's own sensors (phase C, all inference until a host run):

- **Gunfire:** a Harmony postfix on `Player.OnMakingShot` (`Player.cs:26033`) that reads `__runOriginal`, so a shot that another prefix skipped is not counted. ArenaMode patches the same method independently; both coexist.
  - The alternative `GlobalEventDispatcher.OnSoundPlayed` (`GlobalEventDispatcher.cs:226`, throttled to one call per shooter per second by `AIData.cs:634-652`) is not clean.
  - The reason: EFT's own `BotsController.OnGrenadeExplosive` (`BotsController.cs:349-362`) dispatches `PlaySound(..., 190f, AISoundType.gun)` for every grenade explosion, smoke included. `OnRocketExplosive` (`:381-389`) does the same, and `BotEventDebug.cs:21` passes a null player.
  - A gunfire sensor there would double-count explosions and smoke.
- **Explosions:** `GlobalEventDispatcher.OnGrenadeExplosive` (`:376`, `isSmoke == false`) and `OnRocketExplosive`, as one source. Flashbangs and stun grenades also arrive with `isSmoke == false` (`Grenade.cs:339-357`); include or drop them by a rule written down when the sensor is built. Content (2.4): no recognised speech of a Hide clip mentions an explosion or a grenade, so this source has no content basis; the recommendation is to leave it out of v1 (if built, it plays the 12-clip panic subset only).
- **Every handler:**
  - is wrapped in try/catch. The dispatcher invokes handlers bare (`GlobalEventDispatcher.cs:1278-1281`, `:1311-1314`), so a throw would break the shot or the explosion;
  - checks for a null player;
  - logs nothing, searches no scene and allocates nothing;
  - is gated first on a cheap crowd-present flag. `ArenaMapRaid.IsLoaded()` makes five scene lookups, too expensive per shot.
- **Re-subscribe every raid.** `GlobalEventDispatcher.Stop()` nulls the dispatcher events at raid end (`:1349-1371`, from `BaseLocalGame.cs:972`); the dispatcher instance is reused. Re-subscribe each raid (remove, then add). `Player.OnPlayerDeadStatic` is not affected.
- **Pin the obfuscated delegate types** `GDelegate27` and `GDelegate17` in `HarmonyPatches.Verify()` (AGENTS.md rule).
- **TakeCover has the weakest basis.** Arena's Hide is a sustained animation state, while a voice reaction is a one-shot. It ships default off behind a developer key until heard. The subtitles (2.4) narrow it further: the Hide clips talk to a fighter who aims or fires toward them (toward the stands is an inference), so the sensor asks whether the line of fire points at a stand (testing it against the stand centres of the murmur bed, 4 to 6 per map, keeps the per-shot cost small), not whether something went off near one. A close shot without that direction plays only the 12-clip panic subset.
- **An external claim stands the plugin's sensors down.** While a caller holds a reaction claim, the plugin's own Approve, Disapprove and Start sensors stand down; otherwise every ArenaMode kill would react twice. TakeCover stays ArenaMapPort's own in both raid kinds.

### 4.3 Public contract extension (the ScoreMonitors shape)

`ContractVersion` stays 1. The new members are append-only (`SpectatorCrowd.cs:14-15`), and `IsAvailable` keeps its meaning (the bed is on). Reactions have their own availability signal, so they work with the bed switched off.

```csharp
public static int  ReactionKinds { get; }                            // bit mask (1 << kind), kinds 1..31; 0 = no reactions this raid
public static int  ClaimReactions(string owner)                      // handle > 0, or 0 (refused, no crowd, no reaction set)
public static bool React(int claim, int kind, float strength01)      // true = accepted into the queue; false = stale/foreign claim or refused
public static bool ReleaseReactions(int claim)                       // true = this claim was the holder
```

**Rules:**
- Same as the existing members: public static, primitive parameters, no fields, constants, events, generics or optional parameters.
- **Claim:**
  - A claim by another owner is refused. A `developer-demo` claim yields to a plugin's claim, and the same owner gets the same handle back.
  - The handle carries a per-raid generation. It becomes void when the crowd is removed (`ArenaSpectators.OnDestroy`, `:659-666`), like `ScoreMonitorHub.ResetForNewRaid`.
  - A lost claim shows up as `false` from `React`.
- **`React` cost:** any thread, state only, never throws. It is a lock plus a fixed ring of 16 slots (proposal), with no Unity API and no allocation. Each entry is stamped with `Stopwatch.GetTimestamp()`; the main-thread drain discards entries older than 2 s (proposal).
- **`React` refuses** a kind outside 1..31, a bit not in `ReactionKinds`, NaN, or a stale handle. Strength is clamped.
- **No crowd** (Iceberg, a vanilla map, no reaction set, switched off): `ReactionKinds == 0`, `ClaimReactions` returns 0, nothing is queued and nothing grows.
- **Unimplemented kinds** (Lull, PurifiedTeam) are absent from `ReactionKinds`. That keeps them reserved without a version change.
- **Binders** treat the four members as optional and bind them separately from `SetExcitement`. True absence means an older ArenaMapPort; a member present with the wrong signature is an incompatibility, warned once (as `ArenaMapPortSpawnGates.BindCompletion` does).
- **The pin test lists eight members** and keeps every prohibition.

### 4.4 Runtime behaviour

**Code placement.**
- A Unity-free core, `CrowdReactionModel`, holds the queue, claim, coalescing, cooldowns, priority, speaker and clip choice and the swell envelope. It is linked into the tests with a `Compile Include` in `ArenaMapPort.Server.Tests.csproj:40-50`.
- The Unity glue reuses the emitter, `BetterSource`, the `SpatialAudioSystem` container, `Combined` occlusion, the clip cache and the audio-reset handling of `SpectatorAudio`. The alternative `PlayAtPoint`, as in `GateSounds.cs:346`, propagates differently and returns null silently when the pool is empty.

**Placement and the guard (the checkers' main correction).** A drain behind the early return at `SpectatorAudio.cs:160-164` would never run while the local player is dead, and `StopAllVoices` there would cut reaction voices. Therefore:
- The reaction drain and the end-of-life handling of reaction voices run before that guard.
- The reaction path has its own gate: the reaction switch on, `Status == Started`, and the spatial audio services ready. It does not require `IsAlive`.
- Reaction voices stay outside `StopAllVoices` and `_schedule.Stop()`.
- A source pin holds this.

The random calls keep their living-player guard unchanged. In an ArenaMode raid the raid stays `Started` across the body handover, so a Disapprove for the local death and a Finish during the respawn wait remain possible (D12). In a plain raid `Status` leaves `Started` at once, so reactions there go silent too.

**Where reactions live.** `SpectatorAudio.Current` exists only when the 13 calls validate. Either reactions get their own component, or `SpectatorAudio` is changed so that a damaged call set leaves reactions working, and the reverse. Recommendation: one component with two independently validated sets (`ValidateReactions` beside the calls' validation, like `ValidateBed`), each failing with one Warning on its own.

**Listener position.** The calls measure from `MyPlayer` (`:205`), which can be a dead or null body between bodies. Reactions use a null-safe listener: the active `AudioListener` or camera position, falling back to `MyPlayer`. The concrete cases to check on the host are ArenaMode's `ArenaSpectatorService` (Tactical and Zombie team arenas) and the death replay. Where the listener sits there is not known; it is a host test item.

**Priority over the random calls.**
- An append-only `Hold(now, seconds)` on `SpectatorAudioSchedule` keeps new random calls from starting while a reaction plays, plus a gap.
- Calls already playing finish by themselves (at most 3.3 s): no cut and no fade, which keeps the single `VolumeFadeOut` pin.
- The gap after Approve, Start and Finish is at least one call pause (4 s, proposal): the random calls are passivity jibes (2.4) and would contradict a fresh cheer. Inference from the subtitles, to be checked by ear.

**Voices.**
- **Pool:** a separate pool of 3 reaction slots (proposal), so at most 5 voices come from the shared Environment pool of 32 sources (`spectator-audio.md:133`). A failed `GetSource` is skipped and counted.
- **Speakers:** chosen with `ChooseSpeaker` unchanged. For k voices, the k first distinct speakers by that law, each at least 4 m (proposal) from the ones already picked.
- **Clip choice:** the model reads the pool tag and sentence group of every clip from the manifest (4.5) and draws in three steps.
  1. **Pool:** the strength picks the sub-pools (table in 2.4). Approve below 0.8 draws from the 13 normal clips, from 0.8 the first voice is drawn from the strong-only clips (2 shouts, plus the 2 urging clips if the listening check keeps them); Disapprove below 0.4 draws the 10 laments, from 0.4 all 18; Start, Finish and Lull use their whole bank; TakeCover uses the 12 panic clips and adds the 22 directed ones only when the sensor saw the line of fire point at a stand.
  2. **Voice family `fanN`**, drawn with a weight of the square root of the family's clip count in the pool. A uniform family draw gives a family with one clip (fan7 and fan15 in Angry, fan7 and fan13 in Start) a share of 20 to 25 percent; a plain clip draw lets fan12 hold 44 percent of Angry and 50 percent of Start (fan12 has 47 of the 123 non-Participating clips).
  3. **Distinct sentences:** within one reaction the families and the sentence groups are distinct (the 36 Hide clips carry about 22 sentences, the 12 panic clips only 6); the last two clips and the last three sentence groups of a pool are not repeated. When the rules cannot all hold for the voices the strength asks for (Start has 5 sentence groups and 4 families, two of them with one clip; the panic pool has 6 sentence groups), relax, all steps at the full voice count before a voice is given up: (1) the "last two clips" rule, (2) the window of the last three sentence groups, one sentence at a time (3, 2, 1, none), (3) the family distinctness; a strong Approve's first voice goes through the same steps with the shouts before it may take the normal clips. Never the sentence-group distinctness within one reaction: only then does the reaction get fewer voices (lead decision 2026-11-02: the first order, which never relaxed the window, left every Start after a three-voice Start at two voices for the rest of the raid). A model test pins that order.
- **`_families`** becomes a general `fanN_` parse.

**Cooldowns and coalescing** (proposals, scaled by one developer key):
- **Per-kind gap:** Approve and Disapprove 3 s, TakeCover 15 s, Start and Finish 5 s, Lull and PurifiedTeam 20 s. Strength 0.8 or more halves the gap.
- **Coalescing:** within one drain the maximum strength per kind is kept, and a Start or Finish removes queued Approve and Disapprove. The deciding kill's `OnKill` arrives before its `MatchWon` cue (`ArenaMatch.cs:147-148`).
- **Global rate:** at most one reaction start per 0.75 s.

**Gain.** Reactions use gain 0.5 (developer key `ReactionVolume`) and the calls' normalisation (-22 dBFS RMS), so loudness comes from the voice count. ArenaMapPort does no ducking. The announcer conflict is handled on ArenaMode's side (6.6).

**Murmur-bed coupling.**
- `SetExcitement` stays the sustained level, set by the external caller only. There is no automatic excitement from gunfire (D6).
- Each reaction adds a transient swell in `CrowdExcitement`: attack 0.3 s, time constant 3 s, amplitude equal to strength (proposals).
- Bed level = max(smoothed external level, swell), so the factor never exceeds 1.6 and `ClipGain` 3.2 stays valid.
- Allocation-free (`SpectatorCrowdBedSourcePinTests.cs:59-72`).
- **The bed is the Neutral clip since `deb7b85` (user decision 2026-11-02).** Measured on the decoded files: -31.2 dBFS RMS in the mono downmix against -40.1 for the postmatch clip. The extractor trims it by -8.9 dB to the approved level; the shipped loop is 79.5 s (7.0 MB) with the usual 500 ms seam. `ClipGain` 3.2 is MaxVolume x MaxFactor and does not depend on the clip, so the swell headroom above holds unchanged; a louder bed is `Spectators/CrowdBedVolume` (up to 2.0).

**Audio reset at raid start.** The logs show an audio reset 8-14 s after the crowd is built (`spectator-audio.md:744-746`). The queue survives it, clips are rebuilt lazily, and entries older than 2 s are dropped.

**Loading.**
- **Worker:** reaction clips are read, hashed (CNG) and PCM-decoded in the existing crowd worker (`ArenaSpectators.cs:286-339`). The worker never touches Unity and never logs.
- **Clips:** `AudioClip`s are created lazily on first use. All of them at once would be about 56 MB of floats (inference).
- **Failure:** a damaged or missing reaction set gives one Warning and leaves the calls working.

**NoiseProbe.**
- Append a `NoiseEv` value and add `Note*` one-liners behind `if (NoiseProbe.Armed)`.
- Add census fields (`NoiseProbe.cs:529-537`, `:597-628`) and `ProbeVoices`, `AnyAudible` and `ActiveVoices` coverage.
- Extend `NoiseText.ShortClip` (`NoiseProbeModel.cs`, after `IsCause` at `:48`), which shortens only `_participating` names.
- Add the new `NoiseEv` to the `IsCause` list in `NoiseProbeModelTests.cs:489-490`.

### 4.5 Audio data

**Banks (recommendation, pending D1 and D2).**
- **Phase A:** stage the usable embedded clips of Cheer, Owned, Angry, ChangedRound, EndRound and Hide. That is 89 distinct clips, 226.1 s and 19.9 MB (91 clips, 229.3 s and 20.2 MB before 2 are left out): 17 (Cheer and Owned, 13 shared names stored once) + 18 + 9 + 11 + 34. Of the 89, 70 are keep and 19 listen (2.4).
- **Later:** LowDeathIntensity (14 of 15 clips, 34.1 s, 3.0 MB) and PurifiedTeam (12 of 16, 33.2 s, 2.9 MB) come with their kinds. All eight banks then make 115 clips, 293.3 s and 25.9 MB (122 clips before 7 are left out); with Lull only, 103 clips (260.1 s, 22.9 MB).
- **Not exported:** Neutral (one optional start shout, 2.4), every clip left out by name, and every external Scav reference (pending D2).

**Extractor** (`extract_spectator_audio.py`):
- **Bank table:** name, trigger, expected embedded count, left-out clips (name and reason code, 2.4), exported count and omitted-external count; the validators check embedded = exported + left out:

  | Bank | Embedded | Left out (reason) | Exported | Omitted external |
  |---|---:|---|---:|---|
  | Cheer | 14 | none | 14 | 15 |
  | Owned | 16 | none | 16 (17 in the union with Cheer) | 3 |
  | Angry | 18 | none | 18 | 23, plus 2 null |
  | ChangedRound | 9 | none | 9 | 5 |
  | EndRound | 11 | none | 11 | 8 |
  | Hide | 36 | `fan8_aim_hide_01`, `fan12_shoot_hide_05` (SLUR) | 34 | 14 |

  Later banks: LowDeathIntensity 15 embedded, `fan12_low_death_intensity_04` (UNCL) out, 14 exported, 0 external; PurifiedTeam 16 embedded, `fan7_sweep_team_01`, `fan12_sweep_team_11`, `fan10_sweep_team_02`, `fan13_sweep_team_02` (WRONG) out, 12 exported, 0 external.

- **Naming:** a generalised name regex for the clip categories and `fan15`; today only fan7-fan13 and `participating` pass (`:25`).
- **Length cap:** a 7 s cap for reactions; the calls keep 5 s.
- **Over 5 s after the exclusions:** three exported clips (6.71 s in Cheer and Owned, 5.36 s in Angry, 5.49 s in ChangedRound); the other three clips of 2.2 above 5 s (5.66 s and 5.22 s in PurifiedTeam, 5.16 s in LowDeathIntensity) are left out. The 7 s cap stays.
- **Pool sidecar:** a tracked `tools/extract/reaction_pools.json` holds, per clip name, bank, pool tag (praise, gloat, shout, urge, lament, heckle, panic, directed, start, finish, lull, hype), sentence-group id, status (keep, listen, out) and reason code. Names and tags only: no transcript, translation or voice line enters git. The extractor reads it, enforces the left-out list and writes `pool` and `group` per clip into the manifest; a test pins that every exported clip has exactly one pool and that the sidecar covers the bank counts above.
- **Spread groups:** the per-bank spread-group check (`:209-210`) needs a rule for the new banks.
- **Key-source guard, which is new work:**
  - `protected_names()` today covers only the fan bundle's file name (`:203`) and the bed's source (`:193`).
  - Add a per-clip stream-source check: no clip may stream from a key-source file, using the 19 clips of 2.3 as the control set.
  - Add a staged-file check in `validate_distribution.py`: no staged WAV may be byte-identical to a key-source file.

**Format.** Mono 44.1 kHz 16-bit WAV with the same `normalize()` as the calls: -22 dBFS RMS, at most +6 dB, peak at most -3 dBFS, ffmpeg resample when needed.

**Manifest.**
- Schema 1 and the Participating keys stay unchanged.
- An optional `reactions` object sits next to `bed`. It holds a deduplicated clip table (name, size, frames, SHA-256) plus per bank its trigger, clip-name list, left-out clip names with reason code and omitted-external count, and per clip its pool tag and sentence-group id (from the sidecar above). Cheer and Owned share 13 names, and the validators' expected-file set and name-uniqueness check work on the union.
- `validate(require_reactions)`; `validate_distribution.py:525-535` passes the flag, and its byte comparison then covers the new files.
- The kind-to-bank table lives in the client and is pinned by a test.

**Flat folder.** The csproj glob (`ArenaMapPort.Client.csproj:196-206`) and the packer regex (`make_release_zips.py:360`) are flat. The WAVs stay in `spectator-audio/`, and the regex widens to the clip categories with the calls' numeric suffix: `kill_cheer`, `kill_angry`, `match_start`, `match_end`, `aim_hide`, `shoot_hide`, later `low_death_intensity` and `sweep_team`.

**Distribution counts** (the manifest is not a new file):

| Layout | today | phase A (+89) | with Lull (+103) | eight banks (+115) |
|---|---:|---:|---:|---:|
| released 0.4.0 layout (no Iceberg) | 256 | 345 | 359 | 371 |
| current dist with Iceberg | 283 (Iceberg part 27) | 372 | 386 | 398 |
| core part | 154 | 243 | 257 | 269 |

No test pins the totals; `make_release_zips.py --plan` prints them. AGENTS.md still says 281 and an Iceberg part of 25, which is stale since the DynamicMaps overview added 2 files.

### 4.6 Developer keys, demo and test trigger

All keys are bound with `Plugin.Instance.Settings.Bind(...)` and are developer-only. They live in `SpectatorAudio.cs` or a new file, not in `SpectatorCrowdBed.cs`, whose `s.Bind(` count is pinned at three (`SpectatorCrowdBedSourcePinTests.cs:84`). None is public.

| Key (section `Spectators`) | Default | Meaning |
|---|---|---|
| `Reactions` | on | Master switch for reaction voices and the swell (live) |
| `ReactionVolume` | 0.5 | Gain of the reaction voices |
| `ReactionSwell` | 1 | Scales the bed swell; 0 = off |
| `ReactionGapScale` | 1 | Multiplies the cooldown table |
| `ReactionAuto` | Off | The plugin's own sensors: Off, Kills, All (phase C) |
| `ReactionDemo` | Off | Off, Cycle or Once: claims as `developer-demo` and fires each implemented kind at stepped strengths |
| `ReactionDemoKind` | 0 | With Once: which kind (0 = all) |

- **Logging:** Debug only, tag `[Spectator]`, one line per played reaction (`reaction kind=... strength=... voices=... bank=...`) and per dropped one (reason code). Nothing logs from a calling thread.
- **Host trigger:** the host path is `uibridge_config_set` (letter keys do not reach EFT).
- **Hearing reactions alone:** `Audio diagnostics/SpectatorCalls` silences the random calls.

## 5. Change list: ArenaMapPort

Phases:
- **A:** data, contract, runtime, demo and swell.
- **B:** ArenaMode (section 6).
- **C:** the plugin's own sensors.

The listening check comes after A, before B is trusted.

| File | Change |
|---|---|
| `tools/extract/extract_spectator_audio.py` | Bank table, name regex, 7 s reaction cap, deduplicated `reactions` manifest, `validate(require_reactions)`, per-clip key-source stream check; docstring correction (2.1) |
| New `tools/extract/reaction_pools.json` | Pool tag, sentence group, status and reason code per clip name (2.4, 4.5); read by the extractor, pinned by a test |
| `ArenaMapPort.Client.csproj:196-206` | Verify that the flat glob copies the new WAVs; keep the missing-manifest error |
| `tools/validate/validate_distribution.py:525-535` | Pass `require_reactions`; new staged-WAV key-source check |
| `tools/release/make_release_zips.py:360,404,696,711,760` | Widen the regex and the texts |
| `ArenaMapPort.Client/SpectatorCrowd.cs` | The four members (4.3); fix the doc comment at `:6` |
| New `CrowdReactionModel.cs` | Unity-free queue, claim with handle and generation, coalescing, cooldowns, priority, speaker and clip choice, swell |
| `SpectatorAudioModel.cs` | `ReactionManifest`, `ValidateReactions`, `SpectatorAudioSchedule.Hold`, length cap per set |
| `SpectatorAudio.cs` | Reaction slots (`:119-125`), the `_families` parse (`:118`), the reaction drain and gate before the guard (`:160-164`), null-safe listener, probe helpers (`:341-366`) |
| `SpectatorCrowdBedModel.cs`, `SpectatorCrowdBed.cs` | `CrowdExcitement` swell (allocation-free) and reset; doc comment fixes |
| `ArenaSpectators.cs:210-222,286-339,659-666` | Worker prefetch of the reaction set, create order, claim and queue reset on destroy |
| `NoiseProbeModel.cs`, `NoiseProbe.cs` | `NoiseEv`, `IsCause`, `ShortClip`, census |
| New `SpectatorReactionDemo.cs` | The developer demo |
| Phase C: new `SpectatorReactionSensors.cs` | `OnPlayerDeadStatic`, an `OnMakingShot` postfix with `__runOriginal`, explosion events, `GatesOpened`; per-raid idempotent subscription, try/catch, null guards, delegate pins in `HarmonyPatches.Verify()`, stand-down on a claim |

**Tests:**
- **.NET:**
  - `SpectatorCrowdContractTests.cs:27-62`: eight members, all prohibitions kept.
  - New `CrowdReactionModelTests` covering:
    - claim, handle and generation, stale handle, foreign owner, demo yield;
    - queue bound, thread safety and stale drop;
    - coalescing, including Finish over Approve;
    - cooldowns and the kind range 1..31;
    - weighted family choice, distinct sentence groups, the strength bands (Approve 0.8, Disapprove 0.4), and a swell never above level 1;
    - `Hold`.
  - New manifest tests: a damaged reaction set leaves the calls valid, and the reverse.
  - Source pins:
    - the reaction drain sits before the guard, with no `StopAllVoices` on reaction slots;
    - `NoiseProbeSourcePinTests.cs:264-309,324-358`;
    - `SpectatorCrowdBedSourcePinTests.cs:59-72,84`;
    - `SpectatorWorkerSourcePinTests` (reaction set in the worker);
    - no Unity API and no scene search in `React`;
    - sensors subscribe idempotently and every handler catches.
  - `NoiseProbeModelTests.cs:489-490` (`IsCause` list).
- **Python:**
  - `test_spectator_audio.py:40-62`: flip the rejection of Cheer, of trigger 1119 and of extra files. Keep the tamper and key-source rejections (`:220`).
  - `test_validate_distribution.py:383-399,1285-1370`.
  - `test_release_zips.py:88-92,194-195,303,375-383`: the assertion that `arena_fan7_kill_cheer_01.wav` is an error flips; an unknown category stays an error.
  - Offline pins of the 89 (103, 115) clips, the 7 left out by name and the bank counts against the installed Arena, skipped without it.
- **Docs:**
  - Append to `spectator-audio.md`; correct `:13` (`ChangedRound`, not `StartRound`) and `:568-569` (the in-match beds of 2.1).
  - Update `dist-manifest.md`, `arena-audio.md:583`, the AGENTS.md layout row, the counts (283) and the rule "Keep only the 13 embedded Participating fan clips", which changes with D1 and D2.
  - The released sentences in `README.md:29-31,226`, `FORGE.md:27-28`, `CHANGELOG.md:23,26` and `docs/releases/v0.4.0.md:16,20-21` change only in the next release's texts. README.md:31 says "kill and round-result reactions are not implemented yet" and README.md:226 says "crowd reactions to match events are not included".
- **Release:** run the extractor in the main checkout, a client Release build and `validate_distribution.py`, then a single-ZIP re-cut and a VirusTotal scan of both DLLs. The number and form are the user's decision.

## 6. Change list: ArenaMode (for the user or Codex)

1. **Binder.** New `C/Compat/ArenaMapPortSpectatorCrowd.cs`, a sibling of `ArenaMapPortScoreMonitors.cs` (sketch below). No `BepInDependency`. It binds once per match and caches the delegates.
2. **Presentation service.** Suggested name `C/Audio/ArenaCrowdReactions.cs`.
   - **Attach** with announcer, hitmarker and lighting at `ArenaMatchService.cs:145-147`, before `mode.OnMatchStart` at `:148`.
   - **Claim** once per match with a unique owner (`"AM crowd " + Guid`, as Team DM does for the monitors).
     - Retry only `ReactionKinds` and `ClaimReactions`, for a bounded window (proposal: until `Status == Started` plus 30 s).
     - Stop at once when the plugin or the type is absent or the contract is incompatible.
     - On Iceberg the outcome is no claim and no retries.
   - **Gate every push** like `ArenaAnnouncementDirector.Tick` (`:37-45`): `ReferenceEquals(match, ArenaMatchService.Current)`, `Status == Started` and `ArenaSession.IsExactActiveRaid(game, world)`.
   - **Release** on every exit:
     - the failure path `:160-161`, `OnRaidEnded` `:203`, `RecoverAtNewRaidStart` `:221` and `C/Plugin.cs:276-277`;
     - `NativeStopOwnsCurrentRaid` and `TeamStartAbortPending`, both through the presentation-stop hook as `TeamDeathmatchMode.Monitors.cs:13-19` does;
     - the failed-start abort.
   - **Release** when `React` returns false.
3. **Subscriptions.** All on the main thread: `match.OnKill` (`ArenaMatch.cs:38`), `match.OnCallout` (`:47`), `match.OnAnnouncerCue` (`:56`), and `ArenaEvents.OnPlayerDied` for deaths without a kill record. Mapping, with strengths as proposals:

| ArenaMode source | Kind, strength |
|---|---|
| `KilledByLocal` kill | Approve 0.35; 0.6 when `DamageKind` is `Melee`, `Explosion` or `GrenadeFragment`, or for a headshot |
| Callouts FIRST BLOOD, PAYBACK, COLLATERAL, multi-kill and streak labels, SHUTDOWN, AIRBORNE, NO SCOPE | Approve 0.9 (HEADSHOT stays on the 0.6 path); from 0.8 the first voice is drawn from the strong-only clips (2.4) |
| `LocalIsVictim`, or `ArenaEvents.OnPlayerDied` without a kill record | Disapprove 0.5 |
| Teammate kill (Team DM flags) | Approve 0.25, optional |
| `RoundStart` cue (Team DM, Domination, Tactical); Deathmatch `OnRoundStarted` | Start 1.0 |
| `RoundWon`, `RoundLost`, `MatchWon`, `MatchLost`, `Draw` | Finish 1.0 (no win/loss colouring, D7; the EndRound clips name no winner, 2.4) |
| `NoContest` (abort, failed start, teardown) | no reaction |
| `TakenTheLead` | Approve 0.5 |
| `LostTheLead` | Disapprove 0.3 (laments only, below the 0.4 band of 2.4; own death keeps 0.5) |
| `LastManStanding`, `Clutch`, `FinalWeapon`, `LadderComplete` | Approve 0.9 |
| Countdown cues (`Three`, `Two`, `One`, `PrepareToFight`) | none (a probable countdown complaint, `fan12_match_start_05`, stays a listen clip of Start, 2.4) |
| No `OnKill` for 60 s while `Live` (proposal; every kill counts, bot-vs-bot included; at most one per 90 s) | Lull 0.4 (reserved until D14) |
| a sweeping squad or wave appears | PurifiedTeam: ArenaMode has no such event today (no Zombies wave event, section 3); nothing is sent |

   - Ignore kills with `ScoreEligible == false` and bot-vs-bot kills in v1 (D5).
   - When the deciding kill and its result cue arrive together, send Finish only.
   - Deathmatch: `RoundsMode.PublishRoundEnd` raises the result cue and then `OnRoundEnded` in the same call (`:254-264`). A round end that already sent a match result sends nothing more; only a non-terminal round end sends its own Finish.
4. **Sustained excitement.**
   - Poll `ArenaAnnouncementDirector.ReadSnapshot(match.Mode)` (`:60`) at about 1 Hz and call `SetExcitement` with a monotone mapping (proposal): `MatchPoint`, `FinalRound`, a late clock or few `KillsRemaining` raise the level; `Terminal` or not `Live` sets 0.
   - The snapshot does not cover Domination objectives, the Tactical round score or Zombies waves.
5. **New call sites for gaps.**
   - **Deathmatch:** the `OnRoundStarted` and `OnRoundEnded` wiring of point 3.
   - **Domination:** a per-point ownership diff at `DominationRuntime.cs:181-183`. A capture by the local team gives Approve 0.6 and a loss Disapprove 0.6. Arena's own announcer has CapturePointAppearance 1128 and PointCapturing 1129 for this kind of event. Later.
   - **Skip** Gun Game tier-ups (`FinalWeapon` and `LadderComplete` are cues) and Zombies (D5).
6. **Announcer interlock.** While `AnnouncerService.IsPmcVoiceBlocked` (`:77`) is true, skip Approve or send it weaker. Never skip Start and Finish. That masking would otherwise occur is an inference; nothing has been listened to.
7. **Setting.** An F12 key, default on, optionally frozen at arm like the announcer (`ArenaAnnouncerGate`, `ArenaSession.cs:121`). Not a mutator row: the map screen already has 23 mutator ids in 22 visible slots (`docs/FEATURES.md:817`).
8. **Tests.** A production-linked harness in the style of `tools/TdmMonitorChecks/Program.cs` (suggested `tools/CrowdReactionChecks`) and the adversarial review that ArenaMode's AGENTS.md:194 requires. `tools/AnnouncementIntegrationChecks` is recorded as broken.

**The ArenaMode code must not:**
- throw, block or log from the hot path. A failed reaction never changes match state;
- push outside an armed ArenaMode match or in a plain raid on a ported map;
- keep a claim past any exit path;
- subscribe to the process-global `SpawnGates.GatesOpened` event (`docs/AREA-RULES.md:719-721`);
- send reactions every frame;
- require the reaction members for the `SetExcitement` binding, or assume that `ContractVersion` changes;
- depend on `ArenaEvents.OnLocalDamageDealt` (raised only with the hitmarker settings on), or on Fika, which ArenaMode refuses.

**Binder sketch.** The getter and binder helpers are copies of the private ones in `ArenaMapPortScoreMonitors.cs:83-102`. The sketch was compiled against a stub of the proposed contract (netstandard2.1 target, LangVersion latest).

```csharp
using System;
using System.Reflection;
using BepInEx.Bootstrap;
namespace ArenaMode.Compat;

/// Optional, primitive-only MapPort contract: SpectatorCrowd (ContractVersion 1) with its append-only reaction members.
/// Absent members mean an older ArenaMapPort; a member with another signature is incompatible (warned once). Match state never depends on it.
internal static class ArenaMapPortSpectatorCrowd
{
    // MapPort documents the kind numbers instead of publishing constants (its contract test forbids them). Mirror them here.
    internal const int Approve = 1, Disapprove = 2, TakeCover = 3, Start = 4, Finish = 5;

    internal sealed class Api
    {
        internal Func<int> Kinds; internal Func<string, int> Claim; internal Func<int, int, float, bool> React;
        internal Func<int, bool> Release; internal Action<float> Excite;   // Excite is optional
    }

    internal static Api Bind()   // once per match
    {
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue("com.maschine.ArenaMapPort", out var plugin) || plugin.Instance == null) return null;
            var type = plugin.Instance.GetType().Assembly.GetType("ArenaMapPort.Client.SpectatorCrowd");
            if (type == null) return null;
            var version = Getter<int>(type, "ContractVersion");
            if (version == null || version() != 1) return null;
            var api = new Api
            {
                Kinds   = Getter<int>(type, "ReactionKinds"),
                Claim   = Bind<Func<string, int>>(type, "ClaimReactions", typeof(int), typeof(string)),
                React   = Bind<Func<int, int, float, bool>>(type, "React", typeof(bool), typeof(int), typeof(int), typeof(float)),
                Release = Bind<Func<int, bool>>(type, "ReleaseReactions", typeof(bool), typeof(int)),
                Excite  = Bind<Action<float>>(type, "SetExcitement", typeof(void), typeof(float)),
            };
            return api.Kinds == null || api.Claim == null || api.React == null || api.Release == null ? null : api;   // older MapPort
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("ArenaMapPort crowd reactions unavailable: " + ex.GetBaseException().Message); return null; }
    }

    // Getter<T> and Bind<T>: copies of the private helpers in ArenaMapPortScoreMonitors.cs:83-102; warn once when a member exists with another signature.

    internal sealed class Session
    {
        private readonly Api _api; private readonly int _claim; private readonly int _mask;
        private bool _released; private float _lastLevel = -1f;
        private Session(Api api, int claim, int mask) { _api = api; _claim = claim; _mask = mask; }

        internal static Session TryClaim(Api api, string owner)   // retried only within the bounded window
        {
            if (api == null || string.IsNullOrWhiteSpace(owner)) return null;
            try
            {
                int mask = api.Kinds();
                if (mask == 0) return null;                          // no crowd: Iceberg, vanilla map, reactions off
                int claim = api.Claim(owner);
                return claim > 0 ? new Session(api, claim, mask) : null;
            }
            catch { return null; }
        }

        internal bool Alive => !_released;
        internal bool Has(int kind) => (uint)(kind - 1) < 31u && (_mask & (1 << kind)) != 0;

        internal void React(int kind, float strength)
        {
            if (_released || !Has(kind)) return;
            try { if (!_api.React(_claim, kind, strength)) Release(); }        // a lost claim ends the session
            catch (Exception ex) { Release(); Plugin.ModLogger.LogWarning("ArenaMapPort crowd reactions stopped: " + ex.GetBaseException().Message); }
        }

        internal void SetExcitement(float level)
        {
            if (_released || _api.Excite == null || level == _lastLevel) return;
            _lastLevel = level;
            try { _api.Excite(level); } catch { /* cosmetic */ }
        }

        internal void Release()
        {
            if (_released) return;
            _released = true;
            try { _api.Release(_claim); } catch { }
        }
    }
}
```

## 7. Verification plan

**Offline, on the VM:**
- `python -m unittest discover -s tools/tests -p "test_*.py"`.
- `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release`.
- `extract_spectator_audio.py extract`, then `validate_distribution.py` (byte comparison and key-source checks).
- `make_release_zips.py --plan` for the counts of 4.5.
- ArenaMode: its harness and its adversarial review.

**Host, GPU-2, after phase A.** First check that `windows-gpu-2` connects; `gpu-run` works without it.
1. `uibridge_status`.
   - Deploy only through `tools/deploy-to-spt.ps1`. The dist grows by about 20 MB: close EFT and wait for Syncthing.
   - `gpu-run restart --host host2` for the new client DLL.
2. One raid without bots per crowd map with `Spectators/ReactionDemo=Cycle`.
   - The Debug lines show every implemented kind with its bank, and the set loads in the worker.
   - `load total=` stays near 2.1 s with `wait=0`.
   - No ArenaMapPort warnings, no new hitch (`uibridge_status` max).
3. A reaction right after the crowd is built, to cover the audio-reset window.
4. A plain-raid local death: reactions end with the raid, with no exception.
5. Iceberg: `ReactionKinds == 0`, no warning.
6. NoiseProbe in `Detect`: reaction voices are not orphans and cause no new bursts.
7. The hideout once.
8. Phase C: a raid with bots and `ReactionAuto`, over two consecutive raids (re-subscription).
9. Phase B, an ArenaMode raid with the binder:
   - kills, awards, round start and finish react once each;
   - the plugin's sensors stand down;
   - no cheer before a Finish;
   - a reaction is audible during the respawn wait and in the `ArenaSpectatorService` camera, with the listener position checked;
   - `NoContest` stays silent.

**Listening, by the user.** Nothing may be called approved before this.
- Each kind alone, with `SpectatorCalls` off.
- Loudness against the calls and the murmur, and the swell.
- The merged Approve pool: a gloat clip on a kill, the strong-only shouts on awards, the 5 listen clips of the pool.
- Reactions over the announcer in Team DM and Tactical.
- The behaviour while dead.
- Variety: 17 Approve clips (14 distinct sentences) and 18 Disapprove clips (15) against Arena's roughly 29 (Cheer) and 43 (Angry) slots, about half of them Scav references. The thinnest pools are Start (8 clips, 7 sentences, 4 voice families) and the TakeCover panic pool (12 clips, 6 sentences).
- The 19 phase-A clips marked listen and, for a later bank, its listen clips (2.4, D13); the 7 clips left out by name stay out unless the user reverses.

## 8. Decisions for the user

| # | Decision | Recommendation |
|---|---|---|
| D1 | Banks now: 89 clips (6 banks, 91 less 2 left out), 103 with Lull, or 115 with Lull and PurifiedTeam (8 banks, 122 less 7) | **Decided 2026-11-02 (user: "dann lassen wir die 19 clips erstmal weg. gerne wie empfohlen mit der umsetzung beginnen"):** phase A exports only the 70 keep clips of the six banks; the 19 listen clips stay out until the user has listened (a clip is promoted by its status in `tools/extract/reaction_pools.json`), the 2 SLUR clips stay out; the other recommendations of this table apply. Earlier recommendation: 89 now. Lull's 14 once D14 settles its trigger (its meaning is content-confirmed); PurifiedTeam's 12 only when an ArenaMode event is named for it. Optional extras for Start: the Neutral start shout (+1) and 5 neutral PurifiedTeam hype clips |
| D2 | External Scav references of the reaction banks (68 slots, 60 distinct in phase A; the earlier listening decision covered only Participating's 9, which are spotting callouts) | Keep excluded, consistent with that decision. By content about 23 to 28 of the 60 would fit a spectator (kill praise about 9 to 11, Angry groans 6 firm and 3 possible, session-start greetings 4, Hide 4 in meaning: 2 take-cover calls, a plea and a scold); all are Scav voices in combat delivery, and nothing in the subtitles argues for reversing. If the user reverses after the first listening, the 4 greetings and the 6 groans are the first candidates. Ask again after the first listening |
| D3 | Arena's in-match beds | **Decided 2026-11-02 (user):** the 80 s Neutral bed replaces the postmatch clip as the murmur (implemented in `deb7b85`, trimmed -8.9 dB to the approved level, 4.4). The Cheer and Angry beds (steady roars and boos, no intelligible speech) as swell layers stay out of v1; after the first listening, a developer-key A/B only. **Revised 2026-11-07 (user: "gerne einbauen. das neutrale gemurmel anzuheben macht glaube ich nicht wirklich sinn, eher sollten dann die lauteren Cheer und Angry Betten laufen"):** the Cheer and Angry beds are built as the reaction's roar layer (Approve, Start, Finish -> Cheer; Disapprove -> Angry; from the murmur's stands, common -32 dBFS RMS, own sources) and the phase-A neutral-bed swell is dropped; see [progress/spectator-audio.md](progress/spectator-audio.md) "2026-11-07 - Reaction roar layer". The Cheer beds are about 8 to 9 dB louder than the Neutral bed, the Angry beds 1 to 5 dB, and they would need their own sources. The premise behind the murmur ("Arena has none") is corrected in the next release texts either way |
| D4 | Loudness of reactions | Gain 0.5 and the calls' normalisation; raise only after hearing |
| D5 | Which ArenaMode events | Local-centric: own kills, own deaths, whitelisted awards, lead changes, round and match start and result; Domination captures later. No bot-vs-bot kills, no Zombies in v1. Owned is not a separate pool: its clips are the Cheer clips plus three twins (2.4); strong events (awards) use the strong-only shouts of the merged Approve pool |
| D6 | Automatic excitement from gunfire | No: swells from reactions plus the external `SetExcitement` only |
| D7 | Win or loss colouring of Finish | None in v1. The EndRound clips support that: none names a winner or a loser; 3 praise the fighters, 1 is positive in tone, 2 are lukewarm or disappointed. Whether a loss variant needs any clip dropped is a listening question; decide after listening |
| D8 | Plain-raid sensors (phase C) | Start and kills behind `ReactionAuto` (default off); TakeCover off until heard, redefined as a shot or aim toward a stand with the explosion source dropped (2.4); droppable |
| D9 | Animation reactions (Pleased/BigWin, Anger, Owned, Shock triggers, Hide/Parapet states) | Out of scope; a separate animation export later if audio without visuals bothers you |
| D10 | Player-facing switch | None in ArenaMapPort (developer keys only); ArenaMode's F12 key |
| D11 | Release number and form | Yours; the client DLL changes, so a re-cut and a VirusTotal scan follow |
| D12 | Reactions while the local player is dead | Allow them (ArenaMode raids); the random calls stay silenced |
| D13 | Content audit of the new shout clips | **Decided 2026-11-02 (user):** the 19 listen clips are left out of phase A instead of being listened to now (D1). The subtitles are the text check the plan asked for, but they are automatic (speech recognition plus machine translation; 48 of 252 rows flagged uncertain), not a manual review. Result: two Hide clips contain a slur (left out), three Hide clips mock mental health and one makes a crude family joke (kept as listen), strong profanity is in nearly every bank as in the shipped Participating calls, at least five Scav references contain a slur (three of them in Hide) and many more abusive terms (all excluded). Recommendation: the user listens once to the 19 phase-A listen clips; release texts may say that two clips in which automatic subtitles showed a slur were left out and that the subtitles are not a manual review, never "reviewed" |
| D14 | Lull (LowDeathIntensity) | **Decided 2026-11-02 (user, "wie empfohlen"):** built after phase A, trigger on ArenaMode's side as recommended here. The bank is content-confirmed as complaints about a dull match. Recommendation: build it after phase A, with the trigger on ArenaMode's side (no `OnKill` for 60 s while `Live`, Lull 0.4, at most one per 90 s); no contract change. Because the random Participating calls have the same tone, the user may prefer to add the 14 clips to the random pool instead (that changes the approved call set) or to skip Lull |

## 9. Risks and open questions

- **Arena's triggers are not confirmed.** Bank meaning is inference from names and the id space. Since 2026-11-02 the automatic subtitles (2.4) back it for Cheer, Angry, ChangedRound, EndRound and LowDeathIntensity, narrow Hide to a shot or aim toward a stand, and leave Owned (no meaning of its own) and PurifiedTeam (no ArenaMode event) open. The subtitles are not a manual check and nobody has listened. Player texts say "original fan clips played on match events chosen by the port", not "like Arena".
- **The death guard and the listener.** The reaction path bypasses the living-player guard by design. The respawn window, ArenaMode's spectator camera, the death replay and the Killcam mod are host test items.
- **Claim lifetime.** The crowd is built once per raid. The claim is lost only through a failed first build or the unload between raids, which the handle generation covers.
- **Shared sources.** The audio reset window (8-14 s after the crowd is built) and the shared Environment pool of 32 sources: a null `GetSource` drops a reaction, counted.
- **Hot-path rules.** No scene searches, no per-shot allocation, try/catch in every dispatcher handler, and re-subscription every raid.
- **NoiseProbe coverage.** Owned sources outside its walk are invisible unless the new voices get taps and census fields.
- **Load cost and memory.** The worker path is mandatory; clips are created lazily.
- **Pinned lists.** The extractor, client validator, packer and distribution validator change in one commit, or `dist/` stops validating.
- **Two writers of excitement.** The claim rule and no automatic level avoid them by design; a second external caller would still be last-write-wins.
- **Sided stands.** Not implemented. Arena's two-stand emitter groups suggest sides (inference); using them needs a stand-to-team mapping from ArenaMode and Arena's emitter positions, which no extractor writes yet.
- **Iceberg.** It has Arena-authored crowd emitters (12 `FansAmbient`) but no spectators; giving it a bed or reactions is a separate decision.
- **Unverified:**
  - whether the installed ArenaMode matches HEAD `65007e74`;
  - where the AudioListener sits in ArenaMode's spectator camera;
  - Fika behaviour, which does not exist.
