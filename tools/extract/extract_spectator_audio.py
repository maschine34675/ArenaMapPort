"""Export the 13 embedded Participating fan calls and the crowd murmur bed, shared by all restored crowds.

No match/kill announcements or external bank dependencies. The one clip that is not a call is the bed:
Arena's `arena_fans_ambient_postmatch` (its only crowd ambience, a results-screen bed that the port plays
live under the stands, docs/progress/spectator-audio.md "Crowd bed"), read from resources.assets, downmixed
to mono and made loop-safe offline by an equal-power crossfade of the head into the tail. Original content
stays under ignored work/extract and dist; Arena is only ever read.
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
BED_NAME = 'arena_fans_ambient_postmatch'
BED_FILE = BED_NAME + '.wav'
BED_SOURCE = 'resources.assets'
BED_CROSSFADE_MS = 500
BED_GAIN_DB = 0.0  # constant gain after the downmix (0 = Arena's own level); recorded in the bed's processing
BED_MIN_FRAMES = 20 * 44100
BED_MAX_FRAMES = 40 * 44100
BED_MAX_JUMP = 3.0  # loop point: |last - first sample| at most this many times the RMS of the sample-to-sample differences


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
            actual['frames'] == processing['original_frames'] - fade, f'{BED_NAME}: processing record mismatch')
    jump, step = loop_jump(read_wav(payload)[0])
    require(step > 0 and jump <= BED_MAX_JUMP * step, f'{BED_NAME}: the loop point jumps ({jump:.5f} against a step of {step:.5f})')
    return BED_FILE


def validate(folder, require_bed=False):
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
    require({p.name for p in folder.iterdir()} == expected, 'unexpected spectator audio files')
    return data


def find_bed(source):
    """Arena's bed clip, found by name in resources.assets exactly like extract_arena_audio.py finds it for its `crowd` group (the path
    id that script records is only a drift hint: 4985 in its notes, 5167 in Arena 0.6.1). Returns (reader, decoded stereo WAV).
    Neither resources.assets nor the stream file that holds the audio may take part in the bundle content keys."""
    from extract_arena_audio import GROUP_SOURCES, _load_environment, _index_clip_names, _decode_and_validate_source
    spec = GROUP_SOURCES['crowd']
    require(spec['root'] == 'arena' and spec['file'] == BED_SOURCE and list(spec['clips']) == [BED_NAME],
            'extract_arena_audio.py no longer describes the crowd bed')
    index = _index_clip_names(_load_environment(Path(source), BED_SOURCE), wanted_names=[BED_NAME])
    require(BED_NAME in index, f'{BED_NAME} not found in {BED_SOURCE}')
    reader = index[BED_NAME]
    stream = (reader.read_typetree().get('m_Resource') or {}).get('m_Source') or ''
    protected = protected_names()
    require(BED_SOURCE not in protected and Path(stream).name not in protected, 'crowd bed overlaps a protected key source')
    return reader, _decode_and_validate_source(reader, BED_NAME)[0]


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
    bed_reader, bed_wav = find_bed(source)
    bed_payload, bed_processing = process_bed(bed_wav)
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    rows = []
    for name, clip in sorted(local):
        wav, _ = _decode_and_validate_source(clip, name)
        payload, processing = normalize(wav)
        row = clip_record(name, payload)
        row.update(source_id=clip.path_id, processing=processing)
        (output / row['file']).write_bytes(payload)
        rows.append(row)
    (output / BED_FILE).write_bytes(bed_payload)
    manifest = dict(schema=1, bank='Participating', trigger=1123, source_bundle=BUNDLE, external_entries_omitted=external,
                    clips=rows, bed=bed_record(bed_payload, bed_reader.path_id, bed_processing))
    (output / MANIFEST).write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    validate(output, require_bed=True)
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['extract', 'verify'])
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=OUTPUT)
    args = parser.parse_args()
    result = extract(args.source, args.output) if args.action == 'extract' else validate(args.output)
    bed = result.get('bed')
    print(f'SPECTATOR_AUDIO_OK {len(result["clips"])} clips, {sum(c["bytes"] for c in result["clips"])} bytes; ' +
          (f'bed {bed["name"]} {bed["frames"] / bed["sample_rate"]:.3f} s mono, {bed["bytes"]} bytes, sha256 {bed["sha256"]}'
           if bed else 'no bed (the manifest has none)'))
