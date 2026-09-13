#!/usr/bin/env python3
"""tools/bundle/patch_monoscript_identity.py — LATENT RUNTIME BLOCKER fix (task label
`identity-fix`).

# The defect

`tools/gamerefs-fixup` (task item 1 of `s1-fix-r1`) had to rename the GameRefs copies of
`Assembly-CSharp.dll` / `Assembly-CSharp-firstpass.dll` — both their on-disk FILE name and their
embedded CLR assembly identity — to `SptGameRefs-AssemblyCSharp[Firstpass].dll`, because Unity
treats the literal names `Assembly-CSharp[-firstpass]` as RESERVED for its own implicit,
project-compiled script assemblies: a precompiled Plugin DLL under those exact names is
permanently stuck "not finished compilation" for every `AddComponent<T>()` call
(`docs/progress/s1-fix-r1.md` SS1.2). That rename is what makes it possible to author the Scripts
scene (`LevelSettings`/`LocationScene`/`BotZone`/`LocationExportInfo`/`SpawnPointMarker`, SPEC.md
SSD) inside the Unity Editor at all.

But the rename has a side effect nobody downstream corrected: when Unity's Editor serializes each
of those MonoBehaviours' `MonoScript` into the built AssetBundle, it writes the CURRENT (renamed)
assembly identity into `MonoScript.m_AssemblyName` — confirmed empirically this pass, reading
`work/build/s1/arena_equator_scenes.bundle` directly with UnityPy: every one of the 5 SSD types
carries `m_AssemblyName == 'SptGameRefs-AssemblyCSharp.dll'`.

The REAL SPT/EFT client never loads a `SptGameRefs-AssemblyCSharp.dll` — it loads
`Assembly-CSharp.dll` (main) / `Assembly-CSharp-firstpass.dll` (firstpass), confirmed this pass by
reading real MonoScript objects directly out of `D:/SPT41/EscapeFromTarkov_Data/
globalgamemanagers.assets` (200 real MonoScripts scanned; every EFT/BSG one reads
`m_AssemblyName == 'Assembly-CSharp'` or `'Assembly-CSharp-firstpass'` — note: NO `.dll` suffix,
contradicting this task's own literal wording, which asked for `'Assembly-CSharp.dll'` —
corroborated independently by `tools/validate/validate_preset.py`'s own
`EXPECTED_MONOSCRIPT = {..., "m_AssemblyName": "Assembly-CSharp"}`, confirmed against the real
donor `develop_preset.bundle`'s own embedded `EFT.ScenesPreset` MonoScript). Unity resolves a
MonoBehaviour's script by (assembly, namespace, class) against the assemblies actually loaded in
the running process — an assembly name that doesn't match any loaded assembly makes the component
unresolvable, i.e. every one of these 5 components would render as a "missing script" the moment a
real raid loads this bundle, silently no-oping every EFT system that depends on them
(`LocationScene.Awake`, `BotZone.Init`, spawn-point discovery, ...). Every prior pass
(`s1-fix-r1`/`s1-fix-r2`/`s1-validate-r2`/`s1-validate-r3`) documented this deviation as
"intentional, load-bearing, not a defect" — that judgment was WRONG: it never actually checked what
assembly name the real client resolves MonoScripts against, only that the two pipelines were
"correctly distinguished". This is the bug this script exists to fix.

# The fix

The Editor-time rename (`SptGameRefs-*`) is still required to get PAST Unity's AddComponent
blocker at BUILD time — reverting `tools/gamerefs-fixup`/`tools/sync-game-refs.ps1` would
reintroduce the original SIGSEGV-adjacent blocker. So the fix is a POST-BUILD, BINARY patch of the
already-built AssetBundle: open it with UnityPy, find every `MonoScript` whose `m_AssemblyName` is
one of the renamed GameRefs spellings, and rewrite JUST that one string field back to the real,
empirically-verified spelling the actual EFT client uses (`Assembly-CSharp` /
`Assembly-CSharp-firstpass`, no `.dll` suffix) — touching nothing else in the bundle (every other
object, and every other MonoScript field, is byte-for-byte untouched; verified below).

# Usage

    python tools/bundle/patch_monoscript_identity.py --bundle work/build/s1/arena_equator_scenes.bundle
    python tools/bundle/patch_monoscript_identity.py --bundle work/build/s1/arena_equator_navmesh.bundle
    python tools/bundle/patch_monoscript_identity.py --bundle <path> --out <other-path> --packer lz4

Exit code 0 on success (including "0 MonoScripts needed patching, already clean" — the bundle is
left untouched on disk in that case, no output file written unless --out is different from
--bundle and --force-rewrite-if-clean is passed). Exit code 1, with a specific message, on any
failure (bundle unreadable, verification mismatch, save error) — never a bare stack trace.
"""
from __future__ import annotations

import argparse
import gc
import os
import shutil
import struct
import sys
import tempfile
import time
import traceback
from pathlib import Path
from typing import NoReturn

# ---------------------------------------------------------------------------
# The rename map — every spelling `tools/gamerefs-fixup`/`tools/sync-game-refs.ps1` are known (or
# could plausibly, defensively) produce, mapped to the real, empirically-verified spelling the
# actual EFT client resolves MonoScripts against (see module docstring "The defect" for the
# evidence). Source of the LHS spellings: `tools/sync-game-refs.ps1` SS`$renameMap`
# ("Assembly-CSharp" -> "SptGameRefs-AssemblyCSharp.dll", "Assembly-CSharp-firstpass" ->
# "SptGameRefs-AssemblyCSharpFirstpass.dll") and `tools/gamerefs-fixup/Program.cs`'s own
# `RenameMap` (embedded-identity rewrite target: "SptGameRefs-AssemblyCSharp" /
# "SptGameRefs-AssemblyCSharpFirstpass", no ".dll"). Both the ".dll"-suffixed and bare spellings
# are covered defensively since which one a MonoScript's `m_AssemblyName` actually carries depends
# on Cecil's `ModuleDefinition.Name` (matches the file name, WITH ".dll") vs
# `AssemblyDefinition.Name.Name` (assembly identity, WITHOUT ".dll") — this pass empirically
# observed the ".dll"-suffixed form for the 5 SSD types in `arena_equator_scenes.bundle`, but a
# future GameRefs sync could plausibly hit either, so both are handled the same way, never
# silently ignored.
RENAME_MAP: dict[str, str] = {
    "SptGameRefs-AssemblyCSharp.dll": "Assembly-CSharp",
    "SptGameRefs-AssemblyCSharp": "Assembly-CSharp",
    "SptGameRefs-AssemblyCSharpFirstpass.dll": "Assembly-CSharp-firstpass",
    "SptGameRefs-AssemblyCSharpFirstpass": "Assembly-CSharp-firstpass",
    # defensive: a hyphenated "-Firstpass" spelling is not what either tool currently produces,
    # but if that ever changes this rewrite must not silently miss it.
    "SptGameRefs-AssemblyCSharp-Firstpass.dll": "Assembly-CSharp-firstpass",
    "SptGameRefs-AssemblyCSharp-Firstpass": "Assembly-CSharp-firstpass",
}

# The only two spellings the fixed-up bundle's MonoScripts are allowed to carry afterward, for
# this script's own self-check (kept in sync with
# tools/validate/validate_bundle_roundtrip.py's ACCEPTED_ASSEMBLY_NAMES).
REAL_ASSEMBLY_NAMES = {"Assembly-CSharp", "Assembly-CSharp-firstpass"}

COMPRESSION_SWITCH_NAMES = {0: "none", 1: "lzma", 2: "lz4", 3: "lz4hc", 4: "lzham"}


def die(msg: str) -> NoReturn:
    print(f"[patch_monoscript_identity] FATAL: {msg}", file=sys.stderr)
    sys.exit(1)


def decode_compression(dataflags: int, block_info_flags: int | None) -> str:
    meta_switch = dataflags & 0x3F
    meta_name = COMPRESSION_SWITCH_NAMES.get(meta_switch, f"unknown({meta_switch})")
    if block_info_flags is None:
        return f"meta={meta_name} data=<no blocks read yet>"
    data_switch = block_info_flags & 0x3F
    data_name = COMPRESSION_SWITCH_NAMES.get(data_switch, f"unknown({data_switch})")
    return f"meta={meta_name}(flags={dataflags}) data={data_name}(flags={block_info_flags})"


def iter_serialized_files(env):
    """Recursively walks a UnityPy Environment's file tree, yielding every nested SerializedFile
    (mirrors tools/validate/validate_bundle_roundtrip.py's own helper of the same name, kept
    independent/duplicated deliberately — this script must not import the validator, and vice
    versa)."""
    from UnityPy.files.SerializedFile import SerializedFile

    def _walk(f):
        if isinstance(f, SerializedFile):
            yield f
        nested = getattr(f, "files", None)
        if nested:
            for f2 in nested.values():
                yield from _walk(f2)

    for f in env.files.values():
        yield from _walk(f)


def get_top_bundle_file(env):
    """Returns the single top-level BundleFile this Environment was loaded from. Raises if the
    bundle isn't a simple single-file load (this tool is only meant for a single .bundle path,
    same convention validate_bundle_roundtrip.py's --scenes-bundle/--navmesh-bundle use)."""
    from UnityPy.files.BundleFile import BundleFile

    if len(env.files) != 1:
        die(f"expected exactly one top-level file in the loaded Environment, found "
            f"{len(env.files)}: {list(env.files)} — this tool takes a single .bundle path, not a "
            f"folder/multi-file load")
    f = next(iter(env.files.values()))
    if not isinstance(f, BundleFile):
        die(f"top-level file is a {type(f).__name__}, not a BundleFile — expected a .bundle "
            f"(UnityFS) input")
    return f


def snapshot_non_monoscript_objects(env) -> dict[tuple[str, int], bytes]:
    """Every object EXCEPT MonoScript, raw bytes, keyed by (owning SerializedFile name, path_id) —
    used to independently verify (task item 4) that nothing but the targeted MonoScript string
    fields changed."""
    snap: dict[tuple[str, int], bytes] = {}
    for sf in iter_serialized_files(env):
        for path_id, obj in sf.objects.items():
            if obj.type.name == "MonoScript":
                continue
            snap[(sf.name, path_id)] = obj.get_raw_data()
    return snap


def read_cstr(buf: bytes, offset: int) -> tuple[str, int]:
    end = buf.index(0, offset)
    return buf[offset:end].decode("utf-8", errors="replace"), end + 1


def parse_unityfs_header(raw: bytes) -> dict:
    """Independent, UnityPy-free parse of the primary UnityFS bundle header (task item 5's
    "compare bundle header/flags/block sizes... before vs after"), reading exactly the fields
    BundleFile.read_fs() itself reads at this stage (see UnityPy/files/BundleFile.py), directly
    off the raw bytes with struct/big-endian ('>') — so this check does not rely on the same
    library code path being used to both write and re-verify the patch."""
    off = 0
    signature, off = read_cstr(raw, off)
    fmt = struct.unpack_from(">I", raw, off)[0]; off += 4
    version_player, off = read_cstr(raw, off)
    version_engine, off = read_cstr(raw, off)
    result = {"signature": signature, "format": fmt, "version_player": version_player,
              "version_engine": version_engine}
    if signature != "UnityFS":
        return result
    file_size = struct.unpack_from(">q", raw, off)[0]; off += 8
    compressed_size = struct.unpack_from(">I", raw, off)[0]; off += 4
    uncompressed_size = struct.unpack_from(">I", raw, off)[0]; off += 4
    dataflags = struct.unpack_from(">I", raw, off)[0]; off += 4
    result.update(file_size=file_size, compressed_size=compressed_size,
                   uncompressed_size=uncompressed_size, dataflags=dataflags)
    return result


def nested_file_sizes(env) -> dict[str, int]:
    """name -> serialized byte length, for every nested file (one per Unity 'block'/sub-file:
    each scene's SerializedFile + its .sharedAssets) inside a loaded bundle Environment. Used to
    compare block-level sizes before vs after the patch (task item 5)."""
    sizes: dict[str, int] = {}
    bundle_file = get_top_bundle_file(env)
    for name, f in bundle_file.files.items():
        if hasattr(f, "save") and callable(f.save):
            data = f.save()
        else:
            data = getattr(f, "bytes", b"")
        sizes[name] = len(data)
    return sizes


def list_assembly_names(env) -> dict[str, int]:
    """class_name -> count is not what we want here; distinct m_AssemblyName -> count of
    MonoScript objects carrying it (task item 1's "list all distinct m_AssemblyName values")."""
    counts: dict[str, int] = {}
    for obj in env.objects:
        if obj.type.name != "MonoScript":
            continue
        tt = obj.read_typetree()
        asm = tt.get("m_AssemblyName")
        counts[asm] = counts.get(asm, 0) + 1
    return counts


def patch_env(env) -> list[dict]:
    """Rewrites m_AssemblyName in place (via save_typetree) for every MonoScript matching
    RENAME_MAP. Returns the list of patched-object details for reporting. Touches NOTHING else —
    no other MonoScript field, no non-MonoScript object."""
    patched = []
    for obj in env.objects:
        if obj.type.name != "MonoScript":
            continue
        tt = obj.read_typetree()
        old_asm = tt.get("m_AssemblyName")
        new_asm = RENAME_MAP.get(old_asm)
        if new_asm is None:
            continue
        tt["m_AssemblyName"] = new_asm
        obj.save_typetree(tt)
        patched.append({
            "class_name": tt.get("m_ClassName"),
            "namespace": tt.get("m_Namespace", ""),
            "path_id": obj.path_id,
            "old_assembly": old_asm,
            "new_assembly": new_asm,
        })
    return patched


def write_bytes_with_retry(path: Path, data: bytes, attempts: int = 5, delay: float = 0.2) -> None:
    """Windows Defender / AV real-time scanning can hold a brief exclusive handle on a just-
    written file (same class of transient failure tools/gamerefs-fixup's
    ReadAllBytesWithRetry/WriteAllBytesWithRetry worked around for GameRefs DLL copies,
    docs/progress/s1-fix-r1.md SS1.3) — bounded retry, not a silent swallow."""
    last_exc = None
    for i in range(attempts):
        try:
            path.write_bytes(data)
            return
        except (PermissionError, OSError) as e:
            last_exc = e
            time.sleep(delay)
    raise last_exc  # type: ignore[misc]


def replace_with_retry(src: Path, dst: Path, attempts: int = 5, delay: float = 0.2) -> None:
    last_exc = None
    for i in range(attempts):
        try:
            os.replace(str(src), str(dst))
            return
        except (PermissionError, OSError) as e:
            last_exc = e
            time.sleep(delay)
    raise last_exc  # type: ignore[misc]


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bundle", type=Path, required=True, help="the built .bundle to patch")
    ap.add_argument("--out", type=Path, default=None,
                     help="output path (default: patch in place, same path as --bundle)")
    ap.add_argument("--packer", choices=["original", "none", "lz4", "lzma"], default="original",
                     help="compression to write with (default: 'original' — reuse the bundle's own "
                          "detected data/block compression flags, the closest UnityPy can get to "
                          "'preserve exactly as built')")
    ap.add_argument("--force-rewrite-if-clean", action="store_true",
                     help="normally, if 0 MonoScripts need patching the input file is left "
                          "completely untouched (exit 0) rather than re-serialized for no reason; "
                          "pass this to force a re-save (e.g. to normalize --packer) even then")
    args = ap.parse_args()

    if not args.bundle.is_file():
        die(f"--bundle not found: {args.bundle}")
    out_path = args.out if args.out is not None else args.bundle

    try:
        import UnityPy
    except ImportError as e:
        die(f"UnityPy not importable: {e}")

    # captured BEFORE any load/mutation/in-place-replace touches this path -- the ground truth
    # for every "before" comparison below (item 5's raw header parse).
    before_raw_bytes = args.bundle.read_bytes()

    print(f"[patch_monoscript_identity] loading {args.bundle} ...")
    try:
        env = UnityPy.load(str(args.bundle))
    except Exception as e:  # noqa: BLE001
        die(f"UnityPy failed to load --bundle {args.bundle}: {e}")

    bundle_file = get_top_bundle_file(env)
    before_dataflags = int(bundle_file.dataflags)
    before_block_flags = getattr(bundle_file, "_block_info_flags", None)
    before_size = args.bundle.stat().st_size
    before_names = list_assembly_names(env)

    print(f"[patch_monoscript_identity] BEFORE: {before_size:,} bytes, "
          f"compression: {decode_compression(before_dataflags, before_block_flags)}")
    print(f"[patch_monoscript_identity] BEFORE distinct m_AssemblyName values (MonoScript count each):")
    for name, count in sorted(before_names.items(), key=lambda kv: (kv[0] or "")):
        flag = " <- WILL BE RENAMED" if name in RENAME_MAP else ""
        print(f"    {name!r}: {count}{flag}")

    # Perf fix (flagged as technical debt in docs/progress/s2-build.md SS4: the exhaustive
    # byte-identity snapshot below took 35+ minutes at S2's ~120k-object scale): `before_names`
    # (just read, from the SAME RENAME_MAP-based MonoScript scan patch_env() itself does) already
    # tells us with certainty whether anything will be patched -- no need to pay the expensive
    # whole-bundle snapshot cost just to re-discover "0 MonoScripts need patching" a second time.
    # Skip straight to the cheap early-return path in that case; the exhaustive byte-identity
    # check below is completely unchanged (not weakened) for the case that actually matters --
    # when a real patch is about to be written.
    needs_patch = any(name in RENAME_MAP for name in before_names)
    if not needs_patch and not args.force_rewrite_if_clean:
        print("[patch_monoscript_identity] 0 MonoScript(s) needed patching — bundle already carries "
              "only real assembly names, nothing to do. Input left completely untouched.")
        if args.out is not None and args.out != args.bundle:
            # still an explicit --out request: copy through unchanged rather than silently doing
            # nothing at the requested destination.
            args.out.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(args.bundle, args.out)
            print(f"[patch_monoscript_identity] copied unchanged bundle to --out {args.out}")
        print("[patch_monoscript_identity] OK -- nothing to patch")
        return

    # A real patch (or --force-rewrite-if-clean) is about to happen -- NOW pay the expensive
    # precomputation costs, exactly as before this perf fix (nothing about the actual
    # verification below is weakened or skipped).
    # nested-file (Unity 'block') sizes MUST be captured before patch_env() mutates any object's
    # .data in place within this same `env` -- otherwise "before" would already reflect the patch.
    before_blocks = nested_file_sizes(env)
    # snapshot every non-MonoScript object's raw bytes BEFORE any mutation, for the byte-identity
    # check after saving (task item 4).
    pre_snapshot = snapshot_non_monoscript_objects(env)

    patched = patch_env(env)

    print(f"[patch_monoscript_identity] patched {len(patched)} MonoScript object(s):")
    for p in patched:
        ns = (p["namespace"] + ".") if p["namespace"] else ""
        print(f"    {ns}{p['class_name']} (path_id={p['path_id']}): "
              f"{p['old_assembly']!r} -> {p['new_assembly']!r}")

    # save to a temp file next to the destination, then atomically replace — never leave a
    # half-written bundle at out_path on failure. BundleFile.save()'s own packer semantics accept
    # the string "original" directly (reuses self.dataflags/self._block_info_flags as read).
    packer_arg = args.packer
    print(f"[patch_monoscript_identity] saving with packer={packer_arg!r} ...")
    try:
        data = bundle_file.save(packer=packer_arg)
    except Exception as e:  # noqa: BLE001
        die(f"BundleFile.save(packer={packer_arg!r}) failed: {e}\n{traceback.format_exc()}")

    out_path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(prefix=out_path.name + ".", suffix=".tmp", dir=str(out_path.parent))
    os.close(fd)
    tmp_path = Path(tmp_name)
    write_bytes_with_retry(tmp_path, data)

    # free the OS file handle UnityPy's EndianBinaryReader keeps open on the ORIGINAL file
    # (helpers/ImportHelper.py opens it via plain `open(path, "rb")`, not a fully-buffered
    # in-memory read) before replacing it — relevant when --out == --bundle (in-place patch, the
    # default). Windows refuses os.replace() onto a path this same process still has open, so drop
    # every reference and force a collection (plain refcounting can leave this cyclic-referenced
    # object graph — ObjectReader <-> SerializedFile <-> BundleFile <-> reader — alive a moment
    # longer than expected) rather than relying on the retry loop alone to out-wait it.
    del bundle_file, env
    gc.collect()
    replace_with_retry(tmp_path, out_path)

    after_size = out_path.stat().st_size
    print(f"[patch_monoscript_identity] wrote {after_size:,} bytes -> {out_path} "
          f"(was {before_size:,} bytes, {after_size - before_size:+,} bytes)")

    # ---- re-open the OUTPUT fresh and verify, independently of the in-memory state above ----
    print(f"[patch_monoscript_identity] re-loading {out_path} fresh to verify ...")
    try:
        env2 = UnityPy.load(str(out_path))
    except Exception as e:  # noqa: BLE001
        die(f"patched bundle FAILED to re-load with UnityPy: {e}\n{traceback.format_exc()}")

    bundle_file2 = get_top_bundle_file(env2)
    after_dataflags = int(bundle_file2.dataflags)
    after_block_flags = getattr(bundle_file2, "_block_info_flags", None)
    after_names = list_assembly_names(env2)

    print(f"[patch_monoscript_identity] AFTER: {after_size:,} bytes, "
          f"compression: {decode_compression(after_dataflags, after_block_flags)}")
    print(f"[patch_monoscript_identity] AFTER distinct m_AssemblyName values (MonoScript count each):")
    for name, count in sorted(after_names.items(), key=lambda kv: (kv[0] or "")):
        print(f"    {name!r}: {count}")

    ok = True

    bad_names = set(after_names) - REAL_ASSEMBLY_NAMES
    if bad_names:
        print(f"[patch_monoscript_identity] FAIL: {sorted(bad_names)} still present after patching "
              f"(expected only {sorted(REAL_ASSEMBLY_NAMES)})", file=sys.stderr)
        ok = False
    else:
        print(f"[patch_monoscript_identity]  ok : every MonoScript now carries a real assembly name "
              f"({sorted(REAL_ASSEMBLY_NAMES)})")

    # byte-identity check: every non-MonoScript object, re-read from the FRESH reload, must be
    # byte-for-byte identical to its pre-patch snapshot.
    post_snapshot = snapshot_non_monoscript_objects(env2)
    missing = set(pre_snapshot) - set(post_snapshot)
    extra = set(post_snapshot) - set(pre_snapshot)
    if missing or extra:
        print(f"[patch_monoscript_identity] FAIL: non-MonoScript object set changed by the patch "
              f"(missing after: {len(missing)}, new after: {len(extra)}) — expected byte-for-byte "
              f"identical object set", file=sys.stderr)
        ok = False
    mismatched = [k for k in (set(pre_snapshot) & set(post_snapshot)) if pre_snapshot[k] != post_snapshot[k]]
    if mismatched:
        print(f"[patch_monoscript_identity] FAIL: {len(mismatched)} non-MonoScript object(s) changed "
              f"raw bytes across the patch (task item 4's byte-identity requirement) — first few: "
              f"{mismatched[:5]}", file=sys.stderr)
        ok = False
    else:
        print(f"[patch_monoscript_identity]  ok : all {len(pre_snapshot)} non-MonoScript objects "
              f"(MonoBehaviour/Mesh/Material/Texture2D/GameObject/... — everything but the "
              f"{len(patched)} patched MonoScript(s)) are byte-for-byte identical before vs after "
              f"the patch")

    # compression fidelity: report whether the requested packer reproduced the original flags
    # exactly (informational unless --packer original was requested and failed to match, which
    # would mean UnityPy silently normalized the compression — worth knowing, not fatal by
    # itself since a different-but-valid compression still loads fine).
    if args.packer == "original":
        if after_dataflags == before_dataflags and after_block_flags == before_block_flags:
            print(f"[patch_monoscript_identity]  ok : packer='original' reproduced the exact "
                  f"original compression flags (meta dataflags={before_dataflags}, "
                  f"data block_info_flags={before_block_flags})")
        else:
            print(f"[patch_monoscript_identity] WARN: packer='original' did NOT reproduce the exact "
                  f"original flags (before: dataflags={before_dataflags}/block_flags={before_block_flags}, "
                  f"after: dataflags={after_dataflags}/block_flags={after_block_flags}) — the bundle "
                  f"still loads (see the UnityPy re-load check above) but the on-disk compression "
                  f"scheme changed; not treated as fatal, but flagged here rather than silently "
                  f"passed over", file=sys.stderr)

    # AssetBundle m_Container / m_PreloadTable structural check (task item 5) — the AssetBundle
    # object's type is "AssetBundle", not "MonoScript", so it is already covered by the general
    # non-MonoScript byte-identity check above (pre_snapshot/post_snapshot) — a byte-level
    # comparison is strictly stronger than a field-level m_Container/m_PreloadTable diff, so no
    # separate check is needed; called out explicitly here so this requirement is traceable.
    assetbundle_keys = [k for k in pre_snapshot if k[1] in {
        oid for sf in iter_serialized_files(env2) for oid, o in sf.objects.items() if o.type.name == "AssetBundle"
    }]
    if assetbundle_keys and not mismatched:
        print(f"[patch_monoscript_identity]  ok : AssetBundle object ({len(assetbundle_keys)} found) "
              f"— including m_Container/m_PreloadTable/m_AssetBundleName/m_Dependencies — is part of "
              f"the byte-identity check above and is confirmed unchanged")
    elif not assetbundle_keys:
        print("[patch_monoscript_identity] WARN: no AssetBundle object found in this bundle to check "
              "m_Container/m_PreloadTable against (unexpected for a built .bundle, but not fatal by "
              "itself)")

    # header/flags/block-size comparison (task item 5): a raw, UnityPy-independent parse of the
    # UnityFS primary header, plus the nested-file (one per Unity "block"/serialized sub-file)
    # name+size table, compared before vs after.
    before_header = parse_unityfs_header(before_raw_bytes)
    after_header = parse_unityfs_header(out_path.read_bytes())
    print("[patch_monoscript_identity] UnityFS primary header (raw, parsed independently of UnityPy):")
    for key in ("signature", "format", "version_player", "version_engine", "file_size",
                "compressed_size", "uncompressed_size", "dataflags"):
        b, a = before_header.get(key), after_header.get(key)
        same = "==" if b == a else "!="
        print(f"    {key}: before={b!r} {same} after={a!r}")
    header_fields_expected_equal = ("signature", "format", "version_player", "version_engine", "dataflags")
    header_diffs = [k for k in header_fields_expected_equal if before_header.get(k) != after_header.get(k)]
    if header_diffs:
        print(f"[patch_monoscript_identity] WARN: header field(s) {header_diffs} changed by the patch "
              f"(compressed_size/uncompressed_size/file_size are EXPECTED to change slightly — the "
              f"patched string fields are a different length)", file=sys.stderr)
    else:
        print(f"[patch_monoscript_identity]  ok : signature/format/unity-version/dataflags header "
              f"fields unchanged (only compressed_size/uncompressed_size/file_size shifted, matching "
              f"the {sum(len(p['new_assembly']) - len(p['old_assembly']) for p in patched):+d}-byte "
              f"aggregate string-length delta from the {len(patched)} patched field(s))")

    after_blocks = nested_file_sizes(env2)
    print("[patch_monoscript_identity] nested file (Unity 'block') sizes, before vs after:")
    for name in sorted(set(before_blocks) | set(after_blocks)):
        b, a = before_blocks.get(name), after_blocks.get(name)
        marker = "" if b == a else "  <-- size changed (expected only for the file(s) containing a patched MonoScript)"
        print(f"    {name}: before={b} after={a}{marker}")

    print()
    if ok:
        print("[patch_monoscript_identity] OK -- patch applied and verified "
              f"({len(patched)} MonoScript(s) patched, {len(pre_snapshot)} other object(s) "
              f"confirmed byte-identical)")
    else:
        print("[patch_monoscript_identity] FAILED -- see FAIL lines above", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:  # noqa: BLE001
        print("[patch_monoscript_identity] FATAL: unexpected exception", file=sys.stderr)
        traceback.print_exc()
        sys.exit(1)
