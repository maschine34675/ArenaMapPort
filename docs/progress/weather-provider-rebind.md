# Rebind the cached weather rig's sky provider — 2026-10-17

## Observed failure and boundary

GPU2 process `d101ff36c8db402793323eadc40a3801` tested AirPit Team DM,
Customs Yard DM, Labyrinth DM, then Equator DM with ArenaMode candidate `da437d3`.
The first three maps had no matching weather/shadow/ambient errors in the captured
log windows. Equator placed all six pickups, but emitted repeated native errors.

The complete host log `LogOutput-SPT-GPU-2-2026-10-17_22-35-20.log` establishes
this order (local clock; line numbers from the reviewed file):

| Time | Line | Event |
|---|---:|---|
| 22:35:37.886 | 15610 | Reserve full weather rig harvested once. |
| 22:37:27.438 | 15765 | That rig activated for AirPit. |
| 22:47:14.263 | 17657 | Same cached rig activated for Equator after the vanilla maps. |
| 22:47:14.330 | 17668 | First `WeatherController.ControlFogginess` exception. |
| 22:47:21.918 | 18653 | First `DistantShadow.SetupComputeShadowMaskCommandBuffer` exception. |
| 22:47:22.092 | 18672 | First `AmbientHighlight.UpdateAmbientBuffer` exception. |
| 22:47:26.163 | 26321 | Arena session becomes active on Equator. |
| 22:47:26.368 | 26400 | Arena creates its separate phase-feedback light. |

All three errors began before the new Equator phase-feedback light existed. In
particular, the weather failure preceded its creation by 12.038 seconds. The
chronology excludes that newly created light as their initial trigger; it is not
a claim that every possible lighting interaction was tested.

## Native/source mechanism and fix

Native `TODSkyProvider` stores a separate static `Instance`. Its `IsAvailable`
checks the `TOD_Sky`/`TODSkySimple` singleton registrations, not that reference.
`WeatherController.ControlFogginess` dereferences `Instance.Atmosphere`;
`AmbientHighlight` and `DistantShadow` both dereference `Instance.LightObject`.

The cached rig's first harvest/native `Awake` registers the provider. Its later
activation previously restored the singleton and camera subscriptions only.
An intervening vanilla map can replace the provider, and deactivation/destruction
does not make a reused rig run `Awake` again. That leaves the independently stored
provider inconsistent with the restored singleton. A Simple sky additionally has
no default Atmosphere object. The exact stale object's identity was not read
back during the failed raid, so that last runtime identity detail remains an
inference from the source and sequence, not an observed inspector result.

`WeatherRig.ReestablishSingletons` now calls the native
`TODSkyProvider.RegisterInstance` with the exact owned `rig.Sky` or
`rig.SimpleSky` immediately after restoring its corresponding singleton. Existing
activation gates, camera subscriptions, dome transform, weather controller,
unload behavior and all vanilla initialization paths are unchanged. There is no
global Harmony patch, scene scan, cloned replacement sky, config change or
exception suppression.

## Offline verification and build boundary

`python tools/tests/test_weather_provider_rebind.py` compiles and executes the
actual production reactivation method in an isolated SDK-only project. The
baseline passes 55 assertions covering Full and Simple rigs, foreign/null/current
providers, repeated activation, missing/current/replacement cameras, native
singleton identity, existing weather/rain callbacks and dome placement. Two
compiled controls omit either provider assignment; each fails the intended
behavioral assertion. The fixture does not load Unity or certify rendering.

Implementation is isolated in `D:\ArenaMapPort-weather-rebind`, branch
`fix/weather-provider-rebind`, based on `275b668`. Release DllOnly build and its
normal auxiliary-asset staging passed with zero warnings/errors. The fresh
worktree initially lacked the required selection-map inputs; the staging targets
also reassigned the requested input-path overrides. The fix did not change or
bypass those checks. Instead, 28 already accepted input files (17,751,784 bytes)
were copied into this worktree's ignored `work` directory and verified against
their original SHA-256 hashes before the successful build. No assets were edited.

The pre-commit isolated candidate is
`dist/BepInEx/plugins/maschine-ArenaMapPort/maschine-ArenaMapPort.Client.dll`
(815,104 bytes), SHA-256
`53da5b767d282e1de7090af7f145f7bd14f5643d7478ffcc1b4e91caf97a38c7`.
Root will rebuild after committing for exact candidate attribution; the hash
above identifies this implementing agent's pre-commit build only.
The main checkout's source/client dist and the live client DLL were hash-checked
unchanged around the build. This worktree staged no bundles or server output.
Only the candidate DLL is intended for the root agent's client-only deployment;
the copied auxiliary assets are not a deployment change.

No live deployment, server/bundle rebuild, published ZIP/tag update or commit was
performed by the implementing agent. Root and an independent map-lighting agent
reviewed the production change without finding a defect. Root owns delivery and
native acceptance.

## Native acceptance still required

1. With the same ArenaMode candidate and phase lighting enabled, start a fresh
   process and enter Equator directly; inspect the provider against the active
   owned rig and confirm the three exception signatures remain absent.
2. With the companion fix loaded, repeat ported map → vanilla map (including
   Labyrinth) → Equator in one process. Verify provider identity after reactivation,
   weather/light rendering and absence of all three signatures through combat
   and a local respawn.
3. Leave normally and check that the next vanilla raid uses its own native sky.

These are pending acceptance steps, not reported GPU passes. Existing native
evidence and unrelated map assets remain unchanged.

## Root deployment and native follow-up — 2026-10-17

The preceding pending section describes the implementing agent's handoff. Root
committed the correction as `6c6a38c`, independently reran the production-method
regression and isolated Release build, then deployed exactly one client DLL with
`tools/deploy-to-spt.ps1 -SptRoot D:\SPT41 -DllOnly -DllTarget Client -Confirm`.
The post-commit candidate is 815,104 bytes, SHA-256
`e4e917381cc524ad224947cf0ef7e530021d8a88899fdc0b0141ee9d75bbaf0b`.
The earlier pre-commit hash is superseded. Local and GPU2 files match; after
restart, UiBridge reported all 19 modules identical across build, host disk and
loaded process. No published ZIP, tag, server, bundle or main `dist` was changed.
The deployment's 2,239 protected non-client files, including the server DLL,
total 12,009,189,770 bytes and all retain their pre-deploy SHA-256 hashes.

GPU2 process `dc8d8d22033641dbb4f0ac3147a7c862` (started 23:09:28) ran:

1. Fresh Equator DM: all six pickups placed, DoubleDamage collected after an
   8 m native navigation walk, local death/respawn at 23:12:06 completed in
   0.23 s. No Error-level entries followed the respawn.
2. Labyrinth DM then Equator DM in the same process: both placed six pickups,
   created their owned phase-light object, rendered a live scene and left
   normally. The second Equator was a brief startup/reuse check; the local
   respawn above belongs to the first Equator, not this repeat.
3. Customs Yard DM loaded its native map and placed six pickups after the
   ported-map sequence. This is a native-map control within Arena, not a
   separate ordinary PMC gameplay test. A full file-backed BepInEx Error query
   after Customs covered 17,442 lines, including loading, and returned zero matches.
4. Hideout opened successfully, including its rendered first-person view.

Root independently reviewed the scenario results and Equator/Hideout captures.
There was no recurrence of the three original weather/shadow/ambient exception
signatures in these samples. The provider's static object identity was not
directly read back; the mechanism remains supported by native source and the
production-method regression, while the map-transition behavior is now tested
in the game. This is not a certification of every weather preset or transition.

Final BepInEx inspection covered 17,512 lines with zero Error matches. The
separate native EFT stream still contained 68 Error-level entries, including
JetMaterial, wind/WeatherObstacle, seasonal managers and spawn-marker
reconciliation. None was one of the three repaired exception signatures. Those
entries were preserved, not suppressed or exhaustively resolved by this fix;
the result must not be described as all game logs being error-free.

Detailed scenarios, screenshots, process/configuration cleanup and limitations
are recorded in ArenaMode's `docs/GPU-MAP-FEATURES-20261017.md` and its ignored
`artifacts/map-features-native/` evidence directory. The protected-file manifests
and build/gate evidence are retained by the integrating root session.
