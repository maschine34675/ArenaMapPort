"""Yard's ambient-audio exporter rules (PLAN-YARD item 6, 2026-11-20): the reviewed silent-clip pin (states.silent_clips, Yard's
amb_yard_cat_06), the two minDistance-0 hum loops, the 14 inactive players that no group lists (coverage "active_players") and the
registration of the exporter. The synthetic tests need no Arena files; the real-data tests read Arena's Sound scene (level163 of the
installed 0.6.x build, ARENAMAPPORT_ARENA_DATA or the default install) and skip without it.

The contract block the real-data tests export with is CONTRACT below; ContractFileTests proves the tracked native_contracts/yard.json
carries exactly that block, so the two cannot drift."""
import copy
import io
import json
import os
import sys
import tempfile
import unittest
import wave
from pathlib import Path
from struct import pack
from unittest import mock

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import extract_bay5_audio as audio  # noqa: E402
import native_contract_loader as loader  # noqa: E402

ARENA_DATA = Path(os.environ.get("ARENAMAPPORT_ARENA_DATA")
                  or "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data")
KEY = "arena_yard"
SILENT = {"sharedassets163.assets:109": "amb_yard_cat_06"}
HUMS = {"level163:672": "fridge", "level163:734": "electricity"}
INACTIVE = {"level163:615": "crow (2)", "level163:626": "water_drops (3)", "level163:640": "water_drops (1)",
            "level163:663": "sparrow (2)", "level163:665": "water_drops (3)", "level163:669": "indoor_wind (2)",
            "level163:720": "sparrow_in_garbage (2)", "level163:738": "water_drops (5)", "level163:741": "sparrow (4)",
            "level163:760": "water_drops (5)", "level163:771": "pigeon_flying~2", "level163:775": "indoor_wind (4)",
            "level163:776": "sparrow_in_garbage (4)", "level163:784": "sparrow (6)"}
CONTRACT = {
    "feature": "ambient_audio", "schema": 2, "scene": "level163", "root": "ArenaMapPort_YardNativeAudio",
    "players": {"AmbientSoundPlayer": 26, "SeasonAmbientSoundPlayer": 22, "LoopAmbientSoundPlayer": 2},
    "support": {"AmbientSoundPlayerGroup": 2},
    "banks": 13, "clips": 145, "group_coverage": "active_players",
    "states": {
        "grouped_players": 36, "disabled_players": [], "inactive_players": INACTIVE,
        "groups": {"CommonAmbientOutGroup": {"mixer": "CommonAmbOutEffects", "players": 31},
                   "CommonAmbientInGroup": {"mixer": "CommonAmbInEffects", "players": 5}},
        "group_mixers": ["CommonAmbOutEffects", "CommonAmbInEffects"], "loops": {}, "loop_players": [],
        "zero_min_distance_players": HUMS,
        "silent_clips": SILENT,
    },
}


def parsed(contract=None):
    return loader._parse_audio(copy.deepcopy(contract or CONTRACT), "audio")


def wav(silent):
    stream = io.BytesIO()
    with wave.open(stream, "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(8000)
        writer.writeframes(pack("h", 0 if silent else 1000) * 800)
    return stream.getvalue()


META = {"m_Channels": 1, "m_Frequency": 8000, "m_Length": .1}


class SilentClipRuleTests(unittest.TestCase):
    def test_a_silent_clip_is_refused_unless_its_pin_allows_it(self):
        with self.assertRaisesRegex(ValueError, "silent decoded clip"):
            audio.validate_wav(wav(True), META, "audio/x.wav")
        with self.assertRaisesRegex(ValueError, "silent decoded clip"):
            audio.validate_wav(wav(True), META, "audio/x.wav", silent_ok=False)

    def test_a_pinned_silent_clip_is_reported_and_an_audible_one_is_not(self):
        receipt = audio.validate_wav(wav(True), META, "audio/x.wav", silent_ok=True)
        self.assertTrue(receipt["silent"])
        self.assertEqual((1, 8000, 800), (receipt["channels"], receipt["sampleRate"], receipt["frames"]))
        audible = audio.validate_wav(wav(False), META, "audio/x.wav", silent_ok=True)
        self.assertNotIn("silent", audible)

    def test_the_default_call_is_the_released_behaviour(self):
        receipt = audio.validate_wav(wav(False), META, "audio/x.wav")
        self.assertEqual({"file", "channels", "sampleRate", "frames", "sampleWidth", "bytes", "sha256"}, set(receipt))


class ContractSchemaTests(unittest.TestCase):
    def test_the_pins_load_and_default_to_none_for_every_other_map(self):
        states = parsed()["states"]
        self.assertEqual(SILENT, states["silent_clips"])
        self.assertEqual(HUMS, states["zero_min_distance_players"])
        self.assertEqual(INACTIVE, states["inactive_players"])
        self.assertEqual({}, states["dropped_players"])
        self.assertEqual({}, states["forced_mixer_players"])
        for stem in ("equator", "bay5", "airpit", "bowl", "iceberg", "autoservice"):
            audio_contract = loader.load_contracts()["arena_" + stem]["audio"]
            self.assertEqual({}, (audio_contract["states"] or {}).get("silent_clips", {}), stem)

    def test_the_coverage_rule_counts_the_active_players(self):
        self.assertEqual("active_players", parsed()["group_coverage"])
        self.assertEqual(36, parsed()["states"]["grouped_players"])
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["grouped_players"] = 50
        with self.assertRaises(loader.NativeContractError):
            parsed(bad)

    def test_a_silent_pin_must_be_a_leaf_map_with_no_more_entries_than_clips(self):
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["silent_clips"] = ["sharedassets163.assets:109"]
        with self.assertRaises(loader.NativeContractError):
            parsed(bad)
        bad = copy.deepcopy(CONTRACT)
        bad["clips"] = 1
        bad["states"]["silent_clips"] = {"a:1": "x", "a:2": "y"}
        with self.assertRaisesRegex(loader.NativeContractError, "silent_clips"):
            parsed(bad)


@unittest.skipUnless((ARENA_DATA / "level163").is_file(), "needs Arena's Sound scene (level163)")
class RealSoundSceneTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.out = tempfile.TemporaryDirectory()
        cls.entry, cls.data = cls.export(CONTRACT, cls.out.name)

    @classmethod
    def tearDownClass(cls):
        cls.out.cleanup()

    @staticmethod
    def export(contract, out):
        with mock.patch.object(audio, "load_contracts", lambda: {KEY: {"audio": parsed(contract)}}):
            entry = audio.export(str(ARENA_DATA), out, KEY)
        return entry, json.loads((Path(out) / "audio" / "native-audio.json").read_text(encoding="utf-8"))

    def refuses(self, contract, pattern):
        with tempfile.TemporaryDirectory() as out, self.assertRaisesRegex(ValueError, pattern):
            self.export(contract, out)

    def test_the_strict_check_passes_with_the_reviewed_pins(self):
        self.assertEqual((50, 13, 145), (self.entry["players"], self.entry["banks"], self.entry["clips"]))
        kinds = {k: sum(1 for p in self.data["players"] if p["kind"] == k)
                 for k in ("AmbientSoundPlayer", "SeasonAmbientSoundPlayer", "LoopAmbientSoundPlayer")}
        self.assertEqual({"AmbientSoundPlayer": 26, "SeasonAmbientSoundPlayer": 22, "LoopAmbientSoundPlayer": 2}, kinds)
        self.assertEqual(["AmbientSoundPlayerGroup"] * 2, [s["kind"] for s in self.data["support"]])
        self.assertEqual({"CommonAmbientOutGroup": 31, "CommonAmbientInGroup": 5},
                         {s["path"].rsplit("/", 1)[-1]: len(s["soundPlayers"]) for s in self.data["support"]})
        self.assertNotIn("dropped", self.data)   # nothing is dropped: the index stays in the shape of the released maps

    def test_the_inactive_players_are_in_no_group_and_the_hums_have_minDistance_zero(self):
        players = {p["id"]: p for p in self.data["players"]}
        grouped = {p["resolved"] for s in self.data["support"] for p in s["soundPlayers"]}
        self.assertEqual(set(INACTIVE), {i for i, p in players.items() if not p["activeInHierarchy"]})
        self.assertFalse(grouped & set(INACTIVE))
        self.assertEqual(36, len(grouped))
        self.assertEqual(set(players) - set(INACTIVE), grouped)
        self.assertEqual(sorted(HUMS), sorted(i for i, p in players.items() if p["minDistance"] == 0))
        for pid in HUMS:
            self.assertEqual("LoopAmbientSoundPlayer", players[pid]["kind"])
            self.assertEqual(0, players[pid]["sourceAudio"]["Loop"])
        # the sibling rule renames the second of two equal full paths ('~2'); the three pigeon_flying leaves are pigeon_flying, (1) and ~2
        self.assertEqual(["pigeon_flying", "pigeon_flying (1)", "pigeon_flying~2"],
                         sorted(p["path"].rsplit("/", 1)[-1] for p in players.values() if p["path"].rsplit("/", 1)[-1].startswith("pigeon_flying")))
        self.assertFalse([p for p in players.values() if "mixerPolicy" in p])

    def test_the_season_players_carry_no_winter_bank_and_no_loop_class_exists(self):
        seasons = {k for p in self.data["players"] if p["kind"] == "SeasonAmbientSoundPlayer" for k in p["seasonAmbientBanks"]}
        self.assertEqual({"Spring", "SpringEarly", "Summer", "Autumn", "AutumnLate"}, seasons)
        self.assertEqual(0, self.data["componentCounts"].get(audio.SEASON_LOOP, 0))

    def test_exactly_one_clip_is_silent_and_it_is_the_pinned_one(self):
        silent = []
        for clip in self.data["clips"]:
            pcm = (Path(self.out.name) / clip["decoded"]["file"]).read_bytes()
            if not any(pcm[44:]):
                silent.append((clip["id"], clip["name"]))
        self.assertEqual(list(SILENT.items()), silent)
        self.assertNotIn("silent", self.data["clips"][0]["decoded"])

    def test_an_unpinned_or_misnamed_silent_clip_is_refused(self):
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["silent_clips"] = {}
        self.refuses(bad, "silent decoded clip")
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["silent_clips"] = {"sharedassets163.assets:109": "amb_yard_cat_07"}
        self.refuses(bad, "silent decoded clip")

    def test_a_pin_for_an_audible_clip_is_refused(self):
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["silent_clips"] = dict(SILENT, **{"sharedassets163.assets:46": "amb_yard_cat_01"})
        self.refuses(bad, "silent_clips changed")

    def test_the_zero_min_distance_and_inactive_pins_are_enforced(self):
        bad = copy.deepcopy(CONTRACT)
        del bad["states"]["zero_min_distance_players"]["level163:672"]
        self.refuses(bad, "zero-minDistance players changed")
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["inactive_players"]["level163:615"] = "crow (3)"
        self.refuses(bad, "inactive source players changed")

    def test_the_validator_accepts_the_pinned_silent_clip_and_refuses_it_unpinned(self):
        import validate_native_features as validator
        clip = next(c for c in self.data["clips"] if c["id"] in SILENT)
        audible = next(c for c in self.data["clips"] if c["id"] not in SILENT)
        validator.check_wave(self.out.name, clip, SILENT)
        validator.check_wave(self.out.name, audible, SILENT)
        with self.assertRaises(ValueError):
            validator.check_wave(self.out.name, clip, None)
        with self.assertRaises(ValueError):
            validator.check_wave(self.out.name, clip, {clip["id"]: "other"})
        with self.assertRaises(ValueError):   # a pinned clip that is audible is a changed Arena clip
            validator.check_wave(self.out.name, audible, {audible["id"]: audible["name"]})

    def test_the_validator_recheck_accepts_the_export(self):
        import validate_native_features as validator
        manifest = {"source": {"arena_data_root": str(ARENA_DATA), "map_key": KEY}}
        contract = parsed()
        with mock.patch.object(validator, "map_contract", lambda m: (KEY, {"audio": contract})):
            validator.check_audio_group_states(contract, self.data, validator.Counter(
                p["resolved"] for s in self.data["support"] for p in s.get("soundPlayers", [])))
            validator.validate_audio_source(self.out.name, manifest, self.data)

    def test_the_export_is_deterministic(self):
        with tempfile.TemporaryDirectory() as out:
            entry, _ = self.export(CONTRACT, out)
        self.assertEqual(self.entry["sha256"], entry["sha256"])


@unittest.skipUnless((REPO / "tools" / "extract" / "native_contracts" / "yard.json").is_file(), "the yard contract file does not exist")
class ContractFileTests(unittest.TestCase):
    def test_the_tracked_audio_block_equals_the_reviewed_one(self):
        tracked = loader.load_contracts().get(KEY)
        self.assertIsNotNone(tracked["audio"], "the yard contract carries no audio block")
        self.assertEqual(parsed(), tracked["audio"])


class RegistrationTests(unittest.TestCase):
    def test_the_row_matches_the_plan_sound_role_and_the_contract(self):
        import native_features as NF
        import source_plan as SP
        self.assertIn(KEY, NF.AUDIO_EXPORTERS)
        self.assertEqual(("ambient_audio", audio.export), NF.AUDIO_EXPORTERS[KEY])
        plan = SP.load_plan(REPO / "tools/extract/plans/yard.json")
        self.assertEqual(163, SP.scene_for_role(plan, "sound")["index"])
        self.assertEqual("level163", loader.load_contract(KEY)["audio"]["scene"])

    def test_the_released_maps_keep_their_exporter_rows(self):
        import native_features as NF
        for key in ("arena_airpit", "arena_bowl", "arena_iceberg", "arena_autoservice"):
            self.assertEqual(("ambient_audio", audio.export), NF.AUDIO_EXPORTERS[key])


if __name__ == "__main__":
    unittest.main()
