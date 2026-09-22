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
