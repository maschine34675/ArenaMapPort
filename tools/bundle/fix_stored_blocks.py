#!/usr/bin/env python3
"""tools/bundle/fix_stored_blocks.py -- repair UnityFS data blocks that carry a compression flag but are not smaller
than the data they hold.

# The defect (2026-10-29, the intermittent Equator "Rauschen")

A UnityFS bundle stores its data region as a list of blocks; the block table gives each block an uncompressed size
(usize), a stored size (csize) and flags whose low six bits name the compression (2 = LZ4, 3 = LZ4HC, 0 = stored).
UnityPy's `chunk_based_compress` (helpers/CompressionHelper.py, used by `BundleFile.save(packer=...)`, which is what
tools/bundle/patch_monoscript_identity.py calls in every build) keeps a chunk raw only when its compressed form is
LARGER than the chunk (`>` where `>=` is needed). A 128 KiB chunk whose LZ4HC form is exactly 131072 bytes is therefore
written compressed, with the flag set and csize == usize. Unity's player evidently takes such a block for stored data and
never decodes it (inferred from the data, not read from Unity's code), so the raw LZ4 token stream reaches the consumer: in
work/build/full/arena_equator_scenes.bundle, block
6657 of 6943 held the tail of the AudioClip moll_indoor_wind_04, and about every 45 s a 4.5 s burst of full-scale static
came out of the player `indoor_wind (4)`. Only that one block of the four maps' bundles is affected today, but it is a
per-rebuild lottery for any bundle. It was found on 2026-10-29 by following the burst in NoiseProbe captures back to the
bytes of the bundle.

# The repair

For every block with a compression flag and csize >= usize the tool LZ4-decodes the block (refusing a block that does
not decode to exactly usize bytes), stores the decoded bytes, clears the compression bits of the block's flags and sets
its csize to usize. The block table is then packed again with the header's own method (LZ4HC or LZ4; stored when the
packed form would not be smaller, which would be the same defect in the table itself) and the three header fields that
depend on it (file size, table sizes, flags) are rewritten. Every other byte of the file, the other blocks, the node
table and the uncompressed stream, is copied unchanged. For the defect above (csize == usize) the file keeps its size
when the packed table keeps its length, as it did for work/build/full/arena_equator_scenes.bundle (577,488,350 bytes).

The result is verified before it replaces anything: the new file is parsed again (same block count and sizes, same
nodes, no bad block left, header size equals file size), every repaired block holds exactly the LZ4-decoded bytes,
and every other block run and any trailing bytes are byte-identical to the source (streamed comparison).

# Usage

    python tools/bundle/fix_stored_blocks.py work/build/full/arena_equator_scenes.bundle            (in place, atomic)
    python tools/bundle/fix_stored_blocks.py --out work/tmp/fixed work/build/bay5/arena_bay5_scenes.bundle work/build/bay5/arena_bay5_navmesh.bundle
    python tools/bundle/fix_stored_blocks.py --check work/build/full/arena_equator_scenes.bundle     (report only)

In place, the tool writes `<name>.tmp` beside the file, verifies it and replaces the original with `os.replace`. A
clean bundle is left untouched (with --out it is copied through unchanged) so the tool can run on every build.
`--out DIR` writes the copies (clean bundles copied through) into DIR. The tool does not expand wildcards (a shell does: Git
Bash yes, PowerShell passes `*` on literally), so name the files. One line per defective block, then one result line per bundle:

    BLOCKS_OK <file> repaired=N        N blocks repaired (0 = nothing to do; with --check, none is defective)
    BLOCKS_FAIL <file>: <reason>       --check found a defective block, or the bundle was refused / verification failed

Exit code 0 when every bundle is OK, 1 otherwise, 2 for a usage error. Refused on purpose: a block table at the end
of the file (flags 0x80), an encrypted bundle (flag 0x200 on an engine before 2020.3.34 / 2021.3.2 / 2022.1.1, where it is
not the data padding, or 0x400 / 0x1000 on a later one), an encrypted AMPB container, a table or block compressed with
anything but LZ4/LZ4HC, and a flagged block that is not a valid LZ4 stream of usize bytes. The decode functions are importable (`parse_header`, `scan_blocks`, `repair`), the
validator tools/validate/validate_bundle_blocks.py and the tests use them.
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import struct
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

import lz4.block

COMPRESSION_MASK = 0x3F
NONE, LZMA, LZ4, LZ4HC = 0, 1, 2, 3
COMPRESSION_NAMES = {NONE: "none", LZMA: "lzma", LZ4: "lz4", LZ4HC: "lz4hc", 4: "lzham"}
FLAG_COMBINED = 0x40          # block table and node table share one blob (always set by Unity 2019.4+ and UnityPy)
FLAG_TABLE_AT_END = 0x80
FLAG_PADDED_DATA = 0x200      # BlockInfoNeedPaddingAtStart: the data region starts 16-aligned (from 2020.3.34 / 2021.3.2 / 2022.1.1 on)
FLAG_ENCRYPTED_OLD = 0x200    # UsesAssetBundleEncryption on the older engines (Unity CN): the same bit
FLAG_ENCRYPTED_NEW = 0x1400   # UsesAssetBundleEncryption from those engines on (UnityPy: 0x400 or 0x1000)
SIGNATURE = b"UnityFS\0"
HEADER_PROBE = 512            # the header (signature, version, two version strings, 20 bytes of sizes) is far smaller
MAX_TABLE_BYTES = 256 << 20   # a block table larger than this is not a bundle's
COPY_CHUNK = 8 << 20
COMPARE_CHUNK = 4 << 20
TABLE_HEAD = 20               # 16-byte hash + int32 block count; the block entries start here
BLOCK_ENTRY = 10              # uint32 usize, uint32 csize, uint16 flags (big endian)
TAG = "BLOCKS"


class BundleBlockError(Exception):
    """A bundle the tool refuses (unsupported layout, an undecodable block) or a repair that failed its verification."""


@dataclass(frozen=True)
class Block:
    index: int
    usize: int
    csize: int
    flags: int
    file_offset: int      # where the stored bytes of the block start in the file
    stream_offset: int    # where the block's decoded bytes start in the uncompressed stream

    @property
    def compression(self) -> int:
        return self.flags & COMPRESSION_MASK

    @property
    def bad(self) -> bool:
        """A compression flag on a block that is not smaller than its content: Unity's player evidently reads it as stored data.
        (A block with no content cannot be misread and is left alone.)"""
        return self.compression != NONE and self.usize > 0 and self.csize >= self.usize

    def describe(self, count: int | None = None) -> str:
        of = f" of {count}" if count is not None else ""
        return (f"block {self.index}{of}: {COMPRESSION_NAMES.get(self.compression, self.compression)} flag "
                f"{self.flags:#x}, csize {self.csize} >= usize {self.usize} (stream bytes {self.stream_offset}.."
                f"{self.stream_offset + self.usize}, file offset {self.file_offset})")


@dataclass(frozen=True)
class Header:
    version: int
    player: bytes
    engine: bytes
    size: int                 # the file size the header declares
    table_csize: int
    table_usize: int
    flags: int
    size_field_offset: int    # where the int64 file size sits in the file
    table_offset: int
    table: bytes              # the decoded block + node table
    data_offset: int
    blocks: tuple[Block, ...]
    nodes: tuple[tuple[int, int, int, bytes], ...]   # (offset, size, flags, name)
    prefix: bytes             # file bytes [0, table_offset): signature, strings, size fields, alignment

    @property
    def table_compression(self) -> int:
        return self.flags & COMPRESSION_MASK

    @property
    def table_bad(self) -> bool:
        """The block table itself carries the same defect (compression flag, stored size not smaller)."""
        return self.table_compression != NONE and self.table_usize > 0 and self.table_csize >= self.table_usize

    @property
    def table_hash(self) -> bytes:
        return self.table[:16]

    @property
    def data_end(self) -> int:
        return self.data_offset + sum(b.csize for b in self.blocks)


@dataclass
class Repair:
    blocks: list[Block] = field(default_factory=list)   # the defective blocks, as found in the source
    table: bool = False                                  # the block table was packed again
    size_before: int = 0
    size_after: int = 0

    @property
    def changed(self) -> bool:
        return bool(self.blocks) or self.table


class FileReader:
    """`read(offset, length)` on an open binary file; a read past the end is shortened like a file read."""

    def __init__(self, stream):
        self.stream = stream

    def read(self, offset: int, length: int) -> bytes:
        self.stream.seek(offset)
        return self.stream.read(length)


class MemoryReader:
    def __init__(self, data):
        self.data = data

    def read(self, offset: int, length: int) -> bytes:
        return bytes(self.data[offset:offset + length])


def _cstr(buf: bytes, pos: int) -> tuple[bytes, int]:
    end = buf.find(b"\0", pos)
    if end < 0:
        raise BundleBlockError("unterminated string in the UnityFS header")
    return bytes(buf[pos:end]), end + 1


def _header_aligned(version: int, engine: bytes) -> bool:
    """UnityPy's rule (BundleFile.read_fs): format 7+, or an engine 2019.4.15 and later that still says format 6."""
    if version >= 7:
        return True
    match = re.match(rb"(\d+)\.(\d+)\.(\d+)", engine)
    return bool(match) and int(match.group(1)) == 2019 and tuple(int(g) for g in match.groups()) >= (2019, 4, 15)


def _old_flag_scheme(engine: bytes):
    """UnityPy's rule (BundleFile.read_fs) for the meaning of flag 0x200: True before 2020, 2020.3.34, 2021.3.2 and 2022.1.1
    (there it marks an encrypted bundle), False from those engines on (there it is the data padding). None when the engine
    string carries no version."""
    match = re.match(rb"(\d+)\.(\d+)\.(\d+)", engine)
    if not match:
        return None
    version = tuple(int(g) for g in match.groups())
    return (version < (2020,) or (version[0] == 2020 and version < (2020, 3, 34))
            or (version[0] == 2021 and version < (2021, 3, 2)) or (version[0] == 2022 and version < (2022, 1, 1)))


def _align16(value: int) -> int:
    return (value + 15) & ~15


def _lz4_decode(raw: bytes, usize: int, what: str) -> bytes:
    try:
        out = lz4.block.decompress(raw, uncompressed_size=usize)
    except Exception as exc:  # lz4.block.LZ4BlockError, ValueError for an absurd size
        raise BundleBlockError(f"{what} is not a valid LZ4 stream of {usize} bytes ({exc})") from exc
    if len(out) != usize:
        raise BundleBlockError(f"{what} decodes to {len(out)} bytes, not {usize}")
    return out


def parse_header(reader) -> Header:
    """Parse the UnityFS header, the block table and the node table. `reader.read(offset, length)` is all that is
    needed, so a plain file, a byte string or a decrypting reader all work, and the data blocks are never read."""
    head = reader.read(0, HEADER_PROBE)
    if head[:8] != SIGNATURE:
        if head[:4] == b"AMPB":
            raise BundleBlockError("an encrypted AMPB container (the distributed form), not a plaintext UnityFS bundle: run the "
                                   "tool on the plaintext in work/build/<map>/ and stage it again "
                                   "(tools/validate/validate_bundle_blocks.py reads dist bundles through the BundleCrypt tool)")
        raise BundleBlockError("not a UnityFS bundle (the signature is missing)")
    try:
        pos = 8
        (version,) = struct.unpack_from(">I", head, pos)
        pos += 4
        player, pos = _cstr(head, pos)
        engine, pos = _cstr(head, pos)
        size_field_offset = pos
        size, table_csize, table_usize, flags = struct.unpack_from(">qIII", head, pos)
        pos += 20
    except struct.error as exc:
        raise BundleBlockError("the UnityFS header is cut off") from exc
    if _header_aligned(version, engine):
        pos = _align16(pos)
    if flags & FLAG_TABLE_AT_END:
        raise BundleBlockError("the block table is at the end of the file (flags 0x80), which this tool does not handle")
    old_scheme = _old_flag_scheme(engine)
    if flags & FLAG_ENCRYPTED_OLD and old_scheme is not False:
        raise BundleBlockError(f"flag 0x200 on engine {engine.decode('latin-1')!r}: before 2020.3.34 / 2021.3.2 / 2022.1.1 (or with no "
                               f"readable version) it marks an encrypted bundle, from those engines on the data padding; this tool "
                               f"handles only the second")
    if flags & FLAG_ENCRYPTED_NEW and old_scheme is not True:
        raise BundleBlockError(f"the bundle is encrypted by Unity (flags {flags:#x}), which this tool does not handle")
    if not flags & FLAG_COMBINED:
        raise BundleBlockError(f"the block table and the node table are not combined (flags {flags:#x} lack 0x40)")
    if table_csize > MAX_TABLE_BYTES or table_usize > MAX_TABLE_BYTES:
        raise BundleBlockError(f"the block table sizes ({table_csize}, {table_usize}) are not plausible")
    table_offset = pos
    stored = reader.read(table_offset, table_csize)
    if len(stored) != table_csize:
        raise BundleBlockError(f"the block table is cut off (wanted {table_csize} bytes at {table_offset}, got {len(stored)})")
    compression = flags & COMPRESSION_MASK
    if compression == NONE:
        if table_csize != table_usize:
            raise BundleBlockError(f"the stored block table has two sizes ({table_csize}, {table_usize})")
        table = bytes(stored)
    elif compression in (LZ4, LZ4HC):
        table = _lz4_decode(stored, table_usize, "the block table")
    else:
        raise BundleBlockError(f"the block table uses {COMPRESSION_NAMES.get(compression, compression)} compression, "
                               f"only lz4 and lz4hc are handled")
    try:
        (count,) = struct.unpack_from(">i", table, 16)
        if count < 0 or TABLE_HEAD + BLOCK_ENTRY * count + 4 > len(table):
            raise BundleBlockError(f"the block table claims {count} blocks and holds only {len(table)} bytes")
        data_offset = table_offset + table_csize
        if flags & FLAG_PADDED_DATA:
            data_offset = _align16(data_offset)
        blocks = []
        file_pos, stream_pos = data_offset, 0
        for index in range(count):
            usize, csize, bflags = struct.unpack_from(">IIH", table, TABLE_HEAD + BLOCK_ENTRY * index)
            blocks.append(Block(index, usize, csize, bflags, file_pos, stream_pos))
            file_pos += csize
            stream_pos += usize
        pos = TABLE_HEAD + BLOCK_ENTRY * count
        (node_count,) = struct.unpack_from(">i", table, pos)
        pos += 4
        if node_count < 0 or node_count > 1 << 20:
            raise BundleBlockError(f"the node table claims {node_count} nodes")
        nodes = []
        for _ in range(node_count):
            offset, node_size, node_flags = struct.unpack_from(">qqI", table, pos)
            name, pos = _cstr(table, pos + 20)
            nodes.append((offset, node_size, node_flags, name))
    except struct.error as exc:
        raise BundleBlockError("the block or node table is cut off") from exc
    return Header(version, player, engine, size, table_csize, table_usize, flags, size_field_offset, table_offset,
                  table, data_offset, tuple(blocks), tuple(nodes), bytes(head[:table_offset]))


def scan_blocks(header: Header) -> list[Block]:
    """The defective blocks of a parsed bundle: a compression flag and csize >= usize."""
    return [block for block in header.blocks if block.bad]


def _pack_table(table: bytes, compression: int) -> bytes:
    if compression == LZ4HC:
        return lz4.block.compress(table, mode="high_compression", compression=9, store_size=False)
    if compression == LZ4:
        return lz4.block.compress(table, mode="default", store_size=False)
    return table


def _read_exact(stream, offset: int, length: int, what: str) -> bytes:
    stream.seek(offset)
    data = stream.read(length)
    if len(data) != length:
        raise BundleBlockError(f"{what} is cut off (wanted {length} bytes at {offset}, got {len(data)})")
    return data


def _copy_range(src, src_offset: int, dst, length: int) -> None:
    src.seek(src_offset)
    left = length
    while left:
        chunk = src.read(min(COPY_CHUNK, left))
        if not chunk:
            raise BundleBlockError(f"the source ended {left} bytes early at {src_offset + length - left}")
        dst.write(chunk)
        left -= len(chunk)


def _same_range(a, a_offset: int, b, b_offset: int, length: int) -> bool:
    done = 0
    while done < length:
        n = min(COMPARE_CHUNK, length - done)
        a.seek(a_offset + done)
        b.seek(b_offset + done)
        x, y = a.read(n), b.read(n)
        if len(x) != n or x != y:
            return False
        done += n
    return True


def _verify(src_stream, header: Header, src_size: int, dst_path: Path, decoded: dict[int, bytes]) -> Header:
    """Parse the repaired file again and compare it with the source (see the module docstring). Returns its header."""
    dst_size = dst_path.stat().st_size
    with dst_path.open("rb") as dst_stream:
        new = parse_header(FileReader(dst_stream))
        if (new.version, new.player, new.engine) != (header.version, header.player, header.engine):
            raise BundleBlockError("verification: the version fields changed")
        if new.size != dst_size:
            raise BundleBlockError(f"verification: the header declares {new.size} bytes, the file has {dst_size}")
        if len(new.blocks) != len(header.blocks) or new.nodes != header.nodes or new.table_hash != header.table_hash:
            raise BundleBlockError("verification: the block count, the node table or the table hash changed")
        if scan_blocks(new) or new.table_bad:
            raise BundleBlockError("verification: a defective block or table is left")
        for old, now in zip(header.blocks, new.blocks):
            if now.usize != old.usize:
                raise BundleBlockError(f"verification: block {old.index} changed its uncompressed size")
            if old.index in decoded:
                if now.compression != NONE or now.csize != old.usize or now.flags != old.flags & ~COMPRESSION_MASK:
                    raise BundleBlockError(f"verification: block {old.index} is not stored with cleared flags")
            elif (now.csize, now.flags) != (old.csize, old.flags):
                raise BundleBlockError(f"verification: untouched block {old.index} changed its size or flags")
        if new.data_end > dst_size:
            raise BundleBlockError("verification: the blocks run past the end of the repaired file")
        # bytes: repaired blocks hold the decoded data, runs of untouched blocks are the source's, the tail too
        run = None   # (first block index, byte length) of the current untouched run
        for old, now in list(zip(header.blocks, new.blocks)) + [(None, None)]:
            if old is not None and old.index not in decoded:
                run = (run[0], run[1] + old.csize) if run else (old.index, old.csize)
                continue
            if run:
                first = run[0]
                if not _same_range(src_stream, header.blocks[first].file_offset, dst_stream,
                                   new.blocks[first].file_offset, run[1]):
                    raise BundleBlockError(f"verification: untouched blocks from {first} are not byte-identical")
                run = None
            if old is not None and _read_exact(dst_stream, now.file_offset, now.csize, f"repaired block {old.index}") != decoded[old.index]:
                raise BundleBlockError(f"verification: repaired block {old.index} is not the decoded source block")
        tail = src_size - header.data_end
        if dst_size - new.data_end != tail or (tail and not _same_range(src_stream, header.data_end, dst_stream, new.data_end, tail)):
            raise BundleBlockError("verification: the bytes after the last block changed")
    return new


def repair(src, dst) -> Repair:
    """Write a repaired copy of the bundle `src` to `dst` (a different file) and verify it. Nothing is written when the
    bundle is clean; the returned Repair says what was found (`changed` False). Raises BundleBlockError for a refused
    bundle or a failed verification (a partly written `dst` is left for the caller to remove)."""
    src, dst = Path(src), Path(dst)
    if _same_file(src, dst):
        raise ValueError("the repaired copy must be written to a different file than the source")
    with src.open("rb") as stream:
        header = parse_header(FileReader(stream))
        bad = scan_blocks(header)
        result = Repair(blocks=bad, table=header.table_bad, size_before=os.fstat(stream.fileno()).st_size)
        if not result.changed:
            result.size_after = result.size_before
            return result
        if header.data_end > result.size_before:
            raise BundleBlockError(f"the blocks run past the end of the file (they end at {header.data_end}, the file has "
                                   f"{result.size_before} bytes): truncated")
        decoded: dict[int, bytes] = {}
        for block in bad:
            if block.compression not in (LZ4, LZ4HC):
                raise BundleBlockError(f"block {block.index} uses {COMPRESSION_NAMES.get(block.compression, block.compression)} "
                                       f"compression, only lz4 and lz4hc can be decoded")
            raw = _read_exact(stream, block.file_offset, block.csize, f"block {block.index}")
            decoded[block.index] = _lz4_decode(raw, block.usize, f"block {block.index}")
        table = bytearray(header.table)
        for block in bad:
            struct.pack_into(">IIH", table, TABLE_HEAD + BLOCK_ENTRY * block.index, block.usize, block.usize,
                             block.flags & ~COMPRESSION_MASK)
        flags = header.flags
        packed = _pack_table(bytes(table), flags & COMPRESSION_MASK)
        if flags & COMPRESSION_MASK != NONE and len(packed) >= len(table):
            packed, flags = bytes(table), flags & ~COMPRESSION_MASK     # stored, not a flagged block-sized table
        prefix = bytearray(header.prefix)
        with dst.open("wb") as out:
            struct.pack_into(">qIII", prefix, header.size_field_offset, 0, len(packed), len(table), flags)
            out.write(prefix)
            out.write(packed)
            if flags & FLAG_PADDED_DATA:
                out.write(bytes(_align16(out.tell()) - out.tell()))
            run = None   # (source offset, length) of the untouched blocks waiting to be copied
            for block in header.blocks:
                if block.index in decoded:
                    if run:
                        _copy_range(stream, run[0], out, run[1])
                        run = None
                    out.write(decoded[block.index])
                else:
                    run = (run[0], run[1] + block.csize) if run else (block.file_offset, block.csize)
            if run:
                _copy_range(stream, run[0], out, run[1])
            _copy_range(stream, header.data_end, out, result.size_before - header.data_end)   # bytes after the last block
            result.size_after = out.tell()
            out.seek(header.size_field_offset)
            out.write(struct.pack(">q", result.size_after))
        _verify(stream, header, result.size_before, dst, decoded)
    return result


def _same_file(a: Path, b: Path) -> bool:
    try:
        return os.path.samefile(a, b)
    except OSError:
        return a.resolve() == b.resolve()


def _replace_with_retry(src: Path, dst: Path, attempts: int = 5, delay: float = 0.2) -> None:
    """os.replace, retried a few times: on Windows a scanner or a sync client may hold the target for a moment."""
    for attempt in range(attempts):
        try:
            os.replace(src, dst)
            return
        except PermissionError:
            if attempt == attempts - 1:
                raise
            time.sleep(delay)


def repair_file(src, out=None) -> Repair:
    """Repair the bundle `src` in place (`out` None) or into the file `out`. The result is written beside its target as
    `<name>.tmp`, verified and moved into place with an atomic replace, so a failure never leaves a half-written bundle.
    A clean bundle stays untouched; with `out` it is copied through unchanged."""
    src = Path(src)
    target = src if out is None else Path(out)
    tmp = target.with_name(target.name + ".tmp")
    try:
        result = repair(src, tmp)
        if result.changed:
            _replace_with_retry(tmp, target)
        elif not _same_file(src, target):
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(src, tmp)
            _replace_with_retry(tmp, target)
    finally:
        tmp.unlink(missing_ok=True)
    return result


def inspect_file(path) -> Header:
    with Path(path).open("rb") as stream:
        return parse_header(FileReader(stream))


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundles", nargs="+", type=Path, metavar="BUNDLE", help="plaintext UnityFS bundle(s)")
    ap.add_argument("--check", action="store_true",
                    help="report only: write nothing, exit 1 when a bundle has a defective block")
    ap.add_argument("--out", type=Path, metavar="DIR",
                    help="write the repaired copies (clean bundles copied through) into DIR instead of repairing in place")
    args = ap.parse_args(argv)
    if args.check and args.out:
        ap.error("--check writes nothing, so --out has no meaning with it")
    if args.out is not None:
        if args.out.exists() and not args.out.is_dir():
            ap.error(f"--out is not a directory: {args.out}")
        names = [p.name for p in args.bundles]
        if len(names) != len(set(names)):
            ap.error("two bundles have the same file name and would overwrite each other in --out")

    if args.out is not None and not args.check:
        args.out.mkdir(parents=True, exist_ok=True)
    failed = 0
    for path in args.bundles:
        try:
            if not path.is_file():
                raise BundleBlockError("no such file")
            if args.check:
                header = inspect_file(path)
                bad = scan_blocks(header)
                for block in bad:
                    print(f"  bad {block.describe(len(header.blocks))}")
                if header.table_bad:
                    print(f"  bad block table: {COMPRESSION_NAMES.get(header.table_compression)} flag, csize "
                          f"{header.table_csize} >= usize {header.table_usize}")
                if bad or header.table_bad:
                    raise BundleBlockError(f"{len(bad)} block(s) with a compression flag and csize >= usize: "
                                           f"{[b.index for b in bad]}" + (" and the block table" if header.table_bad else ""))
                print(f"{TAG}_OK {path} repaired=0")
                continue
            before = inspect_file(path)
            target = None if args.out is None else args.out / path.name
            result = repair_file(path, target)
            for block in result.blocks:
                print(f"  repaired {block.describe(len(before.blocks))}: decoded and stored, flags {block.flags:#x} -> "
                      f"{block.flags & ~COMPRESSION_MASK:#x}")
            if result.table and not result.blocks:
                print("  repacked the block table: it had a compression flag and was not smaller than its content")
            if result.changed:
                print(f"  wrote {target or path}: {result.size_after} bytes (was {result.size_before}, "
                      f"{result.size_after - result.size_before:+d}), verified")
            else:
                print(f"  no defective block, {'copied unchanged to ' + str(target) if target else 'left untouched'}")
            print(f"{TAG}_OK {path} repaired={len(result.blocks)}")
        except (BundleBlockError, OSError) as exc:
            failed += 1
            print(f"{TAG}_FAIL {path}: {exc}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
