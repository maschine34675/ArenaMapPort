# Per-room gunshot acoustics (invented data) - 2026-10-15

Status: implemented, builds and unit tests green. Two host measurement rounds on 2026-10-15 (AirPit, GPU-2): the mechanism
works; the first invented table was wrong (a shop rang louder and longer than the hall); the second round measured
alternatives and the defaults are now the measured choice (see "Tuning round"). The table stays live-tunable in the developer
config section `ShotAcousticsLab` for tuning by ear; **the user's listening note is still open**. Default **off** on every map.

## Purpose

A shot in AirPit's 100 m hall should ring differently from a shot inside a small shop. Today it does not: the
gunshot reverb is the same everywhere on the ported maps. `RoomShotAcoustics` feeds the room the listener stands
in (hall, shop, metal body, outdoors) to the Meta XR Audio plugin's one global shoebox room, so each class gets a
different reverb length and colour.

## The data is invented

Neither EFT nor Arena carries any per-room value for gunshot reverb. Checked in the source audit: zero reverb
zones, reverb filters or Meta XR components in all 24 raw Arena levels, and `RoomAmbientData.ReverbPreset` is
empty on all 91 rooms. There is no original to restore. **Every number below is the author's guess**: the four
classes, the 40 m hall threshold, the 50 m cap, every material and every clutter factor. The classes and thresholds
live in one table (`RoomShotAcousticsModel`); the materials and clutter factors are live-tunable developer keys in
the `ShotAcousticsLab` config section (see "Config keys" and "Tuning round").

The first host measurement (see "First host measurement") showed that the first table's hard presets (Concrete,
PlasterOnBrick, CeramicTile: reflection coefficients 0.95 to 0.99) ring far too long in a small shop, several times
louder and longer than in the hall, which is the opposite of the intent. The defaults therefore moved to absorptive
ceilings (AcousticTile) and cluttered walls (shop walls Cardboard, clutter 0.7) as the FIRST tuning attempt. That attempt
is superseded: the second measurement round (see "Tuning round") found the Cardboard shop dead and the hall tail short,
and the defaults are now the measured choice (hall Concrete walls and ceiling, CeramicTile floor, clutter 0.4; shop
GypsumBoard walls, AcousticTile ceiling, CeramicTile floor, clutter 0.7; size cap 50 m). The measured choice is exactly
as invented as the earlier tables: it is the best contrast between hall and shop found by measurement, not original
data, and it is open to tuning by ear. The switch defaults off until the user has judged the result; whether player
mode ships it on is the user's decision.

The table is generic by room type and size, not per map, so it is code and not descriptor/sidecar data
(per-map data belongs in `ArenaMapDescriptor` and `tools/maps`; this table has none).

## Mechanism: why the Meta shoebox and not the mixer

- Every gunshot is a `ReverbSuperSource` (`BetterAudio.AudioSourceGroupType.Gunshots`). Its audible reverb comes
  from Meta-spatialized sources (`MetaSpatialAudioSource` -> `MetaXRAudioSource` with `EnableAcoustics`,
  `ReverbSendDb`, `EarlyReflectionsSendDB` from the Gunshots `AudioGroupPreset`), and there are two routes:
  - Every real weapon shot, the player's and everyone else's, goes through the weapon queue
    (`WeaponSoundPlayer.FireBullet`: `BorrowWeaponAudioQueue(Gunshots)`, `EnableStereo(PointOfView == FirstPerson)`,
    `Enqueue`), then `SuperBetterAudioQueue.Update` -> `SuperAudioSample.PlayOn` -> `ReverbSuperSource.PlayScheduled`.
    There the two helper sources (`_reverbSourceA/B`, stereo acoustic settings) start only when the source is
    NOT spatialized (`EnableReverb && !EnableSpatialization`); otherwise they are switched off and the source's own
    `MetaSpatialAudioSource` (mono acoustic settings) is the Meta path.
  - `BetterAudio.PlayAtPoint` -> `SuperSource.Play` never reaches `PlayScheduled`, so the helper sources never start;
    only the source's own spatializer (mono settings, when `CheckBinauralAllowed` leaves it on) carries Meta
    acoustics. In the decompile only the artillery sound plays the Gunshots group this way (`forceStereo: true`).
  Which of these actually carries audible reverb with the shoebox model is exactly what the three probe buses measure.
- The Unity mixer return for guns is muted in EFT and cannot be driven. The ambient `RoomReverb` chain
  (`room-reverb.md`) only reaches the imported ambience.
- The plugin has one process-global acoustic model (`MetaXRAcousticSettings.Instance.AcousticModel`:
  Automatic -1, None 0, ShoeboxRoom 1, AcousticRayTracing 3). On the host it is `AcousticRayTracing` with zero
  `MetaXRAcousticMap`, zero `MetaXRAudioRoomAcousticProperties`, zero `MetaXRAcousticControlZone` and 436 pooled
  `MetaXRAudioSource`. The ported maps carry no `.xramap` geometry, so ray tracing most likely renders nothing,
  which is why a hall and a shop sound identical.
- `ShoeboxRoom` is driven by ONE `MetaXRAudioRoomAcousticProperties` component. Its private `Update` converts the
  six `MaterialPreset` values to 24 absorption coefficients and calls the native
  `SetAdvancedBoxRoomParameters(width, height, depth, lockPositionToListener, ...)` and `SetRoomClutterFactor`
  every frame while it is enabled. With `lockPositionToListener = true` the box is centred on the listener, so
  one component serves every room. At startup the plugin logs "No Meta XR Audio Room found, setting default
  room" and applies 8 x 3 x 5 m gypsum / acoustic tile / carpet, clutter 0.5, once from a temporary object.

## Class table (all values invented)

Rules in order: Outdoor, Metal, Hall, Shop. Inputs are the live `ListenerCurrentRoom` (`Type`, `IsOutdoor`,
`IsIsolated`, `Bounds.size`); sizes are world X, Y, Z. Every preset edge is clamped to
[2 m, `HallMaxDimension`]. The material and clutter columns are the DEFAULTS (`RoomShotTuning.Default`); every one of
them is a live key in `ShotAcousticsLab`. "Walls" are the four side walls (right, left, front, back) and share one
material; ceiling and floor have their own.

| Class | Rule | Shoebox size | Walls / ceiling / floor | Clutter |
|---|---|---|---|---|
| Outdoor | not isolated and (outdoor flag or an outdoor type bit: OutdoorGeneric 1, OutdoorVehicle 2, OutdoorCommon 64) | none | none: the shipped model stays | - |
| Metal | `IndoorMetalThin` (512) | real size | walls Steel, ceiling Steel, floor Carpet | 0.7 |
| Hall | not outdoor, `max(width, depth) >= 40 m` | real size, capped (default 50 m) | walls Concrete, ceiling Concrete, floor CeramicTile | 0.4 |
| Shop | every other indoor room | real size | walls GypsumBoard, ceiling AcousticTile, floor CeramicTile | 0.7 |

Notes:

- Isolated beats outdoor, the same precedence `RoomReverb` uses for its mixer categories. An isolated room is
  never Outdoor.
- Only `IndoorMetalThin` is Metal. `IndoorMetalThick` (Bay5 `PlantBuldingA_back_indoor`: `room_id` 27, graph node
  id 1250, 9.0 x 2.7 x 10.0 m) is a Shop for now; adding it is one line in `RoomShotAcousticsModel.Classify`.
- Room numbers in this document and in the log are the runtime `ISpatialAudioRoom.ID` = the graph's `room_id`, not the
  graph node `id` (a different, larger number). Grep the host log for `room=27`, not `room=1250`.
- The Hall test reads the real room size, not the capped preset size.
- Equator room 14 (`UpRoof`, 9.9 x 6.5 x 40.7 m) is a Hall by the 40 m rule although it is a corridor; a tuning candidate.
- Material names are `MaterialPreset` value names as strings; `RoomShotAcousticsModelTests` keeps the plugin's list
  as a literal so a typo fails a test, and pins the model's own list (`RoomShotAcousticsModel.MaterialNames`, the
  list the config keys offer) to it.
- The first table gave brick rooms (`IndoorBrick`) PlasterOnBrick walls and every other shop ConcreteBlockPainted.
  That distinction is gone: the shop walls are one lab value, whatever the room type.
- `HallMaxDimension` caps the edges of every indoor class (the first table's behaviour, kept); the name says where
  it matters: halls are the class expected to reach the cap (which rooms exceed 50 m was not re-counted; the classes in the
  census below do not depend on the cap).
- AirPit's spawn room 6 (`Start1`, the passport hall, IndoorBrick 111 x 35 x 89 m) classifies as a Hall by the 40 m rule
  (80 x 34.9 x 80 under the old cap, 50 x 34.9 x 50 now), as the rule intends. Whether the user wants the passport hall
  as a hall is an open question.

Classes of the active rooms in the three graphs (offline, same rules): Equator 19 Shop, 2 Hall. Bay5 15 Outdoor,
18 Metal, 6 Shop, 1 Hall. AirPit 12 Shop, 9 Metal, 2 Hall (Main 111.4 x 43.2 x 96.4 and the passport hall
111.4 x 34.9 x 89.3, both capped on X and Z, to 50 m with the current default, 80 m before), 1 Outdoor.

## Config keys

| Section / key | Default | Meaning |
|---|---|---|
| `Audio (<map key>) / RoomShotAcoustics` (every map in `ArenaMapRegistry.All`) | false | Live switch. Off restores the shipped acoustics. Developer-mode menu only. |
| `ShotAcousticsLab / HallWalls`, `HallCeiling`, `HallFloor` | Concrete, Concrete, CeramicTile | Meta material of the hall's four side walls, ceiling, floor. List of all 36 `MaterialPreset` names, each key's list led by its own default. |
| `ShotAcousticsLab / HallClutter` | 0.4 (range 0..1) | Hall clutter factor. |
| `ShotAcousticsLab / ShopWalls`, `ShopCeiling`, `ShopFloor` | GypsumBoard, AcousticTile, CeramicTile | Same for the shop class. |
| `ShotAcousticsLab / ShopClutter` | 0.7 (range 0..1) | Shop clutter factor. |
| `ShotAcousticsLab / MetalWalls`, `MetalCeiling`, `MetalFloor` | Steel, Steel, Carpet | Same for the metal class. |
| `ShotAcousticsLab / MetalClutter` | 0.7 (range 0..1) | Metal clutter factor. |
| `ShotAcousticsLab / HallMaxDimension` | 50 (range 10..200) | Largest shoebox edge in metres (caps every indoor class). Replaces `Audio diagnostics / RoomShotAcousticsMaxDimension`. |
| `Audio diagnostics / ReverbPulseBus = Gunshots / GunshotsFirstPerson / GunshotsThirdPerson` | - | New values of the existing probe bus (appended last, saved values keep their meaning). |
| `Audio diagnostics / ReverbPulseVolume` | 0.08 | Level of the probe tone (0.01-1). The first host runs at 0.08 had tails at the level of the background audio (baseline 1e-6 to 2e-4 between runs), so the second round measures at 0.8: the host is muted and only the energy ratio matters. Compare runs at one level. |

All binds go through `Plugin.Instance.Settings`. No shipped key changed its default or meaning. Every `ShotAcousticsLab`
key is live: a change re-applies the current room on the next tick. The lab keys are developer-mode keys (F12 with the
Advanced filter); the defaults are the table chosen by the second measurement round (values still invented). BepInEx keeps
a value that is already saved in a `.cfg`: the code defaults apply to a fresh file, a developer `.cfg` that already holds
the keys keeps what it has.

`Audio diagnostics / RoomShotAcousticsMaxDimension` was bound for the first time on 2026-10-15 and never shipped; the
feature is unreleased on `feature/airpit`. It was renamed to `ShotAcousticsLab / HallMaxDimension` and the old bind is
gone entirely (a value saved under the old name in a developer `.cfg` is simply an unread orphan, the new key starts at
its default).

A name hand-edited into the `.cfg` that is not in the list is clamped by BepInEx itself to the FIRST entry of the key's
`AcceptableValueList`, when the file loads and on every set (checked 2026-10-15 against the installed BepInEx core).
With one shared enum-ordered list that first entry was `AcousticTile`, so a typo such as `HallWalls = Concerte`, an
emptied value or a wrong-case name silently turned the walls into a very absorptive material and the model's fallback
never ran. Each of the nine material keys therefore has its own list, built by `RoomShotAcousticsModel.MaterialChoices`:
the key's own default first (hall walls Concrete, shop walls GypsumBoard, shop ceiling AcousticTile, ...), then the other names in enum order.
A typo now lands on that key's default (verified with a scratch program against the installed `BepInEx.dll`: bad name,
empty value, wrong case and `entry.Value = "NotInList"` all give the default; a valid non-default name survives; a bad
value set while the entry is already at the default changes nothing and raises no event). The F12 dropdown shows the
key's default first and the rest in enum order.
The model keeps a second guard for every other path (a tuning built in code, a future key without a list):
`RoomShotAcousticsModel.Resolve` never throws on a bad value, it replaces an unknown material name by the
class default (a clutter factor that is not a number by the class default, one outside 0..1 by the nearest end) and
lists each replacement in `RoomShotPreset.Corrections`; the one Debug line then ends with `class-default-fallback=[...]`.
From the config alone that marker is no longer expected to appear, because the entry clamps first.

## Runtime behaviour

- `RoomShotAcoustics.Tick(state)` runs from `NativeSpatialAudio.RoomToneRunner.Update` next to `RoomReverb.Tick`.
  Gates: `ArenaMapRaid.IsLoaded()`, state `Finished` and not `Released`, key on for `state.MapKey`, BetterAudio
  instantiated, game status Started (after the raid-start audio reset), a process that can be heard
  (`RoomShotAcousticsModel.IsAudioClient(Application.isBatchMode, graphics device is Null)`, decided once) and a listener
  room with `IsValid`. The listener object itself is no gate: EFT creates the `AudioListener` and its
  `AudioListenerConsistencyManager` on every client, headless ones included (`ClientApplication.StartTask`). Whether a
  Fika headless host is really started in batch mode / without graphics is not verified here; if it is not, only the
  parked listener (reset position (0,-1000,0), so `ListenerCurrentRoom` never becomes valid) keeps it inert.
  Vanilla maps and the hideout never reach the Meta types: every Meta call lives in the nested `MetaPlugin` helper
  (no-inlining), covered by a text test.
- Work happens only on a room change, a change of any `ShotAcousticsLab` key or an audio configuration change
  (`RoomShotAcousticsModel.NeedsApply`). Every lab entry's `SettingChanged` bumps a tuning version; `NeedsApply`
  compares it with the version that was applied, so an edit re-applies the CURRENT room on the next `Tick` even when
  the room is unchanged, and several edits between two frames cost one application. The tuning is read from the
  entries on each application (`ReadTuning`) and validated by `RoomShotAcousticsModel.Resolve`. The room is read from
  the live `ISpatialAudioRoom`, not looked up
  in the graph JSON: the graph's `room_id` repeats on inactive source rooms (Equator 14 and 21, Bay5 6, 8, 10, 35),
  while the live room is already the active one and carries the same type, flags and bounds.
- Indoor class: ensure ONE component `ArenaMapPort Shot Acoustics Room` under `state.Root`, set its fields, invoke its
  private `Update` once through `AccessTools` so the native room is current, enable it, save the shipped model once,
  switch to `ShoeboxRoom`. This repair is the only creator of the component (zero existed on the host, and the plugin
  reports more than one itself at startup), so there is no scene search for a foreign one: a `FindObjectsOfType` over
  a ported scene costs about 30 ms on the main thread and would be a one-off hitch at the first indoor frame
  (a text test keeps scene searches out of the file).
- Audio configuration change (`AudioSettings.OnAudioConfigurationChanged`: headphone switch, `AudioSettings.Reset`)
  while a room is applied: the next `Tick` applies the current room again even though it is unchanged, and once the
  repair has changed the model it sends the managed model value to the native side explicitly
  (`MetaXRAcousticNativeInterface.Interface.SetAcousticModel`, the call the plugin's own `ApplyAllSettings` makes at
  startup), because the plugin's setter ignores an unchanged managed value. The change line then ends with
  `model-resent-rc=<native result>`.
- Outdoor class: restore the shipped model, disable the component.
- Debug line per applied change:
  `[ArenaMapPort][ShotAcoustics] <map>: room=<id>/<type> class=<class> dims=<w>x<h>x<d> materials=<r,l,c,f,fr,b> clutter=<c> model=<model>`,
  followed by `class-default-fallback=[<Class>Walls='Bogus'->Concrete; ...]` only when a tuning value was replaced by the
  class default (the same line, no extra warning).
- `Stop()` (switch turned off during a raid, and the `NativeSpatialAudio.Release` cleanup step): restore the shipped
  model, destroy the component, re-apply the plugin default room like its own `CheckSceneHasRoom` (fresh
  component, one `Update`, destroy), clear all static state.
- Any exception is caught, logged once at Error, restores the shipped acoustics and disables the feature for the rest
  of the raid. Nothing escapes into EFT.
- Cost: nothing per frame beyond the gates; the component itself makes two small native calls per frame (Meta's design,
  accepted).

## Probe procedure

Developer mode. `Audio diagnostics / ReverbPulse = true`, once per bus and scenario, with `NO BOTS`, standing still.
Three gunshot buses (`ReverbPulseBus`), because a shot can reach the Meta plugin on more than one route and a single
bus could not tell "the shoebox does nothing" from "this route does not use it":

| Bus | Route | Position | What it stands for |
|---|---|---|---|
| `Gunshots` | `BetterAudio.PlayAtPoint(..., Gunshots, 200, 0.08, spatialize: true, autoReleaseSource: false)` | 2 m behind the listener | the plain spatialized route (the source's own Meta spatializer only) |
| `GunshotsFirstPerson` | weapon queue, `EnableStereo(true)` | 2 m in front | the player's own weapon (`PointOfView == FirstPerson`) |
| `GunshotsThirdPerson` | weapon queue, `EnableStereo(false)` | 2 m behind | every other shooter's weapon (bots, other players) |

Weapon-queue buses do what `WeaponSoundPlayer` does: `BorrowWeaponAudioQueue(Gunshots)`, `Pose`, `SetRolloff(200)`,
`SetMixerGroup(GunshotMixerGroup)`, `EnableStereo(..)`, `Enqueue` of the synthetic 0.12 s 1500 Hz pulse at volume
0.08, and `ReleaseQueue` afterwards. The `Gunshots` source is released with `BetterSource.Release()` at the same
point (it is played with `autoReleaseSource: false`, so BetterAudio's time queue neither frees it early nor a second
time). The spatialized buses play from behind because `BetterSource.CheckBinauralAllowed` switches the Meta
spatializer off for a source straight ahead at 2 m; the `pulse route` line proves what really happened. The
measurement windows are identical on every bus, so numbers compare across buses within the limits of the different
routes (HRTF colouring differs); the meaningful comparison is the same bus across scenarios.

Scenarios per bus:

1. Feature off (`RoomShotAcoustics` false): baseline.
2. Feature on, standing in the AirPit main hall.
3. Feature on, standing inside a shop (for example a Burger Spot room).
4. Optional: inside an airliner room (Metal), and outdoors (must equal the baseline).

Log lines (`[ArenaMapPort][RoomReverb]`):

- `pulse begin ... acousticModel=<model> nativeRoom rc=<rc> size=<w>x<h>x<d> position=<x,y,z> coefs[0..5]=...`:
  the plugin's model and the native shoebox room active during the measurement
  (`MetaXRAudioNativeInterface.Interface.GetRoomDimensions`; oversized buffers, native axes).
- `pulse route bus=<bus> source=ReverbSuperSource spatialized=<bool> acoustics=<bool> reverbSendDb=<db> helperSources=<bool> position=...`
  50 ms after the tone started: which Meta path the source really took. `spatialized=False` on a "spatialized" bus means
  the heuristics kept the plugin off and that bus did not measure its route.
- `pulse result bus=<Gunshots(PlayAtPoint) | GunshotsFirstPerson(weapon queue) | GunshotsThirdPerson(weapon queue)> ... tailEarly=<e> tailLate=<e>`.

Expected if the mechanism works on a route: `acousticModel=ShoeboxRoom` and the native size equal to the logged
preset (hall 50 x 43.2 x 50 with the default cap), `tailEarly` and `tailLate` of the hall clearly above the shop and both different from
feature-off. If the numbers do not move on a route although the log shows the right model, room and a Meta path
(`spatialized=True` or `helperSources=True`), the shoebox is not audible on that route and the mechanism must be
re-examined by ear and by `MetaXRAudioSource` inspection. A route that moves while another does not tells which
shots (own or other shooters') the invented rooms reach.

## First host measurement (2026-10-15 15:00-15:07, AirPit, GPU-2, bots None, DeveloperMode)

Build: client DLL of the uncommitted feature (sha256 2d56641f...), `uibridge_status` 19/19 identical (VM build = host disk =
loaded), EFT instance ad32a23d. Probe: `Audio diagnostics / ReverbPulse` per bus, standing still; numbers are listener-output
energy at 1500 Hz (baseline before the pulse, early 0.35-0.7 s and late 0.7-1.5 s after it; "output includes background
audio", so the baseline column is the noise floor of that run).

| Room / state | Bus | baseline | tailEarly | tailLate |
|---|---|---|---|---|
| Main hall (room 17 IndoorConcrete), off | Gunshots (PlayAtPoint) | 7.4e-7 | 9.2e-7 | 1.1e-6 |
| | GunshotsFirstPerson | 2.0e-5 | 3.8e-5 | 4.6e-6 |
| | GunshotsThirdPerson | 5.4e-6 | 5.8e-6 | 8.1e-5 |
| Main hall, ON (80.0x43.2x80.0 Concrete walls+ceiling, CeramicTile floor, clutter 0.30) | Gunshots | 2.6e-5 | 1.2e-4 | 1.2e-4 |
| | GunshotsFirstPerson | 1.1e-5 | 7.4e-5 | 4.0e-5 |
| | GunshotsThirdPerson | 2.7e-5 | 2.3e-4 | 8.6e-5 |
| Burger Spot (room 7 IndoorBrick), ON (14.05x5.26x9.55 PlasterOnBrick walls, GypsumBoard ceiling, CeramicTile floor, clutter 0.60) | Gunshots | 2.0e-4 | 3.7e-4 | 8.1e-4 |
| | GunshotsFirstPerson | 8.1e-7 | 4.2e-4 | 2.4e-4 |
| | GunshotsThirdPerson | 9.9e-6 | 4.2e-4 | 2.5e-4 |
| Burger Spot, off again | Gunshots | 1.4e-5 | 9.2e-6 | 1.7e-5 |

Log evidence: `[ShotAcoustics] arena_airpit: room=17/IndoorConcrete class=Hall dims=80.0x43.2x80.0 ... model=ShoeboxRoom`
(15:03:10.741, the frame after the switch), `room=7/IndoorBrick class=Shop dims=14.0x5.3x9.5 ... clutter=0.60` (15:04:00.198,
after an 11.5 m `uibridge_walk` from (12,0,4) to (19.6,0,12.6)), `room=21/OutdoorGeneric class=Outdoor dims=none ...
model=AcousticRayTracing` (15:07:02.350, teleport to the apron (-3,0,-58)). Every `pulse begin` line read the native room
back through `GetRoomDimensions` (rc=0): 8.00x3.00x5.00 with the shipped model, 80.00x43.22x80.00 / 14.05x5.26x9.55 with the
feature on, and 8.00x3.00x5.00 + `AcousticRayTracing` again after the switch was turned off mid-raid (15:06:46, the plugin
default room restored by `Revert`). The read-back coefficient column printed 0.970 for every wall in every state, so it does
not reflect the material table (the native array layout is not the per-wall preset list; the dims are the usable proof).
Routes: `Gunshots` = ReverbSuperSource spatialized=False acoustics=True reverbSendDb=-6 helperSources=False (the PlayAtPoint
source did not get the Meta spatializer even from behind, the reverb send alone carried the tail); both weapon-queue buses
spatialized=False acoustics=True helperSources=True. No Warning or Error in the BepInEx log; `uibridge_status` max frame
time stayed at the raid-start/exit spikes only.

Reading: the mechanism works on all three routes (tail energy up 2x to 100x with the feature on, back to the noise floor
with it off), but the invented materials are wrong: the small shop rings 3-6x LOUDER and longer than the hall on every
route (PlasterOnBrick 0.976 and CeramicTile 0.99 reflect almost everything; a 14x5x9.5 m box of that is a tiled bathroom),
and the hall's late tail equals its early tail (a long, flat wash). Next round: absorptive ceilings and cluttered walls as
new defaults, then tuning through the ShotAcousticsLab keys (measure, then the user's ears).

Cause: the first table chose almost perfectly reflective presets (Meta reflection coefficients over the four bands,
from `MetaXRAudioRoomAcousticProperties.SetWallMaterialPreset`: Concrete 0.99 / 0.98 / 0.98 / 0.98, PlasterOnBrick
0.976 / 0.979 / 0.961 / 0.950, CeramicTile 0.99 / 0.99 / 0.98 / 0.98). A small box with almost lossless walls
re-reflects the energy many times; the big hall dilutes it. So the table cannot be guessed, it is tuned by measurement
and ear, which is why every material and clutter factor became a live key and the defaults moved to absorptive
ceilings and cluttered walls (next section).

## Tuning round

Developer mode, feature on, `ReverbPulse` probe on the three gunshot buses, standing in the hall, in a shop and in an
airliner room. Edit the `ShotAcousticsLab` keys in F12 (no rebuild, no raid restart); each edit re-applies the
current room on the next tick and the `[ArenaMapPort][ShotAcoustics]` Debug line shows the materials that went in.
Compare `tailEarly` / `tailLate` per route, then listen.

Reflection coefficients of the more absorptive presets over the four bands (decompile of the Meta plugin; 1 = perfectly
reflective, 0 = nothing returns): AcousticTile 0.49 / 0.36 / 0.34 / 0.50, Cardboard 0.59 / 0.44 / 0.25 / 0.21,
Curtain 0.69 / 0.55 / 0.31 / 0.40, Foliage 0.52 / 0.50 / 0.58 / 0.69, CarpetHeavyPadded 0.91 / 0.53 / 0.29 / 0.27,
Carpet 0.99 / 0.91 / 0.58 / 0.35, Snow 0.53 / 0.15 / 0.05 / 0.05, SoundProof 0 / 0 / 0 / 0. For comparison Steel is
0.79 / 0.84 / 0.93 / 0.98; the measured Metal tail (round 2) sits between shop and hall, untuned. Meta describes the clutter factor as
diffusing the reflections and reverberation to simulate objects in the room (0 = a completely empty room); the plugin
spreads it over the four bands as c for band 3, halving per band down to c/8 for band 0.

First tuning attempt (all invented, **superseded** by the measured choice below): Hall Concrete walls, AcousticTile
ceiling (airport halls have absorptive ceilings), CeramicTile floor, clutter 0.4, cap 80 m; Shop Cardboard walls (shelves
full of goods), AcousticTile ceiling, CeramicTile floor, clutter 0.7; Metal unchanged materials (Steel walls and ceiling,
Carpet floor), clutter 0.7. These were the defaults when round 2 started.

### Round 2: measured record (2026-10-15, AirPit, GPU-2, bots None, DeveloperMode)

Numbers are listener-output energy at 1500 Hz as the probe logs them (`tailEarly` 0.35-0.7 s, `tailLate` 0.7-1.5 s after
the pulse), pulse level 0.8 (`ReverbPulseVolume`) on the muted host. Rain and ambience were muted for the measurement
through `Audio (arena_airpit)` `RainVolume` 0, `AmbientGainDb` -12 and `AmbientVolumeOffset` 0, and restored afterwards;
baselines after muting were 1e-6 to 7e-6. Before the rain was muted the hall baseline was 2.0e-4 and swamped the tails,
which is why `ReverbPulseVolume` exists. Build and instance facts are under "Host verification".

| Room | Walls / ceiling / floor, clutter, cap | Route | tailEarly | tailLate | Note |
|---|---|---|---|---|---|
| Hall, room 17 `Main` IndoorConcrete | Concrete / AcousticTile / CeramicTile, 0.4, cap 80 | GunshotsFirstPerson | 4.6e-4 | 2.1e-4 | dryPeak 0.127 |
| same | same | GunshotsThirdPerson | 4.6e-4 | 2.1e-4 | dryPeak 0.0965 |
| Shop, room 7 `AudioRoom_Burger_Spot_3` IndoorBrick 14.0x5.3x9.5 | Cardboard / AcousticTile / CeramicTile, 0.7 | GunshotsFirstPerson | 5.5e-6 | 8.8e-7 | dead, no audible room |
| same | BrickPainted / AcousticTile / CeramicTile, 0.7 | GunshotsFirstPerson | 1.0e-3 | 2.6e-4 | louder than the hall again |
| same | ConcreteBlock / AcousticTile / CeramicTile, 0.7 | GunshotsFirstPerson | 7.8e-5 | 8.5e-6 | tight and quiet |
| same | GypsumBoard / AcousticTile / CeramicTile, 0.7 | GunshotsFirstPerson | 5.6e-4 | 9.8e-5 | **chosen**: a clear early slap that dies within the late window |
| Hall, room 17 again | Concrete / AcousticTile / CeramicTile, 0.4, cap 50 | not noted | 5.4e-4 | 3.7e-4 | |
| Hall, room 17 again | Concrete / Concrete / CeramicTile, 0.4, cap 50 | not noted | 8.7e-4 | 7.4e-4 | **chosen**: the long sustained tail of a big hall; the late window keeps 85 % of the early energy, against 18 % in the GypsumBoard shop |
| Metal, room 1 airliner IndoorMetalThin 6.0x2.1x39.4 | Steel / Steel / Carpet, 0.7 (unchanged) | not noted | 6.3e-4 | 1.4e-4 | between shop and hall, a medium metallic tail; untuned, left for the user's ear |

The shop rows are all `GunshotsFirstPerson`; the record does not name a route for the two "hall again" rows and the metal
row. The spawn room 6 (`Start1`, the passport hall, IndoorBrick 111x35x89) classifies as a Hall (80x34.9x80 under the old
cap), as the 40 m rule intends; whether the user wants the passport hall as a hall is an open question.

Reading: the hall-vs-shop contrast now points the right way on the first-person weapon route (the shop and the cap-50 hall rows were measured on GunshotsFirstPerson only; the hall's third-person row exists only for the first table, where both routes gave the same numbers): hall late tail 7.4e-4 vs shop 9.8e-5
(7.5x), early 8.7e-4 vs 5.6e-4. Walls are by far the strongest lever (three steps between dead and too loud: in the same
shop the early energy runs from 5.5e-6 with Cardboard to 1.0e-3 with BrickPainted, a factor of about 180), the ceiling comes
next, and the cap moves the late level more than the early one (hall with the AcousticTile ceiling: early 4.6e-4 at 80 m
against 5.4e-4 at 50 m, late 2.1e-4 against 3.7e-4). All values remain invented; the lab keys stay for tuning by ear.

Decision (this is what the code defaults are now, pinned in `RoomShotAcousticsModelTests`):

| | Walls | Ceiling | Floor | Clutter |
|---|---|---|---|---|
| Hall | Concrete | Concrete | CeramicTile | 0.4 |
| Shop | GypsumBoard | AcousticTile | CeramicTile | 0.7 |
| Metal (unchanged) | Steel | Steel | Carpet | 0.7 |

`HallMaxDimension` default 50 (range stays 10..200). The earlier "AcousticTile ceiling, Cardboard shop walls" choice is
the first tuning attempt above and no longer a default.

## Open points

- That `ShoeboxRoom` is audible on the gun path is answered by the first measurement (tail energy rises on all three
  routes: `PlayAtPoint`, first-person and third-person weapon queue). The table question (which materials and clutter give a long hall tail
  and a short, tight shop) is answered by measurement in round 2 for Hall and Shop. Open: the Metal class is untuned, the
  passport hall as a hall, and the ear: every value is invented and waits for the user's listening note.
- The plugin's setter calls the native `SetAcousticModel` only when the managed value changes. An audio configuration
  change during a raid is handled by re-applying the room and re-sending the model (see Runtime behaviour); whether the
  plugin really drops its native state on a reset is not verified (the Unity native interface also caches its
  context pointer once, `UnityNativeInterface.context_`, and nothing refreshes it). Not observed on the host yet; the
  raid-start reset happens before the repair applies anything (it waits for `Started`).
- Fika / headless: the batch-mode / null-graphics gate and the parked listener keep the repair inert; not tested.
- `GetRoomDimensions` buffer sizes (16 / 64 floats) are deliberately generous because the native extent is not
  documented; the first host log shows the returned values.
- Possible later work: per-room overrides for individual rooms (keyed by the graph path), a Metal class for
  `IndoorMetalThick`, orienting the box to the room (the plugin's box is axis-aligned).

## Files

- `ArenaMapPort.Client/RoomShotAcousticsModel.cs`: pure model, no Unity/EFT/Meta types.
- `ArenaMapPort.Client/RoomShotAcoustics.cs`: runtime repair, config, Meta helper.
- `ArenaMapPort.Client/ReverbAudioProbe.cs`: `ProbeBus.Gunshots` / `GunshotsFirstPerson` / `GunshotsThirdPerson`,
  Meta state in the "pulse begin" line, source state in the "pulse route" line.
- `ArenaMapPort.Client/NativeSpatialAudio.cs`, `Plugin.cs`, `ArenaMapPort.Client.csproj` (Meta.XR.Audio reference):
  wiring.
- `ArenaMapPort.Server.Tests/RoomShotAcousticsModelTests.cs` (+ csproj link): 76 tests, AirPit rooms as literal
  fixtures, the pinned default table, per-class tuning, the unknown-name fallback and its report, the per-key
  material lists (`MaterialChoices`, and the BepInEx clamp landing on the key default), clamping,
  outdoor/isolated, Meta material names, the re-apply rule (`NeedsApply` with the tuning version), the audio-client
  rule, wiring text checks (lab section and keys, the removed old key, `SettingChanged` hooks).

## Review follow-ups (2026-10-15)

Five review findings, all addressed (the first four in code):

1. Scene search in live play: the once-per-raid `FindObjectsOfType<MetaXRAudioRoomAcousticProperties>` ran on the first
   indoor frame (about 30 ms on a ported scene). Removed: the repair is the only creator of the component. A text test
   forbids scene searches in `RoomShotAcoustics.cs`.
2. Audio reset / device change: the repair now observes `AudioSettings.OnAudioConfigurationChanged`, re-applies the
   current room and re-sends the model natively (`NeedsApply` rule, unit-tested).
3. Headless gate: the listener object exists on every client, so the old "no AudioListener = headless" guard was
   vacuous. Replaced by `IsAudioClient(Application.isBatchMode, graphics device Null)`; comments and this document no
   longer claim more than that.
4. Probe route: `Gunshots` is `BetterAudio.PlayAtPoint` as specified (released by the probe, `autoReleaseSource: false`);
   the weapon-queue route moved to `GunshotsFirstPerson`, and `GunshotsThirdPerson` was added for the spatialized
   weapon-queue route that bots and other players take. Real shots all use the weapon queue; PlayAtPoint is a
   different route (only the artillery sound uses it for this group), so it is covered as a bus of its own. One part
   of the finding does not hold: bot shots are not "exactly the PlayAtPoint route" but the weapon queue with
   `EnableStereo(false)`, which is why the third bus exists.
5. Room ids: `1250` and `1197` were graph node ids, the runtime and the log use `room_id` (27 and 3).

Lab-keys review (2026-10-15), one finding, confirmed and fixed:

6. The model's fallback to the class default (`class-default-fallback=[...]`) was unreachable from the `.cfg`: BepInEx's
   shared `AcceptableValueList` clamps an invalid name to its first entry (`AcousticTile`) when the file loads, so a typo in
   the hall walls became very absorptive tile instead of the default Concrete and nothing was logged. Reproduced against
   the installed `BepInEx.dll`. Fixed with one list per material key, led by the key's default
   (`RoomShotAcousticsModel.MaterialChoices`); see "Config keys". Tests: list construction, the simulated clamp for all
   nine keys, and a wiring check that no key shares the plain enum-ordered list.

## Equator and Bay5 (user decision 2026-10-16: keep the shoebox solution, enable it on the other two maps)

The switch `Audio (<map>)/RoomShotAcoustics` already existed for every released map and the class table is generic by
room type and size, so nothing map-specific is needed. Offline census of the active rooms in each map's spatial-audio
graph (`work/extract/spatial-audio/<map>/spatial-audio.json`, the same bounds the runtime reads; types are
`EAudioRoomTypeMask` values: 4 IndoorGeneric, 64 OutdoorCommon, 1024 IndoorWoodThick, 16384 IndoorWoodThin,
65536 IndoorMetalThick; every indoor type other than IndoorMetalThin is a Shop unless the 40 m rule makes it a Hall):

| Map | Hall | Metal | Shop | Outdoor | Notes |
|---|---|---|---|---|---|
| Equator | `Main` 54.8 x 24.7 x 59.7 IndoorConcrete; `UpRoof` 9.9 x 6.5 x 40.7 IndoorBrick (a corridor over 40 m long: concrete walls, a plausible ringing service corridor) | none | 19 (the shops, corridors, kitchen, start rooms; 8 of them IndoorWoodThin) | none (the atrium is an indoor room by Arena's data) | |
| Bay5 | `SpawnBuilding1` 44.0 x 9.6 x 21.6 IndoorBrick | 18: `SpawnBulding2` 17.2 x 12.6 x 62.4, `Hangar` 18.9 x 12.1 x 23.1 and 16 containers, all IndoorMetalThin (Steel walls: the long warehouse rings metallic, which fits a metal shed) | 6 (`PlantBuldingA` 23.4 x 10.6 x 18.7 IndoorBrick, its small rooms IndoorWoodThick / IndoorMetalThick, `Hangar_out`, the building-2 entrance) | 15 (the big `OUTDOOR` 251 x 27 x 175 OutdoorCommon, the ship, every back yard) | outdoors the shipped model stays, so most of Bay5 is unchanged |
| AirPit | `Main`, `Start1` | 9 (planes, bus) | 12 shops | `Start2` (apron) | measured above |

Host check (GPU-2, 2026-10-16 09:58-10:05, EFT instance of the 2026-10-15 final build, one process: Equator Day raid,
then Bay5 raid, bots None, DeveloperMode, `ReverbPulseVolume` 0.8, the map's ambience keys muted during the measurement
and restored afterwards; Bay5 had rain 0.27 with `RainVolume` 0). `GunshotsFirstPerson` bus, measured defaults:

| Map / room | class, dims applied | baseline | tailEarly | tailLate |
|---|---|---|---|---|
| Equator `Main` (room 19 IndoorConcrete) | Hall 50.0 x 24.7 x 50.0 | 2.0e-6 | 1.86e-3 | 9.8e-4 |
| Equator `adik` (room 3 IndoorBrick) | Shop 17.8 x 4.3 x 23.5 | 1.4e-4 | 1.26e-3 | 3.0e-4 |
| Bay5 `SpawnBuilding1` (room 16 IndoorBrick) | Hall 44.0 x 9.6 x 21.6 | 2.4e-6 | 3.80e-3 | 3.30e-3 |
| Bay5 `SpawnBulding2` (room 11 IndoorMetalThin) | Metal 17.2 x 12.6 x 50.0 (capped) | 1.6e-5 | 7.0e-4 | 3.5e-4 |
| Bay5 `PlantBuldingA` (room 40 IndoorBrick) | Shop 23.4 x 10.6 x 18.7 | 9.5e-6 | 8.9e-4 | 3.5e-4 |

Log evidence: Equator spawn `room=5/IndoorGeneric class=Shop dims=29.0x4.1x12.7` (09:59:37), `room=19/IndoorConcrete
class=Hall dims=50.0x24.7x50.0` (09:59:43), `room=3/IndoorBrick class=Shop` (09:59:55); Bay5 spawn `room=14/OutdoorCommon
class=Outdoor dims=none ... model=AcousticRayTracing` (10:04:06), then `room=16/IndoorBrick class=Hall dims=44.0x9.6x21.6`
(10:04:11), `room=11/IndoorMetalThin class=Metal dims=17.2x12.6x50.0` (10:04:33), `room=40/IndoorBrick class=Shop
dims=23.4x10.6x18.7` (10:04:45). No Warning or Error from the plugin in either raid; the map switch AirPit -> Equator ->
Bay5 inside one process (the 2026-10-15 AirPit raids ran in the previous process of the same build) left the model and
room state consistent (Bay5 started outdoors with the shipped model).

Reading: Equator behaves like AirPit (atrium late tail 3x the shop's, decay 0.53 vs 0.24). Bay5's `SpawnBuilding1` is
the loudest room measured so far (late tail 3.3e-3, 4.5x AirPit's hall): a 44 x 22 m warehouse with a 9.6 m ceiling under
the Hall materials (Concrete walls and ceiling) keeps nearly all energy, where the airport hall's 43 m height dilutes it.
That is the Hall class applied to a mid-size closed box; if the user finds it too live, the lever is the Hall ceiling
(AcousticTile) or clutter, or a volume-scaled clutter in the model. The metal warehouse (`SpawnBulding2`, Steel) and
the plant building (Shop) sit close together; outdoors (most of Bay5) nothing changes.


## Host verification

Two measurement rounds, both 2026-10-15 on AirPit, GPU-2, bots None, DeveloperMode:

- Round 1: section "First host measurement" (15:00-15:07, client DLL of the uncommitted feature, sha256 2d56641f...,
  `uibridge_status` 19/19 identical, EFT instance ad32a23d). It carries the probe table, the `pulse begin` / `pulse route`
  readings, the room-change log lines (hall -> shop -> outdoors) and the switch-off restore.
- Round 2: section "Tuning round" (15:34-15:39): client DLL of the lab-keys build, `uibridge_status` 19/19 identical (VM
  build = host disk = loaded), EFT instance 85490820. The measured record, the decision and the new defaults are there.
  The chosen values were measured live through the lab keys. They then became the code defaults (this document and
  `RoomShotAcousticsModelTests` were updated together; the Release build, the unit tests and `ConfigRetirementCheck` were
  rerun for that change); a host build that carries them as defaults has not been run yet.

TODO (still open):

- The Stop path on raid exit: seen only indirectly, through the next raid's `pulse begin` reading 8x3x5 + `AcousticRayTracing`
  after the switch-off. A hideout visit and a cross-map raid with the switch on are still to do.
- The user's listening note (hall vs shop vs the airliner rooms, with the measured defaults).
- Equator and Bay5: the switch exists for every map in `ArenaMapRegistry.All`, nothing has been measured there.
