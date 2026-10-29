#!/usr/bin/env python3
"""tools/bundle/chunk_compressor_guard.py -- UnityPy's chunk compressor with its size comparison corrected.

# The defect (2026-10-29, the intermittent Equator "Rauschen")

`BundleFile.save(packer=...)` compresses the data region in 128 KiB chunks through UnityPy's
`helpers/CompressionHelper.chunk_based_compress`. In UnityPy 1.25.2 (lines 191 and 213) a chunk is kept raw only when its
compressed form is LARGER than the chunk (`>` where `>=` is needed). A chunk whose LZ4HC form is EXACTLY the chunk size is
therefore written compressed, with the flag set and csize == usize, and Unity's player evidently takes such a block for
stored data and never decodes it: the raw LZ4 stream reached the AudioClip moll_indoor_wind_04 in the released Equator
scenes bundle (block 6657 of 6943), an intermittent burst of static in the map's ambience.

# What this module does

`corrected_chunk_based_compress` is a copy of the UnityPy function that stores a chunk raw, flag cleared, whenever its
compressed form is not smaller. `install_corrected_chunk_compressor()` makes `BundleFile.save` use it; call it BEFORE every
`BundleFile.save(packer=...)` that compresses chunks (tools/bundle/patch_monoscript_identity.py and tools/preset/patch_preset.py
do). tools/bundle/fix_stored_blocks.py (build_all.ps1 step 3b) stays the safety net that repairs a bundle that has the defect
anyway, and tools/validate/validate_bundle_blocks.py the independent check. Drop this module once UnityPy is fixed upstream.

The function is derived from UnityPy (MIT, https://github.com/K0lb3/UnityPy); see THIRD-PARTY-NOTICES.md.
UnityPy is imported inside the functions, so importing this module needs nothing.
"""
from __future__ import annotations

import sys


def corrected_chunk_based_compress(data, block_info_flag: int):
    """UnityPy 1.25.2's `helpers/CompressionHelper.chunk_based_compress` with its two size comparisons fixed.

    The original keeps a chunk raw only when `len(compressed) > chunk_size` (the full-chunk branch) or
    `len(compressed) > remaining` (the last chunk). A chunk whose compressed form is EXACTLY as long as the chunk is
    then written compressed, with the flag set and csize == usize, which Unity's player evidently reads as stored data
    (the 2026-10-29 Equator noise, see the module docstring). Here a chunk is stored raw, flag cleared, whenever the
    compressed form is not smaller (`>=`). Chunking, block sizes, flags and output for every other chunk are those of
    the original (the two loops are merged into one: a final chunk of exactly chunk_size took the original's
    last-chunk branch, which had the same condition)."""
    from UnityPy.helpers import CompressionHelper as helper

    switch = block_info_flag & 0x3F
    if switch == 0:
        return data, [(len(data), len(data), block_info_flag)]
    if switch not in helper.COMPRESSION_MAP:
        raise NotImplementedError(f"No compression function in the CompressionHelper.COMPRESSION_MAP for {switch}")
    if switch not in helper.COMPRESSION_CHUNK_SIZE_MAP:
        raise NotImplementedError(f"No chunk size in the CompressionHelper.COMPRESSION_CHUNK_SIZE_MAP for {switch}")
    compress_func = helper.COMPRESSION_MAP[switch]
    chunk_size = helper.COMPRESSION_CHUNK_SIZE_MAP[switch]
    stored_flag = block_info_flag ^ switch
    block_info = []
    out = bytearray()
    position = 0
    while position < len(data):
        raw = data[position:position + chunk_size]
        packed = compress_func(raw)
        if len(packed) >= len(raw):
            out.extend(raw)
            block_info.append((len(raw), len(raw), stored_flag))
        else:
            out.extend(packed)
            block_info.append((len(raw), len(packed), block_info_flag))
        position += len(raw)
    return bytes(out), block_info


def install_corrected_chunk_compressor() -> None:
    """Make UnityPy's `BundleFile.save` use `corrected_chunk_based_compress`. `BundleFile.py` calls
    `CompressionHelper.chunk_based_compress(...)` through the module (UnityPy 1.25.2), so the attribute of
    `UnityPy.helpers.CompressionHelper` is what counts; any other `UnityPy*` module that bound the original function by
    name at import time (a `from ... import chunk_based_compress`) is patched too, so a UnityPy that imports it
    differently does not silently keep the bug. Idempotent."""
    from UnityPy.helpers import CompressionHelper as helper

    original = helper.chunk_based_compress
    if original is corrected_chunk_based_compress:
        return
    helper.chunk_based_compress = corrected_chunk_based_compress
    for name, module in list(sys.modules.items()):
        if module is None or not (name == "UnityPy" or name.startswith("UnityPy.")):
            continue
        try:
            bound = getattr(module, "chunk_based_compress", None)
        except Exception:  # noqa: BLE001 -- a lazy module attribute that raises is not ours to patch
            continue
        if bound is original:
            module.chunk_based_compress = corrected_chunk_based_compress
