# HANDOFF — dated working state

Snapshot for whoever continues (human or agent). Verify the commits named here against `git log`; this file
is updated by hand at each handover, it is not an automatic record. Read [AGENTS.md](AGENTS.md) first.

## State on 2026-09-26 — overview-map north markers clarified

Branch `feature/dynamicmaps-north-marker`, based on `783d065`. Both installed DynamicMaps
drawings now have a visible N and a gold/gray compass needle at upper left. North is world
+Z / map up. Only the old marker was replaced; all other SVG bytes and both JSONC definitions
are unchanged. Equator's generator emits the new marker; the separate updater stages it on
the existing Bay5 pack. The data-only deploy now explicitly supports Equator or Bay5.

27 exporter tests, 69 deploy fixture cases, browser visual review and installed-byte checks
passed. Installed to D:/SPT41 only. Native rendering was not retested: UiBridge-2 is available,
but windows-gpu-2 is absent from this session's tools. EFT remains idle at its main menu and
needs a restart to refresh cached map sprites. See the appended
[north marker evidence](docs/progress/equator-dynamicmaps.md#north-marker-clarification--2026-09-26).

## State on 2026-09-26 (late) — plan item 7 built, verified and deployed (light culling / volumetrics / flicker)

Branch `feature/native-dust`. Item 7 went the extractor + importer route (Arena tunes fade distances and
`_maxLightIntensity` per light, so client-side class defaults would have flattened it): `tools/extract/extract_light_culling.py`
decodes `CullingLightObject` / `VolumetricLight` / `EFT.Visual.LightFlicker` from the raw bytes (three trailing
`AnimationCurve` ints; the `CullingObject` lists are consumed, counts only), `LightCullingImporter` attaches them in three
ordered passes, validators/roundtrip/tests extended (345 Python tests). New accepted packages `work/extract/{equator,bay5}-native-v4`,
both maps rebuilt. Client: `CullingManager` fallback (`EnvironmentRepairs`), `VolumetricLightRenderer` on the raid camera
(`CameraEffectsRepairs`; the donor Cam2 already carries one), and `ArenaLightingRepairs` now takes the authored baseline
from `_maxLightIntensity` and writes `CullingLightObject.float_1` alongside `Light.intensity` — without that the culling
loop erases the brightness uplift, and 79 Equator / 107 Bay5 lights that Arena saved at intensity 0 would stay dark
(they light up now; expect both maps brighter, `GlobalLightIntensityMultiplier` may want re-tuning). Batch-import trap:
the three classes are `[ExecuteInEditMode]`, their Awake/OnEnable exceptions are logged by Unity and fail the build's log
scan — editor stubs (stand-in shader `Sandbox/VolumetricLight`, a `FlickerSystem` on a HideAndDontSave object) keep it
clean. Host-verified 22:15–22:37 (Lighting summary `cullingLights=472/186`, fallback lines, no errors; fade and god-rays
not settled by screenshot). Deployed to `D:\SPT41` and `D:\SPT41vanilla`. Details: docs/progress/two-maps-item-7-light-culling.md.
Next: plan items 8 (AI hubs), 10 (banners), then 9 (release).

## State on 2026-09-26 — round 3 verified on the new host SPT-GPU-2; Equator's atrium rains by design

Branch `feature/native-dust`. The GPU test host is now **SPT-GPU-2** (MCP servers `uibridge-2` / `windows-gpu-2`; same
click points, raid start ≈ 9 s, `D:\SPT41` still mirrors it). Round 3 verified there (EFT `cd73afd7`, 18:04–18:06,
forced rain, max settings): F8 at the atrium reads 47.51 m with `FullyIndoorCover=true` and 23.1 m (a beam) with
false, streaks visible on the screenshots; `AmbientRangeMultiplier` set live mid-raid logs `live settings applied …
re-targeted 23 playing source(s)`. User decision: the atrium's broken glass roof is Arena's authored geometry (pane mesh
`m_IsActive=false` in the raw level, "broken glass atlas" material, inactive glass-shard debris under it, no toggling
script; the puddles are generic mall dressing and prove nothing either way), so it rains — `FullyIndoorCover` defaults to false everywhere (DefaultsRevision 3 re-seed, `DefaultsRevision = 3` read
back after the restart) and the key stays as an opt-in dry mall. Correction: the puddle ripple follows the global rain
value (`RippleController.WaterCucle`), never the depth photo, so no cover setting removes it. Deployed to `D:\SPT41`
and `D:\SPT41vanilla` (vanilla server restart = user). Details: item-6 doc "Round 3 verified on SPT-GPU-2", items-1-3
doc tail. Next: plan items 7 (light culling), 8 (AI hubs), 10 (banners), then 9 (release).

## State on 2026-09-25 (afternoon) — plan items 4–6 built, verified and deployed

Branch `feature/native-dust`. The three parallel agent branches (NavMesh modifiers, physics props, rain cover)
are merged with the review fixes; both maps were re-extracted into `work/extract/equator-native-v3` /
`bay5-native-v3` (now the accepted inputs, AGENTS.md) and rebuilt. What the first real build/raid taught, all
fixed in the same commit: bake-only `NavMeshModifier`/`Volume` components must be stripped before the bundle
build (`NavMeshModifierImporter.StripBakeOnlyComponents`, else `patch_monoscript_identity` rejects the bundle);
the bake's Triggers/LevelBorder layer rule must not apply to markups (112/113 Bay5 overrides sit on layer 13);
the roundtrip validator needs the authored rain-cover objects (`raincover_extra_counts`); imported
`SmallPhysicsObject`s need Arena's `SoundType` (decoded from the raw bytes) plus a null-bank guard
(`PhysicsPropSoundRepairs`). Host raids 13:09–14:35 on EFT `9f0fe0dc`/`70aeac2c`: Bay5 NavMesh 22117/10213
(was 22190/10248), Equator 10404/4572 unchanged; 391/413 and 18/18 props active, touch without exception;
dry floor under Bay5's warehouse roof in forced rain. Evidence: the three `docs/progress/two-maps-item-{4,5,6}-*.md`
"Lead integration"/"Host evidence" sections. Deployed to `D:\SPT41`; `D:\SPT41vanilla` gets the same build
(user restarts its server). Known noise: one `SmallPhysicsSystem.Update` NRE per raid end (vanilla teardown
order), `Books` props are silent (no bank on this SPT build).

**Evening correction (user report: rain indoors on both maps, ambience only audible next to the source).**
Item 6's rain part was wrong: `AuthoredRainCover=true` had switched the depth-photo heuristic off, Equator has
no WeatherObstacle, and Arena's 669 Bay5 DryPlanes are EFT-Lighthouse leftovers (root `Lighthouse_DryPlanes`,
a square kilometre off the map). `extract_raincover.select_dry_planes` now drops foreign sets (Bay5: 669 → 0,
`weather_obstacle=false`), the client keeps the heuristic armed on every map (a real WeatherObstacle only adds to
the photo), Bay5 was rebuilt. Ambience: new `Audio (<map>) › AmbientRangeMultiplier` (default 1) stretches the
authored max distances before the graph's Awake bakes the rolloff curve. `PhysicsPropTeardownRepairs` silences
the raid-end `SmallPhysicsSystem.Update` NRE. Host-verified 17:49–18:02 (F8 depth readbacks: Equator mall roof
5.2 m, Bay5 warehouse 8.0 m, container 1.4 m, outdoor control = ground; range line `x3.00 … 25 player(s)`;
clean teardown). Details: item-6 doc "Correction", items-1-3 doc "Item 1 follow-up", item-5 doc.

**Round 3 (evening, not host-verified at the time — verified 2026-09-26, see the section above; the default of
`FullyIndoorCover` has since moved to false):**
Equator's atrium roof is an open lattice in Arena's data (`equator_roof_Glass_LOD0` ships inactive), so rain fell
through the cells → new key `Lighting (arena_equator) › FullyIndoorCover` (default true) draws the CoverAll quad;
the three ambient keys now apply live (`ApplyUserSettingsLive`). Bay5 has no rain-reactive puddle decals in
Arena's data (content gap, documented, nothing changed). Built and deployed to `D:\SPT41` only; `D:\SPT41vanilla`
waits for the host verification (F8 at the atrium ≈ 47 m, live-apply log line). Next: plan items 7 (light culling), 8 (AI hubs), 10 (banners), then 9 (release).

## State on 2026-09-25 — plan items 1–3 verified and deployed

Branch `feature/native-dust`, commit `a947cce` (+ docs). Ambient gain/offset keys per map (neutral default),
`BarbedWireRepairs` on Arena's concertina wire (192 triggers on Bay5, damage verified), release names
"Arena: Equator" / "Arena: Bay 5" (server log + map-select screen). Host raids 10:22–10:27 on EFT `027b491c`;
deployed to `D:\SPT41` and `D:\SPT41vanilla` (vanilla also received the native-dust scene bundles it was
missing; its server must be restarted by the user for the new names/base.json). Evidence:
[two-maps-items-1-3.md](docs/progress/two-maps-items-1-3.md). Next: items 4–6 (one rebuild per map).

## State on 2026-09-24 — inventory of remaining native content (no code change)

Question answered: what of Arena's Equator/Bay5 is still not ported. A class census over all source scenes
of both maps (including the excluded TeamFight_AI, Sound, Culling and Scripts scenes; Bay5 had none before)
is in [native-content-inventory.md](docs/progress/native-content-inventory.md) with a ranked candidate list:
ambient gain, Arena's own AI anchors (PatrolPoint 23/47, AICorePoint 14/11), Bay5's 669 DryPlane + 30
IndoorTrigger rain-cover boxes, physics props (391 SmallPhysicsObject + 413 Rigidbody on Equator), NavMesh
modifiers (124+93 / 304+75), barbed wire (246, Bay5), decals, flares; ArenaMode-side: pickups, lighting
director allowlist, team starts. Not worth it / blocked: glass (user decision), crowd/monitors/gates
(Arena-only classes), PerfectCulling, impostors, mannequins. Nothing was built or deployed; GPU host untouched.

Scope decision 2026-09-25: the first release contains Equator and Bay5 only; the other twelve maps are
deferred (still built/staged, no further work). Map quality on the two comes before ArenaMode features; the
ordered plan is docs/PLAN-TWO-MAPS.md (feasibility spikes of 2026-09-25).

User answers 2026-09-25: Team Deathmatch/Tactical AND pickups are wanted on both maps later — faction spawn
pools and pickup surveys are therefore real map-side work, not optional polish (inventory §7). Bay5 DynamicMaps
is installed (`Maps/ArenaMapPort_Bay5/ArenaMapPort_Bay5.jsonc`, user's own pack) and must not be listed as open.

## State on 2026-09-23 — original Equator and Bay5 dust verified

Branch `feature/native-dust`, based on `29ea1be`. Both maps now import the original lossless
Alpha8 Texture3D and reuse SPT's native dust animation with the authored particle meshes.
The runtime restores padded bounds and the native transparent queue on one owned material;
unload restores renderer state and releases that material. No lighting or audio gain changed.

Both full bundle pipelines and independent byte/binding comparisons passed, including all
519 normal textures. Navigation bundles and shader sidecars remain identical. Checks:
356 .NET passes, 222 Python passes /27 expected skips, eight PowerShell cases and the complete
14-map distribution. Exactly four of72 staged files changed: both DLLs and the two scene bundles.
Sanctioned deployment and final installed hashes passed.

Fresh EFT `3e1ca2da` verified Equator → Bay5 → Equator with visible moving dust, correct native
speeds, unchanged meshes, queue3000 and three clean material releases. One existing MenuScreen
coroutine message appeared on the first Escape; identical pre-change archive entries are recorded
in [the native evidence](docs/progress/native-dust.md). No dust errors occurred.

Current accepted extraction inputs are `work/extract/equator-native-dust` and
`work/extract/bay5-native-dust`; canonical built outputs remain `work/build/full` and
`work/build/bay5`. Use absolute descriptor paths for Unity builds. Older native-feature packages
lack the new raw volume payload and must not replace these accepted inputs.

Bay5 already has a user-installed DynamicMaps map. Its JSONC/SVG and ArenaMapPort's complete
configuration were verified unchanged. Final profile: 440/440 HP,100 energy,100 hydration;
all145 inventory records preserve IDs, templates, parent relationships and slots. All three
tests were botless and short. EFT closed normally around11:54; server running, GPU host free.
The remaining quiet-ambience balance is a separate follow-up; glass remains deferred.

## State on 2026-09-23 — remaining Bay5 barrier and shadow-cache spam fixed

Branch `feature/bay5-barrier-followup`, based on `d842ef8`. The one reported unsuffixed
`Military_sandbag_barrier_03` had all three separate shadow models inactive already in Arena's
source. A descriptor-scoped runtime repair activates its existing ShadowsOnly LOD proxies.
The archived 20,583 null-material exceptions came from DistantShadow keeping retired material
references after live lighting changes. Clone replacement/restoration now resets its pending
work and schedules a native snapshot rebuild. F10 also avoids reading absent `_MainTex` slots.

Build and checks passed (353 .NET tests, 190 Python passes /27 expected skips). Only client and
server DLLs changed among72 installed files; bundles are unchanged. Fresh EFT `71873626`
verified repeated Bay5 live changes, Equator, two Bay5 reloads and the exact barrier's visible
shadow (F10 07:21:06.699). The reported exception did not recur. See
[source, deployment and native evidence](docs/progress/bay5-barrier-followup.md).

The user's current Bay5 lighting settings had been reset before this turn: ground multiplier1,
hands multiplier1, specular cap1. All temporary tests were restored to these current values;
do not restore older milestone preferences automatically. Equator cap remains0.3.

One repeated Bay5 visit re-enabled bots; the test character was killed and recovered through
the existing Redeploy UI. Final check after a botless repeat: 440/440 HP,100 energy,100 hydration,
all non-structural inventory items preserved. No profile files or third-party configs were
edited. Set bot amount None and bosses off again before each diagnostic visit, including after
a map switch. EFT closed normally at about07:22; server running, GPU host free.

## State on 2026-09-23 — Bay5 shadows, veil and debugging controls verified

Branch `feature/bay5-shadows-menu`, based on `7147e8e`. All four requested parts have passed
build, deployment and native checks. ArenaMapDebug commit `86f9524` is installed;
F12 shows per-map options only for Equator and Bay5 while keeping other maps' saved values.
The final client also retains a map-specific hands ambient-highlight scale (neutral 1;
local Bay5 0.35), restoring original fields on teardown.

Source and bundle comparisons identified an actual packed-normal defect behind incorrect
lighting: PNG normal-map import repacked an already encoded alpha/X channel to opaque.
Two PNG-import workarounds passed a small fixture but failed full-map checks and were rejected.
The accepted importer uses native Texture2D assets, preserving source channels, color space,
compression, complete mipmaps, filtering and stable GUIDs. Full builds and independent checks
passed all 519 normals across Equator/Bay5. Only the client and those two scene bundles changed
among the 72 installed files; no failed candidate bundle was deployed. Fresh EFT instance
`453175d3` verified Bay5 → Equator → Bay5: coherent exterior shadows, natural cardboard instead
of the blue-white veil, reduced knife highlight, neutral Equator hands, repeated Bay5 settings
and clean restoration. Both buttons skip selection/countdown and return directly with living
HP protection. Final control point: 440/440 HP; EFT closed normally at about 00:33 CEST.
See [complete evidence](docs/progress/bay5-shadows-menu.md).

Current inputs must remain `equator-native-features` and `bay5-native-features`. CPU-readable
compressed normal copies total about 178 MiB for Equator / 305 MiB for Bay5; the actual increase
in total game memory has not been measured. Local Bay5 ground brightness remains 2 and hands
highlight 0.35. The ineffective BuildBundles startup workaround was removed. The GPU host is free.

During an earlier long diagnostic raid, time expired and normal MIA penalties applied.
The test character was healed and replenished through normal game UI, reaching 440/440 HP,
100 energy and 100 hydration before EFT was closed at 22:24. The MIA record and consumed
medical/food items remain; no profile-file edits were made. Keep later test raids short.

## State on 2026-09-22 — Bay5 exterior ground brightened and tested

Branch `feature/bay5-concrete-lighting`, based on `cfbbf16`. The user's clarified problem was
dark exterior ground. The sampled Concrete collider covers an original layered asphalt material;
no missing-texture defect was found. New live `Lighting (arena_bay5)/VertexGroundDiffuseMultiplier`
scales only that shader family's layer RGB. Neutral default 1; local Bay5 setting 2. Reflection
inputs and other lighting experiments are restored/preserved. F10 now emits complete numbered
parts so UiBridge can retrieve later material slots.

Release client C02C98D130616F9CA96ECD45870ED193A2852B2C49C318FA30F507EDC330ABDC was deployed
through the sanctioned script. All 72 installed identities match staging; only the client changed.
Fresh process `09cca5c0` verified Bay5 → Equator → Bay5, visible yard/north-lane improvement,
absolute 1 → 2 → 1 scaling, unchanged reflection fields, neutral Equator, saved 2 on revisit,
and nine-tint cleanup on both Bay5 exits. Health 440/440 and ammunition 37+1 at final extraction.
The user's preferred brightness is still a visual check; bright authored wet patches remain.
See [ground visibility evidence](docs/progress/bay5-concrete-lighting.md).

EFT was closed normally after the final menu return; the GPU host is free.

The user has now listened: impacts fit, ambience is audible but very quiet. Only the ground
complaint was explicitly assigned to Bay5. Audio gain was not changed; source-level volume and
randomization findings plus a scoped gain follow-up are documented in the same progress entry.
The DynamicMaps prototype and prior native-feature work below remain intact.

## State on 2026-09-22 — Equator DynamicMaps prototype installed and tested

Branch `feature/equator-dynamicmaps`, based on `2c673e2`. The optional Equator SVG/JSON
pack is generated under ignored `work/dynamicmaps/equator/` and installed through the new
`deploy-to-spt.ps1 -DynamicMapsPackage ... -Confirm` mode. Existing map bundles and mod DLLs
were not rebuilt. Standard deployment remains the original 72-file mapping.

Fresh EFT process `96ebdca3` (11:36:40 CEST), DynamicMaps 1.2.1, SPT 4.1.6: inventory → MAP
automatically selected Equator. Four positions, all four cardinal arrow directions and both
live exits matched. Normal South Gate extraction and main-menu return passed; final HP 440/440,
ammo 37+1 unchanged. No DynamicMaps warnings/errors. EFT is closed and the host is free.

27 exporter tests, 59 deploy fixture cases and independent format/native review passed.
Two full exports were byte-identical. Generated data stay out of git. This is a simplified
ground plan, not exact walkability; upper decoration and extensive labels are absent.
Bay5 is a future follow-up, not included. See [acceptance](docs/progress/equator-dynamicmaps.md)
and [reproduction](tools/dynamicmaps/README.md).

## State on 2026-09-22 — Equator native features deployed and tested

The user released the GPU host, lifting the earlier hold. Candidate `a728428` on
`feature/equator-native-materials-audio` is now deployed through the sanctioned script.
All 72 installed hashes match staging; exactly the client and Equator scenes bundle differ
from the immutable Bay5 baseline. Their SHA-256 values remain those listed below. No further
production change was needed during native acceptance.

Fresh EFT process `adbbe116` (10:33:05 CEST) confirmed all 25 maschine plugins identical across
VM, host disk and loaded code before and after Equator → Bay5 → Equator. Ordinary PMC raids
used the menu's bot amount None. Real WoodThin, MetalThick and Concrete hits selected the original
values with `defaultFallback=False`. Both Equator visits initialized 23 of 25 authored sources;
the two original inactive dogs stayed uninitialized. Samples advanced, nearby electricity/wind
produced nonzero output, and the indoor mixer route was correct. Each Equator exit stopped 23
players and released 23 empty clips with zero failures; Bay5 retained its 24-source Summer graph,
moving shoreline and 24/24 cleanup. No native audio lifecycle exception appeared through final
menu return. Existing F10 shader-inspection and other startup/UI errors are recorded separately.

Equator's saved reflection controls and 10,404-vertex / 4,572-triangle NavMesh remain unchanged.
The matched atrium view has no obvious visual regression; this is not a universal reflection
quality claim. All three test diagnostics were restored to false. Final health was 440/440;
EFT was exited normally and the host is available. Test shots consumed ammunition normally.

**Remaining subjective check:** listen to the environmental mix. The OS was muted, so the
technical playback evidence does not establish audible balance. Original random volume ranges
can also produce silent individual sources for a raid; no gain override was introduced. Glass
remains deferred. See the appended [native acceptance](docs/progress/equator-native-features.md).

## State on 2026-09-22 — Equator native features staged; GPU hold

**Built and staged, not deployed or natively accepted.** The user chose original Equator materials
and environmental audio, then explicitly asked to wait for their release before further GPU tests
because other sessions are using the host. Do not take over the host or install this candidate
before that release. Branch `feature/equator-native-materials-audio`, based on `2862da7`.

The opt-in `extract_equator.py --native-features --scenes all5` package adds 2,892 original ballistic
components and 25 authored ambient players / 11 banks / 65 clips. Original activation is preserved:
23 active grouped players, two inactive dogs. Runtime uses the native indoor mixer and keeps cleanup
away from uninitialized players. Glass, crowd/match events and full room acoustics remain excluded.

Unity retry `unity_build_all_20260922_094952.log`, mandatory component-identity patch, two independent
fresh exports, original-source/bundle comparisons and distribution checks passed. Tests: 182 Python,
348 .NET, eight PowerShell; final client Release build has zero warnings/errors. Bay5's actual source
and accepted bundle still pass the shared validator, and a fresh default Bay5 audio export is
byte-identical to all 107 accepted files. Generated game assets stay ignored.

All original Equator scene objects and 3,645 source asset files are unchanged. The NavMesh is
byte-identical (10,404 vertices / 4,572 triangles); authored start behavior is identical apart from
the existing fresh-GUID-per-build policy. Two of 18 cubemaps now retain their original Linear sampling.
Adding Equator's map identity had exposed an unintended generic spawn-placement path; the bounded
Equator-only guard preserves its legacy start authoring and bundle build option.

Only two of 72 staged files differ from the installed baseline:

- Client SHA-256: `74A17BB99DF33F897691730AD484F4B776353D7FA1E08AF593F8A92ACE711A96`.
- Equator scenes SHA-256: `02E687EEE5DB21D9188FDC899F98C9A1E663D6578C13113B63B598C5DA956A7F`.

All 72 installed MapPort files still match the pre-task baseline. The server, other maps, NavMesh,
presets and shader sidecars are unchanged in the candidate. Resume with the sanctioned deploy script,
fresh EFT identity check, and Equator–Bay5–Equator tests after host release. Required evidence includes
real original material hits, 25/23/two-inactive Equator audio, sample/RMS/spatial/mixer behavior,
23-player/clip teardown with no native Fader errors, Bay5 regression control and the saved central
reflection view. A muted-host technical signal test cannot establish audible balance.

Full provenance, build issue, artifact receipts and exact acceptance checklist:
[Equator native features](docs/progress/equator-native-features.md). The previous Bay5 acceptance below
remains the installed state; this Equator checkpoint does not supersede its runtime evidence.

## State on 2026-09-22 — Bay5 original materials and harbor ambience

**Built, deployed and natively verified.** Branch `feature/bay5-native-materials-glass-audio`,
based on `aa5e3cc`. User explicitly deferred glass: all 147 original panes are inactive, and no
glass activation or WindowBreaker port was added. The task restores 6,322 original ballistic
components and 24 native ambient sources / 13 banks / 106 original PCM clips. Three Linear
cubemaps retain their original color space. Original geometry, navigation and reflection knobs
are preserved. Independent source/package/bundle comparisons and reproducibility passed;
152 Python tests, 348 .NET tests and eight PowerShell cases passed. Final client build: zero
warnings/errors. Original assets remain ignored; proprietary audio is not a cleared release asset.

Only two of 72 installed files changed; all other maps, the server and NavMesh are byte-identical:

- Client SHA-256: `24F9753F97D160CD59380EE2442C7EF15A233A5C2350A7ADFC660D68BB1AB015`.
- Bay5 scenes SHA-256: `3957E836C63C0F76AD8433DDC2236706C76E4E36893A945AA98B9A382E58E261`.

Final process `90bc532c` (08:29:03 CEST) confirmed VM = host disk = loaded. Real WoodThin,
MetalThick and Concrete projectile hits selected original values, with no default fallback on
those targets. Native Summer audio started 23 players and retained the one missing seasonal
bank as silence. Water was correctly placed before any movement and followed the native shoreline;
sample advancement, nonzero source RMS and both mixer routes were verified. Two SPT shoreline
initialization defects were corrected only inside the owned Bay5 graph. The host OS output is
muted: this is technical playback evidence, not an auditory mix-quality approval.

The Bay5–Equator–Bay5 sequence passed. Both Bay5 exits stopped 24 players, released 24 generated
clips and reported zero cleanup failures; the revisit retained exactly 24 sources. Equator had
no Bay5 audio and kept its accepted reflection settings. Both Bay5 teardowns restored 642
renderers / 1,008 clones; Equator restored 607 / 1,103. Bay5 used its ordinary South exfil;
the Equator Debug disconnect remains scoped to debug-started sessions. Final profile health
was 440/440, diagnostics were restored to false, and EFT was exited normally around 08:44.

Full source/runtime reasoning, native timestamps, caveats, screenshot paths and local receipts:
[Bay5 native features](docs/progress/bay5-native-features.md). Next: user hearing/visual approval,
then one further map through the existing map recipe. Dust/extra decals, full room acoustics
and glass are separate follow-ups; no additional feature expansion is implicit.

## State on 2026-09-21 — Bay5 covered-floor reflections accepted at 22:13 CEST

**Bay5's covered Hangar floor now has independent reflection and smoothness controls, built,
deployed and tested in-game.** Branch `feature/bay5-indoor-materials`, based on `9f8911e`.
The render-only partition corrects covered floor portions inside mixed building meshes; original
wall/decal/outdoor slots are retained. All four Hangar LODs follow the same route. F12 section
`Lighting (arena_bay5)`: `IndoorSurfaceRepair=true`, `IndoorSurfaceReflectionStrength=0.20`,
`IndoorSurfaceSmoothness=0.50`. Existing Bay5 cap `0.2957746` and legacy settings are preserved.
Other maps default to the new surface path disabled.

Final client SHA-256:
`8F8E79C808BDF7D1054C68180443DFE6F82E61CE4F3AD7A84BDB223AF3593CBA`.
Of 72 deployed artifacts only that client changed; 71 remain byte-identical to the accepted
background delivery. No scene/NavMesh/server/ArenaMode changes. Release build had zero
warnings/errors; 348 tests passed and independent review found no blocking issue.

Fresh GPU process `851020e4` (21:53:32) verified matching VM, host and loaded identities. Bay5
automatically partitioned 32 render meshes including LODs / 21,032 covered triangle fragments.
Enabled classification took 602–642 ms in six observed passes. Same-view material and independent
knob controls, exterior on/off control, glass false/true plus F8, and the Bay5–Equator–Bay5 sequence
passed. Both final Bay5 teardowns restored 642 renderers / destroyed 1,008 owned material clones.
Equator retained 607 renderers / 1,103 clones with the new path off. No MapPort warnings/errors in
the recorded final process. EFT returned to MainMenu and was exited normally at about 22:13.

The covered concrete is less reflective. Original puddles still reflect strongly, and small
material transitions can remain around roof openings: cover is sampled, not exact polygon
clipping. No new interior-reflection renderer or exhaustive all-distance LOD walk is claimed.
Full evidence, local screenshot paths, algorithm limits and receipts are in
[Bay5 covered-floor reflections](docs/progress/bay5-indoor-materials.md).

The next gameplay step remains one additional map through [the map recipe](docs/MAP-RECIPE.md).
For further Bay5 visual tuning, use the two new controls independently; authored puddles are
outside this repair. Historical gameplay/profile acceptance below was not repeated by this task.

## Previous acceptance on 2026-09-21 — Bay5 harbor background at 11:56 CEST

**Bay5's original harbor background is built, deployed to the GPU host and tested in-game.**
The native views show the northern gantry, southeast portal crane and original container stacks.
Bowl was left unchanged after the user confirmed its stadium is present. Equator's accepted
reflection/lifecycle behavior below remains the baseline; its bundles and base are byte-identical.

| Repository / artifact | Current accepted implementation |
|---|---|
| MapPort | `feature/bay5-background`: background `4f2025e`, legacy preset guard `2ba4d71`, bot marker `b6e0692` plus its generated server base |
| Bay5 Unity bundle | `unity_build_all_20260921_104434.log`; 10 loaded scenes, 22,190 NavMesh vertices / 10,248 triangles |
| ArenaMode | Unchanged implementation `75a7430`; no files edited in this task |
| Final receipts | `D:/.unlazy/bay5-background/distribution-after.json`, `distribution-delta.json`, `install-final-verification.json`, `native-final.json` |

Client SHA-256: `6E12B25466F156A977F39DF3ABA44FA8AF61A6D54D9BCFEE789942B46B8140FD`.
Bay5 NavMesh SHA-256: `CD7A3308E9D21F54F4FCF19242114B97E3952FB452F2E1F6ADAF0082D7DE64F3`.
Bay5 base SHA-256: `CEAA30A95521FB80FEE39A623B43E69FF5FA5D32F70914A1A4DF50993A76775C`.
All 72 deployed files matched their staged hashes; seven intended files changed and 65 remained
byte-identical. The vanilla test install was not updated.

Fresh process `d1121cb1` (11:48:44 CEST) confirmed all 25 plugins plus Redeploy Core identical
across VM, host disk and loaded code. At 11:52:47, the ten bot markers formed **one connected
NavMesh component, zero isolated/off-mesh points**. The northeast marker is now three metres
south at (106.267, -3.8574, 61.7033), avoiding an automatic move onto an isolated container-side
patch. Four player starts and all ten effective bot positions passed full path and standing-room
checks. Deathmatch bots moved, fought and replenished; protected return preserved the full saved
profile except the background hideout timestamp, including the final flush. EFT was quit at 11:56.

Both ordinary exits completed in process `23dee6d0` on the **same scene/NavMesh bundles** before
the final single bot-position change. RainBounds and indoor classification stayed unchanged.
Background colliders participate in navigation, while distant renderer bounds do not enlarge
the bake volume or weather scans. The skyline is included, but floor-view visibility was not
established. No new Gun Game ladder, long-session or performance acceptance is claimed.

Full build, native evidence and the separate ArenaMode HUD warning are recorded in
[Bay5 background acceptance](docs/progress/bay5-background.md). That warning comes from
`GrenadeInventoryCounter.CollectSummary` during kit setup; this task does not modify ArenaMode.
Next gameplay work remains one additional map through [the map recipe](docs/MAP-RECIPE.md).

## Previous acceptance on 2026-09-21 — 08:32 CEST (historical artifact identities)

**Bay5 ArenaMode integration and Equator's reflection cap are deployed and host-tested.
The final MapPort repeat also passed: corrected Bay5 NavMesh, both ordinary exits, and automatic
Equator indoor classification before and after Hideout in the same restarted process.**

| Repository / artifact | Accepted implementation |
|---|---|
| MapPort | `feature/bay5-and-equator-reflections`, code `d896214` |
| Bay5 Unity bundle | Built from `c95bdca`, `unity_build_all_20260921_074006.log`; 14,754 vertices / 6,816 triangles, natively verified at 08:27:48 |
| ArenaMode | `feature/bay5-map-integration`, code `75a7430`; `eb3537d` adds first-pass native evidence only |
| Artifact receipt | `D:/.unlazy/bay5-reflections/mapport-accepted-candidate-artifacts.json` (08:07 CEST; accepted by the later native repeat) |

The artifact receipt records the complete hashes. Client SHA-256 is
`9957897262AA1F5C0AE9E2BFFB2DA6B1FB80B1EE54F8DD711DDBCE63FFF92D34`; Bay5 NavMesh SHA-256 is
`5BA00920DFA3A5E9E5D33E05A3A08CD3B9B9959C31C83A87D2EE5FDCC5C00DEB`.
Fresh GPU process `c2a37398` (started 08:11:03) verified identical VM/host/loaded identities for all
25 maschine plugins and Redeploy Core. Equator automatically classified **607 indoor renderers /
1,103 clones** at 08:13:48 and again at 08:25:40 after an intervening Hideout visit; both raids
capped **7,022 material slots**, without F8. Hideout took roughly three minutes to become responsive,
then recovered without killing/restarting the process. Final Bay5 probes and both ordinary exits
passed afterward. The client was quit at 08:32 for log flushing; it is not left in a raid.

**Current implementation:** Equator and Bay5 each have four player starts, ten separate bot markers
in `BotZone_Main`, and two ordinary test exits. ArenaMode admits both maps independently for
Deathmatch/Gun Game with an optional MapPort dependency and guarded revision-2 default migration;
custom lists, disabled settings and intentional removals survive. Tactical and team modes are excluded.
MapPort's complete Python/server suites and distribution oracle passed after integration; the full
distribution remains 14 maps / 42 bundles / 14 shader sidecars / 14 server base files plus two DLLs.
The focused reflection/lifecycle oracle passed **55 tests** and a zero-warning/error client build.
ArenaMode's independent verifier passed **691 server tests, one optional APBS skip**, its native
source-linked harnesses and five fault controls. Offline checks, the first ArenaMode pass and the
final MapPort repeat have separate evidence; the final ordinary raids did not repeat DM/GG.

### Native history that remains valid

- **Equator ArenaMode, 2026-09-20:** both modes admitted, PMCs replenished from the atrium, real combat
  deaths respawned (two Deathmatch, one Gun Game), and protected return restored the entire saved
  profile except the background hideout timestamp. Gun Game reached tier 1/24 only. See
  [equator-arenamode-integration.md](docs/progress/equator-arenamode-integration.md).
- **Bay5 first pass, 07:29–07:45 on 2026-09-21:** the older **15,045 / 6,945** NavMesh passed all
  fourteen authored spawn-point probes and connected the northern depot opening and warehouse.
  South extraction completed after eight seconds at 07:33:45. Native Deathmatch and Gun Game
  replenishment, combat respawns (0.63 / 0.62 seconds) and protected return passed. Entire normalized
  before/after profiles matched after the final persistence flush; Gun Game remained tier 1/24.
  Those tests precede the new bake and north-exit correction. See the append-only
  [Bay5 integration checkpoint](docs/progress/bay5-arenamode-integration.md).
- **Equator reflections, 06:52–07:04:** same-view cap 1 → 0.30 → 1 reduced/restored the armored van
  and sandbag sky sheen; the already-damped lounge parquet stayed unchanged. F12 section
  `Lighting (arena_equator)` / `ReflectiveSpecularCap` defaults to 0.30; 1 restores uncapped inputs.
  Bay5 defaults to 1 (disabled). See [equator-specular-cap.md](docs/progress/equator-specular-cap.md).

### Fixes confirmed in the final loaded-build repeat

- Bay5's original spawn boxes remained non-trigger on native `Triggers` layer 13: physically ignored
  by the player but baked into ghost NavMesh islands. `c95bdca` excludes those collider owners;
  exactly 20 old Bear/Usec boxes are affected, no mesh floors. The new **14,754 / 6,816** bake passed
  the old depot sample (52.7, -3.8, -6.3), all fourteen authored spawn points, adjusted runtime bot
  positions, both exits and additional depot/warehouse connectivity and standing-room probes.
- The first north-exit runtime trigger sampled an upper floor 5.6 m above its authored point.
  `fce614a` prefers a nearby floor before the legacy wide search; `30c3e3d` moves north to the
  clear loading door at (96, -2.9252, 61), with descriptor-compatible decimal spelling in `d896214`.
  Both exit triggers sampled the nearby floor with 0.0 m logged displacement. North and south
  completed ordinary extraction at 08:28:58 and 08:31:03 respectively, reaching ExitStatus and then
  the menu. The duplicate eastern depot gate remains closed; interior connectivity passed.
- Rain-camera subscription reattachment alone did not fix every reused-donor startup path.
  `26dd9f2` adds a raid-ready cover bootstrap; `d514957` reuses successful camera initialization
  instead of allocating rain resources again on that camera. Both automatic Equator classifications
  passed around the Hideout transition in one fresh process, without a manual diagnostic render.

The final results are recorded in [Bay5 integration](docs/progress/bay5-arenamode-integration.md),
[Equator reflection evidence](docs/progress/equator-specular-cap.md) and ignored
`D:/.unlazy/bay5-reflections/native-c2a37398-evidence.json`. Navigation used coordinate-assisted
probes, not a complete walk-through. Gun Game remains a tier-1 smoke test; no full ladder,
long-session or performance acceptance is claimed.

**Other twelve maps:** built, validated and staged in `dist/`, but still without authored gameplay
bots/exits and without ArenaMode acceptance. Customs, Shoreline and Woods also await terrain runtime
validation. Earlier "inherited exclusions" describe the original build pass; additional gameplay
integration proceeds one map at a time through [docs/MAP-RECIPE.md](docs/MAP-RECIPE.md).

**Installs:** `D:\SPT41` is the deployment mirror of the GPU host and holds the accepted build.
`D:\SPT41vanilla` was not updated in this Bay5/reflection pass and retains its earlier stage-1 build;
verify its artifacts separately before testing there. Use the authorized deployment script, not
manual copying. Keep the first-pass history and final-repeat evidence separate when continuing.

## Decisions (user, 2026-09-20) — see docs/SPEC.md "Decisions 2026-09-20"

ArenaMode is the target; no loot; no scav raids; bundles are distributed as built; Equator first.
The full gap analysis behind the stages below lives in the user's session notes; the tiers are:
(1) combat/death correctness and the NavMesh bake on Equator, (2) the other maps through the Equator
recipe, (3) parity only where ArenaMode needs it, (4) release hygiene.

## Stage 1 — done on the host (2026-09-20 evening), one item left for the vanilla install

| Item | Status | Evidence / where |
|---|---|---|
| `EffectsController.OnHealthDied` null-guard on the Cam2 camera (player death) | built; installed and harmless on the host (death is intercepted by Redeploy there), the Cam2 path itself needs a raid on D:\SPT41vanilla | `EffectsControllerRepairs.DeathScreenEffectsGuard`, `HarmonyPatches.InstallDeathEffectsGuard` |
| Bullets ignore LowPolyCollider-only props (13 on Equator: racks, rubble, fences, booth revolving doors) | verified 20.09. 19:03 (13 twins added, `stage1-combat.md`) | `BallisticTwinRepairs.cs` (`[Combat] BallisticTwins`), runs from `SpawnInfiltrationRepairs`' postfix |
| NavMesh bake kept booth doors, `SPAWN_BOUND*`, the FCK shutter and the 41 TeamFight `LevelBorder` walls → islands for bots | verified 20.09. 19:33: ground floor incl. booths and FCK hall is one component (`stage1-combat.md` host test 2) | `BakeNavMesh.RoundOpenedReason`, `ArenaMapDescriptorData.opened_barriers`, `tools/maps/equator.json` |
| Host raid: death path, PMC hostility in combat, bot pathing into the booths | done 20.09. (PMCs hostile via SAIN combat layer, killed the player; booths reachable) | `docs/progress/stage1-combat.md` |
| Extraction UI look during the countdown | not inspected (de-prioritised, ArenaMode target) | — |
| Survived/KIA post-raid screens, insurance | de-prioritised (ArenaMode target) | — |

Next gameplay work: continue one additional map through [docs/MAP-RECIPE.md](docs/MAP-RECIPE.md),
then tackle a terrain map. Bay5's current acceptance is complete; do not extend it to the other
twelve maps or to untested full Gun Game ladders and prolonged sessions.

Release (stage 4, not started): `dist/` is what gets shipped, zipped as-is (BepInEx/ + SPT_Runtime/ trees);
no packaging script, CHANGELOG, LICENSE file or version bump exists yet — `PluginVersion`/`ModMetadata.Version`
are 0.1.0.

## Pending user actions

- Restart the `D:\SPT41vanilla` server after a server-DLL/base.json deploy (client-only changes need only an
  EFT restart).
- Decide when to bump `PluginVersion` (still 0.1.0) and add a LICENSE/CHANGELOG (stage 4).

## Environment facts an agent cannot infer

- GPU host `WIN-Q470OL2A14A`, EFT via SPT launcher, profile `ArenaGPU20260908`; Parsec keeps the A2000
  display alive — do not switch displays. Host server terminal: closing it is how the server restarts.
- The GPU host runs SPT 4.1.6 / EFT 0.16.9.5-40743 for the documented 20/21 September acceptance.
  The source server dependency pin still targets 4.1.5; the vanilla test bed was not verified this pass.
- The user's `maschine-ArenaMapDebug.dll` (EQUATOR DEBUG button, penalty-free disconnect) is outside this
  repo and outside version control; host tests rely on it.
