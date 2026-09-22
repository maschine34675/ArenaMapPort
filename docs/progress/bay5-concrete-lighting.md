# Bay5 exterior ground visibility — 2026-09-22

## Diagnosis

The user accepted the much lower reflections, but reported unnaturally dark exterior ground
on Bay5. Branch `feature/bay5-concrete-lighting` starts at `cfbbf16`.

The yard at approximately (56,-3.9,16.5) reports Concrete on its ballistic collider. Its visible
road triangles use `c0cee7841b421fb1` / `Bay5_road_asphalt_01`, slot 4 of `Bay5_floor_LOD0`,
shader `Custom/Vert Paint Shader Solid`. Impact type does not identify the visible material.
Source triangle sampling, vertex colors and albedo textures were valid; no missing/black-texture
import defect was established. The shadow-only mesh is an authored shadow caster, not an
opaque overlay. This shader is outside the p0/Reflective specular-cap family.

Whole-map ambient 1 → 2 → 4 experiments lifted other objects as well, excessively brightening
weapons/walls at 4. Rain removal did not resolve the target. These experiments were reverted:
`AmbientIntensity=1`, `KeepFillLight=false`, `FillLightIntensity=0.2`, LightingLab `RainOff=false`.
The correction improves ground balance under donor lighting; it does not establish exact
original-Arena parity or claim defective source data.

## Implemented control

F12 → maschine-ArenaMapPort → **Lighting (arena_bay5)** → **VertexGroundDiffuseMultiplier**.
Default **1**, range **0.5–4**, live. **2 is saved on the local test installation** following A/B.
Other maps/installations retain the neutral default; no config migration.

- Exact shader `Custom/Vert Paint Shader Solid` in owned map scenes only. Bay5 captures three
  source materials: `be005d9cd5d9384e`, `c0cee7841b421fb1`, `04ab704414ce3b26`. This includes
  layered ground beyond one road, not a concrete-only selection.
- Multiply RGB of `_Color0/1/2` from captured originals. Alpha, textures, blend weights,
  smoothness, specular/Fresnel, reflection and wetness inputs stay unchanged.
- Absolute original × setting prevents cumulative scaling. Existing indoor clones rebuild
  through their established lifecycle; derived clones are excluded from baseline capture.
- Already-real cached shaders participate on later raids. Scripts unload conditionally
  restores still-owned tints and removes map state; other controllers' later changes are respected.
- No new adjustment to weapons or unrelated shader families. No geometry, NavMesh, bundle,
  server or audio changes.

F10 now emits numbered parts with at most 1,400 payload characters. The previous giant entry
exceeded UiBridge's 2,000-character entry limit, concealing later material slots. Full text is
retained without splitting CRLF/surrogate pairs. Individual getter failures no longer abort
remaining materials. This is a diagnostic output change, not a new scan/rendering change.

## Build, deployment and native evidence

Release build: zero warnings/errors. Client **386,560 bytes**, SHA-256
`C02C98D130616F9CA96ECD45870ED193A2852B2C49C318FA30F507EDC330ABDC`.
Sanctioned deployment: `deploy-to-spt.ps1 -SptRoot D:/SPT41 -Confirm`.
All 72 installed files match staging; only the client differs from the immutable pre-task
baseline. The other 71 files and separate DynamicMaps pack remain unchanged.

Fresh EFT process **09cca5c0**, started **19:08:07 CEST**, SPT 4.1.6 / EFT 0.16.9.5-40743 /
Unity 2022.3.43f1, UiBridge 0.6.1. All 23 maschine plugins matched VM, host disk and loaded code.
Bay5 tests use ordinary PMC raids with bot amount None; Equator uses the DEBUG shortcut.

### Same-view comparisons

Yard (56,-3.9,16.5), yaw 243°, pitch 10°/45°; north lane (120.085,-3.8574,57.7943),
yaw 225°, pitch 15°; Hangar control (129.73,-3.8574,7.73), yaw 294.5°, pitch 12°.

At 2, near-black ground becomes visibly textured dark gray. Weapons/walls remain substantially
similar, subject to normal time/sway differences. Authored wet patches remain and also brighten
with the tint. Unchanged reflection inputs do not guarantee identical perceived highlight
contrast. User preference remains a visual check; this does not repair every dark exterior surface.

F10 inspections 1/2/3 (19:12:45 / 19:13:32 / 19:14:12) each returned all 18 parts:

| Setting | Layer 0 RGB | Layer 1 RGB | Layer 2 RGB | Alpha |
|---|---|---|---|---|
| 1 | 0.755 each | 1.000 each | 0.828 each | 1 each |
| 2 | 1.509 each | 2.000 each | 1.656 each | 1 each |
| 1 again | 0.755 each | 1.000 each | 0.828 each | 1 each |

Precision is three decimals. All other road fields match, including textures, smoothness
0.979/0.945/0.904, specular 0/0/0 and reflection strength 0. Live updates report nine tints
in three materials. Existing p0 cap reapplication still reports the 30% ceiling.

At **19:16:33.527**, Bay5 extraction released three materials/restored all nine tints.
The native audio graph stopped/released 24/24 with zero failures. Equator loaded at neutral
multiplier 1 without matching ground-material capture; its spawn view showed no obvious
regression. Equator South Gate return completed. Bay5 revisit at **19:26:51** captured exactly
three materials again at saved multiplier 2. Inspection 4 returned all 18 parts and reproduced
the first visit's factor-2 RGB and all unchanged road fields, proving no cumulative scaling.

Local ignored screenshots: `analysis/screenshots/bay5-concrete-20260922/`.
The clearest pair is `yard-steep-diffuse-1.png` / `yard-steep-diffuse-2.png`; grazing-angle,
north-lane and Hangar views supplement it. `yard-revisit-diffuse-2.png` records the second raid.
Full receipts, build/deployment logs and immutable baseline: `work/verify/bay5-concrete/`.
Independent source review covered ownership, shader/map scope, alpha/reflection preservation,
cached-material revisit, cleanup and bounded F10 output. Native receipts independently verify
four complete inspections, original restoration and invariant road fields.

### Final return and diagnostics

The second normal Bay5 extraction stopped/released 24/24 audio players with zero failures at
19:31:02.489. At **19:31:07.691**, GroundDiffuse again released three materials/restored nine
tints. MainMenu returned. Final raid health was 440/440, ammunition 37+1 unchanged; no shots
were fired during this visual test. All 23 loaded plugin identities still matched after the
three-map sequence. Bay5 multiplier 2, Equator 1 and Bay5 RainOff=false were read back.

The full retained log through #4207 had no GroundDiffuse/native-audio lifecycle warning/error.
The error-query positive control returned 13 existing entries: microphone initialization and
three unsupported `_MainTex` getter messages per F10 inspection. Full inspection output survived
those getter messages. This session is not described as globally error-free.

EFT was exited through the normal menu; UiBridge confirmed it unreachable for five seconds.
The GPU host is free for the user's next visual test.

## User audio feedback and follow-up

The user listened: impact sounds fit; ambient sounds are present but very quiet. Only the
dark-ground report was explicitly attributed to Bay5, so this is not map-specific subjective
audio acceptance. No audio gain changed in this correction.

The ambient bus is already neutral at 0 dB. Authored gain, spatial falloff and random gaps
affect loudness. Equator base levels include 0.03–0.10; native `GetBaseVolume` subtracts a random
delta, potentially selecting nonpositive water gain (base 0.05, delta 0.089). Bay5 sources
commonly use 0.2–0.25 with spatial falloff and 9–60-second gaps. Multiplication cannot recover
a source whose selected gain is zero.

A separate per-map ambient gain with neutral default and a modest +6 dB audition is a possible
follow-up, with conditional restoration. A parent-bus adjustment affects all sources on that
route, not exclusively imported clips. Combat/master gain and original random activation
remain separate decisions. Glass remains deferred by the user's earlier choice.
