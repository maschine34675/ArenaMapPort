#!/usr/bin/env python3
"""ArenaMapPort location-select banner extractor (PLAN-TWO-MAPS item 10).

EFT 0.16.9.5's location-select screen (MatchMakerSelectionLocationScreen) shows one picture per
location: LocationInfoPanel.Set() reads only location.Banners[0] and requests it from the SPT
server's ImageRouter as "/files/" + pic.path (client decompile:
JsonType/LocationSettings.cs:146-163, EFT.UI/LocationInfoPanel.cs:167). Today's base.json forces
Banners: [] for every ArenaMapPort location (tools/server/make_base_json.py), so the screen shows
the client's own default picture instead.

Arena ships matching art for exactly the three maps in this release: Matchmaker_Banner_Equator
(resources.assets path_id 3030), Matchmaker_Banner_Bay5 (path_id 1361) and Matchmaker_Banner_AirPit
(0.6.0 path_id 1584), all 769x460 RGB24 Texture2D (vanilla banners are 764/765x460 -- close but not
identical, confirmed harmless: the client's own Sprite.Create call just uses the texture's actual
width/height). This script reads those three objects read-only with UnityPy from Arena's own
resources.assets and writes them
straight into the server mod's DEPLOYED folder
(dist/SPT_Runtime/user/mods/ArenaMapPort/banners/arena_<key>.<ext>) -- a sibling of that folder's
"db" directory, not a subdirectory of it, specifically so the existing csproj Content/DeployToDist
globs (db/**/*.json only) never need to change and this folder is never touched by `dotnet build`.
ArenaMapPort.Server/ArenaMapBanners.cs looks for the same three file stems under that exact
directory at OnLoadAsync and wires each one it finds through ImageRouter; a file that is not there
yet just leaves that map's Banners empty (never a dangling/[UNHANDLED] entry -- see that file's own
doc comment).

BSG art never goes into git (AGENTS.md): the only write targets are dist/ (gitignored) or a
--output override, typically under work/ for a one-off inspection. Reads Arena's own
resources.assets read-only and never writes there.

Selects the unique exact Texture2D name in the expected source file, then validates its
known dimensions. The Equator/Bay5 path_id values below document the 0.5.4 source only; Arena
0.6 reused them for unrelated textures. The AirPit value was read from the 0.6.0.47752 install
(2026-10-11) and is a hint as well. Missing/ambiguous names or unexpected dimensions
remain hard errors, never first-match guesses or placeholder images.

CLI:
    python extract_banners.py                       # extract all three maps into dist/
    python extract_banners.py --ext png              # write PNG instead of the jpg default
    python extract_banners.py --output <dir>         # override the output directory
    python extract_banners.py --source <ArenaData>   # override the Arena EscapeFromTarkovArena_Data dir
    python extract_banners.py --check                # verify the output files exist; no UnityPy/Arena read
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

# Same default Arena data directory every other extractor in this pipeline uses
# (extract_equator.py/extract_bay5.py/extract_map.py's own DEFAULT_SOURCE) -- read-only.
DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"

# repo_root/dist/SPT_Runtime/user/mods/ArenaMapPort/banners -- exactly the directory
# ArenaMapBanners.cs's `bannersDir` resolves to at runtime (Path.Combine(modDir, "banners"), where
# modDir is the deployed mod DLL's own folder -- DeployToDist copies the DLL straight into
# ".../mods/ArenaMapPort", not a subfolder of it).
_REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = _REPO_ROOT / "dist" / "SPT_Runtime" / "user" / "mods" / "ArenaMapPort" / "banners"

EXPECTED_WIDTH = 769
EXPECTED_HEIGHT = 460

# map_key -> (Arena's own object name in resources.assets, path_id hint: 0.5.4 for Equator/Bay5, 0.6.0 for
# AirPit; the lookup is always by the unique name). The dict key doubles as
# the output file stem ("arena_<key>.<ext>") and must equal ArenaMapBanners.cs's own
# Descriptor.FileStem for each map -- BannerFileStemsTests (tools/tests/test_banners.py) and
# ArenaMapBannersTests (ArenaMapPort.Server.Tests, C# side) each independently pin this string so
# the two sides can't silently drift apart.
BANNERS = {
    "arena_equator": {"asset_name": "Matchmaker_Banner_Equator", "path_id": 3030},
    "arena_bay5": {"asset_name": "Matchmaker_Banner_Bay5", "path_id": 1361},
    "arena_airpit": {"asset_name": "Matchmaker_Banner_AirPit", "path_id": 1584},
}


def _require(condition, message):
    if not condition:
        raise HardExtractError("Banners: " + message)


def output_filename(map_key: str, ext: str = "jpg") -> str:
    """The exact basename ArenaMapBanners.cs's Resolve() and Banner.pic.path both key off of for
    `map_key` -- BANNERS' own key, unchanged, plus the extension. Unity-independent: usable from a
    test with no UnityPy import and no BSG data on disk."""
    _require(map_key in BANNERS, f"unknown banner map key {map_key!r} (known: {sorted(BANNERS)})")
    return f"{map_key}.{ext}"


def output_path(output_dir, map_key: str, ext: str = "jpg") -> Path:
    return Path(output_dir) / output_filename(map_key, ext)


def validate_dimensions(width: int, height: int, map_key: str) -> None:
    """All known banners are 769x460 (2026-09-25 research pass and the 2026-10-11 AirPit probe,
    re-checked here rather than just trusted) -- a different size for the same exact name means the source art changed and this table
    needs a fresh look, not a silently mis-sized banner shipped to players."""
    _require(
        width == EXPECTED_WIDTH and height == EXPECTED_HEIGHT,
        f"{map_key}: expected {EXPECTED_WIDTH}x{EXPECTED_HEIGHT}, got {width}x{height}",
    )


def _load_resources_assets(source_dir):
    import UnityPy

    root = Path(source_dir)
    path = root / "resources.assets"
    _require(path.is_file(), f"resources.assets not found under {root} (is the Arena install path right?)")
    # Loaded alongside globalgamemanagers.assets, matching extract_ballistics.py's own
    # _read_presets() -- harmless if resources.assets turns out to be fully self-contained for
    # these two objects, and consistent with the rest of this pipeline either way.
    gm = root / "globalgamemanagers.assets"
    env = UnityPy.load(str(path), str(gm)) if gm.is_file() else UnityPy.load(str(path))
    source_file = env.files.get(str(path))
    _require(source_file is not None, f"resources.assets unavailable in the loaded environment: {root}")
    return source_file


def _read_banner_texture(source_file, map_key):
    spec = BANNERS[map_key]
    return unique_named_texture(source_file, spec["asset_name"], f"Banners: {map_key}")


def extract_one(source_file, map_key, output_dir, ext="jpg", quality=95):
    """Reads, validates and writes a single map's banner. Returns the written Path."""
    data = _read_banner_texture(source_file, map_key)
    validate_dimensions(data.m_Width, data.m_Height, map_key)

    image = data.image
    _require(image is not None, f"{map_key}: texture decode failed (UnityPy returned no image)")

    dest = output_path(output_dir, map_key, ext)
    dest.parent.mkdir(parents=True, exist_ok=True)
    if ext.lower() in ("jpg", "jpeg"):
        image.convert("RGB").save(dest, quality=quality)
    else:
        image.save(dest)
    return dest


def extract_all(source_dir=DEFAULT_SOURCE, output_dir=DEFAULT_OUTPUT, ext="jpg", quality=95):
    """Extracts every map in BANNERS. Idempotent -- re-running overwrites the same output paths
    with the same source data, never appends/renames. Returns {map_key: Path}."""
    source_file = _load_resources_assets(source_dir)
    return {
        map_key: extract_one(source_file, map_key, output_dir, ext, quality)
        for map_key in BANNERS
    }


def check(output_dir=DEFAULT_OUTPUT, ext="jpg"):
    """--check: confirms every expected output file already exists (non-empty), without reading
    the Arena install or importing UnityPy at all. Returns {map_key: Path} for whatever is
    missing/empty ({} on success)."""
    missing = {}
    for map_key in BANNERS:
        path = output_path(output_dir, map_key, ext)
        if not path.is_file() or path.stat().st_size == 0:
            missing[map_key] = path
    return missing


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", default=DEFAULT_SOURCE,
                        help="Arena's EscapeFromTarkovArena_Data directory (read-only)")
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT),
                        help="directory to write arena_equator.<ext> / arena_bay5.<ext> / arena_airpit.<ext> into")
    parser.add_argument("--ext", default="jpg", choices=["jpg", "jpeg", "png"],
                        help="output image format (default jpg, quality=95)")
    parser.add_argument("--quality", type=int, default=95, help="JPEG quality, 1-95 (ignored for --ext png)")
    parser.add_argument("--check", action="store_true",
                         help="verify the output files already exist; does not touch the Arena install")
    args = parser.parse_args(argv)

    if args.check:
        missing = check(args.output, args.ext)
        if missing:
            for map_key, path in sorted(missing.items()):
                print(f"MISSING: {map_key} -> {path}")
            return 1
        print(f"OK: {len(BANNERS)} banner(s) present under {args.output}")
        return 0

    try:
        written = extract_all(args.source, args.output, args.ext, args.quality)
    except HardExtractError as e:
        print(f"FAILED: {e}", file=sys.stderr)
        return 1

    for map_key, path in sorted(written.items()):
        print(f"OK: {map_key} -> {path} ({path.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
