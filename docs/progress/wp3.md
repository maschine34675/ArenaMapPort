# WP3 — Preset bundle patcher — progress log

Status: **done** (offline). Not tested in a live raid (that's WP6's §G.S4, needs WP2/WP4/WP5
too). Everything below was run and verified this session.

## What was built

- `tools/preset/patch_preset.py` — patches a copy of the read-only donor
  `D:/SPT41/EscapeFromTarkov_Data/StreamingAssets/Windows/maps/develop_preset.bundle` into
  `D:/ArenaMapPort/work/build/preset/arena_equator_preset.bundle`, per SPEC.md §B.WP3/§E.3.
  Accepts `--src`/`--out`/`--donor-copy`/`--server-name`/`--packer`.
- `tools/validate/validate_preset.py` — re-reads the patched bundle and independently asserts
  every §E.3 field, MonoScript identity, and the container-entry fix (see below). Accepts
  `--bundle`/`--scenes-bundle` (auto-probes `work/build/s1/*scenes*.bundle` if `--scenes-bundle`
  is omitted).
- `THIRD-PARTY-NOTICES.md` — created (didn't exist yet), WP3's attribution entry for the
  `patch_icebreaker_preset.py` pattern reuse.

Both scripts run clean:

```
python tools/preset/patch_preset.py
python tools/validate/validate_preset.py
  -> [validate_preset] OK -- all checks passed for .../arena_equator_preset.bundle
  -> "no scenes bundle found under work/build/s1 -- running preset-only checks" (WP2 hasn't
     produced one yet this session; the flag/auto-probe is wired and ready)
```

Confirmed the donor source is untouched: `develop_preset.bundle`'s mtime (Aug 2, 6656 bytes)
is unchanged after every run — the script always stages its own copy first
(`work/build/preset/_donor_develop_preset.bundle`) and only ever opens that copy with UnityPy.

## ServerName evidence (WP3 brief's explicit ask)

> "Before choosing ServerName, grep the decompiled client for every read of
> ScenesPreset.ServerName and decide whether it must equal the location id 'arena_equator' or
> may be 'arena_equator_tdm'."

```
grep -rni "servername" D:/SPT41/Development/_decompiled
  -> 1 hit: EFT/ScenesPreset.cs:55, the field DECLARATION only ("public string ServerName;").
     Zero reads/comparisons anywhere else in the client decompile.
grep -rni "servername" D:/ArenaMapPort/analysis/decompiled-server-415
  -> 0 hits.
```

Corroborating (not authoritative, but consistent): Arena's own live TDM preset
(`analysis/equator_tdm_preset.json`, dumped from `arena_equator_tdm_02_preset.bundle`) sets
`ServerName` on its top-level `"Arena_Equator.ScenesPreset"` object to the arbitrary
`"Arena_equator_TDM_02"` — BSG's own tooling doesn't treat it as needing to equal any
location/scene id either.

**Decision**: `ServerName` is functionally inert in this build's load chain — neither client
nor server reads it. Kept as `"arena_equator_tdm"` per SPEC.md §E.3 (distinct from the §0
location id `"arena_equator"`, purely so it doesn't get misread as the location key when
skimming a typetree dump — no functional difference either way).

## Correction to SPEC.md §B.WP3's literal step list: the AssetBundle container entry must also be renamed

This is the one substantive finding from this pass, flagged for lead review.

SPEC.md §B.WP3 steps 1–9 only rewrite the `ScenesPreset` MonoBehaviour's own typetree fields
(`ServerName`, `_activeSceneName`, `ChildPresets`, `ScenesGuids`, `_scenesResourceKeys`,
`m_Name`). Tracing the client's actual load path this pass shows that alone is not enough for
the patched preset to be *loadable at all*:

1. `base.json`'s `Scene` ResourceKey (`path="maps/arena_equator_preset.bundle"`,
   `rcid="arena_equator.scenespreset.asset"`, per §0/§E.4) feeds into
   `Assets.Manager.LoadAssetAsync(preset.key)`
   (`D:/SPT41/Development/_decompiled/EFT/LoadScenesFromPresetOperation.cs:109`).
2. `AssetsManager.LoadAssetAsync(ResourceKey)`
   (`EFT.AssetsManager/AssetsManager.cs:519-522`) calls
   `LoadAssetAsync(resourceKey.path, resourceKey.ToAssetName())`.
   `ResourceKey.ToAssetName()` (`EFT/ResourceKey.cs:22-39`) returns `rcid` **verbatim**.
3. The actual fetch (`EFT.AssetsManager/AssetsManager.cs:216-236`) does
   `string text = bundle.FixAssetName(assetName); bundle.LoadAsset(text);` — a real
   `UnityEngine.AssetBundle.LoadAsset(string name)` call.
4. `AssetBundleExtension.FixAssetName` (`EFT.AssetsManager/AssetBundleExtension.cs:21-45`)
   resolves that name against `bundle.GetAllAssetNames()` — i.e. against the `AssetBundle`
   object's own `m_Container` path table — via a suffix match. It never looks at the
   MonoBehaviour's own `m_Name` field at all.
5. Direct UnityPy read of the donor's `AssetBundle` object:
   `m_Container[0] == ["Assets/Content/Locations/_Presets/develop.ScenesPreset.asset",
   {..., "asset": {"m_FileID": 0, "m_PathID": 7034189889941257301}}]` — pointing at the same
   MonoBehaviour we patch, but registered under the **old** `"develop"` filename.

If that container entry is left alone while we rewrite the MonoBehaviour's own `m_Name` to
`"arena_equator.ScenesPreset"` and expect `base.json`'s
`rcid="arena_equator.scenespreset.asset"` to resolve it: `FixAssetName`'s suffix match fails
(different base filename, not just different case — `"develop.scenespreset"` does not end with
`"arena_equator.scenespreset"` under any case folding), it logs `"Can't fix assetName:..."` and
returns the query unfixed, `AssetBundle.LoadAsset` then also finds nothing, the asset load
resolves to `null`, the `ScenesPreset` cast fails, and
`LoadScenesFromPresetOperation.LoadPresetAsync` hits `SetFailed("Invalid preset")`
(`LoadScenesFromPresetOperation.cs:112-138`) — **the raid cannot start at all**, regardless of
how correct every other field is. This would have been a very expensive bug to chase at S4
(live-raid) time, since every offline field-level check in §E.3 would still pass.

**Fix implemented**: `patch_preset.py` additionally renames that `m_Container` entry to
`assets/content/locations/_presets/arena_equator.scenespreset.asset` (fully lowercased, so
`FixAssetName`'s *primary* exact-suffix match succeeds directly without needing its
case-insensitive fallback branch), and — cosmetically, confirmed non-load-bearing via
`BundlesManager.FindBundle` (`EFT.AssetsManager/BundlesManager.cs:164-171`, which keys purely
off the caller-supplied bundle-name string) — renames the `AssetBundle` object's own `m_Name`/
`m_AssetBundleName` from `"maps/develop_preset.bundle"` to `"maps/arena_equator_preset.bundle"`
to match §0's served key and avoid a confusing internal mismatch.

`validate_preset.py` independently re-checks this (`validate_container()`): asserts exactly one
`m_Container` entry points at the `ScenesPreset` MonoBehaviour's `path_id`, and that its key
ends with `arena_equator.scenespreset.asset`.

**This is an addition to SPEC.md's WP3 algorithm, not a contradiction** — every field §E.3
specifies is still produced exactly as written; one more field (belonging to a different
serialized object in the same bundle file) is also patched, because it's required for the
already-specified fields to actually take effect at runtime. Recommend the lead fold this into
SPEC.md §B.WP3/§E.3 so a future re-implementation doesn't rediscover it the hard way.

## Verified facts (re-confirmed live this pass, not assumed from SPEC.md's earlier dump)

- Donor bundle has exactly 1 `MonoBehaviour` matching `"ScenesPreset"` in `m_Name`
  (`path_id=7034189889941257301`), plus 1 `AssetBundle` object and 1 embedded `MonoScript`
  object (`path_id=-7501068993988226796`).
- The embedded `MonoScript` resolves to `m_ClassName="ScenesPreset"`, `m_Namespace="EFT"`,
  `m_AssemblyName="Assembly-CSharp"` — i.e. the donor's `MonoBehaviour.m_Script` genuinely
  binds to `EFT.ScenesPreset` in `Assembly-CSharp`, confirmed by directly reading the embedded
  object rather than assuming from the class name string alone. `patch_preset.py` asserts this
  *before* touching anything, and `validate_preset.py` re-asserts it after.
- Donor's raw typetree key set is exactly
  `{ActiveSceneGuid, ChildPresets, ScenesGuids, ServerName, _activeSceneName,
  _scenesResourceKeys, m_Enabled, m_GameObject, m_Name, m_Script}` — **no `GameModes` key**,
  confirming SPEC.md §B.WP3 step 6's correction (an earlier evidence pass's claim was wrong).
  `patch_preset.py` hard-fails if a future donor swap's typetree doesn't match this set exactly,
  rather than guessing whether to write an unrecognized field.
- Donor's `ScenesGuids` has 3 entries (not ≥6) — padded to 6 with inert
  `{"guid": "", "_onlyOffline": 0}` placeholders per SPEC.md §B.WP3 step 7.
- `m_Script`'s PPtr (`m_FileID=0, m_PathID=-7501068993988226796`) is confirmed byte-identical
  before/after the save, via an independent re-read of the file written to disk (not just the
  in-memory UnityPy object) — this is §B.WP3's stated acceptance criterion #3.

## What's NOT done / known gaps

- `validate_preset.py --scenes-bundle` hasn't been exercised against a real WP2-built scenes
  bundle yet (WP2 hasn't produced one — `work/extract/equator_s1/` and `work/build/s1/` are
  both empty this session). The auto-probe and cross-check logic are written and will run
  automatically once a `*scenes*.bundle` file exists under `work/build/s1/`, or can be pointed
  at one explicitly via `--scenes-bundle <path>`. Re-run
  `python tools/validate/validate_preset.py` once WP2 lands.
- No live-raid test (§G.S4) — needs WP2 (scene bundle + navmesh bundle), WP4 (client plugin to
  actually serve these 3 bundles + rebind shaders + add navmesh), and WP5 (server registration +
  `base.json`) before that's even attempted.

## Files

- `D:/ArenaMapPort/tools/preset/patch_preset.py`
- `D:/ArenaMapPort/tools/validate/validate_preset.py`
- `D:/ArenaMapPort/THIRD-PARTY-NOTICES.md` (created)
- `D:/ArenaMapPort/work/build/preset/arena_equator_preset.bundle` (gitignored build output)
- `D:/ArenaMapPort/work/build/preset/_donor_develop_preset.bundle` (gitignored staged donor copy)
