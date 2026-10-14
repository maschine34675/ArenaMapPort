# Rain playback and wall-adjacent chemlights — 2026-10-09

## Cause and implementation

Equator rendered rain without the separate native precipitation playback system.
The same omission affected Bay5. GPU-2 baseline on Equator confirmed rain=1/High,
zero AmbientAudioSystem/PrecipitationAmbientBlender components, and an already
loaded native PrecipitationSoundStorageSO. Increasing scenery ambience cannot
repair this missing sound bed.

WeatherRig harvests the Weather root from Reserve_Base_Scripts. Native rain audio
instead belongs to Reserve_Sound's two precipitation blenders. The repair uses
the already loaded original bank, with a native EasyAssets fallback to
`assets/content/audio/data/soundcontainers/precipitationsoundstorageso.bundle`.
No new sound asset, Sound scene or global ambient manager is introduced.

`PrecipitationAudio` starts four owned 2D looping sources only after the raid's
audio reset/Started state. It follows actual rain intensity, native seasonal
clips/multipliers and the current acoustic room. Interior room level/range and
seasonal clip overrides are supported. EmptyAudioRoom remains Outdoor even
where the separate gameplay IndoorTrigger still calls the player Indoor.
Original PrecipitationAmbientIn/Out mixer routes retain native hearing protection
and master audio. Smooth clip and room changes avoid sharp starts/stops.

New live controls in `Audio (arena_equator)` and `Audio (arena_bay5)`:
`RainVolume=1`, range 0–1. This is precipitation only; 0 silences it. The native
bank has no winter indoor snow clip; missing seasonal clips remain silent.
Portal-dependent exterior rain bleed into interiors is outside this repair;
gameplay propagation and imported scenery/room reverb remain as before.

`AmbientBusLease` coordinates the existing scenery, room-tone and new rain
owners. Native reset values (-80 dB) open to their normal neutral endpoint;
the last owner restores the previous value. Foreign non-neutral writes win.
Sources stop on exit, unload, device reset or a foreign ambient manager taking
over. An asynchronous bank completion cannot create sources for a departed raid.
F10 logs actual rain, room, selected clips, native bus gain, sample progression,
source output RMS and the combined listener RMS.

## Chemlights

Eight existing accents move toward solid wall/cover edges. The green van-side
accent already has about 18 cm clearance and stays. The nine colors/count,
3 m range, shadow resolution and night-only ownership remain unchanged.

| Accent | X | Z | Yaw |
|---|---:|---:|---:|
| south-west | -17 | -7.72 | -76 |
| south-east | 16.95 | -8.42 | 0 |
| north-west | -18 | 14 | 0 |
| north-east | 21 | 14.5 | 90 |
| west-lane | -21.8 | -16.8 | 90 |
| atrium-south | 3.1 | -13.6 | -30 |
| west-cover | -12.5 | 4 | 10 |
| east-cover | 11.47 | -4 | 90 |
| techlight | 8.3 | 34.12 | 0 |

The accepted v5 scene matrices/components were independently verified, and all
135 padded model-footprint samples clear raised render geometry and non-trigger
mesh/box colliders. This is source evidence; native screenshots are separate.
Most center-to-face clearances are 26–31 cm. Source evidence and rejected clipping
candidates: ignored `work/equator-rain-accents/placement-audit/`.

Native review rejected the first north-west candidate (-19.9,16.5): it cleared
the floor but sat behind the sandbag line, outside the accessible main NavMesh.
The final (-18,14), yaw 0 sits 21 cm in front of its south-facing end. A second
source pass verifies all 135 samples for the final layout, and GPU-2 confirms
the magenta body and pool are visible from the reachable main-floor approach.

The brightness slider keeps its existing linear meaning and range. Its default
becomes 1.6 (60% brighter body emission and light pool). Hidden
`AccentDefaultsRevision=1` migrates only the exact former default 1 once;
disabled/custom values are retained. Other lighting and PostFX values are untouched.

## Verification

Release client builds without warnings/errors. The Release suite passes 577
tests, including nine new shared-bus lifetime/failure/foreign-writer checks.
The distribution validator passes: 14 maps, 42 bundles, 14 sidecars. No scene
bundle or accepted source package was rebuilt.

### GPU-2 native sequence

EFT 0.16.9.5, SPT 4.1.6, Unity 2022.3.43f1. Candidate process `80a69452`
(21:22:46 start) ran ordinary botless Equator 00:00/Rain. The final process
`f4e0303e` (21:37:33 start) loaded the corrected north-west placement; gpu-run
verified 20/20 plugin identities against the host disk and this VM before play.

- Equator High rain: four sources ready only after raid start. Indoor room 19
  selected `amb_rain_indoor_strong`; final-build source RMS 0.013622, volume 1,
  original PrecipitationAmbientIn route, both parent buses 0 dB.
- Candidate live RainVolume 1 -> 0 -> 1: at zero both source levels/RMS were
  zero; restored indoor source RMS 0.018827. Outside at (-11.7,48.6), EmptyAudioRoom
  ID -1 correctly selected `amb_rain_outdoor_strong`, native seasonal volume
  0.5, source RMS 0.018459. The separate gameplay IndoorTrigger did not override
  the acoustic room. Repeated probes were required after the output-data warmup.
- All nine placements visually checked in Night/Rain, including the corrected
  magenta north-west site in the final process. Live strength 1.6 -> 0 removed
  all nine bodies/lights and their root; restoring 1.6 recreated exactly nine.
- Both ordinary Equator raids ended by normal extraction. Precipitation logged
  four sources and both bus leases released before the next map.
- Bay5 then ran with actual dynamic backend rain 0.104 -> 0.107 / Low. Native
  OutdoorCommon room 14 selected `amb_rain_outdoor_weak`, volume 0.6, source RMS
  0.001134, listener RMS 0.000902. Weather was not forced or edited for this test.
- The following Equator 12:00/Clear raid reported rain 0/None, four stopped
  sources with empty clips/zero levels and no night-neon root, strips or lights.
  The same north-west screenshot position confirmed no remaining chemlight.
- All three final-process raids logged precipitation cleanup. Final MainMenu
  still reports 20/20 matching loaded plugin identities. The complete 4,928-line
  log contains one previously documented inactive MenuScreen coroutine error
  during the debug exit; no precipitation, accent or shared-bus error. Five
  unrelated long stored lines were truncated by UiBridge at 2,000 characters.

These are native playback, signal and routing checks, not a listening assessment
of the final balance. No audio recording was auditioned. Winter, device hot-swap
and third-party ambient takeover were not exercised in the game.

Screenshots plus matching player/settings JSON are under the ignored
`analysis/screenshots/equator-rain-accents-2026-10-09/`; `after-<accent>.png`
contains the nine reviewed views. `before-south-east.png` and
`after-south-east.png` provide a representative comparison. Raw native log and
configuration evidence lives under `work/equator-rain-accents/native/`.

### Deployment and saved settings

Both `D:/SPT41` and `D:/SPT41vanilla` received the final DLL pair through the
sanctioned deployment script, dry run followed by `-DllOnly -Confirm`. No
bundle, map asset, profile or config file was copied. SHA256:

- Client: `1817D2FBD5578D60785E85FBD6F941B5B96017EC1C46B2EE27A99EDF2FEDC38F`
- Server: `5D2AD68B07110B9DAFCA07B3591ED2F93266273537D4CBF741B2E38234BC0F2C`

The host retains all 888 previous assignments; the only changed value is the
intended AccentLightIntensity 1 -> 1.6. Three additions are the two RainVolume
entries (both 1) and AccentDefaultsRevision=1. All 804 vanilla assignments are
unchanged on disk. Vanilla receives the same selective migration on client
restart. All temporary live test edits were restored. GPU-2 remains at MainMenu;
the user's vanilla client/server need restarting to load the updated DLLs.

## Follow-up after user listening test — 2026-10-09

The user still heard no Equator rain. The source-only signal checks above missed
an unexposed -80 dB child gain in the effective mixer snapshot. This acceptance
is superseded by [the rain-output repair and native A/B comparison](equator-rain-output.md).
Indoor rain now uses the effect-free immediate AmbientIn parent while retaining
all downstream processing; outdoor routing and the chemlight work remain.

## 1 Hz hitch on every ported map: the precipitation driver's foreign-owner poll (found and fixed 2026-10-14)

**Symptom (user, 2026-10-14):** on the ported maps the picture freezes very briefly every second (the user guessed
half a second), standing still and moving; TAA smears afterwards. Two recordings from the user's PC (AirPit, 2560x1440,
60 fps capture): `D:\SPT41\Development\Escape From Tarkov 2026.10.14 - 15.47.26.01.mp4` (14 s, still) and
`... - 16.12.10.01.mp4` (8.4 s, moving).

**Video evidence** (`ffmpeg tblend=difference + signalstats` per frame, scratchpad `stutter/`): one duplicated frame
at exactly every 60th frame in both clips - still clip frames 78, 138, 197, 258, 318, 378, 438, 498, 558, 618, 679,
739, 799; moving clip 22, 82, 142, 203, 263, 323, 383, 443 - i.e. a frame that took two capture intervals (>= 33 ms)
once per 1.000 s. No timestamp gaps (constant-rate capture).

**Profiler evidence**: the user had ModProfiler 1.4.0 running; its export of 16:12:29-16:12:59 (AirPit, synced to the
host share as `BepInEx/plugins/maschine-ModProfiler/exports/20261014-161027/modprofiler-20261014-161259.csv`)
lists 30 spikes in 30 s, 1.000-1.005 s apart, 33-36 ms per frame, each with
`maschine-ArenaMapPort Driver.Update 29.1-30.6 ms` on top (`gc=false`). The same on GPU-2 (AirPit, 16:21:58-16:22:28,
headless capture): `Driver.Update max_ms_in_frame 31.1`, 1 call/frame, 0.277 ms/frame average = one 31 ms call per
second at 117 fps; `uibridge_status` `max 35.4 ms` over every 300-frame window while the menu shows 16.7 ms. Every
other ArenaMapPort method stays below 0.2 ms; the next spike source is ArenaMode's `ArenaController.Update` at
8.6-9.6 ms (not this mod).

**Cause**: `PrecipitationAudio.Driver.Update` checked once per second whether a native ambient owner had appeared:
`MonoBehaviourSingleton<AmbientAudioSystem>.Instantiated || Object.FindObjectsOfType<PrecipitationAmbientBlender>()
.Any(...)`. The singleton flag is a field read; `FindObjectsOfType` walks every loaded object - about 30 ms on the
ported scenes (AirPit 55,263 objects, Equator and Bay5 of the same order) - and has done so every second on every
ported map since the driver's introduction (`equator-rain-accents`, 2026-10-09). Vanilla maps never run the driver
(`Volumes.ContainsKey(map.Key)`), which is why only our maps stuttered.

**Fix**: the blender scan runs once per raid (`_blenderScanned`/`_blenderPresent`, reset by `Reset()` and after an
`OnAudioConfigurationChanged` refresh); the per-second check keeps only the singleton flag. Scene components do not
appear out of nowhere, and a late native `AmbientAudioSystem` still trips the flag. AGENTS.md carries the trap
("Scene-wide searches in periodic code").

**Host verification (GPU-2, 2026-10-14):**

- Before (16:19-16:24, build `900917f`, AirPit PMC raid, bots None, ModProfiler 1.4.0 headless capture via
  `uibridge_config_set Capture/Headless`): `uibridge_status` `116-117 fps, mean 8.5 ms, p95 8.3 ms, max 35.4 ms` in
  every 300-frame window; export 16:21:58-16:22:28: `Driver.Update max_ms_in_frame 31.11, 0.277 ms/frame, 1 call/frame`.
- After (16:28-16:33, the fix deployed with `deploy-to-spt -Confirm` + `gpu-run deploy-wait --server --game --force`,
  19/19 plugins VM build = host disk = loaded, same raid recipe): `uibridge_status` `120 fps, mean 8.3 ms, p95 8.3 ms,
  max 8.8 ms` and `max 12.0 ms` in later windows; three 30 s exports (16:31:15, 16:31:45, 16:32:15; 3523-3599 frames,
  117-119 fps): `Driver.Update max_ms_in_frame 0.08-0.18 ms`, `0.0013 / 0.0088 ms/frame` for the two drivers, **no
  1 Hz spike** - the spike list holds only two one-off 284 / 300 ms frames at 16:30:58 and 16:31:53 topped by
  `Tyfon.UIFixes AddTagParsedItemPatch.Postfix (ItemDeserializer.CreateItem)` 2 ms (a profile/item deserialization,
  not this mod) and `ArenaController.Update` (ArenaMode) stays the largest per-frame cost at 8.9-9.8 ms max. Raid left
  via `gpu-run leave`, `Capture/Headless` set back to false. The one-time blender scan still costs its ~30 ms once at
  the first foreign-owner check after the raid starts (inside the first second of play).

**Measurement recipe for the next hitch report:** (1) frame-diff the recording (`ffmpeg -vf
"scale=640:-1,tblend=all_mode=difference,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=diff.txt"`,
duplicated frames = YAVG far below the median; their spacing is the period); (2) `uibridge_status` `max` over 300
frames in the raid versus the menu; (3) ModProfiler: `uibridge_config_set plugin=com.maschine.ModProfiler key="Capture
› Headless" value=true` in the raid (it instruments all mod code once, about 6 s main-thread pause), wait for `CSV
written`, read `exports/<session>/modprofiler-*.meta.json` `spikes[]` (frame, ms, top method) and the CSV's
`max_ms_in_frame` per method from `\192.168.178.109\C$\SPT41\BepInEx\plugins\maschine-ModProfiler\exports`
(PowerShell for the UNC copy); set the key back to false afterwards.
