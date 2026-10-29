# Mixed native release sweep — 29 October 2026

The user requested representative pre-release checks of ArenaMode, Redeploy and
ArenaMapPort using both GPU hosts concurrently. GPU-2 has a verified real Arena
installation; GPU-1 exercised the no-Arena availability path alongside Arena
tests on Factory. No installation/encryption checks were bypassed.

## Changes and verification

Spectator `GetOutputData` readback is now behind the existing startup-latched
DeveloperMode. Scheduling, source/room ownership, original clips and the two-voice
limit remain unchanged. With developer mode off, confirmed playback counts
increased while `LastOutputPeak` stayed zero; that zero is an absent diagnostic,
not proof of silence or subjective audio quality.

A Bowl end exposed a separate lifetime gap at **12:02:29.068**, after the Scripts
scene and bundles were released: a late `SmallPhysicsObject` collision requested
the unloaded native `weaponsounds.bundle`. The existing sound guard had already
returned to vanilla because `ArenaMapRaid.IsLoaded()` was false. It now identifies
remaining objects by their owned scene name and skips only this late sound
callback, retaining the native 0.5-second collision timer. It does not require
`scene.isLoaded` or a missing cached bank; neither guarantees audio lifetime.
Foreign/invalid scenes retain vanilla behavior. Live missing banks still warn.

The actual production prefix and ownership policy are linked into **21 new
behavioral cases**: all four maps with loaded/unloaded remaining geometry and
cached/missing banks, foreign/invalid scenes, normal live bank setup and the real
missing-bank warning. Full Release suite: **2,545 passed, no skips/failures**.
Release client build: zero warnings/errors. Independent review found no concrete
regression. The original exception lacks an instance/scene handle, so source and
timing establish the gap; the native rerun is a bounded acceptance check.

## GPU-2 coverage

| Map | Exercise | Result |
|---|---|---|
| Equator day/night | TDM, gates, walking, bot combat, lighting, 120-second ends | Native results/menu and cleanup passed. First day run retained developer mode and is explicitly diagnostic evidence. Night baseline was verified off. |
| AirPit | Original A/B Domination, long route to A, autonomous B capture | 120-second end, visible/animated crowd and complete cleanup passed. |
| Bowl | Original A/B Domination, route/captures | 120-second end passed; late sound exception found and corrected above. |
| Bay5 | Deathmatch, movement and five fighting bots | 120-second end and cleanup passed. |
| Bowl, final R2 | 90-second Domination and 64.5 m route, developer mode off | Native menu, zero errors/fatals or repeated soundbundle exception; all spectator resources released. |

Crowd checks saw 99 actors on Equator/AirPit/Bowl and 88 on Bay5, 39 shared meshes
and 33 materials, animation transitions, confirmed native spatial voices and a
maximum of two concurrent voices. Managers, clips, emitters and materials were
absent after scene exit. These observations do not establish hearing quality.

Final client: SHA-256
`D847AB2F3B1914A5598F497CD1CD8A960E5616387A1053AE52FE688C4FB6090D`,
loaded MVID `c64f36e8-3853-49f2-9ce4-2a4d94e8c7c1`. Installed using the standard
client-only dry/confirmed deployment on the local installation and both hosts.
The earlier `F3AECA69…` candidate is retained as before-fix evidence.

## Remaining limits

Two Equator shoe props explicitly carry schema-2 Books sound data in the accepted
source package. SPT has no matching impact bank; the sound-only fallback remains
visible. Existing Unity missing-script/LOD/attachment warnings and optional
Fika/Harmony startup warnings were preserved. The final Bowl run had seven such
startup warnings and no Error/Fatal. No global logging filter was changed.

The previously reported intermittent noise remains unresolved. This change does
not alter playback order, clips, filters or gain to guess at its cause. The old
October 28 ZIP is still outdated; native smoke checks do not publish a release.
Evidence is local in the ArenaMode checkout under
`artifacts/release-sweep-20261029/`, including raw logs, captures, loaded identities,
test results, deployment receipts and independent reviews.

## Final developer comparison

The subsequent R2 Equator run with DeveloperMode=true reached 29 confirmed calls,
peak output 0.002342 and two concurrent voices, then returned through native end
and released its resources. R2 Bowl with DeveloperMode=false reached 46 confirmed
calls and peak 0. This verifies the diagnostic boundary in both directions; it
does not establish that the intermittent reported noise is absent.

## Restoration

GPU-2 restored 250 original configuration files, including its original
DeveloperMode setting. The original profiles retained their inventories; a
separate disposable profile and only its test-owned artifacts were archived and
removed. GPU-1's 220 original configs were restored separately. Both hosts were
stopped before the original synchronization state was restored. The mixed sweep
report in ArenaMode records the exact native timestamp/mail-expiry differences
and historical backup retention; no active profile was rolled back.

## Synchronization follow-up

Catch-up produced eight new cache/log/profile conflict copies across the test
installations. They were hash-archived and removed from live paths. The profile
variants differed only in the documented native time fields. Current ordinary
profiles, all 470 original host configs and candidate binaries were independently
checked after propagation; inventories remain unchanged. Historical conflicts
and archived evidence were preserved.
