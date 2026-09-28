"""Binary parsing, room coverage and malformed table controls without source fixtures."""
import copy
import json
from pathlib import Path
import struct
import sys
from types import SimpleNamespace
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/extract'))
from extract_bay5_audio import Reader
from room_reverb import parameter, validate, room_categories, PARAMETERS, ENUM


def payload():
    names = iter(sorted(PARAMETERS))
    handlers = {}
    for role, kind, count in [('reverb', 'ReverbMixerDataSO', 13), ('delay', 'DelayMixerDataSO', 5),
            ('effects_sends', 'GenericVolumeParamsMixerDataSO', 8), ('effects_volumes', 'GenericVolumeParamsMixerDataSO', 0)]:
        handlers[role] = dict(**{'class': kind}, parameters=[dict(param_name=next(names),
            values=[dict(room_type_name=k, room_type=v, value=-i) for i, (k, v) in enumerate(ENUM.items())]) for _ in range(count)])
    return dict(schema_version=1, map_key='arena_equator', handlers=handlers)


def graph():
    return dict(map_key='arena_equator', groups=[dict(room_ids=[1, 2, 3])], rooms=[
        dict(id=1, type=2048, outdoor=False, isolated=False),
        dict(id=2, type=1, outdoor=True, isolated=False),
        dict(id=3, type=4096, outdoor=False, isolated=True),
        dict(id=4, type=2048, outdoor=True, isolated=False)])


def binary(keys=('IndoorGeneric', 'IndoorIsolated', 'Outdoor'), values=(.1, -.2, 1.5)):
    def string(s):
        b = s.encode(); return struct.pack('<i', len(b)) + b + b'\0' * (-len(b) % 4)
    raw = string('AmbientReverbVolume') + struct.pack('<i', len(keys)) + b''.join(map(string, keys))
    raw += struct.pack('<i', len(values)) + struct.pack('<' + 'f' * len(values), *values)
    return Reader(SimpleNamespace(path_id=1, get_raw_data=lambda: raw))


class RoomReverbTests(unittest.TestCase):
    def test_binary_positive_and_full_consumption(self):
        r = binary(); p = parameter(r); r.done()
        self.assertEqual([v['room_type'] for v in p['values']], [4, 8, 67])
        self.assertAlmostEqual(p['values'][0]['value'], .1)

    def test_binary_bad_count(self):
        with self.assertRaisesRegex(ValueError, 'count'): parameter(binary(values=(1, 2)))

    def test_binary_duplicate_category(self):
        with self.assertRaisesRegex(ValueError, 'duplicate'): parameter(binary(keys=('Outdoor',) * 3))

    def test_binary_unknown_category(self):
        with self.assertRaisesRegex(ValueError, 'categories'): parameter(binary(keys=('IndoorBrick', 'IndoorIsolated', 'Outdoor')))

    def test_binary_nonfinite(self):
        with self.assertRaisesRegex(ValueError, 'nonfinite'): parameter(binary(values=(1, float('nan'), 2)))

    def test_binary_truncated(self):
        r = binary(); r.data = r.data[:-1]
        with self.assertRaisesRegex(ValueError, 'truncated'): parameter(r)

    def test_category_coverage_and_inactive_ignored(self):
        self.assertEqual(room_categories(graph()), {67: 67, 2048: 4, 1: 67, 4096: 8})
        validate(payload(), graph())

    def test_active_conflicting_categories_rejected(self):
        g = graph(); g['groups'][0]['room_ids'].append(4)
        with self.assertRaisesRegex(ValueError, 'conflicting'): room_categories(g)

    def reject(self, edit, message):
        d = payload(); edit(d)
        with self.assertRaisesRegex(ValueError, message): validate(d, graph())

    def test_wrong_map(self):
        self.reject(lambda d: d.update(map_key='arena_bay5'), 'identity')

    def test_missing_handler(self):
        self.reject(lambda d: d['handlers'].pop('effects_volumes'), 'handlers')

    def test_unknown_parameter(self):
        self.reject(lambda d: d['handlers']['reverb']['parameters'][0].update(param_name='WorldReverbLevel'), 'parameters')

    def test_duplicate_parameter(self):
        self.reject(lambda d: d['handlers']['delay']['parameters'][0].update(
            param_name=d['handlers']['reverb']['parameters'][0]['param_name']), 'parameters')

    def test_wrong_enum_value(self):
        self.reject(lambda d: d['handlers']['delay']['parameters'][0]['values'][0].update(room_type=4096), 'invalid mixer value')

    def test_missing_category(self):
        self.reject(lambda d: d['handlers']['delay']['parameters'][0]['values'].pop(), 'categories')

    def test_nonfinite_json(self):
        self.reject(lambda d: d['handlers']['delay']['parameters'][0]['values'][0].update(value=float('inf')), 'invalid mixer value')

    def test_original_packages_when_available(self):
        for key in ('arena_equator', 'arena_bay5'):
            path = ROOT / 'work/extract/spatial-audio' / key / 'spatial-audio.json'
            if not path.exists(): self.skipTest('local original package unavailable')
            d = json.loads(path.read_text()); validate(d['mixer_effects'], d)
            self.assertEqual(len(room_categories(d)), 5 if key == 'arena_equator' else 8)


if __name__ == '__main__': unittest.main()
