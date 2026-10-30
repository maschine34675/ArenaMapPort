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


def stereo_source(seconds=22, seed=7, drift=0.0):
    """A stand-in for Arena's stereo bed: correlated noise at about -40 dBFS, optionally with a slow drift so that its first and its last
    sample are far apart (the raw clip's click at the loop point)."""
    rng = np.random.RandomState(seed)
    frames = int(seconds * 44100)
    common = rng.normal(0, .008, frames)
    wave = np.stack([common + rng.normal(0, .004, frames), common + rng.normal(0, .004, frames)], axis=1)
    return wave + np.linspace(-drift, drift, frames)[:, None]


class CrowdBedTests(unittest.TestCase):
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

    def add_bed(self, source=None, source_id=5167):
        payload, processing = audio.process_bed(audio.write_wav(stereo_source() if source is None else source, 44100))
        (self.folder / audio.BED_FILE).write_bytes(payload)
        self.manifest['bed'] = audio.bed_record(payload, source_id, processing)
        self.save()
        return payload

    def reject(self, message=None, **kwargs):
        self.save()
        with self.assertRaisesRegex((ValueError, FileNotFoundError), message or ''):
            audio.validate(self.folder, **kwargs)

    def test_a_manifest_without_a_bed_is_still_a_valid_package(self):
        self.assertEqual(len(audio.validate(self.folder)['clips']), 13)
        self.assertNotIn('bed', audio.validate(self.folder))
        self.reject('the crowd bed is missing', require_bed=True)

    def test_a_complete_bed_validates_and_is_required_when_asked(self):
        self.add_bed()
        self.assertEqual(audio.validate(self.folder, require_bed=True)['bed']['name'], audio.BED_NAME)
        self.assertEqual(len(audio.validate(self.folder)['clips']), 13)

    def test_the_bed_is_mono_shorter_by_the_crossfade_and_keeps_arenas_level(self):
        source = stereo_source()
        payload, processing = audio.process_bed(audio.write_wav(source, 44100))
        result, rate = audio.read_wav(payload)
        fade = 22050
        self.assertEqual((result.shape[1], rate), (1, 44100))
        self.assertEqual(len(result), len(source) - fade)
        self.assertEqual(processing['crossfade_frames'], fade)
        self.assertEqual(processing['original_frames'], len(source))
        self.assertEqual(processing['original_channels'], 2)
        # past the crossfade the loop is the plain mean of the channels (within the PCM16 rounding)
        expected = source.mean(axis=1)[fade:len(source) - fade]
        self.assertLess(np.max(np.abs(result[fade:, 0] - expected)), 2 / 32767)
        # equal power: noise that is not correlated head to tail keeps its level through the faded stretch
        for window in (slice(0, fade // 2), slice(fade // 2, fade)):
            ratio = np.sqrt(np.mean(result[window] ** 2)) / np.sqrt(np.mean(result[fade:] ** 2))
            self.assertLess(abs(20 * np.log10(ratio)), 1.0)

    def test_the_loop_point_has_no_step_where_the_raw_clip_has_one(self):
        source = stereo_source(drift=.1)
        raw_jump, raw_step = audio.loop_jump(source.mean(axis=1, keepdims=True))
        self.assertGreater(raw_jump, 10 * raw_step)
        payload, _ = audio.process_bed(audio.write_wav(source, 44100))
        jump, step = audio.loop_jump(audio.read_wav(payload)[0])
        self.assertLess(jump, audio.BED_MAX_JUMP * step)

    def test_unsuitable_sources_are_refused(self):
        for label, wave, rate in [('rate', stereo_source(), 22050), ('silent', np.zeros((22 * 44100, 2)), 44100),
                                  ('short', stereo_source(1.2), 44100), ('channels', np.stack([stereo_source()[:, 0]] * 3, axis=1), 44100)]:
            with self.subTest(label=label), self.assertRaises(ValueError):
                audio.process_bed(audio.write_wav(wave, rate))

    def test_a_tampered_or_mislabelled_bed_is_refused(self):
        payload = self.add_bed()
        bed = self.manifest['bed']
        for key, value in [('name', 'arena_fans_ambient'), ('file', '../bed.wav'), ('sha256', '0' * 64), ('frames', bed['frames'] + 1),
                           ('bytes', bed['bytes'] + 2), ('channels', 2), ('sample_rate', 22050), ('peak', bed['peak'] * 1.01),
                           ('rms', bed['rms'] * 1.01), ('seam_crossfade_ms', 250), ('source', 'resources.resource'),
                           ('source_id', '5167'), ('source_id', True), ('processing', None)]:
            with self.subTest(key=key, value=value):
                before = bed[key]
                bed[key] = value
                self.reject(require_bed=True)
                bed[key] = before
        for key, value in [('crossfade_frames', 11025), ('original_rate', 22050), ('original_channels', 6), ('original_frames', 1), ('downmix', 'left')]:
            with self.subTest(processing=key):
                before = bed['processing'][key]
                bed['processing'][key] = value
                self.reject('processing record mismatch', require_bed=True)
                bed['processing'][key] = before
        damaged = bytearray(payload)
        damaged[-1] ^= 0x80
        (self.folder / audio.BED_FILE).write_bytes(damaged)
        self.reject('sha256 mismatch', require_bed=True)
        (self.folder / audio.BED_FILE).write_bytes(payload)
        self.assertIn('bed', audio.validate(self.folder, require_bed=True))

    def test_a_bed_that_steps_at_the_loop_point_is_refused(self):
        ramp = np.linspace(-.05, .05, 25 * 44100)[:, None]
        self.manifest['bed'] = audio.bed_record(audio.write_wav(ramp, 44100), 1, dict(
            original_channels=1, original_rate=44100, original_frames=len(ramp) + 22050, original_duration=0, original_peak=.05,
            original_rms=.03, downmix='mean', gain_db=0.0, crossfade_frames=22050))
        (self.folder / audio.BED_FILE).write_bytes(audio.write_wav(ramp, 44100))
        self.reject('the loop point jumps', require_bed=True)

    def test_a_bed_file_the_manifest_does_not_list_is_refused(self):
        self.add_bed()
        del self.manifest['bed']
        self.reject('unexpected spectator audio files')

    def test_a_listed_bed_without_its_file_is_refused(self):
        self.add_bed()
        (self.folder / audio.BED_FILE).unlink()
        self.reject('missing/oversized crowd bed', require_bed=True)

    def test_the_bed_clip_never_comes_from_a_file_that_feeds_the_bundle_keys(self):
        from extract_arena_audio import GROUP_SOURCES
        spec = GROUP_SOURCES['crowd']
        self.assertEqual((spec['root'], spec['file'], list(spec['clips'])), ('arena', audio.BED_SOURCE, [audio.BED_NAME]))
        self.assertTrue({audio.BED_SOURCE, 'resources.resource'}.isdisjoint(audio.protected_names()))


if __name__ == '__main__':
    unittest.main()
