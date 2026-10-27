"""Export the 13 embedded Participating fan calls, shared by all restored crowds.

No match/kill announcements, external bank dependencies or postmatch loop. Original
content stays under ignored work/extract and dist; Arena is only ever read.
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


def validate(folder):
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
    require({p.name for p in folder.iterdir()} == expected, 'unexpected spectator audio files')
    return data


def extract(source=SOURCE, output=OUTPUT):
    import UnityPy
    from extract_arena_audio import _decode_and_validate_source
    bundle = Path(source) / BUNDLE
    # Plaintext audio must never use a file that participates in bundle content keys.
    key_maps = json.loads((REPO / 'tools/bundle/key-sources.json').read_text(encoding='utf-8'))['maps']
    protected = {Path(f).name for entries in key_maps.values() for entry in entries for f in entry['files']}
    require(bundle.name not in protected, 'spectator audio overlaps a protected key source')
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
    manifest = dict(schema=1, bank='Participating', trigger=1123, source_bundle=BUNDLE,
                    external_entries_omitted=external, clips=rows)
    (output / MANIFEST).write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    validate(output)
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['extract', 'verify'])
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=OUTPUT)
    args = parser.parse_args()
    result = extract(args.source, args.output) if args.action == 'extract' else validate(args.output)
    print(f'SPECTATOR_AUDIO_OK {len(result["clips"])} clips, {sum(c["bytes"] for c in result["clips"])} bytes')
