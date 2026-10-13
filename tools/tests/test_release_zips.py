"""The multi-part release packager: strict classification of dist/, size gate, compression choice, manifests,
checksums, verification and the extract-and-compare check. Everything runs on a fake dist tree in a temporary
directory (tiny files named like the real ones); only the classification smoke test reads the real dist/, and it
is skipped unless every file the release requires is staged. No bundle bytes are involved anywhere."""
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import random
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
    return "material-shaders.json" if key == "arena_equator" else f"material-shaders.{key}.json"


def git_env():
    """The test's own git calls must not inherit GIT_DIR / GIT_WORK_TREE from a hook or an IDE task."""
    return {k: v for k, v in os.environ.items() if not k.upper().startswith("GIT_")}


def fake_dist_files(maps=MAPS):
    """Tiny stand-ins named like the real dist: random bytes where the real file is incompressible (bundles,
    DLLs), repeated text elsewhere. AirPit has no flares / water / overview map, Bay5 alone has water."""
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
            files[f"{PLUGIN}/bundles/{key}_{kind}.bundle"] = rng.randbytes(4096)
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
                                or f"ArenaMapPort_{display}/" in f.rel
                                or f.rel == f"{PLUGIN}/material-shaders.json",
                                f"{f.rel} does not belong to {key}")

    def test_base_json_lands_with_its_bundles(self):
        for part, (key, _display) in zip(self.partition()[1:], MAPS):
            names = {f.rel for f in part.files}
            self.assertIn(f"{SERVER}/db/{key}/base.json", names)
            for kind in ("scenes", "navmesh", "preset"):
                self.assertIn(f"{PLUGIN}/bundles/{key}_{kind}.bundle", names)

    def test_legacy_shader_sidecar_belongs_to_equator_only(self):
        parts = {p.id: {f.rel for f in p.files} for p in self.partition()}
        self.assertIn(f"{PLUGIN}/material-shaders.json", parts["equator"])
        self.assertIn(f"{PLUGIN}/material-shaders.arena_bay5.json", parts["bay5"])
        self.assertIn(f"{PLUGIN}/material-shaders.arena_airpit.json", parts["airpit"])

    def test_unknown_files_are_rejected(self):
        for name in (f"{PLUGIN}/extra.txt", f"{PLUGIN}/maschine-ArenaMapPort.Client.pdb",
                     f"{SERVER}/Thumbs.db", f"{PLUGIN}/README.txt", f"{PLUGIN}/parts/core.json",
                     f"{PLUGIN}/bundles/arena_airpit_scenes.bundle.bak",
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
                  f"{PLUGIN}/bundles/arena_bowl_scenes.bundle", f"{SERVER}/db/arena_prison/base.json")
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
