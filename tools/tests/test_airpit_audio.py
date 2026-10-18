"""PLAN-AIRPIT item 13: AirPit's native ambient audio contract, on synthetic graphs.

AirPit is the first map with BOTH an Out and an In AmbientSoundPlayerGroup, so the extractor and the
validator can no longer assume one indoor group. The pins come from tools/extract/native_contracts/
airpit.json: source scene level39, schema 2, 26 AmbientSoundPlayer, two groups, pinned by leaf name, routed
to CommonAmbOutEffects with 4 players (CommonAmbientOutGroup) and CommonAmbInEffects with 22
(CommonAmbientInGroup), every player grouped, none inactive, disabled or looping. The pins are per-group
data, not object-table order, and a player moved between the groups must fail (review of item 13). The graphs here carry no source data; the real package is covered (when present) by
test_native_contracts.AcceptedPackageTests.test_airpit_native_package_when_present.
"""
from __future__ import annotations

from collections import Counter
import copy
from pathlib import Path
import sys
import unittest
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import extract_bay5_audio as audio
import validate_native_features as v

GROUP_PATH = "AmbientAudioSystem/AmbientPlayersGroup"
GROUPS = (("CommonAmbientOutGroup", "CommonAmbOutEffects", 4), ("CommonAmbientInGroup", "CommonAmbInEffects", 22))


def node(active=True, layer=0):
    return {"position": {"x": 0, "y": 0, "z": 0}, "rotation": {"x": 0, "y": 0, "z": 0, "w": 1},
            "scale": {"x": 1, "y": 1, "z": 1}, "activeSelf": active, "layer": layer}


def graph():
    """26 players in an Out group (4) and an In group (22), Out first, as in the source scene."""
    players, support = [], []
    for g, (name, mixer, size) in enumerate(GROUPS):
        members = []
        for j in range(size):
            ident = f"level39:{1000 + 100 * g + j}"
            players.append({"id": ident, "kind": "AmbientSoundPlayer", "path": f"{GROUP_PATH}/{name}/sound_{g}_{j}",
                            "activeInHierarchy": True, "transformChain": [node() for _ in range(4)], "enabled": True,
                            "sourceAudio": {"m_Enabled": 1, "Loop": 0}, "playOnAwake": False,
                            "forceSetMixerGroup": False, "mixerGroup": {"pathId": 0},
                            "ambientBank": {"resolved": f"bank:{(size * g + j) % 9}"}})
            members.append({"resolved": ident})
        support.append({"id": f"level39:{500 + g}", "kind": "AmbientSoundPlayerGroup", "path": f"{GROUP_PATH}/{name}",
                        "transformChain": [node() for _ in range(3)], "activeInHierarchy": True, "enabled": True,
                        "mixerName": mixer, "soundPlayers": members})
    return {"schemaVersion": 2, "mapKey": "arena_airpit", "sourceScene": "level39", "players": players,
            "support": support,
            "banks": {f"bank:{i}": {"environments": [[[{"resolved": f"clip:{i}"}]]]} for i in range(9)},
            "clips": [{"id": f"clip:{i}"} for i in range(61)]}


class AirPitAudioContractTests(unittest.TestCase):
    def test_the_extractor_reads_the_airpit_pins_from_the_contract(self):
        pins = audio.audio_contract("arena_airpit")
        self.assertEqual((pins["schema"], pins["scene"], pins["feature"]), (2, "level39", "ambient_audio"))
        self.assertEqual(pins["players"], {"AmbientSoundPlayer": 26})
        self.assertEqual((pins["banks"], pins["clips"], pins["support"]), (9, 61, {"AmbientSoundPlayerGroup": 2}))
        self.assertEqual(pins["group_coverage"], "all_players")
        self.assertEqual(pins["states"]["group_mixers"], [mixer for _name, mixer, _size in GROUPS])
        self.assertEqual(pins["states"]["groups"], {name: {"mixer": mixer, "players": size} for name, mixer, size in GROUPS})
        self.assertEqual(pins["states"]["loops"], {})

    def test_the_synthetic_graph_has_the_documented_group_sizes(self):
        data = graph()
        self.assertEqual(len(data["players"]), 26)
        self.assertEqual([(g["mixerName"], len(g["soundPlayers"])) for g in data["support"]],
                         [("CommonAmbOutEffects", 4), ("CommonAmbInEffects", 22)])

    def test_valid_graph_passes_and_is_not_mutated(self):
        data = graph()
        before = copy.deepcopy(data)
        audio.validate_graph(data)
        self.assertEqual(before, data)
        pins = audio.audio_contract("arena_airpit")
        self.assertEqual(audio.validate_audio_states(data["players"], data["support"], pins),
                         [p["id"] for p in data["players"]])
        self.assertEqual(audio.expected_group_members(data["players"], pins), [p["id"] for p in data["players"]])

    def test_group_to_mixer_routes_are_enforced_by_group_name(self):
        def swap(d):
            d["support"][0]["mixerName"], d["support"][1]["mixerName"] = "CommonAmbInEffects", "CommonAmbOutEffects"
        def renamed(d):
            d["support"][0]["path"] = d["support"][0]["path"] + "Renamed"
        def both_indoor(d):
            d["support"][0]["mixerName"] = "CommonAmbInEffects"
        def both_outdoor(d):
            d["support"][1]["mixerName"] = "CommonAmbOutEffects"
        def bypass(d):
            d["support"][0]["mixerName"] = "CommonAmbOutBypass"
        def no_mixer(d):
            d["support"][1]["mixerName"] = None
        for change in (swap, renamed, both_indoor, both_outdoor, bypass, no_mixer):
            data = graph()
            change(data)
            with self.subTest(change.__name__), self.assertRaisesRegex(ValueError, "group mixer contract"):
                audio.validate_graph(data)

    def test_group_order_is_not_pinned(self):
        # The object-table order of the two groups is a by-product of path ids (Out 1295 < In 1429); the pins are
        # keyed by group name, so a reshuffled source must not be a false alarm.
        data = graph()
        data["support"].reverse()
        audio.validate_graph(data)

    def test_group_names_must_be_unique_keys(self):
        data = graph()
        moved = "Other/Parent/" + GROUPS[0][0]  # same depth as the real groups, same leaf name as the Out group
        data["support"][1]["path"] = moved
        for player in data["players"][4:]:
            player["path"] = moved + "/" + player["path"].rsplit("/", 1)[-1]
        with self.assertRaisesRegex(ValueError, "names not unique"):
            audio.validate_graph(data)

    def test_a_player_moved_between_the_groups_is_rejected(self):
        # Coverage and the grouped total still hold (every player is grouped once, 26 of 26); only the per-group
        # member counts of the contract (Out 4 / In 22) reveal that an indoor source now plays on the Out bus.
        data = graph()
        data["support"][0]["soundPlayers"].append(data["support"][1]["soundPlayers"].pop())
        self.assertEqual([len(g["soundPlayers"]) for g in data["support"]], [5, 21])
        with self.assertRaisesRegex(ValueError, "audio group membership"):
            audio.validate_graph(data)
        data = graph()  # the other direction (Out 3 / In 23)
        data["support"][1]["soundPlayers"].append(data["support"][0]["soundPlayers"].pop())
        with self.assertRaisesRegex(ValueError, "audio group membership"):
            audio.validate_graph(data)

    def test_players_swapped_between_equal_sized_groups_are_rejected(self):
        # 4 / 22 is preserved; the hierarchy still parents each player under its own group.
        data = graph()
        out, indoor = data["support"][0]["soundPlayers"], data["support"][1]["soundPlayers"]
        out[0], indoor[0] = indoor[0], out[0]
        self.assertEqual([len(g["soundPlayers"]) for g in data["support"]], [4, 22])
        with self.assertRaisesRegex(ValueError, "audio group membership"):
            audio.validate_graph(data)
        pins = audio.audio_contract("arena_airpit")
        with self.assertRaisesRegex(ValueError, "audio group membership"):
            audio.validate_audio_group_members(data["players"], data["support"], pins["states"])

    def test_membership_is_checked_after_coverage(self):
        # An omitted or duplicated player is a coverage failure first; the membership pin only has to catch what
        # coverage cannot, and it relies on every listed id being a player.
        data = graph()
        data["support"][1]["soundPlayers"].pop()
        with self.assertRaisesRegex(ValueError, "group coverage"):
            audio.validate_graph(data)

    def test_group_census_is_exactly_two_groups(self):
        data = graph()
        data["support"].pop()
        with self.assertRaisesRegex(ValueError, "source audio census"):
            audio.validate_graph(data)
        data = graph()
        data["support"].append(copy.deepcopy(data["support"][1]))
        with self.assertRaisesRegex(ValueError, "source audio census"):
            audio.validate_graph(data)

    def test_every_player_must_be_grouped_exactly_once(self):
        for mutation in ("omit", "duplicate", "cross-group duplicate", "substitute"):
            data = graph()
            out, indoor = data["support"][0]["soundPlayers"], data["support"][1]["soundPlayers"]
            if mutation == "omit":
                indoor.pop()
            elif mutation == "duplicate":
                indoor.append(copy.deepcopy(indoor[0]))
            elif mutation == "cross-group duplicate":
                out.append(copy.deepcopy(indoor[0]))
            else:
                indoor[-1] = copy.deepcopy(out[0])
            with self.subTest(mutation), self.assertRaisesRegex(ValueError, "group coverage"):
                audio.validate_graph(data)

    def test_player_census_is_exactly_26_ambient_sound_players(self):
        for mutation in ("season", "loop", "extra", "fewer"):
            data = graph()
            if mutation == "season":
                data["players"][0]["kind"] = "SeasonAmbientSoundPlayer"
            elif mutation == "loop":
                data["players"][0]["kind"] = "LoopAmbientSoundPlayer"
            elif mutation == "extra":
                data["players"].append(dict(copy.deepcopy(data["players"][0]), id="level39:9999"))
            else:
                data["players"].pop()
            with self.subTest(mutation), self.assertRaisesRegex(ValueError, "player census"):
                audio.validate_graph(data)

    def test_bank_clip_identity_and_schema_pins(self):
        data = graph()
        data["banks"].pop("bank:0")
        with self.assertRaisesRegex(ValueError, "source audio census"):
            audio.validate_graph(data)
        data = graph()
        data["clips"].pop()
        with self.assertRaisesRegex(ValueError, "source audio census"):
            audio.validate_graph(data)
        for key, value in (("schemaVersion", 1), ("sourceScene", "level56"), ("mapKey", "arena_prison")):
            data = graph()
            data[key] = value
            with self.subTest(key), self.assertRaisesRegex(ValueError, "audio package identity"):
                audio.validate_graph(data)

    def test_source_states_cannot_change_silently(self):
        def inactive(d):
            d["players"][5].update(activeInHierarchy=False)
            d["players"][5]["transformChain"][-1]["activeSelf"] = False
        def disabled_component(d):
            d["players"][5]["enabled"] = False
        def disabled_source(d):
            d["players"][5]["sourceAudio"]["m_Enabled"] = 0
        def looping(d):
            d["players"][5]["sourceAudio"]["Loop"] = 1
        def play_on_awake(d):
            d["players"][5]["playOnAwake"] = True
        def mixer_override(d):
            d["players"][5]["forceSetMixerGroup"] = True
        def mixer_pointer(d):
            d["players"][5]["mixerGroup"] = {"pathId": 42}
        def disabled_group(d):
            d["support"][0]["enabled"] = False
        cases = (("inactive", inactive, "inactive source players"), ("disabled_component", disabled_component, "component activation"),
                 ("disabled_source", disabled_source, "AudioSource activation"), ("looping", looping, "source loop"),
                 ("play_on_awake", play_on_awake, "start/mixer policy"), ("mixer_override", mixer_override, "start/mixer policy"),
                 ("mixer_pointer", mixer_pointer, "start/mixer policy"), ("disabled_group", disabled_group, "component activation"))
        for name, change, pattern in cases:
            data = graph()
            change(data)
            with self.subTest(name), self.assertRaisesRegex(ValueError, pattern):
                audio.validate_graph(data)

    def test_cli_accepts_airpit_and_still_rejects_unknown_maps(self):
        argv = ["extract_bay5_audio.py", "--source", "S", "--package", "P"]
        with mock.patch.object(audio, "export", return_value={"ok": 1}) as export, \
             mock.patch.object(sys, "argv", argv + ["--map-key", "arena_airpit"]), mock.patch("builtins.print"):
            audio.main()
        self.assertEqual(export.call_args.args[2], "arena_airpit")
        with mock.patch.object(sys, "argv", argv + ["--map-key", "arena_prison"]), \
             mock.patch.object(sys, "stderr"), self.assertRaises(SystemExit) as stopped:
            audio.main()
        self.assertEqual(stopped.exception.code, 2)


class AirPitValidatorAgreementTests(unittest.TestCase):
    """validate_native_features reads the same contract: both layers accept and reject the same graphs."""

    def check(self, data):
        pins = v.MAPS["arena_airpit"]["audio"]
        grouped = Counter(p["resolved"] for s in data["support"] for p in s.get("soundPlayers", []))
        v.check_audio_group_states(pins, data, grouped)

    def test_validator_accepts_the_extractor_graph(self):
        self.check(graph())

    def test_validator_pins_group_mixers_by_group_name(self):
        for change in (lambda d: d["support"][0].update(mixerName="CommonAmbInEffects"),
                       lambda d: d["support"][1].update(mixerName="CommonAmbOutBypass"),
                       lambda d: d["support"][1].update(path=d["support"][1]["path"] + "Renamed")):
            data = graph()
            change(data)
            with self.assertRaisesRegex(ValueError, "audio group mixer routes"):
                self.check(data)

    def test_validator_does_not_pin_group_order(self):
        data = graph()
        data["support"].reverse()
        self.check(data)

    def test_validator_pins_per_group_membership(self):
        data = graph()  # a player moved Out -> In: Out 3 / In 23
        data["support"][1]["soundPlayers"].append(data["support"][0]["soundPlayers"].pop())
        with self.assertRaisesRegex(ValueError, "audio group member counts"):
            self.check(data)
        data = graph()  # two players swapped: 4 / 22 preserved, the hierarchy disagrees
        out, indoor = data["support"][0]["soundPlayers"], data["support"][1]["soundPlayers"]
        out[0], indoor[0] = indoor[0], out[0]
        with self.assertRaisesRegex(ValueError, "outside their group hierarchy"):
            self.check(data)

    def test_validator_pins_state_coverage_and_loops(self):
        def drop(d):
            d["support"][1]["soundPlayers"].pop()
        def inactive(d):
            d["players"][3]["activeInHierarchy"] = False
        def disabled(d):
            d["players"][3]["enabled"] = False
        def looping(d):
            d["players"][3]["sourceAudio"]["Loop"] = 1
        for change in (drop, inactive, disabled, looping):
            data = graph()
            change(data)
            with self.subTest(change.__name__), self.assertRaises(ValueError):
                self.check(data)


if __name__ == "__main__":
    unittest.main()
