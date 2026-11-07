"""Pins of the crowd reaction pool sidecar tools/extract/reaction_pools.json (docs/PLAN-CROWD-REACTIONS.md 2.4 and 4.5) and of the bank layout of the
installed Arena's fan_speaker_voice.bundle that it describes.

The first class is offline: counts, the clips that are left out and why, and the closed vocabulary (names, tags and codes; no field can hold a
transcript). Promoting a `listen` clip after listening to it changes this file's pins on purpose: edit the sidecar, the pinned set below and run
`python tools/extract/extract_spectator_audio.py extract` again. The second class reads the installed Arena and the extractor's ignored output under
work/extract/spectator-audio and skips without them, like the other real-data tests."""
import collections
import hashlib
import json
from pathlib import Path
import re
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import extract_spectator_audio as audio

REPO = Path(__file__).resolve().parents[2]
SIX = ('Cheer', 'Owned', 'Angry', 'ChangedRound', 'EndRound', 'Hide')
# bank -> (embedded clips of the bank, of them `keep`)
BANK_COUNTS = {'Cheer': (14, 10), 'Owned': (16, 12), 'Angry': (18, 14), 'ChangedRound': (9, 6), 'EndRound': (11, 9), 'Hide': (36, 29),
               'LowDeathIntensity': (15, 11), 'PurifiedTeam': (16, 5)}
# The `listen` clips of the six phase-A banks (left out until the author has listened) with their reason codes: 19.
LISTEN = {
    'arena_fan10_kill_cheer_01': 'FLAG', 'arena_fan10_match_end_02': 'FLAG', 'arena_fan11_match_start_01': 'TASTE',
    'arena_fan12_kill_angry_08': 'FLAG', 'arena_fan12_kill_angry_12': 'FLAG', 'arena_fan12_kill_cheer_10': 'FLAG',
    'arena_fan12_match_end_02': 'FLAG', 'arena_fan12_match_start_04': 'CTX', 'arena_fan12_match_start_05': 'FLAG',
    'arena_fan12_shoot_hide_06': 'TASTE', 'arena_fan13_aim_hide_01': 'CTX', 'arena_fan13_kill_angry_02': 'FLAG',
    'arena_fan13_kill_angry_06': 'FLAG', 'arena_fan13_kill_cheer_03': 'ROLE', 'arena_fan13_shoot_hide_07': 'TASTE',
    'arena_fan13_shoot_hide_08': 'TASTE', 'arena_fan15_kill_cheer_02': 'ROLE', 'arena_fan8_aim_hide_02': 'TASTE',
    'arena_fan9_kill_cheer_01': 'FLAG'}
# The `listen` clips of LowDeathIntensity and PurifiedTeam (banks without a kind in phase A): 10.
LISTEN_LATER = {
    'arena_fan11_low_death_intensity_03': 'FLAG', 'arena_fan11_sweep_team_02': 'SIDED', 'arena_fan12_low_death_intensity_03': 'FLAG',
    'arena_fan12_sweep_team_03': 'SIDED', 'arena_fan12_sweep_team_10': 'SIDED', 'arena_fan12_sweep_team_12': 'SIDED',
    'arena_fan13_low_death_intensity_03': 'FLAG', 'arena_fan13_sweep_team_04': 'SIDED', 'arena_fan8_sweep_team_03': 'SIDED',
    'arena_fan9_sweep_team_02': 'SIDED'}
# The clips that are out for good: 7 (SLUR 2, UNCL 1, WRONG 4).
OUT = {
    'arena_fan10_sweep_team_02': 'WRONG', 'arena_fan12_low_death_intensity_04': 'UNCL', 'arena_fan12_shoot_hide_05': 'SLUR',
    'arena_fan12_sweep_team_11': 'WRONG', 'arena_fan13_sweep_team_02': 'WRONG', 'arena_fan7_sweep_team_01': 'WRONG',
    'arena_fan8_aim_hide_01': 'SLUR'}
# pool tag -> (kept clips, distinct sentence groups among them) over the six phase-A banks
KEPT_POOLS = {'praise': (6, 5), 'gloat': (4, 4), 'shout': (2, 2), 'urge': (0, 0), 'lament': (10, 9), 'heckle': (4, 3), 'start': (6, 5),
              'finish': (9, 9), 'panic': (12, 6), 'directed': (17, 12)}


def sidecar():
    return json.loads(audio.REACTION_POOLS.read_text(encoding='utf-8'))


class SidecarPinTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.doc = sidecar()
        cls.clips = cls.doc['clips']

    def in_banks(self, banks):
        return {name: row for name, row in self.clips.items() if set(row['banks']) & set(banks)}

    def test_the_file_passes_the_extractors_own_structure_check(self):
        self.assertIs(audio.check_pools(self.doc), self.doc)
        self.assertEqual(self.doc['schema'], 1)
        self.assertEqual(self.doc['exported_banks'], list(SIX))
        self.assertEqual(self.doc['exported_status'], ['keep'])

    def test_122_names_and_the_counts_per_bank(self):
        self.assertEqual(len(self.clips), 122)
        for bank, (embedded, kept) in BANK_COUNTS.items():
            with self.subTest(bank=bank):
                rows = [row for row in self.clips.values() if bank in row['banks']]
                self.assertEqual((len(rows), sum(1 for row in rows if row['status'] == 'keep')), (embedded, kept))
        six = self.in_banks(SIX)
        self.assertEqual(len(six), 91)           # 17 (Cheer and Owned) + 18 + 9 + 11 + 36 distinct clips
        self.assertEqual(collections.Counter(row['status'] for row in six.values()), {'keep': 70, 'listen': 19, 'out': 2})
        self.assertEqual(collections.Counter(row['status'] for row in self.clips.values()), {'keep': 86, 'listen': 29, 'out': 7})
        # Cheer and Owned share 13 names: 13 with both banks, one Cheer-only and three Owned-only
        self.assertEqual(collections.Counter(tuple(row['banks']) for row in self.clips.values() if 'Cheer' in row['banks'] or 'Owned' in row['banks']),
                         {('Cheer', 'Owned'): 13, ('Cheer',): 1, ('Owned',): 3})

    def test_exactly_the_19_listen_and_the_7_out_clips_with_their_reasons(self):
        listen = {n: r['reason'] for n, r in self.clips.items() if r['status'] == 'listen' and set(r['banks']) & set(SIX)}
        later = {n: r['reason'] for n, r in self.clips.items() if r['status'] == 'listen' and not set(r['banks']) & set(SIX)}
        out = {n: r['reason'] for n, r in self.clips.items() if r['status'] == 'out'}
        self.assertEqual(listen, LISTEN)
        self.assertEqual(later, LISTEN_LATER)
        self.assertEqual(out, OUT)
        self.assertEqual((len(LISTEN), len(LISTEN_LATER), len(OUT)), (19, 10, 7))
        self.assertEqual(collections.Counter(OUT.values()), {'SLUR': 2, 'UNCL': 1, 'WRONG': 4})

    def test_what_ships_in_phase_a_is_the_70_kept_clips_of_the_six_banks(self):
        shipped = audio.exported_clips(self.doc)
        self.assertEqual(len(shipped), 70)
        self.assertTrue(all(row['status'] == 'keep' and set(row['banks']) & set(SIX) for row in shipped.values()))
        self.assertFalse(set(shipped) & set(LISTEN) | set(shipped) & set(OUT))
        # the later banks export nothing, whatever their status
        later_only = set(self.clips) - set(self.in_banks(SIX))
        self.assertEqual(len(later_only), 31)
        self.assertFalse(set(shipped) & later_only)
        self.assertTrue(all(audio.REACTION_NAME.fullmatch(name) for name in shipped))
        # a clip that is out is never exportable, whatever exported_status says
        widened = dict(self.doc, exported_status=['keep', 'listen'])
        self.assertEqual(len(audio.exported_clips(widened)), 89)
        self.assertFalse(set(audio.exported_clips(widened)) & set(OUT))

    def test_pool_and_sentence_group_counts_of_the_kept_clips(self):
        kept = [row for name, row in audio.exported_clips(self.doc).items()]
        by_pool = collections.defaultdict(list)
        for row in kept:
            by_pool[row['pool']].append(row['group'])
        for pool, (clips, groups) in KEPT_POOLS.items():
            with self.subTest(pool=pool):
                self.assertEqual((len(by_pool[pool]), len(set(by_pool[pool]))), (clips, groups))
        self.assertEqual(sum(clips for clips, _ in KEPT_POOLS.values()), 70)
        # Start has four voice families (fan7, fan11, fan12, fan13), two of them with one clip; the panic pool has six sentence groups
        families = collections.Counter(int(re.match(r'arena_fan(\d+)_', n).group(1)) for n, r in audio.exported_clips(self.doc).items() if r['pool'] == 'start')
        self.assertEqual(families, {7: 1, 11: 1, 12: 3, 13: 1})
        # one sentence group belongs to one pool
        owner = {}
        for row in self.clips.values():
            if row['group']:
                self.assertEqual(owner.setdefault(row['group'], row['pool']), row['pool'], row['group'])

    def test_no_row_holds_anything_but_names_tags_and_codes(self):
        # every key and every string of the file is from a closed vocabulary or a pattern: nothing in it can be a transcript or a translation
        self.assertEqual(set(self.doc), audio.POOL_KEYS)
        # ... except `about`, the one prose string, which is pinned by its hash (an edit of it is an edit of the pin and a review of the new text)
        self.assertEqual(hashlib.sha256(self.doc['about'].encode('utf-8')).hexdigest(), audio.POOL_ABOUT_SHA256)
        pools = {tag for tags in audio.BANK_POOL_TAGS.values() for tag in tags}
        reasons = {code for codes in audio.LEFT_OUT_REASONS.values() for code in codes}
        self.assertEqual(pools, {'praise', 'gloat', 'shout', 'urge', 'lament', 'heckle', 'start', 'finish', 'panic', 'directed', 'lull', 'hype', 'sided'})
        self.assertEqual(reasons, {'FLAG', 'ROLE', 'TASTE', 'CTX', 'SIDED', 'SLUR', 'UNCL', 'WRONG'})
        for name, row in self.clips.items():
            with self.subTest(name=name):
                self.assertTrue(audio.POOL_NAME.fullmatch(name), name)
                self.assertLessEqual(set(row), audio.POOL_ROW_KEYS | {'reason'})
                self.assertIn(row['status'], ('keep', 'listen', 'out'))
                self.assertTrue(set(row['banks']) <= set(audio.POOL_BANKS))
                self.assertIn(row['pool'], pools | {None})
                self.assertTrue(row['group'] is None or re.fullmatch(r'(?:approve|disapprove|start|finish|takecover|lull|purified)-\d{2}', row['group']))
                self.assertTrue(row.get('reason') is None or row['reason'] in reasons)
                self.assertEqual('reason' in row, row['status'] != 'keep')
        longest = max(len(v) for row in self.clips.values() for v in [row['pool'] or '', row['group'] or '', row['status'], row.get('reason', ''), *row['banks']])
        self.assertLessEqual(longest, 17)       # 'LowDeathIntensity'; a sentence would not fit

    def test_the_banks_and_tags_of_the_extractor_agree_with_the_plan(self):
        self.assertEqual([row[0] for row in audio.REACTION_BANKS], list(SIX))
        self.assertEqual(audio.POOL_BANKS, SIX + ('LowDeathIntensity', 'PurifiedTeam'))
        self.assertEqual(audio.BANK_POOL_TAGS['Cheer'], audio.BANK_POOL_TAGS['Owned'])
        self.assertEqual((audio.BANK_POOL_TAGS['Angry'], audio.BANK_POOL_TAGS['ChangedRound'], audio.BANK_POOL_TAGS['EndRound'], audio.BANK_POOL_TAGS['Hide']),
                         (('lament', 'heckle'), ('start',), ('finish',), ('panic', 'directed')))
        self.assertEqual(audio.REACTION_CATEGORIES, ('kill_cheer', 'kill_angry', 'match_start', 'match_end', 'aim_hide', 'shoot_hide'))
        self.assertEqual(audio.REACTION_MAX_FRAMES, 308700)
        # the exported names are exactly those of the phase-A categories; the later banks' names are not, so they can never reach the manifest
        later = [n for n, r in self.clips.items() if not set(r['banks']) & set(SIX)]
        self.assertEqual(len(later), 31)
        self.assertFalse(any(audio.REACTION_NAME.fullmatch(n) for n in later))


ARENA_BUNDLE = audio.SOURCE / audio.BUNDLE
WORK_AUDIO = REPO / 'work/extract/spectator-audio'


@unittest.skipUnless(ARENA_BUNDLE.is_file(), 'Arena is not installed')
class InstalledArenaTests(unittest.TestCase):
    """fan_speaker_voice.bundle of the installed Arena against the table, the sidecar and the key-source rule (measured 2026-11-02 on 0.6.1 and 0.6.2)."""

    @classmethod
    def setUpClass(cls):
        import UnityPy
        cls.pools = audio.load_pools()
        env = UnityPy.load(str(ARENA_BUNDLE))
        cls.banks = [(o, o.read_typetree()) for o in env.objects if o.type.name == 'MonoBehaviour']

    def bank(self, name):
        selected = [(o, t) for o, t in self.banks if t.get('m_Name') == name and 'Clips' in t]
        self.assertEqual(len(selected), 1, name)
        return selected[0]

    def members(self, name):
        reader, tree = self.bank(name)
        return [reader.assets_file.objects[s['Clip']['m_PathID']] for s in tree['Clips'] if s['Clip']['m_PathID'] and not s['Clip']['m_FileID']]

    def test_the_bank_layout_is_the_one_the_extractor_pins(self):
        later = (('LowDeathIntensity', 1116, 15, 0, 0), ('PurifiedTeam', 1127, 16, 0, 0))
        for bank, trigger, embedded, external, null in audio.REACTION_BANKS + later:
            with self.subTest(bank=bank):
                reader, tree = self.bank(bank)
                slots = tree['Clips']
                local = [s for s in slots if s['Clip']['m_PathID'] and not s['Clip']['m_FileID']]
                self.assertEqual((tree['Trigger'], len(slots), len(local)), (trigger, embedded + external + null, embedded))
                self.assertEqual(sum(1 for s in slots if s['Clip']['m_FileID']), external)
                self.assertEqual(sum(1 for s in slots if not s['Clip']['m_PathID']), null)
                self.assertEqual(len(tree['SpreadGroups']), 1)
                self.assertEqual(tree['SpreadGroups'][0]['Clips'], slots)

    def test_the_sidecar_names_exactly_the_embedded_clips_of_the_eight_banks(self):
        seen = collections.defaultdict(list)
        for bank in audio.POOL_BANKS:
            names = [clip.read_typetree()['m_Name'] for clip in self.members(bank)]
            self.assertEqual(len(names), len(set(names)), bank)
            for name in names:
                seen[name].append(bank)
        self.assertEqual(set(seen), set(self.pools['clips']))
        self.assertEqual(len(seen), 122)
        for name, row in self.pools['clips'].items():
            self.assertEqual(seen[name], row['banks'], name)

    def test_no_clip_of_the_eight_banks_streams_from_a_key_source_file(self):
        protected = audio.protected_names()
        self.assertNotIn(Path(audio.BUNDLE).name, protected)
        streams = set()
        for bank in audio.POOL_BANKS:
            for clip in self.members(bank):
                tree = clip.read_typetree()
                stream = Path((tree.get('m_Resource') or {}).get('m_Source') or '').name
                streams.add(stream)
                self.assertNotIn(stream, protected, tree['m_Name'])
        self.assertEqual(len(streams), 1)       # every clip streams from the bundle's own CAB resource
        self.assertTrue(next(iter(streams)).startswith('CAB-'))

    def test_find_reactions_gives_the_70_kept_clips(self):
        clips, trees = audio.find_reactions(self.banks, self.pools)
        self.assertEqual(set(clips), set(audio.exported_clips(self.pools)))
        self.assertEqual(len(clips), 70)
        self.assertEqual(set(trees), set(clips))
        # every kept clip carries a pool and group in the sidecar
        self.assertTrue(all(self.pools['clips'][n]['pool'] and self.pools['clips'][n]['group'] for n in clips))

    def test_extract_replaces_a_stale_export_and_removes_clips_that_are_no_longer_exported(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            # files of an earlier export: a demoted `listen` clip, a clip that was never in the sidecar, and a file that is not a reaction clip
            (output / 'arena_fan9_kill_cheer_01.wav').write_bytes(b'stale')
            (output / 'arena_fan7_kill_cheer_99.wav').write_bytes(b'stale')
            (output / 'arena_fans_ambient_postmatch.wav').write_bytes(b'retired bed')
            manifest = audio.extract(audio.SOURCE, output)
            self.assertEqual(len(manifest['reactions']['clips']), 70)
            self.assertEqual(len(list(output.iterdir())), 1 + 13 + 1 + 70 + 6)
            self.assertFalse((output / 'arena_fan9_kill_cheer_01.wav').exists())
            self.assertFalse((output / 'arena_fans_ambient_postmatch.wav').exists())
            self.assertEqual(len(audio.validate(output, require_bed=True, require_reactions=True)['reactions']['clips']), 70)
            if (WORK_AUDIO / audio.MANIFEST).is_file():
                # the export is deterministic: a second one into another folder is the one the build stages
                self.assertEqual((output / audio.MANIFEST).read_bytes(), (WORK_AUDIO / audio.MANIFEST).read_bytes())
                for path in sorted(output.iterdir()):
                    self.assertEqual(path.read_bytes(), (WORK_AUDIO / path.name).read_bytes(), path.name)


@unittest.skipUnless((WORK_AUDIO / audio.MANIFEST).is_file(), 'work/extract/spectator-audio is not extracted')
class ExtractedPackageTests(unittest.TestCase):
    """The ignored output of the extractor, as the client build stages it."""

    @classmethod
    def setUpClass(cls):
        cls.data = audio.validate(WORK_AUDIO, require_bed=True, require_reactions=True, require_reaction_beds=True)
        cls.reactions = cls.data['reactions']

    def test_the_package_has_the_phase_a_set(self):
        clips = self.reactions['clips']
        self.assertEqual(len(clips), 70)
        self.assertEqual(len(list(WORK_AUDIO.iterdir())), 1 + 13 + 1 + 70 + 6)
        self.assertEqual({b['bank']: (b['embedded'], len(b['clips']), len(b['left_out'])) for b in self.reactions['banks']},
                         {'Cheer': (14, 10, 4), 'Owned': (16, 12, 4), 'Angry': (18, 14, 4), 'ChangedRound': (9, 6, 3), 'EndRound': (11, 9, 2),
                          'Hide': (36, 29, 7)})
        seconds = [c['frames'] / c['sample_rate'] for c in clips]
        self.assertAlmostEqual(min(seconds), 0.70, delta=0.01)
        self.assertAlmostEqual(max(seconds), 6.71, delta=0.01)
        self.assertAlmostEqual(sum(seconds), 174.99, delta=0.1)
        self.assertLess(sum(c['bytes'] for c in clips), 16 * 1024 * 1024)

    def test_every_left_out_clip_is_absent_and_every_listed_one_present(self):
        left = {e['name']: (e['status'], e['reason']) for b in self.reactions['banks'] for e in b['left_out']}
        self.assertEqual(left, {**{n: ('listen', r) for n, r in LISTEN.items()}, 'arena_fan12_shoot_hide_05': ('out', 'SLUR'),
                                'arena_fan8_aim_hide_01': ('out', 'SLUR')})
        for name in left:
            self.assertFalse((WORK_AUDIO / (name + '.wav')).exists(), name)
        for clip in self.reactions['clips']:
            self.assertTrue((WORK_AUDIO / clip['file']).is_file(), clip['file'])

    def test_no_staged_file_is_a_copy_of_a_key_source_file(self):
        protected = audio.protected_names()
        sizes = {(audio.SOURCE / name).stat().st_size for name in protected if (audio.SOURCE / name).is_file()}
        if not sizes:
            self.skipTest('no key-source file of the maps in the installed Arena (without them the comparison below would pass for any file)')
        for path in WORK_AUDIO.iterdir():
            self.assertNotIn(path.stat().st_size, sizes, path.name)


if __name__ == '__main__':
    unittest.main()
