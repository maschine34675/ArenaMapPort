"""ArenaMapPort loading-screen art extractor (PLAN-TWO-MAPS item 10b).

Loading-flow research (2026-09-27, this session): EFT 0.16.9.5's own "Deploying in progress (NN%)"
deploy screen (`EFT.UI.Matchmaker.MatchmakerTimeHasCome.Show`, decompile:
`EFT.UI.Matchmaker/MatchmakerTimeHasCome.cs:209`) is shown BEFORE the raid's scenes/bundles load and
stays up through the whole load (`TarkovApplication.LocalGameMatching`) — it already receives
`raidSettings.SelectedLocation`/`LocationId` at that point, but shows no location-specific image of
its own; `LocationSettings.Location.Preview` (its own preview-image field) is declared but never
read anywhere in that path. `tools/server/make_base_json.py` never populates it either. So every
ArenaMapPort raid deploys behind whatever EFT's own generic/default background is — nothing
Arena-specific, unlike the small `MatchmakerBannersPanel` carousel item 10 already wires (server-side,
separate mechanism — see `extract_banners.py` / `docs/progress/two-maps-item-10-banners.md`).

Arena ships one purpose-built, already-vignetted loading-screen image per playable location in its
own `resources.assets`: `AllScreens_Loadingscreen-Equator` (path_id 1558),
`AllScreens_Loadingscreen-Bay5` (path_id 804) and, added with AirPit (2026-10-11, Arena 0.6.0 path_id
2704), `AllScreens_Loadingscreen-AirPit`, all 1920x1080 `Texture2D` (DXT5/BC3). (A further object,
`AllScreens_Loadingscreen-Bay5_NY`, path_id 791, is an unwanted Christmas/New-Year reskin of the Bay5
shot and is intentionally not in this table.) Re-verified this pass with a live UnityPy read against
`D:\\Battlestate Games\\Escape from Tarkov Arena\\EscapeFromTarkovArena_Data\\resources.assets`
(read-only) — the Equator and Bay5 objects really are `Texture2D`, really carry that exact name, and really
decode to 1920x1080 (AirPit's was re-read the same way in the 0.6.0 probe, see docs/progress/airpit-release.md).

This script reads the table's objects (per map: that loading screen plus four main-menu backgrounds)
read-only with UnityPy and writes them as high-quality JPEGs
straight into the CLIENT plugin's deployed folder — a sibling of that folder's own `bundles/`
directory, exactly the folder `ArenaMapPort.Client.csproj`'s `ArenaMapPortDistCopy` target already
copies the plugin DLL into (`$(ArenaMapPortDistDir)` = `dist/BepInEx/plugins/maschine-ArenaMapPort/`), so
`ArenaMapPort.Client/LoadingScreenArt.cs`'s own `PluginDir`-relative lookup
(`Path.Combine(PluginDir, "loadscreens", ...)`, the same convention `ShaderRebind.cs`/`BundleHost.cs`/
`NavMeshInstaller.cs` already use) finds it with zero extra staging step in the csproj. BSG art never
goes into git (AGENTS.md): the only write targets are `dist/` (gitignored) or a `--output` override,
typically under `work/` for a one-off inspection.

Output filenames are numbered per map, `arena_<key>_<n>.jpg` starting at `n=1` — five files per map
today (the loading screen plus four main-menu backgrounds), and the client probes contiguously
(`arena_equator_1.jpg`, `_2.jpg`, ...) and stops at the
first missing index, so shipping a further variety image later (e.g. a future additional
`AllScreens`-style shot) is a drop-in: no client rebuild, no naming change, just add the next
number after the map's last file (`arena_<key>_6.jpg` today).

Selects a unique exact Texture2D name in the expected source file, then checks that
entry's known dimensions. The old path_id values below are historical hints only;
Arena 0.6 repacked them. Missing/duplicate names or dimension drift fail loudly.

CLI:
    python extract_loadscreens.py                     # extract all three maps into dist/
    python extract_loadscreens.py --ext png            # write PNG instead of the jpg default
    python extract_loadscreens.py --output <dir>       # override the output directory
    python extract_loadscreens.py --source <ArenaData> # override the Arena EscapeFromTarkovArena_Data dir
    python extract_loadscreens.py --check              # verify the output files exist; no UnityPy/Arena read
"""
from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)

from common_ids import HardExtractError  # noqa: E402
from named_assets import unique_named_texture  # noqa: E402

# Same default Arena data directory every other extractor in this pipeline uses -- read-only.
DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"

# repo_root/dist/BepInEx/plugins/maschine-ArenaMapPort/loadscreens -- the exact directory
# LoadingScreenArt.cs's own PluginDir-relative lookup resolves to at runtime
# (Path.Combine(PluginDir, "loadscreens", ...), where PluginDir is the deployed plugin DLL's own
# folder -- ArenaMapPortDistCopy copies the DLL straight into ".../plugins/maschine-ArenaMapPort", not a
# subfolder of it, same as the "bundles" and "banners"-style siblings already staged there).
_REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = _REPO_ROOT / "dist" / "BepInEx" / "plugins" / "maschine-ArenaMapPort" / "loadscreens"

EXPECTED_WIDTH = 1920
EXPECTED_HEIGHT = 1080

# map_key -> [entry, ...], in the order they become arena_<key>_1.jpg, arena_<key>_2.jpg, ... . Entry 1 is
# Arena's purpose-made 1920x1080 loading screen (resources.assets); entries 2-5 are the four main-menu
# backgrounds Arena shows for the map (sharedassets1.assets, 2048x1024, opaque; the user pointed at these,
# 2026-09-27). The client rotates over whatever files exist. The "-NY" (New Year) reskins are never used.
LOADSCREENS = {
    "arena_equator": [
        {"asset_name": "AllScreens_Loadingscreen-Equator", "path_id": 1558, "file": "resources.assets", "size": (1920, 1080)},
        {"asset_name": "Arena_MainMenu_Paralax_Equator-1", "path_id": 292, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_Equator-2", "path_id": 1267, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_Equator-3", "path_id": 1102, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_Equator-4", "path_id": 1550, "file": "sharedassets1.assets", "size": (2048, 1024)},
    ],
    "arena_bay5": [
        {"asset_name": "AllScreens_Loadingscreen-Bay5", "path_id": 804, "file": "resources.assets", "size": (1920, 1080)},
        {"asset_name": "Arena_MainMenu_Paralax_Bay5-1", "path_id": 607, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_Bay5-2", "path_id": 526, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_Bay5-3", "path_id": 1433, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_Bay5-4", "path_id": 1638, "file": "sharedassets1.assets", "size": (2048, 1024)},
    ],
    # AirPit rows carry the 0.6.0.47752 path ids (2026-10-11 probe; hints only, the lookup is by the unique
    # name). The four "Arena_MainMenu_Paralax_AirPit-Alt-1..4" images exist in the same file but are NOT part
    # of the rotation (user decision 2026-10-11, PLAN-AIRPIT section 4 item 10: the five non-Alt images, like
    # Equator and Bay5).
    "arena_airpit": [
        {"asset_name": "AllScreens_Loadingscreen-AirPit", "path_id": 2704, "file": "resources.assets", "size": (1920, 1080)},
        {"asset_name": "Arena_MainMenu_Paralax_AirPit-1", "path_id": 1472, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_AirPit-2", "path_id": 438, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_AirPit-3", "path_id": 403, "file": "sharedassets1.assets", "size": (2048, 1024)},
        {"asset_name": "Arena_MainMenu_Paralax_AirPit-4", "path_id": 936, "file": "sharedassets1.assets", "size": (2048, 1024)},
    ],
}


def _require(condition, message):
    if not condition:
        raise HardExtractError("Loadscreens: " + message)


def output_filename(map_key: str, index: int = 1, ext: str = "jpg") -> str:
    """The exact basename LoadingScreenArt.cs's own probe loop expects for `map_key`'s `index`-th
    (1-based) image -- LOADSCREENS' own key, unchanged, plus the 1-based index and extension.
    Unity-independent: usable from a test with no UnityPy import and no BSG data on disk."""
    _require(map_key in LOADSCREENS, f"unknown loadscreen map key {map_key!r} (known: {sorted(LOADSCREENS)})")
    entries = LOADSCREENS[map_key]
    _require(1 <= index <= len(entries), f"{map_key}: index {index} out of range (has {len(entries)} image(s))")
    return f"{map_key}_{index}.{ext}"


def output_path(output_dir, map_key: str, index: int = 1, ext: str = "jpg") -> Path:
    return Path(output_dir) / output_filename(map_key, index, ext)


def validate_dimensions(width: int, height: int, map_key: str, index: int = 1, expected=None) -> None:
    """Every known loadscreen source is 1920x1080 (2026-09-27 research pass, re-checked here rather
    than just trusted) -- a different size at the same path_id means Arena's own asset moved and
    this table needs a fresh look, not a silently mis-sized/cropped image shipped to players."""
    if expected is None:
        entries = LOADSCREENS.get(map_key, [])
        expected = entries[index - 1]["size"] if 1 <= index <= len(entries) else (EXPECTED_WIDTH, EXPECTED_HEIGHT)
    exp_w, exp_h = expected
    _require(
        width == exp_w and height == exp_h,
        f"{map_key}[{index}]: expected {exp_w}x{exp_h}, got {width}x{height}",
    )


def _load_assets_file(source_dir, file_name):
    import UnityPy

    root = Path(source_dir)
    path = root / file_name
    _require(path.is_file(), f"{file_name} not found under {root} (is the Arena install path right?)")
    gm = root / "globalgamemanagers.assets"
    env = UnityPy.load(str(path), str(gm)) if gm.is_file() else UnityPy.load(str(path))
    source_file = env.files.get(str(path))
    _require(source_file is not None, f"{file_name} unavailable in the loaded environment: {root}")
    return source_file


def _load_resources_assets(source_dir):
    return _load_assets_file(source_dir, "resources.assets")


def _read_loadscreen_texture(source_file, map_key, index):
    spec = LOADSCREENS[map_key][index - 1]
    return unique_named_texture(source_file, spec["asset_name"], f"Loadscreens: {map_key}[{index}]")


def extract_one(source_file, map_key, index, output_dir, ext="jpg", quality=95):
    """Reads, validates and writes a single map/index's loadscreen image. Returns the written Path."""
    data = _read_loadscreen_texture(source_file, map_key, index)
    validate_dimensions(data.m_Width, data.m_Height, map_key, index, LOADSCREENS[map_key][index - 1]["size"])

    image = data.image
    _require(image is not None, f"{map_key}[{index}]: texture decode failed (UnityPy returned no image)")

    dest = output_path(output_dir, map_key, index, ext)
    dest.parent.mkdir(parents=True, exist_ok=True)
    if ext.lower() in ("jpg", "jpeg"):
        image.convert("RGB").save(dest, quality=quality)
    else:
        image.save(dest)
    return dest


def extract_all(source_dir=DEFAULT_SOURCE, output_dir=DEFAULT_OUTPUT, ext="jpg", quality=95):
    """Extracts every map/index in LOADSCREENS. Idempotent -- re-running overwrites the same output
    paths with the same source data, never appends/renames. Returns {(map_key, index): Path}."""
    files = {}
    written = {}
    for map_key, entries in LOADSCREENS.items():
        for index in range(1, len(entries) + 1):
            file_name = entries[index - 1].get("file", "resources.assets")
            if file_name not in files:
                files[file_name] = _load_assets_file(source_dir, file_name)
            written[(map_key, index)] = extract_one(files[file_name], map_key, index, output_dir, ext, quality)
    return written


def check(output_dir=DEFAULT_OUTPUT, ext="jpg"):
    """--check: confirms every expected output file already exists (non-empty), without reading the
    Arena install or importing UnityPy at all. Returns {(map_key, index): Path} for whatever is
    missing/empty ({} on success)."""
    missing = {}
    for map_key, entries in LOADSCREENS.items():
        for index in range(1, len(entries) + 1):
            path = output_path(output_dir, map_key, index, ext)
            if not path.is_file() or path.stat().st_size == 0:
                missing[(map_key, index)] = path
    return missing


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", default=DEFAULT_SOURCE,
                        help="Arena's EscapeFromTarkovArena_Data directory (read-only)")
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT),
                        help="directory to write arena_<key>_<n>.<ext> (n = 1..5 per map) into")
    parser.add_argument("--ext", default="jpg", choices=["jpg", "jpeg", "png"],
                        help="output image format (default jpg, quality=95)")
    parser.add_argument("--quality", type=int, default=95, help="JPEG quality, 1-95 (ignored for --ext png)")
    parser.add_argument("--check", action="store_true",
                         help="verify the output files already exist; does not touch the Arena install")
    args = parser.parse_args(argv)

    if args.check:
        missing = check(args.output, args.ext)
        if missing:
            for (map_key, index), path in sorted(missing.items()):
                print(f"MISSING: {map_key}[{index}] -> {path}")
            return 1
        total = sum(len(v) for v in LOADSCREENS.values())
        print(f"OK: {total} loadscreen(s) present under {args.output}")
        return 0

    try:
        written = extract_all(args.source, args.output, args.ext, args.quality)
    except HardExtractError as e:
        print(f"FAILED: {e}", file=sys.stderr)
        return 1

    for (map_key, index), path in sorted(written.items()):
        print(f"OK: {map_key}[{index}] -> {path} ({path.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
