"""Source-backed ballistic material components for explicitly opted-in maps.

Original preset names establish material identity; source enum integers and preset
indices are never treated as target IDs. No source values or assets are embedded.
"""
from __future__ import annotations

from collections import Counter
import hashlib
import math
from pathlib import Path
import re
import struct

from common_ids import HardExtractError

SCRIPT_NAME = "EFT.Ballistics.BallisticCollider"
VALUE_FIELDS = ("penetration_level", "penetration_chance", "ricochet_chance",
                "fragmentation_chance", "trajectory_deviation_chance", "trajectory_deviation")
PHYSICS_TYPES = frozenset(("MeshCollider", "BoxCollider", "SphereCollider", "CapsuleCollider"))
# Public target API symbols, deliberately without numeric enum values. Unity
# additionally verifies each resolved name/value against its actual game assembly.
MATERIAL_NAMES = frozenset("None Asphalt Body Cardboard Chainfence Concrete Fabric GarbageMetal "
                          "GarbagePaper GenericSoft Glass GlassShattered Grate GrassHigh GrassLow "
                          "Gravel MetalThin MetalThick Mud Pebbles Plastic Stone Soil SoilForest "
                          "Tile Water WaterPuddle WoodThin WoodThick Tyre Rubber GenericHard "
                          "BodyArmor Swamp Helmet GlassVisor HelmetRicochet MetalNoDecal Snow".split())


# Arena components whose named preset and surface material disagree, reviewed one pair at a time: the component
# is imported exactly as authored (EFT keeps TypeOfMaterial and Preset as two fields, so Arena itself ran it that
# way, and the validator's independent decoding expects the same two values). Bowl, 2026-10-18: the two
# 'Guardian_Tower_Fance_BALLISTIC_MetalThin' colliders of Arena_Bowl (level41) carry the preset Chainfence over the
# material MetalThin. Any other pair still fails the extraction closed ("preset/material disagreement").
REVIEWED_PRESET_MATERIAL_PAIRS = frozenset({("Chainfence", "MetalThin")})


def _require(condition, message):
    if not condition:
        raise HardExtractError("Ballistics: " + message)


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _values_valid(values):
    _require(len(values) == 6 and all(type(v) in (int, float) and math.isfinite(v) for v in values),
             "invalid six-value payload")
    _require(0 <= values[0] <= 3.4028234663852886e38 and all(0 <= v <= 1 for v in values[1:]),
             "ballistic value outside domain")


def script_name(reader):
    """Read only Unity's stable MonoBehaviour header; reject broken non-null references."""
    try:
        script = reader.parse_monobehaviour_head().m_Script
        if script is None or script.path_id == 0:
            return None
        tree = script.read_typetree()
        name, namespace = tree["m_ClassName"], tree.get("m_Namespace", "")
        _require(isinstance(name, str) and isinstance(namespace, str), "invalid MonoScript class/namespace")
        # Unity resources contain stripped, unrelated MonoScripts with empty
        # class names. They cannot identify a ballistic component or settings.
        if not name:
            return None
        return namespace + "." + name if namespace else name
    except HardExtractError:
        raise
    except Exception as error:
        raise HardExtractError(f"Ballistics: cannot resolve MonoScript for {reader.path_id}: {error}") from error


def parse_preset_table(raw):
    """Locate the unique named ColliderPresets table in EFTHardSettings bytes.

Only the proven empty scale-curve representation is supported. A future scale-
dependent preset aborts extraction instead of guessing a Keyframe layout.
"""
    candidates = []
    for match in re.finditer(re.escape(struct.pack("<i", 4) + b"None"), raw):
        start = match.start() - 4
        if start < 0 or start % 4:
            continue
        count = struct.unpack_from("<i", raw, start)[0]
        if not 1 <= count <= 256:
            continue
        pos, presets = start + 4, []
        try:
            for index in range(count):
                length = struct.unpack_from("<i", raw, pos)[0]
                pos += 4
                _require(1 <= length <= 100, "invalid preset name length")
                name = raw[pos:pos + length].decode("ascii")
                _require(re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", name), "invalid preset name")
                pos = (pos + length + 3) & ~3
                material, value_count = struct.unpack_from("<ii", raw, pos)
                pos += 8
                _require(material >= 0 and value_count == 6, "invalid preset material/value count")
                values = struct.unpack_from("<6f", raw, pos)
                pos += 24
                _values_valid(values)
                depend_on_scale, key_count = struct.unpack_from("<ii", raw, pos)
                pos += 8
                _require(depend_on_scale == 0 and key_count == 0, "scale-aware presets require an adapter")
                curve_tail = struct.unpack_from("<iii", raw, pos)
                pos += 12
                _require(curve_tail == (2, 2, 4), "unknown empty scale curve")
                presets.append({"index": index, "name": name, "material_id": material,
                                "values": dict(zip(VALUE_FIELDS, values)), "depend_on_scale": False})
            names = {p["name"] for p in presets}
            _require(len(names) == count, "duplicate preset names")
            _require({"None", "MetalThin", "WoodThin", "Concrete", "Glass"} <= names,
                     "named ballistic anchor presets missing")
            candidates.append({"offset": start, "byte_length": pos - start,
                               "sha256": _sha(raw[start:pos]), "presets": presets})
        except (HardExtractError, UnicodeDecodeError, struct.error):
            continue
    _require(len(candidates) == 1, f"expected one supported preset table; found {len(candidates)}")
    return candidates[0]


def _read_presets(data_root):
    import UnityPy
    root = Path(data_root)
    path = root / "resources.assets"
    env = UnityPy.load(str(path), str(root / "globalgamemanagers.assets"))
    source_file = env.files.get(str(path))
    _require(source_file is not None, f"resources.assets unavailable: {root}")
    matches = [reader for reader in source_file.objects.values()
               if reader.type.name == "MonoBehaviour" and script_name(reader) == "EFTHardSettings"]
    _require(len(matches) == 1, "expected exactly one EFTHardSettings resource")
    reader = matches[0]
    raw = reader.get_raw_data()
    result = parse_preset_table(raw)
    result.update({"file": "resources.assets", "component_id": str(reader.path_id),
                   "object_sha256": _sha(raw)})
    return result


def _bindings(contract):
    _require(isinstance(contract, dict) and type(contract.get("schema_version")) is int and
             contract["schema_version"] == 1, "unsupported contract")
    maps = []
    for key in ("source_presets", "target_presets"):
        table = contract.get(key)
        _require(isinstance(table, dict), "missing source/target preset table")
        rows = table.get("presets")
        _require(isinstance(rows, list) and 1 <= len(rows) <= 256, "invalid preset rows")
        names, materials = {}, {}
        for index, row in enumerate(rows):
            _require(isinstance(row, dict), "invalid preset row")
            _require(type(row.get("index")) is int and row["index"] == index and
                     isinstance(row.get("name"), str) and re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", row["name"]) and
                     row["name"] not in names, "invalid/duplicate preset index or name")
            _require(type(row.get("material_id")) is int and 0 <= row["material_id"] <= 2147483647,
                     "invalid preset material ID")
            _require(isinstance(row.get("values"), dict) and set(row["values"]) == set(VALUE_FIELDS),
                     "unexpected preset value fields")
            _values_valid([row["values"][f] for f in VALUE_FIELDS])
            _require(row.get("depend_on_scale") is False, "scale-aware contract is unsupported")
            names[row["name"]] = row
            if row["name"] in MATERIAL_NAMES:
                _require(row["material_id"] not in materials, "ambiguous canonical material identity")
                materials[row["material_id"]] = row["name"]
        _require(all(row["material_id"] in materials for row in rows), "unknown source/target material symbol")
        maps.append((names, materials))
    source, target = maps
    _require(set(source[0]) <= set(target[0]), "target is missing a named source preset")
    for name, row in source[0].items():
        _require(source[1][row["material_id"]] == target[1][target[0][name]["material_id"]],
                 f"named preset changed material identity: {name}")
    return source, target


def load_contract(source_root, target_root):
    """Read original/installed preset tables once. Result is JSON-serializable provenance."""
    result = {"schema_version": 1, "source_presets": _read_presets(source_root),
              "target_presets": _read_presets(target_root)}
    _bindings(result)
    return result


def decode_payload(raw):
    """Decode the validated Arena v1 body; never silently tolerate trailing bytes."""
    _require(len(raw) >= 32, "truncated MonoBehaviour header")
    owner_file, owner_id = struct.unpack_from("<iq", raw, 0)
    _require(owner_file == 0 and owner_id > 0, "owner must be a scene-local GameObject")
    enabled = raw[12]
    _require(enabled in (0, 1) and raw[13:16] == b"\0\0\0", "invalid enabled field/padding")
    _, script_id = struct.unpack_from("<iq", raw, 16)
    _require(script_id != 0, "null ballistic script")
    name_length = struct.unpack_from("<i", raw, 28)[0]
    _require(0 <= name_length <= len(raw) - 32, "invalid MonoBehaviour name length")
    body = (32 + name_length + 3) & ~3
    _require(len(raw) - body == 44, "unknown BallisticCollider payload layout")
    surface, preset, net, hit, material = struct.unpack_from("<5i", raw, body)
    values = struct.unpack_from("<6f", raw, body + 20)
    _values_valid(values)
    return {"owner_id": owner_id, "enabled": bool(enabled), "surface": surface,
            "preset": preset, "net": net, "hit": hit, "material": material,
            "values": dict(zip(VALUE_FIELDS, values))}


def _component(reader, source_file_name, contract, bindings):
    raw = reader.get_raw_data()
    decoded = decode_payload(raw)
    source, target = bindings
    _require(decoded["material"] in source[1], "unmapped source material")
    material = source[1][decoded["material"]]
    _require(material in target[0], "missing canonical target material")
    index = decoded["preset"]
    rows = contract["source_presets"]["presets"]
    _require(index == -900 or 0 <= index < len(rows), "unmapped source preset index")
    preset = rows[index] if index != -900 else None
    _require(preset is None or preset["material_id"] == decoded["material"] or
             (preset["name"], material) in REVIEWED_PRESET_MATERIAL_PAIRS, "preset/material disagreement")
    _require((decoded["hit"] == 0 and decoded["net"] == 0) or
             (decoded["hit"] == 2 and material == "Glass" and decoded["net"] > 0),
             "unreviewed hit role/network identity")
    _require(decoded["surface"] == 0 or (decoded["surface"] == 12 and material == "WoodThick"),
             "unreviewed surface sound encoding")
    return {"type": "BallisticCollider", "schema_version": 1, "enabled": decoded["enabled"],
            "material": material, "target_material_id": target[0][material]["material_id"],
            "preset_name": preset["name"] if preset else None,
            "target_preset_id": target[0][preset["name"]]["index"] if preset else -900,
            "values": decoded["values"],
            "source": {"file": source_file_name, "component_id": str(reader.path_id),
                       "object_id": str(decoded["owner_id"]), "raw_sha256": _sha(raw),
                       "preset_id": index, "material_id": decoded["material"],
                       "surface_sound_id": decoded["surface"], "hit_type_id": decoded["hit"],
                       "net_id": decoded["net"]}}


def augment_scene(scene_json, source_file, contract):
    """Append exact native material components atomically; identical reruns add zero.

Caller explicitly opts a scene into this feature after the normal extractor has
created its hierarchy/physics. Unrelated objects and their original state are untouched.
"""
    bindings = _bindings(contract)
    objects = scene_json["objects"]
    by_id = {o["id"]: o for o in objects}
    _require(len(by_id) == len(objects), "duplicate scene object IDs")
    pending, source_owners = [], set()
    basename = Path(source_file.name).name
    for reader in source_file.objects.values():
        if reader.type.name != "MonoBehaviour" or script_name(reader) != SCRIPT_NAME:
            continue
        component = _component(reader, basename, contract, bindings)
        owner_id = component["source"]["object_id"]
        _require(owner_id in by_id and owner_id not in source_owners, "missing or duplicate ballistic owner")
        source_owners.add(owner_id)
        owner = by_id[owner_id]
        original = source_file.objects[int(owner_id)].read_typetree()
        _require(type(owner.get("active")) is bool and owner["active"] == bool(original["m_IsActive"]),
                 "source owner activation changed")
        linked = [p["component"]["m_PathID"] for p in original["m_Component"]]
        _require(reader.path_id in linked, "ballistic component is not linked by its owner")
        physics = []
        for component_id in linked:
            source_component = source_file.objects[component_id]
            if source_component.type.name in PHYSICS_TYPES:
                tree = source_component.read_typetree()
                physics.append((source_component.type.name, bool(tree.get("m_Enabled", 1)), bool(tree.get("m_IsTrigger", 0))))
        imported = [(c["type"], c.get("enabled", True), c.get("is_trigger", False))
                    for c in owner["components"] if c["type"] in PHYSICS_TYPES]
        _require(all(type(enabled) is bool and type(trigger) is bool for _, enabled, trigger in imported) and
                 physics and Counter(physics) == Counter(imported), "source/imported owner physics or state differ")
        existing = [c for c in owner["components"] if c["type"] == "BallisticCollider"]
        _require(not existing or existing == [component], "conflicting existing ballistic component")
        if not existing:
            pending.append((owner, component))
    for owner in objects:
        _require(owner["id"] in source_owners or not any(c["type"] == "BallisticCollider" for c in owner["components"]),
                 "ballistic component has no source counterpart")
    for owner, component in pending:
        owner["components"].append(component)
    return len(pending)
