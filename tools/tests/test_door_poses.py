"""Arena's gate animations as end poses (docs/progress/door-poses.md): tools/extract/extract_door_poses.py and its sidecar.

The decoder tests build synthetic Mecanim clips in memory (no game data). The sidecar tests read only the tracked
tools/maps/bowl.doors.json and the C# registry through validate_map_descriptors.py. One real-data class re-extracts the
sidecar from the installed Arena and skips without Arena or UnityPy.
"""
import contextlib
import copy
import io
import json
import math
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import extract_door_poses as E  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402

SIDECAR = REPO / "tools" / "maps" / "bowl.doors.json"
BOWL = REPO / "tools" / "maps" / "bowl.json"
PACKAGE = REPO / "work" / "extract" / "bowl-native-v2"
ARENA_DATA = E.DEFAULT_DATA
HAVE_ARENA = (ARENA_DATA / "level48").is_file() and (ARENA_DATA / "sharedassets48.assets").is_file()
NEG_INF = -3.4028234663852886e+38   # the first "key" every streamed curve carries
E_KEYS = ("barrier", "part", "node", "clip", "seconds", "closed", "open")


def streamed_blob(frames):
    """Pack frames [(time, [(curve, a, b, c, d), ...]), ...] the way a StreamedClip stores them, plus the +inf end frame."""
    out = b""
    for time, keys in frames + [(math.inf, [])]:
        out += struct.pack("<fI", time, len(keys))
        for key in keys:
            out += struct.pack("<I4f", *key)
    return out


def clip_typetree(name, stop, bindings, streamed=(), n_streamed=0, dense=None, constants=()):
    """An AnimationClip typetree with the fields build_clip reads. bindings: [(path_hash, kind, type_id)]."""
    dense = dense or {"m_FrameCount": 0, "m_CurveCount": 0, "m_SampleRate": 60.0, "m_BeginTime": 0.0, "m_SampleArray": []}
    return {
        "m_Name": name,
        "m_MuscleClip": {
            "m_StopTime": stop,
            "m_Clip": {"data": {
                "m_StreamedClip": {"data": list(struct.unpack("<%dI" % (len(streamed_blob(list(streamed))) // 4),
                                                              streamed_blob(list(streamed)))), "curveCount": n_streamed},
                "m_DenseClip": dense,
                "m_ConstantClip": {"data": list(constants)},
            }},
        },
        "m_ClipBindingConstant": {"genericBindings": [
            {"path": h, "attribute": kind, "typeID": type_id, "customType": 0, "isPPtrCurve": False}
            for h, kind, type_id in bindings]},
    }


class PathHashTests(unittest.TestCase):
    def test_crc32_of_the_utf8_path_names_the_nodes_of_arenas_gates(self):
        # Hashes read from the clips of level48's sharedassets (numbers only), resolved to the node names beneath the Animator.
        for path, expected in (("Gates_Army_door_01", 1174491159), ("Gates_Army_door_02", 3741843885),
                               ("Bowl_Gates_Respawn_01_Gate", 4194129352), ("BTR_82_wheel_L_01", 171395748),
                               ("BTR_82_wheel_R_04", 3399196295),
                               ("Bowl_Respawn_Ambulance_Body/Bowl_Respawn_Ambulance_Body_Back_Door_R", 44582716)):
            with self.subTest(path=path):
                self.assertEqual(E.crc32_path(path), expected)

    def test_the_empty_path_is_the_animators_own_object(self):
        self.assertEqual(E.crc32_path(""), 0)

    def test_the_hash_is_case_and_separator_sensitive(self):
        self.assertNotEqual(E.crc32_path("gates_army_door_01"), E.crc32_path("Gates_Army_door_01"))
        self.assertNotEqual(E.crc32_path("A/B"), E.crc32_path("A\\B"))


class OpenClipRuleTests(unittest.TestCase):
    def test_the_open_clip_is_the_one_not_ending_in_closed(self):
        self.assertEqual(E.pick_open_clip(["BTR_82_Respawn_Animation", "BTR_82_Respawn_Animation_closed"]), "BTR_82_Respawn_Animation")
        self.assertEqual(E.pick_open_clip(["Closed", "Opened"]), "Opened")
        self.assertEqual(E.pick_open_clip(["Bowl_Gates_Respawn_01_Animation"]), "Bowl_Gates_Respawn_01_Animation")
        self.assertEqual(E.pick_open_clip(["Gates_Army_02_Animation_CLOSED", "Gates_Army_02_Animation"]), "Gates_Army_02_Animation")

    def test_zero_or_several_candidates_stop_the_tool(self):
        with self.assertRaises(E.DoorPoseError):
            E.pick_open_clip(["Door_closed", "Gate_Closed"])
        with self.assertRaises(E.DoorPoseError):
            E.pick_open_clip(["Open_A", "Open_B", "Rest_closed"])
        with self.assertRaises(E.DoorPoseError):
            E.pick_open_clip([])

    def test_a_gate_path_splits_at_the_longest_opened_barrier(self):
        barriers = ["DOORS_SPAWN_B", "WAREHOUSE_DOORS (1)/Gate (3)", "WAREHOUSE_DOORS (1)"]
        self.assertEqual(E.split_gate_path("DOORS_SPAWN_B/Gates_Army_01/Gates_Army", barriers),
                         ("DOORS_SPAWN_B", "Gates_Army_01", "Gates_Army"))
        self.assertEqual(E.split_gate_path("DOORS_SPAWN_B/Bowl_Respawn_Ambulance_Animated Group/Bowl_Respawn_Ambulance_Animated", barriers),
                         ("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated"))
        self.assertEqual(E.split_gate_path("WAREHOUSE_DOORS (1)/Gate (3)/Leaf/Hinge", barriers),
                         ("WAREHOUSE_DOORS (1)/Gate (3)", "Leaf", "Hinge"))
        self.assertEqual(E.split_gate_path("DOORS_SPAWN_B/BTR", barriers), ("DOORS_SPAWN_B", "BTR", ""))
        self.assertIsNone(E.split_gate_path("DOORS_SPAWN_B", barriers))          # the barrier root itself
        self.assertIsNone(E.split_gate_path("DOORS_SPAWN_BX/Leaf", barriers))    # a prefix is not a barrier
        self.assertIsNone(E.split_gate_path("Elsewhere/Leaf", barriers))


class StreamedClipDecoderTests(unittest.TestCase):
    def test_frames_keep_their_keys_and_drop_the_terminating_infinity(self):
        blob = streamed_blob([(NEG_INF, [(0, 0.0, 0.0, 0.0, 5.0)]),
                              (0.0, [(0, 1.0, 2.0, 3.0, 5.0), (1, 0.0, 0.0, 0.0, -2.0)]),
                              (2.5, [(0, 0.0, 0.0, 0.0, 9.0)])])
        frames = E.decode_streamed(blob)
        self.assertEqual([round(t, 3) for t, _ in frames if t > -1e30], [0.0, 2.5])
        self.assertEqual(len(frames), 3)
        self.assertEqual(frames[1][1], [(0, 1.0, 2.0, 3.0, 5.0), (1, 0.0, 0.0, 0.0, -2.0)])

    def test_words_and_bytes_decode_alike(self):
        blob = streamed_blob([(0.0, [(2, 0.0, 0.0, 0.0, 1.5)])])
        words = list(struct.unpack("<%dI" % (len(blob) // 4), blob))
        self.assertEqual(E.decode_streamed(words), E.decode_streamed(blob))

    def test_a_truncated_blob_is_refused(self):
        blob = streamed_blob([(0.0, [(0, 0.0, 0.0, 0.0, 1.0)])])
        with self.assertRaises(E.DoorPoseError):
            E.decode_streamed(blob[:16])   # cut inside the first key

    def test_a_cubic_key_evaluates_after_its_time_and_the_last_key_holds(self):
        # v(dt) = a*dt^3 + b*dt^2 + c*dt + d after the key; the d of a key is the value AT the key.
        curve = E.Curve(keys=[(NEG_INF, 0.0, 0.0, 0.0, 1.0), (0.0, 0.0, 0.0, 2.0, 1.0), (2.0, 0.0, 0.0, 0.0, 5.0)])
        self.assertEqual(curve.start(), 1.0)
        self.assertEqual(curve.end(), 5.0)
        self.assertAlmostEqual(curve.at(1.0), 3.0)      # 1 + 2 * 1
        self.assertAlmostEqual(curve.at(2.0), 5.0)
        self.assertAlmostEqual(curve.at(99.0), 5.0)     # holds after the last key
        self.assertAlmostEqual(curve.at(-1.0), 1.0)     # before the first real key: the start value
        cubic = E.Curve(keys=[(0.0, 1.0, 1.0, 1.0, 1.0)])
        self.assertAlmostEqual(cubic.at(2.0), 8 + 4 + 2 + 1)

    def test_an_infinite_slope_key_is_a_step(self):
        step = E.Curve(keys=[(0.0, math.inf, math.inf, math.inf, 2.0), (1.0, 0.0, 0.0, 0.0, 7.0)])
        self.assertEqual(step.at(0.5), 2.0)
        self.assertEqual(step.at(1.0), 7.0)

    def test_a_dense_curve_interpolates_its_samples(self):
        dense = E.Curve(samples=[0.0, 10.0, 20.0], rate=2.0, begin=0.0)
        self.assertEqual(dense.start(), 0.0)
        self.assertEqual(dense.end(), 20.0)
        self.assertAlmostEqual(dense.at(0.25), 5.0)
        self.assertAlmostEqual(dense.at(5.0), 20.0)


class BuildClipTests(unittest.TestCase):
    def test_streamed_dense_and_constant_curves_follow_the_binding_order(self):
        # binding 0: position, 3 curves; binding 1: euler, 3 curves; binding 2: scale, 3 curves.
        frames = [(NEG_INF, [(0, 0, 0, 0, 1.0), (1, 0, 0, 0, 2.0), (2, 0, 0, 0, 3.0)]),
                  (0.0, [(0, 0, 0, 0, 1.0), (1, 0, 0, 0, 2.0), (2, 0, 0, 0, 3.0)]),
                  (4.0, [(0, 0, 0, 0, 11.0), (1, 0, 0, 0, 12.0), (2, 0, 0, 0, 13.0)])]
        dense = {"m_FrameCount": 3, "m_CurveCount": 3, "m_SampleRate": 1.0, "m_BeginTime": 0.0,
                 "m_SampleArray": [0, 0, 0, 5, 6, 7, 20, 30, 40]}
        tt = clip_typetree("Open", 4.0, [(0, E.BIND_POSITION, 4), (7, E.BIND_EULER, 4), (9, E.BIND_SCALE, 4)],
                           streamed=frames, n_streamed=3, dense=dense, constants=[1.0, 1.0, 1.0])
        # the three bindings span 9 curves but only 3 + 3 + 3 are stored: streamed 0-2, dense 3-5, constant 6-8
        clip = E.build_clip(tt)
        self.assertEqual(clip.name, "Open")
        self.assertEqual(clip.stop, 4.0)
        by_kind = {c.kind: c for c in clip.channels}
        self.assertEqual(by_kind[E.BIND_POSITION].values_end, [11.0, 12.0, 13.0])
        self.assertEqual(by_kind[E.BIND_POSITION].values_start, [1.0, 2.0, 3.0])
        self.assertEqual(by_kind[E.BIND_EULER].values_end, [20.0, 30.0, 40.0])
        self.assertEqual(by_kind[E.BIND_SCALE].values_end, [1.0, 1.0, 1.0])
        self.assertEqual(by_kind[E.BIND_EULER].path_hash, 7)

    def test_a_quaternion_binding_takes_four_curves(self):
        frames = [(0.0, [(i, 0, 0, 0, v) for i, v in enumerate((0.0, 0.0, 0.0, 1.0))])]
        clip = E.build_clip(clip_typetree("Rot", 1.0, [(1, E.BIND_ROTATION, 4)], streamed=frames, n_streamed=4))
        self.assertEqual(clip.channels[0].values_end, [0.0, 0.0, 0.0, 1.0])

    def test_bindings_that_do_not_cover_the_stored_curves_are_refused(self):
        frames = [(0.0, [(0, 0, 0, 0, 1.0), (1, 0, 0, 0, 1.0), (2, 0, 0, 0, 1.0)])]
        with self.assertRaises(E.DoorPoseError):
            E.build_clip(clip_typetree("Bad", 1.0, [(0, E.BIND_POSITION, 4)], streamed=frames, n_streamed=3, constants=[0.0]))

    def test_a_missing_curve_is_refused(self):
        with self.assertRaises(E.DoorPoseError):
            E.build_clip(clip_typetree("Bad", 1.0, [(0, E.BIND_POSITION, 4)], streamed=[], n_streamed=0))

    def test_non_transform_bindings_are_counted_not_decoded(self):
        frames = [(0.0, [(0, 0, 0, 0, 1.0)])]
        tt = clip_typetree("Float", 1.0, [(0, 123456, 114)], streamed=frames, n_streamed=1)
        clip = E.build_clip(tt)
        self.assertEqual(clip.channels, [])
        self.assertEqual(clip.unsupported, 1)

    def test_a_streamed_key_past_the_curve_count_is_refused(self):
        with self.assertRaises(E.DoorPoseError):
            E.build_clip(clip_typetree("Bad", 1.0, [(0, E.BIND_POSITION, 4)],
                                       streamed=[(0.0, [(5, 0, 0, 0, 1.0)])], n_streamed=3))


class CsharpLiteralTests(unittest.TestCase):
    POSE = {"barrier": "B", "part": "P", "node": "", "clip": "C", "seconds": 8.3,
            "closed": {"position": [0.0, -0.035, 51.182], "euler": [-90.0, 0.0, 83.511]},
            "open": {"position": [4.773, 0.0, 0.0], "euler": [-90.3, 266.474, -182.962]}}

    def test_one_pose_is_three_lines_of_float_literals(self):
        self.assertEqual(E.render_csharp([self.POSE], indent="  ").split("\n"), [
            '  new ArenaDoorPose("B", "P", "", "C", 8.3f,',
            '      new[] { 0.0f, -0.035f, 51.182f }, new[] { -90.0f, 0.0f, 83.511f },',
            '      new[] { 4.773f, 0.0f, 0.0f }, new[] { -90.3f, 266.474f, -182.962f }),'])

    def test_a_number_without_a_plain_spelling_is_refused(self):
        with self.assertRaises(E.DoorPoseError):
            E.csharp_float(1e-7)


class KeyResidualTests(unittest.TestCase):
    """key_residual: the decode self-check (each key's cubic must reach the next key's value)."""

    @staticmethod
    def clip(keys):
        return E.Clip("c", 2.0, [E.Channel(1, E.BIND_POSITION, [E.Curve(keys=keys), E.Curve(constant=0.0), E.Curve(samples=[0.0, 1.0], rate=1.0)])])

    def test_a_consistent_curve_passes(self):
        # span 1: v = 2 dt from 0 s to 1 s, which reaches 2.0; span 2: v = dt^3 - dt^2 + 2, which is 2.0 again at 2 s
        keys = [(NEG_INF, 0, 0, 0, 0.0), (0.0, 0.0, 0.0, 2.0, 0.0), (1.0, 1.0, -1.0, 0.0, 2.0), (2.0, 0.0, 0.0, 0.0, 2.0)]
        spans, worst, bad = E.key_residual(self.clip(keys))
        self.assertEqual((spans, bad), (2, 0))
        self.assertLess(worst, 1e-9)

    def test_a_wrong_value_or_coefficient_is_caught(self):
        keys = [(NEG_INF, 0, 0, 0, 0.0), (0.0, 0.0, 0.0, 2.0, 0.0), (1.0, 0.0, 0.0, 0.0, 5.0)]
        spans, worst, bad = E.key_residual(self.clip(keys))
        self.assertEqual((spans, bad), (1, 1))
        self.assertAlmostEqual(worst, 3.0)

    def test_stepped_keys_are_skipped(self):
        keys = [(0.0, math.inf, math.inf, math.inf, 1.0), (1.0, 0.0, 0.0, 0.0, 9.0)]
        self.assertEqual(E.key_residual(self.clip(keys)), (0, 0.0, 0))


class RotationHelperTests(unittest.TestCase):
    SCENE_BTR = (-0.5275, 0.4709, 0.4709, 0.5275)    # DOORS_SPAWN_A/BTR_82_closed, local rotation (xyzw), 4 decimals

    def test_unitys_euler_matches_the_btr_scene_rotation(self):
        # the clip's raw closed euler (-90, 0, 83.511) and the scene quaternion are the same rotation
        self.assertLess(E.quat_angle(E.euler_to_quat((-90.0, 0.0, 83.511)), self.SCENE_BTR), 0.05)
        # the same heading written (270, 83.5, 0), what the quaternion alone converts to at the gimbal lock
        self.assertLess(E.quat_angle(E.euler_to_quat((270.0, 83.511, 0.0)), self.SCENE_BTR), 0.05)
        euler = E.quat_to_euler(self.SCENE_BTR)
        self.assertAlmostEqual(euler[0], 270.0, delta=0.1)
        self.assertLess(E.quat_angle(E.euler_to_quat(euler), self.SCENE_BTR), 0.05)

    def test_euler_round_trip_away_from_the_gimbal_lock(self):
        for euler in ((10.0, 20.0, 30.0), (-45.0, 170.0, -100.0), (0.0, 3.0, 0.0), (80.0, 200.0, 5.0), (0.0, 0.0, 142.922)):
            with self.subTest(euler=euler):
                back = E.quat_to_euler(E.euler_to_quat(euler))
                self.assertLess(E.quat_angle(E.euler_to_quat(back), E.euler_to_quat(euler)), 1e-3)
                for a, b in zip(back, euler):
                    self.assertAlmostEqual((a - b + 180.0) % 360.0 - 180.0, 0.0, places=3)

    def test_the_angle_between_rotations_ignores_the_quaternion_sign(self):
        q = E.euler_to_quat((10.0, 20.0, 30.0))
        self.assertAlmostEqual(E.quat_angle(q, tuple(-v for v in q)), 0.0, places=3)
        self.assertAlmostEqual(E.quat_angle(E.euler_to_quat((0, 0, 0)), E.euler_to_quat((0, 90, 0))), 90.0, places=3)

    def test_branch_helpers(self):
        self.assertAlmostEqual(E.unwrap_near(350.0, 10.0), -10.0)
        self.assertAlmostEqual(E.unwrap_near(-170.0, 170.0), 190.0)
        self.assertAlmostEqual(E.centered(270.0), -90.0)
        self.assertAlmostEqual(E.centered(180.0), 180.0)
        self.assertEqual(E.tidy([-0.0001, 1.23456, 266.4744]), [0.0, 1.235, 266.474])
        self.assertFalse(math.copysign(1.0, E.tidy([-0.0001])[0]) < 0)    # no negative zero in the sidecar

    def test_a_quaternion_channel_that_spins_several_turns_keeps_its_turns(self):
        # a wheel turning 800 degrees about local y, sampled densely: the end euler is 800, not 800 mod 360
        samples = 81
        angles = [800.0 * i / (samples - 1) for i in range(samples)]
        quats = [E.euler_to_quat((0.0, a, 0.0)) for a in angles]
        curves = [E.Curve(samples=[q[axis] for q in quats], rate=float(samples - 1), begin=0.0) for axis in range(4)]
        channel = E.Channel(1, E.BIND_ROTATION, curves)
        end = E.continuous_euler(channel, 1.0, [0.0, 0.0, 0.0])
        self.assertAlmostEqual(end[1], 800.0, delta=1.0)
        self.assertAlmostEqual(end[0], 0.0, delta=0.5)
        self.assertAlmostEqual(end[2], 0.0, delta=0.5)


class SolveNodeTests(unittest.TestCase):
    def channels(self, kinds):
        out = {}
        for kind, (start, end) in kinds.items():
            keys = [[(NEG_INF, 0, 0, 0, s), (0.0, 0, 0, 0, s), (2.0, 0, 0, 0, e)] for s, e in zip(start, end)]
            out[kind] = E.Channel(1, kind, [E.Curve(keys=k) for k in keys])
        return out

    def test_a_moved_euler_node_keeps_the_raw_open_value_and_the_scene_closed_pose(self):
        scene_q = E.euler_to_quat((0.0, 0.0, 0.0))
        ch = self.channels({E.BIND_EULER: ((0.0, 0.0, 0.369), (0.0, 0.0, 142.922))})
        # kind keys are ints, build with positional dict
        solved, issues, scale_changed = E.solve_node((1.95, 0.0, 1.1), scene_q, (1, 1, 1), ch, {}, "door", 2.0)
        self.assertTrue(solved["moved"])
        self.assertEqual(tidy3(solved["open_euler"]), [0.0, 0.0, 142.922])
        # the scene says 0, the clip's first key 0.369 degrees: the closed pose is the scene's, a warning-free offset
        self.assertEqual(tidy3(solved["closed_euler"]), [0.0, 0.0, 0.0])
        self.assertEqual(issues, [])
        self.assertFalse(scale_changed)
        self.assertEqual(solved["closed_position"], [1.95, 0.0, 1.1])
        self.assertEqual(solved["open_position"], [1.95, 0.0, 1.1])

    def test_a_node_that_ends_where_it_started_is_not_a_move(self):
        ch = self.channels({E.BIND_POSITION: ((1.0, 2.0, 3.0), (1.004, 2.0, 3.0))})
        solved, _, _ = E.solve_node((1.0, 2.0, 3.0), (0, 0, 0, 1), (1, 1, 1), ch, {}, "n", 2.0)
        self.assertFalse(solved["moved"])
        ch = self.channels({E.BIND_POSITION: ((1.0, 2.0, 3.0), (1.02, 2.0, 3.0))})
        solved, _, _ = E.solve_node((1.0, 2.0, 3.0), (0, 0, 0, 1), (1, 1, 1), ch, {}, "n", 2.0)
        self.assertTrue(solved["moved"])

    def test_a_raw_spin_by_whole_turns_counts_as_a_move(self):
        ch = self.channels({E.BIND_EULER: ((0.0, 3.0, 0.0), (-720.0, 3.0, 0.0))})
        scene_q = E.euler_to_quat((0.0, 3.0, 0.0))
        solved, _, _ = E.solve_node((0, 0, 0), scene_q, (1, 1, 1), ch, {}, "wheel", 2.0)
        self.assertTrue(solved["moved"])
        self.assertEqual(tidy3(solved["open_euler"]), [-720.0, 3.0, 0.0])
        self.assertLess(solved["rot_delta"], 1e-3)

    def test_the_closed_clip_and_the_scene_are_cross_checked(self):
        ch = self.channels({E.BIND_POSITION: ((0.0, 0.0, 0.0), (5.0, 0.0, 0.0))})
        closed = self.channels({E.BIND_POSITION: ((0.03, 0.0, 0.0), (0.03, 0.0, 0.0))})
        _, issues, _ = E.solve_node((0.0, 0.0, 0.0), (0, 0, 0, 1), (1, 1, 1), ch, closed, "wheel", 2.0)
        self.assertEqual([level for level, _ in issues], ["warn"])           # 3 cm: beyond 2 cm, inside the hard 10 cm
        closed = self.channels({E.BIND_POSITION: ((0.5, 0.0, 0.0), (0.5, 0.0, 0.0))})
        _, issues, _ = E.solve_node((0.0, 0.0, 0.0), (0, 0, 0, 1), (1, 1, 1), ch, closed, "wheel", 2.0)
        self.assertEqual([level for level, _ in issues], ["error"])          # a wrong hash or frame convention

    def test_a_closed_rotation_that_disagrees_with_the_scene_is_an_error(self):
        ch = self.channels({E.BIND_EULER: ((0.0, 0.0, 30.0), (0.0, 0.0, 90.0))})
        _, issues, _ = E.solve_node((0, 0, 0), (0, 0, 0, 1), (1, 1, 1), ch, {}, "door", 2.0)
        self.assertEqual([level for level, _ in issues], ["error"])

    def test_a_scale_change_is_reported_not_carried(self):
        ch = self.channels({E.BIND_SCALE: ((1.0, 1.0, 1.0), (1.0, 1.0, 0.0))})
        _, _, scale_changed = E.solve_node((0, 0, 0), (0, 0, 0, 1), (1, 1, 1), ch, {}, "n", 2.0)
        self.assertTrue(scale_changed)

    def test_motion_window_brackets_the_movement(self):
        curve = E.Curve(keys=[(NEG_INF, 0, 0, 0, 0.0), (0.0, 0, 0, 0, 0.0), (1.0, 0, 0, 0, 0.0), (2.0, 0, 0, 0, 10.0), (4.0, 0, 0, 0, 10.0)])
        # key 1.0 -> 2.0: value stays 0 (zero slopes) until key 2 steps to 10 (the value AT a key holds after it)
        start, end = E.motion_window([E.Channel(1, E.BIND_POSITION, [curve, E.Curve(constant=0.0), E.Curve(constant=0.0)])], 4.0)
        self.assertLessEqual(start, end + 1e-6)
        self.assertGreaterEqual(start, 0.9)
        self.assertLessEqual(end, 2.1)


def tidy3(values):
    return [round(v, 3) + 0.0 for v in values]


class SidecarTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.doc = json.loads(SIDECAR.read_text(encoding="utf-8"))
        cls.bowl = json.loads(BOWL.read_text(encoding="utf-8"))
        cls.poses = cls.doc["poses"]

    def pose(self, part, node=""):
        found = [p for p in self.poses if p["part"] == part and p["node"] == node]
        self.assertEqual(len(found), 1, (part, node))
        return found[0]

    def test_header(self):
        self.assertEqual(self.doc["schema_version"], 1)
        self.assertEqual(self.doc["key"], "arena_bowl")
        self.assertEqual(self.doc["source"]["scene"], "Arena_Bowl_TeamFight_DesignStuff")
        self.assertEqual(self.doc["source"]["level"], 48)
        self.assertEqual(self.doc["source"]["assets"], "sharedassets48.assets")
        self.assertIn(self.doc["source"]["scene"], self.bowl["all_scene_names"])
        self.assertTrue(self.doc["_comment"].startswith("Generated by tools/extract/extract_door_poses.py"))

    def test_every_pose_is_a_well_formed_move_below_an_opened_barrier(self):
        seen = set()
        for p in self.poses:
            with self.subTest(pose=p["part"] + "/" + p["node"]):
                self.assertEqual(set(p), set(E_KEYS))
                self.assertIn(p["barrier"], self.bowl["opened_barriers"])
                self.assertNotIn("frame", p["part"].lower())     # a frame part is never switched off, never animated here
                self.assertGreater(p["seconds"], 0)
                for state in ("closed", "open"):
                    for field in ("position", "euler"):
                        self.assertEqual(len(p[state][field]), 3)
                        self.assertTrue(all(math.isfinite(v) for v in p[state][field]))
                ident = (p["barrier"], p["part"], p["node"])
                self.assertNotIn(ident, seen)
                seen.add(ident)
                moved = (math.dist(p["closed"]["position"], p["open"]["position"]) > 0.01
                         or max(abs(a - b) for a, b in zip(p["closed"]["euler"], p["open"]["euler"])) > 1.0)
                self.assertTrue(moved, "a pose in the sidecar must differ from its closed pose by 1 cm or 1 degree")

    def test_the_decoded_table(self):
        counts = {}
        for p in self.poses:
            counts[p["part"]] = counts.get(p["part"], 0) + 1
        self.assertEqual(counts, {"BTR_82_closed": 9, "Bowl_Gates_Respawn_01": 1,
                                  "Bowl_Respawn_Ambulance_Animated Group": 7, "Gates_Army_01": 2})
        btr = self.pose("BTR_82_closed")
        self.assertEqual((btr["barrier"], btr["clip"], btr["seconds"]), ("DOORS_SPAWN_A", "BTR_82_Respawn_Animation", 7.683))
        self.assertEqual(btr["closed"], {"position": [51.182, -0.035, -21.498], "euler": [-90.0, 0.0, 83.511]})
        self.assertEqual(btr["open"], {"position": [45.61, -0.04, -22.14], "euler": [-90.3, 266.474, -182.962]})
        gate = self.pose("Bowl_Gates_Respawn_01", "Bowl_Gates_Respawn_01_Gate")
        self.assertEqual(gate["seconds"], 13.5)
        self.assertEqual(gate["closed"]["position"][1], 1.99)
        self.assertEqual(gate["open"]["position"][1], -1.833)
        door1 = self.pose("Gates_Army_01", "Gates_Army/Gates_Army_door_01")
        door2 = self.pose("Gates_Army_01", "Gates_Army/Gates_Army_door_02")
        self.assertEqual((door1["open"]["euler"][2], door2["open"]["euler"][2]), (142.922, -161.335))
        self.assertEqual((door1["seconds"], door1["barrier"]), (10.0, "DOORS_SPAWN_B"))
        ambulance = self.pose("Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated")
        self.assertEqual(ambulance["seconds"], 8.3)
        self.assertEqual(ambulance["open"]["position"], [4.773, 0.0, 0.0])
        self.assertEqual(ambulance["closed"]["position"], [0.0, 0.0, 0.0])

    def test_the_btr_body_keeps_its_heading(self):
        # the open euler is a different triple of the same heading: the BTR reverses 5.6 m, it does not turn
        btr = self.pose("BTR_82_closed")
        closed_q = E.euler_to_quat(btr["closed"]["euler"])
        open_q = E.euler_to_quat(btr["open"]["euler"])
        self.assertLess(E.quat_angle(closed_q, open_q), 1.0)
        self.assertAlmostEqual(math.dist(btr["closed"]["position"], btr["open"]["position"]), 5.61, delta=0.05)

    def test_the_sidecar_and_the_registry_agree(self):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        entries = VD.registry_entries(text)
        self.assertEqual(VD.door_pose_errors(self.bowl, SIDECAR, entries["Bowl"].get("DoorPoses")), [])
        for name, entry in entries.items():
            if name != "Bowl":
                self.assertIsNone(entry.get("DoorPoses"), name)

    def test_the_registry_carries_exactly_the_generated_csharp(self):
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")   # text mode: CRLF reads as LF
        self.assertIn(E.render_csharp(self.poses), text)

    def test_the_validator_run_passes(self):
        done = subprocess.run([sys.executable, str(REPO / "tools" / "validate" / "validate_map_descriptors.py")],
                              capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertTrue(done.stdout.startswith("OK"), done.stdout)


class MirrorValidatorTests(unittest.TestCase):
    """validate_map_descriptors.door_pose_errors catches every way the two copies can drift."""

    @classmethod
    def setUpClass(cls):
        cls.bowl = json.loads(BOWL.read_text(encoding="utf-8"))
        cls.doc = json.loads(SIDECAR.read_text(encoding="utf-8"))
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        cls.registry = VD.registry_entries(text)["Bowl"]["DoorPoses"]

    def errors(self, doc, registry=None):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "bowl.doors.json"
            path.write_text(json.dumps(doc), encoding="utf-8")
            return VD.door_pose_errors(self.bowl, path, self.registry if registry is None else registry)

    def test_clean(self):
        self.assertEqual(self.errors(self.doc), [])

    def test_a_changed_number_is_reported_with_the_pose(self):
        doc = copy.deepcopy(self.doc)
        doc["poses"][0]["open"]["position"][0] += 0.001
        errors = self.errors(doc)
        self.assertEqual(len(errors), 1)
        self.assertIn("BTR_82_closed", errors[0])
        self.assertIn("open", errors[0])

    def test_a_changed_clip_name_seconds_or_node_is_reported(self):
        for field, value in (("clip", "Other"), ("seconds", 9.0), ("node", "BTR_82_wheel_L_09"), ("part", "Elsewhere")):
            with self.subTest(field=field):
                doc = copy.deepcopy(self.doc)
                doc["poses"][1][field] = value
                self.assertTrue(self.errors(doc), field)

    def test_a_missing_or_extra_or_reordered_pose_is_reported(self):
        doc = copy.deepcopy(self.doc)
        doc["poses"].pop()
        self.assertTrue(any("poses, the registry" in e for e in self.errors(doc)))
        doc = copy.deepcopy(self.doc)
        doc["poses"][0], doc["poses"][1] = doc["poses"][1], doc["poses"][0]
        self.assertTrue(self.errors(doc))
        self.assertTrue(any("poses, the registry" in e for e in self.errors(self.doc, registry=self.registry[:-1])))

    def test_schema_violations_are_reported(self):
        def mutated(change):
            doc = copy.deepcopy(self.doc)
            change(doc)
            return self.errors(doc)

        self.assertTrue(mutated(lambda d: d.update(schema_version=2)))
        self.assertTrue(mutated(lambda d: d.update(key="arena_other")))
        self.assertTrue(mutated(lambda d: d["source"].update(scene="Arena_Nowhere")))
        self.assertTrue(mutated(lambda d: d["poses"][0].update(barrier="NOT_A_BARRIER")))
        self.assertTrue(mutated(lambda d: d["poses"][0].update(seconds=0)))
        self.assertTrue(mutated(lambda d: d["poses"][0]["closed"].update(position=[1.0, 2.0])))
        self.assertTrue(mutated(lambda d: d["poses"][0]["open"].update(euler=[0.0, float("nan"), 0.0])))
        self.assertTrue(mutated(lambda d: d["poses"][0].update(extra=1)))
        self.assertTrue(mutated(lambda d: d["poses"].append(copy.deepcopy(d["poses"][0]))))
        self.assertTrue(mutated(lambda d: d.update(poses=[])))

    def test_a_registry_without_the_sidecar_and_the_reverse_are_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = Path(tmp) / "bowl.doors.json"
            self.assertTrue(VD.door_pose_errors(self.bowl, missing, self.registry))
            self.assertEqual(VD.door_pose_errors(self.bowl, missing, []), [])
            self.assertEqual(VD.door_pose_errors(self.bowl, missing, None), [])
        self.assertTrue(self.errors(self.doc, registry=[]))

    def test_the_csharp_literal_parses_into_a_sidecar_pose(self):
        literal = ('new ArenaDoorPose("DOORS_SPAWN_A", "Part", "", "Clip", 7.683f, new[] { 1.0f, -2.5f, 3.0f }, '
                   'new[] { -90.0f, 0.0f, 83.511f }, new[] { 4.0f, 5.0f, 6.0f }, new[] { 0.0f, 1.0f, 2.0f })')
        self.assertEqual(VD.parse_door_pose_literal(literal), {
            "barrier": "DOORS_SPAWN_A", "part": "Part", "node": "", "clip": "Clip", "seconds": 7.683,
            "closed": {"position": [1.0, -2.5, 3.0], "euler": [-90.0, 0.0, 83.511]},
            "open": {"position": [4.0, 5.0, 6.0], "euler": [0.0, 1.0, 2.0]}})
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            VD.parse_door_pose_literal('new ArenaDoorPose("A", "B", "", "C", 1.0f)')


@unittest.skipUnless(HAVE_ARENA, "Arena is not installed")
class RealArenaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")
        package = PACKAGE if (PACKAGE / "scenes" / "Arena_Bowl_TeamFight_DesignStuff.json").is_file() else None
        cls.text, cls.source, cls.summary = E.extract("arena_bowl", "auto", package)
        cls.fresh = json.loads(cls.text)

    def test_the_tracked_sidecar_is_what_the_installed_arena_yields(self):
        tracked = json.loads(SIDECAR.read_text(encoding="utf-8"))
        # the Arena build is recorded, not compared: a hotfix that leaves the clips alone must not fail this test
        self.assertEqual(self.fresh["poses"], tracked["poses"])
        self.assertEqual({k: v for k, v in self.fresh["source"].items() if k != "arena_version"},
                         {k: v for k, v in tracked["source"].items() if k != "arena_version"})

    def test_all_four_animators_are_gates_and_every_hash_resolved(self):
        self.assertTrue(self.summary[0].endswith("4 animators (4 below opened barriers)"), self.summary[0])
        self.assertEqual(sum(1 for line in self.summary if line.startswith("gate ")), 4)
        self.assertTrue(all("transition into open clip yes" in line for line in self.summary if "transition into open clip" in line))

    def test_the_sidecar_text_is_what_the_cli_writes(self):
        self.assertTrue(self.text.endswith("}\n"))
        self.assertNotIn("\r", self.text)

    def test_airpits_bus_doors_carry_an_empty_controller_and_are_ignored(self):
        airpit = json.loads((REPO / "tools" / "maps" / "airpit.json").read_text(encoding="utf-8"))
        scene = E.ArenaScene(ARENA_DATA, E.scene_level_index("arena_airpit", "Arena_Airpit_TeamFight_DesignStuff"))
        gates, ignored = E.read_gates(scene, airpit["opened_barriers"])
        self.assertEqual(gates, [])
        self.assertEqual(len(ignored), 3)
        self.assertTrue(all(path.startswith("BUS_DOORS/Apron_bus_MAZ_doors") and "has no clips" in path for path in ignored), ignored)

    def test_the_decode_self_check_runs_on_every_gate(self):
        checks = [line for line in self.summary if "decode self-check:" in line]
        self.assertEqual(len(checks), 4)

    def test_the_extractor_refuses_an_unknown_map(self):
        with self.assertRaises(E.DoorPoseError):
            E.extract("arena_unknown", "auto", None)


if __name__ == "__main__":
    unittest.main()
