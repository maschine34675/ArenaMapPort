#!/usr/bin/env python3
"""WP3 -- preset bundle patcher (ArenaMapPort SPEC.md SS B.WP3 / SS E.3).

Patches a *copy* of SPT 4.1.5's donor `develop_preset.bundle` (read-only source, never
written to -- O8) into `arena_equator_preset.bundle`: rewrites its single `EFT.ScenesPreset`
MonoBehaviour to point at ArenaMapPort's own 6-scene set (SPEC.md SS E.1), and renames the
bundle's own AssetBundle-object container entry so the asset is still resolvable by the new
rcid at runtime (see "CORRECTION" below -- this is not spelled out in SPEC.md's literal
step list and was found by tracing the client's actual load path this pass).

Near-verbatim port of ManimalIcebreaker's `patch_icebreaker_preset.py` (MIT), adapted to
this project's donor/field layout -- see D:/ArenaMapPort/THIRD-PARTY-NOTICES.md.

--------------------------------------------------------------------------------------------
Evidence: ServerName is not read anywhere in this build's load chain
--------------------------------------------------------------------------------------------
Per the WP3 brief: "grep the decompiled client for every read of ScenesPreset.ServerName and
decide whether it must equal the location id 'arena_equator' or may be 'arena_equator_tdm'".

    grep -rni "servername" D:/SPT41/Development/_decompiled
        -> exactly 1 hit: EFT/ScenesPreset.cs:55, the field DECLARATION (`public string
           ServerName;`). No read/comparison of it anywhere in the ~full client decompile.
    grep -rni "servername" D:/ArenaMapPort/analysis/decompiled-server-415
        -> 0 hits.
    Corroborating: Arena's own live TDM preset (`arena_equator_tdm_02_preset.bundle`, dumped
    to analysis/equator_tdm_preset.json) sets ServerName on its top-level
    "Arena_Equator.ScenesPreset" object to the arbitrary-looking "Arena_equator_TDM_02" --
    BSG's own tooling doesn't treat it as required to equal any location/scene id either.

Conclusion: ServerName is inert for this build -- neither client nor server load path reads
it. Kept as "arena_equator_tdm" (distinct from the SS0 location id "arena_equator") per
SPEC.md SS E.3, purely so a human skimming a typetree dump doesn't mistake it for the
location key; functionally either string works identically. Full writeup:
D:/ArenaMapPort/docs/progress/wp3.md.

--------------------------------------------------------------------------------------------
CORRECTION to SPEC.md SS B.WP3's literal step list: the AssetBundle container entry must
also be renamed, or the patched preset never loads
--------------------------------------------------------------------------------------------
SPEC.md SS B.WP3 steps 1-9 only touch the `ScenesPreset` MonoBehaviour's own typetree fields.
Tracing the actual client load path this pass (all citations in
D:/SPT41/Development/_decompiled) shows that is not sufficient:

  1. `base.json`'s `Scene` ResourceKey (path="maps/arena_equator_preset.bundle",
     rcid="arena_equator.scenespreset.asset") is used to build a `ScenePresetLoadConfig`
     (TarkovApplication.cs:2315 etc.) and passed to
     `Assets.Manager.LoadAssetAsync(preset.key)` (LoadScenesFromPresetOperation.cs:109).
  2. `AssetsManager.LoadAssetAsync(ResourceKey)` (EFT.AssetsManager/AssetsManager.cs:519-522)
     calls `LoadAssetAsync(resourceKey.path, resourceKey.ToAssetName())`.
     `ResourceKey.ToAssetName()` (EFT/ResourceKey.cs:22-39) returns `rcid` **verbatim** when
     non-empty -- i.e. the literal string "arena_equator.scenespreset.asset".
  3. The actual asset fetch (`AssetsManager.cs:216-236`, `LoadAssetOperation.LoadContent` /
     `LoadContentCoroutine`) does `string text = bundle.FixAssetName(assetName);` then
     `bundle.LoadAsset(text)` -- a real `UnityEngine.AssetBundle.LoadAsset(string name)` call.
  4. `AssetBundleExtension.FixAssetName` (EFT.AssetsManager/AssetBundleExtension.cs:21-45)
     resolves `assetName` against `bundle.GetAllAssetNames()` -- i.e. against the
     AssetBundle object's own `m_Container` path list -- via a suffix match (falls back to a
     case-insensitive filename-without-extension suffix match). It does **not** consult the
     MonoBehaviour's own `m_Name` field at all.
  5. Direct UnityPy read of the donor confirms the container entry is
     `m_Container[0] == ["Assets/Content/Locations/_Presets/develop.ScenesPreset.asset",
     {..., "asset": {"m_FileID": 0, "m_PathID": 7034189889941257301}}]` -- i.e. it points at
     exactly the same MonoBehaviour we patch, but under the OLD "develop" filename.

If step 5's container entry is left as "develop.ScenesPreset.asset" while base.json's rcid
asks for "arena_equator.scenespreset.asset", `FixAssetName`'s suffix match fails (different
base filename, not just different case), it logs "Can't fix assetName:..." and returns the
query unfixed, `AssetBundle.LoadAsset` then also fails to find anything, `LoadAssetAsync`
resolves to a null asset, the `ScenesPreset` cast fails, and
`LoadScenesFromPresetOperation.LoadPresetAsync` hits `SetFailed("Invalid preset")`
(LoadScenesFromPresetOperation.cs:112-138) -- the raid cannot start at all, regardless of how
correct every other field is. This patcher therefore ALSO renames the container entry (see
`patch_container_entry()` below) to a fully-lowercased path ending in
"arena_equator.scenespreset.asset", so `FixAssetName`'s primary (exact, no-fallback-needed)
suffix match succeeds directly. `AssetBundle.m_Name`/`m_AssetBundleName` are renamed too for
readability (BundlesManager.FindBundle keys purely off the caller-supplied bundle-name
string, EFT.AssetsManager/BundlesManager.cs:164-171 -- so this part is cosmetic, not
load-bearing, but costs nothing and avoids a confusing internal name mismatch).

This is flagged here as a deviation from SPEC.md's literal WP3 step list for the lead to
review -- the *fields* SPEC.md's SS E.3 table asserts are still produced exactly as written;
this is an addition, not a removal or contradiction.

--------------------------------------------------------------------------------------------
SECOND CORRECTION (fix-preset-cab-and-bundle-lifetime, 2026-09-16): the donor's own internal
bundle-directory name must also be made unique per map, or a second map's preset silently fails
to load in the same client session
--------------------------------------------------------------------------------------------
Every preset bundle this script has ever produced is a patched *copy* of the same SPT donor,
`develop_preset.bundle` (see DEFAULT_SRC above). Unity's own bundle packer gave that donor's
single nested SerializedFile the internal name `CAB-db2b26ca214c6211c4fae58b5cbe8d61` -- an
identifier Unity uses purely as a within-process de-duplication key for already-loaded
serialized files, stored ONLY in the outer BundleFile's directory table (confirmed by direct
UnityPy inspection: this string never appears inside the ScenesPreset MonoBehaviour's own
typetree, the AssetBundle object's m_Container/m_Dependencies/m_Name/m_AssetBundleName, or the
SerializedFile's own (empty) externals list -- renaming it changes nothing else about the
bundle's resolvable content).

Because this script starts every invocation from a fresh copy of the SAME donor and never
touched that name, all 14 of this project's preset bundles carry the exact same CAB name.
Confirmed empirically against the shipped `dist/` bundles (UnityPy, 2026-09-16): all 14
`*_preset.bundle` files' single nested SerializedFile is named
`CAB-db2b26ca214c6211c4fae58b5cbe8d61`, while every `*_scenes.bundle`/`*_navmesh.bundle` pair
already has unique names (Unity itself derives those from the authored scene names /
a fresh per-bake hash, so this collision is preset-bundle-specific).

Runtime symptom (`D:/SPT41vanilla/BepInEx/LogOutput.log`, 19:50:19, reproduced by raiding
`arena_bay5` then `arena_equator` in the same client session): Unity's AssetBundle system
refuses the SECOND `AssetBundle.LoadFromFile` call whose bundle contains a serialized file
already loaded under that same internal name --

    The AssetBundle '...\\arena_equator_preset.bundle' can't be loaded because another
    AssetBundle with the same files is already loaded.
    [Error  :maschine-ArenaMapPort] [Bundles] AssetBundle.LoadFromFile returned null for '...arena_equator_preset.bundle'
    Exception: Can't load bundle: maps/arena_equator_preset.bundle HTTP/1.1 404 Not Found

-- silently aborting the raid start, with no indication the actual cause is a name collision
against whichever OTHER map's preset bundle the same session already loaded.

Fix: `unique_cab_name()` below derives a deterministic `CAB-<md5>` name from the map's own key
(`md5("arenamapport:<key>:preset")` -- same length as the donor's own name, 36 characters
total, so the rename does not need to be paired with a matching in-file padding change
anywhere), and `patch_bundle_cab_name()` renames the donor copy's single nested SerializedFile
to it (both the `BundleFile.files` dict key UnityPy reads/writes the directory-table "path"
entry from, and the nested file's own `.name` attribute, kept in sync for consistency though
only the former is ever serialized). This is independent of, and in addition to, the
`m_Container` rename `patch_container_entry()` already performs above -- that one lets the
*client's asset lookup* (`FixAssetName`/`AssetBundle.LoadAsset(rcid)`) find the right object
inside a bundle that has already loaded; this one lets *Unity's own bundle loader* load a
second map's preset bundle at all once a different map's preset is already resident. Full
writeup: docs/progress/fix-preset-cab-and-bundle-lifetime.md.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
from pathlib import Path
from typing import NoReturn

import UnityPy
from UnityPy.files.SerializedFile import SerializedFile

# ----------------------------------------------------------------------------------------
# Constants (SPEC.md SS0 / SS B.WP3 / SS E.1 / SS E.3)
#
# Multimap generalization (docs/progress/multimap-generalize.md): every one of these used to be
# an Equator-only literal with no override. They are now DEFAULTS for the arena_equator map
# specifically (tools/maps/equator.json) -- main() loads a --map descriptor (default: that same
# equator.json) and overwrites these module globals from it before any of the patch_* functions
# below read them, so an unparameterized invocation is byte-for-byte unchanged from before this
# generalization (verified: py_compile + a full run against the donor bundle reproduces the exact
# same _scenesResourceKeys/ServerName/_activeSceneName/m_Name/m_Container output as before, see
# docs/progress/multimap-generalize.md).
# ----------------------------------------------------------------------------------------

REPO = Path(__file__).resolve().parents[2]
DEFAULT_MAP = REPO / "tools" / "maps" / "equator.json"
DEFAULT_SRC = Path("D:/SPT41/EscapeFromTarkov_Data/StreamingAssets/Windows/maps/develop_preset.bundle")

SCENE_BUNDLE_KEY = "arena_equator/arena_equator_scenes.bundle"  # SPEC.md SS0 / SS E.2
PRESET_BUNDLE_KEY = "maps/arena_equator_preset.bundle"          # SPEC.md SS0 -- served key
PRESET_ASSET_NAME = "arena_equator.scenespreset.asset"          # SPEC.md SS0 -- base.json Scene.rcid

# SPEC.md SS B.WP3 step 3 / SS E.1 -- exact order, Scripts scene first (LoadSceneMode.Single)
SCENE_TABLE = [
    "Arena_Equator_Scripts",
    "Arena_Equator",
    "Arena_Equator_Lighting",
    "Arena_Equator_DesignStuff",
    "Arena_Equator_TeamFight",
    "Arena_Equator_TeamFight_DesignStuff",
]

SERVER_NAME = "arena_equator_tdm"            # SPEC.md SS E.3 -- see module docstring, unused by load chain
ACTIVE_SCENE_NAME = "Arena_Equator_Scripts"  # SPEC.md SS B.WP3 step 5
PRESET_M_NAME = "arena_equator.ScenesPreset"  # SPEC.md SS B.WP3 step 8


def load_map(map_path: Path) -> dict:
    """Reads one tools/maps/<key>.json descriptor (the same file the Editor's ArenaPaths.cs and
    tools/server/make_base_json.py load) -- see either file's own doc comment for the field list."""
    return json.loads(map_path.read_text(encoding="utf-8"))


def apply_map(map_data: dict) -> None:
    """Overwrites this module's Equator-only-by-default globals from a loaded map descriptor.
    Bundle served-key/asset-name naming follows the project-wide convention
    (docs/progress/multimap-generalize.md): scenes served key "<key>/<key>_scenes.bundle", preset
    served key "maps/<key>_preset.bundle", preset asset name "<key>.scenespreset.asset",
    ScenesPreset m_Name "<key>.ScenesPreset" -- so the descriptor doesn't need to spell any of
    those out, only the scene-name table and identity/display fields that have no derivable
    convention."""
    global SCENE_BUNDLE_KEY, PRESET_BUNDLE_KEY, PRESET_ASSET_NAME, SCENE_TABLE
    global SERVER_NAME, ACTIVE_SCENE_NAME, PRESET_M_NAME

    key = map_data["key"]
    SCENE_BUNDLE_KEY = f"{key}/{key}_scenes.bundle"
    PRESET_BUNDLE_KEY = f"maps/{key}_preset.bundle"
    PRESET_ASSET_NAME = f"{key}.scenespreset.asset"
    SCENE_TABLE = list(map_data["all_scene_names"])
    SERVER_NAME = f"{key}_tdm"
    ACTIVE_SCENE_NAME = map_data["scripts_scene_name"]
    PRESET_M_NAME = f"{key}.ScenesPreset"

# EFT.ScenesPreset's real field set (D:/SPT41/Development/_decompiled/EFT/ScenesPreset.cs:10-99,
# directly re-read this pass). No `GameModes` member exists on this class -- an earlier
# evidence-gathering pass incorrectly reported one; see SPEC.md SS B.WP3 step 6 / SS E.3 note.
# If a future donor swap's raw typetree ever contains a key outside this set, FAIL LOUDLY
# instead of silently dropping or blindly writing it.
KNOWN_SCENESPRESET_FIELDS = {
    "m_GameObject", "m_Enabled", "m_Script", "m_Name",
    "ActiveSceneGuid", "ServerName", "ScenesGuids", "ChildPresets",
    "_activeSceneName", "_scenesResourceKeys",
}

EXPECTED_MONOSCRIPT = {"m_ClassName": "ScenesPreset", "m_Namespace": "EFT", "m_AssemblyName": "Assembly-CSharp"}


def fail(msg: str) -> NoReturn:
    print(f"[patch_preset] FATAL: {msg}", file=sys.stderr)
    sys.exit(1)


def info(msg: str) -> None:
    print(f"[patch_preset] {msg}")


# ----------------------------------------------------------------------------------------
# Steps
# ----------------------------------------------------------------------------------------

def stage_donor_copy(src: Path, copy_path: Path) -> Path:
    """Copy the read-only donor to our own workspace before UnityPy ever touches it (O8)."""
    if not src.is_file():
        fail(f"donor bundle not found: {src}")
    copy_path.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(src, copy_path)
    info(f"staged donor copy: {src} -> {copy_path} ({copy_path.stat().st_size} bytes)")
    return copy_path


def find_scenespreset_and_bundle(env):
    """Return (mono_obj, mono_tt, bundle_obj, bundle_tt). Fails loudly on 0 or >1 matches."""
    mono_matches = []
    bundle_obj = None
    for obj in env.objects:
        if obj.type.name == "MonoBehaviour":
            tt = obj.read_typetree()
            if "ScenesPreset" in str(tt.get("m_Name", "")):
                mono_matches.append((obj, tt))
        elif obj.type.name == "AssetBundle":
            if bundle_obj is not None:
                fail("donor bundle has more than one AssetBundle object -- unexpected, refusing to guess")
            bundle_obj = obj

    if len(mono_matches) == 0:
        fail('no MonoBehaviour with "ScenesPreset" in m_Name found in donor bundle')
    if len(mono_matches) > 1:
        fail(
            f"donor bundle has {len(mono_matches)} MonoBehaviours matching \"ScenesPreset\" "
            f"(path_ids: {[o.path_id for o, _ in mono_matches]}) -- SPEC.md SS B.WP3 edge case: "
            f"must fail loudly rather than patch the first match"
        )
    if bundle_obj is None:
        fail("no AssetBundle object found in donor bundle -- can't verify/patch the container entry")

    mono_obj, mono_tt = mono_matches[0]
    bundle_tt = bundle_obj.read_typetree()
    return mono_obj, mono_tt, bundle_obj, bundle_tt


def verify_monoscript(env, mono_tt: dict) -> None:
    """Confirm m_Script resolves to EFT.ScenesPreset / Assembly-CSharp *before* we touch anything."""
    script_ptr = mono_tt.get("m_Script") or {}
    file_id, path_id = script_ptr.get("m_FileID"), script_ptr.get("m_PathID")
    if file_id != 0:
        fail(
            f"donor MonoBehaviour's m_Script PPtr has m_FileID={file_id} (expected 0, i.e. embedded "
            f"in the same file) -- the embedded-MonoScript identity check below assumes a local PPtr; "
            f"re-verify manually for this donor"
        )
    script_obj = None
    for obj in env.objects:
        if obj.type.name == "MonoScript" and obj.path_id == path_id:
            script_obj = obj
            break
    if script_obj is None:
        fail(f"donor's m_Script PPtr (path_id={path_id}) does not resolve to any embedded MonoScript object")
    script_tt = script_obj.read_typetree()
    actual = {k: script_tt.get(k) for k in EXPECTED_MONOSCRIPT}
    if actual != EXPECTED_MONOSCRIPT:
        fail(
            f"donor's embedded MonoScript is {actual}, expected {EXPECTED_MONOSCRIPT} -- this donor's "
            f"MonoBehaviour.m_Script does not resolve to EFT.ScenesPreset in Assembly-CSharp, refusing to patch"
        )
    info(f"verified m_Script -> MonoScript(path_id={path_id}) == {EXPECTED_MONOSCRIPT}")


def verify_field_set(mono_tt: dict) -> None:
    actual = set(mono_tt.keys())
    extra = actual - KNOWN_SCENESPRESET_FIELDS
    missing = KNOWN_SCENESPRESET_FIELDS - actual
    if extra:
        fail(
            f"donor's ScenesPreset typetree has unexpected extra key(s) {sorted(extra)} not present on "
            f"EFT.ScenesPreset (D:/SPT41/Development/_decompiled/EFT/ScenesPreset.cs) -- SPEC.md SS B.WP3 "
            f"step 6 requires failing loudly here instead of guessing whether to write them "
            f"(this is exactly the historical 'GameModes' false-positive -- re-verify against the live "
            f"decompile before adding the key to KNOWN_SCENESPRESET_FIELDS)"
        )
    if missing:
        fail(f"donor's ScenesPreset typetree is missing expected key(s) {sorted(missing)}")
    info(f"donor field set matches EFT.ScenesPreset exactly: {sorted(actual)}")


def patch_mono_typetree(mono_tt: dict, server_name: str) -> dict:
    donor_guids = mono_tt.get("ScenesGuids") or []
    info(f"donor ScenesGuids length = {len(donor_guids)} (must not assume >= {len(SCENE_TABLE)})")

    scenes_guids = list(donor_guids)[: len(SCENE_TABLE)]
    while len(scenes_guids) < len(SCENE_TABLE):
        scenes_guids.append({"guid": "", "_onlyOffline": 0})  # inert placeholder, SPEC.md SS B.WP3 step 7

    resource_keys = [
        {"path": SCENE_BUNDLE_KEY, "rcid": rcid, "_onlyOffline": 0}
        for rcid in SCENE_TABLE
    ]

    mono_tt["ServerName"] = server_name                       # step 4
    mono_tt["_activeSceneName"] = ACTIVE_SCENE_NAME            # step 5
    # ActiveSceneGuid left as-is (donor's) -- inert, step 5
    mono_tt["ChildPresets"] = []                               # step 6
    mono_tt["ScenesGuids"] = scenes_guids                      # step 7
    mono_tt["m_Name"] = PRESET_M_NAME                          # step 8
    mono_tt["_scenesResourceKeys"] = resource_keys             # step 3

    info(f"patched _scenesResourceKeys ({len(resource_keys)} entries):")
    for e in resource_keys:
        info(f"    rcid={e['rcid']!r} path={e['path']!r}")
    info(f"patched ServerName={server_name!r} _activeSceneName={ACTIVE_SCENE_NAME!r} "
         f"ChildPresets=[] ScenesGuids(len={len(scenes_guids)}) m_Name={PRESET_M_NAME!r}")
    return mono_tt


def unique_cab_name(map_key: str) -> str:
    """Deterministic per-map internal bundle-directory name (see module docstring's "SECOND
    CORRECTION" section for the full root-cause evidence and why this must be unique across
    every map's preset bundle, not just readable/valid). `CAB-` + an md5 hex digest matches the
    donor's own naming convention and length (36 chars total) exactly, so this never needs a
    padding change anywhere it's referenced."""
    digest = hashlib.md5(f"arenamapport:{map_key}:preset".encode("utf-8")).hexdigest()
    return f"CAB-{digest}"


def patch_bundle_cab_name(env, new_cab_name: str) -> str:
    """Renames the donor copy's single nested SerializedFile's internal bundle-directory entry
    (its 'CAB-...' name) to `new_cab_name`. Fails loudly if the bundle doesn't have exactly one
    nested SerializedFile -- this project's preset bundles never have a second nested file (no
    streamed .resS -- the ScenesPreset MonoBehaviour has no resource-stream fields), so more or
    fewer than 1 is unexpected and this script must not guess which one to rename.

    Only touches the outer BundleFile.files dict key (what UnityPy's BundleFile.save_fs()
    actually serializes as the directory-table "path" for each nested file) plus the nested
    file's own .name attribute for consistency -- see module docstring for why nothing else in
    the bundle's serialized content ever references this string."""
    bundle_file = env.file
    matches = [name for name, f in bundle_file.files.items() if isinstance(f, SerializedFile)]
    if len(matches) != 1:
        fail(
            f"expected exactly 1 nested SerializedFile in the donor bundle to rename for CAB "
            f"uniqueness, found {len(matches)}: {matches} -- refusing to guess which one to rename"
        )
    old_cab_name = matches[0]
    if old_cab_name == new_cab_name:
        fail(
            f"donor's CAB name {old_cab_name!r} already equals the target {new_cab_name!r} -- "
            f"unexpected (a real collision fix should always change this string), refusing to "
            f"silently no-op the rename"
        )
    f = bundle_file.files.pop(old_cab_name)
    f.name = new_cab_name
    bundle_file.files[new_cab_name] = f
    info(f"patched BundleFile nested SerializedFile (CAB) name: {old_cab_name!r} -> {new_cab_name!r}")
    return old_cab_name


def patch_container_entry(bundle_tt: dict, mono_path_id: int) -> dict:
    """CORRECTION (see module docstring): rename the AssetBundle's container entry so
    FixAssetName()/AssetBundle.LoadAsset(rcid) can still find the ScenesPreset asset."""
    container = list(bundle_tt.get("m_Container") or [])
    match_indices = [
        i for i, entry in enumerate(container)
        if isinstance(entry, (list, tuple)) and len(entry) == 2
        and (entry[1].get("asset") or {}).get("m_PathID") == mono_path_id
    ]
    if len(match_indices) != 1:
        fail(
            f"expected exactly 1 m_Container entry pointing at the ScenesPreset MonoBehaviour "
            f"(path_id={mono_path_id}), found {len(match_indices)} -- refusing to guess which to rename"
        )
    idx = match_indices[0]
    old_key, old_value = container[idx]
    old_dir = old_key.rsplit("/", 1)[0] if "/" in old_key else ""
    new_key = f"{old_dir.lower()}/{PRESET_ASSET_NAME}" if old_dir else PRESET_ASSET_NAME
    container[idx] = [new_key, old_value]
    bundle_tt["m_Container"] = container
    info(f"patched AssetBundle m_Container entry: {old_key!r} -> {new_key!r}")

    old_bundle_name = bundle_tt.get("m_Name")
    bundle_tt["m_Name"] = PRESET_BUNDLE_KEY
    bundle_tt["m_AssetBundleName"] = PRESET_BUNDLE_KEY
    info(f"patched AssetBundle m_Name/m_AssetBundleName: {old_bundle_name!r} -> {PRESET_BUNDLE_KEY!r} "
         f"(cosmetic -- BundlesManager.FindBundle keys off the caller-supplied string, not this field)")
    return bundle_tt


def save_and_verify(env, mono_obj, mono_path_id: int, bundle_obj, out_path: Path, packer: str,
                     expected_cab_name: str) -> None:
    donor_script_ptr = None
    for obj in env.objects:
        if obj.type.name == "MonoBehaviour" and obj.path_id == mono_path_id:
            donor_script_ptr = obj.read_typetree().get("m_Script")
            break

    data = env.file.save(packer=packer)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(data)
    info(f"wrote {out_path} ({len(data)} bytes, packer={packer})")

    # re-read what we just wrote, from disk, independent of the in-memory env we patched
    reread = UnityPy.load(str(out_path))
    mono_reread = None
    bundle_reread = None
    for obj in reread.objects:
        if obj.type.name == "MonoBehaviour" and obj.path_id == mono_path_id:
            mono_reread = obj.read_typetree()
        elif obj.type.name == "AssetBundle":
            bundle_reread = obj.read_typetree()
    if mono_reread is None:
        fail("re-read of saved bundle: could not find the ScenesPreset MonoBehaviour again")
    if mono_reread.get("m_Script") != donor_script_ptr:
        fail(
            f"re-read of saved bundle: m_Script PPtr changed! donor={donor_script_ptr} "
            f"saved={mono_reread.get('m_Script')} -- the save corrupted MonoScript identity"
        )
    info(f"re-read verification: m_Script PPtr unchanged ({mono_reread.get('m_Script')})")

    if bundle_reread is not None:
        container = bundle_reread.get("m_Container") or []
        keys = [e[0] for e in container if isinstance(e, (list, tuple)) and len(e) == 2]
        if not any(k.endswith(PRESET_ASSET_NAME) for k in keys):
            fail(f"re-read of saved bundle: no m_Container entry ends with {PRESET_ASSET_NAME!r} (keys={keys})")
        info(f"re-read verification: m_Container has an entry ending with {PRESET_ASSET_NAME!r}")

    # re-read verification (fix-preset-cab-and-bundle-lifetime): the CAB rename must have
    # actually landed in the bytes written to disk, from a fresh, independent load -- not just
    # in the in-memory `env` this function already had a reference to before saving.
    reread_names = [name for name, f in reread.file.files.items() if isinstance(f, SerializedFile)]
    if reread_names != [expected_cab_name]:
        fail(
            f"re-read of saved bundle: nested SerializedFile name(s) {reread_names!r} != expected "
            f"[{expected_cab_name!r}] -- the CAB rename did not survive the save/reload round-trip"
        )
    info(f"re-read verification: nested SerializedFile (CAB) name is {expected_cab_name!r}")


# ----------------------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------------------

def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--map", type=Path, default=DEFAULT_MAP,
                     help="tools/maps/<key>.json descriptor (default: equator.json, byte-identical "
                          "to this script's pre-generalization behavior)")
    ap.add_argument("--src", type=Path, default=DEFAULT_SRC, help="donor develop_preset.bundle (read-only source)")
    ap.add_argument("--out", type=Path, default=None,
                     help="patched output bundle path (default: work/build/preset/<key>_preset.bundle)")
    ap.add_argument("--donor-copy", type=Path, default=None,
                     help="where to stage the read-only donor's copy before patching "
                          "(default: <out's dir>/_donor_develop_preset.bundle)")
    ap.add_argument("--server-name", default=None,
                     help="ScenesPreset.ServerName value (default: '<key>_tdm', e.g. 'arena_equator_tdm')")
    ap.add_argument("--packer", choices=["none", "lz4", "lzma", "original"], default="none",
                     help="BundleFile.save() compression (default: none, matches the Icebreaker reference script)")
    args = ap.parse_args()

    map_data = load_map(args.map)
    apply_map(map_data)
    if args.out is None:
        args.out = REPO / "work" / "build" / "preset" / f"{map_data['key']}_preset.bundle"
    if args.server_name is None:
        args.server_name = SERVER_NAME  # apply_map already derived "<key>_tdm"

    donor_copy = args.donor_copy or (args.out.parent / "_donor_develop_preset.bundle")

    copy_path = stage_donor_copy(args.src, donor_copy)
    env = UnityPy.load(str(copy_path))

    mono_obj, mono_tt, bundle_obj, bundle_tt = find_scenespreset_and_bundle(env)
    info(f"found ScenesPreset MonoBehaviour: path_id={mono_obj.path_id} m_Name={mono_tt.get('m_Name')!r}")

    verify_monoscript(env, mono_tt)
    verify_field_set(mono_tt)

    mono_path_id = mono_obj.path_id
    mono_tt = patch_mono_typetree(mono_tt, args.server_name)
    mono_obj.save_typetree(mono_tt)

    bundle_tt = patch_container_entry(bundle_tt, mono_path_id)
    bundle_obj.save_typetree(bundle_tt)

    # fix-preset-cab-and-bundle-lifetime (module docstring "SECOND CORRECTION"): every map's
    # preset bundle must carry a unique internal name, or a second map's preset silently fails
    # to load (Unity refuses a second AssetBundle whose serialized file shares an
    # already-loaded internal name) once a different map's preset is already resident in the
    # same client session.
    new_cab_name = unique_cab_name(map_data["key"])
    patch_bundle_cab_name(env, new_cab_name)

    save_and_verify(env, mono_obj, mono_path_id, bundle_obj, args.out, args.packer, new_cab_name)

    # the staged donor copy served its purpose (UnityPy never writes to it either way, since
    # we always save to a fresh writer -- but SPEC.md O8 wants the read-only source untouched
    # and this makes that guarantee obvious/auditable, not just incidentally true).
    info("done.")


if __name__ == "__main__":
    main()
