"""Refresh only verified Texture3D payloads in an existing full native-feature package.

The complete source extract uses the same write_volume_texture path. This focused refresh
keeps accepted geometry, normal PNGs, audio and material JSON byte-identical. No source writes.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import UnityPy
from volume_textures import write_volume_texture


def refresh(package: Path) -> int:
    manifest_path = package / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    source = Path(manifest["source"]["arena_data_root"])
    wanted = {key: value for key, value in manifest["textures"].items() if value.get("dimension") == "Tex3D"}
    found = {}
    # Read serialized asset tables, not existing flattened PNGs; stable identity selects the source.
    for path in sorted(source.glob("sharedassets*.assets")):
        environment = UnityPy.load(str(path))
        for obj in environment.objects:
            if obj.type.name != "Texture3D" or Path(obj.assets_file.name).name != path.name:
                continue
            key = hashlib.sha1(f"{path.name}:{obj.path_id}:Texture2D".encode()).hexdigest()[:16]
            if key not in wanted:
                continue
            if key in found:
                raise ValueError("Ambiguous source volume: " + key)
            metadata, preview = write_volume_texture(obj, key, package)
            preview.save(package / wanted[key]["file"])
            wanted[key]["native_volume"] = metadata
            wanted[key]["format_src"] = "GraphicsFormat54_Alpha8"
            found[key] = metadata
        if len(found) == len(wanted):
            break
    if set(found) != set(wanted):
        raise ValueError(f"Missing source volumes: {set(wanted) - set(found)}")
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"NATIVE_VOLUMES_REFRESHED count={len(found)} package={package}")
    return len(found)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    refresh(parser.parse_args().package)
