"""Gate and door sounds (docs/progress/gate-sounds.md): tools/extract/extract_gate_sounds.py, its tracked inputs
(tools/maps/<stem>.gatesounds.json, tools/extract/gate_sound_fit.json) and the package it stages.

The first classes read only tracked files and synthetic signals (no game data): the row sidecars against the doors sidecars, the
matching rule, the player decoder, the join, the pitch bake, the fitter and the manifest writer. The real-data classes need the
installed Arena (they regenerate the rows, decode the clips and search the key-source files) or the ignored package under
work/extract/gate-sounds, and skip without them.
"""
import contextlib
import copy
import hashlib
import io
import json
import math
import mmap
import struct
import sys
import tempfile
import unittest
import wave
from pathlib import Path
from unittest.mock import patch

import numpy as np

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import extract_gate_sounds as G  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402

MAPS = ("arena_equator", "arena_bay5", "arena_airpit", "arena_bowl")
STEM = {key: key.removeprefix("arena_") for key in MAPS}
PACKAGE = REPO / "work" / "extract" / "gate-sounds"
ARENA = G.doors.DEFAULT_DATA
HAVE_ARENA = (ARENA / "level56").is_file() and (ARENA / "resources.assets").is_file()
HAVE_PACKAGE = all((PACKAGE / key / G.MANIFEST_NAME).is_file() for key in MAPS)


def sidecar(key):
    return json.loads((REPO / "tools" / "maps" / f"{STEM[key]}.gatesounds.json").read_text(encoding="utf-8"))


def doors(key):
    return json.loads((REPO / "tools" / "maps" / f"{STEM[key]}.doors.json").read_text(encoding="utf-8"))


def gain_db(row):
    return 20 * math.log10(row["source_volume"] * row["player_volume"] * 10 ** (row["mixer_db"] / 20))


def part_units(key):
    """The (barrier, part, at, closed local position) of every gate part the client notes for a map, from the doors sidecar: a part's
    closed position is the one of its node "" row (the part itself), None when only nodes below the part move."""
    groups = {}
    for pose in doors(key)["poses"]:
        if "switch" in pose:
            continue
        at = tuple(pose["at"]) if pose["at"] is not None else None
        group = groups.setdefault((pose["barrier"], pose["part"], at), set())
        if pose["node"] == "":
            group.add(tuple(pose["closed"]["position"]))
    return [(barrier, part, at, closed) for (barrier, part, at), closes in sorted(groups.items(), key=str) for closed in (sorted(closes) or [None])]


class RowSidecarTests(unittest.TestCase):
    """The tracked row sidecars: what they say, and that the matching rule gives every gate part of the port at most one sound."""

    COUNTS = {"arena_equator": 5, "arena_bay5": 5, "arena_airpit": 11, "arena_bowl": 4}   # incl. Equator's one borrowed row
    CLIPS = {"arena_equator": {"arena_equator_old_slide_doors"},
             "arena_bay5": {"arena_bay5_roller_door_full", "arena_bay5_big_gates"},
             "arena_airpit": {"arena_airpit_metal_gates", "arena_airpit_bus_doors"},
             "arena_bowl": {"arena_bowl_btr_gates", "arena_bowl_emergency_car_gates_full", "arena_bowl_double_metal_gates", "arena_bowl_roller_gates"}}

    def test_every_released_map_has_its_rows_and_clips(self):
        for key in MAPS:
            with self.subTest(key=key):
                doc = sidecar(key)
                self.assertEqual((doc["schema_version"], doc["key"]), (1, key))
                self.assertEqual(len(doc["rows"]), self.COUNTS[key])
                self.assertEqual({c["id"] for c in doc["clips"]}, self.CLIPS[key])
                self.assertEqual({r["clip"] for r in doc["rows"]}, self.CLIPS[key])
                self.assertEqual(doc["mixer"]["name"], "ArenaGates")
                self.assertEqual(doc["mixer"]["db"], -15.0)
        self.assertEqual(sum(self.COUNTS.values()), 25)   # 24 players of Arena + the borrowed Equator door

    def test_the_gain_of_every_row_is_the_plan_table(self):
        expected = {"arena_equator_old_slide_doors": -15.0, "arena_bay5_roller_door_full": -15.9, "arena_bay5_big_gates": -17.5,
                    "arena_airpit_bus_doors": -19.2, "arena_bowl_btr_gates": -15.0, "arena_bowl_emergency_car_gates_full": -15.0,
                    "arena_bowl_double_metal_gates": -15.0, "arena_bowl_roller_gates": -15.0}
        for key in MAPS:
            for row in sidecar(key)["rows"]:
                with self.subTest(key=key, clip=row["clip"], part=row["part"]):
                    if row["clip"] == "arena_airpit_metal_gates":      # the leaf players name no mixer: 0.2 * 0.9 = -14.9 dB
                        self.assertIsNone(row["mixer"])
                        self.assertEqual(row["mixer_db"], 0.0)
                        self.assertAlmostEqual(gain_db(row), -14.9, places=1)
                    else:
                        self.assertEqual((row["mixer"], row["mixer_db"]), ("ArenaGates", -15.0))
                        self.assertAlmostEqual(gain_db(row), expected[row["clip"]], places=1)

    def test_the_client_plays_at_pitch_one_and_airpits_authored_pitch_is_baked(self):
        authored = {"arena_airpit_metal_gates": 1.25, "arena_airpit_bus_doors": 1.2}
        for key in MAPS:
            for row in sidecar(key)["rows"]:
                with self.subTest(key=key, clip=row["clip"]):
                    self.assertEqual(row["pitch"], 1.0)
                    self.assertEqual(row["authored_pitch"], authored.get(row["clip"], 1.0))
        fit = G.load_fit()["clips"]
        self.assertEqual({name: spec["bake_pitch"] for name, spec in fit.items() if "bake_pitch" in spec}, authored)

    def test_the_rows_validate_against_the_door_rows_and_the_descriptor(self):
        with patch.object(sys, "argv", ["validate_map_descriptors.py"]), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(VD.main(), 0)
        for key in MAPS:
            with self.subTest(key=key):
                descriptor = json.loads((REPO / "tools" / "maps" / f"{STEM[key]}.json").read_text(encoding="utf-8"))
                self.assertEqual(VD.gate_sound_errors(descriptor, REPO / "tools" / "maps" / f"{STEM[key]}.gatesounds.json",
                                                      REPO / "tools" / "maps" / f"{STEM[key]}.doors.json"), [])
                for row in sidecar(key)["rows"]:
                    self.assertTrue(G.row_resolves(row, doors(key)["poses"]), row["origin"])

    def test_no_gate_part_has_two_sounds_and_the_sounding_parts_are_the_plan_set(self):
        sounding = {}
        for key in MAPS:
            rows = sidecar(key)["rows"]
            units = part_units(key)
            hits = {u: [r for r in rows if G.row_applies(r, u[0], u[1], u[2], u[3])] for u in units}
            for unit, found in hits.items():
                self.assertLessEqual(len(found), 1, f"{key}: {unit} has {len(found)} sounds")
            for row in rows:
                self.assertTrue(any(row in found for found in hits.values()), f"{key}: {row['origin']} belongs to no gate part")
            sounding[key] = sorted(u[1] for u, found in hits.items() if found)
        self.assertEqual(len(sounding["arena_equator"]), 10)     # both leaves of the five booth doors
        self.assertEqual(sounding["arena_bay5"], sorted(["", "", "", "Gates_Depot_Door_L", "Gates_Depot_Door_R", "Gates_Depot_Door_L", "Gates_Depot_Door_R"]))
        self.assertEqual(len(sounding["arena_airpit"]), 11)      # 8 passport leaves (the row with z 5.938) + the 3 front bus assemblies
        self.assertEqual(sounding["arena_bowl"], sorted(["BTR_82_closed", "Bowl_Gates_Respawn_01", "Bowl_Respawn_Ambulance_Animated Group", "Gates_Army_01"]))

    def test_airpits_repeated_leaf_names_are_told_apart_by_the_closed_position(self):
        rows = [r for r in sidecar("arena_airpit")["rows"] if r["barrier"] == "PASSPORT_CONTROL_DOORS"]
        self.assertEqual(len(rows), 8)
        self.assertTrue(all(r["closed_position"] is not None and r["closed_position"][2] == 5.938 for r in rows))
        names = sorted(r["part"] for r in rows)
        self.assertEqual(names, sorted(["Passport_control_01_Door (2)"] * 3 + ["Passport_control_01_Door (4)"] * 4 + ["Passport_control_01_Door (6)"]))
        # the leaf (4) at x -5.089 is not the leaf (4) at x 9.802, and a leaf of the silent row (z 3.099) has no sound
        first = next(r for r in rows if r["closed_position"][0] == -5.089)
        self.assertTrue(G.row_applies(first, first["barrier"], "Passport_control_01_Door (4)", None, (-5.089, -3.23, 5.938)))
        self.assertFalse(G.row_applies(first, first["barrier"], "Passport_control_01_Door (4)", None, (9.802, -3.23, 5.938)))
        self.assertFalse(G.row_applies(first, first["barrier"], "Passport_control_01_Door (4)", None, (-5.089, -3.23, 3.099)))

    def test_equators_wildcard_rows_serve_both_leaves_and_the_fifth_door_is_borrowed(self):
        rows = sidecar("arena_equator")["rows"]
        self.assertEqual([r["origin"].startswith("borrowed from") for r in rows].count(True), 1)
        borrowed = next(r for r in rows if r["origin"].startswith("borrowed from"))
        self.assertEqual(borrowed["barrier"], "SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)")
        self.assertEqual(borrowed["emitter"], [-3.753, 1.027, -45.865])   # the lender's offset of 0.979 m up and 0.005 m along z
        lender = next(r for r in rows if r["barrier"] == "SPAWN_DOORS/Door_Sliding_Respawn (1)")
        for field in ("clip", "source_volume", "player_volume", "min_distance", "max_distance", "mixer", "mixer_db"):
            self.assertEqual(borrowed[field], lender[field], field)
        door = lender["barrier"], lender["at"]
        for leaf in ("Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R"):
            self.assertTrue(G.row_applies(lender, door[0], leaf, tuple(door[1]), None))
        self.assertFalse(G.row_applies(lender, door[0], "Door_Sliding_Respawn_L", (4.0, 0.048, 40.0), None))   # another assembly

    def test_the_fit_file_has_an_entry_for_every_clip_and_only_for_those(self):
        fit = G.load_fit()
        used = set().union(*self.CLIPS.values())
        self.assertEqual(set(fit["clips"]), used)
        for name, spec in fit["clips"].items():
            with self.subTest(clip=name):
                self.assertIn(spec["mode"], ("body_cut", "as_is"))
                if spec["mode"] == "body_cut":
                    lo, hi = spec["body"]
                    self.assertTrue(0 < lo < hi and spec["event_s"] > lo)
                else:
                    self.assertNotIn("event_s", spec)
        self.assertEqual(fit["xfade_s"], 0.06)

    def test_no_arena_file_content_or_hash_is_in_the_tracked_files(self):
        texts = [(REPO / "tools" / "extract" / "gate_sound_fit.json").read_text(encoding="utf-8")]
        texts += [(REPO / "tools" / "maps" / f"{STEM[key]}.gatesounds.json").read_text(encoding="utf-8") for key in MAPS]
        for text in texts:
            self.assertNotRegex(text, r"[0-9a-f]{40,}")      # no digest of any file


class MatchingRuleTests(unittest.TestCase):
    ROW = {"barrier": "B/Gate", "part": None, "at": [1.0, 0.0, 2.0], "closed_position": None}

    def test_null_means_every_and_a_value_must_match_within_five_centimetres(self):
        self.assertTrue(G.row_applies(self.ROW, "B/Gate", "L", (1.0, 0.0, 2.0), None))
        self.assertTrue(G.row_applies(self.ROW, "B/Gate", "R", (1.04, 0.0, 2.0), (9.0, 9.0, 9.0)))
        self.assertFalse(G.row_applies(self.ROW, "B/Gate", "L", (1.06, 0.0, 2.0), None))
        self.assertFalse(G.row_applies(self.ROW, "B/Gate", "L", None, None))
        self.assertFalse(G.row_applies(self.ROW, "B/Other", "L", (1.0, 0.0, 2.0), None))
        named = dict(self.ROW, part="L", at=None)
        self.assertTrue(G.row_applies(named, "B/Gate", "L", None, None))
        self.assertTrue(G.row_applies(named, "B/Gate", "L", (5.0, 5.0, 5.0), None))
        self.assertFalse(G.row_applies(named, "B/Gate", "R", None, None))
        root = dict(self.ROW, part="", at=None)
        self.assertTrue(G.row_applies(root, "B/Gate", "", None, None))
        self.assertFalse(G.row_applies(root, "B/Gate", "L", None, None))
        keyed = dict(self.ROW, part="D (4)", at=None, closed_position=[1.0, 2.0, 3.0])
        self.assertTrue(G.row_applies(keyed, "B/Gate", "D (4)", None, (1.0, 2.0, 3.04)))
        self.assertFalse(G.row_applies(keyed, "B/Gate", "D (4)", None, (1.0, 2.0, 3.06)))
        self.assertFalse(G.row_applies(keyed, "B/Gate", "D (4)", None, None))

    def test_the_validator_and_the_extractor_agree_on_which_door_rows_a_sound_row_resolves_to(self):
        poses = [{"barrier": "B/Gate", "part": "L", "node": "", "at": [1.0, 0.0, 2.0], "closed": {"position": [0.5, 0.0, 0.0]}},
                 {"barrier": "B/Gate", "part": "R", "node": "n", "at": [1.0, 0.0, 2.0], "closed": {"position": [-0.5, 0.0, 0.0]}},
                 {"barrier": "B/Lamp", "part": "L", "node": "", "at": None, "closed": {"position": [0, 0, 0]}, "switch": {"closed": False, "open": True}}]
        rows = [dict(self.ROW), dict(self.ROW, part="L"), dict(self.ROW, part="R"), dict(self.ROW, part="X"), dict(self.ROW, at=[9.0, 0.0, 2.0]),
                dict(self.ROW, part="L", closed_position=[0.5, 0.0, 0.03]), dict(self.ROW, part="L", closed_position=[0.5, 0.0, 0.2]),
                dict(self.ROW, part="R", closed_position=[-0.5, 0.0, 0.0]), dict(self.ROW, barrier="B/Lamp", part="L", at=None)]
        for row in rows:
            with self.subTest(part=row["part"], at=row["at"], closed=row["closed_position"], barrier=row["barrier"]):
                self.assertEqual(G.row_resolves(row, poses), VD.gate_row_resolves(row, poses))
        self.assertEqual([G.row_resolves(r, poses) for r in rows], [True, True, True, False, False, True, False, False, False])


class PlayerDecodeTests(unittest.TestCase):
    def tail(self, **over):
        values = dict(entity=-42, source=(0, 1056), clip=(4, 19), volume=0.9, pitch=1.25, w9=0.0, w10=0.1, minimum=0.75, maximum=30.0, w13=0.24, mixer=(2, 8907))
        values.update(over)
        return (struct.pack("<i", values["entity"]) + struct.pack("<iq", *values["source"]) + struct.pack("<iq", *values["clip"])
                + struct.pack("<7f", values["volume"], values["pitch"], values["w9"], values["w10"], values["minimum"], values["maximum"], values["w13"])
                + struct.pack("<iq", *values["mixer"]))

    def test_the_seventeen_words_are_read_in_place(self):
        data = self.tail()
        self.assertEqual(len(data), 68)
        player = G.decode_player(data)
        self.assertEqual((player["entity"], player["source"], player["clip"], player["mixer"]), (-42, (0, 1056), (4, 19), (2, 8907)))
        self.assertAlmostEqual(player["volume"], 0.9, places=6)
        self.assertAlmostEqual(player["pitch"], 1.25, places=6)
        self.assertEqual((player["min"], player["max"]), (0.75, 30.0))

    def test_a_player_of_another_size_stops_the_tool(self):
        for size in (0, 64, 72):
            with self.subTest(size=size), self.assertRaises(G.GateSoundError):
                G.decode_player(b"\0" * size)


class JoinTests(unittest.TestCase):
    @staticmethod
    def candidate(name, world, root=False, part="L", at=(0.0, 0.0, 0.0)):
        return {"group": (name, at), "part": None if root else part, "closed": None, "world": world, "root": root}

    def emitter(self, x, z):
        class E:
            label = "level1 test"
            world = (x, 1.0, z)
        return E()

    def test_the_nearest_place_of_the_nearest_assembly_wins_and_the_root_wins_a_tie(self):
        near = [self.candidate("A", (0, 0, 0), root=True), self.candidate("A", (0.7, 0, 0), part="L"), self.candidate("A", (-0.7, 0, 0), part="R")]
        far = [self.candidate("B", (20, 0, 0), root=True, at=(20.0, 0.0, 0.0))]
        joined = G.join_emitter(self.emitter(0.0, 0.005), near + far)
        self.assertTrue(joined["part"] is None and joined["group"][0] == "A")
        # both leaves at the root (Bay5's depot frame): one sound for the assembly
        stacked = [self.candidate("A", (0, 0, 0), root=True), self.candidate("A", (0, 0, 0), part="L"), self.candidate("A", (0, 0, 0), part="R")]
        self.assertIsNone(G.join_emitter(self.emitter(0.0, 0.0), stacked)["part"])
        # an emitter on a part, away from the root, belongs to the part
        spread = [self.candidate("A", (10, 0, 0), root=True), self.candidate("A", (0.03, 0, 0), part="L"), self.candidate("A", (-3.5, 0, 0), part="R")]
        self.assertEqual(G.join_emitter(self.emitter(0.0, 0.0), spread)["part"], "L")

    def test_an_emitter_with_no_gate_within_two_metres_is_skipped_and_an_unclear_one_stops_the_tool(self):
        places = [self.candidate("A", (0, 0, 0), root=True)]
        self.assertIsNone(G.join_emitter(self.emitter(2.5, 0.0), places))
        self.assertIsNotNone(G.join_emitter(self.emitter(1.9, 0.0), places))
        crowded = places + [self.candidate("B", (3.0, 0, 0), root=True, at=(3.0, 0.0, 0.0))]
        with self.assertRaises(G.GateSoundError):
            G.join_emitter(self.emitter(1.0, 0.0), crowded)


class PitchBakeTests(unittest.TestCase):
    def test_a_baked_sine_is_the_faster_sine_and_nothing_aliases(self):
        t = np.arange(44100) / 44100
        for factor in (1.25, 1.2):
            tone = np.sin(2 * np.pi * 1000 * t)[:, None]
            out = G.resample_speed(tone, factor)
            self.assertEqual(len(out), int(44100 / factor))
            spectrum = np.abs(np.fft.rfft(out[:, 0] * np.hanning(len(out))))
            self.assertAlmostEqual(np.argmax(spectrum) * 44100 / len(out), 1000 * factor, delta=2.0)
            self.assertAlmostEqual(float(np.sqrt((out[2000:-2000] ** 2).mean())), math.sqrt(0.5), delta=0.01)
        high = np.sin(2 * np.pi * 19000 * t)[:, None]          # 19 kHz sped up 1.25x would fold back: the anti-alias filter removes it
        self.assertLess(float(np.sqrt((G.resample_speed(high, 1.25)[2000:-2000] ** 2).mean())), 0.05)

    def test_stereo_channels_stay_apart_and_the_bake_is_deterministic(self):
        left = np.sin(2 * np.pi * 500 * np.arange(8000) / 44100)
        stereo = np.stack([left, np.zeros_like(left)], axis=1)
        a, b = G.resample_speed(stereo, 1.2), G.resample_speed(stereo, 1.2)
        self.assertTrue(np.array_equal(a, b))
        self.assertTrue(np.allclose(a[:, 1], 0.0))

    def test_a_baked_clip_is_no_copy_of_its_source_samples(self):
        rng = np.random.default_rng(7)
        noise = (rng.uniform(-0.5, 0.5, (30000, 2)) * 32767).round() / 32767
        source = G.write_wav(noise, 44100)
        baked = G.write_wav(G.resample_speed(noise, 1.25), 44100)
        source_pcm, baked_pcm = source[44:], baked[44:]
        for start in range(0, len(baked_pcm) - 64, 4096):
            self.assertNotIn(baked_pcm[start:start + 64], source_pcm)


class FitterTests(unittest.TestCase):
    RATE = 8000

    def clip(self):
        """3 s of stationary band noise whose level drifts slowly, with a click at 2.4 s that stands for the end event."""
        rng = np.random.default_rng(11)
        n = 3 * self.RATE
        x = rng.normal(0, 1, n)
        x = np.convolve(x, np.ones(6) / 6, mode="same") * (0.2 + 0.05 * np.sin(np.arange(n) / 3000))
        x[int(2.4 * self.RATE):int(2.4 * self.RATE) + 40] += 0.8
        return np.stack([x, 0.9 * x], axis=1)

    def test_apply_cut_removes_exactly_the_frames_and_keeps_head_and_tail(self):
        x = np.arange(1000, dtype=float)[:, None] * np.ones((1, 2))
        y = G.apply_cut(x, 400, 700, 50)
        self.assertEqual(len(y), 700)
        self.assertTrue(np.array_equal(y[:350], x[:350]))
        self.assertTrue(np.array_equal(y[400:], x[700:]))
        # equal power across the crossfade: cos^2 + sin^2 = 1 for uncorrelated material
        t = np.linspace(0, np.pi / 2, 50, endpoint=False)
        self.assertTrue(np.allclose(np.cos(t) ** 2 + np.sin(t) ** 2, 1.0))
        self.assertEqual(y[350, 0], x[350, 0] * 1.0 + x[650, 0] * 0.0)      # the fade starts on the old material

    def test_the_seam_table_is_consistent_and_deterministic(self):
        x = self.clip()
        anchor = {"event_s": 2.4, "body": [0.4, 2.2]}
        table = G.seam_table(x, self.RATE, anchor, 0.06)
        again = G.seam_table(x, self.RATE, anchor, 0.06)
        self.assertEqual(table, again)
        self.assertEqual((table["mode"], table["xfade_f"], table["body_f"]), ("body_cut", 480, [3200, 17600]))
        self.assertGreater(len(table["seams"]), 5)
        previous = 0.0
        for seam in table["seams"]:
            self.assertGreater(seam["remove_s"], previous - 0.2)           # ascending removals (the +-5 ms alignment may bend one by a few ms)
            previous = seam["remove_s"]
            self.assertAlmostEqual((seam["c1f"] - seam["c0f"]) / self.RATE, seam["remove_s"], delta=0.001)
            self.assertLess(table["body_f"][0], seam["c0f"] - table["xfade_f"] + 1)
            self.assertLessEqual(seam["c1f"], table["body_f"][1] + int(0.005 * self.RATE))
            self.assertIsInstance(seam["ok"], bool)
            self.assertAlmostEqual(seam["c0"], seam["c0f"] / self.RATE, delta=0.0001)
        self.assertTrue(any(seam["ok"] for seam in table["seams"]))

    def test_the_fitter_reproduces_its_own_table_entry(self):
        x = self.clip()
        y, info = G.fit_cut(x, self.RATE, 0.8, 0.4, 2.2, 0.06)
        self.assertEqual(len(y), len(x) - (info["c1"] - info["c0"]))
        self.assertAlmostEqual(info["removed"], 0.8, delta=0.02)
        # the head before the crossfade and the tail after the seam are untouched (the ramp only scales material after the body start)
        self.assertTrue(np.array_equal(y[:info["lo"]], x[:info["lo"]]))
        self.assertTrue(np.array_equal(y[info["c0"]:], x[info["c1"]:]))
        # the click at 2.4 s moved earlier by the removed stretch
        peak = info["c0"] + int(np.argmax(np.abs(y[info["c0"]:, 0])))
        self.assertAlmostEqual(peak, 2.4 * self.RATE - (info["c1"] - info["c0"]), delta=60)


class PackageWriterTests(unittest.TestCase):
    def synthetic_rows(self):
        return {"schema_version": 1, "key": "arena_equator", "mixer": {"name": "ArenaGates", "db": -15.0, "path": "x/ArenaGates"},
                "clips": [{"id": "clip_a", "asset": "sharedassets1.assets", "path_id": 1, "format": "PCM16", "channels": 2, "sample_rate": 8000,
                           "seconds": 3.0, "resource": {"file": "sharedassets1.resource", "offset": 0, "size": 96000}},
                          {"id": "clip_b", "asset": "sharedassets2.assets", "path_id": 2, "format": "PCM16", "channels": 2, "sample_rate": 8000,
                           "seconds": 3.0, "resource": {"file": "sharedassets28.resource", "offset": 0, "size": 96000}}],
                "rows": [{"barrier": "B", "part": None, "at": None, "closed_position": None, "clip": "clip_a"}]}

    def fit(self, bake_b=1.25):
        spec_b = {"mode": "as_is"}
        if bake_b:
            spec_b["bake_pitch"] = bake_b
        return {"xfade_s": 0.06, "clips": {"clip_a": {"mode": "body_cut", "event_s": 2.4, "body": [0.4, 2.2]}, "clip_b": spec_b}}

    def decode(self, entry):
        x = FitterTests().clip()
        return G.write_wav(x, 8000), None

    def test_a_key_source_clip_without_a_bake_is_refused(self):
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(G.GateSoundError, "key-source file"):
            G.build_package("arena_equator", self.synthetic_rows(), self.decode, Path(folder), self.fit(bake_b=None), "0.6.1.0.48206")

    def test_the_package_holds_the_wavs_and_a_manifest_that_lists_them_and_is_byte_stable(self):
        with tempfile.TemporaryDirectory() as folder:
            out = Path(folder) / "arena_equator"
            out.mkdir()
            (out / "stale.wav").write_bytes(b"old")
            manifest = G.build_package("arena_equator", self.synthetic_rows(), self.decode, out, self.fit(), "0.6.1.0.48206")
            self.assertEqual(sorted(p.name for p in out.iterdir()), ["clip_a.wav", "clip_b.wav", "gate-sounds.json"])
            first = {p.name: p.read_bytes() for p in out.iterdir()}
            self.assertEqual(json.loads((out / "gate-sounds.json").read_text(encoding="utf-8")), manifest)
            a, b = manifest["clips"]
            self.assertEqual((a["pitch_baked"], a["baked_pitch"], b["pitch_baked"], b["baked_pitch"]), (False, 1.0, True, 1.25))
            self.assertEqual(b["frames"], int(24000 / 1.25))
            self.assertEqual(b["fit"], {"mode": "as_is"})
            for clip in manifest["clips"]:
                blob = first[clip["file"]]
                self.assertEqual((clip["bytes"], clip["sha256"]), (len(blob), hashlib.sha256(blob).hexdigest()))
                with wave.open(io.BytesIO(blob)) as w:
                    self.assertEqual((w.getnchannels(), w.getframerate(), w.getnframes()), (clip["channels"], clip["sampleRate"], clip["frames"]))
            G.build_package("arena_equator", self.synthetic_rows(), self.decode, out, self.fit(), "0.6.1.0.48206")
            self.assertEqual({p.name: p.read_bytes() for p in out.iterdir()}, first)

    def test_a_silent_or_drifting_clip_stops_the_tool(self):
        silent = lambda entry: (G.write_wav(np.zeros((8000, 2)), 8000), None)
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(G.GateSoundError, "silent"):
            G.build_package("arena_equator", self.synthetic_rows(), silent, Path(folder), self.fit(), "v")
        drift = lambda entry: (G.write_wav(FitterTests().clip(), 8000), {"m_Channels": 2, "m_Frequency": 8000, "m_Length": 9.0})
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(G.GateSoundError, "duration drift"):
            G.build_package("arena_equator", self.synthetic_rows(), drift, Path(folder), self.fit(), "v")

    def test_the_sidecar_text_is_valid_json_and_stable(self):
        for key in MAPS:
            doc = sidecar(key)
            text = G.render_rows_sidecar(doc)
            self.assertEqual(json.loads(text), doc)
            self.assertEqual(text, (REPO / "tools" / "maps" / f"{STEM[key]}.gatesounds.json").read_bytes().decode("utf-8").replace("\r\n", "\n"))

    def test_the_validator_refuses_a_row_that_matches_no_door_and_a_client_pitch_other_than_one(self):
        key = "arena_bowl"
        descriptor = json.loads((REPO / "tools" / "maps" / "bowl.json").read_text(encoding="utf-8"))
        doc = sidecar(key)
        doors_path = REPO / "tools" / "maps" / "bowl.doors.json"
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "bowl.gatesounds.json"

            def errors(mutate):
                changed = copy.deepcopy(doc)
                mutate(changed)
                path.write_text(json.dumps(changed), encoding="utf-8")
                return VD.gate_sound_errors(descriptor, path, doors_path)

            self.assertEqual(errors(lambda d: None), [])
            self.assertTrue(any("matches no transform row" in e for e in errors(lambda d: d["rows"][0].update(part="Nope"))))
            self.assertTrue(any("pitch must be 1.0" in e for e in errors(lambda d: d["rows"][0].update(pitch=1.25))))
            self.assertTrue(any("mixer" in e for e in errors(lambda d: d["rows"][0].update(mixer_db=-3.0))))
            self.assertTrue(any("not in clips" in e for e in errors(lambda d: d["rows"][0].update(clip="nope"))))
            self.assertTrue(any("duplicate row" in e for e in errors(lambda d: d["rows"].append(copy.deepcopy(d["rows"][0])))))
            self.assertTrue(any("used by no row" in e for e in errors(lambda d: d["rows"].pop())))
            self.assertTrue(any("exactly the keys" in e for e in errors(lambda d: d["rows"][0].pop("origin"))))


def contains_pcm(path, pcm, windows=8, size=512):
    """Does any of `windows` slices of `pcm` occur in the file? (mmap search; the shipped bytes must not be a copy of an Arena file.)"""
    if not path.is_file():
        return False
    offsets = [int(len(pcm) * i / (windows + 1)) // 4 * 4 for i in range(1, windows + 1)]
    with path.open("rb") as stream:
        mapped = mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            return any(mapped.find(pcm[o:o + size]) >= 0 for o in offsets)
        finally:
            mapped.close()


def wav_pcm(path):
    with wave.open(str(path)) as w:
        return w.readframes(w.getnframes())


@unittest.skipUnless(HAVE_PACKAGE, "work/extract/gate-sounds is not extracted (tools/extract/extract_gate_sounds.py clips)")
class StagedPackageTests(unittest.TestCase):
    """The ignored package as the extractor left it: manifests, WAVs, seam tables and the key-source rule."""

    FRAMES = {"arena_equator_old_slide_doors": 157396, "arena_bay5_roller_door_full": 228445, "arena_bay5_big_gates": 269206,
              "arena_airpit_metal_gates": 141120, "arena_airpit_bus_doors": 93635, "arena_bowl_btr_gates": 529200,
              "arena_bowl_emergency_car_gates_full": 319094, "arena_bowl_double_metal_gates": 336262, "arena_bowl_roller_gates": 270689}
    # the seam the runtime picks at SpawnGateDriveScale 0.5: the largest ok removal not above (1 - 0.5) * event_s, and where the end event lands
    PICKS_AT_HALF = {"arena_equator_old_slide_doors": (1.303, 1.697), "arena_bay5_roller_door_full": (1.999, 2.021),
                     "arena_bay5_big_gates": (1.205, 1.345), "arena_bowl_btr_gates": (2.9, 3.05),
                     "arena_bowl_emergency_car_gates_full": (3.001, 3.159), "arena_bowl_double_metal_gates": (2.403, 2.477),
                     "arena_bowl_roller_gates": (2.301, 2.449)}

    def manifest(self, key):
        return json.loads((PACKAGE / key / G.MANIFEST_NAME).read_text(encoding="utf-8"))

    def clips(self):
        return {c["id"]: (key, c) for key in MAPS for c in self.manifest(key)["clips"]}

    def test_manifest_rows_are_the_tracked_sidecar_rows_and_every_wav_is_intact(self):
        for key in MAPS:
            with self.subTest(key=key):
                manifest = self.manifest(key)
                self.assertEqual((manifest["schema_version"], manifest["map_key"]), (1, key))
                self.assertEqual(manifest["rows"], sidecar(key)["rows"])
                self.assertEqual(manifest["mixer"], sidecar(key)["mixer"])
                self.assertEqual(sorted(p.name for p in (PACKAGE / key).iterdir()),
                                 sorted([G.MANIFEST_NAME] + [c["file"] for c in manifest["clips"]]))
                for clip in manifest["clips"]:
                    blob = (PACKAGE / key / clip["file"]).read_bytes()
                    self.assertEqual((clip["bytes"], clip["sha256"]), (len(blob), hashlib.sha256(blob).hexdigest()))
                    self.assertEqual((clip["channels"], clip["sampleRate"]), (2, 44100))
                    self.assertEqual(clip["frames"], self.FRAMES[clip["id"]])
                    self.assertEqual(len(blob), 44 + 4 * clip["frames"])

    def test_only_airpits_two_clips_are_pitch_baked_and_their_source_is_named_without_a_hash(self):
        clips = self.clips()
        baked = {name: c["baked_pitch"] for name, (key, c) in clips.items() if c["pitch_baked"]}
        self.assertEqual(baked, {"arena_airpit_metal_gates": 1.25, "arena_airpit_bus_doors": 1.2})
        self.assertEqual(clips["arena_airpit_metal_gates"][1]["source"]["resource"]["file"], "sharedassets28.resource")
        for name, (key, c) in clips.items():
            self.assertEqual(set(c["source"]), {"asset", "path_id", "format", "resource"})
            self.assertEqual(set(c["source"]["resource"]), {"file", "offset", "size"})

    def test_seam_tables_are_consistent_and_the_runtime_picks_are_the_reviewed_ones(self):
        for name, (key, clip) in self.clips().items():
            fit = clip["fit"]
            with self.subTest(clip=name):
                self.assertEqual(fit["mode"], "as_is" if name in ("arena_airpit_metal_gates", "arena_airpit_bus_doors") else "body_cut")
                if fit["mode"] != "body_cut":
                    continue
                self.assertEqual((fit["xfade_s"], fit["xfade_f"]), (0.06, 2646))
                lo, hi = fit["body_f"]
                for seam in fit["seams"]:
                    self.assertAlmostEqual((seam["c1f"] - seam["c0f"]) / 44100, seam["remove_s"], delta=0.001)   # not a clean 0.1 s grid: 1 ms
                    self.assertGreaterEqual(seam["c0f"] - fit["xfade_f"], lo)
                    self.assertLessEqual(seam["c1f"], hi + int(0.005 * 44100))
                    self.assertAlmostEqual(seam["c0"], seam["c0f"] / 44100, delta=0.0001)
                half = [s for s in fit["seams"] if s["ok"] and s["remove_s"] <= 0.5 * fit["event_s"] + 1e-9]
                pick = max(half, key=lambda s: s["remove_s"])
                removal, lands = self.PICKS_AT_HALF[name]
                self.assertEqual(pick["remove_s"], removal)
                self.assertAlmostEqual(fit["event_s"] - pick["remove_s"], lands, places=3)
        # not within 0.01 s of the ideal for every clip: only the Bay5 roller is (the review of 2026-10-25)
        late = {name: round(self.PICKS_AT_HALF[name][1] - 0.5 * self.clips()[name][1]["fit"]["event_s"], 3) for name in self.PICKS_AT_HALF}
        self.assertEqual([n for n, v in late.items() if v <= 0.011], ["arena_bay5_roller_door_full"])
        self.assertEqual(late["arena_equator_old_slide_doors"], 0.197)

    def test_no_shipped_sample_run_occurs_in_a_key_source_file_of_its_map(self):
        if not (ARENA / "level56").is_file():
            self.skipTest("Arena is not installed")
        table = json.loads(G.KEY_SOURCES.read_text(encoding="utf-8"))["maps"]
        for key in MAPS:
            files = sorted({ARENA / Path(f).name for entry in table[key] for f in entry["files"]})
            for clip in self.manifest(key)["clips"]:
                with self.subTest(map=key, clip=clip["id"]):
                    pcm = wav_pcm(PACKAGE / key / clip["file"])
                    for path in files:
                        self.assertFalse(contains_pcm(path, pcm), f"{clip['id']} occurs in {path.name}")

    def test_the_overlap_detector_finds_the_copy_the_bake_prevents(self):
        resource = ARENA / "sharedassets28.resource"
        if not resource.is_file():
            self.skipTest("Arena is not installed")
        original = resource.read_bytes()[96:96 + 705600]            # the unbaked PCM payload of the passport clip, as the review found it
        self.assertTrue(contains_pcm(resource, original))
        baked = wav_pcm(PACKAGE / "arena_airpit" / "arena_airpit_metal_gates.wav")
        self.assertFalse(contains_pcm(resource, baked))


@unittest.skipUnless(HAVE_ARENA, "Arena is not installed")
class RealDataRowTests(unittest.TestCase):
    def test_the_tracked_sidecars_are_what_the_installed_arena_gives(self):
        version = G.doors.arena_version(ARENA)
        mixers = G.MixerBook(ARENA)
        for key in MAPS:
            with self.subTest(key=key):
                doc = G.extract_rows(key, ARENA, version, mixers)
                tracked = (REPO / "tools" / "maps" / f"{STEM[key]}.gatesounds.json").read_bytes().decode("utf-8").replace("\r\n", "\n")
                self.assertEqual(G.render_rows_sidecar(doc), tracked)
                self.assertEqual(len(doc["rows"]), RowSidecarTests.COUNTS[key])

    @unittest.skipUnless(HAVE_PACKAGE, "work/extract/gate-sounds is not extracted (tools/extract/extract_gate_sounds.py clips)")
    def test_every_shipped_clip_is_the_decode_of_arenas_clip_or_its_pitch_bake(self):
        decode = G.arena_decoder(ARENA)
        fit = G.load_fit()["clips"]
        seen = 0
        for key in MAPS:
            for entry in sidecar(key)["clips"]:
                with self.subTest(clip=entry["id"]):
                    wav, metadata = decode(entry)
                    shipped = (PACKAGE / key / (entry["id"] + ".wav")).read_bytes()
                    self.assertEqual(metadata["m_Name"], entry["id"])
                    self.assertEqual(metadata["m_Resource"]["m_Source"], entry["resource"]["file"])
                    bake = fit[entry["id"]].get("bake_pitch")
                    if bake is None:
                        self.assertEqual(wav, shipped)                       # an unbaked clip ships as the decoder wrote it
                    else:
                        samples, rate = G.read_wav(wav)
                        self.assertNotEqual(wav, shipped)
                        self.assertEqual(shipped, G.write_wav(G.resample_speed(samples, bake), rate))    # and the bake is reproducible
                        self.assertEqual(len(shipped), 44 + 4 * int(len(samples) / bake))
                    seen += 1
        self.assertEqual(seen, 9)

    def test_the_players_are_one_hundred_bytes_and_the_mixer_is_minus_fifteen_decibels(self):
        scene = G.doors.ArenaScene(ARENA, 56)
        emitters = G.read_emitters(scene, 56)
        self.assertEqual(len(emitters), 4)
        mixers = G.MixerBook(ARENA)
        group = mixers.group(emitters[0].player["mixer"][1])
        self.assertEqual((group["name"], group["db"]), ("ArenaGates", -15.0))
        self.assertEqual(group["path"], "Master/InGame/World/Main/Ambient/ArenaCommonAmb/ArenaGates")


if __name__ == "__main__":
    unittest.main()
