"""Same-named sibling players share one hierarchy path (Bowl level111: two 'indoor_wind' AmbientSoundPlayers under
CommonAmbientInGroup); the exporter and the validator give the later duplicates unique leaves '~2', '~3' ... in source
id order and keep the authored leaf as sourceLeafName. Maps without duplicates are untouched."""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "extract")))
import extract_bay5_audio as audio  # noqa: E402


class SiblingNameTests(unittest.TestCase):
    def test_unique_paths_are_untouched(self):
        paths = {"level1:5": "A/B/wind", "level1:7": "A/B/rain", "level1:9": "A/C/wind"}
        self.assertEqual(audio.disambiguate_sibling_paths(paths), paths)

    def test_duplicates_are_renamed_in_source_id_order_from_two(self):
        paths = {"level1:597": "A/In/indoor_wind", "level1:560": "A/In/indoor_wind", "level1:9": "A/In/cracles"}
        self.assertEqual(audio.disambiguate_sibling_paths(paths),
                         {"level1:560": "A/In/indoor_wind", "level1:597": "A/In/indoor_wind~2", "level1:9": "A/In/cracles"})

    def test_an_ordinal_that_collides_with_an_authored_path_is_skipped(self):
        paths = {"level1:1": "A/x", "level1:2": "A/x", "level1:3": "A/x", "level1:4": "A/x~2"}
        self.assertEqual(audio.disambiguate_sibling_paths(paths),
                         {"level1:1": "A/x", "level1:2": "A/x~3", "level1:3": "A/x~4", "level1:4": "A/x~2"})

    def test_apply_renames_records_and_keeps_the_authored_leaf(self):
        items = [{"id": "level1:2", "path": "A/B/w"}, {"id": "level1:1", "path": "A/B/w"}, {"id": "level1:3", "path": "A/B/v"}]
        self.assertEqual(audio.apply_sibling_paths(items), ["level1:2"])
        self.assertEqual(items[0], {"id": "level1:2", "path": "A/B/w~2", "sourceLeafName": "w"})
        self.assertEqual(items[1], {"id": "level1:1", "path": "A/B/w"})
        self.assertNotIn("sourceLeafName", items[2])


class ComponentsOfOneGameObjectTests(unittest.TestCase):
    """Components that sit on ONE GameObject share its path and are never renamed apart: Bay5's and Iceberg's
    AmbientSplineEmitterController + AmbientSoundPlayerGroup ('AmbientSplineEmitterWaterGroup') and
    SoundAmbientZoneCalculator + AmbientPlayerSplineMappedEmitter (the spline emitter object). Before the GameObject key
    existed the second component of each pair got '~2', which moved the group away from its members' hierarchy."""

    def test_two_components_of_one_object_keep_their_shared_path(self):
        paths = {"level1:10": "A/Water", "level1:11": "A/Water", "level1:20": "A/Water/Emitter", "level1:21": "A/Water/Emitter"}
        objects = {"level1:10": (0, 183), "level1:11": (0, 183), "level1:20": (0, 205), "level1:21": (0, 205)}
        self.assertEqual(audio.disambiguate_sibling_paths(paths, objects), paths)

    def test_a_second_object_with_the_same_name_is_renamed_as_a_whole(self):
        # object 7 holds ids 30 and 31, object 5 holds id 40: the object with the lowest id (30) keeps the name
        paths = {"level1:40": "A/x", "level1:31": "A/x", "level1:30": "A/x"}
        objects = {"level1:40": (0, 5), "level1:31": (0, 7), "level1:30": (0, 7)}
        self.assertEqual(audio.disambiguate_sibling_paths(paths, objects),
                         {"level1:30": "A/x", "level1:31": "A/x", "level1:40": "A/x~2"})
        # the object that owns the lowest id keeps the name, whatever its GameObject number is
        objects = {"level1:40": (0, 9), "level1:31": (0, 7), "level1:30": (0, 7)}
        self.assertEqual(audio.disambiguate_sibling_paths(paths, objects)["level1:40"], "A/x~2")
        # both ids of the renamed object get the same new path
        paths = {"level1:1": "A/x", "level1:2": "A/x", "level1:3": "A/x"}
        objects = {"level1:1": (0, 1), "level1:2": (0, 2), "level1:3": (0, 2)}
        self.assertEqual(audio.disambiguate_sibling_paths(paths, objects),
                         {"level1:1": "A/x", "level1:2": "A/x~2", "level1:3": "A/x~2"})

    def test_ids_without_an_object_key_stay_their_own_object(self):
        paths = {"level1:1": "A/x", "level1:2": "A/x", "level1:3": "A/x"}
        self.assertEqual(audio.disambiguate_sibling_paths(paths, {"level1:2": (0, 9), "level1:3": (0, 9)}),
                         {"level1:1": "A/x", "level1:2": "A/x~2", "level1:3": "A/x~2"})
        self.assertEqual(audio.disambiguate_sibling_paths(paths, {}), audio.disambiguate_sibling_paths(paths))

    def test_apply_reads_the_object_from_the_header_pointer(self):
        def record(source_id, path, game_object):
            return {"id": f"level1:{source_id}", "path": path, "gameObject": {"fileId": 0, "pathId": game_object}}
        items = [record(10, "A/Water", 183), record(11, "A/Water", 183),
                 record(20, "A/In/cracles", 300), record(21, "A/In/cracles", 301),
                 record(30, "A/Water/Emitter", 205), record(31, "A/Water/Emitter", 205)]
        self.assertEqual(audio.apply_sibling_paths(items), ["level1:21"])
        self.assertEqual([i["path"] for i in items],
                         ["A/Water", "A/Water", "A/In/cracles", "A/In/cracles~2", "A/Water/Emitter", "A/Water/Emitter"])
        self.assertEqual(items[3]["sourceLeafName"], "cracles")
        self.assertTrue(all("sourceLeafName" not in i for i in items[:3] + items[4:]))
        # the object key is the pointer's (fileId, pathId): the same pathId in another file is another object
        items = [{"id": "level1:1", "path": "A/x", "gameObject": {"fileId": 0, "pathId": 4}},
                 {"id": "level1:2", "path": "A/x", "gameObject": {"fileId": 1, "pathId": 4}}]
        self.assertEqual(audio.apply_sibling_paths(items), ["level1:2"])

    def test_game_object_key(self):
        self.assertEqual(audio.game_object_key({"gameObject": {"fileId": 0, "pathId": 183}}), (0, 183))
        self.assertIsNone(audio.game_object_key({"id": "level1:1"}))


class ForcedMixerPolicyTests(unittest.TestCase):
    """A player inherits its group's mixer unless the contract pins it in states.forced_mixer_players (Bowl's
    metal_rigging forces Arena's 'ArenaCommonAmb'); pinned players carry mixerPolicy 'group' for the importer."""

    @staticmethod
    def player(**fields):
        base = {"id": "level111:557", "playOnAwake": False, "forceSetMixerGroup": False, "mixerGroup": {"pathId": 0}}
        base.update(fields)
        return base

    def test_plain_players_pass_and_any_force_or_awake_fails_without_a_pin(self):
        audio.check_mixer_policy(self.player(), {})
        for fields in ({"playOnAwake": True}, {"forceSetMixerGroup": True}, {"mixerGroup": {"pathId": 8987}},
                       {"mixerPolicy": "group"}):
            with self.subTest(fields=fields), self.assertRaisesRegex(ValueError, "start/mixer policy"):
                audio.check_mixer_policy(self.player(**fields), {})

    def test_a_pinned_player_needs_the_force_the_name_and_the_marker(self):
        forced = {"level111:557": "ArenaCommonAmb"}
        pinned = self.player(forceSetMixerGroup=True, mixerGroup={"pathId": 8987}, mixerGroupName="ArenaCommonAmb",
                             mixerPolicy="group")
        audio.check_mixer_policy(pinned, forced)
        for fields in ({"mixerGroupName": "OtherMixer"}, {"mixerPolicy": None}, {"forceSetMixerGroup": False, "mixerGroup": {"pathId": 0}},
                       {"playOnAwake": True}):
            with self.subTest(fields=fields), self.assertRaisesRegex(ValueError, "start/mixer policy"):
                audio.check_mixer_policy(dict(pinned, **fields), forced)

    def test_apply_marks_only_the_pinned_players(self):
        players = [self.player(), self.player(id="level111:9")]
        self.assertEqual(audio.apply_mixer_policy(players, {"forced_mixer_players": {"level111:9": "ArenaCommonAmb"}}), ["level111:9"])
        self.assertNotIn("mixerPolicy", players[0])
        self.assertEqual(players[1]["mixerPolicy"], "group")
        self.assertEqual(audio.apply_mixer_policy(players, None), [])


if __name__ == "__main__":
    unittest.main()
