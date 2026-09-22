"""Synthetic contracts for source decoding; no proprietary fixtures are checked in."""
import copy
import io
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import wave

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import extract_bay5_audio as audio


def pack(fmt, *values): return struct.pack('<' + fmt, *values)
def pointer(file=0, path=0): return pack('iq', file, path)
def boolean(value=False): return pack('Bxxx', value)
def string(value):
    data=value.encode(); return pack('i',len(data))+data+b'\0'*((-len(data))%4)
def curve():
    return pack('i',2)+pack('ffffiff',0,1,0,-1,0,0,0)+pack('ffffiff',1,0,-1,0,0,0,0)+pack('iii',2,2,4)
def blob(kind):
    data=pointer(0,1)+boolean(True)+pointer(1,20)+string('')
    data+=pack('ffffff',.2,0,.9,1.1,0,0)+pointer()+boolean()+boolean()
    data+=pack('ffff',.5,1,1,30)+curve()+pack('ff',0,20)+curve()+boolean()
    if kind=='LoopAmbientSoundPlayer': return data+pointer(2,7)
    data+=pack('ff',15,30)
    if kind=='AmbientSoundPlayer': return data+pointer(3,8)
    return data+pack('i',1)+string('Winter')+pack('i',1)+pointer(3,8)+pack('ii',0,0)


class Raw:
    path_id=1
    def __init__(self,data): self.data=data
    def get_raw_data(self): return self.data


def graph():
    clips=[{'id':f'clip:{i}'} for i in range(106)]
    banks={f'bank:{i}':{'environments':[[[{'resolved':f'clip:{i}'}]]]} for i in range(13)}
    players=[]
    for i,kind in enumerate(['AmbientSoundPlayer']*17+['SeasonAmbientSoundPlayer']*6+['LoopAmbientSoundPlayer']):
        p={'id':f'player:{i}','kind':kind}
        if kind=='AmbientSoundPlayer': p['ambientBank']={'resolved':'bank:0'}
        elif kind=='SeasonAmbientSoundPlayer': p['seasonAmbientBanks']={'Winter':{'resolved':'bank:1'}}
        else: p['loopClip']={'resolved':'clip:100'}
        players.append(p)
    support=[{'soundPlayers':[{'resolved':p['id']} for p in players[:-1]]},{'soundPlayers':[{'resolved':players[-1]['id']}]}]+[{} for _ in range(4)]
    return {'schemaVersion':1,'mapKey':'arena_bay5','sourceScene':'level188','players':players,'banks':banks,'clips':clips,'support':support}


class Bay5AudioTests(unittest.TestCase):
    def test_all_player_layouts_fully_decode(self):
        for kind in audio.PLAYERS:
            with self.subTest(kind=kind):
                data=audio.parse_player(Raw(blob(kind)),kind)
                self.assertAlmostEqual(.2,data['volume'])
                self.assertEqual([0,20],data['startPlayingDelay'])
                self.assertEqual(2,len(data['rolloffCurve']['keys']))
                if kind=='SeasonAmbientSoundPlayer': self.assertEqual({'Winter'},set(data['seasonAmbientBanks']))

    def test_unknown_trailing_source_field_is_rejected(self):
        for kind in audio.PLAYERS:
            with self.subTest(kind=kind), self.assertRaises(ValueError): audio.parse_player(Raw(blob(kind)+b'\0'*4),kind)

    def test_truncated_source_field_is_rejected(self):
        for kind in audio.PLAYERS:
            with self.subTest(kind=kind), self.assertRaises(ValueError): audio.parse_player(Raw(blob(kind)[:-4]),kind)

    def test_invalid_bool_and_source_distance_are_rejected(self):
        for kind in audio.PLAYERS:
            data=bytearray(blob(kind)); data[12]=2
            with self.subTest(kind=kind,case='bool'),self.assertRaises(ValueError): audio.parse_player(Raw(bytes(data)),kind)
            data=bytearray(blob(kind)); struct.pack_into('<f',data,88,-1)
            with self.subTest(kind=kind,case='distance'),self.assertRaises(ValueError): audio.parse_player(Raw(bytes(data)),kind)

    def test_negative_and_unbounded_counts_are_rejected(self):
        for count in (-1,1024,0x7fffffff):
            with self.subTest(count=count),self.assertRaises(ValueError): audio.Reader(Raw(pack('i',count))).count()

    def test_nonfinite_curve_key_is_rejected(self):
        data=bytearray(curve()); struct.pack_into('<f',data,4,float('nan'))
        with self.assertRaises(ValueError): audio.Reader(Raw(bytes(data))).curve()

    def test_valid_graph_does_not_fill_missing_seasons(self):
        data=graph(); before=copy.deepcopy(data)
        audio.validate_graph(data)
        self.assertEqual(before,data)

    def test_group_omissions_and_double_registration_are_rejected(self):
        for mutation in ('omit','duplicate'):
            data=graph()
            if mutation=='omit': data['support'][0]['soundPlayers'].pop()
            else: data['support'][0]['soundPlayers'].append(data['support'][0]['soundPlayers'][0])
            with self.subTest(mutation=mutation),self.assertRaises(ValueError): audio.validate_graph(data)

    def test_missing_clip_bank_and_loop_edges_are_rejected(self):
        for mutation in ('clip','bank','loop','empty'):
            data=graph()
            if mutation=='clip': data['banks']['bank:0']['environments'][0][0][0]['resolved']='missing'
            elif mutation=='bank': data['players'][0]['ambientBank']['resolved']='missing'
            elif mutation=='loop': data['players'][-1]['loopClip']['resolved']='missing'
            else: data['banks']['bank:0']['environments']=[[]]
            with self.subTest(mutation=mutation),self.assertRaises(ValueError): audio.validate_graph(data)

    def test_unknown_scene_and_event_player_are_rejected(self):
        data=graph(); data['sourceScene']='level225'
        with self.assertRaises(ValueError): audio.validate_graph(data)
        data=graph(); data['players'][0]['kind']='SpeakerEntity'
        with self.assertRaises(ValueError): audio.validate_graph(data)

    def wav(self, silent=False):
        stream=io.BytesIO()
        with wave.open(stream,'wb') as writer:
            writer.setnchannels(1); writer.setsampwidth(2); writer.setframerate(8000)
            writer.writeframes(pack('h',0 if silent else 1000)*800)
        return stream.getvalue()

    def test_wav_format_and_hash_receipt(self):
        receipt=audio.validate_wav(self.wav(),{'m_Channels':1,'m_Frequency':8000,'m_Length':.1},'audio/test.wav')
        self.assertEqual((1,8000,800,2),(receipt['channels'],receipt['sampleRate'],receipt['frames'],receipt['sampleWidth']))
        self.assertEqual(64,len(receipt['sha256']))

    def test_wav_rate_channels_duration_and_silence_are_rejected(self):
        baseline={'m_Channels':1,'m_Frequency':8000,'m_Length':.1}
        for key,value in [('m_Channels',2),('m_Frequency',44100),('m_Length',.2)]:
            meta=dict(baseline); meta[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError): audio.validate_wav(self.wav(),meta,'x')
        with self.assertRaises(ValueError): audio.validate_wav(self.wav(True),baseline,'x')

    def test_export_never_writes_inside_source_installation(self):
        with tempfile.TemporaryDirectory() as path:
            for output in (path,Path(path)/'nested'):
                with self.subTest(output=output),self.assertRaises(ValueError): audio.export(path,output)
            self.assertFalse((Path(path)/'nested').exists())


if __name__=='__main__': unittest.main()
