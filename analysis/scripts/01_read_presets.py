import UnityPy
import json
import sys

def dump_bundle(path, out_path):
    env = UnityPy.load(path)
    results = []
    for obj in env.objects:
        entry = {"path_id": obj.path_id, "type": str(obj.type), "byte_size": obj.byte_size}
        try:
            if obj.type.name == "MonoBehaviour":
                tree = obj.read_typetree()
                entry["typetree"] = tree
            else:
                data = obj.read()
                entry["name"] = getattr(data, "m_Name", None)
        except Exception as e:
            entry["error"] = str(e)
        results.append(entry)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, default=str, ensure_ascii=False)
    print(f"Wrote {out_path} ({len(results)} objects)")

if __name__ == "__main__":
    base = "X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/StreamingAssets/Windows/maps"
    targets = [
        ("arena_equator_tdm_02_preset.bundle", "D:/ArenaMapPort/analysis/equator_tdm_preset.json"),
        ("arena_autoservice_preset.bundle", "D:/ArenaMapPort/analysis/autoservice_preset.json"),
    ]
    for fname, out in targets:
        dump_bundle(f"{base}/{fname}", out)
