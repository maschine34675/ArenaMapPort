"""The ArenaMapPort grass pack, format version 1 ("AMGP"): the plaintext of the per-map staged grass bundle.

Pure Python, standard library only (no UnityPy, no numpy), so the validators and the tests can import it. The writer (build) and the reader (parse) are the Python half of a contract whose C#
half is the client's grass pack reader; the bytes of a tiny synthetic pack are pinned in both (work/tmp/grass2/handoff/pack.md, tools/tests/test_grass_pack.py).

Layout (little-endian):
  offset 0   4 bytes   ASCII "AMGP"
  offset 4   uint32    version = 1
  offset 8   uint32    headerSize = 272 (the first payload starts here)
  offset 12  uint32    entryCount = 4
  offset 16  32 bytes  map key in ASCII, zero-padded
  offset 48  4 entries of 56 bytes each, in the fixed order of kinds 1, 2, 3, 4:
     +0  uint32 kind      1 placement records, 2 albedo array (GrassTexArray), 3 normal array (GrassNormalArray), 4 normal texture (Grass_new_1_N)
     +4  uint32 format    0 records, 1 DXT1 (BC1, 8 bytes per 4x4 block), 5 DXT5 (BC3, 16 bytes per block)
     +8  uint32 width     (placement: the record size in bytes, 68)
     +12 uint32 height    (placement: the record count)
     +16 uint32 slices    (placement: the slice count of the albedo array; kind 4: 1)
     +20 uint32 mips      (placement: 0)
     +24 uint32 filterMode  +28 uint32 wrapMode  +32 uint32 aniso   (Unity's enum values as Arena serialized them; 0 for the placement)
     +36 uint32 reserved = 0
     +40 uint64 offset    absolute, from the start of the file
     +48 uint64 size
Payloads follow the header contiguously in entry order with no padding. A placement payload is Arena's .pcl file byte for byte (68-byte records: an int32 slice index and a column-major
4x4 float matrix). A texture payload is Arena's stream range byte for byte: slice-major, the whole mip chain of a slice from mip 0 down to the last mip, a mip smaller than one 4x4 block
padded to one block.

What parse() refuses (each a GrassPackError with a stable `code`): TooShort, BadMagic, BadVersion, BadHeaderSize, BadEntryCount, BadMapKey, MapKeyMismatch, KindOrder, BadFormat,
ReservedNotZero, BadPlacementShape, BadTextureShape, SizeMismatch, NotContiguous, FileLength, Inconsistent (the placement's slice count or a normal array's slice count differs from the
albedo's). The reader does not look at floats; finite numbers, affine matrices and slice indices below the slice count are the extractor's checks (extract_grass.py) and the client's.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from typing import Dict, List, Optional

MAGIC = b"AMGP"
VERSION = 1
HEADER_SIZE = 272
ENTRY_COUNT = 4
ENTRY_SIZE = 56
MAP_KEY_FIELD = 32
FIRST_ENTRY_AT = 48
RECORD_SIZE = 68

KIND_PLACEMENT = 1
KIND_ALBEDO = 2
KIND_NORMAL_ARRAY = 3
KIND_NORMAL_TEXTURE = 4
KINDS = (KIND_PLACEMENT, KIND_ALBEDO, KIND_NORMAL_ARRAY, KIND_NORMAL_TEXTURE)
KIND_NAMES = {KIND_PLACEMENT: "placement", KIND_ALBEDO: "albedo", KIND_NORMAL_ARRAY: "normal_array", KIND_NORMAL_TEXTURE: "normal_texture"}

FORMAT_RECORDS = 0
FORMAT_DXT1 = 1
FORMAT_DXT5 = 5
KIND_FORMAT = {KIND_PLACEMENT: FORMAT_RECORDS, KIND_ALBEDO: FORMAT_DXT5, KIND_NORMAL_ARRAY: FORMAT_DXT1, KIND_NORMAL_TEXTURE: FORMAT_DXT1}
BLOCK_BYTES = {FORMAT_DXT1: 8, FORMAT_DXT5: 16}

_HEADER = struct.Struct("<4sIII32s")
_ENTRY = struct.Struct("<IIIIIIIIIIQQ")
assert _HEADER.size == FIRST_ENTRY_AT and _ENTRY.size == ENTRY_SIZE and FIRST_ENTRY_AT + ENTRY_COUNT * ENTRY_SIZE == HEADER_SIZE


class GrassPackError(Exception):
    """A pack that is not exactly the format. `code` is one of the names in the module docstring."""

    def __init__(self, code: str, message: str = ""):
        super().__init__(code + (": " + message if message else ""))
        self.code = code


# ---- the mip-chain arithmetic (the same rule as GrassArtRange in NativeGrassArtModel.cs) ----------------------------------------------------------------------------------------

def mip_bytes(width: int, height: int, mip: int, block_bytes: int) -> int:
    """Bytes of one mip of one slice: a mip below 4 pixels still occupies one whole 4 x 4 block."""
    w = max(1, width >> mip)
    h = max(1, height >> mip)
    return max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * block_bytes


def slice_bytes(width: int, height: int, mips: int, block_bytes: int) -> int:
    """Bytes of one slice's whole chain, mip 0 first."""
    return sum(mip_bytes(width, height, m, block_bytes) for m in range(mips))


def texture_bytes(width: int, height: int, slices: int, mips: int, block_bytes: int) -> int:
    return slices * slice_bytes(width, height, mips, block_bytes)


def max_mips(width: int, height: int) -> int:
    """The longest chain a texture of this size has (down to 1 x 1)."""
    return max(width, height).bit_length()


# ---- the writer -----------------------------------------------------------------------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class TexFacts:
    """What a texture entry carries besides its bytes. filter / wrap / aniso are Unity's enum values as Arena serialized them."""
    width: int
    height: int
    slices: int
    mips: int
    filter_mode: int
    wrap_mode: int
    aniso: int


@dataclass(frozen=True)
class Entry:
    kind: int
    format: int
    width: int
    height: int
    slices: int
    mips: int
    filter_mode: int
    wrap_mode: int
    aniso: int
    reserved: int
    offset: int
    size: int

    @property
    def name(self) -> str:
        return KIND_NAMES.get(self.kind, str(self.kind))


@dataclass
class GrassPack:
    version: int
    map_key: str
    entries: List[Entry]
    data: memoryview

    def entry(self, kind: int) -> Entry:
        return self.entries[KINDS.index(kind)]

    def payload(self, kind: int) -> memoryview:
        e = self.entry(kind)
        return self.data[e.offset:e.offset + e.size]

    @property
    def record_count(self) -> int:
        return self.entry(KIND_PLACEMENT).height


def _facts(value) -> TexFacts:
    if isinstance(value, TexFacts):
        return value
    if isinstance(value, dict):
        return TexFacts(**value)
    return TexFacts(*value)


def _encode_key(map_key: str) -> bytes:
    try:
        raw = map_key.encode("ascii")
    except UnicodeEncodeError:
        raise GrassPackError("BadMapKey", "the map key is not ASCII")
    if not raw or len(raw) > MAP_KEY_FIELD or any(not 0x21 <= c <= 0x7E for c in raw):
        raise GrassPackError("BadMapKey", "the map key must be 1 to 32 printable ASCII characters without spaces")
    return raw.ljust(MAP_KEY_FIELD, b"\0")


def build(map_key: str, placement_bytes: bytes, albedo: bytes, normal_array: bytes, normal_texture: bytes, facts: Dict[str, object]) -> bytes:
    """The pack's bytes. `facts` maps "albedo", "normal_array" and "normal_texture" to a TexFacts (or a dict / tuple of its seven fields). The result is parsed again before it is returned,
    so a writer that produced a pack the reader refuses raises here. Deterministic: the same inputs give the same bytes."""
    if len(placement_bytes) % RECORD_SIZE:
        raise GrassPackError("BadPlacementShape", "the placement size %d is not a multiple of %d" % (len(placement_bytes), RECORD_SIZE))
    count = len(placement_bytes) // RECORD_SIZE
    fa, fn, ft = _facts(facts["albedo"]), _facts(facts["normal_array"]), _facts(facts["normal_texture"])
    rows = [
        (KIND_PLACEMENT, FORMAT_RECORDS, RECORD_SIZE, count, fa.slices, 0, 0, 0, 0, 0, bytes(placement_bytes)),
        (KIND_ALBEDO, FORMAT_DXT5, fa.width, fa.height, fa.slices, fa.mips, fa.filter_mode, fa.wrap_mode, fa.aniso, 0, bytes(albedo)),
        (KIND_NORMAL_ARRAY, FORMAT_DXT1, fn.width, fn.height, fn.slices, fn.mips, fn.filter_mode, fn.wrap_mode, fn.aniso, 0, bytes(normal_array)),
        (KIND_NORMAL_TEXTURE, FORMAT_DXT1, ft.width, ft.height, ft.slices, ft.mips, ft.filter_mode, ft.wrap_mode, ft.aniso, 0, bytes(normal_texture)),
    ]
    out = bytearray(_HEADER.pack(MAGIC, VERSION, HEADER_SIZE, ENTRY_COUNT, _encode_key(map_key)))
    offset = HEADER_SIZE
    for kind, fmt, w, h, s, m, fi, wr, an, res, payload in rows:
        out += _ENTRY.pack(kind, fmt, w, h, s, m, fi, wr, an, res, offset, len(payload))
        offset += len(payload)
    for row in rows:
        out += row[-1]
    result = bytes(out)
    parse(result, expected_map_key=map_key)
    return result


# ---- the reader -----------------------------------------------------------------------------------------------------------------------------------------------------------------

def parse(data, expected_map_key: Optional[str] = None) -> GrassPack:
    """Reads a pack and refuses anything that is not exactly the format (see the module docstring for the codes). `expected_map_key` is the map the pack is opened for."""
    view = memoryview(data)
    if view.nbytes < HEADER_SIZE:
        raise GrassPackError("TooShort", "%d bytes, the header alone is %d" % (view.nbytes, HEADER_SIZE))
    magic, version, header_size, entry_count, key_raw = _HEADER.unpack_from(view, 0)
    if magic != MAGIC:
        raise GrassPackError("BadMagic")
    if version != VERSION:
        raise GrassPackError("BadVersion", str(version))
    if header_size != HEADER_SIZE:
        raise GrassPackError("BadHeaderSize", str(header_size))
    if entry_count != ENTRY_COUNT:
        raise GrassPackError("BadEntryCount", str(entry_count))
    end = key_raw.find(b"\0")
    key_bytes = key_raw if end < 0 else key_raw[:end]
    if end >= 0 and any(key_raw[end:]):
        raise GrassPackError("BadMapKey", "bytes after the terminating zero")
    if not key_bytes or any(not 0x21 <= c <= 0x7E for c in key_bytes):
        raise GrassPackError("BadMapKey")
    map_key = key_bytes.decode("ascii")
    if expected_map_key is not None and map_key != expected_map_key:
        raise GrassPackError("MapKeyMismatch", "the pack is for '%s', opened for '%s'" % (map_key, expected_map_key))

    entries: List[Entry] = []
    for i in range(ENTRY_COUNT):
        entries.append(Entry(*_ENTRY.unpack_from(view, FIRST_ENTRY_AT + i * ENTRY_SIZE)))
    for i, e in enumerate(entries):
        if e.kind != KINDS[i]:
            raise GrassPackError("KindOrder", "entry %d has kind %d, expected %d" % (i, e.kind, KINDS[i]))
        if e.format != KIND_FORMAT[e.kind]:
            raise GrassPackError("BadFormat", "%s has format %d, expected %d" % (e.name, e.format, KIND_FORMAT[e.kind]))
        if e.reserved != 0:
            raise GrassPackError("ReservedNotZero", e.name)

    placement = entries[0]
    if placement.width != RECORD_SIZE:
        raise GrassPackError("BadPlacementShape", "record size %d, expected %d" % (placement.width, RECORD_SIZE))
    if placement.height < 1:
        raise GrassPackError("BadPlacementShape", "no records")
    if placement.mips != 0 or (placement.filter_mode, placement.wrap_mode, placement.aniso) != (0, 0, 0):
        raise GrassPackError("BadPlacementShape", "mips, filter, wrap and aniso of the placement must be 0")
    if placement.size != RECORD_SIZE * placement.height:
        raise GrassPackError("SizeMismatch", "placement size %d is not %d x %d" % (placement.size, RECORD_SIZE, placement.height))
    if not 1 <= placement.slices <= 1024:
        raise GrassPackError("BadPlacementShape", "slice count %d" % placement.slices)

    for e in entries[1:]:
        if e.width < 1 or e.height < 1 or e.slices < 1 or e.mips < 1 or e.width > 16384 or e.height > 16384 or e.slices > 1024:
            raise GrassPackError("BadTextureShape", "%s %dx%d slices=%d mips=%d" % (e.name, e.width, e.height, e.slices, e.mips))
        if e.mips > max_mips(e.width, e.height):
            raise GrassPackError("BadTextureShape", "%s has %d mips, at most %d exist" % (e.name, e.mips, max_mips(e.width, e.height)))
        if e.kind == KIND_NORMAL_TEXTURE and e.slices != 1:
            raise GrassPackError("BadTextureShape", "the normal texture has one slice")
        want = texture_bytes(e.width, e.height, e.slices, e.mips, BLOCK_BYTES[e.format])
        if e.size != want:
            raise GrassPackError("SizeMismatch", "%s size %d, the mip-chain arithmetic gives %d" % (e.name, e.size, want))

    if placement.slices != entries[1].slices:
        raise GrassPackError("Inconsistent", "the placement says %d slices, the albedo array has %d" % (placement.slices, entries[1].slices))
    if entries[2].slices != entries[1].slices:
        raise GrassPackError("Inconsistent", "the normal array has %d slices, the albedo array %d" % (entries[2].slices, entries[1].slices))

    expect = HEADER_SIZE
    for e in entries:
        if e.offset != expect:
            raise GrassPackError("NotContiguous", "%s starts at %d, expected %d" % (e.name, e.offset, expect))
        expect += e.size
    if view.nbytes != expect:
        raise GrassPackError("FileLength", "the file is %d bytes, the entries end at %d" % (view.nbytes, expect))
    return GrassPack(version, map_key, entries, view)
