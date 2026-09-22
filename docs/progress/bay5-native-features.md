# Bay5 original hit materials and harbor ambience

## 2026-09-22 — source-backed implementation

The user accepted the covered-floor reflection repair and requested further useful Arena
features. This change restores original ballistic material data and harbor ambience to Bay5.
Breakable glass was explicitly deferred after source inspection found all 147 original
WindowBreaker owners inactive (143 directly, four through an inactive ancestor). No pane is
activated and no WindowBreaker component is authored by this change. Glass material classification
on existing colliders is independent of breakable glass.

### Material response

The extractor reads every original BallisticCollider in the nine accepted Bay5 content scenes.
All 6,322 components retain their original owner, local and ancestor transforms, active states,
enabled flags and six penetration/ricochet/fragmentation/deviation values. Canonical material and
preset names map to the installed SPT tables; Arena numeric enum IDs are not assumed compatible.
The original and target 47-preset tables match byte for byte, while 37 individual overrides remain
individual overrides. Static target colliders use `HitType.Default` and `NetId=0`; source role,
network and surface IDs remain provenance. Native target material association supplies surface sound.

| Original scene | Ballistic components |
|---|---:|
| Main | 3,541 |
| Light | 44 |
| DesignStuff | 5 |
| TeamFight | 429 |
| TeamFight_DesignStuff | 65 |
| DEPOT | 645 |
| WAREHOUSE | 541 |
| HANGAR | 159 |
| Background | 893 |

These are source component counts, not 6,322 additional active obstacles: 5,535 owners are active
through their hierarchy, and 5,330 have active own non-trigger physics. The new native component
sits beside the existing physics data. EFT also resolves the direct parent for the existing runtime
ballistic twins. The optional `Combat / LogMaterialHits` probe observes the native projectile's
selected collider and resulting state; it never reruns random penetration/ricochet calculations.

An independent package comparison found all 59,422 original scene objects unchanged after removing
only the added ballistic components. All 5,309 original mesh/material/texture files are byte-identical
to the accepted background package. The geometry and source collider flags are unchanged.

### Harbor ambience

The original Sound scene is `level188`. The exported subset contains 17 ordinary ambient players,
six seasonal players and one loop player, their two groups, original shoreline spline/controller/
calculator/emitter, 13 SoundBanks and 106 decoded PCM16 clips (54,026,220 WAV bytes). Every timing,
gain, pitch, spatial curve, seasonal dictionary and ordered bank link comes from source data.
The shoreline retains 25 points, all 58 stored modes, resolution 80, radius 75 and blend speed 3.

The owned `ArenaMapPort_Bay5NativeAudio` hierarchy adds 31 GameObjects/Transforms and 24 AudioSources
inside the existing Scripts scene. Its own inactive root prevents native player initialization
until the raid has started and the real listener, season and mixers exist. Native players use the
target `CommonAmbOutEffects` route; shoreline water uses `CommonAmbOutBypass`. Missing seasonal banks
remain silent. There is no invented seasonal fallback, Master-mixer fallback, Arena announcer,
crowd or match-event subsystem. Full room/portal acoustics are outside this change.

The owned driver forwards native shoreline Tick/LateTick and stops players when the raid stops.
It destroys only its hierarchy and generated EmptyClips. Original bundle-owned clips and banks
retain bundle lifetime. Native calculator destruction completes its jobs and disposes its arrays.
`Audio (arena_bay5) / PlaybackDiagnostics` and F10 expose actual clips, advancing sample positions,
mixer routing, gain, distance, virtualization and shoreline movement for host verification.

### Cubemap color space

The previous importer forced every cubemap to sRGB. Three original Linear cubemaps were therefore
built with the wrong flag: `b093fe961644b515` (Cube), `ca8b576aa447c88b` (airport_cubemap) and
`492aa7b57d46f0b6` (lab_cubemap_blurred). The importer now honors original Linear/sRGB metadata and
rejects unknown values. A built-asset check reads all 21 in-scope cubemaps. This is a sampling
correction, not a new reflection-strength preset; the accepted Bay5 cap and floor controls remain.

### Independent verification before build

The native validator imports neither exporter as its oracle. It independently decodes original
ballistic bytes and re-encodes all 43 audio MonoBehaviour payloads, checks target preset tables,
re-resolves source references and freshly decodes every original PCM clip. The actual candidate
passes this complete source-to-package comparison, including exact float32 payload storage.
Negative controls exercise wrong owners, flags, values, script assemblies, curves, graph links,
seasonal dictionaries and same-length altered PCM. Old packages without this optional feature
contract retain their prior behavior; incomplete native contracts fail closed.

All source-derived assets remain ignored under work/dist/Unity import paths. Source inspection and
local use do not establish redistribution rights for proprietary Arena audio or map assets.

Build, deployment and native acceptance will be recorded separately below after execution.

### Unity curve integration correction

The first Unity import exposed a real serialization mismatch: native components are wrapped in
`MonoBehaviour` by EditorJsonUtility. Sending a bare curve field silently left the native default
curve intact, and the independent roundtrip guard stopped authoring. The importer now edits the
actual existing envelope, supplies native curve/key serialization versions and verifies every
source key plus all three curve header fields. A separate real Unity probe passed all 48 curves.
The failed build was discarded; it was never staged or deployed. The complete successful build
must still pass the independent bundle-level curve comparison.

Regression checks at this point: 152 Python tests, eight PowerShell log-scan cases and 348 .NET
tests passed. The client Release build had zero warnings/errors. No server code changed. Rebuilding
the unchanged server with its accepted informational revision `2ba4d71` reproduced the accepted
server DLL byte for byte (`EBFAD3424224693656FCFCD55431B859E0F9506BE933B32799F4981A0D97103A`),
avoiding an unrelated server binary update caused solely by the current branch revision.

### Exterior audio bus lifecycle

Native `BetterAudio.ResetMasterValues` sets `AmbientOutVolume` to -80 dB. Raid fade-in restores
Main/InGame only; ordinarily `EnvironmentSoundBlendSystem` and `AmbientSoundBlender` open the
exterior ambient bus. Merely starting the restored players would therefore leave them muted.
Original Bay5 `level188:1179` references `sharedassets39.assets:101`
(`AmbientOutMixerBlendParameters`); its exterior curve endpoint is (1,1), or neutral 0 dB.
Actual source and target mixer snapshots both use this endpoint. Both selected effects/bypass
groups descend from AmbientOut, so shoreline water needs the same parent bus.

The Bay5 driver acquires only this 0 dB parent gain while its owned graph runs. It retains the
previous value and restores it only if the same mixer still contains the value it set; there is
no repeated enforcement against native controllers or other mods. Environment snapshots,
headphone processing, low/high-pass filters and effect sends remain under native ownership.
The original blender's extra source fields differ from SPT's ABI and are not guessed or imported.
This is an explicit target-runtime adaptation, not a complete room/portal acoustic port.
Diagnostics additionally observe listener volume/pause, Main/InGame/AmbientOut/World dB values,
and each active AudioSource's actual output RMS, alongside clip/sample/movement evidence.

### Built native data proof — 2026-09-22

The clean Unity run `unity_build_all_20260922_072422.log` completed successfully. It retained
22,190 NavMesh vertices / 10,248 triangles and authored the complete audio graph in Scripts.
After the mandatory MonoScript identity patch, the parent independently compared the actual
scene bundle against the original source and candidate package. This passed with
`NATIVE_SOURCE_AND_BUILT_DATA_PASSED` (`native-built-parent.log`): all ballistic fields/owners,
native script identities, audio hierarchy/references/curves and all decoded PCM samples agree.
This proves the built data; native playback and real projectile behavior are separate checks.

### First native raid and shoreline integration correction

Process `86f0faf9` (08:05:40 CEST) loaded the candidate client with matching VM/host/loaded
identities. At 08:10:18, a real projectile penetrated original WoodThin plywood, additional
wood, Cardboard and Plastic, then stopped on Concrete. At 08:10:55 another stopped on the
original MetalThick fuel tank. Every logged hit selected its source-backed native collider
with the expected values and `defaultFallback=False`.

All 24 ambient sources initialized; Summer started 23 and correctly left one seasonal bank
silent. Clips and sample counters advanced, both native mixer routes bound, all relevant bus
gains were 0 dB and nonzero source output was measured. The first host run exposed a separate
SPT shoreline defect: its broad-range radius is measured around the spline, while distance is
tested around the emitter object's original transform. Bay5's original origins differ by
215.34 m. Yard/Hangar were wrongly outside that broad sphere (229.17/237.58 m versus radius
210.11 m), although their nearest sampled shoreline was only 55.39/28.48 m away, inside the
original 75 m calculation range. Thus the water clip played at its original static position.

After native Init, the owned driver now aligns that one private range-origin cache with the
spline transform. It verifies the exact target field, exactly one owned calculator and both
references before changing the cache. Source geometry, fine radius, jobs and attenuation stay
native. Independent review confirmed the geometric/lifetime reasoning. This is a client-only
runtime adaptation; the already validated bundles are unchanged. The first raid's normal South
exit stopped all 24 players and released 24 generated EmptyClips with zero cleanup failures
(08:14:58). Final acceptance requires a fresh process with this correction.

Process `cc3d7485` (08:18:22) confirmed the range correction: after moving, water was emitted
55.65 m from the yard listener (RMS 0.000010), then 28.51 m from the Hangar listener
(RMS 0.000276); the source moved 73.58 m along the native calculated positions. Native material
hits were repeated successfully. Its South exit again stopped/released 24/24 with zero failures
at 08:25:36. A surviving initial-position defect was visible while standing still at spawn:
the stock first result adds its direction to zero `_lastPos`, putting the emitter at the wrong
position until the listener first moves.

The driver now also primes that first native calculation synchronously: schedule with the real
listener, seed the private previous-position field, then consume the native result while the
first-calculation flag remains true. Both field types/state are verified. Source-range exclusions
and a target equal to the previous position defer to native movement; no per-frame reflection,
new audio positioning algorithm or altered source attenuation is introduced. Independent review
checked scheduling order, initialization and cleanup. Final native acceptance follows below.

## Final acceptance — 2026-09-22, 08:29–08:44 CEST

Final fresh process `90bc532c`, started 08:29:03, reported all 25 maschine plugins identical
between VM build, host disk and loaded code. The accepted client SHA-256 is
`24F9753F97D160CD59380EE2442C7EF15A233A5C2350A7ADFC660D68BB1AB015`;
Bay5 scenes SHA-256 is `3957E836C63C0F76AD8433DDC2236706C76E4E36893A945AA98B9A382E58E261`.
The sanctioned deploy copied 72 artifacts; exact installed/staged comparison passed. Only
the client and Bay5 scenes differ from the pre-task baseline. The other 70 files, including
server, every other map, Bay5 NavMesh/preset/base/shader sidecar, are byte-identical.

### Real material hits

Native projectile logs on this final client verified:

| Time | Native material | Preset | Six original values | Result |
|---|---|---|---|---|
| 08:32:34 | WoodThin | 27 | 3 / .68 / .16 / .08 / .17 / .025 | Deviation/penetration hits through original wood |
| 08:33:11 | MetalThick | 17 | 69 / .45 / .34 / .13 / .22 / .031 | StopHit on the Hangar fuel tank |
| 08:33:31 | Concrete | -900 | 100 / 0 / .41 / .16 / .23 / .037 | StopHit on Hangar_01_BALLISTIC_concrete |

All three selected the original native collider with `defaultFallback=False`, `HitType.Default`
and `NetId=0`. Additional wood/plastic/cardboard hits were observed across the test iterations.
Not every map collider originally has ballistic metadata: unchanged ballistic twins can still
use EFT's default body. No claim of universal material coverage or statistical penetration
probability measurement is made from these few real shots.

### Audio, lifecycle and control map

- At 08:32:14 the first Bay5 load initialized exactly 24 players; Summer started 23 and left
  one missing seasonal bank silent. Before movement, listener (54.23,-2.34,53.65) received
  shoreline emitter (64.05,-3.61,109.84), avoiding the old world-origin offset.
- Yard water measured distance 55.55–55.64 m, advancing samples and RMS 0.000009–0.000014.
  At Hangar, water moved about 73.71 m to (156.60,-3.61,-1.69), distance 28.78 m,
  RMS 0.000218; a later sample was 0.000256. A seagull source on CommonAmbOutEffects had
  RMS 0.000014. Water used CommonAmbOutBypass. Listener gain was 1, pause false and
  Main/InGame/AmbientOut/World were all 0 dB.
- South exfil stopped all 24 players and released 24 generated EmptyClips at 08:34:12,
  with zero cleanup failures. Reflection teardown restored 642 renderers / 1,008 clones.
- Equator Debug then started normally. Its NativeAudio log filter was empty during the
  Equator interval; its new surface path remained disabled and existing reflection strength
  remained 0.3051643. The debug disconnect returned to MainMenu; teardown restored its
  expected 607 renderers / 1,103 clones.
- Bay5 revisit at 08:39:19 again initialized exactly 24 players, not 48. A different stationary
  spawn immediately received emitter (158.67,-3.61,71.01), distance 40.97 m. The water loop
  advanced from sample 653759 to 1081990 with RMS 0.000151 / 0.000110, before any movement.
  Ordinary random ambient players subsequently selected their clips as expected.
- Bay5's ordinary disconnect displays the normal CONFIRM LEAVE screen: ArenaMapDebug's
  bypass is scoped to debug-started sessions. That dialog was cancelled, gameplay resumed,
  and the South exfil completed at 08:42:41. Cleanup again stopped/released 24/24 with zero
  failures and restored 642 renderers / 1,008 clones. No Bay5-disconnect bypass is claimed.
- Final PMC health in the menu was 440/440. Shots consumed ammunition normally; this is
  health preservation, not a byte-identical profile claim. Both new diagnostic keys were
  reset to false. EFT exited normally around 08:44, with the bridge confirmed gone.

The final process had no ArenaMapPort warnings/errors and no matching audio/spline/native-array
or NullReference errors. F10 can still produce the existing Unity `_MainTex` diagnostic errors
when it inspects shadow/dust shaders without that property; that inspector noise is unrelated
to source playback and was not hidden from the log.

**Audio evidence limit:** Windows CABLE Input remained muted throughout. Source PCM, signal
generation, sample progress, native routing, spatial movement and teardown are proven; no human
hearing test, subjective loudness balance or complete room/portal acoustics is claimed.

### Reflection comparison and verification receipts

All 21 built cubemaps passed source color-space comparison; exactly the three previously wrong
Linear flags were corrected. The same yard and Hangar viewpoints were compared visually against
the accepted pre-task scene. The harbor background, ordinary surfaces and accepted floor repair
remain intact; authored puddles still reflect strongly. This is not a claim that every reflection
became weaker. Small camera/weather/animation differences prevent pixel-identical comparison.
Bay5 cap 0.2957746, covered-floor reflection 0.20, smoothness 0.50 and repair=true were preserved.

Local ignored screenshots:

- `analysis/screenshots/bay5-native-yard-before.png` and `bay5-native-yard-after.png`.
- `analysis/screenshots/bay5-native-hangar-before.png` and `bay5-native-hangar-after.png`.
- `analysis/screenshots/bay5-native-equator-control.png`.

Local evidence under `D:/.unlazy/bay5-native-features/`: `build-clean.log` (successful Unity,
two reproducible full extractions and all validators), `native-built-parent.log`,
`geometry-unchanged.json`, `distribution-installed.json`, `native-observations.json`,
`root-oracles-final.log` and `root-install-final.log`. Final client Release build passed with
zero warnings/errors. Regression totals remain 152 Python tests, 348 .NET tests and eight
PowerShell log-scan cases; independent review also covered the final shoreline initialization.
All game-derived assets and local evidence remain outside git. Glass remains explicitly deferred.
