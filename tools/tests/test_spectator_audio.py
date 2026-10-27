import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import extract_spectator_audio as audio


class SpectatorAudioTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        wave = .08 * np.sin(np.arange(44100)[:, None] * (2 * np.pi * 440 / 44100))
        payload = audio.write_wav(wave, 44100)
        rows = []
        for i in range(13):
            row = audio.clip_record(f'arena_fan7_participating_{i:02}', payload)
            (self.folder / row['file']).write_bytes(payload)
            rows.append(row)
        self.manifest = dict(schema=1, bank='Participating', trigger=1123,
                             source_bundle=audio.BUNDLE, external_entries_omitted=9, clips=rows)
        self.save()

    def save(self):
        (self.folder / audio.MANIFEST).write_text(json.dumps(self.manifest), encoding='utf-8')

    def reject(self):
        self.save()
        with self.assertRaises((ValueError, FileNotFoundError)):
            audio.validate(self.folder)

    def test_complete_canonical_package(self):
        self.assertEqual(len(audio.validate(self.folder)['clips']), 13)

    def test_wrong_bank_or_trigger(self):
        for key, value in [('bank', 'Cheer'), ('trigger', 1119), ('schema', 2)]:
            with self.subTest(key=key):
                before = copy.deepcopy(self.manifest)
                self.manifest[key] = value
                self.reject()
                self.manifest = before

    def test_clip_missing_duplicate_or_path_escape(self):
        row = self.manifest['clips'][0]
        for key, value in [('name', '../escape'), ('file', '../clip.wav'), ('sha256', '0'*64),
                           ('frames', row['frames']+1), ('channels', 2), ('sample_rate', 22050)]:
            with self.subTest(key=key):
                before = row[key]
                row[key] = value
                self.reject()
                row[key] = before
        self.manifest['clips'][1] = row
        self.reject()

    def test_no_extra_files_or_directories(self):
        (self.folder / 'old.wav').write_bytes(b'old')
        self.reject()

    def test_missing_clip(self):
        (self.folder / self.manifest['clips'][0]['file']).unlink()
        self.reject()

    def test_tampering_detected(self):
        path = self.folder / self.manifest['clips'][0]['file']
        data = bytearray(path.read_bytes())
        data[-1] ^= 0x80
        path.write_bytes(data)
        self.reject()

    def test_level_normalization_and_resampling(self):
        for amplitude, rate in [(.9, 44100), (.02, 22050), (.25, 36393)]:
            with self.subTest(amplitude=amplitude, rate=rate):
                samples = amplitude * np.sin(np.arange(rate*2)[:, None] * (2*np.pi*440/rate))
                payload, processing = audio.normalize(audio.write_wav(samples, rate))
                result, output_rate = audio.read_wav(payload)
                peak, rms = audio.levels(result)
                self.assertEqual(output_rate, 44100)
                self.assertLess(abs(len(result) / 44100 - 2), .001)
                self.assertLessEqual(peak, .709)
                self.assertLessEqual(rms, .080)
                self.assertLessEqual(processing['gain_db'], 6.00001)

    def test_silence_and_stereo_refused(self):
        for values in [np.zeros((44100, 1)), np.ones((44100, 2))*.1]:
            with self.assertRaises(ValueError):
                audio.normalize(audio.write_wav(values, 44100))


if __name__ == '__main__':
    unittest.main()
