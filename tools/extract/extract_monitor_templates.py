#!/usr/bin/env python3
"""The artwork of Arena's score monitors, for the faces the client draws on the TV props (docs/progress/score-monitors.md).

Arena's monitor canvases are built from templates (`MonitorTemplatesHolder` in sharedassets1.assets): a UGUI tree of images, sprites and
TextMeshPro texts per template. The client draws the three it needs (Timer, TeamsCommonRoundScore, AlivePlayers; the final-score word 6 shares the score
face) as its own UGUI tree with the numbers of ArenaMapPort.Client/ScoreMonitorLayout.cs, which were decoded from those templates. This tool
exports the nine sprites that tree uses, as PNG plus a manifest, into the ignored work/extract/monitors; the client build stages them to
BepInEx/plugins/maschine-ArenaMapPort/monitors/ (plaintext, like the selection artwork: Arena art, never in git).

What is NOT exported on purpose: the three 1920 x 1080 noise sprites (13 MB, an animated overlay at alpha 0.24 to 0.52), the Interference texture
(a scrolling CRT effect), and every sprite of the templates the port does not draw (point boards, player boards, scoreboards). The static faces
carry no animated effect.

What is read: sharedassets1.assets (Sprite and Texture2D objects) and, through the texture's StreamData, sharedassets1.assets.resS. Neither is one of
the files tools/bundle/key-sources.json samples for the stage-2 content keys (those are the sharedassets<N> of the maps' own scenes); the tool refuses to
run if that ever changes, and it pins the DECODED RGBA pixels of every sprite (SHA-256), never the bytes of an Arena file.

A sprite is exported as the full texture, which for these nine equals the sprite's rect (checked) and, where Unity draws a tight mesh inside it
(the skulls, the timer glow, the toggle-off disc), carries transparent pixels around it: drawing the PNG over the sprite's rect is what Unity draws.
Preserve-aspect fits therefore use the rect size, as Unity's Image does.

Package (work/extract/monitors, nothing else may be in the folder):
  monitors.json   schema 1: arena_version, and per sprite name / file / width / height / pivot / ppu / border (l, b, r, t) / pixels_sha256 (decoded RGBA)
                  / bytes + sha256 (the PNG) / used_by / source (asset, sprite and texture path ids, texture format)
  <sprite name>.png

CLI:
    python tools/extract/extract_monitor_templates.py [--source <Arena Data folder>] [--output work/extract/monitors] [--check]
`--check` validates an existing package and writes nothing. The output is byte-stable for one Pillow version.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
DEFAULT_SOURCE = Path("D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data")
DEFAULT_OUTPUT = REPO / "work" / "extract" / "monitors"
KEY_SOURCES = REPO / "tools" / "bundle" / "key-sources.json"

SCHEMA_VERSION = 1
MANIFEST = "monitors.json"
SOURCE_ASSET = "sharedassets1.assets"
MANIFEST_KEYS = {"schema_version", "arena_version", "sprites"}
SPRITE_KEYS = {"name", "file", "width", "height", "pivot", "ppu", "border", "pixels_sha256", "bytes", "sha256", "used_by", "source"}

# name -> pins. size = the sprite rect (= the texture), border = the 9-slice border l, b, r, t in sprite pixels (only the vignette is sliced),
# used_by = the faces of ScoreMonitorLayout that draw it, pixels_sha256 = SHA-256 of the decoded RGBA8 pixels, top row first. A future Arena artwork
# revision needs a new look at the faces, even at the same size, so a changed pixel hash stops the tool (the selection map does the same).
SPRITES = {
    "Arena_Scoreboard_AllBoards_Background": {
        "size": (1920, 1080), "border": (0, 0, 0, 0), "used_by": ("timer",),
        "pixels_sha256": "9e141d79a06b4f017953d5e9a9241edff9eeac39edf32be8409debb10ef8b184"},
    "Arena_Scoreboard_Timer_Glow_White": {
        "size": (1920, 1080), "border": (0, 0, 0, 0), "used_by": ("timer",),
        "pixels_sha256": "b6a214e5a3e9313e2627865d2cb9b62f626c7c968bac562d3fd7afcf562c226e"},
    "Arena_Scoreboard_AllBoards_Vignette": {
        "size": (1920, 1080), "border": (80, 80, 80, 80), "used_by": ("timer", "score", "alive"),
        "pixels_sha256": "bce131fb69f116fc025640910e529f24d73b4faeabd46cee7112e4c0937100b5"},
    "Arena_Scoreboard_AllBoards_Icon_Glow": {
        "size": (1250, 500), "border": (0, 0, 0, 0), "used_by": ("score", "alive"),
        "pixels_sha256": "58da285873ec77211302818fb3600a40a18ded0a3e31d6d4dab7b4d64150bc44"},
    "Arena_Scoreboard_AllBoards_Icon_Background": {
        "size": (356, 356), "border": (0, 0, 0, 0), "used_by": ("score", "alive"),
        "pixels_sha256": "5992f1ca2e1c0d36bb1a330fa47beb14581e30eeef526c5e55c4f8f4b4baa38d"},
    "Arena_Scoreboard_Score_Icon_Scull": {
        "size": (250, 250), "border": (0, 0, 0, 0), "used_by": ("score",),
        "pixels_sha256": "8fd77d912d7516cca3da6fcd97224caa326fddae59925e52d2df59850d81f7c7"},
    "Arena_Scoreboard_Status_Icon_Scull": {
        "size": (250, 250), "border": (0, 0, 0, 0), "used_by": ("alive",),
        "pixels_sha256": "a15009138221390b10242e2fa3e83d824782d4b2c13cc7a003b52c015815a601"},
    "Arena_Scoreboard_Status_Toggle_On": {
        "size": (178, 178), "border": (0, 0, 0, 0), "used_by": ("alive",),
        "pixels_sha256": "53394ad45b2688e130e69067ddbd7cee1b6d88b8d7dc58cf9f08994c8ae62b07"},
    "Arena_Scoreboard_Status_Toggle_Off": {
        "size": (178, 178), "border": (0, 0, 0, 0), "used_by": ("alive",),
        "pixels_sha256": "983a850c7489ad712a51dc3297fdd7108eacebda8d4cfac06b0f358dcb80dd3c"},
}
PIVOT = (0.5, 0.5)
PPU = 100.0
FACES = ("timer", "score", "alive")


class MonitorTemplateError(Exception):
    """A violated contract of the data (fails closed: nothing is written)."""


def key_source_files() -> set[str]:
    """The base names of every file the stage-2 key derivation samples (tools/bundle/key-sources.json), over all maps."""
    table = json.loads(KEY_SOURCES.read_text(encoding="utf-8"))["maps"]
    return {Path(name).name for entries in table.values() for entry in entries for name in entry["files"]}


def file_name(sprite: str) -> str:
    return sprite + ".png"


def pixel_hash(image) -> str:
    """SHA-256 of the decoded RGBA8 pixels, top row first (independent of the PNG encoder and of its colour mode)."""
    return hashlib.sha256(image.convert("RGBA").tobytes()).hexdigest()


def check_pins_complete() -> None:
    """Every sprite has a pixel pin, a known face list, a plain size and a border that fits its size."""
    for name, pins in SPRITES.items():
        width, height = pins["size"]
        if not pins["pixels_sha256"] or len(pins["pixels_sha256"]) != 64:
            raise MonitorTemplateError(f"{name}: no pixel pin yet (run with --pin to print them)")
        if not pins["used_by"] or any(face not in FACES for face in pins["used_by"]):
            raise MonitorTemplateError(f"{name}: used_by must name faces of {FACES}")
        left, bottom, right, top = pins["border"]
        if min(pins["border"]) < 0 or left + right > width or bottom + top > height:
            raise MonitorTemplateError(f"{name}: the border does not fit the sprite")


def check_image(name: str, image) -> None:
    """The decoded sprite against its pins: size and pixel hash."""
    pins = SPRITES[name]
    if image.size != tuple(pins["size"]):
        raise MonitorTemplateError(f"{name}: the sprite is {image.size}, expected {tuple(pins['size'])}: review the face layout before exporting")
    if pixel_hash(image) != pins["pixels_sha256"]:
        raise MonitorTemplateError(f"{name}: the artwork changed (pixel hash differs); recheck the face layout before exporting")


def png_bytes(image) -> bytes:
    """A deterministic PNG: RGB when the sprite is fully opaque (the background), RGBA otherwise."""
    import io
    rgba = image.convert("RGBA")
    if rgba.getchannel("A").getextrema() == (255, 255):
        rgba = rgba.convert("RGB")
    buffer = io.BytesIO()
    rgba.save(buffer, format="PNG", compress_level=9, optimize=False)
    return buffer.getvalue()


def manifest_entry(name: str, data: bytes, source: dict) -> dict:
    pins = SPRITES[name]
    return {"name": name, "file": file_name(name), "width": pins["size"][0], "height": pins["size"][1], "pivot": list(PIVOT), "ppu": PPU,
            "border": list(pins["border"]), "pixels_sha256": pins["pixels_sha256"], "bytes": len(data),
            "sha256": hashlib.sha256(data).hexdigest(), "used_by": list(pins["used_by"]), "source": source}


def render_manifest(arena_version: str, entries: list[dict]) -> str:
    """The manifest text: one sprite per line, so a diff reads sprite by sprite."""
    lines = ["{", '  "schema_version": %d,' % SCHEMA_VERSION, '  "arena_version": %s,' % json.dumps(arena_version), '  "sprites": [',
             ",\n".join("    " + json.dumps(e, separators=(", ", ": ")) for e in entries), "  ]", "}"]
    return "\n".join(lines) + "\n"


def validate(output) -> dict:
    """The package at `output` is exactly this tool's: manifest identity and layout, every sprite's table values, file bytes and SHA-256, the decoded
    pixels against the pins, and no other file. Returns the manifest."""
    from PIL import Image
    check_pins_complete()
    output = Path(output)
    manifest_path = output / MANIFEST
    if not manifest_path.is_file():
        raise MonitorTemplateError(f"{MANIFEST} is missing in {output}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(manifest, dict) or set(manifest) != MANIFEST_KEYS or manifest["schema_version"] != SCHEMA_VERSION \
            or not isinstance(manifest["arena_version"], str):
        raise MonitorTemplateError("the monitor manifest has an unsupported identity or layout")
    sprites = manifest["sprites"]
    if not isinstance(sprites, list) or [s.get("name") if isinstance(s, dict) else None for s in sprites] != list(SPRITES):
        raise MonitorTemplateError(f"the monitor manifest must list exactly the sprites {list(SPRITES)} in that order")
    for entry in sprites:
        name = entry["name"]
        pins = SPRITES[name]
        if set(entry) != SPRITE_KEYS or entry["file"] != file_name(name):
            raise MonitorTemplateError(f"{name}: the manifest entry has an unsupported layout or file name")
        if (entry["width"], entry["height"]) != tuple(pins["size"]) or entry["pivot"] != list(PIVOT) or entry["ppu"] != PPU \
                or entry["border"] != list(pins["border"]) or entry["used_by"] != list(pins["used_by"]) \
                or entry["pixels_sha256"] != pins["pixels_sha256"]:
            raise MonitorTemplateError(f"{name}: the manifest entry disagrees with the reviewed sprite table")
        source = entry["source"]
        if not isinstance(source, dict) or set(source) != {"asset", "sprite_path_id", "texture_path_id", "texture_format"} \
                or source["asset"] != SOURCE_ASSET:
            raise MonitorTemplateError(f"{name}: the manifest entry has no valid source")
        data = (output / entry["file"]).read_bytes() if (output / entry["file"]).is_file() else None
        if data is None:
            raise MonitorTemplateError(f"{name}: {entry['file']} is missing")
        if len(data) != entry["bytes"] or hashlib.sha256(data).hexdigest() != entry["sha256"]:
            raise MonitorTemplateError(f"{name}: {entry['file']} does not match its manifest entry (length or SHA-256)")
        with Image.open(output / entry["file"]) as image:
            image.load()
            check_image(name, image)
    if {p.name for p in output.iterdir()} != {MANIFEST, *(file_name(n) for n in SPRITES)}:
        raise MonitorTemplateError("unexpected monitor package payload")
    return manifest


def read_sprites(source: Path) -> tuple[dict, str]:
    """{name: (RGBA image, source info)} read from sharedassets1.assets, and the Arena version. Every sprite must exist exactly once, be one whole
    texture (rect == texture) and stream from a file that is no key source."""
    import UnityPy
    asset = source / SOURCE_ASSET
    if not asset.is_file():
        raise MonitorTemplateError(f"{asset} not found")
    protected = key_source_files()
    if SOURCE_ASSET in protected or any(n.startswith(SOURCE_ASSET) for n in protected):
        raise MonitorTemplateError(f"{SOURCE_ASSET} is a stage-2 key-source file now: the sprites would be sampled bytes; stop and rethink the export")
    env = UnityPy.load(str(asset))
    found: dict[str, list] = {}
    for obj in env.objects:
        if obj.type.name == "Sprite":
            name = obj.peek_name()
            if name in SPRITES:
                found.setdefault(name, []).append(obj)
    out = {}
    for name in SPRITES:
        objects = found.get(name, [])
        if len(objects) != 1:
            raise MonitorTemplateError(f"{name}: {len(objects)} sprites of that name in {SOURCE_ASSET}, expected exactly one")
        sprite = objects[0].read()
        rect = sprite.m_Rect
        if (rect.x, rect.y) != (0.0, 0.0):
            raise MonitorTemplateError(f"{name}: the sprite rect does not start at the texture origin")
        texture = sprite.m_RD.texture.read()
        if (rect.width, rect.height) != (float(texture.m_Width), float(texture.m_Height)):
            raise MonitorTemplateError(f"{name}: the sprite rect {rect.width}x{rect.height} is not the whole {texture.m_Width}x{texture.m_Height} texture")
        stream = getattr(texture, "m_StreamData", None)
        streamed = Path(stream.path).name if stream is not None and stream.path else ""
        if streamed in protected:
            raise MonitorTemplateError(f"{name}: its texture streams from {streamed}, a stage-2 key-source file")
        if (sprite.m_Pivot.x, sprite.m_Pivot.y) != PIVOT or sprite.m_PixelsToUnits != PPU:
            raise MonitorTemplateError(f"{name}: pivot / pixels per unit differ from the reviewed values")
        border = (sprite.m_Border.x, sprite.m_Border.y, sprite.m_Border.z, sprite.m_Border.w)
        if tuple(float(v) for v in SPRITES[name]["border"]) != border:
            raise MonitorTemplateError(f"{name}: the sprite border {border} differs from the reviewed {SPRITES[name]['border']}")
        image = texture.image.convert("RGBA")
        check_image(name, image)
        out[name] = (image, {"asset": SOURCE_ASSET, "sprite_path_id": objects[0].path_id, "texture_path_id": sprite.m_RD.texture.path_id,
                             "texture_format": int(texture.m_TextureFormat)})
    return out, _arena_version(source)


def _arena_version(data_dir: Path) -> str:
    """The installed Arena build from the launcher manifest (ConsistencyInfo beside the Data directory), or '' when it cannot be read."""
    try:
        manifest = json.loads((data_dir.parent / "ConsistencyInfo").read_bytes().decode("utf-8-sig"))
        return str(manifest.get("Version", ""))
    except (OSError, ValueError):
        return ""


def extract(source, output) -> dict:
    source, output = Path(source).resolve(), Path(output).resolve()
    # Never write into an Arena install, even when an alternate source is used.
    for protected in (source, DEFAULT_SOURCE.resolve().parent):
        if output == protected or protected in output.parents:
            raise MonitorTemplateError("the monitor package must be written outside the Arena source")
    check_pins_complete()
    sprites, version = read_sprites(source)
    output.mkdir(parents=True, exist_ok=True)
    entries = []
    for name, (image, info) in sprites.items():
        data = png_bytes(image)
        (output / file_name(name)).write_bytes(data)
        entries.append(manifest_entry(name, data, info))
    (output / MANIFEST).write_text(render_manifest(version, entries), encoding="utf-8", newline="\n")
    keep = {MANIFEST, *(file_name(n) for n in SPRITES)}
    for stale in sorted(p for p in output.iterdir() if p.is_file() and p.name not in keep):
        stale.unlink()
    return validate(output)


def print_pins(source: Path) -> None:
    """Print the pixel hashes of the installed Arena's sprites (a human pastes them into SPRITES after looking at the artwork)."""
    import UnityPy
    asset = source / SOURCE_ASSET
    env = UnityPy.load(str(asset))
    for obj in env.objects:
        if obj.type.name == "Sprite" and obj.peek_name() in SPRITES:
            texture = obj.read().m_RD.texture.read()
            print(f"{obj.peek_name()}: {texture.m_Width}x{texture.m_Height} {pixel_hash(texture.image)}")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE, help="the EscapeFromTarkovArena_Data folder of the Arena installation")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true", help="validate an existing package and write nothing")
    parser.add_argument("--pin", action="store_true", help="print the pixel hashes of the installed sprites (for a reviewed update of SPRITES)")
    args = parser.parse_args(argv)
    try:
        if args.pin:
            print_pins(args.source)
            return 0
        manifest = validate(args.output) if args.check else extract(args.source, args.output)
        total = sum(e["bytes"] for e in manifest["sprites"])
        print(f"{len(manifest['sprites'])} sprites, {total} bytes of PNG, Arena {manifest['arena_version'] or 'unknown'}")
        print("MONITOR_TEMPLATES_OK")
        return 0
    except (MonitorTemplateError, OSError, ValueError, KeyError) as exc:
        print(f"MONITOR_TEMPLATES_FAILED: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
