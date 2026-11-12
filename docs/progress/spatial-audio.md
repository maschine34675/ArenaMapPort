# Native spatial acoustics — Equator and Bay5

## Implementation — 2026-09-28

User accepted Bay5 water and requested the next acoustic step. Branch
`feature/spatial-audio`, based on `12b0363`. This restores native propagation and
wall/room attenuation plus source room-tone clips. Room-dependent mixer reverb
and outdoor ambience blending are separate, still unimplemented systems.

### Source and validation

`tools/extract/extract_spatial_audio.py` decodes the original Sound scenes from
the user's Arena 0.5.4.0 installation: Equator `level56`, Bay5 `level188`.
Private serialized fields are byte-decoded against the actual component layout;
every decoder must consume the complete payload. References, active state,
transforms, trigger ownership, room OBB geometry and reciprocal links are checked.
The independent `spatial_audio_bake.py` reader validates every original baked
route against the source graph, counts, indices and complete file boundaries.

| Source | Equator | Bay5 |
|---|---:|---:|
| All rooms / portals / areas | 23 / 55 / 51 | 44 / 89 / 69 |
| Active rooms / portals / areas | 21 / 50 / 49 | 40 / 85 / 63 |
| Room pairs / complete routes | 210 / 31,676 | 706 / 5,924 |
| Original bake bytes | 1,052,882 | 191,304 |

Inactive originals reuse IDs and remain inactive. Zero-thickness portals are
authored data and valid; zero-volume rooms are rejected. Equator's original
rooms, including the atrium, are all classified indoor. No classification was invented.

New original room tones are `mall_indoor_ambient_tone` (Equator,
`sharedassets56.assets:62`) and `amb_bay5_arena_indoor` (Bay5,
`sharedassets87.assets:74`). Package metadata records source resource-slice and
decoded WAV SHA256 values. Original BSG data stays in ignored `work/` and `dist/`;
this is extraction from the user's installation, not a new asset licence.

Original bake SHA256:

- Equator: `3f7fd1fb5328e1235f24eda4d5ee2f14725e2b49b32de704a5b33de68ca3fa8e`
- Bay5: `7f386cb792aa205e6de3018fc6905525c6596b7dae10b872f9aff3df05994f5b`

### Runtime and packaging

`NativeSpatialAudio` builds an inactive hierarchy in the owned Scripts scene,
wires every portal before activation, creates native location/pool settings and
then lets EFT initialize its real spatial system. EFT backend occlusion tuning
is retained; source room geometry, material categories and transmission data
drive it. Existing SurroundAudio processing remains separate.

Bake and WAV lengths/hashes are checked before use. Missing/rejected data or a
30-second initialization timeout retains the old unoccluded fallback. Caller
cancellation still cancels the raid. Owned groups, clips, sources, native data
and mixer changes are released on unload; cleanup does not clear foreign groups.
Native `RoutesDataCount` is the number of pair ranges, not the total route count.

Room tones start after `GameStatus.Started`: earlier creation gets stopped by
the raid's BetterAudio reset. Bay5 temporarily opens its indoor ambient bus and
restores the previous value only while still owning that value. Existing harbor
and mall ambient players keep their own lifecycle. Source room-tone volume and
fade values are preserved.

Per-map F12: `Audio (<map>) / NativeSpatialAudio=true`; applies next raid.
`Audio diagnostics / SpatialProbe=false` is a silent, pooled remote source for
measuring actual native Combined occlusion and final volume/filter application.
F10 reports listener/source classification and real room-tone playback state.
It does not prove subjective listening quality.

Regenerate with `python tools/extract/extract_spatial_audio.py`; inputs land in
`work/extract/spatial-audio/<map>/`. Release builds, including DllOnly builds,
stage six files under `BepInEx/plugins/ArenaMapPort/acoustics/`: graph JSON,
original bake and one decoded WAV per map. Existing v5 scene bundles are unchanged.

### First native comparison — SPT-GPU-2, 12:31–12:38 CEST

Process `c7133a5e` initialized exact native counts on both maps. Equator source
room 2 to listener room 4 at 12:32:05 applied volume 0.4022 and low-pass 11,205.6 Hz;
moving into source room 2 at 12:32:29 restored volume 1 and zero acoustic effects.
Bay5 correctly classified indoor room 40 and adjacent outdoor room 37. A remote
source restarted after the raid reset was actively playing at 12:35:41, volume
0.9125 across that boundary. These are amplitude factors, not perceived loudness.

This first pass exposed the early source-reset issue; startup was moved after
raid start for both room tones and the diagnostic source. Both graph unloads
returned native Initialized to false and group count to zero. Final-build
verification is recorded below after the restart.

### Runtime-created clip reset — 12:51–12:58 CEST

The next pass (`a70e9fb2`, exact build/disk/loaded identity confirmed) showed that
moving playback after raid start alone was insufficient. Native room tone sources
had their correct clip and bus, but actual `AudioSource.isPlaying=false` and zero
samples on both maps. `LocalGame` calls `AudioUtils.ResetAudioBuffer`, which calls
`AudioSettings.Reset`; Unity discards script-created clip data at that point.
The room-tone WAVs must therefore be decoded into Unity clips **after** the reset.
Source validation still occurs before spatial initialization. Native rooms retain
the same `RoomAmbientData` object, so assigning its newly created clip before the
controller starts preserves native behavior. An audio-configuration event marks
owned room tones for main-thread release/recreation as well. PCM upload is checked.

Unity documents this behavior in its [2022.3 AudioSettings manual](https://docs.unity3d.com/2022.3/Documentation/Manual/class-AudioSettings.html).
The pre-existing ambient clips are serialized bundle assets, not procedural clips;
their authored two-stage delays mean an early snapshot with no playing clips does
not alone establish an audio failure. Diagnostic source readback now includes
actual AudioSource playback/sample values in addition to BetterSource's state.

## Final build — 2026-09-28, GPU-2 process `a0b19549`

Client SHA256 `2207A864D75DC72ADC8850B63BF111DFC6767800A9F7CF3DA03B860467079098`.
UiBridge confirmed all 23 plugin builds = host disk = loaded code after the
12:59:47 restart. Earlier final-build attempts were not accepted merely from
local disk identity: Syncthing had still delivered a newer DLL after process
startup, so the game was restarted after host-disk identity was confirmed.

### Bay5 — 13:01–13:03 CEST

- Native counts 40 rooms / 85 portals / 706 pairs / 5,924 routes loaded at
  13:01:13.482. Room tones started after reset at 13:01:28.024.
- At `(90.91,-2.9,74.24)`, listener and diagnostic source both occupy room 40,
  IndoorBrick. Actual source playback was true, volume factor 1, propagation,
  obstruction and occlusion all zero. LPF 18,000 Hz, HPF 80 Hz.
- Native `amb_bay5_arena_indoor` was **actually playing**, clip Loaded,
  AmbientIn bus 0 dB, listener unpaused. Sample cursor 263,423 at 13:01:50.041
  and 230,912 after looping at 13:02:05.233 confirm continued playback.
- At `(94.6,-3.9,88.8)`, listener changed to outdoor room 37 and room-tone
  controller cleared its indoor room. Same indoor source remained actually
  playing with volume factor 0.9125 and native filtering.
- At `(130,-3.8,3.7)`, listener room 17 (Hangar, IndoorMetalThin), that same
  source applied factor 0.0987, LPF 2,405.6 Hz, HPF 119.8 Hz. These factors
  are the native acoustic gain, not perceived loudness or distance attenuation.
- Existing harbor seagulls and shoreline loop were also actually playing at
  13:02:22.895; shoreline loop RMS 0.000390, sample cursor advancing.
- No new errors; the pre-existing inactive MenuScreen coroutine message
  occurred when opening the pause menu at 13:02:48.398.

### Equator — 13:04–13:05 CEST

- Bay5 unload 13:03:07.050 returned `Initialized=False`, groups=0. Equator then
  loaded 21 rooms / 50 portals / 210 pairs / 31,676 routes at 13:04:02.937.
- One north-side spawn `(-11.7,0.1,48.6)` lies outside the original acoustic
  volumes and correctly remained Empty Audio Room. Entering the authored room
  at `(-37.13,0.1,12.81)` classified room 2 and started room tones at 13:04:29.303.
  Source coverage is retained, not extended with invented rooms.
- `mall_indoor_ambient_tone` played with Loaded data, 0 dB indoor bus and
  progressing sample cursor: 312,345 at 13:04:36.387; 108,389 after looping
  at 13:04:57.055. The same-room remote source was actually playing with
  volume 1 and zero acoustic effects.
- Moving the listener to `(-5,0.1,-33.5)` changed it to room 4: same source
  applied gain 0.5399, LPF 14,724.7 Hz, HPF 119.7 Hz at 13:04:57.055.
- Existing ambient system: 25 players, 23 initialized, original two inactive
  dogs retained. Six original sources were playing at 13:04:57; nearby indoor
  wind had nonzero measured output. No new errors occurred.
- Diagnostic source disabled and its position restored to zero before return.

### Repeated Bay5 load — 13:06–13:07 CEST

Equator unload at 13:05:51.351 returned Initialized false and groups zero.
Bay5 then reloaded the exact 40/85/706/5,924 native counts at 13:06:26.165.
Room-tone startup occurred at 13:06:35.074. Re-entering room 40 produced Loaded,
actually playing `amb_bay5_arena_indoor`, sample cursor 338,687 at 13:06:58.884,
indoor bus 0 dB and listener unpaused. Probe remained disabled. No errors during
this raid; player alive, 440/440 HP, no bots. This completes the final-build
Bay5 -> Equator -> Bay5 sequence.

### Verification scope

Client Release build: zero warnings/errors. Python pipeline: all 445 discovered
tests passed or used their existing expected skips; the 20 new spatial tests all
passed, including both real local packages. The full distribution validator passed
14 maps / 42 bundles / 14 sidecars and Release DLL identities. Scene bundles and
server code were unchanged by this task. Independent source/runtime reviews found
no remaining necessary changes after the clip-reset correction.

Native acceptance measures actual room/source classification, propagation,
applied gain/filter values, clip playback and repeated teardown. The host's output
device was muted; subjective listening and sound balance remain the user's check.
No acoustic retuning, invented room coverage, full mixer reverb, outdoor ambience
blending or performance benchmark is claimed. A mid-raid audio-device change was
handled in code but not deliberately induced on the shared host.

### Final deployment and handover — 13:10 CEST

Both installations received the verified build through `tools/deploy-to-spt.ps1`:
`D:\SPT41` and `D:\SPT41vanilla`, 97 staged files each. A separate read-only
SHA256 comparison independently matched all 97 destinations in each install
against `dist/`, with no mismatches. Six new acoustic files total 6,542,571 bytes.
Ignored evidence: `work/spatial-audio-research/deploy-{host,vanilla}-final.txt`,
`install-hashes.json`, `final-gates.txt` and original decode/integrity probes.

Last unload at 13:08:01.191: native Initialized false, zero owned groups. GPU-2
is in MainMenu, no raid, control free, all 23 plugin builds/disk/loaded identical;
local menu profile health 440/440. Both NativeSpatialAudio switches true, silent
probe off and position zero. The original 11 changed settings (including saved
lighting and Clear weather) match the pre-test snapshot. No config file was
edited or deployed. Restart the user's game to load the client; server code is
unchanged. Vanilla's own in-game acceptance remains separate from GPU-2 testing.

## Follow-up — room-dependent ambient reverb, 2026-09-28

The separate mixer-reverb item is now implemented and GPU-tested on both maps.
The original 26 parameters are an additive part of the same graph JSON. Equator
needs a scoped ambient routing adaptation because EFT mutes its original hall
return. Actual map-channel tests and the unchanged gameplay scope are documented
in [room-reverb.md](room-reverb.md). Outdoor ambience blending remains separate.

## Follow-up — Iceberg's bake is repaired, 2026-10-30

Iceberg (PLAN-ICEBERG item 11, [iceberg-release.md](iceberg-release.md)) is the first map whose staged `source.audiobakedata` is NOT Arena's file byte
for byte: the strict route check failed on 533 of 17,751 routes that all pass through portal ID 66, which Arena's bake uses for two different
doorways; a pinned, reviewed repair rewrites exactly those 533 positions to the one portal that joins the two rooms (66 to 30; 533 bytes differ) and
records the original and the repaired SHA-256 under `bake_repair` in the graph JSON. Where this document, README text or a release note says
"the original bake", that is true of Equator, Bay5, AirPit and Bowl only; for Iceberg say "Arena's bake with one duplicated portal ID corrected". The
10 mirrored `AudioTriggerArea` volumes of two Iceberg rooms keep their negative scale (`AcousticVolumeRules`).

## Follow-up - Iceberg's route twins: what they do at run time, and why 66 -> 30 is not physically exact, 2026-11-12

Question (user, 2026-11-12): is a fix of Iceberg's room acoustics sensible? SPT-SEB's checker found 510 routes in 153 of 1,003 room pairs of the repaired bake whose portal list equals another route of the same pair, with a different stored cost and distance; Arena's original has none. Result: no change. Facts are separated from inferences below.

**1. What a twin does at run time (EFT 0.16.9.5 decompile, read in full).**
- Facts. `RoomPair.Route.Read` (RoomPair.cs) keeps only the portal IDs, the stored `HeuristicCost` and `TraverseDistance`. `SpatialAudioDataContainer.FinalizeRoutesData` and `FinalizePortalIndicesData` flatten every route of a pair into one array, in file order. `SoundPropagationJobScheduler.Schedule` runs `PropagationJob` once per route. The job takes only `PortalDataLength`, `StartIndex` and `NodeStartIndex` from the route data. `Execute` rebuilds the polyline from the scene's portal geometry (`ComputeClosestPointOnPortal`) and `GetRouteCostAndDistance` computes cost and distance from it. The file's stored cost and distance are not read by this job; the stored `AudioRouteData.HeuristicCost` is unused in the job. `PropagationEffectCalculationJob.Execute` reduces to ONE value per pair, the minimum cost with a 0.01 tie rule and then the minimum distance. It is not a sum.
- So a route with the same portal list computes the same cost and distance as its twin. Nothing is doubled and the minimum is unchanged. The only cost is one extra job index per twin (510 of 17,751, 2.9 percent).
- The route-count cut `num = max(min(350,len), ceil(len*q))` always covers every route, because the largest pair has 180 routes (max_routes_count 180, below 350). No twin is ever skipped or crowds out a distinct route. The node buffer is `maxRoutePortalsCount + 2*maxRoutesCount` and the file's caps (180 / 1,015) are the maxima including the twins, so they fit.
- `ShortestRouteLength` (used only in `GetIsolationFactor` and `InteractiveObjectOccluder`) is the minimum route length. A twin has the same length, so it changes nothing.
- The old `SoundPropagationCalculator`, the only reader of `Route.HeuristicCost` (as a prune), is not referenced anywhere in the decompile. Nothing live reads the stored cost.
- Limit: the job is Burst-compiled. I read its managed source, which is the code Burst compiles; I did not read native code.

**2. What the twins are.**
- Facts. Arena's file has 876 routes with ID 66 and 1,309 with 30; the repaired file has 380 and 1,842. In Arena's original no pair has a repeated portal list. The 533 rewritten positions split into 510 routes that become a twin of an existing route ending in or passing 30, and 23 that have no twin. The twins show the pattern 'route A: ..., 4, 30' and 'route B: ..., 4, 66' with different stored costs and distances. Example, pair 10 (rooms 1 to 11): 72.36 m via 30 and 69.89 m via 66.
- Scene geometry (graph, world transforms). Portal 30 (elevators to boiler, room 11) sits at (33.28, 4.32, -83.09). Portal 66 (elevators to 'boiler (2)', room 24) sits at (29.60, 4.32, -83.09): the same wall, 3.68 m apart, the same size and depth. Room 11's volume centre is at x 33.4, room 24's at x 29.8. Both doorways are real and separate in the scene.
- Measurement. For the 510 twin groups, the difference between the stored distances of the two routes correlates r = 0.98 with the difference in polyline length obtained by moving that hop from portal 30 to the scene's portal 66. Mean absolute error of that prediction is 0.58 m; using portal 30's position for both gives 1.75 m (i.e. no difference). A grid fit of the unknown portal position puts it at (30.1, -82.6), about 0.7 m from portal 66 (rms 0.33 m; rms 0.65 m at portal 66; 2.28 m at portal 30).
- Inference (not proven): in Arena's bake the ID 66 on those 533 hops names the doorway that the scene still calls portal 66, at x about 29.6. The bake saw that doorway as joining elevators and boiler (11), while the shipped scene joins it to 'boiler (2)' (24). The graph check refuses the route because of that room link, not because of the doorway itself. The old bake-time scene is not available.
- So the true defect is the repair target, not duplicates: 66 -> 30 relabels a hop that physically goes through x 29.6 as a hop through x 33.3. It is valid against the graph, and it creates the twins wherever the bake also contained a genuine route through 30.

**3. Decision: leave it.**
- Run-time effect of the twins: none beyond 2.9 percent extra jobs (section 1).
- Run-time effect of the wrong target. Model: my Python port of `PropagationJob` with EFT's default `PropagationSettings` and scene portal data. 157 pairs contain a rewritten route; 10 uniformly random source/listener positions per pair inside the two room volumes, 3 seeds (4,710 samples), comparing the best cost of the repaired file with Arena's original bytes (positive = repaired is more occluded): mean +0.006 to +0.008; difference > 0.01 in 6.0 to 7.3 percent; > 0.05 in 4.4 to 5.4 percent; > 0.2 in 0.6 to 0.9 percent; max +0.33 to +0.40; negative values rare (-0.18 at worst). This is a model estimate (inference), limited to pairs involving the elevators and boiler rooms (15.7 percent of pairs), i.e. well below 1 percent of all pair-position combinations.
- Options weighed.
  - Remove the costlier twin per pair: both twins compute the same cost at run time, so nothing is gained; it breaks the scene's `routes_count` (17,751) and possibly `max_routes_count` (180) pinned against the graph and the exporter (every validator strict), changes pair aggregates, and does not restore the lost doorway.
  - Correct the repair so the 533 positions keep ID 66 (Arena's original bytes, the 'bake_repair' goes away): this is the only option that recovers Arena's run-time geometry and gets rid of the twins. It needs a reviewed, pinned exception in `parse_bake`'s route-walk check (portal 66 additionally joins rooms 27 and 11), and the SEB scene-walk check ('doors that don't join their rooms') then fails for exactly 533 routes (its documented negative control). It changes the released map's acoustics and needs a re-export, a client DllOnly build, validate_distribution, a deploy and a host check.
  - Benefit does not justify that. Revisit only if a listening check in the boiler or elevators area (rooms 11, 24, 27) finds something wrong.

**4. Wording corrections for the docs (for the lead).**
- spatial-audio.md, this follow-up: 'the repaired file keeps 510 routes as portal-list twins of another route of the same pair (153 pairs); EFT's job recomputes cost from the portal list and takes the minimum, so a twin changes nothing; the rewrite moves 533 hops from the doorway at x about 29.6 (scene portal 66) to portal 30 at x 33.3.'
- iceberg-release.md and spt-seb-compatibility.md: the open item 'leave or de-duplicate' is decided: leave.

Not done: no export, no test change, no deploy, no host use. Scratch files are under work/tmp/iceberg-routes/ only.

Lead's check (2026-11-12): `Audio.SpatialSystem/PropagationJob.cs` lines 72-98 compute each route's cost from `StaticPortalData` through
`RoutePortalIndices` and write it to `LowestCosts[index]`; `PropagationEffectCalculationJob.Execute` (lines 24-37) starts from `float.MaxValue` and keeps the
lower cost, with the 0.01 tie rule on distance. Read by the lead as well; it matches section 1. The review of this analysis raised no finding. Decision
recorded: **no change to Iceberg's bake**; the Arena-faithful variant (original bytes plus a pinned portal-66 exception) stays an option on request.
