#!/usr/bin/env python3
"""Fails a UnityFS bundle that has a data block with a compression flag whose stored size is not smaller than its
content (csize >= usize).

# Why

UnityPy's `chunk_based_compress` (used by `BundleFile.save(packer=...)` in tools/bundle/patch_monoscript_identity.py)
keeps a chunk raw only when the compressed form is LARGER than the chunk, so a 128 KiB chunk whose LZ4HC form is exactly
131072 bytes is written compressed, flag set, csize == usize. Unity's player evidently takes such a block for stored data and
never decodes it, so the raw LZ4 token stream reaches the consumer. In work/build/full/arena_equator_scenes.bundle (and
therefore in the released 0.3.0 bundle) block 6657 of 6943 held the tail of the AudioClip moll_indoor_wind_04: about
every 45 s a 4.5 s burst of static from the ambient player `indoor_wind (4)`, the intermittent Equator "Rauschen". It is
a per-rebuild lottery for any bundle; tools/bundle/fix_stored_blocks.py repairs a bundle (the build runs it as step 3b)
and patch_monoscript_identity.py now installs a corrected chunk compressor. This check is what would have caught the file
on 2026-10-24.

# What it reads

Only the UnityFS header and the block table (~27 KB compressed for the 577 MB Equator bundle), never the data blocks. A
plaintext bundle is read directly; an AMPB container (the distributed form, `--encrypted` or detected by its magic) is
read through tools/bundle/bundle_crypt.py (`read-range` of the header, then of the table), which needs the key store of
the build machine (default work/release-keys). tools/validate/validate_distribution.py runs the same check on every
bundle of dist/.

Usage:
    python tools/validate/validate_bundle_blocks.py work/build/full/arena_equator_scenes.bundle
    python tools/validate/validate_bundle_blocks.py --bundles-dir dist/BepInEx/plugins/maschine-ArenaMapPort/bundles
    python tools/validate/validate_bundle_blocks.py --encrypted dist/.../arena_equator_scenes.bundle --key-store work/release-keys

Prints one line per bundle (a FAIL line per defective block) and ends with BUNDLE_BLOCKS_OK or BUNDLE_BLOCKS_FAIL.
Exit code 0 when every bundle is clean, 1 when one is defective or cannot be read, 2 for a usage error.
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "bundle"))
import bundle_crypt  # noqa: E402 -- reads AMPB containers (the distributed bundles) through the C# tool
import fix_stored_blocks as blocks  # noqa: E402 -- the one UnityFS header and block table parser

TAG = "[validate_bundle_blocks]"


@dataclass
class Report:
    name: str
    block_count: int = 0
    problems: list[str] = field(default_factory=list)   # empty = clean


class _ToolReader:
    """Reads the PLAINTEXT of an AMPB container through the BundleCrypt tool (`read-range`, a read past the end is shortened)."""

    def __init__(self, path, key_store, crypt):
        self.path, self.key_store, self.crypt = path, key_store, crypt

    def read(self, offset: int, length: int) -> bytes:
        return self.crypt.read_range(self.path, offset, length, key_store=self.key_store, clip=True)


def read_header(path, key_store=None, encrypted=None, crypt=None) -> blocks.Header:
    """Header and block table of a bundle. `encrypted` None detects the AMPB magic. `crypt` replaces the bundle_crypt module (tests)."""
    crypt = bundle_crypt if crypt is None else crypt
    path = Path(path)
    if encrypted is None:
        encrypted = crypt.is_encrypted(path)
    if encrypted:
        return blocks.parse_header(_ToolReader(path, key_store, crypt))
    with path.open("rb") as stream:
        return blocks.parse_header(blocks.FileReader(stream))


def check_bundle(path, key_store=None, encrypted=None, crypt=None) -> Report:
    """Never raises for a defect or an unreadable bundle: they are the report's problems."""
    crypt = bundle_crypt if crypt is None else crypt
    report = Report(Path(path).name)
    try:
        header = read_header(path, key_store, encrypted, crypt)
    except blocks.BundleBlockError as exc:
        report.problems.append(f"cannot be checked: {exc}")
        return report
    except crypt.BundleCryptError as exc:
        report.problems.append(f"cannot be read through the BundleCrypt tool: {exc.message}")
        return report
    except OSError as exc:
        report.problems.append(f"cannot be read: {exc}")
        return report
    report.block_count = len(header.blocks)
    for block in blocks.scan_blocks(header):
        report.problems.append(f"{block.describe(report.block_count)}; Unity's player evidently reads it as stored data, so the raw "
                               f"LZ4 stream reaches the consumer")
    if header.table_bad:
        report.problems.append(f"the block table has the same defect (compression flag "
                               f"{blocks.COMPRESSION_NAMES.get(header.table_compression)}, csize {header.table_csize} >= "
                               f"usize {header.table_usize})")
    return report


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundles", nargs="*", type=Path, metavar="BUNDLE", help="UnityFS bundle(s), plaintext or AMPB containers")
    ap.add_argument("--bundles-dir", type=Path, default=None, help="also check every *.bundle in this directory")
    ap.add_argument("--encrypted", action="store_true",
                    help="read the bundles as AMPB containers through the BundleCrypt tool (otherwise detected by the magic)")
    ap.add_argument("--key-store", type=Path, default=None,
                    help="the build machine's key store for AMPB containers (default: work/release-keys)")
    args = ap.parse_args(argv)

    paths = list(args.bundles)
    if args.bundles_dir is not None:
        if not args.bundles_dir.is_dir():
            ap.error(f"--bundles-dir not found: {args.bundles_dir}")
        paths.extend(sorted(args.bundles_dir.glob("*.bundle")))
    if not paths:
        ap.error("no bundles given")

    failed = 0
    for path in paths:
        if not path.is_file():
            report = Report(path.name, problems=["no such file"])
        else:
            report = check_bundle(path, key_store=args.key_store, encrypted=True if args.encrypted else None)
        if report.problems:
            failed += 1
            for problem in report.problems:
                print(f"{TAG} FAIL {report.name}: {problem}")
        else:
            print(f"{TAG} ok   {report.name}: {report.block_count} blocks, none with a compression flag and csize >= usize")
    if failed:
        print(f"{TAG} repair a plaintext bundle with: python tools/bundle/fix_stored_blocks.py <bundle>, "
              f"then stage it again")
        print(f"BUNDLE_BLOCKS_FAIL {failed} of {len(paths)} bundle(s)")
        return 1
    print(f"BUNDLE_BLOCKS_OK {len(paths)} bundle(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
