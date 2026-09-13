# Adversarial verification: ref-interchange/F7 (collider layer / physic-material carry-through)

## Finding under test
> Inference: because the rip is same-codebase (same EFT Assembly-CSharp/layer table), collider
> layer indices and ballistic/physic-material data likely carry through the AssetBundle unmodified
> without needing runtime remap -- no explicit repair module addresses this.
>
> Evidence offered: Absence of any layer-fix module (F6) combined with BlockerRepair.cs:11-13
> showing the map-boundary BLOCKER colliders are handled by disabling vanilla-scene colliders
> rather than fixing ripped ones, implying ripped colliders are otherwise trusted as-is.

## Verdict: partially confirmed, but the offered evidence does not support it -- the real support comes from elsewhere

The underlying *conclusion* (layer indices and physic materials survive the rip and need no runtime
remap) turns out to be strongly supported by direct measurement. But the *evidence cited* for it is
a mischaracterization of the source file, and "absence of a fix module" is weak circumstantial
reasoning on its own. I replaced it with a direct check of the actual data.

## 1. The cited evidence does not say what F7 claims

Read `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client/BlockerRepair.cs:11-13`:

```
// Replaces SPT's map boundary with retail's from plugin-data/blockers.json. The BLOCKER colliders
// live in a scene that stays native, so they are disabled rather than destroyed.
internal static class BlockerRepair
```

Cross-checked against `InterchangeRework.Shared/SceneNames.cs:36-39`: of the 18 `Shopping_Mall_*`
scenes, only `Shopping_Mall_Scripts` is listed in `NativeSceneNames` (kept as SPT's own, non-ripped
copy); the other 17 are swapped for the `_IR` (ripped/retail) versions. The BLOCKER colliders
BlockerRepair.cs disables live in that one *native, never-ripped* scene.

Reading further (`BlockerRepair.cs:97-111`), the replacement geometry is not sourced from the
AssetBundle at all: it is a brand-new `GameObject`/`BoxCollider` (`AddComponent<BoxCollider>()`)
positioned from `Position`/`Size` floats read out of a hand-extracted `blockers.json` sidecar. Its
layer is never set, so it silently sits on layer 0 (`Default`).

So this file says nothing about ripped-collider fidelity in either direction: it toggles off
colliders that were never ripped, and adds colliders that were never ripped either. Citing it as
"implying ripped colliders are otherwise trusted as-is" is a non sequitur -- the file simply isn't
about that population of colliders. F7's own evidence, read carefully, is closer to counter-evidence
(the one place the mod needed rock-solid collision fidelity for something gameplay-critical, it
avoided the ripped collider population entirely rather than "trusting it as-is").

"Absence of a layer-fix module" (F6) checks out empirically -- `grep -rniE
"\.layer\b|SetLayer|LayerMask|PhysicMaterial|physMat"` across all 89 `.cs` files in
`SPT-InterchangeRework-main` (Client+Server+Shared+Tests) returns zero hits, and the same grep
returns zero hits in `ManimalLabs-main`, `ManimalLighthouse-master`, and `Tarkov-1.0-Backport-main`
too. But an absence of code is weak evidence by itself (could mean "not needed" or "not yet
discovered as needed"); `ManimalIcebreaker-main` is the one exception and is instructive about which
reading is right (see ยง3).

## 2. Direct check: do Arena and SPT 4.1's EFT client actually share one layer table?

The finding's real crux -- "same codebase ... same layer table" -- was asserted, not checked, in the
project context. I checked it directly by reading each client's `TagManager` and `PhysicsManager`
out of `globalgamemanagers` with UnityPy (read-only, no writes to either client tree):

- `D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers` (SPT 4.1, EFT 0.16.9.5-40743)
- `X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/globalgamemanagers` (Arena 0.5.4.0-46834)

**`TagManager.layers[0..31]`: byte-for-byte identical in both**, all 32 slots including the three
blanks at 3/6/7:

```
0 Default, 1 TransparentFX, 2 Ignore Raycast, 3 "", 4 Water, 5 UI, 6 "", 7 "",
8 Player, 9 DoorLowPolyCollider, 10 PlayerCollisionTest, 11 Terrain, 12 HighPolyCollider,
13 Triggers, 14 DisablerCullingObject, 15 Loot, 16 HitCollider, 17 PlayerRenderers,
18 LowPolyCollider, 19 Weapon Preview, 20 Shells, 21 CullingMask, 22 Interactive,
23 Deadbody, 24 RainDrops, 25 Menu Environment, 26 Foliage, 27 PlayerSpiritAura,
28 Sky, 29 LevelBorder, 30 TransparentCollider, 31 Grass
```

**`PhysicsManager.m_LayerCollisionMatrix` (32 x uint32 bitmasks): also byte-for-byte identical**
between the two clients (verified full 32-value array, e.g. index0=1073781193 ... index31=200,
matching in both dumps). This is the actual collision-behavior table (which layer collides with
which) -- a separate Project Setting from TagManager that F7 didn't even consider, and it matches
too.

Minor, unrelated divergence for completeness: `TagManager.tags[]` differs slightly (Arena has
`AdditionalCamera`, drops `PortalsCollection`/`NotEmpty` vs EFT's list) and `Physics2DSettings` has
version-appropriate extra fields on Arena's side (`m_ContactThreshold`, `m_SimulationLayers` --
consistent with Arena's newer Unity 6000.3.6f1 vs SPT41's 2022.3.43f1). Neither touches 3D
`m_Layer`/`m_LayerCollisionMatrix`, so neither undermines the confirmation above; it does show the
two Project Settings are not a byte-identical fork, so "same codebase" should be read as "same layer
table," not "identical project in every setting."

This is much stronger, on-point support for F7's "same layer table" premise than anything the
original citation offered.

## 3. Direct check: does the actual rip pipeline preserve `m_Layer` and `m_Material` correctly?

Matching layer *tables* only proves index N means the same thing in both projects; it says nothing
about whether the AssetRipper-based rip+repack pipeline actually writes sane `m_Layer`/`m_Material`
values onto the ripped GameObjects in the first place. No Arena rip exists yet in this environment
(the spike's Unity project is only just being created), so I checked the one already-shipped,
same-tooling-family rip available: `SPT-InterchangeRework-1.0.0-SPT4.1`'s
`interchange_rework.bundle` (3.19 GB, retail EFT -> SPT EFT, same AssetRipper+UnityPy-class pipeline
per project memory).

Sampled the first 4001 `GameObject`s' `m_Layer` field via `read_typetree()`:

```
layer histogram: {0: 2863, 18: 462, 12: 553, 8: 96, 9: 2, 22: 5, 11: 1, 29: 1, 30: 17}
```

and the non-default values are semantically correct, not garbage: objects named
`WoodSlice_Collider`/`collider`/`colider` sit on layer 18 (`LowPolyCollider`); objects named
`Wood`/`wood`/`phone_ballistic`/`Cardboard_MedPen` sit on layer 12 (`HighPolyCollider`); character
rig bones (`Base HumanLDigit11` etc.) sit on layer 8 (`Player`). These are exactly the assignments a
correctly-authored EFT scene would have -- names and layers line up, across hundreds of objects.

Then sampled `m_Material` (the PhysicMaterial PPtr) on colliders directly: 13 `PhysicMaterial`
assets exist in the bundle with real names (`wood`, `concrete`, `loot`, `Normal`, ...), and scanning
40,001 `MeshCollider`/`BoxCollider`/`CapsuleCollider`/`SphereCollider`/`WheelCollider` objects found
73 with a non-null, resolvable `m_Material` PPtr (`m_FileID:2, m_PathID:698-700` etc., pointing at
those same 13 assets). A low hit rate is expected and correct here -- most Unity colliders in any
authored scene don't carry a custom physic material and fall back to the engine default, so this is
consistent with normal authoring, not with the reference being silently dropped.

Both fields therefore demonstrably survive an actual same-tooling-family rip+repack, not just in
theory.

## Sharpened conclusion

- F7's headline claim -- layer indices and physic-material references carry through unmodified and
  need no runtime remap -- holds up, and holds up far more solidly than the finding's own citation
  suggested. Direct measurement (not inference) confirms: (a) Arena and SPT 4.1's EFT client share
  an identical 32-slot layer table *and* an identical layer-collision matrix, and (b) a real,
  already-shipped rip of this tooling family preserves both `m_Layer` and `m_Material` on individual
  GameObjects/Colliders with semantically correct, non-garbage values.
- The evidence F7 actually cited is not evidence for this: `BlockerRepair.cs:11-13` describes a
  population of colliders (native SPT scene + freshly-synthesized JSON-driven boxes) that were never
  part of the ripped bundle in the first place, so it cannot "imply ripped colliders are otherwise
  trusted as-is." That inferential step should be struck from the finding.
- "No layer-fix module exists" (F6) is real (checked across all 89 Interchange `.cs` files plus
  three other reference-mod repos, zero hits), but note `ManimalIcebreaker-main` is the one
  counter-example: it *does* touch `.layer` in three places --
  `icebreaker-client/Keypad/IcebreakerPasscodes.cs:163-178` (explicit `Interactive`-layer assignment,
  but on freshly grafted keypad-panel objects, not ripped ones), `IcebreakerHeliExfil.cs:288`
  (`go.layer = src.gameObject.layer` -- copies a *ripped* collider's layer onto a new object,
  trusting it as correct source-of-truth rather than remapping it), and
  `IcebreakerLootDiag.cs:104-125` (a `DevMode`-only one-raid diagnostic, built in response to an
  "unlootable containers" bug report, that logs whether rebaked `LootableContainer`s sit on the
  `Interactive` layer -- audits, does not remap). None of these is a layer *remap*; all three are
  consistent with "the ripped layer value is correct and usable," reinforcing rather than undermining
  F7's conclusion. It's also worth noting this diagnostic tool's existence shows layer correctness for
  *interaction* purposes was a real enough worry on a prior port to warrant dedicated tooling --
  something F7's "no one has looked at this" framing understates.
- Residual, unaddressed gap: my pipeline-fidelity check (ยง3) used a retail-EFT rip (Mono source),
  not an Arena rip (IL2CPP + encrypted global-metadata). Serialized asset data (`m_Layer`,
  `m_Material` PPtrs) lives in the engine asset format and is independent of the scripting backend,
  so there's good reason to expect the same fidelity from an Arena rip -- but that specific
  extrapolation is not yet empirically checked in this environment, since no Arena rip exists yet.

## Files/data read (all read-only)
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client/BlockerRepair.cs` (full file)
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Shared/SceneNames.cs:1-80`
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client/InteractiveFieldRestore.cs:1-60`
- grep of `.layer|LayerMask|SetLayer|PhysicMaterial|physMat` across `SPT-InterchangeRework-main`, `ManimalIcebreaker-main`, `ManimalLabs-main`, `ManimalLighthouse-master`, `Tarkov-1.0-Backport-main` (all `.cs`)
- `ManimalIcebreaker-main/icebreaker-client/Keypad/IcebreakerPasscodes.cs:120-186`
- `ManimalIcebreaker-main/icebreaker-client/IcebreakerHeliExfil.cs:270-300`
- `ManimalIcebreaker-main/icebreaker-client/IcebreakerLootDiag.cs:1-135`
- `ManimalIcebreaker-main/docs/MAP-BACKPORT-PLAYBOOK.md` (grep for layer/physic)
- `D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers` -- `TagManager.layers[]`, `PhysicsManager.m_LayerCollisionMatrix` (UnityPy `read_typetree()`)
- `X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/globalgamemanagers` -- same two objects
- `D:/sptmaps/SPT-InterchangeRework-1.0.0-SPT4.1/BepInEx/plugins/LennoxP90-InterchangeRework/interchange_rework.bundle` -- sampled `GameObject.m_Layer` (first 4001 objects) and `Collider.m_Material` (first 40001 collider-type objects), plus enumerated all 13 `PhysicMaterial` assets

No files were written outside `D:/ArenaMapPort/analysis/`; the two client `globalgamemanagers` files
and the InterchangeRework bundle were opened read-only via UnityPy, nothing was modified or copied.
