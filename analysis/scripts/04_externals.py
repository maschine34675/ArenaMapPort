from local_output import output_path
import UnityPy
import json

BASE = "X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"

EQUATOR = {51:'main',52:'TeamFight_AI',53:'DesignStuff',54:'Scripts',55:'Lighting',56:'Sound',57:'TeamFight_DesignStuff',58:'TeamFight',59:'TeamFight_Culling'}
AUTOSERVICE = {62:'main',63:'AI',65:'DesignStuff',69:'Scripts',243:'Scripts_Winter',112:'Sound',136:'TeamFight_DesignStuff',66:'Light',135:'Culling'}

def get_externals(fname):
    try:
        env = UnityPy.load(f"{BASE}/{fname}")
    except Exception as e:
        return {"error": str(e)}
    out = []
    for f in env.files.values():
        if hasattr(f, "externals"):
            for ext in f.externals:
                out.append(str(ext.name) if hasattr(ext, 'name') else str(ext))
    return out

results = {"Equator": {}, "AutoService": {}}
for idx, label in EQUATOR.items():
    results["Equator"][f"{idx}_{label}"] = {
        "level_externals": get_externals(f"level{idx}"),
    }
for idx, label in AUTOSERVICE.items():
    results["AutoService"][f"{idx}_{label}"] = {
        "level_externals": get_externals(f"level{idx}"),
    }

with open(output_path("scene_externals.json"), "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2, ensure_ascii=False)
print("done")
