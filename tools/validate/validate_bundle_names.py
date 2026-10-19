#!/usr/bin/env python3
"""Distribution-wide check (task label `fix-preset-cab-and-bundle-lifetime`, item 3): fails if two
`dist/` bundles of the SAME kind (preset/scenes/navmesh) -- or, defensively, of any kind -- share an
internal UnityFS directory-table name (a "CAB-..." or "BuildPlayer-..." entry).

# Root cause this guards against

Every one of this project's `*_preset.bundle` files is a patched copy of the same SPT donor,
`develop_preset.bundle` (`tools/preset/patch_preset.py`). Before `patch_preset.py`'s own
`fix-preset-cab-and-bundle-lifetime` fix, every preset bundle inherited that donor's single nested
SerializedFile's internal name verbatim (`CAB-db2b26ca214c6211c4fae58b5cbe8d61`), unchanged by the
patcher. Confirmed empirically against the shipped `dist/` bundles (UnityPy, 2026-09-16): all 14
`*_preset.bundle` files shared that exact name; every `*_scenes.bundle`/`*_navmesh.bundle` (Unity's
own build output, not hand-patched) already had unique names, derived from each map's own distinct
scene names / a fresh per-bake hash.

Unity's own `AssetBundle.LoadFromFile` refuses to load a SECOND bundle whose serialized file shares
an internal name with one already resident in the process -- reproduced by raiding `arena_bay5` then
`arena_equator` in the same client session (`D:/SPT41vanilla/BepInEx/LogOutput.log`, 19:50:19):

    The AssetBundle '...\\arena_equator_preset.bundle' can't be loaded because another AssetBundle
    with the same files is already loaded.
    [Error  :maschine-ArenaMapPort] [Bundles] AssetBundle.LoadFromFile returned null for '...arena_equator_preset.bundle'
    Exception: Can't load bundle: maps/arena_equator_preset.bundle HTTP/1.1 404 Not Found

Full writeup: docs/progress/fix-preset-cab-and-bundle-lifetime.md.

# How this reads bundles

`read_directory_names()` parses only the UnityFS primary header plus the (typically kilobyte-sized)
compressed block-info/directory blob -- the same data `UnityPy.files.BundleFile.read_fs()` reads
before it ever decompresses the actual (up to ~1.4 GB for this project's largest scenes bundles)
per-file data blocks. This makes scanning all 42 dist bundles take a fraction of a second instead of
however long a full `UnityPy.load()` of every scenes bundle would take. If this fast, independent
parse ever raises (an unexpected header shape, a compression codec it doesn't handle, ...), it is
never trusted to silently report "no duplicates" -- `read_directory_names_checked()` falls back to a
full `UnityPy.load()` for that one file and logs that it did so, rather than risk a false pass.

# Encrypted bundles (stage 2 of the Arena gate, docs/PLAN-ENCRYPTION.md)

The distributed bundles are AMPB containers, so the UnityFS bytes this parse needs are not in the file. Every read
goes through a small reader (`read(offset, length)`, a negative offset counts from the end): a plain file is read
directly, an AMPB container through tools/bundle/bundle_crypt.py (`read-range`, the one C# implementation, with the
content key from the key store, default work/release-keys). The parse needs exactly two ranges, the first ~100
bytes (the UnityFS header) and the blocks-info blob; the data blocks are never read, so a 1 GB scenes bundle costs
two tool calls. The fallback to a full UnityPy.load() decrypts to a temporary file under work/tmp first and removes it.

Usage:
    python tools/validate/validate_bundle_names.py
    python tools/validate/validate_bundle_names.py --bundles-dir dist/BepInEx/plugins/maschine-ArenaMapPort/bundles
    python tools/validate/validate_bundle_names.py --bundle some.bundle --bundle other.bundle
    python tools/validate/validate_bundle_names.py --key-store work/release-keys   (encrypted bundles; this is the default)

Exit code 0 + "OK" summary on success. Exit code 1 with a specific, actionable message (never a bare
stack trace) on any duplicate or failure, per SPEC.md SS B.WP6's acceptance criterion for validators.
"""
from __future__ import annotations

import argparse
import contextlib
import gc
import struct
import sys
import traceback
import uuid
from pathlib import Path
from typing import NoReturn

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "bundle"))
import bundle_crypt  # noqa: E402 -- reads AMPB containers (the distributed bundles) through the C# tool

DEFAULT_BUNDLES_DIR = REPO / "dist" / "BepInEx" / "plugins" / "maschine-ArenaMapPort" / "bundles"
TMP_DIR = REPO / "work" / "tmp"

# Unity's own bit, ArchiveFlags.BlocksInfoAtTheEnd (UnityPy.enums.BundleFile.ArchiveFlags) --
# duplicated here as a bare int rather than importing the enum so this reader stays a fully
# independent, from-first-bytes re-implementation (same spirit as
# tools/bundle/patch_monoscript_identity.py's own parse_unityfs_header()).
_BLOCKS_INFO_AT_THE_END = 0x80
_COMPRESSION_TYPE_MASK = 0x3F


def die(msg: str) -> NoReturn:
    print(f"[validate_bundle_names] FATAL: {msg}", file=sys.stderr)
    sys.exit(1)


def _read_cstr(buf: bytes, offset: int) -> tuple[str, int]:
    end = buf.index(0, offset)
    return buf[offset:end].decode("utf-8", errors="replace"), end + 1


def _decompress(comp_flag: int, data: bytes, uncompressed_size: int) -> bytes:
    from UnityPy.helpers import CompressionHelper
    from UnityPy.enums.BundleFile import CompressionFlags

    fn = CompressionHelper.DECOMPRESSION_MAP.get(CompressionFlags(comp_flag))
    if fn is None:
        raise NotImplementedError(f"no decompressor registered for compression flag {comp_flag}")
    return fn(data, uncompressed_size)


class _FileReader:
    """Reads a plain (unencrypted) bundle: `read(offset, length)`, a negative offset counts from the end."""

    def __init__(self, path: Path):
        self.path = path

    def read(self, offset: int, length: int) -> bytes:
        with self.path.open("rb") as fh:
            if offset < 0:
                fh.seek(offset, 2)
            else:
                fh.seek(offset)
            return fh.read(length)


class _ToolReader:
    """Reads the PLAINTEXT of an AMPB container through the BundleCrypt tool (`read-range`; a range that reaches past the
    end is shortened, like a file read). One tool call per read; the data blocks of the bundle are never requested."""

    def __init__(self, path: Path, key_store):
        self.path = path
        self.key_store = key_store

    def read(self, offset: int, length: int) -> bytes:
        return bundle_crypt.read_range(self.path, offset, length, key_store=self.key_store, clip=True)


def open_reader(path: Path, key_store=None):
    """The reader for a bundle: the tool for an AMPB container (the distributed form), the file itself otherwise."""
    if bundle_crypt.is_encrypted(path):
        return _ToolReader(path, key_store)
    return _FileReader(path)


def read_directory_names(path: Path, key_store=None, reader=None) -> list[str]:
    """Returns every nested file's directory-table name (the UnityFS "path" field -- what UnityPy
    itself later keys `BundleFile.files` by, and what shows up as e.g. 'CAB-...' for a preset
    bundle or 'BuildPlayer-...' for a built scenes/navmesh bundle) WITHOUT decompressing the
    bundle's actual per-file data blocks. Raises on any unsupported/unexpected shape -- callers
    that need a guaranteed answer should use read_directory_names_checked() instead, which falls
    back to a full (slow, but authoritative) UnityPy.load() on any exception here.
    An AMPB container is read through the tool (see the module docstring); `key_store` is its key folder
    (None = work/release-keys). `reader` replaces the choice (tests)."""
    reader = open_reader(path, key_store) if reader is None else reader
    header = reader.read(0, 64)
    off = 0
    signature, off = _read_cstr(header, off)
    if signature != "UnityFS":
        raise NotImplementedError(f"unsupported bundle signature {signature!r} (only UnityFS is handled)")
    fmt = struct.unpack_from(">I", header, off)[0]; off += 4
    _ver_player, off = _read_cstr(header, off)
    _ver_engine, off = _read_cstr(header, off)

    need = off + 20  # size(8) + compressedSize(4) + uncompressedSize(4) + dataflags(4)
    if need > len(header):
        header = reader.read(0, need + 64)
    _file_size = struct.unpack_from(">q", header, off)[0]; off += 8
    compressed_size = struct.unpack_from(">I", header, off)[0]; off += 4
    uncompressed_size = struct.unpack_from(">I", header, off)[0]; off += 4
    dataflags = struct.unpack_from(">I", header, off)[0]; off += 4

    # Mirrors UnityPy.files.BundleFile.BundleFile.read_fs()'s own
    # `if self.version >= 7: reader.align_stream(16)` (the >=2019.4.15-old-Unity carve-out
    # does not apply to this project's single, current 2022.3.43f1 toolchain).
    if fmt >= 7:
        pad = (16 - (off % 16)) % 16
        off += pad
    start = off

    if dataflags & _BLOCKS_INFO_AT_THE_END:
        blocks_info_bytes = reader.read(-compressed_size, compressed_size)
    else:
        blocks_info_bytes = reader.read(start, compressed_size)
    if len(blocks_info_bytes) != compressed_size:
        raise ValueError(f"short read of block-info blob: wanted {compressed_size}, got {len(blocks_info_bytes)}")

    comp_flag = dataflags & _COMPRESSION_TYPE_MASK
    blocks_info_bytes = _decompress(comp_flag, blocks_info_bytes, uncompressed_size)

    bi_off = 16  # uncompressedDataHash
    blocks_info_count = struct.unpack_from(">i", blocks_info_bytes, bi_off)[0]; bi_off += 4
    if blocks_info_count < 0:
        raise ValueError(f"negative blocksInfoCount {blocks_info_count}")
    bi_off += blocks_info_count * (4 + 4 + 2)  # uncompressedSize(u32) compressedSize(u32) flags(u16)

    nodes_count = struct.unpack_from(">i", blocks_info_bytes, bi_off)[0]; bi_off += 4
    if nodes_count < 0:
        raise ValueError(f"negative directory nodesCount {nodes_count}")
    names: list[str] = []
    for _ in range(nodes_count):
        bi_off += 8 + 8 + 4  # offset(i64) size(i64) flags(u32)
        name, bi_off = _read_cstr(blocks_info_bytes, bi_off)
        names.append(name)
    return names


@contextlib.contextmanager
def _plaintext_copy(path: Path, key_store):
    """The path UnityPy can load: the bundle itself, or for an AMPB container a decrypted copy under work/tmp that
    is removed again whatever happens."""
    if not bundle_crypt.is_encrypted(path):
        yield path
        return
    TMP_DIR.mkdir(parents=True, exist_ok=True)
    copy = TMP_DIR / f"validate_bundle_names-{uuid.uuid4().hex}-{path.name}"
    try:
        bundle_crypt.decrypt(path, copy, key_store=key_store)
        yield copy
    finally:
        copy.unlink(missing_ok=True)


def read_directory_names_checked(path: Path, key_store=None) -> list[str]:
    """read_directory_names(), but never silently trusts a parse it isn't sure of: any exception
    falls back to a full UnityPy.load() (authoritative, just far slower for a large bundle) rather
    than risk this validator reporting a false 'no duplicates' pass. For an AMPB container the
    fallback first decrypts to a temporary file under work/tmp (removed afterwards)."""
    try:
        return read_directory_names(path, key_store)
    except bundle_crypt.BundleCryptError as e:
        # The container itself cannot be read (no key in the store, a damaged file, the tool is missing): a UnityPy load
        # of a decrypted copy would fail the same way, so this is the actionable answer, not a parse shape to fall back from.
        die(f"{path.name} cannot be read through the BundleCrypt tool: {e.message}")
    except Exception as e:  # noqa: BLE001 -- deliberately broad: any failure here must fall back, not propagate
        print(f"[validate_bundle_names] fast directory-info parse failed for {path.name} "
              f"({type(e).__name__}: {e}) -- falling back to a full UnityPy.load() for this file", file=sys.stderr)
        import UnityPy
        from UnityPy.files.SerializedFile import SerializedFile

        with _plaintext_copy(path, key_store) as loadable:
            env = UnityPy.load(str(loadable))
            bundle_file = None
            try:
                if len(env.files) != 1:
                    raise RuntimeError(f"expected exactly 1 top-level file in {path}, found {len(env.files)}") from e
                bundle_file = next(iter(env.files.values()))
                names = [name for name, f in bundle_file.files.items() if isinstance(f, SerializedFile) or hasattr(f, "name")]
            finally:
                # UnityPy keeps the file open until its Environment is collected, and Windows will not delete an open
                # file: let go of it so a decrypted temporary copy can be removed.
                env = bundle_file = None
                gc.collect()
            return names


def check_unique_names(bundle_paths: list[Path], key_store=None) -> list[str]:
    """Returns a list of human-readable failure messages (empty == all internal names distinct
    across every given bundle). Checked across ALL kinds together (not just preset-vs-preset) --
    scenes/navmesh bundles are independently confirmed unique today (see module docstring), but a
    cross-kind collision would produce the exact same "another AssetBundle with the same files is
    already loaded" failure, so it is not treated as structurally impossible here."""
    owners: dict[str, list[str]] = {}
    for p in bundle_paths:
        for name in read_directory_names_checked(p, key_store):
            owners.setdefault(name, []).append(p.name)

    failures = []
    for name, files in sorted(owners.items()):
        unique_files = sorted(set(files))
        if len(unique_files) > 1:
            failures.append(
                f"internal name {name!r} is shared by {len(unique_files)} dist bundles: {unique_files} "
                f"-- Unity refuses to load the second one while the first is still resident in the "
                f"same client session (see LogOutput.log evidence in this script's module docstring)"
            )
    return failures


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bundles-dir", type=Path, default=None,
                     help=f"directory of *.bundle files to scan (default: {DEFAULT_BUNDLES_DIR})")
    ap.add_argument("--bundle", action="append", type=Path, default=None,
                     help="an explicit bundle path to include (repeatable); combines with "
                          "--bundles-dir if both are given")
    ap.add_argument("--key-store", type=Path, default=None,
                     help="the build machine's key store, needed for encrypted (AMPB) bundles "
                          "(default: work/release-keys)")
    args = ap.parse_args()

    bundle_paths: list[Path] = []
    bundles_dir = args.bundles_dir
    if bundles_dir is None and not args.bundle:
        bundles_dir = DEFAULT_BUNDLES_DIR
    if bundles_dir is not None:
        if not bundles_dir.is_dir():
            die(f"--bundles-dir not found: {bundles_dir}")
        bundle_paths.extend(sorted(bundles_dir.glob("*.bundle")))
    if args.bundle:
        bundle_paths.extend(args.bundle)
    for p in bundle_paths:
        if not p.is_file():
            die(f"bundle not found: {p}")
    if not bundle_paths:
        die("no *.bundle files found to check")

    print(f"[validate_bundle_names] scanning {len(bundle_paths)} bundle(s) for internal name collisions ...")
    failures = check_unique_names(bundle_paths, args.key_store)

    print()
    if failures:
        print(f"[validate_bundle_names] {len(failures)} collision(s) FOUND:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print(f"[validate_bundle_names] OK -- {len(bundle_paths)} bundle(s), every internal directory "
          f"name is unique")


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:  # noqa: BLE001
        print("[validate_bundle_names] FATAL: unexpected exception", file=sys.stderr)
        traceback.print_exc()
        sys.exit(1)
