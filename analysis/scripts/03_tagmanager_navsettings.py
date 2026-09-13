import UnityPy
import json

def dump(path, out_path, type_names):
    env = UnityPy.load(path)
    results = {}
    for obj in env.objects:
        if obj.type.name in type_names:
            try:
                tree = obj.read_typetree()
            except Exception as e:
                tree = {"error": str(e)}
            results[obj.type.name] = tree
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, default=str, ensure_ascii=False)
    print(f"Wrote {out_path}")

if __name__ == "__main__":
    dump("X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/globalgamemanagers",
         "D:/ArenaMapPort/analysis/arena_gm.json",
         {"TagManager", "NavMeshProjectSettings", "BuildSettings", "PlayerSettings", "QualitySettings", "GraphicsSettings"})
    dump("D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers",
         "D:/ArenaMapPort/analysis/spt_gm.json",
         {"TagManager", "NavMeshProjectSettings"})
