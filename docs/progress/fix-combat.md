# fix-combat — grenade/damage screen-effect NREs on arena_equator

Implements the two ACTIONABLE FINDINGS from the explosives-items audit (F1 `EyeBurn`, F2
`GrenadeFlashScreenEffect`) plus one additional finding of the same root-cause class, confirmed
directly against `D:/SPT41/Development/_decompiled/EffectsController.cs` while implementing F1/F2
(flagged by the skeptic pass as "possibly missed", independently verified here). All code changes
are in `ArenaMapPort.Client`; no Unity importer change was needed — the root cause is Cam2 (our
fallback camera, `LevelSettings.CameraPrefab == null`) predating several BSG screen-effect
components, not anything about our imported geometry.

## Modified files

- `ArenaMapPort.Client/EffectsControllerRepairs.cs` — 4 new guard classes (below), added after the
  existing `EffectsControllerFrostbitePrefix`.
- `ArenaMapPort.Client/HarmonyPatches.cs` — 3 new `InstallXxx` methods wired into `InstallAll`,
  following the file's existing `AccessTools.Method` + `Verify()` convention.

No new files, no importer (`UnityProject/Assets/ArenaMapPort/Editor/Import/*.cs`) changes, no git
commits, no Unity launch.

## Root cause (shared by all three fixes)

`EffectsController.Init()` (`EffectsController.cs:1273-1381`) fetches several screen-effect
components straight off `base.gameObject` with a bare `GetComponent<T>()` — unlike every sibling in
that method (DOF/CC_FastVignette/CC_DoubleVision/etc, all `?? AddComponentCopy(_effectsPrefab...)`),
four of them have **no fallback**: `_bloodOnScreen` (:1285, `BloodOnScreen`), `_blur` (:1288,
`FastBlur`), `_grenadeFlash` (:1326, `GrenadeFlashScreenEffect`), `_eyeBurn` (:1327, `EyeBurn`). Cam2
never carried these (they normally ship on the per-map camera prefab); on real maps they're always
present. Each is dereferenced unguarded later in the file and throws an NRE the first time it fires.

Unlike the already-fixed `FrostbiteEffect` gap (`EffectsControllerFrostbitePrefix`), a bare
`AddComponent<T>()` is **not** safe for any of these four — verified in their own decompiled sources:

- `EyeBurn.Burn()` indexes an empty `[SerializeField]` material array (`EyeBurn.cs:204`).
- `GrenadeFlashScreenEffect.Awake()` dereferences its own unassigned `PrismEffects` field immediately
  (`GrenadeFlashScreenEffect.cs:30`) — Unity calls `Awake`/`OnEnable` synchronously inside
  `AddComponent`, so this throws on add, before any guard could run.
- `BloodOnScreen.Start()` passes its unassigned `BloodShader` field straight into
  `new Material(Shader)` (`BloodOnScreen.cs:237`) — throws on a null shader.
- `FastBlur.Update()` evaluates its unassigned `_hitCurve`/`_deathCurve` `AnimationCurve` every frame
  once enabled (`FastBlur.cs:105-115`) — would NRE every frame, worse than the bug it replaces.

So the fix for all four is **guard-and-skip** (documented "no screen effect" degrade, same tradeoff
already accepted for TOD/PostProcessResources per `audit-reference-repairs.md`), never add-component.

## Fixes

### F1 — `EyeBurn` (blocker per audit: error-spam, every flash/stun grenade the local player sees)

**Finding → change**: `_eyeBurn` (private field) is dereferenced unguarded in two places —
`OnHealthBurnEyes` (`EffectsController.cs:1404-1408`, raised by
`ActiveHealthController.DoBurnEyes`/`FirearmControllerSharedMethods.ApplyLightAndSoundHealthEffects`
on every flash/stun grenade with LOS) and `OnClientPlayerEffectsPauseEvent`
(`EffectsController.cs:1256-1266`). Both are now guarded by a Harmony prefix that reads `_eyeBurn`
via `AccessTools.Field` (private field, no idiomatic direct access from our assembly) and returns
`false` (skip original) when it's null.

**Implementation**: `EyeBurnGuardPrefix` (targets `OnHealthBurnEyes`) + `EyeBurnPauseGuardPrefix`
(targets `OnClientPlayerEffectsPauseEvent`), sharing one cached `FieldInfo`. Logs once per session
(`[ArenaMapPort][RaidFix] EffectsController._eyeBurn is null on Cam2 — skipping eye-burn screen
overlay for flash/stun grenades this raid`).

**Why**: matches the audit's `correctedFix` exactly — both entry points needed guarding, not just one.

### F2 — `GrenadeFlashScreenEffect` (error-spam, any Stun-type grenade — same trigger as F1, often
the same explosion)

**Finding → change**: the nested `EffectsController.FlashAccumulator.AddEffect(IHealthEffect)`
(`EffectsController.cs:634-646`) dereferences `_screenEffect.Explode(...)` unguarded at line 644,
reached whenever a Stun-type health effect is added (`ActiveHealthController.DoStun`). `_screenEffect`
is **public** on `FlashAccumulator` (`EffectsController.cs:622`) — no reflection needed here, unlike
F1's private `_eyeBurn`.

**Implementation**: `FlashAccumulatorGuardPrefix`, a Harmony prefix directly typed on
`EffectsController.FlashAccumulator` (public nested class, referenced like `EffectsController`
already is elsewhere in this codebase), returns `false` when `__instance._screenEffect == null`.
Logs once (`[ArenaMapPort][RaidFix] EffectsController._grenadeFlash is null on Cam2 — skipping
white-flash screen overlay for stun grenades this raid`).

**Why**: matches the audit's proposed fix. This was independently corroborated by
`audit-reference-repairs.md`'s own deferred-item table ("add when grenade testing starts") — this
task is that milestone.

### Additional finding — `BloodOnScreen`/`FastBlur` (same root cause, confirmed higher frequency
than F1/F2 — every damaging hit, not just flash/stun grenades)

**Finding → change**: `_bloodOnScreen` and `_blur` share the exact same unguarded-`GetComponent`
pattern as `_eyeBurn`/`_grenadeFlash`, but are dereferenced on a much hotter path:
`OnHealthApplyDamage` (`EffectsController.cs:1410-1419`, every bleeding wound —
`player.HealthController.ApplyDamageEvent`) and `OnPlayerDamaged` (`EffectsController.cs:1421-1473`,
almost every non-Fall damaging hit ≥ 0.1 — `player.OnDamageReceived`, the single most frequently
invoked damage callback in this file). This was flagged by the skeptic pass as a same-class gap the
audit's own methodology should have caught; I re-verified it directly against the decompile while
implementing F1/F2 (field declarations :1164/:1170, assignments :1285/:1288, unguarded dereferences
:1417, :1460-1461, :1464/:1470) and confirmed the "bare AddComponent is unsafe" conclusion
independently for both types (`BloodOnScreen.cs:237`, `FastBlur.cs:105-115`, see Root cause above).

`OnHealthApplyDamage`'s only content is the `_bloodOnScreen.HitBleeding(...)` call — skip-whole-method
(prefix, same convention as F1/F2) loses nothing else. `OnPlayerDamaged` is different:
`ProceduralWeaponAnimation.ForceReact.AddForce(...)` (recoil kick, :1458) and `PlaySpecialSound`
(:1427-1429, glass/helmet ricochet audio) both run **before** the first unguarded dereference
(`_blur.enabled = true;`, :1460) — a prefix that skips the whole method would also silently drop that
recoil kick and special-hit audio, which have nothing to do with the missing screen-effect
components.

**Implementation**: `CombatScreenEffectsGuard` in `EffectsControllerRepairs.cs` —
- `Prefix` on `OnHealthApplyDamage`: skip (return `false`) when `_bloodOnScreen` (read via
  `AccessTools.Field`) is null.
- `Finalizer` (no prefix) on `OnPlayerDamaged`: lets the original run to completion up to the NRE, so
  the recoil kick and special-hit audio still fire, and only swallows the exception that follows —
  same technique and the same "swallowing is the riskier half, so gate hard on `IsLoaded()`"
  reasoning as the existing `SpatialAudioOcclusionGuard`/`SpatialAudioInteractiveOcclusionGuard`.

Both log once per session (`[ArenaMapPort][RaidFix] EffectsController._bloodOnScreen is null on
Cam2 …` / `EffectsController.OnPlayerDamaged threw … (fix-combat, EffectsController.cs:1288/1460-1470)`).

**Why not a prefix everywhere**: preserves BSG's own unrelated side effects (recoil, ricochet sound)
on every hit instead of trading a screen-effect NRE for silently missing recoil feedback too.

## Deferred — not fixed, with reasoning

- **`OnHealthDied` (`EffectsController.cs:1488-1497`)** also dereferences `_blur` unguarded (and
  possibly `_deathFade`, guarded only in `Init()`, not at its `OnHealthDied` use site). Not fixed:
  this event fires at most once per raid (player death), so per this task's own severity taxonomy
  ("error-spam" = *recurring* exceptions per shot/step/frame/event) it is "degraded", not
  "error-spam" — and raid gameplay is already over at that point regardless. Left as a known gap for
  a future pass if death-screen fidelity becomes in scope.
- **`ZombieInfection_Accumulator._cameraEffect` (`InfectionEffect`, `EffectsController.cs:1031-1130`)**
  — skeptic-flagged, same unguarded-`GetComponent` pattern (:1377), but only reachable via
  `TryAddZombieInfection` (`ActiveHealthController.cs:3469-3475`), which requires a zombie-type bot
  dealing melee damage. arena_equator currently ships no bots at all (project brief) — unreachable
  this milestone. Will resurface once bots (specifically zombie-type ones) are added; flagging for
  that milestone rather than fixing speculatively now.
- **Ballistics: `LowPolyCollider`-layer geometry excluded from `BallisticsCalculatorConstants.HitMask`**
  (skeptic note, ballistics) — confirmed real (~1,700+ active colliders on layer 18 across our
  geometry scenes never generate a `RaycastHit`), but explicitly rated "degraded" (silent miss, no
  exception, no crash), not blocker/error-spam. Out of scope for this task's fix criteria; needs a
  separate mesh/position-overlap check against the `HighPolyCollider` population before deciding
  between re-layering and duplicate proxy colliders — not attempted here.
- **Ballistics: bare `BaseBallistic` component hazard** (skeptic note, movement-interaction) — this
  was a warning against a *different* audit's proposed fix (adding `AddComponent<BaseBallistic>()` to
  our colliders for footstep-sound material info), not a request to implement anything here. No
  ballistic component was added to any collider in this task — confirmed by re-reading
  `UnityProject/Assets/ArenaMapPort/Editor/Import/SceneAssembler.cs`, unchanged. Noting only so this
  hazard stays on record if that other finding is ever implemented: use `BallisticCollider` (whose
  `Get()` returns `this`), never bare `BaseBallistic` (whose `Get()` returns `null`, which
  `Shot.HandleCollision`/`IsTargetPenetrated`/etc. then dereference unguarded).

## Build

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release
```

Result: **Build succeeded, 0 Warnung(en), 0 Fehler** (0 warnings, 0 errors).

## Changed dist files

- `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` — rebuilt (new guard classes +
  patches).
- `dist/BepInEx/plugins/ArenaMapPort/bundles/*` and `material-shaders.json` — restaged by the build's
  existing post-build copy step (unchanged content; no Unity asset or importer touched this task).

## Not fixed / out of scope

Everything under "Deferred" above. Nothing else from the ACTIONABLE FINDINGS or SKEPTIC NOTES lists
was left unaddressed among items meeting the blocker/error-spam bar.
