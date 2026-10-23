"""Arena's gate animations as end poses (docs/progress/door-poses.md): tools/extract/extract_door_poses.py and its sidecar.

The decoder tests build synthetic Mecanim clips and synthetic DotAnimator objects in memory (no game data). The sidecar
tests read only the tracked tools/maps/{bowl,equator,bay5}.doors.json and the C# registry through
validate_map_descriptors.py. One real-data class re-extracts the sidecars from the installed Arena and skips without Arena
or UnityPy.
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
E_KEYS = ("barrier", "part", "node", "clip", "source", "seconds", "ease", "curve", "at", "closed", "open")


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
        self.assertEqual(self.doc["schema_version"], 2)
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
                self.assertEqual((p["source"], p["ease"], p["curve"], p["at"]), ("clip", "smoothstep", None, None))
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
            if name not in ("Bowl", "Equator", "Bay5"):        # AirPit's doors come in a later change set
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

        self.assertTrue(mutated(lambda d: d.update(schema_version=1)))      # schema 1 (Bowl until 2026-10-23) is gone
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
            "barrier": "DOORS_SPAWN_A", "part": "Part", "node": "", "clip": "Clip", "source": "clip", "seconds": 7.683,
            "ease": "smoothstep", "curve": None, "at": None,
            "closed": {"position": [1.0, -2.5, 3.0], "euler": [-90.0, 0.0, 83.511]},
            "open": {"position": [4.0, 5.0, 6.0], "euler": [0.0, 1.0, 2.0]}})
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            VD.parse_door_pose_literal('new ArenaDoorPose("A", "B", "", "C", 1.0f)')

    def test_the_thirteen_argument_literal_carries_source_ease_curve_and_at(self):
        literal = ('new ArenaDoorPose("G", "P", "N", "DotAnimator", 7.0f, new[] { 1.0f, 2.0f, 3.0f }, new[] { 0.0f, 0.0f, 3.8f }, '
                   'new[] { 1.0f, 2.0f, 3.0f }, new[] { 0.0f, 0.0f, 162.432f }, "dotanimator", "curve", '
                   'new[] { 0.0f, 0.0f, 2.0f, 2.0f, 0.4f, 0.8f, 0.9f, 0.9f, 1.0f, 1.0f, 0.0f, 0.0f }, new[] { 50.5f, -3.9f, 7.5f })')
        parsed = VD.parse_door_pose_literal(literal)
        self.assertEqual((parsed["source"], parsed["ease"], parsed["at"]), ("dotanimator", "curve", [50.5, -3.9, 7.5]))
        self.assertEqual(parsed["curve"], [[0.0, 0.0, 2.0, 2.0], [0.4, 0.8, 0.9, 0.9], [1.0, 1.0, 0.0, 0.0]])
        none = VD.parse_door_pose_literal(literal.replace('"curve",', '"linear", null,').replace(
            'new[] { 0.0f, 0.0f, 2.0f, 2.0f, 0.4f, 0.8f, 0.9f, 0.9f, 1.0f, 1.0f, 0.0f, 0.0f }, new[] { 50.5f, -3.9f, 7.5f })',
            'null)'))
        self.assertEqual((none["ease"], none["curve"], none["at"]), ("linear", None, None))
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            VD.parse_door_pose_literal(literal.replace("1.0f, 1.0f, 0.0f, 0.0f }", "1.0f, 1.0f, 0.0f }"))   # 11 curve numbers
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            VD.parse_door_pose_literal(literal.replace('"dotanimator", ', ''))                            # 12 arguments


# ---------------------------------------------------------------------------------------------------------------
# DotAnimator (Equator and Bay5, 2026-10-23): the decoder on synthetic bytes, the pose rules on a hand-made hierarchy
# ---------------------------------------------------------------------------------------------------------------

DEPOT_CURVE = [(0.0, 0.0, 2.0, 2.0), (0.4, 0.8, 0.886, 0.886), (1.0, 1.0, 0.0, 0.0)]


def dot_element(target, kind=1, seconds=5.0, ease=16, vector=(2.0, 0.0, 0.0), keys=(), unknown=0.0, wrap=(2, 2, 4), file_id=0):
    return {"target": target, "kind": kind, "seconds": seconds, "ease": ease, "vector": vector, "keys": list(keys),
            "unknown": unknown, "wrap": wrap, "file_id": file_id}


def dot_blob(elements, name="", game_object=100, script=(1, 4154), flag=0, header=(0, 1, 0)):
    """The raw data of a DotAnimator MonoBehaviour: the 32-byte head, the name, 5 header words, then per element 53 words plus
    7 per curve key (the layout of the module doc)."""
    encoded = name.encode("utf-8")
    out = struct.pack("<iqiiqi", 0, game_object, 1, script[0], script[1], len(encoded)) + encoded
    out += b"\0" * (-len(out) % 4)
    out += struct.pack("<5i", *header, flag, len(elements))
    for e in elements:
        out += struct.pack("<iq", e["file_id"], e["target"])
        out += struct.pack("<ff7i", e["unknown"], e["seconds"], e["kind"], 0, e["ease"], 0, 1, -1, len(e["keys"]))
        for t, v, slope_in, slope_out in e["keys"]:
            out += struct.pack("<ffffiff", t, v, slope_in, slope_out, 0, 0.0, 0.0)
        out += struct.pack("<3i", *e["wrap"])
        out += struct.pack("<i3f", 0, *e["vector"]) + b"\0" * (34 * 4)
    return out


class DotAnimatorDecoderTests(unittest.TestCase):
    def test_elements_with_and_without_curve_keys_decode_at_their_own_stride(self):
        blob = dot_blob([dot_element(11, 1, 5.0, 16, (2.0, 0.5, -1.0), unknown=-2.0),
                         dot_element(12, 2, 7.0, 37, (0.0, 0.0, -141.759), DEPOT_CURVE, unknown=-1.0),
                         dot_element(13, 3, 0.0, 17, (1.0, 0.0, 0.0))], name="", game_object=4242, script=(1, 4154), flag=1)
        animator = E.decode_dot_animator(blob)
        self.assertEqual((animator.game_object, animator.script, animator.header), (4242, (1, 4154), (0, 1, 0, 1)))
        self.assertEqual(len(animator.elements), 3)
        move, rotate, switch = animator.elements
        self.assertEqual((move.target_file, move.target_path, move.kind, move.ease, len(move.keys)), (0, 11, 1, 16, 0))
        self.assertEqual((move.seconds, move.unknown), (5.0, -2.0))
        self.assertEqual(tuple(round(v, 4) for v in move.target), (2.0, 0.5, -1.0))
        self.assertEqual((rotate.target_path, rotate.kind, rotate.ease, rotate.seconds), (12, 2, 37, 7.0))
        self.assertEqual([tuple(round(v, 3) for v in k[:4]) for k in rotate.keys], [tuple(k) for k in DEPOT_CURVE])
        self.assertTrue(all(k[4] == 0 for k in rotate.keys))                       # unweighted tangents
        self.assertAlmostEqual(rotate.target[2], -141.759, places=3)
        self.assertEqual((switch.kind, switch.ease), (3, 17))
        self.assertEqual(len(move.payload), 38)

    def test_an_empty_and_a_long_name_are_skipped_with_4_byte_alignment(self):
        for name in ("", "G", "Gate", "Gate_Door", "Gate_Door_01"):
            with self.subTest(name=name):
                animator = E.decode_dot_animator(dot_blob([dot_element(11)], name=name))
                self.assertEqual(animator.name, name)
                self.assertEqual(animator.elements[0].target_path, 11)

    def test_the_object_size_is_20_plus_212_per_element_plus_28_per_key(self):
        head = len(dot_blob([]))
        self.assertEqual(len(dot_blob([dot_element(1)])) - head, 212)
        self.assertEqual(len(dot_blob([dot_element(1, 2, 7.0, 37, (0, 0, 1.0), DEPOT_CURVE)])) - head, 212 + 3 * 28)
        self.assertEqual(len(dot_blob([dot_element(1)] * 2)) - head, 424)

    def test_a_decode_that_does_not_end_at_the_object_length_is_refused(self):
        blob = dot_blob([dot_element(11)])
        for bad in (blob + b"\0\0\0\0", blob[:-4], blob[:-8]):
            with self.assertRaises(E.DoorPoseError):
                E.decode_dot_animator(bad)
        with self.assertRaises(E.DoorPoseError):
            E.decode_dot_animator(blob + b"\0")                  # not a whole number of words

    def test_a_wrong_header_wrap_kind_or_number_is_refused(self):
        cases = {
            "header": dot_blob([dot_element(11)], header=(1, 1, 0)),
            "wrap": dot_blob([dot_element(11, wrap=(2, 2, 3))]),
            "kind": dot_blob([dot_element(11, kind=7)]),
            "nan": dot_blob([dot_element(11, vector=(float("nan"), 0.0, 0.0))]),
            "inf duration": dot_blob([dot_element(11, seconds=float("inf"))]),
            "zero duration": dot_blob([dot_element(11, seconds=0.0)]),
            "negative duration": dot_blob([dot_element(11, kind=2, seconds=-1.0)]),
        }
        for label, blob in cases.items():
            with self.subTest(label), self.assertRaises(E.DoorPoseError):
                E.decode_dot_animator(blob)

    def test_a_switch_element_may_have_no_duration(self):
        self.assertEqual(E.decode_dot_animator(dot_blob([dot_element(11, kind=3, seconds=0.0)])).elements[0].kind, 3)

    def test_an_absurd_count_or_a_cut_off_element_is_refused(self):
        good = dot_blob([dot_element(11)])
        head = len(dot_blob([]))
        with self.assertRaises(E.DoorPoseError):
            E.decode_dot_animator(good[:head - 4] + struct.pack("<i", 9999) + good[head:])      # count 9999
        with self.assertRaises(E.DoorPoseError):
            E.decode_dot_animator(good[:head - 20 + 4 * 8])                                      # inside the header
        with self.assertRaises(E.DoorPoseError):
            E.decode_dot_animator(good[:head + 40])                                              # inside the element
        with self.assertRaises(E.DoorPoseError):
            E.decode_dot_animator(b"\0" * 20)                                                    # shorter than a MonoBehaviour head

    def test_the_head_parser_reads_the_game_object_and_script_pointers(self):
        gf, gp, enabled, sf, sp, name, off = E.parse_mono_head(dot_blob([], name="Abc", game_object=77, script=(3, 99)))
        self.assertEqual((gf, gp, enabled, sf, sp, name, off), (0, 77, 1, 3, 99, "Abc", 36))
        with self.assertRaises(E.DoorPoseError):
            E.parse_mono_head(struct.pack("<iqiiqi", 0, 1, 1, 0, 2, 1000) + b"abc")           # the name runs past the object


class EaseMappingTests(unittest.TestCase):
    def test_dotween_ease_numbers_map_to_the_sidecar_names(self):
        self.assertEqual([E.dot_ease_name(n) for n in (1, 16, 37)], ["linear", "inOutQuint", "curve"])

    def test_every_other_ease_number_stops_the_tool(self):
        for number in (0, 2, 3, 17, 18, 36, 38, -1):
            with self.subTest(number=number), self.assertRaises(E.DoorPoseError):
                E.dot_ease_name(number)

    def test_every_ease_is_exact_at_the_ends_and_monotone(self):
        curve = E.curve_from_keys([(t, v, i, o, 0, 0.0, 0.0) for t, v, i, o in DEPOT_CURVE])
        for ease in E.EASES:
            with self.subTest(ease=ease):
                f = lambda k: E.ease_fraction(ease, curve, k)
                self.assertAlmostEqual(f(0.0), 0.0, places=9)
                self.assertAlmostEqual(f(1.0), 1.0, places=9)
                self.assertEqual((f(-3.0), f(7.0)), (0.0, 1.0))                      # clamped outside the duration
                values = [f(i / 200) for i in range(201)]
                self.assertTrue(all(b >= a - 1e-9 for a, b in zip(values, values[1:])), ease)

    def test_in_out_quint_is_dotweens_formula(self):
        f = lambda k: E.ease_fraction(E.EASE_IN_OUT_QUINT, None, k)
        self.assertAlmostEqual(f(0.5), 0.5)
        self.assertAlmostEqual(f(0.25), 0.5 * 0.5 ** 5)                                # 0.015625
        for k in (0.1, 0.3, 0.45):
            self.assertAlmostEqual(f(k) + f(1.0 - k), 1.0)                            # point symmetric about the middle
        self.assertLess(f(0.3), E.ease_fraction(E.EASE_LINEAR, None, 0.3))            # slow start
        self.assertEqual(E.ease_fraction(E.EASE_LINEAR, None, 0.3), 0.3)
        self.assertAlmostEqual(E.ease_fraction(E.EASE_SMOOTHSTEP, None, 0.5), 0.5)

    def test_the_depot_curve_does_80_percent_of_the_swing_in_the_first_40_percent(self):
        curve = E.curve_from_keys([(t, v, i, o, 0, 0.0, 0.0) for t, v, i, o in DEPOT_CURVE])
        self.assertAlmostEqual(E.hermite_value(curve, 0.4), 0.8, places=6)
        self.assertAlmostEqual(E.hermite_value(curve, 0.4 * 7.0 / 7.0) * 7.0, 5.6, places=5)
        slope = (E.hermite_value(curve, 1e-6) - E.hermite_value(curve, 0.0)) / 1e-6
        self.assertAlmostEqual(slope, 2.0, places=3)                                  # the first key's out slope
        self.assertAlmostEqual(E.hermite_value(curve, 0.9999999), 1.0, places=5)
        self.assertEqual(E.hermite_value(curve, 5.0), 1.0)                            # holds the last value

    def test_a_curve_without_keys_is_refused(self):
        with self.assertRaises(E.DoorPoseError):
            E.ease_fraction("curve", None, 0.5)
        with self.assertRaises(E.DoorPoseError):
            E.ease_fraction("bounce", None, 0.5)

    def test_curve_validation(self):
        key = lambda t, v, i=0.0, o=0.0, w=0: (t, v, i, o, w, 0.0, 0.0)
        good = E.curve_from_keys([key(0.0, 0.0, 2.0, 2.0), key(0.4, 0.8123456, 0.9, 0.9), key(1.0, 1.0)])
        self.assertEqual(good, [[0.0, 0.0, 2.0, 2.0], [0.4, 0.81235, 0.9, 0.9], [1.0, 1.0, 0.0, 0.0]])
        for label, keys in (("one key", [key(0.0, 0.0)]),
                            ("unsorted", [key(0.0, 0.0), key(0.6, 0.5), key(0.4, 0.8), key(1.0, 1.0)]),
                            ("not from 0", [key(0.1, 0.0), key(1.0, 1.0)]),
                            ("not to 1", [key(0.0, 0.0), key(0.9, 1.0)]),
                            ("value does not start at 0", [key(0.0, 0.3), key(1.0, 1.0)]),
                            ("value does not end at 1", [key(0.0, 0.0), key(1.0, 0.6)]),
                            ("weighted tangents", [key(0.0, 0.0), key(0.5, 0.5, w=1), key(1.0, 1.0)])):
            with self.subTest(label), self.assertRaises(E.DoorPoseError):
                E.curve_from_keys(keys)


class FakeObject:
    def __init__(self, raw, script):
        self._raw = raw
        self.script = script

    def get_raw_data(self):
        return self._raw


class FakeScene(E.ArenaScene):
    """ArenaScene over a hand-made hierarchy (no Arena file): the real path, descendant, world-position and activity rules,
    with DotAnimator blobs attached to nodes."""

    def __init__(self):
        self.go, self.tr, self.go2tr = {}, {}, {}
        self._paths, self._by_path, self._script_names = {}, None, {}
        self.level_name = "level0"
        self.blobs = {}
        self._next = 1

    def add(self, name, parent=0, pos=(0.0, 0.0, 0.0), euler=(0.0, 0.0, 0.0), active=True):
        tp, go = self._next, self._next + 1000
        self._next += 1
        x, y, z, w = E.euler_to_quat(euler)
        self.tr[tp] = {"m_GameObject": {"m_PathID": go}, "m_Father": {"m_PathID": parent}, "m_Children": [],
                       "m_LocalPosition": {"x": pos[0], "y": pos[1], "z": pos[2]},
                       "m_LocalRotation": {"x": x, "y": y, "z": z, "w": w},
                       "m_LocalScale": {"x": 1.0, "y": 1.0, "z": 1.0}}
        self.go[go] = {"m_Name": name, "m_IsActive": active, "m_Component": []}
        self.go2tr[go] = tp
        if parent:
            self.tr[parent]["m_Children"].append({"m_PathID": tp})
        return tp

    def go_of(self, tp):
        return self.tr[tp]["m_GameObject"]["m_PathID"]

    def add_dot(self, tp, elements, script=E.DOT_SCRIPT, game_object=None):
        raw = dot_blob(elements, game_object=self.go_of(tp) if game_object is None else game_object)
        self.blobs.setdefault(tp, []).append(FakeObject(raw, script))

    def monobehaviours(self, tpid):
        return list(self.blobs.get(tpid, []))

    def mono_class(self, obj):
        return obj.script

    def export_package(self, directory, scene_name):
        """The scene as a package scenes/<name>.json (what check_package reads)."""
        objects = []
        for tp, t in self.tr.items():
            p, r = t["m_LocalPosition"], t["m_LocalRotation"]
            objects.append({"id": str(tp), "name": self.name(tp), "parent_id": str(t["m_Father"]["m_PathID"]) if t["m_Father"]["m_PathID"] else None,
                            "local_position": [p["x"], p["y"], p["z"]], "local_rotation": [r["x"], r["y"], r["z"], r["w"]],
                            "local_scale": [1.0, 1.0, 1.0]})
        (Path(directory) / "scenes").mkdir(parents=True, exist_ok=True)
        (Path(directory) / "scenes" / f"{scene_name}.json").write_text(json.dumps({"objects": objects}), encoding="utf-8")


def booth_scene():
    """Two assemblies with the SAME name under one root (Equator's unnumbered Door_Sliding_Respawn), a leaf pair each."""
    sc = FakeScene()
    root = sc.add("SPAWN_DOORS")
    sc.gates = []
    for x, left_x, seconds_r in ((-4.328, 0.717, 5.0), (4.535, 0.709, 7.0)):
        gate = sc.add("Door", root, pos=(x, 0.048, 40.02), euler=(270.0, 0.0, 0.0))
        left = sc.add("Leaf_L", gate, pos=(left_x, 0.0, 0.0))
        right = sc.add("Leaf_R", gate, pos=(-0.722, 0.0, 0.0))
        sc.add_dot(left, [dot_element(sc.go_of(left), 1, 5.0, 16, (2.0, 0.0, 0.0))])
        sc.add_dot(right, [dot_element(sc.go_of(right), 1, seconds_r, 16, (-2.0, 0.0, 0.0))])
        sc.gates.append(gate)
    return sc


BOOTH_SCAN = {"root": "SPAWN_DOORS", "depth": 2, "at": True}


class ScanDotAnimatorTests(unittest.TestCase):
    def test_a_booth_leaf_moves_to_the_absolute_local_target_not_by_it(self):
        scan = E.scan_dot_animators(booth_scene(), "S", BOOTH_SCAN)
        self.assertEqual((scan.objects, scan.elements, len(scan.poses)), (4, 4, 4))
        left = [p for p in scan.poses if p.part == "Leaf_L" and p.closed_position[0] == 0.717][0]
        self.assertEqual(left.open_position, [2.0, 0.0, 0.0])                      # 2.0, not 0.717 + 2.0
        self.assertEqual(left.closed_position, [0.717, 0.0, 0.0])
        self.assertEqual(left.open_euler, left.closed_euler)
        right = [p for p in scan.poses if p.part == "Leaf_R" and p.seconds == 7.0][0]
        self.assertEqual(right.open_position, [-2.0, 0.0, 0.0])

    def test_barrier_part_node_follow_the_depth_and_the_barrier_root_position_is_recorded(self):
        scan = E.scan_dot_animators(booth_scene(), "S", BOOTH_SCAN)
        self.assertEqual({(p.barrier, p.part, p.node) for p in scan.poses},
                         {("SPAWN_DOORS/Door", "Leaf_L", ""), ("SPAWN_DOORS/Door", "Leaf_R", "")})
        self.assertEqual(sorted(tidy3(p.at) for p in scan.poses if p.part == "Leaf_L"), [[-4.328, 0.048, 40.02], [4.535, 0.048, 40.02]])
        self.assertTrue(all(p.source == "dotanimator" and p.ease == "inOutQuint" and p.curve is None and p.clip == "DotAnimator"
                            for p in scan.poses))
        self.assertEqual({p.scene for p in scan.poses}, {"S"})

    def test_two_instances_of_one_path_are_told_apart_by_at_and_refused_without_it(self):
        poses = E.scan_dot_animators(booth_scene(), "S", BOOTH_SCAN).poses
        E.check_identities(poses)
        no_at = E.scan_dot_animators(booth_scene(), "S", dict(BOOTH_SCAN, at=False)).poses
        self.assertTrue(all(p.at is None for p in no_at))
        with self.assertRaisesRegex(E.DoorPoseError, "lacks 'at'"):
            E.check_identities(no_at)
        twin = copy.deepcopy(poses[0])
        with self.assertRaisesRegex(E.DoorPoseError, "occurs twice at"):
            E.check_identities(poses + [twin])

    def test_the_world_position_composes_the_parents_rotation_and_scale(self):
        sc = FakeScene()
        root = sc.add("R", 0, pos=(10.0, 0.0, 0.0), euler=(0.0, 90.0, 0.0))
        gate = sc.add("G", root, pos=(0.0, 0.0, 3.0))                    # 3 m along the parent's local z = world +x after the turn
        leaf = sc.add("L", gate, pos=(1.0, 0.0, 0.0))
        sc.add_dot(leaf, [dot_element(sc.go_of(leaf))])
        pose = E.scan_dot_animators(sc, "S", {"root": "R", "depth": 2, "at": True}).poses[0]
        self.assertEqual(tidy3(pose.at), [13.0, 0.0, 0.0])

    def test_a_dotanimator_on_the_barrier_root_moves_the_whole_root_with_an_empty_part(self):
        sc = FakeScene()
        wh = sc.add("WAREHOUSE (1)")
        roller = sc.add("Roller (4)", wh, pos=(-6.2269, 0.124, -21.5612), euler=(270.0, 0.0, 0.0))
        sc.add_dot(roller, [dot_element(sc.go_of(roller), 1, 4.0, 1, (-6.2269, 3.213, -21.5612))])
        pose = E.scan_dot_animators(sc, "S", {"root": "WAREHOUSE (1)", "depth": 2, "at": False}).poses[0]
        self.assertEqual((pose.barrier, pose.part, pose.node, pose.at), ("WAREHOUSE (1)/Roller (4)", "", "", None))
        self.assertEqual((pose.ease, pose.seconds, pose.curve), ("linear", 4.0, None))
        self.assertEqual(tidy3(pose.closed_position), [-6.227, 0.124, -21.561])
        self.assertAlmostEqual(pose.open_position[1], 3.213, places=5)
        self.assertEqual(tidy3(pose.closed_euler), [-90.0, 0.0, 0.0])                # 270 as (-90, 0, 0)
        self.assertEqual(pose.open_euler, pose.closed_euler)                          # a move leaves the rotation alone

    def test_a_rotation_keeps_the_position_and_takes_the_target_euler_and_the_curve(self):
        sc = FakeScene()
        old = sc.add("OLD")
        frame = sc.add("Frame", old, pos=(50.574, -3.895, 7.525))
        door = sc.add("Door_R", frame)
        leaf = sc.add("Leaf", door, pos=(-2.432, -0.115, 0.282), euler=(0.0, 0.0, 357.958))
        sc.add_dot(leaf, [dot_element(sc.go_of(leaf), 2, 7.0, 37, (0.0, 0.0, -98.974), DEPOT_CURVE)])
        pose = E.scan_dot_animators(sc, "S", {"root": "OLD", "depth": 2, "at": True}).poses[0]
        self.assertEqual((pose.barrier, pose.part, pose.node), ("OLD/Frame", "Door_R", "Leaf"))
        self.assertEqual(tidy3(pose.closed_euler), [0.0, 0.0, -2.042])
        self.assertEqual(tidy3(pose.open_euler), [0.0, 0.0, -98.974])
        self.assertEqual(pose.open_position, pose.closed_position)
        self.assertEqual((pose.ease, pose.seconds), ("curve", 7.0))
        self.assertEqual(pose.curve, [[0.0, 0.0, 2.0, 2.0], [0.4, 0.8, 0.886, 0.886], [1.0, 1.0, 0.0, 0.0]])

    def test_a_gimbal_lock_scene_rotation_is_kept_as_the_same_rotation(self):
        sc = FakeScene()
        root = sc.add("W")
        roller = sc.add("Roller (2)", root, pos=(9.8217, 0.13, -27.7051), euler=(270.0, 270.0, 0.0))
        sc.add_dot(roller, [dot_element(sc.go_of(roller), 1, 4.0, 1, (9.8217, 3.222, -27.7051))])
        pose = E.scan_dot_animators(sc, "S", {"root": "W", "depth": 2, "at": False}).poses[0]
        quat = sc.local(roller)[1]
        self.assertLess(E.quat_angle(E.euler_to_quat(pose.closed_euler), quat), 0.05)

    def test_a_node_that_moves_and_rotates_gets_one_pose(self):
        sc = FakeScene()
        root = sc.add("W")
        node = sc.add("Gate", root, pos=(0.0, 0.0, 0.0))
        sc.add_dot(node, [dot_element(sc.go_of(node), 1, 3.0, 1, (0.0, 2.0, 0.0)), dot_element(sc.go_of(node), 2, 3.0, 1, (0.0, 90.0, 0.0))])
        pose = E.scan_dot_animators(sc, "S", {"root": "W", "depth": 2, "at": False}).poses[0]
        self.assertEqual((pose.open_position, pose.open_euler), ([0.0, 2.0, 0.0], [0.0, 90.0, 0.0]))
        sc2 = FakeScene()
        r2 = sc2.add("W")
        n2 = sc2.add("Gate", r2)
        sc2.add_dot(n2, [dot_element(sc2.go_of(n2), 1, 3.0, 1, (0.0, 2.0, 0.0)), dot_element(sc2.go_of(n2), 2, 4.0, 1, (0.0, 90.0, 0.0))])
        with self.assertRaisesRegex(E.DoorPoseError, "differ in duration"):
            E.scan_dot_animators(sc2, "S", {"root": "W", "depth": 2, "at": False})
        sc3 = FakeScene()
        r3 = sc3.add("W")
        n3 = sc3.add("Gate", r3)
        sc3.add_dot(n3, [dot_element(sc3.go_of(n3), 1, 3.0, 1, (0.0, 2.0, 0.0)), dot_element(sc3.go_of(n3), 1, 3.0, 1, (0.0, 3.0, 0.0))])
        with self.assertRaisesRegex(E.DoorPoseError, "same kind"):
            E.scan_dot_animators(sc3, "S", {"root": "W", "depth": 2, "at": False})

    def test_switch_elements_inactive_objects_and_no_ops_are_skipped_with_a_note(self):
        sc = FakeScene()
        root = sc.add("W")
        lamp = sc.add("Lamp", root)
        sc.add_dot(lamp, [dot_element(sc.go_of(lamp), 3, 0.0, 17, (1.0, 0.0, 0.0))])
        twin = sc.add("Twin", root, active=False)
        twin_leaf = sc.add("Leaf", twin)
        sc.add_dot(twin_leaf, [dot_element(sc.go_of(twin_leaf), 1, 7.0, 1, (5.0, 0.0, 0.0))])
        still = sc.add("Still", root, pos=(1.0, 2.0, 3.0))
        sc.add_dot(still, [dot_element(sc.go_of(still), 1, 2.0, 1, (1.0, 2.004, 3.0))])       # moves 4 mm: not a move
        scan = E.scan_dot_animators(sc, "S", {"root": "W", "depth": 2, "at": False})
        self.assertEqual((scan.objects, scan.elements, len(scan.poses)), (3, 3, 0))
        self.assertEqual((scan.skipped_switch, scan.skipped_inactive), (1, 1))
        self.assertTrue(any("no net change" in line for line in scan.lines))

    def test_the_decode_must_belong_to_the_game_object_it_sits_on(self):
        sc = FakeScene()
        root = sc.add("W")
        gate = sc.add("Gate", root)
        sc.add_dot(gate, [dot_element(sc.go_of(gate))], game_object=999)
        with self.assertRaisesRegex(E.DoorPoseError, "names GameObject 999"):
            E.scan_dot_animators(sc, "S", {"root": "W", "depth": 2, "at": False})

    def test_targets_outside_the_scan_root_and_unknown_things_stop_the_tool(self):
        sc = FakeScene()
        root = sc.add("W")
        other = sc.add("Elsewhere")
        gate = sc.add("Gate", root)
        sc.add_dot(gate, [dot_element(sc.go_of(other))])
        with self.assertRaisesRegex(E.DoorPoseError, "outside the scan root"):
            E.scan_dot_animators(sc, "S", {"root": "W", "depth": 2, "at": False})
        sc2 = FakeScene()
        r2 = sc2.add("W")
        g2 = sc2.add("Gate", r2)
        sc2.add_dot(g2, [dot_element(123456)])
        with self.assertRaisesRegex(E.DoorPoseError, "not in this scene"):
            E.scan_dot_animators(sc2, "S", {"root": "W", "depth": 2, "at": False})
        sc3 = FakeScene()
        r3 = sc3.add("W")
        g3 = sc3.add("Gate", r3)
        sc3.add_dot(g3, [dot_element(sc3.go_of(g3), file_id=1)])
        with self.assertRaisesRegex(E.DoorPoseError, "another file"):
            E.scan_dot_animators(sc3, "S", {"root": "W", "depth": 2, "at": False})
        sc4 = FakeScene()
        r4 = sc4.add("W")
        g4 = sc4.add("Gate", r4)
        sc4.add_dot(g4, [dot_element(sc4.go_of(g4), ease=17)])
        with self.assertRaisesRegex(E.DoorPoseError, "ease 17"):
            E.scan_dot_animators(sc4, "S", {"root": "W", "depth": 2, "at": False})
        with self.assertRaisesRegex(E.DoorPoseError, "not found"):
            E.scan_dot_animators(sc4, "S", {"root": "Nowhere", "depth": 2, "at": False})
        with self.assertRaisesRegex(E.DoorPoseError, "barrier depth"):
            E.scan_dot_animators(sc4, "S", {"root": "W", "depth": 5, "at": False})

    def test_other_scripts_are_not_dotanimators(self):
        sc = FakeScene()
        root = sc.add("W")
        gate = sc.add("Gate", root)
        sc.add_dot(gate, [dot_element(sc.go_of(gate))], script="CommonAssets.Scripts.Game.Modes.Data.GateEntity")
        self.assertEqual(E.scan_dot_animators(sc, "S", {"root": "W", "depth": 2, "at": False}).objects, 0)

    def test_a_curve_beside_a_non_curve_ease_is_ignored_with_a_warning(self):
        sc = FakeScene()
        root = sc.add("W")
        gate = sc.add("Gate", root)
        sc.add_dot(gate, [dot_element(sc.go_of(gate), 1, 2.0, 1, (0.0, 2.0, 0.0), DEPOT_CURVE)])
        scan = E.scan_dot_animators(sc, "S", {"root": "W", "depth": 2, "at": False})
        self.assertIsNone(scan.poses[0].curve)
        self.assertEqual(len(scan.warnings), 1)

    def test_a_target_that_moves_all_three_axes_is_flagged_for_a_look(self):
        sc = FakeScene()
        root = sc.add("W")
        gate = sc.add("Gate", root, pos=(1.0, 1.0, 1.0))
        sc.add_dot(gate, [dot_element(sc.go_of(gate), 1, 2.0, 1, (2.0, 2.0, 2.0))])
        self.assertEqual(len(E.scan_dot_animators(sc, "S", {"root": "W", "depth": 2, "at": False}).warnings), 1)


class BorrowedGateTests(unittest.TestCase):
    @staticmethod
    def lender(ease="linear", curve=None, at=None):
        return E.NodePose("W/Lender", "", "", "DotAnimator", 4.0, [-6.2269, 0.124, -21.5615], [-90.0, 0.0, 0.0],
                          [-6.2269, 3.213, -21.5615], [-90.0, 0.0, 0.0], True, E.SOURCE_DOT, ease, curve, at, "S")

    @staticmethod
    def borrower_scene(own_dot=False):
        sc = FakeScene()
        w = sc.add("W")
        gate = sc.add("Borrower", w, pos=(-6.2269, 0.0918, -26.5106), euler=(270.0, 0.0, 0.0))
        if own_dot:
            sc.add_dot(gate, [dot_element(sc.go_of(gate))])
        return sc

    SPEC = {"barrier": "W/Borrower", "parts": [""], "lender": {"barrier": "W/Lender"}, "mode": "offset", "at": False}

    def test_offset_mode_adds_the_lenders_travel_to_the_borrowers_own_closed_pose(self):
        borrowed = E.build_borrowed(self.borrower_scene(), "S", self.SPEC, [self.lender()])
        pose = borrowed.poses[0]
        self.assertEqual((pose.source, pose.barrier, pose.part, pose.node), ("borrowed", "W/Borrower", "", ""))
        self.assertEqual((pose.seconds, pose.ease, pose.curve, pose.at), (4.0, "linear", None, None))
        self.assertEqual(tidy3(pose.closed_position), [-6.227, 0.092, -26.511])
        self.assertAlmostEqual(pose.open_position[1], 0.0918 + 3.089, places=5)
        self.assertEqual(pose.open_position[0], pose.closed_position[0])           # the lender moved only y
        self.assertEqual(pose.clip, "borrowed from W/Lender")

    def test_absolute_mode_takes_the_lenders_end_value_on_the_axes_it_moves(self):
        lender = E.NodePose("W/Lender", "Leaf", "", "DotAnimator", 5.0, [0.711, 0.0, 0.0], [0.0, 0.0, 0.0], [2.0, 0.0, 0.0],
                            [0.0, 0.0, 0.0], True, E.SOURCE_DOT, "inOutQuint", None, [1.0, 2.0, 3.0], "S")
        sc = FakeScene()
        w = sc.add("W")
        gate = sc.add("Borrower", w, pos=(10.0, 0.0, 0.0))
        sc.add("Leaf", gate, pos=(0.683, 0.5, 0.25))
        spec = {"barrier": "W/Borrower", "parts": ["Leaf"], "lender": {"barrier": "W/Lender"}, "mode": "absolute", "at": True}
        pose = E.build_borrowed(sc, "S", spec, [lender]).poses[0]
        self.assertEqual(pose.open_position, [2.0, 0.5, 0.25])                       # x = the lender's 2.0, y and z stay
        self.assertEqual((pose.ease, pose.seconds, pose.part), ("inOutQuint", 5.0, "Leaf"))
        self.assertEqual(tidy3(pose.at), [10.0, 0.0, 0.0])                           # the borrower's own root position

    def test_the_lenders_rotation_is_borrowed_too(self):
        lender = E.NodePose("W/Lender", "", "", "DotAnimator", 2.0, [0.0, 0.0, 0.0], [0.0, 0.0, 0.0], [0.0, 0.0, 0.0],
                            [0.0, 0.0, 90.0], True, E.SOURCE_DOT, "linear", None, None, "S")
        sc = FakeScene()
        w = sc.add("W")
        sc.add("Borrower", w, euler=(0.0, 0.0, 10.0))
        offset = E.build_borrowed(sc, "S", dict(self.SPEC), [lender]).poses[0]
        self.assertEqual(tidy3(offset.open_euler), [0.0, 0.0, 100.0])
        absolute = E.build_borrowed(sc, "S", dict(self.SPEC, mode="absolute"), [lender]).poses[0]
        self.assertEqual(tidy3(absolute.open_euler), [0.0, 0.0, 90.0])

    def test_a_gate_with_a_dotanimator_of_its_own_is_not_borrowed(self):
        with self.assertRaisesRegex(E.DoorPoseError, "own"):
            E.build_borrowed(self.borrower_scene(own_dot=True), "S", self.SPEC, [self.lender()])

    def test_missing_ambiguous_or_motionless_lenders_stop_the_tool(self):
        sc = self.borrower_scene()
        with self.assertRaisesRegex(E.DoorPoseError, "found 0 time"):
            E.build_borrowed(sc, "S", self.SPEC, [])
        with self.assertRaisesRegex(E.DoorPoseError, "found 2 time"):
            E.build_borrowed(sc, "S", self.SPEC, [self.lender(), self.lender()])
        still = self.lender()
        still.open_position = list(still.closed_position)
        with self.assertRaisesRegex(E.DoorPoseError, "moves nothing"):
            E.build_borrowed(sc, "S", self.SPEC, [still])
        with self.assertRaisesRegex(E.DoorPoseError, "found 0 time"):
            E.build_borrowed(sc, "S", dict(self.SPEC, barrier="W/Nobody"), [self.lender()])
        with self.assertRaisesRegex(E.DoorPoseError, "part 'Leaf'"):
            E.build_borrowed(sc, "S", dict(self.SPEC, parts=["Leaf"]), [self.lender()])

    def test_two_lenders_of_a_repeated_path_are_chosen_by_at(self):
        a, b = self.lender(at=[1.0, 0.0, 0.0]), self.lender(at=[9.0, 0.0, 0.0])
        b.seconds = 7.0
        spec = dict(self.SPEC, lender={"barrier": "W/Lender", "at": [9.0, 0.0, 0.0]})
        self.assertEqual(E.build_borrowed(self.borrower_scene(), "S", spec, [a, b]).poses[0].seconds, 7.0)


class PackageCheckTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.scene = booth_scene()
        self.scene.export_package(self.tmp.name, "S")
        self.poses = E.scan_dot_animators(self.scene, "S", BOOTH_SCAN).poses

    def test_the_poses_of_a_matching_package_pass_and_repeated_paths_are_told_apart_by_at(self):
        self.assertEqual(E.check_package(Path(self.tmp.name), "S", self.poses), [])

    def test_a_pose_without_at_on_a_repeated_path_finds_two_roots(self):
        pose = copy.deepcopy(self.poses[0])
        pose.at = None
        problems = E.check_package(Path(self.tmp.name), "S", [pose])
        self.assertEqual(len(problems), 1)
        self.assertIn("found 2 time", problems[0])

    def test_a_pose_at_the_wrong_place_or_with_a_changed_closed_pose_is_reported(self):
        elsewhere = copy.deepcopy(self.poses[0])
        elsewhere.at = [50.0, 0.0, 0.0]
        self.assertIn("found 0 time", E.check_package(Path(self.tmp.name), "S", [elsewhere])[0])
        moved = copy.deepcopy(self.poses[0])
        moved.closed_position = [moved.closed_position[0] + 0.01, 0.0, 0.0]
        self.assertTrue(any("local position" in p for p in E.check_package(Path(self.tmp.name), "S", [moved])))
        turned = copy.deepcopy(self.poses[0])
        turned.closed_euler = [0.0, 0.0, 5.0]
        self.assertTrue(any("local rotation" in p for p in E.check_package(Path(self.tmp.name), "S", [turned])))

    def test_a_missing_part_or_scene_is_reported(self):
        ghost = copy.deepcopy(self.poses[0])
        ghost.part = "Leaf_X"
        self.assertTrue(any("Leaf_X" in p for p in E.check_package(Path(self.tmp.name), "S", [ghost])))
        unscened = copy.deepcopy(self.poses)
        for p in unscened:
            p.scene = ""                                                      # no scene of its own: the caller's scene name is used
        with self.assertRaises(E.DoorPoseError):
            E.check_package(Path(self.tmp.name), "Missing", unscened)

    def test_a_pose_of_another_scene_is_looked_up_in_that_scene(self):
        other = FakeScene()
        r = other.add("Respawn_2")
        other.add("Door", r, pos=(-3.753, 0.048, -45.87))
        other.export_package(self.tmp.name, "Other")
        pose = E.NodePose("Respawn_2/Door", "", "", "borrowed from X", 5.0, [-3.753, 0.048, -45.87], [0.0, 0.0, 0.0],
                          [0.0, 0.0, 0.0], [0.0, 0.0, 0.0], True, E.SOURCE_BORROWED, "linear", None, [-3.753, 0.048, -45.87], "Other")
        self.assertEqual(E.check_package(Path(self.tmp.name), "S", [pose]), [])


class SchemaTwoRenderTests(unittest.TestCase):
    DOT = {"barrier": "G", "part": "P", "node": "N", "clip": "DotAnimator", "source": "dotanimator", "seconds": 7.0,
           "ease": "curve", "curve": [[0.0, 0.0, 2.0, 2.0], [0.4, 0.8, 0.886, 0.886], [1.0, 1.0, 0.0, 0.0]], "at": [50.574, -3.895, 7.525],
           "closed": {"position": [2.377, -0.112, 0.282], "euler": [0.0, 0.0, 3.8]},
           "open": {"position": [2.377, -0.112, 0.282], "euler": [0.0, 0.0, 162.432]}}

    def test_a_pose_with_extras_is_four_lines_with_source_ease_curve_and_at(self):
        self.assertEqual(E.render_csharp([self.DOT], indent="  ").split("\n"), [
            '  new ArenaDoorPose("G", "P", "N", "DotAnimator", 7.0f,',
            '      new[] { 2.377f, -0.112f, 0.282f }, new[] { 0.0f, 0.0f, 3.8f },',
            '      new[] { 2.377f, -0.112f, 0.282f }, new[] { 0.0f, 0.0f, 162.432f },',
            '      "dotanimator", "curve", new[] { 0.0f, 0.0f, 2.0f, 2.0f, 0.4f, 0.8f, 0.886f, 0.886f, 1.0f, 1.0f, 0.0f, 0.0f }, '
            'new[] { 50.574f, -3.895f, 7.525f }),'])

    def test_null_curve_and_at_are_spelled_null_and_a_default_pose_stays_three_lines(self):
        linear = dict(self.DOT, ease="linear", curve=None, at=None)
        self.assertTrue(E.render_csharp([linear], indent="").endswith('"dotanimator", "linear", null, null),'))
        clip = dict(self.DOT, source="clip", ease="smoothstep", curve=None, at=None)
        self.assertTrue(E.has_default_extras(clip))
        self.assertFalse(E.has_default_extras(self.DOT))
        self.assertFalse(E.has_default_extras(dict(clip, at=[0.0, 0.0, 0.0])))
        self.assertEqual(len(E.render_csharp([clip]).split("\n")), 3)

    def test_the_rendered_literal_parses_back_to_the_sidecar_pose(self):
        for pose in (self.DOT, dict(self.DOT, ease="linear", curve=None, at=None), dict(self.DOT, source="clip", ease="smoothstep", curve=None, at=None)):
            text = E.render_csharp([pose], indent="").replace("\n", " ")
            self.assertEqual(VD.parse_door_pose_literal(text.rstrip(",")), pose)

    def test_sidecar_pose_orders_the_keys_and_rounds(self):
        node = E.NodePose("B", "P", "", "DotAnimator", 5.0, [0.71149, 0.0, -0.0], [0.0, 0.0, 0.0], [2.0, 0.0, 0.0], [0.0, 0.0, 0.0],
                          True, E.SOURCE_DOT, "inOutQuint", None, [-4.3281, 0.0481, 40.0199])
        row = E.sidecar_pose(node)
        self.assertEqual(tuple(row), E_KEYS)
        self.assertEqual((row["at"], row["closed"]["position"], row["curve"]), ([-4.328, 0.048, 40.02], [0.711, 0.0, 0.0], None))
        self.assertFalse(math.copysign(1.0, row["closed"]["position"][2]) < 0)


def sidecar_doc(stem):
    return json.loads((REPO / "tools" / "maps" / f"{stem}.doors.json").read_text(encoding="utf-8"))


def final_state_map(stem):
    """The map descriptor with the opened_barriers the map-data change set of 2026-10-23 gives it: every pose barrier of Bay5 is an
    opened_barriers entry (Door_01 (2) and One_man_Door join today's three). Equator's booth roots are not barriers at all."""
    map_json = json.loads((REPO / "tools" / "maps" / f"{stem}.json").read_text(encoding="utf-8"))
    if stem == "bay5":
        map_json["opened_barriers"] = list(dict.fromkeys(list(map_json["opened_barriers"]) + [p["barrier"] for p in sidecar_doc(stem)["poses"]]))
    return map_json


class DotSidecarTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.equator = sidecar_doc("equator")
        cls.bay5 = sidecar_doc("bay5")
        text = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        cls.cs_text = text
        cls.entries = VD.registry_entries(text)

    def test_headers(self):
        for doc, key, scene, level in ((self.equator, "arena_equator", "Arena_Equator_TeamFight_DesignStuff", 57),
                                       (self.bay5, "arena_bay5", "Arena_Bay5_TeamFight_DesignStuff", 88)):
            with self.subTest(key):
                self.assertEqual((doc["schema_version"], doc["key"]), (2, key))
                self.assertEqual((doc["source"]["scene"], doc["source"]["level"], doc["source"]["assets"]),
                                 (scene, level, f"sharedassets{level}.assets"))
                self.assertTrue(doc["_comment"].startswith("Generated by tools/extract/extract_door_poses.py"))
                for pose in doc["poses"]:
                    self.assertEqual(tuple(pose), E_KEYS)
        self.assertEqual(self.equator["source"]["extra_scenes"], [{"scene": "Arena_Equator", "level": 51, "assets": "sharedassets51.assets"}])
        self.assertNotIn("extra_scenes", self.bay5["source"])

    def test_equators_booth_doors(self):
        poses = self.equator["poses"]
        self.assertEqual(len(poses), 10)
        self.assertEqual({p["source"] for p in poses}, {"dotanimator", "borrowed"})
        self.assertEqual(sum(1 for p in poses if p["source"] == "dotanimator"), 8)
        by_barrier = {}
        for p in poses:
            by_barrier.setdefault(p["barrier"], []).append(p)
        self.assertEqual({b: len(v) for b, v in by_barrier.items()}, {
            "SPAWN_DOORS/Door_Sliding_Respawn": 4, "SPAWN_DOORS/Door_Sliding_Respawn (1)": 2, "SPAWN_DOORS/Door_Sliding_Respawn (2)": 2,
            "SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)": 2})
        for p in poses:
            with self.subTest(barrier=p["barrier"], part=p["part"], at=p["at"]):
                self.assertEqual((p["node"], p["ease"], p["curve"]), ("", "inOutQuint", None))
                sign = 1.0 if p["part"] == "Door_Sliding_Respawn_L" else -1.0
                self.assertEqual(p["open"]["position"], [sign * 2.0, 0.0, 0.0])      # the absolute local target of Arena's script
                self.assertEqual(p["closed"]["position"][1:], [0.0, 0.0])
                self.assertAlmostEqual(abs(p["closed"]["position"][0]), 0.7, delta=0.03)
                self.assertGreater(abs(p["open"]["position"][0] - p["closed"]["position"][0]), 1.27)    # travel 1.28 to 1.32 m
                self.assertLess(abs(p["open"]["position"][0] - p["closed"]["position"][0]), 1.33)
                self.assertEqual(p["open"]["euler"], p["closed"]["euler"])
                self.assertEqual(p["at"][1], 0.048)
        long_leaf = [q for q in poses if q["seconds"] == 7.0]
        self.assertEqual(len(long_leaf), 1)                                           # exactly one 7 s leaf: the R leaf of GateEntity 24002
        self.assertEqual((long_leaf[0]["part"], long_leaf[0]["at"]), ("Door_Sliding_Respawn_R", [4.535, 0.048, 40.02]))
        self.assertEqual({q["seconds"] for q in poses if q is not long_leaf[0]}, {5.0})
        north = sorted(tidy3(q["at"]) for q in by_barrier["SPAWN_DOORS/Door_Sliding_Respawn"])
        self.assertEqual(north, [[-4.328, 0.048, 40.02]] * 2 + [[4.535, 0.048, 40.02]] * 2)
        borrowed = by_barrier["SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)"]
        self.assertEqual({q["source"] for q in borrowed}, {"borrowed"})
        self.assertEqual({q["clip"] for q in borrowed}, {"borrowed from SPAWN_DOORS/Door_Sliding_Respawn (1)"})
        self.assertEqual({tuple(q["at"]) for q in borrowed}, {(-3.753, 0.048, -45.87)})
        self.assertEqual([q["closed"]["position"][0] for q in borrowed], [0.683, -0.686])        # the door's own leaf pivots

    def test_bay5s_rollers_and_depot_gates(self):
        poses = self.bay5["poses"]
        self.assertEqual(len(poses), 8)
        rollers = {p["barrier"].split("/")[-1]: p for p in poses if p["part"] == ""}
        self.assertEqual(sorted(rollers), ["Bay5_Warehouse_Gate_Door_01 (2)", "Bay5_Warehouse_Gate_Door_01 (3)",
                                           "Bay5_Warehouse_Gate_Door_01 (4)", "One_man_Door"])
        for name, (closed_y, open_y) in {"Bay5_Warehouse_Gate_Door_01 (2)": (0.13, 3.222), "Bay5_Warehouse_Gate_Door_01 (4)": (0.124, 3.213),
                                         "One_man_Door": (0.15, 3.323), "Bay5_Warehouse_Gate_Door_01 (3)": (0.092, 3.181)}.items():
            with self.subTest(name):
                p = rollers[name]
                self.assertEqual((p["seconds"], p["ease"], p["curve"], p["at"], p["node"]), (4.0, "linear", None, None, ""))
                self.assertEqual((p["closed"]["position"][1], p["open"]["position"][1]), (closed_y, open_y))
                self.assertEqual(p["open"]["position"][0::2], p["closed"]["position"][0::2])         # only y changes
                self.assertEqual(p["open"]["euler"], p["closed"]["euler"])
        self.assertEqual({n: p["source"] for n, p in rollers.items()}, {
            "Bay5_Warehouse_Gate_Door_01 (2)": "dotanimator", "Bay5_Warehouse_Gate_Door_01 (3)": "borrowed",
            "Bay5_Warehouse_Gate_Door_01 (4)": "dotanimator", "One_man_Door": "dotanimator"})
        lift = lambda p: p["open"]["position"][1] - p["closed"]["position"][1]
        self.assertAlmostEqual(lift(rollers["Bay5_Warehouse_Gate_Door_01 (3)"]), lift(rollers["Bay5_Warehouse_Gate_Door_01 (4)"]), delta=0.002)
        self.assertEqual(rollers["Bay5_Warehouse_Gate_Door_01 (3)"]["clip"], "borrowed from WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)")
        depot = [p for p in poses if p["barrier"] == "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame"]
        self.assertEqual(len(depot), 4)
        table = {(tuple(p["at"]), p["part"]): (p["closed"]["euler"][2], p["open"]["euler"][2]) for p in depot}
        self.assertEqual(table, {
            ((50.574, -3.895, 7.525), "Gates_Depot_Door_L"): (3.8, 162.432), ((50.574, -3.895, 7.525), "Gates_Depot_Door_R"): (0.0, -141.759),
            ((69.045, -3.883, -6.317), "Gates_Depot_Door_L"): (0.0, 138.837), ((69.045, -3.883, -6.317), "Gates_Depot_Door_R"): (-2.042, -98.974)})
        for p in depot:
            self.assertEqual((p["node"] == p["part"] + "_Leaf", p["seconds"], p["ease"], p["source"]), (True, 7.0, "curve", "dotanimator"))
            self.assertEqual(p["open"]["position"], p["closed"]["position"])
            self.assertEqual(p["open"]["euler"][:2], [0.0, 0.0])
            curve = p["curve"]
            self.assertEqual([k[0] for k in curve], [0.0, 0.4, 1.0])
            self.assertEqual((curve[0][1], curve[-1][1]), (0.0, 1.0))
            self.assertAlmostEqual(curve[1][1], 0.8, delta=0.004)
            values = [E.hermite_value(curve, i / 100) for i in range(101)]
            self.assertTrue(all(b >= a - 1e-9 for a, b in zip(values, values[1:])))

    def test_no_pose_names_a_frame_part_or_a_node_without_a_part(self):
        for doc in (self.equator, self.bay5):
            for p in doc["poses"]:
                self.assertNotIn("frame", p["part"].lower())
                self.assertFalse(p["node"] and not p["part"])

    def test_the_poses_are_sorted_by_barrier_then_position_then_part(self):
        for doc in (self.equator, self.bay5):
            keys = [(p["barrier"], tuple(p["at"]) if p["at"] else (), p["part"], p["node"]) for p in doc["poses"]]
            self.assertEqual(keys, sorted(keys))

    def test_the_registry_carries_exactly_the_generated_csharp(self):
        for doc in (self.equator, self.bay5):
            self.assertIn(E.render_csharp(doc["poses"]), self.cs_text)

    def test_the_registry_mirrors_the_sidecars_given_the_final_opened_barriers(self):
        for stem, name in (("equator", "Equator"), ("bay5", "Bay5")):
            with self.subTest(stem):
                errors = VD.door_pose_errors(final_state_map(stem), REPO / "tools" / "maps" / f"{stem}.doors.json",
                                             self.entries[name].get("DoorPoses"))
                self.assertEqual(errors, [])

    def test_the_sidecars_regenerate_byte_for_byte_from_their_rows(self):
        for doc in (self.equator, self.bay5):
            again = E.render_sidecar(doc["key"], doc["source"], doc["poses"])
            self.assertEqual(json.loads(again), doc)
            self.assertTrue(again.endswith("}\n"))
            self.assertNotIn("\r", again)


class DoorValidatorRuleTests(unittest.TestCase):
    """validate_map_descriptors.door_pose_errors on the schema-2 rules, by mutating the Equator and Bay5 sidecars."""

    @classmethod
    def setUpClass(cls):
        cls.entries = VD.registry_entries((REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8"))

    def run_case(self, stem, change, registry=None):
        doc = sidecar_doc(stem)
        change(doc)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / f"{stem}.doors.json"
            path.write_text(json.dumps(doc), encoding="utf-8")
            reg = self.entries[{"equator": "Equator", "bay5": "Bay5"}[stem]].get("DoorPoses") if registry is None else registry
            return VD.door_pose_errors(final_state_map(stem), path, reg)

    def test_clean(self):
        self.assertEqual(self.run_case("equator", lambda d: None), [])
        self.assertEqual(self.run_case("bay5", lambda d: None), [])

    def test_source_ease_and_curve_rules(self):
        def poses(doc, **kw):
            for p in doc["poses"]:
                p.update(kw)

        self.assertTrue(self.run_case("equator", lambda d: poses(d, source="magic")))
        self.assertTrue(self.run_case("equator", lambda d: poses(d, ease="bounce")))
        self.assertTrue(self.run_case("equator", lambda d: poses(d, ease="smoothstep")))       # smoothstep is for clips only
        self.assertTrue(self.run_case("equator", lambda d: poses(d, curve=[[0.0, 0.0, 1.0, 1.0], [1.0, 1.0, 1.0, 1.0]])))   # curve without ease curve
        self.assertTrue(self.run_case("bay5", lambda d: d["poses"][0].update(curve=None)))     # ease curve without keys
        self.assertTrue(self.run_case("bay5", lambda d: d["poses"][0].update(curve=[[0.0, 0.0, 1.0, 1.0]])))
        self.assertTrue(self.run_case("bay5", lambda d: d["poses"][0].update(curve=[[0.0, 0.0, 1.0, 1.0], [0.5, 1.0, 1.0, 1.0]])))   # ends at t 0.5
        self.assertTrue(self.run_case("bay5", lambda d: d["poses"][0].update(curve=[[0.0, 0.0, 1.0, 1.0], [1.0, 0.5, 1.0, 1.0]])))   # ends at v 0.5
        self.assertTrue(self.run_case("bay5", lambda d: d["poses"][0].update(curve=[[0.0, 0.0, 1.0, 1.0], [0.0, 1.0, 1.0, 1.0]])))   # times do not increase
        self.assertTrue(self.run_case("equator", lambda d: d["poses"][0].update(clip="Something")))   # dotanimator / borrowed clip names
        self.assertTrue(self.run_case("bay5", lambda d: [p.update(clip="mine") for p in d["poses"] if p["source"] == "borrowed"]))

    def test_at_rules(self):
        self.assertTrue(self.run_case("equator", lambda d: d["poses"][0].update(at=[1.0, 2.0])))
        self.assertTrue(self.run_case("equator", lambda d: d["poses"][0].update(at=None)))                 # mixed at / no at under one barrier
        self.assertTrue(self.run_case("equator", lambda d: d["poses"].append(copy.deepcopy(d["poses"][2]))))   # duplicate at the same place
        # a repeated identity needs distinct `at`: two depot gates moved onto each other
        def merge_gates(doc):
            for p in doc["poses"]:
                if p["at"]:
                    p["at"] = [50.574, -3.895, 7.525]
        self.assertTrue(self.run_case("bay5", merge_gates))

        def drop_at(doc):
            for p in doc["poses"]:
                if p["barrier"].startswith("OLD_WAREHOUSE"):
                    p["at"] = None
        self.assertTrue(any("without 'at'" in e or "lacks 'at'" in e for e in self.run_case("bay5", drop_at)))

    def test_barriers_part_and_node_rules(self):
        self.assertTrue(self.run_case("equator", lambda d: d["poses"][1].update(barrier="SPAWN_DOORS/Other")))    # not a booth assembly root
        self.assertTrue(self.run_case("equator", lambda d: d["poses"][1].update(barrier="SPAWN_DOORS/Door_Sliding_Respawn (1)/x")))
        self.assertTrue(self.run_case("bay5", lambda d: d["poses"][0].update(barrier="SPAWN_DOORS/Door_Sliding_Respawn")))   # the exception is Equator's
        self.assertTrue(self.run_case("bay5", lambda d: d["poses"][4].update(part="", node="Leaf")))          # a node needs a part
        self.assertTrue(self.run_case("bay5", lambda d: d["poses"][0].update(part="Gates_Depot_Frame_X")))      # a frame part
        self.assertEqual(self.run_case("bay5", lambda d: [p.update(node="") for p in d["poses"] if p["part"] == ""]), [])    # part "" is fine

    def test_extra_scenes_and_the_source_block(self):
        self.assertTrue(self.run_case("equator", lambda d: d["source"].update(extra_scenes=[{"scene": "Arena_Nowhere", "level": 1, "assets": "x"}])))
        self.assertTrue(self.run_case("equator", lambda d: d["source"].update(extra_scenes=[])))
        self.assertTrue(self.run_case("equator", lambda d: d["source"].update(extra_scenes=[{"scene": "Arena_Equator", "level": "51", "assets": "x"}])))

    def test_every_field_the_registry_mirrors_is_compared(self):
        for field, value in (("ease", "linear"), ("source", "borrowed"), ("at", [0.0, 0.0, 0.0]), ("seconds", 9.0), ("clip", "borrowed from X")):
            with self.subTest(field):
                errors = self.run_case("equator", lambda d, f=field, v=value: d["poses"][3].update({f: v}))
                self.assertTrue(errors)
        errors = self.run_case("bay5", lambda d: d["poses"][0]["curve"][1].__setitem__(1, 0.7))
        self.assertTrue(any("curve" in e for e in errors), errors)
        errors = self.run_case("bay5", lambda d: d["poses"][0]["open"]["euler"].__setitem__(2, 1.0))
        self.assertTrue(any("open" in e for e in errors), errors)
        self.assertTrue(self.run_case("equator", lambda d: None, registry=self.entries["Equator"]["DoorPoses"][:-1]))


class RealArenaDotAnimatorTests(unittest.TestCase):
    """The DotAnimator decode on the installed Arena (skipped without it): the layout holds on every DotAnimator of the five
    surveyed levels, and the tracked sidecars are what the installed Arena yields."""

    @classmethod
    def setUpClass(cls):
        if not (ARENA_DATA / "level57").is_file() or not (ARENA_DATA / "level88").is_file():
            raise unittest.SkipTest("Arena is not installed")
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")

    def test_the_layout_holds_on_every_dotanimator_of_the_surveyed_levels(self):
        expected = {57: (12, 20), 61: (12, 20), 88: (11, 11), 28: (21, 27), 70: (4, 4)}
        total = 0
        for level, (objects, elements) in expected.items():
            with self.subTest(level=level):
                scene = E.ArenaScene(ARENA_DATA, level)
                animators = [E.decode_dot_animator(obj.get_raw_data())                              # raises unless offset == length
                             for tp in scene.tr for obj in scene.monobehaviours(tp) if scene.mono_class(obj) == E.DOT_SCRIPT]
                self.assertEqual((len(animators), sum(len(a.elements) for a in animators)), (objects, elements))
                total += elements
                self.assertTrue(all(e.kind in (1, 2, 3) for a in animators for e in a.elements))
        self.assertEqual(total, 82)

    def test_equators_and_bay5s_sidecars_are_what_the_installed_arena_yields(self):
        for map_key, stem, package in (("arena_equator", "equator", "equator-native-v5"), ("arena_bay5", "bay5", "bay5-native-v5")):
            with self.subTest(map_key):
                pkg = REPO / "work" / "extract" / package
                text, source, summary = E.extract(map_key, "auto", pkg if (pkg / "scenes").is_dir() else None)
                tracked = sidecar_doc(stem)
                fresh = json.loads(text)
                self.assertEqual(fresh["poses"], tracked["poses"])
                strip = lambda s: {k: v for k, v in s.items() if k != "arena_version"}
                self.assertEqual(strip(fresh["source"]), strip(tracked["source"]))
                self.assertEqual(text, E.extract(map_key, "auto", None)[0])                          # byte-stable, with or without the package
                self.assertTrue(any(line.startswith("decode self-check:") for line in summary))
                self.assertNotIn("\r", text)

    def test_every_dotanimator_of_level_88_is_under_a_scan_root(self):
        text, _, summary = E.extract("arena_bay5", "auto", None)
        check = [line for line in summary if line.startswith("decode self-check:")][0]
        self.assertIn("11 DotAnimator object(s), 11 element(s)", check)

    def test_equators_booth_scan_finds_the_eight_leaf_animators_and_the_lamps_stay_out(self):
        _, _, summary = E.extract("arena_equator", "auto", None)
        self.assertIn("8 DotAnimator object(s), 8 element(s)", [line for line in summary if line.startswith("decode self-check:")][0])
        self.assertIn("poses 10: 8 dotanimator, 2 borrowed", summary[-1])

    def test_a_borrowed_gate_with_its_own_script_would_be_refused(self):
        scene = E.ArenaScene(ARENA_DATA, 57)               # level 57 holds the booth leaves WITH a DotAnimator
        spec = {"barrier": "SPAWN_DOORS/Door_Sliding_Respawn (1)", "parts": ["Door_Sliding_Respawn_L"],
                "lender": {"barrier": "x"}, "mode": "absolute", "at": False}
        with self.assertRaisesRegex(E.DoorPoseError, "DotAnimator of its own"):
            E.build_borrowed(scene, "Arena_Equator_TeamFight_DesignStuff", spec, [])


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
