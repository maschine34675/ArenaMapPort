"""Stage a compass-only update of an existing Equator or Bay5 DynamicMaps pack.

Run in the DynamicMaps venv. This preserves the JSON definition and all SVG content
outside the known legacy arrow. Install the staged pack with deploy-to-spt.ps1.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from export_equator import LEGACY_NORTH_ARROW, NORTH_MARKER_SVG


def stage(source: Path, output: Path, map_name: str):
    source, output = source.resolve(), output.resolve()
    if source == output or source in output.parents or output in source.parents:
        raise ValueError("Source and output must not overlap")
    definition = source / f"ArenaMapPort_{map_name}.jsonc"
    svg_path = source / "Layers/Ground.svg"
    svg = svg_path.read_bytes()
    old = LEGACY_NORTH_ARROW.encode("utf-8")
    if svg.count(old) != 1 or b'id="arena-north-marker"' in svg:
        raise ValueError("Expected exactly one original north arrow; source was not changed")
    # Read/validate before creating output; don't reserialize or regenerate geometry.
    definition.read_bytes()
    (output / "Layers").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(definition, output / definition.name)
    (output / "Layers/Ground.svg").write_bytes(svg.replace(old, NORTH_MARKER_SVG.encode("utf-8")))
    print(f"Staged {map_name}: compass changed, definition and remaining SVG bytes preserved")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--map", required=True, choices=("Equator", "Bay5"))
    args = parser.parse_args()
    stage(args.source, args.output, args.map)
