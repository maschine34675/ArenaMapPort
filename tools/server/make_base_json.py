"""Generate ArenaMapPort.Server/db/<key>/base.json from a complete vanilla location base.

Why: a minimal hand-written base.json leaves fields such as BossLocationSpawn and
waves null on the client. The map selection screen dereferences
location.BossLocationSpawn unguarded (MatchMakerSelectionLocationScreen.cs:439/451,
LocationInfoPanel.cs:174), so the button for such a location throws and never
appears. Starting from a real base (factory4_day) keeps every field the client
expects, and only identity, scene, placement and gameplay content are overridden.

Multimap generalization (docs/progress/multimap-generalize.md): OVERRIDES/DEFAULT_OUT used to be
Equator-only literals with no override mechanism. Both are now sourced from a map descriptor
(tools/maps/<key>.json, the same JsonUtility-compatible shape
UnityProject/Assets/ArenaMapPort/Editor/Common/ArenaPaths.cs reads) via --map; with no --map, the
defaults are tools/maps/equator.json's own values, so an unparameterized invocation is
byte-for-byte unchanged from before this generalization (verified by diffing the generated file
against the previously-committed db/base.json — see docs/progress/multimap-generalize.md).

Usage:
    python tools/server/make_base_json.py [--template <vanilla base.json>] [--out <path>] [--map <descriptor.json>]
The template is read-only input from the SPT install.

bots-stage1 (docs/progress/bots-stage1.md): an OPTIONAL sidecar tools/maps/<stem>.bots.json next to the
descriptor turns bots on for that map only -- SpawnPointParams (player + Bot-category points),
Factory-shaped waves, OpenZones and BotMax are emitted from it. Without the sidecar the output is
byte-identical to before (SpawnPointParams absent, waves empty, OpenZones "", BotMax from the template).
"""

import argparse
import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_TEMPLATE = Path("D:/SPT41/SPT_Runtime/SPT_Data/database/locations/factory4_day/base.json")
DEFAULT_MAP = REPO / "tools" / "maps" / "equator.json"


def load_map(map_path: Path) -> dict:
    """Reads one tools/maps/<key>.json descriptor (the same file the Editor's ArenaPaths.cs
    loads) -- see that file's own doc comment for the field list."""
    return json.loads(map_path.read_text(encoding="utf-8"))


def overrides_for(map_data: dict) -> dict:
    """Every OVERRIDES key that used to be an Equator-only literal, now derived from the map
    descriptor. Bundle served-key/asset-name naming follows the project-wide convention
    (docs/progress/multimap-generalize.md): preset served key "maps/<key>_preset.bundle", preset
    asset name "<key>.scenespreset.asset" -- so the descriptor doesn't need to spell either out."""
    key = map_data["key"]
    return {
        "Id": key,
        "_Id": map_data["id_field"],
        "Name": map_data["locale_name"],
        "Scene": {"path": f"maps/{key}_preset.bundle", "rcid": f"{key}.scenespreset.asset"},
        "Enabled": True,
        "Locked": False,
        "IsSecret": False,
        "ForceOnlineRaidInPVE": False,
        "DisabledForScav": True,
        "UnixDateTime": 1789257600,
        "IconX": map_data["icon_x"],
        "IconY": map_data["icon_y"],
        "MinPlayers": 1,
        "MaxPlayers": 1,
        "AveragePlayTime": 10,
        "EscapeTimeLimit": 15,
        "EscapeTimeLimitPVE": 15,
        "RequiredPlayerLevelMin": 0,
        "RequiredPlayerLevelMax": 100,
        "DisabledScavExits": "",  # Factory exfil names, meaningless on our scenes
    }

# Content that belongs to Factory's scenes and must not leak into ours.
EMPTY_LISTS = [
    "waves", "BossLocationSpawn", "exits", "Banners", "Loot", "AccessKeys", "AccessKeysPvE",
    "MinPlayerLvlAccessKeys", "DisabledScavExits", "doors", "limits", "maxItemCountInLocation",
    "secretExits", "transits",
]
REMOVE = ["SpawnPointParams"]  # absent on purpose: scene-authored markers stand alone (unless a bots sidecar exists)


def sphere(radius: float) -> dict:
    """Vanilla SpawnPointParams collider shape (factory4_day uses SpawnSphereParams, bots R=35)."""
    return {"_parent": "SpawnSphereParams", "_props": {"Center": {"x": 0, "y": 0, "z": 0}, "Radius": radius}}


def vec(p) -> dict:
    return {"x": p[0], "y": p[1], "z": p[2]}


def apply_boss_waves(base: dict, bots: dict) -> None:
    """bots-stage3: PMC squads. SPT 4.1.5 spawns PMCs from base.json BossLocationSpawn entries whose BossName is
    pmcUSEC/pmcBEAR (factory4_day ships four), not from waves; PmcWaveGenerator.ApplyWaveChangesToMap only
    strips/replaces them for maps present in pmc.json customPmcWaves, so an Arena key keeps what we ship.
    Client: LocalGame.cs:199 runs the boss scenario unconditionally, BossSpawnScenario fires Time -1 entries at
    raid start after the BossChance roll; an explicit BossZone is resolved by FindZoneWithCorrectName over every
    BotZone (BossLocationSpawn.cs:318-337); IgnoreMaxBots true skips BotMax like Factory."""
    out = []
    for w in bots.get("boss_waves", []):
        out.append({
            "BossChance": w.get("boss_chance", 50),
            "BossDifficult": "normal",
            "BossEscortAmount": w.get("escort_amount", "0,0,0,0,2,2,2,1,1,1,1,1,0,2,3"),
            "BossEscortDifficult": "normal",
            "BossEscortType": w.get("escort_type", w["boss_name"]),
            "BossName": w["boss_name"],
            "BossPlayer": False,
            "BossZone": w.get("boss_zone", bots["zone"]),
            "Delay": w.get("delay", 0),
            "DependKarma": False,
            "ForceSpawn": False,
            "IgnoreMaxBots": True,
            "RandomTimeSpawn": False,
            "ShowOnTarkovMap": False,
            "ShowOnTarkovMapPvE": False,
            "SpawnMode": ["regular", "pve"],
            "Supports": None,
            "Time": w.get("time", -1),
            "TriggerId": "",
            "TriggerName": "",
        })
    if out:
        base["BossLocationSpawn"] = out


def apply_exits(base: dict, map_data: dict, exits: dict) -> None:
    """exfils-stage2: base.json 'exits' from tools/maps/<key>.exits.json — the shape of Factory's own entries
    (factory4_day/base.json exits[]) plus the explicit enum fields SPT's Exit model carries (server decompile
    Exit.cs: Chance/ChancePVE double, Count/CountPVE int, EntryPoints string, EventAvailable bool,
    ExfiltrationTime/ExfiltrationTimePVE double, ExfiltrationType enum, Id string, Min/MaxTime(+PVE) double,
    Name string, PassageRequirement enum, PlayersCount/PlayersCountPVE int, RequirementTip string).
    Client side (ExfiltrationController.InitAllExfiltrationPoints, ExfiltrationController.cs:222-262) matches a
    scene ExfiltrationPoint to an entry by exact Name and then loads these settings into it; EntryPoints is
    lowercased and compared with Profile.Info.EntryPoint, which BaseLocalGame copies from the spawn point's
    Infiltration — i.e. the map's infiltration_name, never the location key. Positions stay client-side
    (ArenaMapRegistry.<Map>.Exits); a unit test keeps both tables identical."""
    infiltration = map_data["infiltration_name"]
    out = []
    for e in exits["exits"]:
        chance = e.get("chance", 100)
        time = e.get("exfiltration_time", 8)
        out.append({
            "Chance": chance, "ChancePVE": chance,
            "Count": 0, "CountPVE": 0,
            "EntryPoints": infiltration,
            "EventAvailable": False,
            "ExfiltrationTime": time, "ExfiltrationTimePVE": time,
            "ExfiltrationType": e.get("type", "Individual"),
            "Id": "",
            "MaxTime": 0, "MaxTimePVE": 0, "MinTime": 0, "MinTimePVE": 0,
            "Name": e["name"],
            "PassageRequirement": "None",
            "PlayersCount": 0, "PlayersCountPVE": 0,
            "RequirementTip": "",
        })
    base["exits"] = out


def apply_bots(base: dict, map_data: dict, bots: dict) -> None:
    """bots-stage1: server JSON becomes the single source of spawn points again, as on vanilla maps.
    Client side (EFT.Game.Spawning/SpawnPointsCollection.cs:152-245): CreateFromScene -> FixSpawns deletes
    every scene marker whose Id is not in SpawnPointParams (our 3 GUID-named authored markers) and calls
    SpawnPointMarker.Create for every param without a scene marker; Create links a point whose
    BotZoneName equals a BotZone's GameObject name to that zone's SpawnPointMarkers (SpawnPointMarker.cs:
    368-374) -- which is exactly the list waves spawn from (BotSpawner.GetZoneByName, BotSpawner.cs:404/645).
    IsValid needs LocationExportInfo.DateTime <= base.json UnixDateTime: both are BuildBundles.cs's
    BuildUnixDateTime (1789257600), so the params are accepted."""
    zone = bots["zone"]
    params = []
    for sp in bots["player_spawns"]:
        params.append({
            "BotZoneName": "",
            "Categories": ["Player"],
            "ColliderParams": sphere(sp.get("radius", 5)),
            "CorePointId": 0,
            "DelayToCanSpawnSec": 4,
            "Id": sp["id"],
            "Infiltration": map_data["infiltration_name"],
            "Position": vec(sp["position"]),
            "Rotation": sp.get("rotation", 0.0),
            "Sides": sp["sides"],
        })
    for sp in bots["bot_spawns"]:
        params.append({
            "BotZoneName": zone,
            "Categories": list(sp.get("categories", ["Bot"])),
            "ColliderParams": sphere(sp.get("radius", 25)),
            "CorePointId": 0,
            "DelayToCanSpawnSec": 4,
            "Id": sp["id"],
            "Infiltration": "",
            "Position": vec(sp["position"]),
            "Rotation": sp.get("rotation", 0.0),
            "Sides": sp.get("sides", ["Savage"]),
        })
    base["SpawnPointParams"] = params
    base["waves"] = [{
        "BotPreset": w.get("preset", "normal"),
        "BotSide": w.get("side", "Savage"),
        "KeepZoneOnSpawn": False,
        "SpawnMode": ["regular", "pve"],
        "SpawnPoints": zone,
        "WildSpawnType": w.get("type", "assault"),
        "isPlayers": False,
        "number": i,
        "slots_max": w["slots_max"],
        "slots_min": w["slots_min"],
        "time_max": w["time_max"],
        "time_min": w["time_min"],
    } for i, w in enumerate(bots["waves"])]
    base["OpenZones"] = zone
    base["BotMax"] = bots["max_bots"]
    # BaseLocalGame.ModifyBotsOnLoaction (BaseLocalGame.cs:456-459) overwrites OldSpawn/NewSpawn with the
    # Offline* twins before LocalGame.vmethod_1 tests them (LocalGame.cs:190) -- waves run only with
    # OfflineOldSpawn true. Factory ships it true; pin it so a different template can't switch bots off.
    base["OfflineOldSpawn"] = True
    base["OfflineNewSpawn"] = False


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--template", type=Path, default=DEFAULT_TEMPLATE)
    parser.add_argument("--map", type=Path, default=DEFAULT_MAP,
                         help="tools/maps/<key>.json descriptor (default: equator.json, "
                              "byte-identical to this script's pre-generalization behavior)")
    parser.add_argument("--out", type=Path, default=None,
                         help="default: ArenaMapPort.Server/db/<map's db_base_json_path>")
    args = parser.parse_args()

    map_data = load_map(args.map)
    out_path = args.out or (REPO / "ArenaMapPort.Server" / "db" / map_data["db_base_json_path"])

    base = json.loads(args.template.read_text(encoding="utf-8-sig"))
    for key in REMOVE:
        base.pop(key, None)
    for key in EMPTY_LISTS:
        if key not in base:
            continue
        # only clear real lists; scalar fields with list-like names (DisabledScavExits is a
        # string, MinPlayerLvlAccessKeys an int in the server model) keep the template value
        if isinstance(base[key], list):
            base[key] = []
    if "OpenZones" in base:
        base["OpenZones"] = ""  # Factory zone names do not exist in our scenes
    base.update(overrides_for(map_data))

    exits_path = args.map.with_name(args.map.stem + ".exits.json")
    exits_note = "no exits sidecar"
    if exits_path.exists():
        exits = json.loads(exits_path.read_text(encoding="utf-8"))
        apply_exits(base, map_data, exits)
        exits_note = f"exits: {len(exits['exits'])} ({exits_path.name})"

    bots_path = args.map.with_name(args.map.stem + ".bots.json")
    bots_note = "no bots sidecar"
    if bots_path.exists():
        bots = json.loads(bots_path.read_text(encoding="utf-8"))
        apply_bots(base, map_data, bots)
        apply_boss_waves(base, bots)
        bots_note = (f"bots: {len(bots['player_spawns'])} player + {len(bots['bot_spawns'])} bot spawn point(s), "
                     f"{len(bots['waves'])} wave(s), {len(bots.get('boss_waves', []))} PMC boss wave(s), zone {bots['zone']}, "
                     f"BotMax {bots['max_bots']} ({bots_path.name})")

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(base, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {out_path} ({len(base)} keys) from {args.template} (map={map_data['key']}; {bots_note}; {exits_note})")


if __name__ == "__main__":
    main()
