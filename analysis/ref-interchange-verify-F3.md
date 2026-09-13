# Adversarial verification: ref-interchange/F3

## Claim under test
"Scene swap is a single Harmony prefix on `LoadScenesFromPresetOperation.LoadPresetAsync` that
reflectively rewrites `ScenesPreset._scenesResourceKeys` path/rcid fields from base names to
`_IR`-suffixed names, gated on `preset.ServerName=='interchange'`."

Cited evidence: `InterchangeRework.Client/PresetSwap.cs:32-34` (HarmonyPatch attr), `:40` (ServerName
gate), `:94-134` (RewriteEntries via AccessTools.Field on 'path'/'rcid').

## Verdict: CONFIRMED (mechanism accurate), with three precision corrections

## What I read
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client/PresetSwap.cs` (full file, 172 lines)
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Shared/SceneNames.cs` (full file)
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Tests/{SceneNamesTests,NativeSceneTests}.cs`
- Grep of every `[HarmonyPatch` in `InterchangeRework.Client/*.cs` and a whole-repo grep for
  `LoadPresetAsync`/`LoadScenesFromPresetOperation` to check the "single" claim
- Decompiled real EFT classes for ground truth: `D:/SPT41/Development/_decompiled/EFT/LoadScenesFromPresetOperation.cs`,
  `.../EFT/ScenesPreset.cs`, `.../EFT/ResourceKey.cs`, `.../EFT/SceneResourceKey.cs`

## Point-by-point

**1. "Single Harmony prefix on `LoadScenesFromPresetOperation.LoadPresetAsync`" — confirmed.**
`PresetSwap.cs:32-34`:
```
[HarmonyPatch(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetAsync))]
[HarmonyPrefix]
static void BeforePresetLoad(ScenesPreset preset)
```
A whole-repo grep for `LoadPresetAsync|LoadScenesFromPresetOperation` across all `*.cs` in the source
tree returns exactly this one hit — no other patch (prefix, postfix, transpiler) touches this method
anywhere in the mod. `InterchangeRework.Client` has 9 other `[HarmonyPatch]` sites
(`InteractionLog.cs`, `KeypadBeep.cs`, `LocationSceneAwakeGuard.cs`, `MapVariantPrompt.cs`,
`NavMeshExtension.cs`, `QuietAwakes.cs`×2, `SpatialAudioBakeSwap.cs`×2, `WaypointsStandDown.cs`), all on
unrelated targets (interactables, keycard doors, sniper imitator awake, GPU instancer terrain detail).
None target scene loading or path rewriting. "Single" holds.

Cross-checked against the real (decompiled) EFT class: `LoadScenesFromPresetOperation.cs` in
`_decompiled` has exactly one method named `LoadPresetAsync` — `public async Task
LoadPresetAsync(ScenesPreset preset)` — so `nameof(...)` is unambiguous and the prefix's own
`(ScenesPreset preset)` parameter matches the real method's parameter name/type exactly (required for
Harmony to bind by name). This is a real, existing method, not a guess.

**2. "Reflectively rewrites `ScenesPreset._scenesResourceKeys` path/rcid fields ... via
AccessTools.Field" — confirmed, mechanism matches the real field layout.**
`PresetSwap.cs:94` `AccessTools.Field(typeof(ScenesPreset), "_scenesResourceKeys")`,
`:104`/`:105` `AccessTools.Field(key.GetType(), "path"/"rcid")`, then `:128-129`
`_pathField.SetValue(key, newPath)` / `_rcidField.SetValue(key, newPath)`.

Ground truth in `_decompiled/EFT/ScenesPreset.cs:69`: `public List<SceneResourceKey>
_scenesResourceKeys;` — field exists, matches the name used. Note it is actually a **public** field
(not private/internal) — AccessTools.Field still works on public fields, so this isn't a bug, just
worth noting the reflection isn't defeating an access modifier, it's defeating not having a compile-time
reference to the (presumably internal-only-at-build-time or version-fragile) type layout.
`_decompiled/EFT/ResourceKey.cs:15,18`: `public string path;` / `public string rcid;` — confirmed, and
`SceneResourceKey : ResourceKey` (`_decompiled/EFT/SceneResourceKey.cs:10`), so the fields
`AccessTools.Field(key.GetType(), "path")` finds on a `SceneResourceKey` instance are inherited from
the base class; Harmony's `AccessTools.Field` walks the type hierarchy, so this resolves correctly.

**3. "Gated on `preset.ServerName=='interchange'`" — confirmed but imprecisely phrased.**
Actual code, `PresetSwap.cs:40`:
```
if (!string.Equals(preset.ServerName, SceneNames.PresetServerName, StringComparison.OrdinalIgnoreCase)) return;
```
`SceneNames.cs:13`: `public const string PresetServerName = "interchange";` — so the compared value is
indeed the literal `"interchange"`, confirmed. But the comparison is **case-insensitive**
(`OrdinalIgnoreCase`) against a **named constant**, not a literal `==`. Functionally equivalent for the
finding's purpose but the paraphrase overstates precision. `ScenesPreset.ServerName` is a public field
(`_decompiled/EFT/ScenesPreset.cs:55`: `public string ServerName;`), also confirmed real.

## What the finding leaves out (sharpening)

- **Not every scene entry gets rewritten — 17 of 18.** `SceneNames.BaseNames` lists 18 Shopping_Mall
  scenes; `NativeSceneNames = ["Shopping_Mall_Scripts"]` is explicitly excluded, and
  `SwappedBaseNames = BaseNames - NativeSceneNames` (17 entries) is the actual rewrite set
  (`SceneNames.cs:37-46`). `Shopping_Mall_Scripts` is deliberately left pointing at the client's own
  copy — comment at `SceneNames.cs:36`: "the rollback for any scene." So describing the patch as
  rewriting "base names to `_IR`-suffixed names" without qualification slightly overstates scope; one
  scene is intentionally held back per-design, not a bug.
- **The rewrite path is idempotent/self-detecting, not a blind overwrite.** `RewriteEntries`
  (`PresetSwap.cs:112-118`) calls `SceneNames.RewriteScenePath(path)`; when that returns `null` (path's
  file isn't in the swap set — either the native `_Scripts` scene *or* a path already ending in `_IR`
  from an earlier raid this process), it separately calls `RevertScenePath(path)` just to test whether
  it's already an IR name, and if so counts it as already-matched (`n++`, `alreadyIr++`) **without
  mutating it again**. This matters because the `ScenesPreset` is a cached `ScriptableObject` that lives
  for the process (per the comment at `PresetSwap.cs:41` and `:91`), so a second raid in the same
  session would otherwise be rewritten twice or misdetected as a partial-swap failure.
- **`rcid` is only touched when already non-empty**, and is set equal to the new `path`, not
  independently rewritten (`PresetSwap.cs:129`, `:155`) — the comment at `:120` notes this is because
  "rcid is byte-identical to path in this build," an assumption the code itself flags as build-specific
  (logs a warning-level anomaly on the first entry if it's ever untrue, `:125-126`).
- **Symmetric `RevertEntries`** (`:136-160`) exists for the same reason: since the preset is mutated
  in-place and cached process-wide, opting out or a missing bundle must actively undo any earlier swap,
  not just skip swapping.

## Files/lines cited (for the record)
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client/PresetSwap.cs:32-34,40,94-134,136-160`
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Shared/SceneNames.cs:13,17-33,36-46`
- `D:/SPT41/Development/_decompiled/EFT/LoadScenesFromPresetOperation.cs` (method `LoadPresetAsync`, single overload)
- `D:/SPT41/Development/_decompiled/EFT/ScenesPreset.cs:55,68-69,75` (`ServerName`, `_scenesResourceKeys`, filtered `ScenesResourceKeys` property)
- `D:/SPT41/Development/_decompiled/EFT/ResourceKey.cs:15,18` (`path`, `rcid`)
- `D:/SPT41/Development/_decompiled/EFT/SceneResourceKey.cs:10` (`: ResourceKey`)

## Note for Arena-port relevance (not part of F3, flagging for later)
`ScenesPreset.ScenesResourceKeys` (capital S, the property actually consumed by
`LoadPresetAsync`'s `ScenesLoadCoroutine(preset.ScenesResourceKeys, ...)`) is `_scenesResourceKeys.Where(ShouldLoadScene).ToArray()`,
i.e. a *filtered* view, not the raw list (`ScenesPreset.cs:75`). The Harmony prefix runs before the
original method body executes, so it mutates the raw backing field first and the filter is applied
afterward against already-rewritten entries — order is safe, no bug here, just noting the two are
different members for anyone reusing this pattern against Arena's own preset/scene-key types.
