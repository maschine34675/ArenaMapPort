"""Arena's score monitors (docs/progress/score-monitors.md): tools/extract/extract_score_monitors.py, its tracked sidecars and the registry mirror.

The extractor's rules run on hand-made scenes (no Arena file). The sidecar tests read only the tracked tools/maps/<stem>.monitors.json and the C#
registry through validate_map_descriptors.py. One real-data class re-extracts the sidecars from the installed Arena and skips without Arena or UnityPy.
"""
import contextlib
import copy
import io
import json
import math
import re
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "extract"))
sys.path.insert(0, str(REPO / "tools" / "validate"))
import extract_door_poses as DOORS  # noqa: E402
import extract_score_monitors as E  # noqa: E402
import validate_map_descriptors as VD  # noqa: E402

STEMS = ("equator", "bay5", "airpit", "bowl")
KEYS = {"equator": "arena_equator", "bay5": "arena_bay5", "airpit": "arena_airpit", "bowl": "arena_bowl"}
DESCRIPTOR = REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs"
ARENA_DATA = DOORS.DEFAULT_DATA
HAVE_ARENA = (ARENA_DATA / "level53").is_file() and (ARENA_DATA / "sharedassets53.assets").is_file()
ROW_KEYS = ("scene", "path", "at", "arena_type", "template", "confirmed", "face", "active_in_scene", "prop_scale", "world_size_m", "viewer_yaw")


def sidecar_path(stem):
    return REPO / "tools" / "maps" / f"{stem}.monitors.json"


def sidecar_doc(stem):
    return json.loads(sidecar_path(stem).read_text(encoding="utf-8"))


def map_json(stem):
    return json.loads((REPO / "tools" / "maps" / f"{stem}.json").read_text(encoding="utf-8"))


def registry():
    return VD.registry_entries(DESCRIPTOR.read_text(encoding="utf-8"))


# ---------------------------------------------------------------------------------------------------------------
# A hand-made scene: the real path, world-position and activity rules of ArenaScene with MonoBehaviours attached
# ---------------------------------------------------------------------------------------------------------------

class FakeType:
    def __init__(self, name):
        self.name = name


class FakeObject:
    def __init__(self, raw, script, kind="MonoBehaviour"):
        self._raw, self.script, self.type = raw, script, FakeType(kind)

    def get_raw_data(self):
        return self._raw


def holder_blob(word, game_object, holder_id=-105, size=None, name=""):
    """The raw data of an ArenaMonitorHolder: the 32-byte head, an empty name, then holder_id and the type word."""
    encoded = name.encode("utf-8")
    out = struct.pack("<iqiiqi", 0, game_object, 1, 1, 4154, len(encoded)) + encoded
    out += b"\0" * (-len(out) % 4)
    out += struct.pack("<ii", holder_id, word)
    return out if size is None else out[:size].ljust(size, b"\0")


class FakeScene(DOORS.ArenaScene):
    def __init__(self):
        self.go, self.tr, self.go2tr = {}, {}, {}
        self._paths, self._by_path, self._script_names = {}, None, {}
        self.level_name = "level0"
        self.files = {"level0": {}}
        self._next = 1
        self._object = 9000

    def add(self, name, parent=0, pos=(0.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0, 1.0), scale=(1.0, 1.0, 1.0), active=True, rect=None):
        tp, go = self._next, self._next + 1000
        self._next += 1
        self.tr[tp] = {"m_GameObject": {"m_PathID": go}, "m_Father": {"m_PathID": parent}, "m_Children": [],
                       "m_LocalPosition": {"x": pos[0], "y": pos[1], "z": pos[2]},
                       "m_LocalRotation": {"x": rot[0], "y": rot[1], "z": rot[2], "w": rot[3]},
                       "m_LocalScale": {"x": scale[0], "y": scale[1], "z": scale[2]}}
        if rect is not None:
            self.tr[tp]["m_SizeDelta"] = {"x": rect[0], "y": rect[1]}
            self.tr[tp]["m_Pivot"] = {"x": rect[2], "y": rect[3]}
        self.go[go] = {"m_Name": name, "m_IsActive": active, "m_Component": []}
        self.go2tr[go] = tp
        if parent:
            self.tr[parent]["m_Children"].append({"m_PathID": tp})
        return tp

    def go_of(self, tp):
        return self.tr[tp]["m_GameObject"]["m_PathID"]

    def attach(self, tp, raw, script=E.HOLDER_CLASS, kind="MonoBehaviour"):
        self._object += 1
        self.files["level0"][self._object] = FakeObject(raw, script, kind)

    def mono_class(self, obj):
        return obj.script


def mount_pose(**overrides):
    pose = {"pos": tuple(E.MOUNT["position"]), "rot": tuple(E.MOUNT["rotation"]), "scale": (E.MOUNT["scale"],) * 3,
            "rect": (float(E.MOUNT["size"][0]), float(E.MOUNT["size"][1]), 0.5, 0.5)}
    pose.update(overrides)
    return pose


def tv_scene(prop_scale=3.0, prop_pos=(10.0, 5.0, -4.0), prop_euler=(0.0, 0.0, 0.0), prop_active=True, word=1, holder=None, root_scale=(1.0, 1.0, 1.0), **mount):
    """A root TV with one prop Arena_Home_theater holding one ArenaMonitorHolder. `holder` may set name, active, size (the blob's byte length) or no_rect.
    Returns (scene, prop transform id, holder transform id)."""
    options = holder or {}
    scene = FakeScene()
    root = scene.add("TV", scale=root_scale)
    prop = scene.add("Arena_Home_theater", root, pos=prop_pos, rot=DOORS.euler_to_quat(prop_euler), scale=(prop_scale,) * 3, active=prop_active)
    pose = mount_pose(**mount)
    holder_tp = scene.add(options.get("name", E.HOLDER_NAME), prop, pos=pose["pos"], rot=pose["rot"], scale=pose["scale"], active=options.get("active", True),
                          rect=None if options.get("no_rect") else pose["rect"])
    scene.attach(holder_tp, holder_blob(word, scene.go_of(holder_tp), size=options.get("size")))
    return scene, prop, holder_tp


# ---------------------------------------------------------------------------------------------------------------
# Pure rules
# ---------------------------------------------------------------------------------------------------------------

class PoseTests(unittest.TestCase):
    def test_the_world_pose_composes_position_rotation_and_scale_down_the_chain(self):
        quarter = DOORS.euler_to_quat((0.0, 90.0, 0.0))
        pos, rot, scale = E.world_pose([((1.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (2.0, 2.0, 2.0)), ((0.0, 0.0, 1.0), quarter, (3.0, 3.0, 3.0)),
                                        ((1.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (0.5, 0.5, 0.5))])
        # the child's (0, 0, 1) is scaled by 2 and the grandchild's (1, 0, 0) by 2 * 3 and turned 90 degrees about Y: (1, 0, 0) -> (0, 0, -1) * 6
        self.assertAlmostEqual(pos[0], 1.0, places=6)
        self.assertAlmostEqual(pos[1], 0.0, places=6)
        self.assertAlmostEqual(pos[2], 2.0 - 6.0, places=5)
        self.assertEqual(tuple(round(v, 6) for v in scale), (3.0, 3.0, 3.0))
        self.assertLess(DOORS.quat_angle(rot, quarter), 1e-3)

    def test_the_shared_mount_faces_the_reader_along_the_props_plus_x(self):
        # canvas forward (the holder's +Z) = the prop's -X, so a prop without rotation is read from +X: yaw 90
        for euler, yaw in (((0.0, 0.0, 0.0), 90.0), ((0.0, 90.0, 0.0), 180.0), ((0.0, 180.0, 0.0), 270.0), ((0.0, 270.0, 0.0), 0.0)):
            with self.subTest(euler=euler):
                rot = DOORS.quat_mul(DOORS.euler_to_quat(euler), tuple(E.MOUNT["rotation"]))
                self.assertEqual(E.reader_yaw(rot), yaw)

    def test_the_prop_of_equator_atrium_is_read_from_the_west_and_the_east_one_from_the_east(self):
        # Equator (0) stands at x = -17.8 and is read from +X (yaw 90), Equator (3) at x = 17.3 from -X (yaw 270): the viewer yaw is the
        # direction from the screen towards the reader, not the canvas forward
        rows = {r["path"]: r for r in sidecar_doc("equator")["monitors"] if r["scene"] == "Arena_Equator_DesignStuff"}
        self.assertEqual((rows["TV/Arena_Home_theater"]["viewer_yaw"], rows["TV/Arena_Home_theater (3)"]["viewer_yaw"]), (90.0, 270.0))

    def test_a_scale_must_be_uniform_and_positive(self):
        self.assertAlmostEqual(E.uniform_scale((3.0, 3.0, 3.0), "x"), 3.0)
        self.assertAlmostEqual(E.uniform_scale((19.68, 19.6801, 19.6799), "x"), 19.68, places=3)
        for bad in ((3.0, 3.0, 2.9), (3.0, 1.0, 3.0), (0.0, 0.0, 0.0), (-3.0, -3.0, -3.0)):
            with self.subTest(bad=bad), self.assertRaises(E.MonitorError):
                E.uniform_scale(bad, "x")

    def test_the_prop_world_scale_is_the_product_of_the_chain(self):
        scene, prop, holder = tv_scene(prop_scale=2.0, root_scale=(1.5, 1.5, 1.5))
        rows = E.read_holders(scene, "S", 1)
        self.assertEqual(rows[0]["prop_scale"], 3.0)
        self.assertEqual(rows[0]["world_size_m"], [round(1930 * 0.00054 * 3.0, 3), round(1163 * 0.00054 * 3.0, 3)])

    def test_the_holders_world_position_is_the_mount_point_of_the_prop(self):
        scene, prop, holder = tv_scene(prop_scale=3.0, prop_pos=(10.0, 5.0, -4.0))
        row = E.read_holders(scene, "S", 1)[0]
        # identity prop rotation: the mount's (0, 0, 0.369285) scaled by 3 along +Z
        self.assertEqual(row["at"], [10.0, 5.0, round(-4.0 + 3.0 * 0.369285, 4)])
        self.assertEqual(tuple(row["at"]), tuple(round(v, 4) for v in E.prop_world_at(((10.0, 5.0, -4.0), (0.0, 0.0, 0.0, 1.0), (3.0, 3.0, 3.0)))))


class TypeWordTests(unittest.TestCase):
    def test_the_known_words_and_their_faces(self):
        self.assertEqual(E.type_word(0, "x"), ("MonitorTimer", True, "timer"))
        self.assertEqual(E.type_word(1, "x"), ("MonitorTeamsCommonRoundScore", True, "score"))
        self.assertEqual(E.type_word(2, "x"), ("MonitorAlivePlayers", True, "alive"))
        self.assertEqual(E.type_word(6, "x"), ("MonitorTeamsFinalRoundScore", False, "score"))
        self.assertEqual(E.type_word(7, "x"), ("MonitorPlayerboard", False, "none"))
        self.assertEqual(E.type_word(10, "x"), (None, False, "none"))

    def test_only_the_three_confirmed_words_are_confirmed_and_a_word_above_seven_names_no_template(self):
        self.assertEqual({w for w, (_t, confirmed, _f) in E.TYPE_WORDS.items() if confirmed}, {0, 1, 2})
        self.assertTrue(all(t is None for w, (t, _c, _f) in E.TYPE_WORDS.items() if w > 7))

    def test_an_unknown_word_stops_the_tool_and_says_where_to_classify_it(self):
        for word in (3, 4, 5, 8, 9, 11, 12, 13, 14, -1, 99):
            with self.subTest(word=word), self.assertRaisesRegex(E.MonitorError, "TYPE_WORDS"):
                E.type_word(word, "Arena_X TV/Prop")

    def test_the_validators_table_is_the_extractors(self):
        self.assertEqual(VD.SCORE_MONITOR_TYPE_WORDS, E.TYPE_WORDS)
        self.assertEqual(VD.SCORE_MONITOR_MOUNT, E.MOUNT)
        self.assertEqual(VD.SCORE_MONITOR_AT_TOLERANCE, E.PACKAGE_AT_TOL)

    def test_the_mount_is_the_one_the_csharp_descriptor_spells(self):
        text = DESCRIPTOR.read_text(encoding="utf-8")
        block = text[text.index("public static class ArenaScoreMonitorMount"):]
        block = block[:block.index("\n    }\n")]

        def floats(name):
            found = re.search(name + r"\s*=\s*\{([^}]*)\}", block)
            return [float(v.strip().rstrip("f")) for v in found.group(1).split(",")]

        def const(name):
            return float(re.search(r"const float " + name + r"\s*=\s*([0-9.]+)f;", block).group(1))

        self.assertEqual(floats("Position"), E.MOUNT["position"])
        self.assertEqual(floats("Rotation"), E.MOUNT["rotation"])
        self.assertEqual(const("Scale"), E.MOUNT["scale"])
        self.assertEqual([const("Width"), const("Height")], [float(v) for v in E.MOUNT["size"]])
        self.assertEqual([const("PivotX"), const("PivotY")], E.MOUNT["pivot"])


class RowHelperTests(unittest.TestCase):
    def test_natural_sort_puts_ten_after_nine(self):
        rows = [{"scene": "S", "path": f"TV/Arena_Home_theater ({n})", "at": [0.0, 0.0, 0.0]} for n in (11, 2, 9, 1)]
        self.assertEqual([r["path"] for r in E.sort_rows(rows, ["S"])], [f"TV/Arena_Home_theater ({n})" for n in (1, 2, 9, 11)])

    def test_rows_follow_the_scene_order_then_the_path_then_the_position(self):
        rows = [{"scene": "B", "path": "P", "at": [0.0, 0.0, 0.0]}, {"scene": "A", "path": "Q", "at": [2.0, 0.0, 0.0]},
                {"scene": "A", "path": "Q", "at": [1.0, 0.0, 0.0]}, {"scene": "A", "path": "P", "at": [9.0, 0.0, 0.0]}]
        order = [(r["scene"], r["path"], r["at"][0]) for r in E.sort_rows(rows, ["A", "B"])]
        self.assertEqual(order, [("A", "P", 9.0), ("A", "Q", 1.0), ("A", "Q", 2.0), ("B", "P", 0.0)])

    def test_two_holders_at_one_place_are_refused_and_a_repeated_path_is_told_apart_by_position(self):
        row = {"scene": "S", "path": "TV/P", "at": [1.0, 2.0, 3.0]}
        E.check_unique_rows("arena_x", [row, dict(row, at=[1.0, 2.0, 5.0]), dict(row, scene="T")])
        with self.assertRaisesRegex(E.MonitorError, "two holders at the same place"):
            E.check_unique_rows("arena_x", [row, dict(row, at=[1.0, 2.01, 3.0])])

    def test_the_sidecar_text_has_one_row_per_line(self):
        doc = {"schema_version": 1, "key": "arena_x", "source": {"a": 1}, "mount": E.MOUNT, "monitors": [{"scene": "S", "path": "P"}, {"scene": "T", "path": "Q"}]}
        text = E.render_sidecar(doc)
        self.assertTrue(text.startswith("{\n") and text.endswith("}\n"))
        self.assertNotIn("\r", text)
        lines = text.splitlines()
        self.assertEqual([l for l in lines if l.startswith('    {"scene"')], ['    {"scene": "S", "path": "P"},', '    {"scene": "T", "path": "Q"}'])
        self.assertEqual(json.loads(text)["monitors"], doc["monitors"])

    def test_csharp_literals_spell_floats_the_validator_parses(self):
        rows = [{"scene": "Arena_X", "path": "TV/P (1)", "at": [1.0, -2.5, 40.6855], "arena_type": 6, "active_in_scene": False}]
        text = E.render_csharp(rows, indent="")
        self.assertEqual(text, 'new ArenaScoreMonitor("Arena_X", "TV/P (1)", new[] { 1.0f, -2.5f, 40.6855f }, 6, false),')
        self.assertEqual(VD.parse_score_monitor_literal(text.rstrip(",")),
                         {"scene": "Arena_X", "path": "TV/P (1)", "at": [1.0, -2.5, 40.6855], "arena_type": 6, "active_in_scene": False})
        with self.assertRaises(E.MonitorError):
            E.csharp_float(1e-7)


# ---------------------------------------------------------------------------------------------------------------
# Reading holders: every check fails closed
# ---------------------------------------------------------------------------------------------------------------

class ReadHoldersTests(unittest.TestCase):
    def test_a_holder_becomes_one_row(self):
        scene, prop, holder = tv_scene(word=2, prop_euler=(0.0, 90.0, 0.0))
        rows = E.read_holders(scene, "Arena_S", 53)
        self.assertEqual(len(rows), 1)
        row = rows[0]
        self.assertEqual(tuple(row), ROW_KEYS)
        self.assertEqual((row["scene"], row["path"], row["arena_type"], row["template"], row["confirmed"], row["face"], row["active_in_scene"]),
                         ("Arena_S", "TV/Arena_Home_theater", 2, "MonitorAlivePlayers", True, "alive", True))
        self.assertEqual(row["viewer_yaw"], 180.0)

    def test_an_inactive_prop_or_ancestor_makes_an_inactive_row(self):
        for kwargs in ({"prop_active": False},):
            scene, _prop, _holder = tv_scene(**kwargs)
            self.assertFalse(E.read_holders(scene, "S", 1)[0]["active_in_scene"])
        scene, prop, _holder = tv_scene()
        scene.go[scene.tr[1]["m_GameObject"]["m_PathID"]]["m_IsActive"] = False        # the root TV object
        self.assertFalse(E.read_holders(scene, "S", 1)[0]["active_in_scene"])

    def test_other_scripts_and_other_components_are_not_holders(self):
        scene, _prop, holder = tv_scene()
        scene.attach(holder, holder_blob(1, scene.go_of(holder), size=44), script="Game.Modes.Data.Other")
        scene.attach(holder, b"x" * 12, kind="Transform", script="")
        self.assertEqual(len(E.read_holders(scene, "S", 1)), 1)

    def test_a_holder_of_another_size_is_the_class_changing(self):
        for size in (36, 44, 48):
            scene, _prop, _holder = tv_scene(holder={"size": size})
            with self.subTest(size=size), self.assertRaisesRegex(E.MonitorError, "expected 40"):
                E.read_holders(scene, "S", 1)

    def test_a_holder_with_a_name_or_the_wrong_object_name_or_inactive_or_without_parent_is_refused(self):
        scene, _prop, holder = tv_scene()
        scene.files["level0"].clear()
        scene.attach(holder, holder_blob(1, scene.go_of(holder), name="abcd"))
        with self.assertRaisesRegex(E.MonitorError, "empty name|expected 40"):
            E.read_holders(scene, "S", 1)
        scene, _prop, _holder = tv_scene(holder={"name": "Canvas"})
        with self.assertRaisesRegex(E.MonitorError, "ArenaMonitorHolder"):
            E.read_holders(scene, "S", 1)
        scene, _prop, _holder = tv_scene(holder={"active": False})
        with self.assertRaisesRegex(E.MonitorError, "inactive"):
            E.read_holders(scene, "S", 1)
        orphan = FakeScene()
        lone = orphan.add(E.HOLDER_NAME, rect=(1930.0, 1163.0, 0.5, 0.5))
        orphan.attach(lone, holder_blob(1, orphan.go_of(lone)))
        with self.assertRaisesRegex(E.MonitorError, "no parent prop"):
            E.read_holders(orphan, "S", 1)
        scene, _prop, _holder = tv_scene(holder={"no_rect": True})
        with self.assertRaisesRegex(E.MonitorError, "no RectTransform"):
            E.read_holders(scene, "S", 1)

    def test_a_pose_that_is_not_the_shared_mount_is_refused(self):
        for label, change, message in (
                ("position", {"pos": (0.0, 0.0, 0.4)}, "local position"),
                ("rotation", {"rot": (0.0, 0.0, 0.0, 1.0)}, "local rotation"),
                ("scale", {"scale": (0.001, 0.001, 0.001)}, "local scale"),
                ("size", {"rect": (1920.0, 1080.0, 0.5, 0.5)}, "rect"),
                ("pivot", {"rect": (1930.0, 1163.0, 0.0, 0.5)}, "rect")):
            scene, _prop, _holder = tv_scene(**change)
            with self.subTest(label), self.assertRaisesRegex(E.MonitorError, message):
                E.read_holders(scene, "S", 1)

    def test_the_mount_rotation_may_come_with_either_sign(self):
        r = E.MOUNT["rotation"]
        scene, _prop, _holder = tv_scene(rot=tuple(-v for v in r))
        self.assertEqual(len(E.read_holders(scene, "S", 1)), 1)

    def test_a_prop_that_is_not_uniformly_scaled_is_refused(self):
        scene = FakeScene()
        root = scene.add("TV")
        prop = scene.add("Arena_Home_theater", root, scale=(3.0, 3.0, 2.0))
        pose = mount_pose()
        holder = scene.add(E.HOLDER_NAME, prop, pos=pose["pos"], rot=pose["rot"], scale=pose["scale"], rect=pose["rect"])
        scene.attach(holder, holder_blob(1, scene.go_of(holder)))
        with self.assertRaisesRegex(E.MonitorError, "uniform"):
            E.read_holders(scene, "S", 1)

    def test_an_unknown_type_word_stops_the_read(self):
        scene, _prop, _holder = tv_scene(word=11)
        with self.assertRaisesRegex(E.MonitorError, "type word 11"):
            E.read_holders(scene, "S", 1)

    def test_two_holders_of_one_scene_make_two_rows(self):
        scene, prop, _holder = tv_scene(prop_pos=(0.0, 0.0, 0.0))
        pose = mount_pose()
        second_prop = scene.add("Arena_Home_theater (1)", 1, pos=(5.0, 0.0, 0.0), scale=(3.0, 3.0, 3.0))
        second = scene.add(E.HOLDER_NAME, second_prop, pos=pose["pos"], rot=pose["rot"], scale=pose["scale"], rect=pose["rect"])
        scene.attach(second, holder_blob(0, scene.go_of(second), holder_id=-105))      # the holder id repeats: it is scene-local and never a key
        rows = E.read_holders(scene, "S", 1)
        self.assertEqual(sorted(r["arena_type"] for r in rows), [0, 1])
        self.assertEqual(len({E.row_identity(r) for r in rows}), 2)


class PackageCheckTests(unittest.TestCase):
    """The rows against an extracted package: the prop stands there once and has the row's activity."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.package = Path(self.tmp.name)
        (self.package / "scenes").mkdir()
        self.scene, self.prop, _holder = tv_scene(prop_scale=3.0, prop_pos=(10.0, 5.0, -4.0))
        self.rows = E.read_holders(self.scene, "S", 1)

    def export(self, mutate=None, scene_name="S"):
        objects = []
        for tp, t in self.scene.tr.items():
            if "m_SizeDelta" in t:
                continue                                        # the extractor of the packages skips rect transforms
            p, r, s = t["m_LocalPosition"], t["m_LocalRotation"], t["m_LocalScale"]
            objects.append({"id": str(tp), "name": self.scene.name(tp), "parent_id": str(t["m_Father"]["m_PathID"]) if t["m_Father"]["m_PathID"] else None,
                            "local_position": [p["x"], p["y"], p["z"]], "local_rotation": [r["x"], r["y"], r["z"], r["w"]],
                            "local_scale": [s["x"], s["y"], s["z"]], "active": self.scene.go[t["m_GameObject"]["m_PathID"]]["m_IsActive"]})
        if mutate:
            mutate(objects)
        (self.package / "scenes" / f"{scene_name}.json").write_text(json.dumps({"objects": objects}), encoding="utf-8")

    def test_a_matching_package_passes(self):
        self.export()
        self.assertEqual(E.check_package(self.rows, self.package), [])

    def test_a_prop_that_moved_or_is_missing_or_stands_twice_is_reported(self):
        def move(objects):
            next(o for o in objects if o["name"] == "Arena_Home_theater")["local_position"][0] += 0.2
        self.export(move)
        self.assertIn("0 props", E.check_package(self.rows, self.package)[0])

        def duplicate(objects):
            twin = copy.deepcopy(next(o for o in objects if o["name"] == "Arena_Home_theater"))
            twin["id"] = "999"
            objects.append(twin)
        self.export(duplicate)
        self.assertIn("2 props", E.check_package(self.rows, self.package)[0])
        self.export(lambda objects: objects.remove(next(o for o in objects if o["name"] == "Arena_Home_theater")))
        self.assertIn("0 props", E.check_package(self.rows, self.package)[0])

    def test_a_different_activity_is_reported(self):
        def off(objects):
            next(o for o in objects if o["name"] == "Arena_Home_theater")["active"] = False
        self.export(off)
        problems = E.check_package(self.rows, self.package)
        self.assertEqual(len(problems), 1)
        self.assertIn("active_in_scene is True", problems[0])
        # an inactive ancestor counts too
        self.export(lambda objects: objects[0].update(active=False))
        self.assertTrue(E.check_package(self.rows, self.package))

    def test_a_scene_missing_from_the_package_is_an_error(self):
        with self.assertRaises(DOORS.DoorPoseError):
            E.check_package(self.rows, self.package)


# ---------------------------------------------------------------------------------------------------------------
# The tracked sidecars and the registry
# ---------------------------------------------------------------------------------------------------------------

class SidecarTests(unittest.TestCase):
    EXPECTED = {"equator": (11, 11), "bay5": (8, 8), "airpit": (8, 6), "bowl": (16, 6)}      # rows, active rows

    def test_header_scenes_and_levels(self):
        key_sources = json.loads((REPO / "tools" / "bundle" / "key-sources.json").read_text(encoding="utf-8"))["maps"]
        for stem in STEMS:
            with self.subTest(stem):
                doc, descriptor = sidecar_doc(stem), map_json(stem)
                self.assertEqual((doc["schema_version"], doc["key"]), (1, KEYS[stem]))
                self.assertEqual(doc["source"]["tool"], "tools/extract/extract_score_monitors.py")
                self.assertTrue(doc["source"]["arena_version"])
                self.assertEqual(doc["_comment"][:60], E.SIDECAR_COMMENT[:60])
                table = {e["scene"]: e["index"] for e in key_sources[KEYS[stem]]}
                for entry in doc["source"]["scenes"]:
                    self.assertIn(entry["scene"], descriptor["all_scene_names"])
                    self.assertEqual(entry["level"], table[entry["scene"]])
                # every scene the map imports that key-sources lists was read, in the map's load order
                self.assertEqual([e["scene"] for e in doc["source"]["scenes"]], [n for n in descriptor["all_scene_names"] if n in table])
                self.assertEqual(doc["mount"], E.MOUNT)

    def test_the_tournament_scene_and_other_unimported_scenes_are_never_read(self):
        for stem in STEMS:
            scenes = {e["scene"] for e in sidecar_doc(stem)["source"]["scenes"]}
            self.assertFalse({s for s in scenes if "Tournament" in s}, stem)
        self.assertEqual({e["level"] for e in sidecar_doc("equator")["source"]["scenes"]}, {51, 53, 55, 57, 58})     # no level 61

    def test_every_row_has_exactly_the_schema_and_a_known_type_word(self):
        for stem in STEMS:
            for row in sidecar_doc(stem)["monitors"]:
                with self.subTest(stem=stem, path=row["path"]):
                    self.assertEqual(tuple(row), ROW_KEYS)
                    self.assertEqual((row["template"], row["confirmed"], row["face"]), E.TYPE_WORDS[row["arena_type"]])
                    self.assertEqual(len(row["at"]), 3)
                    self.assertTrue(all(math.isfinite(v) for v in row["at"]))
                    self.assertGreater(row["prop_scale"], 0)
                    self.assertTrue(0 <= row["viewer_yaw"] < 360)
                    self.assertEqual(row["world_size_m"], [round(1930 * 0.00054 * row["prop_scale"], 3), round(1163 * 0.00054 * row["prop_scale"], 3)])

    def test_the_counts_of_holders_active_props_and_faces(self):
        total = active = 0
        for stem, (rows, live) in self.EXPECTED.items():
            monitors = sidecar_doc(stem)["monitors"]
            self.assertEqual((len(monitors), sum(1 for r in monitors if r["active_in_scene"])), (rows, live), stem)
            total, active = total + rows, active + live
        self.assertEqual((total, active), (43, 31))
        faces = {stem: sorted((f, sum(1 for r in sidecar_doc(stem)["monitors"] if r["active_in_scene"] and r["face"] == f))
                              for f in ("timer", "score", "alive", "none")) for stem in STEMS}
        self.assertEqual(faces["equator"], [("alive", 2), ("none", 2), ("score", 5), ("timer", 2)])
        self.assertEqual(faces["bay5"], [("alive", 3), ("none", 0), ("score", 2), ("timer", 3)])
        self.assertEqual(faces["airpit"], [("alive", 2), ("none", 0), ("score", 2), ("timer", 2)])
        self.assertEqual(faces["bowl"], [("alive", 2), ("none", 0), ("score", 2), ("timer", 2)])

    def test_the_type_multiset_per_map(self):
        def words(stem):
            counts = {}
            for r in sidecar_doc(stem)["monitors"]:
                counts[r["arena_type"]] = counts.get(r["arena_type"], 0) + 1
            return counts
        self.assertEqual(words("equator"), {0: 2, 1: 3, 2: 2, 6: 2, 7: 2})
        self.assertEqual(words("bay5"), {0: 3, 1: 2, 2: 3})
        self.assertEqual(words("airpit"), {0: 2, 1: 4, 2: 2})
        self.assertEqual(words("bowl"), {0: 4, 1: 6, 2: 4, 10: 2})

    def test_inactive_rows_are_airpits_team_box_screens_and_bowls_base_set(self):
        inactive = {stem: [r for r in sidecar_doc(stem)["monitors"] if not r["active_in_scene"]] for stem in STEMS}
        self.assertEqual([r["path"] for r in inactive["airpit"]], ["TV/Arena_Home_theater (9)", "TV/Arena_Home_theater (11)"])
        self.assertEqual({r["scene"] for r in inactive["bowl"]}, {"Arena_Bowl_DesignStuff"})
        self.assertEqual((len(inactive["equator"]), len(inactive["bay5"])), (0, 0))

    def test_rows_are_in_load_order_and_unique(self):
        for stem in STEMS:
            doc = sidecar_doc(stem)
            order = [e["scene"] for e in doc["source"]["scenes"]]
            self.assertEqual(doc["monitors"], E.sort_rows(copy.deepcopy(doc["monitors"]), order), stem)
            E.check_unique_rows(doc["key"], doc["monitors"])

    def test_the_sidecar_text_is_what_the_cli_writes(self):
        for stem in STEMS:
            raw = sidecar_path(stem).read_bytes()
            self.assertNotIn(b"\r", raw, stem)
            self.assertEqual(raw.decode("utf-8"), E.render_sidecar(json.loads(raw)), stem)

    def test_the_sidecars_carry_no_hash_and_no_arena_content(self):
        for stem in STEMS:
            text = sidecar_path(stem).read_text(encoding="utf-8")
            self.assertIsNone(re.search(r"[0-9a-fA-F]{32,}", text), stem)           # no digest of any Arena file, ever (key sources)
            self.assertNotIn("sharedassets", text)
            self.assertNotIn(".resS", text)

    def test_the_registry_carries_exactly_the_generated_csharp(self):
        text = DESCRIPTOR.read_text(encoding="utf-8")          # text mode: CRLF reads as LF
        for stem in STEMS:
            with self.subTest(stem):
                self.assertIn(E.render_csharp(sidecar_doc(stem)["monitors"]), text)

    def test_the_registry_and_the_sidecars_agree_and_only_the_released_maps_have_rows(self):
        entries = registry()
        for stem in STEMS:
            name = {"equator": "Equator", "bay5": "Bay5", "airpit": "AirPit", "bowl": "Bowl"}[stem]
            self.assertEqual(VD.score_monitor_errors(map_json(stem), sidecar_path(stem), entries[name].get("ScoreMonitors")), [], stem)
        for name, entry in entries.items():
            if name not in ("Equator", "Bay5", "AirPit", "Bowl"):
                self.assertIsNone(entry.get("ScoreMonitors"), name)
        for path in (REPO / "tools" / "maps").glob("*.monitors.json"):
            self.assertIn(path.name.removesuffix(".monitors.json"), STEMS)

    def test_the_validator_run_passes(self):
        done = subprocess.run([sys.executable, str(REPO / "tools" / "validate" / "validate_map_descriptors.py")],
                              capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertTrue(done.stdout.startswith("OK"), done.stdout)


class MirrorValidatorTests(unittest.TestCase):
    """validate_map_descriptors.score_monitor_errors catches every way the two copies can drift, one negative test per error class."""

    @classmethod
    def setUpClass(cls):
        cls.map = map_json("bowl")
        cls.doc = sidecar_doc("bowl")
        cls.registry = registry()["Bowl"]["ScoreMonitors"]

    def errors(self, doc, registry_rows=None, descriptor=None):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "bowl.monitors.json"
            path.write_text(json.dumps(doc), encoding="utf-8")
            return VD.score_monitor_errors(descriptor or self.map, path, self.registry if registry_rows is None else registry_rows)

    def mutated(self, change, **kwargs):
        doc = copy.deepcopy(self.doc)
        change(doc)
        return self.errors(doc, **kwargs)

    def test_clean(self):
        self.assertEqual(self.errors(self.doc), [])

    def test_a_changed_mirrored_field_is_reported_with_the_row(self):
        for field, value in (("scene", "Arena_Bowl_Light"), ("path", "TV/Other"), ("arena_type", 0), ("active_in_scene", True)):
            with self.subTest(field=field):
                def change(doc, field=field, value=value):
                    row = doc["monitors"][0]
                    row[field] = value
                    if field == "arena_type":
                        row["template"], row["confirmed"], row["face"] = E.TYPE_WORDS[value]
                errors = self.mutated(change)
                self.assertTrue(errors, field)
        errors = self.mutated(lambda d: d["monitors"][0]["at"].__setitem__(0, d["monitors"][0]["at"][0] + 0.001))
        self.assertEqual(len(errors), 1)
        self.assertIn("differs from the registry in at", errors[0])
        self.assertIn("TV/Arena_Home_theater", errors[0])

    def test_a_missing_extra_or_reordered_row_is_reported(self):
        self.assertTrue(any("monitors, the registry" in e for e in self.mutated(lambda d: d["monitors"].pop())))
        self.assertTrue(any("monitors, the registry" in e for e in self.errors(self.doc, registry_rows=self.registry[:-1])))

        def swap(doc):
            doc["monitors"][0], doc["monitors"][1] = doc["monitors"][1], doc["monitors"][0]
        self.assertTrue(self.mutated(swap))

    def test_schema_violations_are_reported(self):
        for label, change in (
                ("schema", lambda d: d.update(schema_version=2)),
                ("key", lambda d: d.update(key="arena_other")),
                ("source scene", lambda d: d["source"]["scenes"][0].update(scene="Arena_Nowhere")),
                ("source level", lambda d: d["source"]["scenes"][0].update(level="43")),
                ("source tool", lambda d: d["source"].pop("tool")),
                ("mount", lambda d: d["mount"].update(scale=0.001)),
                ("no rows", lambda d: d.update(monitors=[])),
                ("extra key", lambda d: d["monitors"][0].update(extra=1)),
                ("missing key", lambda d: d["monitors"][0].pop("viewer_yaw")),
                ("scene not of the map", lambda d: d["monitors"][0].update(scene="Arena_Nowhere")),
                ("path", lambda d: d["monitors"][0].update(path="TV//X")),
                ("at", lambda d: d["monitors"][0].update(at=[1.0, 2.0])),
                ("nan", lambda d: d["monitors"][0].update(at=[1.0, float("nan"), 0.0])),
                ("unknown word", lambda d: d["monitors"][0].update(arena_type=11)),
                ("bool word", lambda d: d["monitors"][0].update(arena_type=True)),
                ("template", lambda d: d["monitors"][0].update(template="MonitorTimer")),
                ("confirmed", lambda d: d["monitors"][0].update(confirmed=False)),
                ("face", lambda d: d["monitors"][0].update(face="alive")),
                ("active", lambda d: d["monitors"][0].update(active_in_scene=1)),
                ("scale", lambda d: d["monitors"][0].update(prop_scale=0)),
                ("size", lambda d: d["monitors"][0].update(world_size_m=[1.0, 1.0])),
                ("yaw", lambda d: d["monitors"][0].update(viewer_yaw=360.0)),
                ("duplicate", lambda d: d["monitors"].append(copy.deepcopy(d["monitors"][0])))):
            with self.subTest(label):
                self.assertTrue(self.mutated(change), label)

    def test_a_registry_without_the_sidecar_and_the_reverse_are_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = Path(tmp) / "bowl.monitors.json"
            self.assertTrue(VD.score_monitor_errors(self.map, missing, self.registry))
            self.assertEqual(VD.score_monitor_errors(self.map, missing, []), [])
            self.assertEqual(VD.score_monitor_errors(self.map, missing, None), [])
        self.assertTrue(self.errors(self.doc, registry_rows=[]))

    def test_invalid_json_and_a_non_object_are_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "bowl.monitors.json"
            path.write_text("{", encoding="utf-8")
            self.assertIn("not valid JSON", VD.score_monitor_errors(self.map, path, self.registry)[0])
            path.write_text("[]", encoding="utf-8")
            self.assertIn("must be an object", VD.score_monitor_errors(self.map, path, self.registry)[0])

    def test_two_rows_of_one_path_at_different_places_are_two_screens(self):
        # Bowl's TeamFight set repeats a path twice (two ALIVE props); distinct positions are fine
        paths = [r["path"] for r in self.doc["monitors"]]
        self.assertTrue(len(paths) != len(set(paths)))
        self.assertEqual(self.errors(self.doc), [])


class RegistryLiteralTests(unittest.TestCase):
    def test_a_literal_parses_into_the_mirrored_fields(self):
        literal = 'new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater (9)", new[] { -31.6808f, 3.9988f, 77.6882f }, 1, false)'
        self.assertEqual(VD.parse_cs_value(literal), {"scene": "Arena_Airpit_DesignStuff", "path": "TV/Arena_Home_theater (9)",
                                                      "at": [-31.6808, 3.9988, 77.6882], "arena_type": 1, "active_in_scene": False})
        self.assertTrue(VD.parse_score_monitor_literal(literal.replace("false)", "true)"))["active_in_scene"])

    def test_malformed_literals_stop_the_validator(self):
        good = 'new ArenaScoreMonitor("S", "P", new[] { 1.0f, 2.0f, 3.0f }, 1, true)'
        for label, bad in (("four arguments", 'new ArenaScoreMonitor("S", "P", new[] { 1.0f, 2.0f, 3.0f }, 1)'),
                           ("six arguments", good[:-1] + ", ActivateOnClaim: true)"),
                           ("word as bool", good.replace("true)", "1)")),
                           ("path not a string", good.replace('"P"', "7")),
                           ("at not an array", good.replace("new[] { 1.0f, 2.0f, 3.0f }", "5")),
                           ("type not an int", good.replace(", 1, true)", ", 1.5f, true)")),
                           ("unterminated", good[:-1])):
            with self.subTest(label), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                VD.parse_cs_value(bad)

    def test_the_literal_survives_comments_between_the_arguments_of_the_registry(self):
        text = ('ScoreMonitors: new[]\n{\n    // a comment line\n    new ArenaScoreMonitor("S", "P", new[] { 1.0f, 2.0f, 3.0f }, 0, true), // trailing\n}\n')
        parsed = VD.parse_cs_value(VD.strip_line_comments(text).split("ScoreMonitors:", 1)[1].strip().rstrip(","))
        self.assertEqual(parsed, [{"scene": "S", "path": "P", "at": [1.0, 2.0, 3.0], "arena_type": 0, "active_in_scene": True}])


# ---------------------------------------------------------------------------------------------------------------
# The installed Arena (skipped without it)
# ---------------------------------------------------------------------------------------------------------------

@unittest.skipUnless(HAVE_ARENA, "Arena is not installed")
class RealArenaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import UnityPy  # noqa: F401
        except ImportError:
            raise unittest.SkipTest("UnityPy is not installed")
        cls.data_dir, cls.version = DOORS.find_arena("auto")
        cls.fresh = {}
        for key in KEYS.values():
            doc, notes = E.extract_map(key, cls.data_dir, cls.version, E.EXTRACT_ROOT)
            cls.fresh[key] = (doc, notes)

    def test_the_tracked_sidecars_are_what_the_installed_arena_yields(self):
        for stem, key in KEYS.items():
            with self.subTest(stem):
                fresh, tracked = self.fresh[key][0], sidecar_doc(stem)
                # the Arena build is recorded, not compared: a hotfix that leaves the holders alone must not fail this test
                self.assertEqual(fresh["monitors"], tracked["monitors"])
                self.assertEqual(fresh["mount"], tracked["mount"])
                self.assertEqual(fresh["source"]["scenes"], tracked["source"]["scenes"])
                self.assertEqual(E.render_sidecar(dict(fresh, source=tracked["source"])), sidecar_path(stem).read_text(encoding="utf-8"))

    def test_the_rows_are_cross_checked_against_the_accepted_packages_when_they_exist(self):
        for key, (doc, notes) in self.fresh.items():
            package = E.EXTRACT_ROOT / E.MAPS[key]["package"]
            if package.is_dir():
                self.assertTrue(any("found once in" in n for n in notes), (key, notes))

    def test_no_holder_hides_in_a_scene_the_sidecar_did_not_list(self):
        # the extractor reads every key-sources scene the map imports; scan them again for ANY MonoBehaviour of the holder class
        for key, (doc, _notes) in self.fresh.items():
            counted = 0
            for name, level in E.map_scenes(key):
                scene = DOORS.ArenaScene(self.data_dir, level)
                counted += sum(1 for obj in scene.files[scene.level_name].values() if obj.type.name == "MonoBehaviour" and scene.mono_class(obj) == E.HOLDER_CLASS)
            self.assertEqual(counted, len(doc["monitors"]), key)

    def test_every_holder_of_the_four_maps_is_forty_bytes_with_the_shared_mount(self):
        # read_holders already fails closed on both; this pins the totals: 43 holders in the imported scenes
        self.assertEqual(sum(len(doc["monitors"]) for doc, _ in self.fresh.values()), 43)

    def test_an_unclassified_type_word_would_stop_the_extraction(self):
        trimmed = {w: v for w, v in E.TYPE_WORDS.items() if w != 10}
        with patch.object(E, "TYPE_WORDS", trimmed), self.assertRaisesRegex(E.MonitorError, "type word 10"):
            E.extract_map("arena_bowl", self.data_dir, self.version, None)

    def test_the_extractor_refuses_a_scene_set_without_holders(self):
        with patch.object(E, "map_scenes", lambda key: [("Arena_Equator", 51)]), self.assertRaisesRegex(E.MonitorError, "no ArenaMonitorHolder"):
            E.extract_map("arena_equator", self.data_dir, self.version, None)


class CliTests(unittest.TestCase):
    def test_check_mode_reports_a_differing_sidecar_and_writes_nothing(self):
        before = {stem: sidecar_path(stem).read_bytes() for stem in STEMS}
        doc = sidecar_doc("bowl")
        fake = (dict(doc, monitors=doc["monitors"][:-1]), [])
        output = io.StringIO()
        with patch.object(E.doors, "find_arena", lambda arena: (Path("."), "x")), patch.object(E, "extract_map", lambda *a, **k: fake), \
                contextlib.redirect_stdout(output):
            status = E.main(["--map", "arena_bowl", "--check", "--no-package"])
        self.assertEqual(status, 1)
        self.assertIn("DIFFERS", output.getvalue())
        self.assertEqual(before, {stem: sidecar_path(stem).read_bytes() for stem in STEMS})

    def test_a_failure_is_one_line_on_stderr_and_exit_code_two(self):
        errors = io.StringIO()

        def broken(*args, **kwargs):
            raise E.MonitorError("boom")

        with patch.object(E.doors, "find_arena", lambda arena: (Path("."), "x")), patch.object(E, "extract_map", broken), contextlib.redirect_stderr(errors):
            self.assertEqual(E.main(["--map", "arena_bowl", "--no-package"]), 2)
        self.assertEqual(errors.getvalue().strip(), "[extract_score_monitors] FAILED: boom")


if __name__ == "__main__":
    unittest.main()
