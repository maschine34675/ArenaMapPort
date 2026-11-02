import copy
import json
from pathlib import Path
import sys
import tempfile
import types
import unittest

import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import extract_spectator_audio as audio


def tone(frames=22050, amplitude=.08):
    """A canonical clip for the fixtures: a 440 Hz tone, mono, 44.1 kHz, PCM16 (22050 frames = 0.5 s is the shortest a reaction may be)."""
    return audio.write_wav(amplitude * np.sin(np.arange(frames)[:, None] * (2 * np.pi * 440 / 44100)), 44100)


def reaction_fixture(folder, payload=None, pools=None):
    """One WAV per exported clip of the sidecar (all the same canonical payload) written into `folder`; returns the manifest's `reactions` object, built
    by the extractor's own reaction_object from the clip records (synthetic: no Arena audio)."""
    payload = tone() if payload is None else payload
    pools = audio.load_pools() if pools is None else pools
    rows = {}
    for index, name in enumerate(audio.exported_clips(pools), start=1):
        row = audio.clip_record(name, payload)
        row.update(source_id=1000 + index, processing=dict(original_rate=44100, original_duration=0.5, original_peak=.08, original_rms=.0566, gain_db=0.0))
        (Path(folder) / row['file']).write_bytes(payload)
        rows[name] = row
    return audio.reaction_object(rows, pools)


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
        # the manifest of the calls stays Participating / 1123; Cheer / 1119 and the other reaction banks live in its `reactions` object
        # (ReactionSetTests: Cheer is the first bank of a valid reaction set and a reaction bank with another trigger is refused)
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

    def test_a_reaction_named_file_is_extra_unless_the_manifest_lists_it(self):
        # a reaction clip is no longer a name the validator refuses, but the file set stays exact: no `reactions` object, no such file
        (self.folder / 'arena_fan11_kill_cheer_01.wav').write_bytes(tone())
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

    def test_the_bed_is_mono_shorter_by_the_crossfade_and_carries_the_constant_gain(self):
        source = stereo_source()
        payload, processing = audio.process_bed(audio.write_wav(source, 44100))
        result, rate = audio.read_wav(payload)
        fade = 22050
        self.assertEqual((result.shape[1], rate), (1, 44100))
        self.assertEqual(len(result), len(source) - fade)
        self.assertEqual(processing['crossfade_frames'], fade)
        self.assertEqual(processing['original_frames'], len(source))
        self.assertEqual(processing['original_channels'], 2)
        self.assertEqual(processing['gain_db'], audio.BED_GAIN_DB)
        # past the crossfade the loop is the mean of the channels times the constant gain (within the PCM16 rounding)
        expected = source.mean(axis=1)[fade:len(source) - fade] * 10 ** (audio.BED_GAIN_DB / 20)
        self.assertLess(np.max(np.abs(result[fade:, 0] - expected)), 2 / 32767)
        # equal power: noise that is not correlated head to tail keeps its level through the faded stretch
        for window in (slice(0, fade // 2), slice(fade // 2, fade)):
            ratio = np.sqrt(np.mean(result[window] ** 2)) / np.sqrt(np.mean(result[fade:] ** 2))
            self.assertLess(abs(20 * np.log10(ratio)), 1.0)

    def test_the_gain_keeps_the_level_the_user_approved(self):
        # 2026-11-02: the neutral bed after the downmix measures -31.17 dBFS RMS, the approved postmatch bed -40.03 dBFS: -8.9 dB keeps the loudness
        self.assertEqual(audio.BED_GAIN_DB, -8.9)
        self.assertLess(abs(-31.17 + audio.BED_GAIN_DB - -40.03), 0.1)
        self.assertEqual((audio.BED_NAME, audio.BED_BANK, audio.BED_TRIGGER), ('arena_fans_ambient_neutral', 'Neutral', 1122))
        self.assertEqual(audio.RETIRED_BED_FILES, ('arena_fans_ambient_postmatch.wav',))
        self.assertNotIn(audio.BED_FILE, audio.RETIRED_BED_FILES)
        # the 80 s source minus the crossfade fits the frame window, and the client's 10-120 s window holds it too
        self.assertLessEqual(80 * 44100 - 22050, audio.BED_MAX_FRAMES)
        self.assertLessEqual(audio.BED_MAX_FRAMES, 120 * 44100)

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
        for key, value in [('crossfade_frames', 11025), ('original_rate', 22050), ('original_channels', 6), ('original_frames', 1), ('downmix', 'left'),
                           ('gain_db', 0.0)]:
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
            original_rms=.03, downmix='mean', gain_db=audio.BED_GAIN_DB, crossfade_frames=22050))
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
        # the bed streams from a CAB resource inside ambient_voice.bundle; neither name can be a key-source file (sharedassets*.resS/.resource)
        self.assertEqual(Path(audio.BED_SOURCE).name, 'ambient_voice.bundle')
        self.assertNotIn(Path(audio.BED_SOURCE).name, audio.protected_names())
        self.assertTrue(all(name.startswith('sharedassets') for name in audio.protected_names()))

    @unittest.skipUnless((audio.SOURCE / audio.BED_SOURCE).is_file(), 'Arena is not installed')
    def test_the_installed_arena_gives_the_neutral_banks_one_bed(self):
        reader, wav = audio.find_bed(audio.SOURCE)
        samples, rate = audio.read_wav(wav)
        self.assertEqual((samples.shape[1], rate), (2, 44100))
        self.assertLess(abs(len(samples) / rate - 80.0), 0.01)
        stream = reader.read_typetree()['m_Resource']['m_Source']
        self.assertTrue(stream.startswith('archive:/CAB-') and stream.endswith('.resource'), stream)


class ReactionSetTests(unittest.TestCase):
    """The manifest's optional `reactions` object (phase A of docs/PLAN-CROWD-REACTIONS.md): 70 synthetic clips for the sidecar's `keep` set, the
    calls beside them. The real sidecar is used on purpose: its counts are what the banks' `embedded == exported + left out` rule runs against."""

    @classmethod
    def setUpClass(cls):
        cls.pools = audio.load_pools()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        payload = tone(44100)
        rows = []
        for i in range(13):
            row = audio.clip_record(f'arena_fan7_participating_{i:02}', payload)
            (self.folder / row['file']).write_bytes(payload)
            rows.append(row)
        self.manifest = dict(schema=1, bank='Participating', trigger=1123, source_bundle=audio.BUNDLE, external_entries_omitted=9, clips=rows)
        self.manifest['reactions'] = reaction_fixture(self.folder, pools=self.pools)
        self.reactions = self.manifest['reactions']
        self.save()

    def save(self):
        (self.folder / audio.MANIFEST).write_text(json.dumps(self.manifest), encoding='utf-8')

    def reject(self, message=None, **kwargs):
        self.save()
        with self.assertRaisesRegex((ValueError, FileNotFoundError), message or ''):
            audio.validate(self.folder, require_reactions=True, **kwargs)

    def clip(self, name):
        return next(row for row in self.reactions['clips'] if row['name'] == name)

    def bank(self, name):
        return next(row for row in self.reactions['banks'] if row['bank'] == name)

    def test_the_complete_set_validates_and_is_required_when_asked(self):
        self.save()
        data = audio.validate(self.folder, require_reactions=True)
        self.assertEqual(len(data['reactions']['clips']), 70)
        self.assertEqual([b['bank'] for b in data['reactions']['banks']], ['Cheer', 'Owned', 'Angry', 'ChangedRound', 'EndRound', 'Hide'])
        self.assertEqual(len(audio.validate(self.folder)['clips']), 13)
        self.assertEqual(len(list(self.folder.iterdir())), 1 + 13 + 70)
        # without the object the 70 files are unlisted extras; without them the calls alone are a valid package that lacks the set
        del self.manifest['reactions']
        self.save()
        with self.assertRaisesRegex(ValueError, 'unexpected spectator audio files'):
            audio.validate(self.folder)
        for path in list(self.folder.iterdir()):
            if audio.REACTION_FILE.fullmatch(path.name):
                path.unlink()
        self.assertNotIn('reactions', audio.validate(self.folder))
        self.reject('the reaction set is missing')

    def test_phase_a_exports_the_keep_clips_of_six_banks_once_each(self):
        counts = {b['bank']: (b['embedded'], len(b['clips']), len(b['left_out'])) for b in self.reactions['banks']}
        self.assertEqual(counts, {'Cheer': (14, 10, 4), 'Owned': (16, 12, 4), 'Angry': (18, 14, 4), 'ChangedRound': (9, 6, 3),
                                  'EndRound': (11, 9, 2), 'Hide': (36, 29, 7)})
        union = {name for b in self.reactions['banks'] for name in b['clips']}
        self.assertEqual(union, {row['name'] for row in self.reactions['clips']})
        self.assertEqual(len(union), 70)
        # Cheer and Owned share 10 of their kept names: one table row, listed in both banks
        self.assertEqual(len(set(self.bank('Cheer')['clips']) & set(self.bank('Owned')['clips'])), 10)
        self.assertEqual([row['name'] for row in self.reactions['clips']], sorted(union))
        self.assertEqual({row['pool'] for row in self.reactions['clips']},
                         {'praise', 'gloat', 'shout', 'lament', 'heckle', 'start', 'finish', 'panic', 'directed'})

    def test_a_missing_or_renamed_wav_is_refused(self):
        name = self.reactions['clips'][0]['name']
        (self.folder / (name + '.wav')).rename(self.folder / 'arena_fan7_kill_cheer_99.wav')
        self.reject('missing/oversized reaction clip')

    def test_an_unlisted_wav_is_refused_even_when_it_looks_like_a_reaction(self):
        (self.folder / 'arena_fan7_kill_cheer_99.wav').write_bytes(tone())
        self.reject('unexpected spectator audio files')

    def test_a_left_out_clip_as_a_file_is_refused(self):
        left = self.bank('Cheer')['left_out'][0]['name']
        (self.folder / (left + '.wav')).write_bytes(tone())
        self.reject('unexpected spectator audio files')
        (self.folder / (left + '.wav')).unlink()
        # listed as an exported clip as well: a bank may not export what it leaves out
        self.bank('Cheer')['clips'] = sorted(self.bank('Cheer')['clips'] + [left])
        self.reject('left-out clips are duplicated or also exported|not in the clip table')

    def test_a_hash_or_a_byte_that_does_not_match_is_refused(self):
        row = self.clip(self.reactions['clips'][3]['name'])
        before = row['sha256']
        row['sha256'] = '0' * 64
        self.reject('sha256 mismatch')
        row['sha256'] = before
        path = self.folder / row['file']
        data = bytearray(path.read_bytes())
        data[-1] ^= 0x80
        path.write_bytes(data)
        self.reject('sha256 mismatch')

    def test_a_clip_record_must_match_its_file_and_stay_canonical(self):
        row = self.reactions['clips'][0]
        for key, value in [('frames', row['frames'] + 1), ('bytes', row['bytes'] + 2), ('channels', 2), ('sample_rate', 22050),
                           ('peak', row['peak'] * 1.01), ('rms', row['rms'] * 1.01), ('file', '../clip.wav'), ('name', '../escape'),
                           ('source_id', '5'), ('source_id', True), ('processing', None), ('pool', 'lament')]:
            with self.subTest(key=key, value=value):
                before = row[key]
                row[key] = value
                self.reject()
                row[key] = before
        for key in ('pool', 'group', 'processing'):
            with self.subTest(missing=key):
                before = row.pop(key)
                self.reject('invalid reaction clip record')
                row[key] = before
        row['text'] = 'a transcript must never fit'
        self.reject('invalid reaction clip record')
        del row['text']
        row['processing']['gain_db'] = 6.5     # the normalisation never boosts by more than 6 dB
        self.reject('processing record mismatch')

    def test_a_pool_tag_must_belong_to_the_banks_of_the_clip(self):
        cheer = next(row for row in self.reactions['clips'] if row['pool'] == 'praise')
        for pool in ('lament', 'panic', 'start', 'finish', 'lull', 'hype', 'nonsense'):
            with self.subTest(pool=pool):
                before = cheer['pool']
                cheer['pool'] = pool
                self.reject('does not belong to')
                cheer['pool'] = before
        hide = next(row for row in self.reactions['clips'] if row['pool'] == 'panic')
        hide['pool'] = 'directed'                    # a tag of the right bank but not the clip's own: only the sidecar can tell
        self.reject('differ from reaction_pools.json')
        hide['pool'] = 'panic'
        cheer['group'] = 'takecover-01'
        self.reject('differ from reaction_pools.json')

    def test_bank_counts_provenance_and_order_must_match_the_table(self):
        cheer = self.bank('Cheer')
        for key, value in [('embedded', 15), ('trigger', 1120), ('external_entries_omitted', 14), ('null_entries', 1), ('extra', 1)]:
            with self.subTest(key=key):
                before = cheer.get(key)
                cheer[key] = value
                self.reject('bank provenance mismatch')
                if before is None:
                    del cheer[key]
                else:
                    cheer[key] = before
        self.reactions['banks'].reverse()
        self.reject('reaction banks differ from the extractor table')
        self.reactions['banks'].reverse()
        self.reactions['banks'].pop()
        self.reject('reaction banks differ from the extractor table')

    def test_a_bank_that_loses_a_clip_without_recording_it_does_not_add_up(self):
        angry = self.bank('Angry')
        lost = angry['clips'].pop()
        self.reject('do not add up')
        # recorded as left out the numbers add up, but the clip then sits in the table without a bank
        angry['left_out'].append(dict(name=lost, status='listen', reason='FLAG'))
        self.reject('not listed in any bank')
        # a clip of the table that no bank lists, and a bank that lists a name the table lacks
        angry['left_out'].pop()
        angry['clips'].append(lost)
        angry['clips'].append('arena_fan9_kill_angry_77')
        self.reject('not in the clip table')

    def test_left_out_records_need_a_known_status_and_a_reason_that_fits(self):
        entry = next(e for e in self.bank('Hide')['left_out'] if e['status'] == 'listen')
        for key, value in [('status', 'keep'), ('status', 'maybe'), ('reason', 'SLUR'), ('reason', None), ('reason', 'transcript')]:
            with self.subTest(key=key, value=value):
                before = entry[key]
                entry[key] = value
                self.reject('invalid left-out record')
                entry[key] = before
        entry['text'] = 'free text'
        self.reject('invalid left-out record')

    def test_names_must_be_unique_sorted_and_of_the_phase_a_categories(self):
        rows = self.reactions['clips']
        rows.append(dict(rows[0]))
        self.reject('duplicated or not sorted')
        rows.pop()
        rows.reverse()
        self.reject('duplicated or not sorted')
        rows.reverse()
        for name in ('arena_fan7_participating_77', 'arena_fan12_sweep_team_03', 'arena_fan12_low_death_intensity_03', 'arena_fans_ambient_neutral',
                     'arena_fan7_kill_cheer_1', 'arena_fan7_kill_cheer_001', 'arena_fan7_cheer_01'):
            with self.subTest(name=name):
                before = rows[0]['name']
                rows[0]['name'] = name
                rows[0]['file'] = name + '.wav'
                self.reject('invalid reaction clip name')
                rows[0]['name'] = before
                rows[0]['file'] = before + '.wav'
        self.assertEqual(sum(1 for row in rows if audio.REACTION_NAME.fullmatch(row['name'])), 70)

    def test_a_clip_outside_the_half_second_to_seven_second_window_is_refused(self):
        name = self.reactions['clips'][5]['name']
        for frames, ok in ((audio.REACTION_MAX_FRAMES, True), (audio.REACTION_MAX_FRAMES + 1, False), (audio.REACTION_MIN_FRAMES - 1, False)):
            with self.subTest(frames=frames):
                payload = tone(frames)
                (self.folder / (name + '.wav')).write_bytes(payload)
                row = self.clip(name)
                row.update(audio.clip_record(name, payload))
                if ok:
                    self.save()
                    audio.validate(self.folder, require_reactions=True)
                else:
                    self.reject('missing/oversized reaction clip|expected canonical mono 44.1 kHz PCM16 WAV of 0.5-7 s')
        self.assertEqual(audio.REACTION_MAX_FRAMES, 308700)

    def test_silent_and_over_loud_clips_are_refused(self):
        name = self.reactions['clips'][2]['name']
        for amplitude in (.0005, .8):
            with self.subTest(amplitude=amplitude):
                payload = tone(amplitude=amplitude)
                (self.folder / (name + '.wav')).write_bytes(payload)
                self.clip(name).update(audio.clip_record(name, payload))
                self.reject('silent/over-loud reaction clip')

    def test_the_whole_object_is_pinned(self):
        for key, value in [('schema', 2), ('source_bundle', 'other.bundle'), ('max_frames', 220500), ('max_frames', '308700'), ('extra', 1)]:
            with self.subTest(key=key):
                before = self.reactions.get(key)
                self.reactions[key] = value
                self.reject('unsupported reaction set')
                if before is None:
                    del self.reactions[key]
                else:
                    self.reactions[key] = before
        for key in ('banks', 'clips'):
            with self.subTest(key=key):
                before = self.reactions[key]
                self.reactions[key] = None
                self.reject('unsupported reaction set')
                self.reactions[key] = before

    def test_the_sidecar_decides_what_ships(self):
        # promoting a `listen` clip changes the exported set: the old manifest no longer matches until the extractor runs again
        promoted = copy.deepcopy(self.pools)
        promoted['clips']['arena_fan9_kill_cheer_01']['status'] = 'keep'
        del promoted['clips']['arena_fan9_kill_cheer_01']['reason']
        self.save()
        with self.assertRaisesRegex(ValueError, 'differ from reaction_pools.json'):
            audio.validate(self.folder, require_reactions=True, pools=promoted)
        # the manifest an extraction of that sidecar would write is valid, with the promoted clip in both of its banks
        with tempfile.TemporaryDirectory() as other:
            folder = Path(other)
            manifest = copy.deepcopy(self.manifest)
            manifest['reactions'] = reaction_fixture(folder, pools=promoted)
            for row in manifest['clips']:
                (folder / row['file']).write_bytes(tone(44100))
            (folder / audio.MANIFEST).write_text(json.dumps(manifest), encoding='utf-8')
            data = audio.validate(folder, require_reactions=True, pools=promoted)
        self.assertEqual(len(data['reactions']['clips']), 71)
        self.assertEqual([len(b['clips']) for b in data['reactions']['banks']], [11, 13, 14, 6, 9, 29])

    def test_a_damaged_reaction_set_refuses_the_whole_package_here(self):
        # the validator is strict about everything it is given; that a damaged set leaves the calls working is the client's rule
        # (SpectatorAudioManifest.ValidateReactions is independent of Validate), not this module's
        self.clip(self.reactions['clips'][0]['name'])['sha256'] = '0' * 64
        self.save()
        with self.assertRaisesRegex(ValueError, 'sha256 mismatch'):
            audio.validate(self.folder)
        with self.assertRaisesRegex(ValueError, 'sha256 mismatch'):
            audio.validate(self.folder, require_reactions=True)


class StaleReactionFileTests(unittest.TestCase):
    """extract() deletes a reaction WAV that is no longer exported (demoted in the sidecar), and nothing else."""

    def test_only_unlisted_files_that_match_the_reaction_pattern_are_deleted(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            keep = {'arena_fan9_kill_angry_01'}
            stale = ['arena_fan7_kill_cheer_01.wav', 'arena_fan15_kill_cheer_02.wav', 'arena_fan12_shoot_hide_05.wav', 'arena_fan8_aim_hide_02.wav']
            untouched = ['arena_fan9_kill_angry_01.wav', 'arena_fan7_participating_01.wav', 'arena_fans_ambient_neutral.wav', 'spectator-audio.json',
                         'arena_fan7_kill_cheer_01.wav.bak', 'arena_fan7_kill_cheer_1.wav', 'arena_fan7_sweep_team_01.wav', 'notes.txt']
            for name in stale + untouched:
                (folder / name).write_bytes(b'x')
            (folder / 'arena_fan7_kill_cheer_02.wav').mkdir()      # a directory with a matching name is not a file
            audio.remove_stale_reactions(folder, keep)
            self.assertEqual(sorted(p.name for p in folder.iterdir()), sorted(untouched + ['arena_fan7_kill_cheer_02.wav']))


def fake_bundle(pools, stream='archive:/CAB-0123/CAB-0123.resource'):
    """The six reaction banks of fan_speaker_voice.bundle as UnityPy would hand them to find_reactions: (object, typetree) pairs whose objects reach
    their AudioClips through assets_file.objects. Each bank holds the sidecar's clips of that bank, then its external slots and its null slots."""
    assets = types.SimpleNamespace(objects={})
    ids, banks = {}, []
    for bank, trigger, embedded, external, null in audio.REACTION_BANKS:
        slots = []
        for name in sorted(n for n, r in pools['clips'].items() if bank in r['banks']):
            if name not in ids:
                ids[name] = 100 + len(ids)
                assets.objects[ids[name]] = types.SimpleNamespace(
                    path_id=ids[name], type=types.SimpleNamespace(name='AudioClip'),
                    read_typetree=lambda name=name: {'m_Name': name, 'm_Resource': {'m_Source': stream, 'm_Offset': 0, 'm_Size': 1}})
            slots.append({'Clip': {'m_FileID': 0, 'm_PathID': ids[name]}})
        slots += [{'Clip': {'m_FileID': 1, 'm_PathID': 7 + i}} for i in range(external)]
        slots += [{'Clip': {'m_FileID': 0, 'm_PathID': 0}} for _ in range(null)]
        banks.append((types.SimpleNamespace(assets_file=assets), {'m_Name': bank, 'Trigger': trigger, 'Clips': slots, 'SpreadGroups': [{'Clips': slots}]}))
    banks.append((types.SimpleNamespace(assets_file=assets), {'m_Name': 'Fan_Speaker_Voice'}))     # a MonoBehaviour that is no bank
    return banks, assets


class FindReactionsTests(unittest.TestCase):
    """find_reactions, the extractor's reading of the bundle, on stand-in banks: the layout, membership and key-source rules fail closed."""

    @classmethod
    def setUpClass(cls):
        cls.pools = audio.load_pools()

    def find(self, banks, pools=None):
        return audio.find_reactions(banks, self.pools if pools is None else pools)

    def test_the_layout_of_the_installed_arena_is_accepted_and_gives_the_keep_clips(self):
        banks, assets = fake_bundle(self.pools)
        clips, trees = self.find(banks)
        self.assertEqual(set(clips), set(audio.exported_clips(self.pools)))
        self.assertEqual(len(clips), 70)
        self.assertEqual(set(trees), set(clips))
        # a `listen` or `out` clip is read for the membership check and never handed on
        for name in ('arena_fan9_kill_cheer_01', 'arena_fan8_aim_hide_01', 'arena_fan12_shoot_hide_05'):
            self.assertNotIn(name, clips)
        # Cheer and Owned reach their 13 shared names through one object each: 17 + 18 + 9 + 11 + 36 distinct clips in the six banks (89 usable + 2 out)
        self.assertEqual(len(assets.objects), 91)

    def test_the_table_matches_the_measured_layout(self):
        self.assertEqual([(b, t, e, x, n) for b, t, e, x, n in audio.REACTION_BANKS],
                         [('Cheer', 1119, 14, 15, 0), ('Owned', 1120, 16, 3, 0), ('Angry', 1121, 18, 23, 2), ('ChangedRound', 1125, 9, 5, 0),
                          ('EndRound', 1126, 11, 8, 0), ('Hide', 1124, 36, 14, 0)])
        self.assertEqual([e + x + n for _, _, e, x, n in audio.REACTION_BANKS], [29, 19, 43, 14, 19, 50])

    def test_a_missing_or_duplicated_bank_is_refused(self):
        banks, _ = fake_bundle(self.pools)
        with self.assertRaisesRegex(ValueError, 'Angry bank is missing or ambiguous'):
            self.find([row for row in banks if row[1].get('m_Name') != 'Angry'])
        with self.assertRaisesRegex(ValueError, 'Angry bank is missing or ambiguous'):
            self.find(banks + [row for row in banks if row[1].get('m_Name') == 'Angry'])

    def test_a_changed_bank_layout_is_refused(self):
        for label, change in (('trigger', lambda t: t.update(Trigger=1)),
                              ('two spread groups', lambda t: t.update(SpreadGroups=t['SpreadGroups'] * 2)),
                              ('no spread group', lambda t: t.update(SpreadGroups=[])),
                              ('spread group != clips', lambda t: t.update(SpreadGroups=[{'Clips': t['Clips'][:-1]}]))):
            with self.subTest(label):
                banks, _ = fake_bundle(self.pools)
                change(next(t for _, t in banks if t.get('m_Name') == 'Hide'))
                with self.assertRaisesRegex(ValueError, 'Hide bank layout changed'):
                    self.find(banks)

    def test_changed_slot_counts_are_refused(self):
        for label, change in (('external slot lost', lambda s: s.pop()), ('embedded slot lost', lambda s: s.pop(0)),
                              ('null slot added', lambda s: s.append({'Clip': {'m_FileID': 0, 'm_PathID': 0}})),
                              ('external slot added', lambda s: s.append({'Clip': {'m_FileID': 1, 'm_PathID': 9}})),
                              ('embedded slot twice', lambda s: s.__setitem__(slice(0, 2), [s[0], s[0]]))):
            with self.subTest(label):
                banks, _ = fake_bundle(self.pools)
                tree = next(t for _, t in banks if t.get('m_Name') == 'Angry')
                change(tree['Clips'])
                tree['SpreadGroups'] = [{'Clips': tree['Clips']}]
                with self.assertRaisesRegex(ValueError, 'Angry bank membership changed'):
                    self.find(banks)

    def test_names_outside_the_categories_or_the_sidecar_are_refused(self):
        banks, assets = fake_bundle(self.pools)
        clip = assets.objects[next(iter(assets.objects))]
        clip.read_typetree = lambda: {'m_Name': 'arena_fan7_sweep_team_01', 'm_Resource': {'m_Source': 'archive:/CAB-1/CAB-1.resource'}}
        with self.assertRaisesRegex(ValueError, 'unexpected reaction clip name'):
            self.find(banks)
        banks, assets = fake_bundle(self.pools)
        clip = assets.objects[next(iter(assets.objects))]
        clip.read_typetree = lambda: {'m_Name': 'arena_fan7_kill_cheer_99', 'm_Resource': {'m_Source': 'archive:/CAB-1/CAB-1.resource'}}
        with self.assertRaisesRegex(ValueError, 'is not in reaction_pools.json'):
            self.find(banks)

    def test_a_shared_name_must_be_one_object(self):
        banks, assets = fake_bundle(self.pools)
        twin = next(i for i, c in assets.objects.items() if c.read_typetree()['m_Name'] == 'arena_fan11_kill_cheer_01')
        assets.objects[9999] = types.SimpleNamespace(path_id=9999, type=assets.objects[twin].type, read_typetree=assets.objects[twin].read_typetree)
        owned = next(t for _, t in banks if t.get('m_Name') == 'Owned')
        for slot in owned['Clips']:
            if slot['Clip']['m_PathID'] == twin:
                slot['Clip']['m_PathID'] = 9999
        with self.assertRaisesRegex(ValueError, 'two different clips share the name'):
            self.find(banks)

    def test_a_reference_that_is_not_audio_is_refused(self):
        banks, assets = fake_bundle(self.pools)
        assets.objects[next(iter(assets.objects))].type = types.SimpleNamespace(name='Texture2D')
        with self.assertRaisesRegex(ValueError, 'a reaction reference is not audio'):
            self.find(banks)

    def test_the_sidecar_must_name_exactly_the_banks_of_the_bundle(self):
        banks, _ = fake_bundle(self.pools)
        narrowed = copy.deepcopy(self.pools)
        narrowed['clips']['arena_fan11_kill_cheer_01']['banks'] = ['Cheer']
        with self.assertRaisesRegex(ValueError, 'banks in reaction_pools.json differ from the bundle'):
            self.find(banks, narrowed)
        extended = copy.deepcopy(self.pools)
        extended['clips']['arena_fan7_aim_hide_09'] = dict(banks=['Hide'], pool='panic', group='takecover-30', status='keep')
        with self.assertRaisesRegex(ValueError, 'is in reaction_pools.json but not in the bundle'):
            self.find(banks, extended)

    def test_a_clip_that_would_ship_may_not_stream_from_a_key_source_file(self):
        protected = sorted(audio.protected_names())
        self.assertTrue(protected and all(name.startswith('sharedassets') for name in protected))
        for stream in (f'archive:/CAB-1/{protected[0]}', f'D:/Arena/EscapeFromTarkovArena_Data/{protected[-1]}', protected[len(protected) // 2]):
            with self.subTest(stream=stream):
                banks, _ = fake_bundle(self.pools, stream=stream)
                with self.assertRaisesRegex(ValueError, 'streams from a protected key source'):
                    self.find(banks)
        # a clip without a stream (data inside the asset) and the bundle's own CAB resource are fine
        for stream in ('', None, 'archive:/CAB-e279417c724aa980200dc285379c3d3a/CAB-e279417c724aa980200dc285379c3d3a.resource'):
            with self.subTest(stream=stream):
                banks, _ = fake_bundle(self.pools, stream=stream)
                self.assertEqual(len(self.find(banks)[0]), 70)

    def test_a_clip_that_is_left_out_is_not_checked_for_its_stream(self):
        # only what would ship is guarded: a left-out clip is read for its name and never decoded or written
        banks, assets = fake_bundle(self.pools)
        protected = sorted(audio.protected_names())[0]
        for clip in assets.objects.values():
            name = clip.read_typetree()['m_Name']
            if self.pools['clips'][name]['status'] != 'keep':
                clip.read_typetree = lambda name=name: {'m_Name': name, 'm_Resource': {'m_Source': f'archive:/CAB-1/{protected}'}}
        self.assertEqual(len(self.find(banks)[0]), 70)

    def test_the_bundle_itself_is_not_a_key_source(self):
        self.assertNotIn(Path(audio.BUNDLE).name, audio.protected_names())


class PoolSidecarCheckTests(unittest.TestCase):
    """check_pools: the closed vocabularies of the sidecar. No free-text field can pass, a clip that is out never ships."""

    @classmethod
    def setUpClass(cls):
        cls.pools = audio.load_pools()

    def reject(self, mutate, message):
        doc = copy.deepcopy(self.pools)
        mutate(doc)
        with self.assertRaisesRegex(ValueError, message):
            audio.check_pools(doc)

    def test_the_tracked_sidecar_passes(self):
        self.assertIs(audio.check_pools(self.pools), self.pools)

    def test_the_top_level_is_pinned(self):
        self.reject(lambda d: d.update(schema=2), 'unsupported reaction pool sidecar')
        self.reject(lambda d: d.update(transcript='x'), 'unsupported reaction pool sidecar')
        self.reject(lambda d: d.pop('about'), 'unsupported reaction pool sidecar')
        # `about` is the one free string of the file: it is pinned by its hash, so a pasted line (or any edit) is refused until the pin is edited on purpose
        self.reject(lambda d: d.update(about=d['about'] + ' x'), 'unsupported reaction pool sidecar')
        self.reject(lambda d: d.update(about='any transcript line'), 'unsupported reaction pool sidecar')
        self.reject(lambda d: d.update(about=''), 'unsupported reaction pool sidecar')
        self.reject(lambda d: d.update(about=5), 'unsupported reaction pool sidecar')
        self.reject(lambda d: d.update(exported_banks=d['exported_banks'][:-1]), 'exports other banks')
        self.reject(lambda d: d.update(exported_banks=d['exported_banks'][::-1]), 'exports other banks')
        self.reject(lambda d: d.update(exported_status=['out']), 'never out')
        self.reject(lambda d: d.update(exported_status=['keep', 'out']), 'never out')
        self.reject(lambda d: d.update(exported_status=[]), 'never out')
        self.reject(lambda d: d.update(exported_status=['keep', 'keep']), 'never out')
        self.reject(lambda d: d.update(clips={}), 'holds no clips')
        audio.check_pools({**self.pools, 'exported_status': ['keep', 'listen']})

    def test_a_row_may_only_hold_the_known_keys(self):
        name = 'arena_fan9_kill_cheer_01'
        self.reject(lambda d: d['clips'][name].update(text='a line'), 'unexpected keys')
        self.reject(lambda d: d['clips'][name].update(note='a line'), 'unexpected keys')
        self.reject(lambda d: d['clips'][name].pop('group'), 'unexpected keys')
        self.reject(lambda d: d['clips'][name].pop('reason'), 'unexpected keys')       # a listen clip without its reason code
        keep = 'arena_fan11_kill_cheer_01'
        self.reject(lambda d: d['clips'][keep].update(reason='FLAG'), 'unexpected keys')   # a kept clip carries no reason

    def test_vocabularies_are_closed(self):
        name = 'arena_fan9_kill_cheer_01'
        self.reject(lambda d: d['clips'][name].update(status='maybe'), 'invalid status')
        self.reject(lambda d: d['clips'][name].update(reason='SLUR'), 'invalid reason')        # a listen clip with an out reason
        self.reject(lambda d: d['clips'][name].update(reason='free text'), 'invalid reason')
        self.reject(lambda d: d['clips'][name].update(pool='lament'), 'pool tag does not belong')
        self.reject(lambda d: d['clips'][name].update(pool='free text'), 'pool tag does not belong')
        self.reject(lambda d: d['clips'][name].update(group='free text'), 'sentence group does not fit')
        self.reject(lambda d: d['clips'][name].update(group='takecover-01'), 'sentence group does not fit')
        self.reject(lambda d: d['clips'][name].update(group='approve-1'), 'sentence group does not fit')
        self.reject(lambda d: d['clips'][name].update(group=None), 'sentence group does not fit')
        self.reject(lambda d: d['clips'][name].update(banks=['Hide']), 'banks do not fit the clip name')
        self.reject(lambda d: d['clips'][name].update(banks=['Owned', 'Cheer']), 'banks do not fit the clip name')
        self.reject(lambda d: d['clips'][name].update(banks=[]), 'banks do not fit the clip name')
        self.reject(lambda d: d['clips'].update({'arena_fan7_kill_cheer_1': copy.deepcopy(d['clips'][name])}), 'invalid clip name')
        self.reject(lambda d: d['clips'].update({'arena_fan7_roar_01': copy.deepcopy(d['clips'][name])}), 'invalid clip name')

    def test_a_clip_that_is_out_has_no_pool_and_no_group(self):
        out = 'arena_fan8_aim_hide_01'
        self.reject(lambda d: d['clips'][out].update(pool='panic'), 'has no pool and no group')
        self.reject(lambda d: d['clips'][out].update(group='takecover-01'), 'has no pool and no group')
        self.reject(lambda d: d['clips'][out].update(reason='FLAG'), 'invalid reason')


if __name__ == '__main__':
    unittest.main()
