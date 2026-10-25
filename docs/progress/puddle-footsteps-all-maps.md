# Puddle footsteps on Equator, Air Pit and Bowl (2026-10-25)

Status 2026-10-25: implemented on the build VM and covered by unit tests; **nothing of this has run in a raid yet** (the host checks at the
end are the lead's). It extends Bay5's puddle footsteps ([bay5-water.md](bay5-water.md), 2026-09-28) to the other three released maps, with
two additions the planning review asked for (a bounded cache rebuild and one more Debug line) and one per-map data field for Bowl
(`PuddleExtraSurfaces`). The same change set moves four edge spawn points; that part is in
[equator-booth-spawns.md](equator-booth-spawns.md) and [door-poses.md](door-poses.md) (both "2026-10-25").

Because `ArenaMapDescriptor.cs` lives in `ArenaMapPort.Shared`, which is compiled into BOTH the client and the server DLL, **both DLLs
change** (the server gains the two descriptor members and nothing else). Deploy both, check the host server DLL hash, restart the SPT
server; the regenerated `db/arena_equator/base.json` and `db/arena_bay5/base.json` need the same restart (and a deploy that is not
`-DllOnly`, because base.json is not a DLL).

## What the repair does (recap)

Arena's puddle decals have no collider, so the native footstep query (`Player.CalculateMovementSurface`, one downward raycast) reports the
floor beneath. `PuddleFootstepRepairs` is a Harmony postfix on that query: where the native result is a successful `Concrete`/`Asphalt`
(plus the map's extra surfaces, below) and the player's feet lie inside the triangles of a listed water decal, within 16 cm of its plane,
and the decal's shader coverage is at least 10 %, the result becomes `ESurfaceSound.Puddle`; EFT's existing Puddle bank does the rest. The
coverage is the native shader formula `saturate((MainTex.red + vertexAlpha * waterLevel - 1) * FadeStrength)`; the water level is the
global `_WaterLevel` for a rain-driven material (keyword `USERAIN`) and `_EditorWaterLevel` for a static one. No collider, no audio file, no
Harmony patch on anything but the one query. Oil decals share the shader and stay out by material identity.

## What changed

- **Descriptor** (`ArenaMapDescriptor.cs`, mirrored by the sidecars): `PuddleWaterMaterialNames` now for Equator, Air Pit and Bowl (Bay5
  unchanged) and the new optional `PuddleExtraSurfaces` (`BaseBallistic.ESurfaceSound` names as text, default empty = exactly
  Concrete/Asphalt; Bowl = `["Soil"]`). A map takes part when it lists at least one water material.
- **Sidecars**: `tools/maps/equator.json` (`70f045bb011600e7`, `0877f2335044042e`), `airpit.json` (`1b8546c701e1d1dc`,
  `0877f2335044042e`), `bowl.json` (`puddle_water_material_names` `0877f2335044042e`, `puddle_extra_surfaces` `Soil`). Unity's `JsonUtility`
  ignores the unknown fields, as it already does for Bay5's.
- **Client**: `PuddleFootstepRepairs.cs` no longer knows Bay5. The map comes from `ArenaMapRaid.CurrentMap` (a released map with a
  non-empty list, otherwise the postfix returns at once: vanilla maps, the hideout), the scenes from `map.AllSceneNames` (background
  scenes skipped) and the water materials from the descriptor; the load/unload resets apply to every owned scene. New
  `PuddleCacheGate.cs` holds the pacing rules (no Unity types, linked into the tests, below).
- **Config**: one developer switch per participating map, `Water (<key>)/PuddleFootsteps`, default true, live. Bay5's key and description
  are exactly those of 2026-09-28 (`Water (arena_bay5)`), so a stored value keeps applying; the other three bind the same key name in
  their own section (`Water (arena_equator)`, `Water (arena_airpit)`, `Water (arena_bowl)`; Bowl's text adds that Soil counts). Bound
  through `Plugin.Instance.Settings`, so they exist for developers only; no public key, `CompactConfiguration.IsPublic` and
  `MapConfigVisibility` are untouched.
- **Validator** (`validate_map_descriptors.py`): `puddle_errors` (shape of both fields: unpadded names, no duplicates, surfaces must be
  members of `BaseBallistic.ESurfaceSound` of EFT 0.16.9.5 and not `Concrete`/`Asphalt`/`Puddle`, extra surfaces need a water
  material) and a sidecar-versus-descriptor check of both fields for every catalog map.

## Census (extract packages, 2026-10-25)

Every water decal is a 4-vertex quad of mesh `2e93310068beccf4` (readable in the built scenes bundles; checked for Equator, Air Pit and
Bowl). "Active" counts the renderers `Rendered()` accepts; the cache holds one entry per active quad.

| Map | Material (id = name in the bundles) | Source name | Rain driven | Active quads | Scene(s) |
|---|---|---|---|---|---|
| Equator | `70f045bb011600e7` | puddle indoor static | no | 17 | Arena_Equator |
| Equator | `0877f2335044042e` | puddle | yes (`USERAIN`) | 21 | Arena_Equator |
| Air Pit | `1b8546c701e1d1dc` | Airpit_puddle_white | no | 14 | Main_hall 10, ShootOut 4 |
| Air Pit | `0877f2335044042e` | puddle | yes | 26 | Light 1, Main_hall 7, ShootOut 3, ShootOut_Light 14, TeamFight 1 |
| Bowl | `0877f2335044042e` | puddle | yes | 13 | Arena_Bowl |
| Bay5 | `70f045bb011600e7` | puddle indoor static | no | 45 cached (46 incl. one in the background scene) | unchanged |

Expected cache line per map (Debug): **Equator `surfaces=38 masks=2`, Air Pit `surfaces=40 masks=2`, Bowl `surfaces=13 masks=1`, Bay5
`surfaces=45 masks=1` (unchanged; 45 active surfaces and one mask on GPU-2, [bay5-water.md](bay5-water.md))**. The staged shader sidecars (`material-shaders.arena_<key>.json`) carry all
water materials as `Decal/Water Deferred Decal` with `USERAIN` on the rain material for Equator, Air Pit and Bowl, so the native shader is
what the cache finds once `ShaderRebind` has run.

Not listed, on purpose:

- Bay5 `25519639aa567c85` ("puddle benz", 7 renderers, two of them named `puddle_oil`): oil (ring/crater mask, opaque).
- Air Pit `823c859e2c5d11bc` ("City_puddle_atlas_OUTDOOR"): two runway decals, both inactive and off the map. Two further inactive Air Pit
  runway decals use mesh `1c85aa34fefde6dc` (1166 vertices, 11 submeshes) and are neither listed nor active, so they are never cached
  (an earlier note called every puddle mesh a 4-vertex quad; that is true of the listed ones only).
- Equator and Bowl have no oil decal.

## What lies under the wet areas, and the ground guard

The step raycast reports the collider under the feet. The census replays the native query (nearest hit below the capsule centre on the
Terrain, HighPolyCollider, TransparentCollider and Water layers) from sample points of each decal's wet mask (coverage at least 0.1)
that lie on the walkable play component of the built NavMesh, and counts the ballistic material it hits (sample counts, not areas;
"off-plane" is the census label for samples whose floor is more than 16 cm off the decal plane, so the 16 cm rule leaves them dry):

| Map | Material | Under the wet samples |
|---|---|---|
| Equator | static | Concrete 364, MetalThin 8, WoodThick 8 (+ off-plane 4) |
| Equator | rain | Concrete 104, WoodThick 31, WoodThin 6, Plastic 6, Fabric 4, MetalThin 3, GenericSoft 1 (+ off-plane 4) |
| Air Pit | static | Concrete 463, WoodThick 70, MetalThin 9, WoodThin 4, MetalThick 3, GenericSoft 2 (+ off-plane 140) |
| Air Pit | rain | Concrete 532, WoodThick 87, MetalThin 67, Asphalt 19, WoodThin 1, GenericSoft 1 (+ off-plane 30) |
| Bowl | rain | **Soil 16**, MetalThick 15, Plastic 11, WoodThin 7, Rubber 5, MetalThin 5, Glass 1 |
| Bay5 | static | Concrete 3436, Soil 158, MetalThick 46, MetalThin 9, ... (for reference) |

Decisions (lead, 2026-10-25):

- **Equator and Air Pit keep the Concrete/Asphalt guard.** The rain puddles there lie about 19.5 % (Equator) and 11.8 % WoodThick /
  9.1 % MetalThin (Air Pit) over wood and metal; those surfaces keep their native sounds (a wooden step under a puddle decal stays wooden),
  exactly as Bay5's soil decals keep `soil_walk_01`. Revisit only if a tester reports a dry-sounding puddle on a wooden walkway.
- **Bowl: `Soil` is an eligible ground** (`puddle_extra_surfaces`). `Bowl_floor_BALLISTIC_soil` is the whole Bowl ground; the
  MetalThick/Plastic/WoodThin/Rubber/MetalThin shares in the table are containers and rubber mats LYING ON the quads, not floor. With the
  Concrete/Asphalt guard alone, Bowl would stay silent and the "puddle footsteps on Bowl" request would be unmet. With Soil eligible, a
  step on bare ground inside a decal's mask becomes a puddle step; a step on a metal or plastic container on top keeps its own sound (its native
  surface is not Soil), and so does anything more than 16 cm above or below the decal plane. Review 2026-10-25: Rubber, Fabric and GenericSoft
  have no ground sound of their own in EFT's `BaseBallistic.Associate` (the field keeps its serialized default, normally Concrete), so a
  rubber mat inside a decal's mask can sound as puddle too; that is accepted, the 16 cm plane rule still applies.
- **No Bowl-house change** (not part of this request): the 20 `SPAWNS/Bear house (N)` BoxColliders of `Arena_Bowl_TeamFight_DesignStuff`
  stand on the 20 ArenaMode Bowl formation points and on the four port player spawns; fixing it would need a data-driven collider disable
  in `SpawnPlacementRepairs` fed by a descriptor field. Left as a separate lead decision.

## Cache pacing (`PuddleCacheGate`) and what logs

The cache is built lazily on the first eligible footstep of a raid by one scan over every `MeshRenderer` of the map's scenes (extract
sizes: Equator 39,398 objects / 14,957 renderers, Bay5 59,422 / 27,362, Air Pit 55,292 / 24,057, **Bowl 80,178 / 38,795**: about 30 ms per
55,000 objects on the main thread, so expect a single hitch of 20 to 60 ms per raid, the largest on Bowl). Two traps are closed:

- **No periodic scan.** The old code set `_dirty = waitingForRebind`, so ONE listed water renderer whose shader was not yet
  `Decal/Water Deferred Decal` repeated the full scan every second for as long as the player walked on concrete - the 1 Hz freeze trap of
  AGENTS.md. Now a full scan that still finds such a renderer is repeated at most `MaxFullScans = 5` times (one second apart); then the
  cache is accepted with the surfaces that do have the shader and one Debug line says so (`water cache of <key>: N listed water material
  slot(s) still lack shader 'Decal/Water Deferred Decal' after 5 scans; keeping the M surfaces that have it.`). A scene load or unload and
  the config toggle grant a fresh set of attempts. ShaderRebind finishes during the raid load, so on the shipped maps the first scan should find every
  shader; the bound is a safety net.
- **No silent wait.** If a scene of `AllSceneNames` is not loaded, the cache cannot build; today that path was silent. It now logs once per
  scene state (Debug): `water cache of <key> not built: scene '<name>' is not loaded.` and checks again each second (cheap, no scan).

Log lines (all Debug, source `maschine-ArenaMapPort`, tag `[PuddleFootsteps]`): `cached water surfaces=N masks=M colorSpace=Gamma ...`,
the two lines above, `... keeping native surface after inspection failure` (Warning, once, as before) and, with developer mode, F10's
`map=<key> enabled=... cached=... native=... result=... matched=... coverage=... red=... planeDistance=... path='...'`. No Info line.

## Tests

- `ArenaMapPort.Server.Tests/PuddleCacheGateTests.cs` (7 tests, new): attempt spacing, ready/retry/give-up, the bound of five scans (also
  as a one-minute simulation at 60 Hz with a renderer that never gets its shader: exactly 5 scans), fresh attempts after a reset, the
  once-only missing-scene report.
- `Bay5MapTests.PuddleDataMatchesTheSidecarAndTheExpectedLists` (theory over the four released maps: sidecar == registry == expected
  lists) and `ParkedMapsListNoPuddleDataAndTheSurfacesAreMembersOfTheNativeEnum` replace the Bay5-only test.
- `tools/tests/test_map_pipeline.py` `RegistryTests`: `test_puddle_sidecar_drift_is_rejected` (five single-side edits fail with
  `<field> mismatch`) and `test_puddle_field_shapes` (twelve malformed shapes rejected).
- `tools/tests/test_puddle_water_census.py` (3 tests, new; skipped where the staged `dist/.../material-shaders.arena_<stem>.json` is absent):
  listed + documented exclusions (Bay5 `puddle benz`, Air Pit's runway decals) == exactly the `Decal/Water Deferred Decal` materials of each
  package, an excluded id is never listed, and the rain-driven `puddle` keeps its `USERAIN` keyword on Equator, Air Pit and Bowl. A rebuilt
  package that brings an unclassified water material (oil or water?) fails until someone decides.
- `python tools/validate/validate_map_descriptors.py`: OK, now covering both fields.
- Results 2026-10-25 (Release): `dotnet test` 2296 passed, 0 failed, 0 skipped (the whole tree, including the sibling changes of the day);
  client Release build (`-p:ArenaMapPortDllOnly=true`) and server Release build 0 warnings, 0 errors.

## Host checks (lead)

Order: deploy (full, not `-DllOnly`: base.json changes), verify the host server DLL hash, restart the SPT server, restart EFT, then
`uibridge_status` (all plugins matched) before any raid.

1. **Plain raid per map, Debug lines**: `[PuddleFootsteps] cached water surfaces=38 masks=2` (Equator), `=40 masks=2` (Air Pit),
   `=13 masks=1` (Bowl), `=45 masks=1` (Bay5, unchanged). The line appears at the first footstep on eligible ground (Concrete/Asphalt;
   Bowl: Soil), not at raid start. No Warning; no `water cache ... not built` line (if one appears it names the scene that never loads);
   no `still lack shader` line.
2. **F10 Probe at the check points** (developer mode; `uibridge_teleport` to the FLOOR y below, NOT the NavMesh polygon y: the Air Pit
   polygon plane is 0.2 to 0.4 m above the real floor, `airport_floor_BALLISTIC_concrete` is flat at y 0.003, and the 16 cm foot-plane rule
   would otherwise reject a correct point). Wet = coverage above 0.1 and result `Puddle`; dry = result unchanged.

   | Map / material | Wet point (x, floor y, z) | red | Dry point | red | Renderer |
   |---|---|---|---|---|---|
   | Equator static (`70f045bb011600e7`) | (-18.58, 0.031, -16.86) | 0.377 | (-17.84, 0.031, -15.70) | 0.078 | `SBG_Arena_Equator/OO/PROPS/Puddles/puddle2_indoornoripple (1)` |
   | Equator rain (`0877f2335044042e`) | (20.66, 0.032, 30.19) | 0.785 | (22.07, 0.032, 29.25) | 0.0 | `SBG_Arena_Equator/OO/PROPS/heap_scrap_alang_6 Group/puddle2` |
   | Air Pit static (`1b8546c701e1d1dc`) | (-14.36, 0.006, 44.48) | 0.381 | (-13.77, 0.006, 60.40) | 0.0 | `SBG_Arena_Airpit_Main_hall/OO/PROPS/INDOOR/FRONT/puddle2_indoornoripple (2)` |
   | Air Pit rain | (13.48, 0.003, -1.99) | 1.0 | (18.33, 0.003, -1.11) | 0.031 | `PUDDLES/puddle2 (2)` (Arena_AirPit_ShootOut_Light) |
   | Bowl rain (Soil) | (56.64, 0.02, -11.40) | 1.0 | (56.30, 0.02, -11.16) | 0.0 | `SBG_Arena_Bowl/OO/PROPS/FRONT/puddle2_indoornoripple (15)` |

   The static points are wet at their material's own level (about 0.86), no weather needed. For the rain material use LightingLab
   instead of waiting for rain: `LightingLab (<key>)/WaterLevelOverrideEnabled = true` with `WaterLevelOverrideValue` 0.6 to 0.95 (developer
   mode, live). At red 0.785 the Equator wet point already passes at a water level of about 0.28; the Bowl wet point (red 1.0) passes above
   about 0.07; the same points in clear weather (`_WaterLevel` 0) must stay `Concrete` (Equator, Air Pit) or `Soil` (Bowl). Mesh clearance
   of the points is 2.1 to 4.6 m, so they are safe to stand on.
3. **Audio**: walk through a wet point and a dry point (or step between them) and listen for the Puddle bank (splash) against the
   floor sound; on Bowl the dry point must keep the soil sound and a step on a metal or plastic container next to a decal must keep its own (a rubber mat has no ground sound of its own and may sound as puddle; read `native=` in the F10 Probe line before calling it a defect).
4. **Toggle**: `Water (<key>)/PuddleFootsteps = false` restores the native sound live (the cache is dropped), `true` rebuilds it at the next
   eligible step; also check that Bay5's stored value from the old install still applies.
5. **Frame time**: `uibridge_status` `max` and ModProfiler's `max_ms_in_frame` around the first eligible footstep on all three new maps
   (the one-shot scan is the largest on Bowl). Expect one hitch of at most a few frames, then nothing at 1 Hz: a periodic spike would
   be a regression of the retry bound.
6. **Hideout**: open it once (the patch target is not generic, so there is no hideout trap, but acceptance of a client build includes it)
   and grep EFT's `Logs/<session>/*errors.log` for a first-use exception.

## Risks and open points

- Rain-driven puddles (`USERAIN`, global `_WaterLevel`) have never been host-verified for the footstep repair; Bay5's material is static.
  The LightingLab override above makes the check deterministic.
- The offline wet masks and plane distances were computed from the extract packages; the live cache reads the real textures. A mismatch
  between a predicted wet point and the F10 probe is information, not a build error.
- Air Pit: 140 of the static material's wet samples and 30 of the rain material's are off-plane by more than 16 cm (decals under platforms or
  over steps); the 16 cm foot-plane rule leaves them dry, which is intended.
- Rebuilding a map's scenes bundle does not change this feature (it reads materials, meshes and textures at raid time); renaming a water
  material would, and `test_puddle_sidecar_drift_is_rejected` / the xunit theory pin the lists.
