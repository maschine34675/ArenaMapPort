#!/usr/bin/env python3
"""Extract official released-map descriptions from Arena's shipped locale assets.

Reads resources.assets without changing the Arena install. The generated text is
BSG-derived data and belongs under ignored work/ or dist/, never in source control.
Select exact TextAsset names rather than build-dependent Unity path IDs. Only the
two released maps are exported; each available translation must be complete.

    python tools/extract/extract_map_descriptions.py
    python tools/extract/extract_map_descriptions.py --check
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import tempfile

DEFAULT_SOURCE = Path(
    "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"
)
DEFAULT_OUTPUT = (
    Path(__file__).resolve().parents[2]
    / "work" / "extract" / "map-descriptions" / "map-descriptions.json"
)
LOCALE_ASSETS = {
    "en": "TestBackendLocaleEn",
    "ge": "TestBackendLocalege",
    "ru": "TestBackendLocaleRu",
}
DESCRIPTION_KEYS = {
    "arena_equator": "621df90bf200a905ed57d31d Description",
    "arena_bay5": "62a05c960a18b50b7c575b1b Description",
}


def validate_pack(payload):
    """Return the complete locale-to-map mapping or reject a malformed pack.

    English is the required fallback. Additional locale keys are permitted, but
    every translation must cover exactly the released maps. Text is preserved
    verbatim; trimming is used only to detect empty values.
    """
    if not isinstance(payload, dict) or set(payload) != {"schema_version", "descriptions"}:
        raise ValueError("Map descriptions: expected schema_version and descriptions")
    if type(payload["schema_version"]) is not int or payload["schema_version"] != 1:
        raise ValueError("Map descriptions: unsupported schema_version")
    descriptions = payload["descriptions"]
    if not isinstance(descriptions, dict) or "en" not in descriptions:
        raise ValueError("Map descriptions: English fallback is required")
    for locale, maps in descriptions.items():
        if not isinstance(locale, str) or not locale.strip() or locale != locale.strip():
            raise ValueError("Map descriptions: invalid locale key")
        if not isinstance(maps, dict) or set(maps) != set(DESCRIPTION_KEYS):
            raise ValueError(f"Map descriptions: {locale!r} must contain exactly the released maps")
        for map_key, text in maps.items():
            if not isinstance(text, str) or not text.strip():
                raise ValueError(f"Map descriptions: empty or invalid {locale}/{map_key}")
    return descriptions


def pack_from_assets(source_file):
    """Read unique named TextAssets from one resources.assets file."""
    wanted_names = set(LOCALE_ASSETS.values())
    found = {}
    for obj in source_file.objects.values():
        if obj.type.name != "TextAsset":
            continue
        asset = obj.read()
        if asset.m_Name not in wanted_names:
            continue
        if asset.m_Name in found:
            raise ValueError(f"Map descriptions: duplicate TextAsset {asset.m_Name!r}")
        found[asset.m_Name] = asset.m_Script

    descriptions = {}
    for locale, asset_name in LOCALE_ASSETS.items():
        if asset_name not in found:
            raise ValueError(f"Map descriptions: missing TextAsset {asset_name!r}")
        try:
            payload = json.loads(found[asset_name])
        except (TypeError, ValueError) as error:
            raise ValueError(f"Map descriptions: invalid JSON in {asset_name!r}") from error
        data = payload.get("data") if isinstance(payload, dict) else None
        if not isinstance(data, dict):
            raise ValueError(f"Map descriptions: {asset_name!r} has no locale data object")
        descriptions[locale] = {map_key: data.get(key) for map_key, key in DESCRIPTION_KEYS.items()}
    pack = {"schema_version": 1, "descriptions": descriptions}
    validate_pack(pack)
    return pack


def ensure_safe_output(source_dir, output_file):
    """Reject writes into the source tree or its containing Arena installation."""
    source = Path(source_dir).resolve()
    output = Path(output_file).resolve()
    protected = [source, DEFAULT_SOURCE.resolve().parent]
    if source.name.casefold() == "escapefromtarkovarena_data":
        protected.append(source.parent)
    for root in protected:
        if output.is_relative_to(root):
            raise ValueError(f"Map descriptions: output must be outside the read-only source {root}")
    return output


def extract(source_dir=DEFAULT_SOURCE, output_file=DEFAULT_OUTPUT):
    output = ensure_safe_output(source_dir, output_file)
    source_path = Path(source_dir).resolve() / "resources.assets"
    if not source_path.is_file():
        raise ValueError(f"Map descriptions: source not found: {source_path}")
    import UnityPy

    environment = UnityPy.load(str(source_path))
    source_file = environment.files.get(str(source_path))
    if source_file is None:
        raise ValueError("Map descriptions: resources.assets was not loaded")
    pack = pack_from_assets(source_file)
    output.parent.mkdir(parents=True, exist_ok=True)
    temp_path = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", newline="\n", dir=output.parent,
            prefix=output.name + ".", suffix=".tmp", delete=False,
        ) as stream:
            temp_path = Path(stream.name)
            json.dump(pack, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.replace(temp_path, output)
    finally:
        if temp_path is not None and temp_path.exists():
            temp_path.unlink()
    return output


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE,
                        help="Arena's read-only EscapeFromTarkovArena_Data directory")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT,
                        help="generated JSON file (default: ignored work/extract/map-descriptions)")
    parser.add_argument("--check", action="store_true",
                        help="validate an existing output without loading Arena or UnityPy")
    args = parser.parse_args(argv)
    try:
        if not args.check:
            extract(args.source, args.output)
        descriptions = validate_pack(json.loads(args.output.read_text(encoding="utf-8")))
    except (OSError, ValueError) as error:
        print(f"FAILED: {error}", file=sys.stderr)
        return 1
    print(f"MAP_DESCRIPTIONS_OK {len(descriptions)} locales {len(DESCRIPTION_KEYS)} maps: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
