# Equator original materials and environmental audio

## Scope and source contract — 2026-09-22

The user selected Equator materials and environmental audio while the user's Bay5 hearing/visual
test remains pending. This extends the native feature path introduced for Bay5. Glass, crowd,
announcer/match events, full room/portal acoustics and gameplay changes are outside this task.
Source assets remain local and ignored; this work does not establish redistribution rights.

Starting revision: `2862da7`, branch `feature/equator-native-materials-audio`. Source is the user's
Arena installation; target is SPT 4.1.6 / EFT 0.16.9.5-40743 / Unity 2022.3.43f1. The accepted
`work/extract/equator` package remains untouched. Candidate extraction is separate at
`work/extract/equator-native-features`.

### Original material data

The existing source decoder and target importer accept all **2,892** original BallisticColliders:

| Original scene | Components |
|---|---:|
| Main / level51 | 2,639 |
| DesignStuff / level53 | 25 |
| Lighting / level55 | 127 |
| TeamFight_DesignStuff / level57 | 89 |
| TeamFight / level58 | 12 |

All six original float32 values, original owners, ancestor states and existing physics are retained.
Canonical names map source presets to the independently read installed target table. **40** original
named-preset instance overrides remain overrides. Static target HitType/NetId and native surface
association use the existing reviewed adaptation; no WindowBreaker is authored.

There are 2,374 owners active in hierarchy with their own enabled non-trigger physics. This is an
offline availability count, not universal live-ray coverage. Existing fallback ballistic twins
remain for source objects without native ballistic metadata.

### Original environmental graph

Original BuildSettings resolves Equator Sound to **level56**. The selected graph consists of
**25 AmbientSoundPlayers, one group, 11 SoundBanks and 65 PCM16 clips** (50,554,644 decoded WAV bytes).
The group's 23 ordered members are exactly the originally active sources. The two ungrouped dog
sources, `far_dogs1` and `far_dogs2`, retain their inactive GameObjects; their banks/clips are retained.
The active graph uses nine banks / 56 clips.

These are localized water drops, creaks, metal noises, electricity, indoor wind and distant
miscellaneous sounds. Unlike Bay5, this graph has no seasonal players or shoreline calculator.
Original player gains, pitch/timing ranges, curves, source state, bank links and PCM are preserved.
The electricity player retains its original looping AudioSource. Original sample rates vary,
including unusual rates such as 8,137 Hz; the importer must preserve them rather than resample.

Equator's audio schema 2 adds each hierarchy node's `activeSelf` and layer. Bay5 retains its existing
schema 1 and default exporter behavior. Each map gets its own owned root and diagnostic setting.
Equator uses exact native `CommonAmbInEffects` and the interior `AmbientInVolume` bus; Bay5 keeps
its exterior routes. The driver temporarily owns the neutral parent gain and conditionally restores
the captured prior value when it still owns that setting.

Native players are wired under an inactive root and started only after the real raid/listener/mixers
exist. Only the 23 active grouped players initialize. Inactive dogs have no Fader or generated
EmptyClip and must never receive native Stop; teardown and diagnostics account for that distinction.
Room-dependent filtering and environment transitions are not recreated.

Six `FansAmbient` records belong to inactive cheering/neutral/angry crowd infrastructure. Four
other sources are Arena respawn-gate events. The original day/night bed sources are both muted
and rely on the full environment singleton. These systems are not activated or substituted.

### Preservation and cubemap correction

Fresh source inspection matched all 39,398 existing scene objects, all asset identity sets and
all 332 material JSON bytes against the accepted package. Final candidate asset-byte comparison
and actual rebuilt bundle acceptance are recorded below when completed.

Of 18 original cubemaps, two were previously sampled as sRGB despite original Linear flags. The
already-implemented importer fix corrects these on rebuilding Equator:

| Texture | Material users |
|---|---|
| `492aa7b57d46f0b6` / lab_cubemap_blurred | elevator, black marble |
| `b093fe961644b515` / Cubemap | white marble, grey plaster, plush carpet, rough bricks |

Their material definitions and the user's reflection controls are preserved. Source color decoding
does not imply universally weaker reflections. Baseline NavMesh: 10,404 vertices / 4,572 triangles,
SHA-256 `0c32649698dcf85633818c5a9077f579816a25c3e1db0ed17318d44848f17e1a`.

The baseline native process `a573411d` loaded the previous build with VM/host/loaded identity agreement.
The central view was captured at feet (0,0.2,0), yaw 0, pitch 8 in
`analysis/screenshots/equator-native-atrium-before.png`. A second attempted view was obstructed and
is not a useful visual acceptance reference. One ordinary bot hit during this baseline debug raid
caused combat damage; the debug exit bypassed the disconnect penalty, and the menu showed normal
health recovery. This is not a byte-identical profile snapshot claim.

### Build and validation contract

`extract_equator.py --native-features` explicitly opts in; only all five complete scenes are
accepted, with no slices. Legacy extraction without the flag remains unchanged. Reproducibility
forwards both feature selection and the selected target installation. The manifest requires the
complete ballistic/audio contract; partial, mismatched-map or sliced native packages fail closed.

Equator retains its legacy scene order and builds **without `-FullMap`**, into `work/build/full`.
The sanctioned build still performs the mandatory MonoScript identity patch and strict validators.
Independent validation reads original payloads and PCM directly, checks built hierarchy/fields/
links/identity, and retains the original Bay5 source/bundle checks. Build, deployment and native
acceptance remain distinct stages. Audio technical evidence does not substitute for a hearing test.

Local source and verification evidence is under `D:/.unlazy/equator-native-features/` and
`work/equator-native-features/`. Final outcomes follow as append-only entries.

## 2026-09-22 — extraction and integration checks

The full opt-in extraction completed at 09:30 CEST with zero errors (219 existing extraction
warnings). Independent original-source comparison accepted the complete material/audio package.
Comparing with the accepted legacy package found all five scenes / 39,398 original objects
unchanged after removing only the newly added ballistic records; all 3,645 original mesh,
material and texture files are byte-identical. The added component census is 2,639 / 25 / 127 /
89 / 12 in the original scene order, totaling 2,892.

The complete Python suite passed 180 tests; .NET passed all 348 tests and the Unity-log scanner
passed eight cases. The client Release build had zero warnings and errors. The test project
retains four existing nullable warnings. Server informational revision is explicitly retained
because this task changes no server source or installed server artifact.

A fresh default Bay5 audio export produced exactly the accepted 107 files / 54,373,265 bytes,
including the original schema-1 JSON and all 106 WAV files, byte-for-byte. Independent review
of nine final source/test artifacts found no unresolved integration blocker. It caught one
validator admission gap: an all-five-scene manifest with a nonempty slice must be rejected;
the corrected guard and its negative test are included.

At 09:42 CEST the GPU host was in use by other sessions in a Factory raid. Build and offline
validation continue independently; runtime control waits for the user's host-availability reply.

## 2026-09-22 — build admission correction and host hold

The user explicitly requested waiting for their release before further GPU tests. Installation
and native acceptance therefore remain pending; finish and retain the offline/staged checkpoint.

The first actual Unity attempt (`unity_build_all_20260922_093907.log`) failed before bundle
construction with `Missing hash-bound target-physics.json`. The underlying issue was the new
Equator `source.map_key`: the generic spawn-placement gate previously treated any such key as
permission to relocate three authored starts. Supplying the missing snapshot would have enabled
an unintended placement change. Equator now explicitly retains its legacy authoring path; all
other identified maps, including unknown future keys, still require generic placement proof.
The bundle validator applies the same automatic-report boundary and still honors an explicitly
requested `--spawn-placement` proof. The full Python suite now passes 182 tests.

This also preserves Equator's original default bundle-build route (`BuildAssetBundleOptions.None`), because the existing
generic-placement predicate selects LZ4 compression for generic maps. Existing map-identity
checks that refresh meshes and reject unresolved assets remain active. Retry build and final
source/bundle/preservation results are recorded below when complete.

### Native acceptance to resume after host release

1. Recheck the staged hashes and free host/control state, deploy through
   `tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 -Confirm`, and start a fresh EFT process.
   Require matching VM, host-disk and loaded client identities before testing.
2. Run Equator → Bay5 → Equator, preferably ordinary PMC raids with bots disabled for the
   focused checks. Verify real original WoodThin, MetalThick and Concrete hits with
   `Combat/LogMaterialHits`, then restore diagnostics to false.
3. On each Equator visit require 25 authored sources, 23 initialized/started players, two
   inactive and uninitialized dogs, the original looping electricity source, advancing clip
   samples/nonzero signal, and the `CommonAmbInEffects` route. Compare source distance at
   different positions. The original random delays are 4–45 seconds.
4. Each Equator exit must stop 23 players, release 23 generated empty clips and produce no
   cleanup/Fader/OnDestroy errors. Verify indoor mixer restoration. Bay5 must retain its
   accepted 24-source Summer graph, shoreline movement and 24-player/clip cleanup.
5. Compare the central view at (0,0.2,0), yaw 0 / pitch 8 with the saved baseline. Confirm
   unchanged reflection controls/navigation, no graph accumulation and normal profile return.
   Muted-host sample/RMS evidence cannot establish audible balance; hearing remains a user test.

## 2026-09-22 — built preservation evidence

Unity retry `unity_build_all_20260922_094952.log` completed successfully. It authored the original
25-player / 11-bank / 65-clip graph and baked the original 4,104 navigation sources into 10,404
vertices / 4,572 triangles. The mandatory component-identity patch then completed.

An independent read of the accepted installed bundle and rebuilt bundle confirmed all three
authored start records retain their exact Position and world Transform values, names, enabled
and trigger states, sides, categories and infiltration. `AuthorScriptsScene` already assigns
`Guid.NewGuid()` on each build: those IDs differ by design and were checked separately as valid,
nonempty, unique GUIDs. Runtime's four effective player starts remain a later native check.

All 18 built Cubemap sampling flags match their original source values. Exactly the two expected
maps (`492aa7b57d46f0b6`, `b093fe961644b515`) changed from sRGB=1 to original Linear=0; the other
16 retain their prior values. This confirms data correction, not an in-game visual approval.
Receipt: `D:/.unlazy/equator-native-features/built-preservation.json`.

The installed distribution still matches all 72 pre-task hashes; deployment has not occurred.
Receipt: `D:/.unlazy/equator-native-features/installed-baseline-preserved.json`. No test-host input
or configuration action was performed after the user requested the host hold.

## 2026-09-22 — validated staged checkpoint

At 10:18 CEST the full sanctioned build/staging sequence had passed. The mandatory patch corrected
nine MonoScript identities and proved its 218 other affected objects byte-identical. Strict bundle
roundtrip accepted 39,435 total GameObjects, the exact native component/audio graph, source fields,
clip PCM and identities. Two independent fresh exports and the actual candidate package had zero
differences. Existing extraction warnings and one built-in extra material warning remain explicit;
no validation was skipped to suppress them.

The final suite passed 182 Python tests, 348 .NET tests and eight PowerShell cases. Final client
Release/staging had zero warnings/errors. The original Bay5 source/accepted bundle also passed the
extended independent validator. Distribution validation accepted all 14 maps / 42 bundles / 14
shader sidecars, and the separate immutable-baseline check found exactly two changed files among 72:

| Staged artifact | Bytes | SHA-256 |
|---|---:|---|
| `maschine-ArenaMapPort.Client.dll` | 381,440 | `74A17BB99DF33F897691730AD484F4B776353D7FA1E08AF593F8A92ACE711A96` |
| `arena_equator_scenes.bundle` | 329,839,655 | `02E687EEE5DB21D9188FDC899F98C9A1E663D6578C13113B63B598C5DA956A7F` |

The other 70 staged files, including the server, every other map, the Equator NavMesh/preset and
shader sidecar, are byte-identical to the installed baseline. Unity's generated stand-in shader
working-file changes were restored after the editor completed; no generated assets enter git.

This checkpoint is ready for deployment after host release. **No deployment, new loaded-code
confirmation, actual Equator audio/material hits, teardown test or visual/hearing approval is
claimed.** Those remain the native checklist above. Local receipts are `build-validation.log`,
`bay5-compatibility.log`, `distribution-validation.log`, `distribution-candidate.json` and
`root-build-gates.log` under `D:/.unlazy/equator-native-features/`.

## 2026-09-22 — deployed Equator–Bay5–Equator native acceptance

The user explicitly released the shared GPU host. Sanctioned deployment installed the unchanged
candidate from commit `a728428`; all 72 installed files match staging, with exactly the two scoped
changes in the table above. The first installed check raced the still-running copy and failed
on the Equator bundle; the check after deployment completed passed. This was a verification
timing issue, not a different candidate or a bypassed check.

Fresh EFT process `adbbe116`, started 10:33:05 CEST, reported all 25 maschine plugins identical
across VM build, host disk and loaded assembly. The same comparison passed after all three raids.
SPT 4.1.6 / EFT 0.16.9.5-40743 / Unity 2022.3.43f1. These were ordinary PMC test raids with bot
amount None, using ordinary extraction; no ArenaMode or third-party configuration was changed.

### Original material hits and preserved scene

Actual X-17 projectiles selected these authored components, each with `defaultFallback=False`:

| Time CEST / log seq | Target | Material / preset | Six original values |
|---|---|---|---|
| 10:39:18.360 / #3067 | `Bench_shop (4)/Ballistic/Wood` | WoodThin / 27 | `3/0.68/0.16/0.08/0.17/0.025` |
| 10:39:38.615 / #3112 | `Coke_automate_update/Coke_automate_BALLISTIC_metalthick` | MetalThick / 17 | `69/0.45/0.34/0.13/0.22/0.031` |
| 10:40:15.889 / #3185 | `Fortification_wall_02_BALLISTIC_concrete` | Concrete / -900 | `100/0/0.41/0.16/0.23/0.037` |

The metal shot also passed through original Fabric and WoodThin surfaces before stopping in the
machine; their logged values were native rather than fallback. Both Equator raids loaded 10,404
NavMesh vertices / 4,572 triangles, with ten bot markers in one connected component and two patrol
ways. Runtime subpoints are generated and differed (92 and 97); the baked NavMesh itself is
byte-identical. Several test positions passed the full reachable-path probe, and the north and
south exits both extracted normally. This focused no-bot run is not a new bot-AI test.

Saved reflection values matched the baseline: specular cap 0.3, indoor strength 0.3051643,
indoor surface repair false, floor smoothness 0.5, cover 30 and ZeroLevel -4.4. The central view
at (0,0.2,0), yaw 0 / pitch 8 was compared with the pre-deployment view. Both show consistent
geometry and material appearance without an obvious regression; weapon pose differs slightly.
The two source-corrected cubemaps do not imply that every shiny surface has been visually accepted.
Local images: `analysis/screenshots/equator-native-atrium-before.png` and
`analysis/screenshots/equator-native-atrium-after.png` (ignored, not release assets).

### Native playback and map transitions

| Visit | Ready time CEST / seq | Native graph | Exit time CEST / seq | Cleanup |
|---|---|---|---|---|
| Equator 1 | 10:38:20.765 / #2950 | 25 sources, 23 started, two inactive | 10:43:00.891 / #3266 | 23 stopped, 23 empty clips released, zero failures |
| Bay5 | 10:46:51.066 / #3446 | 24 sources, 23 started, one Summer bank silent | 10:48:04.192 / #3607 | 24 stopped, 24 empty clips released, zero failures |
| Equator 2 | 10:50:20.153 / #3771 | 25 sources, 23 started, two inactive | 10:51:52.474 / #3907 | 23 stopped, 23 empty clips released, zero failures |

Each visit acquired its intended mixer parent at 0 dB and restored it to the previous 0 dB on
exit. Equator used `CommonAmbInEffects`; Bay5 retained `CommonAmbOutEffects` and shoreline
`CommonAmbOutBypass`. Both Equator dogs remained `active=False initialized=False`, with no clip.
Electricity retained `loop=True`. Repeated initialization counts and successful teardown show
no accumulation in the owned audio graph across these three visits.

Playback observations:

- Equator 1: electricity at 1.32 m had sample 53,145 and RMS 0.000058 (#3126); at 3.70 m,
  sample 139,981 and RMS 0.000052 (#3207). A later 12.56 m observation had RMS 0 (#3241).
  Nearby indoor wind at 1.61 m produced RMS 0.000007 (#3255). Samples advanced across probes.
  Listener volume was 1, not paused, and mixer buses reported
  0 dB. These are spatial/playback observations, not a controlled same-clip gain test.
- Bay5: the initial shoreline emitter was (64.05,-3.61,109.84), calculated before movement.
  Moving to (90,-3.9,5) moved the emitter 96.43 m to (80.46,-3.61,14.82). The water loop was
  13.88 m away, sample 484,624, RMS 0.001428 (#3595–3596), confirming the existing shoreline
  correction still works after leaving Equator.
- Equator 2: electricity again produced nonzero output, sample 13,843 / RMS 0.000028 at
  1.66 m (#3881); the same report counted two advancing sources (#3896). The initial delay
  before clips began was consistent with the authored randomized timing.

The full in-memory log still covered #1 through #3935 at final review. A targeted lifecycle
scan returned zero `NullReferenceException`, `BaseAmbientSoundPlayer`, `Fader`, `OnDestroy`,
source-stop, mixer-cleanup or root-destruction failures, including deferred destruction after
each exit. The positive control was the unfiltered error query, which returned 26 entries:
startup microphone initialization and AsyncUploadManager errors, existing F10 inspector
`_MainTex` queries on dust/shadow shaders, and one inactive MenuScreen coroutine error. The
session is therefore not described as globally error-free. None of those entries reported an
ambient lifecycle failure, and all three raids/extractions completed.

### Final state and limits

Both `Audio (arena_*)/PlaybackDiagnostics` keys and `Combat/LogMaterialHits` were restored to
false and read back. Final health was 440/440 after the three raids; ammunition was
consumed by test shots, so this is not byte-identical profile preservation. EFT was exited
normally after final menu return; UiBridge confirmed it unreachable for eight seconds.

The host OS remained muted. Technical sample/RMS/routing proof does **not** establish audible
balance or approve the mix. Original random volume ranges can clamp individual sources to
silence: native `GetBaseVolume` samples `volume + Random.Range(-deltaVolume, 0)`, and some
authored deltas exceed their base volume. For example, water can select zero gain for a raid.
This behavior was preserved; no louder override or activation of the two dogs was added.
Glass, crowd/match events and full room acoustics remain outside this feature.

Evidence: ignored `work/equator-native-features/native-acceptance-part1.json` through `part3.json`,
the comparison screenshots, `distribution-installed.json`, `deployment.log` and
`deployment-gates.log` under `D:/.unlazy/equator-native-features/`. This acceptance reused the
already validated candidate (182 Python / 348 .NET / eight PowerShell tests); it introduced no
additional production change and did not rerun the Unity build merely to record game evidence.

## User listening feedback — 2026-09-22

The user subsequently heard the port: impact sounds fit and environmental sounds are present,
but very quiet. The listening feedback did not explicitly identify its map; the later Bay5
clarification refers to dark exterior ground. This confirms audibility while leaving mix balance
open. No gain adjustment was included in the subsequent visual correction. Authored low gains,
spatial attenuation and random per-raid volumes were reviewed; see the source-backed
[audio follow-up](bay5-concrete-lighting.md#user-audio-feedback-and-follow-up).
