"""ArenaMapPort Arena audio extractor for ArenaMode (optional dependency, not used by ArenaMapPort itself).

ArenaMode (D:/SPT41/Development/ArenaMode, read-only, the user's other mod) plays announcer stingers
as OGG Vorbis files from `BepInEx/plugins/ArenaMode/sounds` via
`UnityWebRequestMultimedia.GetAudioClip(AudioType.OGGVORBIS)`
(`ArenaMode.Client/Audio/AnnouncerService.cs`, `AnnouncerCueCatalog.cs`) -- its own clips are 44.1 kHz
stereo Vorbis made with FFmpeg. Escape from Tarkov: Arena ships a much larger, unused-by-EFT set of
announcer/ambience/stinger audio of its own. This script extracts a curated subset of that Arena audio
read-only from the user's own installs, transcodes it to the same OGG Vorbis format ArenaMode already
consumes, and stages it into ArenaMapPort's own deployed plugin folder so ArenaMode can pick it up as an
OPTIONAL source of additional/alternate lines -- ArenaMapPort itself never plays any of it. See
docs/progress/arena-audio.md (the contract for that staged folder) and docs/ARENA-CONTENT-FOR-ARENAMODE.md
(written in parallel by the lead) for the full picture.

BSG-derived audio never goes into git (AGENTS.md): the only write targets are `work/` (gitignored,
this script's default `--output`) or `dist/` (gitignored, where the client csproj stages it for a
Release build). The only git-tracked file this script depends on is `arena_audio_catalog.json` --
category NAMES, a per-category include/exclude decision with a reason, and the in-game ArenaTagBank
event tag(s) each category belongs to (never audio).

Sources (every path and clip verified against a live read of the user's own installs, 2026-09-29):
  - commentator: Arena's EN commentator bank,
    `StreamingAssets/Windows/assets/content/audio/phrases/arena/arenaspeakertagbankmanagereng.bundle`
    (1640 AudioClip objects, all mono 44.1 kHz). Each clip's own `m_Name` is `<category>_<nn>` (a
    zero-padded index) except for the bank's one single-clip category, `eng_commentator_count_down`,
    which has no numeric suffix at all -- `category_of()` below handles both. The bank groups into
    exactly 69 categories, grouped in turn by 30 `ArenaTagBank` MonoBehaviours -- one per in-game EVENT
    tag (`StartGame`, `TeamLead`, `KillAllWin`, ...), each listing the AudioClips it fires for in its own
    `Clips` field (`read_tag_bank()` resolves those PPtrs back to categories; `arena_audio_catalog.json`
    records each category's tag(s) as `arena_tag` and cross-checks them every run,
    `validate_tag_bank()`). Classifying by clip-NAME prefix alone (an earlier version of this catalog)
    got two categories wrong: `eng_commentator_bg_overtime_start`'s real tag, `OvertimeStart`, is
    generic despite the `bg_` prefix (now included), and `eng_commentator_tm_sweep_team`'s real tag,
    `PurifiedTeamAppearance`, is an Arena-specific 'Purified' team spawn event, not a team sweep/wipe
    despite the category's own name (now excluded). `arena_audio_catalog.json` classifies every category
    as included (with a reason) or excluded (Blast-Gang-only content, AGENTS.md: ArenaMode targets
    TDM/FFA, not Blast Gang -- 14 categories referenced only by a `blast_gang`-mode tag; plus the one
    `arena_specific`-mode `tm_sweep_team` case above). 54 categories/1207 clips are included -- see
    docs/progress/arena-audio.md's tag-evidence table for the full tag -> categories -> cue mapping.
  - stingers: `arena_start`/`arena_win`/`arena_lose` -- EFT's OWN `resources.assets`
    (`D:/SPT41/EscapeFromTarkov_Data`), not Arena's. No Arena dependency for this one group.
  - crowd: `arena_fans_ambient_postmatch` (~30.3 s stereo), Arena's `resources.assets` -- for the
    post-match results screen (the clip's own name says `_postmatch`), not the live in-round spectator
    camera; see docs/progress/arena-audio.md's cue-mapping section.
  - spectator_glitch: the five `arena_cam_digital_glitch_0N` stingers plus the one
    `arena_cam_glitch_loop_02` loop -- all six live in Arena's `resources.assets` (no need for the
    separate `arenasoundeffectswrapper.bundle` the task brief also allowed; this table only needs one
    source file).
  - capture: `arena_point_captured_ally`/`_enemy`, Arena's
    `StreamingAssets/Windows/assets/content/audio/arena/soundeffects/arenasoundeffectstagbankmanager.bundle`.
  - round_signal: `asrena_start_signal_dry` (sic -- that misspelling is the source's own object name,
    kept verbatim), Arena's `StreamingAssets/Windows/assets/content/audio/phrases/arena_effects.bundle`.
  - result_music: `result_music_start`/`_loop`/`_end_winner`/`_end_looser`/`matchend_music`, Arena's
    `resources.assets` -- these really do carry odd, non-44.1 kHz native sample rates (23-38 kHz); this
    script resamples every group to 44100 Hz on the way out (ArenaMode's own convention), never just
    the odd ones.

Every non-commentator clip is selected by its own `m_Name` inside its source file, never by path_id
alone -- a `path_id` recorded in `GROUP_SOURCES` below is only a drift-detection hint (logged, not
fatal, if the live path_id differs from the one this script's own 2026-09-29 research recorded).

Encoding: `ffmpeg -c:a libvorbis -q:a 5 -ar 44100` (channel count passed through unchanged -- mono
commentator lines stay mono, stereo groups stay stereo), run through a thread pool since each encode is
an independent external process. Every encoded file is re-probed with `ffprobe` and rejected if its
duration drifts more than `DURATION_TOLERANCE_S` (60 ms) from the decoded source's own duration --
measured empirically well under 100 microseconds for libvorbis at this quality/sample rate across a
short (0.35 s), a long (30 s+) and a resampled-from-odd-rate clip this session, so 60 ms is a
comfortably wide, not a tight, tolerance -- or if its channel count or sample rate does not match. The
decoded source PCM itself is also rejected as silent if its RMS (in 16-bit PCM units) falls at or under
`RMS_SILENCE_THRESHOLD`; every measured real clip this session ran one to two orders of magnitude above
that floor, except one: `eng_commentator_eng_match_end_red_07` decodes to real (non-error) but
near-silent PCM (peak amplitude 11/32768) -- a flaw in Arena's own recording, not this pipeline. For the
six small named-clip groups (`extract_group`, every clip individually required by name, no redundancy)
any such failure still aborts the whole run with `HardExtractError`. For commentator clips
(`extract_commentator`), where every included category has many interchangeable variants (ArenaMode
picks a random one at runtime), a single flawed variant is instead SKIPPED with a logged warning and
recorded in the manifest's `groups.commentator.skipped_clips`; only a category left with ZERO surviving
clips is still a hard failure.

CLI:
    python extract_arena_audio.py                        # extract every group into work/extract/arena-audio
    python extract_arena_audio.py --only commentator      # extract just one group (repeatable flag)
    python extract_arena_audio.py --check                 # verify manifest.json + files exist; touches no source
    python extract_arena_audio.py --source <ArenaData> --eft-source <EFTData> --output <dir>
"""
from __future__ import annotations

import argparse
import array
import collections
import concurrent.futures
import io
import json
import math
import os
import re
import subprocess
import sys
import time
import wave
from datetime import datetime, timezone
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)

from common_ids import HardExtractError  # noqa: E402

SCHEMA_VERSION = 1
GENERATOR = "extract_arena_audio.py"

# Same default installs every other extractor in this pipeline uses -- both read-only.
DEFAULT_ARENA_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
DEFAULT_EFT_SOURCE = "D:/SPT41/EscapeFromTarkov_Data"

_REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = _REPO_ROOT / "work" / "extract" / "arena-audio"
DEFAULT_CATALOG_PATH = Path(__file__).resolve().parent / "arena_audio_catalog.json"

ENCODE_QUALITY = 5          # ffmpeg libvorbis -q:a (matches ArenaMode's own FFmpeg-made clips' quality band)
ENCODE_SAMPLE_RATE = 44100  # ArenaMode's own clips are 44.1 kHz; every source group is resampled to match
DURATION_TOLERANCE_S = 0.06  # see module docstring -- measured drift this session was < 0.001 s
RMS_SILENCE_THRESHOLD = 40.0  # 16-bit PCM units (full scale 32768); measured real clips: 118-6100
DEFAULT_WORKERS = 8

COMMENTATOR_GROUP = "commentator"

# group -> {root: "arena"|"eft", file: <path relative to that install's *_Data dir>,
#           clips: {clip_name: path_id_hint_or_None}}
# path_id hints are drift-detection only (logged, never fatal) -- selection is always by m_Name.
GROUP_SOURCES = {
    "stingers": {
        "root": "eft",
        "file": "resources.assets",
        "clips": {
            "arena_start": 3044,
            "arena_win": 2925,
            "arena_lose": 2930,
        },
    },
    "crowd": {
        "root": "arena",
        "file": "resources.assets",
        "clips": {
            "arena_fans_ambient_postmatch": 4985,
        },
    },
    "spectator_glitch": {
        "root": "arena",
        "file": "resources.assets",
        "clips": {
            "arena_cam_digital_glitch_01": 5218,
            "arena_cam_digital_glitch_02": 5146,
            "arena_cam_digital_glitch_03": 5128,
            "arena_cam_digital_glitch_04": 4959,
            "arena_cam_digital_glitch_05": 5038,
            "arena_cam_glitch_loop_02": 5094,
        },
    },
    "capture": {
        "root": "arena",
        "file": "StreamingAssets/Windows/assets/content/audio/arena/soundeffects/arenasoundeffectstagbankmanager.bundle",
        "clips": {
            "arena_point_captured_ally": 3090464798856874830,
            "arena_point_captured_enemy": -6444912900452477379,
        },
    },
    "round_signal": {
        "root": "arena",
        "file": "StreamingAssets/Windows/assets/content/audio/phrases/arena_effects.bundle",
        "clips": {
            "asrena_start_signal_dry": 6324952239254449295,
        },
    },
    "result_music": {
        "root": "arena",
        "file": "resources.assets",
        "clips": {
            "result_music_start": 5410,
            "result_music_loop": 4880,
            "result_music_end_winner": 5204,
            "result_music_end_looser": 5262,
            "matchend_music": 4780,
        },
    },
}

COMMENTATOR_SOURCE = {
    "root": "arena",
    "file": "StreamingAssets/Windows/assets/content/audio/phrases/arena/arenaspeakertagbankmanagereng.bundle",
}

ALL_GROUPS = sorted(GROUP_SOURCES) + [COMMENTATOR_GROUP]

# ArenaMode cue key -> ordered list of picks {group, category|clip, team?, note?}. Cue keys are the real
# ones ArenaMode.Client.Audio.AnnouncerCueCatalog/AnnouncerService.CalloutPriority actually key sound
# lookups by (D:/SPT41/Development/ArenaMode, read-only) -- a prior version of this dict invented
# double_kill/triple_kill/monster_kill/multi_kill/godlike/unstoppable/dominating/rampage/killing_spree
# entries that do not exist as ArenaMode.Client.Modes.ArenaAnnouncerCue values at all; those HUD kill-tier
# labels are real ArenaMode audio keys, but ArenaMode already owns its own per-tier ElevenLabs recordings
# for them (docs/ARENA-CONTENT-FOR-ARENAMODE.md: "a UT-style kill ladder ... Arena has [no] equivalent
# ladder", only the one generic eng_commentator_killing_streak line) -- pointing all nine at that one
# generic line would flatten ArenaMode's own ladder, so killing_streak stays catalogued but unmapped here
# (see docs/progress/arena-audio.md's "Deliberately not mapped" section). A "team" pick means the caller
# resolves which color to use at runtime (TeamDeathmatchArmbandPolicy: BEAR=red, USEC=blue); Arena's
# commentator always speaks about a team by colour, never "you". Mirrored in docs/progress/arena-audio.md
# -- keep both in sync by hand, this dict IS the source of the doc table.
SUGGESTED_ARENAMODE_CUES = {
    "round_start": [
        {"group": "commentator", "category": "eng_commentator_start_round",
         "note": "arena_tag StartRound"},
        {"group": "stingers", "clip": "arena_start",
         "note": "short native stinger; layer under/just before the spoken line, not instead of it"},
        {"group": "round_signal", "clip": "asrena_start_signal_dry",
         "note": "short native pre-roll signal; another optional layer alongside the stinger"},
    ],
    "prepare_to_fight": [
        {"group": "commentator", "category": "eng_commentator_count_down",
         "note": "arena_tag CountDown; closer semantic match for a 'get ready' countdown line"},
        {"group": "commentator", "category": "eng_commentator_start",
         "note": "arena_tag StartGame; alternate if CountDown reads too late in ArenaMode's own flow"},
    ],
    "first_blood": [
        {"group": "commentator", "category": "eng_commentator_first_blood", "note": "arena_tag FirstBlood"},
    ],
    "round_won": [
        {"group": "commentator", "category": "eng_commentator_eng_tm_round_end_red", "team": "red",
         "note": "arena_tag MatchWon -- Arena's own tag name for round-end audio, not a mismap"},
        {"group": "commentator", "category": "eng_commentator_eng_tm_round_end_blue", "team": "blue",
         "note": "arena_tag MatchWon -- Arena's own tag name for round-end audio, not a mismap"},
    ],
    "round_lost": [
        {"group": "commentator", "category": "eng_commentator_eng_tm_round_end_red", "team": "red",
         "note": "arena_tag MatchWon -- Arena's own tag name for round-end audio, not a mismap"},
        {"group": "commentator", "category": "eng_commentator_eng_tm_round_end_blue", "team": "blue",
         "note": "arena_tag MatchWon -- Arena's own tag name for round-end audio, not a mismap"},
    ],
    "match_won": [
        {"group": "commentator", "category": "eng_commentator_eng_match_end_red", "team": "red",
         "note": "arena_tag MatchEnd; TDM"},
        {"group": "commentator", "category": "eng_commentator_eng_match_end_blue", "team": "blue",
         "note": "arena_tag MatchEnd; TDM"},
        {"group": "commentator", "category": "eng_commentator_shootout_battle_end_victory_blue", "note": "arena_tag MatchEnd; FFA colour identity, future -- not used by today's TDM-only ArenaMode"},
        {"group": "commentator", "category": "eng_commentator_shootout_battle_end_victory_green", "note": "arena_tag MatchEnd; FFA colour identity, future -- not used by today's TDM-only ArenaMode"},
        {"group": "commentator", "category": "eng_commentator_shootout_battle_end_victory_pink", "note": "arena_tag MatchEnd; FFA colour identity, future -- not used by today's TDM-only ArenaMode"},
        {"group": "commentator", "category": "eng_commentator_shootout_battle_end_victory_red", "note": "arena_tag MatchEnd; FFA colour identity, future -- not used by today's TDM-only ArenaMode"},
        {"group": "commentator", "category": "eng_commentator_shootout_battle_end_victory_white", "note": "arena_tag MatchEnd; FFA colour identity, future -- not used by today's TDM-only ArenaMode"},
        {"group": "commentator", "category": "eng_commentator_shootout_battle_end_victory_yellow", "note": "arena_tag MatchEnd; FFA colour identity, future -- not used by today's TDM-only ArenaMode"},
        {"group": "stingers", "clip": "arena_win",
         "note": "EFT's own Arena victory stinger; layer as an SFX sting, never a replacement for the spoken line"},
    ],
    "match_lost": [
        {"group": "commentator", "category": "eng_commentator_eng_match_end_red", "team": "red",
         "note": "arena_tag MatchEnd; TDM"},
        {"group": "commentator", "category": "eng_commentator_eng_match_end_blue", "team": "blue",
         "note": "arena_tag MatchEnd; TDM"},
        {"group": "stingers", "clip": "arena_lose",
         "note": "layer as an SFX sting for the losing side's listener"},
    ],
    "draw": [
        {"group": "commentator", "category": "eng_round_draw",
         "note": "arena_tag MatchWon; round-level naming in Arena, closest available line for ArenaMode's "
                 "match-level Draw cue"},
        {"group": "commentator", "category": "eng_clash_draw",
         "note": "arena_tag MatchWon/MatchEnd; clash/tiebreak-level naming in Arena, alternate for "
                 "ArenaMode's Draw cue"},
    ],
    # "shutdown" is deliberately NOT mapped (docs/progress/arena-audio.md, "Deliberately not mapped"):
    # ArenaMode's Shutdown is a per-kill Award (ending one opponent's 5+ kill streak while the match
    # continues, AnnouncerService.CalloutPriority groups it with first_blood/monster_kill/multi_kill), not
    # a team-wipe/match-result line. eng_kill_all_win_red/bg_kill_all_win_blue (arena_tag KillAllWin,
    # generic despite the bg_ prefix) stay included for a possible future team-wipe announcement, just
    # not wired to this mismatched cue.
    "taken_the_lead": [
        {"group": "commentator", "category": "eng_commentator_red_team_lead_match_middle", "team": "red",
         "note": "arena_tag TeamLead; resolve to the team NOW in the lead, not the team the cue fired for"},
        {"group": "commentator", "category": "eng_commentator_blue_team_lead_match_middle", "team": "blue",
         "note": "arena_tag TeamLead; resolve to the team NOW in the lead, not the team the cue fired for"},
    ],
    "lost_the_lead": [
        {"group": "commentator", "category": "eng_commentator_red_team_lead_match_middle", "team": "red",
         "note": "arena_tag TeamLead; resolve to the team that just took the lead over you; mirrors taken_the_lead"},
        {"group": "commentator", "category": "eng_commentator_blue_team_lead_match_middle", "team": "blue",
         "note": "arena_tag TeamLead; resolve to the team that just took the lead over you; mirrors taken_the_lead"},
    ],
    "one_minute_remaining": [
        {"group": "commentator", "category": "eng_commentator_1_min_left", "note": "arena_tag CheckPointTimeExpired"},
    ],
    "final_round": [
        {"group": "commentator", "category": "eng_commentator_eng_final_battle", "note": "arena_tag NextRound"},
    ],
}

_CATEGORY_SUFFIX_RE = re.compile(r"^(.+)_(\d{1,3})$")


def _require(condition, message):
    if not condition:
        raise HardExtractError("ArenaAudio: " + message)


def category_of(clip_name: str) -> str:
    """Category = clip name minus a trailing `_<1-3 digit index>`, e.g. 'eng_commentator_start_01' ->
    'eng_commentator_start'. Unity-independent. The bank ships exactly one bare-name category with no
    numeric suffix at all ('eng_commentator_count_down', a single clip); such a name is its own
    category unchanged."""
    match = _CATEGORY_SUFFIX_RE.match(clip_name)
    return match.group(1) if match else clip_name


KNOWN_TAG_MODES = ("generic", "blast_gang", "arena_specific")


def load_catalog(path=None) -> dict:
    """Loads and integrity-checks arena_audio_catalog.json: unique category names, every entry has a
    bool 'included', a non-empty 'reason', a non-empty 'family' and a non-empty 'arena_tag' list (the
    in-game ArenaTagBank event tag(s) -- source: `read_tag_bank()`/`ArenaTagBank` MonoBehaviours in the
    bank itself, see docs/progress/arena-audio.md's tag-evidence table -- that reference this category; a
    category in two tags, e.g. `eng_clash_draw` in both `MatchWon` and `MatchEnd`, lists both). A
    top-level 'known_tags' dict must be present (every tag name -> {'mode': one of
    KNOWN_TAG_MODES, 'purpose': non-empty str}) and every category's 'arena_tag' entries must name a
    known tag. Beyond that, each category's 'included'/'family' must agree with its tag(s)' mode(s):
    a category referenced ONLY by 'blast_gang' tags must be excluded with family 'blast_gang_excluded'
    (AGENTS.md, ArenaMode targets TDM/FFA); a category referenced ONLY by 'arena_specific' tags must be
    excluded (any family, e.g. `eng_commentator_tm_sweep_team`'s real tag is `PurifiedTeamAppearance`, an
    Arena-specific mechanic, not a team-sweep line despite the category's own name); any category with at
    least one 'generic'-mode tag must be included (a generic tag is evidence the line is usable in
    TDM/FFA regardless of a `bg_`-prefixed sibling or the category's own naming, e.g.
    `eng_commentator_bg_kill_all_win_blue`/`_bg_overtime_start`, both tagged by a plain generic tag
    despite the `bg_` prefix). The 'family'/'reason' checks matter beyond schema hygiene:
    extract_commentator() unconditionally reads `reasons[category]["family"]`/`["arena_tag"]` for every
    included category when it builds the manifest, so a catalog edit that dropped either on an included
    entry would otherwise pass this loader silently and only surface as a raw KeyError deep into the
    ~60s real extraction run. This is the catalog's own internal self-consistency check (pure, no bank
    read); cross-checking it against what a LIVE bank read actually shows is `validate_tag_bank()`'s job,
    called separately from `extract_commentator()`. Unity-independent -- no source data touched."""
    path = Path(path) if path is not None else DEFAULT_CATALOG_PATH
    _require(path.is_file(), f"category catalog not found: {path}")
    catalog = json.loads(path.read_text(encoding="utf-8"))
    _require(catalog.get("schema_version") == 1, f"unsupported catalog schema_version in {path}")

    known_tags = catalog.get("known_tags")
    _require(isinstance(known_tags, dict) and known_tags, f"empty/invalid 'known_tags' dict in {path}")
    for tag_name, meta in known_tags.items():
        _require(isinstance(meta, dict), f"known_tags[{tag_name!r}] must be an object in {path}")
        _require(meta.get("mode") in KNOWN_TAG_MODES,
                  f"known_tags[{tag_name!r}] has an invalid/missing 'mode' in {path} "
                  f"(must be one of {KNOWN_TAG_MODES})")
        _require(isinstance(meta.get("purpose"), str) and meta["purpose"],
                  f"known_tags[{tag_name!r}] missing a non-empty 'purpose' in {path}")

    categories = catalog.get("categories")
    _require(isinstance(categories, list) and categories, f"empty/invalid 'categories' list in {path}")
    seen = set()
    for entry in categories:
        name = entry.get("category")
        _require(isinstance(name, str) and name, f"catalog entry missing a 'category' name: {entry}")
        _require(name not in seen, f"duplicate category in catalog: {name!r}")
        seen.add(name)
        _require(isinstance(entry.get("included"), bool), f"catalog entry {name!r} missing a bool 'included'")
        _require(isinstance(entry.get("reason"), str) and entry["reason"],
                  f"catalog entry {name!r} missing a non-empty 'reason'")
        _require(isinstance(entry.get("family"), str) and entry["family"],
                  f"catalog entry {name!r} missing a non-empty 'family'")
        arena_tag = entry.get("arena_tag")
        _require(isinstance(arena_tag, list) and arena_tag,
                  f"catalog entry {name!r} missing a non-empty 'arena_tag' list")
        unknown_tags = [t for t in arena_tag if t not in known_tags]
        _require(not unknown_tags,
                  f"catalog entry {name!r} 'arena_tag' names unknown tag(s) {unknown_tags} "
                  f"(add them to 'known_tags' first)")
        modes = {known_tags[t]["mode"] for t in arena_tag}
        # Checked in this order so the rule matches the docstring exactly: a generic tag alone justifies
        # inclusion, REGARDLESS of what else is mixed in (a category could in principle carry a generic
        # tag alongside a blast_gang or arena_specific one too). Only once "generic" is ruled out do we
        # look at blast_gang/arena_specific -- and that covers every remaining combination, including one
        # neither branch below names explicitly: blast_gang + arena_specific with no generic tag at all
        # (review finding 2026-09-29: the previous `if .. == {"blast_gang"} / elif .. == {"arena_specific"}
        # / else: require included` fell through that exact combination into the "else" arm and wrongly
        # forced included=True there -- unreachable with today's 30 known_tags, since none combine those
        # two modes without a generic tag, but the check now matches its own documented rule for any
        # future tag combination too, not just by the absence of that combination in the data today).
        if "generic" in modes:
            _require(entry["included"] is True,
                      f"catalog entry {name!r} is referenced by a generic tag {arena_tag} "
                      f"and must have included=true")
        elif modes == {"blast_gang"}:
            _require(entry["included"] is False and entry["family"] == "blast_gang_excluded",
                      f"catalog entry {name!r} is referenced only by Blast Gang tag(s) {arena_tag} "
                      f"and must have included=false, family='blast_gang_excluded'")
        else:
            # modes is {"arena_specific"} or {"blast_gang", "arena_specific"} -- no generic tag anywhere.
            _require(entry["included"] is False,
                      f"catalog entry {name!r} is referenced only by non-generic tag(s) {arena_tag} "
                      f"(mode(s) {sorted(modes)}) and must have included=false")
    return catalog


def classify_categories(discovered, catalog):
    """discovered: iterable of category names actually found in a live bank read. Returns
    (included_names, excluded_names), each intersected with `discovered` (so callers only see
    categories that really exist right now). Raises HardExtractError if the bank contains a category
    the catalog does not know (source changed, catalog needs updating) or if an catalog-included
    category is missing from the bank (source changed, an expected line disappeared)."""
    known = {c["category"]: c for c in catalog["categories"]}
    discovered = set(discovered)
    unknown = discovered - set(known)
    _require(not unknown,
              "unknown categor" + ("y" if len(unknown) == 1 else "ies") +
              f" in the bank, arena_audio_catalog.json needs updating: {sorted(unknown)}")
    included_known = {name for name, c in known.items() if c["included"]}
    missing = included_known - discovered
    _require(not missing,
              "expected included categor" + ("y" if len(missing) == 1 else "ies") +
              f" missing from the bank: {sorted(missing)}")
    excluded_known = set(known) - included_known
    return included_known & discovered, excluded_known & discovered


def _walk_pptr_categories(node, clip_categories):
    """Yields the category of every AudioClip PPtr (`{'m_PathID': ..., ...}`) found anywhere under
    `node`, via `clip_categories` ({path_id: category}). Pure recursive dict/list walk, no UnityPy calls
    -- reusable on a real typetree dict (from `read_tag_bank()`) or on hand-built fake data in tests."""
    if isinstance(node, dict):
        path_id = node.get("m_PathID")
        if path_id is not None and path_id in clip_categories:
            yield clip_categories[path_id]
        for value in node.values():
            yield from _walk_pptr_categories(value, clip_categories)
    elif isinstance(node, list):
        for value in node:
            yield from _walk_pptr_categories(value, clip_categories)


def read_tag_bank(env) -> dict:
    """Reads every ArenaTagBank MonoBehaviour in a loaded commentator bank environment (e.g.
    `StartGame`, `TeamLead`, `KillAllWin`...) and returns {tag_name: set(category_name)} -- each tag's
    own `Clips` PPtr list resolved to the categories of the AudioClips it references (`_index_clip_names`
    groups clips by exact name, category grouping here is separate: many tags reference every clip in a
    category, e.g. `TeamLead` references all 23 `..._red_team_lead_match_middle` variants, which all
    collapse to the one category). Skips the bank's one non-tag MonoBehaviour, the manager
    (`ArenaSpeakerTagBankManagerEng`, field `Banks`, no `Clips` field at all -- distinguishing tag
    MonoBehaviours from the manager by the presence of a `Clips` key is more robust than matching
    `m_Name` against an expected manager name, which could rename). Unity-dependent (needs a live UnityPy
    environment); the pure comparison logic that consumes this dict's output lives in
    `validate_tag_bank()`, exercised by tests with fake `{tag: {category, ...}}` data instead of a live
    read."""
    clip_categories = {}
    for obj in env.objects:
        if obj.type.name != "AudioClip":
            continue
        tree = obj.read_typetree()
        clip_categories[obj.path_id] = category_of(tree["m_Name"])

    tag_categories = {}
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        if "Clips" not in tree:
            continue  # the bank manager (field 'Banks'), not a tag
        name = tree.get("m_Name")
        _require(isinstance(name, str) and name, f"tag MonoBehaviour (path_id {obj.path_id}) has no m_Name")
        _require(name not in tag_categories, f"duplicate tag MonoBehaviour name in bank: {name!r}")
        tag_categories[name] = set(_walk_pptr_categories(tree, clip_categories))
    return tag_categories


def invert_tag_category_map(tag_categories: dict) -> dict:
    """{tag: {category, ...}} -> {category: sorted [tag, ...]}. A category can appear under more than
    one tag (e.g. `eng_clash_draw` under both `MatchWon` and `MatchEnd`). Pure, Unity-independent."""
    by_category = collections.defaultdict(set)
    for tag, categories in tag_categories.items():
        for category in categories:
            by_category[category].add(tag)
    return {category: sorted(tags) for category, tags in by_category.items()}


def validate_tag_bank(tag_categories: dict, catalog: dict):
    """Cross-checks a (live or fake) bank tag read against `catalog` (a `load_catalog()`-validated
    dict). `tag_categories`: {tag_name: set(category_name)}, as `read_tag_bank()` returns (or as a test
    builds by hand). Raises HardExtractError if:
      1. the bank has a tag `catalog['known_tags']` does not know (Arena added/renamed a tag; the
         catalog needs a new `known_tags` entry, with a mode/purpose note, before it can be trusted) --
         'unknown tag' in the task brief;
      2. a known tag's `Clips` resolve to a category `catalog['categories']` does not know at all --
         'unknown category in a tag'; caught here, distinctly from `classify_categories()`'s own,
         differently-worded 'unknown category' check (which compares the bank's *discovered clip-name*
         categories against the catalog, not tag references) so the two failure modes are never
         conflated;
      3. a known catalog category's own `arena_tag` field does not exactly match the tag(s) the live
         bank actually shows for it -- 'disagreeing tag'; catches both directions of drift, a catalog
         claiming a tag the bank no longer shows and a bank tag the catalog does not record.
    Pure comparison logic -- no I/O, no UnityPy -- so every one of these three failure modes is exercised
    directly with fake `tag_categories`/`catalog` data in tests, never only through a live bank read."""
    known_tags = catalog["known_tags"]
    unknown_tags = sorted(set(tag_categories) - set(known_tags))
    _require(not unknown_tags,
              "unknown tag" + ("" if len(unknown_tags) == 1 else "s") +
              f" in the bank, arena_audio_catalog.json's known_tags needs updating: {unknown_tags}")

    known_categories = {c["category"] for c in catalog["categories"]}
    by_category = invert_tag_category_map(tag_categories)
    unknown_categories = sorted(set(by_category) - known_categories)
    _require(not unknown_categories,
              "tag(s) in the bank reference categor" +
              ("y" if len(unknown_categories) == 1 else "ies") +
              f" arena_audio_catalog.json does not know: {unknown_categories}")

    catalog_arena_tag = {c["category"]: sorted(c["arena_tag"]) for c in catalog["categories"]}
    mismatches = []
    for category in sorted(set(catalog_arena_tag) | set(by_category)):
        expected = catalog_arena_tag.get(category, [])
        actual = by_category.get(category, [])
        if expected != actual:
            mismatches.append(f"{category}: bank shows {actual}, catalog says {expected}")
    _require(not mismatches,
              f"arena_tag disagrees with the bank for {len(mismatches)} "
              "categor" + ("y" if len(mismatches) == 1 else "ies") + ": " + "; ".join(mismatches))


def clip_output_path(output_dir, group: str, clip_name: str, category: str = None) -> Path:
    """<output_dir>/<group>/<clip_name>.ogg, or for commentator clips
    <output_dir>/commentator/<category>/<clip_name>.ogg. Unity-independent."""
    if group == COMMENTATOR_GROUP:
        _require(category, f"commentator clip {clip_name!r} needs a category for its output path")
        return Path(output_dir) / COMMENTATOR_GROUP / category / f"{clip_name}.ogg"
    _require(group in GROUP_SOURCES, f"unknown group {group!r} (known: {ALL_GROUPS})")
    return Path(output_dir) / group / f"{clip_name}.ogg"


def build_ffmpeg_command(dest_path, quality=ENCODE_QUALITY, sample_rate=ENCODE_SAMPLE_RATE):
    """WAV bytes arrive on stdin (pipe:0); ffmpeg writes straight to dest_path. Channel count is
    intentionally not forced -- ffmpeg passes the input's own channel count through unchanged."""
    return ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-f", "wav", "-i", "pipe:0",
            "-c:a", "libvorbis", "-q:a", str(quality), "-ar", str(sample_rate), str(dest_path)]


def build_ffprobe_command(path):
    return ["ffprobe", "-v", "error", "-print_format", "json",
            "-show_entries", "format=duration:stream=channels,sample_rate", str(path)]


def wav_rms(wav_bytes: bytes) -> float:
    """RMS of the decoded 16-bit PCM, in int16 units (full scale 32768). Raises if the WAV is not
    16-bit PCM (every known source in this pipeline is; a different bit depth means the FSB5 decode
    changed shape and needs a fresh look, not a silently wrong RMS)."""
    with wave.open(io.BytesIO(wav_bytes)) as w:
        _require(w.getsampwidth() == 2, f"expected 16-bit PCM, got {w.getsampwidth() * 8}-bit")
        raw = w.readframes(w.getnframes())
    if not raw:
        return 0.0
    samples = array.array("h")
    samples.frombytes(raw[: len(raw) - (len(raw) % 2)])
    if not samples:
        return 0.0
    total = sum(s * s for s in samples)
    return math.sqrt(total / len(samples))


def _root_dir(root: str, arena_source, eft_source) -> Path:
    if root == "arena":
        return Path(arena_source)
    if root == "eft":
        return Path(eft_source)
    raise HardExtractError(f"ArenaAudio: unknown source root {root!r} (known: arena, eft)")


def _load_environment(root_dir, relative_file: str):
    """Loads one source file (a .bundle or an .assets file) read-only with UnityPy. An .assets file is
    loaded alongside globalgamemanagers.assets when present, matching every other extractor in this
    pipeline (extract_banners.py/extract_loadscreens.py); a .bundle is self-contained and loaded alone."""
    import UnityPy

    root_dir = Path(root_dir)
    path = root_dir / relative_file
    _require(path.is_file(), f"source file not found: {path} (is the install path right?)")
    if relative_file.endswith(".bundle"):
        return UnityPy.load(str(path))
    gm = root_dir / "globalgamemanagers.assets"
    if gm.is_file() and gm != path:
        return UnityPy.load(str(path), str(gm))
    return UnityPy.load(str(path))


def _index_clip_names(env, wanted_names=None):
    """Builds {m_Name: AudioClip object reader} for the loaded environment. Every object in the file is
    type-checked first (cheap: `obj.type.name`, no parsing) to skip non-AudioClip entries outright; each
    remaining AudioClip's own `m_Name` is then read via `read_typetree()` to decide membership -- that
    per-clip name read is not skipped even when `wanted_names` narrows the small named-clip groups down
    to a handful of clips inside a much bigger .assets file, since UnityPy still has to parse each
    AudioClip's own typetree to learn its name before this function can compare it against `wanted`. What
    `wanted_names` buys is a smaller *result* dict and no chance of an unrelated same-named clip
    elsewhere in that huge file tripping the duplicate-name guard below, not a lighter read for the
    AudioClips it discards. The commentator bank is purpose-built and self-contained, so it is indexed in
    full (wanted_names=None). Raises on a duplicate name within the indexed set -- name-based lookup
    would otherwise be ambiguous."""
    wanted = set(wanted_names) if wanted_names is not None else None
    index = {}
    for obj in env.objects:
        if obj.type.name != "AudioClip":
            continue
        name = obj.read_typetree()["m_Name"]
        if wanted is not None and name not in wanted:
            continue
        if name in index:
            raise HardExtractError(
                f"ArenaAudio: duplicate AudioClip name {name!r} in source "
                f"(path_ids {index[name].path_id} and {obj.path_id})")
        index[name] = obj
    return index


def _decode_and_validate_source(reader, clip_name: str):
    """Fully decodes one AudioClip (FSB5, via UnityPy's own decoder) and cross-checks the decoded WAV
    against the object's own typetree metadata and an RMS silence floor. Returns (wav_bytes, meta)
    where meta = {channels, sample_rate, duration_s, rms}."""
    tree = reader.read_typetree()
    data = reader.read()
    _require(data.m_Name == clip_name,
              f"{clip_name}: object m_Name drifted to {data.m_Name!r} between the indexing and decode passes")
    samples = data.samples
    _require(len(samples) == 1, f"{clip_name}: expected exactly one decoded clip, got {len(samples)}")
    _, wav_bytes = next(iter(samples.items()))
    with wave.open(io.BytesIO(wav_bytes)) as w:
        _require(w.getsampwidth() == 2, f"{clip_name}: expected 16-bit decoded PCM, got {w.getsampwidth() * 8}-bit")
        channels, freq, frames = w.getnchannels(), w.getframerate(), w.getnframes()
    duration = frames / freq if freq else 0.0
    _require(channels == tree["m_Channels"] and freq == tree["m_Frequency"],
              f"{clip_name}: decoded format {channels}ch/{freq}Hz does not match source metadata "
              f"{tree['m_Channels']}ch/{tree['m_Frequency']}Hz")
    _require(abs(duration - tree["m_Length"]) <= max(0.05, 2 / freq),
              f"{clip_name}: decoded duration {duration:.3f}s drifted from source metadata "
              f"{tree['m_Length']:.3f}s")
    rms = wav_rms(wav_bytes)
    _require(rms > RMS_SILENCE_THRESHOLD,
              f"{clip_name}: decoded PCM looks silent (rms={rms:.1f}, threshold={RMS_SILENCE_THRESHOLD})")
    return wav_bytes, {"channels": channels, "sample_rate": freq, "duration_s": round(duration, 3),
                       "rms": round(rms, 1)}


def _run_ffmpeg(wav_bytes: bytes, dest_path, quality, sample_rate):
    dest_path = Path(dest_path)
    dest_path.parent.mkdir(parents=True, exist_ok=True)
    proc = subprocess.run(build_ffmpeg_command(dest_path, quality, sample_rate),
                          input=wav_bytes, capture_output=True)
    if proc.returncode != 0 or not dest_path.is_file() or dest_path.stat().st_size == 0:
        raise HardExtractError(
            f"ArenaAudio: ffmpeg failed for {dest_path.name}: "
            f"{proc.stderr.decode(errors='replace').strip()}")


def _probe_output(path):
    proc = subprocess.run(build_ffprobe_command(path), capture_output=True, text=True)
    if proc.returncode != 0:
        raise HardExtractError(f"ArenaAudio: ffprobe failed for {path}: {proc.stderr.strip()}")
    try:
        data = json.loads(proc.stdout)
        stream = next(s for s in data["streams"] if "channels" in s)
        return {"duration_s": float(data["format"]["duration"]),
                "channels": int(stream["channels"]), "sample_rate": int(stream["sample_rate"])}
    except (KeyError, StopIteration, ValueError, json.JSONDecodeError) as exc:
        raise HardExtractError(f"ArenaAudio: unreadable ffprobe output for {path}: {exc}") from exc


def _encode_and_validate(wav_bytes, dest_path, source_meta, quality, sample_rate,
                          tolerance_s=DURATION_TOLERANCE_S):
    _run_ffmpeg(wav_bytes, dest_path, quality, sample_rate)
    probed = _probe_output(dest_path)
    drift = abs(probed["duration_s"] - source_meta["duration_s"])
    _require(drift <= tolerance_s,
              f"{Path(dest_path).name}: encoded duration {probed['duration_s']:.3f}s drifted "
              f"{drift * 1000:.1f}ms from source {source_meta['duration_s']:.3f}s "
              f"(tolerance {tolerance_s * 1000:.0f}ms)")
    _require(probed["channels"] == source_meta["channels"],
              f"{Path(dest_path).name}: encoded channel count {probed['channels']} != "
              f"source {source_meta['channels']}")
    _require(probed["sample_rate"] == sample_rate,
              f"{Path(dest_path).name}: encoded sample rate {probed['sample_rate']} != requested {sample_rate}")
    return probed


def _run_jobs(jobs, quality, sample_rate, workers):
    """jobs: list of dicts with at least {name, wav, meta, dest, output_dir}. Runs encode+validate for
    every job in a thread pool (each is an independent ffmpeg/ffprobe subprocess, so this is I/O bound,
    not GIL bound). Returns (name -> manifest clip entry, [(job, error_message), ...])."""
    results = {}
    failures = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, workers)) as pool:
        future_to_job = {
            pool.submit(_encode_and_validate, job["wav"], job["dest"], job["meta"], quality, sample_rate): job
            for job in jobs
        }
        for future in concurrent.futures.as_completed(future_to_job):
            job = future_to_job[future]
            try:
                probed = future.result()
            except HardExtractError as exc:
                failures.append((job, str(exc)))
                continue
            entry = {
                "name": job["name"],
                "file": Path(job["dest"]).relative_to(job["output_dir"]).as_posix(),
                "duration_s": round(probed["duration_s"], 3),
                "channels": probed["channels"],
                "sample_rate": probed["sample_rate"],
                "source_duration_s": job["meta"]["duration_s"],
                "source_rms": job["meta"]["rms"],
            }
            if job.get("path_id_hint") is not None:
                entry["path_id_hint"] = job["path_id_hint"]
                entry["path_id_matched"] = job.get("path_id_actual") == job["path_id_hint"]
            results[job["name"]] = entry
    return results, failures


def _source_descriptor(root: str, relative_file: str, root_dir, unity_version=None) -> dict:
    path = Path(root_dir) / relative_file
    stat = path.stat()
    return {
        "root": root,
        "file": relative_file,
        "size": stat.st_size,
        "mtime": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc).isoformat(),
        "unity_version": unity_version,
    }


def extract_group(group_name, arena_source, eft_source, output_dir,
                  quality=ENCODE_QUALITY, sample_rate=ENCODE_SAMPLE_RATE, workers=DEFAULT_WORKERS):
    """Extracts every clip in one of GROUP_SOURCES' non-commentator groups. Returns (clips, source)."""
    _require(group_name in GROUP_SOURCES, f"unknown group {group_name!r} (known: {ALL_GROUPS})")
    spec = GROUP_SOURCES[group_name]
    root_dir = _root_dir(spec["root"], arena_source, eft_source)
    env = _load_environment(root_dir, spec["file"])
    index = _index_clip_names(env, wanted_names=spec["clips"].keys())

    output_dir = Path(output_dir)
    jobs = []
    for clip_name, path_id_hint in spec["clips"].items():
        reader = index.get(clip_name)
        _require(reader is not None, f"{group_name}: expected clip {clip_name!r} not found in {spec['file']}")
        if path_id_hint is not None and reader.path_id != path_id_hint:
            print(f"  [warn] {group_name}/{clip_name}: path_id drifted from the recorded hint "
                  f"{path_id_hint} to {reader.path_id} (still matched by name)")
        wav_bytes, meta = _decode_and_validate_source(reader, clip_name)
        jobs.append({
            "name": clip_name, "wav": wav_bytes, "meta": meta, "output_dir": output_dir,
            "dest": clip_output_path(output_dir, group_name, clip_name),
            "path_id_hint": path_id_hint, "path_id_actual": reader.path_id,
        })

    results, failures = _run_jobs(jobs, quality, sample_rate, workers)
    _require(not failures, f"{group_name}: {len(failures)} clip(s) failed: " +
              "; ".join(f"{job['name']}: {err}" for job, err in failures))

    unity_version = next(iter(index.values())).assets_file.unity_version if index else None
    source = _source_descriptor(spec["root"], spec["file"], root_dir, unity_version)
    clips = [results[name] for name in spec["clips"]]
    print(f"[{group_name}] {len(clips)}/{len(spec['clips'])} clips ok")
    return clips, source


def extract_commentator(arena_source, output_dir, catalog,
                        quality=ENCODE_QUALITY, sample_rate=ENCODE_SAMPLE_RATE, workers=DEFAULT_WORKERS):
    """Extracts every clip in every catalog-included commentator category, after cross-checking the
    catalog's per-category `arena_tag` field against the bank's own ArenaTagBank MonoBehaviours
    (`read_tag_bank()`/`validate_tag_bank()`). Returns (categories, excluded_categories, source,
    skipped_clips)."""
    root_dir = _root_dir(COMMENTATOR_SOURCE["root"], arena_source, None)
    env = _load_environment(root_dir, COMMENTATOR_SOURCE["file"])
    index = _index_clip_names(env)  # purpose-built, self-contained bank: index every clip
    print(f"[commentator] scanning bank: {len(index)} clips")

    by_category = collections.defaultdict(list)
    for name in index:
        by_category[category_of(name)].append(name)
    included, excluded = classify_categories(by_category.keys(), catalog)
    print(f"[commentator] {len(by_category)} categories: {len(included)} included, {len(excluded)} excluded")

    # Cross-checks every category's catalogued arena_tag against the bank's own ArenaTagBank
    # MonoBehaviours (see docs/progress/arena-audio.md's tag-evidence table) -- a category grouping is
    # otherwise only ever inferred from clip-name prefixes (category_of() above), which is exactly what
    # produced the earlier bg_/eng_ prefix misclassifications this catalog now corrects.
    tag_categories = read_tag_bank(env)
    validate_tag_bank(tag_categories, catalog)

    # Every included category has many variant clips (ArenaMode picks a random one at runtime -- see
    # docs/progress/arena-audio.md), so one flawed source variant must not abort the other ~1260. A
    # decode-time failure (e.g. the RMS silence guard) here is caught per clip and the clip is
    # SKIPPED, not fatal, unless it would leave a whole category with zero surviving clips -- that
    # would make an included line family entirely unplayable, which stays a hard failure. The six
    # small named-clip groups (extract_group) have no such redundancy, so a failure there still aborts
    # the whole run.
    output_dir = Path(output_dir)
    jobs = []
    decode_failures = []
    for category in sorted(included):
        for clip_name in sorted(by_category[category]):
            reader = index[clip_name]
            try:
                wav_bytes, meta = _decode_and_validate_source(reader, clip_name)
            except HardExtractError as exc:
                decode_failures.append((clip_name, category, str(exc)))
                continue
            jobs.append({
                "name": clip_name, "category": category, "wav": wav_bytes, "meta": meta,
                "output_dir": output_dir,
                "dest": clip_output_path(output_dir, COMMENTATOR_GROUP, clip_name, category),
                "path_id_hint": None, "path_id_actual": reader.path_id,
            })

    results, encode_failures = _run_jobs(jobs, quality, sample_rate, workers)
    skipped = [{"name": name, "category": category, "reason": err} for name, category, err in decode_failures]
    skipped += [{"name": job["name"], "category": job["category"], "reason": err} for job, err in encode_failures]
    for entry in skipped:
        print(f"  [warn] commentator/{entry['category']}/{entry['name']}: skipped ({entry['reason']})")

    reasons = {c["category"]: c for c in catalog["categories"]}
    categories_out = {}
    empty_categories = []
    for category in sorted(included):
        clip_names = [name for name in sorted(by_category[category]) if name in results]
        if not clip_names:
            empty_categories.append(category)
            continue
        categories_out[category] = {
            "family": reasons[category]["family"], "reason": reasons[category]["reason"],
            "arena_tag": reasons[category]["arena_tag"], "clips": [results[name] for name in clip_names],
        }
    _require(not empty_categories,
              "commentator categor" + ("y" if len(empty_categories) == 1 else "ies") +
              f" left with zero surviving clips after validation failures: {sorted(empty_categories)}")
    excluded_out = {category: {"reason": reasons[category]["reason"], "arena_tag": reasons[category]["arena_tag"]}
                    for category in sorted(excluded)}

    print(f"[commentator] {len(results)}/{len(results) + len(skipped)} clips ok"
         + (f", {len(skipped)} skipped" if skipped else ""))
    unity_version = next(iter(index.values())).assets_file.unity_version if index else None
    source = _source_descriptor(COMMENTATOR_SOURCE["root"], COMMENTATOR_SOURCE["file"], root_dir, unity_version)
    return categories_out, excluded_out, source, skipped


def build_manifest(groups: dict, commentator: dict, excluded_commentator: dict, sources: list,
                   generated_at: str = None, skipped_commentator_clips: list = None) -> dict:
    """Pure assembly of the manifest.json structure -- no I/O, easy to unit test against fake data."""
    all_groups = dict(groups)
    all_groups[COMMENTATOR_GROUP] = {
        "categories": commentator, "excluded_categories": excluded_commentator,
        "skipped_clips": skipped_commentator_clips or [],
    }
    return {
        "schema_version": SCHEMA_VERSION,
        "generator": GENERATOR,
        "generated_at": generated_at or datetime.now(timezone.utc).isoformat(),
        "sources": sources,
        "groups": all_groups,
        "suggested_arenamode_cues": SUGGESTED_ARENAMODE_CUES,
    }


def extract_all(arena_source=DEFAULT_ARENA_SOURCE, eft_source=DEFAULT_EFT_SOURCE, output_dir=DEFAULT_OUTPUT,
                only=None, quality=ENCODE_QUALITY, sample_rate=ENCODE_SAMPLE_RATE, workers=DEFAULT_WORKERS,
                catalog_path=None) -> dict:
    """Extracts the requested groups (default: every group) and writes manifest.json, atomically and
    idempotently. A partial run (`only`) merges into whatever manifest.json already exists on disk so a
    debug re-run of one group never discards the others' entries."""
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = output_dir / "manifest.json"

    existing = {}
    if manifest_path.is_file():
        try:
            existing = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            existing = {}

    requested = set(only) if only else set(ALL_GROUPS)
    unknown = requested - set(ALL_GROUPS)
    _require(not unknown, f"unknown --only group(s): {sorted(unknown)} (known: {ALL_GROUPS})")

    existing_groups = existing.get("groups", {})
    groups = {name: data for name, data in existing_groups.items() if name != COMMENTATOR_GROUP}
    commentator = existing_groups.get(COMMENTATOR_GROUP, {}).get("categories", {})
    excluded_commentator = existing_groups.get(COMMENTATOR_GROUP, {}).get("excluded_categories", {})
    skipped_commentator = existing_groups.get(COMMENTATOR_GROUP, {}).get("skipped_clips", [])
    sources = {f"{s['root']}:{s['file']}": s for s in existing.get("sources", [])}

    for group_name in sorted(GROUP_SOURCES):
        if group_name not in requested:
            continue
        clips, source = extract_group(group_name, arena_source, eft_source, output_dir, quality, sample_rate, workers)
        groups[group_name] = {"clips": clips}
        sources[f"{source['root']}:{source['file']}"] = source

    if COMMENTATOR_GROUP in requested:
        catalog = load_catalog(catalog_path)
        commentator, excluded_commentator, source, skipped_commentator = extract_commentator(
            arena_source, output_dir, catalog, quality, sample_rate, workers)
        sources[f"{source['root']}:{source['file']}"] = source

    manifest = build_manifest(groups, commentator, excluded_commentator,
                              sorted(sources.values(), key=lambda s: (s["root"], s["file"])),
                              skipped_commentator_clips=skipped_commentator)
    payload = json.dumps(manifest, indent=2, sort_keys=False).encode("utf-8") + b"\n"
    tmp = manifest_path.with_suffix(".json.tmp")
    tmp.write_bytes(payload)
    tmp.replace(manifest_path)
    return manifest


def _iter_manifest_clip_lists(group_data: dict):
    if "clips" in group_data:
        yield group_data["clips"]
    for category_data in group_data.get("categories", {}).values():
        yield category_data.get("clips", [])


def check(output_dir=DEFAULT_OUTPUT) -> dict:
    """--check: confirms manifest.json exists and every file it references exists and is non-empty.
    Touches no source install and does not import UnityPy. Returns {} on success, else
    {problem_key: detail}."""
    output_dir = Path(output_dir)
    manifest_path = output_dir / "manifest.json"
    if not manifest_path.is_file():
        return {"manifest.json": str(manifest_path)}
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return {"manifest.json": f"{manifest_path} (unreadable: {exc})"}

    missing = {}
    for group_name, group_data in manifest.get("groups", {}).items():
        for clips in _iter_manifest_clip_lists(group_data):
            for clip in clips:
                rel = clip.get("file")
                if not rel:
                    missing[f"{group_name}:{clip.get('name', '?')}"] = "manifest entry missing 'file'"
                    continue
                path = output_dir / rel
                if not path.is_file() or path.stat().st_size == 0:
                    missing[rel] = str(path)
    return missing


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", default=DEFAULT_ARENA_SOURCE,
                        help="Arena's EscapeFromTarkovArena_Data directory (read-only)")
    parser.add_argument("--eft-source", default=DEFAULT_EFT_SOURCE,
                        help="EFT's EscapeFromTarkov_Data directory (read-only)")
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT),
                        help="directory to write <group>/<clip>.ogg (+ manifest.json) into")
    parser.add_argument("--only", action="append", choices=ALL_GROUPS,
                        help="extract only this group (repeatable); default: every group")
    parser.add_argument("--check", action="store_true",
                         help="verify manifest.json and referenced files exist/are non-empty; touches no source")
    parser.add_argument("--quality", type=int, default=ENCODE_QUALITY, help="ffmpeg libvorbis -q:a (default 5)")
    parser.add_argument("--workers", type=int, default=DEFAULT_WORKERS,
                        help="thread pool size for ffmpeg encode+validate (default 8)")
    parser.add_argument("--catalog", default=None, help="override path to arena_audio_catalog.json")
    args = parser.parse_args(argv)

    if args.check:
        missing = check(args.output)
        if missing:
            for key, detail in sorted(missing.items()):
                print(f"MISSING: {key} -> {detail}")
            return 1
        print(f"OK: manifest and referenced files present under {args.output}")
        return 0

    start = time.time()
    try:
        manifest = extract_all(args.source, args.eft_source, args.output, only=args.only,
                               quality=args.quality, workers=args.workers, catalog_path=args.catalog)
    except HardExtractError as e:
        print(f"FAILED: {e}", file=sys.stderr)
        return 1

    elapsed = time.time() - start
    output_dir = Path(args.output)
    total_clips = 0
    total_bytes = 0
    for group_data in manifest["groups"].values():
        for clips in _iter_manifest_clip_lists(group_data):
            for clip in clips:
                total_clips += 1
                total_bytes += (output_dir / clip["file"]).stat().st_size
    print(f"OK: {total_clips} clip(s), {total_bytes / 1024 / 1024:.2f} MiB, {elapsed:.1f}s wall -> {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
