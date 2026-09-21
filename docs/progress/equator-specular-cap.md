# Equator shader-wide specular ceiling

## 2026-09-21 — implementation and offline checkpoint

The user's uncovered-atrium F10 targets (sandbags, table/bookcase, pallet, armored-car door and
mesh floor decals) still use bright original `p0/Reflective` materials. Roof coverage is not a
reliable selector for those objects. The previous host A/B established that this shader family's
deferred sky reflection is driven by `_SpecColor.rgb × _Specularness`; see
[donor-settings-camera-weather.md](donor-settings-camera-weather.md) §20.3–20.4.

### Behavior

New live F12 key: **Lighting (arena_equator) → ReflectiveSpecularCap**, range 0–1, default **0.30**.
Every other map, including Bay5, defaults to **1 (disabled)**. This is a new key; existing lighting
values and `DefaultsRevision` are not migrated or rewritten.

The repair scans the current map's own mesh renderers after raid start and shader rebinding.
It includes small props, mesh decals, and inactive LODs, regardless of cover or renderer name.
Player descendants, other maps' scenes, vanilla scenes, other shader families and incorrectly
typed shader properties are excluded. The weather donor can be Off; no roof photo is required.

The ceiling is applied to the strongest channel of the **effective specular product**. For example:

| Uncapped specular RGB | Specularness | Cap | Resulting RGB | Effective maximum |
|---|---:|---:|---|---:|
| (1, 0.5, 0.25) | 1 | 0.30 | (0.30, 0.15, 0.075) | 0.30 |
| (1, 0.5, 0.25) | 0.9 | 0.30 | (0.3333, 0.1667, 0.0833) | 0.30 |
| (0.3, 0.15, 0.075) | 0.27 | 0.30 | unchanged | 0.081 |

Only `_SpecColor` RGB is reduced, with hue and alpha preserved. `_Specularness`, diffuse color,
smoothness and `_ReflectColor` are not changed. Missing `_Specularness` means a direct specular
color (factor 1). Invalid/non-finite values are skipped. **1 disables the ceiling**, including for
authored values above 1; 0 removes the specular term and can make spec-only props appear black.

The existing `IndoorReflectionStrength` semantics stay unchanged: it already scales color and
strength individually, so its effective product includes both factors. The new ceiling works on
that result and does not multiply it again when it is already below 0.30. Resetting the new key to
1 therefore restores the previous indoor-tuned appearance, not the pre-indoor original look.

### Material ownership and live changes

`ReflectiveSpecularRepairs` owns an outer layer of renderer-local material copies. Shared material
assets are never written. It restores the underlying material slots before the indoor classifier
reads, rebuilds or updates its own copies, and before LightingLab changes ground smoothness. The
ceiling is then recomputed from those uncapped inputs. Nested changes are handled as one scope;
teardown cancels a scope rather than recreating material copies after the raid.

Changing 0.30 → 0.60 → 0.30 → 1 is original-relative and reversible. Unload restores the inputs
before destroying owned copies; an unrelated component's replacement slot is preserved. A scan
failure restores the partial changes and logs the failure. A later configuration change retries.

F9 includes a `SpecularCap` line with cap, readiness, matching slots, capped slots, mesh decals and
renderer count. F10 identifies changed materials with the `ArenaMapPort SpecularCap clone` suffix.

### Offline verification

Measured on 2026-09-21: **34 focused tests passed**, followed by the real client Release build with
**0 warnings and 0 errors**. The script printed `SPECULAR_CAP_CHECKS_PASSED`; leaf gate G1 recorded
the successful oracle. Test compilation additionally reports three nullability warnings in the
pre-existing scene gate compiled under the test project's stricter nullable context, and one existing
`BaseJsonTests` warning. These are not client build warnings.

`tools/tests/test_specular_cap.ps1` compiles the production `SpecularCapPolicy`, repair and scene
gate into the server test assembly with a small test-only Unity facade, then builds the real client
in Release with DLL-only staging. Cases cover effective product arithmetic; no second indoor
scaling; exact shader/property filtering; mesh decals; shared/vanilla/player/other-map isolation;
preserved alpha and non-specular properties; repeated live values; nested reclassification;
partial-failure rollback; unload and the next raid. The facade tests material identity and lifecycle,
not rendering, Unity's native destruction timing, batching, or the visual quality of 0.30.

Native acceptance still belongs to a restarted EFT process with the new DLL: compare an uncovered
prop/decal at cap 1, 0.30, then 1; verify the existing lounge appearance stays unchanged when already
below the ceiling, and test Bay5 with its default disabled ceiling. Append actual timestamps and
observations below after that run. No host visual proof is claimed by this implementation checkpoint.

## 2026-09-21 — reused donor lost its rain-camera callback between raids

During the root's A/B test of `5234d81`, the first Equator raid classified 607 indoor renderers and
1103 material copies at 06:52:49; cleanup restored 607 renderers at 06:53:47. A second ordinary
Equator raid in the same EFT process applied the new specular ceiling but never logged a new indoor
classification. At cap 0.30 it consequently capped 7914 slots rather than the first raid's 7022.
Those are root-observed symptoms; they are not a second-raid acceptance result for this follow-up.

Read-only trace against the installed decompile identified an existing lifecycle gap:

- `RainController.Awake` subscribes `RainController.CameraChanged` once (`RainController.cs:536–539`).
- That callback reaches `AbstractState.CameraChanged`, which calls `_depthPhotograper.Render()`
  (`RainController.cs:97–101`). The indoor repair schedules classification from that Render postfix.
- `CameraManager.Dispose` clears its `OnCameraChanged` delegate and singleton
  (`EFT.CameraControl/CameraManager.cs:1252–1256`).
- A harvested donor is reused via `SetActive`, so its RainController never calls Awake again.
  `WeatherRig.ReestablishSingletons` already reattached WeatherController's callback on later raids,
  but omitted RainController's callback. WeatherController's own callback only binds sky/scattering
  to the camera; it does not render the roof photo (`EFT.Weather/WeatherController.cs:123–140`).

The bounded fix adds `DonorCameraSubscriptions`, used only by WeatherRig's full donor. It reattaches
both callbacks, removes Awake's first-activation subscriptions before adding them, and retains the
actual subscribed CameraManager for teardown. Repeated establishment stays at one callback each.
Unload and failed activation detach from that captured manager even if the global singleton has
already changed. The simplified donor path and existing lighting settings are unchanged.

Six tests compile the production subscription owner against the installed manager's event/disposal
contract: first-activation duplicates, repeated binding, second raid after disposal, replacement
without disposal, idempotent teardown, and missing RainController. Native acceptance requires a new
client process followed by two raids without manually rendering the roof photo; the second raid
must classify indoor materials automatically. The F8 diagnostic rerender is only a workaround and
does not establish that the lifecycle fix succeeded.

Offline result for this follow-up: **6/6 subscription tests executed and passed** (TRX counters
checked, zero failures); real DLL-only Release client build **0 warnings / 0 errors**. Output was
staged only inside this repository's `dist/`; no deployment or native verification occurred in
this implementation pass.

## 2026-09-21 — first callback fix failed native second-raid acceptance

The root restarted into client `aed4e141` / DLL SHA prefix `DD2F` and repeated the test without F8.
The first raid rendered cover at 07:19:50 and classified 607 renderers / 1103 copies at 07:20:28.
After extraction and an intervening Hideout visit, the second raid armed RainCover at 07:25:31 but
did not render a photo or classify indoors; cap count returned to 7914 at 07:26:00. There were no
activation warnings. **The six event-owner tests did not establish correct production binding time.**

The remaining gap was the production call site: binding happened only during Scripts-scene loading
and only when `CameraManager.Exist` was already true. A reused donor does not Awake to create that
manager. A manager appearing later, or replaced after an intervening camera/Hideout lifecycle, can
therefore miss the early binding completely. Installed `CameraManager.SetCamera` initializes its
camera and then invokes its own event (`EFT.CameraControl/CameraManager.cs:386–408`); the manager's
presence during Scripts load is not a readiness contract.

### Explicit per-raid startup

`RaidCoverBootstrap` now subscribes to Scripts-scene load after indoor state creation. It waits for
this raid to be Started and for the current CameraManager to own a live camera, gives late scene
work a short margin, then performs one automatic post-start cover/classification pass. Unload
invalidates the captured raid object immediately, including when the next raid uses the same map.
Missing readiness and unsuccessful preparation have bounded retries and explicit failure logs.

The pass rebinds WeatherController and RainController to the actual current manager and invokes
their native camera callbacks. RainController's callback binds its camera/effects and renders its
serialized `_depthPhotograper`. Cover caches are reset before that render; classification uses the
new photo. The former `DepthPhotograper.Render` postfix scheduler was removed, so an early native
camera photo cannot schedule a competing classification. Original native event callbacks remain;
the new driver adds exactly one complete post-start pass.

For cover rendering and readback, the active donor's serialized photographer is now authoritative.
The global `DepthPhotograper.Instance` is assigned only in `Start` and unconditionally cleared in
`OnDestroy` (`DepthPhotograper.cs:49–52,184–188`); a different scene's photographer can therefore
overwrite or clear it while the reused donor still owns its valid component. No global singleton
is forcibly replaced by this fix.

The new integration harness compiles the production bootstrap, scene gate, and subscription owner.
It drives Scripts load with no manager, a manager with no camera, a not-yet-started game, camera
replacement during the readiness margin, a stale/null global photographer, unload cancellation,
two raids with an intervening Hideout manager, retry, and vanilla/donor-off exclusions. Unity frame
timing and indoor rendering still require native acceptance; passing these tests is not a claim
that the second raid has now succeeded on the host.

## 2026-09-21 06:52–07:04 CEST — native cap A/B and diagnostic confirmation

Initial client SHA-256 `BE7BA518BE71B0991BFFBF7BBB9B19B22C44CD6BEE1D63E6638EC42BB21BEA9E`,
EFT instance `8ffa1945`, SPT 4.1.6. UiBridge verified all 25 maschine plugins and Core against
the mirror, GPU disk and loaded process before testing. This run predates the donor lifecycle
follow-up above; the cap itself is unchanged by that follow-up.

At (-18.5, 0.1, 6.7), looking at the armored van's rear door, live cap 1 → 0.30 → 1 visibly
reduced and restored the bright sky sheen, including the nearby sandbag emplacement. F10
identified `Encashment_car_Crush_Closed/BackDoor_Left/BackDoor_Left_LOD0`: both original
materials had `_SpecColor=(1,1,1)` and `_Specularness=1`; capped clones had RGB=(0.3,0.3,0.3)
with strength still 1 and alpha still 1. Shared originals returned when disabled. The 1.00
log at 06:58:17, 0.30 at 06:58:39 and restored 1.00 at 06:59:00 establish the live sequence.
The bright diffuse white paint remains visible; this is a specular ceiling, not an exposure change.

A second view at (-6.5, 0.1, 6.2), facing the damaged floor at (-8.1, 0.031, 6.238), showed
the white floor sheen reduced at 0.30. F10's first collider-parent hit is the large main-floor
decal, so this view alone does not uniquely identify a particular small damage overlay.
The repair's native shader-variant counter reported 349 capped mesh-decal slots; ordinary
mesh overlays using a shader without `Decal` in its name are covered but absent from that counter.

In this same process, the donor reload gap described above left the second raid without a new
roof photo. F8 at 07:02:22 manually triggered it: automatic indoor classification then reported
607 renderers / 1103 clones at 07:02:27, and the outer cap returned from 7914 to 7022 slots.
This confirms the diagnosis, not the later fix. The F8 key also opens the installed ScopeRangefinder
editor; that editor was closed without changing its settings.

At the documented lounge point (1.34, 0.4, 7.19), the parquet stayed visually unchanged at cap
0.30 → 1 → 0.30. F10 at 07:02:49 and 07:03:21 showed the same indoor-only material names and
specular values (parquet RGB approximately (0.305,0.295,0.275), strength 0.2746). No outer clone
was needed for that already-damped material. The existing indoor strength remained at its user's
approximately 0.31 setting. Final ceiling was reset through F12 to its 0.30 default.

Live changes completed and the game stayed responsive; no frame-time benchmark is claimed.
The final 7022 material slots use 6221 renderer-local entries. Screenshots are saved, unchanged,
under ignored `analysis/screenshots/bay5-reflections/` (`van-cap1-image.png`,
`van-cap03-image.png`, `van-cap1-restored-image.png`, `decal-cap1-image.png`,
`decal-cap03-image.png`, `lounge-cap1-image.png`, `lounge-cap03-clean-image.png`). The full
native log is retained in ignored `D:/.unlazy/bay5-reflections/equator-cap-first-native.log`.

The first debug attempt was interrupted by a PMC death. The repeat used the native ordinary
raid AI=None setting; a native Redeploy respawn restored health, and the raid ended through
Equator North Gate (Survived, 00:07:11). On exit, the persisted profile had all 440 HP and empty
body-part effect dictionaries. Ordinary test results are excluded from later protected ArenaMode
profile comparisons. The AI setting must be restored before bot acceptance.

## 2026-09-21 — explicit bootstrap offline verification

`tools/tests/test_specular_cap.ps1` executed **49/49 passing tests**: 34 material-cap cases,
6 event-subscription cases, and 9 bootstrap cases. The latter separately check an absent game
instance and a live game still in `Starting`, alongside delayed/replaced cameras and raid/Hideout
lifecycle transitions. The TRX oracle confirmed total=executed=passed=49, failed=0; the client
DLL-only Release build completed with **0 warnings / 0 errors**. Test compilation retains four
pre-existing nullable warnings in `ArenaMapRaid.cs` and `BaseJsonTests.cs`; this change adds none.
The leaf G1 automatic re-verification passed with output SHA-256
`487390d6c565e8c75418dd7cc7cf2f7ac2ef965d44a121696d5ffedbfabed37a`.

For native acceptance, every ordinary Equator raid must emit the Scripts-scene scheduling log,
the current-camera/donor-owned-photographer refresh log, the depth rerender tagged
`[automatic raid bootstrap]`, indoor classification, and one
`automatic post-start cover/classification complete` log without F8. Repeat after extraction and
an intervening Hideout visit in the same EFT process. This offline pass only stages the DLL in
the repository's `dist/`; deployment and that second-raid acceptance remain the root task's work.

## 2026-09-21 — avoid duplicate native rain initialization during bootstrap

Independent review found that the native summer camera callback calls `RainScreenDrops.Init`
(`RainController.cs:163–168`), which creates a new drop placer and material
(`RainScreenDrops.cs:201–212`). The bootstrap must not repeat that initialization when the normal
camera event already completed it on the same camera.

`DonorCameraSubscriptions` now replaces only its donor's Awake rain callback with an owned wrapper.
That wrapper records the camera only after the native callback completes successfully. Binding the
same manager and delegates preserves that observation; a new manager, camera, or unload invalidates
it. A refresh still updates weather, but an already-initialized camera needs only its donor photo's
`Render` call. A failed callback remains retryable. Unrelated manager event subscribers remain intact.

The focused TRX oracle executed **55/55 passing tests** (34 cap, 11 subscription, 10 bootstrap cases),
including natural callback followed by bootstrap, repeated binding/preparation, replacement camera
or manager, and failed native initialization. The real DLL-only Release build completed with
**0 warnings / 0 errors** and no host deployment. Native two-raid acceptance remains pending.
