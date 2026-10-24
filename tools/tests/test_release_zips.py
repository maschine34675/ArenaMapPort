"""The release packager, both layouts (the multi-part set and the single ZIP the user chose for the release):
strict classification of dist/, size gate (multi-part only), compression choice, manifests, checksums,
verification and the extract-and-compare check. Everything runs on a fake dist tree in a temporary directory (tiny
files named like the real ones); only the classification smoke test reads the real dist/, and it is skipped unless
every file the release requires is staged. No bundle bytes are involved anywhere."""
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import random
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import warnings
import zipfile

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "release"))
import make_release_zips as release

PLUGIN = "BepInEx/plugins/maschine-ArenaMapPort"
SERVER = "SPT_Runtime/user/mods/ArenaMapPort"
MAPS = (("arena_equator", "Equator"), ("arena_bay5", "Bay5"), ("arena_airpit", "AirPit"))
COMMIT = "a" * 40
AMPB = b"AMPB"
# The one statement about the gates of the team spawn enclosures that README.md, FORGE.md and the generated package README share
# (whitespace-normalised; docs/progress/door-poses.md, spawn-gates.md): open positions from Arena's data, Arena's own motion where it has one.
GATE_RULE = ("use Arena's own open positions and, where Arena has them, its motions (at half their time). They stand open from raid "
             "start, or a mode plugin such as ArenaMode holds them closed and opens them when the fight starts; Air Pit's passport "
             "control stays shut until a mode plugin opens it. A few doors that Arena itself never moves (three warehouse rollers on "
             "Bay5, the FCK shop's roller shutter on Equator) stay shut.")

MIT = """MIT License

Copyright (c) 2026 {holder}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction.
"""
NOTICES_MD = ("# Third-party notices\n\n"
              + "".join(f"## {title}\n\n```\n{MIT.format(holder=holder)}```\n\n"
                        for title, holder in (("ManimalIcebreaker (a)", "danauraborealis"),
                                              ("ManimalIcebreaker (b)", "danauraborealis"),
                                              ("SPT-InterchangeRework", "LennoxP90"))))


def csproj(assembly, version):
    return (f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>{assembly}</AssemblyName>'
            f'<Version>{version}</Version></PropertyGroup>'
            '<ItemGroup><PackageReference Include="X" Version="9.9.9" /></ItemGroup></Project>')


def quiet(*_args, **_kwargs):
    pass


def shader_sidecar(key):
    return f"material-shaders.{key}.json"


def git_env():
    """The test's own git calls must not inherit GIT_DIR / GIT_WORK_TREE from a hook or an IDE task."""
    return {k: v for k, v in os.environ.items() if not k.upper().startswith("GIT_")}


def fake_dist_files(maps=MAPS):
    """Tiny stand-ins named like the real dist: random bytes where the real file is incompressible (bundles,
    DLLs), repeated text elsewhere. A bundle is "AMPB" + random bytes: since stage 2 of the Arena gate every
    distributed bundle is an encrypted AMPB container and every map carries keys/<key>.json (the packer reads
    four bytes, it never decrypts). AirPit has no flares / water / overview map, Bay5 alone has water."""
    rng = random.Random(20261012)
    text = lambda label: (label.encode() + b" ") * 200
    files = {
        f"{PLUGIN}/maschine-ArenaMapPort.Client.dll": rng.randbytes(3000),
        f"{SERVER}/maschine-ArenaMapPort.Server.dll": rng.randbytes(2000),
        f"{SERVER}/locales/map-descriptions.json": text("descriptions"),
        f"{PLUGIN}/map-selection/region-map.png": b"\x89PNG\r\n\x1a\n" + rng.randbytes(1000),
        f"{PLUGIN}/map-selection/manifest.json": text("selection map manifest"),
    }
    for key, display in maps:
        stem = key.removeprefix("arena_")
        for kind in ("scenes", "navmesh", "preset"):
            files[f"{PLUGIN}/bundles/{key}_{kind}.bundle"] = AMPB + rng.randbytes(4092)
        files[f"{PLUGIN}/keys/{key}.json"] = text(key + "keys")
        files[f"{PLUGIN}/acoustics/{key}/spatial-audio.json"] = text(key + "audio")
        files[f"{PLUGIN}/acoustics/{key}/source.audiobakedata"] = text(key + "bake")
        files[f"{PLUGIN}/acoustics/{key}/room.assets_9.wav"] = rng.randbytes(1500)
        files[f"{PLUGIN}/ai/{stem}.ai.json"] = text(key + "ai")
        for n in (1, 2):
            files[f"{PLUGIN}/loadscreens/{key}_{n}.jpg"] = b"\xff\xd8\xff" + b"\x00" * 3000
        files[f"{PLUGIN}/{shader_sidecar(key)}"] = text(key + "shaders")
        files[f"{SERVER}/db/{key}/base.json"] = text(key + "base")
        files[f"{SERVER}/banners/{key}.jpg"] = b"\xff\xd8\xff" + b"\x01" * 2000
        if key != "arena_airpit":
            files[f"{PLUGIN}/flares/{key}/flares.json"] = text(key + "flares")
            files[f"{PLUGIN}/flares/{key}/textures/atlas.bin"] = rng.randbytes(1000)
        folder = f"{PLUGIN}/dynamicmaps/ArenaMapPort_{display}"
        files[f"{folder}/ArenaMapPort_{display}.jsonc"] = text(key + "overview")
        files[f"{folder}/Layers/Ground.svg"] = text(key + "ground")
        if key == "arena_bay5":
            files[f"{PLUGIN}/water/{key}/water.json"] = text("water")
            files[f"{PLUGIN}/water/{key}/textures/foam.bin"] = rng.randbytes(800)
    return files


def write_tree(root: Path, files):
    for name, data in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)


class FakeRepoCase(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.tmp = Path(temporary.name)
        self.repo = self.tmp / "repo"
        self.dist = self.repo / "dist"
        for project, assembly in (("ArenaMapPort.Client", "maschine-ArenaMapPort.Client"),
                                  ("ArenaMapPort.Server", "maschine-ArenaMapPort.Server")):
            (self.repo / project).mkdir(parents=True)
            (self.repo / project / f"{project}.csproj").write_text(csproj(assembly, "0.1.0"), encoding="utf-8")
        (self.repo / "THIRD-PARTY-NOTICES.md").write_text(NOTICES_MD, encoding="utf-8")
        self.files = fake_dist_files()
        write_tree(self.dist, self.files)
        self.spec = release.load_spec(self.repo, maps=MAPS)

    def scan(self):
        return release.scan_dist(self.dist)

    def partition(self):
        return release.partition_dist(self.scan(), self.spec)

    def build(self, out_name="out", **kwargs):
        kwargs.setdefault("commit", COMMIT)
        kwargs.setdefault("log", quiet)
        return release.build_release(self.spec, self.dist, self.tmp / out_name, self.repo, **kwargs)

    def cli(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with patch.object(release, "default_maps", return_value=MAPS), redirect_stdout(out), redirect_stderr(err):
            code = release.main(list(argv), repo=self.repo)
        return code, out.getvalue(), err.getvalue()

    def pin_released_maps(self):
        """verify_set checks a single ZIP against the released map list (default_maps()); the fake dist carries
        MAPS, so tests that call it directly pin that list instead of depending on the real one."""
        patcher = patch.object(release, "default_maps", return_value=MAPS)
        patcher.start()
        self.addCleanup(patcher.stop)


class PartitionTests(FakeRepoCase):
    def test_partition_covers_dist_exactly_once(self):
        parts = self.partition()
        names = [f.rel for part in parts for f in part.files]
        self.assertEqual(len(names), len(set(names)), "a file is in two parts")
        self.assertEqual(sorted(names), sorted(self.files))
        self.assertEqual([p.id for p in parts], ["core", "equator", "bay5", "airpit"])
        self.assertEqual([p.zip_name for p in parts], [
            "maschine-ArenaMapPort-0.1.0-part1of4-core.zip",
            "maschine-ArenaMapPort-0.1.0-part2of4-equator.zip",
            "maschine-ArenaMapPort-0.1.0-part3of4-bay5.zip",
            "maschine-ArenaMapPort-0.1.0-part4of4-airpit.zip"])

    def test_core_holds_only_the_five_shared_files(self):
        core = self.partition()[0]
        self.assertEqual({f.rel for f in core.files}, {
            f"{PLUGIN}/maschine-ArenaMapPort.Client.dll", f"{SERVER}/maschine-ArenaMapPort.Server.dll",
            f"{SERVER}/locales/map-descriptions.json",
            f"{PLUGIN}/map-selection/region-map.png", f"{PLUGIN}/map-selection/manifest.json"})

    def test_each_map_part_holds_only_its_own_files(self):
        for part, (key, display) in zip(self.partition()[1:], MAPS):
            for f in part.files:
                self.assertTrue(key in f.rel or f.rel.endswith(f"/ai/{part.id}.ai.json")
                                or f"ArenaMapPort_{display}/" in f.rel,
                                f"{f.rel} does not belong to {key}")

    def test_base_json_lands_with_its_bundles(self):
        for part, (key, _display) in zip(self.partition()[1:], MAPS):
            names = {f.rel for f in part.files}
            self.assertIn(f"{SERVER}/db/{key}/base.json", names)
            for kind in ("scenes", "navmesh", "preset"):
                self.assertIn(f"{PLUGIN}/bundles/{key}_{kind}.bundle", names)

    def test_every_shader_sidecar_carries_its_map_key(self):
        parts = {p.id: {f.rel for f in p.files} for p in self.partition()}
        self.assertIn(f"{PLUGIN}/material-shaders.arena_equator.json", parts["equator"])
        self.assertIn(f"{PLUGIN}/material-shaders.arena_bay5.json", parts["bay5"])
        self.assertIn(f"{PLUGIN}/material-shaders.arena_airpit.json", parts["airpit"])

    def test_unknown_files_are_rejected(self):
        for name in (f"{PLUGIN}/extra.txt", f"{PLUGIN}/maschine-ArenaMapPort.Client.pdb",
                     f"{SERVER}/Thumbs.db", f"{PLUGIN}/README.txt", f"{PLUGIN}/parts/core.json",
                     f"{PLUGIN}/bundles/arena_airpit_scenes.bundle.bak", f"{PLUGIN}/material-shaders.json",
                     f"{PLUGIN}/bundles/xarena_airpit_scenes.bundle",
                     f"{PLUGIN}/bundles/arena_airpit_other.bundle",
                     f"{PLUGIN}/acoustics/loose.json", f"{SERVER}/db/base.json",
                     f"{SERVER}/db/arena_airpit/other.json", f"{PLUGIN}/loadscreens/arena_airpit_x.jpg",
                     f"{PLUGIN}/arena-audio/clip.wav",
                     f"{PLUGIN}/map-selection/Thumbs.db", f"{PLUGIN}/map-selection/region-map.png.bak",
                     f"{PLUGIN}/map-selection/manifest.json.bak", f"{PLUGIN}/map-selection/other.png",
                     f"{PLUGIN}/map-selection/nested/region-map.png",
                     # strays inside the folders the build stages file by file
                     f"{PLUGIN}/acoustics/arena_bay5/Thumbs.db", f"{PLUGIN}/acoustics/arena_bay5/notes.txt",
                     f"{PLUGIN}/acoustics/arena_equator/old/spatial-audio.json",
                     f"{PLUGIN}/acoustics/arena_equator/clip.wav.bak",
                     f"{PLUGIN}/flares/arena_equator/flares.json.bak", f"{PLUGIN}/flares/arena_equator/other.json",
                     f"{PLUGIN}/flares/arena_equator/textures/desktop.ini",
                     f"{PLUGIN}/flares/arena_equator/textures/nested/a.bin",
                     f"{PLUGIN}/flares/arena_equator/loose.bin",
                     f"{PLUGIN}/water/arena_bay5/textures/desktop.ini", f"{PLUGIN}/water/arena_bay5/water.json~",
                     f"{PLUGIN}/water/arena_bay5/textures/a/b.bin", f"{PLUGIN}/water/arena_bay5/extra.json",
                     f"{PLUGIN}/dynamicmaps/ArenaMapPort_Equator/Layers/Ground.svg~",
                     f"{PLUGIN}/dynamicmaps/ArenaMapPort_Equator/Layers/sub/x.svg",
                     f"{PLUGIN}/dynamicmaps/ArenaMapPort_Bay5/.git/config",
                     f"{PLUGIN}/dynamicmaps/ArenaMapPort_Bay5/ArenaMapPort_Equator.jsonc",
                     f"{PLUGIN}/dynamicmaps/ArenaMapPort_Bay5/Ground.svg"):
            with self.subTest(name=name):
                path = self.dist / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x")
                try:
                    with self.assertRaises(release.ClassificationError) as caught:
                        self.partition()
                    self.assertIn(name, str(caught.exception))
                finally:
                    path.unlink()

    def test_parked_map_files_are_rejected(self):
        parked = (f"{PLUGIN}/bundles/arena_airpit2_scenes.bundle", f"{PLUGIN}/bundles/arena_airpit2_navmesh.bundle",
                  f"{PLUGIN}/bundles/arena_airpit2_preset.bundle", f"{PLUGIN}/ai/airpit2.ai.json",
                  f"{PLUGIN}/material-shaders.arena_airpit2.json", f"{PLUGIN}/acoustics/arena_airpit2/a.json",
                  f"{PLUGIN}/flares/arena_airpit2/flares.json", f"{PLUGIN}/water/arena_airpit2/water.json",
                  f"{PLUGIN}/loadscreens/arena_airpit2_1.jpg", f"{PLUGIN}/dynamicmaps/ArenaMapPort_AirPit2/a.jsonc",
                  f"{SERVER}/db/arena_airpit2/base.json", f"{SERVER}/banners/arena_airpit2.jpg",
                  f"{PLUGIN}/bundles/arena_prison_scenes.bundle", f"{SERVER}/db/arena_prison/base.json")
        for name in parked:
            with self.subTest(name=name):
                path = self.dist / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"parked")
                try:
                    with self.assertRaises(release.ClassificationError) as caught:
                        self.partition()
                    self.assertIn(name, str(caught.exception))
                finally:
                    path.unlink()

    def test_a_file_matching_two_rules_is_rejected(self):
        overlapping = [release.Rule("core", "first", release.re.compile(r"a/.*")),
                       release.Rule("equator", "second", release.re.compile(r"a/b"))]
        with self.assertRaises(release.ClassificationError) as caught:
            release.classify("a/b", overlapping)
        self.assertIn("doubly classified", str(caught.exception))
        self.assertEqual(release.classify("a/c", overlapping).part, "core")
        with self.assertRaises(release.ClassificationError):
            release.classify("z", overlapping)

    def test_shipped_rules_never_overlap_on_the_fake_tree(self):
        rules = release.build_rules(self.spec)
        for name in self.files:
            hits = [r for r in rules if r.regex.fullmatch(name)]
            self.assertEqual(len(hits), 1, name)

    def test_incomplete_dist_is_rejected(self):
        names = [f"{SERVER}/db/arena_bay5/base.json", f"{PLUGIN}/bundles/arena_equator_navmesh.bundle",
                 f"{SERVER}/locales/map-descriptions.json", f"{PLUGIN}/maschine-ArenaMapPort.Client.dll",
                 f"{SERVER}/maschine-ArenaMapPort.Server.dll",
                 f"{PLUGIN}/map-selection/region-map.png", f"{PLUGIN}/map-selection/manifest.json"]
        for key, _display in MAPS:  # everything validate_distribution.py demands of every released map
            names += [f"{PLUGIN}/{shader_sidecar(key)}", f"{PLUGIN}/ai/{key.removeprefix('arena_')}.ai.json",
                      f"{SERVER}/banners/{key}.jpg", f"{PLUGIN}/acoustics/{key}/spatial-audio.json",
                      f"{PLUGIN}/loadscreens/{key}_1.jpg"]
        for name in names:
            with self.subTest(name=name):
                data = (self.dist / name).read_bytes()
                (self.dist / name).unlink()
                try:
                    with self.assertRaises(release.PackagingError) as caught:
                        self.partition()
                    self.assertIn("incomplete dist", str(caught.exception))
                    self.assertIn(name, str(caught.exception))
                finally:
                    (self.dist / name).write_bytes(data)

    def test_flares_water_and_overview_map_stay_optional(self):
        for folder in ("flares", "water", "dynamicmaps"):
            shutil.rmtree(self.dist / PLUGIN / folder)
        parts = {p.id: len(p.files) for p in self.partition()}
        self.assertEqual(sorted(parts), ["airpit", "bay5", "core", "equator"])
        self.assertTrue(all(count > 0 for count in parts.values()))

    def test_the_pinned_leaf_names_accept_what_the_build_stages(self):
        rules = release.build_rules(self.spec)
        for name in (f"{PLUGIN}/acoustics/arena_bay5/sharedassets87.assets_74.wav",
                     f"{PLUGIN}/acoustics/arena_bay5/source.audiobakedata",
                     f"{PLUGIN}/flares/arena_equator/textures/resources.assets_2812.bin",
                     f"{PLUGIN}/water/arena_bay5/textures/normalsDetails.bin",
                     f"{PLUGIN}/map-selection/region-map.png", f"{PLUGIN}/map-selection/manifest.json",
                     f"{PLUGIN}/dynamicmaps/ArenaMapPort_Equator/Layers/Ground.svg"):
            with self.subTest(name=name):
                self.assertEqual(len([r for r in rules if r.regex.fullmatch(name)]), 1)

    def test_an_unlistable_directory_is_an_error_not_a_silent_skip(self):
        real_scandir = os.scandir
        target = self.dist / PLUGIN / "acoustics" / "arena_bay5"

        def flaky(path="."):
            if Path(path) == target:
                raise PermissionError(13, "Access is denied", str(path))
            return real_scandir(path)

        with patch.object(os, "scandir", flaky):
            with self.assertRaises(release.PackagingError) as caught:
                release.scan_dist(self.dist)
            self.assertIn("cannot list", str(caught.exception))
            self.assertIn("arena_bay5", str(caught.exception))
            with self.assertRaises(release.PackagingError):
                self.build()
        self.assertEqual(len(self.scan()), len(self.files))  # the same tree is complete once it can be read

    def test_linked_files_and_directories_are_refused(self):
        victim = self.dist / PLUGIN / "ai" / "bay5.ai.json"
        with patch.object(Path, "is_symlink", autospec=True, side_effect=lambda path: path == victim):
            with self.assertRaises(release.PackagingError) as caught:
                release.scan_dist(self.dist)
        self.assertIn("symbolic link refused", str(caught.exception))
        with patch.object(Path, "is_symlink", autospec=True, side_effect=lambda path: path.name == "banners"):
            with self.assertRaises(release.PackagingError) as caught:
                release.scan_dist(self.dist)
        self.assertIn("linked directory refused", str(caught.exception))
        if hasattr(Path, "is_junction"):
            with patch.object(Path, "is_junction", autospec=True, side_effect=lambda path: path.name == "bundles"):
                with self.assertRaises(release.PackagingError) as caught:
                    release.scan_dist(self.dist)
            self.assertIn("linked directory refused", str(caught.exception))

    def test_case_insensitive_collisions_are_refused(self):
        collide = [release.DistFile("a/B.txt", Path("x"), 1), release.DistFile("a/b.txt", Path("y"), 1)]
        with self.assertRaises(release.PackagingError) as caught:
            release.check_case_collisions(collide)
        self.assertIn("collision", str(caught.exception))
        release.check_case_collisions(collide[:1] + [release.DistFile("a/c.txt", Path("z"), 1)])

    def test_unsafe_names_are_rejected(self):
        for name in ("a/../b", "a\\b", "C:x", "/abs", "a//b", "café/x", "a/./b", "a/b."):
            with self.subTest(name=name):
                with self.assertRaises(release.PackagingError):
                    release.check_safe_name(name)
        release.check_safe_name(f"{PLUGIN}/bundles/arena_equator_scenes.bundle")

    def test_empty_or_missing_dist_is_rejected(self):
        with self.assertRaises(release.PackagingError):
            release.scan_dist(self.tmp / "absent")
        (self.tmp / "empty").mkdir()
        with self.assertRaises(release.PackagingError):
            release.scan_dist(self.tmp / "empty")


class SpecTests(FakeRepoCase):
    def test_version_comes_from_the_csproj(self):
        self.assertEqual(self.spec.version, "0.1.0")
        self.assertEqual(self.spec.client_dll, f"{PLUGIN}/maschine-ArenaMapPort.Client.dll")
        self.assertEqual(self.spec.server_dll, f"{SERVER}/maschine-ArenaMapPort.Server.dll")

    def test_disagreeing_csproj_versions_are_an_error_unless_overridden(self):
        server = self.repo / "ArenaMapPort.Server/ArenaMapPort.Server.csproj"
        server.write_text(csproj("maschine-ArenaMapPort.Server", "0.2.0"), encoding="utf-8")
        with self.assertRaises(release.PackagingError) as caught:
            release.load_spec(self.repo, maps=MAPS)
        self.assertIn("disagree", str(caught.exception))
        self.assertEqual(release.load_spec(self.repo, "0.3.0-rc1", maps=MAPS).version, "0.3.0-rc1")

    def test_invalid_version_is_rejected(self):
        for version in ("1", "1.2", "../1.2.3", "1.2.3/x", "v1.2.3", "1.2.3 "):
            with self.subTest(version=version):
                with self.assertRaises(release.PackagingError):
                    release.load_spec(self.repo, version, maps=MAPS)

    def test_real_release_maps_come_from_the_validator(self):
        from validate_map_descriptors import RELEASE_MAP_KEYS, RELEASE_MAP_NAMES
        self.assertEqual(release.default_maps(), tuple(zip(RELEASE_MAP_KEYS, RELEASE_MAP_NAMES)))


class CompressionTests(FakeRepoCase):
    def decide(self, name, data, probe_bytes=release.PROBE_BYTES):
        path = self.tmp / "probe" / name.replace("/", "_")
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(data)
        return release.decide_file(release.DistFile(name, path, len(data)), probe_bytes)

    def test_incompressible_data_is_stored(self):
        decision = self.decide("a.bundle", random.Random(1).randbytes(50000))
        self.assertEqual(decision.method, zipfile.ZIP_STORED)
        self.assertLess(decision.gain, release.MIN_GAIN)
        self.assertEqual(decision.estimate, 50000)

    def test_compressible_data_is_deflated(self):
        decision = self.decide("a.json", b"abcdefgh" * 20000)
        self.assertEqual(decision.method, zipfile.ZIP_DEFLATED)
        self.assertGreater(decision.gain, 0.9)
        self.assertLess(decision.estimate, 50000)

    def test_jpg_is_always_stored_even_when_compressible(self):
        for name in ("a.jpg", "A.JPG", "b.jpeg"):
            self.assertEqual(self.decide(name, b"\x00" * 100000).method, zipfile.ZIP_STORED, name)

    def test_empty_file_is_stored(self):
        self.assertEqual(self.decide("empty.bin", b"").method, zipfile.ZIP_STORED)

    def test_probe_reads_only_the_head(self):
        mixed = random.Random(2).randbytes(2000) + b"\x00" * 200000
        self.assertEqual(self.decide("head.bin", mixed, probe_bytes=2000).method, zipfile.ZIP_STORED)
        self.assertEqual(self.decide("head.bin", mixed, probe_bytes=100000).method, zipfile.ZIP_DEFLATED)

    def test_threshold_is_three_percent(self):
        # Random bytes do not deflate; the zero tail deflates to almost nothing, so the gain tracks its share.
        noise = random.Random(3).randbytes(100000)
        under = self.decide("under.bin", noise[:98000] + b"\x00" * 2000)
        over = self.decide("over.bin", noise[:96000] + b"\x00" * 4000)
        self.assertTrue(0.01 < under.gain < release.MIN_GAIN, under.gain)
        self.assertEqual(under.method, zipfile.ZIP_STORED)
        self.assertTrue(release.MIN_GAIN < over.gain < 0.05, over.gain)
        self.assertEqual(over.method, zipfile.ZIP_DEFLATED)

    def test_built_entries_use_the_chosen_method(self):
        result = self.build()
        methods = {}
        for path in result.zips:
            with zipfile.ZipFile(path) as archive:
                methods.update({i.filename: i.compress_type for i in archive.infolist()})
        self.assertEqual(methods[f"{PLUGIN}/bundles/arena_equator_scenes.bundle"], zipfile.ZIP_STORED)
        self.assertEqual(methods[f"{PLUGIN}/loadscreens/arena_equator_1.jpg"], zipfile.ZIP_STORED)
        self.assertEqual(methods[f"{SERVER}/banners/arena_equator.jpg"], zipfile.ZIP_STORED)
        self.assertEqual(methods[f"{SERVER}/db/arena_equator/base.json"], zipfile.ZIP_DEFLATED)
        self.assertEqual(methods[f"{PLUGIN}/acoustics/arena_equator/spatial-audio.json"], zipfile.ZIP_DEFLATED)

    def test_plan_lists_the_choice_for_large_files(self):
        parts = self.partition()
        decisions = {f.rel: release.decide_file(f) for p in parts for f in p.files}
        big = release.DistFile(f"{PLUGIN}/bundles/arena_equator_huge.bundle", self.tmp / "none", 5_000_000)
        parts[1].files.append(big)
        decisions[big.rel] = release.Decision(zipfile.ZIP_STORED, 0.0, 5_000_000)
        estimates = release.estimate_parts(self.spec, parts, decisions,
                                           release.generated_core_entries(self.spec, parts, self.repo), COMMIT)
        text = release.format_plan(self.spec, self.dist, COMMIT, estimates, decisions, release.SIZE_LIMIT)
        self.assertRegex(text, r"STORED\s+gain\s+0\.0%\s+5,000,000 B -> ~\s+5,000,000 B\s+" + release.re.escape(big.rel))
        self.assertNotIn("WARNING", text)
        small = release.format_plan(self.spec, self.dist, COMMIT, estimates, decisions, 1000)
        self.assertIn("WARNING: part 2 (equator)", small)


class BuildTests(FakeRepoCase):
    def read_zips(self, result):
        contents = {}
        for path in result.zips:
            with zipfile.ZipFile(path) as archive:
                contents[path.name] = {i.filename: archive.read(i) for i in archive.infolist()}
        return contents

    def generated_names(self):
        return {f"{PLUGIN}/README.txt", f"{PLUGIN}/THIRD-PARTY-NOTICES.txt", f"{SERVER}/THIRD-PARTY-NOTICES.txt",
                *(f"{PLUGIN}/parts/{i}.json" for i in ("core", "equator", "bay5", "airpit"))}

    def test_extract_all_parts_overlays_to_dist(self):
        result = self.build()
        scratch = self.tmp / "overlay"
        for path in result.zips:
            with zipfile.ZipFile(path) as archive:
                archive.extractall(scratch)  # independent of the module's own extractor
        extracted = {p.relative_to(scratch).as_posix(): p.read_bytes() for p in scratch.rglob("*") if p.is_file()}
        generated = self.generated_names()
        self.assertTrue(generated <= set(extracted))
        shipped = {name: data for name, data in extracted.items() if name not in generated}
        self.assertEqual(shipped, self.files)

    def test_extract_check_compares_with_dist(self):
        result = self.build()
        verified = release.verify_set(result.out_dir, log=quiet)
        count = release.extract_check(verified, self.dist, self.tmp / "scratch", log=quiet)
        self.assertEqual(count, len(self.files))
        # a changed dist byte is caught
        victim = self.dist / f"{SERVER}/db/arena_bay5/base.json"
        victim.write_bytes(victim.read_bytes()[:-1] + b"!")
        with self.assertRaises(release.VerificationError) as caught:
            release.extract_check(verified, self.dist, self.tmp / "scratch2", log=quiet)
        self.assertIn("db/arena_bay5/base.json", str(caught.exception))

    def test_extract_check_refuses_a_non_empty_scratch_directory(self):
        result = self.build()
        verified = release.verify_set(result.out_dir, log=quiet)
        scratch = self.tmp / "scratch"
        scratch.mkdir()
        (scratch / "old.txt").write_text("x")
        with self.assertRaises(release.PackagingError) as caught:
            release.extract_check(verified, self.dist, scratch, log=quiet)
        self.assertIn("empty", str(caught.exception))
        self.assertEqual([p.name for p in scratch.iterdir()], ["old.txt"])

    def test_extract_check_refuses_a_scratch_directory_inside_dist(self):
        result = self.build()
        verified = release.verify_set(result.out_dir, log=quiet)
        with self.assertRaises(release.PackagingError):
            release.extract_check(verified, self.dist, self.dist / "scratch", log=quiet)

    def test_every_part_manifest_names_all_parts(self):
        result = self.build()
        zips = [p.name for p in result.zips]
        contents = self.read_zips(result)
        self.assertEqual(len(zips), 4)
        for index, (zip_name, entries) in enumerate(contents.items(), 1):
            part_id = zip_name.rsplit("-", 1)[1].removesuffix(".zip")
            manifest = json.loads(entries[f"{PLUGIN}/parts/{part_id}.json"])
            self.assertEqual(manifest["schema"], 1)
            self.assertEqual(manifest["version"], "0.1.0")
            self.assertEqual(manifest["source_commit"], COMMIT)
            self.assertEqual(manifest["part"], {"index": index, "of": 4, "id": part_id, "zip": zip_name})
            self.assertEqual([p["zip"] for p in manifest["parts"]], zips)
            self.assertEqual([p["index"] for p in manifest["parts"]], [1, 2, 3, 4])
            self.assertEqual([p["id"] for p in manifest["parts"]], ["core", "equator", "bay5", "airpit"])
            self.assertIn("ALL 4 parts", manifest["install"])
            for listed, plan in zip(manifest["parts"], result.parts):
                self.assertEqual(listed["dist_files"], len(plan.files))
                self.assertEqual(listed["raw_bytes"], plan.raw_bytes)
            own = {e["path"]: e for e in manifest["entries"]}
            self.assertEqual(set(own), set(entries) - {f"{PLUGIN}/parts/{part_id}.json"})
            for path, entry in own.items():
                self.assertEqual(entry["size"], len(entries[path]))
                self.assertEqual(entry["sha256"], hashlib.sha256(entries[path]).hexdigest())
                self.assertEqual(entry["origin"], "dist" if path in self.files else "generated")

    def test_sha256_file_covers_all_parts(self):
        result = self.build()
        self.assertEqual(result.sha256_path.name, "maschine-ArenaMapPort-0.1.0.sha256")
        text = result.sha256_path.read_bytes().decode("ascii")
        self.assertNotIn("\r", text)
        self.assertEqual(text, "".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n"
                                       for p in result.zips))
        self.assertEqual(len(text.splitlines()), 4)

    def test_readme_and_notices_are_core_only(self):
        result = self.build()
        contents = self.read_zips(result)
        core = contents["maschine-ArenaMapPort-0.1.0-part1of4-core.zip"]
        readme = core[f"{PLUGIN}/README.txt"].decode("utf-8")
        for plan in result.parts:
            self.assertIn(plan.zip_name, readme)
        self.assertIn("EVERY part", readme)
        self.assertIn("close", readme.lower())
        self.assertIn("Equator, Bay5, AirPit", readme)
        self.assertIn("selection artwork", readme)
        notices = core[f"{PLUGIN}/THIRD-PARTY-NOTICES.txt"]
        self.assertEqual(notices, core[f"{SERVER}/THIRD-PARTY-NOTICES.txt"])
        text = notices.decode("utf-8")
        self.assertEqual(text.count("MIT License"), 2)
        self.assertIn("danauraborealis", text)
        self.assertIn("LennoxP90", text)
        for name, entries in contents.items():
            if "core" in name:
                continue
            self.assertEqual({n for n in entries if n not in self.files},
                             {f"{PLUGIN}/parts/{name.rsplit('-', 1)[1].removesuffix('.zip')}.json"}, name)

    def test_selection_artwork_is_byte_identical_and_core_only(self):
        result = self.build()
        contents = self.read_zips(result)
        core_name = "maschine-ArenaMapPort-0.1.0-part1of4-core.zip"
        for path in (f"{PLUGIN}/map-selection/region-map.png", f"{PLUGIN}/map-selection/manifest.json"):
            self.assertEqual(contents[core_name][path], self.files[path])
            self.assertEqual([name for name, entries in contents.items() if path in entries], [core_name])

    def test_zip_timestamps_and_attributes_are_fixed(self):
        result = self.build()
        for path in result.zips:
            with zipfile.ZipFile(path) as archive:
                for info in archive.infolist():
                    self.assertEqual(info.date_time, (1980, 1, 1, 0, 0, 0), info.filename)
                    self.assertEqual(info.external_attr >> 16, 0o100644)

    def test_build_is_reproducible(self):
        first = self.build("one")
        second = self.build("two")
        for a, b in zip(first.zips, second.zips):
            self.assertEqual(a.read_bytes(), b.read_bytes(), a.name)
        self.assertEqual(first.sha256_path.read_bytes(), second.sha256_path.read_bytes())

    def test_part_size_limit_is_enforced(self):
        out = self.tmp / "limited"
        with self.assertRaises(release.PartTooLarge):
            self.build("limited", size_limit=300)
        self.assertEqual(list(out.iterdir()) if out.exists() else [], [], "failed build left files behind")
        largest = max(p.stat().st_size for p in self.build("probe").zips)
        with self.assertRaises(release.PartTooLarge):
            self.build("exact", size_limit=largest)  # strictly below the limit
        self.assertEqual(list((self.tmp / "exact").iterdir()), [])
        self.assertEqual(len(self.build("just-fits", size_limit=largest + 1).zips), 4)

    def test_real_limit_is_two_gibibytes(self):
        self.assertEqual(release.SIZE_LIMIT, 2 ** 31)

    def test_existing_outputs_are_not_overwritten_without_force(self):
        first = self.build()
        before = {p.name: p.read_bytes() for p in first.out_dir.iterdir()}
        with self.assertRaises(release.PackagingError) as caught:
            self.build()
        self.assertIn("--force", str(caught.exception))
        self.assertEqual({p.name: p.read_bytes() for p in first.out_dir.iterdir()}, before)
        self.build(force=True)
        self.assertEqual({p.name for p in first.out_dir.iterdir()}, set(before))

    def test_stale_partial_blocks_a_build_without_force(self):
        out = self.tmp / "out"
        out.mkdir()
        stale = out / "maschine-ArenaMapPort-0.1.0-part2of4-equator.zip.partial"
        stale.write_bytes(b"crashed")
        with self.assertRaises(release.PackagingError):
            self.build()
        self.assertTrue(stale.exists())
        result = self.build(force=True)
        self.assertFalse(stale.exists())
        self.assertEqual(len(result.zips), 4)

    def snapshot(self, directory):
        return {p.name: p.read_bytes() for p in directory.iterdir()}

    def test_failed_forced_rebuild_leaves_the_previous_release_intact(self):
        first = self.build()
        before = self.snapshot(first.out_dir)
        with self.assertRaises(release.PartTooLarge):
            self.build(force=True, size_limit=300)
        self.assertEqual(self.snapshot(first.out_dir), before)
        self.assertEqual(len(release.verify_set(first.out_dir, log=quiet).parts), 4)
        # the same holds for a source file that changes while packaging, and for Ctrl-C
        victim = f"{SERVER}/db/arena_bay5/base.json"
        files = [release.DistFile(f.rel, f.path, f.size + 1) if f.rel == victim else f for f in self.scan()]
        with patch.object(release, "scan_dist", return_value=files):
            with self.assertRaises(release.PackagingError):
                self.build(force=True)
        self.assertEqual(self.snapshot(first.out_dir), before)
        with patch.object(release, "write_part", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                self.build(force=True)
        self.assertEqual(self.snapshot(first.out_dir), before)

    def test_successful_forced_rebuild_replaces_the_set(self):
        first = self.build()
        self.dist.joinpath(SERVER, "db", "arena_bay5", "base.json").write_bytes(b"changed")
        second = self.build(force=True)
        self.assertNotEqual(first.digests[2], second.digests[2])
        self.assertEqual(release.verify_set(second.out_dir, log=quiet).version, "0.1.0")
        self.assertEqual(sorted(p.name for p in second.out_dir.iterdir()),
                         sorted([p.name for p in second.zips] + [second.sha256_path.name]))

    def test_a_changed_part_count_never_leaves_two_sets(self):
        four = self.build()
        before = self.snapshot(four.out_dir)
        spec3 = release.load_spec(self.repo, maps=MAPS[:2])
        dist3 = self.tmp / "dist3"
        write_tree(dist3, fake_dist_files(MAPS[:2]))

        def three_parts(**kwargs):
            return release.build_release(spec3, dist3, four.out_dir, self.repo, commit=COMMIT, log=quiet, **kwargs)

        with self.assertRaises(release.PackagingError) as caught:
            three_parts()  # without --force the older 4-part files count as existing output
        self.assertIn("part1of4-core.zip", str(caught.exception))
        self.assertIn("--force", str(caught.exception))
        self.assertEqual(self.snapshot(four.out_dir), before)
        (four.out_dir / "maschine-ArenaMapPort-0.1.0-part2of5-equator.zip.partial").write_bytes(b"crashed")
        result = three_parts(force=True)
        self.assertEqual(sorted(p.name for p in four.out_dir.iterdir()),
                         sorted([p.name for p in result.zips] + [result.sha256_path.name]))
        self.assertEqual([p.id for p in release.verify_set(four.out_dir, log=quiet).parts],
                         ["core", "equator", "bay5"])

    def test_other_versions_in_the_same_directory_are_left_alone(self):
        first = self.build()
        spec2 = release.load_spec(self.repo, "0.2.0", maps=MAPS)
        second = release.build_release(spec2, self.dist, first.out_dir, self.repo, commit=COMMIT, log=quiet)
        self.assertEqual(len(list(first.out_dir.glob("*.zip"))), 8)
        release.build_release(spec2, self.dist, first.out_dir, self.repo, force=True, commit=COMMIT, log=quiet)
        self.assertEqual(len(list(first.out_dir.glob("*.zip"))), 8)
        self.assertEqual(release.verify_set(first.out_dir, "0.1.0", log=quiet).version, "0.1.0")
        self.assertEqual(release.verify_set(first.out_dir, "0.2.0", log=quiet).version, "0.2.0")
        self.assertEqual(second.sha256_path.name, "maschine-ArenaMapPort-0.2.0.sha256")

    def test_no_partial_files_remain_after_a_successful_build(self):
        result = self.build()
        self.assertEqual(sorted(p.name for p in result.out_dir.iterdir()),
                         sorted([p.name for p in result.zips] + [result.sha256_path.name]))

    def test_source_changing_during_packaging_fails_the_build_cleanly(self):
        victim = f"{SERVER}/db/arena_bay5/base.json"
        files = [release.DistFile(f.rel, f.path, f.size + 1) if f.rel == victim else f for f in self.scan()]
        with patch.object(release, "scan_dist", return_value=files):
            with self.assertRaises(release.PackagingError) as caught:
                self.build()
        self.assertIn("changed while packaging", str(caught.exception))
        self.assertEqual(list((self.tmp / "out").iterdir()), [])

    def test_output_inside_dist_is_refused(self):
        with self.assertRaises(release.PackagingError):
            release.build_release(self.spec, self.dist, self.dist / "out", self.repo, commit=COMMIT, log=quiet)

    def test_estimates_are_in_the_right_range(self):
        result = self.build()
        parts = self.partition()
        decisions = {f.rel: release.decide_file(f) for p in parts for f in p.files}
        estimates = release.estimate_parts(self.spec, parts, decisions,
                                           release.generated_core_entries(self.spec, parts, self.repo), COMMIT)
        for est, size in zip(estimates, result.sizes):
            self.assertLess(abs(est.estimated_zip - size), 0.3 * size + 1000, (est.plan.id, est.estimated_zip, size))


class VerifyTests(FakeRepoCase):
    def copy_build(self):
        result = self.build()
        copy = self.tmp / "copy"
        shutil.copytree(result.out_dir, copy)
        return result, copy

    def flip_stored_byte(self, directory, result):
        """Flip one byte inside the stored bundle bytes of the equator part; returns the zip path."""
        path = directory / result.zips[1].name
        data = bytearray(path.read_bytes())
        needle = self.files[f"{PLUGIN}/bundles/arena_equator_scenes.bundle"][100:140]
        offset = bytes(data).find(needle)
        self.assertGreater(offset, 0)
        data[offset] ^= 0xFF
        path.write_bytes(bytes(data))
        return path

    def test_untouched_build_verifies(self):
        _result, copy = self.copy_build()
        verified = release.verify_set(copy, log=quiet)
        self.assertEqual([p.id for p in verified.parts], ["core", "equator", "bay5", "airpit"])
        self.assertEqual(verified.version, "0.1.0")

    def test_verify_detects_a_tampered_byte(self):
        result, copy = self.copy_build()
        self.flip_stored_byte(copy, result)
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(copy, log=quiet)
        self.assertIn("SHA-256 mismatch", str(caught.exception))

    def test_verify_detects_a_tampered_byte_even_when_the_checksum_is_rewritten(self):
        result, copy = self.copy_build()
        path = self.flip_stored_byte(copy, result)
        sha = copy / result.sha256_path.name
        lines = sha.read_text(encoding="ascii").splitlines()
        lines[1] = f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}"
        sha.write_text("\n".join(lines) + "\n", encoding="ascii")
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(copy, log=quiet)
        self.assertIn(path.name, str(caught.exception))

    def test_verify_detects_a_missing_part(self):
        result, copy = self.copy_build()
        (copy / result.zips[3].name).unlink()
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(copy, log=quiet)
        self.assertIn("missing part", str(caught.exception))

    def test_verify_detects_a_part_the_checksum_file_does_not_list(self):
        _result, copy = self.copy_build()
        sha = copy / "maschine-ArenaMapPort-0.1.0.sha256"
        sha.write_text("".join(sha.read_text(encoding="ascii").splitlines(True)[:3]), encoding="ascii")
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(copy, log=quiet)
        self.assertIn("not listed", str(caught.exception))

    def test_verify_detects_a_manifest_that_disagrees_with_the_content(self):
        """Rebuild the equator ZIP with one extra file the manifest does not list, then fix the checksum."""
        result, copy = self.copy_build()
        path = copy / result.zips[1].name
        with zipfile.ZipFile(path, "a") as archive:
            archive.writestr(f"{PLUGIN}/stowaway.txt", b"x")
        sha = copy / result.sha256_path.name
        lines = sha.read_text(encoding="ascii").splitlines()
        lines[1] = f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}"
        sha.write_text("\n".join(lines) + "\n", encoding="ascii")
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(copy, log=quiet)
        self.assertIn("stowaway", str(caught.exception))

    def test_verify_requires_exactly_one_checksum_file(self):
        _result, copy = self.copy_build()
        with self.assertRaises(release.VerificationError):
            release.verify_set(self.tmp, log=quiet)
        shutil.copy(copy / "maschine-ArenaMapPort-0.1.0.sha256", copy / "maschine-ArenaMapPort-0.2.0.sha256")
        with self.assertRaises(release.VerificationError):
            release.verify_set(copy, log=quiet)
        self.assertEqual(release.verify_set(copy, "0.1.0", log=quiet).version, "0.1.0")

    def test_old_single_zip_checksums_are_not_taken_for_a_release(self):
        _result, copy = self.copy_build()
        (copy / "maschine-ArenaMapPort-0.1.0-test-2026-10-11.zip.sha256").write_text("x")
        self.assertEqual(release.verify_set(copy, log=quiet).version, "0.1.0")


class DamagedSetTests(FakeRepoCase):
    """Verification of sets damaged in ways that keep the checksum file honest: the ZIP is rewritten and its
    SHA-256 line refreshed, so only the intended inconsistency is left for the verifier to find."""

    def setUp(self):
        super().setUp()
        self.result = self.build()
        self.copy = self.tmp / "copy"
        shutil.copytree(self.result.out_dir, self.copy)

    def fresh_copy(self):
        shutil.rmtree(self.copy)
        shutil.copytree(self.result.out_dir, self.copy)

    def zip_path(self, index):
        return self.copy / self.result.zips[index].name

    def refresh_checksum(self, index):
        sha = self.copy / self.result.sha256_path.name
        lines = sha.read_text(encoding="ascii").splitlines()
        lines[index] = f"{hashlib.sha256(self.zip_path(index).read_bytes()).hexdigest()}  {self.zip_path(index).name}"
        sha.write_text("\n".join(lines) + "\n", encoding="ascii", newline="\n")

    def rewrite_part(self, index, mutate=None, replace=None):
        """Rebuild one part after mutate(entries, manifest) (return value ignored); replace(manifest) may swap the
        whole manifest object for something else."""
        path = self.zip_path(index)
        manifest_name = f"{PLUGIN}/parts/{self.result.parts[index].id}.json"
        with zipfile.ZipFile(path) as archive:
            entries = {i.filename: archive.read(i) for i in archive.infolist()}
        manifest = json.loads(entries.pop(manifest_name))
        if mutate is not None:
            mutate(entries, manifest)
        if replace is not None:
            manifest = replace(manifest)
        entries[manifest_name] = (json.dumps(manifest, indent=2) + "\n").encode("utf-8")
        path.unlink()
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, data in entries.items():
                archive.writestr(name, data)
        self.refresh_checksum(index)

    def rename_entry(self, index, old, new):
        def mutate(entries, manifest):
            entries[new] = entries.pop(old)
            for entry in manifest["entries"]:
                if entry["path"] == old:
                    entry["path"] = new
        self.rewrite_part(index, mutate)

    def patch_entry(self, index, filename, *, method=None, flags=None):
        """Overwrite the compression method / general-purpose flags of one entry in both ZIP headers."""
        path = self.zip_path(index)
        with zipfile.ZipFile(path) as archive:
            local = archive.getinfo(filename).header_offset
            position = archive.start_dir
        data = bytearray(path.read_bytes())
        for offset, value in ((6, flags), (8, method)):  # local file header: flags at 6, method at 8
            if value is not None:
                struct.pack_into("<H", data, local + offset, value)
        while data[position:position + 4] == b"PK\x01\x02":
            name_length, extra_length, comment_length = struct.unpack_from("<HHH", data, position + 28)
            if bytes(data[position + 46:position + 46 + name_length]) == filename.encode("ascii"):
                for offset, value in ((8, flags), (10, method)):  # central directory record: flags 8, method 10
                    if value is not None:
                        struct.pack_into("<H", data, position + offset, value)
                break
            position += 46 + name_length + extra_length + comment_length
        else:
            self.fail(f"no central directory record for {filename}")
        path.write_bytes(bytes(data))
        self.refresh_checksum(index)

    def write_sha(self, text):
        (self.copy / self.result.sha256_path.name).write_bytes(text.encode("ascii"))

    def assert_rejected(self, fragment, **kwargs):
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(self.copy, log=quiet, **kwargs)
        self.assertIn(fragment, str(caught.exception))

    # --- the ZIP itself ---------------------------------------------------------------------------------------

    def test_an_unsupported_compression_method_is_a_verification_error_not_a_traceback(self):
        name = f"{SERVER}/db/arena_equator/base.json"
        self.patch_entry(1, name, method=99)
        self.assert_rejected("compression method 99")
        code, _out, err = self.cli("--verify-only", str(self.copy))
        self.assertEqual(code, 1)
        self.assertIn("ERROR", err)
        self.assertNotIn("Traceback", err)

    def test_every_entry_must_be_stored_or_deflated(self):
        self.patch_entry(1, f"{SERVER}/db/arena_equator/base.json", method=zipfile.ZIP_BZIP2)
        self.assert_rejected("only STORED and DEFLATED")

    def test_decompression_failures_surface_as_verification_errors(self):
        for error in (NotImplementedError("That compression method is not supported"), EOFError("truncated"),
                      RuntimeError("damaged stream")):
            with self.subTest(error=type(error).__name__):
                with patch.object(zipfile.ZipFile, "open", side_effect=error):
                    self.assert_rejected(str(error))

    def test_encrypted_and_unsafe_entries_are_refused(self):
        self.patch_entry(1, f"{SERVER}/db/arena_equator/base.json", flags=1)
        self.assert_rejected("encrypted")
        self.fresh_copy()
        with zipfile.ZipFile(self.zip_path(1), "a") as archive:
            archive.writestr("somewhere/", b"")
        self.refresh_checksum(1)
        self.assert_rejected("unsafe path")

    def test_duplicate_entry_names_inside_one_zip_are_refused(self):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")  # zipfile warns about the duplicate on purpose
            with zipfile.ZipFile(self.zip_path(1), "a") as archive:
                archive.writestr(f"{SERVER}/db/arena_equator/base.json", b"again")
        self.refresh_checksum(1)
        self.assert_rejected("duplicate entry names")

    def test_exactly_one_part_manifest_is_required(self):
        self.rewrite_part(1, lambda entries, manifest: entries.update({f"{PLUGIN}/parts/extra.json": b"{}"}))
        self.assert_rejected("expected exactly one")

    # --- the part manifest ------------------------------------------------------------------------------------

    def test_inconsistent_manifests_are_refused(self):
        cases = (
            ("unsupported manifest schema", lambda e, m: m.update(schema=2)),
            ("does not match its part id", lambda e, m: m["part"].update(id="bay5")),
            ("manifest names", lambda e, m: m["part"].update(zip="other.zip")),
            ("part list is inconsistent", lambda e, m: m["parts"].pop()),
            ("part list is inconsistent", lambda e, m: m["part"].update(index="two")),
            ("part list is inconsistent", lambda e, m: m["part"].update(index=0)),
            ("twice", lambda e, m: m["entries"].append(dict(m["entries"][0]))),
            ("unknown origin", lambda e, m: m["entries"][0].update(origin="elsewhere")),
            ("manifest and ZIP entries differ", lambda e, m: m["entries"].pop(0)),
            ("size or SHA-256 mismatch", lambda e, m: m["entries"][0].update(sha256="0" * 64)),
            ("size or SHA-256 mismatch", lambda e, m: m["entries"][0].update(size=m["entries"][0]["size"] + 1)),
            ("dist file count or raw bytes", lambda e, m: m["parts"][1].update(dist_files=99)),
            ("missing ['version']", lambda e, m: m.pop("version")),
            ("missing ['source_commit']", lambda e, m: m.pop("source_commit")),
            ("missing ['entries']", lambda e, m: m.pop("entries")),
            ("must be strings", lambda e, m: m.update(version=1)),
            ("malformed manifest", lambda e, m: m.update(entries=[5])),
        )
        for number, (fragment, mutate) in enumerate(cases):
            with self.subTest(number=number, fragment=fragment):
                self.fresh_copy()
                self.rewrite_part(1, mutate)
                self.assert_rejected(fragment)

    def test_a_manifest_that_is_not_an_object_is_refused(self):
        self.rewrite_part(1, replace=lambda manifest: [manifest])
        self.assert_rejected("not a JSON object")

    def test_manifests_that_differ_between_parts_are_refused(self):
        for key, value in (("version", "0.9.9"), ("source_commit", "b" * 40)):
            with self.subTest(key=key):
                self.fresh_copy()
                self.rewrite_part(1, lambda e, m: m.update({key: value}))
                self.assert_rejected(f"manifest field '{key}' differs from part 1")

    def test_a_path_in_two_parts_is_refused(self):
        self.rename_entry(1, f"{PLUGIN}/acoustics/arena_equator/room.assets_9.wav",
                          f"{PLUGIN}/acoustics/arena_bay5/room.assets_9.wav")
        self.assert_rejected("appears in both")

    def test_a_part_list_that_names_a_part_outside_the_set_is_refused(self):
        def add_ghost(entries, manifest):
            manifest["part"]["of"] = 5
            manifest["parts"].append({"index": 5, "id": "ghost", "zip": "maschine-ArenaMapPort-0.1.0-part5of5-ghost.zip",
                                      "dist_files": 0, "raw_bytes": 0})
        self.rewrite_part(1, add_ghost)
        self.assert_rejected("part id/index/count disagree")

    # --- the checksum file ------------------------------------------------------------------------------------

    def test_malformed_checksum_files_are_refused(self):
        lines = (self.copy / self.result.sha256_path.name).read_text(encoding="ascii").splitlines()
        digest, name = lines[0].split("  ")
        cases = {
            "malformed line": "\n".join([f"{digest.upper()}  {name}"] + lines[1:]) + "\n",
            "malformed line (one space)": "\n".join([f"{digest} {name}"] + lines[1:]) + "\n",
            "missing final newline": "\n".join(lines),
            "empty": "",
            "is not part 2 of 4": "\n".join([lines[0], lines[2], lines[1], lines[3]]) + "\n",
            "duplicate part": "\n".join(lines + [lines[0]]) + "\n",
        }
        for label, text in cases.items():
            with self.subTest(label=label):
                self.write_sha(text)
                self.assert_rejected(label.split(" (")[0])

    # --- size gate and partition invariant --------------------------------------------------------------------

    def test_verify_enforces_the_size_gate(self):
        largest = max(p.stat().st_size for p in self.copy.glob("*.zip"))
        self.assert_rejected("not below", size_limit=largest)
        self.assertEqual(len(release.verify_set(self.copy, size_limit=largest + 1, log=quiet).parts), 4)
        self.assertEqual(release.SIZE_LIMIT, 2 ** 31)  # the default gate is the real limit

    def test_map_data_in_a_foreign_part_is_refused(self):
        self.rename_entry(1, f"{PLUGIN}/acoustics/arena_equator/room.assets_9.wav", f"{SERVER}/db/arena_airpit/other.json")
        self.assert_rejected("belong to part 'airpit'")
        self.fresh_copy()
        self.rename_entry(3, f"{PLUGIN}/acoustics/arena_airpit/room.assets_9.wav",
                          f"{PLUGIN}/bundles/arena_equator_extra.bundle")
        self.assert_rejected("belong to part 'equator'")

    def test_a_database_entry_that_is_not_with_its_bundles_is_refused(self):
        base = f"{SERVER}/db/arena_bay5/base.json"
        self.rename_entry(2, base, f"{PLUGIN}/acoustics/arena_bay5/moved.wav")
        self.rename_entry(1, f"{PLUGIN}/acoustics/arena_equator/room.assets_9.wav", base)
        self.assert_rejected(base)

    def test_a_map_part_without_all_three_bundles_is_refused(self):
        self.rename_entry(3, f"{PLUGIN}/bundles/arena_airpit_navmesh.bundle", f"{PLUGIN}/bundles/arena_airpit_navmesh.old")
        self.assert_rejected("arena_airpit_navmesh.bundle")

    def test_a_map_part_without_its_key_file_is_refused(self):
        self.rename_entry(3, f"{PLUGIN}/keys/arena_airpit.json", f"{PLUGIN}/keys/arena_airpit.json.old")
        self.assert_rejected("keys/arena_airpit.json")

    def test_a_key_file_that_is_not_with_its_bundles_is_refused(self):
        key_file = f"{PLUGIN}/keys/arena_bay5.json"
        self.rename_entry(2, key_file, f"{PLUGIN}/acoustics/arena_bay5/moved.wav")
        self.rename_entry(1, f"{PLUGIN}/acoustics/arena_equator/room.assets_9.wav", key_file)
        self.assert_rejected(key_file)

    def test_a_plaintext_bundle_inside_a_finished_part_is_refused(self):
        name = f"{PLUGIN}/bundles/arena_bay5_scenes.bundle"

        def plaintext(entries, manifest):
            entries[name] = b"UnityFS\0" + b"\x01" * 500
            for entry in manifest["entries"]:
                if entry["path"] == name:
                    entry["size"] = len(entries[name])
                    entry["sha256"] = hashlib.sha256(entries[name]).hexdigest()
            manifest["parts"][2]["raw_bytes"] = sum(e["size"] for e in manifest["entries"] if e["origin"] == "dist")
        self.rewrite_part(2, plaintext)
        self.assert_rejected(f"plaintext bundle {name}")

    def test_the_core_part_must_come_first(self):
        verified = release.verify_set(self.copy, log=quiet)
        release.check_partition(verified.parts)
        with self.assertRaises(release.VerificationError):
            release.check_partition(list(reversed(verified.parts)))
        with self.assertRaises(release.VerificationError):
            release.check_partition([verified.parts[0], verified.parts[0]])


class NoticesTests(FakeRepoCase):
    def test_a_third_distinct_licence_block_fails_the_gate(self):
        extra = "## New\n\n```\n" + MIT.format(holder="someone else") + "```\n\n"
        (self.repo / "THIRD-PARTY-NOTICES.md").write_text(NOTICES_MD + extra, encoding="utf-8")
        with self.assertRaises(release.PackagingError) as caught:
            release.notices_text(self.repo)
        self.assertIn("exactly", str(caught.exception))
        with self.assertRaises(release.PackagingError):
            release.generated_core_entries(self.spec, self.partition(), self.repo)

    def test_missing_or_reordered_blocks_fail_the_gate(self):
        blocks = [MIT.format(holder="LennoxP90"), MIT.format(holder="danauraborealis")]
        (self.repo / "THIRD-PARTY-NOTICES.md").write_text(
            "".join(f"```\n{block}```\n\n" for block in blocks), encoding="utf-8")
        with self.assertRaises(release.PackagingError):
            release.notices_text(self.repo)
        (self.repo / "THIRD-PARTY-NOTICES.md").write_text("no licence blocks\n", encoding="utf-8")
        with self.assertRaises(release.PackagingError):
            release.notices_text(self.repo)

    def test_the_two_expected_blocks_pass_and_are_deduplicated(self):
        text = release.notices_text(self.repo)
        self.assertEqual(text.count("MIT License"), 2)


class CommandLineTests(FakeRepoCase):
    def test_plan_prints_the_partition_and_writes_nothing(self):
        before = sorted(p.relative_to(self.repo).as_posix() for p in self.repo.rglob("*"))
        code, out, err = self.cli("--plan")
        self.assertEqual(code, 0, err)
        for token in ("part 1 of 4  core", "part 2 of 4  equator", "part 3 of 4  bay5", "part 4 of 4  airpit",
                      "estimated zip", "total:"):
            self.assertIn(token, out)
        self.assertEqual(sorted(p.relative_to(self.repo).as_posix() for p in self.repo.rglob("*")), before)

    def test_plan_with_an_unclassified_file_fails(self):
        (self.dist / PLUGIN / "bundles" / "arena_airpit2_scenes.bundle").write_bytes(b"x")
        code, _out, err = self.cli("--plan")
        self.assertEqual(code, 1)
        self.assertIn("unclassified", err)

    def test_build_verify_and_extract_check_end_to_end(self):
        out = self.tmp / "releases"
        code, stdout, err = self.cli("--allow-dirty", "--out", str(out), "--extract-check", str(self.tmp / "s"))
        self.assertEqual(code, 0, err)
        self.assertIn("RELEASE_ZIPS_BUILT", stdout)
        self.assertIn("extract-check OK", stdout)
        code, stdout, err = self.cli("--verify-only", str(out))
        self.assertEqual(code, 0, err)
        self.assertIn("RELEASE_ZIPS_VERIFIED 0.1.0 (4 parts)", stdout)
        code, stdout, err = self.cli("--verify-only", str(out), "--extract-check", str(self.tmp / "s2"))
        self.assertEqual(code, 0, err)
        self.assertIn("extract-check OK", stdout)

    def test_default_output_directory_is_work_releases_version(self):
        code, _out, err = self.cli("--allow-dirty")
        self.assertEqual(code, 0, err)
        self.assertTrue((self.repo / "work/releases/0.1.0/maschine-ArenaMapPort-0.1.0.sha256").is_file())

    def test_verify_only_fails_on_a_tampered_part(self):
        out = self.tmp / "releases"
        self.assertEqual(self.cli("--allow-dirty", "--out", str(out))[0], 0)
        zip_path = out / "maschine-ArenaMapPort-0.1.0-part3of4-bay5.zip"
        data = bytearray(zip_path.read_bytes())
        data[len(data) // 2] ^= 0x01
        zip_path.write_bytes(bytes(data))
        code, _out, err = self.cli("--verify-only", str(out))
        self.assertEqual(code, 1)
        self.assertIn("ERROR", err)

    def test_dirty_gate_blocks_a_build_outside_a_clean_repository(self):
        code, _out, err = self.cli("--out", str(self.tmp / "o"))
        self.assertEqual(code, 1)
        self.assertIn("--allow-dirty", err)
        self.assertFalse((self.tmp / "o").exists())

    def test_extract_check_scratch_is_validated_before_anything_is_built(self):
        out = self.tmp / "releases"
        busy = self.tmp / "busy"
        busy.mkdir()
        (busy / "old.txt").write_text("x")
        not_a_directory = self.tmp / "plain-file"
        not_a_directory.write_text("x")
        for label, scratch in (("non-empty", busy), ("a file", not_a_directory), ("inside dist", self.dist / "scratch")):
            with self.subTest(label=label):
                code, stdout, err = self.cli("--allow-dirty", "--out", str(out), "--extract-check", str(scratch))
                self.assertEqual(code, 1)
                self.assertIn("scratch", err)
                self.assertNotIn("RELEASE_ZIPS_BUILT", stdout)
                self.assertFalse(out.exists(), "a complete set was written before the scratch directory was checked")
        self.assertEqual([p.name for p in busy.iterdir()], ["old.txt"])
        built = self.tmp / "built"
        self.assertEqual(self.cli("--allow-dirty", "--out", str(built))[0], 0)
        code, stdout, err = self.cli("--verify-only", str(built), "--extract-check", str(busy))
        self.assertEqual(code, 1)
        self.assertNotIn("RELEASE_ZIPS_VERIFIED", stdout)
        self.assertIn("scratch", err)

    def test_environment_errors_are_reported_not_raised(self):
        blocker = self.tmp / "blocker"
        blocker.write_text("a file where the output directory should be created")
        code, _out, err = self.cli("--allow-dirty", "--out", str(blocker / "sub"))
        self.assertEqual(code, 1)
        self.assertIn("ERROR", err)
        code, _out, err = self.cli("--verify-only", str(self.tmp / "no-such-directory"))
        self.assertEqual(code, 1)
        self.assertIn("ERROR", err)

    def test_an_unlistable_dist_directory_fails_the_plan(self):
        real_scandir = os.scandir
        target = self.dist / PLUGIN / "water" / "arena_bay5"

        def flaky(path="."):
            if Path(path) == target:
                raise PermissionError(13, "Access is denied", str(path))
            return real_scandir(path)

        with patch.object(os, "scandir", flaky):
            code, _out, err = self.cli("--plan")
        self.assertEqual(code, 1)
        self.assertIn("cannot list", err)

    def test_contradicting_modes_are_usage_errors(self):
        for argv in (["--plan", "--verify-only", "x"], ["--plan", "--extract-check", "x"], ["--plan", "--force"],
                     ["--verify-only", "x", "--force"]):
            with self.subTest(argv=argv), redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as caught:
                    release.parse_args(argv)
                self.assertEqual(caught.exception.code, 2)


SINGLE_ZIP = "maschine-ArenaMapPort-0.1.0.zip"
SHA_NAME = "maschine-ArenaMapPort-0.1.0.sha256"
FULL_MANIFEST = f"{PLUGIN}/parts/full.json"
GENERATED_TRIO = (f"{PLUGIN}/README.txt", f"{PLUGIN}/THIRD-PARTY-NOTICES.txt", f"{SERVER}/THIRD-PARTY-NOTICES.txt")


def read_zip(path):
    with zipfile.ZipFile(path) as archive:
        return {i.filename: archive.read(i) for i in archive.infolist()}


def snapshot(directory):
    return {p.name: p.read_bytes() for p in directory.iterdir()}


class SingleLayoutTests(FakeRepoCase):
    """--single: ONE ZIP with every dist file, the generated README / notices and parts/full.json."""

    def setUp(self):
        super().setUp()
        self.pin_released_maps()

    def build_single(self, out_name="out", **kwargs):
        return self.build(out_name, single=True, **kwargs)

    # --- what is built ----------------------------------------------------------------------------------------

    def test_partition_single_is_one_sorted_plan_over_every_file(self):
        parts = release.partition_single(self.scan(), self.spec)
        self.assertEqual(len(parts), 1)
        plan = parts[0]
        self.assertEqual((plan.index, plan.count, plan.id, plan.zip_name), (1, 1, "full", SINGLE_ZIP))
        self.assertEqual([f.rel for f in plan.files], sorted(self.files))
        self.assertEqual(plan.raw_bytes, sum(len(data) for data in self.files.values()))
        self.assertEqual(release.single_zip_name("0.1.0"), SINGLE_ZIP)
        self.assertTrue(release.is_single_layout(parts))
        self.assertFalse(release.is_single_layout(self.partition()))

    def test_build_writes_one_zip_and_a_one_line_sha256(self):
        result = self.build_single()
        self.assertEqual(result.layout, "single")
        self.assertEqual([p.name for p in result.zips], [SINGLE_ZIP])
        self.assertEqual(sorted(p.name for p in result.out_dir.iterdir()), sorted([SINGLE_ZIP, SHA_NAME]))
        self.assertEqual(result.sha256_path.name, SHA_NAME)
        digest = hashlib.sha256((result.out_dir / SINGLE_ZIP).read_bytes()).hexdigest()
        text = result.sha256_path.read_bytes().decode("ascii")
        self.assertEqual(text, f"{digest}  {SINGLE_ZIP}\n")  # one line, LF, same format as the multi-part file
        self.assertNotIn("\r", text)
        self.assertEqual(len(text.splitlines()), 1)
        self.assertEqual(result.digests, [digest])
        self.assertEqual(result.sizes, [(result.out_dir / SINGLE_ZIP).stat().st_size])

    def test_the_zip_holds_every_dist_file_then_the_generated_entries_then_the_manifest(self):
        result = self.build_single()
        with zipfile.ZipFile(result.zips[0]) as archive:
            names = archive.namelist()
        self.assertEqual(len(names), len(set(names)))
        self.assertEqual(names[:len(self.files)], sorted(self.files))
        self.assertEqual(names[len(self.files):-1], sorted(GENERATED_TRIO))
        self.assertEqual(names[-1], FULL_MANIFEST, "the manifest needs every hash, so it is the last entry")
        self.assertEqual(set(names), set(self.files) | set(GENERATED_TRIO) | {FULL_MANIFEST})

    def test_manifest_names_the_one_part_full_1_of_1(self):
        result = self.build_single()
        entries = read_zip(result.zips[0])
        manifest = json.loads(entries[FULL_MANIFEST])
        self.assertEqual(manifest["schema"], 1)
        self.assertEqual(manifest["version"], "0.1.0")
        self.assertEqual(manifest["source_commit"], COMMIT)
        self.assertEqual(manifest["part"], {"index": 1, "of": 1, "id": "full", "zip": SINGLE_ZIP})
        self.assertEqual(manifest["parts"], [{"index": 1, "id": "full", "zip": SINGLE_ZIP,
                                              "dist_files": len(self.files),
                                              "raw_bytes": sum(len(d) for d in self.files.values())}])
        self.assertIn("extract this ZIP into the SPT root", manifest["install"])
        self.assertNotIn("ALL", manifest["install"])
        own = {e["path"]: e for e in manifest["entries"]}
        self.assertEqual(set(own), set(entries) - {FULL_MANIFEST})
        for path, entry in own.items():
            self.assertEqual(entry["size"], len(entries[path]))
            self.assertEqual(entry["sha256"], hashlib.sha256(entries[path]).hexdigest())
            self.assertEqual(entry["origin"], "dist" if path in self.files else "generated")
        self.assertEqual({p for p, e in own.items() if e["origin"] == "generated"}, set(GENERATED_TRIO))

    def test_readme_says_it_is_one_zip_to_extract_into_the_spt_root(self):
        entries = read_zip(self.build_single().zips[0])
        readme = entries[f"{PLUGIN}/README.txt"].decode("utf-8")
        self.assertIn("ONE ZIP: " + SINGLE_ZIP, readme)
        self.assertIn("Extract the ZIP into your SPT installation directory", readme)
        self.assertIn("BepInEx and SPT_Runtime", readme)
        self.assertIn("close", readme.lower())
        self.assertIn("Equator, Bay5, AirPit", readme)
        # the Verification paragraph describes the single checksum line, not "every part"
        self.assertIn(f"Verification: {SHA_NAME} lists the SHA-256 of the ZIP", readme)
        self.assertIn("parts/full.json records", readme)
        for multi_part_wording in ("EVERY part", "ZIP parts", "ALL of them", "part1of", "of every part", "any order",
                                   "core part"):
            self.assertNotIn(multi_part_wording, readme)

    def test_the_multi_part_readme_keeps_its_wording(self):
        parts_readme = read_zip(self.build().zips[0])[f"{PLUGIN}/README.txt"].decode("utf-8")
        for wording in ("This release comes in 4 ZIP parts. Install ALL of them", "Extract EVERY part",
                        "The parts do not overlap and can be extracted in any order",
                        f"Verification: {SHA_NAME} lists the SHA-256 of every part",
                        "parts/ records, per part,"):
            self.assertIn(wording, parts_readme)
        self.assertNotIn("ONE ZIP", parts_readme)

    def test_notices_equal_the_multi_part_core_entries(self):
        single = read_zip(self.build_single("single").zips[0])
        core = read_zip(self.build("parts").zips[0])
        for name in GENERATED_TRIO[1:]:
            self.assertEqual(single[name], core[name])
        self.assertEqual(single[GENERATED_TRIO[1]].decode("utf-8").count("MIT License"), 2)

    def test_compression_decisions_and_dist_bytes_match_the_multi_part_layout(self):
        single = self.build_single("single")
        parts = self.build("parts")
        with zipfile.ZipFile(single.zips[0]) as archive:
            in_single = {i.filename: (i.compress_type, i.CRC, i.file_size, i.compress_size, archive.read(i))
                         for i in archive.infolist()}
        in_parts = {}
        for path in parts.zips:
            with zipfile.ZipFile(path) as archive:
                in_parts.update({i.filename: (i.compress_type, i.CRC, i.file_size, i.compress_size, archive.read(i))
                                 for i in archive.infolist() if i.filename in self.files})
        self.assertEqual({n: v for n, v in in_single.items() if n in self.files}, in_parts)
        self.assertEqual(in_single[f"{PLUGIN}/bundles/arena_equator_scenes.bundle"][0], zipfile.ZIP_STORED)
        self.assertEqual(in_single[f"{SERVER}/banners/arena_equator.jpg"][0], zipfile.ZIP_STORED)
        self.assertEqual(in_single[f"{SERVER}/db/arena_equator/base.json"][0], zipfile.ZIP_DEFLATED)

    def test_timestamps_and_attributes_are_fixed_and_zip64_stays_on(self):
        calls = []

        class Spy(zipfile.ZipFile):
            def __init__(self, file, mode="r", *args, **kwargs):
                calls.append((mode, kwargs.get("allowZip64")))
                super().__init__(file, mode, *args, **kwargs)

        with patch.object(zipfile, "ZipFile", Spy):
            result = self.build_single()
        self.assertIn(("x", True), calls, "the single ZIP must be written with allowZip64=True")
        with zipfile.ZipFile(result.zips[0]) as archive:
            for info in archive.infolist():
                self.assertEqual(info.date_time, (1980, 1, 1, 0, 0, 0), info.filename)
                self.assertEqual(info.external_attr >> 16, 0o100644)

    def test_single_build_is_reproducible(self):
        first = self.build_single("one")
        second = self.build_single("two")
        self.assertEqual(first.zips[0].read_bytes(), second.zips[0].read_bytes())
        self.assertEqual(first.sha256_path.read_bytes(), second.sha256_path.read_bytes())
        self.assertEqual(first.digests, second.digests)

    # --- no size gate ------------------------------------------------------------------------------------------

    def test_the_two_gibibyte_gate_does_not_apply_to_the_single_layout(self):
        with self.assertRaises(release.PartTooLarge):
            self.build("parts", size_limit=300)  # the multi-part layout still refuses
        result = self.build_single("single", size_limit=300)  # ... the one ZIP is far above 300 B and is accepted
        self.assertGreater(result.sizes[0], 300)
        verified = release.verify_set(result.out_dir, size_limit=300, log=quiet)
        self.assertEqual(verified.layout, "single")
        self.assertEqual(release.verify_set(result.out_dir, size_limit=1, log=quiet).layout, "single")
        self.assertEqual(release.SIZE_LIMIT, 2 ** 31)

    # --- strict classification is unchanged ---------------------------------------------------------------------

    def test_a_stray_or_parked_file_is_still_a_hard_error_in_single_mode(self):
        for name in (f"{PLUGIN}/extra.txt", f"{PLUGIN}/maschine-ArenaMapPort.Client.pdb", f"{SERVER}/Thumbs.db",
                     f"{PLUGIN}/README.txt", FULL_MANIFEST, f"{PLUGIN}/parts/core.json",
                     f"{PLUGIN}/bundles/arena_airpit2_scenes.bundle", f"{PLUGIN}/ai/airpit2.ai.json",
                     f"{PLUGIN}/acoustics/arena_bay5/Thumbs.db", f"{PLUGIN}/water/arena_bay5/water.json~",
                     f"{SERVER}/db/arena_prison/base.json"):
            with self.subTest(name=name):
                path = self.dist / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x")
                try:
                    with self.assertRaises(release.ClassificationError) as caught:
                        release.partition_single(self.scan(), self.spec)
                    self.assertIn(name, str(caught.exception))
                    with self.assertRaises(release.ClassificationError):
                        self.build_single("rejected")
                    self.assertFalse((self.tmp / "rejected").exists(), "a rejected dist wrote output")
                    code, _out, err = self.cli("--plan", "--single")
                    self.assertEqual(code, 1)
                    self.assertIn("unclassified", err)
                finally:
                    path.unlink()

    def test_an_incomplete_dist_is_still_rejected_in_single_mode(self):
        for name in (f"{SERVER}/db/arena_bay5/base.json", f"{PLUGIN}/bundles/arena_equator_navmesh.bundle",
                     f"{PLUGIN}/ai/airpit.ai.json", f"{PLUGIN}/maschine-ArenaMapPort.Client.dll",
                     f"{PLUGIN}/loadscreens/arena_equator_1.jpg"):
            with self.subTest(name=name):
                data = (self.dist / name).read_bytes()
                (self.dist / name).unlink()
                try:
                    with self.assertRaises(release.PackagingError) as caught:
                        self.build_single("incomplete")
                    self.assertIn("incomplete dist", str(caught.exception))
                    self.assertIn(name, str(caught.exception))
                finally:
                    (self.dist / name).write_bytes(data)

    def test_full_is_a_reserved_map_stem(self):
        with self.assertRaises(release.PackagingError) as caught:
            release.load_spec(self.repo, maps=(("arena_equator", "Equator"), ("arena_full", "Full")))
        self.assertIn("'full'", str(caught.exception))

    # --- verification: layout auto-detection, extract-check -------------------------------------------------------

    def test_verify_set_detects_the_single_layout(self):
        result = self.build_single()
        logged = []
        verified = release.verify_set(result.out_dir, log=logged.append)
        self.assertEqual(verified.layout, "single")
        self.assertEqual(verified.version, "0.1.0")
        self.assertEqual([(p.index, p.id, p.zip_name) for p in verified.parts], [(1, "full", SINGLE_ZIP)])
        self.assertEqual(verified.parts[0].sha256, result.digests[0])
        self.assertEqual(verified.parts[0].size, result.sizes[0])
        self.assertEqual(logged, [f"verified single ZIP: {SINGLE_ZIP}"])
        self.assertEqual(release.verify_set(self.build("parts").out_dir, log=quiet).layout, "parts")

    def test_extract_overlay_equals_dist_byte_for_byte(self):
        result = self.build_single()
        scratch = self.tmp / "overlay"
        with zipfile.ZipFile(result.zips[0]) as archive:
            archive.extractall(scratch)  # independent of the module's own extractor
        extracted = {p.relative_to(scratch).as_posix(): p.read_bytes() for p in scratch.rglob("*") if p.is_file()}
        generated = {*GENERATED_TRIO, FULL_MANIFEST}
        self.assertTrue(generated <= set(extracted))
        self.assertEqual({n: d for n, d in extracted.items() if n not in generated}, self.files)

    def test_extract_check_works_for_the_single_layout(self):
        result = self.build_single()
        verified = release.verify_set(result.out_dir, log=quiet)
        logged = []
        count = release.extract_check(verified, self.dist, self.tmp / "scratch", log=logged.append)
        self.assertEqual(count, len(self.files))
        self.assertEqual(logged, [f"extract-check OK: {len(self.files)} files identical to dist, "
                                  "4 generated entries ignored"])
        victim = self.dist / f"{SERVER}/db/arena_bay5/base.json"  # a changed dist byte is caught
        victim.write_bytes(victim.read_bytes()[:-1] + b"!")
        with self.assertRaises(release.VerificationError) as caught:
            release.extract_check(verified, self.dist, self.tmp / "scratch2", log=quiet)
        self.assertIn("db/arena_bay5/base.json", str(caught.exception))
        (self.tmp / "busy").mkdir()
        (self.tmp / "busy" / "old.txt").write_text("x")
        with self.assertRaises(release.PackagingError):
            release.extract_check(verified, self.dist, self.tmp / "busy", log=quiet)

    def test_verify_detects_a_tampered_byte(self):
        result = self.build_single()
        copy = self.tmp / "copy"
        shutil.copytree(result.out_dir, copy)
        path = copy / SINGLE_ZIP
        data = bytearray(path.read_bytes())
        needle = self.files[f"{PLUGIN}/bundles/arena_equator_scenes.bundle"][100:140]
        offset = bytes(data).find(needle)
        self.assertGreater(offset, 0)
        data[offset] ^= 0xFF
        path.write_bytes(bytes(data))
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(copy, log=quiet)
        self.assertIn("SHA-256 mismatch", str(caught.exception))
        (copy / SHA_NAME).write_text(f"{hashlib.sha256(bytes(data)).hexdigest()}  {SINGLE_ZIP}\n", encoding="ascii",
                                     newline="\n")  # an honest checksum still cannot hide the damaged entry
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(copy, log=quiet)
        self.assertIn(SINGLE_ZIP, str(caught.exception))

    def test_a_missing_single_zip_is_reported(self):
        result = self.build_single()
        (result.out_dir / SINGLE_ZIP).unlink()
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(result.out_dir, log=quiet)
        self.assertIn("missing part", str(caught.exception))

    def test_zips_of_the_other_layout_are_an_error_in_both_directions(self):
        parts = self.build("parts")
        single = self.build_single("single")
        mixed = self.tmp / "mixed-single"
        shutil.copytree(single.out_dir, mixed)
        shutil.copy(parts.zips[0], mixed)
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(mixed, log=quiet)
        self.assertIn("not listed", str(caught.exception))
        self.assertIn("part1of4-core.zip", str(caught.exception))
        mixed = self.tmp / "mixed-parts"
        shutil.copytree(parts.out_dir, mixed)
        shutil.copy(single.zips[0], mixed)
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(mixed, log=quiet)
        self.assertIn("not listed", str(caught.exception))
        self.assertIn(SINGLE_ZIP, str(caught.exception))

    # --- --force and the other layout ---------------------------------------------------------------------------

    def test_an_existing_multi_part_release_blocks_a_single_build_without_force(self):
        parts = self.build()
        before = snapshot(parts.out_dir)
        with self.assertRaises(release.PackagingError) as caught:
            self.build_single()
        self.assertIn("--force", str(caught.exception))
        self.assertIn("part1of4-core.zip", str(caught.exception))
        self.assertEqual(snapshot(parts.out_dir), before)
        self.assertEqual(release.verify_set(parts.out_dir, log=quiet).layout, "parts")

    def test_an_existing_single_release_blocks_a_multi_part_build_without_force(self):
        single = self.build_single()
        before = snapshot(single.out_dir)
        with self.assertRaises(release.PackagingError) as caught:
            self.build()
        self.assertIn("--force", str(caught.exception))
        self.assertIn(SINGLE_ZIP, str(caught.exception))
        self.assertEqual(snapshot(single.out_dir), before)

    def test_an_existing_single_release_blocks_a_single_rebuild_without_force(self):
        first = self.build_single()
        before = snapshot(first.out_dir)
        with self.assertRaises(release.PackagingError) as caught:
            self.build_single()
        self.assertIn("--force", str(caught.exception))
        self.assertEqual(snapshot(first.out_dir), before)
        second = self.build_single(force=True)
        self.assertEqual(snapshot(second.out_dir), before)  # reproducible, so the forced rebuild is identical

    def test_force_replaces_a_multi_part_release_with_the_single_zip(self):
        parts = self.build()
        single = self.build_single(force=True)
        self.assertEqual(sorted(p.name for p in single.out_dir.iterdir()), sorted([SINGLE_ZIP, SHA_NAME]))
        verified = release.verify_set(single.out_dir, log=quiet)
        self.assertEqual((verified.layout, [p.id for p in verified.parts]), ("single", ["full"]))
        self.assertEqual(parts.out_dir, single.out_dir)

    def test_force_replaces_the_single_zip_with_a_multi_part_release(self):
        single = self.build_single()
        parts = self.build(force=True)
        self.assertEqual(sorted(p.name for p in parts.out_dir.iterdir()),
                         sorted([p.name for p in parts.zips] + [SHA_NAME]))
        self.assertFalse((single.out_dir / SINGLE_ZIP).exists())
        verified = release.verify_set(parts.out_dir, log=quiet)
        self.assertEqual((verified.layout, [p.id for p in verified.parts]), ("parts", ["core", "equator", "bay5", "airpit"]))

    def test_other_versions_in_the_same_directory_are_left_alone(self):
        first = self.build_single()
        spec2 = release.load_spec(self.repo, "0.2.0", maps=MAPS)
        release.build_release(spec2, self.dist, first.out_dir, self.repo, commit=COMMIT, log=quiet, single=True)
        release.build_release(spec2, self.dist, first.out_dir, self.repo, force=True, commit=COMMIT, log=quiet)
        self.assertEqual(sorted(p.name for p in first.out_dir.glob("*.zip") if "0.1.0" in p.name), [SINGLE_ZIP])
        self.assertEqual(release.verify_set(first.out_dir, "0.1.0", log=quiet).layout, "single")
        self.assertEqual(release.verify_set(first.out_dir, "0.2.0", log=quiet).layout, "parts")

    def test_stale_partials_of_either_layout_block_a_build_without_force_and_go_with_it(self):
        out = self.tmp / "out"
        out.mkdir()
        for stale_name, build_single in (("maschine-ArenaMapPort-0.1.0.zip.partial", False),
                                         ("maschine-ArenaMapPort-0.1.0-part2of4-equator.zip.partial", True),
                                         ("maschine-ArenaMapPort-0.1.0-part2of5-equator.zip.partial", True)):
            with self.subTest(stale=stale_name, single=build_single):
                stale = out / stale_name
                stale.write_bytes(b"crashed")
                with self.assertRaises(release.PackagingError):
                    self.build(single=build_single)
                self.assertTrue(stale.exists())
                result = self.build(single=build_single, force=True)
                self.assertFalse(stale.exists())
                self.assertEqual(release.verify_set(out, log=quiet).layout, result.layout)
                for leftover in out.iterdir():
                    leftover.unlink()

    def test_a_failed_forced_single_build_leaves_the_previous_set_intact(self):
        for previous in ("parts", "single"):
            with self.subTest(previous=previous):
                out_name = f"out-{previous}"
                first = self.build(out_name, single=(previous == "single"))
                before = snapshot(first.out_dir)
                victim = f"{SERVER}/db/arena_bay5/base.json"
                files = [release.DistFile(f.rel, f.path, f.size + 1) if f.rel == victim else f for f in self.scan()]
                with patch.object(release, "scan_dist", return_value=files):
                    with self.assertRaises(release.PackagingError):
                        self.build(out_name, force=True, single=True)
                self.assertEqual(snapshot(first.out_dir), before, "changed-while-packaging left files behind")
                with patch.object(release, "write_part", side_effect=KeyboardInterrupt):
                    with self.assertRaises(KeyboardInterrupt):
                        self.build(out_name, force=True, single=True)
                self.assertEqual(snapshot(first.out_dir), before, "an interrupted build left files behind")
                with patch.object(release, "verify_zip", side_effect=release.VerificationError("boom")):
                    with self.assertRaises(release.VerificationError):
                        self.build(out_name, force=True, single=True)
                self.assertEqual(snapshot(first.out_dir), before)
                self.assertEqual(release.verify_set(first.out_dir, log=quiet).layout, previous)

    def test_a_failed_first_single_build_leaves_no_files(self):
        victim = f"{SERVER}/db/arena_bay5/base.json"
        files = [release.DistFile(f.rel, f.path, f.size + 1) if f.rel == victim else f for f in self.scan()]
        with patch.object(release, "scan_dist", return_value=files):
            with self.assertRaises(release.PackagingError) as caught:
                self.build_single()
        self.assertIn("changed while packaging", str(caught.exception))
        self.assertEqual(list((self.tmp / "out").iterdir()), [])

    def test_output_inside_dist_is_refused_in_single_mode(self):
        with self.assertRaises(release.PackagingError):
            release.build_release(self.spec, self.dist, self.dist / "out", self.repo, commit=COMMIT, log=quiet,
                                  single=True)

    # --- the plan -----------------------------------------------------------------------------------------------

    def test_estimate_and_plan_text_for_the_single_layout(self):
        files = self.scan()
        parts = release.partition_single(files, self.spec)
        decisions = {f.rel: release.decide_file(f) for f in files}
        estimates = release.estimate_parts(self.spec, parts, decisions,
                                           release.generated_core_entries(self.spec, parts, self.repo), COMMIT)
        self.assertEqual(len(estimates), 1)
        self.assertEqual(estimates[0].generated_count, 4)  # README, two notices, the manifest
        actual = self.build_single().sizes[0]
        self.assertLess(abs(estimates[0].estimated_zip - actual), 0.3 * actual + 1000)
        text = release.format_plan(self.spec, self.dist, COMMIT, estimates, decisions, None)
        for token in ("layout single (1 ZIP)", f"single ZIP  full  ->  {SINGLE_ZIP}", "size limit    none",
                      "(+4 generated)", f"total: {len(files)} dist files", "estimated single ZIP:"):
            self.assertIn(token, text)
        self.assertNotIn("part 1 of", text)
        self.assertNotIn("WARNING", text)
        # far beyond the old limit: still no warning, the estimate is printed in GiB
        big = release.DistFile(f"{PLUGIN}/bundles/arena_equator_huge.bundle", self.tmp / "none", 5_000_000_000)
        parts[0].files.append(big)
        decisions[big.rel] = release.Decision(zipfile.ZIP_STORED, 0.0, 5_000_000_000)
        estimates = release.estimate_parts(self.spec, parts, decisions,
                                           release.generated_core_entries(self.spec, parts, self.repo), COMMIT)
        text = release.format_plan(self.spec, self.dist, COMMIT, estimates, decisions, None)
        self.assertNotIn("WARNING", text)
        self.assertRegex(text, r"estimated single ZIP: 4\.\d\d GiB")

    def test_plan_prints_the_one_archive_and_writes_nothing(self):
        before = sorted(p.relative_to(self.repo).as_posix() for p in self.repo.rglob("*"))
        code, out, err = self.cli("--plan", "--single")
        self.assertEqual(code, 0, err)
        for token in ("layout single (1 ZIP)", f"single ZIP  full  ->  {SINGLE_ZIP}", "estimated zip", "total:",
                      "size limit    none", "estimated single ZIP:"):
            self.assertIn(token, out)
        for token in ("part 1 of", "part 2 of", "WARNING"):  # (not the part ids: the temp dir name is random)
            self.assertNotIn(token, out)
        self.assertEqual(sorted(p.relative_to(self.repo).as_posix() for p in self.repo.rglob("*")), before)
        code, out, err = self.cli("--plan")  # the default plan is unchanged
        self.assertEqual(code, 0, err)
        self.assertIn("part 1 of 4  core", out)
        self.assertNotIn("single ZIP", out)

    # --- command line --------------------------------------------------------------------------------------------

    def test_single_with_verify_only_is_a_usage_error(self):
        for argv in (["--single", "--verify-only", "x"], ["--verify-only", "x", "--single"],
                     ["--verify-only", "x", "--single", "--extract-check", "y"]):
            with self.subTest(argv=argv):
                err = io.StringIO()
                with redirect_stderr(err):
                    with self.assertRaises(SystemExit) as caught:
                        release.parse_args(argv)
                self.assertEqual(caught.exception.code, 2)
                self.assertIn("detects its layout from the .sha256", err.getvalue())
        for argv in (["--plan", "--single"], ["--single", "--force", "--allow-dirty"],
                     ["--single", "--extract-check", "x"], ["--single"]):  # these combine fine
            with self.subTest(argv=argv):
                self.assertTrue(release.parse_args(argv).single)
        self.assertFalse(release.parse_args([]).single)

    def test_build_verify_and_extract_check_end_to_end(self):
        out = self.tmp / "releases"
        code, stdout, err = self.cli("--single", "--allow-dirty", "--out", str(out),
                                     "--extract-check", str(self.tmp / "s"))
        self.assertEqual(code, 0, err)
        self.assertIn("RELEASE_ZIPS_BUILT", stdout)
        self.assertIn(f"{len(self.files)} files identical to dist", stdout)
        self.assertIn(f"{SINGLE_ZIP}  ", stdout)
        self.assertRegex(stdout, r"single ZIP: \d+\.\d\d GiB \([\d,]+ B\); the 2\*\*31 per-ZIP size gate does not apply")
        self.assertEqual(sorted(p.name for p in out.iterdir()), sorted([SINGLE_ZIP, SHA_NAME]))
        code, stdout, err = self.cli("--verify-only", str(out))
        self.assertEqual(code, 0, err)
        self.assertIn("RELEASE_ZIPS_VERIFIED 0.1.0 (single ZIP)", stdout)
        code, stdout, err = self.cli("--verify-only", str(out), "--extract-check", str(self.tmp / "s2"))
        self.assertEqual(code, 0, err)
        self.assertIn("extract-check OK", stdout)

    def test_cli_force_semantics_across_layouts(self):
        out = self.tmp / "releases"
        self.assertEqual(self.cli("--allow-dirty", "--out", str(out))[0], 0)  # multi-part first
        code, _stdout, err = self.cli("--single", "--allow-dirty", "--out", str(out))
        self.assertEqual(code, 1)
        self.assertIn("--force", err)
        self.assertEqual(len(list(out.glob("*.zip"))), 4)
        self.assertEqual(self.cli("--single", "--allow-dirty", "--force", "--out", str(out))[0], 0)
        self.assertEqual([p.name for p in out.glob("*.zip")], [SINGLE_ZIP])
        code, _stdout, err = self.cli("--single", "--allow-dirty", "--out", str(out))
        self.assertEqual(code, 1)
        self.assertIn("--force", err)

    def test_default_output_directory_and_the_dirty_gate_apply_to_single(self):
        code, _out, err = self.cli("--single")  # not a clean repository
        self.assertEqual(code, 1)
        self.assertIn("--allow-dirty", err)
        self.assertFalse((self.repo / "work").exists())
        code, _out, err = self.cli("--single", "--allow-dirty")
        self.assertEqual(code, 0, err)
        self.assertTrue((self.repo / "work/releases/0.1.0" / SINGLE_ZIP).is_file())
        self.assertTrue((self.repo / "work/releases/0.1.0" / SHA_NAME).is_file())

    def test_extract_check_scratch_is_validated_before_the_single_zip_is_built(self):
        busy = self.tmp / "busy"
        busy.mkdir()
        (busy / "old.txt").write_text("x")
        out = self.tmp / "releases"
        code, stdout, err = self.cli("--single", "--allow-dirty", "--out", str(out), "--extract-check", str(busy))
        self.assertEqual(code, 1)
        self.assertIn("scratch", err)
        self.assertNotIn("RELEASE_ZIPS_BUILT", stdout)
        self.assertFalse(out.exists())

    def test_verify_only_fails_on_a_tampered_single_zip(self):
        out = self.tmp / "releases"
        self.assertEqual(self.cli("--single", "--allow-dirty", "--out", str(out))[0], 0)
        path = out / SINGLE_ZIP
        data = bytearray(path.read_bytes())
        data[len(data) // 2] ^= 0x01
        path.write_bytes(bytes(data))
        code, _out, err = self.cli("--verify-only", str(out))
        self.assertEqual(code, 1)
        self.assertIn("ERROR", err)


class ForcedSwapTests(FakeRepoCase):
    """The commit phase of build_release, where the previous release is replaced: a failed forced rebuild changes
    nothing in any combination of layouts (finished files, stale .partial files, a locked previous file), and a
    previous file that cannot be deleted once the new set is in place never spoils that set."""

    PART3 = "maschine-ArenaMapPort-0.1.0-part3of4-bay5.zip"
    STALE = ("maschine-ArenaMapPort-0.1.0.zip.partial",  # the single layout's own in-progress name
             "maschine-ArenaMapPort-0.1.0-part2of4-equator.zip.partial",  # a multi-part leftover of this part count
             "maschine-ArenaMapPort-0.1.0-part2of5-equator.zip.partial",  # ... of an older part count
             "maschine-ArenaMapPort-0.1.0.sha256.partial")

    def setUp(self):
        super().setUp()
        self.pin_released_maps()
        self.counter = 0

    @staticmethod
    def locked(should_fail):
        """os.replace failing like a file held open by 7-Zip, a virus scan or an upload still reading it: the
        renames `should_fail(source name, target name)` selects raise PermissionError."""
        real = os.replace

        def replace(src, dst, *args, **kwargs):
            if should_fail(Path(src).name, Path(dst).name):
                raise PermissionError(13, "The process cannot access the file because it is being used by another "
                                          "process", str(src))
            return real(src, dst, *args, **kwargs)

        return patch.object(os, "replace", replace)

    def change_dist(self):
        """A different dist byte, so a new set that wrongly reached the directory could not equal the old one."""
        self.counter += 1
        self.dist.joinpath(SERVER, "db", "arena_bay5", "base.json").write_bytes(f"changed {self.counter}".encode())

    @staticmethod
    def into_place(name):
        return lambda src, dst: src.endswith(".partial") and dst == name

    # --- finding 1: a failing rename into place --------------------------------------------------------------

    def test_a_failing_rename_into_place_leaves_no_partial_files_and_the_old_set_intact(self):
        cases = (("single over single, the ZIP", True, True, self.into_place(SINGLE_ZIP)),
                 ("single over single, the checksum", True, True, self.into_place(SHA_NAME)),
                 ("single over parts", False, True, self.into_place(SINGLE_ZIP)),
                 ("parts over single, the third part", True, False, self.into_place(self.PART3)),
                 ("parts over parts, the third part", False, False, self.into_place(self.PART3)),
                 ("parts over parts, the checksum", False, False, self.into_place(SHA_NAME)))
        for label, old_single, new_single, fail in cases:
            with self.subTest(label):
                name = f"out{self.counter}"
                old = self.build(name, single=old_single)
                before = snapshot(old.out_dir)
                self.change_dist()
                with self.locked(fail):
                    with self.assertRaises(OSError):
                        self.build(name, single=new_single, force=True)
                self.assertEqual(snapshot(old.out_dir), before, "the failed swap changed the directory")
                self.assertEqual(release.verify_set(old.out_dir, log=quiet).layout,
                                 "single" if old_single else "parts")

    def test_a_failed_swap_says_so_and_a_retry_goes_through(self):
        old = self.build(single=True)
        before = snapshot(old.out_dir)
        self.change_dist()
        logged = []
        with self.locked(self.into_place(SINGLE_ZIP)):
            with self.assertRaises(OSError):
                self.build(single=True, force=True, log=logged.append)
        self.assertTrue(any("previous release was left as it was" in line for line in logged), logged)
        self.assertEqual(snapshot(old.out_dir), before)
        self.build(single=True, force=True)
        self.assertEqual(sorted(p.name for p in old.out_dir.iterdir()), sorted([SINGLE_ZIP, SHA_NAME]))
        self.assertNotEqual(snapshot(old.out_dir)[SINGLE_ZIP], before[SINGLE_ZIP])

    # --- finding 2: a locked or undeletable previous file ----------------------------------------------------

    def test_a_locked_previous_file_stops_the_swap_before_anything_changes(self):
        for label, old_single, new_single, locked in (
                ("part 3 of the old parts, new single", False, True, self.PART3),
                ("the old single ZIP, new parts", True, False, SINGLE_ZIP),
                ("the old checksum, new single", False, True, SHA_NAME)):
            with self.subTest(label):
                name = f"out{self.counter}"
                old = self.build(name, single=old_single)
                before = snapshot(old.out_dir)
                self.change_dist()
                with self.locked(lambda src, dst: src == locked):
                    with self.assertRaises(release.PackagingError) as caught:
                        self.build(name, single=new_single, force=True)
                self.assertIn(locked, str(caught.exception))
                self.assertIn("another program", str(caught.exception))
                self.assertEqual(snapshot(old.out_dir), before, "no new ZIP, no .partial, no .superseded may remain")
                self.assertEqual(release.verify_set(old.out_dir, log=quiet).layout,
                                 "single" if old_single else "parts")
                new = self.build(name, single=new_single, force=True)  # the lock is gone: the same command works
                self.assertEqual(release.verify_set(new.out_dir, log=quiet).layout,
                                 "single" if new_single else "parts")
                self.assertEqual(sorted(p.name for p in new.out_dir.iterdir()),
                                 sorted([p.name for p in new.zips] + [SHA_NAME]))

    def test_a_previous_file_that_cannot_be_deleted_afterwards_does_not_spoil_the_new_set(self):
        real_unlink = Path.unlink

        def unlink(path, missing_ok=False):
            if path.name == self.PART3 + ".superseded":
                raise PermissionError(13, "The process cannot access the file", str(path))
            return real_unlink(path, missing_ok=missing_ok)

        old = self.build(single=False)
        self.change_dist()
        logged = []
        with patch.object(Path, "unlink", unlink):
            new = self.build(single=True, force=True, log=logged.append)
        self.assertEqual([p.name for p in new.out_dir.glob("*.zip")], [SINGLE_ZIP])  # nothing of the old set is a ZIP
        verified = release.verify_set(old.out_dir, log=quiet)  # ... so the directory verifies as the new release
        self.assertEqual((verified.layout, [p.id for p in verified.parts]), ("single", ["full"]))
        self.assertEqual([p.name for p in new.out_dir.iterdir() if p.name.endswith(".superseded")],
                         [self.PART3 + ".superseded"])
        warnings = [line for line in logged if line.startswith("WARNING")]
        self.assertEqual(len(warnings), 1, logged)
        self.assertIn(self.PART3 + ".superseded", warnings[0])
        self.assertIn("new release is complete", warnings[0])

    def test_the_command_line_reports_success_when_only_the_cleanup_of_old_files_fails(self):
        out = self.tmp / "releases"
        self.assertEqual(self.cli("--allow-dirty", "--out", str(out))[0], 0)
        real_unlink = Path.unlink

        def unlink(path, missing_ok=False):
            if path.name.endswith(".superseded"):
                raise PermissionError(13, "The process cannot access the file", str(path))
            return real_unlink(path, missing_ok=missing_ok)

        with patch.object(Path, "unlink", unlink):
            code, stdout, err = self.cli("--single", "--allow-dirty", "--force", "--out", str(out),
                                         "--extract-check", str(self.tmp / "scratch"))
        self.assertEqual(code, 0, err)
        self.assertIn("RELEASE_ZIPS_BUILT", stdout)
        self.assertIn("WARNING", stdout)
        self.assertIn("extract-check OK", stdout)
        self.assertEqual([p.name for p in out.glob("*.zip")], [SINGLE_ZIP])

    # --- finding 3: stale .partial files belong to the previous release ------------------------------------------

    def failures(self):
        """(label, exception raised, a context that makes the build fail after it started writing)."""
        victim = f"{SERVER}/db/arena_bay5/base.json"
        grown = [release.DistFile(f.rel, f.path, f.size + 1) if f.rel == victim else f for f in self.scan()]
        yield "dist file changing while packaging", release.PackagingError, patch.object(
            release, "scan_dist", return_value=grown)
        yield "Ctrl-C while writing", KeyboardInterrupt, patch.object(release, "write_part",
                                                                     side_effect=KeyboardInterrupt)
        yield "a ZIP failing its check", release.VerificationError, patch.object(
            release, "verify_zip", side_effect=release.VerificationError("boom"))
        yield "a failing rename into place", OSError, self.locked(
            lambda src, dst: src.endswith(".partial") and dst.endswith((".zip", ".sha256")))

    def test_stale_partials_survive_a_failed_forced_build_and_go_after_a_successful_one(self):
        for new_single in (True, False):
            for old_layout in (None, "single", "parts"):
                with self.subTest(new_single=new_single, old_layout=old_layout):
                    name = f"out{self.counter}"
                    out = self.tmp / name
                    if old_layout:
                        self.build(name, single=(old_layout == "single"))
                        self.change_dist()
                    out.mkdir(exist_ok=True)
                    for stale in self.STALE:
                        (out / stale).write_bytes(b"crashed " + stale.encode())
                    before = snapshot(out)
                    for label, expected, failing in self.failures():
                        with self.subTest(failure=label), failing:
                            with self.assertRaises(expected):
                                self.build(name, single=new_single, force=True)
                        self.assertEqual(snapshot(out), before, f"{label}: the previous files were touched")
                    result = self.build(name, single=new_single, force=True)
                    self.assertEqual(sorted(p.name for p in out.iterdir()),
                                     sorted([p.name for p in result.zips] + [SHA_NAME]),
                                     "stale .partial files and the previous release go once the new set is in place")
                    self.assertEqual(release.verify_set(out, log=quiet).layout,
                                     "single" if new_single else "parts")
                    self.counter += 1


class DamagedSingleSetTests(FakeRepoCase):
    """Verification of single-layout sets damaged in ways that keep the checksum honest: the ZIP is rewritten and
    its one SHA-256 line refreshed, so only the intended inconsistency is left for the verifier to find."""

    def setUp(self):
        super().setUp()
        self.pin_released_maps()
        self.result = self.build(single=True)
        self.copy = self.tmp / "copy"
        shutil.copytree(self.result.out_dir, self.copy)

    def rewrite(self, mutate=None):
        path = self.copy / SINGLE_ZIP
        entries = read_zip(path)
        manifest = json.loads(entries.pop(FULL_MANIFEST))
        if mutate is not None:
            mutate(entries, manifest)
        entries[FULL_MANIFEST] = (json.dumps(manifest, indent=2) + "\n").encode("utf-8")
        path.unlink()
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, data in entries.items():
                archive.writestr(name, data)
        (self.copy / SHA_NAME).write_text(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {SINGLE_ZIP}\n",
                                          encoding="ascii", newline="\n")

    @staticmethod
    def drop(entries, manifest, name):
        entries.pop(name)
        removed = next(e for e in manifest["entries"] if e["path"] == name)
        manifest["entries"] = [e for e in manifest["entries"] if e["path"] != name]
        if removed["origin"] == "dist":  # keep the manifest's own counters honest
            manifest["parts"][0]["dist_files"] -= 1
            manifest["parts"][0]["raw_bytes"] -= removed["size"]

    def assert_rejected(self, fragment):
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(self.copy, log=quiet)
        self.assertIn(fragment, str(caught.exception))

    def test_the_untouched_copy_verifies(self):
        self.rewrite()  # the rewrite helper itself must not break a good set
        self.assertEqual(release.verify_set(self.copy, log=quiet).layout, "single")

    def test_every_maps_bundles_and_database_entry_must_be_present(self):
        for name in (f"{PLUGIN}/bundles/arena_airpit_navmesh.bundle", f"{PLUGIN}/bundles/arena_equator_scenes.bundle",
                     f"{PLUGIN}/bundles/arena_bay5_preset.bundle", f"{SERVER}/db/arena_bay5/base.json",
                     f"{SERVER}/db/arena_equator/base.json"):
            with self.subTest(name=name):
                shutil.rmtree(self.copy)
                shutil.copytree(self.result.out_dir, self.copy)
                self.rewrite(lambda entries, manifest: self.drop(entries, manifest, name))
                self.assert_rejected(name)

    def test_every_maps_key_file_must_be_present(self):
        for key in ("arena_equator", "arena_bay5", "arena_airpit"):
            with self.subTest(key=key):
                shutil.rmtree(self.copy)
                shutil.copytree(self.result.out_dir, self.copy)
                self.rewrite(lambda entries, manifest: self.drop(entries, manifest, f"{PLUGIN}/keys/{key}.json"))
                self.assert_rejected(f"map {key} lacks")

    def test_a_plaintext_bundle_inside_the_single_zip_is_refused(self):
        name = f"{PLUGIN}/bundles/arena_equator_navmesh.bundle"

        def plaintext(entries, manifest):
            entries[name] = b"UnityFS\0" + b"\x02" * 300
            for entry in manifest["entries"]:
                if entry["path"] == name:
                    entry["size"] = len(entries[name])
                    entry["sha256"] = hashlib.sha256(entries[name]).hexdigest()
            manifest["parts"][0]["raw_bytes"] = sum(e["size"] for e in manifest["entries"] if e["origin"] == "dist")
        self.rewrite(plaintext)
        self.assert_rejected(f"plaintext bundle {name}")

    def test_a_single_zip_without_any_map_is_refused(self):
        def strip_maps(entries, manifest):
            for name in [n for n in entries if release.MAP_DATA_RE.fullmatch(n)]:
                self.drop(entries, manifest, name)
        self.rewrite(strip_maps)
        self.assert_rejected("carries no map")

    def drop_map(self, key):
        """Every map-data entry of one released map (3 bundles + keys/<key>.json + db/<key>/base.json), counters kept honest."""
        def mutate(entries, manifest):
            names = [n for n in entries if (m := release.MAP_DATA_RE.fullmatch(n)) and key in m.groups()]
            self.assertEqual(len(names), 5, names)
            for name in names:
                self.drop(entries, manifest, name)
        self.rewrite(mutate)

    def test_a_whole_released_map_missing_from_the_zip_is_refused(self):
        """The map list used to come from the archive's own entries, so a ZIP that lost a complete map (all three
        bundles and its database entry) verified: nothing in it mentioned the map any more."""
        for key in ("arena_bay5", "arena_airpit", "arena_equator"):
            with self.subTest(key=key):
                shutil.rmtree(self.copy)
                shutil.copytree(self.result.out_dir, self.copy)
                self.drop_map(key)
                with self.assertRaises(release.VerificationError) as caught:
                    release.verify_set(self.copy, log=quiet)
                message = str(caught.exception)
                self.assertIn(f"map {key} lacks", message)
                self.assertIn(f"bundles/{key}_scenes.bundle", message)
                self.assertIn(f"keys/{key}.json", message)
                self.assertIn(f"db/{key}/base.json", message)

    def test_the_whole_map_guard_reaches_the_command_line(self):
        self.drop_map("arena_bay5")
        code, stdout, err = self.cli("--verify-only", str(self.copy))
        self.assertEqual(code, 1)
        self.assertNotIn("RELEASE_ZIPS_VERIFIED", stdout)
        self.assertIn("map arena_bay5 lacks", err)

    def test_the_expected_map_list_is_injectable(self):
        self.drop_map("arena_bay5")
        two = ["arena_equator", "arena_airpit"]
        self.assertEqual(release.verify_set(self.copy, maps=two, log=quiet).layout, "single")
        self.assert_rejected("arena_bay5")  # the default (released) list still wants all three
        for wanted in (two + ["arena_prison"], ["arena_bay5"]):
            with self.assertRaises(release.VerificationError) as caught:
                release.verify_set(self.copy, maps=wanted, log=quiet)
            self.assertIn(f"map {wanted[-1]} lacks", str(caught.exception))
        # a map the ZIP carries but the expected list does not name must still be complete
        shutil.rmtree(self.copy)
        shutil.copytree(self.result.out_dir, self.copy)
        name = f"{PLUGIN}/bundles/arena_airpit_navmesh.bundle"
        self.rewrite(lambda entries, manifest: self.drop(entries, manifest, name))
        with self.assertRaises(release.VerificationError) as caught:
            release.verify_set(self.copy, maps=["arena_equator", "arena_bay5"], log=quiet)
        self.assertIn(name, str(caught.exception))

    def test_a_single_zip_built_from_other_maps_verifies_against_those_maps(self):
        spec2 = release.load_spec(self.repo, maps=MAPS[:2])
        dist2 = self.tmp / "dist2"
        write_tree(dist2, fake_dist_files(MAPS[:2]))
        built = release.build_release(spec2, dist2, self.tmp / "out2", self.repo, commit=COMMIT, log=quiet,
                                      single=True)
        keys = [m.key for m in spec2.maps]
        self.assertEqual(release.verify_set(built.out_dir, maps=keys, log=quiet).layout, "single")
        with self.assertRaises(release.VerificationError) as caught:  # against the 3 released maps, AirPit is gone
            release.verify_set(built.out_dir, log=quiet)
        self.assertIn("map arena_airpit lacks", str(caught.exception))

    def test_the_generated_readme_and_notices_must_be_present(self):
        for name in (f"{PLUGIN}/README.txt", f"{PLUGIN}/THIRD-PARTY-NOTICES.txt", f"{SERVER}/THIRD-PARTY-NOTICES.txt"):
            with self.subTest(name=name):
                shutil.rmtree(self.copy)
                shutil.copytree(self.result.out_dir, self.copy)
                self.rewrite(lambda entries, manifest: self.drop(entries, manifest, name))
                self.assert_rejected(f"generated entry missing: {name}")

    def test_the_manifest_must_say_full_1_of_1(self):
        cases = (("does not match its part id", lambda e, m: m["part"].update(id="core")),
                 ("part list is inconsistent", lambda e, m: m["part"].update(of=2)),
                 ("part list is inconsistent", lambda e, m: m["parts"].append(dict(m["parts"][0], index=2))),
                 ("manifest names", lambda e, m: m["part"].update(zip="other.zip")))
        for fragment, mutate in cases:
            with self.subTest(fragment=fragment):
                shutil.rmtree(self.copy)
                shutil.copytree(self.result.out_dir, self.copy)
                self.rewrite(mutate)
                self.assert_rejected(fragment)

    def test_a_manifest_naming_a_multi_part_id_is_refused_even_when_consistent(self):
        """parts/core.json inside the file the .sha256 calls the single ZIP: not the single layout."""
        path = self.copy / SINGLE_ZIP
        entries = read_zip(path)
        manifest = json.loads(entries.pop(FULL_MANIFEST))
        manifest["part"]["id"] = "core"
        manifest["parts"][0]["id"] = "core"
        entries[f"{PLUGIN}/parts/core.json"] = (json.dumps(manifest, indent=2) + "\n").encode("utf-8")
        path.unlink()
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, data in entries.items():
                archive.writestr(name, data)
        (self.copy / SHA_NAME).write_text(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {SINGLE_ZIP}\n",
                                          encoding="ascii", newline="\n")
        self.assert_rejected("manifest part id/index/count disagree")

    def test_malformed_single_checksum_files_are_refused(self):
        digest = hashlib.sha256((self.copy / SINGLE_ZIP).read_bytes()).hexdigest()
        sha = self.copy / SHA_NAME
        cases = {
            "duplicate part": f"{digest}  {SINGLE_ZIP}\n{digest}  {SINGLE_ZIP}\n",
            "malformed line": f"{digest.upper()}  {SINGLE_ZIP}\n",
            "missing final newline": f"{digest}  {SINGLE_ZIP}",
            "empty": "",
            # a second line leaves the single layout: the one ZIP is then not part 1 of 2
            "is not part 1 of 2": f"{digest}  {SINGLE_ZIP}\n{digest}  maschine-ArenaMapPort-0.1.0-part2of2-x.zip\n",
        }
        for label, text in cases.items():
            with self.subTest(label=label):
                sha.write_bytes(text.encode("ascii"))
                self.assert_rejected(label)

    def test_a_checksum_naming_another_zip_is_not_the_single_layout(self):
        digest = hashlib.sha256((self.copy / SINGLE_ZIP).read_bytes()).hexdigest()
        (self.copy / SHA_NAME).write_text(f"{digest}  maschine-ArenaMapPort-0.9.9.zip\n", encoding="ascii",
                                          newline="\n")
        with self.assertRaises(release.VerificationError):
            release.verify_set(self.copy, log=quiet)


class GitInvocationTests(FakeRepoCase):
    """What the packager asks git, without needing git: the arguments and the environment it passes."""

    def captured_call(self, function, *args):
        completed = subprocess.CompletedProcess([], 0, stdout="", stderr="")
        with patch.dict(os.environ, {"GIT_DIR": "elsewhere", "git_work_tree": "elsewhere", "GIT_INDEX_FILE": "x",
                                     "GIT_CEILING_DIRECTORIES": "other", "UNRELATED": "kept"}):
            with patch.object(release.subprocess, "run", return_value=completed) as run:
                function(*args)
        return run.call_args

    def test_status_ignores_user_configuration_and_leaves_the_index_alone(self):
        call = self.captured_call(release.require_clean_tree, self.repo)
        self.assertEqual(call.args[0], ["git", "--no-optional-locks", "status", "--porcelain=v1",
                                        "--untracked-files=all", "--ignore-submodules=none"])
        self.assertEqual(call.kwargs["cwd"], self.repo)

    def test_the_callers_git_variables_never_reach_git(self):
        for function in (release.require_clean_tree, release.source_commit):
            with self.subTest(function=function.__name__):
                env = self.captured_call(function, self.repo).kwargs["env"]
                self.assertEqual({k for k in env if k.upper().startswith("GIT_")}, {"GIT_CEILING_DIRECTORIES"})
                self.assertEqual(env["GIT_CEILING_DIRECTORIES"], str(self.repo.parent))
                self.assertEqual(env["UNRELATED"], "kept")


@unittest.skipUnless(shutil.which("git"), "git not installed")
class GitGateTests(FakeRepoCase):
    def git(self, *args, cwd=None):
        proc = subprocess.run(["git", "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                               "-c", "commit.gpgsign=false", *args], cwd=cwd or self.repo, check=True,
                              capture_output=True, text=True, env=git_env())
        return proc.stdout.strip()

    def commit_everything(self, cwd=None):
        self.git("add", "-A", cwd=cwd)
        self.git("commit", "-q", "-m", "fixture", cwd=cwd)
        return self.git("rev-parse", "HEAD", cwd=cwd)

    def clean_repository(self):
        """The fake repo committed in full, with the build output directories ignored (like the real .gitignore)."""
        (self.repo / ".gitignore").write_text("work/\n", encoding="utf-8")
        self.git("init", "-q")
        return self.commit_everything()

    def part_manifest(self, out_dir, part="core"):
        archive_path = next(out_dir.glob(f"*-{part}.zip"))
        with zipfile.ZipFile(archive_path) as archive:
            return json.loads(archive.read(f"{PLUGIN}/parts/{part}.json"))

    def test_non_repository_has_no_commit_and_fails_the_gate(self):
        self.assertEqual(release.source_commit(self.repo), "unknown")
        with self.assertRaises(release.PackagingError):
            release.require_clean_tree(self.repo)

    def test_untracked_files_make_the_tree_dirty(self):
        self.git("init", "-q")
        with self.assertRaises(release.PackagingError) as caught:
            release.require_clean_tree(self.repo)
        self.assertIn("not clean", str(caught.exception))

    def test_clean_tree_passes_and_an_unborn_branch_has_no_commit(self):
        clean = self.tmp / "clean"
        clean.mkdir()
        subprocess.run(["git", "init", "-q"], cwd=clean, check=True, capture_output=True, env=git_env())
        release.require_clean_tree(clean)
        self.assertEqual(release.source_commit(clean), "unknown")

    def test_user_configuration_cannot_hide_untracked_files(self):
        head = self.clean_repository()
        self.git("config", "status.showUntrackedFiles", "no")
        release.require_clean_tree(self.repo)
        (self.repo / "stray.txt").write_text("untracked")
        self.assertEqual(self.git("status", "--porcelain"), "", "the fixture must hide the file from plain git status")
        with self.assertRaises(release.PackagingError) as caught:
            release.require_clean_tree(self.repo)
        self.assertIn("stray.txt", str(caught.exception))
        self.assertEqual(release.source_commit(self.repo), head)

    def test_another_repository_in_the_environment_is_not_mistaken_for_this_one(self):
        other = self.tmp / "other"
        other.mkdir()
        self.git("init", "-q", cwd=other)
        (other / "f.txt").write_text("x")
        self.commit_everything(cwd=other)
        with patch.dict(os.environ, {"GIT_DIR": str(other / ".git"), "GIT_WORK_TREE": str(other)}):
            self.assertEqual(release.source_commit(self.repo), "unknown")  # self.repo is not a repository
            with self.assertRaises(release.PackagingError):
                release.require_clean_tree(self.repo)

    def test_the_status_check_does_not_rewrite_the_index(self):
        self.clean_repository()
        tracked = self.repo / "THIRD-PARTY-NOTICES.md"
        stat = tracked.stat()
        os.utime(tracked, (stat.st_atime + 100, stat.st_mtime + 100))  # stale stat data: a plain `git status` refreshes it
        index = self.repo / ".git" / "index"
        before = index.read_bytes()
        release.require_clean_tree(self.repo)
        self.assertEqual(index.read_bytes(), before)
        self.git("status")  # sanity: the same situation does make a plain `git status` rewrite the index
        self.assertNotEqual(index.read_bytes(), before)

    def test_a_clean_tree_records_the_plain_commit_without_warnings(self):
        head = self.clean_repository()
        code, _out, err = self.cli()
        self.assertEqual(code, 0, err)
        self.assertEqual(err, "")
        out_dir = self.repo / "work" / "releases" / "0.1.0"
        self.assertEqual(self.part_manifest(out_dir)["source_commit"], head)
        shutil.rmtree(out_dir)
        code, _out, err = self.cli("--allow-dirty")
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.part_manifest(out_dir)["source_commit"], head)

    def test_an_allowed_dirty_tree_is_recorded_as_dirty(self):
        head = self.clean_repository()
        (self.repo / "stray.txt").write_text("uncommitted")
        out = self.tmp / "dirty"
        code, _out, err = self.cli("--allow-dirty", "--out", str(out))
        self.assertEqual(code, 0, err)
        self.assertIn("WARNING", err)
        self.assertIn("+dirty", err)
        for part in ("core", "equator", "bay5", "airpit"):
            self.assertEqual(self.part_manifest(out, part)["source_commit"], head + "+dirty")
        code, stdout, err = self.cli("--verify-only", str(out))  # the pre-upload check says so, but still verifies
        self.assertEqual(code, 0, err)
        self.assertIn("RELEASE_ZIPS_VERIFIED", stdout)
        self.assertIn("uncommitted", err)
        code, _out, err = self.cli("--out", str(self.tmp / "refused"))  # without --allow-dirty the gate still blocks
        self.assertEqual(code, 1)
        self.assertIn("stray.txt", err)

    def test_an_unidentifiable_commit_is_reported(self):
        code, _out, err = self.cli("--allow-dirty", "--out", str(self.tmp / "unknown"))  # self.repo is no repository
        self.assertEqual(code, 0, err)
        self.assertIn("cannot be determined", err)
        self.assertEqual(self.part_manifest(self.tmp / "unknown")["source_commit"], "unknown")
        code, _out, err = self.cli("--verify-only", str(self.tmp / "unknown"))
        self.assertEqual(code, 0)
        self.assertIn("unidentified", err)


class EncryptedBundleTests(FakeRepoCase):
    """Stage 2 of the Arena gate (docs/PLAN-ENCRYPTION.md): every bundle in dist is an AMPB container and every released
    map ships keys/<key>.json. The packer reads four bytes of each bundle; it refuses a plaintext one before it writes
    anything, classifies the key files per map, demands one per map and says in the generated README what the
    encryption is and is not."""

    def test_a_plaintext_bundle_in_dist_is_refused_for_every_bundle(self):
        for key, _display in MAPS:
            for kind in ("scenes", "navmesh", "preset"):
                name = f"{PLUGIN}/bundles/{key}_{kind}.bundle"
                path = self.dist / name
                original = path.read_bytes()
                for plaintext in (b"UnityFS\0" + b"x" * 200, b"", b"AMP", b"ampb" + b"x" * 20, b"\0AMPB" + b"x" * 20):
                    with self.subTest(name=name, head=plaintext[:8]):
                        path.write_bytes(plaintext)
                        try:
                            with self.assertRaises(release.PackagingError) as caught:
                                self.partition()
                            self.assertIn("plaintext bundle in dist", str(caught.exception))
                            self.assertIn(name, str(caught.exception))
                        finally:
                            path.write_bytes(original)

    def test_neither_layout_writes_anything_for_a_plaintext_bundle(self):
        (self.dist / PLUGIN / "bundles/arena_bay5_scenes.bundle").write_bytes(b"UnityFS\0" + b"x" * 64)
        for single in (False, True):
            with self.subTest(single=single):
                with self.assertRaises(release.PackagingError) as caught:
                    self.build(f"out-{single}", single=single)
                self.assertIn("plaintext bundle in dist", str(caught.exception))
                self.assertFalse((self.tmp / f"out-{single}").exists())
                code, _out, err = self.cli("--dist", str(self.dist), "--out", str(self.tmp / f"cli-{single}"),
                                           "--allow-dirty", *(["--single"] if single else []))
                self.assertEqual(code, 1)
                self.assertIn("plaintext bundle in dist", err)
        code, _out, err = self.cli("--dist", str(self.dist), "--plan")
        self.assertEqual(code, 1, err)
        self.assertIn("plaintext bundle in dist", err)

    def test_ampb_bundles_are_accepted_whatever_follows_the_magic(self):
        parts = self.partition()
        self.assertEqual(sum(len(p.files) for p in parts), len(self.files))

    def test_a_key_file_travels_with_its_map(self):
        parts = {p.id: {f.rel for f in p.files} for p in self.partition()}
        for key, _display in MAPS:
            stem = key.removeprefix("arena_")
            name = f"{PLUGIN}/keys/{key}.json"
            self.assertIn(name, parts[stem])
            for other, names in parts.items():
                if other != stem:
                    self.assertNotIn(name, names)
        self.assertEqual({r.label for r in release.build_rules(self.spec)} & {"key file"}, {"key file"})

    def test_a_map_without_its_key_file_is_not_installable(self):
        for key, _display in MAPS:
            name = f"{PLUGIN}/keys/{key}.json"
            with self.subTest(key=key):
                original = (self.dist / name).read_bytes()
                (self.dist / name).unlink()
                try:
                    with self.assertRaises(release.PackagingError) as caught:
                        self.partition()
                    self.assertIn("incomplete dist", str(caught.exception))
                    self.assertIn(name, str(caught.exception))
                finally:
                    (self.dist / name).write_bytes(original)
        self.assertIn(f"{PLUGIN}/keys/arena_bay5.json", release.required_files(self.spec)["bay5"])

    def test_only_a_released_maps_exact_key_file_is_classified(self):
        for name in (f"{PLUGIN}/keys/arena_prison.json", f"{PLUGIN}/keys/arena_airpit2.json",
                     f"{PLUGIN}/keys/arena_equator.json.bak", f"{PLUGIN}/keys/arena_equator.txt",
                     f"{PLUGIN}/keys/arena_equator.key", f"{PLUGIN}/keys/sub/arena_equator.json",
                     f"{PLUGIN}/keys/Thumbs.db", f"{PLUGIN}/arena_equator.json"):
            with self.subTest(name=name):
                (self.dist / name).parent.mkdir(parents=True, exist_ok=True)
                (self.dist / name).write_bytes(b"{}")
                try:
                    with self.assertRaises(release.PackagingError):
                        self.partition()
                finally:
                    (self.dist / name).unlink()

    def test_the_gate_does_not_look_at_other_files(self):
        # Only bundle-rule files are read for the magic: a sidecar that happens to start with UnityFS is a different check
        # (validate_distribution), and the JSON key files are not bundles.
        (self.dist / PLUGIN / "ai/airpit.ai.json").write_bytes(b"UnityFS\0 not a bundle")
        self.assertEqual(sum(len(p.files) for p in self.partition()), len(self.files))

    def test_the_generated_readme_states_what_the_encryption_is_and_is_not(self):
        for single in (False, True):
            parts = release.partition_single(self.scan(), self.spec) if single else self.partition()
            text = release.readme_text(self.spec, parts)
            with self.subTest(single=single):
                self.assertIn("Encrypted bundles", text)
                self.assertIn("your PC derives from the Arena resource files each map is built from", text)
                self.assertIn("a fixed sample of a few MB", text)
                self.assertIn("by path and size only", text)
                self.assertNotIn("level files", text)
                self.assertIn("not copy protection", text)
                self.assertIn("reads no game data", text)  # the installation check's own statement, kept true next to the new paragraph
                self.assertIn("are not encrypted", text)
                for word in ("Loading screens", "audio"):
                    self.assertIn(word, text.split("Encrypted bundles", 1)[1].split("Installation", 1)[0])
                self.assertLess(text.index("Requirement:"), text.index("Encrypted bundles"))
                self.assertLess(text.index("Encrypted bundles"), text.index("Installation"))

    def test_the_generated_readme_names_the_keys_folder_the_refs_and_the_gate_rule(self):
        # 0.3.0 texts: the keys folder belongs to the bundles, the main-menu message carries a ref code, and the gates of the team
        # spawn enclosures stand open at raid start unless a mode plugin holds them (Air Pit's passport control is held in every raid).
        for single in (False, True):
            parts = release.partition_single(self.scan(), self.spec) if single else self.partition()
            text = re.sub(r"\s+", " ", release.readme_text(self.spec, parts))
            with self.subTest(single=single):
                self.assertIn("Updating: extract the complete ZIP over the installation with the SPT server closed", text)
                self.assertIn("The keys folder (BepInEx/plugins/maschine-ArenaMapPort/keys) belongs to the bundles next to it", text)
                self.assertIn("a missing or damaged key file is reported as an error in the client log, and the maps do not load", text)
                self.assertIn('ends with a ref code, for example "ref NO_ARENA"', text)
                self.assertIn('section "If the maps do not show up"', text)
                self.assertIn("the lines marked [Arena] from BepInEx/LogOutput.log", text)
                self.assertIn(GATE_RULE, text)
                self.assertLess(text.index("Updating:"), text.index("Included locations:"))
                self.assertLess(text.index("Included locations:"), text.index(GATE_RULE))

    def test_built_sets_carry_the_key_files_and_verify(self):
        self.pin_released_maps()
        for single in (False, True):
            with self.subTest(single=single):
                result = self.build(f"set-{single}", single=single)
                verified = release.verify_set(result.out_dir, "0.1.0", log=quiet, maps=[k for k, _d in MAPS])
                names = {e["path"] for part in verified.parts for e in part.manifest["entries"]}
                for key, _display in MAPS:
                    self.assertIn(f"{PLUGIN}/keys/{key}.json", names)
                    for kind in ("scenes", "navmesh", "preset"):
                        self.assertIn(f"{PLUGIN}/bundles/{key}_{kind}.bundle", names)

    def test_bundles_are_stored_not_compressed(self):
        result = self.build("stored")
        for zip_path in result.zips:
            with zipfile.ZipFile(zip_path) as archive:
                for info in archive.infolist():
                    if info.filename.endswith(".bundle"):
                        self.assertEqual(info.compress_type, zipfile.ZIP_STORED, info.filename)


class RealRepoTests(unittest.TestCase):
    """Facts about the checked-in repository that must hold whether or not dist/ is staged."""

    def test_the_real_notices_file_passes_the_licence_gate(self):
        text = release.notices_text(REPO)
        self.assertEqual(text.count("MIT License"), 2)
        self.assertIn("danauraborealis", text)
        self.assertIn("LennoxP90", text)

    def test_client_and_server_versions_agree(self):
        spec = release.load_spec(REPO)  # raises when the two csproj versions differ or are missing
        self.assertTrue(release.VERSION_RE.fullmatch(spec.version), spec.version)

    def test_the_arena_build_of_the_keys_is_named_the_same_everywhere(self):
        """The generated README names the Arena build the shipped key files are built for (KEYS_BUILT_FOR); it must be the build of
        tools/bundle/key-sources.json, the one every player-facing text states (README, FORGE, the release notes of this version)."""
        sources = json.loads((REPO / "tools/bundle/key-sources.json").read_text(encoding="utf-8"))
        self.assertEqual(release.KEYS_BUILT_FOR, sources["arena_build"])
        self.assertIn(f"built for Arena {release.KEYS_BUILT_FOR}", release.ENCRYPTION_TEXT)
        spec = release.load_spec(REPO)
        for name in ("README.md", "FORGE.md", f"docs/releases/v{spec.version}.md"):
            text = (REPO / name).read_text(encoding="utf-8")
            with self.subTest(file=name):
                self.assertIn(release.KEYS_BUILT_FOR, text)

    def test_the_gate_rule_reads_the_same_in_the_player_texts_and_names_no_developer_key(self):
        """README.md and FORGE.md carry the shared GATE_RULE sentence (the generated package README is pinned in
        EncryptedBundleTests), and neither names a developer key of the gates: Spawns/* are developer settings, the public F12
        keys stay NaturalEquatorNight, ArenaInstallPath and ShowMapNotices."""
        for name in ("README.md", "FORGE.md"):
            text = re.sub(r"\s+", " ", (REPO / name).read_text(encoding="utf-8"))
            with self.subTest(file=name):
                self.assertIn(GATE_RULE, text)
                self.assertNotIn("Spawns/", text)
                self.assertNotIn("SpawnGate", text)


BOWL = ("arena_bowl", "Bowl")


class BowlPartTests(FakeRepoCase):
    """PLAN-BOWL item 8 (2026-10-18): Bowl is the fourth released map. It ships bundles, database entry, shader sidecar,
    AI sidecar, banner, acoustics and FOUR loading screens (Arena has no loading screen for it), but no flares, water
    package or overview map (item 17 polish), so its part must be complete and classified without them."""

    def setUp(self):
        super().setUp()
        bowl = {name: data for name, data in fake_dist_files(MAPS + (BOWL,)).items()
                if "arena_bowl" in name or "ArenaMapPort_Bowl" in name or name.endswith("/ai/bowl.ai.json")}
        for name in list(bowl):
            if "/flares/" in name or "/dynamicmaps/" in name:
                del bowl[name]
        for n in (3, 4):
            bowl[f"{PLUGIN}/loadscreens/arena_bowl_{n}.jpg"] = b"\xff\xd8\xff" + b"\x00" * 3000
        write_tree(self.dist, bowl)
        self.spec = release.load_spec(self.repo, maps=MAPS + (BOWL,))

    def test_bowl_is_the_fifth_part_and_holds_exactly_its_own_files(self):
        parts = self.partition()
        self.assertEqual([p.id for p in parts], ["core", "equator", "bay5", "airpit", "bowl"])
        self.assertEqual(parts[-1].zip_name, "maschine-ArenaMapPort-0.1.0-part5of5-bowl.zip")
        names = {f.rel for f in parts[-1].files}
        self.assertEqual(names, {
            *(f"{PLUGIN}/bundles/arena_bowl_{kind}.bundle" for kind in ("scenes", "navmesh", "preset")),
            f"{PLUGIN}/keys/arena_bowl.json",
            f"{PLUGIN}/acoustics/arena_bowl/spatial-audio.json", f"{PLUGIN}/acoustics/arena_bowl/source.audiobakedata",
            f"{PLUGIN}/acoustics/arena_bowl/room.assets_9.wav", f"{PLUGIN}/ai/bowl.ai.json",
            *(f"{PLUGIN}/loadscreens/arena_bowl_{n}.jpg" for n in (1, 2, 3, 4)),
            f"{PLUGIN}/material-shaders.arena_bowl.json", f"{SERVER}/db/arena_bowl/base.json",
            f"{SERVER}/banners/arena_bowl.jpg"})
        total = [f.rel for part in parts for f in part.files]
        self.assertEqual(len(total), len(set(total)))
        self.assertEqual(sorted(total), sorted(f.rel for f in self.scan()))

    def test_bowl_needs_its_required_files_but_not_a_fifth_loading_image(self):
        required = release.required_files(self.spec)["bowl"]
        self.assertIn(f"{PLUGIN}/material-shaders.arena_bowl.json", required)
        self.assertIn(f"{PLUGIN}/loadscreens/arena_bowl_1.jpg", required)
        for name in required:
            with self.subTest(name=name):
                data = (self.dist / name).read_bytes()
                (self.dist / name).unlink()
                try:
                    with self.assertRaises(release.PackagingError) as caught:
                        self.partition()
                    self.assertIn("incomplete dist", str(caught.exception))
                    self.assertIn(name, str(caught.exception))
                finally:
                    (self.dist / name).write_bytes(data)
        # A stray fifth image of a map is still classified (the rule is any numbered .jpg), the real guard against a fake
        # fifth Bowl image is validate_distribution.validate_art; Alt-style names remain unclassifiable.
        (self.dist / PLUGIN / "loadscreens/arena_bowl_Alt_1.jpg").write_bytes(b"\xff\xd8\xff")
        with self.assertRaises(release.ClassificationError):
            self.partition()


class RealDistSmokeTests(unittest.TestCase):
    def test_real_dist_classifies_completely(self):
        # The version does not matter for classification; the csproj agreement has its own test above. Only a
        # complete dist is classified (a server-only Release build leaves a partial one); once it is complete, any
        # unclassified, doubly classified or parked-map file is a hard failure.
        spec = release.load_spec(REPO, version="0.0.0")
        dist = REPO / "dist"
        missing = [name for names in release.required_files(spec).values() for name in names
                   if not (dist / name).is_file()]
        if missing:
            self.skipTest(f"dist/ is absent or incomplete ({len(missing)} required files missing, e.g. {missing[0]})")
        files = release.scan_dist(dist)
        parts = release.partition_dist(files, spec)  # raises on any unclassified / doubly classified / missing file
        self.assertEqual(sum(len(p.files) for p in parts), len(files))
        self.assertEqual(len({f.rel for p in parts for f in p.files}), len(files))
        from validate_map_descriptors import RELEASE_MAP_KEYS
        self.assertEqual([p.id for p in parts], ["core"] + [k.removeprefix("arena_") for k in RELEASE_MAP_KEYS])
        for part, key in zip(parts[1:], RELEASE_MAP_KEYS):
            names = {f.rel for f in part.files}
            self.assertIn(f"{SERVER}/db/{key}/base.json", names)
            self.assertIn(f"{PLUGIN}/bundles/{key}_scenes.bundle", names)
        self.assertEqual(spec.client_dll, f"{PLUGIN}/maschine-ArenaMapPort.Client.dll")
        self.assertEqual(spec.server_dll, f"{SERVER}/maschine-ArenaMapPort.Server.dll")
        print(f"\nreal dist: {len(files)} files, 0 unclassified, parts "
              + ", ".join(f"{p.id}={len(p.files)}" for p in parts), file=sys.stderr)


if __name__ == "__main__":
    unittest.main()
