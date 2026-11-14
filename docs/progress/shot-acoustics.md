# Per-room gunshot acoustics (invented data) - 2026-10-15

Status: implemented, builds and unit tests green. Two host measurement rounds on 2026-10-15 (AirPit, GPU-2): the mechanism
works; the first invented table was wrong (a shop rang louder and longer than the hall); the second round measured
alternatives and the defaults are now the measured choice (see "Tuning round"); a third round on 2026-10-16 measured Equator
and Bay5 (see "Equator and Bay5"). The table stays live-tunable in the developer
config section `ShotAcousticsLab` for tuning by ear. **On by default on every map since 2026-10-16** (user decision after
listening on all three maps); the switch stays per map and turns it off.

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
data, and it is open to tuning by ear. The switch is on by default since 2026-10-16 (user decision after listening on all
three maps); it ships on in player mode, and the data stays invented.

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
- Only `IndoorMetalThin` is Metal (2026-10-18: plus a big `IndoorMetalThick` room, see "Bowl" below). `IndoorMetalThick` (Bay5 `PlantBuldingA_back_indoor`: `room_id` 27, graph node
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
| `Audio (<map key>) / RoomShotAcoustics` (every map in `ArenaMapRegistry.All`) | true (on by default since 2026-10-16, user decision after listening on all three maps) | Live switch. Off restores the shipped acoustics. A Settings key: in player mode it is visible and changeable only with `General/DeveloperMode`. The key never shipped before the default became true (born 2026-10-15 on the unreleased branch), so there is no `DefaultsRevision` migration. |
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
  passport hall as a hall. The ear has answered: the user listened on all three maps on 2026-10-16 and kept the
  shoebox solution (every value stays invented).
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
dims=23.4x10.6x18.7` (10:04:45). No Warning or Error from the plugin in either raid; the map switch Equator -> Bay5
inside one process left the model and room state consistent (Bay5 started outdoors with the shipped model). The
2026-10-15 AirPit raids ran in an earlier process of the same build, so an AirPit -> Equator switch inside one process
was not observed.

Reading: Equator behaves like AirPit (atrium late tail 3x the shop's, decay 0.53 vs 0.24). Bay5's `SpawnBuilding1` is
the loudest room measured so far (late tail 3.3e-3, 4.5x AirPit's hall): a 44 x 22 m warehouse with a 9.6 m ceiling under
the Hall materials (Concrete walls and ceiling) keeps nearly all energy, where the airport hall's 43 m height dilutes it.
That is the Hall class applied to a mid-size closed box; if the user finds it too live, the lever is the Hall ceiling
(AcousticTile) or clutter, or a volume-scaled clutter in the model. The metal warehouse (`SpawnBulding2`, Steel) and
the plant building (Shop) sit close together; outdoors (most of Bay5) nothing changes.


## Host verification

Three measurement rounds on GPU-2, bots None, DeveloperMode: two on AirPit on 2026-10-15, one on Equator and Bay5 on 2026-10-16.
During all three the switch was set on in the shared developer config (the code default was still off at that time):

- Round 1: section "First host measurement" (15:00-15:07, client DLL of the uncommitted feature, sha256 2d56641f...,
  `uibridge_status` 19/19 identical, EFT instance ad32a23d). It carries the probe table, the `pulse begin` / `pulse route`
  readings, the room-change log lines (hall -> shop -> outdoors) and the switch-off restore.
- Round 2: section "Tuning round" (15:34-15:39): client DLL of the lab-keys build, `uibridge_status` 19/19 identical (VM
  build = host disk = loaded), EFT instance 85490820. The measured record, the decision and the new defaults are there.
  The chosen values were measured live through the lab keys. They then became the code defaults (this document and
  `RoomShotAcousticsModelTests` were updated together; the Release build, the unit tests and `ConfigRetirementCheck` were
  rerun for that change).
- Round 3: section "Equator and Bay5" (2026-10-16 09:58-10:05): Equator Day and Bay5 raids in one process (the AirPit
  raids of rounds 1 and 2 ran in an earlier process), measured defaults, map switch Equator -> Bay5 left the model and
  room state consistent (Bay5 started outdoors with the shipped model). The hideout opened with the build in 7 s
  (HANDOFF 2026-10-15/16).
- Decision 2026-10-16: after listening on all three maps the user kept the shoebox solution (no clip-set remap) and decided to
  ship it **on by default** on every map. The code default of `RoomShotAcoustics` is therefore `true` (client Release build,
  server build, the unit tests incl. the pinned bind in `RoomShotAcousticsWiringTests` and `ConfigRetirementCheck` were rerun
  for that change). The key was born on the unreleased `feature/airpit` branch on 2026-10-15, so no `DefaultsRevision`
  migration is needed; a developer `.cfg` that already holds the key keeps its saved value.

Closed by the rounds above: the Stop path across raids (the next raid's `pulse begin` read 8x3x5 + `AcousticRayTracing` after
the switch-off), the Equator -> Bay5 start in one process, the hideout visit, the user's listening note, and the Equator
and Bay5 measurements.

Still open:

- A host raid on a build that carries the default-on value with a fresh configuration (no saved `RoomShotAcoustics` entry);
  the rounds above ran with the switch set in the shared developer config.
- An AirPit -> Equator (or AirPit -> Bay5) map switch with the switch on inside one EFT process; only Equator -> Bay5 was
  observed that way.
- HANDOFF 2026-10-15 lists two user inputs without a recorded answer: whether the passport hall (Start1, classified Hall by
  the 40 m rule) should stay a hall, and a metallic character for the planes and the bus (Steel/Steel/Carpet, measured but
  untuned).


## Bowl (2026-10-18, PLAN-BOWL item 17: room classes)

Status: offline analysis, one rule change and the Bowl pins are done and green; no host raid has run the changed rule yet (the
Bowl raids of 2026-10-18 ran before it; recipe below). The switch `Audio (arena_bowl)/RoomShotAcoustics`
already exists and defaults on: `Install` loops `ArenaMapRegistry.All`, which carries Bowl, and Bowl's acoustic graph is built by
`NativeSpatialAudio` (it needs a started acoustic state for the repair to tick).

Source: `work/extract/spatial-audio/arena_bowl/spatial-audio.json`, 14 rooms, 25 portals, 24 areas; the system group lists 11
rooms (`groups[0].room_ids`). The loader (`NativeSpatialAudio`, around "Explicit source group arrays exclude inactive
originals") sets `ISpatialAudioRoom.ID` from the graph's `room_id` and gives the group the 11 rooms of `room_ids` as its room list,
so the three other source rooms are never the listener's room. The brief and `RoomReverb` use the graph's own room `id` (531 ...); the `[ShotAcoustics]` log line prints the runtime
`room_id` (1 ... 11), exactly like the AirPit/Bay5 rows above (Bay5 `room_id` 27 = graph id 1250). Both are listed.

Types are `EAudioRoomTypeMask` bits (Audio.SpatialSystem, decompile 0.16.9.5): 1 OutdoorGeneric, 2 OutdoorVehicle, 4 IndoorGeneric,
8 IndoorIsolated, 0x10 IndoorVehicle, 0x20 IndoorStairs, 0x40 (64) OutdoorCommon, 0x80 IndoorBasement, 0x100 IndoorTransparent,
0x200 (512) IndoorMetalThin, 0x400 (1024) IndoorWoodThick, 0x800 IndoorBrick, 0x1000 IndoorConcrete, 0x2000 IndoorPlasterBoard,
0x4000 IndoorWoodThin, 0x8000 (32768) IndoorFabric, 0x10000 (65536) IndoorMetalThick; Outdoor = 0x43, Indoor = 0x1FFBC,
Isolated = 0x88. Sizes are the full AABB sizes (twice the bounds extents), the shoebox gets them clamped to [2 m, 50 m].

| `room=` (runtime) | graph id | path below SpatialAudioSystem | type | AABB W x H x D (m) | class before | class now | shoebox preset |
|---|---|---|---|---|---|---|---|
| 1 | 531 | Containers_R/Container_1 | 512 IndoorMetalThin | 8.15 x 2.68 x 7.04 | Metal | Metal | 8.2x2.7x7.0 |
| 2 | 554 | Military_R/Hospital_enterance | 32768 IndoorFabric | 2.31 x 2.44 x 3.59 | Shop | Shop | 2.3x2.4x3.6 |
| 3 | 558 | Offices_L/Offices_AudioRoom_3 | 1024 IndoorWoodThick | 14.37 x 2.50 x 2.26 | Shop | Shop | 14.4x2.5x2.3 |
| 4 | 563 | Helicopter/Helicopter (crashed Mi-26) | 65536 IndoorMetalThick | 20.78 x 4.49 x 13.79 | Shop | **Metal** | 20.8x4.5x13.8 |
| 5 | 568 | FanZone/FanZone | 4 IndoorGeneric | 49.48 x 2.91 x 10.12 | Hall | Hall | 49.5x2.9x10.1 |
| 6 | 587 | Containers_L/Container_2 | 512 IndoorMetalThin | 6.54 x 2.64 x 4.89 | Metal | Metal | 6.5x2.6x4.9 |
| 7 | 594 | Offices_L/Offices_AudioRoom_2 | 1024 IndoorWoodThick | 6.00 x 2.50 x 2.26 | Shop | Shop | 6.0x2.5x2.3 |
| 8 | 636 | Outdoor/Outdoor (the stadium) | 64 OutdoorCommon, outdoor=true | 239.9 x 42.4 x 235.3 | Outdoor | Outdoor | none (shipped model stays) |
| 9 | 656 | Military_R/Hospital | 512 IndoorMetalThin | 8.84 x 2.83 x 14.21 | Metal | Metal | 8.8x2.8x14.2 |
| 10 | 665 | Containers_L/Container_1 | 512 IndoorMetalThin | 3.39 x 2.68 x 6.39 | Metal | Metal | 3.4x2.7x6.4 |
| 11 | 672 | Offices_L/Offices_AudioRoom_1 | 1024 IndoorWoodThick | 2.26 x 2.41 x 12.11 | Shop | Shop | 2.3x2.4x12.1 |

Outside the group (`active_in_hierarchy` false, never a listener room): graph 550 `Military_R/Base` (IndoorGeneric,
9.40 x 1.94 x 5.29, `room_id` 2 like the active `Hospital_enterance`: the inactive originals reuse valid native ids), graph 572
`Military_L/Base` and 659 `Military_L/Tower` (IndoorGeneric, bounds 0 x 0 x 0 at (69.7, 12.9, -42), `room_id` 0).

Active classes of the 11: Metal 5 (4 IndoorMetalThin + the Mi-26), Shop 4, Hall 1, Outdoor 1. With the old rule: Metal 4, Shop 5.

### Judgement room by room

- Stadium (room 8): the outdoor flag AND the type bit 64 (inside `OutdoorBits` 0x43) both say Outdoor, not isolated, so
  `IsOutdoor` is true by either; no preset, the shipped model stays, as on Bay5's yards. No change needed.
- Containers, Hospital (rooms 1, 6, 9, 10; IndoorMetalThin): Metal already (Steel walls and ceiling, Carpet floor, clutter 0.7).
- Offices (rooms 3, 7, 11; IndoorWoodThick) and the Hospital entrance tent (room 2, IndoorFabric): Shop. Fine. The
  corridor-like offices (2.3 m wide) get a 2.3 m wide shoebox; the minimum edge is 2 m, so none is raised. The tent is a Shop although
  fabric would be more absorptive; the room is 2.3 x 2.4 x 3.6 m, an invented-data refinement nobody can hear.
- Mi-26 (room 4; IndoorMetalThick): the one wrong result. A steel hull classified as a shop (GypsumBoard walls, AcousticTile
  ceiling) while the containers next door ring as steel. Metal so far meant IndoorMetalThin; IndoorMetalThick was a Shop on purpose
  (Bay5 `PlantBuldingA_back_indoor`, room_id 27, 9.0 x 2.7 x 10.0 m). The hull lies rotated by about -30 degrees (OBB half
  extents 7.5 x 1.7 x 1.95 m for the main body), so its AABB (20.8 x 13.8 m floor) overstates the cross-section; the plugin's
  box is axis aligned anyway.
- FanZone (room 5; IndoorGeneric, 49.5 x 2.9 x 10.1 m at y 20.7, above the north stand): Hall by the 40 m rule, a long low
  concourse, the same situation as Equator's `UpRoof` corridor (Concrete shell, clutter 0.4). The NavMesh ends at y 11.05 and holds no vertex within 4.5 m
  (vertical) of its box, so bots never path there; whether a player could reach it by other means (stand geometry) was not checked.
  Note only, no change.
- Zero-extent rooms 572 and 659: `Clamp` raises every edge to `MinDimension` 2 m, so the model answer is a Shop preset of
  2x2x2 with the default Shop materials (no correction, no exception; pinned). They are not in the group, hence never applied.

### Rule change (minimal)

`RoomShotAcousticsModel.Classify`: an IndoorMetalThick room whose larger floor edge reaches `MetalThickBodyMinFloorEdge` (15 m)
is Metal, right after the IndoorMetalThin test and before the Hall test (the same precedence the thin-metal rule has over the
Hall rule). Threshold INVENTED, chosen between the only two IndoorMetalThick rooms in the four graphs: Bay5's back room has 10.0 m
(stays Shop, as released and as pinned), Bowl's Mi-26 has 20.8 m (becomes Metal; its real hull is 14.9 m long, so the AABB and the
threshold are not coupled to the hull). Nothing else moves: Equator 19 Shop / 2 Hall, Bay5 15 Outdoor / 18 Metal / 6 Shop / 1 Hall,
AirPit 12 Shop / 9 Metal / 2 Hall / 1 Outdoor, recomputed offline with the new rule (identical to the census above), and the
existing pins stay green. No default of any config key changed; the description text of `ShotAcousticsLab/MetalWalls`, `MetalCeiling`,
`MetalFloor`, `MetalClutter` now names the Mi-26 (text only, the saved values are untouched).
The alternative would be IndoorMetalThick = Metal at any size, which also turns Bay5's room 27 into Metal; it was not done because
Bay5's released results stay unchanged.

### RoomReverb mixer categories (can two rooms of one type get different categories?)

No. `RoomReverb.TableHandler` gives each room of the active group `isolated ? 8 : outdoor ? 67 : 4` by FLAGS (not type bits) and
refuses the graph ("conflicting mixer room category") when a type gets two. Bowl: type 512 (4 rooms) -> 4, 1024 (3) -> 4, 4 -> 4,
32768 -> 4, 65536 -> 4, 64 -> 67; no isolated room, no type 0, no conflict. Replicated offline on all four graphs: no conflict anywhere
(Equator, Bay5, AirPit, Bowl); the mixer_effects table is complete for the three categories (26 parameters, each with room types 4, 8
and 67, no duplicate). The shot rule's Outdoor decision (flag OR type bit) and RoomReverb's outdoor category (flag only) select
the same rooms in all four graphs (a room carrying an outdoor type bit without the flag would diverge; none exists). Both are
pinned for Bowl by `BowlRoomTypesMapToOneMixerCategoryEachAndTheShotRuleAgreesWithIt`, with a text pin on the RoomReverb line
(`RoomReverbStillDerivesItsMixerCategoryFromTheIsolatedAndOutdoorFlagsOnly`).

### Tests

`RoomShotAcousticsModelTests`: 78 model tests + 13 wiring tests (91; the file had 76 before). New: the Bowl fixture of the 11 active
rooms (runtime id, graph id, path, type, flag, full size, expected class and default preset), the stadium by flag and by type bit, the
Mi-26 preset, the 15 m edge (6 cases incl. Bay5's 9 x 10 and the 45 x 10 precedence), MetalThick outdoor-flagged and other types
unaffected by the size edge, Bay5's back room stays Shop, the FanZone Hall, the inactive source rooms (zero extents, 9.4 x 1.9 x 5.3),
the mixer-category check and its RoomReverb text pin. Existing tests are unchanged apart from comments and the new `IndoorFabric` constant.

### Host recipe (Bowl, GPU-2; first raid with a client that carries this rule)

Needs: a client build with the rule (restart EFT), `RoomShotAcoustics` on (default), BepInEx Debug lines collected
(`uibridge_log` filter `[ArenaMapPort][ShotAcoustics]`; the line is Debug), a Bowl raid with bots None. The line appears on each
room change once the raid is `Started`: `[ArenaMapPort][ShotAcoustics] arena_bowl: room=<room_id>/<type> class=<class> dims=...`.
Teleport targets are NavMesh polygon centres of `work/build/bowl/arena_bowl_navmesh.bundle` inside each room (head height 1.7 m
inside the room's area volumes, main component); confirm with `uibridge_teleport dryRun=true` first.

| Stand in | Teleport near | Expected log line |
|---|---|---|
| Mi-26 hull (room 4) | (1.1, 1.55, -33.5) | `room=4/IndoorMetalThick class=Metal dims=20.8x4.5x13.8 materials=Steel,Steel,Steel,Carpet,Steel,Steel clutter=0.70 model=ShoeboxRoom` |
| container (room 1) | (34.1, 0.21, -55.1) | `room=1/IndoorMetalThin class=Metal dims=8.2x2.7x7.0 ... model=ShoeboxRoom` (other containers: room 6 at (30.4, 0.20, 0.9), 6.5x2.6x4.9; room 10 at (21.9, 0.25, -2.5), 3.4x2.7x6.4) |
| office (room 3) | (0.9, 2.75, -13.4) | `room=3/IndoorWoodThick class=Shop dims=14.4x2.5x2.3 materials=GypsumBoard,GypsumBoard,AcousticTile,CeramicTile,GypsumBoard,GypsumBoard clutter=0.70 model=ShoeboxRoom` (room 11 at (-14.6, 0.20, -8.2), 2.3x2.4x12.1) |
| the field (room 8) | the east spawn (50.2, 0.1, -3.3) or any pitch spot outside the structures | `room=8/OutdoorCommon class=Outdoor dims=none materials=none clutter=none model=AcousticRayTracing` (the shipped model; the first line of the raid, and again after leaving a room) |

Order: field -> Mi-26 -> field shows Outdoor -> Metal -> Outdoor (model switches ShoeboxRoom <-> AcousticRayTracing). The old result would
read `room=4/IndoorMetalThick class=Shop dims=20.8x4.5x13.8 materials=GypsumBoard,...` (a client without the rule). Not testable by
walking: the Hall (room 5, FanZone, no NavMesh) and the Hospital rooms 2 and 9 (their NavMesh polygons sit on component 22, apart
from the main component 0; `dryRun` will probably answer `reachable=false`, like the tarp-building probes in bowl-release.md);
the Hospital class (Metal, thin metal) is the same as the containers anyway. An ear check of the Mi-26 against a container on the
Gunshots buses is the optional second step (`ReverbPulse`, see "Probe procedure"); no measurement exists for Bowl.

### Open points

- The 15 m edge is a design guess (nothing measured); whether the user wants Bay5's back room (and the Hospital tent) to follow is
  a one-line decision in `Classify`.
- Bowl's stadium is Outdoor, so the shipped model stays across the whole field; a stadium-sized tail would need an Outdoor
  preset, which no class has. Not requested.
- The Mi-26 box is axis aligned and 20.8 x 13.8 m wide although the hull is about 15 x 4 x 4 m.

## 2026-10-18 (evening) - host verification of the Mi-26 rule on GPU-2

Raid 3 of the 0.3.0 verification (Bowl, no bots), teleports followed by the `[ShotAcoustics]` line: spawn
`room=8/OutdoorCommon class=Outdoor model=AcousticRayTracing`; Mi-26 at (1.1, 1.6, -33.5) `room=4/IndoorMetalThick
class=Metal dims=20.8x4.5x13.8 materials=Steel,Steel,Steel,Carpet,Steel,Steel clutter=0.70 model=ShoeboxRoom`; container
at (34.1, 0.2, -55.1) `room=1/IndoorMetalThin class=Metal dims=8.2x2.7x7.0`; office at (0.9, 2.8, -13.4)
`room=3/IndoorWoodThick class=Shop dims=14.4x2.5x2.3 materials=GypsumBoard,GypsumBoard,AcousticTile,CeramicTile,...`;
field again `class=Outdoor`. The user's own raid earlier that day (MASCHINE11, 13:27:03, pre-rule client) logged the Mi-26
as `class=Shop` with the GypsumBoard set, so the rule's effect is the only difference. No reverb measurement was taken.

## 2026-11-14 - Hall table weakened: AcousticTile ceiling, clutter 0.6 (user: "ein wenig too much" under full-auto fire)

User, 2026-11-14: "unseren hall sollten wir aber nochmal abschwächen, gerade auf Equator & Airpit in der Haupthalle ist der ein wenig too
much sobald man mit vollautomatik schießt". ("Hall" is the German word for reverb.)

### Why automatic fire shows it (read from the EFT decompile, `D:/SPT41/Development/_decompiled`)

- A burst is ONE looping voice, not one voice per shot: `WeaponSoundPlayer.FireBullet` sets `_isFiring` for an automatic weapon and every
  later call only updates mixer, pitch and balance; the body clip is queued with end time 0 and `SuperBetterAudioQueue.Update` turns that
  into `superSource.Loop = (End == 0.0)`. The plugin's shared reverb therefore gets a continuous feed for the whole burst, and its level
  climbs towards a steady state that grows with the decay time, while a single shot only shows its own tail. The old Hall table (Concrete
  walls and ceiling) decays over 5 to 7 s (below), so a burst keeps building for seconds and rings on after it. This reading is inferred
  from the code and the measurement; nobody recorded a burst.
- The player's own gun presumably uses the loudest route (the third-person route was not measured today): first person switches the
  queue to stereo, and the two reverb-only helper sources of `ReverbSuperSource.PlayScheduled` take the group's stereo settings, which
  SPT's `settings.json` (`MetaXRAudioPluginSettings`, group Gunshots) gives as reverb send 0 dB against -6 dB for mono. The probe's route
  line of this session, `pulse route bus=GunshotsFirstPerson source=ReverbSuperSource spatialized=False acoustics=True reverbSendDb=-6.0
  helperSources=True`, shows the main source's own value, not the helpers'.

### Correction to the sections above: the probe logs amplitudes, not energies

`ReverbAudioProbe` logs `Math.Sqrt(sum |X|^2 / n)` of the 1500 Hz bin for the baseline and the two tail windows: an RMS amplitude. The
sections above call these numbers "energy"; the numbers themselves are right, but a ratio of two of them is `20 log10` in dB, and "the
late window keeps 85 % of the early energy" reads "85 % of the early amplitude". The tables below use 20 log10.

### Host measurement (lead, GPU-2, 2026-11-14 09:34-09:48, build `08369fc` = version 0.6.0, one EFT process)

Method as in "Probe procedure": plain PMC raid, bots None, DeveloperMode, `ReverbPulseBus = GunshotsFirstPerson`, `ReverbPulseVolume` 0.8,
one `ReverbPulse = true` per row, the lab keys set live with `uibridge_config_set persist=false` (nothing was saved to the host's .cfg, all
values were set back). Muted for the measurement: the map's `RainVolume` 0 and `AmbientGainDb` -12 (Air Pit also `AmbientVolumeOffset` 0)
and, new since the crowd exists, `Spectators/CrowdBed` false, `Audio diagnostics/SpectatorCalls` false and `Spectators/Reactions` false:
with the murmur on, the first pulse read `baseline=5.1819E-004 ... tailEarly=1.4735E-003 tailLate=9.5846E-004` (not used); with it off
the baseline is 8e-7 to 8e-6. `tailEarly` is the window 0.35-0.7 s after the pulse, `tailLate` 0.7-1.5 s; dB against the mean of the
same room's default rows.

Air Pit, main hall (room 17 `Main`, IndoorConcrete, class Hall, box 50.00 x 43.22 x 50.00), standing at (-3.2, 0.0, -12.1):

| Hall ceiling, clutter (walls Concrete, floor CeramicTile, cap 50) | tailEarly | tailLate | early dB | late dB | log time |
|---|---|---|---|---|---|
| Concrete, 0.4 (the table until today) | 9.68e-4 | 5.40e-4 | 0 | 0 | 09:35:03 |
| same | 9.37e-4 | 5.75e-4 | 0 | 0 | 09:35:10 |
| AcousticTile, 0.4 | 6.02e-4 | 2.88e-4 | -4.0 | -5.8 | 09:35:24 |
| Concrete, 0.8 | 5.18e-4 | 3.14e-4 | -5.3 | -5.0 | 09:35:32 |
| Concrete, 1.0 | 4.08e-4 | 2.26e-4 | -7.3 | -7.9 | 09:35:40 |
| AcousticTile, 0.8 | 3.26e-4 | 1.58e-4 | -9.3 | -11.0 | 09:35:48 (baseline 1.1e-4: repeated) |
| same | 3.31e-4 | 1.48e-4 | -9.2 | -11.6 | 09:35:55 |
| AcousticTile, 1.0 | 2.24e-4 | 1.15e-4 | -12.5 | -13.7 | 09:36:09 |
| Concrete, 0.4, cap 30 (box 30 x 30 x 30) | 1.87e-3 | 1.10e-3 | +5.9 | +5.9 | 09:36:38 |
| `Audio (arena_airpit)/RoomShotAcoustics` false | 2.87e-6 | 2.35e-6 | (floor) | (floor) | 09:36:23 |

Equator, main hall (room 19 `Main`, IndoorConcrete, class Hall, box 50.00 x 24.72 x 50.00), standing at (0.0, 0.2, 2.0):

| Hall ceiling, clutter | tailEarly | tailLate | early dB | late dB | log time |
|---|---|---|---|---|---|
| Concrete, 0.4 (the table until today) | 1.37e-3 | 6.76e-4 | 0 | 0 | 09:38:01 |
| same (baseline 2.1e-5, a background sound: not used) | 1.96e-3 | 9.62e-4 | | | 09:38:26 |
| same | 1.35e-3 | 6.69e-4 | 0 | 0 | 09:38:36 |
| same | 1.30e-3 | 7.19e-4 | 0 | 0 | 09:46:59 |
| Concrete, 0.6 | 9.36e-4 | 5.31e-4 | -3.1 | -2.3 | 09:47:05 |
| same | 9.27e-4 | 5.40e-4 | -3.2 | -2.1 | 09:47:12 |
| AcousticTile, 0.4 | 8.64e-4 | 3.42e-4 | -3.8 | -6.1 | 09:39:03 |
| same | 8.06e-4 | 3.70e-4 | -4.4 | -5.4 | 09:47:44 |
| AcousticTile, 0.5 | 6.92e-4 | 3.22e-4 | -5.7 | -6.6 | 09:47:35 |
| **AcousticTile, 0.6 (chosen)** | 5.90e-4 | 2.61e-4 | -7.1 | -8.4 | 09:47:22 |
| same | 5.88e-4 | 2.77e-4 | -7.2 | -7.9 | 09:47:27 |
| AcousticTile, 0.8 | 4.79e-4 | 1.94e-4 | -8.9 | -11.0 | 09:38:46 |
| AcousticTile, 1.0 | 3.49e-4 | 1.50e-4 | -11.7 | -13.2 | 09:38:54 |

References: Air Pit 9.5e-4 / 5.6e-4, Equator 1.34e-3 / 6.88e-4 (means of the used default rows). Repeats agree within about 0.7 dB.

Reading:

- Equator's hall is louder than Air Pit's under the same table (early +3.0 dB, late +1.8 dB): its box is lower (24.7 m against 43.2 m).
  A smaller box is LOUDER (cap 30: +5.9 dB in both windows), so the size cap is no lever for "weaker".
- With the feature off the tail is at the noise floor: the ported maps have no gunshot reverb at all without the shoebox.
- The two levers act differently. A single-exponential read of the two windows (`E(t) = E0 exp(-kt)` for the energy, the squared
  amplitude, fitted to the window means; a crude model, the first 0.35 s are extrapolated, and nothing below it was measured as such) gives a decay time of 7.0 s (Air Pit) and 5.4 s (Equator) for the old table, 4.8 s and 4.1 s
  with the AcousticTile ceiling, and no shorter decay with clutter alone (Air Pit Concrete 0.8: 7.4 s against 7.0 s; Equator Concrete 0.6: 6.6 s against 5.4 s, a
  difference the two-window fit cannot be trusted with): the ceiling shortens the decay, clutter lowers the level and does not shorten it.
- The same read gives the steady reverb level under a continuous feed (`E0 / k`: the tails of an incoherent feed add in energy; the
  automatic-fire case, modelled, no burst was recorded), in dB against the old table:

| Hall ceiling, clutter | Air Pit: decay, build-up | Equator: decay, build-up |
|---|---|---|
| AcousticTile, 0.4 | 4.8 s, -3.6 dB | 4.1 s, -3.6 dB |
| AcousticTile, 0.5 | | 4.6 s, -5.5 dB |
| AcousticTile, 0.6 | | 4.5 s, -6.8 dB |
| AcousticTile, 0.8 | 4.6 s, -8.8 dB | 3.8 s, -8.2 dB |
| AcousticTile, 1.0 | 5.4 s, -12.4 dB | 4.2 s, -11.2 dB |
| Concrete, 0.8 / 0.6 | 7.4 s, -5.3 dB (0.8) | 6.6 s, -3.3 dB (0.6) |

### Decision (lead; the new code defaults, pinned in `RoomShotAcousticsModelTests`)

| | Walls | Ceiling | Floor | Clutter |
|---|---|---|---|---|
| Hall (2026-11-14) | Concrete | **AcousticTile** (was Concrete) | CeramicTile | **0.6** (was 0.4) |
| Shop (unchanged) | GypsumBoard | AcousticTile | CeramicTile | 0.7 |
| Metal (unchanged) | Steel | Steel | Carpet | 0.7 |

`HallMaxDimension` stays 50. Why this step: the user called the hall "a little too much", so the middle of the measured range: the
tail windows about 7 dB (early) and 8 dB (late) lower, the automatic-fire build-up about 7 dB lower, the decay a sixth to a third
shorter (Equator 5.4 -> 4.5 s; Air Pit 7.0 s -> between its measured 4.8 s at 0.4 and 4.6 s at 0.8). The ceiling alone (about -4 / -6 dB,
build-up -3.6 dB) was judged too small a step to be noticed without an A/B, the 0.8 step (about -9 / -11 dB) more than "a little". The
hall still decays more than twice as long as a shop (the GypsumBoard shop of round 2 reads 1.8 s, Equator's `adik` 2.2 s), but its level
is no longer above a shop's: Equator's hall now reads 5.9e-4 / 2.7e-4 against `adik`'s 1.26e-3 / 3.0e-4 of 2026-10-16 (that row had a
1.4e-4 baseline). Still INVENTED data. **Nobody has listened to the new table yet.**

Live steps for the ear (developer keys, no rebuild): `ShotAcousticsLab/HallClutter` 0.8 is the next step down (about -9 / -11 dB),
0.4 the step back up (about -4 / -6 dB); `HallCeiling = Concrete` with `HallClutter = 0.4` is the old table.

### The Hall class is eight rooms on six maps (one table for all)

Equator `Main` (the atrium, 54.8 x 24.7 x 59.7) and `UpRoof` (the 40 m service corridor); Air Pit `Main` (111.4 x 43.2 x 96.4) and
`Start1` (the passport hall); Bay5 `SpawnBuilding1` (44.0 x 9.6 x 21.6, the loudest room of the 2026-10-16 table: 3.8e-3 / 3.3e-3);
Bowl `FanZone`; Iceberg `coffe_2`; Chop Shop `MainRoom` (57.5 x 22.7 x 46.7). The user named Equator's and Air Pit's main halls; the
other six rooms change with them. Chop Shop's hall is part of the map the user listened to and approved on the same day ("chop shop
habe ich mir angehört, passt alles"), before this change.

### Saved values: `ShotAcousticsLab/HallDefaultsRevision`

The lab keys are bound through `Settings`, so a developer .cfg (GPU-2, the user's own installs) and an old developer archive hold the old
table as explicit values. Hard rule (AGENTS.md): no silent default change. `RoomShotHallDefaults` (Unity-free, in
`RoomShotAcousticsModel.cs`) is revision 1: the hall table moves as a SET, exactly a saved Concrete / Concrete / CeramicTile / 0.4 at
revision 0 moves once to the new table (ceiling and clutter are written), a table with any tuned value stays entirely. The hidden int key
`ShotAcousticsLab/HallDefaultsRevision` records it; one Debug line `[ShotAcoustics] config re-seeded to hall defaults revision 1 (...)`.
In player mode the move happens in memory at every start (the archive is never rewritten), as with the other revision keys. A player
without a developer file simply gets the new code defaults.

### Not done, options

- A burst-only trim: a Harmony prefix on `ReverbSuperSource.PlayScheduled` could lower the Gunshots reverb send on ported maps and add a
  further trim while `Loop` is true (an automatic burst), which would keep the single-shot tail and weaken only the build-up. Not built;
  an option if single shots should keep more hall than bursts.
- `MetaXRAudioNativeInterface.Interface.SetSharedReverbWetLevel(float)` exists and EFT never calls it: a process-global wet level,
  untested, and it would need the same restore discipline as the model (see the Meta XR trap in AGENTS.md). Not used.
- Walls are the lever for a still shorter decay; no wall material was measured today.
- Not measured: the third-person route (other shooters), an actual burst, the passport hall, and before the build the other maps' halls.

### Host verification of the build (lead, GPU-2, 2026-11-14 10:16-10:25, client DLL of commit `7dba143`)

Deploy: dry run `0 new, 1 to overwrite, 395 already identical` (the client DLL; the server DLL was not redeployed: its code is unchanged
and a rebuilt file would have asked for a server restart, so the deployed `08369fc` server DLL was put back into `dist/`), then
`[deploy-to-spt] OK -- 396 file(s) copied into D:\SPT41`, `deploy-wait: 2 file(s) identical on the host after 14.1 s`, `gpu-run restart`:
`ok 74 s ... 16/16 identical`. One EFT process (started 10:16:09), three plain PMC raids with bots None and the hideout:

- **The re-seed ran once** (the BepInEx log file, before UiBridge records): `10:16:08.304 [Debug maschine-ArenaMapPort] [ShotAcoustics]
  config re-seeded to hall defaults revision 1 (2 value(s) moved; hall=Concrete/AcousticTile/CeramicTile clutter=0.6)`. The host's
  configuration afterwards (`uibridge_config`): `HallCeiling = AcousticTile (default AcousticTile ...)`, `HallClutter = 0.6 (default 0.6;
  0–1; Single)`, `HallDefaultsRevision = 1 (default 0; Int32) · hidden, protected`, walls Concrete, floor CeramicTile, cap 50. GPU-2's
  developer .cfg held the untouched old table, so this is the "moves" case; the "tuned table stays" case is unit-tested only.
- **Old against new in the same raid** (same method and muting as above; "old" is the previous table set live with
  `uibridge_config_set persist=false` and set back afterwards, "new" the shipped defaults):

| Room | Table | tailEarly / tailLate per pulse (log time) |
|---|---|---|
| Air Pit `Main` (room 17, 50.0 x 43.2 x 50.0), at (-3.2, 0.0, -12.1) | new | 4.40e-4 / 2.00e-4 (10:17:42), 4.54e-4 / 2.01e-4 (10:17:48) |
| | old | 9.39e-4 / 5.93e-4 (10:17:56) |
| Equator `Main` (room 19, 50.0 x 24.7 x 50.0), at (0.0, 0.2, 2.0) | new | 5.86e-4 / 2.69e-4 (10:19:14), 6.04e-4 / 2.51e-4 (10:20:59), **8.33e-4 / 3.78e-4** (10:21:04), 5.92e-4 / 2.65e-4 (10:21:16) |
| | old | **1.89e-3 / 1.02e-3** (10:19:22), **1.73e-3 / 1.00e-3** (10:19:34), 1.24e-3 / 6.62e-4 (10:20:16), 1.20e-3 / 7.16e-4 (10:20:23), 1.31e-3 / 7.19e-4 (10:20:28) |
| Bay5 `SpawnBuilding1` (room 16, IndoorBrick, 44.0 x 9.6 x 21.6), at (40.0, -3.8, -3.4) | new | 1.18e-3 / 5.97e-4 (10:22:49), 1.16e-3 / 6.01e-4 (10:22:54), 1.11e-3 / 5.82e-4 (10:22:58) |
| | old | 2.67e-3 / 2.32e-3 (10:23:07), **3.92e-3 / 3.20e-3** (10:23:12, baseline 5.5e-5: the previous tail still rings), 2.67e-3 / 2.25e-3 (10:23:19) |

  Room lines: `arena_airpit: room=17/IndoorConcrete class=Hall dims=50.0x43.2x50.0 materials=Concrete,Concrete,AcousticTile,CeramicTile,
  Concrete,Concrete clutter=0.60 model=ShoeboxRoom` (10:18:02), `arena_bay5: room=16/IndoorBrick class=Hall dims=44.0x9.6x21.6
  materials=Concrete,Concrete,AcousticTile,CeramicTile,Concrete,Concrete clutter=0.60 model=ShoeboxRoom` (10:22:36).
- **The probe reads in two groups.** The bold pulses are about 3 dB (a factor 1.4, a doubling of energy) above the others in both windows,
  under either table (early / late: Equator old 3.1 / 3.1 dB, new 3.0 / 3.1 dB, Bay5 old 3.3 / 2.9 dB); five of the day's 41 pulses (four
  of the 18 of this verification): three of Equator's nine old-table pulses, one of its six new-table pulses, one of Bay5's six, none of
  Air Pit's 13, with clean baselines. The
  row of 09:38:26 that the table above sets aside as "a background sound" was probably the same thing. The cause was not looked for (a guess: the
  weapon queue's two reverb helper sources). Consequence for every table in this document: one pulse is not a measurement; take three or
  more and compare the lower group with the lower group. Within a group the pulses agree within about 1.2 dB.
- **Result** (means of the lower group, the morning's pulses of the same tables included; decay and build-up from the single-exponential
  read):

| Room | old early / late | new early / late | early | late | decay | automatic-fire build-up |
|---|---|---|---|---|---|---|
| Air Pit `Main` | 9.48e-4 / 5.69e-4 | 4.47e-4 / 2.00e-4 | -6.5 dB | -9.1 dB | 7.2 -> 4.4 s | -6.0 dB |
| Equator `Main` | 1.30e-3 / 6.94e-4 | 5.92e-4 / 2.65e-4 | -6.8 dB | -8.4 dB | 5.8 -> 4.4 s | -6.4 dB |
| Bay5 `SpawnBuilding1` | 2.67e-3 / 2.29e-3 | 1.15e-3 / 5.93e-4 | -7.3 dB | -11.7 dB | about 25 -> 5.5 s | -9.5 dB |

  Equator's higher group gives the same differences (-7.0 / -8.4 dB). These means pool the morning's and the verification's pulses, so
  the old-table decay reads 7.2 s and 5.8 s here against 7.0 s and 5.4 s in the "Reading" above, Equator's new decay 4.4 s against 4.5 s and
  its build-up -6.4 dB against -6.8 dB: that is the scatter of the method, and "about 7 dB" in the decision paragraph is "about 6 dB" by
  this table. Decay and build-up are modelled from the two windows; only the windows are measured. Bay5's spawn building, a low 44 x 22 m box that barely decayed under
  the concrete ceiling, gains the most; it is the south team's start building.
- **Acceptance of the client build:** `warnings 0; errors 0` for the Air Pit and Bay5 raids and `warnings 0 from mods, 4 Unity Log; errors
  0` for Equator in gpu-run's raid lines, no ArenaMapPort line at Warning or above in the whole process, the hideout opened in 5.9 s
  (10:24), `16 identical (VM build = host disk = loaded)`. All temporary values were set back (`persist=false`, nothing but the plugin's own
  re-seed reached the host's .cfg).
- **Not verified:** nobody has listened to the new table (the user's ear decides); an actual burst; the third-person route; the other five
  Hall rooms (Equator `UpRoof`, Air Pit `Start1`, Bowl `FanZone`, Iceberg `coffe_2`, Chop Shop `MainRoom`), which get the same table by
  the same code path; a developer file with a tuned hall table on a real install.

## 2026-11-14 (2) - Hall table, revision 2: the long ringing (ConcreteBlock walls, AcousticTile ceiling, clutter 0.2)

User, 2026-11-14, after listening to the table of `7dba143` (Concrete / AcousticTile / CeramicTile, clutter 0.6): "habe ich mir angehört,
ich glaube es ist eher das lange nachklingen das wir nochmal überarbeiten sollten". So the morning's step lowered the level and left what
he actually minds: how long a hall rings on.

### The probe got a later window and a tone frequency

`ReverbAudioProbe` (developer only): a third tail window `tailFar`, 1.5-3.0 s after the pulse (the pulse now takes 3.1 s instead of 1.6 s),
and the key `Audio diagnostics/ReverbPulseFrequency` (default 1500 Hz, 100-8000, read when a pulse starts; tone and readback use it). The
result line ends `... tailEarly=<e> tailLate=<e> tailFar=<e> freq=<n> blocks=a/b/c/d; output includes background audio`. Every number
recorded before this section is a 1500 Hz number without a far window. Two things to know when using it: at 400 Hz the muted host's
baseline is about 3e-5 (1500 Hz: about 2e-6), so a `tailFar` near that is the floor, not the room; and Air Pit has a background sound at
400 Hz that `AmbientGainDb` -12 does not remove (baselines of 1e-4 to 3e-3): set `Audio (arena_airpit)/AmbientRangeMultiplier` to 1 for
the measurement as well.

### Host measurement (lead, GPU-2, 2026-11-14 10:56-11:06, the probe build on top of `8755049`, lab keys set live, all values set back)

Method and muting as in the section above; one EFT process, raids Equator, Air Pit, Equator, bots None. `S0` is the table of `7dba143`,
`ORIG` the table of 0.5.0 (Concrete ceiling, clutter 0.4). Columns: walls / ceiling / clutter (floor CeramicTile, cap 50 throughout);
amplitudes early / late / far; "decay" is the plain slope between the window centres (0.525 s, 1.1 s, 2.25 s), from early to far where far
stands clear of the floor, else from early to late. The raw pulses with their log times, the high-group pulses included, are in the table
below the summary.

Equator `Main` (room 19, 50.0 x 24.7 x 50.0), at (0.0, 0.2, 2.0):

| Table | 1500 Hz early / late / far | decay | 400 Hz early / late / far | decay |
|---|---|---|---|---|
| ORIG: Concrete / Concrete / 0.4 | (morning: 1.30e-3 / 6.94e-4, no far window) | | 2.13e-3 / 1.41e-3 / 8.60e-4 (one pulse) | 13 s |
| S0: Concrete / AcousticTile / 0.6 | 5.27e-4 / 2.61e-4 / 4.29e-5 | 4.8 s | 9.54e-4 / 5.58e-4 / 2.92e-4 | 10 s |
| ConcreteBlockPainted / AcousticTile / 0.6 | 4.77e-4 / 1.87e-4 / 2.62e-5 | 4.1 s | | |
| Mud / AcousticTile / 0.6 | 2.94e-4 / 8.85e-5 / floor | 3.3 s | 3.29e-4 / 1.01e-4 / floor | 3.4 s |
| Vent / AcousticTile / 0.6 | 2.01e-4 / 4.75e-5 / floor | 2.8 s | | |
| ConcreteBlock / AcousticTile / 0.6 | 1.90e-4 / 4.49e-5 / floor | 2.8 s | | |
| ConcreteBlock / Concrete / 0.6 | 2.65e-4 / 7.82e-5 / floor | 3.3 s | | |
| ConcreteBlock / Concrete / 0.2 | 5.15e-4 / 1.47e-4 / 6.7e-6 | 2.7 s | 5.04e-4 / 1.57e-4 / floor | 3.4 s |
| **ConcreteBlock / AcousticTile / 0.2 (chosen)** | 3.89e-4 / 7.89e-5 / floor | 2.5 s | 3.18e-4 / 8.17e-5 / floor | 2.9 s |

At 4000 Hz (one pulse each): S0 9.58e-5 / 4.16e-5 / 9.27e-6 (5.1 s), ConcreteBlock / Concrete / 0.2 9.26e-5 / 2.36e-5 / 2.12e-6 (3.2 s).
The unchanged Shop table in the spawn room (room 5, 29.0 x 4.1 x 12.7) at 400 Hz: 1.12e-3 / 1.94e-4 / floor (2.3 s): the shops have no
low-end ring.

Air Pit `Main` (room 17, 50.0 x 43.2 x 50.0), at (-3.2, 0.0, -12.1):

| Table | 1500 Hz early / late / far | decay | 400 Hz early / late / far | decay |
|---|---|---|---|---|
| S0: Concrete / AcousticTile / 0.6 | 4.54e-4 / 1.89e-4 / 4.02e-5 | 4.9 s | 8.16e-4 / 5.11e-4 / 1.82e-4 (one pulse) | 7.9 s |
| ConcreteBlock / Concrete / 0.6 | 3.00e-4 / 8.19e-5 / floor | 3.1 s | | |
| ConcreteBlock / Concrete / 0.2 | 5.94e-4 / 1.60e-4 / floor (and two pulses at 7.9e-4 / 2.2e-4 / 2.2e-5) | 3.0 to 3.3 s | 5.80e-4 / 2.63e-4 / 4.53e-5 (far within a factor 4 of its baselines) | 4.7 to 5.0 s |
| **ConcreteBlock / AcousticTile / 0.2 (chosen)** | 3.79e-4 / 7.86e-5 / 3.89e-6 | 2.6 s | 3.34e-4 / 1.08e-4 / floor (one pulse) | 3.5 s |

Raw pulses, early / late / far (log time; "high" = the 3 dB group):

- Equator 1500 Hz. S0: 8.38e-4 / 4.21e-4 / 7.10e-5 (10:56:40, high), 5.27e-4 / 2.61e-4 / 4.29e-5 (10:56:48), 8.49e-4 / 3.79e-4 / 7.07e-5
  (10:56:56, high). ConcreteBlock / AcousticTile / 0.6: 1.90e-4 / 4.49e-5 / 2.73e-6 (10:57:07), 2.75e-4 / 5.60e-5 / 2.68e-6 (10:57:15, high).
  ConcreteBlockPainted: 4.89e-4 / 1.86e-4 / 2.65e-5 (10:57:24), 4.65e-4 / 1.87e-4 / 2.58e-5 (10:57:31). Mud: 2.94e-4 / 8.85e-5 / 4.06e-6
  (10:58:02), 4.06e-4 / 1.18e-4 / 6.09e-6 (10:58:10, high). Vent: 2.01e-4 / 4.75e-5 / 2.97e-6 (10:58:17). ConcreteBlock / Concrete / 0.6:
  2.76e-4 / 7.61e-5 / 4.15e-6 (10:58:25), 2.53e-4 / 8.02e-5 / 4.22e-6 (10:58:34); / 0.2: 5.04e-4 / 1.43e-4 / 7.01e-6 (10:58:47), 5.25e-4 /
  1.51e-4 / 6.38e-6 (10:58:55). Chosen: 4.07e-4 / 7.88e-5 / 2.92e-6 (11:05:42), 3.70e-4 / 7.89e-5 / 2.99e-6 (11:05:50).
- Equator 400 Hz. ConcreteBlock / Concrete / 0.2: 4.91e-4 / 1.55e-4 / 4.4e-5 (10:59:03), 5.16e-4 / 1.58e-4 / 3.9e-5 (10:59:10). S0: 9.40e-4 /
  5.61e-4 / 2.90e-4 (10:59:19), 9.68e-4 / 5.55e-4 / 2.94e-4 (10:59:30). ORIG: 2.13e-3 / 1.41e-3 / 8.60e-4 (10:59:38). Mud / AcousticTile /
  0.6: 3.29e-4 / 1.01e-4 / 3.8e-5 (10:59:48). Chosen: 4.42e-4 / 1.21e-4 / 2.89e-5 (11:05:57, high), 3.18e-4 / 8.17e-5 / 2.95e-5 (11:06:04).
  Shop room 5: 1.12e-3 / 1.94e-4 / 3.4e-5 (11:06:26).
- Air Pit 1500 Hz. S0: 6.82e-4 / 2.97e-4 / 5.77e-5 (11:01:29, baseline 2.0e-5, high), 4.54e-4 / 1.89e-4 / 4.02e-5 (11:01:36, baseline
  7.0e-7). ConcreteBlock /
  Concrete / 0.2: 7.88e-4 / 2.26e-4 / 2.18e-5 (11:01:44), 7.96e-4 / 2.22e-4 / 2.30e-5 (11:01:52), 5.94e-4 / 1.60e-4 / 2.64e-5 (11:02:43,
  baseline 2.0e-5); / 0.6: 3.00e-4 / 8.19e-5 / 2.29e-5 (11:02:06, baseline 2.8e-5), 4.27e-4 / 1.17e-4 / 3.03e-5 (11:02:14, baseline 2.2e-5,
  high). Chosen: 3.67e-4 / 7.70e-5 / 3.84e-6 (11:04:00), 3.90e-4 / 8.01e-5 / 3.93e-6 (11:04:08).
- Air Pit 400 Hz. ConcreteBlock / Concrete / 0.2: three pulses lost to the background sound (baselines 2.96e-3, 3.2e-4, 1.0e-4), then with
  `AmbientRangeMultiplier` 1: 5.69e-4 / 2.54e-4 / 4.50e-5 (11:03:22), 5.90e-4 / 2.72e-4 / 4.56e-5 (11:03:30). S0: 8.16e-4 / 5.11e-4 / 1.82e-4
  (11:03:39). Chosen: 3.34e-4 / 1.08e-4 / 1.48e-5 (11:03:52, baseline 1.6e-5).

Which of Air Pit's ConcreteBlock / Concrete / 0.2 pulses at 1500 Hz are the high group is not settled (7.9e-4 twice, 5.9e-4 once).

### Reading

- **The lingering is a low-end problem.** At 400 Hz S0 still holds 0.31 (Equator) and 0.22 (Air Pit) of its early amplitude in the
  1.5-3 s window, a decay of about 10 s and 8 s, against 0.08 and 0.09 at 1500 Hz (4.8 s and 4.9 s). ORIG read 13 s at 400 Hz in Equator in the one pulse of this session (9.2 s from two
  pulses in the verification below; 13.6 s in Air Pit there): revision 1 made the low end about 7 dB quieter at first and left Equator's
  ringing about where it was. Why the morning's clutter step did so little for it is INFERRED, not measured: the plugin hands the clutter
  factor to its native side as four band values `[c/8, c/4, c/2, c]` (decompile, `MetaXRAudioRoomAcousticProperties.Update`; what the
  native code does with them is not in the decompile), and the material tables put the low end in band 0 (Carpet 0.99 / 0.91 / 0.58 /
  0.35), so the lowest band gets an eighth of the factor. The clutter step was worth about 3 dB at 1500 Hz in the morning; no clutter
  A/B was run at 400 Hz. The 1500 Hz probe of the morning could not see the low end at all.
- **Walls are the broadband lever.** The plugin's reflection coefficients for the four bands: Concrete 0.99 / 0.98 / 0.98 / 0.98,
  ConcreteBlockPainted 0.90 / 0.94 / 0.92 / 0.92, Mud 0.84 / 0.73 / 0.79 / 0.85, Vent 0.85 / 0.62 / 0.70 / 0.80, ConcreteBlock 0.64 / 0.65 /
  0.67 / 0.79, AcousticTile 0.49 / 0.36 / 0.34 / 0.50. ConcreteBlock walls bring the decay to about 3 s with a Concrete ceiling and to about
  2.5 s with the AcousticTile ceiling, at 1500 Hz and (within a second) at 400 Hz; a painted block wall is too close to concrete (4.1 s).
  A Carpet floor would only take the highs (0.99 at the lowest band) and was not tried.
- **Clutter is now the level control of the highs.** With absorbing walls the tail is quieter as a whole; clutter 0.2 instead of 0.6 gives
  about 6 dB back at 1500 Hz and leaves the decay about where it is (the probe reads 0.3 to 0.6 s less at 0.2: 2.5 s against 2.8 s with
  the AcousticTile ceiling, 2.7 s against 3.3 s with the Concrete one).

### Decision (lead; the code defaults, pinned in `RoomShotAcousticsModelTests`)

| | Walls | Ceiling | Floor | Clutter |
|---|---|---|---|---|
| Hall (revision 2, 2026-11-14) | **ConcreteBlock** (was Concrete) | AcousticTile | CeramicTile | **0.2** (0.6 in revision 1, 0.4 in 0.5.0) |
| Shop (unchanged) | GypsumBoard | AcousticTile | CeramicTile | 0.7 |
| Metal (unchanged) | Steel | Steel | Carpet | 0.7 |

Against S0 (what the user heard) the chosen table reads, early / late / far: Equator 1500 Hz -2.6 / -10.4 dB / at the floor (at least
-23 dB), 400 Hz -9.5 / -16.7 dB / at the floor (at least -20 dB); Air Pit 1500 Hz -1.6 / -7.6 / -20.3 dB, 400 Hz -7.8 / -13.5 dB / at the
floor (at least -22 dB). In words: the first third of a second is about as loud as before in the mids and clearly leaner in the lows, and
from 1.5 s on the hall is 20 dB or more quieter than it was; the decay is about 2.5 s in the mids and 3 to 3.5 s in the lows, where it was
5 s and 8 to 10 s. In the low end the hall is still slower than a shop (2.3 s at 400 Hz in Equator's spawn room, one pulse); at 1500 Hz it now decays
about as fast as one (the GypsumBoard shop row of 2026-10-15, 5.6e-4 / 9.8e-5, is 2.3 s by the same slope reading), and its early level is
below a shop's. What is left of "a hall" is the low end and the size of the box; whether that is enough is a question for the ear. Why the shorter of the two candidates: the request names the ringing,
and the Concrete-ceiling variant keeps Air Pit's low end at about 5 s. Still INVENTED data. **Nobody has listened to this table yet.**

Live steps for the ear (developer keys, no rebuild): `ShotAcousticsLab/HallCeiling = Concrete` is the longer variant (about 3 s in the
mids, 3.4 to 4.7 s in the lows, and about 2 to 5 dB louder early); `HallClutter` 0.4 or 0.6 takes about 3 or 6 dB off the highs and
leaves the decay about where it is (0.3 to 0.6 s longer at 0.6 by the probe); `HallWalls = Concrete` with `HallClutter = 0.6` is the table of the morning.

### Saved values: revision 2 of `ShotAcousticsLab/HallDefaultsRevision`

`RoomShotHallDefaults.Revision` is 2. A saved hall table moves only when it is exactly the default of the revision the file was written
with: a revision-0 file (0.5.0) holding Concrete / Concrete / CeramicTile / 0.4, or a revision-1 file (written by the DLL of `7dba143`: GPU-2, logged below, and any other install that ran that DLL in developer mode)
holding Concrete / AcousticTile / CeramicTile / 0.6. Anything else is a tuned table and stays entirely, a revision-1
file that was set back to the 0.5.0 values by hand included. Revision 1 was never released.

### Host verification of revision 2 (lead, GPU-2, 2026-11-14 11:19-11:32, client DLL of commit `ee46a3e`)

Deploy: `0 new, 2 to overwrite, 394 already identical` (the client DLL and, by oversight, the server DLL that the test build had rebuilt:
same code, new bytes), `[deploy-to-spt] OK -- 396 file(s) copied into D:\SPT41`. The running server held its DLL, so EFT was closed, the
server restarted (`gpu-run server restart`: `ready after 13 s`), `deploy-wait: 2 file(s) identical on the host`, `gpu-run start`:
`16/16 identical`. .NET 3,313 of 3,313 (one run of the suite right after the build showed 3,312; which test failed was not captured, the
suite has a load-dependent throughput test, and the rerun was clean).

- **The re-seed ran once, from revision 1.** Process of 11:19:24 (the first start of the new DLL): `11:19:34.008 [Debug
  maschine-ArenaMapPort] [ShotAcoustics] config re-seeded to hall defaults revision 2 (2 value(s) moved; hall=ConcreteBlock/AcousticTile/
  CeramicTile clutter=0.2)`. The next process (11:22:19) logged no such line, and its configuration reads `HallWalls = ConcreteBlock
  (default ConcreteBlock ...)`, `HallCeiling = AcousticTile`, `HallFloor = CeramicTile`, `HallClutter = 0.2 (default 0.2; 0–1; Single)`,
  `HallDefaultsRevision = 2 (default 0; Int32) · hidden, protected`. The revision-0 case (a 0.5.0 file) and the tuned-table case are
  unit-tested only.
- **0.5.0's table against the shipped one, same raid** (three plain PMC raids, bots None, one process; "old" is Concrete / Concrete /
  CeramicTile / 0.4 set live with `persist=false` and set back; method and muting as above; means of the lower group; early / late / far):

| Room | Hz | old | new | early | late | far | decay old -> new |
|---|---|---|---|---|---|---|---|
| Equator `Main` | 1500 | 1.30e-3 / 6.40e-4 / 1.96e-4 | 4.09e-4 / 6.66e-5 / 3.1e-6 (floor) | -10.0 dB | -19.6 dB | at least -36 dB | 6.3 s -> about 2.3 s |
| | 400 | 2.57e-3 / 1.68e-3 / 7.04e-4 | 3.64e-4 / 1.13e-4 / floor (4.6e-5) | -17.0 dB | -23.4 dB | at least -24 dB | 9.2 s -> 3.4 s |
| Air Pit `Main` | 1500 | 9.66e-4 / 5.48e-4 / 1.59e-4 | 3.69e-4 / 8.45e-5 / 3.78e-6 (see the note) | -8.4 dB | -16.2 dB | -32.5 dB | 6.6 s -> 2.6 s |
| | 400 | 1.41e-3 / 9.61e-4 / 5.88e-4 | 3.30e-4 / 9.62e-5 / 1.99e-5 (far within a factor 2 of its baseline) | -12.6 dB | -20.0 dB | at least -29 dB | 13.6 s -> about 3.2 s |
| Bay5 `SpawnBuilding1` | 1500 | 2.67e-3 / 2.29e-3 (the morning's pulses) | 2.80e-4 / 6.7e-5 (floor) | -19.6 dB | at least -30 dB | | |
| | 400 | 4.96e-3 / 4.03e-3 / 2.15e-3 (one pulse) | 2.71e-4 / floor (6.0e-5) | -25.3 dB | at least -36 dB | at least -31 dB | 14 s -> under 3 s |

  Decay is the slope from early to far where far stands clear of the baseline, else from early to late (Equator and Bay5 new, Air Pit
  new at 400 Hz). Note on Air Pit new at 1500 Hz: only one of the verification's three pulses is in the lower group (3.50e-4; the other two
  read 4.96e-4 and 4.92e-4, 3.0 dB higher in all three windows), so the row pools it with the two live-key pulses of 11:04 (3.67e-4 and
  3.90e-4, same table); the single pulse alone gives -8.8 / -15.1 / -33.0 dB. Bay5's old 1500 Hz reference is the morning's raid; the one
  old pulse of this raid (4.11e-3, baseline 1.6e-4, probably the high group) would give -23 dB. The three halls now start at about the same
  level (2.8e-4 to 4.1e-4 at 1500 Hz), where 0.5.0 spread them over 9 dB.
- Raw pulses (early / late / far, log time). Equator 1500 Hz new: 4.22e-4 / 6.81e-5 / 3.42e-6 (11:24:11), 4.23e-4 / 6.61e-5 / 3.19e-6
  (11:24:19), 3.83e-4 / 6.57e-5 / 2.73e-6 (11:24:26); old: 1.97e-3 / 9.96e-4 / 2.95e-4 (11:24:34, high), 1.38e-3 / 6.92e-4 / 2.08e-4
  (11:24:41), 1.21e-3 / 5.88e-4 / 1.85e-4 (11:24:47). 400 Hz old: 2.59e-3 / 1.64e-3 / 6.92e-4 (11:24:57), 2.55e-3 / 1.72e-3 / 7.15e-4
  (11:25:03); new, after 12 s of silence: 3.38e-4 / 1.22e-4 / 4.6e-5 (11:25:29, baseline 3.8e-5), 3.77e-4 / 1.25e-4 / 1.6e-4 (11:25:36,
  baseline 8.6e-5, a background sound in the far window), 3.76e-4 / 9.33e-5 / 1.1e-4 (11:25:44, the same). Air Pit 1500 Hz new: 3.50e-4 /
  9.63e-5 / 3.56e-6 (11:27:06), 4.96e-4 / 1.37e-4 / 4.93e-6 (11:27:13, high), 4.92e-4 / 1.38e-4 / 5.15e-6 (11:27:20, high); old: 9.80e-4 /
  5.45e-4 / 1.58e-4 (11:27:29), 9.53e-4 / 5.50e-4 / 1.60e-4 (11:27:36). 400 Hz (with `AmbientRangeMultiplier` 1) old: 1.41e-3 / 9.61e-4 /
  5.88e-4 (11:27:44), 1.66e-3 / 1.07e-3 / 6.66e-4 (11:27:50, baseline 1.2e-4: the previous tail); new, after 14 s: 3.30e-4 / 9.38e-5 /
  1.68e-5 (11:28:16), 3.30e-4 / 9.86e-5 / 2.29e-5 (11:28:23). Bay5 (baselines 5e-5 to 6e-5 this time) 1500 Hz new: 2.84e-4 / 5.81e-5 /
  floor (11:29:44), 2.75e-4 / 7.60e-5 / floor (11:29:51); 400 Hz new: 2.71e-4 / 6.04e-5 / floor (11:30:03); 400 Hz old: 4.96e-3 / 4.03e-3 /
  2.15e-3 (11:30:11); 1500 Hz old: 4.11e-3 / 3.16e-3 / 7.3e-4 (11:30:19, baseline 1.6e-4, not used).
- Room lines: `arena_equator: room=19/IndoorConcrete class=Hall dims=50.0x24.7x50.0 materials=ConcreteBlock,ConcreteBlock,AcousticTile,
  CeramicTile,ConcreteBlock,ConcreteBlock clutter=0.20 model=ShoeboxRoom` (11:25:08), the same materials for `arena_airpit: room=17`
  (11:27:55) and `arena_bay5: room=16/IndoorBrick class=Hall dims=44.0x9.6x21.6` (11:30:26).
- **Acceptance of the client build:** `warnings 0; errors 0` (Air Pit, Bay5) and `warnings 0 from mods, 4 Unity Log; errors 0` (Equator)
  in gpu-run's raid lines, no ArenaMapPort line at Warning or above in the process, the hideout opened in 6.9 s. All temporary values were
  set back.
- **Not verified:** nobody has listened to revision 2 (the user heard revision 1 and asked for a shorter ring); an actual burst; the
  third-person route; the other five Hall rooms; whether a hall that now decays in about 2.5 s still reads as a hall next to the shops
  (about 2 s) is a question for the ear, the longer live variant is `HallCeiling = Concrete`.
