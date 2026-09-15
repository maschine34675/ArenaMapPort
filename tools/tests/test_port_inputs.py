"""Real filesystem mutation controls for input-bound full-map receipts."""
from pathlib import Path
import json
import os
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'validate'))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'extract'))
import port_inputs as P
import validate_port as V


class PortInputTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.package = self.root / 'package'
        self.output = self.root / 'output'
        self.source = self.root / 'source'
        self.code = self.root / 'code'
        for directory in (self.package / 'scenes', self.package / 'meshes', self.output, self.source, self.code):
            directory.mkdir(parents=True, exist_ok=True)
        (self.package / 'manifest.json').write_text(json.dumps({'source': {'map_key': 'arena_test', 'arena_data_root': str(self.source)}}))
        (self.package / 'scenes/Test.json').write_text('{"value":1}')
        (self.package / 'meshes/mesh.bin').write_bytes(b'AMPM\x01\x00')
        (self.source / 'source.assets').write_bytes(b'source bytes')
        (self.source / 'source.assets.resS').write_bytes(b'stream bytes')
        (self.code / 'validator.py').write_text('raise ValueError("fail")\n')
        self.snapshot = self.output / P.SNAPSHOT_NAME
        self.discovery = patch.object(P, 'discover_groups', side_effect=self.groups)
        self.discovery.start()
        self.addCleanup(self.discovery.stop)

    def groups(self, package, output, repo, arena_paths=None):
        return ({'map_key': 'arena_test', 'package': str(self.package), 'output': str(self.output),
                 'python_version': 'test', 'dependencies': {'decoder': '1'}},
                {'package': P.tree_files(self.package), 'arena_source': P.tree_files(self.source) if arena_paths is None else arena_paths,
                 'repository_code': P.tree_files(self.code)})

    def capture(self):
        self.snapshot.write_text(json.dumps(P.create_snapshot(self.package, self.output)), encoding='utf-8')

    def test_unchanged_complete_snapshot_passes(self):
        self.capture()
        self.assertEqual(P.verify_snapshot(self.package, self.snapshot)['map_key'], 'arena_test')
        self.assertIsNotNone(P.discover_groups.call_args.kwargs['arena_paths'])

    def test_same_size_scene_or_mesh_edit_with_restored_mtime_fails(self):
        for relative, value in [('scenes/Test.json', b'{"value":2}'), ('meshes/mesh.bin', b'AMPM\x02\x00')]:
            with self.subTest(path=relative):
                self.capture()
                file = self.package / relative
                before = file.stat()
                file.write_bytes(value)
                os.utime(file, ns=(before.st_atime_ns, before.st_mtime_ns))
                with self.assertRaisesRegex(ValueError, 'changed.*package'):
                    P.verify_snapshot(self.package, self.snapshot)

    def test_deleted_and_added_package_files_fail(self):
        self.capture()
        file = self.package / 'scenes/extra.json'
        file.write_text('{}')
        with self.assertRaisesRegex(ValueError, 'added='):
            P.verify_snapshot(self.package, self.snapshot)
        file.unlink()
        (self.package / 'meshes/mesh.bin').unlink()
        with self.assertRaisesRegex(ValueError, 'missing='):
            P.verify_snapshot(self.package, self.snapshot)

    def test_source_stream_and_validator_changes_fail(self):
        for file in (self.source / 'source.assets', self.source / 'source.assets.resS', self.code / 'validator.py'):
            with self.subTest(path=file.name):
                self.capture()
                file.write_bytes(file.read_bytes() + b'changed')
                with self.assertRaisesRegex(ValueError, 'Validation inputs changed'):
                    P.verify_snapshot(self.package, self.snapshot)

    def test_added_validator_is_an_input(self):
        self.capture()
        (self.code / 'new_validator.py').write_text('pass')
        with self.assertRaisesRegex(ValueError, 'repository_code'):
            P.verify_snapshot(self.package, self.snapshot)

    def test_removed_group_or_entry_and_forged_digest_fail(self):
        for mutation in ('group', 'entry', 'digest'):
            self.capture()
            value = P.load(self.snapshot)
            if mutation == 'group':
                value['groups'].pop('arena_source')
            elif mutation == 'entry':
                value['groups']['package']['files'].pop()
            else:
                value['groups']['package']['sha256'] = '0' * 64
            self.snapshot.write_text(json.dumps(value))
            with self.assertRaises(ValueError):
                P.verify_snapshot(self.package, self.snapshot)

    def test_legacy_snapshot_and_receipt_fail_before_expensive_checks(self):
        self.snapshot.write_text('{"schema_version":1}')
        with self.assertRaisesRegex(ValueError, 'Legacy'):
            P.verify_snapshot(self.package, self.snapshot)
        (self.root / 'tools/maps').mkdir(parents=True)
        (self.root / 'work/build/test').mkdir(parents=True)
        (self.root / 'tools/maps/test.json').write_text('{"key":"arena_test"}')
        (self.root / 'work/build/test/full-map-validation.json').write_text('{"schema_version":1,"full_map":true}')
        with patch.object(V, 'REPO', self.root):
            with self.assertRaisesRegex(ValueError, 'legacy receipts'):
                V.verify('test')

    def test_repository_policy_excludes_mutable_generated_and_docs(self):
        repo = self.root / 'repo'
        dirs = ('tools/extract', 'tools/validate', 'tools/bundle', 'tools/preset', 'tools/server',
                'analysis/scripts', 'tools/gamerefs-closure', 'tools/gamerefs-fixup',
                'ArenaMapPort.Shared', 'ArenaMapPort.Client', 'ArenaMapPort.Server', 'tools/maps',
                'tools/extract/plans', 'UnityProject/Assets/ArenaMapPort/Editor/GameRefs',
                'UnityProject/Assets/ArenaMapPort/Shaders', 'UnityProject/Assets/ArenaImport',
                'UnityProject/Assets/ArenaMapPort/StandinShaders', 'tools/tests', 'docs')
        for name in dirs:
            (repo / name).mkdir(parents=True, exist_ok=True)
        wanted = repo / 'UnityProject/Assets/ArenaMapPort/Shaders/Terrain.shader'
        wanted.write_text('shader')
        for relative in ('UnityProject/Assets/ArenaMapPort/Editor/GameRefs/Derived.dll.meta',
                         'UnityProject/Assets/ArenaMapPort/Editor/GameRefs.meta',
                         'UnityProject/Assets/ArenaMapPort/StandinShaders.meta',
                         'UnityProject/Assets/ArenaImport/Mesh.asset',
                         'UnityProject/Assets/ArenaMapPort/StandinShaders/Generated.shader', 'tools/tests/test_fake.py', 'docs/note.md'):
            (repo / relative).write_text('not an input')
        self.assertEqual(P.repository_inputs(repo), [wanted.resolve()])

    def test_receipt_rejects_modified_snapshot_even_if_internal_digest_is_recomputed(self):
        (self.root / 'tools/maps').mkdir(parents=True)
        output = self.root / 'work/build/test'
        output.mkdir(parents=True)
        (self.root / 'tools/maps/test.json').write_text('{"key":"arena_test"}')
        bound = output / P.SNAPSHOT_NAME
        bound.write_text('{"schema_version":2,"groups":{}}')
        artifacts = [bound]
        for i in range(7):
            file = output / f'artifact{i}'
            file.write_bytes(b'unchanged')
            artifacts.append(file)
        files = [{'path': p.relative_to(self.root).as_posix(), 'sha256': P.file_entry(p)['sha256']} for p in artifacts]
        (output / 'full-map-validation.json').write_text(json.dumps({
            'schema_version': 2, 'full_map': True, 'map_key': 'arena_test', 'files': files}))
        bound.write_text('{"schema_version":2,"groups":{"forged":"recomputed digest"}}')
        with patch.object(V, 'REPO', self.root):
            with self.assertRaisesRegex(ValueError, 'Validated file changed'):
                V.verify('test')

    @unittest.skipUnless(os.name == 'nt', 'NTFS junction control')
    def test_package_junction_cannot_add_foreign_files(self):
        outside = self.root / 'outside'
        outside.mkdir()
        original = outside / 'protected.bin'
        original.write_bytes(b'unchanged outside payload')
        junction = self.package / 'linked'
        result = subprocess.run(['cmd', '/c', 'mklink', '/J', str(junction), str(outside)], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        try:
            with self.assertRaisesRegex(ValueError, 'junction'):
                P.tree_files(self.package)
            self.assertEqual(original.read_bytes(), b'unchanged outside payload')
        finally:
            # Remove only the junction itself; never traverse/delete its target.
            junction.rmdir()


if __name__ == '__main__':
    unittest.main()
