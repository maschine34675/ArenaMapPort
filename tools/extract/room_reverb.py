"""Decode Arena's room-dependent ambient mixer tables (no source data in git)."""
import hashlib
import math
from pathlib import Path

from extract_bay5_audio import Reader, deref, script, identity, hierarchy

REVERB = ('reverb_volume', 'reverb_dry_level', 'mix', 'reverb_delay',
          'reflect_delay', 'reflect_mix', 'room_size', 'decay_time',
          'decay_hf_ratio', 'room_low_frequency_gain', 'low_frequency_reference',
          'room_high_frequency_gain', 'high_frequency_reference')
DELAY = ('delay_volume', 'delay', 'decay_time', 'wet_mix', 'lowpass_filter_freq')
ENUM = {'IndoorGeneric': 4, 'IndoorIsolated': 8, 'Outdoor': 67}
PARAMETERS = frozenset('''AmbientReverbVolume AmbientReverbDryLevel AmbientReverbMix
AmbientReverbDelay AmbientReverbReflectDelay AmbientReverbReflectMix AmbientReverbRoomSize
AmbientReverbDecayTime AmbientReverbDecayHFRatio AmbientReverbRoomLowFreqGain AmbientReverbLFReference
AmbientReverbRoomHighFreqGain AmbientReverbHFReference DelayVolume DelayTime DelayDecayTime
DelayWetmix DelayLowpassFilterFreq AmbientOutDelaySend AmbientOutReverbSend AmbCommonEffectsSend
AmbientOutDayEffectsSend AmbientOutNightEffectsSend CommonAmbOutEffectsSend CommonAmbInEffectsSend
DelayToReverbSendLevel'''.split())


def parameter(reader):
    name = reader.string()
    keys = [reader.string() for _ in range(reader.count())]
    values = [reader.take('f') for _ in range(reader.count())]
    if len(keys) != len(values) or len(set(keys)) != len(keys):
        raise ValueError('mixer mapping count or duplicate')
    if set(keys) != set(ENUM):
        raise ValueError('mixer room categories')
    if not all(math.isfinite(v) for v in values):
        raise ValueError('nonfinite mixer value')
    return dict(param_name=name, values=[dict(room_type_name=k, room_type=ENUM[k], value=v)
                                       for k, v in zip(keys, values)])


def parse_handler(obj, kind):
    reader = Reader(obj)
    header = reader.header()
    result = dict(id=identity(obj), name=header['name'], **{'class': kind},
                  raw_bytes=len(reader.data), raw_sha256=hashlib.sha256(reader.data).hexdigest())
    if kind in ('ReverbMixerDataSO', 'DelayMixerDataSO'):
        fields = REVERB if kind == 'ReverbMixerDataSO' else DELAY
        result['parameters'] = [dict(field=f, **parameter(reader)) for f in fields]
    elif kind == 'GenericVolumeParamsMixerDataSO':
        result['parameters'] = [parameter(reader) for _ in range(reader.count())]
    else:
        raise ValueError('unexpected mixer handler')
    reader.done()
    return result


def room_categories(graph):
    """Exact native type keys; flags on active authored rooms have priority."""
    categories = {67: 67}  # EmptyAudioRoom is a legitimate outdoor listener.
    active = set(graph['groups'][0]['room_ids'])
    for room in graph['rooms']:
        if room['id'] not in active:
            continue
        key = room['type']
        category = 8 if room['isolated'] else 67 if room['outdoor'] else 4
        if not key or key in categories and categories[key] != category:
            raise ValueError('conflicting mixer room category')
        categories[key] = category
    return categories


def validate(data, graph):
    if data['schema_version'] != 1 or data['map_key'] != graph['map_key']:
        raise ValueError('mixer identity')
    expected = {'reverb': ('ReverbMixerDataSO', 13), 'delay': ('DelayMixerDataSO', 5),
                'effects_sends': ('GenericVolumeParamsMixerDataSO', 8),
                'effects_volumes': ('GenericVolumeParamsMixerDataSO', 0)}
    if set(data['handlers']) != set(expected):
        raise ValueError('mixer handlers')
    names = []
    for role, (kind, count) in expected.items():
        handler = data['handlers'][role]
        if handler['class'] != kind or len(handler['parameters']) != count:
            raise ValueError('mixer handler layout')
        for param in handler['parameters']:
            names.append(param['param_name'])
            values = param['values']
            if len(values) != 3 or {v['room_type_name'] for v in values} != set(ENUM):
                raise ValueError('mixer room categories')
            for value in values:
                if ENUM[value['room_type_name']] != value['room_type'] or not math.isfinite(value['value']):
                    raise ValueError('invalid mixer value')
    if len(names) != len(PARAMETERS) or set(names) != PARAMETERS:
        raise ValueError('unexpected or duplicate mixer parameters')
    room_categories(graph)


def extract(scene_file, source, graph):
    objects = [o for o in scene_file.objects.values()
               if o.type.name == 'MonoBehaviour' and script(o) == 'AmbientAudioSystem']
    if len(objects) != 1:
        raise ValueError('one ambient system required')
    obj = objects[0]
    reader = Reader(obj)
    header = reader.header()
    ambient = deref(obj, reader.ptr())
    effects = deref(obj, reader.ptr())
    interval, simple = reader.take('i'), reader.boolean()
    reader.done()
    if not header['enabled'] or not hierarchy(obj)['activeInHierarchy']:
        raise ValueError('inactive ambient system')
    if script(effects) != 'MixerEffectsDataSO':
        raise ValueError('unexpected mixer effects reference')
    result = dict(schema_version=1, map_key=graph['map_key'], source_scene=graph['source_scene'],
                  system_id=identity(obj), system_raw_sha256=hashlib.sha256(obj.get_raw_data()).hexdigest(),
                  ambient_data=identity(ambient), frame_count_update_interval=interval,
                  use_simple_under_roof_check=simple, effects_id=identity(effects), handlers={})
    reader = Reader(effects)
    result['effects_name'] = reader.header()['name']
    for role in ('reverb', 'delay', 'effects_sends', 'effects_volumes'):
        handler = deref(effects, reader.ptr())
        result['handlers'][role] = parse_handler(handler, script(handler))
    reader.done()
    asset = Path(effects.assets_file.name).name
    result['source_asset'] = asset
    result['source_asset_sha256'] = hashlib.sha256((Path(source) / asset).read_bytes()).hexdigest()
    validate(result, graph)
    return result
