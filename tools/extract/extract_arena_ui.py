"""ArenaMapPort Arena UI sprite/texture extractor for ArenaMode (optional dependency, not used by
ArenaMapPort itself).

Mirrors `extract_arena_audio.py`'s shape and rules exactly, for a different asset class: this script
extracts a curated set of Arena's own 2D UI sprites/textures (killfeed icons, a K/D/A glyph triplet,
mode-select wordmark logos, kit-class medallion badges, capture-point progress/marker art, scoreboard
row backgrounds and a team-color palette, and an animated post-match MVP medal) read-only from the
user's own Arena install (0.5.4 and 0.6 verified), and stages plain RGBA PNGs into ArenaMapPort's own deployed plugin
folder so ArenaMode (a separate mod, D:/SPT41/Development/ArenaMode, read-only from here) can optionally
pick any of it up. ArenaMapPort itself never displays any of this. See docs/progress/arena-ui.md (the
contract for the staged folder and the ArenaMode integration notes) and
docs/ARENA-CONTENT-FOR-ARENAMODE.md (the survey this catalog was built from, plus a 2026-09-29 dated
correction on the killfeed-icon item).

BSG-derived art never goes into git (AGENTS.md): the only write targets are `work/` (gitignored, this
script's default `--output`) or `dist/` (gitignored, where the client csproj stages it for a Release
build). The only git-tracked file this script depends on is `arena_ui_catalog.json` -- group names, a
per-item Unity object name + a `path_id` drift-detection hint, a semantic label with a confidence rating,
and a suggested ArenaMode use (never pixels).

Every item is a Unity `Sprite` except the one `Killmetod_icon` strip, which is a raw `Texture2D` sliced
into 15 cells (see `slice_strip()`'s docstring for why 32px, not the 30px a naive reading of the 480x30
texture's own height would suggest, and why this is *derived*, never hardcoded). Selection is always by
the object's own `m_Name` (a `path_id` in the catalog is a drift-detection hint only, logged when it no
longer matches -- same rule as `extract_arena_audio.py`), except where Arena's own build genuinely reuses
one name for several distinct sprites (every `mode_logos` name, once per size tier): there,
the catalog records the reviewed sprite rectangle to identify the intended tier across both builds.
A changed or ambiguous rectangle is a hard failure; a recycled path ID never overrides it. Older
custom catalogs without rectangles retain their strict ID disambiguation.

CLI:
    python extract_arena_ui.py                    # extract every group into work/extract/arena-ui
    python extract_arena_ui.py --only killfeed     # extract just one group (repeatable flag)
    python extract_arena_ui.py --check             # verify manifest.json + files exist; touches no source
    python extract_arena_ui.py --source <ArenaData> --output <dir>
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)

from common_ids import HardExtractError  # noqa: E402

SCHEMA_VERSION = 1
GENERATOR = "extract_arena_ui.py"

# Same default Arena install every other extractor in this pipeline uses -- read-only.
DEFAULT_SOURCE = "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"

_REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = _REPO_ROOT / "work" / "extract" / "arena-ui"
DEFAULT_CATALOG_PATH = Path(__file__).resolve().parent / "arena_ui_catalog.json"

KNOWN_KINDS = ("sprite", "strip_slot")
KNOWN_CONFIDENCE = ("high", "medium", "low")
KNOWN_ROOTS = ("arena",)  # every item in this catalog reads from the Arena install; no EFT source needed


def _require(condition, message):
    if not condition:
        raise HardExtractError("ArenaUi: " + message)


# ---------------------------------------------------------------------------
# Catalog loading / validation
# ---------------------------------------------------------------------------

def load_catalog(path=None) -> dict:
    """Loads and integrity-checks arena_ui_catalog.json. Unity-independent -- no source data touched.

    Checks: schema_version == 1; a non-empty 'groups' dict, each with a non-empty 'purpose' string and a
    non-empty 'items' list; every item has 'key' (non-empty str, unique within its group), 'kind' (one of
    KNOWN_KINDS), 'root' (one of KNOWN_ROOTS), 'file' (non-empty str), 'label' (non-empty str),
    'confidence' (one of KNOWN_CONFIDENCE) and 'arenamode_use' (non-empty str). A 'sprite' item also needs
    a non-empty 'name' and an int 'path_id'. A 'strip_slot' item needs a 'strip' dict with 'strip_key'
    naming an entry in the top-level 'strips' dict and an int 'index'; when that slot also carries its own
    named Sprite (the catalog's 'has_named_sprite' flag), 'name'/'path_id' must be set too, matching
    extract_strip()'s own expectation. Every 'strips' entry needs 'root', 'file', 'texture_name'
    (non-empty str), an int 'texture_path_id', int 'texture_width'/'texture_height' and a positive int
    'expected_cell_count'. (group, key) pairs are unique across the whole catalog, since that pair is
    this script's own output path."""
    path = Path(path) if path is not None else DEFAULT_CATALOG_PATH
    _require(path.is_file(), f"catalog not found: {path}")
    catalog = json.loads(path.read_text(encoding="utf-8"))
    _require(catalog.get("schema_version") == 1, f"unsupported catalog schema_version in {path}")

    groups = catalog.get("groups")
    _require(isinstance(groups, dict) and groups, f"empty/invalid 'groups' dict in {path}")
    strips = catalog.get("strips", {})
    _require(isinstance(strips, dict), f"'strips' must be an object in {path}")
    for strip_key, strip in strips.items():
        _require(strip.get("root") in KNOWN_ROOTS, f"strips[{strip_key!r}]: invalid/missing 'root'")
        _require(isinstance(strip.get("file"), str) and strip["file"], f"strips[{strip_key!r}]: missing 'file'")
        _require(isinstance(strip.get("texture_name"), str) and strip["texture_name"],
                  f"strips[{strip_key!r}]: missing 'texture_name'")
        _require(isinstance(strip.get("texture_path_id"), int), f"strips[{strip_key!r}]: missing int 'texture_path_id'")
        _require(isinstance(strip.get("texture_width"), int) and strip["texture_width"] > 0,
                  f"strips[{strip_key!r}]: missing positive int 'texture_width'")
        _require(isinstance(strip.get("texture_height"), int) and strip["texture_height"] > 0,
                  f"strips[{strip_key!r}]: missing positive int 'texture_height'")
        _require(isinstance(strip.get("expected_cell_count"), int) and strip["expected_cell_count"] > 0,
                  f"strips[{strip_key!r}]: missing positive int 'expected_cell_count'")

    seen_global = set()
    for group_name, group in groups.items():
        _require(isinstance(group.get("purpose"), str) and group["purpose"], f"group {group_name!r}: missing 'purpose'")
        items = group.get("items")
        _require(isinstance(items, list) and items, f"group {group_name!r}: empty/invalid 'items' list")
        seen_in_group = set()
        for item in items:
            key = item.get("key")
            _require(isinstance(key, str) and key, f"group {group_name!r}: item missing a 'key'")
            _require(key not in seen_in_group, f"group {group_name!r}: duplicate item key {key!r}")
            seen_in_group.add(key)
            _require((group_name, key) not in seen_global, f"duplicate (group, key) across catalog: {(group_name, key)}")
            seen_global.add((group_name, key))

            tag = f"{group_name}/{key}"
            _require(item.get("kind") in KNOWN_KINDS, f"{tag}: invalid/missing 'kind' (known: {KNOWN_KINDS})")
            _require(item.get("root") in KNOWN_ROOTS, f"{tag}: invalid/missing 'root' (known: {KNOWN_ROOTS})")
            _require(isinstance(item.get("file"), str) and item["file"], f"{tag}: missing 'file'")
            _require(isinstance(item.get("label"), str) and item["label"], f"{tag}: missing 'label'")
            _require(item.get("confidence") in KNOWN_CONFIDENCE, f"{tag}: invalid/missing 'confidence'")
            _require(isinstance(item.get("arenamode_use"), str) and item["arenamode_use"], f"{tag}: missing 'arenamode_use'")

            if item["kind"] == "sprite":
                _require(isinstance(item.get("name"), str) and item["name"], f"{tag}: sprite item missing 'name'")
                _require(isinstance(item.get("path_id"), int), f"{tag}: sprite item missing int 'path_id'")
                if "sprite_rect" in item:
                    rect = item["sprite_rect"]
                    _require(isinstance(rect, list) and len(rect) == 4 and all(
                        type(v) in (int, float) and math.isfinite(v) for v in rect)
                        and min(rect[:2]) >= 0 and min(rect[2:]) > 0,
                        f"{tag}: sprite_rect must contain finite x/y and positive width/height")
            else:  # strip_slot
                strip_ref = item.get("strip")
                _require(isinstance(strip_ref, dict), f"{tag}: strip_slot item missing 'strip'")
                strip_key = strip_ref.get("strip_key")
                _require(strip_key in strips, f"{tag}: strip_slot references unknown strip {strip_key!r}")
                _require(isinstance(strip_ref.get("index"), int) and strip_ref["index"] >= 0,
                          f"{tag}: strip_slot missing a non-negative int 'index'")
                if strip_ref.get("has_named_sprite"):
                    _require(isinstance(item.get("name"), str) and item["name"],
                              f"{tag}: has_named_sprite=true but item is missing 'name'")
                    _require(isinstance(item.get("path_id"), int),
                              f"{tag}: has_named_sprite=true but item is missing int 'path_id'")
    return catalog


def output_path(output_dir, group: str, key: str) -> Path:
    """<output_dir>/<group>/<key>.png -- the catalog item's own unique 'key', not Unity's own m_Name
    (several catalog items, e.g. every mode_logos size tier, legitimately share one m_Name). Pure,
    Unity-independent."""
    return Path(output_dir) / group / f"{key}.png"


def natural_key(key: str):
    """Sort key that orders embedded numbers numerically: mvp_frame_2 before mvp_frame_10."""
    return [int(part) if part.isdigit() else part for part in re.split(r"(\d+)", key)]


def preview_path(output_dir, group: str) -> Path:
    return Path(output_dir) / "previews" / f"{group}.png"


# ---------------------------------------------------------------------------
# Strip slicing (pure -- operates on a PIL Image, no UnityPy) -----------------
# ---------------------------------------------------------------------------

def derive_strip_grid(named_slots, texture_width, texture_height, expected_cell_count):
    """named_slots: iterable of (index, x, y, width, height) for a strip's own named Sprite objects
    (Killmetod_icon's indices 10-14 today). Derives and validates a uniform left-to-right grid over the
    full texture, never assuming a cell width -- a naive reading of the 480x30 Killmetod_icon texture's
    own 30px HEIGHT as its cell WIDTH (this repo's own first guess, corrected this session) would be
    wrong: the true, live-verified grid is 32px (480 / 32 == 15 exactly), independently confirmed by
    these named sprites' own rects, not merely assumed.

    Requires: every named slot shares one width W and one height H; each slot's own y is 0 and its x
    equals index * W (a plain left-to-right grid starting at the texture's own left edge, no offset);
    texture_height == H; texture_width is evenly divisible by W into exactly expected_cell_count cells.
    Returns (cell_width, cell_height, cell_count). Raises HardExtractError on any disagreement -- this is
    the drift guard: if a future Arena update changes this strip's layout, extraction stops instead of
    silently slicing the wrong boxes."""
    named_slots = list(named_slots)
    _require(named_slots, "derive_strip_grid: no named slots given to derive the grid from")
    widths = {w for _, _, _, w, _ in named_slots}
    heights = {h for _, _, _, _, h in named_slots}
    _require(len(widths) == 1, f"strip's own named sprites disagree on cell width: {sorted(widths)}")
    _require(len(heights) == 1, f"strip's own named sprites disagree on cell height: {sorted(heights)}")
    cell_width_raw = widths.pop()
    cell_height_raw = heights.pop()
    cell_width, cell_height = int(cell_width_raw), int(cell_height_raw)
    _require(cell_width == cell_width_raw and cell_height == cell_height_raw,
              f"strip cell size is not a whole number of pixels: {cell_width_raw}x{cell_height_raw}")
    _require(cell_height == texture_height,
              f"strip's named-sprite cell height {cell_height} != texture height {texture_height}")
    for index, x, y, w, h in named_slots:
        _require(y == 0, f"strip slot {index}: expected y=0, got {y}")
        _require(x == index * cell_width,
                  f"strip slot {index}: expected x={index * cell_width} (index*cell_width), got {x} -- grid is not a plain left-to-right layout")
    _require(texture_width % cell_width == 0,
              f"strip texture width {texture_width} does not divide evenly by cell width {cell_width}")
    cell_count = texture_width // cell_width
    _require(cell_count == expected_cell_count,
              f"strip has {cell_count} cells at width {cell_width}, catalog expects {expected_cell_count}")
    return cell_width, cell_height, cell_count


def slice_uniform_grid(image, cell_width, cell_height):
    """image: a PIL Image. Returns a list of `cell_count` crops (image.width // cell_width, rounded down)
    of size (cell_width, cell_height) at y=0, left to right. Pure PIL, no UnityPy -- exercised directly in
    tests against small synthetic RGBA images."""
    count = image.width // cell_width
    return [image.crop((i * cell_width, 0, i * cell_width + cell_width, cell_height)) for i in range(count)]


# ---------------------------------------------------------------------------
# UnityPy-dependent reads
# ---------------------------------------------------------------------------

def _root_dir(root: str, arena_source) -> Path:
    _require(root in KNOWN_ROOTS, f"unknown source root {root!r} (known: {KNOWN_ROOTS})")
    return Path(arena_source)


def _load_environment(root_dir, relative_file: str):
    """Loads one source file read-only with UnityPy, matching every other extractor in this pipeline: an
    .assets file is loaded alongside globalgamemanagers.assets when present."""
    import UnityPy

    root_dir = Path(root_dir)
    path = root_dir / relative_file
    _require(path.is_file(), f"source file not found: {path} (is the Arena install path right?)")
    gm = root_dir / "globalgamemanagers.assets"
    if gm.is_file() and gm != path:
        return UnityPy.load(str(path), str(gm))
    return UnityPy.load(str(path))


def _build_name_index(env):
    """{(type_name, m_Name): [object reader, ...]} for every Sprite/Texture2D in the loaded environment.
    Built once per environment and reused for every catalog item that reads from it -- the per-name list
    (not a single object) is what lets mode_logos' legitimately-repeated names (one per size tier) be
    disambiguated by path_id afterwards instead of raising a false 'duplicate name' error."""
    index = {}
    for obj in env.objects:
        if obj.type.name not in ("Sprite", "Texture2D"):
            continue
        name = obj.read_typetree().get("m_Name")
        if not name:
            continue
        index.setdefault((obj.type.name, name), []).append(obj)
    return index


def _resolve(index, type_name, name, path_id_hint, tag, sprite_rect=None):
    candidates = index.get((type_name, name), [])
    _require(candidates, f"{tag}: expected {type_name} {name!r} not found")
    if sprite_rect is not None:
        # Arena 0.6 changed build-local IDs, including IDs recycled for a different
        # size tier of the same name. The reviewed rectangle remains stable in both
        # source generations. Require it even when the old ID happens to exist.
        _require(type_name == "Sprite", f"{tag}: sprite_rect requires a Sprite")
        matches = [o for o in candidates if all(math.isclose(a, b, rel_tol=0, abs_tol=0.001)
                   for a, b in zip(_sprite_geometry(o)[0], sprite_rect))]
        _require(len(matches) == 1,
                 f"{tag}: expected one {name!r} sprite with rect {sprite_rect}, found {len(matches)}")
        candidates = matches
    if len(candidates) == 1:
        obj = candidates[0]
        if path_id_hint is not None and obj.path_id != path_id_hint:
            print(f"  [warn] {tag}: path_id drifted from the recorded hint {path_id_hint} to {obj.path_id} (still matched by name)")
        return obj
    matches = [o for o in candidates if o.path_id == path_id_hint]
    _require(matches, f"{tag}: {len(candidates)} objects named {name!r} exist and none match the recorded "
                       f"path_id hint {path_id_hint} -- Arena's own build reuses this name, path_id is the "
                       f"only disambiguator and it no longer resolves")
    return matches[0]


def _sprite_geometry(obj):
    """Returns ((x, y, width, height), (pivot_x, pivot_y)) from a Sprite's own typetree -- m_Rect is the
    sprite's own rect within its parent texture (pixels), m_Pivot is normalized (0..1, 0.5/0.5 == center)."""
    tree = obj.read_typetree()
    rect = tree.get("m_Rect", {})
    pivot = tree.get("m_Pivot", {})
    return (
        (float(rect.get("x", 0)), float(rect.get("y", 0)), float(rect.get("width", 0)), float(rect.get("height", 0))),
        (float(pivot.get("x", 0.5)), float(pivot.get("y", 0.5))),
    )


def _avg_rgba(image):
    """Mean R/G/B/A across every pixel -- used for the scoreboard group's team-color palette (recorded in
    the manifest, never hardcoded). PIL's own box-filter downscale to 1x1 computes this mean directly."""
    from PIL import Image

    image = image.convert("RGBA")
    r, g, b, a = image.resize((1, 1), Image.BOX).getpixel((0, 0))
    return [r, g, b, a]


# ---------------------------------------------------------------------------
# Extraction
# ---------------------------------------------------------------------------

def extract_sprite_item(index, item, output_dir, arena_source):
    tag = f"{item['group']}/{item['key']}"
    obj = _resolve(index, "Sprite", item["name"], item.get("path_id"), tag, item.get("sprite_rect"))
    data = obj.read()
    _require(data.m_Name == item["name"],
              f"{tag}: object m_Name drifted to {data.m_Name!r} between indexing and read")
    image = data.image.convert("RGBA")
    (x, y, w, h), (pivot_x, pivot_y) = _sprite_geometry(obj)

    dest = output_path(output_dir, item["group"], item["key"])
    dest.parent.mkdir(parents=True, exist_ok=True)
    image.save(dest)

    entry = {
        "group": item["group"], "key": item["key"], "kind": "sprite",
        "source": {"root": item["root"], "file": item["file"]},
        "name": item["name"], "path_id": obj.path_id,
        "pixel_size": [image.width, image.height],
        "sprite_rect": {"x": x, "y": y, "width": w, "height": h},
        "sprite_pivot": {"x": pivot_x, "y": pivot_y},
        "label": item["label"], "confidence": item["confidence"], "arenamode_use": item["arenamode_use"],
        "file": dest.relative_to(output_dir).as_posix(),
    }
    if item["group"] == "scoreboard":
        entry["avg_color_rgba"] = _avg_rgba(image)
    return entry


def extract_strip_group(index, strip_key, strip_def, slot_items, output_dir):
    """Extracts every strip_slot item that references `strip_key`. Reads the strip's own raw Texture2D
    once, derives+validates its grid from the named slots (`derive_strip_grid`), slices every cell
    (`slice_uniform_grid`) and writes one PNG per catalog item. A named slot's own Sprite.image is used
    as-is (pixel-identical to the equivalent grid crop, confirmed this session) rather than re-derived,
    so its own sprite rect is recorded like any other sprite item; an unnamed slot's manifest entry
    instead records the strip's own {x, y, width, height} cell box."""
    tex_tag = f"strip:{strip_key}"
    tex_obj = _resolve(index, "Texture2D", strip_def["texture_name"], strip_def.get("texture_path_id"), tex_tag)
    tex_data = tex_obj.read()
    _require(tex_data.m_Name == strip_def["texture_name"],
              f"{tex_tag}: texture m_Name drifted to {tex_data.m_Name!r}")
    full_image = tex_data.image.convert("RGBA")
    _require(full_image.width == strip_def["texture_width"] and full_image.height == strip_def["texture_height"],
              f"{tex_tag}: texture is {full_image.width}x{full_image.height}, catalog expects "
              f"{strip_def['texture_width']}x{strip_def['texture_height']}")

    named_slots = []
    named_by_index = {}
    for item in slot_items:
        if item["strip"].get("has_named_sprite"):
            obj = _resolve(index, "Sprite", item["name"], item.get("path_id"), f"{item['group']}/{item['key']}")
            data = obj.read()
            (x, y, w, h), pivot = _sprite_geometry(obj)
            idx = item["strip"]["index"]
            named_slots.append((idx, x, y, w, h))
            named_by_index[idx] = (obj, data, x, y, w, h, pivot)

    cell_width, cell_height, cell_count = derive_strip_grid(
        named_slots, strip_def["texture_width"], strip_def["texture_height"], strip_def["expected_cell_count"])
    crops = slice_uniform_grid(full_image, cell_width, cell_height)

    entries = {}
    for item in slot_items:
        idx = item["strip"]["index"]
        tag = f"{item['group']}/{item['key']}"
        _require(0 <= idx < cell_count, f"{tag}: strip index {idx} out of range (0..{cell_count - 1})")
        dest = output_path(output_dir, item["group"], item["key"])
        dest.parent.mkdir(parents=True, exist_ok=True)
        if idx in named_by_index:
            obj, data, x, y, w, h, pivot = named_by_index[idx]
            image = data.image.convert("RGBA")
            rect = {"x": x, "y": y, "width": w, "height": h}
            pivot_entry = {"x": pivot[0], "y": pivot[1]}
            name, path_id = item["name"], obj.path_id
        else:
            image = crops[idx]
            rect = {"x": idx * cell_width, "y": 0, "width": cell_width, "height": cell_height}
            pivot_entry = None  # no Sprite object for this cell -- no pivot to report
            name, path_id = None, None
        image.save(dest)
        entries[item["key"]] = {
            "group": item["group"], "key": item["key"], "kind": "strip_slot",
            "source": {"root": item["root"], "file": item["file"]},
            "strip_key": strip_key, "strip_index": idx,
            "name": name, "path_id": path_id,
            "pixel_size": [image.width, image.height], "sprite_rect": rect, "sprite_pivot": pivot_entry,
            "label": item["label"], "confidence": item["confidence"], "arenamode_use": item["arenamode_use"],
            "file": dest.relative_to(output_dir).as_posix(),
        }
    return entries


def build_preview(group_name, entries, output_dir):
    """Writes a dark-background, 3x-nearest-neighbour contact sheet for one group's own extracted items
    to <output_dir>/previews/<group>.png -- for a human to eyeball, never read back by this script."""
    from PIL import Image, ImageDraw, ImageFont

    font = ImageFont.load_default()
    scale = 3
    pad = 14
    cell_w = 190
    cols = max(1, 1400 // cell_w)
    rows = (len(entries) + cols - 1) // cols
    row_h = 0
    thumbs = []
    for entry in entries:
        img = Image.open(Path(output_dir) / entry["file"]).convert("RGBA")
        capped = img.copy()
        capped.thumbnail((cell_w - 2 * pad, 160), Image.LANCZOS if img.width > 64 or img.height > 64 else Image.NEAREST)
        if img.width <= 64 and img.height <= 64:
            capped = img.resize((img.width * scale, img.height * scale), Image.NEAREST)
            capped.thumbnail((cell_w - 2 * pad, 160), Image.NEAREST)
        thumbs.append((entry["key"], capped))
        row_h = max(row_h, capped.height)
    cell_h = row_h + 46
    sheet = Image.new("RGBA", (cols * cell_w, rows * cell_h), (22, 24, 28, 255))
    draw = ImageDraw.Draw(sheet)
    for i, (label, img) in enumerate(thumbs):
        col, row = i % cols, i // cols
        cx, cy = col * cell_w, row * cell_h
        x = cx + (cell_w - img.width) // 2
        sheet.alpha_composite(img, (x, cy + pad))
        draw.rectangle([cx, cy, cx + cell_w - 1, cy + cell_h - 1], outline=(55, 55, 60, 255))
        draw.text((cx + 6, cy + cell_h - 16), label[: cell_w // 6], fill=(190, 190, 120, 255), font=font)
    dest = preview_path(output_dir, group_name)
    dest.parent.mkdir(parents=True, exist_ok=True)
    sheet.convert("RGB").save(dest)
    return dest


def extract_group(group_name, group_def, catalog, arena_source, output_dir, env_cache, index_cache, make_preview=True):
    sprite_items = []
    strip_items_by_key = {}
    for raw_item in group_def["items"]:
        item = dict(raw_item)
        item["group"] = group_name
        if item["kind"] == "sprite":
            sprite_items.append(item)
        else:
            strip_items_by_key.setdefault(item["strip"]["strip_key"], []).append(item)

    def _get_index(root, file):
        cache_key = (root, file)
        if cache_key not in env_cache:
            env_cache[cache_key] = _load_environment(_root_dir(root, arena_source), file)
        if cache_key not in index_cache:
            index_cache[cache_key] = _build_name_index(env_cache[cache_key])
        return index_cache[cache_key]

    entries = []
    for item in sprite_items:
        index = _get_index(item["root"], item["file"])
        entries.append(extract_sprite_item(index, item, output_dir, arena_source))

    for strip_key, items in strip_items_by_key.items():
        strip_def = catalog["strips"][strip_key]
        index = _get_index(strip_def["root"], strip_def["file"])
        by_key = extract_strip_group(index, strip_key, strip_def, items, output_dir)
        entries.extend(by_key[item["key"]] for item in items)

    entries.sort(key=lambda e: natural_key(e["key"]))
    print(f"[{group_name}] {len(entries)} item(s) ok")
    if make_preview:
        preview = build_preview(group_name, entries, output_dir)
        print(f"[{group_name}] preview -> {preview}")
    return entries


def extract_all(arena_source=DEFAULT_SOURCE, output_dir=DEFAULT_OUTPUT, only=None,
                catalog_path=None, make_previews=True) -> dict:
    """Extracts the requested groups (default: every group) and writes manifest.json, atomically and
    idempotently. A partial run (`only`) merges into whatever manifest.json already exists on disk, like
    extract_arena_audio.py's own extract_all()."""
    catalog = load_catalog(catalog_path)
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = output_dir / "manifest.json"

    existing = {}
    if manifest_path.is_file():
        try:
            existing = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            existing = {}

    all_groups = sorted(catalog["groups"])
    requested = set(only) if only else set(all_groups)
    unknown = requested - set(all_groups)
    _require(not unknown, f"unknown --only group(s): {sorted(unknown)} (known: {all_groups})")

    groups_out = {g: data for g, data in existing.get("groups", {}).items() if g not in requested}

    env_cache, index_cache = {}, {}
    for group_name in sorted(requested):
        entries = extract_group(group_name, catalog["groups"][group_name], catalog, arena_source,
                                output_dir, env_cache, index_cache, make_preview=make_previews)
        groups_out[group_name] = {"purpose": catalog["groups"][group_name]["purpose"], "items": entries}

    manifest = {
        "schema_version": SCHEMA_VERSION,
        "generator": GENERATOR,
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "source": {"root": "arena", "dir": str(arena_source)},
        "groups": groups_out,
    }
    payload = json.dumps(manifest, indent=2, sort_keys=False).encode("utf-8") + b"\n"
    tmp = manifest_path.with_suffix(".json.tmp")
    tmp.write_bytes(payload)
    tmp.replace(manifest_path)
    return manifest


def _iter_manifest_items(manifest):
    for group_data in manifest.get("groups", {}).values():
        yield from group_data.get("items", [])


def check(output_dir=DEFAULT_OUTPUT) -> dict:
    """--check: confirms manifest.json exists and every file it references exists and is non-empty.
    Touches no source install and does not import UnityPy. Returns {} on success, else
    {problem_key: detail}."""
    output_dir = Path(output_dir)
    manifest_path = output_dir / "manifest.json"
    if not manifest_path.is_file():
        return {"manifest.json": str(manifest_path)}
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return {"manifest.json": f"{manifest_path} (unreadable: {exc})"}

    missing = {}
    for item in _iter_manifest_items(manifest):
        rel = item.get("file")
        if not rel:
            missing[f"{item.get('group', '?')}:{item.get('key', '?')}"] = "manifest entry missing 'file'"
            continue
        path = output_dir / rel
        if not path.is_file() or path.stat().st_size == 0:
            missing[rel] = str(path)
    return missing


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", default=DEFAULT_SOURCE,
                        help="Arena's EscapeFromTarkovArena_Data directory (read-only)")
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT),
                        help="directory to write <group>/<key>.png (+ manifest.json, previews/<group>.png) into")
    parser.add_argument("--only", action="append",
                        help="extract only this group (repeatable); default: every group in the catalog")
    parser.add_argument("--check", action="store_true",
                         help="verify manifest.json and referenced files exist/are non-empty; touches no source")
    parser.add_argument("--no-previews", action="store_true", help="skip writing previews/<group>.png contact sheets")
    parser.add_argument("--catalog", default=None, help="override path to arena_ui_catalog.json")
    args = parser.parse_args(argv)

    if args.check:
        missing = check(args.output)
        if missing:
            for key, detail in sorted(missing.items()):
                print(f"MISSING: {key} -> {detail}")
            return 1
        print(f"OK: manifest and referenced files present under {args.output}")
        return 0

    import time
    start = time.time()
    try:
        manifest = extract_all(args.source, args.output, only=args.only, catalog_path=args.catalog,
                               make_previews=not args.no_previews)
    except HardExtractError as e:
        print(f"FAILED: {e}", file=sys.stderr)
        return 1

    elapsed = time.time() - start
    output_dir = Path(args.output)
    total_items = 0
    total_bytes = 0
    for item in _iter_manifest_items(manifest):
        total_items += 1
        total_bytes += (output_dir / item["file"]).stat().st_size
    print(f"OK: {total_items} item(s), {total_bytes / 1024 / 1024:.2f} MiB, {elapsed:.1f}s wall -> {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
