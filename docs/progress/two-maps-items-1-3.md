# PLAN-TWO-MAPS items 1–3 — ambient gain, barbed wire, release names (2026-09-25)

Scope: the three no-rebuild items of [docs/PLAN-TWO-MAPS.md](../PLAN-TWO-MAPS.md). Commit `a947cce`
(code + data), host-verified 10:22–10:27 on the GPU host (EFT instance `027b491c`, SPT 4.1.6 server on the
host), deployed to `D:\SPT41` and `D:\SPT41vanilla`.

## 1. Ambient gain / offset (both maps)

`Bay5AmbientAudio` (the native ambient driver for both maps) binds two keys per map next to
`PlaybackDiagnostics`: `Audio (<map>) › AmbientGainDb` (−12…12, default 0) and `AmbientVolumeOffset` (0…1,
default 0). `Driver.ApplyUserMix()` runs once in `TryStart()` right before the players start: with neutral
values it logs `user mix neutral (original Arena volumes)` and touches nothing; otherwise every active
player's `_volume` becomes `clamp01(_volume·10^(dB/20) + offset)` and `_deltaVolume` is scaled the same way and
capped at the new volume, so `GetBaseVolume() = _volume + Random(-_deltaVolume, 0)` can no longer draw below
zero (13 of Equator's 25 authored players have `_deltaVolume > _volume`; Bay5 none). The scene's player
instances are unloaded with the raid, nothing accumulates. Keys appear in F12/`uibridge_config` after the map
was loaded once (they are bound lazily in `Ensure()`).

Host test: raid 1 (10:22) `user mix neutral (original Arena volumes) map=arena_bay5`; set `AmbientGainDb=6`
through `uibridge_config_set`; raid 2 (10:25) `user mix gainDb=6.0 offset=0.000: adjusted 24 player(s), dip
capped on 0; e.g. 'containers_squeak (3)' 0.200-0.000->0.399-0.000; …` followed by the normal
`ready players=24 started=23`. No new errors. The key was reset to 0 afterwards (the user tunes by ear).

## 2. Barbed wire on Arena's concertina wire (map-agnostic, Bay5 has it)

`BarbedWireRepairs.Apply()` (spawn postfix, after the ballistic twins) attaches `ArenaBarbedWire`, a subclass
of `EFT.Interactive.BarbedWire` whose `PlaySound` is skipped while `_soundBank` is null (no barbed-wire bank
exists in SPT's streaming assets; the per-instance bank is an unrecoverable field), to every active trigger
`BoxCollider` named `bruno_helix*` without a `DamageTrigger`. `DamageTrigger.Awake` moves the object to the
Triggers layer and the player's `TriggerColliderSearcher` drives `OnTriggerStay`; `BodyPartCollider.ProceedBarb`
accumulates damage with movement distance (standing still costs nothing).

Host test (10:22): `barbed wire: attached 192 BarbedWire trigger(s) (0 already present)` with the first five
positions (`'bruno_helix (36)' at (45.5, 0.7, 43.7) size (1.5, 1.0, 1.8)` …); second `CreateFromScene` pass:
`attached 0 (192 already present)`. The wire volumes are off the NavMesh (a teleport into (36) snapped 1.5 m
short); `bruno_helix (6)` at (71.2,-0.4,48.7) accepted a teleport to (71.2,-1.2,49.0) inside its box. Standing
still: no damage; one `Jump` command: Head 35→34, Chest 85→83, Stomach 70→67, LeftArm 60→56, LeftLeg 65→62,
RightLeg 65→61 — Barbed damage across body parts as in vanilla. Config `[Combat] BarbedWire` (default on).

## 3. Release names

`ArenaMapDescriptor` (Equator, Bay5), the Editor default in `ArenaPaths.cs`, `tools/maps/equator.json` and
`bay5.json` now carry "Arena: Equator" / "Arena: Bay 5" with a one-line description; both `base.json` were
regenerated with `make_base_json.py` (diff: only `Name`). The name the client shows comes from the locale table
the server fills at load, so the server DLL was rebuilt and the host server restarted (09:01):
`[Manimal-ArenaMapPort] Arena: Equator registered as an independent location`, same for Bay 5. Map-select screen
(10:27, `uibridge_snapshot`): `ARENA: BAY 5`, `ARENA: EQUATOR`; the twelve deferred maps keep `(SPIKE)`.

## Notes

- The host is on SPT 4.1.6 (server console title); the mod's `~4.1.5` pin loads there.
- Pre-existing log noise unchanged: Unity "referenced script on 'extractor_smoke' is missing" warnings at Bay5
  raid start (Arena's own exfil-smoke prefab), the microphone error and the MenuScreen coroutine message.
- Fourteen location tabs crowd one row of the map-select screen (tabs overlap); with the two-map release this
  goes away by itself.
