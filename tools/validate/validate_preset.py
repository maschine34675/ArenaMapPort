#!/usr/bin/env python3
"""WP6-style offline validator for WP3's patched preset bundle (ArenaMapPort SPEC.md SS B.WP3 /
SS E.3, owned+written by WP3 per the WP3 brief: "Write tools/validate/validate_preset.py").

Re-reads `work/build/preset/arena_equator_preset.bundle` (or --bundle) with UnityPy and asserts,
independently of the patcher script, every field SPEC.md SS E.3 specifies plus the MonoScript
identity and the AssetBundle-container rename this patcher additionally performs (see
tools/preset/patch_preset.py's module docstring "CORRECTION" section for why that's required).

Exit code 0 + "OK" summary on success. Exit code 1 with a specific, actionable message (never a
bare stack trace) on any failure, per SPEC.md SS B.WP6's acceptance criterion for validators.

Usage:
    python tools/validate/validate_preset.py
    python tools/validate/validate_preset.py --bundle work/build/preset/arena_equator_preset.bundle
    python tools/validate/validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle
"""
from __future__ import annotations

import argparse
import sys
import traceback
from pathlib import Path
from typing import NoReturn

import UnityPy

DEFAULT_BUNDLE = Path("D:/ArenaMapPort/work/build/preset/arena_equator_preset.bundle")

SCENE_BUNDLE_KEY = "arena_equator/arena_equator_scenes.bundle"  # SPEC.md SS0 / SS E.2
PRESET_ASSET_NAME = "arena_equator.scenespreset.asset"          # SPEC.md SS0

SCENE_TABLE = [  # SPEC.md SS B.WP3 step 3 / SS E.1 -- exact order, Scripts scene first
    "Arena_Equator_Scripts",
    "Arena_Equator",
    "Arena_Equator_Lighting",
    "Arena_Equator_DesignStuff",
    "Arena_Equator_TeamFight",
    "Arena_Equator_TeamFight_DesignStuff",
]

SERVER_NAME = "arena_equator_tdm"            # SPEC.md SS E.3
ACTIVE_SCENE_NAME = "Arena_Equator_Scripts"  # SPEC.md SS E.3

EXPECTED_MONOSCRIPT = {"m_ClassName": "ScenesPreset", "m_Namespace": "EFT", "m_AssemblyName": "Assembly-CSharp"}
# donor develop_preset.bundle's embedded MonoScript path_id, confirmed by direct UnityPy read
# this pass (docs/progress/wp3.md) -- SPEC.md SS B.WP3 acceptance criterion #3 asks specifically
# for "same file index + path id" as the donor, not just "resolves to the right class".
EXPECTED_SCRIPT_PATH_ID = -7501068993988226796

FAILURES: list[str] = []


def check(condition: bool, msg: str) -> None:
    if not condition:
        FAILURES.append(msg)
        print(f"[validate_preset] FAIL: {msg}", file=sys.stderr)
    else:
        print(f"[validate_preset]  ok : {msg}")


def die(msg: str) -> NoReturn:
    print(f"[validate_preset] FATAL: {msg}", file=sys.stderr)
    sys.exit(1)


def load_bundle_objects(path: Path):
    if not path.is_file():
        die(f"bundle not found: {path}")
    try:
        env = UnityPy.load(str(path))
    except Exception as e:  # noqa: BLE001 -- intentionally broad, we want a clean diagnosis not a stack trace
        die(f"UnityPy failed to load {path}: {e}")
    mono = None
    bundle = None
    mono_count = 0
    for obj in env.objects:
        if obj.type.name == "MonoBehaviour":
            tt = obj.read_typetree()
            if "ScenesPreset" in str(tt.get("m_Name", "")):
                mono_count += 1
                mono = (obj, tt)
        elif obj.type.name == "AssetBundle":
            bundle = (obj, obj.read_typetree())
    if mono_count == 0:
        die(f"no MonoBehaviour with 'ScenesPreset' in m_Name found in {path}")
    if mono_count > 1:
        die(f"{mono_count} MonoBehaviours with 'ScenesPreset' in m_Name found in {path} -- expected exactly 1")
    return env, mono, bundle


def validate_scenespreset(mono_obj, mono_tt: dict) -> None:
    check(mono_tt.get("ServerName") == SERVER_NAME,
          f"ServerName == {SERVER_NAME!r} (got {mono_tt.get('ServerName')!r})")
    check(mono_tt.get("_activeSceneName") == ACTIVE_SCENE_NAME,
          f"_activeSceneName == {ACTIVE_SCENE_NAME!r} (got {mono_tt.get('_activeSceneName')!r})")
    check(mono_tt.get("ChildPresets") == [],
          f"ChildPresets == [] (got {mono_tt.get('ChildPresets')!r})")

    keys = mono_tt.get("_scenesResourceKeys") or []
    check(len(keys) == len(SCENE_TABLE),
          f"_scenesResourceKeys has {len(SCENE_TABLE)} entries (got {len(keys)})")
    for i, expected_rcid in enumerate(SCENE_TABLE):
        if i >= len(keys):
            check(False, f"_scenesResourceKeys[{i}] missing (expected rcid={expected_rcid!r})")
            continue
        entry = keys[i]
        check(entry.get("rcid") == expected_rcid,
              f"_scenesResourceKeys[{i}].rcid == {expected_rcid!r} (got {entry.get('rcid')!r})")
        check(entry.get("path") == SCENE_BUNDLE_KEY,
              f"_scenesResourceKeys[{i}].path == {SCENE_BUNDLE_KEY!r} (got {entry.get('path')!r}, rcid={expected_rcid!r})")
        check(entry.get("_onlyOffline") == 0,
              f"_scenesResourceKeys[{i}]._onlyOffline == 0 (got {entry.get('_onlyOffline')!r}, rcid={expected_rcid!r})")

    guids = mono_tt.get("ScenesGuids") or []
    check(len(guids) == len(SCENE_TABLE),
          f"ScenesGuids has {len(SCENE_TABLE)} entries, padded/trimmed per SPEC.md SS B.WP3 step 7 (got {len(guids)})")

    # MonoScript identity: m_Script must resolve, within this same bundle, to EFT.ScenesPreset
    # in Assembly-CSharp (SPEC.md SS B.WP3 acceptance criteria + the WP3 brief's explicit ask).
    script_ptr = mono_tt.get("m_Script") or {}
    check(script_ptr.get("m_FileID") == 0,
          f"m_Script PPtr.m_FileID == 0 / local (got {script_ptr.get('m_FileID')!r}) -- "
          f"required for the embedded-MonoScript lookup below to be meaningful")
    check(script_ptr.get("m_PathID") == EXPECTED_SCRIPT_PATH_ID,
          f"m_Script PPtr.m_PathID == {EXPECTED_SCRIPT_PATH_ID} (donor's, unchanged by the patch) "
          f"(got {script_ptr.get('m_PathID')!r}) -- SPEC.md SS B.WP3 acceptance criterion #3")


def validate_monoscript_identity(env, mono_tt: dict) -> None:
    script_ptr = mono_tt.get("m_Script") or {}
    path_id = script_ptr.get("m_PathID")
    script_obj = next((o for o in env.objects if o.type.name == "MonoScript" and o.path_id == path_id), None)
    check(script_obj is not None,
          f"m_Script PPtr (path_id={path_id}) resolves to an embedded MonoScript object in this bundle")
    if script_obj is None:
        return
    script_tt = script_obj.read_typetree()
    actual = {k: script_tt.get(k) for k in EXPECTED_MONOSCRIPT}
    check(actual == EXPECTED_MONOSCRIPT,
          f"embedded MonoScript resolves to EFT.ScenesPreset in Assembly-CSharp (got {actual})")


def validate_container(bundle_tt: dict | None, mono_path_id: int) -> None:
    if bundle_tt is None:
        check(False, "bundle has an AssetBundle object (needed to verify the container-entry rename)")
        return
    container = bundle_tt.get("m_Container") or []
    matches = [
        e for e in container
        if isinstance(e, (list, tuple)) and len(e) == 2
        and (e[1].get("asset") or {}).get("m_PathID") == mono_path_id
    ]
    check(len(matches) == 1,
          f"exactly 1 m_Container entry points at the ScenesPreset MonoBehaviour (path_id={mono_path_id}, "
          f"found {len(matches)}) -- see tools/preset/patch_preset.py's 'CORRECTION' docstring section for why "
          f"this must exist: AssetBundleExtension.FixAssetName() resolves base.json's Scene.rcid against "
          f"this table, not against the MonoBehaviour's own m_Name")
    if matches:
        key = matches[0][0]
        check(key.lower().endswith(PRESET_ASSET_NAME),
              f"m_Container entry key ends with {PRESET_ASSET_NAME!r} (got {key!r}) -- "
              f"AssetBundle.LoadAsset(rcid) resolves by this table, a stale/donor-named key here "
              f"means the patched preset silently fails to load at runtime even though every "
              f"MonoBehaviour field is correct")


def validate_scenes_cross_check(scenes_bundle_path: Path, allow_missing_scenes: bool = False) -> None:
    """Optional: cross-check SCENE_TABLE's rcids against the actual scene names inside a
    WP2-built scenes bundle. SPEC.md SS B.WP3 edge cases flags a typo here as 'the single
    highest-leverage typo risk in this whole pipeline' -- a mismatch means
    SceneManager.LoadSceneAsync silently finds nothing, no exception.

    A *slice* build (S1: a handful of hand-picked objects, deliberately built with
    ``-skipScriptsScene`` and only a subset of the 6 scenes actually authored/imported) will
    legitimately have most of SCENE_TABLE's rcids absent from the bundle -- that is not a typo,
    it's the slice doing exactly what it was asked to do. A *typo* (rcid spelled differently than
    the Unity scene name WP2 actually saved) is indistinguishable from "the slice never built this
    scene" by inspecting the bundle alone -- only the caller knows which one applies for a given
    run, hence the explicit ``--allow-missing-scenes`` opt-in below rather than auto-detecting
    "bundle has <6 scenes -> must be a slice" (that heuristic would just as happily hide a real
    missing-scene regression in a supposedly-full 6-scene build). When the flag is not passed
    (the default), a missing rcid is still a hard FAIL, unchanged from the original behaviour --
    this preserves the check's value for a real S2/full build, where every rcid MUST resolve."""
    if not scenes_bundle_path.is_file():
        print(f"[validate_preset] --scenes-bundle {scenes_bundle_path} not found -- skipping scene cross-check")
        return
    try:
        env = UnityPy.load(str(scenes_bundle_path))
    except Exception as e:  # noqa: BLE001
        die(f"UnityPy failed to load --scenes-bundle {scenes_bundle_path}: {e}")

    scene_paths = set()
    # UnityPy exposes bundled scene paths as either top-level ObjectReader-less container
    # entries or (more reliably here) via env.container's keys ending in .unity.
    for path in getattr(env, "container", {}).keys():
        if path.lower().endswith(".unity"):
            scene_paths.add(path)
    for f in env.files.values():
        for path in getattr(f, "container", {}).keys() if hasattr(f, "container") else []:
            if path.lower().endswith(".unity"):
                scene_paths.add(path)

    if not scene_paths:
        check(False, f"--scenes-bundle {scenes_bundle_path} contains no .unity scene entries "
                      f"(container empty or bundle has no scenes -- can't cross-check rcids)")
        return

    def scene_short_name(p: str) -> str:
        base = p.rsplit("/", 1)[-1]
        return base[:-len(".unity")] if base.lower().endswith(".unity") else base

    short_names = {scene_short_name(p) for p in scene_paths}
    print(f"[validate_preset] --scenes-bundle scene short names found: {sorted(short_names)}")

    present, absent = [], []
    for rcid in SCENE_TABLE:
        # match case-insensitively against the short scene name, mirroring
        # AssetBundleExtension.FixAssetName's own case-insensitive suffix matching for scenes
        # loaded by name (SPEC.md SS B.WP2.5's "name-based, not bundle-scoped" note).
        found = any(rcid.lower() == n.lower() for n in short_names)
        if found:
            present.append(rcid)
            check(True, f"preset rcid {rcid!r} matches a scene name inside {scenes_bundle_path.name} "
                        f"(available: {sorted(short_names)})")
        else:
            absent.append(rcid)
            msg = (f"preset rcid {rcid!r} matches a scene name inside {scenes_bundle_path.name} "
                   f"(available: {sorted(short_names)})")
            if allow_missing_scenes:
                print(f"[validate_preset] WARN: {msg} -- absent, tolerated under --allow-missing-scenes "
                      f"(slice build; not scored as a failure)")
            else:
                check(False, msg)

    print(f"[validate_preset] scene cross-check summary: {len(present)}/{len(SCENE_TABLE)} rcids present "
          f"{sorted(present)}, {len(absent)} absent {sorted(absent)}"
          + (" (absent ones tolerated: --allow-missing-scenes)" if allow_missing_scenes and absent else ""))


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bundle", type=Path, default=DEFAULT_BUNDLE, help="patched preset bundle to validate")
    ap.add_argument("--scenes-bundle", type=Path, default=None,
                     help="optional WP2-built scenes bundle to cross-check rcids against "
                          "(default: auto-probe work/build/s1/ for a scenes bundle if not given)")
    ap.add_argument("--allow-missing-scenes", action="store_true",
                     help="treat a SCENE_TABLE rcid with no matching scene inside --scenes-bundle as "
                          "a WARN instead of a FAIL -- for validating an intentionally partial slice "
                          "build (e.g. S1, or any -skipScriptsScene run) where most of the 6 scenes "
                          "were deliberately never imported/authored. Do NOT pass this for a real "
                          "S2/full 6-scene build -- there, a missing rcid is a genuine defect.")
    args = ap.parse_args()

    env, (mono_obj, mono_tt), bundle = load_bundle_objects(args.bundle)
    print(f"[validate_preset] validating {args.bundle} (MonoBehaviour path_id={mono_obj.path_id})")

    validate_scenespreset(mono_obj, mono_tt)
    validate_monoscript_identity(env, mono_tt)
    validate_container(bundle[1] if bundle else None, mono_obj.path_id)

    scenes_bundle = args.scenes_bundle
    if scenes_bundle is None:
        s1_dir = Path("D:/ArenaMapPort/work/build/s1")
        candidates = sorted(s1_dir.glob("*scenes*.bundle")) if s1_dir.is_dir() else []
        if candidates:
            scenes_bundle = candidates[0]
            print(f"[validate_preset] auto-probed --scenes-bundle: {scenes_bundle}")
        else:
            print(f"[validate_preset] no scenes bundle found under {s1_dir} -- running preset-only checks "
                  f"(pass --scenes-bundle explicitly once WP2 has produced one)")
    if scenes_bundle is not None:
        validate_scenes_cross_check(scenes_bundle, allow_missing_scenes=args.allow_missing_scenes)

    print()
    if FAILURES:
        print(f"[validate_preset] {len(FAILURES)} check(s) FAILED:")
        for f in FAILURES:
            print(f"  - {f}")
        sys.exit(1)
    print(f"[validate_preset] OK -- all checks passed for {args.bundle}")


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:  # noqa: BLE001 -- convert any unexpected crash into a diagnosable message, not a bare trace
        print("[validate_preset] FATAL: unexpected exception", file=sys.stderr)
        traceback.print_exc()
        sys.exit(1)
