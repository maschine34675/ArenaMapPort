"""refresh_native_volumes.py leaves a package without Tex3D textures byte-identical (PLAN-AIRPIT item 5).

Why: the AirPit package holds 23 Cube + 782 Tex2D and no Texture3D, so the focused volume refresh the
extraction recipe ends with has nothing to do. It used to re-serialise ``manifest.json`` anyway, which
added one trailing CRLF that a fresh extraction does not write (extract_map.py's write_json ends without
a newline); validate_extract compares manifests as JSON and never saw it, a byte-level hash would.
Equator and Bay5 each hold one Tex3D and must keep taking the refresh path, so the second test pins that
a manifest with a Tex3D entry is NOT short-circuited. Offline: temporary packages only, no Arena source.
"""
from __future__ import annotations

import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "extract"))
import refresh_native_volumes as RV  # noqa: E402


def write_manifest(package: Path, textures: dict, arena_root: Path) -> bytes:
    manifest = {"source": {"arena_data_root": str(arena_root)}, "textures": textures}
    # Same byte shape as a fresh extraction on Windows: CRLF, indent 2, sorted keys, no trailing newline.
    raw = json.dumps(manifest, indent=2, sort_keys=True).replace("\n", "\r\n").encode("utf-8")
    (package / "manifest.json").write_bytes(raw)
    return raw


def quiet(call, *args):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        result = call(*args)
    return result, out.getvalue()


class RefreshNativeVolumesTests(unittest.TestCase):
    def test_package_without_tex3d_is_a_byte_for_byte_no_op(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp)
            textures = {"a" * 16: {"dimension": "Tex2D", "file": "textures/a.png"},
                        "b" * 16: {"dimension": "Cube", "file": "textures/b.png"}}
            before = write_manifest(package, textures, package / "no-such-arena-root")
            count, out = quiet(RV.refresh, package)
            self.assertEqual(count, 0)
            self.assertEqual((package / "manifest.json").read_bytes(), before)
            self.assertFalse(before.endswith(b"\n"))
            self.assertIn("NATIVE_VOLUMES_REFRESHED count=0", out)
            self.assertEqual(sorted(p.name for p in package.iterdir()), ["manifest.json"])

    def test_package_without_any_textures_is_also_a_no_op(self):
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp)
            before = write_manifest(package, {}, package / "no-such-arena-root")
            self.assertEqual(quiet(RV.refresh, package)[0], 0)
            self.assertEqual((package / "manifest.json").read_bytes(), before)

    def test_a_tex3d_entry_still_goes_through_the_refresh_path(self):
        # Equator/Bay5 shape: one Tex3D. The source cannot supply it here, so the refresh must fail closed
        # (not return early) and must not touch the manifest.
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp)
            arena_root = package / "empty-arena-root"
            arena_root.mkdir()
            textures = {"c" * 16: {"dimension": "Tex3D", "file": "textures/c.png"},
                        "d" * 16: {"dimension": "Tex2D", "file": "textures/d.png"}}
            before = write_manifest(package, textures, arena_root)
            with self.assertRaisesRegex(ValueError, "Missing source volumes"):
                quiet(RV.refresh, package)
            self.assertEqual((package / "manifest.json").read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
