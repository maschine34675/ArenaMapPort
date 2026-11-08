"""Autoservice's ambient-audio exporter rules (PLAN-AUTOSERVICE item 4, 2026-11-08): the SeasonLoopAmbientSoundPlayer layout, the
dropped-component pins (three Winter-only season loops, the inactive spline chain), the nine minDistance-0 players and the four
forced-mixer players. The synthetic tests need no Arena files; the real-data tests read Arena's Sound scene (level112 of the
installed 0.6.x build, ARENAMAPPORT_ARENA_DATA or the default install) and skip without it.

The contract block the real-data tests use is CONTRACT below, NOT the tracked native_contracts/autoservice.json (the package-v2 stage
writes that block from the exporter's output); once the file carries it, ContractFileTests compares the two."""
import copy
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import extract_bay5_audio as audio  # noqa: E402
import native_contract_loader as loader  # noqa: E402
import test_bay5_audio as synthetic  # noqa: E402

ARENA_DATA = Path(os.environ.get("ARENAMAPPORT_ARENA_DATA")
                  or "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data")
KEY = "arena_autoservice"
WATER = {"level112:389": "water_drops (2)", "level112:399": "water_drops (1)", "level112:443": "water_drops (4)",
         "level112:454": "water_drops", "level112:458": "water_drops (7)", "level112:459": "water_drops (3)",
         "level112:470": "water_drops (5)", "level112:478": "water_drops (6)", "level112:490": "water_drops (8)"}
FAR_FIGHT = ["level112:393", "level112:395", "level112:411", "level112:481"]
CONTRACT = {
    "feature": "ambient_audio", "schema": 2, "scene": "level112", "root": "ArenaMapPort_AutoServiceNativeAudio",
    "players": {"AmbientSoundPlayer": 22, "SeasonAmbientSoundPlayer": 16}, "support": {"AmbientSoundPlayerGroup": 2},
    "banks": 13, "clips": 113, "group_coverage": "all_players",
    "states": {
        "grouped_players": 38, "disabled_players": [], "inactive_players": {},
        "groups": {"CommonAmbientInGroup": {"mixer": "CommonAmbInEffects", "players": 18},
                   "CommonAmbientOutGroup": {"mixer": "CommonAmbOutEffects", "players": 20}},
        "group_mixers": ["CommonAmbInEffects", "CommonAmbOutEffects"], "loops": {}, "loop_players": [],
        "forced_mixer_players": {i: "CommonAmbOutBypass" for i in FAR_FIGHT},
        "zero_min_distance_players": WATER,
        "dropped_players": {"level112:415": "indoor_winds_winter", "level112:486": "indoor_winds_winter (2)",
                            "level112:495": "indoor_winds_winter (1)", "level112:469": "AmbientSoundPlayer"},
        "dropped_support": {"level112:408": "LoopAmbientSoundPlayer_spline_emitter", "level112:435": "spline",
                            "level112:468": "AmbientSoundPlayer"},
    },
}


def parsed(contract=None):
    return loader._parse_audio(copy.deepcopy(contract or CONTRACT), "audio")


def season_loop_blob(keys=("Winter",)):
    """A SeasonLoopAmbientSoundPlayer payload: the common player fields, then the season -> clip dictionary (no random time range)."""
    common = synthetic.blob("AmbientSoundPlayer")[:-20]   # strip randomTimeRange (8 bytes) and the bank pointer (12)
    keys = list(keys)
    data = common + synthetic.pack("i", len(keys)) + b"".join(synthetic.string(k) for k in keys)
    return data + synthetic.pack("i", len(keys)) + b"".join(synthetic.pointer(3, 8 + i) for i in range(len(keys)))


class SeasonLoopLayoutTests(unittest.TestCase):
    def test_the_layout_decodes_to_the_last_byte(self):
        d = audio.parse_player(synthetic.Raw(season_loop_blob()), audio.SEASON_LOOP)
        self.assertEqual(["Winter"], list(d["seasonSoundClips"]))
        self.assertEqual({"fileId": 3, "pathId": 8}, d["seasonSoundClips"]["Winter"])
        self.assertAlmostEqual(.2, d["volume"])
        self.assertNotIn("randomTimeRange", d)

    def test_two_seasons_decode(self):
        d = audio.parse_player(synthetic.Raw(season_loop_blob(("Winter", "Summer"))), audio.SEASON_LOOP)
        self.assertEqual(["Winter", "Summer"], list(d["seasonSoundClips"]))

    def test_trailing_truncated_empty_and_duplicate_layouts_are_rejected(self):
        for name, payload in {"trailing": season_loop_blob() + b"\0" * 4, "truncated": season_loop_blob()[:-4],
                              "empty": season_loop_blob(()), "duplicate": season_loop_blob(("Winter", "Winter"))}.items():
            with self.subTest(case=name), self.assertRaises(ValueError):
                audio.parse_player(synthetic.Raw(payload), audio.SEASON_LOOP)

    def test_the_player_bounds_still_apply(self):
        data = bytearray(season_loop_blob())
        import struct
        struct.pack_into("<ff", data, 84, 5.0, 3.0)   # minDistance above maxDistance
        with self.assertRaisesRegex(ValueError, "player bounds"):
            audio.parse_player(synthetic.Raw(bytes(data)), audio.SEASON_LOOP)

    def test_the_class_is_not_an_exported_player_kind(self):
        self.assertNotIn(audio.SEASON_LOOP, audio.PLAYERS)


class ContractSchemaTests(unittest.TestCase):
    def test_the_dropped_pins_load_and_default_to_none(self):
        states = parsed()["states"]
        self.assertEqual(4, len(states["dropped_players"]))
        self.assertEqual(3, len(states["dropped_support"]))
        self.assertEqual(9, len(states["zero_min_distance_players"]))
        self.assertEqual(4, len(states["forced_mixer_players"]))
        for stem in ("equator", "bay5", "airpit", "bowl", "iceberg"):
            contract = loader.load_contracts()["arena_" + stem]["audio"]
            self.assertEqual({}, (contract["states"] or {}).get("dropped_players", {}), stem)
            self.assertEqual({}, (contract["states"] or {}).get("dropped_support", {}), stem)

    def test_a_dropped_component_must_not_carry_another_pin(self):
        for extra in ("forced_mixer_players", "zero_min_distance_players"):
            bad = copy.deepcopy(CONTRACT)
            bad["states"][extra]["level112:415"] = "x"
            with self.subTest(extra=extra), self.assertRaisesRegex(loader.NativeContractError, "another pin"):
                parsed(bad)

    def test_a_dropped_player_cannot_also_be_dropped_support(self):
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["dropped_support"]["level112:469"] = "AmbientSoundPlayer"
        with self.assertRaisesRegex(loader.NativeContractError, "another pin"):
            parsed(bad)

    def test_the_drop_pins_must_be_leaf_maps(self):
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["dropped_players"] = ["level112:415"]
        with self.assertRaises(loader.NativeContractError):
            parsed(bad)


@unittest.skipUnless((ARENA_DATA / "level112").is_file(), "needs Arena's Sound scene (level112)")
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
        self.assertEqual((38, 13, 113), (self.entry["players"], self.entry["banks"], self.entry["clips"]))
        self.assertEqual({"AmbientSoundPlayer": 22, "SeasonAmbientSoundPlayer": 16},
                         {k: sum(1 for p in self.data["players"] if p["kind"] == k) for k in ("AmbientSoundPlayer", "SeasonAmbientSoundPlayer")})
        self.assertEqual(["AmbientSoundPlayerGroup"] * 2, [s["kind"] for s in self.data["support"]])
        self.assertEqual([18, 20], [len(s["soundPlayers"]) for s in self.data["support"]])

    def test_the_source_census_still_counts_the_dropped_components(self):
        counts = self.data["componentCounts"]
        self.assertEqual((23, 16, 3), (counts["AmbientSoundPlayer"], counts["SeasonAmbientSoundPlayer"], counts[audio.SEASON_LOOP]))
        self.assertEqual((1, 1, 1), (counts["BezierSpline"], counts["AmbientPlayerSplineMappedEmitter"], counts["SplineEmitterPathMover"]))

    def test_the_index_audits_what_it_dropped(self):
        dropped = self.data["dropped"]
        self.assertEqual({k: v for k, v in CONTRACT["states"]["dropped_players"].items()}, {r["id"]: r["leaf"] for r in dropped["players"]})
        self.assertEqual({k: v for k, v in CONTRACT["states"]["dropped_support"].items()}, {r["id"]: r["leaf"] for r in dropped["support"]})
        loops = [r for r in dropped["players"] if r["kind"] == audio.SEASON_LOOP]
        self.assertEqual(3, len(loops))
        for r in loops:
            self.assertEqual(["Winter"], r["seasonKeys"])
            self.assertEqual("winter_only_loop", r["reason"])
            self.assertEqual(1, len(r["memberships"]))
            self.assertEqual("CommonAmbientInGroup", r["memberships"][0]["group"])
        chain = [r for r in dropped["players"] if r["kind"] != audio.SEASON_LOOP]
        self.assertEqual(1, len(chain))
        self.assertFalse(chain[0]["activeInHierarchy"])
        self.assertNotIn("memberships", chain[0])
        self.assertTrue(all(r["reason"] == "inactive_spline_chain" and not r["activeInHierarchy"] for r in dropped["support"]))
        exported = {p["id"] for p in self.data["players"]} | {s["id"] for s in self.data["support"]}
        self.assertFalse(exported & set(dropped_ids(dropped)))
        self.assertFalse([s for s in self.data["support"] if s["kind"] in audio.DROPPABLE_SUPPORT])

    def test_the_nine_water_drops_and_the_four_far_fight_players_carry_their_states(self):
        players = {p["id"]: p for p in self.data["players"]}
        for pid, leaf in WATER.items():
            self.assertEqual(0, players[pid]["minDistance"], pid)
            self.assertEqual("SeasonAmbientSoundPlayer", players[pid]["kind"])
            self.assertEqual(leaf, players[pid]["path"].rsplit("/", 1)[-1])
        self.assertEqual(sorted(WATER), sorted(p["id"] for p in players.values() if p["minDistance"] == 0))
        for pid in FAR_FIGHT:
            self.assertEqual(("group", "CommonAmbOutBypass"), (players[pid]["mixerPolicy"], players[pid]["mixerGroupName"]), pid)
            self.assertTrue(players[pid]["path"].rsplit("/", 1)[-1].startswith("far_fight"))
        self.assertEqual(sorted(FAR_FIGHT), sorted(p["id"] for p in players.values() if "mixerPolicy" in p))

    def test_an_unpinned_season_loop_is_refused(self):
        bad = copy.deepcopy(CONTRACT)
        del bad["states"]["dropped_players"]["level112:415"]
        self.refuses(bad, "is not pinned in states.dropped_players")

    def test_a_renamed_or_unpinned_drop_is_refused(self):
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["dropped_players"]["level112:415"] = "indoor_winds_summer"
        self.refuses(bad, "contract pins 'indoor_winds_summer'")
        bad = copy.deepcopy(CONTRACT)
        del bad["states"]["dropped_support"]["level112:435"]
        self.refuses(bad, "source audio census changed")   # the spline would be exported: the support census no longer matches

    def test_a_dropped_player_must_be_inactive_unless_it_is_a_winter_loop(self):
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["dropped_players"]["level112:393"] = "far_fight (2)"   # an active, audible far_fight player
        del bad["states"]["forced_mixer_players"]["level112:393"]
        self.refuses(bad, "must be inactive in the hierarchy")

    def test_the_zero_min_distance_and_forced_mixer_pins_are_enforced(self):
        bad = copy.deepcopy(CONTRACT)
        del bad["states"]["zero_min_distance_players"]["level112:389"]
        self.refuses(bad, "zero-minDistance players changed")
        bad = copy.deepcopy(CONTRACT)
        del bad["states"]["forced_mixer_players"]["level112:393"]
        self.refuses(bad, "mixer policy changed")
        bad = copy.deepcopy(CONTRACT)
        bad["states"]["forced_mixer_players"]["level112:393"] = "CommonAmbInEffects"
        self.refuses(bad, "mixer policy changed")

    def test_the_validator_recheck_accepts_the_export_and_refuses_a_tampered_audit(self):
        import validate_native_features as validator
        manifest = {"source": {"arena_data_root": str(ARENA_DATA), "map_key": KEY}}
        contract = parsed()
        with mock.patch.object(validator, "map_contract", lambda m: (KEY, {"audio": contract})):
            validator.check_audio_group_states(contract, self.data, validator.Counter(
                p["resolved"] for s in self.data["support"] for p in s.get("soundPlayers", [])))
            validator.validate_audio_source(self.out.name, manifest, self.data)
            tampered = copy.deepcopy(self.data)
            tampered["dropped"]["players"].pop()
            with self.assertRaises(ValueError):
                validator.check_audio_group_states(contract, tampered, validator.Counter(
                    p["resolved"] for s in tampered["support"] for p in s.get("soundPlayers", [])))
            tampered = copy.deepcopy(self.data)
            for record in tampered["dropped"]["players"]:
                for membership in record.get("memberships", []):
                    membership["index"] = 0
            with self.assertRaisesRegex(ValueError, "source audio payload"):
                validator.validate_audio_source(self.out.name, manifest, tampered)

    def test_validate_graph_compares_the_audit_with_the_pins(self):
        def check(data):
            with mock.patch.object(audio, "load_contracts", lambda: {KEY: {"audio": parsed()}}):
                audio.validate_graph(data)
        check(copy.deepcopy(self.data))
        lost = copy.deepcopy(self.data)
        lost["dropped"]["players"].pop()
        with self.assertRaisesRegex(ValueError, "dropped_players changed"):
            check(lost)
        lost = copy.deepcopy(self.data)
        lost["dropped"]["support"].pop()
        with self.assertRaisesRegex(ValueError, "dropped_support changed"):
            check(lost)
        renamed = copy.deepcopy(self.data)
        renamed["dropped"]["players"][0]["leaf"] = "other"
        with self.assertRaisesRegex(ValueError, "dropped_players changed"):
            check(renamed)
        no_audit = copy.deepcopy(self.data)
        del no_audit["dropped"]
        with self.assertRaisesRegex(ValueError, "dropped_players changed"):
            check(no_audit)

    def test_the_export_is_deterministic(self):
        with tempfile.TemporaryDirectory() as out:
            entry, _ = self.export(CONTRACT, out)
        self.assertEqual(self.entry["sha256"], entry["sha256"])


def dropped_ids(dropped):
    return [r["id"] for r in dropped["players"] + dropped["support"]]


@unittest.skipUnless((REPO / "tools" / "extract" / "native_contracts" / "autoservice.json").is_file(), "the autoservice contract file does not exist yet")
class ContractFileTests(unittest.TestCase):
    """Once the package-v2 stage has filled the audio block of the tracked contract it must equal the block the exporter was reviewed with."""

    def test_the_tracked_audio_block_equals_the_reviewed_one(self):
        tracked = loader.load_contracts().get(KEY)
        if tracked is None or tracked["audio"] is None:
            self.skipTest("the autoservice contract carries no audio block yet (package v2, PLAN-AUTOSERVICE item 4)")
        self.assertEqual(parsed(), tracked["audio"])


class RegistrationTests(unittest.TestCase):
    """The AUDIO_EXPORTERS row is added by the package-v2 stage together with the contract's audio block (a registered exporter demands an
    audio contract before any extraction, so a row added earlier would stop the v1 extraction that has none). Until then this skips."""

    def test_the_row_matches_the_plan_sound_role_and_the_contract(self):
        import native_features as NF
        import source_plan as SP
        if KEY not in NF.AUDIO_EXPORTERS:
            self.skipTest("arena_autoservice is not registered in native_features.AUDIO_EXPORTERS yet (package v2)")
        self.assertEqual(("ambient_audio", audio.export), NF.AUDIO_EXPORTERS[KEY])
        plan = SP.load_plan(REPO / "tools/extract/plans/autoservice.json")
        self.assertEqual(112, SP.scene_for_role(plan, "sound")["index"])
        self.assertEqual("level112", loader.load_contract(KEY)["audio"]["scene"])


if __name__ == "__main__":
    unittest.main()
