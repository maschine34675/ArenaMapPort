"""Generate ArenaMapPort.Server/db/base.json from a complete vanilla location base.

Why: a minimal hand-written base.json leaves fields such as BossLocationSpawn and
waves null on the client. The map selection screen dereferences
location.BossLocationSpawn unguarded (MatchMakerSelectionLocationScreen.cs:439/451,
LocationInfoPanel.cs:174), so the button for such a location throws and never
appears. Starting from a real base (factory4_day) keeps every field the client
expects, and only identity, scene, placement and gameplay content are overridden.

Usage:
    python tools/server/make_base_json.py [--template <vanilla base.json>] [--out <path>]
The template is read-only input from the SPT install.
"""

import argparse
import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_TEMPLATE = Path("D:/SPT41/SPT_Runtime/SPT_Data/database/locations/factory4_day/base.json")
DEFAULT_OUT = REPO / "ArenaMapPort.Server" / "db" / "base.json"

OVERRIDES = {
    "Id": "arena_equator",
    "_Id": "e9b8e12c60dfd2820e949462",
    "Name": "Arena: Equator (spike)",
    "Scene": {"path": "maps/arena_equator_preset.bundle", "rcid": "arena_equator.scenespreset.asset"},
    "Enabled": True,
    "Locked": False,
    "IsSecret": False,
    "ForceOnlineRaidInPVE": False,
    "DisabledForScav": True,
    "UnixDateTime": 1789257600,
    "IconX": 150,
    "IconY": 760,
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
REMOVE = ["SpawnPointParams"]  # absent on purpose: scene-authored markers stand alone


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--template", type=Path, default=DEFAULT_TEMPLATE)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = parser.parse_args()

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
    base.update(OVERRIDES)

    args.out.write_text(json.dumps(base, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {args.out} ({len(base)} keys) from {args.template}")


if __name__ == "__main__":
    main()
