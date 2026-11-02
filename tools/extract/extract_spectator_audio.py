"""Export the shared crowd audio of fan_speaker_voice.bundle: the 13 embedded Participating fan calls, the crowd murmur bed and, since
2026-11-02 (phase A of docs/PLAN-CROWD-REACTIONS.md), the action-bound reaction clips. All of it is shared by every restored crowd.

Three kinds of clip, each with its own record in the manifest:
  calls     the 13 embedded clips of the `Participating` bank (trigger 1123), the random calls (5 s cap). The nine external Scav references
            of that bank are omitted.
  bed       Arena's `arena_fans_ambient_neutral` (since 2026-11-02, user decision; until then the results-screen bed
            `arena_fans_ambient_postmatch` from resources.assets), the 80 s stereo bed of the `Neutral` bank (trigger 1122) of
            `ambient_voice.bundle`, which Arena plays from the inactive `FansAmbient` emitters of its sound scenes
            (docs/progress/spectator-audio.md "Crowd bed" and "Neutral bed"). It is downmixed to mono, brought to the level of the approved
            postmatch bed and made loop-safe offline by an equal-power crossfade of the head into the tail.
  reactions the embedded clips of six more banks of the same bundle, Cheer (1119), Owned (1120), Angry (1121), ChangedRound (1125),
            EndRound (1126) and Hide (1124) (REACTION_BANKS), as far as the tracked sidecar tools/extract/reaction_pools.json lets them
            through: only the clips whose status is in its `exported_status` (`keep`; `listen` clips wait for the author's ear, `out`
            clips never ship). The sidecar also gives each clip its pool tag and sentence group, which travel into the manifest's
            `reactions` object. The same normalisation as the calls, a 7 s cap, 70 clips in phase A. The external Scav references of
            these banks (68 slots) are omitted, as are the banks that have no kind yet (LowDeathIntensity, PurifiedTeam, Neutral).
            Nothing here plays a reaction: that is the client's job and starts when a mode plugin claims them.
Original content stays under ignored work/extract and dist; Arena is only ever read. No voice text enters git: the sidecar holds names and tags.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess

from extract_gate_sounds import read_wav, write_wav

REPO = Path(__file__).resolve().parents[2]
OUTPUT = REPO / 'work/extract/spectator-audio'
SOURCE = Path('D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data')
BUNDLE = 'StreamingAssets/Windows/assets/content/audio/phrases/fan_speaker_voice.bundle'
MANIFEST = 'spectator-audio.json'
NAME = re.compile(r'arena_fan(?:[7-9]|1[0-3])_participating_\d{2}')
# The crowd bed: optional in a manifest (an older one has none), required by extract() and by the distribution validator.
BED_NAME = 'arena_fans_ambient_neutral'
BED_FILE = BED_NAME + '.wav'
BED_SOURCE = 'StreamingAssets/Windows/assets/content/audio/phrases/ambient_voice.bundle'
BED_BANK, BED_TRIGGER = 'Neutral', 1122
# The bed the port shipped until 2026-11-02 (0.4.0); extract() removes it from its output folder so the exact file set validates again.
RETIRED_BED_FILES = ('arena_fans_ambient_postmatch.wav',)
BED_CROSSFADE_MS = 500
# Constant gain after the downmix, recorded in the bed's processing. -8.9 dB brings the neutral bed (-31.2 dBFS RMS after the downmix) to the level of the
# postmatch bed the user approved on 2026-10-30 (-40.0 dBFS RMS): the character changes, the loudness does not (Spectators/CrowdBedVolume raises it, up to 2.0).
BED_GAIN_DB = -8.9
BED_MIN_FRAMES = 20 * 44100
BED_MAX_FRAMES = 90 * 44100
BED_MAX_JUMP = 3.0  # loop point: |last - first sample| at most this many times the RMS of the sample-to-sample differences

# The reaction clips (phase A of docs/PLAN-CROWD-REACTIONS.md). Optional in a manifest (an older one has none), required by extract() and by the
# distribution validator. Their bundle is the calls' bundle (BUNDLE).
# (bank, trigger, embedded clips, external slots omitted, null slots), measured 2026-11-02 on the installed Arena; every bank has one SpreadGroup that
# equals its Clips, and every embedded clip streams from the bundle's own CAB resource (plan 2.2 and 2.3).
REACTION_BANKS = (
    ('Cheer', 1119, 14, 15, 0),
    ('Owned', 1120, 16, 3, 0),
    ('Angry', 1121, 18, 23, 2),
    ('ChangedRound', 1125, 9, 5, 0),
    ('EndRound', 1126, 11, 8, 0),
    ('Hide', 1124, 36, 14, 0),
)
REACTION_CATEGORIES = ('kill_cheer', 'kill_angry', 'match_start', 'match_end', 'aim_hide', 'shoot_hide')
REACTION_NAME = re.compile(r'arena_fan\d{1,2}_(?:' + '|'.join(REACTION_CATEGORIES) + r')_\d{2}')
REACTION_FILE = re.compile(REACTION_NAME.pattern + r'\.wav')  # the only files extract() may delete from its output folder
REACTION_MIN_FRAMES = 22050           # 0.5 s, like the calls
REACTION_MAX_FRAMES = 7 * 44100       # 7 s (the calls keep 5 s): the longest kept clip is 6.71 s
REACTION_POOLS = Path(__file__).resolve().with_name('reaction_pools.json')
# The sidecar covers all eight reaction banks of the bundle, the two that have no kind yet included (they are never exported or cross-checked
# against the bundle here; the real-data test pins their layout).
POOL_BANKS = tuple(row[0] for row in REACTION_BANKS) + ('LowDeathIntensity', 'PurifiedTeam')
# Which bank(s) a clip name category belongs to, and which pool tags a bank may use (Cheer and Owned share their clips).
CATEGORY_BANKS = {'kill_cheer': ('Cheer', 'Owned'), 'kill_angry': ('Angry',), 'match_start': ('ChangedRound',), 'match_end': ('EndRound',),
                  'aim_hide': ('Hide',), 'shoot_hide': ('Hide',), 'low_death_intensity': ('LowDeathIntensity',), 'sweep_team': ('PurifiedTeam',)}
BANK_POOL_TAGS = {'Cheer': ('praise', 'gloat', 'shout', 'urge'), 'Owned': ('praise', 'gloat', 'shout', 'urge'), 'Angry': ('lament', 'heckle'),
                  'ChangedRound': ('start',), 'EndRound': ('finish',), 'Hide': ('panic', 'directed'),
                  'LowDeathIntensity': ('lull',), 'PurifiedTeam': ('lull', 'hype', 'sided')}
# The kind of sentence group (`<kind>-NN`) a pool tag draws its clips from.
POOL_GROUP_KINDS = {'praise': ('approve',), 'gloat': ('approve',), 'shout': ('approve',), 'urge': ('approve',), 'lament': ('disapprove',),
                    'heckle': ('disapprove',), 'start': ('start',), 'finish': ('finish',), 'panic': ('takecover',), 'directed': ('takecover',),
                    'lull': ('lull', 'purified'), 'hype': ('purified',), 'sided': ('purified',)}
POOL_NAME = re.compile(r'arena_fan\d{1,2}_(' + '|'.join(CATEGORY_BANKS) + r')_\d{2}')
GROUP_NAME = re.compile(r'([a-z]+)-\d{2}')
# Status of a sidecar clip: `keep` ships, `listen` waits for the author's ear (promote it by editing the status), `out` never ships. A clip that does not
# ship carries a reason code.
LEFT_OUT_REASONS = {'listen': ('FLAG', 'ROLE', 'TASTE', 'CTX', 'SIDED'), 'out': ('SLUR', 'UNCL', 'WRONG')}
POOL_STATUSES = ('keep',) + tuple(LEFT_OUT_REASONS)
POOL_KEYS = {'schema', 'about', 'exported_banks', 'exported_status', 'clips'}
# SHA-256 of the reviewed `about` prose of the sidecar: it is the one free string of the file, so it is pinned and not described by a vocabulary. Editing the prose means editing the pin
# (and a review of the new text: nothing of it may be a transcript, a translation or a voice line).
POOL_ABOUT_SHA256 = '0b2905e796b1184159a2353d917a95b391f538d22662d7bdc6270a1d7f56ed8d'
POOL_ROW_KEYS = {'banks', 'pool', 'group', 'status'}
REACTION_KEYS = {'schema', 'source_bundle', 'max_frames', 'banks', 'clips'}
REACTION_BANK_KEYS = {'bank', 'trigger', 'embedded', 'external_entries_omitted', 'null_entries', 'clips', 'left_out'}
REACTION_ROW_KEYS = {'name', 'file', 'channels', 'sample_rate', 'frames', 'bytes', 'sha256', 'peak', 'rms', 'source_id', 'processing', 'pool', 'group'}
REACTION_PROCESSING_KEYS = {'original_rate', 'original_duration', 'original_peak', 'original_rms', 'gain_db'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def levels(samples):
    import numpy as np
    return float(np.max(np.abs(samples))), float(np.sqrt(np.mean(samples * samples)))


def normalize(wav):
    """Resample, then constant gain only: -22 dBFS RMS, <=+6 dB boost, <=-3 dBFS peak."""
    import numpy as np
    samples, rate = read_wav(wav)
    require(samples.shape[1] == 1, 'fan calls must be mono')
    original_duration = len(samples) / rate
    if rate != 44100:
        result = subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-i', 'pipe:0',
                                 '-ar', '44100', '-ac', '1', '-f', 's16le', 'pipe:1'],
                                input=wav, capture_output=True, check=True)
        samples = np.frombuffer(result.stdout, dtype='<i2').astype(np.float64).reshape(-1, 1) / 32768
    peak, rms = levels(samples)
    require(peak > .001 and rms > .001, 'silent fan clip')
    gain = min(10 ** (6 / 20), 10 ** (-22 / 20) / rms, 10 ** (-3 / 20) / peak)
    data = write_wav(samples * gain, 44100)
    return data, dict(original_rate=rate, original_duration=original_duration,
                      original_peak=peak, original_rms=rms, gain_db=20 * math.log10(gain))


def clip_record(name, data):
    samples, rate = read_wav(data)
    peak, rms = levels(samples)
    return dict(name=name, file=name + '.wav', channels=samples.shape[1], sample_rate=rate,
                frames=len(samples), bytes=len(data), sha256=hashlib.sha256(data).hexdigest(), peak=peak, rms=rms)


def protected_names():
    """Base names of the raw files that take part in the bundle content keys (tools/bundle/key-sources.json)."""
    key_maps = json.loads((REPO / 'tools/bundle/key-sources.json').read_text(encoding='utf-8'))['maps']
    return {Path(f).name for entries in key_maps.values() for entry in entries for f in entry['files']}


def process_bed(wav):
    """Mono or stereo 44.1 kHz clip -> mono loop-safe bed: the mean of the channels, the constant gain BED_GAIN_DB, then the first
    BED_CROSSFADE_MS of the clip is faded IN over its last BED_CROSSFADE_MS (equal power) and that tail is dropped. The result is that
    much shorter than the source and its last sample runs straight into its first one: the source's own level step and sample
    jump at the loop point are gone. Returns (wav bytes, processing record)."""
    import numpy as np
    samples, rate = read_wav(wav)
    require(rate == 44100 and samples.shape[1] in (1, 2), 'the crowd bed must be a mono or stereo 44.1 kHz clip')
    peak, rms = levels(samples)
    require(peak > .001 and rms > .001, 'silent crowd bed')
    mono = samples.mean(axis=1) * 10 ** (BED_GAIN_DB / 20)
    fade = round(BED_CROSSFADE_MS * rate / 1000)
    length = len(mono) - fade
    require(length > 2 * fade, 'crowd bed too short for its crossfade')
    ramp = np.arange(fade) / fade * (np.pi / 2)
    seam = mono[length:] * np.cos(ramp) + mono[:fade] * np.sin(ramp)
    loop = np.concatenate([seam, mono[fade:length]])[:, None]
    require(np.max(np.abs(loop)) < .999, 'crowd bed clips')
    processing = dict(original_channels=samples.shape[1], original_rate=rate, original_frames=len(samples),
                      original_duration=len(samples) / rate, original_peak=peak, original_rms=rms,
                      downmix='mean', gain_db=BED_GAIN_DB, crossfade_frames=fade)
    return write_wav(loop, rate), processing


def loop_jump(samples):
    """(|last - first sample|, RMS of the sample-to-sample differences) of a mono clip: the step heard when the clip loops."""
    import numpy as np
    flat = samples[:, 0]
    return float(abs(flat[0] - flat[-1])), float(np.sqrt(np.mean(np.diff(flat) ** 2)))


def bed_record(data, source_id, processing):
    row = clip_record(BED_NAME, data)
    row.update(seam_crossfade_ms=BED_CROSSFADE_MS, source=BED_SOURCE, source_id=source_id, processing=processing)
    return row


def validate_bed(folder, row):
    """Checks the bed record against its file like a call (hash, size, format, levels) plus what only the bed has: the crossfade
    bookkeeping and a loop point without a step. Returns the file name."""
    require(isinstance(row, dict) and row.get('name') == BED_NAME and row.get('file') == BED_FILE, 'invalid crowd bed record')
    file = folder / BED_FILE
    require(file.is_file() and not file.is_symlink() and file.stat().st_size <= 44 + 2 * BED_MAX_FRAMES, 'missing/oversized crowd bed')
    payload = file.read_bytes()
    actual = clip_record(BED_NAME, payload)
    for key in ('channels', 'sample_rate', 'frames', 'bytes', 'sha256'):
        require(row.get(key) == actual[key], f'{BED_NAME}: {key} mismatch')
    require(actual['channels'] == 1 and actual['sample_rate'] == 44100 and
            BED_MIN_FRAMES <= actual['frames'] <= BED_MAX_FRAMES and actual['bytes'] == 44 + actual['frames'] * 2,
            'expected canonical mono 44.1 kHz PCM16 crowd bed of 20-40 s')
    require(.001 < actual['rms'] < .081 and .001 < actual['peak'] <= .709, 'silent/over-loud crowd bed')
    for key in ('peak', 'rms'):
        require(abs(row.get(key, -1) - actual[key]) < 1e-9, f'{BED_NAME}: {key} mismatch')
    require(row.get('seam_crossfade_ms') == BED_CROSSFADE_MS and row.get('source') == BED_SOURCE and
            type(row.get('source_id')) is int, f'{BED_NAME}: unexpected provenance')
    processing = row.get('processing')
    fade = round(BED_CROSSFADE_MS * 44100 / 1000)
    require(isinstance(processing, dict) and processing.get('crossfade_frames') == fade and
            processing.get('original_rate') == 44100 and processing.get('original_channels') in (1, 2) and
            type(processing.get('original_frames')) is int and processing.get('downmix') == 'mean' and
            processing.get('gain_db') == BED_GAIN_DB and
            actual['frames'] == processing['original_frames'] - fade, f'{BED_NAME}: processing record mismatch')
    jump, step = loop_jump(read_wav(payload)[0])
    require(step > 0 and jump <= BED_MAX_JUMP * step, f'{BED_NAME}: the loop point jumps ({jump:.5f} against a step of {step:.5f})')
    return BED_FILE


def check_pools(doc):
    """Structure of the tracked sidecar tools/extract/reaction_pools.json (schema 1; docs/PLAN-CROWD-REACTIONS.md 4.5): the keys, the banks it exports
    (the extractor's REACTION_BANKS) and the statuses that ship, and for every clip name a row of exactly `banks`, `pool`, `group`, `status`
    (+ `reason` when the clip does not ship), all from closed vocabularies: bank names, pool tags, `<kind>-NN` group ids, status and reason codes.
    No field can hold free text: the one prose string, `about`, is pinned by its SHA-256 (POOL_ABOUT_SHA256), so editing it means editing the pin. A clip may only sit in the
    banks its name category belongs to, and its pool tag must be one of those banks'. Returns the document."""
    require(isinstance(doc, dict) and set(doc) == POOL_KEYS and doc['schema'] == 1 and isinstance(doc['about'], str) and
            hashlib.sha256(doc['about'].encode('utf-8')).hexdigest() == POOL_ABOUT_SHA256,
            'unsupported reaction pool sidecar')
    require(doc['exported_banks'] == [row[0] for row in REACTION_BANKS], 'the reaction pool sidecar exports other banks than the extractor')
    shipped = doc['exported_status']
    require(isinstance(shipped, list) and shipped and len(set(shipped)) == len(shipped) and set(shipped) <= {'keep', 'listen'},
            'the reaction pool sidecar must export keep (and may export listen), never out')
    clips = doc['clips']
    require(isinstance(clips, dict) and clips, 'the reaction pool sidecar holds no clips')
    for name, row in clips.items():
        match = POOL_NAME.fullmatch(name)
        require(match, f'reaction pool sidecar: invalid clip name {name!r}')
        require(isinstance(row, dict) and row.get('status') in POOL_STATUSES, f'{name}: invalid status')
        status = row['status']
        require(set(row) == POOL_ROW_KEYS | ({'reason'} if status != 'keep' else set()), f'{name}: unexpected keys in the pool row')
        banks = row['banks']
        require(isinstance(banks, list) and banks and len(set(banks)) == len(banks) and
                [b for b in POOL_BANKS if b in banks] == banks and set(banks) <= set(CATEGORY_BANKS[match.group(1)]),
                f'{name}: banks do not fit the clip name')
        if status == 'keep' or status == 'listen':
            pool, group = row['pool'], row['group']
            require(isinstance(pool, str) and all(pool in BANK_POOL_TAGS[b] for b in banks), f'{name}: pool tag does not belong to its bank(s)')
            kind = GROUP_NAME.fullmatch(group) if isinstance(group, str) else None
            require(kind and kind.group(1) in POOL_GROUP_KINDS[pool], f'{name}: sentence group does not fit the pool')
        else:
            require(row['pool'] is None and row['group'] is None, f'{name}: a clip that is out has no pool and no group')
        if status != 'keep':
            require(row['reason'] in LEFT_OUT_REASONS[status], f'{name}: invalid reason for status {status}')
    return doc


def load_pools(path=REACTION_POOLS):
    return check_pools(json.loads(Path(path).read_text(encoding='utf-8')))


def exported_clips(pools):
    """name -> pool row of the clips that ship: in one of the exported banks and with a status in `exported_status`."""
    exported = set(pools['exported_banks'])
    return {name: row for name, row in sorted(pools['clips'].items())
            if exported & set(row['banks']) and row['status'] in pools['exported_status']}


def reaction_banks(pools):
    """The expected `banks` list of the manifest, from the sidecar alone: per bank of REACTION_BANKS its provenance numbers, the sorted names that
    ship and the sorted left-out clips with status and reason."""
    result = []
    for bank, trigger, embedded, external, null in REACTION_BANKS:
        members = sorted(name for name, row in pools['clips'].items() if bank in row['banks'])
        shipped = [name for name in members if pools['clips'][name]['status'] in pools['exported_status']]
        left = [dict(name=name, status=pools['clips'][name]['status'], reason=pools['clips'][name]['reason'])
                for name in members if name not in shipped]
        result.append(dict(bank=bank, trigger=trigger, embedded=embedded, external_entries_omitted=external, null_entries=null,
                           clips=shipped, left_out=left))
    return result


def reaction_object(rows, pools):
    """The manifest's `reactions` object for the clip records `rows` (name -> clip_record plus source_id and processing) of the exported clips: the
    deduplicated table sorted by name, each clip with the pool tag and sentence group of the sidecar, and the banks of reaction_banks(). A name shared
    by Cheer and Owned is one table row listed in both banks."""
    table = []
    for name in sorted(rows):
        row = dict(rows[name])
        row.update(pool=pools['clips'][name]['pool'], group=pools['clips'][name]['group'])
        table.append(row)
    return dict(schema=1, source_bundle=BUNDLE, max_frames=REACTION_MAX_FRAMES, banks=reaction_banks(pools), clips=table)


def validate_reaction_clip(folder, row):
    """One row of the reaction clip table against its file, like a call but with the 7 s window: hash, size, canonical mono 44.1 kHz PCM16, levels,
    the provenance fields and the shape of the normalisation record. Returns the file name."""
    require(isinstance(row, dict) and set(row) == REACTION_ROW_KEYS, 'invalid reaction clip record')
    name = row['name']
    require(isinstance(name, str) and REACTION_NAME.fullmatch(name) and row['file'] == name + '.wav', f'invalid reaction clip name {name!r}')
    file = folder / row['file']
    require(file.is_file() and not file.is_symlink() and file.stat().st_size <= 44 + 2 * REACTION_MAX_FRAMES,
            f'{name}: missing/oversized reaction clip')
    payload = file.read_bytes()
    actual = clip_record(name, payload)
    for key in ('channels', 'sample_rate', 'frames', 'bytes'):
        require(type(row[key]) is int and row[key] == actual[key], f'{name}: {key} mismatch')
    require(row['sha256'] == actual['sha256'], f'{name}: sha256 mismatch')
    require(actual['channels'] == 1 and actual['sample_rate'] == 44100 and
            REACTION_MIN_FRAMES <= actual['frames'] <= REACTION_MAX_FRAMES and actual['bytes'] == 44 + actual['frames'] * 2,
            f'{name}: expected canonical mono 44.1 kHz PCM16 WAV of 0.5-7 s')
    require(.001 < actual['rms'] < .081 and .001 < actual['peak'] <= .709, f'{name}: silent/over-loud reaction clip')
    for key in ('peak', 'rms'):
        require(isinstance(row[key], float) and abs(row[key] - actual[key]) < 1e-9, f'{name}: {key} mismatch')
    processing = row['processing']
    require(type(row['source_id']) is int and isinstance(processing, dict) and set(processing) == REACTION_PROCESSING_KEYS and
            all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in processing.values()) and
            processing['original_rate'] > 0 and processing['gain_db'] <= 6.00001, f'{name}: processing record mismatch')
    return row['file']


def validate_reactions(folder, reactions, pools):
    """The manifest's `reactions` object (docs/PLAN-CROWD-REACTIONS.md 4.5) against the folder and the sidecar `pools` (already checked). The clip
    table: unique names of the phase-A categories in sorted order, every clip a valid file (validate_reaction_clip), every clip in at least one bank,
    its pool tag one of every bank's. The banks: exactly REACTION_BANKS in order with their provenance numbers, sorted unique name lists, left-out
    clips with a status and a reason that fit, and `embedded == len(clips) + len(left_out)`. Then the sidecar decides: the exported names and the
    left-out rows of every bank and the pool and group of every clip must be what reaction_pools.json says now (a promoted `listen` clip needs a
    new extraction). Returns the clip file names."""
    require(isinstance(reactions, dict) and set(reactions) == REACTION_KEYS and reactions['schema'] == 1 and
            reactions['source_bundle'] == BUNDLE and type(reactions['max_frames']) is int and reactions['max_frames'] == REACTION_MAX_FRAMES and
            isinstance(reactions['banks'], list) and isinstance(reactions['clips'], list), 'unsupported reaction set')
    rows, banks = reactions['clips'], reactions['banks']
    files, names = set(), []
    for row in rows:
        files.add(validate_reaction_clip(folder, row))
        names.append(row['name'])
    require(len(set(names)) == len(names) and names == sorted(names), 'reaction clip names are duplicated or not sorted')
    require([b.get('bank') if isinstance(b, dict) else None for b in banks] == [row[0] for row in REACTION_BANKS],
            'reaction banks differ from the extractor table')
    member_of = {name: [] for name in names}
    for bank_row, (bank, trigger, embedded, external, null) in zip(banks, REACTION_BANKS):
        require(set(bank_row) == REACTION_BANK_KEYS and bank_row['trigger'] == trigger and bank_row['embedded'] == embedded and
                bank_row['external_entries_omitted'] == external and bank_row['null_entries'] == null, f'{bank}: bank provenance mismatch')
        listed, left = bank_row['clips'], bank_row['left_out']
        require(isinstance(listed, list) and all(isinstance(n, str) for n in listed) and listed == sorted(set(listed)) and
                isinstance(left, list), f'{bank}: clip list is not sorted/unique')
        for name in listed:
            require(name in member_of, f'{bank}: lists {name}, which is not in the clip table')
            member_of[name].append(bank)
        for entry in left:
            require(isinstance(entry, dict) and set(entry) == {'name', 'status', 'reason'} and isinstance(entry['name'], str) and
                    isinstance(entry['status'], str) and entry['status'] in LEFT_OUT_REASONS and
                    entry['reason'] in LEFT_OUT_REASONS[entry['status']], f'{bank}: invalid left-out record')
        left_names = [entry['name'] for entry in left]
        require(left_names == sorted(set(left_names)) and not set(left_names) & set(listed), f'{bank}: left-out clips are duplicated or also exported')
        require(embedded == len(listed) + len(left), f'{bank}: {embedded} embedded clips do not add up to {len(listed)} exported + {len(left)} left out')
    for row in rows:
        homes = member_of[row['name']]
        require(homes, f"{row['name']}: not listed in any bank")
        require(all(row['pool'] in BANK_POOL_TAGS[bank] for bank in homes), f"{row['name']}: pool {row['pool']!r} does not belong to {homes}")
    for bank_row, want in zip(banks, reaction_banks(pools)):
        require(bank_row['clips'] == want['clips'], f"{want['bank']}: exported clips differ from reaction_pools.json (extract again)")
        require(bank_row['left_out'] == want['left_out'], f"{want['bank']}: left-out clips differ from reaction_pools.json (extract again)")
    for row in rows:
        side = pools['clips'][row['name']]
        require(row['pool'] == side['pool'] and row['group'] == side['group'],
                f"{row['name']}: pool/group differ from reaction_pools.json (extract again)")
    return files


def validate(folder, require_bed=False, require_reactions=False, pools=None):
    """The calls, then the optional parts: the bed (`bed`) and the reaction set (`reactions`), each required with its flag. `pools` is the sidecar
    document the reaction set is checked against (default: the tracked reaction_pools.json). The folder must hold exactly the files the manifest lists."""
    folder = Path(folder)
    data = json.loads((folder / MANIFEST).read_text(encoding='utf-8'))
    require(data.get('schema') == 1 and data.get('trigger') == 1123 and data.get('bank') == 'Participating',
            'unsupported spectator audio manifest')
    require(data.get('source_bundle') == BUNDLE and data.get('external_entries_omitted') == 9,
            'unexpected spectator audio provenance')
    rows = data.get('clips', [])
    require(len(rows) == 13, 'expected 13 embedded participating calls')
    expected = {MANIFEST}
    names = set()
    for row in rows:
        name = row.get('name', '')
        require(NAME.fullmatch(name) and name not in names and row.get('file') == name + '.wav', 'invalid/duplicate clip name')
        names.add(name)
        file = folder / row['file']
        require(file.is_file() and not file.is_symlink() and file.stat().st_size <= 441044, 'missing/oversized spectator clip')
        payload = file.read_bytes()
        actual = clip_record(name, payload)
        for key in ('channels', 'sample_rate', 'frames', 'bytes', 'sha256'):
            require(row.get(key) == actual[key], f'{name}: {key} mismatch')
        require(actual['channels'] == 1 and actual['sample_rate'] == 44100 and
                22050 <= actual['frames'] <= 220500 and actual['bytes'] == 44 + actual['frames'] * 2,
                'expected canonical mono 44.1 kHz PCM16 WAV')
        require(.001 < actual['rms'] < .081 and .001 < actual['peak'] <= .709, 'silent/over-loud spectator clip')
        for key in ('peak', 'rms'):
            require(abs(row.get(key, -1) - actual[key]) < 1e-9, f'{name}: {key} mismatch')
        expected.add(row['file'])
    if data.get('bed') is None:
        require(not require_bed, 'the crowd bed is missing from the spectator audio manifest')
    else:
        expected.add(validate_bed(folder, data['bed']))
    if data.get('reactions') is None:
        require(not require_reactions, 'the reaction set is missing from the spectator audio manifest')
    else:
        expected |= validate_reactions(folder, data['reactions'], load_pools() if pools is None else check_pools(pools))
    require({p.name for p in folder.iterdir()} == expected, 'unexpected spectator audio files')
    return data


def find_bed(source):
    """Arena's bed clip: the one embedded clip of the `Neutral` bank (trigger 1122) of ambient_voice.bundle, which must be named BED_NAME (the
    bank's provenance is checked, not only the name). Returns (reader, decoded stereo WAV). Neither the bundle nor the stream file that holds
    the audio may take part in the bundle content keys."""
    import UnityPy
    from extract_arena_audio import _decode_and_validate_source
    bundle = Path(source) / BED_SOURCE
    protected = protected_names()
    require(bundle.name not in protected, 'crowd bed overlaps a protected key source')
    env = UnityPy.load(str(bundle))
    banks = [(o, o.read_typetree()) for o in env.objects if o.type.name == 'MonoBehaviour']
    selected = [(o, t) for o, t in banks if t.get('m_Name') == BED_BANK and 'Clips' in t]
    require(len(selected) == 1 and selected[0][1].get('Trigger') == BED_TRIGGER, f'{BED_BANK} bank of the crowd beds is missing or changed')
    reader, bank = selected[0]
    local = [slot['Clip']['m_PathID'] for slot in bank['Clips'] if slot['Clip']['m_PathID'] and not slot['Clip']['m_FileID']]
    require(len(local) == 1, f'{BED_BANK} bank no longer holds exactly one embedded bed')
    clip = reader.assets_file.objects[local[0]]
    tree = clip.read_typetree()
    require(clip.type.name == 'AudioClip' and tree['m_Name'] == BED_NAME, f'{BED_NAME} is not the {BED_BANK} bank\'s bed')
    stream = (tree.get('m_Resource') or {}).get('m_Source') or ''
    require(Path(stream).name not in protected, 'crowd bed overlaps a protected key source')
    return clip, _decode_and_validate_source(clip, BED_NAME)[0]


def remove_stale_reactions(output, keep):
    """Deletes from `output` the reaction WAVs of an earlier export that the new one does not write (a clip demoted in the sidecar, or gone): the folder
    must hold exactly the manifest's files, and validate() refuses a stray one. Only a regular file whose whole name matches REACTION_FILE and whose
    clip name is not in `keep` goes; the calls, the bed, the manifest and anything else stay."""
    for path in Path(output).iterdir():
        if path.is_file() and REACTION_FILE.fullmatch(path.name) and path.name[:-4] not in keep:
            path.unlink()


def find_reactions(banks, pools):
    """Reads the six reaction banks of the bundle (`banks` is the list of its (object, typetree) MonoBehaviours) and checks them against REACTION_BANKS and
    the sidecar `pools`: per bank one bank object with its trigger and one SpreadGroup equal to Clips, the embedded, external and null slot counts
    of the table, unique AudioClip names of the reaction categories, and for every clip the membership the sidecar names (a name shared by Cheer and
    Owned is one object). A clip that will ship must not stream from a key-source file (tools/bundle/key-sources.json). Returns (name -> AudioClip
    object, name -> its typetree) for the clips to export."""
    protected = protected_names()
    table = [row[0] for row in REACTION_BANKS]
    members, objects, trees = {}, {}, {}
    for bank, trigger, embedded, external, null in REACTION_BANKS:
        selected = [(o, t) for o, t in banks if t.get('m_Name') == bank and 'Clips' in t]
        require(len(selected) == 1, f'{bank} bank is missing or ambiguous')
        reader, tree = selected[0]
        require(tree['Trigger'] == trigger and len(tree['SpreadGroups']) == 1 and
                tree['SpreadGroups'][0]['Clips'] == tree['Clips'], f'{bank} bank layout changed')
        names, outside, hollow = [], 0, 0
        for slot in tree['Clips']:
            ptr = slot['Clip']
            if not ptr['m_PathID']:
                hollow += 1
            elif ptr['m_FileID']:
                outside += 1
            else:
                clip = reader.assets_file.objects[ptr['m_PathID']]
                require(clip.type.name == 'AudioClip', f'{bank}: a reaction reference is not audio')
                clip_tree = clip.read_typetree()
                name = clip_tree['m_Name']
                require(REACTION_NAME.fullmatch(name), f'{bank}: unexpected reaction clip name {name!r}')
                require(objects.setdefault(name, clip).path_id == clip.path_id, f'{name}: two different clips share the name')
                trees[name] = clip_tree
                names.append(name)
        require((len(names), outside, hollow) == (embedded, external, null) and len(set(names)) == len(names),
                f'{bank} bank membership changed ({len(names)} embedded, {outside} external, {hollow} null)')
        for name in names:
            members.setdefault(name, []).append(bank)
    for name, homes in members.items():
        row = pools['clips'].get(name)
        require(row is not None, f'{name} is not in reaction_pools.json')
        require([b for b in row['banks'] if b in table] == homes, f'{name}: banks in reaction_pools.json differ from the bundle ({homes})')
    for name, row in pools['clips'].items():
        require(name in members or not set(row['banks']) & set(table), f'{name} is in reaction_pools.json but not in the bundle')
    shipped = exported_clips(pools)
    for name in shipped:
        stream = Path((trees[name].get('m_Resource') or {}).get('m_Source') or '').name
        require(stream not in protected, f'{name} streams from a protected key source')
    return {name: objects[name] for name in shipped}, {name: trees[name] for name in shipped}


def extract(source=SOURCE, output=OUTPUT):
    import UnityPy
    from extract_arena_audio import _decode_and_validate_source
    bundle = Path(source) / BUNDLE
    # Plaintext audio must never use a file that participates in bundle content keys.
    require(bundle.name not in protected_names(), 'spectator audio overlaps a protected key source')
    env = UnityPy.load(str(bundle))
    banks = [(o, o.read_typetree()) for o in env.objects if o.type.name == 'MonoBehaviour']
    selected = [(o, t) for o, t in banks if t.get('m_Name') == 'Participating']
    require(len(selected) == 1, 'Participating bank is missing or ambiguous')
    reader, bank = selected[0]
    require(bank['Trigger'] == 1123 and len(bank['SpreadGroups']) == 1 and
            bank['SpreadGroups'][0]['Clips'] == bank['Clips'], 'Participating bank layout changed')
    local, external = [], 0
    for slot in bank['Clips']:
        ptr = slot['Clip']
        require(ptr['m_PathID'] != 0, 'null participating clip')
        if ptr['m_FileID']:
            external += 1
            continue
        clip = reader.assets_file.objects[ptr['m_PathID']]
        require(clip.type.name == 'AudioClip', 'participating reference is not audio')
        name = clip.read_typetree()['m_Name']
        require(NAME.fullmatch(name), 'unexpected participating clip name')
        local.append((name, clip))
    require(len(local) == 13 and external == 9 and len({n for n, _ in local}) == 13, 'fan bank membership changed')
    pools = load_pools()
    reaction_clips, _ = find_reactions(banks, pools)
    bed_reader, bed_wav = find_bed(source)
    bed_payload, bed_processing = process_bed(bed_wav)
    # everything is decoded and normalised before the first file is written, so a clip that fails leaves the output folder as it was
    reaction_payloads, reaction_rows = {}, {}
    for name, clip in reaction_clips.items():
        wav, _ = _decode_and_validate_source(clip, name)
        payload, processing = normalize(wav)
        row = clip_record(name, payload)
        require(REACTION_MIN_FRAMES <= row['frames'] <= REACTION_MAX_FRAMES, f'{name}: {row["frames"]} frames, expected 0.5-7 s')
        row.update(source_id=clip.path_id, processing=processing)
        reaction_payloads[name], reaction_rows[name] = payload, row
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    for retired in RETIRED_BED_FILES:
        (output / retired).unlink(missing_ok=True)
    remove_stale_reactions(output, reaction_rows)
    rows = []
    for name, clip in sorted(local):
        wav, _ = _decode_and_validate_source(clip, name)
        payload, processing = normalize(wav)
        row = clip_record(name, payload)
        row.update(source_id=clip.path_id, processing=processing)
        (output / row['file']).write_bytes(payload)
        rows.append(row)
    (output / BED_FILE).write_bytes(bed_payload)
    for name, payload in reaction_payloads.items():
        (output / (name + '.wav')).write_bytes(payload)
    manifest = dict(schema=1, bank='Participating', trigger=1123, source_bundle=BUNDLE, external_entries_omitted=external,
                    clips=rows, bed=bed_record(bed_payload, bed_reader.path_id, bed_processing), reactions=reaction_object(reaction_rows, pools))
    (output / MANIFEST).write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    validate(output, require_bed=True, require_reactions=True, pools=pools)
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['extract', 'verify'])
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=OUTPUT)
    args = parser.parse_args()
    result = extract(args.source, args.output) if args.action == 'extract' else validate(args.output)
    bed, reactions = result.get('bed'), result.get('reactions')
    print(f'SPECTATOR_AUDIO_OK {len(result["clips"])} clips, {sum(c["bytes"] for c in result["clips"])} bytes; ' +
          (f'bed {bed["name"]} {bed["frames"] / bed["sample_rate"]:.3f} s mono, {bed["bytes"]} bytes, sha256 {bed["sha256"]}'
           if bed else 'no bed (the manifest has none)') + '; ' +
          (f'reactions {len(reactions["clips"])} clips, {sum(c["bytes"] for c in reactions["clips"])} bytes'
           if reactions else 'no reactions (the manifest has none)'))
